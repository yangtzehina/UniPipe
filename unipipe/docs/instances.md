# Instances

Which editor a command reaches, when more than one is open.

```bash
unicli instances
```

```
NAME          STATE      UNITY            PID  UPTIME  PROJECT
OzzWeb        reloading  2022.3.62f3    96841  25h09m  /Users/ai/ECS/OzzWeb
UniCli.Unity  ready      2022.3.62f3    41899   4h42m  /Users/ai/ECS/UniPipe/src/UniCli.Unity
```

## The problem discovery solves

An editor's address is derived from its project path — hash the path, get the pipe name. That works
from inside the project and nowhere else. A shell in `/tmp`, a CI runner, an agent holding two
projects open: none of them can ask a question without first being told the answer.

Worse, the failure was indistinguishable from a healthy editor being busy. A wrong `UNICLI_PROJECT`
produced "server is not running", which is also what a compiling editor produces.

Now each editor advertises itself, and a caller can ask what is running.

## Addressing

`UNICLI_PROJECT` takes a path as it always did, and now also takes a name:

```bash
UNICLI_PROJECT=MyGame unicli exec Editor.Status      # by name
UNICLI_PROJECT=b/MyGame unicli exec Editor.Status    # by enough path to be unambiguous
UNICLI_PROJECT=src/UniCli.Unity unicli exec Compile  # unchanged: a path is still a path
```

Resolution order, and the reason for it:

1. **An existing directory** — someone who typed a path meant it, including a path no running
   editor matches. That case still launches Unity, as it always did.
2. **The working directory**, if it is inside a project. Cheapest correct answer, and it reads no
   registry at all.
3. **The registry**, if exactly one editor matches.

A name is matched case-insensitively, then by any trailing run of path segments — so two projects
called `MyGame` are told apart by `a/MyGame` and `b/MyGame` without typing either path in full.

## Ambiguity is refused, not guessed

```
Several editors are running and none was named.
    OzzWeb                   /Users/ai/ECS/OzzWeb  (reloading)
    UniCli.Unity             /Users/ai/ECS/UniPipe/src/UniCli.Unity
  Name one with UNICLI_PROJECT, or run from inside the project directory.
```

Exit code 1. Picking one of two editors named `MyGame` would send writes into a project the caller
never named, and nothing downstream would notice — the same class of silent mistake the dirty-scene
gate exists to prevent.

## The three states

| State | |
|---|---|
| `ready` | the server answered; commands will be accepted |
| `reloading` | the process is alive but the server is not answering — almost always a domain reload |
| `stale` | the process is gone; the record is a leftover |

`reloading` is worth its own state because waiting is the right response to it and not to the
others, and because a reloading editor is still a valid routing target — refusing to route to one
would make every recompile look like the editor had vanished.

## Records are hints, never truth

A record is a small JSON file in `~/.unicli/instances/`, named after the pipe. An editor that
crashes leaves its file behind — there is no shutdown hook for a process that dies — so liveness is
established by looking at the process and connecting to the pipe, never by the file's presence.
`unicli instances` deletes the records whose process is gone, and says how many.

Probing is a connect and nothing more. Sending a command would queue behind whatever that editor is
already running, turning a listing into a wait of unbounded length.

## Lifetime

Published from the bootstrap's static constructor, withdrawn when the editor quits — deliberately
the same lifetime as the PID file, and deliberately *not* the server's. The server is torn down and
rebuilt on every domain reload while the editor stays up; tying the record to it would make an
editor vanish from the registry every time it recompiles, which is exactly when a client most needs
to be told it exists but is momentarily unreachable.

The record's `startedAt` is the process start, not the time of writing, so it is byte-identical
across reloads and the file is not rewritten on every recompile.

## Isolating the registry

`UNICLI_HOME` moves the state directory (default `~/.unicli`). Set it per job on a CI machine where
several builds run as the same user, so parallel jobs cannot discover each other's editors. The
tests set it for the same reason.

## What deliberately does not use discovery

**`unicli install`** writes to a project's `manifest.json`. Discovery answers "which running editor
do I talk to", not "which directory do I modify"; installing into a project the caller never named
is a write, and writes get the conservative rule.

**Shell completion** runs on every tab press. It stays on the working directory, because probing —
and, on a stale record, connecting and then launching Unity — is not something to do while someone
is typing.

## Verified

On a machine with three editors running: the live one advertised itself and was reachable by name
and from a directory outside any project; a second record was refused rather than guessed at, with
both paths listed and exit code 1; naming one resolved it; a record pointing at a dead process was
pruned and the live ones were not. `UNICLI_PROJECT=src/UniCli.Unity` — a relative path, the old
usage — behaved exactly as before.
