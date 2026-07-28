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

    // Interleaved gradient noise, static (no time term - PSX dither never crawled).
    //
    // This replaced a 4x4 ordered Bayer matrix. Bayer tiles into a hard crosshatch that
    // is extremely visible across smooth gradients - sky, fog, dark interiors - and was
    // the most fatiguing part of the whole effect to look at. IGN breaks up the
    // regularity while still being a fixed per-pixel pattern, so it reads as period-
    // correct dither instead of film grain.
    float DitherNoise(uint2 pixel)
    {
        const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
        return frac(magic.z * frac(dot(float2(pixel), magic.xy)));
    }

    float3 QuantizeDither(float3 c, uint2 pixel)
    {
        // Triangular PDF (difference of two offset samples). Uniform noise leaves a harsh
        // one-sided speckle; TPDF cancels banding far more evenly for the same amplitude.
        float n = DitherNoise(pixel) - DitherNoise(pixel + uint2(17, 13));
        float d = n * _PSXParams.w;
        float3 levels = _PSXParams.xyz;

        // Quantize in a perceptual (sqrt) space, not linearly. This buffer is post-tonemap
        // but still linear, and the game is mostly dark: a linear step of 1/95 is a ~13%
        // jump on a 0.08 pixel, which is why contour rings appear in every dark falloff
        // even at 255 levels. sqrt spends the available levels where the eye actually
        // resolves them, so the banding disappears without needing dither to hide it.
        float3 perceptual = sqrt(saturate(c));
        float3 quantized = floor(perceptual * levels + 0.5 + d) / levels;
        return quantized * quantized;
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
