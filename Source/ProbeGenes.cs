using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness-only checks of gene parts (Api 35, GeneParts.cs). Keys:
    //   geneyield=<packageId>,<Gene:Gene..>|all|except:<Gene:Gene..>   a test GenePartsYieldDef (that mod; those genes, all
    //                                             genes, or all but those) added and the XML yields
    //                                             resolved again, as at startup; geneyield=clear removes the test defs
    //   geneapiyield=<owner>,<Gene:Gene..>|all|except:<Gene:Gene..>|stop  Api.YieldGeneParts / YieldAllGeneParts /
    //                                             YieldAllGenePartsExcept / StopYieldingGeneParts
    //   genecheck=x,z,<ModelDef:ModelDef..>|none  the person in the cell: the gene part defs in their look as last worked out,
    //                                             the head tint and hair, against the expected list -> CHECK ok / FAILED
    //   genereport=label                          per humanlike: xenotype, body, gene part defs drawn, head tint, hair
    // dressed=... takes an item xeno:<XenotypeDef> (the colonist made of that xenotype, ProbeScene).
    public static partial class Probe
    {
        private static readonly List<GenePartsYieldDef> testYields = new List<GenePartsYieldDef>();

        private static bool GeneKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "geneyield":
                {
                    if (a[0] == "clear")
                    {
                        foreach (GenePartsYieldDef d in testYields)
                        {
                            AccessTools.Method(typeof(DefDatabase<GenePartsYieldDef>), "Remove").Invoke(null, new object[] { d });
                        }
                        testYields.Clear();
                    }
                    else
                    {
                        GenePartsYieldDef d = new GenePartsYieldDef
                        {
                            defName = "R3D_TestYield" + testYields.Count,
                            packageIds = new List<string> { a[0] },
                            genes = a.Length > 1 && a[1] != "all" && !a[1].StartsWith("except:") ? a[1].Split(':').ToList() : null,
                            exceptGenes = a.Length > 1 && a[1].StartsWith("except:") ? a[1].Substring(7).Split(':').ToList() : null
                        };
                        DefDatabase<GenePartsYieldDef>.Add(d);
                        testYields.Add(d);
                    }
                    GeneParts.ResolveYieldDefs();
                    Log.Message("[IsoRim probe] geneyield " + string.Join(",", a) + ": active yield defs [" + string.Join(", ", GeneParts.ActiveYieldDefs)
                        + "]; yielded " + YieldedNames());
                    return true;
                }
                case "geneapiyield":
                    if (a[1] == "stop")
                    {
                        Api.StopYieldingGeneParts(a[0]);
                    }
                    else if (a[1] == "all")
                    {
                        Api.YieldAllGeneParts(a[0]);
                    }
                    else if (a[1].StartsWith("except:"))
                    {
                        Api.YieldAllGenePartsExcept(a[0], a[1].Substring(7).Split(':').Select(n => DefDatabase<GeneDef>.GetNamed(n)));
                    }
                    else
                    {
                        Api.YieldGeneParts(a[0], a[1].Split(':').Select(n => DefDatabase<GeneDef>.GetNamed(n)));
                    }
                    Log.Message("[IsoRim probe] geneapiyield " + string.Join(",", a) + ": yielded " + YieldedNames());
                    return true;
                case "genecheck":
                {
                    IntVec3 at = new IntVec3(I(a[0]), 0, I(a[1]));
                    Pawn p = map.thingGrid.ThingsListAt(at).OfType<Pawn>().FirstOrDefault()
                        ?? GenAdj.CellsAdjacent8Way(new TargetInfo(at, map)).Where(c => c.InBounds(map)).SelectMany(c => map.thingGrid.ThingsListAt(c).OfType<Pawn>()).FirstOrDefault();   // (a drafted one may have stepped aside)
                    People.Look look = p != null ? People.LastLook(p) : null;
                    List<string> want = a[2] == "none" ? new List<string>() : a[2].Split(':').ToList();
                    List<string> got = GeneDefNames(look);
                    bool ok = look != null && want.OrderBy(x => x).SequenceEqual(got.OrderBy(x => x));
                    Log.Message("[IsoRim probe] genecheck " + a[0] + "," + a[1] + " " + (p != null ? p.LabelShort + " " + Xeno(p) : "no pawn")
                        + ": drawn [" + string.Join(" ", got) + "], expected [" + string.Join(" ", want) + "]" + (look != null ? ", head tint " + look.geneHeadTint
                        + ", hair " + Name(look.hair) + ", beard " + Name(look.beard) : ", no look") + " -> " + (ok ? "CHECK ok" : "CHECK FAILED"));
                    return true;
                }
                case "genereport":
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike).OrderBy(x => x.Position.z).ThenBy(x => x.Position.x))
                    {
                        People.Look look = People.LastLook(p);
                        Log.Message("[IsoRim probe] genereport " + a[0] + " " + p.Position.x + "," + p.Position.z + " " + Xeno(p) + " body " + (p.story.bodyType != null ? p.story.bodyType.defName : "-")
                            + ": [" + string.Join(" ", GeneDefNames(look)) + "]" + (look != null ? " head tint " + look.geneHeadTint + ", hair " + Name(look.hair)
                            + ", shown " + look.hairShown + ", hat " + Name(look.hat) + ", beard " + Name(look.beard) + ", skin " + look.skin + ", hairColor " + look.hairColor : " no look"));
                    }
                    return true;
            }
            return false;
        }

        private static string Xeno(Pawn p)
        {
            return p.genes != null && p.genes.Xenotype != null ? p.genes.Xenotype.defName : "-";
        }

        private static List<string> GeneDefNames(People.Look look)
        {
            List<string> names = new List<string>();
            if (look == null || look.genes == null)
            {
                return names;
            }
            foreach (GeneParts.Placed g in look.genes)
            {
                ModelDef md;
                names.Add(GeneParts.DefOf.TryGetValue(g.model, out md) ? md.defName : "?");
            }
            return names;
        }

        private static string YieldedNames()
        {
            List<string> y = DefDatabase<GeneDef>.AllDefsListForReading.Where(g => Api.GenePartsYielded(g)).Select(g => g.defName).ToList();
            return y.Count == DefDatabase<GeneDef>.DefCount ? "all" : (y.Count == 0 ? "none" : string.Join(" ", y));
        }
    }
}
