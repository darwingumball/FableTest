using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Lets a loose physics object stop being one and ride a <see cref="CargoAnchor"/>
    /// instead - lashed to a deck, or hanging off a crane hook.
    ///
    /// WHAT TRAVELS IS THE CLAIM, NOT THE MOTION. All that goes on the wire is "I am on
    /// anchor N of network object H, at this local pose", written once when the state
    /// changes. Every peer then parents the object locally and lets the anchor's own
    /// transform carry it. A crate lashed to a boat therefore costs nothing per frame and
    /// cannot drift, which matters because <see cref="BoatMotion"/> deliberately replicates
    /// nothing at all - the boat is a function of server time, so a crate synced by absolute
    /// world position would be fighting a hull that each peer computes for itself.
    ///
    /// While attached the object is not networked physics in any sense: NetworkTransform,
    /// NetworkRigidbody and <see cref="Buoyancy"/> are all switched off and the body goes
    /// kinematic. It comes back the moment it is released, which is what makes cargo dropped
    /// over the side fall and splash like anything else.
    ///
    /// Requires <c>NetworkObject.AutoObjectParentSync = false</c>: the parenting here is
    /// deliberately outside NGO, and NGO would otherwise try to replicate and then undo it.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class CargoAttachment : NetworkBehaviour
    {
        public struct State : INetworkSerializable
        {
            public bool Attached;
            public NetworkObjectReference Host;
            public int AnchorIndex;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Attached);
                s.SerializeValue(ref Host);
                s.SerializeValue(ref AnchorIndex);
                s.SerializeValue(ref LocalPosition);
                s.SerializeValue(ref LocalRotation);
            }
        }

        private readonly NetworkVariable<State> _state = new(default,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Rigidbody _body;
        private NetworkTransform _netTransform;
        private NetworkRigidbody _netRigidbody;
        private Buoyancy _buoyancy;

        private CargoAnchor _anchor;
        private bool _dirty;
        private bool _simulated = true;

        public bool IsAttached => _state.Value.Attached;
        /// <summary>The anchor this is riding on THIS peer, or null. Never replicated.</summary>
        public CargoAnchor Anchor => _anchor;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _netTransform = GetComponent<NetworkTransform>();
            _netRigidbody = GetComponent<NetworkRigidbody>();
            _buoyancy = GetComponent<Buoyancy>();
        }

        public override void OnNetworkSpawn()
        {
            _state.OnValueChanged += OnStateChanged;
            _dirty = true;
        }

        public override void OnNetworkDespawn()
        {
            _state.OnValueChanged -= OnStateChanged;
            if (_anchor != null) { _anchor.Unregister(this); _anchor = null; }
        }

        private void OnStateChanged(State previous, State current) => _dirty = true;

        private void Update()
        {
            // Retried rather than applied once, because the host may not exist on this peer
            // yet: a late joiner receives the crate and the boat it is lashed to in whatever
            // order the spawn messages arrive, and the crate frequently wins.
            if (_dirty) Apply();
        }

        private void Apply()
        {
            var state = _state.Value;

            if (!state.Attached)
            {
                _dirty = false;
                if (_anchor != null) { _anchor.Unregister(this); _anchor = null; }
                if (transform.parent != null) transform.SetParent(null, worldPositionStays: true);
                SetSimulated(true);
                return;
            }

            var anchor = CargoAnchor.Find(state.Host, state.AnchorIndex);
            if (anchor == null) return;   // not resolvable yet - try again next frame

            _dirty = false;
            if (_anchor != anchor)
            {
                if (_anchor != null) _anchor.Unregister(this);
                _anchor = anchor;
                _anchor.Register(this);
            }

            // Stop simulating BEFORE reparenting, so PhysX is not asked to resolve a body
            // that just teleported several metres.
            SetSimulated(false);
            transform.SetParent(anchor.AttachRoot, worldPositionStays: false);
            transform.localPosition = state.LocalPosition;
            transform.localRotation = state.LocalRotation;
        }

        private void SetSimulated(bool simulated)
        {
            if (_simulated == simulated) return;
            _simulated = simulated;

            if (!simulated)
            {
                // Kill the velocity while the body is still dynamic - a kinematic body
                // refuses the write, and the stale velocity would be waiting on release.
                if (_body != null && !_body.isKinematic)
                {
                    _body.linearVelocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                }
                if (_netTransform != null) _netTransform.enabled = false;
                if (_netRigidbody != null) _netRigidbody.enabled = false;
                if (_buoyancy != null) _buoyancy.enabled = false;
                if (_body != null)
                {
                    _body.isKinematic = true;
                    // The anchor writes the transform outright; interpolating a kinematic
                    // child of a moving parent smears it a frame behind the deck.
                    _body.interpolation = RigidbodyInterpolation.None;
                }
                return;
            }

            if (_body != null)
            {
                _body.isKinematic = false;
                _body.interpolation = RigidbodyInterpolation.Interpolate;
            }
            // NetworkRigidbody re-asserts kinematic-on-non-owner for itself once enabled.
            if (_netRigidbody != null) _netRigidbody.enabled = true;
            if (_netTransform != null) _netTransform.enabled = true;
            if (_buoyancy != null) _buoyancy.enabled = true;
        }

        // ---------------- server ----------------

        public void ServerAttachLocal(CargoAnchor anchor, Vector3 localPosition, Quaternion localRotation)
        {
            if (!IsServer || anchor == null) return;

            var host = anchor.Host;
            int index = anchor.Index;
            if (host == null || !host.IsSpawned || index < 0)
            {
                Debug.LogWarning($"[CargoAttachment] '{name}' cannot attach to '{anchor.name}': " +
                                 "no spawned NetworkObject above it.");
                return;
            }

            _state.Value = new State
            {
                Attached = true,
                Host = new NetworkObjectReference(host),
                AnchorIndex = index,
                LocalPosition = localPosition,
                LocalRotation = localRotation,
            };
            _dirty = true;
        }

        public void ServerAttach(CargoAnchor anchor, Vector3 worldPosition, Quaternion worldRotation)
        {
            if (anchor == null) return;
            anchor.WorldToLocal(worldPosition, worldRotation, out var local, out var rotation);
            ServerAttachLocal(anchor, local, rotation);
        }

        public void ServerDetach()
        {
            if (!IsServer || !_state.Value.Attached) return;
            _state.Value = default;
            _dirty = true;
        }

        // ---------------- client requests ----------------

        /// <summary>
        /// Asks to be lashed down. The pose is a request, not an instruction: for a
        /// <see cref="PlacementZone"/> the server re-plans from scratch and refuses outright
        /// if the answer comes back red, so a client cannot place cargo inside a bulkhead by
        /// sending a pose of its own choosing.
        /// </summary>
        public void RequestAttach(CargoAnchor anchor, Vector3 worldPosition, Quaternion worldRotation)
        {
            if (!IsSpawned || anchor == null) return;
            if (IsServer) { ServerValidateAndAttach(anchor, worldPosition, worldRotation); return; }

            var host = anchor.Host;
            if (host == null || !host.IsSpawned) return;
            RequestAttachServerRpc(new NetworkObjectReference(host), anchor.Index,
                worldPosition, worldRotation);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestAttachServerRpc(NetworkObjectReference hostRef, int anchorIndex,
            Vector3 worldPosition, Quaternion worldRotation)
        {
            var anchor = CargoAnchor.Find(hostRef, anchorIndex);
            if (anchor != null) ServerValidateAndAttach(anchor, worldPosition, worldRotation);
        }

        private void ServerValidateAndAttach(CargoAnchor anchor, Vector3 worldPosition,
            Quaternion worldRotation)
        {
            if (anchor is PlacementZone zone)
            {
                if (!zone.Plan(gameObject, worldPosition, worldRotation.eulerAngles.y,
                        out var planned, out var plannedRotation))
                    return;
                ServerAttach(anchor, planned, plannedRotation);
                return;
            }
            ServerAttach(anchor, worldPosition, worldRotation);
        }

        public void RequestDetach()
        {
            if (!IsSpawned) return;
            if (IsServer) ServerDetach();
            else RequestDetachServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestDetachServerRpc() => ServerDetach();
    }
}
