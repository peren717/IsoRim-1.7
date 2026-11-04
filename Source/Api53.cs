using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>
    /// A picture printed on one part of a thing's 3D model (Api version 53): a rectangle of the part as the viewer of that side
    /// sees it. Hand these out from a <see cref="ModelDecalProvider"/>. Painted into the part's own texture when the model is
    /// built, so it is lit, clipped and faded as the model is and costs nothing per frame.
    /// </summary>
    public sealed class ModelDecal
    {
        /// <summary>The picture, tinted by <see cref="colour"/>, or by both colours through <see cref="mask"/> as the game's
        /// CutoutComplex does. Null with a mask: a plain two-colour picture, only where the mask is red or green.</summary>
        public Texture2D texture;

        /// <summary>The game's mask: red takes <see cref="colour"/>, green <see cref="colourTwo"/>, black neither.</summary>
        public Texture2D mask;

        public Color colour = Color.white;

        public Color colourTwo = Color.white;

        /// <summary>Front: the faces the model turns to its south (and its sides, top and bottom that lean that way, or not at all
        /// either way). Back: those turned to its north, seen from behind.</summary>
        public DecalSide side = DecalSide.Front;

        /// <summary>The picture's middle, in model units: x to the right as the viewer of that side sees it (0 the model's
        /// origin), y the height over the model's base. A front and a back decal with the same numbers read the same.</summary>
        public Vector2 centre = Vector2.zero;

        /// <summary>The picture's size in model units.</summary>
        public Vector2 size = Vector2.one;

        /// <summary>The part of the texture and mask used, in texture coordinates (0 to 1, y up). Default: all of it.</summary>
        public Rect source = new Rect(0f, 0f, 1f, 1f);

        /// <summary>With a mask and no texture: the two colours cover the whole rectangle, what the mask leaves black taking the
        /// nearest of them, for a pattern whose outline is not the part's. Default: black leaves the part as it is.</summary>
        public bool spread;
    }

    /// <summary>
    /// Gives the decals of one thing's model part (Api version 53; <see cref="Api.AddModelDecals"/>): add zero or more to decals (a
    /// list IsoRim clears and reuses). Asked when the model is built, not every frame. Painted in the order added, each over the
    /// one before. None: the part stays as the model has it.
    /// </summary>
    public delegate void ModelDecalProvider(Thing thing, List<ModelDecal> decals);

    public static partial class Api
    {
        /// <summary>(Version 53) Registers a provider for the part of that def's model named by its OBJ material. Twice does nothing.
        /// Call at startup. Throws on a null argument.</summary>
        public static void AddModelDecals(ThingDef def, string part, ModelDecalProvider provider) => ModelDecals.Add(def, part, provider);

        /// <summary>(Version 53) Removes it. Things already built keep their look until they are next built.</summary>
        public static void RemoveModelDecals(ThingDef def, string part, ModelDecalProvider provider) => ModelDecals.Remove(def, part, provider);

        /// <summary>(Version 53) The thing's decals have changed: its model is built again soon, and the providers asked. Does nothing
        /// for things no provider is registered for.</summary>
        public static void RefreshModelDecals(Thing thing) => ModelDecals.Refresh(thing);

        public static partial class Dev
        {
            // (Api 53) For tests: the looks held, painted so far, taken over from a look nothing used, and what is switched off.
            public static string ModelDecalReport() => ModelDecals.Report();

            // (Api 53) For tests: the texture of the look a thing's part was last built with, or null.
            public static Texture2D ModelDecalTexture(Thing thing) => ModelDecals.TextureOf(thing);
        }
    }

    // Model decals (Api 53). ModelBaker.Write asks For for each model it writes; with no provider for the thing's def that is one
    // dictionary miss per model written, and with none registered at all it is not called. A provider's decals are painted on the
    // CPU, once per different look, into a small texture with a half for each side, and the part's faces are written with it in
    // place of their plain ones (lit, roof clipped, faded, tagged as the rest of the model; no plain copy under them, which would
    // show its colour at their edges), their coordinates projected from their own positions, so the OBJ needs none. Works with
    // textured models off.
    //
    // Looks are few (one per heraldry, sign, ...), but a player clicking through colours makes a new one each click, and a section
    // keeps a piece of mesh per material it ever drew. So there is a fixed number of looks, each with one material for good: a look
    // nothing is built with any more is painted over (its texture rewritten, the material kept) when a new one needs its place, the
    // one unused longest first. With every look in use, the new one stays plain until one is free.
    internal static class ModelDecals
    {
        private sealed class Reg
        {
            public ThingDef def;
            public string part;
            public ModelDecalProvider provider;
            public bool off, found;
        }

        // One painted look: what the part's faces are written with.
        internal sealed class Look
        {
            public string key;
            public Texture2D texture;
            public Material material;
            public int stamp, users;
            public float minX, maxX, minY, height1;   // the part's box, model units (height1: 1 / its height)
            public float frontU, backU, halfU;        // where each side's half starts in the texture, and its width
        }

        private struct Holder
        {
            public Thing thing;
            public Look look;
        }

        internal const int MaxLooks = 64;
        private const float PixelsPerUnit = 192f;
        private const int MinSide = 16, MaxSide = 512, Gap = 4;

        private static readonly Dictionary<ThingDef, Reg[]> byDef = new Dictionary<ThingDef, Reg[]>();
        private static readonly Dictionary<string, Look> byKey = new Dictionary<string, Look>();
        private static readonly List<Look> looks = new List<Look>();
        private static readonly Dictionary<long, Holder> held = new Dictionary<long, Holder>();
        private static readonly Dictionary<long, Vector4> boxes = new Dictionary<long, Vector4>();
        private static readonly List<ModelDecal> scratch = new List<ModelDecal>();
        private static readonly List<long> gone = new List<long>();
        private static Look[] forModel = new Look[8];
        private static int stamp, painted, retaken, switchedOff;
        private static bool fullWarned;

        // Registrations on (ModelBaker.Write skips everything here at 0).
        internal static int Count;

        internal static void Add(ThingDef def, string part, ModelDecalProvider provider)
        {
            if (def == null || part.NullOrEmpty() || provider == null)
            {
                throw new ArgumentNullException(def == null ? nameof(def) : provider == null ? nameof(provider) : nameof(part));
            }
            byDef.TryGetValue(def, out Reg[] list);
            list = list ?? new Reg[0];
            foreach (Reg r in list)
            {
                if (r.provider == provider && r.part == part)
                {
                    return;
                }
            }
            Reg[] next = new Reg[list.Length + 1];
            list.CopyTo(next, 0);
            next[list.Length] = new Reg { def = def, part = part, provider = provider };
            byDef[def] = next;
            Count++;
        }

        internal static void Remove(ThingDef def, string part, ModelDecalProvider provider)
        {
            if (def == null || !byDef.TryGetValue(def, out Reg[] list))
            {
                return;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].provider == provider && list[i].part == part)
                {
                    Reg[] next = new Reg[list.Length - 1];
                    Array.Copy(list, 0, next, 0, i);
                    Array.Copy(list, i + 1, next, i, list.Length - i - 1);
                    if (next.Length == 0)
                    {
                        byDef.Remove(def);
                    }
                    else
                    {
                        byDef[def] = next;
                    }
                    if (!list[i].off)
                    {
                        Count--;
                    }
                    return;
                }
            }
        }

        internal static void Refresh(Thing t)
        {
            if (t == null || !t.Spawned || !byDef.ContainsKey(t.def) || !View3D.Ready)
            {
                return;
            }
            if (t.def.drawerType == DrawerType.RealtimeOnly)
            {
                MapSections.QueueAround(t.Map, t.Position, 0, typeof(SectionLayer_IsoRimBodies), true);
            }
            else
            {
                t.DirtyMapMesh(t.Map);
            }
        }

        // One warning, the registration off; what it gave stays plain.
        private static void SwitchOff(Reg r, string why)
        {
            if (!r.off)
            {
                r.off = true;
                Count--;
                switchedOff++;
                Log.Warning("[IsoRim] Model decals of " + r.def.defName + " (" + r.part + ", " + (r.provider.Method.DeclaringType != null ? r.provider.Method.DeclaringType.FullName : "?")
                    + ") are off: " + why);
            }
        }

        // The looks of a model being written for a thing, by material index (null entries: plain); null when it has none. The
        // array is reused: good until the next call.
        internal static Look[] For(Thing thing, Model m)
        {
            if (thing == null || m.materials == null || m.material == null || !byDef.TryGetValue(thing.def, out Reg[] regs))
            {
                return null;
            }
            if (forModel.Length < m.materials.Length)
            {
                forModel = new Look[m.materials.Length * 2];
            }
            Array.Clear(forModel, 0, forModel.Length);
            bool any = false;
            for (int mi = 0; mi < m.materials.Length; mi++)
            {
                scratch.Clear();
                Reg first = null;
                string name = m.materials[mi].name;
                for (int i = 0; i < regs.Length; i++)
                {
                    if (regs[i].off || !string.Equals(regs[i].part, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    first = first ?? regs[i];
                    Ask(regs[i], thing);
                }
                long holder = ((long)thing.thingIDNumber << 32) | (uint)(m.id * 64 + mi);
                Look look = scratch.Count > 0 ? LookFor(first, m, mi, thing) : null;
                Hold(holder, thing, look);
                forModel[mi] = look;
                any |= look != null;
            }
            for (int i = 0; i < regs.Length; i++)
            {
                if (!regs[i].off && !regs[i].found && !HasPart(m, regs[i].part))
                {
                    // Not on this model: a thing's model can be written in parts, so only off if no model of the def has it.
                    ModelEntry entry = Forms.For(thing.def).model;
                    bool anywhere = false;
                    for (int v = 0; entry != null && entry.variants != null && v < entry.variants.Length && !anywhere; v++)
                    {
                        anywhere = entry.variants[v] != null && entry.variants[v].materials != null && HasPart(entry.variants[v], regs[i].part);
                    }
                    if (!anywhere)
                    {
                        SwitchOff(regs[i], "the thing's model has no such part");
                    }
                }
                else if (!regs[i].off)
                {
                    regs[i].found = true;
                }
            }
            scratch.Clear();
            return any ? forModel : null;
        }

        private static bool HasPart(Model m, string part)
        {
            for (int i = 0; i < m.materials.Length; i++)
            {
                if (string.Equals(m.materials[i].name, part, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Ask(Reg r, Thing thing)
        {
            int before = scratch.Count;
            try
            {
                r.provider(thing, scratch);
                for (int i = before; i < scratch.Count; i++)
                {
                    string bad = Bad(scratch[i]);
                    if (bad != null)
                    {
                        scratch.RemoveRange(before, scratch.Count - before);
                        SwitchOff(r, bad);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                scratch.RemoveRange(before, scratch.Count - before);
                SwitchOff(r, "it failed: " + e);
            }
        }

        private static string Bad(ModelDecal d)
        {
            if (d == null)
            {
                return "a null decal";
            }
            if (d.texture == null && d.mask == null)
            {
                return "a decal with neither texture nor mask";
            }
            if (!(d.size.x > 0f) || !(d.size.y > 0f) || float.IsInfinity(d.size.x) || float.IsInfinity(d.size.y) || float.IsNaN(d.centre.x) || float.IsNaN(d.centre.y)
                || float.IsInfinity(d.centre.x) || float.IsInfinity(d.centre.y))
            {
                return "a decal with a bad place or size";
            }
            if (!(d.source.width > 0f) || !(d.source.height > 0f) || d.source.xMin < 0f || d.source.yMin < 0f || d.source.xMax > 1f || d.source.yMax > 1f)
            {
                return "a decal with a bad source rectangle";
            }
            return null;
        }

        private static void Hold(long holder, Thing thing, Look look)
        {
            if (held.TryGetValue(holder, out Holder h))
            {
                if (h.look == look)
                {
                    return;
                }
                h.look.users--;
                held.Remove(holder);
            }
            if (look != null)
            {
                look.users++;
                held[holder] = new Holder { thing = thing, look = look };
            }
        }

        // The look for the decals in scratch, painted if it is a new one.
        private static Look LookFor(Reg reg, Model m, int mi, Thing thing)
        {
            Vector4 box = BoxOf(m, mi);
            int w = Mathf.Clamp(Mathf.CeilToInt((box.y - box.x) * PixelsPerUnit), MinSide, MaxSide);
            int h = Mathf.Clamp(Mathf.CeilToInt((box.w - box.z) * PixelsPerUnit), MinSide, MaxSide);
            ModelMaterial mm = m.materials[mi];
            Color plain = mm.color;
            if (mm.tinted)
            {
                plain *= thing.DrawColor;
            }
            string key = KeyOf(m, mi, plain);
            stamp++;
            if (byKey.TryGetValue(key, out Look look))
            {
                look.stamp = stamp;
                return look;
            }
            look = Free();
            if (look == null)
            {
                if (!fullWarned)
                {
                    fullWarned = true;
                    Log.Warning("[IsoRim] All " + MaxLooks + " model decal looks are in use: further ones stay plain until one is free.");
                }
                return null;
            }
            Color32[] px = Paint(w, h, box, plain, out string unreadable);
            if (px == null)
            {
                SwitchOff(reg, "cannot read the texture " + unreadable);
                return null;
            }
            int aw = w * 2 + Gap;
            if (look.texture == null || look.texture.width != aw || look.texture.height != h)
            {
                if (look.texture != null)
                {
                    UnityEngine.Object.Destroy(look.texture);
                }
                look.texture = new Texture2D(aw, h, TextureFormat.RGBA32, true, false);
                look.texture.name = "IsoRim_ModelDecal_" + looks.IndexOf(look);
                look.texture.wrapMode = TextureWrapMode.Clamp;
                look.texture.filterMode = FilterMode.Bilinear;
                if (look.material != null)
                {
                    look.material.mainTexture = look.texture;
                }
            }
            look.texture.SetPixels32(px);
            look.texture.Apply(true);
            if (look.material == null)
            {
                look.material = R3DMaterials.LitTexture(look.texture, look.texture.name, Color.white, 1);
            }
            if (look.material == null)
            {
                return null;
            }
            if (look.key != null)
            {
                byKey.Remove(look.key);
            }
            look.key = key;
            look.stamp = stamp;
            look.minX = box.x;
            look.maxX = box.y;
            look.minY = box.z;
            look.height1 = 1f / Mathf.Max(0.0001f, box.w - box.z);
            look.frontU = 0f;
            look.halfU = (float)w / aw;
            look.backU = (float)(w + Gap) / aw;
            byKey[key] = look;
            painted++;
            return look;
        }

        // A look to paint: a new one while there are fewer than MaxLooks, else the one unused longest of those nothing is built with.
        private static Look Free()
        {
            if (looks.Count < MaxLooks)
            {
                Look fresh = new Look();
                looks.Add(fresh);
                return fresh;
            }
            Look best = Oldest();
            if (best == null)
            {
                Sweep();
                best = Oldest();
            }
            if (best != null)
            {
                retaken++;
            }
            return best;
        }

        private static Look Oldest()
        {
            Look best = null;
            for (int i = 0; i < looks.Count; i++)
            {
                if (looks[i].users <= 0 && (best == null || looks[i].stamp < best.stamp))
                {
                    best = looks[i];
                }
            }
            return best;
        }

        // Things gone (or on a map gone) never build again: what they held is free.
        private static void Sweep()
        {
            gone.Clear();
            foreach (KeyValuePair<long, Holder> e in held)
            {
                if (!e.Value.thing.Spawned || e.Value.thing.Map == null || !Find.Maps.Contains(e.Value.thing.Map))
                {
                    gone.Add(e.Key);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                held[gone[i]].look.users--;
                held.Remove(gone[i]);
            }
        }

        // The part's box in the model: x min, x max, y min, y max.
        private static Vector4 BoxOf(Model m, int mi)
        {
            long k = ((long)m.id << 16) | (uint)mi;
            if (boxes.TryGetValue(k, out Vector4 b))
            {
                return b;
            }
            b = new Vector4(float.MaxValue, float.MinValue, float.MaxValue, float.MinValue);
            for (int i = 0; i < m.positions.Length; i++)
            {
                if (m.material[i] == mi)
                {
                    Vector3 p = m.positions[i];
                    b.x = Mathf.Min(b.x, p.x);
                    b.y = Mathf.Max(b.y, p.x);
                    b.z = Mathf.Min(b.z, p.y);
                    b.w = Mathf.Max(b.w, p.y);
                }
            }
            if (b.x > b.y)
            {
                b = new Vector4(0f, 1f, 0f, 1f);
            }
            boxes[k] = b;
            return b;
        }

        private static string KeyOf(Model m, int mi, Color plain)
        {
            StringBuilder s = new StringBuilder();
            s.Append(m.id).Append('/').Append(mi).Append('/').Append(((Color32)plain).GetHashCode());
            for (int i = 0; i < scratch.Count; i++)
            {
                ModelDecal d = scratch[i];
                s.Append('|').Append(d.texture != null ? d.texture.GetInstanceID() : 0).Append(',').Append(d.mask != null ? d.mask.GetInstanceID() : 0).Append(',').Append(d.side == DecalSide.Front ? 'F' : 'B');
                Num(s, d.colour.r); Num(s, d.colour.g); Num(s, d.colour.b); Num(s, d.colour.a);
                Num(s, d.colourTwo.r); Num(s, d.colourTwo.g); Num(s, d.colourTwo.b); Num(s, d.colourTwo.a);
                Num(s, d.centre.x); Num(s, d.centre.y); Num(s, d.size.x); Num(s, d.size.y);
                Num(s, d.source.x); Num(s, d.source.y); Num(s, d.source.width); Num(s, d.source.height);
                s.Append(d.spread ? 'S' : '-');
            }
            return s.ToString();
        }

        private static void Num(StringBuilder s, float f)
        {
            s.Append(',').Append(f.ToString("F4", CultureInfo.InvariantCulture));
        }

        // ---- painting

        private struct Source
        {
            public Color32[] px;
            public int w, h;
        }

        // The texture of the decals in scratch: the front half, a gap, the back half, each the part's box at w by h, starting from
        // the part's plain colour. Null (and what could not be read) if a picture's pixels cannot be read.
        private static Color32[] Paint(int w, int h, Vector4 box, Color plain, out string unreadable)
        {
            unreadable = null;
            int aw = w * 2 + Gap;
            Color[] canvas = new Color[aw * h];
            Color opaque = new Color(plain.r, plain.g, plain.b, 1f);
            for (int i = 0; i < canvas.Length; i++)
            {
                canvas[i] = opaque;
            }
            float bw = box.y - box.x, bh = box.w - box.z;
            for (int i = 0; i < scratch.Count; i++)
            {
                ModelDecal d = scratch[i];
                Source tex = default(Source), mask = default(Source);
                if (d.texture != null && !Read(d.texture, out tex))
                {
                    unreadable = d.texture.name;
                    return null;
                }
                if (d.mask != null && !Read(d.mask, out mask))
                {
                    unreadable = d.mask.name;
                    return null;
                }
                bool back = d.side == DecalSide.Back;
                int half = back ? w + Gap : 0;
                // The viewer of the back has the model's west on the right: its x runs the other way, from the box's far side.
                float left = back ? -box.y : box.x;
                float x0 = d.centre.x - d.size.x * 0.5f, y0 = d.centre.y - d.size.y * 0.5f;
                if (d.spread && d.mask != null && d.texture == null)
                {
                    Spread(canvas, aw, half, w, h, box, left, bw, bh, d, mask);
                    continue;
                }
                for (int y = 0; y < h; y++)
                {
                    float ty = (box.z + (y + 0.5f) / h * bh - y0) / d.size.y;
                    if (ty < 0f || ty > 1f)
                    {
                        continue;
                    }
                    float v = d.source.y + ty * d.source.height;
                    for (int x = 0; x < w; x++)
                    {
                        float tx = (left + (x + 0.5f) / w * bw - x0) / d.size.x;
                        if (tx < 0f || tx > 1f)
                        {
                            continue;
                        }
                        float u = d.source.x + tx * d.source.width;
                        Color c = PixelOf(d, tex, mask, u, v);
                        int at = y * aw + half + x;
                        canvas[at] = Color.Lerp(canvas[at], new Color(c.r, c.g, c.b, 1f), c.a);
                    }
                }
            }
            Color32[] px = new Color32[canvas.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < aw; x++)
                {
                    // The gap takes each half's edge, so small mip levels do not mix the sides.
                    int from = x < w ? x : x < w + Gap / 2 ? w - 1 : x < w + Gap ? w + Gap : x;
                    px[y * aw + x] = canvas[y * aw + from];
                }
            }
            return px;
        }

        // A spreading decal: its solid red and green texels, then each of the others in its rectangle takes the nearest of them.
        private static void Spread(Color[] canvas, int aw, int half, int w, int h, Vector4 box, float left, float bw, float bh, ModelDecal d, Source mask)
        {
            float x0 = d.centre.x - d.size.x * 0.5f, y0 = d.centre.y - d.size.y * 0.5f;
            bool[] inside = new bool[w * h], done = new bool[w * h];
            Queue<int> open = new Queue<int>();
            for (int y = 0; y < h; y++)
            {
                float ty = (box.z + (y + 0.5f) / h * bh - y0) / d.size.y;
                for (int x = 0; x < w; x++)
                {
                    float tx = (left + (x + 0.5f) / w * bw - x0) / d.size.x;
                    if (tx < 0f || tx > 1f || ty < 0f || ty > 1f)
                    {
                        continue;
                    }
                    inside[y * w + x] = true;
                    Color k = Bilinear(mask, d.source.x + tx * d.source.width, d.source.y + ty * d.source.height);
                    if (k.a > 0f && k.r + k.g > 0.9f)
                    {
                        Color c = Color.Lerp(d.colourTwo, d.colour, k.r / (k.r + k.g));
                        canvas[y * aw + half + x] = new Color(c.r, c.g, c.b, 1f);
                        done[y * w + x] = true;
                        open.Enqueue(y * w + x);
                    }
                }
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue(), x = i % w, y = i / w;
                Color c = canvas[y * aw + half + x];
                for (int n = 0; n < 4; n++)
                {
                    int nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0), ny = y + (n == 2 ? 1 : n == 3 ? -1 : 0);
                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && inside[ny * w + nx] && !done[ny * w + nx])
                    {
                        done[ny * w + nx] = true;
                        canvas[ny * aw + half + nx] = c;
                        open.Enqueue(ny * w + nx);
                    }
                }
            }
        }

        // A decal's colour and cover at a point of its picture: the game's Cutout, or CutoutComplex with a mask, on the CPU.
        private static Color PixelOf(ModelDecal d, Source tex, Source mask, float u, float v)
        {
            Color c = tex.px != null ? Bilinear(tex, u, v) : Color.white;
            if (mask.px == null)
            {
                return new Color(c.r * d.colour.r, c.g * d.colour.g, c.b * d.colour.b, c.a * d.colour.a);
            }
            Color k = Bilinear(mask, u, v);
            float gate = k.a > 0f ? 1f : 0f;   // the game's MaskGate
            float r = k.r * gate, g = k.g * gate;
            float mr = (r * d.colour.r + 1f - r) * (g * d.colourTwo.r + 1f - g);
            float mg = (r * d.colour.g + 1f - r) * (g * d.colourTwo.g + 1f - g);
            float mb = (r * d.colour.b + 1f - r) * (g * d.colourTwo.b + 1f - g);
            float cover = tex.px != null ? c.a : Mathf.Clamp01(r + g);
            return new Color(c.r * mr, c.g * mg, c.b * mb, cover);
        }

        private static bool Read(Texture2D t, out Source s)
        {
            s = default(Source);
            s.px = SpritePixels.Full(t, out s.w, out s.h);
            return s.px != null && s.w > 0 && s.h > 0;
        }

        private static Color Bilinear(Source s, float u, float v)
        {
            float x = u * s.w - 0.5f, y = v * s.h - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            int x1 = Mathf.Clamp(x0 + 1, 0, s.w - 1), y1 = Mathf.Clamp(y0 + 1, 0, s.h - 1);
            x0 = Mathf.Clamp(x0, 0, s.w - 1);
            y0 = Mathf.Clamp(y0, 0, s.h - 1);
            Color a = Color.Lerp(s.px[y0 * s.w + x0], s.px[y0 * s.w + x1], fx);
            Color b = Color.Lerp(s.px[y1 * s.w + x0], s.px[y1 * s.w + x1], fx);
            return Color.Lerp(a, b, fy);
        }

        // ---- writing

        // Where a corner of a part's face lands in its look's texture: Front for a face turned south or neither way, Back for one
        // turned north (normal: the face's, in the model's own space).
        internal static Vector2 Uv(Look l, Vector3 p, Vector3 normal)
        {
            bool back = normal.z > 0f;
            float t = (back ? l.maxX - p.x : p.x - l.minX) / Mathf.Max(0.0001f, l.maxX - l.minX);
            return new Vector2((back ? l.backU : l.frontU) + Mathf.Clamp01(t) * l.halfU, Mathf.Clamp01((p.y - l.minY) * l.height1));
        }

        // ---- tests

        internal static string Report()
        {
            int used = 0;
            foreach (Look l in looks)
            {
                used += l.users > 0 ? 1 : 0;
            }
            return "registrations " + Count + ", looks " + looks.Count + " of " + MaxLooks + ", in use " + used + ", painted " + painted + ", taken over " + retaken + ", switched off " + switchedOff
                + ", holders " + held.Count;
        }

        internal static Texture2D TextureOf(Thing t)
        {
            foreach (KeyValuePair<long, Holder> e in held)
            {
                if (e.Value.thing == t)
                {
                    return e.Value.look.texture;
                }
            }
            return null;
        }
    }
}
