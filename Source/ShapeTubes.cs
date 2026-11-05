using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 58) ModelDef.shape Tube: a low linked thing (a power conduit, a pipe) drawn as a tube lying on the ground in place of its
    // flat link sprite, with no model file. It is an ordinary generated model, one piece per set of joined sides, so a cell is written
    // once however it is joined and a straight run has no joint. What joins is what the game draws joined: the thing's own
    // Graphic_Linked.ShouldLinkWith, which a mod's linked graphic overrides (and which the game's transmitters answer for power
    // buildings beside them). The game redraws a cell when that changes, for the sprite as for this. The link sprite the game prints
    // under a building beside it goes with the rest of the sprite (the bake replaces it).
    // Thickness and colour come from the thing's own atlas unless the def sets them (height, tubeColor): the width and the middle's
    // colour of its straight piece, read once per ThingDef when the first one is baked, so one def can serve many things (a pack's
    // model rule). An arm toward something that is not a tube runs a little into its cell and is closed, so it meets a model set in
    // from the cell's edge. Blueprints, frames and placing ghosts keep the game's flat drawing (Ghosts.FormOf).
    public static class ShapeTubes
    {
        public const float DefaultThickness = 0.22f;   // the width of the game's conduit art: an atlas that cannot be read
        private const int Pieces = 256;       // bits | ends << 4
        private const float Stub = 0.18f;     // half the length of a tube joined to nothing
        private const float Edge = 0.5f;
        private const float Reach = 0.06f;    // how far an arm runs into the cell of what it joins that is not a tube
        private const float CellOfTexture = 0.1875f;   // one cell of a link atlas, of the texture (MaterialAtlasPool)
        private const int Straight = 10;      // the east-west piece of an atlas (LinkDirections Right | Left)
        private const float ArtToLit = 1.05f;  // the art's colour to a tube's that reads the same under the model lighting

        public class Look
        {
            public float width;
            public float whole;   // the art's width with its dark outline (harness)
            public Color color;
            public bool measured;
            public Model[] pieces = new Model[Pieces];
        }

        private static readonly Dictionary<ThingDef, Look> looks = new Dictionary<ThingDef, Look>();

        public static bool IsTube(ThingDef def)
        {
            ThingForm f = Forms.For(def);
            return f.form == Form3D.Model && f.model != null && f.model.def.shape == ModelShape.Tube;
        }

        // The sides this thing joins, as the game's own sprite would show them (bits 1 << d, GenAdj.CardinalDirections order), and
        // those of them where what it joins is not a tube (ends).
        public static int LinksOf(Thing t, out int ends)
        {
            ends = 0;
            Graphic_Linked g = t != null && t.Spawned ? t.Graphic as Graphic_Linked : null;
            int bits = 0;
            if (g != null)
            {
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 c = t.Position + GenAdj.CardinalDirections[d];
                    if (g.ShouldLinkWith(c, t))
                    {
                        bits |= 1 << d;
                        if (!TubeIn(t.Map, c))
                        {
                            ends |= 1 << d;
                        }
                    }
                }
            }
            return bits;
        }

        private static bool TubeIn(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def.category == ThingCategory.Building && IsTube(things[i].def))
                {
                    return true;
                }
            }
            return false;
        }

        // The piece this thing is drawn with now.
        public static Model PieceFor(ModelEntry entry, Thing t)
        {
            int ends;
            int bits = LinksOf(t, out ends);
            return Piece(entry.def, LookOf(entry.def, t), bits, ends);
        }

        public static Look LookOf(ModelDef md, Thing t)
        {
            Look look;
            if (!looks.TryGetValue(t.def, out look))
            {
                look = new Look();
                Measure(t.Graphic as Graphic_Linked, look);
                if (md.height > 0f)
                {
                    look.width = md.height;
                }
                if (md.tubeColor.a > 0f)
                {
                    look.color = md.tubeColor;
                }
                looks[t.def] = look;
            }
            return look;
        }

        // A stand-in for what asks of the model in general (HasModel, its top): the piece joined to nothing, at the default size.
        public static Model Plain(ModelDef md)
        {
            Look look = new Look { width = md.height > 0f ? md.height : DefaultThickness, color = Color.white };
            return Piece(md, look, 0, 0);
        }

        // The straight piece's body width (its dark outline left out: a tube has none of its own) and the colour of its middle, read
        // down columns across the cell; the median column, so flanges and rivets along the pipe do not count.
        internal static void Measure(Graphic_Linked g, Look look)
        {
            look.width = DefaultThickness;
            look.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            Material mat = g != null && g.SubGraphic != null ? g.SubGraphic.MatSingle : null;
            Texture tex = mat != null ? mat.mainTexture : null;
            if (tex == null)
            {
                return;
            }
            const int N = 128;   // samples across one cell
            const float Tile = 0.25f;
            Vector2 offset = new Vector2(Straight % 4 * Tile + Tile / 8f, Straight / 4 * Tile + Tile / 8f);
            Color32[] px = SpritePixels.Sample(tex, new Vector2(CellOfTexture, CellOfTexture), offset, N);
            if (px == null)
            {
                return;
            }
            List<Column> columns = new List<Column>();
            for (int x = N / 8; x < N; x += N / 16)
            {
                Column c = Read(px, x, N);
                if (c != null)
                {
                    columns.Add(c);
                }
            }
            if (columns.Count == 0)
            {
                return;
            }
            columns.Sort((p, q) => p.body.CompareTo(q.body));
            Column mid = columns[columns.Count / 2];
            look.measured = true;
            look.whole = (float)mid.whole / N;
            look.width = (float)mid.body / N;
            Color art = mid.color * mat.color;
            look.color = new Color(Mathf.Min(1f, art.r * ArtToLit), Mathf.Min(1f, art.g * ArtToLit), Mathf.Min(1f, art.b * ArtToLit), 1f);
        }

        private class Column
        {
            public int whole, body;
            public Color color;
        }

        private static Column Read(Color32[] px, int x, int n)
        {
            int first = -1, last = -1;
            for (int row = 0; row < n; row++)
            {
                if (px[row * n + x].a >= 128)
                {
                    first = first < 0 ? row : first;
                    last = row;
                }
            }
            if (first < 0)
            {
                return null;
            }
            float r = 0f, g = 0f, b = 0f;
            int count = 0;
            int skip = (last - first + 1) / 4;   // the dark outline stays out of the colour
            for (int row = first + skip; row <= last - skip; row++)
            {
                Color32 c = px[row * n + x];
                r += c.r;
                g += c.g;
                b += c.b;
                count++;
            }
            if (count == 0)
            {
                return null;
            }
            float bright = (r + g + b) / count;
            int body = 0;
            for (int row = first; row <= last; row++)
            {
                Color32 c = px[row * n + x];
                if (c.r + c.g + c.b >= bright * 0.8f)
                {
                    body++;
                }
            }
            return new Column { whole = last - first + 1, body = body, color = new Color(r / count / 255f, g / count / 255f, b / count / 255f, 1f) };
        }

        private static Model Piece(ModelDef md, Look look, int bits, int ends)
        {
            int index = bits | ends << 4;
            Model m = look.pieces[index];
            if (m == null)
            {
                m = Make(look, bits, ends, look.width / 2f / Mathf.Max(0.01f, md.scale));
                look.pieces[index] = m;
            }
            return m;
        }

        private static Model Make(Look look, int bits, int ends, float r)
        {
            bool n = (bits & 1) != 0, e = (bits & 2) != 0, s = (bits & 4) != 0, w = (bits & 8) != 0;
            List<Vector3[]> quads = new List<Vector3[]>();
            if (bits == 0)
            {
                Run(quads, false, -Stub, Stub, true, true, r);
            }
            if (n || s)
            {
                bool through = e && w;   // its open end lies inside the run across
                float a = !s ? -r : (ends & 4) != 0 ? -Edge - Reach : -Edge;
                float b = !n ? r : (ends & 1) != 0 ? Edge + Reach : Edge;
                Run(quads, true, a, b, !s ? !through : (ends & 4) != 0, !n ? !through : (ends & 1) != 0, r);
            }
            if (e || w)
            {
                bool through = n && s;
                float a = !w ? -r : (ends & 8) != 0 ? -Edge - Reach : -Edge;
                float b = !e ? r : (ends & 2) != 0 ? Edge + Reach : Edge;
                Run(quads, false, a, b, !w ? !through : (ends & 8) != 0, !e ? !through : (ends & 2) != 0, r);
            }
            List<Color> colors = new List<Color>(quads.Count);
            for (int i = 0; i < quads.Count; i++)
            {
                colors.Add(look.color);
            }
            Model m = ObjLoader.FromQuads(quads, colors);
            m.materials[0].name = "stuff_round";
            m.materials[0].round = true;   // smooth along its round side, plain faces at its ends
            for (int i = 0; i < m.tinted.Length; i++)
            {
                m.tinted[i] = false;
            }
            return m;
        }

        // The tube's cross-section, a hexagon on one flat side (left out: it lies on the ground): across, up.
        private static Vector2[] Section(float r)
        {
            float mid = r * 0.866f;
            return new[]
            {
                new Vector2(r / 2f, 0f), new Vector2(r, mid), new Vector2(r / 2f, mid * 2f),
                new Vector2(-r / 2f, mid * 2f), new Vector2(-r, mid), new Vector2(-r / 2f, 0f)
            };
        }

        // One straight stretch from a to b along z (north) or x (east), its ends closed where asked.
        private static void Run(List<Vector3[]> quads, bool alongZ, float a, float b, bool capA, bool capB, float r)
        {
            Vector2[] p = Section(r);
            Vector3 middle = At(alongZ, new Vector2(0f, r * 0.866f), (a + b) / 2f);
            for (int i = 0; i + 1 < p.Length; i++)
            {
                Add(quads, middle, At(alongZ, p[i], a), At(alongZ, p[i], b), At(alongZ, p[i + 1], b), At(alongZ, p[i + 1], a));
            }
            if (capA)
            {
                Cap(quads, alongZ, p, a, b);
            }
            if (capB)
            {
                Cap(quads, alongZ, p, b, a);
            }
        }

        private static void Cap(List<Vector3[]> quads, bool alongZ, Vector2[] p, float at, float away)
        {
            Vector3 inside = At(alongZ, Vector2.zero, away);
            Add(quads, inside, At(alongZ, p[5], at), At(alongZ, p[0], at), At(alongZ, p[1], at), At(alongZ, p[2], at));
            Add(quads, inside, At(alongZ, p[2], at), At(alongZ, p[3], at), At(alongZ, p[4], at), At(alongZ, p[5], at));
        }

        private static Vector3 At(bool alongZ, Vector2 section, float along)
        {
            return alongZ ? new Vector3(section.x, section.y, along) : new Vector3(along, section.y, section.x);
        }

        // A quad facing away from inside (ObjLoader.FromQuads takes the normal from its corner order).
        private static void Add(List<Vector3[]> quads, Vector3 inside, Vector3 c0, Vector3 c1, Vector3 c2, Vector3 c3)
        {
            Vector3 normal = Vector3.Cross(c1 - c0, c2 - c0);
            quads.Add(Vector3.Dot(normal, (c0 + c1 + c2 + c3) / 4f - inside) >= 0f ? new[] { c0, c1, c2, c3 } : new[] { c3, c2, c1, c0 });
        }
    }
}
