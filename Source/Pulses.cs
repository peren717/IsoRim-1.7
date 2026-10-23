using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Parts of a model that beat (the fleshmass heart): the entry's pulseParts, drawn every frame over the baked body,
    // each at its pivot and scaled about it as the game scales its flat parts (Building_FleshmassHeart.GetBeatScale):
    // lerp(1, maxScale, sin(pi * f)) with f = (ticks - (lastBeatTick + delay)) / 15 while 0 <= f <= 1, else 1; the
    // height follows the mean of the two. By game ticks, so it keeps pace with the game speed and stands still while
    // paused. The game's own flat parts (drawn in Building_FleshmassHeart.DrawAt after the body) are left out while the
    // model stands in; with the 3D view or models off the game draws everything as usual.
    public static class Pulses
    {
        private const float BeatTicks = 15f;   // Building_FleshmassHeart.BeatDurationTicks

        private static List<ThingDef> defs;
        private static Action<ThingWithComps, Vector3, bool> baseDrawAt;
        private static readonly AccessTools.FieldRef<Building_FleshmassHeart, int> lastBeatTick =
            AccessTools.FieldRefAccess<Building_FleshmassHeart, int>("lastBeatTick");

        private static void Init()
        {
            defs = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ModelEntry entry;
                if (ModelLibrary.TryGet(def, out entry) && entry.pulse != null && entry.HasModel)
                {
                    defs.Add(def);
                }
            }
        }

        // True while the beating parts are drawn for this thing: models on, the 3D view up, a model with pulse parts; a
        // body drawn every frame (RealtimeOnly) only once its baked model stands in.
        private static bool StandsIn(Thing t)
        {
            if (t == null || !IsoRimMod.Settings.models3D || !View3D.Ready)
            {
                return false;
            }
            ThingForm form = Forms.For(t.def);
            if (form.form != Form3D.Model || form.model == null || form.model.pulse == null)
            {
                return false;
            }
            return t.def.drawerType != DrawerType.RealtimeOnly || SectionLayer_IsoRimBodies.StandsIn(t);
        }

        // The tick the thing last beat; int.MinValue for things that do not beat.
        private static int LastBeat(Thing t)
        {
            Building_FleshmassHeart heart = t as Building_FleshmassHeart;
            return heart != null ? lastBeatTick(heart) : int.MinValue;
        }

        // How far a part is scaled now (1 at rest).
        public static float Scale(int lastBeat, int delay, float max)
        {
            if (lastBeat == int.MinValue)
            {
                return 1f;
            }
            float f = (Find.TickManager.TicksGame - (lastBeat + delay)) / BeatTicks;
            return f >= 0f && f <= 1f ? Mathf.Lerp(1f, max, Mathf.Sin(Mathf.PI * f)) : 1f;
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
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3);
                for (int d = 0; d < defs.Count; d++)
                {
                    List<Thing> things = map.listerThings.ThingsOfDef(defs[d]);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (view.Contains(t.Position) && !t.Fogged() && StandsIn(t))
                        {
                            Draw(t, Forms.For(t.def).model);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing beating parts failed: " + e, 93611271);
            }
        }

        private static void Draw(Thing t, ModelEntry entry)
        {
            ModelDef def = entry.def;
            float yaw = entry.Yaw(t.Rotation);
            if (entry.Swapped(t.def))
            {
                yaw += 90f;
            }
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            Vector3 fit = entry.Fit(t.def) * def.scale;
            Vector3 at = t.TrueCenter();
            int beat = LastBeat(t);
            Color tint = R3DTextures.ModelTint(t);
            string swatch = R3DTextures.ModelSwatch(t);
            for (int i = 0; i < entry.pulse.Length; i++)
            {
                PulsePart part = def.pulseParts[i];
                if (entry.pulse[i] == null)
                {
                    continue;
                }
                float sx = Scale(beat, part.delay, part.maxScale.x);
                float sz = Scale(beat, part.delay, part.maxScale.y);
                Vector3 pivot = turn * (Vector3.Scale(new Vector3(part.pivot.x, 0f, part.pivot.y), fit) + def.offset);
                DynamicModels.Draw(ObjLoader.SheetTinted(entry.pulse[i], R3DTextures.SheetTint(t)), new Vector3(at.x + pivot.x, pivot.y, at.z + pivot.z), yaw,
                    Vector3.Scale(fit, new Vector3(sx, (sx + sz) * 0.5f, sz)), tint, swatch);
            }
        }

        // Building_FleshmassHeart.DrawAt prefix: while the model stands in, only the base DrawAt runs (the body is held back
        // by IsoRim's Thing.DrawAt prefix; the comps still draw, among them the flat fleshmass base under it) and the
        // game's three flat beating parts are left out: Pulses draws them in 3D.
        public static bool HeartDrawAt_Prefix(Building_FleshmassHeart heart, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (heart == null || !heart.Spawned || !StandsIn(heart))
                {
                    return true;
                }
                if (baseDrawAt == null)
                {
                    baseDrawAt = AccessTools.MethodDelegate<Action<ThingWithComps, Vector3, bool>>(
                        AccessTools.DeclaredMethod(typeof(ThingWithComps), "DrawAt", new[] { typeof(Vector3), typeof(bool) }), null, false, (Type[])null);
                }
                baseDrawAt(heart, drawLoc, flip);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Leaving out the fleshmass heart's flat parts failed: " + e, 93611272);
                return true;
            }
        }

        // For the harness: the scale of each part now.
        public static string Report(Thing t)
        {
            ModelEntry entry;
            if (!ModelLibrary.TryGet(t.def, out entry) || entry.pulse == null)
            {
                return "no pulse parts";
            }
            int beat = LastBeat(t);
            string s = "lastBeat " + beat + " standsIn " + StandsIn(t);
            foreach (PulsePart p in entry.def.pulseParts)
            {
                s += " " + Scale(beat, p.delay, p.maxScale.x).ToString("0.000");
            }
            return s;
        }
    }
}
