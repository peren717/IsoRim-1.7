using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for smooth shading on people (SmoothFigures.cs; the settings: set=smoothPeople,0|1 set=smoothCrease,<deg>):
    //   smoothforget=1               every person's made meshes and the cached smooth normals thrown away, the timers zeroed
    //                                (they are made again the next time each person is drawn)
    //   smoothreport=label           per humanlike: a hash of the figure meshes as last made (vertices, colours, triangles,
    //                                normals), and the time spent making them since smoothforget
    //   smoothhold=save|restore      save: every humanlike's cell and facing; restore: each put back there, facing that way,
    //                                drafted (standing still), its tracked turn forgotten and the speech bubbles removed (the same pose in every pass of a sheet)
    public static partial class Probe
    {
        private static readonly Dictionary<Pawn, (IntVec3 cell, Rot4 rot)> held = new Dictionary<Pawn, (IntVec3, Rot4)>();

        private static bool SmoothKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "smoothhold":
                {
                    int n = 0;
                    if (a[0] == "save")
                    {
                        held.Clear();
                        foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike))
                        {
                            held[p] = (p.Position, p.Rotation);
                            n++;
                        }
                    }
                    else
                    {
                        foreach (KeyValuePair<Pawn, (IntVec3 cell, Rot4 rot)> h in held)
                        {
                            Pawn p = h.Key;
                            if (!p.Spawned)
                            {
                                continue;
                            }
                            if (p.Position != h.Value.cell || p.Rotation != h.Value.rot)
                            {
                                Log.Message("[IsoRim probe] smoothhold " + p.LabelShort + " was at " + p.Position + " " + p.Rotation + ", put back to "
                                    + h.Value.cell + " " + h.Value.rot + " (job " + p.CurJobDef?.defName + ")");
                            }
                            p.pather?.StopDead();
                            if (p.Position != h.Value.cell)
                            {
                                p.Position = h.Value.cell;
                            }
                            if (p.drafter != null && !p.Drafted)
                            {
                                p.drafter.Drafted = true;   // standing still (Wait_Combat)
                            }
                            p.Rotation = h.Value.rot;
                            p.Drawer.tweener.ResetTweenedPosToRoot();
                            // the figure's tracked turn forgotten: a tiny jump of the drawn position would read as a step
                            // and turn it the way of that step (Animals.Advance); drawn next as standing, facing its rotation
                            ((System.Collections.IDictionary)AccessTools.Field(typeof(Animals), "states").GetValue(null)).Remove(p.thingIDNumber);
                            n++;
                        }
                        foreach (Thing t in map.listerThings.AllThings.Where(t => t is Mote).ToList())
                        {
                            t.Destroy();
                        }
                    }
                    Log.Message("[IsoRim probe] smoothhold " + a[0] + " " + n + " pawns");
                    return true;
                }
                case "smoothforget":
                {
                    Dictionary<int, People.Look> all = (Dictionary<int, People.Look>)AccessTools.Field(typeof(People), "looks").GetValue(null);
                    foreach (People.Look l in all.Values)
                    {
                        l.Forget();
                    }
                    DynamicModels.Forget();
                    SmoothFigures.Clear();
                    People.MergeTicks = 0;
                    People.Merges = 0;
                    Log.Message("[IsoRim probe] smoothforget " + all.Count + " looks");
                    return true;
                }
                case "smoothreport":
                {
                    double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Log.Message("[IsoRim probe] smoothreport " + a[0] + ": smooth " + SmoothFigures.On + " crease " + SmoothFigures.Crease
                        + ", " + People.Merges + " figure sets made in " + (People.MergeTicks * tick).ToString("0.00") + " ms ("
                        + (People.Merges > 0 ? (People.MergeTicks * tick / People.Merges).ToString("0.000") : "-") + " ms each), smooth normals of "
                        + SmoothFigures.Worked + " models in " + (SmoothFigures.Ticks * tick).ToString("0.00") + " ms");
                    ulong total = 14695981039346656037UL;
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike).OrderBy(x => x.Position.z).ThenBy(x => x.Position.x))
                    {
                        People.Look look = People.LastLook(p);
                        if (look == null)
                        {
                            continue;
                        }
                        ulong h = 14695981039346656037UL, g = 14695981039346656037UL;   // g: geometry only (no colours, which differ run to run)
                        int meshes = 0, verts = 0, withNormals = 0;
                        foreach (int k in look.meshes.Keys.OrderBy(k => k))
                        {
                            foreach (DynamicModels.Part part in look.meshes[k])
                            {
                                Mesh m = part.mesh;
                                if (m == null)
                                {
                                    continue;
                                }
                                meshes++;
                                verts += m.vertexCount;
                                h = Mix(h, k);
                                foreach (Vector3 v in m.vertices)
                                {
                                    h = Mix(h, v.x.GetHashCode());
                                    h = Mix(h, v.y.GetHashCode());
                                    h = Mix(h, v.z.GetHashCode());
                                    g = Mix(g, v.x.GetHashCode());
                                    g = Mix(g, v.y.GetHashCode());
                                    g = Mix(g, v.z.GetHashCode());
                                }
                                foreach (Color32 c in m.colors32)
                                {
                                    h = Mix(h, c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                                }
                                foreach (int t in m.triangles)
                                {
                                    h = Mix(h, t);
                                    g = Mix(g, t);
                                }
                                Vector3[] ns = m.normals;
                                if (ns != null && ns.Length > 0)
                                {
                                    withNormals++;
                                    foreach (Vector3 v in ns)
                                    {
                                        h = Mix(h, v.x.GetHashCode());
                                        h = Mix(h, v.y.GetHashCode());
                                        h = Mix(h, v.z.GetHashCode());
                                        g = Mix(g, v.x.GetHashCode());
                                        g = Mix(g, v.y.GetHashCode());
                                        g = Mix(g, v.z.GetHashCode());
                                    }
                                }
                            }
                        }
                        total = Mix(total, (int)(h ^ (h >> 32)));
                        Log.Message("[IsoRim probe] smoothreport " + a[0] + " " + p.Position.x + "," + p.Position.z + ": " + meshes + " meshes, " + verts
                            + " verts, " + withNormals + " with normals, hash " + h.ToString("x16") + " geo " + g.ToString("x16") + ", rot " + p.Rotation + " drawn yaw "
                            + DrawnYaw(p).ToString("0") + " drawn off " + (p.DrawPos - p.Position.ToVector3Shifted()).ToString("F3")
                            + " job " + p.CurJobDef?.defName + " drafted " + p.Drafted);
                    }
                    Log.Message("[IsoRim probe] smoothreport " + a[0] + " all: hash " + total.ToString("x16"));
                    return true;
                }
            }
            return false;
        }

        // The figure's drawn turn as last tracked (Animals.Track), or NaN.
        private static float DrawnYaw(Pawn p)
        {
            System.Collections.IDictionary states = (System.Collections.IDictionary)AccessTools.Field(typeof(Animals), "states").GetValue(null);
            object st = states.Contains(p.thingIDNumber) ? states[p.thingIDNumber] : null;
            return st != null ? Traverse.Create(st).Field<float>("yaw").Value : float.NaN;
        }

        private static ulong Mix(ulong h, int v)
        {
            return (h ^ (uint)v) * 1099511628211UL;
        }
    }
}
