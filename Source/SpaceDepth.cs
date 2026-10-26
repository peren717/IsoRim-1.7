using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The ground's depth on space maps (Sky), cell by cell: only where the game draws terrain. The space cells (the
    // void, TerrainDef.dontRender) get none, so the sky reaches them and, past the planet's limb (OrbitPlanet), space
    // shows there as all round, not the world camera's own backdrop.
    //
    // The game's terrain writes no depth, so on other maps Sky draws one flat depth quad under the whole map. On space
    // maps the void cells must stay open to the sky, yet the sky's dim pass (IsoRimSkyDim) must not darken the planet
    // there a second time: the game's light overlay already darkens it on the drawn sections, as in 2D. So Sky lays
    // the drawn sections at a depth just short of the far end (IsoRimDepthFar), which the sky quad still passes (it is
    // drawn a hair nearer on space maps) but the dim pass does not, and this layer gives the terrain cells their real
    // depth over it. Rebuilt with the terrain (a platform built or torn down); stacked floors (Floors.DrawnRows): only
    // the ground rows.
    public class SectionLayer_IsoRimSpaceDepth : SectionLayer
    {
        private static Material material;
        private static bool tried;

        public SectionLayer_IsoRimSpaceDepth(Section section) : base(section)
        {
            relevantChangeTypes = MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible
        {
            get { return View3D.Ready && Sky.SpaceDepthNow; }
        }

        private static Material Mat
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    Shader s = R3DMaterials.ShaderOf("IsoRimDepthPlane");
                    if (s != null)
                    {
                        material = new Material(s);
                        material.name = "IsoRim_SpaceDepth";
                    }
                }
                return material;
            }
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            if (!View3D.Active || !SpaceEdges.IsSpaceMap(map) || Mat == null)
            {
                FinalizeMesh(MeshParts.All);
                return;
            }
            try
            {
                TerrainGrid grid = map.terrainGrid;
                CellRect r = section.CellRect;
                int maxZ = Mathf.Min(r.maxZ, Floors.DrawnRows(map) - 1);
                LayerSubMesh sm = MeshWriter.Sub(this, Mat);
                // One quad per run of drawn cells along a row.
                for (int z = r.minZ; z <= maxZ; z++)
                {
                    int start = -1;
                    for (int x = r.minX; x <= r.maxX + 1; x++)
                    {
                        bool ground = false;
                        if (x <= r.maxX)
                        {
                            TerrainDef t = grid.TerrainAt(new IntVec3(x, 0, z));
                            ground = t != null && !t.dontRender;
                        }
                        if (ground && start < 0)
                        {
                            start = x;
                        }
                        else if (!ground && start >= 0)
                        {
                            int n = sm.verts.Count;
                            sm.verts.Add(new Vector3(start, 0f, z));
                            sm.verts.Add(new Vector3(start, 0f, z + 1));
                            sm.verts.Add(new Vector3(x, 0f, z + 1));
                            sm.verts.Add(new Vector3(x, 0f, z));
                            sm.tris.Add(n);
                            sm.tris.Add(n + 1);
                            sm.tris.Add(n + 2);
                            sm.tris.Add(n);
                            sm.tris.Add(n + 2);
                            sm.tris.Add(n + 3);
                            start = -1;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Building the ground's depth on a space map failed: " + e, 93611962);
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
