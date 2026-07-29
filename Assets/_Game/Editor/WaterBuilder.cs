using System.IO;
using Game.World;
using Unity.Netcode;
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
        private const float LAKE_DEPTH = 7f;      // waterline to lake bed

        // The water is deliberately enormous. "Rough about 300 m out" needs 300 m of open
        // water to actually be out in, and the shore is at z=72, so anything smaller would
        // put the storm belt against the far edge of the quad.
        private const float WATER_SPAN = 1200f;
        private const float LAKE_CENTRE_Z = 620f;   // spans z 20..1220
        private static readonly Vector3 LakeCentre = new(0f, WATER_LEVEL, LAKE_CENTRE_Z);

        // Roughness gradient, in world Z. Calm out to CALM_END_Z, fully open water from
        // ROUGH_START_Z, smoothstepped between - about 300 m past the beach.
        private const float CALM_END_Z = 150f;
        private const float ROUGH_START_Z = 400f;
        // Per-band multipliers in the sheltered water. Band 0 is the long swell (killed
        // almost entirely near shore), band 1 the agitation, band 2 the ripples - ripples
        // keep most of their strength because glassy water reads as broken, not as calm.
        private const float SHELTERED_SWELL = 0.06f;
        private const float SHELTERED_AGITATION = 0.16f;
        private const float SHELTERED_RIPPLES = 0.5f;
        private const string MASK_PATH = "Assets/_Game/Textures/WaterRoughnessMask.png";

        // Close in, so the boat is a short swim from the beach. From here you drive it out
        // into the weather yourself.
        private static readonly Vector3 BoatCourseCentre = new(0f, 0f, 128f);
        private const float BOAT_COURSE_RADIUS = 22f;

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
            // This is the OPEN WATER state; the mask below scales it down near the shore.
            surface.repetitionSize = 180f;
            surface.largeWindSpeed = 36f;
            surface.largeChaos = 0.9f;
            surface.largeBand0Multiplier = 1f;
            surface.largeBand1Multiplier = 1f;
            surface.largeOrientationValue = 20f;
            surface.ripples = true;
            surface.ripplesWindSpeed = 11f;
            surface.ripplesChaos = 0.85f;

            ApplyRoughnessMask(surface);

            go.transform.localScale = new Vector3(WATER_SPAN, 1f, WATER_SPAN);

            // Murky cold water rather than tropical blue - this is a winter city.
            surface.refractionColor = new Color(0.05f, 0.13f, 0.15f);
            surface.scatteringColor = new Color(0.03f, 0.10f, 0.11f);
            // Absorption is what you see THROUGH the surface from above; the multiplier
            // below reopens it once the camera is under, because a 4 m view distance
            // underwater is just a green screen.
            surface.absorptionDistance = 4.5f;

            ConfigureCaustics(surface);
            // Parented to the unscaled root, NOT to this object: the water quad is scaled
            // 1200x, and a collider under it inherits that scale, which turned a 1.2 km box
            // into a 720 km one covering the entire world below the waterline.
            ConfigureUnderwater(surface, parent);

            go.AddComponent<WaterVolume>();
        }

        /// <summary>
        /// Caustics: the light pattern the surface throws onto the lake bed. Only visible
        /// from under the water, which is exactly where the swimming is.
        /// </summary>
        private static void ConfigureCaustics(WaterSurface surface)
        {
            surface.caustics = true;
            surface.causticsIntensity = 0.9f;
            // Band 2 is the ripples. Bands 0 and 1 are the 180 m swell, whose caustics are
            // enormous soft smears - the recognisable dancing net comes from the small band.
            surface.causticsBand = 2;
            surface.causticsResolution = WaterSurface.WaterCausticsResolution.Caustics256;
            // The virtual plane the pattern is projected onto. Roughly mid-depth, so the
            // bed and the lower half of the boat hull both land near focus.
            surface.virtualPlaneDistance = 4f;
            surface.causticsTilingFactor = 1f;
            surface.causticsPlaneBlendDistance = 1.5f;
            // Shadowed caustics cost an extra directional shadow evaluation for something
            // nearly invisible in water this murky.
            surface.causticsDirectionalShadow = false;
        }

        /// <summary>
        /// Underwater fog and colour shift. A finite surface will not do this at all unless
        /// it is given a BoxCollider to bound the effect - volumeDepth/volumeHeight are only
        /// consulted for infinite oceans, which is why they looked like they did nothing.
        /// </summary>
        private static void ConfigureUnderwater(WaterSurface surface, Transform parent)
        {
            surface.underWater = true;
            surface.underWaterRefraction = true;
            // See about three times as far under the surface as through it. Without this the
            // absorption tuned for looking INTO the water leaves you blind once submerged.
            surface.absorptionDistanceMultiplier = 3f;

            var boundsGO = new GameObject("UnderwaterVolume");
            boundsGO.transform.SetParent(parent, false);
            // Ignore Raycast: this box is 1.2 km across and only exists so HDRP can test
            // whether the camera is submerged. Left on the default layer it would swallow
            // the player's interaction raycast near its faces.
            boundsGO.layer = LayerMask.NameToLayer("Ignore Raycast");

            // Open water only. Starting at the shoreline instead of the quad's near edge
            // keeps the box from reaching back under the city, where anything below the
            // waterline - a basement, the metro - would otherwise render as submerged.
            const float nearEdgeZ = SHORE_START_Z + BEACH_LENGTH;
            const float farEdgeZ = LAKE_CENTRE_Z + WATER_SPAN * 0.5f;
            const float top = 3f;   // crest clearance above the still waterline

            var box = boundsGO.AddComponent<BoxCollider>();
            box.isTrigger = true;
            boundsGO.transform.localPosition = new Vector3(
                0f, WATER_LEVEL, (nearEdgeZ + farEdgeZ) * 0.5f);
            box.center = new Vector3(0f, (top - LAKE_DEPTH) * 0.5f, 0f);
            box.size = new Vector3(WATER_SPAN, top + LAKE_DEPTH, farEdgeZ - nearEdgeZ);
            surface.volumeBounds = box;
        }

        /// <summary>
        /// Calm in the bay, storm offshore.
        ///
        /// The water mask attenuates each simulation band per texel, and - the part that
        /// makes it usable here - the CPU height search reads the same mask. So the buoyancy
        /// probes, the hull sampling and what you can see all agree: the boat really is
        /// steadier near the beach, not just painted that way.
        /// </summary>
        private static void ApplyRoughnessMask(WaterSurface surface)
        {
            surface.waterMask = BuildRoughnessMaskTexture();
            surface.waterMaskRemap = new Vector2(0f, 1f);    // values are absolute in the PNG
            surface.waterMaskExtent = new Vector2(WATER_SPAN, WATER_SPAN);
            surface.waterMaskOffset = new Vector2(0f, LAKE_CENTRE_Z);
        }

        /// <summary>
        /// Writes the mask as a real imported PNG rather than a generated .asset. The GPU
        /// path would take either, but the CPU simulation reads the mask back through an
        /// async readback that expects a normally imported, uncompressed texture.
        /// </summary>
        private static Texture2D BuildRoughnessMaskTexture()
        {
            const int res = 256;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, linear: true);
            var pixels = new Color32[res * res];

            for (int y = 0; y < res; y++)
            {
                // The mask samples UV.y from world Z across waterMaskExtent, and row 0 of a
                // Texture2D is the bottom of that range.
                float z = LAKE_CENTRE_Z + ((y + 0.5f) / res - 0.5f) * WATER_SPAN;
                float open = Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(CALM_END_Z, ROUGH_START_Z, z));

                var c = new Color32(
                    ToByte(Mathf.Lerp(SHELTERED_SWELL, 1f, open)),
                    ToByte(Mathf.Lerp(SHELTERED_AGITATION, 1f, open)),
                    ToByte(Mathf.Lerp(SHELTERED_RIPPLES, 1f, open)),
                    255);

                for (int x = 0; x < res; x++) pixels[y * res + x] = c;
            }

            tex.SetPixels32(pixels);
            tex.Apply();

            TestMaterials.EnsureFolder("Assets/_Game/Textures");
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), MASK_PATH),
                               tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(MASK_PATH, ImportAssetOptions.ForceUpdate);

            var importer = (TextureImporter)AssetImporter.GetAtPath(MASK_PATH);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;              // band multipliers, not colour
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            // Clamp, so beyond the mask extent the water stays at the edge value - full
            // storm - instead of wrapping the calm bay back in every 1.2 km.
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Texture2D>(MASK_PATH);
        }

        private static byte ToByte(float value01) =>
            (byte)Mathf.RoundToInt(Mathf.Clamp01(value01) * 255f);

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

            // Lake bed, so swimming down does not fall out of the world. It spans the whole
            // water quad: with the storm belt 300 m out, "out of the world" is somewhere a
            // driven boat can genuinely reach.
            TestMaterials.Box("LakeBed", shore,
                new Vector3(0f, WATER_LEVEL - LAKE_DEPTH, LAKE_CENTRE_Z),
                new Vector3(WATER_SPAN, 1f, WATER_SPAN), rock);

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

            // Spread across the bay so there is always something visibly bobbing, plus two
            // well offshore: side by side they are the clearest read on the roughness
            // gradient, since the same crate barely rocks inshore and pitches hard out in
            // the storm belt.
            var spots = new[]
            {
                new Vector3(-14f, 0f, 96f), new Vector3(9f, 0f, 104f),
                new Vector3(-3f, 0f, 118f), new Vector3(19f, 0f, 126f),
                new Vector3(-22f, 0f, 132f),
                new Vector3(12f, 0f, 470f), new Vector3(-26f, 0f, 620f),
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

            // Needed only because the boat became drivable. Autopilot replicates nothing at
            // all; a hull steered by a human has to have an owner, and that is the server.
            boat.AddComponent<NetworkObject>();

            // Without a Rigidbody, PhysX treats these colliders as STATIC geometry that
            // happens to teleport every frame: it rebuilds the static broadphase constantly
            // and gives the riders' CharacterControllers nothing to collide against
            // properly, which is how a heaving deck ends up passing through people. A
            // kinematic body makes the boat a legitimate moving collider. Interpolation is
            // off because BoatMotion writes the transform outright each frame.
            var body = boat.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;

            // --- collision deck: stays level, this is what the player walks on ---
            var deckCollider = boat.AddComponent<BoxCollider>();
            // Top face flush with the visual deck plate at y=0.80, so standing on the level
            // collider does not leave the player shin-deep in the planking.
            deckCollider.center = new Vector3(0f, 0.45f, 0f);
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

            // The hull is a VISUAL. CreatePrimitive hands out a collider with every box, and
            // those tilt with the roll - worse, the deck plate's sits a centimetre ABOVE the
            // level deck collider, so the player was standing on the tilting one and the
            // whole reason the two are separate was quietly defeated.
            foreach (var stray in hull.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(stray);

            var helm = BuildHelm(boat.transform, trim, deck);
            BuildLadder(boat.transform, trim);

            var motion = boat.AddComponent<BoatMotion>();
            var so = new SerializedObject(motion);
            so.FindProperty("helm").objectReferenceValue = helm;
            so.FindProperty("hullVisual").objectReferenceValue = hull;
            so.FindProperty("courseCentre").vector3Value = BoatCourseCentre;
            so.FindProperty("courseRadius").floatValue = BOAT_COURSE_RADIUS;
            so.FindProperty("lapSeconds").floatValue = 110f;
            so.FindProperty("hullLength").floatValue = 6.5f;
            so.FindProperty("hullBeam").floatValue = 2.6f;
            so.FindProperty("freeboard").floatValue = 1.1f;
            // Lower gain against a much higher ceiling, approached asymptotically. At 1.0
            // into a hard 16-degree clamp, ordinary chop already pinned the limit, so a
            // storm looked identical to a breeze - the tilt carried no information. At 0.7
            // into a soft 24, everyday water leans noticeably less and a genuinely big sea
            // still has somewhere to go.
            so.FindProperty("waveFollow").floatValue = 0.7f;
            so.FindProperty("maxTiltDegrees").floatValue = 24f;
            // Heave is the part the riders actually stand on. The CPU water query updates
            // on a GPU readback rather than per frame, so its height comes back as a
            // staircase; damping it hard is what stops that becoming visible judder
            // underfoot at speed.
            so.FindProperty("heaveFollow").floatValue = 0.7f;
            so.FindProperty("heaveSmoothing").floatValue = 1.8f;
            // The hull was rolling faster than the seas it sat in, because probes at exactly
            // hull size read the metre-scale ripples as steep local slopes. Measuring the
            // gradient over a longer baseline cancels those and leaves the swell; the slower
            // tilt smoothing then filters what is left, so the roll period matches the waves
            // you can actually see.
            so.FindProperty("slopeBaseline").floatValue = 1.8f;
            so.FindProperty("smoothing").floatValue = 0.9f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var carry = boat.AddComponent<BoatRiderCarry>();
            var cso = new SerializedObject(carry);
            cso.FindProperty("deckCenter").vector3Value = new Vector3(0f, 1.6f, 0f);
            cso.FindProperty("deckSize").vector3Value = new Vector3(6f, 3.4f, 14.4f);
            cso.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// The wheel. Its own child object rather than a component on the root, because
        /// InteractionSystem resolves an interactable with GetComponentInParent: on the root
        /// it would answer for every collider on the boat, and merely standing near the hull
        /// would offer to hand you the helm.
        ///
        /// It hangs off the LEVEL root, not the rolling hull, so the thing you aim at does
        /// not swing away from the crosshair every time the boat takes a wave.
        /// </summary>
        private static BoatHelm BuildHelm(Transform boat, Material trim, Material wood)
        {
            var helmGO = new GameObject("Helm");
            helmGO.transform.SetParent(boat, false);
            helmGO.transform.localPosition = new Vector3(0f, 1.35f, -1.75f);

            var box = helmGO.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.1f, 0f);
            box.size = new Vector3(0.9f, 0.9f, 0.5f);
            box.isTrigger = true;   // aimable, but must not shove the driver standing at it

            TestMaterials.Box("Binnacle", helmGO.transform, new Vector3(0f, -0.35f, 0f),
                new Vector3(0.35f, 0.9f, 0.35f), trim);
            var wheel = TestMaterials.Box("Wheel", helmGO.transform, new Vector3(0f, 0.15f, 0f),
                new Vector3(0.72f, 0.72f, 0.09f), wood);
            wheel.transform.localRotation = Quaternion.Euler(18f, 0f, 45f);

            var helm = helmGO.AddComponent<BoatHelm>();
            var so = new SerializedObject(helm);
            so.FindProperty("boundsCentre").vector2Value = new Vector2(0f, LAKE_CENTRE_Z);
            // Inside the quad by a hull length, so the bow never overhangs the edge.
            so.FindProperty("boundsHalfExtents").vector2Value =
                new Vector2(WATER_SPAN * 0.5f - 20f, WATER_SPAN * 0.5f - 20f);
            so.ApplyModifiedPropertiesWithoutUndo();
            return helm;
        }

        /// <summary>
        /// Boarding ladder on the port quarter, reaching from below the waterline to the
        /// deck. Parented to the level root rather than the rolling hull on purpose: the
        /// climb track and its top exit have to stay square to the deck the climber steps
        /// out onto, and a 16-degree roll would throw that exit most of a metre sideways -
        /// straight over the rail.
        /// </summary>
        private static void BuildLadder(Transform boat, Material trim)
        {
            var ladderGO = new GameObject("BoardingLadder");
            ladderGO.transform.SetParent(boat, false);
            ladderGO.transform.localPosition = new Vector3(-2.75f, -2.6f, 1f);
            // Local +Z faces outboard to port, so the climber hangs off the outside of the
            // hull and faces in toward the rungs.
            ladderGO.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);

            // Grab volume, generous enough to catch from the water while the swell is
            // lifting you past it.
            var grab = ladderGO.AddComponent<BoxCollider>();
            grab.center = new Vector3(0f, 1.95f, 0.2f);
            grab.size = new Vector3(0.9f, 4.2f, 0.7f);
            grab.isTrigger = true;

            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"Stile_{i}", ladderGO.transform,
                    new Vector3(i * 0.3f, 1.95f, 0.06f),
                    new Vector3(0.09f, 4.2f, 0.09f), trim);
            for (int r = 0; r < 8; r++)
                TestMaterials.Box($"Rung_{r}", ladderGO.transform,
                    new Vector3(0f, 0.2f + r * 0.52f, 0.06f),
                    new Vector3(0.68f, 0.07f, 0.07f), trim);

            var ladder = ladderGO.AddComponent<Ladder>();
            var so = new SerializedObject(ladder);
            so.FindProperty("climbHeight").floatValue = 3.9f;
            so.FindProperty("climbSpeed").floatValue = 2.4f;
            so.FindProperty("standOffset").vector3Value = new Vector3(0f, 0f, 0.45f);
            // Up over the gunwale and inboard onto the deck plate.
            so.FindProperty("topExitLocal").vector3Value = new Vector3(0f, 3.75f, -1.3f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
