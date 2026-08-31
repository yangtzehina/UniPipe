using System;
using System.Collections.Generic;
using System.Text;
using UniCli.Protocol;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Decides which commands an MCP client sees, and describes them.
    ///
    /// There are 136 commands. Listing all of them costs an agent a large part of its context
    /// before it has done anything, and most of that is commands it will never call. Every tool
    /// surface we surveyed solves this somehow — a handful of tools, aggregated entry points, or a
    /// two-level lazy list — and none of the answers agree.
    ///
    /// The shape here: a short list of the commands an agent reaches for constantly, plus one
    /// escape hatch that can run any command by name. Nothing is unreachable, discovery is a tool
    /// call rather than a context tax, and the common loop needs no indirection.
    ///
    /// Command traits go into the descriptions, so a model can see that something is destructive,
    /// replaces open scenes, or needs a particular editor state *before* calling it rather than
    /// from the error afterwards.
    /// </summary>
    internal static class McpToolSurface
    {
        /// <summary>Prefix so these are distinguishable in a client wired to several servers.</summary>
        private const string Prefix = "unity_";

        /// <summary>The command run by the escape hatch tool.</summary>
        public const string RunCommandTool = Prefix + "run_command";

        private readonly struct CoreTool
        {
            public readonly string Tool;
            public readonly string Command;
            public readonly string Purpose;

            public CoreTool(string tool, string command, string purpose)
            {
                Tool = tool;
                Command = command;
                Purpose = purpose;
            }
        }

        // Chosen from what an editor-driving loop actually repeats: look at the state, change
        // something, compile, read the errors, look again.
        private static readonly CoreTool[] s_Core =
        {
            new(Prefix + "status", "Editor.Status", "Editor state: play mode, compiling, dirty scenes."),
            new(Prefix + "compile", "Compile", "Recompile scripts and report errors and warnings."),
            new(Prefix + "console", "Console.GetLog", "Read the Unity console."),
            new(Prefix + "eval", "Eval", "Run a C# snippet in the editor and return its value."),
            new(Prefix + "hierarchy", "GameObject.GetHierarchy", "The GameObject tree of the open scenes."),
            new(Prefix + "screenshot", "Screenshot.Capture", "Capture the Game view to a PNG."),
            new(Prefix + "list_commands", "Commands.List", "Every command this editor exposes, with its parameters."),
        };

        public static bool IsCoreTool(string toolName, out string commandName)
        {
            foreach (var tool in s_Core)
            {
                if (tool.Tool == toolName)
                {
                    commandName = tool.Command;
                    return true;
                }
            }

            commandName = null;
            return false;
        }

        /// <summary>
        /// The tools/list payload, built from the live command metadata so a project's own commands
        /// show up in the escape hatch's description without anything being hard-coded.
        /// </summary>
        public static string BuildToolList(CommandInfo[] commands)
        {
            var byName = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in commands ?? Array.Empty<CommandInfo>())
                byName[command.name] = command;

            var json = new StringBuilder();
            json.Append("{\"tools\":[");

            var first = true;
            foreach (var tool in s_Core)
            {
                if (!byName.TryGetValue(tool.Command, out var info))
                    continue;   // module disabled in this project; do not advertise it

                if (!first) json.Append(',');
                first = false;
                AppendTool(json, tool.Tool, DescribeCommand(info, tool.Purpose), BuildSchema(info));
            }

            if (!first) json.Append(',');
            AppendTool(json, RunCommandTool, DescribeEscapeHatch(commands), EscapeHatchSchema);
            json.Append("]}");
            return json.ToString();
        }

        private static void AppendTool(StringBuilder json, string name, string description, string schema)
        {
            json.Append("{\"name\":").Append(McpJson.Quote(name))
                .Append(",\"description\":").Append(McpJson.Quote(description))
                .Append(",\"inputSchema\":").Append(schema)
                .Append('}');
        }

        /// <summary>
        /// A command's description plus the traits worth knowing before calling it. This is where
        /// the declared preconditions earn their keep: the model is told a command replaces open
        /// scenes rather than discovering it from the result.
        /// </summary>
        internal static string DescribeCommand(CommandInfo info, string purpose = null)
        {
            var text = new StringBuilder();
            text.Append(purpose ?? info.description ?? info.name);

            var notes = new List<string>();
            if (!string.IsNullOrEmpty(info.requiresEditorState))
            {
                notes.Add(info.requiresEditorState == "NotPlaying"
                    ? "requires the editor to be out of Play Mode"
                    : info.requiresEditorState == "NotCompiling"
                        ? "requires compilation to have finished"
                        : "requires the editor to be out of Play Mode and not compiling");
            }

            if (info.replacesOpenScenes)
                notes.Add("can replace the open scenes and discard unsaved changes");
            if (info.destructive)
                notes.Add("makes changes that are not trivially undoable");
            if (info.singleUndoStep)
                notes.Add("its edits collapse into one undo step");

            if (notes.Count > 0)
                text.Append(" (").Append(string.Join("; ", notes.ToArray())).Append(')');

            return text.ToString();
        }

        private static string DescribeEscapeHatch(CommandInfo[] commands)
        {
            var count = commands?.Length ?? 0;
            return $"Run any of this editor's {count} commands by name. Use {Prefix}list_commands to " +
                   "see the names and their parameters. The tools above are shortcuts for the most " +
                   "common of these; anything else goes through here.";
        }

        private const string EscapeHatchSchema =
            "{\"type\":\"object\"," +
            "\"properties\":{" +
            "\"command\":{\"type\":\"string\",\"description\":\"Command name, e.g. Scene.Open\"}," +
            "\"arguments\":{\"type\":\"object\",\"description\":\"The command's parameters as an object\"}}," +
            "\"required\":[\"command\"]}";

        /// <summary>
        /// A JSON Schema for a command's request fields. Unity's serializer restricts these to a
        /// small closed set of types, so the mapping is total rather than best-effort.
        /// </summary>
        internal static string BuildSchema(CommandInfo info)
        {
            var fields = info?.requestFields;
            if (fields == null || fields.Length == 0)
                return "{\"type\":\"object\",\"properties\":{}}";

            var json = new StringBuilder("{\"type\":\"object\",\"properties\":{");
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(McpJson.Quote(fields[i].name)).Append(':').Append(SchemaForType(fields[i].type));
            }

            json.Append("}}");
            return json.ToString();
        }

        private static string SchemaForType(string type)
        {
            switch (type)
            {
                case "string": return "{\"type\":\"string\"}";
                case "bool": return "{\"type\":\"boolean\"}";
                case "int":
                case "Int64": return "{\"type\":\"integer\"}";
                case "float":
                case "Single": return "{\"type\":\"number\"}";
                case "string[]": return "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}";
                case "Single[]":
                case "float[]": return "{\"type\":\"array\",\"items\":{\"type\":\"number\"}}";
                case "int[]": return "{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}";
                default:
                    // Structured parameters (ColorValue and the like) arrive as objects; leaving the
                    // shape open is better than describing it wrongly.
                    return "{\"type\":\"object\"}";
            }
        }
    }
}
