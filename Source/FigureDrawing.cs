using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 62) A person's placing as People.Draw works it out, read without drawing (Api.ReadFigurePlacement,
    // FigureDrawContext.ReadPlacement). People.Draw runs as usual with active set and returns at the point where it would
    // start drawing (after the selection ring's placing is known), handing its locals to Fill. Nothing is drawn or noted;
    // the stride tracker steps once a frame whoever asks first (Animals.Advance), so reading the placing and drawing the
    // figure in the same frame never steps it twice.
    internal static class Placing
    {
        internal static bool active;
        internal static float stride;
        internal static float? face;

        private static bool filled;
        private static FigurePlacement result;

        // What the hooks and the selection ring need besides the public placing.
        internal static People.Look look;
        internal static float ringGround;
        internal static bool lying, crawling, seated, bodyShown;
        internal static Building_Bed sleptIn;   // (Api 67) the head-only bed People.Draw would draw the blanket of (SleeperBlanket)
        internal static float ground;           // and the ground it passes with it
        private static Pawn lastPawn;
        private static int lastFrame = -1;

        internal static void Fill(FigurePlacement p, People.Look l, float ring, bool isLying, bool isCrawling, bool isSeated, bool body,
            Building_Bed bed, float groundY)
        {
            sleptIn = bed;
            ground = groundY;
            result = p;
            look = l;
            ringGround = ring;
            lying = isLying;
            crawling = isCrawling;
            seated = isSeated;
            bodyShown = body;
            filled = true;
        }

        // The placing of a person IsoRim would draw as a figure now; false if it would not (or is a statue's subject).
        internal static bool Read(Pawn pawn, Vector3 drawLoc, bool neverAim, float strideWanted, float? faceWanted, out FigurePlacement p)
        {
            p = default(FigurePlacement);
            if (active || pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || !View3D.Ready)
            {
                return false;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            if (st == null || !st.models3D || !st.pawns3D)
            {
                return false;
            }
            active = true;
            stride = strideWanted;
            face = faceWanted;
            filled = false;
            sleptIn = null;
            try
            {
                People.Draw(pawn, drawLoc, neverAim);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Reading a figure's placing failed: " + e, 93618201);
                filled = false;
            }
            finally
            {
                active = false;
                face = null;
            }
            if (!filled)
            {
                lastPawn = null;
                return false;
            }
            p = result;
            lastPawn = pawn;
            lastFrame = Time.frameCount;
            return true;
        }

        // The last placing read is this pawn's of this frame (its look and ring fields are valid).
        internal static bool Holds(Pawn pawn)
        {
            return lastPawn == pawn && lastFrame == Time.frameCount;
        }
    }

    // (Api 62) The figure drawers (Api.AddFigureDrawer): asked where IsoRim would draw a figure, after its own gates; the first
    // that answers true draws it instead. Copy on write; with none registered the figure draws only read Count.
    internal static class FigureDrawers
    {
        private static FigureDrawer[] drawers = new FigureDrawer[0];
        private static FigureKinds[] kinds = new FigureKinds[0];
        internal static int Count;
        private static readonly FigureDrawContext ctx = new FigureDrawContext();
        private static bool busy;

        internal static void Add(FigureDrawer drawer, FigureKinds kind)
        {
            if (drawer == null)
            {
                throw new ArgumentNullException(nameof(drawer));
            }
            if (Array.IndexOf(drawers, drawer) >= 0)
            {
                throw new ArgumentException("[IsoRim] Api.AddFigureDrawer: this drawer is added already.");
            }
            if ((kind & (FigureKinds.People | FigureKinds.Animals | FigureKinds.Mechs)) == 0)
            {
                throw new ArgumentException("[IsoRim] Api.AddFigureDrawer: no figure kind given.");
            }
            FigureDrawer[] next = new FigureDrawer[drawers.Length + 1];
            FigureKinds[] nextKinds = new FigureKinds[kinds.Length + 1];
            drawers.CopyTo(next, 0);
            kinds.CopyTo(nextKinds, 0);
            next[drawers.Length] = drawer;
            nextKinds[kinds.Length] = kind;
            drawers = next;
            kinds = nextKinds;
            Count = next.Length;
        }

        internal static void Remove(FigureDrawer drawer)
        {
            int i = Array.IndexOf(drawers, drawer);
            if (i < 0)
            {
                return;
            }
            FigureDrawer[] next = new FigureDrawer[drawers.Length - 1];
            FigureKinds[] nextKinds = new FigureKinds[kinds.Length - 1];
            Array.Copy(drawers, 0, next, 0, i);
            Array.Copy(drawers, i + 1, next, i, drawers.Length - i - 1);
            Array.Copy(kinds, 0, nextKinds, 0, i);
            Array.Copy(kinds, i + 1, nextKinds, i, kinds.Length - i - 1);
            drawers = next;
            kinds = nextKinds;
            Count = next.Length;
        }

        // From People/Animals/Mechs.Draw: true when a drawer drew the figure (IsoRim then draws nothing of it).
        internal static bool Offer(Pawn pawn, Vector3 drawLoc, bool neverAim, FigureKinds kind)
        {
            if (busy)
            {
                return false;   // a figure drawn from inside a drawer (Api.DrawFigure): IsoRim's own
            }
            FigureDrawer[] all = drawers;
            FigureKinds[] allKinds = kinds;
            bool any = false;
            for (int i = 0; i < allKinds.Length; i++)
            {
                any |= (allKinds[i] & kind) != 0;
            }
            if (!any)
            {
                return false;
            }
            busy = true;
            bool drawn = false;
            try
            {
                ctx.Set(pawn, drawLoc, neverAim, kind);
                if (FigureScope.Active && kind == FigureKinds.People && ctx.Placed(out FigurePlacement root))
                {
                    FigureScope.Root(root.Position, root.Yaw);   // (Api.DrawFigure: what the drawer submits is moved to the caller's placing)
                }
                for (int i = 0; i < all.Length && !drawn; i++)
                {
                    if ((allKinds[i] & kind) == 0)
                    {
                        continue;
                    }
                    try
                    {
                        drawn = all[i](ctx);
                    }
                    catch (Exception e)
                    {
                        Log.Warning("[IsoRim] A figure drawer (" + (all[i].Method.DeclaringType != null ? all[i].Method.DeclaringType.FullName : "?")
                            + ") failed and is switched off; IsoRim draws its figures again: " + e);
                        Remove(all[i]);
                        drawn = false;
                    }
                }
                if (drawn && kind == FigureKinds.People && !ctx.ringNoted && !FigureScope.Active && ctx.Placed(out FigurePlacement p))
                {
                    // The selection ring round IsoRim's own placing of the body, unless the drawer noted its own.
                    SelectionRings.NotePerson(pawn, Placing.look, p.Position, p.Yaw, Placing.ringGround, p.BodyPlace, p.HeadPlace);
                }
            }
            finally
            {
                ctx.Clear();
                busy = false;
            }
            return drawn;
        }

        // The game's damage flash on a figure now, in sixteenths, as HitFlash.Begin works it out; 0 for a figure drawn for
        // another mod (Api.DrawFigure).
        internal static float FlashOf(Pawn pawn)
        {
            if (FigureScope.Active || pawn == null || pawn.Drawer == null || pawn.Drawer.renderer == null)
            {
                return 0f;
            }
            DamageFlasher flasher = pawn.Drawer.renderer.flasher;
            if (flasher == null || !flasher.FlashingNowOrRecently)
            {
                return 0f;
            }
            float s = 1f - flasher.CurColor.g;
            return s < 0.01f ? 0f : Mathf.RoundToInt(s * 16f) / 16f;
        }
    }

    // (Api 62) The other mods' rules for weapons' models (Api.AddWeaponModelRule), asked by WeaponModels.Uses first.
    internal static class WeaponRules
    {
        private static Func<ThingDef, bool?>[] rules = new Func<ThingDef, bool?>[0];
        internal static int Count;

        internal static void Add(Func<ThingDef, bool?> rule)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }
            if (Array.IndexOf(rules, rule) >= 0)
            {
                throw new ArgumentException("[IsoRim] Api.AddWeaponModelRule: this rule is added already.");
            }
            Func<ThingDef, bool?>[] next = new Func<ThingDef, bool?>[rules.Length + 1];
            rules.CopyTo(next, 0);
            next[rules.Length] = rule;
            rules = next;
            Count = next.Length;
        }

        internal static void Remove(Func<ThingDef, bool?> rule)
        {
            int i = Array.IndexOf(rules, rule);
            if (i < 0)
            {
                return;
            }
            Func<ThingDef, bool?>[] next = new Func<ThingDef, bool?>[rules.Length - 1];
            Array.Copy(rules, 0, next, 0, i);
            Array.Copy(rules, i + 1, next, i, rules.Length - i - 1);
            rules = next;
            Count = next.Length;
        }

        // The first answer, in the order added; null: IsoRim's own rules decide.
        internal static bool? Ask(ThingDef def)
        {
            Func<ThingDef, bool?>[] all = rules;
            for (int i = 0; i < all.Length; i++)
            {
                try
                {
                    bool? r = all[i](def);
                    if (r.HasValue)
                    {
                        return r;
                    }
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] A weapon model rule failed and is removed: " + e);
                    Remove(all[i]);
                }
            }
            return null;
        }
    }

    // (Api 62) The other mods' rules for items baked on the ground (Api.AddGroundModelRule), asked by the item print first
    // (Patches.TakePrintFrom_Prefix).
    internal static class GroundRules
    {
        private static Func<GroundModelContext, GroundModel?>[] rules = new Func<GroundModelContext, GroundModel?>[0];
        internal static int Count;

        internal static void Add(Func<GroundModelContext, GroundModel?> rule)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }
            if (Array.IndexOf(rules, rule) >= 0)
            {
                throw new ArgumentException("[IsoRim] Api.AddGroundModelRule: this rule is added already.");
            }
            Func<GroundModelContext, GroundModel?>[] next = new Func<GroundModelContext, GroundModel?>[rules.Length + 1];
            rules.CopyTo(next, 0);
            next[rules.Length] = rule;
            rules = next;
            Count = next.Length;
        }

        internal static void Remove(Func<GroundModelContext, GroundModel?> rule)
        {
            int i = Array.IndexOf(rules, rule);
            if (i < 0)
            {
                return;
            }
            Func<GroundModelContext, GroundModel?>[] next = new Func<GroundModelContext, GroundModel?>[rules.Length - 1];
            Array.Copy(rules, 0, next, 0, i);
            Array.Copy(rules, i + 1, next, i, rules.Length - i - 1);
            rules = next;
            Count = next.Length;
        }

        // Writes the item as a rule's model; false (nothing written) when no rule answers: IsoRim prints it as before.
        internal static bool Print(SectionLayer layer, Thing thing, ThingForm form, float resting)
        {
            if (ModelBaker.DamageMarks > 0 || ModelBaker.Ghost.a > 0f)
            {
                return false;
            }
            GroundModelContext c = new GroundModelContext
            {
                Thing = thing,
                IsoRimModel = form.form == Form3D.Model && form.model != null && form.model.HasModel ? Api.HandleFor(form.model.Pick(thing)) : null,
                WornLook = form.form == Form3D.Worn
            };
            Func<GroundModelContext, GroundModel?>[] all = rules;
            for (int i = 0; i < all.Length; i++)
            {
                GroundModel? answer;
                try
                {
                    answer = all[i](c);
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] A ground model rule failed and is removed: " + e);
                    Remove(all[i]);
                    continue;
                }
                if (!answer.HasValue)
                {
                    continue;
                }
                GroundModel g = answer.Value;
                if (g.Model == null || g.Model.model == null || g.Scale <= 0f)
                {
                    Log.Warning("[IsoRim] A ground model rule answered no model or no size for " + thing.def.defName + " and is removed.");
                    Remove(all[i]);
                    continue;
                }
                int mark = RoofClip.Mark(layer);
                try
                {
                    return ModelBaker.WritePosed(layer, thing, g.Model.model, thing.DrawPos, Quaternion.AngleAxis(g.Yaw, Vector3.up), g.Scale, resting,
                        g.Tint, 1f);
                }
                finally
                {
                    RoofClip.Tag(layer, mark, thing.Position);
                }
            }
            return false;
        }
    }
}
