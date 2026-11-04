using System.Reflection;

namespace IsoRim
{
    public static partial class Api
    {
        /// <summary>
        /// (Version 54) For a mod that draws a weapon in hand itself, in a method of its own (a Harmony prefix on the game's
        /// DrawEquipmentAiming), with Graphics.DrawMesh(mesh, matrix, material, layer): IsoRim's figures never see that draw, so the
        /// weapon stays flat on the ground. While a figure's weapon is drawn, this method's draws are taken as the game's own: it shows
        /// its model, or its sprites made solid, one over the other; every other draw of it goes to Unity as before (a pawn drawn as
        /// a card, the 2D view). A weapon drawn in several pictures shows one model. Call once, at startup; twice does nothing. False, with one warning, if the method
        /// makes no such draw or cannot be patched (nothing is changed then). Throws on null.
        /// </summary>
        public static bool CatchWeaponDraws(MethodBase method) => FigureWeapons.CatchDrawsOf(method);
    }
}
