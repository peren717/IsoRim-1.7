using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only keys for smooth shading on animals (SmoothFigures.cs; the setting: set=smoothAnimals,0|1). Animals are spawned
    // with mech=x,z,<kind>,player,<age>,<rot>[,dead|down] (ProbeMechs).
    //   animalpose=x,z,<state>,<rot>  the pawn in the cell held as stand (waiting), sleep (lying down there, tired) or fly (waiting,
    //                                 then taking off: Pawn_FlightTracker.StartFlying); remembered for animalhold
    //   animalhold=1                  every remembered pawn put back in its cell and facing, its tracked turn forgotten, its pose
    //                                 given again (the same pose in every pass of a sheet)
    //   animalforget=1                every ready-made model mesh (DynamicModels) and the cached smooth normals thrown away
    //   animalreport=label            a hash of every ready-made model mesh (DynamicModels.PartsFor: animals' parts and the other
    //                                 moving models), in key order: all (vertices, colours, triangles, normals) and geometry only,
    //                                 how many meshes carry normals, and the smooth normals worked out since animalforget
    public static partial class Probe
    {
        private static readonly Dictionary<Pawn, (IntVec3 cell, Rot4 rot, string state)> poses = new Dictionary<Pawn, (IntVec3, Rot4, string)>();

        private static bool SmoothAnimalKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "animalpose":
                {
                    Pawn p = PawnAt(map, int.Parse(a[0]), int.Parse(a[1]));
                    if (p != null)
                    {
                        poses[p] = (p.Position, new Rot4(int.Parse(a[3])), a[2]);
                        Pose(p, poses[p]);
                    }
                    return true;
                }
                case "animalhold":
                {
                    int n = 0;
                    foreach (KeyValuePair<Pawn, (IntVec3 cell, Rot4 rot, string state)> h in poses)
                    {
                        if (h.Key.Spawned && !h.Key.Dead)
                        {
                            Pose(h.Key, h.Value);
                            n++;
                        }
                    }
                    foreach (Thing t in map.listerThings.AllThings.Where(t => t is Mote).ToList())
                    {
                        t.Destroy();
                    }
                    Log.Message("[IsoRim probe] animalhold " + n + " pawns");
                    return true;
                }
                case "animalforget":
                {
                    DynamicModels.Forget();
                    SmoothFigures.Clear();
                    Log.Message("[IsoRim probe] animalforget");
                    return true;
                }
                case "animalreport":
                {
                    double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    var field = AccessTools.Field(typeof(IsoRimSettings), "smoothAnimals");
                    string on = field != null ? field.GetValue(IsoRimMod.Settings).ToString() : "(no setting)";
                    Dictionary<long, List<DynamicModels.Part>> cache = (Dictionary<long, List<DynamicModels.Part>>)AccessTools.Field(typeof(DynamicModels), "cache").GetValue(null);
                    // only the models of the posed kinds (not mechanoids): wandering wild animals and the like left out
                    HashSet<int> ids = new HashSet<int>();
                    foreach (ThingDef def in poses.Keys.Where(p => !p.RaceProps.IsMechanoid).Select(p => p.def).Distinct())
                    {
                        ModelEntry entry;
                        if (ModelLibrary.TryGet(def, out entry))
                        {
                            foreach (Model mo in (entry.variants ?? new Model[0]).Concat(entry.female ?? new Model[0]).Concat(new[] { entry.leg, entry.wing, entry.wingMirror, entry.femaleLeg, entry.femaleWing, entry.femaleWingMirror }))
                            {
                                if (mo != null)
                                {
                                    ids.Add(mo.id);
                                }
                            }
                        }
                    }
                    ulong h = 14695981039346656037UL, g = 14695981039346656037UL;
                    int meshes = 0, verts = 0, withNormals = 0, others = 0, othersWithNormals = 0;
                    foreach (long k in cache.Keys.OrderBy(k => k))
                    {
                        if (!ids.Contains((int)(k >> 34)))
                        {
                            foreach (DynamicModels.Part part in cache[k])
                            {
                                others++;
                                if (part.mesh != null && part.mesh.normals != null && part.mesh.normals.Length > 0)
                                {
                                    othersWithNormals++;
                                }
                            }
                            continue;
                        }
                        foreach (DynamicModels.Part part in cache[k])
                        {
                            Mesh m = part.mesh;
                            if (m == null)
                            {
                                continue;
                            }
                            meshes++;
                            verts += m.vertexCount;
                            h = Mix(h, (int)k);
                            h = Mix(h, (int)(k >> 32));
                            g = Mix(g, (int)k);
                            g = Mix(g, (int)(k >> 32));
                            foreach (Vector3 v in m.vertices)
                            {
                                h = Mix(Mix(Mix(h, v.x.GetHashCode()), v.y.GetHashCode()), v.z.GetHashCode());
                                g = Mix(Mix(Mix(g, v.x.GetHashCode()), v.y.GetHashCode()), v.z.GetHashCode());
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
                            List<Vector4> tags = new List<Vector4>();
                            m.GetUVs(1, tags);
                            foreach (Vector4 t in tags)
                            {
                                g = Mix(g, t.z.GetHashCode());
                            }
                            Vector3[] ns = m.normals;
                            if (ns != null && ns.Length > 0)
                            {
                                withNormals++;
                                foreach (Vector3 v in ns)
                                {
                                    h = Mix(Mix(Mix(h, v.x.GetHashCode()), v.y.GetHashCode()), v.z.GetHashCode());
                                    g = Mix(Mix(Mix(g, v.x.GetHashCode()), v.y.GetHashCode()), v.z.GetHashCode());
                                }
                            }
                        }
                    }
                    Log.Message("[IsoRim probe] animalreport " + a[0] + ": smoothAnimals " + on + " crease " + SmoothFigures.Crease + ", " + cache.Count
                        + " cached models (" + ids.Count + " animal model ids; other meshes " + others + ", " + othersWithNormals + " with normals), animal: " + meshes + " meshes, " + verts + " verts, " + withNormals + " with normals, hash " + h.ToString("x16")
                        + " geo " + g.ToString("x16") + "; smooth normals of " + SmoothFigures.Worked + " models in "
                        + (SmoothFigures.Ticks * tick).ToString("0.00") + " ms");
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => !x.RaceProps.Humanlike).OrderBy(x => x.Position.z).ThenBy(x => x.Position.x))
                    {
                        Log.Message("[IsoRim probe] animalreport " + a[0] + " " + p.def.defName + " " + p.Position.x + "," + p.Position.z + ": posture "
                            + p.GetPosture() + " flying " + (p.flight != null && p.flight.Flying) + " air " + Animals.AirborneFactor(p).ToString("0.00")
                            + " job " + p.CurJobDef?.defName + " rot " + p.Rotation + " drawn yaw " + DrawnYaw(p).ToString("0"));
                    }
                    return true;
                }
            }
            return FemaleKey(key, a, map);   // (ProbeFemale)
        }

        private static void Pose(Pawn p, (IntVec3 cell, Rot4 rot, string state) at)
        {
            if (p.flight != null && p.flight.Flying && at.state != "fly")
            {
                p.flight.ForceLand();
            }
            p.pather?.StopDead();
            if (p.Position != at.cell)
            {
                p.Position = at.cell;
            }
            p.Rotation = at.rot;
            p.Drawer.tweener.ResetTweenedPosToRoot();
            ((System.Collections.IDictionary)AccessTools.Field(typeof(Animals), "states").GetValue(null)).Remove(p.thingIDNumber);
            Job job;
            if (at.state == "sleep")
            {
                if (p.needs?.rest != null)
                {
                    p.needs.rest.CurLevel = 0.05f;
                }
                job = JobMaker.MakeJob(JobDefOf.LayDown, at.cell);
            }
            else
            {
                job = JobMaker.MakeJob(JobDefOf.Wait);
            }
            job.expiryInterval = 100000;
            job.checkOverrideOnExpire = false;
            p.jobs.StartJob(job, JobCondition.InterruptForced);
            p.Rotation = at.rot;
            if (at.state != "fly" && p.flight != null && p.flight.Flying)
            {
                p.flight.ForceLand();   // a bird may take off as a job starts (flightStartChanceOnJobStart)
            }
            if (at.state == "fly" && p.flight != null)
            {
                if (!p.flight.Flying)
                {
                    AccessTools.Field(typeof(Pawn_FlightTracker), "flightCooldownTicks").SetValue(p.flight, 0);   // again in a later pass
                    p.flight.StartFlying();
                }
                AccessTools.Field(typeof(Pawn_FlightTracker), "flyingTicks").SetValue(p.flight, 0);   // a full flight from now on
            }
            Log.Message("[IsoRim probe] animalpose " + p.def.defName + " " + at.cell + " " + at.state + ": job " + p.CurJobDef?.defName + " posture " + p.GetPosture()
                + " flying " + (p.flight != null && p.flight.Flying) + " can fly " + (p.flight != null && p.flight.CanEverFly));
        }
    }
}
