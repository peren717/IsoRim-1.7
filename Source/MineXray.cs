using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // X-ray of the floors next to the viewed one (stacked floors, Floors), both ways: with the game's Mine tool in hand
    // (Designator_Mine, MineVein too) the floor directly below the viewed one and the floor directly above it are drawn
    // faintly, each on its own floor's plane (where its cells really are in the 3D view, so the floor below shows lower
    // and the floor above higher than the viewed one): their open cells (tunnels, rooms, mountain tops, built floors)
    // outlined, with a light fill, rock and walls left empty; the pawns there as dots. Below: only under what the viewed
    // floor covers (its rock, floors, buildings); below the open air the floor is seen anyway. Above: everywhere (the floors
    // above the viewed one are not drawn). While placing a def an addon registered (Api.XrayPlacing: its stairs show the floor
    // above, where the exit lands) and with other mods' designators (Api.XrayDesignator), the floors they ask for are shown,
    // without the crosshair. A
    // crosshair (Mine tool) runs Reach cells along x and z from the pointer's cell on the viewed floor, all on the plane
    // of the pointer's frame (so on screen its arms run straight through the pointer). All on the overlay shader (no depth test), so the rock never hides it. Only what the player
    // knows is shown: fogged cells count as solid and pawns on them are left out.
    //
    // Cheap per frame: the outline is one mesh per floor shown, remade only when something on the map that changes it
    // (buildings, terrain, fog: Floors.MapMeshDirty_Postfix -> Dirty) has happened, at most every RebuildSeconds.
    [StaticConstructorOnStartup]
    public static class MineXray
    {
        public const int Reach = 5;                 // crosshair cells each way from the pointer
        private const float RebuildSeconds = 0.5f;
        private const float Line = 0.07f;           // outline width (cells)

        private static readonly Color Edge = new Color(0.55f, 0.85f, 1f, 0.6f);
        private static readonly Color Fill = new Color(0.55f, 0.85f, 1f, 0.14f);
        private static readonly Color EdgeUp = new Color(1f, 0.7f, 0.95f, 0.6f);
        private static readonly Color FillUp = new Color(1f, 0.7f, 0.95f, 0.12f);
        private static readonly Color Cross = new Color(1f, 0.9f, 0.45f, 0.18f);
        private static readonly Color CrossCell = new Color(1f, 0.9f, 0.45f, 0.35f);
        private static readonly Color Colonist = new Color(0.6f, 1f, 0.6f, 0.85f);
        private static readonly Color Other = new Color(1f, 0.55f, 0.45f, 0.85f);

        // One floor's outline (below or above the viewed one).
        private class Layer
        {
            public Mesh mesh;
            public Map map;
            public int floor = -1;
            public int viewed = -1;
            public int rows;
            public bool dirty = true;
            public float builtAt = -100f;
            public int quads;
        }

        private static readonly Layer down = new Layer();
        private static readonly Layer up = new Layer();
        private static Mesh quad;
        private static Mesh disc;
        private static Material mat;
        private static MaterialPropertyBlock block;
        private static int colorId;

        // Harness: drawn this frame (probe readout). Shown: anything; ShownDown / ShownUp: the floor below / above.
        public static bool Shown, ShownDown, ShownUp;
        public static int OutlineQuads, OutlineQuadsUp;
        public static double BuildMs;               // the last outline rebuild
        public static int Builds;

        // Something that can change the outline happened somewhere on the map (cheap: a flag).
        public static void Dirty(ulong flags)
        {
            if ((flags & ((ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.FogOfWar)) != 0)
            {
                down.dirty = true;
                up.dirty = true;
            }
        }

        public static bool Tool(Designator d)
        {
            return d is Designator_Mine;   // Designator_MineVein derives from it
        }

        // Other designators that show the x-ray (Api.XrayDesignator), with the floors they show.
        private static readonly List<KeyValuePair<Type, int>> others = new List<KeyValuePair<Type, int>>();
        private const int ShowBelow = 1, ShowAbove = 2;

        public static void Register(Type t, bool below, bool above)
        {
            if (t == null)
            {
                return;
            }
            others.RemoveAll(e => e.Key == t);
            if (below || above)
            {
                others.Add(new KeyValuePair<Type, int>(t, (below ? ShowBelow : 0) | (above ? ShowAbove : 0)));
            }
        }

        // The floors a registered designator shows (0: not registered).
        private static int OtherTool(Designator d)
        {
            for (int i = 0; d != null && i < others.Count; i++)
            {
                if (others[i].Key.IsInstanceOfType(d))
                {
                    return others[i].Value;
                }
            }
            return 0;
        }

        // (Api 20, Api.XrayPlacing) Defs whose placing shows the x-ray (an addon's stairs: the floor above, where the exit
        // lands), with the floors they show.
        private static readonly Dictionary<BuildableDef, int> placing = new Dictionary<BuildableDef, int>();

        public static void RegisterPlacing(BuildableDef def, bool below, bool above)
        {
            if (def == null)
            {
                return;
            }
            if (below || above)
            {
                placing[def] = (below ? ShowBelow : 0) | (above ? ShowAbove : 0);
            }
            else
            {
                placing.Remove(def);
            }
        }

        // The floors shown while this designator places a registered def (0: not one).
        private static int PlacingTool(Designator d)
        {
            BuildableDef def = placing.Count > 0 ? (d as Designator_Place)?.PlacingDef : null;
            return def != null && placing.TryGetValue(def, out int show) ? show : 0;
        }

        // Every frame while the 3D view draws (View3D, with the drag box).
        public static void Draw()
        {
            Shown = ShownDown = ShownUp = false;
            Map map = Find.CurrentMap;
            DesignatorManager dm = Find.DesignatorManager;
            IsoRimSettings st = IsoRimMod.Settings;
            if (!Floors.On || map == null || dm == null || !st.mineXray && !st.mineCrosshair)
            {
                return;
            }
            Designator sel = dm.SelectedDesignator;
            bool mine = Tool(sel);
            // the floors shown: Mine both, others as registered (placing a def: XrayPlacing, wins; a designator: XrayDesignator)
            int show = mine ? ShowBelow | ShowAbove : PlacingTool(sel);
            if (show == 0)
            {
                show = OtherTool(sel);
            }
            if (show == 0)
            {
                return;
            }
            int k = Floors.PointerTop;
            int rows = Floors.GroundRowsOf(map);
            if (rows <= 0)
            {
                return;
            }
            int below = (show & ShowBelow) != 0 && k >= 1 ? k - 1 : -1;
            int above = (show & ShowAbove) != 0 && k + 1 <= Floors.Count ? k + 1 : -1;
            if (below < 0 && above < 0)
            {
                return;
            }
            Floors.FloorPlane(k, out float hK, out int zK);

            IntVec3 cell = Interaction.DesignatorMouseCell();
            if (Mat == null)
            {
                return;
            }
            if (st.mineXray && below >= 0)
            {
                Floors.FloorPlane(below, out float hB, out int zB);
                ShownDown = DrawLayer(map, down, below, k, hB, zB, zK, rows, true);
            }
            if (st.mineXray && above >= 0)
            {
                Floors.FloorPlane(above, out float hA, out int zA);
                ShownUp = DrawLayer(map, up, above, k, hA, zA, zK, rows, false);
            }
            OutlineQuads = down.quads;
            OutlineQuadsUp = up.quads;

            // The crosshair (Mine tool), on the viewed floor's rows: one square per cell, so the cells can be counted.
            bool cross = mine && st.mineCrosshair && cell.InBounds(map) && Floors.FloorOfRow(cell.z) == k;
            if (cross)
            {
                // all on the plane of the pointer's frame, so the arms run straight through the pointer on screen
                Matrix4x4 lift = View3D.LiftMatrix(Interaction.MouseTop + 0.03f);
                for (int i = 1; i <= Reach; i++)
                {
                    CrossSquare(map, lift, cell.x - i, cell.z, k);
                    CrossSquare(map, lift, cell.x + i, cell.z, k);
                    CrossSquare(map, lift, cell.x, cell.z - i, k);
                    CrossSquare(map, lift, cell.x, cell.z + i, k);
                }
                Rect(lift, cell.x + CrossGap, cell.z + CrossGap, cell.x + 1f - CrossGap, cell.z + 1f - CrossGap, CrossCell);
            }
            Shown = ShownDown || ShownUp || cross;
        }

        // One floor's outline and its pawns on that floor's own plane: its rows moved down to the ground's (zF) and lifted to
        // its height hF, as the drawing map draws that floor (Floors.Drawn); done here because the floor above is hidden.
        // Below: only under what the viewed floor covers.
        private static bool DrawLayer(Map map, Layer l, int floor, int k, float hF, int zF, int zK, int rows, bool isBelow)
        {
            if (l.mesh == null || l.map != map || l.floor != floor || l.viewed != k || l.rows != rows
                || l.dirty && Time.realtimeSinceStartup - l.builtAt >= RebuildSeconds)
            {
                Build(map, l, floor, k, zF, zK, rows, isBelow);
            }
            Matrix4x4 lift = View3D.LiftMatrix(hF + 0.03f);
            if (l.mesh != null)
            {
                Graphics.DrawMesh(l.mesh, lift, mat, 0, null, 0, Block(Color.white));
            }
            // Pawns there, where the player can see them.
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                IntVec3 pc = p.Position;
                if (pc.z < zF || pc.z >= zF + rows || Floors.ColumnFloor(map, pc, out float ph, out int pz) != floor || map.fogGrid.IsFogged(pc)
                    || isBelow && !Covered(map, new IntVec3(pc.x, 0, pc.z - zF + zK)))
                {
                    continue;
                }
                Vector3 d = p.DrawPos;
                Matrix4x4 m = lift * Matrix4x4.TRS(new Vector3(d.x, 0f, d.z - zF), Quaternion.identity, new Vector3(0.55f, 1f, 0.55f));
                Graphics.DrawMesh(Disc, m, mat, 0, null, 0, Block(p.Faction != null && p.Faction == Faction.OfPlayer ? Colonist : Other));
            }
            return true;
        }

        // Open = walkable and known (not fogged); on an upper floor also a built floor (not the open air).
        private static bool Open(Map map, IntVec3 c, int floor)
        {
            if (!c.InBounds(map) || map.fogGrid.IsFogged(c) || !c.Walkable(map))
            {
                return false;
            }
            return floor == 0 || !map.terrainGrid.TerrainAt(c).dontRender;
        }

        private static readonly List<Vector3> verts = new List<Vector3>();
        private static readonly List<Color32> cols = new List<Color32>();
        private static readonly List<int> tris = new List<int>();

        // The viewed floor hides what is below this cell: it has a floor there or something built or rock on it (not the
        // open air, where the floor below is seen anyway).
        private static bool Covered(Map map, IntVec3 up)
        {
            return up.InBounds(map) && (!map.terrainGrid.TerrainAt(up).dontRender || up.GetEdifice(map) != null);
        }

        private static void Build(Map map, Layer l, int floor, int k, int zF, int zK, int rows, bool isBelow)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            l.dirty = false;
            l.builtAt = Time.realtimeSinceStartup;
            l.map = map;
            l.floor = floor;
            l.viewed = k;
            l.rows = rows;
            verts.Clear();
            cols.Clear();
            tris.Clear();
            int w = map.Size.x;
            bool[] open = new bool[w * rows];
            bool[] shown = new bool[w * rows];
            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool o = Open(map, new IntVec3(x, 0, z + zF), floor);
                    open[z * w + x] = o;
                    shown[z * w + x] = o && (!isBelow || Covered(map, new IntVec3(x, 0, z + zK)));
                }
            }
            Color32 fill = isBelow ? Fill : FillUp;
            Color32 edge = isBelow ? Edge : EdgeUp;
            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!shown[z * w + x])
                    {
                        continue;
                    }
                    if (x == 0 || !shown[z * w + x - 1])
                    {
                        int run = x + 1;   // the fill: one quad per run of shown cells along the row
                        while (run < w && shown[z * w + run])
                        {
                            run++;
                        }
                        Add(x, z, run, z + 1, fill);
                    }
                    // edges: only toward what is solid there (a tunnel's mouth under open air gets none)
                    if (x == 0 || !open[z * w + x - 1])
                    {
                        Add(x, z, x + Line, z + 1, edge);
                    }
                    if (x == w - 1 || !open[z * w + x + 1])
                    {
                        Add(x + 1 - Line, z, x + 1, z + 1, edge);
                    }
                    if (z == 0 || !open[(z - 1) * w + x])
                    {
                        Add(x, z, x + 1, z + Line, edge);
                    }
                    if (z == rows - 1 || !open[(z + 1) * w + x])
                    {
                        Add(x, z + 1 - Line, x + 1, z + 1, edge);
                    }
                }
            }
            if (l.mesh == null)
            {
                l.mesh = new Mesh();
                l.mesh.name = isBelow ? "IsoRim_MineXray" : "IsoRim_MineXrayUp";
                l.mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }
            l.mesh.Clear();
            l.mesh.SetVertices(verts);
            l.mesh.SetColors(cols);
            l.mesh.SetTriangles(tris, 0);
            l.mesh.RecalculateBounds();
            l.quads = verts.Count / 4;
            BuildMs = sw.Elapsed.TotalMilliseconds;
            Builds++;
        }

        // A flat quad (both sides) in the outline mesh.
        private static void Add(float x0, float z0, float x1, float z1, Color32 c)
        {
            int b = verts.Count;
            verts.Add(new Vector3(x0, 0f, z0));
            verts.Add(new Vector3(x0, 0f, z1));
            verts.Add(new Vector3(x1, 0f, z1));
            verts.Add(new Vector3(x1, 0f, z0));
            cols.Add(c);
            cols.Add(c);
            cols.Add(c);
            cols.Add(c);
            tris.Add(b);
            tris.Add(b + 1);
            tris.Add(b + 2);
            tris.Add(b);
            tris.Add(b + 2);
            tris.Add(b + 3);
        }

        // One crosshair cell, inset so neighbouring squares stay apart; none off the map or off the viewed floor.
        private static void CrossSquare(Map map, Matrix4x4 lift, int x, int z, int k)
        {
            if (x < 0 || x >= map.Size.x || z < 0 || z >= map.Size.z || Floors.FloorOfRow(z) != k)
            {
                return;
            }
            Rect(lift, x + CrossGap, z + CrossGap, x + 1f - CrossGap, z + 1f - CrossGap, Cross);
        }

        private const float CrossGap = 0.08f;   // the gap round each crosshair square, cells

        private static void Rect(Matrix4x4 lift, float x0, float z0, float x1, float z1, Color c)
        {
            Matrix4x4 m = lift * Matrix4x4.TRS(new Vector3((x0 + x1) * 0.5f, 0f, (z0 + z1) * 0.5f), Quaternion.identity, new Vector3(x1 - x0, 1f, z1 - z0));
            Graphics.DrawMesh(Quad, m, mat, 0, null, 0, Block(c));
        }

        private static MaterialPropertyBlock Block(Color c)
        {
            if (block == null)
            {
                block = new MaterialPropertyBlock();
                colorId = Shader.PropertyToID("_Color");
            }
            block.SetColor(colorId, c);
            return block;
        }

        private static Material Mat
        {
            get
            {
                if (mat == null && R3DMaterials.Solid != null)
                {
                    mat = R3DMaterials.Make(R3DMaterials.OverlayDef, "IsoRim_MineXray", 3650);
                }
                return mat;
            }
        }

        // A unit square centred on the origin, flat, both sides.
        private static Mesh Quad
        {
            get
            {
                if (quad == null)
                {
                    quad = new Mesh();
                    quad.SetVertices(new List<Vector3> { new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f) });
                    Color32 wh = new Color32(255, 255, 255, 255);
                    quad.SetColors(new List<Color32> { wh, wh, wh, wh });
                    quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }, 0);
                    quad.RecalculateBounds();
                }
                return quad;
            }
        }

        // A disc one unit across, flat, both sides.
        private static Mesh Disc
        {
            get
            {
                if (disc == null)
                {
                    const int N = 16;
                    List<Vector3> v = new List<Vector3> { Vector3.zero };
                    List<Color32> c = new List<Color32> { new Color32(255, 255, 255, 255) };
                    List<int> t = new List<int>();
                    for (int i = 0; i < N; i++)
                    {
                        float a = i * Mathf.PI * 2f / N;
                        v.Add(new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f));
                        c.Add(new Color32(255, 255, 255, 255));
                        int j = 1 + i;
                        int n = 1 + (i + 1) % N;
                        t.Add(0); t.Add(j); t.Add(n);
                        t.Add(0); t.Add(n); t.Add(j);
                    }
                    disc = new Mesh();
                    disc.SetVertices(v);
                    disc.SetColors(c);
                    disc.SetTriangles(t, 0);
                    disc.RecalculateBounds();
                }
                return disc;
            }
        }
    }
}
