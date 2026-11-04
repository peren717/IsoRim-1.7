using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 55) A model whose body hangs in the air round a point (the void node's orb): the game lays the thing's effects out
    // flat round its centre, on the ground, seen from above. With ModelDef.effectHeight (the point's height) and effectRadius
    // (the body's radius) those effects go round the body instead. Only what the thing's own effecters make, and only what the
    // game draws over things (altitude MoteOverheadLow and up; what it lays on the ground stays there):
    //   - a fleck that orbits, or starts outside the radius: flat at the point's height, so it passes behind and in front of the body;
    //   - any other fleck, and a mote: stood up in front of the body, facing the camera, keeping its place round the centre as
    //     the game lays it out from above, so the picture round the body is the game's own whichever way the camera turns.
    // ModelDef.cardState: while the thing is in that state (Api.AddModelState) the model stands down and the game's own sprite
    // is drawn on a card facing the camera at that point (the void node's end: the game's shader dissolves it). The block is
    // rebuilt when the state changes. Vanilla's state here: "closing" for a void node from the moment it is disrupted or embraced.
    public static class EffectLift
    {
        private static ThingDef[] defs;
        private static ModelDef[] models;

        // While a lifted thing's effecter runs: that thing (only read and written while the game ticks).
        private static Thing current;
        private static ModelDef currentDef;

        // Things in their card state as last seen, so that the block is rebuilt once when that changes.
        private static readonly Dictionary<int, bool> shown = new Dictionary<int, bool>();

        private static readonly AccessTools.FieldRef<CompVoidNode, int> closeTick = AccessTools.FieldRefAccess<CompVoidNode, int>("closeTick");

        private static void Init()
        {
            List<ThingDef> d = new List<ThingDef>();
            List<ModelDef> m = new List<ModelDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ModelEntry entry;
                if (ModelLibrary.TryGet(def, out entry) && entry.HasModel && entry.def.effectHeight > 0f)
                {
                    d.Add(def);
                    m.Add(entry.def);
                }
            }
            models = m.ToArray();
            defs = d.ToArray();
            StateModels.AddState(VanillaState);
        }

        // The model entry's def of a lifted thing, or null. A list of one or two: cheaper than a table for every thing drawn.
        private static ModelDef Of(ThingDef def)
        {
            if (defs == null)
            {
                Init();
            }
            for (int i = 0; i < defs.Length; i++)
            {
                if (defs[i] == def)
                {
                    return models[i];
                }
            }
            return null;
        }

        private static bool Lifting(Thing t, ModelDef def)
        {
            return def != null && IsoRimMod.Settings.models3D && View3D.Ready && Forms.For(t.def).form == Form3D.Model;
        }

        private static string VanillaState(Thing t)
        {
            if (t.def != ThingDefOf.VoidNode)
            {
                return null;
            }
            CompVoidNode comp = t.TryGetComp<CompVoidNode>();
            return comp != null && closeTick(comp) > 0 ? "closing" : null;
        }

        // True while the game's sprite stands in for the model on its card.
        public static bool ShowsCard(Thing t)
        {
            ModelDef def = Of(t.def);
            return def != null && !def.cardState.NullOrEmpty() && StateModels.StateOf(t) == def.cardState;
        }

        // Patches.ThingDraw_Prefix, for every thing drawn: true if this thing's sprite is to stand on its card now; rebuilds its
        // block when that changes.
        public static bool Card(Thing t, out float height, out Vector3 pivot)
        {
            height = 0f;
            pivot = default(Vector3);
            ModelDef def = Of(t.def);
            if (!Lifting(t, def) || def.cardState.NullOrEmpty())
            {
                return false;
            }
            bool now = StateModels.StateOf(t) == def.cardState;
            bool was;
            shown.TryGetValue(t.thingIDNumber, out was);
            if (now != was)
            {
                shown[t.thingIDNumber] = now;
                MapSections.QueueAround(t.Map, t.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
            }
            height = def.effectHeight;
            pivot = t.TrueCenter();
            return now;
        }

        // Patches.ThingDraw_Prefix, for a mote: true if it is over a lifted thing and goes in front of the body. height and pivot
        // are for a card (DrawHook: billboardHeight, billboardPivot).
        public static bool MoteCard(Mote mote, out float height, out Vector3 pivot)
        {
            height = 0f;
            pivot = default(Vector3);
            if (defs == null)
            {
                Init();
            }
            if (defs.Length == 0 || mote.def.altitudeLayer < AltitudeLayer.MoteOverheadLow || !mote.Spawned)
            {
                return false;
            }
            Building b = mote.Position.GetEdifice(mote.Map);
            ModelDef def = b != null ? Of(b.def) : null;
            if (!Lifting(b, def))
            {
                return false;
            }
            InFront(b.TrueCenter(), def, out height, out pivot);
            return true;
        }

        // A point of the card over the body's nearest side to the camera: the body's centre moved toward the camera by its radius.
        public static void InFront(Vector3 centre, ModelDef def, out float height, out Vector3 pivot)
        {
            float a = View3D.Pitch * Mathf.Deg2Rad;
            Vector3 back = Quaternion.Euler(0f, View3D.Yaw, 0f) * Vector3.back;
            pivot = centre + back * (def.effectRadius * Mathf.Cos(a));
            height = def.effectHeight + def.effectRadius * Mathf.Sin(a);
        }

        // Effecter.EffectTick and Effecter.Trigger: note whose effecter runs.
        public static void Effecter_Prefix(TargetInfo A, TargetInfo B, out Thing __state)
        {
            __state = current;
            Thing t = A.HasThing ? A.Thing : (B.HasThing ? B.Thing : null);
            ModelDef def = t != null ? Of(t.def) : null;
            if (def != null)
            {
                current = t;
                currentDef = def;
            }
        }

        public static void Effecter_Finalizer(Thing __state)
        {
            current = __state;
        }

        // FleckManager.CreateFleck: a fleck of a lifted thing's effecter is noted by where it starts (Emitters).
        public static void CreateFleck_Prefix(FleckCreationData fleckData)
        {
            Thing t = current;
            if (t == null || !t.Spawned || fleckData.def == null || fleckData.def.altitudeLayer < AltitudeLayer.MoteOverheadLow || !Lifting(t, currentDef))
            {
                return;
            }
            Vector3 c = t.TrueCenter();
            Vector3 d = fleckData.spawnPosition - c;
            d.y = 0f;
            bool flat = fleckData.orbitSpeed != 0f || d.magnitude >= currentDef.effectRadius;
            Emitters.RaiseRound(fleckData.spawnPosition, c, currentDef.effectHeight, currentDef.effectRadius, flat, fleckData.def);
        }

        // For the harness.
        public static string Report(Thing t)
        {
            ModelDef def = Of(t.def);
            return def == null ? "not lifted" : "height " + def.effectHeight + " radius " + def.effectRadius + " state " + (StateModels.StateOf(t) ?? "-")
                + " card " + ShowsCard(t) + " standsIn " + SectionLayer_IsoRimBodies.StandsIn(t);
        }
    }
}
