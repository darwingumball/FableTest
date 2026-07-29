using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// A lake/seashore beyond the city, with a shelving beach, floating props and a tugboat
    /// on a patrol course.
    ///
    /// Placed past the ground plane on purpose: the ground and the snow deformation region
    /// are both exactly 100 m square centred on the origin, so the shore starts where the
    /// ground ends and the water never has to intersect the street or the snow.
    ///
    /// Menu: Game/Setup/Build Water. Idempotent.
    /// </summary>
    public static class WaterBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string ROOT_NAME = "TestWater";
        private const string BOAT_NAME = "TestBoat";

        // Ground spans -50..50. The beach starts at its edge and shelves down to the water.
        private const float SHORE_START_Z = 46f;
        private const float WATER_LEVEL = -3.2f;
        private const float BEACH_LENGTH = 26f;   // z 46 -> 72, dropping to the water level
        private const float SHORE_WIDTH = 220f;

        private static readonly Vector3 LakeCentre = new(0f, WATER_LEVEL, 190f);
        private static readonly Vector3 BoatCourseCentre = new(0f, 0f, 150f);
        private const float BOAT_COURSE_RADIUS = 38f;

        [MenuItem("Game/Setup/Build Water")]
        public static void Build()
        {
            var sand = TestMaterials.Lit("TB_Sand", new Color(0.34f, 0.31f, 0.26f), 0.06f, 0f);
            var wetRock = TestMaterials.Lit("TB_ShoreRock", new Color(0.17f, 0.17f, 0.18f), 0.30f, 0f);
            var hullPaint = TestMaterials.Lit("TB_Hull", new Color(0.28f, 0.10f, 0.09f), 0.45f, 0.1f);
            var deckWood = TestMaterials.Lit("TB_Deck", new Color(0.30f, 0.24f, 0.17f), 0.20f, 0f);
            var trim = TestMaterials.Lit("TB_Trim", new Color(0.12f, 0.13f, 0.16f), 0.55f, 0.6f);

            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            foreach (var existing in scene.GetRootGameObjects())
                if (existing.name == ROOT_NAME || existing.name == BOAT_NAME)
                    Object.DestroyImmediate(existing);

            var root = new GameObject(ROOT_NAME);
            SceneManager.MoveGameObjectToScene(root, scene);

            BuildWaterSurface(root.transform);
            BuildShore(root.transform, sand, wetRock);
            BuildFloatingProps(root.transform, deckWood);
            BuildBoat(scene, hullPaint, deckWood, trim);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[WaterBuilder] Lake at {LakeCentre}, shore from z={SHORE_START_Z}, " +
                      "boat on a patrol course. Water level " + WATER_LEVEL + ".");
        }

        private static void BuildWaterSurface(Transform parent)
        {
            var go = new GameObject("WaterSurface");
            go.transform.SetParent(parent, false);
            go.transform.position = LakeCentre;

            var surface = go.AddComponent<WaterSurface>();
            surface.surfaceType = WaterSurfaceType.OceanSeaLake;
            // Finite, not Infinite: an infinite ocean would also sit under the whole city
            // and show through anywhere the ground does not cover.
            surface.geometryType = WaterGeometryType.Quad;
            // Height queries are worthless without this, and it is off by default.
            surface.scriptInteractions = true;
            surface.timeMultiplier = 1f;
            surface.tessellation = true;

            // Swell. The defaults (30 m/s wind over a 500 m repeat) produce long, low
            // ocean rollers that are almost flat across a 13 m hull - the boat measured
            // zero tilt on them. A shorter repetition packs the same energy into waves the
            // boat's own length can straddle, which is what actually makes it pitch and
            // roll. Chaos near 1 keeps crests from marching in visible parallel lines.
            surface.repetitionSize = 180f;
            surface.largeWindSpeed = 42f;
            surface.largeChaos = 0.9f;
            surface.largeBand0Multiplier = 1f;
            surface.largeBand1Multiplier = 1f;
            surface.largeOrientationValue = 20f;
            surface.ripples = true;
            surface.ripplesWindSpeed = 12f;
            surface.ripplesChaos = 0.85f;

            // A big quad so the far edge is over the horizon rather than a visible seam.
            go.transform.localScale = new Vector3(360f, 1f, 360f);

            // Murky cold water rather than tropical blue - this is a winter city.
            surface.refractionColor = new Color(0.05f, 0.13f, 0.15f);
            surface.scatteringColor = new Color(0.03f, 0.10f, 0.11f);
            surface.absorptionDistance = 2.4f;
            surface.caustics = true;
            surface.causticsIntensity = 0.25f;

            // Underwater rendering so swimming down actually looks like being submerged.
            surface.underWater = true;
            surface.volumeDepth = 40f;
            surface.volumeHeight = 2f;

            go.AddComponent<WaterVolume>();
        }

        /// <summary>
        /// A shelving beach from the ground edge down under the water. Built from a few
        /// rotated slabs rather than a mesh: the player needs a walkable ramp into the
        /// water, and a box collider ramp is both cheap and predictable for a
        /// CharacterController.
        /// </summary>
        private static void BuildShore(Transform parent, Material sand, Material rock)
        {
            var shore = TestMaterials.Node("Shore", parent, Vector3.zero);

            // Sloping beach: from y=0 at the ground edge down past the waterline.
            float drop = -(WATER_LEVEL - 1.2f);           // finish below the water surface
            float slopeLength = Mathf.Sqrt(BEACH_LENGTH * BEACH_LENGTH + drop * drop);
            float angle = Mathf.Atan2(drop, BEACH_LENGTH) * Mathf.Rad2Deg;

            var ramp = TestMaterials.Box("Beach", shore,
                new Vector3(0f, -drop * 0.5f, SHORE_START_Z + BEACH_LENGTH * 0.5f),
                new Vector3(SHORE_WIDTH, 1.2f, slopeLength), sand);
            ramp.transform.rotation = Quaternion.Euler(angle, 0f, 0f);

            // Lake bed, so swimming down does not fall out of the world.
            TestMaterials.Box("LakeBed", shore,
                new Vector3(0f, WATER_LEVEL - 7f, LakeCentre.z),
                new Vector3(SHORE_WIDTH, 1f, 300f), rock);

            // Breakwater rocks either side, so the beach reads as a shore rather than a ramp.
            for (int i = -1; i <= 1; i += 2)
                for (int j = 0; j < 4; j++)
                {
                    float z = SHORE_START_Z + 6f + j * 7f;
                    var boulder = TestMaterials.Box($"Rock_{i}_{j}", shore,
                        new Vector3(i * (26f + j * 2.5f), WATER_LEVEL + 0.9f - j * 0.25f, z),
                        new Vector3(4.5f - j * 0.4f, 3.2f, 4f), rock);
                    boulder.transform.rotation = Quaternion.Euler(0f, j * 23f + i * 11f, i * 6f);
                }

            // Snow must not lie on open water or the shelving beach.
            TestMaterials.SnowBlock(shore, "SnowBlock_Shore",
                new Vector3(0f, 0f, SHORE_START_Z + BEACH_LENGTH * 0.5f),
                new Vector2(SHORE_WIDTH, BEACH_LENGTH + 8f));
        }

        private static void BuildFloatingProps(Transform parent, Material wood)
        {
            var props = TestMaterials.Node("Flotsam", parent, Vector3.zero);

            // Spread across the bay so there is always something visibly bobbing.
            var spots = new[]
            {
                new Vector3(-14f, 0f, 96f), new Vector3(9f, 0f, 104f),
                new Vector3(-3f, 0f, 118f), new Vector3(19f, 0f, 126f),
                new Vector3(-22f, 0f, 132f),
            };

            for (int i = 0; i < spots.Length; i++)
            {
                var crate = TestMaterials.Box($"Crate_{i}", props,
                    spots[i] + Vector3.up * (WATER_LEVEL + 0.4f),
                    new Vector3(1.1f, 1.1f, 1.1f), wood);
                crate.transform.rotation = Quaternion.Euler(0f, i * 27f, 0f);

                var body = crate.AddComponent<Rigidbody>();
                body.mass = 40f;
                // Continuous stops a light box tunnelling through the lake bed when the
                // swell drops out from under it.
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.interpolation = RigidbodyInterpolation.Interpolate;

                crate.AddComponent<Buoyancy>();
            }
        }

        /// <summary>
        /// Tugboat: a level collision deck on the root, and a hull child that does the
        /// pitching and rolling. See <see cref="BoatMotion"/> for why they are separate.
        /// </summary>
        private static void BuildBoat(Scene scene, Material hullPaint, Material deck, Material trim)
        {
            var boat = new GameObject(BOAT_NAME);
            SceneManager.MoveGameObjectToScene(boat, scene);
            boat.transform.position = BoatCourseCentre + new Vector3(BOAT_COURSE_RADIUS, 0f, 0f);

            // --- collision deck: stays level, this is what the player walks on ---
            var deckCollider = boat.AddComponent<BoxCollider>();
            deckCollider.center = new Vector3(0f, 0.35f, 0f);
            deckCollider.size = new Vector3(5.2f, 0.7f, 13f);

            // Gunwales so you can walk to the edge without walking off it.
            for (int i = -1; i <= 1; i += 2)
            {
                var rail = boat.AddComponent<BoxCollider>();
                rail.center = new Vector3(i * 2.7f, 1.2f, 0f);
                rail.size = new Vector3(0.3f, 1.6f, 13f);
            }
            for (int i = -1; i <= 1; i += 2)
            {
                var cap = boat.AddComponent<BoxCollider>();
                cap.center = new Vector3(0f, 1.2f, i * 6.6f);
                cap.size = new Vector3(5.2f, 1.6f, 0.3f);
            }

            // --- visual hull: this is the part that rolls ---
            var hull = TestMaterials.Node("Hull", boat.transform, Vector3.zero);

            TestMaterials.Box("HullBody", hull, new Vector3(0f, -0.5f, 0f),
                new Vector3(5.2f, 2.2f, 13f), hullPaint);
            var bow = TestMaterials.Box("Bow", hull, new Vector3(0f, -0.4f, 7.1f),
                new Vector3(3.4f, 2f, 2.6f), hullPaint);
            bow.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            TestMaterials.Box("DeckPlate", hull, new Vector3(0f, 0.72f, 0f),
                new Vector3(5.2f, 0.16f, 13f), deck);

            // Wheelhouse aft, so there is something to walk around.
            TestMaterials.Box("Wheelhouse", hull, new Vector3(0f, 2f, -3.2f),
                new Vector3(3.2f, 2.4f, 3.4f), trim);
            TestMaterials.Box("Funnel", hull, new Vector3(0f, 3.8f, -4.4f),
                new Vector3(1.1f, 1.6f, 1.1f), trim);
            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"Rail_{i}", hull, new Vector3(i * 2.6f, 1.4f, 2f),
                    new Vector3(0.14f, 1f, 8f), trim);

            // Navigation lights - a boat at night with no lights reads as a prop.
            TestMaterials.PointLight("DeckLight", hull, new Vector3(0f, 3.4f, -3.2f),
                new Color(1f, 0.88f, 0.7f), 9000f, 12f, volumetric: 1.4f,
                group: "boat", nightBoost: 2.2f);
            TestMaterials.PointLight("BowLight", hull, new Vector3(0f, 1.6f, 6.4f),
                new Color(0.7f, 0.9f, 1f), 3200f, 8f, volumetric: 1.6f,
                group: "boat", nightBoost: 2.4f);

            var motion = boat.AddComponent<BoatMotion>();
            var so = new SerializedObject(motion);
            so.FindProperty("hullVisual").objectReferenceValue = hull;
            so.FindProperty("courseCentre").vector3Value = BoatCourseCentre;
            so.FindProperty("courseRadius").floatValue = BOAT_COURSE_RADIUS;
            so.FindProperty("lapSeconds").floatValue = 110f;
            so.FindProperty("hullLength").floatValue = 6.5f;
            so.FindProperty("hullBeam").floatValue = 2.6f;
            so.FindProperty("freeboard").floatValue = 1.1f;
            // Tilt is visual only - the collision deck stays level - so exaggerating it
            // costs nothing in playability and is what sells "rough water". Measured roll
            // on this swell is 2-4 degrees at 1.0, which reads as a working boat in chop
            // rather than a barge on a millpond.
            so.FindProperty("waveFollow").floatValue = 1f;
            so.FindProperty("maxTiltDegrees").floatValue = 16f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var carry = boat.AddComponent<BoatRiderCarry>();
            var cso = new SerializedObject(carry);
            cso.FindProperty("deckCenter").vector3Value = new Vector3(0f, 1.6f, 0f);
            cso.FindProperty("deckSize").vector3Value = new Vector3(6f, 3.4f, 14.4f);
            cso.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
