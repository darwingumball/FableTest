using Game.Interaction;
using Game.Player;
using Game.World;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Per-client player root (the NGO player prefab). On spawn it enables the local-only
    /// components (input, camera, audio listener, interaction) for the owner and disables
    /// them on remote copies. Movement replicates via an owner-authoritative
    /// NetworkTransform on the same object; camera pitch replicates at low frequency for
    /// remote head aim.
    ///
    /// Deliberately NO reflection wiring and no scene-object hunting beyond the single
    /// spawn-point lookup - UI binds to <see cref="Local"/> via <see cref="OnLocalPlayerReady"/>.
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        [Header("Owner-only components")]
        [SerializeField] private FirstPersonController firstPersonController;
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private InteractionSystem interactionSystem;

        [Header("Remote head aim")]
        [SerializeField] private Transform cameraPivot;
        [SerializeField] private float pitchReplicateInterval = 0.1f;

        public static NetworkPlayer Local { get; private set; }
        public static event System.Action<NetworkPlayer> OnLocalPlayerReady;

        public PlayerStats Stats { get; private set; }
        public FirstPersonController Controller => firstPersonController;
        public Camera PlayerCamera => playerCamera;
        public InteractionSystem Interaction => interactionSystem;

        private readonly NetworkVariable<float> _cameraPitch = new(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        private float _nextPitchSend;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Local = null;
            OnLocalPlayerReady = null;
        }

        private void Awake()
        {
            Stats = GetComponent<PlayerStats>();
        }

        public override void OnNetworkSpawn()
        {
            bool owner = IsOwner;

            if (firstPersonController != null) firstPersonController.enabled = owner;
            if (interactionSystem != null) interactionSystem.enabled = owner;
            if (playerCamera != null) playerCamera.gameObject.SetActive(owner);
            if (audioListener != null) audioListener.enabled = owner;

            if (owner)
            {
                Local = this;
                SuppressOtherAudioListeners();
                MoveToSpawnPoint();
                OnLocalPlayerReady?.Invoke(this);
            }

            gameObject.name = owner ? "NetworkPlayer (Local)" : $"NetworkPlayer (client {OwnerClientId})";
        }

        public override void OnNetworkDespawn()
        {
            if (Local == this) Local = null;
        }

        /// <summary>
        /// The player can spawn while the menu scene (and its camera/listener) is still
        /// loaded, or alongside remote players' disabled listeners. Unity wants exactly
        /// one active listener - ours wins.
        /// </summary>
        private void SuppressOtherAudioListeners()
        {
            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener != audioListener && listener.enabled)
                    listener.enabled = false;
            }
        }

        private void MoveToSpawnPoint()
        {
            if (!PlayerSpawnPoint.TryGetSpawnPose(out var pos, out var rot)) return;
            // CharacterController fights teleports; disable around the warp.
            var cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            transform.SetPositionAndRotation(pos, rot);
            if (cc != null) cc.enabled = true;
            if (firstPersonController != null) firstPersonController.SyncRotationFromTransform();
        }

        private void Update()
        {
            if (IsOwner)
            {
                if (firstPersonController != null && Time.time >= _nextPitchSend)
                {
                    _nextPitchSend = Time.time + pitchReplicateInterval;
                    if (!Mathf.Approximately(_cameraPitch.Value, firstPersonController.CameraPitch))
                        _cameraPitch.Value = firstPersonController.CameraPitch;
                }
            }
            else if (cameraPivot != null)
            {
                // Cosmetic remote head pitch; NetworkTransform handles body yaw.
                var target = Quaternion.Euler(_cameraPitch.Value, 0f, 0f);
                cameraPivot.localRotation = Quaternion.Slerp(cameraPivot.localRotation, target, Time.deltaTime * 10f);
            }
        }
    }
}
