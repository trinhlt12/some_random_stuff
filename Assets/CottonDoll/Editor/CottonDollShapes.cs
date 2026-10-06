using System.Collections.Generic;
using UnityEngine;

namespace CottonDoll.Editor
{
    /// <summary>
    /// The doll as signed distance fields, in normalised units (height = 1, soles at y = 0, facing +Z).
    /// Proportions follow the reference image where it shows them (head ≈ 31% of the height, short thin
    /// neck, small mitten hands, slim legs with a small gap, low round shoes); the torso, shoulders and
    /// hips are hidden by the coat in the image, so their round shape is a design guess.
    /// Every field is mirror-symmetric in X (evaluated on |x|).
    /// </summary>
    public sealed class CottonDollShapes
    {
        // Layout (normalised).
        public readonly Vector3 headC, headR;
        public readonly float chinY, shoulderY, hipY, crotchY, ankleY;
        public readonly Vector3 chestC, chestR, bellyC, bellyR;
        public readonly Vector3 neckTop, neckBottom;
        public readonly Vector3 shoulder, shoulderInner, wrist, armDir;
        public readonly float armLen, armR1, armR2;
        public readonly Vector3 handC, handR;
        public readonly Vector3 hip, ankle, legDir;
        public readonly float legLen, legR1, legR2;
        public readonly Vector3 footC, footR;
        public readonly Vector3 earC, earR;
        public readonly bool ears;

        // Outfit / hair layout.
        public readonly float gap, hemY, waistY, hairBottomY, hairVol, bangBottomY, hairlineY;
        public readonly Vector3 sleeveEnd, shortsEnd;
        public readonly float eyeY, mouthY;

        readonly CottonDollSettings s;
        readonly Quaternion[] lockRot, lockInv;
        readonly Vector3[] lockC;
        readonly Vector3 lockR;

        const float ArmAngleDeg = 20f;   // light A-pose

        public CottonDollShapes(CottonDollSettings settings)
        {
            s = settings;

            // Head: top of the head at y = 1.
            float r = 0.17f * s.headSize;
            headR = new Vector3(r * (1f + 0.5f * s.headFlatness), r * (1f - s.headFlatness), r * 0.94f);
            headC = new Vector3(0f, 1f - headR.y, 0f);
            chinY = headC.y - headR.y;
            neckTop = new Vector3(0f, chinY + 0.045f, -0.012f);
            neckBottom = new Vector3(0f, chinY - 0.05f, -0.006f);
            shoulderY = chinY - 0.045f;

            // Legs and torso share the rest of the height.
            ankleY = 0.06f;
            legLen = 0.255f * s.legLength;
            hipY = ankleY + legLen;
            crotchY = hipY - 0.03f;
            float torsoTop = shoulderY + 0.025f, torsoBottom = crotchY - 0.012f;
            float torsoH = torsoTop - torsoBottom;
            chestR = new Vector3(0.094f * s.torsoWidth, torsoH * 0.36f, 0.079f * s.torsoDepth);
            chestC = new Vector3(0f, torsoTop - chestR.y, 0f);
            bellyR = new Vector3(0.108f * s.torsoWidth, torsoH * 0.36f, 0.089f * s.torsoDepth);
            bellyC = new Vector3(0f, torsoBottom + bellyR.y, 0.004f);

            // Arms: straight, hanging slightly away from the body.
            float a = ArmAngleDeg * Mathf.Deg2Rad;
            armDir = new Vector3(Mathf.Sin(a), -Mathf.Cos(a), 0f);
            shoulder = new Vector3(chestR.x + 0.016f, shoulderY - 0.035f, 0f);
            shoulderInner = shoulder - armDir * 0.035f;
            armLen = 0.29f * s.armLength;
            wrist = shoulder + armDir * armLen;
            armR1 = 0.04f * s.armThickness;
            armR2 = 0.035f * s.armThickness;
            handR = new Vector3(0.031f, 0.043f, 0.038f) * s.handSize;
            handC = wrist + armDir * (handR.y * 0.75f);

            // Legs: a little apart, ankles slightly wider than hips.
            // Thicker legs stand a little further apart, so the gap between them (and room for the shoes) stays.
            legR1 = 0.039f * s.legThickness;
            legR2 = 0.031f * s.legThickness;
            hip = new Vector3(0.019f + legR1, hipY + 0.02f, 0f);
            ankle = new Vector3(0.035f + legR2, ankleY, 0f);
            legDir = (ankle - hip).normalized;
            footR = new Vector3(0.032f, 0.03f, 0.055f) * s.footSize;
            footC = new Vector3(ankle.x, footR.y + 0.002f, 0.022f * s.footSize);

            ears = s.earSize > 0.01f;
            earR = new Vector3(0.022f, 0.04f, 0.032f) * Mathf.Max(s.earSize, 0.01f);
            earC = new Vector3(headR.x * 0.95f, headC.y - 0.12f * headR.y, -0.01f);

            // Outfit.
            gap = s.clothesGapPercent / 100f;
            waistY = crotchY + 0.115f;
            hemY = Mathf.Lerp(waistY - 0.015f, crotchY + 0.012f, s.shirtLength);
            sleeveEnd = shoulder + armDir * (armLen * Mathf.Lerp(0.18f, 0.55f, s.sleeveLength));
            shortsEnd = hip + legDir * ((hip - ankle).magnitude * Mathf.Lerp(0.2f, 0.62f, s.shortsLength));

            // Hair.
            hairVol = 0.012f + 0.022f * s.hairVolume;
            hairBottomY = Mathf.Lerp(headC.y - 0.2f * headR.y, chinY + 0.035f, s.hairLength);
            hairlineY = headC.y + 0.5f * headR.y;
            eyeY = headC.y - 0.2f * headR.y;
            mouthY = headC.y - 0.55f * headR.y;
            bangBottomY = Mathf.Lerp(headC.y + 0.25f * headR.y, eyeY + 0.07f * headR.y + 0.012f, s.bangLength);

            // Fringe: four large locks across the forehead (symmetric pairs), outside the hair volume.
            float[] angles = { -44f, -15f, 15f, 44f };
            lockRot = new Quaternion[angles.Length];
            lockInv = new Quaternion[angles.Length];
            lockC = new Vector3[angles.Length];
            float lockHalf = Mathf.Max(0.02f, (hairlineY - bangBottomY) * 0.5f + 0.01f);
            lockR = new Vector3(0.05f * s.headSize, lockHalf, 0.018f);
            float midY = (hairlineY + bangBottomY) * 0.5f;
            for (int i = 0; i < angles.Length; i++)
            {
                float t = angles[i] * Mathf.Deg2Rad;
                var outward = new Vector3(Mathf.Sin(t), 0f, Mathf.Cos(t));
                Vector3 surf = HeadSurfacePoint(outward, midY);
                lockC[i] = surf + outward * (hairVol * 0.55f + lockR.z);
                // Lean back at the top to follow the forehead.
                lockRot[i] = Quaternion.LookRotation(outward, Vector3.up) * Quaternion.Euler(-14f, 0f, 0f);
                lockInv[i] = Quaternion.Inverse(lockRot[i]);
            }
        }

        /// <summary>Point on the head ellipsoid in a horizontal direction at height y.</summary>
        Vector3 HeadSurfacePoint(Vector3 dirXZ, float y)
        {
            float ny = (y - headC.y) / headR.y;
            float k = Mathf.Sqrt(Mathf.Max(0.05f, 1f - ny * ny));
            float dx = dirXZ.x / headR.x, dz = dirXZ.z / headR.z;
            float s2 = k / Mathf.Sqrt(dx * dx + dz * dz);
            return new Vector3(headC.x + dirXZ.x * s2, y, headC.z + dirXZ.z * s2);
        }

        static Vector3 M(Vector3 p) => new Vector3(Mathf.Abs(p.x), p.y, p.z);

        // ------------------------------------------------------------------------------------
        // Body

        public float Head(Vector3 p)
        {
            p = M(p);
            float d = Sdf.Ellipsoid(p, headC, headR);
            if (ears) d = Sdf.Smin(d, Sdf.Ellipsoid(p, earC, earR), 0.02f);
            return d;
        }

        /// <summary>One continuous body. <paramref name="blend"/> > 1 gives a puffier version (used as the base for clothes).</summary>
        public float Body(Vector3 p, float blend = 1f)
        {
            p = M(p);
            float torso = Sdf.Smin(Sdf.Ellipsoid(p, chestC, chestR), Sdf.Ellipsoid(p, bellyC, bellyR), 0.07f);
            float neck = Sdf.RoundCone(p, neckBottom, neckTop, 0.042f, 0.033f);
            float d = Sdf.Smin(torso, neck, 0.04f * blend);

            float arm = Sdf.RoundCone(p, shoulderInner, wrist, armR1, armR2);
            arm = Sdf.Smin(arm, Sdf.Ellipsoid(p, handC, handR), 0.022f);
            d = Sdf.Smin(d, arm, 0.03f * blend);   // small: shoulders blend, hands never bridge to the hips

            float leg = Sdf.RoundCone(p, hip, ankle, legR1, legR2);
            leg = Sdf.Smin(leg, Sdf.Ellipsoid(p, footC, footR), 0.025f);
            d = Sdf.Smin(d, leg, 0.05f * blend);

            float head = Sdf.Ellipsoid(p, headC, headR);
            if (ears) head = Sdf.Smin(head, Sdf.Ellipsoid(p, earC, earR), 0.02f);
            return Sdf.Smin(d, head, 0.03f);
        }

        // ------------------------------------------------------------------------------------
        // Clothes (closed shells cut by hard planes; the hidden part of each cut is culled later)

        // Limb ownership: a point belongs to a limb only where it is closer to that limb than to the
        // torso, so cutting a sleeve or a trouser leg never bites into the torso next to it.
        float Torso(Vector3 q) => Sdf.Smin(Sdf.Ellipsoid(q, chestC, chestR), Sdf.Ellipsoid(q, bellyC, bellyR), 0.07f);
        float Arm(Vector3 q) => Sdf.Smin(Sdf.RoundCone(q, shoulderInner, wrist, armR1, armR2), Sdf.Ellipsoid(q, handC, handR), 0.022f);
        float Leg(Vector3 q) => Sdf.Smin(Sdf.RoundCone(q, hip, ankle, legR1, legR2), Sdf.Ellipsoid(q, footC, footR), 0.025f);

        /// <summary>Rounding of cut edges (hems, sleeve ends, openings): a soft felt lip instead of a sharp cut.</summary>
        const float EdgeRound = 0.006f;

        float ShortsGap => gap * 0.75f + 0.004f;

        public float Shirt(Vector3 p)
        {
            Vector3 q = M(p);
            float chestY = chestC.y;
            float flare = Mathf.Clamp01((chestY - q.y) / Mathf.Max(0.01f, chestY - hemY));
            float loose = 0.006f + 0.02f * s.shirtLooseness * (0.4f + flare);
            // Torso, neck and upper arms only (up to the sleeve end): no fabric bridging to the forearms.
            // Blends are wider than the body's, so this stays outside the body everywhere it is kept.
            float torso = Torso(q);
            float neck = Sdf.RoundCone(q, neckBottom, neckTop, 0.042f, 0.033f);
            float upperArm = Sdf.RoundCone(q, shoulderInner, sleeveEnd + armDir * 0.03f, armR1, Mathf.Lerp(armR1, armR2, 0.5f));
            float shirtBase = Sdf.Smin(Sdf.Smin(torso, neck, 0.05f), upperArm, 0.08f);
            float outer = shirtBase - gap - loose;
            // Always over the shorts where they overlap (the shorts follow the hips and thighs).
            float hips = Sdf.Smin(torso, Sdf.RoundCone(q, hip, ankle, legR1, legR2), 0.08f);
            outer = Mathf.Min(outer, hips - ShortsGap - 0.008f);

            float region = Mathf.Max(hemY - q.y, q.y - (chinY - 0.005f));
            float neckHole = Sdf.Ellipsoid(q, new Vector3(0f, shoulderY + 0.04f, 0.012f), new Vector3(0.064f, 0.05f, 0.06f));
            region = Mathf.Max(region, -neckHole);
            // Beyond the sleeve end (plane > 0) and closer to the arm than to the torso.
            float forearm = Mathf.Max(-Sdf.Plane(q, sleeveEnd, armDir), upperArm - torso);
            region = Mathf.Max(region, -forearm);
            return Sdf.Smax(outer, region, EdgeRound);
        }

        public float Shorts(Vector3 p)
        {
            Vector3 q = M(p);
            // Torso and legs only (no arms), blended wider than the body so it stays outside it.
            float outer = Sdf.Smin(Torso(q), Sdf.RoundCone(q, hip, ankle, legR1, legR2), 0.08f) - ShortsGap;
            float torso = Torso(q), leg = Leg(q);
            float region = q.y - waistY;
            // Below the trouser end (plane > 0) and closer to the leg than to the torso.
            float shin = Mathf.Max(-Sdf.Plane(q, shortsEnd, legDir), leg - torso);
            region = Mathf.Max(region, -shin);
            return Sdf.Smax(outer, region, EdgeRound);
        }

        Vector3 ShoeCenter => footC + new Vector3(0f, 0.004f, -0.004f);

        Vector3 ShoeRadii
        {
            get
            {
                var r = footR * s.shoeSize + new Vector3(0.012f, 0.014f, 0.026f);   // room behind the ankle opening
                r.x = Mathf.Min(r.x, ShoeCenter.x - 0.01f);   // the two shoes never touch
                return r;
            }
        }

        /// <summary>Negative on the inner wall of the ankle opening, well below the visible rim (hidden by the leg).</summary>
        public float ShoeHiddenWall(Vector3 p)
        {
            Vector3 q = M(p);
            float top = ShoeCenter.y + ShoeRadii.y;
            return Mathf.Max(Sdf.RoundCone(q, hip, ankle, legR1, legR2) - 0.007f, q.y - (top - 0.012f));
        }

        public float Shoes(Vector3 p)
        {
            Vector3 q = M(p);
            var c = ShoeCenter;
            var r = ShoeRadii;
            float shoe = Mathf.Max(Sdf.Ellipsoid(q, c, r), -q.y);   // flat sole on the ground
            // Opening around the leg with a rounded rim, so shoe and leg meet at a soft collar.
            float legHole = Sdf.RoundCone(q, hip, ankle, legR1, legR2) - 0.005f;
            return Sdf.Smax(shoe, -legHole, 0.008f);
        }

        // ------------------------------------------------------------------------------------
        // Hair: a puffy cap around the head with openings for the face and ears, plus fringe locks.

        public float Hair(Vector3 p)
        {
            Vector3 q = M(p);
            float side = Mathf.Clamp01(q.x / headR.x);
            float lower = Mathf.Clamp01((headC.y + 0.2f * headR.y - q.y) / headR.y);
            float puff = hairVol * (1f + 0.9f * s.hairVolume * side * side * lower);
            float cap = Sdf.Ellipsoid(q, headC, headR) - puff;

            float region = hairBottomY - q.y;
            float face = Sdf.Ellipsoid(q, new Vector3(0f, headC.y - 0.3f * headR.y, headC.z + headR.z * 0.72f),
                                       new Vector3(headR.x * 0.8f, (hairlineY - (headC.y - 0.3f * headR.y)), headR.z * 0.62f));
            region = Mathf.Max(region, -face);
            if (ears) region = Mathf.Max(region, -(Sdf.Ellipsoid(q, earC, earR) - 0.016f));
            float hair = Sdf.Smax(cap, region, 0.016f);

            float locks = 1f;
            for (int i = 0; i < lockC.Length; i++)
            {
                Vector3 local = lockInv[i] * (q - lockC[i]);
                locks = Sdf.Smin(locks, Sdf.Ellipsoid(local, Vector3.zero, lockR), 0.014f);
            }
            return Sdf.Smin(hair, locks, 0.02f);
        }

        // ------------------------------------------------------------------------------------
        // Face: closed-eye arcs and a small mouth, half sunk into the face. These are tiny, simple
        // shapes, so they are swept directly (fixed, clean topology) instead of going through the
        // field + decimation pipeline.

        public MeshData FaceMesh()
        {
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var tris = new List<int>();
            float eyeX = 0.062f * s.headSize, halfW = 0.022f * s.headSize, sag = 0.008f * s.headSize, rr = 0.0042f * s.headSize;
            const int Segments = 8, Sides = 6;

            for (int side = 0; side < 2; side++)
            {
                float sx = side == 0 ? 1f : -1f;
                int start = pos.Count;
                Vector3 Arc(float u)
                {
                    float t = u * 2f - 1f;
                    var p = FrontSurface(eyeX + t * halfW, eyeY - sag * (1f - t * t), rr * 0.3f);
                    return new Vector3(p.x * sx, p.y, p.z);
                }
                // Rings along the arc.
                for (int i = 0; i <= Segments; i++)
                {
                    float u = i / (float)Segments;
                    Vector3 c = Arc(u);
                    Vector3 tangent = (Arc(Mathf.Min(1f, u + 0.01f)) - Arc(Mathf.Max(0f, u - 0.01f))).normalized;
                    Vector3 outward = HeadNormal(c);
                    Vector3 bin = Vector3.Cross(tangent, outward).normalized;
                    outward = Vector3.Cross(bin, tangent).normalized;
                    for (int k = 0; k < Sides; k++)
                    {
                        float a = k / (float)Sides * 2f * Mathf.PI;
                        Vector3 n = outward * Mathf.Cos(a) + bin * Mathf.Sin(a);
                        pos.Add(c + n * rr);
                        nrm.Add(n);
                    }
                }
                for (int i = 0; i < Segments; i++)
                    for (int k = 0; k < Sides; k++)
                    {
                        int a = start + i * Sides + k, b = start + i * Sides + (k + 1) % Sides;
                        int c = a + Sides, d = b + Sides;
                        AddOutward(tris, pos, nrm, a, c, b);
                        AddOutward(tris, pos, nrm, b, c, d);
                    }
                // Rounded tips.
                for (int end = 0; end < 2; end++)
                {
                    float u = end == 0 ? 0f : 1f;
                    Vector3 dir = (Arc(end == 0 ? 0f : 1f) - Arc(end == 0 ? 0.05f : 0.95f)).normalized;
                    int tip = pos.Count;
                    pos.Add(Arc(u) + dir * rr);
                    nrm.Add(dir);
                    int ring = start + (end == 0 ? 0 : Segments) * Sides;
                    for (int k = 0; k < Sides; k++)
                        AddOutward(tris, pos, nrm, ring + k, ring + (k + 1) % Sides, tip);
                }
            }

            // Mouth: a small flattened ellipsoid.
            Vector3 mc = FrontSurface(0f, mouthY, 0.001f);
            Vector3 mr = new Vector3(0.0085f, 0.0042f, 0.0035f) * s.headSize;
            const int Lat = 5, Lon = 10;
            int m0 = pos.Count;
            for (int i = 0; i <= Lat; i++)
            {
                float th = i / (float)Lat * Mathf.PI;
                for (int k = 0; k < Lon; k++)
                {
                    float ph = k / (float)Lon * 2f * Mathf.PI;
                    var unit = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    pos.Add(mc + Vector3.Scale(unit, mr));
                    nrm.Add(new Vector3(unit.x / mr.x, unit.y / mr.y, unit.z / mr.z).normalized);
                }
            }
            for (int i = 0; i < Lat; i++)
                for (int k = 0; k < Lon; k++)
                {
                    int a = m0 + i * Lon + k, b = m0 + i * Lon + (k + 1) % Lon, c = a + Lon, d = b + Lon;
                    if (i > 0) AddOutward(tris, pos, nrm, a, b, c);
                    if (i < Lat - 1) AddOutward(tris, pos, nrm, b, d, c);
                }
            return new MeshData { positions = pos.ToArray(), normals = nrm.ToArray(), triangles = tris.ToArray() };
        }

        /// <summary>Adds a triangle wound so it faces the way its vertex normals point.</summary>
        static void AddOutward(List<int> tris, List<Vector3> pos, List<Vector3> nrm, int a, int b, int c)
        {
            Vector3 face = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            if (face.sqrMagnitude < 1e-20f) return;
            if (Vector3.Dot(face, nrm[a] + nrm[b] + nrm[c]) < 0f) { int t = b; b = c; c = t; }
            tris.Add(a); tris.Add(b); tris.Add(c);
        }

        Vector3 HeadNormal(Vector3 p)
        {
            Vector3 q = new Vector3(Mathf.Abs(p.x), p.y, p.z) - headC;
            var n = new Vector3(q.x / (headR.x * headR.x), q.y / (headR.y * headR.y), q.z / (headR.z * headR.z));
            n.x *= Mathf.Sign(p.x == 0f ? 1f : p.x);
            return n.normalized;
        }

        Vector3 FrontSurface(float x, float y, float outset)
        {
            float nx = (x - headC.x) / headR.x, ny = (y - headC.y) / headR.y;
            float z = headC.z + headR.z * Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
            return new Vector3(x, y, z + outset);
        }
    }
}
