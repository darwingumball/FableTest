using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Game.World
{
    /// <summary>
    /// Re-renders a realtime reflection probe on a slow timer.
    ///
    /// The world's lighting is not static - the sun moves, weather swaps the sky, neon is
    /// the dominant source at night - so a probe captured once at startup goes stale and
    /// reflections stop matching the scene. Rendering every frame is far too expensive for
    /// what is essentially a slow-changing environment, so this refreshes on an interval
    /// with a per-probe offset that keeps several probes from all firing on one frame.
    /// </summary>
    [RequireComponent(typeof(ReflectionProbe))]
    public class PeriodicReflectionProbe : MonoBehaviour
    {
        [Tooltip("Seconds between captures. Lighting here changes over minutes, not frames.")]
        [SerializeField] private float interval = 8f;
        [Tooltip("Randomised start delay so probes stagger instead of spiking together.")]
        [SerializeField] private float maxStagger = 4f;

        private HDAdditionalReflectionData _probe;
        private float _nextCapture;

        private void Awake()
        {
            _probe = GetComponent<HDAdditionalReflectionData>();
            _nextCapture = Time.time + Random.value * maxStagger;
        }

        private void Update()
        {
            if (_probe == null || Time.time < _nextCapture) return;
            _nextCapture = Time.time + interval;
            _probe.RequestRenderNextUpdate();
        }
    }
}
