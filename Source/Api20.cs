using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 20 (the API sprint's first batch, design api-sprint-design.md): the members below plus ModelDef.cutaway
    // (CutawayRule, WallBoxes.CanBeCut / LowWall), the flat scopes (UprightScopes, FlatScope), the arc rules
    // (Projectiles.Lobbed) and x-ray placing (MineXray.RegisterPlacing).
    public static partial class Api
    {
        // ---- the view (Version 20)

        /// <summary>The 3D view draws with a perspective camera now (its perspective option, or first person); false for the
        /// flat (orthographic) camera and while the view is off.</summary>
        public static bool ViewPerspective => View3D.Ready && View3D.Perspective;

        /// <summary>The perspective camera's vertical field of view in degrees (first person: its own option); 0 while the
        /// camera is flat or the view is off.</summary>
        public static float FieldOfView => ViewPerspective ? View3D.FieldOfView : 0f;

        /// <summary>The pawn whose eyes the view looks through (IsoRim's first person), or null.</summary>
        public static Pawn FirstPersonPawn => View3D.Ready && FirstPerson.On ? FirstPerson.Pawn : null;

        /// <summary>
        /// A counter that goes up whenever how the 3D view looks at the map changes: the view turned on or off, perspective
        /// or flat camera, field of view, <see cref="ViewYaw"/> or <see cref="ViewPitch"/> (every frame of a turn), first person
        /// on, off or to another pawn. Moving or zooming the camera does not count. Compare it with the value you last saw (a
        /// post-processing mod resets its history then). Checked when read, at most once a frame.
        /// </summary>
        public static int ViewVersion => ViewSignal.Version;

        // ---- flat drawing scopes (Version 20; the same scope stack as the upright scope, Version 12)

        /// <summary>
        /// Opens a flat skip scope: until <see cref="EndSkipFlat"/>, the flat 2D meshes IsoRim draws for the game (those an
        /// upright scope stands up: a <c>Graphic</c>'s draw, <c>GenDraw.DrawMeshNowOrLater</c>, fillable bars, overlays, and
        /// <see cref="DrawMesh"/>) are not drawn. For leaving out what a mod draws over a body a 3D model stands in for (check
        /// <see cref="ModelStandsIn"/> first). Without the 3D view nothing is skipped. Scopes nest with the upright and flat
        /// transform scopes; one left open is closed after the thing whose drawing opened it, or at the next frame.
        /// </summary>
        public static void BeginSkipFlat() => UprightScopes.BeginSkip();

        /// <summary>Closes the innermost drawing scope (skip, transform or upright). With none open it does nothing.</summary>
        public static void EndSkipFlat() => UprightScopes.End();

        /// <summary><see cref="BeginSkipFlat"/> for a <c>using</c> block; disposing it closes the scope (and any left open inside it).</summary>
        public static FlatScope SkipFlat() => UprightScopes.BeginSkip();

        /// <summary>
        /// Opens a flat transform scope: until <see cref="EndFlatTransform"/>, the same flat 2D meshes are drawn with matrix
        /// (drawn space, see <see cref="DrawnMatrix"/>) applied on top of their own, in place of an outer upright scope or
        /// IsoRim's own lift for the thing being drawn (still moved onto the thing's stacked floor). For drawing a thing's sprite
        /// somewhere else: a corpse laid on a shelf (<c>corpse.DrawNowAt</c> inside the scope). Without the 3D view nothing
        /// changes. Scopes nest as above.
        /// </summary>
        public static void BeginFlatTransform(Matrix4x4 matrix) => UprightScopes.BeginTransform(matrix);

        /// <summary>Closes the innermost drawing scope (skip, transform or upright). With none open it does nothing.</summary>
        public static void EndFlatTransform() => UprightScopes.End();

        /// <summary><see cref="BeginFlatTransform"/> for a <c>using</c> block.</summary>
        public static FlatScope FlatTransform(Matrix4x4 matrix) => UprightScopes.BeginTransform(matrix);

        /// <summary>
        /// Leaves out a method of another mod (or the game) while a 3D model stands in for the thing it draws: IsoRim patches
        /// method so that it is skipped while <see cref="ModelStandsIn"/> is true for its thing: the instance it is called on
        /// (a Thing, or a ThingComp's parent), else its first Thing argument. Meant for drawing methods (a comp's PostDraw, a
        /// building's DrawAt, an overlay drawer); a skipped method returns its type's default value. Patching the same method
        /// twice does nothing. False (and a warning in the log) if it could not be patched. Call once, at startup.
        /// </summary>
        public static bool SkipWhenModelled(MethodBase method) => ModelledSkips.Add(method);

        // ---- placing on stacked floors (Version 20)

        /// <summary>
        /// Where IsoRim draws a matrix given in the game's world space (drawn space: x and z map cells, y the height in cells
        /// divided by <see cref="HeightSquash"/>), whose z may be a row of an upper floor (stacked floors, <see cref="Floors"/>):
        /// moved onto that floor (down by its rows, up by its height), plus the layout's offset of the thing being drawn now
        /// (a pawn on a stair step) when called while IsoRim draws a thing. Unchanged without floors, on the ground, and on a
        /// hidden floor (check <see cref="FloorShown"/>). Use it for meshes drawn with UnityEngine.Graphics yourself (those
        /// drawn through <see cref="DrawMesh"/>, <see cref="DrawModel(ModelHandle, Vector3, float, Vector3, Color, string)"/>
        /// and the game's own drawing are placed already).
        /// </summary>
        public static Matrix4x4 DrawnMatrix(Matrix4x4 matrix) => IsoRim.Floors.DrawnMatrix(matrix);

        /// <summary>The same for a point in true space (x and z map cells, y the height over its floor in cells): where it is
        /// drawn, in drawn space (y divided by <see cref="HeightSquash"/>, its floor's height added).</summary>
        public static Vector3 DrawnPosition(Vector3 position)
        {
            Vector3 p = new Vector3(position.x, position.y / View3D.Squash, position.z);
            if (DrawHook.floor)
            {
                p = DrawHook.floorMatrix.MultiplyPoint3x4(p);
            }
            IsoRim.Floors.Drawn(ref p);
            return p;
        }

        /// <summary>Whether things at this point (true space; its row decides its floor) are drawn now: always without floors;
        /// false on a floor the viewer has hidden (faded floors count as drawn).</summary>
        public static bool FloorShown(Vector3 position)
        {
            Vector3 p = position;
            return IsoRim.Floors.Drawn(ref p);
        }

        // ---- build ghosts (Version 20)

        /// <summary>
        /// Draws, this frame, the see-through 3D ghost IsoRim draws under the pointer while a thing is placed: the thing's model
        /// (in stuff's or style's variant) or its block, at cell facing rot, tinted from colour (the game's ghost colour; the
        /// blueprint blue or frame sawdust look the same way). On an upper floor's rows it shows on that floor. False when
        /// nothing was drawn (3D view or IsoRim's 3D ghosts option off, or the def has no 3D shape): draw the game's own ghost
        /// then. Call from draw-time code.
        /// </summary>
        public static bool DrawGhost(ThingDef def, IntVec3 cell, Rot4 rot, Color colour, ThingDef stuff = null, ThingStyleDef style = null)
        {
            return View3D.Ready && Ghosts.DrawPreview(def, cell, rot, colour, stuff, style);
        }

        // ---- projectile arcs (Version 20)

        /// <summary>
        /// Registers a rule for how IsoRim draws a projectile's flight: true on the high arc of indirect fire (as mortar
        /// shells), false straight (a direct shot, with the game's own small arc), null leaves it to IsoRim (the arc for things
        /// that fly overhead). Asked once per flight, when the projectile is first drawn; the first rule with an answer wins.
        /// Only the drawing changes. A projectile without graphicData is never drawn on the arc. Registering the same rule twice
        /// does nothing. With no rule registered this costs nothing. Call once, at startup.
        /// </summary>
        public static void AddProjectileArc(Func<Projectile, bool?> rule) => ArcRules.Add(rule);

        /// <summary>Removes a rule added with <see cref="AddProjectileArc"/>.</summary>
        public static void RemoveProjectileArc(Func<Projectile, bool?> rule) => ArcRules.Remove(rule);

        // ---- x-ray while placing (Version 20)

        /// <summary>
        /// While the player places this def (its build designator in hand), the mining x-ray (IsoRim's mineXray option) shows
        /// the floor below and/or the floor above the viewed one, without the crosshair: an addon's stairs show the floor above,
        /// where the exit lands. Both false removes it. Wins over <see cref="XrayDesignator"/>. Call once, at startup.
        /// </summary>
        public static void XrayPlacing(BuildableDef def, bool below, bool above) => MineXray.RegisterPlacing(def, below, above);

        // ---- model queries (Version 20)

        /// <summary>
        /// Whether a 3D model file is bound to this def (a ModelDef entry with a model, by thingDef, graphicPath, category, name
        /// match or fallback; not flat). Says nothing about the player's options or the 3D view: whether the model stands in for a
        /// thing now is <see cref="ModelStandsIn"/>.
        /// </summary>
        public static bool HasModel(ThingDef def)
        {
            ModelEntry entry;
            return def != null && ModelLibrary.TryGet(def, out entry) && entry.HasModel && !entry.def.flat;
        }
    }

    // Api 20: the projectile arc rules (Projectiles.Lobbed asks them once a flight).
    internal static class ArcRules
    {
        private static readonly List<Func<Projectile, bool?>> rules = new List<Func<Projectile, bool?>>();

        public static bool Any => rules.Count > 0;

        public static void Add(Func<Projectile, bool?> rule)
        {
            if (rule != null && !rules.Contains(rule))
            {
                rules.Add(rule);
            }
        }

        public static void Remove(Func<Projectile, bool?> rule)
        {
            rules.Remove(rule);
        }

        public static bool? Ask(Projectile p)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                try
                {
                    bool? said = rules[i](p);
                    if (said != null)
                    {
                        return said;
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] A projectile arc rule (Api.AddProjectileArc) failed: " + e, 93622001);
                }
            }
            return null;
        }
    }

    // Api 20: methods skipped while a model stands in for their thing (Api.SkipWhenModelled).
    internal static class ModelledSkips
    {
        private static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
        private static Harmony harmony;

        public static bool Add(MethodBase method)
        {
            if (method == null)
            {
                return false;
            }
            if (patched.Contains(method))
            {
                return true;
            }
            try
            {
                if (harmony == null)
                {
                    harmony = new Harmony("isorim.skipmodelled");
                }
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ModelledSkips), nameof(Prefix)) { priority = Priority.First });
                patched.Add(method);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Api.SkipWhenModelled could not patch " + method.DeclaringType?.FullName + "." + method.Name + ": " + e.Message);
                return false;
            }
        }

        // false = skip the original (its thing's model stands in).
        public static bool Prefix(object __instance, object[] __args)
        {
            if (!View3D.Ready)
            {
                return true;
            }
            try
            {
                Thing t = __instance as Thing ?? (__instance as ThingComp)?.parent;
                if (t == null && __args != null)
                {
                    for (int i = 0; i < __args.Length && t == null; i++)
                    {
                        t = __args[i] as Thing;
                    }
                }
                return t == null || !Api.ModelStandsIn(t);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Api.SkipWhenModelled: checking a skipped method failed: " + e, 93622002);
                return true;
            }
        }
    }

    // Api 20: Api.ViewVersion, the view's change counter (worked out when read, at most once a frame).
    internal static class ViewSignal
    {
        private static int version;
        private static int checkedFrame = -1;
        private static bool ready, persp;
        private static float fov, yaw, pitch;
        private static Pawn eyes;

        public static int Version
        {
            get
            {
                if (checkedFrame != Time.frameCount)
                {
                    checkedFrame = Time.frameCount;
                    bool r = View3D.Ready;
                    bool p = r && View3D.Perspective;
                    float f = p ? View3D.FieldOfView : 0f;
                    float y = r ? View3D.Yaw : 0f;
                    float pt = r ? View3D.Pitch : 0f;
                    Pawn e = r && FirstPerson.On ? FirstPerson.Pawn : null;
                    if (r != ready || p != persp || f != fov || y != yaw || pt != pitch || e != eyes)
                    {
                        version++;
                        ready = r;
                        persp = p;
                        fov = f;
                        yaw = y;
                        pitch = pt;
                        eyes = e;
                    }
                }
                return version;
            }
        }
    }
}
