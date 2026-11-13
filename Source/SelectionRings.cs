using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Selection rings: in the 3D view a selected pawn is marked by a flat circle on the ground round its body, in place of
    // the game's corner brackets (Patches.DrawSelectionBracketFor_Prefix; buildings and items keep IsoRim's brackets).
    //
    // Where and how big: the drawers note the pawn's posed body every frame while it is selected (People.Draw: the body and
    // head boxes through the figure's own body and head placing; Animals.Draw: the body model's box through its pose; Mechs.Draw:
    // the mech's box through its pose; Patches.RenderPawnAt_Prefix: a 2D card, standing or lying). The circle's centre is the
    // middle of that box on the ground, its radius the box's half diagonal on the ground plus a margin, never less than a
    // radius by body size (MinRadius * sqrt(BodySize): a standing person, a chicken). So it follows every pose the drawers know
    // (lying, downed, crawling, getting up, carried, crouched or face down by Api 45), worked out anew each frame. Height: the
    // ground under the pawn, a holder's surface (the drawers' own heights), in a bed the top of the bedding (Poses.FlatLyingHeight,
    // where a lying card lies: over the blanket, not hidden under it); never the lift of being carried or in mid-jump. A pawn no
    // drawer noted this frame or the last (a 2D sleeper the game does not redraw every frame, a pawn drawn flat) gets its ring
    // from where the game draws it, lying as long as its card along its bed.
    //
    // Colour: the game's name colour (PawnNameColorUtility.PawnNameColorOf, so other mods' name colours carry over), except
    // the colony's own (a light grey there) is green and a prisoner's pale orange is drawn more saturated in the same hue.
    //
    // Drawn with the game's Transparent shader (a texture tinted by the colour) after the game's lighting overlay (queue
    // 3110: unlit, readable at night), depth tested and not written: walls and roofs in front hide it as they hide the
    // pawn. The world matrix is in the pawn's own rows, so stacked floors move it with the pawn (Floors.Single_Prefix) and
    // the layout's stair offset noted with it applies. The ring texture is made here (a band of constant width on screen, as
    // IsoRim's other ground lines); other mods may give their own (Api 48, LookOf).
    internal static class SelectionRings
    {
        // Harness counters: rings drawn (since the last reset) and the pawns they were drawn for in the last frame drawn.
        internal static int Drawn;
        internal static int Hidden;
        internal static int FrameOf = -1, InFrame;   // rings drawn in frame FrameOf

        private const float MinRadius = 0.42f;      // radius (cells) round a standing figure of body size 1
        private const float Margin = 0.08f;         // between the body's box and the inside of the band
        private const float GroundLift = 0.02f;     // above the ground or bed surface (true cells), clear of it in depth
        private const int RingQueue = 3110;         // after the game's lighting overlay (3100), before the fog of war (3175)
        private const float CardFill = 0.85f;       // share of a 2D card's quad its figure fills, lying (for its length)

        internal static readonly Color ColonyGreen = new Color(0.30f, 0.90f, 0.30f);
        private static readonly Color VanillaColony = new Color(0.9f, 0.9f, 0.9f);   // PawnNameColorUtility.ColorColony

        private struct Footprint
        {
            public int frame;
            public float x, z;        // the middle of the body on the ground, world (the pawn's own rows)
            public float y;           // true height of the ground (bed surface) under it
            public float fit;         // half diagonal of the body's box on the ground
            public float top;         // true height of the posed body's top (a box drawer's note; 0 for a card)
            public float hx, hz, yaw; // the posed body's box on the ground: half extents in the figure's own x and z, its turn
            public Matrix4x4 extra;   // the layout's stair offset (DrawHook.floorMatrix) while it was drawn, or identity
        }

        private static readonly Dictionary<Pawn, Footprint> notes = new Dictionary<Pawn, Footprint>();
        private static int pruned;

        // ---- notes from the drawers

        private static bool Wanted(Pawn pawn)
        {
            if (pawn == null || FigureScope.Active || !View3D.Ready)
            {
                return false;
            }
            if (pawn == TargetBox.Figure)
            {
                return true;   // the target box's figure: its posed box (FigureBox)
            }
            Selector sel = Find.Selector;
            return sel != null && sel.NumSelected > 0 && sel.IsSelected(pawn);
        }

        // A drawer's posed body: the box lo..hi in the figure's own space (cells, before the turn by yaw and the move to at),
        // placed by local; at in world (rows) space, its y the true height local places from (the body's top: FigureBox);
        // ground: the true height the ring lies at.
        internal static void NoteBox(Pawn pawn, Vector3 at, float yaw, float ground, Matrix4x4 local, Vector3 lo, Vector3 hi)
        {
            if (!Wanted(pawn))
            {
                return;
            }
            Vector3 min = new Vector3(float.MaxValue, 0f, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Grow(ref min, ref max, local, lo, hi);
            Note(pawn, at, yaw, ground, min, max);
        }

        // People.Draw: the body and head boxes (FigureBoxes) through the figure's own body and head placing.
        internal static void NotePerson(Pawn pawn, People.Look look, Vector3 at, float yaw, float ground, Matrix4x4 bodyPlace, Matrix4x4 headPlace)
        {
            if (!Wanted(pawn) || look == null)
            {
                return;
            }
            Vector3 min = new Vector3(float.MaxValue, 0f, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            FigureBoxes.Box b = FigureBoxes.Body(look);
            Grow(ref min, ref max, bodyPlace, b.centre - b.size * 0.5f, b.centre + b.size * 0.5f);
            FigureBoxes.Box h = FigureBoxes.Head(look);
            Grow(ref min, ref max, headPlace, h.centre - h.size * 0.5f, h.centre + h.size * 0.5f);
            Note(pawn, at, yaw, ground, min, max);
        }

        // Patches.RenderPawnAt_Prefix: a pawn drawn as a 2D card (standing, or lying flat), centred at at; length: how long
        // its card lies (0 standing: the radius by body size).
        internal static void NoteCard(Pawn pawn, Vector3 at, float ground, float length)
        {
            if (!Wanted(pawn))
            {
                return;
            }
            Building_Bed bed = length > 0f ? pawn.CurrentBed() : null;
            if (bed != null)
            {
                at = AlongBed(bed, at);
            }
            Store(pawn, at.x, at.z, ground, length * CardFill * 0.5f, 0f, 0f, 0f, 0f);
        }

        // A 2D sleeper's body lies along its bed from the pillow (where the game draws it) toward the foot: the middle of the
        // bed's length in the sleeper's own row of the bed.
        private static Vector3 AlongBed(Building_Bed bed, Vector3 at)
        {
            Vector3 dir = bed.Rotation.FacingCell.ToVector3();
            Vector3 mid = bed.TrueCenter();
            float along = Vector3.Dot(new Vector3(mid.x - at.x, 0f, mid.z - at.z), dir);
            return new Vector3(at.x + dir.x * along, at.y, at.z + dir.z * along);
        }

        // A 2D card's length when lying: the larger side of its body graphic's quad.
        internal static float CardLength(Pawn pawn)
        {
            float len = 1.5f;
            try
            {
                if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
                {
                    float factor = pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.bodySizeFactor : 1f;
                    len = 1.5f * People.SizeFor(factor);
                }
                else if (pawn.ageTracker != null)
                {
                    PawnKindLifeStage stage = pawn.ageTracker.CurKindLifeStage;
                    if (stage != null && stage.bodyGraphicData != null)
                    {
                        len = Mathf.Max(stage.bodyGraphicData.drawSize.x, stage.bodyGraphicData.drawSize.y);
                    }
                }
            }
            catch (Exception)
            {
            }
            return len;
        }

        private static void Grow(ref Vector3 min, ref Vector3 max, Matrix4x4 m, Vector3 lo, Vector3 hi)
        {
            for (int k = 0; k < 8; k++)
            {
                Vector3 p = m.MultiplyPoint3x4(new Vector3((k & 1) == 0 ? lo.x : hi.x, (k & 2) == 0 ? lo.y : hi.y, (k & 4) == 0 ? lo.z : hi.z));
                min.x = Mathf.Min(min.x, p.x);
                min.z = Mathf.Min(min.z, p.z);
                max.x = Mathf.Max(max.x, p.x);
                max.z = Mathf.Max(max.z, p.z);
                max.y = Mathf.Max(max.y, p.y);   // the top (TargetBox)
            }
        }

        private static void Note(Pawn pawn, Vector3 at, float yaw, float ground, Vector3 min, Vector3 max)
        {
            if (min.x > max.x)
            {
                return;
            }
            Vector3 mid = Quaternion.AngleAxis(yaw, Vector3.up) * new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f);
            float dx = max.x - min.x, dz = max.z - min.z;
            Store(pawn, at.x + mid.x, at.z + mid.z, ground, 0.5f * Mathf.Sqrt(dx * dx + dz * dz), at.y + max.y, dx * 0.5f, dz * 0.5f, yaw);
        }

        private static void Store(Pawn pawn, float x, float z, float ground, float fit, float top, float hx, float hz, float yaw)
        {
            Building_Bed bed = pawn.CurrentBed();
            if (bed != null)
            {
                ground = Mathf.Max(ground, Poses.FlatLyingHeight(bed, pawn));   // on top of the bedding (as a lying card), not under the blanket
            }
            notes[pawn] = new Footprint
            {
                frame = Time.frameCount,
                x = x,
                z = z,
                y = ground,
                fit = fit,
                top = top,
                hx = hx,
                hz = hz,
                yaw = yaw,
                extra = DrawHook.floor ? DrawHook.floorMatrix : Matrix4x4.identity
            };
            Prune();
        }

        private static void Prune()
        {
            int now = Time.frameCount;
            if (now - pruned < 600 || notes.Count < 32)
            {
                return;
            }
            pruned = now;
            List<Pawn> stale = null;
            foreach (KeyValuePair<Pawn, Footprint> kv in notes)
            {
                if (now - kv.Value.frame > 2)
                {
                    (stale ?? (stale = new List<Pawn>())).Add(kv.Key);
                }
            }
            if (stale != null)
            {
                foreach (Pawn p in stale)
                {
                    notes.Remove(p);
                }
            }
        }

        // A pawn's body as a box drawer posed it this frame or the last: its rect on the ground (world rows, the posed box turned
        // by its yaw, axis aligned round it) and its top (true cells over the floor it stands on). False when not noted (not
        // drawn by a box drawer, a 2D card, or neither selected nor the target box's figure).
        internal static bool FigureBox(Pawn pawn, out Rect rect, out float top)
        {
            Footprint f;
            if (pawn == null || !notes.TryGetValue(pawn, out f) || Time.frameCount - f.frame > 2 || f.top <= 0f || f.hx <= 0f && f.hz <= 0f)
            {
                rect = default(Rect);
                top = 0f;
                return false;
            }
            float c = Mathf.Abs(Mathf.Cos(f.yaw * Mathf.Deg2Rad)), sn = Mathf.Abs(Mathf.Sin(f.yaw * Mathf.Deg2Rad));
            float ex = c * f.hx + sn * f.hz, ez = sn * f.hx + c * f.hz;
            rect = new Rect(f.x - ex, f.z - ez, 2f * ex, 2f * ez);
            top = f.top;
            return true;
        }

        // ---- the look: THE one place that decides a pawn's ring

        internal struct Ring
        {
            public bool draw;           // false: no ring (hidden by Api 48)
            public Color colour;
            public float radius;        // to the middle of the band (built-in ring) or half the quad (a mod's texture), cells
            public Texture2D texture;   // a mod's own ring picture (Api 48); null: the built-in ring
            public float x, z, y;       // centre (world rows) and true height
            public Matrix4x4 extra;     // the layout's stair offset
            public bool noted;          // from a drawer's note this frame or the last (else from the pawn's position and size)
        }

        // How the ring of this pawn looks now: where (the drawers' notes, or its position), how big (the fitted radius times
        // a rule's scale), what colour and picture (default, then an XML SelectionRing on its race, then code rules: Api 48).
        internal static Ring LookOf(Pawn pawn)
        {
            Ring r = default(Ring);
            r.draw = true;
            Footprint f;
            if (notes.TryGetValue(pawn, out f) && Time.frameCount - f.frame <= 2)
            {
                r.x = f.x;
                r.z = f.z;
                r.y = f.y;
                r.extra = f.extra;
                r.noted = true;
            }
            else
            {
                // Not drawn by a drawer that notes it this frame (a 2D sleeper the game does not redraw every frame, a pawn drawn
                // flat): from where the game draws it; lying, as long as its card, on the bedding of its bed.
                Thing at = pawn.SpawnedParentOrMe ?? pawn;
                Vector3 p = at.DrawPos;
                r.x = p.x;
                r.z = p.z;
                r.extra = Floors.ExtraMatrix(at);
                f.fit = 0f;
                if (pawn.Spawned && pawn.GetPosture() != PawnPosture.Standing)
                {
                    f.fit = CardLength(pawn) * CardFill * 0.5f;
                    Building_Bed bed = pawn.CurrentBed();
                    r.y = bed != null ? Mathf.Max(0f, Poses.FlatLyingHeight(bed, pawn)) : 0f;
                    if (bed != null)
                    {
                        Vector3 c = AlongBed(bed, p);
                        r.x = c.x;
                        r.z = c.z;
                    }
                }
            }
            float band = BandWidth;
            float min = MinRadius * Mathf.Sqrt(Mathf.Max(0.05f, pawn.BodySize));
            r.radius = Mathf.Max(f.fit + Margin, min) + band * 0.5f;
            r.colour = DefaultColour(pawn);

            SelectionRing xml = SelectionRingRules.XmlOf(pawn);
            SelectionRing code = SelectionRingRules.Ask(pawn);
            Apply(ref r, xml);
            Apply(ref r, code);
            return r;
        }

        private static void Apply(ref Ring r, SelectionRing look)
        {
            if (look == null)
            {
                return;
            }
            if (look.hide)
            {
                r.draw = false;
            }
            if (look.color.HasValue)
            {
                r.colour = look.color.Value;
            }
            if (look.radiusScale > 0f && look.radiusScale != 1f)
            {
                r.radius *= look.radiusScale;
            }
            Texture2D tex = look.Texture;
            if (tex != null)
            {
                r.texture = tex;
            }
        }

        // The game's name colour, the colony's as green and a prisoner's more saturated.
        internal static Color DefaultColour(Pawn pawn)
        {
            Color c;
            try
            {
                c = PawnNameColorUtility.PawnNameColorOf(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a pawn's name colour failed: " + e, 93619481);
                return ColonyGreen;
            }
            if (Mathf.Abs(c.r - VanillaColony.r) < 0.01f && Mathf.Abs(c.g - VanillaColony.g) < 0.01f && Mathf.Abs(c.b - VanillaColony.b) < 0.01f)
            {
                return ColonyGreen;
            }
            if (pawn.IsPrisoner && pawn.MentalStateDef == null)
            {
                float h, s, v;
                Color.RGBToHSV(c, out h, out s, out v);
                c = Color.HSVToRGB(h, Mathf.Max(s, 0.8f), v);
            }
            c.a = 1f;
            return c;
        }

        // ---- drawing

        // From Patches.DrawSelectionBracketFor_Prefix for a selected pawn (rings on, 3D view). True: handled (drawn or hidden).
        internal static bool Draw(Pawn pawn)
        {
            Ring r = LookOf(pawn);
            if (!r.draw || r.radius <= 0f)
            {
                Hidden++;
                return true;
            }
            float half;
            Texture2D tex;
            if (r.texture != null)
            {
                tex = r.texture;
                half = r.radius;
            }
            else
            {
                float band = BandWidth;
                half = r.radius + band * 0.5f;
                tex = RingTexture(band / half);
            }
            Material mat = MaterialFor(tex, r.colour);
            if (mat == null)
            {
                return false;
            }
            float s = View3D.Squash;
            Matrix4x4 m = r.extra * Matrix4x4.TRS(new Vector3(r.x, (r.y + GroundLift) / s, r.z), Quaternion.identity, new Vector3(half * 2f, 1f, half * 2f));
            Graphics.DrawMesh(MeshPool.plane10, m, mat, 0);
            Drawn++;
            if (FrameOf != Time.frameCount)
            {
                FrameOf = Time.frameCount;
                InFrame = 0;
            }
            InFrame++;
            return true;
        }

        // The band's width (cells): about seven pixels at any zoom (feathered), as IsoRim's other ground lines scale.
        internal static float BandWidth
        {
            get { return Mathf.Clamp(Interaction.LineWidth * 3.2f, 0.07f, 0.4f); }   // feathered: its solid middle about half of it
        }

        // ---- the built-in ring textures, one per share of the quad's half the band takes (in 1/128 steps)
        //
        // The soft look (user decision 2026-11-01): the band feathered on both edges (solid only in its middle, fading out to
        // nothing at its inner and outer edge), a faint glow of the same colour inside it fading to nothing at the centre (a
        // soft pool of light, not a frame), the whole at about 75% opacity so the ground shows through.
        private const float RingOpacity = 0.78f;   // the band's middle
        private const float GlowOpacity = 0.22f;   // the inner glow right inside the band (of the band's)
        private const float GlowPower = 2.2f;      // how fast the glow fades toward the centre

        private const int TexSize = 256;
        private static readonly Dictionary<int, Texture2D> rings = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<Texture2D, Dictionary<int, Material>> materials = new Dictionary<Texture2D, Dictionary<int, Material>>();

        private static Texture2D RingTexture(float share)
        {
            int key = Mathf.Clamp(Mathf.RoundToInt(share * 128f), 3, 96);
            Texture2D tex;
            if (rings.TryGetValue(key, out tex) && tex != null)
            {
                return tex;
            }
            float q = key / 128f;
            int n = TexSize;
            float px = 2f / n;              // one texel, in quad-half units
            float outer = 1f - px;          // a texel clear of the quad's edge
            float inner = outer - q;
            float mid = (outer + inner) * 0.5f;
            float half = q * 0.5f;
            Color32[] cols = new Color32[n * n];
            for (int yy = 0; yy < n; yy++)
            {
                for (int xx = 0; xx < n; xx++)
                {
                    float u = (xx + 0.5f) * px - 1f;
                    float v = (yy + 0.5f) * px - 1f;
                    float d = Mathf.Sqrt(u * u + v * v);
                    // the band: solid across its middle 40%, smoothly to nothing at both edges
                    float t = Mathf.Abs(d - mid) / Mathf.Max(half, px);
                    float band = 1f - Smooth(0.4f, 1f, t);
                    // the glow inside it: from GlowOpacity at the band's middle to nothing at the centre
                    float glow = d < mid ? GlowOpacity * Mathf.Pow(Mathf.Clamp01(d / mid), GlowPower) : 0f;
                    float a = RingOpacity * Mathf.Max(band, glow);
                    cols[yy * n + xx] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex = new Texture2D(n, n, TextureFormat.RGBA32, true, false);
            tex.name = "IsoRim_SelectionRing_" + key;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.SetPixels32(cols);
            tex.Apply(true);
            rings[key] = tex;
            return tex;
        }

        private static float Smooth(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        private static Material MaterialFor(Texture2D tex, Color colour)
        {
            if (tex == null)
            {
                return null;
            }
            Color32 c = colour;
            int key = c.r | c.g << 8 | c.b << 16 | c.a << 24;
            Dictionary<int, Material> byColour;
            if (!materials.TryGetValue(tex, out byColour))
            {
                if (materials.Count > 64)
                {
                    materials.Clear();
                }
                byColour = materials[tex] = new Dictionary<int, Material>();
            }
            Material m;
            if (byColour.TryGetValue(key, out m) && m != null)
            {
                return m;
            }
            Shader sh = ShaderDatabase.Transparent;
            if (sh == null)
            {
                return null;
            }
            m = new Material(sh);
            m.name = "IsoRim_SelectionRing";
            m.mainTexture = tex;
            m.color = colour;
            m.renderQueue = RingQueue;
            if (byColour.Count > 64)
            {
                byColour.Clear();
            }
            byColour[key] = m;
            return m;
        }
    }
}
