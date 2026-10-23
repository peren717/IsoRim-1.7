using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for Api 25 (humanlike races, Races.cs / RaceCards.cs):
    //   racepawn=x,z,rot,<PawnKindDef>,age[:Body[:M|F]],Def[:Stuff[:RRGGBB]];...  a new drafted colonist of that kind (its race),
    //                                as dressed= (fixed seed per cell; hair:<HairDef>, beard:<BeardDef>; "-" wears nothing)
    //   racehooks=1                  a test figure hook (draws small cubes at the anchors tailRoot, handRight and face, counts
    //                                calls per person) and a test face drawer (counts, draws nothing) registered
    //   keeppaper=<ThingDef>|-       the "Keep as paper" list set to that race (or emptied), as the settings button does
    //   goto=px,pz,tx,tz             only the colonist in (px,pz) ordered to walk to (tx,tz) (a real Goto job; run tick= after it)
    //   racecheck=1                  logs every humanlike on the map: race, Api.FigureModeOf, drawn as a model, the look's
    //                                race placing (People.Report), the hook and face drawer counts
    public static partial class Probe
    {
        private static readonly Dictionary<int, int> hookCalls = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> faceCalls = new Dictionary<int, int>();
        private static readonly Dictionary<int, string> hookModes = new Dictionary<int, string>();
        private static bool raceHooks;
        private static ModelHandle cubeA, cubeB, cubeC;

        private static bool RaceKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "racepawn":
                    RacePawn(a, map);
                    return true;
                case "racehooks":
                    if (!raceHooks)
                    {
                        raceHooks = true;
                        cubeA = new ModelHandle(ObjLoader.Box(new Vector3(-0.03f, -0.03f, -0.03f), new Vector3(0.03f, 0.03f, 0.03f), new Color(1f, 0f, 1f)));
                        cubeB = new ModelHandle(ObjLoader.Box(new Vector3(-0.025f, -0.025f, -0.025f), new Vector3(0.025f, 0.025f, 0.025f), new Color(0f, 1f, 1f)));
                        cubeC = new ModelHandle(ObjLoader.Box(new Vector3(-0.02f, -0.02f, -0.02f), new Vector3(0.02f, 0.02f, 0.02f), new Color(1f, 1f, 0f)));
                        Api.AddFigureHook(new FigureHook(RaceHook));
                        Api.AddFaceDrawer(new FaceDrawer(RaceFace));
                    }
                    return true;
                case "keeppaper":
                    IsoRimMod.Settings.keepPaper.Clear();
                    if (a[0] != "-")
                    {
                        IsoRimMod.Settings.keepPaper.Add(a[0]);
                    }
                    RaceFigures.Clear();
                    return true;
                case "goto":
                {
                    // goto=px,pz,tx,tz: only the colonist in (px,pz) ordered to walk there (a real Goto job; others keep their jobs)
                    Pawn gp = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    if (gp != null)
                    {
                        Job gj = JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(I(a[2]), 0, I(a[3])));
                        gp.jobs.TryTakeOrderedJob(gj, JobTag.Misc);
                    }
                    return true;
                }
                case "racecheck":
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps != null && x.RaceProps.Humanlike).OrderBy(x => x.Position.x * 1000 + x.Position.z))
                    {
                        int hc, fc;
                        hookCalls.TryGetValue(p.thingIDNumber, out hc);
                        faceCalls.TryGetValue(p.thingIDNumber, out fc);
                        string hm;
                        hookModes.TryGetValue(p.thingIDNumber, out hm);
                        Log.Message("[IsoRim probe] racecheck " + p.LabelShort + " at " + p.Position + " race " + p.def.defName + " mode " + Api.FigureModeOf(p)
                            + " drawnAsModel " + Patches.DrawnAsModel(p) + " hooks " + hc + " faces " + fc + " (" + (hm ?? "-") + ") posture " + p.GetPosture()
                            + " job " + (p.CurJobDef != null ? p.CurJobDef.defName : "-") + " carrying " + (p.carryTracker != null && p.carryTracker.CarriedThing != null ? p.carryTracker.CarriedThing.LabelShort : "-")
                            + "; " + People.Report(p));
                    }
                    return true;
            }
            return false;
        }

        private static void RaceHook(FigureFrame f)
        {
            if (f.Pawn == null)
            {
                return;
            }
            int n;
            hookCalls.TryGetValue(f.Pawn.thingIDNumber, out n);
            hookCalls[f.Pawn.thingIDNumber] = n + 1;
            hookModes[f.Pawn.thingIDNumber] = "frame mode " + f.Mode;
            Matrix4x4 m;
            if (f.Anchor("tailRoot", out m))
            {
                DynamicModels.DrawMatrix(cubeA.model, m, Color.white, null);
            }
            if (f.Anchor("hand_r", out m))
            {
                DynamicModels.DrawMatrix(cubeB.model, m, Color.white, null);
            }
            if (f.Anchor("face", out m))
            {
                DynamicModels.DrawMatrix(cubeC.model, m, Color.white, null);
            }
        }

        private static bool RaceFace(FaceFrame f)
        {
            if (f.Pawn != null)
            {
                int n;
                faceCalls.TryGetValue(f.Pawn.thingIDNumber, out n);
                faceCalls[f.Pawn.thingIDNumber] = n + 1;
            }
            return false;
        }

        private static void RacePawn(string[] a, Map map)
        {
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed(a[3]);
            string[] ageBody = a[4].Split(':');
            float age = float.Parse(ageBody[0], CultureInfo.InvariantCulture);
            bool child = age < 13f;
            string items = a.Length > 5 ? a[5] : "-";
            Rand.PushState(Gen.HashCombineInt(I(a[0]), I(a[1]) * 7919 + 17));
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                fixedGender: ageBody.Length > 2 ? (ageBody[2] == "F" ? Gender.Female : Gender.Male) : (Gender?)null,
                mustBeCapableOfViolence: items.Contains("Gun_") || items.Contains("MeleeWeapon_") || items.Contains("Bow_"),
                fixedBiologicalAge: age, fixedChronologicalAge: age, developmentalStages: child ? DevelopmentalStage.Child : DevelopmentalStage.Adult));
            Rand.PopState();
            p.apparel.DestroyAll();
            p.equipment.DestroyAllEquipment();
            if (!child && ageBody.Length > 1 && ageBody[1] != "-")
            {
                p.story.bodyType = DefDatabase<BodyTypeDef>.GetNamed(ageBody[1]);
            }
            foreach (string item in items.Split(';'))
            {
                if (item == "-" || item.Length == 0)
                {
                    continue;
                }
                string[] ds = item.Split(':');
                if (ds[0] == "hair")
                {
                    p.story.hairDef = DefDatabase<HairDef>.GetNamed(ds[1]);
                    continue;
                }
                if (ds[0] == "beard")
                {
                    p.style.beardDef = DefDatabase<BeardDef>.GetNamed(ds[1]);
                    continue;
                }
                ThingDef dd = DefDatabase<ThingDef>.GetNamed(ds[0]);
                ThingDef stuff = ds.Length > 1 && ds[1] != "-" ? DefDatabase<ThingDef>.GetNamed(ds[1]) : (dd.MadeFromStuff ? GenStuff.DefaultStuffFor(dd) : null);
                Thing t = ThingMaker.MakeThing(dd, stuff);
                Color dye;
                if (ds.Length > 2 && ColorUtility.TryParseHtmlString("#" + ds[2], out dye))
                {
                    t.SetColor(dye);
                }
                if (t is Apparel ap)
                {
                    p.apparel.Wear(ap, false, true);
                }
                else if (t is ThingWithComps eq && dd.IsWeapon)
                {
                    p.equipment.AddEquipment(eq);
                }
            }
            p.Drawer.renderer.SetAllGraphicsDirty();
            GenSpawn.Spawn(p, new IntVec3(I(a[0]), 0, I(a[1])), map, new Rot4(I(a[2])));
            p.drafter.Drafted = true;
            p.Rotation = new Rot4(I(a[2]));
            Log.Message("[IsoRim probe] racepawn " + p.LabelShort + " (" + p.def.defName + ", " + p.story.bodyType.defName + ", " + p.story.headType.defName + ") at " + p.Position
                + ": " + string.Join(", ", p.apparel.WornApparel.Select(x => x.def.defName)) + (p.equipment.Primary != null ? " + " + p.equipment.Primary.def.defName : "")
                + "; " + People.Report(p));
        }
    }
}
