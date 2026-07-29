using System.Collections.Generic;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// A region where cargo stops being physics and starts being part of whatever it is
    /// sitting on: the lashing area on a deck, the floor of a property.
    ///
    /// The rule Evan asked for, and the reason this is not just a trigger volume: entering
    /// the zone does NOT attach anything. It only makes a plan - a snapped pose plus a
    /// green/red verdict - which the carrier shows as a ghost and commits on RELEASE. Drop
    /// something on a red verdict and it stays loose and falls where it was. That is what
    /// separates "put the crate down over there" from "the deck ate my crate".
    ///
    /// PLANNING IS DETERMINISTIC AND PURE. <see cref="Plan"/> reads the world and returns a
    /// pose; it never moves anything. The carrying client calls it every frame for the ghost
    /// and the server calls it once on release to decide what actually happens, so both
    /// arrive at the same answer without the client being trusted for the result.
    /// </summary>
    public class PlacementZone : CargoAnchor
    {
        [Header("Region (zone-local, metres)")]
        [SerializeField] private Vector3 center = Vector3.zero;
        [Tooltip("Y is the headroom cargo may be stacked into, measured about the centre.")]
        [SerializeField] private Vector3 size = new(5f, 3f, 6f);

        [Header("Snapping")]
        [Tooltip("Grid the footprint centre snaps to. 0 for free placement.\n\n" +
                 "Small values only take the jitter out of a hand-held pose; large ones make " +
                 "cargo tile. Yaw is always snapped to 90 degrees regardless - cargo lashed " +
                 "askew on a deck reads as a mistake, and a square footprint makes the " +
                 "overlap test exact instead of conservative.")]
        [SerializeField, Min(0f)] private float cellSize = 0.25f;

        [Tooltip("What blocks a placement and what cargo may rest on. Leave as everything " +
                 "unless props start refusing to sit on their own deck.")]
        [SerializeField] private LayerMask blockMask = ~0;

        [Header("Presentation")]
        [Tooltip("Optional child geometry marking the border, so players can see where cargo " +
                 "is allowed before they are holding any.")]
        [SerializeField] private GameObject outline;

        private static readonly List<PlacementZone> Active = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Active.Clear();

        private void OnEnable() => Active.Add(this);
        private void OnDisable() => Active.Remove(this);

        public bool ShowOutline
        {
            get => outline != null && outline.activeSelf;
            set { if (outline != null) outline.SetActive(value); }
        }

        // ---------------- lookup ----------------

        /// <summary>The zone a point falls inside, or null. First match wins; zones are not
        /// expected to overlap, and if they do, either answer is as good as the other.</summary>
        public static PlacementZone Find(Vector3 worldPoint)
        {
            for (int i = 0; i < Active.Count; i++)
                if (Active[i].Contains(worldPoint)) return Active[i];
            return null;
        }

        public bool Contains(Vector3 worldPoint)
        {
            Vector3 local = ToZone(worldPoint) - center;
            Vector3 half = size * 0.5f;
            return Mathf.Abs(local.x) <= half.x
                && Mathf.Abs(local.y) <= half.y
                && Mathf.Abs(local.z) <= half.z;
        }

        // Scale-free conversions. InverseTransformPoint would divide by the zone's lossy
        // scale, so a zone that someone scaled in the inspector would quietly shrink its own
        // region while the gizmo kept drawing the authored size.
        private Vector3 ToZone(Vector3 world) =>
            Quaternion.Inverse(transform.rotation) * (world - transform.position);

        private Vector3 ToWorld(Vector3 zoneLocal) =>
            transform.position + transform.rotation * zoneLocal;

        // ---------------- planning ----------------

        /// <summary>
        /// Where <paramref name="cargo"/> would end up if it were released here, and whether
        /// that is allowed. Always outputs a pose - a red ghost still has to be drawn
        /// somewhere, and drawing it at the rejected position is what tells the player *why*
        /// it is red.
        /// </summary>
        public bool Plan(GameObject cargo, Vector3 desiredPosition, float desiredYaw,
            out Vector3 worldPosition, out Quaternion worldRotation)
        {
            Bounds own = CargoBounds.InOwnFrame(cargo);

            // Yaw to the nearest quarter turn of the zone's own heading.
            float zoneYaw = transform.eulerAngles.y;
            float snapped = Mathf.Round(Mathf.DeltaAngle(zoneYaw, desiredYaw) / 90f) * 90f;
            worldRotation = Quaternion.Euler(0f, zoneYaw + snapped, 0f);

            // Everything below is in zone space, relative to the region centre.
            Quaternion relative = Quaternion.Inverse(transform.rotation) * worldRotation;
            Vector3 offset = relative * own.center;         // origin -> collider centroid
            Vector3 extents = AxisExtents(relative, own.extents);

            Vector3 half = size * 0.5f;
            Vector3 desiredLocal = ToZone(desiredPosition) - center;

            // Footprint centre, snapped then held inside the region. A cargo bigger than the
            // zone cannot be legal at any position, and the clamp below would invert, so bail
            // on the geometry before doing the work.
            var footprint = new Vector2(desiredLocal.x + offset.x, desiredLocal.z + offset.z);
            if (cellSize > 0f)
            {
                footprint.x = Mathf.Round(footprint.x / cellSize) * cellSize;
                footprint.y = Mathf.Round(footprint.y / cellSize) * cellSize;
            }

            bool fits = extents.x <= half.x + 1e-3f && extents.z <= half.z + 1e-3f;
            if (fits)
            {
                footprint.x = Mathf.Clamp(footprint.x, -half.x + extents.x, half.x - extents.x);
                footprint.y = Mathf.Clamp(footprint.y, -half.z + extents.z, half.z - extents.z);
            }

            // Rest height: sweep the real footprint down through the region and land on the
            // first thing it meets. A downward sweep rather than a fixed deck height is what
            // gives stacking for free - the second crate lands on the first one's lid.
            bool supported = TrySupportHeight(cargo, footprint, extents, out float supportY);

            float originY = supportY + extents.y - offset.y;
            worldPosition = ToWorld(center + new Vector3(footprint.x - offset.x, originY,
                                                        footprint.y - offset.z));

            if (!fits || !supported) return false;

            // Stacked out of the top of the region. Without this, cargo could be piled
            // arbitrarily high off a deck that only claims three metres of headroom.
            if (supportY + extents.y * 2f > half.y + 1e-3f) return false;

            // Clear of anything already there. Shrunk deliberately: at full size the box is
            // exactly touching whatever it is resting on, and every legal placement would
            // report itself blocked by its own support.
            Vector3 testCentre = worldPosition + worldRotation * own.center;
            var hits = Physics.OverlapBox(testCentre, extents * 0.9f, transform.rotation,
                blockMask, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
                if (!IsPartOf(hit, cargo)) return false;

            return true;
        }

        private bool TrySupportHeight(GameObject cargo, Vector2 footprint, Vector3 extents,
            out float supportY)
        {
            supportY = -size.y * 0.5f;

            // Start above the region and sweep its full height plus a little, so cargo held
            // above the coaming still finds the deck underneath.
            const float probeSkin = 0.02f;
            float top = size.y * 0.5f + 0.5f;
            Vector3 start = ToWorld(center + new Vector3(footprint.x, top, footprint.y));
            var castExtents = new Vector3(Mathf.Max(extents.x - 0.01f, 0.01f), probeSkin,
                                          Mathf.Max(extents.z - 0.01f, 0.01f));
            float distance = size.y + 1f;

            var hits = Physics.BoxCastAll(start, castExtents, Vector3.down, transform.rotation,
                distance, blockMask, QueryTriggerInteraction.Ignore);

            bool found = false;
            float best = float.MaxValue;
            foreach (var hit in hits)
            {
                // The cargo being placed is usually still hanging in the sweep - off a crane
                // hook or in someone's hands - and would otherwise land on itself.
                if (IsPartOf(hit.collider, cargo)) continue;
                // A zero distance means the sweep started already overlapping, which gives no
                // usable surface normal or depth.
                if (hit.distance <= 0f) continue;
                if (hit.distance < best) { best = hit.distance; found = true; }
            }

            if (!found) return false;

            float surfaceWorldY = start.y - best - probeSkin;
            supportY = ToZone(new Vector3(start.x, surfaceWorldY, start.z)).y - center.y;
            return true;
        }

        private static bool IsPartOf(Collider collider, GameObject root) =>
            collider != null && collider.transform.IsChildOf(root.transform);

        /// <summary>
        /// Half-extents of a rotated box's axis-aligned bound. Exact for the quarter turns
        /// placement actually uses, and correct for anything else, which keeps the overlap
        /// test honest if a caller ever passes an unsnapped rotation.
        /// </summary>
        private static Vector3 AxisExtents(Quaternion rotation, Vector3 extents)
        {
            Matrix4x4 m = Matrix4x4.Rotate(rotation);
            return new Vector3(
                Mathf.Abs(m.m00) * extents.x + Mathf.Abs(m.m01) * extents.y + Mathf.Abs(m.m02) * extents.z,
                Mathf.Abs(m.m10) * extents.x + Mathf.Abs(m.m11) * extents.y + Mathf.Abs(m.m12) * extents.z,
                Mathf.Abs(m.m20) * extents.x + Mathf.Abs(m.m21) * extents.y + Mathf.Abs(m.m22) * extents.z);
        }

        // ---------------- editor ----------------

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.9f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(center, size);
        }
    }
}
