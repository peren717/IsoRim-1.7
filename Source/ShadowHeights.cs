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
    public sealed class ShadowHeights
    {
        public const float Unit = 8f;   // steps per cell
        public const float SlabDepth = 0.5f;   // a slab counts this deep under its floor (drawn: Floors.SlabThickness), so the
                                               // march's 0.5-cell steps cannot pass through it; the room below shades its ceiling anyway
        private static readonly Dictionary<Map, ShadowHeights> maps = new Dictionary<Map, ShadowHeights>();

        public static int Builds, CellsWorked;      // harness
        public static double LastFullMs;            // harness: the last full build

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

        private ShadowHeights(Map map)
        {
            this.map = map;
            width = map.Size.x;
            rows = Floors.DrawnRows(map);
            cells = new Color32[width * rows];
            dirty = new bool[width * rows];
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

        // From IsoCellMap.Dirty: cells of the map (any row) whose blocks or floors may have changed.
        public static void Dirty(Map map, CellRect rect)
        {
            ShadowHeights s;
            if (map == null || !maps.TryGetValue(map, out s) || s.all)
            {
                return;
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
                foreach (int i in dirtyList)
                {
                    dirty[i] = false;
                    Work(i);
                }
                dirtyList.Clear();
            }
            if (changed || tex == null)
            {
                if (tex == null)
                {
                    tex = new LiveTexture(width, rows, "IsoRim_ShadowHeights", FilterMode.Point);
                }
                tex.Upload(cells);
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
