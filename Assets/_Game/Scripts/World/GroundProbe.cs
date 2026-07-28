using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Resolves a "put the player here" position onto whatever solid surface is actually
    /// on top at that XZ.
    ///
    /// Authored spawn points and saved positions are both fixed heights, but the walkable
    /// surface is not: snow accumulates and melts, so a Y that was correct when authored
    /// (or saved) can end up buried inside the snow volume - which reads as falling
    /// through the floor. Probing at placement time makes both paths depth-agnostic.
    /// </summary>
    public static class GroundProbe
    {
        private const float PROBE_UP = 8f;
        private const float PROBE_LENGTH = 40f;
        private const float CLEARANCE = 0.05f;

        /// <summary>
        /// Returns <paramref name="desired"/> with its Y dropped onto the first surface
        /// found below. Falls back to the input unchanged when nothing is hit, so an
        /// off-mesh position is never silently teleported to the void.
        /// </summary>
        public static Vector3 ResolveStandingPosition(Vector3 desired, int layerMask = ~0)
        {
            Vector3 from = desired + Vector3.up * PROBE_UP;
            if (Physics.Raycast(from, Vector3.down, out var hit, PROBE_LENGTH, layerMask,
                    QueryTriggerInteraction.Ignore))
                return new Vector3(desired.x, hit.point.y + CLEARANCE, desired.z);
            return desired;
        }
    }
}
