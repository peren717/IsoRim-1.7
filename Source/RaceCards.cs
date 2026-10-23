using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 25: a race's own render nodes on its 3D figure (HAR body addons: ears, tails, horns, hair drawn as an addon), as
    // cards of the game's sprites. Generic: any node of the pawn's render tree whose class comes from another mod's
    // assembly (not the game's: vanilla gene parts on people are left to other mods, as before), under the head or the
    // body. Worked out with the look (People.Work, every RefreshFrames) for the four facings from the game's own render tree:
    // the node's matrix against its head or body node (TryGetMatrix: HAR's offsets per facing, body type and head type,
    // its angle, size and mirroring are all in it), its material (HAR's colour channels), its mesh and whether it shows
    // (CanDrawNow: conditions, skip flags such as a hat hiding the ears). Drawn with the figure: the card of the facing the
    // camera sees, in the head or body frame (it follows pose, sitting, stacked floors and Api.DrawFigure; body cards are left
    // out while the figure lies or crawls: a flat picture reads badly on a lying body).
    internal static class RaceCards
    {
        internal sealed class Card
        {
            public Mesh mesh;
            public Material material;
            public Matrix4x4 local;   // in the head or body frame
            public bool head;
        }

        internal sealed class Node
        {
            public string path;       // its texture path (ModelLibrary.NormalPath), for pack addon entries
            public bool onHead;
            public bool shown;        // drawn in at least one facing
            public bool bound;        // a pack entry draws it (model) or hides it: no card
            public Color colour = Color.white;   // its material's colour (south), for addon models
            public readonly Card[] byFacing = new Card[4];
        }

        internal sealed class Set
        {
            public readonly List<Node> nodes = new List<Node>();
        }

        private static readonly Matrix4x4[] PictureToFigure = new Matrix4x4[4];

        static RaceCards()
        {
            // The game's flat picture (x right, z up the picture, y toward the viewer above) onto the figure (x its right,
            // y up, z forward) as the facing's sprite shows it: north from behind, east from its right, south from the front,
            // west from its left.
            Vector3[] right = { new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, -1f) };
            Vector3[] toViewer = { new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(-1f, 0f, 0f) };
            for (int f = 0; f < 4; f++)
            {
                Matrix4x4 m = Matrix4x4.identity;
                m.SetColumn(0, right[f]);
                m.SetColumn(1, toViewer[f]);
                m.SetColumn(2, new Vector4(0f, 1f, 0f, 0f));
                m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
                PictureToFigure[f] = m;
            }
        }

        internal static Vector3 ToViewer(int facing)
        {
            return PictureToFigure[facing].GetColumn(1);
        }

        private delegate void AdjustParmsCall(PawnRenderTree tree, ref PawnDrawParms parms);
        private static AdjustParmsCall adjustParms;
        private static bool looked;

        internal static Set Collect(Pawn pawn, People.Look look, RaceLook r)
        {
            if (r.pack != null && !r.pack.addonCards && (r.pack.addons == null || r.pack.addons.Count == 0))
            {
                return null;
            }
            PawnRenderTree tree = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.renderTree : null;
            if (tree == null)
            {
                return null;
            }
            try
            {
                if (!tree.Resolved)
                {
                    tree.EnsureInitialized(PawnRenderFlags.Clothes | PawnRenderFlags.Headgear);
                }
                if (tree.rootNode == null)
                {
                    return null;
                }
                PawnRenderNode headNode, bodyNode;
                tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out headNode);
                tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out bodyNode);
                List<PawnRenderNode> foreign = new List<PawnRenderNode>();
                Walk(tree.rootNode, foreign);
                if (foreign.Count == 0)
                {
                    return null;
                }
                if (!looked)
                {
                    looked = true;
                    var m = AccessTools.Method(typeof(PawnRenderTree), "AdjustParms");
                    adjustParms = m != null ? (AdjustParmsCall)Delegate.CreateDelegate(typeof(AdjustParmsCall), null, m) : null;
                }
                Set set = new Set();
                foreach (PawnRenderNode n in foreign)
                {
                    Node node = new Node { onHead = headNode != null && Under(n, headNode) };
                    Graphic g = n.PrimaryGraphic;
                    node.path = g != null ? ModelLibrary.NormalPath(g.path) : null;
                    set.nodes.Add(node);
                }
                FigureBoxes.Box hb = FigureBoxes.Head(look);
                FigureBoxes.Box bb = FigureBoxes.Body(look);
                for (int f = 0; f < 4; f++)
                {
                    PawnDrawParms parms = PawnDrawParms.Default;
                    parms.pawn = pawn;
                    parms.matrix = Matrix4x4.identity;
                    parms.facing = new Rot4(f);
                    parms.rotDrawMode = RotDrawMode.Fresh;
                    parms.posture = PawnPosture.Standing;
                    parms.flags = PawnRenderFlags.Clothes | PawnRenderFlags.Headgear;
                    parms.dead = false;
                    if (adjustParms != null)
                    {
                        adjustParms(tree, ref parms);
                    }
                    Matrix4x4 headM = Matrix4x4.identity, bodyM = Matrix4x4.identity;
                    bool haveHead = headNode != null && tree.TryGetMatrix(headNode, parms, out headM);
                    bool haveBody = bodyNode != null && tree.TryGetMatrix(bodyNode, parms, out bodyM);
                    for (int i = 0; i < foreign.Count; i++)
                    {
                        PawnRenderNode n = foreign[i];
                        Node node = set.nodes[i];
                        if ((node.onHead && !haveHead) || (!node.onHead && !haveBody) || !n.Worker.CanDrawNow(n, parms))
                        {
                            continue;
                        }
                        Material mat = n.Worker.GetFinalizedMaterial(n, parms);
                        Mesh mesh = n.GetMesh(parms);
                        Matrix4x4 nm;
                        if (mat == null || mesh == null || !tree.TryGetMatrix(n, parms, out nm))
                        {
                            continue;
                        }
                        Matrix4x4 rel = (node.onHead ? headM : bodyM).inverse * nm;
                        node.byFacing[f] = new Card { mesh = mesh, material = mat, head = node.onHead, local = Place(rel, f, node.onHead, look, r, hb, bb) };
                        if (!node.shown || f == 2)
                        {
                            node.colour = mat.color;
                        }
                        node.shown = true;
                    }
                }
                return set;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Working out a race's addon cards failed: " + e, 93625010);
                return null;
            }
        }

        // Every node of another mod's class under the root (not the game's own: body, head, hair, apparel, gene parts ...;
        // nor Facial Animation's face, which IsoRim draws itself).
        private static void Walk(PawnRenderNode n, List<PawnRenderNode> into)
        {
            if (n == null)
            {
                return;
            }
            Type t = n.GetType();
            if (t.Assembly != typeof(PawnRenderNode).Assembly && (t.Namespace == null || !t.Namespace.StartsWith("FacialAnimation")))
            {
                into.Add(n);
            }
            if (n.children != null)
            {
                for (int i = 0; i < n.children.Length; i++)
                {
                    Walk(n.children[i], into);
                }
            }
        }

        private static bool Under(PawnRenderNode n, PawnRenderNode ancestor)
        {
            for (PawnRenderNode p = n.parent; p != null; p = p.parent)
            {
                if (p == ancestor)
                {
                    return true;
                }
            }
            return false;
        }

        // The node's matrix against its head or body node (the game's cells; y = layer height) to the figure frame for
        // facing f: the picture's middle where the game's head (body) sprite middle lands on the figure (FigureBoxes),
        // the game's cells to the frame's (the race's sizes are already in the frame), pushed out to the front or the back
        // of the head (body) by the layer's sign.
        private static Matrix4x4 Place(Matrix4x4 rel, int f, bool onHead, People.Look look, RaceLook r, FigureBoxes.Box hb, FigureBoxes.Box bb)
        {
            float layer = rel.m13;
            rel.m13 = 0f;
            Vector3 origin;
            float k;
            if (onHead)
            {
                k = FaceDecal.SpriteCells / 1.5f / Mathf.Max(0.05f, r.headDraw);
                origin = new Vector3(0f, FaceDecal.CentreHeight, 0f);
            }
            else
            {
                float up = RaceFigures.SpriteToFigure(r.build);
                float feet = r.build == "female" || r.build == "fat" ? -0.568f : (r.build == "hulk" ? -0.721f : -0.510f);
                k = up / Mathf.Max(0.05f, r.bodyDraw);
                origin = new Vector3(0f, -feet * up, 0f);
            }
            FigureBoxes.Box box = onHead ? hb : bb;
            Vector3 v = ToViewer(f);
            float reach = Mathf.Abs(Vector3.Dot(box.size * 0.5f, new Vector3(Mathf.Abs(v.x), 0f, Mathf.Abs(v.z)))) + 0.025f;
            float depth = Vector3.Dot(box.centre, v) + (layer >= 0f ? reach : -reach);
            return Matrix4x4.Translate(origin + v * depth) * PictureToFigure[f] * Matrix4x4.Scale(new Vector3(k, k, k)) * rel;
        }

        // People.Draw: the cards of the facing the camera sees, in the head and body frames (hidden parts left out).
        internal static void Draw(People.Look look, Vector3 at, float yaw, Matrix4x4 headPlace, Matrix4x4 bodyPlace, FigureParts hidden, bool bodyShown)
        {
            Set set = look.race != null ? look.race.cards : null;
            if (set == null || set.nodes.Count == 0 || (hidden & FigureParts.Addons) != 0 || FigureScope.Measuring)
            {
                return;
            }
            // The facing whose picture faces the camera most, as the game swaps its sprites while a pawn turns.
            Vector3 toCam = -View3D.GroundForward;
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            int facing = 2;
            float best = float.MinValue;
            for (int f = 0; f < 4; f++)
            {
                float d = Vector3.Dot(turn * ToViewer(f), toCam);
                if (d > best)
                {
                    best = d;
                    facing = f;
                }
            }
            bool headShown = !look.headHidden && (hidden & FigureParts.Head) == 0;
            bool body = bodyShown && (hidden & FigureParts.Body) == 0;
            float s = View3D.Squash;
            Matrix4x4 placed = Matrix4x4.Translate(new Vector3(at.x, at.y / s, at.z)) * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f)) * Matrix4x4.Rotate(turn);
            for (int i = 0; i < set.nodes.Count; i++)
            {
                Node n = set.nodes[i];
                Card c = n.byFacing[facing];
                if (n.bound || c == null || (c.head ? !headShown : !body))
                {
                    continue;
                }
                FigureScope.DrawMesh(c.mesh, c.material, placed * (c.head ? headPlace : bodyPlace) * c.local);
            }
        }
    }
}
