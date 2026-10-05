using System.Collections.Generic;
using UnityEngine;

namespace LowPolyTools.Tests
{
    /// <summary>Procedural meshes used by the tests. Triangles are wound so that faces point outwards.</summary>
    static class TestMeshes
    {
        /// <summary>UV sphere with a duplicated UV seam column and per-segment pole vertices.</summary>
        public static Mesh UvSphere(int rings, int segments, float radius = 1f, Vector3 center = default)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int i = 0; i <= rings; i++)
            {
                float phi = Mathf.PI * i / rings;
                for (int j = 0; j <= segments; j++)
                {
                    float theta = 2f * Mathf.PI * j / segments;
                    // Snap the seam column and the poles to exactly the same positions.
                    if (j == segments) theta = 0f;
                    var n = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                    if (i == 0) n = Vector3.up;
                    if (i == rings) n = Vector3.down;
                    positions.Add(center + n * radius);
                    normals.Add(n);
                    uvs.Add(new Vector2(j / (float)segments, 1f - i / (float)rings));
                }
            }

            var tris = new List<int>();
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segments; j++)
                {
                    int a = i * (segments + 1) + j, b = a + 1, c = a + segments + 1, d = c + 1;
                    if (i != 0) AddOutward(tris, positions, center, a, b, c);
                    if (i != rings - 1) AddOutward(tris, positions, center, b, d, c);
                }

            var mesh = new Mesh { name = "TestSphere" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Open cylinder (no caps) along Y with a UV seam column.</summary>
        public static Mesh Cylinder(int segments, int rings, float radius, float height)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int i = 0; i <= rings; i++)
                for (int j = 0; j <= segments; j++)
                {
                    float theta = j == segments ? 0f : 2f * Mathf.PI * j / segments;
                    var n = new Vector3(Mathf.Cos(theta), 0f, Mathf.Sin(theta));
                    positions.Add(n * radius + Vector3.up * (height * i / rings - height * 0.5f));
                    normals.Add(n);
                    uvs.Add(new Vector2(j / (float)segments, i / (float)rings));
                }

            var tris = new List<int>();
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segments; j++)
                {
                    int a = i * (segments + 1) + j, b = a + 1, c = a + segments + 1, d = c + 1;
                    AddFacing(tris, positions, a, b, c, OutwardXZ(positions, a, b, c));
                    AddFacing(tris, positions, b, d, c, OutwardXZ(positions, b, d, c));
                }

            var mesh = new Mesh { name = "TestCylinder" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Flat XZ grid facing +Y. With twoSubmeshes the x&lt;0 half is submesh 0, the rest submesh 1.</summary>
        public static Mesh Grid(int cells, bool twoSubmeshes, bool withColors = false)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            for (int i = 0; i <= cells; i++)
                for (int j = 0; j <= cells; j++)
                {
                    float x = -1f + 2f * j / cells, z = -1f + 2f * i / cells;
                    positions.Add(new Vector3(x, 0f, z));
                    normals.Add(Vector3.up);
                    uvs.Add(new Vector2(j / (float)cells, i / (float)cells));
                    colors.Add(new Color(j / (float)cells, i / (float)cells, 0.5f, 1f));
                }

            var left = new List<int>();
            var right = new List<int>();
            for (int i = 0; i < cells; i++)
                for (int j = 0; j < cells; j++)
                {
                    int a = i * (cells + 1) + j, b = a + 1, c = a + cells + 1, d = c + 1;
                    var list = twoSubmeshes && (j + 0.5f) / cells >= 0.5f ? right : left;
                    AddFacing(list, positions, a, b, c, Vector3.up);
                    AddFacing(list, positions, b, d, c, Vector3.up);
                }

            var mesh = new Mesh { name = twoSubmeshes ? "TestGrid2Sub" : "TestGrid" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (withColors) mesh.SetColors(colors);
            mesh.subMeshCount = twoSubmeshes ? 2 : 1;
            mesh.SetTriangles(left, 0);
            if (twoSubmeshes) mesh.SetTriangles(right, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Axis-aligned box whose six faces are subdivided grids with their own (hard-edged) vertices.</summary>
        public static Mesh SubdividedBox(Vector3 size, int cells)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var half = size * 0.5f;
            Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var n in axes)
            {
                var u = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
                var v = Vector3.Cross(n, u);
                int start = positions.Count;
                for (int i = 0; i <= cells; i++)
                    for (int j = 0; j <= cells; j++)
                    {
                        float s = -1f + 2f * j / cells, t = -1f + 2f * i / cells;
                        var p = n + u * s + v * t;
                        positions.Add(Vector3.Scale(p, half));
                        normals.Add(n);
                        uvs.Add(new Vector2(j / (float)cells, i / (float)cells));
                    }
                for (int i = 0; i < cells; i++)
                    for (int j = 0; j < cells; j++)
                    {
                        int a = start + i * (cells + 1) + j, b = a + 1, c = a + cells + 1, d = c + 1;
                        AddFacing(tris, positions, a, b, c, n);
                        AddFacing(tris, positions, b, d, c, n);
                    }
            }

            var mesh = new Mesh { name = "TestBox" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Cube with only 8 shared vertices (no seams at all).</summary>
        public static Mesh WeldedCube()
        {
            var positions = new List<Vector3>();
            for (int i = 0; i < 8; i++)
                positions.Add(new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f));
            int[][] faces =
            {
                new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 },
                new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 },
            };
            var tris = new List<int>();
            foreach (var f in faces)
            {
                AddOutward(tris, positions, Vector3.zero, f[0], f[1], f[2]);
                AddOutward(tris, positions, Vector3.zero, f[0], f[2], f[3]);
            }
            var mesh = new Mesh { name = "WeldedCube" };
            mesh.SetVertices(positions);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Vector3 OutwardXZ(List<Vector3> p, int a, int b, int c)
        {
            var centroid = (p[a] + p[b] + p[c]) / 3f;
            return new Vector3(centroid.x, 0f, centroid.z);
        }

        static void AddOutward(List<int> tris, List<Vector3> p, Vector3 center, int a, int b, int c)
        {
            AddFacing(tris, p, a, b, c, (p[a] + p[b] + p[c]) / 3f - center);
        }

        static void AddFacing(List<int> tris, List<Vector3> p, int a, int b, int c, Vector3 desired)
        {
            var n = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            if (Vector3.Dot(n, desired) >= 0f) { tris.Add(a); tris.Add(b); tris.Add(c); }
            else { tris.Add(a); tris.Add(c); tris.Add(b); }
        }

        /// <summary>Distance from a point to the closest triangle of the mesh (brute force).</summary>
        public static float DistanceToMesh(Vector3 point, Mesh mesh)
        {
            var vertices = mesh.vertices;
            float best = float.MaxValue;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var t = mesh.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                    best = Mathf.Min(best, PointTriangleDistance(point, vertices[t[i]], vertices[t[i + 1]], vertices[t[i + 2]]));
            }
            return best;
        }

        static float PointTriangleDistance(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // Ericson, Real-Time Collision Detection, ClosestPtPointTriangle.
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return (p - a).magnitude;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return (p - b).magnitude;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return (p - (a + ab * (d1 / (d1 - d3)))).magnitude;
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return (p - c).magnitude;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return (p - (a + ac * (d2 / (d2 - d6)))).magnitude;
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
                return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).magnitude;
            float denom = 1f / (va + vb + vc);
            return (p - (a + ab * (vb * denom) + ac * (vc * denom))).magnitude;
        }
    }
}
