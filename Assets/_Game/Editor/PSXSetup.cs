using System.Linq;
using System.Reflection;
using Game.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Game.Editor
{
    /// <summary>
    /// Registers <see cref="PSXPostProcess"/> in HDRP's After Post Process order list
    /// (mandatory - unregistered custom post processes silently never run) and adds the
    /// override to the World volume profile. The orders-settings class is internal, so
    /// registration goes through reflection against the public list API.
    /// </summary>
    public static class PSXSetup
    {
        private const string PROFILE_PATH = "Assets/Settings/SkyandFogSettingsProfile.asset";
        private const string GLOBAL_SETTINGS_PATH = "Assets/Settings/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset";

        [MenuItem("Game/Setup/Register PSX Post Process")]
        public static void Register()
        {
            RegisterInGlobalSettings();
            AddToWorldProfile();
            AssetDatabase.SaveAssets();
            Debug.Log("[PSXSetup] PSX post process registered and added to the World volume profile.");
        }

        private static void RegisterInGlobalSettings()
        {
            var hdrpAssembly = typeof(CustomPostProcessVolumeComponent).Assembly;
            var ordersType = hdrpAssembly.GetType("UnityEngine.Rendering.HighDefinition.CustomPostProcessOrdersSettings");
            if (ordersType == null)
            {
                Debug.LogError("[PSXSetup] CustomPostProcessOrdersSettings type not found - HDRP version change?");
                return;
            }

            var getSettings = typeof(GraphicsSettings)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "GetRenderPipelineSettings" && m.IsGenericMethod && m.GetParameters().Length == 0);
            var orders = getSettings.MakeGenericMethod(ordersType).Invoke(null, null);
            if (orders == null)
            {
                Debug.LogError("[PSXSetup] Could not fetch CustomPostProcessOrdersSettings instance (is HDRP the active pipeline?).");
                return;
            }

            var list = ordersType.GetProperty("afterPostProcessCustomPostProcesses").GetValue(orders);
            string aqn = typeof(PSXPostProcess).AssemblyQualifiedName;
            bool contains = (bool)list.GetType().GetMethod("Contains", new[] { typeof(string) }).Invoke(list, new object[] { aqn });
            if (contains)
            {
                Debug.Log("[PSXSetup] Already registered.");
                return;
            }

            bool added = (bool)list.GetType().GetMethod("Add", new[] { typeof(string) }).Invoke(list, new object[] { aqn });
            if (!added)
            {
                Debug.LogError("[PSXSetup] Registration Add() returned false.");
                return;
            }

            var globalSettings = AssetDatabase.LoadAssetAtPath<RenderPipelineGlobalSettings>(GLOBAL_SETTINGS_PATH);
            if (globalSettings != null) EditorUtility.SetDirty(globalSettings);
            Debug.Log("[PSXSetup] Registered PSXPostProcess in After Post Process order.");
        }

        private static void AddToWorldProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PROFILE_PATH);
            if (profile == null)
            {
                Debug.LogError($"[PSXSetup] Volume profile not found at {PROFILE_PATH}.");
                return;
            }
            // Values already serialized in the profile do NOT pick up changed C# defaults,
            // so re-running this must push the current defaults onto an existing override
            // rather than bailing out.
            bool existed = profile.TryGet<PSXPostProcess>(out var component);
            if (!existed)
            {
                component = profile.Add<PSXPostProcess>(overrides: true);
                component.name = "PSXPostProcess";
            }

            var defaults = ScriptableObject.CreateInstance<PSXPostProcess>();
            component.enabledEffect.overrideState = true;
            component.enabledEffect.value = true;
            component.redLevels.value = defaults.redLevels.value;
            component.greenLevels.value = defaults.greenLevels.value;
            component.blueLevels.value = defaults.blueLevels.value;
            component.dither.value = defaults.dither.value;
            Object.DestroyImmediate(defaults);

            if (!existed) AssetDatabase.AddObjectToAsset(component, profile);
            EditorUtility.SetDirty(component);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PSXSetup] PSX override {(existed ? "updated" : "added")}: " +
                      $"levels {component.redLevels.value}/{component.greenLevels.value}/" +
                      $"{component.blueLevels.value}, dither {component.dither.value}.");
        }
    }
}
