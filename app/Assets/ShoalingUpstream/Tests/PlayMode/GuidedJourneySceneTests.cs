using System.Collections;
using NUnit.Framework;
using ShoalingUpstream.Experience;
using ShoalingUpstream.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShoalingUpstream.Tests
{
    public sealed class GuidedJourneySceneTests
    {
        [UnityTest]
        public IEnumerator FullScriptSupportsRepeatsIgnoreRepeatedJumpsAndFinishes()
        {
            yield return SceneManager.LoadSceneAsync("Assets/ShoalingUpstream/Scenes/StandaloneAR.unity");
            var driver = Object.FindFirstObjectByType<SimulationDriver>();
            var player = driver.GetComponent<StandalonePlayer>();
            var guide = driver.GetComponent<GuidedJourneyPlayer>();
            Assert.That(guide, Is.Not.Null);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!driver.Ready && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(driver.Ready, Is.True);
            Assert.That(player.VisibleActions().Count, Is.EqualTo(1));
            Assert.That(player.VisibleActions()[0].Label, Is.EqualTo("Start the Journey"));
            player.StartExperience();
            Assert.That(player.VisibleActions(), Is.Empty); // no permanent overlay while listening
            player.OpeningAndChapterOne.OpeningEnded();
            player.ContinueToChapterOne();
            player.OpeningAndChapterOne.SearchEnded();
            player.ConfirmShelter(true);
            Assert.That(player.VisibleActions()[0].Selected, Is.True);
            player.ConfirmShelter(false);
            player.OpeningAndChapterOne.SpawnPromptEnded();
            player.SpawnNewLife();
            var audio = player.NarrationSource;
            float priorScale = Time.timeScale;
            bool shelterRepeated = false;
            int heronVisits = 0, returnVisits = 0, homeVisits = 0, rainbowCount = 0, poolFrames = 0;
            bool poolObserved = false, poolRejoined = false, heronScaleChecked = false, contactCaptured = false;
            int previousRemaining = 0;
            float nextDiagnostic = Time.realtimeSinceStartup + 8f;
            bool lastReturnWasJump = false, patienceObserved = false, growthObserved = false, departuresObserved = false;
            try
            {
                Time.timeScale = 20f;
                deadline = Time.realtimeSinceStartup + 120f;
                while (!guide.Finished && Time.realtimeSinceStartup < deadline)
                {
                    // End audio segments explicitly; the test validates content/animation wiring,
                    // not real-time narration boundaries or sound on an actual iPad.
                    audio.Stop();
                    if (guide.WaitingForHeronWalk)
                    {
                        Assert.That(guide.HeronWalkReachedAt, Is.LessThan(0f));
                        Camera.main.transform.position += Vector3.forward * 1.01f;
                    }
                    if (Time.realtimeSinceStartup >= nextDiagnostic)
                    {
                        Debug.Log($"[continuity test] chapter={guide.Chapter} stage={guide.ChapterTwoStage} gate={guide.Choices.Gate} {driver.LocalMotionReport()}");
                        nextDiagnostic = Time.realtimeSinceStartup + 8f;
                    }
                    Assert.That(driver.LocalError, Is.Null.Or.Empty);
                    Assert.That(guide.Error, Is.Null.Or.Empty);
                    if (guide.ChapterTwoStage == "Patience")
                    {
                        patienceObserved = true;
                        Assert.That(guide.PatienceStartTime - guide.PoolArrivalTime, Is.GreaterThanOrEqualTo(2.99f));
                        Assert.That(driver.LocalFlockCount("beat-4"), Is.Zero, "Growth starts after patience, not before");
                    }
                    if (guide.ChapterTwoStage == "Growing" && guide.Chapter == 2)
                    {
                        growthObserved = true;
                        Assert.That(guide.ActiveNarrationStart, Is.EqualTo(guide.Cues.GrowingPatienceEnd));
                        Assert.That(guide.GrowthStartTime, Is.GreaterThanOrEqualTo(guide.PatienceStartTime));
                    }
                    if (guide.Chapter == 5 && driver.LocalDepartingCount > 0)
                    {
                        departuresObserved = true;
                        Assert.That(driver.LocalFlockCount("beat-13"), Is.EqualTo(2));
                        bool right = false, left = false, forward = false, backward = false;
                        foreach (var heading in driver.LocalDepartureHeadings)
                        { right |= heading.x > .4f; left |= heading.x < -.4f; forward |= heading.z > .4f; backward |= heading.z < -.4f; }
                        Assert.That(right && left && forward && backward, Is.True, "Other fish disperse in every direction");
                    }
                    if (guide.PoolRejoinPending)
                    {
                        poolObserved = true;
                        Assert.That(guide.FollowingParticipant, Is.False);
                        if (++poolFrames == 3) Camera.main.transform.position += Vector3.forward * .6f;
                    }
                    else if (poolObserved && guide.Chapter == 3 && guide.FollowingParticipant) poolRejoined = true;
                    foreach (var fish in driver.LocalFlock("beat-14"))
                        if (fish != null) Assert.That(fish.position.y - driver.LocalGroundY, Is.LessThan(.45f), "Breeding pair stays close to ground");
                    if (guide.Chapter == 3 && driver.LocalHeronContactTime > 0f && driver.LocalFlockCount("beat-9") == 0)
                    {
                        Assert.That(driver.LocalFlockCount("beat-6"), Is.EqualTo(2), "Heron and nest stay until rainbow trout appear");
                        if (driver.LocalOfferedFish != null)
                        {
                            Transform beak = null;
                            foreach (var actor in driver.LocalFlock("beat-6"))
                                foreach (var bone in actor.GetComponentsInChildren<Transform>()) if (bone.name == "Bone.007") beak = bone;
                            Assert.That(beak, Is.Not.Null);
                            if (!contactCaptured && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                            {
                                var audit = new GameObject("Heron contact audit camera").AddComponent<Camera>();
                                audit.fieldOfView = 42f; audit.aspect = 1.5f;
                                audit.clearFlags = CameraClearFlags.SolidColor;
                                audit.backgroundColor = new Color(.38f, .46f, .5f);
                                audit.transform.position = beak.position + Vector3.right * 1.5f + Vector3.up * .35f;
                                audit.transform.LookAt(beak.position);
                                ShoalRefinementSceneTests.CaptureActualModels(audit, "steady-fish-heron-contact-runtime.png");
                                Object.Destroy(audit.gameObject); contactCaptured = true;
                            }
                            Assert.That(Vector3.Distance(driver.LocalOfferedFish.position, beak.TransformPoint(new Vector3(0f, .15f, 0f))),
                                Is.LessThan(.055f), "Fish stays in the live beak while the bird raises, turns and feeds");
                        }
                    }
                    switch (guide.Choices.Gate)
                    {
                        case JourneyGate.Shelter:
                            Assert.That(guide.VisibleActions()[0].Label, Is.EqualTo("Deep Pool Found"));
                            Assert.That(guide.VisibleActions()[1].Label, Is.EqualTo("Water Plants Found"));
                            if (!shelterRepeated)
                            {
                                guide.Choose(JourneyChoice.FirstFound);
                                guide.Choose(JourneyChoice.Repeat);
                                shelterRepeated = true;
                            }
                            else
                            {
                                Assert.That(guide.Choices.FirstFound, Is.True);
                                guide.Choose(JourneyChoice.SecondFound);
                            }
                            break;
                        case JourneyGate.Strider: guide.Choose(JourneyChoice.Receive); break;
                        case JourneyGate.Heron:
                            Assert.That(guide.HeronWalkReachedAt, Is.GreaterThanOrEqualTo(0f));
                            Assert.That(Time.time - guide.HeronWalkReachedAt, Is.GreaterThanOrEqualTo(14.99f));
                            if (!heronScaleChecked)
                            {
                                foreach (var actor in driver.LocalFlock("beat-6"))
                                {
                                    Vector3 relative = actor.position - Camera.main.transform.position;
                                    Assert.That(Vector3.Dot(relative, Vector3.forward), Is.GreaterThan(0f));
                                    Assert.That(Vector3.Dot(relative, Vector3.right), Is.GreaterThan(0f));
                                    var source = driver.SceneTriggers.Find(t => t.BeatId == "beat-6"
                                        && t.Template != null && actor.name.StartsWith(t.Template.name)).Template;
                                    Assert.That(source, Is.Not.Null);
                                    Vector3 expected = source.transform.Find("Mesh").localScale * .4f;
                                    Assert.That(Vector3.Distance(actor.Find("Mesh").localScale, expected), Is.LessThan(.0001f));
                                    foreach (var part in actor.GetComponentsInChildren<Transform>())
                                        if (part.name.StartsWith("Baby Bird"))
                                            {
                                                Assert.That(Vector3.Distance(part.localScale, source.transform.Find(part.name).localScale * .4f), Is.LessThan(.0001f));
                                                Assert.That(Vector3.Distance(part.localPosition, source.transform.Find(part.name).localPosition / 3f), Is.LessThan(.0001f));
                                            }
                                }
                                heronScaleChecked = true;
                            }
                            guide.Choose(heronVisits++ == 0 ? JourneyChoice.Ignore
                                : heronVisits == 2 ? JourneyChoice.Repeat : JourneyChoice.Offer);
                            break;
                        case JourneyGate.Return:
                            if (rainbowCount == 0) rainbowCount = driver.LocalFlockCount("beat-9");
                            Assert.That(rainbowCount, Is.GreaterThan(2));
                            int remaining = driver.LocalFlockCount("beat-9");
                            Assert.That(remaining, Is.InRange(2, rainbowCount));
                            if (lastReturnWasJump)
                            {
                                if (previousRemaining > 5) Assert.That(previousRemaining - remaining, Is.InRange(3, Mathf.Min(5, previousRemaining - 2)));
                                else Assert.That(remaining, Is.EqualTo(previousRemaining), "Stop attrition at five fish or fewer");
                            }
                            Assert.That(driver.LocalDetachedCount + remaining, Is.LessThanOrEqualTo(rainbowCount));
                            JourneyChoice next = remaining > 5 ? JourneyChoice.Jump
                                : returnVisits++ < 2 ? JourneyChoice.Jump
                                : returnVisits == 3 ? JourneyChoice.Repeat : JourneyChoice.Locate;
                            previousRemaining = remaining; lastReturnWasJump = next == JourneyChoice.Jump;
                            guide.Choose(next);
                            break;
                        case JourneyGate.Home:
                            guide.Choose(homeVisits++ == 0 ? JourneyChoice.Repeat : JourneyChoice.Home); break;
                        case JourneyGate.Spawn:
                            Assert.That(driver.LocalFlockCount("beat-13"), Is.EqualTo(2));
                            Assert.That(driver.LocalFlockCount("beat-9"), Is.Zero);
                            Assert.That(driver.LocalDetachedCount, Is.Zero);
                            guide.Choose(JourneyChoice.Spawn); break;
                    }
                    yield return null;
                }
                Assert.That(guide.Finished, Is.True, $"Stopped at chapter {guide.Chapter}, gate {guide.Choices.Gate}, stage {guide.ChapterTwoStage}, {driver.LocalMotionReport()}");
                Assert.That(patienceObserved && growthObserved && departuresObserved, Is.True);
                Assert.That(poolObserved, Is.True);
                Assert.That(poolRejoined, Is.True);
                Assert.That(heronScaleChecked, Is.True);
                Assert.That(heronVisits, Is.EqualTo(3));
                Assert.That(returnVisits, Is.EqualTo(4));
                Assert.That(homeVisits, Is.EqualTo(2));
                Assert.That(player.VisibleActions(), Is.Empty);
                Assert.That(driver.LocalFlockCount("beat-14"), Is.EqualTo(0));
            }
            finally { Time.timeScale = priorScale; }
        }
    }
}
