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

1. **Unified command routing with transport envelopes.** *(landed.)* One command
   definition; named pipe, HTTP,
   MCP and CLI as shells over it. Everything else attaches here. The concurrency and buffering
   conflicts above are all downstream of not having this. Fix JSON output escaping while here —
   the default encoder mangles non-ASCII into `\uXXXX`.
2. **Self-hosted hot reload.** *(landed — `HotReload.Apply`, see
   [`hot-reload.md`](hot-reload.md).)* Harmony whole-method detour plus
   accessibility relaxation,
   replacing the woven-prologue approach — this clears both the void-only and public-only limits
   and, more importantly, retires the dependency that costs us the upgrade tax. Build the private
   access as a replaceable layer (`skip_visibility` → `InternalsVisibleTo` injection → CoreCLR
   EnC), because Unity 6.8 moves the editor off Mono.
3. **Write safety ring.** *(landed.)* Undo by default,
   batch collapsing, stale-write detection, compile pre-validation, `dry_run` —
   plus the dirty-scene gate and cancellation from above.

**Then:** *(both landed — [`mcp.md`](mcp.md), [`events.md`](events.md).)* native MCP and an event
subscription channel, so clients stop polling for compile state and domain reloads.

MCP arrived as a transport rather than a second implementation: a tool call becomes the same
`CommandRequest` the pipe carries, so it inherits the command slot, the preconditions and the undo
grouping for free — the payoff of having built the routing layer first.

Events answer a question status could not. Polling `Editor.Status` until it looks settled is lossy:
a compile that started and finished between two polls reads the same as an idle editor, and so does
a domain reload. Sequenced events with a cursor answer "what happened since I last looked", and the
buffer survives the domain reload that would otherwise erase the record of itself. Clients that can
hold a connection get the same events pushed over SSE; the stream reads the buffer directly, so a
subscriber cannot starve the single command slot. Eight tools are exposed
rather than 136, with an escape hatch for the rest, because listing every command would spend an
agent's context before it acts. The declared traits go into the tool descriptions, which is where
they finally have a reader. Built without the official C# SDK, whose dependency tree the package
does not otherwise need.

**Then:** *(discovery, CI degradation and render statistics landed — [`instances.md`](instances.md),
[`ci.md`](ci.md), [`render-stats.md`](render-stats.md); snapshot comparison was already there;
frame debugger control is blocked.)*
multi-instance discovery and routing, Profiler domain completion (frame debugger control, snapshot
comparison), CI degradation paths.

Discovery removes an assumption the whole client rested on: that a caller already knows the project
path, because the address is derived from it. Editors now advertise themselves in a per-user
registry, so `unicli` works from outside any project and an editor can be named rather than
pathed. Two rules carry the weight. Records are hints — a crashed editor leaves its file behind, so
liveness comes from the process and the pipe, never the file. And ambiguity is refused rather than
guessed: choosing one of two editors named `MyGame` would send writes into a project nobody named,
which is the dirty-scene failure again in a different costume. Probing is a connect and nothing
more, because sending a command would queue behind whatever that editor is already doing.

The CI work turned out to be about the "read commands can lie" contract rather than about error
messages. Measured on one project across three environments: `Screenshot.Capture` under
`-batchmode -nographics` took the editor down with a native crash; under plain `-batchmode` it
returned success and a fully transparent frame; `Scene.Screenshot3D` under `-nographics` returned
success and a buffer whose every byte, alpha included, was `0xCD`. Three failures, one of which
looked like one. Commands now declare what they need from the environment and the dispatcher
refuses beforehand, because there is no error handling around a dead editor. The measurement also
corrected the design: `Scene.Screenshot3D` works fine under plain `-batchmode`, so the requirement
is a graphics device rather than an interactive editor, and gating both on batch mode — the obvious
first guess — would have removed a capability CI actually wants.

Of the Profiler track, snapshot comparison turned out to be done already and simply unrecorded here.
Verified rather than assumed: allocating exactly 400 arrays of 64 KB between two captures, the diff
reported `countDelta: 400` and `sizeDelta: 26,227,200` — the 26,214,400 bytes allocated plus 32
bytes of array header each. `MemorySnapshot.Diff`, `Analyze` and `AllOfMemory` all take a baseline.

Frame debugger control is blocked, and the blocker is worth recording so it is not rediscovered.
The data lives behind `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility` — a namespace
that already moved once between Unity versions — and the public `UnityEngine.FrameDebugger` exposes
only a read-only `enabled`. Capture could not be driven from outside the editor's own window:
`SetEnabled`, raising `limit`, forcing repaints, opening a Game View, calling the window's own
`OpenWindowAndToggleEnabled`, and running with the editor foregrounded all left `count` at zero.
The capture appears to be driven by `FrameDebuggerWindow`'s multi-frame enabling sequence in lockstep
with a rendering Game View. Reproducing that means reverse-engineering internal editor code for a
command that, by the environment rules above, could only ever run on an interactive editor — the
opposite of where the automation value is. Left unbuilt rather than shipped unverified.

What replaced it answers the same question through public API. `Render.GetStats` reads
`UnityEditor.UnityStats` — the Game View's Statistics overlay — and reports the batching breakdown,
which is what "draw calls went up" actually needs: 20 cubes with 20 materials measured 71 batches
and no instancing; the same cubes on one instanced material measured 7, attributed to 4 instanced
batches covering 68 draw calls. The numbers persist after the render that produced them, so the
command repaints, lets the editor render, and reports the resolution they were measured at.

It does not, however, deliver the CI property it was chosen for, and the reason is worth keeping.
Under `-batchmode` every counter reads zero at resolution `0x0` — and so do Unity's own profiler
counters, because batch mode runs no per-frame display render at all. The same run had
`Time.renderedFrameCount` climbing into the millions while an explicit `camera.Render()` produced a
real image: the GPU works, nothing drives a per-frame render. There are no render statistics to
take rather than an API failing to report them. The environment gate turns that into a refusal. The
environment that does render per frame is a built development player over PlayerConnection — the
deferred player tier.

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

Two more hazards close the ring, both cases where an agent can do damage it cannot
see, and both needing the deserialized request — so they are helpers commands opt
into rather than gates the dispatcher imposes.

`AssemblyDefinition.AddReference` and `RemoveReference` read a file, change it and
write it back; anything that touched it in between was discarded without a word.
`Get` now reports a fingerprint and the mutating commands take it back as
`expectedSha`, refusing when the file has moved on. Omitting it opts out.

The routing layer earned its keep: the named pipe and an HTTP loopback transport
are now two front ends onto the same server. A command POSTed over HTTP goes
through the same single command slot, the same precondition checks and the same
undo grouping as one from the CLI — a GameObject created over HTTP is visible over
the pipe and is removed by a single undo, because both arrived at one dispatcher.
HTTP is opt-in (`UNICLI_HTTP=1`) and loopback-only with no auth yet, so it stays a
local convenience until a bearer token is added.

And writing a broken `.cs` is not an ordinary error: a project that fails to
compile can drop the editor into Safe Mode, where the server is gone and nothing
can reach it — including whatever would put the file back. We hit that during the
package port. `Script.Validate` compiles source in isolation and reports errors
with line and column, so the check happens before the file is written; it reuses
eval's compilation path rather than adding a Roslyn dependency, and never loads
what it builds.

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
  Unity EditMode 153/153 (42 of them new, covering a dispatcher path that had no
  coverage at all), client 52/52.

- **Hot reload is a command now**, not a proof: `HotReload.Apply` compiles an edited file on its
  own, matches each recompiled method to the loaded one, and detours it. Verified against a live
  editor — an object called three times kept its counter and its identity across an edit that
  changed its result and read a private field, with no recompile and no domain reload. The layout
  check refuses a type whose fields moved, which is the difference between this and a demo.

Still assumption: everything beyond replacing a single method body — signature changes, added
fields, rebinding callers that already resolved the old shape. Those are the cases SingularityGroup
handles by recompiling affected callers; known engineering shapes rather than open questions, now
that the mechanism underneath is established.
