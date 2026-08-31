using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UniCli.Protocol;
using UniCli.Server.Editor;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// The MCP envelope reader. The package has no JSON dependency, so these guard the small
    /// scanner that stands in for one — particularly that it does not confuse a nested property
    /// with a top-level one, which would let a tool call's own arguments hijack the call.
    /// </summary>
    [TestFixture]
    public class McpJsonTests
    {
        [Test]
        public void ExtractString_ReadsATopLevelProperty()
        {
            Assert.That(McpJson.ExtractString("{\"method\":\"tools/list\"}", "method"), Is.EqualTo("tools/list"));
        }

        [Test]
        public void ExtractRaw_ReturnsAnObjectUntouched()
        {
            var raw = McpJson.ExtractRaw("{\"params\":{\"a\":1,\"b\":[2,3]}}", "params");

            Assert.That(raw, Is.EqualTo("{\"a\":1,\"b\":[2,3]}"),
                "arguments are passed through to the command as-is, so they must survive verbatim");
        }

        [Test]
        public void ExtractRaw_DoesNotMatchANestedPropertyOfTheSameName()
        {
            // params.name is the tool; arguments.name is a value the caller chose. Confusing them
            // would run a different tool than the one asked for.
            var body = "{\"params\":{\"name\":\"unity_run_command\",\"arguments\":{\"name\":\"decoy\"}}}";
            var parameters = McpJson.ExtractRaw(body, "params");

            Assert.That(McpJson.ExtractString(parameters, "name"), Is.EqualTo("unity_run_command"));
        }

        [Test]
        public void ExtractRaw_SkipsBracesInsideStrings()
        {
            var body = "{\"code\":\"var x = new [] { 1 };\",\"id\":7}";

            Assert.That(McpJson.ExtractRaw(body, "id"), Is.EqualTo("7"));
        }

        [Test]
        public void ExtractRaw_KeepsAnIdInWhateverFormItArrived()
        {
            // JSON-RPC ids may be numbers or strings and must be echoed back unchanged.
            Assert.That(McpJson.ExtractRaw("{\"id\":42}", "id"), Is.EqualTo("42"));
            Assert.That(McpJson.ExtractRaw("{\"id\":\"abc\"}", "id"), Is.EqualTo("\"abc\""));
        }

        [Test]
        public void ExtractRaw_MissingProperty_IsNull()
        {
            // How a notification is recognised: no id, so no reply.
            Assert.That(McpJson.ExtractRaw("{\"method\":\"notifications/initialized\"}", "id"), Is.Null);
        }

        [Test]
        public void ExtractString_UnescapesEscapes()
        {
            Assert.That(McpJson.ExtractString("{\"s\":\"a\\nb\\\"c\"}", "s"), Is.EqualTo("a\nb\"c"));
        }

        [Test]
        public void Escape_ProducesValidJsonText()
        {
            Assert.That(McpJson.Escape("a\"b\\c\nd"), Is.EqualTo("a\\\"b\\\\c\\nd"));
        }

        [Test]
        public void Escape_EscapesControlCharacters()
        {
            Assert.That(McpJson.Escape(""), Is.EqualTo("\\u0001"));
        }

        [Test]
        public void Malformed_YieldsNullRatherThanThrowing()
        {
            Assert.That(McpJson.ExtractRaw("{\"a\":", "a"), Is.Null);
            Assert.That(McpJson.ExtractRaw("not json", "a"), Is.Null);
            Assert.That(McpJson.ExtractRaw(null, "a"), Is.Null);
        }
    }

    /// <summary>
    /// What an MCP client is shown. The point of the surface is that 136 commands do not become
    /// 136 tools — that would spend a large part of an agent's context before it acts.
    /// </summary>
    [TestFixture]
    public class McpToolSurfaceTests
    {
        private static CommandInfo Command(string name, params CommandFieldInfo[] fields)
            => new() { name = name, description = name + " does something", requestFields = fields };

        private static CommandFieldInfo Field(string name, string type)
            => new() { name = name, type = type };

        [Test]
        public void ToolList_IsShortAndAlwaysHasTheEscapeHatch()
        {
            var json = McpToolSurface.BuildToolList(new[]
            {
                Command("Editor.Status"), Command("Compile"), Command("Console.GetLog"),
                Command("Eval"), Command("GameObject.GetHierarchy"), Command("Screenshot.Capture"),
                Command("Commands.List"), Command("Scene.Open"), Command("Prefab.Save"),
            });

            Assert.That(json, Does.Contain("unity_status").And.Contain(McpToolSurface.RunCommandTool));
            Assert.That(json, Does.Not.Contain("Prefab.Save"),
                "commands outside the core set are reached through the escape hatch, not listed");
        }

        [Test]
        public void ToolList_OmitsCoreToolsWhoseCommandIsUnavailable()
        {
            // Modules can be disabled per project; advertising a tool that cannot run is worse
            // than not offering it.
            var json = McpToolSurface.BuildToolList(new[] { Command("Editor.Status") });

            Assert.That(json, Does.Contain("unity_status"));
            Assert.That(json, Does.Not.Contain("unity_screenshot"));
        }

        [Test]
        public void ToolList_SurvivesAnEmptyCommandSet()
        {
            var json = McpToolSurface.BuildToolList(new CommandInfo[0]);

            Assert.That(json, Does.Contain(McpToolSurface.RunCommandTool));
        }

        [Test]
        public void IsCoreTool_MapsToolNamesToCommands()
        {
            Assert.That(McpToolSurface.IsCoreTool("unity_eval", out var command), Is.True);
            Assert.That(command, Is.EqualTo("Eval"));
            Assert.That(McpToolSurface.IsCoreTool("unity_nope", out _), Is.False);
        }

        [TestCase("string", "\"type\":\"string\"")]
        [TestCase("bool", "\"type\":\"boolean\"")]
        [TestCase("int", "\"type\":\"integer\"")]
        [TestCase("Int64", "\"type\":\"integer\"")]
        [TestCase("float", "\"type\":\"number\"")]
        [TestCase("string[]", "\"type\":\"array\"")]
        public void Schema_MapsUnitySerializableTypes(string fieldType, string expected)
        {
            var schema = McpToolSurface.BuildSchema(Command("X", Field("f", fieldType)));

            Assert.That(schema, Does.Contain(expected));
        }

        [Test]
        public void Schema_ForACommandWithNoParameters_IsAnEmptyObject()
        {
            Assert.That(McpToolSurface.BuildSchema(Command("X")),
                Is.EqualTo("{\"type\":\"object\",\"properties\":{}}"));
        }

        [Test]
        public void Description_CarriesTheTraitsAModelShouldKnowBeforeCalling()
        {
            var info = new CommandInfo
            {
                name = "Scene.Open",
                description = "Open a scene",
                requiresEditorState = "NotPlaying",
                replacesOpenScenes = true,
            };

            var description = McpToolSurface.DescribeCommand(info);

            Assert.That(description, Does.Contain("Play Mode"));
            Assert.That(description, Does.Contain("discard unsaved changes"),
                "the model should learn this from the description, not from losing work");
        }

        [Test]
        public void Description_OfAnUndemandingCommand_HasNoNoise()
        {
            var description = McpToolSurface.DescribeCommand(Command("Scene.List"));

            Assert.That(description, Does.Not.Contain("("));
        }
    }

    /// <summary>
    /// End to end over the wire: a request arriving as JSON-RPC reaches the same handler the
    /// named pipe feeds, and failures are reported the way MCP expects.
    /// </summary>
    [TestFixture]
    public class McpTransportTests
    {
        private McpTransport _transport;
        private CommandRequest _received;
        private CommandResponse _reply;

        [SetUp]
        public void SetUp()
        {
            _received = null;
            _reply = new CommandResponse { success = true, message = "ok", data = "{\"v\":1}" };
            _transport = new McpTransport(
                (request, _, respond) =>
                {
                    _received = request;
                    respond(request.command == "Commands.List"
                        ? new CommandResponse
                        {
                            success = true,
                            data = "{\"commands\":[{\"name\":\"Editor.Status\",\"description\":\"state\"}]}"
                        }
                        : _reply);
                },
                logger: null);
        }

        [TearDown]
        public void TearDown() => _transport?.Dispose();

        private string Rpc(string body) => _transport.HandleRpcAsync(body).GetAwaiter().GetResult();

        [Test]
        public void Initialize_EchoesTheClientsProtocolVersion()
        {
            var response = Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"," +
                               "\"params\":{\"protocolVersion\":\"2025-06-18\"}}");

            Assert.That(response, Does.Contain("\"protocolVersion\":\"2025-06-18\""));
            Assert.That(response, Does.Contain("\"tools\""));
        }

        [Test]
        public void Notification_GetsNoReply()
        {
            Assert.That(Rpc("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"), Is.Null);
        }

        [Test]
        public void ToolsCall_ReachesTheSharedHandler()
        {
            var response = Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\"," +
                               "\"params\":{\"name\":\"unity_status\",\"arguments\":{}}}");

            Assert.That(_received, Is.Not.Null);
            Assert.That(_received.command, Is.EqualTo("Editor.Status"));
            Assert.That(response, Does.Contain("\"isError\":false"));
        }

        [Test]
        public void ToolsCall_PassesArgumentsThroughVerbatim()
        {
            Rpc("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\"," +
                "\"params\":{\"name\":\"unity_eval\",\"arguments\":{\"code\":\"return 1;\"}}}");

            Assert.That(_received.command, Is.EqualTo("Eval"));
            Assert.That(_received.data, Is.EqualTo("{\"code\":\"return 1;\"}"));
        }

        [Test]
        public void EscapeHatch_RunsAnyCommandByName()
        {
            Rpc("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"" +
                McpToolSurface.RunCommandTool +
                "\",\"arguments\":{\"command\":\"Prefab.Save\",\"arguments\":{\"path\":\"x\"}}}}");

            Assert.That(_received.command, Is.EqualTo("Prefab.Save"));
            Assert.That(_received.data, Is.EqualTo("{\"path\":\"x\"}"));
        }

        [Test]
        public void UnknownTool_IsAProtocolError()
        {
            var response = Rpc("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\"," +
                               "\"params\":{\"name\":\"unity_nope\",\"arguments\":{}}}");

            Assert.That(response, Does.Contain("\"error\""));
            Assert.That(_received, Is.Null, "a call that names no tool must not reach the editor");
        }

        [Test]
        public void FailedCommand_IsAToolResultNotAProtocolError()
        {
            // The call was well-formed and did reach the editor; the model needs to see why it
            // was refused, which an error envelope would hide.
            _reply = new CommandResponse { success = false, message = "Cannot execute while in Play Mode." };

            var response = Rpc("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\"," +
                               "\"params\":{\"name\":\"unity_status\",\"arguments\":{}}}");

            Assert.That(response, Does.Contain("\"isError\":true"));
            Assert.That(response, Does.Not.Contain("\"error\""));
            Assert.That(response, Does.Contain("Play Mode"));
        }

        [Test]
        public void UnknownMethod_IsMethodNotFound()
        {
            Assert.That(Rpc("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"resources/list\"}"),
                Does.Contain("-32601"));
        }

        [Test]
        public void ToolsList_IsBuiltFromTheLiveCommandMetadata()
        {
            var response = Rpc("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/list\"}");

            Assert.That(_received.command, Is.EqualTo("Commands.List"),
                "the surface describes itself from the same metadata clients query");
            Assert.That(response, Does.Contain("unity_status"));
        }

        [Test]
        public void WaitForShutdown_CompletesOnDispose()
        {
            var shutdown = _transport.WaitForShutdownAsync(CancellationToken.None);
            _transport.Dispose();
            _transport = null;

            Task.WhenAny(shutdown, Task.Delay(2000)).GetAwaiter().GetResult();
            Assert.That(shutdown.IsCompleted, Is.True);
        }
    }
}
