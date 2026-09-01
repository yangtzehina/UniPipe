#if ENABLE_PROFILER
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace UniCli.Remote.Commands
{
    /// <summary>
    /// Keeps the rendering counters running so a query has something to read.
    ///
    /// A <see cref="ProfilerRecorder"/> collects from the moment it starts, so one created inside a
    /// request handler would answer its own first call with nothing. These start with the player
    /// instead, which costs a handful of counters running continuously — what any on-screen stats
    /// overlay does — in exchange for the first query being answerable.
    ///
    /// Every counter is optional. Platforms and render pipelines differ in which ones they emit,
    /// and a counter that is absent is reported by name rather than as a zero, because "not
    /// measured here" and "nothing drawn" are otherwise the same number.
    /// </summary>
    internal static class RenderRecorders
    {
        public static ProfilerRecorder Batches;
        public static ProfilerRecorder DrawCalls;
        public static ProfilerRecorder SetPassCalls;
        public static ProfilerRecorder Triangles;
        public static ProfilerRecorder Vertices;
        public static ProfilerRecorder DynamicBatches;
        public static ProfilerRecorder DynamicBatchedDrawCalls;
        public static ProfilerRecorder StaticBatches;
        public static ProfilerRecorder StaticBatchedDrawCalls;
        public static ProfilerRecorder InstancedBatches;
        public static ProfilerRecorder InstancedBatchedDrawCalls;
        public static ProfilerRecorder ShadowCasters;
        public static ProfilerRecorder VisibleSkinnedMeshes;
        public static ProfilerRecorder RenderTextures;
        public static ProfilerRecorder RenderTextureChanges;

        private static readonly List<string> s_Unavailable = new List<string>();
        private static bool s_Started;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        internal static void Start()
        {
            if (s_Started)
                return;

            s_Started = true;

            Batches = Open("Batches Count");
            DrawCalls = Open("Draw Calls Count");
            SetPassCalls = Open("SetPass Calls Count");
            Triangles = Open("Triangles Count");
            Vertices = Open("Vertices Count");
            DynamicBatches = Open("Dynamic Batches Count");
            DynamicBatchedDrawCalls = Open("Dynamic Batched Draw Calls Count");
            StaticBatches = Open("Static Batches Count");
            StaticBatchedDrawCalls = Open("Static Batched Draw Calls Count");
            InstancedBatches = Open("Instanced Batches Count");
            InstancedBatchedDrawCalls = Open("Instanced Batched Draw Calls Count");
            ShadowCasters = Open("Shadow Casters Count");
            VisibleSkinnedMeshes = Open("Visible Skinned Meshes Count");
            RenderTextures = Open("Render Textures Count");
            RenderTextureChanges = Open("Render Textures Changes Count");
        }

        private static ProfilerRecorder Open(string counterName)
        {
            var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, counterName);

            if (!recorder.Valid)
                s_Unavailable.Add(counterName);

            return recorder;
        }

        /// <summary>True once at least one counter has collected a frame.</summary>
        public static bool HasSamples
        {
            get
            {
                Start();
                return Batches.Valid && Batches.Count > 0;
            }
        }

        public static long Read(ProfilerRecorder recorder)
        {
            Start();
            return recorder.Valid && recorder.Count > 0 ? recorder.LastValue : 0;
        }

        public static string[] UnavailableCounters()
        {
            Start();
            return s_Unavailable.ToArray();
        }
    }
}
#endif
