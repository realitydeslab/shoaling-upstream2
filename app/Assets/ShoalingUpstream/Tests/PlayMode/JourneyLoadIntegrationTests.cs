using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ShoalingUpstream.Config;
using ShoalingUpstream.Journey;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace ShoalingUpstream.Tests.Integration
{
    /// <summary>
    /// What the app is actually handed when it asks a live service for its journey.
    ///
    /// The control tests need a centreline to project onto, and where that centreline comes from
    /// is not a detail: the app reads PUBLISHED revisions, and the revision published in the
    /// artist's data is the superseded seeded layout. Everything here runs against a scratch
    /// service holding a copy, published there so the app sees the current artwork — which is
    /// also the only way to find out whether the current artwork loads at all.
    /// </summary>
    [TestFixture]
    public sealed class JourneyLoadIntegrationTests
    {
        private const string Slug = "ubc-nitobe-garden-creek";
        private LiveService _service;

        [OneTimeSetUp]
        public void StartService()
        {
            _service = LiveService.Start();
            _service.Http("POST", $"/api/sites/{Slug}/publish");
        }

        [OneTimeTearDown]
        public void StopService() => _service?.Dispose();

        /// <summary>
        /// A real UnityWebRequest to a real service, through the reader the launch uses.
        /// </summary>
        [UnityTest]
        public IEnumerator TheAppLoadsTheCurrentArtworkFromALiveService()
        {
            var settings = new JourneyProviderSettings
            {
                Slug = Slug,
                ServiceHost = _service.HttpUrl,
                ServiceTimeoutSeconds = 5f,
                // A desk, not a creek. The distinction decides whether an uncalibrated journey is
                // usable, and it is the whole reason simulation can proceed at all today.
                Environment = JourneyEnvironment.Simulation,
            };

            // Bundled and cache left out deliberately: this is a test of the wire, and a cache in
            // the editor's persistent data would let a previous run answer for the service.
            var provider = new JourneyProvider(settings, bundled: null, cache: null,
                                               service: new ServiceJourneyReader(
                                                   settings.ServiceHost, Slug, settings.ServiceTimeoutSeconds));

            var resolve = provider.ResolveAsync();
            yield return Until(() => resolve.IsCompleted, "the journey to resolve over HTTP", 15f);

            var resolution = resolve.Result;
            JourneyProvider.Log(resolution);

            Assert.That(resolution.HasJourney, Is.True,
                        "nothing resolved — " + string.Join(" | ", resolution.Notes));
            Assert.That(resolution.Source, Is.EqualTo(JourneySourceKind.Service));

            var journey = resolution.Document;
            Assert.That(journey.site.slug, Is.EqualTo(Slug));
            Assert.That(journey.site.centreline.Count, Is.GreaterThanOrEqualTo(2));

            float length = Centreline.Length(journey.site.centreline);
            Debug.Log($"[integration] resolved r{journey.revision}: "
                      + $"{journey.site.centreline.Count} centreline points, {length:0.0} m, "
                      + $"beats {string.Join("/", journey.beats.ConvertAll(b => b.id))}, "
                      + $"calibrated={journey.editorFrame?.calibrated}");

            Assert.That(length, Is.GreaterThan(1f), "a journey with no length cannot be walked");
        }

        /// <summary>
        /// The refusal that stops this from being a device test.
        ///
        /// `editorFrame.calibrated` is false on the current draft, and the resolver is designed to
        /// refuse an uncalibrated journey on device and allow it in simulation. That is correct
        /// behaviour, and it is also exactly the reason nothing below the desk can be proven yet.
        /// </summary>
        [UnityTest]
        public IEnumerator AnUncalibratedJourneyIsRefusedOnDeviceAndAllowedInSimulation()
        {
            string published = _service.Http("GET", $"/api/sites/{Slug}/published");
            Assert.That(JourneyParser.TryParse(published, out var journey, out string error), Is.True, error);

            if (journey.editorFrame is { calibrated: true })
            {
                Assert.Ignore("the current draft has been calibrated on site — this test has "
                              + "nothing left to say, and that is good news");
            }

            var candidates = new[] { new JourneyCandidate(JourneySourceKind.Service, published) };

            var onDevice = JourneyResolver.Resolve(candidates, JourneyEnvironment.Device, Slug);
            Assert.That(onDevice.HasJourney, Is.False,
                        "an uncalibrated journey must not run on a phone in a creek");
            Assert.That(onDevice.VerdictFor(JourneySourceKind.Service),
                        Is.EqualTo(CandidateVerdict.Uncalibrated));

            var atTheDesk = JourneyResolver.Resolve(candidates, JourneyEnvironment.Simulation, Slug);
            Assert.That(atTheDesk.HasJourney, Is.True,
                        "simulation is where uncalibrated coordinates are supposed to be exercised");
            yield break;
        }

        /// <summary>
        /// Not an assertion — a record.
        ///
        /// Whether the artist's published revision matches their draft is their business and will
        /// change the day they publish, so this reports rather than fails. It is here because a
        /// test run that says "everything works" while the app on the phone would load different
        /// artwork is worse than no test run at all.
        /// </summary>
        [Test]
        public void ReportWhetherThePublishedRevisionMatchesTheDraft()
        {
            string dir = Path.Combine(LiveService.RepoRoot, "data", "journeys", Slug);
            string draftPath = Path.Combine(dir, "draft.json");
            string revisionDir = Path.Combine(dir, "revisions");
            if (!File.Exists(draftPath) || !Directory.Exists(revisionDir))
            {
                Assert.Ignore($"no draft or revisions under {dir}");
            }

            Assert.That(JourneyParser.TryParse(File.ReadAllText(draftPath), out var draft, out _), Is.True);

            string[] revisions = Directory.GetFiles(revisionDir, "r*.json");
            Array.Sort(revisions);
            if (revisions.Length == 0) Assert.Ignore("nothing has been published for this site");

            Assert.That(JourneyParser.TryParse(File.ReadAllText(revisions[^1]), out var latest, out _), Is.True);

            string draftShape = Describe(draft);
            string publishedShape = Describe(latest);
            Debug.Log($"[integration] draft      {draftShape}\n"
                      + $"[integration] published  {publishedShape}\n"
                      + $"[integration] the app reads PUBLISHED, so the phone would walk the second one.");

            if (draftShape != publishedShape)
            {
                Debug.LogWarning(
                    "[integration] the published revision is NOT the current draft. Every test in "
                    + "this folder therefore runs against a scratch service with the draft "
                    + "published into a copy — nothing on a phone would match until the artist "
                    + "publishes.");
            }
        }

        private static string Describe(JourneyDocument journey) =>
            $"r{journey.revision}: {journey.site.centreline.Count} points, "
            + $"{Centreline.Length(journey.site.centreline):0.0} m, "
            + $"beats {string.Join("/", journey.beats.ConvertAll(b => b.id))}, "
            + $"calibrated={journey.editorFrame?.calibrated}";

        private static IEnumerator Until(Func<bool> condition, string what, float seconds = 5f)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                bool met;
                try { met = condition(); }
                catch (Exception) { met = false; }
                if (met) yield break;
                yield return null;
            }
            Assert.Fail($"timed out after {seconds:0.#} s waiting for {what}");
        }
    }
}
