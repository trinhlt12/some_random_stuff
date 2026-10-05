using System;
using System.Collections.Generic;
using UnityEngine;

namespace LowPolyTools
{
    public sealed class SimplifyOptions
    {
        /// <summary>Fraction of triangles to keep. A target, not a guarantee.</summary>
        public float targetRatio = 0.5f;

        /// <summary>Largest allowed geometric error, as a fraction of the mesh bounds diagonal.</summary>
        public float maxError = 0.01f;

        /// <summary>Lock every vertex on an open border so holes and plane edges keep their outline.</summary>
        public bool preserveBorders = true;

        /// <summary>
        /// Lock vertices on UV/vertex-colour seams and on material (submesh) boundaries.
        /// When off, those vertices may still slide along their seam line, but never across it.
        /// </summary>
        public bool protectSeams = true;
    }

    public sealed class SimplifyReport
    {
        public int trianglesBefore;
        public int trianglesAfter;
        public int verticesBefore;
        public int verticesAfter;
        public int targetTriangles;
        public int mergedDuplicateVertices;
        public int lockedVertices;
        public bool reachedTarget;
        public bool hitErrorLimit;

        public string StopReason
        {
            get
            {
                if (reachedTarget) return "Đạt mục tiêu";
                if (hitErrorLimit) return "Dừng vì giới hạn sai số (giữ hình dạng)";
                return "Không còn cạnh nào gộp được mà vẫn giữ ràng buộc";
            }
        }
    }

    /// <summary>
    /// Garland–Heckbert quadric error metric simplification using half-edge collapses.
    /// A collapse moves vertex u onto an existing neighbour v, so every surviving vertex keeps its
    /// original attributes (UVs, colours, normals) exactly. Attribute seams are handled per wedge:
    /// each render vertex at u must map onto exactly one render vertex at v on the same side of the
    /// seam, otherwise the collapse is rejected.
    /// </summary>
    public static class QemSimplifier
    {
        public static MeshBuffers Simplify(MeshBuffers source, SimplifyOptions options, SimplifyReport report,
            Func<float, bool> progress = null)
        {
            var state = new State(source, options, report);
            state.Run(progress);
            return state.BuildOutput();
        }

        enum EdgeKind { Interior, Border, Crease, HardSeam, NonManifold }

        struct Quadric
        {
            public double a2, ab, ac, ad, b2, bc, bd, c2, cd, d2;

            public static Quadric FromPlane(double a, double b, double c, double d, double w)
            {
                return new Quadric
                {
                    a2 = a * a * w, ab = a * b * w, ac = a * c * w, ad = a * d * w,
                    b2 = b * b * w, bc = b * c * w, bd = b * d * w,
                    c2 = c * c * w, cd = c * d * w,
                    d2 = d * d * w,
                };
            }

            public void Add(in Quadric q)
            {
                a2 += q.a2; ab += q.ab; ac += q.ac; ad += q.ad;
                b2 += q.b2; bc += q.bc; bd += q.bd;
                c2 += q.c2; cd += q.cd;
                d2 += q.d2;
            }

            public double Evaluate(Vector3 p)
            {
                double x = p.x, y = p.y, z = p.z;
                return a2 * x * x + 2 * ab * x * y + 2 * ac * x * z + 2 * ad * x
                     + b2 * y * y + 2 * bc * y * z + 2 * bd * y
                     + c2 * z * z + 2 * cd * z
                     + d2;
            }
        }

        struct HeapEntry
        {
            public float error;
            public int from;
            public int to;
        }

        sealed class MinHeap
        {
            readonly List<HeapEntry> items = new List<HeapEntry>();

            public int Count => items.Count;

            public void Push(HeapEntry e)
            {
                items.Add(e);
                int i = items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (items[parent].error <= e.error) break;
                    items[i] = items[parent];
                    i = parent;
                }
                items[i] = e;
            }

            public HeapEntry Pop()
            {
                var top = items[0];
                var last = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (items.Count == 0) return top;

                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1;
                    if (l >= items.Count) break;
                    int r = l + 1;
                    int c = r < items.Count && items[r].error < items[l].error ? r : l;
                    if (items[c].error >= last.error) break;
                    items[i] = items[c];
                    i = c;
                }
                items[i] = last;
                return top;
            }
        }

        sealed class State
        {
            const double FlipDotThreshold = 0.2;
            // Bound on how far a triangle may rotate from its original orientation over all collapses,
            // so faces cannot creep step by step into fins perpendicular to the surface.
            const double MaxDeviationDot = 0.3;
            const float BorderConstraintWeight = 2f;
            const float SeamConstraintWeight = 1f;

            readonly MeshBuffers src;
            readonly SimplifyOptions opt;
            readonly SimplifyReport report;

            readonly int[] canonical;   // source vertex -> first exactly identical vertex
            readonly int[] attrClass;   // source vertex -> class ignoring normals/tangents
            readonly int[] wedgePos;    // source vertex -> position id
            readonly Vector3[] pos;     // position id -> position

            readonly int triCount;
            readonly int[] tw;          // 3 corners per triangle, as canonical vertex ids
            readonly int[] tsub;
            readonly bool[] tAlive;
            readonly bool[] tFrozen;    // degenerate input triangles, kept verbatim
            readonly Vector3d[] tOriginalNormal;

            readonly List<int>[] vtris; // position id -> live, non-frozen triangles
            readonly bool[] locked;
            readonly bool[] dead;
            readonly Quadric[] quadric;
            readonly double[] weight;

            readonly double errorLimit;
            readonly double degenerateEpsilon;
            int aliveTriangles;

            readonly MinHeap heap = new MinHeap();

            // Scratch buffers reused by Evaluate/Collapse.
            readonly List<int> nbr = new List<int>();
            readonly List<int> nbrCount = new List<int>();
            readonly List<int> nbrT0 = new List<int>();
            readonly List<int> nbrT1 = new List<int>();
            readonly List<int> nbrOfV = new List<int>();
            readonly List<int> mapFrom = new List<int>();
            readonly List<int> mapTo = new List<int>();
            readonly List<int> opposite = new List<int>();
            readonly List<int> tmpTris = new List<int>();
            readonly List<int> ring = new List<int>();
            readonly List<int> ring2 = new List<int>();

            public State(MeshBuffers source, SimplifyOptions options, SimplifyReport rep)
            {
                src = source;
                opt = options;
                report = rep;

                int n = src.VertexCount;
                canonical = new int[n];
                attrClass = new int[n];
                wedgePos = new int[n];

                var exact = new Dictionary<int, int>(n, new VertexComparer(src, false));
                var loose = new Dictionary<int, int>(n, new VertexComparer(src, true));
                var positionIds = new Dictionary<Vector3, int>(n, ExactPositionComparer.Instance);
                var posList = new List<Vector3>();
                int merged = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!exact.TryGetValue(i, out int c))
                    {
                        exact.Add(i, i);
                        c = i;
                    }
                    else
                    {
                        merged++;
                    }
                    canonical[i] = c;

                    if (!loose.TryGetValue(i, out int a))
                    {
                        loose.Add(i, i);
                        a = i;
                    }
                    attrClass[i] = a;

                    var p = src.positions[i];
                    if (!positionIds.TryGetValue(p, out int pid))
                    {
                        pid = posList.Count;
                        positionIds.Add(p, pid);
                        posList.Add(p);
                    }
                    wedgePos[i] = pid;
                }
                pos = posList.ToArray();

                var bounds = new Bounds(pos.Length > 0 ? pos[0] : Vector3.zero, Vector3.zero);
                foreach (var p in pos) bounds.Encapsulate(p);
                double diagonal = Math.Max(bounds.size.magnitude, 1e-6);
                errorLimit = opt.maxError * diagonal;
                degenerateEpsilon = diagonal * diagonal * 1e-12;

                triCount = src.TriangleCount;
                tw = new int[triCount * 3];
                tsub = new int[triCount];
                tAlive = new bool[triCount];
                tFrozen = new bool[triCount];
                tOriginalNormal = new Vector3d[triCount];

                int np = pos.Length;
                vtris = new List<int>[np];
                for (int i = 0; i < np; i++) vtris[i] = new List<int>(6);
                locked = new bool[np];
                dead = new bool[np];
                quadric = new Quadric[np];
                weight = new double[np];

                int t = 0;
                for (int s = 0; s < src.submeshTriangles.Count; s++)
                {
                    var tris = src.submeshTriangles[s];
                    for (int k = 0; k + 2 < tris.Length; k += 3, t++)
                    {
                        tw[t * 3] = canonical[tris[k]];
                        tw[t * 3 + 1] = canonical[tris[k + 1]];
                        tw[t * 3 + 2] = canonical[tris[k + 2]];
                        tsub[t] = s;
                        tAlive[t] = true;
                    }
                }
                aliveTriangles = triCount;

                // Face quadrics; degenerate triangles are frozen and lock their corners.
                for (t = 0; t < triCount; t++)
                {
                    int p0 = P(t, 0), p1 = P(t, 1), p2 = P(t, 2);
                    var normal = Cross(pos[p0], pos[p1], pos[p2]);
                    double len = Math.Sqrt(Dot(normal, normal));
                    if (p0 == p1 || p1 == p2 || p0 == p2 || len * len <= degenerateEpsilon)
                    {
                        tFrozen[t] = true;
                        locked[p0] = locked[p1] = locked[p2] = true;
                        continue;
                    }

                    vtris[p0].Add(t);
                    vtris[p1].Add(t);
                    vtris[p2].Add(t);

                    double a = normal.x / len, b = normal.y / len, c = normal.z / len;
                    tOriginalNormal[t] = new Vector3d(a, b, c);
                    double d = -(a * pos[p0].x + b * pos[p0].y + c * pos[p0].z);
                    double area = len * 0.5;
                    var q = Quadric.FromPlane(a, b, c, d, area);
                    for (int k = 0; k < 3; k++)
                    {
                        int p = P(t, k);
                        quadric[p].Add(q);
                        weight[p] += area;
                    }
                }

                // Edge classification: lock non-manifold vertices and add boundary constraint planes so
                // that open borders and seams keep their outline when they are allowed to move.
                var edges = new Dictionary<long, (int t0, int t1, int count)>();
                for (t = 0; t < triCount; t++)
                {
                    if (tFrozen[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int a = P(t, k), b = P(t, (k + 1) % 3);
                        long key = EdgeKey(a, b);
                        if (edges.TryGetValue(key, out var e))
                            edges[key] = (e.t0, e.count == 1 ? t : e.t1, e.count + 1);
                        else
                            edges.Add(key, (t, -1, 1));
                    }
                }

                foreach (var pair in edges)
                {
                    int a = (int)(pair.Key >> 32), b = (int)(pair.Key & 0xffffffff);
                    var e = pair.Value;
                    if (e.count > 2)
                    {
                        locked[a] = locked[b] = true;
                        continue;
                    }

                    var kind = e.count == 1 ? EdgeKind.Border : Classify(a, b, e.t0, e.t1);
                    if (kind == EdgeKind.Border)
                        AddEdgeConstraint(a, b, e.t0, BorderConstraintWeight);
                    else if (kind == EdgeKind.HardSeam)
                        AddEdgeConstraint(a, b, e.t0, SeamConstraintWeight);
                }

                int lockedCount = 0;
                foreach (bool l in locked) if (l) lockedCount++;

                report.trianglesBefore = triCount;
                report.verticesBefore = n;
                report.mergedDuplicateVertices = merged;
                report.lockedVertices = lockedCount;
                report.targetTriangles = Mathf.Clamp(Mathf.RoundToInt(triCount * opt.targetRatio), 1, triCount);

                foreach (var pair in edges)
                {
                    if (pair.Value.count > 2) continue;
                    int a = (int)(pair.Key >> 32), b = (int)(pair.Key & 0xffffffff);
                    Push(a, b);
                    Push(b, a);
                }
            }

            int P(int t, int k) => wedgePos[tw[t * 3 + k]];

            static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            static Vector3d Cross(Vector3 p0, Vector3 p1, Vector3 p2)
            {
                double ux = p1.x - p0.x, uy = p1.y - p0.y, uz = p1.z - p0.z;
                double vx = p2.x - p0.x, vy = p2.y - p0.y, vz = p2.z - p0.z;
                return new Vector3d(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
            }

            static double Dot(Vector3d a, Vector3d b) => a.x * b.x + a.y * b.y + a.z * b.z;

            bool Contains(int t, int p) => P(t, 0) == p || P(t, 1) == p || P(t, 2) == p;

            int WedgeAt(int t, int p)
            {
                for (int k = 0; k < 3; k++)
                    if (P(t, k) == p)
                        return tw[t * 3 + k];
                return -1;
            }

            EdgeKind Classify(int a, int b, int t0, int t1)
            {
                int wa0 = WedgeAt(t0, a), wa1 = WedgeAt(t1, a);
                int wb0 = WedgeAt(t0, b), wb1 = WedgeAt(t1, b);
                bool sameSub = tsub[t0] == tsub[t1];
                if (wa0 == wa1 && wb0 == wb1 && sameSub)
                    return EdgeKind.Interior;
                if (!sameSub || attrClass[wa0] != attrClass[wa1] || attrClass[wb0] != attrClass[wb1])
                    return EdgeKind.HardSeam;
                return EdgeKind.Crease;
            }

            void AddEdgeConstraint(int a, int b, int t, float w)
            {
                var faceNormal = Cross(pos[P(t, 0)], pos[P(t, 1)], pos[P(t, 2)]);
                Vector3 pa = pos[a], pb = pos[b];
                var edge = new Vector3d(pb.x - pa.x, pb.y - pa.y, pb.z - pa.z);
                double edgeLen2 = Dot(edge, edge);
                // Plane containing the edge and perpendicular to the face.
                var n = new Vector3d(
                    edge.y * faceNormal.z - edge.z * faceNormal.y,
                    edge.z * faceNormal.x - edge.x * faceNormal.z,
                    edge.x * faceNormal.y - edge.y * faceNormal.x);
                double len = Math.Sqrt(Dot(n, n));
                if (len <= 0) return;
                double nx = n.x / len, ny = n.y / len, nz = n.z / len;
                double d = -(nx * pa.x + ny * pa.y + nz * pa.z);
                var q = Quadric.FromPlane(nx, ny, nz, d, edgeLen2 * w);
                quadric[a].Add(q);
                quadric[b].Add(q);
            }

            float CollapseError(int u, int v)
            {
                var q = quadric[u];
                q.Add(quadric[v]);
                double w = Math.Max(weight[u] + weight[v], 1e-30);
                return (float)Math.Sqrt(Math.Max(0, q.Evaluate(pos[v])) / w);
            }

            void Push(int u, int v)
            {
                if (locked[u] || dead[u] || dead[v]) return;
                heap.Push(new HeapEntry { error = CollapseError(u, v), from = u, to = v });
            }

            void GatherNeighbours(int u)
            {
                nbr.Clear();
                nbrCount.Clear();
                nbrT0.Clear();
                nbrT1.Clear();
                foreach (int t in vtris[u])
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int p = P(t, k);
                        if (p == u) continue;
                        int idx = nbr.IndexOf(p);
                        if (idx < 0)
                        {
                            nbr.Add(p);
                            nbrCount.Add(1);
                            nbrT0.Add(t);
                            nbrT1.Add(-1);
                        }
                        else
                        {
                            nbrCount[idx]++;
                            if (nbrCount[idx] == 2) nbrT1[idx] = t;
                        }
                    }
                }
            }

            void GatherPositions(int u, List<int> into)
            {
                into.Clear();
                foreach (int t in vtris[u])
                    for (int k = 0; k < 3; k++)
                    {
                        int p = P(t, k);
                        if (p != u && !into.Contains(p)) into.Add(p);
                    }
            }

            /// <summary>Checks every rule for collapsing u onto v and fills the wedge map on success.</summary>
            bool Evaluate(int u, int v, out float error)
            {
                error = float.MaxValue;
                if (dead[u] || dead[v] || locked[u]) return false;

                GatherNeighbours(u);
                int uvIndex = nbr.IndexOf(v);
                if (uvIndex < 0) return false;

                int border = 0, seams = 0, hardSeams = 0;
                var uvKind = EdgeKind.Interior;
                for (int i = 0; i < nbr.Count; i++)
                {
                    EdgeKind kind;
                    if (nbrCount[i] == 1) kind = EdgeKind.Border;
                    else if (nbrCount[i] > 2) kind = EdgeKind.NonManifold;
                    else kind = Classify(u, nbr[i], nbrT0[i], nbrT1[i]);

                    if (kind == EdgeKind.NonManifold)
                    {
                        locked[u] = true;
                        return false;
                    }
                    if (kind == EdgeKind.Border) border++;
                    if (kind == EdgeKind.Crease || kind == EdgeKind.HardSeam) seams++;
                    if (kind == EdgeKind.HardSeam) hardSeams++;
                    if (i == uvIndex) uvKind = kind;
                }

                // Border vertices may only slide along their own border.
                if (border > 0)
                {
                    if (opt.preserveBorders || border != 2 || seams > 0 || uvKind != EdgeKind.Border)
                        return false;
                }

                // Seam vertices may only slide along a seam that simply passes through them.
                if (seams > 0)
                {
                    if (seams != 2 || (uvKind != EdgeKind.Crease && uvKind != EdgeKind.HardSeam))
                        return false;
                    if (opt.protectSeams && hardSeams > 0)
                        return false;
                }

                // Wedge map: every render vertex at u must land on exactly one render vertex at v.
                mapFrom.Clear();
                mapTo.Clear();
                opposite.Clear();
                foreach (int t in vtris[u])
                {
                    if (!Contains(t, v)) continue;
                    int a = WedgeAt(t, u), b = WedgeAt(t, v);
                    int idx = mapFrom.IndexOf(a);
                    if (idx < 0)
                    {
                        mapFrom.Add(a);
                        mapTo.Add(b);
                    }
                    else if (mapTo[idx] != b)
                    {
                        return false;
                    }

                    for (int k = 0; k < 3; k++)
                    {
                        int p = P(t, k);
                        if (p != u && p != v) opposite.Add(p);
                    }
                }

                foreach (int t in vtris[u])
                    if (!Contains(t, v) && mapFrom.IndexOf(WedgeAt(t, u)) < 0)
                        return false;

                // Link condition: u and v may only share the vertices opposite the edge uv.
                // This keeps the surface manifold and stops separate parts from being fused.
                GatherPositions(v, nbrOfV);
                foreach (int p in nbr)
                    if (nbrOfV.Contains(p) && !opposite.Contains(p))
                        return false;

                // Geometry: no flipped, degenerate or duplicated triangles.
                Vector3 pv = pos[v];
                foreach (int t in vtris[u])
                {
                    if (Contains(t, v)) continue;
                    int p0 = P(t, 0), p1 = P(t, 1), p2 = P(t, 2);
                    var before = Cross(pos[p0], pos[p1], pos[p2]);
                    var after = Cross(p0 == u ? pv : pos[p0], p1 == u ? pv : pos[p1], p2 == u ? pv : pos[p2]);
                    double afterLen2 = Dot(after, after);
                    if (afterLen2 <= degenerateEpsilon) return false;
                    double cos = Dot(before, after) / Math.Sqrt(Dot(before, before) * afterLen2);
                    if (cos < FlipDotThreshold) return false;
                    if (Dot(tOriginalNormal[t], after) / Math.Sqrt(afterLen2) < MaxDeviationDot) return false;

                    int x = p0 == u ? p1 : p0;
                    int y = p2 == u ? p1 : p2;
                    foreach (int t2 in vtris[v])
                        if (Contains(t2, x) && Contains(t2, y))
                            return false;
                }

                error = CollapseError(u, v);
                return true;
            }

            void Collapse(int u, int v)
            {
                tmpTris.Clear();
                tmpTris.AddRange(vtris[u]);
                foreach (int t in tmpTris)
                {
                    if (Contains(t, v))
                    {
                        tAlive[t] = false;
                        aliveTriangles--;
                        for (int k = 0; k < 3; k++)
                        {
                            int p = P(t, k);
                            if (p != u) vtris[p].Remove(t);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            int w = tw[t * 3 + k];
                            if (wedgePos[w] == u)
                                tw[t * 3 + k] = mapTo[mapFrom.IndexOf(w)];
                        }
                        vtris[v].Add(t);
                    }
                }

                vtris[u].Clear();
                dead[u] = true;
                quadric[v].Add(quadric[u]);
                weight[v] += weight[u];
            }

            public void Run(Func<float, bool> progress)
            {
                int target = report.targetTriangles;
                int toRemove = Math.Max(1, triCount - target);
                int iterations = 0;

                while (aliveTriangles > target && heap.Count > 0)
                {
                    if (progress != null && (++iterations & 255) == 0)
                    {
                        float done = (triCount - aliveTriangles) / (float)toRemove;
                        if (progress(Mathf.Clamp01(done)))
                            throw new OperationCanceledException();
                    }

                    var e = heap.Pop();
                    if (dead[e.from] || dead[e.to] || locked[e.from]) continue;

                    if (!Evaluate(e.from, e.to, out float error)) continue;
                    if (error > errorLimit)
                    {
                        report.hitErrorLimit = true;
                        continue;
                    }
                    if (error > e.error * 1.0001f + 1e-12f)
                    {
                        heap.Push(new HeapEntry { error = error, from = e.from, to = e.to });
                        continue;
                    }

                    int v = e.to;
                    Collapse(e.from, v);

                    // Re-queue every collapse whose moving vertex had its fan changed.
                    GatherPositions(v, ring);
                    ring.Add(v);
                    foreach (int n in ring)
                    {
                        GatherPositions(n, ring2);
                        foreach (int m in ring2) Push(n, m);
                    }
                }

                report.reachedTarget = aliveTriangles <= target;
            }

            public MeshBuffers BuildOutput()
            {
                var dst = src.CreateEmptyLike();
                var remap = new int[src.VertexCount];
                for (int i = 0; i < remap.Length; i++) remap[i] = -1;

                for (int t = 0; t < triCount; t++)
                {
                    if (!tAlive[t]) continue;
                    for (int k = 0; k < 3; k++) remap[tw[t * 3 + k]] = 0;
                }

                for (int i = 0; i < remap.Length; i++)
                    if (remap[i] == 0)
                        remap[i] = dst.AppendVertex(src, i);

                var lists = new List<int>[src.submeshTriangles.Count];
                for (int s = 0; s < lists.Length; s++) lists[s] = new List<int>();
                for (int t = 0; t < triCount; t++)
                {
                    if (!tAlive[t]) continue;
                    var list = lists[tsub[t]];
                    for (int k = 0; k < 3; k++) list.Add(remap[tw[t * 3 + k]]);
                }
                foreach (var list in lists) dst.submeshTriangles.Add(list.ToArray());

                report.trianglesAfter = dst.TriangleCount;
                report.verticesAfter = dst.VertexCount;
                return dst;
            }
        }

        readonly struct Vector3d
        {
            public readonly double x, y, z;
            public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        }
    }
}
