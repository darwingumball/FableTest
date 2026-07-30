using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Keeps loose physics objects on a moving deck instead of letting the boat drive out from
    /// under them.
    ///
    /// THE PROBLEM IS THAT THE DECK NEVER PUSHES ANYTHING. <see cref="BoatMotion"/> writes the
    /// hull transform outright every frame, so as far as PhysX is concerned the deck is a
    /// kinematic collider that teleports - it has no velocity, and a body resting on it gets no
    /// friction from it. A crate on the deck therefore just stands still in world space while
    /// the boat slides forward underneath, which reads as the crate rolling aft at exactly the
    /// boat's speed and piling up against the transom. Nothing about the crate is wrong; it is
    /// being simulated in the wrong frame of reference.
    ///
    /// So the missing force is supplied here: anything loose inside the deck volume has its
    /// HORIZONTAL velocity eased toward the velocity the deck has at that point, rotation
    /// included. That is deliberately a soft pull rather than a hard snap - a crate can still
    /// be shoved, still slides when the boat turns hard, and still slithers about in a swell.
    /// It simply stops treating the boat as a passing conveyor belt.
    ///
    /// Vertical is left completely alone. Falling, settling and floating belong to gravity and
    /// <see cref="Buoyancy"/>, and a crate that could not fall would be worse than one that
    /// slides.
    ///
    /// Owner-only, like <see cref="Buoyancy"/>: world items are owner-authoritative, so every
    /// peer writing velocities would fight the transform sync.
    /// </summary>
    public class DeckCargoCarry : MonoBehaviour
    {
        [Header("Volume (boat-local)")]
        [Tooltip("Region loose cargo is carried in. Normally the same box as BoatRiderCarry's - " +
                 "anywhere a player can stand is somewhere a crate can end up.")]
        [SerializeField] private Vector3 deckCenter = new(0f, 1.6f, -0.5f);
        [SerializeField] private Vector3 deckSize = new(6.2f, 9f, 19f);

        [Header("Grip")]
        [Tooltip("How fast a loose body is pulled onto the deck's own velocity, per second.\n\n" +
                 "This is the whole feel knob. High is a crate bolted down; low is a crate on " +
                 "ice. Around 6 leaves things sliding a little under acceleration and in a " +
                 "turn, then settling - which is what cargo on a wet steel deck actually does.")]
        [SerializeField] private float grip = 6f;

        [Tooltip("Ignore the deck velocity if it exceeds this. A hull that teleports - a boat " +
                 "respawning, the mooring settling on its first frame - would otherwise fire " +
                 "every crate on deck over the horizon.")]
        [SerializeField] private float maxDeckSpeed = 16f;

        [SerializeField] private LayerMask cargoMask = ~0;

        // Deliberately no [RequireComponent(typeof(BoatMotion))]. Requiring it means whichever
        // of these components a builder adds FIRST silently creates a defaults-only BoatMotion,
        // and two of those both write the hull transform every frame. That has already cost a
        // day once - see gotcha 55 in docs/PROGRESS.md.
        private BoatMotion _motion;

        private readonly Collider[] _hits = new Collider[48];
        private readonly HashSet<Rigidbody> _seen = new();
        private Vector3 _velocity;

        private void Awake()
        {
            _motion = GetComponent<BoatMotion>();
            if (_motion == null)
                Debug.LogWarning($"[DeckCargoCarry] No BoatMotion on '{name}' - " +
                                 "loose cargo will not be carried.");
        }

        // LateUpdate, and added to the boat AFTER BoatMotion so component order runs it second:
        // the deck's movement for this frame has to exist before it can be handed on.
        private void LateUpdate()
        {
            if (_motion == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Smoothed, because LastFrameDelta is a per-frame difference and dividing it by a
            // varying dt is noisy enough to buzz the cargo.
            Vector3 instant = _motion.LastFrameDelta / dt;
            _velocity = Vector3.Lerp(_velocity, instant, 1f - Mathf.Exp(-14f * dt));

            if (_velocity.magnitude > maxDeckSpeed) return;

            // Angular velocity about world up. Without this a crate stowed out on the rail
            // stays put while the boat turns underneath it and ends up over the side, which is
            // the same bug as the linear case in a different axis.
            Vector3 omega = Vector3.up * (_motion.LastFrameYawDelta / dt * Mathf.Deg2Rad);

            Vector3 worldCentre = transform.TransformPoint(deckCenter);
            int count = Physics.OverlapBoxNonAlloc(worldCentre, deckSize * 0.5f, _hits,
                transform.rotation, cargoMask, QueryTriggerInteraction.Ignore);

            float pull = 1f - Mathf.Exp(-grip * dt);
            _seen.Clear();

            for (int i = 0; i < count; i++)
            {
                var body = _hits[i].attachedRigidbody;
                if (body == null) continue;

                // Cargo lashed into a PlacementZone is kinematic and already rides the deck as
                // a child; the hull itself is kinematic too. Neither wants a velocity.
                if (body.isKinematic) continue;

                // A body with several colliders comes back once per collider.
                if (!_seen.Add(body)) continue;

                var netObject = body.GetComponent<NetworkObject>();
                if (netObject != null && netObject.IsSpawned && !netObject.IsOwner) continue;

                Vector3 deckVelocity = _velocity + Vector3.Cross(omega, body.position - transform.position);

                // Horizontal only, and expressed as the part of the body's motion that is NOT
                // shared with the deck. Easing that toward zero is what "rides along but can
                // still slide" means.
                Vector3 relative = body.linearVelocity - deckVelocity;
                relative.y = 0f;
                body.linearVelocity -= relative * pull;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.3f, 0.8f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(deckCenter, deckSize);
        }
    }
}
