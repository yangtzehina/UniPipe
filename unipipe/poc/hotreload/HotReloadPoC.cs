using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;

// Stage A of the hot-reload proof of concept.
//
// The plan's strategy rests on one unverified assumption: that Harmony's whole-method
// detour works on the Mono shipped with Unity 2022.3, for the exact method shapes the
// official [HotReload] weaver refuses — private methods, methods with a return value,
// and static methods. If detour cannot reach those here, self-hosted hot reload does not
// clear the limits it is supposed to clear, and the strategy needs rethinking.
//
// Run headless:
//   Unity -batchmode -quit -projectPath . -executeMethod HotReloadPoC.Run -logFile <path>
public static class HotReloadPoC
{
    // ---- targets -----------------------------------------------------------------
    // Deliberately shaped like the cases the weaver skips.

    public class Target
    {
        private int _secret = 42;              // private state the patch must reach
        public int PublicSeed = 100;

        // private + returns a value: the weaver handles neither
        private int ComputeSecret(int x) => _secret + x;

        // same shape, but opted out of inlining — isolates "detour cannot do this"
        // from "the JIT inlined the call site before we ever patched it"
        [MethodImpl(MethodImplOptions.NoInlining)]
        private int ComputeSecretNoInline(int x) => _secret + x;

        // static + returns a value: the weaver skips static entirely
        private static string Describe(string tag) => "v1:" + tag;

        // void instance method: the one shape the weaver does handle, as a control
        public string LastNote;
        private void Note(string s) => LastNote = "v1:" + s;

        // public entry points so the harness can drive the private methods
        public int CallCompute(int x) => ComputeSecret(x);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int CallComputeNoInline(int x) => ComputeSecretNoInline(x);
        public static string CallDescribe(string tag) => Describe(tag);
        public void CallNote(string s) => Note(s);
    }

    // ---- replacement bodies ------------------------------------------------------
    // A Harmony prefix returning false replaces the original outright. __instance gives
    // the receiver; __result is the return slot. Note these patch bodies are ordinary
    // compiled code in this assembly — reaching Target's private field from here needs
    // reflection. Stage B is about removing that need.

    static readonly FieldInfo s_SecretField =
        AccessTools.Field(typeof(Target), "_secret");

    public static bool ComputePatch(Target __instance, int x, ref int __result)
    {
        var secret = (int)s_SecretField.GetValue(__instance);   // private field, read
        __result = secret * 1000 + x;                            // v2 behaviour
        return false;                                            // skip the original
    }

    public static bool DescribePatch(string tag, ref string __result)
    {
        __result = "v2:" + tag;
        return false;
    }

    public static bool NotePatch(Target __instance, string s)
    {
        __instance.LastNote = "v2:" + s;
        return false;
    }

    // ---- probe -------------------------------------------------------------------

    static readonly StringBuilder s_Log = new StringBuilder();
    static int s_Pass, s_Fail;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { s_Pass++; s_Log.AppendLine($"PASS {name} — {detail}"); }
        else { s_Fail++; s_Log.AppendLine($"FAIL {name} — {detail}"); }
    }

    public static void Run()
    {
        var harmony = new Harmony("unipipe.hotreload.poc");
        var target = new Target();

        try
        {
            // Baseline: original bodies.
            Check("A0.baseline private+return", target.CallCompute(1) == 43, $"CallCompute(1)={target.CallCompute(1)} (expect 43)");
            Check("A0.baseline static+return", Target.CallDescribe("x") == "v1:x", $"CallDescribe=\"{Target.CallDescribe("x")}\"");
            target.CallNote("n");
            Check("A0.baseline void instance", target.LastNote == "v1:n", $"LastNote=\"{target.LastNote}\"");

            // A1 — private instance method with a return value.
            harmony.Patch(
                AccessTools.Method(typeof(Target), "ComputeSecret"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HotReloadPoC), nameof(ComputePatch))));
            var got = target.CallCompute(1);
            Check("A1.detour private method with return value", got == 42001,
                  $"CallCompute(1)={got} (expect 42001 = private field 42 reached and body replaced)");

            // A1b — identical shape with inlining disabled. If A1 fails and this passes,
            // the limit is the JIT inlining the call site, not the detour mechanism.
            harmony.Patch(
                AccessTools.Method(typeof(Target), "ComputeSecretNoInline"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HotReloadPoC), nameof(ComputePatch))));
            var gotNI = target.CallComputeNoInline(1);
            Check("A1b.same method, NoInlining", gotNI == 42001,
                  $"CallComputeNoInline(1)={gotNI} (expect 42001)");

            // A2 — private static method with a return value.
            harmony.Patch(
                AccessTools.Method(typeof(Target), "Describe"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HotReloadPoC), nameof(DescribePatch))));
            var desc = Target.CallDescribe("x");
            Check("A2.detour private static method", desc == "v2:x", $"CallDescribe=\"{desc}\" (expect \"v2:x\")");

            // A3 — the control case the weaver already supports.
            harmony.Patch(
                AccessTools.Method(typeof(Target), "Note"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HotReloadPoC), nameof(NotePatch))));
            target.CallNote("n");
            Check("A3.detour void instance method", target.LastNote == "v2:n", $"LastNote=\"{target.LastNote}\"");

            // A4 — state survives patching: the object is not rebuilt, only its code.
            Check("A4.instance state preserved", target.PublicSeed == 100, $"PublicSeed={target.PublicSeed}");

            // A5 — unpatching restores the original body (needed for cleanup / rollback).
            harmony.UnpatchAll("unipipe.hotreload.poc");
            var restored = target.CallCompute(1);
            Check("A5.unpatch restores original", restored == 43, $"CallCompute(1)={restored} (expect 43)");
        }
        catch (Exception e)
        {
            Check("A!.exception", false, e.GetType().Name + ": " + e.Message);
            s_Log.AppendLine(e.StackTrace);
        }

        s_Log.AppendLine();
        s_Log.AppendLine($"HOTRELOAD POC STAGE A: {(s_Fail == 0 ? "PASS" : "FAIL")} pass={s_Pass} fail={s_Fail}");
        s_Log.AppendLine($"runtime={typeof(object).Assembly.ImageRuntimeVersion} mono={Type.GetType("Mono.Runtime") != null} unity={Application.unityVersion}");

        Debug.Log(s_Log.ToString());
        Console.WriteLine(s_Log.ToString());
    }
}
