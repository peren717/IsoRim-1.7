using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for the ship roof lights (ShipRoofLights, ShipRoofs), 2026-11-13. Written so the same file also
    // builds into an older IsoRim (a base build for before/after timings): it only reads the roofs layer's meshes.
    //   roofbench=N[,label]   every block of the current map rebuilds its roofs layer N times in a row (as the game does when
    //                         roofs change); logs the total, the median of the N map-wide passes and the slowest block
    //   roofmesh=label        logs the roofs layer's pieces of mesh over the whole current map: per material, the blocks,
    //                         vertices and triangles (and the vertex total), as "[IsoRim probe] roofmesh <label> ..." lines
    //   debrisstats=label     logs the collapse debris counters (Api.Dev.DebrisStats: snapshots, cells, pieces, triangles)
    public static partial class Probe
    {
        private static bool ShipRoofKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "roofbench":
                    RoofBench(map, Mathf.Max(1, I(a[0])), a.Length > 1 ? a[1] : "-");
                    return true;
                case "debrisstats":
                    Log.Message("[IsoRim probe] debrisstats " + (a.Length > 0 ? a[0] : "-") + ": " + Api.Dev.DebrisStats + ", live " + Api.Dev.DebrisLive + ", pending " + Api.Dev.DebrisPending);
                    return true;
                case "roofmesh":
                    RoofMesh(map, a.Length > 0 ? a[0] : "-");
                    return true;
            }
            return false;
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<MapDrawer, Section[,]> roofSectionsOf = HarmonyLib.AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static List<SectionLayer> RoofLayers(Map map)
        {
            List<SectionLayer> layers = new List<SectionLayer>();
            roofBlockAt.Clear();
            Section[,] sections = roofSectionsOf(map.mapDrawer);
            foreach (Section s in sections)
            {
                SectionLayer l = s != null ? s.GetLayer(typeof(SectionLayer_IsoRimRoofs)) : null;
                if (l != null)
                {
                    layers.Add(l);
                    roofBlockAt[l] = s.botLeft.ToString();
                }
            }
            return layers;
        }

        private static readonly Dictionary<SectionLayer, string> roofBlockAt = new Dictionary<SectionLayer, string>();

        private static void RoofBench(Map map, int n, string label)
        {
            List<SectionLayer> layers = RoofLayers(map);
            List<double> passes = new List<double>();
            double slowest = 0;
            string slowestAt = "-";
            for (int k = 0; k < n; k++)
            {
                Stopwatch all = Stopwatch.StartNew();
                foreach (SectionLayer l in layers)
                {
                    Stopwatch one = Stopwatch.StartNew();
                    l.Regenerate();
                    double ms = one.Elapsed.TotalMilliseconds;
                    if (ms > slowest)
                    {
                        slowest = ms;
                        slowestAt = roofBlockAt[l];
                    }
                }
                passes.Add(all.Elapsed.TotalMilliseconds);
            }
            List<double> sorted = passes.OrderBy(x => x).ToList();
            Log.Message("[IsoRim probe] roofbench " + label + ": " + layers.Count + " blocks x " + n + ", total " + passes.Sum().ToString("0.0")
                + " ms, per map pass median " + sorted[n / 2].ToString("0.00") + " ms (min " + sorted[0].ToString("0.00") + ", max " + sorted[n - 1].ToString("0.00")
                + "), slowest block " + slowest.ToString("0.00") + " ms at " + slowestAt);
        }

        private static void RoofMesh(Map map, string label)
        {
            Dictionary<string, int[]> byMat = new Dictionary<string, int[]>();
            int verts = 0;
            foreach (SectionLayer l in RoofLayers(map))
            {
                foreach (LayerSubMesh sm in l.subMeshes)
                {
                    if (sm.verts.Count == 0)
                    {
                        continue;
                    }
                    string name = sm.material != null ? sm.material.name : "null";
                    int v = sm.verts.Count;
                    int t = sm.tris.Count / 3;
                    int[] c;
                    if (!byMat.TryGetValue(name, out c))
                    {
                        byMat[name] = c = new int[3];
                    }
                    if (c[0] == 0 && sm.material != null && name.Contains("Glow"))
                    {
                        Log.Message("[IsoRim probe] roofmesh " + label + " " + name + " colour " + sm.material.color + ", sky " + IsoGlow.SkyFor(map));
                    }
                    c[0]++;
                    c[1] += v;
                    c[2] += t;
                    verts += v;
                }
            }
            foreach (KeyValuePair<string, int[]> kv in byMat.OrderBy(kv => kv.Key))
            {
                Log.Message("[IsoRim probe] roofmesh " + label + " " + kv.Key + ": " + kv.Value[0] + " blocks, " + kv.Value[1] + " vertices, " + kv.Value[2] + " triangles");
            }
            Log.Message("[IsoRim probe] roofmesh " + label + " total: " + byMat.Count + " materials, " + verts + " vertices");
        }
    }
}
