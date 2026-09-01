# Render stats

What the Game View drew, and which batching path handled it.

```bash
unicli exec Render.GetStats
```

```
Resolution:  691x352
Batches:     7
SetPass:     7
Draw calls:  7
Triangles:   2,104
Vertices:    5,888

Batching:
  Dynamic:   0 batches from 0 draw calls
  Static:    0 batches from 0 draw calls
  Instanced: 4 batches from 68 draw calls

Shadow casters:  0
Render textures: 14 (25.2 MB), 5 changes
Frame time:      104.74 ms (render 0.67 ms)
```

## The breakdown is the point

"Draw calls went up" is a symptom. Which batching path stopped working is the question a rendering
or UI change actually raises, and the dynamic / static / instanced split is what answers it.

Measured on an empty scene, twice:

| | batches | SetPass | instanced |
|---|---|---|---|
| baseline | 2 | 2 | 0 |
| 20 cubes, 20 distinct materials | 71 | 45 | 0 batches |
| the same 20 cubes, one shared material with instancing | **7** | 7 | **4 batches from 68 draw calls** |

The collapse from 71 to 7 is attributed to the path that caused it, without guessing.

## Where the numbers come from, and when they are true

`UnityEditor.UnityStats` — public API, the same values the Game View's Statistics overlay shows.

They describe the **most recent Game View render**, and they persist after it. Reading them blind
means reporting an old frame as if it were this one, so the command asks for a repaint, lets the
editor render, and reports the resolution the numbers were measured at. Pass `repaint: false` to
read whatever is already there without disturbing the editor.

The resolution matters beyond provenance: batch counts depend on what is on screen, so numbers taken
at two different resolutions are not comparable.

## Batch mode has nothing to report

Measured on 2022.3.62f3 under `-batchmode` with a working Metal device, in both Edit Mode and Play
Mode: every counter reads zero and the resolution is `0x0`.

This is not the API hiding something. Batch mode runs **no per-frame display render at all** — the
same run showed `Time.renderedFrameCount` climbing into the millions while an explicit
`camera.Render()` into a RenderTexture produced 424 distinct colours. The GPU works; nothing is
driving a per-frame render, so there are no render statistics to take. Unity's profiler counters
(`Batches Count`, `SetPass Calls Count`, `Draw Calls Count`) read zero there for the same reason, so
this is not a limitation of `UnityStats` in particular.

So the command declares `Graphics | InteractiveWindows` and is refused headless rather than
answering with a page of zeros that looks like data. See [`ci.md`](ci.md).

For rendering regressions in CI, the environment that actually renders per frame is a built
development player over PlayerConnection. That is `Debug.RenderStats`, and it works headless —
see [`player-tier.md`](player-tier.md).

## Nothing drawn yet

Even in an interactive editor, a Game View that has never drawn reports `0x0`. The command says so
rather than returning the zeros underneath:

```
No frame has been rendered, so there are no statistics to report.
  Open a Game View; the numbers describe what it last drew.
```

## Verified

The batching table above is a live measurement, cleaned up afterwards back to the 2-batch baseline.
Refusal in batch mode is covered end to end against a real `-batchmode` editor, and the resolution
predicate that separates "nothing rendered" from "rendered nothing" is unit tested.
