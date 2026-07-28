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
(quest log), `Build Console`, `Build HUD` (health/clock) and `Build Map And Toasts`
(map + toast stack). Those five patch the existing prefab, so re-run them in order after
any `Build Menus`. All generators are individually idempotent.

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

## Trust model

Co-op, not competitive: world item EXISTENCE is server-authoritative (spawn/despawn/
pickup), inventory CONTENTS are owner-client-authoritative. Health server-write. Quests:
shared progress server-owned, personal state per client. Saves: host-only disk IO, client
records requested over RPC and stored under the client's auth id.

## User preferences observed

- Proceed autonomously; ask only for genuine scope decisions.
- Conserve tokens: batch work, minimal play-mode verification, no redundant screenshots.
- Keep this doc + the memory files updated as phases complete.
