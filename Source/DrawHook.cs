using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Stand-ins for UnityEngine.Graphics.DrawMesh. The pawn drawing code is rewritten (see
    // Patches.RedirectDrawCalls) to call these instead. While "active" is set, every mesh drawn
    // has "xform" applied on top of its own transform; otherwise they behave exactly like the
    // originals. Each method must keep the same parameter list as the overload it replaces.
    public static class DrawHook
    {
        public static bool active;
        public static Matrix4x4 xform = Matrix4x4.identity;

        // Stacked floors (Floors): while set, everything drawn is also moved onto the floor of the thing being drawn
        // (applied after xform, in drawn space). Independent of active.
        public static bool floor;
        public static Matrix4x4 floorMatrix = Matrix4x4.identity;

        // While set (with active), each mesh is instead stood upright where it is, facing the
        // camera, and raised by billboardHeight cells: status icons, bubbles and designations
        // float above things instead of lying on the floor.
        public static bool billboard;
        public static float billboardHeight;

        // With an anchor, icons are pulled in over the anchor (the middle of the thing they
        // belong to), keeping only their spread across the screen, so a thing's icons hang
        // in a row over it, like the diamond over a Sim's head.
        public static bool hasAnchor;
        public static Vector3 billboardAnchor;

        // With a pivot, everything drawn is stood up as one card hinged on the pivot (the person a speech
        // bubble belongs to): each mesh keeps its offset from the pivot as the game lays it out seen from
        // above (east = right, north = up), so a bubble, its icon and its arrow sit round the person on
        // screen as in the game, at billboardHeight over the pivot.
        public static bool hasPivot;
        public static Vector3 billboardPivot;

        // With pivotOnFirst (and no pivot yet), the first mesh drawn becomes the pivot: everything drawn after it stands up
        // with it as one card (UprightScopes: Api.BeginUpright without a pivot).
        public static bool pivotOnFirst;

        // How far flat overlays (field edges, drag highlights, placement sprites) are kept off
        // the ground: the game spaces its drawing layers far apart in height, which the 3D view
        // would otherwise show as overlays floating in the air.
        private const float FlatKeep = 0.08f;

        public static Matrix4x4 Flatten(Matrix4x4 matrix)
        {
            return Matrix4x4.Scale(new Vector3(1f, FlatKeep, 1f)) * matrix;
        }

        // The transform to draw a mesh with while active.
        public static Matrix4x4 Transform(Matrix4x4 matrix)
        {
            if (!billboard)
            {
                return xform * matrix;
            }
            Vector3 p = matrix.GetColumn(3);
            if (pivotOnFirst && !hasPivot)
            {
                hasPivot = true;
                billboardPivot = p;
            }
            if (hasPivot)
            {
                p = new Vector3(billboardPivot.x, p.y, billboardPivot.z);
            }
            Matrix4x4 local = Matrix4x4.Translate(-p) * matrix;
            if (hasAnchor && !hasPivot)
            {
                Vector3 right = Quaternion.Euler(0f, View3D.Yaw, 0f) * Vector3.right;
                float along = Vector3.Dot(new Vector3(p.x - billboardAnchor.x, 0f, p.z - billboardAnchor.z), right);
                p = new Vector3(billboardAnchor.x + right.x * along, p.y, billboardAnchor.z + right.z * along);
            }
            float s = View3D.Squash;
            return Matrix4x4.Translate(new Vector3(p.x, p.y + billboardHeight / s, p.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f))
                * local;
        }

        // Transform (while active) and the floor (while floor).
        public static Matrix4x4 Final(Matrix4x4 matrix)
        {
            if (active)
            {
                matrix = Transform(matrix);
            }
            return floor ? floorMatrix * matrix : matrix;
        }

        // While set, nothing is drawn at all: a 3D model is standing in for the sprite.
        public static bool skip;

        // With skip: the fillable bars drawn meanwhile (GenDraw.DrawFillableBar) are still drawn, as the thing's other bars
        // (Bars.DrawFillableBar_Prefix lifts skip for each): a model stands in for the sprite, not for its gauge.
        public static bool skipKeepsBars;

        // The stand-ins found per class (DrawHook, CardDraw) for each Graphics.DrawMesh overload (ReplacementIn).
        private static readonly Dictionary<Type, Dictionary<MethodInfo, MethodInfo>> replacements = new Dictionary<Type, Dictionary<MethodInfo, MethodInfo>>();

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
        {
            if (FigureWeapons.Capturing)
            {
                FigureWeapons.Note(mesh, matrix, material);   // a 3D figure's weapon, posed by the game (FigureWeapons)
                return;
            }
            if (skip)
            {
                return;
            }
            if (Bars.FillMat != null)
            {
                material = Bars.FillOver(material);   // a fillable bar's fill: drawn after its back (Bars.FillOver)
            }
            if (active)
            {
                if (Projectiles.CatchingTip)
                {
                    Projectiles.TipDrawn(mesh, matrix);   // a 2D pawn's aimed weapon: where its barrel ends (shots start there)
                }
                matrix = Transform(matrix);
            }
            if (floor)
            {
                matrix = floorMatrix * matrix;
            }

            if (!ShipCapture.Note(mesh, matrix, material, null))
            {
                Graphics.DrawMesh(mesh, matrix, material, layer);
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer)
        {
            if (FigureWeapons.Capturing)
            {
                FigureWeapons.Note(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material);   // a 3D figure's weapon, posed by the game (FigureWeapons)
                return;
            }
            if (skip)
            {
                return;
            }
            if (active || floor)
            {
                if (!ShipCapture.Note(mesh, Final(Matrix4x4.TRS(position, rotation, Vector3.one)), material, null))
                {
                    Graphics.DrawMesh(mesh, Final(Matrix4x4.TRS(position, rotation, Vector3.one)), material, layer);
                }
            }

            else
            {
                if (!ShipCapture.Note(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material, null))
                {
                    Graphics.DrawMesh(mesh, position, rotation, material, layer);
                }
            }
        }

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex, MaterialPropertyBlock properties)
        {
            if (FigureWeapons.Capturing)
            {
                FigureWeapons.Note(mesh, matrix, material);   // a 3D figure's weapon, posed by the game (FigureWeapons)
                return;
            }
            if (skip)
            {
                return;
            }
            if (active)
            {
                matrix = Transform(matrix);
            }
            if (floor)
            {
                matrix = floorMatrix * matrix;
            }

            if (!ShipCapture.Note(mesh, matrix, material, properties, submeshIndex, true))
            {
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties);
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer, Camera camera, int submeshIndex, MaterialPropertyBlock properties)
        {
            if (FigureWeapons.Capturing)
            {
                FigureWeapons.Note(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material);   // a 3D figure's weapon, posed by the game (FigureWeapons)
                return;
            }
            if (skip)
            {
                return;
            }
            if (active || floor)
            {
                if (!ShipCapture.Note(mesh, Final(Matrix4x4.TRS(position, rotation, Vector3.one)), material, properties, submeshIndex, true))
                {
                    Graphics.DrawMesh(mesh, Final(Matrix4x4.TRS(position, rotation, Vector3.one)), material, layer, camera, submeshIndex, properties);
                }
            }

            else
            {
                if (!ShipCapture.Note(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material, properties, submeshIndex, true))
                {
                    Graphics.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex, properties);
                }
            }
        }

        // MoteBubble's arrow toward the person spoken to: the game puts it PointerOut cells from the bubble,
        // turned toward them on the ground. On a card it is turned (and moved round the bubble) to point
        // where they are on screen: pointerAt (x, z on the map; y their middle, in cells over the ground).
        private const float PointerOut = 0.6f;
        public static bool hasPointerAt;
        public static Vector3 pointerAt;

        public static void DrawPointer(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer)
        {
            if (active && billboard && hasPivot && hasPointerAt)
            {
                Vector3 d = rotation * Vector3.forward;
                Vector3 bubble = position - d * PointerOut;   // on the card: its offset from the pivot as laid out by the game
                float yaw = View3D.Yaw * Mathf.Deg2Rad;
                float pitch = View3D.Pitch * Mathf.Deg2Rad;
                float dx = pointerAt.x - billboardPivot.x, dz = pointerAt.z - billboardPivot.z;
                float across = dx * Mathf.Cos(yaw) - dz * Mathf.Sin(yaw);
                float up = (dx * Mathf.Sin(yaw) + dz * Mathf.Cos(yaw)) * Mathf.Sin(pitch) + (pointerAt.y - billboardHeight) * Mathf.Cos(pitch);
                Vector3 onCard = new Vector3(across - (bubble.x - billboardPivot.x), 0f, up - (bubble.z - billboardPivot.z));
                if (onCard.sqrMagnitude > 1e-6f)
                {
                    onCard.Normalize();
                    position = bubble + onCard * PointerOut;
                    rotation = Quaternion.LookRotation(onCard, Vector3.up);
                }
            }
            DrawMesh(mesh, position, rotation, material, layer);
        }

        // Graphic_Mote.DrawMote, for a mote in its own colour (no property block): speech bubbles while fully shown.
        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex)
        {
            if (FigureWeapons.Capturing)
            {
                FigureWeapons.Note(mesh, matrix, material);   // a 3D figure's weapon, posed by the game (FigureWeapons)
                return;
            }
            if (skip)
            {
                return;
            }
            if (active)
            {
                matrix = Transform(matrix);
            }
            if (floor)
            {
                matrix = floorMatrix * matrix;
            }

            if (!ShipCapture.Note(mesh, matrix, material, null, submeshIndex, true))
            {
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex);
            }
        }

        // Graphic_RandomRotated (weapons and other items lying at an angle): a relic in a reliquary.
        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer, Camera camera, int submeshIndex)
        {
            if (FigureWeapons.Capturing)
            {
                FigureWeapons.Note(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material);   // a 3D figure's weapon, posed by the game (FigureWeapons)
                return;
            }
            if (skip)
            {
                return;
            }
            if (active || floor)
            {
                if (!ShipCapture.Note(mesh, Final(Matrix4x4.TRS(position, rotation, Vector3.one)), material, null, submeshIndex, true))
                {
                    Graphics.DrawMesh(mesh, Final(Matrix4x4.TRS(position, rotation, Vector3.one)), material, layer, camera, submeshIndex);
                }
            }
            else
            {
                if (!ShipCapture.Note(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material, null, submeshIndex, true))
                {
                    Graphics.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex);
                }
            }
        }

        // The stand-in with the same parameter list as the given Graphics.DrawMesh overload, or null.
        public static MethodInfo ReplacementFor(MethodInfo original)
        {
            return ReplacementIn(typeof(DrawHook), original);
        }

        // The stand-in in host (DrawHook, or CardDraw: card draws only) with the same parameter list, or null.
        internal static MethodInfo ReplacementIn(Type host, MethodInfo original)
        {
            Dictionary<MethodInfo, MethodInfo> ofHost;
            if (!replacements.TryGetValue(host, out ofHost))
            {
                ofHost = new Dictionary<MethodInfo, MethodInfo>();
                replacements[host] = ofHost;
            }
            MethodInfo found;
            if (ofHost.TryGetValue(original, out found))
            {
                return found;
            }

            ParameterInfo[] ps = original.GetParameters();
            Type[] types = new Type[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                types[i] = ps[i].ParameterType;
            }
            found = host.GetMethod("DrawMesh", BindingFlags.Public | BindingFlags.Static, null, types, null);
            ofHost[original] = found;
            return found;
        }
    }

    // Stand-ins for UnityEngine.Graphics.DrawMesh in other mods' own weapon drawing (CardWeapons: their patches on the game's
    // weapon drawing and the helpers those call; Api 54's fallback, FigureWeapons.DrawOrCatchWeapon). While a pawn is stood up
    // as a 2D card they are DrawHook's own draws (up with the card, onto its floor); anything else (a 3D figure, whose weapon
    // catch stays with the game's own draws and Api 54; the 2D view; any other drawing) goes to Graphics.DrawMesh exactly as
    // the mod wrote it. The same overloads as DrawHook (Patches.IsRedirectable; CheckOverloads). Public because other mods'
    // rewritten code calls it; not Api.
    public static class CardDraw
    {
        // Once, before the card scan: every DrawHook.DrawMesh overload has its twin here (one warning naming any missing: those
        // calls then stay as drawn, RedirectDraws keeps a call it has no stand-in for).
        internal static void CheckOverloads()
        {
            foreach (MethodInfo m in typeof(DrawHook).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != nameof(DrawHook.DrawMesh))
                {
                    continue;
                }
                Type[] types = m.GetParameters().Select(p => p.ParameterType).ToArray();
                if (typeof(CardDraw).GetMethod(nameof(DrawMesh), BindingFlags.Public | BindingFlags.Static, null, types, null) == null)
                {
                    Log.Warning("[IsoRim] Card weapons: CardDraw has no DrawMesh(" + string.Join(", ", types.Select(t => t.Name)) + ") like DrawHook's.");
                }
            }
        }

        // A pawn is being stood up as a card now (Patches.DrawingCard), and no 3D figure's weapon is being caught.
        public static bool OnCard => Patches.DrawingCard && !FigureWeapons.Capturing;

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
        {
            if (OnCard)
            {
                DrawHook.DrawMesh(mesh, matrix, material, layer);
            }
            else
            {
                Graphics.DrawMesh(mesh, matrix, material, layer);
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer)
        {
            if (OnCard)
            {
                DrawHook.DrawMesh(mesh, position, rotation, material, layer);
            }
            else
            {
                Graphics.DrawMesh(mesh, position, rotation, material, layer);
            }
        }

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex, MaterialPropertyBlock properties)
        {
            if (OnCard)
            {
                DrawHook.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties);
            }
            else
            {
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties);
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer, Camera camera, int submeshIndex, MaterialPropertyBlock properties)
        {
            if (OnCard)
            {
                DrawHook.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex, properties);
            }
            else
            {
                Graphics.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex, properties);
            }
        }

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex)
        {
            if (OnCard)
            {
                DrawHook.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex);
            }
            else
            {
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex);
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer, Camera camera, int submeshIndex)
        {
            if (OnCard)
            {
                DrawHook.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex);
            }
            else
            {
                Graphics.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex);
            }
        }
    }
}
