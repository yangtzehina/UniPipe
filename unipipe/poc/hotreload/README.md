# Hot reload PoC — Stage A: does detour work here?

The plan's strategy rests on replacing Unity's `[HotReload]` weaver with whole-method detour, which
would clear the two limits the weaver has: it only weaves **instance methods returning `void`**, and
the replacement body may touch **public members only**. Everything downstream — retiring the
upgrade tax, dropping the UPDL dependency, shrinking the compliance surface — depends on that
working on the Mono that ships with Unity 2022.3.

Stage A answers exactly that question and nothing more.

## Result: it works

```
PASS A0.baseline private+return          CallCompute(1)=43
PASS A0.baseline static+return           CallDescribe="v1:x"
PASS A0.baseline void instance           LastNote="v1:n"
PASS A1.detour private method w/ return  CallCompute(1)=42001
PASS A1b.same method, NoInlining         CallComputeNoInline(1)=42001
PASS A2.detour private static method     CallDescribe="v2:x"
PASS A3.detour void instance method      LastNote="v2:n"
PASS A4.instance state preserved         PublicSeed=100
PASS A5.unpatch restores original        CallCompute(1)=43

HOTRELOAD POC STAGE A: PASS pass=9 fail=0
Unity 2022.3.62f3, macOS arm64 (native), Mono
```

`42001` is the load-bearing number. It means the replacement body ran *and* read `_secret`, a
private field of the target — both limits cleared in one assertion.

A4 and A5 matter for the surrounding workflow: the object is not rebuilt when its code is replaced,
and unpatching restores the original body, which is what makes rollback and cleanup possible.

## The one real constraint: JIT inlining

A1 and A1b are the same method body, differing only in `[MethodImpl(MethodImplOptions.NoInlining)]`.
With default settings **A1 fails and A1b passes** — Mono inlines the tiny method into its caller, so
detouring the method has no effect at the call site that was already inlined.

Two mitigations, both verified:

- **`[MethodImpl(MethodImplOptions.NoInlining)]`** on the target — precise, but requires annotating
  ahead of time, which is the same "decide your entry points early" tax the weaver imposes.
- **`MONO_INLINELIMIT=0`** in the editor's environment — this is what turns the run above green,
  including A1. Blanket, no annotation needed, and it applies to code that was never marked for
  reloading. Cost is unmeasured editor-side JIT performance.

The blanket option is the more interesting one for a hot-reload workflow, since it removes the need
to predict which methods you will want to edit. Its performance cost should be measured before it
becomes a recommendation.

## Getting the library right matters

Three attempts, and the failure modes are worth recording because they are not obvious:

| Build | Result |
|---|---|
| Lib.Harmony 2.3.3 (NuGet, `net472`) | `NotImplementedException` inside `PatchFunctions.UpdateWrapper` — the detour backend refuses this platform |
| Lib.Harmony 2.2.2 (NuGet, `net472`) | `TypeInitializationException` on `HarmonySharedState` — that package is not self-contained and needs `MonoMod.Common` alongside |
| **Harmony 2.4.2.0 "Fat" (GitHub release, `net472`)** | **Works** |

The "Fat" build bundles its MonoMod dependencies; the NuGet `lib/` assemblies do not, and the
resulting errors point at Harmony rather than at the missing pieces. Note also that
`lib/netstandard2.0/` in these packages is an empty placeholder — `net472` is the target to take
for Unity 2022.

## Reproducing

Create an empty Unity 2022.3 project, then:

1. Download `Harmony-Fat.<version>.zip` from
   [pardeike/Harmony releases](https://github.com/pardeike/Harmony/releases) and copy
   `net472/0Harmony.dll` to `Assets/Plugins/Harmony/`. Harmony is MIT — keep its LICENSE alongside.
2. Copy `HotReloadPoC.cs` to `Assets/Editor/`.
3. Run:

```bash
MONO_INLINELIMIT=0 /path/to/Unity -batchmode -quit \
  -projectPath . -executeMethod HotReloadPoC.Run -logFile run.log
```

Drop the environment variable to observe the inlining effect: A1 fails, A1b passes.

## What is not yet established

Stage A patches with **compiled** replacement bodies living in the same assembly, and reaches the
target's private field by reflection (`AccessTools.Field`). That is enough to prove the detour
mechanism, but it is not the full picture.

**Stage B** is the `skip_visibility` half: compiling a replacement body from *source* at runtime,
where the source refers to private members directly, and flipping the bit in Mono's method
structure so the JIT skips visibility checks. That removes the reflection and lets a developer's
edit compile as written. Until Stage B runs, "edit any method body and reload it" is proven for the
detour half only.
