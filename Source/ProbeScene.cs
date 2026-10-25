using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsoRim
{
    // Harness-only scene keys for the probe file (see Probe): things the screenshot harness's
    // layouts cannot set up, for the 3D render checks. Cells are map cells (x,z).
    //   set=<setting>,<value>        an IsoRim setting (bool 0/1, int, float or text, e.g. lookName,Noir)
    //   delay=<frames>               apply this pass that many frames after the clock jump (default 10)
    //   fire=x,z                     a fire in the cell (also on a wall)
    //   gas=x,z,<GasType>,<radius>   a gas cloud
    //   snow=<depth>,x0,z0,x1,z1     snow over a rectangle
    //   terrain=<Def>,x0,z0,x1,z1    terrain over a rectangle
    //   spawn=<Def>,x,z[,<Stuff>|-[,<count>]]  a thing (the player's), optionally a stack
    //   stack=x,z,<count>            the first item in the cell brought to that count the game's way: split off
    //                                (and the rest destroyed) or a new stack absorbed (TryAbsorbStack)
    //   grow=x0,z0,x1,z1,<0-1>       every plant in the rectangle set to that growth (map mesh redrawn)
    //   leafless=x0,z0,x1,z1         every tree in the rectangle made leafless as by cold (map mesh redrawn)
    //   pollute=x0,z0,x1,z1          the ground polluted over a rectangle (Biotech)
    //   roof=0|1,x0,z0,x1,z1         built roof off or on over a rectangle
    //   flatroof=<fraction>          the concrete flat roof's thickness, of a wall height (-1: FlatRoofs.Thickness)
    //   active=0|1                   the 3D view off or on
    //   fog=x0,z0,x1,z1              fog of war over a rectangle
    //   mine=x,z                     the rock in the cell is mined out at once
    //   zone=x0,z0,x1,z1             a stockpile
    //   home=x0,z0,x1,z1             home area
    //   designate=mine|cut|deconstruct,x,z
    //   select=x,z | select=none     kept selected through the shots (the harness clears the selection)
    //   overlay=power|temperature|beauty|roof|fertility|none
    //   darkness=1                   Anomaly's unnatural darkness
    //   fleck=smoke|dust|glow,x,z,<count>
    //   ramp=0|1                     the grade's test band of known colours (Grade.TestPattern)
    //   gradegpu=N                   the grade pass drawn N more times a frame (to time it on the GPU)
    //   skygpu=N                     the sky drawn N more times a frame (to time the sky styles on the GPU)
    //   skycover=x                   the painted sky's cloud cover forced to x (0..1; -1: from the weather)
    //   sunlog=0|1                   every frame the sky's drawn sun (hour, glow, elevation, azimuth) goes to IsoRimSunLog.tsv
    //   named=1                      player faction and settlements count as named (no naming dialog pausing long runs)
    //   tiltgpu=N                    the two tilt-shift passes drawn N more times a frame (to time them)
    //   tiltdebug=0|1                tilt-shift tints the view red by blur amount (band checks)
    //   gradebench=N                 builds a grade table N times (alternating looks) and logs the times
    //   spritesweep=1                checks the CPU sprite sampling over every extruded / colour-sampled def
    //                                (SpriteSweep; writes IsoRimSpriteSweep.tsv/.txt, IsoRimSpriteColours.tsv)
    //   spritestats=1                logs the sprite sampling counters (time, reads, cache size)
    //   itemsheet=k,x0,z0,cols,rows  lays batch k of the extruded items out in a grid (contact sheets)
    //   coloursweep=1               model face colours vs the sprite for every vanilla/DLC def drawn as a model
    //   modelsweep=1                 lists the 3D form, model entry and drawing of every vanilla/DLC def
    //                                (ModelSweep; writes IsoRimModelSweep.tsv/.txt)
    //   perf=N[,label]               logs frame times (median, mean, p95) and IsoRim's tick over the next N frames,
    //                                with the realtime building bodies the game drew / IsoRim held back per frame
    //   uncap=1                      lifts the game's frame cap (Application.targetFrameRate) for perf
    //   fuel=x,z,0|1                 the refuelable building in the cell emptied or filled
    //   casket=x,z,0|1               the casket in the cell emptied, or given a new colonist to hold
    //   books=x,z,N[,Def:Def...]     the bookcase in the cell holds exactly N new books, kinds taken in turn
    //                                (default every book kind there is); books=x,z,+<Def> adds one,
    //                                books=x,z,- destroys the last, books=x,z,drop drops the first beside it
    //   haulbook=<Def>,bx,bz,cx,cz   a new book at (bx,bz) and the first free colonist told to haul it
    //                                into the bookcase at (cx,cz) (a real job: run tick= after it)
    //   tick=N                       N game ticks at once (N <= 900)
    //   cutjob=x,z                   the plant in the cell designated for cutting and the nearest free colonist ordered to cut it
    //   minejob=x,z                  the rock in the cell designated for mining and the nearest colonist able to
    //                                mine, stood beside it on the south, ordered to mine it (a real job: run film= after it)
    //   sway=0                       plant wind sway held still (every plant material's _SwayHead 0, each frame),
    //                                so plants and cards come out the same in every run
    //   burst=N[,k,tag,x,z,size]     a screenshot every k-th frame for N frames (renders\<harness tag>\burst_<tag>_<frame>.png),
    //                                camera held at (x,z,size); with "#startoff" as the probe file's first line the 3D view
    //                                starts off, so "active=1 burst=..." films the first switch-on (layout_firston)
    //   film=x,z,N[,tag]             films the mining of the rock in the cell: game ticks run fast until it is
    //                                one pick hit from breaking, then one tick and one screenshot per frame, the camera on the cells north of it
    //                                (renders\<harness tag>\film_<tag>_<frame>.png, with the rock's hit points,
    //                                fog of the cell north of it and tick logged) until N frames after it broke
    //   bookcheck=1                  logs, for each bookcase, the books held against the books baked
    //   paint=x,z,<ColorDef>|-        the building in the cell painted (Building.ChangePaint, as the paint job does)
    //   style=x,z,<ThingStyleDef>|-   the building in the cell given that style (as the game's "Set style" debug action)
    //   ritual=x,z                   the building in the cell counts as the target of a running ritual (RitualUtility.IsRitualTarget);
    //                                a ritual fire there burns (Effects.Burning)
    //   contain=x,z,<Def>[,<Stuff>]  a new thing put into the building's container (its own or its first holding comp);
    //                                a race def gives a new pawn, corpse:<race> a new corpse (reliquary, gibbet cage, dryad cocoon)
    //                                (contain=x,z,<Def>,-|<Stuff>,<count> for a stack)
    //   gestate=x,z,<RecipeDef>,forming|formed  a mech bill started at the gestator, its gathering finished; formed runs its cycles out
    //   vatchild=x,z,<age>           a new child colonist chosen for the growth vat (walks in: run tick= after it)
    //   embryo=x,z[,<done 0-1>]      a new embryo started in the powered growth vat, optionally that far along
    //   sculpt=x,z,<cycle key>[,new] the biosculpter pod filled and the nearest colonist (or a new one) taken in for that cycle
    //   hold=x,z,<PawnKindDef>[,opt...]  a new entity held on the holding platform in the cell; opts: stage:<LifeStageDef>,
    //                                open:<f> (nociosphere shells), shambler (made a shambler: people, animals), down
    //   chaincheck=1                 logs each holding platform: what it holds, its chains (to the entity or across), floor height
    //   sealflag=x,z                 the portal in the cell marked sealed (CompSealable) and its map mesh redrawn
    //   portalcheck=1                logs each map portal: enterable (and why not), hacked, form
    //   enterportal=x,z              the first free colonist ordered into the map portal in the cell (run tick= after)
    //   pocketmap=x,z[,size[,keepfog]]  the portal in the cell opens its pocket map, made current (fog cleared unless keepfog), camera held over its exit
    //   dressed=x,z,rot,age[:Body[:M|F]],Def[:Stuff];...  a new drafted colonist (adults male body, or that BodyTypeDef)
    //                                wearing/holding exactly those ("-" for nothing), facing rot; items hair:<HairDef>
    //                                and beard:<BeardDef> set those styles instead; Def:Stuff:RRGGBB also dyes it that colour
    //   bedrest=px,pz,bx,bz[,nomed]  the colonist in cell px,pz undrafted, made tired and ordered to lie down in the bed at
    //                                bx,bz (LayDown); nomed first turns the bed's medical toggle off (hospital beds start medical)
    //   downpawn=x,z                 the colonist in the cell hurt until downed (no bleeding wounds)
    //   bedcheck=1                   logs each free colonist: posture, bed, job, People.Report (hat, skips)
    //   testslots=Def,h0,h1..        (Api 28 test) gives the live ModelDef of that thing ModelDef.slots with those heights
    //                                (model units; -1 keeps the surface); testslots=Def,none takes them off again
    //   slotcheck=1                  logs each pawn in a bed: its slot and the heights worked out for it (Poses.SlotReport)
    //   standcheck=1                 logs each outfit stand: apparel, weapon, head hidden, side seen, drawn as its model
    //   workcheck=1                  logs each building with platform cables: work panel running or idle, cables drawn in 3D
    //   glasscheck=1                 logs each building drawn with glass (Glass.cs): its contents state and floor height
    //   containcheck=1               logs each building with a CompThingContainer: in the dynamic draw list, what it holds, height, form
    //   spincheck=1                logs each building with a turning part (Spinners): turning or still, angle; and every mote
    //   power=x,z                    the building in the cell counts as powered from now on
    //   enter=x,z[,<Xenotype>]       the nearest free colonist (made that xenotype) is sent into the enterable building (as its select button)
    //   entercheck=1                 logs each enterable building: working, chosen person, people inside
    //   faction=x,z,<FactionDef>     the building in the cell given to that faction (e.g. Mechanoid)
    //   shoot=x0,z0,x1,z1[,<Def>]    a revolver bullet (or that projectile) launched from one cell at the other, by the
    //                                pawn in the first cell (else the first colonist), at the pawn in the second if any
    //   bars=1                       progress bars are drawn in the shots (the game leaves them out in screenshot mode)
    //   halfdone=x,z                 the gene extractor in the cell half way through (its progress bar half full)
    //   emitcheck=1                  logs the smoke emitters and how many puffs are in the air
    //   opendoor=x,z                 the door in the cell opened by a colonist (Building_Door.StartManualOpenBy)
    //   walkto=px,pz,tx,tz           every free colonist drafted (so none wanders off), the one in cell px,pz ordered to walk
    //                                to tx,tz (a real Goto job: it opens doors on its path; run tick= or burst= after it)
    //   burstticks=k                 each burst= frame first runs k game ticks (films a real job, e.g. a door opening),
    //                                and logs every wide door's open fraction
    //   lockwall=h                   the wall height locked as ProjectZ locks it (View3D.LockedWallHeight; 0 = unlocked)
    //   hack=x,z[,half]              the hackable building in the cell hacked by a colonist, all the way or half way
    //   doorcheck=1                  logs every door: rotation, cells, open, open fraction
    //   charged=x,z                  the shield in the cell out of its recharge pause
    //   shieldcheck=1                logs every shield: active, charging, cooldown, selected, faction
    //   wake=x,z                   the dormant building in the cell (mech capsule) woken at once, as when disturbed
    //   eggs=x,z,N[,<Def>]           the egg box in the cell holds N eggs (default unfertilized chicken eggs)
    //   motecheck=1                  logs every mote on the map (def, cell, draw position, age, linked thing)
    //   statecheck=1                 logs, for each building with an empty-state sprite, its state against its built model
    //   blueprint=<Def>,x,z[,<Stuff>|-[,rot]]  the player's blueprint to build it there
    //   tickuntil=bursting|intercept|burst,N  game ticks until some turret fires a burst / some shield stops a projectile /
    //                                some slow fleck emitter (toxic spewer) throws its puffs (at most N <= 900)
    //   tickuntil=flash,N[,x,z]      game ticks until a shot's muzzle flash was made (by the shooter in that cell), at most N
    //   tipmarks=0|1                 2D pawns aiming on cards: a magenta dot where their shots start (the drawn barrel tip) and a cyan dot
    //                                where they started before (Projectiles.TipMarks), for measuring against the drawn barrel
    //   muzzle=0|1                   shots leave from the muzzle (Projectiles.Muzzle, default 1): the flash standing there and the
    //                                bullet drawn from there; 0: the game's flat flash and the bullet from the middle (before/after)
    //   muzzlepie=0|1                a 3D figure aiming a gun shows its aim pie at the muzzle (AimPies, default 1); 0: the game's pie on the ground
    //   clearflashes=1               the shot flashes in the air removed, no tick (the same frame without them, for diffs)
    //   interact=x,z                 the nearest free colonist is ordered to activate the building (as its float menu option)
    //   opencrate=x,z                the crate in the cell (Building_Crate) opened by the nearest free colonist not already opening one:
    //                                the game's Open job, as its float menu option (run tick= after it)
    //   social=x,z,chat|mood|thought the colonist there chats with the nearest other colonist, gains a mood bubble or a thought bubble
    //   lookcheck=1                  logs the free look key bindings (bound keys, labels) and whether they read as held
    //   manfire=x,z,tx,tz            the turret in the cell (a mortar: loaded with an HE shell) manned by the nearest free
    //                                colonist and ordered to fire at (tx,tz) (real jobs: run tickuntil=flash after it)
    //   turretcheck=1                logs every turret's verb state, aim override, gun rotation and which gun model it shows
    //   roofclip=0|1                 things under roofs clipped at the roof (RoofClip, default 1) or not (before/after shots)
    //   fogclip=0|1                  the fog clips what stands under the fog lid (RoofClip.FogOn, default 1) or not
    //   spacemap=<size>              a new empty space map (orbit tile, fog cleared) made the current map: later keys and the
    //                                harness shots work on it (layout shot coords are then plain map cells, see layout_space_edges)
    //   spaceedges=0|1               space platform sides in 3D (SpaceEdges, default 1) or the game flat strips (before/after)
    //   spaceplanet=0|1              perspective sky in space: the game's planet backdrop below the horizon (Sky.PlanetBelow) or stars all round
    //   spaceedgecheck=1             logs the space-edge sets on the map (loop texture sizes, colour) and both edge layers sizes
    //   ui=1                         map overlays the screenshot mode hides (selection, temperature,
    //                                roof, fertility, areas) are drawn in the shots; overlay=home
    //                                shows the home area
    public static partial class Probe
    {
        private static readonly List<IntVec3> keepSelected = new List<IntVec3>();
        private static string overlay = "none";

        private static int DelayOf(int k)
        {
            if (passes == null || k < 0 || k >= passes.Count)
            {
                return ApplyDelay;
            }
            foreach (string part in passes[k].Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("delay="))
                {
                    return int.Parse(part.Substring(6), CultureInfo.InvariantCulture);
                }
            }
            return ApplyDelay;
        }

        // (U<z>: that ground row on floor 2, V<z>: on floor 3, ProbeFixes)
        private static int I(string s)
        {
            return s.StartsWith("U") || s.StartsWith("V") ? int.Parse(s.Substring(1), CultureInfo.InvariantCulture) + UShift(s[0] == 'V' ? 2 : 1) : int.Parse(s, CultureInfo.InvariantCulture);
        }

        private static float F(string s)
        {
            return s.StartsWith("U") || s.StartsWith("V") ? float.Parse(s.Substring(1), CultureInfo.InvariantCulture) + UShift(s[0] == 'V' ? 2 : 1) : float.Parse(s, CultureInfo.InvariantCulture);
        }

        // Times the two halves of a grade table rebuild: working out the table, and the upload.
        private static void GradeBench(int n)
        {
            List<double> build = new List<double>(), upload = new List<double>();
            Color32[] px = null;
            Texture2D tex = new Texture2D(Grade.Size * Grade.Size, Grade.Size, TextureFormat.RGBA32, false, true);
            for (int i = 0; i < n; i++)
            {
                LookPreset p = Looks.Presets[i % (Looks.Presets.Count - 1)];
                System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                px = LookMath.Strip(p, Grade.Size, px);
                build.Add(w.Elapsed.TotalMilliseconds);
                w.Restart();
                tex.SetPixels32(px);
                tex.Apply(false);
                upload.Add(w.Elapsed.TotalMilliseconds);
            }
            UnityEngine.Object.Destroy(tex);
            build.Sort();
            upload.Sort();
            Log.Message("[IsoRim probe] grade table x" + n + ": build median " + build[n / 2].ToString("0.000") + " max " + build[n - 1].ToString("0.000")
                + " ms, upload median " + upload[n / 2].ToString("0.000") + " max " + upload[n - 1].ToString("0.000") + " ms");
        }

        private static CellRect Rect(string[] a, int i)
        {
            return CellRect.FromLimits(I(a[i]), I(a[i + 1]), I(a[i + 2]), I(a[i + 3]));
        }

        private static void Scene(string key, string[] a)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            switch (key)
            {
                case "delay":
                    return;
                case "set":
                {
                    FieldInfo f = AccessTools.Field(typeof(IsoRimSettings), a[0]);
                    object v = f.FieldType == typeof(bool) ? (object)(a[1] == "1" || a[1] == "true")
                        : f.FieldType == typeof(int) ? (object)I(a[1])
                        : f.FieldType == typeof(string) ? (object)a[1] : (object)F(a[1]);
                    if (a[0] == "showRoofs")
                    {
                        Roofs.Set((bool)v);   // as the button does: the roofs are rebuilt
                    }
                    else
                    {
                        f.SetValue(IsoRimMod.Settings, v);
                        if (a[0] == "roofStyle")
                        {
                            Roofs.Rebuild();   // as the options menu does
                        }
                    }
                    break;
                }
                case "aged":
                {
                    // aged=x: every ModelDef with aged > 0 drawn at strength x (-1: their own), entries rebuilt, map redrawn.
                    Aged.Override = F(a[0]);
                    Dictionary<ThingDef, ModelEntry> byThing = (Dictionary<ThingDef, ModelEntry>)AccessTools.Field(typeof(ModelLibrary), "byThing").GetValue(null);
                    MethodInfo make = AccessTools.Method(typeof(ModelLibrary), "MakeEntry");
                    int n = 0;
                    foreach (ModelDef md in DefDatabase<ModelDef>.AllDefsListForReading)
                    {
                        ThingDef td = md.aged > 0f && !md.thingDef.NullOrEmpty() && md.style.NullOrEmpty() ? DefDatabase<ThingDef>.GetNamedSilentFail(md.thingDef) : null;
                        if (td != null && byThing.ContainsKey(td))
                        {
                            byThing[td] = (ModelEntry)make.Invoke(null, new object[] { md });
                            n++;
                        }
                    }
                    ((System.Collections.IDictionary)AccessTools.Field(typeof(Forms), "cache").GetValue(null)).Clear();
                    DynamicModels.Forget();
                    map.mapDrawer.RegenerateEverythingNow();
                    Log.Message("[IsoRim probe] aged " + a[0] + ": " + n + " entries rebuilt");
                    break;
                }
                case "flatroof":
                    FlatRoofs.ThicknessOverride = F(a[0]);   // the concrete flat roof's thickness, of a wall height (-1: the default)
                    Roofs.Rebuild();
                    break;
                case "fire":
                {
                    // fire=x,z[,size[,shape]]: a fire on the ground (or on what stands there), size 1 by default; shape 0..3
                    // forces its flame's shape (Effects.BurnFlame).
                    Fire fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                    fire.fireSize = a.Length > 2 ? F(a[2]) : 1f;
                    if (a.Length > 3)
                    {
                        Effects.ForcedShape[fire.thingIDNumber] = I(a[3]);
                    }
                    GenSpawn.Spawn(fire, new IntVec3(I(a[0]), 0, I(a[1])), map, Rot4.North);
                    break;
                }
                case "fires":
                {
                    // fires=x0,z0,x1,z1,min,max: a fire in every cell of a rectangle, sizes spread between min and max.
                    float lo = F(a[4]), hi = F(a[5]);
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map) && !c.ContainsStaticFire(map))
                        {
                            Fire f = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                            f.fireSize = Mathf.Lerp(lo, hi, Hashing.Unit(c.x, c.z, 7));
                            GenSpawn.Spawn(f, c, map, Rot4.North);
                        }
                    }
                    break;
                }
                case "burn3d":
                    Effects.BurningOn = a[0] == "1";   // burn3d=0: burning fires as the flat sprite (before)
                    break;
                case "burn":
                {
                    // burn=x,z,size: the pawns in that cell set on fire, as the game attaches it (only pawns carry a fire).
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    foreach (Thing t in c.GetThingList(map).ToList())
                    {
                        if (t is Pawn)
                        {
                            t.TryAttachFire(F(a[2]), null);
                        }
                    }
                    break;
                }
                case "gas":
                    GasUtility.AddGas(new IntVec3(I(a[0]), 0, I(a[1])), map, (GasType)Enum.Parse(typeof(GasType), a[2]), F(a[3]));
                    break;
                case "snow":
                    foreach (IntVec3 c in Rect(a, 1))
                    {
                        if (c.InBounds(map))
                        {
                            map.snowGrid.SetDepth(c, F(a[0]));
                        }
                    }
                    break;
                case "terrain":
                {
                    TerrainDef def = DefDatabase<TerrainDef>.GetNamed(a[0]);
                    foreach (IntVec3 c in Rect(a, 1))
                    {
                        if (c.InBounds(map))
                        {
                            map.terrainGrid.SetTerrain(c, def);
                        }
                    }
                    break;
                }
                case "grow":
                {
                    // grow=x0,z0,x1,z1,<growth>: plants shown at a set growth stage (crop size checks).
                    float growth = float.Parse(a[4], CultureInfo.InvariantCulture);
                    int grown = 0;
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        foreach (Thing t in c.GetThingList(map).ToList())
                        {
                            Plant plant = t as Plant;
                            if (plant != null)
                            {
                                plant.Growth = growth;
                                plant.DirtyMapMesh(map);
                                grown++;
                            }
                        }
                    }
                    Log.Message("[IsoRim probe] grow: " + grown + " plants at " + growth.ToString("0.00"));
                    break;
                }
                case "leafless":
                {
                    // leafless=x0,z0,x1,z1: every tree in the rectangle loses its leaves as in a cold snap (Plant.MakeLeafless).
                    int bare = 0;
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        Plant plant = c.InBounds(map) ? c.GetPlant(map) : null;
                        if (plant != null && plant.def.plant != null && plant.def.plant.IsTree)
                        {
                            plant.MakeLeafless(Plant.LeaflessCause.Cold, false);
                            plant.DirtyMapMesh(map);
                            bare++;
                        }
                    }
                    Log.Message("[IsoRim probe] leafless: " + bare + " trees");
                    break;
                }
                case "pollute":
                {
                    // pollute=x0,z0,x1,z1: the ground polluted (Biotech), where the toxin-adapted trees keep their leaves.
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map))
                        {
                            map.pollutionGrid.SetPolluted(c, true, true);
                        }
                    }
                    break;
                }
                case "spawn":
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(a[0]);
                    ThingDef stuff = a.Length > 3 && a[3] != "-" ? DefDatabase<ThingDef>.GetNamed(a[3]) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    Thing t = ThingMaker.MakeThing(def, stuff);
                    if (a.Length > 4)
                    {
                        t.stackCount = Mathf.Clamp(I(a[4]), 1, def.stackLimit);
                    }
                    if (t.def.CanHaveFaction)
                    {
                        t.SetFaction(Faction.OfPlayer);
                    }
                    GenSpawn.Spawn(t, new IntVec3(I(a[1]), 0, I(a[2])), map, Rot4.North);
                    break;
                }
                case "stack":
                {
                    Thing item = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstItem(map);
                    int want = I(a[2]);
                    if (item == null || want < 1)
                    {
                        Log.Warning("[IsoRim probe] stack: no item at " + a[0] + "," + a[1]);
                        break;
                    }
                    int had = item.stackCount;
                    if (want < had)
                    {
                        item.SplitOff(had - want).Destroy();
                    }
                    else if (want > had)
                    {
                        Thing more = ThingMaker.MakeThing(item.def, item.Stuff);
                        more.stackCount = Mathf.Min(want - had, item.def.stackLimit);
                        item.TryAbsorbStack(more, true);
                    }
                    Log.Message("[IsoRim probe] stack " + item.def.defName + " at " + a[0] + "," + a[1] + ": " + had + " -> " + item.stackCount);
                    break;
                }
                case "roof":
                {
                    RoofDef def = a[0] == "0" ? null : RoofDefOf.RoofConstructed;
                    foreach (IntVec3 c in Rect(a, 1))
                    {
                        if (c.InBounds(map))
                        {
                            map.roofGrid.SetRoof(c, def);
                        }
                    }
                    break;
                }
                case "active":
                    View3D.Active = a[0] == "1";
                    break;
                case "fog":
                    map.fogGrid.Refog(Rect(a, 0));
                    break;
                case "mine":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetEdifice(map);
                    if (b != null)
                    {
                        b.Destroy(DestroyMode.KillFinalize);
                    }
                    break;
                }
                case "zone":
                {
                    Zone_Stockpile z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(z);
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map) && map.zoneManager.ZoneAt(c) == null && c.Standable(map))
                        {
                            z.AddCell(c);
                        }
                    }
                    break;
                }
                case "home":
                    foreach (IntVec3 c in Rect(a, 0))
                    {
                        if (c.InBounds(map))
                        {
                            map.areaManager.Home[c] = true;
                        }
                    }
                    break;
                case "designate":
                {
                    IntVec3 c = new IntVec3(I(a[1]), 0, I(a[2]));
                    if (a[0] == "mine")
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    else if (a[0] == "cut")
                    {
                        Plant p = c.GetPlant(map);
                        if (p != null)
                        {
                            map.designationManager.AddDesignation(new Designation(p, DesignationDefOf.CutPlant));
                        }
                    }
                    else
                    {
                        Building b = c.GetEdifice(map);
                        if (b != null)
                        {
                            map.designationManager.AddDesignation(new Designation(b, DesignationDefOf.Deconstruct));
                        }
                    }
                    break;
                }
                case "select":
                    if (a[0] == "none")
                    {
                        keepSelected.Clear();
                    }
                    else
                    {
                        keepSelected.Add(new IntVec3(I(a[0]), 0, I(a[1])));
                    }
                    break;
                case "overlay":
                {
                    overlay = a[0];
                    PlaySettings ps = Find.PlaySettings;
                    ps.showTemperatureOverlay = overlay == "temperature";
                    ps.showBeauty = overlay == "beauty";
                    ps.showRoofOverlay = overlay == "roof";
                    ps.showFertilityOverlay = overlay == "fertility";
                    break;
                }
                case "ramp":
                    Grade.TestPattern = a[0] == "1";
                    break;
                case "gradegpu":
                    Grade.ExtraDraws = I(a[0]);
                    break;
                case "skygpu":
                    Sky.ExtraDraws = I(a[0]);
                    break;
                case "skycover":
                    Sky.CoverOverride = F(a[0]);
                    break;
                case "sunlog":
                    Sky.SunLog = a[0] == "1";
                    break;
                case "named":
                    // named=1: the player faction and its settlements count as named, so the naming dialog (day 4.3)
                    // does not pause long runs.
                    Faction.OfPlayer.Name = "Probe";
                    foreach (RimWorld.Planet.Settlement ns in Find.WorldObjects.Settlements)
                    {
                        if (ns.Faction == Faction.OfPlayer)
                        {
                            ns.namedByPlayer = true;
                        }
                    }
                    break;
                case "tiltgpu":
                    TiltShift.ExtraDraws = I(a[0]);
                    break;
                case "tiltdebug":
                    TiltShift.Debug = I(a[0]) != 0;
                    break;
                case "gradebench":
                    GradeBench(I(a[0]));
                    break;
                case "spritesweep":
                    SpriteSweep.Run();
                    break;
                case "perf":
                    PerfHooks();
                    perfLeft = I(a[0]);
                    perfLabel = a.Length > 1 ? a[1] : "";
                    perfDt.Clear();
                    perfTick.Clear();
                    perfUpdate.Clear();
                    perfDynamic.Clear();
                    perfMesh.Clear();
                    Array.Clear(perfLapSum, 0, 8);
                    Array.Clear(perfLapMax, 0, 8);
                    bodiesDrawn = 0;
                    bodiesHeld = 0;
                    RenderPerfStart();
                    break;
                case "uncap":
                    Application.targetFrameRate = a[0] == "1" ? 1000 : Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
                    break;
                case "coloursweep":
                    ColourSweep.Run();
                    break;
                case "modelsweep":
                    ModelSweep.Run();
                    break;
                case "fuel":
                {
                    Building b = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompRefuelable fuel = b != null ? b.TryGetComp<CompRefuelable>() : null;
                    if (fuel == null)
                    {
                        Log.Warning("[IsoRim probe] no refuelable building at " + a[0] + "," + a[1]);
                        return;
                    }
                    if (a[2] == "0")
                    {
                        fuel.ConsumeFuel(fuel.Fuel);
                    }
                    else
                    {
                        fuel.Refuel(fuel.Props.fuelCapacity - fuel.Fuel);
                    }
                    break;
                }
                case "casket":
                {
                    Building_Casket casket = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_Casket;
                    if (casket == null)
                    {
                        Log.Warning("[IsoRim probe] no casket at " + a[0] + "," + a[1]);
                        return;
                    }
                    if (a[2] == "0")
                    {
                        casket.GetDirectlyHeldThings().ClearAndDestroyContents();
                    }
                    else if (!casket.HasAnyContents)
                    {
                        Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                        casket.TryAcceptThing(p, false);
                    }
                    break;
                }
                case "spritestats":
                    SpriteSweep.Stats();
                    break;
                case "contain":
                    Contain(map, a);
                    break;
                case "containcheck":
                {
                    object dd = AccessTools.Field(typeof(DynamicDrawManager), "drawThings").GetValue(map.dynamicDrawManager);
                    HashSet<Thing> drawn = new HashSet<Thing>();
                    foreach (object o in (System.Collections.IEnumerable)dd)
                    {
                        drawn.Add(o as Thing);
                    }
                    foreach (Thing ct in map.listerThings.AllThings.Where(x => x.TryGetComp<CompThingContainer>() != null).ToList())
                    {
                        CompThingContainer cc = ct.TryGetComp<CompThingContainer>();
                        Thing inner = cc.ContainedThing;
                        Log.Message("[IsoRim probe] containcheck " + ct.def.defName + " " + ct.Position + " dynamic " + drawn.Contains(ct) + " holds "
                            + (inner != null ? inner.def.defName + " graphic " + inner.Graphic + " spawned " + inner.Spawned : "nothing")
                            + " height " + Forms.HeightOf(ct) + " form " + Forms.For(ct.def).form);
                    }
                    break;
                }
                case "style":
                {
                    // As the game's "Set style" debug action: the style, then the map mesh rebuilt.
                    Building sb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    if (sb != null)
                    {
                        sb.StyleDef = a[2] == "-" ? null : DefDatabase<ThingStyleDef>.GetNamed(a[2]);
                        sb.DirtyMapMesh(map);
                        Log.Message("[IsoRim probe] style " + sb.def.defName + " " + sb.Position + " -> " + (sb.StyleDef != null ? sb.StyleDef.defName : "none"));
                    }
                    break;
                }
                case "settingswin":
                    SettingsWin(a);
                    break;
                case "tiltsettings":
                    TiltSettings(a);
                    break;
                case "itemsheet":
                    SpriteSweep.PlaceSheet(map, I(a[0]), I(a[1]), I(a[2]), I(a[3]), I(a[4]));
                    break;
                case "ui":
                    if (!uiPatched)
                    {
                        uiPatched = true;
                        Harmony h = new Harmony("peren.isorim17.probe");
                        h.Patch(AccessTools.Method(typeof(MapInterface), "MapInterfaceUpdate"),
                            prefix: new HarmonyMethod(typeof(Probe), nameof(OverlaysOn)), finalizer: new HarmonyMethod(typeof(Probe), nameof(OverlaysOff)));
                    }
                    break;
                case "darkness":
                {
                    GameCondition dark = GameConditionMaker.MakeConditionPermanent(GameConditionDefOf.UnnaturalDarkness);
                    map.gameConditionManager.RegisterCondition(dark);
                    dark.startTick = Find.TickManager.TicksGame - 2000;      // past its fade-in
                    map.gameConditionManager.SetTargetBrightness(0f, 100000f);   // the darkness sheet at once
                }
                    break;
                case "fleck":
                {
                    Vector3 at = new IntVec3(I(a[1]), 0, I(a[2])).ToVector3Shifted();
                    int n = a.Length > 3 ? I(a[3]) : 4;
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 p = at + new Vector3(Rand.Range(-0.4f, 0.4f), 0f, Rand.Range(-0.4f, 0.4f));
                        if (a[0] == "smoke")
                        {
                            FleckMaker.ThrowSmoke(p, map, 1.5f);
                        }
                        else if (a[0] == "dust")
                        {
                            FleckMaker.ThrowDustPuff(p, map, 2f);
                        }
                        else
                        {
                            FleckMaker.ThrowFireGlow(p, map, 1f);
                        }
                    }
                    break;
                }
                case "books":
                    Books(map, a);
                    break;
                case "haulbook":
                    HaulBook(map, a);
                    break;
                case "minejob":
                    MineJob(map, new IntVec3(I(a[0]), 0, I(a[1])));
                    break;
                case "cutjob":
                {
                    // cutjob=x,z: the plant in the cell designated for cutting and the nearest free colonist ordered to cut it
                    // (a real job: let the game run after it).
                    IntVec3 c = new IntVec3(I(a[0]), 0, I(a[1]));
                    Plant plant = c.GetPlant(map);
                    Pawn p = map.mapPawns.FreeColonistsSpawned.Where(x => !x.Downed).OrderBy(x => x.Position.DistanceToSquared(c)).FirstOrDefault();
                    if (plant == null || p == null)
                    {
                        Log.Warning("[IsoRim probe] cutjob: no plant or no colonist at " + c);
                        break;
                    }
                    map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = false;
                    }
                    bool ok = p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.CutPlantDesignated, plant), JobTag.Misc);
                    Log.Message("[IsoRim probe] cutjob: " + p.LabelShort + " cutting " + plant.def.defName + " at " + c + ": " + ok);
                    break;
                }
                case "sway":
                    swayStill = a[0] == "0";
                    break;
                case "burst":
                    burstLeft = I(a[0]);
                    burstEvery = a.Length > 1 ? Math.Max(1, I(a[1])) : 1;
                    burstTag = a.Length > 2 ? a[2] : "b";
                    burstCam = a.Length > 5 ? new Vector3(F(a[3]), F(a[4]), F(a[5])) : Vector3.zero;   // x, z, size
                    burstFrame = 0;
                    burstStart = Time.realtimeSinceStartup;
                    break;
                case "film":
                    filmCell = new IntVec3(I(a[0]), 0, I(a[1]));
                    filmAfter = I(a[2]);
                    filmTag = a.Length > 3 ? a[3] : "m";
                    filmFrame = 0;
                    filmGone = -1;
                    filming = true;
                    break;
                case "tick":
                {
                    int n = Mathf.Clamp(I(a[0]), 1, 900);   // under the clock jump that starts a pass
                    for (int i = 0; i < n; i++)
                    {
                        Find.TickManager.DoSingleTick();
                    }
                    foreach (Pawn tp in map.mapPawns.AllPawnsSpawned)
                    {
                        tp.Drawer.tweener.ResetTweenedPosToRoot();   // paused game: the drawn position follows the ticks
                    }
                    break;
                }
                case "unpower":
                {
                    // unpower=x,z: cuts the building's power for real (CompPowerTrader.PowerOn = false: PowerTurnedOff, as a
                    // broken grid), after taking it out of the harnesses' forced-power lists (this probe's, Capture17's).
                    Building up = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompPowerTrader upc = up != null ? up.TryGetComp<CompPowerTrader>() : null;
                    if (upc == null)
                    {
                        Log.Warning("[IsoRim probe] no powered building at " + a[0] + "," + a[1]);
                        return;
                    }
                    powered.Remove(up);
                    HashSet<Thing> capLights = AccessTools.StaticFieldRefAccess<HashSet<Thing>>(AccessTools.TypeByName("Capture17.Main"), "PoweredLights");
                    capLights?.Remove(up);
                    upc.PowerOn = false;
                    Log.Message("[IsoRim probe] unpower " + up.def.defName + " at " + up.Position + ": power " + upc.PowerOn + ", lit " + (up.TryGetComp<CompGlower>()?.Glows));
                    break;
                }
                case "flickoff":
                {
                    // flickoff=x,z: switches the building off with its own switch (CompFlickable: FlickedOff)
                    Building fb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompFlickable fl = fb != null ? fb.TryGetComp<CompFlickable>() : null;
                    if (fl == null)
                    {
                        Log.Warning("[IsoRim probe] no switch at " + a[0] + "," + a[1]);
                        return;
                    }
                    fl.SwitchIsOn = false;
                    Log.Message("[IsoRim probe] flickoff " + fb.def.defName + " at " + fb.Position + ": lit " + (fb.TryGetComp<CompGlower>()?.Glows));
                    break;
                }
                case "litcheck":
                {
                    // litcheck=x0,z0,x1,z1: each thing with a light in the rect, whether it is lit and its glow faces drawn lit
                    CellRect lr = CellRect.FromLimits(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
                    foreach (IntVec3 lc in lr)
                    {
                        Building lb = lc.InBounds(map) ? lc.GetFirstBuilding(map) : null;
                        CompGlower lg = lb != null && lb.Position == lc ? lb.TryGetComp<CompGlower>() : null;
                        if (lg != null)
                        {
                            Log.Message("[IsoRim probe] litcheck " + lb.def.defName + " at " + lc + " style " + (lb.StyleDef?.defName ?? "-") + ": Glows " + lg.Glows
                                + ", glow faces " + (StateModels.LightOut(lb) ? "unlit" : "lit"));
                        }
                    }
                    break;
                }
                case "bookcheck":
                    bookCheckAt = Time.frameCount + 30;   // once the queued rebuilds are done
                    break;
                case "power":
                {
                    Building pw = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    if (pw == null || pw.TryGetComp<CompPowerTrader>() == null)
                    {
                        Log.Warning("[IsoRim probe] no powered building at " + a[0] + "," + a[1]);
                        return;
                    }
                    if (!powerPatched)
                    {
                        powerPatched = true;
                        new Harmony("peren.isorim17.probe.power").Patch(AccessTools.PropertyGetter(typeof(CompPowerTrader), nameof(CompPowerTrader.PowerOn)),
                            postfix: new HarmonyMethod(typeof(Probe), nameof(PoweredPostfix)));
                    }
                    powered.Add(pw);
                    break;
                }
                case "ritual":
                {
                    // ritual=x,z: the building counts as the target of a running ritual (a party at the lightball);
                    // its own comps then react as in a real one.
                    Building rb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    if (rb == null)
                    {
                        Log.Warning("[IsoRim probe] no building at " + a[0] + "," + a[1]);
                        return;
                    }
                    if (!ritualPatched)
                    {
                        ritualPatched = true;
                        new Harmony("peren.isorim17.probe.ritual").Patch(AccessTools.Method(typeof(RitualUtility), nameof(RitualUtility.IsRitualTarget)),
                            postfix: new HarmonyMethod(typeof(Probe), nameof(RitualPostfix)));
                    }
                    ritualTargets.Add(rb);
                    break;
                }
                case "wallfitcheck":
                    // Every building that holds the roof or is drawn from a model at least 1 cell tall: whether its model
                    // follows the walls (ModelEntry.WallFit), its drawn height now and what Api.ModelHeight reports.
                    Log.Message("[IsoRim probe] wallfitcheck wall height " + View3D.WallHeight.ToString("0.00"));
                    foreach (ThingDef wd in DefDatabase<ThingDef>.AllDefs.Where(x => x.category == ThingCategory.Building && !x.IsFrame && !x.IsBlueprint))
                    {
                        ModelEntry we;
                        bool hasModel = ModelLibrary.TryGet(wd, out we) && we.HasModel && !we.def.flat;
                        float drawn = hasModel ? we.DrawnHeight(wd) : 0f;
                        if (wd.holdsRoof || drawn >= 1f)
                        {
                            Log.Message("[IsoRim probe] wallfitcheck " + wd.defName + " holdsRoof=" + (wd.holdsRoof ? 1 : 0) + " model=" + (hasModel ? we.def.defName : "-")
                                + " wallFit=" + (hasModel ? we.WallFit(wd).ToString("0.00") : "-") + " drawn=" + drawn.ToString("0.00") + " apiHeight=" + Api.ModelHeight(wd).ToString("0.00")
                                + " fill=" + wd.fillPercent.ToString("0.00"));
                        }
                    }
                    break;
                case "spincheck":
                    foreach (Thing st in map.listerThings.AllThings.Where(x => x.def.category == ThingCategory.Building).ToList())
                    {
                        ModelEntry se;
                        if (ModelLibrary.TryGet(st.def, out se) && se.spin != null)
                        {
                            Log.Message("[IsoRim probe] spincheck " + st.def.defName + " " + st.Position + " " + Spinners.Report(st));
                            // Motes are not in the lister: the ones in its cell.
                            foreach (Thing mt in st.Position.GetThingList(map).Where(x => x is Mote).ToList())
                            {
                                Log.Message("[IsoRim probe] spincheck   mote " + mt.def.defName + " drawn " + Spinners.MoteDrawAt_Prefix((Mote)mt));
                            }
                        }
                    }
                    break;
                case "enter":
                {
                    // enter=x,z: as the building's "select a person" button: the nearest free colonist walks in.
                    Building_Enterable be = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_Enterable;
                    Pawn bp = be == null ? null : map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).OrderBy(p => p.Position.DistanceToSquared(be.Position)).FirstOrDefault();
                    if (bp == null)
                    {
                        Log.Warning("[IsoRim probe] enter: no enterable building at " + a[0] + "," + a[1] + " or no colonist");
                        return;
                    }
                    if (bp.drafter != null)
                    {
                        bp.drafter.Drafted = false;
                    }
                    if (a.Length > 2 && bp.genes != null)
                    {
                        bp.genes.SetXenotype(DefDatabase<XenotypeDef>.GetNamed(a[2]));   // someone with genes to extract
                    }
                    AccessTools.Method(be.GetType(), "SelectPawn").Invoke(be, new object[] { bp });
                    AcceptanceReport can = be.CanAcceptPawn(bp);
                    Log.Message("[IsoRim probe] enter: " + bp.LabelShort + " at " + bp.Position + " goes into " + be.def.defName + " (can accept: " + (can.Accepted ? "yes" : can.Reason) + ", queued " + bp.jobs.jobQueue.Count + ")");
                    break;
                }
                case "faction":
                {
                    Building fb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    Faction ff = Find.FactionManager.FirstFactionOfDef(DefDatabase<FactionDef>.GetNamed(a[2]));
                    if (fb != null && ff != null)
                    {
                        fb.SetFaction(ff);
                    }
                    break;
                }
                case "shoot":
                {
                    // shoot=x0,z0,x1,z1[,<projectile def>]: a colonist's revolver bullet (or that projectile) flies from the
                    // first cell at the second; fired by the pawn standing in the first cell, else the first colonist; aimed
                    // at the pawn in the second cell if there is one.
                    IntVec3 from = new IntVec3(I(a[0]), 0, I(a[1])), to = new IntVec3(I(a[2]), 0, I(a[3]));
                    Pawn shooter = from.GetFirstPawn(map) ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                    Pawn aimed = to.GetFirstPawn(map);
                    LocalTargetInfo targ = aimed != null ? new LocalTargetInfo(aimed) : new LocalTargetInfo(to);
                    Projectile bullet = (Projectile)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed(a.Length > 4 ? a[4] : "Bullet_Revolver"), from, map);
                    bullet.Launch(shooter, from.ToVector3Shifted(), targ, targ, ProjectileHitFlags.IntendedTarget);
                    break;
                }
                case "bars":
                    if (!barsPatched)
                    {
                        barsPatched = true;
                        Harmony hb = new Harmony("peren.isorim17.probe.bars");
                        hb.Patch(AccessTools.Method(typeof(MoteProgressBar), "DrawAt"), prefix: new HarmonyMethod(typeof(Probe), nameof(BarsOn)), finalizer: new HarmonyMethod(typeof(Probe), nameof(BarsOff)));
                        hb.Patch(AccessTools.PropertyGetter(typeof(ScreenshotModeHandler), nameof(ScreenshotModeHandler.Active)), postfix: new HarmonyMethod(typeof(Probe), nameof(BarsShot)));
                    }
                    break;
                case "gestate":
                {
                    // gestate=x,z,<RecipeDef>,forming|formed: a mech bill started at the gestator as the game does
                    // (bill started by a colonist, its work finished: gathering -> forming); "formed" also runs the
                    // last gestation cycle out (Bill_Mech.ForceCompleteAllCycles, one bill tick: the mech is made).
                    Building_MechGestator mg = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_MechGestator;
                    Pawn doer = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                    if (mg == null || doer == null)
                    {
                        Log.Warning("[IsoRim probe] gestate: no mech gestator at " + a[0] + "," + a[1] + " or no colonist");
                        return;
                    }
                    Bill_Mech mb = (Bill_Mech)DefDatabase<RecipeDef>.GetNamed(a[2]).MakeNewBill();
                    mg.billStack.AddBill(mb);
                    mb.Notify_DoBillStarted(doer);
                    mb.Notify_BillWorkFinished(doer);
                    if (a.Length > 3 && a[3] == "formed")
                    {
                        mb.ForceCompleteAllCycles();
                        mb.BillTick();
                    }
                    Log.Message("[IsoRim probe] gestate " + mg.def.defName + " " + mg.Position + " " + a[2] + " -> " + mb.State + " mech " + (mg.GestatingMech != null ? mg.GestatingMech.def.defName : "none"));
                    break;
                }
                case "vatchild":
                {
                    // vatchild=x,z,<age>: a new child colonist of that age at the growth vat, chosen with the vat's
                    // select button: the child walks in (a real job: run tick= after it).
                    Building_GrowthVat gv = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_GrowthVat;
                    if (gv == null)
                    {
                        Log.Warning("[IsoRim probe] vatchild: no growth vat at " + a[0] + "," + a[1]);
                        return;
                    }
                    float age = F(a[2]);
                    Pawn kid = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                        fixedBiologicalAge: age, fixedChronologicalAge: age, developmentalStages: DevelopmentalStage.Child));
                    GenSpawn.Spawn(kid, gv.InteractionCell, map);
                    AccessTools.Method(gv.GetType(), "SelectPawn").Invoke(gv, new object[] { kid });
                    AcceptanceReport ok = gv.CanAcceptPawn(kid);
                    Log.Message("[IsoRim probe] vatchild: " + kid.LabelShort + " age " + age + " goes into the growth vat at " + gv.Position + " (can accept: " + (ok.Accepted ? "yes" : ok.Reason) + ")");
                    break;
                }
                case "embryo":
                {
                    // embryo=x,z[,<fraction done>]: a new embryo put in the powered growth vat and started as the vat
                    // does (TryGrowEmbryo); with a fraction, its gestation that far along.
                    Building_GrowthVat ev = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_GrowthVat;
                    if (ev == null)
                    {
                        Log.Warning("[IsoRim probe] embryo: no growth vat at " + a[0] + "," + a[1]);
                        return;
                    }
                    HumanEmbryo em = (HumanEmbryo)ThingMaker.MakeThing(ThingDefOf.HumanEmbryo);
                    ev.SelectEmbryo(em);
                    ev.innerContainer.TryAdd(em);
                    AccessTools.Method(typeof(Building_GrowthVat), "TryGrowEmbryo").Invoke(ev, null);
                    if (a.Length > 2 && ev.Working)
                    {
                        Traverse.Create(ev).Field("startTick").SetValue(Find.TickManager.TicksGame + (int)(540000 * (1f - F(a[2]))));
                    }
                    Log.Message("[IsoRim probe] embryo in growth vat " + ev.Position + ": working " + ev.Working + ", " + ev.EmbryoGestationPct.ToString("0.00") + " done");
                    break;
                }
                case "sculpt":
                {
                    // sculpt=x,z,<cycle key>: the pod's nutrition filled and the nearest free colonist taken in for
                    // that cycle (CompBiosculpterPod.TryAcceptPawn, as when they walk in).
                    Thing bp2 = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompBiosculpterPod pod = bp2 != null ? bp2.TryGetComp<CompBiosculpterPod>() : null;
                    Pawn sp = pod == null ? null : map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).OrderBy(p => p.Position.DistanceToSquared(bp2.Position)).FirstOrDefault();
                    if (pod != null && a.Length > 3 && a[3] == "new")
                    {
                        // sculpt=x,z,<key>,new: a new colonist at the pod instead
                        sp = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                        GenSpawn.Spawn(sp, bp2.InteractionCell, map);
                    }
                    if (sp == null)
                    {
                        Log.Warning("[IsoRim probe] sculpt: no biosculpter pod at " + a[0] + "," + a[1] + " or no colonist");
                        return;
                    }
                    Traverse.Create(pod).Field("liquifiedNutrition").SetValue(5f);
                    bool took = pod.TryAcceptPawn(sp, a[2]);
                    Log.Message("[IsoRim probe] sculpt: " + sp.LabelShort + " into " + bp2.def.defName + " " + bp2.Position + " cycle " + a[2] + ": " + (took ? "in" : "refused") + ", state " + pod.State);
                    break;
                }
                case "hold":
                {
                    // hold=x,z,<PawnKindDef>: a new entity of that kind (entities' faction) held on the holding platform
                    // (put into its container, which the harvesters linked to it watch to rebuild their cables).
                    Building_HoldingPlatform hp = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_HoldingPlatform;
                    if (hp == null)
                    {
                        Log.Warning("[IsoRim probe] hold: no holding platform at " + a[0] + "," + a[1]);
                        return;
                    }
                    PawnKindDef hk = DefDatabase<PawnKindDef>.GetNamed(a[2]);
                    Pawn ent = PawnGenerator.GeneratePawn(hk, hk.RaceProps.Humanlike || hk.RaceProps.Animal ? null : Faction.OfEntities);
                    for (int i = 3; i < a.Length; i++)
                    {
                        if (a[i].StartsWith("stage:")) SetLifeStage(ent, a[i].Substring(6));
                        else if (a[i] == "shambler") MutantUtility.SetPawnAsMutantInstantly(ent, MutantDefOf.Shambler);
                        else if (a[i] == "down") HealthUtility.DamageUntilDowned(ent, false);
                        else if (a[i].StartsWith("open:"))
                        {
                            CompNociosphere hs = ent.TryGetComp<CompNociosphere>();
                            if (hs != null)
                            {
                                hs.segScale = F(a[i].Substring(5));
                                Traverse.Create(hs).Field("segScaleGoal").SetValue(hs.segScale);
                            }
                        }
                    }
                    bool held = hp.innerContainer.TryAdd(ent);
                    Log.Message("[IsoRim probe] hold " + hp.Position + " <- " + ent.def.defName + " " + (held ? "held" : "refused") + ", occupied " + hp.Occupied
                        + ", stage " + (ent.ageTracker != null && ent.ageTracker.CurLifeStage != null ? ent.ageTracker.CurLifeStage.defName : "-")
                        + (ent.RaceProps.Humanlike ? ", " + People.Report(ent) : ", " + Mechs.Report(ent)));
                    break;
                }
                case "chaincheck":
                    foreach (Building_HoldingPlatform hp2 in map.listerThings.AllThings.OfType<Building_HoldingPlatform>().ToList())
                    {
                        Log.Message("[IsoRim probe] chaincheck " + hp2.Position + ": " + HoldingChains.Report(hp2) + ", floor " + Forms.HeightOf(hp2));
                    }
                    break;
                case "sealflag":
                {
                    // sealflag=x,z: the portal's CompSealable marked sealed and the map mesh redrawn, as Seal() does
                    // when the portal is kept (harness shortcut: Seal() itself needs a generated pocket map)
                    Thing sp2 = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompSealable cs2 = sp2 != null ? sp2.TryGetComp<CompSealable>() : null;
                    if (cs2 == null)
                    {
                        Log.Warning("[IsoRim probe] sealflag: nothing sealable at " + a[0] + "," + a[1]);
                        return;
                    }
                    Traverse.Create(cs2).Field("isSealed").SetValue(true);
                    sp2.DirtyMapMesh(map);
                    Log.Message("[IsoRim probe] sealflag " + sp2.def.defName + " " + sp2.Position);
                    break;
                }
                case "portalcheck":
                    foreach (MapPortal mp in map.listerThings.AllThings.OfType<MapPortal>().ToList())
                    {
                        string why;
                        bool enter = mp.IsEnterable(out why);
                        CompHackable mh = mp.TryGetComp<CompHackable>();
                        Log.Message("[IsoRim probe] portalcheck " + mp.def.defName + " " + mp.Position + ": enterable " + enter + " (" + why + ")"
                            + (mh != null ? ", hacked " + mh.IsHacked : "") + ", form " + Forms.For(mp.def).form);
                    }
                    break;
                case "dressed":
                {
                    // dressed=x,z,<rot 0-3>,<age>,<Def[:Stuff]>;...: a new colonist of that age (adults the male body,
                    // as the outfit stand) wearing exactly those things and holding the weapon among them, drafted
                    // so it stands still facing rot (to compare with an outfit stand).
                    string[] dageBody = a[3].Split(':');
                    float dage = F(dageBody[0]);
                    bool dchild = dage < 13f;
                    // One holding a gun can fire it (attack=, tickuntil=flash).
                    Rand.PushState(Gen.HashCombineInt(I(a[0]), I(a[1]) * 7919 + 17));   // fixed seed per cell: the same person every run
                    Pawn dp = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                        fixedGender: dageBody.Length > 2 ? (dageBody[2] == "F" ? Gender.Female : Gender.Male) : (Gender?)null,
                        mustBeCapableOfViolence: a[4].Contains("Gun_") || a[4].Contains("MeleeWeapon_") || a[4].Contains("Bow_") || a[4].Contains("Weapon_Grenade") || a[4].Contains("Pila"), fixedBiologicalAge: dage, fixedChronologicalAge: dage, developmentalStages: dchild ? DevelopmentalStage.Child : DevelopmentalStage.Adult));
                    Rand.PopState();
                    dp.apparel.DestroyAll();
                    dp.equipment.DestroyAllEquipment();
                    if (!dchild)
                    {
                        dp.story.bodyType = dageBody.Length > 1 ? DefDatabase<BodyTypeDef>.GetNamed(dageBody[1]) : BodyTypeDefOf.Male;
                    }
                    foreach (string item in a[4].Split(';'))
                    {
                        if (item == "-")
                        {
                            continue;   // nothing worn
                        }
                        string[] ds = item.Split(':');
                        if (ds[0] == "hair")
                        {
                            dp.story.hairDef = DefDatabase<HairDef>.GetNamed(ds[1]);
                            continue;
                        }
                        if (ds[0] == "beard")
                        {
                            dp.style.beardDef = DefDatabase<BeardDef>.GetNamed(ds[1]);
                            continue;
                        }
                        ThingDef dd = DefDatabase<ThingDef>.GetNamed(ds[0]);
                        ThingDef dstuff = ds.Length > 1 && ds[1] != "-" ? DefDatabase<ThingDef>.GetNamed(ds[1]) : (dd.MadeFromStuff ? GenStuff.DefaultStuffFor(dd) : null);
                        Thing dt = ThingMaker.MakeThing(dd, dstuff);
                        Color ddye;
                        if (ds.Length > 2 && ColorUtility.TryParseHtmlString("#" + ds[2], out ddye))
                        {
                            dt.SetColor(ddye);   // as dyed at a styling station (CompColorable)
                        }
                        if (dt is Apparel dap)
                        {
                            dp.apparel.Wear(dap, false, true);
                        }
                        else if (dt is ThingWithComps deq && dd.IsWeapon)
                        {
                            dp.equipment.AddEquipment(deq);
                        }
                    }
                    GenSpawn.Spawn(dp, new IntVec3(I(a[0]), 0, I(a[1])), map, new Rot4(I(a[2])));
                    dp.drafter.Drafted = true;
                    dp.Rotation = new Rot4(I(a[2]));
                    Log.Message("[IsoRim probe] dressed " + dp.LabelShort + " at " + dp.Position + " rot " + dp.Rotation + ": " + string.Join(", ", dp.apparel.WornApparel.Select(x => x.def.defName))
                        + (dp.equipment.Primary != null ? " + " + dp.equipment.Primary.def.defName : "") + "; " + People.Report(dp));
                    break;
                }
                case "bedrest":
                {
                    // bedrest=px,pz,bx,bz: the colonist in cell px,pz undrafted, made tired and ordered to lie down in the bed in cell bx,bz
                    // (JobDefOf.LayDown, as the "rest" order); run tick= after it so they walk there and lie down.
                    Pawn rp = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    Building_Bed rb = map.thingGrid.ThingsListAt(new IntVec3(I(a[2]), 0, I(a[3]))).OfType<Building_Bed>().FirstOrDefault();
                    if (rp != null && rb != null)
                    {
                        if (rp.drafter != null)
                        {
                            rp.drafter.Drafted = false;
                        }
                        if (rp.needs != null && rp.needs.rest != null)
                        {
                            rp.needs.rest.CurLevel = 0.1f;   // tired, so they sleep on rather than get up rested
                        }
                        if (a.Length > 4 && a[4] == "nomed")
                        {
                            rb.Medical = false;   // as the player's "medical" toggle: a healthy colonist may sleep in it
                        }
                        bool taken = rp.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.LayDown, rb), JobTag.Misc);
                        Log.Message("[IsoRim probe] bedrest " + rp.LabelShort + " -> " + rb.def.defName + " " + rb.Position + ": ordered " + taken);
                    }
                    else
                    {
                        Log.Warning("[IsoRim probe] bedrest: no colonist or bed at " + a[0] + "," + a[1] + " / " + a[2] + "," + a[3]);
                    }
                    break;
                }
                case "downpawn":
                {
                    // downpawn=x,z: the colonist in the cell hurt until downed (HealthUtility.DamageUntilDowned, no bleeding wounds).
                    Pawn dpn = map.thingGrid.ThingsListAt(new IntVec3(I(a[0]), 0, I(a[1]))).OfType<Pawn>().FirstOrDefault();
                    if (dpn != null)
                    {
                        if (dpn.drafter != null)
                        {
                            dpn.drafter.Drafted = false;
                        }
                        HealthUtility.DamageUntilDowned(dpn, false);
                        Log.Message("[IsoRim probe] downpawn " + dpn.LabelShort + ": downed " + dpn.Downed);
                    }
                    break;
                }
                case "testslots":
                {
                    // testslots=Def,h0,h1..: the live ModelDef of that thing gets ModelDef.slots with those heights (Api 28 test);
                    // testslots=Def,none takes them off again.
                    ThingDef sd = DefDatabase<ThingDef>.GetNamedSilentFail(a[0]);
                    ThingForm sf = sd != null ? Forms.For(sd) : null;
                    ModelDef smd = sf != null && sf.model != null ? sf.model.def : null;
                    if (smd == null)
                    {
                        Log.Warning("[IsoRim probe] testslots: no model for " + a[0]);
                        break;
                    }
                    if (a.Length > 1 && a[1] == "none")
                    {
                        smd.slots = null;
                    }
                    else
                    {
                        smd.slots = new List<ModelSlot>();
                        for (int si = 1; si < a.Length; si++)
                        {
                            smd.slots.Add(new ModelSlot { height = F(a[si]) });
                        }
                    }
                    Log.Message("[IsoRim probe] testslots " + smd.defName + ": " + (smd.slots == null ? "none" : string.Join(", ", smd.slots.Select(x => x.height.ToString("0.00")))));
                    break;
                }
                case "slotcheck":
                    foreach (Pawn sp in map.mapPawns.AllPawnsSpawned.Where(x => x.CurrentBed() != null).OrderBy(x => x.Position.x).ThenBy(x => x.Position.z).ToList())
                    {
                        Log.Message("[IsoRim probe] slotcheck " + sp.LabelShort + " " + sp.Position + ": " + Poses.SlotReport(sp));
                    }
                    break;
                case "bedcheck":
                    foreach (Pawn bp in map.mapPawns.FreeColonistsSpawned.ToList())
                    {
                        Building_Bed bb = bp.CurrentBed();
                        Log.Message("[IsoRim probe] bedcheck " + bp.LabelShort + " " + bp.Position + ": posture " + bp.GetPosture() + ", bed " + (bb != null ? bb.def.defName : "none")
                            + ", job " + (bp.CurJobDef != null ? bp.CurJobDef.defName : "none") + "; " + People.Report(bp));
                    }
                    break;
                case "standcheck":
                    foreach (Building_OutfitStand os in map.listerThings.AllThings.OfType<Building_OutfitStand>().ToList())
                    {
                        Log.Message("[IsoRim probe] standcheck " + os.def.defName + " " + os.Position + " rot " + os.Rotation + ": " + Stands.Report(os));
                    }
                    break;
                case "workcheck":
                    foreach (Thing wt in map.listerThings.AllThings.Where(x => x.def.category == ThingCategory.Building && x.TryGetComp<CompCableConnection>() != null).ToList())
                    {
                        Log.Message("[IsoRim probe] workcheck " + wt.def.defName + " " + wt.Position + " rot " + wt.Rotation + ": " + WorkPanels.Report(wt)
                            + ", cables " + Cables.Count(wt.TryGetComp<CompCableConnection>()) + ", form " + Forms.For(wt.def).form);
                    }
                    break;
                case "glasscheck":
                    foreach (Thing gt in map.listerThings.AllThings.Where(x => x.def.category == ThingCategory.Building && Glass.EntryFor(x) != null).ToList())
                    {
                        string state = "";
                        if (gt is Building_MechGestator g1)
                        {
                            state = "bill " + (g1.ActiveBill != null ? g1.ActiveBill.State.ToString() : "none") + " mech " + (g1.GestatingMech != null ? g1.GestatingMech.def.defName : "none");
                        }
                        else if (gt is Building_GrowthVat g2)
                        {
                            state = "working " + g2.Working + " child " + (g2.SelectedPawn != null ? g2.SelectedPawn.LabelShort + " inside " + g2.innerContainer.Contains(g2.SelectedPawn) : "none")
                                + " embryo " + (g2.selectedEmbryo != null ? g2.EmbryoGestationPct.ToString("0.00") : "none");
                        }
                        else if (gt is Building_WastepackAtomizer g3)
                        {
                            state = "wastepacks " + g3.Atomizer.TotalStackCount;
                        }
                        else if (gt.TryGetComp<CompBiosculpterPod>() is CompBiosculpterPod g4)
                        {
                            state = "pod " + g4.State + " occupant " + (g4.Occupant != null ? g4.Occupant.LabelShort : "none");
                        }
                        Log.Message("[IsoRim probe] glasscheck " + gt.def.defName + " " + gt.Position + " rot " + gt.Rotation + ": " + state + ", floor " + Forms.HeightOf(gt));
                    }
                    break;
                case "halfdone":
                {
                    // halfdone=x,z: the gene extractor in the cell half way through its work.
                    Building hb2 = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    if (hb2 is Building_GeneExtractor)
                    {
                        Traverse.Create(hb2).Field("ticksRemaining").SetValue(15000);
                    }
                    break;
                }
                case "emitcheck":
                {
                    string sys = string.Join(", ", map.flecks.Systems.Select(fs => fs.GetType().Name + " " + ((Traverse.Create(fs).Field("dataGametime").GetValue() as System.Collections.ICollection)?.Count ?? -1)));
                    foreach (Thing et in map.listerThings.AllThings.Where(x => x.TryGetComp<CompThrownFleckEmitter>() != null))
                    {
                        Log.Message("[IsoRim probe] emitcheck " + et.def.defName + " since last " + et.TryGetComp<CompThrownFleckEmitter>().ticksSinceLastEmitted + " ours " + Emitters.Count + " | " + sys);
                    }
                    break;
                }
                case "walkto":
                {
                    IntVec3 wFrom = new IntVec3(I(a[0]), 0, I(a[1])), wTo = new IntVec3(I(a[2]), 0, I(a[3]));
                    Pawn walker = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.Position == wFrom);
                    if (walker == null)
                    {
                        Log.Warning("[IsoRim probe] walkto: no colonist at " + a[0] + "," + a[1]);
                        return;
                    }
                    foreach (Pawn dp in map.mapPawns.FreeColonistsSpawned)
                    {
                        if (dp.drafter != null)
                        {
                            dp.drafter.Drafted = true;
                        }
                    }
                    bool taken = walker.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, wTo), JobTag.DraftedOrder);
                    Log.Message("[IsoRim probe] walkto " + walker.LabelShort + " " + wFrom + " -> " + wTo + ": " + taken);
                    break;
                }
                case "burstticks":
                    burstTicks = Math.Max(0, I(a[0]));
                    break;
                case "lockwall":
                    View3D.LockedWallHeight = F(a[0]);
                    break;
                case "opendoor":
                {
                    // opendoor=x,z: the door in the cell opened as a colonist walking through opens it.
                    Building_Door od = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_Door;
                    Pawn opener = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                    if (od == null || opener == null)
                    {
                        Log.Warning("[IsoRim probe] opendoor: no door at " + a[0] + "," + a[1] + " or no colonist");
                        return;
                    }
                    od.StartManualOpenBy(opener);
                    break;
                }
                case "hack":
                {
                    // hack=x,z[,half]: the hackable building in the cell hacked by a colonist (CompHackable.Hack),
                    // all the way, or half way (still locked).
                    Thing hk = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompHackable ch = hk != null ? hk.TryGetComp<CompHackable>() : null;
                    if (ch == null)
                    {
                        Log.Warning("[IsoRim probe] hack: nothing hackable at " + a[0] + "," + a[1]);
                        return;
                    }
                    float need = ch.defence * (a.Length > 2 && a[2] == "half" ? 0.5f : 1f);
                    ch.Hack(need, map.mapPawns.FreeColonistsSpawned.FirstOrDefault(), true);
                    Log.Message("[IsoRim probe] hack " + hk.def.defName + " " + hk.Position + ": hacked " + ch.IsHacked + ", progress " + ch.ProgressPercent.ToString("0.00"));
                    break;
                }
                case "enterportal":
                {
                    // enterportal=x,z: the first free colonist ordered to go into the map portal in the cell (the game's
                    // own job; its pocket map is made when the colonist steps in). Run tick= after it.
                    MapPortal ep = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as MapPortal;
                    Pawn ec = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                    if (ep == null || ec == null)
                    {
                        Log.Warning("[IsoRim probe] enterportal: no portal or colonist at " + a[0] + "," + a[1]);
                        return;
                    }
                    bool eok = ec.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterPortal, ep), JobTag.Misc);
                    Log.Message("[IsoRim probe] enterportal " + ec.LabelShort + " -> " + ep.def.defName + " " + ep.Position + ": ordered " + eok);
                    break;
                }
                case "pocketmap":
                {
                    // pocketmap=x,z[,size]: the map portal in the cell opens its pocket map (generated as on entering), fog
                    // cleared, made the current map; logs where the exit stands and how it is drawn. The camera is then
                    // held over the exit (root size, default 4) for the shots, wherever the map put it.
                    MapPortal pp = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as MapPortal;
                    if (pp == null)
                    {
                        Log.Warning("[IsoRim probe] pocketmap: no map portal at " + a[0] + "," + a[1]);
                        return;
                    }
                    Map pm = pp.GetOtherMap();
                    if (!(a.Length > 3 && a[3] == "keepfog"))
                    {
                        pm.fogGrid.ClearAllFog();
                    }
                    Current.Game.CurrentMap = pm;
                    Thing px = pp.exit;
                    pocketExit = px;
                    pocketCamSize = a.Length > 2 ? F(a[2]) : 4f;
                    Log.Message("[IsoRim probe] pocketmap " + pm.Size + " exit " + (px != null ? px.def.defName + " " + px.Position + " rot " + px.Rotation.AsInt
                        + " drawPos " + px.DrawPos + " form " + Forms.For(px.def).form + " roofed " + px.Position.Roofed(pm) : "none"));
                    break;
                }
                case "doorcheck":
                    foreach (Building_Door dc in map.listerThings.AllThings.OfType<Building_Door>())
                    {
                        CompHackable dh = dc.GetComp<CompHackable>();
                        Log.Message("[IsoRim probe] doorcheck " + dc.def.defName + " " + dc.Position + " rot " + dc.Rotation.ToStringHuman() + " cells " + dc.OccupiedRect() + " open " + dc.Open
                            + " pct " + Traverse.Create(dc).Property("OpenPct").GetValue<float>().ToString("0.00")
                            + (dh != null ? " hacked " + dh.IsHacked : "") + " cut " + WallBoxes.CellCut(map, dc.Position)
                            + " stuck " + Traverse.Create(dc).Property("StuckOpen").GetValue<bool>() + " power " + dc.DoorPowerOn);
                    }
                    break;
                case "charged":
                {
                    // charged=x,z: the shield in the cell is not in its recharge pause (the game picks that time at random).
                    Building cb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompProjectileInterceptor ci = cb != null ? cb.TryGetComp<CompProjectileInterceptor>() : null;
                    if (ci != null)
                    {
                        Traverse.Create(ci).Field("startedChargingTick").SetValue(-1);
                    }
                    break;
                }
                case "shieldcheck":
                    foreach (Thing sh in map.listerThings.AllThings.Where(x => x.TryGetComp<CompProjectileInterceptor>() != null))
                    {
                        CompProjectileInterceptor sc = sh.TryGetComp<CompProjectileInterceptor>();
                        Log.Message("[IsoRim probe] shieldcheck " + sh.def.defName + " " + sh.Position + " active " + sc.Active + " charging " + sc.Charging + " cooldown " + sc.OnCooldown + " selected " + Find.Selector.IsSelected(sh) + " faction " + (sh.Faction != null ? sh.Faction.def.defName : "-"));
                    }
                    break;
                case "wake":
                {
                    Building wb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompWakeUpDormant wc = wb != null ? wb.TryGetComp<CompWakeUpDormant>() : null;
                    if (wc == null)
                    {
                        Log.Warning("[IsoRim probe] nothing to wake at " + a[0] + "," + a[1]);
                        return;
                    }
                    wc.Activate(map.mapPawns.FreeColonistsSpawned.FirstOrDefault(), true, false, true);   // as being disturbed: wakes at once
                    break;
                }
                case "eggs":
                {
                    Building eb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompThingContainer ec = eb != null ? eb.TryGetComp<CompThingContainer>() : null;
                    if (ec == null)
                    {
                        Log.Warning("[IsoRim probe] no container at " + a[0] + "," + a[1]);
                        return;
                    }
                    ec.innerContainer.ClearAndDestroyContents();
                    if (I(a[2]) > 0)
                    {
                        Thing egg = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(a.Length > 3 ? a[3] : "EggChickenUnfertilized"));
                        egg.stackCount = I(a[2]);
                        ec.innerContainer.TryAdd(egg);
                    }
                    break;
                }
                case "entercheck":
                    foreach (Building_Enterable en in map.listerThings.AllThings.OfType<Building_Enterable>())
                    {
                        Effecter bar = Traverse.Create(en).Field("progressBar").GetValue<Effecter>();
                        MoteProgressBar barMote = bar != null && bar.children.Count > 0 ? (bar.children[0] as SubEffecter_ProgressBar)?.mote : null;
                        Log.Message("[IsoRim probe] entercheck " + en.def.defName + " " + en.Position + " working " + en.Working + " holds " + (en.SelectedPawn != null ? en.SelectedPawn.LabelShort : "-") + " inside " + en.innerContainer.Count
                            + " bar draws " + barDraws + "/" + barShotChecks + " zoom " + Find.CameraDriver.CurrentZoom + " progress bar " + (barMote == null ? "none" : "spawned " + barMote.Spawned + " at " + barMote.DrawPos + " progress " + barMote.progress.ToString("0.000")));
                    }
                    break;
                case "motecheck":
                    foreach (Mote mo in map.listerThings.AllThings.OfType<Mote>())
                    {
                        Log.Message("[IsoRim probe] motecheck " + mo.def.defName + " " + mo.Position + " draw " + mo.DrawPos + " age " + mo.AgeSecs.ToString("0.00") + " link " + (mo.link1.Target.HasThing ? mo.link1.Target.Thing.def.defName : "-"));
                    }
                    break;
                case "statecheck":
                    foreach (Thing st in map.listerThings.AllThings.Where(x => StateModels.CompOf(x) != null))
                    {
                        Log.Message("[IsoRim probe] statecheck " + st.def.defName + " " + st.Position + " empty " + StateModels.IsEmpty(st)
                            + " built as " + StateModels.Report(st) + " model stands in " + StateModels.StandsIn(st));
                    }
                    break;
                case "blueprint":
                {
                    ThingDef bd = DefDatabase<ThingDef>.GetNamed(a[0]);
                    ThingDef bs = a.Length > 3 && a[3] != "-" ? DefDatabase<ThingDef>.GetNamed(a[3]) : (bd.MadeFromStuff ? GenStuff.DefaultStuffFor(bd) : null);
                    GenConstruct.PlaceBlueprintForBuild(bd, new IntVec3(I(a[1]), 0, I(a[2])), map, a.Length > 4 ? new Rot4(I(a[4])) : Rot4.North, Faction.OfPlayer, bs);
                    break;
                }
                case "tickuntil":
                {
                    // tickuntil=bursting,N: game ticks one at a time until some turret is firing a burst (at most N).
                    // tickuntil=flash,N[,x,z]: until a shot's muzzle flash (by the shooter in that cell) was made in the last tick.
                    int limit = Mathf.Clamp(I(a[1]), 1, 900), done = 0;
                    int flashesSeen = Projectiles.Flashes;
                    IntVec3 flasher = a.Length > 3 ? new IntVec3(I(a[2]), 0, I(a[3])) : IntVec3.Invalid;
                    Func<bool> reached = a[0] == "flash"
                        ? (Func<bool>)(() =>
                        {
                            bool hit = Projectiles.Flashes != flashesSeen
                                && (!flasher.IsValid || (Projectiles.LastFlasher != null && Projectiles.LastFlasher.Position == flasher));
                            flashesSeen = Projectiles.Flashes;
                            return hit;
                        })
                        : a[0] == "burst"
                        ? (Func<bool>)(() => map.listerThings.AllThings.Any(t => t.TryGetComp<CompThrownFleckEmitter>() is CompThrownFleckEmitter fe
                            && ((CompProperties_ThrownFleckEmitter)fe.props).emissionInterval >= 100 && fe.ticksSinceLastEmitted == 0))
                        : a[0] == "intercept"
                        ? (Func<bool>)(() => map.listerThings.AllThings.Any(t => t.TryGetComp<CompProjectileInterceptor>() is CompProjectileInterceptor pi
                            && Traverse.Create(pi).Field("lastInterceptTicks").GetValue<int>() == Find.TickManager.TicksGame))
                        : () => map.listerThings.AllThings.OfType<Building_Turret>().Any(t => t.CurrentEffectiveVerb != null && t.CurrentEffectiveVerb.state == VerbState.Bursting);
                    while (done < limit && !reached())
                    {
                        Find.TickManager.DoSingleTick();
                        done++;
                    }
                    Log.Message("[IsoRim probe] tickuntil " + a[0] + ": " + done + " ticks");
                    break;
                }
                case "manfire":
                {
                    // manfire=x,z,tx,tz: the turret in the cell (a mortar: loaded with a high-explosive shell) manned by the
                    // nearest free colonist (a real ManTurret job) and ordered to fire at the cell (tx,tz), as the player would.
                    Building_TurretGun mt = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_TurretGun;
                    Pawn mp = mt != null ? map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).OrderBy(p => p.Position.DistanceToSquared(mt.Position)).FirstOrDefault() : null;
                    if (mt == null || mp == null)
                    {
                        Log.Warning("[IsoRim probe] manfire: no turret at " + a[0] + "," + a[1] + " or no colonist");
                        return;
                    }
                    CompChangeableProjectile shells = mt.gun != null ? mt.gun.TryGetComp<CompChangeableProjectile>() : null;
                    if (shells != null && !shells.Loaded)
                    {
                        shells.LoadShell(ThingDefOf.Shell_HighExplosive, 1);
                    }
                    Job man = JobMaker.MakeJob(JobDefOf.ManTurret, mt);
                    man.playerForced = true;
                    mp.jobs.StartJob(man, JobCondition.InterruptForced);
                    mt.OrderAttack(new LocalTargetInfo(new IntVec3(I(a[2]), 0, I(a[3]))));
                    Log.Message("[IsoRim probe] manfire: " + mp.LabelShort + " mans " + mt.def.defName + " (" + (mp.CurJob != null ? mp.CurJob.def.defName : "no job") + "), loaded " + (shells != null && shells.Loaded));
                    break;
                }
                case "interact":
                {
                    Building ib = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    CompInteractable ic = ib != null ? ib.TryGetComp<CompInteractable>() : null;
                    Pawn ip = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).OrderBy(p => p.Position.DistanceToSquared(ib != null ? ib.Position : IntVec3.Zero)).FirstOrDefault();
                    if (ic == null || ip == null)
                    {
                        Log.Warning("[IsoRim probe] interact: nothing to interact with at " + a[0] + "," + a[1] + " or no colonist");
                        return;
                    }
                    if (ip.drafter != null)
                    {
                        ip.drafter.Drafted = false;
                    }
                    ic.OrderForceTarget(ip);   // as the float menu option does: the colonist walks over and activates it
                    Log.Message("[IsoRim probe] interact: " + ip.LabelShort + " at " + ip.Position + " activates " + ib.def.defName + " (" + (ip.CurJob != null ? ip.CurJob.def.defName : "no job") + ")");
                    break;
                }
                case "opencrate":
                {
                    Building_Crate cr = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map) as Building_Crate;
                    Pawn op = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && (p.CurJob == null || p.CurJob.def != JobDefOf.Open))
                        .OrderBy(p => p.Position.DistanceToSquared(cr != null ? cr.Position : IntVec3.Zero)).FirstOrDefault();
                    if (cr == null || op == null)
                    {
                        Log.Warning("[IsoRim probe] opencrate: no crate at " + a[0] + "," + a[1] + " or no free colonist");
                        return;
                    }
                    if (op.drafter != null)
                    {
                        op.drafter.Drafted = false;
                    }
                    op.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Open, cr), JobTag.Misc);
                    Log.Message("[IsoRim probe] opencrate: " + op.LabelShort + " at " + op.Position + " opens " + cr.def.defName + " at " + cr.Position
                        + " (can open " + cr.CanOpen + ", job " + (op.CurJob != null ? op.CurJob.def.defName : "none") + ")");
                    break;
                }
                case "lookcheck":
                {
                    // lookcheck=1: the free look key bindings as loaded (defaults, labels) and whether they read as held now.
                    foreach (KeyBindingDef kd in new[] { R3DKeyDefOf.R3D_LookModifier })
                    {
                        Log.Message("[IsoRim probe] lookcheck " + (kd == null ? "missing" : kd.defName + " '" + kd.LabelCap + "' A=" + KeyPrefs.KeyPrefsData.GetBoundKeyCode(kd, KeyPrefs.BindingSlot.A)
                            + " B=" + KeyPrefs.KeyPrefsData.GetBoundKeyCode(kd, KeyPrefs.BindingSlot.B) + " held " + View3D.BindingHeld(kd, false)));
                    }
                    Log.Message("[IsoRim probe] lookcheck LookHeld " + View3D.LookHeld() + " lookMode " + IsoRimMod.Settings.lookMode);
                    break;
                }
                case "social":
                {
                    // social=x,z,chat|mood|thought: the colonist in that cell chats with the nearest other colonist
                    // (Pawn_InteractionsTracker.TryInteractWith, as the interaction worker does), gains a bad memory
                    // with a mood bubble, or shows a thought bubble (the disoriented icon, as HediffComp_Disorientation).
                    IntVec3 sc = new IntVec3(I(a[0]), 0, I(a[1]));
                    Pawn sp = sc.GetFirstPawn(map);
                    string kind = a.Length > 2 ? a[2] : "chat";
                    if (sp == null)
                    {
                        Log.Warning("[IsoRim probe] social: nobody at " + sc);
                        return;
                    }
                    bool ok = false;
                    if (kind == "chat")
                    {
                        Pawn other = map.mapPawns.FreeColonistsSpawned.Where(p => p != sp).OrderBy(p => p.Position.DistanceToSquared(sc)).FirstOrDefault();
                        ok = other != null && sp.interactions.TryInteractWith(other, InteractionDefOf.Chitchat);
                    }
                    else if (kind == "mood" && sp.needs != null && sp.needs.mood != null)
                    {
                        sp.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.AteRawFood);
                        ok = true;
                    }
                    else if (kind == "thought")
                    {
                        ok = MoteMaker.MakeThoughtBubble(sp, "Things/Mote/Disoriented") != null;
                    }
                    Log.Message("[IsoRim probe] social " + kind + " by " + sp.LabelShort + " at " + sc + ": " + (ok ? "done" : "failed"));
                    break;
                }
                case "turretcheck":
                    foreach (Building_Turret tt in map.listerThings.AllThings.OfType<Building_Turret>())
                    {
                        Verb v = tt.CurrentEffectiveVerb;
                        ModelEntry te;
                        bool hasEntry = ModelLibrary.TryGet(tt.def, out te);
                        bool loaded = tt.def.building != null && tt.TurretTopMaterial != tt.def.building.turretTopMat;
                        TurretTop top = tt is Building_TurretGun g ? Traverse.Create(g).Field("top").GetValue<TurretTop>() : null;
                        Log.Message("[IsoRim probe] turretcheck " + tt.def.defName + " " + tt.Position + " verb " + (v != null ? v.state.ToString() : "-")
                            + " override " + (v != null && v.AimAngleOverride.HasValue ? v.AimAngleOverride.Value.ToString("0.0") : "-")
                            + " rot " + (top != null ? top.CurRotation.ToString("0.0") : "-") + " loaded " + loaded
                            + " gun " + (!hasEntry || te.top == null ? "none" : loaded && te.topLoaded != null ? "topModelLoaded" : "topModel")
                            + " fuel " + (tt.TryGetComp<CompRefuelable>()?.Fuel.ToString("0.0") ?? "-") + " power " + (tt.TryGetComp<CompPowerTrader>()?.PowerOn.ToString() ?? "-"));
                    }
                    break;
                case "paint":
                {
                    Building pb = new IntVec3(I(a[0]), 0, I(a[1])).GetFirstBuilding(map);
                    if (pb != null)
                    {
                        pb.ChangePaint(a[2] == "-" ? null : DefDatabase<ColorDef>.GetNamed(a[2]));
                    }
                    break;
                }
                case "cam":
                case "drag":
                case "dragpx":
                case "zdrag":
                case "ddrag":
                case "click":
                case "labels":
                    IxKey(key, a);
                    break;
                case "window":
                case "campath":
                case "spin":
                case "tilt":
                case "walls":
                case "clear":
                case "save":
                case "loadtest":
                case "cull":
                case "cullab":
                case "cullcheck":
                case "cullshrink":
                case "batch":
                case "abswitch":
                case "speed":
                    PerfKey(key, a, map);
                    break;
                case "meshstats":
                case "ghostpreview":
                case "aim":
                case "grassab":
                    Batch5Key(key, a, map);
                    break;
                case "roofclip":
                    RoofClip.Enabled = a[0] == "1";
                    break;
                case "fogclip":
                    // fogclip=0|1: the fog clip (RoofClip.FogOn) off or on; the map is redrawn (fogged trees' shadows).
                    RoofClip.FogEnabled = a[0] == "1";
                    map.mapDrawer.RegenerateEverythingNow();
                    break;
                case "spacemap":
                {
                    // spacemap=<size>: a new empty space map (Odyssey's "Space" site on a free orbit tile), fog
                    // cleared, made the current map; later keys and the harness's shots work on it.
                    PlanetLayer orbit = Find.WorldGrid.Orbit;
                    if (orbit == null)
                    {
                        Log.Warning("[IsoRim probe] spacemap: no orbit layer");
                        break;
                    }
                    PlanetTile tile = PlanetTile.Invalid;
                    for (int i = 0; i < orbit.TilesCount; i++)
                    {
                        PlanetTile pt = new PlanetTile(i, orbit);
                        if (!Find.WorldObjects.AnyWorldObjectAt(pt))
                        {
                            tile = pt;
                            break;
                        }
                    }
                    int n = I(a[0]);
                    Map sm = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(n, 1, n), DefDatabase<WorldObjectDef>.GetNamed("Space"));
                    sm.fogGrid.ClearAllFog();
                    Current.Game.CurrentMap = sm;
                    Log.Message("[IsoRim probe] spacemap " + sm.Size + " tile " + tile + " space " + SpaceEdges.DrawsOn(sm) + " clippers " + sm.DrawMapClippers);
                    break;
                }
                case "spaceplanet":
                    Sky.PlanetBelow = a[0] == "1";
                    break;
                case "spaceedges":
                    // spaceedges=0|1: platform sides in 3D (SpaceEdges, default 1) or the game's flat strips (before/after).
                    SpaceEdges.Enabled = a[0] == "1";
                    map.mapDrawer.RegenerateEverythingNow();
                    break;
                case "spaceedgecheck":
                {
                    // spaceedgecheck=1: per space-edge terrain on the map, its loop texture sizes and colour; then the
                    // game's edge layer and IsoRim's sides layer: drawn sub-meshes and vertices.
                    HashSet<TerrainDef> seen = new HashSet<TerrainDef>();
                    foreach (IntVec3 c in map.AllCells)
                    {
                        TerrainDef t = map.terrainGrid.BaseTerrainAt(c);
                        if (t != null && t.spaceEdgeGraphicData != null && seen.Add(t))
                        {
                            List<string> parts = new List<string>();
                            foreach (SectionLayer_TerrainEdges.EdgeType et in new[] { SectionLayer_TerrainEdges.EdgeType.Loop, SectionLayer_TerrainEdges.EdgeType.LoopLeft,
                                SectionLayer_TerrainEdges.EdgeType.LoopRight, SectionLayer_TerrainEdges.EdgeType.LoopSingle, SectionLayer_TerrainEdges.EdgeType.Flat })
                            {
                                Material m = t.spaceEdgeGraphicData.GetMaterial(t, et);
                                parts.Add(et + " " + (m != null && m.mainTexture != null ? m.mainTexture.width + "x" + m.mainTexture.height + " " + m.mainTexture.name : "-"));
                            }
                            Material m0 = t.spaceEdgeGraphicData.GetMaterial(t, SectionLayer_TerrainEdges.EdgeType.Loop);
                            Log.Message("[IsoRim probe] spaceedge " + t.defName + " colour " + (m0 != null ? m0.color.ToString() : "-") + ": " + string.Join(", ", parts));
                        }
                    }
                    MethodInfo gl = AccessTools.Method(typeof(Section), "GetLayer");
                    foreach (Type lt in new[] { typeof(SectionLayer_TerrainEdges), typeof(SectionLayer_IsoRimSpaceEdges) })
                    {
                        int subs = 0, verts = 0;
                        HashSet<Section> done = new HashSet<Section>();
                        foreach (IntVec3 c in map.AllCells)
                        {
                            Section s = map.mapDrawer.SectionAt(c);
                            if (!done.Add(s))
                            {
                                continue;
                            }
                            SectionLayer l = gl != null ? gl.Invoke(s, new object[] { lt }) as SectionLayer : null;
                            if (l == null)
                            {
                                continue;
                            }
                            foreach (LayerSubMesh sub in l.subMeshes)
                            {
                                if (sub.finalized && !sub.disabled && sub.mesh != null && sub.mesh.vertexCount > 0)
                                {
                                    subs++;
                                    verts += sub.mesh.vertexCount;
                                }
                            }
                        }
                        Log.Message("[IsoRim probe] spaceedge layer " + lt.Name + ": " + subs + " drawn sub-meshes, " + verts + " vertices");
                    }
                    break;
                }
                case "tipmarks":
                    HarmonyLib.AccessTools.Field(typeof(Projectiles), "TipMarks")?.SetValue(null, a[0] == "1");   // (by reflection: builds without it too)
                    break;
                case "muzzle":
                    Projectiles.FromMuzzle = a[0] == "1";
                    break;
                case "muzzlepie":
                    AimPies.AtMuzzle = a[0] == "1";
                    break;
                case "clearflashes":
                {
                    // The shot flashes in the air removed, nothing else touched (no tick): the same frame without them.
                    int removed = 0;
                    foreach (FleckSystem fs in map.flecks.Systems)
                    {
                        System.Collections.IList live = Traverse.Create(fs).Field("dataGametime").GetValue() as System.Collections.IList;
                        for (int i = live != null ? live.Count - 1 : -1; i >= 0; i--)
                        {
                            if (live[i] is FleckStatic fl && fl.def == FleckDefOf.ShotFlash)
                            {
                                live.RemoveAt(i);
                                removed++;
                            }
                        }
                    }
                    Log.Message("[IsoRim probe] clearflashes: " + removed);
                    break;
                }
                default:
                    if (CutKey(key, a) || MechKey(key, a, map) || EntityKey(key, a, map) || FixKey(key, a, map) || SeatKey(key, a, map) || GshipKey(key, a, map) || SkyKey(key, a, map) || StrikeKey(key, a, map) || SilKey(key, a, map) || WeaponKey(key, a, map) || CrawlKey(key, a, map) || CardApiKey(key, a) || Api8Key(key, a, map) || Api9Key(key, a, map) || Api12Key(key, a, map) || Api17Key(key, a, map) || FogKey(key, a, map) || BuildSeeKey(key, a, map) || MineMarkKey(key, a, map) || HairTuckKey(key, a, map) || GroundKey(key, a, map) || BinderKey(key, a, map) || BenchKey(key, a, map) || Young24Key(key, a, map) || BarKey(key, a, map) || GhostKey(key, a, map) || AlienKey(key, a, map) || Ghost3Key(key, a, map) || WoundKey(key, a, map) || BooksKey(key, a, map) || GeoKey(key, a, map) || YayoKey(key, a, map) || OverheadKey(key, a, map))
                    {
                        break;
                    }
                    Log.Warning("[IsoRim probe] unknown key " + key);
                    return;
            }
            Log.Message("[IsoRim probe] " + key + "=" + string.Join(",", a));
        }

        private static readonly string[] BookOrder = { "TextBook", "Novel", "Schematic", "Tome", "Map" };

        private static List<ThingDef> BookKinds(string list)
        {
            List<ThingDef> kinds = new List<ThingDef>();
            string[] names = list != null ? list.Split(':') : BookOrder;
            foreach (string name in names)
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (d != null && typeof(Book).IsAssignableFrom(d.thingClass))
                {
                    kinds.Add(d);
                }
            }
            return kinds;
        }

        private static Building_Bookcase BookcaseAt(Map map, int x, int z)
        {
            Building_Bookcase b = new IntVec3(x, 0, z).GetFirstBuilding(map) as Building_Bookcase;
            if (b == null)
            {
                Log.Warning("[IsoRim probe] no bookcase at " + x + "," + z);
            }
            return b;
        }

        private static void Books(Map map, string[] a)
        {
            Building_Bookcase b = BookcaseAt(map, I(a[0]), I(a[1]));
            if (b == null)
            {
                return;
            }
            ThingOwner held = b.GetDirectlyHeldThings();
            string what = a[2];
            if (what == "-")
            {
                if (b.HeldBooks.Count > 0)
                {
                    b.HeldBooks[b.HeldBooks.Count - 1].Destroy();
                }
            }
            else if (what == "drop")
            {
                if (b.HeldBooks.Count > 0)
                {
                    Thing dropped;
                    held.TryDrop(b.HeldBooks[0], b.Position, map, ThingPlaceMode.Near, out dropped);
                }
            }
            else if (what.StartsWith("+"))
            {
                List<ThingDef> kinds = BookKinds(what.Substring(1));
                if (kinds.Count > 0)
                {
                    held.TryAdd(BookUtility.MakeBook(kinds[0], ArtGenerationContext.Colony));
                }
            }
            else
            {
                held.ClearAndDestroyContents();
                List<ThingDef> kinds = BookKinds(a.Length > 3 ? a[3] : null);
                int n = I(what);
                for (int i = 0; i < n && kinds.Count > 0; i++)
                {
                    held.TryAdd(BookUtility.MakeBook(kinds[i % kinds.Count], ArtGenerationContext.Colony));
                }
            }
            Log.Message("[IsoRim probe] bookcase " + b.Position + " holds " + b.HeldBooks.Count + ": "
                + string.Join(" ", b.HeldBooks.Select(k => k.def.defName)));
        }

        private static void HaulBook(Map map, string[] a)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(a[0]);
            Building_Bookcase b = BookcaseAt(map, I(a[3]), I(a[4]));
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed);
            if (b == null || pawn == null)
            {
                Log.Warning("[IsoRim probe] haulbook: no bookcase or no colonist");
                return;
            }
            Book book = BookUtility.MakeBook(def, ArtGenerationContext.Colony);
            GenSpawn.Spawn(book, new IntVec3(I(a[1]), 0, I(a[2])), map);
            if (pawn.drafter != null)
            {
                pawn.drafter.Drafted = false;
            }
            Verse.AI.Job job = Verse.AI.HaulAIUtility.HaulToContainerJob(pawn, book, b);
            pawn.jobs.StartJob(job, Verse.AI.JobCondition.InterruptForced);
            Log.Message("[IsoRim probe] haulbook: " + pawn.LabelShort + " at " + pawn.Position + " hauls " + book.def.defName + " from " + book.Position
                + " into the bookcase at " + b.Position + " (" + job.def.defName + ")");
        }

        private static int bookCheckAt = -1;

        private static void BookCheckDue()
        {
            if (bookCheckAt >= 0 && Time.frameCount >= bookCheckAt)
            {
                bookCheckAt = -1;
                if (Find.CurrentMap != null)
                {
                    BookCheck(Find.CurrentMap);
                }
            }
        }

        private static void BookCheck(Map map)
        {
            Log.Message("[IsoRim probe] book colours: " + Bookcases.ColourList());
            foreach (Building_Bookcase b in map.listerThings.AllThings.OfType<Building_Bookcase>())
            {
                ModelEntry entry = Forms.For(b.def).model;
                bool stands = SectionLayer_IsoRimBodies.StandsIn(b);
                int expect = stands ? Mathf.Min(b.HeldBooks.Count, Bookcases.Room(entry)) : 0;
                int printed;
                if (!Bookcases.Printed.TryGetValue(b.thingIDNumber, out printed) || !stands)
                {
                    printed = 0;
                }
                Log.Message("[IsoRim probe] bookcheck " + b.def.defName + " " + b.Position + " rot " + b.Rotation.ToStringHuman()
                    + " model " + stands + " held " + b.HeldBooks.Count + " baked " + printed + (printed == expect ? " OK" : " MISMATCH"));
            }
        }

        private static bool uiPatched;
        private static bool powerPatched;
        private static bool barsPatched;
        private static bool inBar;

        // bars=1: progress bars are drawn in the shots too (the game hides them in screenshot mode).
        public static int barDraws, barShotChecks;

        public static void BarsOn() { inBar = true; barDraws++; }

        public static void BarsOff() { inBar = false; }

        public static void BarsShot(ref bool __result)
        {
            if (inBar)
            {
                barShotChecks++;
                __result = false;
            }
        }
        private static readonly HashSet<Thing> powered = new HashSet<Thing>();
        // contain=x,z,<Def>[,<Stuff>]: a new thing put into the building's container (the building's own,
        // or its first comp that holds things: reliquary, dryad cocoon). A race def gives a new pawn of it
        // (the player's); corpse:<race> a new corpse of one.
        private static void Contain(Map map, string[] a)
        {
            // Any building (a dryad cocoon is a plain ThingWithComps, not a Building).
            Thing b = new IntVec3(I(a[0]), 0, I(a[1])).GetThingList(map).FirstOrDefault(x => x.def.category == ThingCategory.Building);
            IThingHolder holder = b as IThingHolder;
            if (b != null && !(b is Building_Casket))
            {
                ThingWithComps twc = b as ThingWithComps;
                IThingHolder comp = twc != null ? twc.AllComps.OfType<IThingHolder>().FirstOrDefault() : null;
                holder = comp ?? holder;
            }
            if (holder == null)
            {
                Log.Warning("[IsoRim probe] contain: nothing that holds things at " + a[0] + "," + a[1]);
                return;
            }
            bool corpse = a[2].StartsWith("corpse:");
            ThingDef def = DefDatabase<ThingDef>.GetNamed(corpse ? a[2].Substring(7) : a[2]);
            Thing t;
            if (def.race != null)
            {
                PawnKindDef kind = def.race.Humanlike ? PawnKindDefOf.Colonist : DefDatabase<PawnKindDef>.AllDefs.First(k => k.race == def);
                // A corpse of no faction: a gibbet cage takes strangers' corpses by default, not colonists'.
                Pawn p = PawnGenerator.GeneratePawn(kind, corpse ? null : Faction.OfPlayer);
                if (corpse)
                {
                    GenSpawn.Spawn(p, b.InteractionCell.IsValid && b.def.hasInteractionCell ? b.InteractionCell : CellFinder.StandableCellNear(b.Position, map, 5), map);
                    p.Kill(null);
                    t = p.Corpse;
                    t.DeSpawn();
                }
                else
                {
                    t = p;
                }
            }
            else
            {
                t = ThingMaker.MakeThing(def, a.Length > 3 && a[3] != "-" ? DefDatabase<ThingDef>.GetNamed(a[3]) : GenStuff.DefaultStuffFor(def));
                if (a.Length > 4)
                {
                    t.stackCount = I(a[4]);
                }
            }
            Building_Casket casket = holder as Building_Casket;
            bool ok = casket != null ? casket.TryAcceptThing(t, false) : holder.GetDirectlyHeldThings().TryAdd(t);
            Log.Message("[IsoRim probe] contain " + b.def.defName + " " + b.Position + " <- " + t.def.defName + " " + (ok ? "ok" : "refused"));
        }

        private static readonly HashSet<Thing> ritualTargets = new HashSet<Thing>();
        private static bool ritualPatched;

        // ritual=x,z on a ritual fire (pyre, effigy, flag, burnbong): its fire burns (Effects.Burning), as far into a
        // ritual as it needs; the harness scenes run no real ritual lord.
        public static bool RitualTarget(Thing t)
        {
            return ritualTargets.Count > 0 && ritualTargets.Contains(t);
        }

        // ritual=x,z: the building counts as a running ritual's target.
        public static void RitualPostfix(Thing thing, ref bool __result)
        {
            if (!__result && ritualTargets.Contains(thing))
            {
                __result = true;
            }
        }

        // power=x,z: the building counts as powered (the harness scenes have no power grid).
        public static void PoweredPostfix(CompPowerTrader __instance, ref bool __result)
        {
            if (!__result && powered.Contains(__instance.parent))
            {
                __result = true;
            }
        }

        private static int perfLeft;
        private static string perfLabel;
        private static readonly List<float> perfDt = new List<float>();
        private static readonly List<float> perfTick = new List<float>();
        private static readonly List<float> perfUpdate = new List<float>();
        private static readonly List<float> perfDynamic = new List<float>();
        private static readonly List<float> perfMesh = new List<float>();
        private static double updMs, dynMs, meshMs;
        private static long updStart, dynStart, meshStart;
        private static bool perfHooked;

        // CPU time of the game's whole frame update (Root_Play.Update), of the per-frame thing drawing
        // (DynamicDrawManager.DrawDynamicThings) and of the map mesh drawing (MapDrawer.DrawMapMesh):
        // the frame time itself is held at the screen's refresh rate by the swapchain.
        private static void PerfHooks()
        {
            if (perfHooked)
            {
                return;
            }
            perfHooked = true;
            Harmony h = new Harmony("peren.isorim17.probeperf");
            h.Patch(AccessTools.Method(typeof(Root_Play), "Update"), new HarmonyMethod(typeof(Probe), nameof(UpdBegin)), new HarmonyMethod(typeof(Probe), nameof(UpdEnd)));
            h.Patch(AccessTools.Method(typeof(DynamicDrawManager), "DrawDynamicThings"), new HarmonyMethod(typeof(Probe), nameof(DynBegin)), new HarmonyMethod(typeof(Probe), nameof(DynEnd)));
            h.Patch(AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"), new HarmonyMethod(typeof(Probe), nameof(MeshBegin)), new HarmonyMethod(typeof(Probe), nameof(MeshEnd)));
            h.Patch(AccessTools.Method(typeof(DynamicDrawManager), "DrawDynamicThings"), new HarmonyMethod(typeof(Probe), nameof(DynCmdBegin)), new HarmonyMethod(typeof(Probe), nameof(DynCmdEnd)));
            h.Patch(AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"), new HarmonyMethod(typeof(Probe), nameof(MeshCmdBegin)), new HarmonyMethod(typeof(Probe), nameof(MeshCmdEnd)));
            RenderHooks(h);
        }

        public static void UpdBegin() { updStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void UpdEnd() { updMs += (System.Diagnostics.Stopwatch.GetTimestamp() - updStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        public static void DynBegin() { dynStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void DynEnd() { dynMs += (System.Diagnostics.Stopwatch.GetTimestamp() - dynStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        public static void MeshBegin() { meshStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void MeshEnd() { meshMs += (System.Diagnostics.Stopwatch.GetTimestamp() - meshStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        // The realtime-body layer's meshes over the whole map: sections holding any, draws (sub-meshes), vertices.
        private static string BodyMeshes()
        {
            Map map = Find.CurrentMap;
            Array secs = map != null ? MapSections.SectionsOf(map) : null;
            MethodInfo gl = AccessTools.Method(AccessTools.TypeByName("Verse.Section"), "GetLayer", new[] { typeof(Type) });
            int sections = 0, subs = 0, verts = 0;
            if (secs != null && gl != null)
            {
                foreach (object s in secs)
                {
                    SectionLayer l = gl.Invoke(s, new object[] { typeof(SectionLayer_IsoRimBodies) }) as SectionLayer;
                    if (l == null)
                    {
                        continue;
                    }
                    int before = subs;
                    foreach (LayerSubMesh sm in l.subMeshes)
                    {
                        if (sm.finalized && sm.mesh != null && sm.mesh.vertexCount > 0)
                        {
                            subs++;
                            verts += sm.mesh.vertexCount;
                        }
                    }
                    if (subs > before)
                    {
                        sections++;
                    }
                }
            }
            return "body layer: " + sections + " sections, " + subs + " sub-meshes, " + verts + " vertices";
        }

        private static float Median(List<float> l)
        {
            List<float> c = new List<float>(l);
            c.Sort();
            return c.Count > 0 ? c[c.Count / 2] : 0f;
        }

        private static int bodiesDrawn;
        private static int bodiesHeld;

        // Called by Patches.ThingDrawAt_Prefix for every realtime building body (harness counter;
        // only things with a hand-made model count).
        public static void CountBody(Thing t, bool held)
        {
            ModelEntry e;
            if (perfLeft > 0 && ModelLibrary.TryGet(t.def, out e) && e.HasModel)
            {
                if (held)
                {
                    bodiesHeld++;
                }
                else
                {
                    bodiesDrawn++;
                }
            }
        }

        private static readonly double[] perfLapMax = new double[8];
        private static readonly double[] perfLapSum = new double[8];

        private static void PerfFrame(float dt, float tickMs, double[] laps)
        {
            if (perfLeft > 0)
            {
                for (int i = 0; i < laps.Length; i++)
                {
                    perfLapSum[i] += laps[i];
                    perfLapMax[i] = Math.Max(perfLapMax[i], laps[i]);
                }
            }
            if (perfLeft <= 0)
            {
                updMs = dynMs = meshMs = 0;
                RenderPerfIdle();
                return;
            }
            RenderPerfFrame(perfLeft);
            perfDt.Add(dt);
            perfTick.Add(tickMs);
            perfUpdate.Add((float)updMs);
            perfDynamic.Add((float)dynMs);
            perfMesh.Add((float)meshMs);
            updMs = dynMs = meshMs = 0;
            if (--perfLeft > 0)
            {
                return;
            }
            int n = perfDt.Count;
            List<float> d = new List<float>(perfDt);
            d.Sort();
            List<float> k = new List<float>(perfTick);
            k.Sort();
            float mean = 0f;
            for (int i = 0; i < n; i++)
            {
                mean += d[i];
            }
            mean /= n;
            float tickMean = 0f;
            for (int i = 0; i < n; i++)
            {
                tickMean += k[i];
            }
            tickMean /= n;
            List<float> u = new List<float>(perfUpdate);
            u.Sort();
            System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
            Log.Message("[IsoRim probe] perf " + perfLabel + ": " + n + " frames, frame median " + d[n / 2].ToString("0.00") + " ms, mean "
                + mean.ToString("0.00") + ", p95 " + d[Mathf.Min(n - 1, (int)(n * 0.95f))].ToString("0.00") + ", max " + d[n - 1].ToString("0.00") + ", IsoRim tick median "
                + k[n / 2].ToString("0.000") + " mean " + tickMean.ToString("0.000") + " max " + k[n - 1].ToString("0.000") + " ms; frame update p95 "
                + u[Mathf.Min(n - 1, (int)(n * 0.95f))].ToString("0.000") + " max " + u[n - 1].ToString("0.000") + " ms; memory managed "
                + (GC.GetTotalMemory(false) / 1048576.0).ToString("0") + " MB, process working set " + (proc.WorkingSet64 / 1048576.0).ToString("0") + " MB, private "
                + (proc.PrivateMemorySize64 / 1048576.0).ToString("0") + " MB; IsoRim tick parts mean [" + Breakdown(perfLapSum.Select(s => s / n).ToArray()) + "] max [" + Breakdown(perfLapMax) + "]; CPU medians: frame update " + Median(perfUpdate).ToString("0.000") + " ms, dynamic things "
                + Median(perfDynamic).ToString("0.000") + " ms, map mesh " + Median(perfMesh).ToString("0.000") + " ms; modelled realtime bodies per frame: game drew " + (bodiesDrawn / (float)n).ToString("0.0")
                + ", held back " + (bodiesHeld / (float)n).ToString("0.0") + "; " + BodyMeshes() + "; " + RenderPerfReport() + "; 3D " + View3D.Active + ", targetFrameRate " + Application.targetFrameRate);
        }

        public static void OverlaysOn()
        {
            Find.UIRoot.screenshotMode.Active = false;
            if (overlay == "home" && Find.CurrentMap != null)
            {
                Find.CurrentMap.areaManager.Home.MarkForDraw();
            }
        }

        public static void OverlaysOff()
        {
            Find.UIRoot.screenshotMode.Active = true;
        }

        // pocketmap=: the camera is held over the pocket map's exit (wherever the map put it) for the shots there.
        private static Thing pocketExit;
        private static float pocketCamSize = 4f;

        // Every frame: what has to be kept up through the shots.
        private static void Sticky()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            if (overlay == "power")
            {
                OverlayDrawHandler.DrawPowerGridOverlayThisFrame();
            }
            foreach (IntVec3 c in keepSelected)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = c.GetThingList(map);
                Thing t = null;
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Pawn || things[i].def.selectable)
                    {
                        t = things[i];
                        if (t is Pawn)
                        {
                            break;
                        }
                    }
                }
                if (t != null && !Find.Selector.IsSelected(t))
                {
                    Find.Selector.Select(t, false, false);
                }
            }
            if (pocketExit != null && pocketExit.Spawned && pocketExit.Map == map)
            {
                Find.CameraDriver.SetRootPosAndSize(pocketExit.TrueCenter(), pocketCamSize);
            }
            FilmTick(map);
            IxTick(map);
            MotionTick();
            CutTick();
            BurstTick();
            WalkFilmTick();
            GhostTick();
            GameGhostTick();
            if (swayStill)
            {
                List<Material> plantMats = (List<Material>)plantMaterialsField.GetValue(null);
                for (int i = 0; i < plantMats.Count; i++)
                {
                    plantMats[i].SetFloat(ShaderPropertyIDs.SwayHead, 0f);
                }
            }
        }

        private static bool swayStill;
        private static readonly FieldInfo plantMaterialsField = AccessTools.Field(typeof(WindManager), "plantMaterials");

        // ---- filming a mined rock frame by frame (the fog lid must never show a flat frame)

        private static bool filming;
        private static IntVec3 filmCell;
        private static int filmAfter;
        private static string filmTag;
        private static int filmFrame;
        private static int filmGone;

        private static void MineJob(Map map, IntVec3 c)
        {
            Thing rock = c.GetEdifice(map);
            if (rock == null)
            {
                Log.Warning("[IsoRim probe] minejob: nothing to mine at " + c);
                return;
            }
            map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
            IntVec3 stand = c + IntVec3.South;
            Pawn p = map.mapPawns.FreeColonistsSpawned.Where(x => !x.Downed && !x.WorkTypeIsDisabled(WorkTypeDefOf.Mining))
                .OrderBy(x => x.Position.DistanceToSquared(stand)).FirstOrDefault();
            if (p == null)
            {
                Log.Warning("[IsoRim probe] minejob: no colonist can mine");
                return;
            }
            if (p.Position != stand)
            {
                p.DeSpawn();
                GenSpawn.Spawn(p, stand, map, Rot4.North);
            }
            if (p.drafter != null)
            {
                p.drafter.Drafted = false;
            }
            bool ok = p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Mine, rock), JobTag.Misc);
            Log.Message("[IsoRim probe] minejob: " + p.LabelShort + " mining " + rock.def.defName + " at " + c + " (" + rock.HitPoints + " hp): " + ok);
        }

        private static void FilmTick(Map map)
        {
            if (!filming)
            {
                return;
            }
            Building rock = filmCell.GetEdifice(map);
            bool there = rock != null && rock.def.mineable;
            // The camera on the pocket north of the rock, 12 cells tall (the harness only moves it later).
            Find.CameraDriver.SetRootPosAndSize(new Vector3(filmCell.x + 0.5f, 0f, filmCell.z + 3.5f), 6f);
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);   // prompts the game opens while the clock runs
            }
            if (there && filmFrame == 0 && !NearBreaking(map, rock))
            {
                // Not near breaking yet: skip ahead without pictures.
                for (int i = 0; i < 60 && filmCell.GetEdifice(map) is Building b && !NearBreaking(map, b); i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                return;
            }
            Find.TickManager.DoSingleTick();
            rock = filmCell.GetEdifice(map);
            there = rock != null && rock.def.mineable;
            if (!there && filmGone < 0)
            {
                filmGone = filmFrame;
            }
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "film_" + filmTag + "_" + filmFrame.ToString("000") + ".png"));
            IntVec3 north = filmCell + IntVec3.North;
            Log.Message("[IsoRim probe] film " + filmTag + " frame " + filmFrame + " (Unity frame " + Time.frameCount + "): tick " + Find.TickManager.TicksGame
                + ", rock " + (there ? rock.HitPoints + " hp" : "gone") + ", north cell fogged " + north.Fogged(map));
            filmFrame++;
            if (filmGone >= 0 && filmFrame - filmGone > filmAfter)
            {
                filming = false;
            }
            if (filmFrame > 400)
            {
                filming = false;
                Log.Warning("[IsoRim probe] film " + filmTag + ": the rock did not break");
            }
        }

        private static readonly FieldInfo pickHitField = AccessTools.Field(typeof(JobDriver_Mine), "ticksToPickHit");

        // The rock breaks with the next pick hit, which lands within a few ticks.
        private static bool NearBreaking(Map map, Building rock)
        {
            if (rock.HitPoints > 80)
            {
                return false;
            }
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.jobs != null && p.jobs.curDriver is JobDriver_Mine d && p.CurJob.targetA.Thing == rock)
                {
                    return (int)pickHitField.GetValue(d) <= 4;
                }
            }
            return false;
        }

        // ---- a screenshot every k-th frame for N frames (e.g. right after the first switch-on)
        private static int burstLeft;
        private static int burstEvery = 1;
        private static int burstFrame;
        private static string burstTag;
        private static Vector3 burstCam;
        private static float burstStart;
        private static int burstTicks;   // burstticks=k: game ticks run before each burst frame

        private static void BurstTick()
        {
            if (burstLeft <= 0)
            {
                return;
            }
            if (burstCam.z > 0f)
            {
                Find.CameraDriver.SetRootPosAndSize(new Vector3(burstCam.x, 0f, burstCam.y), burstCam.z);
            }
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            }
            string doors = "";
            if (burstTicks > 0 && Find.CurrentMap != null)
            {
                for (int t = 0; t < burstTicks; t++)
                {
                    Find.TickManager.DoSingleTick();
                }
                foreach (Building_Door bd in Find.CurrentMap.listerThings.AllThings.OfType<Building_Door>())
                {
                    if (bd.def.size.x > 1 || bd.def.size.z > 1
                        || (burstCam.z > 0f && Mathf.Abs(bd.Position.x + 0.5f - burstCam.x) < burstCam.z && Mathf.Abs(bd.Position.z + 0.5f - burstCam.y) < burstCam.z))
                    {
                        doors += " " + bd.def.defName + bd.Position + " pct " + Traverse.Create(bd).Property("OpenPct").GetValue<float>().ToString("0.00");
                    }
                }
                foreach (Pawn bp in Find.CurrentMap.mapPawns.FreeColonistsSpawned.Concat(Find.CurrentMap.mapPawns.SpawnedColonyAnimals))
                {
                    bp.Drawer.tweener.ResetTweenedPosToRoot();   // paused game: the drawn position follows the ticks
                    doors += " | " + bp.LabelShort + " " + bp.Position + " " + (bp.CurJobDef?.defName ?? "-") + " moving " + bp.pather.Moving;
                }
                doors += " tick " + Find.TickManager.TicksGame;
            }
            if (burstFrame % burstEvery == 0)
            {
                string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", HarnessTag());
                System.IO.Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "burst_" + burstTag + "_" + burstFrame.ToString("000") + ".png"));
            }
            Log.Message("[IsoRim probe] burst " + burstTag + " frame " + burstFrame + " (Unity frame " + Time.frameCount + "): "
                + ((Time.realtimeSinceStartup - burstStart) * 1000f).ToString("0") + " ms since start, dt "
                + (Time.unscaledDeltaTime * 1000f).ToString("0.0") + " ms, 3D " + View3D.Active + doors);
            burstFrame++;
            burstLeft--;
        }

        private static string HarnessTag()
        {
            string tag;
            return GenCommandLine.TryGetCommandLineArg("cap17tag", out tag) && !tag.NullOrEmpty() ? tag : "probe";
        }
    }
}
