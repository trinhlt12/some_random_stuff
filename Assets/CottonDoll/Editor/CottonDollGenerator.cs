using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CottonDoll.Editor
{
    public enum DollMaterialSlot { Skin, Hair, Outfit, Face }

    /// <summary>One generated part: its mesh data (normalised units), material slot and vertex colour.</summary>
    public sealed class DollPart
    {
        public string name;
        public MeshData data;
        public DollMaterialSlot slot;
        public Color vertexColor = Color.white;
    }

    /// <summary>
    /// Builds the cotton doll parts from <see cref="CottonDollSettings"/>. Pure geometry: no scene objects,
    /// no assets. Same settings → same meshes. Only runs when asked (Editor tool or code), never per frame.
    /// </summary>
    public static class CottonDollGenerator
    {
        public const string MeshPrefix = "CottonDoll_";

        // Grid step and triangle budget per part (normalised units; the doll is 1 unit tall).
        struct PartSpec
        {
            public string name;
            public float cell;
            public int target;
            public PartSpec(string name, float cell, int target) { this.name = name; this.cell = cell; this.target = target; }
        }

        static readonly PartSpec BodySpec = new PartSpec("Body", 0.0065f, 3300);
        static readonly PartSpec HairSpec = new PartSpec("Hair", 0.005f, 1250);
        static readonly PartSpec ShirtSpec = new PartSpec("Shirt", 0.0058f, 700);
        static readonly PartSpec ShortsSpec = new PartSpec("Shorts", 0.0058f, 380);
        static readonly PartSpec ShoesSpec = new PartSpec("Shoes", 0.0035f, 260);

        public static List<DollPart> Generate(CottonDollSettings settings)
        {
            settings.Sanitize();
            var shape = new CottonDollShapes(settings);
            var parts = new List<DollPart>();
            Func<Vector3, float> body = p => shape.Body(p);

            if (settings.showBody)
                parts.Add(Part(BodySpec, p => shape.Body(p), DollMaterialSlot.Skin, settings.skinColor,
                    new Vector3(0f, -0.01f, -0.2f), new Vector3(shape.handC.x + shape.handR.x + 0.04f, 1.02f, 0.2f), null, 0f));

            if (settings.showHair)
            {
                var hr = shape.headR;
                float reach = shape.hairVol * 2f + 0.05f;
                parts.Add(Part(HairSpec, shape.Hair, DollMaterialSlot.Hair, settings.hairColor,
                    new Vector3(0f, shape.hairBottomY - 0.03f, shape.headC.z - hr.z - reach),
                    new Vector3(hr.x + reach, 1f + reach, shape.headC.z + hr.z + reach), p => shape.Head(p), 0.014f));
            }

            if (settings.showShirt)
                parts.Add(Part(ShirtSpec, shape.Shirt, DollMaterialSlot.Outfit, settings.shirtColor,
                    new Vector3(0f, shape.hemY - 0.05f, -0.17f), new Vector3(shape.sleeveEnd.x + 0.09f, shape.chinY + 0.02f, 0.17f),
                    body, CullDepth(shape)));

            if (settings.showShorts)
                parts.Add(Part(ShortsSpec, shape.Shorts, DollMaterialSlot.Outfit, settings.shortsColor,
                    new Vector3(0f, shape.shortsEnd.y - 0.06f, -0.16f), new Vector3(0.2f, shape.waistY + 0.03f, 0.16f),
                    body, CullDepth(shape)));

            if (settings.showShoes)
                parts.Add(Part(ShoesSpec, shape.Shoes, DollMaterialSlot.Outfit, settings.shoeColor,
                    new Vector3(0f, -0.01f, -0.08f), new Vector3(0.16f, 0.13f, 0.18f), shape.ShoeHiddenWall, 0f));   // drop the hidden inner wall of the ankle opening

            if (settings.faceFeatures && settings.showBody)
                parts.Add(new DollPart { name = "Face", data = shape.FaceMesh(), slot = DollMaterialSlot.Face, vertexColor = settings.faceColor });

            return parts;
        }

        // Only triangles clearly inside the body are dropped; cutting closer leaves ragged edges at hems.
        static float CullDepth(CottonDollShapes shape) => Mathf.Max(0.016f, shape.gap * 1.5f);

        static DollPart Part(PartSpec spec, Func<Vector3, float> field, DollMaterialSlot slot, Color color,
                             Vector3 min, Vector3 max, Func<Vector3, float> cull, float cullDepth)
        {
            var data = SymmetricMesher.Build(field, new SymmetricMesher.Settings
            {
                cellSize = spec.cell,
                boundsMin = min,
                boundsMax = max,
                targetTriangles = spec.target,
                cullField = cull,
                cullDepth = cullDepth,
            });
            return new DollPart { name = spec.name, data = data, slot = slot, vertexColor = color };
        }

        /// <summary>Writes a part into <paramref name="mesh"/> (reused if given), scaled to the doll height.</summary>
        public static Mesh ToMesh(DollPart part, float height, Mesh mesh = null)
        {
            if (mesh == null) mesh = new Mesh();
            mesh.Clear();
            mesh.name = MeshPrefix + part.name;
            var d = part.data;
            var verts = new Vector3[d.VertexCount];
            for (int i = 0; i < verts.Length; i++) verts[i] = d.positions[i] * height;
            mesh.indexFormat = verts.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(d.normals);
            if (part.slot == DollMaterialSlot.Outfit)
            {
                var colors = new Color[verts.Length];
                for (int i = 0; i < colors.Length; i++) colors[i] = part.vertexColor;
                mesh.SetColors(colors);
            }
            mesh.SetTriangles(d.triangles, 0, true);
            return mesh;
        }

        // ------------------------------------------------------------------------------------
        // Materials (up to 4, shared by every part that uses the slot)

        public const string ShaderName = "Pastel/Low Poly Pastel";
        public const string FallbackShader = "Universal Render Pipeline/Lit";

        /// <summary>Creates or updates the material of a slot (matte, smooth normals; Outfit uses vertex colours).</summary>
        public static Material SetupMaterial(Material m, DollMaterialSlot slot, CottonDollSettings s)
        {
            var shader = Shader.Find(ShaderName) ?? Shader.Find(FallbackShader);
            if (m == null) m = new Material(shader);
            else if (m.shader != shader) m.shader = shader;
            m.name = MeshPrefix + slot;

            Color baseColor = slot switch
            {
                DollMaterialSlot.Skin => s.skinColor,
                DollMaterialSlot.Hair => s.hairColor,
                DollMaterialSlot.Face => s.faceColor,
                _ => Color.white,   // outfit colours come from the vertex colours
            };
            m.SetColor("_BaseColor", baseColor);   // sRGB; Unity converts to linear in a linear project

            if (shader.name == ShaderName)
            {
                // Smooth (mesh) normals, never flat shading.
                m.SetFloat("_NormalMode", 0f);
                m.EnableKeyword("_NORMALMODE_MESH");
                m.DisableKeyword("_NORMALMODE_FLAT");
                bool vc = slot == DollMaterialSlot.Outfit;
                m.SetFloat("_UseVertexColor", vc ? 1f : 0f);
                m.SetFloat("_VertexColorIsSRGB", 1f);
                if (vc) m.EnableKeyword("_USE_VERTEX_COLOR"); else m.DisableKeyword("_USE_VERTEX_COLOR");
                m.SetFloat("_Pastelize", 0f);
                m.DisableKeyword("_PASTELIZE");
                // Softer, cotton-like light: a bit more wrap and gentle contrast.
                m.SetFloat("_Wrap", 0.55f);
                m.SetFloat("_LightInfluence", 0.8f);
            }
            else
            {
                m.SetFloat("_Smoothness", 0.12f);
                m.SetFloat("_Metallic", 0f);
            }
            return m;
        }

        public static int TriangleCount(IEnumerable<DollPart> parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.data.TriangleCount;
            return n;
        }

        public static int VertexCount(IEnumerable<DollPart> parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.data.VertexCount;
            return n;
        }
    }
}
