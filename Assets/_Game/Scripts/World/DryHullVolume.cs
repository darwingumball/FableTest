using System.Collections.Generic;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// An air pocket inside a hull that sits below the waterline - a hold, a well deck, a
    /// submerged walkway. Marks the space as DRY for everything that otherwise decides
    /// wet-or-not purely from the water surface height overhead.
    ///
    /// HDRP splits that problem into two halves and only solves one of them for you:
    ///
    /// - THE SURFACE is handled by geometry. A mesh drawn with the water exclusion material
    ///   tags those pixels in the stencil buffer and the water surface is rejected there, so
    ///   the sea is not drawn across the inside of the compartment. That is what
    ///   <c>WaterExcluder</c> sets up, and it needs no code at runtime.
    ///
    /// - THE UNDERWATER EFFECT is not. For a finite water surface HDRP decides the camera is
    ///   submerged with a single <c>volumeBounds.bounds.Contains(cameraPosition)</c> - a box
    ///   test that knows nothing about excluders. Walk into a dry compartment a metre below
    ///   the waterline and the screen still floods with underwater fog and caustics.
    ///
    /// Gameplay has the same gap: <see cref="SwimmerProbe"/> compares the surface height to
    /// your chest, and the surface is above the deck plating whether or not there is a deck
    /// in between - so you would start swimming inside a sealed room.
    ///
    /// Both consult this registry. The volume is in LOCAL space and tested through the
    /// transform, so it rides the hull for free as the boat heaves, surges and turns.
    /// </summary>
    public class DryHullVolume : MonoBehaviour
    {
        [Tooltip("Interior air space, boat-local. Should match the inside of the exclusion " +
                 "mesh, not the outside of the hull.")]
        [SerializeField] private Vector3 center = Vector3.zero;
        [SerializeField] private Vector3 size = new(3f, 2.4f, 4f);

        private static readonly List<DryHullVolume> Active = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Active.Clear();

        private void OnEnable() => Active.Add(this);

        private void OnDisable() => Active.Remove(this);

        /// <summary>
        /// True when the point is inside any dry interior. Cheap enough to call per frame:
        /// there are single digits of these in a scene and each is three comparisons.
        /// </summary>
        public static bool ContainsPoint(Vector3 worldPoint)
        {
            for (int i = 0; i < Active.Count; i++)
            {
                if (Active[i].Contains(worldPoint)) return true;
            }
            return false;
        }

        public bool Contains(Vector3 worldPoint)
        {
            Vector3 offset = transform.InverseTransformPoint(worldPoint) - center;
            Vector3 half = size * 0.5f;
            return Mathf.Abs(offset.x) <= half.x
                && Mathf.Abs(offset.y) <= half.y
                && Mathf.Abs(offset.z) <= half.z;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.9f);
            Gizmos.DrawWireCube(center, size);
        }
    }
}
