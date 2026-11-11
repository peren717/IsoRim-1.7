namespace IsoRim
{
    public static partial class Api
    {
        // ---- first person on (Version 71)

        /// <summary>
        /// (Version 71) IsoRim's first person is on: the player (or an addon) put the view in a colonist's head and has not
        /// left it. Also true while an external camera pose (<see cref="PushViewPose"/>) holds the view: first person carries
        /// on underneath and is shown again when the pose goes, while <see cref="FirstPersonPawn"/> is null (the camera is not
        /// in the head). Not gated on <see cref="ViewReady"/>: true from the moment first person starts, also on the frame the
        /// map is still being prepared for 3D; false once the 3D view's switch (<see cref="View3DActive"/>) is off, as first
        /// person then ends on the next frame. So <see cref="FirstPersonPawn"/> not null always means this is true. A camera
        /// that should give way to first person checks this each frame instead of predicting the first person key.
        /// </summary>
        public static bool FirstPersonOn => FirstPerson.Engaged;
    }
}
