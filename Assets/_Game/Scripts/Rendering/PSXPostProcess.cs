using System;
using Game.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Game.Rendering
{
    /// <summary>
    /// The PSX look: point-downsample the frame to a low internal resolution, crush
    /// colors to 5-6-5 style bit depths with ordered Bayer dithering, then point-upscale.
    /// Injected After Post Process so tonemapped LDR colors are quantized and UI stays crisp.
    ///
    /// The internal resolution and dither strength come from the user's settings
    /// (<see cref="SettingsService"/>); the volume component decides whether the effect
    /// is on at all and the color depth (the artistic part).
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/Custom/PSX")]
    public sealed class PSXPostProcess : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        [Tooltip("Master switch for the PSX stack.")]
        public BoolParameter enabledEffect = new(false);

        [Tooltip("Color levels per channel. 63/127/63 keeps neon/emissive gradients smooth " +
                 "while still crushing subtly; drop toward 31/63/31 for harsher retro banding.")]
        public ClampedIntParameter redLevels = new(63, 2, 255);
        public ClampedIntParameter greenLevels = new(127, 2, 255);
        public ClampedIntParameter blueLevels = new(63, 2, 255);

        [Tooltip("Base dither amount; multiplied by the user's dither setting.")]
        public ClampedFloatParameter dither = new(0.5f, 0f, 2f);

        public override CustomPostProcessInjectionPoint injectionPoint =>
            CustomPostProcessInjectionPoint.AfterPostProcess;

        private Material _material;
        private static readonly int LowResId = Shader.PropertyToID("_PSXLowResTemp");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int LowResTexId = Shader.PropertyToID("_PSXLowRes");
        private static readonly int ParamsId = Shader.PropertyToID("_PSXParams");
        private static readonly int LowResSizeId = Shader.PropertyToID("_PSXLowResSize");

        public bool IsActive() => enabledEffect.value && _material != null;

        public override void Setup()
        {
            var shader = Resources.Load<Shader>("Shaders/PSXPost");
            if (shader == null)
            {
                Debug.LogError("[PSXPostProcess] Shader Resources/Shaders/PSXPost not found.");
                return;
            }
            _material = CoreUtils.CreateEngineMaterial(shader);
        }

        public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
        {
            if (_material == null) return;

            float userDither = Application.isPlaying ? SettingsService.Data.ditherStrength : 1f;
            int internalHeight = Application.isPlaying ? SettingsService.Data.psxInternalHeight : 360;

            _material.SetVector(ParamsId, new Vector4(
                redLevels.value, greenLevels.value, blueLevels.value, dither.value * userDither));
            _material.SetTexture(MainTexId, source);

            if (internalHeight <= 0 || internalHeight >= camera.actualHeight)
            {
                HDUtils.DrawFullScreen(cmd, _material, destination, null, 2);
                return;
            }

            int lowW = Mathf.Max(2, Mathf.RoundToInt(camera.actualWidth * (internalHeight / (float)camera.actualHeight)));
            int lowH = internalHeight;
            _material.SetVector(LowResSizeId, new Vector4(lowW, lowH, 1f / lowW, 1f / lowH));

            cmd.GetTemporaryRT(LowResId, lowW, lowH, 0, FilterMode.Point, RenderTextureFormat.ARGB32);
            cmd.SetRenderTarget(LowResId);
            CoreUtils.DrawFullScreen(cmd, _material, (MaterialPropertyBlock)null, 0);

            cmd.SetGlobalTexture(LowResTexId, LowResId);
            HDUtils.DrawFullScreen(cmd, _material, destination, null, 1);
            cmd.ReleaseTemporaryRT(LowResId);
        }

        public override void Cleanup()
        {
            CoreUtils.Destroy(_material);
            _material = null;
        }
    }
}
