using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LowPolyTools
{
    /// <summary>
    /// Renders a set of meshes with a neutral grey material (and optional wireframe) from a shared
    /// orbit camera, so the original and the result can be compared under identical conditions.
    /// </summary>
    public sealed class LowPolyPreview : IDisposable
    {
        public struct Item
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
        }

        static readonly Color Background = new Color(0.32f, 0.33f, 0.35f);
        static readonly Color WireColor = new Color(0.08f, 0.08f, 0.1f);

        readonly PreviewRenderUtility utility = new PreviewRenderUtility();
        readonly Material neutral;
        readonly Material wire;
        readonly Dictionary<Mesh, Mesh> wireMeshes = new Dictionary<Mesh, Mesh>();

        public LowPolyPreview()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            neutral = new Material(lit) { hideFlags = HideFlags.HideAndDontSave };
            neutral.SetColor("_BaseColor", new Color(0.72f, 0.72f, 0.72f));
            neutral.SetColor("_Color", new Color(0.72f, 0.72f, 0.72f));
            neutral.SetFloat("_Smoothness", 0.15f);
            neutral.SetFloat("_Glossiness", 0.15f);
            neutral.SetFloat("_Metallic", 0f);

            var unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            wire = new Material(unlit) { hideFlags = HideFlags.HideAndDontSave };
            wire.SetColor("_BaseColor", WireColor);
            wire.SetColor("_Color", WireColor);

            utility.camera.fieldOfView = 30f;
            utility.camera.clearFlags = CameraClearFlags.SolidColor;
            utility.camera.backgroundColor = Background;
            utility.lights[0].intensity = 1.1f;
            utility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            utility.lights[1].intensity = 0.5f;
            utility.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
            utility.ambientColor = new Color(0.3f, 0.3f, 0.32f);
        }

        /// <summary>Draws into the current IMGUI rect.</summary>
        public void Draw(Rect rect, IList<Item> items, Bounds bounds, Vector2 orbit, bool wireframe)
        {
            if (Event.current.type != EventType.Repaint) return;
            utility.BeginPreview(rect, GUIStyle.none);
            RenderItems(items, bounds, orbit, wireframe);
            utility.EndAndDrawPreview(rect);
        }

        /// <summary>Renders to a texture outside of OnGUI (used by automated visual checks).</summary>
        public Texture2D RenderToTexture(int width, int height, IList<Item> items, Bounds bounds, Vector2 orbit, bool wireframe)
        {
            utility.BeginStaticPreview(new Rect(0, 0, width, height));
            RenderItems(items, bounds, orbit, wireframe);
            return utility.EndStaticPreview();
        }

        void RenderItems(IList<Item> items, Bounds bounds, Vector2 orbit, bool wireframe)
        {
            var cam = utility.camera;
            float radius = Mathf.Max(bounds.extents.magnitude, 1e-3f);
            float distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
            var rotation = Quaternion.Euler(orbit.y, orbit.x, 0f);
            cam.transform.rotation = rotation;
            cam.transform.position = bounds.center - rotation * Vector3.forward * distance;
            cam.nearClipPlane = Mathf.Max(distance - radius * 2f, distance * 0.01f);
            cam.farClipPlane = distance + radius * 2f;

            foreach (var item in items)
                for (int s = 0; s < item.mesh.subMeshCount; s++)
                    utility.DrawMesh(item.mesh, item.matrix, neutral, s);

            if (wireframe)
            {
                // Pull the lines slightly towards the camera so they win the depth test against
                // their own faces without showing hidden edges.
                var bias = Matrix4x4.Translate(-(rotation * Vector3.forward) * radius * 0.004f);
                foreach (var item in items)
                    utility.DrawMesh(WireMesh(item.mesh), bias * item.matrix, wire, 0);
            }

            utility.Render(true);
        }

        Mesh WireMesh(Mesh mesh)
        {
            if (wireMeshes.TryGetValue(mesh, out var cached) && cached != null) return cached;

            var vertices = new List<Vector3>();
            mesh.GetVertices(vertices);
            var seen = new HashSet<long>();
            var lines = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = tris[i + k], b = tris[i + (k + 1) % 3];
                        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                        if (seen.Add(key))
                        {
                            lines.Add(a);
                            lines.Add(b);
                        }
                    }
            }

            var wireMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            if (vertices.Count > 65535) wireMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            wireMesh.SetVertices(vertices);
            wireMesh.SetIndices(lines, MeshTopology.Lines, 0);
            wireMeshes[mesh] = wireMesh;
            return wireMesh;
        }

        public static List<Item> CollectItems(GameObject root, IList<LowPolyEntry> entries,
            IDictionary<Mesh, MeshConversionResult> replace, out Bounds bounds)
        {
            var items = new List<Item>();
            var toRoot = root.transform.worldToLocalMatrix;
            bool any = false;
            bounds = new Bounds();
            foreach (var entry in entries)
            {
                var mesh = entry.mesh;
                if (replace != null && replace.TryGetValue(mesh, out var r)) mesh = r.result;
                var matrix = toRoot * entry.filter.transform.localToWorldMatrix;
                items.Add(new Item { mesh = mesh, matrix = matrix });

                var b = TransformBounds(entry.mesh.bounds, matrix);
                if (!any) bounds = b;
                else bounds.Encapsulate(b);
                any = true;
            }
            return items;
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var result = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                result.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return result;
        }

        public void Dispose()
        {
            utility.Cleanup();
            Object.DestroyImmediate(neutral);
            Object.DestroyImmediate(wire);
            foreach (var m in wireMeshes.Values)
                if (m != null) Object.DestroyImmediate(m);
            wireMeshes.Clear();
        }
    }
}
