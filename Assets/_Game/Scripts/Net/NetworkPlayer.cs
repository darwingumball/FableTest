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
        private bool _placed;
        private float _placeDeadline;

        [Header("Spawn placement")]
        [Tooltip("How long to wait for the gameplay scene's spawn point before giving up " +
                 "and handing control over wherever the player currently is.")]
        [SerializeField] private float spawnPointWaitTimeout = 10f;

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

            // The owner's controller stays off until the player has been placed - see
            // TryPlaceAtSpawn. Remote copies never run it at all.
            if (firstPersonController != null) firstPersonController.enabled = false;
            if (interactionSystem != null) interactionSystem.enabled = owner;
            if (playerCamera != null) playerCamera.gameObject.SetActive(owner);
            if (audioListener != null) audioListener.enabled = owner;

            if (owner)
            {
                Local = this;
                SuppressOtherAudioListeners();
                _placeDeadline = Time.time + spawnPointWaitTimeout;
                TryPlaceAtSpawn();
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

        /// <summary>
        /// Places the owner on the gameplay scene's spawn point, retrying until it exists.
        ///
        /// NGO spawns the player object as soon as the connection is approved, which on the
        /// host is before NetworkSceneManager has finished loading the gameplay scene. At
        /// that moment there is no spawn point and no ground at all, so a one-shot attempt
        /// silently does nothing and the player free-falls through an empty scene. Movement
        /// is held off until placement succeeds so gravity can't run in the meantime.
        /// </summary>
        private void TryPlaceAtSpawn()
        {
            if (_placed) return;

            if (!PlayerSpawnPoint.TryGetSpawnPose(out var pos, out var rot))
            {
                // Scene still loading. Give up eventually rather than freezing forever.
                if (Time.time >= _placeDeadline)
                {
                    Debug.LogWarning("[NetworkPlayer] No PlayerSpawnPoint found; " +
                                     "releasing control at the current position.", this);
                    Release();
                }
                return;
            }

            // CharacterController fights teleports; disable around the warp. Disabling it
            // first also keeps our own capsule out of the ground probe below.
            var cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            // The authored spawn Y assumes bare ground. Lying snow raises the walkable
            // surface above it, so drop onto whatever is actually on top instead.
            transform.SetPositionAndRotation(GroundProbe.ResolveStandingPosition(pos), rot);
            if (cc != null) cc.enabled = true;
            Release();
        }

        private void Release()
        {
            _placed = true;
            if (firstPersonController == null) return;
            firstPersonController.enabled = true;
            firstPersonController.SyncRotationFromTransform();
        }

        private void Update()
        {
            if (IsOwner)
            {
                if (!_placed) TryPlaceAtSpawn();

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
