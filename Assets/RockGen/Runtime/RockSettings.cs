using System;
using UnityEngine;

namespace RockGen
{
    /// <summary>Input of <see cref="RockGenerator"/>. Every field is clamped before use.</summary>
    [Serializable]
    public struct RockSettings
    {
        public const float MinSize = 0.05f;
        public const float MaxSize = 50f;
        public const int MinTriangles = 20;
        public const int MaxTriangles = 600;

        public int seed;

        [Tooltip("Final bounding box size in Unity units (X width, Y height, Z depth).")]
        public Vector3 size;

        [Tooltip("0 = smooth pebble, 1 = chipped, faceted rock.")]
        [Range(0f, 1f)] public float roughness;

        [Tooltip("0 = symmetric blob, 1 = lopsided and leaning.")]
        [Range(0f, 1f)] public float asymmetry;

        [Tooltip("0 = round bottom, 1 = wide flat base for sitting on the ground.")]
        [Range(0f, 1f)] public float bottomFlatten;

        [Tooltip("Upper bound for the triangle count. The convex result can use fewer.")]
        public int triangleBudget;

        public static RockSettings Default => new RockSettings
        {
            seed = 1,
            size = new Vector3(1f, 0.7f, 0.9f),
            roughness = 0.5f,
            asymmetry = 0.5f,
            bottomFlatten = 0.6f,
            triangleBudget = 80,
        };

        /// <summary>Returns a copy with every value inside its valid range (NaN/Infinity replaced).</summary>
        public RockSettings Clamped()
        {
            var d = Default;
            var c = this;
            c.size = new Vector3(ClampSize(size.x, d.size.x), ClampSize(size.y, d.size.y), ClampSize(size.z, d.size.z));
            c.roughness = Clamp01(roughness, d.roughness);
            c.asymmetry = Clamp01(asymmetry, d.asymmetry);
            c.bottomFlatten = Clamp01(bottomFlatten, d.bottomFlatten);
            c.triangleBudget = Mathf.Clamp(triangleBudget, MinTriangles, MaxTriangles);
            return c;
        }

        static float ClampSize(float v, float fallback) =>
            float.IsNaN(v) || float.IsInfinity(v) ? fallback : Mathf.Clamp(v, MinSize, MaxSize);

        static float Clamp01(float v, float fallback) =>
            float.IsNaN(v) || float.IsInfinity(v) ? fallback : Mathf.Clamp01(v);
    }
}
