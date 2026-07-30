# FableTest — Progress & Handoff Doc

**Read this first.** It exists so any assistant can continue the rebuild without
re-deriving context. Companion: `docs/ARCHITECTURE.md` (rules + system map). Approved plan:
`C:\Users\Evan\.claude\plans\i-was-trying-to-sunny-mist.md`. The OLD project
(`C:\Users\Evan\worldbuild_test`, reference ONLY — never modify) has its own `docs\`;
`COOP_NETWORKING_ONBOARDING.md` there is the canonical co-op spec.

## The game

PSX-style co-op (2–4 players, fully playable solo) mixing Tarkov / STALKER / PSX horror.
Unity 6000.0.47f1, **HDRP 17.0.4**, Netcode for GameObjects 2.11, UGS Auth/Lobby/Relay.
Art direction: **quality PSX** — low internal res (360p default) is the core signal, gentle
color quantization (63/127/63), subtle dither (0.5). The map will have neon/city-light
regions and dark grim red-lit regions — **lighting does the mood, the post filter stays
gentle**. Motion blur off. No heavy retro crunch.

## Phase status (2026-07-27) — Phases 0–12 DONE, 13 in progress

| Phase | Status | Notes |
|---|---|---|
| 0 Editor stability | DONE | Deferred.compute fixed by ShaderCache purge. DX12. Burst broken here — NetworkBootstrap force-disables it. |
| 1 Packages/scaffold | DONE | multiplayer.playmode REMOVED (breaks on editor 47f1; re-add 1.6.x only after editor ≥ 6000.0.48f1). |
| 2 Session core | DONE (solo verified) | **MP join still untested** — see "Next steps". Lobby+Relay enabled in dashboard. |
| 3 Player/interaction | DONE | |
| 4 Menu + settings | DONE (verified) | Title/SaveSlots/GameSetup/LobbyBrowser/Settings+rebinding, pause menu. |
| 5 PSX rendering | DONE (verified) | Custom post process After Post Process, registered via reflection. PSX Lit vertex-snap graph deferred (`_Game/Shaders/PSXVertex.hlsl` has the Custom Function code). |
| 6 Inventory/items/carry | DONE (verified) | Grid + drag/rotate, 3D hover preview + click-spin info, drop-to-world, server pickup, physics carry w/ ownership transfer. |
| 7 Quests | DONE | Shared (server-owned) + personal quests, late-join RequestSync, quest log tab, notice board. Play-verified indirectly via save test. |
| 8 Time + weather | DONE (verified) | Clock + weather are pure functions of ServerTime; 6 presets; camera-following precipitation. |
| 9 Moving platforms | DONE (verified) | Shuttle + elevator as pure `EvaluatePoseAt(serverTime)`; carry w/ snap rejection. |
| 10 Snow deformation | DONE | RT stamp/refill + shader globals wired. **Snow material Shader Graph still to author** (samples `_SnowDeformRT`). |
| 11 Admin console | DONE (verified) | Backquote console; server-executed weather/time/give/tp/heal/quest/players/admin. |
| 12 Save system | DONE (verified) | Host-only slot saves; round-trip restores inventory/position/quests/flags/weather/clock. |
| 13 Polish | IN PROGRESS | Done: health/clock HUD, quest toasts, live map w/ player arrows, tunable weather (fog + volumetric clouds + precipitation) with event triggers, deformable snow ground shader, lighting/exposure overhaul, controller feel pass. Remaining: see below. |

### Snow: current state and the plan for the city

Today: one 100m deformable plane (`SnowGround`), 600 segments (~0.17m verts), 2048
deformation texture (~5cm/texel).

**Snow depth is dynamic, not a fixed slab.** `WeatherManager` integrates a 0..1
`SnowCoverage` — gaining while snow falls, melting in proportion to sun elevation and
clear sky — and publishes `_SnowHeightMeters` (= coverage × `maxSnowDepth`, default 1m ≈
waist deep). The ground mesh sits at ground level and the shader *lifts* it by that
height, so snow visibly builds up during a storm and sinks away in sun. Trails can never
carve deeper than the snow actually lying (`min(_DepthMeters, snowHeight)`). Coverage is a
server-owned NetworkVariable, so late joiners inherit the exact depth. Verified: grew to
0.60m while snowing, melted to 0.38m under clear midday sun.

Tuning: `maxSnowDepth` / `snowGainPerSecond` / `snowMeltPerSecond` on WeatherManager;
`_DepthMeters` and `_SmoothRadiusTexels` on the SnowGround material; `radius`/`depth` on
the player's `SnowDeformer`. Smoothness comes from `_SmoothRadiusTexels` (9-tap tent
filter on the height field, matched in the normal reconstruction) plus the smoothstep
stamp falloff — raise it if prints look faceted, lower it for sharper edges.

**When the city exists, a single flat plane stops working.** The intended split:

1. **Walkable snow (streets, plazas, open ground)** keeps the current system: subdivided
   meshes using `Game/SnowGround`, all sampling the same `_SnowDeformRT`. They can be
   separate meshes at different heights — the shader maps world XZ to the texture, so any
   number of surfaces share one deformation field. Only the *walkable* areas need the
   dense subdivision.
2. **Static accumulation on everything else (rooftops, ledges, cars, props)** should NOT
   be geometry. Use a snow-coverage term in the building materials: blend toward snow
   albedo/normal based on world-space `normal.y` (upward faces get snow, walls stay
   clean), scaled by a global snow-amount float driven by
   `WeatherManager.SnowAccumulation`. This is one shader feature applied broadly, costs no
   extra geometry, and makes snow appear/melt with the weather.
3. **Region size**: the deformation texture covers a fixed world box. For a city, either
   make it a sliding window that follows the local player (copy-offset blit when it moves)
   or use per-district managers. 2048 over 100m ≈ 5cm/texel — keep that ratio.

Deformation is per-machine presentation driven by replicated transforms, so it already
covers all players (local and remote) with no extra networking.

### Weather tuning (what Evan asked for)

Each `WeatherPreset` asset in `_Game/Resources/Weather` exposes fog (mean free path,
volumetric on/off, depth extent, albedo, anisotropy, height), clouds (density, shape,
erosion, altitude, thickness, sun dimmer, wind speed), precipitation rates, wind, and
snow accumulation. `WeatherManager` numerically LERPs every value between the outgoing
and incoming preset, so transitions are smooth and everything stays inspector-tunable.
Trigger weather from gameplay with `WeatherEventTrigger` (listens to a `GameEventBus`
event id, server applies the change) or from the console: `weather <type> [seconds]
[intensity]`.

## Remaining work (Phase 13 + follow-ups)

1. **MP smoke test** (highest value, untested): standalone build exists at
   `Builds/FableTest.exe`. Host in the editor (GameSetup → visibility Public/Friends),
   read the lobby code from the lobby, join from the build. Then the regression gates:
   join mid-shuttle-travel, mid-weather-transition, mid-quest — all should match the host.
2. **Snow material Shader Graph**: HDRP Lit + tessellation, sample `_SnowDeformRT` with
   `_SnowRegionParams` (xy = region origin XZ, zw = 1/size) to displace down + rebuild
   normals. Everything else for snow is done.
3. **PSX Lit Shader Graph**: use `_Game/Shaders/PSXVertex.hlsl` Custom Functions
   (PSXSnap_float vertex snap, PSXAffinePack/Unpack for affine UVs).
4. Equipment slots / backpack expansion; audio mixer buses (SettingsService already stores
   music/sfx volumes but nothing consumes them yet); map tab is still a placeholder.
5. Medical system (deliberately deferred; `PlayerStats` is the hook).

## How to work on this project

- **Unity MCP** (mcp__unityMCP__*): `refresh_unity` (compile), `read_console`,
  `execute_menu_item`, `execute_code` (C# in editor), `manage_editor` (play/stop),
  `manage_build`. MCP composited screenshots are broken — use
  `ScreenCapture.CaptureScreenshot` via execute_code then Read the PNG. First frames after
  entering play render CYAN (async shader compilation) — not a bug. After `manage_editor
  play`, the first `execute_code` often lands while Boot is still loading; just retry once.
- **Verification pattern**: enter play from Boot, drive UI via `execute_code`
  (`button.onClick.Invoke()`), inspect state. Keep it minimal (token budget).
- Scenes: `Assets/_Game/Scenes/{Boot,MainMenu,World}.unity`. Boot is the entry scene.

### Editor generators (menu Game/Setup/...) — ORDER MATTERS

`Full Project Setup` → `Build Menus` → `Build Items` → `Build Inventory UI` →
`Build Quests` → `Build Console` → `Build HUD` → `Build Map And Toasts` →
`Build Weather` → `Build Platforms` → `Build Snow` → `Build Snow Ground` →
`Build Save` → `Register PSX Post Process`.

**Why order matters:** `Build Menus` REGENERATES the WorldUI/MainMenuUI prefabs from
scratch, wiping the subtrees added by `Build Inventory UI` (TabMenu), `Build Quests`
(quest log), `Build Console`, `Build Chat`, `Build HUD` (health/clock) and
`Build Map And Toasts` (map + toast stack). Those SIX patch the existing prefab, so re-run
them in order after any `Build Menus`. All generators are individually idempotent.

**This has already bitten once (2026-07-29).** The main-menu work ran `Build Menus` and the
patchers were not re-run, so Tab, M, Enter and backquote all silently did nothing in the
World scene for a whole session — the actions and bindings were fine, the UI subtrees simply
were not there. **The symptom is dead hotkeys, not an error**, and nothing logs. If a UI
hotkey stops responding, check `WorldUI`'s children before touching input: a healthy one has
HUD, TabMenuRoot, PausePanel, SettingsPanel, ConsoleRoot and ChatRoot.

Note: `Build Map And Toasts` also links the scene's WorldUI instance to the scene's
MapCamera — prefabs cannot store scene references, so that link lives on the instance.

## Critical gotchas (cost hours — do not rediscover)

1. Burst JIT broken on this machine → keep `NetworkBootstrap.DisableBurst` fallback.
2. `DISABLE_DEDICATED_SERVER_EXPERIMENTAL` is defined (Standalone). Do NOT re-add
   `com.unity.multiplayer.playmode` on this editor version.
3. HDRP custom post processes MUST be registered in Global Settings order lists
   (`PSXSetup` does it via reflection — the settings class is internal).
4. HDRP photometric units: directional intensity is LUX (40000 = day); the AddComponent
   default of 1 is pitch black. Point lights use lumens.
5. `string[]` is not RPC-serializable → `FixedString64Bytes[]` / `int[]` (flattened with a
   strides array — see NetworkQuestSync/SaveService).
6. Scene-object `ServerRpc(RequireOwnership=false)` works fine (quest/admin/item managers).
7. Domain reload is expected to be disabled → every static event/field needs
   `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` reset (all current code has it).
8. **Scene NetworkObject spawn order is arbitrary.** Systems that write another system's
   NetworkVariables on spawn must defer a frame (SaveService.RestoreWorldDeferred) or the
   other system's own `OnNetworkSpawn` defaults will clobber the restore.
9. UGS: project is cloud-linked; anonymous auth + Lobby + Relay are ENABLED.
10. Solo = host over loopback port 0. There is no separate singleplayer code path. Ever.
11. Save identity = auth playerId from connection approval (`ServerPlayerRegistry`).

### Rendering / lighting gotchas (all cost real debugging time)

12. **A scene with no `StaticLightingSky` has black ambient.** Shadowed faces render
    almost pure black and the whole game "looks dark". It lives on the Sky and Fog Volume
    object with `profile` = the sky profile and `staticLightingSkyUniqueID` =
    `(int)SkyType.PhysicallyBased`. `ProjectSetup` does not add it — the World scene has
    it now; add it to any NEW scene.
13. **Exposure is Fixed, not Automatic, and is driven by `WeatherManager`** from
    `SunController.DayBlend01(hour)` (day ≈ 11.8 EV, night ≈ 8.0, brightened under heavy
    cloud). Auto-exposure was tried and rejected: a large bright surface (the snow ground)
    drags the histogram and pushes the whole image dark. Tune `dayExposure`/
    `nightExposure` on the WeatherManager component.
14. **Custom HDRP shaders must output real luminance and multiply by
    `GetCurrentExposureMultiplier()`** (`SnowGround.shader` does: `albedo * NdL *
    illuminance / PI`). Skipping exposure = blown-out white; scaling by an arbitrary
    constant = black. `SunController` publishes `_GameSunDirection`, `_GameSunLux`,
    `_GameSunAmount` for this.
15. **Volumetric clouds are OFF by default** in every HDRP quality asset
    (`supportVolumetricClouds`) — now enabled in all three under `Assets/Settings/`.
16. **Particle box emitters remap axes when the shape is rotated.** Rotating a Box shape
    90° about X (to emit downward) maps local Y→world Z and local Z→world -Y, so the
    *thin* axis must be Z or you get a vertical curtain of rain instead of a ceiling.
    Also: never offset the precipitation rig by `camera.forward` — it makes rain visibly
    swing/tilt as the player looks around. Follow the player's position only.
16b. **HDRP renders camera-relative.** `TransformObjectToWorld` in a custom shader returns
    a position *relative to the camera*, so anything that maps world position to a texture
    (snow deformation, triplanar, world-space masks) must wrap it in
    `GetAbsolutePositionWS()`. Without it the pattern slides around with the player and
    looks "stuck to the camera".
16d. **Normal reconstruction must step ONE TEXEL.** The snow shader stepped by
    `1/regionSize` (= 1 metre) instead of `1/textureResolution`, so every pixel shaded from
    four samples metres away — visible in game as "four circles in a square around the
    player" and a surface that looked painted-flat rather than displaced. The manager now
    publishes `_SnowDeformTexel`, and the shader converts the gradient into a true
    world-space slope (`delta * _DepthMeters / worldStep`).
16e. **The snow layer needs thickness.** Snow sits `SNOW_THICKNESS` (0.28m) above the base
    ground and `_DepthMeters` (0.24m) must stay below that, or deep prints punch through
    and reveal the ground plane. Detail is limited by vertex spacing: 450 segments over
    100m ≈ 0.22m, with a 2048 deformation texture ≈ 5cm per texel.
16c. **Never use a float render target with subtractive blending.** The snow refill pass
    (`BlendOp RevSub`) drove depth *negative* on an RFloat RT with no clamping, which
    displaced the snow mesh UPWARD and swallowed the camera in white geometry. Fixed-point
    targets (R8/ARGB32) clamp to [0,1] in hardware; the shader also `saturate`s as backup.
17. **Components on NGO-spawned players can Awake before scene singletons exist.**
    `SnowDeformer` registered once against a null manager and silently never stamped;
    it now retries in `Update` until registered. Watch for this pattern with any
    scene-manager + spawned-object pairing.
18. **The FPS controller tracks yaw explicitly** and writes
    `transform.rotation = Euler(0, yaw, 0)`. Using `transform.Rotate` compounds onto any
    pitch/roll already on the body (from spawn placement or teleports) and shows up as
    camera roll/tilt while moving. Call `SyncRotationFromTransform()` after any external
    reposition.
19. **HDRP re-derives light intensity when the emitting shape changes, so ordering
    matters.** `hd.SetIntensity(lm, LightUnit.Lumen)` followed by `hd.SetSpotAngle(a)`
    applies the lumen→candela conversion a SECOND time; point lights authored at 9000 lm
    came out at 57 lm (a factor of 16π²). Set `range` and `SetSpotAngle` FIRST, then
    `hd.lightUnit = LightUnit.Lumen`, then `hd.intensity = lumens`. That order round-trips
    exactly. See `TestBuildingsBuilder.MakeLight`.
20. **`LightUnit` lives in `UnityEngine.Rendering`, not `.HighDefinition`.**
21. **`ScreenSpaceReflection.minSmoothness` / `.smoothnessFadeStart` are plain floats
    backed by the quality preset, not `VolumeParameter`s.** Setting the value alone does
    nothing — the getter ignores it unless the quality level is flagged as an override:
    `ssr.quality.levelAndOverride = ((int)Level.High, true)`. Use
    `profile.Add<T>(overrides: true)` so every parameter's `overrideState` is on.
22. **Metallic ≈ 1 leaves almost no diffuse response and reads as pure black** under
    practical (non-IBL) lighting. The warehouse walls at 0.75 metallic were invisible at
    night; 0.25 fixed it. Reserve high metallic for surfaces that have a reflection probe
    worth reflecting.
23. **Emissive surfaces are not lights.** A 2600-nit neon sign is ~1000× brighter than
    what a 9000 lm bulb puts on a 0.3-albedo wall, so signage blows out while the building
    stays black. Pair every emissive prop with a real light carrying its colour, and give
    it `affectsVolumetric = true` + a `volumetricDimmer` above 1 or the glow stops at the
    surfaces it hits instead of hazing through fog.

24. **`m_AngularDiameter` does NOT size a rendered celestial body.** HDRP has a second,
    separate pair — `diameterMultiplerMode` / `diameterOverride` — and `diameterOverride`
    **defaults to 0.5**, so any sun/moon left at the default draws at half a degree no
    matter what angular diameter it claims. `m_AngularDiameter` is the physical value used
    for shadow softness. This cost an hour of "why is the moon still a dot".

25. **A celestial body's disc brightness is derived from its own light intensity.** In
    emission mode, disc radiance = intensity / solid angle. Moonlight needs ~450 lux for
    the ground to read at night, which across a 9° disc is ~200× the night exposure
    ceiling — the disc clips to a white blob and `surfaceTint` will not pull it back.
    Fix: **two lights.** The root light lights the world and draws nothing
    (`interactsWithSky = false`); a dim child light draws the disc. Solid angle grows with
    the SQUARE of the diameter, so derive the disc intensity from a target radiance or
    resizing the body silently darkens it.

26. **The sun cannot get that two-light treatment.** `PhysicallyBasedSky` sources ALL
    atmospheric scattering from the light marked `interactsWithSky`. Move the sun's disc to
    a dim child and the daytime sky goes nearly black. Tame the sun's *flare* instead —
    Unity defaults it to 2° at full multiplier, which is what eats the screen, not the
    0.5° disc.

27. **The atmosphere reddens space emission.** Rayleigh scattering removes several times
    more blue than red, so a star cubemap that measures neutral (verified: mean RGB
    0.345/0.342/0.344) renders amber. Pre-multiply the texture by the inverse of zenith
    transmittance (~0.82/0.87/1.0) rather than chasing it in the sky settings.

28. **Never fix an input binding with `ChangeBinding(i).To(new InputBinding(...))`.** A
    fresh `InputBinding` carries no action name, so it orphans the binding; the map then
    throws `ArgumentNullException: actionNameOrId` from `FindAction(null)` while rebuilding
    its lookup arrays, and the `.inputactions` asset is corrupt. Remove the action and
    re-add it. Also **check for double-bound keys** — `ChatChannel` first went onto Tab,
    which `TabMenu` already owned.

29. **Builders run mid-recompile silently do the wrong thing.** Editing an editor script
    then immediately invoking its menu item runs the OLD compiled code and reports success.
    Confirm `EditorApplication.isCompiling == false` first. Related: a builder that opens
    the World scene additively will **save whatever pose the scene is currently in** — a
    temporary test pose got baked into `World.unity` (Sun intensity 0) this way.

30. **A finite water surface does no underwater rendering without `volumeBounds`.**
    `volumeDepth` / `volumeHeight` are consulted *only* for infinite oceans, so setting them
    on a `Quad` surface looks like a fix and does nothing. Give it a `BoxCollider`. Two
    traps follow: parent that collider anywhere under the water GameObject and it inherits
    the quad's scale (a 1200× scale turned a 1.2 km box into a **720 km** one covering the
    whole world below the waterline), and if the box reaches back under the city, every
    basement and tunnel below y=0 renders as submerged. Parent it to the unscaled root and
    clip its near edge to the shoreline.

31. **`WaterSurface.simulationTime` reading 0 means there is no simulation, not a stuck
    clock.** The getter is `simulation?.simulationTime ?? 0f`, and HDRP only allocates
    `simulation` for a surface some camera actually renders. Open World standalone with no
    player spawned and there is no game camera, so every `ProjectPointOnWaterSurface` call
    returns false and the water looks broken. This is a **harness artifact** — water
    physics can only be verified from a real session.

32. **The water mask is the tool for varying roughness by place, and it is honest.** It
    attenuates each simulation band per texel (R = swell, G = agitation, B = ripples), and
    `waterScriptInteractionsMode = GPUReadback` reads the same mask back for the CPU height
    search — so buoyancy, hull sampling and what you can see all agree. Write it as a real
    imported, uncompressed, **non-sRGB**, Clamp-wrapped PNG; Clamp matters, or the calm bay
    wraps back in every mask extent. Note `waterMaskExtent` is the FULL width, and UV.y maps
    to world Z.

33. **`CreatePrimitive` hands out a collider with every box.** The tugboat's "visual only"
    hull was quietly carrying eight of them, and the deck plate's sat 1 cm *above* the
    level deck collider — so the player was standing on the tilting one and the entire
    reason the deck and hull are separate objects was defeated. Strip colliders from
    anything built as decoration.

34. **`GetComponentInParent<IInteractable>()` makes a root-level interactable answer for
    every collider beneath it.** `BoatHelm` on the boat root offered "Take the helm" from
    anywhere on the hull. Put interactables on their own child with their own collider.

35. **A queued carry delta always lands one frame late.** Platforms move *after* the player
    has already moved (player in Update, platform in Update/LateUpdate), so
    `AddExternalMove`-style deferral put every rider a frame behind. An elevator at 2 m/s
    hides it; a boat at 9 m/s reads as the deck stuttering underfoot, and on a heaving deck
    it lets the hull sweep through the capsule before the capsule is told to move. Apply the
    delta immediately (`ApplyCarry`), with a hair of downward bias when grounded — a purely
    horizontal `Move` can leave the controller reporting airborne on a deck it is plainly
    standing on, which costs the rider their jump.

36. **Moving colliders with no Rigidbody are STATIC colliders that teleport.** PhysX rebuilds
    the static broadphase every frame and CharacterControllers get nothing solid to resolve
    against — the "player morphs through the boat" symptom. Add a kinematic Rigidbody with
    interpolation off (the transform is written outright each frame). Note the elevator and
    metro still lack this; they are slow enough not to show it yet.

37. **A hard clamp destroys the information in a tilt.** At full gain into a 16° clamp,
    ordinary chop already pinned the limit, so a storm looked identical to a breeze.
    Lower the gain and approach a *higher* ceiling asymptotically (`max * tanh(x / max)`):
    gentle water stays gentle and a big sea still has somewhere to go.

38. **The CPU water height query returns a staircase, not a curve.** It updates on a GPU
    readback rather than per frame, so anything that follows it directly (a boat's heave)
    inherits visible judder at speed. Damp heave harder than tilt — heave is what riders
    stand on, roll is free because it only ever touches the visual hull.

39. **Builders must refuse to run in PLAY MODE, not just during compiles.**
    `EditorSceneManager.OpenScene` throws in play mode, so the builder does nothing at all
    while the scene on screen looks untouched and entirely plausible — worse than gotcha 29,
    where at least stale code ran. `MenuSceneBuilder.Ready()` is the shared guard; every
    builder should call it.

40. **A night sky is a property of the light marked `interactsWithSky`, not of intensity.**
    `PhysicallyBasedSky` takes all its scattering from that one light; aim it from above the
    horizon and you get a blue daytime sky no matter how dim it is. Aim the sky light from
    *below* the horizon (the sun has set) and add a second directional light with
    `interactsWithSky = false` for the moonlight. Same two-light split as the world's moon
    (gotcha 26), for the opposite reason.

41. **Play mode MUST start from Boot.** `NetworkSessionManager` is authored in Boot and
    survives on DontDestroyOnLoad, so pressing Play while MainMenu or World is the open
    scene skips it — and every start/join button then died on a bare
    `NullReferenceException` naming only the UI line. `NetworkSessionManager.Require()`
    now reports the real cause; never dereference `Instance` blind. Corollary for tooling:
    anything that opens a scene must put Boot back when it is done.

42. **Order the guard before the side effects.** `GameSetupScreen` created the save slot on
    disk and *then* hit the null session, so a failed start still left a slot behind.
    Validate first, write second.

43. **Water decals only render inside a finite region, and it defaults to the WORLD ORIGIN.**
    HDRP centres the decal region on `Camera.main` and falls back to (0,0) when nothing
    carries the MainCamera tag — silently, and it looks exactly like broken decals. With a
    200 m region at the origin, the only crate that ever foamed was the one at z=96; the
    boat at z=106+ never did. `WaterVolume` now re-anchors `decalRegionAnchor` to the local
    player, which also beats relying on the tag: every peer has a player camera and
    `Camera.main` returns whichever tagged one it finds first.

44. **Foam is a separate shader pass from deformation.** It multiplies by the foam dimmers,
    NOT by amplitude — so `amplitude = 0` on a foam-only decal is correct, not a no-op. The
    `_TYPE` numbering is shared between both paths (0 sphere/disk, 1 box/rectangle,
    4 texture), so a foam rectangle is `_TYPE 1` with `_AffectDeformation` off. And the
    trail is not authored: the foam buffer decays instead of being redrawn, so a moving
    foam source leaves a fading line — `foamPersistenceMultiplier` IS the wake length.

45. **A NetworkTransform replicates WORLD position, which is wrong for anything on a moving
    platform.** A boat at 9 m/s covers most of a metre between ticks, so every remote rider
    arrives a tick or two stale and visibly slides around the deck no matter how well they
    are interpolated. The fix is NGO parenting (`NetworkObject.TrySetParent`, **server
    only**) plus `NetworkTransform.SwitchTransformSpaceWhenParented = true`, which flips the
    replicated values into the parent's space on the tick the parent changes AND converts
    the in-flight interpolation with it. Setting `InLocalSpace` by hand does neither and
    snaps. It is mutually exclusive with `UseUnreliableDeltas` — NGO silently reverts one of
    them at runtime, and which one depends on the order they were changed in.

46. **A CharacterController parented under a Rigidbody stays an independent actor.** Verified
    directly: a child `BoxCollider` reports `attachedRigidbody = <parent>`, a child
    `CharacterController` reports `null`. So parenting riders to the boat does not fold them
    into its compound collider and they keep colliding with the deck normally. This was the
    one structural risk in the parenting approach.

47. **Once a rider is parented, the local carry MUST stop.** The hierarchy moves them; adding
    `ApplyCarry` on top double-counts every metre the boat travels. Yaw is the opposite
    case and must KEEP being applied, parented or not, because `FirstPersonController`
    rewrites its world rotation from its own tracked `_yaw` every frame — the deck turns a
    parented player and the controller immediately turns them back.

48. **HDRP water exclusion only removes the SURFACE, not the underwater effect.** For a
    finite surface, "the camera is submerged" is a plain
    `volumeBounds.bounds.Contains(cameraPosition)` that ignores excluders completely. A dry
    compartment below the waterline therefore renders with the sea correctly carved out of
    it and the screen still flooded with underwater fog and caustics. Swimming has the same
    gap — `SwimmerProbe` compares surface height to your chest and does not care that there
    is a deck in between. Both go through `DryHullVolume` now.

49. **The exclusion mesh must not poke out through the hull.** Exclusion is a stencil tag
    drawn with `LessEqual` depth against the opaque buffer, so any part of the box that
    reaches outside the hull starts rejecting the open sea alongside the boat as well. And
    `WaterExcluder` keeps both of its fields internal — they can only be written through a
    `SerializedObject`. Neither is read at runtime (the pass just collects renderers using
    the exclusion material) but leaving them empty makes the component's own inspector claim
    it has nothing to exclude.

52. **`Hidden/HDRP/WaterExclusion` is `Cull Back`, so one box only excludes from OUTSIDE it.**
    This is the big one for interiors. A box around a compartment works perfectly looking
    down into it from the deck, and does nothing at all once you are standing in the room —
    every face is back-facing, no stencil is written, and the sea renders straight through
    at eye level while fog and swimming (which go through `DryHullVolume`) correctly stay
    off. Fix is a SECOND renderer using an inside-out cube; whichever way the camera is,
    exactly one of the two survives culling, so they never conflict. Negative scale is not a
    substitute — Unity flips front-face winding for negatively scaled renderers precisely so
    geometry keeps facing the same way, cancelling the trick out.

53. **Do not make the exclusion box exactly the interior dimensions.** Coplanar with the
    bulkheads, `ZTest LEqual` comes down to float rounding and the water tears in and out
    along the walls. Inset a couple of centimetres. Shrinking costs nothing: seen from
    inside, a smaller concentric box subtends a *larger* solid angle, so it still covers
    everything.

54. **The two exclusion boxes want OPPOSITE sizes, and the dry volume wants the opposite
    again.** A stencil tag only lands where its fragment is nearer than the opaque surface
    behind it, and the two meshes present different faces:
    - **Outward** (seen from the deck) presents its TOP face, already in front of everything
      in the room, so it should hug the opening and cover all of it.
    - **Inward** (seen from inside) presents its FAR faces, so *anything protruding into the
      room sits in front of them and defeats them*. Water kept showing at the hold ladder
      for exactly this reason — the box's aft face was 6 cm behind the rungs, so every pixel
      of ladder went untagged. The inward box must fit inside the room's AIR, clear of the
      fittings, and must reach high enough that a jumping player cannot rise out through the
      top (that was one flicker of open water per jump, at the apex).
    - **`DryHullVolume`** should run OUT through the plating instead. Nobody can stand inside
      a solid bulkhead, so the extra space cannot make anyone wrongly dry; it is free margin
      against the fog and swim state flickering at a wall or at the top of a jump.

    Rule of thumb for the boats to come: exclusion mesh inside the air, dry volume through
    the steel.

50. **A file written externally can land in Unity's asset database but NOT in the compile
    set.** `AssetDatabase.FindAssets` found it, the importer was `MonoImporter`, and
    `GetAssemblyNameFromScriptPath` returned the right assembly — while
    `Library/Bee/artifacts/*/Game.Runtime.rsp` (the actual compiler input list) omitted it,
    so every reference failed with CS0103 through repeated forced refreshes. Reading that
    .rsp is the fastest way to tell "Unity has not seen my file" from "my file has an
    error". The fix is `AssetDatabase.DeleteAsset` then rewrite and re-import — deleting the
    file from the shell does not clear the stale record.

51. **Cutting an opening in a deck means cutting every solid box under it, not just the
    plate.** The hold's hatch was carved out of the deck collider AND the deck plating and
    still read as "blocked by red", because `HullBody` was one solid box spanning the whole
    hull — you were looking down the hole at its top face in `TB_Hull` dark red. It is now
    four slabs carved around the hold's outer shell (not its interior, so the hold's own
    walls fill that 30 cm rather than z-fighting with hull sitting in the same space).

55. **`[RequireComponent]` means `AddComponent` ORDER decides whether you get one component
    or two, and it fails silently.** `BoatWake` and `BoatRiderCarry` both
    `[RequireComponent(typeof(BoatMotion))]`. `WaterBuilder` added `BoatWake` first, which
    auto-created a defaults-only `BoatMotion`, and then called `AddComponent<BoatMotion>()`
    itself — so **every tug built before 2026-07-29 shipped with two of them**. That is not a
    cosmetic duplicate: both write `transform.SetPositionAndRotation` in `LateUpdate`, and the
    stray one has `helm = null` and `moored = false`, so it drove the hull around
    `BoatMotion`'s *default* patrol circle (centre 0,0,150, radius 38) while the configured one
    held the mooring. Which one you actually saw came down to component order on the object.
    Nothing logged, and it survived a `Full Project Setup`.

    Builders now GET-or-add anything another component might have required, and `CrabBoatBuilder`
    adds `BoatMotion` before `BoatRiderCarry` deliberately. **When a builder adds several
    components to one object, check the component count on the result, not just the values** —
    a `SerializedObject` read of "the" component returns whichever comes first, which is how
    this hid: `moored` read back as `false` from the stray copy while the real one said `true`.

56. **Cargo attachment is deliberately OUTSIDE NGO parenting, and needs
    `AutoObjectParentSync = false` to stay that way.** `CargoAttachment` replicates only a
    claim — "I am on anchor N of network object H, at this local pose" — and every peer applies
    it by parenting locally. Nothing per-frame goes on the wire. That is not an optimisation,
    it is a correctness requirement: `BoatMotion` replicates *nothing* (the hull is a function
    of server time), so a crate synced by absolute world position would be fighting a boat that
    each peer computes for itself, and would visibly lag the deck it is bolted to. With
    `AutoObjectParentSync` left at its default `true`, NGO tries to replicate and then undo that
    parenting and warns once per crate.

    While attached, `NetworkTransform`, `NetworkRigidbody` and `Buoyancy` are all switched off
    and the body goes kinematic — the object is not networked physics in any sense until it is
    released.

57. **An anchor's network identity is its hierarchy INDEX, not an authored id.**
    `CargoAnchor.Index` is its position in `host.GetComponentsInChildren<CargoAnchor>(true)`,
    recomputed on demand rather than cached in `Awake` — cargo routinely resolves an anchor
    before that anchor's `Awake` has run, because a late joiner receives the crate and the boat
    it is lashed to in whatever order the spawn messages arrive. A hand-assigned id would drift
    out of sync the first time a builder regenerated the vessel; an index cannot, because every
    peer walks the same scene file. **The cost: inserting an anchor into the middle of a
    hierarchy renumbers everything after it**, so anything holding a saved index (saves, later)
    must be migrated when a vessel's anchor list changes.

59. **`QueryTriggerInteraction.Collide` on a single `Physics.Raycast` lets one irrelevant
    trigger swallow every interaction behind it.** `InteractionSystem` has to include triggers,
    because most interactables ARE triggers (helm, ladders, crane console). But HDRP's
    underwater volume bounds is a **1200 × 10 × 1148 m trigger** whose top face sits at
    y = −0.2, just below both boats' deck height. Standing on a deck puts the camera at ~y 0.4,
    so looking DOWN at anything crossed that trigger first, got a hit with no `IInteractable`,
    and gave up. Symptom: **E does nothing on a dropped item, while dragging it with Grab works
    fine** — because `PhysicsPickup` uses `QueryTriggerInteraction.Ignore`. Nothing logged.

    Fixed with `RaycastNonAlloc` over all hits, keeping the nearest that actually resolves to an
    interactable with a prompt. That also fixes the reverse case (an interactable behind a
    non-interactable trigger) and any future large trigger volume, of which there will be more.
    **Anything raycasting with `Collide` needs to walk the hits, not take the first.**

60. **A crane that lets go of a load inside its own reach re-hooks it on the next frame.** Auto
    grab fired the instant `hook.First` went null, and a load released over open water is still
    within the reach sphere for the first few frames of its fall — so Space looked like it did
    nothing at all. The released object is now excluded from AUTO grab (never from a deliberate
    keypress) until it has drifted twice the reach away, so the hysteresis cannot chatter at the
    boundary.

61. **The pendulum swing is the one thing on this boat the server has to integrate.** Everything
    else about the vessel is a function of state every peer already holds, but a pendulum has
    history: two peers starting from identical numbers drift apart, and a load hanging somewhere
    different on each machine lands somewhere different depending on who you ask. So `Rig`
    carries two more floats and the server owns them. Published on a **0.05° deadband** — the
    boat is always moving a little, and without that a moored crane would write a
    NetworkVariable every tick forever for a swing nobody can see.

    Period comes from the rope length exactly as a real pendulum's does, so hauling the load up
    under the block makes it snappy and paying out the drum makes it ponderous. `swingResponse`
    is the weight knob (lower = heavier, because a heavy block resists being flicked around and
    lags the boom). Verified numerically before shipping: stable at 60 Hz, returns to exactly
    zero, 1.4–5° on a gentle slew and clamped at 20° on a hard one, settling in 0.5 s on a short
    rope and 4.4 s on a long one.

    **Empty and loaded are separate sets of values**, blended by `loadBlendRate` rather than
    switched, so taking a load reads as the rope going taut — which doubles as the feedback that
    the grab actually worked. A bare block swings 3.7× further than a loaded one on the same slew
    (23.8° against 6.5°, 5 m rope, 4 m/s² for 1.5 s).

62. **Correcting a body's LINEAR velocity toward a moving surface's is not enough to keep it
    from tumbling — its ANGULAR velocity needs the same correction.** `DeckCargoCarry` originally
    only eased loose cargo's travel toward the deck's own velocity (gotcha 61's sibling problem:
    a kinematic collider that teleports pushes nothing). That fixed the net drift, but a crate
    was still free to pick up spin from every ordinary contact as the deck accelerated under it,
    and an object that keeps accumulating spin eventually tumbles end over end — reported back
    as "rolls over and over to the back, but slower". The fix eases `angularVelocity` toward the
    deck's own yaw rate (`omega`, from `BoatMotion.LastFrameYawDelta`) the same way the linear
    term eases toward the deck's travel, at a MUCH stronger rate (`angularGrip` 14 vs `grip` 6) —
    the difference between a crate that slides and one that rolls away. 95% of any spin decays
    in 0.21 s, fast enough to stop visible tumbling while still letting a deliberate shove wobble
    briefly before it settles.

63. **`GameEventBus` is a per-process static — firing it on the server only satisfies the
    SERVER's own quest flags.** Every other place in the codebase that fires a shared-consequence
    event does so from inside a `ClientRpc` (`WorldItemNetworkSync.GrantItemClientRpc`), which is
    easy to miss as the reason if you're only looking at `GameEventBus.Fire` itself — the bus has
    no concept of "network" at all, it is one `Action<string>` per peer. `Generator` broadcasts
    its `generator_powered:<id>` / `generator_stopped:<id>` events through a `FixedString128Bytes`
    `ClientRpc` for exactly this reason: NGO invokes a `ClientRpc` locally on the host as well as
    on remote clients, so this is the one place a "fire everywhere" helper is worth building
    rather than special-casing the caller's own peer.

58. **Never read a component off a scene you have just closed.** `EditorSceneManager.CloseScene`
    destroys the objects, so a `Debug.Log` at the end of a builder that reports
    `zone.Index` throws `MissingReferenceException` *after* the scene has already been saved
    correctly — the build succeeded and the tool call reported failure. Capture anything you
    want to log before `SaveScene`/`CloseScene`.


## Cargo, cranes and placement (2026-07-29)

One mechanism serves the crane hook, the deck lashing area, and (next) furniture on a
property, because from the cargo's point of view they are the same thing: "stop being physics,
ride this transform instead".

| Script | Role |
|---|---|
| `CargoAnchor` | Base for anywhere cargo attaches. Owns network identity (host + index) and the live list of what is riding it. |
| `PlacementZone : CargoAnchor` | A region on a deck or floor. Snaps, validates, draws a border. |
| `CraneHook : CargoAnchor` | The block on the rope. Reports what is in reach; never decides. |
| `CargoAttachment` | On the cargo. Replicates the claim; applies it locally on every peer. |
| `PlacementGhost` | The green/red preview. Local-only, one per machine. |
| `CargoBounds` | True size of an object in its own frame, in metres. |
| `CraneController` | Server-authoritative rig: three floats on the wire. |

**Entering a zone attaches nothing.** It only produces a *plan* — a snapped pose plus a
green/red verdict — which the carrier previews and commits on RELEASE. Released on red, cargo
stays loose and falls where it is. `PlacementZone.Plan` is pure and deterministic: the carrying
client calls it every frame for the ghost, and the server calls it once on release to decide
what actually happens, so both reach the same answer without the client being trusted for the
result.

Design notes that cost thought:

- **Yaw snaps to 90° of the zone's own heading.** Cargo lashed askew on a deck reads as a
  mistake, and a square footprint makes the overlap test exact instead of conservative.
- **Rest height is a downward box sweep, not a fixed deck height.** Stacking then falls out for
  free — the second crate lands on the first one's lid.
- **The overlap test is shrunk to 0.9.** At full size the box is exactly touching whatever it is
  resting on, so every legal placement would report itself blocked by its own support.
- **The hook takes a load by its TOP face and levels it.** A crate coming off the water is
  usually lying at an angle, and a crane straightening it out as it comes up is both what
  happens and what looks right.
- **Auto-grab only ever takes LOOSE cargo.** Anything already lashed into a zone was put there
  deliberately, so unlashing it needs Space — otherwise swinging the hook across a loaded deck
  would strip it.
- **Cargo on the hook swings, but as a replicated pendulum, not as physics.** See gotcha 61.
  It becomes a real dynamic body the instant it is released, which is what makes dropping a pot
  over the side work.
- **The crane is SPLIT between the level root and the rolling hull.** The boom leans with the
  boat, because a crane bolted to a rolling deck leans with it. The rope does not — gravity is
  not a property of the hull — so `ApplyRig` places the rope and hook in WORLD space, hanging
  plumb from wherever the tip ended up. The console stays level and stays parented under the
  controller, so the interaction raycast can still resolve it (gotcha 34) and it does not swing
  out from under the crosshair on every wave.

### What rolls with the hull, and how (2026-07-29)

`RollingTwin` in `CrabBoatBuilder` is the pattern: a visual node under the hull at the same
boat-local pose as a logic node on the level root. The two coincide at zero roll and separate by
the roll angle. Crane boom, both ladders, the zone border **and stowed cargo** all use it.

For the cargo zone the twin is doing double duty — it is the outline's parent AND the zone's
`CargoAnchor.AttachRoot`. That one field is what makes stowed cargo lean with the deck, and it
works because **`PlacementZone.Plan` does all its planning in the attach root's frame**, not its
own transform's:

- The quarter-turn yaw snap is relative to the deck, so a crate lashed during a lean ends up
  square to the planking instead of baking that lean into its stored pose.
- The rest-height sweep runs along the deck's own down (`-AttachRoot.up`), so cargo lies flush on
  a tilted deck instead of floating above it on one side of the boat.
- `Contains` is in the same frame, so the region itself rolls — the lashing area is part of the
  deck, not a box hanging in the air above it.

On land the attach root is the level property root, so this all degenerates to the obvious
behaviour. That is the reason to express it this way rather than special-casing boats.

**Known cost:** a stowed crate's collider now tilts, so it is up to ~0.9 m off the LEVEL deck
collider at full roll three metres off centreline, and a player standing on a crate in a big sea
will drift. That is the same compromise the visual deck has always made (gotcha 33), just now
visible on something you can walk on. The way out, if it ever matters, is to tilt the deck
collider itself and stop the CharacterController sliding — detect "standing on a boat", project
movement onto the deck plane, cancel gravity-induced drift. That would make everything roll
together with no split at all, but it changes the foundation every boat is built on.

### Loose cargo on a moving deck (2026-07-29)

`DeckCargoCarry`. **A kinematic collider that teleports never pushes anything.** `BoatMotion`
writes the hull transform outright, so PhysX gives the deck no velocity and a crate resting on it
gets no friction — the crate stands still in world space while the boat slides out from under it,
which reads as the crate rolling aft at exactly the boat's speed and piling against the transom.
Nothing about the crate is wrong; it is being simulated in the wrong frame of reference.

Fixed by supplying the missing force: anything loose inside the deck volume has its **horizontal**
velocity eased toward the velocity the deck has at that point, rotation included (without the
`ω × r` term, cargo stowed out on the rail stays put while the boat turns underneath it and goes
over the side). Vertical is untouched — falling, settling and floating belong to gravity and
`Buoyancy`.

`grip` is the whole feel knob. Measured against the crab boat getting under way (0.8 m/s² to
7.5 m/s, then steady):

| grip | max aft slide | verdict |
|---|---|---|
| 0 | **190 m and climbing** | the bug: unbounded, ends at the transom |
| 3 | 2.44 m | slithery |
| 6 | 1.19 m | shipped — slides while accelerating, then settles |
| 12 | 0.56 m | nearly bolted down |

Owner-only, like `Buoyancy`: world items are owner-authoritative, so every peer writing
velocities would fight the transform sync. Kinematic bodies are skipped, which covers both lashed
cargo and the hull itself.

Deliberately **no `[RequireComponent(typeof(BoatMotion))]`** — see gotcha 55. It is added to each
boat after `BoatMotion` so component order runs its `LateUpdate` second.


## Performance: measured, not assumed (2026-07-28)

Baseline was 3351 draw calls / 15,918,618 triangles / 26.5 ms. Reflection probes were
re-rendering the 180k-tri snow plane once per cube face. After giving the snow its own
`SnowGround` layer and excluding it from probe culling masks (**the layer did not actually
exist before — the exclusion was a silent no-op**), plus a 20 s probe interval gated on
player distance: 2118 draw calls / 2,215,792 triangles.

**The frame time barely moved, and that is the important result.** With the snow renderer
disabled outright the frame is no faster (26.3 ms). GPU 23.8 ms and CPU main 23.4 ms are
both saturated by the HDRP feature stack — volumetric clouds, volumetric fog, SSR, SSAO,
four shadowed lights. Geometry is free at this scale. **The next optimisation pass belongs
in quality tiers on those features, not in more mesh reduction.**

Caveat when measuring: a reflection probe capture spikes the frame it lands on (one sample
read 4M triangles / 55 ms). Freeze `PeriodicReflectionProbe` before sampling, and prefer
`manage_profiler get_frame_timing` over a single `UnityStats` read.

## Snow LOD ring mesh

The snow plane is a player-following concentric-LOD mesh: 1/3 m spacing out to 16 m — the
same as the old uniform grid, so trails are unchanged where you can see them — coarsening
through 1 m / 4 m / 8 m rings to 160 m. 90,601 verts / 180,000 tris became 22,548 / 36,152.

Three things it must keep doing:
- **Snap the origin to an 8 m world lattice** (`SnowGroundFollow`). A freely sliding grid
  samples different texels every frame and the surface visibly crawls. The snap step must
  be a multiple of the coarsest ring's step.
- **Skirt BOTH sides of every LOD seam.** Adjacent rings share corner vertices but the fine
  edge has extra vertices between them, so the fine polyline and the coarse chord diverge
  into a lens-shaped hole. Which side is higher varies, so one skirt is not enough.
- **`SampleMask` returns 0 outside the deformation region**, not 1. The outer rings
  deliberately overhang the 100 m region so the far corner is covered from anywhere inside
  it; without this the overhang clamp-samples the edge texel and smears snow onto ground
  that has no snow data.

Collision does NOT follow the player — the root keeps the region-sized box collider and a
child carries the mesh.

## Chat and voice

`ChatRelay` (NetworkBehaviour, World scene) — Local/Global/System channels. The **server**
resolves who hears a Local message from real player positions, resolves the display name
from `ServerPlayerRegistry`, and rate limits per client. Lines starting with `/` are split
off before they can reach anyone else and go to `AdminService`, which already owns
permissions — chat has no command path of its own.

`VoiceChatService` (plain MonoBehaviour, World scene) — Vivox. Not a NetworkObject: Vivox
carries its own audio, so the game only agrees a channel name and reports the listener pose.
Channels are named from `NetworkSessionManager.SessionKey` (lobby id, or auth id for solo)
so two sessions on the same Vivox project cannot hear each other.
- The listener is the **camera**, not the body — panning uses the forward vector, so the
  body would put voices behind you when you looked over your shoulder.
- It self-starts in `Start()` and not from `OnSessionStarted`: that event fires *before*
  the World scene loads, so a listener in this scene would never hear it.
- Push-to-talk starts closed.

Keys: Enter type, Y switch channel, V push-to-talk, backquote console. All rebindable.

### Dithering removed (2026-07-28) — Evan asked for this twice, do not reintroduce it

The PSX pass originally used a **4x4 ordered Bayer matrix**, which tiles into a hard
crosshatch that is extremely visible on smooth gradients and was genuinely tiring to look
at. Two things were wrong and both had to be fixed:

1. **The pattern.** Bayer → interleaved gradient noise with a triangular PDF. Static (PSX
   dither never crawled), but unstructured, so it reads as fine grain rather than a grid.
2. **The banding underneath.** Dropping dither amplitude exposed contour rings in every
   dark falloff. This was NOT output precision — it survived at 255 levels, with HDRP's
   camera `dithering` on, and with volumetric fog off. The cause is that the pass
   quantized **linearly** on a post-tonemap-but-still-linear buffer in a very dark game:
   a 1/95 linear step is a ~13% jump on a 0.08 pixel. Quantizing in **perceptual (sqrt)
   space** and squaring back spends the levels where the eye resolves them and the banding
   disappears entirely.

Result: `dither = 0` by default, levels 95/191/95, and the image has **neither grain nor
banding**. The PSX character now comes from internal resolution, vertex snap and affine
warp — not from noise. `dither` is still exposed if a deliberately noisier look is ever
wanted; because it now dithers in perceptual space it stays useful at low amplitudes.

`PSXSetup.AddToWorldProfile` re-pushes the current C# defaults onto the existing override
instead of bailing when one exists — values already serialized in a VolumeProfile do NOT
pick up changed defaults, so re-run `Game/Setup/Register PSX Post Process` after editing
them.

### Snow collision (fixed 2026-07-28)

The snow mesh is displaced entirely in the **vertex shader**, so a MeshCollider only ever
describes the flat undisplaced plane. With `maxSnowDepth = 1m` the visible surface sat a
metre above the physics ground: the player spawned *inside* the snow volume looking at
backfaces, which reads exactly like falling through the floor.

`SnowSurfaceCollider` (on `SnowGround`) now drives a BoxCollider spanning the region with
its top face at `snowDepth − sinkDepth`. `sinkDepth` must equal the material's
`_DepthMeters` — both are written from `SnowGroundBuilder.TRAIL_DEPTH` (0.35) — so a body
rests exactly at the bottom of the footprint it compresses. Verified: at 0.8 m coverage the
player stands 0.27 m below the surface, grounded.

### Street snow, snow masking, precipitation occlusion (2026-07-28)

The base is a **street**, so snow is now shallow by design and three things follow.

**1. Footprints carve to the road.** `maxSnowDepth` dropped 1.0m → **0.2m**, and the shader
clips fragments below `_MinThickness` (0.012m) so bare road shows instead of a paper-thin
snow sheet z-fighting the surface underneath.

The carve math changed and the reason is subtle: it was `drop = compression *
min(_DepthMeters, snowHeight)`, which caps the carve at exactly the lying depth — so only
a *perfect* compression of 1.0 could reach zero, and the 9-tap tent filter that smooths the
height field never produces 1.0. Trails always stopped a few centimetres short. Now the
carve is allowed to **overshoot** and the result is clamped:
`drop = compression * _DepthMeters; lift = max(snowHeight - drop, 0)`. With
`TRAIL_DEPTH` (0.3m) at ~1.5x the snow depth, the core of a footprint hits bare street
while its edges still ramp out. Measured: trail centre compression 0.90 → 0.000m
thickness; untouched snow 0.200m.

This also **dissolves the old collision problem**. `sinkDepth` = `TRAIL_DEPTH` >=
`maxSnowDepth`, so the collider top sits at street level permanently — the player walks on
the road with snow around their ankles, and deep snow can no longer flood interiors.
Raising `maxSnowDepth` for a rural/deep region brings that trade-off back; keep
`TRAIL_DEPTH >= maxSnowDepth` or trails stop short again.

**2. Snow mask keeps snow out of buildings.** `SnowDeformationManager` owns a second R8 RT
(`_SnowMaskRT`, 1 = snow allowed) painted from every active `SnowBlocker` footprint via
`SnowDeform.shader` pass 2 (min blend, feathered edge). The snow shader multiplies its
depth by the mask, so blocked ground stays flat at street level.

`SnowBlocker` takes its axis-aligned XZ footprint from renderer bounds or a hand-set size,
with an `inset` so exterior walls still catch snow against them. Repaint is triggered by
`MarkMaskDirty()` on enable/disable and runs in `LateUpdate`, so a whole streamed district
registers before the mask is painted. Verified: mask = 0 inside both test buildings, 1 on
the street and on the storefront forecourt (which *should* collect snow).

**Limitation:** footprints are axis-aligned rectangles. Rotated buildings will over-mask at
the corners — needs a rotated-rect or per-mesh stamp when the city has non-grid geometry.

**3. Rain/snow no longer falls through roofs.** Two mechanisms, because neither is enough
alone: `PrecipitationController` enables particle **world collision** (Medium quality,
`lifetimeLoss = 1` so drops die on contact) which stops precipitation at a roof edge; and
an **overhead raycast** from the player fades emission out under cover, because the emitter
box sits 14m up and would otherwise spawn a full downpour indoors just to kill it instantly
on the ceiling. Collision quality is deliberately Medium — High is one raycast per particle
per frame, which at these counts is not affordable.

**Old known limitation, now resolved:** the flat snow collider used to raise the walkable
surface inside buildings under deep snow. Street-depth snow plus the mask removes it.

### Spawn placement (fixed 2026-07-28)

NGO spawns the player object at connection approval, which on the host is **before**
NetworkSceneManager finishes loading the gameplay scene — so `PlayerSpawnPoint` doesn't
exist yet, there is no ground at all, and a one-shot placement attempt silently no-ops
while the player free-falls. `NetworkPlayer` now keeps `FirstPersonController` disabled
(no gravity) and retries `TryPlaceAtSpawn()` every frame until the spawn point appears,
with a `spawnPointWaitTimeout` fallback. Placement also runs through `GroundProbe`, which
drops the authored/saved Y onto whatever surface is actually on top — this is what makes
both spawn and save-restore snow-depth-agnostic.

## Water: roughness gradient, driving, climbing (2026-07-28)

`Game/Setup/Build Water`. Water level −3.2; the ground and snow region are both 100 m
square, so the beach starts at z=46 where the ground ends and shelves down — water never
touches the street.

**Scale.** The quad is 1200 m spanning z 20..1220. It has to be: "rough 300 m out" needs
300 m of open water past a shoreline at z≈72.

**Roughness by place.** Open-water settings (36 m/s over a 180 m repeat) are the *storm*
state; `WaterRoughnessMask.png` scales them down inshore. Fully sheltered to z=150,
smoothstep to full strength by z=400 — about 300 m past the beach. Sheltered multipliers
are 0.06 swell / 0.16 agitation / 0.50 ripples; ripples keep most of their strength because
glassy water reads as broken rather than calm. The 180 m repetition is not tunable downward:
HDRP's default 500 m repeat is flat across a 13 m hull and the boat measured *zero* tilt.

**Underwater.** `underWater` + `underWaterRefraction`, with `absorptionDistance` 4.5 and
`absorptionDistanceMultiplier` 3 — you see roughly three times further under the surface
than through it, because absorption tuned for looking *into* water leaves you blind once
submerged. Caustics use band 2 (the ripples); bands 0–1 are the 180 m swell and produce
enormous soft smears instead of the recognisable dancing net.

**The boat is drivable, and that is a deliberate break in the architecture.** Autopilot is a
pure function of `ServerTime.Time` and replicates nothing. A hull steered by a human has no
function of time to evaluate, so the server integrates it and replicates twelve bytes
(`BoatHelm.Nav`: XZ, heading, speed); clients ease onto it rather than snapping. **Once
engaged it never returns to the patrol course** — handing back to a time-driven course would
teleport the hull to wherever that course says it should be by now. Released, it coasts.
Rudder authority scales with way on and reverses going astern, which is the single thing
that makes a boat feel like a boat and not a car.

**Ladder** (`Ladder.cs`) is entirely local — the climber's own NetworkTransform already
replicates the result, so there is nothing to agree on. The climb track is in ladder-LOCAL
space and re-resolved every LateUpdate, which is what carries the climber with a heaving,
turning boat for free. Parented to the level root, not the rolling hull: a 16° roll would
throw the top exit most of a metre sideways, over the rail.

**Once-per-frame toggle guard.** Both the ladder and the helm are reachable from the
interaction raycast *and* from a direct input read (so you are not stranded if you look
away). Without a `Time.frameCount` guard on the toggle, one keypress mounts and instantly
dismounts.

**Helm seat state follows the replicated `_driver`, never the local button press.**
Releasing optimistically hands movement back before the server agrees, and if the server
refuses you end up walking around while still steering.

### Riders on a moving deck, across the network (2026-07-29)

`BoatRiderCarry` runs two mechanisms that solve different problems, and the split matters:

**Network parenting (server).** Riders inside the deck volume are parented to the boat's
NetworkObject, which flips their NetworkTransform into local space. This is the part that
makes *other people* on the deck look right — see gotcha 45. The boat's pose is a pure
function of server time, so every peer reconstructs the same world position from that local
offset exactly; the only thing left being interpolated is the rider's walking. `TrySetParent`
is server-only, and has to be, or two peers could disagree about whose deck someone is on.

**Local carry (every peer, own player only).** Applied only while NOT parented — during the
round trip it takes the server to notice you stepped aboard, and in any session with no
network at all. See gotcha 47 for why it must stop once the parent lands, and why yaw is
the exception that must keep being applied either way.

**Hysteresis, and only ever claiming an UNPARENTED player.** Boarding uses the plain deck
volume, leaving uses a padded one, because each parent change is a network message. And a
boat never steals a rider from another boat: stepping between two hulls costs one frame with
no parent, which the local carry covers, whereas letting boats claim each other's riders lets
two overlapping deck volumes trade the same player back and forth every frame.

**The ladder owns its climber outright.** `BoatRiderCarry` bails on `IsClimbing`. The ladder
is bolted to the same hull and already applies its own yaw, so both running would spin the
climber at twice the boat's rate.

### Hulls below the waterline (2026-07-29)

The tug now has a **hold**: floor at boat-local y=−2.6 against a waterline at y=−1.1, so a
metre and a half of that room is under the sea outside it. It is the reference case for the
submerged walkspaces to come, and it takes three things agreeing — HDRP does one of them.

- **The surface** is pure geometry: a mesh drawn with `MaterialWaterExclusion` tags those
  pixels in the stencil buffer and the water surface is rejected there. No code at runtime.
  Requires `supportWaterExclusion` on the HDRP asset (on) and the `WaterExclusion` frame
  setting (on by default for cameras). See gotcha 49 for the shape constraint.
- **The underwater effect** and **swimming** both go through `DryHullVolume`, which is a
  box in hull-local space that rides the boat for free. See gotcha 48 for why neither is
  handled by the excluder.

`WaterVolume` switches `WaterSurface.underWater` off while the local camera is inside a dry
volume. Toggling the whole surface is the right lever rather than a blunt one: this is a
per-peer decision about one camera, and every peer has exactly one. It never turns the
effect *on* for a surface that was authored without it.

**The hold is split exactly the way the deck already is**: visuals on the rolling hull so it
heels over with everything else, level colliders on the root for the floor you stand on
(a CharacterController capsule is always world-upright and slides down a tilted collider).
The exclusion mesh and the dry volume stay level too, and for the exclusion that is not just
convenience — rolled with the hull, its top edge would tilt out of the water plane and dip
under the surface on the low side, letting the sea render back into the room at exactly the
moment the boat is working hardest.

Note the ordering trap: `BuildBoat` strips primitive colliders from the whole hull *before*
`BuildHold` runs, so the hold's visual boxes have to shed their own or the room ends up with
a second, tilting set of walls inside the level ones.

The boarding ladder moved forward to z=5.4. Its top exit lands inboard at x=−1.45, which
used to be clear deck and is now the middle of the hatch — climbing aboard from the water
would have dropped you straight down into the hold.


### The crabber, and both boats moored (2026-07-29)

`Game/Setup/Build Crab Boat` → `TestCrabBoat`. Twenty metres by 6.4, wheelhouse forward with a
walk-in doorway aft, and the rest of it open working deck. Its own generator rather than a
second mode of `WaterBuilder`, because that one carries a heavily tuned tug with a
below-waterline hold and two water-exclusion meshes; parameterising it into two vessels would
put both at risk every time either changed.

Both boats are now **moored off the beach** (tug at (−12, 78), crabber at (11, 80)) instead of
running patrol circles. A test boat you have to chase is a test boat that does not get tested.
`BoatMotion` now checks the **helm before `moored`** — mooring is a default heading, not a
restraint, and a moored boat that refused to be driven would be scenery. `goto tug` and
`goto crabboat` land on each working deck.

Layout numbers worth keeping (all boat-local, deck top at 0.80 to match the tug so stepping
between the two does not change height):

- Crane pedestal at (2.6, 0.80, 2.6), starboard against the wheelhouse, 11.5 m boom.
- Lashing area centred (−0.4, 2.20, −3.0), 4.8 × 7.6 m, bottom flush with the deck.
- **Reach was the constraint on both.** Far corner of the area is 10.8 m from the pedestal
  against 11.4 m of reach at minimum elevation; the near corner is 1.9 m against 1.6 m at
  maximum. `luffMax` went 78° → 82° specifically because at 78° the nearest metre and a half
  was unreachable — which is exactly where a busy deck stacks things.
- Slew is limited to ±100° so the boom cannot swing through the wheelhouse at low elevation.
- Crane controls are on the **wheelhouse roof**, not the pedestal: from up there the operator
  can see the whole working deck and both rails, which is the only spot on this boat where the
  job is doable. Reached by a second ladder up the aft face.

The **`Solid()` helper** is the pattern for anything both seen and walked on: the visual goes on
the rolling hull child, a matching invisible `BoxCollider` goes on the level root. The mismatch
between them is the roll angle — a few degrees in ordinary water, which is the same trade the
deck itself has always made (gotcha 33 and `BoatMotion`'s class summary).

Deck lights are **point lights, not shadowed spots**, on purpose: the cached shadow atlas that
everything ashore now uses would bake this boat's shadows at the mooring and leave them there,
and an every-frame shadow map on a moving vessel is exactly the cost that was just taken out
of the town.


## Fuel system (2026-07-29)

`FuelContainer` marks a world item as fuel (jerry can 20 L, fuel barrel 100 L — set by
`ItemsBuilder`, not on `ItemData`, because "how many litres" is a property of the physical
prefab, not the shared catalog entry). `FuelTank` is a server-authoritative refillable tank
(one `NetworkVariable<float>`): interact while carrying a `FuelContainer` to pour it in and
consume the container, interact empty-handed to just read the gauge.

**Consumers never touch litres or containers, only `TryConsume(litersPerSecond, dt)`.** It
returns whether there was fuel to draw *before* this call — the last tick before empty still
returns true (no visible stutter exactly at zero), and the very next call reports false, which
is the caller's shutdown signal. Both current consumers use it the same way:

- **`BoatHelm`** gates the THROTTLE, not the boat. Out of fuel, the helm's own `throttle`
  variable (not the raw input) drops to zero for the rate calculation too — using the raw
  input there would have picked the acceleration-toward-zero rate instead of drag, so an empty
  tank would have "decelerated hard" rather than genuinely coasting. Steering still answers for
  as long as there is way on, because rudder authority is already tied to actual speed.
- **`Generator`** refuses to start at all with an empty tank, and shuts itself off the instant
  `TryConsume` reports false.

**`Generator` firing its own events is the one place a "broadcast to every peer" helper earns
its keep** — see gotcha 63. `GameEventBus` is a bare per-process static; a generator switching
on is shared world state, so `generator_powered:<id>` / `generator_stopped:<id>` go out through
a `ClientRpc` rather than a local `GameEventBus.Fire`, and every peer (host included) applies
`poweredObjects` from the replicated `running` flag rather than from the RPC itself.

Both boats have their own dedicated tank (tug 90 L cap / 35 L start / 40 L·h⁻¹ burn; crabber
140 L / 50 L / 55 L·h⁻¹ — heavier hull, faster engine) at a filler cap beside the helm. The
apartment's tank starts at 0 L specifically so refuelling-then-switching-on is the first thing
that has to happen, not an afterthought.


## Home / property system, first pass (2026-07-29)

The placement foundation built for the boats (`CargoAnchor`, `PlacementZone`,
`CargoAttachment`, the green/red ghost) turned out to need nothing boat-specific added for a
building: `PropertyBuilder` wires a `PlacementZone` onto "Apartment Floor" — geometry Evan
built by hand, three ProBuilder slabs — by reading its **collider bounds at build time** rather
than hardcoding numbers, so editing the floor and re-running `Build Property` re-fits the zone
automatically. The same method takes a floor name, so the next property is a one-line call.

The floor's three slabs form an L/dumbbell shape with two notches inside the overall bounding
box. The zone still just uses the bounding rectangle — this is deliberately NOT a problem,
because `PlacementZone.Plan`'s support-height sweep already requires a real collider to rest
on; a placement attempted over one of the notches simply finds no floor and comes back red.
Shape-correctness falls out of the physics check for free, so there was no reason to build a
multi-region zone.

**`ItemsBuilder` gained composite geometry** for furniture. Every existing item is one
primitive scaled up, which is fine for a crate but gives a couch no backrest — just a bigger
box. `ItemDef.buildGeometry` (an `Action<Transform, Material>`) lets an item build several
child boxes instead; `CargoBounds` already walks every collider under an object's root, so a
multi-box couch measures and places correctly with no changes there. First two pieces: **couch**
(seat + back + two arms, w3×h2, mass 30 — heavy enough to trip the carry speed penalty) and
**chair** (seat + back + four legs, w1×h1, mass 6).

**Not built yet, and worth flagging before it's assumed done:** buying/owning properties, and
the "manually placed property outline... transparent green walls near the fence border" from
the original spec — that is a different visual (vertical walls marking a plot boundary) from
the floor's own placement-region outline built here, which is a low border on the floor itself.


## Admin console

Backquote (`` ` ``) opens it. The host is admin automatically; others need
`admin grant <clientId>` (see `players` for ids). `help` lists everything.

Every command executes on the SERVER after a permission check. The "Look" group is
presentation-only state, so it is broadcast by ClientRpc rather than run server-side —
otherwise a host tuning the look would see a different frame from everyone else.

- **World:** `weather <type> [seconds] [intensity]`, `time <hour> [dayLengthMinutes]`,
  `snow <0-1>`
- **Look:** `wet <0-1|auto>`, `exposure <evBias>`, `dither <0-2>`, `res <height|native>`
- **Player:** `goto <storefront|warehouse|spawn>`, `tp <x> <y> <z>`, `speed <mult>`,
  `heal`, `give <itemId> [count]`
- **Session:** `quest <accept|complete> <id>`, `players`, `admin <grant|revoke> <clientId>`

`snow` matters for testing: natural accumulation is ~0.004 coverage/sec, so reaching full
depth takes four minutes of real time. `wet` likewise pins wetness instead of waiting out
the soak curve. `exposure` is an EV offset — **negative brightens**.

## Graphics testbed (`Game/Setup/Build Test Buildings`)

Two lighting testbeds in World, built entirely from box primitives so the numbers stay
legible. Rebuild wipes and regenerates `TestBuildings`.

- **Storefront** at `(-26, 0, 18)` — neon pink/blue signage, lit interior visible through
  the window, dark asphalt forecourt for reflections.
- **Warehouse** at `(26, 0, -20)` — red ceiling strips + shadow-casting work lights that
  throw volumetric beams, one cold blue leak at the back for contrast.
- **GraphicsQualityVolume** — global Volume (priority −10, so it never fights the runtime
  profile `WeatherManager` builds) carrying Screen Space Reflections + AO.

### Adjustable lighting (`PracticalLight`)

Every authored fixture carries a `PracticalLight`, which owns its intensity at runtime and
solves two things:

- **Night response.** A fixture tuned to read at noon is far too weak once the sun is down,
  because exposure follows the day curve. `nightBoost` multiplies output at full night on
  the same `SunController.DayBlend01` curve. Exteriors want 2-4; interiors that are on
  regardless want ~1.1-1.3.
- **Live tuning.** Lights join a named `group`, scaled from the console without leaving
  play mode: `light warehouse 2`, `light list`. Broadcast to all clients.

Groups currently: `neon`, `storefront`, `warehouse` (nightBoost 3.5 - the red work lights
Evan wanted stronger after dark), `street` (3), `apartment` (1.15-1.3).

Intensity must be written as range/cone → `lightUnit` → `intensity`. `PracticalLight` does
this; anything else authoring HDRP lights must too (gotcha 19).

### Street, apartment (`Game/Setup/Build Street`, `Build Apartment`)

- **Street** at z = -35, 90m long: road, kerbs, pavements, dashed centre line, 6 sodium
  lamps on alternating sides. Carries `SurfaceWetness`. Surfaces sit BELOW the 0.2m snow
  depth on purpose (road 6cm, pavement 16cm) so snow covers them and footprints reveal
  them again.
- **Apartment** at (0, 0, 34): 3 storeys, warm interior practicals, emissive window bays
  that read from outside, ground-floor entrance, and a working `ElevatorPlatform` in an
  open shaft with a call button. Verified: cab 0.25m → 7.25m, player rode it grounded.

Note asphalt albedo. The first pass used 0.06, which is close to black - the lamps lit
almost nothing back and the street was unreadable at night. 0.13 is both more accurate and
far more legible. The storefront forecourt keeps a darker `TB_Forecourt` because it is
lit by neon at close range.

Supporting runtime components:
- `SurfaceWetness` — raises smoothness and darkens base colour as `WeatherManager.RainRate`
  climbs, dries off ~5× slower. Operates on material *instances*, so the `.mat` assets are
  never dirtied. Publishes `_GameWetness` globally. **Note:** because renderers use
  instances at runtime, editing the shared material in play mode has no visible effect —
  tune through the renderer's `.material` or re-run the builder.
- `PeriodicReflectionProbe` — realtime probes refresh on an 8 s stagger; capturing once at
  startup goes stale as the sun moves and neon takes over at night.

Tuning notes from the first pass: point lights land in the 8k–30k lm range, spots 50k–70k;
neon emissive at 2600 nits, dim strips at 900.

## Trust model

Co-op, not competitive: world item EXISTENCE is server-authoritative (spawn/despawn/
pickup), inventory CONTENTS are owner-client-authoritative. Health server-write. Quests:
shared progress server-owned, personal state per client. Saves: host-only disk IO, client
records requested over RPC and stored under the client's auth id.

## User preferences observed

- Proceed autonomously; ask only for genuine scope decisions.
- Conserve tokens: batch work, minimal play-mode verification, no redundant screenshots.
- Keep this doc + the memory files updated as phases complete.
