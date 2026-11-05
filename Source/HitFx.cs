using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The game's red damage flash on a 3D figure. The game keeps it per pawn (DamageFlasher: set by a harmful hit, gone after
    // 16 ticks, so it holds while paused and runs with the game's speed) and tints a pawn's sprites with its CurColor. IsoRim's
    // figures are not those sprites, so while one that flashes now is drawn (People, Animals, Mechs.Draw: Begin to End) every
    // part passing DynamicModels.Submit is drawn with that strength (_IsoFlash, IsoRimSolid.frag.glsl), unbatched, and every
    // part of the game's own materials with the game's own flash material. Outlines keep their colour; shadows, the wound
    // marks (the game does not flash its wound overlays), held weapons, tools and carried things are drawn outside the scope
    // or pass neither. A figure drawn for another mod (Api.DrawFigure) does not flash, as a portrait does not. Cards are the
    // game's sprites and flash by themselves. A figure not flashing costs one comparison per draw.
    internal static class HitFlash
    {
        internal static bool On;
        private static float strength;
        private static int step;

        private static readonly int flashId = Shader.PropertyToID("_IsoFlash");
        private static readonly int ownerFloorId = Shader.PropertyToID("_IsoOwnerFloor");
        private static readonly Dictionary<int, MaterialPropertyBlock> blocks = new Dictionary<int, MaterialPropertyBlock>();

        // Before the parts of pawn's figure are drawn.
        internal static void Begin(Pawn pawn)
        {
            On = false;
            if (FigureScope.Active)
            {
                return;
            }
            DamageFlasher flasher = pawn.Drawer.renderer.flasher;
            if (!flasher.FlashingNowOrRecently)
            {
                return;
            }
            float s = 1f - flasher.CurColor.g;   // Color.Lerp(white, red, ticks left / 16)
            if (s < 0.01f)
            {
                return;
            }
            strength = s;
            step = Mathf.RoundToInt(s * 16f);
            On = true;
        }

        internal static void End()
        {
            On = false;
        }

        // Around a part of a flashing figure that the game draws apart (a mech's gun): Resume with what Pause gave.
        internal static bool Pause()
        {
            bool was = On;
            On = false;
            return was;
        }

        internal static void Resume(bool was)
        {
            On = was;
        }

        // The draw's property block now: the flash and, as RoofClip.FloorBlock, the floor it stands on (below 0: not given).
        internal static MaterialPropertyBlock Block(int floor)
        {
            int owner = floor >= 0 && Floors.On ? floor + 1 : 0;
            int key = owner * 32 + step;
            MaterialPropertyBlock b;
            if (!blocks.TryGetValue(key, out b))
            {
                b = new MaterialPropertyBlock();
                b.SetFloat(flashId, step / 16f);
                if (owner > 0)
                {
                    b.SetFloat(ownerFloorId, owner);
                }
                blocks[key] = b;
            }
            return b;
        }

        // A material of the game's or another mod's on the figure (a face layer, a decal, a race's card): the game's own.
        internal static Material Mat(Material m)
        {
            return On ? DamagedMatPool.GetDamageFlashMat(m, strength) : m;
        }
    }

    // Blood from a wound on a 3D figure in 3D. A wound triggers its flesh type's damage effecter (DamageWorker_AddInjury) with the
    // pawn and whoever hit it; a sprayer of that effecter that makes the game's blood splashes or its puffs is replaced here
    // (FloorSprays.MakeMote_Prefix) by a small burst of solid droplets or soft puffs, in the colour, number and size of the
    // sprayer's own def, so an insect's stays yellow and a mod's flesh keeps its colour; the other sprayers run as before, stood at
    // the hit's height (the small body impact). An effecter with no blood splashes (a mech's sparks) is left alone, as is
    // everything outside the 3D view, without the effects setting, on a pawn drawn as a card, and the blood on the ground.
    //
    // A burst starts at the hit: at the figure's body on the side the hit came from, as high as shots aim. Most droplets leave
    // along the hit from the far side, some spatter back, and they arc down and go out by shrinking or at the ground. Nothing is
    // kept per droplet: where each is follows from the burst, its number and the game's ticks (still while paused, faster at
    // higher speed, the same every run, and no call to the game's random numbers). Droplets are one small mesh per colour,
    // drawn instanced with the things (PartBatches); DynamicModels.Submit puts them on the floor of their rows.
    internal static class HitFx
    {
        private struct Burst
        {
            public Map map;
            public Vector3 centre;   // the figure's body at the hit's height: x, z in the game's rows, y over its floor
            public Vector3 dir;      // the way the hit went, flat; zero: none (spreads all round)
            public float reach;      // from the centre out to the surface of the body
            public int born;         // game tick
            public int seed;
            public int count;
            public float size;       // of a droplet or a puff, cells
            public Look look;
            public bool puffs;
        }

        private sealed class Look
        {
            public List<DynamicModels.Part> drop;
            public List<DynamicModels.Part>[] puff;
        }

        private const int MaxBursts = 400;
        private const int MaxPuffBursts = 40;   // see-through puffs are one draw each: left out when more than this are in the air
        private const int DropLife = 30;        // ticks, at most
        private const int PuffLife = 40;
        private const int PuffSteps = 5;
        private const float Gravity = 7f;       // cells per second, per second
        private const int Salt = 8831;

        private static readonly List<Burst> bursts = new List<Burst>();
        private static readonly Dictionary<EffecterDef, bool> blood = new Dictionary<EffecterDef, bool>();
        private static readonly Dictionary<int, Look> looks = new Dictionary<int, Look>();
        private static bool failed;
        private static int puffBursts;

        internal static bool Off;   // harness: the game's flat spray
        internal static int Made, Live, DrawnLast;   // harness: bursts made so far, in the air, droplets and puffs drawn in the last frame

        private static bool Splash(FleckDef f)
        {
            return f != null && f.graphicData != null && f.graphicData.texPath == "Things/Mote/BloodSplash";
        }

        // A flesh type's damage effecter that sprays blood.
        private static bool Blood(EffecterDef def)
        {
            bool yes;
            if (blood.TryGetValue(def, out yes))
            {
                return yes;
            }
            List<FleshTypeDef> flesh = DefDatabase<FleshTypeDef>.AllDefsListForReading;
            for (int i = 0; i < flesh.Count && !yes; i++)
            {
                yes = flesh[i].damageEffecter == def;
            }
            bool splashes = false;
            for (int i = 0; yes && def.children != null && i < def.children.Count; i++)
            {
                splashes |= Splash(def.children[i].fleckDef);
            }
            blood[def] = yes && splashes;
            return yes && splashes;
        }

        // From SubEffecter_Sprayer.MakeMote: true if the sprayer's blood is made here, and the game's is left out.
        internal static bool Replaces(SubEffecter_Sprayer sprayer, TargetInfo A, TargetInfo B)
        {
            Pawn pawn = A.Thing as Pawn;
            if (pawn == null || sprayer.parent == null || sprayer.parent.def == null || !View3D.Ready || Off || failed || !Blood(sprayer.parent.def))
            {
                return false;
            }
            IsoRimSettings st = IsoRimMod.Settings;
            Map map = A.Map ?? B.Map;
            if (!st.effects3D || !st.models3D || map == null || map != Find.CurrentMap || !Patches.DrawnAsModel(pawn) || Api.IsCard(pawn))
            {
                return false;
            }
            try
            {
                Matrix4x4 extra = Overhead.Floor(pawn);   // a stair's rise, a bridge's deck
                int tick = Find.TickManager.TicksGame;
                int seed = pawn.thingIDNumber * 31 + tick;
                float height = FigureWeapons.TargetHeight(pawn) * (0.85f + 0.3f * Hashing.Unit(seed, 0, Salt));
                FleckDef fleck = sprayer.def.fleckDef;
                bool splash = Splash(fleck);
                if (!splash && fleck != FleckDefOf.AirPuff)
                {
                    IngestBits.Stand(height + extra.m13 * View3D.Squash, extra);
                    return false;
                }
                if (bursts.Count >= MaxBursts || (!splash && puffBursts >= MaxPuffBursts))
                {
                    return true;
                }
                Look look = LookFor(sprayer.EffectiveColor);
                if (look == null)
                {
                    return false;
                }
                Vector3 centre = pawn.Spawned ? pawn.DrawPos : A.CenterVector3;
                Vector3 from = B.IsValid && B.Thing != pawn ? (B.HasThing ? B.Thing.PositionHeld : B.Cell).ToVector3Shifted() : centre;
                Vector3 dir = FloorSprays.Sub(centre, from);
                dir.y = 0f;
                dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.zero;
                float wide = sprayer.def.scale.Average;
                IntRange burst = sprayer.def.burstCount;
                int n = burst.min + (int)(Hashing.Unit(seed, 1, Salt) * (burst.max - burst.min + 1));
                bursts.Add(new Burst
                {
                    map = map,
                    centre = new Vector3(centre.x, height + extra.m13 * View3D.Squash, centre.z),
                    dir = dir,
                    reach = Mathf.Clamp(0.18f * pawn.BodySize, 0.12f, 0.7f),
                    born = tick,
                    seed = seed + (splash ? 0 : 1),
                    count = splash ? n * 4 : n / 2 + 1,
                    size = splash ? 0.075f * wide : 0.3f * wide,
                    look = look,
                    puffs = !splash,
                });
                if (!splash)
                {
                    puffBursts++;
                }
                Made++;
                return true;
            }
            catch (Exception e)
            {
                failed = true;
                Log.ErrorOnce("[IsoRim] Making 3D blood failed (the game's spray stays): " + e, 93612101);
                return false;
            }
        }

        private static Look LookFor(Color c)
        {
            int key = ((Color32)c).r << 16 | ((Color32)c).g << 8 | ((Color32)c).b;
            Look l;
            if (looks.TryGetValue(key, out l))
            {
                return l;
            }
            Material lit = R3DMaterials.LitModel;
            Material puff = FireFx.SmokeMat;
            if (lit == null || puff == null)
            {
                return null;
            }
            l = new Look { drop = new List<DynamicModels.Part> { new DynamicModels.Part { mesh = DropMesh(c), material = lit } }, puff = new List<DynamicModels.Part>[PuffSteps] };
            for (int i = 0; i < PuffSteps; i++)
            {
                float u = i / (float)(PuffSteps - 1);
                l.puff[i] = new List<DynamicModels.Part> { new DynamicModels.Part { mesh = FireFx.SmokeMesh(c, Mathf.Lerp(0.5f, 0.06f, u)), material = puff, alone = true } };
            }
            looks[key] = l;
            return l;
        }

        // A droplet one unit across, round its middle: an icosahedron, its faces flat.
        private static Mesh DropMesh(Color c)
        {
            float g = (1f + Mathf.Sqrt(5f)) / 2f;
            Vector3[] p =
            {
                new Vector3(-1, g, 0), new Vector3(1, g, 0), new Vector3(-1, -g, 0), new Vector3(1, -g, 0),
                new Vector3(0, -1, g), new Vector3(0, 1, g), new Vector3(0, -1, -g), new Vector3(0, 1, -g),
                new Vector3(g, 0, -1), new Vector3(g, 0, 1), new Vector3(-g, 0, -1), new Vector3(-g, 0, 1),
            };
            int[] f = { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                        3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
            Color32 k = R3DMaterials.Shaded(c, 1f);
            List<Vector3> v = new List<Vector3>();
            List<Color32> col = new List<Color32>();
            for (int i = 0; i < f.Length; i += 3)
            {
                FireFx.Tri(v, col, p[f[i]].normalized * 0.5f, p[f[i + 1]].normalized * 0.5f, p[f[i + 2]].normalized * 0.5f, k, k, k);
            }
            return FireFx.Finish(v, col, "IsoRim_BloodDrop");
        }

        // With the things (Patches.CullThings_Finalizer), every frame.
        internal static void Draw()
        {
            DrawnLast = 0;
            if (bursts.Count == 0 || !View3D.Ready || Find.TickManager == null)
            {
                return;
            }
            try
            {
                Map map = Find.CurrentMap;
                int now = Find.TickManager.TicksGame;
                int kept = 0;
                puffBursts = 0;
                for (int i = 0; i < bursts.Count; i++)
                {
                    Burst b = bursts[i];
                    int age = now - b.born;
                    if (age < 0 || age >= (b.puffs ? PuffLife : DropLife))
                    {
                        continue;
                    }
                    bursts[kept++] = b;
                    puffBursts += b.puffs ? 1 : 0;
                    Matrix4x4 floor;
                    if (b.map != map || (Floors.On && !Floors.RowMatrix(Mathf.FloorToInt(b.centre.z), out floor)))
                    {
                        continue;   // another map, or a floor hidden now
                    }
                    if (b.puffs)
                    {
                        DrawPuffs(b, age);
                    }
                    else
                    {
                        DrawDrops(b, age);
                    }
                }
                bursts.RemoveRange(kept, bursts.Count - kept);
                Live = kept;
            }
            catch (Exception e)
            {
                failed = true;
                bursts.Clear();
                Log.ErrorOnce("[IsoRim] Drawing 3D blood failed: " + e, 93612102);
            }
        }

        private static void DrawDrops(Burst b, int age)
        {
            float sq = View3D.Squash;
            bool aimed = b.dir != Vector3.zero;
            Vector3 side = new Vector3(b.dir.z, 0f, -b.dir.x);
            for (int k = 0; k < b.count; k++)
            {
                float r1 = Hashing.Unit(b.seed, k, Salt + 1), r2 = Hashing.Unit(b.seed, k, Salt + 2), r3 = Hashing.Unit(b.seed, k, Salt + 3);
                float r4 = Hashing.Unit(b.seed, k, Salt + 4), r5 = Hashing.Unit(b.seed, k, Salt + 5);
                float life = DropLife * (0.6f + 0.4f * r4);
                float u = age / life;
                if (u >= 1f)
                {
                    continue;
                }
                float t = age / 60f;
                Vector3 way;
                if (aimed)
                {
                    float along = r5 < 0.3f ? -1f : 1f;   // some spatter back the way the hit came
                    way = b.dir * along + side * ((r1 - 0.5f) * 1.2f);
                }
                else
                {
                    way = Quaternion.AngleAxis(r1 * 360f, Vector3.up) * Vector3.forward;
                }
                Vector3 start = b.centre + way.normalized * b.reach;
                float speed = 1.4f + 1.8f * r2;
                float up = 0.2f + 1.3f * r3;
                Vector3 p = start + way.normalized * (speed * t);
                p.y += up * t - 0.5f * Gravity * t * t;
                if (p.y <= 0.01f)
                {
                    continue;   // landed: the game's blood on the ground takes over
                }
                float w = b.size * (0.7f + 0.6f * r4) * (1f - u * u * u);
                Matrix4x4 m = Matrix4x4.TRS(new Vector3(p.x, p.y / sq, p.z), Quaternion.AngleAxis(r1 * 360f, Vector3.up), new Vector3(w, w / sq, w));
                DynamicModels.SubmitParts(b.look.drop, m);
                DrawnLast++;
            }
        }

        private static void DrawPuffs(Burst b, int age)
        {
            float sq = View3D.Squash;
            float t = age / 60f;
            for (int k = 0; k < b.count; k++)
            {
                float r1 = Hashing.Unit(b.seed, k, Salt + 6), r2 = Hashing.Unit(b.seed, k, Salt + 7), r3 = Hashing.Unit(b.seed, k, Salt + 8);
                float u = age / (float)PuffLife;
                Vector3 way = Quaternion.AngleAxis(r1 * 360f, Vector3.up) * Vector3.forward;
                Vector3 p = b.centre - b.dir * b.reach + way * (b.reach * r2) + (way * 0.5f - b.dir * 0.3f) * ((0.4f + 0.4f * r3) * t);
                p.y += 0.3f * t;
                float w = b.size * (0.6f + 0.7f * Mathf.Sqrt(u)) * (0.8f + 0.4f * r2);
                int step = Mathf.Min(PuffSteps - 1, (int)(u * PuffSteps));
                // (its foot at 0: lowered by half its height to centre it)
                Matrix4x4 m = Matrix4x4.TRS(new Vector3(p.x, (p.y - w * 0.5f) / sq, p.z), Quaternion.AngleAxis(r1 * 360f, Vector3.up), new Vector3(w, w / sq, w));
                DynamicModels.SubmitParts(b.look.puff[step], m);
                DrawnLast++;
            }
        }
    }
}
