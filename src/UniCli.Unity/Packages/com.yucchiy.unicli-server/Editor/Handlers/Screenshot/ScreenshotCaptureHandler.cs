using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UniCli.Server.Editor.Handlers
{
    internal enum ScreenshotCaptureMode
    {
        Auto,
        Edit,
        Play,
    }

    // Measured on 2022.3.62f3: under -batchmode -nographics this crashes the editor natively in
    // MonoGUIView::IsHDRActive(); under plain -batchmode it returns success and a fully
    // transparent frame. Scene.Screenshot3D renders a camera directly and produces a real image in
    // batch mode, so the refusal names it.
    [CommandPrecondition(
        Environment = EnvironmentRequirement.Graphics | EnvironmentRequirement.InteractiveWindows,
        AlternativeCommand = "Scene.Screenshot3D")]
    public sealed class ScreenshotCaptureHandler : CommandHandler<ScreenshotCaptureRequest, ScreenshotCaptureResponse>
    {
        public override string CommandName => "Screenshot.Capture";
        public override string Description => "Capture a screenshot of the Game View and save as PNG (works in both Edit Mode and Play Mode)";

        protected override bool TryWriteFormatted(ScreenshotCaptureResponse response, bool success, IFormatWriter writer)
        {
            if (success)
            {
                writer.WriteLine($"Screenshot saved to: {response.path}");
                writer.WriteLine($"  Mode: {response.mode}");
                writer.WriteLine($"  Resolution: {response.width}x{response.height}");
                writer.WriteLine($"  Size: {FormatBytes(response.size)}");
            }
            else
            {
                writer.WriteLine("Failed to capture screenshot");
            }
            return true;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        protected override async ValueTask<ScreenshotCaptureResponse> ExecuteAsync(ScreenshotCaptureRequest request, CancellationToken cancellationToken)
        {
            var requestedMode = ParseMode(request.mode);
            var isPlaying = EditorApplication.isPlaying;

            if (requestedMode == ScreenshotCaptureMode.Edit && isPlaying)
                throw new InvalidOperationException(
                    $"'{CommandName}' with mode \"edit\" requires Edit Mode, but the editor is in Play Mode. Use PlayMode.Exit first, or omit mode to capture the current mode.");
            if (requestedMode == ScreenshotCaptureMode.Play && !isPlaying)
                throw new InvalidOperationException(
                    $"'{CommandName}' with mode \"play\" requires Play Mode. Use PlayMode.Enter first, or omit mode to capture the current mode.");

            var superSize = request.superSize > 0 ? request.superSize : 1;

            var path = string.IsNullOrEmpty(request.path)
                ? Path.Combine("Screenshots", $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png")
                : request.path;
            path = ResolvePath(path);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var (width, height) = isPlaying
                ? await CapturePlayModeAsync(path, superSize, cancellationToken)
                : EditModeGameViewCapture.Capture(path, superSize);

            var fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"Failed to save screenshot to: {fullPath}");

            return new ScreenshotCaptureResponse
            {
                path = fullPath,
                mode = isPlaying ? "play" : "edit",
                width = width,
                height = height,
                size = new FileInfo(fullPath).Length
            };
        }

        private ScreenshotCaptureMode ParseMode(string mode)
        {
            if (string.IsNullOrEmpty(mode) || string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
                return ScreenshotCaptureMode.Auto;

            if (string.Equals(mode, "edit", StringComparison.OrdinalIgnoreCase))
                return ScreenshotCaptureMode.Edit;

            if (string.Equals(mode, "play", StringComparison.OrdinalIgnoreCase))
                return ScreenshotCaptureMode.Play;

            throw new ArgumentException(
                $"Invalid mode \"{mode}\" for '{CommandName}'. Valid values: \"auto\" (default), \"edit\", \"play\".");
        }

        private static async Task<(int width, int height)> CapturePlayModeAsync(string path, int superSize, CancellationToken cancellationToken)
        {
            Texture2D texture = null;
            try
            {
                texture = await CapturePlayModeTextureAsync(superSize, cancellationToken);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return (texture.width, texture.height);
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static async Task<Texture2D> CapturePlayModeTextureAsync(int superSize, CancellationToken cancellationToken)
        {
            var gameObject = new GameObject("UniCliScreenshotCaptureRunner")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(gameObject);

            var runner = gameObject.AddComponent<ScreenshotCaptureRunner>();

            try
            {
                return await runner.CaptureAsync(superSize, cancellationToken);
            }
            finally
            {
                if (gameObject != null)
                    UnityEngine.Object.Destroy(gameObject);
            }
        }

        private sealed class ScreenshotCaptureRunner : MonoBehaviour
        {
            public Task<Texture2D> CaptureAsync(int superSize, CancellationToken cancellationToken)
            {
                var tcs = new TaskCompletionSource<Texture2D>();
                StartCoroutine(CaptureCoroutine(superSize, tcs, cancellationToken));
                return tcs.Task;
            }

            private System.Collections.IEnumerator CaptureCoroutine(int superSize, TaskCompletionSource<Texture2D> tcs, CancellationToken cancellationToken)
            {
                yield return new WaitForEndOfFrame();

                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    yield break;
                }

                var tex = ScreenCapture.CaptureScreenshotAsTexture(superSize);
                if (tex != null)
                {
                    tcs.TrySetResult(tex);
                    yield break;
                }

                tcs.TrySetException(new InvalidOperationException("Failed to capture screenshot. Ensure the Game View is visible and rendering."));
            }
        }
    }

    /// <summary>
    /// Captures the Game View in Edit Mode by rendering the game cameras
    /// directly into the view's target texture through the same internal entry
    /// point the Game View uses when it repaints.
    ///
    /// ScreenCapture.CaptureScreenshot is not used here on purpose: its file is
    /// only written when the Game View actually repaints, which never happens
    /// while the editor application is in the background — the normal state when
    /// an agent drives the editor through UniCli.
    /// </summary>
    internal static class EditModeGameViewCapture
    {
        private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static (int width, int height) Capture(string path, int superSize)
        {
            var editorAssembly = typeof(EditorWindow).Assembly;
            var playModeViewType = editorAssembly.GetType("UnityEditor.PlayModeView");
            if (playModeViewType == null)
                throw new InvalidOperationException("Edit Mode capture is not supported: UnityEditor.PlayModeView is unavailable in this Unity version.");

            var view = GetOrCreateGameView(editorAssembly, playModeViewType);

            // Outside a Repaint event RenderView only (re)configures and returns
            // the view's target texture without drawing into it; the actual
            // rendering is requested explicitly below.
            var viewTexture = InvokeRenderView(playModeViewType, view);
            var targetDisplay = GetTargetDisplay(playModeViewType, view);

            RenderTexture scaledTexture = null;
            try
            {
                var target = viewTexture;
                if (superSize > 1)
                {
                    var descriptor = viewTexture.descriptor;
                    descriptor.width = viewTexture.width * superSize;
                    descriptor.height = viewTexture.height * superSize;
                    scaledTexture = new RenderTexture(descriptor);
                    if (!scaledTexture.Create())
                        throw new InvalidOperationException($"Failed to create a {descriptor.width}x{descriptor.height} render texture for superSize={superSize}.");
                    target = scaledTexture;
                }

                // Game cameras take their viewport from the display view size,
                // which stays at its 640x480 default until the Game View has
                // repainted at least once; align it with the capture target so
                // the cameras fill the whole texture, then restore it.
                using (DisplayViewSizeScope.Apply(playModeViewType, view, targetDisplay, target.width, target.height))
                {
                    RenderGameViewCameras(target, targetDisplay);
                }

                WritePng(target, path);
                return (target.width, target.height);
            }
            finally
            {
                if (scaledTexture != null)
                    UnityEngine.Object.DestroyImmediate(scaledTexture);
            }
        }

        private static EditorWindow GetOrCreateGameView(Assembly editorAssembly, Type playModeViewType)
        {
            var getMainPlayModeView = playModeViewType.GetMethod("GetMainPlayModeView", BindingFlags.Static | BindingFlags.NonPublic);
            var view = getMainPlayModeView?.Invoke(null, null) as EditorWindow;
            if (view != null)
                return view;

            var gameViewType = editorAssembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
                throw new InvalidOperationException("The Unity Game View type is unavailable.");

            view = EditorWindow.GetWindow(gameViewType, false, "Game", false);
            if (view == null)
                throw new InvalidOperationException("No Game View is available.");
            return view;
        }

        private static RenderTexture InvokeRenderView(Type playModeViewType, EditorWindow view)
        {
            var renderView = playModeViewType.GetMethod("RenderView", MemberFlags, null, new[] { typeof(Vector2), typeof(bool) }, null);
            if (renderView == null)
                throw new InvalidOperationException("Edit Mode capture is not supported: PlayModeView.RenderView is unavailable in this Unity version.");

            var texture = renderView.Invoke(view, new object[] { Vector2.zero, true }) as RenderTexture;
            if (texture == null)
                throw new InvalidOperationException("The Game View did not provide a target texture to capture.");
            return texture;
        }

        private static int GetTargetDisplay(Type playModeViewType, EditorWindow view)
        {
            var targetDisplay = playModeViewType.GetProperty("targetDisplay", MemberFlags);
            return targetDisplay != null ? (int)targetDisplay.GetValue(view) : 0;
        }

        private static void RenderGameViewCameras(RenderTexture target, int targetDisplay)
        {
            var renderCameras = typeof(EditorGUIUtility).GetMethod("RenderPlayModeViewCamerasInternal", BindingFlags.Static | BindingFlags.NonPublic);
            if (renderCameras == null)
                throw new InvalidOperationException("Edit Mode capture is not supported: EditorGUIUtility.RenderPlayModeViewCamerasInternal is unavailable in this Unity version.");

            renderCameras.Invoke(null, new object[] { target, targetDisplay, Vector2.zero, false, true });
        }

        private readonly struct DisplayViewSizeScope : IDisposable
        {
            private readonly MethodInfo _setDisplayViewSize;
            private readonly EditorWindow _view;
            private readonly int _targetDisplay;
            private readonly Vector2 _originalSize;

            private DisplayViewSizeScope(MethodInfo setDisplayViewSize, EditorWindow view, int targetDisplay, Vector2 originalSize)
            {
                _setDisplayViewSize = setDisplayViewSize;
                _view = view;
                _targetDisplay = targetDisplay;
                _originalSize = originalSize;
            }

            public static DisplayViewSizeScope Apply(Type playModeViewType, EditorWindow view, int targetDisplay, int width, int height)
            {
                var setDisplayViewSize = playModeViewType.GetMethod("SetDisplayViewSize", MemberFlags);
                var getDisplayViewSize = playModeViewType.GetMethod("GetDisplayViewSize", MemberFlags);
                if (setDisplayViewSize == null || getDisplayViewSize == null)
                    return default;

                var originalSize = (Vector2)getDisplayViewSize.Invoke(view, new object[] { targetDisplay });
                setDisplayViewSize.Invoke(view, new object[] { targetDisplay, new Vector2(width, height) });
                return new DisplayViewSizeScope(setDisplayViewSize, view, targetDisplay, originalSize);
            }

            public void Dispose()
            {
                _setDisplayViewSize?.Invoke(_view, new object[] { _targetDisplay, _originalSize });
            }
        }

        private static void WritePng(RenderTexture source, string path)
        {
            var previousActive = RenderTexture.active;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                texture.Apply();

                if (SystemInfo.graphicsUVStartsAtTop)
                    FlipVertically(texture);

                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void FlipVertically(Texture2D texture)
        {
            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            var flipped = new Color32[pixels.Length];
            for (var y = 0; y < height; y++)
                Array.Copy(pixels, y * width, flipped, (height - 1 - y) * width, width);
            texture.SetPixels32(flipped);
            texture.Apply();
        }
    }

    [Serializable]
    public class ScreenshotCaptureRequest
    {
        public string path;
        public int superSize;
        public string mode = ""; // "auto" (default), "edit", "play"
    }

    [Serializable]
    public class ScreenshotCaptureResponse
    {
        public string path;
        public string mode;
        public int width;
        public int height;
        public long size;
    }
}
