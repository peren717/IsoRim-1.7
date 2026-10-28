using System.Collections.Generic;
using Verse;

namespace IsoRim
{
    // Api 35: gene parts. IsoRim draws 3D parts for Biotech genes on its 3D people from ModelDefs with a geneDef (GeneParts.cs).
    // A mod that draws some genes' parts on IsoRim's figures itself (through figure hooks, Api 8) tells IsoRim to leave those
    // genes out, here in code or with an IsoRim.GenePartsYieldDef in XML. Generic: no mod names in code.
    public static partial class Api
    {
        /// <summary>
        /// (Api version 35) IsoRim draws no 3D parts for these genes: the caller draws them itself; other genes are drawn as
        /// before. owner names the caller (its packageId, say); a later Yield call by the same owner replaces this one. Any
        /// thread; figures are worked out again at once. <see cref="StopYieldingGeneParts"/> undoes it.
        /// </summary>
        public static void YieldGeneParts(string owner, IEnumerable<GeneDef> genes) => GeneParts.Yield(owner, genes, false);

        /// <summary>(Api version 35) IsoRim draws no 3D gene parts at all while this owner's yield stands.</summary>
        public static void YieldAllGeneParts(string owner) => GeneParts.Yield(owner, null, true);

        /// <summary>(Api version 35) IsoRim draws 3D parts only for these genes; every other gene is left to the caller.</summary>
        public static void YieldAllGenePartsExcept(string owner, IEnumerable<GeneDef> keep) => GeneParts.Yield(owner, keep, true);

        /// <summary>(Api version 35) Takes back this owner's yield (YieldGeneParts, YieldAllGeneParts, YieldAllGenePartsExcept).</summary>
        public static void StopYieldingGeneParts(string owner) => GeneParts.Unyield(owner);

        /// <summary>(Api version 35) Whether IsoRim leaves this gene's 3D parts to another mod (code or XML yield).</summary>
        public static bool GenePartsYielded(GeneDef gene) => gene != null && GeneParts.Yielded(gene);
    }
}
