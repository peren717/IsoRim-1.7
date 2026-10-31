using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness keys for Api 44 and the Flourish muzzle adapter (FlourishMuzzle; api44muzzle, 2026-10-31):
    //   flourishcheck=1   logs the adapter's state and counters (TryGet answers, motes stood), every armed colonist's
    //                     Api.TryGetMuzzle point (true space and on screen), every turret's (and IsoRim's own TurretMuzzle for
    //                     comparison), and every Flourish mote on the map: where it is drawn from and whether it is stood
    public static partial class Probe
    {
        private static bool FlourishKey(string key, string[] a, Map map)
        {
            if (key != "flourishcheck")
            {
                return false;
            }
            Log.Message("[IsoRim probe] flourishcheck adapter " + FlourishMuzzle.On + " answers " + FlourishMuzzle.Answers + " stood " + FlourishMuzzle.Stood
                + " motesStood " + Emitters.MotesStood + " flashes " + Projectiles.Flashes + " view " + View3D.Ready + " yaw " + View3D.Yaw.ToString("0"));
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.equipment == null || p.equipment.Primary == null)
                {
                    continue;
                }
                Vector3 m;
                float aim;
                bool ok = Api.TryGetMuzzle(p.equipment.Primary, p, out m, out aim);
                Log.Message("[IsoRim probe] flourishcheck muzzle " + p.LabelShort + " " + p.equipment.Primary.def.defName + " at " + p.Position + ": " + ok
                    + " " + V(m) + " aim " + aim.ToString("0") + " px " + Px(m, p.DrawPos.y));
            }
            foreach (Building_Turret t in map.listerThings.AllThings.OfType<Building_Turret>())
            {
                Vector3 m, own;
                bool ok = Api.TryGetMuzzle(t, null, out m);
                bool has = Projectiles.TurretMuzzle(t, Vector3.zero, out own);
                Log.Message("[IsoRim probe] flourishcheck turret " + t.def.defName + " at " + t.Position + ": " + ok + " " + V(m)
                    + " own " + (has ? V(own) : "-") + " px " + Px(m, t.DrawPos.y));
            }
            foreach (Thing t in map.dynamicDrawManager.DrawThings.ToList())
            {
                Mote mote = t as Mote;
                if (mote == null || !mote.def.defName.StartsWith("Flourish_"))
                {
                    continue;
                }
                float h;
                bool flat;
                bool stood = Emitters.MoteStandOf(mote, out h, out flat);
                Vector3 at = mote.DrawPos;
                Log.Message("[IsoRim probe] flourishcheck mote " + mote.def.defName + " at " + V(at) + " stood " + stood + (stood ? " h " + h.ToString("0.000") + (flat ? " flat" : " up") : "")
                    + " px " + Px(new Vector3(at.x, stood ? h : 0f, at.z), at.y));
            }
            return true;
        }

        private static string V(Vector3 v)
        {
            return v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000");
        }

        // A true-space point (y over the ground) on screen, pixels from the top left.
        private static string Px(Vector3 m, float groundY)
        {
            Vector3 w = new Vector3(m.x, groundY + m.y / View3D.Squash, m.z);
            Floors.Drawn(ref w);
            Vector3 sp = Find.Camera.WorldToScreenPoint(w);
            return sp.x.ToString("0.0") + "," + (Screen.height - sp.y).ToString("0.0");
        }
    }
}
