using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // (Api 74) The colours of a 3D explosion: the fireball's ramp from the hottest (its light core) to the coldest (where a
    // shrinking piece ends), the smoke's darkest shade (it lightens as it rises, as FireFx's smoke), and the ground ring's
    // and debris' dust.
    internal sealed class ExplosionLook
    {
        public Color[] fire;
        public Color smoke;
        public Color dust;
        public int id;   // cache key of its meshes
    }

    // Ordinary explosions in 3D (explosions3D setting, default off; Api 74): a few big round outlined volumes in the fire's
    // yellow, orange and red that swell with the blast front, hold while the game's flash lasts, shrink and cool to red while
    // the smoke comes in, and are gone when it is fully in (FireEndRule); grey outlined smoke puffs that rise and drift as the
    // game's and shrink away ever faster over SmokeLife3D (shorter than the game's flat smoke); a ring of dust lumps on the ground that runs out with the front and on at the
    // dust's speed; and a debris chunk for each dust puff, flying out to the blast's edge over the dust's life.
    //
    // Covered: an explosion with visual effects whose damage def is explosive (DamageDef.isExplosive) and whose worker draws
    // the game's standard explosion (no class between it and DamageWorker overrides ExplosionStart,
    // ExplosionVisualEffectCenter or ExplosionAffectCell), on the map shown, with the 3D view on, unless a mod opted it out
    // (Api.SkipExplosionFx, Api.AddExplosionFxFilter). Vanilla: Bomb, BombSuper, Thump; not Flame, EMP, Stun, gas, Vaporize.
    //
    // Nothing is copied from the game: while the game's own DamageWorker.ExplosionStart and ExplosionAffectCell run for a
    // covered explosion, the flecks they make themselves (outside any effecter) are recorded, and the replaced ones made by the
    // game already at the end of their life (Hold): the flash (FleckDefOf.ExplosionFlash: its fade times the hold), the smoke
    // (FleckDefOf.Smoke: where, how big, which way and how fast, the def's fade-in) and the dust puff thrown with each cell's
    // fleck (FleckDefOf.DustPuff: the ring's speed, a debris chunk). The cell flecks themselves (the damage def's
    // explosionCellFleck) stay the game's, under the burst, so the cells hit still show. The front is every
    // Explosion.AffectCell on the burst's floor (Cell_Prefix), at the game's own tick for that cell. Everything else the
    // explosion does (sound, shake, heat, the blast mark, the water splash, a def's centre / interior / cell effecters and its
    // other flecks, a projectile's explosionEffect) is the game's. A cell on another stacked floor keeps the game's flecks.
    //
    // Drawn with the things (Patches.CullThings_Finalizer) from a list of bursts (as HitFx's blood): every part of a burst on
    // the burst's floor (Floors.RowMatrix of its row, DynamicModels.SubmitParts with that floor), hidden with its floor;
    // clipped under roofs as fire (RoofClip moving tag). Ready-made unit spheres, instanced (PartBatches): one per colour step
    // and size step, its outline shell made for that size so the line stays ObjLoader's width at any size (SizeStep).
    internal static class ExplosionFx
    {
        // ---- how long the fireball lasts: ONE rule (user 2026-11-11, explosion_anim_v1.gif: A)
        internal enum FireEnd
        {
            SmokeFadeIn,   // the smoke's fade-in time (the smoke is fully in when the fireball is gone)
            DustLife,      // a dust puff's whole life
        }

        internal const FireEnd FireEndRule = FireEnd.SmokeFadeIn;

        // ---- our own tuning (the look; sizes in units of the game's explosion radius)
        internal struct Blob
        {
            public float x, y, h, r;   // x east, y north, h up, radius
            public byte tone;          // 0 light core, 1 orange, 2 red
            public Blob(float x, float y, float h, float r, byte tone)
            {
                this.x = x; this.y = y; this.h = h; this.r = r; this.tone = tone;
            }
        }

        internal sealed class Variant
        {
            public float scale;     // a small factor around the game's radius
            public Blob[] blobs;
            public Vector4[] lobes; // a smoke puff's lobes: x, y, h, radius in puff diameters
            public float debris;    // degrees the debris' ways are turned
        }

        // Three looks of the same approved burst (user 2026-11-11): the blobs reshuffled inside the ball, another light core,
        // count +-1, size +-10 %, the smoke lobes and debris a little different. Each explosion picks one by a stable hash
        // (Look), turned and jittered a little by it. Row 0 is the approved concept (model-review\explosion, v1).
        private static readonly Variant[] Variants =
        {
            new Variant
            {
                scale = 1.00f, debris = 0f,
                blobs = new[]
                {
                    new Blob(-0.18f, -0.20f, 0.52f, 0.34f, 0), new Blob(0.05f, -0.05f, 0.86f, 0.25f, 0),
                    new Blob(-0.38f, 0.12f, 0.34f, 0.34f, 1), new Blob(0.30f, -0.25f, 0.36f, 0.33f, 1), new Blob(0.08f, 0.18f, 0.62f, 0.36f, 1),
                    new Blob(-0.15f, -0.45f, 0.22f, 0.27f, 1), new Blob(0.55f, 0.10f, 0.22f, 0.30f, 2), new Blob(-0.50f, -0.25f, 0.16f, 0.28f, 2),
                    new Blob(0.10f, 0.52f, 0.24f, 0.32f, 2), new Blob(-0.25f, 0.35f, 0.78f, 0.28f, 2), new Blob(0.35f, 0.28f, 0.80f, 0.26f, 2),
                },
                lobes = new[] { new Vector4(0f, 0f, 0f, 0.50f), new Vector4(0.42f, 0.05f, -0.06f, 0.34f), new Vector4(-0.36f, -0.08f, -0.04f, 0.32f) },
            },
            new Variant
            {
                scale = 1.10f, debris = 25f,
                blobs = new[]
                {
                    new Blob(0.15f, -0.22f, 0.48f, 0.33f, 0), new Blob(-0.08f, 0.00f, 0.82f, 0.27f, 0),
                    new Blob(0.40f, 0.10f, 0.36f, 0.33f, 1), new Blob(-0.32f, -0.22f, 0.38f, 0.32f, 1), new Blob(-0.05f, 0.25f, 0.60f, 0.34f, 1),
                    new Blob(0.20f, -0.45f, 0.20f, 0.26f, 1), new Blob(-0.55f, 0.05f, 0.20f, 0.30f, 2), new Blob(0.50f, -0.20f, 0.18f, 0.28f, 2),
                    new Blob(-0.10f, 0.50f, 0.22f, 0.31f, 2), new Blob(0.28f, 0.30f, 0.80f, 0.28f, 2),
                },
                lobes = new[] { new Vector4(0f, 0f, 0f, 0.48f), new Vector4(-0.40f, 0.06f, -0.05f, 0.35f), new Vector4(0.34f, -0.10f, -0.02f, 0.33f) },
            },
            new Variant
            {
                scale = 0.90f, debris = -20f,
                blobs = new[]
                {
                    new Blob(-0.05f, -0.25f, 0.40f, 0.36f, 0), new Blob(-0.20f, 0.00f, 0.92f, 0.24f, 0),
                    new Blob(0.35f, 0.00f, 0.45f, 0.32f, 1), new Blob(-0.40f, -0.05f, 0.40f, 0.31f, 1), new Blob(0.05f, 0.25f, 0.75f, 0.33f, 1),
                    new Blob(0.25f, -0.40f, 0.22f, 0.27f, 1), new Blob(-0.45f, -0.40f, 0.18f, 0.26f, 2), new Blob(0.55f, 0.25f, 0.22f, 0.29f, 2),
                    new Blob(-0.25f, 0.48f, 0.25f, 0.30f, 2), new Blob(0.20f, 0.40f, 0.88f, 0.25f, 2), new Blob(-0.42f, 0.28f, 0.70f, 0.27f, 2),
                    new Blob(0.45f, -0.15f, 0.70f, 0.24f, 2),
                },
                lobes = new[] { new Vector4(0f, 0f, 0f, 0.50f), new Vector4(0.38f, 0.10f, -0.04f, 0.32f), new Vector4(-0.30f, -0.12f, -0.06f, 0.30f), new Vector4(0.05f, 0.30f, 0.12f, 0.28f) },
            },
        };

        private const int VariantSalt = 9311;       // Hashing.Unit salt of the pick (the next salts: turn, jitters)
        private const float VariantJitter = 0.05f;  // blob centres moved up to this (x radius) each way
        private const float VariantSize = 0.08f;    // blob radii scaled by 1 +- this
        private const float DebrisJitter = 15f;     // each chunk's way turned by up to this (degrees)
        private static readonly float[] ToneAt = { 0.15f, 0.55f, 1f };   // where on the fire ramp (0 hottest .. 1 coldest) each tone sits
        private static readonly float[] FireBands = { 1f, 0.87f, 0.74f }; // the glow's three flat tones: upper, middle, lower faces
        // The 3D smoke's life (user's choice 2026-11-11, explosion_anim_v3.gif, v4.gif): the game's 9.7 s felt long in 3D. A puff grows
        // in over the game's own fade-in, then shrinks slowly at first and ever faster (power SmokeShrink) until it is gone at
        // SmokeLife3D. Rise and drift stay the game's. The flat explosions (option off, opted out) keep the game's smoke.
        internal const float SmokeLife3D = 3.0f;   // user 2026-11-11: 4.5 s (v3), then 3.0 s (v4)
        private const float SmokeShrink = 3f;      // user 2026-11-11: steeper than the 2 of v3
        private const float RingLump = 0.20f;       // the ground ring's dust lumps (cells)
        private const float RingStep = 1.3f;        // lumps spaced this many lump radii apart
        private const int MaxRingLumps = 48;        // more ring than this: bigger lumps instead
        private const float RingSquash = 0.7f;      // the lumps are this flat
        private const float RingLift = 0.35f;       // a lump's middle this many of its radii over the floor (the approved concept's)
        private const float LumpSmallest = 0.7f, LumpSpread = 0.5f;   // a lump 0.7 .. 1.2 x RingLump (fixed per lump)
        private const float RockInnermost = 0.75f, RockSpread = 0.5f; // a chunk's corners pushed to 0.75 .. 1.25 x its size
        private const float FlashTone = 0.5f;       // while the flash lasts each tone sits half way to the hottest
        private const float CellHalf = 0.5f;        // a cell hit is hit to its edge: the front reaches half a cell past its middle
        private const float RingMinRadius = 0.2f;   // no ring while the front is nearer than this (cells): only the centre cell
        private const float MinPart = SizeBase / 2f;   // a part smaller than half the smallest size step is not drawn (cells)
        private const float MinSpan = 1f / 60f;     // no span shorter than one tick (s)
        private const int AgedMargin = 2;           // a replaced fleck is made this many ticks past its life (never -1: "no override")
        private const float PuffTurnStep = 97f;     // degrees each further puff of a burst is turned (lobes not all alike)
        private const float Chunk = 0.13f;          // a debris chunk (cells)
        private const int MaxChunks = 24;           // per burst
        private const int MaxBursts = 48;
        private const int Steps = FireFx.Steps;     // colour steps along the fire ramp
        private const int SmokeSteps = FireFx.SmokeSteps;
        private const float SizeBase = 0.02f;       // smallest size step (cells, radius)
        private const float SizeRatio = 1.35f;      // each size step this much bigger: the outline within about +-15 % of its width
        private const int SizeSteps = 24;

        // ---- opt-outs and looks (Api 74)
        private static readonly HashSet<DamageDef> skipped = new HashSet<DamageDef>();
        private sealed class Filter
        {
            public Func<Explosion, bool> skip;
            public string owner;
            public bool failed;
        }
        private static readonly List<Filter> filters = new List<Filter>();
        private static readonly Dictionary<DamageDef, ExplosionLook> apiLooks = new Dictionary<DamageDef, ExplosionLook>();
        private static readonly Dictionary<DamageDef, ExplosionLook> looks = new Dictionary<DamageDef, ExplosionLook>();
        private static int lookIds;

        internal static void Skip(DamageDef def)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            skipped.Add(def);
        }

        internal static void AddFilter(Func<Explosion, bool> skip, string owner)
        {
            if (skip == null)
            {
                throw new ArgumentNullException(nameof(skip));
            }
            filters.Add(new Filter { skip = skip, owner = owner });
        }

        internal static void SetLook(DamageDef def, Color[] fire, Color smoke, Color dust)
        {
            if (def == null || fire == null || fire.Length == 0)
            {
                throw new ArgumentException("Api.AddExplosionLook: a damage def and at least one fire colour are needed");
            }
            apiLooks[def] = new ExplosionLook { fire = (Color[])fire.Clone(), smoke = smoke, dust = dust, id = ++lookIds };
            looks.Remove(def);
        }

        // The look of a damage def's explosions. Fire: IsoRim's fire ramp (FireFx) is the 3D form of the game's standard
        // explosion colour, the Bomb def's explosionColorCenter; a def with another centre colour gets the ramp turned to its
        // hue and its saturation scaled as much as its centre's against Bomb's (a blue blast is blue), one that keeps the
        // DamageDef default (white: the game draws the cell fleck untinted) the ramp as it is. Dust: the def's
        // explosionColorEdge (default white: Bomb's). Smoke: FireFx's. An Api look wins.
        internal static ExplosionLook LookOf(DamageDef def)
        {
            ExplosionLook l;
            if (apiLooks.TryGetValue(def, out l) || looks.TryGetValue(def, out l))
            {
                return l;
            }
            DamageDef std = DamageDefOf.Bomb;
            Color center = def.explosionColorCenter, edge = def.explosionColorEdge;
            Color[] ramp = FireFx.StandardFlame;
            if (std != null && center != DefaultCenter && center != std.explosionColorCenter)
            {
                float h, s, v, h0, s0, v0;
                Color.RGBToHSV(center, out h, out s, out v);
                Color.RGBToHSV(std.explosionColorCenter, out h0, out s0, out v0);
                float turn = h - h0, ks = s0 > 0f ? s / s0 : 1f;
                Color[] turned = new Color[ramp.Length];
                for (int i = 0; i < ramp.Length; i++)
                {
                    float rh, rs, rv;
                    Color.RGBToHSV(ramp[i], out rh, out rs, out rv);
                    turned[i] = Color.HSVToRGB(Mathf.Repeat(rh + turn, 1f), Mathf.Clamp01(rs * ks), rv);
                }
                ramp = turned;
            }
            Color dust = edge != DefaultEdge ? edge : std != null ? std.explosionColorEdge : edge;
            l = new ExplosionLook { fire = ramp, smoke = FireFx.StandardSmoke, dust = dust, id = ++lookIds };
            looks[def] = l;
            return l;
        }

        // The DamageDef class defaults, read from the class itself (a def that sets neither).
        private static Color? defaultCenter, defaultEdge;
        private static Color DefaultCenter => defaultCenter ?? (defaultCenter = new DamageDef().explosionColorCenter).Value;
        private static Color DefaultEdge => defaultEdge ?? (defaultEdge = new DamageDef().explosionColorEdge).Value;

        // ---- which explosions
        private static readonly Dictionary<Type, bool> standardWorker = new Dictionary<Type, bool>();
        private static readonly string[] VisualMethods = { "ExplosionStart", "ExplosionVisualEffectCenter", "ExplosionAffectCell" };

        private static bool StandardWorker(Type t)
        {
            bool yes;
            if (standardWorker.TryGetValue(t, out yes))
            {
                return yes;
            }
            yes = true;
            for (Type k = t; k != null && k != typeof(DamageWorker) && yes; k = k.BaseType)
            {
                foreach (string name in VisualMethods)
                {
                    foreach (MethodInfo m in k.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        yes &= m.Name != name;
                    }
                }
            }
            standardWorker[t] = yes;
            return yes;
        }

        internal static bool On => IsoRimMod.Settings.explosions3D && View3D.Ready && !failed;

        // Whether this explosion is drawn in 3D (its game flecks held back). Opted out: never, and nothing else is touched.
        private static bool Covered(DamageWorker worker, Explosion e)
        {
            if (!On || e == null || !e.doVisualEffects || e.Map == null || e.Map != Find.CurrentMap || e.damType == null || !e.damType.isExplosive
                || !StandardWorker(worker.GetType()))
            {
                return false;
            }
            if (skipped.Contains(e.damType))
            {
                OptedOut++;
                return false;
            }
            for (int i = 0; i < filters.Count; i++)
            {
                Filter f = filters[i];
                if (f.failed)
                {
                    continue;
                }
                bool skip;
                try
                {
                    skip = f.skip(e);
                }
                catch (Exception ex)
                {
                    f.failed = true;
                    Log.Warning("[IsoRim] An Api.AddExplosionFxFilter filter (" + f.owner + ") failed and is ignored from now on: " + ex);
                    continue;
                }
                if (skip)
                {
                    OptedOut++;
                    return false;
                }
            }
            return true;
        }

        // ---- the bursts
        internal struct Puff
        {
            public float x, z, scale, angle, speed, spin;
        }

        internal struct Dust
        {
            public float x, z, angle, speed, turn;
            public int born;
        }

        internal sealed class Burst
        {
            public Explosion explosion;   // while it spawns its cells; null for an Api burst
            public Map map;
            public Vector3 centre;        // x, z: the cell's middle in the game's rows; y: a stair's or bridge's rise (cells)
            public int row;
            public int floor;
            public float radius;
            public int born;
            public int seed;
            public ExplosionLook look;
            public Variant variant;
            public float turn;
            public Blob[] blobs;          // turned and jittered (x radius)
            public float top;             // the highest blob's middle (x radius): where the smoke starts
            public FleckDef flash, cellFleck, smoke, dust;
            public float front;           // the farthest cell hit so far (cells)
            public int frontEnd = -1;     // tick of its last cell, once the explosion is gone
            public int lastCell;
            public bool instant;          // an Api burst: no front, full size from the start
            public bool smoke3D = true;   // drawn with 3D smoke (OwnLookSource: not when the source has a look of its own)
            public readonly List<Puff> puffs = new List<Puff>();
            public readonly List<Dust> dusts = new List<Dust>();
        }

        private static readonly List<Burst> bursts = new List<Burst>();
        private static readonly Dictionary<int, Burst> byExplosion = new Dictionary<int, Burst>();
        private static bool failed;

        // Harness / Api.Dev
        internal static int Made, Held, OptedOut, DrawnLast;
        internal static int Started;            // every explosion's ExplosionStart so far (covered or not)
        internal static IntVec3 LastStart;      // the last one's cell
        internal static bool LastSmoke3D;       // whether the last burst made has 3D smoke

        // The parts a burst gets, decided once here (user 2026-11-12): an explosion whose source brings an effecter look of its
        // own (its projectile's explosionEffect, or the explosionEffect of the CompExplosive detonating now: in the game only
        // the antigrain warhead's GiantExplosion, as shell and as IED) gets no 3D smoke; its fireball, ring and debris stay,
        // and so does the game's own effecter. Its flat smoke flecks are replaced all the same.
        internal static int detonatingOwnLook;   // CompExplosive.Detonate with an explosionEffect running now (Patches)

        private static bool OwnLookSource(Explosion e)
        {
            return e.projectile?.projectile?.explosionEffect != null || detonatingOwnLook > 0;
        }
        internal static int Live => bursts.Count;

        private static Burst NewBurst(Map map, IntVec3 cell, float radius, DamageDef def, Thing at)
        {
            CheckGame();
            if (bursts.Count >= MaxBursts)
            {
                return null;
            }
            Matrix4x4 extra = at != null ? Overhead.Floor(at) : Matrix4x4.identity;   // a stair's rise, a bridge's deck
            int tick = Find.TickManager.TicksGame;
            unchecked
            {
                int key = cell.x * 65536 + cell.z, salt = tick ^ (map.uniqueID * 7919);
                Variant v = Variants[Hashing.Index(key, salt, VariantSalt, Variants.Length)];
                Burst b = new Burst
                {
                    map = map,
                    centre = new Vector3(cell.x + 0.5f, extra.m13 * View3D.Squash, cell.z + 0.5f),
                    row = cell.z,
                    floor = Floors.On ? Floors.FloorOfRow(cell.z) : 0,
                    radius = radius,
                    born = tick,
                    seed = key ^ salt,
                    look = LookOf(def),
                    variant = v,
                    turn = Hashing.Unit(key, salt, VariantSalt + 1) * 360f,
                };
                float ca = Mathf.Cos(b.turn * Mathf.Deg2Rad), sa = Mathf.Sin(b.turn * Mathf.Deg2Rad);
                b.blobs = new Blob[v.blobs.Length];
                for (int i = 0; i < v.blobs.Length; i++)
                {
                    Blob o = v.blobs[i];
                    float jx = (Hashing.Unit(b.seed, i, VariantSalt + 2) - 0.5f) * 2f * VariantJitter;
                    float jz = (Hashing.Unit(b.seed, i, VariantSalt + 3) - 0.5f) * 2f * VariantJitter;
                    float jr = (Hashing.Unit(b.seed, i, VariantSalt + 4) - 0.5f) * 2f * VariantSize;
                    float x = o.x + jx, z = o.y + jz;
                    b.blobs[i] = new Blob(x * ca - z * sa, x * sa + z * ca, o.h, o.r * (1f + jr), o.tone);
                    b.top = Mathf.Max(b.top, o.h);
                }
                bursts.Add(b);
                Made++;
                return b;
            }
        }

        // (Api 74) A burst for a mod's own explosion that does not go through the game's DamageWorker: fireball and ground ring
        // at once at full size (no front, no smoke or debris: those come from the game's own fleck calls).
        internal static bool Draw3D(Map map, IntVec3 cell, float radius, DamageDef def)
        {
            if (!On || map == null || map != Find.CurrentMap || def == null || radius <= 0f || skipped.Contains(def))
            {
                return false;
            }
            Burst b = NewBurst(map, cell, radius, def, null);
            if (b == null)
            {
                return false;
            }
            b.instant = true;
            b.front = radius;
            b.frontEnd = b.born;
            b.flash = FleckDefOf.ExplosionFlash;
            return true;
        }

        // ---- the scope: while the game's own explosion visuals run (Patches)
        internal struct Scope
        {
            public Burst burst;
            public bool cell;
            public IntVec3 at;
            public bool dustOpen;
            public bool any;    // inside an explosion's ExplosionStart / ExplosionAffectCell (also an uncovered one: BodyFlecks)
            public Explosion explosion;
        }

        private static Scope scope;

        // The bursts belong to one game: a new or loaded game starts with none (no old maps kept, no stale explosion ids).
        private static Game game;

        private static void CheckGame()
        {
            if (Current.Game != game)
            {
                game = Current.Game;
                bursts.Clear();
                byExplosion.Clear();
            }
        }

        internal static void Start_Prefix(DamageWorker __instance, Explosion explosion, out Scope __state)
        {
            __state = scope;
            scope = new Scope { any = true, explosion = explosion };
            Started++;
            LastStart = explosion != null ? explosion.Position : IntVec3.Invalid;
            try
            {
                CheckGame();
                if (!Covered(__instance, explosion))
                {
                    return;
                }
                Burst b = NewBurst(explosion.Map, explosion.Position, explosion.radius, explosion.damType, explosion);
                if (b == null)
                {
                    return;
                }
                b.explosion = explosion;
                b.cellFleck = explosion.damType.explosionCellFleck;
                b.smoke3D = !OwnLookSource(explosion);
                LastSmoke3D = b.smoke3D;
                byExplosion[explosion.thingIDNumber] = b;
                scope.burst = b;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // A cell the explosion reaches (Explosion.AffectCell, at the game's own tick for it): the front, whatever the def throws
        // there (a fleck, a mote or nothing). Only this explosion's own burst, on its floor, with the option on.
        internal static void Cell_Prefix(Explosion explosion, IntVec3 c, out Scope __state)
        {
            __state = scope;
            scope = new Scope { any = true, cell = true, at = c, explosion = explosion };
            try
            {
                CheckGame();
                Burst b;
                if (On && explosion != null && byExplosion.TryGetValue(explosion.thingIDNumber, out b) && b.explosion == explosion
                    && (!Floors.On || Floors.FloorOfRow(c.z) == b.floor))
                {
                    scope.burst = b;
                    b.front = Mathf.Max(b.front, (c - explosion.Position).LengthHorizontal);
                    b.lastCell = Find.TickManager.TicksGame;
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        internal static void Scope_Finalizer(Scope __state)
        {
            scope = __state;
        }

        // Which of an explosion's own flecks this is (the explosion's own visual calls, not an effecter's (BodyFlecks.InEffecter),
        // not its damage's): 1 the flash, 2 a smoke puff, 3 another fleck of ExplosionStart (a def's centre or interior fleck),
        // 4 a cell's fleck, 5 the dust puff thrown right after it; 0 none. Worked out once per fleck by the first CreateFleck
        // prefix (Hold), for every explosion (also uncovered ones and with the option off: BodyFlecks leaves them on the ground,
        // OwnFleck). Any fleck after a cell's fleck other than its dust puff closes the dust (the cell threw none).
        private static int Classify(ref FleckCreationData data)
        {
            if (!scope.any || data.def == null || scope.explosion == null)
            {
                return 0;
            }
            bool dustOpen = scope.dustOpen;
            scope.dustOpen = false;
            if (BodyFlecks.InEffecter)
            {
                return 0;
            }
            if (!scope.cell)
            {
                return data.def == FleckDefOf.ExplosionFlash ? 1 : data.def == FleckDefOf.Smoke ? 2 : 3;
            }
            if (data.def == scope.explosion.damType?.explosionCellFleck && data.spawnPosition.ToIntVec3() == scope.at)
            {
                scope.dustOpen = true;
                return 4;
            }
            if (data.def == FleckDefOf.DustPuff && dustOpen && data.spawnPosition.ToIntVec3() == scope.at)
            {
                return 5;
            }
            return 0;
        }

        // The fleck being made now is one of an explosion's own (set by Hold, the first CreateFleck prefix).
        internal static bool OwnFleck;

        // FleckManager.CreateFleck, first of its prefixes, for a covered explosion's own flecks (the ONE rule of what the 3D
        // burst replaces; user 2026-11-11):
        //   - the cell flecks (kind 4) stay the game's, so the cells hit still show;
        //   - the flash, the smoke puffs and the cell dust puffs (1, 2, 5) are recorded and replaced: the game still makes them
        //     (its random draws in the fleck's set-up stay as without IsoRim, so everything after is the game's to the byte),
        //     but already at the end of their life (ageTicksOverride): drawn at alpha 0 and dropped at the next tick; a def
        //     that does not fade out (alpha 1 at its end) is made at zero size instead.
        // Only for this explosion's own burst with the option on (switched off mid-explosion: from then on the game's).
        internal static void Hold(ref FleckCreationData data)
        {
            int kind = Classify(ref data);
            OwnFleck = kind != 0;
            Burst b = scope.burst;
            if (kind == 0 || kind == 3 || kind == 4 || b == null || b.explosion != scope.explosion || !On)
            {
                return;   // not replaced (3: a def's own centre or interior fleck, 4: the cell squares, kept)
            }
            int now = Find.TickManager.TicksGame;
            switch (kind)
            {
                case 1:
                    b.flash = data.def;
                    break;
                case 2:
                    b.smoke = data.def;
                    b.puffs.Add(new Puff
                    {
                        x = data.spawnPosition.x, z = data.spawnPosition.z, scale = data.scale,
                        angle = data.velocityAngle, speed = data.velocitySpeed, spin = data.rotationRate,
                    });
                    break;
                case 5:
                {
                    b.dust = data.def;
                    int k = b.dusts.Count;
                    b.dusts.Add(new Dust
                    {
                        x = data.spawnPosition.x, z = data.spawnPosition.z, angle = data.velocityAngle, speed = data.velocitySpeed, born = now,
                        turn = b.variant.debris + (Hashing.Unit(b.seed, k, VariantSalt + 5) - 0.5f) * 2f * DebrisJitter,
                    });
                    break;
                }
            }
            Held++;
            // born a life ago (the game's own life span of this fleck, its solid-time override too): gone at once
            float life = new FleckStatic { def = data.def, solidTimeOverride = data.solidTimeOverride ?? -1f }.Lifespan;
            data.ageTicksOverride = now - (Mathf.CeilToInt(life * 60f) + AgedMargin);
            if (data.ageTicksOverride == -1)
            {
                data.ageTicksOverride--;
            }
            if (data.def.fadeOutTime <= 0f)
            {
                data.exactScale = Vector3.zero;   // (no fade: full alpha at its end)
            }
        }

        private static void Fail(Exception e)
        {
            failed = true;
            bursts.Clear();
            byExplosion.Clear();
            scope = default;
            Log.ErrorOnce("[IsoRim] 3D explosions failed (explosions are drawn flat from now on): " + e, 93613301);
        }

        // ---- timing (game seconds; every length is a fleck def's own)
        private static float Life(FleckDef f) => f != null ? f.Lifetime : 0f;

        // A fleck of def f's alpha at age (s), as the game works it out: its own FleckStatic.Alpha.
        private static float Alpha(FleckDef f, float age)
        {
            if (f == null || age < 0f || age > Life(f))
            {
                return 0f;
            }
            return Mathf.Clamp01(new FleckStatic { def = f, solidTimeOverride = -1f, ageSecs = age }.Alpha);
        }

        private static float FireEndOf(Burst b)
        {
            FleckDef smoke = b.smoke ?? FleckDefOf.Smoke, dust = b.dust ?? FleckDefOf.DustPuff;
            return FireEndRule == FireEnd.SmokeFadeIn ? smoke.fadeInTime : Life(dust);
        }

        // ---- meshes: a unit sphere per colour and size step, its outline made for that size. Made a few per frame: the standard
        // look's ahead of time while the option is on (Warm), anything else when first wanted (at most BuildsPerFrame a frame:
        // a part not made yet is left out of that frame).
        private static readonly Dictionary<long, List<DynamicModels.Part>> parts = new Dictionary<long, List<DynamicModels.Part>>();
        private static List<Vector3> sphere, rock;
        private const int BuildsPerFrame = 6;       // part sets made in one frame, at most
        private static int builds, buildFrame = -1, warmed;
        private static readonly int[] KindSteps = { Steps, SmokeSteps, 1, 1 };   // colour steps of kind 0 fire, 1 smoke, 2 dust, 3 debris

        private static int SizeStep(float r)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(Mathf.Max(r, SizeBase) / SizeBase) / Mathf.Log(SizeRatio)), 0, SizeSteps - 1);
        }

        private static float StepSize(int k) => SizeBase * Mathf.Pow(SizeRatio, k);

        private static bool MayBuild()
        {
            if (buildFrame != Time.frameCount)
            {
                buildFrame = Time.frameCount;
                builds = 0;
            }
            return builds < BuildsPerFrame;
        }

        // kind 0 glowing fire, 1 lit smoke, 2 lit dust, 3 lit debris; null while not made yet (the frame's builds are used up)
        private static List<DynamicModels.Part> PartsFor(int kind, ExplosionLook look, int colour, int step)
        {
            long key = ((long)kind << 40) | ((long)look.id << 20) | ((long)colour << 8) | (long)step;
            List<DynamicModels.Part> p;
            if (parts.TryGetValue(key, out p))
            {
                return p;
            }
            if (!MayBuild())
            {
                return null;
            }
            builds++;
            sphere ??= Icosahedron.Sphere(2);
            rock ??= Rock();
            Color c = kind == 0 ? FireRamp(look.fire, colour / (float)(Steps - 1))
                : kind == 1 ? FireFx.SmokeShade(look.smoke, colour / (float)(SmokeSteps - 1))   // FireFx's: lighter as it ages
                : look.dust;
            List<Vector3> shape = kind == 3 ? rock : sphere;
            float grow = 1f + ObjLoader.OutlineWidthCells / StepSize(step);   // the outline at its width for this size
            List<Vector3> v = new List<Vector3>(shape.Count), line = new List<Vector3>(shape.Count), normals = kind == 1 || kind == 2 ? new List<Vector3>(shape.Count) : null;
            List<Color32> col = new List<Color32>(shape.Count), lineCol = new List<Color32>(shape.Count);
            Color32 dark = R3DMaterials.Shaded(c, ModelBaker.OutlineShade);
            for (int i = 0; i + 2 < shape.Count; i += 3)
            {
                for (int j = 0; j < 3; j++)
                {
                    // the glow's tones by each corner's height on the ball (blended across a face: no stepped face edges)
                    float y = shape[i + j].normalized.y;
                    float band = kind != 0 ? 1f : y > 1f / 3f ? FireBands[0] : y > -1f / 3f ? FireBands[1] : FireBands[2];
                    v.Add(shape[i + j]);
                    col.Add(R3DMaterials.Shaded(c, band));
                    normals?.Add(shape[i + j].normalized);
                    line.Add(shape[i + j] * grow);
                    lineCol.Add(dark);
                }
            }
            p = DynamicModels.PartsFromCorners(
                kind == 0 ? default : new KeyValuePair<List<Vector3>, List<Color32>>(v, col), normals,
                kind == 0 ? new KeyValuePair<List<Vector3>, List<Color32>>(v, col) : default,
                new KeyValuePair<List<Vector3>, List<Color32>>(line, lineCol));
            parts[key] = p;
            return p;
        }

        // The standard look's fireball parts (the vanilla explosives' colours: the first parts every explosion needs at once)
        // made ahead, a few a frame, while the option is on: every colour step, up to the size step the biggest explosive of
        // the game can reach (WarmSteps). Smoke, dust and debris (few parts, wanted later in a burst) are made when wanted.
        private static int warmSteps = -1;
        internal static int WarmedVertices;   // harness: vertices of the parts made ahead so far

        private static void Warm()
        {
            if (warmSteps < 0)
            {
                warmSteps = WarmSteps();
            }
            int total = Steps * warmSteps;
            if (warmed >= total || DamageDefOf.Bomb == null)
            {
                return;
            }
            ExplosionLook look = LookOf(DamageDefOf.Bomb);
            while (warmed < total && MayBuild())
            {
                List<DynamicModels.Part> p = PartsFor(0, look, warmed / warmSteps, warmed % warmSteps);
                for (int i = 0; p != null && i < p.Count; i++)
                {
                    WarmedVertices += p[i].mesh != null ? p[i].mesh.vertexCount : 0;
                }
                warmed++;
            }
        }

        // Size steps a fireball blob of the game's explosives can reach: the largest explosion radius of an explosive projectile
        // or CompExplosive among the loaded defs, times the biggest variant scale, blob and jitter.
        private static int WarmSteps()
        {
            float r = 0f;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.projectile != null && d.projectile.damageDef != null && d.projectile.damageDef.isExplosive)
                {
                    r = Mathf.Max(r, d.projectile.explosionRadius);
                }
                CompProperties_Explosive ce = d.GetCompProperties<CompProperties_Explosive>();
                if (ce != null && ce.explosiveDamageType != null && ce.explosiveDamageType.isExplosive)
                {
                    r = Mathf.Max(r, ce.explosiveRadius);
                }
            }
            float blob = 0f, scale = 0f;
            foreach (Variant v in Variants)
            {
                scale = Mathf.Max(scale, v.scale);
                foreach (Blob o in v.blobs)
                {
                    blob = Mathf.Max(blob, o.r);
                }
            }
            return SizeStep(r * scale * blob * (1f + VariantSize)) + 1;
        }

        private static Color FireRamp(Color[] keys, float u)
        {
            if (keys.Length == 1)
            {
                return keys[0];
            }
            float x = Mathf.Clamp01(u) * (keys.Length - 1);
            int i = Mathf.Min(keys.Length - 2, (int)x);
            return Color.Lerp(keys[i], keys[i + 1], x - i);
        }

        // A debris chunk: an icosahedron with its corners pushed in and out a little (fixed), faces lit flat.
        private static List<Vector3> Rock()
        {
            List<Vector3> s = Icosahedron.Sphere(0);
            List<Vector3> r = new List<Vector3>(s.Count);
            foreach (Vector3 v in s)
            {
                int h = Mathf.RoundToInt(v.x * 97f) * 31 + Mathf.RoundToInt(v.y * 89f) * 17 + Mathf.RoundToInt(v.z * 83f);
                r.Add(v * (RockInnermost + RockSpread * Hashing.Unit(h, 0, VariantSalt + 6)));
            }
            return r;
        }

        // ---- drawing (with the things, every frame)
        internal static void Draw()
        {
            DrawnLast = 0;
            if (Find.TickManager == null)
            {
                return;
            }
            try
            {
                CheckGame();
                if (On)
                {
                    Warm();
                }
                if (bursts.Count == 0)
                {
                    return;
                }
                Map map = Find.CurrentMap;
                int now = Find.TickManager.TicksGame;
                int kept = 0;
                for (int i = 0; i < bursts.Count; i++)
                {
                    Burst b = bursts[i];
                    if (b.explosion != null && !b.explosion.Spawned)
                    {
                        byExplosion.Remove(b.explosion.thingIDNumber);
                        b.frontEnd = b.lastCell > 0 ? b.lastCell : b.born;
                        b.explosion = null;
                    }
                    float t = (now - b.born) / 60f;
                    if (t < 0f || (b.explosion == null && t > LifeOf(b)) || !Find.Maps.Contains(b.map))
                    {
                        if (b.explosion != null)
                        {
                            byExplosion.Remove(b.explosion.thingIDNumber);
                        }
                        continue;   // over, from another time (a load), or its map gone
                    }
                    bursts[kept++] = b;
                    Matrix4x4 floor = Matrix4x4.identity;
                    if (b.map != map || !View3D.Ready || !IsoRimMod.Settings.explosions3D || (Floors.On && !Floors.RowMatrix(b.row, out floor)))
                    {
                        continue;   // another map, the 3D view off, or its floor hidden now
                    }
                    DrawBurst(b, t, now, floor);
                }
                bursts.RemoveRange(kept, bursts.Count - kept);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // Till its last part is gone: the fireball, the smoke (only if the game threw some), the ring and the debris. Asked only
        // once its explosion is over (frontEnd known; while it still spreads its cells the burst stays).
        private static float LifeOf(Burst b)
        {
            float front = (b.frontEnd - b.born) / 60f;
            float fire = Mathf.Max(Life(b.flash ?? FleckDefOf.ExplosionFlash), front) + FireShrinkSpan(b);
            float smoke = b.puffs.Count > 0 && b.smoke3D ? SmokeLife3D : 0f;
            float dust = front + Life(b.dust ?? FleckDefOf.DustPuff);
            return Mathf.Max(Mathf.Max(smoke, dust), fire);
        }

        // How long the fireball shrinks: from the flash's end to the fire's end (FireEndRule).
        private static float FireShrinkSpan(Burst b) => Mathf.Max(MinSpan, FireEndOf(b) - Life(b.flash ?? FleckDefOf.ExplosionFlash));

        // One part of a burst, in the burst's rows and moved onto the burst's floor by floor (Floors.RowMatrix of its row): every
        // part of a burst is on the burst's floor, whatever rows it reaches.
        private static void Submit(Burst b, Matrix4x4 floor, int kind, int colour, Vector3 at, float r, float squash = 1f)
        {
            if (r < MinPart)
            {
                return;
            }
            List<DynamicModels.Part> p = PartsFor(kind, b.look, colour, SizeStep(r));
            if (p == null)
            {
                return;
            }
            float sq = View3D.Squash;
            Matrix4x4 m = floor * Matrix4x4.TRS(new Vector3(at.x, at.y / sq, at.z), Quaternion.identity, new Vector3(r, r * squash / sq, r));
            DynamicModels.SubmitParts(p, m, b.floor);
            DrawnLast++;
        }

        // The fireball's size (0 .. 1 of its full size) at t: it swells with the front (a cell hit is hit to its edge, half a
        // cell out), holds till both the front and the flash are done, then shrinks to nothing over FireShrinkSpan (as FireFx's
        // flames: never darkening). The one rule; tools\make_explosion_concept.py follows it.
        private static float FireSize(Burst b, float t, out float hold)
        {
            float tFlash = Life(b.flash ?? FleckDefOf.ExplosionFlash);
            float tFront = b.frontEnd >= 0 ? (b.frontEnd - b.born) / 60f : float.MaxValue;
            hold = Mathf.Max(tFlash, b.frontEnd >= 0 ? tFront : t);
            if (t <= hold)
            {
                float e = b.instant ? 1f : Mathf.Clamp01((b.front + CellHalf) / b.radius);
                return 1f - (1f - e) * (1f - e);
            }
            return Mathf.Pow(Mathf.Max(0f, 1f - (t - hold) / FireShrinkSpan(b)), FireFx.ShrinkPower);
        }

        private static void DrawBurst(Burst b, float t, int now, Matrix4x4 floor)
        {
            float R = b.radius;
            FleckDef smokeDef = b.smoke ?? FleckDefOf.Smoke, dustDef = b.dust ?? FleckDefOf.DustPuff;
            float tFront = b.frontEnd >= 0 ? (b.frontEnd - b.born) / 60f : t;

            // the smoke's own rise: the mean northward speed of its puffs (the game's north is up in 3D)
            float rise = 0f;
            for (int i = 0; i < b.puffs.Count; i++)
            {
                rise += b.puffs[i].speed * Mathf.Cos(b.puffs[i].angle * Mathf.Deg2Rad) / b.puffs.Count;
            }

            // fireball: hot while it holds, then cooling toward red as it shrinks
            float hold;
            float k = FireSize(b, t, out hold);
            if (k > 0f)
            {
                float heat = t <= hold ? 0f : Mathf.Clamp01((t - hold) / FireShrinkSpan(b));
                float up = rise * Mathf.Max(0f, t - hold);
                float Rk = R * b.variant.scale * k;
                for (int i = 0; i < b.blobs.Length; i++)
                {
                    Blob o = b.blobs[i];
                    float u = t <= hold ? ToneAt[o.tone] * FlashTone : ToneAt[o.tone] + (1f - ToneAt[o.tone]) * heat;
                    int step = Mathf.Clamp(Mathf.RoundToInt(u * (Steps - 1)), 0, Steps - 1);
                    Submit(b, floor, 0, step, new Vector3(b.centre.x + o.x * Rk, b.centre.y + o.h * Rk + up, b.centre.z + o.y * Rk), o.r * Rk);
                }
            }

            // smoke: each of the game's puffs, from the fireball's top, its north speed as the rise, east as the drift
            if (b.smoke3D && t < SmokeLife3D)
            {
                float fadeIn = smokeDef.fadeInTime;
                float a = t < fadeIn ? t / fadeIn : 1f - Mathf.Pow(Mathf.Clamp01((t - fadeIn) / Mathf.Max(MinSpan, SmokeLife3D - fadeIn)), SmokeShrink);   // the game's fade-in, then ever faster
                int tone = Mathf.Clamp((int)(t / SmokeLife3D * SmokeSteps), 0, SmokeSteps - 1);
                for (int i = 0; i < b.puffs.Count; i++)
                {
                    Puff p = b.puffs[i];
                    float size = (p.scale + smokeDef.growthRate * t) * SmokeCover * a;
                    if (size < MinPart)
                    {
                        continue;
                    }
                    float ang = p.angle * Mathf.Deg2Rad;
                    Vector3 c = new Vector3(p.x + p.speed * Mathf.Sin(ang) * t, b.centre.y + R * b.variant.scale * b.top + p.speed * Mathf.Cos(ang) * t, p.z);
                    float yaw = (b.turn + i * PuffTurnStep + p.spin * t) * Mathf.Deg2Rad;
                    float cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw);
                    foreach (Vector4 l in b.variant.lobes)
                    {
                        Submit(b, floor, 1, tone, new Vector3(c.x + (l.x * cy - l.y * sy) * size, c.y + l.z * size, c.z + (l.x * sy + l.y * cy) * size), l.w * size);
                    }
                }
            }

            // ground ring: out with the front, then on at the dust's mean speed while it sinks away over the dust's life
            float dustLife = Life(dustDef);
            float ringAge = t - tFront;
            if (b.front > RingMinRadius && ringAge < dustLife)
            {
                float speed = 0f;
                for (int i = 0; i < b.dusts.Count; i++)
                {
                    speed += b.dusts[i].speed / b.dusts.Count;
                }
                float rad = b.frontEnd >= 0 ? b.front + speed * Mathf.Max(0f, ringAge) : b.front;
                float lump = Mathf.Max(RingLump, 2f * Mathf.PI * rad / (RingStep * MaxRingLumps)) * (b.frontEnd >= 0 ? Alpha(dustDef, ringAge) : 1f);
                if (lump > MinPart)
                {
                    int n = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * rad / (RingStep * lump)), 6, MaxRingLumps);
                    for (int j = 0; j < n; j++)
                    {
                        float aj = 2f * Mathf.PI * j / n;
                        float rr = lump * (LumpSmallest + LumpSpread * Hashing.Unit(b.seed, j, VariantSalt + 7));
                        float rj = rad + (Hashing.Unit(b.seed, j, VariantSalt + 8) - 0.5f) * lump;
                        Submit(b, floor, 2, 0, new Vector3(b.centre.x + Mathf.Cos(aj) * rj, b.centre.y + rr * RingLift, b.centre.z + Mathf.Sin(aj) * rj), rr, RingSquash);
                    }
                }
            }

            // debris: a chunk for each of the game's dust puffs, out to the blast's edge over the dust's life
            float g = CollapseDebris.Gravity;
            int chunks = Mathf.Min(MaxChunks, b.dusts.Count);
            for (int i = 0; i < chunks; i++)
            {
                Dust d = b.dusts[i * b.dusts.Count / chunks];
                float age = (now - d.born) / 60f;
                if (age < 0f || age >= dustLife)
                {
                    continue;
                }
                float ox = d.x - b.centre.x, oz = d.z - b.centre.z;
                float len = Mathf.Sqrt(ox * ox + oz * oz);
                float wa = (len > MinPart ? Mathf.Atan2(oz, ox) : (90f - d.angle) * Mathf.Deg2Rad) + d.turn * Mathf.Deg2Rad;
                float v0 = g * dustLife / 2f, f = age / dustLife;
                float h = Chunk + v0 * age - g * age * age / 2f;
                if (h < Chunk * 0.5f)
                {
                    continue;
                }
                Submit(b, floor, 3, 0, new Vector3(d.x + Mathf.Cos(wa) * R * f, b.centre.y + h, d.z + Mathf.Sin(wa) * R * f), Chunk);
            }
        }

        // How much of the Smoke fleck's quad its visible puff fills (the alpha-weighted radius holding 90 % of its alpha, 1 = the
        // quad's edge). Measured from vanilla Things/Mote/Smoke (tools\make_explosion_concept.py cover(), 2026-11-11): the
        // texture is not readable at run time.
        private const float SmokeCover = 0.746f;
    }
}
