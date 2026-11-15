using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Holes sunk into the ground (ModelDef.sinkHole: the pit gate, pit burrows). The model reaches below the ground
    // (Baking.Bake lets it), and two things make the ground open there and only there:
    //   1. the cut: the ground cells the opening disc touches (CutAt) are left out of every layer the game paints on the
    //      ground (terrain with its blends and the water layer, terrain edges, scatter, snow, sand: CutGround, a postfix
    //      of each layer's Regenerate), so nothing at ground level is drawn over the hole. The model's apron covers the
    //      cut cells outside the opening;
    //   2. the ground's depth: SectionLayer_IsoRimSinkHoles writes depth only (no colour), just under the ground, for the
    //      uncut cells within Reach of a hole, before any model is drawn (queue 1990): the near ground then hides the
    //      shaft, which only shows through the opening. (Measured 2026-11-15, tools/runners/hidebelow: the game's terrain
    //      and water write no depth, but they are drawn after IsoRim's models (queue 2000 + their renderPrecedence, depth
    //      tested against the models' depth), so they hide what of a model is below them on their own as well.)
    // Only while the 3D view is on and the thing is drawn as its model; ground floor only (ProjectZ floors are untouched).
    // A hole that appears or goes dirties the ground round it (NoteChange, from Patches.NoteShapeChange); a settings
    // change rebuilds the whole map anyway.
    // Call sites: Baking.Bake (below-ground corners, quarter turns), Patches.NoteShapeChange (spawn / despawn),
    // Patches.SinkCut_Postfix (SectionLayer_Terrain / _TerrainEdges / _TerrainScatter / _Snow / _Sand .Regenerate; the
    // game's water layer is a SectionLayer_Terrain), this layer.
    [StaticConstructorOnStartup]
    public static class SinkHoles
    {
        public const int Reach = 9;            // cells round a hole whose ground writes depth: 3 cells deep seen at 20 degrees
        private const float MaskDrop = 0.003f;  // that depth sits this far (true cells) under the ground

        private static List<ThingDef> defs;
        private static Material mask;
        private static bool triedMask;

        private static List<ThingDef> Defs
        {
            get
            {
                if (defs == null)
                {
                    defs = new List<ThingDef>();
                    foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs)
                    {
                        ModelEntry e;
                        if (ModelLibrary.TryGet(d, out e) && e.HasModel && e.def.sinkHole > 0f)
                        {
                            defs.Add(d);
                        }
                    }
                }
                return defs;
            }
        }

        public static bool IsHoleDef(ThingDef d)
        {
            return d != null && Defs.Contains(d);
        }

        // The opening (centre x, z and radius) of a hole cut now, or false (3D off, drawn flat, not spawned).
        private static bool Opening(Thing t, out float cx, out float cz, out float r)
        {
            cx = cz = r = 0f;
            if (t == null || !t.Spawned || !View3D.Active)
            {
                return false;
            }
            ThingForm f = Forms.For(t.def);
            if (f.form != Form3D.Model || f.model == null || f.model.def.sinkHole <= 0f)
            {
                return false;
            }
            Vector3 c = t.TrueCenter();
            Vector3 fit = f.model.Fit(t.def) * f.model.def.scale;
            cx = c.x;
            cz = c.z;
            r = f.model.def.sinkHole * Mathf.Min(fit.x, fit.z);
            return true;
        }

        // Whether the opening disc overlaps the cell (by more than a hair).
        private static bool Touches(float cx, float cz, float r, IntVec3 c)
        {
            float dx = Mathf.Clamp(cx, c.x, c.x + 1f) - cx;
            float dz = Mathf.Clamp(cz, c.z, c.z + 1f) - cz;
            return dx * dx + dz * dz < (r - 0.01f) * (r - 0.01f);
        }

        private struct Hole
        {
            public CellRect rect;   // the footprint
            public float cx, cz, r;
        }

        private static readonly List<Hole> near = new List<Hole>();
        private static readonly HarmonyLib.AccessTools.FieldRef<SectionLayer, Section> layerSection = HarmonyLib.AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        // The holes cut now whose footprint, grown by reach, meets the rectangle (into near). False if there are none.
        private static bool Near(Map map, CellRect area, int reach)
        {
            near.Clear();
            if (map == null || !View3D.Active)
            {
                return false;
            }
            List<ThingDef> ds = Defs;
            for (int d = 0; d < ds.Count; d++)
            {
                List<Thing> things = map.listerThings.ThingsOfDef(ds[d]);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    float cx, cz, r;
                    if (!Opening(t, out cx, out cz, out r))
                    {
                        continue;
                    }
                    CellRect rect = t.OccupiedRect();
                    if (!rect.ExpandedBy(reach).Overlaps(area))
                    {
                        continue;
                    }
                    near.Add(new Hole { rect = rect, cx = cx, cz = cz, r = r });
                }
            }
            return near.Count > 0;
        }

        private static bool CutIn(IntVec3 c)
        {
            for (int i = 0; i < near.Count; i++)
            {
                Hole h = near[i];
                if (h.rect.Contains(c) && Touches(h.cx, h.cz, h.r, c))
                {
                    return true;
                }
            }
            return false;
        }

        // Whether the ground of this cell is cut out under a hole now.
        public static bool CutAt(Map map, IntVec3 c)
        {
            return Near(map, CellRect.SingleCell(c), 0) && CutIn(c);
        }

        // Postfix of the ground layers' Regenerate: what they put on the cut cells is taken out again. Layers whose
        // geometry is built once and only recoloured (snow, sand: 9 corners a cell, the cell's middle last) get those
        // cells' corners clear instead; the others lose the triangles whose middle is in a cut cell.
        public static void CutGround(SectionLayer layer, bool recoloured)
        {
            Section section = layer != null ? layerSection(layer) : null;
            if (section == null || !View3D.Active)
            {
                return;
            }
            Map map = section.map;
            if (!Near(map, section.CellRect, 0))
            {
                return;
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            for (int s = 0; s < subs.Count; s++)
            {
                LayerSubMesh sm = subs[s];
                if (sm == null || !sm.finalized || sm.mesh == null)
                {
                    continue;
                }
                if (recoloured)
                {
                    ClearCorners(sm);
                }
                else
                {
                    DropTriangles(sm);
                }
            }
        }

        private static void DropTriangles(LayerSubMesh sm)
        {
            List<int> tris = sm.tris;
            List<Vector3> verts = sm.verts;
            if (tris.Count == 0 || verts.Count == 0)
            {
                return;
            }
            int kept = 0;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
                IntVec3 cell = new IntVec3(Mathf.FloorToInt((a.x + b.x + c.x) / 3f), 0, Mathf.FloorToInt((a.z + b.z + c.z) / 3f));
                if (CutIn(cell))
                {
                    continue;
                }
                tris[kept] = tris[i];
                tris[kept + 1] = tris[i + 1];
                tris[kept + 2] = tris[i + 2];
                kept += 3;
            }
            if (kept == tris.Count)
            {
                return;
            }
            tris.RemoveRange(kept, tris.Count - kept);
            if (kept == 0)
            {
                sm.finalized = false;   // nothing left: not drawn until the layer is rebuilt
                return;
            }
            sm.mesh.SetTriangles(tris, 0);
        }

        private static void ClearCorners(LayerSubMesh sm)
        {
            List<Vector3> verts = sm.verts;
            List<Color32> cols = sm.colors;
            if (verts.Count == 0 || cols.Count != verts.Count || verts.Count % 9 != 0)
            {
                return;
            }
            bool any = false;
            for (int v = 0; v < verts.Count; v += 9)
            {
                Vector3 mid = verts[v + 8];
                if (!CutIn(new IntVec3(Mathf.FloorToInt(mid.x), 0, Mathf.FloorToInt(mid.z))))
                {
                    continue;
                }
                for (int k = v; k < v + 9; k++)
                {
                    Color32 c = cols[k];
                    c.a = 0;
                    cols[k] = c;
                }
                any = true;
            }
            if (any)
            {
                sm.mesh.SetColors(cols);
            }
        }

        // A hole appeared or goes: the ground under it (terrain, snow, sand) and the ground's depth round it are redrawn.
        public static void NoteChange(Thing t, Map map)
        {
            if (t == null || map == null || !IsHoleDef(t.def))
            {
                return;
            }
            ulong under = MapMeshFlagDefOf.Terrain;
            if (MapMeshFlagDefOf.Snow != null)
            {
                under |= MapMeshFlagDefOf.Snow;
            }
            if (MapMeshFlagDefOf.Sand != null)
            {
                under |= MapMeshFlagDefOf.Sand;
            }
            CellRect foot = t.OccupiedRect();
            foreach (IntVec3 c in foot.ExpandedBy(Reach).ClipInsideMap(map))
            {
                map.mapDrawer.MapMeshDirty(c, foot.Contains(c) ? under : (ulong)MapMeshFlagDefOf.Terrain);
            }
        }

        // ---- the ground's depth round the holes (SectionLayer_IsoRimSinkHoles)

        private static Material Mask
        {
            get
            {
                if (!triedMask)
                {
                    triedMask = true;
                    // the sky's depth plane program (depth only, no colour), drawn before the models
                    mask = R3DMaterials.Make("IsoRimDepthPlane", "IsoRim_SinkHoleGround", 1990);
                }
                return mask;
            }
        }

        public static void PrintGround(SectionLayer layer, Section section)
        {
            Map map = section.map;
            if (!Near(map, section.CellRect, Reach) || Mask == null)
            {
                return;
            }
            int groundRows = Floors.GroundRowsOf(map);
            LayerSubMesh sm = layer.GetSubMesh(Mask);
            float y = -MaskDrop / View3D.Squash;
            TerrainGrid terrain = map.terrainGrid;
            foreach (IntVec3 c in section.CellRect)
            {
                if (groundRows > 0 && c.z >= groundRows)
                {
                    continue;   // a stacked floor's rows: not ground
                }
                bool inReach = false;
                for (int i = 0; i < near.Count; i++)
                {
                    if (near[i].rect.ExpandedBy(Reach).Contains(c))
                    {
                        inReach = true;
                        break;
                    }
                }
                if (!inReach || CutIn(c))
                {
                    continue;
                }
                TerrainDef td = terrain.TerrainAt(c);
                if (td == null || td.dontRender || Ground.IsWater(td))
                {
                    continue;   // no ground there (space), or IsoRim's water, drawn below the ground
                }
                int b = sm.verts.Count;
                sm.verts.Add(new Vector3(c.x, y, c.z));
                sm.verts.Add(new Vector3(c.x, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z));
                sm.tris.Add(b);
                sm.tris.Add(b + 1);
                sm.tris.Add(b + 2);
                sm.tris.Add(b);
                sm.tris.Add(b + 2);
                sm.tris.Add(b + 3);
            }
        }

        // Harness report: the cut cells and the ground-depth cells round each hole on the map.
        public static string Report(Map map)
        {
            if (!Near(map, new CellRect(0, 0, map.Size.x, map.Size.z), 0))
            {
                return "no holes cut";
            }
            List<string> parts = new List<string>();
            foreach (Hole h in near)
            {
                int cut = 0;
                foreach (IntVec3 c in h.rect)
                {
                    if (Touches(h.cx, h.cz, h.r, c))
                    {
                        cut++;
                    }
                }
                parts.Add(h.rect + " r " + h.r.ToString("0.00") + " cut " + cut);
            }
            return string.Join("; ", parts);
        }
    }

    // The ground's depth round sunk holes (SinkHoles.PrintGround): depth only, so the near ground hides what is below it.
    public class SectionLayer_IsoRimSinkHoles : SectionLayer
    {
        public SectionLayer_IsoRimSinkHoles(Section section) : base(section)
        {
            MapSections.SetChangeFlags(this, "Terrain");
        }

        public override bool Visible
        {
            get { return View3D.Ready; }
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            if (!View3D.Active)
            {
                return;
            }
            try
            {
                SinkHoles.PrintGround(this, section);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the ground round a sunk hole failed: " + e, 93611390);
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
