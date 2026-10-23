using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for the cut-away (Cutaway reads the real mouse, which the harness cannot move):
    //   cutptr=x,z | cutptr=off        the cut-away's pointer is at that ground point (cells) from now on, or back to the mouse
    //   cutsweep=x0,z0,dx,dz,steps[,every]  the cut-away's pointer starts at (x0, z0) and moves (dx, dz) every frame (or every
    //                                  that many frames) for that many steps
    //                                  frames, then stays; every frame logs "[IsoRim probe] cutframe k ..." with the pointer and
    //                                  the cut-away's own work that frame (Cutaway.TrackPointer + the cell texture update)
    //   cutfade=seconds                (shader cut-away) how long a cell takes to go down or come back up
    //   hurt=x,z,amount                the building at that cell takes that much blunt damage (damage marks)
    //   cutmark=0|1                    a small ring on the ground round the cut-away's pointer (default 1)
    //   damageid=x,z,Def,Stuff|-,id,hp a building spawned there with that thing id (the id picks its damage marks) and that
    //                                  fraction of its hit points (layout_dmgface: id 25960 once drew a mark on a fifth side)
    public static partial class Probe
    {
        public static bool HasCutPointer;
        public static Vector3 CutPointer;
        private static int sweepLeft;
        private static int sweepFrame = -1;
        private static Vector3 sweepStep;
        private static int sweepEvery = 1;
        private static int sweepStop = -1;
        private static bool cutMark = true;
        private static double cellMs;
        private static readonly List<double> sweepCost = new List<double>();

        // Time spent bringing the cell texture up to date (IsoCellMap.BeginFrame), added up between frames.
        public static void AddCellTime(double ms)
        {
            cellMs += ms;
        }

        private static bool CutKey(string key, string[] a)
        {
            switch (key)
            {
                case "cutptr":
                    if (a[0] == "off")
                    {
                        HasCutPointer = false;
                    }
                    else
                    {
                        HasCutPointer = true;
                        CutPointer = new Vector3(F(a[0]), 0f, F(a[1]));
                    }
                    return true;
                case "cutsweep":
                    HasCutPointer = true;
                    CutPointer = new Vector3(F(a[0]), 0f, F(a[1]));
                    sweepStep = new Vector3(F(a[2]), 0f, F(a[3]));
                    sweepLeft = Math.Max(1, I(a[4]));
                    sweepEvery = a.Length > 5 ? Math.Max(1, I(a[5])) : 1;
                    sweepFrame = 0;
                    sweepStop = -1;
                    sweepCost.Clear();
                    return true;
                case "cutfade":
                    CutFadeOverride = F(a[0]);
                    return true;
                case "hurt":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetEdifice(Find.CurrentMap);
                    if (b != null)
                    {
                        b.TakeDamage(new DamageInfo(RimWorld.DamageDefOf.Blunt, F(a[2])));
                    }
                    return true;
                }
                case "damageid":
                {
                    ThingDef dd = DefDatabase<ThingDef>.GetNamed(a[2]);
                    Thing t = ThingMaker.MakeThing(dd, a[3] == "-" ? null : DefDatabase<ThingDef>.GetNamed(a[3]));
                    t.thingIDNumber = I(a[4]);
                    t.HitPoints = Math.Max(1, (int)(t.MaxHitPoints * F(a[5])));
                    GenSpawn.Spawn(t, new IntVec3(I(a[0]), 0, I(a[1])), Find.CurrentMap);
                    Log.Message("[IsoRim probe] damageid " + dd.defName + " id " + t.thingIDNumber + " hp " + t.HitPoints + "/" + t.MaxHitPoints);
                    return true;
                }
                case "cutmark":
                    cutMark = a[0] == "1";
                    return true;
            }
            return false;
        }

        // (shader cut-away) seconds a cell takes to go down or up; negative: the built-in constant.
        public static float CutFadeOverride = -1f;

        // Every frame, before IsoRim's own work (from Sticky): moves the swept pointer.
        private static void CutTick()
        {
            BuildSeeTick();
            if (sweepFrame > 0 && sweepLeft > 0 && sweepFrame % sweepEvery == 0)
            {
                CutPointer += sweepStep;
                sweepLeft--;
            }
            if (HasCutPointer && cutMark)
            {
                GenDraw.DrawCircleOutline(CutPointer, 0.45f);
            }
        }

        // End of View3D.Tick: logs the sweep frame's cost (cutPointer lap = the cut-away's own work,
        // including any blocks of the map it redraws at once; cells = the cell texture update of the frame before).
        private static void CutFrameLog(double[] laps)
        {
            if (sweepFrame < 0)
            {
                cellMs = 0;
                return;
            }
            double cut = laps[6] + laps[5];
            double total = cut + cellMs;
            sweepCost.Add(total);
            Log.Message("[IsoRim probe] cutframe " + sweepFrame + ": pointer " + CutPointer.x.ToString("0.0") + "," + CutPointer.z.ToString("0.0")
                + " cutaway " + cut.ToString("0.000") + " ms, cells " + cellMs.ToString("0.000") + " ms, dt " + (Time.unscaledDeltaTime * 1000f).ToString("0.0") + " ms");
            cellMs = 0;
            sweepFrame++;
            if (sweepLeft <= 0 && sweepFrame > 0)
            {
                // Summary once the pointer has stopped and things have had 60 frames to catch up.
                if (sweepStop < 0)
                {
                    sweepStop = sweepFrame;
                }
                if (sweepCost.Count > 0 && sweepFrame - sweepStop >= 60)
                {
                    List<double> s = new List<double>(sweepCost);
                    s.Sort();
                    double sum = 0;
                    for (int i = 0; i < s.Count; i++)
                    {
                        sum += s[i];
                    }
                    Log.Message("[IsoRim probe] cutsweep summary over " + s.Count + " frames: mean " + (sum / s.Count).ToString("0.000")
                        + " ms, median " + s[s.Count / 2].ToString("0.000") + " ms, max " + s[s.Count - 1].ToString("0.000") + " ms, total " + sum.ToString("0.0") + " ms");
                    sweepFrame = -1;
                    sweepStop = -1;
                }
            }
        }
    }
}
