using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UniCli.Protocol;
using UniCli.Server.Editor;
using UniCli.Server.Editor.Handlers;

// UniPipe bridge prototype: expose the official com.unity.pipeline command surface
// (143 commands incl. hot reload) through UniCli as a single facade:
//   unicli exec Pipeline.Exec '{"command":"editor_status"}'
//   unicli exec Pipeline.Exec '{"command":"reload_file","args":"{\"filename\":\"Assets/X.cs\"}"}'
// Forwards over loopback HTTP with the bearer token from the pipeline port file, so both
// packages stay unmodified — the bridge itself is the only new (MIT-able) code.
//
// Dirty-scene guard: pipeline's open_scene/create_scene check Play Mode only and silently
// discard unsaved changes when they replace the open scenes (verified: the marker object was
// gone). UniCli's own Scene.* commands refuse that by default and demand an explicit
// dirtyAction. Forwarding raw would let a caller lose work through a door they believe is
// guarded, so the bridge re-imposes UniCli's contract on the commands that can replace scenes.
public sealed class PipelineExecHandler : CommandHandler<PipelineExecRequest, PipelineExecResponse>
{
    public override string CommandName => "Pipeline.Exec";
    public override string Description => "Forward a command to the com.unity.pipeline HTTP server (bridge facade)";

    static readonly HttpClient s_Client = new HttpClient();

    protected override async ValueTask<PipelineExecResponse> ExecuteAsync(PipelineExecRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.command))
            throw new CommandFailedException("'command' is required", null);

        var portFile = Path.Combine(Directory.GetCurrentDirectory(), "Library/Pipeline/.unity-pipeline-port");
        if (!File.Exists(portFile))
            throw new CommandFailedException("Pipeline server not running (no port file at Library/Pipeline/.unity-pipeline-port)", null);

        GuardDirtyScenes(request);

        var desc = JObject.Parse(File.ReadAllText(portFile));
        var port = desc.Value<int>("port");
        var token = desc.Value<string>("evalToken");

        var body = new JObject { ["command"] = request.command };
        if (!string.IsNullOrEmpty(request.args))
            body["parameters"] = JObject.Parse(request.args);
        if (request.timeoutMs > 0)
            body["timeout"] = request.timeoutMs;

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/exec");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

        // The await yields the main thread back to EditorApplication.update, which pumps the
        // pipeline server's own main-thread dispatcher — the two servers compose without deadlock.
        var httpResponse = await s_Client.SendAsync(msg, cancellationToken);
        var text = await httpResponse.Content.ReadAsStringAsync();
        if (!httpResponse.IsSuccessStatusCode)
            throw new CommandFailedException($"pipeline server returned {(int)httpResponse.StatusCode}: {text}", null);

        return new PipelineExecResponse { resultJson = text };
    }

    // Pipeline commands that replace the open scenes when additive is false.
    static readonly string[] k_SceneReplacingCommands = { "open_scene", "create_scene" };

    static void GuardDirtyScenes(PipelineExecRequest request)
    {
        if (Array.IndexOf(k_SceneReplacingCommands, request.command) < 0)
            return;

        // Additive keeps the open scenes, so nothing can be discarded.
        if (!string.IsNullOrEmpty(request.args) && JObject.Parse(request.args).Value<bool?>("additive") == true)
            return;

        var dirty = new System.Collections.Generic.List<UnityEngine.SceneManagement.Scene>();
        for (var i = 0; i < EditorSceneManager.sceneCount; i++)
        {
            var scene = EditorSceneManager.GetSceneAt(i);
            if (scene.isDirty)
                dirty.Add(scene);
        }

        if (dirty.Count == 0)
            return;

        var action = string.IsNullOrEmpty(request.dirtyAction) ? "error" : request.dirtyAction.ToLowerInvariant();
        var names = string.Join(", ", dirty.ConvertAll(
            s => string.IsNullOrEmpty(s.path) ? $"'{(string.IsNullOrEmpty(s.name) ? "Untitled" : s.name)}' (unsaved, no path)" : s.path));

        switch (action)
        {
            case "discard":
                return;

            case "save":
                // An untitled scene has no path to save to, and SaveOpenScenes would raise a modal
                // save dialog that hangs a headless editor — same reason UniCli refuses it.
                foreach (var scene in dirty)
                {
                    if (string.IsNullOrEmpty(scene.path))
                        throw new CommandFailedException(
                            $"dirtyAction \"save\" cannot save '{(string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name)}' — it has never been saved, so there is no path to save to. " +
                            "Save it once with a path, or use dirtyAction \"discard\".", null);
                }
                if (!EditorSceneManager.SaveOpenScenes())
                    throw new CommandFailedException("Failed to save open scenes before forwarding to the pipeline server.", null);
                return;

            case "error":
                throw new CommandFailedException(
                    $"'{request.command}' would replace the open scenes, discarding unsaved changes in: {names}. " +
                    "Pipeline does not check this, so the bridge does. Pass dirtyAction \"save\" or \"discard\", " +
                    "or save the scene first.", null);

            default:
                throw new CommandFailedException(
                    $"Invalid dirtyAction \"{request.dirtyAction}\". Valid values: \"error\" (default), \"save\", \"discard\".", null);
        }
    }

    protected override bool TryWriteFormatted(PipelineExecResponse response, bool success, IFormatWriter writer)
    {
        writer.WriteLine(success ? response.resultJson : "Pipeline.Exec failed");
        return true;
    }
}

[Serializable]
public class PipelineExecRequest
{
    public string command;
    public string args;      // JSON object string with the pipeline command's parameters
    public int timeoutMs;
    public string dirtyAction;   // "error" (default) | "save" | "discard" — see GuardDirtyScenes
}

[Serializable]
public class PipelineExecResponse
{
    public string resultJson;
}
