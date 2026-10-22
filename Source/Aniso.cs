using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Anisotropic filtering (Graphics tab): sharper ground where it is seen at a slant (low pitch, perspective,
    // first person). Waylith builds each sampler from the texture's anisoLevel (clamped to the GPU's limit); the
    // engine's global QualitySettings.anisotropicFiltering does nothing, so the level is set per texture: on the
    // terrain textures (vanilla and other mods' terrain defs) and on IsoRim's own textures (ground, walls, roofs,
    // models). Data textures (IsoCellMap, glow map, LUTs) and Point-filtered (pixel-art) textures are left alone.
    // Waylith keeps each material's descriptor set, sampler included, until the material changes, so a new level
    // shows only on materials drawn after it is set. It is set at start-up (terrain here, IsoRim's own textures as
    // they are made, see Own); when the option changes, each affected material points at a 1x1 dummy texture for
    // one frame and then gets its texture back, which makes the engine build new sets.
    [StaticConstructorOnStartup]
    public static class Aniso
    {
        public static readonly int[] Levels = { 0, 2, 4, 8, 16 };   // the option's steps; 0 = textures as they come
        private const int OwnLevel = 4;                              // IsoRim's own textures without the option

        private static int applied;
        private static readonly Dictionary<Texture, int> terrain = new Dictionary<Texture, int>();   // texture -> its own level
        private static readonly List<System.WeakReference<Texture>> own = new List<System.WeakReference<Texture>>();
        private static readonly List<System.WeakReference<Material>> materials = new List<System.WeakReference<Material>>();
        private static readonly List<KeyValuePair<Material, Texture>> swapped = new List<KeyValuePair<Material, Texture>>();
        private static Texture2D dummy;

        // The level the option asks for (0 = off).
        public static int Level
        {
            get { return IsoRimMod.Settings != null ? Math.Max(0, IsoRimMod.Settings.aniso) : 0; }
        }

        static Aniso()
        {
            try
            {
                int point = 0;
                foreach (TerrainDef def in DefDatabase<TerrainDef>.AllDefsListForReading)
                {
                    Texture t = TextureOf(def);
                    if (t != null && t.filterMode == FilterMode.Point)
                    {
                        point++;
                    }
                    else if (t != null && !terrain.ContainsKey(t))
                    {
                        terrain[t] = t.anisoLevel;
                    }
                }
                applied = Level;
                SetLevels(applied);   // before the first draw: no swap needed
                if (Probe.On)
                {
                    Log.Message("[IsoRim probe] aniso: level " + applied + " on " + terrain.Count + " terrain textures, " + point + " point-filtered left alone");
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Setting the texture filtering failed: " + e, 93611980);
            }
        }

        // One of IsoRim's own textures, as it is made (R3DTextures, wall tops, aged copies).
        public static void Own(Texture tex)
        {
            if (tex == null)
            {
                return;
            }
            tex.anisoLevel = Math.Max(OwnLevel, applied);
            own.Add(new System.WeakReference<Texture>(tex));
        }

        // One of IsoRim's own materials (they may show one of its textures): swapped too when the option changes.
        public static void Track(Material m)
        {
            if (m == null)
            {
                return;
            }
            if (materials.Count >= 256 && (materials.Count & (materials.Count - 1)) == 0)
            {
                materials.RemoveAll(w => !w.TryGetTarget(out Material x) || x == null);
            }
            materials.Add(new System.WeakReference<Material>(m));
        }

        // The texture a material really shows (not the dummy of a swap in progress): for Ground, which remembers it.
        public static Texture RealTexture(Material m)
        {
            Texture t = m.mainTexture;
            if (dummy != null && t == dummy)
            {
                foreach (KeyValuePair<Material, Texture> p in swapped)
                {
                    if (p.Key == m)
                    {
                        return p.Value;
                    }
                }
            }
            return t;
        }

        // Every frame (View3D): applies a changed option.
        public static void Sync()
        {
            if (swapped.Count > 0)
            {
                // Second frame of a change: the materials get their textures back (unless changed meanwhile).
                foreach (KeyValuePair<Material, Texture> p in swapped)
                {
                    if (p.Key != null && p.Key.mainTexture == dummy)
                    {
                        p.Key.mainTexture = p.Value;
                    }
                }
                swapped.Clear();
            }
            int want = Level;
            if (want == applied)
            {
                return;
            }
            applied = want;
            try
            {
                SetLevels(want);
                SwapForOneFrame();
                if (Probe.On)
                {
                    Log.Message("[IsoRim probe] aniso: level " + want + ", " + swapped.Count + " materials swapped for one frame");
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Changing the texture filtering failed: " + e, 93611981);
            }
        }

        private static void SetLevels(int level)
        {
            foreach (KeyValuePair<Texture, int> p in terrain)
            {
                if (p.Key != null)
                {
                    p.Key.anisoLevel = level > 1 ? level : p.Value;
                }
            }
            own.RemoveAll(w => !w.TryGetTarget(out Texture x) || x == null);
            foreach (System.WeakReference<Texture> w in own)
            {
                if (w.TryGetTarget(out Texture t))
                {
                    t.anisoLevel = Math.Max(OwnLevel, level);
                }
            }
        }

        // First frame of a change: every material showing an affected texture points at the dummy instead.
        private static void SwapForOneFrame()
        {
            if (dummy == null)
            {
                dummy = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                dummy.name = "IsoRim_AnisoSwap";
                dummy.SetPixel(0, 0, Color.white);
                dummy.Apply(false);
            }
            HashSet<Texture> affected = new HashSet<Texture>(terrain.Keys);
            foreach (System.WeakReference<Texture> w in own)
            {
                if (w.TryGetTarget(out Texture t))
                {
                    affected.Add(t);
                }
            }
            HashSet<Material> seen = new HashSet<Material>();
            foreach (TerrainDef def in DefDatabase<TerrainDef>.AllDefsListForReading)
            {
                Swap(MaterialOf(def), affected, seen);
            }
            materials.RemoveAll(w => !w.TryGetTarget(out Material x) || x == null);
            foreach (System.WeakReference<Material> w in materials)
            {
                if (w.TryGetTarget(out Material m))
                {
                    Swap(m, affected, seen);
                }
            }
        }

        private static void Swap(Material m, HashSet<Texture> affected, HashSet<Material> seen)
        {
            if (m == null || !seen.Add(m) || !m.HasProperty("_MainTex"))
            {
                return;
            }
            Texture t = m.mainTexture;
            if (t != null && t != dummy && affected.Contains(t))
            {
                swapped.Add(new KeyValuePair<Material, Texture>(m, t));
                m.mainTexture = dummy;
            }
        }

        private static Material MaterialOf(TerrainDef def)
        {
            try
            {
                return def.DrawMatSingle;
            }
            catch
            {
                return null;
            }
        }

        private static Texture TextureOf(TerrainDef def)
        {
            Material m = MaterialOf(def);
            return m != null && m.HasProperty("_MainTex") ? m.mainTexture : null;
        }
    }
}
