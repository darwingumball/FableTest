using Game.World;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Shared props for the fuel system: a fillable tank and a switched generator, each a
    /// proper standing fixture rather than a small aimable box - about a metre square and up
    /// to two tall, which is what makes them read as ship/property equipment instead of light
    /// switches. Used by both a ship's dedicated tank (WaterBuilder, CrabBoatBuilder) and a
    /// property's tank (PropertyBuilder) so the interaction is identical everywhere fuel shows
    /// up.
    ///
    /// Both are BASE-PIVOTED: <c>localPosition</c> is where the prop stands, not its centre,
    /// so a caller placing one at deck height does not have to work out half its height first.
    /// </summary>
    public static class FuelSystemBuilder
    {
        /// <summary>
        /// A standing tank, about a metre square and 1.8 m tall - big enough that a barrel
        /// carried up to it does not read as bigger than the thing it is refuelling. Needs its
        /// own collider because <c>InteractionSystem</c> resolves an interactable with
        /// GetComponentInParent - without one here, nothing under this object would ever
        /// answer the interaction raycast at all.
        /// </summary>
        public static FuelTank BuildFuelTank(Transform parent, Vector3 localBasePosition,
            float capacityLiters, float startingLiters, Material trim)
        {
            const float width = 1f, depth = 1f, height = 1.8f;

            var go = new GameObject("FuelTank");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localBasePosition;

            var aim = go.AddComponent<BoxCollider>();
            aim.center = new Vector3(0f, height * 0.5f, 0f);
            aim.size = new Vector3(width, height, depth);
            aim.isTrigger = true;   // aimable, but must not shove a player who walks into it

            TestMaterials.Box("Body", go.transform, new Vector3(0f, height * 0.5f, 0f),
                new Vector3(width, height, depth), trim);
            // Filler cap on top - purely a visual cue for where the fuel goes in.
            TestMaterials.Box("Cap", go.transform, new Vector3(0f, height + 0.08f, 0f),
                new Vector3(0.28f, 0.16f, 0.28f), trim);
            foreach (var stray in go.GetComponentsInChildren<Collider>())
                if (!stray.isTrigger) Object.DestroyImmediate(stray);

            var tank = go.AddComponent<FuelTank>();
            var so = new SerializedObject(tank);
            so.FindProperty("capacityLiters").floatValue = capacityLiters;
            so.FindProperty("startingLiters").floatValue = startingLiters;
            so.ApplyModifiedPropertiesWithoutUndo();
            return tank;
        }

        /// <summary>
        /// A switched generator: a housing with a lever, wired to <paramref name="tank"/>.
        /// <paramref name="poweredObjects"/> are disabled at build time by the caller (see
        /// PropertyBuilder) - the generator itself only ever flips them at RUNTIME, from its
        /// replicated running state, so authoring an already-off scene is what "needs
        /// refuelling and switching on" actually starts from.
        /// </summary>
        public static Generator BuildGenerator(Transform parent, Vector3 localBasePosition,
            float localYaw, FuelTank tank, float litersPerHour, string generatorId,
            GameObject[] poweredObjects, Material housing, Material trim)
        {
            const float width = 1f, height = 1.4f, depth = 0.8f;

            var go = new GameObject("Generator");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localBasePosition;
            go.transform.localRotation = Quaternion.Euler(0f, localYaw, 0f);

            var aim = go.AddComponent<BoxCollider>();
            aim.center = new Vector3(0f, height * 0.5f, 0f);
            aim.size = new Vector3(width, height, depth);
            aim.isTrigger = true;

            TestMaterials.Box("Housing", go.transform, new Vector3(0f, height * 0.5f, 0f),
                new Vector3(width, height, depth), housing);
            var lever = TestMaterials.Box("Switch", go.transform,
                new Vector3(width * 0.3f, height + 0.1f, 0f), new Vector3(0.05f, 0.22f, 0.05f), trim);
            lever.transform.localRotation = Quaternion.Euler(-20f, 0f, 0f);
            foreach (var stray in go.GetComponentsInChildren<Collider>())
                if (!stray.isTrigger) Object.DestroyImmediate(stray);

            var generator = go.AddComponent<Generator>();
            var so = new SerializedObject(generator);
            so.FindProperty("tank").objectReferenceValue = tank;
            so.FindProperty("litersPerHour").floatValue = litersPerHour;
            so.FindProperty("generatorId").stringValue = generatorId;
            var array = so.FindProperty("poweredObjects");
            array.arraySize = poweredObjects.Length;
            for (int i = 0; i < poweredObjects.Length; i++)
                array.GetArrayElementAtIndex(i).objectReferenceValue = poweredObjects[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return generator;
        }
    }
}
