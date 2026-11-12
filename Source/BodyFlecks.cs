using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Effects the game throws at a pawn's body, drawn at the body in the 3D view, not flat on the ground under it (body
    // flecks, 2026-11-09). One rule for every fleck the game draws over things (altitude MoteOverhead) that starts at a pawn
    // IsoRim draws as a 3D figure (a person, an animal, a mech; not one drawn as a card): its breath in the cold, and what
    // other mods throw there the same way. Called as the game creates a fleck (FleckManager.CreateFleck, while it ticks), after
    // every other rule that places flecks (Patches.BodyFleck_Prefix, registered last):
    //   - whose: the nearest such pawn whose drawn place (DrawPos) is within Reach of the fleck's start (more for a big body);
    //     none: left as it is
    //   - how high: the fleck's offset from the drawn place up the picture (+z) is laid on the figure as the game lays it on
    //     the sprite: from the body's middle, in the figure's units (FigureWeapons.Picture and the figure's size, as BodyAnims;
    //     an animal's or a mech's in cells, as its body animations), at most the figure's top (Interaction.PickHeight) and at
    //     least Lowest over the ground. Lying (downed, asleep, crawling): from a lying body's middle, at most its top (as the
    //     icons over it, Overhead). Across (x) it stays where the game put it
    //   - drawn on a card in front of the body, facing the camera (Emitters.RaiseRound, the card of Api 55's effects): the
    //     game's own movement of the fleck (a puff drifting the way the pawn faces) goes on over the card as on the sprite,
    //     and the fleck stays in front of the figure from every side, as it is drawn over the sprite. On the pawn's floor
    //     (Overhead.Floor)
    //   - left alone: what another rule placed already (Emitters.Raised: shots and their trails, turret flashes, the
    //     effects round a lifted model, blood's small puffs and food bits at a mouth, smoke and steam from nozzles), a fleck
    //     the shield dome holds back (ShieldHits), an explosion's own flecks (ExplosionFx.OwnFleck: the flash, smoke, cell flecks
    //     and their dust that DamageWorker.ExplosionStart / ExplosionAffectCell make themselves; 2026-11-11), puffs of a steam sprayer, what an effecter makes for something else than
    //     that pawn (its first target: another pawn, a cell) or at the work the pawn does (its second target a thing that is
    //     not a pawn: the frame being built, the rock being mined; Effecter_Prefix), and what starts at the feet (below
    //     Feet up the picture: it lies on the ground in the picture too). Other altitudes (footprints, splashes and ripples
    //     in water, filth, the ground's own: MoteLow and below) are not touched; the icons over a pawn (MetaOverlays) are
    //     Overhead's
    public static class BodyFlecks
    {
        private const float Reach = 0.6f;        // from the drawn place to a fleck of its body, cells (body size 1)
        private const float Lowest = 0.1f;       // over the ground, cells
        private const float Feet = -0.25f;       // up the picture from the drawn place: below this a fleck is at the feet
        private const float Middle = 0.56f;      // the body's middle on a person figure of size 1 (FigureWeapons, BodyAnims)
        private const float LyingMiddle = 0.18f; // a lying figure's middle (its top: Overhead.LyingTop)
        private const float BedMiddle = 0.4f;    // one lying in a bed (its top: Overhead.BedTop)

        // Harness switch (bodyflecks=0): as before (flat on the ground), for before / after sheets.
        public static bool Off;

        // Harness counter: flecks laid on a body so far.
        internal static int Lifted;

        // Patches.BodyFleck_Prefix. runOriginal false: the fleck is not made now (held back by an earlier prefix).
        public static void OnFleck(Map map, ref FleckCreationData data, bool runOriginal)
        {
            if (inventory && Probe.On && data.def != null)
            {
                string k = data.def.defName + " (" + data.def.altitudeLayer + ")";
                int n;
                created.TryGetValue(k, out n);
                created[k] = n + 1;
            }
            if (map == null || data.def == null || data.def.altitudeLayer != AltitudeLayer.MoteOverhead || (!View3D.Ready && !Probe.On))
            {
                return;
            }
            Vector3 off;
            Pawn p = Owner(map, data.spawnPosition, out off);
            if (p == null)
            {
                return;
            }
            string skip = !View3D.Ready ? "3D view off" : Skip(p, data, off, runOriginal);   // (the harness notes it all the same)
            float h = 0f;
            if (skip == null)
            {
                h = Height(p, off.z);
                Vector3 centre = p.DrawPos;
                // (up the picture is now the height: the fleck starts level with the body's middle, across where the game put it)
                data.spawnPosition = new Vector3(data.spawnPosition.x, data.spawnPosition.y, centre.z);
                Emitters.RaiseRound(data.spawnPosition, new Vector3(centre.x, 0f, centre.z), h, Radius(p), false, data.def, Overhead.Floor(p));
                Lifted++;
            }
            if (Probe.On)
            {
                Seen++;
                if (seenList.Count < 1000)
                {
                    seenList.Add(new KeyValuePair<Pawn, FleckDef>(p, data.def));
                }
                if (inventory)
                {
                    Note(data.def, p, off, skip, h);
                }
            }
        }

        // While an effecter runs (Effecter.EffectTick, Effecter.Trigger; only read and written while the game ticks): on, the
        // thing it is for (its first target; null: a cell) and the thing it is aimed at (its second; null: none or a cell).
        // A pawn's work runs the work's effecter with the pawn first and the work second (ToilEffects.WithEffect: building,
        // smoothing, cooking; JobDriver_Mine: mining).
        public struct EffecterScope
        {
            public bool on;
            public Thing a, b;
        }

        private static EffecterScope effecter;

        // Inside an effecter's EffectTick or Trigger now (also used by ExplosionFx: an effecter's flecks are never an explosion's own).
        internal static bool InEffecter => effecter.on;

        public static void Effecter_Prefix(TargetInfo A, TargetInfo B, out EffecterScope __state)
        {
            __state = effecter;
            effecter = new EffecterScope { on = true, a = A.HasThing ? A.Thing : null, b = B.HasThing ? B.Thing : null };
        }

        public static void Effecter_Finalizer(EffecterScope __state)
        {
            effecter = __state;
        }

        // Harness (bfuntil): MoteOverhead flecks seen at a pawn so far; those since ClearSeen, by pawn and def.
        internal static int Seen;
        private static readonly List<KeyValuePair<Pawn, FleckDef>> seenList = new List<KeyValuePair<Pawn, FleckDef>>();

        internal static void ClearSeen()
        {
            seenList.Clear();
        }

        internal static bool SeenAt(Pawn p, FleckDef def)
        {
            return seenList.Any(kv => kv.Key == p && (def == null || kv.Value == def));
        }

        // Why the fleck stays as it is (null: it is laid on the body).
        private static string Skip(Pawn p, FleckCreationData data, Vector3 off, bool runOriginal)
        {
            if (Off)
            {
                return "switched off";
            }
            if (!runOriginal)
            {
                return "held back (shield dome)";
            }
            if (Emitters.Raised(data.spawnPosition))
            {
                return "placed by another rule";
            }
            if (ExplosionFx.OwnFleck)
            {
                return "an explosion's own";   // its flash, smoke, cell flecks and dust lie round the blast, not on a body near it
            }
            if (Emitters.sprayer != null)
            {
                return "steam sprayer";
            }
            if (!Patches.DrawnAsModel(p) || Api.IsCard(p))
            {
                return "not a 3D figure";
            }
            if (effecter.on && effecter.a != p)
            {
                return "an effect of " + (effecter.a != null ? effecter.a.def.defName : "a cell");
            }
            if (effecter.on && effecter.b != null && !(effecter.b is Pawn))
            {
                return "at the work (" + effecter.b.def.defName + ")";   // (between the pawn and what it works on: a frame, a rock)
            }
            if (Standing(p) && off.z < Feet * Scale(p))
            {
                return "at the feet";
            }
            return null;
        }

        private static bool Standing(Pawn p)
        {
            return !p.Dead && !p.Downed && p.GetPosture() == PawnPosture.Standing;
        }

        // The picture's size against a person's of size 1: a person's figure size; an animal's or a mech's sprite grows
        // with its body.
        private static float Scale(Pawn p)
        {
            return p.RaceProps.Humanlike ? AlienFigures.SizeOf(p) : Mathf.Max(1f, Mathf.Sqrt(p.BodySize));
        }

        // From the body's middle out to its front, cells (as HitFx's blood).
        private static float Radius(Pawn p)
        {
            return Mathf.Clamp(0.18f * p.BodySize, 0.12f, 0.7f);
        }

        // How high over the pawn's floor a fleck offset up the picture by up from its drawn place is laid on the figure.
        internal static float Height(Pawn p, float up)
        {
            float middle, top, k;
            if (!Standing(p))
            {
                Building_Bed bed = p.CurrentBed();
                float slot = bed != null ? Poses.SlotRise(p, bed) : 0f;
                middle = (bed != null ? BedMiddle : LyingMiddle) + slot;
                top = (bed != null ? Overhead.BedTop : Overhead.LyingTop) + slot;
                k = p.RaceProps.Humanlike ? FigureWeapons.Picture * AlienFigures.SizeOf(p) : 1f;
            }
            else if (p.RaceProps.Humanlike)
            {
                float size = AlienFigures.SizeOf(p), headRise;
                Poses.DrawnSpot(p, out headRise);   // seated: on the seat
                middle = Middle * size + headRise;
                top = Mathf.Min(Interaction.PickHeight(p), FigureWeapons.FigureTop * size + headRise);
                k = FigureWeapons.Picture * size;
            }
            else
            {
                top = Interaction.PickHeight(p);   // (with a host's anchor, a hover)
                middle = top * 0.5f;
                k = 1f;
            }
            return Mathf.Clamp(middle + up * k, Lowest, Mathf.Max(Lowest, top));
        }

        // The nearest pawn whose drawn place is within its reach of at; off: at from that place (flat). One standing goes
        // before one lying (a colonist walking over a downed animal: drawn over it, its breath is its own).
        private static Pawn Owner(Map map, Vector3 at, out Vector3 off)
        {
            off = Vector3.zero;
            IntVec3 c = at.ToIntVec3();
            Pawn best = null;
            float bestD = float.MaxValue;
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    IntVec3 cell = new IntVec3(c.x + dx, 0, c.z + dz);
                    if (!cell.InBounds(map))
                    {
                        continue;
                    }
                    List<Thing> list = map.thingGrid.ThingsListAtFast(cell);
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn pawn = list[i] as Pawn;
                        if (pawn == null || !pawn.Spawned || pawn.RaceProps == null)
                        {
                            continue;
                        }
                        Vector3 d = at - pawn.DrawPos;
                        d.y = 0f;
                        float m = d.magnitude;
                        if (m <= Reach * Mathf.Max(1f, Mathf.Sqrt(pawn.BodySize)) && m + (Standing(pawn) ? 0f : 100f) < bestD)
                        {
                            best = pawn;
                            bestD = m + (Standing(pawn) ? 0f : 100f);
                            off = d;
                        }
                    }
                }
            }
            return best;
        }

        // ---- Harness (bodyfleckinv): every MoteOverhead fleck that starts at a pawn, by def and caller, and what was done.

        internal static bool inventory;

        private sealed class Entry
        {
            public string def, caller, pawn, what;
            public int n;
            public float minUp = float.MaxValue, maxUp = float.MinValue, minH = float.MaxValue, maxH = float.MinValue;
        }

        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();

        private static void Note(FleckDef def, Pawn p, Vector3 off, string skip, float h)
        {
            string caller = Caller();
            string kind = (p.RaceProps.Humanlike ? "person" : p.RaceProps.IsMechanoid ? "mech" : "animal") + (Standing(p) ? "" : " lying");
            string what = skip == null ? "lifted" : "skipped: " + skip;
            string key = def.defName + "|" + caller + "|" + kind + "|" + what;
            Entry e;
            if (!entries.TryGetValue(key, out e))
            {
                e = new Entry { def = def.defName, caller = caller, pawn = kind, what = what };
                entries[key] = e;
            }
            e.n++;
            e.minUp = Mathf.Min(e.minUp, off.z);
            e.maxUp = Mathf.Max(e.maxUp, off.z);
            if (skip == null)
            {
                e.minH = Mathf.Min(e.minH, h);
                e.maxH = Mathf.Max(e.maxH, h);
            }
        }

        // The first two methods up the stack that are not the fleck plumbing (FleckMaker, FleckManager, Harmony, IsoRim).
        private static string Caller()
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            List<string> found = new List<string>();
            for (int i = 0; frames != null && i < frames.Length && found.Count < 2; i++)
            {
                System.Reflection.MethodBase m = frames[i].GetMethod();
                Type t = m != null ? m.DeclaringType : null;
                if (t == null || t == typeof(FleckMaker) || t == typeof(FleckManager) || (t.Namespace ?? "").StartsWith("IsoRim") || (t.Namespace ?? "").StartsWith("HarmonyLib")
                    || m.Name.Contains("_Patch") || m.Name.StartsWith("DMD<"))
                {
                    continue;
                }
                found.Add(t.Name + "." + m.Name);
            }
            return found.Count == 0 ? "?" : string.Join(" < ", found);
        }

        // Every fleck created while noting, anywhere, by def (and altitude).
        private static readonly Dictionary<string, int> created = new Dictionary<string, int>();

        internal static void ResetInventory()
        {
            entries.Clear();
            created.Clear();
        }

        internal static void LogInventory(string tag)
        {
            Log.Message("[IsoRim probe] bodyfleckinv " + tag + ": " + entries.Count + " kinds, lifted so far " + Lifted + "; all flecks made: "
                + string.Join(", ", created.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)));
            foreach (Entry e in entries.Values.OrderBy(x => x.def).ThenBy(x => x.caller))
            {
                Log.Message("[IsoRim probe] bodyfleckinv\t" + tag + "\t" + e.def + "\t" + e.caller + "\t" + e.pawn + "\t" + e.what + "\t" + e.n
                    + "\tup " + e.minUp.ToString("0.00") + ".." + e.maxUp.ToString("0.00")
                    + (e.minH <= e.maxH ? "\theight " + e.minH.ToString("0.00") + ".." + e.maxH.ToString("0.00") : "\t-"));
            }
        }
    }
}
