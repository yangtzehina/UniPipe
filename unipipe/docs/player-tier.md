# Player observation

Reading a running build, over Unity's own PlayerConnection.

```bash
unicli exec Connection.Connect '{"ip":"192.168.3.8"}'
unicli exec Remote.List
unicli exec Remote.Invoke '{"command":"Debug.RenderStats"}'
```

## Why this tier exists

Rendering regressions cannot be measured from the editor. Batch mode runs no per-frame display
render, so every render counter there reads zero — Unity's own included, not just the editor's
(see [`render-stats.md`](render-stats.md)). A running player renders every frame, which makes it
the only place the question can be answered automatically.

`Debug.RenderStats` closes that. It reads Unity's profiler counters in the player and reports the
totals with the dynamic / static / instanced batching breakdown, at the player's resolution, on the
player's hardware.

## Measured against a running player

A scene alternating every three seconds between 20 cubes on 20 distinct materials and the same 20
cubes on one shared instanced material, sampled from the editor while it ran at 1920×1080:

| | batches | SetPass | draw calls | instanced | triangles |
|---|---|---|---|---|---|
| 20 distinct materials | 23 | 23 | 23 | 0 | 1924 |
| one shared instanced material | **4** | 4 | 4 | **1 batch / 20 draw calls** | 1924 |

Triangles stay at 1924 across both: the geometry never changed, only how it was batched. That is
the invariant that makes the batch numbers trustworthy.

## Stripping

The command registry finds commands by reflection — `GetTypes()` then `Activator.CreateInstance` —
which is exactly what managed stripping removes. The plan gated this tier on measuring that, so:

| build | result |
|---|---|
| Mono, stripping Disabled | 8 commands, all counters available |
| Mono, stripping **High** + strip engine code | **8 commands, all counters available** |
| IL2CPP | **not measured** — the IL2CPP module is not installed on this machine |

The `[Preserve]` and `[RequireDerived]` attributes on the command base classes, plus the package's
linker processor, survive High stripping intact. IL2CPP remains unmeasured and should not be
assumed from this.

## What is in the build, and what is not

The remote assembly carries two `defineConstraints`:

```
UNICLI_REMOTE || UNITY_EDITOR
DEVELOPMENT_BUILD || UNITY_EDITOR
```

Both must hold, so a release build excludes it structurally rather than by a runtime flag — and so
does a development build that has not opted in. This is deliberate, and it is worth knowing before
debugging a silent player: a build without `UNICLI_REMOTE` in its scripting define symbols answers
`Remote.List` with a timeout, because the receiver is not in it at all.

## Read-only, on purpose

Everything in this tier observes. There is no remote command that moves an object, calls a method,
or writes a preference. Verified working against the player: `Debug.SystemInfo` (30 fields),
`Debug.Stats` (19 fields, 59 fps), `Debug.GetScenes`, `Debug.GetHierarchy`, `Debug.GetLogs`,
`Debug.FindGameObjects` (found all 20 cubes), and `Debug.RenderStats`.

The test scene alternates its own materials on a timer for exactly this reason — proving the
batching breakdown responds to a real change needed no way to poke the player.

## Discovery, and when it fails

The player broadcasts on multicast `225.0.0.222:54997`. On this machine the editor never saw it:
the host had a `198.18.0.1` interface alongside the LAN address — the signature of a VPN or proxy
tunnel — and the player logged `Failed to initialize networking layer after 30 seconds`.

`Connection.Connect` with an explicit `ip` bypasses discovery entirely and worked first time. Worth
reaching for early rather than debugging multicast on a machine with a tunnel on it.

## Counters

Fifteen are requested; every one was available on macOS/Metal. A counter a platform does not emit is
reported by name in `unavailableCounters` rather than as a zero, because "not measured here" and
"nothing drawn" are otherwise the same number.

They are started at `RuntimeInitializeOnLoadMethod` rather than per request. A `ProfilerRecorder`
collects only from the moment it starts, so one created inside a request handler would answer its
own first call with nothing. The cost is a handful of counters running continuously in a
development build — what any on-screen stats overlay does.
