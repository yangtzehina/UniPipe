# Running Unity's `com.unity.pipeline` on Unity 2022.3

Unity's [Unity CLI](https://unity.com/blog/meet-the-unity-cli) drives a running Editor through
the `com.unity.pipeline` package. The package declares `"unity": "6000.0"`, so UPM refuses to
install it on Unity 2022.3 — which is where a lot of shipping projects still are.

It does run there. This document records every obstacle we hit getting
`com.unity.pipeline@0.5.0-exp.1` working on **Unity 2022.3.62f3 (macOS, Apple silicon)**, driven
by the official `unity` CLI `1.0.0-beta.6`.

**This is a description of changes, not a distribution of them.** The package is Unity's, under the
[Unity Package Distribution License](https://unity3d.com/legal/licenses/Unity_Package_Distribution_License);
no Unity source, patch hunk, or binary is reproduced here. Prior art:
[rocwood/unity-cli-2022-mod](https://github.com/rocwood/unity-cli-2022-mod) documents the first
three items below — items 4 through 9 are, as far as we can tell, undocumented elsewhere.

Nothing here is endorsed by or affiliated with Unity Technologies. Modifying an experimental
package to bypass its declared version floor is your call to make and your risk to carry; see
*Before you start* for the licensing and Terms-of-Service considerations.

---

## Before you start

Two separate constraints, often conflated:

**Copyright.** The package is under the UPDL. You may work with it inside your own project; you
may not redistribute it, patched or otherwise. That is why this file describes edits instead of
shipping them, and why you must extract the package yourself from Unity's registry.

**Terms of Service.** Unity's ToS was updated 2026-06-30. Section 17.2 gained clause **(ff)**,
restricting access to Unity offerings by "AI agents, LLMs, command-line interfaces, MCP clients or
servers […] or other automated or non-human callers" outside of *Authorized Agentic Access*, and
clause **(gg)**, barring unauthorized third-party integrations. Unity staff have stated informally
(on Reddit, not in the ToS text) that the restriction targets connections to Unity's *cloud*
platform, servers, Asset Store and public APIs, and that local AI-assisted development is fine.
That distinction is not written into the ToS. If your usage stays strictly local — no login, no
cloud commands — you are on the side Unity has verbally endorsed, but the gray area is real.

## Getting the package

Fetch the tarball from Unity's package registry (`packages.unity.com/com.unity.pipeline` lists the
published versions and their tarball URLs), extract it, and place it in your project's `Packages/`
directory as an embedded package. Embedding is what lets you edit it; UPM will not install it from
the registry while the version floor stands.

Unity's own `unity pipeline install` will not help here — it targets 6000.0+.

---

## The nine obstacles

### 1. The version floor

`package.json` declares `"unity": "6000.0"`. Change it to `"2022.3"`. This is the gate that makes
everything else reachable; on its own it changes nothing about whether the code compiles.

### 2. The bundled Roslyn assemblies are invisible to 2022 — 117 compile errors

**Symptom.** The runtime assembly fails to compile with over a hundred `CS0246` errors, all of them
unable to find `Microsoft.CodeAnalysis.*` types.

**Cause.** The package bundles Roslyn and a few BCL assemblies under `Runtime/Plugins/CodeAnalysis/`.
Their `.meta` files were written by Unity 6 and carry a `PluginImporter` block at
`serializedVersion: 3`. Unity 2022 cannot parse that revision, so it never registers the DLLs as
plugins and never passes them to the compiler. There is no error about the `.meta` itself — the
failure surfaces only as missing types.

**Fix.** Reduce each affected `.meta` to its two essential lines — the `fileFormatVersion` and the
`guid` — and delete the rest. Unity 2022 regenerates the import settings at its own revision on
next import. Keep the original GUIDs. The assembly definition references these DLLs by filename,
not GUID, so nothing else needs updating.

Four `.meta` files need this. A fifth DLL in the same folder already ships with a minimal `.meta`
and is a useful reference for what "minimal" looks like.

### 3. Two Unity 6-only APIs

After item 2, roughly fifteen errors remain across two files:

- **`PhysicsMaterial`** — Unity 6 renamed `PhysicMaterial` to `PhysicsMaterial`. The asset commands
  use the new spelling in several places. Rather than editing each site, add a file-scoped `using`
  alias mapping the new name to the old type, guarded so it only applies below Unity 6. The
  property names used on it are identical in both versions, so the body needs no changes.
- **`Material.rawRenderQueue`** — a Unity 6 addition, used once in the material commands to report
  the un-resolved render queue. Under 2022, read the serialized `m_CustomRenderQueue` property
  instead: it is the same underlying value, with `-1` meaning "inherit from shader", which
  preserves the round-trip contract the surrounding code documents.

Guard both with a version check rather than replacing the Unity 6 path outright — you want the
package to still be correct if the project later moves to Unity 6.

### 4. The HTTP listener binds a prefix 2022's Mono rejects

**Symptom.** Compilation is clean, the Editor starts, and the log says
`Failed to start Pipeline Server: No available ports in range 7800-7849` — while the ports are
demonstrably free.

**Cause.** The server binds a wildcard-host prefix of the form `http://+:<port>/`. The comment in
the source explains why a wildcard is needed at all: Mono's `HttpListener` binds a *hostname*
prefix to one resolved address family and then matches the request's `Host` header literally, so a
prefix of `127.0.0.1` rejects `Host: localhost` and vice versa. The problem is that the Mono
shipped with Unity 2022.3 on macOS throws `SocketException: The requested address is not valid in
this context` for the `+` wildcard specifically. The port-probing loop treats any exception as
"port busy", so all fifty candidate ports appear unavailable and the real cause never surfaces.

**Fix.** Use the `*` wildcard instead. In Mono both wildcards match any `Host` header; only `+`
trips the exception. We verified the four candidates directly on 2022.3.62f3: `+` throws, while
`*`, `127.0.0.1` and `localhost` all bind. Guard the change so Unity 6 keeps the original prefix.

This is the obstacle most likely to waste an afternoon, because the error message points at the
wrong thing entirely.

### 5. The Input System guard trusts the wrong signal

**Symptom.** `CS0246: The type or namespace name 'ButtonControl' could not be found` in the runtime
input command — and because this lands during startup compilation, the Editor drops into Safe Mode,
where it is unreachable by any CLI.

**Cause.** The input command is guarded by `ENABLE_INPUT_SYSTEM`. That symbol is defined by the
project's **Active Input Handling** setting, not by whether `com.unity.inputsystem` is actually
installed. A project set to *Both* (or *Input System*) without the package installed defines the
symbol while the types are absent. The assembly definition names `Unity.InputSystem` as a
reference, but an unresolvable assembly reference is silently skipped rather than failing loudly.

**Fix.** Add a `versionDefines` entry keyed on the `com.unity.inputsystem` package that emits your
own symbol, and require both that symbol and `ENABLE_INPUT_SYSTEM` at each of the four guard sites.
Then the code compiles only when the types genuinely exist.

Worth knowing before you hit this: a Safe Mode Editor is invisible to CLI tooling, and some CLIs
respond to "no server" by launching another Editor instance.

### 6. In-place hot reload needs a registrar that nothing sets up for you

**Symptom.** `reload_file` returns success — with `0 methods` reloaded. Your edited method keeps
running the old body.

**Cause.** This is by design, not a porting artifact. The in-place workflow weaves a dispatch
prologue into every `[HotReload]` method at compile time, but dispatch only happens for methods
that were *registered* at runtime. Registration is performed by a scan that the package's runtime
manager component runs in `Awake`. The shipped sample scenes carry that component; your scene
almost certainly does not.

**Fix.** Put the runtime manager component in the scene you are iterating in. If you only need it
occasionally, invoking the discovery scan once per Play session by reflection is enough to populate
the registry.

Once registered, this works well: we measured 279–431 ms end-to-end for a method-body swap, with
the frame counter running continuously across it — no domain reload, no Play Mode restart.

### 7. Two constraints on what hot reload can actually change

Not porting problems, but they decide whether the feature is useful to you:

- Only **instance methods returning `void`** get the woven prologue. Static methods and any method
  with a return value are silently skipped.
- The replacement body may touch **public members only**. It is compiled into a separate assembly,
  so private and internal members of the original type are out of reach.

Together these mean you can hot-swap logic expressed through a type's public surface, but not the
private hot paths inside a library. Plan your `[HotReload]` entry points accordingly.

### 8. The package puts a live hook into your player builds

**Symptom.** None at development time — which is the problem.

**Cause.** The console-capture type registers a `[RuntimeInitializeOnLoadMethod]` with no platform
or development-build guard around it. Methods carrying that attribute are linker roots, so managed
stripping preserves it. We confirmed by inspecting a real WebGL/IL2CPP release build: the assembly
list contains the pipeline runtime assembly, and the runtime-initialize manifest contains that
bootstrap entry. It runs at startup in a shipping build, subscribes to Unity's log callback, and
buffers every log line into a 2000-entry ring for a CLI client that can never connect.

The rest is stripped cleanly — the eval and compilation services sit behind a guard that is false
on WebGL, so no Roslyn code survives into the build. This one hook is the exception.

**Fix.** Guard that bootstrap method for editor and development builds only. A `link.xml` will not
help; the attribute makes it a root regardless.

Verify by searching the built `.data`/`.wasm` for the type name — with the guard in place, the
runtime-initialize manifest entry disappears, leaving only an inert assembly-name string.

### 9. The bundled Roslyn assemblies leak into every assembly's compile inputs

**Symptom.** None yet — this is a latent conflict.

**Cause.** After item 2, those DLLs are imported with default settings: **Any Platform**, **Auto
Referenced**. Unity therefore passes them to every assembly that does not set `overrideReferences`.
We confirmed this against the compiler response files: unrelated assemblies in the project — ones
that never reference the pipeline package at all — receive Roslyn, Mono.Cecil and Newtonsoft in
their reference lists.

With one copy present nothing breaks. Introduce a second copy of any of those assemblies — pulling
a NuGet package that carries Newtonsoft or Roslyn is the realistic path — and every assembly that
can see both fails with `CS0433` ambiguous-type errors.

**Fix.** Turn off Auto Referenced on those DLLs. The package's own assembly definition already sets
`overrideReferences` and lists them explicitly, so it is unaffected. For comparison, Unity's own
Newtonsoft package avoids the problem differently, shipping two copies with mutually exclusive
platform sets — also worth studying if you need a per-platform split.

---

## What you get, and what you do not

Working on 2022.3 after the above: **143 of the package's 153 commands**, including `eval`
(Roslyn compiles and executes in the 2022 Mono runtime — we measured ~200 ms warm), scene and
GameObject authoring, prefabs, materials, screenshots, console, tests, builds, and in-place hot
reload. No login is required for any of it; authentication gates only the cloud-facing commands.

Absent: the UI-element capture commands, which are guarded for Unity 6000.7+ and simply do not
exist on 2022. The player-side channel is also narrower than the docs suggest — `eval` and hot
reload in a player are compiled only for development builds on the Mono backend, so IL2CPP targets
(WebGL, mobile, console) get nothing there regardless of Unity version.

One behavioral wrinkle worth knowing: the Game View screenshot command reports success in Edit Mode
but writes an essentially empty frame. Capture from Play Mode, or use another tool for Edit Mode
captures.

## The upgrade tax

Budget for this before committing. Comparing `0.4.0-exp.1` against `0.5.0-exp.1` — nineteen days
apart — 109 files changed and 24 were added. **Four of the five files we patch were themselves
modified upstream**, one of them by 861 lines. Releases have been landing every one to three weeks.

So this is a standing cost, not a one-time conversion, and it concentrates in exactly the files you
have edited. Two things soften it: nearly all of the tax is specific to running on 2022 (on Unity 6
none of items 1, 3 or 4 apply), and pinning a version you have working is perfectly viable — the
newer releases' additions may well be irrelevant to your workflow.

---

*Verified on Unity 2022.3.62f3 (macOS arm64), `com.unity.pipeline@0.5.0-exp.1`, Unity CLI
`1.0.0-beta.6`, 2026-08.*
