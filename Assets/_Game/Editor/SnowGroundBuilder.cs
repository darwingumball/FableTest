using System.IO;
using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Creates the deformable snow ground: a densely subdivided plane mesh (vertex
    /// displacement needs geometry to push around) plus the Game/SnowGround material,
    /// placed over the snow deformation region in the World scene.
    /// Menu: Game/Setup/Build Snow Ground. Idempotent.
    /// </summary>
    public static class SnowGroundBuilder
    {
        private const string MESH_PATH = "Assets/_Game/Meshes/SnowPlane.asset";
        private const string MAT_PATH = "Assets/_Game/Materials/SnowGround.mat";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";

        private const float SIZE = 100f;      // must match SnowDeformationManager's region
        // Geometry detail is the limiting factor for how sharp a trail can look:
        // 450 segments over 100m = ~0.22m vertex spacing (~203k verts, one draw).
        private const int SEGMENTS = 600;
        // Snow depth is no longer a constant here - it is driven at runtime by
        // WeatherManager (maxSnowDepth * coverage). See _SnowHeightMeters in the shader.

        // How deep a full-strength stamp presses into lying snow. This single number feeds
        // BOTH the material (_DepthMeters, the visual dent) and SnowSurfaceCollider
        // (sinkDepth, how far a body drops below the surface). If they disagree the player
        // either hovers over their own footprints or wades below them.
        private const float TRAIL_DEPTH = 0.35f;

        [MenuItem("Game/Setup/Build Snow Ground")]
        public static void Build()
        {
            EnsureFolder("Assets/_Game/Meshes");
            EnsureFolder("Assets/_Game/Materials");

            var mesh = BuildMesh();
            var material = BuildMaterial();
            PlaceInScene(mesh, material);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[SnowGroundBuilder] Deformable snow ground built.");
        }

        private static Mesh BuildMesh()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MESH_PATH);
            if (existing != null && existing.vertexCount == (SEGMENTS + 1) * (SEGMENTS + 1))
                return existing;

            int side = SEGMENTS + 1;
            var vertices = new Vector3[side * side];
            var normals = new Vector3[side * side];
            var uvs = new Vector2[side * side];
            float step = SIZE / SEGMENTS;
            float half = SIZE * 0.5f;

            for (int z = 0; z < side; z++)
                for (int x = 0; x < side; x++)
                {
                    int i = z * side + x;
                    vertices[i] = new Vector3(x * step - half, 0f, z * step - half);
                    normals[i] = Vector3.up;
                    uvs[i] = new Vector2(x / (float)SEGMENTS, z / (float)SEGMENTS);
                }

            var triangles = new int[SEGMENTS * SEGMENTS * 6];
            int t = 0;
            for (int z = 0; z < SEGMENTS; z++)
                for (int x = 0; x < SEGMENTS; x++)
                {
                    int i = z * side + x;
                    triangles[t++] = i;
                    triangles[t++] = i + side;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + side;
                    triangles[t++] = i + side + 1;
                }

            var mesh = new Mesh
            {
                name = "SnowPlane",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, // > 65k verts
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles,
            };
            mesh.RecalculateBounds();

            if (existing != null) AssetDatabase.DeleteAsset(MESH_PATH);
            AssetDatabase.CreateAsset(mesh, MESH_PATH);
            return mesh;
        }

        private static Material BuildMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MAT_PATH);
            var shader = Shader.Find("Game/SnowGround");
            if (shader == null)
            {
                Debug.LogError("[SnowGroundBuilder] Shader 'Game/SnowGround' not found.");
                return null;
            }
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MAT_PATH);
            }
            mat.shader = shader;
            mat.SetColor("_SnowColor", new Color(0.90f, 0.92f, 0.96f));
            mat.SetColor("_PackedColor", new Color(0.60f, 0.64f, 0.72f));
            // The shader also clamps this to the current snow height, so shallow snow
            // never gets carved through to the ground beneath.
            mat.SetFloat("_DepthMeters", TRAIL_DEPTH);
            mat.SetFloat("_NormalStrength", 3.5f);
            // Higher = softer, more sculpted prints; lower = sharper but more faceted.
            mat.SetFloat("_SmoothRadiusTexels", 5f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void PlaceInScene(Mesh mesh, Material material)
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            GameObject go = null;
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "SnowGround") go = root;
            if (go == null)
            {
                go = new GameObject("SnowGround");
                SceneManager.MoveGameObjectToScene(go, scene);
            }

            // Sits at ground level; the shader lifts it by the CURRENT snow depth
            // (WeatherManager.maxSnowDepth * coverage), so snow accumulates and melts.
            go.transform.position = new Vector3(0f, 0.02f, 0f);

            // MeshFilter must exist before MeshRenderer, or the renderer throws on access.
            if (!go.TryGetComponent<MeshFilter>(out var filter)) filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            if (!go.TryGetComponent<MeshRenderer>(out var renderer)) renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            // Displacement happens in the vertex shader, so the CPU bounds must stay valid.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // A MeshCollider would describe the flat, undisplaced plane - the displacement
            // only exists in the vertex shader - so the player would walk at y=0 while the
            // snow rendered above their head. SnowSurfaceCollider drives a box whose top
            // face tracks the live snow depth instead.
            var meshCollider = go.GetComponent<MeshCollider>();
            if (meshCollider != null) Object.DestroyImmediate(meshCollider);

            if (!go.TryGetComponent<BoxCollider>(out var box)) box = go.AddComponent<BoxCollider>();
            if (!go.TryGetComponent<SnowSurfaceCollider>(out var surface))
                surface = go.AddComponent<SnowSurfaceCollider>();
            var so = new SerializedObject(surface);
            so.FindProperty("regionSize").floatValue = SIZE;
            so.FindProperty("sinkDepth").floatValue = TRAIL_DEPTH;
            so.ApplyModifiedPropertiesWithoutUndo();
            box.size = new Vector3(SIZE, 6f, SIZE);
            box.center = new Vector3(0f, -3f, 0f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
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
