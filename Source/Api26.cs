using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 26: other mods' angled wall pieces in 3D, and other mods' section layers in the gravship take-off and landing
    // scene. Generic: a mod that draws its own wall corners in a flat section layer (gravship armour corners) registers the
    // cells and IsoRim stands 3D pieces there, drawn like its wall blocks.
    public static partial class Api
    {
        // ---- angled wall pieces (Version 26)

        /// <summary>
        /// Registers corner wedges for a wall (Api version 26): in an empty cell between two blocks of that wall the rule
        /// says yes for, IsoRim stands the same 3D wedge as the vanilla gravship hull's corner pieces: half of the cell,
        /// the half against the two legs, as tall as the lower leg, slanted face looking out, with the wall's look and the
        /// colour the rule gives. legA and legB are the two neighbours (directions: two of North, East, South, West, at a
        /// right angle). Only while the wall is drawn as an IsoRim block (3D view, textured 3D walls). changeFlags (the
        /// game's MapMeshFlag bits) are added to what redraws IsoRim's walls layer, for rules that depend on more than
        /// buildings and things. Asked while a block of the map is redrawn, on the main thread; an exception is logged once
        /// and the rule is left out. Call once, at startup.
        /// </summary>
        public static void AddWallCorners(ThingDef wall, CornerRule rule, ulong changeFlags = 0) => WallPieces.AddCorners(wall, rule, changeFlags);

        /// <summary>
        /// Leaves out a flat section layer of another mod while IsoRim's 3D walls stand in for it (Api version 26): after the
        /// layer rebuilds a block, its sub-meshes whose material the filter accepts (all if null) are emptied, so they are
        /// not drawn. Nothing changes with the 3D view or the textured 3D walls off. For a layer that also holds things that
        /// must stay (masks, edges), give a filter for the corner materials only. Call once, at startup.
        /// </summary>
        public static void HideFlatLayer(Type sectionLayer, Func<Material, bool> which = null) => WallPieces.HideLayer(sectionLayer, which);

        /// <summary>
        /// Redraws IsoRim's walls around a cell (Api version 26): for a mod whose corner rule changed in a way the
        /// game's change flags don't report.
        /// </summary>
        public static void RefreshWallShapes(Map map, IntVec3 cell)
        {
            if (map != null && cell.InBounds(map))
            {
                foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(cell, map)))
                {
                    SectionLayer_IsoRimWalls.Mark(map, c);
                }
                SectionLayer_IsoRimWalls.Mark(map, cell);
            }
        }

        // ---- the gravship scene (Version 26)

        /// <summary>
        /// Flies another mod's section layer with the gravship in IsoRim's 3D take-off and landing scene (Api version 26):
        /// the layer's geometry over the ship's cells goes with the ship snapshot. flatPieces: also one ring of cells round
        /// the ship (pieces that lie just past the hull, as the game's hull corner layer). Call once, at startup.
        /// </summary>
        public static void FlyLayer(Type sectionLayer, bool flatPieces = false) => WallPieces.Fly(sectionLayer, flatPieces);

        /// <summary>
        /// Marks a section layer of another mod as no geometry of the ship (a mask, an overlay; Api version 26): it is not
        /// flown and not reported as a layer that is not flown.
        /// </summary>
        public static void NotGeometryLayer(Type sectionLayer) => WallPieces.NotGeometry(sectionLayer);
    }

    /// <summary>Api 26: does the cell get a corner wedge, against which two neighbours (legs), and in which colour.</summary>
    public delegate bool CornerRule(Map map, IntVec3 cell, out IntVec3 legA, out IntVec3 legB, out Color colour);

    // The registries behind Api 26 and the code that uses them (HullCorners, the walls layer, ShipFlight).
    internal static class WallPieces
    {
        internal struct CornerEntry
        {
            public ThingDef wall;
            public CornerRule rule;
        }

        internal static readonly List<CornerEntry> corners = new List<CornerEntry>();
        internal static readonly HashSet<ThingDef> cornerWalls = new HashSet<ThingDef>();
        internal static ulong extraFlags;
        private static readonly Dictionary<Type, Func<Material, bool>> hidden = new Dictionary<Type, Func<Material, bool>>();
        internal static readonly Dictionary<string, int> flown = new Dictionary<string, int>();
        internal static readonly HashSet<string> notGeometry = new HashSet<string>();
        private static readonly HashSet<int> failed = new HashSet<int>();

        public static void AddCorners(ThingDef wall, CornerRule rule, ulong flags)
        {
            if (wall == null || rule == null)
            {
                return;
            }
            corners.Add(new CornerEntry { wall = wall, rule = rule });
            cornerWalls.Add(wall);
            extraFlags |= flags;
        }

        public static void Fly(Type layer, bool ring)
        {
            if (layer != null && typeof(SectionLayer).IsAssignableFrom(layer))
            {
                flown[layer.Name] = ring ? 2 : 1;
            }
        }

        public static void NotGeometry(Type layer)
        {
            if (layer != null)
            {
                notGeometry.Add(layer.Name);
            }
        }

        private static void Failed(object rule, Exception e)
        {
            if (failed.Add(rule.GetHashCode()))
            {
                Log.Error("[IsoRim] A registered wall piece rule failed (left out from now on): " + e);
            }
        }

        // A registered corner for the cell (the first rule that says yes), as legs, colour and the wall it belongs to.
        public static bool Corner(Map map, IntVec3 c, out IntVec3 a, out IntVec3 b, out Color colour, out ThingDef wall)
        {
            a = b = IntVec3.Invalid;
            colour = Color.white;
            wall = null;
            for (int i = 0; i < corners.Count; i++)
            {
                CornerEntry e = corners[i];
                if (failed.Contains(e.rule.GetHashCode()))
                {
                    continue;
                }
                try
                {
                    if (e.rule(map, c, out a, out b, out colour) && a.IsValid && b.IsValid && a != b && a.x * b.x + a.z * b.z == 0
                        && a.LengthHorizontalSquared == 1 && b.LengthHorizontalSquared == 1)
                    {
                        wall = e.wall;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Failed(e.rule, ex);
                }
            }
            return false;
        }

        // ---- hidden flat layers

        public static void HideLayer(Type layer, Func<Material, bool> which)
        {
            if (layer == null || !typeof(SectionLayer).IsAssignableFrom(layer) || hidden.ContainsKey(layer))
            {
                return;
            }
            var regen = AccessTools.Method(layer, "Regenerate");
            if (regen == null || regen.IsAbstract)
            {
                ModLog.Warn("[IsoRim] HideFlatLayer: " + layer.FullName + ".Regenerate not found; the layer stays.");
                return;
            }
            hidden[layer] = which;
            try
            {
                new Harmony("isorim.api26").Patch(regen, postfix: new HarmonyMethod(typeof(WallPieces), nameof(Regenerate_Postfix)));
            }
            catch (Exception e)
            {
                hidden.Remove(layer);
                ModLog.Warn("[IsoRim] HideFlatLayer: could not hook " + layer.FullName + ": " + e.Message);
            }
        }

        public static void Regenerate_Postfix(SectionLayer __instance)
        {
            Func<Material, bool> which;
            if (__instance == null || !hidden.TryGetValue(__instance.GetType(), out which))
            {
                return;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            if (!View3D.Active || !st.walls3D || !st.texturedWalls)
            {
                return;
            }
            try
            {
                List<LayerSubMesh> subs = __instance.subMeshes;
                for (int i = 0; i < subs.Count; i++)
                {
                    if (subs[i].verts.Count > 0 && (which == null || (subs[i].material != null && which(subs[i].material))))
                    {
                        subs[i].Clear(MeshParts.All);   // not finalized: not drawn
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Hiding a flat layer failed: " + e, 93626001);
            }
        }
    }
}
