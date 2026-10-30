using Verse;

namespace IsoRim
{
    // Harness-only key for models by runtime sprite (Api 39: ModelDef.byGraphic, GraphicModels).
    //   graphiccheck=x,z[,tag]  every building in the cell with a byGraphic entry: its current Graphic.path and class, the
    //                           entry it was last built with (index, model id; -1 none matched), the entry that matches now,
    //                           the yaw it is built at, whether its model stands in, and the counters (blocks rebuilt by the
    //                           sprite check, flat sprites left out)
    public static partial class Probe
    {
        private static bool GraphicKey(string key, string[] a, Map map)
        {
            if (key != "graphiccheck")
            {
                return false;
            }
            IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
            string tag = a.Length > 2 ? a[2] : "";
            bool any = false;
            foreach (Thing t in c.GetThingList(map))
            {
                ModelEntry e;
                if (!ModelLibrary.TryGet(t.def, out e) || e.byGraphic == null)
                {
                    continue;
                }
                any = true;
                Graphic g = t.Graphic;
                GraphicModels.Entry now = GraphicModels.For(t, e);
                int? baked = GraphicModels.BakedIndex(t);
                Log.Message("[IsoRim probe] graphiccheck " + tag + " " + t.def.defName + " " + c + " rot " + t.Rotation + " path " + g?.path
                    + " class " + g?.GetType().Name + " baked " + (baked.HasValue ? baked.Value.ToString() : "never")
                    + (baked.HasValue && baked.Value >= 0 ? " bakedModel " + e.byGraphic[baked.Value].model.id : "")
                    + " now " + (now != null ? System.Array.IndexOf(e.byGraphic, now) + " model " + now.model.id : "-1")
                    + " yaw " + GraphicModels.YawFor(t, e).ToString("0") + " standsIn " + Api.ModelStandsIn(t)
                    + " rebuilds " + GraphicModels.Rebuilds + " skipped " + GraphicModels.Skipped);
            }
            if (!any)
            {
                Log.Message("[IsoRim probe] graphiccheck " + tag + " " + c + ": no thing with byGraphic models");
            }
            return true;
        }
    }
}
