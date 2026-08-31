using UnityEngine;
using UnityEngine.Rendering;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Reads the environment an environment check needs. Exists for the same reason
    /// <see cref="IEditorStateProbe"/> does: a test cannot put Unity's statics into a chosen state,
    /// and this check is only worth having if its rules are tested.
    /// </summary>
    public interface IEnvironmentProbe
    {
        bool IsBatchMode { get; }
        bool HasGraphicsDevice { get; }
    }

    internal sealed class UnityEnvironmentProbe : IEnvironmentProbe
    {
        public static readonly UnityEnvironmentProbe Instance = new();

        public bool IsBatchMode => Application.isBatchMode;

        public bool HasGraphicsDevice => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
    }
}
