using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 34) Upper floors under wall shapes. A cell upstairs whose block its shapes replace (a trimmed wall cell) has its
    // floor (the game's terrain and IsoRim's slab under it) only where the room is: over its shapes and over the free parts
    // that open onto a floored cell beside them (not a wall). The parts opening onto the open air beside the building are
    // left out, so no floor pokes out past a slanted wall at the storey seam. The ground floor is the map's ground and stays
    // whole. Generic: decided from the registered shapes (WallShapes) only.
    internal static class ShapeFloors
    {
        // The pieces of floor to keep in an upper-floor cell with replacing shapes; null for every other cell (whole).
        public static List<Vector2[]> Keep(Map map, IntVec3 c)
        {
            if (!WallShapes.Drawn || map == null || !c.InBounds(map))
            {
                return null;
            }
            float h;
            int z;
            int k = Floors.ColumnFloor(map, c, out h, out z);
            if (k < 1)
            {
                return null;
            }
            WallShapes.Cell s = WallShapes.Cell.Walled(WallShapes.At(map, c));   // (fences alone: floors as before)
            if (s == null || !s.replaces)
            {
                return null;
            }
            List<Vector2[]> keep = new List<Vector2[]>(s.polys);
            List<List<Vector2>> free = ShapeRoofs.FreeParts(c, s);
            bool[] inside = ShapeRoofs.FreeSide(c, free, n => Floored(map, n, k) && !Wallish(map, n));
            for (int i = 0; i < free.Count; i++)
            {
                if (inside[i])
                {
                    keep.Add(free[i].ToArray());
                }
            }
            return keep;
        }

        private static bool Floored(Map map, IntVec3 n, int k)
        {
            float h;
            int z;
            return n.InBounds(map) && Floors.ColumnFloor(map, n, out h, out z) == k && !map.terrainGrid.TerrainAt(n).dontRender;
        }

        private static bool Wallish(Map map, IntVec3 n)
        {
            WallShapes.Cell s = WallShapes.Cell.Walled(WallShapes.At(map, n));
            if (s != null)
            {
                return s.replaces;
            }
            Building b = n.GetEdifice(map);
            return b != null && WallBoxes.IsWallForm(Forms.For(b.def));
        }

        // Whether point p (map cells) has floor of floor k under it as drawn (cells with replacing shapes: their kept pieces).
        public static bool FloorAt(Map map, Vector2 p, int k)
        {
            IntVec3 n = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.y));
            if (!Floored(map, n, k))
            {
                return false;
            }
            List<Vector2[]> keep = Keep(map, n);
            if (keep == null)
            {
                return true;
            }
            foreach (Vector2[] q in keep)
            {
                if (WallShapes.Inside(q, p, 0f))
                {
                    return true;
                }
            }
            return false;
        }

        // The game's terrain of an upper-floor block: triangles in cells with replacing shapes cut to the kept pieces.
        public static void ClipTerrain(SectionLayer layer, Section section)
        {
            if (!WallShapes.Any || !WallShapes.Drawn || section == null)
            {
                return;
            }
            Map map = section.map;
            float h;
            int z;
            if (Floors.ColumnFloor(map, section.botLeft, out h, out z) < 1)
            {
                return;
            }
            Dictionary<int, List<Vector2[]>> cells = null;
            WallShapes.Begin(map);
            try
            {
                foreach (IntVec3 c in section.CellRect)
                {
                    List<Vector2[]> keep = Keep(map, c);
                    if (keep != null)
                    {
                        if (cells == null)
                        {
                            cells = new Dictionary<int, List<Vector2[]>>();
                        }
                        cells[c.x * 65536 + c.z] = keep;
                    }
                }
            }
            finally
            {
                WallShapes.End();
            }
            if (cells == null)
            {
                return;
            }
            try
            {
                foreach (LayerSubMesh sm in layer.subMeshes)
                {
                    if (sm.verts.Count > 0 && sm.tris.Count > 0 && Clip(sm, cells))
                    {
                        sm.finalized = false;
                        sm.FinalizeMesh(MeshParts.All);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Cutting the floor along wall shapes failed: " + e, 93634001);
            }
        }

        private struct V
        {
            public Vector3 p, uv, uv2, pol, nrm;
            public Color32 col;
        }

        private static readonly List<V> polyA = new List<V>(), polyB = new List<V>();

        // Cuts the triangles of a piece of mesh lying in the given cells to their kept pieces; true if anything changed.
        private static bool Clip(LayerSubMesh sm, Dictionary<int, List<Vector2[]>> cells)
        {
            int n = sm.verts.Count;
            bool hasUv = sm.uvs.Count == n, hasCol = sm.colors.Count == n, hasUv2 = sm.uvsChannelTwo.Count == n;
            bool hasPol = sm.pollution.Count == n, hasNrm = sm.normals.Count == n;
            List<int> tris = new List<int>(sm.tris.Count);
            bool changed = false;
            for (int t = 0; t + 2 < sm.tris.Count; t += 3)
            {
                int a = sm.tris[t], b = sm.tris[t + 1], c = sm.tris[t + 2];
                Vector3 va = sm.verts[a], vb = sm.verts[b], vc = sm.verts[c];
                int cx = Mathf.FloorToInt((va.x + vb.x + vc.x) / 3f), cz = Mathf.FloorToInt((va.z + vb.z + vc.z) / 3f);
                List<Vector2[]> keep;
                if (!cells.TryGetValue(cx * 65536 + cz, out keep))
                {
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(c);
                    continue;
                }
                changed = true;
                foreach (Vector2[] poly in keep)
                {
                    polyA.Clear();
                    foreach (int i in new[] { a, b, c })
                    {
                        polyA.Add(new V
                        {
                            p = sm.verts[i],
                            uv = hasUv ? sm.uvs[i] : Vector3.zero,
                            uv2 = hasUv2 ? sm.uvsChannelTwo[i] : Vector3.zero,
                            pol = hasPol ? sm.pollution[i] : Vector3.zero,
                            nrm = hasNrm ? sm.normals[i] : Vector3.up,
                            col = hasCol ? sm.colors[i] : new Color32(255, 255, 255, 255)
                        });
                    }
                    if (!ClipTo(poly))
                    {
                        continue;
                    }
                    int start = sm.verts.Count;
                    foreach (V v in polyA)
                    {
                        sm.verts.Add(v.p);
                        if (hasUv) sm.uvs.Add(v.uv);
                        if (hasCol) sm.colors.Add(v.col);
                        if (hasUv2) sm.uvsChannelTwo.Add(v.uv2);
                        if (hasPol) sm.pollution.Add(v.pol);
                        if (hasNrm) sm.normals.Add(v.nrm);
                    }
                    for (int k = 1; k + 1 < polyA.Count; k++)
                    {
                        tris.Add(start);
                        tris.Add(start + k);
                        tris.Add(start + k + 1);
                    }
                }
            }
            if (changed)
            {
                sm.tris.Clear();
                sm.tris.AddRange(tris);
            }
            return changed;
        }

        // polyA cut to the inside of a counter-clockwise convex polygon (x, z); false if nothing is left. Keeps polyA's winding.
        private static bool ClipTo(Vector2[] poly)
        {
            for (int e = 0; e < poly.Length && polyA.Count >= 3; e++)
            {
                Vector2 a = poly[e], d = poly[(e + 1) % poly.Length] - a;
                polyB.Clear();
                for (int i = 0; i < polyA.Count; i++)
                {
                    V p = polyA[i], q = polyA[(i + 1) % polyA.Count];
                    float fp = d.x * (p.p.z - a.y) - d.y * (p.p.x - a.x);
                    float fq = d.x * (q.p.z - a.y) - d.y * (q.p.x - a.x);
                    const float E = 1e-6f;
                    if (fp >= -E)
                    {
                        polyB.Add(p);
                    }
                    if ((fp > E && fq < -E) || (fp < -E && fq > E))
                    {
                        float t = fp / (fp - fq);
                        polyB.Add(new V
                        {
                            p = Vector3.Lerp(p.p, q.p, t),
                            uv = Vector3.Lerp(p.uv, q.uv, t),
                            uv2 = Vector3.Lerp(p.uv2, q.uv2, t),
                            pol = Vector3.Lerp(p.pol, q.pol, t),
                            nrm = Vector3.Lerp(p.nrm, q.nrm, t),
                            col = Color32.Lerp(p.col, q.col, t)
                        });
                    }
                }
                polyA.Clear();
                polyA.AddRange(polyB);
            }
            return polyA.Count >= 3;
        }
    }
}
