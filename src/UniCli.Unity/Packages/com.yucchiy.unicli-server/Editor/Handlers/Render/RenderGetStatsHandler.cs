using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;

namespace UniCli.Server.Editor.Handlers
{
    /// <summary>
    /// The numbers behind the Game View's Statistics overlay: batches, SetPass calls, and the
    /// breakdown of what batched and how.
    ///
    /// The breakdown is the point. "Draw calls went up" is a symptom; dynamic versus static versus
    /// instanced batch counts are what tells you which batching path stopped working — the question
    /// a UI or rendering change actually raises.
    ///
    /// Reads <see cref="UnityStats"/>, which is public API and reflects the most recent Game View
    /// render. That last part is the trap this handler exists to close: the values persist after
    /// the render that produced them, so returning them blind means reporting an old frame as if it
    /// were this one. So it asks for a repaint, lets the editor render, and reports the resolution
    /// the numbers were measured at.
    /// </summary>
    [Module("Render")]
    // Measured: under -batchmode every one of these reads zero and screenRes is 0x0, because batch
    // mode runs no per-frame display render at all -- there is nothing to measure rather than
    // something being hidden. The gate turns that into a refusal instead of a page of zeros.
    [CommandPrecondition(
        Environment = EnvironmentRequirement.Graphics | EnvironmentRequirement.InteractiveWindows,
        Cancellable = true)]
    public sealed class RenderGetStatsHandler : CommandHandler<RenderGetStatsRequest, RenderGetStatsResponse>
    {
        public override string CommandName => "Render.GetStats";

        public override string Description =>
            "Game View render statistics: batches, SetPass calls, and the batching breakdown";

        /// <summary>
        /// Editor updates to let pass after asking for a repaint. The render happens on the
        /// editor's own schedule, so this waits rather than assuming; two ticks was enough in
        /// every case measured, and the default leaves margin.
        /// </summary>
        private const int DefaultSettleTicks = 4;

        protected override bool TryWriteFormatted(RenderGetStatsResponse response, bool success, IFormatWriter writer)
        {
            if (!success)
                return false;

            if (!response.rendered)
            {
                writer.WriteLine("No frame has been rendered, so there are no statistics to report.");
                writer.WriteLine("  Open a Game View; the numbers describe what it last drew.");
                return true;
            }

            writer.WriteLine($"Resolution:  {response.resolution}");
            writer.WriteLine($"Batches:     {response.batches}");
            writer.WriteLine($"SetPass:     {response.setPassCalls}");
            writer.WriteLine($"Draw calls:  {response.drawCalls}");
            writer.WriteLine($"Triangles:   {response.triangles:N0}");
            writer.WriteLine($"Vertices:    {response.vertices:N0}");
            writer.WriteLine("");
            writer.WriteLine("Batching:");
            writer.WriteLine($"  Dynamic:   {response.dynamicBatches} batches from {response.dynamicBatchedDrawCalls} draw calls");
            writer.WriteLine($"  Static:    {response.staticBatches} batches from {response.staticBatchedDrawCalls} draw calls");
            writer.WriteLine($"  Instanced: {response.instancedBatches} batches from {response.instancedBatchedDrawCalls} draw calls");
            writer.WriteLine("");
            writer.WriteLine($"Shadow casters:  {response.shadowCasters}");
            writer.WriteLine($"Render textures: {response.renderTextureCount} ({FormatBytes(response.renderTextureBytes)}), {response.renderTextureChanges} changes");
            writer.WriteLine($"Frame time:      {response.frameTimeMs:F2} ms (render {response.renderTimeMs:F2} ms)");

            return true;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        protected override async ValueTask<RenderGetStatsResponse> ExecuteAsync(
            RenderGetStatsRequest request, CancellationToken cancellationToken)
        {
            if (request.repaint)
            {
                InternalEditorUtility.RepaintAllViews();

                var ticks = request.settleTicks > 0 ? request.settleTicks : DefaultSettleTicks;
                await EditorTicks.WaitAsync(ticks, cancellationToken);
            }

            return Read();
        }

        /// <summary>
        /// Whether the reported resolution describes an actual render. "0x0" is how the editor
        /// says nothing has been drawn; reporting the zeros underneath it as measurements would be
        /// the read-command version of lying.
        /// </summary>
        internal static bool IsRendered(string resolution)
        {
            if (string.IsNullOrEmpty(resolution))
                return false;

            var separator = resolution.IndexOf('x');
            if (separator <= 0)
                return false;

            return int.TryParse(resolution.Substring(0, separator), out var width)
                   && int.TryParse(resolution.Substring(separator + 1), out var height)
                   && width > 0 && height > 0;
        }

        internal static RenderGetStatsResponse Read()
        {
            var resolution = UnityStats.screenRes;

            return new RenderGetStatsResponse
            {
                rendered = IsRendered(resolution),
                resolution = resolution,
                batches = UnityStats.batches,
                drawCalls = UnityStats.drawCalls,
                setPassCalls = UnityStats.setPassCalls,
                triangles = UnityStats.triangles,
                vertices = UnityStats.vertices,
                dynamicBatches = UnityStats.dynamicBatches,
                dynamicBatchedDrawCalls = UnityStats.dynamicBatchedDrawCalls,
                staticBatches = UnityStats.staticBatches,
                staticBatchedDrawCalls = UnityStats.staticBatchedDrawCalls,
                instancedBatches = UnityStats.instancedBatches,
                instancedBatchedDrawCalls = UnityStats.instancedBatchedDrawCalls,
                shadowCasters = UnityStats.shadowCasters,
                renderTextureCount = UnityStats.renderTextureCount,
                renderTextureBytes = UnityStats.renderTextureBytes,
                renderTextureChanges = UnityStats.renderTextureChanges,
                frameTimeMs = UnityStats.frameTime * 1000f,
                renderTimeMs = UnityStats.renderTime * 1000f
            };
        }
    }

    [Serializable]
    public class RenderGetStatsRequest
    {
        /// <summary>
        /// Ask the editor to redraw before reading, so the numbers describe a render that just
        /// happened rather than whenever the Game View last drew. On by default; turn it off to
        /// read whatever is already there without disturbing the editor.
        /// </summary>
        public bool repaint = true;

        /// <summary>Editor updates to let pass after requesting the repaint. 0 uses the default.</summary>
        public int settleTicks;
    }

    [Serializable]
    public class RenderGetStatsResponse
    {
        /// <summary>False when nothing has been drawn; the counts below are then meaningless.</summary>
        public bool rendered;

        /// <summary>
        /// What the numbers were measured at. Batch counts depend on what is on screen, so a
        /// comparison across two resolutions is not a comparison.
        /// </summary>
        public string resolution;

        public int batches;
        public int drawCalls;
        public int setPassCalls;
        public int triangles;
        public int vertices;

        public int dynamicBatches;
        public int dynamicBatchedDrawCalls;
        public int staticBatches;
        public int staticBatchedDrawCalls;
        public int instancedBatches;
        public int instancedBatchedDrawCalls;

        public int shadowCasters;
        public int renderTextureCount;
        public long renderTextureBytes;
        public int renderTextureChanges;

        public float frameTimeMs;
        public float renderTimeMs;
    }
}
