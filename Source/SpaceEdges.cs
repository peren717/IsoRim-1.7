using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using EdgeType = RimWorld.SectionLayer_TerrainEdges.EdgeType;

namespace IsoRim
{
    // Odyssey's space-edge terrain in 3D: orbital and mechanoid platforms, asteroid rock and every other terrain
    // with a spaceEdgeGraphicData (all natural terrain and the generated stone floors get the asteroid set; mods
    // can add their own through the def field) reads as a slab floating in space.
    //
    // In 2D the game (SectionLayer_TerrainEdges, space maps only) paints two things: a border on the platform's
    // own top cells along the rim (OShape/UShape/Corner/Flat), which stays as it is, and the "loop" strip, the
    // platform's machinery side seen from above, laid flat on the void south of every south rim. In 3D that strip
    // is removed and the rim is built from a kit of 3D pieces (Models\space_edge_<style>_<piece>.obj) hanging
    // under the cell's own rim:
    //   - per side facing the void: a full straight piece when the run goes on past both ends (the cells to the
    //     left and right of that side are solid), picked by the outside cell's hash; otherwise a half piece on
    //     each half whose neighbour is solid (a lone tip gets none there: its corners close it);
    //   - per quadrant: an outer corner when both of its sides face the void, an inner corner when both are solid
    //     and the diagonal cell is void;
    //   - styles: MechanoidPlatform -> mech, OrbitalPlatform -> orbital, any other edge terrain -> asteroid, whose
    //     stuff_sedge_* faces take the terrain's edge colour (overrideColor, else its colour) like the game's art.
    // The pieces are built facing north / the north-east quadrant and turned a quarter clockwise per step.
    // Drawn before the ground (queue 1999 with depth, as the game's own edge shader), so a platform's own floor
    // and anything nearer hides the sides behind it: all four sides are built once and nothing is redrawn when the
    // camera turns. Lit like the models (sun by face direction), the light of the cell from the overlay; no outline.
    // Which cells, the void test and the map gate are the game's (ShouldDrawRockEdges / Regenerate). With the 3D
    // view off nothing changes.
    [StaticConstructorOnStartup]
    public static class SpaceEdges
    {
        // Harness only (probe spaceedges=0): the game's flat strips for before/after shots.
        public static bool Enabled = true;

        public const int Queue = 1999;   // the game's TerrainEdge queue: before the ground (2000), which then covers the far sides

        public const int Mech = 0;
        public const int Orbital = 1;
        public const int Asteroid = 2;
        private static readonly string[] StyleNames = { "mech", "orbital", "asteroid" };
        private static readonly int[] StraightCount = { 4, 4, 3 };

        private static HashSet<Material> loopMaterials;
        private static Material litMaterial;
        private static bool kitTried;
        private static Kit[] kits;

        // One style's pieces.
        public class Kit
        {
            public Model[] straights;
            public Model half;
            public Model outer;
            public Model inner;
        }

        static SpaceEdges()
        {
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            try
            {
                var target = AccessTools.Method(typeof(SectionLayer_TerrainEdges), nameof(SectionLayer_TerrainEdges.Regenerate));
                if (target == null)
                {
                    ModLog.Warn("[IsoRim] SectionLayer_TerrainEdges.Regenerate not found: the game's flat space-edge strips stay beside the 3D sides.");
                    return;
                }
                new Harmony("isorim.spaceedges").Patch(target, postfix: new HarmonyMethod(typeof(SpaceEdges), nameof(Regenerate_Postfix)));
                ModLog.Note("[IsoRim] Hooked SectionLayer_TerrainEdges.Regenerate (3D platform sides).");
            }
            catch (Exception e)
            {
                ModLog.Warn("[IsoRim] Could not hook the game's space-edge strips: " + e.Message);
            }
        }

        public static bool Active
        {
            get { return Enabled && View3D.Active && ModsConfig.OdysseyActive; }
        }

        // The game's gate (SectionLayer_TerrainEdges.Regenerate): space maps and maps without a world tile.
        public static bool DrawsOn(Map map)
        {
            if (map == null)
            {
                return false;
            }
            PlanetTile tile = map.Tile;
            return !tile.Valid || (tile.LayerDef != null && tile.LayerDef.isSpace);
        }

        // A map out in space (orbit, space platforms, asteroids): on a space layer of the world, or made by a map
        // generator the game draws the planet behind (MapGeneratorDef.renderWorld: the Space maps, pocket ones included).
        // The one test for "is space" in IsoRim's own looks (the sky); DrawsOn above stays the game's edge-layer gate.
        public static bool IsSpaceMap(Map map)
        {
            if (map == null)
            {
                return false;
            }
            if (map.generatorDef != null && map.generatorDef.renderWorld)
            {
                return true;
            }
            PlanetTile tile = map.Tile;
            return tile.Valid && tile.LayerDef != null && tile.LayerDef.isSpace;
        }

        // After the game rebuilds its edge layer: while the 3D sides are drawn, its flat loop strips
        // (LoopLeft/Right/Single/Loop of every edge set) are emptied; the rim border on the top cells stays.
        public static void Regenerate_Postfix(SectionLayer_TerrainEdges __instance)
        {
            if (!Active || __instance.subMeshes.Count == 0 || !DrawsOn(MapSections.MapOf(__instance)))
            {
                return;   // (the game draws nothing on other maps)
            }
            try
            {
                HashSet<Material> loops = LoopMaterials();
                List<LayerSubMesh> subs = __instance.subMeshes;
                for (int i = 0; i < subs.Count; i++)
                {
                    if (subs[i].material != null && loops.Contains(subs[i].material) && subs[i].verts.Count > 0)
                    {
                        subs[i].Clear(MeshParts.All);   // not finalized: not drawn, and left out of the culling boxes
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Hiding the game's flat space-edge strips failed: " + e, 93611451);
            }
        }

        private static HashSet<Material> LoopMaterials()
        {
            if (loopMaterials != null)
            {
                return loopMaterials;
            }
            HashSet<Material> set = new HashSet<Material>();
            foreach (TerrainDef t in DefDatabase<TerrainDef>.AllDefsListForReading)
            {
                TerrainDef.SpaceEdgeGraphicData data = t.spaceEdgeGraphicData;
                if (data == null)
                {
                    continue;
                }
                try
                {
                    set.Add(data.GetMaterial(t, EdgeType.LoopLeft));
                    set.Add(data.GetMaterial(t, EdgeType.LoopRight));
                    set.Add(data.GetMaterial(t, EdgeType.LoopSingle));
                    int n = data.LoopTexPaths != null ? data.LoopTexPaths.Count : 0;
                    for (int i = 0; i < n; i++)
                    {
                        set.Add(data.GetMaterial(t, EdgeType.Loop, i));
                    }
                }
                catch (Exception e)
                {
                    ModLog.Warn("[IsoRim] Space-edge set of " + t.defName + " could not be read: " + e.Message);
                }
            }
            set.Remove(null);
            loopMaterials = set;
            return set;
        }

        public static int StyleOf(TerrainDef t)
        {
            if (t.defName == "MechanoidPlatform")
            {
                return Mech;
            }
            return t.defName == "OrbitalPlatform" ? Orbital : Asteroid;
        }

        // The pieces of a style, loaded on first use; null if any of its files is missing.
        public static Kit KitOf(int style)
        {
            if (!kitTried)
            {
                kitTried = true;
                kits = new Kit[StyleNames.Length];
                string dir = IsoRimMod.RootDir != null ? Path.Combine(IsoRimMod.RootDir, "Models") : null;
                for (int i = 0; dir != null && i < StyleNames.Length; i++)
                {
                    string pre = Path.Combine(dir, "space_edge_" + StyleNames[i] + "_");
                    Kit k = new Kit();
                    k.straights = new Model[StraightCount[i]];
                    bool all = true;
                    for (int v = 0; v < k.straights.Length; v++)
                    {
                        k.straights[v] = ModelLibrary.LoadFile(pre + "straight_" + (char)('a' + v) + ".obj");
                        all &= k.straights[v] != null;
                    }
                    k.half = ModelLibrary.LoadFile(pre + "half.obj");
                    k.outer = ModelLibrary.LoadFile(pre + "outer.obj");
                    k.inner = ModelLibrary.LoadFile(pre + "inner.obj");
                    if (all && k.half != null && k.outer != null && k.inner != null)
                    {
                        kits[i] = k;
                    }
                    else
                    {
                        ModLog.Warn("[IsoRim] Space-edge pieces of the " + StyleNames[i] + " set are missing: those platform sides are not drawn.");
                    }
                }
            }
            return kits != null && style >= 0 && style < kits.Length ? kits[style] : null;
        }

        // The plain material lit like the models (sun by face direction), drawn in the game's edge queue. Lit by the
        // overlay, not by an own-cell mark: the far sides the ground covers must stay unmarked.
        public static Material LitMaterial
        {
            get
            {
                if (litMaterial == null)
                {
                    litMaterial = R3DMaterials.Make(R3DMaterials.SolidDef, "IsoRim_SpaceEdge", Queue, R3DMaterials.LightModel);
                    IsoGlow.Use(litMaterial, IsoGlow.None);
                }
                return litMaterial;
            }
        }

        // Writes one piece turned k quarter turns clockwise seen from above ((x, z) -> (z, -x)), its origin at
        // (x, z) and its top at the rim. Its own vertex write: the pieces hang below the ground, which the model
        // baker would clamp away (it keeps y >= 0).
        public static bool Write(LayerSubMesh sm, Model m, int k, float x, float z, float top, float squash, Color tint)
        {
            int n = m.positions.Length;
            if (sm.verts.Count + n > MeshWriter.MaxVerts)
            {
                return false;
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m.positions[i];
                float px = p.x;
                float pz = p.z;
                for (int r = 0; r < (k & 3); r++)
                {
                    float t = px;
                    px = pz;
                    pz = -t;
                }
                Color c = m.colors[i];
                if (m.tinted[i])
                {
                    c *= tint;
                }
                sm.tris.Add(sm.verts.Count);
                sm.verts.Add(new Vector3(x + px, top + p.y / squash, z + pz));
                sm.colors.Add(R3DMaterials.Shaded(c, 1f));
                sm.uvs.Add(default);
            }
            return true;
        }
    }

    // The 3D sides of space-edge terrain (see SpaceEdges).
    public class SectionLayer_IsoRimSpaceEdges : SectionLayer
    {
        private static readonly IntVec3[] Sides = { IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West };
        private static readonly bool[] voids = new bool[4];

        public SectionLayer_IsoRimSpaceEdges(Section section) : base(section)
        {
            relevantChangeTypes = MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible
        {
            get { return View3D.Ready && SpaceEdges.Enabled; }
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            if (!SpaceEdges.Active || !SpaceEdges.DrawsOn(map))
            {
                FinalizeMesh(MeshParts.All);
                return;
            }
            try
            {
                TerrainGrid grid = map.terrainGrid;
                CellRect r = section.CellRect;
                // Stacked floors (Floors): only the ground rows; rows above count as outside the map.
                int h = Floors.DrawnRows(map);
                r.maxZ = Mathf.Min(r.maxZ, h - 1);
                float squash = View3D.Squash;
                float top = -0.002f / squash;
                bool clippers = map.DrawMapClippers;
                Material mat = SpaceEdges.LitMaterial;
                LayerSubMesh sm = mat != null ? MeshWriter.Sub(this, mat) : null;
                foreach (IntVec3 c in r)
                {
                    if (sm == null)
                    {
                        break;
                    }
                    TerrainDef t = grid.BaseTerrainAt(c);
                    if (t == null || t.spaceEdgeGraphicData == null)
                    {
                        continue;
                    }
                    bool any = false;
                    for (int s = 0; s < 4; s++)
                    {
                        voids[s] = VoidAt(map, grid, c + Sides[s], h, clippers);
                        any |= voids[s];
                    }
                    if (!any)
                    {
                        continue;
                    }
                    SpaceEdges.Kit kit = SpaceEdges.KitOf(SpaceEdges.StyleOf(t));
                    if (kit == null)
                    {
                        continue;
                    }
                    Color tint = t.spaceEdgeGraphicData.overrideColor ?? t.color;
                    tint.a = 1f;
                    float cx = c.x + 0.5f;
                    float cz = c.z + 0.5f;
                    for (int s = 0; s < 4; s++)
                    {
                        if (!voids[s])
                        {
                            continue;
                        }
                        int left = (s + 3) & 3;
                        int right = (s + 1) & 3;
                        if (!voids[left] && !voids[right])
                        {
                            int hash = (c + Sides[s]).GetHashCode();
                            int v = (hash == int.MinValue ? 0 : Math.Abs(hash)) % kit.straights.Length;
                            SpaceEdges.Write(sm, kit.straights[v], s, cx, cz, top, squash, tint);
                            continue;
                        }
                        for (int j = 0; j < 2; j++)
                        {
                            int e = j == 0 ? left : right;
                            if (!voids[e])
                            {
                                IntVec3 d = Sides[e];
                                SpaceEdges.Write(sm, kit.half, s, cx + 0.25f * d.x, cz + 0.25f * d.z, top, squash, tint);
                            }
                        }
                    }
                    for (int q = 0; q < 4; q++)
                    {
                        int b = (q + 1) & 3;
                        if (voids[q] && voids[b])
                        {
                            SpaceEdges.Write(sm, kit.outer, q, cx, cz, top, squash, tint);
                        }
                        else if (!voids[q] && !voids[b] && VoidAt(map, grid, c + Sides[q] + Sides[b], h, clippers))
                        {
                            SpaceEdges.Write(sm, kit.inner, q, cx, cz, top, squash, tint);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing the sides of space platforms failed: " + e, 93611452);
            }
            FinalizeMesh(MeshParts.All);
        }

        // The game's ShouldDrawRockEdges: a void neighbour (dontRender: space), or past the edge of a map without
        // edge sheets. Rows above the ground rows (stacked floors) count as outside the map.
        private static bool VoidAt(Map map, TerrainGrid grid, IntVec3 o, int h, bool clippers)
        {
            if (!o.InBounds(map) || o.z >= h)
            {
                return !clippers;
            }
            TerrainDef t = grid.TerrainAt(o);
            return t == null || t.dontRender;
        }
    }
}
