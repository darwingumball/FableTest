using System.Collections.Generic;
using Game.Net;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Game.World
{
    /// <summary>
    /// A scene light whose output is authored once and then adjusted at runtime.
    ///
    /// Two problems this solves. First, a fixture tuned to read at noon is far too weak
    /// once the sun is down, because exposure follows the day curve - so every practical
    /// gets a night boost driven by the same curve. Second, retuning a district meant
    /// editing dozens of serialized intensities and re-entering play mode; lights now join
    /// a named <see cref="Group"/> that the console can scale live (`light warehouse 2`).
    ///
    /// Intensity is written in the order HDRP requires: range and cone first, then unit,
    /// then value. Writing the value through SetIntensity() before the shape is final
    /// applies the lumen conversion twice.
    /// </summary>
    [RequireComponent(typeof(Light))]
    public class PracticalLight : MonoBehaviour
    {
        [Tooltip("Console handle. Lights sharing a group scale together.")]
        [SerializeField] private string group = "default";
        [Tooltip("Authored output in lumens, at full daylight.")]
        [SerializeField] private float baseLumens = 20000f;
        [Tooltip("Multiplier applied at full night. 1 = no day/night response. Exterior " +
                 "fixtures want 2-4; interiors that are always on want ~1.")]
        [SerializeField] private float nightBoost = 2.5f;

        public string Group => group;

        private static readonly Dictionary<string, float> GroupScale = new();
        private static readonly List<PracticalLight> All = new();

        private HDAdditionalLightData _hd;
        private float _applied = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            GroupScale.Clear();
            All.Clear();
        }

        /// <summary>Scales a group, or every light when group is "all". Console `light`.</summary>
        public static string SetGroupScale(string groupName, float scale)
        {
            if (string.Equals(groupName, "all", System.StringComparison.OrdinalIgnoreCase))
            {
                var names = new List<string>();
                foreach (var light in All)
                    if (light != null && !names.Contains(light.group)) names.Add(light.group);
                foreach (var name in names) GroupScale[name] = scale;
                return $"Scaled {names.Count} group(s) to x{scale:0.##}.";
            }

            GroupScale[groupName] = scale;
            int count = 0;
            foreach (var light in All)
                if (light != null && light.group == groupName) count++;
            return count == 0
                ? $"No lights in group '{groupName}'. Try: light list"
                : $"Group '{groupName}' ({count} lights) -> x{scale:0.##}.";
        }

        public static string DescribeGroups()
        {
            var counts = new Dictionary<string, int>();
            foreach (var light in All)
            {
                if (light == null) continue;
                counts.TryGetValue(light.group, out int c);
                counts[light.group] = c + 1;
            }
            if (counts.Count == 0) return "No practical lights in the scene.";

            var sb = new System.Text.StringBuilder("Light groups:");
            foreach (var kvp in counts)
            {
                GroupScale.TryGetValue(kvp.Key, out float scale);
                sb.Append($"\n  {kvp.Key}: {kvp.Value} lights, x{(scale <= 0f ? 1f : scale):0.##}");
            }
            return sb.ToString();
        }

        private void Awake()
        {
            _hd = GetComponent<HDAdditionalLightData>();
            All.Add(this);
        }

        private void OnDestroy() => All.Remove(this);

        private void LateUpdate()
        {
            if (_hd == null) return;

            float scale = GroupScale.TryGetValue(group, out float s) && s > 0f ? s : 1f;
            float dayBlend = NetworkTimeSync.Instance != null
                ? SunController.DayBlend01(NetworkTimeSync.Instance.HourOfDay)
                : 1f;
            // Full boost at night (dayBlend 0), authored value at noon (dayBlend 1).
            float target = baseLumens * scale * Mathf.Lerp(nightBoost, 1f, dayBlend);

            // Lights change slowly with the clock; skip writes that would not be visible.
            if (Mathf.Abs(target - _applied) < Mathf.Max(target * 0.01f, 1f)) return;
            _applied = target;
            _hd.lightUnit = LightUnit.Lumen;
            _hd.intensity = target;
        }
    }
}
