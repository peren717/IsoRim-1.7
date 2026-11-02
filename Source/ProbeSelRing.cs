using System.Collections.Generic;
using System.Linq;
using System.Xml;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for selection rings (SelectionRings, Api 48; selring, 2026-11-01). Cells are map cells (U: upper floor).
    //   rpawn=x,z,<role>[,<PawnKind>][,rot]  a pawn on a fixed seed per cell: colonist (drafted, stands still), hostile (a pirate),
    //                          visitor (a non-hostile faction's), prisoner, slave (of the colony; a pirate before), tame / wild
    //                          (an animal of the kind, adult), mech (the kind, the colony's)
    //   rlink=mx,mz,ox,oz      the mech in (mx,mz) gets the colonist in (ox,oz) as overseer (a mechlink put in first)
    //   rbed=px,pz,bx,bz[,prison] the pawn in (px,pz) tired and given the game's LayDown job on the bed in (bx,bz) (prison: the
    //                          bed made a prison bed first; auto: no job, their own think tree takes them to bed); runtilbed= ticks them in
    //   runtilbed=N            game ticks until every pawn given rbed lies in its bed (at most N)
    //   rlook=x,z,<mode>       a harness Api 48 rule gives the pawn in (x,z): blue (colour), big (radius x1.6), square / hex (a
    //                          ring picture made here), hide, none (passes)
    //   rxml=<ThingDef>        an IsoRim.SelectionRing mod extension read from XML (DirectXmlToObject, as the game loads defs)
    //                          added to that race: colour (0.65,0.35,1), radiusScale 1.3, texPath of a game ring picture (ActivatorProximityRadius)
    //   ringcheck=N            N frames later: every selected pawn's ring (Api.SelectionRingOf, the look, where its note came from)
    //                          and how many rings were drawn in that frame
    public static partial class Probe
    {
        private static readonly List<KeyValuePair<Pawn, Building_Bed>> ringBeds = new List<KeyValuePair<Pawn, Building_Bed>>();
        private static readonly Dictionary<Pawn, SelectionRing> ringLooks = new Dictionary<Pawn, SelectionRing>();
        private static bool ringRuleAdded;
        private static int ringCheckAt = -1;
        private static Texture2D ringSquare, ringHex;

        private static Faction RingFaction(bool hostile)
        {
            if (hostile)
            {
                return Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate)
                    ?? Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction && !f.Hidden);
            }
            return Find.FactionManager.AllFactions.Where(f => !f.IsPlayer && !f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction && !f.Hidden && !f.temporary)
                .OrderBy(f => f.loadID).FirstOrDefault();
        }

        private static bool RingKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "rpawn":
                {
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    string role = a[2];
                    PawnKindDef kind = a.Length > 3 && a[3] != "-" ? DefDatabase<PawnKindDef>.GetNamedSilentFail(a[3]) : null;
                    Rot4 rot = a.Length > 4 ? new Rot4(I(a[4])) : Rot4.South;
                    bool animal = role == "tame" || role == "wild";
                    Faction fac = role == "colonist" || role == "tame" || role == "mech" ? Faction.OfPlayer
                        : role == "visitor" ? RingFaction(false) : role == "wild" ? null : RingFaction(true);
                    if (kind == null)
                    {
                        kind = role == "colonist" ? PawnKindDefOf.Colonist : fac != null && fac.def.basicMemberKind != null ? fac.def.basicMemberKind : PawnKindDefOf.Colonist;
                    }
                    Rand.PushState(Gen.HashCombineInt(c.x, c.z * 7919 + 41));   // fixed seed per cell: the same pawn every run
                    Pawn p;
                    try
                    {
                        PawnGenerationRequest req = animal || role == "mech"
                            ? new PawnGenerationRequest(kind, fac, fixedBiologicalAge: 5f, fixedChronologicalAge: 5f)
                            : new PawnGenerationRequest(kind, fac, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                                mustBeCapableOfViolence: true, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, developmentalStages: DevelopmentalStage.Adult);
                        p = PawnGenerator.GeneratePawn(req);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    GenSpawn.Spawn(p, c, map, rot);
                    if (role == "colonist" && p.drafter != null)
                    {
                        p.drafter.Drafted = true;   // stands still facing rot
                    }
                    if (role == "prisoner" || role == "slave")
                    {
                        p.guest.SetGuestStatus(Faction.OfPlayer, role == "slave" ? GuestStatus.Slave : GuestStatus.Prisoner);
                    }
                    p.Rotation = rot;
                    Log.Message("[IsoRim probe] rpawn " + role + " " + p.kindDef.defName + " " + p.LabelShort + " at " + p.Position + " faction "
                        + (p.Faction != null ? p.Faction.Name : "none") + " prisoner " + p.IsPrisoner + " slave " + p.IsSlave + " 3D " + Patches.DrawnAsModel(p));
                    return true;
                }
                case "rlink":
                {
                    Pawn mech = PawnAt(map, I(a[0]), I(a[1]));
                    Pawn over = PawnAt(map, I(a[2]), I(a[3]));
                    if (mech == null || over == null || !ModsConfig.BiotechActive)
                    {
                        return true;
                    }
                    if (!over.health.hediffSet.HasHediff(HediffDefOf.MechlinkImplant))
                    {
                        over.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf.MechlinkImplant, over), over.health.hediffSet.GetBrain());
                    }
                    over.relations.AddDirectRelation(PawnRelationDefOf.Overseer, mech);
                    Log.Message("[IsoRim probe] rlink " + mech.LabelShort + " overseer " + over.LabelShort + ": colony mech " + mech.IsColonyMech
                        + ", needs a mechanitor " + mech.IsColonyMechRequiringMechanitor());
                    return true;
                }
                case "rbed":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    Building_Bed bed = map.thingGrid.ThingsListAt(new IntVec3(I(a[2]), 0, I(a[3]))).OfType<Building_Bed>().FirstOrDefault();
                    if (p == null || bed == null)
                    {
                        Log.Warning("[IsoRim probe] rbed: no pawn or bed");
                        return true;
                    }
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();   // walls placed this frame: the room is there for the bed's checks
                    if (a.Length > 4 && a[4] == "prison")
                    {
                        bed.ForPrisoners = true;
                        bed.GetRoom()?.Notify_RoomShapeChanged();   // the room counts as a prison cell now (as the game works it out when a room changes)
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    if (p.needs != null && p.needs.rest != null)
                    {
                        p.needs.rest.CurLevel = 0.05f;
                    }
                    CompAssignableToPawn assign = bed.TryGetComp<CompAssignableToPawn>();
                    if (assign != null)
                    {
                        assign.TryAssignPawn(p);   // their own bed, as the player assigns it
                    }
                    if (a.Length > 5 && a[5] == "auto")
                    {
                        p.jobs.EndCurrentJob(JobCondition.InterruptForced);   // tired: their own think tree finds the bed (a prisoner's rest)
                    }
                    else
                    {
                        Job job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
                        p.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    ringBeds.Add(new KeyValuePair<Pawn, Building_Bed>(p, bed));
                    Log.Message("[IsoRim probe] rbed " + p.LabelShort + " -> " + bed.def.defName + " " + bed.Position + " prison " + bed.ForPrisoners + ": " + (p.CurJobDef != null ? p.CurJobDef.defName : "none"));
                    return true;
                }
                case "runtilbed":
                {
                    int limit = Mathf.Clamp(I(a[0]), 1, 3000), n = 0;
                    System.Func<bool> inBed = () => ringBeds.All(kv => kv.Key.CurrentBed() == kv.Value && kv.Key.GetPosture() != PawnPosture.Standing);
                    Rand.PushState(Gen.HashCombineInt(limit, 47));   // the ticks on a fixed seed
                    try
                    {
                        while (n < limit && !inBed())
                        {
                            Find.TickManager.DoSingleTick();
                            n++;
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();
                    }
                    Log.Message("[IsoRim probe] runtilbed " + n + " ticks, in bed " + inBed() + ": "
                        + string.Join(", ", ringBeds.Select(kv => kv.Key.LabelShort + " " + kv.Key.GetPosture() + " at " + kv.Key.Position + " job " + (kv.Key.CurJobDef != null ? kv.Key.CurJobDef.defName : "none") + " bed " + (kv.Key.CurrentBed() != null) + " prisonCell " + kv.Value.GetRoom()?.IsPrisonCell + " downed " + kv.Key.Downed)));
                    return true;
                }
                case "rlook":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    if (!ringRuleAdded)
                    {
                        ringRuleAdded = true;
                        Api.AddSelectionRingRule(new System.Func<Pawn, SelectionRing>(RingRule));
                    }
                    SelectionRing look;
                    switch (a[2])
                    {
                        case "blue": look = new SelectionRing { color = new Color(0.25f, 0.55f, 1f) }; break;
                        case "big": look = new SelectionRing { radiusScale = 1.6f }; break;
                        case "square": look = new SelectionRing { texture = RingPicture(false) }; break;
                        case "hex": look = new SelectionRing { texture = RingPicture(true), color = new Color(1f, 0.85f, 0.2f) }; break;
                        case "hide": look = new SelectionRing { hide = true }; break;
                        default: look = null; break;
                    }
                    if (look == null)
                    {
                        ringLooks.Remove(p);
                    }
                    else
                    {
                        ringLooks[p] = look;
                    }
                    Log.Message("[IsoRim probe] rlook " + p.LabelShort + " " + a[2]);
                    return true;
                }
                case "rxml":
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a[0]);
                    if (def == null)
                    {
                        Log.Warning("[IsoRim probe] rxml: no ThingDef " + a[0]);
                        return true;
                    }
                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml("<li Class=\"IsoRim.SelectionRing\"><color>(0.65, 0.35, 1)</color><radiusScale>1.3</radiusScale><texPath>Things/Mote/ActivatorProximityRadius</texPath></li>");
                    DefModExtension ext = DirectXmlToObject.ObjectFromXml<DefModExtension>(doc.DocumentElement, true);
                    if (def.modExtensions == null)
                    {
                        def.modExtensions = new List<DefModExtension>();
                    }
                    def.modExtensions.RemoveAll(e => e is SelectionRing);
                    def.modExtensions.Add(ext);
                    SelectionRingRules.Forget();
                    SelectionRing sr = ext as SelectionRing;
                    Log.Message("[IsoRim probe] rxml " + def.defName + ": " + (sr != null ? "colour " + sr.color + " scale " + sr.radiusScale + " tex " + sr.texPath + " loaded " + (sr.Texture != null) : "not a SelectionRing: " + ext));
                    return true;
                }
                case "ringcheck":
                    ringCheckAt = Time.frameCount + Mathf.Clamp(I(a[0]), 1, 600);
                    return true;
            }
            return false;
        }

        private static SelectionRing RingRule(Pawn p)
        {
            SelectionRing look;
            return ringLooks.TryGetValue(p, out look) ? look : null;
        }

        // A white ring picture made here: a square outline, or a hexagon outline (a mod's own shape, Api 48).
        private static Texture2D RingPicture(bool hex)
        {
            Texture2D cached = hex ? ringHex : ringSquare;
            if (cached != null)
            {
                return cached;
            }
            const int n = 128;
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float d;
                    if (hex)
                    {
                        float ax = Mathf.Abs(u), az = Mathf.Abs(v);
                        d = Mathf.Max(az, ax * 0.866f + az * 0.5f) / 0.95f;   // pointy sides east and west
                    }
                    else
                    {
                        d = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v)) / 0.95f;
                    }
                    bool on = d <= 1f && d >= 0.84f;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(on ? 235 : 0));
                }
            }
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, true, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Trilinear;
            t.SetPixels32(px);
            t.Apply(true);
            if (hex)
            {
                ringHex = t;
            }
            else
            {
                ringSquare = t;
            }
            return t;
        }

        // Every frame (Sticky): the ring report when it is due.
        private static void RingSticky(Map map)
        {
            if (ringCheckAt < 0 || Time.frameCount < ringCheckAt)
            {
                return;
            }
            ringCheckAt = -1;
            int sel = 0;
            foreach (object o in Find.Selector.SelectedObjects)
            {
                Pawn p = o as Pawn;
                if (p == null)
                {
                    continue;
                }
                sel++;
                Color col;
                float rad;
                bool drawn = Api.SelectionRingOf(p, out col, out rad);
                SelectionRings.Ring r = SelectionRings.LookOf(p);
                Log.Message("[IsoRim probe] ringcheck " + p.LabelShort + " " + p.def.defName + " at " + p.Position + " posture " + p.GetPosture() + " bed " + (p.CurrentBed() != null)
                    + " drawn " + drawn + " colour " + ColorUtility.ToHtmlStringRGB(col) + " radius " + rad.ToString("0.000") + " noted " + r.noted
                    + " centre (" + r.x.ToString("0.00") + ", " + r.z.ToString("0.00") + ") y " + r.y.ToString("0.000") + " picture " + (r.texture != null ? r.texture.name : "built-in")
                    + " card " + Api.IsCard(p) + " 3D " + Patches.DrawnAsModel(p));
            }
            Log.Message("[IsoRim probe] ringcheck selected pawns " + sel + ", rings drawn in frame " + SelectionRings.FrameOf + ": "
                + (SelectionRings.FrameOf >= Time.frameCount - 2 ? SelectionRings.InFrame : 0) + " (total " + SelectionRings.Drawn + ", hidden " + SelectionRings.Hidden
                + "), setting " + IsoRimMod.Settings.selectionRings + ", 3D " + View3D.Ready);
        }
    }
}
