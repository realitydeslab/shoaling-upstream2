using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ShoalingUpstream.Simulation;
using UnityEditor;
using UnityEngine;

namespace ShoalingUpstream.EditorTools
{
    public static class OrganicModelAudit
    {
        [Serializable] private class Item
        {
            public string name;
            public Vector3 position, renderedCentre, renderedSize;
            public int vertices, activeRenderers;
            public List<string> shaders = new();
        }
        [Serializable] private class Report { public List<Item> models = new(); }
        public static void RenderFromCommandLine()
        {
            SimulationSceneBuilder.BuildStandaloneAr();
            var driver = UnityEngine.Object.FindFirstObjectByType<SimulationDriver>();
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../validation"));
            Directory.CreateDirectory(output);
            var report = new Report();
            var camera = new GameObject("Model audit camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.38f, .46f, .5f);
            camera.fieldOfView = 42f;
            camera.nearClipPlane = .03f;
            var texture = new RenderTexture(1440, 960, 24);
            camera.targetTexture = texture;
            foreach (string beat in new[] { "beat-5", "beat-6" })
            {
                var models = new List<GameObject>();
                foreach (var trigger in driver.SceneTriggers)
                {
                    if (trigger.BeatId != beat || trigger.Template == null) continue;
                    int count = beat == "beat-5" ? 3 : 1;
                    for (int i = 0; i < count; i++)
                    {
                        var model = UnityEngine.Object.Instantiate(trigger.Template);
                        typeof(SimulationDriver).GetMethod("ScaleLocalVisuals", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, new object[] { model });
                        if (beat == "beat-6") typeof(SimulationDriver).GetMethod("EnlargeHeronVisuals", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { model });
                        model.SetActive(true);
                        model.transform.position = beat == "beat-5" ? new Vector3((i - 1) * .4f, .2f, i % 2 * .15f)
                            : new Vector3(-trigger.AnchorOffsetM.x / 3f - .47f, 0f, 0f);
                        foreach (var animator in model.GetComponentsInChildren<Animator>())
                        { animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Update(0f); }
                        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>()) renderer.updateWhenOffscreen = true;
                        var item = new Item { name = model.name, position = model.transform.position };
                        bool first = true; Bounds bounds = default;
                        foreach (var renderer in model.GetComponentsInChildren<Renderer>())
                        {
                            if (renderer.enabled && renderer.gameObject.activeInHierarchy) item.activeRenderers++;
                            if (first) { bounds = renderer.bounds; first = false; } else bounds.Encapsulate(renderer.bounds);
                            if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null) item.vertices += skin.sharedMesh.vertexCount;
                            var filter = renderer.GetComponent<MeshFilter>(); if (filter != null && filter.sharedMesh != null) item.vertices += filter.sharedMesh.vertexCount;
                            foreach (var material in renderer.sharedMaterials) item.shaders.Add(material != null && material.shader != null ? material.shader.name : "MISSING");
                        }
                        item.renderedCentre = bounds.center; item.renderedSize = bounds.size; report.models.Add(item);
                        models.Add(model);
                    }
                }
                camera.transform.position = beat == "beat-5" ? new Vector3(0f, .7f, -2.1f) : new Vector3(0f, .9f, -2.9f);
                camera.transform.LookAt(new Vector3(0f, beat == "beat-5" ? .2f : .65f, 0f));
                camera.Render();
                RenderTexture.active = texture;
                var pixels = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); pixels.Apply();
                File.WriteAllBytes(Path.Combine(output, beat == "beat-5" ? "strider-model-audit.png" : "heron-group-audit.png"), pixels.EncodeToPNG());
                RenderTexture.active = null; UnityEngine.Object.DestroyImmediate(pixels);
                foreach (var model in models) UnityEngine.Object.DestroyImmediate(model);
            }
            File.WriteAllText(Path.Combine(output, "organic-model-audit.json"), JsonUtility.ToJson(report, true));
            Debug.Log("[organic audit] Rendered real models and recorded meshes/materials/bounds");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
