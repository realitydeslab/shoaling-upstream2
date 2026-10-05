using System.Collections.Generic;
using System.IO;
using ShoalingUpstream.Audio;
using ShoalingUpstream.Control;
using ShoalingUpstream.Simulation;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
#if NSDK_PRESENT
using ShoalingUpstream.Localization.Nsdk;
#endif

namespace ShoalingUpstream.EditorTools
{
    /// <summary>
    /// Builds Scenes/Simulation.unity from nothing, and registers it in the build settings.
    ///
    /// A scene is a binary-ish YAML blob that nobody reviews and nobody can reconstruct. Written
    /// by a script it is reproducible, it diffs as code, and it can be regenerated in batch mode
    /// on a machine with no GUI — which is how this one was made, since the agent that wrote it
    /// cannot open the editor.
    ///
    /// Re-running it overwrites the scene. That is the intent: change this file, run it again,
    /// and the scene follows. Nothing should be hand-edited into the saved asset, because the
    /// next run would silently throw it away.
    /// </summary>
    public static class SimulationSceneBuilder
    {
        public const string ScenePath = "Assets/ShoalingUpstream/Scenes/Simulation.unity";
        public const string ArScenePath = "Assets/ShoalingUpstream/Scenes/AR.unity";

        // The laptop running `npm start`, on whatever network the iPads will actually be on —
        // it prints this address itself on startup ("on en0  http://<this>:8710/"). Baked in at
        // build time because there is nowhere on-device yet to type it in instead, so this is
        // the one line to change (and rebuild AR.unity, and re-export to Xcode) whenever that
        // network changes.
        private const string ArServiceHost = "192.168.8.125";

        private const string OrangeEggPath =
            "Assets/ShoalingUpstream/Models/New Born/ORANGE EGG.glb";
        private const string FishEggMeshPrefabPath =
            "Assets/ShoalingUpstream/Models/New Born/Fish Egg Mesh.prefab";
        private const string AlevinFishPath =
            "Assets/ShoalingUpstream/Models/New Born/ALEVIN FISH.glb";
        private const string AlevinFishPrefabPath =
            "Assets/ShoalingUpstream/Models/New Born/ALEVIN FISH.prefab";
        private const string FryPath =
            "Assets/ShoalingUpstream/Models/Grow/FRY.glb";
        private const string FryPrefabPath =
            "Assets/ShoalingUpstream/Models/Grow/FRY.prefab";
        private const string RainbowTroutPath =
            "Assets/ShoalingUpstream/Models/Rainbow Trout/Rainbow Trout.glb";
        private const string RainbowTroutPrefabPath =
            "Assets/ShoalingUpstream/Models/Rainbow Trout/Rainbow Trout.prefab";
        private const string HeronPath =
            "Assets/ShoalingUpstream/Models/Heron/HERON.glb";
        private const string BirdNestPath =
            "Assets/ShoalingUpstream/Models/Heron/BIRD NEST.glb";

        // The Strider and baby herons are Unity-native asset-store packs (Quirky Series), not
        // glTF imports — they arrive with a working Animator + AnimatorController already wired,
        // unlike glTFast's Mecanim path which never builds one. Used as-is rather than converted
        // to this project's usual Legacy Animation convention, since the mesh and its animation
        // clips live in separate FBX files sharing one Avatar; that split is exactly what
        // Mecanim's retargeting is for; Legacy's plain bone-path binding would need both files to
        // share an identical hierarchy, which was not worth gambling on for a purchased asset
        // pack in current use elsewhere in the project.
        private const string CricketMeshPath =
            "Assets/Quirky Series/Insect Bundle/Insect Vol.1/Prefabs/Single LODs/Cricket_LOD0.prefab";
        private const string KookaburraMeshPath =
            "Assets/Quirky Series Vol 2/Island/Kookaburra/Prefabs/Kookaburra_LOD0.prefab";

        [MenuItem("Shoaling Upstream/Rebuild Simulation Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildCamera();
            BuildLight();
            // true: the desk renderer, never PHASE at a laptop — see BuildRig.
            BuildRig(forceSimulationAudio: true, serviceHost: "127.0.0.1");

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)!);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Register(ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[scene] built {ScenePath}");
        }

        /// <summary>Entry point for `-executeMethod`, so the scene can be produced without anyone
        /// opening the GUI.</summary>
        public static void BuildFromCommandLine()
        {
            Build();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// The on-device counterpart to Build(): the exact same rig (every SceneTrigger,
        /// unchanged), fronted by an ARKit passthrough camera instead of the desktop's static one,
        /// so what plays here is provably the same show already proven on the desk.
        ///
        /// No VPS session anchor yet — deliberately. The rig's own camera-relative placement
        /// (BuildFishEggTrigger and friends spawn "in front of the eye") already works with
        /// whatever transform Camera.main resolves to, desktop or AR, so a first pass on a real
        /// device needs only a moving camera, not a localized one. VpsLocalizationRunner stays
        /// off SimulationDriver.Update's pose path unless it is fed a fix (see the #if
        /// NSDK_PRESENT branch there), so leaving it out here is inert, not a missing feature.
        /// </summary>
        [MenuItem("Shoaling Upstream/Rebuild AR Scene")]
        public static void BuildAr()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildArCamera();
            BuildLight();
            // false: a real PHASE render on the device this scene is actually built for.
            BuildRig(forceSimulationAudio: false, serviceHost: ArServiceHost);

            Directory.CreateDirectory(Path.GetDirectoryName(ArScenePath)!);
            EditorSceneManager.SaveScene(scene, ArScenePath);
            Register(ArScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[scene] built {ArScenePath}");
        }

        public static void BuildArFromCommandLine()
        {
            BuildAr();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public const string StandaloneScenePath = "Assets/ShoalingUpstream/Scenes/StandaloneAR.unity";

        [MenuItem("Shoaling Upstream/Rebuild Standalone AR Scene")]
        public static void BuildStandaloneAr()
        {
            BuildAr();
            var driver = Object.FindFirstObjectByType<SimulationDriver>();
            driver.LocalOnly = true;
            driver.LocalModelScale = 1f / 3f;
            driver.ShowDiagnostics = false;
            driver.LocalJourney = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/ShoalingUpstream/Standalone/Journey.json");
            driver.GetComponent<ControlLink>().ConnectOnStart = false;
            driver.gameObject.AddComponent<GuidedJourneyPlayer>();
            for (int i = 0; i < driver.SceneTriggers.Count; i++)
            {
                var trigger = driver.SceneTriggers[i];
                if (trigger.BeatId == "beat-1") trigger.Count = 40;
                if (trigger.BeatId == "beat-5") trigger.Count = 3;
                if (trigger.BeatId == "beat-4") trigger.HatchDurationSeconds = 8.4f;
                if (trigger.BeatId == "beat-8") trigger.FishSwimDurationSeconds *= 3f;
                if (trigger.BeatId == "beat-12") trigger.JumpHeightM *= .25f;
                if (trigger.BeatId == "beat-3") { trigger.SwimDurationSeconds = 5f; trigger.SwimScatterRadiusM = .6f; }
                if (trigger.BeatId == "beat-17") { trigger.SwimForwardDistanceM = 6f; trigger.SwimForwardDurationSeconds = 5f; }
                if (trigger.BeatId == "beat-15") trigger.SwimForwardDistanceM = 3f;
                driver.SceneTriggers[i] = trigger;
            }
            var player = driver.gameObject.AddComponent<StandalonePlayer>();
            // Calibrated against the user-selected 01 New Life recording (2026-10-04).
            player.SearchStartSeconds = 6.5f;
            player.SearchEndSeconds = 16.65f;
            player.SpawnPromptEndSeconds = 33.5f;
            string[] files = { "00 Opening", "01 New Life", "02 Growing", "03 Journey to the Ocean", "04 Returning Home", "05 Rebirth" };
            player.Narration = new AudioClip[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                player.Narration[i] = AssetDatabase.LoadAssetAtPath<AudioClip>(
                    "Assets/ShoalingUpstream/Standalone/Narration/" + files[i] + ".mp3");
                if (player.Narration[i] == null) throw new System.InvalidOperationException("Missing narration: " + files[i]);
            }
            if (driver.LocalJourney == null) throw new System.InvalidOperationException("Missing local journey");
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), StandaloneScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(StandaloneScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[standalone] local AR scene ready");
        }

        public static void BuildStandaloneFromCommandLine()
        {
            BuildStandaloneAr();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        // ------------------------------------------------------------------ pieces

        /// <summary>
        /// The visitor's eye. It is the listener as well — the audio engine follows this
        /// transform, so a mix judged here is the mix judged on the phone.
        /// </summary>
        private static void BuildCamera()
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";
            var camera = go.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            // Not black: a black Game view is indistinguishable from a scene that failed to load.
            camera.backgroundColor = new Color(0.09f, 0.12f, 0.15f);
            camera.nearClipPlane = 0.05f;
            camera.fieldOfView = 62f;              // roughly a phone held at arm's length
            go.transform.position = new Vector3(0f, 0f, -6f);
        }

        /// <summary>
        /// BuildCamera's on-device counterpart: an ARKit passthrough camera under an XR Origin,
        /// in place of the desktop's static one. `Camera.main` still resolves to this — the tag
        /// is what SimulationDriver's `_eye = Camera.main` actually keys off, so the whole rig's
        /// camera-relative code needs nothing scene-specific to find it.
        ///
        /// No ARSession-driven anchor is placed here — the origin sits at the scene's own
        /// origin, so the rig's spawns land wherever the device happens to be facing at launch,
        /// same as BuildFishEggTrigger already does on the desk.
        ///
        /// Drives the camera with ARPoseDriver, not TrackedPoseDriver + hand-picked Input System
        /// bindings — confirmed on a real device that with ARSession.state reading
        /// SessionTracking (ARKit itself genuinely tracking), the camera transform still never
        /// moved under TrackedPoseDriver, through two different binding attempts including one
        /// copied verbatim from AR Foundation's own "XR Origin (Mobile AR)" menu item. ARPoseDriver
        /// is older and Unity's docs point newer projects at TrackedPoseDriver instead, but it
        /// still ships and works: it reads pose via the legacy UnityEngine.XR.InputDevice API
        /// (CommonUsages.centerEyePosition, falling back to colorCameraPosition — the "eye" usage
        /// doesn't really describe a handheld phone, so colorCameraPosition is likely what a
        /// handheld device actually reports), which sidesteps Input System device-layout name
        /// matching entirely rather than needing another guess at which layout NSDK's wrapped
        /// ARKit loader reports through.
        /// </summary>
        private static void BuildArCamera()
        {
            new GameObject("AR Session", typeof(ARSession));

            var originGo = new GameObject("XR Origin", typeof(XROrigin));
            var offsetGo = new GameObject("Camera Offset");
            offsetGo.transform.SetParent(originGo.transform, false);

            var cameraGo = new GameObject("AR Camera",
                typeof(Camera), typeof(AudioListener), typeof(ARCameraManager),
                typeof(ARCameraBackground), typeof(ARPoseDriver));
            cameraGo.tag = "MainCamera";
            cameraGo.transform.SetParent(offsetGo.transform, false);

            var camera = cameraGo.GetComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            // ARCameraBackground takes over the actual draw once tracking starts; this is only
            // what shows before that (or if it never does).
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.09f, 0.12f, 0.15f);

            var origin = originGo.GetComponent<XROrigin>();
            origin.Camera = camera;
            origin.CameraFloorOffsetObject = offsetGo;
            // ARPoseDriver needs no further configuration — it auto-detects any connected
            // TrackedDevice-characteristic input device on its own.

            // For the egg-drop finale: lets SimulationDriver raycast down from an egg's spawn
            // point against the real detected floor (LiDAR-assisted plane detection on a
            // supporting iPad) instead of falling a fixed distance. No planePrefab assigned —
            // planes are tracked and raycastable without needing a visible representation.
            originGo.AddComponent<ARPlaneManager>();
            originGo.AddComponent<ARRaycastManager>();
        }

        private static void BuildLight()
        {
            var go = new GameObject("Directional Light", typeof(Light));
            var light = go.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            go.transform.rotation = Quaternion.Euler(48f, 150f, 0f);
        }

        /// <summary>
        /// One object carrying the whole piece: the socket, the audio, the device-path localizer,
        /// and the driver that assembles them. Shared by both the desktop scene and the AR one —
        /// every SceneTrigger below plays identically on either, since none of them know or care
        /// what kind of camera Camera.main resolves to.
        ///
        /// forceSimulationAudio picks the desk's Resonance stand-in (true, Build's caller) or a
        /// real PHASE render (false, BuildAr's caller) — the two are otherwise the same rig.
        ///
        /// serviceHost is "127.0.0.1" on the desk, where the Editor and the service are the same
        /// machine, and the service's own printed LAN address (e.g. "192.168.1.127") for a device
        /// build — 127.0.0.1 on an iPad means the iPad itself, not the laptop running the
        /// service, so a device build with the desk's value would never connect to anything.
        /// </summary>
        private static void BuildRig(bool forceSimulationAudio, string serviceHost)
        {
            var go = new GameObject("Shoaling Upstream");

            var link = go.AddComponent<ControlLink>();
            link.Host = serviceHost;
            link.Port = 8710;
            link.ConnectOnStart = true;
            link.Mode = PoseSourceMode.Auto;
            link.BuildName = forceSimulationAudio ? "unity-simulation" : "unity-ar";

            var audio = go.AddComponent<JourneyAudioRunner>();
            audio.forceSimulation = forceSimulationAudio;

#if NSDK_PRESENT
            if (forceSimulationAudio)
            {
                // Wired but starved: with no AR session it samples nothing and never reaches a
                // fix. That is the honest desk behaviour and it is worth being able to see,
                // rather than leaving the device path out of the scene entirely and discovering
                // on a phone that it was never wired. The AR scene skips this component
                // entirely for now — no VPS session anchor yet, and SimulationDriver.Update's
                // #if NSDK_PRESENT branch only ever reads it, never requires it, so omitting it
                // here is inert rather than a missing dependency.
                var vps = go.AddComponent<VpsLocalizationRunner>();
                SetPrivateEnum(vps, "_surface", 1);   // RuntimeSurface.Simulation
            }
#endif

            var driver = go.AddComponent<SimulationDriver>();
            driver.ServiceHost = serviceHost;
            driver.ServicePort = 8710;
            driver.Slug = "ucb-strawberry-creek-south";
            driver.ShowOverview = false;
            driver.SceneTriggers.Add(BuildFishEggTrigger());
            driver.SceneTriggers.Add(BuildHatchTrigger());
            driver.SceneTriggers.Add(BuildSwimTrigger());
            driver.SceneTriggers.Add(BuildAlevinToFryTrigger());
            driver.SceneTriggers.Add(BuildStriderTrigger());
            driver.SceneTriggers.Add(BuildEatAndGrowTrigger());
            driver.SceneTriggers.Add(BuildHeronTrigger());
            driver.SceneTriggers.Add(BuildNestTrigger());
            driver.SceneTriggers.Add(BuildHeronFeedTrigger());
            driver.SceneTriggers.Add(BuildFrySwimTrigger());
            driver.SceneTriggers.Add(BuildFryToRainbowTroutTrigger());
            driver.SceneTriggers.Add(BuildHeronHideTrigger());
            driver.SceneTriggers.Add(BuildTroutReturnTrigger());
            driver.SceneTriggers.Add(BuildTroutSwimTrigger());
            driver.SceneTriggers.Add(BuildTroutJumpTrigger());
            driver.SceneTriggers.Add(BuildRebirthLocateTrigger());
            driver.SceneTriggers.Add(BuildRebirthSpawnTrigger());
            driver.SceneTriggers.Add(BuildRebirthFadeTrigger());
            driver.FadeVariantMaterials = BuildFadeVariantMaterials();
        }

        private const string FadeVariantFolder = "Assets/ShoalingUpstream/Materials/FadeVariants";

        /// <summary>Alpha-blend twins of the rainbow trout's own materials, saved as assets and
        /// referenced from SimulationDriver only so the player build keeps the shader variant they
        /// use. glTFast's shader declares its blend mode with shader_feature (_ALPHABLEND_ON), and a
        /// variant that no material in the build uses is stripped — so FadeAndDestroy switching the
        /// keyword on at run time found nothing to switch to on the iPad, and the fish just vanished
        /// at the end of the fade instead of fading (fine in the editor, which never strips). Only
        /// the trout ever fades (07-a's cull and 07-c's finale), so only its materials are needed.
        /// Each twin is the original plus exactly what FadeAndDestroy does at run time, so the
        /// keyword set is the same one the fading fish will ask for.</summary>
        private static Material[] BuildFadeVariantMaterials()
        {
            var trout = AssetDatabase.LoadAssetAtPath<GameObject>(RainbowTroutPrefabPath);
            if (trout == null) return new Material[0];

            if (!AssetDatabase.IsValidFolder("Assets/ShoalingUpstream/Materials"))
                AssetDatabase.CreateFolder("Assets/ShoalingUpstream", "Materials");
            if (!AssetDatabase.IsValidFolder(FadeVariantFolder))
                AssetDatabase.CreateFolder("Assets/ShoalingUpstream/Materials", "FadeVariants");

            var twins = new List<Material>();
            var seen = new HashSet<Material>();
            foreach (var renderer in trout.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var source in renderer.sharedMaterials)
                {
                    if (source == null || !seen.Add(source)) continue;
                    var twin = new Material(source) { name = $"{source.name} (fade variant)" };
                    SimulationDriver.PrepareMaterialForFade(twin);

                    string fileName = twins.Count + " " + source.name;
                    foreach (char bad in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(bad, '_');
                    string path = $"{FadeVariantFolder}/{fileName}.mat";
                    AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(twin, path);
                    twins.Add(twin);
                }
            }
            return twins.ToArray();
        }

        /// <summary>Shared by BuildFishEggTrigger's fall-in egg mesh (01-a) and
        /// BuildFishEggMeshPrefab's runtime egg mesh (07-b's tail-drop finale) so both read as
        /// the same size egg, per feedback ("07-b FIRE后，生成的egg保持和01-a里一样的大小") —
        /// previously each had its own independently-tuned scale and had drifted apart.</summary>
        private static float ComputeEggMeshLocalScale()
        {
            // Per feedback, the egg's visible VOLUME should read as 0.6x of what it currently is
            // — volume scales with the cube of linear size, so the localScale factor below needs
            // the cube root of 0.6, not 0.6 itself.
            const float visibleVolumeScale = 0.6f;
            float visibleLinearScale = Mathf.Pow(visibleVolumeScale, 1f / 3f);
            // The source GLB was authored in centimetres, not the metres glTFast assumes. 1/10 on
            // top of that per feedback in AR, where the base size read too large, then
            // visibleLinearScale on top of THAT per the 0.6x-volume feedback above, and now a
            // further 10x per the latest feedback ("scale改为现在的10倍").
            const float latestSizeMultiplier = 10f;
            return 0.0001f * visibleLinearScale * latestSizeMultiplier;
        }

        /// <summary>"01 New Born" (beat id "beat-1"): a flock of fish eggs, dropped one after
        /// another, falling away from wherever the device is aimed. Position is a placeholder —
        /// SimulationDriver replants each copy's anchor in front of the eye the instant the beat
        /// fires, since there is no VPS fix to key it against on the wizard-of-oz path.
        ///
        /// Two levels deep on purpose. SnapEggAnchorToFloorWhenSettled (SimulationDriver) moves
        /// the OUTER anchor once a floor is found under it, straight down from spawn height —
        /// see its own comment for why nothing here plays out on the inner object directly.
        ///
        /// The mesh is a child of the inner object rather than the object the fall itself drives,
        /// because the source GLB's own pivot sits well off its geometry's centre — a Sketchfab
        /// export nested under empties with no geometry of their own, so "origin to geometry" has
        /// nothing in Blender to grab onto. Animating the mesh directly around that off-centre
        /// pivot reads as the object skewing rather than falling straight. Its own local origin
        /// is recentred onto the mesh's actual rendered bounds instead, in code, so it is exact
        /// regardless of the source file's structure.
        /// </summary>
        private static SimulationDriver.SceneTrigger BuildFishEggTrigger()
        {
            var anchor = new GameObject("Fish Egg Anchor");

            var root = new GameObject("Fish Egg");
            root.transform.SetParent(anchor.transform, false);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrangeEggPath);
            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            mesh.transform.localScale = Vector3.one * ComputeEggMeshLocalScale();
            mesh.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

            var renderers = mesh.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                // bounds is in world space, but root sits at the world origin at build time, so
                // it doubles as the offset needed in the root's local space.
                mesh.transform.localPosition -= bounds.center;
            }

            // The template itself never plays — PlaySceneFlock clones it fresh every time (see
            // SimulationDriver) — so left active it would just sit there rendering one small egg
            // at the origin for anyone to see before the beat ever fires.
            anchor.SetActive(false);

            return new SimulationDriver.SceneTrigger
            {
                BeatId = "beat-1",
                Kind = SimulationDriver.SceneTriggerKind.Spawn,
                FallTemplate = root.transform,
                // Level, not tilted with the device's own pitch — an egg falling straight down
                // (world Y) should look the same regardless of where the operator happened to be
                // aiming when it spawned.
                FollowCameraPitch = false,
                // Directly under the device, not SceneTriggerDistanceM out in front of it: the
                // floor-snap (SnapEggAnchorToFloorWhenSettled) already pulls the settled egg's
                // X/Z back to wherever the anchor itself started, so this is what actually places
                // the clutch under the iPad rather than a few metres ahead of it.
                SpawnUnderEye = true,
                // How long the visible straight-down fall itself takes, once a floor is found.
                SpawnFallDurationSeconds = 1.5f,
                Count = 20,
                StaggerSeconds = 0.05f,
                // Quarter of the original 1 m radius — half of THAT per earlier feedback, and
                // half again per feedback that the whole clutch's diameter should be half of
                // what it currently was ("所有egg散开的直径改为现在的1/2").
                ScatterRadiusM = 0.25f,
            };
        }

        /// <summary>"01-b: New Born - Hatch" (beat id "beat-2"): reaches into whatever "beat-1"
        /// left standing and animates it in place — a shake and a grow, then a swap for
        /// <see cref="BuildFishPrefab"/>.</summary>
        private static SimulationDriver.SceneTrigger BuildHatchTrigger() => new()
        {
            BeatId = "beat-2",
            Kind = SimulationDriver.SceneTriggerKind.ReactFlock,
            TargetBeatId = "beat-1",
            HatchDurationSeconds = 20f,
            GrowMultiplier = 0.5f,
            ShakeAmplitudeM = 0.03f,
            FishPrefab = BuildFishPrefab(),
            // Per feedback: once hatched, the whole clutch should slowly spread out to 5x its
            // post-hatch (egg-cluster) spacing rather than staying bunched exactly where the eggs
            // landed, rising to roughly eye height over that same spread as it does — first a
            // fixed 0.5 m ("散开的过程中同时缓慢升高0.5m"), then to the operator's own eye height
            // instead ("升高到大概iPad所在的高度，让用户...有种在Alevin鱼群中的感觉，而不是...俯
            // 视鱼群") once a fixed 0.5 m still left them well below eye level. SpreadRiseM is the
            // desktop-only fallback SpreadRiseToEyeHeight uses when there is no eye to read.
            SpreadMultiplier = 5f,
            SpreadRiseM = 1.4f,
            SpreadRiseToEyeHeight = true,
            SpreadDurationSeconds = 10f,
        };

        /// <summary>"02-a Grow - Swim" (beat id "beat-3"): carries whatever "beat-2" hatched to
        /// wherever the eye is aimed when this fires — the operator has walked the device
        /// somewhere new since Hatch planted them.</summary>
        private static SimulationDriver.SceneTrigger BuildSwimTrigger() => new()
        {
            BeatId = "beat-3",
            Kind = SimulationDriver.SceneTriggerKind.SwimToEye,
            TargetBeatId = "beat-2",
            // Twice the previous 3 s over the same 2 m, per feedback that the swim should be half
            // as fast ("swim的速度改为当前速度的1/2"). Each fish then adds its own random start
            // delay and 0.7–1.3x on top of this (see SwimOneToEyeStaggered).
            SwimDurationSeconds = 6f,
            SwimDestinationDistanceM = 9f,
            SwimScatterRadiusM = 4f,
        };

        /// <summary>Builds (or rebuilds) the standalone fish prefab HatchOne swaps an egg for. A
        /// prefab asset rather than an in-scene template, because Instantiate needs something
        /// that outlives the temporary build instance used to construct it.
        ///
        /// Scale and rotation are left at their defaults, not measured: unlike the egg, this
        /// mesh's own bounding box already reads roughly metre-scaled, so nothing obviously needs
        /// correcting. Check it by eye once it is in the scene, and adjust here if it reads
        /// sideways or the wrong size — the same way the egg's own correction was found.</summary>
        private static GameObject BuildFishPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(AlevinFishPath);

            var root = new GameObject("ALEVIN FISH");
            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(source);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            // 1/10 per feedback in AR, where every model besides the egg still read too large,
            // then a further 0.7x per feedback ("alevin scale 改为当前的0.7倍").
            mesh.transform.localScale = Vector3.one * 0.26f * 0.7f;
            // A first guess at "head to the left" — check it by eye and adjust if it is facing
            // the wrong way or the rotation axis is wrong entirely.
            mesh.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);

            // The source GLB carries a separate "eyes" sub-mesh with a huge stray offset baked
            // into its own translation — tens of units from the body, versus the ~1-2 units the
            // rest of the fish spans. It renders as a small dot that drifts along with the fish
            // since it is still a child of it. The body's own texture already paints an eye, so
            // dropping this stray mesh rather than trying to reposition it loses nothing.
            foreach (var t in mesh.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name.ToLowerInvariant().Contains("eyes")) Object.DestroyImmediate(t.gameObject);
            }

            var renderers = mesh.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                mesh.transform.localPosition -= bounds.center;
            }

            var asset = PrefabUtility.SaveAsPrefabAsset(root, AlevinFishPrefabPath);
            Object.DestroyImmediate(root);
            return asset;
        }

        /// <summary>"02-b Grow - Alevin to Fry" (beat id "beat-4"): swaps whatever "beat-3" is
        /// still carrying for a Fry, in place — no shake, no growth, just the model and its own
        /// "Swim" clip.</summary>
        private static SimulationDriver.SceneTrigger BuildAlevinToFryTrigger() => new()
        {
            BeatId = "beat-4",
            Kind = SimulationDriver.SceneTriggerKind.ReactFlock,
            TargetBeatId = "beat-3",
            InheritFacing = true,
            HatchDurationSeconds = 5f,
            GrowMultiplier = 0f,
            ShakeAmplitudeM = 0f,
            FishPrefab = BuildFryPrefab(),
        };

        /// <summary>Builds (or rebuilds) the standalone Fry prefab HatchOne swaps an Alevin Fish
        /// for, scaled to 1.5x the Alevin's own length and facing the same way. Check it by eye
        /// once it is in the scene and adjust here if either reads wrong.</summary>
        private static GameObject BuildFryPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(FryPath);

            var root = new GameObject("FRY");
            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(source);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            // 1.5x the Alevin's own final body length: Alevin's raw mesh (excluding "eyes") spans
            // 2.73 units, scaled 2.6x there = 7.11 m; Fry's own raw mesh spans 0.443 m, so
            // 7.11 * 1.5 / 0.443 ≈ this factor. 1/10 on top of that per feedback in AR, where
            // every model besides the egg still read too large.
            mesh.transform.localScale = Vector3.one * 2.406f;
            // The Alevin's own +90° reads as facing right on this mesh instead of left — the two
            // source files do not share a forward convention, so the same value does not mean
            // the same facing.
            mesh.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);

            // The source GLB carries the Blender scene's own ground plane as a sibling of the
            // fish — a second top-level node with no relation to the rig, tens of metres across.
            // Left in, it would both render as a giant slab and blow out the centring below.
            foreach (var t in mesh.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name.Equals("Ground", System.StringComparison.OrdinalIgnoreCase))
                    Object.DestroyImmediate(t.gameObject);
            }

            var renderers = mesh.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                mesh.transform.localPosition -= bounds.center;
            }

            var asset = PrefabUtility.SaveAsPrefabAsset(root, FryPrefabPath);
            Object.DestroyImmediate(root);
            return asset;
        }

        /// <summary>"05-a: Fry - Swim" (beat id "beat-17"): every fry from "beat-7" eases a short
        /// distance forward along its own current facing, independently staggered in when it
        /// starts — the same in-place, no-reregistration move as "06-b: Return Home - Swim"
        /// (BuildTroutSwimTrigger), with a shorter distance ("先后向前游动一小段距离") — inserted
        /// ahead of what was "05-a: Fry to Rainbow Trout", relabelled 05-b below to make room.
        /// </summary>
        private static SimulationDriver.SceneTrigger BuildFrySwimTrigger() => new()
        {
            BeatId = "beat-17",
            Kind = SimulationDriver.SceneTriggerKind.SwimFlock,
            TargetBeatId = "beat-7",
            SwimForwardDistanceM = 1f,
            SwimForwardDurationSeconds = 1.5f,
            RotateStaggerMaxSeconds = 1.5f,
        };

        /// <summary>"05-b: Fry - Become Rainbow Trout" (beat id "beat-9"; labelled 05-a until the
        /// new 05-a Swim above was inserted ahead of it): swaps whatever "beat-7" is still
        /// carrying (the grown fry) for a Rainbow Trout, in place — no shake, no growth, just the
        /// model and its own "Trout_Swim" clip (a different name from every other fish's "Swim",
        /// hence FishSwimClipName).</summary>
        private static SimulationDriver.SceneTrigger BuildFryToRainbowTroutTrigger() => new()
        {
            BeatId = "beat-9",
            Kind = SimulationDriver.SceneTriggerKind.ReactFlock,
            TargetBeatId = "beat-7",
            InheritFacing = true,
            HatchDurationSeconds = 5f,
            GrowMultiplier = 0f,
            ShakeAmplitudeM = 0f,
            FishPrefab = BuildRainbowTroutPrefab(),
            FishSwimClipName = "Trout_Swim",
        };

        /// <summary>Builds (or rebuilds) the standalone Rainbow Trout prefab HatchOne swaps a Fry
        /// for. The source GLB carries a baked-in x100 node scale (the same Sketchfab
        /// unit-conversion pattern as the nest); leaving it uncorrected read far too small in
        /// practice, so an additional 130x sits on top of that rather than countering it — found
        /// by testing in the scene, not computed. Rotation is a genuine first guess (identity) —
        /// check both by eye and adjust here if it reads the wrong size or facing.</summary>
        private static GameObject BuildRainbowTroutPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(RainbowTroutPath);

            var root = new GameObject("RAINBOW TROUT");
            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(source);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            // 1/10 per feedback in AR, where every model besides the egg still read too large,
            // then a further 0.7x per feedback ("Rainbow tout scale改为现在的0.7"). BuildTroutJump
            // Trigger's own JumpHeightM/JumpForwardM are scaled by the same 0.7x to match — see
            // its own comment.
            mesh.transform.localScale = Vector3.one * 10f * 0.7f;
            // First guess, matching the fry's own convention — check by eye and adjust here if
            // it faces the wrong way.
            mesh.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);

            var renderers = mesh.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                // WorldSkinnedBounds, not plain Renderer.bounds: the latter is known-unreliable
                // for a SkinnedMeshRenderer fresh out of PrefabUtility.InstantiatePrefab in an
                // editor script, outside Play mode, before the skin has ever actually posed —
                // this was previously patched with an empirically-measured correction (found by
                // comparing a fry's and a trout's spawn positions in a live scene), which then
                // needed re-tuning by hand every time the trout's own scale changed and was
                // confirmed on a device to still be wrong (trout landing "far higher than" the
                // fry it replaced) even after that. Reading each renderer's own reliable bind-pose
                // bounds instead removes the guesswork entirely.
                var bounds = WorldSkinnedBounds(renderers[0]);
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(WorldSkinnedBounds(renderers[i]));
                mesh.transform.localPosition -= bounds.center;
            }

            var asset = PrefabUtility.SaveAsPrefabAsset(root, RainbowTroutPrefabPath);
            Object.DestroyImmediate(root);
            return asset;
        }

        /// <summary>"03-a Strider - Show" (beat id "beat-5"): a single strider appears 2 m directly
        /// ahead of the frontmost fry, as the operator is currently aiming the device (not a fixed
        /// world position, and not the fry's own facing either — see AnchorForwardFromEye; their
        /// own facing can differ from the operator's current aim by enough to read as "off to the
        /// side" rather than "dead ahead" for an offset this small, per feedback "现在仍然在右前
        /// 方") — and loops "Idle_A" at half speed. "beat-4" still holds the fry at this point in
        /// the beat order (03-a fires before 03-b's EatAndGrow reaches into it), so that is the
        /// flock this anchors to. See PlayAppear's own retry loop for why this used to render
        /// nowhere near the fry when beat-4 was still mid-hatch.</summary>
        private static SimulationDriver.SceneTrigger BuildStriderTrigger() => new()
        {
            BeatId = "beat-5",
            Kind = SimulationDriver.SceneTriggerKind.Appear,
            Template = BuildStriderTemplate(),
            Count = 1,
            AnchorBeatId = "beat-4",
            // Ahead of the frontmost fry, not of the flock's average — per feedback ("Strider需在
            // 所有鱼的正前方生成"): a flock that has spread out has its centroid well behind the
            // leader, so 2 m in front of the centroid can land among the fish.
            AnchorToFrontmost = true,
            AnchorForwardFromEye = true,
            AnchorOffsetM = new Vector3(2f, 0f, 0f),
            AppearClipName = "Idle_A",
            AppearClipSpeed = 0.5f,
        };

        /// <summary>An in-scene template (not a saved prefab asset — PlayAppear just clones it
        /// directly, the way Spawn's own template works), left inactive so nothing is visible
        /// before "beat-5" fires. Scale and rotation are left at their defaults — this is a
        /// different asset entirely from the old STRIDER2.glb export (a purchased "Quirky
        /// Series" pack, already correctly scaled and rigged for direct use in Unity), so
        /// nothing from that export's tuning carries over. Check it by eye once it is in the
        /// scene and adjust here if it reads sideways or the wrong size. The mesh prefab already
        /// carries its own Animator, Avatar and AnimatorController — nothing to wire up.</summary>
        private static GameObject BuildStriderTemplate()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(CricketMeshPath);

            var root = new GameObject("STRIDER");
            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(source);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            // First guess at "head toward screen-left" — check by eye and flip the sign here if
            // it comes in facing the other way.
            mesh.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);

            var renderers = mesh.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                mesh.transform.localPosition -= bounds.center;
            }

            // Applied last, after the bounds-centring above, for the same reason as the heron's
            // own scale-after-centring: doing it earlier would scale that world-space-derived
            // offset along with everything else. 1/10 per feedback in AR, where every model
            // besides the egg still read too large.
            root.transform.localScale = Vector3.one * 1f;

            root.SetActive(false);
            return root;
        }

        /// <summary>"03-b Strider - Eat Strider & Grow" (beat id "beat-7"): the fry closest to
        /// the strider from "beat-5" swims toward it over 1 second, then the strider is
        /// destroyed outright; 2 seconds after that, every fry still alive from "beat-4"
        /// (the approaching one included) grows in place to 1.3x its current scale.</summary>
        private static SimulationDriver.SceneTrigger BuildEatAndGrowTrigger() => new()
        {
            BeatId = "beat-7",
            Kind = SimulationDriver.SceneTriggerKind.EatAndGrow,
            ConsumeTargetBeatId = "beat-5",
            TargetBeatId = "beat-4",
            GrowMultiplier = 0.3f,
            HatchDurationSeconds = 1f,
            SwimDurationSeconds = 1f,
            EatDelaySeconds = 1f,
            GrowDelaySeconds = 2f,
        };

        /// <summary>"04-a Heron - Show" (beat id "beat-6"), first half: the heron plants itself
        /// 6 m to the fry's right, level with them — not a fixed world position or
        /// camera-relative, same reasoning as the strider — and loops its own "Idle" clip.
        /// "beat-7" (03-b's EatAndGrow), not "beat-4": by the time 04-a fires, 03-b has already
        /// consumed "beat-4"'s flock and re-registered the (grown) survivors under its own beat
        /// id, so "beat-4" itself would be empty here. Paired with <see cref="BuildNestTrigger"/>,
        /// which plants the nest and its babies at the same anchor, offset from THIS heron the
        /// way BuildNestTrigger's own comment explains.</summary>
        private static SimulationDriver.SceneTrigger BuildHeronTrigger() => new()
        {
            BeatId = "beat-6",
            Kind = SimulationDriver.SceneTriggerKind.Appear,
            Template = BuildHeronTemplate(),
            Count = 1,
            AnchorBeatId = "beat-7",
            // Per feedback: X (head axis) now tracks whichever fry is furthest ahead rather than
            // the flock's own average, Y is pinned to the real floor rather than floating at the
            // fry's own (mid-water) height, and Z (how far to the fry's right) went 9 -> 1 m
            // (still read as floating at 9 m — likely the real floor there, well to the side of
            // wherever the fish are, was less likely to have been scanned yet than a spot only
            // 1 m away, so AnchorOnGround's own retry more often ran out its window and fell back
            // to the fish's own mid-water height instead of a real floor reading), then +2 m and
            // +1 m more across two more rounds of "整套模型再往右侧平移" to 4 m.
            AnchorToFrontmost = true,
            AnchorOnGround = true,
            AnchorOffsetM = new Vector3(0f, 0f, 4f),
            AppearClipName = "Idle",
        };

        /// <summary>Scale is left at its default, not measured: unlike the egg or the nest below,
        /// this mesh's own bounding box already reads roughly metre-scaled (about 1.2 m tall)
        /// with no stray baked-in node scale, so nothing obviously needs correcting. Rotation is
        /// a first guess at "beak toward screen-left" — check it by eye and flip the sign here if
        /// it comes in facing the other way. The source carries four clips ("Idle", "Lower Head",
        /// "Turn", and a second "Lower Head") — glTFast's Legacy import wires up only whichever
        /// one it treats as the default (here, "Idle") onto the Animation component's own playable
        /// list, so the other three are re-added explicitly by name below, or Play("Lower Head")
        /// from "04-b Heron - Feed" finds nothing and silently no-ops. The source also has two
        /// separate clips both literally named "Lower Head" — one moves all 26 rigged bones, the
        /// other only 2 (a stray leftover from the export, barely a twitch) — so whichever one
        /// AddClip saw last would silently win the name and make the feed gesture invisible;
        /// picking by curve count instead keeps the real one regardless of enumeration order.
        /// </summary>
        private static GameObject BuildHeronTemplate()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(HeronPath);

            var root = new GameObject("HERON");
            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(source);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            mesh.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);

            var animation = mesh.GetComponentInChildren<Animation>();
            if (animation != null)
            {
                foreach (var kv in LoadClipsPreferringMostCurves(HeronPath)) animation.AddClip(kv.Value, kv.Key);
            }

            // Stood on its feet, not centred on its bounds: the root's origin is the middle of the
            // feet on the ground, so putting the root on the floor puts the bird's feet on the
            // floor, and any turn of the root spins it about its feet. It used to be centred on the
            // bounds of the whole body — neck and beak included, 9 cm of model space in front of the
            // feet — which planted the bird half sunk into the floor and swung its feet round an
            // arc whenever it turned. Its own "Turn" clip rotates the Armature about the model's
            // origin, which is another point again (46% of the way up, 4 cm behind the feet, measured
            // off HERON.glb); FeetLock holds the feet still through that at run time, via the marker
            // placed under the Armature below.
            Vector3 feet = FindFeetPoint(mesh);
            mesh.transform.localPosition -= feet;

            Transform armature = null;
            foreach (var t in mesh.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Armature") { armature = t; break; }
            }
            var feetMarker = new GameObject(HeronFeetMarkerName).transform;
            feetMarker.SetParent(armature != null ? armature : mesh.transform, false);
            // The feet are at the world origin now (root sits there at build time, unscaled).
            feetMarker.position = root.transform.position;

            // Applied last, after the centring above (which reads world-space positions and folds
            // them straight into a local offset) — doing this any earlier would scale that offset
            // along with everything else and throw the centring off by 30x.
            // 1/10 per feedback in AR, where every model besides the egg still read too large.
            root.transform.localScale = Vector3.one * 3f;

            root.SetActive(false);
            return root;
        }

        /// <summary>Name of the empty marker BuildHeronTemplate parks under the heron's Armature, at
        /// the middle of its feet; the feed beat's LockFeetTransformName looks it up by this.</summary>
        private const string HeronFeetMarkerName = "FeetPivot";

        /// <summary>Where a model stands, in world space: the middle of its feet in X and Z and its
        /// lowest point in Y. Feet are taken as the lowest 5% of the skinned mesh's own vertices in
        /// its bind pose — the heron's legs are rigid parts of the mesh, not bones, so there is no
        /// foot bone to ask — falling back to the middle of the bind-pose bounds' floor if the mesh
        /// is not readable.</summary>
        private static Vector3 FindFeetPoint(GameObject model)
        {
            var skinned = model.GetComponentInChildren<SkinnedMeshRenderer>();
            if (skinned != null && skinned.sharedMesh != null && skinned.sharedMesh.isReadable)
            {
                var local = skinned.sharedMesh.vertices;
                if (local.Length > 0)
                {
                    var world = new Vector3[local.Length];
                    float minY = float.MaxValue;
                    float maxY = float.MinValue;
                    for (int i = 0; i < local.Length; i++)
                    {
                        world[i] = skinned.transform.TransformPoint(local[i]);
                        minY = Mathf.Min(minY, world[i].y);
                        maxY = Mathf.Max(maxY, world[i].y);
                    }
                    float soleTop = minY + (maxY - minY) * 0.05f;
                    Vector3 sum = Vector3.zero;
                    int count = 0;
                    for (int i = 0; i < world.Length; i++)
                    {
                        if (world[i].y > soleTop) continue;
                        sum += world[i];
                        count++;
                    }
                    return new Vector3(sum.x / count, minY, sum.z / count);
                }
            }

            var renderers = model.GetComponentsInChildren<Renderer>();
            var bounds = WorldSkinnedBounds(renderers[0]);
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(WorldSkinnedBounds(renderers[i]));
            return new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        }

        /// <summary>Every AnimationClip sub-asset at path, keyed by name — when two share a name
        /// (the heron's two "Lower Head" clips), the one with more animated curves wins, since
        /// AddClip would otherwise let whichever AssetDatabase happens to enumerate last silently
        /// overwrite the real one with a near-empty duplicate.</summary>
        private static Dictionary<string, AnimationClip> LoadClipsPreferringMostCurves(string path)
        {
            var byName = new Dictionary<string, AnimationClip>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is not AnimationClip clip) continue;
                if (byName.TryGetValue(clip.name, out var existing)
                    && AnimationUtility.GetCurveBindings(existing).Length
                        >= AnimationUtility.GetCurveBindings(clip).Length)
                {
                    continue;
                }
                byName[clip.name] = clip;
            }
            return byName;
        }

        /// <summary>World-space bounds for one renderer, using a SkinnedMeshRenderer's own
        /// bind-pose localBounds — transformed by its current Transform — instead of plain
        /// Renderer.bounds. Renderer.bounds is known-unreliable for a skinned mesh immediately
        /// after PrefabUtility.InstantiatePrefab in an editor script outside Play mode, before
        /// the skin has ever actually posed; localBounds is computed straight from the mesh
        /// asset's own bind pose, so it has no such staleness window. Ordinary MeshRenderers
        /// (no skinning) have nothing to be stale, so they just use Renderer.bounds directly.
        /// Only the returned bounds' centre is meaningful as a true world AABB — the size is an
        /// axis-aligned approximation from the eight local corners, fine for the centring this
        /// exists for, not a rigorous extent.</summary>
        private static Bounds WorldSkinnedBounds(Renderer renderer)
        {
            if (renderer is not SkinnedMeshRenderer skinned) return renderer.bounds;

            Bounds local = skinned.localBounds;
            var world = new Bounds(skinned.transform.TransformPoint(local.center), Vector3.zero);
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3(sx, sy, sz));
                world.Encapsulate(skinned.transform.TransformPoint(corner));
            }
            return world;
        }

        /// <summary>"04-a Heron - Show" (beat id "beat-6"), second half: the nest plants itself
        /// with three baby birds already inside it, anchored to the same fry, at the same 6 m
        /// distance to their right as <see cref="BuildHeronTrigger"/>'s own heron, offset from
        /// that heron along the fry's head/tail axis instead — not the same axis as the shared
        /// 6 m, and not "+1 unit" either. The very first version of this beat (before either
        /// followed the fry) had the two at fixed world positions, Heron (2,14,50) and Nest
        /// (30,14,50): identical Y and Z, a 28-unit gap in X alone — i.e. entirely along the
        /// head/tail axis (world X, since the desk camera's own "screen-right" is world +X,
        /// opposite the fixed Vector3.left every model's head actually points), nothing to do
        /// with "right of the fry" at all, and a leftover magnitude from that old desktop-scale
        /// project. Reproduced here as the same relationship, scaled down by the same 1/10 as
        /// every model's own size (28 → 2.8 m), on top of the shared 6 m. The nest itself carries
        /// no animation; each baby loops "Idle A" at half speed.</summary>
        private static SimulationDriver.SceneTrigger BuildNestTrigger() => new()
        {
            BeatId = "beat-6",
            Kind = SimulationDriver.SceneTriggerKind.Appear,
            Template = BuildNestTemplate(),
            Count = 1,
            AnchorBeatId = "beat-7",
            // Same X/Y/Z treatment as BuildHeronTrigger, for the same reason — see its own comment
            // (Z: 9 -> 1 -> 3 -> 4 m). X is unchanged: -2.8 m keeps the nest offset from the
            // heron along the fry's own head/tail axis exactly as before, so the two still sit
            // side by side rather than on top of each other regardless of how far out Z pushes
            // the pair.
            AnchorToFrontmost = true,
            AnchorOnGround = true,
            AnchorOffsetM = new Vector3(-2.8f, 0f, 4f),
            AppearClipName = "Idle A",
            AppearClipSpeed = 0.5f,
        };

        /// <summary>The nest's position, rotation and scale are copied straight from a live Play
        /// session's Inspector, after the user hand-tuned it by eye. The babies come from
        /// Kookaburra_LOD0 (see <see cref="CricketMeshPath"/>'s doc comment — same "Quirky
        /// Series" reasoning applies here, and this prefab already carries its own Animator,
        /// Avatar and AnimatorController, so nothing needs adding). The three babies' position,
        /// rotation and scale are likewise copied straight from a live Play session's Inspector,
        /// after the user hand-tuned all three by eye.</summary>
        private static GameObject BuildNestTemplate()
        {
            var nestSource = AssetDatabase.LoadAssetAtPath<GameObject>(BirdNestPath);
            var babySource = AssetDatabase.LoadAssetAtPath<GameObject>(KookaburraMeshPath);

            var root = new GameObject("BIRD NEST");
            var nest = (GameObject)PrefabUtility.InstantiatePrefab(nestSource);
            nest.name = "Mesh";
            nest.transform.SetParent(root.transform, false);
            nest.transform.localPosition = new Vector3(-0.0119476f, -0.46f, 0.0473462f);
            nest.transform.localRotation = Quaternion.Euler(-90f, 0f, -180f);
            nest.transform.localScale = Vector3.one * 0.005f;

            var babies = new[]
            {
                (name: "Baby Bird 1", pos: new Vector3(-0.154f, -0.37f, -0.112f), rot: new Vector3(0f, -158.1f, 0f)),
                (name: "Baby Bird 2", pos: new Vector3(0.048f, -0.37f, -0.1f), rot: new Vector3(0f, -150.1f, 0f)),
                (name: "Baby Bird 3", pos: new Vector3(-0.049f, -0.37f, 0.006f), rot: new Vector3(0f, -179f, 0f)),
            };
            var babyObjects = new List<Transform>();
            foreach (var (name, pos, rot) in babies)
            {
                var baby = (GameObject)PrefabUtility.InstantiatePrefab(babySource);
                baby.name = name;
                baby.transform.SetParent(root.transform, false);
                baby.transform.localPosition = pos;
                baby.transform.localRotation = Quaternion.Euler(rot);
                baby.transform.localScale = Vector3.one * 0.125f;
                babyObjects.Add(baby.transform);
            }

            // Stood on its own base, not left at the hand-tuned pivot above — the nest's own
            // lowest point (FindFeetPoint, the same "stand it on the ground" measure
            // BuildHeronTemplate uses) is pulled back to the root's own origin, and the three
            // babies — siblings of the nest under this same root, not its children — are shifted
            // by the same amount so their hand-tuned positions relative to the nest are
            // unchanged. Needed so AnchorOnGround (see BuildNestTrigger) lands the nest's own
            // base on the real floor, per feedback ("nest的底端都在地上"), rather than wherever
            // this hand-tuned offset happened to leave it relative to the root.
            Vector3 nestBase = FindFeetPoint(nest);
            nest.transform.localPosition -= nestBase;
            foreach (var baby in babyObjects) baby.localPosition -= nestBase;

            // 1/10 per feedback in AR, where every model besides the egg still read too large.
            // Scales the nest and all three babies uniformly, since it sits above them in the
            // hierarchy — their own individual scales above are relative fits within the group
            // (hand-tuned by eye) and stay untouched, so the group's internal layout is unchanged.
            root.transform.localScale = Vector3.one * 3f;

            root.SetActive(false);
            return root;
        }

        /// <summary>"04-b Heron - Feed" (beat id "beat-8"): reaches into "beat-6"'s flock (the
        /// heron and nest planted by 04-a) and runs "Lower Head" → (1s pause) → "Turn" → (1s
        /// pause) → "Lower Head" again, this last one preceded by an instant 180° turn — "Lower
        /// Head" resets to the same baked facing every time it plays, so without this the second
        /// playthrough would snap straight back to the original side. The heron's feet are held
        /// in place throughout (LockFeetTransformName), since "Turn" pivots on the model's own
        /// origin, not on the feet, and would otherwise slide it round an arc. None of the
        /// three loop, since these are gestures, not an idle. Only the heron's own Legacy
        /// Animation actually has clips by these names; the nest and babies have no match, so
        /// <see cref="SimulationDriver.PlayClipOnFlock"/> finding nothing there — and not turning
        /// or holding anything — is expected, not an error.
        ///
        /// Also pulls one fish out of "beat-7" (whatever survived the strider-eats-fry beat) and
        /// feeds it to the heron: it swims to "Bone.007" — a first guess at the beak-tip bone,
        /// the outermost link in the neck chain with no children of its own; check by eye and
        /// rename here if the fish arrives somewhere else on the model — then tracks it for the
        /// rest of the sequence, disappearing exactly when the second "Lower Head" begins. That
        /// timing is computed from the same clip lengths and delays above, not a separate guess,
        /// so it stays correct if either changes.</summary>
        private static SimulationDriver.SceneTrigger BuildHeronFeedTrigger()
        {
            var clips = LoadClipsPreferringMostCurves(HeronPath);
            float lowerHeadLength = clips.TryGetValue("Lower Head", out var lowerHead) ? lowerHead.length : 0f;
            float turnLength = clips.TryGetValue("Turn", out var turn) ? turn.length : 0f;

            var sequence = new[]
            {
                new SimulationDriver.ClipStep { ClipName = "Lower Head" },
                // The turn itself needs no correction here: LockFeetTransformName below holds the feet
                // in place while it plays, so it reads as turning on the spot. (It used to carry a
                // hand-tuned 4 m sideways nudge, measured when the heron was ten times the size it
                // is now — at today's size that alone slid the bird several metres across the floor.)
                new SimulationDriver.ClipStep { ClipName = "Turn", DelaySeconds = 1f },
                new SimulationDriver.ClipStep
                {
                    ClipName = "Lower Head", DelaySeconds = 1f, RotateYDegrees = 180f,
                },
            };
            // The fish disappears exactly when the third step (the second "Lower Head") begins:
            // the first step's own length, then the second step's own delay and length, then the
            // third step's own delay — all read from the sequence above, not duplicated here.
            float fishDisappearAfter = lowerHeadLength + sequence[1].DelaySeconds + turnLength + sequence[2].DelaySeconds;

            return new SimulationDriver.SceneTrigger
            {
                BeatId = "beat-8",
                Kind = SimulationDriver.SceneTriggerKind.PlayClip,
                TargetBeatId = "beat-6",
                ClipSequence = sequence,
                LockFeetTransformName = HeronFeetMarkerName,
                FishSourceBeatId = "beat-7",
                FishFollowBoneName = "Bone.007",
                // First guess: Bone.007 itself reads closer to the eye than the beak tip, so
                // nudged a bit further out along its own local Y — check by eye and adjust the
                // axis/magnitude here if it lands somewhere else.
                FishFollowLocalOffset = new Vector3(0f, 0.15f, 0f),
                FishSwimDurationSeconds = 1f,
                FishDisappearAfterSeconds = fishDisappearAfter,
            };
        }

        /// <summary>"04-c Heron - Hide" (beat id "beat-11"): destroys everything "beat-6" put on
        /// screen — the heron, the nest, and its three babies — outright.</summary>
        private static SimulationDriver.SceneTrigger BuildHeronHideTrigger() => new()
        {
            BeatId = "beat-11",
            Kind = SimulationDriver.SceneTriggerKind.Despawn,
            TargetBeatId = "beat-6",
        };

        /// <summary>"06-a Return Home - Return" (beat id "beat-10"): every rainbow trout from
        /// "beat-9" turns 180° around Y over 1 second, each one starting after its own random
        /// delay of up to 1 second so the flock turns raggedly rather than in lockstep. Rotate
        /// rather than PlayClip, since PlayClip would switch "Trout_Swim" from its own looping
        /// playback to a one-shot clip and leave it frozen once that single playthrough finished
        /// — this only needs the turn, with swimming left alone.</summary>
        private static SimulationDriver.SceneTrigger BuildTroutReturnTrigger() => new()
        {
            BeatId = "beat-10",
            Kind = SimulationDriver.SceneTriggerKind.Rotate,
            TargetBeatId = "beat-9",
            RotateYDegrees = 180f,
            RotateDurationSeconds = 1f,
            RotateStaggerMaxSeconds = 3f,
        };

        /// <summary>"06-b: Return Home - Swim" (beat id "beat-16"): every rainbow trout from
        /// "beat-9" eases forward along its own current facing, independently staggered in when
        /// it starts, per feedback ("所有 Rainbow tout 陆续向头的方向移动1m") — a new beat inserted
        /// between the existing 06-a Return (turn around) and what was "06-b: Return Home -
        /// Jump", relabelled 06-c below to make room for this one. Distance doubled to 2 m per
        /// feedback ("Swim的距离再远1倍").</summary>
        private static SimulationDriver.SceneTrigger BuildTroutSwimTrigger() => new()
        {
            BeatId = "beat-16",
            Kind = SimulationDriver.SceneTriggerKind.SwimFlock,
            TargetBeatId = "beat-9",
            SwimForwardDistanceM = 2f,
            SwimForwardDurationSeconds = 1.5f,
            RotateStaggerMaxSeconds = 1.5f,
        };

        /// <summary>"06-c Return Home - Jump" (beat id "beat-12"; labelled 06-b until the new
        /// 06-b Swim above was inserted ahead of it): every rainbow trout from "beat-9" arcs up
        /// and back down along world Y on a true parabola while translating along world X — a
        /// leap out of the water and a dive back into it — pitching around world Z (level at
        /// takeoff, 60° nose-up at the quarter-point, level at the peak, mirrored nose-down on
        /// the way back), pivoting on its own tail bone, "TailFinLower_M_010", rather than its
        /// body centre. Each fish is independently staggered in when it starts (up to 1.5 s) and
        /// how high it goes (1.12–2.24 m). This whole shape (true parabola, pitch easing
        /// level→up→level→down→level, tail pivot) matches what read right in an earlier session,
        /// found again by searching that session's own transcript per feedback ("按照当时满意的
        /// 效果重新制作") — JumpHeightM/JumpForwardM there were tuned to 32/2 against the trout's
        /// OLD, much larger mesh scale (100x baked-in + a further ~130x on top); BuildRainbowTrout
        /// Prefab's own mesh has since had a further 1/10, then a further 0.7x, applied ("every
        /// model besides the egg still read too large", then "scale改为现在的0.7" — see its own
        /// comment) that these two absolute-metre fields never got, so left alone they would again
        /// launch the trout many times its own body length into the air. Scaled by the same
        /// 1/10 * 0.7 here to match.</summary>
        private static SimulationDriver.SceneTrigger BuildTroutJumpTrigger() => new()
        {
            BeatId = "beat-12",
            Kind = SimulationDriver.SceneTriggerKind.Jump,
            TargetBeatId = "beat-9",
            JumpHeightM = 3.2f * 0.7f,
            JumpDurationSeconds = 1.5f,
            JumpStaggerMaxSeconds = 1.5f,
            JumpForwardM = 0.2f * 0.7f,
            JumpPitchDegrees = 60f,
        };

        /// <summary>"07-a Rebirth - Locate" (beat id "beat-13"): keeps 2 of "beat-9"'s rainbow
        /// trout, picked at random, and fades every other one away over 6 seconds (alpha, not a
        /// sudden disappearance — see FadeAndDestroy). The two survivors carry on registered
        /// under "beat-13" for whatever "07-b Rebirth - Spawn" and "07-c Rebirth - Fade" need
        /// next.</summary>
        private static SimulationDriver.SceneTrigger BuildRebirthLocateTrigger() => new()
        {
            BeatId = "beat-13",
            Kind = SimulationDriver.SceneTriggerKind.Cull,
            TargetBeatId = "beat-9",
            KeepCount = 2,
            CullDurationSeconds = 6f,
        };

        /// <summary>"07-b Rebirth - Spawn" (beat id "beat-14"): of the two survivors "beat-13"
        /// kept, only the first ("fish A" — "fish B" is untouched here, presumably for a later
        /// beat) tilts its Rotation X to -90°, pivoting about a point 0.3 m toward its own belly,
        /// over 1 second; plays its own already-running animation at 3x speed for 4 seconds; then
        /// reverses both — speed drops back to normal instantly and the tilt eases back, pivoting
        /// the same way, over another 1 second.</summary>
        private static SimulationDriver.SceneTrigger BuildRebirthSpawnTrigger() => new()
        {
            BeatId = "beat-14",
            Kind = SimulationDriver.SceneTriggerKind.Stunt,
            TargetBeatId = "beat-13",
            RotateDurationSeconds = 1f,
            // Not exactly -90: at precisely -90 the fish's Y/Z rotation sits on Unity's gimbal-lock
            // pole, where the same orientation can silently redisplay with Y and Z swapped (e.g.
            // Y=-180,Z=0 flipping to Y=0,Z=-180) — visually identical, but reads as "Z is changing"
            // in the Inspector. A hair off the pole keeps the same near-vertical tilt without it.
            StuntRotationXTarget = -89f,
            StuntSpeedMultiplier = 3f,
            StuntSpeedDurationSeconds = 4f,
            // The finale: once the fish settles back down, 20 eggs release from its tail over
            // 3 seconds, each jittered slightly so they don't all fall from the same point. 1.5 m
            // was tuned against the egg's own old, much larger scale (see BuildFishEggMeshPrefab)
            // — now that it matches 01-a's own tiny egg exactly, that same radius scattered them
            // across an area many times the egg's own size, per feedback ("egg散的太开了，让所有
            // egg都从rainbow trout A的尾部附近产生").
            EggPrefab = BuildFishEggMeshPrefab(),
            EggCount = 20,
            EggDropWindowSeconds = 3f,
            EggJitterRadiusM = 0.15f,
            EggFallDistanceM = 4f,
            EggFallDurationSeconds = 1f,
        };

        /// <summary>"07-c Rebirth - Fade" (beat id "beat-15"): both survivors of beat-14's flock
        /// (Fish A and Fish B) swim forward 1 m, tilt onto their side the same way Fish A did for
        /// 07-b's stunt (reuses StuntRotationXTarget/RotateDurationSeconds), hold there for
        /// LieDownHoldSeconds, then fade away (alpha, not a sudden disappearance — see
        /// FadeAndDestroy) over 6 seconds and are destroyed.</summary>
        private static SimulationDriver.SceneTrigger BuildRebirthFadeTrigger() => new()
        {
            BeatId = "beat-15",
            Kind = SimulationDriver.SceneTriggerKind.LieDownFade,
            TargetBeatId = "beat-14",
            SwimForwardDistanceM = 1f,
            SwimForwardDurationSeconds = 1f,
            StuntRotationXTarget = -89f,
            RotateDurationSeconds = 1f,
            LieDownHoldSeconds = 3f,
            CullDurationSeconds = 6f,
        };

        /// <summary>A standalone egg mesh for runtime spawning — reuses ORANGE EGG's unit-scale
        /// and rotation fix from BuildFishEggTrigger, but without that trigger's anchor/root/
        /// Timeline wrapper, since these eggs are instantiated and animated directly at runtime
        /// (07-b's tail-drop finale) rather than pre-placed and Timeline-driven in the scene.
        /// </summary>
        private static GameObject BuildFishEggMeshPrefab()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(OrangeEggPath);

            var root = new GameObject("Fish Egg Mesh");
            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(source);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            // Matches BuildFishEggTrigger's own egg mesh (01-a) exactly again, per feedback
            // ("现在的egg太大了，保持和01-a里的egg一样大") — this had drifted through several
            // rounds of its own independent tuning (10x, then halved twice to 2.5x, then 4x back
            // up to 10x) since it last matched 01-a.
            mesh.transform.localScale = Vector3.one * ComputeEggMeshLocalScale();
            mesh.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

            var renderers = mesh.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                mesh.transform.localPosition -= bounds.center;
            }

            var asset = PrefabUtility.SaveAsPrefabAsset(root, FishEggMeshPrefabPath);
            Object.DestroyImmediate(root);
            return asset;
        }

        /// <summary>`_surface` is private and serialized on purpose — it is the one guard against
        /// shipping provisional coordinates to somebody standing in a creek, so it is meant to be
        /// set deliberately rather than assigned from code. Setting it through SerializedObject
        /// keeps that property: it is stored in the scene asset, visible in the Inspector, and
        /// reviewable in the diff.</summary>
        private static void SetPrivateEnum(Object target, string field, int value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(field);
            if (property == null)
            {
                Debug.LogWarning($"[scene] no serialized field '{field}' on {target.GetType().Name}");
                return;
            }
            property.enumValueIndex = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Put the scene first in the build list, and leave anything else alone.</summary>
        private static void Register(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == path);
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
