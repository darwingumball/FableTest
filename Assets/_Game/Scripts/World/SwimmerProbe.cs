using Game.Player;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Samples the water under the local player and puts the controller into swim mode.
    ///
    /// The test is against a point partway up the body rather than the feet. Using the feet
    /// would start you swimming the moment you waded in ankle-deep, which is exactly where
    /// you still want to be walking; using the head would leave you walking along the
    /// bottom of the lake. Chest height is where "wading" turns into "swimming".
    /// </summary>
    [RequireComponent(typeof(FirstPersonController))]
    public class SwimmerProbe : MonoBehaviour
    {
        [Tooltip("Height up the body at which the water has to reach before swimming starts.")]
        [SerializeField] private float swimStartHeight = 1.15f;
        [Tooltip("Hysteresis so standing right at the waterline does not flicker between " +
                 "walking and swimming every frame.")]
        [SerializeField] private float exitMargin = 0.2f;

        private FirstPersonController _controller;
        private bool _swimming;

        private void Awake() => _controller = GetComponent<FirstPersonController>();

        private void Update()
        {
            var water = WaterVolume.Instance;
            if (water == null)
            {
                if (_swimming) SetSwimming(false, 0f, 0f);
                return;
            }

            if (!water.SampleSurface(transform.position, out float surfaceY, out _))
                return;   // simulation not ready - leave the current state alone

            // Metres of the body below the waterline, measured from the feet.
            float submersion = surfaceY - transform.position.y;

            // Enter once the water passes chest height; leave only after it drops a little
            // further, so standing exactly at the waterline does not flicker every frame.
            float threshold = _swimming ? swimStartHeight - exitMargin : swimStartHeight;
            bool shouldSwim = submersion >= threshold;

            if (shouldSwim != _swimming) SetSwimming(shouldSwim, submersion, surfaceY);
            else if (_swimming) _controller.SetSwimState(true, submersion, surfaceY);
        }

        private void SetSwimming(bool swimming, float submersion, float surfaceY)
        {
            _swimming = swimming;
            _controller.SetSwimState(swimming, submersion, surfaceY);
        }
    }
}
