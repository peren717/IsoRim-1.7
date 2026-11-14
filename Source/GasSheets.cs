using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Gas in 3D (the game's SectionLayer_Gas: smoke, tox gas, rot stink, deadlife dust, and a mod's gas layer deriving from
    // it). The game lays one soft quad per gas cell flat on the ground (AddCell: jittered size and place, the cell's gas
    // densities as its colour, its index in uv z for the turning). Seen from a tilted camera that is a sheet on the ground, so
    // in the 3D view each quad is copied into Sheets - 1 more sheets stacked above it, from the ground up to Top: a cloud
    // with depth. The game's quad stays as it is (the bottom sheet); a sheet k up is drawn with a copy of the layer's own
    // material whose colour alpha is FadePerSheet^k (fainter going up), a little smaller (Shrink) and moved a little
    // (Drift, fixed per cell and sheet), and turns its own way (IndexStep added to the turning index): the layers never
    // look the same. Material, textures, the densities and the turning are the game's: only the height and the alpha change.
    //
    // THE gas height rule, run once per rebuild of a gas block (Patches.Gas_Postfix, the one Regenerate postfix of the gas
    // layer), in this order:
    //   1. Stack (here): the sheets, at heights over the game's quad. Under a lid (a roof, or a stacked floor above:
    //      Floors.SlabOver) the top stays under the wall tops, where roofs and slabs lie (UnderLid); fogged cells get no
    //      sheets (they would rise through the fog lid).
    //   2. Bridges.LiftGround: every quad in a deck cell (and on a ramp's land) put on the deck, the sheets with it.
    //   3. A block of a stacked floor is drawn moved onto that floor (Floors: DrawLayer_Prefix below hands the gas layer's
    //      own drawing the block's matrix), the sheets with it.
    // With the 3D view off nothing is added: the 2D view is the game's own (the map is rebuilt when the view switches).
    // Not touched: the gas grid and everything that reads it (the game's rules); motes and flecks of smoke (other drawers).
    public static class GasSheets
    {
        // Sheets in all, the game's own quad on the ground counting as the first (user plan 2026-11-13: start with 3).
        public const int Sheets = 3;

        // The top sheet's height over open ground, cells (user plan: about 1.5 cells; a colonist is about 1.2 tall).
        public const float Top = 1.5f;

        // Under a lid (roof or a floor above) the top is at most this share of the wall height: roofs lie on the wall tops
        // and slabs higher (Floors), so the sheets stay inside the room and hide under the lid; with the lid cut away they show.
        public const float UnderLid = 0.85f;

        // Alpha of sheet k (0 = the game's quad) is FadePerSheet^k: 1, 0.8, 0.64. The game's gas is faint (a smokepop cloud
        // about 0.2-0.4 alpha), so fainter upper sheets (0.6: tried in engine 2026-11-13) barely showed; at 0.8 the cloud reads
        // as a haze with depth while a pawn in it still shows.
        public const float FadePerSheet = 0.8f;

        // Sheet k is drawn 1 - Shrink * k of the game's quad's size (round its middle): the cloud narrows a little upward.
        public const float Shrink = 0.08f;

        // Cells each sheet's quad is moved at most, either way in x and z (fixed per cell and sheet: the same every rebuild).
        public const float Drift = 0.18f;

        // Added to the quad's turning index (uv z, GasRotating's per-cell speed, direction and start angle) per sheet.
        public const float IndexStep = 0.37f;

        // Totals since the game started, for the probe's performance check (ProbeGas): blocks stacked, the game's quads
        // stacked over, sheet quads written, stopwatch ticks spent.
        public static int Blocks, QuadsIn, QuadsOut;
        public static long Ticks;

        // ---- the faded copies of a gas material, one per sheet above the ground (they follow their source's properties)

        private sealed class Copies
        {
            public readonly Material[] mats = new Material[Sheets];   // [0] unused (the game's own material)
            public int version = int.MinValue;
        }

        private static readonly ConditionalWeakTable<Material, Copies> copies = new ConditionalWeakTable<Material, Copies>();
        private static readonly HashSet<Material> ours = new HashSet<Material>();

        private static Material SheetMaterial(Material src, int k)
        {
            Copies e = copies.GetValue(src, _ => new Copies());
            int v = src.engineMaterial != null ? src.engineMaterial.Version : 0;
            if (e.version != v)
            {
                for (int i = 1; i < Sheets; i++)
                {
                    Material m = e.mats[i];
                    if (m == null)
                    {
                        m = e.mats[i] = new Material(src.shader) { name = src.name + "_GasSheet" + i };
                        ours.Add(m);
                    }
                    m.CopyPropertiesFromMaterial(src);
                    m.renderQueue = src.renderQueue;
                    Color c = src.color;
                    c.a *= Mathf.Pow(FadePerSheet, i);
                    m.color = c;
                }
                e.version = v;
            }
            return e.mats[k];
        }

        // ---- the sheets

        private static readonly LayerSubMesh[] targets = new LayerSubMesh[Sheets];

        // After the game's Regenerate of a gas block (before the bridge lift).
        public static void Stack(SectionLayer layer)
        {
            if (!View3D.Active || !(layer is SectionLayer_Gas))
            {
                return;
            }
            Map map = MapSections.MapOf(layer);
            if (map == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            List<LayerSubMesh> subs = layer.subMeshes;
            int sources = subs.Count;   // (the sheets' sub-meshes are added after these, or are among them, cleared)
            float squash = View3D.Squash;
            float lidTop = Mathf.Min(Top, UnderLid * View3D.WallHeight);
            bool any = false;
            for (int s = 0; s < sources; s++)
            {
                LayerSubMesh src = subs[s];
                if (src == null || src.material == null || ours.Contains(src.material) || !src.finalized || src.material.IsDeferredMaterial)
                {
                    continue;
                }
                List<Vector3> verts = src.verts;
                int n = verts.Count;
                // the game's layout (AddCell): 4 corners and 2 triangles a quad, in order; anything else is left flat
                if (n == 0 || n % 4 != 0 || src.tris.Count != n / 4 * 6 || src.uvs.Count != n || (src.colors.Count != n && src.colors.Count != 0))
                {
                    continue;
                }
                for (int k = 1; k < Sheets; k++)
                {
                    targets[k] = layer.GetSubMesh(SheetMaterial(src.material, k));
                }
                for (int q = 0; q < n / 4; q++)
                {
                    int v = q * 4;
                    Vector3 a = verts[v], c = verts[v + 2];
                    float mx = (a.x + c.x) * 0.5f, mz = (a.z + c.z) * 0.5f;
                    IntVec3 cell = new IntVec3(Mathf.FloorToInt(mx), 0, Mathf.FloorToInt(mz));
                    if (!cell.InBounds(map) || map.fogGrid.IsFogged(cell))
                    {
                        continue;
                    }
                    float top = map.roofGrid.Roofed(cell) || Floors.SlabOver(map, cell) ? lidTop : Top;
                    for (int k = 1; k < Sheets; k++)
                    {
                        AddSheet(src, q, targets[k], k, mx, mz, top * k / (Sheets - 1) / squash, Hash(cell.x, cell.z, k));
                    }
                    any = true;
                    QuadsIn++;
                }
            }
            if (any)
            {
                for (int s = 0; s < subs.Count; s++)
                {
                    LayerSubMesh sm = subs[s];
                    if (!sm.finalized && sm.verts.Count > 0 && ours.Contains(sm.material))
                    {
                        sm.FinalizeMesh(MeshParts.All);
                    }
                }
                Blocks++;
            }
            Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        // Quad q of src copied into sheet k: dy higher (map units), scaled round its middle (mx, mz) and moved.
        private static void AddSheet(LayerSubMesh src, int q, LayerSubMesh to, int k, float mx, float mz, float dy, uint h)
        {
            float scale = 1f - Shrink * k;
            float ox = (Unit(h) * 2f - 1f) * Drift;
            float oz = (Unit(h * 2654435761u + 1u) * 2f - 1f) * Drift;
            int v0 = q * 4;
            int start = to.verts.Count;
            bool colours = src.colors.Count > 0;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = src.verts[v0 + i];
                to.verts.Add(new Vector3(mx + (p.x - mx) * scale + ox, p.y + dy, mz + (p.z - mz) * scale + oz));
                Vector3 uv = src.uvs[v0 + i];
                uv.z += IndexStep * k;
                to.uvs.Add(uv);
                if (colours)
                {
                    to.colors.Add(src.colors[v0 + i]);
                }
            }
            int t0 = q * 6;
            for (int i = 0; i < 6; i++)
            {
                to.tris.Add(start + src.tris[t0 + i] - v0);
            }
            QuadsOut++;
        }

        // A fixed hash of a cell and a sheet (no game random state touched).
        private static uint Hash(int x, int z, int k)
        {
            uint h = (uint)x * 73856093u ^ (uint)z * 19349663u ^ (uint)k * 83492791u;
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }

        private static float Unit(uint h)
        {
            return (h & 0xFFFFFF) / 16777216f;
        }

        // ---- the gas layer on a stacked floor

        // SectionLayer_Gas draws itself (its own DrawLayer, which Patches.DrawLayer_Prefix does not see). For a block of an
        // upper floor its draws get the block's matrix (Floors.SelfDrawnLifted, read by Floors.Single_Prefix), as every other
        // layer of that block (Floors.DrawLayer).
        public static void DrawLayer_Prefix()
        {
            Floors.SelfDrawnLifted = Floors.sectionLifted;
        }

        public static void DrawLayer_Finalizer()
        {
            Floors.SelfDrawnLifted = false;
        }
    }
}
