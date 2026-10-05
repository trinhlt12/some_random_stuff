using System.Collections.Generic;
using UnityEngine;

namespace LowPolyTools
{
    public sealed class FlatNormalsReport
    {
        public int verticesBefore;
        public int verticesAfter;
        public int degenerateTriangles;
    }

    /// <summary>
    /// Gives every triangle its own face normal by splitting vertices where neighbouring faces
    /// disagree. Triangle count is unchanged; vertex count usually grows. All other channels
    /// (UVs, colours, submesh assignment) are copied from the original corner.
    /// </summary>
    public static class FlatNormals
    {
        // Normals are quantised only to decide whether two corners can share a vertex.
        const float NormalKeyScale = 1e4f;

        public static MeshBuffers Apply(MeshBuffers src, FlatNormalsReport report)
        {
            var dst = src.CreateEmptyLike(forceNormals: true);
            var lookup = new Dictionary<(int, int, int, int), int>();

            report.verticesBefore = src.VertexCount;

            foreach (var tris in src.submeshTriangles)
            {
                var outTris = new int[tris.Length];
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    Vector3 p0 = src.positions[tris[k]], p1 = src.positions[tris[k + 1]], p2 = src.positions[tris[k + 2]];
                    // Same winding convention as Mesh.RecalculateNormals.
                    Vector3 face = Vector3.Cross(p1 - p0, p2 - p0);
                    float len = face.magnitude;
                    bool degenerate = len <= 1e-12f;
                    if (degenerate) report.degenerateTriangles++;
                    else face /= len;

                    for (int c = 0; c < 3; c++)
                    {
                        int vi = tris[k + c];
                        // A degenerate face has no direction: keep the corner's own normal.
                        Vector3 n = degenerate
                            ? (src.normals != null ? src.normals[vi] : Vector3.up)
                            : face;

                        var key = (vi, Mathf.RoundToInt(n.x * NormalKeyScale),
                            Mathf.RoundToInt(n.y * NormalKeyScale), Mathf.RoundToInt(n.z * NormalKeyScale));
                        if (!lookup.TryGetValue(key, out int index))
                        {
                            index = dst.AppendVertex(src, vi);
                            dst.normals[index] = n;
                            if (dst.tangents != null)
                                dst.tangents[index] = Orthogonalize(src.tangents[vi], n);
                            lookup.Add(key, index);
                        }
                        outTris[k + c] = index;
                    }
                }
                dst.submeshTriangles.Add(outTris);
            }

            report.verticesAfter = dst.VertexCount;
            return dst;
        }

        /// <summary>Gram–Schmidt the old tangent against the new normal, keeping its handedness.</summary>
        static Vector4 Orthogonalize(Vector4 tangent, Vector3 normal)
        {
            var t = new Vector3(tangent.x, tangent.y, tangent.z);
            t -= normal * Vector3.Dot(normal, t);
            if (t.sqrMagnitude < 1e-12f)
            {
                // Old tangent was parallel to the new normal: pick any perpendicular direction.
                t = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.99f ? Vector3.up : Vector3.right);
            }
            t.Normalize();
            return new Vector4(t.x, t.y, t.z, tangent.w < 0f ? -1f : 1f);
        }
    }
}
