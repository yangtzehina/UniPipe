# UniPipe plan

Where this fork is going, what has been measured, and what is still assumption. Revised after
six rounds of hands-on verification (2026-08).

## The shape of the problem

Two tools cover Unity editor automation from different directions.

**UniCli** (MIT) speaks over a named pipe: 142 editor commands, handler auto-discovery, and the
Profiler / memory-snapshot / recorder domains that no other tool we surveyed has. Forkable,
extendable, redistributable.

**`com.unity.pipeline`** (Unity, UPDL) speaks local HTTP with bearer auth: 153 commands, plus an
official MCP story, baking, project management — and in-place C# hot reload. Usable inside your
own project, not redistributable.

Three ways to combine them, and only one survives scrutiny:

| | Verdict |
|---|---|
| **Bridge facade** — one front end, both command sets | Works today (`unipipe/bridge/`), but it is glue, not a library |
| **Absorb designs into an MIT fork** | The route. Only form that can be open-sourced, distributed, or built on |
| **Merge the packages** | Blocked. The UPDL forbids redistributing Unity's package; ToS §17.2(gg) adds a second layer |

## What changed the priorities

Two measurements reordered the roadmap.

**We depend on the Unity package for exactly one command.** Going through everything we actually
used across six rounds of work: `editor_status`, `recompile`, `eval`, `get_console_logs`, `build`,
`create_scene`, `create_gameobject` — UniCli has an equivalent for all of them, and for screenshots
UniCli's implementation is better (pipeline's Edit Mode capture reports success and writes an empty
frame). The single exception is `reload_file`. Hot reload is the whole dependency.

**Keeping the package current is a standing tax.** Between `0.4.0-exp.1` and `0.5.0-exp.1` —
nineteen days — 109 files changed, 24 were added, and **four of the five files we patch were
modified upstream**, one by 861 lines. Releases land every one to three weeks.

Together: we carry eight patches, a recurring merge burden, a UPDL constraint and a ToS gray area,
and what we get for it is one command.

That makes **self-hosted hot reload the strategic exit**, not merely a differentiating feature.
Implementing it retires the upgrade tax, the redistribution constraint, and part of the compliance
exposure in one move. It moves from "phase two" to "start now".

## Compliance boundary

Unity's ToS, updated 2026-06-30, added §17.2(ff) — restricting access to Unity offerings by AI
agents, LLMs, CLIs, MCP clients or servers, and other automated callers outside *Authorized Agentic
Access* — and (gg), barring unauthorized third-party integrations.

The consequential part: **this constrains the act of AI-driving an editor, not the copyright status
of the code**. An MIT license does not exempt you. UniCli and a ported `com.unity.pipeline` sit
under the same cloud.

Unity staff have said informally (Reddit, not the ToS text) that AI-assisted development is fine
and that Authorized Agentic Access applies to connections to Unity's cloud platform, servers, Asset
Store and public APIs. Local editor automation that never logs in and never calls a cloud endpoint
lands on the endorsed side — but the text does not say so, and the maintainer of UniCli has not
responded to the issue raised about it.

So: **local-first, cloud-isolated** is an architectural boundary set now, not a note for later.
Local editor control is the core. Cloud-facing capability is excluded, or off by default and
labeled. Deciding this late would mean retrofitting isolation into work already done.

## Engineering contracts

Verified problems that a unified library has to settle by design rather than per command. Each was
observed, not predicted.

**Destructive-operation gates belong in the routing layer.** Pipeline's `open_scene` /
`create_scene` check Play Mode only and silently discard unsaved changes; UniCli's equivalents
refuse by default and demand an explicit `dirtyAction`. Same operation, opposite contract. The
bridge now re-imposes UniCli's contract, but doing it per command is how the gap appeared in the
first place.

**Cancellation has to be designed in, not added.** Neither tool can interrupt a synchronous command
once it starts. Their timeouts bound how long a *caller* waits, not how long the command runs — a
wedged command is unreachable by either.

**One overload policy.** UniCli rejects concurrent commands outright; pipeline queues them FIFO.
Both are defensible; having both is not.

**Read commands can lie too.** Pipeline's Edit Mode screenshot returns success with an empty
frame. `dry_run` protects writes; nothing protects a read that quietly returns nothing. Result
self-checks — empty-frame detection, non-empty assertions on dumps — belong in the command
contract.

**Structural build exclusion, not runtime flags.** Pipeline's runtime assembly compiles into every
player build, and its console-capture bootstrap is a linker root — we found it live in a release
WebGL build, buffering every log line for a client that could never connect. UniCli's remote
assembly is excluded from release builds by assembly-definition constraints, which is the shape to
copy.

**Absorbing code means absorbing its assembly hygiene.** The Roslyn assemblies the package bundles
are imported Auto Referenced on Any Platform, so Unity passes them to nearly every assembly in the
project — verified in the compiler response files. Harmless with one copy present; a second copy of
any of them turns into `CS0433` across the project.

**Instrumentation goes in its own assembly.** Marking one method `[HotReload]` in a UI library gave
the whole library a compile-time dependency on the automation package and spread IL post-processing
across six assemblies. A dedicated assembly with `defineConstraints: ["UNITY_EDITOR"]` fixes it —
note that `includePlatforms: ["Editor"]` does not work, because MonoBehaviours in an editor-only
assembly cannot be attached to GameObjects.

## What to absorb

Ordered by dependency, not by appeal. Sources are the tools surveyed in the market study — Unreal's
Remote Control API, Chrome DevTools Protocol, the Unity MCP family, SingularityGroup's hot reload,
AltTester, game-ci.

**Status: the first three landed.** What follows marks what is done, what that
changed, and what is still open. Details in the commit history; the shape of each
decision is below.

**First, in parallel:**

1. **Unified command routing with transport envelopes.** *(partly landed — the
   routing layer exists and enforces policy; multiple transports do not yet.)*
   One command definition; named pipe, HTTP,
   MCP and CLI as shells over it. Everything else attaches here. The concurrency and buffering
   conflicts above are all downstream of not having this. Fix JSON output escaping while here —
   the default encoder mangles non-ASCII into `\uXXXX`.
2. **Self-hosted hot reload.** *(proven buildable, not yet built — see
   [`../poc/hotreload/`](../poc/hotreload/).)* Harmony whole-method detour plus
   accessibility relaxation,
   replacing the woven-prologue approach — this clears both the void-only and public-only limits
   and, more importantly, retires the dependency that costs us the upgrade tax. Build the private
   access as a replaceable layer (`skip_visibility` → `InternalsVisibleTo` injection → CoreCLR
   EnC), because Unity 6.8 moves the editor off Mono.
3. **Write safety ring.** *(undo grouping and the cancellation contract landed;
   stale-write detection and compile pre-validation are open.)* Undo by default,
   batch collapsing, stale-write detection, compile pre-validation, `dry_run` —
   plus the dirty-scene gate and cancellation from above.

**Then:** native MCP (in-process C# SDK, layered tool surface, first-connection approval) and an
event subscription channel, so clients stop polling for compile state and domain reloads.

**Then:** multi-instance discovery and routing, Profiler domain completion (frame debugger control,
snapshot comparison), CI degradation paths.

**Deferred:** the player/device tiers — read-only observation, then an embedded agent for real
devices, then IL2CPP code replacement. Highest cost, most unresolved assumptions; gated on a
measured matrix of stripping levels against reflection capability. Note that avoiding GPL by
copying designs rather than code addresses copyright only — distributing device-driving capability
still meets ToS §17.2(gg) separately.

## What M1 changed, concretely

Preconditions are declared and the dispatcher enforces them. Twenty handlers used
to open with `_guard.BeginScope(...)`; the condition was a per-handler constant,
so it lifted whole and the boilerplate is gone. A handler that declares nothing is
unaffected. What did *not* lift is the dirty-scene policy: it reads a request field
and needs to know which scenes a given request touches, and deserialization happens
below the dispatcher. Commands declare `ReplacesOpenScenes` anyway, so the risk is
at least visible where enforcement is not.

Each command's edits now collapse into one undo entry named after the command.
Before, a command's undo footprint was whatever its mutations happened to register —
`GameObject.Create` registers the new object, then parents it and adds components
with raw calls. "One command, one Ctrl+Z" is now a property of the dispatcher.

The single command slot accounts for itself. Ten concurrent requests used to produce
nine refusals blaming a command called "unknown": the slot has two occupied states
and the message could only describe one. It now names the queued command as well as
the running one, and reports how long the running one has been going.

Cancellation got the contract it can actually keep. .NET cancellation is
cooperative, so a handler that ignores its token cannot be stopped and no amount of
framework code changes that. What the framework can do, it now does: refuse work for
a client that already disconnected, let commands declare whether they cooperate, and
report a command that was cancelled and kept running — which turns an editor that
looks frozen into a named command that is still busy.

All of it reaches clients through `Commands.List`, so an agent can read what a
command requires and risks before calling it rather than after.

## What has actually been verified

Not claims — measurements, on Unity 2022.3.62f3 / macOS arm64.

- `com.unity.pipeline@0.5.0-exp.1` runs on 2022.3: 143 commands, `eval` executing through Roslyn in
  the 2022 Mono runtime. Nine obstacles, all documented in
  [`porting-pipeline-to-2022.md`](porting-pipeline-to-2022.md).
- Hot reload works: 279–431 ms for a method-body swap, frame counter continuous across it, no
  domain reload.
- Both servers coexist in one editor for 11 hours with zero errors: separate transports, no key or
  path collisions, both surviving domain reloads. The one real conflict is the dirty-scene contract.
- The bridge composes without deadlock, `eval` round-tripping in ~216 ms.
- A release WebGL/IL2CPP build strips the Roslyn and hot-reload machinery cleanly; the console
  bootstrap was the sole leak, now guarded and re-verified.
- **The validation suite stayed green throughout** — 282 items growing to 339, across the
  coexistence period, the package port, three rounds of package edits, the assembly decoupling and
  a UniCli upgrade. Interactive 339 and 678 (both backends), headless CI 678 with exit 0, no
  failure attributable to any of it. This is the most direct evidence that the absorb-and-fork
  route is low-risk.

- **Detour-based hot reload works on this Mono** — Stage A of the PoC, 9/9. A private instance
  method returning a value was replaced at runtime and the replacement read the target's private
  field: both of the weaver's limits cleared in one assertion. Instance state survives patching and
  unpatching restores the original. Details and the reproduction in
  [`../poc/hotreload/`](../poc/hotreload/).

  Two things the PoC pinned down that change how this gets built. **JIT inlining is the real
  constraint** — Mono inlines small methods, and detouring one has no effect at a call site that was
  already inlined; `MONO_INLINELIMIT=0` in the editor environment clears it globally, which is what
  makes the 9/9 run green. And **the Harmony build matters**: the NuGet `lib/` assemblies are not
  self-contained and fail in ways that point at the wrong culprit; the GitHub "Fat" release works.

- **Private access needs no runtime hacking** — Stage B, 10/10 across two probes. Private access is
  refused twice, and each barrier has a supported key: the compiler is opened with Roslyn's
  `MetadataImportOptions.All` plus `BinderFlags.IgnoreAccessibility`, and the runtime with
  `DynamicMethod(skipVisibility: true)`, reached by re-emitting the compiled body through MonoMod's
  `DynamicMethodDefinition`. The full chain runs: source naming private members → compiled →
  re-emitted → detoured → executing with private state reachable.

  This is better than the plan assumed. The `skip_visibility` layer was scoped as a Mono-only
  stopgap needing replacement when Unity 6.8 moves the editor off Mono; `DynamicMethod`'s flag is
  standard .NET and carries to CoreCLR. The one fragile dependency left is Roslyn's internal
  `BinderFlags` — a compile-time reflection lookup that fails loudly, not a native struct layout.

- **The routing and safety work holds up in a live editor**, not only in tests:
  creating a GameObject with two components and pressing undo once removes it
  entirely, and `Commands.List` reports 19 commands as single-undo-step, 19 as
  requiring an editor state, and the three scene commands as replacing open scenes.
  Unity EditMode 139/139 (28 of them new, covering a dispatcher path that had no
  coverage at all), client 52/52.

Still assumption: everything beyond replacing a single method body — signature changes, added
fields, rebinding callers that already resolved the old shape. Those are the cases SingularityGroup
handles by recompiling affected callers; known engineering shapes rather than open questions, now
that the mechanism underneath is established.
