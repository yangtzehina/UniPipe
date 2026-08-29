# Hot reload PoC


Two stages, answering the two questions the plan rests on: can a method body be replaced at
runtime here, and can the replacement reach private state without reflection. Both: yes.

## Stage A: does detour work here?

The plan's strategy rests on replacing Unity's `[HotReload]` weaver with whole-method detour, which
would clear the two limits the weaver has: it only weaves **instance methods returning `void`**, and
the replacement body may touch **public members only**. Everything downstream — retiring the
upgrade tax, dropping the UPDL dependency, shrinking the compliance surface — depends on that
working on the Mono that ships with Unity 2022.3.

Stage A answers exactly that question and nothing more.

### Result: it works

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

### The one real constraint: JIT inlining

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

### Getting the library right matters

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

### Reproducing

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

## Stage B: private access without reflection

Stage A's patch reached the target's private field through `AccessTools.Field` — reflection,
written by hand. A real workflow compiles the *developer's* edited source, and that source names
private members directly. Stage B asks what makes that legal.

The plan assumed Mono's `_MonoMethod.skip_visibility` bit: poking a native struct laid out by the
runtime. That works, but it is version-fragile and vanishes when Unity 6.8 moves the editor off
Mono. Before reaching for it, Stage B checked whether supported APIs already cover the ground.

**They do — the struct poking is not needed.**

### Result

```
PASS B0.baseline                                CallSecret(1)=71
PASS B1.control: refused without the flag       FieldAccessException as expected
PASS B2.DynamicMethod skipVisibility, field     _hidden=7
PASS B3.DynamicMethod skipVisibility, method    Secret(2)=72
PASS B4.detour + private access, no reflection  CallSecret(1)=7001
PASS B5.supported path available                no _MonoMethod bit poking needed
HOTRELOAD POC STAGE B: PASS pass=6 fail=0

PASS B6.control: compiler refuses private       CS1061 as expected
PASS B7.compiles with accessibility relaxed     MetadataImportOptions.All + BinderFlags.IgnoreAccessibility
PASS B8.control: loaded assembly still checked  FieldAccessException as expected
PASS B9.re-emit via DynamicMethodDefinition     Read(t)=7
HOTRELOAD POC STAGE B2: PASS pass=4 fail=0
```

`TargetLib` lives in its own assembly (`target/`), so "private" means something to both the
compiler and the runtime. B1, B6 and B8 are controls: each confirms a barrier is genuinely there
before the next test claims to clear it.

### Two barriers, two different answers

Private access is refused **twice**, at different times, and each needs its own key.

**The compiler** will not even see a private member of a referenced assembly, let alone let you
name one (B6: `CS1061`). Roslyn has the switch, because debuggers need it — an immediate window
inspects private state routinely. It takes two settings: `MetadataImportOptions.All` so the members
are imported at all (public API), and `BinderFlags.IgnoreAccessibility` so referencing them is not
an error (internal, reached by reflection). Together they compile the patch source as written (B7).

**The runtime** then refuses to run it anyway (B8: `FieldAccessException`) — compiling is not
permission. This is where the plan expected the Mono bit. But `DynamicMethod` takes a
`skipVisibility` flag for exactly this purpose, and it works here: reading a private field (B2),
calling a private method (B3), and as the body of a detoured method with no reflection at call
time (B4). Feeding the compiled body through MonoMod's `DynamicMethodDefinition` re-emits it as
such a method, and then it runs (B9).

### The chain, end to end

```
developer edits source naming private members
  → Roslyn, accessibility relaxed          (B7)
  → compiled assembly                       (will not run as loaded — B8)
  → re-emit via DynamicMethodDefinition     (B9)
  → detour the original to it               (B4, and Stage A)
  → runs, private state reachable
```

Every step is a public API or a mature MIT library. Nothing writes to runtime-internal structs.

### Why this matters beyond "it works"

The plan called for building private access as a replaceable layer — `skip_visibility` first, then
`InternalsVisibleTo` injection, then CoreCLR's Encode-and-Continue — on the assumption that the
first rung was Mono-only and would need replacing at Unity 6.8.

`DynamicMethod(skipVisibility: true)` is not Mono-specific; it is standard .NET and works on
CoreCLR too. The layer is still worth keeping as an abstraction, but the migration pressure that
motivated it is much weaker than assumed: the mechanism proven here should survive the runtime
change.

The one piece that stays fragile is `BinderFlags.IgnoreAccessibility` — internal Roslyn API,
reached reflectively, and free to move between versions. That is a compile-time dependency on one
enum member and one method name, which is a far smaller surface to guard than a native struct
layout, and it fails loudly (the reflection lookup returns null) rather than corrupting memory.

### Reproducing

Beyond Stage A's setup, the probes need Roslyn (`Microsoft.CodeAnalysis` and
`Microsoft.CodeAnalysis.CSharp`, plus `System.Collections.Immutable`,
`System.Reflection.Metadata` and `System.Runtime.CompilerServices.Unsafe`) in
`Assets/Plugins/Roslyn/`, and `target/` as its own assembly definition.

```bash
MONO_INLINELIMIT=0 /path/to/Unity -batchmode -quit -projectPath . \
  -executeMethod HotReloadPoCStageB.Run  -logFile b.log
MONO_INLINELIMIT=0 /path/to/Unity -batchmode -quit -projectPath . \
  -executeMethod HotReloadPoCStageB2.Run -logFile b2.log
```

### What is still open

Both stages patch a *single* method. A working implementation also has to decide what happens when
an edit changes a method's signature, adds a field, or touches a type that other loaded code has
already bound to — the cases SingularityGroup handles by recompiling affected callers. Those are
engineering questions with known shapes, not open research: the mechanism underneath them is now
established.
