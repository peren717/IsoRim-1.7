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
            ThingForm form = Forms.For(t.def);
            Vector3 at = t.DrawPos;
            Vector3 bottom = new Vector3(at.x, Forms.BaseUnder(t, true), at.z);
            if (form.form == Form3D.Model && form.model != null && form.model.HasModel)
            {
                Model m = form.model.Pick(t);
                DynamicModels.Draw(m, bottom, form.model.Yaw(t.Rotation) + form.model.def.rotation, Vector3.one * form.model.def.scale,
                    form.model.def.spriteTint ? SpriteColors.MeanFor(t) : t.DrawColor, R3DTextures.StuffSwatch(t));
                return true;
            }
            if (form.form == Form3D.Extruded && form.height > 0.004f)
            {
                return SpriteSolids.DrawLying(t, bottom, form.height, 1f);
            }
            return false;
        }
    }
}
