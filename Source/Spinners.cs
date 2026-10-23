using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // A part of a model that turns about the thing's upright axis (the lightball's ball): the
    // entry's spinModel, drawn every frame over the baked body. It turns while the thing is
    // powered (and, for things the game switches on only when wanted, such as the lightball
    // during a party, while it wants to be on) and stands still otherwise, and with the game's
    // speed. The flat mote the game draws for the turning part (spinMote) is left out in 3D.
    // While it turns, its litMaterial faces (the lightball's lenses) light up (spinLit).
    public static class Spinners
    {
        private static List<ThingDef> defs;
        private static readonly HashSet<ThingDef> hiddenMotes = new HashSet<ThingDef>();
        private static readonly Dictionary<int, float> angles = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> lastTick = new Dictionary<int, int>();

        private static void Init()
        {
            defs = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ModelEntry entry;
                if (ModelLibrary.TryGet(def, out entry) && entry.spin != null && entry.HasModel)
                {
                    defs.Add(def);
                    ThingDef mote = entry.def.spinMote.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(entry.def.spinMote);
                    if (mote != null)
                    {
                        hiddenMotes.Add(mote);
                    }
                }
            }
        }

        // Whether the thing's part is turning now.
        public static bool Turning(Thing t)
        {
            CompPowerTrader power = t.TryGetComp<CompPowerTrader>();
            if (power == null || !power.PowerOn)
            {
                return false;
            }
            ThingWithComps twc = t as ThingWithComps;
            CompAutoPowered auto = twc != null ? twc.GetComp<CompAutoPowered>() : null;
            return auto == null || auto.WantsToBeOn;
        }

        // Called every frame while the 3D view is on.
        public static void DrawAll()
        {
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.models3D || map == null || Find.TickManager == null)
            {
                return;
            }
            try
            {
                if (defs == null)
                {
                    Init();
                }
                if (defs.Count == 0)
                {
                    return;
                }
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
                for (int d = 0; d < defs.Count; d++)
                {
                    ThingForm form = Forms.For(defs[d]);
                    if (form.form != Form3D.Model || form.model == null || form.model.spin == null)
                    {
                        continue;
                    }
                    List<Thing> things = map.listerThings.ThingsOfDef(defs[d]);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (view.Contains(t.Position) && !t.Fogged())
                        {
                            Draw(t, form.model);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing turning parts failed: " + e, 93611241);
            }
        }

        private static void Draw(Thing t, ModelEntry entry)
        {
            ModelDef def = entry.def;
            // Turned by game ticks, so it keeps pace with the game speed and stands still while paused.
            int now = Find.TickManager.TicksGame;
            int was;
            float angle;
            angles.TryGetValue(t.thingIDNumber, out angle);
            if (!lastTick.TryGetValue(t.thingIDNumber, out was))
            {
                was = now;
            }
            lastTick[t.thingIDNumber] = now;
            bool on = Turning(t);
            if (now > was && on)
            {
                angle = Mathf.Repeat(angle + Mathf.Min(now - was, 60) / 60f * def.spinSpeed, 360f);
                angles[t.thingIDNumber] = angle;
            }
            float yaw = entry.Yaw(t.Rotation);
            if (entry.Swapped(t.def))
            {
                yaw += 90f;
            }
            Vector3 at = t.TrueCenter();
            Vector3 offset = Quaternion.AngleAxis(yaw, Vector3.up) * def.offset;
            // Its lit copy (the lightball's lenses) while it is on, also while the game is paused.
            DynamicModels.Draw(ObjLoader.SheetTinted(on && entry.spinLit != null ? entry.spinLit : entry.spin, R3DTextures.SheetTint(t)), new Vector3(at.x + offset.x, offset.y, at.z + offset.z), yaw + angle,
                entry.Fit(t.def) * def.scale, R3DTextures.ModelTint(t), R3DTextures.ModelSwatch(t));
        }

        // Mote.DrawAt prefix: the game's flat stand-in for a part that turns in 3D is not drawn.
        public static bool MoteDrawAt_Prefix(Mote __instance)
        {
            return defs == null || hiddenMotes.Count == 0 || !View3D.Ready || !IsoRimMod.Settings.models3D
                || __instance == null || !hiddenMotes.Contains(__instance.def);
        }

        // For the harness: the turning state and angle of a thing's part.
        public static string Report(Thing t)
        {
            float angle;
            angles.TryGetValue(t.thingIDNumber, out angle);
            ModelEntry entry;
            bool lit = Turning(t) && ModelLibrary.TryGet(t.def, out entry) && entry.spinLit != null;
            return (Turning(t) ? "turning" : "still") + " angle " + angle.ToString("0.0") + (lit ? " lit" : " dark");
        }
    }
}
