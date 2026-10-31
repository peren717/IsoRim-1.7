using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Apparel on layers IsoRim does not know (ApparelLayerDefs added by mods), read by People.Dress. An item whose layers
    // are all unknown is given a figure slot from its body part groups and its layers' draw order against the game's own
    // layers, and is then drawn the way IsoRim draws a game item in that slot: its binding (ApparelModelDef) if it has one,
    // else IsoRim's name rules and default shapes as for that vanilla layer, else IsoRim's stand-in for the slot.
    //   head (FullHead, UpperHead)          headgear, as any hat (by name; generic hat or helmet)
    //   face (Teeth, Mouth; Eyes too)       face wear: its own model by name, else the gas mask (with Eyes) or cloth mask
    //   eyes (Eyes only)                    eye wear: its own model by name, else the blindfold band
    //   neck (Neck only)                    neck wear: its own model by name, else the collar
    //   pack (worn picture drawn as a pack) belt gear on the back (mount pack), stand-in pack
    //   belt (Waist only)                   belt gear round the waist (mount belt), stand-in belt
    //   body (the rest: torso, legs ...)    clothing as the vanilla layer nearest its draw order: OnSkin, Middle or Shell
    //                                       (Shell also for every order above it); drawn after the clothes on the game's
    //                                       own layers, so it never takes a coat's place from one of them
    // Left alone (as before): items with a layer the game knows (also mixed ones), items whose class draws its own worn
    // picture (DrawWornExtras overridden: shields) and items drawn through render nodes of another mod's classes.
    // Bindings always win (Dress reads them first); a bound item is never moved in the order.
    internal static class CustomLayers
    {
        internal enum Kind { Head, Face, Eyes, Neck, Pack, Belt, Body }

        internal sealed class Fit
        {
            public Kind kind;
            public string layer;      // Body: the vanilla layer it is drawn as (OnSkin, Middle, Shell); else null
            public int drawOrder;     // its outermost layer's
        }

        // While GroundGear dresses a laid item: no stand-in models (the laid item keeps the game's picture instead).
        [ThreadStatic] internal static bool Laying;

        // Harness only (probe ap9cloff): everything as before CustomLayers, for before/after renders.
        internal static bool Off;

        private static readonly Dictionary<ThingDef, Fit> fits = new Dictionary<ThingDef, Fit>();

        private static bool IsVanilla(ApparelLayerDef l)
        {
            if (l == null)
            {
                return true;
            }
            switch (l.defName)
            {
                case "OnSkin":
                case "Middle":
                case "Shell":
                case "Belt":
                case "Overhead":
                case "EyeCover":
                    return true;
            }
            return false;
        }

        // The slot of an apparel whose layers are all unknown, or null (a known layer, or it draws itself).
        internal static Fit FitOf(ThingDef def)
        {
            ApparelProperties props = def != null ? def.apparel : null;
            if (Off || props == null || props.layers == null || props.layers.Count == 0)
            {
                return null;
            }
            Fit fit;
            lock (fits)
            {
                if (fits.TryGetValue(def, out fit))
                {
                    return fit;
                }
            }
            fit = Work(def, props);
            lock (fits)
            {
                fits[def] = fit;
            }
            return fit;
        }

        private static Fit Work(ThingDef def, ApparelProperties props)
        {
            int order = int.MinValue;
            for (int i = 0; i < props.layers.Count; i++)
            {
                if (IsVanilla(props.layers[i]))
                {
                    return null;
                }
                order = Math.Max(order, props.layers[i].drawOrder);
            }
            if (DrawsOwn(def) || ForeignNodes(props))
            {
                return null;
            }
            bool head = People.Covers(props, "FullHead") || People.Covers(props, "UpperHead");
            bool torso = People.Covers(props, "Torso");
            bool legs = People.Covers(props, "Legs");
            bool body = torso || legs;
            Fit fit = new Fit { drawOrder = order };
            if (head && !body)
            {
                fit.kind = Kind.Head;
            }
            else if (!body && (People.Covers(props, "Teeth") || People.Covers(props, "Mouth")))
            {
                fit.kind = Kind.Face;
            }
            else if (!body && People.Covers(props, "Eyes"))
            {
                fit.kind = Kind.Eyes;
            }
            else if (!body && People.Covers(props, "Neck"))
            {
                fit.kind = Kind.Neck;
            }
            else if (props.wornGraphicData != null && props.wornGraphicData.renderUtilityAsPack)
            {
                fit.kind = Kind.Pack;
            }
            else if (!body && People.Covers(props, "Waist"))
            {
                fit.kind = Kind.Belt;
            }
            else
            {
                fit.kind = Kind.Body;
                fit.layer = order < Between(ApparelLayerDefOf.OnSkin, ApparelLayerDefOf.Middle, 50) ? "OnSkin"
                    : (order < Between(ApparelLayerDefOf.Middle, ApparelLayerDefOf.Shell, 150) ? "Middle" : "Shell");
            }
            return fit;
        }

        // Half way between two layers' draw orders (or the given default when a layer is missing).
        private static int Between(ApparelLayerDef a, ApparelLayerDef b, int fallback)
        {
            return a != null && b != null ? (a.drawOrder + b.drawOrder) / 2 : fallback;
        }

        // Its class draws its own worn picture (as ApparelModels.Binding.drawsOwn).
        private static bool DrawsOwn(ThingDef def)
        {
            Type cls = def.thingClass ?? typeof(Apparel);
            System.Reflection.MethodInfo extras = AccessTools.Method(cls, nameof(Apparel.DrawWornExtras));
            return extras != null && extras.DeclaringType != typeof(Apparel) && extras.DeclaringType != typeof(ThingWithComps) && extras.DeclaringType != typeof(Thing);
        }

        // It is drawn through render nodes of another mod's classes (its own drawing).
        private static bool ForeignNodes(ApparelProperties props)
        {
            if (props.RenderNodeProperties == null)
            {
                return false;
            }
            System.Reflection.Assembly game = typeof(PawnRenderNode).Assembly;
            for (int i = 0; i < props.RenderNodeProperties.Count; i++)
            {
                PawnRenderNodeProperties n = props.RenderNodeProperties[i];
                if (n != null && (n.GetType().Assembly != game || (n.nodeClass != null && n.nodeClass.Assembly != game)
                    || (n.workerClass != null && n.workerClass.Assembly != game)))
                {
                    return true;
                }
            }
            return false;
        }

        // The worn items in the order Dress takes them: as worn, with the unbound items on unknown layers last (so they
        // never take a slot from an item on one of the game's layers). The list itself when there are none.
        internal static List<Apparel> Ordered(List<Apparel> worn)
        {
            if (worn == null)
            {
                return null;
            }
            List<Apparel> late = null;
            for (int i = 0; i < worn.Count; i++)
            {
                if (Late(worn[i]))
                {
                    (late ?? (late = new List<Apparel>())).Add(worn[i]);
                }
            }
            if (late == null)
            {
                return worn;
            }
            List<Apparel> order = new List<Apparel>(worn.Count);
            for (int i = 0; i < worn.Count; i++)
            {
                if (!Late(worn[i]))
                {
                    order.Add(worn[i]);
                }
            }
            order.AddRange(late);
            return order;
        }

        private static bool Late(Apparel a)
        {
            return a != null && a.def != null && FitOf(a.def) != null && ApparelModels.For(a.def) == null && WornGear.For(a.def) == null;
        }

        // ---- stand-ins

        private static WornGearDef packDef, beltDef;

        // The stand-in gear for a pack or belt (WornGear, IsoRim's own models), or null.
        internal static WornGearDef GearFor(Fit fit)
        {
            if (fit.kind == Kind.Pack)
            {
                return packDef ?? (packDef = new WornGearDef { defName = "R3DGear_StandInPack", model = "Models/person_pack_control.obj", mount = "pack", tintMaterials = new List<string> { "utility_shell" } });
            }
            if (fit.kind == Kind.Belt)
            {
                return beltDef ?? (beltDef = new WornGearDef { defName = "R3DGear_StandInBelt", model = "Models/person_belt_smoke.obj", mount = "belt", tintMaterials = new List<string> { "utility_shell" } });
            }
            return null;
        }

        // The stand-in face, eye or neck wear (IsoRim's own part file, person_hat_<file>), or null.
        internal static string FaceFile(Fit fit, ApparelProperties props)
        {
            switch (fit.kind)
            {
                case Kind.Face: return People.Covers(props, "Eyes") ? "gas_mask" : "cloth_mask";
                case Kind.Eyes: return "blindfold";
                case Kind.Neck: return "collar";
            }
            return null;
        }

        // For the harness: how an apparel def is taken (its slot, the layer it is drawn as, its stand-in), or why it is left alone.
        internal static string Describe(ThingDef def)
        {
            ApparelProperties props = def.apparel;
            if (props == null || props.layers.NullOrEmpty())
            {
                return "no layers";
            }
            bool allUnknown = true;
            for (int i = 0; i < props.layers.Count; i++)
            {
                allUnknown &= !IsVanilla(props.layers[i]);
            }
            if (!allUnknown)
            {
                return "known layer";
            }
            if (DrawsOwn(def))
            {
                return "draws own (DrawWornExtras)";
            }
            if (ForeignNodes(props))
            {
                return "own render nodes";
            }
            Fit fit = FitOf(def);
            if (fit == null)
            {
                return "off";
            }
            string s =fit.kind.ToString().ToLowerInvariant() + (fit.layer != null ? " as " + fit.layer : "");
            if (ApparelModels.For(def) != null)
            {
                return s + ", bound " + ApparelModels.For(def).def.slot;
            }
            if (WornGear.For(def) != null)
            {
                return s + ", worn gear " + WornGear.For(def).defName;
            }
            WornGearDef g = GearFor(fit);
            string face = FaceFile(fit, props);
            return s + (g != null ? ", stand-in " + g.model : (face != null ? ", stand-in person_hat_" + face + " unless own by name" : ""));
        }
    }
}
