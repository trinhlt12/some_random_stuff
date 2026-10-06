using UnityEngine;

namespace CottonDoll.Editor
{
    /// <summary>Signed distance helpers (negative inside). Distances are in the doll's normalised units.</summary>
    public static class Sdf
    {
        /// <summary>Polynomial smooth minimum: blends two shapes with a soft fillet of size k.</summary>
        public static float Smin(float a, float b, float k)
        {
            if (k <= 0f) return Mathf.Min(a, b);
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Min(a, b) - h * h * k * 0.25f;
        }

        public static float Smax(float a, float b, float k) => -Smin(-a, -b, k);

        /// <summary>Ellipsoid (close approximation, exact on the axes, smooth everywhere).</summary>
        public static float Ellipsoid(Vector3 p, Vector3 center, Vector3 radii)
        {
            float x = (p.x - center.x) / radii.x, y = (p.y - center.y) / radii.y, z = (p.z - center.z) / radii.z;
            float k0 = Mathf.Sqrt(x * x + y * y + z * z);
            float x2 = x / radii.x, y2 = y / radii.y, z2 = z / radii.z;
            float k1 = Mathf.Sqrt(x2 * x2 + y2 * y2 + z2 * z2);
            if (k1 < 1e-9f) return -Mathf.Min(radii.x, Mathf.Min(radii.y, radii.z));
            return k0 * (k0 - 1f) / k1;
        }

        /// <summary>Exact capsule with different radii at each end (Inigo Quilez's round cone).</summary>
        public static float RoundCone(Vector3 p, Vector3 a, Vector3 b, float r1, float r2)
        {
            Vector3 ba = b - a;
            float l2 = Vector3.Dot(ba, ba);
            float rr = r1 - r2;
            float a2 = l2 - rr * rr;
            float il2 = 1f / l2;
            Vector3 pa = p - a;
            float y = Vector3.Dot(pa, ba);
            float z = y - l2;
            Vector3 q = pa * l2 - ba * y;
            float x2 = Vector3.Dot(q, q);
            float y2 = y * y * l2;
            float z2 = z * z * l2;
            float k = Sign(rr) * rr * rr * x2;
            if (Sign(z) * a2 * z2 > k) return Mathf.Sqrt(x2 + z2) * il2 - r2;
            if (Sign(y) * a2 * y2 < k) return Mathf.Sqrt(x2 + y2) * il2 - r1;
            return (Mathf.Sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
        }

        static float Sign(float v) => v > 0f ? 1f : v < 0f ? -1f : 0f;

        /// <summary>Signed distance to the plane through <paramref name="point"/>; positive on the <paramref name="normal"/> side.</summary>
        public static float Plane(Vector3 p, Vector3 point, Vector3 normal) => Vector3.Dot(p - point, normal);
    }
}
