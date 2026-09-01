using System;
using UnityEngine;
using UnityEngine.Scripting;

#if ENABLE_PROFILER
using Unity.Profiling;
#endif

namespace UniCli.Remote.Commands
{
    /// <summary>
    /// What the player actually drew last frame, including which batching path handled it.
    ///
    /// This is the counterpart to the editor's <c>Render.GetStats</c>, and it exists because the
    /// editor cannot answer this question where it matters. Batch mode runs no per-frame display
    /// render, so every render counter there reads zero; a running player renders every frame, so
    /// this is the only place a rendering regression can be measured automatically.
    ///
    /// Reads Unity's own profiler counters rather than anything editor-side, so the numbers are the
    /// player's, on the player's hardware, at the player's resolution.
    /// </summary>
    [Preserve]
    public sealed class RenderStatsCommand : DebugCommand<Unit, RenderStatsCommand.Response>
    {
        public override string CommandName => "Debug.RenderStats";

        public override string Description =>
            "Rendering counters for the last frame, with the dynamic/static/instanced batching breakdown";

        protected override Response ExecuteCommand(Unit request)
        {
#if ENABLE_PROFILER
            var response = new Response
            {
                available = true,
                sampled = RenderRecorders.HasSamples,
                resolutionWidth = Screen.width,
                resolutionHeight = Screen.height,
                frameCount = Time.frameCount,
                batches = RenderRecorders.Read(RenderRecorders.Batches),
                drawCalls = RenderRecorders.Read(RenderRecorders.DrawCalls),
                setPassCalls = RenderRecorders.Read(RenderRecorders.SetPassCalls),
                triangles = RenderRecorders.Read(RenderRecorders.Triangles),
                vertices = RenderRecorders.Read(RenderRecorders.Vertices),
                dynamicBatches = RenderRecorders.Read(RenderRecorders.DynamicBatches),
                dynamicBatchedDrawCalls = RenderRecorders.Read(RenderRecorders.DynamicBatchedDrawCalls),
                staticBatches = RenderRecorders.Read(RenderRecorders.StaticBatches),
                staticBatchedDrawCalls = RenderRecorders.Read(RenderRecorders.StaticBatchedDrawCalls),
                instancedBatches = RenderRecorders.Read(RenderRecorders.InstancedBatches),
                instancedBatchedDrawCalls = RenderRecorders.Read(RenderRecorders.InstancedBatchedDrawCalls),
                shadowCasters = RenderRecorders.Read(RenderRecorders.ShadowCasters),
                visibleSkinnedMeshes = RenderRecorders.Read(RenderRecorders.VisibleSkinnedMeshes),
                renderTextureCount = RenderRecorders.Read(RenderRecorders.RenderTextures),
                renderTextureChanges = RenderRecorders.Read(RenderRecorders.RenderTextureChanges),
                unavailableCounters = RenderRecorders.UnavailableCounters()
            };

            return response;
#else
            // The counters are compiled out of non-development builds. Saying so beats reporting
            // a row of zeros that reads like "nothing was drawn".
            return new Response
            {
                available = false,
                resolutionWidth = Screen.width,
                resolutionHeight = Screen.height,
                frameCount = Time.frameCount
            };
#endif
        }

        [Serializable]
        public class Response
        {
            /// <summary>False when the build has no profiler, so the counters do not exist at all.</summary>
            public bool available;

            /// <summary>
            /// False when the counters exist but no frame has been sampled yet. The values below
            /// are then not measurements.
            /// </summary>
            public bool sampled;

            public int resolutionWidth;
            public int resolutionHeight;
            public int frameCount;

            public long batches;
            public long drawCalls;
            public long setPassCalls;
            public long triangles;
            public long vertices;

            public long dynamicBatches;
            public long dynamicBatchedDrawCalls;
            public long staticBatches;
            public long staticBatchedDrawCalls;
            public long instancedBatches;
            public long instancedBatchedDrawCalls;

            public long shadowCasters;
            public long visibleSkinnedMeshes;
            public long renderTextureCount;
            public long renderTextureChanges;

            /// <summary>
            /// Counters this platform does not provide. Named rather than silently zeroed, because
            /// a zero that means "not measured here" and a zero that means "nothing drawn" are the
            /// same number.
            /// </summary>
            public string[] unavailableCounters;
        }
    }
}
