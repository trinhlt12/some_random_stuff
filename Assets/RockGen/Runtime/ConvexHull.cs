using System.Collections.Generic;
using UnityEngine;

namespace RockGen
{
    /// <summary>
    /// Incremental 3D convex hull (double precision). Output triangles use Unity's winding:
    /// Cross(b - a, c - a) points out of the hull. Points within <c>epsilon</c> of the surface
    /// are treated as inside, so coplanar points (e.g. a flattened base) never create slivers.
    /// </summary>
    public static class ConvexHull
    {
        struct Face
        {
            public int a, b, c;
            public double nx, ny, nz, d; // unit outward normal, plane offset
        }

        /// <summary>Returns hull triangles as indices into <paramref name="points"/>, or null if degenerate.</summary>
        public static List<int> Build(IList<Vector3> points, double epsilon)
        {
            int n = points.Count;
            if (n < 4) return null;

            var p = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                p[i * 3] = points[i].x;
                p[i * 3 + 1] = points[i].y;
                p[i * 3 + 2] = points[i].z;
            }

            if (!InitialTetrahedron(p, n, epsilon, out int i0, out int i1, out int i2, out int i3))
                return null;

            var faces = new List<Face>();
            double cx = (p[i0 * 3] + p[i1 * 3] + p[i2 * 3] + p[i3 * 3]) / 4;
            double cy = (p[i0 * 3 + 1] + p[i1 * 3 + 1] + p[i2 * 3 + 1] + p[i3 * 3 + 1]) / 4;
            double cz = (p[i0 * 3 + 2] + p[i1 * 3 + 2] + p[i2 * 3 + 2] + p[i3 * 3 + 2]) / 4;
            AddOriented(faces, p, i0, i1, i2, cx, cy, cz);
            AddOriented(faces, p, i0, i1, i3, cx, cy, cz);
            AddOriented(faces, p, i0, i2, i3, cx, cy, cz);
            AddOriented(faces, p, i1, i2, i3, cx, cy, cz);

            var visible = new List<int>();
            var edges = new Dictionary<long, int>();
            for (int i = 0; i < n; i++)
            {
                if (i == i0 || i == i1 || i == i2 || i == i3) continue;

                visible.Clear();
                for (int f = 0; f < faces.Count; f++)
                    if (Distance(faces[f], p, i) > epsilon)
                        visible.Add(f);
                if (visible.Count == 0) continue; // inside or on the surface

                // Horizon = directed edges of visible faces whose reverse edge is not visible.
                edges.Clear();
                foreach (int f in visible)
                {
                    var face = faces[f];
                    AddEdge(edges, face.a, face.b);
                    AddEdge(edges, face.b, face.c);
                    AddEdge(edges, face.c, face.a);
                }

                for (int k = visible.Count - 1; k >= 0; k--)
                {
                    int f = visible[k];
                    faces[f] = faces[faces.Count - 1];
                    faces.RemoveAt(faces.Count - 1);
                }

                foreach (var pair in edges)
                {
                    int a = (int)(pair.Key >> 32), b = (int)(pair.Key & 0xffffffff);
                    if (edges.ContainsKey(Key(b, a))) continue;
                    // Same direction along the horizon edge as the removed face: winding stays outward.
                    faces.Add(MakeFace(p, a, b, i));
                }
            }

            var result = new List<int>(faces.Count * 3);
            foreach (var f in faces)
            {
                result.Add(f.a);
                result.Add(f.b);
                result.Add(f.c);
            }
            return result;
        }

        static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        static void AddEdge(Dictionary<long, int> edges, int a, int b) => edges[Key(a, b)] = 1;

        static double Distance(in Face f, double[] p, int i) =>
            f.nx * p[i * 3] + f.ny * p[i * 3 + 1] + f.nz * p[i * 3 + 2] - f.d;

        static Face MakeFace(double[] p, int a, int b, int c)
        {
            double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
            double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double len = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0) { nx /= len; ny /= len; nz /= len; }
            return new Face
            {
                a = a, b = b, c = c, nx = nx, ny = ny, nz = nz,
                d = nx * p[a * 3] + ny * p[a * 3 + 1] + nz * p[a * 3 + 2],
            };
        }

        static void AddOriented(List<Face> faces, double[] p, int a, int b, int c, double cx, double cy, double cz)
        {
            var f = MakeFace(p, a, b, c);
            if (f.nx * cx + f.ny * cy + f.nz * cz - f.d > 0) f = MakeFace(p, a, c, b);
            faces.Add(f);
        }

        static bool InitialTetrahedron(double[] p, int n, double eps, out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = -1;

            i0 = 0;
            for (int i = 1; i < n; i++)
                if (p[i * 3] < p[i0 * 3]) i0 = i;

            double best = 0;
            for (int i = 0; i < n; i++)
            {
                double d = Dist2(p, i, i0);
                if (d > best) { best = d; i1 = i; }
            }
            if (i1 < 0 || best <= eps * eps) return false;

            best = 0;
            for (int i = 0; i < n; i++)
            {
                double d = LineDist2(p, i0, i1, i);
                if (d > best) { best = d; i2 = i; }
            }
            if (i2 < 0 || best <= eps * eps) return false;

            var plane = MakeFace(p, i0, i1, i2);
            best = 0;
            for (int i = 0; i < n; i++)
            {
                double d = System.Math.Abs(Distance(plane, p, i));
                if (d > best) { best = d; i3 = i; }
            }
            return i3 >= 0 && best > eps;
        }

        static double Dist2(double[] p, int a, int b)
        {
            double x = p[a * 3] - p[b * 3], y = p[a * 3 + 1] - p[b * 3 + 1], z = p[a * 3 + 2] - p[b * 3 + 2];
            return x * x + y * y + z * z;
        }

        static double LineDist2(double[] p, int a, int b, int i)
        {
            double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
            double vx = p[i * 3] - p[a * 3], vy = p[i * 3 + 1] - p[a * 3 + 1], vz = p[i * 3 + 2] - p[a * 3 + 2];
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            double u2 = ux * ux + uy * uy + uz * uz;
            return u2 <= 0 ? 0 : (cx * cx + cy * cy + cz * cz) / u2;
        }
    }
}
