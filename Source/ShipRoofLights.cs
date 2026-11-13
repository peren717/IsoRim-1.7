using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Ship roof lights (option shipRoofLights, default on; ShipRoofs in Gravship.cs): the gravship deck's circuit-board look.
    //   - The deck's top faces (the plates of roof_square and roof_triangle) take one repeating texture, ShipDeck: plates,
    //     rib gaps, dark glass panels, vents, light-dot plates and pale cyan trace inlays, TileCells x TileCells cells per
    //     copy, laid by world position like the pitched roofs' courses (SectionLayer_IsoRimRoofs.RoofUV). Each block of
    //     TileCells x TileCells cells shows the copy turned or mirrored by a fixed order (Orientation: neighbouring blocks
    //     always differ), so the pattern does not read as a grid. It is lit as the plain deck was (sky colour only, the sun
    //     on the faces: _GlowMode 2, _LightMode 3); the deck's rims, sides and gaps keep their plain colours.
    //   - The same faces again with ShipDeckGlow (traces, nodes and dots in cyan on black), added to what is there (IsoRimAddTex)
    //     and neither darkened by the sky nor by the light pass: they glow at night and in space and add a little by day
    //     (Frame: the strength follows the sky colour).
    //   - A thin glowing strip along the top of the rims round the hull outline (GlowColour), mitred where the outline
    //     turns, diagonal corners too.
    // All of it is built with the deck in SectionLayer_IsoRimRoofs, so it follows "show roofs", the roof opened under the
    // pointer or over colonists (shader cut and the old cut-away), the ship's flight and the roof clip (which it does not
    // change). Off, or a texture missing: the plain deck as before. The tiles' generator: tools\runners\shiproof\tile.py
    // (Blender, seed 23) outside this repository.
    [StaticConstructorOnStartup]
    internal static class ShipRoofLights
    {
        // ---- the tile and its order (the one place for them)
        public const int TileCells = 8;            // one copy of the texture covers this many cells a side
        private const int Orientations = 8;        // 4 quarter turns, each also mirrored
        private const int OrderX = 1, OrderZ = 3;  // block (bx, bz) shows orientation (OrderX * bx + OrderZ * bz) mod Orientations
        private const string DeckPath = "IsoRim/Roofs/ShipDeck";
        private const string GlowPath = "IsoRim/Roofs/ShipDeckGlow";

        // ---- the light
        public static readonly Color GlowColour = new Color(0.25f, 0.85f, 1.0f);   // the traces' colour (tile.py GLOW), the rim strip's
        // The plates' tone as stored in ShipDeck.png (tile.py PLATE, written as sRGB). The deck texture is tinted so its plates
        // read in the ship kit's plate colour (ShipRoofs.PlateColour, the rims' top), as the user asked (2026-11-13: the raw
        // texture read near white).
        private static readonly Color TexturePlate = new Color(212f / 255f, 216f / 255f, 214f / 255f);

        private static Color DeckTint
        {
            get
            {
                Color want = ShipRoofs.PlateColour;
                return new Color(want.r / TexturePlate.r, want.g / TexturePlate.g, want.b / TexturePlate.b, 1f);
            }
        }
        // The glow texture times this, added: full in the dark, a little in daylight (Frame: by the brightness of the sky
        // colour the deck is lit with, between SkyDark and SkyDay).
        private const float GlowNight = 1.0f;
        private const float GlowDay = 0.3f;
        private const float SkyDark = 0.7f;        // the sky colour's brightest channel at which the glow is full (the game's night sky: about 0.48, 0.60, 0.68)
        private const float SkyDay = 1.0f;         // ... and at which it is GlowDay
        private const float StripStrength = 1.0f;  // the rim strip's GlowColour times this, added
        public const float StripHalf = 0.022f;     // half the width of the rim strip, in cells (tile.py: 0.044 wide)
        public const float Lift = 0.002f;          // glowing faces this far over what they lie on, in cells
        private const int DeckQueue = 3601;        // after the plain deck (3600), as the pitched roofs' courses
        private const int GlowQueue = 3602;        // after the deck it lies on (additive, no depth write)
        private const string AddTexDef = "IsoRimAddTex";   // textured additive (Defs/ShaderDefs/IsoRim_Shaders.xml); none: no glow layer

        private static Material deck, glow, strip;
        private static bool made;

        // Whether the deck is drawn with the lights now: the option, and the deck texture found.
        public static bool On
        {
            get { return IsoRimMod.Settings.shipRoofLights && DeckMaterial != null; }
        }

        // The deck's textured top: the roof's light (sky only, the sun on the faces).
        public static Material DeckMaterial
        {
            get
            {
                Make();
                return deck;
            }
        }

        // The glow over the deck (null without the textured additive shader def or the glow texture).
        public static Material GlowMaterial
        {
            get
            {
                Make();
                return glow;
            }
        }

        // The rim strip: unlit, added (the beams' def, IsoRimAdd), vertex colour GlowColour * StripStrength.
        public static Material StripMaterial
        {
            get
            {
                Make();
                return strip;
            }
        }

        public static Color32 StripColour
        {
            get { return R3DMaterials.Shaded(GlowColour, StripStrength); }
        }

        private static void Make()
        {
            if (made || R3DMaterials.Solid == null)
            {
                return;
            }
            made = true;
            Texture2D deckTex = R3DTextures.Get(DeckPath);
            if (deckTex == null)
            {
                return;
            }
            deck = R3DMaterials.Make(R3DMaterials.SolidTexDef, "IsoRim_ShipDeck", DeckQueue, R3DMaterials.LightRoof);
            if (deck != null)
            {
                deck.mainTexture = deckTex;
                deck.color = DeckTint;
                IsoGlow.Use(deck, IsoGlow.SkyOnly);
            }
            strip = R3DMaterials.Make(Beams.AddDef, "IsoRim_ShipRimLight", GlowQueue);
            Texture2D glowTex = DefDatabase<ShaderDef>.GetNamedSilentFail(AddTexDef) != null ? R3DTextures.Get(GlowPath) : null;
            if (glowTex != null)
            {
                glow = R3DMaterials.Make(AddTexDef, "IsoRim_ShipDeckGlow", GlowQueue);
                if (glow != null)
                {
                    glow.mainTexture = glowTex;
                    SetGlow(GlowNight);
                }
            }
        }

        private static float glowNow = -1f;

        // Every 3D frame, with the sky colour the roofs are lit with (IsoGlow.BeginFrame; white with the lighting fix off): the
        // glow's strength for that light.
        public static void Frame(Color sky)
        {
            if (glow == null)
            {
                return;
            }
            float t = Mathf.InverseLerp(SkyDark, SkyDay, Mathf.Max(sky.r, Mathf.Max(sky.g, sky.b)));
            SetGlow(Mathf.Lerp(GlowNight, GlowDay, t));
        }

        private static void SetGlow(float strength)
        {
            if (Mathf.Abs(strength - glowNow) < 0.002f)
            {
                return;
            }
            glowNow = strength;
            glow.color = new Color(strength, strength, strength, 1f);
        }

        // The orientation of the tile in block (bx, bz): 0-3 quarter turns, 4-7 the same mirrored.
        public static int Orientation(int bx, int bz)
        {
            int k = (OrderX * bx + OrderZ * bz) % Orientations;
            return k < 0 ? k + Orientations : k;
        }

        // The texture coordinates of the point (x, z) (cells) of a deck face in cell (cellX, cellZ): its place in the
        // cell's block, the block's tile turned and mirrored by Orientation (mirror u first, then the quarter turns).
        public static Vector2 UV(int cellX, int cellZ, float x, float z)
        {
            int bx = FloorDiv(cellX), bz = FloorDiv(cellZ);
            float u = (x - bx * TileCells) / TileCells;
            float v = (z - bz * TileCells) / TileCells;
            int k = Orientation(bx, bz);
            if (k >= Orientations / 2)
            {
                u = 1f - u;
            }
            for (int i = 0; i < k % 4; i++)
            {
                float t = u;
                u = 1f - v;
                v = t;
            }
            return new Vector2(u, v);
        }

        private static int FloorDiv(int c)
        {
            return c >= 0 ? c / TileCells : -((-c + TileCells - 1) / TileCells);
        }

        // Which triangles of a deck piece's model are its top (all three corners at the model's highest point): those take
        // the texture. One flag per corner, the model's triangles in order.
        private static readonly Dictionary<Model, bool[]> tops = new Dictionary<Model, bool[]>();

        public static bool[] TopFaces(Model m)
        {
            bool[] top;
            if (tops.TryGetValue(m, out top))
            {
                return top;
            }
            Vector3[] p = m.positions;
            float high = float.MinValue;
            for (int i = 0; i < p.Length; i++)
            {
                high = Mathf.Max(high, p[i].y);
            }
            top = new bool[p.Length];
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                bool t = p[i].y >= high - 1e-4f && p[i + 1].y >= high - 1e-4f && p[i + 2].y >= high - 1e-4f;
                top[i] = top[i + 1] = top[i + 2] = t;
            }
            tops[m] = top;
            return top;
        }

        // Where the outline turns at p, from the side running in along dIn to the side running out along dOut (unit
        // directions; zero where there is none): the offset from p to the strip's edge on the outer side (the mitre), half
        // width h. A side with no neighbour, or one turning right back, ends square.
        public static Vector2 Mitre(Vector2 nIn, Vector2 nOut, float h)
        {
            if (nIn == Vector2.zero)
            {
                nIn = nOut;
            }
            if (nOut == Vector2.zero)
            {
                nOut = nIn;
            }
            float d = 1f + Vector2.Dot(nIn, nOut);
            if (d < 0.1f)
            {
                return nOut * h;
            }
            return (nIn + nOut) * (h / d);
        }

        // The strip along one outline side a -> b (outward normal n), its ends mitred by the offsets ma (at a) and mb (at
        // b) from Mitre, on top of the rims (y in cells).
        public static void Strip(LayerSubMesh sm, Vector2 a, Vector2 b, Vector2 ma, Vector2 mb, float y)
        {
            float yy = y / View3D.Squash;
            Color32 c = StripColour;
            MeshWriter.Quad(sm, new Vector3(a.x - ma.x, yy, a.y - ma.y), new Vector3(a.x + ma.x, yy, a.y + ma.y),
                new Vector3(b.x + mb.x, yy, b.y + mb.y), new Vector3(b.x - mb.x, yy, b.y - mb.y), c);
        }
    }
}
