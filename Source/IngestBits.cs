using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Food bits, drink and smoke from the mouth (eatfx, 2026-11-01). While a pawn eats, drinks or smokes, the game runs the
    // ingested thing's ingest effecter (ThingDef.ingestible.ingestEffect, or ingestEffectEat for an animal: EatVegetarian,
    // EatMeat, Smoke_Joint, Smoke_Flake), a sprayer that puts its motes between the pawn and its table (positionLerpFactor),
    // flat on the ground. In the 3D view, for an eater drawn as a 3D model (Patches.DrawnAsModel), each one is drawn standing
    // up at the height of the eater's mouth instead (Emitters.StandMote, Api 44's mechanism): the figure's mouth as posed
    // now (People.MouthHeight: standing, seated at a table, crouching, on a host, in a bed; size and race), an animal's
    // (Animals.MouthHeight). Where on the map it is stays the game's. A pawn on an upper floor: the height is over its own
    // floor, as for every stood mote (the mote is in that floor's rows). Card mode and the 3D view off: unchanged.
    //
    // The game's callers of these effecters (all through Toils_Ingest.AddIngestionEffects -> ToilEffects.WithEffect ->
    // Effecter.Trigger / EffectTick -> SubEffecter_SprayerContinuous -> SubEffecter_Sprayer.MakeMote; A is the toil's
    // actor, B the eating surface, the food, or the eater being fed):
    //   - JobDriver_Ingest (Toils_Ingest.ChewIngestible: meals, raw food, drugs, beer, joints; A the eater)
    //   - JobDriver_SocialRelax (AddIngestionEffects for the drink or smoke of a social relax; A the eater)
    //   - JobDriver_FoodFeedPatient and JobDriver_Feed (ChewIngestible with the patient or baby as chewer: A is the feeder,
    //     B the one fed, whose mouth it is)
    //   - JobDriver_PredatorHunt (ChewIngestible: an animal eating its kill; A the predator)
    //   - JobDriver_EatAtCannibalPlatter (Ideology: WithEffect(EatMeat) directly; A the eater)
    // So one place, the sprayer's MakeMote (FloorSprays patches it already), covers them all: an ingest effecter is one
    // some ingestible names, and the eater is B when B is a pawn (being fed), else A. A mote it makes is stood (OnMote,
    // after the game creates it); a fleck it makes (none in the game's own, a mod's ingest effecter may) is stood the same
    // way (Emitters.Raise, on the eater's upper floor: CreateFleck).
    internal static class IngestBits
    {
        public static bool Off;           // harness switch (eatfx=0): as the game draws them, for before shots
        public static int Stood, Flecks;  // harness counters (eatcheck): motes and flecks stood at a mouth

        private static HashSet<EffecterDef> ingestEffects;

        private static bool active;
        private static float height;
        private static Matrix4x4 floor;
        internal static Pawn LastEater;   // harness (eatcheck)
        internal static float LastHeight;

        // An effecter some ingestible runs while it is eaten (built once, after the defs are loaded).
        public static bool IsIngestEffect(EffecterDef def)
        {
            if (def == null)
            {
                return false;
            }
            if (ingestEffects == null)
            {
                HashSet<EffecterDef> set = new HashSet<EffecterDef>();
                foreach (ThingDef t in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (t.ingestible != null)
                    {
                        if (t.ingestible.ingestEffect != null)
                        {
                            set.Add(t.ingestible.ingestEffect);
                        }
                        if (t.ingestible.ingestEffectEat != null)
                        {
                            set.Add(t.ingestible.ingestEffectEat);
                        }
                    }
                }
                if (EffecterDefOf.EatMeat != null)
                {
                    set.Add(EffecterDefOf.EatMeat);   // the cannibal platter's (Ideology), named directly
                }
                ingestEffects = set;
            }
            return ingestEffects.Contains(def);
        }

        // From FloorSprays.MakeMote_Prefix: whether the motes this sprayer makes now come from an eater's mouth, and how high.
        public static void Begin(SubEffecter_Sprayer sprayer, TargetInfo A, TargetInfo B)
        {
            active = false;
            if (Off || !View3D.Ready || sprayer == null || sprayer.parent == null || !IsIngestEffect(sprayer.parent.def))
            {
                return;
            }
            Pawn eater = B.Thing as Pawn ?? A.Thing as Pawn;   // (B a pawn: it is being fed)
            if (eater == null || !eater.Spawned || !Patches.DrawnAsModel(eater))
            {
                return;
            }
            float h;
            bool ok = eater.RaceProps.Humanlike ? People.MouthHeight(eater, out h) : Animals.MouthHeight(eater, out h);
            if (!ok)
            {
                return;
            }
            active = true;
            height = h;
            floor = Overhead.Floor(eater);
            LastEater = eater;
            LastHeight = h;
        }

        public static void End()
        {
            active = false;
        }

        // Right after MakeMote has created a mote (inserted by FloorSprays.MakeMote_Transpiler).
        public static void OnMote(Mote mote)
        {
            if (active && mote != null)
            {
                Emitters.StandMote(mote, height, false);
                Stood++;
            }
        }

        // In place of MakeMote's map.flecks.CreateFleck(data) (FloorSprays.MakeMote_Transpiler).
        public static void CreateFleck(FleckManager flecks, FleckCreationData data)
        {
            if (active && data.def != null)
            {
                Emitters.Raise(data.spawnPosition, height, 0f, data.def, floor);
                Flecks++;
            }
            flecks.CreateFleck(data);
        }
    }
}
