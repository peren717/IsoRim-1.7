using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // A part of a model that bobs up and down (the grav engine's orb): the entry's bobModel, drawn every frame over the
    // baked body, raised by the game's own motion: 0.5 * (1 + sin(2 pi * ticks / bobPeriod)) * bobHeight cells, by game
    // ticks, so it keeps pace with the game speed and stands still while paused. A grav engine's orb rests at its lowest
    // point while the engine cools down after a flight (as Building_GravEngine.DrawAt does). The game's own flat orb
    // (drawn in Building_GravEngine.DrawAt after the body) is left out while the model stands in; with the 3D view or
    // models off the game draws everything as usual.
    // Also the Cerebrex core's brain (CompCerebrexCore.DrawAt: 0.35 cells, 300 ticks; its flat brain left out) and the
    // mech relay's crystal (Building_MechRelay.DrawAt: the same sine; the game's drop-spot shadow kept, its flat crystal
    // left out). A relay about to break down sinks to the ground and turns 45 degrees over its last 120 ticks, as the game's.
    public static class Bobbers
    {
        private static List<ThingDef> defs;
        private static Action<ThingWithComps, Vector3, bool> baseDrawAt;
        private static Material relayShadow;

        // Building_MechRelay.DrawAt lifts its sprite 0.55 cells before the bob; the crystal model is built at that lift.
        private const float RelayLift = 0.55f;

        private static void Init()
        {
            defs = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ModelEntry entry;
                if (ModelLibrary.TryGet(def, out entry) && entry.bob != null && entry.HasModel)
                {
                    defs.Add(def);
                }
            }
        }

        // How far up the part is now, in cells.
        public static float Rise(Thing t, ModelDef def)
        {
            Building_GravEngine engine = t as Building_GravEngine;
            if (engine != null && Find.TickManager.TicksGame < engine.cooldownCompleteTick)
            {
                return 0f;
            }
            int period = Mathf.Max(1, def.bobPeriod);
            float rise = 0.5f * (1f + Mathf.Sin(Mathf.PI * 2f * GenTicks.TicksGame / period)) * def.bobHeight;
            Building_MechRelay relay = t as Building_MechRelay;
            if (relay != null)
            {
                rise = Mathf.Lerp(rise, -RelayLift, RelayFall(relay));
            }
            return rise;
        }

        // How far a mech relay has fallen (0..1) in its last 120 ticks before it breaks down, as Building_MechRelay.DrawAt.
        private static float RelayFall(Building_MechRelay relay)
        {
            CompMechRelay comp = relay.GetComp<CompMechRelay>();
            if (comp == null || comp.DestabilizationTick == -1)
            {
                return 0f;
            }
            return Mathf.Pow(1f - Mathf.Clamp01((comp.DestabilizationTick - Find.TickManager.TicksGame) / 120f), 2f);
        }

        // True while the bob part is drawn for this thing (as DrawAll draws it): models on, the 3D view up, a model with a
        // bob part; a body drawn every frame (RealtimeOnly) only once its baked model stands in.
        private static bool PartStandsIn(Thing t)
        {
            if (t == null || !IsoRimMod.Settings.models3D || !View3D.Ready)
            {
                return false;
            }
            ThingForm form = Forms.For(t.def);
            if (form.form != Form3D.Model || form.model == null || form.model.bob == null)
            {
                return false;
            }
            return t.def.drawerType != DrawerType.RealtimeOnly || SectionLayer_IsoRimBodies.StandsIn(t);
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
                    ThingForm form = Forms.For(defs[d]);
                    if (form.form != Form3D.Model || form.model == null || form.model.bob == null)
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
                Log.ErrorOnce("[IsoRim] Drawing bobbing parts failed: " + e, 93611251);
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
            Building_MechRelay relay = t as Building_MechRelay;
            if (relay != null)
            {
                yaw += Mathf.Lerp(0f, 45f, RelayFall(relay));
            }
            Vector3 at = t.TrueCenter();
            Vector3 offset = Quaternion.AngleAxis(yaw, Vector3.up) * def.offset;
            DynamicModels.Draw(ObjLoader.SheetTinted(BobFor(t, entry), R3DTextures.SheetTint(t)), new Vector3(at.x + offset.x, offset.y + Rise(t, def), at.z + offset.z), yaw,
                entry.Fit(t.def) * def.scale, R3DTextures.ModelTint(t), R3DTextures.ModelSwatch(t));
        }

        // (Api 27) The bob part for the thing now: its state's (ModelDef.bobByState, the addons' Api.AddModelState), else
        // bobModel. Asked each frame the part is drawn.
        internal static Model BobFor(Thing t, ModelEntry entry)
        {
            if (entry.bobByState != null)
            {
                string st = StateModels.StateOf(t);
                Model m;
                if (st != null && entry.bobByState.TryGetValue(st, out m))
                {
                    return m;
                }
            }
            return entry.bob;
        }

        // Building_GravEngine.DrawAt prefix: while the model stands in (body baked, orb drawn here), the engine is drawn
        // as a plain ThingWithComps (body held back by the Thing.DrawAt prefix, comps drawn as usual), without its flat orb.
        public static bool GravEngineDrawAt_Prefix(Building_GravEngine engine, Vector3 drawLoc, bool flip)
        {
            try
            {
                if (engine == null || !IsoRimMod.Settings.models3D || !SectionLayer_IsoRimBodies.StandsIn(engine))
                {
                    return true;
                }
                ModelEntry entry = Forms.For(engine.def).model;
                if (entry == null || entry.bob == null)
                {
                    return true;
                }
                if (baseDrawAt == null)
                {
                    baseDrawAt = AccessTools.MethodDelegate<Action<ThingWithComps, Vector3, bool>>(
                        AccessTools.DeclaredMethod(typeof(ThingWithComps), "DrawAt", new[] { typeof(Vector3), typeof(bool) }), null, false, (Type[])null);
                }
                baseDrawAt(engine, drawLoc, flip);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Leaving out the grav engine's flat orb failed: " + e, 93611252);
                return true;
            }
        }

        // CompCerebrexCore.DrawAt prefix: the brain is the model's bob part; the game's flat brain is left out.
        public static bool CerebrexDrawAt_Prefix(ThingComp comp)
        {
            try
            {
                return comp == null || !PartStandsIn(comp.parent);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Leaving out the Cerebrex core's flat brain failed: " + e, 93611253);
                return true;
            }
        }

        // Building_MechRelay.DrawAt prefix: the crystal is the bob part over the baked pad; the game's drop-spot shadow is
        // drawn as the game does, its flat bobbing crystal left out.
        public static bool MechRelayDrawAt_Prefix(Building_MechRelay relay, Vector3 drawLoc)
        {
            try
            {
                if (relay == null || !relay.Spawned || !PartStandsIn(relay))
                {
                    return true;
                }
                if (relayShadow == null)
                {
                    relayShadow = MaterialPool.MatFrom("Things/Skyfaller/SkyfallerShadowDropPod", ShaderDatabase.Transparent);
                }
                Skyfaller.DrawDropSpotShadow(drawLoc, Rot4.North, relayShadow, relay.def.size.ToVector2(), 65);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Leaving out the mech relay's flat crystal failed: " + e, 93611254);
                return true;
            }
        }

        // For the harness: how far up a thing's bobbing part is, and whether the model stands in for the body.
        public static string Report(Thing t)
        {
            ModelEntry entry;
            if (!ModelLibrary.TryGet(t.def, out entry) || entry.bob == null)
            {
                return "no bob part";
            }
            return "rise " + Rise(t, entry.def).ToString("0.000") + " standsIn " + SectionLayer_IsoRimBodies.StandsIn(t);
        }
    }
}
