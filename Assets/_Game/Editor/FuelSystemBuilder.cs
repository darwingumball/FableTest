using Game.World;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Shared props for the fuel system: a fillable tank with a physical filler cap to aim at,
    /// and a switched generator. Used by both a ship's dedicated tank (WaterBuilder,
    /// CrabBoatBuilder) and a property's tank (PropertyBuilder) so the "walk up and aim at a
    /// small box" interaction is identical everywhere fuel shows up.
    /// </summary>
    public static class FuelSystemBuilder
    {
        /// <summary>
        /// A filler cap: a small box with a trigger collider carrying <see cref="FuelTank"/>.
        /// Needs its own collider because <c>InteractionSystem</c> resolves an interactable
        /// with GetComponentInParent - without one here, nothing under this object would ever
        /// answer the interaction raycast at all.
        /// </summary>
        public static FuelTank BuildFuelTank(Transform parent, Vector3 localPosition,
            float capacityLiters, float startingLiters, Material trim)
        {
            var go = new GameObject("FuelInlet");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var aim = go.AddComponent<BoxCollider>();
            aim.size = new Vector3(0.4f, 0.4f, 0.4f);
            aim.isTrigger = true;   // aimable, but must not shove a player who walks into it

            TestMaterials.Box("Cap", go.transform, Vector3.zero,
                new Vector3(0.3f, 0.22f, 0.3f), trim);
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
        public static Generator BuildGenerator(Transform parent, Vector3 localPosition,
            float localYaw, FuelTank tank, float litersPerHour, string generatorId,
            GameObject[] poweredObjects, Material housing, Material trim)
        {
            var go = new GameObject("Generator");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(0f, localYaw, 0f);

            var aim = go.AddComponent<BoxCollider>();
            aim.center = new Vector3(0f, 0.35f, 0f);
            aim.size = new Vector3(0.7f, 0.7f, 0.5f);
            aim.isTrigger = true;

            TestMaterials.Box("Housing", go.transform, new Vector3(0f, 0.3f, 0f),
                new Vector3(0.6f, 0.6f, 0.4f), housing);
            var lever = TestMaterials.Box("Switch", go.transform, new Vector3(0.2f, 0.55f, 0.2f),
                new Vector3(0.05f, 0.14f, 0.05f), trim);
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
