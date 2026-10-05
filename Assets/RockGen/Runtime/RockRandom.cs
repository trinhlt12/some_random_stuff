using UnityEngine;

namespace RockGen
{
    /// <summary>
    /// Small deterministic SplitMix64 generator. Independent of UnityEngine.Random, so generating a
    /// rock never changes the global random state, and the same seed gives the same sequence.
    /// </summary>
    public struct RockRandom
    {
        ulong state;

        public RockRandom(int seed, ulong stream)
        {
            state = ((ulong)(uint)seed << 32) ^ (stream * 0x9E3779B97F4A7C15UL) ^ 0xD1B54A32D192ED03UL;
        }

        public ulong NextULong()
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform float in [0, 1).</summary>
        public float Value() => (NextULong() >> 40) * (1f / 16777216f);

        public float Range(float min, float max) => min + (max - min) * Value();

        public Vector3 OnUnitSphere()
        {
            float z = Range(-1f, 1f);
            float a = Range(0f, 2f * Mathf.PI);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(a), z, r * Mathf.Sin(a));
        }
    }
}
