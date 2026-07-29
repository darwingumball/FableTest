using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Game.Editor
{
    /// <summary>
    /// Builds the menu's TMP font asset.
    ///
    /// The source TTF is copied INTO the project the first time and used from there
    /// afterwards, so this does not keep reaching outside the repo and the project stays
    /// buildable on another machine. Swapping the typeface later is a matter of replacing
    /// that one file - the generated asset path and every reference to it stay put.
    ///
    /// LICENSING: Franklin Gothic Medium ships with Windows and is NOT redistributable in a
    /// shipped game. It is here because it is on hand, reads cold and industrial, and is
    /// obviously not Arial. Replace <see cref="ProjectTtf"/> with a licensed or open face
    /// (Oswald, Archivo, Barlow Condensed all sit in the same register) before shipping.
    /// </summary>
    public static class MenuFont
    {
        private const string FontsFolder = "Assets/_Game/UI/Fonts";
        private const string ProjectTtf = FontsFolder + "/MenuFont.ttf";
        private const string AssetPath = FontsFolder + "/MenuFont SDF.asset";
        private const string SystemTtf = @"C:\Windows\Fonts\framd.ttf";
        private const string FallbackAsset =
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

        /// <summary>
        /// The menu typeface, generating it if it does not exist yet. Returns null only if
        /// no source font can be found at all, in which case callers should leave TMP on
        /// its default rather than blanking the UI.
        /// </summary>
        public static TMP_FontAsset Ensure()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetPath);
            if (existing != null) return existing;

            var source = EnsureSourceFont();
            if (source == null) return null;

            // Dynamic population: glyphs are rasterised into the atlas on demand, so the
            // menu does not need a character set declared up front. This is why the TTF has
            // to live in the project - a dynamic atlas needs the source face at runtime.
            var font = TMP_FontAsset.CreateFontAsset(
                source, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);
            font.name = Path.GetFileNameWithoutExtension(AssetPath);

            AssetDatabase.CreateAsset(font, AssetPath);

            // The atlas texture and material are generated objects with no home of their
            // own; without this they are dropped on the next reimport and the font renders
            // as blank quads.
            if (font.atlasTextures != null && font.atlasTextures.Length > 0)
            {
                font.atlasTextures[0].name = "Atlas";
                AssetDatabase.AddObjectToAsset(font.atlasTextures[0], font);
            }
            if (font.material != null)
            {
                font.material.name = "Material";
                AssetDatabase.AddObjectToAsset(font.material, font);
            }

            // Anything this face is missing (symbols, accents) falls through to the stock
            // TMP font rather than rendering as a missing-glyph box.
            var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FallbackAsset);
            if (fallback != null)
                font.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };

            EditorUtility.SetDirty(font);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[MenuFont] Generated {AssetPath}.");
            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetPath);
        }

        private static Font EnsureSourceFont()
        {
            var inProject = AssetDatabase.LoadAssetAtPath<Font>(ProjectTtf);
            if (inProject != null) return inProject;

            if (!File.Exists(SystemTtf))
            {
                Debug.LogWarning($"[MenuFont] No font at {ProjectTtf} and none at {SystemTtf}. " +
                                 "Drop a .ttf at the first path and re-run; the menu will use " +
                                 "the TMP default until then.");
                return null;
            }

            TestMaterials.EnsureFolder(FontsFolder);
            File.Copy(SystemTtf, Path.Combine(Directory.GetCurrentDirectory(), ProjectTtf), true);
            AssetDatabase.ImportAsset(ProjectTtf, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<Font>(ProjectTtf);
        }
    }
}
