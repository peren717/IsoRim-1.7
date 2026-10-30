using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Light between stacked floors (render only). The game lights each floor on its own rows (the glow grid floods a lamp's
    // light round its own cell, and a floor's cells are rows far from the ground's, with void rows between), so a torch in an
    // open courtyard never lit the walls of the floor above standing right beside it, and a lamp on a balcony never lit the
    // ground below it. The game's glow grid is left as it is (gameplay light, plants, darkness mood stay per floor, as
    // ProjectZ designed them); only what is drawn changes.
    //
    // One mechanism, where the corner colours are made: whenever the game rebuilds a block of its lighting overlay
    // (SectionLayer_LightingOverlay.GenerateLightingOverlay), each corner of a floor's rows gets, on top of its own light,
    // the light of the same corner on the floor right below and right above, as the game would average it there, times
    //   - how open the ceiling between the two floors is at that corner: the share of the cells round the corner on the
    //     lower floor that let light through (not walls) and have no roof drawn over them (a floor's slab is a roof below it;
    //     roofs the floor layout keeps hidden, a stairwell's, count as open), so a slab, a roof or rock blocks it, and
    //   - Attenuation, for the storey of height the light crosses.
    // The block's mesh colours are written back, so the game's overlay (the ground: terrain, sprites) and IsoRim's glow
    // texture (copied from them right after, IsoGlow.Generated: every 3D face on every floor) both show it. Only adjacent
    // floors: what reaches two storeys up crosses two ceilings.
    //
    // A block's light now also depends on the floors above and below: when a block's own light (or which of its cells are
    // roofed or walls) changed since it was last made, the blocks over and under it are made again (MapMeshDirty with
    // GroundGlow). Those find their own light unchanged and stop there.
    public static class GlowFloors
    {
        // Harness only (probe or dev tools): off for before/after shots.
        public static bool Enabled = true;

        // The share of a corner's light that reaches the same corner one storey up or down.
        public const float Attenuation = 0.7f;

        private static readonly HarmonyLib.AccessTools.FieldRef<MapDrawer, Section[,]> drawerSections = HarmonyLib.AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static readonly int[] dx = { -1, 0, -1, 0 };
        private static readonly int[] dz = { -1, -1, 0, 0 };

        // Per map: the last state (own light, roofs, walls) of each block, by its south-west cell index.
        private static readonly Dictionary<Map, Dictionary<int, int>> states = new Dictionary<Map, Dictionary<int, int>>();

        // Bands of the map being worked on (refetched once a frame per map).
        private static Map bandsMap;
        private static int bandsFrame = -1;
        private static int groundRows, floors;
        private static float[] heights;
        private static int[] shifts;

        public static int Applied, Dirtied;   // counts (harness)

        // After the game set the colours of a block (rect, clipped; corners first row by row, then the centres from
        // firstCenter). Changes nothing on maps without floors.
        public static void Apply(Map map, LayerSubMesh subMesh, CellRect rect, int firstCenter)
        {
            if (!Enabled || !LoadBands(map))
            {
                return;
            }
            Mesh mesh = subMesh.mesh;
            Color32[] colors = mesh.colors32;
            int w = rect.Width + 1, h = rect.Height + 1;
            if (rect.Width <= 0 || rect.Height <= 0 || colors == null || colors.Length < w * h || firstCenter < w * h
                || colors.Length < firstCenter + rect.Width * rect.Height)
            {
                return;
            }
            int sx = map.Size.x, sz = map.Size.z;
            Thing[] edifices = map.edificeGrid.InnerArray;
            RoofGrid roofs = map.roofGrid;
            GlowGrid glow = map.glowGrid;

            // what the block's own light depends on: its colours as the game made them, its roofs and walls
            int state = 17;
            for (int i = 0; i < firstCenter; i++)
            {
                Color32 c = colors[i];
                state = state * 31 + (c.r | c.g << 8 | c.b << 16 | c.a << 24);
            }
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    int i = z * sx + x;
                    state = state * 5 + (Open(edifices[i]) ? 1 : 0) + (Roofed(roofs, i) ? 2 : 0);
                }
            }

            bool any = false;
            for (int j = 0; j < h; j++)
            {
                int z = rect.minZ + j;
                int k = BandOfRow(z);
                if (k < 0)
                {
                    k = BandOfRow(z - 1);
                }
                if (k < 0)
                {
                    continue;
                }
                int gz = z - shifts[k];   // the corner row in ground terms, 0..groundRows
                for (int i = 0; i < w; i++)
                {
                    int x = rect.minX + i;
                    int r = 0, g = 0, b = 0;
                    for (int other = k - 1; other <= k + 1; other += 2)
                    {
                        if (other < 0 || other > floors)
                        {
                            continue;
                        }
                        float open = Openness(edifices, roofs, sx, x, gz, shifts[Mathf.Min(k, other)]);
                        if (open <= 0f)
                        {
                            continue;
                        }
                        Color32 lit = Corner(edifices, glow, sx, x, gz, shifts[other]);
                        float f = open * Attenuation;
                        r += (int)(lit.r * f);
                        g += (int)(lit.g * f);
                        b += (int)(lit.b * f);
                    }
                    if (r + g + b == 0)
                    {
                        continue;
                    }
                    int at = j * w + i;
                    Color32 c = colors[at];
                    colors[at] = new Color32((byte)Mathf.Min(255, c.r + r), (byte)Mathf.Min(255, c.g + g), (byte)Mathf.Min(255, c.b + b), c.a);
                    any = true;
                }
            }
            if (any)
            {
                // the centres again: the average of their four corners (the alpha, the roof cover, as the game made it)
                for (int j = 0; j < rect.Height; j++)
                {
                    for (int i = 0; i < rect.Width; i++)
                    {
                        int bl = j * w + i;
                        Color32 a = colors[bl], b = colors[bl + 1], c = colors[bl + w], d = colors[bl + w + 1];
                        int ci = firstCenter + j * rect.Width + i;
                        colors[ci] = new Color32((byte)((a.r + b.r + c.r + d.r) / 4), (byte)((a.g + b.g + c.g + d.g) / 4), (byte)((a.b + b.b + c.b + d.b) / 4), colors[ci].a);
                    }
                }
                mesh.colors32 = colors;
                Applied++;
            }

            // the blocks above and below follow when this one's own light changed
            Dictionary<int, int> known;
            if (!states.TryGetValue(map, out known))
            {
                Prune();
                known = new Dictionary<int, int>();
                states[map] = known;
            }
            int key = rect.minZ * sx + rect.minX;
            int last;
            if (known.TryGetValue(key, out last) && last == state)
            {
                return;
            }
            known[key] = state;
            DirtyNeighbours(map, rect, sx, sz);
        }

        // The blocks over and under every floor's part of rect.
        private static void DirtyNeighbours(Map map, CellRect rect, int sx, int sz)
        {
            Section[,] all = drawerSections(map.mapDrawer);
            if (Current.ProgramState != ProgramState.Playing || all == null)
            {
                return;
            }
            ulong flag = (ulong)RimWorld.MapMeshFlagDefOf.GroundGlow;
            for (int k = 0; k <= floors; k++)
            {
                int lo = Mathf.Max(rect.minZ, shifts[k]), hi = Mathf.Min(rect.maxZ, shifts[k] + groundRows - 1);
                if (lo > hi)
                {
                    continue;
                }
                for (int other = k - 1; other <= k + 1; other += 2)
                {
                    if (other < 0 || other > floors)
                    {
                        continue;
                    }
                    int d = shifts[other] - shifts[k];
                    // the corners of the moved part (a block of 17 lines up with one there; when it doesn't, it covers up to four)
                    foreach (int z in new[] { lo + d, hi + d })
                    {
                        foreach (int x in new[] { rect.minX, rect.maxX })
                        {
                            if (x >= 0 && x < sx && z >= 0 && z < sz && x / 17 < all.GetLength(0) && z / 17 < all.GetLength(1) && all[x / 17, z / 17] != null)
                            {
                                map.mapDrawer.MapMeshDirty(new IntVec3(x, 0, z), flag, false, false);
                                Dirtied++;
                            }
                        }
                    }
                }
            }
        }

        // The game's colour for corner (x, gz) of the floor whose rows start at shift: the average glow of the cells round it
        // that are not walls (SectionLayer_LightingOverlay.GenerateLightingOverlay), alpha left out.
        private static Color32 Corner(Thing[] edifices, GlowGrid glow, int sx, int x, int gz, int shift)
        {
            int r = 0, g = 0, b = 0, n = 0;
            for (int q = 0; q < 4; q++)
            {
                int cx = x + dx[q], cz = gz + dz[q];
                if (cx < 0 || cx >= sx || cz < 0 || cz >= groundRows)
                {
                    continue;
                }
                int i = (cz + shift) * sx + cx;
                if (!Open(edifices[i]))
                {
                    continue;
                }
                Color32 c = glow.VisualGlowAt(i);
                r += c.r;
                g += c.g;
                b += c.b;
                n++;
            }
            return n == 0 ? new Color32(0, 0, 0, 0) : new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 0);
        }

        // How open the ceiling over corner (x, gz) of the floor whose rows start at shift is: the share of the cells round
        // it that let light through and have no roof (0 when none lets light through).
        private static float Openness(Thing[] edifices, RoofGrid roofs, int sx, int x, int gz, int shift)
        {
            int n = 0, open = 0;
            for (int q = 0; q < 4; q++)
            {
                int cx = x + dx[q], cz = gz + dz[q];
                if (cx < 0 || cx >= sx || cz < 0 || cz >= groundRows)
                {
                    continue;
                }
                int i = (cz + shift) * sx + cx;
                if (!Open(edifices[i]))
                {
                    continue;
                }
                n++;
                if (!Roofed(roofs, i))
                {
                    open++;
                }
            }
            return n == 0 ? 0f : (float)open / n;
        }

        private static bool Open(Thing edifice)
        {
            return edifice == null || !edifice.def.blockLight;
        }

        private static bool Roofed(RoofGrid roofs, int i)
        {
            RoofDef roof = roofs.RoofAt(i);
            return roof != null && !Api.RoofHidden(roof);
        }

        private static int BandOfRow(int z)
        {
            for (int k = 0; k <= floors; k++)
            {
                if (z >= shifts[k] && z < shifts[k] + groundRows)
                {
                    return k;
                }
            }
            return -1;
        }

        private static bool LoadBands(Map map)
        {
            if (map == null || Api.Floors == null)
            {
                return false;
            }
            if (!ReferenceEquals(map, bandsMap) || bandsFrame != Time.frameCount)
            {
                bandsMap = map;
                bandsFrame = Time.frameCount;
                floors = Floors.Bands(map, out groundRows, out heights, out shifts);
            }
            return floors > 0 && groundRows > 0;
        }

        private static readonly List<Map> gone = new List<Map>();

        private static void Prune()
        {
            List<Map> live = Find.Maps;
            foreach (Map m in states.Keys)
            {
                if (live == null || !live.Contains(m))
                {
                    gone.Add(m);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                states.Remove(gone[i]);
            }
            gone.Clear();
        }
    }
}
