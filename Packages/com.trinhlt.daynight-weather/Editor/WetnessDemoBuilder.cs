using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DayNightSky.Editor
{
    /// <summary>
    /// Builds a small primitive test area for rain/wetness work: a wide ground, a stylised rock made of
    /// primitives, an object meant to get only lightly wet and one meant to stay dry. Materials and the
    /// prefab are saved as assets; the scene gets one prefab instance under "WeatherWetnessDemo".
    ///
    /// Safe to run again: materials are updated in place (same assets), the prefab is rebuilt at the same
    /// path (same GUID, so instances update), and only a root named WeatherWetnessDemo is touched.
    /// Lighting comes from the day/night system's Directional Light; nothing else is created.
    /// Materials use the sample DayNightSky/Wet Lit shader with per-material wetness (exposure, darkening,
    /// smoothness boost, or none); WetnessController drives the shared value.
    /// </summary>
    public static class WetnessDemoBuilder
    {
        public const string RootName = "WeatherWetnessDemo";
        public static readonly string Folder = PackagePaths.Root + "/WetnessDemo";
        public static readonly string PrefabPath = Folder + "/WeatherWetnessDemo.prefab";
        /// <summary>Sample lit shader with weather wetness (only used by the demo materials).</summary>
        public const string LitShader = "DayNightSky/Wet Lit";

        /// <summary>Distance from the camera to the row of objects (m).</summary>
        public const float DemoDistance = 9f;

        struct MaterialSpec
        {
            public string name;
            public Color color;
            public float smoothness;
            // Wetness response (visual only): exposure 0..1, darkening, smoothness boost; receive = false opts out.
            public float exposure, darkening, wetSmoothness;
            public bool receive;
            public MaterialSpec(string name, Color color, float smoothness, float exposure, float darkening, float wetSmoothness, bool receive = true)
            {
                this.name = name; this.color = color; this.smoothness = smoothness;
                this.exposure = exposure; this.darkening = darkening; this.wetSmoothness = wetSmoothness; this.receive = receive;
            }
        }

        // Distinct colours and gloss levels so dry/wet changes are easy to compare. Ground and rock get fully
        // wet, the wood only lightly (low exposure), the painted lantern never (receive off).
        static readonly MaterialSpec Ground = new MaterialSpec("WetDemo_Ground_Wet", new Color(0.55f, 0.62f, 0.45f), 0.12f, 1f, 0.45f, 0.55f);
        static readonly MaterialSpec Rock = new MaterialSpec("WetDemo_Rock_Wet", new Color(0.62f, 0.58f, 0.55f), 0.2f, 1f, 0.4f, 0.5f);
        static readonly MaterialSpec RockMoss = new MaterialSpec("WetDemo_RockMoss_Wet", new Color(0.45f, 0.58f, 0.40f), 0.15f, 1f, 0.3f, 0.3f);
        static readonly MaterialSpec LightWet = new MaterialSpec("WetDemo_Wood_LightWet", new Color(0.78f, 0.56f, 0.36f), 0.3f, 0.4f, 0.3f, 0.35f);
        static readonly MaterialSpec NoWet = new MaterialSpec("WetDemo_Painted_NoWet", new Color(0.55f, 0.70f, 0.92f), 0.55f, 0f, 0f, 0f, receive: false);

        [MenuItem("Tools/Day Night Sky/Create or Update Wetness Demo")]
        public static void CreateOrUpdateMenu()
        {
            var root = CreateOrUpdate(SceneManagerActive());
            if (root != null)
            {
                Selection.activeGameObject = root;
                EditorGUIUtility.PingObject(root);
            }
        }

        static UnityEngine.SceneManagement.Scene SceneManagerActive() => UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        /// <summary>Updates the assets and makes sure the scene has exactly one demo instance. Returns it.</summary>
        public static GameObject CreateOrUpdate(UnityEngine.SceneManagement.Scene scene)
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("Create or Update Wetness Demo only works outside Play Mode (it saves assets and the scene).");
                return null;
            }
            var prefab = BuildPrefab();

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create or Update Wetness Demo");

            GameObject keep = null;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name != RootName) continue;
                bool ours = PrefabUtility.GetCorrespondingObjectFromSource(go) == prefab &&
                            PrefabUtility.IsAnyPrefabInstanceRoot(go);
                if (ours && keep == null)
                {
                    keep = go;
                    continue;
                }
                // A duplicate or an old non-prefab copy of the demo: replace it, keeping its placement.
                if (keep == null)
                {
                    keep = Instantiate(prefab, scene, go.transform.position, go.transform.rotation);
                }
                Undo.DestroyObjectImmediate(go);
            }

            if (keep == null)
            {
                Place(out var position, out var rotation);
                keep = Instantiate(prefab, scene, position, rotation);
            }
            else
            {
                // Pick up the rebuilt prefab content; the instance keeps its own placement.
                var t = keep.transform;
                Vector3 position = t.position;
                Quaternion rotation = t.rotation;
                PrefabUtility.RevertPrefabInstance(keep, InteractionMode.UserAction);
                Undo.RecordObject(t, "Update Wetness Demo");
                t.SetPositionAndRotation(position, rotation);
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);
            return keep;
        }

        static GameObject Instantiate(GameObject prefab, UnityEngine.SceneManagement.Scene scene, Vector3 position, Quaternion rotation)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            go.transform.SetPositionAndRotation(position, rotation);
            Undo.RegisterCreatedObjectUndo(go, "Create Wetness Demo");
            return go;
        }

        /// <summary>On the ground, Demo Distance ahead of the main camera, facing it.</summary>
        static void Place(out Vector3 position, out Quaternion rotation)
        {
            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            position = new Vector3(camPos.x, 0f, camPos.z) + fwd * DemoDistance;
            rotation = Quaternion.LookRotation(-fwd, Vector3.up);   // the row faces the camera
        }

        // ------------------------------------------------------------------------------------
        // Assets

        static Material GetOrUpdateMaterial(MaterialSpec spec)
        {
            string path = $"{Folder}/{spec.name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                System.IO.Directory.CreateDirectory(Folder);
                m = new Material(Shader.Find(LitShader)) { name = spec.name };
                AssetDatabase.CreateAsset(m, path);
            }
            else
            {
                Undo.RecordObject(m, "Update Wetness Demo Material");
                if (m.shader == null || m.shader.name != LitShader) m.shader = Shader.Find(LitShader);
            }
            m.SetColor("_BaseColor", spec.color);
            m.SetFloat("_Smoothness", spec.smoothness);
            m.SetFloat("_ReceiveWetness", spec.receive ? 1f : 0f);
            m.SetFloat("_WetExposure", spec.exposure);
            m.SetFloat("_WetDarkening", spec.darkening);
            m.SetFloat("_WetSmoothness", spec.wetSmoothness);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Rebuilds the demo prefab at its fixed path (GUID kept) and returns it.</summary>
        public static GameObject BuildPrefab()
        {
            var ground = GetOrUpdateMaterial(Ground);
            var rock = GetOrUpdateMaterial(Rock);
            var moss = GetOrUpdateMaterial(RockMoss);
            var wood = GetOrUpdateMaterial(LightWet);
            var painted = GetOrUpdateMaterial(NoWet);
            AssetDatabase.SaveAssets();

            var root = new GameObject(RootName);
            try
            {
                // 80 × 80 m, wide enough to reach under the camera (the root is ~9 m in front of it, local +Z faces it).
                Part(root, "Ground (wet)", PrimitiveType.Plane, ground, new Vector3(0f, 0f, 2f), Vector3.zero, new Vector3(8f, 1f, 8f));

                // One side: stylised rock from rotated, flattened primitives.
                var rockGroup = new GameObject("Rock (wet)");
                rockGroup.transform.SetParent(root.transform, false);
                rockGroup.transform.localPosition = new Vector3(-3.5f, 0f, 0f);
                Part(rockGroup, "Rock Base", PrimitiveType.Cube, rock, new Vector3(0f, 0.45f, 0f), new Vector3(8f, 25f, 6f), new Vector3(1.8f, 0.9f, 1.4f));
                Part(rockGroup, "Rock Top", PrimitiveType.Cube, rock, new Vector3(0.2f, 1.05f, 0.1f), new Vector3(-12f, -20f, 15f), new Vector3(1.1f, 0.7f, 0.9f));
                Part(rockGroup, "Rock Side", PrimitiveType.Sphere, rock, new Vector3(-0.9f, 0.3f, -0.3f), new Vector3(0f, 30f, 0f), new Vector3(0.9f, 0.6f, 0.8f));
                Part(rockGroup, "Moss Cap", PrimitiveType.Sphere, moss, new Vector3(0.15f, 1.47f, 0.1f), new Vector3(-12f, -20f, 15f), new Vector3(0.85f, 0.25f, 0.7f));

                // Centre: wooden post and bench, meant to get only lightly wet.
                var bench = new GameObject("Bench (light wet)");
                bench.transform.SetParent(root.transform, false);
                Part(bench, "Seat", PrimitiveType.Cube, wood, new Vector3(0f, 0.55f, 0f), Vector3.zero, new Vector3(1.8f, 0.12f, 0.55f));
                Part(bench, "Leg L", PrimitiveType.Cube, wood, new Vector3(-0.7f, 0.25f, 0f), Vector3.zero, new Vector3(0.14f, 0.5f, 0.45f));
                Part(bench, "Leg R", PrimitiveType.Cube, wood, new Vector3(0.7f, 0.25f, 0f), Vector3.zero, new Vector3(0.14f, 0.5f, 0.45f));
                Part(bench, "Post", PrimitiveType.Cylinder, wood, new Vector3(1.2f, 0.8f, 0.3f), Vector3.zero, new Vector3(0.18f, 0.8f, 0.18f));

                // Other side: glossy painted lantern, meant to never get wet.
                var lantern = new GameObject("Lantern (no wet)");
                lantern.transform.SetParent(root.transform, false);
                lantern.transform.localPosition = new Vector3(3.5f, 0f, 0f);
                Part(lantern, "Body", PrimitiveType.Capsule, painted, new Vector3(0f, 0.75f, 0f), Vector3.zero, new Vector3(0.7f, 0.75f, 0.7f));
                Part(lantern, "Ball", PrimitiveType.Sphere, painted, new Vector3(0f, 1.75f, 0f), Vector3.zero, new Vector3(0.55f, 0.55f, 0.55f));

                System.IO.Directory.CreateDirectory(Folder);
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void Part(GameObject parent, string name, PrimitiveType type, Material material, Vector3 position, Vector3 euler, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = position;
            go.transform.localEulerAngles = euler;
            go.transform.localScale = scale;
            // Visual demo only: no physics needed.
            Object.DestroyImmediate(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
        }
    }
}
