using Game.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// World-clock replication with zero periodic traffic: time of day is a pure function
    /// of NetworkManager.ServerTime and three anchor NetworkVariables, so any client -
    /// including late joiners - computes the identical clock every frame.
    ///
    ///   worldHours(t) = anchorWorldHours + (serverTime - anchorServerTime) * hoursPerSecond
    ///
    /// The server only writes the anchors on spawn, on admin "time set", or on speed change.
    /// </summary>
    public class NetworkTimeSync : NetworkBehaviour
    {
        public static NetworkTimeSync Instance { get; private set; }

        private readonly NetworkVariable<double> _anchorServerTime = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<double> _anchorWorldHours = new(8.0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<float> _hoursPerRealSecond = new(0.0133f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void Awake() => Instance = this;

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;
            float dayLengthMinutes = Mathf.Max(1f, SessionContext.Config?.dayLengthMinutes ?? 30f);
            _anchorServerTime.Value = NetworkManager.ServerTime.Time;
            _anchorWorldHours.Value = 8.0; // sessions start at 08:00 (saves override later)
            _hoursPerRealSecond.Value = 24f / (dayLengthMinutes * 60f);
        }

        /// <summary>Continuous world time in hours since day 0 (8.0 = 08:00 of day 0).</summary>
        public double WorldHours
        {
            get
            {
                if (!IsSpawned) return _anchorWorldHours.Value;
                double elapsed = NetworkManager.ServerTime.Time - _anchorServerTime.Value;
                return _anchorWorldHours.Value + elapsed * _hoursPerRealSecond.Value;
            }
        }

        public int Day => (int)(WorldHours / 24.0);
        public float HourOfDay => (float)(WorldHours % 24.0);

        /// <summary>Admin/save hook. Server only.</summary>
        public void ServerSetTime(double worldHours, float dayLengthMinutes = -1f)
        {
            if (!IsServer) return;
            _anchorServerTime.Value = NetworkManager.ServerTime.Time;
            _anchorWorldHours.Value = worldHours;
            if (dayLengthMinutes > 0f)
                _hoursPerRealSecond.Value = 24f / (dayLengthMinutes * 60f);
        }
    }
}
