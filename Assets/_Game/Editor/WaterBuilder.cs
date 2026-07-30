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

        // --- the boat's below-waterline hold, all in boat-local space ---
        // The boat root rides freeboard (1.1 m) above the water, so local y = -1.1 IS the
        // waterline. A floor at -2.6 puts the room 1.5 m under - deep enough that a player
        // standing in it would be chest-deep and swimming if the hold were not excluded,
        // which is the whole point of testing it here.
        private const float HOLD_FLOOR_Y = -2.6f;
        private const float HOLD_CEILING_Y = 0.64f;    // underside of the deck plating
        private const float HOLD_CENTRE_Z = 2.4f;      // forward of the helm at z = -1.75
        private const float HOLD_WIDTH = 3f;
        private const float HOLD_LENGTH = 3.6f;
        private const float HOLD_WALL = 0.3f;      // floor and bulkhead thickness
        // The two exclusion boxes want OPPOSITE sizes - see BuildHold.
        // Outward: hugs the room so the hatch is fully covered seen from the deck. The 2 cm
        // is only to avoid being coplanar with the bulkheads.
        private const float HOLD_EXCLUSION_INSET = 0.02f;
        // Inward: must fit inside the room's AIR, clear of anything protruding into it. The
        // ladder reaches ~13 cm off the aft bulkhead, so 20 cm clears it with margin.
        private const float HOLD_INTERIOR_INSET = 0.2f;
        // ...and start just above the floor plating rather than below it, or looking down at
        // the deck the box's own bottom face lands behind the floor and stops tagging.
        private const float HOLD_INTERIOR_FLOOR_CLEARANCE = 0.05f;
        private const string EXCLUSION_MATERIAL =
            "Packages/com.unity.render-pipelines.high-definition/Runtime/" +
            "RenderPipelineResources/Material/MaterialWaterExclusion.mat";
        private const string INVERTED_CUBE = "Assets/_Game/Meshes/InvertedCube.asset";

        // Ground spans -50..50. The beach starts at its edge and shelves down to the water.
        private const float SHORE_START_Z = 46f;
        // Shared with CrabBoatBuilder, which has to moor at the same water level and steer
        // inside the same quad. Two copies of these numbers would drift apart on the first
        // time anyone resized the lake.
        internal const float WATER_LEVEL = -3.2f;
        private const float BEACH_LENGTH = 26f;   // z 46 -> 72, dropping to the water level
        private const float SHORE_WIDTH = 220f;
        private const float LAKE_DEPTH = 7f;      // waterline to lake bed

        // The water is deliberately enormous. "Rough about 300 m out" needs 300 m of open
        // water to actually be out in, and the shore is at z=72, so anything smaller would
        // put the storm belt against the far edge of the quad.
        internal const float WATER_SPAN = 1200f;
        internal const float LAKE_CENTRE_Z = 620f;   // spans z 20..1220
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
        private const string MAT_FOLDER = "Assets/_Game/Materials/Test";

        // HDRP ships this graph to drive its own procedural decals. Building materials from
        // it means the wake needs no hand-authored Shader Graph, and WaterDecal accepts it
        // because it carries the WaterDecalSubTarget tag the component checks for.
        private const string DECAL_SHADER = "Packages/com.unity.render-pipelines.high-definition/" +
            "Runtime/RenderPipelineResources/ShaderGraph/Sample Water Decal.shadergraph";

        // _TYPE on that graph. Sphere and Box are plain shapes; BowWave is the V-shaped
        // swell HDRP provides specifically for the front of a moving hull.
        internal const float DECAL_SPHERE = 0f;
        private const float DECAL_BOX = 1f;
        internal const float DECAL_BOW_WAVE = 2f;

        // Close in, so the boat is a short swim from the beach. From here you drive it out
        // into the weather yourself.
        private static readonly Vector3 BoatCourseCentre = new(0f, 0f, 128f);
        private const float BOAT_COURSE_RADIUS = 22f;

        // ...and moored right there rather than running the course, because a test boat you
        // have to chase across the bay is a test boat that does not get tested. The patrol
        // course above is still authored and still works: `moored` only holds station until
        // someone takes the wheel, and clearing it in the inspector puts the boat back on its
        // lap. Port side is the boarding ladder, so the hull is angled bow-out to sea with
        // that side toward the beach.
        internal static readonly Vector3 BoatMooring = new(-12f, 0f, 78f);
        private const float BOAT_MOORING_HEADING = 16f;

        [MenuItem("Game/Setup/Build Water")]
        public static void Build()
        {
            if (!MenuSceneBuilder.Ready("WaterBuilder")) return;

            var sand = TestMaterials.Lit("TB_Sand", new Color(0.34f, 0.31f, 0.26f), 0.06f, 0f);
            var wetRock = TestMaterials.Lit("TB_ShoreRock", new Color(0.17f, 0.17f, 0.18f), 0.30f, 0f);
            var hullPaint = TestMaterials.Lit("TB_Hull", new Color(0.28f, 0.10f, 0.09f), 0.45f, 0.1f);
            var deckWood = TestMaterials.Lit("TB_Deck", new Color(0.30f, 0.24f, 0.17f), 0.20f, 0f);
            var trim = TestMaterials.Lit("TB_Trim", new Color(0.12f, 0.13f, 0.16f), 0.55f, 0.6f);

            // Wake pieces. The bow wave lifts the water in front of the hull; the prop wash
            // churns and foams behind it. They are separate because HDRP's bow-wave shape
            // writes deformation only, and foam is what actually persists into a trail.
            var bowWave = DecalMaterial("TB_BowWave", DECAL_BOW_WAVE, deformation: true, foam: false,
                m => m.SetFloat("_Elevation", 1f));
            // Propeller wash. A SPHERE, not a box: the box read as an obvious rectangle
            // dragged across the water, because that is exactly what it was. A narrow
            // elongated blob at the stern smears into a proper churned lane instead, and it
            // does both jobs - it disturbs the surface as well as foaming it, which is what
            // makes the water behind a boat look worked rather than painted.
            var propWash = DecalMaterial("TB_PropWash", DECAL_SPHERE, deformation: true, foam: true);
            var ripple = DecalMaterial("TB_FlotsamRipple", DECAL_SPHERE, deformation: true, foam: true);

            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            foreach (var existing in scene.GetRootGameObjects())
                if (existing.name == ROOT_NAME || existing.name == BOAT_NAME)
                    Object.DestroyImmediate(existing);

            var root = new GameObject(ROOT_NAME);
            SceneManager.MoveGameObjectToScene(root, scene);

            BuildWaterSurface(root.transform);
            BuildShore(root.transform, sand, wetRock);
            BuildFloatingProps(root.transform, deckWood, ripple);
            BuildBoat(scene, hullPaint, deckWood, trim, bowWave, propWash);

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

            ConfigureWakes(surface);
            ConfigureCaustics(surface);
            // Parented to the unscaled root, NOT to this object: the water quad is scaled
            // 1200x, and a collider under it inherits that scale, which turned a 1.2 km box
            // into a 720 km one covering the entire world below the waterline.
            ConfigureUnderwater(surface, parent);

            go.AddComponent<WaterVolume>();
        }

        /// <summary>
        /// Turns on the two buffers the wakes write into.
        ///
        /// Foam is what makes a trail: the buffer accumulates and decays rather than being
        /// redrawn each frame, so a foam source that MOVES leaves a line of slowly fading
        /// foam behind it. Nothing has to author the trail - persistence does it.
        /// Deformation is the other half, and is what makes a bow wave an actual bulge in
        /// the surface rather than a painted-on texture.
        /// </summary>
        private static void ConfigureWakes(WaterSurface surface)
        {
            surface.foam = true;
            surface.deformation = true;
            // How long foam survives. This IS the length of the wake - at the default 0.5 a
            // boat at 9 m/s has lost its trail before it is a hull-length astern.
            surface.foamPersistenceMultiplier = 0.85f;
            surface.foamColor = new Color(0.86f, 0.89f, 0.92f);
            surface.foamSmoothness = 0.25f;
            // Wind-driven whitecaps, separate from the decals. A little, so open water is
            // not glassy, but low enough that the boat's own wake still reads against it.
            surface.simulationFoamAmount = 0.35f;
            surface.foamResolution = WaterSurface.WaterDecalRegionResolution.Resolution512;
            surface.deformationRes = WaterSurface.WaterDecalRegionResolution.Resolution512;

            // The region only needs to cover what is on screen, not the 1.2 km of water.
            // 300 m at 512 is ~0.6 m per texel, fine enough for a PSX-resolution wake, and
            // wide enough that the boat's whole patrol circle stays inside it while the
            // player watches from the beach.
            //
            // WaterVolume re-anchors this to the local player at runtime. Left alone, HDRP
            // centres it on Camera.main and silently falls back to the WORLD ORIGIN when
            // nothing is tagged - which put the boat and six of the seven crates outside
            // the region entirely, so they never foamed.
            surface.decalRegionSize = new Vector2(300f, 300f);
        }

        /// <summary>
        /// Builds a material for <see cref="WaterDecal"/> from HDRP's own procedural decal
        /// graph. The component rejects anything whose shader is not a water decal subtarget,
        /// so this cannot be an ordinary Lit material.
        /// </summary>
        internal static Material DecalMaterial(string name, float type, bool deformation,
                                              bool foam, System.Action<Material> configure = null)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(DECAL_SHADER);
            if (shader == null)
            {
                Debug.LogError($"[WaterBuilder] Water decal shader missing at {DECAL_SHADER}. " +
                               "Wakes will be skipped.");
                return null;
            }

            TestMaterials.EnsureFolder(MAT_FOLDER);
            string path = $"{MAT_FOLDER}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;

            material.SetFloat("_TYPE", type);
            material.SetFloat("_AffectDeformation", deformation ? 1f : 0f);
            material.SetFloat("_AffectFoam", foam ? 1f : 0f);
            configure?.Invoke(material);

            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Attaches a decal. Position is in the parent's local space; only XZ matters, since
        /// a decal is projected straight down onto the water.
        /// </summary>
        internal static WaterDecal AddDecal(Transform parent, string name, Material material,
                                     Vector3 localPos, Vector2 regionSize, float amplitude,
                                     float surfaceFoam = 1f, float deepFoam = 1f)
        {
            if (material == null) return null;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var decal = go.AddComponent<WaterDecal>();
            decal.material = material;
            // ScaleInvariant: regionSize is meant to BE the size in metres. Inheriting the
            // hierarchy would scale it by the parent, and the crates are authored at 1.1.
            decal.scaleMode = DecalScaleMode.ScaleInvariant;
            decal.regionSize = regionSize;
            decal.amplitude = amplitude;
            decal.surfaceFoamDimmer = surfaceFoam;
            decal.deepFoamDimmer = deepFoam;
            decal.resolution = new Vector2Int(128, 128);
            // The shape is procedural and static; only the transform moves, so the atlas
            // entry needs rendering once rather than every frame.
            decal.updateMode = UnityEngine.CustomRenderTextureUpdateMode.OnLoad;
            return decal;
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

        private static void BuildFloatingProps(Transform parent, Material wood, Material ripple)
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

                // A small ring of disturbance. Bobbing in place it reads as ripples around
                // the crate; pushed along by the swell it smears into a short trail, which
                // is exactly what a half-submerged box adrift should do.
                AddDecal(crate.transform, "Ripple", ripple, Vector3.zero,
                    new Vector2(2.1f, 2.1f), amplitude: 0.05f,
                    surfaceFoam: 0.22f, deepFoam: 0.1f);
            }
        }

        /// <summary>
        /// Tugboat: a level collision deck on the root, and a hull child that does the
        /// pitching and rolling. See <see cref="BoatMotion"/> for why they are separate.
        /// </summary>
        private static void BuildBoat(Scene scene, Material hullPaint, Material deck, Material trim,
                                      Material bowWave, Material propWash)
        {
            var boat = new GameObject(BOAT_NAME);
            SceneManager.MoveGameObjectToScene(boat, scene);
            boat.transform.SetPositionAndRotation(BoatMooring,
                Quaternion.Euler(0f, BOAT_MOORING_HEADING, 0f));

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
            // Four pieces rather than one box, because the hold below needs an opening in
            // the deck. Top faces flush with the visual deck plate at y=0.80, so standing on
            // the level collider does not leave the player shin-deep in the planking.
            AddDeckPiece(boat, new Vector3(0f, 0.45f, -2.95f), new Vector3(5.2f, 0.7f, 7.1f));
            AddDeckPiece(boat, new Vector3(0f, 0.45f, 5.35f), new Vector3(5.2f, 0.7f, 2.3f));
            for (int i = -1; i <= 1; i += 2)
                AddDeckPiece(boat, new Vector3(i * 2.05f, 0.45f, HOLD_CENTRE_Z),
                             new Vector3(1.1f, 0.7f, HOLD_LENGTH));

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

            // Deepened from a 2.2 m box so it actually encloses the hold: the floor sits at
            // -2.6 and the hull has to reach past it, which puts 1.8 m of this boat under
            // water. That is a believable draft for a tug and it is what makes a walkway
            // below the waterline mean anything.
            //
            // Carved into four slabs around the hold's outer shell, because a solid box caps
            // the hatch: looking down the opening you were seeing this thing's top face at
            // y=0.6, which is why the hole read as blocked by red. The carve stops at the
            // shell rather than the interior so the hold's own walls fill the gap instead of
            // z-fighting with hull sitting in the same 30 cm.
            const float holdShellHalfW = HOLD_WIDTH * 0.5f + HOLD_WALL;    // 1.8
            const float holdShellHalfL = HOLD_LENGTH * 0.5f + HOLD_WALL;   // 2.1
            const float hullCentreY = -1.15f, hullHeight = 3.5f;
            float shellAftZ = HOLD_CENTRE_Z - holdShellHalfL;              // 0.3
            float shellFwdZ = HOLD_CENTRE_Z + holdShellHalfL;              // 4.5

            TestMaterials.Box("HullBody_Aft", hull,
                new Vector3(0f, hullCentreY, (-6.5f + shellAftZ) * 0.5f),
                new Vector3(5.2f, hullHeight, shellAftZ + 6.5f), hullPaint);
            TestMaterials.Box("HullBody_Fwd", hull,
                new Vector3(0f, hullCentreY, (shellFwdZ + 6.5f) * 0.5f),
                new Vector3(5.2f, hullHeight, 6.5f - shellFwdZ), hullPaint);
            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"HullBody_Side_{i}", hull,
                    new Vector3(i * (holdShellHalfW + 2.6f) * 0.5f, hullCentreY, HOLD_CENTRE_Z),
                    new Vector3(2.6f - holdShellHalfW, hullHeight, holdShellHalfL * 2f), hullPaint);
            var bow = TestMaterials.Box("Bow", hull, new Vector3(0f, -0.4f, 7.1f),
                new Vector3(3.4f, 2f, 2.6f), hullPaint);
            bow.transform.rotation = Quaternion.Euler(0f, 45f, 0f);

            // Deck plating, carved around the hold opening to match the collision deck.
            TestMaterials.Box("DeckPlate_Aft", hull, new Vector3(0f, 0.72f, -2.95f),
                new Vector3(5.2f, 0.16f, 7.1f), deck);
            TestMaterials.Box("DeckPlate_Fwd", hull, new Vector3(0f, 0.72f, 5.35f),
                new Vector3(5.2f, 0.16f, 2.3f), deck);
            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"DeckPlate_Side_{i}", hull,
                    new Vector3(i * 2.05f, 0.72f, HOLD_CENTRE_Z),
                    new Vector3(1.1f, 0.16f, HOLD_LENGTH), deck);

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
            BuildHold(boat.transform, hull, hullPaint, deck, trim);

            // Ship's dedicated tank, beside the wheel where a driver refuelling at port can
            // reach it without leaving the helm. Started well shy of full so running dry is
            // something a play session can actually reach.
            var tank = FuelSystemBuilder.BuildFuelTank(boat.transform,
                new Vector3(1.1f, 1.0f, -1.75f), capacityLiters: 90f, startingLiters: 35f, trim);
            var hso = new SerializedObject(helm);
            hso.FindProperty("fuelTank").objectReferenceValue = tank;
            hso.FindProperty("fuelBurnLitersPerHour").floatValue = 40f;
            hso.ApplyModifiedPropertiesWithoutUndo();

            // Wake. Both hang off the LEVEL root, so they stay square to the water while
            // the hull rolls - a decal is projected straight down, and letting it roll with
            // the visual would swing the wake out from under the boat.
            var bowDecal = AddDecal(boat.transform, "BowWave", bowWave, new Vector3(0f, 0f, 5.5f),
                new Vector2(7.5f, 9f), amplitude: 0.5f);

            // Narrow and behind the transom, roughly a propeller's width. The long trail is
            // not this shape - it comes from foam persistence smearing this source across
            // the water as the boat pulls away from what it just laid down.
            var washDecal = AddDecal(boat.transform, "PropWash", propWash, new Vector3(0f, 0f, -7.5f),
                new Vector2(2.6f, 7f), amplitude: 0.22f,
                surfaceFoam: 1f, deepFoam: 0.7f);

            // Both are driven to zero at rest by BoatWake; the values above are the
            // full-speed strengths it scales toward.
            var wake = boat.AddComponent<BoatWake>();
            var wso = new SerializedObject(wake);
            wso.FindProperty("bowWave").objectReferenceValue = bowDecal;
            wso.FindProperty("propWash").objectReferenceValue = washDecal;
            wso.FindProperty("fullEffectSpeed").floatValue = 6f;
            wso.FindProperty("bowAmplitude").floatValue = 0.5f;
            wso.FindProperty("washAmplitude").floatValue = 0.22f;
            wso.ApplyModifiedPropertiesWithoutUndo();

            // GET or add, never blindly add. BoatWake above and BoatRiderCarry below both
            // [RequireComponent] BoatMotion, so adding either of them has ALREADY put one on
            // the hull with default values - and AddComponent here then left a SECOND one.
            //
            // Two of them is not a cosmetic duplicate: both write the transform in LateUpdate
            // every frame, and the stray one has no helm reference and is not moored, so it
            // drives the boat round BoatMotion's default patrol circle while the configured one
            // holds the mooring. Which of the two you actually got came down to component
            // order. Every tug built before 2026-07-29 shipped with this.
            var motion = boat.GetComponent<BoatMotion>();
            if (motion == null) motion = boat.AddComponent<BoatMotion>();

            var so = new SerializedObject(motion);
            so.FindProperty("helm").objectReferenceValue = helm;
            so.FindProperty("hullVisual").objectReferenceValue = hull;
            // Holds the authored pose and only answers the waves - until someone takes the
            // wheel, which overrides mooring outright. See BoatMotion.
            so.FindProperty("moored").boolValue = true;
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
            // Reaches from just under the keel to head height above the deck, so someone
            // down in the hold counts as a rider too. Narrowed to 5.4 wide (the deck itself
            // is 5.2) specifically to leave the boarding ladder at x=-2.75 outside it -
            // otherwise a swimmer hanging on the rungs gets towed along by the hull.
            cso.FindProperty("deckCenter").vector3Value = new Vector3(0f, 0.4f, 0f);
            cso.FindProperty("deckSize").vector3Value = new Vector3(5.4f, 6.8f, 14.4f);
            cso.ApplyModifiedPropertiesWithoutUndo();

            // Loose cargo gets carried too, or a crate set down on this deck just slides aft
            // until it hits the transom - the deck teleports rather than moving, so PhysX never
            // gives it any of the boat's motion. Same volume as the riders', which also covers
            // the hold. Added after BoatMotion so its LateUpdate runs second.
            var cargo = boat.AddComponent<DeckCargoCarry>();
            var dso = new SerializedObject(cargo);
            dso.FindProperty("deckCenter").vector3Value = new Vector3(0f, 0.4f, 0f);
            dso.FindProperty("deckSize").vector3Value = new Vector3(5.4f, 6.8f, 14.4f);
            dso.FindProperty("grip").floatValue = 6f;
            dso.FindProperty("angularGrip").floatValue = 14f;
            dso.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddDeckPiece(GameObject boat, Vector3 centre, Vector3 size)
        {
            var piece = boat.AddComponent<BoxCollider>();
            piece.center = centre;
            piece.size = size;
        }

        /// <summary>
        /// A hold below the waterline: the reference case for a walkspace that is under the
        /// water. Floor at y=-2.6 against a waterline at y=-1.1, so a metre and a half of
        /// this room is below the sea outside it.
        ///
        /// Three separate things have to agree for that to work, and HDRP only does the
        /// first by itself:
        ///
        /// - THE SURFACE. The exclusion mesh tags these pixels in the stencil buffer and the
        ///   water surface is rejected there, so the sea is not drawn across the inside of
        ///   the room. Purely geometry - no code runs for this.
        /// - THE UNDERWATER EFFECT, which is a plain box test against the water's
        ///   volumeBounds and ignores excluders completely.
        /// - SWIMMING, which compares the surface height to your chest and does not care
        ///   that there is a deck in between.
        ///
        /// The last two go through <see cref="DryHullVolume"/>.
        ///
        /// Split exactly the way the deck already is: the room's VISUALS hang off the rolling
        /// hull so the hold heels over with everything else, while the surfaces you actually
        /// stand on are level colliders on the root. A CharacterController capsule is always
        /// world-upright and would slide down a tilted floor.
        ///
        /// The exclusion mesh and the dry volume stay level too, and for the exclusion that
        /// is not just convenience: rolled with the hull, its top edge would tilt out of the
        /// water plane and dip under the surface on the low side, letting the sea render
        /// back into the room at exactly the moment the boat is working hardest.
        /// </summary>
        private static void BuildHold(Transform boat, Transform hull, Material hullPaint,
                                      Material deck, Material trim)
        {
            var shell = TestMaterials.Node("Hold", hull, Vector3.zero);

            float wallHeight = HOLD_CEILING_Y - HOLD_FLOOR_Y;
            float wallCentreY = (HOLD_CEILING_Y + HOLD_FLOOR_Y) * 0.5f;
            float outerWidth = HOLD_WIDTH + HOLD_WALL * 2f;
            float outerLength = HOLD_LENGTH + HOLD_WALL * 2f;

            // Floor is wider than the opening so the walls stand on it rather than beside it.
            var parts = new System.Collections.Generic.List<(string, Vector3, Vector3, Material)>
            {
                ("Floor", new Vector3(0f, HOLD_FLOOR_Y - HOLD_WALL * 0.5f, HOLD_CENTRE_Z),
                          new Vector3(outerWidth, HOLD_WALL, outerLength), deck),
            };
            for (int i = -1; i <= 1; i += 2)
            {
                parts.Add(($"Wall_Side_{i}",
                    new Vector3(i * (HOLD_WIDTH + HOLD_WALL) * 0.5f, wallCentreY, HOLD_CENTRE_Z),
                    new Vector3(HOLD_WALL, wallHeight, outerLength), hullPaint));
                parts.Add(($"Wall_End_{i}",
                    new Vector3(0f, wallCentreY, HOLD_CENTRE_Z + i * (HOLD_LENGTH + HOLD_WALL) * 0.5f),
                    new Vector3(HOLD_WIDTH, wallHeight, HOLD_WALL), hullPaint));
            }

            foreach (var (name, centre, size, material) in parts)
            {
                var box = TestMaterials.Box(name, shell, centre, size, material);
                // The blanket strip in BuildBoat has already run by now, so these have to
                // shed their own primitive colliders - otherwise the room gets a second,
                // tilting set of walls sitting inside the level ones.
                foreach (var stray in box.GetComponentsInChildren<Collider>())
                    Object.DestroyImmediate(stray);

                var solid = boat.gameObject.AddComponent<BoxCollider>();
                solid.center = centre;
                solid.size = size;
            }

            BuildHoldLadder(boat, trim);

            // --- water exclusion + the dry-interior registration, both on the LEVEL root ---
            var exclusion = new GameObject("HoldWater");
            exclusion.transform.SetParent(boat, false);

            var excluder = exclusion.AddComponent<WaterExcluder>();

            var exclusionMaterial = AssetDatabase.LoadAssetAtPath<Material>(EXCLUSION_MATERIAL);
            if (exclusionMaterial == null)
                Debug.LogError($"[WaterBuilder] Water exclusion material missing at {EXCLUSION_MATERIAL}. " +
                               "The hold will render flooded.");

            // The box is the interior exactly. Exclusion is depth-tested against the opaque
            // buffer, so a box that poked out through the hull sides would start rejecting
            // the open sea alongside the boat as well. Its top is a little above the
            // waterline so a passing crest cannot spill over the edge of the exclusion.
            //
            // TWO meshes, one facing each way, because HDRP's exclusion shader is Cull Back.
            // A single box only tags pixels when it is viewed from OUTSIDE - which covers
            // looking down the hatch from the deck and nothing else. Stand IN the room and
            // every face of that box is back-facing, so nothing is written and the sea
            // renders straight through the hold at eye level. They never conflict: whichever
            // way the camera is, exactly one of them survives culling.
            //
            // What is NOT obvious is that the two want opposite sizes, because a stencil tag
            // only lands where its fragment is nearer than the opaque surface behind it:
            //
            //   OUTWARD, seen from the deck, presents its TOP face - already in front of
            //   everything in the room - so it should hug the opening and cover all of it.
            //
            //   INWARD, seen from inside, presents its FAR faces. Anything sticking into the
            //   room sits in front of those and defeats them. That is the water that was
            //   still showing at the ladder: the box's aft face was 6 cm BEHIND the rungs, so
            //   every pixel of ladder went untagged and the sea drew over it. So the inward
            //   box has to fit inside the room's AIR, clear of the fittings - and shrinking
            //   costs nothing, because from inside a smaller concentric box subtends a LARGER
            //   solid angle and still fills the view.
            float roomCentreY = (HOLD_FLOOR_Y + HOLD_CEILING_Y) * 0.5f;
            float roomHeight = HOLD_CEILING_Y - HOLD_FLOOR_Y;

            var outward = AddExclusionMesh(exclusion.transform, "Water Excluder Renderer",
                Resources.GetBuiltinResource<Mesh>("Cube.fbx"),
                new Vector3(0f, roomCentreY, HOLD_CENTRE_Z),
                new Vector3(HOLD_WIDTH - HOLD_EXCLUSION_INSET * 2f, roomHeight,
                            HOLD_LENGTH - HOLD_EXCLUSION_INSET * 2f),
                exclusionMaterial);

            // Top stays at the deck underside so a jumping player cannot rise out through it
            // - that was one flicker of open water per jump, at the top of the arc.
            float innerFloor = HOLD_FLOOR_Y + HOLD_INTERIOR_FLOOR_CLEARANCE;
            AddExclusionMesh(exclusion.transform, "Water Excluder Renderer (Interior)",
                InvertedCubeMesh(),
                new Vector3(0f, (innerFloor + HOLD_CEILING_Y) * 0.5f, HOLD_CENTRE_Z),
                new Vector3(HOLD_WIDTH - HOLD_INTERIOR_INSET * 2f, HOLD_CEILING_Y - innerFloor,
                            HOLD_LENGTH - HOLD_INTERIOR_INSET * 2f),
                exclusionMaterial);

            // WaterExcluder keeps both of its fields internal, so they can only be written
            // through the serialized object. Neither is read at runtime - the exclusion pass
            // just collects renderers using that material - but leaving them empty makes the
            // component's own inspector claim it has nothing to exclude.
            var eso = new SerializedObject(excluder);
            eso.FindProperty("m_ExclusionRenderer").objectReferenceValue = outward;
            eso.FindProperty("m_InternalMesh").objectReferenceValue =
                Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            eso.ApplyModifiedPropertiesWithoutUndo();

            // The dry volume wants the OPPOSITE treatment to the inward exclusion box: run it
            // out through the plating rather than inset from it. Nobody can stand inside a
            // solid bulkhead, so the extra space cannot make anyone wrongly dry - it is free
            // margin that stops the fog and the swim state flickering when a player presses
            // against a wall or jumps. Floor plating to the top of the deck.
            float dryFloor = HOLD_FLOOR_Y - HOLD_WALL;
            float dryCeiling = HOLD_CEILING_Y + 0.16f;   // through the deck plate
            var dry = exclusion.AddComponent<DryHullVolume>();
            var dso = new SerializedObject(dry);
            dso.FindProperty("center").vector3Value = new Vector3(
                0f, (dryFloor + dryCeiling) * 0.5f, HOLD_CENTRE_Z);
            dso.FindProperty("size").vector3Value = new Vector3(
                HOLD_WIDTH + HOLD_WALL * 2f, dryCeiling - dryFloor, HOLD_LENGTH + HOLD_WALL * 2f);
            dso.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject AddExclusionMesh(Transform parent, string name, Mesh mesh,
                                                   Vector3 centre, Vector3 size, Material material)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre;
            go.transform.localScale = size;

            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        /// <summary>
        /// A unit cube wound inside-out, so it renders when the camera is INSIDE it.
        ///
        /// Negative scale is not a substitute: Unity flips the front-face winding for
        /// negatively scaled renderers precisely so that geometry keeps facing the same way,
        /// which cancels the trick out.
        /// </summary>
        private static Mesh InvertedCubeMesh()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(INVERTED_CUBE);
            if (existing != null) return existing;

            var mesh = Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx"));
            mesh.name = "InvertedCube";

            var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
                (triangles[i], triangles[i + 1]) = (triangles[i + 1], triangles[i]);
            mesh.triangles = triangles;

            var normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++) normals[i] = -normals[i];
            mesh.normals = normals;
            mesh.RecalculateBounds();

            TestMaterials.EnsureFolder(Path.GetDirectoryName(INVERTED_CUBE).Replace('\\', '/'));
            AssetDatabase.CreateAsset(mesh, INVERTED_CUBE);
            return mesh;
        }

        /// <summary>
        /// Ladder down into the hold, on its aft bulkhead. Local +Z faces into the room, so
        /// the climber hangs inside it facing the rungs.
        ///
        /// On the LEVEL root for the same reason the boarding ladder is: the climb track and
        /// its exit have to stay square to the deck being stepped out onto.
        /// </summary>
        private static void BuildHoldLadder(Transform boat, Material trim)
        {
            var ladderGO = new GameObject("HoldLadder");
            ladderGO.transform.SetParent(boat, false);
            ladderGO.transform.localPosition =
                new Vector3(0f, HOLD_FLOOR_Y, HOLD_CENTRE_Z - HOLD_LENGTH * 0.5f + 0.02f);

            float climb = 0.8f - HOLD_FLOOR_Y;   // floor to the top of the deck

            var grab = ladderGO.AddComponent<BoxCollider>();
            grab.center = new Vector3(0f, climb * 0.5f, 0.2f);
            grab.size = new Vector3(0.9f, climb, 0.7f);
            grab.isTrigger = true;

            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"Stile_{i}", ladderGO.transform,
                    new Vector3(i * 0.3f, climb * 0.5f, 0.06f),
                    new Vector3(0.09f, climb, 0.09f), trim);
            int rungs = Mathf.RoundToInt(climb / 0.45f);
            for (int r = 0; r < rungs; r++)
                TestMaterials.Box($"Rung_{r}", ladderGO.transform,
                    new Vector3(0f, 0.25f + r * 0.45f, 0.06f),
                    new Vector3(0.68f, 0.07f, 0.07f), trim);

            var ladder = ladderGO.AddComponent<Ladder>();
            var so = new SerializedObject(ladder);
            so.FindProperty("climbHeight").floatValue = climb;
            so.FindProperty("climbSpeed").floatValue = 2.4f;
            so.FindProperty("standOffset").vector3Value = new Vector3(0f, 0f, 0.45f);
            // Out of the hatch and aft onto solid deck, clear of the opening it just left.
            so.FindProperty("topExitLocal").vector3Value = new Vector3(0f, climb + 0.15f, -0.75f);
            so.ApplyModifiedPropertiesWithoutUndo();
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
            // Forward of the hold. Its top exit lands inboard at x=-1.45, which used to be
            // clear deck and is now the middle of the hatch - climbing aboard from the water
            // would have dropped you straight down into the hold.
            ladderGO.transform.localPosition = new Vector3(-2.75f, -2.6f, 5.4f);
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
