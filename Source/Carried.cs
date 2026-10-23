using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // What a 3D pawn (a colonist figure, an animal, a mech) carries, drawn here because the game's own
    // drawing of the pawn (whose render tree draws it, PawnRenderNodeWorker_Carried) is skipped:
    //   - a hauled item lies in front of the carrier at hand height, made solid from its own sprite
    //     like the items on the ground (or as its model, if it has one);
    //   - where the game's job puts it elsewhere (food set down on a table to eat), there, on the table top;
    //   - a carried person, animal or corpse is drawn by the game, lifted to the carrier's hands (LiftOf).
    // Also how high a pawn is off the ground in mid-jump (PawnFlyer: jump packs, mech jump launchers).
    public static class Carried
    {
        private const float ItemScale = 0.8f;   // of the game's drawn size, to suit the figures (as held weapons)

        // Where each carried pawn (by thingIDNumber) is held this frame: x, height, z.
        private static readonly Dictionary<int, Vector3> heldAt = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, int> heldFrame = new Dictionary<int, int>();

        private static readonly AccessTools.FieldRef<PawnFlyer, float> flyerHeight = AccessTools.FieldRefAccess<PawnFlyer, float>("effectiveHeight");

        // How far above the ground the pawn is drawn: in mid-jump, the height the game shows by moving its
        // sprite up the screen (drawLoc is moved back down onto the ground under it); carried by a 3D pawn,
        // the carrier's hands (drawLoc moved there). 0 otherwise.
        public static float LiftOf(Pawn pawn, ref Vector3 drawLoc)
        {
            try
            {
                float sky;
                if (Skyfallers.PawnLift(pawn, out sky))
                {
                    return sky;   // a flying animal arriving or leaving (Skyfallers: drawn at the ground point under it)
                }
                PawnFlyer flyer = pawn.ParentHolder as PawnFlyer;
                if (flyer != null && flyer.def != null && flyer.def.pawnFlyer != null)
                {
                    float h = flyer.def.pawnFlyer.heightFactor * flyerHeight(flyer);
                    drawLoc.z -= h;
                    return Mathf.Max(0f, h);
                }
                int id = pawn.thingIDNumber;
                Vector3 at;
                int frame;
                if ((pawn.CarriedBy != null || pawn.Corpse != null) && heldAt.TryGetValue(id, out at) && heldFrame.TryGetValue(id, out frame)
                    && frame == Time.frameCount)
                {
                    drawLoc.x = at.x;
                    drawLoc.z = at.z;
                    return at.y;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Working out how high a pawn is off the ground failed: " + e, 93611411);
            }
            return 0f;
        }

        // Draws what the carrier carries. hold: x and z in map cells and the height (in cells) of the bottom of
        // the thing; yaw: the way the carrier faces.
        public static void Draw(Pawn carrier, Vector3 hold, float yaw)
        {
            Thing t = carrier.carryTracker != null ? carrier.carryTracker.CarriedThing : null;
            if (t == null)
            {
                return;
            }
            try
            {
                // The game's job may put it elsewhere (food on the table): there, on whatever stands in that cell.
                Vector3 vanilla = carrier.DrawPos;
                bool flip = false;
                if (carrier.CurJob != null && carrier.jobs != null && carrier.jobs.curDriver != null
                    && carrier.jobs.curDriver.ModifyCarriedThingDrawPos(ref vanilla, ref flip))
                {
                    hold = new Vector3(vanilla.x, hold.y, vanilla.z);
                    IntVec3 c = vanilla.ToIntVec3();
                    Map map = carrier.Map;
                    Building table = map != null && c.InBounds(map) ? c.GetEdifice(map) : null;
                    if (table != null && c != carrier.Position)
                    {
                        hold.y = Forms.HeightOf(table);
                    }
                }
                else if (t.TryGetComp<CompPushable>() != null)
                {
                    // Pushed along the ground beside the pusher, where the game puts it.
                    Vector3 pos = carrier.DrawPos;
                    PawnRenderUtility.CalculateCarriedDrawPos(carrier, t, ref pos, out flip);
                    hold = new Vector3(pos.x, 0f, pos.z);
                }

                Pawn held = t as Pawn ?? (t is Corpse ? ((Corpse)t).InnerPawn : null);
                if (held != null)
                {
                    // Drawn by the game (through its own 3D drawing), lying across the carrier's hands.
                    heldAt[held.thingIDNumber] = hold;
                    heldFrame[held.thingIDNumber] = Time.frameCount;
                    if (heldAt.Count > 200)
                    {
                        heldAt.Clear();
                        heldFrame.Clear();
                    }
                    t.DrawNowAt(new Vector3(hold.x, AltitudeLayer.Pawn.AltitudeFor(), hold.z), flip);
                    return;
                }

                // A book being read: opened (OpenBooks), in the book's colour; closed again when the reading ends.
                Model open = OpenBooks.For(t);
                if (open != null)
                {
                    DynamicModels.Draw(open, hold, yaw, Vector3.one * ItemScale, SpriteColors.MeanFor(t), null);
                    return;
                }
                ThingForm form = Forms.For(t.def);
                if (form.form == Form3D.Model && form.model != null && form.model.HasModel)
                {
                    Model m = form.model.Pick(t);
                    DynamicModels.Draw(m, hold, yaw + form.model.def.rotation, Vector3.one * form.model.def.scale * ItemScale,
                        form.model.def.spriteTint ? SpriteColors.MeanFor(t) : t.DrawColor, R3DTextures.StuffSwatch(t));
                    return;
                }
                float thick = Mathf.Clamp(Forms.ItemThickness(t.def), 0.04f, 0.2f);
                if (SpriteSolids.DrawLying(t, hold, thick, ItemScale))
                {
                    return;
                }
                // No usable sprite: the game's own flat drawing, raised to the hands.
                DrawHook.xform = View3D.LiftMatrix(hold.y);
                DrawHook.active = true;
                try
                {
                    t.DrawNowAt(new Vector3(hold.x, AltitudeLayer.Item.AltitudeFor(), hold.z), flip);
                }
                finally
                {
                    DrawHook.active = false;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing a carried thing failed: " + e, 93611412);
            }
        }
    }
}
