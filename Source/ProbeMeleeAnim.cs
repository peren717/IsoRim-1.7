using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness keys for the Melee Animation adapter (MeleeAnimOff; meleeanim3d, 2026-11-09). That mod is reached by reflection
    // only. Each scenario is a real game flow: the game runs at normal speed (its animations advance per frame) while the
    // harness is held, the mod's own menu, gizmo or scan starts things, and the result is checked (CHECK PASS/FAIL lines).
    //   maset=<field>,<value>        a Melee Animation setting (AM.Core.Settings: bool 0/1, int or float), in memory only
    //   ma=<scenario>,<tag>          one scenario on a fixed seed, its shots renders\<harness tag>\ma_<tag>_*.png:
    //     idle      a drafted colonist with a longsword at 60,48: its idle animation off in 3D, running in 2D
    //     execadj   a colonist at 59,48 picks "Execute" from the mod's own menu on a hostile at 60,48
    //     execwalk  the same from 56,48 (walks there first)
    //     duel      two colonists sent to a duel spot at 60,48 through its "Start duel" gizmo and pawn menus
    //     autoexec  a drafted colonist at 59,48 next to a hostile at 60,48, the mod's own scan left to start an execution
    //     switchexec, switchduel  execadj / duel in 2D, the 3D view switched on (Api.View3DActive) half way through
    //     absent    (without Melee Animation) the adapter is off, and a colonist with a longsword stands in 3D
    //   In 3D: no animation starts (Register refused), no stuck job, no hidden pawn, the pawns move on afterwards and melee
    //   goes on as the game's; in 2D: the animation plays and ends. Errors and warnings logged meanwhile are counted.
    public static partial class Probe
    {
        private class MaRun
        {
            public string scen;
            public string tag;
            public int frame;
            public int phase;
            public int tick0;
            public int phaseTick;
            public Pawn a;
            public Pawn b;
            public Thing spot;
            public Vector3 cam;
            public int refused0;
            public int let0;
            public bool switched;
            public bool newAfterSwitch;
            public int refusedAtSwitch;
            public int extraPhaseTick = -1;
            public float real0;
            public int modMsgs0;
            public bool keepWindows;
            public bool everGrapple;
            public int swings;
            public Pawn c;
            public object firstAnim;
            public string animsSeen = "";
            public bool everAnim;
            public bool everInAnimJob;
            public int lastAttack0;
            public IntVec3 goA;
            public IntVec3 goB;
            public int shots;
            public int errors0;
            public int warnings0;
        }

        private static readonly List<MaRun> maRuns = new List<MaRun>();
        private static bool maLogHooked;
        private static string maLastWindows;
        private static int maErrors;
        private static int maModMsgs;   // errors and warnings of Melee Animation itself ("[MeleeAnim]")
        private static int maWarnings;
        private static readonly List<string> maMessages = new List<string>();

        private static Type maRendererType;
        private static MethodInfo maTryGetAnimator;
        private static PropertyInfo maActiveRenderers;

        private static bool MeleeAnimKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "maset":
                {
                    Type core = AccessTools.TypeByName("AM.Core");
                    object st = core != null ? AccessTools.Field(core, "Settings")?.GetValue(null) : null;
                    FieldInfo f = st != null ? AccessTools.Field(st.GetType(), a[0]) : null;
                    if (f == null)
                    {
                        Log.Warning("[IsoRim probe] maset: no Melee Animation setting " + a[0]);
                        return true;
                    }
                    object v = f.FieldType == typeof(bool) ? (object)(a[1] == "1") : f.FieldType == typeof(int) ? (object)I(a[1]) : (object)F(a[1]);
                    f.SetValue(st, v);
                    Log.Message("[IsoRim probe] maset " + a[0] + " = " + v);
                    return true;
                }
                case "ma":
                    MaHookLog();
                    maRuns.Add(new MaRun { scen = a[0], tag = a[1] });
                    return true;
            }
            return false;
        }

        private static void MaHookLog()
        {
            if (maLogHooked)
            {
                return;
            }
            maLogHooked = true;
            Harmony h = new Harmony("peren.isorim17.probe.meleeanim");
            h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), postfix: new HarmonyMethod(typeof(Probe), nameof(MaError_Postfix)));
            h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), postfix: new HarmonyMethod(typeof(Probe), nameof(MaWarning_Postfix)));
            maRendererType = AccessTools.TypeByName("AM.AnimRenderer");
            maTryGetAnimator = maRendererType != null ? AccessTools.DeclaredMethod(maRendererType, "TryGetAnimator", new[] { typeof(Pawn) }) : null;
            maActiveRenderers = maRendererType != null ? AccessTools.Property(maRendererType, "ActiveRenderers") : null;
        }

        public static void MaError_Postfix(string text)
        {
            maErrors++;
            if (text != null && text.Contains("[MeleeAnim]"))
            {
                maModMsgs++;
            }
            if (maMessages.Count < 400)
            {
                maMessages.Add("E " + (text ?? "").Split('\n')[0]);
            }
        }

        public static void MaWarning_Postfix(string text)
        {
            maWarnings++;
            if (text != null && text.Contains("[MeleeAnim]"))
            {
                maModMsgs++;
            }
            if (maMessages.Count < 400)
            {
                maMessages.Add("W " + (text ?? "").Split('\n')[0]);
            }
        }

        private static object MaAnimator(Pawn p)
        {
            return p != null && maTryGetAnimator != null ? maTryGetAnimator.Invoke(null, new object[] { p }) : null;
        }

        private static int MaActiveCount()
        {
            return maActiveRenderers != null ? ((ICollection)maActiveRenderers.GetValue(null)).Count : 0;
        }

        private static string MaAnimDesc(object r)
        {
            if (r == null)
            {
                return "-";
            }
            Def d = Traverse.Create(r).Field("Def").GetValue() as Def;
            return (d != null ? d.defName : "?") + "@" + Traverse.Create(r).Property("CurrentTime").GetValue<float>().ToString("0.00")
                + "/" + Traverse.Create(r).Property("Duration").GetValue<float>().ToString("0.00");
        }

        private static ThingComp MaIdleComp(Pawn p)
        {
            return p == null ? null : p.AllComps.FirstOrDefault(c => c.GetType().FullName == "AM.Idle.IdleControllerComp");
        }

        private static object MaIdleAnim(Pawn p)
        {
            ThingComp c = MaIdleComp(p);
            return c != null ? Traverse.Create(c).Property("CurrentAnimation").GetValue() : null;
        }

        private static Pawn MaColonist(Map map, int x, int z, int rot)
        {
            Scene("dressed", new[] { x.ToString(), z.ToString(), rot.ToString(), "30:Male", "Apparel_BasicShirt:Synthread;Apparel_Pants:Synthread;MeleeWeapon_LongSword:Steel" });
            return PawnAt(map, x, z);
        }

        // A hostile (pirate) with nothing in its hands, on a fixed seed for the cell.
        private static Pawn MaHostile(Map map, int x, int z, int rot)
        {
            Faction fac = RingFaction(true);
            Rand.PushState(Gen.HashCombineInt(x, z * 7919 + 59));
            Pawn p;
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(fac != null && fac.def.basicMemberKind != null ? fac.def.basicMemberKind : PawnKindDefOf.Colonist, fac,
                    PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: true,
                    fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, developmentalStages: DevelopmentalStage.Adult));
            }
            finally
            {
                Rand.PopState();
            }
            p.equipment.DestroyAllEquipment();
            p.inventory.DestroyAll();
            GenSpawn.Spawn(p, new IntVec3(x, 0, z), map, new Rot4(rot));
            p.Rotation = new Rot4(rot);
            return p;
        }

        private static void MaShot(MaRun r, string what)
        {
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "ma_" + r.tag + "_" + what + ".png"));
            Log.Message("[IsoRim probe] ma " + r.tag + " shot " + what + " (3D " + View3D.Active + " ready " + View3D.Ready + ", camera " + Find.CameraDriver.MapPosition + " size "
                + Find.CameraDriver.RootSize.ToString("0.0") + ", tick " + Find.TickManager.TicksGame + "): A " + MaState(r.a) + "; B " + MaState(r.b));
            r.shots++;
        }

        private static string MaState(Pawn p)
        {
            if (p == null)
            {
                return "-";
            }
            return p.LabelShort + " at " + p.Position + (p.Dead ? " dead" : p.Downed ? " downed" : "") + " job " + (p.CurJobDef != null ? p.CurJobDef.defName : "none")
                + " anim " + MaAnimDesc(MaAnimator(p)) + " idle " + MaAnimDesc(MaIdleAnim(p)) + " hurt " + p.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
        }

        private static string MaCounts()
        {
            return "refused " + MeleeAnimOff.Refused + " letThrough " + MeleeAnimOff.LetThrough + " active renderers " + MaActiveCount();
        }

        // Every frame (from Sticky).
        private static void MeleeAnimTick(Map map)
        {
            if (maRuns.Count == 0)
            {
                return;
            }
            MaRun r = maRuns[0];
            HoldHarness();
            if (!r.keepWindows && Find.WindowStack.Windows.Any())
            {
                // The debug log opens itself on the mod's errors (it would cover the shots); a dialog would pause the game.
                string kinds = string.Join(", ", Find.WindowStack.Windows.Select(w => w.GetType().Name + (w.forcePause ? " (pauses)" : "")));
                if (kinds != maLastWindows)
                {
                    Log.Message("[IsoRim probe] ma " + r.tag + " closing windows: " + kinds);
                    maLastWindows = kinds;
                }
                CloseWindows();
            }
            if (r.frame > 45 && Find.TickManager.Paused && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused && r.frame % 300 == 0)
            {
                Log.Message("[IsoRim probe] ma " + r.tag + " the game is paused (speed " + Find.TickManager.CurTimeSpeed + ", forced " + Find.WindowStack.WindowsForcePause + ")");
            }
            if (r.cam != Vector3.zero)
            {
                Find.CameraDriver.SetRootPosAndSize(new Vector3(r.cam.x, 0f, r.cam.y), r.cam.z);
            }
            bool done;
            try
            {
                done = MaStep(map, r);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim probe] ma " + r.tag + " failed: " + e);
                done = true;
            }
            r.frame++;
            if (done || (r.frame > 1 && Time.realtimeSinceStartup - r.real0 > 240f))
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                int errs = maErrors - r.errors0, warns = maWarnings - r.warnings0;
                Log.Message("[IsoRim probe] ma " + r.tag + " end after " + r.frame + " frames, " + (Find.TickManager.TicksGame - r.tick0) + " ticks; " + MaCounts()
                    + "; logged meanwhile: " + errs + " errors, " + warns + " warnings");
                foreach (string m in maMessages.Skip(maMessages.Count - Math.Min(maMessages.Count, errs + warns)).Distinct().Take(12))
                {
                    Log.Message("[IsoRim probe] ma " + r.tag + " logged: " + m);
                }
                if (r.scen != "absent")
                {
                    Check("ma " + r.tag + " no [MeleeAnim] errors or warnings", maModMsgs == r.modMsgs0, (maModMsgs - r.modMsgs0) + " logged");
                }
                if (r.spot != null && r.spot.Spawned)
                {
                    r.spot.Destroy();
                }
                fakeMouse = null;
                Find.UIRoot.screenshotMode.Active = true;
                maRuns.RemoveAt(0);
            }
        }

        private static int Now()
        {
            return Find.TickManager.TicksGame;
        }

        // One frame of the scenario; true when it is over.
        private static bool MaStep(Map map, MaRun r)
        {
            bool view3d = View3D.Active;
            if (r.frame == 0)
            {
                r.errors0 = maErrors;
                r.warnings0 = maWarnings;
                r.refused0 = MeleeAnimOff.Refused;
                r.let0 = MeleeAnimOff.LetThrough;
                r.tick0 = Now();
                r.real0 = Time.realtimeSinceStartup;
                r.modMsgs0 = maModMsgs;
                Rand.Seed = 7177 + r.scen.GetHashCode() % 1000;
                Log.Message("[IsoRim probe] ma " + r.tag + " begin (" + r.scen + "), 3D " + view3d + ", adapter idle " + MeleeAnimOff.IdleHooked + " animations " + MeleeAnimOff.RegisterHooked);
                switch (r.scen)
                {
                    case "idle":
                    case "absent":
                        r.a = MaColonist(map, 60, 48, 2);
                        r.cam = new Vector3(60.5f, 48.7f, 2.5f);
                        break;
                    case "execadj":
                    case "switchexec":
                    case "fight":
                        r.a = MaColonist(map, 59, 48, 1);
                        r.b = MaHostile(map, 60, 48, 3);
                        r.cam = new Vector3(60f, 48.7f, 3f);
                        break;
                    case "autoexec":
                        // with a lasso worn, a second hostile further off for the automatic lasso
                        Scene("dressed", new[] { "59", "48", "1", "30:Male", "Apparel_BasicShirt:Synthread;Apparel_Pants:Synthread;AM_LassoCloth;MeleeWeapon_LongSword:Steel" });
                        r.a = PawnAt(map, 59, 48);
                        r.b = MaHostile(map, 60, 48, 3);
                        r.c = MaHostile(map, 66, 48, 3);
                        r.cam = new Vector3(61f, 48.7f, 4f);
                        break;
                    case "menu":
                        // an adjacent standing hostile, and a downed one north of the colonist
                        r.a = MaColonist(map, 59, 48, 1);
                        r.b = MaHostile(map, 60, 48, 3);
                        r.c = MaHostile(map, 59, 49, 2);
                        HealthUtility.DamageUntilDowned(r.c, false);
                        r.cam = new Vector3(59.5f, 48.7f, 3f);
                        break;
                    case "execwalk":
                        r.a = MaColonist(map, 56, 48, 1);
                        r.b = MaHostile(map, 60, 48, 3);
                        r.cam = new Vector3(58.5f, 48.7f, 4f);
                        break;
                    case "duel":
                    case "switchduel":
                        r.spot = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("AM_DuelSpot")), new IntVec3(60, 0, 48), map, Rot4.North);
                        r.spot.SetFaction(Faction.OfPlayer);
                        r.a = MaColonist(map, 57, 45, 0);
                        r.b = MaColonist(map, 63, 45, 0);
                        r.a.drafter.Drafted = false;
                        r.b.drafter.Drafted = false;
                        r.cam = new Vector3(60.5f, 48.7f, 3.5f);
                        break;
                    default:
                        Log.Warning("[IsoRim probe] ma: unknown scenario " + r.scen);
                        return true;
                }
                return false;
            }
            if (r.frame < 45)
            {
                return false;   // pawns render ~45 frames after they spawn; the game still paused
            }
            switch (r.scen)
            {
                case "idle":
                case "absent":
                    return MaIdle(r, view3d);
                case "execadj":
                case "execwalk":
                case "switchexec":
                    return MaExec(map, r, view3d);
                case "autoexec":
                    return MaAuto(r, view3d);
                case "fight":
                    return MaFight(r, view3d);
                case "menu":
                    return MaMenu(map, r, view3d);
                case "duel":
                case "switchduel":
                    return MaDuel(map, r, view3d);
            }
            return true;
        }

        private static bool MaIdle(MaRun r, bool view3d)
        {
            if (r.phase == 0)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                r.phaseTick = Now();
                r.phase = 1;
                return false;
            }
            if (r.phase == 1)
            {
                if (Now() - r.phaseTick < 150)
                {
                    return false;
                }
                object idle = MaIdleAnim(r.a);
                bool destroyed = idle != null && Traverse.Create(idle).Property("IsDestroyed").GetValue<bool>();
                if (r.scen == "absent")
                {
                    Check("ma " + r.tag + " adapter off without Melee Animation", !MeleeAnimOff.IdleHooked && !MeleeAnimOff.RegisterHooked && AccessTools.TypeByName("AM.AnimRenderer") == null,
                        "idle " + MeleeAnimOff.IdleHooked + " animations " + MeleeAnimOff.RegisterHooked + " comp " + (MaIdleComp(r.a) != null));
                }
                else if (view3d)
                {
                    Check("ma " + r.tag + " idle animation off in 3D", MaIdleComp(r.a) != null && idle == null && MeleeAnimOff.IdleHooked,
                        "comp " + (MaIdleComp(r.a) != null) + " idle anim " + MaAnimDesc(idle) + "; " + MaState(r.a));
                }
                else
                {
                    Check("ma " + r.tag + " idle animation runs in 2D", idle != null && !destroyed, "idle anim " + MaAnimDesc(idle) + " destroyed " + destroyed + "; " + MaState(r.a));
                }
                MaShot(r, "idle");
                r.phase = 2;
                r.phaseTick = Now();
                return false;
            }
            return Now() - r.phaseTick > 30;
        }

        // Execution from the mod's own float menu (execadj, execwalk, switchexec).
        private static bool MaExec(Map map, MaRun r, bool view3d)
        {
            switch (r.phase)
            {
                case 0:
                {
                    // The mod's menu for the colonist, right-clicking the hostile (DraftedFloatMenuOptionsUI.GenerateMenuOptions).
                    Type ui = AccessTools.TypeByName("AM.UI.DraftedFloatMenuOptionsUI");
                    MethodInfo gen = ui != null ? AccessTools.Method(ui, "GenerateMenuOptions") : null;
                    List<FloatMenuOption> opts = gen != null ? ((IEnumerable<FloatMenuOption>)gen.Invoke(null, new object[] { r.b.DrawPos, r.a })).ToList() : new List<FloatMenuOption>();
                    Log.Message("[IsoRim probe] ma " + r.tag + " menu: " + string.Join(" | ", opts.Select(o => o.Label + (o.Disabled ? " (disabled)" : ""))));
                    if (view3d)
                    {
                        // 3D: the mod offers nothing; the game's own menu has none of its options.
                        FloatMenuContext ctx;
                        List<FloatMenuOption> full = FloatMenuMakerMap.GetOptions(new List<Pawn> { r.a }, r.b.DrawPos, out ctx);
                        Check("ma " + r.tag + " no Melee Animation options in 3D", opts.Count == 0 && !full.Any(MaModOption),
                            "mod " + opts.Count + "; game menu: " + string.Join(" | ", full.Select(o => o.Label)));
                        return true;
                    }
                    FloatMenuOption ex = opts.FirstOrDefault(o => !o.Disabled && o.action != null && o.Label.StartsWith("Execute"));
                    Check("ma " + r.tag + " execute option offered", ex != null, ex != null ? ex.Label : "none");
                    if (ex == null)
                    {
                        return true;
                    }
                    r.lastAttack0 = r.a.LastAttackTargetTick;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    ex.action();
                    Log.Message("[IsoRim probe] ma " + r.tag + " clicked: " + MaCounts() + "; A " + MaState(r.a) + "; B " + MaState(r.b));
                    r.phase = 1;
                    r.phaseTick = Now();
                    return false;
                }
                case 1:
                {
                    // Watch: an animation on either, the in-animation job.
                    object anim = MaAnimator(r.a) ?? MaAnimator(r.b);
                    MaWatch(r, anim);
                    int t = Now() - r.phaseTick;
                    if (r.scen == "switchexec" && !r.switched && anim != null)
                    {
                        float cur = Traverse.Create(anim).Property("CurrentTime").GetValue<float>(), dur = Traverse.Create(anim).Property("Duration").GetValue<float>();
                        if (cur > dur * 0.35f)
                        {
                            MaShot(r, "before_switch");
                            Api.View3DActive = true;
                            r.switched = true;
                            r.refusedAtSwitch = MeleeAnimOff.Refused;
                            r.firstAnim = anim;
                            r.let0 = MeleeAnimOff.LetThrough;
                            Log.Message("[IsoRim probe] ma " + r.tag + " 3D switched on at " + MaAnimDesc(anim) + "; " + MaCounts());
                        }
                        return false;
                    }
                    if (r.scen == "switchexec" && r.switched && r.shots == 1 && anim != null && Traverse.Create(anim).Property("CurrentTime").GetValue<float>() > Traverse.Create(anim).Property("Duration").GetValue<float>() * 0.6f)
                    {
                        MaShot(r, "after_switch");
                    }
                    if (!view3d && r.scen != "switchexec" && r.shots == 0 && anim != null && Traverse.Create(anim).Property("CurrentTime").GetValue<float>() > Traverse.Create(anim).Property("Duration").GetValue<float>() * 0.4f)
                    {
                        MaShot(r, "playing");
                    }
                    if (view3d && !r.switched && r.shots == 0 && t > 150 && r.b.Spawned && r.a.Position.AdjacentTo8WayOrInside(r.b.Position))
                    {
                        MaShot(r, "melee");
                    }
                    bool walking = r.a.CurJobDef != null && r.a.CurJobDef.defName == "AM_WalkToExecution";
                    bool ended = anim == null && !walking && (r.everAnim || view3d) && t > (view3d && !r.switched ? 400 : 30);
                    if (!ended && t < 3000)
                    {
                        return false;
                    }
                    MaReport(r, view3d, t);
                    r.phase = 2;
                    return false;
                }
                default:
                    return MaMoveOn(map, r);
            }
        }

        private static void MaWatch(MaRun r, object anim)
        {
            if (anim != null)
            {
                r.everAnim = true;
                Def d = Traverse.Create(anim).Field("Def").GetValue() as Def;
                string n = d != null ? d.defName : "?";
                if (!r.animsSeen.Contains(n))
                {
                    r.animsSeen += (r.animsSeen.Length > 0 ? "," : "") + n;
                    Log.Message("[IsoRim probe] ma " + r.tag + " animation " + MaAnimDesc(anim) + " (3D " + View3D.Active + ")");
                }
                if (r.firstAnim == null)
                {
                    r.firstAnim = anim;
                }
                if (r.switched && anim != r.firstAnim)
                {
                    r.newAfterSwitch = true;
                    Log.Message("[IsoRim probe] ma " + r.tag + " NEW animation after the switch: " + MaAnimDesc(anim));
                }
            }
            foreach (Pawn p in new[] { r.a, r.b })
            {
                if (p != null && p.CurJobDef != null && p.CurJobDef.defName == "AM_InAnimation")
                {
                    r.everInAnimJob = true;
                }
                if (p != null && p.CurJobDef != null && p.CurJobDef.defName == "AM_GrapplePawn")
                {
                    if (!r.everGrapple)
                    {
                        Log.Message("[IsoRim probe] ma " + r.tag + " lasso job on " + p.LabelShort + " (3D " + View3D.Active + ")");
                    }
                    r.everGrapple = true;
                }
            }
        }

        private static void MaReport(MaRun r, bool view3d, int t)
        {
            object animA = MaAnimator(r.a), animB = MaAnimator(r.b);
            string state = "A " + MaState(r.a) + "; B " + MaState(r.b) + "; " + MaCounts() + "; seen [" + r.animsSeen + "] after " + t + " ticks";
            bool noneLeft = animA == null && animB == null && !(MaIdleAnim(r.a) != null && r.switched);
            if (r.switched)
            {
                bool interrupted = r.firstAnim != null && Traverse.Create(r.firstAnim).Property("WasInterrupted").GetValue<bool>();
                Check("ma " + r.tag + " animation playing at the switch played to its end", r.firstAnim != null && !interrupted && noneLeft, "interrupted " + interrupted + "; " + state);
                Check("ma " + r.tag + " nothing new started after the switch", !r.newAfterSwitch,
                    "refused after the switch " + (MeleeAnimOff.Refused - r.refusedAtSwitch) + ", let through " + (MeleeAnimOff.LetThrough - r.let0) + "; " + state);
            }
            else if (view3d)
            {
                Check("ma " + r.tag + " nothing started in 3D (not even tried)", !r.everAnim && !r.everInAnimJob && !r.everGrapple && MeleeAnimOff.Refused == r.refused0 && noneLeft,
                    "refused " + (MeleeAnimOff.Refused - r.refused0) + " everAnim " + r.everAnim + " inAnimJob " + r.everInAnimJob + " grapple " + r.everGrapple + "; " + state);
            }
            else
            {
                Check("ma " + r.tag + " animation played in 2D and ended", r.everAnim && noneLeft, "everAnim " + r.everAnim + " inAnimJob " + r.everInAnimJob + "; " + state);
            }
            if (r.b != null && (r.scen == "execadj" || r.scen == "autoexec") && view3d && !r.switched)
            {
                bool hit = r.a.LastAttackTargetTick > r.lastAttack0 || r.b.Dead || r.b.Downed || r.b.health.hediffSet.hediffs.Any(h => h is Hediff_Injury);
                Check("ma " + r.tag + " melee goes on as the game's", hit, "last attack tick " + r.a.LastAttackTargetTick + " (before " + r.lastAttack0 + "); " + state);
            }
        }

        // The pawns are not stuck: each one still standing walks three cells north.
        private static bool MaMoveOn(Map map, MaRun r)
        {
            if (r.phase == 2)
            {
                // The fight is over for this test: the hostile goes (else it downs the walker on the way).
                if (r.c != null && r.c.Spawned && r.c.Faction != Faction.OfPlayer)
                {
                    r.c.Destroy();
                }
                if (r.b != null && r.b.Spawned && r.b.Faction != Faction.OfPlayer)
                {
                    r.b.Destroy();
                }
                foreach (Pawn p in new[] { r.a, r.b })
                {
                    if (p == null || !p.Spawned || p.Dead || p.Downed || p.Faction != Faction.OfPlayer)
                    {
                        continue;
                    }
                    IntVec3 to = p.Position + new IntVec3(0, 0, 3);
                    if (p == r.a)
                    {
                        r.goA = to;
                    }
                    else
                    {
                        r.goB = to;
                    }
                    p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, to), JobCondition.InterruptForced);
                }
                r.phase = 3;
                r.phaseTick = Now();
                return false;
            }
            if (r.phase == 4)
            {
                // duel in 3D: the two left to themselves by the duel spot (its joy giver offers friendly duels again).
                if (new[] { r.a, r.b }.Any(p => p.CurJobDef != null && p.CurJobDef.defName == "AM_DoFriendlyDuel"))
                {
                    r.everInAnimJob = true;
                }
                if (Now() - r.phaseTick < 3600)
                {
                    return false;
                }
                Log.Message("[IsoRim probe] ma " + r.tag + " left alone for " + (Now() - r.phaseTick) + " ticks by the duel spot: refused " + (MeleeAnimOff.Refused - r.refusedAtSwitch)
                    + ", errors " + (maErrors - r.extraPhaseTick) + "; A " + MaState(r.a) + "; B " + MaState(r.b));
                Check("ma " + r.tag + " no friendly duel taken up in 3D", !r.everInAnimJob && MeleeAnimOff.Refused == r.refusedAtSwitch, "duel job seen " + r.everInAnimJob
                    + ", refused " + (MeleeAnimOff.Refused - r.refusedAtSwitch));
                return true;
            }
            bool aOk = !r.goA.IsValid || r.goA == IntVec3.Zero || r.a.Position == r.goA;
            bool bOk = r.b == null || !r.goB.IsValid || r.goB == IntVec3.Zero || r.b.Position == r.goB;
            if ((aOk && bOk) || Now() - r.phaseTick > 600)
            {
                Check("ma " + r.tag + " pawns move on afterwards (no stuck job, not hidden)", aOk && bOk && MaAnimator(r.a) == null && (r.b == null || MaAnimator(r.b) == null),
                    "A " + MaState(r.a) + " goal " + r.goA + "; B " + MaState(r.b) + " goal " + r.goB);
                if (r.scen == "duel" && View3D.Active)
                {
                    r.phase = 4;
                    r.phaseTick = Now();
                    r.refusedAtSwitch = MeleeAnimOff.Refused;
                    r.extraPhaseTick = maErrors;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                    return false;
                }
                return true;
            }
            return false;
        }

        // The mod's own scan (MapPawnProcessor, AutoExecute) beside a hostile.
        private static bool MaAuto(MaRun r, bool view3d)
        {
            if (r.phase == 0)
            {
                r.lastAttack0 = r.a.LastAttackTargetTick;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                r.phase = 1;
                r.phaseTick = Now();
                return false;
            }
            if (r.phase == 1)
            {
                object anim = MaAnimator(r.a) ?? MaAnimator(r.b);
                MaWatch(r, anim);
                int t = Now() - r.phaseTick;
                if (r.shots == 0 && ((!view3d && anim != null && Traverse.Create(anim).Property("CurrentTime").GetValue<float>() > 0.5f) || (view3d && t > 300)))
                {
                    MaShot(r, view3d ? "melee" : "playing");
                }
                bool settled = view3d ? t > 1500 : r.everAnim && anim == null && !(r.a.CurJobDef != null && r.a.CurJobDef.defName == "AM_GrapplePawn");
                if (!settled && t < 2400)
                {
                    return false;
                }
                MaReport(r, view3d, t);
                r.phase = 2;
                return false;
            }
            return MaMoveOn(Find.CurrentMap, r);
        }

        // A friendly duel through the duel spot's "Start duel" gizmo and its two pawn menus (Building_DuelSpot.ClickedStartDuel).
        private static bool MaDuel(Map map, MaRun r, bool view3d)
        {
            switch (r.phase)
            {
                case 0:
                {
                    string label = "AM.Gizmos.DuelSpot.StartDuel.Label".Translate();
                    Command_Action start = r.spot.GetGizmos().OfType<Command_Action>().FirstOrDefault(g => g.defaultLabel == label);
                    if (view3d)
                    {
                        Check("ma " + r.tag + " no Start duel button in 3D", start == null, "gizmos: " + string.Join(" | ", r.spot.GetGizmos().OfType<Command>().Select(g => g.defaultLabel)));
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                        r.phase = 2;   // the two left by the spot (MaMoveOn, then 3600 ticks alone)
                        return false;
                    }
                    Check("ma " + r.tag + " start duel gizmo", start != null && !start.Disabled, start == null ? "none" : "disabled " + start.Disabled + " " + start.disabledReason);
                    if (start == null || start.Disabled)
                    {
                        return true;
                    }
                    start.action();
                    if (!MaPickFromMenu(r.a) || !MaPickFromMenu(r.b))
                    {
                        Check("ma " + r.tag + " duel pawn menus", false, "pawn not offered; " + MaState(r.a) + "; " + MaState(r.b));
                        CloseWindows();
                        return true;
                    }
                    CloseWindows();
                    Log.Message("[IsoRim probe] ma " + r.tag + " duel ordered: A " + MaState(r.a) + "; B " + MaState(r.b));
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    r.phase = 1;
                    r.phaseTick = Now();
                    return false;
                }
                case 1:
                {
                    object anim = MaAnimator(r.a) ?? MaAnimator(r.b);
                    MaWatch(r, anim);
                    int t = Now() - r.phaseTick;
                    Def d = anim != null ? Traverse.Create(anim).Field("Def").GetValue() as Def : null;
                    bool mainDuel = d != null && d.defName.IndexOf("Duel", StringComparison.OrdinalIgnoreCase) >= 0 && d.defName.IndexOf("Bow", StringComparison.OrdinalIgnoreCase) < 0;
                    if (r.scen == "switchduel" && !r.switched && mainDuel && Traverse.Create(anim).Property("CurrentTime").GetValue<float>() > 1.5f)
                    {
                        MaShot(r, "before_switch");
                        Api.View3DActive = true;
                        r.switched = true;
                        r.refusedAtSwitch = MeleeAnimOff.Refused;
                        r.firstAnim = anim;
                        Log.Message("[IsoRim probe] ma " + r.tag + " 3D switched on at " + MaAnimDesc(anim) + "; " + MaCounts());
                        return false;
                    }
                    if (r.scen == "switchduel" && r.switched && r.shots == 1 && anim != null && t % 120 == 0)
                    {
                        MaShot(r, "after_switch");
                    }
                    if (r.scen == "duel" && !view3d && r.shots == 0 && mainDuel && Traverse.Create(anim).Property("CurrentTime").GetValue<float>() > 1.5f)
                    {
                        MaShot(r, "playing");
                    }
                    bool inDuelJob = new[] { r.a, r.b }.Any(p => p.CurJobDef != null && p.CurJobDef.defName == "AM_DoFriendlyDuel");
                    if (view3d && !r.switched && r.shots == 0 && inDuelJob && t > 200 && r.a.Position.InHorDistOf(r.spot.Position, 2f))
                    {
                        MaShot(r, "at_spot");
                    }
                    if (inDuelJob || anim != null)
                    {
                        if (t < 6000)
                        {
                            return false;
                        }
                    }
                    MaReport(r, view3d, t);
                    Check("ma " + r.tag + " duel jobs ended", !inDuelJob, "A " + MaState(r.a) + "; B " + MaState(r.b));
                    r.phase = 2;
                    return false;
                }
                default:
                    return MaMoveOn(map, r);
            }
        }

        // A float menu option of Melee Animation's (its English labels: execute, lasso, skills).
        private static bool MaModOption(FloatMenuOption o)
        {
            string l = o.Label ?? "";
            return l.StartsWith("Execute") || l.StartsWith("Cannot execute") || l.StartsWith("Lasso") || l.StartsWith("Cannot lasso") || l.Contains("Cast ") || l.Contains("cast ");
        }

        // A drafted colonist fights hostiles in melee for 3000 ticks (a new hostile when one falls): in 3D no swing is even tried.
        private static bool MaFight(MaRun r, bool view3d)
        {
            if (r.phase == 0)
            {
                r.lastAttack0 = r.a.LastAttackTargetTick;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                r.phase = 1;
                r.phaseTick = Now();
                return false;
            }
            if (r.phase == 1)
            {
                int t = Now() - r.phaseTick;
                if (r.a.LastAttackTargetTick != r.lastAttack0)
                {
                    r.swings++;
                    r.lastAttack0 = r.a.LastAttackTargetTick;
                }
                MaWatch(r, MaAnimator(r.a) ?? MaAnimator(r.b));
                object idle = MaIdleAnim(r.a);
                if (idle != null)
                {
                    Def d = Traverse.Create(idle).Field("Def").GetValue() as Def;
                    if (d != null && d.defName.Contains("Attack") && !r.animsSeen.Contains(d.defName))
                    {
                        r.animsSeen += (r.animsSeen.Length > 0 ? "," : "") + d.defName;
                    }
                }
                if (r.shots == 0 && t > 300)
                {
                    MaShot(r, "fight");
                }
                if (r.b == null || !r.b.Spawned || r.b.Dead || r.b.Downed)
                {
                    if (r.b != null && !r.b.Destroyed)
                    {
                        r.b.Destroy();
                    }
                    Thing corpse = r.b != null ? r.b.Corpse : null;
                    if (corpse != null && corpse.Spawned)
                    {
                        corpse.Destroy();
                    }
                    r.b = MaHostile(Find.CurrentMap, 60, 48, 3);
                }
                if (t < 3000 && !r.a.Downed && !r.a.Dead)
                {
                    return false;
                }
                string state = "swings " + r.swings + ", swing animations seen [" + r.animsSeen + "], " + MaCounts() + "; A " + MaState(r.a) + " after " + t + " ticks";
                if (view3d)
                {
                    Check("ma " + r.tag + " melee in 3D: no swing animation tried", r.swings > 0 && r.animsSeen.Length == 0 && MeleeAnimOff.Refused == r.refused0 && !r.everAnim, state);
                }
                else
                {
                    Check("ma " + r.tag + " melee in 2D: swing animations play", r.swings > 0 && r.animsSeen.Length > 0, state);
                }
                r.phase = 2;
                return false;
            }
            return MaMoveOn(Find.CurrentMap, r);
        }

        // Right-click menus of a drafted colonist on an adjacent hostile and a downed one: the mod's options in 2D, none in 3D.
        private static bool MaMenu(Map map, MaRun r, bool view3d)
        {
            if (r.phase == 0)
            {
                Type ui = AccessTools.TypeByName("AM.UI.DraftedFloatMenuOptionsUI");
                MethodInfo gen = ui != null ? AccessTools.Method(ui, "GenerateMenuOptions") : null;
                int modStanding = 0, modDowned = 0, gameStanding = 0, gameDowned = 0;
                List<FloatMenuOption> shown = null;
                foreach (Pawn target in new[] { r.b, r.c })
                {
                    List<FloatMenuOption> mod = gen != null ? ((IEnumerable<FloatMenuOption>)gen.Invoke(null, new object[] { target.DrawPos, r.a })).ToList() : new List<FloatMenuOption>();
                    FloatMenuContext ctx;
                    List<FloatMenuOption> full = FloatMenuMakerMap.GetOptions(new List<Pawn> { r.a }, target.DrawPos, out ctx);
                    int fullMod = full.Count(MaModOption);
                    Log.Message("[IsoRim probe] ma " + r.tag + " menu on " + (target.Downed ? "downed " : "") + target.LabelShort + ": mod " + string.Join(" | ", mod.Select(o => o.Label))
                        + "; game menu " + string.Join(" | ", full.Select(o => o.Label)));
                    if (target == r.b)
                    {
                        modStanding = mod.Count;
                        gameStanding = fullMod;
                        shown = full;
                    }
                    else
                    {
                        modDowned = mod.Count;
                        gameDowned = fullMod;
                    }
                }
                string state = "mod options standing " + modStanding + " downed " + modDowned + "; in the game menu standing " + gameStanding + " downed " + gameDowned;
                if (view3d)
                {
                    Check("ma " + r.tag + " no Melee Animation options in 3D", modStanding + modDowned + gameStanding + gameDowned == 0, state);
                }
                else
                {
                    Check("ma " + r.tag + " Melee Animation options shown in 2D", modStanding > 0 && gameStanding > 0, state);
                }
                // The game's menu on the standing hostile, open for a shot (interface on, the pointer over the hostile).
                HookMouse();
                fakeMouse = ScreenOf(new Vector3(r.b.DrawPos.x, 0f, r.b.DrawPos.z));
                Find.UIRoot.screenshotMode.Active = false;
                CloseWindows();
                Find.Selector.ClearSelection();
                Find.Selector.Select(r.a, false, false);
                r.keepWindows = true;
                if (shown != null && shown.Count > 0)
                {
                    Find.WindowStack.Add(new FloatMenu(shown));
                }
                r.phase = 1;
                r.phaseTick = r.frame;
                return false;
            }
            if (r.phase == 1)
            {
                if (r.frame - r.phaseTick == 20)
                {
                    MaShot(r, "menu");
                }
                if (r.frame - r.phaseTick < 30)
                {
                    return false;
                }
                CloseWindows();
                Find.Selector.ClearSelection();
                fakeMouse = null;
                Find.UIRoot.screenshotMode.Active = true;
                r.keepWindows = false;
                if (r.c != null && r.c.Spawned)
                {
                    r.c.Destroy();
                }
                r.phase = 2;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                return false;
            }
            return MaMoveOn(map, r);
        }

        // The pawn's entry in the open pawn menu (the last float menu) chosen.
        private static bool MaPickFromMenu(Pawn p)
        {
            FloatMenu menu = Find.WindowStack.Windows.OfType<FloatMenu>().LastOrDefault();
            List<FloatMenuOption> opts = menu != null ? Traverse.Create(menu).Field("options").GetValue<List<FloatMenuOption>>() : null;
            string name = p.Name != null ? p.Name.ToStringShort : p.LabelCap.ToString();
            FloatMenuOption o = opts != null ? opts.FirstOrDefault(x => x.Label == name && !x.Disabled && x.action != null) : null;
            Log.Message("[IsoRim probe] ma pawn menu: " + (opts == null ? "none" : string.Join(" | ", opts.Select(x => x.Label + (x.Disabled ? " (disabled)" : "")))) + " -> " + name);
            if (o == null)
            {
                return false;
            }
            Find.WindowStack.TryRemove(menu, doCloseSound: false);
            o.action();
            return true;
        }
    }
}
