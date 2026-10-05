using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using ShoalingUpstream.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShoalingUpstream.Tests
{
    public sealed class ShoalRefinementSceneTests
    {
        [UnityTest]
        public IEnumerator OrganicShoalSlowSteeringPoolAndRealStriderMeshesWorkTogether()
        {
            yield return SceneManager.LoadSceneAsync("Assets/ShoalingUpstream/Scenes/StandaloneAR.unity");
            var driver = Object.FindFirstObjectByType<SimulationDriver>();
            float deadline = Time.realtimeSinceStartup + 100f;
            while (!driver.Ready && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(driver.Ready, Is.True);
            driver.SetLocalTarget(new Vector3(0f, -1.2f, 2f));
            float previousScale = Time.timeScale;
            try
            {
                Time.timeScale = 20f;
                Assert.That(driver.FireLocally("beat-1"), Is.True);
                yield return Idle(driver, deadline);
                Assert.That(driver.LocalFlockCount("beat-1"), Is.EqualTo(40));
                Assert.That(driver.SceneTriggers.Find(t => t.BeatId == "beat-12").JumpHeightM, Is.EqualTo(.56f).Within(.001f));
                var eggs = driver.LocalFlock("beat-1");
                foreach (var egg in eggs)
                {
                    var renderer = egg.GetComponentInChildren<Renderer>();
                    Quaternion original = egg.localRotation;
                    egg.localRotation = Quaternion.identity;
                    float diameter = Mathf.Max(renderer.bounds.size.x, renderer.bounds.size.z);
                    egg.localRotation = original;
                    Assert.That(Quaternion.Angle(original, Quaternion.identity), Is.GreaterThan(.01f));
                    float nearest = float.MaxValue;
                    foreach (var other in eggs) if (other != egg) nearest = Mathf.Min(nearest, Vector3.Distance(egg.position, other.position));
                    Assert.That(nearest / diameter, Is.InRange(.98f, 1.53f));
                }
                Assert.That(driver.FireLocally("beat-2"), Is.True);
                yield return Idle(driver, deadline);
                var camera = Camera.main.transform;
                yield return FacingSettled(driver, "beat-2", deadline);
                CheckFormation(driver, "beat-2", camera);
                foreach (var fish in driver.LocalFlock("beat-2"))
                    foreach (var animation in fish.GetComponentsInChildren<Animation>())
                        foreach (AnimationState state in animation) Assert.That(state.speed, Is.EqualTo(.8f).Within(.001f));
                driver.FollowLocally(true); yield return null;
                Vector3 before = Centre(driver, "beat-2");
                camera.position += Vector3.right * .1f;
                camera.rotation = Quaternion.Euler(0f, 15f, 0f);
                for (int i = 0; i < 12; i++) { driver.FollowLocally(true); yield return null; }
                Assert.That(HorizontalDistance(Centre(driver, "beat-2"), before), Is.LessThan(.03f));
                Assert.That(Vector3.Angle(driver.LocalTravelHeading, Vector3.forward), Is.LessThan(.1f));
                camera.rotation = Quaternion.Euler(0f, 45f, 0f);
                Vector3 priorHead = driver.LocalFlock("beat-2")[0].TransformDirection(Vector3.left);
                driver.FollowLocally(true); yield return null;
                Assert.That(Vector3.Angle(priorHead, driver.LocalFlock("beat-2")[0].TransformDirection(Vector3.left)), Is.LessThan(20f));
                float until = Time.time + 40f;
                var positions = new Dictionary<Transform, Vector3>();
                while (Time.time < until)
                {
                    foreach (var fish in driver.LocalFlock("beat-2")) positions[fish] = fish.position;
                    driver.FollowLocally(true); yield return null;
                    foreach (var fish in driver.LocalFlock("beat-2"))
                    {
                        Vector3 delta = fish.position - positions[fish]; delta.y = 0f;
                        Assert.That(Vector3.Dot(delta, fish.TransformDirection(Vector3.left)), Is.GreaterThanOrEqualTo(-.002f));
                    }
                }
                Vector3 held = Centre(driver, "beat-2");
                Vector3 heldHead = driver.LocalFlock("beat-2")[0].TransformDirection(Vector3.left);
                Vector3 heldCameraPosition = camera.position;
                camera.rotation = Quaternion.Euler(0f, 225f, 180f); camera.position += Vector3.right * 2f;
                until = Time.time + 3f;
                while (Time.time < until) { driver.FollowLocally(true); yield return null; }
                Assert.That(HorizontalDistance(Centre(driver, "beat-2"), held), Is.LessThan(.05f));
                Assert.That(Vector3.Angle(heldHead, driver.LocalFlock("beat-2")[0].TransformDirection(Vector3.left)), Is.LessThan(.1f));
                camera.position = heldCameraPosition; camera.rotation = Quaternion.Euler(0f, 45f, 0f);
                driver.SetLocalTarget(camera.position + camera.forward * 1.3f - Vector3.up * .5f);
                Assert.That(driver.FireLocally("beat-3"), Is.True); yield return Idle(driver, deadline);
                Assert.That(driver.FireLocally("beat-4"), Is.True); yield return Idle(driver, deadline);
                driver.BeginPoolSwimmingLocally("beat-4");
                Assert.That(driver.LocalPoolSwimming, Is.True);
                Vector3 poolBefore = Centre(driver, "beat-4");
                until = Time.time + 25f;
                while (Time.time < until) yield return null;
                Assert.That(HorizontalDistance(Centre(driver, "beat-4"), poolBefore), Is.GreaterThan(.01f));
                Assert.That(driver.FollowPoolFryLocally("beat-4"), Is.True);
                Assert.That(driver.LocalPoolSwimming, Is.False);
                until = Time.time + 35f;
                while (Time.time < until) { driver.FollowLocally(true); yield return null; }
                Debug.Log($"[follow audit] before eye={camera.position} centre={Centre(driver, "beat-4")} head={driver.LocalFlock("beat-4")[0].TransformDirection(Vector3.left)} travel={driver.LocalTravelHeading}");
                Time.timeScale = 1f;
                until = Time.time + 6f;
                Vector3 walking = Vector3.ProjectOnPlane(camera.forward, Vector3.up).normalized;
                while (Time.time < until)
                {
                    camera.position += walking * (.65f * Time.deltaTime);
                    driver.FollowLocally(true);
                    Assert.That(driver.LocalSceneBusy, Is.False, "Walking follow does not stall narration or buttons");
                    yield return null;
                }
                Debug.Log($"[follow audit] after eye={camera.position} centre={Centre(driver, "beat-4")} head={driver.LocalFlock("beat-4")[0].TransformDirection(Vector3.left)} travel={driver.LocalTravelHeading} {_WalkingDiagnostics(driver)}");
                Assert.That(Vector3.Dot(Centre(driver, "beat-4") - camera.position, walking), Is.GreaterThan(0f), "Fry follow in front during continuous walking");
                Time.timeScale = 20f;
                until = Time.time + 35f;
                while (Time.time < until) { driver.FollowLocally(true); yield return null; }
                yield return FacingSettled(driver, "beat-4", deadline);
                CheckFormation(driver, "beat-4", camera);
                camera.rotation = Quaternion.Euler(45f, 45f, 0f); // Typical downward creek viewing pose.
                Assert.That(driver.FireLocally("beat-5"), Is.True); yield return Idle(driver, deadline);
                Assert.That(driver.LocalFlockCount("beat-5"), Is.EqualTo(3));
                var striders = new List<Transform>(driver.LocalFlock("beat-5"));
                foreach (var strider in striders)
                {
                    foreach (var fish in driver.LocalFlock("beat-4"))
                        Assert.That(Vector3.Dot(strider.position - driver.LocalFishMouthPosition(fish), camera.forward), Is.GreaterThan(.1f), "All Striders are beyond the fry shoal");
                    Assert.That(strider.position.y, Is.EqualTo(striders[0].position.y).Within(.001f));
                    Assert.That(strider.position.y, Is.EqualTo(driver.LocalGroundY + .05f).Within(.001f));
                    var renderers = strider.GetComponentsInChildren<SkinnedMeshRenderer>();
                    Assert.That(renderers.Length, Is.GreaterThan(0));
                    foreach (var renderer in renderers)
                    {
                        Assert.That(renderer.enabled && renderer.gameObject.activeInHierarchy, Is.True);
                        Assert.That(renderer.sharedMesh.vertexCount, Is.GreaterThan(100));
                        Assert.That(renderer.bounds.size.magnitude, Is.GreaterThan(.05f));
                        Assert.That(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(Camera.main), renderer.bounds), Is.True);
                        for (int corner = 0; corner < 8; corner++)
                        {
                            Vector3 world = renderer.bounds.center + Vector3.Scale(renderer.bounds.extents,
                                new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                            var visible = Camera.main.WorldToViewportPoint(world);
                            Assert.That(visible.x, Is.InRange(.03f, .97f));
                            Assert.That(visible.y, Is.InRange(.03f, .97f));
                            Assert.That(visible.z, Is.GreaterThan(.1f));
                        }
                        foreach (var material in renderer.sharedMaterials) Assert.That(material != null && material.shader != null, Is.True);
                    }
                }
                CaptureActualModels(Camera.main, "steady-fish-striders-runtime.png");
                var returns = new Dictionary<Transform, Vector3>();
                foreach (var fish in driver.LocalFlock("beat-4")) returns[fish] = fish.position;
                var sizes = driver.LocalFlock("beat-4")[0].localScale;
                Assert.That(driver.FireLocally("beat-7"), Is.True);
                var eatenTimes = new List<float>();
                var observed = new HashSet<int>();
                var starts = new Dictionary<Transform, Vector3>();
                var reached = new Dictionary<Transform, float>();
                var slid = new HashSet<Transform>();
                foreach (var strider in striders) starts[strider] = strider.position;
                while (driver.LocalFlockCount("beat-5") > 0 && Time.realtimeSinceStartup < deadline)
                {
                    foreach (var strider in striders)
                    {
                        if (strider == null) continue;
                        float noseDistance = float.MaxValue;
                        foreach (var fish in driver.LocalFlock("beat-4"))
                            noseDistance = Mathf.Min(noseDistance, Vector3.Distance(driver.LocalFishMouthPosition(fish), strider.position));
                        if (noseDistance < .18f && !reached.ContainsKey(strider)) reached[strider] = Time.time;
                        if (Vector3.Distance(strider.position, starts[strider]) > .01f && slid.Add(strider))
                        {
                            Assert.That(reached.ContainsKey(strider), Is.True, "A fish head reaches the Strider before it slides");
                            Assert.That(Time.time - reached[strider], Is.GreaterThanOrEqualTo(.95f), "Strider waits a second at the fish head");
                        }
                    }
                    for (int i = 0; i < striders.Count; i++)
                        if (striders[i] == null && observed.Add(i)) eatenTimes.Add(Time.time);
                    yield return null;
                }
                for (int i = 0; i < striders.Count; i++)
                    if (striders[i] == null && observed.Add(i)) eatenTimes.Add(Time.time);
                Assert.That(eatenTimes.Count, Is.EqualTo(3));
                Assert.That(slid.Count, Is.EqualTo(3), "All three Striders slide into a fish mouth");
                eatenTimes.Sort();
                Assert.That(eatenTimes[1] - eatenTimes[0], Is.GreaterThanOrEqualTo(.9f));
                Assert.That(eatenTimes[2] - eatenTimes[1], Is.GreaterThanOrEqualTo(.9f));
                yield return Idle(driver, deadline);
                Assert.That(driver.LocalShoalGrowthStart - driver.LocalStriderReturnsTime, Is.GreaterThanOrEqualTo(2.99f));
                Assert.That(Time.time - driver.LocalShoalGrowthStart, Is.GreaterThanOrEqualTo(2.99f));
                Assert.That(driver.LocalFlockCount("beat-7"), Is.EqualTo(40));
                Assert.That(Time.time - eatenTimes[2], Is.GreaterThanOrEqualTo(3f));
                foreach (var fish in driver.LocalFlock("beat-7"))
                {
                    Assert.That(fish.localScale.x, Is.GreaterThan(sizes.x));
                    Assert.That(Vector3.Distance(fish.position, returns[fish]), Is.LessThan(.1f), "Eating fish must swim back to the shoal");
                }
            }
            finally { Time.timeScale = previousScale; }
        }
        public static void CaptureActualModels(Camera camera, string name)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var previous = camera.targetTexture;
            var active = RenderTexture.active;
            float aspect = camera.aspect;
            var texture = new RenderTexture(Mathf.RoundToInt(960 * aspect), 960, 24);
            camera.targetTexture = texture; camera.aspect = aspect;
            camera.Render(); RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply();
            string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../../validation"));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, name), image.EncodeToPNG());
            camera.targetTexture = previous; RenderTexture.active = active;
            Object.Destroy(texture); Object.Destroy(image);
        }
        private static string _WalkingDiagnostics(SimulationDriver driver)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            return $"speed={typeof(SimulationDriver).GetField("_walkingSpeed", flags).GetValue(driver)} samples={typeof(SimulationDriver).GetField("_walkSamples", flags).GetValue(driver)}";
        }
        private static IEnumerator FacingSettled(SimulationDriver driver, string beat, float deadline)
        {
            Assert.That(driver.LocalSceneBusy, Is.False, "Cosmetic facing must not hold the next button/narration");
            bool unsettled = true;
            while (unsettled && Time.realtimeSinceStartup < deadline)
            {
                unsettled = driver.LocalSettlingCount > 0;
                foreach (var fish in driver.LocalFlock(beat))
                    if (Vector3.Angle(fish.TransformDirection(Vector3.left), driver.LocalTravelHeading) > 2.6f) unsettled = true;
                if (unsettled) { driver.FollowLocally(true); yield return null; }
            }
        }
        private static IEnumerator Idle(SimulationDriver driver, float deadline)
        {
            while (driver.LocalSceneBusy && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(driver.LocalSceneBusy, Is.False, "Animation did not finish");
            Assert.That(driver.LocalError, Is.Null.Or.Empty);
        }
        private static void CheckFormation(SimulationDriver driver, string beat, Transform camera)
        {
            float low = float.MaxValue, high = float.MinValue;
            foreach (var fish in driver.LocalFlock(beat))
            {
                Assert.That(Vector3.Distance(fish.position, camera.position), Is.InRange(.45f, 2.06f));
                Assert.That(fish.position.y, Is.InRange(driver.LocalGroundY, camera.position.y + .03f));
                Assert.That(Vector3.Angle(fish.TransformDirection(Vector3.left), driver.LocalTravelHeading), Is.LessThanOrEqualTo(2.6f));
                Assert.That(Vector3.Dot(fish.up, Vector3.up), Is.GreaterThan(.999f));
                foreach (var renderer in fish.GetComponentsInChildren<Renderer>())
                    foreach (var material in renderer.materials) Assert.That(material.renderQueue, beat == "beat-2" ? Is.EqualTo(3000) : Is.LessThan(3000));
                if (beat == "beat-2") Assert.That(Vector3.ProjectOnPlane(fish.position - camera.position, Vector3.up).magnitude, Is.InRange(.45f, 1.06f));
                low = Mathf.Min(low, fish.position.y); high = Mathf.Max(high, fish.position.y);
            }
            Assert.That(high - low, Is.GreaterThan(beat == "beat-2" ? .025f : .25f), "Formation must have random depths, not one plane");
        }
        private static float HorizontalDistance(Vector3 a, Vector3 b) { a.y = b.y; return Vector3.Distance(a, b); }
        private static Vector3 Centre(SimulationDriver driver, string beat)
        {
            Vector3 centre = Vector3.zero; foreach (var fish in driver.LocalFlock(beat)) centre += fish.position;
            return centre / driver.LocalFlockCount(beat);
        }
    }
}
