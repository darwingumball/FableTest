using Game.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Game.Editor
{
    /// <summary>
    /// The transparent materials the placement system needs at runtime, plus the shared
    /// builder for a zone's visible border.
    ///
    /// These live under Resources because <see cref="PlacementGhost"/> is created on demand
    /// on whichever machine is doing the carrying - there is no scene object to hang a
    /// material reference off. Authoring them here rather than at runtime is deliberate: an
    /// HDRP transparent material needs a coherent set of keywords, blend states and a render
    /// queue, and <c>HDMaterial.ValidateMaterial</c> derives all of it from the properties
    /// below in the editor, where a mistake is visible immediately instead of once per
    /// player's first placement.
    ///
    /// Menu: Game/Setup/Build Cargo. Idempotent, and run for you by Build Crab Boat.
    /// </summary>
    public static class CargoBuilder
    {
        public const string PLACEMENT_RES_FOLDER = "Assets/_Game/Resources/Placement";

        // Alpha is low on purpose. A ghost is an annotation on the world, not an object in
        // it, and at higher opacity players read it as the cargo having already landed.
        private static readonly Color ValidTint = new(0.25f, 1f, 0.42f, 0.34f);
        private static readonly Color BlockedTint = new(1f, 0.22f, 0.18f, 0.34f);
        // Fainter again: this one is on screen the whole time, not only while carrying.
        private static readonly Color OutlineTint = new(0.35f, 1f, 0.55f, 0.16f);

        [MenuItem("Game/Setup/Build Cargo")]
        public static void Build()
        {
            TestMaterials.EnsureFolder(PLACEMENT_RES_FOLDER);
            GhostMaterial("Ghost_Valid", ValidTint);
            GhostMaterial("Ghost_Blocked", BlockedTint);
            GhostMaterial("Zone_Outline", OutlineTint);
            AssetDatabase.SaveAssets();
            Debug.Log("[CargoBuilder] Placement materials ready under " + PLACEMENT_RES_FOLDER + ".");
        }

        public static Material GhostMaterial(string name, Color tint)
        {
            TestMaterials.EnsureFolder(PLACEMENT_RES_FOLDER);
            string path = $"{PLACEMENT_RES_FOLDER}/{name}.mat";

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("HDRP/Unlit");
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;

            // Unlit, not Lit: a preview that took the scene's lighting would go black at
            // night and read as blocked when it was not.
            material.SetColor("_UnlitColor", tint);
            material.SetFloat("_SurfaceType", 1f);           // transparent
            material.SetFloat("_BlendMode", 0f);             // alpha
            material.SetFloat("_TransparentZWrite", 0f);
            // Visible from inside as well as out - the operator is frequently looking down
            // into the footprint rather than across it.
            material.SetFloat("_DoubleSidedEnable", 1f);
            material.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
            // Never receives or casts anything; it is not there.
            material.SetFloat("_ReceivesSSR", 0f);
            material.SetFloat("_ReceivesSSRTransparent", 0f);

            // Derives keywords, blend states and render queue from the above. Without it the
            // material keeps opaque keywords and the tint's alpha is simply ignored.
            HDMaterial.ValidateMaterial(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// A low translucent kerb around a placement region, so players can see where cargo
        /// is allowed before they are holding any. Four strips rather than a filled floor
        /// quad: a coloured deck reads as a different material, while a border reads as a
        /// marking, and it does not fight the deck's own shading.
        /// </summary>
        public static GameObject BuildZoneOutline(Transform parent, Vector3 center, Vector3 size,
            float height, Material material, float thickness = 0.06f)
        {
            var outline = new GameObject("ZoneOutline");
            outline.transform.SetParent(parent, false);
            // Sits on the region's floor and rises from there, so it marks the deck rather
            // than floating at the region's mid-height.
            outline.transform.localPosition = center + Vector3.down * (size.y * 0.5f);

            float halfX = size.x * 0.5f, halfZ = size.z * 0.5f;
            float y = height * 0.5f;

            for (int i = -1; i <= 1; i += 2)
            {
                TestMaterials.Box($"Edge_X{i}", outline.transform,
                    new Vector3(i * halfX, y, 0f),
                    new Vector3(thickness, height, size.z), material);
                TestMaterials.Box($"Edge_Z{i}", outline.transform,
                    new Vector3(0f, y, i * halfZ),
                    new Vector3(size.x, height, thickness), material);
            }

            // Markings, not geometry. A kerb you could trip over around the one part of the
            // deck cargo has to be dragged across would be actively hostile.
            foreach (var stray in outline.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(stray);

            return outline;
        }
    }
}
