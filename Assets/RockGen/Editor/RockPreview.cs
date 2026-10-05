using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RockGen.Editor
{
    /// <summary>
    /// Owns the preview objects and meshes the tool puts in the scene. Everything lives under one
    /// parent that is never saved with the scene; Clear only touches what this class created.
    /// </summary>
    public sealed class RockPreview
    {
        public const string RootName = "[RockGen Preview]";

        GameObject root;
        readonly List<GameObject> rocks = new List<GameObject>();
        readonly List<Mesh> meshes = new List<Mesh>();

        public IReadOnlyList<GameObject> Rocks => rocks;
        public int VertexCount { get; private set; }
        public int TriangleCount { get; private set; }

        public bool Owns(GameObject go) => go != null && rocks.Contains(go);

        /// <summary>Replaces the current preview with the given settings, laid out on a grid.</summary>
        public void Show(IList<RockSettings> settings)
        {
            Clear();
            EnsureRoot();

            var material = RockAssets.SharedMaterial();
            float spacing = 0f;
            foreach (var s in settings)
            {
                var c = s.Clamped();
                // Taller rocks get more room so they do not hide each other in an angled view.
                spacing = Mathf.Max(spacing, Mathf.Max(c.size.x, c.size.z) * 1.6f + c.size.y * 0.6f);
            }
            int columns = Mathf.CeilToInt(Mathf.Sqrt(settings.Count));

            VertexCount = TriangleCount = 0;
            for (int i = 0; i < settings.Count; i++)
            {
                var data = RockGenerator.Generate(settings[i]);
                var mesh = data.ToMesh();
                mesh.hideFlags = HideFlags.DontSave;
                meshes.Add(mesh);

                var go = new GameObject($"Rock_{data.settings.seed}") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(root.transform, false);
                int row = i / columns, col = i % columns;
                go.transform.localPosition = new Vector3((col - (columns - 1) * 0.5f) * spacing, 0f, -row * spacing);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                rocks.Add(go);

                VertexCount += mesh.vertexCount;
                TriangleCount += data.TriangleCount;
            }
        }

        public void Clear()
        {
            foreach (var go in rocks)
                if (go != null) Object.DestroyImmediate(go);
            rocks.Clear();
            foreach (var m in meshes)
                if (m != null) Object.DestroyImmediate(m);
            meshes.Clear();
            VertexCount = TriangleCount = 0;

            if (root != null && root.transform.childCount == 0)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
        }

        void EnsureRoot()
        {
            if (root != null) return;
            root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
        }

        /// <summary>Removes a preview root left behind by an earlier session (e.g. after a script reload).</summary>
        public static void RemoveOrphans()
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.name != RootName || go.transform.parent != null) continue;
                if (EditorUtility.IsPersistent(go) || (go.hideFlags & HideFlags.DontSave) != HideFlags.DontSave) continue;
                foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && !EditorUtility.IsPersistent(filter.sharedMesh))
                        Object.DestroyImmediate(filter.sharedMesh);
                Object.DestroyImmediate(go);
            }
        }
    }
}
