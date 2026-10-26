using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>Which side of a garment an <see cref="ApparelDecal"/> goes on (Api version 10).</summary>
    public enum DecalSide
    {
        /// <summary>The figure's front: placed as on the game's south-facing body sprite.</summary>
        Front,

        /// <summary>The figure's back: placed as on the game's north-facing body sprite (seen from behind).</summary>
        Back,
    }

    /// <summary>
    /// A picture printed on a worn garment of IsoRim's 3D person figures (Api version 10): a rectangle of the game's body
    /// sprite, projected straight onto the side of the garment that faces that way. It follows the figure's pose, facing,
    /// build and stacked floor, and shows only on the garment's own surface (inside its outline). Hand these out from an
    /// <see cref="ApparelDecalProvider"/>.
    /// </summary>
    public sealed class ApparelDecal
    {
        /// <summary>The material drawn, as is (for example a game Graphic's MatSouth, or one from MaterialPool). Null:
        /// made from <see cref="texture"/>, <see cref="mask"/> and the colours below.</summary>
        public Material material;

        /// <summary>Without a material: the picture, drawn with the game's Cutout shader tinted by <see cref="colour"/>,
        /// or with CutoutComplex when <see cref="mask"/> is set (as the game's two-colour apparel).</summary>
        public Texture2D texture;

        /// <summary>Without a material: the mask of a two-colour picture (CutoutComplex: colour where the mask is red,
        /// colourTwo elsewhere, as the game's masks).</summary>
        public Texture2D mask;

        public Color colour = Color.white;

        public Color colourTwo = Color.white;

        /// <summary>Front or back.</summary>
        public DecalSide side = DecalSide.Front;

        /// <summary>The middle of the picture in the game's body sprite of that side: cells from the sprite's middle (the
        /// pawn's draw position), x to the right as the viewer of that side sees it, y up the picture. The adult body
        /// quad is 1.5 cells, so (0, -0.2) is 0.2 cells below the middle, as a render node's offset (0, 0, -0.2).</summary>
        public Vector2 centre = Vector2.zero;

        /// <summary>The picture's size in the same cells (the whole body quad: 1.5 by 1.5).</summary>
        public Vector2 size = new Vector2(1.5f, 1.5f);

        /// <summary>Also on the garment's faces that turn away from that side (its top, its sides): the picture's nearest
        /// edge stretched over them. Front takes every face turned at all to the front, Back the rest, so a front and a
        /// back decal over the whole body quad colour the whole garment (a pattern printed on the cloth). Default: only
        /// the faces that look toward that side.</summary>
        public bool wrap;
    }

    /// <summary>
    /// Gives the decals for one worn item (Api version 10; <see cref="Api.AddApparelDecals"/>): add zero or more to
    /// decals (a list IsoRim clears and reuses). Asked when IsoRim works out how the wearer looks (about every 1.5 seconds,
    /// at once after <see cref="Api.RefreshApparelDecals"/>), not every frame. Drawn in the order added, each over the one
    /// before.
    /// </summary>
    public delegate void ApparelDecalProvider(Apparel apparel, Pawn wearer, List<ApparelDecal> decals);

    // The apparel decals (Api 10): providers by apparel def, asked by People.Work for each worn item that has a body garment
    // of its own on the figure (coat or armour, a garment over the coat, a middle-layer or shirt over-shape; bound by an
    // ApparelModelDef or IsoRim's own shape), and drawn by People.Draw after the body. Each decal is a mesh made from the
    // garment's faces that face its side, clipped to its rectangle of the body sprite (FigureBoxes.BodySprite: the
    // sprite's feet on the figure's feet, its chin at the neck, its width across the body), its picture projected
    // straight on, a little out from the surface; drawn with the decal's own material like FaceDecal's faces (lit by the
    // garment's light pass, faded with its floor). With no provider registered nothing here runs.
    internal static class ApparelDecals
    {
        // A decal ready to draw: its mesh (garment model space, before the body scale) and material.
        internal struct Placed
        {
            public Mesh mesh;
            public Material material;
        }

        private static readonly Dictionary<ThingDef, ApparelDecalProvider[]> byDef = new Dictionary<ThingDef, ApparelDecalProvider[]>();
        private static ApparelDecalProvider[] any = new ApparelDecalProvider[0];
        internal static int Count;

        internal static void Add(ThingDef apparel, ApparelDecalProvider provider)
        {
            if (provider == null)
            {
                return;
            }
            ApparelDecalProvider[] list = apparel == null ? any : (byDef.TryGetValue(apparel, out ApparelDecalProvider[] l) ? l : new ApparelDecalProvider[0]);
            if (Array.IndexOf(list, provider) >= 0)
            {
                return;
            }
            ApparelDecalProvider[] next = new ApparelDecalProvider[list.Length + 1];
            list.CopyTo(next, 0);
            next[list.Length] = provider;
            Set(apparel, next);
        }

        internal static void Remove(ThingDef apparel, ApparelDecalProvider provider)
        {
            ApparelDecalProvider[] list = apparel == null ? any : (byDef.TryGetValue(apparel, out ApparelDecalProvider[] l) ? l : null);
            int i = list != null ? Array.IndexOf(list, provider) : -1;
            if (i < 0)
            {
                return;
            }
            ApparelDecalProvider[] next = new ApparelDecalProvider[list.Length - 1];
            Array.Copy(list, 0, next, 0, i);
            Array.Copy(list, i + 1, next, i, list.Length - i - 1);
            Set(apparel, next);
        }

        private static void Set(ThingDef apparel, ApparelDecalProvider[] list)
        {
            if (apparel == null)
            {
                any = list;
            }
            else if (list.Length == 0)
            {
                byDef.Remove(apparel);
            }
            else
            {
                byDef[apparel] = list;
            }
            int n = any.Length;
            foreach (ApparelDecalProvider[] l in byDef.Values)
            {
                n += l.Length;
            }
            Count = n;
        }

        private static readonly List<ApparelDecal> scratch = new List<ApparelDecal>();

        // The decals of a person's body garments (People.Work, after Dress); null when there are none.
        internal static List<Placed> Collect(Pawn pawn, People.Look look)
        {
            List<Placed> all = null;
            BodyTypeDef type = pawn.story != null ? pawn.story.bodyType : null;
            Collect(pawn, look, type, look.coat, look.coatFrom, ref all);
            Collect(pawn, look, type, look.mid, look.midFrom, ref all);
            Collect(pawn, look, type, look.shirtWear, look.shirtWearFrom, ref all);
            Collect(pawn, look, type, look.overCoat, look.overCoatFrom, ref all);
            return all;
        }

        private static void Collect(Pawn pawn, People.Look look, BodyTypeDef type, Model garment, Apparel from, ref List<Placed> all)
        {
            if (garment == null || from == null)
            {
                return;
            }
            ApparelDecalProvider[] own = byDef.TryGetValue(from.def, out ApparelDecalProvider[] l) ? l : null;
            if (own == null && any.Length == 0)
            {
                return;
            }
            scratch.Clear();
            Ask(own, from, pawn);
            Ask(any, from, pawn);
            for (int i = 0; i < scratch.Count; i++)
            {
                ApparelDecal d = scratch[i];
                if (d == null || d.size.x <= 0f || d.size.y <= 0f)
                {
                    continue;
                }
                Material mat = MaterialOf(d);
                if (mat == null)
                {
                    continue;
                }
                int layer = all != null ? all.Count : 0;
                Mesh mesh = MeshFor(garment, look, type, d.side, d.centre, d.size, d.wrap, layer);
                if (mesh == null)
                {
                    continue;
                }
                (all ?? (all = new List<Placed>())).Add(new Placed { mesh = mesh, material = mat });
            }
            scratch.Clear();
        }

        private static void Ask(ApparelDecalProvider[] list, Apparel apparel, Pawn pawn)
        {
            if (list == null)
            {
                return;
            }
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    list[i](apparel, pawn, scratch);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] An apparel decal provider (" + (list[i].Method.DeclaringType != null ? list[i].Method.DeclaringType.FullName : "?") + ") failed: " + e,
                        93619100 ^ list[i].Method.GetHashCode());
                }
            }
        }

        private static Material MaterialOf(ApparelDecal d)
        {
            if (d.material != null)
            {
                return d.material;
            }
            if (d.texture == null)
            {
                return null;
            }
            MaterialRequest req = new MaterialRequest(d.texture, d.mask != null ? ShaderDatabase.CutoutComplex : ShaderDatabase.Cutout, d.colour);
            if (d.mask != null)
            {
                req.maskTex = d.mask;
                req.colorTwo = d.colourTwo;
            }
            return MaterialPool.MatFrom(req);
        }

        // Draws a look's decals with the body (People.Draw: at, yaw and bodyPlace as the body's merged meshes).
        internal static void Draw(People.Look look, Vector3 at, float yaw, Matrix4x4 bodyPlace)
        {
            List<Placed> list = look.decals;
            if (list == null || FigureScope.Measuring)
            {
                return;
            }
            float s = View3D.Squash;
            Matrix4x4 placed = Matrix4x4.Translate(new Vector3(at.x, at.y / s, at.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * bodyPlace
                * Matrix4x4.Scale(look.bodyScale);
            Matrix4x4 m = Floors.DrawnMatrix(FigureScope.Apply(placed));
            for (int i = 0; i < list.Count; i++)
            {
                Placed p = list[i];
                if (p.mesh != null && p.material != null)
                {
                    Graphics.DrawMesh(p.mesh, m, p.material, 0);
                }
            }
        }

        // ---- the projected meshes

        // How far each decal stands out from the garment's surface (cells, along its normals), the first and each one over it.
        private const float Lift = 0.004f;
        private const float LiftStep = 0.002f;
        // A face takes a decal when its normal points that way at least this much (z of a unit normal); a wrapping decal
        // takes every face on its side (Front: z >= 0, Back: z < 0).
        private const float Facing = 0.1f;

        private static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();

        private static Mesh MeshFor(Model garment, People.Look look, BodyTypeDef type, DecalSide side, Vector2 centre, Vector2 size, bool wrap, int layer)
        {
            string key = string.Concat(garment.id.ToString(CultureInfo.InvariantCulture), ",", look.upper != null ? look.upper.id.ToString(CultureInfo.InvariantCulture) : "-", ",",
                look.lower != null ? look.lower.id.ToString(CultureInfo.InvariantCulture) : "-", ",", type != null ? type.defName : "-", ",",
                F(look.bodyScale.x), ",", F(look.bodyScale.y), ",", F(look.bodyScale.z), ",", side == DecalSide.Front ? "F" : "B", ",", F(centre.x), ",", F(centre.y), ",", F(size.x), ",", F(size.y), ",", wrap ? "W" : "-", ",", layer.ToString(CultureInfo.InvariantCulture));
            Mesh mesh;
            if (meshes.TryGetValue(key, out mesh))
            {
                return mesh;
            }
            if (meshes.Count > 512)
            {
                foreach (Mesh old in meshes.Values)
                {
                    DynamicModels.Retire(old);   // looks still holding one draw nothing until worked out again (People.Work)
                }
                meshes.Clear();
            }
            try
            {
                mesh = Build(garment, look, type, side, centre, size, wrap, Lift + LiftStep * layer);
            }
            catch (Exception e)
            {
                mesh = null;
                Log.ErrorOnce("[IsoRim] Making an apparel decal failed: " + e, 93619101);
            }
            meshes[key] = mesh;
            return mesh;
        }

        private static string F(float f)
        {
            return f.ToString("F3", CultureInfo.InvariantCulture);
        }

        private struct V
        {
            public Vector3 p;
            public Vector3 n;
            public Vector2 s;   // body sprite cells

            public static V Lerp(V a, V b, float t)
            {
                return new V { p = Vector3.Lerp(a.p, b.p, t), n = Vector3.Lerp(a.n, b.n, t), s = Vector2.Lerp(a.s, b.s, t) };
            }
        }

        private static Mesh Build(Model g, People.Look look, BodyTypeDef type, DecalSide side, Vector2 centre, Vector2 size, bool wrap, float lift)
        {
            // Sprite -> body frame (body-scaled) -> garment model space (before the body scale). The back is the north sprite,
            // seen from behind: not turned half round as the south one is.
            Matrix4x4 toModel = Matrix4x4.Scale(new Vector3(1f / look.bodyScale.x, 1f / look.bodyScale.y, 1f / look.bodyScale.z))
                * FigureBoxes.BodySprite(look, type)
                * (side == DecalSide.Back ? Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up)) : Matrix4x4.identity);
            Matrix4x4 toSprite = toModel.inverse;
            float sign = side == DecalSide.Front ? 1f : -1f;
            Vector2 lo = centre - size * 0.5f;
            List<Vector3> verts = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> tris = new List<int>();
            List<V> poly = new List<V>(8);
            List<V> next = new List<V>(8);
            Vector3[] p = g.positions;
            // Each face's outward normal, and per corner position the faces' normals summed (by area): the decal stands out
            // along that smoothed normal, so neighbouring faces meet at the same lifted corners (no cracks between them).
            Vector3[] faceNormal = new Vector3[p.Length / 3];
            Dictionary<Vector3, Vector3> smooth = new Dictionary<Vector3, Vector3>();
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                Vector3 a = p[i], b = p[i + 1], c = p[i + 2];
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-14f)
                {
                    continue;
                }
                Vector3 vn = g.normals != null ? g.normals[i] + g.normals[i + 1] + g.normals[i + 2] : Vector3.zero;
                if (vn.sqrMagnitude > 1e-8f ? Vector3.Dot(n, vn) < 0f : Vector3.Dot(n, (a + b + c) / 3f - new Vector3(0f, (a.y + b.y + c.y) / 3f, 0f)) < 0f)
                {
                    n = -n;   // outward, as the model's own normals (or away from the body's axis)
                }
                faceNormal[i / 3] = n.normalized;
                for (int k = i; k < i + 3; k++)
                {
                    Vector3 sum;
                    smooth.TryGetValue(p[k], out sum);
                    smooth[p[k]] = sum + n;   // (n's length: twice the face's area)
                }
            }
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                Vector3 n = faceNormal[i / 3];
                if (n == Vector3.zero)
                {
                    continue;
                }
                if (wrap ? (side == DecalSide.Front ? n.z < 0f : n.z >= 0f) : n.z * sign < Facing)
                {
                    continue;   // only what faces the decal's side
                }
                poly.Clear();
                for (int k = i; k < i + 3; k++)
                {
                    Vector3 vnormal = smooth[p[k]].normalized;
                    if (Vector3.Dot(vnormal, n) < 0.3f)
                    {
                        vnormal = n;   // a corner shared with faces turned the other way (a knife edge): its own face's
                    }
                    Vector3 sp = toSprite.MultiplyPoint3x4(p[k]);
                    poly.Add(new V { p = p[k], n = vnormal, s = new Vector2(sp.x, sp.y) });
                }
                Clip(poly, next, 0, lo.x, true);
                Clip(poly, next, 0, lo.x + size.x, false);
                Clip(poly, next, 1, lo.y, true);
                Clip(poly, next, 1, lo.y + size.y, false);
                if (poly.Count < 3)
                {
                    continue;
                }
                int first = verts.Count;
                for (int k = 0; k < poly.Count; k++)
                {
                    V v = poly[k];
                    verts.Add(v.p + v.n.normalized * lift);
                    uvs.Add(new Vector2((v.s.x - lo.x) / size.x, (v.s.y - lo.y) / size.y));
                }
                // A fan, both windings (whatever the material culls, the decal shows from its side).
                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    tris.Add(first);
                    tris.Add(first + k);
                    tris.Add(first + k + 1);
                    tris.Add(first);
                    tris.Add(first + k + 1);
                    tris.Add(first + k);
                }
            }
            if (verts.Count == 0)
            {
                return null;
            }
            Mesh mesh = new Mesh();
            MeshGuard.FitIndices(mesh, verts.Count);
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            List<Color32> colours = new List<Color32>(verts.Count);
            List<Vector3> tags = new List<Vector3>(verts.Count);
            for (int i = 0; i < verts.Count; i++)
            {
                colours.Add(new Color32(255, 255, 255, 255));
                tags.Add(RoofClip.MovingTag);
            }
            mesh.SetColors(colours);
            mesh.SetUVs(1, tags);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            MeshGuard.Check(mesh, "apparel decal");
            return mesh;
        }

        // One edge of the clip rectangle (Sutherland-Hodgman): keeps what lies at or past edge on axis (0 x, 1 y) on the
        // side named (low: >= edge, else <= edge).
        private static void Clip(List<V> poly, List<V> into, int axis, float edge, bool low)
        {
            into.Clear();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                V a = poly[i], b = poly[(i + 1) % n];
                float da = (low ? 1f : -1f) * ((axis == 0 ? a.s.x : a.s.y) - edge);
                float db = (low ? 1f : -1f) * ((axis == 0 ? b.s.x : b.s.y) - edge);
                if (da >= 0f)
                {
                    into.Add(a);
                }
                if ((da >= 0f) != (db >= 0f))
                {
                    into.Add(V.Lerp(a, b, da / (da - db)));
                }
            }
            poly.Clear();
            poly.AddRange(into);
        }
    }
}
