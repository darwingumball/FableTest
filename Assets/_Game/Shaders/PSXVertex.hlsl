// PSX vertex helpers for Shader Graph Custom Function nodes (HDRP Lit target).
//
// Usage (when building the "PSX Lit" graph in the editor):
// 1. Vertex stage: Custom Function node -> File: this file, Name: PSXSnap_float.
//    Inputs: PositionOS (Object Space position), SnapResolution (Vector2, e.g. 320x180;
//    wire from global _PSXSnapResolution or a material property).
//    Output: SnappedPositionOS -> Vertex Position block.
// 2. Affine texturing: multiply UV by W in vertex (PSXAffinePack), divide in fragment
//    (PSXAffineUnpack) via custom interpolators to emulate noperspective.
//
// The functions are written against Shader Graph's generated code conventions
// (TransformObjectToHClip etc. come from the graph includes).

#ifndef PSX_VERTEX_INCLUDED
#define PSX_VERTEX_INCLUDED

void PSXSnap_float(float3 PositionOS, float2 SnapResolution, out float3 SnappedPositionOS)
{
    // Snap the vertex to a virtual low-res grid in clip space, then return to object
    // space so the graph's own transforms stay intact.
    float4 clip = TransformObjectToHClip(PositionOS);
    if (clip.w > 0.0)
    {
        float2 grid = max(SnapResolution, float2(2.0, 2.0)) * 0.5;
        float2 ndc = clip.xy / clip.w;
        ndc = floor(ndc * grid + 0.5) / grid;
        clip.xy = ndc * clip.w;
    }
    // Back to object space: inverse VP then inverse M.
    float4 positionWS = mul(UNITY_MATRIX_I_VP, clip);
    positionWS /= positionWS.w;
    SnappedPositionOS = TransformWorldToObject(positionWS.xyz);
}

void PSXAffinePack_float(float2 UV, float3 PositionOS, out float3 PackedUVW)
{
    // Multiply UV by clip W; interpolators then blend without perspective correction
    // when the fragment divides it back out (classic PSX texture warp).
    float4 clip = TransformObjectToHClip(PositionOS);
    PackedUVW = float3(UV * clip.w, clip.w);
}

void PSXAffineUnpack_float(float3 PackedUVW, out float2 UV)
{
    UV = PackedUVW.xy / max(PackedUVW.z, 1e-5);
}

#endif // PSX_VERTEX_INCLUDED
