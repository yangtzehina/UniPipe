# Headless and CI

What a command does when the editor has no windows and no graphics device — and why some of them
now refuse to run.

## What was measured

Unity 2022.3.62f3, one project, the same two commands, three environments:

| | `Screenshot.Capture` | `Scene.Screenshot3D` |
|---|---|---|
| Interactive editor | real render | real render, 445 distinct colours |
| `-batchmode` (Metal present) | **success, fully transparent frame** (`00000000` everywhere) | real render, 702 distinct colours |
| `-batchmode -nographics` | **native crash, editor dies** | **success, unrendered buffer** (`cdcdcdcd` everywhere, alpha included) |

Three failures, and only one of them looked like a failure. The crash was in
`MonoGUIView::IsHDRActive()`, reached through `PlayModeView.RenderView` — one command call and the
CI job has no editor left. The other two returned exit code 0 and a PNG, which a pipeline would
have archived as the screenshot.

The uniform `cdcdcdcd` is the tell: a camera clearing to a colour writes alpha `ff`. A buffer whose
alpha is also `cd` was never rendered into.

## The gate

Commands declare what they need from the environment, and the dispatcher refuses before the handler
runs:

```csharp
[CommandPrecondition(
    Environment = EnvironmentRequirement.Graphics | EnvironmentRequirement.InteractiveWindows,
    AlternativeCommand = "Scene.Screenshot3D")]
public sealed class ScreenshotCaptureHandler : ...
```

```
$ unicli exec Screenshot.Capture      # under -batchmode
Cannot execute 'Screenshot.Capture': batch mode has no interactive editor windows, so this
returns an empty frame while reporting success. Scene.Screenshot3D works here.
```

Exit code 1. Before the handler runs, because one of these failures is not recoverable afterwards —
there is no error handling around a dead editor.

## Why two requirements and not one

`Scene.Screenshot3D` works perfectly under plain `-batchmode`. Gating everything screenshot-shaped
on batch mode would have removed a capability that demonstrably works, and it is the capability a
CI job actually wants.

So the requirements name what is really missing:

| | |
|---|---|
| `Graphics` | a working graphics device; absent under `-nographics` |
| `InteractiveWindows` | editor windows that actually render; absent under `-batchmode`, which has the window objects but nothing behind them |

`Screenshot.Capture` needs both. The scene screenshots need only `Graphics`. The distinction came
out of the measurement, not from reasoning about it — the first guess was that batch mode was the
problem for both, and it was wrong in the direction that would have cost a working feature.

## Refusals name a way forward, when there is one

`Screenshot.Capture` refused in batch mode points at `Scene.Screenshot3D`, verified to produce a
real render there.

Under `-nographics` it names nothing, deliberately: the alternative is another rendering command,
and it is refused too. A suggestion that cannot work is worse than none.

## Ordering

Environment is checked before editor state. Play Mode is something a caller fixes by waiting; a
missing graphics device is not going to appear. Reporting the recoverable condition first would
send a CI job into a retry loop against a wall.

## What clients see

`Commands.List` reports `requiresEnvironment`, so a client can skip what cannot work instead of
collecting blank results. MCP tool descriptions carry it too — a model calling
`unity_screenshot` is told it "needs an interactive editor; returns an empty frame in batch mode"
before it calls rather than after.

## Discovery in batch mode

A headless editor advertises itself like any other, so `unicli instances` finds CI editors and they
can be addressed by name. Set `UNICLI_HOME` per job where several builds share a machine and a user
account — see [`instances.md`](instances.md).

## Verified

With the gate in place, on the same project: under `-batchmode -nographics`, `Screenshot.Capture`
was refused with exit code 1 and the editor survived — zero native crash frames in a log that
previously carried 44. `Scene.Screenshot3D` was refused rather than returning its blank buffer.
`Window.List` (197 window types) and `Compile` were unaffected, which is the point of gating on a
measured requirement rather than on "looks like it touches the GUI". Under `-batchmode` with a
graphics device, `Screenshot.Capture` was refused naming `Scene.Screenshot3D`, and that command
then produced a real 702-colour render.

The scene-screenshot and game-view captures are the commands measured here. Recorder was not: the
package was not installed in the probe project.
