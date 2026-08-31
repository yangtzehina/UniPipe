# MCP

An AI client can drive the editor directly, instead of shelling out to the CLI.

```bash
UNICLI_MCP=1 /path/to/Unity -projectPath .
```

The port is written to `Library/UniCli/mcp-port`; point an MCP client at
`http://127.0.0.1:<port>/`.

MCP is a transport here, not a second implementation. A tool call is decoded into the same
`CommandRequest` the named pipe carries and handed to the same dispatcher, so it inherits the single
command slot, the declared preconditions and the undo grouping without knowing they exist. The tool
list is generated from the same metadata `Commands.List` returns, so a project's own commands are
reachable the moment they compile.

## Eight tools, not 136

Listing every command would spend a large part of an agent's context before it does anything, and
most of it on commands it will never call. What is exposed instead:

| Tool | |
|---|---|
| `unity_status` | play mode, compiling, dirty scenes |
| `unity_compile` | recompile and report errors |
| `unity_console` | read the console |
| `unity_eval` | run a C# snippet |
| `unity_hierarchy` | the GameObject tree |
| `unity_screenshot` | capture the Game view |
| `unity_list_commands` | every command, with parameters |
| `unity_run_command` | run any command by name |

The first seven are the loop an editor-driving agent actually repeats. Everything else — all 136 —
goes through `unity_run_command`, with `unity_list_commands` for discovery. Nothing is unreachable;
discovery is a tool call rather than a standing tax on every conversation.

A core tool is omitted when its command is not available, so a project with a module disabled is not
offered a tool that cannot run.

## Traits reach the model before the call

Tool descriptions carry what the command declared about itself:

```
unity_compile — Recompile scripts and report errors and warnings.
                (requires the editor to be out of Play Mode)
```

Commands that replace open scenes, that are destructive, or that need a particular editor state say
so in their description. The model learns it before calling rather than from the refusal afterwards.

## Errors

Two kinds, kept distinct because they mean different things to a client:

- **Protocol errors** (`error` in the envelope) — the call was malformed: an unknown tool, a missing
  command name. Nothing reached the editor.
- **Tool errors** (`isError: true` in the result) — the call was well-formed and did reach the
  editor, which refused or failed. The reason is the result text, so the model can act on it.

A command refused by its preconditions is the second kind, and reads as
`Cannot execute 'Scene.Open' while in Play Mode. Exit Play Mode first (PlayMode.Exit).`

## Scope, and why it is off by default

- **Loopback only.** Non-loopback callers are refused.
- **Off unless `UNICLI_MCP=1`.**
- **No authentication.** While it is on, anything already on the machine can call it. A token is the
  obvious next step before this is more than a local convenience.
- **Never touches Unity's cloud.** It drives the local editor and nothing else.

That last point is a deliberate boundary. Unity's Terms of Service, updated 2026-06-30, added
§17.2(ff), which names MCP clients and servers among the automated callers that need *Authorized
Agentic Access* to reach Unity **offerings**. Unity staff have said informally that this targets
connections to Unity's cloud platform, servers, Asset Store and public APIs rather than local
editor automation — but the ToS text does not say so. Keeping this transport strictly local is how
the design stays on the side that was verbally endorsed. Anything cloud-facing is deliberately
absent, not merely unimplemented.

## Implementation notes

`initialize`, `tools/list`, `tools/call` and `ping` over JSON-RPC on loopback HTTP. The client's
requested protocol version is echoed back.

Not built on the official MCP C# SDK: it would add a dependency tree to a package that currently has
none, in exchange for protocol surface this does not use. The envelope is read by a small scanner
(`McpJson`) that extracts raw values without interpreting them — enough to pass a tool call's
arguments through verbatim and to echo an id back in whatever form it arrived.

Not implemented: SSE streaming, resources, prompts, and a first-connection approval prompt. The
environment variable is the approval gate for now.

## Verified

On Unity 2022.3.62f3, driven as a client would over JSON-RPC: handshake echoes the requested
protocol version; `tools/list` returns eight tools with schemas generated from live metadata;
`unity_eval` with `{"code":"return 6*7;"}` returns `{"result":42}`; the escape hatch runs
`Scene.GetActive`; an unknown tool is a protocol error; opening a missing scene comes back as
`isError` with the reason. Unit tests cover the scanner, the surface and the transport
(`McpTests.cs`).
