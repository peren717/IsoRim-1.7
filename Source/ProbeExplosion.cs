using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only checks for Api 74 (3D explosions, ExplosionFx), real flow (pawn jobs, the game's ticks):
    //   explscene=<label>,throw,ax,az,tx,tz,<ages>     the colonist in cell ax,az is given a frag grenade, drafted and ordered to
    //                                                  throw it at the pawn in cell tx,tz (else the cell): AttackStatic, its own
    //                                                  verb, warm-up, flight, the grenade's fuse
    //   explscene=<label>,mortar,ax,az,tx,tz,<ages>[,<shell>]   a mortar is built at ax,az (the player's), loaded with a shell
    //                                                  (default Shell_HighExplosive), manned by the nearest free colonist (ManTurret)
    //                                                  and ordered to fire at tx,tz (OrderAttack)
    //     then the game ticks until the explosion starts; at each age in ticks (colon list, e.g. 0:3:9:18:30:60:240:480) a
    //     screenshot renders\<tag>\expl_<label>_<age>.png with the camera on the explosion; every fleck the game makes from the
    //     order on is logged ("explrec <label> F <def> x y z s a v r" for each fleck made, "H <def>" for each held back,
    //     "lifted" when BodyFlecks stood it on a figure) and a summary line with the Api.Dev counters.
    //   explscene=<label>,throw,ax,az,tx,tz,<ages>,up1   the same with both pawns first moved up their columns to ProjectZ
    //                                                  floor 1 (lay a slab there first: pzslab=x0,z0,x1,z1)
    //   explscene=<label>,launch,ax,az,tx,tz,<ages>,<projectile def>   that projectile launched from ax,az at tx,tz by the game's
    //                                                  own Projectile.Launch (the colonist nearest as launcher)
    //   explscene=<label>,wick,ax,az,tx,tz,<ages>,<explosive def>   that explosive (e.g. TrapIED_AntigrainWarhead) built at tx,tz
    //                                                  and its wick lit: the game's own CompExplosive.Detonate; effecters logged
    //   explcam=<size>                                camera root size round the explosion for the shots (default 7)
    //   explreload=<label>,ax,az,tx,tz                 quickload check (see ExplReloadScript)
    //   explskip=<DamageDef>                          Api.SkipExplosionFx
    //   explfilter=weapon,<ThingDef>                  Api.AddExplosionFxFilter: explosions whose weapon is that def stay flat
    //   explfilter=throw                              a filter that throws (fail-soft: warned once, counts as "don't skip")
    //   Runs on the Api 17 frame script runner (FwStart), all within the pass (Capture17 jumps the clock between passes).
    public static partial class Probe
    {
        private const int ExplMaxTicks = 2400;      // the order to the explosion, at most
        private const int ExplTicksPerFrame = 60;   // while waiting for it
        private static float ExplCamSize = 7f;      // camera root size round the explosion (cells); explcam= sets it
        private const string ExplReloadSave = "_agent_explreload";   // the quickload check's save (the runner deletes it after)
        private const int ExplSettle = 3;           // frames before a screenshot

        private static bool explRecPatched;
        private static string explRec;              // label while recording
        private static int explLiftedSeen;
        private static readonly Dictionary<string, int> explMade = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> explHeldDefs = new Dictionary<string, int>();
        private static int explLifted;

        private static bool ExplosionKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "explscene":
                    Api17Patch();
                    ExplRecPatch();
                    FwStart(ExplScript(a, map));
                    return true;
                case "explskip":
                    Api.SkipExplosionFx(DefDatabase<DamageDef>.GetNamed(a[0]));
                    return true;
                case "explcam":
                    ExplCamSize = F(a[0]);
                    return true;
                case "explreload":
                    Api17Patch();
                    ExplRecPatch();
                    FwStart(ExplReloadScript(a, map));
                    return true;
                case "explfilter":
                    if (a[0] == "weapon")
                    {
                        ThingDef w = DefDatabase<ThingDef>.GetNamed(a[1]);
                        Api.AddExplosionFxFilter(e => e.weapon == w);
                    }
                    else if (a[0] == "throw")
                    {
                        Api.AddExplosionFxFilter(e => throw new InvalidOperationException("probe: a filter that fails"));
                    }
                    return true;
                default:
                    return false;
            }
        }

        private static void ExplRecPatch()
        {
            if (explRecPatched)
            {
                return;
            }
            explRecPatched = true;
            Harmony h = new Harmony("peren.isorim17.probe.explrec");
            // the game's random state right after an explosion starts and after each of its ticks (Rand parity, option on vs off)
            h.Patch(AccessTools.Method(typeof(Explosion), nameof(Explosion.StartExplosion)), postfix: new HarmonyMethod(typeof(Probe), nameof(ExplRand_Start)));
            h.Patch(AccessTools.Method(typeof(Explosion), "Tick"), postfix: new HarmonyMethod(typeof(Probe), nameof(ExplRand_Tick)));
            // effecters triggered while recording (an explosive's own explosionEffect, e.g. GiantExplosion, kept)
            h.Patch(AccessTools.Method(typeof(Effecter), nameof(Effecter.Trigger)), postfix: new HarmonyMethod(typeof(Probe), nameof(ExplEffecter_Postfix)));
            h.Patch(AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) }),
                prefix: new HarmonyMethod(typeof(Probe), nameof(ExplRec_Prefix)) { priority = Priority.Last - 1 });
        }

        // After every other CreateFleck prefix (BodyFlecks' too): what the game makes, as made.
        public static void ExplRec_Prefix(ref FleckCreationData fleckData, bool __runOriginal)
        {
            if (explRec == null || fleckData.def == null)
            {
                return;
            }
            string d = fleckData.def.defName;
            bool lifted = BodyFlecks.Lifted != explLiftedSeen;
            explLiftedSeen = BodyFlecks.Lifted;
            if (!__runOriginal)
            {
                explHeldDefs.TryGetValue(d, out int hn);
                explHeldDefs[d] = hn + 1;
                Log.Message("[IsoRim probe] explrec " + explRec + " H " + d + " tick " + Find.TickManager.TicksGame);
                return;
            }
            explMade.TryGetValue(d, out int n);
            explMade[d] = n + 1;
            if (lifted)
            {
                explLifted++;
            }
            Vector3 p = fleckData.spawnPosition;
            Log.Message("[IsoRim probe] explrec " + explRec + " F " + d + " " + p.x.ToString("0.0000") + " " + p.y.ToString("0.0000") + " " + p.z.ToString("0.0000")
                + " s " + fleckData.scale.ToString("0.0000") + " a " + fleckData.velocityAngle.ToString("0.00") + " v " + fleckData.velocitySpeed.ToString("0.0000")
                + " r " + fleckData.rotationRate.ToString("0.00") + " rot " + fleckData.rotation.ToString("0.0") + " tick " + Find.TickManager.TicksGame + (lifted ? " lifted" : "") + (fleckData.ageTicksOverride != -1 ? " aged " + (Find.TickManager.TicksGame - fleckData.ageTicksOverride) : ""));
        }

        private static readonly System.Reflection.PropertyInfo randState = AccessTools.Property(typeof(Rand), "StateCompressed");

        public static void ExplEffecter_Postfix(Effecter __instance)
        {
            if (explRec != null && __instance?.def != null)
            {
                Log.Message("[IsoRim probe] expleffecter " + explRec + " " + __instance.def.defName + " tick " + Find.TickManager.TicksGame);
            }
        }

        public static void ExplRand_Start(Explosion __instance)
        {
            if (explRec != null)
            {
                Log.Message("[IsoRim probe] explrand " + explRec + " start " + __instance.Position + " tick " + Find.TickManager.TicksGame + " state " + randState.GetValue(null));
            }
        }

        public static void ExplRand_Tick(Explosion __instance)
        {
            if (explRec != null)
            {
                Log.Message("[IsoRim probe] explrand " + explRec + " tick " + Find.TickManager.TicksGame + " spawned " + __instance.Spawned + " state " + randState.GetValue(null));
            }
        }

        // explreload=<label>,ax,az,tx,tz: a frag grenade thrown (as explscene throw), the game saved 1 s after its explosion and
        // loaded again (the burst still alive), then a second grenade thrown in the loaded game: CHECKs that no burst of the
        // old game is left after the load and that the new explosion is drawn in 3D (made, held, one burst, parts drawn).
        private static IEnumerator<int> ExplReloadScript(string[] a, Map map)
        {
            string label = a[0];
            IntVec3 at = new IntVec3(I(a[1]), 0, I(a[2])), target = new IntVec3(I(a[3]), 0, I(a[4]));
            IEnumerator<int> first = ExplScript(new[] { label + "_1", "throw", a[1], a[2], a[3], a[4], "0:60" }, map);
            while (first.MoveNext())
            {
                yield return first.Current;
            }
            Check("explreload " + label + " burst before the save", ExplosionFx.Live == 1, "bursts live " + ExplosionFx.Live);
            GameDataSaveLoader.SaveGame(ExplReloadSave);
            Log.Message("[IsoRim probe] explreload " + label + " saved at tick " + Find.TickManager.TicksGame + ", loading");
            Game old = Current.Game;
            GameDataSaveLoader.LoadGame(ExplReloadSave);
            int waited = 0;
            while ((Current.Game == old || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.AnyEventNowOrWaiting) && waited < 3000)
            {
                waited++;
                yield return 1;
            }
            yield return 200;   // the loaded game's first frames (Capture17 builds its scene again meanwhile)
            Map m2 = Find.CurrentMap;
            Check("explreload " + label + " loaded", Current.Game != old && m2 != null, "frames " + waited);
            Check("explreload " + label + " no old burst after the load", ExplosionFx.Live == 0, "bursts live " + ExplosionFx.Live);
            int made0 = ExplosionFx.Made, held0 = ExplosionFx.Held;
            IEnumerator<int> second = ExplScript(new[] { label + "_2", "throw", a[1], a[2], a[3], a[4], "0:6:30" }, m2);
            while (second.MoveNext())
            {
                yield return second.Current;
            }
            Check("explreload " + label + " new explosion in 3D", ExplosionFx.Made == made0 + 1 && ExplosionFx.Held > held0,
                "bursts +" + (ExplosionFx.Made - made0) + " held +" + (ExplosionFx.Held - held0) + " live " + ExplosionFx.Live);
        }

        private static IEnumerator<int> ExplScript(string[] a, Map map)
        {
            string label = a[0], kind = a[1];
            IntVec3 at = new IntVec3(I(a[2]), 0, I(a[3])), target = new IntVec3(I(a[4]), 0, I(a[5]));
            int[] ages = a[6].Split(':').Select(I).ToArray();
            foreach (Pawn dp in map.mapPawns.FreeColonistsSpawned)
            {
                if (dp.drafter != null)
                {
                    dp.drafter.Drafted = true;   // they stand where the layout put them
                }
            }
            explMade.Clear();
            explHeldDefs.Clear();
            explLifted = 0;
            explLiftedSeen = BodyFlecks.Lifted;
            int made0 = ExplosionFx.Made, held0 = ExplosionFx.Held, out0 = ExplosionFx.OptedOut, started0 = ExplosionFx.Started;
            if (kind == "throw" && a.Length > 7 && a[7] == "up1")
            {
                // both on floor 1 (a ProjectZ slab laid by pzslab first): the pawns moved up the column, the cells too
                IntVec3 atUp = Floors.Stacked(map, at, 1), targetUp = Floors.Stacked(map, target, 1);
                foreach (KeyValuePair<IntVec3, IntVec3> mv in new[] { new KeyValuePair<IntVec3, IntVec3>(at, atUp), new KeyValuePair<IntVec3, IntVec3>(target, targetUp) })
                {
                    Pawn pm = mv.Key.GetFirstPawn(map);
                    if (pm != null && mv.Value.IsValid)
                    {
                        pm.DeSpawn();
                        GenSpawn.Spawn(pm, mv.Value, map);
                        pm.drafter.Drafted = true;
                    }
                }
                Log.Message("[IsoRim probe] explscene " + label + " up1: " + at + " -> " + atUp + ", " + target + " -> " + targetUp + " (floor of row " + Floors.FloorOfRow(targetUp.z) + ")");
                at = atUp;
                target = targetUp;
            }
            Pawn victim = target.GetFirstPawn(map);
            LocalTargetInfo targ = victim != null ? new LocalTargetInfo(victim) : new LocalTargetInfo(target);
            if (kind == "throw")
            {
                Pawn thrower = at.GetFirstPawn(map);
                if (thrower == null)
                {
                    Check("explscene " + label, false, "no pawn at " + at);
                    yield break;
                }
                ThingWithComps nade = (ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag"));
                thrower.equipment.MakeRoomFor(nade);
                thrower.equipment.AddEquipment(nade);
                explRec = label;
                bool ok = thrower.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.AttackStatic, targ), JobTag.DraftedOrder);
                Log.Message("[IsoRim probe] explscene " + label + " throw " + thrower.LabelShort + " " + at + " -> " + target + (victim != null ? " (" + victim.LabelShort + ")" : "") + ": " + ok);
            }
            else if (kind == "wick")
            {
                // the explosive def a[7] built at the target and its CompExplosive's wick lit (StartWick): it detonates through the
                // game's own CompExplosive.Detonate
                ThingDef ed = DefDatabase<ThingDef>.GetNamed(a[7]);
                Thing bomb = GenSpawn.Spawn(ThingMaker.MakeThing(ed, GenStuff.DefaultStuffFor(ed)), target, map);
                if (bomb.def.CanHaveFaction)
                {
                    bomb.SetFaction(Faction.OfPlayer);
                }
                explRec = label;
                bomb.TryGetComp<CompExplosive>().StartWick();
                Log.Message("[IsoRim probe] explscene " + label + " wick " + a[7] + " at " + target);
            }
            else if (kind == "launch")
            {
                // the projectile def a[7] launched by the game's own Projectile.Launch from cell at at the target (as the shoot key):
                // its flight, impact and explosion are the game's
                Pawn launcher = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.Position.DistanceToSquared(at)).First();
                Projectile shell = (Projectile)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed(a[7]), at, map);
                explRec = label;
                shell.Launch(launcher, at.ToVector3Shifted(), targ, targ, ProjectileHitFlags.IntendedTarget);
                Log.Message("[IsoRim probe] explscene " + label + " launch " + a[7] + " " + at + " -> " + target);
            }
            else
            {
                Building_TurretGun mortar = (Building_TurretGun)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"), GenStuff.DefaultStuffFor(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"))), at, map);
                mortar.SetFaction(Faction.OfPlayer);
                CompChangeableProjectile shells = mortar.gun.TryGetComp<CompChangeableProjectile>();
                shells.LoadShell(DefDatabase<ThingDef>.GetNamed(a.Length > 7 ? a[7] : "Shell_HighExplosive"), 1);
                Pawn gunner = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.Position.DistanceToSquared(at)).First();
                gunner.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar), JobTag.DraftedOrder);
                for (int i = 0; i < 600 && mortar.TryGetComp<CompMannable>().MannedNow == false; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                explRec = label;
                mortar.OrderAttack(targ);
                Log.Message("[IsoRim probe] explscene " + label + " mortar " + at + " manned " + mortar.TryGetComp<CompMannable>().MannedNow + " by " + gunner.LabelShort + " -> " + target);
            }
            int ticks = 0;
            while (ExplosionFx.Started == started0 && ticks < ExplMaxTicks)
            {
                for (int i = 0; i < ExplTicksPerFrame && ExplosionFx.Started == started0; i++)
                {
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                yield return 1;
            }
            if (ExplosionFx.Started == started0)
            {
                explRec = null;
                Check("explscene " + label, false, "no explosion within " + ticks + " ticks");
                yield break;
            }
            int born = Find.TickManager.TicksGame;
            IntVec3 cell = ExplosionFx.LastStart;
            Log.Message("[IsoRim probe] explscene " + label + " explosion at " + cell + " tick " + born + " after " + ticks + " ticks, 3D smoke " + ExplosionFx.LastSmoke3D);
            foreach (int age in ages)
            {
                while (Find.TickManager.TicksGame - born < age)
                {
                    Find.TickManager.DoSingleTick();
                }
                foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                {
                    tp.Drawer.tweener.ResetTweenedPosToRoot();
                }
                Vector3 look = new Vector3(cell.x + 0.5f, 0f, cell.z + 0.5f);
                Floors.Placed(ref look);   // a stacked floor's cell: where it is drawn
                Find.CameraDriver.SetRootPosAndSize(new Vector3(look.x, 0f, look.z), ExplCamSize);
                yield return ExplSettle;
                string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
                System.IO.Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "expl_" + label + "_" + age.ToString("000") + ".png"));
                Log.Message("[IsoRim probe] explscene " + label + " shot age " + age + " bursts live " + ExplosionFx.Live + " parts " + ExplosionFx.DrawnLast);
                yield return 2;
            }
            explRec = null;
            Log.Message("[IsoRim probe] explsum " + label + ": made " + string.Join(", ", explMade.OrderBy(k => k.Key).Select(k => k.Key + " " + k.Value))
                + " | held " + string.Join(", ", explHeldDefs.OrderBy(k => k.Key).Select(k => k.Key + " " + k.Value))
                + " | lifted " + explLifted + " | bursts +" + (ExplosionFx.Made - made0) + " held +" + (ExplosionFx.Held - held0) + " optedOut +" + (ExplosionFx.OptedOut - out0)
                + " | option " + IsoRimMod.Settings.explosions3D + " | warmed vertices " + ExplosionFx.WarmedVertices);
        }
    }
}
