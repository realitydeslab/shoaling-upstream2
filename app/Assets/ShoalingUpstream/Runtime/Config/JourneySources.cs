using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ShoalingUpstream.Config
{
    /// <summary>
    /// Somewhere a journey document can be read from. Returns null when there is simply nothing
    /// there — an unpublished site, a first launch — and throws only when something went wrong.
    /// The distinction matters: "nothing yet" is ordinary, "the wifi died mid-read" is not.
    /// </summary>
    public interface IJourneyReader
    {
        Task<string> ReadAsync(CancellationToken cancellationToken);
    }

    /// <summary>The one source that is also written to.</summary>
    public interface IJourneyCache : IJourneyReader
    {
        Task WriteAsync(string json, CancellationToken cancellationToken);

        /// <summary>Throw away a cache entry that could not be read. Called when a cached
        /// document fails to parse, so the next launch does not repeat the same failure.</summary>
        void Discard();
    }

    /// <summary>
    /// A journey packaged into the build, or any other plain file.
    ///
    /// The read is synchronous inside a completed Task on purpose. It is a ~30 KB local file,
    /// and everything downstream of it — UnityWebRequest, AudioClip creation — must happen on
    /// the main thread; introducing a thread hop here to save a fraction of a millisecond would
    /// mean the rest of the resolve resumes somewhere it is not allowed to run.
    /// </summary>
    public sealed class FileJourneyReader : IJourneyReader
    {
        private readonly string _path;

        public FileJourneyReader(string path) => _path = path;

        public string Path => _path;

        public Task<string> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(File.Exists(_path) ? File.ReadAllText(_path) : null);

        /// <summary>The journey for a site as packaged by tools/export-to-unity.mjs.</summary>
        public static FileJourneyReader Bundled(string slug) =>
            new(BundleLayout.JourneyPath(BundleLayout.StreamingAssetsRoot, slug));
    }

    /// <summary>
    /// The last fetch that completed, kept in persistentDataPath.
    ///
    /// The whole value of this class is in how it writes. A phone that loses wifi, is killed by
    /// the OS, or runs out of disk halfway through writing a cache file leaves a truncated
    /// document behind, and the next launch — in a park, with no service to fall back on — is
    /// where that is discovered. So the write goes to a temporary file first and is then moved
    /// into place, which on this filesystem is atomic: at every instant the cache path holds
    /// either the previous document or the new one, never half of either.
    /// </summary>
    public sealed class PersistentJourneyCache : IJourneyCache
    {
        public const string Folder = "journey-cache";

        private readonly string _path;

        public PersistentJourneyCache(string path) => _path = path;

        public string Path => _path;

        public static PersistentJourneyCache ForSite(string slug) =>
            new(System.IO.Path.Combine(Application.persistentDataPath, Folder, slug + ".json"));

        public Task<string> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(File.Exists(_path) ? File.ReadAllText(_path) : null);

        public Task WriteAsync(string json, CancellationToken cancellationToken)
        {
            string directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string temporary = _path + ".writing";
            File.WriteAllText(temporary, json);

            // Replace, not an overwriting Move — that overload is not in Unity's .NET Standard
            // profile — and above all not delete-then-move, which would open exactly the window
            // this method exists to close.
            //
            // The guarantee this provides, precisely: both calls are a rename, so a reader at any
            // instant sees either the whole previous document or the whole new one. It never sees
            // half of one, and there is never a moment with no cache at all.
            //
            // The guarantee it does NOT provide: durability. Nothing is fsynced, so a power loss
            // can still cost the newest write or leave its bytes unflushed. That is why the
            // reader validates what it finds and the provider discards what will not parse — the
            // rename protects the common case, the parser protects the rest.
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);

            return Task.CompletedTask;
        }

        public void Discard()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Config] could not discard cache at {_path}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// GET {host}/api/sites/{slug}/published — the laptop on the same wifi.
    ///
    /// UnityWebRequest rather than HttpClient because it is already asynchronous without
    /// leaving the main thread, which is where the rest of launch has to stay. A 404 means the
    /// site exists but has never been published and is reported as "nothing there", not as an
    /// error; anything else throws and the provider carries on without it.
    /// </summary>
    public sealed class ServiceJourneyReader : IJourneyReader
    {
        private readonly string _url;
        private readonly int _timeoutSeconds;

        public ServiceJourneyReader(string host, string slug, float timeoutSeconds)
        {
            _url = $"{host.TrimEnd('/')}/api/sites/{slug}/published";

            // UnityWebRequest's own timeout is whole seconds and is a backstop only; the
            // provider races a shorter deadline of its own so that a host which accepts the
            // connection and then says nothing cannot hold up the launch either.
            _timeoutSeconds = Mathf.Max(1, Mathf.CeilToInt(timeoutSeconds));
        }

        public string Url => _url;

        public Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<string>();
            var request = UnityWebRequest.Get(_url);
            request.timeout = _timeoutSeconds;

            CancellationTokenRegistration registration = default;
            var operation = request.SendWebRequest();

            operation.completed += _ =>
            {
                try
                {
                    if (request.responseCode == 404)
                    {
                        completion.TrySetResult(null);
                    }
                    else if (request.result != UnityWebRequest.Result.Success)
                    {
                        completion.TrySetException(new IOException(
                            $"{_url}: {request.error ?? request.result.ToString()}"));
                    }
                    else
                    {
                        completion.TrySetResult(request.downloadHandler.text);
                    }
                }
                finally
                {
                    registration.Dispose();
                    request.Dispose();
                }
            };

            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() =>
                {
                    if (!request.isDone) request.Abort();
                });
            }

            return completion.Task;
        }
    }
}
