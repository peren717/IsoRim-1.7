using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for the books branch (filth on the ground, books on the ground and read):
    //   filthcheck=x0,z0,x1,z1    logs each filth in the rectangle: def, thickness, graphic class, its material (shader,
    //                             texture, colour), the thing's draw colour and IsoRim's form for the def
    public static partial class Probe
    {
        private static bool BooksKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "filthcheck":
                {
                    CellRect r = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    foreach (Filth f in map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).OfType<Filth>().Where(t => r.Contains(t.Position)).OrderBy(t => t.Position.x * 1000 + t.Position.z).Take(40))
                    {
                        Graphic g = f.Graphic;
                        Material m = g != null ? g.MatSingleFor(f) : null;
                        Log.Message("[IsoRim probe] filthcheck " + f.def.defName + " at " + f.Position + " thickness " + f.thickness
                            + " graphic " + (g != null ? g.GetType().Name + " " + g.path + " colour " + g.color : "-")
                            + " mat " + (m != null ? m.name + " shader " + (m.shader != null ? m.shader.name : "-") + " tex " + (m.mainTexture != null ? m.mainTexture.name : "-") + " colour " + m.color + " queue " + m.renderQueue : "-")
                            + " drawColour " + f.DrawColor + " form " + Forms.For(f.def).form + " drawer " + f.def.drawerType + " altitude " + f.def.altitudeLayer);
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
