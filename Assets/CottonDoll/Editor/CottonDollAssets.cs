using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CottonDoll.Editor
{
    /// <summary>
    /// Scene preview and asset saving for the cotton doll.
    /// The preview is one root object ("CottonDoll Preview") that is never saved with the scene; its
    /// meshes and materials are temporary (not assets). Regenerating reuses them, so nothing piles up,
    /// and leftovers from cleared previews are swept on the next Generate/Clear. Saved dolls get their
    /// own mesh/material assets and a prefab that does not depend on this tool.
    /// </summary>
    public static class CottonDollAssets
    {
        public const string PreviewRootName = "CottonDoll Preview";
        const HideFlags PreviewFlags = HideFlags.DontSaveInEditor;
        const HideFlags TempAssetFlags = HideFlags.DontSave;

        static readonly Material[] previewMaterials = new Material[4];

        // ------------------------------------------------------------------------------------
        // Preview

        public static GameObject FindPreview(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == PreviewRootName && (go.hideFlags & HideFlags.DontSaveInEditor) != 0)
                    return go;
            return null;
        }

        /// <summary>Creates the preview, or updates it in place. Only touches the preview root and its children.</summary>
        public static GameObject CreateOrUpdatePreview(CottonDollSettings s, List<DollPart> parts, Scene scene, Vector3 position, Quaternion rotation)
        {
            var root = FindPreview(scene);
            if (root == null)
            {
                root = new GameObject(PreviewRootName) { hideFlags = PreviewFlags };
                SceneManager.MoveGameObjectToScene(root, scene);
                root.transform.SetPositionAndRotation(position, rotation);
                Undo.RegisterCreatedObjectUndo(root, "Create Cotton Doll Preview");
            }

            var wanted = new HashSet<string>(parts.Select(p => p.name));
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                var child = root.transform.GetChild(i).gameObject;
                if (!wanted.Contains(child.name)) Undo.DestroyObjectImmediate(child);
            }

            foreach (var part in parts)
            {
                var t = root.transform.Find(part.name);
                GameObject go;
                if (t == null)
                {
                    go = new GameObject(part.name, typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = PreviewFlags };
                    go.transform.SetParent(root.transform, false);
                    Undo.RegisterCreatedObjectUndo(go, "Create Cotton Doll Part");
                }
                else go = t.gameObject;

                var filter = go.GetComponent<MeshFilter>();
                var mesh = filter.sharedMesh;
                if (mesh == null || EditorUtility.IsPersistent(mesh) || !mesh.name.StartsWith(CottonDollGenerator.MeshPrefix))
                    mesh = new Mesh { hideFlags = TempAssetFlags };
                filter.sharedMesh = CottonDollGenerator.ToMesh(part, s.height, mesh);
                var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = PreviewMaterial(part.slot, s);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            SweepTemporaryResources();
            return root;
        }

        static Material PreviewMaterial(DollMaterialSlot slot, CottonDollSettings s)
        {
            int i = (int)slot;
            if (previewMaterials[i] == null)
                previewMaterials[i] = new Material(Shader.Find(CottonDollGenerator.ShaderName) ?? Shader.Find(CottonDollGenerator.FallbackShader)) { hideFlags = TempAssetFlags };
            return CottonDollGenerator.SetupMaterial(previewMaterials[i], slot, s);
        }

        /// <summary>Removes the preview (undoable). Its temporary meshes are swept on the next Generate/Clear.</summary>
        public static void ClearPreview(Scene scene)
        {
            var root = FindPreview(scene);
            if (root != null) Undo.DestroyObjectImmediate(root);
            SweepTemporaryResources();
        }

        /// <summary>
        /// Destroys temporary doll meshes and materials no live preview uses (left by cleared previews,
        /// closed scenes or earlier sessions). Assets are never touched.
        /// </summary>
        public static int SweepTemporaryResources()
        {
            var used = new HashSet<Object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var root = FindPreview(SceneManager.GetSceneAt(i));
                if (root == null) continue;
                foreach (var f in root.GetComponentsInChildren<MeshFilter>(true)) if (f.sharedMesh != null) used.Add(f.sharedMesh);
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true)) if (r.sharedMaterial != null) used.Add(r.sharedMaterial);
            }
            foreach (var m in previewMaterials) if (m != null) used.Add(m);

            int removed = 0;
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                if (IsTemporary(mesh) && !used.Contains(mesh)) { Object.DestroyImmediate(mesh); removed++; }
            foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
                if (IsTemporary(mat) && !used.Contains(mat)) { Object.DestroyImmediate(mat); removed++; }
            return removed;
        }

        static bool IsTemporary(Object o) =>
            o != null && o.name.StartsWith(CottonDollGenerator.MeshPrefix) && !EditorUtility.IsPersistent(o) &&
            (o.hideFlags & HideFlags.DontSave) == HideFlags.DontSave;

        /// <summary>Number of temporary doll meshes alive (diagnostics and tests).</summary>
        public static int TemporaryMeshCount() => Resources.FindObjectsOfTypeAll<Mesh>().Count(IsTemporary);

        // ------------------------------------------------------------------------------------
        // Saving

        public static string PrefabPath(string folder, string name) => $"{folder}/{name}.prefab";
        static string MeshPath(string folder, string name, string part) => $"{folder}/{name}_{part}.asset";
        static string MaterialPath(string folder, string name, DollMaterialSlot slot) => $"{folder}/{name}_{slot}.mat";
        static string SettingsPath(string folder, string name) => $"{folder}/{name}_Settings.asset";

        public static bool IsValidFolder(string folder) =>
            !string.IsNullOrEmpty(folder) && (folder == "Assets" || folder.StartsWith("Assets/")) && !folder.Contains("..");

        public static bool IsValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        /// <summary>Asset paths a save would write that already exist.</summary>
        public static List<string> ExistingPaths(string folder, string name, IEnumerable<DollPart> parts)
        {
            var paths = new List<string> { PrefabPath(folder, name), SettingsPath(folder, name) };
            paths.AddRange(parts.Select(p => MeshPath(folder, name, p.name)));
            paths.AddRange(parts.Select(p => p.slot).Distinct().Select(slot => MaterialPath(folder, name, slot)));
            return paths.Where(p => AssetDatabase.LoadMainAssetAtPath(p) != null).ToList();
        }

        /// <summary>
        /// Saves meshes (.asset), materials (.mat), a copy of the settings and a prefab that references
        /// only those assets. Existing assets at the same paths are updated in place (same GUIDs) only
        /// when <paramref name="overwrite"/> is true; otherwise nothing is written and null is returned.
        /// </summary>
        public static GameObject SaveAsAssets(CottonDollSettings s, List<DollPart> parts, string folder, string name, bool overwrite)
        {
            if (!IsValidFolder(folder) || !IsValidName(name) || parts.Count == 0) return null;
            if (!overwrite && ExistingPaths(folder, name, parts).Count > 0) return null;
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();

            var materials = new Dictionary<DollMaterialSlot, Material>();
            foreach (var slot in parts.Select(p => p.slot).Distinct())
            {
                string path = MaterialPath(folder, name, slot);
                var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                var m = CottonDollGenerator.SetupMaterial(existing, slot, s);
                m.name = $"{name}_{slot}";
                if (existing == null) AssetDatabase.CreateAsset(m, path);
                else EditorUtility.SetDirty(m);
                materials[slot] = m;
            }

            var meshes = new Dictionary<string, Mesh>();
            foreach (var part in parts)
            {
                string path = MeshPath(folder, name, part.name);
                var fresh = CottonDollGenerator.ToMesh(part, s.height);
                fresh.name = $"{name}_{part.name}";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing == null) { AssetDatabase.CreateAsset(fresh, path); meshes[part.name] = fresh; }
                else
                {
                    EditorUtility.CopySerialized(fresh, existing);
                    Object.DestroyImmediate(fresh);
                    meshes[part.name] = existing;
                }
            }

            string settingsPath = SettingsPath(folder, name);
            var savedSettings = AssetDatabase.LoadAssetAtPath<CottonDollSettings>(settingsPath);
            if (savedSettings == null)
            {
                savedSettings = Object.Instantiate(s);
                savedSettings.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(savedSettings, settingsPath);
            }
            else EditorUtility.CopySerialized(s, savedSettings);
            savedSettings.hideFlags = HideFlags.None;
            AssetDatabase.SaveAssets();

            // Prefab: pivot between the feet, facing +Z, no colliders, no generator components.
            var root = new GameObject(name);
            try
            {
                foreach (var part in parts)
                {
                    var go = new GameObject(part.name, typeof(MeshFilter), typeof(MeshRenderer));
                    go.transform.SetParent(root.transform, false);
                    go.GetComponent<MeshFilter>().sharedMesh = meshes[part.name];
                    go.GetComponent<MeshRenderer>().sharedMaterial = materials[part.slot];
                }
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(folder, name));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ------------------------------------------------------------------------------------
        // Preview scene

        /// <summary>
        /// Opens a new, unsaved scene with a camera, a light and a floor framed on the doll. Asks to
        /// save the current scenes first; returns false if the user cancels.
        /// </summary>
        public static bool OpenPreviewScene(CottonDollSettings s)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var cam = Camera.main;
            float h = s.height;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0.9f * h, 0.75f * h, 2.1f * h);
                cam.transform.LookAt(new Vector3(0f, 0.5f * h, 0f));
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.01f;
            }
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = Vector3.one * Mathf.Max(0.5f, h);
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            CreateOrUpdatePreview(s, CottonDollGenerator.Generate(s), scene, Vector3.zero, Quaternion.identity);
            return true;
        }
    }
}
