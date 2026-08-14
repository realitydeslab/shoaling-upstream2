using System.Collections.Generic;
using NUnit.Framework;
using ShoalingUpstream.Config;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// Which journey the app runs, and which ones it refuses.
    ///
    /// These are the decisions nobody can check by looking at the phone: every one of them
    /// produces an app that launches and plays something, and the only way to tell a stale
    /// revision or an uncalibrated layout from a correct one is to be standing in the right
    /// part of a creek at the time.
    /// </summary>
    public class JourneyResolverTests
    {
        private const string Slug = "ubc-nitobe-garden-creek";

        /// <summary>A minimal document that the parser accepts, shaped like the real thing.</summary>
        private static string Journey(
            int revision, bool calibrated = true, string slug = Slug, string schemaVersion = "2.0")
            => $@"{{
              ""schemaVersion"": ""{schemaVersion}"",
              ""journeyId"": ""{slug}"",
              ""title"": ""Garden Creek"",
              ""revision"": {revision},
              ""site"": {{
                ""slug"": ""{slug}"",
                ""centreline"": [
                  {{ ""x"": 0, ""y"": 0, ""z"": 0 }},
                  {{ ""x"": 0, ""y"": 0, ""z"": 60 }}
                ]
              }},
              ""editorFrame"": {{ ""calibrated"": {(calibrated ? "true" : "false")} }},
              ""beats"": [
                {{
                  ""id"": ""tree"", ""title"": ""The tree"", ""interaction"": ""proximity"",
                  ""s"": 10, ""position"": {{ ""x"": 0, ""y"": 0, ""z"": 10 }},
                  ""trigger"": {{
                    ""enterRadiusM"": 1.9, ""exitRadiusM"": 3.04,
                    ""dwellSeconds"": 1.2, ""minimumHoldSeconds"": 25,
                    ""requiresPreviousComplete"": true
                  }},
                  ""audio"": {{
                    ""far"": {{ ""clipId"": ""tree-creek-waterplants-1--far"" }},
                    ""mid"": {{ ""clipId"": ""tree-creek-waterplants-1--mid"" }},
                    ""intimate"": {{ ""clipId"": ""tree-creek-waterplants-1--intimate"" }}
                  }}
                }}
              ],
              ""shoal"": {{ ""startingCount"": 40, ""minimumCount"": 6 }}
            }}";

        private static JourneyResolution Resolve(
            JourneyEnvironment environment, params (JourneySourceKind kind, string json)[] candidates)
        {
            var list = new List<JourneyCandidate>();
            foreach (var (kind, json) in candidates) list.Add(new JourneyCandidate(kind, json));
            return JourneyResolver.Resolve(list, environment, Slug);
        }

        [Test]
        public void TheBundledJourneyRunsWhenNothingElseIsThere()
        {
            // The Berkeley walk: TestFlight, a creek, no laptop within a thousand miles.
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, Journey(4)));

            Assert.IsTrue(resolution.HasJourney);
            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source);
            Assert.AreEqual(4, resolution.Document.revision);
        }

        [Test]
        public void AFreshlyPublishedRevisionOvertakesTheBuild()
        {
            // The field test: the artist publishes from the laptop and expects the next launch
            // to pick it up without a rebuild.
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, Journey(4)),
                (JourneySourceKind.Cache, Journey(4)),
                (JourneySourceKind.Service, Journey(5)));

            Assert.AreEqual(JourneySourceKind.Service, resolution.Source);
            Assert.AreEqual(5, resolution.Document.revision);
        }

        [Test]
        public void AStaleLaptopCannotUndoTheBuild()
        {
            // A laptop restored from an old copy of data/journeys serves an older revision than
            // the one in TestFlight. Taking it would silently walk the visitor through a layout
            // that was superseded weeks ago.
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, Journey(9)),
                (JourneySourceKind.Service, Journey(3)));

            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source);
            Assert.AreEqual(9, resolution.Document.revision);
            Assert.AreEqual(CandidateVerdict.Superseded,
                resolution.VerdictFor(JourneySourceKind.Service));
        }

        [Test]
        public void TheLastGoodFetchRunsWhenTheServiceIsGone()
        {
            // Yesterday's field test published r7; today the laptop is shut. The cache is not a
            // fallback of last resort, it is simply another numbered candidate.
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, Journey(4)),
                (JourneySourceKind.Cache, Journey(7)));

            Assert.AreEqual(JourneySourceKind.Cache, resolution.Source);
            Assert.AreEqual(7, resolution.Document.revision);
        }

        [Test]
        public void EqualRevisionsKeepTheBundledCopy()
        {
            // The bundled copy is the only one whose audio is guaranteed to be beside it.
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, Journey(6)),
                (JourneySourceKind.Cache, Journey(6)),
                (JourneySourceKind.Service, Journey(6)));

            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source);
        }

        [Test]
        public void AnUncalibratedJourneyIsRefusedOnDevice()
        {
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, Journey(2, calibrated: false)));

            Assert.IsFalse(resolution.HasJourney,
                "uncalibrated coordinates against a real anchor put every beat in the wrong place");
            Assert.AreEqual(CandidateVerdict.Uncalibrated,
                resolution.VerdictFor(JourneySourceKind.Bundled));
        }

        [Test]
        public void AnUncalibratedJourneyRunsInSimulation()
        {
            // Which is where uncalibrated coordinates are supposed to be exercised — and today
            // is where every journey in the repo stands.
            var resolution = Resolve(JourneyEnvironment.Simulation,
                (JourneySourceKind.Bundled, Journey(2, calibrated: false)));

            Assert.IsTrue(resolution.HasJourney);
            Assert.AreEqual(2, resolution.Document.revision);
        }

        [Test]
        public void ACalibratedBuildIsNotOvertakenByAnUncalibratedFetch()
        {
            // Re-seeding clears the calibration flag. A newer revision number must not be
            // enough on its own to put provisional coordinates back on a device.
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, Journey(4)),
                (JourneySourceKind.Service, Journey(5, calibrated: false)));

            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source);
            Assert.AreEqual(4, resolution.Document.revision);
        }

        [Test]
        public void AnUnsupportedSchemaVersionIsRefusedRatherThanHalfRead()
        {
            var resolution = Resolve(JourneyEnvironment.Simulation,
                (JourneySourceKind.Bundled, Journey(1)),
                (JourneySourceKind.Service, Journey(9, schemaVersion: "3.0")));

            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source,
                "a document from another schema must not win on its revision number");
            Assert.AreEqual(CandidateVerdict.Unreadable,
                resolution.VerdictFor(JourneySourceKind.Service));
        }

        [Test]
        public void MalformedJsonIsRefusedAndTheRestStillResolves()
        {
            var resolution = Resolve(JourneyEnvironment.Simulation,
                (JourneySourceKind.Bundled, Journey(3)),
                (JourneySourceKind.Service, "{ this is not json"));

            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source);
            Assert.AreEqual(CandidateVerdict.Unreadable,
                resolution.VerdictFor(JourneySourceKind.Service));
        }

        [Test]
        public void ATruncatedDocumentIsRefused()
        {
            // What a cache file written by a process that was killed halfway looks like.
            string truncated = Journey(8).Substring(0, Journey(8).Length / 2);

            var resolution = Resolve(JourneyEnvironment.Simulation,
                (JourneySourceKind.Bundled, Journey(3)),
                (JourneySourceKind.Cache, truncated));

            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source);
            Assert.AreEqual(CandidateVerdict.Unreadable,
                resolution.VerdictFor(JourneySourceKind.Cache));
        }

        [Test]
        public void AJourneyForAnotherSiteIsRefused()
        {
            // Revision numbers are per site, so Berkeley's r7 is not newer than UBC's r3 — it
            // is not comparable at all. A mistyped host is an easy way to arrive here.
            var resolution = Resolve(JourneyEnvironment.Simulation,
                (JourneySourceKind.Bundled, Journey(3)),
                (JourneySourceKind.Service, Journey(7, slug: "ucb-strawberry-creek-south")));

            Assert.AreEqual(JourneySourceKind.Bundled, resolution.Source);
            Assert.AreEqual(CandidateVerdict.WrongSite,
                resolution.VerdictFor(JourneySourceKind.Service));
        }

        [Test]
        public void NothingAcceptableYieldsNoJourneyAndSaysSo()
        {
            var resolution = Resolve(JourneyEnvironment.Device,
                (JourneySourceKind.Bundled, "{}"),
                (JourneySourceKind.Cache, ""));

            Assert.IsFalse(resolution.HasJourney);
            Assert.AreEqual(CandidateVerdict.Unreadable,
                resolution.VerdictFor(JourneySourceKind.Bundled));
            Assert.AreEqual(CandidateVerdict.Unreadable,
                resolution.VerdictFor(JourneySourceKind.Cache));
        }

        [Test]
        public void ADocumentWithoutHysteresisIsRefused()
        {
            // The service will not publish one, so seeing it means the document did not come
            // from the service — or that these fields no longer mean what they meant.
            string broken = Journey(3).Replace("\"exitRadiusM\": 3.04", "\"exitRadiusM\": 1.9");

            var resolution = Resolve(JourneyEnvironment.Simulation,
                (JourneySourceKind.Bundled, broken));

            Assert.IsFalse(resolution.HasJourney);
        }

        [Test]
        public void ADocumentWithoutACentrelineIsRefused()
        {
            // Every trigger is a distance along the centreline. Without one nothing can fire,
            // and the app would launch into a silent creek rather than into an error.
            string broken = Journey(3).Replace("{ \"x\": 0, \"y\": 0, \"z\": 0 },", "");

            Assert.IsFalse(JourneyParser.TryParse(broken, out _, out string error));
            StringAssert.Contains("centreline", error);
        }
    }
}
