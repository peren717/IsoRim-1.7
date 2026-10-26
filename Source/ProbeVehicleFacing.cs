using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only test for drafted facing with a mod that turns its pawns itself (2026-10-26): Vehicle Framework's
    // vehicles, ordered by VF's own "Go here" and orientation drag, real ticks. Reflection only (IsoRim does not reference VF).
    //   vehicle=x,z,<VehicleDef>,<rot 0-3>   a player vehicle spawned there, fuelled, one generated colonist boarded
    //                                        (VehiclePawn.TryAddPawn) and the ignition on (as its draft gizmo)
    //   vfaceorder=x,z,tx,tz,N|E|S|W|-       the vehicle in (x,z) gets VF's "Go here" to (tx,tz) by the right-click menu, then
    //                                        the orientation drag ends facing that way (VehicleOrientationController's Rotation
    //                                        as its drag sets it, then its ConfirmOrientation as on the mouse release; '-' a plain
    //                                        click: no drag, it parks the way it drove). Checked by the next face= like faceorder.
    public static partial class Probe
    {
        private static bool VehicleKey(string key, string[] a, Map map)
        {
            switch (key)
            {
                case "vehicle":
                {
                    Type spawner = AccessTools.TypeByName("Vehicles.VehicleSpawner");
                    Type vdefType = AccessTools.TypeByName("Vehicles.VehicleDef");
                    ThingDef vdef = DefDatabase<ThingDef>.AllDefs.FirstOrDefault(d => d.defName == a[2] && vdefType != null && vdefType.IsInstanceOfType(d));
                    if (spawner == null || vdef == null)
                    {
                        Log.Warning("[IsoRim probe] vehicle: no Vehicle Framework or no vehicle " + a[2]);
                        return true;
                    }
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    Rand.PushState(Gen.HashCombineInt(c.x, c.z * 7919 + 31));   // fixed seed per cell
                    Pawn v = (Pawn)AccessTools.Method(spawner, "GenerateVehicle", new[] { vdefType, typeof(Faction) }).Invoke(null, new object[] { vdef, Faction.OfPlayer });
                    GenSpawn.Spawn(v, c, map, new Rot4(I(a[3])));
                    foreach (ThingComp comp in ((ThingWithComps)v).AllComps)
                    {
                        if (comp.GetType().Name == "CompFueledTravel")
                        {
                            Traverse.Create(comp).Field("fuel").SetValue(Traverse.Create(comp).Property("FuelCapacity").GetValue<float>());
                        }
                    }
                    Pawn driver = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer, mustBeCapableOfViolence: true));
                    Rand.PopState();
                    bool boarded = (bool)AccessTools.Method(v.GetType(), "TryAddPawn", new[] { typeof(Pawn) }).Invoke(v, new object[] { driver });
                    object ignition = Traverse.Create(v).Field("ignition").GetValue();
                    Traverse.Create(ignition).Property("Drafted").SetValue(true);
                    Log.Message("[IsoRim probe] vehicle " + a[2] + " at " + c + " facing " + v.Rotation.ToStringHuman() + ": driver boarded " + boarded
                        + ", ignition " + Traverse.Create(ignition).Property("Drafted").GetValue<bool>() + ", Pawn.Drafted " + v.Drafted);
                    return true;
                }
                case "vfaceorder":
                    pendingFace.Add(new FaceOrder
                    {
                        from = new IntVec3(I(a[0]), 0, I(a[1])), to = new IntVec3(I(a[2]), 0, I(a[3])), vehicle = true, vrot = a[4]
                    });
                    return true;
            }
            return false;
        }

        private static bool IsVehicle(Pawn p)
        {
            return p != null && p.GetType().FullName == "Vehicles.VehiclePawn";
        }

        private static bool VehicleMoving(Pawn p)
        {
            object pather = Traverse.Create(p).Field("vehiclePather").GetValue();
            return pather != null && Traverse.Create(pather).Property("Moving").GetValue<bool>();
        }

        private static Rot4 VehicleWant(FaceOrder o)
        {
            switch (o.vrot)
            {
                case "N": return Rot4.North;
                case "E": return Rot4.East;
                case "S": return Rot4.South;
                case "W": return Rot4.West;
            }
            IntVec3 d = o.to - o.from;   // a plain click: the way it drove (the tests drive straight)
            return d.x > 0 ? Rot4.East : d.x < 0 ? Rot4.West : d.z > 0 ? Rot4.North : Rot4.South;
        }

        // VF's own order: the right-click menu's "Go here" (FloatMenuOptionProvider_OrderVehicle) starts its orientation
        // targeter; the drag sets its Rotation, the mouse release confirms (PawnGotoAction: Goto job + end rotation).
        private static void VehicleOrder(Selector sel, FaceOrder o)
        {
            Pawn v = o.pawn;
            sel.ClearSelection();
            sel.Select(v, false, false);
            fakeMouse = ScreenOf(new Vector3(o.to.x + 0.5f, 0f, o.to.z + 0.5f));
            FloatMenuContext ctx;
            System.Collections.Generic.List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(sel.SelectedPawns, Verse.UI.MouseMapPosition(), out ctx);
            string goHere = "GoHere".Translate();
            FloatMenuOption go = opts?.FirstOrDefault(x => x.Label == goHere && x.action != null);
            if (go == null)
            {
                o.done = true;
                o.note = "no Go here option (cell " + Verse.UI.MouseCell() + "; options " + (opts == null ? "none" : string.Join(" | ", opts.Select(x => x.Label))) + ")";
                return;
            }
            go.Chosen(true, null);
            Type ctl = AccessTools.TypeByName("Vehicles.VehicleOrientationController");
            Type rot8 = AccessTools.TypeByName("SmashTools.Rot8");
            object inst = AccessTools.Property(ctl, "Instance").GetValue(null);
            object rot = o.vrot == "-" ? AccessTools.Field(rot8, "Invalid")?.GetValue(null) ?? AccessTools.Property(rot8, "Invalid").GetValue(null)
                : Activator.CreateInstance(rot8, new object[] { VehicleWant(o), 0f });
            AccessTools.Property(ctl, "Rotation").SetValue(inst, rot);
            AccessTools.Method(ctl, "ConfirmOrientation").Invoke(inst, null);
            o.note = "ordered at cell " + Verse.UI.MouseCell() + " end rotation " + o.vrot + ", job " + v.CurJobDef?.defName;
        }
    }
}
