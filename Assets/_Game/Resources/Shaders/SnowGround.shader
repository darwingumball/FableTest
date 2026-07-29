// Deformable snow ground.
//
// Samples the deformation RT written by SnowDeformationManager (_SnowDeformRT,
// R = 0 pristine .. 1 fully compressed) and:
//   - displaces vertices downward where compressed (needs a subdivided mesh),
//   - reconstructs normals from the depth gradient so trail walls catch light,
//   - darkens/blues compressed snow the way packed snow reads in real life.
//
// Shaded with a simple wrapped-lambert against the globals published by SunController
// rather than HDRP's full lit stack: cheap, PSX-appropriate, and it keeps this authorable
// from script. Swap for a proper Shader Graph Lit material when hand-authoring.
Shader "Game/SnowGround"
{
    Properties
    {
        _SnowColor ("Snow Color", Color) = (0.90, 0.92, 0.96, 1)
        _PackedColor ("Compressed Color", Color) = (0.62, 0.66, 0.74, 1)
        _DepthMeters ("Max Depression (m)", Float) = 0.28
        _NormalStrength ("Normal Strength", Float) = 3.0
        _SmoothRadiusTexels ("Smoothing Radius (texels)", Range(1, 12)) = 5
        _MinThickness ("Min Visible Thickness (m)", Float) = 0.012
    }

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
        // GetCurrentExposureMultiplier - without it this shader writes raw values that
        // ignore auto-exposure and read as blown-out next to properly exposed geometry.
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariablesFunctions.hlsl"

        TEXTURE2D(_SnowDeformRT);
        SAMPLER(sampler_SnowDeformRT);
        // 1 = snow may lie here, 0 = blocked (building interiors, covered ground).
        // Written by SnowDeformationManager from registered SnowBlocker footprints.
        TEXTURE2D(_SnowMaskRT);
        SAMPLER(sampler_SnowMaskRT);

        float4 _SnowRegionParams; // xy: region origin XZ, zw: 1/regionSize
        float4 _SnowColor;
        float4 _PackedColor;
        float _DepthMeters;
        float _NormalStrength;
        float4 _GameSunDirection;
        float _GameSunAmount;
        float _GameSunLux;
        float _SnowDeformTexel;
        float _SmoothRadiusTexels;
        float _MinThickness;
        // Current snow depth in metres, driven by WeatherManager (accumulate / melt).
        float _SnowHeightMeters;

        float2 WorldToDeformUV(float3 positionWS)
        {
            return (positionWS.xz - _SnowRegionParams.xy) * _SnowRegionParams.zw;
        }

        float SampleDeform(float2 uv)
        {
            if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
            // saturate is belt-and-braces: displacement must never go negative or the
            // ground lifts up through the player.
            return saturate(SAMPLE_TEXTURE2D_LOD(_SnowDeformRT, sampler_SnowDeformRT, uv, 0).r);
        }

        // Vertices are far coarser than texels, so a single tap per vertex aliases the
        // height field into faceted, jagged prints. Averaging a small cross of taps
        // low-passes the height before it reaches the geometry.
        float SampleDeformSmooth(float2 uv)
        {
            float r = _SnowDeformTexel * _SmoothRadiusTexels;
            float c = SampleDeform(uv);
            float n = SampleDeform(uv + float2(0, r));
            float s = SampleDeform(uv - float2(0, r));
            float e = SampleDeform(uv + float2(r, 0));
            float w = SampleDeform(uv - float2(r, 0));
            float ne = SampleDeform(uv + float2(r, r));
            float nw = SampleDeform(uv + float2(-r, r));
            float se = SampleDeform(uv + float2(r, -r));
            float sw = SampleDeform(uv + float2(-r, -r));
            // Tent weights: centre 4, edges 2, corners 1.
            return (c * 4.0 + (n + s + e + w) * 2.0 + (ne + nw + se + sw)) / 16.0;
        }

        struct Attributes
        {
            float3 positionOS : POSITION;
            float3 normalOS : NORMAL;
        };

        // Mask is authored per-footprint, so bilinear edges are fine and cheap.
        //
        // Outside the deformation region this returns 0, not 1. The mesh is a
        // player-following LOD grid whose outer rings deliberately overhang the region so
        // the far corner is always covered; without this the overhang would clamp-sample
        // the edge texel and smear a skirt of snow across ground that has no snow data.
        // Zero mask means zero thickness, which the fragment clip then discards.
        float SampleMask(float2 uv)
        {
            if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
            return saturate(SAMPLE_TEXTURE2D_LOD(_SnowMaskRT, sampler_SnowMaskRT, uv, 0).r);
        }

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float compression : TEXCOORD1;
            // Snow actually left standing here, in metres. Drives the clip that exposes
            // the street: where a footprint has carved through, this reaches zero.
            float thickness : TEXCOORD2;
            // Reconstructed in the VERTEX stage. Doing it per fragment cost four
            // 9-tap filter evaluations - 36 texture samples for every pixel of a
            // full-screen ground plane. The mesh is dense enough that interpolating
            // per-vertex normals is visually equivalent for a fraction of the cost.
            float3 normalWS : TEXCOORD3;
        };

        float3 ReconstructNormal(float2 uv)
        {
            float texel = _SnowDeformTexel * _SmoothRadiusTexels;
            float dL = SampleDeformSmooth(uv - float2(texel, 0));
            float dR = SampleDeformSmooth(uv + float2(texel, 0));
            float dD = SampleDeformSmooth(uv - float2(0, texel));
            float dU = SampleDeformSmooth(uv + float2(0, texel));

            // Convert the compression gradient into a real world-space slope:
            // height = -compression * depth, sampled 2 texels apart in metres.
            float regionSize = 1.0 / max(_SnowRegionParams.z, 1e-6);
            float worldStep = max(texel * regionSize * 2.0, 1e-4);
            float2 slope = float2(dR - dL, dU - dD) * _DepthMeters / worldStep;

            return normalize(float3(slope.x * _NormalStrength, 1.0, slope.y * _NormalStrength));
        }

        Varyings SnowVert(Attributes input)
        {
            Varyings o;
            // HDRP renders camera-relative: TransformObjectToWorld returns a position
            // relative to the camera. Sampling the deformation texture with that makes the
            // trail slide around with the player. GetAbsolutePositionWS puts it back into
            // true world space so the texture stays pinned to the ground.
            float3 positionRWS = TransformObjectToWorld(input.positionOS);
            float3 positionAWS = GetAbsolutePositionWS(positionRWS);

            float2 deformUV = WorldToDeformUV(positionAWS);
            float compression = SampleDeformSmooth(deformUV);
            // The mesh sits at ground level and RISES with the current snow depth, so snow
            // grows in while it falls and sinks away as it melts. The mask zeroes the depth
            // wherever a SnowBlocker covers the ground (building interiors), so those areas
            // stay flat at street level rather than sprouting snow through the floor.
            float snowHeight = _SnowHeightMeters * SampleMask(deformUV);
            // Carve depth is allowed to OVERSHOOT the lying depth, then the result is
            // clamped at zero. Capping the drop at snowHeight instead would mean only a
            // perfect compression of 1.0 could ever expose the road - and the tent filter
            // that smooths the height field never produces 1.0, so trails always stopped
            // just short. With _DepthMeters ~1.5x the snow depth, the core of a footprint
            // reaches bare street while its edges still ramp out naturally.
            float drop = compression * _DepthMeters;
            float lift = max(snowHeight - drop, 0.0);
            positionRWS.y += lift;
            positionAWS.y += lift;

            o.positionWS = positionAWS;   // absolute - fragment re-samples with it
            o.compression = compression;
            o.thickness = lift;
            o.normalWS = ReconstructNormal(deformUV);
            o.positionCS = TransformWorldToHClip(positionRWS);
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex SnowVert
            #pragma fragment SnowFrag

            float4 SnowFrag(Varyings input) : SV_Target
            {
                // Nothing left lying here: drop the fragment so whatever is underneath -
                // street, warehouse floor - is what you see. Also removes the snow plane
                // entirely when coverage is zero, instead of leaving a white sheet over
                // the ground. Must match the depth pass or depth and colour disagree.
                clip(input.thickness - _MinThickness);

                // Interpolation denormalises; renormalise before lighting.
                float3 normalWS = normalize(input.normalWS);

                float3 lightDir = normalize(-_GameSunDirection.xyz);
                // Wrapped lambert keeps the shadowed side readable instead of pure black.
                float ndl = saturate(dot(normalWS, lightDir) * 0.5 + 0.5);
                float lighting = lerp(0.30, 1.0, ndl);

                float3 albedo = lerp(_SnowColor.rgb, _PackedColor.rgb, saturate(input.compression));
                // Lambert luminance = albedo * illuminance / PI, matching how HDRP lights
                // everything else; exposure then maps it to display range.
                float illuminance = max(_GameSunLux, 1.0);
                float3 luminance = albedo * lighting * (illuminance / PI);
                return float4(luminance * GetCurrentExposureMultiplier(), 1.0);
            }
            ENDHLSL
        }

        // Depth prepass so HDRP has correct depth for fog, SSAO and the PSX pass.
        Pass
        {
            Name "DepthForwardOnly"
            Tags { "LightMode" = "DepthForwardOnly" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex SnowVert
            #pragma fragment DepthFrag
            float4 DepthFrag(Varyings input) : SV_Target
            {
                clip(input.thickness - _MinThickness);
                return 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
