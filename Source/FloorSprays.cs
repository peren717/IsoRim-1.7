using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Sprays, beams and streams between stacked floors (a flame spew from a roof at a raider on the ground): the game
    // works out their way, place and length from map rows, where another floor is a whole band of rows away. Drawn as
    // seen instead (Floors.Placed / FlatDelta), only when the two ends are on two different floors:
    //   - SubEffecter_Sprayer.MakeMote (fire spew, acid/foam spray, burner flecks): the angle toward the target, the
    //     offset toward it and the spot between the two ends (on the nearer end's floor);
    //   - MoteDualAttached (incinerator and graser beams, interaction lines): placed between its two ends as drawn, turned
    //     and stretched along them, at their mean height;
    //   - IncineratorProjectileMotion (incinerator and burner streams): each stream piece moves from its source to its
    //     target as drawn, at the height between.
    // The gameplay side (where it burns) is the addon's (ProjectZ AimFloor). Without floors these change nothing.
    // In the 3D view, a line mote between two ends (MoteDualAttached) is drawn as a strip in 3D from one end to the other
    // (Tilt): between floors any such line, on one floor (and without floors) the lines the game draws overhead (beams,
    // incinerator streams, aim lines), which would otherwise stand up as a card whose slant on screen is not their way on
    // the map. Lines on the ground (psychic and command links, charging cables) already lie the right way and stay flat.
    // An end on a turret with a 3D gun (the turret itself, or a cell it stands on: Vanilla Gravship Expanded's beam guns
    // fire their beam mote from a cell on the turret) is its gun's barrel end (Projectiles.TurretMuzzle), at its height.
    internal static class FloorSprays
    {
        public static int Sprays, Duals, Streams, Tilts, Lines;   // probes: how often each ran (Lines: one-floor lines in 3D)
        public static Matrix4x4 LastTilt;   // probe: the last mote matrix tilted (pre-squash world space)

        internal static void Patch(Harmony h)
        {
            // The strip replaces the mote's matrix where the mesh is queued (every single mesh passes one of these): after
            // FadeFloors reads its row (Priority.First), before the floor mapping (Floors.Single_Prefix, normal priority).
            HarmonyMethod tilt = new HarmonyMethod(typeof(FloorSprays), nameof(Tilt_Prefix)) { priority = Priority.VeryHigh };
            int n = 0;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Graphics)))
            {
                if (Floors.IsSingleEntry(m))
                {
                    h.Patch(m, prefix: tilt);
                    n++;
                }
            }
            if (n == 0)
            {
                Log.Warning("[IsoRim] Beams in 3D: no Graphics.DrawMesh entry point found; beams stay as the game draws them.");
            }
            h.Patch(AccessTools.Method(typeof(SubEffecter_Sprayer), "MakeMote"),
                prefix: new HarmonyMethod(typeof(FloorSprays), nameof(MakeMote_Prefix)),
                transpiler: new HarmonyMethod(typeof(FloorSprays), nameof(MakeMote_Transpiler)),
                finalizer: new HarmonyMethod(typeof(FloorSprays), nameof(MakeMote_Finalizer)));
            h.Patch(AccessTools.Method(typeof(MoteDualAttached), "UpdatePositionAndRotation"),
                postfix: new HarmonyMethod(typeof(FloorSprays), nameof(Dual_Postfix)));
            h.Patch(AccessTools.Method(typeof(Mote), "DrawMote"),
                prefix: new HarmonyMethod(typeof(FloorSprays), nameof(DrawMote_Prefix)));
            h.Patch(AccessTools.Method(typeof(MoteDualAttached), "DrawAt"),
                finalizer: new HarmonyMethod(typeof(FloorSprays), nameof(DualDraw_Finalizer)));
            h.Patch(AccessTools.Method(typeof(IncineratorProjectileMotion), nameof(IncineratorProjectileMotion.Tick)),
                prefix: new HarmonyMethod(typeof(FloorSprays), nameof(MotionTick_Prefix)));
        }

        // ---- SubEffecter_Sprayer.MakeMote

        private static bool cross;
        private static Vector3 lerpA, lerpB;
        private static float lerpF;

        private static Vector3 PosOf(TargetInfo t) => t.HasThing ? t.Thing.DrawPos : t.Cell.ToVector3Shifted();

        public static void MakeMote_Prefix(TargetInfo A, TargetInfo B)
        {
            cross = Floors.On && A.IsValid && B.IsValid && Floors.CrossFloor(PosOf(A), PosOf(B));
            if (cross)
            {
                Sprays++;
            }
        }

        public static void MakeMote_Finalizer()
        {
            cross = false;
        }

        // a - b (two points of the two ends): as drawn between floors
        public static Vector3 Sub(Vector3 a, Vector3 b) => cross ? Floors.FlatDelta(b, a) : a - b;

        // (b - a).AngleFlat of two cells
        public static float CellAngle(IntVec3 b, IntVec3 a)
        {
            if (cross)
            {
                return Floors.FlatDelta(a.ToVector3Shifted(), b.ToVector3Shifted()).AngleFlat();
            }
            return (b - a).AngleFlat;
        }

        // A * f + B * (1 - f), the spot between the two ends
        public static Vector3 LerpA(Vector3 a, float f)
        {
            lerpA = a;
            lerpF = f;
            return a * f;
        }

        public static Vector3 LerpB(Vector3 b, float f)
        {
            lerpB = b;
            return b * f;
        }

        public static Vector3 LerpSum(Vector3 x, Vector3 y) => cross ? Floors.FlatLerp(lerpA, lerpB, lerpF) : x + y;

        public static IEnumerable<CodeInstruction> MakeMote_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            MethodInfo vSub = AccessTools.Method(typeof(Vector3), "op_Subtraction", new[] { typeof(Vector3), typeof(Vector3) });
            MethodInfo vMul = AccessTools.Method(typeof(Vector3), "op_Multiply", new[] { typeof(Vector3), typeof(float) });
            MethodInfo vAdd = AccessTools.Method(typeof(Vector3), "op_Addition", new[] { typeof(Vector3), typeof(Vector3) });
            MethodInfo norm = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.normalized));
            MethodInfo angleV = AccessTools.Method(typeof(Vector3Utility), nameof(Vector3Utility.AngleFlat), new[] { typeof(Vector3) });
            MethodInfo iSub = AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) });
            MethodInfo angleI = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.AngleFlat));
            FieldInfo lerp = AccessTools.Field(typeof(SubEffecterDef), nameof(SubEffecterDef.positionLerpFactor));
            int subs = 0, angles = 0, lerps = 0;
            bool lerpOpen = false;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ci = code[i];
                if (ci.Calls(vSub) && i + 1 < code.Count && (code[i + 1].Calls(angleV)
                    || i + 3 < code.Count && code[i + 1].IsStloc() && code[i + 2].opcode.Name.StartsWith("ldloca") && code[i + 3].Calls(norm)))
                {
                    ci.operand = AccessTools.Method(typeof(FloorSprays), nameof(Sub));
                    subs++;
                }
                else if (ci.Calls(iSub) && i + 3 < code.Count && code[i + 1].IsStloc() && code[i + 2].opcode.Name.StartsWith("ldloca") && code[i + 3].Calls(angleI))
                {
                    ci.operand = AccessTools.Method(typeof(FloorSprays), nameof(CellAngle));
                    for (int j = 1; j <= 3; j++)
                    {
                        code[i + j].opcode = OpCodes.Nop;
                        code[i + j].operand = null;
                    }
                    angles++;
                }
                else if (ci.Calls(vMul) && i >= 1 && code[i - 1].LoadsField(lerp) && lerps == 0)
                {
                    ci.operand = AccessTools.Method(typeof(FloorSprays), nameof(LerpA));
                    lerpOpen = true;
                    lerps++;
                }
                else if (lerpOpen && ci.Calls(vMul) && i >= 2 && code[i - 1].opcode == OpCodes.Sub && code[i - 2].LoadsField(lerp))
                {
                    ci.operand = AccessTools.Method(typeof(FloorSprays), nameof(LerpB));
                    lerps++;
                }
                else if (lerpOpen && lerps == 2 && ci.Calls(vAdd))
                {
                    ci.operand = AccessTools.Method(typeof(FloorSprays), nameof(LerpSum));
                    lerpOpen = false;
                    lerps++;
                }
            }
            if (subs != 2 || angles != 1 || lerps != 3)
            {
                Log.Warning($"[IsoRim] Sprays between floors: SubEffecter_Sprayer.MakeMote changed (swapped {subs}/2 subtractions, {angles}/1 angles, {lerps}/3 lerp steps)");
            }
            return code;
        }

        // ---- MoteDualAttached: between its two ends as drawn

        private sealed class Ends
        {
            public Vector3 a, b;   // placed (drawn) points, y = height over the ground (Floors.Placed)
        }

        private sealed class Raise
        {
            public Vector3 a, b;   // the two ends as placed (pre-squash, y = their floor's height, Floors.Placed)
            public float width;    // the mote's drawn width (cells)
            public float stretch;  // the mote's drawn length over the flat distance between its ends (1 for a beam end to end)
            public bool flip;      // the mesh's length axis runs from b to a
            public float ha = EndHeight, hb = EndHeight;   // each end's height over its floor (a turret's barrel end: its own)
        }

        private static readonly ConditionalWeakTable<MoteDualAttached, Ends> streamEnds = new ConditionalWeakTable<MoteDualAttached, Ends>();
        private static readonly ConditionalWeakTable<Mote, Raise> raised = new ConditionalWeakTable<Mote, Raise>();
        private static readonly AccessTools.FieldRef<MoteDualAttached, MoteAttachLink> link2 = AccessTools.FieldRefAccess<MoteDualAttached, MoteAttachLink>("link2");

        private static Vector3 PlacedFlat(Vector3 p)
        {
            p.y = 0f;
            Floors.Placed(ref p);
            return p;
        }

        // A line the game draws overhead between its two ends (beams, incinerator streams, aim lines): in the 3D view it
        // would stand up as a card over its middle, so it is drawn in 3D on one floor too. Work and status icons that are
        // attached to two things (MetaOverlays) stay icons; lines on the ground lie flat already.
        private static bool OverheadLine(ThingDef def)
        {
            return def.altitudeLayer == AltitudeLayer.MoteOverhead && def.mote != null && def.mote.rotateTowardsTarget;
        }

        public static void Dual_Postfix(MoteDualAttached __instance)
        {
            if (!View3D.Ready)
            {
                raised.Remove(__instance);
                return;
            }
            Vector3 pa, pb;
            bool cross;
            if (Floors.On && streamEnds.TryGetValue(__instance, out Ends e))
            {
                pa = e.a;
                pb = e.b;
                cross = true;
            }
            else
            {
                MoteAttachLink l2 = link2(__instance);
                if (!__instance.link1.Linked || !l2.Linked)
                {
                    raised.Remove(__instance);
                    return;
                }
                cross = Floors.CrossFloor(__instance.link1.LastDrawPos, l2.LastDrawPos);
                if (!cross && !OverheadLine(__instance.def))
                {
                    raised.Remove(__instance);
                    return;
                }
                pa = PlacedFlat(__instance.link1.LastDrawPos);
                pb = PlacedFlat(l2.LastDrawPos);
            }
            float flat = (pb - pa).MagnitudeHorizontal();
            float ha = EndHeight, hb = EndHeight;
            Vector3 ma, mb;
            if (TurretEnd(__instance.link1, __instance.Map, out ma))
            {
                pa = PlacedFlat(new Vector3(ma.x, 0f, ma.z));
                ha = ma.y;
            }
            if (TurretEnd(link2(__instance), __instance.Map, out mb))
            {
                pb = PlacedFlat(new Vector3(mb.x, 0f, mb.z));
                hb = mb.y;
            }
            if (cross)
            {
                // between floors the game's place, turn and length come from rows a band apart: from the ends as drawn
                Duals++;
                __instance.exactPosition = new Vector3((pa.x + pb.x) * 0.5f, __instance.exactPosition.y, (pa.z + pb.z) * 0.5f);
                if (__instance.def.mote.rotateTowardsTarget)
                {
                    __instance.exactRotation = pa.AngleToFlat(pb) + 90f;
                }
                if (__instance.def.mote.scaleToConnectTargets)
                {
                    __instance.linearScale = new Vector3(__instance.def.graphicData.drawSize.y, 1f, Mathf.Max(flat, 0.0001f));
                }
            }
            else
            {
                Lines++;   // on one floor the game's own place, turn and length stand
            }
            Vector3 along = Quaternion.AngleAxis(__instance.exactRotation, Vector3.up) * Vector3.forward;
            Vector3 scale = __instance.ExactScale;
            float width = scale.x * __instance.def.graphicData.drawSize.x;
            float length = scale.z * __instance.def.graphicData.drawSize.y;
            raised.AddOrUpdate(__instance, new Raise
            {
                a = pa,
                b = pb,
                width = width,
                stretch = flat > 0.0001f ? length / flat : 1f,
                flip = Vector3.Dot(along, pb - pa) < 0f,
                ha = ha,
                hb = hb
            });
        }

        // The barrel end (x, z rows; y over its floor) of the turret with a 3D gun an end of a line mote is on: the turret
        // linked, or the one standing on the cell linked. False otherwise.
        private static bool TurretEnd(MoteAttachLink link, Map map, out Vector3 at)
        {
            at = Vector3.zero;
            if (!link.Linked)
            {
                return false;
            }
            TargetInfo t = link.Target;
            Building_Turret turret = t.Thing as Building_Turret;
            if (turret == null && !t.HasThing && map != null && t.Cell.IsValid && t.Cell.InBounds(map))
            {
                turret = t.Cell.GetEdifice(map) as Building_Turret;
            }
            return turret != null && turret.Spawned && Projectiles.TurretMuzzle(turret, Vector3.zero, out at);
        }

        // The mote being drawn in 3D (set in DrawMote_Prefix, used once by its mesh draw: Tilt).
        private static Raise tilting;

        public static void DualDraw_Finalizer()
        {
            tilting = null;
        }

        private const float EndHeight = 0.8f;   // the line's ends over their floors (a shooter's chest, the target's)

        public static void Tilt_Prefix(ref Matrix4x4 matrix)
        {
            Tilt(ref matrix);
        }

        // The game draws such a mote as a card standing over its middle (an overhead mote) or flat: it is drawn instead as
        // a straight strip in 3D along the line between its two ends, each end over its own floor, turned to face the
        // camera, as long and wide as the game draws it (its length along the slope between floors). Called where the
        // mote's mesh is queued (Graphics, Tilt_Prefix): its matrix is replaced.
        internal static void Tilt(ref Matrix4x4 m)
        {
            Raise r = tilting;
            if (r == null)
            {
                return;
            }
            tilting = null;
            if (Strip(r, out Matrix4x4 strip, out _, out _))
            {
                m = strip;
                Tilts++;
                LastTilt = m;
            }
        }

        // The strip's matrix for a raised mote: the game's plane (x across, z along) placed along the line between its two
        // ends, as wide as the game draws it, turned to face the camera (pre-squash drawn space). endA / endB: its two ends
        // (true space; endA the first link's end). False if the ends meet.
        private static bool Strip(Raise r, out Matrix4x4 m, out Vector3 endA, out Vector3 endB)
        {
            m = Matrix4x4.identity;
            float s = View3D.Squash;
            Vector3 a = new Vector3(r.a.x, r.a.y * s + r.ha, r.a.z);   // true space (y = height in cells)
            Vector3 b = new Vector3(r.b.x, r.b.y * s + r.hb, r.b.z);
            endA = a;
            endB = b;
            if (r.flip)
            {
                Vector3 t = a;
                a = b;
                b = t;
            }
            Vector3 seg = (b - a) * r.stretch;
            if (seg.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            Vector3 view = Quaternion.Euler(View3D.Pitch, View3D.Yaw, 0f) * Vector3.forward;
            Vector3 w = Vector3.Cross(seg, view);
            if (w.sqrMagnitude < 0.000001f)
            {
                w = Vector3.Cross(seg, Vector3.up);
            }
            w = w.normalized * r.width;
            if (Vector3.Dot(Vector3.Cross(seg, w), view) > 0f)
            {
                w = -w;   // the strip's face toward the camera
            }
            Vector3 mid = (a + b) * 0.5f;
            m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(w.x, w.y / s, w.z, 0f));
            m.SetColumn(1, new Vector4(0f, 1f, 0f, 0f));
            m.SetColumn(2, new Vector4(seg.x, seg.y / s, seg.z, 0f));
            m.SetColumn(3, new Vector4(mid.x, mid.y / s, mid.z, 1f));
            return true;
        }

        // A raised line mote: drawn by the game, its matrix replaced (Tilt); a beam (Beams.IsBeamMote, Api 33) is drawn as
        // light along the same strip instead, and the game's own draw of it is skipped.
        public static bool DrawMote_Prefix(Mote __instance)
        {
            if (__instance is MoteDualAttached && View3D.Ready && raised.TryGetValue(__instance, out Raise r))
            {
                if (Beams.IsBeamMote(__instance.def))
                {
                    if (!__instance.paused && !Find.UIRoot.HideMotes && Strip(r, out Matrix4x4 m, out Vector3 a, out Vector3 b))
                    {
                        Beams.DrawMote(__instance, m, a, b);
                    }
                    return false;
                }
                tilting = r;
            }
            return true;
        }

        // ---- IncineratorProjectileMotion: a stream piece from its source to its target as drawn

        public static bool MotionTick_Prefix(ref IncineratorProjectileMotion __instance)
        {
            if (__instance.mote == null || !Floors.CrossFloor(__instance.worldSource, __instance.worldTarget))
            {
                return true;
            }
            Streams++;
            __instance.ticks++;
            Vector3 s = PlacedFlat(__instance.worldSource);
            Vector3 t = PlacedFlat(__instance.worldTarget);
            float alpha = __instance.Alpha;
            Vector3 head = Vector3.Lerp(s, t, alpha);
            float len = Mathf.Max((t - s).MagnitudeHorizontal(), 0.01f);
            Vector3 tail = Vector3.Lerp(s, t, alpha - 2f / len);
            __instance.mote.Scale = Mathf.Lerp(__instance.startScale, __instance.endScale, alpha);
            streamEnds.AddOrUpdate(__instance.mote, new Ends { a = head, b = tail });
            if (alpha < 1f)
            {
                __instance.mote.Maintain();
            }
            return false;
        }
    }
}
