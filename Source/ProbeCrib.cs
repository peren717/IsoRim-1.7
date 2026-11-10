using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for babies and children in beds (cribs; branch cribfit). Cells are map cells. The jobs are the game's
    // own (a colonist carries the baby to its crib and tucks it in: BringBabyToSafety), run with real ticks (tick= after them).
    //   baby=x,z,<age years>[,F|M]   a new baby colonist (fixed seed per cell) lying in the cell
    //   babybed=hx,hz,bx,bz,cx,cz    the bed at (cx,cz) assigned to the baby at (bx,bz), the baby made tired and fed, and the
    //                                colonist at (hx,hz) undrafted and ordered to bring the baby to safety (to its bed)
    //   carriersgone=1               the colonists ordered by babybed= taken off the map (out of the shots)
    //   cribcheck=1                  logs each person in a bed: IsoRim's placing (Api 62) and how far the figure's measured box
    //                                (Api.FigureBounds, turned by the yaw) reaches past the bed's cells: head end, foot end, sides
    //                                (CHECK in/out), and the sleeper's blanket parts hash
    //   drawertest=1                 (Api 62/67) a figure drawer that reads the placing, asks ctx.DrawSleeperBlanket() and returns
    //                                false (IsoRim draws its own), and a figure hook that notes IsoRim's own head; drawercheck=1
    //                                logs per sleeper: drawer head vs IsoRim head, HeadOnlyInBed, Holder, blanket answer, lumps built
    public static partial class Probe
    {
        private static readonly List<Pawn> babyCarriers = new List<Pawn>();

        private class DrawerNote
        {
            public Vector3 drawerHead, ownHead, position;
            public float yaw, size;
            public bool headOnly, blanket, ownSeen, drawerSeen;
            public string values, ctxHash, staticHash;
            public int staticAnswer, staticBuilt;   // (Api 68) -1 not asked (not head-only), 0 false, 1 true; lumps built by it
            public string holder;
            public int frames, mismatches;
        }

        private static readonly Dictionary<Pawn, DrawerNote> drawerNotes = new Dictionary<Pawn, DrawerNote>();
        private static bool drawerOn;

        private static bool CribKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "baby":
                {
                    float age = F(a[2]);
                    Rand.PushState(Gen.HashCombineInt(I(a[0]), I(a[1]) * 7919 + 17));   // fixed seed per cell: the same baby every run
                    Pawn b = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                        fixedGender: a.Length > 3 ? (a[3] == "F" ? Gender.Female : Gender.Male) : (Gender?)null,
                        fixedBiologicalAge: age, fixedChronologicalAge: age, developmentalStages: DevelopmentalStage.Baby, allowDowned: true));
                    Rand.PopState();
                    GenSpawn.Spawn(b, new IntVec3(I(a[0]), 0, I(a[1])), map, Rot4.South);
                    Log.Message("[IsoRim probe] baby " + b.LabelShort + " at " + b.Position + ": life stage " + b.ageTracker.CurLifeStage.defName
                        + ", body " + (b.story != null && b.story.bodyType != null ? b.story.bodyType.defName : "none"));
                    return true;
                }
                case "babybed":
                {
                    Pawn h = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    Pawn b = map.thingGrid.ThingsListAt(new IntVec3(I(a[2]), 0, I(a[3]))).OfType<Pawn>().FirstOrDefault();
                    Building_Bed bed = map.thingGrid.ThingsListAt(new IntVec3(I(a[4]), 0, I(a[5]))).OfType<Building_Bed>().FirstOrDefault();
                    if (h == null || b == null || bed == null)
                    {
                        Log.Warning("[IsoRim probe] babybed: no carrier, baby or bed at " + string.Join(",", a));
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
                        b.needs.rest.CurLevel = 0.1f;   // tired, so it sleeps on in the bed
                    }
                    if (b.needs != null && b.needs.food != null)
                    {
                        b.needs.food.CurLevel = b.needs.food.MaxLevel;
                    }
                    if (h.drafter != null)
                    {
                        h.drafter.Drafted = false;
                    }
                    bool taken = h.jobs.TryTakeOrderedJob(ChildcareUtility.MakeBringBabyToSafetyJob(h, b), JobTag.Misc);
                    babyCarriers.Add(h);
                    Log.Message("[IsoRim probe] babybed " + h.LabelShort + " carries " + b.LabelShort + " -> " + bed.def.defName + " " + bed.Position + " rot " + bed.Rotation + ": ordered " + taken);
                    return true;
                }
                case "carriersgone":
                    foreach (Pawn c in babyCarriers)
                    {
                        if (c.Spawned)
                        {
                            Log.Message("[IsoRim probe] carriersgone " + c.LabelShort + " at " + c.Position + ", job " + (c.CurJobDef != null ? c.CurJobDef.defName : "none"));
                            c.DeSpawn();
                        }
                    }
                    babyCarriers.Clear();
                    return true;
                case "cribcheck":
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike && x.CurrentBed() != null).OrderBy(x => x.Position.x).ThenBy(x => x.Position.z).ToList())
                    {
                        CribReport(p);
                    }
                    return true;
                case "drawertest":
                    if (!drawerOn)
                    {
                        drawerOn = true;
                        Api.AddFigureDrawer(TestDrawer, FigureKinds.People);
                        Api.AddFigureHook(TestHook);
                    }
                    drawerNotes.Clear();
                    return true;
                case "drawercheck":
                    foreach (KeyValuePair<Pawn, DrawerNote> kv in drawerNotes.OrderBy(x => x.Key.Position.x).ThenBy(x => x.Key.Position.z))
                    {
                        DrawerNote n = kv.Value;
                        float d = Flat(n.drawerHead - n.ownHead);   // (x, z: the hook frame has the view squash in its heights)
                        Log.Message("[IsoRim probe] drawercheck " + kv.Key.LabelShort + " " + kv.Key.Position + " in " + n.holder + ": position " + CV(n.position) + " yaw " + n.yaw.ToString("0")
                            + " size " + n.size.ToString("0.000") + ", HeadOnlyInBed " + n.headOnly + ", ctx.DrawSleeperBlanket " + n.blanket
                            + ", drawer head " + CV(n.drawerHead) + " own head " + CV(n.ownHead) + " diff " + d.ToString("0.0000") + " (frames " + n.frames + ", mismatched " + n.mismatches + ")"
                            + ", " + SleeperBlanket.Report(kv.Key) + ", parts " + SleeperBlanket.PartsHash(kv.Key)
                            + ", parts after ctx " + n.ctxHash + ", Api.DrawSleeperBlanket " + (n.staticAnswer < 0 ? "not asked" : (n.staticAnswer > 0).ToString())
                            + " parts after static " + n.staticHash + " built by static " + n.staticBuilt
                            + " CHECK " + (n.ownSeen && n.drawerSeen && n.mismatches == 0 && d < 0.0005f && (n.staticAnswer < 0 || n.staticHash == n.ctxHash && n.staticBuilt == 0) ? "same" : "DIFFERENT"));
                        Log.Message("[IsoRim probe] drawervalues " + kv.Key.LabelShort + " " + kv.Key.Position + " " + n.values);
                    }
                    return true;
            }
            return false;
        }

        private static string MH(Matrix4x4 m)
        {
            string s = "";
            for (int i = 0; i < 12; i++)
            {
                s += (i > 0 ? "," : "") + m[i % 3, i / 3].ToString("0.0000");
            }
            return "[" + s + "]";
        }

        private static float Flat(Vector3 v)
        {
            return new Vector2(v.x, v.z).magnitude;
        }

        private static string CV(Vector3 v)
        {
            return "(" + v.x.ToString("0.000") + ", " + v.y.ToString("0.000") + ", " + v.z.ToString("0.000") + ")";
        }

        // How far the figure reaches past its bed's cells (cells; above 0 = out), along the bed (head end, foot end) and across.
        private static void CribReport(Pawn p)
        {
            Building_Bed bed = p.CurrentBed();
            FigurePlacement pl;
            if (!Placing.Read(p, p.DrawPos, false, 0f, null, out pl))
            {
                Log.Message("[IsoRim probe] cribcheck " + p.LabelShort + " " + p.Position + ": no placing (3D off?)");
                return;
            }
            Vector3 lo, hi;
            if (!Api.FigureBounds(p, out lo, out hi))
            {
                Log.Message("[IsoRim probe] cribcheck " + p.LabelShort + " " + p.Position + ": no bounds");
                return;
            }
            Quaternion turn = Quaternion.AngleAxis(pl.Yaw, Vector3.up);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = new Vector3((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z);
                Vector3 w = pl.Position + turn * c;
                minX = Mathf.Min(minX, w.x);
                maxX = Mathf.Max(maxX, w.x);
                minZ = Mathf.Min(minZ, w.z);
                maxZ = Mathf.Max(maxZ, w.z);
            }
            CellRect r = bed.OccupiedRect();
            float bx0 = r.minX, bx1 = r.maxX + 1, bz0 = r.minZ, bz1 = r.maxZ + 1;
            // the bed's foot is the way it faces (FacingCell); its head the other way
            IntVec3 foot = bed.Rotation.FacingCell;
            float outN = maxZ - bz1, outS = bz0 - minZ, outE = maxX - bx1, outW = bx0 - minX;
            float footOut = foot.z > 0 ? outN : foot.z < 0 ? outS : foot.x > 0 ? outE : outW;
            float headOut = foot.z > 0 ? outS : foot.z < 0 ? outN : foot.x > 0 ? outW : outE;
            float sideOut = foot.z != 0 ? Mathf.Max(outE, outW) : Mathf.Max(outN, outS);
            Vector3 cell = p.Position.ToVector3Shifted();
            Vector3 head = pl.Position + turn * pl.HeadPlace.MultiplyPoint3x4(Vector3.zero);
            float maxOut = Mathf.Max(footOut, Mathf.Max(headOut, sideOut));
            Log.Message("[IsoRim probe] cribcheck " + p.LabelShort + " " + p.Position + " (" + p.ageTracker.CurLifeStage.defName + ", body " + (p.story.bodyType != null ? p.story.bodyType.defName : "-")
                + ") in " + bed.def.defName + " " + bed.Position + " rot " + bed.Rotation + " cells " + r + ": situation " + pl.Situation + ", Position " + CV(pl.Position)
                + " (cell centre " + CV(cell) + ", DrawPos " + CV(p.DrawPos) + "), yaw " + pl.Yaw.ToString("0") + ", size " + pl.Size.ToString("0.000")
                + ", HeadOnlyInBed " + pl.HeadOnlyInBed + ", Holder " + (pl.Holder != null ? pl.Holder.def.defName : "none")
                + ", feet " + CV(head) + ", box x " + minX.ToString("0.000") + ".." + maxX.ToString("0.000") + " z " + minZ.ToString("0.000") + ".." + maxZ.ToString("0.000")
                + ", out: head end " + headOut.ToString("0.000") + ", foot end " + footOut.ToString("0.000") + ", side " + sideOut.ToString("0.000")
                + ", blanket parts " + SleeperBlanket.PartsHash(p)
                + " CHECK " + (maxOut <= 0.001f ? "inside" : "OUT by " + maxOut.ToString("0.000")));
        }

        // (Api 62/67 test) Reads the placing, asks for the blanket and lets IsoRim draw its own figure.
        private static bool TestDrawer(FigureDrawContext ctx)
        {
            Pawn p = ctx.Pawn;
            if (p == null || p.Dead || p.CurrentBed() == null || ctx.ForOtherMod)
            {
                return false;
            }
            FigurePlacement pl;
            if (!ctx.ReadPlacement(default(FigurePlacementOptions), out pl))
            {
                return false;
            }
            DrawerNote n;
            if (!drawerNotes.TryGetValue(p, out n))
            {
                n = new DrawerNote();
                drawerNotes[p] = n;
            }
            n.position = pl.Position;
            n.yaw = pl.Yaw;
            n.size = pl.Size;
            n.headOnly = pl.HeadOnlyInBed;
            n.holder = pl.Holder != null ? pl.Holder.def.defName + " " + pl.Holder.Position : "none";
            n.drawerHead = pl.Position + Quaternion.AngleAxis(pl.Yaw, Vector3.up) * pl.HeadPlace.MultiplyPoint3x4(Vector3.zero);
            n.drawerSeen = true;
            n.blanket = ctx.DrawSleeperBlanket();   // the same lump as IsoRim's own (one cache entry: a rebuild every frame would show in "built")
            n.ctxHash = SleeperBlanket.PartsHash(p);
            if (pl.HeadOnlyInBed && pl.Holder is Building_Bed inBed)
            {
                // (Api 68) the static call with IsoRim's own placing, as its documentation says to call it: the same lump, nothing rebuilt
                int built = SleeperBlanket.Built;
                n.staticAnswer = Api.DrawSleeperBlanket(p, inBed, pl.Position, pl.Yaw, pl.Size, pl.Position.y - pl.Lift) ? 1 : 0;
                n.staticBuilt += SleeperBlanket.Built - built;
                n.staticHash = SleeperBlanket.PartsHash(p);
            }
            else
            {
                n.staticAnswer = -1;
                n.staticHash = "-";
            }
            n.values = "Situation " + pl.Situation + " Position " + CV(pl.Position) + " Yaw " + pl.Yaw.ToString("0.00") + " Size " + pl.Size.ToString("0.0000")
                + " Lift " + pl.Lift.ToString("0.0000") + " HeadOnlyInBed " + pl.HeadOnlyInBed + " Holder " + n.holder
                + " BodyPlace " + MH(pl.BodyPlace) + " HeadPlace " + MH(pl.HeadPlace) + " ctx.DrawSleeperBlanket " + n.blanket + " parts " + n.ctxHash;
            return false;
        }

        // IsoRim's own figure, as placed for the drawing (the hook runs inside People.Draw after the drawer said no).
        private static void TestHook(FigureFrame frame)
        {
            Pawn p = frame.Pawn;
            DrawerNote n;
            if (p == null || !drawerNotes.TryGetValue(p, out n) || FigureScope.Active)
            {
                return;
            }
            Vector3 own = frame.Head.MultiplyPoint3x4(Vector3.zero);
            if (n.ownSeen && Flat(own - n.drawerHead) > 0.0005f)
            {
                n.mismatches++;
            }
            n.ownHead = own;
            n.ownSeen = true;
            n.frames++;
        }
    }
}
