using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;

// Stage B of the hot-reload proof of concept: reaching private members from a
// replacement body without reflection.
//
// Stage A proved detour works, but its patch reached the target's private field through
// AccessTools.Field — reflection, written by hand. A real hot-reload workflow compiles the
// developer's edited source, and that source names private members directly. Something has
// to make that legal.
//
// The plan assumed Mono's `_MonoMethod.skip_visibility` bit — poking a native struct, which
// is version-fragile and disappears when Unity 6.8 moves the editor off Mono. Before
// reaching for that, this stage checks whether a supported API already does the job:
// DynamicMethod takes a skipVisibility flag precisely for this.
//
// Run headless:
//   Unity -batchmode -quit -projectPath . -executeMethod HotReloadPoCStageB.Run -logFile <path>
public static class HotReloadPoCStageB
{
    static readonly StringBuilder s_Log = new StringBuilder();
    static int s_Pass, s_Fail;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { s_Pass++; s_Log.AppendLine($"PASS {name} — {detail}"); }
        else { s_Fail++; s_Log.AppendLine($"FAIL {name} — {detail}"); }
    }

    static readonly FieldInfo s_Hidden =
        typeof(TargetLib).GetField("_hidden", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly MethodInfo s_Secret =
        typeof(TargetLib).GetMethod("Secret", BindingFlags.NonPublic | BindingFlags.Instance);

    // Emits: (TargetLib t) => t._hidden — IL that is illegal from this assembly.
    // owner is a type in *this* assembly, so no accessibility is inherited from the target;
    // only the skipVisibility flag can make it run.
    static Func<TargetLib, int> EmitPrivateFieldReader(bool skipVisibility)
    {
        var dm = new DynamicMethod(
            "read_hidden_" + skipVisibility,
            typeof(int),
            new[] { typeof(TargetLib) },
            typeof(HotReloadPoCStageB),
            skipVisibility);
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, s_Hidden);
        il.Emit(OpCodes.Ret);
        return (Func<TargetLib, int>)dm.CreateDelegate(typeof(Func<TargetLib, int>));
    }

    // The replacement body used in B4, reaching the private field with no reflection at
    // call time — the delegate below was emitted once, and its IL touches _hidden directly.
    static Func<TargetLib, int> s_ReadHidden;

    public static bool SecretPatch(TargetLib __instance, int x, ref int __result)
    {
        __result = s_ReadHidden(__instance) * 1000 + x;   // v2: private field, no reflection
        return false;
    }

    public static void Run()
    {
        var t = new TargetLib();

        // B0 — the barrier is real. Reflection is the only legal route today, and the
        // baseline behaviour is what we expect to displace.
        Check("B0.baseline", t.CallSecret(1) == 71, $"CallSecret(1)={t.CallSecret(1)} (expect 71 = _hidden 7 * 10 + 1)");

        // B1 — control: the same IL without the flag must be rejected. If this "passes"
        // by running, then the runtime is not enforcing visibility and B2 proves nothing.
        try
        {
            var reader = EmitPrivateFieldReader(skipVisibility: false);
            var v = reader(t);
            Check("B1.control: private access refused without the flag", false,
                  $"expected a visibility exception, but it returned {v}");
        }
        catch (Exception e)
        {
            Check("B1.control: private access refused without the flag", true,
                  $"{e.GetType().Name} as expected");
        }

        // B2 — the supported API. DynamicMethod(skipVisibility: true) should let the same
        // IL read a private field of a type in another assembly.
        try
        {
            s_ReadHidden = EmitPrivateFieldReader(skipVisibility: true);
            var v = s_ReadHidden(t);
            Check("B2.DynamicMethod skipVisibility reads private field", v == 7, $"_hidden={v} (expect 7)");
        }
        catch (Exception e)
        {
            Check("B2.DynamicMethod skipVisibility reads private field", false, e.GetType().Name + ": " + e.Message);
        }

        // B3 — calling a private *method* the same way, not just reading a field.
        try
        {
            var dm = new DynamicMethod("call_secret", typeof(int),
                new[] { typeof(TargetLib), typeof(int) }, typeof(HotReloadPoCStageB), true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, s_Secret);
            il.Emit(OpCodes.Ret);
            var call = (Func<TargetLib, int, int>)dm.CreateDelegate(typeof(Func<TargetLib, int, int>));
            var v = call(t, 2);
            Check("B3.DynamicMethod skipVisibility calls private method", v == 72, $"Secret(2)={v} (expect 72)");
        }
        catch (Exception e)
        {
            Check("B3.DynamicMethod skipVisibility calls private method", false, e.GetType().Name + ": " + e.Message);
        }

        // B4 — the whole point: detour a private method, with a replacement body that
        // reaches private state through the emitted accessor rather than reflection.
        var harmony = new Harmony("unipipe.hotreload.poc.b");
        try
        {
            harmony.Patch(s_Secret,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HotReloadPoCStageB), nameof(SecretPatch))));
            var v = t.CallSecret(1);
            Check("B4.detour + private access, no reflection at call time", v == 7001,
                  $"CallSecret(1)={v} (expect 7001 = private _hidden 7 * 1000 + 1)");
            harmony.UnpatchAll("unipipe.hotreload.poc.b");
        }
        catch (Exception e)
        {
            Check("B4.detour + private access", false, e.GetType().Name + ": " + e.Message);
        }

        // B5 — is the Mono struct hack even needed? Record whether the fragile path the
        // plan assumed has a supported alternative on this runtime.
        var monoPresent = Type.GetType("Mono.Runtime") != null;
        Check("B5.supported path available", s_Fail == 0,
              monoPresent
                ? "DynamicMethod skipVisibility carried every case — no _MonoMethod bit poking needed"
                : "not Mono; result does not speak to the Mono path");

        s_Log.AppendLine();
        s_Log.AppendLine($"HOTRELOAD POC STAGE B: {(s_Fail == 0 ? "PASS" : "FAIL")} pass={s_Pass} fail={s_Fail}");
        s_Log.AppendLine($"mono={monoPresent} unity={Application.unityVersion}");

        Debug.Log(s_Log.ToString());
        Console.WriteLine(s_Log.ToString());
    }
}
