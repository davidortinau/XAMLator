﻿using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mono.CSharp;

namespace XAMLator.Server
{
    /// <summary>
    /// Evaluates expressions using the mono C# REPL.
    /// This method is thread safe so you can call it from anywhere.
    /// </summary>
    public partial class VM
    {
        readonly object mutex = new object();
        readonly Printer printer = new Printer();
        Evaluator eval;

        public EvalResult Eval(EvalRequest code, TaskScheduler mainScheduler, CancellationToken token)
        {
            var r = new EvalResult();
            Task.Factory.StartNew(() =>
            {
                r = EvalOnMainThread(code, token);
            }, token, TaskCreationOptions.None, mainScheduler).Wait();
            return r;
        }

        EvalResult EvalOnMainThread(EvalRequest code, CancellationToken token)
        {
            var sw = new System.Diagnostics.Stopwatch();

            object result = null;
            bool hasResult = false;

            lock (mutex)
            {
                InitIfNeeded();

                Log.Debug($"EVAL ON THREAD {Thread.CurrentThread.ManagedThreadId}");

                printer.Messages.Clear();

                sw.Start();

                if (code.Xaml != null)
                {
                    result = LoadXAML(code.Xaml, code.XamlType);
                    hasResult = result != null;
                }
                else
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(code.Declarations))
                        {
                            eval.Evaluate(code.Declarations, out result, out hasResult);
                        }
                        if (!string.IsNullOrEmpty(code.ValueExpression))
                        {
                            eval.Evaluate(code.ValueExpression, out result, out hasResult);
                        }
                    }
                    catch (InternalErrorException)
                    {
                        eval = null; // Force re-init
                    }
                    catch (Exception ex)
                    {
                        // Sometimes Mono.CSharp fails when constructing failure messages
                        if (ex.StackTrace.Contains("Mono.CSharp.InternalErrorException"))
                        {
                            eval = null; // Force re-init
                        }
                        printer.AddError(ex);
                    }
                }

                sw.Stop();

                Log.Debug($"END EVAL ON THREAD {Thread.CurrentThread.ManagedThreadId}");
            }

            return new EvalResult
            {
                Messages = printer.Messages.ToArray(),
                Duration = sw.Elapsed,
                Result = result,
                HasResult = hasResult,
            };
        }

        void InitIfNeeded()
        {
            if (eval == null)
            {

                Log.Debug("INIT EVAL");

                var settings = new CompilerSettings();
                settings.AddConditionalSymbol("__XAMLator__");
                settings.AddConditionalSymbol("DEBUG");
                PlatformSettings(settings);
                var context = new CompilerContext(settings, printer);
                eval = new Evaluator(context);

                //
                // Add References to get UIKit, etc. Also add a hook to catch dynamically loaded assemblies.
                //
                AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
                {
                    Log.Debug($"DYNAMIC REF {e.LoadedAssembly}");
                    AddReference(e.LoadedAssembly);
                };
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Log.Debug($"STATIC REF {a}");
                    AddReference(a);
                }

                //
                // Add default namespaces
                //
                object res;
                bool hasRes;
                eval.Evaluate("using System;", out res, out hasRes);
                eval.Evaluate("using System.Collections.Generic;", out res, out hasRes);
                eval.Evaluate("using System.Linq;", out res, out hasRes);
                PlatformInit();
            }
        }

        partial void PlatformSettings(CompilerSettings settings);

        partial void PlatformInit();

        object LoadXAML(string xaml, string xamlType)
        {
            Log.Information($"Loading XAML for type  {xamlType}");
            object view = null;
            try
            {
                view = eval.Evaluate($"new {xamlType} ()");
            }
            catch (Exception ex)
            {
                Log.Error($"Error create new instance of {xamlType}");
                printer.AddError(ex);
                return null;
            }
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                var xamlAssembly = Assembly.Load(new AssemblyName("Xamarin.Forms.Xaml"));
                var xamlLoader = xamlAssembly.GetType("Xamarin.Forms.Xaml.XamlLoader");
                var load = xamlLoader.GetRuntimeMethod("Load", new[] { typeof(object), typeof(string) });
                load.Invoke(null, new object[] { view, xaml });
                return view;
            }
            catch (TargetInvocationException ex)
            {
                Log.Error($"Error loading XAML");
                printer.AddError(ex);
            }
            Log.Information($"XAML loaded correctly for view {view}");
            return null;
        }

        void AddReference(Assembly a)
        {
            //
            // Avoid duplicates of what comes prereferenced with Mono.CSharp.Evaluator
            // or ones that cause problems.
            //
            var name = a.GetName().Name;
            if (name == "mscorlib" || name == "System" || name == "System.Core" ||
                name == "Xamarin.Interactive" || name == "Xamarin.Interactive.iOS")
                return;

            //
            // TODO: Should this lock if called from the AssemblyLoad event?
            //
            eval.ReferenceAssembly(a);
        }
    }
}

