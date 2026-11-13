using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness probe for the target box (TargetBox), real flow: the targeting is started by the pawn's own gizmo (as a click
    // on it does), designators are the architect menu's own instances selected as the menu selects them, and the pointer is
    // put over the target (Input.mousePosition faked, as the other pointer tests). Every frame the game runs its own
    // targeter / designator update; the probe only reads what TargetBox answered and what it drew or left out.
    //   tbcam=size                         camera size while the jobs run (cells)
    //   tbox=mode,x,z,h,tag[,opt...]       one job: the pointer over cell x,z at height h (cells over its floor; -1 = the
    //                                      block's top less a little). mode: attack (the drafted shooter's weapon gizmo),
    //                                      jump (the shooter's jump pack gizmo: a jump pack is put on it), lance (the same
    //                                      with a psychic shock lance), haul, cut, mine,
    //                                      hunt, stockpile (those designators).
    //     opts (no '=': the probe line splits on it): s:x:z   the shooter's cell (attack, jump)
    //           box:1|box:0  expected: a box (on this cell's target) or none
    //           h:lo:hi      expected box height range (true cells); h:hb = the cell's block height exactly
    //           fw:lo:hi     expected footprint's longer side (cells; a pawn's posed body)
    //           up1          the job's cell moved to ProjectZ floor 1 (pzslab first; the camera follows it): haul: a granite
    //                        chunk is put there; attack: the pawn in the ground cell is moved up there, the shooter up its own column
    //           off          shots and checks with the setting off only (today's look)
    //           both         shot with the box (s1), then the same with the setting off (s0)
    //   tbcheck=1                          after the jobs: the totals line "tboxcheck: total N PASS, M FAIL"
    //   tbnocursor=1                       review sheets: the cursor icons and labels left out of the shots while jobs run
    // Shots: renders\<tag>\tb_<tag>_s<1|0>.png (1 = setting on, 0 = off), taken at the strong phase of the targeting pulse.
    public static partial class Probe
    {
        private class TbJob
        {
            public string mode, tag;
            public IntVec3 cell, shooter = IntVec3.Invalid;
            public float h;
            public bool? expectBox;
            public float hLo = -1f, hHi = -1f;
            public float fwLo = -1f, fwHi = -1f;   // expected footprint's longer side (fw:lo:hi)
            public string weapon;                  // (w:<def>) the shooter, if not in its cell any more: the colonist with this weapon
            public bool exactBlock, up1, offOnly, both;
            public int frame = -1, phase, phaseFrame;
            public int shotAt = -1;
            public bool recentred;
            public IntVec3 upCell;    // the job's cell: its own, or (up1) the same on floor 1
            public Vector3? camAt;    // the camera's root    // (up1) the camera moved so the upper floor's cell shows in the middle   // the frame a shot was asked for: nothing changes until it is taken (end of a later frame)
            public Thing expected;
            public int drawn0, marks0, brackets0;
            public float slowest;
        }

        private static readonly List<TbJob> tbJobs = new List<TbJob>();
        private static float tbCam = 6f;
        private static bool tbTotals;
        private static bool? tbSettingWas;   // the targetBox setting before the first job, put back after each
        private static int tbPass, tbFail;

        private const int TbSettle = 12;          // frames after the setup before the measured window
        private const int TbWindow = 8;           // frames measured
        private const int TbPulseWait = 240;      // frames to wait for the strong phase of the pulse at most
        private const float TbPulseStrongG = 0.7f; // the targeting colour's green below this: strong phase
        private const float TbSlowFrame = 0.2f;   // a frame slower than this (s): pointer results not trusted
        private const int TbAfterShot = 3;        // frames kept as they are after a shot is asked for (the capture is taken late)

        private static void TbKey(string key, string[] a)
        {
            if (key == "tbcam")
            {
                tbCam = F(a[0]);
                return;
            }
            if (key == "tbcheck")
            {
                tbTotals = a[0] == "1";
                return;
            }
            if (key == "tbnocursor")
            {
                HarmonyLib.Harmony hm = new HarmonyLib.Harmony("peren.isorim17.probe.tbnocursor");
                HarmonyLib.HarmonyMethod skip = new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(TbNoCursor));
                foreach (System.Reflection.MethodInfo mi in typeof(GenUI).GetMethods().Where(m => m.Name == "DrawMouseAttachment"))
                {
                    hm.Patch(mi, prefix: skip);
                }
                foreach (System.Reflection.MethodInfo mi in typeof(Widgets).GetMethods().Where(m => m.Name == "MouseAttachedLabel"))
                {
                    hm.Patch(mi, prefix: skip);
                }
                return;
            }
            HookMouse();
            if (!tbSettingWas.HasValue)
            {
                tbSettingWas = IsoRimMod.Settings.targetBox;
            }
            TbJob j = new TbJob { mode = a[0], cell = new IntVec3(I(a[1]), 0, I(a[2])), h = F(a[3]), tag = a[4] };
            for (int i = 5; i < a.Length; i++)
            {
                string o = a[i];
                string[] p = o.Split(':');
                if (p[0] == "s" && p.Length == 3)
                {
                    j.shooter = new IntVec3(I(p[1]), 0, I(p[2]));
                }
                else if (p[0] == "box" && p.Length == 2)
                {
                    j.expectBox = p[1] == "1";
                }
                else if (p[0] == "fw" && p.Length == 3)
                {
                    j.fwLo = F(p[1]);
                    j.fwHi = F(p[2]);
                }
                else if (o == "h:hb")
                {
                    j.exactBlock = true;
                }
                else if (p[0] == "h" && p.Length == 3)
                {
                    j.hLo = F(p[1]);
                    j.hHi = F(p[2]);
                }
                else if (p[0] == "w" && p.Length == 2)
                {
                    j.weapon = p[1];
                }
                else if (o == "up1")
                {
                    j.up1 = true;
                }
                else if (o == "off")
                {
                    j.offOnly = true;
                }
                else if (o == "both")
                {
                    j.both = true;
                }
            }
            tbJobs.Add(j);
        }

        // Every frame (from Sticky).
        private static void TbTick(Map map)
        {
            if (tbJobs.Count == 0)
            {
                return;
            }
            TbJob j = tbJobs[0];
            HoldHarness();
            if (j.camAt == null)
            {
                j.camAt = new Vector3(j.cell.x + 0.5f, 0f, j.cell.z + 0.5f);
                j.upCell = j.up1 ? Floors.Stacked(map, j.cell, 1) : j.cell;
            }
            IntVec3 cell = j.upCell;   // the job's cell (on floor 1 with up1)
            if (j.frame < 0)
            {
                Find.CameraDriver.SetRootPosAndSize(j.camAt.Value, tbCam);
                ixSteady = View3D.Active && (View3D.Turning || !View3D.Ready) ? 0 : ixSteady + 1;
                if (ixSteady < 6)
                {
                    return;
                }
                if (j.up1 && !j.recentred)
                {
                    // the ground point the upper floor's cell shows over: the camera there puts the cell in the middle
                    j.camAt = View3D.ScreenToHeight(ScreenOf(new Vector3(cell.x + 0.5f, 0.5f, cell.z + 0.5f)), 0f);
                    j.recentred = true;
                    ixSteady = 0;
                    return;
                }
                j.frame = 0;
            }
            bool done = false;
            try
            {
                done = TbStep(map, j, cell);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim probe] tbox " + j.tag + " failed: " + e);
                TbCheck(j.tag + " ran", false, e.Message);
                done = true;
            }
            j.frame++;
            if (done)
            {
                TbEnd();
                tbJobs.RemoveAt(0);
                ixSteady = 0;
                if (tbJobs.Count == 0 && tbTotals)
                {
                    Log.Message("[IsoRim probe] tboxcheck: total " + tbPass + " PASS, " + tbFail + " FAIL");
                }
            }
        }

        // One frame of a job; true when it is finished.
        private static bool TbStep(Map map, TbJob j, IntVec3 cell)
        {
            if (j.frame == 0)
            {
                IsoRimMod.Settings.targetBox = !j.offOnly;
                TbSetup(map, j, cell);
            }
            float ph = j.h;
            if (ph < 0f)
            {
                ph = WallBoxes.BlockHeightAt(map, cell) - 0.05f;
            }
            fakeMouse = ScreenOf(new Vector3(cell.x + 0.5f, ph, cell.z + 0.5f));
            j.slowest = j.frame > 2 ? Mathf.Max(j.slowest, Time.unscaledDeltaTime) : 0f;
            j.phaseFrame++;
            // phase 0: settle; 1: measure; 2: shot (setting as it is); 3: (both) shot with the setting off
            if (j.phase == 0)
            {
                if (j.phaseFrame >= TbSettle)
                {
                    j.drawn0 = TargetBox.Drawn;
                    j.marks0 = TargetBox.MarksHidden;
                    j.brackets0 = TargetBox.BracketsHidden;
                    j.phase = 1;
                    j.phaseFrame = 0;
                }
                return false;
            }
            if (j.phase == 1)
            {
                if (j.phaseFrame >= TbWindow)
                {
                    TbJudge(map, j, cell);
                    j.phase = 2;
                    j.phaseFrame = 0;
                }
                return false;
            }
            if (j.shotAt < 0)
            {
                if (j.phaseFrame < 3 || GenDraw.CurTargetingColor.g >= TbPulseStrongG && j.phaseFrame < TbPulseWait)
                {
                    return false;
                }
                TbShot(j, IsoRimMod.Settings.targetBox ? 1 : 0);
                j.shotAt = j.frame;
                return false;
            }
            if (j.frame - j.shotAt < TbAfterShot)
            {
                return false;
            }
            j.shotAt = -1;
            if (j.phase == 2 && j.both)
            {
                IsoRimMod.Settings.targetBox = false;
                j.phase = 3;
                j.phaseFrame = 0;
                j.drawn0 = TargetBox.Drawn;
                j.marks0 = TargetBox.MarksHidden;
                j.brackets0 = TargetBox.BracketsHidden;
                return false;
            }
            if (j.phase == 3)
            {
                TbCheck(j.tag + " off: no box, no game mark left out", TargetBox.Drawn == j.drawn0 && TargetBox.MarksHidden == j.marks0 && TargetBox.BracketsHidden == j.brackets0,
                    "drawn " + (TargetBox.Drawn - j.drawn0) + ", marks left out " + (TargetBox.MarksHidden - j.marks0) + ", brackets left out " + (TargetBox.BracketsHidden - j.brackets0));
            }
            return true;
        }

        private static void TbSetup(Map map, TbJob j, IntVec3 cell)
        {
            CloseWindows();
            Find.UIRoot.screenshotMode.Active = false;
            Find.Selector.ClearSelection();
            if (j.up1 && j.mode == "haul" && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item))
            {
                GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ChunkGranite")), cell, map);
            }
            if (j.up1 && (j.mode == "attack" || j.mode == "jump" || j.mode == "lance") && cell.GetFirstPawn(map) == null)
            {
                Pawn moved = j.cell.GetFirstPawn(map);
                if (moved != null)
                {
                    moved.DeSpawn();
                    GenSpawn.Spawn(moved, cell, map);
                }
            }
            List<Thing> here = cell.GetThingList(map);
            switch (j.mode)
            {
                case "attack":
                case "jump":
                case "lance":
                {
                    j.expected = here.FirstOrDefault(t => t is Pawn) ?? here.FirstOrDefault(t => t is Building);
                    Pawn shooter = j.shooter.GetFirstPawn(map);
                    if (shooter == null && j.weapon != null)
                    {
                        shooter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(c => c.equipment?.Primary?.def.defName == j.weapon);
                    }
                    if (shooter == null)
                    {
                        throw new Exception("no pawn at " + j.shooter);
                    }
                    IntVec3 shooterUp = j.up1 ? Floors.Stacked(map, shooter.Position, 1) : IntVec3.Invalid;
                    if (shooterUp.IsValid && shooterUp.InBounds(map) && shooterUp.GetFirstPawn(map) == null)
                    {
                        // (up1) the shooter goes up too: the game marks only what it can hit, and it hits across floors only in sight
                        shooter.DeSpawn();
                        GenSpawn.Spawn(shooter, shooterUp, map);
                    }
                    string pack = j.mode == "jump" ? "Apparel_PackJump" : j.mode == "lance" ? "Apparel_PsychicShockLance" : null;
                    if (pack != null && !shooter.apparel.WornApparel.Any(ap => ap.def.defName == pack))
                    {
                        shooter.apparel.Wear((Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(pack)), false);
                    }
                    shooter.drafter.Drafted = true;
                    Find.Selector.Select(shooter, false, false);
                    // the gizmo the player clicks: the weapon's (attack) or the jump pack's (jump) targeting command
                    Command_VerbTarget cmd = shooter.GetGizmos().OfType<Command_VerbTarget>()
                        .FirstOrDefault(c => j.mode == "jump" ? c.verb is Verb_Jump : j.mode == "lance" ? c.verb is Verse.AI.Verb_CastTargetEffectLances
                            : c.verb == shooter.equipment?.PrimaryEq?.PrimaryVerb);
                    if (cmd == null)
                    {
                        throw new Exception("no " + j.mode + " gizmo on " + Desc(shooter));
                    }
                    cmd.ProcessInput(null);
                    break;
                }
                default:
                {
                    Type type = j.mode == "haul" ? typeof(Designator_Haul) : j.mode == "cut" ? typeof(Designator_PlantsCut)
                        : j.mode == "mine" ? typeof(Designator_Mine) : j.mode == "hunt" ? typeof(Designator_Hunt)
                        : j.mode == "stockpile" ? typeof(Designator_ZoneAddStockpile_Resources) : null;
                    if (type == null)
                    {
                        throw new Exception("unknown mode " + j.mode);
                    }
                    // the architect menu's own instance, selected as its button does
                    Designator d = DefDatabase<DesignationCategoryDef>.AllDefs.SelectMany(c => c.AllResolvedDesignators).FirstOrDefault(x => x.GetType() == type)
                        ?? (Designator)Activator.CreateInstance(type);
                    Find.DesignatorManager.Select(d);
                    j.expected = here.FirstOrDefault(t => d.CanDesignateThing(t).Accepted);
                    break;
                }
            }
            Log.Message("[IsoRim probe] tbox " + j.tag + " (" + Yp() + "): " + j.mode + " at " + cell + (j.up1 ? " (floor " + Floors.FloorOfRow(cell.z) + ")" : "")
                + ", in the cell " + string.Join(", ", here.Select(Desc)) + ", targeting " + Find.Targeter.IsTargeting
                + ", designator " + (Find.DesignatorManager.SelectedDesignator?.GetType().Name ?? "-") + ", setting " + IsoRimMod.Settings.targetBox);
        }

        private static void TbJudge(Map map, TbJob j, IntVec3 cell)
        {
            Thing boxed = TargetBox.Target;
            int drawn = TargetBox.Drawn - j.drawn0, marks = TargetBox.MarksHidden - j.marks0, brackets = TargetBox.BracketsHidden - j.brackets0;
            bool slow = j.slowest > TbSlowFrame;
            string state = "boxed " + (boxed != null ? Desc(boxed) : "nothing") + ", drawn " + drawn + "/" + TbWindow + " frames, marks left out " + marks
                + ", brackets left out " + brackets + ", height " + TargetBox.LastHeight.ToString("0.000", CultureInfo.InvariantCulture)
                + ", footprint " + TbRect(TargetBox.LastFootprint) + ", pointer cell " + Interaction.DesignatorMouseCell()
                + ", slowest frame " + (j.slowest * 1000f).ToString("0") + " ms" + (slow ? " (SLOW: pointer results not trusted)" : "");
            Log.Message("[IsoRim probe] tbox " + j.tag + ": " + state);
            if (j.offOnly)
            {
                TbCheck(j.tag + " off: no box, no game mark left out", drawn == 0 && marks == 0 && brackets == 0, state);
                return;
            }
            if (!j.expectBox.HasValue)
            {
                return;
            }
            if (!j.expectBox.Value)
            {
                TbCheck(j.tag + " no box", boxed == null && drawn == 0 && marks == 0 && brackets == 0, state);
                return;
            }
            Thing t = j.expected;
            TbCheck(j.tag + " box on " + (t != null ? Desc(t) : "?"), t != null && boxed == t && drawn == TbWindow, state);
            if (t == null || boxed != t)
            {
                return;
            }
            Rect got = TargetBox.LastFootprint;
            int outline = 0;
            for (int e = 0; e < 24; e++)
            {
                outline += (TargetBox.LastEdges >> e) & 1;
            }
            TbCheck(j.tag + " brackets on the outline's corners only (4 to 6, the inner ones left out)", TargetBox.LastCorners >= 4 && TargetBox.LastCorners <= 6 && outline >= 2 * TargetBox.LastCorners,
                "corners " + TargetBox.LastCorners + ", bars 0x" + TargetBox.LastEdges.ToString("X6") + " (" + outline + "), look: " + TargetBox.BracketInfo + ", colour " + TargetBox.BracketColour);
            if (t is Pawn)
            {
                // a pawn: its posed body (SelectionRings' note), round where it is drawn
                Vector3 dp = t.DrawPos;
                TbCheck(j.tag + " footprint round the posed body", got.Contains(new Vector2(dp.x, dp.z)), TbRect(got) + ", drawn at " + dp.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + dp.z.ToString("0.00", CultureInfo.InvariantCulture));
            }
            else if (t is Plant)
            {
                // a plant: its cell and its model as baked (a canopy may reach past the cell)
                CellRect r = t.OccupiedRect();
                TbCheck(j.tag + " footprint covers the cell", got.xMin <= r.minX + 0.001f && got.yMin <= r.minZ + 0.001f && got.xMax >= r.maxX + 0.999f && got.yMax >= r.maxZ + 0.999f, TbRect(got));
            }
            else
            {
                CellRect r = t.OccupiedRect();
                Rect want = new Rect(r.minX, r.minZ, r.Width, r.Height);
                TbCheck(j.tag + " footprint = the thing's rect", Mathf.Abs(got.xMin - want.xMin) < 0.05f && Mathf.Abs(got.yMin - want.yMin) < 0.05f
                    && Mathf.Abs(got.width - want.width) < 0.01f && Mathf.Abs(got.height - want.height) < 0.01f, TbRect(got) + " vs " + TbRect(want));
            }
            if (j.fwLo >= 0f)
            {
                float side = Mathf.Max(got.width, got.height);
                TbCheck(j.tag + " footprint's longer side in " + j.fwLo + ".." + j.fwHi, side >= j.fwLo && side <= j.fwHi, TbRect(got));
            }
            float h = TargetBox.LastHeight;
            if (j.exactBlock)
            {
                float block = WallBoxes.BlockHeightAt(map, t.Position);
                TbCheck(j.tag + " height = the block's", Mathf.Abs(h - block) < 0.001f, h.ToString("0.000", CultureInfo.InvariantCulture) + " vs block " + block.ToString("0.000", CultureInfo.InvariantCulture));
            }
            else if (j.hLo >= 0f)
            {
                TbCheck(j.tag + " height in " + j.hLo + ".." + j.hHi, h >= j.hLo && h <= j.hHi, h.ToString("0.000", CultureInfo.InvariantCulture)
                    + " (pick height " + Interaction.PickHeight(t).ToString("0.000", CultureInfo.InvariantCulture) + ")");
            }
            if (Find.Targeter.IsTargeting)
            {
                TbCheck(j.tag + " the game's square left out", marks > 0, "marks left out " + marks);
            }
            else if (j.mode == "haul" || j.mode == "cut" || j.mode == "mine")
            {
                TbCheck(j.tag + " the mouse-over bracket left out", brackets > 0, "brackets left out " + brackets);
            }
        }

        private static string TbRect(Rect r)
        {
            return "(" + r.xMin.ToString("0.00", CultureInfo.InvariantCulture) + "," + r.yMin.ToString("0.00", CultureInfo.InvariantCulture) + " "
                + r.width.ToString("0.00", CultureInfo.InvariantCulture) + "x" + r.height.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        private static void TbCheck(string what, bool ok, string detail)
        {
            if (ok)
            {
                tbPass++;
            }
            else
            {
                tbFail++;
            }
            Log.Message("[IsoRim probe] CHECK " + (ok ? "PASS" : "FAIL") + " tbox " + what + ": " + detail);
        }

        private static void TbShot(TbJob j, int style)
        {
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "tb_" + j.tag + "_s" + style + ".png"));
            Log.Message("[IsoRim probe] tbox " + j.tag + " shot s" + style + " at frame " + j.frame + ", targeting colour " + GenDraw.CurTargetingColor);
        }

        public static bool TbNoCursor()
        {
            return tbJobs.Count == 0;
        }

        private static void TbEnd()
        {
            fakeMouse = null;
            if (Find.Targeter.IsTargeting)
            {
                Find.Targeter.StopTargeting();
            }
            Find.DesignatorManager.Deselect();
            Find.Selector.ClearSelection();
            IsoRimMod.Settings.targetBox = tbSettingWas ?? true;
            Find.UIRoot.screenshotMode.Active = true;
        }
    }
}
