using UnityEngine;
using Verse;

namespace IsoRim
{
    // Api 37: roofed shells. A structure on some map (another mod's temporary map, say) is read once into a Shell: its walls,
    // shut doors, closed roofs, deck and the buildings with models that stand out, combined per material; the Shell is then
    // drawn at any pose, true height and turn (a ship flying in), after the map it came from is gone. Generic: no mod names.
    public static partial class Api
    {
        /// <summary>
        /// (Api version 37) Reads the structure in rect on map into a roofed shell (<see cref="Shell"/>), or null outside the
        /// 3D view (its look is IsoRim's 3D one) or if nothing could be read. Main thread. The shell is drawn round rect's
        /// centre (<see cref="Shell.Origin"/>); the caller owns it and disposes it.
        /// </summary>
        public static Shell BuildShell(Map map, CellRect rect) => Shell.Build(map, rect);

        /// <summary>
        /// (Api version 37) The true height (cells) at which a point shows as far up the screen as the game's fake 2D height,
        /// a slide of slide cells up the screen (vanilla's flying gravship), at the current tilt; kept well below the eye.
        /// The height IsoRim flies the gravship at.
        /// </summary>
        public static float HeightForSlide(float slide) => ShipFlight.HeightFor(slide);

        /// <summary>(Api version 37) Shells built and not disposed yet.</summary>
        public static int LiveShells => Shell.Live;

        // ---- flight effect cards (Version 37): the pieces IsoRim's own gravship flight draws its effects with (ShipFlight),
        // for an addon flying something of its own (thruster flames, glows, the game's screen-space effects).
        // Positions in true space (x, z map cells, y the height in cells). Only meaningful while ViewReady.

        /// <summary>
        /// (Api version 37) A glow sprite (MeshPool.plane10 with mat) stood up facing the camera, centred on at, size cells
        /// across; moved toward the camera by more than half its size (no change on screen) and by pull more, so what it
        /// hangs over does not cut it off.
        /// </summary>
        public static void DrawGlowCard(Vector3 at, float size, Material mat, float pull = 0f)
        {
            if (mat == null || !View3D.Ready)
            {
                return;
            }
            at -= View3D.Forward * (size * 0.6f + pull);
            Vector3 pivot = new Vector3(at.x, at.y / View3D.Squash, at.z);
            Graphics.DrawMesh(MeshPool.plane10, View3D.UprightMatrix(pivot) * Matrix4x4.TRS(pivot, Quaternion.identity, new Vector3(size, 1f, size)), mat, 0);
        }

        /// <summary>
        /// (Api version 37) A flame-like card (MeshPool.plane10, size cells square) centred on at, its texture up along along
        /// (a thruster's thrust), turned about that axis to face the camera, as IsoRim's gravship thruster flames.
        /// </summary>
        public static void DrawAxialCard(Vector3 at, Vector3 along, float size, Material mat, MaterialPropertyBlock props = null)
        {
            if (mat == null || !View3D.Ready || along.sqrMagnitude < 1e-6f)
            {
                return;
            }
            along.Normalize();
            Vector3 toCamera = -View3D.Forward;
            Vector3 face = toCamera - along * Vector3.Dot(along, toCamera);
            if (face.sqrMagnitude < 1e-4f)
            {
                face = Vector3.up;
            }
            Matrix4x4 m = Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f)) * Matrix4x4.TRS(at, Quaternion.LookRotation(along, face.normalized), new Vector3(size, 1f, size));
            Graphics.DrawMesh(MeshPool.plane10, m, mat, 0, null, 0, props);
        }

        /// <summary>(Api version 37) Where a point shows in the viewport, 0-1 from the bottom left (as Camera.WorldToViewportPoint).</summary>
        public static Vector2 WorldToViewport(Vector3 p) => View3D.WorldToViewport(p);

        /// <summary>
        /// (Api version 37) Draws one of the game's screen-space effect materials (gravship distortion, downwash, lens flare:
        /// a plane filling the screen) in the 3D view: in front of everything, or with behind set, at the depth of that point
        /// (what stands in front of it covers the effect). Set its _DrawPos from <see cref="WorldToViewport"/> first.
        /// </summary>
        public static void DrawScreenCard(Material mat, MaterialPropertyBlock props = null, Vector3? behind = null)
        {
            if (mat == null || !View3D.Ready)
            {
                return;
            }
            float depth = behind.HasValue ? View3D.DepthOf(behind.Value) - 0.5f : View3D.NearCard;
            Graphics.DrawMesh(MeshPool.plane10, View3D.ScreenCard(depth), mat, 0, null, 0, props);
        }
    }
}
