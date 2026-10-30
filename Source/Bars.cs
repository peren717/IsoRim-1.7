using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The game's work progress bars (MoteProgressBar: every ToilEffects.WithProgressBar bar of crafting, cooking,
    // building, mining, sowing ..., and the bars of pods and extractors) in the 3D view. The game lays a bar out from
    // above: over the thing worked on (between the worker and the thing for some jobs), offsetZ cells to its south
    // (half a cell: "under" it while north is up), as two flat meshes, the yellow fill beside the grey back. Stood up
    // mesh by mesh where they lie, the bar hung half a cell south of its thing whichever way the camera turned, with the
    // fill drifting off the back when turned. In 3D it is drawn as one card hinged on the bar's own place over the thing
    // (no southward offset), raised over the top of what it belongs to, as IsoRim raises the status icons
    // (Patches.ThingDraw_Prefix opens the card; DrawAt_Prefix drops the offset while the game draws it).
    //
    // The game shows these bars only at its closest zoom (CameraDriver.CurrentZoom == Closest: a root size under its
    // closest limit + 1, 12). In 3D they show out to ZoomOut instead.
    internal static class Bars
    {
        // How far out (camera root size) the bars still show in the 3D view: the game's 12 is about the closest it can
        // zoom, so the bars vanished as soon as one zoomed out a little.
        public const float ZoomOut = 24f;

        // How far over the top of the thing worked on the bar's middle hangs (cells): just clear of it.
        public const float Gap = 0.25f;

        private static readonly Action<MoteDualAttached> updatePosition =
            AccessTools.MethodDelegate<Action<MoteDualAttached>>(AccessTools.Method(typeof(MoteDualAttached), "UpdatePositionAndRotation"));

        private static readonly AccessTools.FieldRef<MoteDualAttached, MoteAttachLink> link2 =
            AccessTools.FieldRefAccess<MoteDualAttached, MoteAttachLink>("link2");

        public static bool Off;    // probe (barfix=0): the game's own drawing, as before this fix
        public static bool ShowAll;   // probe (barshow=1): with Off, the bars shown at every zoom (the harness lets the camera closer than a player can)
        public static int Drawn;   // probe: bars drawn as cards
        public static Vector3 LastPivot;   // probe: the last bar's hinge (map space)
        public static float LastHeight;    // probe: and its height over the ground (cells)

        // Where the bar hangs (its place as the game works it out, without its offset) and how high: over the top of
        // the thing (or cell) it belongs to, the higher of its two ends when it hangs between the worker and the thing.
        public static void Anchor(MoteProgressBar bar, out Vector3 at, out float height)
        {
            updatePosition(bar);   // the game's own placing (DrawAt does it again)
            at = bar.exactPosition;
            float top = TopOf(bar.link1, bar.Map);
            MoteAttachLink l2 = link2(bar);
            if (l2.Linked)
            {
                top = Mathf.Max(top, TopOf(l2, bar.Map));
            }
            if (!bar.link1.Linked && !l2.Linked)
            {
                top = CellTop(bar.Map, at.ToIntVec3());
            }
            height = top + Gap;
            Drawn++;
            LastPivot = at;
            LastHeight = height;
        }

        private static float TopOf(MoteAttachLink link, Map map)
        {
            if (!link.Linked)
            {
                return 0f;
            }
            TargetInfo t = link.Target;
            if (t.HasThing)
            {
                return t.ThingDestroyed ? 0f : Interaction.PickHeight(t.Thing);
            }
            return CellTop(map, t.Cell);
        }

        // A cell worked on (a rock face, a field): the block standing there or the building's top, as for the game's
        // designation icons.
        private static float CellTop(Map map, IntVec3 c)
        {
            if (map == null || !c.IsValid || !c.InBounds(map))
            {
                return 0f;
            }
            float h = WallBoxes.BlockHeightAt(map, c);
            Building b = c.GetEdifice(map);
            if (b != null)
            {
                h = Mathf.Max(h, Interaction.PickHeight(b));
            }
            return h;
        }

        public struct State
        {
            public bool changed;
            public float offsetZ;
            public bool alwaysShow;
        }

        // MoteProgressBar.DrawAt prefix: in 3D, no southward offset (the card is hinged on the bar's place over the
        // thing), and shown out to ZoomOut. Both put back after it is drawn (DrawAt_Finalizer).
        public static void DrawAt_Prefix(MoteProgressBar __instance, out State __state)
        {
            __state = default(State);
            if (!View3D.Ready || __instance == null)
            {
                return;
            }
            if (Off)
            {
                if (ShowAll)
                {
                    __state.changed = true;
                    __state.offsetZ = __instance.offsetZ;
                    __state.alwaysShow = __instance.alwaysShow;
                    __instance.alwaysShow = true;
                }
                return;
            }
            __state.changed = true;
            __state.offsetZ = __instance.offsetZ;
            __state.alwaysShow = __instance.alwaysShow;
            __instance.offsetZ = 0f;
            if (Find.CameraDriver != null && Find.CameraDriver.RootSize < ZoomOut)
            {
                __instance.alwaysShow = true;   // the game's own test (only at its closest zoom) passes
            }
        }

        public static void DrawAt_Finalizer(MoteProgressBar __instance, State __state)
        {
            if (__state.changed && __instance != null)
            {
                __instance.offsetZ = __state.offsetZ;
                __instance.alwaysShow = __state.alwaysShow;
            }
        }

        // ---- (Api 36) ModelDef.barAt: a thing's own fillable bars on its model

        // The thing being drawn whose model sets barAt, and its model (Patches.BeginLift sets them for the thing's drawing,
        // Scope_Finalizer puts them back); null otherwise.
        public static Thing BarOwner;
        public static ModelEntry BarEntry;

        public static int BarAtDraws;       // probe: fillable bars moved to a model's barAt
        public static Vector3 LastBarAt;    // probe: the last one's centre (map x, z; y = height over the ground, cells)
        public static string LastBarOwner;  // probe: and whose it was

        public struct BarAtState
        {
            public bool changed;
            public Matrix4x4 wasXform;
            public bool fillSet;
            public Material wasFill;
            public Material wasBack;
        }

        // ---- the fill over its back from every camera angle (playtest 2026-10-30)
        // GenDraw.DrawFillableBar draws the grey back and then the fill 0.01 above it (a hair in the 3D view, where heights are
        // squashed) with the game's SolidColor materials, both in the same transparent queue (2900). Unity sorts that queue by
        // distance from the camera, and the fill's middle is off to one side of the back's: with the camera turned so the fill
        // lies on the far side, it was drawn first and the back painted over it (the yellow gone at some yaws). While a bar is
        // drawn in 3D, its fill goes through DrawHook with a copy of its material one queue after the back's (FillOver), so it
        // is always drawn over it.
        public static Material FillMat;   // the fill and back of the fillable bar being drawn now (3D only), else null
        public static Material BackMat;
        private static readonly System.Collections.Generic.Dictionary<long, Material> over = new System.Collections.Generic.Dictionary<long, Material>();

        // The material to draw mat with: mat itself, or, for the fill of the bar being drawn, its copy drawn after the back.
        public static Material FillOver(Material mat)
        {
            if (mat == null || mat != FillMat || BackMat == null || mat == BackMat || mat.renderQueue > BackMat.renderQueue)
            {
                return mat;
            }
            int queue = BackMat.renderQueue + 1;
            long key = ((long)mat.GetInstanceID() << 16) ^ queue;
            Material copy;
            if (!over.TryGetValue(key, out copy) || copy == null)
            {
                copy = new Material(mat) { name = mat.name + " (over its back)", renderQueue = queue };
                over[key] = copy;
            }
            return copy;
        }

        // GenDraw.DrawFillableBar prefix: while a thing whose model sets barAt is drawn (raised onto its model by the thing's
        // draw scope), every fillable bar it draws (its own, or a comp's gauge such as VEF's CustomFillableBarGaugeData) is
        // centred on that point of the model and laid flat at its height. With barScale it is resized in its own frame
        // (length, thickness), the margin by the smaller factor, so the filled part keeps its proportion. Rotation stays the
        // game's.
        public static void DrawFillableBar_Prefix(ref GenDraw.FillableBarRequest __0, out BarAtState __state)
        {
            __state = default(BarAtState);
            if (View3D.Ready)
            {
                __state.fillSet = true;
                __state.wasFill = FillMat;
                __state.wasBack = BackMat;
                FillMat = __0.filledMat;
                BackMat = __0.unfilledMat;
            }
            Thing t = BarOwner;
            ModelEntry entry = BarEntry;
            if (t == null || entry == null || !View3D.Ready || !DrawHook.active || DrawHook.billboard)
            {
                return;
            }
            try
            {
                Vector2 scale = entry.def.barScale;
                if (scale != Vector2.one)
                {
                    __0.size = new Vector2(__0.size.x * scale.x, __0.size.y * scale.y);
                    __0.margin *= Mathf.Min(scale.x, scale.y);
                    __0.preRotationOffset = new Vector2(__0.preRotationOffset.x * scale.x, __0.preRotationOffset.y * scale.y);
                }
                if (!entry.def.barAt.HasValue)
                {
                    return;
                }
                Model m = entry.Pick(t);
                if (m == null)
                {
                    return;
                }
                Vector3 fit;
                float yaw;
                Vector3 p = ModelBaker.PointOnMap(t, entry, m, entry.def.barAt.Value, out fit, out yaw);
                __0.center = new Vector3(p.x, __0.center.y, p.z);
                __0.preRotationOffset = Vector2.zero;   // centred on barAt
                __state.changed = true;
                __state.wasXform = DrawHook.xform;
                DrawHook.xform = View3D.LiftMatrix(p.y);
                BarAtDraws++;
                LastBarAt = p;
                LastBarOwner = t.ThingID;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Placing a bar on its model failed: " + e, 93611236);
            }
        }

        public static void DrawFillableBar_Finalizer(BarAtState __state)
        {
            if (__state.changed)
            {
                DrawHook.xform = __state.wasXform;
            }
            if (__state.fillSet)
            {
                FillMat = __state.wasFill;
                BackMat = __state.wasBack;
            }
        }
    }
}
