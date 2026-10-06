// Stylised day/night gradient skybox for URP (mobile friendly).
// All time-of-day and weather work (colours, sun direction, sun visibility, cloud thresholds, wind
// offsets) happens on the CPU in DayNightSkyController; this shader only evaluates what depends on
// the view direction.
// World up is +Y. The direction comes from the skybox mesh, so camera position and roll do not
// affect the result.
Shader "Skybox/Day Night Gradient"
{
    Properties
    {
        // Normally written every update by DayNightSkyController. The defaults show a daytime sky
        // with a few clouds even without the controller.
        _ZenithColor("Zenith Color", Color) = (0.33, 0.60, 0.92, 1)
        _HorizonColor("Horizon Color", Color) = (0.78, 0.89, 0.98, 1)
        _GroundColor("Below Horizon Color", Color) = (0.62, 0.66, 0.72, 1)
        _GlowColor("Sunrise/Sunset Glow (A = strength)", Color) = (1.0, 0.62, 0.42, 0)
        _SunColor("Sun Color (A = visibility)", Color) = (1.0, 0.97, 0.88, 1)
        _SunDirection("Sun Direction (world, towards the sun)", Vector) = (0.35, 0.6, -0.72, 0)
        // x: 1 / horizon band height, y: 1 / ground blend height, z: 1 / glow height, w: unused
        _SkyParams("Sky Params", Vector) = (2.2, 12, 3.5, 0)
        // x: cos(outer radius), y: 1 / (cos(inner) - cos(outer)), z: glow width scale, w: glow width offset
        _SunParams("Sun Params", Vector) = (0.99863, 1095, 2.5, -1.5)

        // Seamless noise: R = large cloud shapes, G = small detail. Shared by every sky material.
        [NoScaleOffset] _CloudNoise("Cloud Noise (R shape, G detail)", 2D) = "black" {}
        _CloudLitColor("Cloud Lit Color (A = sun-side brightening)", Color) = (1, 1, 1, 0.35)
        _CloudShadeColor("Cloud Shade Color", Color) = (0.78, 0.85, 0.95, 1)
        // xy: shape UV offset, zw: detail UV offset (wind; both kept in [0, 1) on the CPU)
        _CloudOffset("Cloud Offset", Vector) = (0, 0, 0, 0)
        // x: shape tiles per plane unit, y: detail tiles per plane unit, z: dome curvature, w: detail strength
        _CloudShape("Cloud Shape", Vector) = (0.35, 0.945, 0.2, 0.175)
        // x/y: density = saturate(n * x + y), z: opacity (0 = no clouds), w: 1 / horizon fade height
        _CloudDensity("Cloud Density", Vector) = (3.33, -2.21, 1, 6)
        // x/y: thickness = saturate(n * x + y), z/w: sun halo = saturate(dot(dir, sun) * z + w)
        _CloudLighting("Cloud Lighting", Vector) = (1.9, -0.9, 7.46, -6.46)
        [Toggle(_DAYNIGHTSKY_DITHER)] _Dither("Dither (reduces banding, tiny cost)", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _ _DAYNIGHTSKY_DITHER

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #if defined(_DAYNIGHTSKY_DITHER)
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
            #endif

            TEXTURE2D(_CloudNoise);
            SAMPLER(sampler_CloudNoise);

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor;
                half4 _HorizonColor;
                half4 _GroundColor;
                half4 _GlowColor;
                half4 _SunColor;
                float4 _SunDirection;
                float4 _SkyParams;
                float4 _SunParams;
                half4 _CloudLitColor;
                half4 _CloudShadeColor;
                float4 _CloudOffset;
                float4 _CloudShape;
                float4 _CloudDensity;
                float4 _CloudLighting;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // The skybox mesh is centred on the camera and never rotated, so its object-space
                // position is the world view direction. Normalised per pixel (interpolation shortens it).
                output.direction = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.direction);
                float height = dir.y;
                // Direction work stays in float: a small sun needs cosines very close to 1.
                float toSun = dot(dir, _SunDirection.xyz);

                // Horizon -> zenith with an ease-out curve (reaches the zenith colour at 1/_SkyParams.x).
                half t = (half)saturate(height * _SkyParams.x);
                t = t * (2.0h - t);
                half3 color = lerp(_HorizonColor.rgb, _ZenithColor.rgb, t);

                // Below the horizon: short blend to a soft ground tone (never black).
                half below = (half)saturate(-height * _SkyParams.y);
                color = lerp(color, _GroundColor.rgb, below);

                // Warm glow near the horizon on the sun's side; strength comes from the CPU (0 at noon/night).
                half side = (half)saturate((toSun * 0.5 + 0.5) * _SunParams.z + _SunParams.w);
                half band = (half)saturate(1.0 - abs(height) * _SkyParams.z);
                half glow = _GlowColor.a * side * side * band * band;
                color = lerp(color, _GlowColor.rgb, glow);

                // Sun disk with a soft edge, cut by the horizon so it sinks instead of showing below.
                float disk = saturate((toSun - _SunParams.x) * _SunParams.y);
                disk = disk * disk * (3.0 - 2.0 * disk);
                disk *= saturate(height * 400.0 + 0.5);
                color = lerp(color, _SunColor.rgb, (half)disk * _SunColor.a);

                // Clouds: a virtual ceiling above the viewer, bent into a shallow dome. The curvature
                // term keeps the divide finite at (and below) the horizon and gives the zenith no seam.
                // Only the direction is used, so the layer stays infinitely far: moving the camera does
                // not drag it, and the wind offset lives in world XZ.
                float2 plane = dir.xz / (max(height, 0.0) + _CloudShape.z);
                half shapeNoise = SAMPLE_TEXTURE2D(_CloudNoise, sampler_CloudNoise, plane * _CloudShape.x + _CloudOffset.xy).r;
                half detailNoise = SAMPLE_TEXTURE2D(_CloudNoise, sampler_CloudNoise, plane * _CloudShape.y + _CloudOffset.zw).g;
                half n = shapeNoise + (detailNoise - 0.5h) * (half)_CloudShape.w;

                // Coverage is a soft threshold computed on the CPU; at coverage 0 it lies above every n.
                half density = (half)saturate(n * _CloudDensity.x + _CloudDensity.y);
                density = density * density * (3.0h - 2.0h * density);
                half fade = (half)saturate(height * _CloudDensity.w);
                fade = fade * fade * (3.0h - 2.0h * fade);

                // Lit edges, shaded thick cores; thin parts near the sun catch its light.
                half thickness = (half)saturate(n * _CloudLighting.x + _CloudLighting.y);
                half3 cloud = lerp(_CloudLitColor.rgb, _CloudShadeColor.rgb, thickness);
                half halo = (half)saturate(toSun * _CloudLighting.z + _CloudLighting.w);
                cloud += _SunColor.rgb * (halo * halo * (1.0h - thickness) * _CloudLitColor.a);
                // Distant clouds take on the horizon colour so they melt into the haze.
                cloud = lerp(_HorizonColor.rgb, cloud, 0.4h + 0.6h * fade);

                // Drawn over the sun: the disk is hidden in proportion to the local density.
                color = lerp(color, cloud, density * fade * (half)_CloudDensity.z);

                #if defined(_DAYNIGHTSKY_DITHER)
                    // +-0.5 of an 8-bit display step, static per pixel: hides banding in slow gradients.
                    half noise = (half)(InterleavedGradientNoise(input.positionCS.xy, 0) - 0.5) / 255.0h;
                    #if defined(UNITY_COLORSPACE_GAMMA)
                        color += noise;
                    #else
                        // In linear space one sRGB step is about 2.2 * sqrt(c) linear units.
                        color += noise * 2.2h * sqrt(max(color, 1e-4h));
                    #endif
                #endif

                return half4(color, 1.0h);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
