using System.Collections.Generic;
using UnityEngine;

namespace RockGen
{
    /// <summary>Checks the guarantees the generator promises for every rock.</summary>
    public static class RockValidation
    {
        public static bool Validate(RockMeshData data, out string error)
        {
            error = null;
            var v = data.vertices;
            var n = data.normals;
            var t = data.triangles;

            if (v == null || n == null || t == null || t.Length == 0 || t.Length % 3 != 0)
                return Fail("Empty mesh or triangle list not a multiple of 3", out error);
            if (n.Length != v.Length)
                return Fail("Normal count differs from vertex count", out error);
            if (t.Length / 3 > data.settings.triangleBudget)
                return Fail($"{t.Length / 3} triangles exceed the budget {data.settings.triangleBudget}", out error);

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < v.Length; i++)
            {
                if (!Finite(v[i]) || !Finite(n[i])) return Fail($"Non-finite value at vertex {i}", out error);
                min = Vector3.Min(min, v[i]);
                max = Vector3.Max(max, v[i]);
            }
            foreach (int i in t)
                if (i < 0 || i >= v.Length) return Fail($"Triangle index {i} out of range", out error);

            // Degeneracy is judged in the unit box (each axis divided by its size), so very long or
            // very thin rocks are not flagged just for their proportions.
            var size = max - min;
            var inv = new Vector3(1f / Mathf.Max(size.x, 1e-12f), 1f / Mathf.Max(size.y, 1e-12f), 1f / Mathf.Max(size.z, 1e-12f));
            const float minUnitArea = 1e-6f;
            // The vertex average is a convex combination, so it lies inside a convex mesh.
            var center = Vector3.zero;
            foreach (var p in v) center += p;
            center /= v.Length;
            double volume = 0;
            var edges = new Dictionary<(Vector3, Vector3), int>();

            for (int k = 0; k < t.Length; k += 3)
            {
                Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                var unitCross = Vector3.Cross(Vector3.Scale(b - a, inv), Vector3.Scale(c - a, inv));
                if (unitCross.magnitude * 0.5f <= minUnitArea || cross.sqrMagnitude <= 0f)
                    return Fail($"Degenerate triangle {k / 3}", out error);
                var face = cross / cross.magnitude;

                for (int j = 0; j < 3; j++)
                    if (Vector3.Dot(n[t[k + j]], face) < 0.9999f)
                        return Fail($"Normal of triangle {k / 3} is not its face normal", out error);

                // Convex mesh: every face must point away from the centre.
                if (Vector3.Dot(face, (a + b + c) / 3f - center) <= 0f)
                    return Fail($"Triangle {k / 3} faces inwards", out error);

                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;

                Count(edges, a, b);
                Count(edges, b, c);
                Count(edges, c, a);
            }

            // Closed and consistently wound: each directed edge exactly once, with its reverse.
            foreach (var pair in edges)
            {
                if (pair.Value != 1) return Fail("A directed edge is used twice (non-manifold or flipped face)", out error);
                if (!edges.ContainsKey((pair.Key.Item2, pair.Key.Item1))) return Fail("Open edge (hole)", out error);
            }

            // Outward normals Cross(b - a, c - a) give a positive signed volume.
            if (volume <= 0) return Fail("Mesh is inside out", out error);
            return true;
        }

        static void Count(Dictionary<(Vector3, Vector3), int> edges, Vector3 a, Vector3 b)
        {
            edges.TryGetValue((a, b), out int c);
            edges[(a, b)] = c + 1;
        }

        static bool Finite(Vector3 p) =>
            !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
              float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
