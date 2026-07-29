using System.Collections.Generic;
using System.Text;
using Game.Rendering;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Game.Admin
{
    /// <summary>
    /// Frame cost readout and a bisection kit for finding what is actually expensive.
    ///
    /// Entirely LOCAL - unlike every other admin command this never goes near the server.
    /// Frame time is a property of the machine looking at the scene, and every toggle here
    /// affects only the local renderer, so routing it through the host would measure the
    /// wrong computer and change everyone's picture to answer one person's question.
    ///
    /// The point is the DELTA, not the absolute number. Stand still, read the ms, turn one
    /// thing off, read it again. Two measurements from the same spot are trustworthy in a
    /// way that a single number never is - which is why every toggle is reversible and
    /// <c>perf reset</c> puts the whole scene back.
    /// </summary>
    public class PerfProbe : MonoBehaviour
    {
        private static PerfProbe _instance;

        // ~1 s of history. Long enough to ride out a reflection probe capture (those spike
        // one frame hard and would otherwise read as a real regression), short enough that
        // turning something off shows up while you are still looking at the console.
        private const int SampleCount = 90;
        private readonly float[] _frameMs = new float[SampleCount];
        private int _sampleIndex;
        private int _samplesTaken;

        private ProfilerRecorder _drawCalls, _setPass, _triangles, _vertices;

        private readonly HashSet<string> _off = new();
        private readonly List<Light> _disabledLights = new();
        private readonly List<Light> _unshadowedLights = new();
        private readonly List<Renderer> _disabledRenderers = new();

        public static PerfProbe Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("~PerfProbe") { hideFlags = HideFlags.HideAndDontSave };
                _instance = go.AddComponent<PerfProbe>();
                DontDestroyOnLoad(go);
                return _instance;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        private void OnEnable()
        {
            // These are only populated in the editor and development builds. A release build
            // reports -1, which Report() prints as "n/a" rather than pretending it is zero.
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        }

        private void OnDisable()
        {
            _drawCalls.Dispose();
            _setPass.Dispose();
            _triangles.Dispose();
            _vertices.Dispose();
        }

        private void Update()
        {
            // Unscaled: a paused or slowed clock must not make the renderer look faster.
            _frameMs[_sampleIndex] = Time.unscaledDeltaTime * 1000f;
            _sampleIndex = (_sampleIndex + 1) % SampleCount;
            if (_samplesTaken < SampleCount) _samplesTaken++;
        }

        // ---------------- command ----------------

        public static string Execute(string[] args)
        {
            var probe = Instance;
            if (args.Length < 2) return probe.Report();

            string what = args[1].ToLowerInvariant();
            if (what == "reset") return probe.ResetAll();

            if (args.Length < 3) return Usage;
            bool on = args[2].ToLowerInvariant() is "on" or "1" or "true";

            switch (what)
            {
                case "lights": return probe.SetLights(on);
                case "shadows": return probe.SetShadows(on);
                case "fog": return probe.SetVolumeComponent<Fog>(on, "fog");
                case "clouds": return probe.SetClouds(on);
                case "post": return probe.SetVolumeComponent<PSXPostProcess>(on, "post");
                case "sky": return probe.SetVolumeComponent<PhysicallyBasedSky>(on, "sky");
                case "water": return probe.SetWater(on);
                case "snow": return probe.SetRenderers(on, "snow", "SnowGround");
                default: return Usage;
            }
        }

        private const string Usage =
            "usage: perf | perf reset | perf <lights|shadows|fog|clouds|post|sky|water|snow> on|off";

        private string Report()
        {
            var sb = new StringBuilder();

            float sum = 0f, worst = 0f;
            int n = Mathf.Max(_samplesTaken, 1);
            for (int i = 0; i < n; i++)
            {
                sum += _frameMs[i];
                if (_frameMs[i] > worst) worst = _frameMs[i];
            }
            float mean = sum / n;

            sb.Append($"frame {mean:0.0} ms ({(mean > 0f ? 1000f / mean : 0f):0} fps), " +
                      $"worst {worst:0.0} ms over {n} frames\n");
            sb.Append($"draws {Read(_drawCalls)}  setPass {Read(_setPass)}  " +
                      $"tris {Read(_triangles)}  verts {Read(_vertices)}\n");

            int lights = 0, shadowed = 0, volumetric = 0;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (!l.isActiveAndEnabled) continue;
                lights++;
                if (l.shadows != LightShadows.None) shadowed++;
                var hd = l.GetComponent<HDAdditionalLightData>();
                if (hd != null && hd.affectsVolumetric) volumetric++;
            }
            sb.Append($"live lights {lights} ({shadowed} shadowed, {volumetric} volumetric)\n");
            sb.Append(_off.Count == 0 ? "nothing disabled" : "DISABLED: " + string.Join(", ", _off));
            return sb.ToString();
        }

        private static string Read(ProfilerRecorder r) =>
            r.Valid && r.LastValue >= 0 ? r.LastValue.ToString("N0") : "n/a";

        // ---------------- toggles ----------------

        private string SetLights(bool on)
        {
            if (on)
            {
                foreach (var l in _disabledLights) if (l != null) l.enabled = true;
                int n = _disabledLights.Count;
                _disabledLights.Clear();
                _off.Remove("lights");
                return $"lights back on ({n})";
            }

            _disabledLights.Clear();
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                // Leave the directional lights alone - killing the sun does not isolate a
                // cost, it just makes the scene black and every other reading meaningless.
                if (l.type == LightType.Directional || !l.enabled) continue;
                l.enabled = false;
                _disabledLights.Add(l);
            }
            _off.Add("lights");
            return $"punctual lights off ({_disabledLights.Count}); sun and moon left alone";
        }

        private string SetShadows(bool on)
        {
            if (on)
            {
                foreach (var l in _unshadowedLights) if (l != null) l.shadows = LightShadows.Soft;
                int n = _unshadowedLights.Count;
                _unshadowedLights.Clear();
                _off.Remove("shadows");
                return $"shadows back on ({n})";
            }

            _unshadowedLights.Clear();
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.shadows == LightShadows.None) continue;
                l.shadows = LightShadows.None;
                _unshadowedLights.Add(l);
            }
            _off.Add("shadows");
            return $"shadows off ({_unshadowedLights.Count} lights)";
        }

        /// <summary>
        /// Flips a volume override off. Works on the shared profile, so it survives until
        /// something writes the asset - <c>perf reset</c> or leaving play mode.
        /// </summary>
        private string SetVolumeComponent<T>(bool on, string key) where T : VolumeComponent
        {
            int hits = 0;
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (v.sharedProfile == null) continue;
                if (!v.sharedProfile.TryGet(out T component)) continue;
                component.active = on;
                hits++;
            }
            if (hits == 0) return $"no {key} override found in any volume";
            if (on) _off.Remove(key); else _off.Add(key);
            return $"{key} {(on ? "on" : "off")} ({hits} volume(s))";
        }

        /// <summary>
        /// Volumetric clouds specifically, separate from fog. WeatherManager rebuilds its
        /// runtime profile from the preset every frame, so flipping the override alone is
        /// overwritten immediately - the suppression flag is what actually holds.
        /// </summary>
        private string SetClouds(bool on)
        {
            SuppressClouds = !on;
            int hits = 0;
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (v.profile == null) continue;
                if (!v.profile.TryGet(out VolumetricClouds clouds)) continue;
                clouds.enable.value = on;
                hits++;
            }
            if (on) _off.Remove("clouds"); else _off.Add("clouds");
            return $"volumetric clouds {(on ? "on" : "off")} ({hits} volume(s))";
        }

        /// <summary>
        /// Read by <see cref="Game.Net.WeatherManager"/> so its per-frame rewrite does not
        /// undo the toggle above.
        /// </summary>
        public static bool SuppressClouds { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSuppression() => SuppressClouds = false;

        private string SetWater(bool on)
        {
            int hits = 0;
            foreach (var w in FindObjectsByType<WaterSurface>(FindObjectsSortMode.None))
            {
                w.enabled = on;
                hits++;
            }
            if (on) _off.Remove("water"); else _off.Add("water");
            return $"water {(on ? "on" : "off")} ({hits} surface(s))";
        }

        private string SetRenderers(bool on, string key, string rootName)
        {
            if (on)
            {
                foreach (var r in _disabledRenderers) if (r != null) r.enabled = true;
                int n = _disabledRenderers.Count;
                _disabledRenderers.Clear();
                _off.Remove(key);
                return $"{key} back on ({n})";
            }

            var root = GameObject.Find(rootName);
            if (root == null) return $"no '{rootName}' in the scene";

            _disabledRenderers.Clear();
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                r.enabled = false;
                _disabledRenderers.Add(r);
            }
            _off.Add(key);
            return $"{key} off ({_disabledRenderers.Count} renderer(s))";
        }

        private string ResetAll()
        {
            SetLights(true);
            SetShadows(true);
            SetVolumeComponent<Fog>(true, "fog");
            SetVolumeComponent<PSXPostProcess>(true, "post");
            SetVolumeComponent<PhysicallyBasedSky>(true, "sky");
            SetClouds(true);
            SetWater(true);
            SetRenderers(true, "snow", "SnowGround");
            _off.Clear();
            return "everything back on";
        }
    }
}
