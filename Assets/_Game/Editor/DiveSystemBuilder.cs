using Game.World;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// The physical dive station: a small davit at the rail, a rope down to the anchor point
    /// in the water, a winch control box, and a suit rack. Shared by both boats so the
    /// interaction is identical wherever a dive rig shows up, the same reasoning as
    /// <see cref="FuelSystemBuilder"/>.
    /// </summary>
    public static class DiveSystemBuilder
    {
        /// <summary>
        /// Builds a rig under <paramref name="parent"/>. <paramref name="localBasePosition"/>
        /// is where the post stands on deck (base-pivoted, like <c>FuelSystemBuilder</c>'s
        /// props); <paramref name="outboardOffset"/> is how far past that, in the rig's own
        /// +Z after <paramref name="localYaw"/> is applied, the rope actually enters the water -
        /// this has to clear the hull, which varies boat to boat, so it is a parameter rather
        /// than a constant.
        /// </summary>
        public static DiveRig BuildDiveRig(Transform parent, Vector3 localBasePosition,
            float localYaw, float outboardOffset, float maxRopeLength, Material trim,
            Material paint)
        {
            var go = new GameObject("DiveRig");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localBasePosition;
            go.transform.localRotation = Quaternion.Euler(0f, localYaw, 0f);

            // Post and davit arm - just enough structure to hang a rope from over the side.
            // Both boats' rails/bulwarks reach close to 2 m, so the davit sits above that
            // rather than through it.
            const float davitHeight = 2.15f;
            TestMaterials.Box("Post", go.transform, new Vector3(0f, davitHeight * 0.5f, 0f),
                new Vector3(0.16f, davitHeight, 0.16f), paint);
            TestMaterials.Box("Davit", go.transform, new Vector3(0f, davitHeight, outboardOffset * 0.5f),
                new Vector3(0.12f, 0.12f, outboardOffset), paint);

            var anchor = TestMaterials.Node("AnchorPoint", go.transform,
                new Vector3(0f, davitHeight, outboardOffset));
            var exit = TestMaterials.Node("ExitPoint", go.transform, new Vector3(0f, 0f, -0.6f));

            // A visual rope from the davit tip down to a modest depth - purely dressing, the
            // diver's actual depth is computed from DiveRig.RopeLength at runtime and this
            // does not move with it. Long enough to read as "goes into the water" without
            // needing to animate.
            var rope = TestMaterials.Box("Rope", anchor, new Vector3(0f, -1.5f, 0f),
                new Vector3(0.04f, 3f, 0.04f), trim);
            foreach (var stray in rope.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(stray);

            // Winch controls: a small box at the post, aimable, separate from the suit rack -
            // two different verbs at two different spots, like the crane's console vs the
            // hook it operates.
            var winchGO = new GameObject("WinchControls");
            winchGO.transform.SetParent(go.transform, false);
            winchGO.transform.localPosition = new Vector3(0.35f, 0.9f, 0f);
            var winchAim = winchGO.AddComponent<BoxCollider>();
            winchAim.size = new Vector3(0.4f, 0.5f, 0.4f);
            winchAim.isTrigger = true;
            TestMaterials.Box("Drum", winchGO.transform, Vector3.zero,
                new Vector3(0.3f, 0.3f, 0.3f), trim);
            foreach (var stray in winchGO.GetComponentsInChildren<Collider>())
                if (!stray.isTrigger) Object.DestroyImmediate(stray);

            // Suit rack: where a dive starts and ends. A simple frame, not a full harness
            // model - this is greybox, and the shape only has to read as "gear is here".
            var rackGO = new GameObject("SuitRack");
            rackGO.transform.SetParent(go.transform, false);
            rackGO.transform.localPosition = new Vector3(-0.35f, 0.9f, 0f);
            var rackAim = rackGO.AddComponent<BoxCollider>();
            rackAim.size = new Vector3(0.5f, 1.4f, 0.4f);
            rackAim.isTrigger = true;
            TestMaterials.Box("Frame", rackGO.transform, new Vector3(0f, -0.2f, 0f),
                new Vector3(0.4f, 1f, 0.1f), trim);
            foreach (var stray in rackGO.GetComponentsInChildren<Collider>())
                if (!stray.isTrigger) Object.DestroyImmediate(stray);

            // Everything else on the post is visual only - a collider on the post/davit/rope
            // would answer the interaction raycast for the whole rig, which is the two
            // dedicated points' job alone.
            foreach (var stray in go.GetComponentsInChildren<Collider>())
                if (stray.gameObject != winchGO && stray.gameObject != rackGO)
                    Object.DestroyImmediate(stray);

            var rig = go.AddComponent<DiveRig>();
            var so = new SerializedObject(rig);
            so.FindProperty("anchorPoint").objectReferenceValue = anchor;
            so.FindProperty("exitPoint").objectReferenceValue = exit;
            so.FindProperty("maxRopeLength").floatValue = maxRopeLength;
            so.ApplyModifiedPropertiesWithoutUndo();

            var suitRack = rackGO.AddComponent<DiveSuitRack>();
            var rso = new SerializedObject(suitRack);
            rso.FindProperty("rig").objectReferenceValue = rig;
            rso.ApplyModifiedPropertiesWithoutUndo();

            return rig;
        }
    }
}
