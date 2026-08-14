using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using ShoalingUpstream.Config;
using Debug = UnityEngine.Debug;

namespace ShoalingUpstream.Editor
{
    /// <summary>
    /// The artist's way to put a published journey and its audio into the build.
    ///
    /// It runs tools/export-to-unity.mjs rather than reimplementing it. A second implementation
    /// of the packaging rules would be a second set of rules the moment either changed, and the
    /// rule that matters — refuse to ship a journey whose clips are missing — is precisely the
    /// one that must not have two versions. So this window is a form over a command line, and
    /// the terminal output is shown verbatim.
    /// </summary>
    public class BundleExportWindow : EditorWindow
    {
        private const string PrefsPrefix = "ShoalingUpstream.Export.";

        private string[] _slugs = Array.Empty<string>();
        private int _slugIndex;
        private bool _allowUncalibrated;
        private bool _fromService;
        private string _serviceHost = "http://localhost:8710";
        private bool _makeDefault;
        private bool _prune;
        private string _output = "";
        private Vector2 _scroll;
        private bool _running;

        [MenuItem("Tools/Shoaling Upstream/Package Journey and Audio…", false, 0)]
        public static void Open()
        {
            var window = GetWindow<BundleExportWindow>(true, "Package Journey and Audio");
            window.minSize = new Vector2(520, 420);
            window.Refresh();
        }

        private void OnEnable()
        {
            _allowUncalibrated = EditorPrefs.GetBool(PrefsPrefix + "allowUncalibrated", false);
            _fromService = EditorPrefs.GetBool(PrefsPrefix + "fromService", false);
            _serviceHost = EditorPrefs.GetString(PrefsPrefix + "serviceHost", _serviceHost);
            Refresh();
        }

        /// <summary>The repo root, two levels above Assets/.</summary>
        private static string RepositoryRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        private void Refresh()
        {
            string journeys = Path.Combine(RepositoryRoot, "data", "journeys");
            var slugs = new List<string>();
            if (Directory.Exists(journeys))
            {
                foreach (string dir in Directory.GetDirectories(journeys))
                {
                    slugs.Add(Path.GetFileName(dir));
                }
            }
            slugs.Sort(StringComparer.Ordinal);
            _slugs = slugs.ToArray();

            var manifest = BundleManifest.Load(BundleLayout.StreamingAssetsRoot);
            if (manifest != null && !string.IsNullOrEmpty(manifest.defaultSlug))
            {
                int index = Array.IndexOf(_slugs, manifest.defaultSlug);
                if (index >= 0) _slugIndex = index;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Repository", RepositoryRoot, EditorStyles.miniLabel);
            EditorGUILayout.Space();

            if (_slugs.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No sites found under data/journeys. Run the service and seed a site first.",
                    MessageType.Warning);
                if (GUILayout.Button("Look again")) Refresh();
                return;
            }

            _slugIndex = EditorGUILayout.Popup("Site", Mathf.Clamp(_slugIndex, 0, _slugs.Length - 1), _slugs);

            _fromService = EditorGUILayout.Toggle(
                new GUIContent("Read from service",
                    "Fetch GET /published from the running service instead of reading the "
                    + "revisions on disk. Both give the same document; the service is the one "
                    + "to use when the editor is open on another machine."),
                _fromService);
            using (new EditorGUI.DisabledScope(!_fromService))
            {
                _serviceHost = EditorGUILayout.TextField("Service", _serviceHost);
            }

            _allowUncalibrated = EditorGUILayout.Toggle(
                new GUIContent("Allow uncalibrated",
                    "Package a journey whose coordinates have never been matched to three "
                    + "physical points. The app runs it in simulation and refuses it on device."),
                _allowUncalibrated);

            _makeDefault = EditorGUILayout.Toggle(
                new GUIContent("Launch into this site",
                    "Make this the site the build opens with. Other packaged sites stay in "
                    + "the build."),
                _makeDefault);

            _prune = EditorGUILayout.Toggle(
                new GUIContent("Delete unreferenced clips",
                    "Remove packaged clips that no bundled journey plays."),
                _prune);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_running))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Check", GUILayout.Height(28))) Run(check: true);
                if (GUILayout.Button("Package", GUILayout.Height(28))) Run(check: false);
            }

            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(_output, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private void Run(bool check)
        {
            EditorPrefs.SetBool(PrefsPrefix + "allowUncalibrated", _allowUncalibrated);
            EditorPrefs.SetBool(PrefsPrefix + "fromService", _fromService);
            EditorPrefs.SetString(PrefsPrefix + "serviceHost", _serviceHost);

            var arguments = new StringBuilder("tools/export-to-unity.mjs");
            arguments.Append($" --site {_slugs[Mathf.Clamp(_slugIndex, 0, _slugs.Length - 1)]}");
            if (check) arguments.Append(" --check");
            if (_allowUncalibrated) arguments.Append(" --allow-uncalibrated");
            if (_makeDefault) arguments.Append(" --default");
            if (_prune) arguments.Append(" --prune");
            if (_fromService) arguments.Append($" --service {_serviceHost}");

            _running = true;
            try
            {
                _output = RunNode(arguments.ToString(), out int exitCode);
                if (!check && exitCode == 0)
                {
                    AssetDatabase.Refresh();
                }
                if (exitCode != 0)
                {
                    // Loud on purpose. The most valuable thing this window does is refuse, and
                    // a refusal that only appears in a text area is a refusal that gets missed.
                    Debug.LogError($"[Export] export-to-unity.mjs exited {exitCode}\n{_output}");
                }
                else
                {
                    Debug.Log($"[Export]\n{_output}");
                }
            }
            finally
            {
                _running = false;
                Repaint();
            }
        }

        /// <summary>
        /// Run the script through a login shell.
        ///
        /// Unity launched from Finder inherits a minimal PATH that does not contain node,
        /// wherever it was installed from — homebrew, nvm, or a pkg. `bash -lc` reads the same
        /// profile the artist's terminal does, so the node they already use for the service is
        /// the node that runs here.
        /// </summary>
        private static string RunNode(string arguments, out int exitCode)
        {
            var info = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-lc \"cd '{RepositoryRoot}' && node {arguments}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using var process = Process.Start(info);
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(120_000);
                exitCode = process.HasExited ? process.ExitCode : -1;
                return string.Concat(stdout, stderr);
            }
            catch (Exception e)
            {
                exitCode = -1;
                return $"could not run node: {e.Message}\n\n"
                     + "This window shells out to tools/export-to-unity.mjs. If node is not on "
                     + "the PATH of a login shell, run it from a terminal instead:\n\n"
                     + $"  cd {RepositoryRoot}\n  node {arguments}";
            }
        }
    }
}
