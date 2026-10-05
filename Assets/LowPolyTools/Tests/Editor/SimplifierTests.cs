using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace LowPolyTools.Tests
{
    public class SimplifierTests
    {
        static LowPolySettings Settings(float ratio, float maxError, bool borders = true, bool seams = true, bool flat = false) =>
            new LowPolySettings
            {
                simplify = true,
                targetRatio = ratio,
                maxError = maxError,
                preserveBorders = borders,
                protectSeams = seams,
                flatNormals = flat,
            };

        static void AssertNoDegenerateTriangles(Mesh mesh)
        {
            var v = mesh.vertices;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var t = mesh.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                {
                    float area = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).magnitude;
                    Assert.Greater(area, 1e-10f, $"Degenerate triangle in submesh {s}");
                }
            }
        }

        [Test]
        public void UnityCube_IsNotCollapsed()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var cube = go.GetComponent<MeshFilter>().sharedMesh;
            try
            {
                var r = LowPolyConverter.ConvertMesh(cube, Settings(0.1f, 0.05f, borders: false, seams: false));
                Assert.AreEqual(12, r.trianglesAfter);
                Assert.AreEqual(cube.bounds.size, r.result.bounds.size);
                Object.DestroyImmediate(r.result);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void WeldedCube_IsNotCollapsed()
        {
            var cube = TestMeshes.WeldedCube();
            var r = LowPolyConverter.ConvertMesh(cube, Settings(0.05f, 0.05f, borders: false, seams: false));
            Assert.AreEqual(12, r.trianglesAfter);
            Assert.AreEqual(cube.bounds.size, r.result.bounds.size);
            Assert.IsFalse(r.simplify.reachedTarget);
        }

        [Test]
        public void CurvedSphere_ReducesButKeepsShape()
        {
            var sphere = TestMeshes.UvSphere(32, 64);
            var r = LowPolyConverter.ConvertMesh(sphere, Settings(0.25f, 0.02f));
            Debug.Log($"Sphere: {r.trianglesBefore} -> {r.trianglesAfter} tris ({r.simplify.StopReason})");

            Assert.Less(r.trianglesAfter, r.trianglesBefore * 0.5f, "Sphere should lose at least half its triangles");
            AssertNoDegenerateTriangles(r.result);

            // Every source vertex must stay close to the simplified surface.
            float worst = 0f;
            foreach (var p in sphere.vertices)
                worst = Mathf.Max(worst, TestMeshes.DistanceToMesh(p, r.result));
            Debug.Log($"Sphere max deviation: {worst:0.0000} (radius 1)");
            Assert.Less(worst, 0.05f);

            // No face may end up standing perpendicular to the surface (a "fin").
            var rv = r.result.vertices;
            var rt = r.result.triangles;
            for (int i = 0; i < rt.Length; i += 3)
            {
                var face = Vector3.Cross(rv[rt[i + 1]] - rv[rt[i]], rv[rt[i + 2]] - rv[rt[i]]).normalized;
                var centroid = ((rv[rt[i]] + rv[rt[i + 1]] + rv[rt[i + 2]]) / 3f).normalized;
                Assert.Greater(Vector3.Dot(face, centroid), 0.3f, "Face rotated away from the surface");
            }

            var size = r.result.bounds.size;
            Assert.AreEqual(2f, size.x, 0.05f);
            Assert.AreEqual(2f, size.y, 0.001f);
            Assert.AreEqual(2f, size.z, 0.05f);
        }

        [Test]
        public void UvSeams_AreNeverTorn([Values(true, false)] bool protectSeams)
        {
            var sphere = TestMeshes.UvSphere(24, 48);
            var r = LowPolyConverter.ConvertMesh(sphere, Settings(0.2f, 0.03f, seams: protectSeams));
            var uv = new List<Vector2>();
            r.result.GetUVs(0, uv);
            var t = r.result.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                float min = Mathf.Min(uv[t[i]].x, Mathf.Min(uv[t[i + 1]].x, uv[t[i + 2]].x));
                float max = Mathf.Max(uv[t[i]].x, Mathf.Max(uv[t[i + 1]].x, uv[t[i + 2]].x));
                Assert.Less(max - min, 0.5f, "A triangle spans the U seam, i.e. the seam was stitched with the wrong UVs");
            }

            // All surviving vertices carry attributes that existed in the source.
            var sourceKeys = new HashSet<(Vector3, Vector2)>();
            var sv = sphere.vertices;
            var suv = new List<Vector2>();
            sphere.GetUVs(0, suv);
            for (int i = 0; i < sv.Length; i++) sourceKeys.Add((sv[i], suv[i]));
            var rv = r.result.vertices;
            for (int i = 0; i < rv.Length; i++)
                Assert.IsTrue(sourceKeys.Contains((rv[i], uv[i])), "Output vertex has a position/UV pair not present in the source");
        }

        [Test]
        public void ThinPlate_KeepsThicknessAndFaces()
        {
            var plate = TestMeshes.SubdividedBox(new Vector3(1f, 0.02f, 1f), 8);
            var r = LowPolyConverter.ConvertMesh(plate, Settings(0.05f, 0.01f, borders: false, seams: false));
            Debug.Log($"Thin plate: {r.trianglesBefore} -> {r.trianglesAfter} tris ({r.simplify.StopReason})");

            Assert.Less(r.trianglesAfter, r.trianglesBefore / 2);
            Assert.GreaterOrEqual(r.trianglesAfter, 12);
            Assert.AreEqual(plate.bounds.size.y, r.result.bounds.size.y, 1e-6f, "Plate thickness changed");
            Assert.AreEqual(plate.bounds.size.x, r.result.bounds.size.x, 1e-6f);
            AssertNoDegenerateTriangles(r.result);

            // Both large faces must still exist.
            bool up = false, down = false;
            var v = r.result.vertices;
            var t = r.result.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                var n = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).normalized;
                up |= n.y > 0.99f;
                down |= n.y < -0.99f;
            }
            Assert.IsTrue(up && down);
        }

        [Test]
        public void ThinPole_DoesNotDisappear()
        {
            var pole = TestMeshes.Cylinder(12, 30, 0.01f, 1f);
            var r = LowPolyConverter.ConvertMesh(pole, Settings(0.1f, 0.02f, borders: true, seams: false));
            Debug.Log($"Thin pole: {r.trianglesBefore} -> {r.trianglesAfter} tris ({r.simplify.StopReason})");

            Assert.Greater(r.trianglesAfter, 0);
            AssertNoDegenerateTriangles(r.result);
            Assert.AreEqual(pole.bounds.size.y, r.result.bounds.size.y, 1e-6f);
            Assert.Greater(r.result.bounds.size.x, 0.015f, "Pole collapsed sideways");
            Assert.Greater(r.result.bounds.size.z, 0.015f, "Pole collapsed sideways");
        }

        [Test]
        public void MultiSubmesh_MaterialsStayOnTheirSide([Values(true, false)] bool protectSeams)
        {
            var grid = TestMeshes.Grid(16, true);
            var r = LowPolyConverter.ConvertMesh(grid, Settings(0.1f, 0.01f, borders: true, seams: protectSeams));
            Debug.Log($"Grid 2 submesh (protect={protectSeams}): {r.trianglesBefore} -> {r.trianglesAfter}");

            Assert.AreEqual(2, r.result.subMeshCount);
            Assert.Less(r.trianglesAfter, r.trianglesBefore / 2);
            var v = r.result.vertices;
            for (int s = 0; s < 2; s++)
            {
                var t = r.result.GetTriangles(s);
                Assert.Greater(t.Length, 0, $"Submesh {s} vanished");
                for (int i = 0; i < t.Length; i += 3)
                {
                    float cx = (v[t[i]].x + v[t[i + 1]].x + v[t[i + 2]].x) / 3f;
                    if (s == 0) Assert.LessOrEqual(cx, 1e-5f, "Submesh 0 triangle crossed the material boundary");
                    else Assert.GreaterOrEqual(cx, -1e-5f, "Submesh 1 triangle crossed the material boundary");
                }
            }

            // Outline of the open plane is preserved.
            Assert.AreEqual(grid.bounds.size, r.result.bounds.size);
        }

        [Test]
        public void VertexColors_ArePreserved()
        {
            var grid = TestMeshes.Grid(12, false, withColors: true);
            var r = LowPolyConverter.ConvertMesh(grid, Settings(0.2f, 0.01f));
            var source = new Dictionary<Vector3, Color>();
            var sc = new List<Color>();
            grid.GetColors(sc);
            var sv = grid.vertices;
            for (int i = 0; i < sv.Length; i++) source[sv[i]] = sc[i];

            var rc = new List<Color>();
            r.result.GetColors(rc);
            Assert.AreEqual(r.result.vertexCount, rc.Count);
            var rv = r.result.vertices;
            for (int i = 0; i < rv.Length; i++)
                Assert.AreEqual(source[rv[i]], rc[i]);
        }

        [Test]
        public void FlatNormals_FacesPointOutAndKeepUVs()
        {
            var sphere = TestMeshes.UvSphere(12, 24);
            var settings = Settings(1f, 0.01f, flat: true);
            settings.simplify = false;
            var r = LowPolyConverter.ConvertMesh(sphere, settings);

            Assert.AreEqual(r.trianglesBefore, r.trianglesAfter);
            Assert.GreaterOrEqual(r.verticesAfter, r.verticesBefore, "Flat normals should split vertices");

            var sv = sphere.vertices;
            var suv = new List<Vector2>();
            sphere.GetUVs(0, suv);
            var st = sphere.triangles;

            var v = r.result.vertices;
            var n = r.result.normals;
            var uv = new List<Vector2>();
            r.result.GetUVs(0, uv);
            var t = r.result.triangles;
            Assert.AreEqual(st.Length, t.Length);
            for (int i = 0; i < t.Length; i += 3)
            {
                var face = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).normalized;
                var centroid = (v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f;
                Assert.Greater(Vector3.Dot(face, centroid), 0f, "Inverted face");
                for (int k = 0; k < 3; k++)
                {
                    Assert.Greater(Vector3.Dot(n[t[i + k]], face), 0.9999f, "Corner normal is not the face normal");
                    Assert.AreEqual(sv[st[i + k]], v[t[i + k]]);
                    Assert.AreEqual(suv[st[i + k]], uv[t[i + k]], "UV changed");
                }
            }
        }

        [Test]
        public void RecalculatedNormals_FollowSurfaceAndKeepHardEdges()
        {
            var sphere = TestMeshes.UvSphere(32, 64);
            var r = LowPolyConverter.ConvertMesh(sphere, Settings(0.2f, 0.03f));
            var v = r.result.vertices;
            var n = r.result.normals;
            float worst = 1f;
            int worstIndex = 0;
            for (int i = 0; i < v.Length; i++)
            {
                float d = Vector3.Dot(n[i], v[i].normalized);
                if (d < worst) { worst = d; worstIndex = i; }
            }
            Debug.Log($"Sphere recalculated normals: worst dot {worst:0.0000} at {v[worstIndex]} n={n[worstIndex]}");
            Assert.Greater(worst, 0.95f, "Sphere normal no longer points outwards");

            var box = TestMeshes.SubdividedBox(Vector3.one, 6);
            var rb = LowPolyConverter.ConvertMesh(box, Settings(0.1f, 0.01f, seams: false));
            foreach (var normal in rb.result.normals)
            {
                float max = Mathf.Max(Mathf.Abs(normal.x), Mathf.Max(Mathf.Abs(normal.y), Mathf.Abs(normal.z)));
                Assert.Greater(max, 0.9999f, "Hard box edge was smoothed");
            }
        }

        [Test]
        public void SimplifyThenFlat_Works()
        {
            var sphere = TestMeshes.UvSphere(24, 48);
            var r = LowPolyConverter.ConvertMesh(sphere, Settings(0.3f, 0.03f, flat: true));
            Assert.Less(r.trianglesAfter, r.trianglesBefore);
            Assert.IsNotNull(r.flatNormals);
            AssertNoDegenerateTriangles(r.result);
        }

        [Test]
        public void UnsupportedMeshes_AreReported()
        {
            var blend = TestMeshes.Grid(2, false);
            blend.AddBlendShapeFrame("shape", 100f, new Vector3[blend.vertexCount], null, null);
            StringAssert.Contains("blend shape", LowPolyConverter.GetMeshIssue(blend));

            var skinned = TestMeshes.Grid(2, false);
            skinned.bindposes = new[] { Matrix4x4.identity };
            var weights = new BoneWeight[skinned.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i].weight0 = 1f;
            skinned.boneWeights = weights;
            StringAssert.Contains("xương", LowPolyConverter.GetMeshIssue(skinned));

            var lines = new Mesh();
            lines.SetVertices(new List<Vector3> { Vector3.zero, Vector3.one });
            lines.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
            StringAssert.Contains("topology", LowPolyConverter.GetMeshIssue(lines));

            var locked = TestMeshes.Grid(2, false);
            locked.UploadMeshData(true);
            StringAssert.Contains("không đọc được", LowPolyConverter.GetMeshIssue(locked));

            Assert.IsNull(LowPolyConverter.GetMeshIssue(TestMeshes.Grid(2, false)));
        }
    }
}
