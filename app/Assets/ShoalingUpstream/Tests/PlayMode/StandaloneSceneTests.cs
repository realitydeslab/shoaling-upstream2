using System.Collections;
using NUnit.Framework;
using ShoalingUpstream.Simulation;
using ShoalingUpstream.Experience;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShoalingUpstream.Tests
{
    public sealed class StandaloneSceneTests
    {
        [UnityTest]
        public IEnumerator EveryLifeStageFadesLinearlyThenRestoresItsOpaqueShader()
        {
            yield return SceneManager.LoadSceneAsync("Assets/ShoalingUpstream/Scenes/StandaloneAR.unity");
            var driver = Object.FindFirstObjectByType<SimulationDriver>();
            float limit = Time.realtimeSinceStartup + 10f;
            while (!driver.Ready && Time.realtimeSinceStartup < limit) yield return null;
            var method = typeof(SimulationDriver).GetMethod("CrossfadeLifeStage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            foreach (string id in new[] { "beat-2", "beat-4", "beat-9" })
            {
                var template = driver.SceneTriggers.Find(t => t.BeatId == id).FishPrefab;
                var fish = Object.Instantiate(template);
                var previous = Object.Instantiate(template);
                var material = fish.GetComponentInChildren<Renderer>().material;
                var shader = material.shader;
                var fade = driver.StartCoroutine((IEnumerator)method.Invoke(driver, new object[] { previous.transform, fish.transform, false }));
                float start = Time.time;
                string alpha = material.HasProperty("baseColorFactor") ? "baseColorFactor" : material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                Assert.That(material.GetColor(alpha).a, Is.LessThan(.03f));
                while (Time.time - start < 1f) yield return null;
                Assert.That(material.GetColor(alpha).a, Is.EqualTo(id == "beat-2" ? .45f : .5f).Within(.06f));
                yield return fade;
                Assert.That(Time.time - start, Is.InRange(1.95f, 2.15f));
                Assert.That(material.GetColor(alpha).a, Is.EqualTo(id == "beat-2" ? .9f : 1f));
                if (id != "beat-2") Assert.That(material.shader, Is.EqualTo(shader));
                Assert.That(material.renderQueue, id == "beat-2" ? Is.EqualTo(3000) : Is.LessThan(3000));
                Assert.That(material.IsKeywordEnabled("_ALPHABLEND_ON"), Is.EqualTo(id == "beat-2"));
                Object.Destroy(fish);
            }
        }

        [UnityTest]
        public IEnumerator SpawnAutomaticallyDropsWaitsSixSecondsAndHatchesWithoutAnotherPress()
        {
            yield return SceneManager.LoadSceneAsync("Assets/ShoalingUpstream/Scenes/StandaloneAR.unity");
            var driver = Object.FindFirstObjectByType<SimulationDriver>();
            var player = driver.GetComponent<StandalonePlayer>();
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!driver.Ready && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(driver.Ready, Is.True);
            Assert.That(player.SearchStartSeconds, Is.GreaterThan(0f));
            Assert.That(player.SearchEndSeconds, Is.GreaterThan(player.SearchStartSeconds));
            Assert.That(player.SpawnPromptEndSeconds, Is.LessThan(player.Narration[1].length));
            // Audio cut points are inspected separately; inject completion events to test the
            // actual scene's complete Spawn -> drop -> wait -> hatch -> narration transition.
            Assert.That(player.StartExperience(), Is.True);
            player.OpeningAndChapterOne.OpeningEnded();
            Assert.That(player.ContinueToChapterOne(), Is.True);
            player.OpeningAndChapterOne.SearchEnded();
            player.ConfirmShelter(false);
            player.ConfirmShelter(true);
            player.OpeningAndChapterOne.SpawnPromptEnded();
            Assert.That(player.SpawnNewLife(), Is.True);
            Assert.That(player.SpawnNewLife(), Is.False);
            float priorScale = Time.timeScale;
            try
            {
                Time.timeScale = 5f;
                deadline = Time.realtimeSinceStartup + 20f;
                while (player.OpeningAndChapterOne.State == OpeningChapterOneState.EggsDropping
                    && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(player.OpeningAndChapterOne.State, Is.EqualTo(OpeningChapterOneState.EggWait));
                float landedAt = Time.time;
                yield return new WaitForSeconds(5f);
                Assert.That(player.OpeningAndChapterOne.State, Is.EqualTo(OpeningChapterOneState.EggWait));
                deadline = Time.realtimeSinceStartup + 20f;
                while (player.OpeningAndChapterOne.State == OpeningChapterOneState.EggWait
                    && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(Time.time - landedAt, Is.GreaterThanOrEqualTo(5.9f));
                Assert.That(player.OpeningAndChapterOne.State, Is.EqualTo(OpeningChapterOneState.Hatching));
                deadline = Time.realtimeSinceStartup + 35f; // slower swimming includes the turn before rejoining
                while (player.OpeningAndChapterOne.State == OpeningChapterOneState.Hatching
                    && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(driver.LocalError, Is.Null.Or.Empty);
                Assert.That(player.OpeningAndChapterOne.State, Is.EqualTo(OpeningChapterOneState.ChapterOneEndingAudio).Or.EqualTo(OpeningChapterOneState.Complete));
                Assert.That(player.Sequence.Current.BeatId, Is.EqualTo("beat-3"));
                Assert.That(player.SpawnNewLife(), Is.False);
            }
            finally { Time.timeScale = priorScale; }
        }

        [UnityTest]
        public IEnumerator LocalSceneLoadsWithoutServiceAndLocksDuplicateAnimation()
        {
            yield return SceneManager.LoadSceneAsync("Assets/ShoalingUpstream/Scenes/StandaloneAR.unity");
            var driver = Object.FindFirstObjectByType<SimulationDriver>();
            Assert.That(driver, Is.Not.Null);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!driver.Ready && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(driver.Ready, Is.True, driver.LocalError);
            Assert.That(driver.LocalOnly, Is.True);
            Assert.That(driver.Link.ConnectOnStart, Is.False);
            Assert.That(driver.LocalJourney, Is.Not.Null);
            Assert.That(driver.Journey.site.slug, Is.EqualTo("ucb-strawberry-creek-south"));
            Assert.That(driver.Journey.beats.Count, Is.EqualTo(17));
            var player = driver.GetComponent<StandalonePlayer>();
            Assert.That(player.Narration.Length, Is.EqualTo(6));
            foreach (var clip in player.Narration)
            {
                Assert.That(clip, Is.Not.Null);
                Assert.That(clip.length, Is.GreaterThan(1f));
            }
            float previousScale = Time.timeScale;
            try
            {
                Time.timeScale = 5f;
            Assert.That(driver.FireLocally("beat-1"), Is.True);
            Assert.That(driver.LocalSceneBusy, Is.True);
            Assert.That(driver.FireLocally("beat-1"), Is.False);
            Assert.That(driver.FireLocally("beat-2"), Is.False);
            deadline = Time.realtimeSinceStartup + 45f;
            while (driver.LocalSceneBusy && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(driver.LocalSceneBusy, Is.False, driver.LocalError + driver.LocalMotionReport());
            Assert.That(driver.LocalError, Is.Null.Or.Empty);
            Assert.That(driver.FireLocally("beat-1"), Is.False);
            Assert.That(driver.FireLocally("beat-2"), Is.True);
            deadline = Time.realtimeSinceStartup + 45f;
            while (driver.LocalSceneBusy && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(driver.LocalSceneBusy, Is.False, driver.LocalError + driver.LocalMotionReport());
            Assert.That(driver.LocalError, Is.Null.Or.Empty);
            }
            finally { Time.timeScale = previousScale; }

        }
    }
}

