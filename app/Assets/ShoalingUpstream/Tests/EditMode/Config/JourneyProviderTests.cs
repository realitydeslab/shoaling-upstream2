using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using ShoalingUpstream.Config;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// Gathering the candidates: what happens when the laptop is not there, when it is there
    /// but silent, and when what it hands back is not a journey.
    ///
    /// The cache is the subject of most of this. A cache that is right costs nobody anything;
    /// a cache holding half a document is the failure that ends a session, because it is only
    /// discovered on the launch where there is nothing else to fall back to.
    ///
    /// Each test drives the resolve from the editor loop rather than blocking on it: the
    /// timeout resumes on the main thread, so a Wait() here would deadlock the thing under test.
    /// </summary>
    public class JourneyProviderTests
    {
        private const string Slug = "ubc-nitobe-garden-creek";
        private const int PatienceMs = 15_000;

        private static string Journey(int revision, string slug = Slug) => $@"{{
          ""schemaVersion"": ""2.0"", ""journeyId"": ""{slug}"", ""title"": ""Garden Creek"",
          ""revision"": {revision},
          ""site"": {{ ""slug"": ""{slug}"", ""centreline"": [
            {{ ""x"": 0, ""y"": 0, ""z"": 0 }}, {{ ""x"": 0, ""y"": 0, ""z"": 60 }} ] }},
          ""editorFrame"": {{ ""calibrated"": true }},
          ""beats"": [ {{ ""id"": ""tree"", ""title"": ""The tree"", ""interaction"": ""proximity"",
            ""s"": 10, ""position"": {{ ""x"": 0, ""y"": 0, ""z"": 10 }},
            ""trigger"": {{ ""enterRadiusM"": 1.9, ""exitRadiusM"": 3.04, ""dwellSeconds"": 1.2,
              ""minimumHoldSeconds"": 25, ""requiresPreviousComplete"": true }},
            ""audio"": {{ ""far"": {{ ""clipId"": ""heron--far"" }} }} }} ],
          ""shoal"": {{ ""startingCount"": 40, ""minimumCount"": 6 }}
        }}";

        private static JourneyProviderSettings Settings(float timeoutSeconds = 0.25f) =>
            new()
            {
                Slug = Slug,
                ServiceHost = "http://unused",
                ServiceTimeoutSeconds = timeoutSeconds,
                Environment = JourneyEnvironment.Simulation,
            };

        // --- doubles ------------------------------------------------------

        private class FakeReader : IJourneyReader
        {
            private readonly string _json;
            private readonly Exception _error;

            public FakeReader(string json = null, Exception error = null)
            {
                _json = json;
                _error = error;
            }

            public Task<string> ReadAsync(CancellationToken cancellationToken) =>
                _error != null ? Task.FromException<string>(_error) : Task.FromResult(_json);
        }

        /// <summary>A service that accepts the connection and then says nothing at all — the
        /// phone-on-the-wrong-wifi case, which does not fail, it hangs. It ignores the
        /// cancellation token on purpose: the launch must survive a source that does.</summary>
        private class SilentReader : IJourneyReader
        {
            public Task<string> ReadAsync(CancellationToken cancellationToken) =>
                new TaskCompletionSource<string>().Task;
        }

        private class FakeCache : IJourneyCache
        {
            public string Contents;
            public bool Discarded;
            public int Writes;

            public FakeCache(string contents = null) => Contents = contents;

            public Task<string> ReadAsync(CancellationToken cancellationToken) =>
                Task.FromResult(Contents);

            public Task WriteAsync(string json, CancellationToken cancellationToken)
            {
                Contents = json;
                Writes++;
                return Task.CompletedTask;
            }

            public void Discard()
            {
                Contents = null;
                Discarded = true;
            }
        }

        // --- tests --------------------------------------------------------

        [UnityTest]
        public IEnumerator AServiceThatNeverAnswersDoesNotStallTheLaunch()
        {
            var provider = new JourneyProvider(
                Settings(timeoutSeconds: 0.25f),
                new FakeReader(Journey(4)),
                new FakeCache(),
                new SilentReader());

            var clock = Stopwatch.StartNew();
            var task = provider.ResolveAsync();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs) yield return null;

            Assert.IsTrue(task.IsCompleted, "the resolve never finished");
            Assert.Less(clock.Elapsed.TotalSeconds, 5.0,
                "a dead network must cost a pause, not a session");
            Assert.AreEqual(JourneySourceKind.Bundled, task.Result.Source);
            Assert.IsNull(task.Result.VerdictFor(JourneySourceKind.Service),
                "a service that never answered is not a candidate at all");
        }

        [UnityTest]
        public IEnumerator AnUnreachableServiceLeavesTheBundledJourneyRunning()
        {
            var provider = new JourneyProvider(
                Settings(),
                new FakeReader(Journey(4)),
                new FakeCache(),
                new FakeReader(error: new IOException("connection refused")));

            var task = provider.ResolveAsync();
            for (var clock = Stopwatch.StartNew();
                 !task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(JourneySourceKind.Bundled, task.Result.Source);
            Assert.AreEqual(4, task.Result.Document.revision);
        }

        [UnityTest]
        public IEnumerator AGoodFetchIsKeptForTheNextLaunch()
        {
            var cache = new FakeCache();
            var provider = new JourneyProvider(
                Settings(), new FakeReader(Journey(4)), cache, new FakeReader(Journey(5)));

            var task = provider.ResolveAsync();
            for (var clock = Stopwatch.StartNew();
                 !task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(JourneySourceKind.Service, task.Result.Source);
            Assert.AreEqual(1, cache.Writes);
            StringAssert.Contains("\"revision\": 5", cache.Contents);
        }

        [UnityTest]
        public IEnumerator AFetchThatIsNotAJourneyNeverReachesTheCache()
        {
            // The single guard that stops a bad afternoon at the creek from becoming a bad
            // launch in a park: what is not good enough to run is not good enough to keep.
            var cache = new FakeCache(Journey(3));
            var provider = new JourneyProvider(
                Settings(), new FakeReader(Journey(4)), cache, new FakeReader("{ half a doc"));

            var task = provider.ResolveAsync();
            for (var clock = Stopwatch.StartNew();
                 !task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(0, cache.Writes);
            Assert.AreEqual(Journey(3), cache.Contents, "the previous good cache must survive");
            Assert.AreEqual(JourneySourceKind.Bundled, task.Result.Source);
        }

        [UnityTest]
        public IEnumerator ATruncatedCacheIsThrownAwayRatherThanReadAgain()
        {
            var cache = new FakeCache(Journey(7).Substring(0, 120));
            var provider = new JourneyProvider(Settings(), new FakeReader(Journey(4)), cache);

            var task = provider.ResolveAsync();
            for (var clock = Stopwatch.StartNew();
                 !task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(JourneySourceKind.Bundled, task.Result.Source);
            Assert.IsTrue(cache.Discarded, "an unreadable cache must not fail the same way twice");
        }

        [UnityTest]
        public IEnumerator AJourneyForAnotherSiteIsNotFiledUnderThisOne()
        {
            // The cache is keyed by slug. Keeping Berkeley's document under UBC's name would
            // let a mistyped host outlive the launch that produced it.
            var cache = new FakeCache();
            var provider = new JourneyProvider(
                Settings(), new FakeReader(Journey(4)), cache,
                new FakeReader(Journey(9, slug: "ucb-strawberry-creek-south")));

            var task = provider.ResolveAsync();
            for (var clock = Stopwatch.StartNew();
                 !task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(0, cache.Writes);
            Assert.AreEqual(JourneySourceKind.Bundled, task.Result.Source);
        }

        [UnityTest]
        public IEnumerator WithNoServiceConfiguredTheLaunchIsPurelyLocal()
        {
            // The standalone TestFlight walk. No host, so nothing is attempted and nothing waits.
            var provider = new JourneyProvider(
                Settings(), new FakeReader(Journey(4)), new FakeCache(Journey(6)));

            var task = provider.ResolveAsync();
            for (var clock = Stopwatch.StartNew();
                 !task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(JourneySourceKind.Cache, task.Result.Source);
            Assert.AreEqual(6, task.Result.Document.revision);
        }

        [UnityTest]
        public IEnumerator AnUnpackagedBuildReportsRatherThanThrows()
        {
            var provider = new JourneyProvider(
                Settings(), new FileJourneyReader("/no/such/journey.json"), new FakeCache());

            var task = provider.ResolveAsync();
            for (var clock = Stopwatch.StartNew();
                 !task.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

            Assert.IsTrue(task.IsCompleted);
            Assert.IsFalse(task.Result.HasJourney);
            Assert.IsNotEmpty(task.Result.Notes, "silence is not an acceptable explanation");
        }

        [UnityTest]
        public IEnumerator TheRealCacheLeavesNoHalfWrittenFileBehind()
        {
            // The atomic move is the whole point of PersistentJourneyCache, so it is worth
            // exercising against a real filesystem rather than a double.
            string directory = Path.Combine(Path.GetTempPath(), "shoaling-cache-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);

            try
            {
                var cache = new PersistentJourneyCache(Path.Combine(directory, Slug + ".json"));

                var write = cache.WriteAsync(Journey(3), CancellationToken.None);
                for (var clock = Stopwatch.StartNew();
                     !write.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

                var rewrite = cache.WriteAsync(Journey(4), CancellationToken.None);
                for (var clock = Stopwatch.StartNew();
                     !rewrite.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

                var read = cache.ReadAsync(CancellationToken.None);
                for (var clock = Stopwatch.StartNew();
                     !read.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;

                StringAssert.Contains("\"revision\": 4", read.Result);
                Assert.AreEqual(1, Directory.GetFiles(directory).Length,
                    "the temporary file must not be left beside the cache");

                cache.Discard();
                var gone = cache.ReadAsync(CancellationToken.None);
                for (var clock = Stopwatch.StartNew();
                     !gone.IsCompleted && clock.ElapsedMilliseconds < PatienceMs;) yield return null;
                Assert.IsNull(gone.Result);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
