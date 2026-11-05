using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for tubes (Api 58, ShapeTubes). Checks read the mesh the game really baked (the block's things layer), not the
    // rule, so a piece left stale after a neighbour changed fails. Lines start "[IsoRim tube] PASS|FAIL".
    //   tubewant=x,z,bits,<label>        the conduit there joins these sides (1 N, 2 E, 4 S, 8 W) and its cell's mesh holds that very piece
    //   tubeflat=x0,z0,x1,z1,n,<label>   n link sprite triangles (of a conduit atlas) baked over the rectangle
    //   tubenone=x,z,<label>             no tube triangles baked in the cell
    //   tubedecon=x,z                    a deconstruct order on the conduit there (colonists do it: buildall does not, use tick=)
    //   tubehurt=x,z,hp                  the conduit there brought to that many hit points
    //   tubeframe=x,z                    a conduit frame in the cell
    //   tubego=x,z[,fromx,fromz]         the colonist nearest the cell (or the one standing at from) drafted and sent there (walks the real path)
    //   tubefield=x0,z0,x1,z1,step[,def] cost fixture: conduit (or that def's) rows every step cells, tied by a column at each end (spawned, not built)
    //   tubedesig=<ThingDef>|off         that def's build designator in hand (a pipe's shows its net overlay)
    //   tubecell=x,z                     logs what stands in the cell
    //   tubeflick=x,z                    a flick order to close the switch of the building there (a colonist does it)
    //   tubenet=x,z,x2,z2,same|diff,<l>  the two VE pipe things are on the same net, or not (PipeSystem's CompResource.PipeNet)
    //   tuberegen=N,<label>              the whole map rebuilt N times, timed; baked vertices by kind and memory logged
    //   tubereload=<save>,cx,cz,size     (last pass) that save loaded; all tubewant checks again, a shot, the game quits
    public static partial class Probe
    {
        private struct TubeWant
        {
            public IntVec3 cell;
            public int bits;
            public string label;
        }

        private static readonly List<TubeWant> tubeWants = new List<TubeWant>();
        private static string tubeReload;
        private static Game tubeReloadFrom;
        private static int tubeReloadFrames = -1;
        private static Vector3 tubeShot;

        private static void TubeLine(bool pass, string text)
        {
            Log.Message("[IsoRim tube] " + (pass ? "PASS " : "FAIL ") + text);
        }

        private static Building TubeThingAt(Map map, IntVec3 c)
        {
            return c.GetThingList(map).OfType<Building>().FirstOrDefault(t => t.def.building != null && (t.def.building.isPowerConduit || ShapeTubes.IsTube(t.def)));
        }

        // Triangles of tube meshes (the round material) and of conduit atlas sprites with their middle in the rectangle.
        private static void TubeTriangles(Map map, CellRect r, out int round, out int sprite, float pad = 0f)
        {
            round = 0;
            sprite = 0;
            HashSet<Section> seen = new HashSet<Section>();
            foreach (IntVec3 c in r)
            {
                Section sec = map.mapDrawer.SectionAt(c);
                if (!seen.Add(sec))
                {
                    continue;
                }
                SectionLayer layer = sec.GetLayer(typeof(SectionLayer_ThingsGeneral));
                if (layer == null)
                {
                    continue;
                }
                foreach (LayerSubMesh sm in layer.subMeshes)
                {
                    bool tube = sm.material == R3DMaterials.LitRound;
                    bool atlas = sm.material != null && sm.material.mainTexture != null && sm.material.mainTexture.name != null
                        && sm.material.mainTexture.name.IndexOf("_Atlas", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!tube && !atlas)
                    {
                        continue;
                    }
                    for (int k = 0; k + 2 < sm.tris.Count; k += 3)
                    {
                        Vector3 mid = (sm.verts[sm.tris[k]] + sm.verts[sm.tris[k + 1]] + sm.verts[sm.tris[k + 2]]) / 3f;
                        if (mid.x >= r.minX - pad && mid.x <= r.maxX + 1f + pad && mid.z >= r.minZ - pad && mid.z <= r.maxZ + 1f + pad)
                        {
                            if (tube)
                            {
                                round++;
                            }
                            else
                            {
                                sprite++;
                            }
                        }
                    }
                }
            }
        }

        private static void TubeCheck(Map map, TubeWant w, string when)
        {
            Building b = TubeThingAt(map, w.cell);
            if (b == null)
            {
                TubeLine(false, when + w.label + ": no conduit at " + w.cell);
                return;
            }
            int ends;
            int bits = ShapeTubes.LinksOf(b, out ends);
            ThingForm f = Forms.For(b.def);
            int want = ShapeTubes.IsTube(b.def) ? ShapeTubes.PieceFor(f.model, b).positions.Length / 3 : -1;
            int round, sprite;
            TubeTriangles(map, CellRect.SingleCell(w.cell), out round, out sprite, 0.07f);   // an arm's closed end lies just over the edge
            TubeLine(bits == w.bits && round == want && sprite == 0, when + w.label + " " + w.cell + ": joins " + bits + " (want " + w.bits + "), mesh tris " + round
                + " (want " + want + "), sprite tris " + sprite);
        }

        private static bool TubeKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "tubewant":
                {
                    TubeWant w = new TubeWant { cell = new IntVec3(I(a[0]), 0, I(a[1])), bits = I(a[2]), label = a[3] };
                    tubeWants.RemoveAll(o => o.cell == w.cell);
                    tubeWants.Add(w);
                    TubeCheck(map, w, "");
                    return true;
                }
                case "tubeart":
                {
                    // tubeart=<ThingDef>,...: the measured width and colour of each def
                    foreach (string name in a)
                    {
                        ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                        if (d == null)
                        {
                            continue;
                        }
                        ShapeTubes.Look look = new ShapeTubes.Look();
                        ShapeTubes.Measure(d.graphic as Graphic_Linked, look);
                        Log.Message("[IsoRim tube] art " + name + ": measured " + look.measured + ", body width " + look.width.ToString("0.000") + " cells (with outline " + look.whole.ToString("0.000") + "), tube colour " + look.color);
                    }
                    return true;
                }
                case "tubeflat":
                {
                    int round, sprite;
                    TubeTriangles(map, CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3])), out round, out sprite);
                    TubeLine(sprite == I(a[4]), a[5] + ": sprite tris " + sprite + " (want " + a[4] + "), tube tris " + round);
                    return true;
                }
                case "tubenone":
                {
                    int round, sprite;
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    TubeTriangles(map, CellRect.SingleCell(c), out round, out sprite, -0.2f);   // an arm's closed end may reach just in
                    TubeLine(round == 0, a[2] + " " + c + ": tube tris " + round + ", sprite tris " + sprite);
                    return true;
                }
                case "tubedecon":
                {
                    Building b = TubeThingAt(map, new IntVec3(I(a[0]), 0, I(a[1])));
                    if (b == null)
                    {
                        b = new IntVec3(I(a[0]), 0, I(a[1])).GetEdifice(map);
                    }
                    if (b != null)
                    {
                        map.designationManager.AddDesignation(new Designation(b, DesignationDefOf.Deconstruct));
                    }
                    Log.Message("[IsoRim probe] tubedecon " + (b != null ? b.def.defName + " " + b.Position : "nothing"));
                    return true;
                }
                case "tubehurt":
                {
                    Building b = TubeThingAt(map, new IntVec3(I(a[0]), 0, I(a[1])));
                    if (b != null)
                    {
                        b.HitPoints = I(a[2]);
                        map.mapDrawer.MapMeshDirty(b.Position, MapMeshFlagDefOf.BuildingsDamage);
                    }
                    return true;
                }
                case "tubego":
                {
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    Pawn p = a.Length > 3 ? PawnAt(map, I(a[2]), I(a[3]))
                        : map.mapPawns.FreeColonistsSpawned.Where(o => !o.Drafted).OrderBy(o => o.Position.DistanceToSquared(c)).ThenBy(o => o.thingIDNumber).FirstOrDefault();
                    if (p != null)
                    {
                        p.drafter.Drafted = true;
                        Verse.AI.Job j = JobMaker.MakeJob(JobDefOf.Goto, c);
                        j.playerForced = true;
                        p.jobs.StartJob(j, Verse.AI.JobCondition.InterruptForced);
                    }
                    Log.Message("[IsoRim probe] tubego " + c + ": " + (p != null ? p.LabelShort + " from " + p.Position : "nobody"));
                    return true;
                }
                case "tubedesig":
                    if (a[0] == "off")
                    {
                        Find.DesignatorManager.Deselect();
                    }
                    else
                    {
                        Find.DesignatorManager.Select(new Designator_Build(DefDatabase<ThingDef>.GetNamed(a[0])));
                    }
                    return true;
                case "tubecell":
                    Log.Message("[IsoRim tube] cell " + a[0] + "," + a[1] + ": " + string.Join(", ", new IntVec3(I(a[0]), 0, I(a[1])).GetThingList(map).Select(t => t.def.defName)));
                    return true;
                case "tubeflick":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetThingList(map).OfType<Building>().FirstOrDefault(t => t.TryGetComp<CompFlickable>() != null);
                    CompFlickable fl = b != null ? b.TryGetComp<CompFlickable>() : null;
                    if (fl != null)
                    {
                        HarmonyLib.Traverse.Create(fl).Field("wantSwitchOn").SetValue(false);
                        FlickUtility.UpdateFlickDesignation(b);
                    }
                    Log.Message("[IsoRim probe] tubeflick " + (b != null ? b.def.defName + " " + b.Position : "nothing"));
                    return true;
                }
                case "tubenet":
                {
                    object n1 = TubeNet(map, new IntVec3(I(a[0]), 0, I(a[1]))), n2 = TubeNet(map, new IntVec3(I(a[2]), 0, I(a[3])));
                    bool same = n1 != null && ReferenceEquals(n1, n2);
                    TubeLine((a[4] == "same") == same && n1 != null && n2 != null, a[5] + ": nets " + (n1 != null ? n1.GetHashCode().ToString() : "none") + ", " + (n2 != null ? n2.GetHashCode().ToString() : "none"));
                    return true;
                }
                case "tubeframe":
                {
                    Thing frame = ThingMaker.MakeThing(ThingDefOf.PowerConduit.frameDef);
                    frame.SetFactionDirect(Faction.OfPlayer);
                    GenSpawn.Spawn(frame, new IntVec3(I(a[0]), 0, I(a[1])), map);
                    return true;
                }
                case "tubefield":
                {
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    int step = I(a[4]), n = 0;
                    foreach (IntVec3 c in r)
                    {
                        bool row = (c.z - r.minZ) % step == 0;
                        bool end = c.x == r.minX || c.x == r.maxX;
                        if (row || end)
                        {
                            Thing t = ThingMaker.MakeThing(a.Length > 5 ? DefDatabase<ThingDef>.GetNamed(a[5]) : ThingDefOf.PowerConduit);
                            t.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(t, c, map);
                            n++;
                        }
                    }
                    Log.Message("[IsoRim tube] field: " + n + " conduits over " + r);
                    return true;
                }
                case "tuberegen":
                {
                    int times = Math.Max(1, I(a[0]));
                    map.mapDrawer.RegenerateEverythingNow();   // warm
                    GC.Collect();
                    long before = GC.GetTotalMemory(true);
                    List<double> ms = new List<double>();
                    for (int i = 0; i < times; i++)
                    {
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        map.mapDrawer.RegenerateEverythingNow();
                        ms.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    ms.Sort();
                    long tubeVerts = 0, allVerts = 0;
                    int subMeshes = 0, sections = 0;
                    foreach (Section sec in SectionsOf(map))
                    {
                        sections++;
                        SectionLayer layer = sec.GetLayer(typeof(SectionLayer_ThingsGeneral));
                        if (layer == null)
                        {
                            continue;
                        }
                        foreach (LayerSubMesh sm in layer.subMeshes)
                        {
                            subMeshes++;
                            allVerts += sm.verts.Count;
                            if (sm.material == R3DMaterials.LitRound)
                            {
                                tubeVerts += sm.verts.Count;
                            }
                        }
                    }
                    GC.Collect();
                    System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
                    Log.Message("[IsoRim tube] cost " + a[1] + ": conduits " + map.listerThings.ThingsOfDef(ThingDefOf.PowerConduit).Count + ", buildings " + map.listerBuildings.allBuildingsColonist.Count + ", whole map rebuild median "
                        + ms[ms.Count / 2].ToString("0.0") + " ms (min " + ms[0].ToString("0.0") + ", max " + ms[ms.Count - 1].ToString("0.0") + ", " + times
                        + " runs); things layer verts " + allVerts + " (tube " + tubeVerts + ") in " + subMeshes + " submeshes over " + sections + " blocks; managed "
                        + (GC.GetTotalMemory(true) / 1048576.0).ToString("0.0") + " MB (before " + (before / 1048576.0).ToString("0.0") + "), private "
                        + (proc.PrivateMemorySize64 / 1048576.0).ToString("0") + " MB");
                    return true;
                }
                case "tubereload":
                {
                    System.Reflection.FieldInfo cap = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("Capture17.Main"), "Active");
                    if (cap != null)
                    {
                        cap.SetValue(null, false);
                    }
                    tubeReload = a[0];
                    tubeReloadFrom = Current.Game;
                    tubeReloadFrames = 0;
                    tubeShot = new Vector3(F(a[1]), F(a[3]), F(a[2]));
                    GameDataSaveLoader.LoadGame(a[0]);
                    return true;
                }
            }
            return false;
        }

        private static object TubeNet(Map map, IntVec3 c)
        {
            foreach (Thing t in c.GetThingList(map))
            {
                ThingWithComps tc = t as ThingWithComps;
                ThingComp comp = tc != null ? tc.AllComps.FirstOrDefault(x => x.GetType().FullName.StartsWith("PipeSystem.CompResource")) : null;
                if (comp != null)
                {
                    return HarmonyLib.Traverse.Create(comp).Property("PipeNet").GetValue();
                }
            }
            return null;
        }

        private static IEnumerable<Section> SectionsOf(Map map)
        {
            for (int x = 0; x < map.Size.x; x += Section.Size)
            {
                for (int z = 0; z < map.Size.z; z += Section.Size)
                {
                    yield return map.mapDrawer.SectionAt(new IntVec3(x, 0, z));
                }
            }
        }

        // After tubereload: frames to settle, the checks, a shot, quit.
        private static void TubeReloadTick()
        {
            if (tubeReload == null || Current.Game == tubeReloadFrom || LongEventHandler.ShouldWaitForEvent || Find.CurrentMap == null
                || Find.CurrentMap.listerThings.AllThings.Count == 0)
            {
                return;
            }
            Map map = Find.CurrentMap;
            tubeReloadFrames++;
            if (tubeReloadFrames == 2)
            {
                Find.WindowStack.Windows.ToList().ForEach(w => w.Close(false));
                View3D.Active = true;
                Find.CameraDriver.config.sizeRange.min = 1f;
                Find.CameraDriver.SetRootPosAndSize(new Vector3(tubeShot.x, 0f, tubeShot.z), tubeShot.y);
            }
            if (tubeReloadFrames == 90)
            {
                foreach (TubeWant w in tubeWants)
                {
                    TubeCheck(map, w, "after reload: ");
                }
                string path = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", "tube_reload.png");
                Find.UIRoot.screenshotMode.Active = true;
                ScreenCapture.CaptureScreenshot(path);
            }
            if (tubeReloadFrames == 150)
            {
                Log.Message("[IsoRim tube] reload checks done, quitting");
                Root.Shutdown();
            }
        }
    }
}
