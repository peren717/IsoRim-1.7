using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Night, lamp and indoor light on the 3D shapes (step 2b of the 1.7 port).
    //
    // The game lights the map with one flat overlay: every cell corner carries the light reaching it
    // (glow grid colour, walls left out of the average) and how much roof covers it, and the overlay
    // multiplies the ground by sky colour * (1 - cover) + glow. IsoRim used to pull that overlay
    // toward the camera, so upright faces took the light of whatever ground lay behind them on
    // screen, and a lit room behind a wall lit its outer face too.
    //
    // Now each map keeps a texture with one texel per cell corner holding exactly the game's corner
    // colours (copied from its overlay mesh whenever a block of it is rebuilt), and every face is
    // lit from its own cell with the game's formula (_GlowMode 1) by a second "light" pass just
    // before the overlay (queue 3099): the same meshes again, depth-tested against themselves,
    // multiplying the colour already there (as the game's overlay does) and setting stencil bit
    // 32, once per pixel. The pulled overlay skips those pixels, so nothing is lit twice, and
    // anything see-through drawn over a face before then (smoke and other motes) is darkened with
    // it, as the overlay darkens it over the ground. Roofs take the sky colour only (_GlowMode 2,
    // in their own pass); ghosts are drawn after the overlay and light themselves from their own
    // cell (_GlowMode 3). Everything else (the ground, the game's upright sprites) keeps the
    // overlay as before.
    public sealed class IsoGlowMap
    {
        private static readonly Dictionary<Map, IsoGlowMap> maps = new Dictionary<Map, IsoGlowMap>();

        public readonly Map map;
        public readonly int width;    // corners across: map width + 1
        public readonly int height;   // corners down: map height + 1
        public Vector2 origin;        // where corner (0, 0) is in the world (0 today; per-map placement later)
        public float baseY;           // the map's ground height (0 today; reserved for stacked maps)
        private readonly Color32[] corners;
        private LiveTexture tex;
        private bool dirty;
        private int uploads;

        private IsoGlowMap(Map map)
        {
            this.map = map;
            width = map.Size.x + 1;
            height = map.Size.z + 1;
            corners = new Color32[width * height];
        }

        // The glow data of a map, made on first use. Maps that are gone are dropped (with their texture).
        public static IsoGlowMap For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            IsoGlowMap g;
            if (maps.TryGetValue(map, out g))
            {
                return g;
            }
            Prune();
            g = new IsoGlowMap(map);
            maps[map] = g;
            return g;
        }

        private static readonly List<Map> gone = new List<Map>();

        private static void Prune()
        {
            if (maps.Count == 0)
            {
                return;
            }
            List<Map> live = Find.Maps;
            foreach (Map m in maps.Keys)
            {
                if (live == null || !live.Contains(m))
                {
                    gone.Add(m);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                maps[gone[i]].Dispose();
                maps.Remove(gone[i]);
            }
            gone.Clear();
        }

        private void Dispose()
        {
            if (tex != null)
            {
                tex.Destroy();
                tex = null;
            }
        }

        // Copies the corner colours of one rebuilt block of the game's lighting overlay. The mesh
        // starts with one vertex per corner of the (clipped) block, row by row from the south-west.
        public void CopySection(CellRect rect, Color32[] meshColors)
        {
            if (meshColors == null)
            {
                return;
            }
            int w = rect.Width + 1;
            int h = rect.Height + 1;
            if (rect.Width <= 0 || rect.Height <= 0 || meshColors.Length < w * h)
            {
                return;
            }
            for (int j = 0; j < h; j++)
            {
                int z = rect.minZ + j;
                if (z < 0 || z >= height)
                {
                    continue;
                }
                int src = j * w;
                int dst = z * width + rect.minX;
                for (int i = 0; i < w; i++)
                {
                    int x = rect.minX + i;
                    if (x >= 0 && x < width)
                    {
                        corners[dst + i] = meshColors[src + i];
                    }
                }
            }
            dirty = true;
        }

        // Sends changed corners to the GPU: at most once a frame, only when a block was rebuilt
        // (lights switched, walls or roofs built, fire). The engine has no partial upload.
        public void Upload()
        {
            if (!dirty && tex != null)
            {
                return;
            }
            Stopwatch watch = uploads < 3 && ModLog.Diag ? Stopwatch.StartNew() : null;
            if (tex == null)
            {
                tex = new LiveTexture(width, height, "IsoRim_Glow", FilterMode.Bilinear);
            }
            tex.Upload(corners);
            dirty = false;
            uploads++;
            if (watch != null)
            {
                ModLog.Note("[IsoRim] Glow texture " + width + "x" + height + " uploaded in " + watch.Elapsed.TotalMilliseconds.ToString("0.000") + " ms");
            }
        }

        // The texture to bind this frame (see LiveTexture.Use).
        public Texture2D Texture
        {
            get { return tex != null ? tex.Use() : null; }
        }
    }

    [StaticConstructorOnStartup]
    public static class IsoGlow
    {
        private static readonly int skyId = Shader.PropertyToID("_IsoSky");
        private static readonly int mapId = Shader.PropertyToID("_IsoGlowMap");
        private static readonly int texId = Shader.PropertyToID("_IsoGlowTex");
        private static readonly int floorsId = Shader.PropertyToID("_IsoFloorGlow");   // stacked floors: Floors.GlowFloors
        private static readonly int glowModeId = Shader.PropertyToID("_GlowMode");
        private static readonly int colorId = Shader.PropertyToID("_Color");
        private static readonly int mainTexStId = Shader.PropertyToID("_MainTex_ST");
        private static readonly int markPassId = Shader.PropertyToID("_MarkPass");

        public const int None = 0;      // _GlowMode: lit by the game's pulled overlay
        public const int OwnCell = 1;   // the face's own cell from the glow texture, applied by the light pass (marked)
        public const int SkyOnly = 2;   // sky colour only (roofs), in the material's own pass, not marked
        public const int OwnCellUnmarked = 3;   // own cell, in the material's own pass, not marked (ghosts, drawn after the overlay)

        public const string MarkDef = "IsoRimLitMark";
        public const string MarkTexDef = "IsoRimLitMarkTex";
        public const string MarkCardDef = "IsoRimLitMarkCard";
        public const string OverlayDef = "IsoRimLightOverlay";

        private static Texture2D neutral;
        private static bool neutralBound;

        // The mark material of every own-cell-lit material.
        private static readonly Dictionary<Material, Material> marks = new Dictionary<Material, Material>();

        // Makes a material take its night and indoor light per pixel (see the class comment).
        public static void Use(Material m, int mode)
        {
            if (m == null)
            {
                return;
            }
            m.SetFloat(glowModeId, mode);
            if (mode != OwnCell || marks.ContainsKey(m))
            {
                return;
            }
            bool textured = m.mainTexture != null || m.shader == R3DMaterials.ShaderOf(R3DMaterials.SolidTexDef);
            Shader s = R3DMaterials.ShaderOf(textured ? MarkTexDef : MarkDef);
            if (s == null)
            {
                // Without the mark the overlay would light these faces a second time.
                m.SetFloat(glowModeId, None);
                return;
            }
            Material mark = new Material(s);
            mark.name = m.name + "_Mark";
            mark.SetFloat(markPassId, 1f);
            marks[m] = mark;
            SyncMark(m, mark);
        }

        // A plant card (UprightCards) takes the light of its own cell too, by a light pass on the card's own vertex
        // stage. Without the def the card keeps the pulled overlay, as before.
        public static void UseCard(Material card)
        {
            if (card == null || marks.ContainsKey(card))
            {
                return;
            }
            Shader s = R3DMaterials.ShaderOf(MarkCardDef);
            if (s == null)
            {
                return;
            }
            Material mark = new Material(s);
            mark.CopyPropertiesFromMaterial(card);
            mark.name = card.name + "_Mark";
            mark.renderQueue = 3099;
            marks[card] = mark;
            WindManager.Notify_PlantMaterialCreated(mark);   // sways with the card
        }

        private static void SyncMark(Material m, Material mark)
        {
            mark.mainTexture = m.mainTexture;
            mark.SetVector(mainTexStId, m.GetVector(mainTexStId));
            mark.SetColor(colorId, m.color);
        }

        // The mark material for a material drawn now, or null if it needs none. Texture and colour
        // are set once the material is filled in, so they are copied again if they changed.
        public static Material MarkFor(Material m)
        {
            Material mark;
            if (m == null || !marks.TryGetValue(m, out mark))
            {
                return null;
            }
            if (mark.mainTexture != m.mainTexture)
            {
                SyncMark(m, mark);
            }
            return mark;
        }

        public static bool Marked(Material m)
        {
            return m != null && marks.ContainsKey(m);
        }

        // Draws an IsoRim mesh and, for own-cell-lit materials, its stencil mark.
        // props: the moving model's floor (RoofClip.FloorBlock), or null.
        public static void DrawLit(Mesh mesh, Matrix4x4 matrix, Material mat, int layer, MaterialPropertyBlock props = null)
        {
            if (ShipCapture.Recording && ShipCapture.Note(mesh, matrix, mat, props))
            {
                return;   // a gravship's moving parts (ShipFlight): noted, and hidden while a landing ship waits under the frozen ground
            }
            Graphics.DrawMesh(mesh, matrix, mat, layer, null, 0, props);
            Material mark = View3D.Ready ? MarkFor(mat) : null;
            if (mark != null)
            {
                Graphics.DrawMesh(mesh, matrix, mark, layer, null, 0, props);
            }
        }

        // The marks for the section layers' own-cell-lit sub-meshes (drawn by the game at identity).
        public static void DrawLayerMarks(SectionLayer layer)
        {
            DrawLayerMarks(layer, Matrix4x4.identity);
        }

        // The same with the matrix the layer is drawn with (a block of an upper floor, Floors).
        public static void DrawLayerMarks(SectionLayer layer, Matrix4x4 matrix)
        {
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                if (sm.finalized && !sm.disabled)
                {
                    Material mark = MarkFor(sm.material);
                    if (mark != null)
                    {
                        Graphics.DrawMesh(sm.mesh, matrix, mark, sm.renderLayer);
                    }
                }
            }
        }

        // ---- the pulled overlay, skipping marked pixels

        private static Material overlay;
        private static bool overlayTried;

        // The overlay material to draw a block of the game's lighting overlay with (pulled in front
        // of everything), or the game's own one if the stencil def is missing.
        public static Material OverlayFor(Material gameMat)
        {
            if (!overlayTried)
            {
                overlayTried = true;
                Shader s = R3DMaterials.ShaderOf(OverlayDef);
                if (s != null)
                {
                    overlay = new Material(s);
                    overlay.name = "IsoRim_LightOverlay";
                }
            }
            if (overlay == null)
            {
                return gameMat;
            }
            overlay.color = gameMat.color;
            return overlay;
        }

        // ---- per frame

        // The sky light colour of a map, as the game's overlay uses it.
        public static Color SkyFor(Map map)
        {
            if (map == null)
            {
                return Color.white;
            }
            if (map == Find.CurrentMap)
            {
                return MatBases.LightOverlay.color;
            }
            if (map.Biome != null && map.Biome.disableSkyLighting)
            {
                return Color.white;
            }
            return map.skyManager.CurSky.colors.sky;
        }

        // Before the map's meshes are drawn (after the game rebuilt the dirty blocks of its own
        // overlay this frame): uploads changed corners and hands texture, placement and sky to the
        // shaders. With the lighting fix off, faces get neutral light (as before: no darkness).
        public static void BeginFrame(Map map)
        {
            if (!View3D.Active || map == null)
            {
                return;
            }
            if (!IsoRimMod.Settings.lightingFix)
            {
                BindNeutral();
                // The floors still go over: the roof and slab clips read them too (the 1 x 1 neutral texture gives the
                // same light wherever the floors' rows point).
                Shader.SetGlobalVector(floorsId, Floors.GlowFloors());
                return;
            }
            IsoGlowMap g = IsoGlowMap.For(map);
            g.Upload();
            neutralBound = false;
            Shader.SetGlobalTexture(texId, g.Texture);
            Shader.SetGlobalVector(mapId, new Vector4(g.origin.x, g.origin.y, 1f / g.width, 1f / g.height));
            Color sky = SkyFor(map);
            Shader.SetGlobalVector(skyId, new Vector4(sky.r, sky.g, sky.b, 1f));
            Shader.SetGlobalVector(floorsId, Floors.GlowFloors());
        }

        // No glow, no roof cover, white sky: light 1 (also the state before the first frame, so
        // faces are never black for want of a sky colour).
        public static void BindNeutral()
        {
            if (neutralBound)
            {
                return;
            }
            neutralBound = true;
            if (neutral == null)
            {
                neutral = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                neutral.name = "IsoRim_GlowNeutral";
                neutral.SetPixels32(new[] { new Color32(0, 0, 0, 0) });
                neutral.Apply(false);
            }
            Shader.SetGlobalTexture(texId, neutral);
            Shader.SetGlobalVector(mapId, new Vector4(0f, 0f, 1f, 1f));
            Shader.SetGlobalVector(skyId, new Vector4(1f, 1f, 1f, 1f));
            Shader.SetGlobalVector(floorsId, Vector4.zero);
        }

        // ---- the copy from the game's overlay

        // Postfix of SectionLayer_LightingOverlay.GenerateLightingOverlay(map, subMesh, rect, ref
        // firstCenterInd, centered, filter): only for the map's own blocks (not centred, unfiltered:
        // the gravship bake passes those).
        public static void Generated(Map map, LayerSubMesh subMesh, CellRect rect, bool centered, Predicate<int> filter)
        {
            if (centered || filter != null || map == null || subMesh == null || subMesh.mesh == null)
            {
                return;
            }
            rect = rect.ClipInsideMap(map);
            IsoGlowMap.For(map).CopySection(rect, subMesh.mesh.colors32);
        }

        // Dev-mode measurement for a big map (harness runs, -isorim3d): full uploads of a 251 x 251 texture.
        public static void Benchmark()
        {
            const int n = 251;
            Color32[] px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32((byte)i, (byte)(i >> 3), 0, 100);
            }
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
            double first = 0, sum = 0;
            for (int k = 0; k < 6; k++)
            {
                Stopwatch w = Stopwatch.StartNew();
                t.SetPixels32(px);
                t.Apply(false);
                double ms = w.Elapsed.TotalMilliseconds;
                if (k == 0)
                {
                    first = ms;
                }
                else
                {
                    sum += ms;
                }
            }
            UnityEngine.Object.Destroy(t);
            Log.Message("[IsoRim] Glow upload benchmark 251x251: first " + first.ToString("0.000") + " ms, then " + (sum / 5).ToString("0.000") + " ms each");
        }
    }
}
