// Render-texture ops for snow deformation. The RT stores compression depth:
// 0 = pristine snow, 1 = fully stamped down. Pass 0 stamps a radial depression
// (max-blended so overlapping stamps keep the deepest); pass 1 uniformly refills
// (reverse-subtract) at the current snowfall rate.
Shader "Hidden/Game/SnowDeform"
{
    SubShader
    {
        ZWrite Off ZTest Always Cull Off

        CGINCLUDE
        #include "UnityCG.cginc"

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        float4 _StampData;   // xy: stamp center (region UV), z: radius (UV), w: depth 0..1
        float _RefillAmount; // depth removed this frame
        ENDCG

        Pass // 0: stamp (max blend)
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragStamp
            float4 fragStamp(v2f i) : SV_Target
            {
                float dist = distance(i.uv, _StampData.xy) / max(_StampData.z, 1e-5);
                // smoothstep gives an S-curve rim instead of the hard parabola edge, so
                // overlapping stamps merge into a channel rather than scalloped circles.
                float depth = smoothstep(1.0, 0.0, dist) * _StampData.w;
                return float4(depth, 0, 0, 1);
            }
            ENDCG
        }

        Pass // 1: refill (dst = dst - src)
        {
            BlendOp RevSub
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragRefill
            float4 fragRefill(v2f i) : SV_Target
            {
                return float4(_RefillAmount, 0, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
