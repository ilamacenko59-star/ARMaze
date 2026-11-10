// Сборка APK из командной строки: -executeMethod MazeBuild.BuildAndroid
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class MazeBuild
{
    [MenuItem("Tools/AR Maze/Build Android APK")]
    public static void BuildAndroid()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SampleScene.unity" };

        EditorUserBuildSettings.buildAppBundle = false;
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = "Builds/lab.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        Debug.Log($"[MazeBuild] result={report.summary.result} size={report.summary.totalSize} errors={report.summary.totalErrors} scenes={string.Join(",", scenes)}");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
