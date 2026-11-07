using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// (Api version 61) A copy of a model's faces, corner by corner in the imported triangle order (three corners a triangle).
    /// Editing it never edits IsoRim's model. From <see cref="Api.ReadStaticModel"/>.
    /// </summary>
    public sealed class StaticModelGeometry
    {
        public Vector3[] Positions { get; internal set; }
        public Vector2[] UVs { get; internal set; }
        /// <summary>Per corner: the index into MaterialNames and TexturePaths.</summary>
        public int[] MaterialIndices { get; internal set; }
        public string[] MaterialNames { get; internal set; }
        public string[] TexturePaths { get; internal set; }
    }

    /// <summary>
    /// (Api version 61) How a row of the current map is drawn right now, from <see cref="Api.ReadStaticModelView"/>. Offset is in
    /// true (unsquashed) units; a faded (peeked) floor counts as visible.
    /// </summary>
    public struct StaticModelView
    {
        public bool Visible { get; internal set; }
        /// <summary>The ProjectZ floor of the row (0 the ground), -1 for rows that are no floor or not known yet.</summary>
        public int Floor { get; internal set; }
        /// <summary>The floor viewed now (0 without floors).</summary>
        public int ViewedFloor { get; internal set; }
        public Vector3 Offset { get; internal set; }
    }

    /// <summary>
    /// (Api version 61) A static building print, after the game's state picked its model and before its faces are written. Handed to
    /// the selectors of <see cref="Api.AddStaticModelSelector"/>.
    /// </summary>
    public struct StaticModelContext
    {
        public Thing Thing { get; internal set; }
        /// <summary>The model the print would use.</summary>
        public ModelHandle Model { get; internal set; }
        internal Vector3 center, lift, scale, offset;
        internal Quaternion turn, lay;
        internal float baseHeight, floor;

        /// <summary>A point of the model in map-row coordinates, true units, before the floor's draw offset. Fixed for a static print:
        /// safe to cache.</summary>
        public Vector3 LocalToMap(Vector3 point)
        {
            Vector3 p = turn * (Vector3.Scale(lay * point + lift, scale) + offset);
            return new Vector3(center.x + p.x, baseHeight + Mathf.Max(floor, p.y), center.z + p.z);
        }

        /// <summary>The same point where it is drawn now, with its floor's offset: hand it to <see cref="Api.WorldToViewport"/>.</summary>
        public Vector3 LocalToWorld(Vector3 point)
        {
            return LocalToMap(point) + Api.ReadStaticModelView(Thing?.Map, Thing == null ? -1 : Thing.Position.z).Offset;
        }
    }

    /// <summary>
    /// (Api version 61) One textured part of a model with a registered surface, for the material factory of
    /// <see cref="Api.RegisterStaticSurface"/>. Original is IsoRim's shared material: read it, never change it.
    /// </summary>
    public struct StaticMaterialContext
    {
        public ModelHandle Model { get; internal set; }
        /// <summary>The material slot (index into StaticModelGeometry.MaterialNames).</summary>
        public int Slot { get; internal set; }
        public Material Original { get; internal set; }
    }

    public static partial class Api
    {
        /// <summary>(Version 61) True while models are drawn, so the static model calls below take effect (with or without floors).</summary>
        public static bool StaticModelExtensionsActive => ModelsOn;

        /// <summary>
        /// (Version 61) How a row of the current map is drawn now: visible (a faded floor counts), its floor, the viewed floor, its offset.
        /// Reads the state the renderer last prepared; changes nothing. Other maps, rows off the map or between floors and a map not
        /// drawn yet give Visible false. No camera frustum or roof test. Once per row or group, not per vertex.
        /// </summary>
        public static StaticModelView ReadStaticModelView(Map map, int row)
        {
            return StaticModelExtensions.ReadView(map, row);
        }

        /// <summary>(Version 61) A copy of an ordinary textured model's faces; null for other models (no textures, own colours, leaves,
        /// twigs, glow, fire, beams, runtime textures). For start-up, not every frame.</summary>
        public static StaticModelGeometry ReadStaticModel(ModelHandle handle)
        {
            return StaticModelExtensions.Read(handle);
        }

        /// <summary>
        /// (Version 61) Gives an ordinary textured model a normal and tangent per corner (as <see cref="ReadStaticModel"/> orders them; unit,
        /// at right angles, tangent w +1 or -1) and a factory for the materials of its textured parts. Static building prints of the model
        /// then carry the basis and draw with the factory's materials. Once per model, main thread. Throws on wrong arrays, an
        /// unsupported model or a second registration.
        /// </summary>
        public static void RegisterStaticSurface(ModelHandle model, Vector3[] normals, Vector4[] tangents, Func<StaticMaterialContext, Material> material)
        {
            StaticModelExtensions.Register(model, normals, tangents, material);
        }

        /// <summary>
        /// (Version 61) A rule that picks another model for a static building print (near or far detail, say). The first non-null answer
        /// wins, in the order added; null keeps the model the game's state picked. Not used for shadows, ghosts, damage marks or anything
        /// moving. A selector that throws or returns an unsupported model is dropped with one warning. Throws on null or a second add.
        /// </summary>
        public static void AddStaticModelSelector(Func<StaticModelContext, ModelHandle> selector)
        {
            StaticModelExtensions.Add(selector);
        }

        /// <summary>(Version 61) Drops a selector added with <see cref="AddStaticModelSelector"/>; nothing if it is not there.</summary>
        public static void RemoveStaticModelSelector(Func<StaticModelContext, ModelHandle> selector)
        {
            StaticModelExtensions.Remove(selector);
        }

        /// <summary>(Version 61) Marks the building's map section for a rebuild, so selectors are asked again. The rebuild comes with the
        /// game's next section update; the caller limits how often it asks.</summary>
        public static void InvalidateStaticModel(Thing thing)
        {
            if (thing is Building && thing.Spawned)
            {
                thing.Map.mapDrawer.MapMeshDirty(thing.Position, MapMeshFlagDefOf.Things, false, false);
            }
        }

        /// <summary>
        /// (Version 61) Sets up a consumer's own material for static textured faces: IsoRim's own-cell lighting and culling, and with
        /// supportsFloorFade the faded copies of peeked floors (its shader must read _IsoFade and _IsoFadeFrom). Call before it is first
        /// printed. It does not make a shader fit IsoRim: IsoRim's shader internals are no stable interface (see Api.md part 42).
        /// </summary>
        public static void ConfigureStaticMaterial(Material material, bool supportsFloorFade = false)
        {
            if (material == null)
            {
                throw new ArgumentNullException(nameof(material));
            }
            IsoGlow.Use(material, IsoGlow.OwnCell);
            Cull.RegisterStaticMaterial(material);
            if (supportsFloorFade)
            {
                FadeFloors.RegisterStaticMaterial(material);
            }
        }

        internal static ModelHandle StaticHandle(Model model)
        {
            return HandleOf(model);
        }
    }
}
