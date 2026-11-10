// Меню Tools > AR Maze — быстрая настройка сцены под MazeGame.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MazeSetupTool
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    static Transform FindMaze(out Transform ball)
    {
        ball = null;
        var target = GameObject.Find("ImageTarget");
        if (target == null) return null;
        var maze = target.transform.Find("mazebrickwall");
        if (maze != null) ball = maze.Find("Sphere");
        return maze;
    }

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    // Рисует карту лабиринта сверху (для поиска выхода). Запуск: -executeMethod MazeSetupTool.DumpMap -mapOut <file>
    public static void DumpMap()
    {
        EditorSceneManager.OpenScene(ScenePath);
        var maze = FindMaze(out var ball);
        var sb = new StringBuilder();
        if (maze == null) { sb.AppendLine("maze not found"); File.WriteAllText(Arg("-mapOut") ?? "MazeMap.txt", sb.ToString()); return; }

        sb.AppendLine($"maze lossyScale={maze.lossyScale} ball={(ball ? maze.InverseTransformPoint(ball.position).ToString("F3") : "none")}");
        foreach (var c in maze.GetComponentsInChildren<Collider>(true))
        {
            var b = c.bounds;
            sb.AppendLine($"collider {c.GetType().Name} on '{c.name}' trigger={c.isTrigger} localMin={maze.InverseTransformPoint(b.min):F3} localMax={maze.InverseTransformPoint(b.max):F3}");
            if (c is MeshCollider mc && mc.sharedMesh) sb.AppendLine($"   mesh={mc.sharedMesh.name} readable={mc.sharedMesh.isReadable} path={AssetDatabase.GetAssetPath(mc.sharedMesh)}");
        }

        Physics.SyncTransforms();
        MeshCollider wall = null;
        foreach (var mc in maze.GetComponentsInChildren<MeshCollider>(true)) wall = mc;
        if (wall == null) { File.WriteAllText(Arg("-mapOut") ?? "MazeMap.txt", sb.ToString()); return; }

        Vector3 lmin = maze.InverseTransformPoint(wall.bounds.min), lmax = maze.InverseTransformPoint(wall.bounds.max);
        Vector3 mn = Vector3.Min(lmin, lmax), mx = Vector3.Max(lmin, lmax);
        int nx = 64, nz = 64;
        var h = new float[nx, nz];
        float hmin = float.MaxValue, hmax = float.MinValue;
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < nz; j++)
        {
            float x = Mathf.Lerp(mn.x, mx.x, (i + 0.5f) / nx);
            float z = Mathf.Lerp(mn.z, mx.z, (j + 0.5f) / nz);
            Vector3 o = maze.TransformPoint(new Vector3(x, mx.y + 1f, z));
            h[i, j] = float.NaN;
            if (wall.Raycast(new Ray(o, -maze.up), out var hit, 1000f))
            {
                float y = maze.InverseTransformPoint(hit.point).y;
                h[i, j] = y;
                hmin = Mathf.Min(hmin, y); hmax = Mathf.Max(hmax, y);
            }
        }
        sb.AppendLine($"grid x:[{mn.x:F3},{mx.x:F3}] z:[{mn.z:F3},{mx.z:F3}] heights {hmin:F3}..{hmax:F3}");
        Vector3 bl = ball ? maze.InverseTransformPoint(ball.position) : Vector3.positiveInfinity;
        // строки сверху вниз = z от max к min
        for (int j = nz - 1; j >= 0; j--)
        {
            float z = Mathf.Lerp(mn.z, mx.z, (j + 0.5f) / nz);
            sb.Append($"{z,7:F3} ");
            for (int i = 0; i < nx; i++)
            {
                float x = Mathf.Lerp(mn.x, mx.x, (i + 0.5f) / nx);
                char ch = float.IsNaN(h[i, j]) ? ' ' : (h[i, j] > hmin + 0.5f * (hmax - hmin) ? '#' : '.');
                if (Mathf.Abs(x - bl.x) < (mx.x - mn.x) / nx && Mathf.Abs(z - bl.z) < (mx.z - mn.z) / nz) ch = 'B';
                sb.Append(ch);
            }
            sb.AppendLine();
        }
        File.WriteAllText(Arg("-mapOut") ?? "MazeMap.txt", sb.ToString());
    }

    [MenuItem("Tools/AR Maze/Setup Scene")]
    public static void SetupFromMenu() => Setup(null);

    // Запуск из командной строки: -executeMethod MazeSetupTool.SetupBatch -finishLocal x,y,z
    public static void SetupBatch()
    {
        Vector3? fin = null;
        var s = Arg("-finishLocal");
        if (s != null)
        {
            var p = s.Split(',');
            fin = new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture));
        }
        Setup(fin);
    }

    static void Setup(Vector3? finishLocal)
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        var maze = FindMaze(out var ball);
        if (maze == null || ball == null) { Debug.LogError("[MazeSetup] Не нашёл ImageTarget/mazebrickwall/Sphere"); return; }

        // Read/Write у модели лабиринта — нужно, чтобы во время игры скопировать её MeshCollider
        foreach (var mc in maze.GetComponentsInChildren<MeshCollider>(true))
        {
            if (mc.sharedMesh == null) continue;
            var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mc.sharedMesh)) as ModelImporter;
            if (imp != null && !imp.isReadable) { imp.isReadable = true; imp.SaveAndReimport(); }
        }

        var game = maze.GetComponent<MazeGame>();
        if (game == null) game = Undo.AddComponent<MazeGame>(maze.gameObject);
        game.ball = ball;
        var cam = GameObject.Find("ARCamera");
        if (cam != null) game.arCamera = cam.GetComponent<Camera>();

        var fin = maze.Find("Finish");
        if (fin == null)
        {
            fin = new GameObject("Finish").transform;
            fin.SetParent(maze, false);
        }
        if (finishLocal.HasValue) fin.localPosition = finishLocal.Value;
        game.finish = fin;
        game.finishRadius = 0.18f; // выход шириной ~0.37, лунка помещается в проход

        // Заглушка входа: раньше стояла мимо щели (x -0.46..0.28 при щели -0.89..-0.53) — шарик выкатывался сверху.
        var plug = maze.Find("Cube (1)");
        if (plug != null)
        {
            plug.localRotation = Quaternion.identity;
            plug.localPosition = new Vector3(-0.325f, plug.localPosition.y, 2.72f);
            plug.localScale = new Vector3(1.25f, plug.localScale.y, 0.46f);
            EditorUtility.SetDirty(plug);
        }

        // Старые скрипты/звук шарика больше не нужны: всем управляет MazeGame
        foreach (var mb in ball.GetComponents<MonoBehaviour>()) { mb.enabled = false; EditorUtility.SetDirty(mb); }
        var src = ball.GetComponent<AudioSource>();
        if (src != null)
        {
            if (game.rollSound == null) game.rollSound = src.clip != null ? src.clip : src.resource as AudioClip;
            src.enabled = false;
            EditorUtility.SetDirty(src);
        }

        EditorUtility.SetDirty(game);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[MazeSetup] Готово: MazeGame настроен, финиш = " + fin.localPosition.ToString("F3"));
    }
}
