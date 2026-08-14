using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Config
{
    /// <summary>
    /// Turns a journey's `clipId` into a playable <see cref="AudioClip"/>.
    ///
    /// Everything it serves is already in the build: tools/export-to-unity.mjs copies every clip
    /// the published revision references into StreamingAssets beside the journey, and refuses to
    /// export at all if one is missing. So this class never fetches, never falls back, and works
    /// identically with a laptop present and with no network at all.
    ///
    /// Clip ids are the ones the editor authors — `heron--far`, `heron--mid`, `heron--intimate`
    /// and the one-shot completions such as `lay-egg`. There is one file per id.
    ///
    /// Usage, from a coroutine:
    ///
    ///     var request = catalogue.Load("heron--mid");
    ///     yield return request;
    ///     if (request.Clip != null) source.clip = request.Clip;
    ///
    /// or as a task, if the caller is already async. Both share one load per clip: asking twice
    /// returns the same request and the same clip, so three distance layers crossfading against
    /// each other cannot decode the same file three times.
    /// </summary>
    public sealed class AudioCatalogue
    {
        private readonly string _directory;
        private readonly Dictionary<string, long> _expectedBytes = new();
        private readonly List<string> _clipIds = new();
        private readonly Dictionary<string, ClipRequest> _requests = new();

        private AudioCatalogue(string directory)
        {
            _directory = directory;
            Index();
        }

        /// <summary>The catalogue packaged into this build.</summary>
        public static AudioCatalogue FromStreamingAssets() =>
            new(BundleLayout.AudioDirectory(BundleLayout.StreamingAssetsRoot));

        /// <summary>Any other folder of packaged clips. Used by the tests, and by the editor
        /// when auditioning a folder that has not been packaged into a build yet.</summary>
        public static AudioCatalogue FromDirectory(string directory) => new(directory);

        public string Directory => _directory;

        /// <summary>Every clip id in the build, in index order.</summary>
        public IReadOnlyList<string> ClipIds => _clipIds;

        public bool Contains(string clipId) =>
            !string.IsNullOrEmpty(clipId) && _expectedBytes.ContainsKey(clipId);

        /// <summary>The clip if it has already been loaded, otherwise null. Never starts a
        /// load — for audio that must not stall, ask for it early and check here.</summary>
        public AudioClip Loaded(string clipId) =>
            clipId != null && _requests.TryGetValue(clipId, out var request) ? request.Clip : null;

        /// <summary>
        /// Begin loading a clip, or hand back the load already in flight. The returned request
        /// can be yielded in a coroutine or awaited; it completes with either a clip or an
        /// error, never with silence and no explanation.
        /// </summary>
        public ClipRequest Load(string clipId)
        {
            if (string.IsNullOrEmpty(clipId))
            {
                return ClipRequest.Failed("no clipId");
            }
            if (_requests.TryGetValue(clipId, out var existing))
            {
                return existing;
            }

            var request = Begin(clipId);
            _requests[clipId] = request;
            return request;
        }

        /// <summary>Await a clip. Same load, same caching, for callers that are already async.</summary>
        public Task<AudioClip> LoadAsync(string clipId) => Load(clipId).Completion;

        /// <summary>
        /// Which of a journey's clips are not in this build.
        ///
        /// Empty is the only acceptable answer for a journey the app is about to run, and it is
        /// what the exporter guarantees for a bundled one. It can be non-empty for a journey
        /// fetched from the service: a revision published after the last packaging run can
        /// reference a clip that was never copied to the device, and every beat using it would
        /// simply be silent. Check it when a fetched revision wins, and say so out loud.
        /// </summary>
        public List<string> MissingClipIds(JourneyDocument journey)
        {
            var missing = new List<string>();
            foreach (string clipId in ReferencedClipIds(journey))
            {
                if (!Contains(clipId) && !missing.Contains(clipId)) missing.Add(clipId);
            }
            return missing;
        }

        /// <summary>
        /// Every clip a journey names, beats and ambient sources alike, without duplicates.
        ///
        /// Layers with no clipId are skipped, and there are always some: JsonUtility fills in
        /// every serializable field it knows about whether or not the JSON contained it, so a
        /// beat with no completion sound still arrives carrying an empty `completion` layer.
        /// Mirrored by clipIdsIn() in tools/export-to-unity.mjs — the two must agree, or the
        /// exporter packages a set the runtime does not ask for.
        /// </summary>
        public static List<string> ReferencedClipIds(JourneyDocument journey)
        {
            var ids = new List<string>();
            if (journey == null) return ids;

            void Add(AudioLayer layer)
            {
                if (layer == null || string.IsNullOrWhiteSpace(layer.clipId)) return;
                if (!ids.Contains(layer.clipId)) ids.Add(layer.clipId);
            }

            void AddAll(BeatAudio audio)
            {
                if (audio == null) return;
                Add(audio.far);
                Add(audio.mid);
                Add(audio.intimate);
                Add(audio.completion);
            }

            if (journey.beats != null)
            {
                foreach (var beat in journey.beats) AddAll(beat?.audio);
            }
            if (journey.ambient != null)
            {
                foreach (var source in journey.ambient) AddAll(source?.audio);
            }
            return ids;
        }

        /// <summary>
        /// Read the index the exporter wrote, or fall back to the folder itself.
        ///
        /// The index carries the byte length of each clip, which is the one cheap way to notice
        /// that a file was copied into the build only partly. Falling back to a directory listing
        /// keeps a hand-assembled folder usable; it works on iOS, where StreamingAssets is an
        /// ordinary directory inside the .app.
        /// </summary>
        private void Index()
        {
            string indexPath = Path.Combine(_directory, BundleLayout.AudioIndexFile);
            if (File.Exists(indexPath))
            {
                try
                {
                    var index = JsonUtility.FromJson<AudioIndex>(File.ReadAllText(indexPath));
                    if (index?.clips != null)
                    {
                        foreach (var entry in index.clips)
                        {
                            if (entry == null || string.IsNullOrWhiteSpace(entry.clipId)) continue;
                            if (_expectedBytes.ContainsKey(entry.clipId)) continue;
                            _expectedBytes[entry.clipId] = entry.bytes;
                            _clipIds.Add(entry.clipId);
                        }
                        return;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Audio] index at {indexPath} is unreadable: {e.Message}");
                }
            }

            if (!System.IO.Directory.Exists(_directory)) return;

            foreach (string file in System.IO.Directory.GetFiles(
                         _directory, "*" + BundleLayout.ClipExtension))
            {
                string clipId = Path.GetFileNameWithoutExtension(file);
                if (_expectedBytes.ContainsKey(clipId)) continue;
                _expectedBytes[clipId] = -1;   // unknown length: nothing to check against
                _clipIds.Add(clipId);
            }
            _clipIds.Sort(StringComparer.Ordinal);
        }

        private ClipRequest Begin(string clipId)
        {
            if (!Contains(clipId))
            {
                return ClipRequest.Failed($"\"{clipId}\" is not in this build");
            }

            string path = BundleLayout.ClipPath(_directory, clipId);
            if (!File.Exists(path))
            {
                return ClipRequest.Failed($"\"{clipId}\" is in the index but missing at {path}");
            }

            long expected = _expectedBytes[clipId];
            long actual = new FileInfo(path).Length;
            if (expected >= 0 && actual != expected)
            {
                // A short file plays as a click or as nothing. Refusing it makes a broken
                // packaging run look like a broken packaging run rather than like a mix problem.
                return ClipRequest.Failed(
                    $"\"{clipId}\" is {actual} bytes, the index says {expected} — repackage it");
            }

            var request = new ClipRequest(clipId);
            var webRequest = UnityWebRequestMultimedia.GetAudioClip(
                new Uri(path).AbsoluteUri, AudioType.MPEG);

            // Keep the MP3 compressed in memory. Decoded, the ambient beds are around 11 MB a
            // minute each and several are resident at once; compressed they are under a megabyte
            // and iOS decodes them in hardware.
            ((DownloadHandlerAudioClip)webRequest.downloadHandler).compressed = true;

            webRequest.SendWebRequest().completed += _ =>
            {
                try
                {
                    if (webRequest.result != UnityWebRequest.Result.Success)
                    {
                        request.Fail($"\"{clipId}\": {webRequest.error}");
                        return;
                    }
                    var clip = DownloadHandlerAudioClip.GetContent(webRequest);
                    if (clip == null)
                    {
                        request.Fail($"\"{clipId}\" decoded to nothing");
                        return;
                    }
                    clip.name = clipId;
                    request.Succeed(clip);
                }
                finally
                {
                    webRequest.Dispose();
                }
            };

            return request;
        }

        [Serializable]
        private class AudioIndex
        {
            public List<AudioIndexEntry> clips = new();
        }

        [Serializable]
        private class AudioIndexEntry
        {
            public string clipId;
            public string file;
            public long bytes;
        }
    }

    /// <summary>
    /// One clip being loaded. Yield it in a coroutine, or await <see cref="Completion"/>.
    /// A finished request has either a <see cref="Clip"/> or an <see cref="Error"/>.
    /// </summary>
    public sealed class ClipRequest : CustomYieldInstruction
    {
        private readonly TaskCompletionSource<AudioClip> _completion = new();

        internal ClipRequest(string clipId) => ClipId = clipId;

        public string ClipId { get; }
        public AudioClip Clip { get; private set; }
        public string Error { get; private set; }
        public bool IsDone { get; private set; }

        /// <summary>Completes with the clip, or with null if the load failed —
        /// <see cref="Error"/> says why. It does not throw: a missing clip should cost one
        /// silent source, not the rest of the walk.</summary>
        public Task<AudioClip> Completion => _completion.Task;

        public override bool keepWaiting => !IsDone;

        internal void Succeed(AudioClip clip)
        {
            Clip = clip;
            IsDone = true;
            _completion.TrySetResult(clip);
        }

        internal void Fail(string error)
        {
            Error = error;
            IsDone = true;
            Debug.LogWarning($"[Audio] {error}");
            _completion.TrySetResult(null);
        }

        internal static ClipRequest Failed(string error)
        {
            var request = new ClipRequest(null);
            request.Fail(error);
            return request;
        }
    }
}
