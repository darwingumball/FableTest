using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// The block at the end of the crane's rope. An anchor like any other, so a crate on the
    /// hook and a crate lashed to the deck are the same kind of thing to
    /// <see cref="CargoAttachment"/> - the only difference is that this anchor happens to
    /// swing around.
    ///
    /// It only ever reports what is within reach. Deciding to take a load is
    /// <see cref="CraneController"/>'s job, and only on the server.
    /// </summary>
    public class CraneHook : CargoAnchor
    {
        [Tooltip("How close a load has to be for the hook to reach it. Generous on purpose - " +
                 "the operator is judging this from twenty metres away down a rocking deck.")]
        [SerializeField] private float reach = 1.1f;

        [SerializeField] private LayerMask grabMask = ~0;

        public float Reach => reach;

        /// <summary>
        /// Nearest cargo the hook could take, or null.
        /// </summary>
        /// <param name="looseOnly">
        /// True for the automatic grab, which must only ever pick up things lying around.
        /// Cargo already lashed into a <see cref="PlacementZone"/> has been deliberately put
        /// there, so unlashing it takes a deliberate keypress - otherwise swinging the hook
        /// across a loaded deck would strip it.
        /// </param>
        public CargoAttachment FindTarget(bool looseOnly)
        {
            Vector3 origin = AttachRoot.position;
            var hits = Physics.OverlapSphere(origin, reach, grabMask, QueryTriggerInteraction.Ignore);

            CargoAttachment best = null;
            float bestDistance = float.MaxValue;

            foreach (var hit in hits)
            {
                var cargo = hit.GetComponentInParent<CargoAttachment>();
                if (cargo == null || !cargo.IsSpawned) continue;
                if (cargo.Anchor == this) continue;              // already ours
                if (looseOnly && cargo.IsAttached) continue;

                float distance = (cargo.transform.position - origin).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = cargo;
            }

            return best;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.8f);
            Gizmos.DrawWireSphere(AttachRoot.position, reach);
        }
    }
}
