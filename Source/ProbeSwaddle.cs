using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for swaddled babies (People.DrawSwaddle; branch swaddle3d). Cells are map cells. The jobs are the game's own,
    // run with real ticks until the state wanted is reached (at most max ticks).
    //   swaddle3d=0|1                 the swaddle model off (the body drawn as before: the old look) or on; every look worked out again
    //   babyvat=hx,hz,bx,bz,vx,vz,max the baby at (bx,bz) chosen for the growth vat at (vx,vz) (its select button) and the colonist at
    //                                 (hx,hz) ordered to carry it in (CarryToBuilding, as the work giver); ticks until it is inside, then
    //                                 the colonist drafted (it stands still)
    //   babyfeed=hx,hz,bx,bz,max      the baby made hungry, baby food in the colonist's cell, the colonist ordered to bottle-feed it
    //                                 (BottleFeedBaby, as the work giver); ticks until it is feeding (holding the baby)
    //   babycarry=hx,hz,bx,bz,cx,cz,max  the bed at (cx,cz) assigned to the baby, the colonist ordered to bring it to safety (to the bed);
    //                                 ticks until it carries the baby, then 30 more (walking with it)
    //   swaddlecheck=1                logs every person: life stage, the game's swaddle state, IsoRim's look (swaddle, colour against the
    //                                 game's swaddle node colour), carried by, bed, holder, IsoRim's placing and body box
    public static partial class Probe
    {
        private static bool SwaddleKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "swaddle3d":
                    People.SwaddleOn = a[0] != "0";
                    People.RefreshAll();
                    Log.Message("[IsoRim probe] swaddle3d " + People.SwaddleOn);
                    return true;
                case "babyvat":
                {
                    Pawn h = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn b = PawnAt(map, I(a[2]), I(a[3]));
                    Building_GrowthVat vat = new IntVec3(I(a[4]), 0, I(a[5])).GetFirstBuilding(map) as Building_GrowthVat;
                    if (h == null || b == null || vat == null)
                    {
                        Log.Warning("[IsoRim probe] babyvat: no carrier, baby or growth vat at " + string.Join(",", a));
                        return true;
                    }
                    AccessTools.Method(vat.GetType(), "SelectPawn").Invoke(vat, new object[] { b });
                    AcceptanceReport ok = vat.CanAcceptPawn(b);
                    if (h.drafter != null)
                    {
                        h.drafter.Drafted = false;
                    }
                    Job job = JobMaker.MakeJob(JobDefOf.CarryToBuilding, vat, b);
                    job.count = 1;
                    bool taken = h.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    int max = Mathf.Clamp(I(a[6]), 1, 5000), done = 0;
                    while (done < max && b.ParentHolder != vat)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    if (h.Spawned && h.drafter != null)
                    {
                        h.drafter.Drafted = true;
                    }
                    Log.Message("[IsoRim probe] babyvat " + h.LabelShort + " carries " + b.LabelShort + " into " + vat.Position + " (can accept: " + (ok.Accepted ? "yes" : ok.Reason)
                        + ", ordered " + taken + "): " + done + " ticks, inside " + (b.ParentHolder == vat) + ", SwaddleBaby " + b.SwaddleBaby());
                    return true;
                }
                case "babyfeed":
                {
                    Pawn h = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn b = PawnAt(map, I(a[2]), I(a[3]));
                    if (h == null || b == null)
                    {
                        Log.Warning("[IsoRim probe] babyfeed: no feeder or baby at " + string.Join(",", a));
                        return true;
                    }
                    if (b.needs != null && b.needs.food != null)
                    {
                        b.needs.food.CurLevel = b.needs.food.MaxLevel * 0.1f;   // hungry
                    }
                    Thing food = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("BabyFood"));
                    food.stackCount = 5;
                    GenSpawn.Spawn(food, h.Position, map);
                    if (h.drafter != null)
                    {
                        h.drafter.Drafted = false;
                    }
                    bool taken = h.jobs.TryTakeOrderedJob(ChildcareUtility.MakeBottlefeedJob(b, food), JobTag.Misc);
                    int max = Mathf.Clamp(I(a[4]), 1, 5000), done = 0;
                    while (done < max && !(h.jobs.curDriver is JobDriver_FeedBaby fd && fd.Feeding))
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    Log.Message("[IsoRim probe] babyfeed " + h.LabelShort + " feeds " + b.LabelShort + " (ordered " + taken + "): " + done + " ticks, feeding "
                        + (h.jobs.curDriver is JobDriver_FeedBaby f2 && f2.Feeding) + ", feeder at " + h.Position + " rot " + h.Rotation + ", carried by " + (b.CarriedBy != null ? b.CarriedBy.LabelShort : "none")
                        + ", baby job " + (b.CurJobDef != null ? b.CurJobDef.defName : "none"));
                    return true;
                }
                case "babycarry":
                {
                    Pawn h = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn b = PawnAt(map, I(a[2]), I(a[3]));
                    Building_Bed bed = map.thingGrid.ThingsListAt(new IntVec3(I(a[4]), 0, I(a[5]))).OfType<Building_Bed>().FirstOrDefault();
                    if (h == null || b == null || bed == null)
                    {
                        Log.Warning("[IsoRim probe] babycarry: no carrier, baby or bed at " + string.Join(",", a));
                        return true;
                    }
                    bed.Medical = false;
                    CompAssignableToPawn assign = bed.TryGetComp<CompAssignableToPawn>();
                    if (assign != null)
                    {
                        assign.TryAssignPawn(b);
                    }
                    if (b.needs != null && b.needs.rest != null)
                    {
                        b.needs.rest.CurLevel = 0.1f;
                    }
                    if (h.drafter != null)
                    {
                        h.drafter.Drafted = false;
                    }
                    bool taken = h.jobs.TryTakeOrderedJob(ChildcareUtility.MakeBringBabyToSafetyJob(h, b), JobTag.Misc);
                    int max = Mathf.Clamp(I(a[6]), 1, 5000), done = 0, after = 0;
                    while (done < max && after < 30)
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                        if (b.CarriedBy == h)
                        {
                            after++;
                        }
                    }
                    Log.Message("[IsoRim probe] babycarry " + h.LabelShort + " carries " + b.LabelShort + " (ordered " + taken + "): " + done + " ticks, carried by "
                        + (b.CarriedBy != null ? b.CarriedBy.LabelShort : "none") + ", carrier at " + h.Position + " rot " + h.Rotation + " DrawPos " + CV(h.DrawPos));
                    return true;
                }
                case "swaddlecheck":
                {
                    foreach (Pawn p in map.mapPawns.AllPawns.Where(x => x.RaceProps.Humanlike && x.ageTracker != null).OrderBy(x => x.thingIDNumber).ToList())
                    {
                        SwaddleReport(p);
                    }
                    return true;
                }
            }
            return false;
        }

        private static void SwaddleReport(Pawn p)
        {
            People.Look look = People.LastLook(p);
            Color game = Color.clear;
            PawnRenderTree tree = p.Drawer != null && p.Drawer.renderer != null ? p.Drawer.renderer.renderTree : null;
            PawnRenderNode node = null;
            if (tree != null && tree.rootNode != null)
            {
                System.Collections.Generic.Stack<PawnRenderNode> open = new System.Collections.Generic.Stack<PawnRenderNode>();
                open.Push(tree.rootNode);
                while (open.Count > 0 && node == null)
                {
                    PawnRenderNode n = open.Pop();
                    if (n is PawnRenderNode_Swaddle)
                    {
                        node = n;
                    }
                    else if (n != null && n.children != null)
                    {
                        foreach (PawnRenderNode c in n.children)
                        {
                            open.Push(c);
                        }
                    }
                }
            }
            if (node != null)
            {
                game = node.ColorFor(p);
            }
            string placing = "no placing";
            FigurePlacement pl;
            Pawn carrier = p.CarriedBy;
            if (p.SpawnedOrAnyParentSpawned && Placing.Read(p, p.DrawPosHeld ?? p.PositionHeld.ToVector3Shifted(), false, 0f, null, out pl))
            {
                placing = "situation " + pl.Situation + " Position " + CV(pl.Position) + " yaw " + pl.Yaw.ToString("0") + " size " + pl.Size.ToString("0.000") + " HeadOnlyInBed " + pl.HeadOnlyInBed
                    + " Carried " + pl.Carried;
            }
            string box = "";
            if (look != null)
            {
                FigureBoxes.Box b = FigureBoxes.Body(look);
                box = " body box centre " + CV(b.centre) + " size " + CV(b.size);
            }
            Log.Message("[IsoRim probe] swaddlecheck " + p.LabelShort + " #" + p.thingIDNumber + " at " + p.PositionHeld + " (" + p.ageTracker.CurLifeStage.defName + ", " + p.DevelopmentalStage
                + "): SwaddleBaby " + p.SwaddleBaby() + ", look " + (look == null ? "none" : "swaddled " + look.swaddled + " model " + (look.swaddle != null) + " colour " + ColorUtility.ToHtmlStringRGB(look.swaddleColor))
                + ", game node " + (node != null ? ColorUtility.ToHtmlStringRGB(game) : "none")
                + (look != null && look.swaddle != null && node != null ? " CHECK colour " + (look.swaddleColor == game ? "same" : "DIFFERENT") : "")
                + ", carried by " + (carrier != null ? carrier.LabelShort : "none") + ", bed " + (p.CurrentBed() != null ? p.CurrentBed().def.defName : "none")
                + ", holder " + (p.ParentHolder != null ? p.ParentHolder.GetType().Name : "none") + ", posture " + p.GetPosture() + ", job " + (p.CurJobDef != null ? p.CurJobDef.defName : "none")
                + ", " + placing + box);
        }
    }
}
