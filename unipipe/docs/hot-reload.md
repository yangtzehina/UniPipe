# Hot reload

`HotReload.Apply` puts an edited method body into the running editor without a domain reload, so
the objects already alive keep their state and Play Mode keeps running.

```bash
unicli exec HotReload.Apply '{"path":"/abs/path/Assets/Foo.cs"}'
```

```
Swapped 1 method(s) in 1508ms.
  Foo.Report
```

Unity's own answer to editing code is to recompile the project and reload the domain, which throws
away every object in it. This replaces that loop for the case it can handle: change a method body,
run this, and the next call runs the new body against the same instances.

## How it works

The file is compiled on its own into a throwaway assembly, producing a second set of types with the
same names. Each recompiled method is matched to the loaded one it replaces, re-emitted as a
dynamic method, and the loaded method is detoured onto it.

Re-emitting is what makes editing a private method work: a body compiled into another assembly has
no right to touch the loaded type's private state, and MonoMod's `DynamicMethodDefinition` produces
a form the runtime lets skip visibility checks.

## What it will not do

Reported per type or per method rather than failing the call, because a partial application is
normal and you should be told exactly what did and did not take:

| Change | Why it is refused |
|---|---|
| Adding or removing a field | Every field after it shifts. A swapped body reaches instance state through the *new* type's field tokens against an *old* instance, so a changed layout reads the wrong memory. This is checked before anything is swapped. |
| Changing a field's type | Same reason. |
| Changing a signature or return type | The detour would hand the method arguments it does not expect. |
| Adding a method or a type | There is nothing loaded to detour; these need a real recompile. |
| Generic and abstract methods | Not attempted. |

Statics are a subtler limit worth knowing: the recompiled type has its *own* static fields, so a
swapped body reading a static reads the new type's copy, not the value the editor has been using.
Keep hot-reloaded bodies to instance state and parameters.

## Inlining

Mono inlines small methods, and a detour has no effect at a call site that was already inlined — the
swap reports success and the old body keeps running. Two ways out:

- Start the editor with **`MONO_INLINELIMIT=0`** — blanket, no annotation, and what the verification
  below used. Editor-side JIT cost is unmeasured.
- Mark the method **`[MethodImpl(MethodImplOptions.NoInlining)]`** — precise, but you have to decide
  in advance which methods you will want to edit.

The command warns when it swapped something in an editor that was not started with the environment
variable, because the symptom otherwise looks like it silently did nothing.

## Setup

The feature is optional and off unless both of these are true:

1. **Harmony** — copy `net472/0Harmony.dll` from a
   [Harmony release](https://github.com/pardeike/Harmony/releases) *Fat* zip into the project (e.g.
   `Assets/Plugins/Harmony/`). The NuGet `lib/` assemblies are **not** self-contained and fail in
   ways that point at the wrong culprit — see [`../poc/hotreload/`](../poc/hotreload/). Harmony is
   MIT; keep its LICENSE alongside.
2. **`UNIPIPE_HOTRELOAD`** in the project's scripting define symbols.

Without both, the module does not compile and the command is simply absent — nothing else changes.
Harmony is not committed to this repository; the dependency is yours to add.

## Verified

On Unity 2022.3.62f3, macOS arm64, with `MONO_INLINELIMIT=0`:

A live object was created and called three times, accumulating state. Its method body was edited on
disk — new text, and reading a private field — and applied. The next call returned the new result,
computed from that private field, with the call counter continuing from 3 to 4 and the same instance
still in place. No recompile, no domain reload; either would have reset both.

The matching and layout rules are unit tested separately (`MethodSwapperTests`), including that an
inserted, removed or retyped field is refused rather than swapped.
