using HarmonyLib;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness keys for the placement ghost (ghost3, 2026-10-23):
    //   designrot=0..3     the build designator in hand turned to that rotation (as the player's Q / E keys: Designator_Place.placingRot)
    //   ghostspritecheck=1 logs how many game ghost sprites IsoRim left out this session because its 3D ghost stands in
    public static partial class Probe
    {
        private static readonly AccessTools.FieldRef<Designator_Place, Rot4> placingRot = AccessTools.FieldRefAccess<Designator_Place, Rot4>("placingRot");

        private static bool Ghost3Key(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "designrot":
                {
                    Designator_Place d = Find.DesignatorManager.SelectedDesignator as Designator_Place;
                    if (d == null)
                    {
                        Log.Warning("[IsoRim probe] designrot: no placing designator in hand");
                        return true;
                    }
                    placingRot(d) = new Rot4(I(a[0]));
                    return true;
                }
                case "ghostspritecheck":
                    Log.Message("[IsoRim probe] ghostspritecheck: game ghost sprites left out " + Ghosts.SpritesLeftOut + ", drawn " + Ghosts.SpritesDrawn);
                    return true;
            }
            return false;
        }
    }
}
