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
        /// <summary>Excluded from reflection probe culling masks. See PlaceInScene.</summary>
        public const string SNOW_LAYER = "SnowGround";
        /// <summary>Child that carries the mesh and follows the player. See PlaceInScene.</summary>
        private const string MESH_CHILD_NAME = "SnowMesh";

        private const float SIZE = 100f;      // must match SnowDeformationManager's region

        // --- LOD rings -------------------------------------------------------------
        //
        // A uniform grid over the whole region spends its entire budget where nobody is
        // looking: at 40m out, a 0.33m quad is smaller than a pixel, while the trail you
        // are actually standing in needs every vertex it can get. So the mesh is built as
        // concentric square rings that get coarser outward and RIDES WITH THE PLAYER
        // (see SnowGroundFollow), keeping the fine ring under the camera at all times.
        //
        // Near-field spacing is 1/3 m - the same as the old uniform grid - so footprints
        // and trails are pixel-for-pixel what they were. The saving is entirely in
        // geometry nobody could resolve: 361k verts / 180k tris becomes ~20k / ~34k.
        //
        // INVARIANT: each level's half-extent must be an exact multiple of the NEXT
        // level's step, and equal to that level's hole. Otherwise the rings do not share
        // vertices at the seams and no amount of skirting will hide the gap.
        private static readonly (float half, float step)[] Levels =
        {
            (16f, 1f / 3f),   //  96 x  96 cells - the one that matters
            (36f, 1f),        //  72 x  72 minus a 32 x 32 hole
            (80f, 4f),        //  40 x  40 minus an 18 x 18 hole
            (160f, 8f),       //  40 x  40 minus a 20 x 20 hole - reaches the far region
        };                    //  corner from anywhere inside the region

        // Adjacent levels share their corner vertices but the fine edge has extra vertices
        // between them, so the fine polyline and the coarse chord diverge and open a
        // lens-shaped crack. Which side is higher varies, so BOTH sides of every seam get a
        // vertical skirt hanging down far enough to bridge the worst case (the skirt is
        // clipped along with the surface wherever the snow is carved to nothing).
        private const float SKIRT_DROP = 0.6f;

        // The mesh origin snaps to this lattice so vertices never slide between world
        // positions. Must be a multiple of the coarsest step above.
        private const float SNAP_STEP = 8f;
        // Snow depth is no longer a constant here - it is driven at runtime by
        // WeatherManager (maxSnowDepth * coverage). See _SnowHeightMeters in the shader.

        // How deep a full-strength stamp presses into lying snow. This single number feeds
        // BOTH the material (_DepthMeters, the visual dent) and SnowSurfaceCollider
        // (sinkDepth, how far a body drops below the surface). If they disagree the player
        // either hovers over their own footprints or wades below them.
        //
        // Deliberately >= WeatherManager.maxSnowDepth (street depth, ~0.2m): a full stamp
        // then carves the whole way down, the shader clips the zero-thickness fragments,
        // and the street shows through the trail. It also means the collider top sits at
        // street level, so the player walks on the road and the snow is around their
        // ankles - which is why deep snow no longer floods building interiors.
        private const float TRAIL_DEPTH = 0.3f;

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
            var vertices = new System.Collections.Generic.List<Vector3>();
            var triangles = new System.Collections.Generic.List<int>();

            for (int level = 0; level < Levels.Length; level++)
            {
                float innerHalf = level == 0 ? 0f : Levels[level - 1].half;
                AddRing(vertices, triangles, Levels[level].half, Levels[level].step, innerHalf);

                // Seam skirts: the fine side of the seam is the previous level's OUTER
                // edge, the coarse side is this level's INNER edge (the hole rim).
                if (level == 0) continue;
                AddSkirt(vertices, triangles, innerHalf, Levels[level - 1].step);
                AddSkirt(vertices, triangles, innerHalf, Levels[level].step);
            }

            var normals = new Vector3[vertices.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;

            var mesh = new Mesh
            {
                name = "SnowPlane",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, // > 65k verts
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.normals = normals;
            // The vertex shader only ever displaces upward from the plane, but the mesh
            // rides with the player, so bounds must stay generous or HDRP culls it while
            // it is still on screen.
            mesh.bounds = new Bounds(Vector3.zero,
                new Vector3(Levels[^1].half * 2f, 8f, Levels[^1].half * 2f));

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MESH_PATH);
            if (existing != null) AssetDatabase.DeleteAsset(MESH_PATH);
            AssetDatabase.CreateAsset(mesh, MESH_PATH);
            return mesh;
        }

        /// <summary>
        /// Square annulus from <paramref name="innerHalf"/> out to <paramref name="half"/>
        /// at the given cell size. innerHalf of 0 gives a solid square.
        /// </summary>
        private static void AddRing(System.Collections.Generic.List<Vector3> vertices,
            System.Collections.Generic.List<int> triangles, float half, float step, float innerHalf)
        {
            int cells = Mathf.RoundToInt(half * 2f / step);
            int side = cells + 1;
            int baseIndex = vertices.Count;

            // The full grid is emitted even though the hole's interior vertices go unused -
            // a few hundred stray vertices cost far less than the index remapping needed to
            // omit them, and they carry no triangles so the GPU never transforms them.
            for (int z = 0; z < side; z++)
                for (int x = 0; x < side; x++)
                    vertices.Add(new Vector3(x * step - half, 0f, z * step - half));

            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    // Cell centre decides membership, so a cell is never half in the hole.
                    float cx = Mathf.Abs((x + 0.5f) * step - half);
                    float cz = Mathf.Abs((z + 0.5f) * step - half);
                    if (innerHalf > 0f && cx < innerHalf && cz < innerHalf) continue;

                    int i = baseIndex + z * side + x;
                    triangles.Add(i);
                    triangles.Add(i + side);
                    triangles.Add(i + 1);
                    triangles.Add(i + 1);
                    triangles.Add(i + side);
                    triangles.Add(i + side + 1);
                }
        }

        /// <summary>
        /// Vertical curtain hanging down from the square boundary at <paramref name="half"/>,
        /// sampled at <paramref name="step"/>. Bridges the LOD seam crack.
        /// </summary>
        private static void AddSkirt(System.Collections.Generic.List<Vector3> vertices,
            System.Collections.Generic.List<int> triangles, float half, float step)
        {
            int cells = Mathf.RoundToInt(half * 2f / step);

            // Walk the square boundary once per edge. Winding is not worth fighting here:
            // both faces are emitted so the curtain is visible from inside and out.
            for (int edge = 0; edge < 4; edge++)
                for (int i = 0; i < cells; i++)
                {
                    Vector3 a = BoundaryPoint(edge, i * step - half, half);
                    Vector3 b = BoundaryPoint(edge, (i + 1) * step - half, half);
                    Vector3 aDown = a + Vector3.down * SKIRT_DROP;
                    Vector3 bDown = b + Vector3.down * SKIRT_DROP;

                    int v = vertices.Count;
                    vertices.Add(a); vertices.Add(b); vertices.Add(aDown); vertices.Add(bDown);

                    triangles.Add(v); triangles.Add(v + 2); triangles.Add(v + 1);
                    triangles.Add(v + 1); triangles.Add(v + 2); triangles.Add(v + 3);
                    triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
                    triangles.Add(v + 1); triangles.Add(v + 3); triangles.Add(v + 2);
                }
        }

        private static Vector3 BoundaryPoint(int edge, float t, float half) => edge switch
        {
            0 => new Vector3(t, 0f, -half),
            1 => new Vector3(t, 0f, half),
            2 => new Vector3(-half, 0f, t),
            _ => new Vector3(half, 0f, t),
        };

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
            // Below this remaining thickness the fragment is clipped and the street shows.
            mat.SetFloat("_MinThickness", 0.012f);
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

            // Own layer so reflection probes can cull it. It is the single heaviest mesh
            // in the scene and contributes almost nothing to a cubemap reflection, but a
            // probe that captures it re-renders all of it once per face.
            int layer = LayerMask.NameToLayer(SNOW_LAYER);
            if (layer >= 0) go.layer = layer;
            else Debug.LogWarning($"[SnowGroundBuilder] Layer '{SNOW_LAYER}' missing; " +
                                  "reflection probes will capture the snow mesh.");

            // The visual mesh rides with the player (its dense middle is only worth having
            // under the camera), but collision must NOT move - it is one box spanning the
            // whole region. So they are split: root holds the collider and stays put, the
            // child holds the mesh and follows.
            foreach (var stale in new[] { typeof(MeshFilter), typeof(MeshRenderer) })
                if (go.TryGetComponent(stale, out var c)) Object.DestroyImmediate(c);

            var meshChild = go.transform.Find(MESH_CHILD_NAME);
            if (meshChild == null)
            {
                var child = new GameObject(MESH_CHILD_NAME);
                child.transform.SetParent(go.transform, false);
                meshChild = child.transform;
            }
            meshChild.localPosition = Vector3.zero;
            var meshGo = meshChild.gameObject;
            if (layer >= 0) meshGo.layer = layer;

            // MeshFilter must exist before MeshRenderer, or the renderer throws on access.
            if (!meshGo.TryGetComponent<MeshFilter>(out var filter)) filter = meshGo.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            if (!meshGo.TryGetComponent<MeshRenderer>(out var renderer)) renderer = meshGo.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (!meshGo.TryGetComponent<SnowGroundFollow>(out var follow))
                follow = meshGo.AddComponent<SnowGroundFollow>();
            var fso = new SerializedObject(follow);
            fso.FindProperty("snapStep").floatValue = SNAP_STEP;
            fso.ApplyModifiedPropertiesWithoutUndo();

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
