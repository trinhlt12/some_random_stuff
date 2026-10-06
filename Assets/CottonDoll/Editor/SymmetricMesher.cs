using System;
using System.Collections.Generic;
using LowPolyTools;
using UnityEngine;

namespace CottonDoll.Editor
{
    /// <summary>Triangle mesh data before it becomes a Unity Mesh.</summary>
    public sealed class MeshData
    {
        public Vector3[] positions;
        public Vector3[] normals;
        public int[] triangles;
        public int TriangleCount => triangles.Length / 3;
        public int VertexCount => positions.Length;
    }

    /// <summary>
    /// Turns a signed distance field that is mirror-symmetric in X into a smooth, exactly symmetric
    /// triangle mesh:
    ///   1. surface nets on the right half (x >= 0) of a fine grid;
    ///   2. the open border left at x = 0 is snapped onto the mirror plane;
    ///   3. quadric decimation (LowPolyTools) down to a triangle budget, with that border locked;
    ///   4. optional removal of triangles buried deep inside another shape (hidden cut caps);
    ///   5. mirror to the left half, welding the vertices on the plane;
    ///   6. vertices are pulled back onto the surface and normals come from the field gradient.
    /// Deterministic: the same field and settings always give the same mesh.
    /// </summary>
    public static class SymmetricMesher
    {
        public struct Settings
        {
            public float cellSize;           // grid step (normalised units)
            public Vector3 boundsMin;        // half-domain; boundsMin.x is forced to 0
            public Vector3 boundsMax;
            public int targetTriangles;      // for the whole (mirrored) mesh
            public Func<Vector3, float> cullField;   // optional: drop triangles with cullField(centroid) < -cullDepth
            public float cullDepth;
        }

        /// <summary>Timings of the last Build call (diagnostics).</summary>
        public static double LastSampleMs, LastDecimateMs, LastFinishMs;
        /// <summary>Raw triangles whose winding had to be corrected in the last Build call (diagnostics).</summary>
        public static int LastReoriented;

        public static MeshData Build(Func<Vector3, float> field, Settings s)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            float h = Mathf.Max(s.cellSize, 1e-4f);
            var min = new Vector3(0f, s.boundsMin.y, s.boundsMin.z);
            int nx = Mathf.CeilToInt((s.boundsMax.x - min.x) / h) + 1;
            int ny = Mathf.CeilToInt((s.boundsMax.y - min.y) / h) + 1;
            int nz = Mathf.CeilToInt((s.boundsMax.z - min.z) / h) + 1;

            // 1. Sample. A coarse pass first (every 4th sample): fine samples are evaluated exactly only
            // where a coarse cell is near the surface; elsewhere the coarse value is interpolated (only
            // its sign matters there). Same result, several times fewer field evaluations.
            const int F = 3;
            int mx = (nx - 1) / F + 2, my = (ny - 1) / F + 2, mz = (nz - 1) / F + 2;
            var coarse = new float[mx * my * mz];
            for (int k = 0; k < mz; k++)
                for (int j = 0; j < my; j++)
                    for (int i = 0; i < mx; i++)
                        coarse[(k * my + j) * mx + i] = field(new Vector3(min.x + i * F * h, min.y + j * F * h, min.z + k * F * h));
            float band = 2.1f * F * h;
            var values = new float[nx * ny * nz];
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int ci = i / F, cj = j / F, ck = k / F;
                        float c000 = coarse[(ck * my + cj) * mx + ci], c100 = coarse[(ck * my + cj) * mx + ci + 1];
                        float c010 = coarse[(ck * my + cj + 1) * mx + ci], c110 = coarse[(ck * my + cj + 1) * mx + ci + 1];
                        float c001 = coarse[((ck + 1) * my + cj) * mx + ci], c101 = coarse[((ck + 1) * my + cj) * mx + ci + 1];
                        float c011 = coarse[((ck + 1) * my + cj + 1) * mx + ci], c111 = coarse[((ck + 1) * my + cj + 1) * mx + ci + 1];
                        float lo = Mathf.Min(Mathf.Min(Mathf.Min(c000, c100), Mathf.Min(c010, c110)), Mathf.Min(Mathf.Min(c001, c101), Mathf.Min(c011, c111)));
                        float hi = Mathf.Max(Mathf.Max(Mathf.Max(c000, c100), Mathf.Max(c010, c110)), Mathf.Max(Mathf.Max(c001, c101), Mathf.Max(c011, c111)));
                        if (lo > band || hi < -band)
                        {
                            float fx = (i - ci * F) / (float)F, fy = (j - cj * F) / (float)F, fz = (k - ck * F) / (float)F;
                            float x00 = Mathf.Lerp(c000, c100, fx), x10 = Mathf.Lerp(c010, c110, fx);
                            float x01 = Mathf.Lerp(c001, c101, fx), x11 = Mathf.Lerp(c011, c111, fx);
                            values[(k * ny + j) * nx + i] = Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
                        }
                        else values[(k * ny + j) * nx + i] = field(new Vector3(min.x + i * h, min.y + j * h, min.z + k * h));
                    }

            // Surface nets: one vertex per cell crossed by the surface (average of edge crossings).
            int cx = nx - 1, cy = ny - 1, cz = nz - 1;
            var cellVertex = new int[cx * cy * cz];
            var positions = new List<Vector3>();
            for (int k = 0; k < cz; k++)
                for (int j = 0; j < cy; j++)
                    for (int i = 0; i < cx; i++)
                    {
                        int ci = (k * cy + j) * cx + i;
                        cellVertex[ci] = -1;
                        Vector3 sum = Vector3.zero;
                        int count = 0;
                        for (int e = 0; e < 12; e++)
                        {
                            var (a, b) = CubeEdges[e];
                            float va = values[Index(i + a.x, j + a.y, k + a.z, nx, ny)];
                            float vb = values[Index(i + b.x, j + b.y, k + b.z, nx, ny)];
                            if ((va < 0f) == (vb < 0f)) continue;
                            float t = va / (va - vb);
                            var pa = new Vector3(i + a.x, j + a.y, k + a.z);
                            var pb = new Vector3(i + b.x, j + b.y, k + b.z);
                            sum += Vector3.Lerp(pa, pb, t);
                            count++;
                        }
                        if (count == 0) continue;
                        cellVertex[ci] = positions.Count;
                        positions.Add(min + sum / count * h);
                    }

            var triangles = new List<int>();
            // Quads around every grid edge with a sign change whose four cells exist.
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        float v0 = values[Index(i, j, k, nx, ny)];
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int i1 = i + (axis == 0 ? 1 : 0), j1 = j + (axis == 1 ? 1 : 0), k1 = k + (axis == 2 ? 1 : 0);
                            if (i1 >= nx || j1 >= ny || k1 >= nz) continue;
                            float v1 = values[Index(i1, j1, k1, nx, ny)];
                            if ((v0 < 0f) == (v1 < 0f)) continue;
                            EmitQuad(i, j, k, axis, v0 < 0f, cellVertex, cx, cy, cz, positions, triangles);
                        }
                    }

            // Orientation check against the field itself: a strongly bent cell can fool the per-quad
            // guess, and the decimator keeps whatever orientation it is given.
            LastReoriented = 0;
            for (int t = 0; t < triangles.Count; t += 3)
            {
                Vector3 a = positions[triangles[t]], b = positions[triangles[t + 1]], c = positions[triangles[t + 2]];
                Vector3 g = Gradient(field, (a + b + c) / 3f, h * 0.5f);
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), g) >= 0f) continue;
                int tmp = triangles[t + 1]; triangles[t + 1] = triangles[t + 2]; triangles[t + 2] = tmp;
                LastReoriented++;
            }

            // 2. Snap the open border (the cut at x = 0) onto the mirror plane. The surface meets the
            // plane at a right angle (symmetric field), so this slides vertices along the surface.
            var border = BorderVertices(triangles, positions.Count);
            for (int v = 0; v < positions.Count; v++)
                if (border[v]) { var p = positions[v]; p.x = 0f; positions[v] = p; }

            LastSampleMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            // 3. Decimate the half, keeping the mirror border fixed.
            int halfTarget = Mathf.Max(8, s.targetTriangles / 2);
            int[] tris = triangles.ToArray();
            Vector3[] pos = positions.ToArray();
            if (tris.Length / 3 > halfTarget)
            {
                var buffers = new MeshBuffers { positions = new List<Vector3>(pos) };
                buffers.submeshTriangles.Add(tris);
                var options = new SimplifyOptions
                {
                    targetRatio = halfTarget / (float)(tris.Length / 3),
                    maxError = 0.05f,
                    preserveBorders = true,
                    protectSeams = false,
                };
                var reduced = QemSimplifier.Simplify(buffers, options, new SimplifyReport());
                pos = reduced.positions.ToArray();
                tris = reduced.submeshTriangles[0];
            }

            LastDecimateMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            // 4. Drop triangles buried inside another shape, and flat triangles lying in the mirror plane.
            var kept = new List<int>(tris.Length);
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 a = pos[tris[t]], b = pos[tris[t + 1]], c = pos[tris[t + 2]];
                if (a.x == 0f && b.x == 0f && c.x == 0f) continue;
                if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-16f) continue;
                if (s.cullField != null && s.cullField((a + b + c) / 3f) < -s.cullDepth) continue;
                kept.Add(tris[t]); kept.Add(tris[t + 1]); kept.Add(tris[t + 2]);
            }

            // 5. Mirror, welding vertices that lie on the plane.
            var remap = new int[pos.Length];
            var mirror = new int[pos.Length];
            for (int v = 0; v < pos.Length; v++) remap[v] = mirror[v] = -1;
            var outPos = new List<Vector3>();
            foreach (int v in kept)
            {
                if (remap[v] >= 0) continue;
                remap[v] = outPos.Count;
                outPos.Add(pos[v]);
            }
            int rightCount = outPos.Count;
            for (int v = 0; v < pos.Length; v++)
            {
                if (remap[v] < 0) continue;
                if (pos[v].x == 0f) { mirror[v] = remap[v]; continue; }
                mirror[v] = outPos.Count;
                outPos.Add(new Vector3(-pos[v].x, pos[v].y, pos[v].z));
            }
            var outTris = new int[kept.Count * 2];
            for (int t = 0; t < kept.Count; t += 3)
            {
                outTris[t] = remap[kept[t]];
                outTris[t + 1] = remap[kept[t + 1]];
                outTris[t + 2] = remap[kept[t + 2]];
                // Mirrored copy with reversed winding keeps the faces pointing outwards.
                outTris[kept.Count + t] = mirror[kept[t]];
                outTris[kept.Count + t + 1] = mirror[kept[t + 2]];
                outTris[kept.Count + t + 2] = mirror[kept[t + 1]];
            }

            // 6. Pull vertices onto the surface (removes the small grid bias) and take smooth normals.
            var finalPos = outPos.ToArray();
            var before = outPos.ToArray();
            var normals = new Vector3[finalPos.Length];
            float eps = h * 0.35f;
            for (int v = 0; v < finalPos.Length; v++)
            {
                Vector3 p = finalPos[v];
                bool onPlane = p.x == 0f;
                for (int it = 0; it < 2; it++)
                {
                    Vector3 g = Gradient(field, p, eps) / (2f * eps);   // central difference -> true gradient
                    float gl2 = g.sqrMagnitude;
                    if (gl2 < 1e-12f) break;
                    float d = field(p);
                    Vector3 step = g * (d / gl2);
                    if (step.magnitude > h) break;   // far from a clean surface point: keep it
                    p -= step;
                }
                if (onPlane) p.x = 0f;
                finalPos[v] = p;
            }

            // Never let the projection fold a triangle: undo it for the corners of any triangle it flipped.
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                for (int t = 0; t < outTris.Length; t += 3)
                {
                    int i0 = outTris[t], i1 = outTris[t + 1], i2 = outTris[t + 2];
                    Vector3 n0 = Vector3.Cross(before[i1] - before[i0], before[i2] - before[i0]);
                    Vector3 n1 = Vector3.Cross(finalPos[i1] - finalPos[i0], finalPos[i2] - finalPos[i0]);
                    if (Vector3.Dot(n0, n1) > 0f) continue;
                    finalPos[i0] = before[i0]; finalPos[i1] = before[i1]; finalPos[i2] = before[i2];
                    changed = true;
                }
                if (!changed) break;
            }

            // Smooth normals from the field gradient; where a thin feature makes the sampled gradient
            // disagree with the surface itself, the area-weighted face normal is used instead.
            var faceNormals = new Vector3[finalPos.Length];
            for (int t = 0; t < outTris.Length; t += 3)
            {
                Vector3 a = finalPos[outTris[t]], b = finalPos[outTris[t + 1]], c = finalPos[outTris[t + 2]];
                Vector3 fn = Vector3.Cross(b - a, c - a);
                faceNormals[outTris[t]] += fn; faceNormals[outTris[t + 1]] += fn; faceNormals[outTris[t + 2]] += fn;
            }
            for (int v = 0; v < finalPos.Length; v++)
            {
                bool onPlane = finalPos[v].x == 0f;
                Vector3 g = Unit(faceNormals[v], Vector3.up);
                if (onPlane) g = Unit(new Vector3(0f, g.y, g.z), Vector3.up);
                Vector3 n = Gradient(field, finalPos[v], eps);
                if (onPlane) n.x = 0f;
                n = Unit(n, g);
                normals[v] = Vector3.Dot(n, g) > 0.3f ? n : g;
            }

            LastFinishMs = watch.Elapsed.TotalMilliseconds;
            return new MeshData { positions = finalPos, normals = normals, triangles = outTris };
        }

        // Vector3.normalized returns zero below 1e-5; tiny but valid vectors still have a direction.
        static Vector3 Unit(Vector3 v, Vector3 fallback)
        {
            float m = v.magnitude;
            return m > 1e-12f ? v / m : fallback;
        }

        /// <summary>Central difference f(p+e) - f(p-e) per axis (2e × the gradient); normalise or divide by 2e before use.</summary>
        public static Vector3 Gradient(Func<Vector3, float> f, Vector3 p, float e)
        {
            return new Vector3(
                f(new Vector3(p.x + e, p.y, p.z)) - f(new Vector3(p.x - e, p.y, p.z)),
                f(new Vector3(p.x, p.y + e, p.z)) - f(new Vector3(p.x, p.y - e, p.z)),
                f(new Vector3(p.x, p.y, p.z + e)) - f(new Vector3(p.x, p.y, p.z - e)));
        }

        static int Index(int i, int j, int k, int nx, int ny) => (k * ny + j) * nx + i;

        static readonly (Vector3Int, Vector3Int)[] CubeEdges =
        {
            (new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0)), (new Vector3Int(0, 1, 0), new Vector3Int(1, 1, 0)),
            (new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 1)), (new Vector3Int(0, 1, 1), new Vector3Int(1, 1, 1)),
            (new Vector3Int(0, 0, 0), new Vector3Int(0, 1, 0)), (new Vector3Int(1, 0, 0), new Vector3Int(1, 1, 0)),
            (new Vector3Int(0, 0, 1), new Vector3Int(0, 1, 1)), (new Vector3Int(1, 0, 1), new Vector3Int(1, 1, 1)),
            (new Vector3Int(0, 0, 0), new Vector3Int(0, 0, 1)), (new Vector3Int(1, 0, 0), new Vector3Int(1, 0, 1)),
            (new Vector3Int(0, 1, 0), new Vector3Int(0, 1, 1)), (new Vector3Int(1, 1, 0), new Vector3Int(1, 1, 1)),
        };

        static void EmitQuad(int i, int j, int k, int axis, bool insideFirst, int[] cellVertex, int cx, int cy, int cz,
                             List<Vector3> positions, List<int> triangles)
        {
            // The four cells around the edge differ by -1/0 in the two other axes (u, v).
            int u = (axis + 1) % 3, w = (axis + 2) % 3;
            Span<int> q = stackalloc int[4];
            for (int c = 0; c < 4; c++)
            {
                int du = (c == 1 || c == 2) ? 0 : -1;
                int dv = (c >= 2) ? 0 : -1;
                var cell = new Vector3Int(i, j, k);
                cell[u] += du;
                cell[w] += dv;
                if (cell.x < 0 || cell.y < 0 || cell.z < 0 || cell.x >= cx || cell.y >= cy || cell.z >= cz) return;
                int vi = cellVertex[(cell.z * cy + cell.y) * cx + cell.x];
                if (vi < 0) return;
                q[c] = vi;
            }
            // Order 0:(-,-) 1:(0,-) 2:(0,0) 3:(-,0) runs around the edge; orient it outwards
            // (towards the positive side of the field).
            Vector3 p0 = positions[q[0]], p1 = positions[q[1]], p2 = positions[q[2]], p3 = positions[q[3]];
            Vector3 axisDir = Vector3.zero;
            axisDir[axis] = insideFirst ? 1f : -1f;
            bool flip = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0) + Vector3.Cross(p2 - p0, p3 - p0), axisDir) < 0f;
            int a = q[0], b = flip ? q[3] : q[1], c2 = q[2], d = flip ? q[1] : q[3];
            // Split along the shorter diagonal.
            if ((positions[a] - positions[c2]).sqrMagnitude <= (positions[b] - positions[d]).sqrMagnitude)
            {
                triangles.Add(a); triangles.Add(b); triangles.Add(c2);
                triangles.Add(a); triangles.Add(c2); triangles.Add(d);
            }
            else
            {
                triangles.Add(a); triangles.Add(b); triangles.Add(d);
                triangles.Add(b); triangles.Add(c2); triangles.Add(d);
            }
        }

        static bool[] BorderVertices(List<int> triangles, int vertexCount)
        {
            var edgeUse = new Dictionary<long, int>();
            for (int t = 0; t < triangles.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[t + e], b = triangles[t + (e + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edgeUse.TryGetValue(key, out int n);
                    edgeUse[key] = n + 1;
                }
            var border = new bool[vertexCount];
            foreach (var kv in edgeUse)
                if (kv.Value == 1)
                {
                    border[(int)(kv.Key >> 32)] = true;
                    border[(int)(kv.Key & 0xffffffff)] = true;
                }
            return border;
        }
    }
}
