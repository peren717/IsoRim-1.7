using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        // Collapse debris (Version 4). The cells (the map's own rows: an upper floor's cell is that floor's) are about to
        // come down: IsoRim takes what it draws there now (an upper floor's slab and the floor laid on it, walls and rock,
        // the buildings' models; not roofs, which break up when the game drops them, nor pawns and items), breaks it into
        // a few big pieces that fall to the floor below or the ground, settle and sink away after a few seconds. Visual
        // only. Call it BEFORE the cells' things and floor are removed (the pieces keep the intact look). Does nothing
        // with the 3D view off, the "Collapse debris" option off, or on a map that is not shown.
        public static void Debris(Map map, IEnumerable<IntVec3> cells)
        {
            CollapseDebris.Snapshot(map, cells, false);
        }

        public static partial class Dev
        {
            // Collapse debris for tests: the pieces stand still except for DebrisAdvance (frame sheets at fixed steps).
            public static bool DebrisHold
            {
                get { return CollapseDebris.Hold; }
                set { CollapseDebris.Hold = value; }
            }

            // Moves the pieces on by this many seconds (in steps of 1/60 s) at the next frame.
            public static void DebrisAdvance(float seconds)
            {
                CollapseDebris.Advance(seconds);
            }

            // Pieces now (falling, lying or sinking) and cells waiting to be broken up.
            public static int DebrisLive => CollapseDebris.Live;

            public static int DebrisPending => CollapseDebris.PendingCells;

            // Counters and timings (snapshots, pieces made, triangles, build and per-frame cost).
            public static string DebrisStats => CollapseDebris.Stats();

            public static void DebrisClear()
            {
                CollapseDebris.Clear();
                CollapseDebris.ResetStats();
            }
        }
    }

    // Collapse debris (design: debris prototype 2026-10-17). What IsoRim draws in collapsing cells is copied from the
    // section meshes while they still hold it (the game rebuilds them a frame later), cut into a few big convex pieces
    // per cell and dropped:
    //   - snapshot: the cell's triangles of IsoRim's walls, realtime bodies and (roof drop) roof layers, an upper floor's
    //     terrain; the cell's buildings printed once more into a spare things layer (their models through the usual
    //     bake: no items, filth or plants); solid cores (an upper floor's slab, a wall's block) so cut faces are closed;
    //   - pieces: each cell box split by 1-3 random, slightly turned planes (a small BSP), the triangles and cores clipped
    //     along them; fewer pieces per cell for big collapses, MaxChunks pieces at most;
    //   - flight: a small push and spin, gravity, one landing on the first floor below with ground there (the top of a
    //     wall that still stands under it), a small bounce, then the piece rolls onto its flattest side, lies a few
    //     seconds and shrinks into the floor;
    //   - drawn with the materials they were drawn with (the game's ground materials a little later, as the gravship
    //     snapshot does), in drawn space; not in fog, not while the floor the piece is in is hidden.
    // The roofs: when the game drops a roof (RoofCollapserImmediate.DropRoofInCells, all three overloads, after other
    // mods' prefixes), the roof's own cells are taken the same way (roof layer only).
    public static class CollapseDebris
    {
        public const int MaxChunks = 400;
        private const float Gravity = 18f;          // cells / s^2 (true space)
        private const float Restitution = 0.3f;
        private const float SettleTime = 0.25f;
        private const float ShrinkTime = 0.8f;
        private const float Step = 1f / 60f;

        // ---- snapshot data (made in the game's tick, broken up over the next frames)

        private struct V
        {
            public Vector3 p;     // drawn x, z; true height y
            public Color32 c;
            public Vector3 uv;
        }

        private struct Tri
        {
            public V a, b, c;
            public int g;
        }

        private struct Solid
        {
            public Vector3 min, max;
            public Color32 col;
        }

        private sealed class CellSnap
        {
            public Map map;
            public IntVec3 cell;      // the map's own row
            public int floor;
            public float x0, z0;      // the cell's corner in drawn space
            public float floorH;
            public bool roof;
            public readonly List<Tri> tris = new List<Tri>();
            public readonly List<Solid> solids = new List<Solid>();
        }

        private sealed class Group
        {
            public Material source;
            public Material draw;
            public Material mark;
            public int layer;
        }

        private static readonly List<Group> groups = new List<Group>();
        private static readonly HashSet<long> done = new HashSet<long>();
        private static readonly HashSet<int> printed = new HashSet<int>();
        private static readonly HashSet<Section> regenerated = new HashSet<Section>();
        private static readonly Dictionary<SectionLayer, Dictionary<int, List<long>>> index = new Dictionary<SectionLayer, Dictionary<int, List<long>>>();
        private static int doneTick = -1;

        // the layers TakeLayer indexes
        private static readonly Type[] IndexedLayers = { typeof(SectionLayer_Terrain), typeof(SectionLayer_IsoRimWalls), typeof(SectionLayer_IsoRimBodies), typeof(SectionLayer_IsoRimRoofs) };

        // the map's floors (this tick)
        private static Map bandsMap;
        private static int bandsTick = -1;
        private static int groundRows;
        private static float[] heights = { 0f };
        private static int[] shifts = { 0 };

        private static readonly MethodInfo takePrintFrom = AccessTools.Method(typeof(SectionLayer_ThingsGeneral), "TakePrintFrom");

        public static bool Hold;
        private static float advance;

        // ---- counters
        private static int snapshots, cellsTaken, chunksMade, trisMade, dropped;
        private static double buildMs, buildMaxMs, frameMsSum, frameMsMax, snapMs, snapTickMs, snapMaxMs, breakMaxMs;
        private static int snapTick = -1;
        private static int frames;

        public static int PendingCells => PendingCount();

        public static int Live => live.Count;

        private static bool Wanted(Map map)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            return map != null && st != null && st.collapseDebris && View3D.Ready && map == Find.CurrentMap && Current.ProgramState == ProgramState.Playing;
        }

        private static void Bands(Map map)
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (bandsMap == map && bandsTick == tick)
            {
                return;
            }
            bandsMap = map;
            bandsTick = tick;
            Floors.Bands(map, out groundRows, out heights, out shifts);
        }

        private static int FloorOfRow(int z)
        {
            if (groundRows <= 0 || z < groundRows)
            {
                return 0;
            }
            for (int k = 1; k < shifts.Length; k++)
            {
                if (z >= shifts[k] && z < shifts[k] + groundRows)
                {
                    return k;
                }
            }
            return -1;
        }

        // ---- taking the snapshot

        private static readonly List<IntVec3> one = new List<IntVec3>(1);

        public static void Snapshot(Map map, IntVec3 c, bool roofOnly)
        {
            one.Clear();
            one.Add(c);
            Snapshot(map, one, roofOnly);
        }

        public static void Snapshot(Map map, IEnumerable<IntVec3> cells, bool roofOnly)
        {
            if (cells == null || !Wanted(map))
            {
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                int tick = Find.TickManager.TicksGame;
                if (tick != doneTick)
                {
                    doneTick = tick;
                    done.Clear();
                    printed.Clear();
                    index.Clear();
                    regenerated.Clear();
                }
                Bands(map);
                foreach (IntVec3 c in cells)
                {
                    if (!c.InBounds(map) || roofOnly && !c.Roofed(map))
                    {
                        continue;
                    }
                    long key = ((long)c.z * map.Size.x + c.x) * 2 + (roofOnly ? 1 : 0);
                    if (!done.Add(key) || map.fogGrid.IsFogged(c))
                    {
                        continue;
                    }
                    int k = FloorOfRow(c.z);
                    if (k < 0)
                    {
                        continue;
                    }
                    CellSnap s = new CellSnap
                    {
                        map = map,
                        cell = c,
                        floor = k,
                        floorH = heights[k],
                        x0 = c.x,
                        z0 = c.z - shifts[k],
                        roof = roofOnly
                    };
                    Take(s);
                    cellsTaken++;
                    if (s.tris.Count > 0 || s.solids.Count > 0)
                    {
                        AddPending(s, tick);
                    }
                }
                snapshots++;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Taking the collapse debris failed: " + e, 93611701);
            }
            double ms = watch.Elapsed.TotalMilliseconds;
            snapMs += ms;
            int t = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            snapTickMs = t == snapTick ? snapTickMs + ms : ms;
            snapTick = t;
            snapMaxMs = Math.Max(snapMaxMs, snapTickMs);
        }

        private static void Take(CellSnap s)
        {
            Map map = s.map;
            IntVec3 c = s.cell;
            Section sec = map.mapDrawer.SectionAt(c);
            if (sec == null)
            {
                return;
            }
            // A hidden floor's blocks are not rebuilt while hidden: bring them up to date once (rare: a collapse up there).
            if (s.floor > 0 && !Floors.Shown(s.floor) && sec.dirtyFlags != 0 && regenerated.Add(sec))
            {
                sec.RegenerateAllLayers();
                // Its meshes are new: an index made from them earlier this tick (a cell taken before the collapse
                // dirtied the block) points at triangles that are gone.
                foreach (Type t in IndexedLayers)
                {
                    SectionLayer l = sec.GetLayer(t);
                    if (l != null)
                    {
                        index.Remove(l);
                    }
                }
            }
            if (s.roof)
            {
                TakeLayer(s, sec.GetLayer(typeof(SectionLayer_IsoRimRoofs)), null);
                return;
            }
            bool upper = s.floor > 0 && Floors.HasFloor(map, c);
            if (upper)
            {
                TakeLayer(s, sec.GetLayer(typeof(SectionLayer_Terrain)), null);
                float unused;
                if (!(Floors.FullBlock(map, c, out unused, true) && Floors.FullBlock(map, Floors.Stacked(map, c, -1), out unused)))
                {
                    Color32 col = map.terrainGrid.TerrainAt(c).DrawColor;
                    col.a = 255;
                    s.solids.Add(new Solid
                    {
                        min = new Vector3(s.x0, s.floorH - Floors.SlabThickness, s.z0),
                        max = new Vector3(s.x0 + 1f, s.floorH - 0.004f, s.z0 + 1f),
                        col = col
                    });
                }
            }
            // Walls and rock: their faces, and a solid block of their colour inside (closes the cut faces).
            Bounds wall = new Bounds();
            bool any = false;
            Color sum = Color.black;
            int n0 = s.tris.Count;
            TakeLayer(s, sec.GetLayer(typeof(SectionLayer_IsoRimWalls)), null);
            float blockH;
            if (s.tris.Count > n0 && Floors.FullBlock(map, c, out blockH))
            {
                for (int i = n0; i < s.tris.Count; i++)
                {
                    Tri t = s.tris[i];
                    Color tint = MatColour(groups[t.g].source);
                    foreach (V v in new[] { t.a, t.b, t.c })
                    {
                        if (!any)
                        {
                            wall = new Bounds(v.p, Vector3.zero);
                            any = true;
                        }
                        else
                        {
                            wall.Encapsulate(v.p);
                        }
                        sum += (Color)v.c * tint;
                    }
                }
                Vector3 min = Vector3.Max(wall.min, new Vector3(s.x0, s.floorH - 2f, s.z0)) + new Vector3(0.006f, 0.004f, 0.006f);
                Vector3 max = Vector3.Min(wall.max, new Vector3(s.x0 + 1f, s.floorH + 4f, s.z0 + 1f)) - new Vector3(0.006f, 0.004f, 0.006f);
                if (max.x > min.x && max.y > min.y && max.z > min.z)
                {
                    Color avg = sum / (3f * (s.tris.Count - n0));
                    avg *= 0.85f;
                    avg.a = 1f;
                    s.solids.Add(new Solid { min = min, max = max, col = avg });
                }
            }
            TakeLayer(s, sec.GetLayer(typeof(SectionLayer_IsoRimBodies)), null);
            Reprint(s, sec);
        }

        private static Color MatColour(Material m)
        {
            return m != null && m.HasProperty("_Color") ? m.color : Color.white;
        }

        // The cell's buildings, printed once more into a spare things layer (the game's TakePrintFrom with IsoRim's
        // bake): the models as drawn, without the items, filth and plants the block's own things layer also holds.
        private static void Reprint(CellSnap s, Section sec)
        {
            if (takePrintFrom == null)
            {
                return;
            }
            List<Thing> things = s.cell.GetThingList(s.map);
            SectionLayer_ThingsGeneral spare = null;
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.def.category != ThingCategory.Building || t is Blueprint || t is Frame || t is Plant || t.def.drawerType == DrawerType.RealtimeOnly
                    || t.def.drawerType == DrawerType.None || SectionLayer_IsoRimWalls.Handles(t) || !printed.Add(t.thingIDNumber))
                {
                    continue;
                }
                if (spare == null)
                {
                    spare = new SectionLayer_ThingsGeneral(sec);
                }
                takePrintFrom.Invoke(spare, new object[] { t });
            }
            if (spare == null)
            {
                return;
            }
            foreach (LayerSubMesh sm in spare.subMeshes)
            {
                if (sm.material != null && sm.material != R3DMaterials.GroundShadow)
                {
                    for (int k = 0; k + 2 < sm.tris.Count; k += 3)
                    {
                        AddTri(s, sm, sm.tris[k], sm.tris[k + 1], sm.tris[k + 2]);
                    }
                }
                if (sm.mesh != null)
                {
                    UnityEngine.Object.Destroy(sm.mesh);
                }
            }
            spare.subMeshes.Clear();
        }

        // The triangles of a layer that belong to the cell: by their cut-away / roof-clip tag's owner cell when tagged,
        // else by the cell under their middle (as the gravship snapshot reads them).
        private static void TakeLayer(CellSnap s, SectionLayer layer, object unused)
        {
            if (layer == null || !layer.Visible)
            {
                return;
            }
            Dictionary<int, List<long>> byCell;
            if (!index.TryGetValue(layer, out byCell))
            {
                byCell = Index(layer, s.map);
                index[layer] = byCell;
            }
            List<long> mine;
            if (!byCell.TryGetValue(s.cell.z * s.map.Size.x + s.cell.x, out mine))
            {
                return;
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            foreach (long e in mine)
            {
                int i = (int)(e >> 32);
                int k = (int)(e & 0xffffffff);
                LayerSubMesh sm = subs[i];
                AddTri(s, sm, sm.tris[k], sm.tris[k + 1], sm.tris[k + 2]);
            }
        }

        private static Dictionary<int, List<long>> Index(SectionLayer layer, Map map)
        {
            Dictionary<int, List<long>> byCell = new Dictionary<int, List<long>>();
            List<LayerSubMesh> subs = layer.subMeshes;
            int w = map.Size.x;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sm = subs[i];
                if (!sm.finalized || sm.disabled || sm.material == null || sm.verts.Count == 0 || sm.material == R3DMaterials.GroundShadow)
                {
                    continue;
                }
                List<Vector3> v = sm.verts;
                List<int> t = sm.tris;
                List<Vector3> tag = sm.uvsChannelTwo.Count == v.Count ? sm.uvsChannelTwo : null;
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    int cx, cz;
                    int flags = tag != null ? (int)(tag[t[k]].z + 0.5f) : 0;
                    if (flags != 0)
                    {
                        if ((flags & ShaderCut.Twig) != 0)
                        {
                            continue;
                        }
                        cx = Mathf.FloorToInt(tag[t[k]].x);
                        cz = Mathf.FloorToInt(tag[t[k]].y);
                    }
                    else
                    {
                        Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
                        cx = Mathf.FloorToInt((a.x + b.x + c.x) / 3f);
                        cz = Mathf.FloorToInt((a.z + b.z + c.z) / 3f);
                    }
                    int key = cz * w + cx;
                    List<long> l;
                    if (!byCell.TryGetValue(key, out l))
                    {
                        byCell[key] = l = new List<long>();
                    }
                    l.Add(((long)i << 32) | (uint)k);
                }
            }
            return byCell;
        }

        private static void AddTri(CellSnap s, LayerSubMesh sm, int a, int b, int c)
        {
            s.tris.Add(new Tri { a = Vert(s, sm, a), b = Vert(s, sm, b), c = Vert(s, sm, c), g = GroupFor(sm.material, sm.renderLayer) });
        }

        private static V Vert(CellSnap s, LayerSubMesh sm, int i)
        {
            Vector3 p = sm.verts[i];
            return new V
            {
                p = new Vector3(p.x, s.floorH + p.y * View3D.Squash, p.z - (s.cell.z - s.z0)),
                c = sm.colors.Count > i ? sm.colors[i] : new Color32(255, 255, 255, 255),
                uv = sm.uvs.Count > i ? sm.uvs[i] : Vector3.zero
            };
        }

        private static int GroupFor(Material mat, int layer)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].source == mat && groups[i].layer == layer)
                {
                    return i;
                }
            }
            groups.Add(new Group { source = mat, draw = ShipSnapshot.Flying(mat), mark = IsoGlow.MarkFor(mat), layer = layer });
            return groups.Count - 1;
        }

        private static int coreGroup = -1;

        private static int CoreGroup
        {
            get
            {
                if (coreGroup < 0 || groups[coreGroup].source != R3DMaterials.LitModel)
                {
                    coreGroup = GroupFor(R3DMaterials.LitModel, 0);
                }
                return coreGroup;
            }
        }

        // ---- the roof drops (RoofCollapserImmediate.DropRoofInCells, prefixes run after other mods')

        public static void Patch(Harmony h)
        {
            string[] names = { nameof(DropCell_Prefix), nameof(DropCells_Prefix), nameof(DropCellList_Prefix) };
            Type[][] args =
            {
                new[] { typeof(IntVec3), typeof(Map), typeof(List<Thing>) },
                new[] { typeof(IEnumerable<IntVec3>), typeof(Map), typeof(List<Thing>) },
                new[] { typeof(List<IntVec3>), typeof(Map), typeof(List<Thing>) }
            };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    MethodInfo target = AccessTools.Method(typeof(RoofCollapserImmediate), nameof(RoofCollapserImmediate.DropRoofInCells), args[i]);
                    if (target == null)
                    {
                        ModLog.Warn("[IsoRim] Roof collapse (" + i + ") not found: no debris for that roof drop.");
                        continue;
                    }
                    h.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(CollapseDebris), names[i])) { priority = Priority.Last });
                }
                catch (Exception e)
                {
                    ModLog.Warn("[IsoRim] Could not hook the roof collapse for debris: " + e.Message);
                }
            }
        }

        public static void DropCell_Prefix(IntVec3 c, Map map, bool __runOriginal)
        {
            if (__runOriginal)
            {
                Snapshot(map, c, true);
            }
        }

        public static void DropCells_Prefix(IEnumerable<IntVec3> cells, Map map, bool __runOriginal)
        {
            if (__runOriginal)
            {
                Snapshot(map, cells, true);
            }
        }

        public static void DropCellList_Prefix(List<IntVec3> cells, Map map, bool __runOriginal)
        {
            if (__runOriginal)
            {
                Snapshot(map, cells, true);
            }
        }

        // ---- pieces

        private sealed class Chunk
        {
            public Mesh mesh;
            public int[] subGroup = new int[8];
            public int subs;
            public Map map;
            public Vector3 pos;        // drawn x, z; true y (the piece's middle)
            public Vector3 vel;
            public Vector3 spin;       // angular velocity, radians per second
            public Quaternion rot = Quaternion.identity;
            public Quaternion from, to;
            public Vector3 half;       // half size of its box (true space)
            public float age, delay, life, t, scale = 1f;
            public int state;          // 0 falling, 1 rolling flat, 2 lying, 3 shrinking
            public int bounces;
            public float land;
            public int floor;          // the floor it falls from
            public float recheck;
            public bool fogged;
        }

        private static readonly List<Chunk> live = new List<Chunk>();
        private static readonly Stack<Mesh> meshPool = new Stack<Mesh>();

        // A polygon of a piece (convex, in a plane).
        private sealed class Poly
        {
            public readonly List<V> v = new List<V>(6);
            public int g;
        }

        // A convex solid (faces of positions) with one colour.
        private sealed class Hull
        {
            public readonly List<List<Vector3>> faces = new List<List<Vector3>>();
            public Color32 col;
        }

        private sealed class Piece
        {
            public readonly List<Poly> polys = new List<Poly>();
            public readonly List<Hull> hulls = new List<Hull>();
        }

        // The cells a collapse took in one tick: broken up over the next frames, BuildBudgetMs a frame (the snapshot
        // itself was taken at the collapse; the pieces fall from above, a frame or two late does not show).
        private sealed class Batch
        {
            public readonly List<CellSnap> cells = new List<CellSnap>();
            public int tick;
            public bool planned;
            public int next, per, stride;
            public bool tall;
            public Vector3 centre;
        }

        private const double BuildBudgetMs = 3.0;
        private static readonly List<Batch> batches = new List<Batch>();

        private static void AddPending(CellSnap s, int tick)
        {
            Batch b = batches.Count > 0 ? batches[batches.Count - 1] : null;
            if (b == null || b.planned || b.tick != tick || b.cells[0].map != s.map)
            {
                b = new Batch { tick = tick };
                batches.Add(b);
            }
            b.cells.Add(s);
        }

        private static int PendingCount()
        {
            int n = 0;
            foreach (Batch b in batches)
            {
                n += b.cells.Count - b.next;
            }
            return n;
        }

        private static void Plan(Batch b)
        {
            b.planned = true;
            int n = b.cells.Count;
            // Pieces per cell: four (eight for walls in small collapses), two or one for big ones.
            b.per = n * 4 <= MaxChunks ? 4 : n * 2 <= MaxChunks ? 2 : 1;
            b.tall = n <= 25;
            b.stride = n > MaxChunks ? Mathf.CeilToInt(n / (float)MaxChunks) : 1;
            int want = Mathf.Min(MaxChunks, n / b.stride * b.per * (b.tall ? 2 : 1));
            // Room for them: the oldest pieces go first.
            while (live.Count > 0 && live.Count + want > MaxChunks)
            {
                int oldest = 0;
                for (int i = 1; i < live.Count; i++)
                {
                    if (live[i].age - live[i].delay > live[oldest].age - live[oldest].delay)
                    {
                        oldest = i;
                    }
                }
                Free(oldest);
            }
            Vector3 centre = Vector3.zero;
            foreach (CellSnap s in b.cells)
            {
                centre += new Vector3(s.x0 + 0.5f, s.floorH, s.z0 + 0.5f);
            }
            b.centre = centre / n;
        }

        private static void Process(Map map)
        {
            if (batches.Count == 0)
            {
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Bands(map);
                do
                {
                    Batch b = batches[0];
                    if (b.cells[0].map != map)
                    {
                        batches.RemoveAt(0);
                        continue;
                    }
                    if (!b.planned)
                    {
                        Plan(b);
                    }
                    if (b.next < b.cells.Count && live.Count < MaxChunks)
                    {
                        double t0 = watch.Elapsed.TotalMilliseconds;
                        Break(b.cells[b.next], b.per, b.tall, b.centre);
                        breakMaxMs = Math.Max(breakMaxMs, watch.Elapsed.TotalMilliseconds - t0);
                        b.next += b.stride;
                    }
                    else
                    {
                        b.next = b.cells.Count;
                    }
                    if (b.next >= b.cells.Count)
                    {
                        batches.RemoveAt(0);
                    }
                }
                while (batches.Count > 0 && watch.Elapsed.TotalMilliseconds < BuildBudgetMs);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Breaking up the collapse debris failed: " + e, 93611702);
                batches.Clear();
            }
            double ms = watch.Elapsed.TotalMilliseconds;
            buildMs += ms;
            buildMaxMs = Math.Max(buildMaxMs, ms);
        }

        private static void Break(CellSnap s, int per, bool tall, Vector3 centre)
        {
            Piece all = new Piece();
            foreach (Tri t in s.tris)
            {
                Poly p = new Poly { g = t.g };
                p.v.Add(t.a);
                p.v.Add(t.b);
                p.v.Add(t.c);
                all.polys.Add(p);
            }
            foreach (Solid so in s.solids)
            {
                all.hulls.Add(BoxHull(so));
            }
            Bounds b = BoundsOf(all);
            int depth = per >= 4 ? 2 : per == 2 ? 1 : 0;
            if (tall && depth == 2 && b.size.y > 0.6f)
            {
                depth = 3;
            }
            List<Piece> leaves = new List<Piece>();
            Split(all, depth, 0, Rand.Bool ? 0 : 1, leaves);
            foreach (Piece p in leaves)
            {
                if (live.Count >= MaxChunks)
                {
                    dropped++;
                    continue;
                }
                Spawn(s, p, centre);
            }
        }

        private static Hull BoxHull(Solid so)
        {
            Vector3 a = so.min, b = so.max;
            Vector3[] q =
            {
                new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z),
                new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z)
            };
            int[][] f = { new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 }, new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 } };
            Hull h = new Hull { col = so.col };
            foreach (int[] face in f)
            {
                List<Vector3> l = new List<Vector3>(4);
                foreach (int i in face)
                {
                    l.Add(q[i]);
                }
                h.faces.Add(l);
            }
            return h;
        }

        private static Bounds BoundsOf(Piece p)
        {
            bool any = false;
            Bounds b = new Bounds();
            foreach (Poly poly in p.polys)
            {
                foreach (V v in poly.v)
                {
                    if (!any)
                    {
                        b = new Bounds(v.p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(v.p);
                    }
                }
            }
            foreach (Hull h in p.hulls)
            {
                foreach (List<Vector3> f in h.faces)
                {
                    foreach (Vector3 v in f)
                    {
                        if (!any)
                        {
                            b = new Bounds(v, Vector3.zero);
                            any = true;
                        }
                        else
                        {
                            b.Encapsulate(v);
                        }
                    }
                }
            }
            return b;
        }

        // A small BSP of random planes: two cuts across the cell (each half its own), then one across the height.
        private static void Split(Piece p, int depth, int level, int axis, List<Piece> leaves)
        {
            if (level >= depth || p.polys.Count == 0 && p.hulls.Count == 0)
            {
                if (p.polys.Count > 0 || p.hulls.Count > 0)
                {
                    leaves.Add(p);
                }
                return;
            }
            Bounds b = BoundsOf(p);
            Vector3 n;
            Vector3 at = b.center;
            if (level < 2)
            {
                int ax = (axis + level) % 2;
                float turn = (ax == 0 ? 0f : 90f) + Rand.Range(-25f, 25f);
                n = new Vector3(Mathf.Cos(turn * Mathf.Deg2Rad), 0f, Mathf.Sin(turn * Mathf.Deg2Rad));
                float ext = ax == 0 ? b.size.x : b.size.z;
                if (ax == 0)
                {
                    at.x += Rand.Range(-0.18f, 0.18f) * ext;
                }
                else
                {
                    at.z += Rand.Range(-0.18f, 0.18f) * ext;
                }
            }
            else
            {
                n = new Vector3(Rand.Range(-0.3f, 0.3f), 1f, Rand.Range(-0.3f, 0.3f)).normalized;
                at.y = b.min.y + b.size.y * Rand.Range(0.35f, 0.65f);
            }
            Piece front = new Piece(), back = new Piece();
            foreach (Poly poly in p.polys)
            {
                ClipPoly(poly, n, at, front.polys, back.polys);
            }
            foreach (Hull h in p.hulls)
            {
                Hull hf = ClipHull(h, n, at), hb = ClipHull(h, -n, at);
                if (hf != null)
                {
                    front.hulls.Add(hf);
                }
                if (hb != null)
                {
                    back.hulls.Add(hb);
                }
            }
            Split(front, depth, level + 1, axis, leaves);
            Split(back, depth, level + 1, axis, leaves);
        }

        private static V Lerp(V a, V b, float t)
        {
            return new V
            {
                p = Vector3.LerpUnclamped(a.p, b.p, t),
                c = Color32.Lerp(a.c, b.c, t),
                uv = Vector3.LerpUnclamped(a.uv, b.uv, t)
            };
        }

        private static void ClipPoly(Poly poly, Vector3 n, Vector3 at, List<Poly> front, List<Poly> back)
        {
            List<V> v = poly.v;
            int count = v.Count;
            bool anyF = false, anyB = false;
            for (int i = 0; i < count; i++)
            {
                float d = Vector3.Dot(v[i].p - at, n);
                anyF |= d > 1e-5f;
                anyB |= d < -1e-5f;
            }
            if (!anyB)
            {
                front.Add(poly);
                return;
            }
            if (!anyF)
            {
                back.Add(poly);
                return;
            }
            Poly f = new Poly { g = poly.g }, bk = new Poly { g = poly.g };
            for (int i = 0; i < count; i++)
            {
                V a = v[i], b = v[(i + 1) % count];
                float da = Vector3.Dot(a.p - at, n), db = Vector3.Dot(b.p - at, n);
                if (da >= 0f)
                {
                    f.v.Add(a);
                }
                if (da <= 0f)
                {
                    bk.v.Add(a);
                }
                if (da > 0f && db < 0f || da < 0f && db > 0f)
                {
                    V m = Lerp(a, b, da / (da - db));
                    f.v.Add(m);
                    bk.v.Add(m);
                }
            }
            if (f.v.Count >= 3)
            {
                front.Add(f);
            }
            if (bk.v.Count >= 3)
            {
                back.Add(bk);
            }
        }

        // The part of a convex solid on the front of the plane, closed by a cap where it is cut; null if none is left.
        private static Hull ClipHull(Hull h, Vector3 n, Vector3 at)
        {
            Hull r = new Hull { col = h.col };
            List<Vector3> cut = new List<Vector3>();
            foreach (List<Vector3> face in h.faces)
            {
                List<Vector3> o = new List<Vector3>(face.Count + 2);
                int count = face.Count;
                for (int i = 0; i < count; i++)
                {
                    Vector3 a = face[i], b = face[(i + 1) % count];
                    float da = Vector3.Dot(a - at, n), db = Vector3.Dot(b - at, n);
                    if (da >= 0f)
                    {
                        o.Add(a);
                    }
                    if (da > 0f && db < 0f || da < 0f && db > 0f)
                    {
                        Vector3 m = Vector3.LerpUnclamped(a, b, da / (da - db));
                        o.Add(m);
                        cut.Add(m);
                    }
                    else if (Mathf.Abs(da) < 1e-6f)
                    {
                        cut.Add(a);
                    }
                }
                if (o.Count >= 3)
                {
                    r.faces.Add(o);
                }
            }
            if (r.faces.Count == 0)
            {
                return null;
            }
            if (cut.Count >= 3)
            {
                Vector3 mid = Vector3.zero;
                foreach (Vector3 p in cut)
                {
                    mid += p;
                }
                mid /= cut.Count;
                Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 w = Vector3.Cross(n, u);
                cut.Sort((p, q) => Mathf.Atan2(Vector3.Dot(p - mid, w), Vector3.Dot(p - mid, u)).CompareTo(Mathf.Atan2(Vector3.Dot(q - mid, w), Vector3.Dot(q - mid, u))));
                r.faces.Add(cut);
            }
            return r;
        }

        // ---- a piece's mesh and flight

        private static readonly List<Vector3> mv = new List<Vector3>();
        private static readonly List<Color32> mc = new List<Color32>();
        private static readonly List<Vector3> mu = new List<Vector3>();
        private static readonly List<List<int>> mt = new List<List<int>>();
        private static readonly List<int> usedGroups = new List<int>();

        private static void Spawn(CellSnap s, Piece p, Vector3 centre)
        {
            Bounds b = BoundsOf(p);
            if (b.size.x < 0.02f && b.size.y < 0.02f && b.size.z < 0.02f)
            {
                return;
            }
            Vector3 pivot = b.center;
            mv.Clear();
            mc.Clear();
            mu.Clear();
            usedGroups.Clear();
            foreach (List<int> l in mt)
            {
                l.Clear();
            }
            foreach (Poly poly in p.polys)
            {
                List<int> tris = TrisFor(poly.g);
                int first = mv.Count;
                foreach (V v in poly.v)
                {
                    mv.Add(v.p - pivot);
                    mc.Add(v.c);
                    mu.Add(v.uv);
                }
                for (int i = 1; i + 1 < poly.v.Count; i++)
                {
                    tris.Add(first);
                    tris.Add(first + i);
                    tris.Add(first + i + 1);
                }
            }
            if (p.hulls.Count > 0)
            {
                List<int> tris = TrisFor(CoreGroup);
                foreach (Hull h in p.hulls)
                {
                    foreach (List<Vector3> f in h.faces)
                    {
                        int first = mv.Count;
                        foreach (Vector3 v in f)
                        {
                            mv.Add(v - pivot);
                            mc.Add(h.col);
                            mu.Add(Vector3.zero);
                        }
                        for (int i = 1; i + 1 < f.Count; i++)
                        {
                            // both sides (as the slab's faces)
                            tris.Add(first);
                            tris.Add(first + i);
                            tris.Add(first + i + 1);
                            tris.Add(first);
                            tris.Add(first + i + 1);
                            tris.Add(first + i);
                        }
                    }
                }
            }
            if (mv.Count == 0 || usedGroups.Count > 8)
            {
                return;
            }
            Mesh mesh = meshPool.Count > 0 ? meshPool.Pop() : new Mesh { name = "IsoRimDebris" };
            mesh.Clear();
            MeshGuard.FitIndices(mesh, mv.Count);
            mesh.SetVertices(mv);
            mesh.SetColors(mc);
            mesh.SetUVs(0, mu);
            mesh.subMeshCount = usedGroups.Count;
            Chunk ch = new Chunk { mesh = mesh, map = s.map, subs = usedGroups.Count, floor = s.floor, half = b.extents };
            int tri = 0;
            for (int i = 0; i < usedGroups.Count; i++)
            {
                mesh.SetTriangles(mt[i], i, false);
                ch.subGroup[i] = usedGroups[i];
                tri += mt[i].Count / 3;
            }
            mesh.RecalculateBounds();
            MeshGuard.Check(mesh, "collapse debris");
            ch.pos = pivot;
            Vector3 away = new Vector3(pivot.x - centre.x, 0f, pivot.z - centre.z);
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.zero;
            float push = s.roof ? 0.25f : 0.4f;
            ch.vel = new Vector3(Rand.Range(-push, push) + away.x * 0.35f, Rand.Range(0f, 0.6f), Rand.Range(-push, push) + away.z * 0.35f);
            ch.spin = Rand.UnitVector3 * (Rand.Range(40f, 150f) * Mathf.Deg2Rad);
            ch.delay = Rand.Range(0f, 0.12f);
            ch.life = Rand.Range(3.5f, 5f);
            ch.land = LandHeight(s.map, ch.pos, ch.pos.y - ch.half.y);
            IntVec3 g = new IntVec3(Mathf.FloorToInt(pivot.x), 0, Mathf.FloorToInt(pivot.z));
            ch.fogged = g.InBounds(s.map) && s.map.fogGrid.IsFogged(g);
            live.Add(ch);
            chunksMade++;
            trisMade += tri;
        }

        private static List<int> TrisFor(int group)
        {
            int i = usedGroups.IndexOf(group);
            if (i < 0)
            {
                usedGroups.Add(group);
                i = usedGroups.Count - 1;
                while (mt.Count <= i)
                {
                    mt.Add(new List<int>());
                }
                mt[i].Clear();
            }
            return mt[i];
        }

        // Where a piece over drawn point p, its bottom at true height bottom, comes to rest: the first floor below it with
        // ground there (an upper floor where its floor is still built), on the top of a wall standing there.
        private static float LandHeight(Map map, Vector3 p, float bottom)
        {
            int gx = Mathf.FloorToInt(p.x), gz = Mathf.FloorToInt(p.z);
            for (int j = heights.Length - 1; j >= 0; j--)
            {
                if (heights[j] > bottom - 0.05f)
                {
                    continue;
                }
                IntVec3 c = new IntVec3(gx, 0, gz + shifts[j]);
                if (!c.InBounds(map))
                {
                    continue;
                }
                if (j > 0 && !Floors.HasFloor(map, c))
                {
                    continue;
                }
                float wall;
                if (Floors.FullBlock(map, c, out wall) && heights[j] + wall <= bottom + 0.05f)
                {
                    return heights[j] + wall;
                }
                return heights[j];
            }
            return 0f;
        }

        // How far a box of half size h turned by q reaches below its middle.
        private static float Below(Quaternion q, Vector3 h)
        {
            float r10 = 2f * (q.x * q.y + q.w * q.z);
            float r11 = 1f - 2f * (q.x * q.x + q.z * q.z);
            float r12 = 2f * (q.y * q.z - q.w * q.x);
            return Mathf.Abs(r10) * h.x + Mathf.Abs(r11) * h.y + Mathf.Abs(r12) * h.z;
        }

        // The turn that lays the piece down: of its sides facing down enough, the one that leaves it lowest (a tall
        // piece that tipped stays on its long side, not stood back up); else the side facing most downward.
        private static readonly Vector3[] Axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        private static Quaternion Flat(Quaternion q, Vector3 half)
        {
            Vector3 best = Vector3.zero, lowest = Vector3.down;
            float bestH = float.MaxValue, low = float.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                Vector3 w = q * Axes[i];
                float h = i < 2 ? half.x : i < 4 ? half.y : half.z;
                if (w.y < -0.35f && h < bestH)
                {
                    bestH = h;
                    best = w;
                }
                if (w.y < low)
                {
                    low = w.y;
                    lowest = w;
                }
            }
            return Quaternion.FromToRotation(bestH < float.MaxValue ? best : lowest, Vector3.down) * q;
        }

        private static void Move(Chunk ch, float dt)
        {
            ch.age += dt;
            if (ch.age < ch.delay)
            {
                return;
            }
            switch (ch.state)
            {
                case 0:
                {
                    ch.vel.y -= Gravity * dt;
                    ch.pos += ch.vel * dt;
                    float ang = ch.spin.magnitude;
                    if (ang > 1e-4f)
                    {
                        ch.rot = Quaternion.AngleAxis(ang * dt * Mathf.Rad2Deg, ch.spin / ang) * ch.rot;
                    }
                    float below = Below(ch.rot, ch.half);
                    if (ch.pos.y - below < ch.land)
                    {
                        ch.pos.y = ch.land + below;
                        if (ch.vel.y < 0f)
                        {
                            ch.bounces++;
                            ch.vel.y = -ch.vel.y * Restitution;
                            ch.vel.x *= 0.5f;
                            ch.vel.z *= 0.5f;
                            ch.spin *= 0.45f;
                            if (ch.vel.y < 0.8f || ch.bounces >= 2)
                            {
                                ch.vel = Vector3.zero;
                                ch.spin = Vector3.zero;
                                ch.from = ch.rot;
                                ch.to = Flat(ch.rot, ch.half);
                                ch.t = 0f;
                                ch.state = 1;
                            }
                        }
                    }
                    break;
                }
                case 1:
                    ch.t += dt / SettleTime;
                    ch.rot = Quaternion.Slerp(ch.from, ch.to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ch.t)));
                    ch.pos.y = ch.land + Below(ch.rot, ch.half);
                    if (ch.t >= 1f)
                    {
                        ch.state = 2;
                    }
                    break;
                case 2:
                    if (ch.age >= ch.life)
                    {
                        ch.state = 3;
                        ch.t = 0f;
                    }
                    break;
                case 3:
                    ch.t += dt / ShrinkTime;
                    ch.scale = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ch.t));
                    ch.pos.y = ch.land + Below(ch.rot, ch.half) * ch.scale;
                    break;
            }
            // The floor under a piece may go too (a collapse goes on down): it falls on.
            ch.recheck -= dt;
            if (ch.recheck <= 0f && ch.state < 3)
            {
                ch.recheck = 0.3f;
                float land = LandHeight(ch.map, ch.pos, ch.pos.y - Below(ch.rot, ch.half));
                if (land < ch.land - 0.01f)
                {
                    ch.land = land;
                    if (ch.state != 0)
                    {
                        ch.state = 0;
                        ch.spin = Rand.UnitVector3 * (Rand.Range(20f, 60f) * Mathf.Deg2Rad);
                        ch.bounces = 0;
                    }
                }
            }
        }

        private static void Free(int i)
        {
            Chunk ch = live[i];
            live.RemoveAt(i);
            if (ch.mesh != null)
            {
                if (meshPool.Count < 512)
                {
                    ch.mesh.Clear();
                    meshPool.Push(ch.mesh);
                }
                else
                {
                    UnityEngine.Object.Destroy(ch.mesh);
                }
            }
        }

        public static void Clear()
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Free(i);
            }
            batches.Clear();
        }

        public static void Advance(float seconds)
        {
            advance += Mathf.Max(0f, seconds);
        }

        // ---- every frame (View3D.Tick, while the 3D view draws)

        public static void Frame()
        {
            if (live.Count == 0 && batches.Count == 0)
            {
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.collapseDebris)
            {
                Clear();
                return;
            }
            try
            {
                if (map == null)
                {
                    batches.Clear();
                    return;
                }
                Process(map);
                Bands(map);
                float dt;
                if (Hold)
                {
                    dt = advance;
                    advance = 0f;
                }
                else
                {
                    TickManager tm = Find.TickManager;
                    dt = tm.Paused ? 0f : Mathf.Min(Time.deltaTime, 0.1f) * Mathf.Min(tm.TickRateMultiplier, 3f);
                }
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    Chunk ch = live[i];
                    if (ch.map != map)
                    {
                        Free(i);
                        continue;
                    }
                    float left = dt;
                    while (left > 1e-6f)
                    {
                        float step = Mathf.Min(Step, left);
                        Move(ch, step);
                        left -= step;
                    }
                    if (ch.state == 3 && ch.t >= 1f)
                    {
                        Free(i);
                    }
                }
                Draw();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Collapse debris failed: " + e, 93611703);
                Clear();
            }
            if (live.Count > 0)
            {
                double ms = watch.Elapsed.TotalMilliseconds;
                frames++;
                frameMsSum += ms;
                frameMsMax = Math.Max(frameMsMax, ms);
            }
        }

        private static void Draw()
        {
            float s = View3D.Squash;
            Vector3 squash = new Vector3(1f, 1f / s, 1f);
            bool was = Floors.InDrawnSpace;
            Floors.InDrawnSpace = true;   // worked out in drawn space
            try
            {
                for (int i = 0; i < live.Count; i++)
                {
                    Chunk ch = live[i];
                    if (ch.fogged || ch.age < 0f || !Floors.Shown(Mathf.Min(FloorAt(ch.pos.y), ch.floor)))
                    {
                        continue;
                    }
                    Matrix4x4 m = Matrix4x4.TRS(new Vector3(ch.pos.x, ch.pos.y / s, ch.pos.z), Quaternion.identity, squash)
                        * Matrix4x4.TRS(Vector3.zero, ch.rot, new Vector3(ch.scale, ch.scale, ch.scale));
                    for (int k = 0; k < ch.subs; k++)
                    {
                        Group g = groups[ch.subGroup[k]];
                        if (g.draw == null)
                        {
                            continue;
                        }
                        Graphics.DrawMesh(ch.mesh, m, g.draw, g.layer, null, k);
                        if (g.mark != null)
                        {
                            Graphics.DrawMesh(ch.mesh, m, g.mark, g.layer, null, k);
                        }
                    }
                }
            }
            finally
            {
                Floors.InDrawnSpace = was;
            }
        }

        // The floor whose storey holds a true height (0 the ground).
        private static int FloorAt(float y)
        {
            for (int k = heights.Length - 1; k >= 1; k--)
            {
                if (y >= heights[k] - 0.02f)
                {
                    return k;
                }
            }
            return 0;
        }

        public static string Stats()
        {
            return "snapshots " + snapshots + ", cells " + cellsTaken + ", pieces made " + chunksMade + " (" + trisMade + " triangles), live " + live.Count
                + ", dropped " + dropped + ", snapshot " + snapMs.ToString("0.0") + " ms (max a tick " + snapMaxMs.ToString("0.0") + " ms), pieces built " + buildMs.ToString("0.0") + " ms (max a frame " + buildMaxMs.ToString("0.0") + " ms, a cell " + breakMaxMs.ToString("0.0") + " ms), frame "
                + (frames > 0 ? (frameMsSum / frames).ToString("0.000") : "-") + " ms avg / " + frameMsMax.ToString("0.000") + " ms max over " + frames + " frames";
        }

        public static void ResetStats()
        {
            snapshots = cellsTaken = chunksMade = trisMade = dropped = frames = 0;
            buildMs = buildMaxMs = frameMsSum = frameMsMax = snapMs = snapTickMs = snapMaxMs = breakMaxMs = 0;
        }
    }
}
