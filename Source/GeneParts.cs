using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 35) What a gene part (ModelDef.geneDef) is part of on a 3D person.
    public enum GeneAnchor
    {
        Head,        // the head mesh, widened with the head type (ears, nose, horns, head fur)
        UpperBody,   // the body, scaled by body type like clothes; left out where clothing covers the upper body (fur)
        LowerBody,   // likewise the legs
        LowerBack    // the body, at its own size, moved back with the build's back (tails); shown over clothes
    }

    // (Api 35) The colour a gene part's stuff_* faces are multiplied by.
    public enum GeneTint
    {
        Skin,
        Hair,
        None
    }

    // (Api 35) One entry of ModelDef.geneBuilds: the part's model for one build (Male, Female, Thin, Fat, Hulk).
    public class GeneBuildModel
    {
        public string build;
        public string model;
    }

    // (Api 35) XML: while a listed mod is active, IsoRim draws no 3D parts for the listed genes: the mod draws them itself.
    // With genes left out: all genes, except those in exceptGenes. Matched once at startup by packageId and/or Workshop
    // PublishedFileId (either one active is enough).
    //
    //   <IsoRim.GenePartsYieldDef>
    //     <defName>R3D_Yield_SomeMod</defName>
    //     <packageIds><li>author.somemod</li></packageIds>
    //     <publishedFileIds><li>1234567890</li></publishedFileIds>
    //     <genes><li>Ears_Pig</li></genes>                 (these genes only; or:)
    //     <exceptGenes><li>Furskin</li></exceptGenes>      (with no genes: all genes but these)
    //   </IsoRim.GenePartsYieldDef>
    public class GenePartsYieldDef : Def
    {
        public List<string> packageIds;
        public List<string> publishedFileIds;
        public List<string> genes;
        public List<string> exceptGenes;
    }

    // Gene parts on 3D people (Api 35): ModelDefs with a geneDef, looked up per person when their look is worked out
    // (People.Work, main thread) and merged into the head and body meshes like hair and clothes, so they follow every
    // facing, pose and animation. Setting geneParts3D off, or a gene yielded to another mod (GenePartsYieldDef,
    // Api.YieldGeneParts), leaves them out: the figure exactly as before.
    internal static class GeneParts
    {
        internal class Part
        {
            public ModelDef def;
            public Model model;
            public Dictionary<string, Model> builds;   // by IsoRim build name (male, female, thin, fat, hulk)

            public Model For(string build)
            {
                Model m;
                return builds != null && build != null && builds.TryGetValue(build, out m) ? m : model;
            }
        }

        // One part as worked out for a person (People.Look.genes).
        internal struct Placed
        {
            public Model model;
            public GeneAnchor anchor;
            public GeneTint tint;
            public bool tuck;

            public bool Same(Placed o)
            {
                return model == o.model && anchor == o.anchor && tint == o.tint && tuck == o.tuck;
            }
        }

        // One yield: these genes, or (allBut) every gene but these.
        private sealed class YieldEntry
        {
            public bool allBut;
            public HashSet<GeneDef> genes = new HashSet<GeneDef>();

            public bool Covers(GeneDef g)
            {
                return allBut != genes.Contains(g);
            }
        }

        private static readonly Dictionary<GeneDef, List<Part>> byGene = new Dictionary<GeneDef, List<Part>>();
        private static readonly List<GeneDef> geneOrder = new List<GeneDef>();
        private static readonly object gate = new object();
        private static readonly Dictionary<string, YieldEntry> apiYields = new Dictionary<string, YieldEntry>();   // by owner
        private static List<YieldEntry> xmlYields = new List<YieldEntry>();
        private static volatile YieldEntry[] yielded = new YieldEntry[0];   // xml + api, rebuilt on change (read without the lock)
        private static volatile int changes;
        private static readonly Dictionary<int, float> backOf = new Dictionary<int, float>();
        private const float MaleBack = 0.125f;   // the back of person_male_lower (size-1 figure), where LowerBack parts are built

        // Changes when the setting or the yields change (People works every look out again).
        internal static int Stamp => (IsoRimMod.Settings != null && IsoRimMod.Settings.geneParts3D ? 1 : 0) + changes * 2;

        internal static int Count => byGene.Count;

        // For the harness: the ModelDef a placed gene part model came from.
        internal static readonly Dictionary<Model, ModelDef> DefOf = new Dictionary<Model, ModelDef>();

        // Names of the active yield defs, for the harness.
        internal static readonly List<string> ActiveYieldDefs = new List<string>();

        internal static void LoadAll()
        {
            byGene.Clear();
            geneOrder.Clear();
            DefOf.Clear();
            if (!ModsConfig.BiotechActive)
            {
                return;
            }
            foreach (ModelDef md in DefDatabase<ModelDef>.AllDefsListForReading)
            {
                if (md.geneDef.NullOrEmpty())
                {
                    continue;
                }
                GeneDef gene = DefDatabase<GeneDef>.GetNamedSilentFail(md.geneDef);
                if (gene == null || md.modContentPack == null)
                {
                    continue;   // a gene from a mod that is not loaded: skipped quietly
                }
                string root = md.modContentPack.RootDir;
                Part p = new Part { def = md, model = md.model.NullOrEmpty() ? null : ModelLibrary.LoadFile(Path.Combine(root, md.model)) };
                if (md.geneBuilds != null)
                {
                    foreach (GeneBuildModel b in md.geneBuilds)
                    {
                        if (b == null || b.build.NullOrEmpty() || b.model.NullOrEmpty())
                        {
                            continue;
                        }
                        Model bm = ModelLibrary.LoadFile(Path.Combine(root, b.model));
                        if (bm != null)
                        {
                            if (p.builds == null)
                            {
                                p.builds = new Dictionary<string, Model>();
                            }
                            p.builds[b.build.ToLowerInvariant()] = bm;
                        }
                    }
                }
                if (p.model == null && p.builds == null)
                {
                    continue;
                }
                List<Part> list;
                if (!byGene.TryGetValue(gene, out list))
                {
                    list = new List<Part>();
                    byGene[gene] = list;
                    geneOrder.Add(gene);
                }
                list.Add(p);
                if (p.model != null)
                {
                    DefOf[p.model] = md;
                }
                if (p.builds != null)
                {
                    foreach (Model bm in p.builds.Values)
                    {
                        DefOf[bm] = md;
                    }
                }
            }
            ResolveYieldDefs();
        }

        // The XML yields whose mod is active (startup; the harness calls it again after adding a test def).
        internal static void ResolveYieldDefs()
        {
            List<YieldEntry> x = new List<YieldEntry>();
            ActiveYieldDefs.Clear();
            HashSet<string> fileIds = null;
            foreach (GenePartsYieldDef d in DefDatabase<GenePartsYieldDef>.AllDefsListForReading)
            {
                bool active = false;
                if (d.packageIds != null)
                {
                    foreach (string id in d.packageIds)
                    {
                        if (!id.NullOrEmpty() && ModsConfig.IsActive(id))
                        {
                            active = true;
                        }
                    }
                }
                if (!active && d.publishedFileIds != null && d.publishedFileIds.Count > 0)
                {
                    if (fileIds == null)
                    {
                        fileIds = ActiveFileIds();
                    }
                    foreach (string id in d.publishedFileIds)
                    {
                        if (!id.NullOrEmpty() && fileIds.Contains(id.Trim()))
                        {
                            active = true;
                        }
                    }
                }
                if (!active)
                {
                    continue;
                }
                ActiveYieldDefs.Add(d.defName);
                bool allBut = d.genes == null || d.genes.Count == 0;
                YieldEntry e = new YieldEntry { allBut = allBut };
                foreach (string g in (allBut ? d.exceptGenes : d.genes) ?? new List<string>())
                {
                    GeneDef gd = g.NullOrEmpty() ? null : DefDatabase<GeneDef>.GetNamedSilentFail(g);
                    if (gd != null)
                    {
                        e.genes.Add(gd);
                    }
                }
                x.Add(e);
            }
            lock (gate)
            {
                xmlYields = x;
                Rebuild();
            }
        }

        // Workshop PublishedFileIds of the active mods: the workshop folder's name, or the mod's About/PublishedFileId.txt.
        private static HashSet<string> ActiveFileIds()
        {
            HashSet<string> ids = new HashSet<string>();
            foreach (ModMetaData m in ModsConfig.ActiveModsInLoadOrder)
            {
                try
                {
                    DirectoryInfo dir = m.RootDir;
                    if (dir == null)
                    {
                        continue;
                    }
                    ulong n;
                    if (ulong.TryParse(dir.Name, out n))
                    {
                        ids.Add(dir.Name);
                    }
                    string file = Path.Combine(Path.Combine(dir.FullName, "About"), "PublishedFileId.txt");
                    if (File.Exists(file))
                    {
                        string t = File.ReadAllText(file).Trim();
                        if (t.Length > 0)
                        {
                            ids.Add(t);
                        }
                    }
                }
                catch (Exception)
                {
                    // unreadable: not matched
                }
            }
            return ids;
        }

        // An owner's yield (Api): these genes, or (allBut) all genes but these; replaces the owner's earlier one.
        internal static void Yield(string owner, IEnumerable<GeneDef> genes, bool allBut)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            YieldEntry e = new YieldEntry { allBut = allBut };
            if (genes != null)
            {
                foreach (GeneDef g in genes)
                {
                    if (g != null)
                    {
                        e.genes.Add(g);
                    }
                }
            }
            lock (gate)
            {
                apiYields[owner] = e;
                Rebuild();
            }
        }

        internal static void Unyield(string owner)
        {
            if (owner == null)
            {
                return;
            }
            lock (gate)
            {
                if (apiYields.Remove(owner))
                {
                    Rebuild();
                }
            }
        }

        private static void Rebuild()
        {
            List<YieldEntry> all = new List<YieldEntry>(xmlYields);
            all.AddRange(apiYields.Values);
            yielded = all.ToArray();
            changes++;
        }

        internal static bool Yielded(GeneDef gene)
        {
            YieldEntry[] all = yielded;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Covers(gene))
                {
                    return true;
                }
            }
            return false;
        }

        // The gene parts this person shows (main thread, People.Work), in def order; tintsHead: a part asks for the head in its
        // tint (Skin or Hair; null none), hidesHair: a part replaces the hair.
        internal static List<Placed> Collect(Pawn pawn, string build, out GeneTint? tintsHead, out bool hidesHair)
        {
            tintsHead = null;
            hidesHair = false;
            if (byGene.Count == 0 || pawn.genes == null || IsoRimMod.Settings == null || !IsoRimMod.Settings.geneParts3D)
            {
                return null;
            }
            List<Placed> found = null;
            for (int i = 0; i < geneOrder.Count; i++)
            {
                GeneDef g = geneOrder[i];
                if (Yielded(g))
                {
                    continue;
                }
                Gene gene = pawn.genes.GetGene(g);
                if (gene == null || !gene.Active)
                {
                    continue;
                }
                foreach (Part p in byGene[g])
                {
                    Model m = p.For(build);
                    if (m == null)
                    {
                        continue;
                    }
                    if (found == null)
                    {
                        found = new List<Placed>();
                    }
                    found.Add(new Placed { model = m, anchor = p.def.geneAnchor, tint = p.def.geneTint, tuck = p.def.geneTuck });
                    if (p.def.geneTintsHead && p.def.geneTint != GeneTint.None)
                    {
                        tintsHead = p.def.geneTint;
                    }
                    hidesHair |= p.def.geneHidesHair;
                }
            }
            return found;
        }

        // How far a LowerBack part moves back on this body (the lower body's back, scaled by body type, against the size-1
        // male figure's, where the parts are built).
        internal static float BackShift(Model lower, Vector3 bodyScale)
        {
            if (lower == null || lower.positions == null)
            {
                return 0f;
            }
            float back;
            if (!backOf.TryGetValue(lower.id, out back))
            {
                back = float.NegativeInfinity;
                foreach (Vector3 v in lower.positions)
                {
                    back = Mathf.Max(back, v.z);
                }
                backOf[lower.id] = back;
            }
            return back * bodyScale.z - MaleBack;
        }

        internal static bool Same(List<Placed> a, List<Placed> b)
        {
            int na = a == null ? 0 : a.Count, nb = b == null ? 0 : b.Count;
            if (na != nb)
            {
                return false;
            }
            for (int i = 0; i < na; i++)
            {
                if (!a[i].Same(b[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
