using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RockGen.Editor
{
    /// <summary>Shared material and saving of generated rocks as .asset mesh + prefab.</summary>
    public static class RockAssets
    {
        public const string MaterialPath = "Assets/RockGen/Materials/Rock.mat";
        public const string DefaultSaveFolder = "Assets/RockGen/Generated";

        /// <summary>One material for every rock (preview and saved prefabs), created on first use.</summary>
        public static Material SharedMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null) return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = "Rock" };
            var stone = new Color(0.72f, 0.69f, 0.64f);
            m.SetColor("_BaseColor", stone);
            m.SetColor("_Color", stone);
            m.SetFloat("_Smoothness", 0.08f);
            m.SetFloat("_Glossiness", 0.08f);
            m.SetFloat("_Metallic", 0f);
            EnsureFolder(Path.GetDirectoryName(MaterialPath).Replace('\\', '/'));
            AssetDatabase.CreateAsset(m, MaterialPath);
            return m;
        }

        public struct SaveResult
        {
            public string meshPath;
            public string prefabPath;
        }

        /// <summary>
        /// Saves a copy of <paramref name="mesh"/> as a new .asset and a prefab that references it.
        /// Never overwrites: existing names get a numeric suffix. No collider is added.
        /// </summary>
        public static SaveResult Save(Mesh mesh, string name, string folder)
        {
            folder = EnsureFolder(string.IsNullOrWhiteSpace(folder) ? DefaultSaveFolder : folder);
            name = Sanitize(name);

            var copy = Object.Instantiate(mesh);
            copy.name = name;
            copy.hideFlags = HideFlags.None;
            string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.asset");
            AssetDatabase.CreateAsset(copy, meshPath);

            var go = new GameObject(name);
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = copy;
                go.AddComponent<MeshRenderer>().sharedMaterial = SharedMaterial();
                string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.prefab");
                PrefabUtility.SaveAsPrefabAsset(go, prefabPath, out bool ok);
                if (!ok)
                {
                    AssetDatabase.DeleteAsset(meshPath);
                    throw new InvalidOperationException($"Could not save prefab {prefabPath}");
                }
                AssetDatabase.SaveAssets();
                return new SaveResult { meshPath = meshPath, prefabPath = prefabPath };
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        public static string EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/"))
                throw new ArgumentException("The save folder must be inside Assets.");
            if (AssetDatabase.IsValidFolder(folder)) return folder;
            string parent = EnsureFolder(Path.GetDirectoryName(folder).Replace('\\', '/'));
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            return folder;
        }

        static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Rock" : name.Trim();
        }
    }
}
