using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // How an extra mesh on a pawn's 2D card takes the night and indoor light (Api.DrawOnCard).
    public enum CardLight
    {
        LikeCard,   // darkened by the game's lighting overlay with the card (drawn after the card, before the overlay)
        Unlit       // drawn after the lighting overlay at full brightness (glows, emissive marks)
    }

    // Extra meshes on upright pawn cards, for other mods.
    //
    // A standing 2D pawn is stood up as a card by DrawHook while the game draws it (Patches.RenderPawnAt_Prefix). Its
    // pieces are not always drawn one by one: zoomed out (the game's root size over 18) a person is one quad from the
    // game's pawn atlas, baked off screen by its cache camera only when the pawn's look changes. So an extra mesh must not
    // ride on the card's own draw calls; it is drawn here with the card's transform, noted for each pawn each frame.
    // The card is lit by the game's lighting overlay pulled toward the camera (queue 3100, Glow.cs), so queues decide
    // the light: after the card's sprites (2900) and before the overlay is lit like the card, after it is not.
    public static partial class Api
    {
        // True when this pawn is currently drawn as an upright 2D card in the 3D view (3D on, pawn spawned and visible,
        // not a 3D figure).
        public static bool IsCard(Pawn pawn)
        {
            Matrix4x4 xform, extra;
            return Cards.TryGet(pawn, out xform, out extra);
        }

        // Draws mesh on the pawn's card this frame. gameMatrix is the matrix you'd pass to Graphics.DrawMesh in vanilla
        // (top-down game space, e.g. the same matrix a render node piece uses). IsoRim applies the card's upright transform,
        // floor and camera. LikeCard: darkened by the night/indoor lighting like the card; Unlit: drawn after the lighting
        // overlay (emissive). Returns false (and draws nothing) when the pawn isn't a card right now, so the caller can
        // fall back to vanilla drawing. Call from draw-time code (a PawnRenderNodeWorker, or MapComponentUpdate / any
        // per-frame draw), once per frame per mesh.
        //
        // The material's own render queue is kept within the band of its light: LikeCard 2901..3099 (after the card's
        // sprites, so a mesh on the card's plane shows over it), Unlit 3101..3174 (after the overlay, under IsoRim's fog
        // of war). Note that zoomed out a person's render nodes run only when the game re-bakes its atlas picture, not
        // every frame: per-frame overlays belong in MapComponentUpdate or similar.
        public static bool DrawOnCard(Pawn pawn, Mesh mesh, Matrix4x4 gameMatrix, Material material, CardLight light = CardLight.LikeCard, MaterialPropertyBlock props = null)
        {
            Matrix4x4 xform, extra;
            if (mesh == null || material == null || !Cards.TryGet(pawn, out xform, out extra))
            {
                return false;
            }
            int queue = material.renderQueue;
            queue = light == CardLight.Unlit ? Mathf.Clamp(queue, 3101, 3174) : Mathf.Clamp(queue, 2901, 3099);
            // Stacked floors: the row-to-floor move is made in Graphics itself (Floors.Single_Prefix), as for the card.
            Graphics.DrawMesh(mesh, extra * xform * gameMatrix, material, 0, null, 0, props,
                UnityEngine.Rendering.ShadowCastingMode.Off, false, queue == material.renderQueue ? 0 : queue);
            return true;
        }
    }

    // The card transform of every pawn stood up this frame (or the last, for calls made before the pawns are drawn).
    internal static class Cards
    {
        private struct Card
        {
            public int frame;        // the frame the game last drew the pawn on the map camera
            public bool upright;     // stood up as a card then
            public Matrix4x4 xform;  // DrawHook.xform
            public Matrix4x4 extra;  // DrawHook.floorMatrix (a stair step's rise) or identity
        }

        private static readonly Dictionary<Pawn, Card> cards = new Dictionary<Pawn, Card>();
        // (Api 76) While a photo's draws are made its cards are kept apart: the shown view's stay as they were drawn.
        private static readonly Dictionary<Pawn, Card> photoCards = new Dictionary<Pawn, Card>();

        private static Dictionary<Pawn, Card> Now
        {
            get { return View3D.InPhoto ? photoCards : cards; }
        }

        // A photo's draws start (Photos.Shoot): none of a former photo's cards is kept.
        public static void BeginPhoto()
        {
            photoCards.Clear();
        }
        private static readonly List<Pawn> stale = new List<Pawn>();
        private static int pruned;

        // RenderPawnAt_Prefix, for a pawn the map camera draws (before it decides how).
        public static void Drawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            Card c;
            Dictionary<Pawn, Card> set = Now;
            set.TryGetValue(pawn, out c);
            c.frame = Time.frameCount;
            c.upright = false;
            set[pawn] = c;
            Prune();
        }

        // RenderPawnAt_Prefix, once the pawn is stood up as a card (DrawHook.xform and floor set).
        public static void Upright(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            Now[pawn] = new Card
            {
                frame = Time.frameCount,
                upright = true,
                xform = DrawHook.xform,
                extra = DrawHook.floor ? DrawHook.floorMatrix : Matrix4x4.identity
            };
        }

        public static bool TryGet(Pawn pawn, out Matrix4x4 xform, out Matrix4x4 extra)
        {
            xform = extra = Matrix4x4.identity;
            Card c = default(Card);
            if (pawn == null || !View3D.Ready || !pawn.Spawned || pawn.Map != Find.CurrentMap || pawn.IsHiddenFromPlayer()
                || !Now.TryGetValue(pawn, out c) || !c.upright || Time.frameCount - c.frame > 1)
            {
                return false;
            }
            xform = c.xform;
            extra = c.extra;
            return true;
        }

        private static void Prune()
        {
            int now = Time.frameCount;
            if (now - pruned < 600)
            {
                return;
            }
            pruned = now;
            foreach (KeyValuePair<Pawn, Card> kv in cards)
            {
                if (now - kv.Value.frame > 2)
                {
                    stale.Add(kv.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                cards.Remove(stale[i]);
            }
            stale.Clear();
        }
    }
}
