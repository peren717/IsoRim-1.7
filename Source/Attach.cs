using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    /// <summary>A named place on a host (Api 13, <see cref="Attachment"/>), measured from what IsoRim draws for it.</summary>
    public enum AttachAnchor
    {
        /// <summary>The host's ground point (the point under it): use the offset to place by hand.</summary>
        Ground,
        /// <summary>The top of the back: for an animal model, a ray straight down through its body at the middle of its trunk
        /// (between the hips), measured once per model (ModelDef.backHeight overrides it); a person figure's shoulders;
        /// a building's surface (ModelDef.surfaceHeight); a card (no model) an estimate from the sprite's size.</summary>
        Back,
        /// <summary>The top of the host as drawn (its model's highest point, a card's estimated top).</summary>
        Top,
    }

    /// <summary>How an attached person figure is posed on its anchor (Api 13).</summary>
    public enum AttachPose
    {
        /// <summary>Standing, feet on the anchor.</summary>
        Stand,
        /// <summary>Seated as on a chair: the base on the anchor, the body squashed a little (as IsoRim seats people).</summary>
        Sit,
        /// <summary>Seated astride, without legs: the hips on the anchor, the legs left out (a rider on a mount).</summary>
        SitNoLegs,
        /// <summary>Lying on its back along the host's facing.</summary>
        Lie,
    }

    /// <summary>Which way an attached figure faces (Api 13).</summary>
    public enum AttachFacing
    {
        /// <summary>Always the host's drawn facing (plus <see cref="Attachment.turn"/>).</summary>
        Host,
        /// <summary>The host's facing, but turned to the target while aiming, as standing people turn to aim and shoot.</summary>
        Aim,
        /// <summary>Its own facing (the game's rotation of the attached pawn), turned to the target while aiming.</summary>
        Own,
    }

    /// <summary>
    /// "This thing is attached to that host" (Api 13, <see cref="Api.AddAttachments"/>). IsoRim draws an attached pawn at
    /// the anchor, following the host's drawn place, facing, walking bob and floor, and everything that measures the pawn
    /// follows it too: picking and box selection, the name label, the held weapon and where its shots start, the bubbles
    /// over it and first person.
    /// </summary>
    public struct Attachment
    {
        /// <summary>What it is attached to (a spawned thing on the same map; anything else is ignored).</summary>
        public Thing host;
        public AttachAnchor anchor;
        /// <summary>Added to the anchor point, in the host's own space: x to its right, y up, z forward; cells, true heights.</summary>
        public Vector3 offset;
        public AttachPose pose;
        public AttachFacing facing;
        /// <summary>Degrees added to the host's facing (a seat facing sideways); clockwise seen from above.</summary>
        public float turn;
        /// <summary>The attached pawn leads the pair's movement (a rider steering its mount): the offset a floor layout gives
        /// it while it climbs (a stair, ladder or lift step: stacked floors) is drawn for the host too. False (the default):
        /// the attached thing takes the host's offset (a seat in a vehicle, someone carried).</summary>
        public bool leads;

        public Attachment(Thing host, AttachAnchor anchor, AttachPose pose, AttachFacing facing)
        {
            this.host = host;
            this.anchor = anchor;
            offset = Vector3.zero;
            this.pose = pose;
            this.facing = facing;
            turn = 0f;
            leads = false;
        }

        public Attachment(Thing host, Vector3 offset, AttachPose pose, AttachFacing facing)
            : this(host, AttachAnchor.Ground, pose, facing)
        {
            this.offset = offset;
        }
    }

    /// <summary>
    /// A host's place as IsoRim draws it now (Api 13, <see cref="Api.ReadHost"/>).
    /// </summary>
    public struct HostTransform
    {
        /// <summary>The host's base point, true space: x and z in map cells (its own floor's rows, as <see cref="Api.DrawModel(ModelHandle, Vector3, float, Vector3, Color, string)"/>
        /// takes them), y its height over its floor (in a jump, carried, flying; 0 standing on the ground).</summary>
        public Vector3 position;
        /// <summary>The way it is drawn facing, degrees clockwise from north (a model's turn as drawn; a card's facing).</summary>
        public float yaw;
        /// <summary>The rise of its body in step now, cells (in the anchors already).</summary>
        public float bob;
        /// <summary>Cells per model unit of its drawn model (1 for a card or flat thing).</summary>
        public float scale;
        /// <summary>Drawn as a 3D model now (false: an upright card or the game's flat drawing).</summary>
        public bool model;
        /// <summary>Its stacked floor (0 the ground or no floors; -1 between floors), whether that floor is drawn, and its
        /// peek fade slot (0 solid; above 0 drawn faded, <see cref="Api.PeekUpperFloors"/>). Things drawn on its rows fade alike.</summary>
        public int floor;
        public bool shown;
        public int fade;
        /// <summary>Its floor's and the layout's own offset (a pawn on a stair step), true space: where it shows over its rows.</summary>
        public Vector3 floorOffset;
        /// <summary>Heights over position.y of its anchors, bob included (Back, Top), and the back's point forward of the base
        /// in its own space (an animal's trunk middle).</summary>
        public float back;
        public float top;
        public Vector3 backAt;

        /// <summary>A point of the host's own space (x right, y up, z forward; cells) in true space.</summary>
        public Vector3 Point(Vector3 local)
        {
            return position + Quaternion.AngleAxis(yaw, Vector3.up) * local;
        }

        /// <summary>The anchor point plus offset (host space) in true space.</summary>
        public Vector3 Anchor(AttachAnchor anchor, Vector3 offset)
        {
            Vector3 a;
            switch (anchor)
            {
                case AttachAnchor.Back:
                    a = new Vector3(backAt.x, back, backAt.z);
                    break;
                case AttachAnchor.Top:
                    a = new Vector3(0f, top, 0f);
                    break;
                default:
                    a = Vector3.zero;
                    break;
            }
            return Point(a + offset);
        }
    }

    // Attachments (Api 13): one place for "this pawn is drawn on that host". Asked by everything that draws or measures a
    // pawn through Poses (People.Draw and MeasureWeapon, Poses.DrawnSpot: picking, first person, bubbles, muzzle height),
    // by the pawn's DrawPos while the 3D view is on (x and z: labels, brackets, aim, shots, motes; patched in only once an
    // addon registers), and by Floors.LayoutOffset (it takes its host's floor and stair offset). With no provider
    // registered nothing here runs.
    internal static class Attachments
    {
        private static Func<Thing, Attachment?>[] providers = new Func<Thing, Attachment?>[0];
        internal static bool Any;
        private static bool patched;
        private static int depth;

        // An attached figure's own drawn facing (Aim, Own), eased as standing figures turn.
        private class Turn
        {
            public float yaw;
            public int frame;
        }

        private static readonly Dictionary<int, Turn> turns = new Dictionary<int, Turn>();

        internal static void Add(Func<Thing, Attachment?> provider)
        {
            if (provider == null || Array.IndexOf(providers, provider) >= 0)
            {
                return;
            }
            Func<Thing, Attachment?>[] next = new Func<Thing, Attachment?>[providers.Length + 1];
            providers.CopyTo(next, 0);
            next[providers.Length] = provider;
            providers = next;
            Any = true;
            if (!patched)
            {
                patched = true;
                try
                {
                    new Harmony("isorim.attach").Patch(AccessTools.PropertyGetter(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.DrawPos)),
                        postfix: new HarmonyMethod(typeof(Attachments), nameof(DrawPos_Postfix)) { priority = Priority.Last });
                }
                catch (Exception e)
                {
                    Log.Error("[IsoRim] Attachments: patching DrawPos failed: " + e);
                }
            }
        }

        internal static void Remove(Func<Thing, Attachment?> provider)
        {
            int i = Array.IndexOf(providers, provider);
            if (i < 0)
            {
                return;
            }
            List<Func<Thing, Attachment?>> left = new List<Func<Thing, Attachment?>>(providers);
            left.RemoveAt(i);
            providers = left.ToArray();
            Any = providers.Length > 0;
        }

        // The providers' answer for t (the first that has one), if its host is usable: spawned on t's map, not t.
        internal static bool Of(Thing t, out Attachment a)
        {
            a = default(Attachment);
            if (!Any || t == null || !t.Spawned)
            {
                return false;
            }
            Func<Thing, Attachment?>[] all = providers;
            for (int i = 0; i < all.Length; i++)
            {
                Attachment? got;
                try
                {
                    got = all[i](t);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] An attachment provider failed: " + e, 93613001);
                    continue;
                }
                if (got.HasValue)
                {
                    a = got.Value;
                    Thing h = a.host;
                    bool ok = h != null && h != t && h.Spawned && h.Map == t.Map;
                    if (ok && a.leads && UnityData.IsInMainThread)
                    {
                        Lead l;
                        if (!leaders.TryGetValue(h.thingIDNumber, out l) || l.by != t || l.frame != Time.frameCount)
                        {
                            if (leaders.Count > 400)
                            {
                                leaders.Clear();
                            }
                            leaders[h.thingIDNumber] = new Lead { by = t, frame = Time.frameCount };
                        }
                    }
                    return ok;
                }
            }
            return false;
        }

        // The host t follows while the 3D view is on, or null.
        internal static Thing HostOf(Thing t)
        {
            Attachment a;
            return Any && View3D.Ready && Poses.On && Of(t, out a) ? a.host : null;
        }

        // Hosts led by an attached pawn (Attachment.leads), as last seen (frame).
        private struct Lead
        {
            public Thing by;
            public int frame;
        }

        private static readonly Dictionary<int, Lead> leaders = new Dictionary<int, Lead>();

        // Whose floor layout offset (a stair, ladder or lift step) t is drawn with (Floors.LayoutOffset; main thread only): an attached pawn
        // its host's, unless it leads; a host led by an attached pawn that pawn's; else its own.
        internal static Thing LayoutSource(Thing t)
        {
            if (!Any || t == null || !View3D.Ready || !Poses.On || !UnityData.IsInMainThread)
            {
                return t;
            }
            Attachment a;
            if (Of(t, out a))
            {
                return a.leads ? t : a.host;
            }
            Lead l;
            if (leaders.TryGetValue(t.thingIDNumber, out l) && Time.frameCount - l.frame <= 2 && l.by != null && l.by.Spawned
                && Of(l.by, out a) && a.host == t && a.leads)
            {
                return l.by;
            }
            return t;
        }

        internal struct Placed
        {
            public Attachment a;
            public HostTransform host;
            public Vector3 at;    // the anchor point, true space (x, z map cells; y over the floor)
            public float yaw;     // the attached figure's facing as drawn
        }

        // Where IsoRim puts an attached pawn now (3D view on, main thread). drawing: from its draw this frame (the host's step is moved on
        // and the figure's own facing eased; aim: the aim as drawn while aiming); else as last drawn.
        internal static bool Place(Thing t, bool drawing, float? aim, out Placed p)
        {
            p = default(Placed);
            if (!Any || !Poses.On || !View3D.Ready || depth > 3 || !UnityData.IsInMainThread || !Of(t, out p.a))
            {
                return false;
            }
            depth++;
            try
            {
                if (!Host(p.a.host, drawing, out p.host))
                {
                    return false;
                }
            }
            finally
            {
                depth--;
            }
            p.at = p.host.Anchor(p.a.anchor, p.a.offset);
            float hostYaw = p.host.yaw + p.a.turn;
            if (p.a.facing == AttachFacing.Host)
            {
                p.yaw = hostYaw;
                return true;
            }
            float want = aim ?? (p.a.facing == AttachFacing.Own ? t.Rotation.AsAngle : hostYaw);
            int id = t.thingIDNumber;
            int frame = Time.frameCount;
            Turn turn;
            if (!turns.TryGetValue(id, out turn))
            {
                turn = new Turn { yaw = want, frame = frame };
                if (turns.Count > 400)
                {
                    turns.Clear();
                }
                turns[id] = turn;
            }
            if (drawing && turn.frame != frame)
            {
                bool fresh = frame - turn.frame <= 2;
                turn.yaw = fresh ? Mathf.MoveTowardsAngle(turn.yaw, want, 500f * Time.deltaTime) : want;
                turn.frame = frame;
            }
            p.yaw = turn.yaw;
            return true;
        }

        // A host's drawn place now (HostTransform). advance: from a draw this frame (moves its step on, as its own draw does).
        internal static bool Host(Thing host, bool advance, out HostTransform h)
        {
            h = default(HostTransform);
            if (host == null || !host.Spawned || !View3D.Ready)
            {
                return false;
            }
            h.scale = 1f;
            Pawn pawn = host as Pawn;
            if (pawn != null)
            {
                IsoRimSettings st = IsoRimMod.Settings;
                bool asModel = Patches.DrawnAsModel(pawn);
                if (!(asModel && !pawn.RaceProps.Humanlike && st.animals3D && Animals.HostPose(pawn, advance, ref h))
                    && !(asModel && pawn.RaceProps.Humanlike && PersonPose(pawn, ref h)))
                {
                    CardPose(pawn, ref h);
                }
            }
            else
            {
                Vector3 c = host.DrawPos;
                h.position = new Vector3(c.x, 0f, c.z);
                h.yaw = host.Rotation.AsAngle;
                ThingForm f = Forms.For(host.def);
                h.model = f.form == Form3D.Model && f.model != null && f.model.HasModel;
                h.top = Forms.HeightOf(host);
                h.back = h.model && f.model.def.surfaceHeight >= 0f ? f.model.SurfaceHeight * f.model.Fit(host.def).y : h.top;
            }
            int k = Floors.On ? Floors.FloorOfRow(host.Position.z) : 0;
            h.floor = k;
            h.shown = k >= 0 && Floors.Shown(k);
            h.fade = k > 0 ? Floors.FadeOf(k) : 0;
            h.floorOffset = Floors.ThingOffset(host);
            return true;
        }

        // A person figure as a host (carried on the back): its tracked facing and step.
        private static bool PersonPose(Pawn pawn, ref HostTransform h)
        {
            Vector3 d = pawn.DrawPos;
            float yaw, phase, gait;
            if (!Animals.LastState(pawn, out yaw, out phase, out gait))
            {
                yaw = pawn.Rotation.AsAngle;
            }
            float size = AlienFigures.SizeOf(pawn);   // (a race's scale too, Api 25)
            h.position = new Vector3(d.x, 0f, d.z);
            h.yaw = yaw;
            h.bob = Mathf.Abs(Mathf.Sin(phase)) * 0.035f * size * gait;
            h.scale = size;
            h.model = true;
            h.back = (Poses.BodyTop - 0.04f) * size + h.bob;
            h.backAt = new Vector3(0f, 0f, -0.12f * size);
            h.top = 1.12f * size + h.bob;
            return true;
        }

        // How high an animal card's back and top are, of the sprite's draw height (the sprite's figure fills its frame
        // only in part, and stands on its feet at feetOffset below the middle).
        private const float CardBack = 0.36f;
        private const float CardTop = 0.62f;

        // A pawn drawn as an upright card (or flat): where the card stands (Patches.CardFeet), its facing, and the anchors
        // estimated from the sprite's size.
        private static void CardPose(Pawn pawn, ref HostTransform h)
        {
            float spriteHeight = Patches.CardHeight(pawn);
            Vector3 feet = Patches.CardFeet(pawn, pawn.DrawPos, spriteHeight);
            h.position = new Vector3(feet.x, 0f, feet.z);
            h.yaw = pawn.Rotation.AsAngle;
            h.model = false;
            h.back = CardBack * spriteHeight;
            h.top = CardTop * spriteHeight;
            // (a figure on it stands just in front of the card, toward the camera)
            Vector3 toCamera = -View3D.GroundForward * 0.08f;
            h.backAt = Quaternion.AngleAxis(-h.yaw, Vector3.up) * toCamera;
        }

        // The figure's own base (feet) height for its pose on the anchor, and how much higher its head is than standing on
        // the floor (picking, first person, bubbles). size: the figure's size.
        internal static float Feet(AttachPose pose, float anchorY, float size, float hipHeight)
        {
            return pose == AttachPose.SitNoLegs ? anchorY - (hipHeight + HipSink) * size : anchorY;
        }

        internal static float HeadRise(AttachPose pose, float anchorY, float size, float hipHeight)
        {
            switch (pose)
            {
                case AttachPose.Sit:
                    return anchorY - (1f - Poses.SitSquash) * Poses.BodyTop * size;
                case AttachPose.SitNoLegs:
                    return Feet(pose, anchorY, size, hipHeight);
                case AttachPose.Lie:
                    return anchorY - 0.9f * size;
                default:
                    return anchorY;
            }
        }

        // The hips sit a little into the host's back, so the curve of the back does not show a gap at the sides.
        internal const float HipSink = 0.03f;

        // The figure's hip height (top of the lower body), figure units before its build's scale.
        internal const float Hips = 0.28f;

        // Pawn_DrawTracker.DrawPos (3D view on, an attached pawn, main thread): x and z at its anchor, so everything that reads
        // the drawn position (labels, brackets, aim, motes) follows it. y (the game's altitude) is kept.
        public static void DrawPos_Postfix(Pawn ___pawn, ref Vector3 __result)
        {
            if (!Any || ___pawn == null || !View3D.Ready || !UnityData.IsInMainThread)
            {
                return;
            }
            try
            {
                Placed p;
                if (Place(___pawn, false, null, out p))
                {
                    __result.x = p.at.x;
                    __result.z = p.at.z;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Placing an attached pawn failed: " + e, 93613002);
            }
        }

        // For the harness: where t is attached now.
        internal static string Report(Thing t)
        {
            Placed p;
            if (!Place(t, false, null, out p))
            {
                return "not attached";
            }
            return "on " + p.a.host.LabelShort + " " + p.a.anchor + " " + p.a.pose + " " + p.a.facing + " at " + p.at.ToString("F2") + " yaw " + p.yaw.ToString("F0")
                + " host yaw " + p.host.yaw.ToString("F0") + " back " + p.host.back.ToString("F2") + " bob " + p.host.bob.ToString("F3") + " model " + p.host.model
                + " floor " + p.host.floor + " fade " + p.host.fade;
        }
    }
}
