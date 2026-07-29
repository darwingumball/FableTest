using Game.World;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// A crabber: twenty metres, wheelhouse forward, and the rest of it open working deck
    /// with a picking crane and a lashing area. The vessel the crane and cargo systems are
    /// tested on, deliberately kept to the size of a real crab boat rather than growing into
    /// a container ship - a big empty deck is the point, not a big boat.
    ///
    /// Built as its OWN generator rather than as a second mode of <see cref="WaterBuilder"/>,
    /// because that one carries a heavily tuned tug with a below-waterline hold and water
    /// exclusion meshes. Parameterising it into two vessels would put both at risk every time
    /// either changed.
    ///
    /// The same split as the tug governs everything here: things you STAND ON or AIM AT go on
    /// the level root, things you LOOK AT go on the rolling hull child. See
    /// <see cref="BoatMotion"/>. The crane is level-root for both reasons - its console has
    /// to stay under the crosshair, and its rope has to hang plumb.
    ///
    /// Menu: Game/Setup/Build Crab Boat. Idempotent. Run Build Water first (this moors
    /// alongside the tug and needs the lake to exist).
    /// </summary>
    public static class CrabBoatBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string BOAT_NAME = "TestCrabBoat";

        // --- hull, all boat-local. Deck top at 0.80, matching the tug so a player stepping
        // between the two does not change height. ---
        private const float HALF_BEAM = 3.2f;
        private const float STERN_Z = -10f;
        private const float BOW_Z = 9f;
        private const float DECK_TOP = 0.8f;
        private const float FREEBOARD = 1.2f;
        private const float HULL_BOTTOM = -3.1f;

        // --- wheelhouse, forward. Doorway aft so the helm inside is reachable. ---
        private const float HOUSE_AFT_Z = 4.3f;
        private const float HOUSE_FWD_Z = 8.9f;
        private const float HOUSE_HALF_WIDTH = 2.2f;
        private const float HOUSE_TOP = 4.6f;
        private const float ROOF_TOP = 4.8f;
        private const float WALL = 0.2f;
        private const float DOOR_HALF_WIDTH = 0.8f;
        private const float DOOR_TOP = 2.9f;

        // --- bulwarks around the working deck ---
        private const float BULWARK_TOP = 1.9f;

        // --- crane. Starboard side against the wheelhouse, so the boom sweeps the whole
        // working deck aft of it without a pedestal in the middle of the deck. ---
        private static readonly Vector3 CranePosition = new(2.6f, DECK_TOP, 2.6f);
        private const float PEDESTAL_HEIGHT = 2.4f;
        private const float BOOM_LENGTH = 11.5f;

        // --- lashing area. Sized so the crane can reach every corner of it: the far corner is
        // 10.8 m from the pedestal against a maximum reach of 11.4 at minimum elevation, and
        // the near corner 1.9 m against a minimum reach of 1.6 at maximum elevation. Both
        // margins are thin on purpose - a lashing area the crane cannot service is worse than
        // a smaller one. ---
        private static readonly Vector3 ZoneCentre = new(-0.4f, 2.2f, -3.0f);
        private static readonly Vector3 ZoneSize = new(4.8f, 2.8f, 7.6f);

        // Moored off the beach beside the tug, far enough apart that neither hull is inside
        // the other's rider volume.
        private static readonly Vector3 Mooring = new(11f, 0f, 80f);
        private const float MOORING_HEADING = -14f;

        [MenuItem("Game/Setup/Build Crab Boat")]
        public static void Build()
        {
            if (!MenuSceneBuilder.Ready("CrabBoatBuilder")) return;

            // The placement materials have to exist before the zone outline can reference one.
            CargoBuilder.Build();

            var hullPaint = TestMaterials.Lit("TB_CrabHull", new Color(0.72f, 0.73f, 0.70f), 0.35f, 0.1f);
            var boot = TestMaterials.Lit("TB_CrabBoot", new Color(0.10f, 0.16f, 0.28f), 0.45f, 0.15f);
            var deck = TestMaterials.Lit("TB_Deck", new Color(0.30f, 0.24f, 0.17f), 0.20f, 0f);
            var trim = TestMaterials.Lit("TB_Trim", new Color(0.12f, 0.13f, 0.16f), 0.55f, 0.6f);
            var outlineMaterial = CargoBuilder.GhostMaterial("Zone_Outline",
                new Color(0.35f, 1f, 0.55f, 0.16f));

            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            foreach (var existing in scene.GetRootGameObjects())
                if (existing.name == BOAT_NAME)
                    Object.DestroyImmediate(existing);

            var boat = new GameObject(BOAT_NAME);
            SceneManager.MoveGameObjectToScene(boat, scene);
            boat.transform.SetPositionAndRotation(Mooring,
                Quaternion.Euler(0f, MOORING_HEADING, 0f));

            // Needed by the helm, the crane and every anchor on board: CargoAnchor names
            // itself relative to the nearest NetworkObject above it.
            boat.AddComponent<NetworkObject>();

            // Without a Rigidbody, PhysX treats a deck that teleports every frame as static
            // geometry and rebuilds the broadphase constantly, which is how riders end up
            // falling through a heaving deck. Kinematic makes it a legitimate mover.
            var body = boat.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;

            var hull = TestMaterials.Node("Hull", boat.transform, Vector3.zero);

            BuildHull(hull, hullPaint, boot, deck);
            BuildDeckColliders(boat);
            BuildBulwarks(boat, hull, hullPaint, trim);
            BuildWheelhouse(boat, hull, hullPaint, trim);
            BuildDeckLights(hull);

            // Everything under Hull is a VISUAL. CreatePrimitive hands out a collider with
            // every box and those tilt with the roll - the deck plate's would sit a hair above
            // the level deck collider, so the player would end up standing on the tilting one
            // and the entire reason the two are separate would be quietly defeated. Colliders
            // for the solid structures were added to the root alongside each visual.
            foreach (var stray in hull.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(stray);

            // Everything below authors a LEVEL logic node on the root and, where it has
            // anything to look at, a matching VISUAL twin under the rolling hull. See
            // RollingTwin - the split is what lets the crane and the ladders lean with the boat
            // without their aim boxes and climb tracks leaning away from the player.
            var helm = BuildHelm(boat.transform, trim, deck);
            BuildBoardingLadder(boat.transform, hull, trim);
            BuildWheelhouseLadder(boat.transform, hull, trim);
            var zone = BuildCargoZone(boat.transform, hull, outlineMaterial);
            BuildCrane(boat.transform, hull, trim, hullPaint);

            BuildMotion(boat, hull, helm);
            BuildWake(boat);

            // Read while the objects still exist. CloseScene below destroys them, and touching
            // a component afterwards to log its index throws instead of reporting anything.
            int zoneIndex = zone.Index;
            int anchorCount = boat.GetComponentsInChildren<CargoAnchor>(true).Length;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();

            Debug.Log($"[CrabBoatBuilder] '{BOAT_NAME}' moored at {Mooring}. " +
                      $"Lashing area {ZoneSize.x}x{ZoneSize.z} m at boat-local {ZoneCentre}; " +
                      $"crane boom {BOOM_LENGTH} m from {CranePosition}. " +
                      $"{anchorCount} cargo anchor(s) aboard, lashing area is index {zoneIndex}.");
        }

        // ---------------- hull ----------------

        private static void BuildHull(Transform hull, Material paint, Material boot, Material deck)
        {
            float length = BOW_Z - STERN_Z;
            float centreZ = (BOW_Z + STERN_Z) * 0.5f;
            float hullHeight = 0.5f - HULL_BOTTOM;
            float hullCentreY = (0.5f + HULL_BOTTOM) * 0.5f;

            TestMaterials.Box("HullBody", hull, new Vector3(0f, hullCentreY, centreZ),
                new Vector3(HALF_BEAM * 2f, hullHeight, length), paint);

            // A waterline stripe. Cheap, and it is what makes a grey box read as a hull rather
            // than a crate - the eye picks up where the boat sits in the water immediately.
            TestMaterials.Box("BootTop", hull, new Vector3(0f, -1.2f, centreZ),
                new Vector3(HALF_BEAM * 2f + 0.04f, 0.45f, length + 0.04f), boot);

            var bow = TestMaterials.Box("Bow", hull, new Vector3(0f, -1.0f, BOW_Z + 1.1f),
                new Vector3(4.2f, 3.0f, 3.2f), paint);
            bow.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);

            TestMaterials.Box("DeckPlate", hull, new Vector3(0f, DECK_TOP - 0.08f, centreZ),
                new Vector3(HALF_BEAM * 2f, 0.16f, length), deck);
        }

        private static void BuildDeckColliders(GameObject boat)
        {
            // One piece: unlike the tug there is no hold below, so nothing has to be carved
            // around. Top face flush with the visual deck plate so standing on the level
            // collider does not leave the player shin-deep in the planking.
            var deck = boat.AddComponent<BoxCollider>();
            deck.center = new Vector3(0f, DECK_TOP - 0.35f, (BOW_Z + STERN_Z) * 0.5f);
            deck.size = new Vector3(HALF_BEAM * 2f, 0.7f, BOW_Z - STERN_Z);
        }

        private static void BuildBulwarks(GameObject boat, Transform hull, Material paint, Material trim)
        {
            float height = BULWARK_TOP - DECK_TOP;
            float centreY = (BULWARK_TOP + DECK_TOP) * 0.5f;
            float aftDeckLength = HOUSE_AFT_Z - STERN_Z;
            float aftDeckCentre = (HOUSE_AFT_Z + STERN_Z) * 0.5f;

            // Solid walls, not rails: on a working deck they are what stops loose cargo - and
            // the player - going over the side when the hull leans.
            for (int i = -1; i <= 1; i += 2)
                Solid(boat, hull, $"Bulwark_{i}",
                    new Vector3(i * (HALF_BEAM - WALL * 0.5f), centreY, aftDeckCentre),
                    new Vector3(WALL, height, aftDeckLength), paint);

            Solid(boat, hull, "Transom",
                new Vector3(0f, centreY, STERN_Z + WALL * 0.5f),
                new Vector3(HALF_BEAM * 2f, height, WALL), paint);

            // Capping rail, purely to catch the light along the top edge.
            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"BulwarkCap_{i}", hull,
                    new Vector3(i * (HALF_BEAM - WALL * 0.5f), BULWARK_TOP + 0.04f, aftDeckCentre),
                    new Vector3(WALL + 0.1f, 0.08f, aftDeckLength), trim);
        }

        private static void BuildWheelhouse(GameObject boat, Transform hull, Material paint, Material trim)
        {
            float centreZ = (HOUSE_AFT_Z + HOUSE_FWD_Z) * 0.5f;
            float length = HOUSE_FWD_Z - HOUSE_AFT_Z;
            float height = HOUSE_TOP - DECK_TOP;
            float centreY = (HOUSE_TOP + DECK_TOP) * 0.5f;

            Solid(boat, hull, "House_Fwd",
                new Vector3(0f, centreY, HOUSE_FWD_Z - WALL * 0.5f),
                new Vector3(HOUSE_HALF_WIDTH * 2f, height, WALL), paint);

            for (int i = -1; i <= 1; i += 2)
                Solid(boat, hull, $"House_Side_{i}",
                    new Vector3(i * (HOUSE_HALF_WIDTH - WALL * 0.5f), centreY, centreZ),
                    new Vector3(WALL, height, length), paint);

            // Aft face with a doorway, so the helm inside is walk-in rather than decoration.
            float pierWidth = HOUSE_HALF_WIDTH - DOOR_HALF_WIDTH;
            for (int i = -1; i <= 1; i += 2)
                Solid(boat, hull, $"House_Aft_{i}",
                    new Vector3(i * (DOOR_HALF_WIDTH + pierWidth * 0.5f), centreY,
                                HOUSE_AFT_Z + WALL * 0.5f),
                    new Vector3(pierWidth, height, WALL), paint);

            Solid(boat, hull, "House_Aft_Lintel",
                new Vector3(0f, (HOUSE_TOP + DOOR_TOP) * 0.5f, HOUSE_AFT_Z + WALL * 0.5f),
                new Vector3(DOOR_HALF_WIDTH * 2f, HOUSE_TOP - DOOR_TOP, WALL), paint);

            // Roof: the crane operator's station. High enough to see the whole working deck,
            // which is the reason the controls are up here and not on the pedestal.
            Solid(boat, hull, "House_Roof",
                new Vector3(0f, (ROOF_TOP + HOUSE_TOP) * 0.5f, centreZ),
                new Vector3(HOUSE_HALF_WIDTH * 2f + 0.4f, ROOF_TOP - HOUSE_TOP, length + 0.4f),
                paint);

            // Railing round the roof, so working the crane up here is not a fall hazard.
            float railHeight = 0.7f;
            float railY = ROOF_TOP + railHeight * 0.5f;
            float roofHalfWidth = HOUSE_HALF_WIDTH + 0.2f;
            float roofHalfLength = length * 0.5f + 0.2f;
            for (int i = -1; i <= 1; i += 2)
            {
                Solid(boat, hull, $"RoofRail_X{i}",
                    new Vector3(i * roofHalfWidth, railY, centreZ),
                    new Vector3(0.08f, railHeight, roofHalfLength * 2f), trim);
                Solid(boat, hull, $"RoofRail_Z{i}",
                    new Vector3(0f, railY, centreZ + i * roofHalfLength),
                    new Vector3(roofHalfWidth * 2f, railHeight, 0.08f), trim);
            }

            // Windows, as a dark band rather than transparent geometry - this is greybox.
            TestMaterials.Box("House_Windows", hull,
                new Vector3(0f, 2.9f, HOUSE_FWD_Z - WALL - 0.02f),
                new Vector3(HOUSE_HALF_WIDTH * 2f - 0.5f, 1.0f, 0.06f), trim);

            TestMaterials.Box("Mast", hull, new Vector3(0f, ROOF_TOP + 1.6f, centreZ + 0.6f),
                new Vector3(0.16f, 3.2f, 0.16f), trim);
        }

        /// <summary>
        /// A structure that is both seen and solid: the visual goes on the rolling hull so it
        /// leans with the boat, and a matching invisible box goes on the level root so a
        /// world-upright CharacterController has something square to stand against. The
        /// mismatch between the two is the roll angle, which is a few degrees in ordinary
        /// water - the same trade the deck itself has always made.
        /// </summary>
        private static void Solid(GameObject boat, Transform hull, string name,
            Vector3 centre, Vector3 size, Material material)
        {
            TestMaterials.Box(name, hull, centre, size, material);
            var box = boat.AddComponent<BoxCollider>();
            box.center = centre;
            box.size = size;
        }

        /// <summary>
        /// A visual node under the ROLLING hull at the same boat-local pose as a logic node on
        /// the LEVEL root. The two coincide exactly at zero roll and separate by the roll angle.
        ///
        /// This is the fitting-scale version of the deck's own compromise, and it is what stops
        /// the crane and the ladders standing bolt upright out of a hull that is leaning: the
        /// parts you LOOK at lean, while the aim boxes, climb tracks and top exits you have to
        /// hit with a crosshair stay square to the deck you are standing on.
        /// </summary>
        private static Transform RollingTwin(Transform hull, string name, Transform logic)
        {
            var twin = new GameObject(name);
            twin.transform.SetParent(hull, false);
            twin.transform.localPosition = logic.localPosition;
            twin.transform.localRotation = logic.localRotation;
            return twin.transform;
        }

        private static void BuildDeckLights(Transform hull)
        {
            // Point lights, NOT shadowed spots. A shadowed light on this boat could not use
            // the cached shadow atlas that everything ashore now uses - the boat moves, so a
            // cached map would be baked at the mooring and stay there - and an every-frame
            // shadow map on a moving vessel is exactly the cost that was just taken out of
            // the town.
            TestMaterials.PointLight("DeckFlood", hull,
                new Vector3(0f, ROOF_TOP - 0.1f, HOUSE_AFT_Z - 0.3f),
                new Color(1f, 0.93f, 0.78f), 14000f, 16f, volumetric: 1.3f,
                group: "boat", nightBoost: 2.2f);

            TestMaterials.PointLight("MastLight", hull,
                new Vector3(0f, ROOF_TOP + 3.1f, (HOUSE_AFT_Z + HOUSE_FWD_Z) * 0.5f + 0.6f),
                new Color(0.75f, 0.9f, 1f), 2600f, 9f, volumetric: 1.5f,
                group: "boat", nightBoost: 2.4f);
        }

        // ---------------- fittings ----------------

        /// <summary>
        /// The wheel, inside the wheelhouse. Its own child object rather than a component on
        /// the root, because InteractionSystem resolves interactables with
        /// GetComponentInParent: on the root it would answer for every collider on the boat,
        /// and standing anywhere near the hull would offer to hand you the helm.
        /// </summary>
        private static BoatHelm BuildHelm(Transform boat, Material trim, Material wood)
        {
            var helmGO = new GameObject("Helm");
            helmGO.transform.SetParent(boat, false);
            helmGO.transform.localPosition = new Vector3(0f, DECK_TOP + 0.55f, HOUSE_FWD_Z - 1.3f);

            var box = helmGO.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.1f, 0f);
            box.size = new Vector3(0.9f, 0.9f, 0.5f);
            box.isTrigger = true;   // aimable, but must not shove the driver standing at it

            TestMaterials.Box("Binnacle", helmGO.transform, new Vector3(0f, -0.35f, 0f),
                new Vector3(0.4f, 0.9f, 0.4f), trim);
            var wheel = TestMaterials.Box("Wheel", helmGO.transform, new Vector3(0f, 0.15f, 0f),
                new Vector3(0.72f, 0.72f, 0.09f), wood);
            wheel.transform.localRotation = Quaternion.Euler(18f, 0f, 45f);

            var helm = helmGO.AddComponent<BoatHelm>();
            var so = new SerializedObject(helm);
            so.FindProperty("boundsCentre").vector2Value = new Vector2(0f, WaterBuilder.LAKE_CENTRE_Z);
            so.FindProperty("boundsHalfExtents").vector2Value =
                new Vector2(WaterBuilder.WATER_SPAN * 0.5f - 20f, WaterBuilder.WATER_SPAN * 0.5f - 20f);
            // A twenty-metre boat is slower to answer than the tug and slower to stop.
            so.FindProperty("maxSpeed").floatValue = 7.5f;
            so.FindProperty("acceleration").floatValue = 0.8f;
            so.FindProperty("dragDeceleration").floatValue = 0.3f;
            so.FindProperty("turnRateDegrees").floatValue = 19f;
            so.ApplyModifiedPropertiesWithoutUndo();
            return helm;
        }

        /// <summary>
        /// Boarding ladder on the port quarter, reaching from below the waterline to the deck.
        /// On the LEVEL root, not the rolling hull: the climb track and its top exit have to
        /// stay square to the deck the climber steps onto, and a twenty-degree roll would
        /// throw that exit most of a metre sideways - straight over the bulwark.
        ///
        /// Aft of the lashing area on purpose, so climbing aboard never lands the player on
        /// top of stowed cargo.
        /// </summary>
        private static void BuildBoardingLadder(Transform boat, Transform hull, Material trim)
        {
            var ladderGO = new GameObject("BoardingLadder");
            ladderGO.transform.SetParent(boat, false);
            ladderGO.transform.localPosition = new Vector3(-(HALF_BEAM + 0.15f), -2.6f, -8f);
            // Local +Z faces outboard to port, so the climber hangs off the outside of the
            // hull and faces in toward the rungs.
            ladderGO.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);

            // Generous grab volume - it has to be catchable from the water while the swell is
            // lifting you past it.
            var grab = ladderGO.AddComponent<BoxCollider>();
            grab.center = new Vector3(0f, 1.95f, 0.2f);
            grab.size = new Vector3(0.9f, 4.6f, 0.7f);
            grab.isTrigger = true;

            Rungs(RollingTwin(hull, "BoardingLadderRig", ladderGO.transform), trim, 4.2f, 9);

            var ladder = ladderGO.AddComponent<Ladder>();
            var so = new SerializedObject(ladder);
            so.FindProperty("climbHeight").floatValue = 3.9f;
            so.FindProperty("climbSpeed").floatValue = 2.4f;
            so.FindProperty("standOffset").vector3Value = new Vector3(0f, 0f, 0.45f);
            // Over the bulwark - whose top is a metre above the deck - and inboard onto the
            // deck plate. Ladder.Release teleports to this point, so it clears the bulwark
            // collider outright rather than having to be climbed over.
            so.FindProperty("topExitLocal").vector3Value = new Vector3(0f, 3.7f, -1.5f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Deck to wheelhouse roof, up the aft face beside the doorway.</summary>
        private static void BuildWheelhouseLadder(Transform boat, Transform hull, Material trim)
        {
            var ladderGO = new GameObject("RoofLadder");
            ladderGO.transform.SetParent(boat, false);
            ladderGO.transform.localPosition = new Vector3(-1.6f, DECK_TOP, HOUSE_AFT_Z - 0.15f);
            // Local +Z faces aft, so the climber hangs off the back of the wheelhouse.
            ladderGO.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            var grab = ladderGO.AddComponent<BoxCollider>();
            grab.center = new Vector3(0f, 2.2f, 0.2f);
            grab.size = new Vector3(0.9f, 4.4f, 0.7f);
            grab.isTrigger = true;

            Rungs(RollingTwin(hull, "RoofLadderRig", ladderGO.transform), trim, 4.4f, 9);

            var ladder = ladderGO.AddComponent<Ladder>();
            var so = new SerializedObject(ladder);
            so.FindProperty("climbHeight").floatValue = 4.4f;
            so.FindProperty("climbSpeed").floatValue = 2.4f;
            so.FindProperty("standOffset").vector3Value = new Vector3(0f, 0f, 0.45f);
            // Onto the roof, forward of the ladder head and clear of the rail.
            so.FindProperty("topExitLocal").vector3Value = new Vector3(0f, 4.35f, -1.2f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Rungs(Transform ladder, Material trim, float height, int count)
        {
            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"Stile_{i}", ladder,
                    new Vector3(i * 0.3f, height * 0.5f, 0.06f),
                    new Vector3(0.09f, height, 0.09f), trim);
            for (int r = 0; r < count; r++)
                TestMaterials.Box($"Rung_{r}", ladder,
                    new Vector3(0f, 0.2f + r * (height - 0.4f) / (count - 1), 0.06f),
                    new Vector3(0.68f, 0.07f, 0.07f), trim);

            // Rungs are for climbing, and Ladder already owns that. Colliders here would
            // catch the climber on the way past every single one.
            foreach (var stray in ladder.GetComponentsInChildren<Collider>())
                if (!stray.isTrigger) Object.DestroyImmediate(stray);
        }

        // ---------------- cargo ----------------

        private static PlacementZone BuildCargoZone(Transform boat, Transform hull,
            Material outlineMaterial)
        {
            // At the region centre with a zero offset, so cargo's replicated local pose is
            // relative to the middle of the lashing area and stays readable in the inspector.
            var zoneGO = new GameObject("CargoZone");
            zoneGO.transform.SetParent(boat, false);
            zoneGO.transform.localPosition = ZoneCentre;

            // The border is PAINT ON THE DECK, so it goes on the rolling hull and stays glued
            // to the planking. The logical region behind it stays level with the deck collider,
            // and the ghost - not the border - is the authority on where cargo will actually
            // land. They agree in ordinary water and diverge by the roll angle in a storm.
            var outline = CargoBuilder.BuildZoneOutline(
                RollingTwin(hull, "CargoZoneRig", zoneGO.transform),
                Vector3.zero, ZoneSize, 0.35f, outlineMaterial);

            var zone = zoneGO.AddComponent<PlacementZone>();
            var so = new SerializedObject(zone);
            so.FindProperty("center").vector3Value = Vector3.zero;
            so.FindProperty("size").vector3Value = ZoneSize;
            // Fine enough to only take the wobble out of a hand-held pose, coarse enough that
            // two crates side by side end up flush.
            so.FindProperty("cellSize").floatValue = 0.2f;
            so.FindProperty("outline").objectReferenceValue = outline;
            so.ApplyModifiedPropertiesWithoutUndo();
            return zone;
        }

        /// <summary>
        /// The picking crane, split across the level root and the rolling hull:
        ///
        ///   Crane        (LEVEL root)  - CraneController
        ///     Rope                     - placed in world space each frame
        ///     HookRoot                 - placed in world space each frame; the cargo anchor
        ///     Controls                 - what the operator aims at, on the wheelhouse roof
        ///   Hull/CraneRig (ROLLING)    - yawed 180 so slew zero points aft
        ///     Pedestal                 - yaws with the slew
        ///       Boom                   - pitches with the luff, local +Z along the boom
        ///         BoomTip              - where the rope hangs from
        ///
        /// The BOOM leans with the boat, because a crane bolted to a rolling deck leans with
        /// it and a crane that did not looked broken. The ROPE does not: gravity is not a
        /// property of the hull, so <see cref="CraneController"/> places the rope and hook in
        /// world space, hanging plumb from wherever the tip has ended up. That is also why the
        /// hook is not a child of the boom.
        ///
        /// The CONTROLS stay level, and stay parented under the controller: the interaction
        /// raycast resolves an interactable with GetComponentInParent, which is what lets the
        /// operator work the crane from a console five metres away from it - and a console that
        /// rolled would swing out from under the crosshair every time the boat took a wave.
        /// </summary>
        private static void BuildCrane(Transform boat, Transform hull, Material trim, Material paint)
        {
            var craneGO = new GameObject("Crane");
            craneGO.transform.SetParent(boat, false);
            craneGO.transform.localPosition = CranePosition;
            // Slew zero points the boom aft, over the working deck, which is where it spends
            // its life. The operator's neutral stick is then their neutral view.
            craneGO.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            var rig = RollingTwin(hull, "CraneRig", craneGO.transform);

            TestMaterials.Box("Base", rig, new Vector3(0f, 0.15f, 0f),
                new Vector3(1.1f, 0.3f, 1.1f), trim);

            var pedestal = TestMaterials.Node("Pedestal", rig,
                new Vector3(0f, PEDESTAL_HEIGHT, 0f));
            TestMaterials.Box("Column", pedestal, new Vector3(0f, -PEDESTAL_HEIGHT * 0.5f, 0f),
                new Vector3(0.7f, PEDESTAL_HEIGHT, 0.7f), paint);

            var boom = TestMaterials.Node("Boom", pedestal, Vector3.zero);
            TestMaterials.Box("BoomArm", boom, new Vector3(0f, 0f, BOOM_LENGTH * 0.5f),
                new Vector3(0.32f, 0.32f, BOOM_LENGTH), paint);
            // Counterweight behind the pivot, so the boom does not read as a plank glued to a
            // post.
            TestMaterials.Box("BoomHeel", boom, new Vector3(0f, 0f, -0.9f),
                new Vector3(0.55f, 0.55f, 1.4f), trim);
            var boomTip = TestMaterials.Node("BoomTip", boom, new Vector3(0f, 0f, BOOM_LENGTH));

            var rope = TestMaterials.Box("Rope", craneGO.transform, Vector3.zero,
                Vector3.one, trim).transform;

            var hookRoot = TestMaterials.Node("HookRoot", craneGO.transform, Vector3.zero);
            // Drawn just ABOVE the attach point, so a load whose top face lands on the anchor
            // meets the underside of the block instead of floating below it.
            TestMaterials.Box("Block", hookRoot, new Vector3(0f, 0.16f, 0f),
                new Vector3(0.26f, 0.32f, 0.26f), trim);

            // Visuals only, all of them, on BOTH nodes. The hull's blanket collider strip has
            // already run by the time this is called, so CraneRig has to shed its own. A
            // collider on the boom would drag the player around as it slewed, and a collider
            // anywhere under the level node would make the whole crane answer the interaction
            // raycast - which is the Controls object's job alone.
            foreach (var stray in rig.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(stray);
            foreach (var stray in craneGO.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(stray);

            var hook = hookRoot.gameObject.AddComponent<CraneHook>();
            var hso = new SerializedObject(hook);
            hso.FindProperty("reach").floatValue = 1.2f;
            hso.ApplyModifiedPropertiesWithoutUndo();

            // Controls on the wheelhouse roof: from up here the operator can see the whole
            // working deck and both rails, which is the only position on this boat where the
            // job is actually doable.
            var controls = new GameObject("Controls");
            controls.transform.SetParent(craneGO.transform, false);
            controls.transform.position = boat.TransformPoint(
                new Vector3(0.9f, ROOF_TOP + 0.5f, HOUSE_AFT_Z + 1.0f));
            controls.transform.localRotation = Quaternion.identity;

            var aim = controls.AddComponent<BoxCollider>();
            aim.size = new Vector3(0.8f, 1.0f, 0.6f);
            aim.isTrigger = true;   // aimable, but must not shove the operator standing at it
            TestMaterials.Box("Console", controls.transform, new Vector3(0f, -0.1f, 0f),
                new Vector3(0.6f, 0.8f, 0.4f), trim);
            var lever = TestMaterials.Box("Lever", controls.transform, new Vector3(0f, 0.38f, 0.05f),
                new Vector3(0.07f, 0.4f, 0.07f), paint);
            lever.transform.localRotation = Quaternion.Euler(-16f, 0f, 0f);
            foreach (var stray in controls.GetComponentsInChildren<Collider>())
                if (!stray.isTrigger) Object.DestroyImmediate(stray);

            var crane = craneGO.AddComponent<CraneController>();
            var so = new SerializedObject(crane);
            so.FindProperty("pedestal").objectReferenceValue = pedestal;
            so.FindProperty("boom").objectReferenceValue = boom;
            so.FindProperty("boomTip").objectReferenceValue = boomTip;
            so.FindProperty("rope").objectReferenceValue = rope;
            so.FindProperty("hookRoot").objectReferenceValue = hookRoot;
            so.FindProperty("hook").objectReferenceValue = hook;
            // Stops short of swinging the boom through the wheelhouse at low elevation.
            so.FindProperty("slewRange").floatValue = 100f;
            so.FindProperty("luffMin").floatValue = 8f;
            // Steep enough that the hook can come all the way in to the pedestal's own corner
            // of the lashing area - at 78 degrees the nearest metre and a half of it was
            // unreachable, which is exactly where a busy deck stacks things.
            so.FindProperty("luffMax").floatValue = 82f;
            so.FindProperty("hoistMin").floatValue = 0.5f;
            // Long enough to put the hook well under the surface alongside, which is what
            // fishing gear out of the water needs.
            so.FindProperty("hoistMax").floatValue = 12f;
            so.FindProperty("slewRate").floatValue = 22f;
            so.FindProperty("luffRate").floatValue = 14f;
            // A heavy block on a stiff wire: it lags the boom rather than chasing it, and the
            // swing dies out rather than ringing. swingResponse is the weight knob - lower is
            // heavier.
            so.FindProperty("swingResponse").floatValue = 0.3f;
            so.FindProperty("swingDamping").floatValue = 1.5f;
            so.FindProperty("maxSwingDegrees").floatValue = 20f;
            // Half a metre a click made setting a load down gently impossible - you overshot
            // past the deck and back up again. Small enough now to feather it in.
            so.FindProperty("hoistStep").floatValue = 0.14f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---------------- motion ----------------

        private static void BuildMotion(GameObject boat, Transform hull, BoatHelm helm)
        {
            var motion = boat.AddComponent<BoatMotion>();
            var so = new SerializedObject(motion);
            so.FindProperty("helm").objectReferenceValue = helm;
            so.FindProperty("hullVisual").objectReferenceValue = hull;
            so.FindProperty("moored").boolValue = true;
            so.FindProperty("hullLength").floatValue = (BOW_Z - STERN_Z) * 0.5f;
            so.FindProperty("hullBeam").floatValue = HALF_BEAM;
            so.FindProperty("freeboard").floatValue = FREEBOARD;
            // Stiffer than the tug in every axis. This is a heavier hull over a longer
            // waterline, so it cuts through chop the tug would ride, and the working deck has
            // to stay workable - cargo placement is judged by eye from twenty metres away.
            so.FindProperty("waveFollow").floatValue = 0.55f;
            so.FindProperty("maxTiltDegrees").floatValue = 18f;
            so.FindProperty("heaveFollow").floatValue = 0.6f;
            so.FindProperty("heaveSmoothing").floatValue = 1.8f;
            so.FindProperty("slopeBaseline").floatValue = 1.8f;
            so.FindProperty("smoothing").floatValue = 0.8f;
            so.ApplyModifiedPropertiesWithoutUndo();

            // BoatRiderCarry [RequireComponent]s BoatMotion, which is why it is added AFTER the
            // one above rather than before it. Reversed, the require would create a second
            // BoatMotion with default values and both would write the hull transform every
            // frame - see the note in WaterBuilder, where that is exactly what happened.
            var carry = boat.AddComponent<BoatRiderCarry>();
            var cso = new SerializedObject(carry);
            // Tall enough to include the wheelhouse roof, because the crane operator standing
            // up there is a rider too and would otherwise be left behind the moment the boat
            // got under way. Narrowed to 6.2 (the hull is 6.4) specifically to leave the
            // boarding ladder at x=-3.35 outside it - otherwise a swimmer hanging on the rungs
            // gets towed along by the hull.
            cso.FindProperty("deckCenter").vector3Value = new Vector3(0f, 1.6f, -0.5f);
            cso.FindProperty("deckSize").vector3Value = new Vector3(6.2f, 9f, 19f);
            cso.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Bow wave and prop wash, sharing the decal materials the tug already uses. Both hang
        /// off the LEVEL root, not the rolling hull: a water decal is projected straight down,
        /// so letting it roll with the visual would swing the wake out from under the boat.
        ///
        /// Called after <see cref="BuildMotion"/> because BoatWake requires BoatMotion, and
        /// adding it first would satisfy that require with a defaults-only second copy.
        /// </summary>
        private static void BuildWake(GameObject boat)
        {
            var bowMaterial = WaterBuilder.DecalMaterial("TB_BowWave", WaterBuilder.DECAL_BOW_WAVE,
                deformation: true, foam: false, m => m.SetFloat("_Elevation", 1f));
            var washMaterial = WaterBuilder.DecalMaterial("TB_PropWash", WaterBuilder.DECAL_SPHERE,
                deformation: true, foam: true);
            if (bowMaterial == null || washMaterial == null) return;

            // Scaled up from the tug's: a twenty-metre hull pushes a correspondingly wider
            // shoulder of water, and a wake narrower than the boat reads as the boat sliding
            // over the surface rather than through it.
            var bow = WaterBuilder.AddDecal(boat.transform, "BowWave", bowMaterial,
                new Vector3(0f, 0f, 7.6f), new Vector2(9.5f, 12f), 0.55f);
            var wash = WaterBuilder.AddDecal(boat.transform, "PropWash", washMaterial,
                new Vector3(0f, 0f, -11.5f), new Vector2(3.4f, 9f), 0.26f,
                surfaceFoam: 1f, deepFoam: 0.7f);

            var wake = boat.AddComponent<BoatWake>();
            var so = new SerializedObject(wake);
            so.FindProperty("bowWave").objectReferenceValue = bow;
            so.FindProperty("propWash").objectReferenceValue = wash;
            // Reached at a lower speed than the tug, because this hull's top speed is lower.
            so.FindProperty("fullEffectSpeed").floatValue = 5f;
            so.FindProperty("bowAmplitude").floatValue = 0.55f;
            so.FindProperty("washAmplitude").floatValue = 0.26f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
