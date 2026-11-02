using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The world shadow height map of the screen-space shadows (SunShadowsSS): per ground cell the solid vertical spans of
    // the big blocky casters standing over it, in drawn height (true cells above the ground): the block in the ground
    // cell (walls, natural rock, any edifice the walls layer bakes as a block: BuiltHeightAt, never cut down by the
    // cut-away), and for every ProjectZ floor, shown or hidden, its slab (SlabDepth under the floor's height)
    // and the block standing on it. The spans are merged; the lowest two are kept, anything above them joins the second
    // (a 3+ storey column of slabs between walls reads as solid from the second floor up: shadows only).
    // The march tests its world position against this besides the screen depth, so hidden floors and walls, rock and
    // buildings off screen still cast. Small things (pawns, furniture, trees) stay depth-only.
    //
    // One texel per ground cell, RGBA32 (LiveTexture), point-sampled: r, g = bottom and top of span 1, b, a = of span 2,
    // in 1/8 cell (0..31.9 cells); bottom rounded up, top down (a receiver on a wall top never shades itself). An empty
    // span is 255, 0. Cells are marked when IsoCellMap marks them (blocks rebuilt, buildings spawned or gone, a floor's
    // cells changed, rows of upper floors moved down to their ground cell) and worked out again before the next draw;
    // the whole map after a rebuild. Owned per map.
    //
    // Roofs (the game's RoofDefs; a ProjectZ floor above is its slab, above): every cell the roofs layer would put a lid
    // over (SectionLayer_IsoRimRoofs.LidKind, as if nothing were cut away: open, cut-away and hidden roofs still cast, so a
    // room keeps its shade whatever the view shows) gets a span at the lid's drawn height over the floor the cell is on (the
    // wall tops: View3D.WallHeight over the floor's height): a built roof from the wall tops up to its slope over the
    // cell's middle (Rise + Pitch per cell in from its edge, as the corner levels; the flat style: FlatRoofs.Depth), at
    // least RoofDepth thick; a ship roof a RoofDepth slab at the wall tops; a rock roof a RoofDepth slab under the rock tops,
    // a thick one (overhead mountain) half a wall height or more. The lid kinds of the map's cells (all rows) are kept and
    // marked unknown round every marked rectangle; a roof set or removed marks the cells its slopes reach
    // (SectionLayer_IsoRimRoofs.Reach, RoofChanged).
    public sealed class ShadowHeights
    {
        public const float Unit = 8f;   // steps per cell
        public const float SlabDepth = 0.5f;   // a slab counts this deep under its floor (drawn: Floors.SlabThickness), so the
                                               // march's 0.5-cell steps cannot pass through it; the room below shades its ceiling anyway
        public const float RoofDepth = SlabDepth;   // the thinnest a roof span is, for the same reason
        private static readonly Dictionary<Map, ShadowHeights> maps = new Dictionary<Map, ShadowHeights>();

        public static bool Roofs = true;            // harness A/B (probe key ssroof=0/1)
        public static int Builds, CellsWorked, Uploads;   // harness
        public static double LastFullMs;            // harness: the last full build
        public static double LastDirtyMs, MaxDirtyMs;   // harness: the last / longest update of marked cells
        public static int LastDirtyCells;           // harness

        public readonly Map map;
        private readonly int width, rows;
        private readonly Color32[] cells;
        private readonly bool[] dirty;
        private readonly List<int> dirtyList = new List<int>();
        private bool all = true, changed;
        private LiveTexture tex;
        private int groundRows;
        private float[] heights;
        private int[] shifts;
        private readonly sbyte[] kinds;   // lid kind per map cell (every row), -1 not known yet
        private readonly int mapRows;

        private ShadowHeights(Map map)
        {
            this.map = map;
            width = map.Size.x;
            rows = Floors.DrawnRows(map);
            mapRows = map.Size.z;
            cells = new Color32[width * rows];
            dirty = new bool[width * rows];
            kinds = new sbyte[width * mapRows];
        }

        public int Width { get { return width; } }
        public int Rows { get { return rows; } }

        public static ShadowHeights For(Map map)
        {
            ShadowHeights s;
            if (!maps.TryGetValue(map, out s) || s.rows != Floors.DrawnRows(map) || s.width != map.Size.x)
            {
                if (s != null)
                {
                    s.Destroy();
                }
                s = new ShadowHeights(map);
                maps[map] = s;
                Prune();
            }
            return s;
        }

        private static void Prune()
        {
            List<Map> gone = null;
            foreach (KeyValuePair<Map, ShadowHeights> kv in maps)
            {
                if (kv.Key == null || Find.Maps == null || !Find.Maps.Contains(kv.Key))
                {
                    (gone ?? (gone = new List<Map>())).Add(kv.Key);
                }
            }
            if (gone != null)
            {
                foreach (Map m in gone)
                {
                    maps[m].Destroy();
                    maps.Remove(m);
                }
            }
        }

        private void Destroy()
        {
            if (tex != null)
            {
                tex.Destroy();
                tex = null;
            }
        }

        // Harness (probe sshnew=1): the texture made again from the current cells on the next draw.
        public static void NewTexture(Map map)
        {
            ShadowHeights s;
            if (map != null && maps.TryGetValue(map, out s))
            {
                s.Destroy();
            }
        }

        // From IsoCellMap.Dirty: cells of the map (any row) whose blocks or floors may have changed.
        public static void Dirty(Map map, CellRect rect)
        {
            ShadowHeights s;
            if (map == null || !maps.TryGetValue(map, out s) || s.all)
            {
                return;
            }

            // lid kinds round the rectangle: a wall carries the edge of a roof beside it
            for (int z = Math.Max(rect.minZ - 1, 0); z <= Math.Min(rect.maxZ + 1, s.mapRows - 1); z++)
            {
                for (int x = Math.Max(rect.minX - 1, 0); x <= Math.Min(rect.maxX + 1, s.width - 1); x++)
                {
                    s.kinds[z * s.width + x] = -1;
                }
            }
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                int zg = s.GroundRowOf(z);
                if (zg < 0 || zg >= s.rows)
                {
                    continue;
                }
                for (int x = Math.Max(rect.minX, 0); x <= Math.Min(rect.maxX, s.width - 1); x++)
                {
                    int i = zg * s.width + x;
                    if (!s.dirty[i])
                    {
                        s.dirty[i] = true;
                        s.dirtyList.Add(i);
                    }
                }
            }
        }

        // A roof was set or removed in a cell (Patches.SetRoof_Postfix, any row): the cells its slopes reach.
        public static void RoofChanged(Map map, IntVec3 c)
        {
            if (map != null && maps.ContainsKey(map))
            {
                Dirty(map, CellRect.CenteredOn(c, SectionLayer_IsoRimRoofs.Reach));
            }
        }

        // From IsoCellMap.Rebuilt: everything again.
        public static void Rebuilt(Map map)
        {
            ShadowHeights s;
            if (map != null && maps.TryGetValue(map, out s))
            {
                s.all = true;
            }
        }

        // The ground row a map row stands over (the row itself on the ground, -1 between floors).
        private int GroundRowOf(int z)
        {
            if (groundRows <= 0 || z < groundRows)
            {
                return z;
            }
            for (int k = heights.Length - 1; k >= 1; k--)
            {
                int zg = z - shifts[k];
                if (zg >= 0 && zg < groundRows)
                {
                    return zg;
                }
            }
            return -1;
        }

        // Before the passes are drawn: brings the texture up to date. Returns the texture (null if none yet).
        public Texture2D Update()
        {
            if (all)
            {
                Stopwatch w = Stopwatch.StartNew();
                all = false;
                Floors.Bands(map, out groundRows, out heights, out shifts);
                for (int i = 0; i < kinds.Length; i++)
                {
                    kinds[i] = -1;
                }
                for (int i = 0; i < cells.Length; i++)
                {
                    Work(i);
                }
                foreach (int i in dirtyList)
                {
                    dirty[i] = false;
                }
                dirtyList.Clear();
                changed = true;
                LastFullMs = w.Elapsed.TotalMilliseconds;
                Builds++;
            }
            else if (dirtyList.Count > 0)
            {
                Stopwatch w = Stopwatch.StartNew();
                foreach (int i in dirtyList)
                {
                    dirty[i] = false;
                    Work(i);
                }
                LastDirtyCells = dirtyList.Count;
                dirtyList.Clear();
                LastDirtyMs = w.Elapsed.TotalMilliseconds;
                MaxDirtyMs = Math.Max(MaxDirtyMs, LastDirtyMs);
            }
            if (changed || tex == null)
            {
                if (tex == null)
                {
                    tex = new LiveTexture(width, rows, "IsoRim_ShadowHeights", FilterMode.Point);
                }
                tex.Upload(cells);
                Uploads++;
                changed = false;
            }
            return tex.Use();
        }

        private readonly List<Vector2> spans = new List<Vector2>(8);

        private void Work(int i)
        {
            CellsWorked++;
            IntVec3 c = new IntVec3(i % width, 0, i / width);
            spans.Clear();
            float h0 = WallBoxes.BuiltHeightAt(map, c);
            if (h0 > 0f)
            {
                spans.Add(new Vector2(0f, h0));
            }
            bool roofs = Roofs;
            float wall = View3D.WallHeight;
            if (roofs)
            {
                AddRoof(c, 0f, wall);
            }
            for (int k = 1; heights != null && k < heights.Length; k++)
            {
                IntVec3 up = new IntVec3(c.x, 0, c.z + shifts[k]);
                if (!up.InBounds(map))
                {
                    continue;
                }
                float hk = heights[k];
                if (Floors.HasFloor(map, up))
                {
                    spans.Add(new Vector2(hk - SlabDepth, hk));
                }
                float bk = WallBoxes.BuiltHeightAt(map, up);
                if (bk > 0f)
                {
                    spans.Add(new Vector2(hk, hk + bk));
                }
                if (roofs)
                {
                    AddRoof(up, hk, wall);
                }
            }
            Color32 v = new Color32(255, 0, 255, 0);
            if (spans.Count > 0)
            {
                spans.Sort((a, b) => a.x.CompareTo(b.x));
                // merge touching spans (gaps under a slab's thickness count as solid)
                int n = 0;
                for (int j = 1; j < spans.Count; j++)
                {
                    if (spans[j].x <= spans[n].y + 0.15f)
                    {
                        spans[n] = new Vector2(spans[n].x, Mathf.Max(spans[n].y, spans[j].y));
                    }
                    else
                    {
                        spans[++n] = spans[j];
                    }
                }
                Vector2 s1 = spans[0];
                v.r = Lo(s1.x);
                v.g = Hi(s1.y);
                if (n >= 1)
                {
                    float top = spans[1].y;
                    for (int j = 2; j <= n; j++)
                    {
                        top = Mathf.Max(top, spans[j].y);
                    }
                    v.b = Lo(spans[1].x);
                    v.a = Hi(top);
                }
            }
            if (!cells[i].Equals(v))
            {
                cells[i] = v;
                changed = true;
            }
        }

        // Harness (probe sshdump): the state of a map's height map and its texel over a cell (any row), as last worked out.
        public static string Report(Map map, IntVec3 c)
        {
            ShadowHeights s;
            if (map == null || !maps.TryGetValue(map, out s))
            {
                return "no height map for this map";
            }
            int zg = s.GroundRowOf(c.z);
            string texel = "-";
            if (zg >= 0 && zg < s.rows && c.x >= 0 && c.x < s.width)
            {
                Color32 v = s.cells[zg * s.width + c.x];
                texel = (v.r / Unit).ToString("0.00") + ".." + (v.g / Unit).ToString("0.00") + ", " + (v.b / Unit).ToString("0.00") + ".." + (v.a / Unit).ToString("0.00");
            }
            return "cell " + c + " ground row " + zg + ": " + texel + " | all " + s.all + ", marked " + s.dirtyList.Count + ", rows " + s.rows + ", ground rows "
                + s.groundRows + ", floors " + (s.heights != null ? s.heights.Length - 1 : -1) + ", texture " + (s.tex != null && s.tex.Ready) + ", builds " + Builds
                + ", roofs " + Roofs;
        }

        // The roof over a cell of a floor whose floor is at height floorH (see the class comment).
        private void AddRoof(IntVec3 c, float floorH, float wall)
        {
            int kind = KindAt(c.x, c.z);
            float top;
            float depth = RoofDepth;
            if (kind == 1)
            {
                top = floorH + wall + SectionLayer_IsoRimRoofs.Rise + (SectionLayer_IsoRimRoofs.Flat ? FlatRoofs.Depth
                    : SectionLayer_IsoRimRoofs.Pitch * Mathf.Clamp(LevelAt(c.x, c.z) - 0.5f, 0f, SectionLayer_IsoRimRoofs.MaxLevel));
                spans.Add(new Vector2(Mathf.Min(floorH + wall, top - depth), top));
                return;
            }
            if (kind == ShipRoofs.Kind)
            {
                top = floorH + wall + 0.1f;
            }
            else if (kind == 2)
            {
                top = floorH + wall;
                RoofDef roof = map.roofGrid.RoofAt(c);
                if (roof != null && roof.isThickRoof)
                {
                    depth = Mathf.Max(depth, 0.5f * wall);
                }
            }
            else
            {
                return;   // none, or a floor above (its slab is in already)
            }
            spans.Add(new Vector2(top - depth, top));
        }

        // The lid kind of a map cell (any row), kept until its cells are marked.
        private int KindAt(int x, int z)
        {
            if (x < 0 || z < 0 || x >= width || z >= mapRows)
            {
                return 0;
            }
            int i = z * width + x;
            sbyte k = kinds[i];
            if (k < 0)
            {
                k = (sbyte)SectionLayer_IsoRimRoofs.LidKind(map, new IntVec3(x, 0, z), false);
                kinds[i] = k;
            }
            return k;
        }

        // How many cells in from its edge a built-roof cell is (1 at the edge, at most MaxLevel + 1), as the roofs layer
        // works it out (the cells of other floors are other rows: a floor's roof ends at its own rows' edge, as drawn).
        private int LevelAt(int x, int z)
        {
            int max = SectionLayer_IsoRimRoofs.MaxLevel;
            for (int r = 1; r <= max; r++)
            {
                for (int k = -r; k <= r; k++)
                {
                    if (KindAt(x + k, z - r) != 1 || KindAt(x + k, z + r) != 1 || KindAt(x - r, z + k) != 1 || KindAt(x + r, z + k) != 1)
                    {
                        return r;
                    }
                }
            }
            return max + 1;
        }

        private static byte Lo(float h)
        {
            return (byte)Mathf.Clamp(Mathf.CeilToInt(h * Unit - 0.001f), 0, 255);
        }

        private static byte Hi(float h)
        {
            return (byte)Mathf.Clamp(Mathf.FloorToInt(h * Unit + 0.001f), 0, 255);
        }
    }
}
