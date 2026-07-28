using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Something that compresses snow (players, heavy props). Emits a stamp whenever it
    /// has moved far enough since the last one.
    /// </summary>
    public class SnowDeformer : MonoBehaviour
    {
        [Tooltip("Trail radius in meters.")]
        public float radius = 0.7f;
        [Range(0f, 1f)] public float depth = 1f;
        [Tooltip("Distance moved before another stamp is written.")]
        [SerializeField] private float stampSpacing = 0.2f;

        private Vector3 _lastStampPos;
        private bool _hasStamped;
        private bool _registered;

        private void OnEnable()
        {
            _hasStamped = false;
            _registered = false;
            TryRegister();
        }

        private void Update()
        {
            // Networked players are spawned by NGO and can exist before the scene's
            // deformation manager has run Awake, so registration has to keep retrying
            // instead of happening once and silently failing.
            if (!_registered) TryRegister();
        }

        private void TryRegister()
        {
            if (SnowDeformationManager.Instance == null) return;
            SnowDeformationManager.Instance.Register(this);
            _registered = true;
        }

        private void OnDisable()
        {
            if (SnowDeformationManager.Instance != null)
                SnowDeformationManager.Instance.Unregister(this);
            _registered = false;
        }

        public bool TryConsumeStamp(out Vector3 position)
        {
            position = transform.position;
            if (_hasStamped && Vector3.Distance(position, _lastStampPos) < stampSpacing)
                return false;
            _lastStampPos = position;
            _hasStamped = true;
            return true;
        }
    }
}
