using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The game's zoomed-out silhouettes in 3D. The game decides who gets one and in which colour
    // (SilhouetteUtility.ShouldHighlightInt / GetColor, its options, its fade); IsoRim decides only when (the camera's
    // altitude above the viewed floor, mapped to the zoom sizes the game uses: one rule in every camera mode, so first
    // person and low close-ups never reach it) and where: a card of the game's silhouette shape facing the camera,
    // standing on the thing's spot, drawn over everything (IsoRimSilhouette: no depth test).
    public static class Silhouettes3D
    {
        // Altitude per unit of the game's zoom size for the default isometric camera (35.3 degrees, 40-degree view).
        private static readonly float AltitudePerSize = Mathf.Sin(35.3f * Mathf.Deg2Rad) / View3D.DefaultTanHalf;

        private static Func<Thing, bool> shouldHighlightInt;
        private static Func<Thing, Color> getColor;
        private static Shader shader;
        private static bool tried;
        private static int colorId;
        private static List<ThingDef> thingDefs;
        private static readonly Dictionary<Texture, Material> mats = new Dictionary<Texture, Material>();
        private static readonly Dictionary<Color, MaterialPropertyBlock> blocks = new Dictionary<Color, MaterialPropertyBlock>();

        private static bool Init()
        {
            if (!tried)
            {
                tried = true;
                shouldHighlightInt = AccessTools.MethodDelegate<Func<Thing, bool>>(AccessTools.Method(typeof(SilhouetteUtility), "ShouldHighlightInt"));
                getColor = AccessTools.MethodDelegate<Func<Thing, Color>>(AccessTools.Method(typeof(SilhouetteUtility), "GetColor"));
                shader = R3DMaterials.ShaderOf("IsoRimSilhouette");
                colorId = Shader.PropertyToID("_Color");
                thingDefs = new List<ThingDef>();
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs)
                {
                    if (d.drawHighlight && d.category != ThingCategory.Pawn)
                    {
                        thingDefs.Add(d);
                    }
                }
            }
            return shouldHighlightInt != null && getColor != null && shader != null;
        }

        // The game's silhouette alpha for this camera (0 = none): its gate (0.9 x the widest zoom) and fade (0.85 .. 1 x),
        // read off the camera's altitude over the viewed floor turned into the zoom size the default camera would have.
        public static float Alpha()
        {
            CameraDriver cd = Find.CameraDriver;
            if (cd == null || Prefs.DotHighlightDisplayMode == DotHighlightDisplayMode.None || Prefs.HighlightStyleMode != HighlightStyleMode.Silhouettes)
            {
                return 0f;
            }
            float floorH;
            int shift;
            Floors.FloorPlane(Floors.PointerTop, out floorH, out shift);
            float size = (View3D.Altitude - floorH) / AltitudePerSize;
            float max = cd.config.sizeRange.max;
            if (size < max * 0.9f)
            {
                return 0f;
            }
            return 0.9f * Mathf.Clamp01(Mathf.InverseLerp(max * 0.85f, max, size));
        }

        // Called every frame while the 3D view is on.
        public static void DrawAll()
        {
            Map map = Find.CurrentMap;
            if (map == null || WorldComponent_GravshipController.GravshipRenderInProgess || Find.ScreenshotModeHandler.Active)
            {
                return;
            }
            float alpha = Alpha();
            if (alpha <= 0f || !Init())
            {
                return;
            }
            try
            {
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);   // the game's cull (3D-widened, floors included)
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Draw(pawns[i], view, alpha);
                }
                for (int d = 0; d < thingDefs.Count; d++)
                {
                    List<Thing> ts = map.listerThings.ThingsOfDef(thingDefs[d]);
                    for (int j = 0; j < ts.Count; j++)
                    {
                        Draw(ts[j], view, alpha);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing silhouettes failed: " + e, 93611901);
            }
        }

        // As SilhouetteUtility.ShouldHighlight, sharing its per-tick cache.
        private static bool ShouldHighlight(Thing t)
        {
            if (t.shouldHighlightCachedTick == GenTicks.TicksGame)
            {
                return t.shouldHighlightCached;
            }
            t.shouldHighlightCachedTick = GenTicks.TicksGame;
            return t.shouldHighlightCached = shouldHighlightInt(t);
        }

        // The silhouette graphic PawnRenderer.ParallelPreRenderPawnAt uses.
        private static Graphic PawnGraphic(Pawn p)
        {
            if (p.ageTracker == null)
            {
                return null;
            }
            if (p.RaceProps.Humanlike)
            {
                return p.ageTracker.CurLifeStage?.silhouetteGraphicData?.Graphic;
            }
            PawnKindLifeStage ks = p.ageTracker.CurKindLifeStage;
            if (ks?.silhouetteGraphicData != null)
            {
                return ks.silhouetteGraphicData.Graphic;
            }
            return p.Drawer?.renderer?.BodyGraphic ?? ks?.bodyGraphicData?.Graphic;
        }

        private static void Draw(Thing t, CellRect view, float alpha)
        {
            if (t == null || !t.Spawned || !t.def.drawHighlight || !view.Contains(t.Position))
            {
                return;
            }
            Matrix4x4 rowMatrix;
            if (Floors.On && !Floors.RowMatrix(t.Position.z, out rowMatrix))
            {
                return;   // on a floor that is not drawn now (hidden, ProjectZ): its things are not drawn, nor their silhouettes
            }
            if (!t.def.seeThroughFog && t.Fogged())
            {
                return;   // the game's cull: nothing under fog
            }
            if (!ShouldHighlight(t))
            {
                return;
            }
            Pawn pawn = t as Pawn;
            Graphic g = pawn != null ? PawnGraphic(pawn) : t.Graphic;
            if (g == null)
            {
                return;
            }
            Vector2 ds = pawn != null ? g.drawSize : BadArt.SizeOf(g, t);
            Vector2 sz2 = pawn == null && t.def.rotatable ? new Vector2(ds.y, ds.x) : ds;   // as DrawSilhouette
            float inv = Find.CameraDriver.InverseFovScale.x;
            float sx = inv * (sz2.x < 2.5f ? sz2.x + SilhouetteUtility.AdjustScale(sz2.x) : sz2.x);
            float sz = inv * (sz2.y < 2.5f ? sz2.y + SilhouetteUtility.AdjustScale(sz2.y) : sz2.y);
            bool west = (pawn != null ? View3D.SeenFacing(t.Rotation) : t.Rotation) == Rot4.West;
            Material src = west ? g.MatWest : g.MatEast;
            Texture tex = src != null ? src.mainTexture : null;
            if (tex == null)
            {
                return;
            }
            float s = View3D.Squash;
            Quaternion r = Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f);   // as View3D.UprightMatrix
            float rise = sz * 0.5f * Mathf.Max(0f, (r * Vector3.forward).y);    // the card's foot on the ground
            Vector3 dp = t.DrawPos;
            Vector3 off = Floors.DrawnOffset(t);
            if (!View3D.InFront(dp + off))
            {
                return;
            }
            Vector3 at = new Vector3(dp.x + off.x, rise / s + off.y, dp.z + off.z);   // upper and hidden floors as Floors.Drawn
            Matrix4x4 m = Matrix4x4.Translate(at) * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f)) * Matrix4x4.Rotate(r)
                * Matrix4x4.Scale(new Vector3(sx, 1f, sz));
            Mesh mesh = west ? MeshPool.GridPlaneFlip(Vector2.one) : MeshPool.GridPlane(Vector2.one);
            Graphics.DrawMesh(mesh, m, MaterialFor(tex), 0, null, 0, BlockFor(getColor(t), alpha));
        }

        private static Material MaterialFor(Texture tex)
        {
            Material m;
            if (mats.TryGetValue(tex, out m) && m != null)
            {
                return m;
            }
            if (mats.Count > 512)
            {
                foreach (Material old in mats.Values)
                {
                    UnityEngine.Object.Destroy(old);
                }
                mats.Clear();
            }
            m = new Material(shader);
            m.name = "IsoRim_Silhouette";
            m.mainTexture = tex;
            mats[tex] = m;
            return m;
        }

        private static MaterialPropertyBlock BlockFor(Color c, float alpha)
        {
            Color key = new Color(c.r, c.g, c.b, 1f);
            MaterialPropertyBlock b;
            if (!blocks.TryGetValue(key, out b))
            {
                b = new MaterialPropertyBlock();
                blocks[key] = b;
            }
            b.SetColor(colorId, new Color(c.r, c.g, c.b, alpha));
            return b;
        }
    }
}
