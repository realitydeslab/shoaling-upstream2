using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ShoalingUpstream.Audio
{
    /// <summary>
    /// A stopgap resolver: clip ids straight onto files under StreamingAssets.
    ///
    /// It exists so the engine can be run today, before the catalogue lands. It resolves a file
    /// path, which is what PHASE wants, and nothing in memory, which is what Unity wants — so the
    /// desk stand-in needs a real catalogue and the device does not. Replace it with an adapter
    /// over the catalogue rather than growing it.
    ///
    /// It reports a miss rather than falling back to a similarly named file. Distance layers are
    /// three names apart by one suffix, and a resolver that guessed would play the intimate
    /// recording from twenty metres away without anyone noticing why the piece felt wrong.
    /// </summary>
    public class StreamingAssetsClipResolver : IAudioClipResolver
    {
        private static readonly string[] Extensions = { ".mp3", ".wav", ".ogg", ".m4a" };

        private readonly string _root;
        private readonly Dictionary<string, ResolvedClip> _cache = new();

        public StreamingAssetsClipResolver(string subdirectory = "audio")
        {
            _root = Path.Combine(Application.streamingAssetsPath, subdirectory);
        }

        public bool TryResolve(string clipId, out ResolvedClip clip)
        {
            if (_cache.TryGetValue(clipId, out clip)) return clip.IsValid;

            clip = ResolvedClip.None;
            foreach (var extension in Extensions)
            {
                string path = Path.Combine(_root, clipId + extension);
                if (!File.Exists(path)) continue;
                clip = new ResolvedClip(clipId, filePath: path);
                break;
            }

            _cache[clipId] = clip;
            return clip.IsValid;
        }
    }
}
