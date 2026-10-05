using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PastelLowPoly.Editor
{
    [Serializable]
    public sealed class PastelConvertSettings
    {
        public bool pastelize = true;
        public bool flatNormals = true;
        [Range(0, 1)] public float pastelAmount = 1f;
        [Range(0, 1)] public float lightMin = 0.6f;
        [Range(0, 1)] public float lightMax = 0.93f;
        [Range(0, 2)] public float saturation = 0.75f;
        [Range(0, 0.3f)] public float chromaMax = 0.11f;
        [Range(0, 8)] public float textureDetailBlur;
        public string outputFolder = "Assets/PastelLowPoly/Converted";
    }

    /// <summary>What the converter could read from one source material.</summary>
    public sealed class SourceMaterialInfo
    {
        public Material source;
        public string textureProperty;
        public Texture texture;
        public Vector2 textureScale = Vector2.one;
        public Vector2 textureOffset = Vector2.zero;
        public Color color = Color.white;
        public bool cutout;
        public float cutoff = 0.5f;
        public bool wasTransparent;
        public bool doubleSided;
        public bool vertexColors;
        public bool vertexColorsAreLinear;
        public bool alreadyPastel;
        public readonly List<string> notes = new List<string>();
    }

    public sealed class PastelConvertResult
    {
        public string prefabPath;
        public bool appliedToScene;
        public readonly List<string> materialPaths = new List<string>();
        public readonly List<SourceMaterialInfo> materials = new List<SourceMaterialInfo>();
        public readonly List<string> notes = new List<string>();
    }

    /// <summary>
    /// Builds pastel materials that keep each part's colour source (material colour, base texture
    /// such as a palette atlas, vertex colours) and swaps them in slot by slot. Source assets are
    /// never modified: assets get a prefab variant, scene objects are changed with Undo.
    /// </summary>
    public static class PastelMaterialConverter
    {
        // glTFast (Shader Graph) first, then URP, HDRP and Built-in names.
        static readonly string[] TextureProperties = { "baseColorTexture", "_BaseMap", "_MainTex", "_BaseColorMap", "_ColorTexture" };
        static readonly string[] ColorProperties = { "baseColorFactor", "_BaseColor", "_Color", "_UnlitColor" };
        static readonly string[] CullProperties = { "_Cull", "_CullMode", "_BUILTIN_CullMode", "_CullModeForward" };

        public static Shader PastelShader => Shader.Find(PastelDemoBuilder.ShaderName);

        public static SourceMaterialInfo Inspect(Material m, bool meshHasVertexColors)
        {
            var info = new SourceMaterialInfo { source = m };
            if (m == null) return info;

            if (m.shader == PastelShader)
            {
                info.alreadyPastel = true;
                info.notes.Add("Đã dùng shader pastel, giữ nguyên.");
                return info;
            }

            string shaderName = m.shader != null ? m.shader.name : "";

            foreach (string p in TextureProperties)
            {
                if (!m.HasProperty(p) || m.GetTexture(p) == null) continue;
                info.textureProperty = p;
                info.texture = m.GetTexture(p);
                if (m.HasProperty(p + "_ST"))
                {
                    var st = m.GetVector(p + "_ST");
                    info.textureScale = new Vector2(st.x, st.y);
                    info.textureOffset = new Vector2(st.z, st.w);
                }
                else
                {
                    info.textureScale = m.GetTextureScale(p);
                    info.textureOffset = m.GetTextureOffset(p);
                }
                break;
            }

            foreach (string p in ColorProperties)
            {
                if (!m.HasProperty(p)) continue;
                info.color = m.GetColor(p);
                break;
            }

            bool standard = shaderName == "Standard" || shaderName == "Standard (Specular setup)";
            float mode = standard && m.HasProperty("_Mode") ? m.GetFloat("_Mode") : 0f;

            info.cutout = m.IsKeywordEnabled("_ALPHATEST_ON") ||
                          (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f) ||
                          (standard && Mathf.Approximately(mode, 1f));
            if (m.HasProperty("alphaCutoff")) info.cutoff = m.GetFloat("alphaCutoff");
            else if (m.HasProperty("_Cutoff")) info.cutoff = m.GetFloat("_Cutoff");

            info.wasTransparent = m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                                  (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) ||
                                  (standard && mode >= 2f) ||
                                  m.renderQueue >= (int)RenderQueue.Transparent;
            if (info.wasTransparent && !info.cutout)
            {
                info.cutout = true;
                info.cutoff = 0.5f;
                info.notes.Add("Material gốc trong suốt (blend): shader pastel chỉ hỗ trợ opaque/cutout nên chuyển thành cắt alpha ở ngưỡng 0.5.");
            }

            foreach (string p in CullProperties)
                if (m.HasProperty(p) && Mathf.Approximately(m.GetFloat(p), (float)CullMode.Off))
                    info.doubleSided = true;
            if (m.HasProperty("_DoubleSidedEnable") && m.GetFloat("_DoubleSidedEnable") > 0.5f)
                info.doubleSided = true;

            // glTF multiplies COLOR_0 into the base colour and stores it linear. Lit/Standard ignore
            // vertex colours, so they stay off for those sources to avoid stray colour data.
            bool gltf = shaderName.IndexOf("glTF", StringComparison.OrdinalIgnoreCase) >= 0;
            if (meshHasVertexColors && gltf)
            {
                info.vertexColors = true;
                info.vertexColorsAreLinear = true;
            }
            else if (meshHasVertexColors && (shaderName.Contains("Particles") || shaderName.Contains("Vertex")))
            {
                info.vertexColors = true;
            }

            if (m.IsKeywordEnabled("_EMISSION") || m.IsKeywordEnabled("_EMISSIVE"))
                info.notes.Add("Emission của material gốc không được chuyển (shader pastel là bề mặt lì).");
            if (info.texture == null && !info.vertexColors)
                info.notes.Add("Không có texture/vertex color: cả phần dùng material này một màu (màu của material gốc).");

            return info;
        }

        /// <summary>Creates (in memory) the pastel counterpart of one source material.</summary>
        public static Material CreatePastelMaterial(SourceMaterialInfo info, PastelConvertSettings s)
        {
            var m = new Material(PastelShader) { name = (info.source != null ? info.source.name.Trim() : "Material") + "_Pastel" };

            m.SetColor("_BaseColor", info.color);

            bool useMap = info.texture != null;
            m.SetFloat("_UseBaseMap", useMap ? 1 : 0);
            SetKeyword(m, "_USE_BASE_MAP", useMap);
            if (useMap)
            {
                m.SetTexture("_BaseMap", info.texture);
                m.SetTextureScale("_BaseMap", info.textureScale);
                m.SetTextureOffset("_BaseMap", info.textureOffset);
            }
            m.SetFloat("_TextureDetailBlur", s.textureDetailBlur);

            m.SetFloat("_UseVertexColor", info.vertexColors ? 1 : 0);
            SetKeyword(m, "_USE_VERTEX_COLOR", info.vertexColors);
            m.SetFloat("_VertexColorIsSRGB", info.vertexColorsAreLinear ? 0 : 1);

            m.SetFloat("_AlphaClip", info.cutout ? 1 : 0);
            SetKeyword(m, "_ALPHATEST_ON", info.cutout);
            m.SetFloat("_Cutoff", info.cutoff);
            m.renderQueue = info.cutout ? (int)RenderQueue.AlphaTest : -1;

            m.SetFloat("_Cull", info.doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            m.doubleSidedGI = info.doubleSided;

            ApplyPastelSettings(m, s);
            return m;
        }

        public static void ApplyPastelSettings(Material m, PastelConvertSettings s)
        {
            m.SetFloat("_Pastelize", s.pastelize ? 1 : 0);
            SetKeyword(m, "_PASTELIZE", s.pastelize);
            m.SetFloat("_PastelAmount", s.pastelAmount);
            m.SetFloat("_PastelLightMin", s.lightMin);
            m.SetFloat("_PastelLightMax", s.lightMax);
            m.SetFloat("_PastelSaturation", s.saturation);
            m.SetFloat("_PastelChromaMax", s.chromaMax);
            PastelDemoBuilder.SetNormalMode(m, s.flatNormals);
        }

        static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword);
            else m.DisableKeyword(keyword);
        }

        /// <summary>Lists every material slot under root together with what can be read from it.</summary>
        public static List<SourceMaterialInfo> Analyze(GameObject root, List<string> notes)
        {
            var infos = new List<SourceMaterialInfo>();
            var seen = new Dictionary<Material, SourceMaterialInfo>();
            var withColors = MaterialsOnMeshesWithColors(root);

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
                {
                    notes?.Add($"Bỏ qua {r.GetType().Name} tại '{r.name}' (chỉ xử lý MeshRenderer và SkinnedMeshRenderer).");
                    continue;
                }
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || seen.ContainsKey(m)) continue;
                    var info = Inspect(m, withColors.Contains(m));
                    seen.Add(m, info);
                    infos.Add(info);
                }
            }
            return infos;
        }

        static HashSet<Material> MaterialsOnMeshesWithColors(GameObject root)
        {
            var set = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = r is SkinnedMeshRenderer s ? s.sharedMesh :
                    r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null;
                if (mesh == null || !mesh.HasVertexAttribute(VertexAttribute.Color)) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null) set.Add(m);
            }
            return set;
        }

        /// <summary>
        /// Writes the pastel materials and either a prefab variant (source is a prefab/model asset)
        /// or, for a scene object, assigns them on the object itself with Undo.
        /// </summary>
        public static PastelConvertResult Convert(GameObject root, PastelConvertSettings settings)
        {
            if (PastelShader == null) throw new InvalidOperationException($"Không tìm thấy shader '{PastelDemoBuilder.ShaderName}'.");

            var result = new PastelConvertResult();
            var infos = Analyze(root, result.notes);
            result.materials.AddRange(infos);

            var created = new List<string>();
            var previewScene = default(UnityEngine.SceneManagement.Scene);
            GameObject instance = null;
            try
            {
                string baseFolder = EnsureFolder(settings.outputFolder);
                string folder = AssetDatabase.GenerateUniqueAssetPath($"{baseFolder}/{Sanitize(root.name)}_Pastel");
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
                created.Add(folder);

                var map = new Dictionary<Material, Material>();
                foreach (var info in infos)
                {
                    if (info.alreadyPastel) { map[info.source] = info.source; continue; }
                    var pastel = CreatePastelMaterial(info, settings);
                    string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(pastel.name)}.mat");
                    AssetDatabase.CreateAsset(pastel, path);
                    result.materialPaths.Add(path);
                    map[info.source] = pastel;
                }

                bool isAsset = EditorUtility.IsPersistent(root);
                if (isAsset)
                {
                    if (root.transform.parent != null || !PrefabUtility.IsPartOfPrefabAsset(root))
                        throw new InvalidOperationException("Hãy chọn gốc của prefab/model trong Project, hoặc một object trong scene.");
                    previewScene = EditorSceneManager.NewPreviewScene();
                    instance = (GameObject)PrefabUtility.InstantiatePrefab(root, previewScene);
                    Remap(instance, map, false);
                    string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(root.name)}_Pastel.prefab");
                    PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool ok);
                    if (!ok) throw new InvalidOperationException("Không lưu được prefab variant.");
                    result.prefabPath = prefabPath;
                }
                else
                {
                    Remap(root, map, true);
                    result.appliedToScene = true;
                }

                AssetDatabase.SaveAssets();
                return result;
            }
            catch
            {
                foreach (string path in created) AssetDatabase.DeleteAsset(path);
                throw;
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static void Remap(GameObject root, Dictionary<Material, Material> map, bool undo)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                var slots = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] != null && map.TryGetValue(slots[i], out var pastel) && pastel != slots[i])
                    {
                        slots[i] = pastel;
                        changed = true;
                    }
                }
                if (!changed) continue;
                if (undo) Undo.RecordObject(r, "Pastel materials");
                r.sharedMaterials = slots;
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
            return string.IsNullOrWhiteSpace(name) ? "Material" : name.Trim();
        }
    }
}
