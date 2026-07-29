using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Somewhere cargo can be attached: a crane hook, a lashing area on a deck, a spot on a
    /// property floor. One base type for all of them, because from the cargo's point of view
    /// they are the same thing - "stop being physics, ride this transform instead".
    ///
    /// IDENTITY OVER THE NETWORK is the whole reason this class exists. An anchor is named by
    /// the <see cref="NetworkObject"/> above it plus its index in that object's anchor list,
    /// and that pair is all <see cref="CargoAttachment"/> ever sends. The index is derived
    /// from hierarchy order rather than authored, so it cannot drift out of sync with a
    /// rebuilt scene the way a hand-assigned id would - every peer walks the same prefab or
    /// the same scene file and counts the same anchors in the same order.
    ///
    /// Nothing here is replicated. Anchors are scene furniture; only the cargo's claim on one
    /// travels.
    /// </summary>
    public abstract class CargoAnchor : MonoBehaviour
    {
        [Tooltip("Transform attached cargo is parented to. Defaults to this object.\n\n" +
                 "On a boat this must hang off the LEVEL root, never the rolling hull: a " +
                 "CharacterController stays world-upright, so cargo that rolled with the " +
                 "visual hull would slide out from under the player standing on it.")]
        [SerializeField] private Transform attachRoot;

        public Transform AttachRoot => attachRoot != null ? attachRoot : transform;

        private readonly List<CargoAttachment> _attached = new();

        /// <summary>
        /// What is currently riding this anchor, on every peer. Maintained by
        /// <see cref="CargoAttachment"/> as it applies replicated state, so a client can ask
        /// "what is on the hook" without the crane replicating it a second time.
        /// </summary>
        public IReadOnlyList<CargoAttachment> Attached => _attached;

        public CargoAttachment First => _attached.Count > 0 ? _attached[0] : null;

        internal void Register(CargoAttachment cargo)
        {
            if (!_attached.Contains(cargo)) _attached.Add(cargo);
        }

        internal void Unregister(CargoAttachment cargo) => _attached.Remove(cargo);

        // ---------------- network identity ----------------

        /// <summary>The NetworkObject this anchor is named relative to. Null if unspawned.</summary>
        public NetworkObject Host => GetComponentInParent<NetworkObject>();

        /// <summary>
        /// This anchor's index under its host. Recomputed on demand rather than cached in
        /// Awake, because cargo can resolve an anchor before that anchor's Awake has run -
        /// a late joiner receives attachment state in whatever order the spawn messages
        /// happen to arrive.
        /// </summary>
        public int Index
        {
            get
            {
                var host = Host;
                if (host == null) return -1;
                var all = host.GetComponentsInChildren<CargoAnchor>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] == this) return i;
                return -1;
            }
        }

        public static CargoAnchor Find(NetworkObjectReference hostRef, int index)
        {
            if (index < 0 || !hostRef.TryGet(out NetworkObject host) || host == null) return null;
            var all = host.GetComponentsInChildren<CargoAnchor>(true);
            return index < all.Length ? all[index] : null;
        }

        /// <summary>Converts a world pose into the local pose that gets replicated.</summary>
        public void WorldToLocal(Vector3 worldPosition, Quaternion worldRotation,
            out Vector3 localPosition, out Quaternion localRotation)
        {
            Transform root = AttachRoot;
            localPosition = root.InverseTransformPoint(worldPosition);
            localRotation = Quaternion.Inverse(root.rotation) * worldRotation;
        }
    }
}
