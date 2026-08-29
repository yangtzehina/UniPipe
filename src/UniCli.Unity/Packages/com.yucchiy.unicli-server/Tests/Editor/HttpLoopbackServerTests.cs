using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UniCli.Protocol;
using UnityEngine;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// The HTTP transport exists to prove the seam: a request that arrives over HTTP reaches the
    /// same handler the named pipe feeds. These stand a server up for real — HttpListener works in
    /// the editor — and round-trip a request through a stub handler.
    /// </summary>
    [TestFixture]
    public class HttpLoopbackServerTests
    {
        private HttpLoopbackServer _server;
        private HttpClient _client;
        private CommandRequest _received;

        [SetUp]
        public void SetUp()
        {
            _received = null;
            _server = new HttpLoopbackServer(
                (request, _, respond) =>
                {
                    _received = request;
                    respond(new CommandResponse { success = true, message = "handled", data = "" });
                },
                logger: null);
            _client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _server?.Dispose();
        }

        // Unity's test framework (1.1.x) does not run async Task tests, so these block on the
        // local round trip. The server runs on its own threads, so blocking the test thread is safe.
        private HttpResponseMessage Post(string body)
            => _client.PostAsync($"http://127.0.0.1:{_server.Port}/",
                new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

        [Test]
        public void Post_ReachesTheHandler_AndReturnsItsResponse()
        {
            var http = Post("{\"command\":\"Editor.Status\"}");
            var body = http.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = JsonUtility.FromJson<CommandResponse>(body);

            Assert.That((int)http.StatusCode, Is.EqualTo(200));
            Assert.That(_received, Is.Not.Null, "the transport must hand the request to the shared handler");
            Assert.That(_received.command, Is.EqualTo("Editor.Status"));
            Assert.That(response.success, Is.True);
            Assert.That(response.message, Is.EqualTo("handled"),
                "the HTTP response is the command's actual result, not an acknowledgement");
        }

        [Test]
        public void Get_IsRejected_BecauseCommandsArePosted()
        {
            var http = _client.GetAsync($"http://127.0.0.1:{_server.Port}/").GetAwaiter().GetResult();

            Assert.That((int)http.StatusCode, Is.EqualTo(405));
            Assert.That(_received, Is.Null, "a rejected request must not reach the handler");
        }

        [Test]
        public void EmptyBody_IsRejectedWithoutReachingTheHandler()
        {
            var http = Post("not a command");

            Assert.That((int)http.StatusCode, Is.EqualTo(400));
            Assert.That(_received, Is.Null);
        }

        [Test]
        public void PortFile_IsWrittenAndRemovedOnDispose()
        {
            var portFile = System.IO.Path.Combine("Library", "UniCli", "http-port");

            Assert.That(System.IO.File.Exists(portFile), Is.True, "a client finds the port here");
            Assert.That(System.IO.File.ReadAllText(portFile).Trim(), Is.EqualTo(_server.Port.ToString()));

            _server.Dispose();
            _server = null;

            Assert.That(System.IO.File.Exists(portFile), Is.False, "a stopped server leaves no stale port");
        }

        [Test]
        public void WaitForShutdown_CompletesWhenDisposed()
        {
            var shutdown = _server.WaitForShutdownAsync(CancellationToken.None);
            Assert.That(shutdown.IsCompleted, Is.False);

            _server.Dispose();
            _server = null;

            Task.WhenAny(shutdown, Task.Delay(2000)).GetAwaiter().GetResult();
            Assert.That(shutdown.IsCompleted, Is.True, "dispose must release anyone waiting on shutdown");
        }
    }
}
