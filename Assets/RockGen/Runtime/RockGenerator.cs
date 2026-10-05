using System;
using System.Collections.Generic;
using UnityEngine;

namespace RockGen
{
    /// <summary>Plain mesh data so the generator can run without touching Unity objects.</summary>
    public sealed class RockMeshData
    {
        /// <summary>Flat-shaded: three vertices per triangle.</summary>
        public Vector3[] vertices;
        public Vector3[] normals;
        public int[] triangles;
        public RockSettings settings;

        public int TriangleCount => triangles.Length / 3;

        public Mesh ToMesh(string name = null)
        {
            var mesh = new Mesh { name = name ?? $"Rock_{settings.seed}" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0, true);
            return mesh;
        }
    }

    /// <summary>
    /// Builds low-poly rocks from code:
    /// 1. points on a sphere in general position, pushed by seeded lobes, roughness and lean;
    /// 2. convex hull of those points (closed, outward, no spikes);
    /// 3. cleavage planes cut flat chips off, and an optional floor plane flattens the base;
    /// 4. rescale to the requested size with the pivot at the bottom centre;
    /// 5. split vertices per face for flat shading.
    /// Same <see cref="AlgorithmVersion"/> + same settings = same mesh.
    /// </summary>
    public static class RockGenerator
    {
        public const int AlgorithmVersion = 1;
        const int MaxAttempts = 24;
        const double HullEpsilon = 1e-6;
        const double ClipEpsilon = 1e-2;

        public static RockMeshData Generate(RockSettings input)
        {
            var s = input.Clamped();
            // A hull of n points has at most 2n - 4 triangles; leave room for the flat base and chips,
            // and shrink the point count if a result still exceeds the budget.
            int pointCount = Mathf.Max(6, Mathf.RoundToInt(s.triangleBudget * 0.42f) + 2);

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var rng = new RockRandom(s.seed, (ulong)(AlgorithmVersion * 1000 + attempt));
                var data = TryBuild(s, pointCount, ref rng);
                if (data != null && data.TriangleCount > s.triangleBudget)
                {
                    pointCount = Mathf.Max(6, pointCount * 85 / 100);
                    continue;
                }
                if (data != null && RockValidation.Validate(data, out _))
                    return data;
                // Rare numerical failure: try the next deterministic stream.
            }
            throw new InvalidOperationException($"Could not build a valid rock for seed {s.seed}.");
        }

        static RockMeshData TryBuild(in RockSettings s, int n, ref RockRandom rng)
        {
            float rough = s.roughness, asym = s.asymmetry;

            // Shape lobes: linear terms shift volume to one side, quadratic terms stretch.
            var lobeDir = new Vector3[3];
            var lobeLinear = new float[3];
            var lobeStretch = new float[3];
            for (int i = 0; i < 3; i++)
            {
                lobeDir[i] = rng.OnUnitSphere();
                lobeLinear[i] = rng.Range(-1f, 1f) * asym * 0.28f;
                lobeStretch[i] = rng.Range(-1f, 1f) * (0.15f + 0.35f * asym);
            }
            var lean = new Vector2(rng.Range(-1f, 1f), rng.Range(-1f, 1f)) * (asym * 0.25f);
            float rotation = rng.Range(0f, 2f * Mathf.PI);
            float jitter = 0.35f / Mathf.Sqrt(n);

            var points = new List<Vector3>(n);
            const float golden = 2.39996323f;
            for (int i = 0; i < n; i++)
            {
                // Jittered Fibonacci sphere: even spacing, no rows, no coplanar groups.
                float y = 1f - 2f * (i + 0.5f) / n;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float a = i * golden + rotation;
                var dir = (new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r) + rng.OnUnitSphere() * jitter).normalized;

                float radius = 1f;
                for (int k = 0; k < 3; k++)
                {
                    float d = Vector3.Dot(dir, lobeDir[k]);
                    radius += lobeLinear[k] * d + lobeStretch[k] * (d * d - 1f / 3f);
                }
                radius *= 1f + rough * rng.Range(-0.12f, 0.12f);
                radius = Mathf.Clamp(radius, 0.55f, 1.6f);
                var p = dir * radius;
                p.x += lean.x * (p.y + 1f);
                p.z += lean.y * (p.y + 1f);
                points.Add(p);
            }

            var hull = ConvexHull.Build(points, HullEpsilon);
            if (hull == null) return null;

            var verts = new List<D3>(points.Count);
            foreach (var p in points) verts.Add(new D3(p.x, p.y, p.z));
            var tris = new List<int>(hull);
            ConvexClip.Compact(verts, tris);
            if (MinArea(verts, tris) < MinUnitArea) return null;

            // Flat base first (it is a requested feature): keep y >= floor, i.e. Dot(-up, p) <= -floor.
            if (s.bottomFlatten > 0.001f)
            {
                Extent(verts, new D3(0, 1, 0), out double minY, out double maxY);
                double floor = minY + s.bottomFlatten * 0.38 * (maxY - minY);
                if (!ConvexClip.Clip(verts, tris, new D3(0, -1, 0), -floor, ClipEpsilon)) return null;
                if (MinArea(verts, tris) < MinUnitArea) return null;
            }

            // Cleavage planes: flat chips on the sides and top; more with higher roughness. A chip is
            // skipped (mesh restored) if it would exceed the triangle budget or leave a sliver.
            int planeCount = 1 + Mathf.RoundToInt(rough * 4f);
            for (int i = 0; i < planeCount; i++)
            {
                var nrm = rng.OnUnitSphere();
                nrm.y = Mathf.Abs(nrm.y) * 0.6f + 0.1f;
                nrm.Normalize();
                float depth = rng.Range(0.62f, 0.9f) - rough * 0.12f;
                var axis = new D3(nrm.x, nrm.y, nrm.z);
                // Offset relative to the current extent along the normal keeps the cut proportional.
                Extent(verts, axis, out double lo, out double hi);

                var savedVerts = new List<D3>(verts);
                var savedTris = new List<int>(tris);
                if (!ConvexClip.Clip(verts, tris, axis, lo + (hi - lo) * (0.5 + 0.5 * depth), ClipEpsilon)) continue;
                if (tris.Count / 3 > s.triangleBudget || MinArea(verts, tris) < MinUnitArea)
                {
                    verts.Clear(); verts.AddRange(savedVerts);
                    tris.Clear(); tris.AddRange(savedTris);
                }
            }

            return Finish(verts, tris, s);
        }

        // Minimum triangle area in generator space (the rock spans roughly 2 units here).
        const double MinUnitArea = 2e-5;

        static double MinArea(List<D3> verts, List<int> tris)
        {
            double min = double.MaxValue;
            for (int t = 0; t < tris.Count; t += 3)
                min = Math.Min(min, D3.Cross(verts[tris[t + 1]] - verts[tris[t]], verts[tris[t + 2]] - verts[tris[t]]).Length * 0.5);
            return min;
        }

        static void Extent(List<D3> verts, D3 axis, out double lo, out double hi)
        {
            lo = double.MaxValue;
            hi = double.MinValue;
            foreach (var v in verts)
            {
                double d = D3.Dot(v, axis);
                lo = Math.Min(lo, d);
                hi = Math.Max(hi, d);
            }
        }

        static RockMeshData Finish(List<D3> verts, List<int> tris, in RockSettings s)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var v in verts)
            {
                minX = Math.Min(minX, v.x); maxX = Math.Max(maxX, v.x);
                minY = Math.Min(minY, v.y); maxY = Math.Max(maxY, v.y);
                minZ = Math.Min(minZ, v.z); maxZ = Math.Max(maxZ, v.z);
            }
            if (maxX - minX <= 0 || maxY - minY <= 0 || maxZ - minZ <= 0) return null;

            // Rescale to the exact requested size; pivot at the bottom centre. Positions are converted
            // once into a shared table, so split corners on the same point are bit-identical.
            double sx = s.size.x / (maxX - minX), sy = s.size.y / (maxY - minY), sz = s.size.z / (maxZ - minZ);
            double cx = (minX + maxX) * 0.5, cz = (minZ + maxZ) * 0.5;
            var final = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                final[i] = new Vector3((float)((verts[i].x - cx) * sx), (float)((verts[i].y - minY) * sy), (float)((verts[i].z - cz) * sz));

            var data = new RockMeshData
            {
                vertices = new Vector3[tris.Count],
                normals = new Vector3[tris.Count],
                triangles = new int[tris.Count],
                settings = s,
            };
            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 a = final[tris[t]], b = final[tris[t + 1]], c = final[tris[t + 2]];
                // Manual normalisation: Vector3.normalized returns zero below 1e-5, which tiny rocks can hit.
                var cross = Vector3.Cross(b - a, c - a);
                var normal = cross / cross.magnitude;
                data.vertices[t] = a;
                data.vertices[t + 1] = b;
                data.vertices[t + 2] = c;
                for (int k = 0; k < 3; k++)
                {
                    data.normals[t + k] = normal;
                    data.triangles[t + k] = t + k;
                }
            }
            return data;
        }
    }
}
