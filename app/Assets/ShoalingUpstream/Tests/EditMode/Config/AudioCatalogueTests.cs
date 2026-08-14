using System;
using System.IO;
using NUnit.Framework;
using ShoalingUpstream.Config;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// What the catalogue knows about the clips in the build, before anything is played.
    ///
    /// Decoding is not exercised here — that needs a real MP3 and a real audio device. What is
    /// exercised is everything that decides whether a beat will have a sound at all, because
    /// the failure mode of getting it wrong is silence, and silence is indistinguishable from
    /// a trigger that never fired.
    /// </summary>
    public class AudioCatalogueTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "shoaling-audio-" + Guid.NewGuid());
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, true);

        private void Clip(string clipId, int bytes = 64) =>
            File.WriteAllBytes(Path.Combine(_directory, clipId + ".mp3"), new byte[bytes]);

        private void Index(params (string clipId, long bytes)[] clips)
        {
            var entries = new System.Text.StringBuilder();
            for (int i = 0; i < clips.Length; i++)
            {
                if (i > 0) entries.Append(',');
                entries.Append(
                    $"{{\"clipId\":\"{clips[i].clipId}\",\"file\":\"{clips[i].clipId}.mp3\","
                    + $"\"bytes\":{clips[i].bytes}}}");
            }
            File.WriteAllText(
                Path.Combine(_directory, "index.json"), $"{{\"clips\":[{entries}]}}");
        }

        private static JourneyDocument Journey(string beatClip, string completionClip = null)
        {
            string completion = completionClip == null
                ? ""
                : $", \"completion\": {{ \"clipId\": \"{completionClip}\" }}";

            string json = $@"{{
              ""schemaVersion"": ""2.0"", ""journeyId"": ""t"", ""title"": ""t"", ""revision"": 1,
              ""site"": {{ ""slug"": ""t"", ""centreline"": [
                {{ ""x"": 0, ""y"": 0, ""z"": 0 }}, {{ ""x"": 0, ""y"": 0, ""z"": 1 }} ] }},
              ""editorFrame"": {{ ""calibrated"": true }},
              ""beats"": [ {{ ""id"": ""tree"", ""title"": ""t"", ""interaction"": ""proximity"",
                ""s"": 1, ""position"": {{ ""x"": 0, ""y"": 0, ""z"": 1 }},
                ""trigger"": {{ ""enterRadiusM"": 1.9, ""exitRadiusM"": 3.04,
                  ""dwellSeconds"": 1, ""minimumHoldSeconds"": 5 }},
                ""audio"": {{
                  ""far"": {{ ""clipId"": ""{beatClip}--far"" }},
                  ""mid"": {{ ""clipId"": ""{beatClip}--mid"" }},
                  ""intimate"": {{ ""clipId"": ""{beatClip}--intimate"" }}{completion}
                }} }} ],
              ""ambient"": [ {{ ""id"": ""creek-bed"", ""title"": ""t"",
                ""position"": {{ ""x"": 0, ""y"": 0, ""z"": 0 }}, ""audibleRadiusM"": 20,
                ""audio"": {{ ""far"": {{ ""clipId"": ""tree-creek-waterplants-2--far"" }} }} }} ],
              ""shoal"": {{ ""startingCount"": 40, ""minimumCount"": 6 }}
            }}";

            Assert.IsTrue(JourneyParser.TryParse(json, out var document, out string error), error);
            return document;
        }

        [Test]
        public void TheIndexIsWhatTheCatalogueKnows()
        {
            Clip("heron--far");
            Clip("heron--mid");
            Index(("heron--far", 64), ("heron--mid", 64));

            var catalogue = AudioCatalogue.FromDirectory(_directory);

            Assert.AreEqual(2, catalogue.ClipIds.Count);
            Assert.IsTrue(catalogue.Contains("heron--far"));
            Assert.IsFalse(catalogue.Contains("heron--intimate"));
            Assert.IsFalse(catalogue.Contains(null));
        }

        [Test]
        public void AFolderWithNoIndexIsStillUsable()
        {
            // A hand-assembled folder, or one the exporter has not been run against yet.
            Clip("jump");
            Clip("lay-egg");

            var catalogue = AudioCatalogue.FromDirectory(_directory);

            Assert.AreEqual(2, catalogue.ClipIds.Count);
            Assert.IsTrue(catalogue.Contains("jump"));
        }

        [Test]
        public void AnAbsentFolderIsEmptyRatherThanAnException()
        {
            var catalogue = AudioCatalogue.FromDirectory(
                Path.Combine(_directory, "never-packaged"));

            Assert.IsEmpty(catalogue.ClipIds);
            Assert.IsFalse(catalogue.Contains("heron--far"));
        }

        [Test]
        public void EveryLayerOfEveryBeatAndAmbientSourceIsCounted()
        {
            var referenced = AudioCatalogue.ReferencedClipIds(Journey("heron", "lay-egg"));

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "heron--far", "heron--mid", "heron--intimate", "lay-egg",
                    "tree-creek-waterplants-2--far",
                },
                referenced);
        }

        [Test]
        public void ABeatWithNoCompletionSoundDoesNotAskForOne()
        {
            // JsonUtility fills in every serializable field whether or not the JSON had it, so
            // a beat with no completion still arrives carrying an empty completion layer. Left
            // unfiltered it becomes a clip id of "" that the exporter would go looking for.
            var referenced = AudioCatalogue.ReferencedClipIds(Journey("strider"));

            CollectionAssert.DoesNotContain(referenced, "");
            Assert.AreEqual(4, referenced.Count);
        }

        [Test]
        public void AClipTheJourneyPlaysButTheBuildLacksIsNamed()
        {
            // The case for a journey fetched from a laptop that has published a revision since
            // the last packaging run: it is legal, it wins on revision, and one beat of it
            // would have been silent.
            Clip("heron--far");
            Clip("heron--mid");
            Clip("heron--intimate");
            Clip("tree-creek-waterplants-2--far");
            Index(("heron--far", 64), ("heron--mid", 64), ("heron--intimate", 64),
                  ("tree-creek-waterplants-2--far", 64));

            var catalogue = AudioCatalogue.FromDirectory(_directory);
            var missing = catalogue.MissingClipIds(Journey("heron", "lay-egg"));

            CollectionAssert.AreEqual(new[] { "lay-egg" }, missing);
        }

        [Test]
        public void ACompleteBuildIsMissingNothing()
        {
            foreach (string clipId in AudioCatalogue.ReferencedClipIds(Journey("heron", "lay-egg")))
            {
                Clip(clipId);
            }

            var catalogue = AudioCatalogue.FromDirectory(_directory);

            Assert.IsEmpty(catalogue.MissingClipIds(Journey("heron", "lay-egg")));
        }

        [Test]
        public void AClipThatIsNotInTheBuildFailsWithAReasonRatherThanSilence()
        {
            var catalogue = AudioCatalogue.FromDirectory(_directory);

            var request = catalogue.Load("heron--far");

            Assert.IsTrue(request.IsDone);
            Assert.IsNull(request.Clip);
            StringAssert.Contains("heron--far", request.Error);
        }

        [Test]
        public void AClipShorterThanTheIndexSaysIsRefused()
        {
            // A half-copied file plays as a click or as nothing at all. Refusing it makes a
            // broken packaging run look like a broken packaging run.
            Clip("heron--far", bytes: 32);
            Index(("heron--far", 91677));

            var request = AudioCatalogue.FromDirectory(_directory).Load("heron--far");

            Assert.IsTrue(request.IsDone);
            Assert.IsNull(request.Clip);
            StringAssert.Contains("repackage", request.Error);
        }
    }
}
