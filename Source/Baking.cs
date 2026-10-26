using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The game builds one static mesh per 17x17 block of the map for everything that does not
    // move ("printing"). All of the 3D geometry is added to that same mesh, so it costs nothing
    // per frame and is rebuilt by the game whenever something in the block changes.

    // ------------------------------------------------------------------ what shape a thing takes

    public enum Form3D
    {
        Flat,       // the normal sprite, lying on the ground
        Model,      // a hand-made model from Models/
        Block,      // walls and rock: a cell-sized block with the sprite as its lid
        Extruded,   // everything else: the sprite raised up, with sides following its outline
        Ground,     // (Api 11) a standable linked thing drawn by the walls layer as a low shape on the ground (ModelDef.wallTop
                    // rule Rubble: a damaged low wall's rubble); not a wall: no wall height, never cut, rooms see through it
        Worn        // apparel lying on the ground drawn as the 3D clothes a colonist wears, laid down (GroundGear)
    }

    public class ThingForm
    {
        public Form3D form = Form3D.Flat;
        public float height;        // in cells; for items this is their thickness
        public ModelEntry model;
        internal float wallTop;     // a wall drawn from a model (WallBoxes.ModelWall): the height of its wall top, cells; else 0
        internal WallTopSpec wallTops;  // (Api 11) a Block or Ground with a shaped top (ModelDef.wallTop, WallTops.cs); else null
    }

    public static class Forms
    {
        private static readonly Dictionary<ThingDef, ThingForm> cache = new Dictionary<ThingDef, ThingForm>();
        private static readonly ThingForm FlatForm = new ThingForm();
        private static int cacheStamp = int.MinValue;

        public static ThingForm For(ThingDef def)
        {
            if (!View3D.Active || def == null)
            {
                return FlatForm;
            }
            if (cacheStamp != View3D.Stamp)
            {
                cache.Clear();
                cacheStamp = View3D.Stamp;
            }
            ThingForm f;
            if (!cache.TryGetValue(def, out f))
            {
                f = Compute(def);
                cache[def] = f;
            }
            return f;
        }

        private static ThingForm Compute(ThingDef def)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            ThingForm f = new ThingForm();

            if (def.drawerType == DrawerType.None)
            {
                return f;
            }
            ModelEntry entry;
            bool listed = ModelLibrary.TryGet(def, out entry);

            // Things the game draws every frame (drawerType RealtimeOnly) are never printed into the
            // map mesh, so only a building with a hand-made model is reshaped: its model is baked
            // by SectionLayer_IsoRimBodies, and the game keeps drawing everything else about it.
            // Items drawn every frame with the plain item drawing (books: vanilla gives them no drawerType) take the
            // same form as any other item below, and are drawn so every frame (LiveItems).
            if (def.drawerType == DrawerType.RealtimeOnly && !LiveItems.Is(def))
            {
                if (def.category != ThingCategory.Building || def.IsFrame || def.IsBlueprint || !listed || entry.def.flat
                    || entry.def.animal || !entry.HasModel || !st.models3D || typeof(Building_Door).IsAssignableFrom(def.thingClass))
                {
                    return f;
                }
                f.form = Form3D.Model;
                f.model = entry;
                f.height = entry.SurfaceHeight * entry.Fit(def).y;
                return f;
            }
            if (listed && entry.def.flat)
            {
                return f;
            }
            // (A landed drop pod is an item to the game, but a bound one is drawn as its pod model: ActiveTransporter defs.)
            bool podModel = listed && entry.HasModel && st.models3D && typeof(ActiveTransporter).IsAssignableFrom(def.thingClass);
            if (def.category == ThingCategory.Item && st.itemsFromArt && !IsChunk(def) && !OverArt(listed ? entry : null) && !podModel)
            {
                // Items are made solid from their own game sprite, so they read exactly as
                // they do in the game. (Chunks keep their rock and scrap models.)
                float thick = st.thickItems ? ItemThickness(def) * Mathf.Clamp01(st.itemThickness) : 0f;
                if (st.autoExtrude && thick > 0.004f)
                {
                    f.form = Form3D.Extruded;
                    f.height = thick;
                }
                return f;
            }
            if (def.category == ThingCategory.Item && GroundGear.Replaces(def, listed ? entry : null))
            {
                // Apparel and weapons look as a colonist wears or holds them (GroundGear): clothes as the 3D figure's clothes laid
                // down, weapons from their own sprite (as held), not a generic model for their category.
                if (GroundGear.WornLook(def))
                {
                    f.form = Form3D.Worn;
                    f.height = GroundGear.Height;
                    return f;
                }
                float thick = st.thickItems ? ItemThickness(def) * Mathf.Clamp01(st.itemThickness) : 0f;
                if (st.autoExtrude && thick > 0.004f)
                {
                    f.form = Form3D.Extruded;
                    f.height = thick;
                }
                return f;
            }
            bool plantModelsOff = false;
            if (def.category == ThingCategory.Plant)
            {
                bool tree = def.plant != null && def.plant.IsTree;
                plantModelsOff = tree ? !st.trees3D : !st.smallPlants3D;
            }
            if (listed && entry.HasModel && st.models3D && !plantModelsOff)
            {
                f.form = Form3D.Model;
                f.model = entry;
                f.height = entry.SurfaceHeight * entry.Fit(def).y;
                f.wallTop = WallBoxes.ModelWallTop(def, entry);
                return f;
            }
            float setHeight = listed ? entry.def.height : -1f;

            if (def.category == ThingCategory.Building)
            {
                // Frames, blueprints and anything that lives at floor level (spots, conduits) stay flat.
                if (def.IsFrame || def.IsBlueprint || def.altitudeLayer <= AltitudeLayer.FloorEmplacement)
                {
                    return f;
                }

                float fill = WallBoxes.Fill(def);
                WallTopSpec tops = listed ? entry.def.wallTop : null;
                if (fill > 0f)
                {
                    if (st.walls3D)
                    {
                        f.form = Form3D.Block;
                        f.height = setHeight > 0f ? setHeight : fill * View3D.WallHeight;
                        if (tops != null)
                        {
                            // (Api 11) a wall with a shaped top: its own body height, in the walls' proportion
                            f.wallTops = tops;
                            if (tops.height > 0f)
                            {
                                f.height = tops.Scaled(tops.height);
                            }
                        }
                    }
                    return f;
                }
                if (tops != null && tops.rule == WallTopRule.Rubble && st.walls3D && WallTops.GroundShaped(def))
                {
                    f.form = Form3D.Ground;   // (Api 11) rubble on the ground: drawn by the walls layer, no wall height
                    f.wallTops = tops;
                    return f;
                }

                // Other linked graphics (fences and the like) do not have one clean sprite to raise.
                if (!st.autoExtrude || def.graphicData == null || def.graphicData.linkType != LinkDrawerType.None)
                {
                    return f;
                }
                f.form = Form3D.Extruded;
                f.height = setHeight > 0f ? setHeight : Mathf.Max(def.fillPercent, 0.2f) * View3D.WallHeight;
                return f;
            }

            if (def.category == ThingCategory.Item && st.autoExtrude && st.thickItems && st.itemThickness > 0.02f)
            {
                f.form = Form3D.Extruded;
                f.height = (setHeight > 0f ? setHeight : (def.fillPercent > 0.3f ? 0.3f : 0.12f)) * Mathf.Clamp01(st.itemThickness);
            }
            return f;
        }

        // An item whose model is used even while items are made from the game's art (raw materials, with
        // the "3D raw materials" setting on).
        public static bool OverArt(ModelEntry entry)
        {
            IsoRimSettings st = IsoRimMod.Settings;
            return entry != null && entry.def.overArt && entry.HasModel && st.rawModels && st.models3D;
        }

        public static bool IsChunk(ThingDef def)
        {
            if (def.defName != null && def.defName.StartsWith("Chunk"))
            {
                return true;
            }
            if (def.thingCategories != null)
            {
                for (int i = 0; i < def.thingCategories.Count; i++)
                {
                    string c = def.thingCategories[i] != null ? def.thingCategories[i].defName : null;
                    if (c != null && c.Contains("Chunk"))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // How thick an item made from its sprite is, in cells: weapons and clothes thin, a
        // stack of goods a little more, anything bulky more again.
        public static float ItemThickness(ThingDef def)
        {
            if (def.IsWeapon)
            {
                return 0.06f;
            }
            if (def.IsApparel)
            {
                return 0.07f;
            }
            if (def.fillPercent > 0.3f)
            {
                return 0.3f;
            }
            return 0.12f;
        }

        // How far above the ground the top of this thing is drawn. 0 for anything flat.
        public static float HeightOf(Thing t)
        {
            if (t == null || t.def == null || !View3D.Active)
            {
                return 0f;
            }
            ThingForm f = For(t.def);
            float h = f.form == Form3D.Flat ? 0f : f.form == Form3D.Model ? SurfaceOf(t, f) : f.height;
            if (f.form == Form3D.Block)
            {
                h = WallBoxes.HeightFor(t, f.height);
            }
            if (t.def.category == ThingCategory.Item)
            {
                h += BaseUnder(t, true);
            }
            return h;
        }

        // The height of the raised building a thing sits on: items stored on a shelf rest on
        // top of the shelf, items on a work table or a table on its top (BenchTops), and plants in a pot
        // or a hydroponics basin grow from its rim.
        public static float BaseUnder(Thing t, bool storageOnly)
        {
            Map map = t.Map;
            if (map == null || !t.Position.InBounds(map))
            {
                return 0f;
            }
            Building b = t.Position.GetEdifice(map);
            if (b == null || b == t)
            {
                return 0f;
            }
            if (storageOnly && !(b is Building_Storage))
            {
                return BenchTops.Carries(b) ? BenchTops.TopOf(b) : 0f;
            }
            ThingForm f = For(b.def);
            return (f.form == Form3D.Flat || f.form == Form3D.Block) ? 0f : f.form == Form3D.Model ? SurfaceOf(b, f) : f.height;
        }

        // The surface of a thing drawn as a model, in cells over the ground: its kind's (form.height), or, for a thing in a
        // style whose own def (ModelDef.style) sets surfaceHeight, that one fitted to the thing (a Viking shelf's top, where
        // the items stored on it rest).
        public static float SurfaceOf(Thing t, ThingForm f)
        {
            ModelEntry own = f.form == Form3D.Model ? ModelLibrary.StyledEntry(t) : null;
            return own != null && own.def.surfaceHeight >= 0f ? own.def.surfaceHeight * own.Fit(t.def).y : f.height;
        }
    }

    // ------------------------------------------------------------------ printing state

    public enum PrintMode
    {
        None,
        Upright,    // plants: stand the sprite up facing the camera
        Lift,       // raise the sprite, nothing else
        Block,      // raise every sprite quad and add cell-sized sides
        Extrude     // raise the first sprite quad and add sides along its outline
    }

    // What should happen to the sprite quads of the thing currently being printed.
    public static class PrintContext
    {
        public static PrintMode mode;
        public static Thing thing;
        public static SectionLayer layer;
        public static float baseHeight;   // cells above the ground the thing rests on
        public static float height;       // cells above the ground of its top
        public static bool done;          // its first sprite quad has been dealt with

        public static void Clear()
        {
            mode = PrintMode.None;
            thing = null;
            layer = null;
            baseHeight = 0f;
            height = 0f;
            done = false;
        }
    }

    // Plants without a 3D model are stood up as cards facing the camera. A card is baked once,
    // flat: its four corners sit on its hinge (the middle of the sprite's bottom edge) with where
    // each corner lay in the flat sprite, relative to the hinge, in texture coordinate 1, drawn with
    // a copy of the plant's material on the IsoRimCardPlant (or, for snow, IsoRimCardSnow) def,
    // whose vertex stage stands it up from the camera's angle (Shaders\IsoRimCard.vert.glsl). So a
    // tilt or a turn rebuilds nothing.
    public static class UprightCards
    {
        public const string PlantDef = "IsoRimCardPlant";
        public const string SnowDef = "IsoRimCardSnow";

        private static readonly Dictionary<Material, Material> cards = new Dictionary<Material, Material>();
        private static readonly int isoCardId = Shader.PropertyToID("_IsoCard");

        // The card material for a plant's material: a copy (texture, colour, autumn colours) on the
        // card shader, swaying with the game's wind like the plant's own. Null if the def is missing.
        public static Material For(Material mat)
        {
            Material card;
            if (mat == null)
            {
                return null;
            }
            if (cards.TryGetValue(mat, out card))
            {
                return card;
            }
            card = null;
            // The game draws snow on plants with TransparentPlant (no depth write); plants with
            // CutoutPlant, the card default.
            bool snow = mat.shader != null && mat.shader == ShaderDatabase.TransparentPlant;
            Shader s = R3DMaterials.ShaderOf(snow ? SnowDef : PlantDef);
            if (s != null)
            {
                card = new Material(s);
                card.CopyPropertiesFromMaterial(mat);
                card.renderQueue = mat.renderQueue;
                card.name = mat.name + "_Card";
                WindManager.Notify_PlantMaterialCreated(card);
                if (!snow)
                {
                    IsoGlow.UseCard(card);   // lit from its own cell, not by the pulled overlay
                }
            }
            cards[mat] = card;
            return card;
        }

        // A vanilla plant sprite covers its cell seen from above; stood up it is as tall as it was long, which for
        // a bush or brambles is taller than a wall. Plants that are not trees can be drawn smaller (the whole card,
        // from its foot, so it keeps its shape).
        private static float SizeOf(Thing t)
        {
            Plant plant = t as Plant;
            if (plant == null || plant.def.plant == null || plant.def.plant.IsTree)
            {
                return 1f;
            }
            return Mathf.Clamp(IsoRimMod.Settings.plantCardSize, 0.3f, 1.5f);
        }

        // How far a card is let down into the ground so that the lowest painted pixel of its sprite stands on it: most
        // plant sprites leave empty rows under the plant, and the card stood on its bottom edge then floats. In the
        // sprite's own units (its length along z); 0 if the pixels cannot be read. Cached per texture and part shown.
        private const int SinkSamples = 32;
        private const float MaxSink = 0.6f;   // of the sprite's height: a sprite that is nearly all margin is left as it is
        private static readonly Dictionary<long, float> sinks = new Dictionary<long, float>();

        private static float Sink(Material mat, LayerSubMesh sm, int n)
        {
            Texture tex = mat != null ? mat.mainTexture : null;
            if (tex == null || n < 4)
            {
                return 0f;
            }
            float zMin = float.MaxValue, zMax = float.MinValue, vBottom = 0f;
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            for (int i = n - 4; i < n; i++)
            {
                Vector3 v = sm.verts[i];
                Vector3 uv = sm.uvs[i];
                if (v.z < zMin)
                {
                    zMin = v.z;
                    vBottom = uv.y;
                }
                zMax = Mathf.Max(zMax, v.z);
                uMin = Mathf.Min(uMin, uv.x);
                uMax = Mathf.Max(uMax, uv.x);
                vMin = Mathf.Min(vMin, uv.y);
                vMax = Mathf.Max(vMax, uv.y);
            }
            float height = zMax - zMin;
            if (height <= 0f || uMax <= uMin || vMax <= vMin)
            {
                return 0f;
            }
            bool flipped = Mathf.Abs(vBottom - vMax) < Mathf.Abs(vBottom - vMin);   // the sprite's foot is at the top of its part
            long key;
            unchecked
            {
                key = (long)tex.GetHashCode() * 31L + Mathf.RoundToInt(uMin * 4096f);
                key = key * 31L + Mathf.RoundToInt(vMin * 4096f);
                key = key * 31L + Mathf.RoundToInt(uMax * 4096f);
                key = key * 31L + Mathf.RoundToInt(vMax * 4096f);
                key = key * 2L + (flipped ? 1 : 0);
            }
            float fraction;
            if (!sinks.TryGetValue(key, out fraction))
            {
                fraction = EmptyBelow(tex, new Vector2(uMax - uMin, vMax - vMin), new Vector2(uMin, vMin), flipped);
                sinks[key] = fraction;
            }
            return fraction * height;
        }

        // The share of the sprite's rows under its lowest painted pixel (rows read bottom first; from the top if flipped).
        private static float EmptyBelow(Texture tex, Vector2 scale, Vector2 offset, bool flipped)
        {
            Color32[] px = SpritePixels.Sample(tex, scale, offset, SinkSamples);
            if (px == null || px.Length < SinkSamples * SinkSamples)
            {
                return 0f;
            }
            const byte Painted = 90;   // as the card's cut-out (alpha 0.35)
            for (int k = 0; k < SinkSamples; k++)
            {
                int row = flipped ? SinkSamples - 1 - k : k;
                for (int x = 0; x < SinkSamples; x++)
                {
                    if (px[row * SinkSamples + x].a >= Painted)
                    {
                        return Mathf.Min(k / (float)SinkSamples, MaxSink);
                    }
                }
            }
            return 0f;
        }

        // The camera's angle for the card shader (global _IsoCard), every frame while the 3D view is on.
        public static void SetCamera(float pitch, float yaw)
        {
            float a = (pitch - 90f) * Mathf.Deg2Rad;
            float y = yaw * Mathf.Deg2Rad;
            Shader.SetGlobalVector(isoCardId, new Vector4(Mathf.Sin(a), Mathf.Cos(a), Mathf.Sin(y), Mathf.Cos(y)));
        }

        // Moves the sprite quad just added to sm (its last four corners) into the layer's card piece
        // for the same material, hinged at pivot and raised by lift (cells).
        public static bool Move(SectionLayer layer, LayerSubMesh sm, Material mat, Vector3 pivot, float lift)
        {
            Material card = For(mat);
            if (card == null)
            {
                return false;
            }
            LayerSubMesh cs = MeshWriter.Sub(layer, card);
            if (cs == null || cs == sm)
            {
                return false;
            }
            int n = sm.verts.Count;
            Vector3 hinge = new Vector3(pivot.x, pivot.y + lift / View3D.Squash, pivot.z);
            float sink = Sink(mat, sm, n);
            float size = SizeOf(PrintContext.thing);
            int start = cs.verts.Count;
            for (int i = n - 4; i < n; i++)
            {
                Vector3 v = sm.verts[i];
                cs.verts.Add(hinge);
                cs.uvsChannelTwo.Add(new Vector3((v.x - pivot.x) * size, v.y - pivot.y, (v.z - pivot.z - sink) * size));
                cs.uvs.Add(sm.uvs[i]);
                cs.colors.Add(sm.colors[i]);
            }
            // The game's plane is two triangles, (0 1 2) and (0 2 3).
            cs.tris.Add(start);
            cs.tris.Add(start + 1);
            cs.tris.Add(start + 2);
            cs.tris.Add(start);
            cs.tris.Add(start + 2);
            cs.tris.Add(start + 3);
            sm.verts.RemoveRange(n - 4, 4);
            sm.uvs.RemoveRange(n - 4, 4);
            sm.colors.RemoveRange(n - 4, 4);
            sm.tris.RemoveRange(sm.tris.Count - 6, 6);
            return true;
        }
    }
    public static class PlaneHandler
    {
        // Called right after the game adds one flat sprite quad (the last four vertices of the
        // sub-mesh for that material). rot is the quad's rotation in degrees.
        public static void Handle(SectionLayer layer, Vector3 center, Vector2 size, Material mat, float rot)
        {
            LayerSubMesh sm = MeshWriter.Sub(layer, mat);
            if (sm == null)
            {
                return;
            }
            int n = sm.verts.Count;
            if (n < 4)
            {
                return;
            }

            switch (PrintContext.mode)
            {
                case PrintMode.Upright:
                    if (rot == 0f)
                    {
                        // Hinge along the bottom edge of the sprite; stood up by the card shader.
                        Vector3 pivot = new Vector3(center.x, center.y, center.z - size.y * 0.5f);
                        if (UprightCards.Move(layer, sm, mat, pivot, PrintContext.baseHeight))
                        {
                            break;
                        }
                    }
                    if (PrintContext.baseHeight > 0f)
                    {
                        MeshWriter.Raise(sm, n, PrintContext.baseHeight);
                    }
                    break;

                case PrintMode.Lift:
                    MeshWriter.Raise(sm, n, PrintContext.height);
                    break;

                case PrintMode.Block:
                    MeshWriter.Raise(sm, n, PrintContext.height);
                    if (!PrintContext.done)
                    {
                        PrintContext.done = true;
                        WallBoxes.PrintPlainSides(layer, PrintContext.thing, PrintContext.height);
                    }
                    break;

                case PrintMode.Extrude:
                    if (!PrintContext.done)
                    {
                        PrintContext.done = true;
                        bool extruded = Extruder.Extrude(layer, sm, n, mat, PrintContext.baseHeight, PrintContext.height);
                        if (!extruded && PrintContext.baseHeight > 0f)
                        {
                            MeshWriter.Raise(sm, n, PrintContext.baseHeight);
                        }
                    }
                    break;
            }
        }
    }

    public static class MeshWriter
    {
        // The most corners one piece of a block's mesh may hold. The game's own pieces stop at
        // 65,535; the ones this mod writes to are switched to the larger index size first (see
        // Sub), so that a block of thick forest still fits.
        public const int MaxVerts = 260000;

        // The piece of a block's mesh drawn with the given material, ready to hold many corners.
        public static LayerSubMesh Sub(SectionLayer layer, Material mat)
        {
            LayerSubMesh sm = layer.GetSubMesh(mat);
            if (sm != null && sm.mesh != null && sm.mesh.indexFormat != UnityEngine.Rendering.IndexFormat.UInt32)
            {
                sm.mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }
            return sm;
        }

        // Raises the last four vertices (one sprite quad) by a height in cells.
        public static void Raise(LayerSubMesh sm, int n, float height)
        {
            float dy = height / View3D.Squash;
            for (int i = n - 4; i < n; i++)
            {
                Vector3 v = sm.verts[i];
                v.y += dy;
                sm.verts[i] = v;
            }
        }

        public static void Quad(LayerSubMesh sm, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 color)
        {
            Quad(sm, a, b, c, d, color, color, color, color);
        }

        public static void Quad(LayerSubMesh sm, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 ca, Color32 cb, Color32 cc, Color32 cd)
        {
            int start = sm.verts.Count;
            sm.verts.Add(a);
            sm.verts.Add(b);
            sm.verts.Add(c);
            sm.verts.Add(d);
            sm.colors.Add(ca);
            sm.colors.Add(cb);
            sm.colors.Add(cc);
            sm.colors.Add(cd);
            for (int i = 0; i < 4; i++)
            {
                sm.uvs.Add(default);
            }
            AddQuadTris(sm, start);
        }

        public static void TexturedQuad(LayerSubMesh sm, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Color32 color)
        {
            int start = sm.verts.Count;
            sm.verts.Add(a);
            sm.verts.Add(b);
            sm.verts.Add(c);
            sm.verts.Add(d);
            sm.uvs.Add(new Vector3(ua.x, ua.y, 0f));
            sm.uvs.Add(new Vector3(ub.x, ub.y, 0f));
            sm.uvs.Add(new Vector3(uc.x, uc.y, 0f));
            sm.uvs.Add(new Vector3(ud.x, ud.y, 0f));
            for (int i = 0; i < 4; i++)
            {
                sm.colors.Add(color);
            }
            AddQuadTris(sm, start);
        }

        public static void AddQuadTris(LayerSubMesh sm, int start)
        {
            sm.tris.Add(start);
            sm.tris.Add(start + 1);
            sm.tris.Add(start + 2);
            sm.tris.Add(start);
            sm.tris.Add(start + 2);
            sm.tris.Add(start + 3);
        }
    }

    // ------------------------------------------------------------------ walls and rock

    public enum WallKind
    {
        Bricks,
        Planks,
        Metal,
        Rock,
        RockOre,
        SmoothStone,
        Sandbags
    }

    // Walls, natural rock, sandbags and barricades are drawn as proper blocks with the mod's own
    // textures: a lighter cap on top, the material's pattern on each exposed side, and dark
    // outlines round the cap, along the ground and up the ends of each run. The textures are
    // light grey and are tinted with the colour of what the wall is built from.
    public static partial class WallBoxes
    {
        private static readonly Dictionary<ThingDef, float> fillCache = new Dictionary<ThingDef, float>();
        private static readonly Dictionary<long, Material> materials = new Dictionary<long, Material>();

        // Texture file names in Textures/IsoRim/Walls, in WallKind order ("Side" or "Top" is added).
        private static readonly string[] KindNames = { "Bricks", "Planks", "Metal", "Rock", "RockOre", "SmoothStone", "Sandbags" };

        // Faces in the order south, north, west, east: the cell each one looks out onto, the
        // direction that is "right" when standing outside looking at it, and how dark it is.
        private static readonly IntVec3[] FaceOut = { IntVec3.South, IntVec3.North, IntVec3.West, IntVec3.East };
        private static readonly IntVec3[] FaceRight = { IntVec3.East, IntVec3.West, IntVec3.South, IntVec3.North };

        private const float TopShade = 1.12f;        // the cap catches the light
        private const float LineShade = 0.32f;       // outline colour, as a fraction of the wall's colour
        private const float OutlineWidth = 0.05f;    // in cells
        private const float Gap = 0.004f;            // how far an outline sits in front of its face
        private const float SideCellsHigh = 1.25f;   // the side textures cover 2 cells across by 1.25 up
        private const float SlopeLight = 0.14f;      // how much lighter the sloping upper part of a rock face is

        // The four corners of a cell in the order south-west, north-west, north-east, south-east,
        // and for each face (south, north, west, east) its left and right corner seen from outside.
        private static readonly int[] CornerX = { 0, 0, 1, 1 };
        private static readonly int[] CornerZ = { 0, 1, 1, 0 };
        private static readonly int[] FaceLeft = { 0, 2, 1, 3 };
        private static readonly int[] FaceRightCorner = { 3, 1, 0, 2 };
        private static readonly Vector3[] rockGround = new Vector3[4];
        private static readonly Vector3[] rockMid = new Vector3[4];
        private static readonly Vector3[] rockTop = new Vector3[4];
        private static readonly Vector3[] markTop = new Vector3[4];   // a marked block's top and which edges have a dark rim (MineMarks.PrintOutline)
        private static readonly bool[] markRim = new bool[4];
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        // The piece of mesh for plain wall faces looking out of side f, lit by the sun as it
        // moves (see R3DMaterials.Lit); the given piece if there is no room.
        private static LayerSubMesh LitSide(SectionLayer layer, int f, LayerSubMesh fallback)
        {
            Material m = R3DMaterials.Lit(R3DMaterials.BinOf(FaceOut[f].ToVector3()), 1);
            LayerSubMesh sm = m != null ? MeshWriter.Sub(layer, m) : null;
            return sm != null && sm.verts.Count + 8 <= MeshWriter.MaxVerts ? sm : fallback;
        }

        // Fraction of full wall height this thing should be drawn at, or 0 if it is not a block.
        public static float Fill(ThingDef def)
        {
            float fill;
            if (fillCache.TryGetValue(def, out fill))
            {
                return fill;
            }

            fill = 0f;
            if (def.category == ThingCategory.Building && def.graphicData != null && def.size.x == 1 && def.size.z == 1)
            {
                LinkDrawerType link = def.graphicData.linkType;
                if ((link == LinkDrawerType.Basic || link == LinkDrawerType.CornerFiller) && def.fillPercent >= 0.2f)
                {
                    fill = Mathf.Clamp01(def.fillPercent);
                }
            }
            fillCache[def] = fill;
            return fill;
        }

        // A stuffless block the game draws in plain white (void metal wall, Odyssey burrow wall) takes the colours of its own
        // atlas instead of coming out white: the sides the mean of the atlas' solid pixels without its dark outlines, the
        // cap halfway from that to its lightest fifth (the void metal's grey shards, the burrow wall's pale stones).
        // Read once per def. Anything with stuff, a def colour or a natural rock look keeps its colour.
        private static readonly Dictionary<ThingDef, Color[]> tones = new Dictionary<ThingDef, Color[]>();

        internal static void BlockTones(Thing t, ref Color side, ref Color cap)
        {
            ThingDef def = t.def;
            if (t.Stuff != null || side.r < 0.98f || side.g < 0.98f || side.b < 0.98f || def.IsSmoothed
                || (def.building != null && def.building.isNaturalRock))
            {
                return;
            }
            Color[] c;
            if (!tones.TryGetValue(def, out c))
            {
                c = ReadTones(t);
                tones[def] = c;
            }
            if (c != null)
            {
                side = c[0];
                cap = c[1];
            }
        }

        private static Color[] ReadTones(Thing t)
        {
            try
            {
                Graphic g = t.Graphic;
                Material mat = g != null ? g.MatSingleFor(t) : null;
                Texture tex = mat != null && mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                if (tex == null)
                {
                    return null;
                }
                Color32[] px = SpritePixels.Sample(tex, Vector2.one, Vector2.zero, 32);
                if (px == null && !SpritePixels.IsSheet(tex))
                {
                    px = SpritePixels.GpuSample(tex, 32);
                }
                if (px == null)
                {
                    return null;
                }
                List<Color32> solid = new List<Color32>();
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a >= 128 && px[i].r * 0.3f + px[i].g * 0.59f + px[i].b * 0.11f >= 15f)
                    {
                        solid.Add(px[i]);
                    }
                }
                if (solid.Count == 0)
                {
                    return null;
                }
                solid.Sort((a, b) => (a.r * 30 + a.g * 59 + a.b * 11).CompareTo(b.r * 30 + b.g * 59 + b.b * 11));
                Color mean = Mean(solid, 0, solid.Count);
                Color light = Mean(solid, solid.Count * 4 / 5, solid.Count);
                Color tint = mat.HasProperty("_Color") ? mat.color : Color.white;
                Color side = mean * tint;
                Color cap = Color.Lerp(mean, light, 0.5f) * tint;
                side.a = 1f;
                cap.a = 1f;
                return new[] { side, cap };
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a wall atlas' colours failed: " + e, 93611270);
                return null;
            }
        }

        private static Color Mean(List<Color32> px, int from, int to)
        {
            float r = 0f, g = 0f, b = 0f;
            int n = Mathf.Max(1, to - from);
            for (int i = from; i < to; i++)
            {
                r += px[i].r;
                g += px[i].g;
                b += px[i].b;
            }
            return new Color(r / n / 255f, g / n / 255f, b / n / 255f, 1f);
        }

        // Which set of textures a block uses.
        public static WallKind KindOf(Thing t)
        {
            ThingDef def = t.def;
            if (def.building != null && def.building.isNaturalRock)
            {
                return def.building.isResourceRock ? WallKind.RockOre : WallKind.Rock;
            }
            if (def.IsSmoothed)
            {
                return WallKind.SmoothStone;
            }
            if (def.defName == "Sandbags")
            {
                return WallKind.Sandbags;
            }
            switch (R3DTextures.StuffSwatch(t))
            {
                case "Stone":
                    return WallKind.Bricks;
                case "Metal":
                    return WallKind.Metal;
                case "Fabric":
                    return WallKind.Sandbags;
                default:
                    return t.Stuff != null ? WallKind.Planks : WallKind.Metal;
            }
        }

        // The cap (top) or a side, at a given brightness. sloped marks the upper, leaning part of a
        // rock face, which catches more light. Shading is part of the material colour, so it does
        // not depend on how the game's shader treats vertex colours.
        // Sides are lit by the sun through LitTexture, so they brighten and darken smoothly as it
        // moves; the cap keeps a steady brightness.
        private static Material MaterialFor(WallKind kind, bool top, int face, bool sloped, Color tint)
        {
            if (!top)
            {
                float lift = sloped ? 1f + SlopeLight * 1.4f : 1f;
                string sidePath = "IsoRim/Walls/" + KindNames[(int)kind] + "Side";
                return R3DMaterials.LitTexture(sidePath, new Color(tint.r * lift, tint.g * lift, tint.b * lift, 1f),
                    R3DMaterials.BinOf(FaceOut[face].ToVector3()), 1);
            }
            float shade = TopShade;
            Color32 c = tint;
            int level = Mathf.Clamp(Mathf.RoundToInt(shade * 100f), 0, 255);
            long key = ((long)(((int)kind * 2 + (top ? 1 : 0)) * 256 + level) << 32) | (uint)((c.r << 16) | (c.g << 8) | c.b);
            Material m;
            if (materials.TryGetValue(key, out m))
            {
                return m;
            }
            string path = "IsoRim/Walls/" + KindNames[(int)kind] + (top ? "Top" : "Side");
            m = R3DTextures.MaterialFor(path, new Color(tint.r * shade, tint.g * shade, tint.b * shade, 1f));
            materials[key] = m;
            return m;
        }

        // Draws one block entirely from the mod's own textures. Returns false, having drawn
        // nothing, if the textures are missing, so the caller can fall back to the game's sprite.
        public static bool PrintBlock(SectionLayer layer, Thing t, float height)
        {
            WallKind kind = KindOf(t);
            if ((kind == WallKind.Rock || kind == WallKind.RockOre) && IsoRimMod.Settings.ruggedRock)
            {
                return PrintRock(layer, t, height, kind);
            }
            Color tint = t.DrawColor;
            Color capTint = tint;
            BlockTones(t, ref tint, ref capTint);
            tint = MineMarks.Tint(t, tint);   // marked for mining: toward the mark's colour
            capTint = MineMarks.Tint(t, capTint);
            WallTopSpec spec = Forms.For(t.def).wallTops;   // (Api 11) a wall with a shaped top: plain cap, its own side texture
            Material topMat = spec != null ? R3DMaterials.Solid : MaterialFor(kind, true, 0, false, capTint);
            if (topMat == null)
            {
                return false;
            }
            LayerSubMesh top = MeshWriter.Sub(layer, topMat);
            if (top == null || top.verts.Count + 4 > MeshWriter.MaxVerts)
            {
                return false;
            }
            Material solidMat = R3DMaterials.Solid;
            LayerSubMesh solid = solidMat != null ? MeshWriter.Sub(layer, solidMat) : null;
            bool room = solid != null && solid.verts.Count + 140 <= MeshWriter.MaxVerts;

            Map map = t.Map;
            IntVec3 c = t.Position;
            float squash = View3D.Squash;
            float x0 = c.x;
            float z0 = c.z;
            float x1 = x0 + 1f;
            float z1 = z0 + 1f;
            // Stacked floors (Floors, design section 5): a wall standing on a wall on the floor below is one wall through both
            // storeys: its sides reach down to the top of the one below, with no line along the bottom, and its texture
            // runs on from it (along the ground row, up from the ground).
            float drop, floorH;
            int zShift;
            Floors.WallColumn(map, c, out drop, out floorH, out zShift);
            float baseY = -drop / squash;
            float tileU = spec != null ? spec.tile.x : 2f;             // the side texture covers tileU cells across by tileV up
            float tileV = spec != null ? spec.tile.y : SideCellsHigh;
            float vBase = (floorH - drop) / tileV;
            float topY = height / squash;
            float rimY = topY + 0.002f / squash;
            Color32 lineColor = R3DMaterials.Shaded(tint, LineShade);
            bool tags = CutTags.Enabled;
            int own = tags && CanBeCut(t.def) ? ShaderCut.Lower : 0;
            int whole = CutTags.Mark(layer);   // (shader cut-away) everything of this block goes down with its cell

            // The cap. Its texture repeats every two cells, so neighbouring blocks continue it.
            if (spec != null)
            {
                // (Api 11) a plain cap in the stone's colour (WallTopSpec.topShade)
                MeshWriter.Quad(top, new Vector3(x0, topY, z0), new Vector3(x0, topY, z1), new Vector3(x1, topY, z1), new Vector3(x1, topY, z0),
                    R3DMaterials.Shaded(capTint, spec.topShade));
            }
            else
            {
                MeshWriter.TexturedQuad(top,
                    new Vector3(x0, topY, z0), new Vector3(x0, topY, z1), new Vector3(x1, topY, z1), new Vector3(x1, topY, z0),
                    new Vector2(x0 * 0.5f, z0 * 0.5f), new Vector2(x0 * 0.5f, z1 * 0.5f), new Vector2(x1 * 0.5f, z1 * 0.5f), new Vector2(x1 * 0.5f, z0 * 0.5f),
                    White);
            }
            if (room)
            {
                // A plain lid just under it, fully solid, so whatever stands behind stays hidden.
                float lidY = topY - 0.01f / squash;
                MeshWriter.Quad(solid, new Vector3(x0, lidY, z0), new Vector3(x0, lidY, z1), new Vector3(x1, lidY, z1), new Vector3(x1, lidY, z0),
                    R3DMaterials.Shaded(tint, 0.9f));
            }

            Vector3 centre = new Vector3(x0 + 0.5f, baseY, z0 + 0.5f);
            for (int f = 0; f < 4; f++)
            {
                markRim[f] = false;
            }
            for (int f = 0; f < 4; f++)
            {
                int cond = 0;
                if (!ExposedFrom(map, c, c + FaceOut[f], height))
                {
                    // (shader cut-away) against a block that may be cut down: baked, shown only while it is lower.
                    if (!tags || !MayDrop(map, c + FaceOut[f]) || HullCorners.Covers(map, c + FaceOut[f], c, height))
                    {
                        continue;
                    }
                    cond = ShaderCut.Cond(FaceOut[f]);
                }
                int faceMark = CutTags.Mark(layer);

                Vector3 outward = FaceOut[f].ToVector3();
                Vector3 right = FaceRight[f].ToVector3();
                Vector3 p0 = centre + outward * 0.5f - right * 0.5f;   // bottom left corner, seen from outside
                Vector3 p1 = p0 + right;

                Material sideMat = spec != null ? WallTops.SideMaterial(spec, tint) : MaterialFor(kind, false, f, false, tint);
                LayerSubMesh side = sideMat != null ? MeshWriter.Sub(layer, sideMat) : null;
                bool textured = side != null && side.verts.Count + 4 <= MeshWriter.MaxVerts;
                if (textured)
                {
                    // Texture position follows the map, not the block, so a run of wall reads as one surface.
                    float along = Vector3.Dot(p0 - new Vector3(0f, 0f, zShift), right);
                    float u0 = along / tileU;
                    float u1 = (along + 1f) / tileU;
                    float v0 = vBase;
                    float v1 = vBase + (height + drop) / tileV;
                    int sideMark = CutTags.Mark(layer);
                    MeshWriter.TexturedQuad(side, p0, Raised(p0, topY), Raised(p1, topY), p1,
                        new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0), White);
                    CutTags.Apply(layer, sideMark, c, own | cond | ShaderCut.TexSide);
                }
                if (!room)
                {
                    CutTags.Apply(layer, faceMark, c, own | cond);
                    continue;
                }

                // A plain face tucked just behind the textured one (or on its own without it).
                Vector3 b0 = textured ? p0 - outward * Gap : p0;
                Vector3 b1 = b0 + right;
                MeshWriter.Quad(LitSide(layer, f, solid), b0, Raised(b0, topY), Raised(b1, topY), b1, R3DMaterials.Shaded(tint, 0.9f));

                // Dark outlines, in the game's art style: a rim on the cap along this edge, a
                // line along the ground, and a line up each end of a run of wall.
                Vector3 inner = outward * -OutlineWidth;
                MeshWriter.Quad(solid, Raised(p0, rimY), Raised(p0 + inner, rimY), Raised(p1 + inner, rimY), Raised(p1, rimY), lineColor);
                markRim[f] = true;

                Vector3 q0 = p0 + outward * Gap;
                Vector3 q1 = q0 + right;
                float lineTop = OutlineWidth * 0.7f / squash;
                if (drop <= 0f)
                {
                    MeshWriter.Quad(solid, q0, Raised(q0, lineTop), Raised(q1, lineTop), q1, lineColor);
                }
                EndLines(solid, map, c, f, q0, right, topY, height, lineColor, layer, cond == 0 ? own : -1);
                CutTags.Apply(layer, faceMark, c, own | cond);
            }

            // Where a wall turns a corner inward, the rim needs a small square to stay unbroken.
            if (room)
            {
                for (int ax = -1; ax <= 1; ax += 2)
                {
                    for (int az = -1; az <= 1; az += 2)
                    {
                        if (ExposedFrom(map, c, c + new IntVec3(ax, 0, 0), height) || ExposedFrom(map, c, c + new IntVec3(0, 0, az), height)
                            || !ExposedFrom(map, c, c + new IntVec3(ax, 0, az), height))
                        {
                            continue;
                        }
                        float cx = ax > 0 ? x1 - OutlineWidth : x0;
                        float cz = az > 0 ? z1 - OutlineWidth : z0;
                        MeshWriter.Quad(solid, new Vector3(cx, rimY, cz), new Vector3(cx, rimY, cz + OutlineWidth),
                            new Vector3(cx + OutlineWidth, rimY, cz + OutlineWidth), new Vector3(cx + OutlineWidth, rimY, cz), lineColor);
                    }
                }
                // marked for mining: the faint line along the top edges of the marked group
                for (int k = 0; k < 4; k++)
                {
                    markTop[k] = new Vector3(x0 + CornerX[k], height, z0 + CornerZ[k]);
                }
                MineMarks.PrintOutline(solid, t, markTop, markRim, OutlineWidth, capTint * TopShade, squash);
                if (spec != null)
                {
                    // (Api 11) what stands on the cap: merlons, a ridge, broken stubs (WallTops.cs)
                    PrintCrown(layer, t, spec, height, tint, capTint, lineColor, own, floorH, zShift);
                }
            }
            CutTags.Apply(layer, whole, c, own);
            return true;
        }

        // Natural rock. Instead of a square block, every exposed edge is broken up: the face
        // leans back, kinks part of the way up and meets a lowered, uneven rim. How far each
        // corner moves depends only on where that corner is on the map and which of the four
        // cells round it are rock, so neighbouring cells always agree and the cliff has no gaps.
        // Corners in the middle of a rock mass stay put, keeping the top level with the fog.
        private static bool PrintRock(SectionLayer layer, Thing t, float height, WallKind kind)
        {
            if (CliffMode > 0)
            {
                return PrintRockSmooth(layer, t, height, kind);   // PROTOTYPE (CliffLook.cs)
            }
            Color tint = MineMarks.Tint(t, t.DrawColor);   // marked for mining: toward the mark's colour
            Material capMat = kind == WallKind.Rock ? RockCapMaterial(t) : null;
            Material topMat = capMat ?? MaterialFor(kind, true, 0, false, tint);
            if (topMat == null)
            {
                return false;
            }
            LayerSubMesh top = MeshWriter.Sub(layer, topMat);
            if (top == null || top.verts.Count + 4 > MeshWriter.MaxVerts)
            {
                return false;
            }
            Material solidMat = R3DMaterials.Solid;
            LayerSubMesh solid = solidMat != null ? MeshWriter.Sub(layer, solidMat) : null;
            bool room = solid != null && solid.verts.Count + 120 <= MeshWriter.MaxVerts;
            int capRows = capMat != null ? CapRows(t.Map, t.Position) : 0;

            Map map = t.Map;
            IntVec3 c = t.Position;
            float squash = View3D.Squash;
            Color32 lineColor = R3DMaterials.Shaded(tint, LineShade);
            bool tags = CutTags.Enabled;
            int own = tags && CanBeCut(t.def) ? ShaderCut.Lower : 0;
            int whole = CutTags.Mark(layer);

            for (int k = 0; k < 4; k++)
            {
                int cx = c.x + CornerX[k];
                int cz = c.z + CornerZ[k];
                rockGround[k] = new Vector3(cx, 0f, cz);
                RockCorner(map, cx, cz, height, out rockMid[k], out rockTop[k]);
            }

            // (under a floor: drawn only while that floor is hidden, CliffLook.CoveredByFloor)
            bool coveredCap = CoveredByFloor(map, c);
            if (coveredCap)
            {
                LayerSubMesh ct = MeshWriter.Sub(layer, CoveredTwin(topMat));
                if (ct != null && ct.verts.Count + 4 <= MeshWriter.MaxVerts)
                {
                    top = ct;
                }
            }
            MeshWriter.TexturedQuad(top,
                ToWorld(rockTop[0], squash), ToWorld(rockTop[1], squash), ToWorld(rockTop[2], squash), ToWorld(rockTop[3], squash),
                CapUV(rockTop[0], capMat, capRows, c), CapUV(rockTop[1], capMat, capRows, c), CapUV(rockTop[2], capMat, capRows, c), CapUV(rockTop[3], capMat, capRows, c), White);
            if (room)
            {
                Vector3 down = new Vector3(0f, -0.01f, 0f);
                MeshWriter.Quad(solid, ToWorld(rockTop[0] + down, squash), ToWorld(rockTop[1] + down, squash),
                    ToWorld(rockTop[2] + down, squash), ToWorld(rockTop[3] + down, squash), R3DMaterials.Shaded(tint, 0.9f));
            }

            for (int f = 0; f < 4; f++)
            {
                markRim[f] = false;
            }
            for (int f = 0; f < 4; f++)
            {
                int cond = 0;
                if (!Exposed(map, c + FaceOut[f], height))
                {
                    if (!tags || !MayDrop(map, c + FaceOut[f]))
                    {
                        continue;
                    }
                    cond = ShaderCut.Cond(FaceOut[f]);   // (shader cut-away) shown only while that cell is lower
                }
                int faceMark = CutTags.Mark(layer);
                Vector3 outward = FaceOut[f].ToVector3();
                Vector3 right = FaceRight[f].ToVector3();
                int l = FaceLeft[f];
                int r = FaceRightCorner[f];
                Vector3 back = outward * -Gap;

                // Lower part of the face, then the part that slopes back to the rim.
                for (int part = 0; part < 2; part++)
                {
                    Vector3 a = part == 0 ? rockGround[l] : rockMid[l];
                    Vector3 b = part == 0 ? rockMid[l] : rockTop[l];
                    Vector3 d = part == 0 ? rockGround[r] : rockMid[r];
                    Vector3 e = part == 0 ? rockMid[r] : rockTop[r];
                    Material sideMat = MaterialFor(kind, false, f, part == 1, tint);
                    LayerSubMesh side = sideMat != null ? MeshWriter.Sub(layer, sideMat) : null;
                    bool textured = side != null && side.verts.Count + 4 <= MeshWriter.MaxVerts;
                    if (textured)
                    {
                        int sideMark = CutTags.Mark(layer);
                        MeshWriter.TexturedQuad(side, ToWorld(a, squash), ToWorld(b, squash), ToWorld(e, squash), ToWorld(d, squash),
                            SideUV(a, right), SideUV(b, right), SideUV(e, right), SideUV(d, right), White);
                        CutTags.Apply(layer, sideMark, c, own | cond | ShaderCut.TexSide);
                    }
                    if (room)
                    {
                        Vector3 shift = textured ? back : Vector3.zero;
                        float shade = (part == 0 ? 1f : 1f + SlopeLight * 1.4f) * 0.9f;
                        MeshWriter.Quad(LitSide(layer, f, solid), ToWorld(a + shift, squash), ToWorld(b + shift, squash), ToWorld(e + shift, squash), ToWorld(d + shift, squash),
                            R3DMaterials.Shaded(tint, shade));
                    }
                }
                if (!room)
                {
                    CutTags.Apply(layer, faceMark, c, own | cond);
                    continue;
                }

                // Dark outlines: along the rim (not where the surface goes on at that height, CliffLook.SurfaceGoesOn) and
                // along the ground.
                Vector3 lift = new Vector3(0f, 0.002f, 0f);
                Vector3 inner = outward * -OutlineWidth;
                if (!SurfaceGoesOn(map, c, f))
                {
                    MeshWriter.Quad(solid, ToWorld(rockTop[l] + lift, squash), ToWorld(rockTop[l] + inner + lift, squash),
                        ToWorld(rockTop[r] + inner + lift, squash), ToWorld(rockTop[r] + lift, squash), lineColor);
                    markRim[f] = true;
                }

                Vector3 q0 = rockGround[l] - back;
                Vector3 q1 = rockGround[r] - back;
                Vector3 lineTop = new Vector3(0f, OutlineWidth * 0.7f, 0f);
                MeshWriter.Quad(solid, ToWorld(q0, squash), ToWorld(q0 + lineTop, squash), ToWorld(q1 + lineTop, squash), ToWorld(q1, squash), lineColor);
                CutTags.Apply(layer, faceMark, c, own | cond);
            }
            if (room)
            {
                // marked for mining: the faint top-edge line (with the cap: under a floor, shown only while the floor is hidden)
                MineMarks.PrintOutline(coveredCap ? MeshWriter.Sub(layer, CoveredTwin(solidMat)) : solid, t, rockTop, markRim, OutlineWidth,
                    capMat != null ? capMat.color : tint * TopShade, squash);
            }
            CutTags.Apply(layer, whole, c, own);
            return true;
        }

        // Where the rock surface passes through one map corner: part of the way up (mid) and at
        // the rim (top), in cells. cx, cz are the corner's map coordinates.
        private static void RockCorner(Map map, int cx, int cz, float height, out Vector3 mid, out Vector3 top)
        {
            int sw = ShapesRock(map, new IntVec3(cx - 1, 0, cz - 1), height) ? 1 : 0;
            int se = ShapesRock(map, new IntVec3(cx, 0, cz - 1), height) ? 1 : 0;
            int nw = ShapesRock(map, new IntVec3(cx - 1, 0, cz), height) ? 1 : 0;
            int ne = ShapesRock(map, new IntVec3(cx, 0, cz), height) ? 1 : 0;

            // Which way is "into the rock" from this corner.
            int dx = Math.Sign(se + ne - sw - nw);
            int dz = Math.Sign(nw + ne - sw - se);
            if (dx == 0 && dz == 0)
            {
                mid = new Vector3(cx, height * 0.5f, cz);
                top = new Vector3(cx, height, cz);
                return;
            }

            float h1 = Hashing.Unit(cx, cz, 1);
            float h2 = Hashing.Unit(cx, cz, 2);
            float h3 = Hashing.Unit(cx, cz, 3);
            float inMid = 0.05f + 0.10f * h1;
            float inTop = 0.18f + 0.14f * h3;
            mid = new Vector3(cx + dx * inMid, height * (0.34f + 0.22f * h2), cz + dz * inMid);
            top = new Vector3(cx + dx * inTop, height - (0.03f + 0.10f * h1), cz + dz * inTop);
        }

        // Cells to the squashed world the game draws in.
        private static Vector3 ToWorld(Vector3 cells, float squash)
        {
            return new Vector3(cells.x, cells.y / squash, cells.z);
        }

        private static Vector2 TopUV(Vector3 cells)
        {
            return new Vector2(cells.x * 0.5f, cells.z * 0.5f);
        }

        // Natural rock's cap in its stone's own rough terrain (the game's texture and colour, as the rough ground beside the
        // rock and the mountain tops of stacked floors), so cap and rough ground read as one stone. Ore keeps RockOreTop.
        // Null when the stone has no natural terrain with a texture: the mod's RockTop then.
        private static readonly Dictionary<ThingDef, Material> rockCaps = new Dictionary<ThingDef, Material>();
        public static bool TerrainCaps = true;   // harness A/B (probe key rockcaps=0|1): off, the old RockTop caps

        // The cap of this rock: its stone's, tinted toward the mine mark's colour when marked for mining (MineMarks).
        private static Material RockCapMaterial(Thing t)
        {
            Material m = RockCapMaterial(t.def);
            if (m == null || !MineMarks.Marked(t))
            {
                return m;
            }
            return R3DTextures.MaterialFor(m.mainTexture as Texture2D, "IsoRim_RockCap_" + t.def.defName, MineMarks.Toward(m.color));
        }

        private static Material RockCapMaterial(ThingDef def)
        {
            Material m;
            if (!TerrainCaps)
            {
                return null;
            }
            if (rockCaps.TryGetValue(def, out m))
            {
                return m;
            }
            TerrainDef terrain = def.building != null ? def.building.naturalTerrain : null;
            Material tm = terrain != null && terrain.graphic != null ? terrain.graphic.MatSingle : null;
            Texture2D tex = tm != null ? tm.mainTexture as Texture2D : null;
            if (tex != null)
            {
                m = R3DTextures.MaterialFor(tex, "IsoRim_RockCap_" + def.defName, terrain.DrawColor);
            }
            rockCaps[def] = m;
            return m;
        }

        // The terrain's own texture coordinates (the game's MapWorldUV: x and -z over 16 cells), so the pattern runs on from
        // the rough stone beside the cap. On stacked floors in the rows of the storey above, whose floor the cap lies level
        // with (a mountain top beside it).
        private const float TerrainTileCells = 16f;

        private static int CapRows(Map map, IntVec3 c)
        {
            float h;
            int z;
            IntVec3 up = Floors.ColumnFloor(map, c, out h, out z) >= 0 ? Floors.Stacked(map, c, 1) : IntVec3.Invalid;
            return up.IsValid && up.InBounds(map) ? up.z - c.z : 0;
        }

        // Counted from the start of the tile the cell is in: the same pattern, with small numbers on big maps.
        private static Vector2 CapUV(Vector3 cells, Material capMat, int rows, IntVec3 c)
        {
            if (capMat == null)
            {
                return TopUV(cells);
            }
            float tileX = Mathf.Floor(c.x / TerrainTileCells) * TerrainTileCells;
            float tileZ = Mathf.Floor((c.z + rows) / TerrainTileCells) * TerrainTileCells;
            return new Vector2((cells.x - tileX) / TerrainTileCells, 1f - (cells.z + rows - tileZ) / TerrainTileCells);
        }

        private static Vector2 SideUV(Vector3 cells, Vector3 right)
        {
            return new Vector2(Vector3.Dot(cells, right) * 0.5f, cells.y / SideCellsHigh);
        }

        // Fallback when the mod's textures are switched off or missing: the game's own sprite is
        // raised to be the lid (by the caller) and these plain coloured sides go underneath it.
        public static void PrintPlainSides(SectionLayer layer, Thing t, float height)
        {
            Material solidMat = R3DMaterials.Solid;
            LayerSubMesh solid = solidMat != null ? MeshWriter.Sub(layer, solidMat) : null;
            if (solid == null || solid.verts.Count + 64 > MeshWriter.MaxVerts)
            {
                return;
            }

            Map map = t.Map;
            IntVec3 c = t.Position;
            Color color = MineMarks.Tint(t, t.DrawColor);   // marked for mining: toward the mark's colour
            float squash = View3D.Squash;
            float topY = t.def.Altitude + height / squash;
            Vector3 centre = new Vector3(c.x + 0.5f, 0f, c.z + 0.5f);
            Color32 lineColor = R3DMaterials.Shaded(color, LineShade);

            for (int f = 0; f < 4; f++)
            {
                if (!Exposed(map, c + FaceOut[f], height))
                {
                    continue;
                }
                Vector3 outward = FaceOut[f].ToVector3();
                Vector3 right = FaceRight[f].ToVector3();
                Vector3 p0 = centre + outward * 0.5f - right * 0.5f;
                Vector3 p1 = p0 + right;
                MeshWriter.Quad(LitSide(layer, f, solid), p0, Raised(p0, topY), Raised(p1, topY), p1, R3DMaterials.Shaded(color, 1f));

                Vector3 q0 = p0 + outward * Gap;
                Vector3 q1 = q0 + right;
                float lineTop = OutlineWidth * 0.7f / squash;
                MeshWriter.Quad(solid, q0, Raised(q0, lineTop), Raised(q1, lineTop), q1, lineColor);
                EndLines(solid, map, c, f, q0, right, topY, height, lineColor);
            }
        }

        // The line up each end of a run of wall. An end is always an outside corner, where the
        // next face round (the block's end face) draws its own end line. Each line runs on past
        // the corner by Gap, so the two meet in one line with no seam, and the shader
        // (IsoRim.vert.glsl) shares the line's width between them by how much each face is turned
        // toward the camera: half each when both show equally, the full width on the one face
        // that shows when the other is turned away (a silhouette corner).
        private static void EndLines(LayerSubMesh solid, Map map, IntVec3 c, int f, Vector3 q0, Vector3 right, float topY, float height, Color32 lineColor,
            SectionLayer layer = null, int own = -1)
        {
            // own >= 0 (shader cut-away, on a face that always shows): an end against a block that may be cut down is
            // baked too, shown only while that block is lower.
            bool left = ExposedFrom(map, c, c - FaceRight[f], height);
            bool leftMay = !left && own >= 0 && layer != null && MayDrop(map, c - FaceRight[f]) && !HullCorners.Covers(map, c - FaceRight[f], c, height);
            if (left || leftMay)
            {
                int m = leftMay ? CutTags.Mark(layer) : -1;
                Vector3 q = left ? Footed(q0, EndFoot(map, c, c - FaceRight[f])) : q0;
                Vector3 s = q - right * Gap;
                EndLine(solid, f, s, q, right, topY, lineColor, false);
                CutTags.Apply(layer, m, c, own | ShaderCut.Cond(IntVec3.Zero - FaceRight[f]));
            }
            bool rightEnd = ExposedFrom(map, c, c + FaceRight[f], height);
            bool rightMay = !rightEnd && own >= 0 && layer != null && MayDrop(map, c + FaceRight[f]) && !HullCorners.Covers(map, c + FaceRight[f], c, height);
            if (rightEnd || rightMay)
            {
                int m = rightMay ? CutTags.Mark(layer) : -1;
                Vector3 k = q0 + right;
                if (rightEnd)
                {
                    k = Footed(k, EndFoot(map, c, c + FaceRight[f]));
                }
                Vector3 e = k + right * Gap;
                EndLine(solid, f, e, k, -right, topY, lineColor, true);
                CutTags.Apply(layer, m, c, own | ShaderCut.Cond(FaceRight[f]));
            }
        }

        // (Api 11) Where the end line of a run beside a lower block starts: at that block's top when either of the two is a wall
        // with a shaped top (WallTops.cs), or (Api 20) the lower one is a low wall (LowWall: a half wall), so no line runs down
        // the joint where both faces lie flush (a castle wall beside a low wall); else at the foot (0), as always.
        private static float EndFoot(Map map, IntVec3 c, IntVec3 n)
        {
            if (map == null || !n.InBounds(map) || !c.InBounds(map))
            {
                return 0f;
            }
            Building nb = n.GetEdifice(map);
            Building ob = c.GetEdifice(map);
            if (nb == null || ob == null)
            {
                return 0f;
            }
            ThingForm nf = Forms.For(nb.def);
            if (nf.form != Form3D.Block || (nf.wallTops == null && Forms.For(ob.def).wallTops == null && !LowWall(nb.def)))
            {
                return 0f;
            }
            return BuiltHeightAt(map, n);
        }

        private static Vector3 Footed(Vector3 p, float foot)
        {
            return foot > 0f ? new Vector3(p.x, Mathf.Max(p.y, foot / View3D.Squash), p.z) : p;
        }

        // One end line on face f: from outer (past the corner) to the corner, whose inner edge the
        // vertex shader moves "along" (into the face) by its share of OutlineWidth. uv carries
        // the direction and width (xy) and which face it lies on (z = 2 + f).
        private static void EndLine(LayerSubMesh sm, int f, Vector3 outer, Vector3 corner, Vector3 along, float topY, Color32 color, bool rightEnd,
            float width = OutlineWidth)
        {
            Vector3 inner = new Vector3(along.x * width, along.z * width, 2f + f);
            Vector3 a = rightEnd ? corner : outer;
            Vector3 d = rightEnd ? outer : corner;
            Vector3 ua = rightEnd ? inner : Vector3.zero;
            Vector3 ud = rightEnd ? Vector3.zero : inner;
            int start = sm.verts.Count;
            sm.verts.Add(a);
            sm.verts.Add(Raised(a, topY));
            sm.verts.Add(Raised(d, topY));
            sm.verts.Add(d);
            sm.uvs.Add(ua);
            sm.uvs.Add(ua);
            sm.uvs.Add(ud);
            sm.uvs.Add(ud);
            for (int i = 0; i < 4; i++)
            {
                sm.colors.Add(color);
            }
            MeshWriter.AddQuadTris(sm, start);
        }

        private static Vector3 Raised(Vector3 v, float y)
        {
            return new Vector3(v.x, y, v.z);
        }

        // A side is only needed where the neighbouring cell does not hold a block at least as tall.
        private static bool Exposed(Map map, IntVec3 neighbour, float height)
        {
            if (!neighbour.InBounds(map))
            {
                return false;
            }
            return BlockHeightAt(map, neighbour) < height - 0.01f;
        }

        // How natural rock's outline sees a neighbouring cell (its bevel and lean: RockCorner, CliffLook's Occupancy): as
        // rock where a block at least as tall stands there (not Exposed), or a door (any Building_Door, every cell of a wide
        // one), whose straight frame stands flush against the rock, so the rock keeps a square edge beside a door.
        private static bool ShapesRock(Map map, IntVec3 c, float height)
        {
            return !Exposed(map, c, height) || (c.InBounds(map) && c.GetEdifice(map) is Building_Door);
        }

        // The same for the side of the block in cell from that looks onto neighbour: also hidden where
        // a gravship hull corner piece stands against it at least as tall (HullCorners).
        private static bool ExposedFrom(Map map, IntVec3 from, IntVec3 neighbour, float height)
        {
            return Exposed(map, neighbour, height) && !HullCorners.Covers(map, neighbour, from, height);
        }

        // ------------------------------------------------------------ walls cut down to show the room behind

        // How tall this block is drawn: its full height, or part of it if it is a wall that
        // stands between the camera and a room.
        public static float HeightFor(Thing t, float full)
        {
            return !FullHeights && IsCut(t.Map, t.Position, t) ? full * CutFraction : full;
        }

        // Set while a layer the shader cut-away lowers is built (ShaderCut): every block is baked whole and the
        // sides against neighbours that can be cut are baked too; the vertex stage does the cutting.
        public static bool FullHeights;

        // A block in this cell that the cut-away can lower (so the side of a block beside it may come to show): a block of
        // the walls layer, or a wall from a model (lowered in the vertex stage too, ModelBaker.BakeModel).
        public static bool MayDrop(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            if (b == null || !CanBeCut(b.def))
            {
                return false;
            }
            ThingForm form = Forms.For(b.def);
            return form.form == Form3D.Block ? SectionLayer_IsoRimWalls.Handles(b) : form.wallTop > 0f && ShaderCut.On;
        }

        public static float CutFraction
        {
            get { return Mathf.Clamp(IsoRimMod.Settings.cutHeight, 0.05f, 1f); }
        }

        // The height of the block standing in a cell (as cut down), or 0 if there is none. A wall drawn from a model counts
        // as a block of its wall top's height (ModelWall).
        public static float BlockHeightAt(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return 0f;
            }
            Building b = c.GetEdifice(map);
            if (b == null)
            {
                return 0f;
            }
            float h = WallHeightOf(Forms.For(b.def));
            if (h <= 0f)
            {
                return 0f;
            }
            return !FullHeights && IsCut(map, c, b) ? h * CutFraction : h;
        }

        // The height of the block standing in a cell as built, never cut down, or 0 if there is none. For what is
        // fixed to a wall (WallMount): the cut-away follows the pointer and the colonists every frame without
        // rebuilding the map mesh, so nothing baked may depend on it.
        public static float BuiltHeightAt(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return 0f;
            }
            Building b = c.GetEdifice(map);
            if (b == null)
            {
                return 0f;
            }
            return WallHeightOf(Forms.For(b.def));
        }

        // ------------------------------------------------------------ walls drawn from a model

        // A wall from a model pack: a building the game draws as a wall (one cell, linked as walls are: Fill > 0) whose
        // model is linked (ModelDef.linked: a post in the cell and an arm toward each neighbour it joins) and follows the
        // walls' height (ModelEntry.WallFit > 0: ModelDef.wallFit, the model's wall top in model space). It stands in for a
        // wall wherever IsoRim asks what wall is in a cell: BlockHeightAt / BuiltHeightAt / Floors.FullBlock count it as a
        // block of its wall top's height, so blocks beside it, slabs over it (ProjectZ), wall fixtures on it (WallMount),
        // rock beside it and joins (ModelBaker.Links) treat it as a wall; the cut-away lowers it as a full-height wall
        // (CanBeCut, cut tags in ModelBaker.BakeModel). Whatever of its model stands above the wall top (a castle wall's
        // merlons) is its crown: left out under a floor built right over it (Floors.SlabOver, as a crowned door's), and
        // clipped by a roof drawn over it (RoofClip). Fences, sandbags and barricades (linked, no wallFit) and fleshmass
        // (wallFit, but no wall link type) are not walls, so nothing about them changes.
        public static bool ModelWall(ThingDef def)
        {
            return def != null && Forms.For(def).wallTop > 0f;
        }

        // The wall top (cells, drawn, never cut down) of a wall drawn from this entry, or 0 if it is not one (Forms.Compute).
        internal static float ModelWallTop(ThingDef def, ModelEntry entry)
        {
            if (def == null || entry == null || !entry.def.linked || !entry.HasModel || def.category != ThingCategory.Building
                || def.IsFrame || def.IsBlueprint || Fill(def) <= 0f)
            {
                return 0f;
            }
            float fit = entry.WallFit(def);
            if (fit <= 0f)
            {
                return 0f;
            }
            return Mathf.Max(0f, fit * entry.def.scale * entry.Fit(def).y + entry.def.offset.y);
        }

        // How tall a thing of this form stands as a wall, as built: a block's height, a model wall's wall top, else 0.
        internal static float WallHeightOf(ThingForm form)
        {
            return form.form == Form3D.Block ? form.height : form.wallTop;
        }

        // Whether a thing of this form is a wall IsoRim draws: a block (walls, rock, sandbags ...) or a wall from a model.
        internal static bool IsWallForm(ThingForm form)
        {
            return form.form == Form3D.Block || form.wallTop > 0f;
        }

        // As in the Sims games: a built wall is cut down when the cell just behind it, seen from
        // the camera, is the floor of a roofed room. The far walls of the same room have open sky
        // behind them and stay whole, so each room keeps its back walls and loses the ones that
        // would hide it. Natural rock only with the cutRock option.
        public static bool IsCut(Map map, IntVec3 c, Thing wall)
        {
            if (map == null || wall == null || !CanBeCut(wall.def))
            {
                return false;
            }
            return CellCut(map, c);
        }

        // THE cut rule (Api 20, ModelDef.cutaway; every cut-away path asks it): a def's own rule (Never, Always) first;
        // Auto: built walls (full blocks, low walls, walls from a model) can be cut down, natural rock by the cutRock option,
        // nothing else (sandbags, barricades, fences). It only has an effect on things IsoRim draws as walls.
        public static bool CanBeCut(ThingDef def)
        {
            if (def == null)
            {
                return false;
            }
            ModelEntry entry;
            if (ModelLibrary.TryGet(def, out entry) && entry.def.cutaway != CutawayRule.Auto)
            {
                return entry.def.cutaway == CutawayRule.Always;
            }
            if (def.building != null && def.building.isNaturalRock)
            {
                return IsoRimMod.Settings.cutRock;
            }
            return Fill(def) >= 0.9f || ModelWall(def) || LowWall(def);
        }

        // (Api 20) A low wall: a wall IsoRim draws as a block (one the game counts as a wall: fill 0.9 or more, or linked as
        // walls) standing lower than nine tenths of the wall height (a ModelDef height or wallTop height under it, or a low
        // fill: a half wall, a low castle wall). The cut-away lowers it as any wall (CanBeCut, Auto); it is not a full-height
        // block (Floors.FullBlock): a wall upstairs does not reach down to it and a floor's slab is laid over it; the end line of
        // a taller wall beside it starts at its top (EndFoot). Sandbags and barricades (not linked as walls) are not walls.
        public static bool LowWall(ThingDef def)
        {
            if (def == null)
            {
                return false;
            }
            ThingForm form = Forms.For(def);
            if (form.form != Form3D.Block || form.height >= 0.9f * View3D.WallHeight)
            {
                return false;
            }
            return Fill(def) >= 0.9f || def.graphicData != null && (def.graphicData.linkFlags & LinkFlags.Wall) != 0;
        }

        // Whether whatever wall or door stands in this cell is to be drawn cut down: because the
        // pointer is over that stretch of wall, or (if that option is on) because it hides a room.
        public static bool CellCut(Map map, IntVec3 c)
        {
            if (FullWalls.Required(map))
            {
                return false;
            }
            if (Cutaway.Hovered(map, c))
            {
                return true;
            }
            if (!IsoRimMod.Settings.cutRooms)
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            if (b != null && b.def.building != null && b.def.building.isNaturalRock)
            {
                return HidesRoomDeep(map, c, RockReach);
            }
            return HidesRoom(map, c);
        }

        // How many cells of solid rock in front of a room (seen from the camera) are cut down,
        // so a room dug into a mountain can be seen into without lowering the whole mountain.
        public const int RockReach = 3;

        // True if, looking from this rock cell away from the camera, a room is reached within
        // reach cells, passing only through rock or walls on the way.
        public static bool HidesRoomDeep(Map map, IntVec3 c, int reach)
        {
            Vector3 f = View3D.GroundForward;
            int dx = f.x > 0.3f ? 1 : (f.x < -0.3f ? -1 : 0);
            int dz = f.z > 0.3f ? 1 : (f.z < -0.3f ? -1 : 0);
            return (dx != 0 && RoomAlong(map, c, dx, 0, reach))
                || (dz != 0 && RoomAlong(map, c, 0, dz, reach))
                || (dx != 0 && dz != 0 && RoomAlong(map, c, dx, dz, reach));
        }

        private static bool RoomAlong(Map map, IntVec3 c, int dx, int dz, int reach)
        {
            for (int k = 1; k <= reach; k++)
            {
                IntVec3 n = new IntVec3(c.x + dx * k, 0, c.z + dz * k);
                if (!n.InBounds(map))
                {
                    return false;
                }
                // (IsSolidBlock, not BlockHeightAt: that asks whether the neighbour is cut, which
                // would ask its own neighbours in turn, and so on across a whole mountain.)
                if (IsSolidBlock(map, n))
                {
                    continue;   // still inside the rock (or a wall): keep looking
                }
                if (!RoomFloor(map, n))
                {
                    return false;   // open ground that is not a room: nothing to show
                }
                Room room = n.GetRoom(map);
                return room != null && room.ProperRoom && !room.PsychologicallyOutdoors;
            }
            return false;
        }

        private static bool IsSolidBlock(Map map, IntVec3 c)
        {
            Building b = c.GetEdifice(map);
            return b != null && IsWallForm(Forms.For(b.def));
        }

        // True if a wall or door standing in this cell would hide the inside of a room from the camera.
        public static bool HidesRoom(Map map, IntVec3 c)
        {
            Vector3 f = View3D.GroundForward;
            int dx = f.x > 0.3f ? 1 : (f.x < -0.3f ? -1 : 0);
            int dz = f.z > 0.3f ? 1 : (f.z < -0.3f ? -1 : 0);
            return (dx != 0 && RoomFloor(map, new IntVec3(c.x + dx, 0, c.z)))
                || (dz != 0 && RoomFloor(map, new IntVec3(c.x, 0, c.z + dz)))
                || (dx != 0 && dz != 0 && RoomFloor(map, new IntVec3(c.x + dx, 0, c.z + dz)));
        }

        // Open floor under a roof.
        private static bool RoomFloor(Map map, IntVec3 c)
        {
            if (!c.InBounds(map) || !map.roofGrid.Roofed(c))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            if (b == null)
            {
                return true;
            }
            if (b is Building_Door)
            {
                return false;
            }
            return !IsWallForm(Forms.For(b.def));
        }
    }

    // ------------------------------------------------------------------ everything else

    // Gives any sprite thickness. The sprite itself is raised to become the top; a low-resolution
    // copy of it is read back once to find its outline, and sides are dropped from every edge
    // that faces the camera, coloured from the sprite's own pixels just inside that edge.
    public static class Extruder
    {
        private const int N = 32;                    // outline resolution per sprite (kept low: a full stockpile is thousands of edges)
        private const float BottomShade = 0.78f;     // sides darken toward the ground
        private const float LineShade = 0.30f;       // dark line where a side meets the ground
        private const float LineHeight = 0.03f;      // in cells
        private const float Gap = 0.004f;

        // dir 0 = toward the sprite's bottom edge, 1 = top, 2 = left, 3 = right.
        private static readonly int[] StepA = { 0, 0, -1, 1 };
        private static readonly int[] StepB = { -1, 1, 0, 0 };

        internal struct Run
        {
            public int dir;
            public float a0, b0, a1, b1;   // edge end points as fractions across and up the sprite
            public Color c0, c1;
        }

        internal class Mask
        {
            public List<Run> runs = new List<Run>();
            public List<Vector4> lids = new List<Vector4>();   // solid rectangles: a0, b0, a1, b1
            public Color mean;
        }

        private struct MaskKey : IEquatable<MaskKey>
        {
            public int tex, sx, sy, ox, oy;

            public bool Equals(MaskKey o)
            {
                return tex == o.tex && sx == o.sx && sy == o.sy && ox == o.ox && oy == o.oy;
            }

            public override bool Equals(object obj)
            {
                return obj is MaskKey && Equals((MaskKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = tex;
                    h = h * 397 + sx;
                    h = h * 397 + sy;
                    h = h * 397 + ox;
                    h = h * 397 + oy;
                    return h;
                }
            }
        }

        private static readonly Dictionary<MaskKey, Mask> cache = new Dictionary<MaskKey, Mask>();

        public static int CacheCount
        {
            get { return cache.Count; }
        }

        // sm holds the sprite quad as its last four vertices (n = its vertex count). bottom and
        // top are heights in cells. Returns false, leaving the sprite untouched, if it cannot be done.
        public static bool Extrude(SectionLayer layer, LayerSubMesh sm, int n, Material mat, float bottom, float top)
        {
            Material solidMat = R3DMaterials.Solid;
            if (solidMat == null || mat == null || top <= bottom)
            {
                return false;
            }

            Mask mask = MaskFor(mat, sm, n);
            if (mask == null)
            {
                return false;
            }
            LayerSubMesh solid = MeshWriter.Sub(layer, solidMat);
            if (solid == null || solid.verts.Count + mask.runs.Count * 8 + mask.lids.Count * 4 > MeshWriter.MaxVerts)
            {
                return false;
            }

            // Corners are stored bottom left, top left, top right, bottom right.
            Vector3 v0 = sm.verts[n - 4];
            Vector3 v1 = sm.verts[n - 3];
            Vector3 v3 = sm.verts[n - 1];
            float ax = v3.x - v0.x;
            float az = v3.z - v0.z;
            float bx = v1.x - v0.x;
            float bz = v1.z - v0.z;

            Color tint = sm.colors.Count >= n ? (Color)sm.colors[n - 4] : Color.white;
            if (mat.HasProperty("_Color"))
            {
                tint *= mat.color;
            }

            float squash = View3D.Squash;
            float yBottom = bottom / squash;
            float yTop = v0.y + top / squash;
            float yLine = Mathf.Min(yTop, yBottom + LineHeight / squash);
            float yLid = Mathf.Max(yBottom, yTop - 0.01f / squash);

            MeshWriter.Raise(sm, n, top);

            if (bottom <= 0.001f && GroundShadows.On)
            {
                GroundShadows.UnderSprite(layer, v0, new Vector2(ax, az), new Vector2(bx, bz), mask.lids, top - bottom);
            }

            for (int i = 0; i < mask.runs.Count; i++)
            {
                Run r = mask.runs[i];

                // Which way this edge faces in the world. (Sides are built all the way round, so
                // that nothing is missing while the camera turns.)
                float nx;
                float nz;
                switch (r.dir)
                {
                    case 0:
                        nx = -bx;
                        nz = -bz;
                        break;
                    case 1:
                        nx = bx;
                        nz = bz;
                        break;
                    case 2:
                        nx = -ax;
                        nz = -az;
                        break;
                    default:
                        nx = ax;
                        nz = az;
                        break;
                }
                float len = Mathf.Sqrt(nx * nx + nz * nz);
                if (len < 1e-5f)
                {
                    continue;
                }
                nx /= len;
                nz /= len;
                float px = v0.x + r.a0 * ax + r.b0 * bx;
                float pz = v0.z + r.a0 * az + r.b0 * bz;
                float qx = v0.x + r.a1 * ax + r.b1 * bx;
                float qz = v0.z + r.a1 * az + r.b1 * bz;

                // The side is lit by the sun through the material of its direction group.
                float shade = 0.92f;
                Color c0 = r.c0 * tint;
                Color c1 = r.c1 * tint;
                Material sideMat = R3DMaterials.Lit(R3DMaterials.BinOf(new Vector3(nx, 0f, nz)));
                LayerSubMesh side = sideMat != null ? MeshWriter.Sub(layer, sideMat) : solid;
                if (side == null || side.verts.Count + 4 > MeshWriter.MaxVerts)
                {
                    side = solid;
                }
                MeshWriter.Quad(side,
                    new Vector3(px, yBottom, pz), new Vector3(px, yTop, pz), new Vector3(qx, yTop, qz), new Vector3(qx, yBottom, qz),
                    R3DMaterials.Shaded(c0, shade * BottomShade), R3DMaterials.Shaded(c0, shade),
                    R3DMaterials.Shaded(c1, shade), R3DMaterials.Shaded(c1, shade * BottomShade));

                float gx = nx * Gap;
                float gz = nz * Gap;
                MeshWriter.Quad(solid,
                    new Vector3(px + gx, yBottom, pz + gz), new Vector3(px + gx, yLine, pz + gz),
                    new Vector3(qx + gx, yLine, qz + gz), new Vector3(qx + gx, yBottom, qz + gz),
                    R3DMaterials.Shaded(c0, LineShade), R3DMaterials.Shaded(c0, LineShade),
                    R3DMaterials.Shaded(c1, LineShade), R3DMaterials.Shaded(c1, LineShade));
            }

            // A solid lid just under the sprite, so things standing behind are hidden by it
            // whatever the game's sprite material does.
            Color32 lidColor = R3DMaterials.Shaded(mask.mean * tint, 0.85f);
            for (int i = 0; i < mask.lids.Count; i++)
            {
                Vector4 l = mask.lids[i];
                MeshWriter.Quad(solid,
                    new Vector3(v0.x + l.x * ax + l.y * bx, yLid, v0.z + l.x * az + l.y * bz),
                    new Vector3(v0.x + l.x * ax + l.w * bx, yLid, v0.z + l.x * az + l.w * bz),
                    new Vector3(v0.x + l.z * ax + l.w * bx, yLid, v0.z + l.z * az + l.w * bz),
                    new Vector3(v0.x + l.z * ax + l.y * bx, yLid, v0.z + l.z * az + l.y * bz),
                    lidColor);
            }
            return true;
        }

        private static Mask MaskFor(Material mat, LayerSubMesh sm, int n)
        {
            if (!mat.HasProperty("_MainTex"))
            {
                return null;
            }
            Texture tex = mat.mainTexture;
            if (tex == null)
            {
                return null;
            }

            // The part of the texture this quad shows. Many sprites share one large sheet.
            Vector2 uv0 = Vector2.zero;
            Vector2 uv1 = Vector2.up;
            Vector2 uv3 = Vector2.right;
            if (sm.uvs.Count >= n)
            {
                uv0 = sm.uvs[n - 4];
                uv1 = sm.uvs[n - 3];
                uv3 = sm.uvs[n - 1];
            }
            Vector2 across = uv3 - uv0;
            Vector2 up = uv1 - uv0;
            if (Mathf.Abs(across.y) > 1e-4f || Mathf.Abs(up.x) > 1e-4f)
            {
                return null;
            }
            Vector2 ts = mat.mainTextureScale;
            Vector2 to = mat.mainTextureOffset;
            Vector2 scale = new Vector2(across.x * ts.x, up.y * ts.y);
            Vector2 offset = new Vector2(uv0.x * ts.x + to.x, uv0.y * ts.y + to.y);

            return MaskOf(tex, scale, offset);
        }

        // The outline of the part of a texture shown at the given scale and offset, read once and kept.
        internal static Mask MaskOf(Texture tex, Vector2 scale, Vector2 offset)
        {
            MaskKey key = new MaskKey();
            key.tex = tex.GetInstanceID();
            key.sx = Mathf.RoundToInt(scale.x * 16384f);
            key.sy = Mathf.RoundToInt(scale.y * 16384f);
            key.ox = Mathf.RoundToInt(offset.x * 16384f);
            key.oy = Mathf.RoundToInt(offset.y * 16384f);

            Mask mask;
            if (cache.TryGetValue(key, out mask))
            {
                return mask;
            }
            try
            {
                mask = Build(tex, scale, offset);
            }
            catch (Exception e)
            {
                mask = null;
                Log.ErrorOnce("[IsoRim] Reading a sprite's outline failed: " + e, 93611020);
            }
            cache[key] = mask;
            return mask;
        }

        // A low-resolution sample of the sprite (SpritePixels: read on the CPU, as the 1.5 mod's
        // Blit and ReadPixels gave it). A whole texture whose pixels cannot be read is still
        // sampled the 1.5 way on the GPU.
        private static Mask Build(Texture tex, Vector2 scale, Vector2 offset)
        {
            Color32[] px = SpritePixels.Sample(tex, scale, offset, N);
            if (px == null && scale == Vector2.one && offset == Vector2.zero && !SpritePixels.IsSheet(tex))
            {
                px = SpritePixels.GpuSample(tex, N);
            }
            return FromPixels(px);
        }

        public const int MaskSize = N;

        // The outline and mean colour from an N x N sample (rows bottom first). Null if empty.
        internal static Mask FromPixels(Color32[] px)
        {
            if (px == null || px.Length < N * N)
            {
                return null;
            }

            bool[] solid = new bool[N * N];
            float r = 0f;
            float g = 0f;
            float b = 0f;
            int count = 0;
            for (int k = 0; k < N * N; k++)
            {
                if (px[k].a >= 128)
                {
                    solid[k] = true;
                    r += px[k].r;
                    g += px[k].g;
                    b += px[k].b;
                    count++;
                }
            }
            if (count == 0)
            {
                return null;
            }

            Mask mask = new Mask();
            mask.mean = new Color(r / count / 255f, g / count / 255f, b / count / 255f, 1f);
            FindRuns(mask, px, solid);
            FindLids(mask, solid);
            return mask;
        }

        private static bool IsSolid(bool[] solid, int i, int j)
        {
            return i >= 0 && j >= 0 && i < N && j < N && solid[j * N + i];
        }

        private static bool IsEdge(bool[] solid, int i, int j, int dir)
        {
            return IsSolid(solid, i, j) && !IsSolid(solid, i + StepA[dir], j + StepB[dir]);
        }

        // Sprites have dark outlines, so the colour is the lightest of the few steps in from the edge.
        private static Color Inside(Color32[] px, bool[] solid, int i, int j, int dir)
        {
            Color best = Opaque(px[j * N + i]);
            float bestLight = best.r + best.g + best.b;
            for (int k = 1; k <= 4; k++)
            {
                int ii = i - StepA[dir] * k;
                int jj = j - StepB[dir] * k;
                if (!IsSolid(solid, ii, jj))
                {
                    break;
                }
                Color c = Opaque(px[jj * N + ii]);
                float light = c.r + c.g + c.b;
                if (light > bestLight)
                {
                    best = c;
                    bestLight = light;
                }
            }
            return best;
        }

        private static Color Opaque(Color32 c)
        {
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
        }

        private static bool Differs(Color x, Color y)
        {
            return Mathf.Abs(x.r - y.r) > 0.12f || Mathf.Abs(x.g - y.g) > 0.12f || Mathf.Abs(x.b - y.b) > 0.12f;
        }

        // Joins neighbouring edge steps into longer stretches wherever the colour changes smoothly.
        private static void FindRuns(Mask mask, Color32[] px, bool[] solid)
        {
            for (int dir = 0; dir < 4; dir++)
            {
                bool alongA = dir < 2;
                for (int line = 0; line < N; line++)
                {
                    int k = 0;
                    while (k < N)
                    {
                        int i = alongA ? k : line;
                        int j = alongA ? line : k;
                        if (!IsEdge(solid, i, j, dir))
                        {
                            k++;
                            continue;
                        }

                        int start = k;
                        Color first = Inside(px, solid, i, j, dir);
                        Color last = first;
                        k++;
                        while (k < N)
                        {
                            int i2 = alongA ? k : line;
                            int j2 = alongA ? line : k;
                            if (!IsEdge(solid, i2, j2, dir))
                            {
                                break;
                            }
                            Color next = Inside(px, solid, i2, j2, dir);
                            if (Differs(next, last))
                            {
                                break;
                            }
                            last = next;
                            k++;
                        }

                        Run run = new Run();
                        run.dir = dir;
                        run.c0 = first;
                        run.c1 = last;
                        float lo = (float)start / N;
                        float hi = (float)k / N;
                        float fixedAt = (float)((dir == 0 || dir == 2) ? line : line + 1) / N;
                        if (alongA)
                        {
                            run.a0 = lo;
                            run.a1 = hi;
                            run.b0 = fixedAt;
                            run.b1 = fixedAt;
                        }
                        else
                        {
                            run.a0 = fixedAt;
                            run.a1 = fixedAt;
                            run.b0 = lo;
                            run.b1 = hi;
                        }
                        mask.runs.Add(run);
                    }
                }
            }
        }

        // Covers the solid part of the sprite with as few rectangles as a simple sweep finds.
        private static void FindLids(Mask mask, bool[] solid)
        {
            List<int[]> open = new List<int[]>();   // each: first column, end column, first row
            for (int j = 0; j <= N; j++)
            {
                List<int[]> next = new List<int[]>();
                if (j < N)
                {
                    int i = 0;
                    while (i < N)
                    {
                        if (!solid[j * N + i])
                        {
                            i++;
                            continue;
                        }
                        int start = i;
                        while (i < N && solid[j * N + i])
                        {
                            i++;
                        }

                        int[] carried = null;
                        for (int k = 0; k < open.Count; k++)
                        {
                            if (open[k][0] == start && open[k][1] == i)
                            {
                                carried = open[k];
                                open.RemoveAt(k);
                                break;
                            }
                        }
                        next.Add(carried ?? new int[] { start, i, j });
                    }
                }

                for (int k = 0; k < open.Count; k++)
                {
                    int[] o = open[k];
                    mask.lids.Add(new Vector4((float)o[0] / N, (float)o[2] / N, (float)o[1] / N, (float)j / N));
                }
                open = next;
            }
        }
    }

    // ------------------------------------------------------------------ hand-made models

    public static class ModelBaker
    {
        public const float OutlineShade = 0.25f;
        public const float Inset = 0.1f;   // fraction of the outline width the plain copy sits inside the textured one
        // How far a textured face stands off the plain copy under it, along the face, in model
        // units. The two are drawn by different shaders, which do not always work out exactly
        // the same depth for the same point; without a clear gap the two fight and flicker.
        public const float FaceLift = 0.004f;
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        // Writes the model into the block's mesh in place of the thing's sprite. baseHeight is
        // how far above the ground it rests, in cells. Returns false if it could not, in which
        // case the sprite is printed as usual.
        //
        // A model is drawn in up to three layers:
        //   1. a plain-coloured copy, fully solid, which hides whatever is behind the model;
        //   2. the same shape slightly enlarged and inside out, which shows as a dark outline;
        //   3. the textured faces, laid over the plain copy.
        // A quarter turn for something whose row runs north-south (the models are built running east-west).
        private static float RunTurn(Thing t)
        {
            Map map = t.Map;
            if (map == null)
            {
                return 0f;
            }
            IntVec3 c = t.Position;
            int eastWest = Joins(map, c + IntVec3.East, t.def) + Joins(map, c + IntVec3.West, t.def);
            int northSouth = Joins(map, c + IntVec3.North, t.def) + Joins(map, c + IntVec3.South, t.def);
            return northSouth > eastWest ? 90f : 0f;
        }

        private static int Joins(Map map, IntVec3 c, ThingDef def)
        {
            if (!c.InBounds(map))
            {
                return 0;
            }
            Building b = c.GetEdifice(map);
            return b != null && (b.def == def || WallBoxes.BlockHeightAt(map, c) > 0f) ? 1 : 0;
        }

        // Puts a wall fitting on the face of its wall at the height WallMount gives it. The models are built
        // in the cell in front of the wall, facing it.
        private static void Mount(Thing t, ModelDef def, Model m, float fitY, ref Vector3 center, ref float yaw, ref float baseHeight)
        {
            float wall = WallMount.Wall(t.Map, t.Position, t.Rotation, ref center, ref yaw);
            baseHeight = WallMount.ModelLift(def, wall, m.maxY * fitY);
        }

        // How big and which way round a thing's model is baked, before anything that depends on what kind of thing it
        // is (a plant's growth): the def's scale and turn, its random turn and size jitter.
        public static void StartPlace(Thing thing, ModelEntry entry, out float scale, out float yaw)
        {
            ModelDef def = entry.def;
            scale = def.scale;
            yaw = entry.Yaw(thing.Rotation);
            int id = thing.thingIDNumber;
            if (def.randomRotation)
            {
                float spin = Hashing.Unit(id, 7, 1) * 360f;
                // a sunk hole's apron covers its square cut only when turned by quarter turns
                yaw += def.sinkHole > 0f ? Mathf.Floor(spin / 90f) * 90f : spin;
            }
            if (def.scaleJitter > 0f)
            {
                scale *= 1f + (Hashing.Unit(id, 11, 2) * 2f - 1f) * def.scaleJitter;
            }
        }

        // True for a plant growing in a plant grower (a plant pot, a bonsai pot, a hydroponics basin: Building_PlantGrower, as the
        // game sows them): it stands in the middle of its cell, as the game draws it there (Plant.Print moves a single plant at
        // most 0.05 off the middle), not scattered about the cell as in a field (ModelDef.scatter).
        public static bool InPlanter(Thing thing)
        {
            return thing is Plant && thing.Spawned && thing.Position.GetEdifice(thing.Map) is Building_PlantGrower;
        }

        // Where a thing's model m stands: its centre on the map, its turn (more turns added to yaw), its size per axis
        // (fit) and its base height (wall fittings go up their wall). One place for the bake and for what is drawn
        // every frame in step with the baked model (Effects' flames).
        public static void Place(Thing thing, ModelEntry entry, Model m, float scale, ref float yaw, ref float baseHeight,
            out Vector3 center, out Vector3 fit, out bool mounted)
        {
            ModelDef def = entry.def;
            center = thing.DrawPos;   // centre of the footprint for anything that does not move
            if (def.alongRun)
            {
                yaw += RunTurn(thing);
            }
            if (entry.Swapped(thing.def))
            {
                yaw += 90f;
            }
            mounted = WallMount.Is(thing.def);
            fit = entry.Fit(thing.def) * scale;
            if (mounted)
            {
                Mount(thing, def, m, fit.y, ref center, ref yaw, ref baseHeight);
            }
            if (def.scatter > 0f && !InPlanter(thing))   // a plant in a pot or basin stands in its middle
            {
                int id = thing.thingIDNumber;
                center.x += (Hashing.Unit(id, 13, 3) - 0.5f) * def.scatter;
                center.z += (Hashing.Unit(id, 17, 4) - 0.5f) * def.scatter;
            }
            if (def.overArt && thing.MultipleItemsPerCellDrawn())
            {
                fit *= 0.8f;   // as the game draws several items in one cell (a shelf) at 0.8 size
            }
        }

        // Where a point of the model (model space) ends up on the map (y in cells above the ground), and the model's
        // size per axis there, as Place puts it for the bake (not for models laid flat).
        public static Vector3 PointOnMap(Thing thing, ModelEntry entry, Model m, Vector3 local, out Vector3 fit, out float yaw)
        {
            float scale;
            StartPlace(thing, entry, out scale, out yaw);
            float baseHeight = 0f;
            Vector3 center;
            bool mounted;
            Place(thing, entry, m, scale, ref yaw, ref baseHeight, out center, out fit, out mounted);
            Vector3 p = Quaternion.AngleAxis(yaw, Vector3.up) * (Vector3.Scale(local, fit) + entry.def.offset);
            return new Vector3(center.x + p.x, baseHeight + p.y, center.z + p.z);
        }

        // Every model baked into a layer is tagged to be clipped under the roof of the cell it stands in (RoofClip).
        public static bool Bake(SectionLayer layer, Thing thing, ModelEntry entry, float baseHeight)
        {
            int mark = RoofClip.Mark(layer);
            Floor = entry.def.sinkHole > 0f ? float.NegativeInfinity : 0f;   // a sunk hole reaches below the ground (SinkHoles)
            try
            {
                return BakeModel(layer, thing, entry, baseHeight);
            }
            finally
            {
                Floor = 0f;
                RoofClip.Tag(layer, mark, thing.TrueCenter().ToIntVec3(), BuildSee.Takes(thing, entry));   // whatever was written (nothing if it failed)
            }
        }

        // The lowest height a baked model's corners are written at: the ground, except below a sunk hole (Bake).
        private static float Floor;

        // (Api 34) the shape pieces a wall drawn from a model stands in (ShapeModels.For), reused.
        private static readonly List<ShapeModels.Piece> shapePieces = new List<ShapeModels.Piece>();

        private static bool BakeModel(SectionLayer layer, Thing thing, ModelEntry entry, float baseHeight)
        {
            ModelDef def = entry.def;
            Model m = entry.Pick(thing);
            if (def.linked && entry.variants != null && entry.variants.Length >= 1)
            {
                m = entry.variants[0];   // the part that stands in the cell; the second is added toward each neighbour
            }
            if (def.fullVariant && entry.variants != null && entry.variants.Length >= 2)
            {
                // Two models, for empty and for occupied: a grave is a pit until someone is buried in it.
                Building_Casket holder = thing as Building_Casket;
                m = entry.variants[holder != null && holder.HasAnyContents ? 1 : 0];
            }
            if (Ghost.a <= 0f)
            {
                m = StateModels.Choose(thing, entry, m);   // an opened mech capsule: its open model
                m = StateModels.ForLight(thing, m);        // a lamp whose light is out: its glow faces unlit
            }
            if (R3DMaterials.Solid == null || m == null)
            {
                return false;
            }
            IsoRimSettings st = IsoRimMod.Settings;

            // A wall drawn from a model (WallBoxes.ModelWall; also its blueprint's ghost): under a floor built right over it
            // its crown (everything above its wall top, a castle wall's merlons) is left out and a flat lid closes it at the
            // wall top, as a crowned door's frame (Crowns); the cut-away lowers it as a block (below).
            ThingDef wallDef = WallDefOf(thing, entry);
            Model arm = def.linked && entry.variants.Length >= 2 ? entry.variants[1] : null;
            if (wallDef != null && thing.Map != null && Floors.SlabOver(thing.Map, thing.Position))
            {
                float wallTop = entry.WallFit(wallDef);   // model space
                m = Crowns.Under(m, wallTop, true);
                arm = Crowns.Under(arm, wallTop, false);
                if (m == null)
                {
                    return false;
                }
            }
            bool wall = wallDef != null && Ghost.a <= 0f;

            // Where, which way round and how big.
            float scale, yaw;
            StartPlace(thing, entry, out scale, out yaw);
            bool bare = false;
            Plant plant = thing as Plant;
            if (plant != null)
            {
                if (def.growthScale && plant.def.plant != null)
                {
                    scale *= plant.def.plant.visualSizeRange.LerpThroughRange(plant.Growth);
                }
                bare = (plant.LeaflessNow && !def.keepLeaves)
                    || (!CutTags.Enabled && plant.def.plant != null && plant.def.plant.IsTree && Cutaway.TreeThinned(thing.Map, thing.Position));
            }

            Vector3 offset = def.offset;
            Vector3 center, fit;
            bool mounted;
            Place(thing, entry, m, scale, ref yaw, ref baseHeight, out center, out fit, out mounted);
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            Quaternion lay = def.layFlat ? Quaternion.AngleAxis(90f, Vector3.right) : Quaternion.identity;
            Vector3 layLift = def.layFlat ? new Vector3(0f, m.halfDepth, 0f) : Vector3.zero;
            Color tint = def.spriteTint ? SpriteColors.MeanFor(thing) : R3DTextures.ModelTint(thing);

            // A wall drawn from a model goes down with the cut-away as a block does: with the shader cut-away (ShaderCut) it
            // is baked whole and every vertex of it (post, arms, damage marks) tagged with its cell and Lower, so the vertex
            // stage presses it down to the cut height (the cell's full height is its wall top: IsoCellMap, BlockHeightAt;
            // its crown goes down with it); with the old cut-away the block is rebuilt, so it is baked cut down.
            bool wallTags = wall && ShaderCut.On && WallBoxes.CanBeCut(thing.def);
            if (wall && !ShaderCut.On && !WallBoxes.FullHeights && WallBoxes.IsCut(thing.Map, thing.Position, thing))
            {
                fit.y *= WallBoxes.CutFraction;
            }
            // (Api 34) a wall or fence drawn from a linked model standing in wall shapes (a trimmed cell): its straight run
            // along the slant, cut to the shapes (ShapeModels), drawn in place of post and arms.
            Model shaped = null;
            Building linkedBuilding = thing as Building;
            if (def.linked && Ghost.a <= 0f && thing.Map != null && (wall || ShapeModels.Takes(linkedBuilding)))
            {
                int links = 0;
                for (int d = 0; d < 4; d++)
                {
                    if (Links(thing, thing.Position + GenAdj.CardinalDirections[d]))
                    {
                        links |= 1 << d;
                    }
                }
                shaped = ShapeModels.For(thing, m, arm, yaw, links, shapePieces, !wall);
            }
            bool tagsWere = CutTags.Enabled;
            int cutMark = -1;
            if (wallTags)
            {
                CutTags.Enabled = true;
                cutMark = CutTags.Mark(layer);
            }
            try
            {
                // (a wall drawn from a model is outlined as one wall: no line along the joints with the next one, and its
                // arms, which carry it on into the post's neighbours, add no outline of their own)
                int joined = wall ? JoinedSides(thing) : 0;
                if (shaped != null)
                {
                    if (!Write(layer, thing, shaped, center, Quaternion.identity, lay, layLift, fit, offset, baseHeight, tint, bare, def.outline))
                    {
                        return false;
                    }
                }
                else if (!Write(layer, thing, m, center, turn, lay, layLift, fit, offset, baseHeight, tint, bare, def.outline, joined))
                {
                    return false;
                }
                if (entry.roofPart != null)
                {
                    // (Api 31) ModelDef.roofModel: placed as the model, tagged to rise through a roof over it (its own mark
                    // inside Bake's: the outer tag only adds to tags already set, so ThroughRoof stays)
                    int roofMark = RoofClip.Mark(layer);
                    try
                    {
                        Write(layer, thing, entry.roofPart, center, turn, lay, layLift, fit, offset, baseHeight, tint, bare, def.outline);
                    }
                    finally
                    {
                        RoofClip.Tag(layer, roofMark, thing.TrueCenter().ToIntVec3(), BuildSee.Takes(thing, entry), throughRoof: true);
                    }
                }

                bool isTree = plant != null && plant.def.plant != null && plant.def.plant.IsTree;
                if (Ghost.a > 0f || DamageMarks > 0)
                {
                    isTree = false;   // a ghost (or a set of damage marks) casts no shadow
                }
                // A tree hidden in the fog (the fog clips it, RoofClip) casts no sun shadow onto revealed ground; the game's
                // unfog redraws its block (FogOfWar | Things on the cell, SectionLayer_IsoRimTrees.MapMeshDirty_Postfix).
                bool fogged = isTree && RoofClip.FogOn && thing.Spawned && thing.Map.fogGrid.IsFogged(thing.Position);
                if (st.treeShadows && isTree && !def.layFlat && !fogged)
                {
                    try
                    {
                        TreeShadows.Print(layer, m, center, turn, scale, offset, baseHeight, bare);
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[IsoRim] Drawing a tree's shadow failed: " + e, 93611060);
                    }
                }
                // (a wall from a model gets the band along a wall's foot instead, below)
                if (st.groundShadows && def.groundShadow && !mounted && baseHeight <= 0.001f && (plant == null || isTree) && Ghost.a <= 0f && DamageMarks <= 0 && !wall)
                {
                    try
                    {
                        GroundShadows.UnderModel(layer, shaped ?? m, center, shaped != null ? Quaternion.identity : turn, fit, offset, def.layFlat, isTree, bare);
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[IsoRim] Shading the ground under a model failed: " + e, 93611063);
                    }
                }

                // Fences and the like: one more part toward each neighbour the thing joins up with.
                if (arm != null && thing.Map != null && shaped == null)
                {
                    for (int d = 0; d < 4; d++)
                    {
                        if (Links(thing, thing.Position + GenAdj.CardinalDirections[d]))
                        {
                            Write(layer, thing, arm, center, Quaternion.AngleAxis(90f * d, Vector3.up), Quaternion.identity, Vector3.zero,
                                fit, offset, baseHeight, tint, false, wall ? 0f : def.outline);
                        }
                    }
                }
                if (wallTags)
                {
                    CutTags.Apply(layer, cutMark, thing.Position, ShaderCut.Lower);
                }
            }
            finally
            {
                CutTags.Enabled = tagsWere;
            }
            if (wall && DamageMarks <= 0 && thing.Map != null)
            {
                IsoCellMap.Dirty(thing.Map, CellRect.SingleCell(thing.Position));   // the fog lid and the cut-away's full height lie on its wall top
                if (shaped != null)
                {
                    ShapeModels.GroundBands(layer, shapePieces);   // (Api 34) along the open sides of its shape pieces
                }
                else
                {
                    GroundShadows.AroundBlock(layer, thing);   // the band along a wall's foot, as a block's (not the soft shade under a model)
                }
            }
            return true;
        }

        // The wall def a thing's model stands for when it is a wall drawn from a model (WallBoxes.ModelWall): the thing's
        // own, or for a blueprint or frame (its ghost) the def it will become; null for anything else.
        private static ThingDef WallDefOf(Thing thing, ModelEntry entry)
        {
            if (thing == null || thing.def == null || !entry.def.linked)
            {
                return null;
            }
            ThingDef def = thing.def.entityDefToBuild as ThingDef ?? thing.def;
            ThingForm f = Forms.For(def);
            return f.wallTop > 0f && f.model == entry ? def : null;
        }

        // The sides (bits 1 << d, GenAdj.CardinalDirections order) where a wall drawn from a model joins on to a door or a
        // wall at least as tall (a block or another such wall) beside it.
        private static int JoinedSides(Thing t)
        {
            Map map = t.Map;
            if (map == null)
            {
                return 0;
            }
            float own = WallBoxes.BuiltHeightAt(map, t.Position);
            int bits = 0;
            for (int d = 0; d < 4; d++)
            {
                IntVec3 c = t.Position + GenAdj.CardinalDirections[d];
                if (!c.InBounds(map))
                {
                    continue;
                }
                Building b = c.GetEdifice(map);
                if (b != null && (b is Building_Door || WallBoxes.BuiltHeightAt(map, c) >= own - 0.01f))
                {
                    bits |= 1 << d;
                }
            }
            return bits;
        }

        // Whether a fence (or other linked thing) joins on to what is in a neighbouring cell.
        private static bool Links(Thing t, IntVec3 c)
        {
            Map map = t.Map;
            if (!c.InBounds(map))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            if (b == null)
            {
                return false;
            }
            return b.def == t.def || b is Building_Door || WallBoxes.BlockHeightAt(map, c) > 0f
                || (b.def.defName != null && b.def.defName.Contains("Fence")) || SharesLink(t.def, b.def);
        }

        // Two defs that join in the game through a shared link flag other than walls, rock and the map edge (Fleshmass and
        // Fleshmass_Active through LinkFlags.Fleshmass).
        private static bool SharesLink(ThingDef a, ThingDef b)
        {
            if (a.graphicData == null || b.graphicData == null)
            {
                return false;
            }
            LinkFlags shared = a.graphicData.linkFlags & b.graphicData.linkFlags & ~(LinkFlags.Wall | LinkFlags.Rock | LinkFlags.MapEdge);
            return shared != LinkFlags.None;
        }

        // Writes one model into the block's mesh. turn, lay and layLift place it; center is where
        // its origin goes on the map and baseHeight how far above the ground it rests, in cells.
        // Returns false, having written nothing, if the mesh has no room for it.
        private static LayerSubMesh[] texturedPool = new LayerSubMesh[64];
        private static bool[] lookedPool = new bool[64];

        // While this has any alpha, Bake writes the model as a see-through ghost of this colour
        // (for blueprints and frames) instead of the real thing.
        public static Color Ghost = new Color(0f, 0f, 0f, 0f);
        // With Ghost: written with the x-ray ghost material (a blueprint inside a solid building, Ghosts.Print).
        public static bool GhostXray;

        private static bool WriteGhost(SectionLayer layer, Model m, Vector3 center, Quaternion turn, Quaternion lay, Vector3 layLift,
            Vector3 scale, Vector3 offset, float baseHeight)
        {
            Material mat = GhostXray && R3DMaterials.GhostXray != null ? R3DMaterials.GhostXray : R3DMaterials.Ghost;   // (Ghosts.Print: inside a solid building)
            LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
            int n = m.positions.Length;
            if (sm == null || sm.verts.Count + n > MeshWriter.MaxVerts)
            {
                return false;
            }
            float squash = View3D.Squash;
            float baseY = baseHeight / squash;
            for (int i = 0; i + 2 < n; i += 3)
            {
                Vector3 normal = turn * (lay * m.normals[i]);
                // A little shading so the shape reads, brighter on top.
                float k = 0.70f + 0.30f * Mathf.Clamp01(normal.y * 0.5f + 0.5f);
                Color32 c = new Color(Ghost.r * k, Ghost.g * k, Ghost.b * k, Ghost.a);
                for (int v = i; v < i + 3; v++)
                {
                    Vector3 p = turn * (Vector3.Scale(lay * m.positions[v] + layLift, scale) + offset);
                    sm.tris.Add(sm.verts.Count);
                    sm.verts.Add(new Vector3(center.x + p.x, baseY + Mathf.Max(0f, p.y) / squash, center.z + p.z));
                    sm.colors.Add(c);
                    sm.uvs.Add(default);
                }
            }
            return true;
        }

        // While above zero, Bake writes this many damage marks onto the model's surface instead
        // of the model itself (see SectionLayer_IsoRimDamage).
        public static int DamageMarks;

        // While set, Bake leaves out flames and embers (ModelMaterial.fire): a torch or campfire
        // whose fuel has run out (see SectionLayer_IsoRimBodies).
        public static bool NoFire;

        // While set, Bake leaves out the model's own flames (flame* parts) but keeps embers: Effects draws a live
        // flame there every frame instead (Effects.Animates).
        public static bool NoFlame;

        private static bool WriteDamage(SectionLayer layer, Thing thing, Model m, Vector3 center, Quaternion turn, Quaternion lay, Vector3 layLift,
            Vector3 scale, Vector3 offset, float baseHeight)
        {
            int tris = m.positions.Length / 3;
            if (tris == 0)
            {
                return false;
            }
            int id = thing.thingIDNumber;
            int placed = 0;
            for (int k = 0; k < DamageMarks * 8 && placed < DamageMarks; k++)
            {
                int ti = Mathf.Min(tris - 1, (int)(Hashing.Unit(id, k, 41) * tris));
                int i = ti * 3;
                Vector3 a = turn * (Vector3.Scale(lay * m.positions[i] + layLift, scale) + offset);
                Vector3 b = turn * (Vector3.Scale(lay * m.positions[i + 1] + layLift, scale) + offset);
                Vector3 c = turn * (Vector3.Scale(lay * m.positions[i + 2] + layLift, scale) + offset);
                Vector3 n = Vector3.Cross(b - a, c - a);
                float area = n.magnitude * 0.5f;
                if (area < 0.004f)
                {
                    continue;   // too small to carry a mark
                }
                n /= area * 2f;
                Vector3 outward = turn * (lay * m.normals[i]);
                if (Vector3.Dot(n, outward) < 0f)
                {
                    n = -n;
                }
                if (n.y < -0.2f)
                {
                    continue;   // faces nobody sees
                }
                // A point inside the triangle, and a square mark on its plane round it.
                float r1 = Hashing.Unit(id, k, 43);
                float r2 = Hashing.Unit(id, k, 47);
                if (r1 + r2 > 1f)
                {
                    r1 = 1f - r1;
                    r2 = 1f - r2;
                }
                Vector3 p = a + (b - a) * r1 + (c - a) * r2 + n * 0.008f;
                float size = Mathf.Min(0.18f, Mathf.Sqrt(area) * 0.9f) * (0.8f + 0.4f * Hashing.Unit(id, k, 53));
                Vector3 world = new Vector3(center.x + p.x, baseHeight + Mathf.Max(0f, p.y), center.z + p.z);
                if (SectionLayer_IsoRimDamage.Mark(layer, world, n, size, id * 31 + k))
                {
                    placed++;
                }
            }
            return placed > 0;
        }

        // Whether triangle i of m (as Write places it, relative to the thing's centre) lies on a side of the cell where the
        // wall joins on to the next one: it faces that way and all its corners are on the cell's edge there.
        private static bool OnJoint(Model m, int i, Quaternion turn, Quaternion lay, Vector3 layLift, Vector3 scale, Vector3 offset, int joinedSides)
        {
            Vector3 n = turn * (lay * m.normals[i]);
            for (int d = 0; d < 4; d++)
            {
                if ((joinedSides & (1 << d)) == 0)
                {
                    continue;
                }
                Vector3 dir = GenAdj.CardinalDirections[d].ToVector3();
                if (Vector3.Dot(n, dir) < 0.9f)
                {
                    continue;
                }
                bool edge = true;
                for (int k = i; k < i + 3 && edge; k++)
                {
                    Vector3 p = turn * (Vector3.Scale(lay * m.positions[k] + layLift, scale) + offset);
                    edge = Vector3.Dot(p, dir) >= 0.5f - JointSlack;
                }
                if (edge)
                {
                    return true;
                }
            }
            return false;
        }

        private const float JointSlack = 0.02f;   // cells: how far inside the cell's edge a face still counts as lying on it

        // Writes an extra part next to a thing's model (a book on a bookcase's shelf): unscaled,
        // its origin at offset (model space, turned with the thing) and baseHeight cells up.
        public static bool WritePart(SectionLayer layer, Thing thing, Model m, Vector3 center, Quaternion turn, Vector3 offset, float baseHeight,
            Color tint, float outlineBold)
        {
            return Write(layer, thing, m, center, turn, Quaternion.identity, Vector3.zero, Vector3.one, offset, baseHeight, tint, false, outlineBold);
        }

        // Writes a model already posed in its own space (GroundGear: apparel lying on the ground): turned by turn, scale times
        // its size, its origin at center (x, z), baseHeight cells up.
        internal static bool WritePosed(SectionLayer layer, Thing thing, Model m, Vector3 center, Quaternion turn, float scale, float baseHeight,
            Color tint, float outlineBold)
        {
            return Write(layer, thing, m, center, turn, Quaternion.identity, Vector3.zero, Vector3.one * scale, Vector3.zero, baseHeight, tint, false, outlineBold);
        }

        // joinedSides (walls drawn from a model, WallBoxes.ModelWall): bits 1 << d of the sides (GenAdj.CardinalDirections
        // order) where the wall joins on to the next one: faces lying on such a side get no outline, so a run of wall shows
        // no line at each joint (as a run of blocks).
        private static bool Write(SectionLayer layer, Thing thing, Model m, Vector3 center, Quaternion turn, Quaternion lay, Vector3 layLift,
            Vector3 scale, Vector3 offset, float baseHeight, Color tint, bool bare, float outlineBold, int joinedSides = 0)
        {
            if (DamageMarks > 0)
            {
                Vector3 c0 = new Vector3(center.x, 0f, center.z);
                return WriteDamage(layer, thing, m, c0, turn, lay, layLift, scale, offset, baseHeight);
            }
            if (Ghost.a > 0f)
            {
                return WriteGhost(layer, m, center, turn, lay, layLift, scale, offset, baseHeight);
            }
            Material mat = R3DMaterials.Solid;
            LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
            int n = m.positions.Length;
            if (sm == null || sm.verts.Count + n > MeshWriter.MaxVerts)
            {
                return false;
            }
            IsoRimSettings st = IsoRimMod.Settings;

            LayerSubMesh outline = null;
            if (st.outlines && m.hull != null && outlineBold > 0f)
            {
                Material outlineMat = R3DMaterials.Outline;
                outline = outlineMat != null ? MeshWriter.Sub(layer, outlineMat) : null;
                if (outline != null && outline.verts.Count + n > MeshWriter.MaxVerts)
                {
                    outline = null;
                }
            }

            bool textured = st.texturedModels && m.materials != null && m.uvs != null && m.material != null;
            string swatch = textured ? R3DTextures.ModelSwatch(thing) : null;
            // (Api 24) "sheet" parts also take the thing's second colour (a bed's sheets by owner type: R3DTextures.SheetTint).
            Color sheetTint = R3DTextures.HasSheet(m) ? R3DTextures.SheetTint(thing) : Color.white;
            // Reused from one model to the next: thousands of plants are written in a row.
            // The shader lights each face by its own direction, so all plain faces share one piece
            // of mesh (see R3DMaterials.Lit) and textured faces one per material.
            int slots = textured ? m.materials.Length : 0;
            if (texturedPool.Length < slots)
            {
                texturedPool = new LayerSubMesh[slots * 2];
                lookedPool = new bool[slots * 2];
            }
            LayerSubMesh[] texturedMeshes = texturedPool;
            bool[] looked = lookedPool;
            for (int i = 0; i < slots; i++)
            {
                texturedMeshes[i] = null;
                looked[i] = false;
            }
            LayerSubMesh plain = null;

            float squash = View3D.Squash;
            float baseY = baseHeight / squash;

            // The outline is a set width on the map: a model drawn larger (a full-grown tree is
            // more than twice the size it was built) does not get a thicker line with it.
            float biggest = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            float hullScale = outlineBold * Mathf.Min(1f, 1f / Mathf.Max(0.01f, biggest));

            // (shader cut-away) a leafy tree keeps its leaves and its twigs: the vertex stage shows one or the other as
            // the tree is thinned out near the pointer.
            Plant treePlant = thing as Plant;
            bool treeTags = CutTags.Enabled && !bare && treePlant != null && treePlant.def.plant != null && treePlant.def.plant.IsTree;
            Vector3 leafTag = treeTags ? CutTags.Tag(thing.Position, ShaderCut.Leaf) : Vector3.zero;
            Vector3 twigTag = treeTags ? CutTags.Tag(thing.Position, ShaderCut.Twig) : Vector3.zero;

            for (int i = 0; i + 2 < n; i += 3)
            {
                int mi = m.material != null ? m.material[i] : 0;
                ModelMaterial mm = m.materials != null ? m.materials[mi] : null;
                bool tagged = treeTags && mm != null && (mm.leaf || mm.twig);
                if (mm != null && ((bare && mm.leaf) || (!bare && mm.twig && !tagged) || (NoFire && mm.fire) || (NoFlame && Effects.IsFlame(mm))))
                {
                    continue;   // no leaves on a bare tree; no hidden twigs on a leafy one; no flame once the fire is out (or where a live one burns)
                }
                Vector3 triTag = tagged ? (mm.leaf ? leafTag : twigTag) : Vector3.zero;
                bool joint = joinedSides != 0 && outline != null && OnJoint(m, i, turn, lay, layLift, scale, offset, joinedSides);

                bool glow = mm != null && mm.glow;

                // The plain copy: lit by direction, or the unlit material for something that glows.
                LayerSubMesh pm = sm;
                if (!glow)
                {
                    if (plain == null)
                    {
                        Material litMat = R3DMaterials.Lit(0);
                        plain = litMat != null ? MeshWriter.Sub(layer, litMat) : null;
                        if (plain == null || plain.verts.Count + n > MeshWriter.MaxVerts)
                        {
                            plain = sm;
                        }
                    }
                    pm = plain;
                }

                // Find (once per material) where this triangle's textured face goes.
                LayerSubMesh tm = null;
                if (textured && mm != null && mm.texture != null)
                {
                    int slot = mi;
                    if (!looked[slot])
                    {
                        looked[slot] = true;
                        Color c = mm.color * TextureLift;
                        if (mm.tinted)
                        {
                            c *= tint;
                        }
                        if (mm.sheet)
                        {
                            c *= sheetTint;
                        }
                        string path = mm.texture.Replace("{stuff}", swatch);
                        Material tmat = mm.runtimeTexture != null ? R3DMaterials.LitTexture(mm.runtimeTexture, mm.runtimeName, c, 1)
                            : glow ? R3DTextures.MaterialFor(path, c) : R3DMaterials.LitTexture(path, c);
                        texturedMeshes[slot] = tmat != null ? MeshWriter.Sub(layer, tmat) : null;
                    }
                    tm = texturedMeshes[slot];
                    if (tm != null && tm.verts.Count + 3 > MeshWriter.MaxVerts)
                    {
                        tm = null;
                    }
                }

                for (int k = i; k < i + 3; k++)
                {
                    Vector3 hull = m.hull != null ? m.hull[k] * hullScale : Vector3.zero;
                    Color c = m.colors[k];
                    if (m.tinted[k])
                    {
                        c *= tint;
                    }
                    if (mm != null && mm.sheet)
                    {
                        c *= sheetTint;
                    }

                    // 1. plain copy, pulled in a hair when a textured face will sit over it
                    Vector3 inside = tm != null ? m.positions[k] - hull * Inset : m.positions[k];
                    Vector3 p = turn * (Vector3.Scale(lay * inside + layLift, scale) + offset);
                    pm.tris.Add(pm.verts.Count);
                    pm.verts.Add(new Vector3(center.x + p.x, baseY + Mathf.Max(Floor, p.y) / squash, center.z + p.z));
                    pm.colors.Add(R3DMaterials.Shaded(c, 1f));
                    pm.uvs.Add(default);
                    if (tagged)
                    {
                        CutTags.Stamp(pm, triTag);
                    }

                    // 2. outline
                    if (outline != null && !joint && (m.noOutline == null || !m.noOutline[k]))
                    {
                        Vector3 h = turn * (Vector3.Scale(lay * (m.positions[k] + hull) + layLift, scale) + offset);
                        outline.tris.Add(outline.verts.Count);
                        outline.verts.Add(new Vector3(center.x + h.x, baseY + Mathf.Max(Floor, h.y) / squash, center.z + h.z));
                        outline.colors.Add(R3DMaterials.Shaded(c, OutlineShade));
                        outline.uvs.Add(default);
                        if (tagged)
                        {
                            CutTags.Stamp(outline, triTag);
                        }
                    }

                    // 3. textured face
                    if (tm != null)
                    {
                        Vector3 q = turn * (Vector3.Scale(lay * (m.positions[k] + m.normals[i] * FaceLift) + layLift, scale) + offset);
                        Vector2 uv = m.uvs[k];
                        tm.tris.Add(tm.verts.Count);
                        tm.verts.Add(new Vector3(center.x + q.x, baseY + Mathf.Max(Floor, q.y) / squash, center.z + q.z));
                        tm.colors.Add(White);
                        tm.uvs.Add(new Vector3(uv.x, uv.y, 0f));
                        if (tagged)
                        {
                            CutTags.Stamp(tm, triTag);
                        }
                    }
                }
            }
            if (m.beam != null)
            {
                Beams.Bake(layer, m.beam, center, turn, lay, layLift, scale, offset, baseY, Floor, squash);   // (Api 33) its beam part, as light
            }
            return true;
        }

        // Textured faces were drawn a little brighter than their plain colour on the lit side.
        public const float TextureLift = 1.08f;
    }

    // The shadow a 3D tree casts on the ground. (The game's own tree shadow is part of drawing
    // the tree's sprite, which the model replaces, and is only a sliver the width of the trunk.)
    //
    // The shape is written into the map mesh with the game's own shadow material. Each corner
    // carries how high above the ground it is, and the game slides it across the ground by
    // that height times the direction of the sun, so the shadow swings round and stretches
    // through the day and fades at night, in step with the shadows of walls and colonists.
    public static class TreeShadows
    {
        // How much the shadow thins out at the crown, on the game's scale of 0 (full) to 255 (gone).
        private const byte CrownFade = 64;

        // Used only if the game's shadow material is missing: a fixed slide (north-west, away from
        // IsoRim's mid-morning sun) and darkness.
        private static readonly Vector2 FixedCast = new Vector2(-1.6f, 2.2f);
        private const byte FixedAlpha = 70;

        private static LayerSubMesh sm;
        private static bool fixedCast;
        private static float groundY;
        private static float perCell;
        private static float baseHeight;
        private static Vector3 centre;
        private static Quaternion turn;
        private static float scale;
        private static Vector3 offset;

        private static readonly int[] post = new int[8];

        public static void Print(SectionLayer layer, Model m, Vector3 center, Quaternion turnBy, float scaleBy, Vector3 offsetBy, float restingHeight, bool bare)
        {
            ShadowShape shape = ShadowShape.For(m);
            if (shape == null)
            {
                return;
            }
            Material mat = R3DMaterials.GameShadow;
            fixedCast = false;
            if (mat == null)
            {
                mat = R3DMaterials.PlainShadow;
                fixedCast = true;
            }
            if (mat == null)
            {
                return;
            }
            bool crown = shape.crown != null && !bare;
            sm = MeshWriter.Sub(layer, mat);
            if (sm == null || sm.verts.Count + 8 + (crown ? shape.crown.Length : 0) > MeshWriter.MaxVerts)
            {
                return;
            }

            groundY = AltitudeLayer.Shadows.AltitudeFor();
            perCell = Mathf.Clamp(IsoRimMod.Settings.treeShadowLength, 0.02f, 1f);
            baseHeight = restingHeight;
            centre = center;
            turn = turnBy;
            scale = scaleBy;
            offset = offsetBy;

            // The trunk: a square post from the ground up to the crown (or to the top of the
            // bare branches). Its foot stays put and its top slides, so it shows as a bar.
            float w = shape.trunkHalf;
            float top = crown ? shape.crownHeight : shape.trunkTop;
            if (top > 0.01f)
            {
                for (int i = 0; i < 2; i++)
                {
                    float y = i == 0 ? 0f : top;
                    byte fade = i == 0 ? (byte)0 : CrownFade;
                    post[i * 4] = Corner(new Vector3(-w, y, -w), fade);
                    post[i * 4 + 1] = Corner(new Vector3(-w, y, w), fade);
                    post[i * 4 + 2] = Corner(new Vector3(w, y, w), fade);
                    post[i * 4 + 3] = Corner(new Vector3(w, y, -w), fade);
                }
                Quad(post[0], post[1], post[2], post[3]);
                if (!crown)
                {
                    Quad(post[4], post[5], post[6], post[7]);
                }
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) % 4;
                    // Which way a side faces once it has been slid depends on the sun, so both ways are written.
                    Quad(post[i], post[j], post[4 + j], post[4 + i]);
                    Quad(post[4 + i], post[4 + j], post[j], post[i]);
                }
            }

            // The crown: the outline of the leaves, all at one height, so it keeps its shape and slides as one piece.
            if (crown)
            {
                Vector2[] c = shape.crown;
                int first = sm.verts.Count;
                for (int i = 0; i < c.Length; i++)
                {
                    Corner(new Vector3(c[i].x, shape.crownHeight, c[i].y), CrownFade);
                }
                for (int i = 1; i + 1 < c.Length; i++)
                {
                    sm.tris.Add(first);
                    sm.tris.Add(first + i + 1);
                    sm.tris.Add(first + i);
                }
            }
            sm = null;
        }

        // Adds one corner, given where it is on the model, and returns its number.
        private static int Corner(Vector3 onModel, byte fade)
        {
            Vector3 p = turn * (onModel * scale + offset);
            float h = Mathf.Clamp01((baseHeight + Mathf.Max(0f, p.y)) * perCell);
            float x = centre.x + p.x;
            float z = centre.z + p.z;
            if (fixedCast)
            {
                x += h * FixedCast.x;
                z += h * FixedCast.y;
                sm.colors.Add(new Color32(0, 0, 0, FixedAlpha));
            }
            else if (h <= 0f)
            {
                sm.colors.Add(new Color32(0, 0, 0, 0));
            }
            else
            {
                // The game's own convention: red marks a raised corner (and thins the shadow there), alpha is its height.
                sm.colors.Add(new Color32(fade, 0, 0, (byte)(h * 255f)));
            }
            sm.verts.Add(new Vector3(x, groundY, z));
            return sm.verts.Count - 1;
        }

        private static void Quad(int a, int b, int c, int d)
        {
            sm.tris.Add(a);
            sm.tris.Add(b);
            sm.tris.Add(c);
            sm.tris.Add(a);
            sm.tris.Add(c);
            sm.tris.Add(d);
        }
    }

    // Soft shading on the ground under and around everything that stands on it: darkest at the
    // foot of the thing and fading to nothing a short way out. It is there day and night, like
    // the shading painted into the game's own sprites, and anchors the 3D shapes to the floor.
    // (The shadows that swing round with the sun are separate: see TreeShadows.)
    public static class GroundShadows
    {
        private static readonly List<Vector2> ring = new List<Vector2>();
        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);
        private static readonly IntVec3[] Sides = { IntVec3.South, IntVec3.North, IntVec3.West, IntVec3.East };

        private static Mesh blob;

        public static bool On
        {
            get { return IsoRimMod.Settings.groundShadows; }
        }

        private static float Height
        {
            get { return AltitudeLayer.Shadows.AltitudeFor(); }
        }

        private static LayerSubMesh MeshFor(SectionLayer layer, int room)
        {
            Material mat = R3DMaterials.GroundShadow;
            if (mat == null)
            {
                return null;
            }
            LayerSubMesh sm = MeshWriter.Sub(layer, mat);
            return sm != null && sm.verts.Count + room <= MeshWriter.MaxVerts ? sm : null;
        }

        private static Color32 Dark(float amount)
        {
            return new Color32(0, 0, 0, (byte)(Mathf.Clamp01(amount) * 255f));
        }

        // points: the corners of an outline with no dents, on the ground, in order.
        // Filled at the given darkness, with a fringe fading out over reach cells.
        public static void Soft(SectionLayer layer, List<Vector2> points, float dark, float reach)
        {
            int n = points.Count;
            if (n < 3)
            {
                return;
            }
            LayerSubMesh sm = MeshFor(layer, n * 2);
            if (sm == null)
            {
                return;
            }
            Vector2 mid = Vector2.zero;
            for (int i = 0; i < n; i++)
            {
                mid += points[i];
            }
            mid /= n;

            float y = Height;
            Color32 inner = Dark(dark);
            int first = sm.verts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = points[i];
                Vector2 away = p - mid;
                float len = away.magnitude;
                Vector2 q = len > 1e-4f ? p + away * (reach / len) : p;
                sm.verts.Add(new Vector3(p.x, y, p.y));
                sm.colors.Add(inner);
                sm.uvs.Add(Vector3.zero);
                sm.verts.Add(new Vector3(q.x, y, q.y));
                sm.colors.Add(Clear);
                sm.uvs.Add(Vector3.zero);
            }
            for (int i = 1; i + 1 < n; i++)
            {
                sm.tris.Add(first);
                sm.tris.Add(first + 2 * i);
                sm.tris.Add(first + 2 * (i + 1));
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int a = first + 2 * i;
                int b = first + 2 * j;
                sm.tris.Add(a);
                sm.tris.Add(a + 1);
                sm.tris.Add(b + 1);
                sm.tris.Add(a);
                sm.tris.Add(b + 1);
                sm.tris.Add(b);
            }
        }

        // Under a hand-made model: the outline of the ground it covers. Under a tree: the
        // outline of its crown, drawn in a little and lighter, or just its trunk when bare.
        public static void UnderModel(SectionLayer layer, Model m, Vector3 center, Quaternion turn, Vector3 scale, Vector3 offset, bool laid, bool tree, bool bare)
        {
            Vector2[] outline;
            float shrink = 1f;
            float dark = 0.30f;
            float reach = Mathf.Clamp(0.07f + 0.10f * m.maxY * scale.y, 0.08f, 0.22f);
            if (tree)
            {
                ShadowShape shape = ShadowShape.For(m);
                outline = shape != null && !bare ? shape.crown : null;
                if (outline == null)
                {
                    float w = shape != null ? shape.trunkHalf * 1.4f : 0.08f;
                    ring.Clear();
                    AddPoint(center, turn, scale, offset, -w, -w);
                    AddPoint(center, turn, scale, offset, -w, w);
                    AddPoint(center, turn, scale, offset, w, w);
                    AddPoint(center, turn, scale, offset, w, -w);
                    Soft(layer, ring, 0.28f, 0.16f);
                    return;
                }
                shrink = 0.72f;
                dark = 0.22f;
                reach = 0.30f * Mathf.Min(1.5f, scale.x);
            }
            else
            {
                outline = ShadowShape.Footprint(m, laid);
            }
            if (outline == null)
            {
                return;
            }
            ring.Clear();
            for (int i = 0; i < outline.Length; i++)
            {
                AddPoint(center, turn, scale, offset, outline[i].x * shrink, outline[i].y * shrink);
            }
            Soft(layer, ring, dark, reach);
        }

        private static void AddPoint(Vector3 center, Quaternion turn, Vector3 scale, Vector3 offset, float x, float z)
        {
            Vector3 p = turn * (Vector3.Scale(new Vector3(x, 0f, z), scale) + offset);
            ring.Add(new Vector2(center.x + p.x, center.z + p.z));
        }

        // Under a thing shaped from its own sprite: the box round the solid part of the sprite.
        // corner is the sprite's bottom left corner, across and up its two edges; lids are the
        // solid parts as fractions of those edges.
        public static void UnderSprite(SectionLayer layer, Vector3 corner, Vector2 across, Vector2 up, List<Vector4> lids, float tall)
        {
            float a0 = 0f;
            float a1 = 1f;
            float b0 = 0f;
            float b1 = 1f;
            if (lids != null && lids.Count > 0)
            {
                a0 = 1f;
                a1 = 0f;
                b0 = 1f;
                b1 = 0f;
                for (int i = 0; i < lids.Count; i++)
                {
                    Vector4 l = lids[i];
                    a0 = Mathf.Min(a0, l.x);
                    b0 = Mathf.Min(b0, l.y);
                    a1 = Mathf.Max(a1, l.z);
                    b1 = Mathf.Max(b1, l.w);
                }
                if (a1 <= a0 || b1 <= b0)
                {
                    return;
                }
            }
            Vector2 o = new Vector2(corner.x, corner.z);
            ring.Clear();
            ring.Add(o + across * a0 + up * b0);
            ring.Add(o + across * a0 + up * b1);
            ring.Add(o + across * a1 + up * b1);
            ring.Add(o + across * a1 + up * b0);
            Soft(layer, ring, 0.28f, Mathf.Clamp(tall * 0.45f, 0.05f, 0.20f));
        }

        // Along the foot of a wall or rock face: a band on every side that looks onto open ground,
        // with the corners filled in.
        public static void AroundBlock(SectionLayer layer, Thing t)
        {
            Map map = t.Map;
            if (!On || map == null)
            {
                return;
            }
            LayerSubMesh sm = MeshFor(layer, 32);
            if (sm == null)
            {
                return;
            }
            const float Reach = 0.30f;
            IntVec3 c = t.Position;
            float y = Height;
            Color32 inner = Dark(0.32f);
            Vector3 centre = new Vector3(c.x + 0.5f, y, c.z + 0.5f);

            bool south = Open(map, c, c + IntVec3.South);
            bool north = Open(map, c, c + IntVec3.North);
            bool west = Open(map, c, c + IntVec3.West);
            bool east = Open(map, c, c + IntVec3.East);

            // PROTOTYPE (CliffLook.cs): natural rock's contact shadow follows its chamfered outline.
            Vector3[] g;
            bool[] moved;
            if (WallBoxes.RockGroundCorners(t, out g, out moved))
            {
                for (int f = 0; f < 4; f++)
                {
                    bool open = f == 0 ? south : (f == 1 ? north : (f == 2 ? west : east));
                    if (!open)
                    {
                        continue;
                    }
                    Vector3 a = g[WallBoxes.FaceLeftCorner(f)], b = g[WallBoxes.FaceRightCornerOf(f)];
                    Vector3 run = b - a;
                    if (run.sqrMagnitude < 1e-4f)
                    {
                        continue;
                    }
                    run.Normalize();
                    Vector3 outward = new Vector3(run.z, 0f, -run.x);
                    a.y = y;
                    b.y = y;
                    MeshWriter.Quad(sm, a, a + outward * Reach, b + outward * Reach, b, inner, Clear, Clear, inner);
                }
                return;
            }

            for (int f = 0; f < 4; f++)
            {
                bool open = f == 0 ? south : (f == 1 ? north : (f == 2 ? west : east));
                if (!open)
                {
                    continue;
                }
                Vector3 outward = Sides[f].ToVector3();
                Vector3 along = new Vector3(outward.z, 0f, -outward.x);
                Vector3 p0 = centre + outward * 0.5f - along * 0.5f;
                Vector3 p1 = p0 + along;
                MeshWriter.Quad(sm, p0, p0 + outward * Reach, p1 + outward * Reach, p1, inner, Clear, Clear, inner);
            }
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    if (!(sx < 0 ? west : east) || !(sz < 0 ? south : north))
                    {
                        continue;
                    }
                    Vector3 k = centre + new Vector3(sx * 0.5f, 0f, sz * 0.5f);
                    MeshWriter.Quad(sm, k, k + new Vector3(sx * Reach, 0f, 0f), k + new Vector3(sx * Reach * 0.7f, 0f, sz * Reach * 0.7f),
                        k + new Vector3(0f, 0f, sz * Reach), inner, Clear, Clear, Clear);
                }
            }
        }

        // Whether the ground in cell c, beside the block in cell from, is open (a gravship hull corner
        // piece standing against that block covers it, see HullCorners) and is there at all (stacked floors: an upper
        // floor's open air has no ground for the band to lie on, Floors.HasFloor).
        private static bool Open(Map map, IntVec3 from, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            Building b = c.GetEdifice(map);
            return (b == null || (!(b is Building_Door) && !WallBoxes.IsWallForm(Forms.For(b.def)))) && !HullCorners.Covers(map, c, from, 0f)
                && Floors.HasFloor(map, c);   // (stacked floors) no band over the open air beside a wall upstairs
        }

        // Under something that moves (an animal): a soft oval, drawn afresh every frame.
        // halfWidth and halfLength are in cells; yaw turns its long side to face the same way.
        public static void Blob(Vector3 position, float yaw, float halfWidth, float halfLength)
        {
            if (FigureScope.Active && !FigureScope.Blob(ref position, ref yaw))
            {
                return;   // a figure drawn for another mod without its shadow (Api.DrawFigure)
            }
            Material mat = R3DMaterials.GroundShadow;
            if (!On || mat == null)
            {
                return;
            }
            if (blob == null)
            {
                const int N = 14;
                List<Vector3> verts = new List<Vector3>();
                List<Color32> colors = new List<Color32>();
                List<int> tris = new List<int>();
                Color32 inner = Dark(0.30f);
                for (int i = 0; i < N; i++)
                {
                    float a = 2f * Mathf.PI * i / N;
                    Vector3 d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    verts.Add(d * 0.55f);
                    colors.Add(inner);
                    verts.Add(d);
                    colors.Add(Clear);
                }
                for (int i = 1; i + 1 < N; i++)
                {
                    tris.Add(0);
                    tris.Add(2 * i);
                    tris.Add(2 * (i + 1));
                }
                for (int i = 0; i < N; i++)
                {
                    int a = 2 * i;
                    int b = 2 * ((i + 1) % N);
                    tris.Add(a);
                    tris.Add(a + 1);
                    tris.Add(b + 1);
                    tris.Add(a);
                    tris.Add(b + 1);
                    tris.Add(b);
                }
                blob = new Mesh();
                blob.name = "IsoRim_ShadowBlob";
                blob.SetVertices(verts);
                blob.SetColors(colors);
                blob.SetTriangles(tris, 0);
                blob.RecalculateBounds();
                MeshGuard.Check(blob, "blob");
            }
            Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(position.x, Height, position.z), Quaternion.AngleAxis(yaw, Vector3.up),
                new Vector3(Mathf.Max(0.05f, halfWidth), 1f, Mathf.Max(0.05f, halfLength)));
            Graphics.DrawMesh(blob, matrix, mat, 0);
        }
    }
}
