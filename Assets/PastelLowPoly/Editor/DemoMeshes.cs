using System.Collections.Generic;
using UnityEngine;

namespace PastelLowPoly.Editor
{
    /// <summary>Small procedural meshes for the demo scene (saved as new assets, never game meshes).</summary>
    static class DemoMeshes
    {
        /// <summary>Low-sided cylinder of radius 0.5 and height 1 with flat caps and hard edges.</summary>
        public static Mesh Cylinder(int sides)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            Vector3 Ring(int i, float y)
            {
                float a = 2f * Mathf.PI * i / sides;
                return new Vector3(Mathf.Cos(a) * 0.5f, y, Mathf.Sin(a) * 0.5f);
            }

            for (int i = 0; i < sides; i++)
            {
                Vector3 b0 = Ring(i, -0.5f), b1 = Ring(i + 1, -0.5f), t0 = Ring(i, 0.5f), t1 = Ring(i + 1, 0.5f);
                var n = ((b0 + b1) * 0.5f).normalized;
                n.y = 0;
                Quad(positions, normals, uvs, tris, b0, t0, t1, b1, n.normalized);
            }

            foreach (var y in new[] { 0.5f, -0.5f })
            {
                var n = new Vector3(0, Mathf.Sign(y), 0);
                int center = positions.Count;
                positions.Add(new Vector3(0, y, 0));
                normals.Add(n);
                uvs.Add(new Vector2(0.5f, 0.5f));
                for (int i = 0; i < sides; i++)
                {
                    var p = Ring(i, y);
                    positions.Add(p);
                    normals.Add(n);
                    uvs.Add(new Vector2(p.x + 0.5f, p.z + 0.5f));
                }
                for (int i = 0; i < sides; i++)
                    Facing(tris, positions, center, center + 1 + i, center + 1 + (i + 1) % sides, n);
            }

            return Build("Cylinder", positions, normals, uvs, null, tris);
        }

        /// <summary>Subdivided icosahedron, radius 0.5, one vertex per face corner (hard edges).</summary>
        public static Mesh Icosphere(int subdivisions, bool colored)
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var v = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized * 0.5f;
            var faces = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };

            for (int s = 0; s < subdivisions; s++)
            {
                var next = new List<int>();
                var cache = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (cache.TryGetValue(key, out int m)) return m;
                    v.Add(((v[a] + v[b]) * 0.5f).normalized * 0.5f);
                    cache[key] = v.Count - 1;
                    return v.Count - 1;
                }
                for (int i = 0; i < faces.Count; i += 3)
                {
                    int a = faces[i], b = faces[i + 1], c = faces[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                faces = next;
            }

            Color[] palette =
            {
                new Color(0.98f, 0.74f, 0.67f), new Color(0.98f, 0.89f, 0.63f), new Color(0.66f, 0.89f, 0.76f),
                new Color(0.53f, 0.80f, 0.80f), new Color(0.76f, 0.70f, 0.92f),
            };

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = colored ? new List<Color>() : null;
            var tris = new List<int>();
            for (int i = 0; i < faces.Count; i += 3)
            {
                Vector3 a = v[faces[i]], b = v[faces[i + 1]], c = v[faces[i + 2]];
                var centroid = (a + b + c) / 3f;
                var n = centroid.normalized;
                int start = positions.Count;
                positions.AddRange(new[] { a, b, c });
                normals.AddRange(new[] { n, n, n });
                uvs.AddRange(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) });
                if (colored)
                {
                    // Colour bands by height so the result is easy to read.
                    var col = palette[Mathf.Clamp(Mathf.FloorToInt((centroid.y + 0.5f) * palette.Length), 0, palette.Length - 1)];
                    colors.AddRange(new[] { col, col, col });
                }
                Facing(tris, positions, start, start + 1, start + 2, n);
            }

            return Build(colored ? "IcosphereColored" : "Icosphere", positions, normals, uvs, colors, tris);
        }

        static void Quad(List<Vector3> p, List<Vector3> n, List<Vector2> uv, List<int> tris,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int s = p.Count;
            p.AddRange(new[] { a, b, c, d });
            n.AddRange(new[] { normal, normal, normal, normal });
            uv.AddRange(new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            Facing(tris, p, s, s + 1, s + 2, normal);
            Facing(tris, p, s, s + 2, s + 3, normal);
        }

        /// <summary>Adds the triangle with Unity's winding so that it faces <paramref name="desired"/>.</summary>
        static void Facing(List<int> tris, List<Vector3> p, int a, int b, int c, Vector3 desired)
        {
            var n = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            if (Vector3.Dot(n, desired) >= 0f) tris.AddRange(new[] { a, b, c });
            else tris.AddRange(new[] { a, c, b });
        }

        static Mesh Build(string name, List<Vector3> p, List<Vector3> n, List<Vector2> uv, List<Color> colors, List<int> tris)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(p);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            if (colors != null) mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
