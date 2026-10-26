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
    // Harness-only tests for the facing and selection fixes (2026-10-16), on top of the pointer tests (ProbeInteract):
    // they run in the same queue, one after the other, with real input paths and the game running.
    //   wipe=x0,z0,x1,z1             everything in the block but colonists removed (rock too), soil, no roof, no fog
    //   pzslab=x0,z0,x1,z1[,k]       (with ProjectZ) floor 2 (k = 1, or floor k + 1) laid over that ground block, as
    //                                ProjectZ's own tests set it up: its slab terrain up there, the floor's roof under it
    //   pzview=k                     the floor the player views (ProjectZ's FloorView: 0 the ground, 1 floor 2)
    //   fadekeep=a,b,...  fadefrom=a,b,...  FadeFloors.Keep / From (peek): kept share and dither start per floor above
    //   peek=1|0  peeklog=1         upper floors faded as if peeking (FadeFloors.ProbeHeld); frame times of each press and release logged
    //   unfogall=1                   every cell of the map revealed (map.fogGrid.ClearAllFog)
    //   U<z> in any coordinate       that ground row's cell on floor 2 (e.g. dressed=40,U30,...); V<z>: on floor 3
    //   want=tag,x:z;x:z|-           what the drag or click with that tag must select, by cell (- nothing): CHECK PASS/FAIL
    //   then=key:value|key:value     probe keys applied when the queue gets there (after the jobs queued before it)
    //   bdrag=def,ax,az,bx,bz,tag    the game's build designator of that def (terrain or thing) dragged between two map
    //                                points (the pointer path as the player's), blueprints logged, shots
    //   faceorder=x,z,tx,tz[,u]      the colonist in (x,z) gets a move order to (tx,tz) by the right-click menu's "Go here"
    //                                as a player gives it (u: undrafted instead, no order: its own work)
    //   facejob=x,z,tx,tz            the drafted colonist in (x,z) is ordered a Wait_Combat job aimed at the cell (tx,tz) (its
    //                                driver leaves facing to the game's UpdateRotation: it faces that cell, 3D off then south)
    //   faceshoot=x,z,tx,tz         the colonist in (x,z) fires at the thing in (tx,tz) (Verb.OrderForceTarget, as the
    //                                targeting gizmo) until it is gone
    //   face=tag,ticks,keep|south    the orders since the last face= are given and the game runs (Superfast, real ticks)
    //                                until each is done, then that many ticks more; the facing is watched every frame:
    //                                keep = it stays as it was when done, the way walked or aimed (south: turned south, the
    //                                game's own drafted rule, 3D off); undrafted pawns: the facing patch never touches them
    public static partial class Probe
    {
        private class FaceOrder
        {
            public IntVec3 from;
            public IntVec3 to;
            public bool shoot;
            public bool undraft;
            public Pawn pawn;
            public Thing target;
            public bool done;
            public int watched;
            public Rot4 rot0 = Rot4.Invalid;
            public int changes;
            public string firstChange;
            public int keptAtStart;
            public string note = "";
            public bool vehicle;   // (vfaceorder, ProbeVehicleFacing)
            public string vrot;
            public bool jobface;   // (facejob)
            public int orderTick;
        }

        private static readonly List<FaceOrder> pendingFace = new List<FaceOrder>();
        private static readonly Dictionary<string, List<IntVec3>> wants = new Dictionary<string, List<IntVec3>>();
        private static readonly Dictionary<int, int> draftFacingKept = new Dictionary<int, int>();
        private static int checksPassed, checksFailed;

        // Patches.DraftedSouth skipped the game's south turn of this drafted pawn (test bookkeeping).
        public static void NoteDraftFacing(Pawn p)
        {
            if (p == null)
            {
                return;
            }
            int n;
            draftFacingKept.TryGetValue(p.thingIDNumber, out n);
            draftFacingKept[p.thingIDNumber] = n + 1;
        }

        private static int KeptCount(Pawn p)
        {
            int n;
            return draftFacingKept.TryGetValue(p.thingIDNumber, out n) ? n : 0;
        }

        private static void Check(string what, bool ok, string detail)
        {
            if (ok)
            {
                checksPassed++;
            }
            else
            {
                checksFailed++;
            }
            Log.Message("[IsoRim probe] CHECK " + (ok ? "PASS" : "FAIL") + " " + what + ": " + detail + " (so far " + checksPassed + " pass, " + checksFailed + " fail)");
        }

        // The rows from the ground to floor 2 (U<z> coordinates), or to floor k + 1 (V<z>: k = 2).
        private static int UShift(int k = 1)
        {
            Map map = Find.CurrentMap;
            IntVec3 up = map != null && Floors.GroundRowsOf(map) > 0 ? Floors.Stacked(map, IntVec3.Zero, k) : IntVec3.Invalid;
            if (!up.IsValid)
            {
                throw new InvalidOperationException("U coordinate without an upper floor");
            }
            return up.z;
        }

        private static IntVec3 CellOf(string s)
        {
            string[] xz = s.Split(':');
            return new IntVec3(I(xz[0]), 0, I(xz[1]));
        }

        private static bool FixKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "zoomlimit":
                {
                    // zoomlimit=set,<min>: another mod (or the game) sets the camera's closest zoom;
                    // zoomlimit=check,<min>: what the limit is now (Patches.ZoomLimit has had frames since).
                    CameraMapConfig cfg = Find.CameraDriver.config;
                    if (a[0] == "set")
                    {
                        cfg.sizeRange.min = F(a[1]);
                        Log.Message("[IsoRim probe] zoomlimit set " + a[1]);
                    }
                    else
                    {
                        Check("closest zoom", Mathf.Abs(cfg.sizeRange.min - F(a[1])) < 0.01f, "min " + cfg.sizeRange.min.ToString("0.00") + ", want " + a[1]
                            + ", size " + Find.CameraDriver.RootSize.ToString("0.00") + ", 3D " + View3D.Ready);
                    }
                    return true;
                }
                case "condition":
                {
                    // condition=<GameConditionDef>: started on the map for a day (VolcanicAsh: Odyssey's ash sheets).
                    GameConditionDef gd = DefDatabase<GameConditionDef>.GetNamedSilentFail(a[0]);
                    if (gd == null)
                    {
                        Log.Warning("[IsoRim probe] condition: no " + a[0]);
                        return true;
                    }
                    map.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(gd, 60000));
                    Log.Message("[IsoRim probe] condition " + a[0] + " started");
                    return true;
                }
                case "weather":
                {
                    // weather=<WeatherDef>: set at once, no transition.
                    WeatherDef wd = DefDatabase<WeatherDef>.GetNamedSilentFail(a[0]);
                    if (wd == null)
                    {
                        Log.Warning("[IsoRim probe] weather: no " + a[0]);
                        return true;
                    }
                    map.weatherManager.curWeather = wd;
                    map.weatherManager.lastWeather = wd;
                    map.weatherManager.curWeatherAge = 100000;
                    Log.Message("[IsoRim probe] weather " + a[0]);
                    return true;
                }
                case "weathercheck":
                {
                    // How each overlay showing now is drawn in 3D (Falling): kind, sheet and the drops' hue.
                    List<SkyOverlay> overlays = new List<SkyOverlay>(map.weatherManager.curWeather.Worker.overlays);
                    foreach (GameCondition gc in map.gameConditionManager.ActiveConditions)
                    {
                        List<SkyOverlay> more = gc.SkyOverlays(map);
                        if (more != null)
                        {
                            overlays.AddRange(more);
                        }
                    }
                    foreach (SkyOverlay o in overlays)
                    {
                        Material m = Falling.MaterialOf(o);
                        Log.Message("[IsoRim probe] weathercheck " + o.GetType().Name + ": " + Falling.KindOf(o) + ", sheet " + (m != null ? m.name : "-")
                            + " colour " + (m != null ? m.color.ToString() : "-") + ", tint " + Falling.TintOf(m)
                            + (m != null && m.HasProperty("_TuningColor") ? ", tuning " + m.GetColor("_TuningColor") : "")
                            + (m != null && m.HasProperty("_MainTexColor") ? ", maintex " + m.GetColor("_MainTexColor") : ""));
                        Texture tx = m != null ? m.mainTexture : null;
                        Color32[] px = tx != null ? SpritePixels.Sample(tx, Vector2.one, Vector2.zero, 16) : null;
                        Log.Message("[IsoRim probe] weathercheck texture " + (tx != null ? tx.name + " " + tx.GetType().Name + " " + tx.width + "x" + tx.height : "-")
                            + ": pixels " + (px != null ? px.Length + ", first " + px[0] + ", mid " + px[px.Length / 2] : "none") + " (" + SpritePixels.LastInfo + ")");
                    }
                    return true;
                }
                case "settingscheck":
                {
                    IsoRimSettings st = IsoRimMod.Settings;
                    Log.Message("[IsoRim probe] settings: weather3D " + st.weather3D + ", faceDecal " + st.faceDecal + ", fullPawnDraw " + st.fullPawnDraw
                        + ", minZoom " + st.minZoom + ", thingLabels " + st.thingLabels + ", plantCardSize " + st.plantCardSize);
                    return true;
                }
                case "wipe":
                {
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    int gone = 0;
                    foreach (IntVec3 c in r)
                    {
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        foreach (Thing t in c.GetThingList(map).ToList())
                        {
                            if (!(t is Pawn p && p.IsColonist) && !t.Destroyed)
                            {
                                t.Destroy(DestroyMode.Vanish);
                                gone++;
                            }
                        }
                        map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                        map.roofGrid.SetRoof(c, null);
                        map.fogGrid.Unfog(c);
                        map.snowGrid.SetDepth(c, 0f);
                    }
                    Log.Message("[IsoRim probe] wipe " + r + ": " + gone + " things removed");
                    return true;
                }
                case "pzslab":
                {
                    TerrainDef slab = DefDatabase<TerrainDef>.GetNamedSilentFail("PZ_UpperFloor");
                    RoofDef under = DefDatabase<RoofDef>.GetNamedSilentFail("PZ_FloorRoof");
                    if (slab == null || under == null || Floors.GroundRowsOf(map) <= 0)
                    {
                        Log.Warning("[IsoRim probe] pzslab: no ProjectZ floors on this map");
                        return true;
                    }
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    int level = a.Length > 4 ? I(a[4]) : 1;
                    foreach (IntVec3 c in r)
                    {
                        IntVec3 up = Floors.Stacked(map, c, level);
                        IntVec3 below = level > 1 ? Floors.Stacked(map, c, level - 1) : c;
                        if (up.IsValid && up.InBounds(map) && below.IsValid)
                        {
                            map.terrainGrid.SetTerrain(up, slab);
                            map.roofGrid.SetRoof(below, under);
                        }
                    }
                    Log.Message("[IsoRim probe] pzslab " + r + ": floor 2 rows start " + UShift() + ", ground rows " + Floors.GroundRowsOf(map) + ", map " + map.Size);
                    return true;
                }
                case "peek":
                    FadeFloors.ProbeHeld = a[0] == "1";
                    return true;
                case "peeklog":
                    FadeFloors.LogFlips = a[0] == "1";
                    return true;
                case "unfogall":
                    map.fogGrid.ClearAllFog();
                    return true;
                case "fadekeep":
                case "fadefrom":
                {
                    float[] v = a.Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    if (key == "fadekeep")
                    {
                        FadeFloors.Keep = v;
                    }
                    else
                    {
                        FadeFloors.From = v;
                    }
                    return true;
                }
                case "pzview":
                {
                    Type fv = AccessTools.TypeByName("ProjectZ.FloorView");
                    if (fv == null)
                    {
                        Log.Warning("[IsoRim probe] pzview: ProjectZ not loaded");
                        return true;
                    }
                    AccessTools.Method(fv, "Set").Invoke(null, new object[] { I(a[0]) });
                    return true;
                }
                case "want":
                    wants[a[0]] = a[1] == "-" ? new List<IntVec3>() : a[1].Split(';').Select(CellOf).ToList();
                    return true;
                case "then":
                    HookMouse();
                    ixJobs.Add(new IxJob { kind = "set", tag = "then", mode = string.Join(",", a) });
                    return true;
                case "bdrag":
                    HookMouse();
                    ixJobs.Add(new IxJob { kind = "bdrag", mode = a[0], tag = a[5], a = new Vector3(F(a[1]), 0f, F(a[2])), b = new Vector3(F(a[3]), 0f, F(a[4])) });
                    return true;
                case "facejob":
                    pendingFace.Add(new FaceOrder { from = new IntVec3(I(a[0]), 0, I(a[1])), to = new IntVec3(I(a[2]), 0, I(a[3])), jobface = true });
                    return true;
                case "faceorder":
                case "faceshoot":
                    pendingFace.Add(new FaceOrder
                    {
                        from = new IntVec3(I(a[0]), 0, I(a[1])), to = new IntVec3(I(a[2]), 0, I(a[3])),
                        shoot = key == "faceshoot", undraft = a.Length > 4 && a[4] == "u"
                    });
                    return true;
                case "face":
                {
                    HookMouse();
                    IxJob j = new IxJob { kind = "face", tag = a[0], mode = a.Length > 2 ? a[2] : "keep", offA = new Vector2(I(a[1]), 0f) };
                    j.orders = new List<FaceOrder>(pendingFace);
                    pendingFace.Clear();
                    ixJobs.Add(j);
                    return true;
                }
            }
            return VehicleKey(key, a, map);
        }

        // ---- queue steps

        private static void SetStep(IxJob j)
        {
            // then=key:value|key:value: applied as a probe line
            string line = string.Join(" ", j.mode.Split('|').Select(kv =>
            {
                int i = kv.IndexOf(':');
                return i < 0 ? kv : kv.Substring(0, i) + "=" + kv.Substring(i + 1);
            }));
            Log.Message("[IsoRim probe] then: " + line);
            Apply(line);
            j.finished = true;
        }

        // The selection after a drag or click against want= for its tag.
        private static void CheckWant(IxJob j)
        {
            List<IntVec3> want;
            if (!wants.TryGetValue(j.tag, out want))
            {
                return;
            }
            List<IntVec3> got = Find.Selector.SelectedObjects.OfType<Thing>().Select(t => t.Position).ToList();
            bool ok = got.Count == want.Count && want.All(got.Contains);
            Check("ix " + j.tag + " (" + Yp() + ", floor viewed " + Floors.PointerTop + ")", ok,
                "want [" + string.Join(" ", want.Select(c => c.x + ":" + c.z)) + "] got [" + string.Join(" ", Find.Selector.SelectedObjects.Select(Desc)) + "]");
        }

        private static void BuildDragStep(Map map, IxJob j)
        {
            DesignatorManager dm = Find.DesignatorManager;
            Vector2 pa = ScreenOf(j.a);
            Vector2 pb = ScreenOf(j.b);
            if (j.frame == 0)
            {
                CloseWindows();
                Find.UIRoot.screenshotMode.Active = false;
                Find.Selector.ClearSelection();
                fakeMouse = pa;
                BuildableDef def = (BuildableDef)DefDatabase<TerrainDef>.GetNamedSilentFail(j.mode) ?? DefDatabase<ThingDef>.GetNamed(j.mode);
                Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(def);
                if (d == null)
                {
                    Check("ix " + j.tag + " build designator", false, "no designator for " + j.mode);
                    j.frame = 1000;
                    return;
                }
                dm.Select(d);
            }
            if (j.frame == 4)
            {
                j.note = "start cell " + Verse.UI.MouseCell();
                dm.Dragger.StartDrag();   // as DesignatorManager.ProcessInputEvents on a mouse-down
            }
            else if (j.frame > 4 && j.frame <= 12)
            {
                fakeMouse = Vector2.Lerp(pa, pb, (j.frame - 4) / 8f);
            }
            if (j.frame == 16)
            {
                IxShot(j, "drag");
            }
            if (j.frame == 20)
            {
                List<IntVec3> cells = new List<IntVec3>(dm.Dragger.DragCells);
                dm.SelectedDesignator.DesignateMultiCell(cells);   // as DesignatorManager on the mouse-up
                dm.Dragger.EndDrag();
                int blueprints = cells.Count(c => c.InBounds(map) && c.GetThingList(map).Any(t => t is Blueprint));
                Log.Message("[IsoRim probe] ix " + j.tag + " build drag " + j.mode + " (" + Yp() + "): " + j.note + ", end cell " + Verse.UI.MouseCell()
                    + ", drag cells " + cells.Count + " (z " + (cells.Count > 0 ? cells.Min(c => c.z) + ".." + cells.Max(c => c.z) : "-") + "), blueprints " + blueprints);
                Check("ix " + j.tag + " blueprints placed by the designator", blueprints > 0, blueprints + " of " + cells.Count + " cells");
                dm.Deselect();
                fakeMouse = null;
            }
            if (j.frame == 26)
            {
                IxShot(j, "placed");
            }
        }

        // While a face test runs the harness (Capture17) waits: its next "time" jumps the clock a day, which would
        // auto-undraft the colonists (AutoUndrafter: 10000 ticks standing by) and break the watch.
        private static void HoldHarness()
        {
            GameComponent runner = Current.Game?.components?.FirstOrDefault(c => c.GetType().Name == "CaptureRunner");
            if (runner != null)
            {
                Traverse.Create(runner).Field("waitUntil").SetValue(Time.frameCount + 30);
            }
        }

        private static void FaceStep(Map map, IxJob j)
        {
            HoldHarness();
            List<FaceOrder> orders = j.orders;
            int ticks = Mathf.RoundToInt(j.offA.x);
            if (j.frame == 0)
            {
                CloseWindows();
                Find.UIRoot.screenshotMode.Active = false;
                Selector sel = Find.Selector;
                foreach (FaceOrder o in orders)
                {
                    o.pawn = PawnAt(map, o.from.x, o.from.z);
                    if (o.pawn == null)
                    {
                        o.done = true;
                        o.note = "no pawn";
                        continue;
                    }
                    o.keptAtStart = KeptCount(o.pawn);
                    if (o.vehicle)
                    {
                        VehicleOrder(sel, o);
                        continue;
                    }
                    if (o.jobface)
                    {
                        // An ordered job whose driver leaves facing to UpdateRotation (rotateToFace = its target A):
                        // Wait_Combat aimed at a cell, as JobGiver_Orders' stand-by but on another cell.
                        o.pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Wait_Combat, o.to), JobTag.Misc);
                        o.orderTick = Find.TickManager.TicksGame;
                        o.note = "job " + o.pawn.CurJobDef?.defName + " facing " + o.to;
                        continue;
                    }
                    if (o.undraft)
                    {
                        o.pawn.drafter.Drafted = false;
                        o.done = true;   // watched from now on, doing its own work
                        o.rot0 = o.pawn.Rotation;
                        continue;
                    }
                    if (o.shoot)
                    {
                        o.target = o.to.GetFirstBuilding(map) ?? (Thing)o.to.GetFirstPawn(map);
                        Verb verb = o.pawn.equipment?.PrimaryEq?.PrimaryVerb;
                        if (o.target == null || verb == null)
                        {
                            o.done = true;
                            o.note = "no target or no gun";
                            continue;
                        }
                        o.target.HitPoints = 1;   // one hit ends it (then the colonist stands by, drafted)
                        verb.OrderForceTarget(new LocalTargetInfo(o.target));   // as the attack gizmo's targeter
                        o.note = "job " + o.pawn.CurJobDef?.defName;
                        continue;
                    }
                    // The right-click menu on the destination with this colonist selected, "Go here" chosen.
                    sel.ClearSelection();
                    sel.Select(o.pawn, false, false);
                    fakeMouse = ScreenOf(new Vector3(o.to.x + 0.5f, 0f, o.to.z + 0.5f));
                    FloatMenuContext ctx;
                    List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(sel.SelectedPawns, Verse.UI.MouseMapPosition(), out ctx);
                    string goHere = "GoHere".Translate();
                    FloatMenuOption go = opts?.FirstOrDefault(x => x.Label == goHere && x.action != null);
                    if (go == null)
                    {
                        o.done = true;
                        o.note = "no Go here option under the pointer (cell " + Verse.UI.MouseCell() + "; options " + (opts == null ? "none" : string.Join(" | ", opts.Select(x => x.Label))) + ")";
                        continue;
                    }
                    go.Chosen(true, null);
                    o.note = "ordered at cell " + Verse.UI.MouseCell() + ", job " + o.pawn.CurJobDef?.defName;
                }
                sel.ClearSelection();
                fakeMouse = null;
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;   // the game runs (held against the harness's pauses)
            int thisFrame = Find.TickManager.TicksThisFrame;
            bool all = true;
            foreach (FaceOrder o in orders)
            {
                Pawn p = o.pawn;
                if (p == null || !p.Spawned)
                {
                    continue;
                }
                if (!o.done)
                {
                    bool finished = o.shoot
                        ? (o.target.Destroyed && p.CurJobDef != JobDefOf.AttackStatic && !(p.stances.curStance is Stance_Busy))
                        : o.vehicle ? (!VehicleMoving(p) && p.CurJobDef != JobDefOf.Goto)
                        : o.jobface ? (p.CurJob != null && p.CurJob.targetA.Cell == o.to && Find.TickManager.TicksGame > o.orderTick + 2)
                        : (!p.pather.Moving && p.CurJobDef != JobDefOf.Goto);
                    if (finished)
                    {
                        o.done = true;
                        o.rot0 = p.Rotation;
                        o.note += ", done at " + p.Position + " facing " + p.Rotation.ToStringHuman() + ", job " + p.CurJobDef?.defName;
                    }
                    all = false;
                    continue;
                }
                if (o.rot0.IsValid && p.Rotation != o.rot0)
                {
                    o.changes++;
                    if (o.firstChange == null)
                    {
                        o.firstChange = "turned " + p.Rotation.ToStringHuman() + " after " + o.watched + " ticks (job " + p.CurJobDef?.defName + ")";
                    }
                }
                o.watched += thisFrame;
                if (o.watched < ticks)
                {
                    all = false;
                }
            }
            if (!all && j.frame < 6000)
            {
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (FaceOrder o in orders)
            {
                Pawn p = o.pawn;
                string who = (p != null ? p.LabelShort : "?") + " from " + o.from + (o.shoot ? " firing at " : " to ") + o.to;
                string detail = o.note + "; watched " + o.watched + " ticks, now facing " + (p != null ? p.Rotation.ToStringHuman() : "-")
                    + ", changes " + o.changes + (o.firstChange != null ? " (first: " + o.firstChange + ")" : "");
                if (p == null || !o.rot0.IsValid || o.watched < ticks)
                {
                    Check("face " + j.tag + " " + who, false, "not finished: " + detail);
                    continue;
                }
                if (o.undraft)
                {
                    int kept = KeptCount(p) - o.keptAtStart;
                    Check("face " + j.tag + " " + who + " undrafted: the facing patch leaves it alone", kept == 0, "patch skipped a south turn " + kept + " times; " + detail);
                    continue;
                }
                if (o.vehicle)
                {
                    // VF turns its vehicles itself (the game's UpdateRotation skipped): parked the ordered way, then kept.
                    Rot4 want = VehicleWant(o);
                    Check("face " + j.tag + " vehicle " + who + " (3D " + (View3D.Active ? "on" : "off") + ") parks facing " + want.ToStringHuman() + " (order " + o.vrot + ")",
                        o.changes == 0 && o.rot0 == want && p.Rotation == want, detail + ", drafted " + p.Drafted + ", IsoRim drafted-facing skips " + (KeptCount(p) - o.keptAtStart));
                    continue;
                }
                if (j.mode == "south")
                {
                    Check("face " + j.tag + " " + who + " (3D " + (View3D.Active ? "on" : "off") + ") turns south as the game does", p.Rotation == Rot4.South, detail);
                    continue;
                }
                List<Rot4> allowed = new List<Rot4>();
                if (o.shoot)
                {
                    allowed.Add(Pawn_RotationTracker.RotFromAngleBiased((o.to.ToVector3Shifted() - o.from.ToVector3Shifted()).AngleFlat()));
                }
                else
                {
                    IntVec3 d = o.to - o.from;
                    if (d.x > 0) allowed.Add(Rot4.East);
                    if (d.x < 0) allowed.Add(Rot4.West);
                    if (d.z > 0) allowed.Add(Rot4.North);
                    if (d.z < 0) allowed.Add(Rot4.South);
                }
                Check("face " + j.tag + " " + who + " (" + Yp() + ") keeps facing " + string.Join("/", allowed.Select(r => r.ToStringHuman())),
                    o.changes == 0 && allowed.Contains(o.rot0), detail);
            }
            IxShot(j, "end");
            j.finished = true;
        }
    }
}
