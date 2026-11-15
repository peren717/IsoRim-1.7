using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness probe for the placement grid (PlaceGrid), real flow: the designator is the architect menu's own instance,
    // selected through the game's designator manager as its button does, and the pointer is put over the cell
    // (Input.mousePosition faked, as the other pointer tests). Every frame the game runs its own designator update and IsoRim
    // its own drawing; the probe only reads what PlaceGrid drew.
    //   pgcam=size                     camera size while the jobs run (cells; a job's c:N overrides it)
    //   pgrid=mode,x,z,tag[,opt...]    one job: mode shelf | wall (build designators) | zone (stockpile)
    //                                  | none (no designator), the pointer over cell x,z.
    //     opts (':' inside): grid:1|grid:0   expected: the grid round this cell, or none
    //                        r:N             the range setting for this job (else the setting as it is)
    //                        off             the placeGrid setting off for this job
    //                        flat            the flat view for this job (the 3D view off, put back after)
    //                        c:N             the camera size for this job
    //                        h:lo:hi         expected walking surface at the centre (true cells)
    //   pgcheck=1                      after the jobs: "pgridcheck: total N PASS, M FAIL"
    // Shots: renders\<tag>\pg_<tag>.png at the end of each job's measured window.
    public static partial class Probe
    {
        private class PgJob
        {
            public string mode, tag;
            public IntVec3 cell;
            public bool? expectGrid;
            public int range = -1;
            public bool off, flat;
            public float cam = -1f;
            public float zoom = -1f;   // (z:N) the camera size changed to N during the measured window (a width-only rebuild)
            public float hLo = -1f, hHi = -1f;
            public int frame = -1, phaseFrame, phase;
            public int drawn0, builds0, layouts0;
            public int shotAt = -1;
            public bool settingWas;
            public int rangeWas;
        }

        private static readonly List<PgJob> pgJobs = new List<PgJob>();
        private static float pgCam = 14f;
        private static bool pgTotals;
        private static int pgPass, pgFail;

        private const int PgSettle = 12;   // frames after the setup before the measured window
        private const int PgWindow = 8;    // frames measured
        private const int PgFlatWait = 900;   // frames to wait at most for the view to switch

        private static void PgKey(string key, string[] a)
        {
            if (key == "pgcam")
            {
                pgCam = F(a[0]);
                return;
            }
            if (key == "pgcheck")
            {
                pgTotals = a[0] == "1";
                return;
            }
            HookMouse();
            PgJob j = new PgJob { mode = a[0], cell = new IntVec3(I(a[1]), 0, I(a[2])), tag = a[3] };
            for (int i = 4; i < a.Length; i++)
            {
                string[] p = a[i].Split(':');
                if (p[0] == "grid" && p.Length == 2)
                {
                    j.expectGrid = p[1] == "1";
                }
                else if (p[0] == "r" && p.Length == 2)
                {
                    j.range = I(p[1]);
                }
                else if (p[0] == "z" && p.Length == 2)
                {
                    j.zoom = F(p[1]);
                }
                else if (p[0] == "c" && p.Length == 2)
                {
                    j.cam = F(p[1]);
                }
                else if (p[0] == "h" && p.Length == 3)
                {
                    j.hLo = F(p[1]);
                    j.hHi = F(p[2]);
                }
                else if (a[i] == "off")
                {
                    j.off = true;
                }
                else if (a[i] == "flat")
                {
                    j.flat = true;
                }
            }
            pgJobs.Add(j);
        }

        // Every frame (from Sticky).
        private static void PgTick(Map map)
        {
            if (pgJobs.Count == 0)
            {
                return;
            }
            PgJob j = pgJobs[0];
            HoldHarness();
            if (j.frame < 0)
            {
                Find.CameraDriver.SetRootPosAndSize(new Vector3(j.cell.x + 0.5f, 0f, j.cell.z + 0.5f), j.cam > 0f ? j.cam : pgCam);
                if (!j.flat)
                {
                    ixSteady = View3D.Active && (View3D.Turning || !View3D.Ready) ? 0 : ixSteady + 1;
                    if (ixSteady < 6)
                    {
                        return;
                    }
                }
                j.frame = 0;
            }
            bool done;
            try
            {
                done = PgStep(map, j);
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim probe] pgrid " + j.tag + " failed: " + e);
                PgCheck(j.tag + " ran", false, e.Message);
                done = true;
            }
            j.frame++;
            if (done)
            {
                PgEnd(j);
                pgJobs.RemoveAt(0);
                ixSteady = 0;
                if (pgJobs.Count == 0 && pgTotals)
                {
                    Log.Message("[IsoRim probe] pgridcheck: total " + pgPass + " PASS, " + pgFail + " FAIL");
                }
            }
        }

        private static bool PgStep(Map map, PgJob j)
        {
            if (j.frame == 0)
            {
                PgSetup(map, j);
            }
            // (z:N) zoomed round the pointer's cell (the screen's middle) from the third measured frame on
            float size = j.zoom > 0f && j.phase >= 1 && j.phaseFrame >= 2 ? j.zoom : j.cam > 0f ? j.cam : pgCam;
            Find.CameraDriver.SetRootPosAndSize(new Vector3(j.cell.x + 0.5f, 0f, j.cell.z + 0.5f), size);
            fakeMouse = ScreenOf(new Vector3(j.cell.x + 0.5f, 0f, j.cell.z + 0.5f));
            if (j.phase == 0)
            {
                // settle (the flat view: also wait for the switch)
                if (j.flat && (View3D.Active || View3D.Ready) && j.phaseFrame < PgFlatWait)
                {
                    j.phaseFrame++;
                    return false;
                }
                j.phaseFrame++;
                if (j.phaseFrame >= PgSettle + (j.flat ? 30 : 0))
                {
                    j.drawn0 = PlaceGrid.Drawn;
                    j.builds0 = PlaceGrid.Builds;
                    j.layouts0 = PlaceGrid.Layouts;
                    j.phase = 1;
                    j.phaseFrame = 0;
                }
                return false;
            }
            if (j.phase == 1)
            {
                j.phaseFrame++;
                if (j.phaseFrame >= PgWindow)
                {
                    PgJudge(map, j);
                    PgShot(j);
                    j.shotAt = j.frame;
                    j.phase = 2;
                }
                return false;
            }
            return j.frame - j.shotAt >= 3;   // the capture is taken at the end of a later frame
        }

        private static void PgSetup(Map map, PgJob j)
        {
            CloseWindows();
            Find.UIRoot.screenshotMode.Active = false;
            Find.Selector.ClearSelection();
            j.settingWas = IsoRimMod.Settings.placeGrid;
            j.rangeWas = IsoRimMod.Settings.placeGridRange;
            if (j.off)
            {
                IsoRimMod.Settings.placeGrid = false;
            }
            if (j.range > 0)
            {
                IsoRimMod.Settings.placeGridRange = j.range;
            }
            if (j.flat)
            {
                View3D.Active = false;
            }
            Designator d = null;
            if (j.mode == "shelf" || j.mode == "wall")
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamed(j.mode == "shelf" ? "Shelf" : "Wall");
                // the architect menu's own instance, selected as its button does
                d = DefDatabase<DesignationCategoryDef>.AllDefs.SelectMany(c => c.AllResolvedDesignators)
                    .FirstOrDefault(x => x is Designator_Build b && b.PlacingDef == def);
                if (d == null)
                {
                    throw new Exception("no build designator for " + def.defName);
                }
            }
            else if (j.mode == "zone")
            {
                d = DefDatabase<DesignationCategoryDef>.AllDefs.SelectMany(c => c.AllResolvedDesignators)
                    .FirstOrDefault(x => x.GetType() == typeof(Designator_ZoneAddStockpile_Resources));
            }
            if (d != null)
            {
                Find.DesignatorManager.Select(d);
            }
            Log.Message("[IsoRim probe] pgrid " + j.tag + " (" + Yp() + "): " + j.mode + " at " + j.cell + ", terrain " + j.cell.GetTerrain(map).defName
                + ", designator " + (Find.DesignatorManager.SelectedDesignator?.GetType().Name ?? "-") + ", setting " + IsoRimMod.Settings.placeGrid
                + ", range " + IsoRimMod.Settings.placeGridRange + ", 3D " + View3D.Active);
        }

        private static void PgJudge(Map map, PgJob j)
        {
            int drawn = PlaceGrid.Drawn - j.drawn0, builds = PlaceGrid.Builds - j.builds0, layouts = PlaceGrid.Layouts - j.layouts0;
            IntVec3 mouse = UI.MouseCell();
            int range = Mathf.Clamp(IsoRimMod.Settings.placeGridRange, PlaceGrid.MinRange, PlaceGrid.MaxRange);
            string state = "drawn " + drawn + "/" + PgWindow + " frames, rebuilds " + builds + ", layouts " + layouts + ", centre " + PlaceGrid.LastCentre + ", pointer cell " + mouse
                + ", range " + PlaceGrid.LastRange + ", edges " + PlaceGrid.LastSegments + ", reach " + PlaceGrid.LastReach.ToString("0.00", CultureInfo.InvariantCulture)
                + ", surface " + PlaceGrid.LastCentreHeight.ToString("0.000", CultureInfo.InvariantCulture) + ", designator "
                + (Find.DesignatorManager.SelectedDesignator?.GetType().Name ?? "-") + ", 3D " + View3D.Ready;
            Log.Message("[IsoRim probe] pgrid " + j.tag + ": " + state);
            if (!j.expectGrid.HasValue)
            {
                return;
            }
            if (!j.expectGrid.Value)
            {
                PgCheck(j.tag + " no grid", drawn == 0, state);
                return;
            }
            PgCheck(j.tag + " grid drawn every frame", drawn == PgWindow, state);
            PgCheck(j.tag + " centred on the pointer cell", PlaceGrid.LastCentre == j.cell && mouse == j.cell, state);
            PgCheck(j.tag + " range " + range, PlaceGrid.LastRange == range, state);
            // fading to nothing at the range: every line point with any opacity is inside it, the farthest within 1.5 cells of it
            PgCheck(j.tag + " fades out at the range", PlaceGrid.LastReach < range && PlaceGrid.LastReach > range - 1.5f
                && PlaceGrid.Fade(0f, range) == 1f && PlaceGrid.Fade(range, range) == 0f && PlaceGrid.Fade(range * 0.5f, range) > 0.4f, state);
            if (j.zoom > 0f)
            {
                PgCheck(j.tag + " zoom rebuilds the mesh, keeps the layout", builds >= 1 && layouts == 0, state);
            }
            else
            {
                PgCheck(j.tag + " mesh kept while the pointer stays", builds <= 1 && layouts <= 1, state);
            }
            if (j.hLo >= 0f)
            {
                PgCheck(j.tag + " surface in " + j.hLo + ".." + j.hHi, PlaceGrid.LastCentreHeight >= j.hLo && PlaceGrid.LastCentreHeight <= j.hHi, state);
            }
        }

        private static void PgCheck(string what, bool ok, string detail)
        {
            if (ok)
            {
                pgPass++;
            }
            else
            {
                pgFail++;
            }
            Log.Message("[IsoRim probe] CHECK " + (ok ? "PASS" : "FAIL") + " pgrid " + what + ": " + detail);
        }

        private static void PgShot(PgJob j)
        {
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "pg_" + j.tag + ".png"));
        }

        private static void PgEnd(PgJob j)
        {
            fakeMouse = null;
            Find.DesignatorManager.Deselect();
            IsoRimMod.Settings.placeGrid = j.settingWas;
            IsoRimMod.Settings.placeGridRange = j.rangeWas;
            if (j.flat)
            {
                View3D.Active = true;
            }
            Find.UIRoot.screenshotMode.Active = true;
        }
    }
}
