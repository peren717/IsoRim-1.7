using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Items the game draws every frame (drawerType RealtimeOnly) instead of printing them into the map mesh: books (vanilla's
    // BookBase gives no drawerType, and RealtimeOnly is the default), and any mod item left the same way. Items in the map
    // mesh are made solid from their sprite or drawn as their model (Forms, ModelBaker); these were left as the game's flat
    // sprite, so a book lay flat on the ground while a carried one (Carried) was solid. Now, on the ground (or a shelf or
    // table), they take the same form as any other item (Forms.Compute) and are drawn so here, every frame, from the
    // Thing.DrawAt prefix (Patches.ThingDrawAt_Prefix).
    //   Only items whose class draws with the plain item drawing (Thing / ThingWithComps DrawAt, or Book's, which uses it
    //   while closed): corpses, minified things and other items that draw themselves keep the game's drawing.
    public static class LiveItems
    {
        private static readonly Dictionary<ThingDef, bool> isLive = new Dictionary<ThingDef, bool>();

        // An item of this def is drawn every frame with the plain item drawing.
        public static bool Is(ThingDef def)
        {
            if (def == null || def.category != ThingCategory.Item || def.drawerType != DrawerType.RealtimeOnly || def.IsCorpse)
            {
                return false;
            }
            bool live;
            lock (isLive)
            {
                if (isLive.TryGetValue(def, out live))
                {
                    return live;
                }
            }
            live = PlainDrawing(def.thingClass);
            lock (isLive)
            {
                isLive[def] = live;
            }
            return live;
        }

        private static bool PlainDrawing(Type cls)
        {
            if (cls == null || typeof(MinifiedThing).IsAssignableFrom(cls) || typeof(Pawn).IsAssignableFrom(cls) || typeof(Corpse).IsAssignableFrom(cls))
            {
                return false;
            }
            MethodInfo draw = AccessTools.Method(cls, "DrawAt", new[] { typeof(Vector3), typeof(bool) });
            Type by = draw != null ? draw.DeclaringType : null;
            return by == typeof(Thing) || by == typeof(ThingWithComps) || by == typeof(Book);
        }

        // Draws the item solid (or as its model) where it lies; false: the game's own drawing (3D off, not on the map,
        // an open book, a form with nothing to draw).
        public static bool Draw(Thing t)
        {
            if (!View3D.Active || t == null || !t.Spawned || !Is(t.def))
            {
                return false;
            }
            if (t is Book book && book.IsOpen)
            {
                return false;
            }
            Vector3 at = t.DrawPos;
            return ItemDraw.Draw(t, new Vector3(at.x, Forms.BaseUnder(t, true), at.z), 0f, 1f, false);
        }
    }

    // (Api 40) One item drawn this frame as IsoRim shows an item lying on the ground, by the player's item settings (Forms: the
    // item's model, its sprite made solid, or flat): for items the game draws every frame (LiveItems), and for items another
    // mod draws itself somewhere of its own (Api.DrawItem: a belt carrying them). The same placing as the map-mesh bake
    // (ModelBaker.StartPlace / Place / BakeModel), only at the given point instead of the item's cell.
    internal static class ItemDraw
    {
        public static int Drawn;   // probe: items drawn here

        // bottom: true space (x, z map cells; y the height of the item's underside over its floor, cells). yaw: degrees added to
        // the item's own turn. scale: of its drawn size. flat: with no 3D form (the player's settings say flat, or a form with
        // nothing to show), the game's own sprite of it is drawn lying at that height (else false, nothing drawn).
        public static bool Draw(Thing t, Vector3 bottom, float yaw, float scale, bool flat)
        {
            ThingForm form = Forms.For(t.def);
            bool done = false;
            switch (form.form)
            {
                case Form3D.Model:
                    done = form.model != null && form.model.HasModel && DrawModel(t, form.model, bottom, yaw, scale);
                    break;
                case Form3D.Extruded:
                    done = form.height > 0.004f && SpriteSolids.DrawLying(t, bottom, form.height, scale, yaw + GroundGear.Turn(t));
                    break;
                case Form3D.Worn:
                    // Clothes laid down as worn (GroundGear), as the bake writes them; without a laid shape, as the bake does
                    // then: its sprite made solid.
                    done = GroundGear.DrawLaid(t, bottom, yaw, scale)
                        || SpriteSolids.DrawLying(t, bottom, Forms.ItemThickness(t.def), scale, yaw + GroundGear.Turn(t));
                    break;
            }
            if (!done && flat)
            {
                done = DrawFlat(t, bottom, yaw, scale);
            }
            if (done)
            {
                Drawn++;
            }
            return done;
        }

        // The item's model placed as ModelBaker bakes it (variant and stack model, state, turn, fit, offset, lay-flat, tint and
        // surface), its middle at bottom's x and z.
        private static bool DrawModel(Thing t, ModelEntry entry, Vector3 bottom, float yaw, float scale)
        {
            ModelDef def = entry.def;
            Model m = entry.Pick(t);
            m = m != null ? StateModels.Choose(t, entry, m) : null;
            if (m == null)
            {
                return false;
            }
            float s, turn;
            ModelBaker.StartPlace(t, entry, out s, out turn);
            float baseHeight = 0f;
            Vector3 center, fit;
            bool mounted;
            ModelBaker.Place(t, entry, m, s * scale, ref turn, ref baseHeight, out center, out fit, out mounted);
            Vector3 home = t.DrawPos;   // Place's centre is the item's own place (plus any scatter): keep only the scatter
            Vector3 at = new Vector3(bottom.x + center.x - home.x, bottom.y + baseHeight, bottom.z + center.z - home.z);
            Matrix4x4 local = Matrix4x4.Translate(def.offset) * Matrix4x4.Scale(fit);
            if (def.layFlat)
            {
                local = local * Matrix4x4.Translate(new Vector3(0f, m.halfDepth, 0f)) * Matrix4x4.Rotate(Quaternion.AngleAxis(90f, Vector3.right));
            }
            DynamicModels.DrawLocal(m, at, turn + yaw, local, def.spriteTint ? SpriteColors.MeanFor(t) : R3DTextures.ModelTint(t), R3DTextures.ModelSwatch(t));
            return true;
        }

        // The game's flat sprite of the item (its Graphic, as Thing.DrawAt draws it: stack sprite, random turn), lying at the
        // height, turned by yaw and scaled about its middle; moved onto the stacked floor of its row when no thing's drawing
        // has put one on already.
        private static bool DrawFlat(Thing t, Vector3 bottom, float yaw, float scale)
        {
            Graphic g = t.Graphic;
            if (g == null)
            {
                return false;
            }
            Vector3 at = new Vector3(bottom.x, AltitudeLayer.Item.AltitudeFor(), bottom.z);
            Matrix4x4 m = View3D.LiftMatrix(bottom.y);
            if (yaw != 0f || scale != 1f)
            {
                m = m * Matrix4x4.Translate(at) * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                    * Matrix4x4.Scale(new Vector3(scale, 1f, scale)) * Matrix4x4.Translate(-at);
            }
            if (!DrawHook.floor)
            {
                Matrix4x4 here = Matrix4x4.Translate(new Vector3(at.x, 0f, at.z));
                Matrix4x4 there = here;
                if (!Floors.Drawn(ref there))
                {
                    return true;   // on a floor the viewer has hidden: nothing shows
                }
                m = there * here.inverse * m;
            }
            using (UprightScopes.BeginTransform(m))
            {
                g.Draw(at, t.Rotation, t);
            }
            return true;
        }
    }
}
