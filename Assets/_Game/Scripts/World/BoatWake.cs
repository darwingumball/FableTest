using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Game.World
{
    /// <summary>
    /// Scales a boat's wake with how fast it is actually going.
    ///
    /// Without this the decals are constant: a boat tied up with its engine off still
    /// churned a full wake, which is the single thing that most gives away that a wake is
    /// painted on rather than made. Amplitude and foam both fall to nothing at rest.
    ///
    /// Speed comes from <see cref="BoatMotion.Speed"/> - real movement, not throttle - so
    /// this is right while coasting with the engine off, right on autopilot, and right on
    /// every peer, since a client scales the wake by the motion of the hull it is actually
    /// looking at rather than by an input it never received.
    /// </summary>
    [RequireComponent(typeof(BoatMotion))]
    public class BoatWake : MonoBehaviour
    {
        [Tooltip("Bow wave. Deformation only - this is the water the hull pushes up in front " +
                 "of it, and it should not foam.")]
        [SerializeField] private WaterDecal bowWave;
        [Tooltip("Propeller wash: the narrow churned band directly astern. This is the one " +
                 "that foams.")]
        [SerializeField] private WaterDecal propWash;

        [Header("Response")]
        [Tooltip("Speed at which the wake is at full strength. Above this it stops growing.")]
        [SerializeField] private float fullEffectSpeed = 6f;
        [Tooltip("Below this the boat is treated as stopped and the wake is fully off, so " +
                 "drifting on the swell does not leave a trail.")]
        [SerializeField] private float minimumSpeed = 0.35f;
        [Tooltip("How fast the wake builds and dies. Water has inertia: a wake keeps going " +
                 "for a moment after the engine stops, and does not appear the instant it " +
                 "starts.")]
        [SerializeField] private float responseRate = 1.6f;

        [Header("Strength at full speed")]
        [SerializeField] private float bowAmplitude = 0.5f;
        [SerializeField] private float washAmplitude = 0.22f;
        [SerializeField, Range(0f, 1f)] private float washSurfaceFoam = 1f;
        [SerializeField, Range(0f, 1f)] private float washDeepFoam = 0.7f;

        private BoatMotion _boat;
        private float _level;

        private void Awake() => _boat = GetComponent<BoatMotion>();

        private void LateUpdate()
        {
            // After BoatMotion has moved the hull and republished Speed this frame.
            float target = _boat.Speed <= minimumSpeed
                ? 0f
                : Mathf.Clamp01(_boat.Speed / Mathf.Max(fullEffectSpeed, 0.01f));

            _level = Mathf.Lerp(_level, target, 1f - Mathf.Exp(-responseRate * Time.deltaTime));

            if (bowWave != null)
                bowWave.amplitude = bowAmplitude * _level;

            if (propWash != null)
            {
                propWash.amplitude = washAmplitude * _level;
                // Foam is written by a different shader pass that ignores amplitude, so the
                // dimmers have to be scaled separately or the trail stays at full strength
                // while the bow wave correctly flattens out.
                propWash.surfaceFoamDimmer = washSurfaceFoam * _level;
                propWash.deepFoamDimmer = washDeepFoam * _level;
            }
        }
    }
}
