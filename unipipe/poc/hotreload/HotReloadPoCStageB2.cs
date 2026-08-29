using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEngine;

// Stage B, second half: the compile-time barrier.
//
// B (first half) showed the *runtime* can be made to allow private access through a
// supported API — DynamicMethod's skipVisibility flag. But a hot-reload workflow starts
// from the developer's edited C# source, and the C# compiler refuses to compile
// `target._hidden` from outside the declaring assembly long before any runtime check.
//
// Roslyn has a switch for this, because debuggers need it: their expression evaluators
// let you inspect private state from an immediate window. It is not public API. This
// probe checks whether it is reachable, and — separately — whether an assembly compiled
// that way is allowed to *run* on this Mono, which is the question that decides how a
// real implementation has to be shaped.
//
// Run headless:
//   Unity -batchmode -quit -projectPath . -executeMethod HotReloadPoCStageB2.Run -logFile <path>
public static class HotReloadPoCStageB2
{
    static readonly StringBuilder s_Log = new StringBuilder();
    static int s_Pass, s_Fail;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { s_Pass++; s_Log.AppendLine($"PASS {name} — {detail}"); }
        else { s_Fail++; s_Log.AppendLine($"FAIL {name} — {detail}"); }
    }

    // A replacement body written the way a developer would write it: naming the private
    // field directly, with no reflection anywhere in sight.
    const string PatchSource = @"
public static class GeneratedPatch
{
    public static int Read(TargetLib t) => t._hidden;
}";

    static CSharpCompilation BuildCompilation(bool relaxAccessibility, out string how)
    {
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        how = "default options";

        if (relaxAccessibility)
        {
            // Two separate things are needed. MetadataImportOptions.All makes Roslyn even
            // *see* private members of referenced assemblies — by default it imports only
            // what is public. That part is public API.
            options = options.WithMetadataImportOptions(MetadataImportOptions.All);

            // IgnoreAccessibility then stops it complaining about referencing them. This is
            // internal, so it has to be reached reflectively: find the BinderFlags enum,
            // take its IgnoreAccessibility member, and call WithTopLevelBinderFlags.
            var binderFlagsType = typeof(CSharpCompilationOptions).Assembly
                .GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
            var setter = typeof(CSharpCompilationOptions).GetMethod(
                "WithTopLevelBinderFlags", BindingFlags.NonPublic | BindingFlags.Instance);

            if (binderFlagsType == null || setter == null)
            {
                how = "MetadataImportOptions.All only (BinderFlags/WithTopLevelBinderFlags not found)";
            }
            else
            {
                var ignoreAccessibility = Enum.Parse(binderFlagsType, "IgnoreAccessibility");
                options = (CSharpCompilationOptions)setter.Invoke(options, new[] { ignoreAccessibility });
                how = "MetadataImportOptions.All + BinderFlags.IgnoreAccessibility";
            }
        }

        return CSharpCompilation.Create(
            "GeneratedPatch_" + (relaxAccessibility ? "relaxed" : "strict") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6),
            new[] { CSharpSyntaxTree.ParseText(PatchSource) },
            refs,
            options);
    }

    static bool TryEmit(CSharpCompilation compilation, out byte[] image, out string firstError)
    {
        using (var ms = new System.IO.MemoryStream())
        {
            var result = compilation.Emit(ms);
            if (!result.Success)
            {
                image = null;
                firstError = result.Diagnostics
                    .First(d => d.Severity == DiagnosticSeverity.Error).ToString();
                return false;
            }
            image = ms.ToArray();
            firstError = null;
            return true;
        }
    }

    public static void Run()
    {
        var target = new TargetLib();

        // B6 — control: the barrier the compiler puts up. Without the switch this must fail,
        // otherwise the rest proves nothing.
        try
        {
            var strict = BuildCompilation(relaxAccessibility: false, out _);
            var ok = TryEmit(strict, out _, out var err);
            Check("B6.control: compiler refuses private access", !ok,
                  ok ? "it compiled, which should not happen" : err.Split('\n')[0]);
        }
        catch (Exception e)
        {
            Check("B6.control: compiler refuses private access", false, e.GetType().Name + ": " + e.Message);
        }

        // B7 — with the switch: source naming a private member should compile.
        byte[] image = null;
        try
        {
            var relaxed = BuildCompilation(relaxAccessibility: true, out var how);
            var ok = TryEmit(relaxed, out image, out var err);
            Check("B7.compiles with accessibility relaxed", ok,
                  ok ? $"{how}, {image.Length} bytes emitted" : $"{how} — {err.Split('\n')[0]}");
        }
        catch (Exception e)
        {
            Check("B7.compiles with accessibility relaxed", false, e.GetType().Name + ": " + e.Message);
        }

        // B8 — and can the result run? An assembly loaded normally still faces the runtime
        // visibility check; the answer here decides whether a real implementation can load
        // the compiled assembly directly, or has to re-emit the body some other way.
        if (image != null)
        {
            try
            {
                var asm = Assembly.Load(image);
                var m = asm.GetType("GeneratedPatch").GetMethod("Read");
                var v = (int)m.Invoke(null, new object[] { target });
                Check("B8.control: loaded assembly still faces the runtime check", false,
                      $"Read(t)={v} — it ran as loaded, so no re-emit would be needed");
            }
            catch (TargetInvocationException e) when (e.InnerException is FieldAccessException
                                                   || e.InnerException is MethodAccessException)
            {
                Check("B8.control: loaded assembly still faces the runtime check", true,
                      $"{e.InnerException.GetType().Name} as expected — compiling is not enough; " +
                      "the body has to be re-emitted (B9)");
            }
            catch (Exception e)
            {
                Check("B8.control: loaded assembly still faces the runtime check", false,
                      "unexpected " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message);
            }
        }

        // B9 — the closing link. B7 gave us a compiled body; B8 says the runtime will not
        // run it as loaded. Stage B showed DynamicMethod with skipVisibility does run such
        // code, so re-emit the compiled method through MonoMod's DynamicMethodDefinition
        // (bundled inside the Harmony build) and call that instead. If this works, the whole
        // chain closes: developer source naming private members -> compiled -> executed.
        if (image != null)
        {
            try
            {
                var dmdType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("MonoMod.Utils.DynamicMethodDefinition"))
                    .FirstOrDefault(t => t != null);

                if (dmdType == null)
                {
                    Check("B9.re-emit via DynamicMethodDefinition", false,
                          "MonoMod.Utils.DynamicMethodDefinition not found in the loaded assemblies");
                }
                else
                {
                    var asm = Assembly.Load(image);
                    var compiled = asm.GetType("GeneratedPatch").GetMethod("Read");

                    var dmd = Activator.CreateInstance(dmdType, new object[] { compiled });
                    var generate = dmdType.GetMethod("Generate", Type.EmptyTypes);
                    var reemitted = (MethodInfo)generate.Invoke(dmd, null);

                    var v = (int)reemitted.Invoke(null, new object[] { target });
                    Check("B9.re-emit via DynamicMethodDefinition", v == 7,
                          $"Read(t)={v} — compiled-from-source body reached the private field after re-emit");
                    (dmd as IDisposable)?.Dispose();
                }
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Check("B9.re-emit via DynamicMethodDefinition", false, inner.GetType().Name + ": " + inner.Message);
            }
        }

        s_Log.AppendLine();
        s_Log.AppendLine($"HOTRELOAD POC STAGE B2: {(s_Fail == 0 ? "PASS" : "FAIL")} pass={s_Pass} fail={s_Fail}");
        s_Log.AppendLine("chain: source naming privates -> Roslyn (accessibility relaxed) -> assembly -> DMD re-emit -> runs.");

        Debug.Log(s_Log.ToString());
        Console.WriteLine(s_Log.ToString());
    }
}
