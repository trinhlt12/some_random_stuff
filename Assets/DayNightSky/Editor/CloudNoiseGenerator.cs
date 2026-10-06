using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    /// <summary>
    /// Builds the seamless cloud noise used by the sky shader and saves it as a PNG asset.
    /// R = large, soft cloud shapes (round blobs whose size and spacing vary over the tile),
    /// G = small billowy detail. Both channels are histogram-equalised, so a threshold of
    /// 1 - coverage covers roughly that fraction of the sky. Generated once in the Editor, never at runtime.
    /// </summary>
    public static class CloudNoiseGenerator
    {
        public const string TexturePath = "Assets/DayNightSky/Textures/CloudNoise.png";
        public const int DefaultSize = 256;
        public const int DefaultSeed = 1337;
        const string SeedPrefix = "seed=";

        /// <summary>Returns the noise texture, generating it with the default seed if it is missing.</summary>
        public static Texture2D GetOrCreateTexture()
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            return tex != null ? tex : Regenerate(DefaultSeed);
        }

        /// <summary>Seed the current asset was generated with (stored in its import settings).</summary>
        public static int GetSeed()
        {
            var importer = AssetImporter.GetAtPath(TexturePath);
            if (importer != null && importer.userData != null && importer.userData.StartsWith(SeedPrefix) &&
                int.TryParse(importer.userData.Substring(SeedPrefix.Length), out int seed))
                return seed;
            return DefaultSeed;
        }

        [MenuItem("Tools/Day Night Sky/Regenerate Cloud Noise")]
        static void RegenerateMenu() => Regenerate(GetSeed());

        /// <summary>Writes the PNG (same path, so materials keep their reference) and configures its import.</summary>
        public static Texture2D Regenerate(int seed, int size = DefaultSize)
        {
            var pixels = Generate(size, seed);
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false, true);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false);
                Directory.CreateDirectory(Path.GetDirectoryName(TexturePath));
                File.WriteAllBytes(TexturePath, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }

            AssetDatabase.ImportAsset(TexturePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(TexturePath);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;           // data, not colour
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = true;          // the dome mapping minifies strongly towards the horizon
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 1;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.userData = SeedPrefix + seed;
            // Two 8-bit channels, uncompressed: block compression would add visible artefacts to
            // the soft gradients. 256x256 RG8 with mips is about 170 KB on every platform.
            foreach (var platform in new[] { "Android", "iPhone", "Standalone", "WebGL" })
            {
                var ps = importer.GetPlatformTextureSettings(platform);
                ps.overridden = true;
                ps.format = TextureImporterFormat.RG16;
                ps.maxTextureSize = Mathf.Max(size, 32);
                importer.SetPlatformTextureSettings(ps);
            }
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        }

        /// <summary>Pure generation, deterministic for a given size and seed. Tiles seamlessly.</summary>
        public static Color32[] Generate(int size, int seed)
        {
            if (size < 16) throw new ArgumentOutOfRangeException(nameof(size));
            int count = size * size;
            var shape = new float[count];
            var detail = new float[count];

            uint s = (uint)seed * 747796405u + 2891336453u;
            for (int y = 0; y < size; y++)
            {
                float v = (y + 0.5f) / size;
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;

                    // Clusters: very low-frequency gradient noise decides where clouds gather and how
                    // big they get, leaving open sky of varying size in between.
                    float cluster = 0.5f + 0.5f * (0.7f * Perlin(u, v, 2, s) + 0.3f * Perlin(u, v, 4, s + 1u));
                    // Rounded puffs at two sizes; the smaller ones only add bumps to the bigger ones.
                    float puffs = 0.7f * Puffs(u, v, 5, s + 2u) + 0.3f * Puffs(u, v, 10, s + 3u);
                    shape[y * size + x] = puffs * Mathf.Lerp(0.25f, 1f, Smooth(cluster)) + 0.35f * cluster;

                    detail[y * size + x] = 0.65f * Puffs(u, v, 6, s + 4u) + 0.35f * Puffs(u, v, 12, s + 5u);
                }
            }

            Equalise(shape);
            Equalise(detail);

            var pixels = new Color32[count];
            for (int i = 0; i < count; i++)
                pixels[i] = new Color32((byte)Mathf.RoundToInt(shape[i] * 255f), (byte)Mathf.RoundToInt(detail[i] * 255f), 0, 255);
            return pixels;
        }

        static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Inverted cellular noise: 1 at a jittered point, falling smoothly to 0 one cell away. Uses a
        /// smooth minimum of the distances, so the borders between cells are rounded valleys instead
        /// of sharp creases (which read as cracks or marble veins in an overcast sky).
        /// </summary>
        static float Puffs(float u, float v, int cells, uint seed)
        {
            const float k = 10f;
            float px = u * cells, py = v * cells;
            int cx = Mathf.FloorToInt(px), cy = Mathf.FloorToInt(py);
            float sum = 0f;
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int gx = cx + ox, gy = cy + oy;
                    uint h = Hash(Mod(gx, cells), Mod(gy, cells), seed);
                    float fx = gx + 0.15f + 0.7f * (h & 0xffff) / 65535f;
                    float fy = gy + 0.15f + 0.7f * (h >> 16) / 65535f;
                    float dx = px - fx, dy = py - fy;
                    sum += Mathf.Exp(-k * Mathf.Sqrt(dx * dx + dy * dy));
                }
            return Smooth(1f + Mathf.Log(sum) / k);
        }

        /// <summary>Tileable gradient noise in about [-1, 1].</summary>
        static float Perlin(float u, float v, int period, uint seed)
        {
            float px = u * period, py = v * period;
            int x0 = Mathf.FloorToInt(px), y0 = Mathf.FloorToInt(py);
            float fx = px - x0, fy = py - y0;
            float n00 = Grad(x0, y0, fx, fy, period, seed);
            float n10 = Grad(x0 + 1, y0, fx - 1f, fy, period, seed);
            float n01 = Grad(x0, y0 + 1, fx, fy - 1f, period, seed);
            float n11 = Grad(x0 + 1, y0 + 1, fx - 1f, fy - 1f, period, seed);
            float sx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
            float sy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
            return 1.41f * Mathf.Lerp(Mathf.Lerp(n00, n10, sx), Mathf.Lerp(n01, n11, sx), sy);
        }

        static float Grad(int x, int y, float dx, float dy, int period, uint seed)
        {
            float a = Hash(Mod(x, period), Mod(y, period), seed) / 4294967295f * 2f * Mathf.PI;
            return Mathf.Cos(a) * dx + Mathf.Sin(a) * dy;
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;

        static uint Hash(int x, int y, uint seed)
        {
            uint h = seed ^ ((uint)x * 0x27d4eb2du) ^ ((uint)y * 0x165667b1u);
            h ^= h >> 15; h *= 0x85ebca6bu;
            h ^= h >> 13; h *= 0xc2b2ae35u;
            h ^= h >> 16;
            return h;
        }

        /// <summary>Monotonic remap to a flat histogram in [0, 1]: keeps every contour, evens out coverage.</summary>
        static void Equalise(float[] values)
        {
            int n = values.Length;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            var keys = (float[])values.Clone();
            Array.Sort(keys, order);
            for (int rank = 0; rank < n; rank++) values[order[rank]] = rank / (float)(n - 1);
        }
    }
}
