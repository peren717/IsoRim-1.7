using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only probe keys for mechanoids and carried things (Mechs.cs, Carried.cs). Cells are map cells.
    //   clearzone=x0,z0,x1,z1         destroys the pawns (not colonists), corpses, items, filth and plants in the rectangle
    //   mech=x,z,<PawnKindDef>,<player|enemy>,<age>,<rot 0-3>[,<state>]  a new mech of that kind, faction and age facing rot
    //                                 (no overseer: the jobs below are started directly, as forced jobs); state: raid (an enemy in an assault lord), draft, down (damaged until downed), dead (killed), shutdown (self-shutdown job)
    //   attack=x0,z0,x1,z1            the pawn in the first cell given the job to fire at the thing (or cell) in the second
    //   undrafted=x,z,wait|tx,tz      the colonist in (x,z) undrafted and given a job that shows its weapon (the game's alwaysShowWeapon):
    //                                 wait = standing guard (Wait_Combat, the carry pose), or fire at the thing in (tx,tz) (AttackStatic)
    //   tipcheck=1                    logs where each aiming colonist's shots start (Projectiles.Muzzle), its drawn barrel tip and every
    //                                 projectile's drawn start (Projectiles.DrawnStart)
    //   rotupdate=1                   every colonist turned as the game turns it after a tick (toward what it aims at)
    //   aimcheck=1                    logs every standing colonist: aiming as the game draws it, the game's aim and the aim on a card
    //                                 (Patches.CardAim), map facing and the sprite facing seen
    //   haul=x,z,<ThingDef>,<count>,ix,iz  a new stack at (ix,iz) and the pawn in (x,z) given the job to haul it to storage
    //                                 (a real job; a stockpile must exist: zone=)
    //   order=x,z,<work>,tx,tz        the pawn in (x,z) given a real work job on (tx,tz): cut (a plant), mine, deconstruct,
    //                                 clean (filth made there first); the designation is added as the player would
    //   goto=x,z,tx,tz                the pawn in (x,z) given the job to walk to (tx,tz)
    //   jump=x,z,tx,tz                the pawn in (x,z) casts its jump ability (LongjumpMech*) at (tx,tz)
    //   walkfilm=N,k,tag,x,z,size[,follow|ticksM] N screenshots, k Unity frames apart (and each written before the next), the game
    //                                 running at normal speed meanwhile (renders\<harness tag>\walk_<tag>_<frame>.png), camera held at (x,z,size);
    //                                 with a 7th part "follow", centred on the pawns walking on a forced job instead
    //                                 (7th part ticksM, 8th part "paused": the game kept paused, exactly M ticks per frame, ticks0 = frozen)
    //   facecheck=1                   logs every pawn: map facing, step it is walking, sprite facing seen (View3D.SeenFacing), 3D model
    //   drawcheck=1                   logs every standing person: map facing, step, the facing its sprite is drawn with (all mods' patches)
    //   turrettarget=x,z,tx,tz        the turret comp of the pawn in (x,z) given the pawn (or cell) at (tx,tz) as its target
    //   mechcheck=1                   logs every mech and carrying pawn on the map (Mechs.Report)
    //   rot=x,z,rotting|dessicated    every corpse in the cell (and one held by the building there) rotted that far (CompRottable.RotImmediately)
    //   haulcorpse=x,z,ix,iz          the pawn in (x,z) given the job to haul the corpse at (ix,iz) to storage (a real job; zone= first)
    //   corpsecheck=1                 logs every corpse on the map: rot stage, how the game draws it, drawn as its model
    //   clearpawns=x0,z0,x1,z1        destroys every pawn in the rectangle, colonists too (carriers left from an earlier pass)
    public static partial class Probe
    {
        private static Pawn PawnAt(Map map, int x, int z)
        {
            IntVec3 c = new IntVec3(x, 0, z);
            Pawn p = c.InBounds(map) ? c.GetThingList(map).OfType<Pawn>().FirstOrDefault() : null;
            if (p == null)
            {
                Log.Warning("[IsoRim probe] no pawn at " + c);
            }
            return p;
        }

        private static bool MechKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "clearzone":
                {
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    List<Thing> gone = new List<Thing>();
                    foreach (IntVec3 c in r)
                    {
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        foreach (Thing t in c.GetThingList(map))
                        {
                            Pawn p = t as Pawn;
                            if ((p != null && !p.IsColonist) || t is Corpse || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth
                                || t.def.category == ThingCategory.Plant)
                            {
                                gone.Add(t);
                            }
                        }
                    }
                    foreach (Thing t in gone)
                    {
                        if (!t.Destroyed)
                        {
                            t.Destroy(DestroyMode.Vanish);
                        }
                    }
                    return true;
                }
                case "mech":
                {
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed(a[2]);
                    Faction f = a[3] == "enemy" ? Find.FactionManager.OfMechanoids : Faction.OfPlayer;
                    float age = F(a[4]);
                    Rand.PushState(Gen.HashCombineInt(I(a[0]), I(a[1]) * 7919 + 17));   // fixed seed per cell: the same mech every run
                    Pawn p;
                    try
                    {
                        p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, f, PawnGenerationContext.NonPlayer,
                            fixedBiologicalAge: age, fixedChronologicalAge: age));
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    Rot4 rot = new Rot4(I(a[5]));
                    GenSpawn.Spawn(p, new IntVec3(I(a[0]), 0, I(a[1])), map, rot);
                    p.jobs?.StopAll();
                    p.pather?.StopDead();
                    p.Rotation = rot;
                    string state = a.Length > 6 ? a[6] : "";
                    if (state == "draft" && p.drafter != null)
                    {
                        p.drafter.Drafted = true;
                    }
                    else if (state == "down")
                    {
                        HealthUtility.DamageUntilDowned(p, false);
                    }
                    else if (state == "dead")
                    {
                        p.Kill(null);
                    }
                    else if (state == "raid")
                    {
                        // An enemy that attacks the colony, as in a raid (a threat the colony's turrets go for).
                        Verse.AI.Group.LordMaker.MakeNewLord(p.Faction, new LordJob_AssaultColony(p.Faction), map, new List<Pawn> { p });
                    }
                    else if (state == "shutdown")
                    {
                        p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.SelfShutdown, p.Position), JobCondition.InterruptForced);
                    }
                    Log.Message("[IsoRim probe] mech " + kind.defName + " at " + p.PositionHeld + " (" + state + "): " + Mechs.Report(p));
                    return true;
                }
                case "attack":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[2]), 0, I(a[3]));
                    Thing target = tc.GetFirstBuilding(map) ?? (Thing)tc.GetThingList(map).OfType<Pawn>().FirstOrDefault();
                    Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target != null ? new LocalTargetInfo(target) : new LocalTargetInfo(tc));
                    job.playerForced = true;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    bool ok = p.CurJob == job;
                    Log.Message("[IsoRim probe] attack: " + p.LabelShort + " fires at " + (target != null ? target.def.defName : tc.ToString()) + ": " + ok);
                    return true;
                }
                case "undrafted":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job job;
                    if (a[2] == "wait")
                    {
                        job = JobMaker.MakeJob(JobDefOf.Wait_Combat);
                        job.expiryInterval = 100000;
                    }
                    else
                    {
                        IntVec3 tc = new IntVec3(I(a[2]), 0, I(a[3]));
                        Thing target = tc.GetFirstBuilding(map) ?? (Thing)tc.GetThingList(map).OfType<Pawn>().FirstOrDefault();
                        job = JobMaker.MakeJob(JobDefOf.AttackStatic, target != null ? new LocalTargetInfo(target) : new LocalTargetInfo(tc));
                    }
                    job.playerForced = true;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] undrafted: " + p.LabelShort + " " + job.def.defName + " (drafted " + p.Drafted + ", shows weapon " + job.def.alwaysShowWeapon + "): " + (p.CurJob == job));
                    return true;
                }
                case "rotupdate":
                    // Every colonist turned as the game turns it after a tick (Pawn_RotationTracker.UpdateRotation: toward what it aims
                    // at), which the harness's jumps of the clock skip.
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                    {
                        p.rotationTracker.UpdateRotation();
                    }
                    return true;
                case "aimcheck":
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                    {
                        // By reflection, so the probe also builds against a Patches without it (before/after runs).
                        System.Reflection.MethodInfo cardAim = HarmonyLib.AccessTools.Method(typeof(Patches), "CardAim");
                        if (cardAim == null)
                        {
                            Log.Message("[IsoRim probe] aimcheck " + p.LabelShort + " at " + p.Position + ": no Patches.CardAim in this build, facing " + p.Rotation
                                + " seen " + View3D.SeenFacing(p.Rotation));
                            continue;
                        }
                        object[] args = { p, false, 0f, 0f };
                        bool aiming = (bool)cardAim.Invoke(null, args);
                        float game = (float)args[2], seen = (float)args[3];
                        Log.Message("[IsoRim probe] aimcheck " + p.LabelShort + " at " + p.Position + " job " + (p.CurJob != null ? p.CurJob.def.defName : "-")
                            + " stance " + (p.stances != null && p.stances.curStance != null ? p.stances.curStance.GetType().Name : "-")
                            + " aiming " + aiming + " game " + game.ToString("0.0") + " card " + seen.ToString("0.0")
                            + " facing " + p.Rotation + " seen " + (aiming ? Rot4.FromAngleFlat(seen) : View3D.SeenFacing(p.Rotation))
                            + " yaw " + View3D.Yaw.ToString("0") + " pitch " + View3D.Pitch.ToString("0.0"));
                    }
                    return true;
                case "tipcheck":
                {
                    // Each standing colonist that aims: the muzzle shots start from (Projectiles.Muzzle) and, where the build has
                    // it, the barrel tip as last drawn (Projectiles.LastTip, by reflection for builds without it), in cells; and
                    // every projectile in flight: where its drawn flight starts (Projectiles.DrawnStart).
                    System.Reflection.MethodInfo lastTip = HarmonyLib.AccessTools.Method(typeof(Projectiles), "LastTip");
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                    {
                        Stance_Busy busy = p.stances != null ? p.stances.curStance as Stance_Busy : null;
                        if (busy == null || !busy.focusTarg.IsValid || p.equipment == null || p.equipment.Primary == null)
                        {
                            continue;
                        }
                        Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
                        Vector3 d = Floors.FlatDelta(p.DrawPos, to);
                        d = d.sqrMagnitude > 0.001f ? d.normalized : Vector3.zero;
                        Vector3 m = Projectiles.Muzzle(p, p.equipment.Primary.def, p.DrawPos, d);
                        string tip = "-";
                        if (lastTip != null)
                        {
                            object[] args = { p, Vector3.zero, 0 };
                            if ((bool)lastTip.Invoke(null, args))
                            {
                                Vector3 t = (Vector3)args[1];
                                tip = t.x.ToString("0.000") + "," + t.y.ToString("0.000") + "," + t.z.ToString("0.000") + " age " + args[2];
                            }
                        }
                        // The muzzle on screen (pixels from the top left) with the camera as it is now, and where the camera is.
                        Vector3 w = new Vector3(m.x, p.DrawPos.y + m.y / View3D.Squash, m.z);
                        Floors.Drawn(ref w);
                        Vector3 sp = Find.Camera.WorldToScreenPoint(w);
                        Log.Message("[IsoRim probe] tipcheck " + p.LabelShort + " at " + p.Position + " muzzle " + m.x.ToString("0.000") + "," + m.y.ToString("0.000") + "," + m.z.ToString("0.000")
                            + " drawn tip " + tip + " yaw " + View3D.Yaw.ToString("0") + " px " + sp.x.ToString("0.0") + "," + (Screen.height - sp.y).ToString("0.0")
                            + " cam " + Find.CameraDriver.MapPosition + " frame " + Time.frameCount);
                    }
                    foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
                    {
                        if (t is Projectile pr)
                        {
                            Vector3 st = Projectiles.DrawnStart(pr);
                            Log.Message("[IsoRim probe] tipcheck projectile " + pr.def.defName + " from " + (pr.Launcher != null ? pr.Launcher.LabelShort : "-") + " drawn start "
                                + st.x.ToString("0.000") + "," + st.y.ToString("0.000") + "," + st.z.ToString("0.000"));
                        }
                    }
                    return true;
                }
                case "haul":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    Thing item = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(a[2]));
                    item.stackCount = Mathf.Clamp(I(a[3]), 1, item.def.stackLimit);
                    GenSpawn.Spawn(item, new IntVec3(I(a[4]), 0, I(a[5])), map);
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job job = HaulAIUtility.HaulToStorageJob(p, item, false);
                    if (job == null)
                    {
                        Log.Warning("[IsoRim probe] haul: no storage for " + item.def.defName);
                        return true;
                    }
                    job.playerForced = true;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] haul: " + p.LabelShort + " at " + p.Position + " hauls " + item.LabelCap + " from " + item.Position + " (" + job.def.defName + ")");
                    return true;
                }
                case "order":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[3]), 0, I(a[4]));
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    Job job = null;
                    switch (a[2])
                    {
                        case "cut":
                        {
                            Plant plant = tc.GetPlant(map);
                            if (plant != null)
                            {
                                map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                                job = JobMaker.MakeJob(JobDefOf.CutPlantDesignated, plant);
                            }
                            break;
                        }
                        case "mine":
                        {
                            Thing rock = tc.GetEdifice(map);
                            if (rock != null)
                            {
                                map.designationManager.AddDesignation(new Designation(tc, DesignationDefOf.Mine));
                                job = JobMaker.MakeJob(JobDefOf.Mine, rock);
                            }
                            break;
                        }
                        case "deconstruct":
                        {
                            Thing b = tc.GetEdifice(map);
                            if (b != null)
                            {
                                map.designationManager.AddDesignation(new Designation(b, DesignationDefOf.Deconstruct));
                                job = JobMaker.MakeJob(JobDefOf.Deconstruct, b);
                            }
                            break;
                        }
                        case "clean":
                        {
                            Filth filth = (Filth)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Filth_Dirt), tc, map);
                            filth.thickness = filth.def.filth.maxThickness;
                            map.areaManager.Home[tc] = true;
                            job = JobMaker.MakeJob(JobDefOf.Clean);
                            job.AddQueuedTarget(TargetIndex.A, filth);
                            break;
                        }
                    }
                    if (job != null)
                    {
                        job.playerForced = true;
                        p.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    bool ok = job != null && p.CurJob == job;
                    Log.Message("[IsoRim probe] order " + a[2] + ": " + p.LabelShort + " at " + p.Position + " -> " + tc + ": " + ok);
                    return true;
                }
                case "goto":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null)
                    {
                        return true;
                    }
                    Job gj = JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(I(a[2]), 0, I(a[3])));
                    gj.playerForced = true;
                    p.jobs.StartJob(gj, JobCondition.InterruptForced);
                    bool ok = p.CurJob == gj;
                    Log.Message("[IsoRim probe] goto: " + p.LabelShort + " at " + p.Position + ": " + ok);
                    return true;
                }
                case "jump":
                {
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    if (p == null || p.abilities == null)
                    {
                        return true;
                    }
                    Ability jump = p.abilities.AllAbilitiesForReading.FirstOrDefault(x => x.def.defName.StartsWith("Longjump"));
                    if (jump == null)
                    {
                        Log.Warning("[IsoRim probe] jump: " + p.LabelShort + " has no jump ability");
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[2]), 0, I(a[3]));
                    Job jj = jump.GetJob(new LocalTargetInfo(tc), new LocalTargetInfo(tc));
                    jj.playerForced = true;
                    p.jobs.StartJob(jj, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] jump: " + p.LabelShort + " " + jump.def.defName + " -> " + tc + ", job " + (p.CurJob != null ? p.CurJob.def.defName : "none"));
                    return true;
                }
                case "turrettarget":
                {
                    // The CompTurretGun of the pawn in (x,z) given the thing (or cell) at (tx,tz) as its target, as its own
                    // target search would; the game's tick then turns and fires it (the search itself did not pick our
                    // harness raiders as threats).
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    CompTurretGun tg = p != null ? p.TryGetComp<CompTurretGun>() : null;
                    if (tg == null)
                    {
                        return true;
                    }
                    IntVec3 tc = new IntVec3(I(a[2]), 0, I(a[3]));
                    Thing tt = tc.GetThingList(map).OfType<Pawn>().FirstOrDefault();
                    HarmonyLib.Traverse.Create(tg).Field("currentTarget").SetValue(tt != null ? new LocalTargetInfo(tt) : new LocalTargetInfo(tc));
                    HarmonyLib.Traverse.Create(tg).Field("burstWarmupTicksLeft").SetValue(60);
                    Log.Message("[IsoRim probe] turrettarget: " + p.LabelShort + " -> " + (tt != null ? tt.LabelShort : tc.ToString()));
                    return true;
                }
                case "walkfilm":
                    walkLeft = I(a[0]);
                    walkTicks = Mathf.Clamp(I(a[1]), 2, 60);
                    walkTag = a[2];
                    walkCam = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                    walkFrame = 0;
                    walkStep = 0;
                    walkPending = null;
                    walkFollow = a.Length > 6 && a[6] == "follow";
                    walkExtraTicks = a.Length > 6 && a[6].StartsWith("ticks") ? I(a[6].Substring(5)) : 0;
                    walkTrack = a.Length > 7 && a[7] == "track";
                    walkPaused = a.Length > 7 && a[7] == "paused";
                    return true;
                case "facecheck":
                    // Each pawn: map facing, the way it is walking, the sprite facing the camera sees, 3D model or not.
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        IntVec3 step = p.pather != null && p.pather.MovingNow ? p.pather.nextCell - p.Position : IntVec3.Zero;
                        Log.Message("[IsoRim probe] facecheck " + p.def.defName + " at " + p.Position + " yaw " + View3D.Yaw.ToString("0") + " rot " + p.Rotation
                            + " step " + step.x + "," + step.z + " seen " + View3D.SeenFacing(p.Rotation) + " posture " + p.GetPosture()
                            + " model " + Patches.DrawnAsModel(p));
                    }
                    return true;
                case "drawcheck":
                {
                    // Each standing person: map facing, the way it is walking, and the facing its sprite is drawn with as the game works
                    // it out ahead of drawing (ParallelGetPreRenderResults, with every mod's patches and IsoRim's camera turn).
                    System.Reflection.MethodInfo pre = HarmonyLib.AccessTools.Method(typeof(PawnRenderer), "ParallelGetPreRenderResults");
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if (pre == null || p.RaceProps == null || !p.RaceProps.Humanlike || p.Dead || p.GetPosture() != PawnPosture.Standing)
                        {
                            continue;
                        }
                        IntVec3 step = p.pather != null && p.pather.MovingNow ? p.pather.nextCell - p.Position : IntVec3.Zero;
                        object drawn;
                        try
                        {
                            p.Drawer.renderer.EnsureGraphicsInitialized();
                            object res = pre.Invoke(p.Drawer.renderer, new object[] { p.DrawPos, null, false, true });
                            object parms = HarmonyLib.Traverse.Create(res).Field("parms").GetValue();
                            drawn = parms != null ? HarmonyLib.Traverse.Create(parms).Field("facing").GetValue() : null;
                        }
                        catch (Exception)
                        {
                            drawn = "(not drawn yet)";
                        }
                        Log.Message("[IsoRim probe] drawcheck " + p.LabelShort + " at " + p.Position + " yaw " + View3D.Yaw.ToString("0") + " rot " + p.Rotation
                            + " step " + step.x + "," + step.z + " drawn " + drawn + " seen " + View3D.SeenFacing(p.Rotation) + " model " + Patches.DrawnAsModel(p));
                    }
                    return true;
                }
                case "rot":
                {
                    // Every corpse in the cell, and one held by the building there (gibbet cage), rotted that far the game's way.
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    RotStage stage = a[2] == "dessicated" ? RotStage.Dessicated : RotStage.Rotting;
                    List<Corpse> found = c.GetThingList(map).OfType<Corpse>().ToList();
                    foreach (Thing t in c.GetThingList(map))
                    {
                        IThingHolder h = t as IThingHolder;
                        if (h != null && !(t is Pawn) && !(t is Corpse))
                        {
                            found.AddRange(h.GetDirectlyHeldThings().OfType<Corpse>());
                        }
                    }
                    foreach (Corpse corpse in found)
                    {
                        CompRottable rot = corpse.TryGetComp<CompRottable>();
                        if (rot != null)
                        {
                            rot.RotImmediately(stage);
                        }
                        Log.Message("[IsoRim probe] rot " + corpse.InnerPawn.def.defName + " at " + c + ": " + corpse.CurRotDrawMode);
                    }
                    if (found.Count == 0)
                    {
                        Log.Warning("[IsoRim probe] rot: no corpse at " + c);
                    }
                    return true;
                }
                case "haulcorpse":
                {
                    // The pawn in (x,z) given the job to haul the corpse at (ix,iz) to storage (a real job; zone= first).
                    Pawn p = PawnAt(map, I(a[0]), I(a[1]));
                    Corpse corpse = new IntVec3(I(a[2]), 0, I(a[3])).GetThingList(map).OfType<Corpse>().FirstOrDefault();
                    if (p == null || corpse == null)
                    {
                        Log.Warning("[IsoRim probe] haulcorpse: no pawn or no corpse");
                        return true;
                    }
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    foreach (Zone_Stockpile zone in map.zoneManager.AllZones.OfType<Zone_Stockpile>())
                    {
                        zone.settings.filter.SetAllow(corpse.def, true);   // as the player ticks the corpse in the stockpile's filter
                    }
                    Job job = HaulAIUtility.HaulToStorageJob(p, corpse, false);
                    if (job == null)
                    {
                        Log.Warning("[IsoRim probe] haulcorpse: no storage for " + corpse.LabelCap);
                        return true;
                    }
                    job.playerForced = true;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    Log.Message("[IsoRim probe] haulcorpse: " + p.LabelShort + " at " + p.Position + " hauls " + corpse.LabelCap + " from " + corpse.Position);
                    return true;
                }
                case "clearpawns":
                {
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if (r.Contains(p.Position) && !p.Destroyed)
                        {
                            p.Destroy(DestroyMode.Vanish);
                        }
                    }
                    return true;
                }
                case "corpsecheck":
                    foreach (Corpse c in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().ToList())
                    {
                        Pawn inner = c.InnerPawn;
                        Thing holder = c.ParentHolder as Thing;
                        Log.Message("[IsoRim probe] corpsecheck " + inner.def.defName + " at " + c.PositionHeld + " in " + (holder != null ? holder.def.defName : (c.ParentHolder is Pawn_CarryTracker ? "hands" : "map"))
                            + ": rot " + c.CurRotDrawMode + ", renderer " + Corpses.RotOf(inner) + ", drawn as model " + Patches.DrawnAsModel(inner));
                    }
                    return true;
                case "mechcheck":
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if ((p.RaceProps != null && p.RaceProps.IsMechanoid) || (p.carryTracker != null && p.carryTracker.CarriedThing != null))
                        {
                            Log.Message("[IsoRim probe] mechcheck " + p.def.defName + " " + p.LabelShort + " at " + p.Position + " rot " + p.Rotation + ": "
                                + (p.RaceProps.IsMechanoid ? Mechs.Report(p) : "carrying " + p.carryTracker.CarriedThing.LabelCap));
                        }
                    }
                    foreach (Corpse c in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>())
                    {
                        if (c.InnerPawn != null && c.InnerPawn.RaceProps.IsMechanoid)
                        {
                            Log.Message("[IsoRim probe] mechcheck corpse " + c.InnerPawn.def.defName + " at " + c.Position + ": " + Mechs.Report(c.InnerPawn));
                        }
                    }
                    return true;
            }
            return false;
        }

        private static int walkLeft;
        private static int walkTicks;
        private static int walkFrame;
        private static string walkTag;
        private static Vector3 walkCam;
        private static bool walkFollow;
        private static bool walkTrack;       // 8th part "track": centred on the first entity bound to a model
        private static bool walkPaused;      // 8th part "paused": the game stays paused, only the ticksM ticks run (exact steps)
        private static int walkExtraTicks;   // 7th part "ticks<N>": N more game ticks run before each film frame (slow states)

        // One film frame every few Unity frames: the game ticks and the camera is set, the picture is taken two
        // frames later, and the next frame waits until that picture has been written (the capture is not instant,
        // and a capture asked for while one is pending repeats the old picture).
        private static int walkStep;
        private static string walkPending;

        private static void WalkFilmTick()
        {
            if (walkLeft <= 0)
            {
                return;
            }
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            }
            if (walkPending != null)
            {
                if (!System.IO.File.Exists(walkPending))
                {
                    return;
                }
                walkPending = null;
                walkLeft--;
                walkStep = 0;
                if (walkLeft <= 0)
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                }
                return;
            }
            if (walkStep == 0)
            {
                // The game runs at normal speed through the film (paused, the game does not move pawns' drawn
                // positions on however many ticks are run).
                Find.TickManager.CurTimeSpeed = walkPaused ? TimeSpeed.Paused : TimeSpeed.Normal;
                for (int i = 0; i < walkExtraTicks; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                Vector3 cam = new Vector3(walkCam.x, 0f, walkCam.y);
                if (walkFollow && Find.CurrentMap != null)
                {
                    // The middle of the pawns walking on a forced job (goto=, haul=).
                    Vector3 sum = Vector3.zero;
                    int n = 0;
                    foreach (Pawn p in Find.CurrentMap.mapPawns.AllPawnsSpawned)
                    {
                        if (p.CurJob != null && p.CurJob.playerForced && p.pather != null && p.pather.Moving)
                        {
                            sum += p.DrawPos;
                            n++;
                        }
                    }
                    if (n > 0)
                    {
                        cam = new Vector3(sum.x / n, 0f, sum.z / n);
                    }
                }
                if (walkTrack && Find.CurrentMap != null)
                {
                    Pawn e = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(Bound);
                    if (e != null)
                    {
                        cam = new Vector3(e.DrawPos.x, 0f, e.DrawPos.z);
                    }
                }
                Find.CameraDriver.SetRootPosAndSize(cam, walkCam.z);
                walkStep = 1;
                return;
            }
            if (walkStep < walkTicks)
            {
                walkStep++;
                return;
            }
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            walkPending = System.IO.Path.Combine(dir, "walk_" + walkTag + "_" + walkFrame.ToString("000") + ".png");
            ScreenCapture.CaptureScreenshot(walkPending);
            Log.Message("[IsoRim probe] walkfilm " + walkTag + " frame " + walkFrame + " (Unity frame " + Time.frameCount + ", tick " + Find.TickManager.TicksGame + ")" + EntityFilmNote());
            walkFrame++;
        }
    }
}
