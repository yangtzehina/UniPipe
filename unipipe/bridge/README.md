# Pipeline bridge (transitional)

Exposes `com.unity.pipeline`'s command surface through UniCli, so one front end reaches both
command sets:

```bash
unicli exec Pipeline.Exec '{"command":"editor_status"}'
unicli exec Pipeline.Exec '{"command":"reload_file","args":"{\"filename\":\"Assets/X.cs\"}"}'
```

`Pipeline.Exec` is an ordinary UniCli handler. It reads the port and bearer token from the
pipeline server's port file (`Library/Pipeline/.unity-pipeline-port`) and forwards over loopback
HTTP. Neither package is modified — this file is the only new code.

## Install

Drop `Editor/UniPipeBridge.cs` anywhere under `Assets/` in a project that has both the UniCli
server package and `com.unity.pipeline` installed. UniCli discovers the handler automatically.
Getting the Unity package onto 2022.3 is covered in
[`../docs/porting-pipeline-to-2022.md`](../docs/porting-pipeline-to-2022.md).

## Why it is transitional

Hot reload is the only pipeline command with no UniCli equivalent — everything else we exercised
(status, recompile, eval, console, build, scene and GameObject authoring) has one, and for
screenshots UniCli's is the better implementation. Once UniPipe implements hot reload natively,
this bridge stops being a dependency.

## Two things the implementation gets right, deliberately

**It must stay `async`.** The handler runs on UniCli's main-thread pump, and the pipeline server
needs that same main thread to execute the command being forwarded. `await` yields it back;
blocking on `.Result` would make the main thread wait for a result only it can produce. That
deadlock does resolve — after the pipeline server's timeout, which defaults to 60 s and which
`eval` allows callers to raise to 24 hours.

**It re-imposes a dirty-scene contract.** Pipeline's `open_scene` and `create_scene` check Play
Mode only; when they replace the open scenes they discard unsaved changes silently. UniCli's own
scene commands refuse that by default and require an explicit `dirtyAction`. Forwarding raw would
let a caller lose work through a door they believe is guarded, so the bridge applies the same
contract:

```bash
# refused, with the dirty scenes named
unicli exec Pipeline.Exec '{"command":"open_scene","args":"{\"path\":\"Assets/A.unity\"}"}'

# explicit intent
unicli exec Pipeline.Exec '{"command":"open_scene","args":"{\"path\":\"Assets/A.unity\"}","dirtyAction":"discard"}'
```

`dirtyAction` accepts `error` (default), `save` and `discard`. `save` refuses a scene that has
never been saved rather than raising a modal save dialog, which would hang a headless editor.
Commands passing `additive: true` are not gated — they do not replace anything.

Guarding at the forwarding layer is a stopgap: the right shape is one contract in the routing
layer, so every destructive command inherits it instead of each one remembering to ask.

## Known limits

- A forwarded command occupies UniCli's single command slot for the whole HTTP round trip. UniCli
  rejects concurrent commands rather than queuing them, so anything else sent meanwhile gets a
  "server is busy" error — for a duration the pipeline side decides.
- Only scene-replacing commands are gated. Other destructive pipeline commands rely on that
  package's own `confirm` / `dry_run` gates.
