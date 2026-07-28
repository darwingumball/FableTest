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

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float compression : TEXCOORD1;
        };

        Varyings SnowVert(Attributes input)
        {
            Varyings o;
            // HDRP renders camera-relative: TransformObjectToWorld returns a position
            // relative to the camera. Sampling the deformation texture with that makes the
            // trail slide around with the player. GetAbsolutePositionWS puts it back into
            // true world space so the texture stays pinned to the ground.
            float3 positionRWS = TransformObjectToWorld(input.positionOS);
            float3 positionAWS = GetAbsolutePositionWS(positionRWS);

            float compression = SampleDeformSmooth(WorldToDeformUV(positionAWS));
            // The mesh sits at ground level and RISES with the current snow depth, so snow
            // grows in while it falls and sinks away as it melts. Trails can never carve
            // deeper than the snow that is actually lying.
            float snowHeight = _SnowHeightMeters;
            float drop = compression * min(_DepthMeters, snowHeight);
            float lift = snowHeight - drop;
            positionRWS.y += lift;
            positionAWS.y += lift;

            o.positionWS = positionAWS;   // absolute - fragment re-samples with it
            o.compression = compression;
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
                // Rebuild the surface normal from the deformation gradient so trail walls
                // shade differently from flat snow. Step exactly one texel.
                float2 uv = WorldToDeformUV(input.positionWS);
                // Match the vertex smoothing radius, otherwise the normals describe a
                // sharper surface than the geometry actually has and edges look creased.
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

                float3 normalWS = normalize(float3(
                    slope.x * _NormalStrength,
                    1.0,
                    slope.y * _NormalStrength));

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
            float4 DepthFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
