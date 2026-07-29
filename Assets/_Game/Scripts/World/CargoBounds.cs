using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Measures how big a loose object actually is, in metres, in its own frame.
    ///
    /// <c>Collider.bounds</c> is a world-axis box around whatever rotation the thing happens
    /// to be lying at, so a crate tumbling in the swell measures half a metre wider than it
    /// is. Placement needs the true size to decide whether it fits and where its underside
    /// is, so the rotation has to come out of the measurement first.
    ///
    /// Colliders rather than renderers, deliberately: placement is decided by overlap tests,
    /// and a ghost that fits but whose collider does not would read as a bug.
    /// </summary>
    public static class CargoBounds
    {
        /// <summary>
        /// Axis-aligned bounds in the object's own rotated frame, in world metres (scale
        /// included). Centre is the collider centroid's offset from the object's origin,
        /// which is rarely zero and matters for resting something on a surface.
        /// </summary>
        public static Bounds InOwnFrame(GameObject go)
        {
            // Rotation only - no translation, no scale. Mapping every collider through this
            // strips the object's own rotation while leaving its scale baked in, which is
            // exactly the frame the object will be placed in.
            Matrix4x4 unrotate = Matrix4x4.Rotate(Quaternion.Inverse(go.transform.rotation));
            Vector3 origin = go.transform.position;

            bool any = false;
            var result = new Bounds();

            foreach (var collider in go.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger) continue;

                Bounds local = LocalBounds(collider);
                Matrix4x4 toFrame = unrotate * collider.transform.localToWorldMatrix;

                Vector3 c = local.center, e = local.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    var offset = new Vector3(
                        (corner & 1) == 0 ? -e.x : e.x,
                        (corner & 2) == 0 ? -e.y : e.y,
                        (corner & 4) == 0 ? -e.z : e.z);
                    // Relative to the object's origin, so the result is an offset rather
                    // than a position and stays valid wherever the object is moved to.
                    Vector3 point = toFrame.MultiplyPoint3x4(c + offset)
                                    - unrotate.MultiplyPoint3x4(origin);
                    if (any) result.Encapsulate(point);
                    else { result = new Bounds(point, Vector3.zero); any = true; }
                }
            }

            // Nothing to measure: a 20 cm cube keeps callers from dividing by zero, and a
            // placement that small will simply never look right, which is a visible failure
            // rather than a silent one.
            return any ? result : new Bounds(Vector3.zero, Vector3.one * 0.2f);
        }

        /// <summary>
        /// A collider's extent in its OWN transform's space, before scale. Primitives are
        /// read from their real parameters; anything else falls back to the mesh bounds.
        /// </summary>
        private static Bounds LocalBounds(Collider collider)
        {
            switch (collider)
            {
                case BoxCollider box:
                    return new Bounds(box.center, box.size);

                case SphereCollider sphere:
                    return new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));

                case CapsuleCollider capsule:
                {
                    float r = capsule.radius;
                    // Height is the full capsule including both caps, and can be authored
                    // shorter than the caps themselves - a squashed capsule is a sphere.
                    float h = Mathf.Max(capsule.height, r * 2f);
                    var size = capsule.direction switch
                    {
                        0 => new Vector3(h, r * 2f, r * 2f),
                        1 => new Vector3(r * 2f, h, r * 2f),
                        _ => new Vector3(r * 2f, r * 2f, h),
                    };
                    return new Bounds(capsule.center, size);
                }

                case MeshCollider mesh when mesh.sharedMesh != null:
                    return mesh.sharedMesh.bounds;

                default:
                    // No local description available. The world box is wrong under rotation,
                    // but it is the only number there is.
                    var world = collider.bounds;
                    return new Bounds(
                        collider.transform.InverseTransformPoint(world.center),
                        collider.transform.InverseTransformVector(world.size));
            }
        }
    }
}
