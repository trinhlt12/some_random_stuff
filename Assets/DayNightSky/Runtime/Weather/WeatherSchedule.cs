using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayNightSky
{
    /// <summary>Everything the sky needs for one moment of weather.</summary>
    public struct WeatherSample
    {
        public float coverage, softness, overcast, windSpeed, windDirection, rain;

        public CloudWeather Clouds => new CloudWeather(coverage, softness, overcast, windSpeed, rain);

        /// <summary>Linear blend (callers apply easing); wind direction takes the short way round.</summary>
        public static WeatherSample Lerp(in WeatherSample a, in WeatherSample b, float t) => new WeatherSample
        {
            coverage = Mathf.Lerp(a.coverage, b.coverage, t),
            softness = Mathf.Lerp(a.softness, b.softness, t),
            overcast = Mathf.Lerp(a.overcast, b.overcast, t),
            windSpeed = Mathf.Lerp(a.windSpeed, b.windSpeed, t),
            windDirection = WeatherMath.LerpAngle(a.windDirection, b.windDirection, t),
            rain = Mathf.Lerp(a.rain, b.rain, t),
        };
    }

    /// <summary>
    /// One step of the schedule: a transition from one preset to another followed by a hold.
    /// Times are schedule hours (game hours since the schedule's start).
    /// </summary>
    [Serializable]
    public struct WeatherSegment
    {
        public long index;
        public int fromPreset, toPreset;          // indices into the schedule's presets
        public double transitionStartHours;
        public double transitionHours;
        public double holdHours;
        // Per-visit random variation, already resolved (the "from" values repeat the previous segment).
        public float fromWindDirection, toWindDirection;
        public float fromCoverageOffset, toCoverageOffset;

        public double TransitionEndHours => transitionStartHours + transitionHours;
        public double EndHours => transitionStartHours + transitionHours + holdHours;

        /// <summary>0..1 (un-eased) transition progress at schedule time t; 1 during the hold.</summary>
        public float ProgressAt(double t) => transitionHours > 0.0
            ? (float)Math.Min(1.0, Math.Max(0.0, (t - transitionStartHours) / transitionHours))
            : 1f;
    }

    /// <summary>
    /// Deterministic, seeded weather schedule. Pure C#: no MonoBehaviour, material or UI. Same seed,
    /// same config and same <see cref="AlgorithmVersion"/> always give the same segments, no matter how
    /// often or in which steps it is queried. Each segment draws from its own random stream derived from
    /// (seed, segment index), so batching and buffer size never change the result.
    /// Keeps a bounded ring buffer; old segments are dropped, and seeking back before the buffer
    /// regenerates from the start (identically).
    /// </summary>
    public sealed class WeatherSchedule
    {
        public const int AlgorithmVersion = 1;
        public const int Capacity = 32;
        const int BatchSize = 8;
        const int MinimumLookAhead = 2;                 // current + two forecast segments
        const long MaxSegmentsPerQuery = 2_000_000;     // guard against absurd seeks

        struct Preset
        {
            public WeatherSample sample;
            public float windJitter, coverageJitter;
        }

        readonly Preset[] presets;
        readonly string[] ids, names;
        readonly int[][] nextPreset;
        readonly float[][] nextCumulativeWeight;
        readonly float[] holdMin, holdMax;
        readonly double transitionMinHours, transitionMaxHours;
        readonly int startPreset;

        readonly WeatherSegment[] ring = new WeatherSegment[Capacity];
        int head, count;
        long nextIndex;
        int cursor;   // logical index of the last located segment

        public int Seed { get; }
        public int PresetCount => presets.Length;
        /// <summary>Total segments generated since the last reset (diagnostics/tests).</summary>
        public long GeneratedCount { get; private set; }
        /// <summary>Segments currently held in memory (never more than <see cref="Capacity"/>).</summary>
        public int BufferedCount => count;

        WeatherSchedule(WeatherScheduleConfig config, int seed)
        {
            Seed = seed;
            int n = config.presets.Length;
            presets = new Preset[n];
            ids = new string[n];
            names = new string[n];
            var lookup = new Dictionary<string, int>(n);
            for (int i = 0; i < n; i++)
            {
                var p = config.presets[i];
                lookup[p.id] = i;
                ids[i] = p.id;
                names[i] = string.IsNullOrWhiteSpace(p.displayName) ? p.id : p.displayName;
                presets[i] = new Preset
                {
                    sample = new WeatherSample
                    {
                        coverage = Mathf.Clamp01(p.cloudCoverage),
                        softness = Mathf.Clamp01(p.cloudSoftness),
                        overcast = Mathf.Clamp01(p.overcastAmount),
                        windSpeed = Mathf.Max(0f, p.windSpeed),
                        windDirection = WeatherMath.WrapAngle(p.windDirection),
                        rain = Mathf.Clamp01(p.rainIntensity),
                    },
                    windJitter = Mathf.Max(0f, p.windDirectionJitter),
                    coverageJitter = Mathf.Max(0f, p.coverageJitter),
                };
            }

            nextPreset = new int[n][];
            nextCumulativeWeight = new float[n][];
            holdMin = new float[n];
            holdMax = new float[n];
            foreach (var r in config.rules)
            {
                int from = lookup[r.fromPresetId];
                holdMin[from] = r.minHoldHours;
                holdMax[from] = r.maxHoldHours;
                var targets = new List<int>();
                var cumulative = new List<float>();
                float total = 0f;
                foreach (var o in r.next)
                {
                    if (o == null || !(o.weight > 0f)) continue;
                    total += o.weight;
                    targets.Add(lookup[o.presetId]);
                    cumulative.Add(total);
                }
                nextPreset[from] = targets.ToArray();
                nextCumulativeWeight[from] = cumulative.ToArray();
            }

            startPreset = lookup[config.startPresetId];
            transitionMinHours = config.minTransitionMinutes / 60.0;
            transitionMaxHours = config.maxTransitionMinutes / 60.0;
            Reset();
        }

        /// <summary>Validates the config and builds a schedule. Returns false (with messages) when invalid.</summary>
        public static bool TryCreate(WeatherScheduleConfig config, int seed, List<string> errors, out WeatherSchedule schedule)
        {
            schedule = null;
            if (config == null)
            {
                errors.Add("No weather schedule config assigned.");
                return false;
            }
            if (!config.Validate(errors)) return false;
            schedule = new WeatherSchedule(config, seed);
            return true;
        }

        public string GetPresetId(int preset) => ids[preset];
        public string GetPresetName(int preset) => names[preset];
        public int FindPreset(string id) => Array.IndexOf(ids, id);

        /// <summary>The preset's own values (default wind, no per-visit variation).</summary>
        public WeatherSample GetPresetSample(int preset) => presets[preset].sample;

        /// <summary>The preset with a segment's per-visit variation applied.</summary>
        public WeatherSample GetPresetSample(int preset, float coverageOffset, float windDirection)
        {
            var s = presets[preset].sample;
            s.coverage = Mathf.Clamp01(s.coverage + coverageOffset);
            s.windDirection = windDirection;
            return s;
        }

        /// <summary>Drops every buffered segment and starts again from segment 0.</summary>
        public void Reset()
        {
            head = count = cursor = 0;
            nextIndex = 0;
            GeneratedCount = 0;
        }

        WeatherSegment Get(int logical) => ring[(head + logical) % Capacity];

        /// <summary>
        /// Finds the segment that is active at schedule time <paramref name="hours"/>, generating more
        /// segments if needed. Times before 0 count as 0. Cost does not depend on frame rate: one call
        /// can skip any number of segments.
        /// </summary>
        public WeatherSegment SegmentAt(double hours)
        {
            int i = Locate(hours);
            return Get(i);
        }

        /// <summary>The segment <paramref name="ahead"/> steps after the one active at <paramref name="hours"/> (0 = current, max 2).</summary>
        public WeatherSegment SegmentAfter(double hours, int ahead)
        {
            ahead = Mathf.Clamp(ahead, 0, MinimumLookAhead);
            int i = Locate(hours);
            return Get(i + ahead);
        }

        /// <summary>The weather at schedule time <paramref name="hours"/> (eased like every other transition).</summary>
        public WeatherSample Evaluate(double hours) => Evaluate(hours, out _, out _);

        public WeatherSample Evaluate(double hours, out WeatherSegment segment, out float progress)
        {
            segment = SegmentAt(hours);
            progress = segment.ProgressAt(hours);
            var from = GetPresetSample(segment.fromPreset, segment.fromCoverageOffset, segment.fromWindDirection);
            var to = GetPresetSample(segment.toPreset, segment.toCoverageOffset, segment.toWindDirection);
            return WeatherSample.Lerp(from, to, WeatherMath.Ease(progress));
        }

        int Locate(double hours)
        {
            if (!(hours > 0.0)) hours = 0.0;
            if (count == 0 || hours < Get(0).transitionStartHours)
            {
                Reset();
                Generate(1);
            }

            // Generate (and drop old segments) until the buffer reaches the requested time.
            long guard = 0;
            while (Get(count - 1).EndHours <= hours)
            {
                Generate(BatchSize);
                guard += BatchSize;
                if (guard > MaxSegmentsPerQuery)
                {
                    Debug.LogError($"WeatherSchedule: gave up seeking to {hours:0.##} h after {guard} segments.");
                    break;
                }
            }

            int i = Mathf.Clamp(cursor, 0, count - 1);
            if (Get(i).transitionStartHours > hours) i = 0;
            while (i < count - 1 && Get(i).EndHours <= hours) i++;

            // Keep the forecast available; the newly generated segments may push old ones out.
            if (count - 1 - i < MinimumLookAhead)
            {
                long first = Get(i).index;
                Generate(BatchSize);
                i = (int)(first - Get(0).index);
            }
            cursor = i;
            return i;
        }

        void Generate(int segments)
        {
            for (int s = 0; s < segments; s++)
            {
                if (count == Capacity)
                {
                    head = (head + 1) % Capacity;
                    count--;
                    cursor = Math.Max(0, cursor - 1);
                }
                var seg = nextIndex == 0 ? First() : Next(Get(count - 1));
                ring[(head + count) % Capacity] = seg;
                count++;
                nextIndex++;
                GeneratedCount++;
            }
        }

        WeatherSegment First()
        {
            var rng = new Rng(Seed, 0);
            var p = presets[startPreset];
            float hold = Mathf.Lerp(holdMin[startPreset], holdMax[startPreset], rng.NextFloat());
            float wind = WeatherMath.WrapAngle(p.sample.windDirection + rng.NextSigned() * p.windJitter);
            float cover = rng.NextSigned() * p.coverageJitter;
            return new WeatherSegment
            {
                index = 0,
                fromPreset = startPreset,
                toPreset = startPreset,
                transitionStartHours = 0.0,
                transitionHours = 0.0,
                holdHours = hold,
                fromWindDirection = wind,
                toWindDirection = wind,
                fromCoverageOffset = cover,
                toCoverageOffset = cover,
            };
        }

        WeatherSegment Next(in WeatherSegment previous)
        {
            long index = previous.index + 1;
            var rng = new Rng(Seed, index);
            // Fixed draw order; changing it requires bumping AlgorithmVersion.
            int from = previous.toPreset;
            int to = Pick(from, rng.NextFloat());
            double transition = transitionMinHours + (transitionMaxHours - transitionMinHours) * rng.NextFloat();
            float hold = Mathf.Lerp(holdMin[to], holdMax[to], rng.NextFloat());
            var p = presets[to];
            float wind = WeatherMath.WrapAngle(p.sample.windDirection + rng.NextSigned() * p.windJitter);
            float cover = rng.NextSigned() * p.coverageJitter;
            return new WeatherSegment
            {
                index = index,
                fromPreset = from,
                toPreset = to,
                transitionStartHours = previous.EndHours,
                transitionHours = transition,
                holdHours = hold,
                fromWindDirection = previous.toWindDirection,
                toWindDirection = wind,
                fromCoverageOffset = previous.toCoverageOffset,
                toCoverageOffset = cover,
            };
        }

        int Pick(int from, float u)
        {
            var targets = nextPreset[from];
            var cumulative = nextCumulativeWeight[from];
            float r = u * cumulative[cumulative.Length - 1];
            for (int i = 0; i < targets.Length; i++)
                if (r < cumulative[i]) return targets[i];
            return targets[targets.Length - 1];
        }

        /// <summary>SplitMix64 stream keyed by (seed, segment index, algorithm version). Never touches UnityEngine.Random.</summary>
        struct Rng
        {
            ulong state;

            public Rng(int seed, long index)
            {
                state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ (ulong)index * 0xD1B54A32D192ED03UL ^ (ulong)AlgorithmVersion * 0x8CB92BA72F3D8DD7UL;
                NextULong();
            }

            ulong NextULong()
            {
                ulong z = state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            /// <summary>[0, 1)</summary>
            public float NextFloat() => (NextULong() >> 40) / (float)(1UL << 24);

            /// <summary>[-1, 1)</summary>
            public float NextSigned() => NextFloat() * 2f - 1f;
        }
    }
}
