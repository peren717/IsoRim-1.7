using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Bridges in 3D: the deck of every bridge terrain (TerrainDef.bridge: Bridge, AncientBridge, HeavyBridge,
    // AncientHeavyBridge and any modded one; on 1.7 it lies in the terrain grid's foundation layer) is raised a little
    // over the water, with the deck's thickness showing along its open sides, posts or piers under those sides, and a
    // short ramp down onto the land where it meets the bank. Everything standing on the deck stands on it.
    //
    // One height rule, HeightAt (deck cells: Deck; land beside a bridge over water: a ramp down to the ground within
    // RampReach of the deck), read by:
    //   - the game's ground layers (GroundLayer_Postfix): the deck cell's own terrain quad (the bridge, or a floor laid
    //     on it), zones, plans, snow and sand are lifted onto the deck;
    //   - SectionLayer_IsoRimBridges: what lies under the deck (the water or ground the game no longer draws there),
    //     the ramps, the deck's sides, beams and posts, and the depth of the deck and ramps (depth only, queue 1990, so
    //     the game's terrain, which writes none, still hides what is under the deck);
    //   - things: Floors.ThingOffset / ExtraMatrix (things drawn every frame, labels, brackets, culling, silhouettes),
    //     PrintLift (things printed into the map mesh: TakePrintFrom, the realtime bodies layer), IsoCellMap.TopOf (the
    //     fog lid).
    // Ground floor only (ProjectZ's floors have no bridges). With the 3D view off nothing changes.
    // Call sites: Patches (TakePrintFrom prefix/finalizer, ThingPhase, ground layer postfixes, BridgeProps postfix,
    // TerrainChanged_Postfix), Floors (ThingOffset, ExtraMatrix, DrawnOffset, ThingPhase_Prefix), Layers (water under
    // bridges, realtime bodies), Cells (TopOf).
    [StaticConstructorOnStartup]
    public static class Bridges
    {
        public const float Deck = 0.15f;         // cells the deck's top is above the ground
        public const float RampReach = 0.5f;     // cells the ramp runs onto the land
        private const float MaskDrop = 0.003f;   // the deck's depth sits this far under its top (as SinkHoles)
        private const float OutlineBold = 0.6f;
        private const float SlabWood = 0.05f;    // the deck's thickness
        private const float SlabHeavy = 0.065f;
        private const float ShadeReach = 0.3f;   // the deck's shade on the water beside it
        private const float ShadeDark = 0.42f;

        public static bool Active
        {
            get { return View3D.Active; }
        }

        // The bridge in a cell (ground rows only), or null.
        public static TerrainDef DeckAt(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return null;
            }
            TerrainDef f = map.terrainGrid.FoundationAt(c);
            if (f == null || !f.bridge)
            {
                return null;
            }
            int rows = Floors.GroundRowsOf(map);
            return rows > 0 && c.z >= rows ? null : f;
        }

        // A cell a bridge deck can lie in (ground rows only: ProjectZ's floors have no bridges).
        public static bool CanDeck(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return false;
            }
            int rows = Floors.GroundRowsOf(map);
            return rows <= 0 || c.z < rows;
        }

        // The deck's thickness (cells).
        public static float SlabOf(TerrainDef bridge)
        {
            return IsHeavy(bridge) ? SlabHeavy : SlabWood;
        }

        // What the bridge stands in: the water (or ground) the game keeps under it.
        public static TerrainDef UnderAt(Map map, IntVec3 c)
        {
            return map.terrainGrid.UnderTerrainAt(c) ?? map.terrainGrid.TopTerrainAt(c);
        }

        // Open water under a bridge cell (the deck rests on posts in it).
        public static bool OverWater(Map map, IntVec3 c)
        {
            return Ground.IsWater(UnderAt(map, c));
        }

        public static bool IsHeavy(TerrainDef t)
        {
            return t.affordances != null && t.affordances.Contains(TerrainAffordanceDefOf.Heavy);
        }

        // Land a ramp can run onto: in the map, not water, not a bridge, nothing impassable standing there.
        private static bool RampLand(Map map, IntVec3 c)
        {
            if (!c.InBounds(map) || DeckAt(map, c) != null)
            {
                return false;
            }
            int rows = Floors.GroundRowsOf(map);
            if (rows > 0 && c.z >= rows)
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            if (t == null || Ground.IsWater(t) || t.passability == Traversability.Impassable)
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            return b == null || b.def.passability != Traversability.Impassable;
        }

        // A bridge cell beside land cell l that runs a ramp onto it (a bridge over water meeting the bank).
        private static bool RampSource(Map map, IntVec3 b)
        {
            return DeckAt(map, b) != null && OverWater(map, b);
        }

        // The height of the walking surface at a point (true cells): the deck, a ramp, or 0.
        public static float HeightAt(Map map, float x, float z)
        {
            IntVec3 c = new IntVec3(Mathf.FloorToInt(x), 0, Mathf.FloorToInt(z));
            if (!c.InBounds(map))
            {
                return 0f;
            }
            if (DeckAt(map, c) != null)
            {
                return Deck;
            }
            return RampHeight(map, c, x, z);
        }

        // On land cell c: the ramp's height at (x, z), from the nearest bridge cell beside it (not across a corner: the
        // ramp is as wide as the deck) that runs one.
        private static float RampHeight(Map map, IntVec3 c, float x, float z)
        {
            float best = float.MaxValue;
            bool land = false, looked = false;
            for (int s = 0; s < 4; s++)
            {
                IntVec3 b = c + Sides[s];
                if (!b.InBounds(map) || !RampSource(map, b))
                {
                    continue;
                }
                if (!looked)
                {
                    looked = true;
                    land = RampLand(map, c);
                }
                if (!land)
                {
                    return 0f;
                }
                float ex = Mathf.Max(b.x - x, 0f, x - (b.x + 1));
                float ez = Mathf.Max(b.z - z, 0f, z - (b.z + 1));
                best = Mathf.Min(best, Mathf.Sqrt(ex * ex + ez * ez));
            }
            return best >= RampReach ? 0f : Deck * (1f - best / RampReach);
        }

        // How far a thing is lifted (true cells): pawns by where they are drawn (smooth up and down a ramp), anything
        // else by the cells it covers (the deck if any of them is a deck cell).
        public static float LiftOf(Thing t)
        {
            if (t == null || !t.Spawned || !Active)
            {
                return 0f;
            }
            Map map = t.Map;
            if (t is Pawn)
            {
                Vector3 p = t.DrawPos;
                return HeightAt(map, p.x, p.z);
            }
            if (t.def.size.x == 1 && t.def.size.z == 1)
            {
                return DeckAt(map, t.Position) != null ? Deck : 0f;
            }
            foreach (IntVec3 c in t.OccupiedRect())
            {
                if (DeckAt(map, c) != null)
                {
                    return Deck;
                }
            }
            return 0f;
        }

        // ---- things printed into the map mesh: what a thing writes into the layer is lifted by its LiftOf

        private struct LiftState
        {
            public SectionLayer layer;
            public float dy;
            public int[] counts;
        }

        private static readonly Stack<LiftState> lifts = new Stack<LiftState>();

        // Before a thing prints: notes how much each piece of the layer holds. Walls and other blocks are not lifted
        // (they rise from the ground through the deck; their tops stay level with the walls round them).
        public static bool BeginPrint(SectionLayer layer, Thing t)
        {
            if (layer == null || t == null || !Active || Forms.For(t.def).form == Form3D.Block)
            {
                return false;
            }
            float lift = LiftOf(t);
            if (lift <= 0f)
            {
                return false;
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            int[] counts = new int[subs.Count];
            for (int i = 0; i < subs.Count; i++)
            {
                counts[i] = subs[i].verts.Count;
            }
            lifts.Push(new LiftState { layer = layer, dy = lift / View3D.Squash, counts = counts });
            return true;
        }

        // After it printed: everything it added is raised.
        public static void EndPrint()
        {
            if (lifts.Count == 0)
            {
                return;
            }
            LiftState s = lifts.Pop();
            List<LayerSubMesh> subs = s.layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                List<Vector3> verts = subs[i].verts;
                for (int v = i < s.counts.Length ? s.counts[i] : 0; v < verts.Count; v++)
                {
                    Vector3 p = verts[v];
                    p.y += s.dy;
                    verts[v] = p;
                }
            }
        }

        // ---- the game's ground layers

        // Postfix of the ground layers' Regenerate (terrain, zones, plans, snow, sand): their pieces in deck cells are put
        // on the deck. Snow and sand keep their geometry between rebuilds (only recoloured), so heights are set, not added.
        public static void LiftGround(SectionLayer layer)
        {
            if (layer == null)
            {
                return;
            }
            Map map = MapSections.MapOf(layer);
            if (map == null || !Active)
            {
                return;
            }
            bool terrain = layer.GetType() == typeof(SectionLayer_Terrain);
            bool nine = layer is SectionLayer_Snow || layer is SectionLayer_Sand;
            if (!terrain && !nine && !(layer.GetType().Name == "SectionLayer_Zones") && !(layer is SectionLayer_Plans))
            {
                return;   // (the water-depth layer derives from the terrain's)
            }
            Section section = layerSection(layer);
            if (section == null || (!nine && !AnyIn(map, section.CellRect)))
            {
                return;   // (snow and sand: heights are set back to the ground where a bridge went)
            }
            float dy = Deck / View3D.Squash;
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int s = 0; s < subs.Count; s++)
            {
                LayerSubMesh sm = subs[s];
                if (sm == null || sm.mesh == null || sm.verts.Count == 0)
                {
                    continue;
                }
                bool changed = nine ? SetNine(map, sm, dy) : LiftQuads(map, sm, dy);
                if (changed)
                {
                    sm.mesh.SetVertices(sm.verts);
                    sm.mesh.RecalculateBounds();
                }
            }
        }

        private static readonly AccessTools.FieldRef<SectionLayer, Section> layerSection = AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        private static bool AnyIn(Map map, CellRect r)
        {
            foreach (IntVec3 c in r)
            {
                if (DeckAt(map, c) != null)
                {
                    return true;
                }
            }
            return false;
        }

        // Quads of their own per cell (terrain on a foundation never blends, zones, plans): each triangle whose middle is
        // in a deck cell is lifted, each corner once.
        private static readonly HashSet<int> lifted = new HashSet<int>();

        private static bool LiftQuads(Map map, LayerSubMesh sm, float dy)
        {
            lifted.Clear();
            List<int> tris = sm.tris;
            List<Vector3> verts = sm.verts;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
                IntVec3 cell = new IntVec3(Mathf.FloorToInt((a.x + b.x + c.x) / 3f), 0, Mathf.FloorToInt((a.z + b.z + c.z) / 3f));
                if (DeckAt(map, cell) == null)
                {
                    continue;
                }
                for (int k = 0; k < 3; k++)
                {
                    if (lifted.Add(tris[i + k]))
                    {
                        Vector3 p = verts[tris[i + k]];
                        p.y += dy;
                        verts[tris[i + k]] = p;
                    }
                }
            }
            return lifted.Count > 0;
        }

        // Nine corners a cell, the cell's middle last (SectionLayerGeometryMaker_Solid): each cell's corners at the
        // ground's height, or the deck's.
        private static bool SetNine(Map map, LayerSubMesh sm, float dy)
        {
            List<Vector3> verts = sm.verts;
            if (verts.Count % 9 != 0)
            {
                return false;
            }
            float ground = AltitudeLayer.Terrain.AltitudeFor();
            bool changed = false;
            for (int v = 0; v < verts.Count; v += 9)
            {
                Vector3 mid = verts[v + 8];
                float want = ground + (DeckAt(map, new IntVec3(Mathf.FloorToInt(mid.x), 0, Mathf.FloorToInt(mid.z))) != null ? dy : 0f);
                if (Mathf.Abs(mid.y - want) < 0.001f)
                {
                    continue;
                }
                for (int k = v; k < v + 9; k++)
                {
                    Vector3 p = verts[k];
                    p.y = want;
                    verts[k] = p;
                }
                changed = true;
            }
            return changed;
        }

        // The game's flat props sprite south of a bridge: the 3D supports stand in for it.
        public static void HideProps(SectionLayer layer)
        {
            if (!Active || layer == null)
            {
                return;
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                if (subs[i].verts.Count > 0)
                {
                    subs[i].Clear(MeshParts.All);   // not finalized: not drawn
                }
            }
        }

        // A bridge appeared, went or changed under c: the supports round it, what is printed on it, the ground layers
        // lifted onto it and the fog lid's height there are worked out again.
        public static void TerrainChanged(Map map, IntVec3 c, TerrainDef oldTerr, TerrainDef newTerr)
        {
            if (map == null || !View3D.Active || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            bool involved = (oldTerr != null && oldTerr.bridge) || (newTerr != null && newTerr.bridge) || DeckAt(map, c) != null;
            if (!involved)
            {
                return;
            }
            MapSections.QueueAround(map, c, 1, typeof(SectionLayer_IsoRimBridges), true);
            ulong flags = MapMeshFlagDefOf.Things;
            flags |= MapMeshFlagDefOf.Buildings;
            flags |= MapMeshFlagDefOf.Snow;
            flags |= MapMeshFlagDefOf.Zone;
            if (MapMeshFlagDefOf.Sand != null)
            {
                flags |= MapMeshFlagDefOf.Sand;
            }
            if (MapMeshFlagDefOf.Plan != null)
            {
                flags |= MapMeshFlagDefOf.Plan;
            }
            if (MapMeshFlagDefOf.PowerGrid != null)
            {
                flags |= MapMeshFlagDefOf.PowerGrid;
            }
            map.mapDrawer.MapMeshDirty(c, flags);
            IsoCellMap.Dirty(map, CellRect.CenteredOn(c, 1).ClipInsideMap(map));
        }

        // ---- materials

        private static Material mask, shade;
        private static bool triedMask, triedShade;

        // See-through dark from the vertices, after the water's colour (3050) and before its ripples (3053), depth-tested.
        public static Material Shade
        {
            get
            {
                if (!triedShade)
                {
                    triedShade = true;
                    shade = R3DMaterials.Make(R3DMaterials.BlendDef, "IsoRim_BridgeShade", 3052);
                }
                return shade;
            }
        }
        private static readonly Dictionary<TerrainDef, Color> sideColours = new Dictionary<TerrainDef, Color>();

        public static Material Mask
        {
            get
            {
                if (!triedMask)
                {
                    triedMask = true;
                    mask = R3DMaterials.Make("IsoRimDepthPlane", "IsoRim_BridgeDeck", 1990);
                }
                return mask;
            }
        }

        private const float TextureShade = 0.85f;   // the deck pictures are a little darker than their tint on average

        // The colour of the deck's sides and supports: the tint the deck's own surface is drawn with (the bridge's
        // material, its paint where painted; IsoRim's floor style tints it too).
        public static Color SideColour(Map map, IntVec3 c, TerrainDef bridge)
        {
            ColorDef paint = map.terrainGrid.ColorAt(c);
            if (paint != null && UnderPaint(map, c, bridge))
            {
                Color p = paint.color * TextureShade;
                p.a = 1f;
                return p;
            }
            Color col;
            if (sideColours.TryGetValue(bridge, out col))
            {
                return col;
            }
            Material mat = bridge.DrawMatSingle;
            col = mat != null && mat.HasProperty("_Color") ? mat.color : bridge.color;
            col *= TextureShade;
            col.a = 1f;
            sideColours[bridge] = col;
            return col;
        }

        // Forgets the side colours (the ground style changes the deck's tint).
        public static void Rebuilt()
        {
            sideColours.Clear();
        }

        // The paint in the grid is the bridge's own (no floor laid over it).
        private static bool UnderPaint(Map map, IntVec3 c, TerrainDef bridge)
        {
            return map.terrainGrid.TerrainAt(c) == bridge;
        }

        // ---- the geometry (SectionLayer_IsoRimBridges)

        private static readonly IntVec3[] Sides = { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };
        private static readonly Dictionary<Vector3Int, Model> boxes = new Dictionary<Vector3Int, Model>();

        // A box model from the origin to size (cells), cached by size (a few kinds of post and beam).
        private static Model BoxOf(Vector3 size)
        {
            Vector3Int key = new Vector3Int(Mathf.RoundToInt(size.x * 1000f), Mathf.RoundToInt(size.y * 1000f), Mathf.RoundToInt(size.z * 1000f));
            Model m;
            if (!boxes.TryGetValue(key, out m))
            {
                m = ObjLoader.Box(Vector3.zero, size);
                boxes[key] = m;
            }
            return m;
        }

        private sealed class Writer
        {
            public LayerSubMesh plain, outline, mask, shade;
            public float squash, groundY;
        }

        // Writes a box (min..max in true cells, map space) lit like the models, with the models' outline.
        private static void WriteBox(Writer w, Vector3 min, Vector3 max, Color c)
        {
            Model m = BoxOf(max - min);
            int n = m.positions.Length;
            if (w.plain == null || w.plain.verts.Count + n > MeshWriter.MaxVerts)
            {
                return;
            }
            Color32 pc = R3DMaterials.Shaded(c, 1f);
            Color32 oc = R3DMaterials.Shaded(c, ModelBaker.OutlineShade);
            for (int k = 0; k < n; k++)
            {
                Vector3 p = m.positions[k] + min;
                w.plain.tris.Add(w.plain.verts.Count);
                w.plain.verts.Add(new Vector3(p.x, w.groundY + p.y / w.squash, p.z));
                w.plain.colors.Add(pc);
                w.plain.uvs.Add(default);
                if (w.outline != null && m.hull != null)
                {
                    Vector3 h = p + m.hull[k] * OutlineBold;
                    w.outline.tris.Add(w.outline.verts.Count);
                    w.outline.verts.Add(new Vector3(h.x, w.groundY + h.y / w.squash, h.z));
                    w.outline.colors.Add(oc);
                    w.outline.uvs.Add(default);
                }
            }
        }

        // A flat quad (true-cell heights), plain lit.
        private static void Face(LayerSubMesh sm, Writer w, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 col)
        {
            if (sm == null)
            {
                return;
            }
            MeshWriter.Quad(sm, Up(w, a), Up(w, b), Up(w, c), Up(w, d), col);
        }

        private static Vector3 Up(Writer w, Vector3 p)
        {
            return new Vector3(p.x, w.groundY + p.y / w.squash, p.z);
        }

        private static void MaskQuad(Writer w, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            LayerSubMesh sm = w.mask;
            if (sm == null)
            {
                return;
            }
            int i = sm.verts.Count;
            sm.verts.Add(Up(w, new Vector3(a.x, a.y - MaskDrop, a.z)));
            sm.verts.Add(Up(w, new Vector3(b.x, b.y - MaskDrop, b.z)));
            sm.verts.Add(Up(w, new Vector3(c.x, c.y - MaskDrop, c.z)));
            sm.verts.Add(Up(w, new Vector3(d.x, d.y - MaskDrop, d.z)));
            sm.tris.Add(i);
            sm.tris.Add(i + 1);
            sm.tris.Add(i + 2);
            sm.tris.Add(i);
            sm.tris.Add(i + 2);
            sm.tris.Add(i + 3);
        }

        // A quad of a terrain's own material (the game's terrain shader: textured by where it is on the map).
        private static void TerrainQuad(SectionLayer layer, Material mat, Writer w, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            if (mat == null)
            {
                return;
            }
            LayerSubMesh sm = layer.GetSubMesh(mat);
            if (sm == null)
            {
                return;
            }
            Color32 white = new Color32(255, 255, 255, 255);
            int i = sm.verts.Count;
            sm.verts.Add(Up(w, a));
            sm.verts.Add(Up(w, b));
            sm.verts.Add(Up(w, c));
            sm.verts.Add(Up(w, d));
            for (int k = 0; k < 4; k++)
            {
                sm.colors.Add(white);
            }
            sm.tris.Add(i);
            sm.tris.Add(i + 1);
            sm.tris.Add(i + 2);
            sm.tris.Add(i);
            sm.tris.Add(i + 2);
            sm.tris.Add(i + 3);
        }

        private static Material TerrainMaterial(Map map, IntVec3 c, TerrainDef def, bool withPaint)
        {
            if (def == null || def.dontRender)
            {
                return null;
            }
            try
            {
                return map.terrainGrid.GetMaterial(def, c.IsPolluted(map), withPaint ? map.terrainGrid.ColorAt(c) : null);
            }
            catch (Exception)
            {
                return def.DrawMatSingle;
            }
        }

        // Kinds of side a deck cell has toward a neighbour.
        private const int SideNone = 0;    // another deck cell
        private const int SideOpen = 1;    // water (or the map's edge, or dry ground under a bridge on dry land): posts
        private const int SideRamp = 2;    // land the deck ramps down onto
        private const int SideWall = 3;    // land it cannot ramp onto (a wall): a plain side down to the ground

        private static int SideKind(Map map, IntVec3 b, IntVec3 o)
        {
            if (o.InBounds(map) && DeckAt(map, o) != null)
            {
                return SideNone;
            }
            if (!o.InBounds(map))
            {
                return SideOpen;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(o);
            if (Ground.IsWater(t))
            {
                return SideOpen;
            }
            if (!OverWater(map, b))
            {
                return SideOpen;   // a bridge on dry land: sides and posts down into the ground
            }
            return RampLand(map, o) ? SideRamp : SideWall;
        }

        public static void Print(SectionLayer layer, Section section)
        {
            Map map = section.map;
            CellRect rect = section.CellRect;
            if (!AnyIn(map, rect.ExpandedBy(1).ClipInsideMap(map)))
            {
                return;
            }
            Writer w = new Writer();
            w.squash = View3D.Squash;
            w.groundY = AltitudeLayer.Terrain.AltitudeFor();
            Material lit = R3DMaterials.Lit(0);
            w.plain = lit != null ? MeshWriter.Sub(layer, lit) : null;
            w.outline = IsoRimMod.Settings.outlines && R3DMaterials.Outline != null ? MeshWriter.Sub(layer, R3DMaterials.Outline) : null;
            w.mask = Mask != null ? MeshWriter.Sub(layer, Mask) : null;
            w.shade = Shade != null ? MeshWriter.Sub(layer, Shade) : null;

            foreach (IntVec3 c in rect)
            {
                TerrainDef bridge = DeckAt(map, c);
                if (bridge != null)
                {
                    PrintDeckCell(map, layer, w, c, bridge);
                }
                else
                {
                    PrintRamp(map, layer, w, c);
                }
            }
            PrintPosts(map, w, rect);
        }

        private static void PrintDeckCell(Map map, SectionLayer layer, Writer w, IntVec3 c, TerrainDef bridge)
        {
            float x0 = c.x, x1 = c.x + 1, z0 = c.z, z1 = c.z + 1;
            // what the bridge stands in, at the ground (seen past the deck's edge)
            TerrainDef under = UnderAt(map, c);
            TerrainQuad(layer, TerrainMaterial(map, c, under, false), w,
                new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), new Vector3(x1, 0f, z0));
            // the deck's depth
            MaskQuad(w, new Vector3(x0, Deck, z0), new Vector3(x0, Deck, z1), new Vector3(x1, Deck, z1), new Vector3(x1, Deck, z0));

            bool heavy = IsHeavy(bridge);
            float bottom = heavy ? Deck - SlabHeavy : Deck - SlabWood;
            Color side = SideColour(map, c, bridge);
            Color32 sideC = R3DMaterials.Shaded(side, 1f);
            Color32 line = R3DMaterials.Shaded(side, ModelBaker.OutlineShade);
            Color beam = side * 0.72f;
            beam.a = 1f;
            for (int s = 0; s < 4; s++)
            {
                IntVec3 d = Sides[s];
                int kind = SideKind(map, c, c + d);
                if (kind == SideNone || kind == SideRamp)
                {
                    continue;
                }
                // the edge's two corners, left to right seen from outside
                Vector3 a, b;
                EdgeOf(c, s, out a, out b);
                float low = kind == SideWall ? 0f : bottom;
                Face(w.plain, w, new Vector3(a.x, low, a.z), new Vector3(a.x, Deck, a.z), new Vector3(b.x, Deck, b.z), new Vector3(b.x, low, b.z), sideC);
                // the rim's line along the top and the bottom of the side
                Vector3 o = new Vector3(d.x, 0f, d.z) * 0.002f;
                Face(w.plain, w, new Vector3(a.x, Deck - 0.012f, a.z) + o, new Vector3(a.x, Deck, a.z) + o, new Vector3(b.x, Deck, b.z) + o,
                    new Vector3(b.x, Deck - 0.012f, b.z) + o, line);
                if (kind == SideWall)
                {
                    continue;
                }
                ShadeStrip(w, a, b, new Vector3(d.x, 0f, d.z));
                Face(w.plain, w, new Vector3(a.x, bottom, a.z) + o, new Vector3(a.x, bottom + 0.008f, a.z) + o, new Vector3(b.x, bottom + 0.008f, b.z) + o,
                    new Vector3(b.x, bottom, b.z) + o, line);
                // the slab's underside along the side (seen from a low camera)
                Vector3 inward = new Vector3(-d.x, 0f, -d.z) * 0.12f;
                Face(w.plain, w, new Vector3(a.x, bottom, a.z), new Vector3(b.x, bottom, b.z), new Vector3(b.x, bottom, b.z) + inward,
                    new Vector3(a.x, bottom, a.z) + inward, R3DMaterials.Shaded(side * 0.6f, 1f));
                if (!heavy)
                {
                    // a beam under the side, set back a little
                    Vector3 lo = Vector3.Min(a, b), hi = Vector3.Max(a, b);
                    Vector3 back = new Vector3(-d.x, 0f, -d.z);
                    Vector3 p0 = lo + back * 0.025f, p1 = hi + back * 0.075f;
                    Vector3 mn = Vector3.Min(p0, p1), mx = Vector3.Max(p0, p1);
                    WriteBox(w, new Vector3(mn.x, bottom - 0.045f, mn.z), new Vector3(mx.x, bottom, mx.z), beam);
                }
            }
        }

        // The deck's shade on the water (or ground) along an open side: dark at the side, fading out.
        private static void ShadeStrip(Writer w, Vector3 a, Vector3 b, Vector3 outward)
        {
            LayerSubMesh sm = w.shade;
            if (sm == null)
            {
                return;
            }
            float y = w.groundY + 0.02f;   // just over the water's own layers
            Color32 dark = new Color32(0, 0, 0, (byte)(ShadeDark * 255f));
            Color32 clear = new Color32(0, 0, 0, 0);
            Vector3 far = outward * ShadeReach;
            int i = sm.verts.Count;
            sm.verts.Add(new Vector3(a.x, y, a.z));
            sm.verts.Add(new Vector3(a.x + far.x, y, a.z + far.z));
            sm.verts.Add(new Vector3(b.x + far.x, y, b.z + far.z));
            sm.verts.Add(new Vector3(b.x, y, b.z));
            sm.colors.Add(dark);
            sm.colors.Add(clear);
            sm.colors.Add(clear);
            sm.colors.Add(dark);
            for (int k = 0; k < 4; k++)
            {
                sm.uvs.Add(Vector3.zero);
            }
            sm.tris.Add(i);
            sm.tris.Add(i + 2);
            sm.tris.Add(i + 1);   // (clockwise from above, as the game's ground)
            sm.tris.Add(i);
            sm.tris.Add(i + 3);
            sm.tris.Add(i + 2);
        }

        // The corners of side s of cell c (0 north, 1 east, 2 south, 3 west).
        private static void EdgeOf(IntVec3 c, int s, out Vector3 a, out Vector3 b)
        {
            float x0 = c.x, x1 = c.x + 1, z0 = c.z, z1 = c.z + 1;
            switch (s)
            {
                case 0:
                    a = new Vector3(x1, 0f, z1);
                    b = new Vector3(x0, 0f, z1);
                    break;
                case 1:
                    a = new Vector3(x1, 0f, z0);
                    b = new Vector3(x1, 0f, z1);
                    break;
                case 2:
                    a = new Vector3(x0, 0f, z0);
                    b = new Vector3(x1, 0f, z0);
                    break;
                default:
                    a = new Vector3(x0, 0f, z1);
                    b = new Vector3(x0, 0f, z0);
                    break;
            }
        }

        // A land cell beside a bridge over water: the ramp down from the deck, drawn with the bridge's own surface, on a
        // grid of quarter cells (corners and bends follow the distance to the deck), with a side where it meets water.
        private const int RampSteps = 4;
        private static readonly float[] rampH = new float[(RampSteps + 1) * (RampSteps + 1)];

        private static void PrintRamp(Map map, SectionLayer layer, Writer w, IntVec3 c)
        {
            IntVec3 source = IntVec3.Invalid;
            for (int s = 0; s < 4 && !source.IsValid; s++)
            {
                if (RampSource(map, c + Sides[s]))
                {
                    source = c + Sides[s];
                }
            }
            if (!source.IsValid || !RampLand(map, c))
            {
                return;
            }
            TerrainDef bridge = DeckAt(map, source);
            Material mat = TerrainMaterial(map, source, map.terrainGrid.TerrainAt(source), true);
            float step = 1f / RampSteps;
            bool any = false;
            for (int j = 0; j <= RampSteps; j++)
            {
                for (int i = 0; i <= RampSteps; i++)
                {
                    float h = RampHeight(map, c, c.x + i * step, c.z + j * step);
                    rampH[j * (RampSteps + 1) + i] = h;
                    any |= h > 0f;
                }
            }
            if (!any)
            {
                return;
            }
            for (int j = 0; j < RampSteps; j++)
            {
                for (int i = 0; i < RampSteps; i++)
                {
                    float h00 = rampH[j * (RampSteps + 1) + i], h10 = rampH[j * (RampSteps + 1) + i + 1];
                    float h01 = rampH[(j + 1) * (RampSteps + 1) + i], h11 = rampH[(j + 1) * (RampSteps + 1) + i + 1];
                    if (h00 <= 0f && h10 <= 0f && h01 <= 0f && h11 <= 0f)
                    {
                        continue;
                    }
                    float x0 = c.x + i * step, x1 = x0 + step, z0 = c.z + j * step, z1 = z0 + step;
                    Vector3 a = new Vector3(x0, h00, z0), b = new Vector3(x0, h01, z1), cc = new Vector3(x1, h11, z1), d = new Vector3(x1, h10, z0);
                    TerrainQuad(layer, mat, w, a, b, cc, d);
                    MaskQuad(w, a, b, cc, d);
                }
            }
            // its sides (where a ramp beside it runs on at the same height, the side is hidden under the two)
            Color32 sideC = R3DMaterials.Shaded(SideColour(map, source, bridge), 1f);
            for (int s = 0; s < 4; s++)
            {
                IntVec3 o = c + Sides[s];
                if (o.InBounds(map) && DeckAt(map, o) != null)
                {
                    continue;
                }
                for (int k = 0; k < RampSteps; k++)
                {
                    int i0, j0, i1, j1;
                    switch (s)
                    {
                        case 0: i0 = k; j0 = RampSteps; i1 = k + 1; j1 = RampSteps; break;
                        case 1: i0 = RampSteps; j0 = k; i1 = RampSteps; j1 = k + 1; break;
                        case 2: i0 = k; j0 = 0; i1 = k + 1; j1 = 0; break;
                        default: i0 = 0; j0 = k; i1 = 0; j1 = k + 1; break;
                    }
                    float ha = rampH[j0 * (RampSteps + 1) + i0], hb = rampH[j1 * (RampSteps + 1) + i1];
                    if (ha <= 0f && hb <= 0f)
                    {
                        continue;
                    }
                    Vector3 pa = new Vector3(c.x + i0 * step, 0f, c.z + j0 * step), pb = new Vector3(c.x + i1 * step, 0f, c.z + j1 * step);
                    Face(w.plain, w, pa, new Vector3(pa.x, ha, pa.z), new Vector3(pb.x, hb, pb.z), pb, sideC);
                }
            }
        }

        // Posts (wooden bridges, every 2 cells) or piers (heavy bridges, every 3) under the open sides, at every corner
        // of the outline and along the straight runs; a row under the middle of wide bridges. Each corner is the
        // section's whose cell it is the south-west corner of.
        private static void PrintPosts(Map map, Writer w, CellRect rect)
        {
            int maxX = rect.maxX == map.Size.x - 1 ? rect.maxX + 1 : rect.maxX;
            int maxZ = rect.maxZ == map.Size.z - 1 ? rect.maxZ + 1 : rect.maxZ;
            for (int x = rect.minX; x <= maxX; x++)
            {
                for (int z = rect.minZ; z <= maxZ; z++)
                {
                    PostAt(map, w, x, z);
                }
            }
        }

        private static readonly IntVec3[] around = new IntVec3[4];
        private static readonly TerrainDef[] aroundDeck = new TerrainDef[4];

        private static void PostAt(Map map, Writer w, int x, int z)
        {
            // the cells round the corner: south-west, south-east, north-east, north-west
            around[0] = new IntVec3(x - 1, 0, z - 1);
            around[1] = new IntVec3(x, 0, z - 1);
            around[2] = new IntVec3(x, 0, z);
            around[3] = new IntVec3(x - 1, 0, z);
            int count = 0;
            TerrainDef bridge = null;
            IntVec3 bridgeCell = IntVec3.Invalid;
            for (int i = 0; i < 4; i++)
            {
                aroundDeck[i] = DeckAt(map, around[i]);
                if (aroundDeck[i] != null)
                {
                    count++;
                    if (bridge == null || (IsHeavy(aroundDeck[i]) && !IsHeavy(bridge)))
                    {
                        bridge = aroundDeck[i];
                        bridgeCell = around[i];
                    }
                }
            }
            if (count == 0)
            {
                return;
            }
            bool heavy = IsHeavy(bridge);
            int spacing = heavy ? 3 : 2;
            float half = heavy ? 0.08f : 0.04f;
            float top = Deck - (heavy ? SlabHeavy : SlabWood);
            Color col = SideColour(map, bridgeCell, bridge) * (heavy ? 0.8f : 0.72f);
            col.a = 1f;
            bool water = OverWater(map, bridgeCell);
            float foot = water ? -0.08f : 0f;
            if (count == 4)
            {
                // inside a wide deck: a middle row
                if (x % (spacing + 1) == 0 && z % (spacing + 1) == 0 && WideAround(map, x, z))
                {
                    WriteBox(w, new Vector3(x - half, foot, z - half), new Vector3(x + half, top - 0.02f, z + half), col);
                }
                return;
            }
            // which way is out: from the deck cells toward the others; only over open sides (no posts in a ramp's land)
            Vector2 outward = Vector2.zero;
            bool open = false;
            for (int i = 0; i < 4; i++)
            {
                Vector2 dir = new Vector2(around[i].x + 0.5f - x, around[i].z + 0.5f - z);
                if (aroundDeck[i] != null)
                {
                    outward -= dir;
                }
                else
                {
                    outward += dir;
                    IntVec3 deckNext = DeckNeighbour(i);
                    open |= SideKind(map, deckNext, around[i]) == SideOpen;
                }
            }
            if (!open)
            {
                return;
            }
            // a straight side (two deck cells side by side): a post only every few cells along it
            int along = -1;
            if (count == 2)
            {
                if ((aroundDeck[0] != null && aroundDeck[1] != null) || (aroundDeck[2] != null && aroundDeck[3] != null))
                {
                    along = x;   // the side runs east-west
                }
                else if ((aroundDeck[1] != null && aroundDeck[2] != null) || (aroundDeck[3] != null && aroundDeck[0] != null))
                {
                    along = z;   // north-south
                }
            }
            if (along >= 0 && along % spacing != 0)
            {
                return;
            }
            // centred just inside the outline, its outer face a little proud of the deck's side
            float proud = heavy ? 0.03f : 0.02f;
            float ox = Mathf.Abs(outward.x) > 0.01f ? Mathf.Sign(outward.x) : 0f;
            float oz = Mathf.Abs(outward.y) > 0.01f ? Mathf.Sign(outward.y) : 0f;
            float cx = x + ox * (proud - half);
            float cz = z + oz * (proud - half);
            WriteBox(w, new Vector3(cx - half, foot, cz - half), new Vector3(cx + half, top, cz + half), col);
        }

        // A deck cell next to around[i] (for its side kind toward it).
        private static IntVec3 DeckNeighbour(int i)
        {
            int[] next = { 1, 2, 3, 0 };
            int[] prev = { 3, 0, 1, 2 };
            if (aroundDeck[next[i]] != null)
            {
                return around[next[i]];
            }
            if (aroundDeck[prev[i]] != null)
            {
                return around[prev[i]];
            }
            return around[(i + 2) & 3];
        }

        // The corner is at least two cells inside the deck along both axes (a wide bridge's middle).
        private static bool WideAround(Map map, int x, int z)
        {
            return DeckAt(map, new IntVec3(x - 2, 0, z)) != null && DeckAt(map, new IntVec3(x + 1, 0, z)) != null
                && DeckAt(map, new IntVec3(x, 0, z - 2)) != null && DeckAt(map, new IntVec3(x, 0, z + 1)) != null;
        }
    }

    // Under and round bridge decks (see Bridges): the water or ground under them, the ramps onto the land, the deck's
    // sides and supports, and the depth of the deck and ramps.
    public class SectionLayer_IsoRimBridges : SectionLayer
    {
        public SectionLayer_IsoRimBridges(Section section) : base(section)
        {
            MapSections.SetChangeFlags(this, "Terrain", "Buildings");
        }

        public override bool Visible
        {
            get { return View3D.Ready; }
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            if (Bridges.Active)
            {
                try
                {
                    Bridges.Print(this, section);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Drawing the bridges failed: " + e, 93611470);
                }
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
