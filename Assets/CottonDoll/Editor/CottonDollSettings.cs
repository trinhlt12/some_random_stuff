using UnityEngine;

namespace CottonDoll.Editor
{
    /// <summary>
    /// Every shape parameter of the cotton doll. Sizes other than Height are multipliers of the
    /// reference proportions (1 = default) or fractions, so the doll keeps its proportions at any
    /// height. Ranges are deliberately limited to shapes that still look good; values outside are
    /// clamped by <see cref="Sanitize"/>.
    /// </summary>
    public sealed class CottonDollSettings : ScriptableObject
    {
        [Header("Body")]
        [Tooltip("Height from the soles to the top of the head, without hair (Unity units = metres).")]
        [Range(0.3f, 2.5f)] public float height = 1f;
        [Tooltip("Head size relative to the reference (about 31% of the height).")]
        [Range(0.85f, 1.2f)] public float headSize = 1f;
        [Tooltip("0 = round head, higher = wider and shorter (squashed).")]
        [Range(0f, 0.25f)] public float headFlatness = 0.1f;
        [Tooltip("Size of the rounded ears (0 = no ears).")]
        [Range(0f, 1.15f)] public float earSize = 1f;
        [Tooltip("Width of the torso.")]
        [Range(0.8f, 1.3f)] public float torsoWidth = 1f;
        [Tooltip("Front-to-back thickness of the torso.")]
        [Range(0.8f, 1.3f)] public float torsoDepth = 1f;
        [Tooltip("Arm length (shoulder to wrist).")]
        [Range(0.8f, 1.2f)] public float armLength = 1f;
        [Tooltip("Arm thickness. Higher = chubbier.")]
        [Range(0.8f, 1.35f)] public float armThickness = 1f;
        [Tooltip("Leg length (hip to ankle). The torso takes up the rest of the height.")]
        [Range(0.8f, 1.25f)] public float legLength = 1f;
        [Tooltip("Leg thickness. 0.8 is close to the slim legs of the reference image, higher is chubbier.")]
        [Range(0.75f, 1.35f)] public float legThickness = 1f;
        [Tooltip("Size of the rounded, fingerless hands.")]
        [Range(0.8f, 1.3f)] public float handSize = 1f;
        [Tooltip("Size of the feet (hidden in the shoes; also drives the shoe size).")]
        [Range(0.8f, 1.3f)] public float footSize = 1f;

        [Header("Hair (short rounded bob)")]
        [Tooltip("How puffy the hair is, mostly at the sides.")]
        [Range(0f, 1f)] public float hairVolume = 0.55f;
        [Tooltip("0 = ends at ear height, 0.85 = just above the chin.")]
        [Range(0f, 0.85f)] public float hairLength = 0.6f;
        [Tooltip("How far the fringe locks come down the forehead (0 = short, 1 = just above the eyes).")]
        [Range(0f, 1f)] public float bangLength = 0.6f;

        [Header("Outfit")]
        [Tooltip("How loose the T-shirt is (it flares slightly towards the hem).")]
        [Range(0f, 1f)] public float shirtLooseness = 0.5f;
        [Tooltip("0 = hem at the waist, 1 = hem over the hips.")]
        [Range(0f, 1f)] public float shirtLength = 0.6f;
        [Tooltip("0 = cap sleeves, 1 = sleeves down to about the elbow.")]
        [Range(0f, 1f)] public float sleeveLength = 0.45f;
        [Tooltip("0 = very short shorts, 1 = shorts to just above the knee.")]
        [Range(0f, 1f)] public float shortsLength = 0.45f;
        [Tooltip("Shoe size relative to the foot.")]
        [Range(0.9f, 1.3f)] public float shoeSize = 1.05f;
        [Tooltip("Gap between body and clothes, in % of the height (1 = 1 cm on a 1 m doll). Too small risks the body poking through.")]
        [Range(0.4f, 3f)] public float clothesGapPercent = 1.1f;

        [Header("Face")]
        [Tooltip("Simple closed-eye arcs and a small mouth. Turn off for a blank face.")]
        public bool faceFeatures = true;

        [Header("Colours (sRGB)")]
        public Color skinColor = new Color(0.98f, 0.86f, 0.78f);
        public Color hairColor = new Color(0.93f, 0.89f, 0.76f);
        public Color shirtColor = new Color(0.55f, 0.66f, 0.30f);
        public Color shortsColor = new Color(0.88f, 0.62f, 0.38f);
        public Color shoeColor = new Color(0.45f, 0.58f, 0.27f);
        public Color faceColor = new Color(0.36f, 0.24f, 0.20f);

        [Header("Parts")]
        public bool showBody = true;
        public bool showHair = true;
        public bool showShirt = true;
        public bool showShorts = true;
        public bool showShoes = true;

        /// <summary>Clamps every value into its supported range (also fixes NaN).</summary>
        public void Sanitize()
        {
            height = Clamp(height, 0.3f, 2.5f, 1f);
            headSize = Clamp(headSize, 0.85f, 1.2f, 1f);
            headFlatness = Clamp(headFlatness, 0f, 0.25f, 0.1f);
            earSize = Clamp(earSize, 0f, 1.15f, 1f);
            torsoWidth = Clamp(torsoWidth, 0.8f, 1.3f, 1f);
            torsoDepth = Clamp(torsoDepth, 0.8f, 1.3f, 1f);
            armLength = Clamp(armLength, 0.8f, 1.2f, 1f);
            armThickness = Clamp(armThickness, 0.8f, 1.35f, 1f);
            legLength = Clamp(legLength, 0.8f, 1.25f, 1f);
            legThickness = Clamp(legThickness, 0.75f, 1.35f, 1f);
            handSize = Clamp(handSize, 0.8f, 1.3f, 1f);
            footSize = Clamp(footSize, 0.8f, 1.3f, 1f);
            hairVolume = Clamp(hairVolume, 0f, 1f, 0.55f);
            hairLength = Clamp(hairLength, 0f, 0.85f, 0.6f);
            bangLength = Clamp(bangLength, 0f, 1f, 0.6f);
            shirtLooseness = Clamp(shirtLooseness, 0f, 1f, 0.5f);
            shirtLength = Clamp(shirtLength, 0f, 1f, 0.6f);
            sleeveLength = Clamp(sleeveLength, 0f, 1f, 0.45f);
            shortsLength = Clamp(shortsLength, 0f, 1f, 0.45f);
            shoeSize = Clamp(shoeSize, 0.9f, 1.3f, 1.05f);
            clothesGapPercent = Clamp(clothesGapPercent, 0.4f, 3f, 1.1f);
        }

        static float Clamp(float v, float min, float max, float fallback) =>
            float.IsNaN(v) || float.IsInfinity(v) ? fallback : Mathf.Clamp(v, min, max);

        public void ResetToDefaults()
        {
            var fresh = CreateInstance<CottonDollSettings>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(fresh), this);
            DestroyImmediate(fresh);
        }
    }
}
