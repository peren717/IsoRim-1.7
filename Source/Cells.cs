using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Per-cell data of a map that IsoRim's vertex shaders read (step 5 of the 1.7 port, design §14).
    //
    // One texel per cell, RGBA32, point-sampled (shaders use texelFetch on the integer cell):
    //   r, g = the top of what stands in the cell, in 1/4096 cells above the ground as a 16-bit number
    //       (the highest block, or the edifice's 3D height): the fog lid lies on it (IsoRimFog.vert.glsl);
    //   b, a = the shader cut-away (ShaderCut): b = how far the cell's block is cut down, a = tree thinned (1),
    //       roof open (2); and a = fogged in the game's fog grid (4, always): nothing drawn in a fogged cell rises
    //       above the fog lid (RoofClip: the fog clips as roofs do).
    // A second texture of the same size (_IsoRoofTex) holds the roof drawn over each cell, written by the roofs
    // layer as it builds (RoofClip.cs: things under a roof are clipped at its underside).
    // (A float texture would be simpler, but an RGBAFloat texture filled with SetPixels reads as 0 in
    // the shader on 1.7.4977.)
    // The game's glow lives in its own texture (IsoGlowMap): a corner grid sampled bilinear, copied
    // wholesale from the game's overlay mesh, where this one is per cell and exact.
    //
    // Cells are marked dirty when something that changes their top changes (buildings spawned or gone,
    // a block of walls rebuilt, which covers cut-away and settings changes, or a cell's fog: the game marks it
    // with its FogOfWar flag, FogDirty_Postfix); just before the map is
    // drawn the dirty cells are worked out again and, if any changed, the texture is uploaded once
    // (the engine has no partial upload). Owned per map, like the glow texture (design §11).
    [StaticConstructorOnStartup]
    public sealed class IsoCellMap
    {
        private static readonly Dictionary<Map, IsoCellMap> maps = new Dictionary<Map, IsoCellMap>();

        public readonly Map map;
        public readonly int width;
        public readonly int height;
        public Vector2 origin;        // where cell (0, 0) is in the world (0 today; per-map placement later)
        public float baseY;           // the map's ground height in cells (0 today; reserved for stacked maps)
        private readonly Color32[] cells;
        private readonly bool[] dirty;
        private readonly List<int> dirtyList = new List<int>();
        private bool all = true;      // everything is to be worked out (new, or after a full rebuild)
        private bool changed;
        private LiveTexture tex;

        // Heights are stored in steps of 1/4096 cell as a 16-bit number (red high byte, green low byte):
        // up to 16 cells, far below a pixel at any zoom.
        public const float HeightSteps = 4096f;
        private int uploads;

        private IsoCellMap(Map map)
        {
            this.map = map;
            width = map.Size.x;
            height = map.Size.z;
            cells = new Color32[width * height];
            dirty = new bool[width * height];
            cutTarget = new byte[width * height];
            fading = new bool[width * height];
            roofCells = new Color32[width * height];
        }

        // ---- the roof over each cell (RoofClip.cs): a second texture, written by the roofs layer as it builds

        private readonly Color32[] roofCells;
        private bool roofChanged = true;
        private LiveTexture roofTex;

        // The roof over a cell as the roofs layer draws it (RoofClip.Pack). Ignored outside the map.
        public void SetRoof(IntVec3 c, Color32 v)
        {
            if (c.x < 0 || c.z < 0 || c.x >= width || c.z >= height)
            {
                return;
            }
            int i = c.z * width + c.x;
            Color32 o = roofCells[i];
            if (o.r != v.r || o.g != v.g || o.b != v.b || o.a != v.a)
            {
                roofCells[i] = v;
                roofChanged = true;
            }
        }

        public Color32 RoofAt(IntVec3 c)
        {
            if (c.x < 0 || c.z < 0 || c.x >= width || c.z >= height)
            {
                return default(Color32);
            }
            return roofCells[c.z * width + c.x];
        }

        private void UploadRoofs()
        {
            if (!roofChanged && roofTex != null)
            {
                return;
            }
            if (roofTex == null)
            {
                roofTex = new LiveTexture(width, height, "IsoRim_Roofs", FilterMode.Point);
            }
            roofTex.Upload(roofCells);
            roofChanged = false;
        }

        // The cell data of a map, made on first use. Maps that are gone are dropped.
        public static IsoCellMap For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            IsoCellMap c;
            if (maps.TryGetValue(map, out c))
            {
                return c;
            }
            Prune();
            c = new IsoCellMap(map);
            maps[map] = c;
            return c;
        }

        // The cell data of a map if it has been made (null otherwise); never makes it.
        public static IsoCellMap Existing(Map map)
        {
            IsoCellMap c;
            return map != null && maps.TryGetValue(map, out c) ? c : null;
        }

        // The tallest top of any cell, as last sent to the GPU (culling: the fog lid is lifted onto it).
        public float MaxTop
        {
            get { return maxTop; }
        }

        private float maxTop = 16f;

        private static readonly List<Map> gone = new List<Map>();

        private static void Prune()
        {
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
            if (roofTex != null)
            {
                roofTex.Destroy();
                roofTex = null;
            }
        }

        // Marks cells to be worked out again before the next draw. Ignored for a map that has no
        // cell data yet (it is worked out in full when first used).
        public static void Dirty(Map map, CellRect rect)
        {
            IsoCellMap c;
            if (map == null || !maps.TryGetValue(map, out c) || c.all)
            {
                return;
            }
            rect = rect.ClipInsideRect(new CellRect(0, 0, c.width, c.height));
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    int i = z * c.width + x;
                    if (!c.dirty[i])
                    {
                        c.dirty[i] = true;
                        c.dirtyList.Add(i);
                    }
                }
            }
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<MapDrawer, Map> drawerMap = HarmonyLib.AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        // Postfix of MapDrawer.MapMeshDirty (via Patches.TreesDirty_Postfix): the game marks every cell whose fog
        // changes (FogGrid.Unfog, Refog, the flood unfog of a dug-into cave) with its FogOfWar flag; the cell's
        // fogged bit is worked out again before the next draw, so what is revealed shows at once.
        public static void FogDirty_Postfix(MapDrawer drawer, IntVec3 loc, ulong dirtyFlags)
        {
            if ((dirtyFlags & (ulong)RimWorld.MapMeshFlagDefOf.FogOfWar) == 0 || maps.Count == 0)
            {
                return;
            }
            Map map = drawerMap(drawer);
            if (map != null)
            {
                Dirty(map, CellRect.SingleCell(loc));
            }
        }

        // The whole map is being rebuilt: every cell is worked out again before the next draw.
        public static void Rebuilt(Map map)
        {
            IsoCellMap c;
            if (map != null && maps.TryGetValue(map, out c))
            {
                c.all = true;
            }
        }

        // The top of what stands in a cell, in cells above the ground: the highest block (as cut
        // down), or else the edifice's 3D height. The fog lid lies on it.
        public static float TopOf(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return 0f;
            }
            float h = WallBoxes.BlockHeightAt(map, c);
            Building b = c.GetEdifice(map);
            if (b != null && h <= 0f)
            {
                h = Forms.HeightOf(b) + Bridges.LiftOf(b);   // (standing on a bridge deck)
            }
            if (Bridges.Active && Bridges.DeckAt(map, c) != null)
            {
                h = Mathf.Max(h, Bridges.Deck);   // the deck itself
            }
            return h;
        }

        private void Work(int i)
        {
            IntVec3 c = new IntVec3(i % width, 0, i / width);
            bool shaderCut = ShaderCut.On;
            float top;
            byte cut = 0, bits = 0;
            if (shaderCut)
            {
                // (shader cut-away) the whole height; b = how far the block is cut, a = tree thinned (1), roof open (2).
                WallBoxes.FullHeights = true;
                try
                {
                    top = TopOf(map, c);
                }
                finally
                {
                    WallBoxes.FullHeights = false;
                }
                Building b = c.GetEdifice(map);
                if (b != null && WallBoxes.IsWallForm(Forms.For(b.def)) && WallBoxes.IsCut(map, c, b))
                {
                    cut = 255;
                }
                if (Cutaway.TreeThinned(map, c))
                {
                    bits |= 1;
                }
                if (Cutaway.RoofOpen(map, c))
                {
                    bits |= 2;
                }
            }
            else
            {
                top = TopOf(map, c);
            }
            if (map.fogGrid.IsFogged(i))
            {
                bits |= 4;   // fogged: whatever is drawn here above the fog lid is clipped (RoofClip, the fog)
            }
            int za = aboveRow != null ? aboveRow[c.z] : -1;
            if (za >= 0 && !map.terrainGrid.topGrid[za * width + c.x].dontRender)
            {
                bits |= 8;   // a floor right above (Floors.HasFloor of the cell above): its storey's lid is not drawn here while that floor shows
            }
            int v = Mathf.Clamp(Mathf.RoundToInt(top * HeightSteps), 0, 65535);
            byte hi = (byte)(v >> 8), lo = (byte)(v & 255);
            if (cells[i].r != hi || cells[i].g != lo)
            {
                cells[i].r = hi;
                cells[i].g = lo;
                changed = true;
            }
            if (cells[i].a != bits)
            {
                cells[i].a = bits;
                changed = true;
            }
            if (cutTarget[i] != cut || cells[i].b != cut)
            {
                cutTarget[i] = cut;
                if (ShaderCut.Fade <= 0f || all)
                {
                    if (cells[i].b != cut)
                    {
                        cells[i].b = cut;
                        changed = true;
                    }
                }
                else if (!fading[i])
                {
                    fading[i] = true;
                    fadingList.Add(i);
                }
            }
        }

        // (shader cut-away) cells on their way down or up (ShaderCut.Fade), one step a frame.
        private readonly byte[] cutTarget;
        private readonly bool[] fading;
        private readonly List<int> fadingList = new List<int>();

        private void Fade()
        {
            if (fadingList.Count == 0)
            {
                return;
            }
            float fade = ShaderCut.Fade;
            int step = fade <= 0f ? 255 : Mathf.Max(1, Mathf.RoundToInt(255f * Time.unscaledDeltaTime / fade));
            for (int k = fadingList.Count - 1; k >= 0; k--)
            {
                int i = fadingList[k];
                int b = cells[i].b;
                int t = cutTarget[i];
                b = b < t ? Mathf.Min(t, b + step) : Mathf.Max(t, b - step);
                cells[i].b = (byte)b;
                changed = true;
                if (b == t)
                {
                    fading[i] = false;
                    fadingList[k] = fadingList[fadingList.Count - 1];
                    fadingList.RemoveAt(fadingList.Count - 1);
                }
            }
        }

        // Works out the dirty cells and sends the texture to the GPU if anything changed, at most
        // once a frame.
        public void Update()
        {
            Stopwatch watch = uploads < 3 && ModLog.Diag ? Stopwatch.StartNew() : null;
            if (all)
            {
                all = false;
                BuildAboveRows();
                for (int i = 0; i < cells.Length; i++)
                {
                    Work(i);
                }
                for (int i = 0; i < dirtyList.Count; i++)
                {
                    dirty[dirtyList[i]] = false;
                }
                dirtyList.Clear();
                changed = true;
            }
            else if (dirtyList.Count > 0)
            {
                for (int i = 0; i < dirtyList.Count; i++)
                {
                    dirty[dirtyList[i]] = false;
                    Work(dirtyList[i]);
                }
                dirtyList.Clear();
            }
            Fade();
            if (RoofClip.TextureOn)
            {
                UploadRoofs();
            }
            if (!changed && tex != null)
            {
                return;
            }
            if (tex == null)
            {
                tex = new LiveTexture(width, height, "IsoRim_Cells", FilterMode.Point);
            }
            tex.Upload(cells);
            int top = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                top = Math.Max(top, (cells[i].r << 8) | cells[i].g);
            }
            maxTop = top / HeightSteps;
            changed = false;
            uploads++;
            if (watch != null)
            {
                ModLog.Note("[IsoRim] Cell texture " + width + "x" + height + " updated in " + watch.Elapsed.TotalMilliseconds.ToString("0.000") + " ms");
            }
        }

        // The texture to bind this frame (see LiveTexture.Use).
        public Texture2D Texture
        {
            get { return tex != null ? tex.Use() : null; }
        }

        public bool Uploaded
        {
            get { return tex != null && tex.Ready; }
        }

        // ---- per frame

        private static readonly int texId = Shader.PropertyToID("_IsoCellTex");
        private static readonly int mapId = Shader.PropertyToID("_IsoCellMap");
        private static readonly int levelId = Shader.PropertyToID("_IsoLevel");
        private static readonly int roofId = Shader.PropertyToID("_IsoRoof");
        private static readonly int roofTexId = Shader.PropertyToID("_IsoRoofTex");

        // Before the map's meshes are drawn: brings the shown map's cell texture up to date and hands
        // it to the shaders (the fog lid, the cut-away, the roof and fog clips).
        public static void BeginFrame(Map map)
        {
            bool roofClip = RoofClip.TextureOn;   // roofs or slabs clip
            Shader.SetGlobalVector(roofId, RoofClip.Globals);   // x = 0: nothing is clipped (bits: 1 roofs, 2 the fog, 4 slabs)
            if (!View3D.Active || map == null || !(View3D.FogLid || RoofClip.FogOn || ShaderCut.On || roofClip))
            {
                return;
            }
            IsoCellMap c = For(map);
            long t0 = Probe.On ? Stopwatch.GetTimestamp() : 0;
            c.Update();
            if (Probe.On)
            {
                Probe.AddCellTime((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            Shader.SetGlobalTexture(texId, c.Texture);
            if (roofClip && c.roofTex != null)
            {
                Shader.SetGlobalTexture(roofTexId, c.roofTex.Use());
            }
            Shader.SetGlobalVector(mapId, new Vector4(c.origin.x, c.origin.y, c.width, c.height));
            Shader.SetGlobalVector(levelId, new Vector4(c.baseY, WallBoxes.CutFraction, 0f, 0f));   // y: what is left of a cut wall
        }

        // ---- the fog lid

        public const string FogDef = "IsoRimFog";
        private static Material fog, fogCover;
        private static bool fogTried;
        private static readonly int lidId = Shader.PropertyToID("_IsoLid");

        // The material a block of the game's fog is drawn with in 3D: the game's fog material on the
        // IsoRimFog shader (the lid is lifted in its vertex stage). Null if the def is missing.
        // cover: the storey above the block's is shown, so fogged cells with a floor right above them get no lid (cell
        // bit 8; the floor above, and its own lid if fogged, shows there). False on maps without floors.
        public static Material FogFor(Material gameMat, bool cover = false)
        {
            if (!fogTried)
            {
                fogTried = true;
                Shader s = R3DMaterials.ShaderOf(FogDef);
                if (s != null && gameMat != null)
                {
                    // A fresh material on the lid shader with the game's properties copied in (a copy made
                    // with new Material(gameMat) of a material the game has not finished loading would take
                    // the game's shader back when it is resolved).
                    fog = new Material(s);
                    fog.CopyPropertiesFromMaterial(gameMat);
                    fog.renderQueue = gameMat.renderQueue;
                    fog.name = "IsoRim_FogLid";
                    fog.SetVector(lidId, Vector4.zero);
                    fogCover = new Material(s);
                    fogCover.CopyPropertiesFromMaterial(gameMat);
                    fogCover.renderQueue = gameMat.renderQueue;
                    fogCover.name = "IsoRim_FogLidCovered";
                    fogCover.SetVector(lidId, new Vector4(1f, 0f, 0f, 0f));
                }
            }
            if (fog != null && gameMat != null && fog.color != gameMat.color)
            {
                fog.color = gameMat.color;
                fogCover.color = gameMat.color;
            }
            return cover ? fogCover : fog;
        }

        // ---- the floor right above each cell (bit 8): per row, the row of the storey above (-1: none), from the layout

        private int[] aboveRow;

        public static bool CoverBits = true;   // harness A/B (probe key cellperf=): off, no cell gets bit 8 (the lid as before)

        // (harness) the whole cell map worked out again now, timed (ms).
        public static double RebuildNow(Map map)
        {
            IsoCellMap c = For(map);
            c.all = true;
            Stopwatch sw = Stopwatch.StartNew();
            c.Update();
            return sw.Elapsed.TotalMilliseconds;
        }

        private void BuildAboveRows()
        {
            if (!CoverBits)
            {
                aboveRow = null;
                return;
            }
            aboveRow = new int[height];
            for (int z = 0; z < height; z++)
            {
                aboveRow[z] = -1;
            }
            int rows;
            float[] hs;
            int[] shifts;
            int n = Floors.Bands(map, out rows, out hs, out shifts);
            for (int k = 0; k < n; k++)
            {
                for (int z = 0; z < rows; z++)
                {
                    int from = shifts[k] + z, to = shifts[k + 1] + z;
                    if (from >= 0 && from < height && to >= 0 && to < height)
                    {
                        aboveRow[from] = to;
                    }
                }
            }
        }
    }
}
