using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace ShoalingUpstream.EditorTools
{
    public static class StandaloneIosBuild
    {
        private static string _savedTeam, _savedBundle, _savedSignStyle;
        public static void ExportFromCommandLine()
        {
            SimulationSceneBuilder.BuildStandaloneAr();
            string destination = Path.GetFullPath(Path.Combine(Application.dataPath, "../../builds/Standalone-iOS"));
            string existingProject = PBXProject.GetPBXProjectPath(destination);
            if (File.Exists(existingProject))
            {
                // Preserve the signing choices made during the user's first device install.
                File.Copy(existingProject, existingProject + ".before-export", true);
                var existing = new PBXProject();
                existing.ReadFromFile(existingProject);
                string main = existing.GetUnityMainTargetGuid();
                _savedTeam = existing.GetBuildPropertyForAnyConfig(main, "DEVELOPMENT_TEAM");
                _savedBundle = existing.GetBuildPropertyForAnyConfig(main, "PRODUCT_BUNDLE_IDENTIFIER");
                _savedSignStyle = existing.GetBuildPropertyForAnyConfig(main, "CODE_SIGN_STYLE");
            }
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { SimulationSceneBuilder.StandaloneScenePath },
                locationPathName = destination,
                target = BuildTarget.iOS,
                options = BuildOptions.None
            });
            bool succeeded = report.summary.result == BuildResult.Succeeded;
            Debug.Log($"[standalone] iOS export: {report.summary.result}; errors: {report.summary.totalErrors}");
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        [PostProcessBuild(100)]
        public static void EnableNativeModules(BuildTarget target, string directory)
        {
            if (target != BuildTarget.iOS) return;
            // Existing PhaseBridge.m imports Apple frameworks through Objective-C modules.
            string path = PBXProject.GetPBXProjectPath(directory);
            var project = new PBXProject();
            project.ReadFromFile(path);
            project.SetBuildProperty(project.GetUnityFrameworkTargetGuid(), "CLANG_ENABLE_MODULES", "YES");
            string main = project.GetUnityMainTargetGuid();
            if (!string.IsNullOrEmpty(_savedTeam)) project.SetBuildProperty(main, "DEVELOPMENT_TEAM", _savedTeam);
            if (!string.IsNullOrEmpty(_savedBundle)) project.SetBuildProperty(main, "PRODUCT_BUNDLE_IDENTIFIER", _savedBundle);
            if (!string.IsNullOrEmpty(_savedSignStyle)) project.SetBuildProperty(main, "CODE_SIGN_STYLE", _savedSignStyle);
            project.WriteToFile(path);
        }
    }
}
