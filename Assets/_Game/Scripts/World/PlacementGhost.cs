using System.Collections.Generic;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// The green/red preview of where cargo is about to land.
    ///
    /// Entirely local and entirely cosmetic - one ghost per machine, never replicated. Two
    /// players lining up the same crate each see their own preview, and neither sees the
    /// other's, which is correct: a ghost is a statement about what YOUR release button is
    /// about to do.
    ///
    /// It mirrors the real object's meshes rather than drawing a bounding box, so a barrel
    /// previews as a barrel. Colliders and scripts are deliberately not copied: a ghost that
    /// could be walked into, or that reported itself to the very overlap test deciding
    /// whether the placement is legal, would be its own worst enemy.
    ///
    /// Callers just keep calling <see cref="Show"/>. Going quiet for a frame hides it, so no
    /// one has to remember to clean up on every path out of a carry.
    /// </summary>
    public class PlacementGhost : MonoBehaviour
    {
        private const string ValidMaterialPath = "Placement/Ghost_Valid";
        private const string BlockedMaterialPath = "Placement/Ghost_Blocked";

        private static PlacementGhost _instance;

        private Material _valid, _blocked;
        private bool _materialsLoaded;

        private int _sourceId;
        private readonly List<MeshRenderer> _renderers = new();
        private bool _showingValid = true;
        private int _lastShownFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        public static PlacementGhost Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("~PlacementGhost") { hideFlags = HideFlags.HideAndDontSave };
                _instance = go.AddComponent<PlacementGhost>();
                DontDestroyOnLoad(go);
                return _instance;
            }
        }

        /// <summary>Previews <paramref name="source"/> at a pose. Call every frame while carrying.</summary>
        public static void Show(GameObject source, Vector3 position, Quaternion rotation, bool valid)
        {
            if (source == null) return;
            Instance.ShowInternal(source, position, rotation, valid);
        }

        public static void Clear()
        {
            if (_instance != null) _instance.HideInternal();
        }

        private void ShowInternal(GameObject source, Vector3 position, Quaternion rotation, bool valid)
        {
            EnsureMaterials();
            if (_sourceId != source.GetInstanceID()) Rebuild(source);

            transform.SetPositionAndRotation(position, rotation);

            if (valid != _showingValid)
            {
                _showingValid = valid;
                var material = valid ? _valid : _blocked;
                foreach (var renderer in _renderers)
                    if (renderer != null) renderer.sharedMaterial = material;
            }

            SetVisible(true);
            _lastShownFrame = Time.frameCount;
        }

        private void LateUpdate()
        {
            // One frame of grace, because Show may legitimately run in Update or LateUpdate
            // depending on the carrier.
            if (_lastShownFrame >= 0 && Time.frameCount - _lastShownFrame > 1) HideInternal();
        }

        private void HideInternal()
        {
            SetVisible(false);
            _lastShownFrame = -1;
        }

        private void SetVisible(bool visible)
        {
            foreach (var renderer in _renderers)
                if (renderer != null) renderer.enabled = visible;
        }

        private void Rebuild(GameObject source)
        {
            foreach (var renderer in _renderers)
                if (renderer != null) Destroy(renderer.gameObject);
            _renderers.Clear();

            _sourceId = source.GetInstanceID();

            // Rotation and translation only. Scale is left out so the whole chain of scales
            // between the source root and each mesh comes through in the relative matrix
            // below, and the ghost ends up exactly the size of the thing it is previewing
            // however deeply nested and however oddly scaled that is.
            Matrix4x4 sourceFrame = Matrix4x4.TRS(source.transform.position,
                source.transform.rotation, Vector3.one).inverse;

            var material = _showingValid ? _valid : _blocked;

            foreach (var filter in source.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                var sourceRenderer = filter.GetComponent<MeshRenderer>();
                if (sourceRenderer == null || !sourceRenderer.enabled) continue;

                Matrix4x4 relative = sourceFrame * filter.transform.localToWorldMatrix;

                var go = new GameObject("GhostMesh") { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(transform, false);
                go.transform.localPosition = relative.GetColumn(3);
                go.transform.localRotation = relative.rotation;
                go.transform.localScale = relative.lossyScale;

                go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                // A preview that cast shadows would put a hard shadow on the deck for cargo
                // that is not there yet.
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = false;

                _renderers.Add(renderer);
            }
        }

        private void EnsureMaterials()
        {
            if (_materialsLoaded) return;
            _materialsLoaded = true;

            _valid = Resources.Load<Material>(ValidMaterialPath);
            _blocked = Resources.Load<Material>(BlockedMaterialPath);
            if (_valid == null || _blocked == null)
                Debug.LogWarning("[PlacementGhost] Missing ghost materials under " +
                                 "Resources/Placement - run Game/Setup/Build Cargo.");
        }
    }
}
