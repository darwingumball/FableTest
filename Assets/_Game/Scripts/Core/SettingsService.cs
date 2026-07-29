using System;
using System.IO;
using Game.Net;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace Game.Core
{
    /// <summary>All user-tweakable settings, persisted as JSON.</summary>
    [Serializable]
    public class SettingsData
    {
        // Graphics
        public int qualityLevel = 0;             // HDRP quality asset index
        public int resWidth = 0, resHeight = 0;  // 0 = leave at current
        public int windowMode = 0;               // 0 borderless, 1 exclusive, 2 windowed
        public int vsync = 1;
        public int targetFps = -1;               // -1 = uncapped
        public float fov = 75f;
        public int psxInternalHeight = 360;      // PSX post process target (0 = native) - applied in Phase 5
        public int antiAliasing = 0;             // HDAdditionalCameraData.AntialiasingMode
        public int textureMipLimit = 0;          // 0 full res
        public bool motionBlur = false;          // consumed by volume setup later
        public bool filmGrain = false;
        public float ditherStrength = 1f;        // PSX post process

        // Audio (music/sfx buses consumed once the mixer lands; master applies now)
        public float masterVolume = 1f, musicVolume = 0.8f, sfxVolume = 1f;

        // Controls
        public float mouseSensitivity = 1f;
        public bool invertY = false;
        public string bindingOverrides = "";
    }

    /// <summary>
    /// Loads/saves <see cref="SettingsData"/> and pushes it into the engine: global
    /// graphics state immediately, per-player state (FOV, sensitivity, AA) whenever a
    /// local player spawns. UI writes to <see cref="Data"/> and calls the Apply methods.
    /// </summary>
    public static class SettingsService
    {
        public static SettingsData Data { get; private set; } = new SettingsData();

        public static event Action OnApplied;

        private static string FilePath => Path.Combine(Application.persistentDataPath, "settings.json");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Data = new SettingsData();
            OnApplied = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            Load();
            ApplyGraphicsGlobal();
            ApplyAudio();
            NetworkPlayer.OnLocalPlayerReady += ApplyToPlayer;
            if (NetworkPlayer.Local != null) ApplyToPlayer(NetworkPlayer.Local);
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    Data = JsonUtility.FromJson<SettingsData>(File.ReadAllText(FilePath)) ?? new SettingsData();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SettingsService] Failed to load settings, using defaults: {ex.Message}");
                Data = new SettingsData();
            }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(Data, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SettingsService] Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>Engine-wide graphics state. Safe to call from any scene.</summary>
        public static void ApplyGraphicsGlobal()
        {
            if (Data.qualityLevel >= 0 && Data.qualityLevel < QualitySettings.names.Length
                && QualitySettings.GetQualityLevel() != Data.qualityLevel)
                QualitySettings.SetQualityLevel(Data.qualityLevel, true);

            QualitySettings.vSyncCount = Mathf.Clamp(Data.vsync, 0, 2);
            Application.targetFrameRate = Data.targetFps;
            QualitySettings.globalTextureMipmapLimit = Mathf.Clamp(Data.textureMipLimit, 0, 3);

            var mode = Data.windowMode switch
            {
                1 => FullScreenMode.ExclusiveFullScreen,
                2 => FullScreenMode.Windowed,
                _ => FullScreenMode.FullScreenWindow,
            };
            int w = Data.resWidth > 0 ? Data.resWidth : Screen.width;
            int h = Data.resHeight > 0 ? Data.resHeight : Screen.height;
            if (w != Screen.width || h != Screen.height || mode != Screen.fullScreenMode)
                Screen.SetResolution(w, h, mode);

            OnApplied?.Invoke();
        }

        public static void ApplyAudio()
        {
            AudioListener.volume = Mathf.Clamp01(Data.masterVolume);
            // musicVolume / sfxVolume are read by their players when the audio pass lands.
        }

        /// <summary>Per-player settings; hooked to NetworkPlayer.OnLocalPlayerReady.</summary>
        public static void ApplyToPlayer(NetworkPlayer player)
        {
            if (player == null) return;

            if (player.PlayerCamera != null)
            {
                player.PlayerCamera.fieldOfView = Mathf.Clamp(Data.fov, 50f, 110f);
                var hdData = player.PlayerCamera.GetComponent<HDAdditionalCameraData>();
                if (hdData != null)
                    hdData.antialiasing = (HDAdditionalCameraData.AntialiasingMode)Data.antiAliasing;
            }

            if (player.Controller != null)
            {
                player.Controller.SetSensitivityScale(Data.mouseSensitivity);
                player.Controller.SetInvertY(Data.invertY);
                ApplyBindingOverrides(player.Controller.InputActions);
            }
        }

        public static void ApplyBindingOverrides(InputActionAsset asset)
        {
            if (asset == null) return;
            try
            {
                asset.RemoveAllBindingOverrides();
                if (!string.IsNullOrEmpty(Data.bindingOverrides))
                    asset.LoadBindingOverridesFromJson(Data.bindingOverrides);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SettingsService] Bad binding overrides, clearing: {ex.Message}");
                Data.bindingOverrides = "";
            }
        }

        public static void StoreBindingOverrides(InputActionAsset asset)
        {
            if (asset == null) return;
            Data.bindingOverrides = asset.SaveBindingOverridesAsJson();
            Save();
        }
    }
}
