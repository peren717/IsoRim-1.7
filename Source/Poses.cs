using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Pawns posed on furniture, worked out in one place for everything that draws or measures a pawn (the 3D figure,
    // the upright 2D card and what it holds, the held weapon, first person, picking and the bubbles over the head).
    //
    // Sitting is the game's own notion (PawnTweener.TweenedPosRoot): a pawn whose life stage has a sittingOffset (children,
    // teenagers, adults; not babies), standing (posture), not walking (pather.MovingNow), on a cell whose edifice
    // isSittable (stools, chairs, armchairs, couches, pews, thrones, kneel pillows and sheets). Which job put them there
    // does not matter, as in the game. The game shows it by drawing the sprite sittingOffset cells up the screen; in 3D
    // that shift is taken back out and the pawn is put on the seat (the seat height of its model, ModelDef.seatHeight):
    //   - a 3D figure stands its base on the seat with the body squashed a little, so the head comes down (People.Draw);
    //   - an upright card stands on the seat, in the middle of its cell (Patches.RenderPawnAt_Prefix).
    // The game eases the shift in and out (its tween spring); how far it has got is the amount, so the pawn settles onto
    // the seat as the game's sprite slides.
    //
    // Lying flat (a sleeper drawn by the game as a flat sprite: people as sprites, figures the mod can't draw, animals with
    // no model): on the top of the bedding (ModelDef.beddingTop: the pillow), not on the mattress under the blanket and
    // pillow that would cover it. A 3D figure has a body of its own and lies on surfaceHeight as before.
    //
    // Attached to a host (Api 13, Attach.cs): on the host's anchor, posed and turned as the attachment says; picking, first
    // person, the bubbles and the muzzle height take it from DrawnSpot / AttachRise, the figure from People.Draw.
    //
    // With the 3D view off nothing here applies: the game's own drawing.
    public static class Poses
    {
        public static bool On = true;   // harness: poses=0 for before/after shots

        public const float SitSquash = 0.82f;   // a seated figure's body height, of its standing height
        public const float BodyTop = 0.74f;     // top of a figure's body (the neck), figure space (person_*_upper)

        public struct Seat
        {
            public Building thing;   // what they sit on
            public float height;     // top of its seat, cells over the floor
            public float amount;     // 0..1: how far the game's own sitting shift has got
            public float shift;      // that shift (cells north), taken back out of the drawn position

            public float Lift
            {
                get { return height * amount; }
            }

            // The figure's body height, of standing.
            public float Squash
            {
                get { return Mathf.Lerp(1f, SitSquash, amount); }
            }

            // How far a figure's head (and hands) come down from squashing the body, for a figure of this size.
            public float HeadDrop(float size)
            {
                return (1f - Squash) * BodyTop * size;
            }
        }

        // True if the pawn sits (see above). drawLoc is where the game draws it: the sitting shift is taken out of it.
        public static bool Seated(Pawn pawn, ref Vector3 drawLoc, out Seat seat)
        {
            seat = default(Seat);
            Building b = SeatUnder(pawn);
            if (b == null)
            {
                return false;
            }
            float offset = pawn.ageTracker.CurLifeStage.sittingOffset.Value;
            float dz = drawLoc.z - (pawn.Position.z + 0.5f);
            seat.thing = b;
            seat.height = SeatHeight(b);
            seat.shift = Mathf.Clamp(dz, 0f, Mathf.Max(0f, offset));
            seat.amount = offset > 0.001f ? seat.shift / offset : 1f;
            drawLoc.z -= seat.shift;
            return true;
        }

        // The same at the game's drawn position (pawn.DrawPos).
        public static bool Seated(Pawn pawn, out Seat seat)
        {
            Vector3 at = pawn.DrawPos;
            return Seated(pawn, ref at, out seat);
        }

        // Where the pawn is drawn on the ground (pawn.DrawPos, the sitting shift taken out), and how much higher (or lower)
        // its head is than standing: for first person's eye, picking and the box selection.
        public static Vector3 DrawnSpot(Pawn pawn, out float headRise)
        {
            Vector3 at = pawn.DrawPos;
            if (Attachments.Any && AttachRise(pawn, out headRise))
            {
                return at;   // attached to a host (Api 13): DrawPos is already on its anchor
            }
            Seat s;
            // (a 3D figure's body is squashed; a card is not)
            headRise = Seated(pawn, ref at, out s) ? s.Lift - (Patches.DrawnAsModel(pawn) ? s.HeadDrop(1f) : 0f) : 0f;
            return at;
        }

        // A pawn attached to a host (Api 13, Attachments; IsoRim draws it there): how much higher than standing on its floor
        // its head is (a person figure, by its pose on the anchor), or the anchor's height (anything else).
        public static bool AttachRise(Pawn pawn, out float rise)
        {
            rise = 0f;
            Attachments.Placed p;
            if (!Attachments.Any || !Attachments.Place(pawn, false, null, out p))
            {
                return false;
            }
            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                rise = p.at.y;
                return true;
            }
            float size = AlienFigures.SizeOf(pawn);   // (a race's scale too, Api 25)
            rise = Attachments.HeadRise(p.a.pose, p.at.y, size, Attachments.Hips);
            return true;
        }

        // The lowest point of an attached pawn's figure as drawn on its host (a little under the anchor: the hips of a
        // rider), for picking.
        public static bool AttachBottom(Pawn pawn, out float bottom)
        {
            bottom = 0f;
            Attachments.Placed p;
            if (!Attachments.Any || !Attachments.Place(pawn, false, null, out p))
            {
                return false;
            }
            bottom = Mathf.Max(0f, p.at.y - 0.15f);
            return true;
        }

        // The seat the pawn sits on by the game's rule, or null.
        public static Building SeatUnder(Pawn pawn)
        {
            if (!On || pawn == null || !View3D.Active || !pawn.Spawned || pawn.Dead || pawn.ageTracker == null || pawn.pather == null
                || Attachments.Any && Attachments.HostOf(pawn) != null)
            {
                return null;
            }
            LifeStageDef stage = pawn.ageTracker.CurLifeStage;
            if (stage == null || !stage.sittingOffset.HasValue || pawn.pather.MovingNow || pawn.GetPosture() != PawnPosture.Standing)
            {
                return null;
            }
            Map map = pawn.Map;
            Building b = map != null && pawn.Position.InBounds(map) ? pawn.Position.GetEdifice(map) : null;
            return b != null && b.def.building != null && b.def.building.isSittable ? b : null;
        }

        // Top of a seat, cells over the floor: its model's seat (ModelDef.seatHeight, else surfaceHeight), else the top of
        // its 3D shape (0 when it stays flat).
        public static float SeatHeight(Building b)
        {
            ThingForm f = Forms.For(b.def);
            if (f.form == Form3D.Model && f.model != null && f.model.HasModel)
            {
                return f.model.SeatHeight * f.model.Fit(b.def).y;
            }
            return Forms.HeightOf(b);
        }

        // Where a pawn drawn by the game as a flat sprite lies on this building (a bed, or one that shows who is inside):
        // the top of its bedding (ModelDef.beddingTop, else surfaceHeight), else the top of its 3D shape.
        public static float FlatLyingHeight(Building b)
        {
            if (b == null)
            {
                return 0f;
            }
            ThingForm f = Forms.For(b.def);
            if (On && f.form == Form3D.Model && f.model != null && f.model.HasModel)
            {
                return f.model.BeddingTop * f.model.Fit(b.def).y;
            }
            return Forms.HeightOf(b);
        }

        // (Api 28) A pawn's own slot on a bed with ModelDef.slots (a stacked bunk bed: its levels): every place that puts a
        // sleeper at a height asks here (the 3D figure, the flat sprite, the icons over the head). The slot is the game's
        // own (RestUtility.CurrentBed: the slot whose occupant the pawn is; a bunk bed mod numbers its levels so). False
        // without such a slot (no list, past its end, height -1): the bed's own surface then, as before.
        private static bool OwnSlot(Pawn pawn, Building b, out ModelEntry entry, out float height)
        {
            entry = null;
            height = 0f;
            Building_Bed bed = b as Building_Bed;
            if (pawn == null || bed == null || !View3D.Active)
            {
                return false;
            }
            ThingForm f = Forms.For(bed.def);
            if (f.form != Form3D.Model)
            {
                return false;
            }
            entry = ModelLibrary.StyledEntry(bed) ?? f.model;
            if (entry == null || !entry.HasModel || entry.def.slots == null)
            {
                return false;
            }
            int? slot;
            if (RestUtility.CurrentBed(pawn, out slot) != bed || !slot.HasValue)
            {
                return false;
            }
            return entry.SlotHeight(slot.Value, out height);
        }

        // (Api 28) Where a 3D figure lies in this bed, cells over the floor: its slot's height (ModelDef.slots), else the
        // bed's surface (Forms.HeightOf).
        public static float SleeperHeight(Pawn pawn, Building_Bed bed)
        {
            ModelEntry e;
            float h;
            if (OwnSlot(pawn, bed, out e, out h))
            {
                return h * e.Fit(bed.def).y;
            }
            return Forms.HeightOf(bed);
        }

        // (Api 28) Where this pawn, drawn as a flat sprite, lies on the building: on its slot (ModelDef.slots) as high over
        // the slot as the bedding is over the bed's surface (beddingTop - surfaceHeight), else FlatLyingHeight(b).
        public static float FlatLyingHeight(Building b, Pawn pawn)
        {
            ModelEntry e;
            float h;
            if (On && OwnSlot(pawn, b, out e, out h))
            {
                return (h + e.BeddingTop - e.SurfaceHeight) * e.Fit(b.def).y;
            }
            return FlatLyingHeight(b);
        }

        // (Api 28) How much higher the pawn's slot is than the bed's own surface, cells (0 without a slot of its own): the
        // icons over a sleeper's head go up by as much (Overhead).
        public static float SlotRise(Pawn pawn, Building_Bed bed)
        {
            ModelEntry e;
            float h;
            if (OwnSlot(pawn, bed, out e, out h))
            {
                return h * e.Fit(bed.def).y - Forms.HeightOf(bed);
            }
            return 0f;
        }

        // For the harness (slotcheck): a sleeper's slot and the heights worked out for it.
        public static string SlotReport(Pawn pawn)
        {
            int? slot = null;
            Building_Bed bed = pawn != null ? RestUtility.CurrentBed(pawn, out slot) : null;
            if (bed == null)
            {
                return "not in a bed";
            }
            ModelEntry e;
            float h;
            bool own = OwnSlot(pawn, bed, out e, out h);
            return "bed " + bed.def.defName + " slot " + (slot.HasValue ? slot.Value.ToString() : "none") + (own ? " own height " + h.ToString("0.00") : " no slot height")
                + ", surface " + Forms.HeightOf(bed).ToString("0.00") + ", figure " + SleeperHeight(pawn, bed).ToString("0.00")
                + ", flat " + FlatLyingHeight(bed, pawn).ToString("0.00") + ", rise " + SlotRise(pawn, bed).ToString("0.00")
                + ", icon " + Overhead.Height(pawn).ToString("0.00");
        }

        // For the harness (seatcheck).
        public static string Report(Pawn pawn)
        {
            Seat s;
            if (!Seated(pawn, out s))
            {
                Building e = pawn.Spawned && pawn.Map != null ? pawn.Position.GetEdifice(pawn.Map) : null;
                return "not seated (on " + (e != null ? e.def.defName : "nothing") + ", moving " + (pawn.pather != null && pawn.pather.MovingNow)
                    + ", posture " + pawn.GetPosture() + ")";
            }
            return "seated on " + s.thing.def.defName + " seat " + s.height.ToString("0.00") + " amount " + s.amount.ToString("0.00")
                + " shift " + s.shift.ToString("0.00") + " lift " + s.Lift.ToString("0.00") + " squash " + s.Squash.ToString("0.00");
        }
    }
}
