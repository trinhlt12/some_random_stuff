using System.Collections.Generic;
using UnityEngine;

namespace LowPolyTools
{
    /// <summary>
    /// Recomputes smooth normals for a simplified mesh. Vertices that share a position and had the
    /// same normal before (i.e. were smooth-connected, possibly split only by a UV seam) are averaged
    /// together; vertices whose original normals differ stay apart, so authored hard edges survive.
    /// </summary>
    public static class SmoothNormals
    {
        public static void Recalculate(MeshBuffers b)
        {
            if (b.normals == null || b.VertexCount == 0) return;

            var groupOf = new int[b.VertexCount];
            var groups = new Dictionary<(Vector3, Vector3), int>(new GroupComparer());
            for (int i = 0; i < b.VertexCount; i++)
            {
                var key = (b.positions[i], b.normals[i]);
                if (!groups.TryGetValue(key, out int g))
                {
                    g = groups.Count;
                    groups.Add(key, g);
                }
                groupOf[i] = g;
            }

            var sums = new Vector3[groups.Count];
            foreach (var tris in b.submeshTriangles)
            {
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    Vector3 p0 = b.positions[tris[k]], p1 = b.positions[tris[k + 1]], p2 = b.positions[tris[k + 2]];
                    // Unnormalised cross product = area weighting, so thin slivers barely contribute.
                    var face = Vector3.Cross(p1 - p0, p2 - p0);
                    sums[groupOf[tris[k]]] += face;
                    sums[groupOf[tris[k + 1]]] += face;
                    sums[groupOf[tris[k + 2]]] += face;
                }
            }

            for (int i = 0; i < b.VertexCount; i++)
            {
                var n = sums[groupOf[i]];
                // Keep the original normal if no live face supports the group.
                if (n.sqrMagnitude > 1e-20f) b.normals[i] = n.normalized;
            }
        }

        sealed class GroupComparer : IEqualityComparer<(Vector3, Vector3)>
        {
            public bool Equals((Vector3, Vector3) a, (Vector3, Vector3) b) =>
                VertexComparer.Same(a.Item1, b.Item1) && VertexComparer.Same(a.Item2, b.Item2);

            public int GetHashCode((Vector3, Vector3) k) =>
                ExactPositionComparer.Instance.GetHashCode(k.Item1) * 31 + ExactPositionComparer.Instance.GetHashCode(k.Item2);
        }
    }
}
