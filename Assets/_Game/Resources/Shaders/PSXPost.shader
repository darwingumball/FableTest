Shader "Hidden/Game/PSXPost"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 texcoord : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }

    TEXTURE2D_X(_MainTex);   // full-res camera color (RTHandle)
    TEXTURE2D(_PSXLowRes);   // low-res intermediate
    float4 _PSXParams;       // x,y,z: color levels per channel, w: dither strength
    float4 _PSXLowResSize;   // xy: size, zw: 1/size

    // Ordered 4x4 Bayer matrix, normalized to [0,1).
    static const float BAYER4[16] =
    {
         0.0 / 16.0,  8.0 / 16.0,  2.0 / 16.0, 10.0 / 16.0,
        12.0 / 16.0,  4.0 / 16.0, 14.0 / 16.0,  6.0 / 16.0,
         3.0 / 16.0, 11.0 / 16.0,  1.0 / 16.0,  9.0 / 16.0,
        15.0 / 16.0,  7.0 / 16.0, 13.0 / 16.0,  5.0 / 16.0
    };

    float3 QuantizeDither(float3 c, uint2 pixel)
    {
        float d = (BAYER4[(pixel.y & 3) * 4 + (pixel.x & 3)] - 0.5) * _PSXParams.w;
        float3 levels = _PSXParams.xyz;
        return floor(saturate(c) * levels + 0.5 + d) / levels;
    }

    // Pass 0: point-downsample full-res source into the low-res RT.
    float4 FragDownsample(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 srcPos = min(uint2(input.texcoord * _ScreenSize.xy), uint2(_ScreenSize.xy) - 1);
        return float4(LOAD_TEXTURE2D_X(_MainTex, srcPos).rgb, 1);
    }

    // Pass 1: quantize + dither the low-res image and point-upscale to full res.
    float4 FragUpscale(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 low = min(uint2(input.texcoord * _PSXLowResSize.xy), uint2(_PSXLowResSize.xy) - 1);
        float3 c = LOAD_TEXTURE2D(_PSXLowRes, low).rgb;
        return float4(QuantizeDither(c, low), 1);
    }

    // Pass 2: native-res quantize + dither only (PSX resolution set to Native).
    float4 FragNative(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 pos = uint2(input.positionCS.xy);
        float3 c = LOAD_TEXTURE2D_X(_MainTex, pos).rgb;
        return float4(QuantizeDither(c, pos), 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "PSX Downsample"
            ZWrite Off ZTest Always Blend Off Cull Off
            HLSLPROGRAM
            #pragma fragment FragDownsample
            #pragma vertex Vert
            ENDHLSL
        }

        Pass
        {
            Name "PSX Upscale Quantize"
            ZWrite Off ZTest Always Blend Off Cull Off
            HLSLPROGRAM
            #pragma fragment FragUpscale
            #pragma vertex Vert
            ENDHLSL
        }

        Pass
        {
            Name "PSX Native Quantize"
            ZWrite Off ZTest Always Blend Off Cull Off
            HLSLPROGRAM
            #pragma fragment FragNative
            #pragma vertex Vert
            ENDHLSL
        }
    }
    Fallback Off
}
