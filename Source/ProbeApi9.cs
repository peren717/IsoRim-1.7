using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only fixtures for Api version 9 (apparel model bindings, held shields). Each fixture is spawned fresh from
    // fixed seeds (the previous one removed first). Rows of people, one row per item, at x, z + 4 * row:
    //   ap9fix=<set>,x,z,<mode>   set: vfem2 (VFE Medieval 2 batch 1, 13 items), api43 (Api 43 shields, ProbeApi43.cs) or vanilla (vanilla apparel, for the
    //                             before/after comparison); mode:
    //     stand  per row: male facing N, E, S, W, female N, E, S, W (drafted, a knife), male and female lying (downed),
    //            a male undrafted facing S, a male drafted facing S with an assault rifle (two-handed)
    //     walk   per row: male walking east-west, male north-south, female east-west, female north-south (drafted, a knife,
    //            real Goto jobs back and forth over 3 cells, one game tick per frame while the fixture stands)
    //   ap9row=a,b               only rows a..b of the set (both inclusive)
    //   ap9shieldcolour=r,g,b    the colour the fixture gives coloured items (CompColorable) on male pawns; females keep
    //                            the item's own
    //   ap9items=a+b;c;...       set items: one row per ';' entry, the defNames worn in it joined by '+' (before ap9fix)
    //   ap9cloff=1               CustomLayers off (as before it), for before/after renders (before ap9fix)
    //   ap9clsweep=label         items on unknown layers (CustomLayers): every apparel def with how it is taken, to
    //                            IsoRimCustomLayers.tsv, and each fixture pawn's dressed look, to the log (ProbeCustomLayers.cs)
    // Apparel9Check (ProbeApi9Check.cs, Api 9 builds) adds the binding checks.
    public static partial class Probe
    {
        private static bool ap9Patched;
        private static readonly List<Pawn> ap9Spawned = new List<Pawn>();
        private static readonly List<Pawn> ap9Walkers = new List<Pawn>();
        private static readonly Dictionary<int, IntVec3[]> ap9Lanes = new Dictionary<int, IntVec3[]>();
        private static readonly Dictionary<int, string> ap9Roles = new Dictionary<int, string>();
        private static bool ap9Ticking;
        private static int ap9RowFrom, ap9RowTo = 999;
        private static Color? ap9Colour;
        private static string[][] ap9Items = new string[0][];

        // Each row: the items worn over a basic shirt and pants.
        private static readonly string[][] Ap9Vfem2 =
        {
            new[] { "VFEM2_Apparel_PlateHelmetHeavy" },
            new[] { "VFEM2_Apparel_HeavyLeatherHelmet" },
            new[] { "VFEM2_Apparel_LeatherHelmet" },
            new[] { "VFEM2_Apparel_PaddedHelmet" },
            new[] { "VFEM2_Apparel_KingsCrown" },
            new[] { "VFEM2_Apparel_DameHat" },
            new[] { "VFEM2_Apparel_JesterHat" },
            new[] { "VFEM2_Apparel_PlagueMask" },
            new[] { "VFEM2_Apparel_Chaperon" },
            new[] { "VFEM2_Apparel_Cap" },
            new[] { "VFEM2_Apparel_TorchBelt" },
            new[] { "VFEM2_Shield_Heater" },
            new[] { "VFEM2_Shield_Round" },
        };

        private static readonly string[][] Ap9Vanilla =
        {
            new[] { "Apparel_CowboyHat", "Apparel_Duster" },
            new[] { "Apparel_SimpleHelmet", "Apparel_FlakVest", "Apparel_FlakPants" },
            new[] { "Apparel_PowerArmorHelmet", "Apparel_PowerArmor" },
            new[] { "Apparel_Tuque", "Apparel_Parka" },
            new[] { "Apparel_ClothMask", "Apparel_Corset" },
            new[] { "Apparel_TribalHeaddress", "Apparel_TribalA" },
            new[] { "Apparel_Burka" },
            new[] { "Apparel_PlateArmor", "Apparel_ShieldBelt" },
            new[] { "Apparel_PackJump", "Apparel_Jacket" },
            new[] { "Apparel_Crown", "Apparel_RobeRoyal" },
        };

        // Api 43 (ApparelModelDef.shown): items of the harness-only test addon (api43test), one per row, and the vanilla shield
        // belt; each bound to a Shield model by that addon (ProbeApi43.cs checks them).
        private static readonly string[][] Ap9Api43 =
        {
            new[] { "Api43Test_ShieldDefault" },
            new[] { "Api43Test_ShieldWeapon" },
            new[] { "Api43Test_ShieldAlways" },
            new[] { "Api43Test_ShieldDrawsOwn" },
            new[] { "Apparel_ShieldBelt" },
        };

        private static readonly Color[] Ap9Hair = { new Color(0.2f, 0.15f, 0.1f), new Color(0.55f, 0.4f, 0.2f), new Color(0.8f, 0.7f, 0.45f), new Color(0.35f, 0.3f, 0.28f) };
        private static readonly Color[] Ap9Skin = { new Color(0.95f, 0.8f, 0.66f), new Color(0.8f, 0.6f, 0.45f), new Color(0.55f, 0.38f, 0.26f) };
        private static readonly Color[] Ap9Cloth = { new Color(0.3f, 0.45f, 0.65f), new Color(0.6f, 0.55f, 0.35f), new Color(0.35f, 0.55f, 0.35f), new Color(0.55f, 0.3f, 0.5f) };

        private static bool Api9Key(string key, string[] a, Map map)
        {
            if (!key.StartsWith("ap9"))
            {
                return false;
            }
            Api9Patch();
            switch (key)
            {
                case "ap9fix":
                    Api9Fixture(a[0], new IntVec3(I(a[1]), 0, I(a[2])), a.Length > 3 ? a[3] : "stand", map);
                    return true;
                case "ap9row":
                    ap9RowFrom = I(a[0]);
                    ap9RowTo = I(a[1]);
                    return true;
                case "ap9shieldcolour":
                    ap9Colour = new Color(F(a[0]), F(a[1]), F(a[2]));
                    return true;
                case "ap9items":
                    ap9Items = a[0].Split(';').Select(r => r.Split('+')).ToArray();
                    return true;
                case "ap9cloff":
                    CustomLayers.Off = a[0] == "1";
                    return true;
                case "ap9clsweep":
                    CustomLayerSweep(a[0]);
                    return true;
            }
            return Api9CheckKey(key, a, map) || Api43CheckKey(key, a, map);
        }

        private static void Api9Patch()
        {
            if (ap9Patched)
            {
                return;
            }
            ap9Patched = true;
            Harmony h = new Harmony("peren.isorim.probe.ap9");
            h.Patch(AccessTools.Method(typeof(MapComponentUtility), nameof(MapComponentUtility.MapComponentUpdate)), postfix: new HarmonyMethod(typeof(Probe), nameof(Api9Update)));
        }

        public static void Api9Update(Map map)
        {
            if (map != Find.CurrentMap)
            {
                return;
            }
            if (ap9Ticking)
            {
                // Walkers go back and forth on their lane (a real Goto job each way), one game tick per frame.
                foreach (Pawn p in ap9Walkers)
                {
                    IntVec3[] lane;
                    if (!p.Spawned || p.Downed || !ap9Lanes.TryGetValue(p.thingIDNumber, out lane) || p.pather.Moving)
                    {
                        continue;
                    }
                    IntVec3 to = p.Position == lane[1] ? lane[0] : lane[1];
                    p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, to), JobTag.DraftedOrder);
                }
                Find.TickManager.DoSingleTick();
            }
            Api9CheckUpdate(map);
            Api43CheckUpdate(map);
        }

        private static void Api9Clear()
        {
            foreach (Pawn p in ap9Spawned)
            {
                Thing gone = p.Corpse != null ? (Thing)p.Corpse : p;
                if (gone != null && !gone.Destroyed)
                {
                    gone.Destroy(DestroyMode.Vanish);
                }
            }
            ap9Spawned.Clear();
            ap9Walkers.Clear();
            ap9Lanes.Clear();
            ap9Roles.Clear();
            ap9Ticking = false;
        }

        private static Pawn Api9Pawn(string role, IntVec3 c, Rot4 rot, int seed, Map map, bool female, string[] wear, string weapon, bool drafted)
        {
            Pawn p;
            Rand.PushState(seed);
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                    fixedGender: female ? Gender.Female : Gender.Male, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f, developmentalStages: DevelopmentalStage.Adult,
                    canGeneratePawnRelations: false, forceNoIdeo: true, forbidAnyTitle: true));
                p.apparel.DestroyAll();
                p.equipment.DestroyAllEquipment();
                // The look fixed by the seed, whatever the world's random state: body, hair, skin, head.
                p.story.bodyType = female ? BodyTypeDefOf.Female : BodyTypeDefOf.Male;
                p.story.hairDef = DefDatabase<HairDef>.GetNamed(female ? "Bob" : "Mop");
                System.Random fix = new System.Random(seed);
                p.story.HairColor = Ap9Hair[fix.Next(Ap9Hair.Length)];
                p.story.skinColorOverride = Ap9Skin[fix.Next(Ap9Skin.Length)];
                p.story.headType = DefDatabase<HeadTypeDef>.GetNamed(female ? "Female_AverageNormal" : "Male_AverageNormal");
                if (p.style != null)
                {
                    p.style.beardDef = BeardDefOf.NoBeard;
                }
                int k = 0;
                foreach (string d in new[] { "Apparel_BasicShirt", "Apparel_Pants" }.Concat(wear))
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(d);
                    if (def == null)
                    {
                        Log.Warning("[IsoRim probe] ap9: no apparel " + d);
                        continue;
                    }
                    Apparel ap = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    CompColorable colorable = ap.TryGetComp<CompColorable>();
                    if (colorable != null)
                    {
                        // made of stuff: the stuff's colour; else the given colour (males) or one of a fixed set
                        if (def.MadeFromStuff)
                        {
                            colorable.Disable();
                        }
                        else
                        {
                            ap.SetColor(ap9Colour.HasValue && !female ? ap9Colour.Value : Ap9Cloth[(seed + k) % Ap9Cloth.Length]);
                        }
                    }
                    k++;
                    p.apparel.Wear(ap, false);
                }
                if (weapon != null)
                {
                    ThingDef w = DefDatabase<ThingDef>.GetNamed(weapon);
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(w, w.MadeFromStuff ? GenStuff.DefaultStuffFor(w) : null));
                }
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(p, c, map, rot);
            p.drafter.Drafted = drafted;
            p.Rotation = rot;
            ap9Spawned.Add(p);
            ap9Roles[p.thingIDNumber] = role;
            return p;
        }

        private static void Api9Fixture(string set, IntVec3 origin, string mode, Map map)
        {
            Api9Clear();
            string[][] rows = set == "vanilla" ? Ap9Vanilla : set == "api43" ? Ap9Api43 : set == "items" ? ap9Items : Ap9Vfem2;
            int made = 0;
            for (int r = 0; r < rows.Length; r++)
            {
                if (r < ap9RowFrom || r > ap9RowTo)
                {
                    continue;
                }
                IntVec3 c = origin + new IntVec3(0, 0, 4 * r);
                string item = rows[r][0];
                int seed = 9000 + 100 * r;
                if (mode == "walk")
                {
                    // lanes: male east-west, male north-south, female east-west, female north-south
                    IntVec3[][] lanes =
                    {
                        new[] { c, c + new IntVec3(3, 0, 0) },
                        new[] { c + new IntVec3(6, 0, 0), c + new IntVec3(6, 0, 2) },
                        new[] { c + new IntVec3(10, 0, 0), c + new IntVec3(13, 0, 0) },
                        new[] { c + new IntVec3(16, 0, 0), c + new IntVec3(16, 0, 2) },
                    };
                    for (int k = 0; k < lanes.Length; k++)
                    {
                        Pawn w = Api9Pawn(item + " walk " + (k < 2 ? "m" : "f") + (k % 2 == 0 ? "EW" : "NS"), lanes[k][0], k % 2 == 0 ? Rot4.East : Rot4.North,
                            seed + k, map, k >= 2, rows[r], "MeleeWeapon_Knife", true);
                        ap9Walkers.Add(w);
                        ap9Lanes[w.thingIDNumber] = lanes[k];
                        made++;
                    }
                    continue;
                }
                Rot4[] facing = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };
                string[] names = { "N", "E", "S", "W" };
                for (int k = 0; k < 8; k++)
                {
                    bool female = k >= 4;
                    Api9Pawn(item + " " + (female ? "f" : "m") + names[k % 4], c + new IntVec3(2 * k, 0, 0), facing[k % 4], seed + k, map, female, rows[r], "MeleeWeapon_Knife", true);
                    made++;
                }
                for (int k = 0; k < 2; k++)
                {
                    Pawn down = Api9Pawn(item + " lie " + (k == 0 ? "m" : "f"), c + new IntVec3(16 + 2 * k, 0, 0), Rot4.South, seed + 10 + k, map, k == 1, rows[r], null, false);
                    Hediff h = HediffMaker.MakeHediff(HediffDefOf.Anesthetic, down);
                    h.Severity = 1f;
                    down.health.AddHediff(h);
                    made++;
                }
                Api9Pawn(item + " undrafted mS", c + new IntVec3(20, 0, 0), Rot4.South, seed + 20, map, false, rows[r], "MeleeWeapon_Knife", false);
                Api9Pawn(item + " rifle mS", c + new IntVec3(22, 0, 0), Rot4.South, seed + 21, map, false, rows[r], "Gun_AssaultRifle", true);
                made += 2;
            }
            ap9Ticking = mode == "walk";
            Log.Message("[IsoRim probe] ap9fix " + set + " " + mode + ": " + made + " pawns from " + origin + " rows " + ap9RowFrom + ".." + Math.Min(ap9RowTo, rows.Length - 1));
        }
    }
}
