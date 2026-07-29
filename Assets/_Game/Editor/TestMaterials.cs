using Game.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Game.Editor
{
    /// <summary>
    /// Shared primitives for the greybox test content (buildings, street, apartment).
    /// Everything is box meshes and HDRP Lit so the numbers stay legible and tweakable.
    /// </summary>
    public static class TestMaterials
    {
        public const string MAT_FOLDER = "Assets/_Game/Materials/Test";

        /// <summary>
        /// Shadows stop being rendered past this multiple of a light's own range. Six is
        /// well beyond where a shadow carries any detail and far enough that you never catch
        /// it dropping out.
        /// </summary>
        public const float SHADOW_FADE_RANGE_MULTIPLE = 6f;

        // ------------------------------------------------------------------ materials

        public static Material Lit(string name, Color baseColor, float smoothness, float metallic)
        {
            var mat = LoadOrCreate(name);
            mat.SetColor("_BaseColor", baseColor);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        public static Material Emissive(string name, Color color, float nits)
        {
            var mat = LoadOrCreate(name);
            // Emissive props read as the source itself, so base colour is nearly black -
            // all the visible energy comes from emission.
            mat.SetColor("_BaseColor", color * 0.08f);
            mat.SetFloat("_Smoothness", 0.6f);
            mat.SetFloat("_Metallic", 0f);
            HDMaterial.SetUseEmissiveIntensity(mat, true);
            HDMaterial.SetEmissiveColor(mat, color);
            HDMaterial.SetEmissiveIntensity(mat, nits, EmissiveIntensityUnit.Nits);
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material LoadOrCreate(string name)
        {
            EnsureFolder(MAT_FOLDER);
            string path = $"{MAT_FOLDER}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("HDRP/Lit");
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            return mat;
        }

        // ------------------------------------------------------------------ geometry

        public static GameObject Box(string name, Transform parent, Vector3 localPos,
            Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        public static Transform Node(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        // ------------------------------------------------------------------ lights

        public static void PointLight(string name, Transform parent, Vector3 localPos,
            Color color, float lumens, float range, float volumetric,
            string group = "default", float nightBoost = 1f)
        {
            MakeLight(name, parent, localPos, Vector3.zero, LightType.Point, color, lumens,
                range, 0f, volumetric, false, group, nightBoost);
        }

        public static void SpotLight(string name, Transform parent, Vector3 localPos,
            Vector3 euler, Color color, float lumens, float range, float angle,
            float volumetric, bool shadows, string group = "default", float nightBoost = 1f)
        {
            MakeLight(name, parent, localPos, euler, LightType.Spot, color, lumens, range,
                angle, volumetric, shadows, group, nightBoost);
        }

        private static void MakeLight(string name, Transform parent, Vector3 localPos,
            Vector3 euler, LightType type, Color color, float lumens, float range,
            float spotAngle, float volumetric, bool shadows, string group, float nightBoost)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localEulerAngles = euler;

            var light = go.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.shadows = shadows ? LightShadows.Soft : LightShadows.None;

            var hd = go.GetComponent<HDAdditionalLightData>();
            if (hd == null) hd = go.AddComponent<HDAdditionalLightData>();

            // Order matters. HDRP re-derives intensity whenever the emitting shape changes,
            // so range and cone must be final BEFORE the value is written - SetIntensity()
            // first and SetSpotAngle() after runs the lumen conversion twice (~160x dim).
            hd.range = range;
            if (type == LightType.Spot) hd.SetSpotAngle(spotAngle);
            hd.lightUnit = LightUnit.Lumen;
            hd.intensity = lumens;

            // Without this, lights hit surfaces but leave the fog untouched - no beams, no
            // haze around the neon, which is most of the look.
            hd.affectsVolumetric = true;
            hd.volumetricDimmer = volumetric;

            // HDRP defaults every fade distance to 10000, i.e. never. A 24 m work light was
            // still re-rendering its shadow map while you looked at it from 300 m out at sea,
            // for a lit patch a few pixels across.
            //
            // Only the SHADOW fades. The light itself deliberately does not: lighting is
            // view-independent, so culling it by camera distance would visibly darken the
            // town as you sail away, which is a look change and not an optimisation. A shadow
            // at six times the light's own range carries no information.
            if (shadows) hd.shadowFadeDistance = range * SHADOW_FADE_RANGE_MULTIPLE;

            // PracticalLight owns the intensity from here: day/night response plus live
            // console scaling by group.
            var practical = go.AddComponent<PracticalLight>();
            var so = new SerializedObject(practical);
            so.FindProperty("group").stringValue = group;
            so.FindProperty("baseLumens").floatValue = lumens;
            so.FindProperty("nightBoost").floatValue = nightBoost;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------ misc

        /// <summary>Marks ground snow must not lie on. See <see cref="SnowBlocker"/>.</summary>
        public static void SnowBlock(Transform parent, string name, Vector3 localPos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var blocker = go.AddComponent<SnowBlocker>();
            var so = new SerializedObject(blocker);
            so.FindProperty("useRendererBounds").boolValue = false;
            so.FindProperty("size").vector2Value = size;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void EnsureFolder(string folder)
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
