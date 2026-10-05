using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LowPolyTools
{
    /// <summary>
    /// Plain copy of every per-vertex channel the low-poly tools understand, plus the triangle
    /// list of each submesh. Channels that the source mesh does not have stay null.
    /// </summary>
    public sealed class MeshBuffers
    {
        public const int MaxUVChannels = 8;

        public List<Vector3> positions = new List<Vector3>();
        public List<Vector3> normals;
        public List<Vector4> tangents;
        public List<Color> colors;
        public bool colorsAreBytes;
        public readonly List<Vector4>[] uvs = new List<Vector4>[MaxUVChannels];
        public readonly int[] uvDimensions = new int[MaxUVChannels];
        public List<int[]> submeshTriangles = new List<int[]>();

        public int VertexCount => positions.Count;

        public int TriangleCount
        {
            get
            {
                int count = 0;
                foreach (var tris in submeshTriangles)
                    count += tris.Length / 3;
                return count;
            }
        }

        /// <summary>Reads a triangle mesh. Call <see cref="LowPolyConverter.GetMeshIssue"/> first.</summary>
        public static MeshBuffers FromMesh(Mesh mesh)
        {
            var b = new MeshBuffers();
            mesh.GetVertices(b.positions);

            if (mesh.HasVertexAttribute(VertexAttribute.Normal))
            {
                b.normals = new List<Vector3>();
                mesh.GetNormals(b.normals);
            }

            if (mesh.HasVertexAttribute(VertexAttribute.Tangent))
            {
                b.tangents = new List<Vector4>();
                mesh.GetTangents(b.tangents);
            }

            if (mesh.HasVertexAttribute(VertexAttribute.Color))
            {
                b.colors = new List<Color>();
                mesh.GetColors(b.colors);
                b.colorsAreBytes = mesh.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8;
            }

            for (int ch = 0; ch < MaxUVChannels; ch++)
            {
                var attribute = VertexAttribute.TexCoord0 + ch;
                if (!mesh.HasVertexAttribute(attribute))
                    continue;
                b.uvDimensions[ch] = mesh.GetVertexAttributeDimension(attribute);
                b.uvs[ch] = new List<Vector4>();
                mesh.GetUVs(ch, b.uvs[ch]);
            }

            for (int s = 0; s < mesh.subMeshCount; s++)
                b.submeshTriangles.Add(mesh.GetTriangles(s, true));

            return b;
        }

        /// <summary>Empty buffers with the same channel layout (optionally forcing a normal channel).</summary>
        public MeshBuffers CreateEmptyLike(bool forceNormals = false)
        {
            var b = new MeshBuffers();
            if (normals != null || forceNormals) b.normals = new List<Vector3>();
            if (tangents != null) b.tangents = new List<Vector4>();
            if (colors != null) b.colors = new List<Color>();
            b.colorsAreBytes = colorsAreBytes;
            for (int ch = 0; ch < MaxUVChannels; ch++)
            {
                if (uvs[ch] == null)
                    continue;
                b.uvs[ch] = new List<Vector4>();
                b.uvDimensions[ch] = uvDimensions[ch];
            }
            return b;
        }

        /// <summary>Appends vertex <paramref name="index"/> of <paramref name="source"/> with all channels.</summary>
        public int AppendVertex(MeshBuffers source, int index)
        {
            positions.Add(source.positions[index]);
            if (normals != null)
                normals.Add(source.normals != null ? source.normals[index] : Vector3.zero);
            if (tangents != null) tangents.Add(source.tangents[index]);
            if (colors != null) colors.Add(source.colors[index]);
            for (int ch = 0; ch < MaxUVChannels; ch++)
                if (uvs[ch] != null)
                    uvs[ch].Add(source.uvs[ch][index]);
            return positions.Count - 1;
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name };
            if (VertexCount > 65535)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(positions);
            if (normals != null) mesh.SetNormals(normals);
            if (tangents != null) mesh.SetTangents(tangents);

            if (colors != null)
            {
                if (colorsAreBytes)
                {
                    var bytes = new List<Color32>(colors.Count);
                    foreach (var c in colors) bytes.Add(c);
                    mesh.SetColors(bytes);
                }
                else
                {
                    mesh.SetColors(colors);
                }
            }

            for (int ch = 0; ch < MaxUVChannels; ch++)
            {
                var channel = uvs[ch];
                if (channel == null)
                    continue;
                switch (uvDimensions[ch])
                {
                    case 1:
                    case 2:
                        var uv2 = new List<Vector2>(channel.Count);
                        foreach (var v in channel) uv2.Add(v);
                        mesh.SetUVs(ch, uv2);
                        break;
                    case 3:
                        var uv3 = new List<Vector3>(channel.Count);
                        foreach (var v in channel) uv3.Add(v);
                        mesh.SetUVs(ch, uv3);
                        break;
                    default:
                        mesh.SetUVs(ch, channel);
                        break;
                }
            }

            mesh.subMeshCount = submeshTriangles.Count;
            for (int s = 0; s < submeshTriangles.Count; s++)
                mesh.SetTriangles(submeshTriangles[s], s, false);

            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>Exact (bitwise-value) comparison of vertices, optionally ignoring normals and tangents.</summary>
    sealed class VertexComparer : IEqualityComparer<int>
    {
        readonly MeshBuffers b;
        readonly bool ignoreShading;

        public VertexComparer(MeshBuffers buffers, bool ignoreNormalsAndTangents)
        {
            b = buffers;
            ignoreShading = ignoreNormalsAndTangents;
        }

        public bool Equals(int x, int y)
        {
            if (x == y) return true;
            if (!Same(b.positions[x], b.positions[y])) return false;
            if (!ignoreShading)
            {
                if (b.normals != null && !Same(b.normals[x], b.normals[y])) return false;
                if (b.tangents != null && !Same(b.tangents[x], b.tangents[y])) return false;
            }
            if (b.colors != null)
            {
                Color cx = b.colors[x], cy = b.colors[y];
                if (cx.r != cy.r || cx.g != cy.g || cx.b != cy.b || cx.a != cy.a) return false;
            }
            for (int ch = 0; ch < MeshBuffers.MaxUVChannels; ch++)
                if (b.uvs[ch] != null && !Same(b.uvs[ch][x], b.uvs[ch][y]))
                    return false;
            return true;
        }

        public int GetHashCode(int i)
        {
            var p = b.positions[i];
            int h = Hash(p.x);
            h = h * 31 + Hash(p.y);
            h = h * 31 + Hash(p.z);
            if (b.uvs[0] != null)
            {
                var uv = b.uvs[0][i];
                h = h * 31 + Hash(uv.x);
                h = h * 31 + Hash(uv.y);
            }
            return h;
        }

        internal static int Hash(float f) => f == 0f ? 0 : f.GetHashCode();
        internal static bool Same(Vector3 a, Vector3 c) => a.x == c.x && a.y == c.y && a.z == c.z;
        static bool Same(Vector4 a, Vector4 c) => a.x == c.x && a.y == c.y && a.z == c.z && a.w == c.w;
    }

    /// <summary>Exact position comparison that treats -0 and +0 as equal.</summary>
    sealed class ExactPositionComparer : IEqualityComparer<Vector3>
    {
        public static readonly ExactPositionComparer Instance = new ExactPositionComparer();

        public bool Equals(Vector3 a, Vector3 b) => VertexComparer.Same(a, b);

        public int GetHashCode(Vector3 p)
        {
            int h = VertexComparer.Hash(p.x);
            h = h * 31 + VertexComparer.Hash(p.y);
            return h * 31 + VertexComparer.Hash(p.z);
        }
    }
}
