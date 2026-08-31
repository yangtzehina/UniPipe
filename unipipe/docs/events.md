# Events

What the editor did, as a sequence a client can catch up on.

```bash
unicli exec Events.Poll                      # everything buffered
unicli exec Events.Poll '{"since":42}'       # only what is new
unicli exec Events.Poll '{"kinds":["compile","domain"]}'
```

```
     5  compile.started    Script compilation started.
     6  compile.finished   Script compilation finished.
     9  domain.reloading   Assemblies are about to reload; in-memory state is being discarded.
    10  domain.reloaded    Assemblies reloaded; the editor is running new code.
cursor=10
```

## Why not just poll status

An agent that starts a compile has to know when it finished. Polling `Editor.Status` until it looks
settled is wasteful, and worse, lossy: a compile that started and finished between two polls is
indistinguishable from nothing having happened, and so is a domain reload. Status answers "what is
true now"; the question a caller actually has is "what happened since I last looked".

Pass back the `cursor` from the previous call and you get exactly the events in between — or are
told you fell behind.

## Kinds

| Kind | |
|---|---|
| `compile.started` | a compilation run began |
| `compile.failed` | one assembly failed, with the error count and the first message |
| `compile.finished` | the run ended |
| `domain.reloading` | assemblies are about to reload; in-memory state is going away |
| `domain.reloaded` | the editor is running new code |
| `playmode.changed` | entering or leaving play mode |
| `log.error` | an error, exception or assertion |

`kinds` filters on the dotted prefix, so `"compile"` matches all three compile events.

**Errors only, not a log mirror.** Ordinary console output would evict the state transitions this
stream exists to carry. `Console.GetLog` remains the place to read logs.

## Falling behind

The buffer holds 256 events. A caller that asks for events older than the buffer still has gets
`dropped` in the response — the count it missed. It is told, rather than handed a gap it would read
as an idle editor.

## Surviving domain reloads

The buffer persists through a domain reload, which is the moment a client is most in the dark: the
compile it triggered discarded everything in memory, including any record of what happened. Poll
after a reload and the whole cycle is there, `domain.reloading` and `domain.reloaded` included.

`domain.reloaded` is published from a static constructor rather than from `afterAssemblyReload` —
the domain that would have raised that event no longer exists to do it.

## Push instead of polling

With the HTTP transport enabled (`UNICLI_HTTP=1`), a client can hold a stream open:

```bash
curl -N "http://127.0.0.1:$(cat Library/UniCli/http-port)/events?since=0"
```

```
id: 5
event: compile.started
data: {"seq":5,"kind":"compile.started","timestamp":1788173654583,"message":"Script compilation started.","data":""}
```

Standard server-sent events. Anything buffered after `since` is delivered first, so a reconnecting
client does not miss what happened while it was away; new events follow as they are published, and
a keep-alive comment goes out every 15 seconds.

The stream reads the event buffer directly rather than going through the command layer, so holding
one open cannot starve command traffic. That is also why `Events.Poll` returns immediately instead
of waiting: the server runs one command at a time, and a long poll would hold that slot against
every other caller.

## Verified

On Unity 2022.3.62f3: writing a script and compiling produced `compile.started`,
`compile.finished`, `domain.reloading` and `domain.reloaded`, all still readable *after* the reload
that would otherwise have erased them. Over SSE, entering and leaving play mode delivered four
`playmode.changed` frames as they happened.
