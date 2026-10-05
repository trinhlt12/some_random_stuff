using System;
using System.Collections.Generic;

namespace RockGen
{
    /// <summary>Minimal double-precision vector for the geometry steps.</summary>
    public struct D3
    {
        public double x, y, z;
        public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public static D3 operator +(D3 a, D3 b) => new D3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static D3 operator -(D3 a, D3 b) => new D3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static D3 operator *(D3 a, double s) => new D3(a.x * s, a.y * s, a.z * s);
        public static double Dot(D3 a, D3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static D3 Cross(D3 a, D3 b) => new D3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public double Length => Math.Sqrt(x * x + y * y + z * z);
        public D3 Normalized => this * (1.0 / Length);
    }

    /// <summary>
    /// Cuts a closed convex triangle mesh with a plane and caps the opening, so the result is again
    /// a closed convex mesh. Intersection vertices are shared between neighbouring triangles, which
    /// keeps the surface watertight. Winding: Cross(b - a, c - a) points outwards.
    /// </summary>
    public static class ConvexClip
    {
        /// <summary>
        /// Keeps the part where Dot(normal, p) &lt;= offset. Vertices within <paramref name="epsilon"/>
        /// of the plane are snapped onto it to avoid slivers. Returns false (mesh unchanged) when the
        /// plane misses the mesh or would remove all of it.
        /// </summary>
        public static bool Clip(List<D3> verts, List<int> tris, D3 normal, double offset, double epsilon)
        {
            normal = normal.Normalized;
            var side = new double[verts.Count];
            bool anyAbove = false, anyBelow = false;
            for (int i = 0; i < verts.Count; i++)
            {
                double s = D3.Dot(normal, verts[i]) - offset;
                if (Math.Abs(s) < epsilon) s = 0;
                side[i] = s;
                if (s > 0) anyAbove = true;
                if (s < 0) anyBelow = true;
            }
            if (!anyAbove || !anyBelow) return false;

            // Snap near-plane vertices exactly onto the plane (shared, so neighbours stay consistent).
            var work = new List<D3>(verts);
            for (int i = 0; i < work.Count; i++)
                if (side[i] == 0)
                    work[i] = work[i] - normal * (D3.Dot(normal, work[i]) - offset);

            var sideList = new List<double>(side);
            var edgeVertex = new Dictionary<long, int>();
            var result = new List<int>(tris.Count);
            var poly = new List<int>(4);

            for (int t = 0; t < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                // Faces lying in the plane are replaced by the cap.
                if (sideList[a] == 0 && sideList[b] == 0 && sideList[c] == 0) continue;

                poly.Clear();
                ClipEdge(a, b, work, sideList, edgeVertex, poly, normal, offset);
                ClipEdge(b, c, work, sideList, edgeVertex, poly, normal, offset);
                ClipEdge(c, a, work, sideList, edgeVertex, poly, normal, offset);
                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    result.Add(poly[0]);
                    result.Add(poly[k]);
                    result.Add(poly[k + 1]);
                }
            }

            // Cap: every kept vertex on the plane lies on the convex cross-section outline.
            var used = new HashSet<int>(result);
            var ring = new List<int>();
            foreach (int i in used)
                if (sideList[i] == 0) ring.Add(i);
            if (ring.Count < 3 || result.Count == 0) return false;

            var center = new D3();
            foreach (int i in ring) center = center + work[i];
            center = center * (1.0 / ring.Count);

            var u = D3.Cross(normal, Math.Abs(normal.y) < 0.9 ? new D3(0, 1, 0) : new D3(1, 0, 0)).Normalized;
            var v = D3.Cross(normal, u);
            ring.Sort((i, j) =>
            {
                var di = work[i] - center;
                var dj = work[j] - center;
                return Math.Atan2(D3.Dot(di, v), D3.Dot(di, u)).CompareTo(Math.Atan2(D3.Dot(dj, v), D3.Dot(dj, u)));
            });

            // Ring order is counter-clockwise around +normal (atan2 in the u,v basis). Ear clipping on
            // strict corners gives k - 2 triangles and never a zero-area one, even with collinear
            // outline points (those must stay to match the side triangles, or the mesh would crack).
            double areaEpsilon = epsilon * epsilon * 1e-3;
            var loop = new List<int>(ring);
            while (loop.Count > 3)
            {
                int ear = -1;
                for (int k = 0; k < loop.Count; k++)
                {
                    int p = loop[(k + loop.Count - 1) % loop.Count], q = loop[k], r = loop[(k + 1) % loop.Count];
                    if (D3.Dot(D3.Cross(work[q] - work[p], work[r] - work[p]), normal) > areaEpsilon) { ear = k; break; }
                }
                if (ear < 0) return false; // outline degenerated; leave the mesh unchanged
                AddCap(result, loop[(ear + loop.Count - 1) % loop.Count], loop[ear], loop[(ear + 1) % loop.Count]);
                loop.RemoveAt(ear);
            }
            if (D3.Dot(D3.Cross(work[loop[1]] - work[loop[0]], work[loop[2]] - work[loop[0]]), normal) <= areaEpsilon)
                return false;
            AddCap(result, loop[0], loop[1], loop[2]);

            verts.Clear();
            verts.AddRange(work);
            tris.Clear();
            tris.AddRange(result);
            Compact(verts, tris);
            return true;
        }

        // Cap triangles face +normal (outwards from the kept part): Cross(b - a, c - a) . normal > 0.
        static void AddCap(List<int> result, int a, int b, int c)
        {
            result.Add(a);
            result.Add(b);
            result.Add(c);
        }

        // Sutherland–Hodgman step for edge a->b: emits a if kept, and the crossing point if any.
        static void ClipEdge(int a, int b, List<D3> verts, List<double> side, Dictionary<long, int> edgeVertex,
            List<int> poly, D3 normal, double offset)
        {
            double sa = side[a], sb = side[b];
            if (sa <= 0) poly.Add(a);
            if ((sa < 0 && sb > 0) || (sa > 0 && sb < 0))
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!edgeVertex.TryGetValue(key, out int index))
                {
                    double t = sa / (sa - sb);
                    var p = verts[a] + (verts[b] - verts[a]) * t;
                    p = p - normal * (D3.Dot(normal, p) - offset); // exactly on the plane
                    index = verts.Count;
                    verts.Add(p);
                    side.Add(0);
                    edgeVertex.Add(key, index);
                }
                poly.Add(index);
            }
        }

        /// <summary>Drops unreferenced vertices and renumbers triangles (order preserved).</summary>
        public static void Compact(List<D3> verts, List<int> tris)
        {
            var map = new int[verts.Count];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var compact = new List<D3>();
            for (int k = 0; k < tris.Count; k++)
            {
                int i = tris[k];
                if (map[i] < 0)
                {
                    map[i] = compact.Count;
                    compact.Add(verts[i]);
                }
                tris[k] = map[i];
            }
            verts.Clear();
            verts.AddRange(compact);
        }
    }
}
