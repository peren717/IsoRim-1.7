using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for the placement ghost (ghost3, 2026-10-23):
    //   designrot=0..3     the build designator in hand turned to that rotation (as the player's Q / E keys: Designator_Place.placingRot)
    //   designcell=x,z     the build designator in hand designates that cell (the player's click: the blueprint)
    //   undraft=x,z        the colonist in the cell undrafted (takes work again)
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
                case "mousecheck":
                    // mousecheck=1: logs the cell under the pointer (UI.MouseCell: where a build designator draws its ghost and
                    // places) and whether the ghost there is Occupied
                    {
                        IntVec3 mc = Verse.UI.MouseCell();
                        Designator_Place dp = Find.DesignatorManager.SelectedDesignator as Designator_Place;
                        ThingDef pd = dp != null ? dp.PlacingDef as ThingDef : null;
                        Log.Message("[IsoRim probe] mousecheck: mouse cell " + mc + ", placing " + (pd?.defName ?? "-")
                            + (pd != null ? ", occupied " + Ghosts.Occupied(pd, mc, placingRot(dp), map, null) + ", shows3D " + Ghosts.Shows3D(pd) : ""));
                    }
                    return true;
                case "plancheck":
                    // plancheck=1: logs each blueprint and frame: what it becomes, its 3D form, where its model ghost stands
                    foreach (Thing t in map.listerThings.AllThings.Where(x => x is Blueprint || x is Frame).ToList())
                    {
                        ThingDef target = Ghosts.Target(t);
                        ThingForm form = target != null ? Forms.For(target) : default(ThingForm);
                        string where = "";
                        if (target != null && form.form == Form3D.Model && form.model != null && form.model.HasModel)
                        {
                            Vector3 fit;
                            float yaw;
                            Vector3 p = ModelBaker.PointOnMap(t, form.model, form.model.variants[0], Vector3.zero, out fit, out yaw);
                            where = ", model origin " + p.ToString("F2") + " yaw " + yaw.ToString("F0");
                        }
                        Building occ = t.Position.GetEdifice(map);
                        Log.Message("[IsoRim probe] plancheck " + t.def.defName + " " + t.Position + " rot " + t.Rotation + ": target " + target?.defName
                            + ", form " + form.form + where + ", edifice " + (occ != null ? occ.def.defName : "none") + ", drawer " + t.def.drawerType);
                    }
                    return true;
                case "designcell":
                {
                    // designcell=x,z: the build designator in hand designates that cell, as the player's click (CanDesignateCell
                    // then DesignateSingleCell: the blueprint, replacing what stands there as the game does)
                    Designator dz = Find.DesignatorManager.SelectedDesignator;
                    IntVec3 dc = new IntVec3(I(a[0]), 0, I(a[1]));
                    AcceptanceReport ok = dz != null ? dz.CanDesignateCell(dc) : "no designator";
                    if (ok.Accepted)
                    {
                        dz.DesignateSingleCell(dc);
                    }
                    Log.Message("[IsoRim probe] designcell " + dc + ": " + (ok.Accepted ? "placed" : "refused: " + ok.Reason));
                    return true;
                }
                case "undraft":
                {
                    // undraft=x,z: the colonist in the cell undrafted, so they take work (here: building the blueprints)
                    Pawn up = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    if (up?.drafter != null)
                    {
                        up.drafter.Drafted = false;
                    }
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
