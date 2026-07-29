using System.IO;
using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Builds the night sky: a procedural star cubemap fed to the physically based sky's
    /// space emission, and a Moon celestial body opposite the Sun.
    ///
    /// Both are generated rather than imported so the look stays in the repo as code -
    /// sparse hard pixels are exactly the PSX register anyway, and a hand-painted 4K star
    /// map would read as far too modern next to everything else.
    ///
    /// The sky itself hides the stars by day for free: atmospheric scattering in
    /// PhysicallyBasedSky drowns space emission once the sun is up, so nothing has to
    /// fade them manually.
    ///
    /// Menu: Game/Setup/Build Sky. Idempotent.
    /// </summary>
    public static class SkyBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string TEX_FOLDER = "Assets/_Game/Textures";
        private const string STARS_PATH = TEX_FOLDER + "/StarField.asset";
        private const string MOON_PATH = TEX_FOLDER + "/MoonSurface.asset";
        private const string MOON_NAME = "Moon";
        private const string MOON_DISC_NAME = "MoonDisc";

        private const int STAR_FACE_SIZE = 512;
        private const int STAR_SEED = 20260728;
        // Roughly how many stars land on the whole sphere. The eye reads density, not
        // count: much above this and it stops looking like a sky and starts looking
        // like noise.
        private const int STAR_COUNT_TARGET = 5500;

        [MenuItem("Game/Setup/Build Sky")]
        public static void Build()
        {
            EnsureFolder(TEX_FOLDER);

            var stars = BuildStarCubemap();
            var moonSurface = BuildMoonTexture();
            ApplyToScene(stars, moonSurface);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[SkyBuilder] Star field + moon built.");
        }

        // --- star field ---------------------------------------------------------------

        private static Cubemap BuildStarCubemap()
        {
            var cube = new Cubemap(STAR_FACE_SIZE, TextureFormat.RGBAHalf, mipChain: false)
            {
                name = "StarField",
                filterMode = FilterMode.Point,   // stars should be hard pixels, not blobs
                wrapMode = TextureWrapMode.Clamp,
            };

            var rng = new System.Random(STAR_SEED);
            // Six faces of N^2 pixels each; converting the target count into a per-pixel
            // probability keeps density independent of the face resolution.
            double perPixel = STAR_COUNT_TARGET / (6.0 * STAR_FACE_SIZE * STAR_FACE_SIZE);

            for (int face = 0; face < 6; face++)
            {
                var pixels = new Color[STAR_FACE_SIZE * STAR_FACE_SIZE];
                for (int y = 0; y < STAR_FACE_SIZE; y++)
                    for (int x = 0; x < STAR_FACE_SIZE; x++)
                    {
                        float u = 2f * (x + 0.5f) / STAR_FACE_SIZE - 1f;
                        float v = 2f * (y + 0.5f) / STAR_FACE_SIZE - 1f;
                        Vector3 dir = FaceDirection((CubemapFace)face, u, v).normalized;

                        Color c = MilkyWay(dir, rng);

                        // A cube face is a flat plane, so its pixels do NOT cover equal
                        // solid angle - the corners cover far less sky than the centre.
                        // Without this weight the corners of every face grow visibly
                        // denser star clusters, which is a dead giveaway of a cubemap.
                        double solidAngleWeight = Mathf.Pow(1f + u * u + v * v, -1.5f);
                        if (rng.NextDouble() < perPixel * solidAngleWeight * 3.0)
                            c += StarColor(rng);

                        pixels[y * STAR_FACE_SIZE + x] = c;
                    }
                cube.SetPixels(pixels, (CubemapFace)face);
            }

            cube.Apply(updateMipmaps: false);

            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(STARS_PATH);
            if (existing != null) AssetDatabase.DeleteAsset(STARS_PATH);
            AssetDatabase.CreateAsset(cube, STARS_PATH);
            return cube;
        }

        /// <summary>
        /// Star brightness is heavily skewed - a handful of bright ones carry the sky and
        /// the rest are barely there. A flat random gives a uniform dusting that reads as
        /// TV static, so brightness is raised to a power to bias it dim.
        /// </summary>
        private static Color StarColor(System.Random rng)
        {
            float brightness = Mathf.Pow((float)rng.NextDouble(), 3f);
            brightness = Mathf.Lerp(0.12f, 1f, brightness);

            // Real star colour runs blue-white to orange. Keep it subtle: at these sizes
            // saturated stars look like stuck pixels.
            float t = (float)rng.NextDouble();
            var tint = t < 0.15f ? new Color(0.75f, 0.83f, 1f)
                     : t > 0.85f ? new Color(1f, 0.85f, 0.70f)
                     : Color.white;
            return tint * brightness;
        }

        /// <summary>
        /// A faint band of unresolved starlight. Without it the sky is featureless and the
        /// stars have nothing to sit against.
        /// </summary>
        private static Color MilkyWay(Vector3 dir, System.Random rng)
        {
            // Band plane tilted off the horizon so it does not read as a level stripe.
            Vector3 poleAxis = new Vector3(0.35f, 0.72f, -0.6f).normalized;
            float distanceFromBand = Mathf.Abs(Vector3.Dot(dir, poleAxis));
            float band = 1f - Mathf.SmoothStep(0.04f, 0.34f, distanceFromBand);
            if (band <= 0f) return Color.black;

            // Break the band up so it is cloudy rather than an airbrushed streak.
            float clumping = 0.55f + 0.45f * Mathf.PerlinNoise(dir.x * 6f + 11f, dir.z * 6f + 7f);
            float glow = band * clumping * 0.030f;
            return new Color(glow * 0.85f, glow * 0.88f, glow);
        }

        private static Vector3 FaceDirection(CubemapFace face, float u, float v) => face switch
        {
            CubemapFace.PositiveX => new Vector3(1f, -v, -u),
            CubemapFace.NegativeX => new Vector3(-1f, -v, u),
            CubemapFace.PositiveY => new Vector3(u, 1f, v),
            CubemapFace.NegativeY => new Vector3(u, -1f, -v),
            CubemapFace.PositiveZ => new Vector3(u, -v, 1f),
            _ => new Vector3(-u, -v, -1f),
        };

        // --- moon ---------------------------------------------------------------------

        /// <summary>
        /// Grey disc with craters and maria. Sampled across the moon's visible face by
        /// HDRP, so it only needs to look right at a couple of degrees across.
        /// </summary>
        private static Texture2D BuildMoonTexture()
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
            {
                name = "MoonSurface",
                wrapMode = TextureWrapMode.Clamp,
            };

            var rng = new System.Random(STAR_SEED + 1);

            // Dark basalt plains, placed before craters so craters can cut across them.
            var maria = new (Vector2 centre, float radius)[5];
            for (int i = 0; i < maria.Length; i++)
                maria[i] = (new Vector2((float)rng.NextDouble(), (float)rng.NextDouble()),
                            Mathf.Lerp(0.10f, 0.22f, (float)rng.NextDouble()));

            var craters = new (Vector2 centre, float radius)[40];
            for (int i = 0; i < craters.Length; i++)
                craters[i] = (new Vector2((float)rng.NextDouble(), (float)rng.NextDouble()),
                              Mathf.Lerp(0.012f, 0.055f, Mathf.Pow((float)rng.NextDouble(), 2f)));

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                    float shade = 0.78f;

                    foreach (var (centre, radius) in maria)
                        shade -= 0.16f * (1f - Mathf.SmoothStep(radius * 0.5f, radius, Vector2.Distance(p, centre)));

                    foreach (var (centre, radius) in craters)
                    {
                        float d = Vector2.Distance(p, centre);
                        if (d > radius) continue;
                        // Bright rim, dark floor - the thing that actually makes a circle
                        // read as a crater rather than a stain.
                        float k = d / radius;
                        shade += k > 0.78f ? 0.13f : -0.14f * (1f - k);
                    }

                    shade += ((float)rng.NextDouble() - 0.5f) * 0.03f;
                    shade = Mathf.Clamp01(shade);
                    byte b = (byte)(shade * 255f);
                    pixels[y * size + x] = new Color32(b, b, (byte)(shade * 250f), 255);
                }

            tex.SetPixels32(pixels);
            tex.Apply();

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(MOON_PATH);
            if (existing != null) AssetDatabase.DeleteAsset(MOON_PATH);
            AssetDatabase.CreateAsset(tex, MOON_PATH);
            return tex;
        }

        // --- scene wiring ---------------------------------------------------------------

        private static void ApplyToScene(Cubemap stars, Texture2D moonSurface)
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            GameObject sunGo = null, skyGo = null, moonGo = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "Sun") sunGo = root;
                else if (root.GetComponent<Volume>() != null && root.GetComponent<StaticLightingSky>() != null) skyGo = root;
                else if (root.name == MOON_NAME) moonGo = root;
            }

            if (skyGo != null) ConfigureSkyProfile(skyGo, stars);
            else Debug.LogWarning("[SkyBuilder] Sky and Fog Volume not found; stars not assigned.");

            if (moonGo == null)
            {
                moonGo = new GameObject(MOON_NAME);
                SceneManager.MoveGameObjectToScene(moonGo, scene);
            }
            ConfigureMoon(moonGo, moonSurface);

            // The Sun already renders a disc; make sure the Moon is wired into the
            // controller that owns the day/night curve, so there is exactly one place
            // deciding which body is the key light.
            if (sunGo != null && sunGo.TryGetComponent<SunController>(out var controller))
            {
                var so = new SerializedObject(controller);
                so.FindProperty("moonLight").objectReferenceValue = moonGo.GetComponent<Light>();
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[SkyBuilder] SunController not found; moon will not move.");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void ConfigureSkyProfile(GameObject skyGo, Cubemap stars)
        {
            var profile = skyGo.GetComponent<Volume>().sharedProfile;
            if (profile == null || !profile.TryGet<PhysicallyBasedSky>(out var pbs)) return;

            pbs.spaceEmissionTexture.overrideState = true;
            pbs.spaceEmissionTexture.value = stars;
            pbs.spaceEmissionMultiplier.overrideState = true;
            // Night exposure is EV100 8, which tops out around 300 cd/m^2. Stars want to
            // sit near that ceiling so the brightest ones punch and the dim ones do not
            // disappear into the quantizer.
            pbs.spaceEmissionMultiplier.value = 260f;
            pbs.spaceRotation.overrideState = true;

            EditorUtility.SetDirty(profile);
        }

        /// <summary>
        /// The moon is TWO lights, and it has to be.
        ///
        /// In emission mode HDRP derives the disc's radiance from the light's own
        /// intensity spread over its solid angle. Moonlight has to be ~450 lux for the
        /// ground to be readable at night; across a 3.5 degree disc that works out around
        /// 150,000 cd/m^2, which is two hundred times the night exposure ceiling. The disc
        /// clips to a featureless white ball, bloom smears it, and no amount of tinting
        /// pulls it back because the tint is not what the disc brightness is derived from.
        ///
        /// So the roles are split: the root light lights the world and draws nothing, and a
        /// child light draws the disc at an intensity chosen purely for how it looks. The
        /// child contributes well under 1% of the illumination, which is invisible.
        /// </summary>
        private static void ConfigureMoon(GameObject moonGo, Texture2D surface)
        {
            if (!moonGo.TryGetComponent<Light>(out var light)) light = moonGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.useColorTemperature = true;
            // 4200K read as a sunset, not a moon. Moonlight is sunlight, so it is neutral
            // to slightly cool once the eye stops comparing it to a warm interior.
            light.colorTemperature = 6800f;
            light.color = Color.white;
            // Shadows from the moon would double up with the sun's during the crossfade
            // and cost a full cascade set for light nobody reads shadows from.
            light.shadows = LightShadows.None;

            if (!moonGo.TryGetComponent<HDAdditionalLightData>(out var hd))
                hd = moonGo.AddComponent<HDAdditionalLightData>();
            var lightSo = new SerializedObject(hd);
            lightSo.FindProperty("m_InteractsWithSky").boolValue = false;   // no disc here
            lightSo.ApplyModifiedPropertiesWithoutUndo();

            // --- the disc ---
            var discTransform = moonGo.transform.Find(MOON_DISC_NAME);
            if (discTransform == null)
            {
                var disc = new GameObject(MOON_DISC_NAME);
                disc.transform.SetParent(moonGo.transform, false);
                discTransform = disc.transform;
            }
            // Parented with an identity local rotation, so it always points exactly where
            // the moon points without SunController having to know it exists.
            discTransform.localRotation = Quaternion.identity;
            discTransform.localPosition = Vector3.zero;

            var discGo = discTransform.gameObject;
            if (!discGo.TryGetComponent<Light>(out var discLight)) discLight = discGo.AddComponent<Light>();
            discLight.type = LightType.Directional;
            discLight.useColorTemperature = true;
            discLight.colorTemperature = 6800f;
            discLight.shadows = LightShadows.None;
            // Chosen so radiance (intensity / solid angle) lands just under the night
            // exposure ceiling: pi * (3.5 deg / 2 in rad)^2 = 2.9e-3 sr, and 1.5 / 2.9e-3
            // is ~510 cd/m^2 against a ceiling near 600. Bright, but the craters survive.
            discLight.intensity = 1.5f;

            if (!discGo.TryGetComponent<HDAdditionalLightData>(out var discHd))
                discHd = discGo.AddComponent<HDAdditionalLightData>();

            // Half of these are backed by m_-prefixed fields and half are not, because the
            // celestial body block was added to HDAdditionalLightData later than the core
            // light settings. FindProperty returns null for a wrong name and the write then
            // throws, so the names below are read off the live SerializedObject, not guessed.
            var so = new SerializedObject(discHd);
            so.FindProperty("m_InteractsWithSky").boolValue = true;
            // Real angular diameter is 0.52 degrees, which at this field of view is a
            // fleck. Oversizing is the standard game cheat and is what makes a night sky
            // feel composed rather than empty.
            so.FindProperty("m_AngularDiameter").floatValue = 3.5f;
            so.FindProperty("m_Distance").floatValue = 3.84e8f;
            so.FindProperty("surfaceTexture").objectReferenceValue = surface;
            so.FindProperty("surfaceTint").colorValue = Color.white;
            // Emission = 1, NOT ReflectSunLight. Reflecting the sun is the physical answer
            // and it is wrong here: SunController drives the sun to zero lux once it is
            // below the horizon, so at night there is nothing for the moon to reflect and
            // it renders as a dim brown smudge lit only by earthshine.
            so.FindProperty("celestialBodyShadingSource").enumValueIndex = 1;
            so.FindProperty("earthshine").floatValue = 0.15f;
            // A wide, strong flare read as a warm haze swamping the stars around it.
            so.FindProperty("flareSize").floatValue = 0.9f;
            so.FindProperty("flareFalloff").floatValue = 6f;
            so.FindProperty("flareMultiplier").floatValue = 0.08f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
