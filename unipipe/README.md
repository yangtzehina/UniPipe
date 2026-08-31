# UniPipe

A fork of [yucchiy/UniCli](https://github.com/yucchiy/UniCli) that is growing into a single
Unity editor-automation library: one command surface, several front ends (CLI, MCP, CI), and the
capabilities we currently have to reach across two separate tools to get.

Everything this fork adds lives under `unipipe/`. The rest of the tree is upstream UniCli,
untouched, so merging upstream stays conflict-free.

## Why fork

UniCli drives a running Unity Editor over a named pipe: 142 editor commands, handler
auto-discovery, and — uniquely among the tools we surveyed — Profiler, memory-snapshot and
recorder domains. It is MIT, so it can be forked, extended and redistributed.

Unity's own `com.unity.pipeline` covers similar ground over local HTTP and adds things UniCli has
no answer for, most importantly **in-place C# hot reload**. It is also under the Unity Package
Distribution License, which permits use inside your own project but not redistribution — so it
cannot be merged into a library, only referenced or reimplemented.

UniPipe takes UniCli as the base and absorbs the *designs* worth having, rather than the code we
are not allowed to carry.

## What is here now

| Path | What it is |
|---|---|
| `unipipe/bridge/` | An optional editor script exposing `com.unity.pipeline`'s command surface through UniCli as one facade. Transitional — see below. |
| `unipipe/samples/UiHotTuning/` | The assembly layout that lets you hot-reload UI tuning code without coupling your UI library to the automation package. |
| `unipipe/docs/porting-pipeline-to-2022.md` | Every obstacle to running `com.unity.pipeline` on Unity 2022.3, and how each was cleared. Useful on its own, independent of this fork. |
| `unipipe/docs/hot-reload.md` | Applying an edited method body to the running editor without a domain reload — how, what it refuses, and why. |
| `unipipe/docs/mcp.md` | Driving the editor from an AI client — the tool surface, the error semantics, and the local-only boundary. |
| `unipipe/docs/events.md` | Catching up on what the editor did, instead of polling for it — cursors, kinds, and the push stream. |
| `unipipe/docs/plan.md` | Where this is going and why, including the compliance boundary the design is built around. |

The bridge is deliberately marked transitional. It exists because hot reload is currently the one
capability we genuinely depend on the Unity package for; once UniPipe implements hot reload
natively, the bridge becomes a reference implementation rather than a dependency. See the plan.

## Status

Early. The bridge and the porting notes are verified working — see the plan for what has been
measured and what has not. The library work described there has not started.

## Compliance

Unity's Terms of Service, updated 2026-06-30, restrict automated and AI-driven access to Unity
offerings (§17.2 ff, gg). Unity staff have stated informally that this targets connections to
Unity's cloud services rather than local editor automation, but the ToS text does not say so.

UniPipe is therefore designed **local-first, cloud-isolated**: driving an editor on your own
machine is the core, and any capability that would talk to Unity's cloud services is kept out or
kept off by default. This is an architectural boundary, not a disclaimer — see the plan.

## License

MIT, inherited from UniCli. Upstream code remains Copyright (c) 2026 Yuichiro Mukai; additions
under `unipipe/` are Copyright (c) 2026 yangtzehina, same license. See `LICENSE`.

Not affiliated with or endorsed by Unity Technologies. No Unity source code is redistributed here.
