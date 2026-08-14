using System.Collections.Generic;
using System.IO;
using ShoalingUpstream.Audio;
using ShoalingUpstream.Control;
using ShoalingUpstream.Simulation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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

        [MenuItem("Shoaling Upstream/Rebuild Simulation Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildCamera();
            BuildLight();
            BuildRig();

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
        /// and the driver that assembles them.
        /// </summary>
        private static void BuildRig()
        {
            var go = new GameObject("Shoaling Upstream");

            var link = go.AddComponent<ControlLink>();
            link.Host = "127.0.0.1";
            link.Port = 8710;
            link.ConnectOnStart = true;
            link.Mode = PoseSourceMode.Auto;
            link.BuildName = "unity-simulation";

            var audio = go.AddComponent<JourneyAudioRunner>();
            audio.forceSimulation = true;   // the desk renderer, never PHASE at a laptop

#if NSDK_PRESENT
            // Wired but starved: with no AR session it samples nothing and never reaches a fix.
            // That is the honest desk behaviour and it is worth being able to see, rather than
            // leaving the device path out of the scene entirely and discovering on a phone that
            // it was never wired.
            var vps = go.AddComponent<VpsLocalizationRunner>();
            SetPrivateEnum(vps, "_surface", 1);   // RuntimeSurface.Simulation
#endif

            var driver = go.AddComponent<SimulationDriver>();
            driver.ServiceHost = "127.0.0.1";
            driver.ServicePort = 8710;
            driver.Slug = "ubc-nitobe-garden-creek";
            driver.ShowOverview = true;
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
