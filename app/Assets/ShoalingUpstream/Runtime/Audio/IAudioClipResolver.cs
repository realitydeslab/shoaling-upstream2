using UnityEngine;

namespace ShoalingUpstream.Audio
{
    /// <summary>
    /// A clip, in whichever of the two forms a backend can use.
    ///
    /// The two backends want different things and neither can be made to want the other's. PHASE
    /// registers sound assets from a file URL and streams them itself; Unity wants an
    /// <see cref="AudioClip"/> in memory. So a resolution carries both slots and each backend
    /// takes the one it can play. A clip that has neither is a miss, and a miss is reported rather
    /// than substituted — playing the wrong recording is worse than playing nothing.
    /// </summary>
    public readonly struct ResolvedClip
    {
        public readonly string ClipId;
        public readonly AudioClip Clip;
        public readonly string FilePath;

        public ResolvedClip(string clipId, AudioClip clip = null, string filePath = null)
        {
            ClipId = clipId; Clip = clip; FilePath = filePath;
        }

        public bool IsValid => Clip != null || !string.IsNullOrEmpty(FilePath);

        public static readonly ResolvedClip None = new(null);

        public override string ToString() =>
            $"{ClipId}{(Clip != null ? " [clip]" : "")}{(string.IsNullOrEmpty(FilePath) ? "" : " [file]")}";
    }

    /// <summary>
    /// The audio engine's whole dependency on wherever clips come from.
    ///
    /// Deliberately one method. The catalogue that will back it is being built separately and
    /// will have its own shape; keeping the seam this narrow means adapting it is a five-line
    /// class rather than a refactor, and means the engine's tests need no catalogue at all.
    /// </summary>
    public interface IAudioClipResolver
    {
        bool TryResolve(string clipId, out ResolvedClip clip);
    }
}
