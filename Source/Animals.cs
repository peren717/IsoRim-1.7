using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Animals drawn as 3D models in place of their sprites.
    //
    // An animal is a body model and one leg model drawn once at each hip (see the animal
    // entries in Defs/Models.xml). Unlike a sprite, which only has four facings, the model
    // turns to face the way the animal is actually walking, and its legs swing in step with
    // the ground it covers. Asleep it settles onto its belly with its legs folded away; downed
    // or dead it lies on its side (dead insects, deadBellyUp: on their backs, legs up and curled in).
    // Flying (birds) it rises over its cell with its legs tucked away; wings (wingModel, Api 14) open and beat.
    public static class Animals
    {
        // What has to be remembered about one animal from frame to frame.
        private class State
        {
            public float yaw;       // the way it is drawn facing, degrees clockwise from north
            public float heading;   // the way it is turning toward
            public float phase;     // where the legs are in their swing, radians
            public float gait;      // 0 standing still, 1 in full stride
            public float travel;    // ground covered while walking, in cells (wheels roll by it)
            public Vector3 last;    // where it was drawn last
            public int frame;       // and when
        }

        // How far a dead insect's legs are curled in over its belly (deadBellyUp), degrees.
        private const float BellyUpCurl = 30f;

        private static readonly Dictionary<int, State> states = new Dictionary<int, State>();
        private static readonly List<int> stale = new List<int>();

        // Api 19 (AddFacingHold): while any of these answers true for a pawn, its figure turns to the game's facing even
        // while it moves (a climber on a ladder faces the ladder), legs still.
        private static readonly List<Func<Pawn, bool>> facingHolds = new List<Func<Pawn, bool>>();

        internal static void AddFacingHold(Func<Pawn, bool> holds)
        {
            if (holds != null && !facingHolds.Contains(holds))
            {
                facingHolds.Add(holds);
            }
        }

        internal static void RemoveFacingHold(Func<Pawn, bool> holds)
        {
            facingHolds.Remove(holds);
        }

        // Tests (Api.Dev.DrawnYaw): the yaw last drawn and how many frames ago.
        internal static float? DrawnYaw(Pawn pawn, out int age)
        {
            age = -1;
            State st;
            if (pawn == null || !states.TryGetValue(pawn.thingIDNumber, out st))
            {
                return null;
            }
            age = Time.frameCount - st.frame;
            return st.yaw;
        }

        private static bool FacingHeld(Pawn pawn)
        {
            for (int i = 0; i < facingHolds.Count; i++)
            {
                try
                {
                    if (facingHolds[i](pawn))
                    {
                        return true;
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] A facing hold (Api.AddFacingHold) failed: " + e, 93621901);
                }
            }
            return false;
        }

        // Draws the animal at drawLoc. Returns false if it has no model, in which case the
        // game's sprite must be drawn as usual.
        public static bool Draw(Pawn pawn, Vector3 drawLoc)
        {
            ModelEntry entry;
            if (pawn.def == null || !ModelLibrary.TryGet(pawn.def, out entry) || !entry.def.animal || !entry.HasModel || !Corpses.ShownAsModel(pawn))
            {
                return false;
            }
            ModelDef def = entry.def;
            Model body = Corpses.Body(pawn, entry.Pick(pawn));
            if (body == null)
            {
                return false;
            }

            float size = def.scale * SizeOf(pawn);
            bool dead = pawn.Dead;
            bool lying = dead || pawn.GetPosture() != PawnPosture.Standing;
            bool bellyUp = dead && def.deadBellyUp;
            bool onSide = (dead || pawn.Downed) && !bellyUp;
            float lift = Carried.LiftOf(pawn, ref drawLoc) + Corpses.RestHeight(pawn);   // carried by a 3D pawn, or a corpse on a shelf
            float flight = FlightOf(pawn, ref drawLoc);       // in the air (before the facing: taking off is not a step north)
            float air = dead ? 0f : AirborneFactor(pawn);      // flying or in mid-jump, 0..1: legs tucked, wings open

            float yaw;
            float phase = 0f;
            float gait = 0f;
            // Attached to a host (Api 13, Attachments): at its anchor, facing as the attachment says, still (its host steps).
            Attachments.Placed att = default(Attachments.Placed);
            bool attached = !dead && Attachments.Any && Attachments.Place(pawn, true, null, out att);
            if (attached)
            {
                drawLoc.x = att.at.x;
                drawLoc.z = att.at.z;
                lift = att.at.y;
                flight = 0f;
                air = 0f;
                lying = att.a.pose == AttachPose.Lie;
                onSide = false;
                yaw = att.yaw;
            }
            else if (dead)
            {
                // Each carcass lies at its own slight angle.
                yaw = pawn.Rotation.AsAngle + (Hashing.Unit(pawn.thingIDNumber, 3, 9) - 0.5f) * 50f;
            }
            else
            {
                State st = Advance(pawn, drawLoc, lying, def.stride * size);
                yaw = st.yaw;
                phase = st.phase;
                gait = st.gait;
            }

            float ground = 0f;
            if (lying && !dead && !attached)
            {
                Building_Bed bed = pawn.CurrentBed();
                if (bed != null)
                {
                    ground = Poses.SleeperHeight(pawn, bed);   // or their slot's (Api 28)
                }
                if (pawn.ParentHolder is Building_HoldingPlatform platform)
                {
                    // Held on a holding platform: lying on its deck, facing as the game draws it (a shambler animal east), turned with the platform.
                    ground = Forms.HeightOf(platform);
                    yaw = (pawn.IsShambler ? Rot4.East.AsAngle : Rot4.South.AsAngle) + platform.Rotation.AsAngle;   // turned by HeldPawnBodyAngle
                    float swayAngle;
                    Vector3 swayShift;
                    if (HoldingChains.Sway(pawn, out swayAngle, out swayShift))
                    {
                        yaw += swayAngle;   // straining against the chains, as the game moves its sprite
                        drawLoc += swayShift;
                    }
                }
            }
            ground += lift + flight;
            Vector3 position = new Vector3(drawLoc.x, ground, drawLoc.z);
            Matrix4x4 sized = Matrix4x4.Scale(new Vector3(size, size, size));

            // How the whole animal is posed, before it is turned to face its heading.
            Matrix4x4 bodyPose;
            Matrix4x4 legPose = Matrix4x4.identity;
            bool drawLegs = true;
            if (bellyUp)
            {
                // Rolled half round about its forward axis, lifted so the top of its back rests on the ground.
                bodyPose = Matrix4x4.Translate(new Vector3(0f, body.maxY * size, 0f))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.forward));
                legPose = bodyPose;
                gait = 0f;
            }
            else if (onSide)
            {
                // Rolled over about the middle of its trunk, which then rests on the ground.
                bodyPose = Matrix4x4.Translate(new Vector3(0f, def.bodyHalfWidth * size, 0f))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(90f, Vector3.forward))
                    * Matrix4x4.Translate(new Vector3(0f, -def.bodyCentre * size, 0f));
                legPose = bodyPose;
                gait = 0f;
            }
            else if (lying)
            {
                bodyPose = Matrix4x4.Translate(new Vector3(0f, -def.legLength * size * 0.78f, 0f));
                drawLegs = false;
            }
            else if (air > 0f)
            {
                // In the air (flying, or in mid-jump in a PawnFlyer): no stepping; the legs are tucked away once it is
                // off the ground.
                bodyPose = Matrix4x4.identity;
                drawLegs = air < LegsTuckAt;
                gait = 0f;
            }
            else
            {
                // A slight rise and fall with each step.
                float bob = Mathf.Abs(Mathf.Sin(phase)) * 0.05f * def.legLength * size * gait;
                bodyPose = Matrix4x4.Translate(new Vector3(0f, bob, 0f));
            }

            // (Api 32) hovering: body and legs raised over the ground while it is up and about (not flying, attached or carried)
            float hover = Hover.Lift(pawn, def, size, !lying && !attached && air <= 0f && lift <= 0f, true);
            if (hover > 0f)
            {
                Matrix4x4 up = Matrix4x4.Translate(new Vector3(0f, hover, 0f));
                bodyPose = up * bodyPose;
                legPose = up * legPose;
            }

            // rotation turns the body model only (one built facing south); its hips and legs are given facing north.
            Matrix4x4 bodyTurn = def.rotation != 0f ? Matrix4x4.Rotate(Quaternion.AngleAxis(def.rotation, Vector3.up)) : Matrix4x4.identity;
            FigureScope.Root(position, yaw);   // (Api.DrawFigure: placed elsewhere)
            DynamicModels.DrawLocal(body, position, yaw, bodyPose * sized * bodyTurn, Color.white, null);
            WoundMarks.DrawAnimal(pawn, body, position, yaw, bodyPose * sized, bodyTurn, size);   // the game's wounds on the body
            if (ground - flight <= 0f)
            {
                // On the ground, or flying: the shadow stays on the ground (the blob is drawn at ground height).
                float spread = Hover.ShadowScale(hover);   // (Api 32) under a hovering animal, a little smaller
                GroundShadows.Blob(position, yaw, def.bodyHalfWidth * size * 1.5f * spread, def.bodyHalfLength * size * 1.25f * spread);
            }

            if (drawLegs && entry.leg != null && def.legs != null)
            {
                Matrix4x4 drop = Matrix4x4.Translate(new Vector3(0f, -def.legLength, 0f));
                for (int i = 0; i < def.legs.Count; i++)
                {
                    Vector3 hip = def.legs[i];

                    // Legs at opposite corners move together, as in a trot.
                    float beat = ((i + i / 2) % 2) * Mathf.PI;
                    float swing = gait > 0.01f ? Mathf.Sin(phase + beat) * Mathf.Min(def.legSwing, 40f) * Mathf.Clamp01(gait) : 0f;
                    Matrix4x4 local;
                    if (def.legFromHip)
                    {
                        // Turned toward the front by its spread plus the swing, about the upright axis (the foot keeps
                        // its height); the foot raised (about the leg's own side axis) while it swings forward.
                        float spread = def.legYaw != null && i < def.legYaw.Count ? def.legYaw[i] : 0f;
                        float raise = gait > 0.01f ? Mathf.Max(0f, Mathf.Cos(phase + beat)) * def.legLift * Mathf.Clamp01(gait) : 0f;
                        if (bellyUp)
                        {
                            // Curled in: bunched together, the feet drawn in over the belly.
                            spread *= 0.5f;
                            raise = -BellyUpCurl;
                        }
                        float forward = spread + swing;
                        Quaternion side = hip.x >= 0f ? Quaternion.AngleAxis(-forward, Vector3.up) : Quaternion.AngleAxis(180f + forward, Vector3.up);
                        local = legPose * sized * Matrix4x4.Translate(hip) * Matrix4x4.Rotate(side * Quaternion.AngleAxis(raise, Vector3.forward));
                    }
                    else
                    {
                        float splay = hip.x >= 0f ? def.legSplay : -def.legSplay;
                        if (bellyUp)
                        {
                            // Short straight legs: only leaning a little over the belly and toward its middle (more would lay them flat).
                            splay = hip.x >= 0f ? -BellyUpCurl * 0.4f : BellyUpCurl * 0.4f;
                            swing = Mathf.Abs(hip.z) > 0.02f ? Mathf.Sign(hip.z) * BellyUpCurl * 0.3f : 0f;
                        }
                        Quaternion turn = Quaternion.AngleAxis(splay, Vector3.forward) * Quaternion.AngleAxis(swing, Vector3.right);
                        local = legPose * sized * Matrix4x4.Translate(hip) * Matrix4x4.Rotate(turn) * drop;
                    }
                    DynamicModels.DrawLocal(Corpses.Body(pawn, entry.leg), position, yaw, local, Color.white, null);
                }
            }

            if (entry.wing != null)
            {
                DrawWings(pawn, entry, def, position, yaw, bodyPose * sized, air);
            }

            if (!lying && pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                // Held in the mouth, in front of the head: the front of the body, at the top of the legs.
                Vector3 front = Quaternion.AngleAxis(yaw, Vector3.up) * Vector3.forward;
                float reach = def.bodyHalfLength * size * 1.1f;
                Carried.Draw(pawn, new Vector3(position.x + front.x * reach, position.y + def.legLength * size + hover, position.z + front.z * reach), yaw);
            }
            return true;
        }

        // ---- as a host (Api 13: Attachments, Api.ReadHost)

        // Where the animal's model is drawn now, posed as Draw poses it (no drawing). advance: from a draw this frame (its
        // step and turn are moved on, as its own Draw does; drawn twice in a frame they are not moved again). False if it has
        // no model.
        internal static bool HostPose(Pawn pawn, bool advance, ref HostTransform h)
        {
            ModelEntry entry;
            if (pawn.def == null || !ModelLibrary.TryGet(pawn.def, out entry) || !entry.def.animal || !entry.HasModel || !Corpses.ShownAsModel(pawn))
            {
                return false;
            }
            ModelDef def = entry.def;
            Model body = Corpses.Body(pawn, entry.Pick(pawn));
            if (body == null)
            {
                return false;
            }
            float size = def.scale * SizeOf(pawn);
            bool dead = pawn.Dead;
            bool lying = dead || pawn.GetPosture() != PawnPosture.Standing;
            Vector3 drawLoc = pawn.DrawPos;
            float lift = Carried.LiftOf(pawn, ref drawLoc) + Corpses.RestHeight(pawn);
            float flight = FlightOf(pawn, ref drawLoc);
            float yaw = pawn.Rotation.AsAngle;
            float phase = 0f;
            float gait = 0f;
            State st;
            if (!dead && advance)
            {
                st = Advance(pawn, drawLoc, lying, def.stride * size);
                yaw = st.yaw;
                phase = st.phase;
                gait = st.gait;
            }
            else if (!dead && states.TryGetValue(pawn.thingIDNumber, out st))
            {
                yaw = st.yaw;
                phase = st.phase;
                gait = st.gait;
            }
            float ground = 0f;
            if (lying && !dead)
            {
                Building_Bed bed = pawn.CurrentBed();
                if (bed != null)
                {
                    ground = Poses.SleeperHeight(pawn, bed);   // or their slot's (Api 28)
                }
            }
            float rise;
            if (lying)
            {
                rise = dead || pawn.Downed ? 0f : -def.legLength * size * 0.78f;   // asleep on its belly; on its side: rough
            }
            else
            {
                rise = flight > 0f || (!dead && AirborneFactor(pawn) > 0f) ? 0f : Mathf.Abs(Mathf.Sin(phase)) * 0.05f * def.legLength * size * gait;
                if (flight <= 0f && lift <= 0f)
                {
                    rise += Hover.Lift(pawn, def, size, true, false);   // (Api 32) hovering, as its Draw last placed it
                }
            }
            Vector3 mid;
            float back = BackHeight(def, body, out mid);
            h.position = new Vector3(drawLoc.x, ground + lift + flight, drawLoc.z);
            h.yaw = yaw;
            h.bob = lying ? 0f : rise;
            h.scale = size;
            h.model = true;
            h.back = back * size + rise;
            h.backAt = new Vector3(mid.x * size, 0f, mid.z * size);
            h.top = TopHeight(def, body) * size + rise;
            return true;
        }

        private struct Back
        {
            public float height;
            public Vector3 mid;
            public float top;
        }

        private static readonly Dictionary<Model, Back> backs = new Dictionary<Model, Back>();

        // The top of the back (model units, as posed: def.rotation applied): a ray straight down through the body at the
        // middle of its trunk (between its hips), the highest face it meets; ModelDef.backHeight overrides it.
        internal static float BackHeight(ModelDef def, Model body, out Vector3 mid)
        {
            Back b = Measure(def, body);
            mid = b.mid;
            return def.backHeight >= 0f ? def.backHeight : b.height;
        }

        internal static float TopHeight(ModelDef def, Model body)
        {
            return Measure(def, body).top;
        }

        private static Back Measure(ModelDef def, Model body)
        {
            Back b;
            if (backs.TryGetValue(body, out b))
            {
                return b;
            }
            Vector3 mid = Vector3.zero;
            if (def.legs != null && def.legs.Count > 0)
            {
                for (int i = 0; i < def.legs.Count; i++)
                {
                    mid += def.legs[i];
                }
                mid /= def.legs.Count;
            }
            mid.x = 0f;   // on the middle line
            mid.y = 0f;
            Quaternion turn = Quaternion.AngleAxis(def.rotation, Vector3.up);
            Vector3[] v = body.positions;
            float best = -1f;
            float top = 0f;
            for (int i = 0; v != null && i + 2 < v.Length; i += 3)
            {
                Vector3 a = turn * v[i];
                Vector3 c1 = turn * v[i + 1];
                Vector3 c2 = turn * v[i + 2];
                top = Mathf.Max(top, Mathf.Max(a.y, Mathf.Max(c1.y, c2.y)));
                float d = (c1.z - c2.z) * (a.x - c2.x) + (c2.x - c1.x) * (a.z - c2.z);
                if (Mathf.Abs(d) < 1e-9f)
                {
                    continue;
                }
                float l1 = ((c1.z - c2.z) * (mid.x - c2.x) + (c2.x - c1.x) * (mid.z - c2.z)) / d;
                float l2 = ((c2.z - a.z) * (mid.x - c2.x) + (a.x - c2.x) * (mid.z - c2.z)) / d;
                float l3 = 1f - l1 - l2;
                if (l1 < 0f || l2 < 0f || l3 < 0f)
                {
                    continue;
                }
                best = Mathf.Max(best, l1 * a.y + l2 * c1.y + l3 * c2.y);
            }
            b.height = best > 0f ? best : def.bodyCentre + def.bodyHalfWidth;
            b.mid = mid;
            b.top = top > 0f ? top : body.maxY;
            backs[body] = b;
            return b;
        }

        // The facing, step and stride last worked out for a pawn (false: none yet).
        internal static bool LastState(Pawn pawn, out float yaw, out float phase, out float gait)
        {
            State st;
            if (states.TryGetValue(pawn.thingIDNumber, out st))
            {
                yaw = st.yaw;
                phase = st.phase;
                gait = st.gait;
                return true;
            }
            yaw = phase = gait = 0f;
            return false;
        }
        // Wings (Api 14, ModelDef.wingModel / wings): folded back on the ground, spread and beating in the air. air is how
        // far up the animal is (the game's PositionOffsetFactor, 0..1): the wings open as it leaves the ground and close
        // just before it lands, in step with the legs tucking away. The beat counts game ticks (frozen when paused, faster
        // at higher speeds, like the game's own flying frames), each animal at its own point in the beat.
        private static void DrawWings(Pawn pawn, ModelEntry entry, ModelDef def, Vector3 position, float yaw, Matrix4x4 body, float air)
        {
            if (def.wings == null)
            {
                return;
            }
            float open = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(air / WingOpenAt));
            float beat = 0f;
            if (open > 0f)
            {
                float t = Find.TickManager.TicksGame / 60f * def.wingBeat + Hashing.Unit(pawn.thingIDNumber, 5, 14);
                beat = Mathf.Sin(t * Mathf.PI * 2f) * def.wingFlap;
            }
            float fold = def.wingFold * (1f - open);
            float lift = -def.wingDroop * (1f - open) + beat * open;
            // Right wing: raised about the body's long axis, then swept back about the upright axis. A left wing is the
            // mirror image, drawn from the mirrored model with the mirrored turn (still a plain rotation).
            Matrix4x4 turn = Matrix4x4.Rotate(Quaternion.AngleAxis(fold, Vector3.up) * Quaternion.AngleAxis(lift, Vector3.forward));
            Matrix4x4 turnLeft = Mirror * turn * Mirror;
            Model right = Corpses.Body(pawn, entry.wing);
            Model left = Corpses.Body(pawn, entry.wingMirror ?? entry.wing);
            for (int i = 0; i < def.wings.Count; i++)
            {
                Vector3 hinge = def.wings[i];
                bool isLeft = hinge.x < 0f;
                Matrix4x4 local = body * Matrix4x4.Translate(hinge) * (isLeft ? turnLeft : turn);
                DynamicModels.DrawLocal(isLeft ? left : right, position, yaw, local, Color.white, null);
            }
        }

        private static readonly Matrix4x4 Mirror = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));

        // How far into the air (AirborneFactor) the wings are fully open, and where the legs tuck away.
        private const float WingOpenAt = 0.3f;
        private const float LegsTuckAt = 0.25f;

        // How far a pawn is into the air, 0 (on the ground) to 1 (fully up), one measure for the legs and the wings
        // (Api.AirborneFactor):
        //   - flying (Pawn_FlightTracker, 1.7 birds and locusts): the game's own take-off and landing curve,
        //     PositionOffsetFactor;
        //   - in mid-jump (carried by a PawnFlyer: jump abilities, jump packs, an addon's leap): eased in over the first
        //     JumpEase of the flight and out over the last, 1 in between, so a jump opens and closes the wings as a
        //     take-off and a landing do.
        public static float AirborneFactor(Pawn pawn)
        {
            if (pawn == null)
            {
                return 0f;
            }
            try
            {
                if (pawn.flight != null && !pawn.Dead)
                {
                    float f = pawn.flight.PositionOffsetFactor;
                    if (f > 0f)
                    {
                        return Mathf.Clamp01(f);
                    }
                }
                PawnFlyer flyer = pawn.ParentHolder as PawnFlyer;
                if (flyer != null)
                {
                    int total = flyerTotal(flyer);
                    if (total <= 0)
                    {
                        return 0f;
                    }
                    float t = Mathf.Clamp01((float)flyerTicks(flyer) / total);
                    return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(t, 1f - t) / JumpEase));
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Working out how far a pawn is in the air failed: " + e, 93611412);
            }
            return 0f;
        }

        // The share of a jump (PawnFlyer) over which the airborne factor rises from 0 to 1, and falls again at the end.
        private const float JumpEase = 0.2f;
        private static readonly AccessTools.FieldRef<PawnFlyer, int> flyerTicks = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlying");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> flyerTotal = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlightTime");

        // The same turning and stepping for anything else that walks (people). Gives back the
        // way it should face, where it is in its stride and how much of a stride it is in.
        // face: standing, turn toward this (degrees clockwise from north: a person aiming) instead of the game's facing.
        public static void Track(Pawn pawn, Vector3 drawLoc, bool lying, float stride, out float yaw, out float phase, out float gait, float? face = null)
        {
            State st = Advance(pawn, drawLoc, lying, stride, face);
            yaw = st.yaw;
            phase = st.phase;
            gait = st.gait;
        }

        // The same, also giving the ground covered so far (wheels roll by it).
        public static void Track(Pawn pawn, Vector3 drawLoc, bool lying, float stride, out float yaw, out float phase, out float gait, out float travel)
        {
            State st = Advance(pawn, drawLoc, lying, stride);
            yaw = st.yaw;
            phase = st.phase;
            gait = st.gait;
            travel = st.travel;
        }

        // How high a flying animal is drawn above the ground at full flight (cells). The game draws a flier
        // 0.6 cells up the screen (Pawn_DrawTracker.FlyingOffset) and a little bigger; from the 3D camera's
        // pitch a lift of this much looks about that high.
        internal const float FlightHeight = 0.75f;

        // An animal in the air (Pawn_FlightTracker, 1.7 birds: taking off, flying, landing). Takes the game's
        // screen offset back out of drawLoc, so the model stays over its cell, and gives the height instead,
        // eased in and out by the game's own take-off and landing curves (PositionOffsetFactor 0..1).
        private static float FlightOf(Pawn pawn, ref Vector3 drawLoc)
        {
            if (pawn.flight == null || pawn.Dead)
            {
                return 0f;
            }
            float f = pawn.flight.PositionOffsetFactor;
            if (f <= 0f)
            {
                return 0f;
            }
            drawLoc.z -= 0.6f * f;   // Pawn_DrawTracker.FlyingOffset
            return FlightHeight * f;
        }

        // Young animals are smaller. The game's own measure of body size shrinks much faster
        // than the animal looks, so it is eased.
        internal static float SizeOf(Pawn pawn)
        {
            float factor = 1f;
            if (pawn.ageTracker != null && pawn.ageTracker.CurLifeStage != null)
            {
                factor = pawn.ageTracker.CurLifeStage.bodySizeFactor;
            }
            return Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(factor));
        }

        // Moves one animal's facing and leg swing on by a frame.
        // drawLoc is where it is drawn; its steps and heading are measured without the game's jolts that are not walking
        // (a melee lunge, a hit's push, leaning out of cover: Moved), which still move it as drawn.
        private static State Advance(Pawn pawn, Vector3 drawLoc, bool lying, float stride, float? face = null)
        {
            int id = pawn.thingIDNumber;
            int frame = Time.frameCount;
            float facing = face ?? pawn.Rotation.AsAngle;
            drawLoc = Moved(pawn, drawLoc);

            State st;
            if (!states.TryGetValue(id, out st))
            {
                st = new State();
                st.yaw = facing;
                st.heading = facing;
                st.last = drawLoc;
                st.frame = frame;
                states[id] = st;
                Prune(frame);
                return st;
            }
            if (st.frame == frame)
            {
                return st;   // drawn twice in one frame
            }

            bool fresh = frame - st.frame <= 2;   // it was on screen a moment ago
            Vector3 move = drawLoc - st.last;
            move.y = 0f;
            float dist = move.magnitude;
            float dt = Time.deltaTime;

            if (!lying && facingHolds.Count > 0 && FacingHeld(pawn))
            {
                // Held to the game's facing while it moves (Api.AddFacingHold): it turns there smoothly, legs together.
                st.heading = facing;
                st.gait = Mathf.MoveTowards(st.gait, 0f, dt * 5f);
            }
            else if (!lying && fresh && dist > 0.0004f && dist < 1.5f)
            {
                // Walking: face the way it is going, and swing the legs by the ground covered.
                st.heading = Mathf.Atan2(move.x, move.z) * Mathf.Rad2Deg;
                // Capped per frame: at high game speed a fast animal covers several strides
                // between frames, and stepping the swing by all of that would make the legs
                // jump to random angles (a blur of spikes) instead of running.
                st.phase += Mathf.Min(dist / Mathf.Max(0.2f, stride) * 2f * Mathf.PI, 0.55f);
                if (st.phase > 6283f)
                {
                    st.phase -= 6283.1853f;   // a thousand whole swings
                }
                st.gait = Mathf.MoveTowards(st.gait, 1f, dt * 8f);
                // Capped like the swing, so wheels turn smoothly instead of spinning to random angles.
                st.travel += Mathf.Min(dist, 0.12f);
                if (st.travel > 10000f)
                {
                    st.travel -= 10000f;
                }
            }
            else if (lying || pawn.pather == null || !pawn.pather.Moving)
            {
                // Standing still: turn to the facing the game gives it and bring the legs together.
                st.heading = facing;
                st.gait = Mathf.MoveTowards(st.gait, 0f, dt * 5f);
            }
            // Otherwise it is in mid-walk but did not move this frame (the game is paused): hold the pose.

            st.yaw = fresh ? Mathf.MoveTowardsAngle(st.yaw, st.heading, 500f * dt) : st.heading;
            st.last = drawLoc;
            st.frame = frame;
            return st;
        }

        // The game's drawn position less its jitter (a melee lunge, a hit's push) and its lean out of cover (Pawn_DrawTracker.DrawPos):
        // where the pawn really is on its way.
        private static readonly AccessTools.FieldRef<Pawn_DrawTracker, JitterHandler> jitterOf = AccessTools.FieldRefAccess<Pawn_DrawTracker, JitterHandler>("jitterer");

        private static Vector3 Moved(Pawn pawn, Vector3 drawLoc)
        {
            Pawn_DrawTracker drawer = pawn.Drawer;
            if (drawer == null)
            {
                return drawLoc;
            }
            JitterHandler jitter = jitterOf(drawer);
            Vector3 jolt = (jitter != null ? jitter.CurrentOffset : Vector3.zero) + (drawer.leaner != null ? drawer.leaner.LeanOffset : Vector3.zero);
            return new Vector3(drawLoc.x - jolt.x, drawLoc.y, drawLoc.z - jolt.z);
        }

        // Forgets animals that have not been drawn for a while (dead, gone, or left behind on another map).
        private static void Prune(int frame)
        {
            if (states.Count < 400)
            {
                return;
            }
            stale.Clear();
            foreach (KeyValuePair<int, State> pair in states)
            {
                if (frame - pair.Value.frame > 600)
                {
                    stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                states.Remove(stale[i]);
            }
        }
    }
}
