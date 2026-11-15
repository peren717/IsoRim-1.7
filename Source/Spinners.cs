using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // A part of a model that turns (the lightball's ball, the watermill's wheel): the entry's spinModel, drawn every frame
    // over the baked body. The flat stand-in the game draws for the turning part is left out in 3D: its mote (spinMote), or
    // the flat drawing of the comp the part replaces (FlatStandIns: the watermill's nine blade quads, from
    // Patches.SpinFlatPostDraw_Prefix). Whether the part is drawn now is one rule (Shown), used by the drawing (DrawAll) and
    // by the leaving out (HidesFlat), so the part and the game's stand-in never both show or both go.
    //   Upright (as before Api 79): built in the model's space, turned about the upright axis through the model's origin,
    //     fitted and scaled as the model.
    //   On an axle (Api 79: spinAxis or spinPivot set, OnAxle): built in the model's space, turned about spinAxis through
    //     spinPivot. Never stretched: the pivot is placed and fitted as a point of the model (with wallFit its height follows
    //     the walls), the part keeps the def's scale. spinDips caps the pivot's height (DipCap). Its outline is built all
    //     round when it is loaded (ObjLoader.HulledAllRound).
    //   The angle (Angle): an addon's provider (Api.AddSpinAngle) first; else the def's spinSource (Source*): turning at
    //     spinSpeed while powered and wanted on (or always, spinAlways), at spinSpeed times the share of its full output, or
    //     the angle vanilla's watermill keeps (CompPowerPlantWater.spinPosition), read as it is each frame (stopping, turning
    //     back and half speed are the game's; with spinRate > 0 the bottom of the 3D wheel moves with the river's flow, as a
    //     real wheel in the river). A source that cannot be read (a game update, WatermillAngle on a thing without the
    //     watermill's comp) warns once and the part stands; an unknown spinSource is warned at load (Models.MakeEntry).
    // While it turns, its litMaterial faces (the lightball's lenses) light up (spinLit).
    // DrawTurning is the one drawing of a part turned about an axis through a pivot; the wind turbine's rotor (Turbines)
    // uses it with its own placing and speed rule.
    public static class Spinners
    {
        // spinSource values
        public const string SourcePower = "Power";
        public const string SourcePowerOutput = "PowerOutput";
        public const string SourceWatermill = "WatermillAngle";

        // With spinDips, the share of the part's reach below its axle that stays under the ground at least (the watermill's
        // paddle boards). Our own tuning value.
        private const float MinDipShare = 0.125f;

        // Cells round the camera's view rect in which a part is still drawn (Shown). Our own value: one cell, as the game's
        // own cull of what it draws every frame; the part's whole sweep is already in its rect (Reach).
        private const int ViewMargin = 1;

        // Our watermill binding (Defs/Models.xml), whose spinPivot z is copied from the game (CheckCopies).
        internal const string WatermillDef = "R3D_WatermillGenerator";

        // The comps whose own drawing is the flat stand-in for a part IsoRim turns in 3D: left out while the part is drawn.
        internal static readonly Type[] FlatStandIns = { typeof(CompPowerPlantWater) };

        private static List<ThingDef> defs;
        private static readonly HashSet<ThingDef> hiddenMotes = new HashSet<ThingDef>();
        private static readonly Dictionary<int, float> angles = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> lastTick = new Dictionary<int, int>();
        private static readonly List<Func<Thing, float?>> providers = new List<Func<Thing, float?>>();

        private struct Drawn
        {
            public float angle, height;
            public int frame;
            public string from;
        }

        private static readonly Dictionary<int, Drawn> drawn = new Dictionary<int, Drawn>();

        // Harness only: what Draw drew is kept for Report (drawn) while a probe sets this; off in play.
        internal static bool record;

        private static void Init()
        {
            defs = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ModelEntry entry;
                if (ModelLibrary.TryGet(def, out entry) && entry.spin != null && entry.HasModel)
                {
                    defs.Add(def);
                    ThingDef mote = entry.def.spinMote.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(entry.def.spinMote);
                    if (mote != null)
                    {
                        hiddenMotes.Add(mote);
                    }
                }
            }
        }

        // (Api 79) Whether the def's spin part turns on an axle (spinAxis or spinPivot set) rather than upright as before.
        public static bool OnAxle(ModelDef md)
        {
            return md != null && (md.spinAxis != Vector3.up || md.spinPivot != Vector3.zero);
        }

        // How far the part reaches from the axis line through pivot (model space), as built.
        public static float Radius(Model m, Vector3 pivot, Vector3 axis)
        {
            if (m == null || m.positions == null)
            {
                return 0f;
            }
            Vector3 a = axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.up;
            float r2 = 0f;
            for (int i = 0; i < m.positions.Length; i++)
            {
                Vector3 d = m.positions[i] - pivot;
                d -= a * Vector3.Dot(d, a);
                r2 = Mathf.Max(r2, d.sqrMagnitude);
            }
            return Mathf.Sqrt(r2);
        }

        // With spinDips: the highest the pivot may stand, so the part's lowest point (its reach straight down about the axle,
        // at the def's scale) stays MinDipShare of that reach under the ground.
        public static float DipCap(ModelEntry entry)
        {
            Vector3 a = entry.def.spinAxis.sqrMagnitude > 1e-8f ? entry.def.spinAxis.normalized : Vector3.up;
            float down = entry.spinRadius * entry.def.scale * Mathf.Sqrt(Mathf.Max(0f, 1f - a.y * a.y));
            return down * (1f - MinDipShare);
        }

        // The pivot's height for this thing now (true cells over its ground), axle parts.
        public static float PivotHeight(Thing t, ModelEntry entry)
        {
            ModelDef def = entry.def;
            float h = def.spinPivot.y * (entry.Fit(t.def) * def.scale).y + def.offset.y;
            return def.spinDips ? Mathf.Min(h, DipCap(entry)) : h;
        }

        // Whether s is a spinSource IsoRim knows ("" or null: Power).
        public static bool KnownSource(string s)
        {
            return s.NullOrEmpty() || s == SourcePower || s == SourcePowerOutput || s == SourceWatermill;
        }

        // The cells the thing's spin part can cover: its footprint, grown for a part on an axle by how far it reaches from
        // its axle (spinRadius at the def's scale: it may sweep past the footprint). An upright part stays in the footprint.
        public static CellRect Reach(Thing t, ModelEntry entry)
        {
            CellRect rect = t.OccupiedRect();
            return OnAxle(entry.def) ? rect.ExpandedBy(Mathf.CeilToInt(entry.spinRadius * entry.def.scale)) : rect;
        }

        // The view a part is drawn in now: the camera's view rect grown by ViewMargin.
        private static CellRect View()
        {
            return Find.CameraDriver.CurrentViewRect.ExpandedBy(ViewMargin);
        }

        // The one rule for whether the thing's spin part is drawn now (DrawAll), and so whether the game's flat stand-in for
        // it is left out (HidesFlat): the thing drawn as its model with a spin part, not fogged, not standing down for the
        // game's sprite on its card (EffectLift), and the cells the part can cover (Reach) in the view.
        private static bool Shown(Thing t, ThingForm form, CellRect view)
        {
            return form.form == Form3D.Model && form.model != null && form.model.spin != null && !t.Fogged()
                && !EffectLift.ShowsCard(t) && view.Overlaps(Reach(t, form.model));
        }

        // Whether the thing's part is turning now (Power source).
        public static bool Turning(Thing t)
        {
            CompPowerTrader power = t.TryGetComp<CompPowerTrader>();
            if (power == null || !power.PowerOn)
            {
                return false;
            }
            ThingWithComps twc = t as ThingWithComps;
            CompAutoPowered auto = twc != null ? twc.GetComp<CompAutoPowered>() : null;
            return auto == null || auto.WantsToBeOn;
        }

        // Called every frame while the 3D view is on.
        public static void DrawAll()
        {
            Map map = Find.CurrentMap;
            if (!IsoRimMod.Settings.models3D || map == null || Find.TickManager == null)
            {
                return;
            }
            try
            {
                if (defs == null)
                {
                    Init();
                }
                if (defs.Count == 0)
                {
                    return;
                }
                CellRect view = View();
                int now = Find.TickManager.TicksGame;
                for (int d = 0; d < defs.Count; d++)
                {
                    ThingForm form = Forms.For(defs[d]);
                    if (form.form != Form3D.Model || form.model == null || form.model.spin == null)
                    {
                        continue;
                    }
                    List<Thing> things = map.listerThings.ThingsOfDef(defs[d]);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (Shown(t, form, view))
                        {
                            Draw(t, form.model);
                        }
                        else if (EffectLift.ShowsCard(t))
                        {
                            lastTick[t.thingIDNumber] = now;   // (Api 55) stood down with the model, not turning
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Drawing turning parts failed: " + e, 93611241);
            }
        }

        // The part's angle now (degrees) and whether it turns now (lights spinLit). from: which rule answered (harness).
        private static float Angle(Thing t, ModelDef def, out bool on, out string from)
        {
            // an addon's provider (Api 79)
            for (int i = 0; i < providers.Count; i++)
            {
                Func<Thing, float?> p = providers[i];
                float? a;
                try
                {
                    a = p(t);
                }
                catch (Exception e)
                {
                    Log.Warning("[IsoRim] A spin angle provider (Api.AddSpinAngle) threw and was removed: " + e);
                    providers.RemoveAt(i--);
                    continue;
                }
                if (!a.HasValue)
                {
                    continue;
                }
                if (float.IsNaN(a.Value) || float.IsInfinity(a.Value))
                {
                    Log.Warning("[IsoRim] A spin angle provider (Api.AddSpinAngle) answered " + a.Value + " and was removed.");
                    providers.RemoveAt(i--);
                    continue;
                }
                on = Turning(t);
                from = "provider";
                return a.Value;
            }
            string source = def.spinSource.NullOrEmpty() ? SourcePower : def.spinSource;
            if (source == SourceWatermill)
            {
                on = Turning(t);
                from = SourceWatermill;
                float w;
                if (WatermillAngle(t, out w))
                {
                    return w;
                }
                if (waterSpin != null)
                {
                    Log.WarningOnce("[IsoRim] " + def.defName + ": spinSource " + SourceWatermill + " on " + t.def.defName
                        + ", which has no CompPowerPlantWater; its spin part stands still.", ("spinWatermill" + t.def.defName).GetHashCode());
                }
                return 0f;
            }
            if (source != SourcePower && source != SourcePowerOutput)
            {
                on = false;   // an unknown spinSource (warned at load, Models.MakeEntry): the part stands
                from = source;
                return 0f;
            }
            // turned by game ticks, so it keeps pace with the game speed and stands still while paused
            int now = Find.TickManager.TicksGame;
            int was;
            float angle;
            angles.TryGetValue(t.thingIDNumber, out angle);
            if (!lastTick.TryGetValue(t.thingIDNumber, out was))
            {
                was = now;
            }
            lastTick[t.thingIDNumber] = now;
            float speed = def.spinSpeed;
            if (source == SourcePowerOutput)
            {
                CompPowerTrader power = t.TryGetComp<CompPowerTrader>();
                float full = power != null ? Mathf.Abs(power.Props.PowerConsumption) : 0f;
                speed = full > 0f ? def.spinSpeed * Mathf.Abs(power.PowerOutput) / full : 0f;
                on = speed > 0f;
                from = SourcePowerOutput;
            }
            else
            {
                on = def.spinAlways || Turning(t);
                from = SourcePower;
            }
            if (now > was && on)
            {
                angle = Mathf.Repeat(angle + Mathf.Min(now - was, 60) / 60f * speed, 360f);
                angles[t.thingIDNumber] = angle;
            }
            return angle;
        }

        private static AccessTools.FieldRef<CompPowerPlantWater, float> waterSpin;
        private static bool triedWaterSpin;

        // The angle (degrees) vanilla's watermill turns its blades by now (CompPowerPlantWater.spinPosition, radians).
        // False if the thing has no such comp, or the field is gone (warned once here).
        public static bool WatermillAngle(Thing t, out float degrees)
        {
            degrees = 0f;
            if (!triedWaterSpin)
            {
                triedWaterSpin = true;
                try
                {
                    waterSpin = AccessTools.FieldRefAccess<CompPowerPlantWater, float>("spinPosition");
                }
                catch (Exception e)
                {
                    waterSpin = null;
                    Log.Warning("[IsoRim] The watermill's blade angle (CompPowerPlantWater.spinPosition) was not found; its wheel is drawn standing still: " + e.Message);
                }
            }
            CompPowerPlantWater comp = waterSpin != null ? t.TryGetComp<CompPowerPlantWater>() : null;
            if (comp == null)
            {
                return false;
            }
            degrees = waterSpin(comp) * Mathf.Rad2Deg;
            return true;
        }

        private static void Draw(Thing t, ModelEntry entry)
        {
            ModelDef def = entry.def;
            bool on;
            string from;
            float angle = Angle(t, def, out on, out from);
            float yaw = entry.Yaw(t.Rotation);
            if (entry.Swapped(t.def))
            {
                yaw += 90f;
            }
            Vector3 at = t.TrueCenter();
            // Its lit copy (the lightball's lenses) while it is on, also while the game is paused.
            Model part = ObjLoader.SheetTinted(on && entry.spinLit != null ? entry.spinLit : entry.spin, R3DTextures.SheetTint(t));
            if (OnAxle(def))
            {
                Vector3 fit = entry.Fit(t.def) * def.scale;
                float height = PivotHeight(t, entry);
                Vector3 along = new Vector3(def.spinPivot.x * fit.x + def.offset.x, 0f, def.spinPivot.z * fit.z + def.offset.z);
                DrawTurning(part, new Vector3(at.x, height, at.z), yaw, along, def.spinAxis, angle, def.scale, def.spinPivot,
                    R3DTextures.ModelTint(t), R3DTextures.ModelSwatch(t));
                if (record)
                {
                    drawn[t.thingIDNumber] = new Drawn { angle = angle, height = height, frame = Time.frameCount, from = from };
                }
                return;
            }
            Vector3 offset = Quaternion.AngleAxis(yaw, Vector3.up) * def.offset;
            DynamicModels.Draw(part, new Vector3(at.x + offset.x, offset.y, at.z + offset.z), yaw + angle,
                entry.Fit(t.def) * def.scale, R3DTextures.ModelTint(t), R3DTextures.ModelSwatch(t));
            if (record)
            {
                drawn[t.thingIDNumber] = new Drawn { angle = angle, height = offset.y, frame = Time.frameCount, from = from };
            }
        }

        // A part turned by angle (degrees) about axis through pivot (in the part's own space), scaled by scale, its pivot put
        // along (in the thing's turned frame) from position (true cells: x, z the thing's place, y the pivot's height), the
        // whole turned by yaw. With pivot zero the matrix is exactly the wind turbine's own (Turbines).
        internal static void DrawTurning(Model part, Vector3 position, float yaw, Vector3 along, Vector3 axis, float angle, float scale,
            Vector3 pivot, Color tint, string swatch)
        {
            Matrix4x4 local = Matrix4x4.Translate(along)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(angle, axis))
                * Matrix4x4.Scale(new Vector3(scale, scale, scale));
            if (pivot != Vector3.zero)
            {
                local *= Matrix4x4.Translate(-pivot);
            }
            DynamicModels.DrawLocal(part, position, yaw, local, tint, swatch);
        }

        // Whether a comp's own drawing is left out now because IsoRim draws the thing's spin part in its place (Shown, the
        // same rule as DrawAll; Patches.SpinFlatPostDraw_Prefix, FlatStandIns).
        public static bool HidesFlat(ThingComp comp)
        {
            Thing t = comp != null ? comp.parent : null;
            if (t == null || t.def == null || !IsoRimMod.Settings.models3D || Find.CameraDriver == null)
            {
                return false;
            }
            return Shown(t, Forms.For(t.def), View());
        }

        // Our watermill binding's spinPivot z is copied from the game: Copied from RimWorld CompPowerPlantWater.BladeOffset
        // (a private const, 2.36: how far forward of the centre the game draws its blades, CompPowerPlantWater.PostDraw).
        // Checked once at startup (SpinnerCopyCheck): one warning if the game's value differs.
        internal static void CheckCopies()
        {
            try
            {
                ModelDef md = DefDatabase<ModelDef>.GetNamedSilentFail(WatermillDef);
                if (md == null)
                {
                    return;
                }
                FieldInfo f = AccessTools.Field(typeof(CompPowerPlantWater), "BladeOffset");
                object v = f != null && f.IsLiteral ? f.GetRawConstantValue() : null;
                if (!(v is float game))
                {
                    Log.Warning("[IsoRim] " + WatermillDef + ": CompPowerPlantWater.BladeOffset (its spinPivot z is copied from it) was not found; the wheel may stand off the game's blade place.");
                }
                else if (Mathf.Abs(game - md.spinPivot.z) > 1e-4f)
                {
                    Log.Warning("[IsoRim] " + WatermillDef + ": spinPivot z " + md.spinPivot.z + " is copied from CompPowerPlantWater.BladeOffset, which is now "
                        + game + "; the wheel may need an update.");
                }
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Checking the watermill's copied blade offset failed: " + e.Message);
            }
        }

        // (Api 79) Angle providers (Api.AddSpinAngle).
        internal static void AddProvider(Func<Thing, float?> p)
        {
            if (p == null)
            {
                throw new ArgumentNullException(nameof(p));
            }
            if (providers.Contains(p))
            {
                throw new ArgumentException("This spin angle provider is already added.");
            }
            providers.Add(p);
        }

        internal static void RemoveProvider(Func<Thing, float?> p)
        {
            providers.Remove(p);
        }

        // Mote.DrawAt prefix: the game's flat stand-in for a part that turns in 3D is not drawn.
        public static bool MoteDrawAt_Prefix(Mote __instance)
        {
            return defs == null || hiddenMotes.Count == 0 || !View3D.Ready || !IsoRimMod.Settings.models3D
                || __instance == null || !hiddenMotes.Contains(__instance.def);
        }

        // For the harness: the turning state and angle of a thing's part (and for axle parts and watermills the angle and
        // pivot height last drawn, the game's own angle, the lowest point).
        public static string Report(Thing t)
        {
            float angle;
            angles.TryGetValue(t.thingIDNumber, out angle);
            ModelEntry entry;
            bool has = ModelLibrary.TryGet(t.def, out entry);
            bool lit = Turning(t) && has && entry.spinLit != null;
            string s = (Turning(t) ? "turning" : "still") + " angle " + angle.ToString("0.0") + (lit ? " lit" : " dark");
            Drawn d;
            if (drawn.TryGetValue(t.thingIDNumber, out d))
            {
                s += " drawn " + d.angle.ToString("0.000") + " from " + d.from + " frame " + d.frame + " pivotY " + d.height.ToString("0.000");
                if (has && OnAxle(entry.def))
                {
                    s += " radius " + entry.spinRadius.ToString("0.000") + " cap " + DipCap(entry).ToString("0.000")
                        + " lowest " + (d.height - entry.spinRadius * entry.def.scale).ToString("0.000");
                }
            }
            float w;
            if (WatermillAngle(t, out w))
            {
                s += " game " + w.ToString("0.000");
            }
            return s;
        }
    }

    // Startup: the copied values Spinners uses checked against the game (Spinners.CheckCopies), after the defs are loaded.
    [StaticConstructorOnStartup]
    internal static class SpinnerCopyCheck
    {
        static SpinnerCopyCheck()
        {
            Spinners.CheckCopies();
        }
    }
}
