using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// (Api version 37) A roofed shell of a structure: its walls and closed doors, every roofed cell closed with IsoRim's roof,
    /// its substructure and floors (with a thin slab under them and a soft shadow), and the buildings with models that stand
    /// out in the open, sit on the outside of the hull or rise through the roof (thrusters, turrets, field extenders...). No pawns, items, plants,
    /// filth, furniture under the roof or lights. Built once from a map area (<see cref="Api.BuildShell"/>), combined per
    /// material, then drawn at any pose with <see cref="Draw"/>; the map it was built from may go away. Main thread only.
    /// Call <see cref="Dispose"/> when done: it holds meshes and a texture.
    /// </summary>
    public sealed class Shell : IDisposable
    {
        private const int MaxVerts = MeshWriter.MaxVerts;
        private const byte Ring = 1, Ship = 2, Deck = 3;   // cell classes, as ShipSnapshot's
        private static readonly int[] DX = { 0, 0, 0, -1, 1, -1, 1, -1, 1 };   // ShaderCut direction codes 1-8
        private static readonly int[] DZ = { 0, -1, 1, 0, 0, -1, -1, 1, 1 };

        /// <summary>Shells built and not disposed yet (for leak checks).</summary>
        public static int Live { get; private set; }

        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly List<Material> mats = new List<Material>();
        private readonly List<Material> sources = new List<Material>();
        private readonly List<int> layers = new List<int>();
        private readonly List<bool> depths = new List<bool>();
        private readonly List<ShipCapture.Rec> items = new List<ShipCapture.Rec>();
        private Mesh slab;
        private Texture2D shadowTex;
        private Material shadowMat;
        private Vector2 shadowSize;
        private Vector2 shadowCentre;   // the shadow card's middle, relative to the origin

        /// <summary>The map point (x, z; y 0) the shell was built round: drawn at <see cref="Draw"/>'s centre.</summary>
        public Vector3 Origin { get; private set; }
        /// <summary>The area it was built from.</summary>
        public CellRect Rect { get; private set; }
        public int Vertices { get; private set; }
        public int Triangles { get; private set; }
        public int Meshes => meshes.Count;
        /// <summary>Moving parts drawn as they were at build time (doors, turret guns): one draw each.</summary>
        public int Parts => items.Count;
        /// <summary>Buildings with models taken in (outside the roof or reaching above it, walls drawn from models).</summary>
        public int Buildings { get; private set; }
        public int DeckCells { get; private set; }
        /// <summary>Walls (wall-form edifices) read.</summary>
        public int WallCells { get; private set; }
        public float BuildMs { get; private set; }
        public bool Disposed { get; private set; }

        // ---------------------------------------------------------------- building

        private sealed class Chunk
        {
            public readonly List<Vector3> verts = new List<Vector3>();
            public readonly List<int> tris = new List<int>();
            public readonly List<Color32> colors = new List<Color32>();
            public readonly List<Vector3> uvs = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public bool hasNormals;
        }

        private sealed class Group
        {
            public Material mat;
            public int layer;
            public bool depth;
            public readonly List<Chunk> chunks = new List<Chunk>();
        }

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> sectionLayers = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");
        private static MethodInfo takePrint;

        // Set while a shell records the moving parts' draws (ShipCapture.Note): noted here, not drawn.
        internal static List<ShipCapture.Rec> Recording;

        // The section layers the shell takes geometry from: 1 = taken, 2 = taken with one ring more for flat pieces
        // (the deck's lips), as ShipSnapshot. Buildings come from their own print (below), not from the things layer.
        private static int Kind(SectionLayer layer)
        {
            switch (layer.GetType().Name)
            {
                case "SectionLayer_Terrain":
                case "SectionLayer_TerrainScatter":
                case "SectionLayer_IsoRimWalls":
                case "SectionLayer_IsoRimRoofs":
                    return 1;
                case "SectionLayer_SubstructureProps":
                case "SectionLayer_GravshipHull":
                    return 2;
            }
            int extra;
            return WallPieces.flown.TryGetValue(layer.GetType().Name, out extra) ? extra : 0;   // (Api 26) FlyLayer
        }

        // Substructure, foundations and built floors (the deck). Cells of buildings off the deck are shell cells too (below),
        // but the ground under them is not taken (another mod's placeholder ground on a temporary map).
        private static bool Footprint(Map map, IntVec3 c)
        {
            TerrainGrid g = map.terrainGrid;
            if (g.FoundationAt(c) != null)
            {
                return true;
            }
            TerrainDef t = g.TerrainAt(c);
            return t != null && (t.isFoundation || t.IsSubstructure || (!t.natural && t.layerable));
        }

        // Out in the open (a cell of it unroofed), on the outside of the hull (beside an open cell: a thruster or a gun set
        // into the hull under the roof's edge), a turret, or with a part that rises through the roof.
        private static bool Exposed(Map map, Thing t, ModelEntry entry)
        {
            if (t is Building_Turret)
            {
                return true;
            }
            CellRect occ = t.OccupiedRect();
            foreach (IntVec3 c in occ)
            {
                if (c.InBounds(map) && !map.roofGrid.Roofed(c))
                {
                    return true;
                }
            }
            foreach (IntVec3 c in occ.ExpandedBy(1).EdgeCells)
            {
                if (!c.InBounds(map))
                {
                    return true;
                }
                if (map.roofGrid.Roofed(c))
                {
                    continue;
                }
                Building b = c.GetEdifice(map);
                if (b == null || !(b is Building_Door || WallBoxes.IsWallForm(Forms.For(b.def)) || b.def.fillPercent >= 0.99f))
                {
                    return true;   // open ground or deck beside it
                }
            }
            // Under the roof: only parts that rise through it (Api 31: a gun with topThroughRoof, a roofModel part); the rest of
            // a model is clipped at the roof in the 3D view, so it would not show over the shell's roof either.
            return entry.def.topThroughRoof || entry.roofPart != null;
        }

        internal static Shell Build(Map map, CellRect rect)
        {
            if (map == null || !View3D.Active || !UnityData.IsInMainThread)
            {
                return null;
            }
            rect = rect.ClipInsideMap(map);
            if (rect.Area <= 0)
            {
                return null;
            }
            Stopwatch watch = Stopwatch.StartNew();
            Shell s = new Shell { Rect = rect };
            Live++;
            Vector3 origin = rect.CenterVector3;
            origin.y = 0f;
            s.Origin = origin;
            IsoRimSettings st = IsoRimMod.Settings;
            bool roofsWere = st.showRoofs;
            List<SectionLayer> roofLayers = new List<SectionLayer>();
            List<MapDrawLayer> printers = new List<MapDrawLayer>();
            try
            {
                // ---- cell classes over the area and a ring round it
                CellRect area = rect.ExpandedBy(1);
                int w = area.Width, h = area.Height;
                byte[] cls = new byte[w * h];
                Func<int, int, int> index = (x, z) => x < area.minX || z < area.minZ || x > area.maxX || z > area.maxZ ? -1 : (z - area.minZ) * w + (x - area.minX);
                Func<float, float, byte> cellAt = (x, z) =>
                {
                    int i = index(Mathf.FloorToInt(x), Mathf.FloorToInt(z));
                    return i < 0 ? (byte)0 : cls[i];
                };
                List<IntVec3> deck = new List<IntVec3>();
                foreach (IntVec3 c in rect)
                {
                    if (Footprint(map, c))
                    {
                        cls[index(c.x, c.z)] = Deck;
                        deck.Add(c);
                    }
                }
                s.DeckCells = deck.Count;
                foreach (IntVec3 c in rect)
                {
                    int i = index(c.x, c.z);
                    if (cls[i] != 0)
                    {
                        continue;
                    }
                    RimWorld.SectionLayer_GravshipHull.CornerType type;
                    IntVec3 a, b;
                    if (c.GetEdifice(map) == null && HullCorners.At(map, c, out type, out _) && HullCorners.Legs(type, out a, out b))
                    {
                        cls[i] = Ship;   // a hull corner piece beside the deck
                    }
                }
                List<Thing> kept = new List<Thing>();
                List<Thing> moving = new List<Thing>();
                foreach (IntVec3 c in rect)
                {
                    List<Thing> here = map.thingGrid.ThingsListAtFast(c);
                    for (int j = 0; j < here.Count; j++)
                    {
                        Thing t = here[j];
                        if (t.Position != c || t is Pawn || t is Frame || t.def.category != ThingCategory.Building
                            || (t.def.building != null && t.def.building.isNaturalRock))
                        {
                            continue;
                        }
                        if (t.def.building != null && t.def.building.isEdifice && WallBoxes.IsWallForm(Forms.For(t.def)))
                        {
                            s.WallCells++;
                        }
                        foreach (IntVec3 o in t.OccupiedRect())
                        {
                            int i = index(o.x, o.z);
                            if (i >= 0 && cls[i] < Ship)
                            {
                                cls[i] = Ship;
                            }
                        }
                        if (t is Building_Door)
                        {
                            moving.Add(t);   // drawn shut, as it is (Doors)
                            continue;
                        }
                        ThingForm form = Forms.For(t.def);
                        if (form.form != Form3D.Model || form.model == null || !form.model.HasModel)
                        {
                            continue;   // walls (the walls layer), and things without a model
                        }
                        if (!WallBoxes.IsWallForm(form) && !Exposed(map, t, form.model))
                        {
                            continue;   // furniture under the roof
                        }
                        kept.Add(t);
                        if (t.def.drawerType != DrawerType.MapMeshOnly)
                        {
                            moving.Add(t);
                        }
                    }
                }
                foreach (IntVec3 c in deck)
                {
                    for (int d = 1; d < 9; d++)
                    {
                        int i = index(c.x + DX[d], c.z + DZ[d]);
                        if (i >= 0 && cls[i] == 0)
                        {
                            cls[i] = Ring;
                        }
                    }
                }
                s.Buildings = kept.Count;
                if (ModLog.Diag)
                {
                    ModLog.Note("[IsoRim] Shell buildings: " + string.Join(", ", kept.GroupBy(k => k.def.defName + "/" + (k.Stuff != null ? k.Stuff.defName : "-") + "/" + k.DrawColor).Select(gr => gr.Key + " x" + gr.Count())));
                }

                // ---- the map's own layers over the area, current; roofs shown whatever the roof display (a closed shell)
                Array sections = MapSections.SectionsOf(map);
                if (sections == null)
                {
                    s.Dispose();
                    return null;
                }
                List<Section> list = new List<Section>();
                CellRect over = area.ClipInsideMap(map);
                for (int sx = over.minX / Section.Size; sx <= over.maxX / Section.Size && sx < sections.GetLength(0); sx++)
                {
                    for (int sz = over.minZ / Section.Size; sz <= over.maxZ / Section.Size && sz < sections.GetLength(1); sz++)
                    {
                        if (sections.GetValue(sx, sz) is Section sec)
                        {
                            list.Add(sec);
                        }
                    }
                }
                if (list.Count == 0)
                {
                    s.Dispose();
                    return null;
                }
                // The ground round the deck (and another mod's placeholder ground under its walls) blends its edges onto deck cells
                // in the game's terrain layer: those pieces are in the ground's own materials, left out.
                HashSet<Material> ground = new HashSet<Material>();
                foreach (IntVec3 c in area.ClipInsideMap(map))
                {
                    if (!Footprint(map, c))
                    {
                        TerrainDef gt = map.terrainGrid.TerrainAt(c);
                        if (gt != null)
                        {
                            ColorDef col = map.terrainGrid.ColorAt(c);
                            ground.Add(map.terrainGrid.GetMaterial(gt, false, col));
                            ground.Add(map.terrainGrid.GetMaterial(gt, true, col));
                        }
                    }
                }
                st.showRoofs = true;
                List<Group> groups = new List<Group>();
                int[] remap = new int[0];
                foreach (Section sec in list)
                {
                    foreach (SectionLayer layer in sectionLayers(sec))
                    {
                        int kind = Kind(layer);
                        if (kind == 0)
                        {
                            continue;
                        }
                        if (layer is SectionLayer_IsoRimRoofs)
                        {
                            roofLayers.Add(layer);
                        }
                        layer.Regenerate();
                        bool terrain = layer.GetType().Name == "SectionLayer_Terrain";
                        Take(groups, layer, kind == 2 ? Ring : Deck, cellAt, ref remap, origin, terrain, false, terrain ? ground : null);
                    }
                }
                st.showRoofs = roofsWere;

                // ---- the kept buildings, printed as on the map (their models, outlines, ground shading) into own layers
                Section home = list[0];
                if (takePrint == null)
                {
                    takePrint = AccessTools.Method(typeof(SectionLayer_ThingsGeneral), "TakePrintFrom");
                }
                SectionLayer_ThingsGeneral things = new SectionLayer_ThingsGeneral(home);
                Printer bodies = new Printer(home);
                printers.Add(things);
                printers.Add(bodies);
                object[] arg = new object[1];
                foreach (Thing t in kept)
                {
                    try
                    {
                        if (SectionLayer_IsoRimBodies.Handles(t))
                        {
                            ModelBaker.Bake(bodies, t, Forms.For(t.def).model, 0f);
                        }
                        else if (t.def.drawerType != DrawerType.RealtimeOnly && takePrint != null)
                        {
                            arg[0] = t;
                            takePrint.Invoke(things, arg);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[IsoRim] Shell: printing " + t + " failed: " + e, 93611701);
                    }
                }
                Take(groups, things, Deck, null, ref remap, origin, false, true, null);
                Take(groups, bodies, Deck, null, ref remap, origin, false, true, null);

                // ---- the moving parts (doors shut, turret guns), as they draw now, noted instead of drawn
                List<ShipCapture.Rec> rec = new List<ShipCapture.Rec>();
                bool was = ShipCapture.Recording;
                Recording = rec;
                ShipCapture.Recording = true;
                try
                {
                    Dictionary<string, int> perDef = ModLog.Diag ? new Dictionary<string, int>() : null;
                    foreach (Thing t in moving)
                    {
                        try
                        {
                            int n0 = rec.Count;
                            t.DynamicDrawPhaseAt(DrawPhase.Draw, t.DrawPos);
                            if (perDef != null)
                            {
                                string key = t.def.defName + ":" + string.Join("/", rec.Skip(n0).Select(r => r.mat.name).Distinct());
                                perDef.TryGetValue(key, out int c0);
                                perDef[key] = c0 + rec.Count - n0;
                            }
                        }
                        catch (Exception e)
                        {
                            Log.ErrorOnce("[IsoRim] Shell: drawing " + t + " failed: " + e, 93611702);
                        }
                    }
                    if (perDef != null)
                    {
                        ModLog.Note("[IsoRim] Shell moving parts: " + string.Join(", ", perDef.Select(kv => kv.Key + " x" + kv.Value)));
                    }
                }
                finally
                {
                    Recording = null;
                    ShipCapture.Recording = was;
                }
                Matrix4x4 back = Matrix4x4.Translate(-origin);
                foreach (ShipCapture.Rec r in rec)
                {
                    s.items.Add(new ShipCapture.Rec { mesh = r.mesh, matrix = back * r.matrix, mat = r.mat, props = r.props });
                }

                // ---- meshes, the slab under the deck and the shadow
                int verts = 0, tris = 0;
                foreach (Group grp in groups)
                {
                    foreach (Chunk ch in grp.chunks)
                    {
                        if (ch.tris.Count == 0)
                        {
                            continue;
                        }
                        Mesh m = new Mesh();
                        m.name = "IsoRimShell";
                        MeshGuard.FitIndices(m, ch.verts.Count);
                        m.SetVertices(ch.verts);
                        m.SetTriangles(ch.tris, 0);
                        m.SetColors(ch.colors);
                        m.SetUVs(0, ch.uvs);
                        if (ch.hasNormals)
                        {
                            m.SetNormals(ch.normals);
                        }
                        m.RecalculateBounds();
                        MeshGuard.Check(m, "shell");
                        s.meshes.Add(m);
                        s.mats.Add(ShipSnapshot.Flying(grp.mat));
                        s.sources.Add(grp.mat);
                        s.depths.Add(grp.depth);
                        s.layers.Add(grp.layer);
                        verts += ch.verts.Count;
                        tris += ch.tris.Count / 3;
                    }
                }
                s.Vertices = verts;
                s.Triangles = tris;
                if (ModLog.Diag)
                {
                    ModLog.Note("[IsoRim] Shell materials: " + string.Join(", ", groups.Select(g => (g.mat.name ?? "?") + " " + g.chunks.Sum(c => c.tris.Count / 3))));
                }
                s.MakeSlab(deck, origin);
                s.MakeShadow(rect, cellAt, origin);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Building a shell failed: " + e, 93611700);
                s.Dispose();
                s = null;
            }
            finally
            {
                if (st.showRoofs != roofsWere)
                {
                    st.showRoofs = roofsWere;
                }
                // The roofs as the player has them again (a live map's roof texture follows the display).
                foreach (SectionLayer layer in roofLayers)
                {
                    try
                    {
                        layer.Regenerate();
                    }
                    catch (Exception)
                    {
                    }
                }
                foreach (MapDrawLayer p in printers)
                {
                    p.Dispose();
                }
            }
            if (s != null)
            {
                s.BuildMs = (float)watch.Elapsed.TotalMilliseconds;
                ModLog.Note("[IsoRim] Shell of " + rect + ": " + s.DeckCells + " deck cells, " + s.Buildings + " buildings, " + s.items.Count + " moving parts, "
                    + s.meshes.Count + " meshes, " + s.Vertices + " vertices, " + s.Triangles + " triangles, built in " + s.BuildMs.ToString("0.0") + " ms");
            }
            return s;
        }

        // A plain layer to print into (never drawn by the game).
        private sealed class Printer : SectionLayer
        {
            public Printer(Section section) : base(section)
            {
            }

            public override void Regenerate()
            {
            }
        }

        // The triangles of a layer that belong to the shell, moved round the origin, tags dropped (never cut away or
        // roof-clipped, as ShipSnapshot). cellAt null: all of them (a building's own print).
        private static void Take(List<Group> groups, MapDrawLayer layer, byte flatNeed, Func<float, float, byte> cellAt, ref int[] remap, Vector3 origin, bool terrain, bool all, HashSet<Material> skip)
        {
            float squash = View3D.Squash;
            foreach (LayerSubMesh sm in layer.subMeshes)
            {
                if (sm.disabled || sm.verts.Count == 0 || sm.material == null || (skip != null && skip.Contains(sm.material)))
                {
                    continue;
                }
                List<Vector3> v = sm.verts;
                List<int> t = sm.tris;
                List<Vector3> tag = sm.uvsChannelTwo.Count == v.Count ? sm.uvsChannelTwo : null;
                Group grp = GroupFor(groups, sm.material, sm.renderLayer);
                grp.depth |= terrain;
                Chunk ch = grp.chunks[grp.chunks.Count - 1];
                if (remap.Length < v.Count)
                {
                    remap = new int[Math.Max(v.Count, remap.Length * 2)];
                }
                for (int i = 0; i < v.Count; i++)
                {
                    remap[i] = -1;
                }
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    int a = t[k], b = t[k + 1], c = t[k + 2];
                    if (!all)
                    {
                        Vector3 va = v[a], vb = v[b], vc = v[c];
                        byte under = cellAt((va.x + vb.x + vc.x) / 3f, (va.z + vb.z + vc.z) / 3f);
                        bool flat = va.y * squash < 0.02f && vb.y * squash < 0.02f && vc.y * squash < 0.02f;
                        int flags = tag != null ? (int)(tag[a].z + 0.5f) : 0;
                        bool keep;
                        if (flags != 0)
                        {
                            int ox = Mathf.FloorToInt(tag[a].x), oz = Mathf.FloorToInt(tag[a].y);
                            keep = cellAt(ox + 0.5f, oz + 0.5f) >= Ship && (flags & ShaderCut.Twig) == 0;
                            int cond = (flags >> ShaderCut.CondShift) & 15;
                            if (keep && cond > 0 && cond < 9 && cellAt(ox + DX[cond] + 0.5f, oz + DZ[cond] + 0.5f) >= Ship)
                            {
                                keep = false;   // an inner join: hidden on the map, hidden in the shell
                            }
                        }
                        else
                        {
                            keep = under >= Ship || (flat && under >= flatNeed);
                        }
                        if (!keep || (flat && under < flatNeed))
                        {
                            continue;
                        }
                    }
                    if (ch.verts.Count + 3 > MaxVerts)
                    {
                        ch = new Chunk();
                        grp.chunks.Add(ch);
                        for (int i = 0; i < v.Count; i++)
                        {
                            remap[i] = -1;
                        }
                    }
                    ch.tris.Add(Vertex(sm, ch, a, remap, origin));
                    ch.tris.Add(Vertex(sm, ch, b, remap, origin));
                    ch.tris.Add(Vertex(sm, ch, c, remap, origin));
                }
            }
        }

        private static Group GroupFor(List<Group> groups, Material mat, int layer)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].mat == mat && groups[i].layer == layer)
                {
                    return groups[i];
                }
            }
            Group g = new Group { mat = mat, layer = layer };
            g.chunks.Add(new Chunk());
            groups.Add(g);
            return g;
        }

        private static int Vertex(LayerSubMesh sm, Chunk ch, int i, int[] remap, Vector3 origin)
        {
            if (remap[i] >= 0)
            {
                return remap[i];
            }
            int n = ch.verts.Count;
            ch.verts.Add(sm.verts[i] - origin);
            ch.colors.Add(sm.colors.Count > i ? sm.colors[i] : new Color32(255, 255, 255, 255));
            ch.uvs.Add(sm.uvs.Count > i ? sm.uvs[i] : Vector3.zero);
            if (sm.normals.Count > i)
            {
                if (!ch.hasNormals)
                {
                    ch.hasNormals = true;
                    while (ch.normals.Count < n)
                    {
                        ch.normals.Add(Vector3.up);
                    }
                }
                ch.normals.Add(sm.normals[i]);
            }
            else if (ch.hasNormals)
            {
                ch.normals.Add(Vector3.up);
            }
            remap[i] = n;
            return n;
        }

        // A plain dark slab a quarter cell thick under the deck cells (its underside and the sides along the outline), as the
        // flying gravship's (ShipSnapshot.MakeSlab), drawn only once clear of the ground.
        private const float SlabThickness = 0.25f;
        private const float SlabFrom = 0.3f;
        private static readonly Color32 SlabColor = new Color32(62, 64, 70, 255);
        private static readonly IntVec3[] Sides = { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };

        private void MakeSlab(List<IntVec3> deck, Vector3 origin)
        {
            if (deck.Count == 0)
            {
                return;
            }
            HashSet<IntVec3> on = new HashSet<IntVec3>(deck);
            List<Vector3> v = new List<Vector3>();
            List<int> t = new List<int>();
            float bottom = -SlabThickness / View3D.Squash;
            Func<float, float, float, Vector3> at = (x, y, z) => new Vector3(x - origin.x, y, z - origin.z);
            Action<Vector3, Vector3, Vector3, Vector3> quad = (a, b, c, d) =>
            {
                int n = v.Count;
                v.Add(a);
                v.Add(b);
                v.Add(c);
                v.Add(d);
                t.Add(n);
                t.Add(n + 1);
                t.Add(n + 2);
                t.Add(n);
                t.Add(n + 2);
                t.Add(n + 3);
            };
            foreach (IntVec3 c in deck)
            {
                quad(at(c.x, bottom, c.z), at(c.x + 1, bottom, c.z), at(c.x + 1, bottom, c.z + 1), at(c.x, bottom, c.z + 1));
                foreach (IntVec3 d in Sides)
                {
                    if (on.Contains(c + d))
                    {
                        continue;
                    }
                    Vector2 m = new Vector2(c.x + 0.5f + d.x * 0.5f, c.z + 0.5f + d.z * 0.5f);
                    Vector2 along = new Vector2(Mathf.Abs(d.z) * 0.5f, Mathf.Abs(d.x) * 0.5f);
                    Vector2 p = m - along, q = m + along;
                    quad(at(p.x, 0f, p.y), at(q.x, 0f, q.y), at(q.x, bottom, q.y), at(p.x, bottom, p.y));
                }
            }
            List<Color32> col = new List<Color32>(v.Count);
            for (int i = 0; i < v.Count; i++)
            {
                col.Add(SlabColor);
            }
            slab = new Mesh();
            slab.name = "IsoRimShellSlab";
            MeshGuard.FitIndices(slab, v.Count);
            slab.SetVertices(v);
            slab.SetTriangles(t, 0);
            slab.SetColors(col);
            slab.RecalculateBounds();
            MeshGuard.Check(slab, "shell slab");
        }

        // A soft silhouette of the shell's cells (4 texels a cell), as the flying gravship's (ShipSnapshot.MakeShadow).
        private const int ShadowTexels = 4;
        private const int ShadowMargin = 2;

        private void MakeShadow(CellRect rect, Func<float, float, byte> cellAt, Vector3 origin)
        {
            CellRect r = rect.ExpandedBy(ShadowMargin);
            int w = r.Width * ShadowTexels, h = r.Height * ShadowTexels;
            float[] a = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    a[y * w + x] = cellAt(r.minX + x / ShadowTexels + 0.5f, r.minZ + y / ShadowTexels + 0.5f) >= Ship ? 1f : 0f;
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                a = Box(a, w, h, true);
                a = Box(a, w, h, false);
            }
            Color32[] px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a[i]) * 255f));
            }
            shadowTex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            shadowTex.name = "IsoRimShellShadow";
            shadowTex.wrapMode = TextureWrapMode.Clamp;
            shadowTex.filterMode = FilterMode.Bilinear;
            shadowTex.SetPixels32(px);
            shadowTex.Apply(true);
            shadowMat = new Material(ShaderDatabase.Transparent);
            shadowMat.name = "IsoRimShellShadow";
            shadowMat.mainTexture = shadowTex;
            shadowMat.color = new Color(0f, 0f, 0f, 0f);
            shadowSize = new Vector2(r.Width, r.Height);
            Vector3 mid = r.CenterVector3;
            shadowCentre = new Vector2(mid.x - origin.x, mid.z - origin.z);
        }

        private static float[] Box(float[] src, int w, int h, bool across)
        {
            float[] dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -1; k <= 1; k++)
                    {
                        int xx = across ? x + k : x, yy = across ? y : y + k;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h)
                        {
                            sum += src[yy * w + xx];
                        }
                    }
                    dst[y * w + x] = sum / 3f;
                }
            }
            return dst;
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>
        /// Draws the shell this frame with its origin over centre (x, z; y is not used), turned yaw degrees clockwise seen from
        /// above (about the origin), its deck height cells above the ground (true height; 0 = standing on the ground). Its
        /// shadow falls on the current map away from IsoRim's sun or moon, fading in over the first cell of height; alpha
        /// (0-1) scales the shadow, and at 0 nothing is drawn. Nothing is drawn outside the 3D view or once disposed.
        /// </summary>
        public void Draw(Vector3 centre, float yaw, float height, float alpha = 1f)
        {
            if (Disposed || !View3D.Ready || alpha <= 0f)
            {
                return;
            }
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            Matrix4x4 m = Matrix4x4.TRS(new Vector3(centre.x, height / View3D.Squash, centre.z), turn, Vector3.one);
            bool was = Floors.InDrawnSpace;
            bool recording = ShipCapture.Recording;
            Floors.InDrawnSpace = true;   // drawn where it is: never moved onto a stacked floor
            ShipCapture.Recording = false;
            try
            {
                Material slabMat = R3DMaterials.Solid;
                if (slab != null && slabMat != null && height > SlabFrom)
                {
                    IsoGlow.DrawLit(slab, m, slabMat, 0);
                }
                Material depthMat = ShipSnapshot.DepthMaterial;
                for (int i = 0; i < meshes.Count; i++)
                {
                    if (depths[i] && depthMat != null)
                    {
                        Graphics.DrawMesh(meshes[i], m, depthMat, layers[i]);
                    }
                    Graphics.DrawMesh(meshes[i], m, mats[i], layers[i]);
                    Material mark = IsoGlow.MarkFor(sources[i]);
                    if (mark != null)
                    {
                        Graphics.DrawMesh(meshes[i], m, mark, layers[i]);
                    }
                }
                for (int i = 0; i < items.Count; i++)
                {
                    ShipCapture.Rec r = items[i];
                    if (r.mesh == null)
                    {
                        continue;
                    }
                    Matrix4x4 mi = m * r.matrix;
                    Graphics.DrawMesh(r.mesh, mi, r.mat, 0, null, 0, r.props);
                    Material mark = IsoGlow.MarkFor(r.mat);
                    if (mark != null)
                    {
                        Graphics.DrawMesh(r.mesh, mi, mark, 0);
                    }
                }
                DrawShadow(centre, height, turn, alpha, Find.CurrentMap);
            }
            finally
            {
                Floors.InDrawnSpace = was;
                ShipCapture.Recording = recording;
            }
        }

        private void DrawShadow(Vector3 centre, float height, Quaternion turn, float alpha, Map map)
        {
            if (shadowMat == null || map == null || map.Biome == BiomeDefOf.Space || map.Biome == BiomeDefOf.Orbit)
            {
                return;
            }
            float glow = GenCelestial.CurCelestialSunGlow(map);
            float a = 0.65f * Mathf.Lerp(0.4f, 1f, glow) * Mathf.SmoothStep(0f, 1f, height) * Mathf.Clamp01(alpha);
            if (a <= 0.004f)
            {
                return;
            }
            Vector3 light = View3D.LightNow();
            float up = Mathf.Max(0.3f, light.y);
            Vector3 off = new Vector3(-light.x, 0f, -light.z) / up * height;
            float grow = 1f + 0.006f * height;
            Vector3 mid = turn * new Vector3(shadowCentre.x, 0f, shadowCentre.y);
            Vector3 at = new Vector3(centre.x + mid.x + off.x, AltitudeLayer.Gas.AltitudeFor(), centre.z + mid.z + off.z);
            if (!at.ToIntVec3().InBounds(map))
            {
                return;
            }
            shadowMat.color = new Color(0f, 0f, 0f, a);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(at, turn, new Vector3(shadowSize.x * grow, 1f, shadowSize.y * grow)), shadowMat, 0);
        }

        /// <summary>Lets go of the meshes, the slab and the shadow. Drawing a disposed shell does nothing.</summary>
        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }
            Disposed = true;
            Live--;
            foreach (Mesh m in meshes)
            {
                if (m != null)
                {
                    UnityEngine.Object.Destroy(m);
                }
            }
            meshes.Clear();
            mats.Clear();
            sources.Clear();
            depths.Clear();
            layers.Clear();
            items.Clear();
            if (slab != null)
            {
                UnityEngine.Object.Destroy(slab);
                slab = null;
            }
            if (shadowMat != null)
            {
                UnityEngine.Object.Destroy(shadowMat);
                shadowMat = null;
            }
            if (shadowTex != null)
            {
                UnityEngine.Object.Destroy(shadowTex);
                shadowTex = null;
            }
        }

        public override string ToString()
        {
            return "Shell " + Rect + ": " + DeckCells + " deck cells, " + WallCells + " walls, " + Buildings + " buildings, " + Parts + " moving parts, " + Meshes + " meshes, "
                + Vertices + " vertices, " + Triangles + " triangles, built in " + BuildMs.ToString("0.0") + " ms" + (Disposed ? " (disposed)" : "");
        }
    }
}
