using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ShoalingUpstream.Config
{
    /// <summary>
    /// Where packaged configuration and audio sit inside the build.
    ///
    /// tools/export-to-unity.mjs writes this layout and the runtime reads it. Nothing else
    /// enforces that the two agree, and a disagreement is invisible until a visitor is standing
    /// in a creek hearing nothing — so the layout is named once, here, and both sides quote it.
    /// </summary>
    public static class BundleLayout
    {
        public const string RootFolder = "ShoalingUpstream";
        public const string JourneysFolder = "journeys";
        public const string AudioFolder = "audio";
        public const string ManifestFile = "manifest.json";
        public const string AudioIndexFile = "index.json";
        public const string ClipExtension = ".mp3";

        /// <summary>
        /// The packaged root inside the built app.
        ///
        /// On iOS StreamingAssets is an ordinary directory inside the .app bundle, so plain
        /// System.IO reads it. That is not true on Android, where it lives inside the APK and
        /// needs UnityWebRequest; this project is iOS-only and the file readers here take the
        /// simpler route deliberately.
        /// </summary>
        public static string StreamingAssetsRoot =>
            Path.Combine(Application.streamingAssetsPath, RootFolder);

        public static string ManifestPath(string root) => Path.Combine(root, ManifestFile);

        public static string JourneysDirectory(string root) => Path.Combine(root, JourneysFolder);

        public static string JourneyPath(string root, string slug) =>
            Path.Combine(root, JourneysFolder, slug + ".json");

        public static string AudioDirectory(string root) => Path.Combine(root, AudioFolder);

        public static string AudioIndexPath(string root) =>
            Path.Combine(root, AudioFolder, AudioIndexFile);

        public static string ClipPath(string audioDirectory, string clipId) =>
            Path.Combine(audioDirectory, clipId + ClipExtension);
    }

    /// <summary>
    /// What the exporter put in the build: which sites are bundled, and which one this build is
    /// for. Read at launch so a device with two sites packaged still knows where it is going.
    /// </summary>
    [Serializable]
    public class BundleManifest
    {
        public string defaultSlug;
        public List<BundledSite> sites = new();

        public BundledSite Find(string slug) => sites.Find(s => s != null && s.slug == slug);

        /// <summary>Reads the manifest beside the packaged journeys. Returns null when absent —
        /// an unpackaged build is a build error, not a runtime one, so the caller reports it.</summary>
        public static BundleManifest Load(string root)
        {
            string path = BundleLayout.ManifestPath(root);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<BundleManifest>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Config] bundled manifest at {path} is unreadable: {e.Message}");
                return null;
            }
        }
    }

    [Serializable]
    public class BundledSite
    {
        public string slug;
        public string title;
        public int revision;
        public bool calibrated;
    }
}
