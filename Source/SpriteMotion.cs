using UnityEngine;

namespace IsoRim
{
    // A flat sprite's motion on screen reproduced on a 3D figure or part (2026-11-09, yayoturn). A mod that animates the game's
    // sprite shifts it across and up the picture and turns it in the picture's plane (degrees clockwise on screen). The same
    // on a 3D thing: the shift along the camera's right and up as height, the turn about the line from its pivot toward the
    // camera (the camera's line of sight there: the view's forward when flat, toward the eye in perspective), so that it turns
    // on screen as much as the sprite and the same way, whichever way the thing faces. Without the 3D view the game's camera
    // looks straight down and the turn is about the vertical, as the sprite's. Generic: nothing here knows which mod moves
    // the sprite (BodyAnims reads them).
    internal static class SpriteMotion
    {
        // From a point (true space) toward the camera, along the camera's line of sight through it.
        public static Vector3 TowardCamera(Vector3 at)
        {
            if (!View3D.Ready)
            {
                return Vector3.up;
            }
            Vector3 d = View3D.Perspective ? View3D.EyePosition - at : -View3D.Forward;
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
        }

        // The shift and turn as a transform of the thing's own space (before its turn by yaw about the vertical): shift
        // (x across the picture, y up it, true-space units), turn (degrees clockwise on screen) about pivot (own space,
        // over the thing's place); pivotAt is the pivot in true space (where the camera's line of sight is taken).
        public static Matrix4x4 Local(float yaw, Vector2 shift, float turn, Vector3 pivot, Vector3 pivotAt)
        {
            Quaternion facing = Quaternion.AngleAxis(yaw, Vector3.up);
            Quaternion back = Quaternion.Inverse(facing);
            Vector3 right = Quaternion.AngleAxis(View3D.Yaw, Vector3.up) * Vector3.right;
            Vector3 move = back * (right * shift.x + Vector3.up * shift.y);
            // (Unity turns clockwise seen from the axis's tip: about the line toward the camera, clockwise on screen)
            Quaternion spin = Quaternion.AngleAxis(turn, back * TowardCamera(pivotAt));
            return Matrix4x4.Translate(move + pivot) * Matrix4x4.Rotate(spin) * Matrix4x4.Translate(-pivot);
        }
    }
}
