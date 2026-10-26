using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The cells of a gravship (ShipFlight): the valid substructure (Deck), the hull corner pieces standing beside it and
    // the cells of buildings on it (Ship), and the ring of cells round the deck (Ring). Made once when the cutscene
    // starts, while the ship still stands on the map.
    //
    // A ship can have upper decks on stacked floors (IsoRim's floor API, Api.Floors: floor k's cells are rows of the same
    // map, shifts[k] rows north of the ground cell under them). The classes are kept per floor over the ground's cells
    // (the ship stands in one column), and every lookup by a map point takes its row to its floor first. On maps without
    // floors, and for a ship without upper decks, there is only the ground: everything works as it always did.
    public sealed class ShipCells
    {
        public const byte Ring = 1, Ship = 2, Deck = 3;
        private static readonly int[] DX = { 0, 0, 0, -1, 1, -1, 1, -1, 1 };
        private static readonly int[] DZ = { 0, -1, 1, 0, 0, -1, -1, 1, 1 };

        public CellRect bounds;      // the substructure's bounds (on the ground) widened by one cell (vanilla's capture bounds)
        public Vector3 pivot;        // their centre: the cutscene's centre
        public CellRect area;        // bounds widened a little more: where the classes are kept
        public int top;              // the ship's highest floor (0: one storey)
        public int engineFloor;      // the floor the grav engine stands on
        public int groundRows;       // the map's ground rows (0 without floors)
        public float[] heights;      // floor k's height (true cells), [0] the ground
        public int[] shifts;         // floor k's rows north of the ground's
        private byte[][] cls;        // per floor up to top
        private readonly Dictionary<int, Vector2> corners = new Dictionary<int, Vector2>();   // floor * area + cell: toward its two hull legs

        public static ShipCells Of(Building_GravEngine engine)
        {
            Map map = engine.Map;
            HashSet<IntVec3> deck = engine.ValidSubstructure;
            ShipCells s = new ShipCells();
            int floors = Floors.Bands(map, out s.groundRows, out s.heights, out s.shifts);
            // Each cell to its floor and the ground cell under it.
            List<IntVec3> ground = new List<IntVec3>(deck.Count);
            List<int> floorOf = new List<int>(deck.Count);
            List<IntVec3> raw = new List<IntVec3>(deck.Count);
            foreach (IntVec3 c in deck)
            {
                int k = s.FloorOfRow(c.z);
                if (k < 0 || k > floors)
                {
                    continue;
                }
                raw.Add(c);
                floorOf.Add(k);
                ground.Add(new IntVec3(c.x, 0, c.z - s.shifts[k]));
                s.top = Mathf.Max(s.top, k);
            }
            s.engineFloor = Mathf.Max(0, s.FloorOfRow(engine.Position.z));
            s.bounds = CellRect.FromCellList(ground).ExpandedBy(1);
            s.pivot = s.bounds.CenterVector3;
            s.pivot.y = 0f;
            s.area = s.bounds.ExpandedBy(4);
            s.cls = new byte[s.top + 1][];
            for (int k = 0; k <= s.top; k++)
            {
                s.cls[k] = new byte[s.area.Width * s.area.Height];
            }
            for (int i = 0; i < raw.Count; i++)
            {
                s.cls[floorOf[i]][s.Index(ground[i].x, ground[i].z)] = Deck;
            }
            for (int k = 0; k <= s.top; k++)
            {
                byte[] f = s.cls[k];
                foreach (IntVec3 g in s.bounds)
                {
                    int i = s.Index(g.x, g.z);
                    IntVec3 c = new IntVec3(g.x, 0, g.z + s.shifts[k]);
                    RimWorld.SectionLayer_GravshipHull.CornerType type;
                    IntVec3 a, b;
                    if (f[i] == 0 && (k == 0 || s.FloorOfRow(c.z) == k) && c.InBounds(map) && c.GetEdifice(map) == null
                        && HullCorners.At(map, c, out type, out _) && HullCorners.Legs(type, out a, out b))
                    {
                        f[i] = Ship;
                        s.corners[k * f.Length + i] = new Vector2(a.x + b.x, a.z + b.z);
                    }
                }
            }
            for (int n = 0; n < raw.Count; n++)
            {
                int k = floorOf[n];
                List<Thing> things = raw[n].GetThingList(map);
                for (int j = 0; j < things.Count; j++)
                {
                    if (things[j] is Pawn || things[j].def.category != ThingCategory.Building)
                    {
                        continue;
                    }
                    foreach (IntVec3 o in things[j].OccupiedRect())
                    {
                        int i = s.Index(o.x, o.z - s.shifts[k]);
                        if (i >= 0 && s.cls[k][i] < Ship)
                        {
                            s.cls[k][i] = Ship;
                        }
                    }
                }
            }
            for (int n = 0; n < raw.Count; n++)
            {
                int k = floorOf[n];
                for (int d = 1; d < 9; d++)
                {
                    int i = s.Index(ground[n].x + DX[d], ground[n].z + DZ[d]);
                    if (i >= 0 && s.cls[k][i] == 0)
                    {
                        s.cls[k][i] = Ring;
                    }
                }
            }
            return s;
        }

        // The floor of a map row: 0 the ground (and every row without floors), k, or -1 between floors.
        public int FloorOfRow(int z)
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

        // Floor k's height (true cells); 0 for the ground or a floor the map does not have.
        public float Height(int k)
        {
            return k > 0 && heights != null && k < heights.Length ? heights[k] : 0f;
        }

        // Floor k drawn over the ground: its rows moved down to the ground's and raised by its height (drawn space,
        // as Floors draws it).
        public Matrix4x4 Lift(int k)
        {
            return k <= 0 ? Matrix4x4.identity : Matrix4x4.Translate(new Vector3(0f, heights[k] / View3D.Squash, -shifts[k]));
        }

        private int Index(int x, int z)
        {
            return x < area.minX || z < area.minZ || x > area.maxX || z > area.maxZ ? -1 : (z - area.minZ) * area.Width + (x - area.minX);
        }

        // The class of the cell under a map point (x, z in the map's own rows: an upper floor's row is that floor's cell).
        public byte At(float x, float z)
        {
            if (top == 0)
            {
                int i0 = Index(Mathf.FloorToInt(x), Mathf.FloorToInt(z));
                return i0 < 0 ? (byte)0 : cls[0][i0];
            }
            int k = FloorOfRow(Mathf.FloorToInt(z));
            return k < 0 || k > top ? (byte)0 : At(k, x, z - shifts[k]);
        }

        // The class of floor k's cell over the ground point (x, z).
        public byte At(int k, float x, float z)
        {
            if (k < 0 || k > top)
            {
                return 0;
            }
            int i = Index(Mathf.FloorToInt(x), Mathf.FloorToInt(z));
            return i < 0 ? (byte)0 : cls[k][i];
        }

        // A draw (its matrix: the game's own, in the map's rows, or IsoRim's, already moved over the ground and raised)
        // whose origin is on one of the ship's cells; drawn: the same moved over the ground and raised (drawn space), as
        // the snapshot keeps everything.
        public bool Holds(Matrix4x4 matrix, out Matrix4x4 drawn)
        {
            drawn = matrix;
            float x = matrix.m03, z = matrix.m23;
            if (top == 0)
            {
                return At(x, z) >= Ship;
            }
            if (z >= groundRows)
            {
                int k = FloorOfRow(Mathf.FloorToInt(z));
                if (k <= 0 || k > top || At(k, x, z - shifts[k]) < Ship)
                {
                    return false;
                }
                drawn = Lift(k) * matrix;
                return true;
            }
            // Drawn space (or the ground): the floor by the height it stands at.
            float y = matrix.m13 * View3D.Squash;
            for (int k = top; k >= 1; k--)
            {
                if (y >= heights[k] - 0.25f && At(k, x, z) >= Ship)
                {
                    return true;
                }
            }
            return At(0, x, z) >= Ship;
        }

        // Floor k's deck cells (ground coordinates), and whether a cell is deck.
        public IEnumerable<IntVec3> DeckCells(int k = 0)
        {
            foreach (IntVec3 c in bounds)
            {
                if (IsDeck(c.x, c.z, k))
                {
                    yield return c;
                }
            }
        }

        public bool IsDeck(int x, int z, int k = 0)
        {
            return At(k, x + 0.5f, z + 0.5f) == Deck;
        }

        // A hull corner cell of floor k (its wedge stands toward its two hull legs, legs = their two directions added).
        public bool Corner(int x, int z, out Vector2 legs, int k = 0)
        {
            int i = Index(x, z);
            legs = Vector2.zero;
            return i >= 0 && k >= 0 && k <= top && corners.TryGetValue(k * cls[k].Length + i, out legs);
        }

        public IEnumerable<IntVec3> CornerCells(int k = 0)
        {
            foreach (IntVec3 c in bounds)
            {
                Vector2 legs;
                if (Corner(c.x, c.z, out legs, k))
                {
                    yield return c;
                }
            }
        }

        // How much of the cell's ground the ship covers at a point inside it (local 0-1), on any of its floors: all of a
        // deck or building cell, the half of a corner cell toward its hull legs (the wedge).
        public bool Covers(int x, int z, float u, float v)
        {
            int i = Index(x, z);
            if (i < 0)
            {
                return false;
            }
            for (int k = 0; k <= top; k++)
            {
                if (cls[k][i] < Ship)
                {
                    continue;
                }
                Vector2 legs;
                if (!corners.TryGetValue(k * cls[k].Length + i, out legs) || (u - 0.5f) * legs.x + (v - 0.5f) * legs.y >= 0f)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // The ship's moving parts in the frame before it leaves (ShipFlight): pawns, animals, door leaves, the engine's
    // orb, spinners, held and carried things, glass. Everything IsoRim draws per frame passes one of its chokepoints
    // (IsoGlow.DrawLit: DynamicModels and the sprite slabs; SpriteSolids' faces; Glass; DrawHook: the game's own pawn
    // and sprite drawing, rewritten). While recording, each of them notes its draw here when the matrix puts its origin
    // on one of the ship's cells. The last whole frame is kept: the game is paused through the cutscene, so it is the
    // pose the player saw.
    public static class ShipCapture
    {
        public struct Rec
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material mat;
            public MaterialPropertyBlock props;
        }

        // Set while the chokepoints call Note (recording, hiding or both).
        public static bool Recording;
        private static bool storing;
        private static bool hiding;
        private static ShipCells cells;
        private static List<Rec> cur = new List<Rec>();
        private static List<Rec> last = new List<Rec>();
        private static int curFrame = -1;

        // Records the ship's moving parts; with hide (a landing ship, placed on the map under the frozen ground until
        // touchdown) they are not drawn either.
        public static void Start(ShipCells c, bool hide)
        {
            cells = c;
            cur.Clear();
            last.Clear();
            curFrame = -1;
            storing = c != null;
            hiding = c != null && hide;
            Recording = c != null;
        }

        // Stops storing draws (the snapshot has its frame) but keeps hiding them, if it did.
        public static void Hold()
        {
            storing = false;
            cur.Clear();
            last.Clear();
            Recording = hiding;
        }

        public static void Stop()
        {
            Recording = false;
            storing = false;
            hiding = false;
            cells = null;
            cur.Clear();
            last.Clear();
        }

        // Notes a draw. True: it belongs to a ship being hidden, so the caller must not draw it. Kept in drawn space (a
        // draw on an upper deck, given in its floor's rows, moved over the ground and raised: ShipCells.Holds).
        public static bool Note(Mesh mesh, Matrix4x4 matrix, Material mat, MaterialPropertyBlock props)
        {
            if (!Recording || mesh == null || mat == null || cells == null)
            {
                return false;
            }
            Matrix4x4 drawn;
            if (!cells.Holds(matrix, out drawn))
            {
                return false;
            }
            if (storing)
            {
                Store(new Rec { mesh = mesh, matrix = drawn, mat = mat, props = props });
            }
            return hiding;
        }

        private static void Store(Rec r)
        {
            int f = Time.frameCount;
            if (f != curFrame)
            {
                List<Rec> t = last;
                last = cur;
                cur = t;
                cur.Clear();
                curFrame = f;
            }
            cur.Add(r);
        }

        // The last whole frame: the latest one if the game has moved on to a new frame since, else the one before.
        public static List<Rec> Take()
        {
            return Time.frameCount != curFrame ? new List<Rec>(cur) : new List<Rec>(last);
        }
    }

    // The 3D "terrain curtain" of a landing (ShipFlight): vanilla photographs the landing area just before the ship is
    // placed (GravshipCapturer.BeginTerrainRender) and lays that picture over the map until touchdown. In 3D the
    // sections over that area are frozen instead: each layer's finished meshes are kept as they are and drawn in place of
    // the layer's own from then on, while the layer gets a fresh list and is rebuilt into it at once (so it carries on as
    // usual: the placed ship goes there, and the snapshot reads it). At touchdown the frozen meshes are dropped and the
    // live ones show again. Nothing is copied: the old meshes are only kept.
    public static class ShipFreeze
    {
        public static bool Active;
        private static readonly Dictionary<MapDrawLayer, List<LayerSubMesh>> frozen = new Dictionary<MapDrawLayer, List<LayerSubMesh>>();
        private static readonly HarmonyLib.AccessTools.FieldRef<Section, List<SectionLayer>> sectionLayers =
            HarmonyLib.AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        public static int Sections { get; private set; }

        // rect: the landing area on the ground. top: the landing ship's highest floor; its upper decks are placed under
        // the frozen picture too, so the same area on each of its floors is frozen (and those blocks' slabs held).
        public static void Freeze(Map map, CellRect rect, int top = 0)
        {
            Unfreeze();
            System.Array sections = MapSections.SectionsOf(map);
            if (sections == null)
            {
                return;
            }
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            int groundRows;
            float[] heights;
            int[] shifts;
            int floors = Floors.Bands(map, out groundRows, out heights, out shifts);
            HashSet<Section> done = new HashSet<Section>();
            int n = 0;
            for (int k = 0; k <= Mathf.Min(top, floors); k++)
            {
                CellRect r = rect.ClipInsideMap(map);
                if (k > 0)
                {
                    // the same ground area on floor k: its rows, kept inside the floor
                    r = new CellRect(rect.minX, rect.minZ, rect.Width, rect.Height);
                    r.minZ = Mathf.Max(r.minZ, 0) + shifts[k];
                    r.maxZ = Mathf.Min(r.maxZ, groundRows - 1) + shifts[k];
                    r = r.ClipInsideMap(map);
                    if (r.maxZ < r.minZ)
                    {
                        continue;
                    }
                }
                for (int sx = r.minX / Section.Size; sx <= r.maxX / Section.Size && sx < sections.GetLength(0); sx++)
                {
                    for (int sz = r.minZ / Section.Size; sz <= r.maxZ / Section.Size && sz < sections.GetLength(1); sz++)
                    {
                        Section sec = sections.GetValue(sx, sz) as Section;
                        if (sec == null || !done.Add(sec))
                        {
                            continue;
                        }
                        if (k > 0)
                        {
                            Floors.HoldSlab(sec, true);
                            held.Add(sec);
                        }
                        bool any = false;
                        foreach (SectionLayer layer in sectionLayers(sec))
                        {
                            if (layer is SectionLayer_Dynamic || !layer.Visible)
                            {
                                continue;
                            }
                            frozen[layer] = layer.subMeshes;
                            layer.subMeshes = new List<LayerSubMesh>();
                            any = true;
                        }
                        if (any)
                        {
                            sec.RegenerateAllLayers();
                            n++;
                        }
                    }
                }
            }
            Sections = n;
            Active = frozen.Count > 0;
            ModLog.Note("[IsoRim] Gravship landing: froze " + n + " sections (" + frozen.Count + " layers) over " + rect + (top > 0 ? " on floors 0-" + Mathf.Min(top, floors) : "") + " in " + watch.ElapsedMilliseconds + " ms");
        }

        // Upper blocks whose slab is held while frozen.
        private static readonly List<Section> held = new List<Section>();

        private static void LetSlabsGo()
        {
            foreach (Section s in held)
            {
                Floors.HoldSlab(s, false);
            }
            held.Clear();
        }

        // The frozen ground still shows what stood where the ship lands (trees, rock, plants, buildings: placing the ship
        // cleared them). Once the ship comes down low over its place, those would rise through its deck: the frozen
        // meshes are filtered then, dropping every triangle that is not flat ground and belongs to a cell inside the
        // ship (its owner cell when tagged, as ShipSnapshot reads them, else the cell under its middle).
        public static void CutOut(ShipCells cells)
        {
            float squash = View3D.Squash;
            int dropped = 0;
            foreach (List<LayerSubMesh> subs in frozen.Values)
            {
                for (int k = 0; k < subs.Count; k++)
                {
                    LayerSubMesh sm = subs[k];
                    if (sm == null || !sm.finalized || sm.disabled || sm.verts.Count == 0)
                    {
                        continue;
                    }
                    List<Vector3> v = sm.verts;
                    List<int> t = sm.tris;
                    List<Vector3> tag = sm.uvsChannelTwo.Count == v.Count ? sm.uvsChannelTwo : null;
                    List<int> keep = null;
                    for (int i = 0; i + 2 < t.Count; i += 3)
                    {
                        Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                        bool flat = a.y * squash < 0.02f && b.y * squash < 0.02f && c.y * squash < 0.02f;
                        bool inShip = false;
                        if (!flat)
                        {
                            int flags = tag != null ? (int)(tag[t[i]].z + 0.5f) : 0;
                            inShip = flags != 0
                                ? cells.At(Mathf.Floor(tag[t[i]].x) + 0.5f, Mathf.Floor(tag[t[i]].y) + 0.5f) >= ShipCells.Ship
                                : cells.At((a.x + b.x + c.x) / 3f, (a.z + b.z + c.z) / 3f) >= ShipCells.Ship;
                        }
                        if (inShip)
                        {
                            if (keep == null)
                            {
                                keep = new List<int>(t.Count);
                                for (int j = 0; j < i; j++)
                                {
                                    keep.Add(t[j]);
                                }
                            }
                            dropped++;
                        }
                        else if (keep != null)
                        {
                            keep.Add(t[i]);
                            keep.Add(t[i + 1]);
                            keep.Add(t[i + 2]);
                        }
                    }
                    if (keep == null)
                    {
                        continue;
                    }
                    UnityEngine.Object.Destroy(sm.mesh);
                    if (keep.Count == 0)
                    {
                        sm.mesh = new Mesh();
                        sm.disabled = true;
                        continue;
                    }
                    // A new mesh from the kept CPU lists (as LayerSubMesh.FinalizeMesh makes it).
                    Mesh m = new Mesh();
                    MeshGuard.FitIndices(m, v.Count);
                    m.SetVertices(v);
                    m.SetTriangles(keep, 0);
                    if (sm.colors.Count == v.Count)
                    {
                        m.SetColors(sm.colors);
                    }
                    if (sm.uvs.Count == v.Count)
                    {
                        m.SetUVs(0, sm.uvs);
                    }
                    if (tag != null)
                    {
                        m.SetUVs(1, tag);
                    }
                    if (sm.normals.Count == v.Count)
                    {
                        m.SetNormals(sm.normals);
                    }
                    m.RecalculateBounds();
                    MeshGuard.Check(m, "frozen ground");
                    sm.mesh = m;
                    sm.tris.Clear();
                    sm.tris.AddRange(keep);
                }
            }
            ModLog.Note("[IsoRim] Gravship landing: " + dropped + " triangles of what stood under the ship taken out of the frozen ground");
        }

        // Lets the frozen meshes of some layers go early (the live ones show again).
        public static void Thaw(Predicate<MapDrawLayer> which)
        {
            List<MapDrawLayer> gone = new List<MapDrawLayer>();
            foreach (KeyValuePair<MapDrawLayer, List<LayerSubMesh>> kv in frozen)
            {
                if (which(kv.Key))
                {
                    foreach (LayerSubMesh sm in kv.Value)
                    {
                        if (sm != null && sm.mesh != null)
                        {
                            UnityEngine.Object.Destroy(sm.mesh);
                        }
                    }
                    gone.Add(kv.Key);
                }
            }
            foreach (MapDrawLayer l in gone)
            {
                frozen.Remove(l);
            }
            Active = frozen.Count > 0;
        }

        public static void Unfreeze()
        {
            LetSlabsGo();
            if (frozen.Count == 0)
            {
                Active = false;
                return;
            }
            foreach (KeyValuePair<MapDrawLayer, List<LayerSubMesh>> kv in frozen)
            {
                foreach (LayerSubMesh sm in kv.Value)
                {
                    if (sm != null && sm.mesh != null)
                    {
                        UnityEngine.Object.Destroy(sm.mesh);
                    }
                }
            }
            frozen.Clear();
            Active = false;
        }

        // MapDrawLayer.DrawLayer, first of all prefixes (IsoRim's own DrawLayer prefix then draws the frozen meshes too):
        // the frozen list in place for the draw, the live one back after it.
        public static void DrawLayer_Prefix(MapDrawLayer __instance, out List<LayerSubMesh> __state)
        {
            __state = null;
            List<LayerSubMesh> old;
            if (Active && frozen.TryGetValue(__instance, out old))
            {
                __state = __instance.subMeshes;
                __instance.subMeshes = old;
            }
        }

        // A finalizer, so the live list goes back even if the draw throws.
        public static System.Exception DrawLayer_Finalizer(MapDrawLayer __instance, List<LayerSubMesh> __state, System.Exception __exception)
        {
            if (__state != null)
            {
                __instance.subMeshes = __state;
            }
            return __exception;
        }
    }
}
