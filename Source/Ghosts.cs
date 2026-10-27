using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Things still to be built shown as see-through 3D ghosts of what they will become, in
    // place of the game's flat blue sprites: blueprints in blue, frames under construction in
    // pale sawdust, and the preview under the pointer while choosing where to build.
    [StaticConstructorOnStartup]
    public static class Ghosts
    {
        public static readonly Color BlueprintColor = new Color(0.42f, 0.70f, 1f, 0.42f);
        public static readonly Color FrameColor = new Color(0.96f, 0.86f, 0.64f, 0.46f);

        // What a blueprint or frame will become, or null.
        public static ThingDef Target(Thing t)
        {
            if (!(t is Blueprint) && !(t is Frame))
            {
                return null;
            }
            return t.def.entityDefToBuild as ThingDef;
        }

        // Writes the ghost of a blueprint or frame into a block of the map. Returns false if the
        // thing it will become has no 3D shape, so the game's own sprite should be drawn.
        public static bool Print(SectionLayer layer, Thing t)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            ThingDef target = Target(t);
            if (!st.ghosts3D || target == null)
            {
                return false;
            }
            ThingForm form = Forms.For(target);
            Color col = t is Frame ? FrameColor : BlueprintColor;
            // Inside a solid building (a cooler set into a wall line): the x-ray ghost (Occupied, see DrawGhost).
            bool xray = !FixOff && Occupied(target, t.Position, t.Rotation, t.Map, t);
            try
            {
                if (form.form == Form3D.Model && form.model != null)
                {
                    ModelBaker.Ghost = col;
                    ModelBaker.GhostXray = xray;
                    try
                    {
                        return ModelBaker.Bake(layer, t, form.model, 0f);
                    }
                    finally
                    {
                        ModelBaker.Ghost = new Color(0f, 0f, 0f, 0f);
                        ModelBaker.GhostXray = false;
                    }
                }
                if (form.form == Form3D.Block)
                {
                    return Box(layer, t.Map, t.Position, form.height, col, xray);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a blueprint ghost failed: " + e, 93611130);
            }
            return false;
        }

        // A see-through block one cell across and height cells high, for walls still to be built.
        // Toward another such block at least as tall the side is left out and the block runs on
        // to the cell edge, so a run of them reads as one block: seen through the near block,
        // the sides between them showed as inner edges or not, depending on which block happened
        // to be drawn first.
        private static bool Box(SectionLayer layer, Map map, IntVec3 c, float height, Color col, bool xray)
        {
            Material mat = xray && R3DMaterials.GhostXray != null ? R3DMaterials.GhostXray : R3DMaterials.Ghost;
            LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
            if (sm == null || sm.verts.Count + 20 > MeshWriter.MaxVerts)
            {
                return false;
            }
            bool south = BoxAt(map, c + IntVec3.South, height);
            bool north = BoxAt(map, c + IntVec3.North, height);
            bool west = BoxAt(map, c + IntVec3.West, height);
            bool east = BoxAt(map, c + IntVec3.East, height);
            float y = height / View3D.Squash;
            float x0 = c.x + (west ? 0f : 0.02f);
            float z0 = c.z + (south ? 0f : 0.02f);
            float x1 = c.x + (east ? 1f : 0.98f);
            float z1 = c.z + (north ? 1f : 0.98f);
            Color32 top = col;
            Color32 side = new Color(col.r * 0.78f, col.g * 0.78f, col.b * 0.78f, col.a);
            MeshWriter.Quad(sm, new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), top);
            if (!south)
            {
                MeshWriter.Quad(sm, new Vector3(x0, 0f, z0), new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, 0f, z0), side);
            }
            if (!north)
            {
                MeshWriter.Quad(sm, new Vector3(x1, 0f, z1), new Vector3(x1, y, z1), new Vector3(x0, y, z1), new Vector3(x0, 0f, z1), side);
            }
            if (!west)
            {
                MeshWriter.Quad(sm, new Vector3(x0, 0f, z1), new Vector3(x0, y, z1), new Vector3(x0, y, z0), new Vector3(x0, 0f, z0), side);
            }
            if (!east)
            {
                MeshWriter.Quad(sm, new Vector3(x1, 0f, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x1, 0f, z1), side);
            }
            return true;
        }

        // True if a ghost box at least this tall is drawn in the cell: a blueprint or frame (one
        // the game prints into the map mesh) of a block. Placing or removing one redraws the
        // cells next to it too (blocks link to their neighbours).
        private static bool BoxAt(Map map, IntVec3 c, float height)
        {
            if (map == null || !c.InBounds(map))
            {
                return false;
            }
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                ThingDef target = Target(t);
                if (target == null || t.def.drawerType == DrawerType.RealtimeOnly)
                {
                    continue;
                }
                ThingForm form = Forms.For(target);
                if (form.form == Form3D.Block && form.height >= height - 0.01f)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- bridges still to be built (2026-10-26, bridgeblueprint)
        // A blueprint of a bridge terrain (any TerrainDef.bridge, the ones Bridges draws in 3D) is a see-through ghost of the
        // deck it will become: the deck's top at Bridges.Deck and its slab along the open sides, joined to the next deck or
        // bridge blueprint. The game's flat blueprint lay at the ground, hidden under the 3D water (drawn after it). Frames keep
        // the game's sprite (drawn every frame, over the water). Ground rows only, as Bridges.

        // The bridge a blueprint will lay, or null.
        public static TerrainDef BridgeTarget(Thing t)
        {
            TerrainDef td = t is Blueprint ? t.def.entityDefToBuild as TerrainDef : null;
            return td != null && td.bridge ? td : null;
        }

        // Writes the deck ghost of a bridge blueprint. False: not a bridge blueprint, or no ghost (ghosts off, an upper floor).
        public static bool PrintBridge(SectionLayer layer, Thing t)
        {
            TerrainDef bridge = BridgeTarget(t);
            if (bridge == null || !IsoRimMod.Settings.ghosts3D || !Bridges.CanDeck(t.Map, t.Position))
            {
                return false;
            }
            Material mat = R3DMaterials.Ghost;
            LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
            if (sm == null || sm.verts.Count + 20 > MeshWriter.MaxVerts)
            {
                return false;
            }
            Map map = t.Map;
            IntVec3 c = t.Position;
            bool south = DeckOrPlanned(map, c + IntVec3.South);
            bool north = DeckOrPlanned(map, c + IntVec3.North);
            bool west = DeckOrPlanned(map, c + IntVec3.West);
            bool east = DeckOrPlanned(map, c + IntVec3.East);
            float y = Bridges.Deck / View3D.Squash;
            float y0 = (Bridges.Deck - Bridges.SlabOf(bridge)) / View3D.Squash;
            float x0 = c.x + (west ? 0f : 0.02f);
            float z0 = c.z + (south ? 0f : 0.02f);
            float x1 = c.x + (east ? 1f : 0.98f);
            float z1 = c.z + (north ? 1f : 0.98f);
            Color col = BlueprintColor;
            Color32 top = col;
            Color32 side = new Color(col.r * 0.78f, col.g * 0.78f, col.b * 0.78f, col.a);
            MeshWriter.Quad(sm, new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), top);
            if (!south)
            {
                MeshWriter.Quad(sm, new Vector3(x0, y0, z0), new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y0, z0), side);
            }
            if (!north)
            {
                MeshWriter.Quad(sm, new Vector3(x1, y0, z1), new Vector3(x1, y, z1), new Vector3(x0, y, z1), new Vector3(x0, y0, z1), side);
            }
            if (!west)
            {
                MeshWriter.Quad(sm, new Vector3(x0, y0, z1), new Vector3(x0, y, z1), new Vector3(x0, y, z0), new Vector3(x0, y0, z0), side);
            }
            if (!east)
            {
                MeshWriter.Quad(sm, new Vector3(x1, y0, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x1, y0, z1), side);
            }
            return true;
        }

        // A finished deck or a bridge blueprint in the cell (a deck ghost runs on into it).
        private static bool DeckOrPlanned(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map) || !Bridges.CanDeck(map, c))
            {
                return false;
            }
            if (Bridges.DeckAt(map, c) != null)
            {
                return true;
            }
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
            {
                if (BridgeTarget(things[i]) != null)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- flat blueprints over the 3D water
        // A blueprint drawn with the game's flat sprite (no 3D ghost: terrain other than bridges, things without a 3D shape,
        // ghosts off) lies at the ground, and the 3D water (SectionLayer_IsoRimWater, queue 3050 and up) is drawn after the
        // game's blueprint materials (2950): it hid them. In water cells what such a blueprint printed is moved to a copy of
        // its material drawn after the water. Begin in TakePrintFrom's prefix, End in its finalizer.

        public const int OverWaterQueue = 3060;   // after the water, its ripples and flow (3050-3054) and the bridges' shade (3052)

        private static SectionLayer flatLayer;
        private static int[] flatVerts;
        private static int[] flatTris;
        private static readonly Dictionary<Material, Material> overWater = new Dictionary<Material, Material>();

        public static void BeginFlatOverWater(SectionLayer layer, Thing t)
        {
            flatLayer = null;
            if (!(t is Blueprint) || t.Map == null || !IsoRimMod.Settings.waterStyle || !SectionLayer_IsoRimWater.IsWater(t.Map, t.Position))
            {
                return;
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            flatVerts = new int[subs.Count];
            flatTris = new int[subs.Count];
            for (int i = 0; i < subs.Count; i++)
            {
                flatVerts[i] = subs[i].verts.Count;
                flatTris[i] = subs[i].tris.Count;
            }
            flatLayer = layer;
        }

        public static void EndFlatOverWater()
        {
            SectionLayer layer = flatLayer;
            flatLayer = null;
            if (layer == null)
            {
                return;
            }
            List<LayerSubMesh> subs = layer.subMeshes;
            int n = subs.Count;   // (pieces the move adds come after these)
            for (int i = 0; i < n; i++)
            {
                LayerSubMesh from = subs[i];
                int v0 = i < flatVerts.Length ? flatVerts[i] : 0;
                int t0 = i < flatTris.Length ? flatTris[i] : 0;
                if (from.verts.Count <= v0 || from.material == null || from.material.renderQueue >= OverWaterQueue)
                {
                    continue;
                }
                LayerSubMesh to = layer.GetSubMesh(OverWater(from.material));
                if (to == null || to == from)
                {
                    continue;
                }
                Move(from, to, v0, t0);
            }
        }

        private static Material OverWater(Material m)
        {
            Material copy;
            if (!overWater.TryGetValue(m, out copy) || copy == null)
            {
                copy = new Material(m);
                copy.name = m.name + "_OverWater";
                copy.renderQueue = OverWaterQueue;
                overWater[m] = copy;
            }
            return copy;
        }

        // Moves the corners from v0 on (and the triangles from t0 on, which use only them) from one piece to another.
        private static void Move(LayerSubMesh from, LayerSubMesh to, int v0, int t0)
        {
            int count = from.verts.Count - v0;
            int start = to.verts.Count;
            int shift = start - v0;
            MoveRange(from.colors, to.colors, v0, count, start);
            MoveRange(from.uvs, to.uvs, v0, count, start);
            MoveRange(from.uvsChannelTwo, to.uvsChannelTwo, v0, count, start);
            MoveRange(from.normals, to.normals, v0, count, start);
            MoveRange(from.pollution, to.pollution, v0, count, start);
            MoveRange(from.verts, to.verts, v0, count, start);
            for (int k = t0; k < from.tris.Count; k++)
            {
                to.tris.Add(from.tris[k] + shift);
            }
            from.tris.RemoveRange(t0, from.tris.Count - t0);
        }

        // A list running alongside the corners: its entries for the moved corners go along (where it is filled for them).
        private static void MoveRange<T>(List<T> from, List<T> to, int v0, int count, int start)
        {
            if (from.Count < v0 + count)
            {
                return;
            }
            if (to.Count == start)
            {
                to.AddRange(from.GetRange(v0, count));
            }
            from.RemoveRange(v0, from.Count - v0);
        }

        // ---- the preview under the pointer, drawn every frame

        private static readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();
        private static Mesh unitBox;

        // def: what is being placed; center: its cell; rot: its rotation. tint: the game's
        // ghost colour (green where it can go, red where it cannot). False: nothing drawn (no 3D shape, ghosts off).
        public static bool DrawPreview(ThingDef def, IntVec3 center, Rot4 rot, Color tint, ThingDef stuff = null, ThingStyleDef style = null)
        {
            Color col = new Color(Mathf.Lerp(tint.r, 0.6f, 0.25f), Mathf.Lerp(tint.g, 0.8f, 0.25f), Mathf.Lerp(tint.b, 1f, 0.25f), 0.45f);
            return DrawGhost(def, center, rot, tint, col, stuff, style, Find.CurrentMap, null);
        }

        // ---- things to be built inside a wall (2026-10-23, ghostwall)
        // One rule for every ghost (the placement preview, blueprints, frames): where the thing's own cells hold a solid building
        // (Occupied: a wall a door replaces, a cooler or vent set into a wall line, a fixture pointed at the wall itself) its 3D
        // ghost is drawn with the x-ray ghost material (seen through, as the mining x-ray): the plain one, depth tested, stood
        // hidden inside it. Blueprints and frames the game draws every frame (doors, lit fixtures: drawerType RealtimeOnly, never
        // printed into the map mesh) whose flat sprite would lie in a wall (Occupied, or a wall fixture: the game draws its sprite
        // half into the wall) get that 3D ghost too (DrawPlanned); a blueprint's flat sprite is then left out, a frame keeps its
        // work sprite. Everything else as before.

        // While true, Draw uses the x-ray ghost material.
        internal static bool XrayNow;

        // True if any cell the def would cover at cell/rot holds a solid building (Fillage Full: a wall, a door, rock) other
        // than self and blueprints or frames.
        public static bool Occupied(ThingDef def, IntVec3 cell, Rot4 rot, Map map, Thing self)
        {
            if (def == null || map == null)
            {
                return false;
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(cell, rot, def.size))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t != self && t is Building && !(t is Blueprint) && !(t is Frame) && t.def.Fillage == FillCategory.Full)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // A blueprint or frame the game draws every frame: its 3D ghost where its flat sprite would lie in a wall (above).
        // True if drawn (a blueprint's own sprite is then left out: Patches.BlueprintDraw_Prefix).
        public static bool DrawPlanned(Thing t)
        {
            ThingDef target = Target(t);
            if (target == null || FixOff || !View3D.Ready || !IsoRimMod.Settings.ghosts3D || t.def.drawerType != DrawerType.RealtimeOnly || t.Map == null)
            {
                return false;
            }
            if (!Occupied(target, t.Position, t.Rotation, t.Map, t) && !WallMount.Is(target))
            {
                return false;
            }
            Color col = t is Frame ? FrameColor : BlueprintColor;
            IConstructible c = t as IConstructible;
            return DrawGhost(target, t.Position, t.Rotation, col, col, c != null ? c.EntityToBuildStuff() : null, t.StyleDef, t.Map, t);
        }

        // The ghost of def at center/rot in col (tint: the game's colour, for the sprite on top of a sprite-made shape), x-ray
        // where Occupied. self: the blueprint or frame drawn (null for the preview).
        private static bool DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color tint, Color col, ThingDef stuff, ThingStyleDef style, Map map, Thing self)
        {
            bool was = XrayNow;
            XrayNow = !FixOff && Occupied(def, center, rot, map, self);
            try
            {
                return DrawGhostIn(def, center, rot, tint, col, stuff, style, map);
            }
            finally
            {
                XrayNow = was;
            }
        }

        private static bool DrawGhostIn(ThingDef def, IntVec3 center, Rot4 rot, Color tint, Color col, ThingDef stuff, ThingStyleDef style, Map map)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            Material mat = R3DMaterials.Ghost;
            if (!st.ghosts3D || def == null || mat == null)
            {
                return false;
            }
            ThingForm form = Forms.For(def);
            float s = View3D.Squash;
            if (!FixOff && def.thingClass != null && typeof(Building_Door).IsAssignableFrom(def.thingClass))
            {
                // Doors are drawn every frame from their own parts (Doors): the shut door, as it will stand.
                return Doors.Ghost(def, center, rot, map, style, col);
            }
            if (!FixOff && form.form == Form3D.Extruded && form.height > 0.004f)
            {
                return DrawExtruded(def, center, rot, tint, col, stuff, form.height);
            }
            if (form.form == Form3D.Model && form.model != null && form.model.HasModel)
            {
                ModelEntry entry = form.model;
                Model m = entry.ForStyle(style) ?? entry.ForStuff(stuff) ?? entry.variants[0];
                Mesh mesh = MeshFor(m);
                if (mesh == null)
                {
                    return false;
                }
                Vector3 at = GenThing.TrueCenter(center, rot, def.size, 0f);
                float yaw = entry.Yaw(rot);
                if (entry.Swapped(def))
                {
                    yaw += 90f;
                }
                Vector3 fit = entry.Fit(def) * entry.def.scale;
                float lift = 0f;
                if (WallMount.Is(def))
                {
                    // On the wall it will hang on, where it will hang (WallMount).
                    float wall = WallMount.Wall(map, center, rot, ref at, ref yaw);
                    lift = WallMount.ModelLift(entry.def, wall, m.maxY * fit.y);
                }
                Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
                Vector3 offset = turn * Vector3.Scale(entry.def.offset, Vector3.one);
                Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(at.x + offset.x, (offset.y + lift) / s, at.z + offset.z))
                    * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                    * Matrix4x4.Rotate(turn)
                    * Matrix4x4.Scale(fit);
                Draw(mesh, matrix, col);
                if (entry.roofPart != null)
                {
                    Mesh roofMesh = MeshFor(entry.roofPart);   // (Api 31) ModelDef.roofModel, placed as the model
                    if (roofMesh != null)
                    {
                        Draw(roofMesh, matrix, col);
                    }
                }
                return true;
            }
            if (form.form == Form3D.Block)
            {
                if (unitBox == null)
                {
                    unitBox = MakeBox();
                }
                Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(center.x + 0.5f, 0f, center.z + 0.5f))
                    * Matrix4x4.Scale(new Vector3(0.96f, form.height / s, 0.96f));
                Draw(unitBox, matrix, col);
                return true;
            }
            return false;
        }

        public static bool FixOff;   // probe (ghostfix=0)

        // Harness counters (ghostspritecheck): game ghost sprites left out because the 3D ghost stands in, and drawn.
        internal static int SpritesLeftOut, SpritesDrawn;

        // GhostDrawer.DrawGhostThing's own flat sprite (its graphic.DrawFromDef call, Patches.GhostSprite_Transpiler): left out
        // where DrawPreview draws the thing in 3D, so the player sees one ghost, not the 3D one plus a flat footprint beside it
        // (any building with a model, a block or a sprite-made shape: vanilla, Neat Storage shelves, every mod's). The comps'
        // and place workers' ghosts are drawn as before; with no 3D preview (3D off, ghosts off, wide doors without their own
        // models, flat things) the game's sprite is drawn as before.
        public static void DrawGameGhostSprite(Graphic graphic, Vector3 loc, Rot4 rot, ThingDef def, float extraRotation)
        {
            if (Shows3D(def))
            {
                SpritesLeftOut++;
                return;
            }
            SpritesDrawn++;
            graphic.DrawFromDef(loc, rot, def, extraRotation);
        }

        // True when DrawPreview draws this def in 3D (the same cases, without drawing).
        public static bool Shows3D(ThingDef def)
        {
            if (def == null || FixOff || !View3D.Ready || !IsoRimMod.Settings.ghosts3D || R3DMaterials.Ghost == null)
            {
                return false;
            }
            if (def.thingClass != null && typeof(Building_Door).IsAssignableFrom(def.thingClass))
            {
                if (!IsoRimMod.Settings.doors3D)
                {
                    return false;
                }
                ModelEntry e;
                return def.size.x == 1 && def.size.z == 1
                    || typeof(Building_MultiTileDoor).IsAssignableFrom(def.thingClass) && ModelLibrary.TryGet(def, out e) && e.HasModel && e.doorLeaf != null
                    || WideSupported(def) && IsoRimMod.Settings.models3D && ModelLibrary.TryGet(def, out e) && e.HasModel;   // (Api 29)
            }
            ThingForm form = Forms.For(def);
            switch (form.form)
            {
                case Form3D.Model:
                    return form.model != null && form.model.HasModel;
                case Form3D.Block:
                    return true;
                case Form3D.Extruded:
                    return form.height > 0.004f && def.graphic != null && !(def.graphic is Graphic_Linked);
            }
            return false;
        }

        // (Api 29) A multi-cell supported door other than the game's Building_MultiTileDoor (drawn by Doors.DrawSupported).
        internal static bool WideSupported(ThingDef def)
        {
            return def?.thingClass != null && (def.size.x != 1 || def.size.z != 1) && typeof(Building_SupportedDoor).IsAssignableFrom(def.thingClass)
                && !typeof(Building_MultiTileDoor).IsAssignableFrom(def.thingClass);
        }

        // How the placement preview shows a def in 3D (probe inventory): "model", "block", or "2d:<why>".
        public static string PreviewKind(ThingDef def)
        {
            ThingForm form = Forms.For(def);
            ModelEntry e;
            bool listed = ModelLibrary.TryGet(def, out e);
            if (def.thingClass != null && typeof(Building_Door).IsAssignableFrom(def.thingClass))
            {
                bool one = def.size.x == 1 && def.size.z == 1;
                return !IsoRimMod.Settings.doors3D ? "2d:3D doors off"
                    : one || typeof(Building_MultiTileDoor).IsAssignableFrom(def.thingClass) && listed && e.HasModel && e.doorLeaf != null
                      || WideSupported(def) && listed && e.HasModel ? "door"
                    : "2d:wide door without its own models";
            }
            if (form.form == Form3D.Model && form.model != null && form.model.HasModel)
            {
                return "model";
            }
            if (form.form == Form3D.Block)
            {
                return "block";
            }
            if (form.form == Form3D.Extruded && form.height > 0.004f && def.graphic != null && !(def.graphic is Graphic_Linked))
            {
                return "extruded";
            }
            if (form.form == Form3D.Model || listed && !e.HasModel)
            {
                return "2d:model listed without a mesh";
            }
            if (listed && e.def.flat)
            {
                return "2d:model def says flat";
            }
            switch (form.form)
            {
                case Form3D.Extruded:
                    return "2d:extruded sprite (no model)";
                case Form3D.Ground:
                    return "2d:ground shape";
            }
            if (def.drawerType == DrawerType.None)
            {
                return "2d:not drawn";
            }
            if (def.drawerType == DrawerType.RealtimeOnly)
            {
                return "2d:drawn every frame, no model";
            }
            if (def.altitudeLayer <= AltitudeLayer.FloorEmplacement)
            {
                return "2d:floor level";
            }
            if (def.graphicData != null && def.graphicData.linkType != LinkDrawerType.None)
            {
                return "2d:linked graphic";
            }
            return "2d:flat (" + form.form + ")";
        }

        // A building made solid from its sprite (Form3D.Extruded: the sprite raised height cells, its sides following the
        // outline): a see-through block over its cells as high, with the game's own ghost sprite lying on its top, where
        // the sprite will be.
        private static bool DrawExtruded(ThingDef def, IntVec3 center, Rot4 rot, Color tint, Color col, ThingDef stuff, float height)
        {
            Graphic baseGraphic = def.graphic;
            if (baseGraphic == null || baseGraphic is Graphic_Linked)
            {
                return false;
            }
            if (unitBox == null)
            {
                unitBox = MakeBox();
            }
            float s = View3D.Squash;
            CellRect r = GenAdj.OccupiedRect(center, rot, def.size);
            Vector3 mid = r.CenterVector3;
            Draw(unitBox, Matrix4x4.Translate(new Vector3(mid.x, 0f, mid.z)) * Matrix4x4.Scale(new Vector3(r.Width - 0.08f, height / s, r.Height - 0.08f)), col);
            Graphic g = GhostUtility.GhostGraphicFor(baseGraphic, def, tint, stuff);
            bool was = DrawHook.active;
            Matrix4x4 wasX = DrawHook.xform;
            DrawHook.active = true;
            DrawHook.xform = View3D.LiftMatrix(height + 0.01f) * Matrix4x4.Scale(new Vector3(1f, 0f, 1f));   // flat, on the top
            try
            {
                g.DrawFromDef(GenThing.TrueCenter(center, rot, def.Size, AltitudeLayer.Blueprint.AltitudeFor()), rot, def);
            }
            finally
            {
                DrawHook.active = was;
                DrawHook.xform = wasX;
            }
            return true;
        }

        // One model as a see-through ghost, placed by matrix (drawn space): a door part (Doors.Ghost).
        internal static void DrawModel(Model m, Matrix4x4 matrix, Color col)
        {
            Mesh mesh = MeshFor(m);
            if (mesh != null && R3DMaterials.Ghost != null)
            {
                Draw(mesh, matrix, col);
            }
        }

        private static MaterialPropertyBlock block;
        private static int colorId;

        private static void Draw(Mesh mesh, Matrix4x4 matrix, Color col)
        {
            if (block == null)
            {
                block = new MaterialPropertyBlock();
                colorId = Shader.PropertyToID("_Color");
            }
            block.SetColor(colorId, col);
            Graphics.DrawMesh(mesh, matrix, XrayNow && R3DMaterials.GhostXray != null ? R3DMaterials.GhostXray : R3DMaterials.Ghost, 0, null, 0, block);
        }

        // The model as one white mesh, shaded a little by which way each face looks; the
        // colour comes from the material when it is drawn.
        private static Mesh MeshFor(Model m)
        {
            Mesh mesh;
            if (meshes.TryGetValue(m.id, out mesh))
            {
                return mesh;
            }
            List<Vector3> v = new List<Vector3>();
            List<Color32> c = new List<Color32>();
            List<int> t = new List<int>();
            for (int i = 0; i + 2 < m.positions.Length; i += 3)
            {
                float k = 0.70f + 0.30f * Mathf.Clamp01(m.normals[i].y * 0.5f + 0.5f);
                Color32 shade = new Color(k, k, k, 1f);
                for (int j = i; j < i + 3; j++)
                {
                    t.Add(v.Count);
                    Vector3 p = m.positions[j];
                    v.Add(new Vector3(p.x, Mathf.Max(0f, p.y), p.z));
                    c.Add(shade);
                }
            }
            mesh = new Mesh();
            MeshGuard.FitIndices(mesh, v.Count);
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            MeshGuard.Check(mesh, "ghost");
            meshes[m.id] = mesh;
            return mesh;
        }

        // A box one unit across and one high, standing on the ground, centred on the origin.
        private static Mesh MakeBox()
        {
            List<Vector3> v = new List<Vector3>();
            List<Color32> c = new List<Color32>();
            List<int> t = new List<int>();
            Color32 top = new Color32(255, 255, 255, 255);
            Color32 side = new Color32(200, 200, 200, 255);
            Vector3[] q =
            {
                new Vector3(-0.5f, 1f, -0.5f), new Vector3(-0.5f, 1f, 0.5f), new Vector3(0.5f, 1f, 0.5f), new Vector3(0.5f, 1f, -0.5f),
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 1f, -0.5f), new Vector3(0.5f, 1f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 1f, 0.5f), new Vector3(-0.5f, 1f, 0.5f), new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(-0.5f, 0f, 0.5f), new Vector3(-0.5f, 1f, 0.5f), new Vector3(-0.5f, 1f, -0.5f), new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, -0.5f), new Vector3(0.5f, 1f, 0.5f), new Vector3(0.5f, 0f, 0.5f),
            };
            for (int f = 0; f < 5; f++)
            {
                int b = v.Count;
                for (int k = 0; k < 4; k++)
                {
                    v.Add(q[f * 4 + k]);
                    c.Add(f == 0 ? top : side);
                }
                // Both windings, so the box shows whichever way the camera's view turns its faces.
                t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3, b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            Mesh mesh = new Mesh();
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            MeshGuard.Check(mesh, "ghost part");
            return mesh;
        }
    }
}
