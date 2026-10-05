using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LowPolyTools
{
    [Serializable]
    public sealed class LowPolySettings
    {
        public bool simplify = true;
        [Range(0.02f, 1f)] public float targetRatio = 0.5f;
        [Range(0.001f, 0.1f)] public float maxError = 0.01f;
        public bool preserveBorders = true;
        public bool protectSeams = true;
        /// <summary>After simplifying, re-average smooth normals over the new, larger faces (hard edges kept).</summary>
        public bool recalculateNormals = true;
        public bool flatNormals;
        public string outputFolder = "Assets/LowPolyOutput";

        public SimplifyOptions ToSimplifyOptions() => new SimplifyOptions
        {
            targetRatio = targetRatio,
            maxError = maxError,
            preserveBorders = preserveBorders,
            protectSeams = protectSeams,
        };
    }

    /// <summary>A MeshFilter inside the source hierarchy that the tool will process.</summary>
    public sealed class LowPolyEntry
    {
        public string path;
        public int[] siblingPath;
        public MeshFilter filter;
        public Mesh mesh;
    }

    public sealed class LowPolyIssue
    {
        public string path;
        public string message;
    }

    public sealed class LowPolyAnalysis
    {
        public GameObject root;
        public readonly List<LowPolyEntry> entries = new List<LowPolyEntry>();
        /// <summary>Objects that are left untouched, with the reason.</summary>
        public readonly List<LowPolyIssue> skipped = new List<LowPolyIssue>();
        /// <summary>Things the user should know before exporting (colliders, LODs, data conversions).</summary>
        public readonly List<string> notes = new List<string>();
        public readonly List<Mesh> uniqueMeshes = new List<Mesh>();

        public int UsersOf(Mesh mesh)
        {
            int count = 0;
            foreach (var e in entries) if (e.mesh == mesh) count++;
            return count;
        }
    }

    public sealed class MeshConversionResult
    {
        public Mesh source;
        public Mesh result;
        public int verticesBefore, verticesAfter;
        public int trianglesBefore, trianglesAfter;
        public SimplifyReport simplify;
        public FlatNormalsReport flatNormals;

        public float TriangleReduction => trianglesBefore == 0 ? 0f : 1f - trianglesAfter / (float)trianglesBefore;
    }

    public sealed class LowPolyExportResult
    {
        public string prefabPath;
        public bool isVariant;
        public readonly List<string> meshPaths = new List<string>();
    }

    public static class LowPolyConverter
    {
        /// <summary>Returns null when the mesh can be processed safely, otherwise the reason it cannot.</summary>
        public static string GetMeshIssue(Mesh mesh)
        {
            if (mesh == null) return "MeshFilter không có mesh.";
            if (!mesh.isReadable)
                return "Mesh không đọc được (Read/Write đang tắt trong import settings). Công cụ không tự sửa import settings của model nguồn; hãy bật Read/Write nếu muốn xử lý.";
            if (mesh.vertexCount == 0) return "Mesh rỗng.";
            if (mesh.blendShapeCount > 0)
                return $"Mesh có {mesh.blendShapeCount} blend shape. Chưa hỗ trợ giảm mesh mà vẫn giữ blend shape nên bỏ qua.";
            if (mesh.bindposes.Length > 0 || mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
                return "Mesh có dữ liệu xương (bind poses / bone weights). Không chuyển mesh có rig thành mesh tĩnh nên bỏ qua.";
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var topology = mesh.GetTopology(s);
                if (topology != MeshTopology.Triangles)
                    return $"Submesh {s} dùng topology {topology}; công cụ chỉ hỗ trợ Triangles.";
            }
            return null;
        }

        public static LowPolyAnalysis Analyze(GameObject root)
        {
            var analysis = new LowPolyAnalysis { root = root };
            if (root == null) return analysis;

            foreach (var skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                analysis.skipped.Add(new LowPolyIssue
                {
                    path = PathOf(root.transform, skinned.transform),
                    message = "SkinnedMeshRenderer (có rig/blend shape). Chưa hỗ trợ an toàn nên giữ nguyên, không chuyển thành mesh tĩnh.",
                });
            }

            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                string path = PathOf(root.transform, filter.transform);
                var mesh = filter.sharedMesh;
                string issue = GetMeshIssue(mesh);
                if (issue != null)
                {
                    analysis.skipped.Add(new LowPolyIssue { path = path, message = issue });
                    continue;
                }

                if (filter.GetComponent<MeshRenderer>() == null)
                    analysis.notes.Add($"'{path}' có MeshFilter nhưng không có MeshRenderer; mesh vẫn được xử lý.");

                analysis.entries.Add(new LowPolyEntry
                {
                    path = path,
                    siblingPath = SiblingPath(root.transform, filter.transform),
                    filter = filter,
                    mesh = mesh,
                });
                if (!analysis.uniqueMeshes.Contains(mesh))
                    analysis.uniqueMeshes.Add(mesh);
            }

            foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true))
                analysis.notes.Add($"MeshCollider tại '{PathOf(root.transform, collider.transform)}' vẫn dùng mesh gốc (không được cập nhật).");
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                if (!(collider is MeshCollider))
                {
                    analysis.notes.Add("Các collider dạng primitive (Box/Sphere/Capsule) được giữ nguyên.");
                    break;
                }
            foreach (var lod in root.GetComponentsInChildren<LODGroup>(true))
                analysis.notes.Add($"LODGroup tại '{PathOf(root.transform, lod.transform)}' giữ nguyên cấu hình; mesh của mọi LOD đều được xử lý với cùng thông số.");
            if (root.GetComponentsInChildren<Animator>(true).Length > 0 || root.GetComponentsInChildren<Animation>(true).Length > 0)
                analysis.notes.Add("Có Animator/Animation: công cụ chỉ thay mesh, không chạm vào animation.");

            foreach (var mesh in analysis.uniqueMeshes)
            {
                for (int ch = 0; ch < MeshBuffers.MaxUVChannels; ch++)
                {
                    var attribute = VertexAttribute.TexCoord0 + ch;
                    if (mesh.HasVertexAttribute(attribute) && mesh.GetVertexAttributeDimension(attribute) == 1)
                        analysis.notes.Add($"Mesh '{mesh.name}': UV{ch} có 1 thành phần sẽ được lưu dạng 2 thành phần (giá trị không đổi).");
                }
                int users = analysis.UsersOf(mesh);
                if (users > 1)
                    analysis.notes.Add($"Mesh '{mesh.name}' được {users} object dùng chung; chỉ tạo một mesh kết quả cho tất cả.");
            }

            return analysis;
        }

        public static MeshConversionResult ConvertMesh(Mesh mesh, LowPolySettings settings, Func<float, bool> progress = null)
        {
            var buffers = MeshBuffers.FromMesh(mesh);
            var result = new MeshConversionResult
            {
                source = mesh,
                verticesBefore = buffers.VertexCount,
                trianglesBefore = buffers.TriangleCount,
            };

            if (settings.simplify)
            {
                result.simplify = new SimplifyReport();
                buffers = QemSimplifier.Simplify(buffers, settings.ToSimplifyOptions(), result.simplify, progress);
                if (settings.recalculateNormals && !settings.flatNormals)
                    SmoothNormals.Recalculate(buffers);
            }

            if (settings.flatNormals)
            {
                result.flatNormals = new FlatNormalsReport();
                buffers = FlatNormals.Apply(buffers, result.flatNormals);
            }

            var output = buffers.ToMesh(mesh.name + "_LowPoly");
            // Keep the source tangent channel meaningful after the topology changed.
            if (output.HasVertexAttribute(VertexAttribute.Tangent) && output.HasVertexAttribute(VertexAttribute.TexCoord0) && !settings.flatNormals)
                output.RecalculateTangents();
            output.hideFlags = HideFlags.DontSave;

            result.result = output;
            result.verticesAfter = buffers.VertexCount;
            result.trianglesAfter = buffers.TriangleCount;
            return result;
        }

        /// <summary>
        /// Converts every unique mesh. Returns null if the user cancelled; nothing is written to disk here.
        /// </summary>
        public static Dictionary<Mesh, MeshConversionResult> Convert(LowPolyAnalysis analysis, LowPolySettings settings,
            Func<string, float, bool> progress = null)
        {
            var results = new Dictionary<Mesh, MeshConversionResult>();
            int count = analysis.uniqueMeshes.Count;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var mesh = analysis.uniqueMeshes[i];
                    int index = i;
                    Func<float, bool> meshProgress = null;
                    if (progress != null)
                    {
                        meshProgress = f => progress($"{mesh.name} ({index + 1}/{count})", (index + f) / count);
                        if (meshProgress(0f)) throw new OperationCanceledException();
                    }
                    results.Add(mesh, ConvertMesh(mesh, settings, meshProgress));
                }
            }
            catch (OperationCanceledException)
            {
                DestroyResults(results);
                return null;
            }
            return results;
        }

        public static void DestroyResults(Dictionary<Mesh, MeshConversionResult> results)
        {
            if (results == null) return;
            foreach (var r in results.Values)
                if (r.result != null && !EditorUtility.IsPersistent(r.result))
                    Object.DestroyImmediate(r.result);
            results.Clear();
        }

        /// <summary>
        /// Writes new mesh assets and a new prefab (a variant when the source is a prefab or model asset).
        /// Source assets are never modified. On failure every asset created by this call is deleted.
        /// </summary>
        public static LowPolyExportResult Export(LowPolyAnalysis analysis, Dictionary<Mesh, MeshConversionResult> results,
            LowPolySettings settings)
        {
            var root = analysis.root;
            var export = new LowPolyExportResult();
            var created = new List<string>();
            var previewScene = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;

            try
            {
                string baseFolder = EnsureFolder(settings.outputFolder);
                string folder = AssetDatabase.GenerateUniqueAssetPath($"{baseFolder}/{Sanitize(root.name)}_LowPoly");
                string folderGuid = AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
                folder = AssetDatabase.GUIDToAssetPath(folderGuid);
                created.Add(folder);

                var saved = new Dictionary<Mesh, Mesh>();
                foreach (var pair in results)
                {
                    var copy = Object.Instantiate(pair.Value.result);
                    copy.name = pair.Value.result.name;
                    copy.hideFlags = HideFlags.None;
                    string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(copy.name)}.asset");
                    AssetDatabase.CreateAsset(copy, meshPath);
                    export.meshPaths.Add(meshPath);
                    saved.Add(pair.Key, copy);
                }

                bool isPrefabAsset = EditorUtility.IsPersistent(root) && root.transform.parent == null &&
                                     PrefabUtility.IsPartOfPrefabAsset(root);
                if (isPrefabAsset)
                {
                    instance = (GameObject)PrefabUtility.InstantiatePrefab(root, previewScene);
                }
                else
                {
                    // Plain copy that keeps the root's local transform; built inside a preview scene so
                    // no open scene is touched.
                    var holder = new GameObject("LowPolyExportHolder");
                    EditorSceneManager.MoveGameObjectToScene(holder, previewScene);
                    instance = Object.Instantiate(root, holder.transform, false);
                    instance.name = root.name;
                    instance.transform.SetParent(null, false);
                    Object.DestroyImmediate(holder);
                }

                foreach (var entry in analysis.entries)
                {
                    var target = Resolve(instance.transform, entry.siblingPath);
                    var filter = target != null ? target.GetComponent<MeshFilter>() : null;
                    if (filter == null)
                        throw new InvalidOperationException($"Không tìm thấy MeshFilter tương ứng cho '{entry.path}' trong bản sao.");
                    filter.sharedMesh = saved[entry.mesh];
                }

                string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(root.name)}_LowPoly.prefab");
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool success);
                if (!success)
                    throw new InvalidOperationException("Không lưu được prefab kết quả.");

                export.prefabPath = prefabPath;
                export.isVariant = PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)) ==
                                   PrefabAssetType.Variant;
                AssetDatabase.SaveAssets();
                return export;
            }
            catch
            {
                foreach (string path in created)
                    AssetDatabase.DeleteAsset(path);
                throw;
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static string EnsureFolder(string folder)
        {
            folder = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/"))
                throw new ArgumentException("Thư mục xuất phải nằm trong Assets.");
            if (AssetDatabase.IsValidFolder(folder)) return folder;

            string parent = EnsureFolder(Path.GetDirectoryName(folder).Replace('\\', '/'));
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            return folder;
        }

        static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Mesh" : name.Trim();
        }

        public static string PathOf(Transform root, Transform t)
        {
            if (t == root) return root.name;
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
            return root.name + "/" + string.Join("/", parts);
        }

        static int[] SiblingPath(Transform root, Transform t)
        {
            var path = new List<int>();
            for (var x = t; x != null && x != root; x = x.parent) path.Insert(0, x.GetSiblingIndex());
            return path.ToArray();
        }

        static Transform Resolve(Transform root, int[] siblingPath)
        {
            var t = root;
            foreach (int i in siblingPath)
            {
                if (i >= t.childCount) return null;
                t = t.GetChild(i);
            }
            return t;
        }
    }
}
