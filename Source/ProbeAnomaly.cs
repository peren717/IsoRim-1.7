using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only keys for the Anomaly set (EffectLift.cs).
    //   voidchoose=disrupt|embrace   the choice of the open void node dialog (it opens when a colonist has touched the node:
    //                                interact=x,z then tick=): its own option's action, then the dialog closed
    //   voidcheck=x,z                logs EffectLift.Report of the thing in the cell and whether the dialog is open
    //   hurtthing=x,z,<amount>       real damage (Blunt) to the building in the cell
    public static partial class Probe
    {
        private static bool AnomalyKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "voidchoose":
                {
                    Dialog_NodeTree dialog = Find.WindowStack.WindowOfType<Dialog_NodeTree>();
                    if (dialog == null)
                    {
                        Log.Warning("[IsoRim probe] voidchoose: no dialog open");
                        return true;
                    }
                    DiaNode node = (DiaNode)AccessTools.Field(typeof(Dialog_NodeTree), "curNode").GetValue(dialog);
                    DiaOption option = node.options[a[0] == "disrupt" ? 0 : 1];
                    System.Action act = (System.Action)AccessTools.Field(typeof(DiaOption), "action").GetValue(option);
                    dialog.Close(false);
                    act();
                    Log.Message("[IsoRim probe] voidchoose: " + a[0]);
                    return true;
                }
                case "voidcheck":
                {
                    IntVec3 c = new IntVec3(int.Parse(a[0]), 0, int.Parse(a[1]));
                    Building b = c.GetEdifice(map);
                    Log.Message("[IsoRim probe] voidcheck: " + (b != null ? b.def.defName + " " + EffectLift.Report(b) : "nothing") + " dialog " + (Find.WindowStack.WindowOfType<Dialog_NodeTree>() != null));
                    return true;
                }
                case "hurtthing":
                {
                    IntVec3 c = new IntVec3(int.Parse(a[0]), 0, int.Parse(a[1]));
                    Building b = c.GetEdifice(map);
                    if (b != null)
                    {
                        b.TakeDamage(new DamageInfo(DamageDefOf.Blunt, float.Parse(a[2])));
                        Log.Message("[IsoRim probe] hurtthing: " + b.def.defName + " hp " + b.HitPoints + " destroyed " + b.Destroyed);
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
