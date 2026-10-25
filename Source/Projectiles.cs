using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Bullets, arrows and shells in 3D. The game draws a projectile flat on the ground along its flight, so in the 3D
    // view shots seemed to leave from the shooter's feet. Here it flies from the muzzle (the height the shooter holds
    // its weapon, or a turret's gun) and its height blends along the flight to the height of what it was aimed at
    // (a standing pawn's chest, a lying pawn, a building's middle, the ground). A lobbed shell's arc, which the game
    // draws as a shift up the screen, becomes real height. The shadow stays on the ground. The game launches every shot
    // from the shooter's middle; it is drawn leaving from the muzzle in front instead (Muzzle, the same point as the
    // flash below) and back on the game's path by the target. Only the drawing moves: the flight and the hit are the
    // game's. A shell from a manned turret leaves from the turret's gun, not the pawn's hands (MuzzleOwner). Every kind
    // leaves from its weapon as drawn: a shot tail first from the barrel's end (a rocket out of its tube, an arrow off the
    // bow: Rear), a thrown thing from the hand that holds it (Thrown), and a shooter not drawn just now (a hidden floor)
    // from where its weapon would be (People.MeasureWeapon). With the 3D view off the game draws as usual.
    //
    // The shot's muzzle flash too (ShotFlash): the game puts it flat on the ground in the middle of the shooter's
    // cell, which in 3D is inside the shooter's body. Here it is put at the muzzle the bullet leaves from (Muzzle: a
    // gun's length toward what it shoots at, MuzzleReach, at MuzzleHeight) and drawn standing up (Emitters.Raise). Every
    // shot flash of the game (1.7 source, Core and DLC defs):
    //   - Verb.TryCastNextBurstShot: FleckMaker.Static(caster.Position, ..., ShotFlash, verbProps.muzzleFlashScale)
    //     for every verb with a muzzleFlashScale, the only use of either in code. Covers guns held by people and
    //     mechs (Core, Biotech, Odyssey, Anomaly weapons), turret guns (security and mech turrets, mech cluster mini
    //     turrets, Odyssey's ancient armored turret, Anomaly's tactical turret; manned mortars too: the caster is the
    //     turret), a mech's turret comp (CompTurretGun, the centurion's charge blaster: the caster is the mech),
    //     apparel verbs (Royalty's grenadier and phoenix armour) and abilities (Anomaly's heatspikes, fleshmelter
    //     bolt). Replaced here (Patches.ShotFlash_Transpiler). No vanilla animal verb has a muzzle flash; a modded one
    //     comes out of the animal's front, as a mech's.
    //   - Effecters with ShotFlash as their fleck (JumpFlightEffect, JumpMechFlightEffect: a jump pack's take-off;
    //     SpikecoreSparks: a ritual): not shots, left as the game's.
    public static class Projectiles
    {
        private sealed class Flight
        {
            public float start;   // cells above the ground at the muzzle
            public float end;     // cells above the ground at the target
            public int launcherRow;   // map rows of the shooter and the target (their floors, Floors)
            public int targetRow;
            public Vector3 lead;      // from the game's launch point (the shooter's middle) to the muzzle, flat
            public Vector3 rear;      // from the muzzle on to the drawn sprite's middle, flat: its tail at the muzzle (Rear)
            public int arc = -2;      // (Api 20) the arc rules' answer, asked once a flight (ArcRules): -2 not asked, -1 none, 0 straight, 1 arc
        }

        private static readonly ConditionalWeakTable<Projectile, Flight> flights = new ConditionalWeakTable<Projectile, Flight>();

        private static readonly ConditionalWeakTable<Projectile, StrongBox<Vector3>> launchPoints = new ConditionalWeakTable<Projectile, StrongBox<Vector3>>();

        // For a gameplay addon that moves a shot's launch point elsewhere on the map (stacked floors: onto the target
        // floor's rows): where the shot really left from (the game's launch point before the move). The shooter, its
        // muzzle and the bullet's lead to it are found from there. Call before the projectile is first drawn.
        public static void SetLaunchPoint(Projectile p, Vector3 from)
        {
            if (p != null)
            {
                launchPoints.AddOrUpdate(p, new StrongBox<Vector3>(from));
            }
        }

        // Where the drawn flight starts: the game's launch point plus the lead to the muzzle (x and z, the game's
        // rows), y the height above the ground in cells (the shooter's floor included).
        public static Vector3 DrawnStart(Projectile p)
        {
            Flight f = flights.GetValue(p, Begin);
            Vector3 o = originRef(p) + f.lead;
            o.y = f.start + FloorHeight(f.launcherRow);
            return o;
        }

        private static readonly AccessTools.FieldRef<Projectile, Vector3> originRef = AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destinationRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
        private static readonly Func<Projectile, float> arcHeight = AccessTools.MethodDelegate<Func<Projectile, float>>(AccessTools.PropertyGetter(typeof(Projectile), "ArcHeightFactor"));
        private static readonly Func<Projectile, float> arcFraction = AccessTools.MethodDelegate<Func<Projectile, float>>(AccessTools.PropertyGetter(typeof(Projectile), "DistanceCoveredFractionArc"));
        private static readonly Action<ThingWithComps> postDraw = AccessTools.MethodDelegate<Action<ThingWithComps>>(AccessTools.Method(typeof(ThingWithComps), "Comps_PostDraw"));
        private static readonly AccessTools.FieldRef<Material> shadowMat = AccessTools.StaticFieldRefAccess<Material>(AccessTools.Field(typeof(Projectile), "shadowMaterial"));

        // Projectile.DrawAt in 3D: the game's drawing redone with the height added. False = drawn here.
        public static bool DrawAt(Projectile p, Vector3 drawLoc)
        {
            if (!View3D.Ready || p == null || p.def?.projectile == null)
            {
                return true;
            }
            // Stacked floors (Floors): the projectile is drawn over the ground cell of its row, its height counted from the
            // ground (a shot between floors blends from the shooter's floor to the target's); hidden with its floor.
            Vector3 row = Vector3.zero;
            if (Floors.On)
            {
                Matrix4x4 unused;
                if (!Floors.RowMatrix(p.Position.z, out unused))
                {
                    return false;
                }
                row = Floors.RowOffset(p.Position.z);
            }
            if (Lobbed(p))
            {
                return DrawLobbed(p, drawLoc, row);
            }
            float arc = arcHeight(p) * GenMath.InverseParabola(arcFraction(p));
            float h = Height(p, drawLoc) + arc;
            // Drawn leaving from the muzzle, not the shooter's middle, and back on the game's path by the target (only
            // the drawing moves: the flight, the hit and ExactPosition are the game's).
            Flight fl = flights.GetValue(p, Begin);
            drawLoc += (fl.lead + fl.rear) * (1f - Flown(p, drawLoc));
            bool wasFloor = DrawHook.floor;
            DrawHook.floor = false;   // the floor is in the position already (the graphic's draw goes through DrawHook)
            try
            {
                if (p.def.projectile.shadowSize > 0f)
                {
                    DrawShadow(p, drawLoc + new Vector3(0f, row.y / View3D.Squash, row.z), arc);
                }
                Vector3 at = drawLoc + new Vector3(0f, h / View3D.Squash, row.z);
                Quaternion rotation = p.ExactRotation;
                if (p.def.projectile.spinRate != 0f)
                {
                    float period = 60f / p.def.projectile.spinRate;
                    rotation = Quaternion.AngleAxis(Find.TickManager.TicksGame % period / period * 360f, Vector3.up);
                }
                if (p.def.projectile.useGraphicClass)
                {
                    p.Graphic.Draw(at, p.Rotation, p, rotation.eulerAngles.y);
                }
                else
                {
                    // An addon may draw the card with its own matrix (Api.ProjectileCard).
                    Matrix4x4 card;
                    ProjectileCardHook hook = Api.ProjectileCard;
                    if (hook != null && hook(p, at, rotation, out card))
                    {
                        Graphics.DrawMesh(MeshPool.GridPlane(p.def.graphicData.drawSize), card, p.DrawMat, 0);
                    }
                    else
                    {
                        Graphics.DrawMesh(MeshPool.GridPlane(p.def.graphicData.drawSize), at, rotation, p.DrawMat, 0);
                    }
                }
                postDraw(p);
            }
            finally
            {
                DrawHook.floor = wasFloor;
            }
            return false;
        }

        // Indirect fire, drawn on the shell arc (DrawLobbed): every projectile that flies overhead (flyOverhead: the game
        // lets it pass over walls; mortar shells, VFE Medieval 2's warbow arrows) and is drawn as one card. Only one with
        // an arc of its own (arcHeightFactor) and its own graphic class (an animated sprite: the fleshmass spitter's acid)
        // keeps the plain flight with its own arc, as DrawLobbed draws a single card. Direct shots (bows, guns) never.
        // (Api 20) An addon's arc rule (Api.AddProjectileArc) decides first, asked once a flight: true draws it on the arc,
        // false straight; no answer leaves it to the rule above. Never on the arc without graphicData.
        public static bool Lobbed(Projectile p)
        {
            ProjectileProperties pp = p.def.projectile;
            if (!SkyStrikes.Enabled || p.def.graphicData == null)
            {
                return false;
            }
            if (ArcRules.Any)
            {
                Flight f = flights.GetValue(p, Begin);
                if (f.arc == -2)
                {
                    bool? said = ArcRules.Ask(p);
                    f.arc = said == null ? -1 : said.Value ? 1 : 0;
                }
                if (f.arc >= 0)
                {
                    return f.arc == 1;
                }
            }
            return pp.flyOverhead && (pp.arcHeightFactor < 0.5f || !pp.useGraphicClass);
        }

        // A projectile class of another mod that draws itself (overrides DrawAt without the game's own): IsoRim draws it
        // when it is indirect fire (Lobbed), else leaves it to the mod (Patches: every such override is hooked).
        public static bool OverrideDrawAt(Projectile p, Vector3 drawLoc)
        {
            if (!View3D.Ready || p == null || p.def?.projectile == null)
            {
                return true;
            }
            bool lobbed = Lobbed(p);
            if (lobbed)
            {
                DrawAt(p, drawLoc);   // IsoRim draws it on the arc; the mod's own DrawAt still runs for its effects, its own draws dropped
            }
            BeginOwnDraw(p, drawLoc, lobbed);
            return true;
        }

        // ---- A projectile class's own DrawAt (another mod's), run inside a scope (Patches: prefix opens it, postfix closes):
        // the flecks it spawns there (trails, glows: VGE's gauss slug) are moved from where the game would draw the projectile
        // (flat, on its rows) to where it is drawn in 3D (its drawn flight: lead from the muzzle, height, the ground rows of
        // its floor) and stood up at that height (Emitters.Raise). A lobbed one is drawn by IsoRim on the arc first; then its
        // own draws in its DrawAt are dropped (DrawHook.skip) and the base Projectile.DrawAt it calls is skipped, so only its
        // effects remain. Main thread (projectiles are drawn there, flecks created there).
        [ThreadStatic] private static Projectile ownDraw;
        [ThreadStatic] private static Vector3 ownShift;
        [ThreadStatic] private static float ownHeight;
        [ThreadStatic] private static bool ownLobbed;
        [ThreadStatic] private static bool ownSkipWas;
        public static int OwnFlecks;   // probes: flecks moved onto a drawn projectile

        private static void BeginOwnDraw(Projectile p, Vector3 drawLoc, bool lobbed)
        {
            float flown;
            Path path = PathOf(p, out flown);
            if (path == null)
            {
                return;
            }
            flown = Flown(p, drawLoc);
            Vector3 at = path.Point(flown);
            ownDraw = p;
            ownShift = new Vector3(at.x - drawLoc.x, 0f, at.z - drawLoc.z);
            ownHeight = at.y;
            ownLobbed = lobbed;
            ownSkipWas = DrawHook.skip;
            if (lobbed)
            {
                DrawHook.skip = true;
            }
        }

        public static void EndOwnDraw(Projectile p)
        {
            if (ownDraw != null && ownDraw == p)
            {
                if (ownLobbed)
                {
                    DrawHook.skip = ownSkipWas;
                }
                ownDraw = null;
            }
        }

        // The base Projectile.DrawAt called from inside a lobbed projectile's own DrawAt: already drawn on the arc.
        public static bool OwnDrawSkipsBase(Projectile p)
        {
            return ownDraw != null && ownDraw == p && ownLobbed;
        }

        // FleckManager.CreateFleck: a fleck spawned inside a projectile's own DrawAt follows the drawn projectile.
        public static void OwnFleck(ref FleckCreationData data)
        {
            if (ownDraw == null || data.def == null)
            {
                return;
            }
            data.spawnPosition += ownShift;
            Emitters.Raise(data.spawnPosition, ownHeight, 0f, data.def);
            OwnFlecks++;
        }

        // Mortar and artillery shells and other indirect fire (flyOverhead; shells have no arc of their own: vanilla flies them straight, which in 3D skimmed
        // the ground through walls): a drawn ballistic arc from the muzzle up into the sky and down onto the target, its
        // top SkyStrikes.ShellArc of the flat flight length (capped). It lands on the top of the target's column (a shell
        // from the sky hits the highest floor built there, as ProjectZ lands it). The sprite is a card facing the camera,
        // its nose along the arc as seen on screen; the shadow lies on what is under the shell. Only the drawing changes:
        // the flight, its timing and the hit are the game's.
        private static bool DrawLobbed(Projectile p, Vector3 drawLoc, Vector3 row)
        {
            Flight f = flights.GetValue(p, Begin);
            Vector3 o = originRef(p);
            Vector3 d = destinationRef(p);
            float length = new Vector2(d.x - o.x, d.z - o.z).magnitude;
            float t = Flown(p, drawLoc);
            float apex = Mathf.Min(SkyStrikes.ShellArc * length, SkyStrikes.ShellArcMax);
            float start = f.start + FloorHeight(f.launcherRow);
            float end = SkyStrikes.LandingHeight(p.Map, d.ToIntVec3(), f.end + FloorHeight(f.targetRow));
            float up = 4f * t * (1f - t);
            float h = Mathf.Lerp(start, end, t) + apex * up;
            Vector3 flat = length > 0.001f ? new Vector3(d.x - o.x, 0f, d.z - o.z) : Vector3.zero;
            Vector3 along = flat + Vector3.up * (end - start + apex * 4f * (1f - 2f * t));
            drawLoc += f.lead * (1f - t);
            bool wasFloor = DrawHook.floor;
            DrawHook.floor = false;
            try
            {
                float sq = View3D.Squash;
                if (p.def.projectile.shadowSize > 0f)
                {
                    SkyStrikes.Impact under = SkyStrikes.ImpactAt(p.Map, p.Position);
                    DrawShadow(p, drawLoc + new Vector3(0f, under.height / sq, row.z), up);
                }
                Vector3 at = drawLoc + new Vector3(0f, h / sq, row.z);
                Vector2 size = p.def.graphicData.drawSize;
                Matrix4x4 m = SkyStrikes.Card(at, SkyStrikes.ScreenSpin(along)) * Matrix4x4.Scale(new Vector3(size.x, 1f, size.y));
                Graphics.DrawMesh(MeshPool.plane10, m, p.DrawMat, 0);
                postDraw(p);
            }
            finally
            {
                DrawHook.floor = wasFloor;
            }
            return false;
        }

        // ---- The drawn flight as numbers, for drawing on past the game's end of it (ShieldHits: a shot stopped by a shield
        // flies on to the dome). The same curves as DrawAt and DrawLobbed, in true space: x and z on the ground rows (an
        // upper floor's rows moved down onto the ground), y cells above the ground; t the share of the way flown (flat).
        public sealed class Path
        {
            public Vector3 origin, destination, lead;
            public float start, end, apex, arc, rowZ, perTick;
            public bool lobbed;

            public Vector3 Point(float t)
            {
                Vector3 flat = Vector3.Lerp(origin, destination, t) + lead * (1f - t);
                float h = Mathf.Lerp(start, end, t) + (lobbed ? apex * 4f * t * (1f - t) : arc * GenMath.InverseParabola(t));
                return new Vector3(flat.x, h, flat.z + rowZ);
            }

            // Which way the shell points at t (a lobbed shell's nose along the arc).
            public Vector3 Along(float t)
            {
                Vector3 flat = new Vector3(destination.x - origin.x, 0f, destination.z - origin.z);
                return flat + Vector3.up * (end - start + apex * 4f * (1f - 2f * t));
            }
        }

        // The path the projectile is drawn along now (null if IsoRim does not draw it), and how far along it is.
        public static Path PathOf(Projectile p, out float flown)
        {
            flown = 1f;
            if (p == null || p.def?.projectile == null || p.def.graphicData == null || p.Map == null)
            {
                return null;
            }
            Flight f = flights.GetValue(p, Begin);
            Vector3 o = originRef(p);
            Vector3 d = destinationRef(p);
            Path path = new Path { origin = o, destination = d, lead = f.lead + f.rear };
            path.start = f.start + FloorHeight(f.launcherRow);
            path.lobbed = Lobbed(p);
            if (path.lobbed)
            {
                float length = new Vector2(d.x - o.x, d.z - o.z).magnitude;
                path.apex = Mathf.Min(SkyStrikes.ShellArc * length, SkyStrikes.ShellArcMax);
                path.end = SkyStrikes.LandingHeight(p.Map, d.ToIntVec3(), f.end + FloorHeight(f.targetRow));
            }
            else
            {
                path.end = f.end + FloorHeight(f.targetRow);
                path.arc = arcHeight(p);
            }
            path.rowZ = Floors.On ? Floors.RowOffset(p.Position.z).z : 0f;
            float whole = (o - d).magnitude;   // as the game's StartingTicksToImpact
            path.perTick = whole > 0.001f ? p.def.projectile.SpeedTilesPerTick / whole : 1f;
            flown = Flown(p, p.ExactPosition);
            return path;
        }

        // Height of the floor a row is on (cells), zero on the ground and without floors.
        private static float FloorHeight(int z)
        {
            return Floors.On ? Floors.RowHeight(z) : 0f;
        }

        // Height above the ground (cells) at this point of the flight: muzzle to target, by the share of the way flown.
        public static float Height(Projectile p, Vector3 at)
        {
            Flight f = flights.GetValue(p, Begin);
            return Mathf.Lerp(f.start + FloorHeight(f.launcherRow), f.end + FloorHeight(f.targetRow), Flown(p, at));
        }

        // The share of the way (flat) from launch to destination at this point of the game's path, 0 to 1.
        private static float Flown(Projectile p, Vector3 at)
        {
            Vector3 o = originRef(p);
            Vector3 d = destinationRef(p);
            float total = new Vector2(d.x - o.x, d.z - o.z).magnitude;
            float done = new Vector2(at.x - o.x, at.z - o.z).magnitude;
            return total > 0.001f ? Mathf.Clamp01(done / total) : 1f;
        }

        private static Flight Begin(Projectile p)
        {
            Flight f = new Flight();
            try
            {
                // (where the shot really left from: the game's launch point, unless an addon moved it, SetLaunchPoint)
                Vector3 o = launchPoints.TryGetValue(p, out StrongBox<Vector3> from) ? from.Value : originRef(p);
                Thing owner = FromMuzzle ? MuzzleOwner(p.Launcher, p.EquipmentDef, o, p.Map) : p.Launcher;
                f.start = MuzzleHeight(owner);
                // From the muzzle (as the flash, ShotFlash) when the shot really left from the shooter (not a shell
                // dropped from elsewhere with a launcher named).
                if (FromMuzzle && owner != null && owner.Spawned && owner.Map == p.Map && LeftFrom(owner, o))
                {
                    // (aimed along the game's flight: its launch point is on the destination's rows even when the
                    // shot really left from a floor hidden now; a turret's muzzle is found from the turret itself, whatever
                    // point on it the game or a mod launched from)
                    Vector3 muzzle = Muzzle(owner, p.EquipmentDef, owner is Building_Turret ? owner.DrawPos : o, FlatDirection(originRef(p), destinationRef(p)));
                    f.start = muzzle.y;
                    f.lead = new Vector3(muzzle.x - o.x, 0f, muzzle.z - o.z);
                    f.rear = FlatDirection(originRef(p), destinationRef(p)) * Rear(p);
                }
                f.end = AimHeight(p);
                f.launcherRow = owner != null && owner.Spawned ? owner.Position.z : p.Position.z;
                f.targetRow = Mathf.FloorToInt(destinationRef(p).z);   // where it comes down (a gameplay addon may move it)
                if (LaunchLog)
                {
                    Vector3 tip;
                    int age;
                    bool had = LastTip(owner, out tip, out age);
                    Log.Message("[IsoRim probe] launch " + p.def.defName + " by " + (owner != null ? owner.LabelShort : "-") + " with " + (p.EquipmentDef != null ? p.EquipmentDef.defName : "-")
                        + " tick " + Find.TickManager.TicksGame + " frame " + Time.frameCount + ": start " + (o + f.lead).ToString("F3") + " y " + f.start.ToString("0.000")
                        + ", last drawn tip " + (had ? tip.ToString("F3") + " age " + age : "none") + ", game origin " + o.ToString("F2"));
                }
            }
            catch (Exception e)
            {
                f.start = f.end = 0.5f;
                Log.ErrorOnce("[IsoRim] Working out a projectile's height failed: " + e, 93611501);
            }
            return f;
        }

        // Whether a shot launched from o left from the shooter: from its middle (within a cell and a half), or for a turret
        // from anywhere on it or just off it (mods launch a turret's shot from their own barrel point: Vanilla Gravship
        // Expanded's ship guns a cell and a half in front of the middle).
        private static bool LeftFrom(Thing owner, Vector3 o)
        {
            if (owner is Building_Turret)
            {
                return owner.OccupiedRect().ExpandedBy(2).Contains(o.ToIntVec3());
            }
            return new Vector2(owner.DrawPos.x - o.x, owner.DrawPos.z - o.z).magnitude < 1.5f;
        }

        // How high the shooter fires from, in cells above its feet.
        public static float MuzzleHeight(Thing launcher)
        {
            if (launcher == null)
            {
                return 0.5f;
            }
            if (launcher is Pawn pawn)
            {
                if (pawn.GetPosture() != PawnPosture.Standing)
                {
                    return 0.2f;
                }
                // About where a 3D figure or a standing sprite holds its weapon (People, Patches.HandHeight); attached to a
                // host (Api 13), as high over its place on the host.
                float onHost;
                Poses.AttachRise(pawn, out onHost);
                return (Interaction.PickHeight(pawn) - onHost) * (pawn.RaceProps != null && pawn.RaceProps.Humanlike ? 0.42f : 0.6f) + onHost;
            }
            if (launcher is Building_Turret turret)
            {
                Vector2 tip;
                if (FromMuzzle && GunTip(turret, out tip))
                {
                    return tip.y;
                }
                ModelEntry entry;
                if (ModelLibrary.TryGet(turret.def, out entry) && entry.HasModel && entry.def.topHeight > 0f)
                {
                    return entry.def.topHeight * entry.Fit(turret.def).y;
                }
                return Mathf.Clamp(Interaction.PickHeight(turret) * 0.75f, 0.3f, 1.5f);
            }
            return Mathf.Clamp(Interaction.PickHeight(launcher) * 0.5f, 0.1f, 1.5f);
        }

        // Harness only (probe muzzle=0): the flash and the bullet as before (flat flash, bullet from the middle).
        public static bool FromMuzzle = true;

        // Harness only (probe launchlog=1): each projectile logs its drawn start when it is first drawn.
        public static bool LaunchLog;

        // Shot flashes so far, and who fired the last one (probe tickuntil=flash).
        public static int Flashes;
        public static Thing LastFlasher;

        // In place of the game's FleckMaker.Static(cell, map, ShotFlash, scale) in Verb.TryCastNextBurstShot.
        public static void ShotFlash(IntVec3 cell, Map map, FleckDef fleck, float scale, Verb verb)
        {
            Flashes++;
            Thing caster = verb != null ? verb.caster : null;
            LastFlasher = caster;
            if (FromMuzzle && View3D.Ready && caster != null && caster.Spawned && caster.Map == map)
            {
                try
                {
                    // The same point the bullet is drawn leaving from (Muzzle; the game launches it from caster.DrawPos).
                    Thing owner = MuzzleOwner(caster, verb.EquipmentSource != null ? verb.EquipmentSource.def : null, caster.DrawPos, map);
                    Vector3 at = Muzzle(owner, verb.EquipmentSource != null ? verb.EquipmentSource.def : null, caster.DrawPos, Aim(verb));
                    FleckCreationData data = FleckMaker.GetDataStatic(new Vector3(at.x, 0f, at.z), map, fleck, scale);
                    Emitters.Raise(data.spawnPosition, at.y, 0f, fleck);
                    flashing = true;
                    try
                    {
                        map.flecks.CreateFleck(data);
                    }
                    finally
                    {
                        flashing = false;
                    }
                    return;
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Putting a muzzle flash at the muzzle failed: " + e, 93611503);
                }
            }
            FleckMaker.Static(cell, map, fleck, scale);
        }

        [ThreadStatic] private static bool flashing;

        // A shot flash a mod throws by hand flat in the middle of a 3D-gunned turret's cell (FleckMaker.Static(turret.Position,
        // ..., ShotFlash, ...): Vanilla Gravship Expanded's point defence) goes to the gun's barrel end too, standing up, as
        // the game's own turret flashes (ShotFlash). FleckManager.CreateFleck prefix (Patches.TurretFlash_Prefix).
        public static void TurretFlash(Map map, ref FleckCreationData data)
        {
            if (flashing || !FromMuzzle || !View3D.Ready || map == null || data.def != FleckDefOf.ShotFlash)
            {
                return;
            }
            IntVec3 c = data.spawnPosition.ToIntVec3();
            if (!c.InBounds(map) || Mathf.Abs(data.spawnPosition.x - (c.x + 0.5f)) > 0.01f || Mathf.Abs(data.spawnPosition.z - (c.z + 0.5f)) > 0.01f)
            {
                return;
            }
            Building_Turret turret = c.GetEdifice(map) as Building_Turret;
            Vector3 at;
            if (turret == null || turret.Position != c || !TurretMuzzle(turret, Vector3.zero, out at))
            {
                return;
            }
            data.spawnPosition = new Vector3(at.x, data.spawnPosition.y, at.z);
            Emitters.Raise(data.spawnPosition, at.y, 0f, data.def);
            Flashes++;
            LastFlasher = turret;
        }

        // Which way the shot leaves the gun (flat, unit length; zero if it cannot tell): as the gun is drawn, toward
        // the target as seen (between floors too) or along a spraying gun's sweep (AimAngleOverride).
        private static Vector3 Aim(Verb verb)
        {
            float? over = verb.AimAngleOverride;
            if (over.HasValue)
            {
                return Quaternion.AngleAxis(over.Value - 90f, Vector3.up) * Vector3.right;
            }
            LocalTargetInfo target = verb.CurrentTarget;
            if (!target.IsValid)
            {
                return Vector3.zero;
            }
            return FlatDirection(verb.caster.DrawPos, target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted());
        }

        // From one point toward another as drawn (between floors too), flat, unit length; zero if they are the same.
        private static Vector3 FlatDirection(Vector3 from, Vector3 to)
        {
            Vector3 d = Floors.FlatDelta(from, to);
            return d.x * d.x + d.z * d.z > 0.001f ? d.normalized : Vector3.zero;
        }

        // The muzzle, the one point both a shot's flash and its bullet's drawn flight start from: from (where the game
        // launches the shot, the shooter's middle) moved MuzzleReach along dir (flat), at MuzzleHeight (in y, cells
        // above the ground). gun is the weapon's def (null if unknown). A person fires from the end of its barrel as the
        // weapon is drawn (CardTip: on a 2D card or a 3D figure); a thrown weapon (Thrown: a grenade, a pilum) leaves from
        // where the thing in the hand is drawn, its middle. A 3D figure not drawn just now (its floor hidden, off screen) is
        // posed for it without drawing (People.MeasureWeapon), so its shots start where they would be seen to.
        public static Vector3 Muzzle(Thing owner, ThingDef gun, Vector3 from, Vector3 dir)
        {
            bool thrown = Thrown(gun);
            Vector3 tip;
            if (CardTip(owner, gun, thrown, out tip))
            {
                return tip;
            }
            // A 3D mech fires from its gun part's barrel end: as last drawn, else posed for it (hidden floor, off screen).
            if (FromMuzzle && owner is Pawn mech && View3D.Ready && (MechTipOf(owner, gun, out tip) || Mechs.MeasureGun(mech, gun, dir, out tip)))
            {
                return tip;
            }
            if (FromMuzzle && owner is Pawn person && View3D.Ready && People.MeasureWeapon(person) && CardTip(owner, gun, thrown, out tip))
            {
                return tip;
            }
            // A turret with a 3D gun fires from the gun's barrel end, posed as it is drawn now (TurretMuzzle).
            if (FromMuzzle && owner is Building_Turret turret && View3D.Ready && TurretMuzzle(turret, dir, out tip))
            {
                return tip;
            }
            Vector3 at = from + dir * MuzzleReach(owner, gun);
            return new Vector3(at.x, MuzzleHeight(owner), at.z);
        }

        // A weapon that is itself thrown (grenades, molotovs, pila): the game's own mark for it, its verb's log of a throw
        // (rangedFireRulepack Combat_RangedFire_Thrown, which modded thrown weapons use too).
        public static bool Thrown(ThingDef gun)
        {
            if (gun == null || !gun.IsWeapon || gun.Verbs == null)
            {
                return false;
            }
            for (int i = 0; i < gun.Verbs.Count; i++)
            {
                RulePackDef log = gun.Verbs[i].rangedFireRulepack;
                if (log != null && log.defName == "Combat_RangedFire_Thrown")
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly Dictionary<ThingDef, float> rears = new Dictionary<ThingDef, float>();

        // How far the drawn projectile reaches back from its middle (cells): its tail, the last opaque pixel of its sprite
        // behind the middle along the way it flies (up the sprite, as the game turns it). A shot leaves the barrel tail
        // first (a rocket out of its tube, an arrow off the bow), so its middle starts this far in front of the muzzle. Not
        // for a thrown thing (it starts in the hand), a spinning one or a lobbed shell (no nose along the flight): 0.
        private static float Rear(Projectile p)
        {
            ProjectileProperties pp = p.def.projectile;
            if (pp.spinRate != 0f || pp.flyOverhead || Thrown(p.EquipmentDef) || p.def.graphicData == null)
            {
                return 0f;
            }
            float rear;
            if (rears.TryGetValue(p.def, out rear))
            {
                return rear;
            }
            rear = 0f;
            try
            {
                Material mat = p.def.graphicData.Graphic != null ? p.def.graphicData.Graphic.MatSingle : null;
                if (mat != null && mat.mainTexture != null)
                {
                    const int n = 64;
                    Color32[] px = SpritePixels.Sample(mat.mainTexture, Vector2.one, Vector2.zero, n);
                    if (px == null && !SpritePixels.IsSheet(mat.mainTexture))
                    {
                        px = SpritePixels.GpuSample(mat.mainTexture, n);
                    }
                    if (px != null && px.Length >= n * n)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            bool any = false;
                            for (int i = 0; i < n && !any; i++)
                            {
                                any = px[j * n + i].a > 128;
                            }
                            if (any)
                            {
                                rear = Mathf.Max(0f, (0.5f - (float)j / n) * p.def.graphicData.drawSize.y);   // row 0 is the bottom (the tail)
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Measuring a projectile's sprite failed: " + e, 93611506);
            }
            rears[p.def] = rear;
            return rear;
        }

        // ---- The barrel tip of a 2D pawn's weapon on its card. The game draws the aimed weapon itself
        // (PawnRenderUtility.DrawEquipmentAiming: its offsets, flip and recoil, and whatever other mods change there);
        // IsoRim only turns the aim and stands the drawing up with the card (Patches.DrawEquipmentAiming_Prefix, DrawHook).
        // While that runs (BeginTip, EndTip) the weapon's own mesh is caught on its way to Unity (TipDrawn) and the end
        // of the barrel, found in the sprite's pixels (BarrelEnd), is carried through the same matrices. A shot fired
        // soon after starts there (bullet, flash, tracer). A 3D figure records its drawn barrel the same way (FigureTip); mechs and
        // turrets keep their own muzzles.

        private sealed class Tip
        {
            public Vector3 at;        // x, z in the game's rows; y cells above the ground
            public Vector3 centre;    // the middle of the drawn weapon, as at (where a thrown thing leaves the hand)
            public ThingDef gun;
            public int frame;
            public float yaw, pitch;  // the camera it was drawn for (a turned camera draws the card, and its tip, elsewhere)
        }

        private static readonly ConditionalWeakTable<Thing, Tip> tips = new ConditionalWeakTable<Thing, Tip>();

        // Mechs (Mechs.Draw): each gun part's barrel end as last drawn, by gun (a mech can carry a weapon and a turret
        // comp). A point in 3D, the same from every camera.
        private static readonly ConditionalWeakTable<Thing, Dictionary<ThingDef, Tip>> mechTips = new ConditionalWeakTable<Thing, Dictionary<ThingDef, Tip>>();

        public static void MechTip(Thing mech, ThingDef gun, Vector3 at)
        {
            if (mech == null || gun == null)
            {
                return;
            }
            Dictionary<ThingDef, Tip> byGun = mechTips.GetValue(mech, k => new Dictionary<ThingDef, Tip>());
            Tip t;
            if (!byGun.TryGetValue(gun, out t))
            {
                byGun[gun] = t = new Tip();
            }
            t.at = at;
            t.centre = at;
            t.gun = gun;
            t.frame = Time.frameCount;
        }

        private static bool MechTipOf(Thing owner, ThingDef gun, out Vector3 at)
        {
            at = Vector3.zero;
            Dictionary<ThingDef, Tip> byGun;
            Tip t = null;
            if (owner == null || !mechTips.TryGetValue(owner, out byGun))
            {
                return false;
            }
            if (gun != null)
            {
                byGun.TryGetValue(gun, out t);
            }
            else
            {
                foreach (Tip x in byGun.Values)
                {
                    if (t == null || x.frame > t.frame)
                    {
                        t = x;
                    }
                }
            }
            if (t == null || Time.frameCount - t.frame > TipFrames)
            {
                return false;
            }
            at = t.at;
            return true;
        }
        private static readonly Dictionary<int, Vector2> barrelEnds = new Dictionary<int, Vector2>();

        private const int TipFrames = 30;     // how long a drawn tip is good for (a pawn on screen is drawn every frame)
        private const float TipLow = 0.05f;   // cells: a drawn tip lower than this is lifted along the line of sight

        private static Thing tipPawn;
        private static Thing tipWeapon;
        private static float tipGround;      // the drawn y of the pawn's ground (DrawHook space)

        // Patches.DrawEquipmentAiming_Prefix, for an upright 2D pawn that is aiming: the next mesh drawn is its weapon.
        public static void BeginTip(Thing pawn, Thing weapon, float ground)
        {
            tipPawn = pawn;
            tipWeapon = weapon;
            tipGround = ground;
        }

        public static void EndTip()
        {
            tipPawn = null;
            tipWeapon = null;
        }

        public static bool CatchingTip
        {
            get { return tipPawn != null; }
        }

        // DrawHook: the weapon's mesh with the game's matrix (before the card's xform).
        public static void TipDrawn(Mesh mesh, Matrix4x4 matrix)
        {
            Thing pawn = tipPawn;
            Thing weapon = tipWeapon;
            tipPawn = null;   // one mesh: the weapon's
            tipWeapon = null;
            if (pawn == null || weapon == null)
            {
                return;
            }
            try
            {
                Vector2 uv;
                if (!BarrelEnd(weapon, out uv))
                {
                    return;
                }
                bool flipped = mesh == MeshPool.plane10Flip;
                // The plane's corners are at -0.5..0.5 (x, z), u = x + 0.5 (the flipped plane: 0.5 - x), v = z + 0.5.
                Vector3 local = new Vector3(flipped ? 0.5f - uv.x : uv.x - 0.5f, 0f, uv.y - 0.5f);
                Vector3 at = OnCard(matrix, local);
                Tip t = tips.GetValue(pawn, k => new Tip());
                t.at = at;
                t.centre = OnCard(matrix, Vector3.zero);
                if (TipMarks)
                {
                    TipMarksDraw(pawn, weapon, at);
                }
                t.gun = weapon.def;
                t.frame = Time.frameCount;
                t.yaw = View3D.Yaw;
                t.pitch = View3D.Pitch;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Finding the end of a drawn weapon's barrel failed: " + e, 93611504);
            }
        }

        // A point of the weapon's plane as drawn on its card (x, z in the game's rows, y cells above the ground). A point
        // drawn below the feet (a barrel pointing down the card) is slid along the camera's line of sight (it stays on the
        // same pixel) up to just above the ground.
        private static Vector3 OnCard(Matrix4x4 matrix, Vector3 local)
        {
            Vector3 drawn = DrawHook.xform.MultiplyPoint3x4(matrix.MultiplyPoint3x4(local));
            Vector3 at = new Vector3(drawn.x, (drawn.y - tipGround) * View3D.Squash, drawn.z);
            if (at.y < TipLow)
            {
                float pitch = View3D.Pitch * Mathf.Deg2Rad;
                float t0 = (TipLow - at.y) / Mathf.Max(0.05f, Mathf.Sin(pitch));
                Vector3 back = View3D.GroundForward * (t0 * Mathf.Cos(pitch));
                at = new Vector3(at.x - back.x, TipLow, at.z - back.z);
            }
            return at;
        }

        // FigureWeapons: a 3D figure's aimed weapon drawn with this matrix (SpriteSolids' slab, true cells: x, z in the game's
        // rows, y over the floor). Its barrel's end is where the figure's shots start (Muzzle, as a card's: CardTip).
        public static void FigureTip(Thing pawn, Thing weapon, Matrix4x4 slab)
        {
            try
            {
                Vector2 uv;
                if (!BarrelEnd(weapon, out uv))
                {
                    return;
                }
                Vector3 at = slab.MultiplyPoint3x4(new Vector3(uv.x - 0.5f, uv.y - 0.5f, 0f));
                Tip t = tips.GetValue(pawn, k => new Tip());
                t.at = at;
                t.centre = slab.MultiplyPoint3x4(Vector3.zero);
                t.gun = weapon.def;
                t.frame = Time.frameCount;
                t.yaw = View3D.Yaw;
                t.pitch = View3D.Pitch;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Finding the end of a 3D figure's barrel failed: " + e, 93611505);
            }
        }

        // Harness only (probe tipmarks=1): a magenta dot where a 2D pawn's shots start (the drawn tip) and a cyan one where
        // they started before (MuzzleReach along the aim at MuzzleHeight), for measuring both against the drawn barrel.
        public static bool TipMarks;
        private static Material markTip, markOld;

        private static void TipMarksDraw(Thing pawn, Thing weapon, Vector3 at)
        {
            if (markTip == null)
            {
                markTip = new Material(SolidColorMaterials.SimpleSolidColorMaterial(new Color(1f, 0f, 1f)));
                markTip.renderQueue = 4000;
                markOld = new Material(SolidColorMaterials.SimpleSolidColorMaterial(new Color(0f, 1f, 1f)));
                markOld.renderQueue = 4000;
            }
            Quaternion face = Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f);
            Vector3 w = new Vector3(at.x, tipGround + at.y / View3D.Squash, at.z);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(w, face, new Vector3(0.05f, 1f, 0.05f)), markTip, 0);
            Pawn p = pawn as Pawn;
            Stance_Busy busy = p != null && p.stances != null ? p.stances.curStance as Stance_Busy : null;
            if (busy != null && busy.focusTarg.IsValid)
            {
                Vector3 d = Floors.FlatDelta(p.DrawPos, busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted());
                d = d.sqrMagnitude > 0.001f ? d.normalized : Vector3.zero;
                Vector3 old = p.DrawPos + d * MuzzleReach(p, weapon.def);
                Vector3 wo = new Vector3(old.x, tipGround + MuzzleHeight(p) / View3D.Squash, old.z);
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(wo, face, new Vector3(0.05f, 1f, 0.05f)), markOld, 0);
            }
        }

        // The drawn barrel tip of a 2D pawn's aimed weapon, if it was drawn just now with that gun.
        public static bool CardTip(Thing owner, ThingDef gun, out Vector3 at)
        {
            return CardTip(owner, gun, false, out at);
        }

        // The same, or with centre the middle of the drawn weapon (a thrown thing in the hand).
        public static bool CardTip(Thing owner, ThingDef gun, bool centre, out Vector3 at)
        {
            at = Vector3.zero;
            Tip t;
            if (!(owner is Pawn) || !tips.TryGetValue(owner, out t) || Time.frameCount - t.frame > TipFrames || (gun != null && t.gun != gun)
                || Mathf.Abs(Mathf.DeltaAngle(t.yaw, View3D.Yaw)) > 2f || Mathf.Abs(t.pitch - View3D.Pitch) > 1f)
            {
                return false;
            }
            at = centre ? t.centre : t.at;
            return true;
        }

        // Harness (probe aimcheck): a pawn's last drawn tip and how many frames ago it was drawn.
        public static bool LastTip(Thing owner, out Vector3 at, out int age)
        {
            at = Vector3.zero;
            age = -1;
            Tip t;
            if (owner == null || !tips.TryGetValue(owner, out t))
            {
                return false;
            }
            at = t.at;
            age = Time.frameCount - t.frame;
            return true;
        }

        // The end of a weapon sprite's barrel in texture coordinates (u, v): of its opaque pixels, the middle of those
        // furthest along the way the game points the sprite (+u turned back by equippedAngleOffset, the angle the game
        // adds for art drawn at a slant), measured at the drawn size so a long sprite is not squeezed.
        private static bool BarrelEnd(Thing weapon, out Vector2 uv)
        {
            uv = new Vector2(1f, 0.5f);
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            if (mat == null || mat.mainTexture == null)
            {
                return false;
            }
            int key = mat.GetInstanceID();
            if (barrelEnds.TryGetValue(key, out uv))
            {
                return uv.x >= 0f;
            }
            const int n = 128;
            Color32[] px = SpritePixels.Sample(mat.mainTexture, Vector2.one, Vector2.zero, n);
            if (px == null && !SpritePixels.IsSheet(mat.mainTexture))
            {
                px = SpritePixels.GpuSample(mat.mainTexture, n);
            }
            uv = new Vector2(-1f, -1f);
            if (px != null && px.Length >= n * n)
            {
                Vector3 d3 = Quaternion.AngleAxis(-weapon.def.equippedAngleOffset, Vector3.up) * Vector3.right;
                Vector2 size = g.drawSize;
                Vector2 dir = new Vector2(d3.x * size.x, d3.z * size.y);   // along the art, texture units scaled to cells
                float best = float.MinValue;
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (px[j * n + i].a > 128)
                        {
                            best = Mathf.Max(best, (i + 0.5f) / n * dir.x + (j + 0.5f) / n * dir.y);
                        }
                    }
                }
                if (best > float.MinValue)
                {
                    // The pixels within a pixel and a half of the furthest: the mouth of the barrel.
                    float near = best - 1.5f / n * dir.magnitude;
                    Vector2 sum = Vector2.zero;
                    int count = 0;
                    for (int j = 0; j < n; j++)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (px[j * n + i].a > 128 && (i + 0.5f) / n * dir.x + (j + 0.5f) / n * dir.y >= near)
                            {
                                sum += new Vector2((i + 0.5f) / n, (j + 0.5f) / n);
                                count++;
                            }
                        }
                    }
                    uv = sum / count;
                }
            }
            barrelEnds[key] = uv;
            return uv.x >= 0f;
        }

        // Who the shot leaves from: the turret, when its gun was fired by the pawn manning it (a mortar: the game
        // names the pawn as the launcher but launches from the turret); otherwise the launcher itself.
        public static Thing MuzzleOwner(Thing launcher, ThingDef gun, Vector3 from, Map map)
        {
            if (launcher is Pawn && gun != null && map != null)
            {
                IntVec3 c = from.ToIntVec3();
                Building_Turret turret = c.InBounds(map) ? c.GetEdifice(map) as Building_Turret : null;
                // (Verb_LaunchProjectile passes the manned turret itself as the equipment; its gun def too, to be safe.)
                if (turret != null && (turret.def == gun || (turret.def.building != null && turret.def.building.turretGunDef == gun)))
                {
                    return turret;
                }
            }
            return launcher;
        }

        // ---- Turrets with a 3D gun (Patches.DrawTurret_Prefix): the gun's barrel end, a point in 3D (x, z rows, y cells above
        // the turret's ground), the same for every turret, vanilla or modded, whatever pedestal raises its pivot (topHeight),
        // its size (Fit, scale), its kick (a mortar's recoil) and aim. Worked out from the turret's state when asked, with
        // the very pose DrawTurret_Prefix draws (TurretGunEnd): a turret turns between frames and several ticks can pass in
        // one frame, so a barrel end remembered from the last drawing would lag behind the gun that fires.

        // Where a turret's 3D gun (gun: its model as drawn, loaded or not) ends when drawn turned to aim (degrees, the game's
        // turret angle) at pivot (x, z: the turret's DrawPos plus its recoil kick), as DynamicModels.Draw places it.
        public static Vector3 TurretGunEnd(Building_Turret turret, ModelEntry entry, Model gun, Vector3 pivot, float aim)
        {
            Vector3 fit = entry.Fit(turret.def) * entry.def.scale;
            Vector3 end = Vector3.Scale(GunEnd(gun), fit);
            Vector3 v = Quaternion.AngleAxis(aim - 90f, Vector3.up) * end;
            return new Vector3(pivot.x + v.x, entry.def.topHeight * fit.y + v.y, pivot.z + v.z);
        }

        // A turret's muzzle: its 3D gun posed as Patches.DrawTurret_Prefix draws it now (the model, loaded or not; its turn, or
        // a spraying gun's sweep; a mortar's kick, as Building_TurretGun.DrawAt), else turned toward dir. False without a gun model.
        internal static bool TurretMuzzle(Building_Turret turret, Vector3 dir, out Vector3 at)
        {
            at = Vector3.zero;
            ModelEntry entry;
            if (!IsoRimMod.Settings.models3D || !ModelLibrary.TryGet(turret.def, out entry) || entry.top == null)
            {
                return false;
            }
            Model gun = entry.top;
            if (entry.topLoaded != null && turret.def.building != null && turret.TurretTopMaterial != turret.def.building.turretTopMat)
            {
                gun = entry.topLoaded;
            }
            Verb verb = turret.CurrentEffectiveVerb;
            float? aim = verb != null ? verb.AimAngleOverride : null;
            Building_TurretGun g = turret as Building_TurretGun;
            if (!aim.HasValue && g != null && g.Top != null)
            {
                aim = g.Top.CurRotation;
            }
            if (!aim.HasValue)
            {
                aim = dir.sqrMagnitude > 0.0001f ? dir.AngleFlat() : 0f;
            }
            Vector3 pivot = turret.DrawPos;
            if (g != null && g.Top != null && turret.def.building != null && turret.def.building.IsMortar && g.AttackVerb is Verb_LaunchProjectile shoot)
            {
                Vector3 kick;
                float turn;
                EquipmentUtility.Recoil(turret.def.building.turretGunDef, shoot, out kick, out turn, g.Top.CurRotation);
                pivot += kick;
            }
            at = TurretGunEnd(turret, entry, gun, pivot, aim.Value);
            return true;
        }

        private static readonly Dictionary<Model, Vector3> gunEnds = new Dictionary<Model, Vector3>();

        // A turret gun model's barrel end (its own space): the middle of its front-most face. Turret guns are built pointing
        // along +x from their pivot, so the barrel ends where the model reaches furthest along x (as Mechs.Tip along +z).
        public static Vector3 GunEnd(Model gun)
        {
            Vector3 end;
            if (gun == null || gun.positions == null || gun.positions.Length == 0)
            {
                return Vector3.zero;
            }
            if (gunEnds.TryGetValue(gun, out end))
            {
                return end;
            }
            float front = float.MinValue;
            for (int i = 0; i < gun.positions.Length; i++)
            {
                front = Mathf.Max(front, gun.positions[i].x);
            }
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < gun.positions.Length; i++)
            {
                if (gun.positions[i].x >= front - 0.02f)
                {
                    sum += gun.positions[i];
                    n++;
                }
            }
            end = sum / Mathf.Max(1, n);
            gunEnds[gun] = end;
            return end;
        }

        // The end of a turret's 3D gun as Patches.DrawTurret_Prefix draws it: x how far in front of the pivot (along the
        // way it aims), y how high above the ground (a mortar's barrel points up: its mouth is well above the pivot).
        // The gun model points along +x; the mouth is the middle of its front-most face (GunEnd). False without one.
        private static bool GunTip(Building_Turret turret, out Vector2 tip)
        {
            tip = Vector2.zero;
            ModelEntry entry;
            if (!IsoRimMod.Settings.models3D || !ModelLibrary.TryGet(turret.def, out entry) || entry.top == null)
            {
                return false;
            }
            Vector3 end = GunEnd(entry.top);
            Vector3 fit = entry.Fit(turret.def) * entry.def.scale;
            tip = new Vector2(end.x * fit.x, (entry.def.topHeight + end.y) * fit.y);
            return true;
        }

        // How far in front of the shooter's middle its muzzle is (flat, cells).
        public static float MuzzleReach(Thing owner, ThingDef gun)
        {
            if (owner is Building_Turret turret)
            {
                // The tip of the 3D gun, or of the game's flat one.
                Vector2 tip;
                if (GunTip(turret, out tip))
                {
                    return tip.x;
                }
                return turret.def.building != null ? turret.def.building.turretTopDrawSize * 0.4f : 0.5f;
            }
            // Where a person holds the gun (before its drawn barrel is known, FigureTip); a mech or an animal fires from about its front.
            float reach = 0.3f;
            Pawn pawn = owner as Pawn;
            if (pawn != null && pawn.RaceProps != null && !pawn.RaceProps.Humanlike)
            {
                GraphicData body = pawn.ageTracker != null && pawn.ageTracker.CurKindLifeStage != null ? pawn.ageTracker.CurKindLifeStage.bodyGraphicData : null;
                reach = Mathf.Max(reach, (body != null ? body.drawSize.x : 1f) * 0.3f);
            }
            // Plus half the gun in its hands (SpriteSolids draws it at 0.8 of its drawn size, centred on the hands);
            // not a mech's turret comp or an apparel or ability verb.
            ThingWithComps held = pawn != null && pawn.equipment != null ? pawn.equipment.Primary : null;
            if (held != null && gun != null && held.def == gun && gun.graphicData != null && !Thrown(gun))   // a thrown thing: in the hand
            {
                reach += gun.graphicData.drawSize.x * 0.4f;
            }
            return reach;
        }

        // How high what the shot is aimed at is hit: the thing hit, or for a miss the thing it was meant for.
        public static float AimHeight(Projectile p)
        {
            Thing t = p.usedTarget.Thing;
            if (t == null && p.intendedTarget.HasThing && p.intendedTarget.Cell.InHorDistOf(p.usedTarget.Cell, 4f))
            {
                t = p.intendedTarget.Thing;
            }
            if (t == null)
            {
                return 0.1f;
            }
            if (t is Pawn pawn)
            {
                return pawn.Dead || pawn.GetPosture() != PawnPosture.Standing ? 0.12f : Interaction.PickHeight(pawn) * 0.45f;
            }
            return Mathf.Clamp(Interaction.PickHeight(t) * 0.5f, 0.05f, 1f);
        }

        // The game's ground shadow (shells), at the shadow's place on the ground: the arc is height now, not a shift.
        private static void DrawShadow(Projectile p, Vector3 drawLoc, float arc)
        {
            Material mat = shadowMat();
            if (mat == null)
            {
                return;
            }
            float size = p.def.projectile.shadowSize * Mathf.Lerp(1f, 0.6f, arc);
            Matrix4x4 m = Matrix4x4.TRS(drawLoc + new Vector3(0f, -0.01f, 0f), Quaternion.identity, new Vector3(size, 1f, size));
            Graphics.DrawMesh(MeshPool.plane10, m, mat, 0);
        }
    }
}
