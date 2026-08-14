using System;
using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using ShoalingUpstream.Control;
using ShoalingUpstream.Journey;
using ShoalingUpstream.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShoalingUpstream.Tests.Integration
{
    /// <summary>
    /// The scene itself, loaded as the artist would load it, driven as the browser would drive it.
    ///
    /// Everything else in this folder builds its objects in code, which is exactly why those tests
    /// passed for a week while the project had no scene at all. This one loads
    /// Scenes/Simulation.unity and asserts on what the scene's own components did — so a scene
    /// that is missing a component, or wired to the wrong host, or never gets a journey, fails
    /// here rather than in front of the artist.
    /// </summary>
    [TestFixture]
    public sealed class SimulationSceneTests
    {
        private const string ScenePath = "Assets/ShoalingUpstream/Scenes/Simulation.unity";

        private LiveService _service;
        private OperatorSocket _operator;
        private SimulationDriver _driver;

        [OneTimeSetUp]
        public void StartService()
        {
            // No publish. The scene asks for the DRAFT, which is the point of it — so this
            // fixture deliberately never publishes anything, and if the scene were reading
            // published revisions it would find none and fail.
            _service = LiveService.Start();
        }

        [OneTimeTearDown]
        public void StopService() => _service?.Dispose();

        [UnitySetUp]
        public IEnumerator LoadTheScene()
        {
            SimulationDriver.HostOverride = "127.0.0.1";
            SimulationDriver.PortOverride = _service.Port;

            SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
            yield return null;   // the load lands at the end of the frame
            yield return null;

            _driver = UnityEngine.Object.FindAnyObjectByType<SimulationDriver>();
            Assert.That(_driver, Is.Not.Null,
                        $"{ScenePath} has no SimulationDriver — rebuild it with "
                        + "-executeMethod ShoalingUpstream.EditorTools.SimulationSceneBuilder.BuildFromCommandLine");

            _operator = OperatorSocket.Connect(_service.OperatorWsUrl);
            yield return Until(() => _driver.Ready, "the scene to load its journey and start", 25f);
        }

        [UnityTearDown]
        public IEnumerator UnloadTheScene()
        {
            _operator?.Dispose();
            _operator = null;
            SimulationDriver.HostOverride = null;
            SimulationDriver.PortOverride = 0;
            yield return null;
        }

        [Test]
        public void TheSceneIsInTheBuildSettings()
        {
            // Without this the artist presses Play on whatever scene happens to be open, and the
            // one that was built for them is a file they have to go and find.
            Assert.That(SceneUtility.GetBuildIndexByScenePath(ScenePath), Is.GreaterThanOrEqualTo(0),
                        $"{ScenePath} is not in EditorBuildSettings");
        }

        [UnityTest]
        public IEnumerator TheSceneWiresTheWholePiece()
        {
            Assert.That(_driver.Link, Is.Not.Null, "no ControlLink on the rig");
            Assert.That(_driver.Journey, Is.Not.Null, "no journey was loaded");
            Assert.That(_driver.Progression, Is.Not.Null, "no trigger machine was started");

            // The DRAFT, not the stale published revision. Sixteen points and 18.8 m is the reach
            // the artist is editing; three points and 34 m is the seeded layout they are not.
            Assert.That(_driver.Journey.site.slug, Is.EqualTo("ubc-nitobe-garden-creek"));
            Assert.That(_driver.Journey.site.centreline.Count, Is.GreaterThan(3),
                        "the scene loaded a 3-point centreline — that is the superseded revision, "
                        + "so it is reading published rather than the draft");
            Assert.That(_driver.Journey.beats.Exists(b => b.id == "falls"), Is.True,
                        "the draft's later beats are missing — this is not the current artwork");

            yield return Until(() => _driver.Link.Client.State == ControlLinkState.Online,
                               "the scene's own ControlLink to reach the service", 15f);
        }

        /// <summary>
        /// The artist's actual question: scrub in the browser, does Unity follow.
        ///
        /// The poses here are the frames `ControlBus.streamPose` broadcasts, sent from a real
        /// operator socket to the scene's own ControlLink — nothing in the scene is reached into
        /// except to read what it did.
        /// </summary>
        [UnityTest]
        [Timeout(90000)]
        public IEnumerator ScrubbingTheBrowserMovesTheWalkerAndArmsABeat()
        {
            yield return Until(() => _driver.Link.Client.State == ControlLinkState.Online,
                               "the scene's ControlLink to come Online", 15f);

            var beat = _driver.Journey.beats.Find(b => b.id == "tree");
            Assert.That(beat, Is.Not.Null);
            Assert.That(_driver.Progression.StateOf(beat.id), Is.EqualTo(BeatState.Idle),
                        "nothing should have fired before the walk starts");

            float startS = _driver.Progression.S;
            float s = 0f;
            double lastSentAt = 0;
            double deadline = Time.realtimeSinceStartupAsDouble + 30.0;

            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                s = Mathf.Min(beat.s, s + 1.2f * Time.deltaTime);
                if (Time.realtimeSinceStartupAsDouble - lastSentAt > 0.05)
                {
                    lastSentAt = Time.realtimeSinceStartupAsDouble;
                    Vector3 point = Centreline.PointAt(s, _driver.Journey.site.centreline);
                    _operator.Send("{\"type\":\"pose\","
                                   + $"\"s\":{R(s)},"
                                   + $"\"position\":{{\"x\":{R(point.x)},\"y\":{R(point.y)},\"z\":{R(point.z)}}},"
                                   + "\"headingRad\":0,\"slug\":\"ubc-nitobe-garden-creek\"}");
                }

                if (_driver.Progression.StateOf(beat.id) == BeatState.Complete) break;
                yield return null;
            }

            // The walker moved, driven by nothing but frames off the socket.
            Assert.That(_driver.Progression.S, Is.GreaterThan(startS + 1f),
                        "the scene's trigger machine never moved along the reach");

            // And the right beat responded.
            Assert.That(_driver.Progression.StateOf(beat.id), Is.EqualTo(BeatState.Complete),
                        $"'{beat.id}' did not complete; it is {_driver.Progression.StateOf(beat.id)} "
                        + $"at s={_driver.Progression.S:0.00}");
            Assert.That(_driver.Progression.Completed, Contains.Item(beat.id));

            // The camera is the visitor, so it must have gone with them — this is the thing the
            // artist is actually looking at.
            var eye = Camera.main;
            Assert.That(eye, Is.Not.Null, "the scene has no main camera");
            Assert.That(Vector3.Distance(eye.transform.position, Vector3.zero), Is.GreaterThan(1f),
                        "the camera never left the origin — the view would not have moved");
        }

        /// <summary>The operator's buttons reach the scene too, so the controller page is usable
        /// against it and not only against a test rig.</summary>
        [UnityTest]
        public IEnumerator TheControllerCanFireABeatIntoTheScene()
        {
            yield return Until(() => _driver.Link.Client.State == ControlLinkState.Online,
                               "the scene's ControlLink to come Online", 15f);

            _operator.Forget();
            _operator.Send("{\"type\":\"command\",\"action\":\"fireBeat\",\"beatId\":\"heron\"}");

            yield return Until(() => _driver.Progression.StateOf("heron") == BeatState.Complete,
                               "the operator's beat to fire in the scene", 10f);

            yield return Until(() =>
            {
                var ack = _operator.Latest("commandAck");
                return ack != null && ack["applied"].AsBool();
            }, "the scene to ack the command as applied", 8f);
        }

        /// <summary>Whatever the scene is doing is reported back, so the operator page shows the
        /// simulated walk rather than an empty panel.</summary>
        [UnityTest]
        public IEnumerator TheSceneReportsItselfToTheOperator()
        {
            yield return Until(() => _driver.Link.Client.State == ControlLinkState.Online,
                               "the scene's ControlLink to come Online", 15f);

            _operator.Send("{\"type\":\"pose\",\"s\":6.0,\"position\":null,"
                           + "\"headingRad\":0,\"slug\":\"ubc-nitobe-garden-creek\"}");

            yield return Until(() =>
            {
                var state = _operator.Latest("state");
                return state != null
                       && state["state"]["site"].AsString() == "ubc-nitobe-garden-creek"
                       && Mathf.Abs(state["state"]["s"].AsFloat() - 6f) < 0.3f;
            }, "the operator to see where the scene's walker is", 10f);

            var reported = _operator.Latest("state")["state"];
            Assert.That(reported["shoalCount"].AsDouble(), Is.GreaterThan(0),
                        "the shoal count should reach the operator");
            Assert.That(reported["localization"].AsString(), Is.EqualTo("precise"),
                        "a simulated walker is exactly where it says it is");
        }

        // ------------------------------------------------------------------ helpers

        private static string R(float value) =>
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

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
