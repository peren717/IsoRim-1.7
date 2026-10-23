using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Skyfallers in 3D: everything of the game's Skyfaller class and its subclasses (incoming and leaving drop pods and
    // transport pods, shuttles and passenger shuttles, crashing shuttles, meteorites, lava rocks, ship chunks, crashed ship
    // parts, mech cluster pods, spacedrones, obelisks and noctoliths, sky lanterns, flying animals arriving and leaving).
    //
    // Vanilla draws a skyfaller as its sprite slid away from the landing cell (SkyfallerDrawPosUtility: a distance along
    // its angle, then the def's x/z position curves). Seen from straight above, a slide up the screen reads as height. In
    // 3D that slide lay on the ground, so a drop pod slid in sideways over the map (and on a map with floors it slid up
    // into the upper floors' rows, which drew it on the wrong floor). Here, one central conversion (Pose) turns vanilla's
    // slide into a real path every frame: up the screen (north) becomes height, east-west and south stay a drift along
    // the ground, and that drift also climbs (Glide), so a shuttle that vanilla flies in level comes down out of the sky
    // instead of skimming the ground. Vanilla's timing, speed curves, angle and turn are all kept: the pose is read from
    // vanilla's own DrawPos and GetDrawPositionAndRotation.
    //
    // The body is drawn the way the thing looks once it has landed, so nothing changes at touchdown:
    //   - Streak: the def has its own falling picture turned toward its path (rotateGraphicTowardsDirection: meteorites,
    //     lava rocks, the warped obelisk): stood up facing the camera, its trail pointing back along the 3D path.
    //   - Model: what lands has a 3D model (shuttles, passenger shuttles, crashed shuttles, ship chunks, transport pods,
    //     crashed ship parts): that model, at its height, turned by vanilla's turn.
    //   - Lying: anything else that lands (drop pods, spacedrones, ancient pods): its sprite made solid as items are on
    //     the ground (SpriteSolids), level, at its height.
    //   - Card: nothing lands (sky lanterns): the sprite stood up facing the camera.
    //   - A flying animal (Skyfaller_FlyingPawn) is drawn by the game's pawn drawing at the path's ground point and
    //     lifted to its height (PawnLift, read by Carried.LiftOf and the upright sprite cards).
    // The game's shadow (def.skyfaller.shadow, growing and fading as vanilla's) lies on the ground under the body.
    //
    // Floors (ProjectZ through the IsoRim floor API): the path is worked out over the ground cell of the landing cell's
    // row, the floor's height added, so the whole fall stays over the floor it lands on (Projectiles does the same).
    [StaticConstructorOnStartup]
    public static class Skyfallers
    {
        // Harness only (probe sky3d=0): the game's drawing (before/after shots).
        public static bool Enabled = true;

        // Height per cell of vanilla's slide up the screen: the isometric pitch's 1/cos, so in the default isometric
        // view the fall moves up the screen exactly as vanilla's slide (independent of the current pitch, so the
        // path does not jump while the camera tilts).
        public const float HeightPerSlide = 1.2247f;

        // How much a drift along the ground climbs (cells up per cell along): about 20 degrees.
        public const float Glide = 0.36f;

        public enum Form
        {
            Streak,
            Model,
            Lying,
            Card,
            Pawn
        }

        // Where a skyfaller is this frame (true space): the ground point under it (ground-band coordinates), the
        // height of its bottom above that ground (floor height included), vanilla's extra turn, and which way the
        // path goes back up toward where it came from (unit, true space).
        public struct Pose
        {
            public Vector3 ground;
            public float height;
            public float turn;
            public Vector3 back;
            public float floorHeight;
            public float rowShift;   // rows from the landing floor's cells down to the ground band (0 on the ground)
        }

        private delegate void PosRotFn(Skyfaller s, ref Vector3 drawLoc, out float extraRotation);

        private static readonly PosRotFn posRot = MakePosRot();
        private static readonly Func<Skyfaller, Material> shadowMat = MakeShadowMat();
        private static MaterialPropertyBlock shadowBlock;

        private static PosRotFn MakePosRot()
        {
            try
            {
                return AccessTools.MethodDelegate<PosRotFn>(AccessTools.Method(typeof(Skyfaller), "GetDrawPositionAndRotation"));
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Skyfaller.GetDrawPositionAndRotation not found, skyfallers stay flat: " + e.Message);
                return null;
            }
        }

        private static Func<Skyfaller, Material> MakeShadowMat()
        {
            try
            {
                return AccessTools.MethodDelegate<Func<Skyfaller, Material>>(AccessTools.PropertyGetter(typeof(Skyfaller), "ShadowMaterial"));
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Skyfaller.ShadowMaterial not found, skyfallers in 3D have no shadow: " + e.Message);
                return null;
            }
        }

        // ---- the one conversion

        // Vanilla's slide (dx, dz: cells on the map from the landing point, as vanilla draws it) as a real drift along
        // the ground and a height.
        public static void Convert(float dx, float dz, out Vector3 drift, out float height)
        {
            float south = Mathf.Min(dz, 0f);
            drift = new Vector3(dx, 0f, south);
            height = Mathf.Max(dz, 0f) * HeightPerSlide + Glide * Mathf.Sqrt(dx * dx + south * south);
        }

        // The pose this frame. drawLoc: what the game passes to DrawAt (its DrawPos, the slide along the angle in it).
        // Runs vanilla's GetDrawPositionAndRotation once (as vanilla's DrawAt does: it also moves the angle along its
        // curve). False when the landing row is not drawn now (a hidden floor).
        public static bool PoseOf(Skyfaller s, Vector3 drawLoc, out Pose pose)
        {
            pose = new Pose();
            float extra;
            posRot(s, ref drawLoc, out extra);
            pose.turn = extra;

            Vector3 land = s.TrueCenter();
            Vector3 row = Vector3.zero;
            if (Floors.On)
            {
                Matrix4x4 unused;
                if (!Floors.RowMatrix(s.Position.z, out unused))
                {
                    return false;
                }
                row = Floors.RowOffset(s.Position.z);
            }
            pose.floorHeight = row.y;
            pose.rowShift = row.z;
            Vector3 drift;
            float h;
            Convert(drawLoc.x - land.x, drawLoc.z - land.z, out drift, out h);
            pose.ground = new Vector3(land.x + drift.x, 0f, land.z + row.z + drift.z);
            pose.height = row.y + h;

            // Back along the path: the direction of vanilla's angle (as SkyfallerDrawPosUtility.PosAtDist), converted.
            Vector3 dir = Vector3Utility.FromAngleFlat(s.angle - 90f);
            if (s.def.skyfaller.flightFlippedHorizontally)
            {
                dir.x = -dir.x;
            }
            Vector3 d;
            float dh;
            Convert(dir.x, dir.z, out d, out dh);
            Vector3 back = new Vector3(d.x, dh, d.z);
            pose.back = back.sqrMagnitude > 1e-6f ? back.normalized : Vector3.up;
            return true;
        }

        // ---- what kind of body

        // The thing the skyfaller becomes (or carries away): the shuttle a transporter carries, else what it holds.
        public static Thing Lands(Skyfaller s)
        {
            if (s.innerContainer == null || s.innerContainer.Count == 0)
            {
                return null;
            }
            Thing inner = s.innerContainer[0];
            ActiveTransporter at = inner as ActiveTransporter;
            if (at != null && at.Contents != null)
            {
                Thing shuttle = at.Contents.GetShuttle();
                if (shuttle != null)
                {
                    return shuttle;
                }
            }
            return inner;
        }

        // Unspawned stand-ins for a launched pod's building (TransportPod), one per def, for its model and colour.
        private static readonly Dictionary<ThingDef, Thing> standIns = new Dictionary<ThingDef, Thing>();

        public static Form FormOf(Skyfaller s, out Thing lands, out ModelEntry model)
        {
            model = null;
            lands = Lands(s);
            if (s is Skyfaller_FlyingPawn)
            {
                return Form.Pawn;
            }
            if (s.def.graphicData != null && s.def.skyfaller.rotateGraphicTowardsDirection)
            {
                return Form.Streak;
            }
            // A launched transport pod leaves as the building it was (its model), not as the landed pod's sprite.
            ActiveTransporter at = lands as ActiveTransporter;
            if (s.def.skyfaller.reversed && at != null && at.Contents != null && at.Contents.sentTransporterDef != null)
            {
                ThingDef sent = at.Contents.sentTransporterDef;
                ThingForm sf = Forms.For(sent);
                if (sf.form == Form3D.Model && sf.model != null && sf.model.HasModel)
                {
                    Thing standIn;
                    if (!standIns.TryGetValue(sent, out standIn))
                    {
                        standIn = ThingMaker.MakeThing(sent, sent.MadeFromStuff ? GenStuff.DefaultStuffFor(sent) : null);
                        standIns[sent] = standIn;
                    }
                    standIn.Rotation = at.Rotation;
                    lands = standIn;
                    model = sf.model;
                    return Form.Model;
                }
            }
            if (lands != null)
            {
                ThingForm f = Forms.For(lands.def);
                if (f.form == Form3D.Model && f.model != null && f.model.HasModel)
                {
                    model = f.model;
                    return Form.Model;
                }
                return Form.Lying;
            }
            return Form.Card;
        }

        // ---- drawing (Skyfaller.DrawAt and Skyfaller_FlyingPawn.DrawAt, prefix)

        public static bool DrawAt(Skyfaller s, Vector3 drawLoc)
        {
            if (!Enabled || !View3D.Ready || s == null || s.def.skyfaller == null || posRot == null
                || WorldComponent_GravshipController.GravshipRenderInProgess)
            {
                return true;
            }
            Pose pose;
            if (!PoseOf(s, drawLoc, out pose))
            {
                return false;   // over a floor that is hidden now
            }
            bool wasFloor = DrawHook.floor;
            DrawHook.floor = false;   // the floor is in the pose already
            try
            {
                Thing lands;
                ModelEntry model;
                Form form = FormOf(s, out lands, out model);
                bool drawn;
                switch (form)
                {
                    case Form.Pawn:
                        drawn = DrawPawn(s, pose);
                        break;
                    case Form.Model:
                        drawn = DrawModel(lands, model, pose);
                        break;
                    case Form.Lying:
                        drawn = DrawLying(s, lands, pose);
                        break;
                    default:
                        drawn = DrawCard(s, pose, form == Form.Streak);
                        break;
                }
                if (!drawn)
                {
                    DrawCard(s, pose, false);
                }
                DrawShadow(s, pose);
            }
            finally
            {
                DrawHook.floor = wasFloor;
            }
            return false;
        }

        private static bool DrawModel(Thing lands, ModelEntry entry, Pose pose)
        {
            Model m = entry.Pick(lands);
            if (m == null)
            {
                return false;
            }
            float scale, yaw;
            ModelBaker.StartPlace(lands, entry, out scale, out yaw);
            if (entry.Swapped(lands.def))
            {
                yaw += 90f;
            }
            yaw += pose.turn;
            Vector3 fit = entry.Fit(lands.def) * scale;
            Matrix4x4 local = Matrix4x4.Translate(entry.def.offset) * Matrix4x4.Scale(fit);
            DynamicModels.DrawLocal(ObjLoader.SheetTinted(m, R3DTextures.SheetTint(lands)), new Vector3(pose.ground.x, pose.height, pose.ground.z), yaw, local,
                R3DTextures.ModelTint(lands), R3DTextures.ModelSwatch(lands));
            return true;
        }

        private static bool DrawLying(Skyfaller s, Thing lands, Pose pose)
        {
            // As thick as the landed thing lies on the ground (Forms: items made solid from their sprite).
            float thick = 0.12f;
            if (lands != null)
            {
                ThingForm f = Forms.For(lands.def);
                if (f.form == Form3D.Extruded && f.height > 0.004f)
                {
                    thick = f.height;
                }
            }
            return SpriteSolids.DrawLying(s, new Vector3(pose.ground.x, pose.height, pose.ground.z), Mathf.Clamp(thick, 0.04f, 0.4f), 1f, pose.turn);
        }

        // The sprite stood up facing the camera, its middle at the pose. A streak's top points back up its path as
        // seen on screen (vanilla turns the picture toward where it came from); a plain card keeps vanilla's turn.
        private static bool DrawCard(Skyfaller s, Pose pose, bool streak)
        {
            Graphic g = s.Graphic;
            Material mat = g != null ? g.MatSingleFor(s) : null;
            if (mat == null)
            {
                return false;
            }
            float spin = pose.turn;
            if (streak)
            {
                float right = Vector3.Dot(pose.back, View3D.Right);
                float up = Vector3.Dot(pose.back, View3D.Up);
                spin = new Vector2(right, up).sqrMagnitude > 1e-6f ? Mathf.Atan2(right, up) * Mathf.Rad2Deg : 0f;
            }
            Vector2 size = g.drawSize;
            float sq = View3D.Squash;
            float mid = streak ? 0f : size.y * 0.5f;   // a card stands on its bottom edge; a streak's head is its middle
            Matrix4x4 m = Matrix4x4.Translate(new Vector3(pose.ground.x, (pose.height + mid) / sq, pose.ground.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / sq, 1f))
                * Matrix4x4.Rotate(Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(spin, Vector3.up))
                * Matrix4x4.Scale(new Vector3(size.x, 1f, size.y));
            m = Floors.DrawnMatrix(m);
            if (!ShipCapture.Note(MeshPool.plane10, m, mat, null))
            {
                Graphics.DrawMesh(MeshPool.plane10, m, mat, 0);
            }
            return true;
        }

        // A flying animal: the game's own pawn drawing at the ground point, lifted (PawnLift).
        private static bool DrawPawn(Skyfaller s, Pose pose)
        {
            Pawn pawn = s.innerContainer.Count > 0 ? s.innerContainer[0] as Pawn : null;
            if (pawn == null)
            {
                return true;
            }
            lifts[pawn.thingIDNumber] = new Lift { height = pose.height, frame = Time.frameCount };
            if (lifts.Count > 200)
            {
                lifts.Clear();
                lifts[pawn.thingIDNumber] = new Lift { height = pose.height, frame = Time.frameCount };
            }
            pawn.DrawNowAt(new Vector3(pose.ground.x, AltitudeLayer.Pawn.AltitudeFor(), pose.ground.z));
            return true;
        }

        private struct Lift
        {
            public float height;
            public int frame;
        }

        private static readonly Dictionary<int, Lift> lifts = new Dictionary<int, Lift>();

        // How high a pawn held by a skyfaller (a flying animal arriving or leaving) is drawn this frame; false if none.
        public static bool PawnLift(Pawn pawn, out float height)
        {
            height = 0f;
            Lift l;
            if (pawn == null || !(pawn.ParentHolder is Skyfaller) || !lifts.TryGetValue(pawn.thingIDNumber, out l) || l.frame != Time.frameCount)
            {
                return false;
            }
            height = l.height;
            return true;
        }

        // Vanilla's shadow (Skyfaller.DrawDropSpotShadow: as big as 1 + ticks/100 times its size, fading in from 200 to
        // 150 ticks out), lying on the ground (the landing floor) under the body instead of at the landing cell.
        private static void DrawShadow(Skyfaller s, Pose pose)
        {
            Material mat = shadowMat != null ? shadowMat(s) : null;
            if (mat == null)
            {
                return;
            }
            if (shadowBlock == null)
            {
                shadowBlock = new MaterialPropertyBlock();
            }
            int ticks = Mathf.Max(s.ticksToImpact, 0);
            float grow = 1f + ticks / 100f;
            Vector2 size = s.def.skyfaller.shadowSize;
            Color c = Color.white;
            if (ticks > 150)
            {
                c.a = Mathf.InverseLerp(200f, 150f, ticks);
            }
            if (c.a <= 0f)
            {
                return;
            }
            shadowBlock.SetColor(ShaderPropertyIDs.Color, c);
            float sq = View3D.Squash;
            // On the landing floor where it has a floor under the shadow; where the drift takes it out over open air, on
            // the ground below.
            float floorAt = pose.floorHeight;
            if (floorAt > 0f && !Floors.HasFloor(s.Map, new IntVec3(Mathf.FloorToInt(pose.ground.x), 0, Mathf.FloorToInt(pose.ground.z - pose.rowShift))))
            {
                floorAt = 0f;
            }
            Vector3 at = new Vector3(pose.ground.x, AltitudeLayer.Shadows.AltitudeFor() + floorAt / sq, pose.ground.z);
            Matrix4x4 m = Matrix4x4.TRS(at, s.Rotation.AsQuat, new Vector3(grow * size.x, 1f, grow * size.y));
            Graphics.DrawMesh(MeshPool.plane10Back, m, mat, 0, null, 0, shadowBlock);
        }

        // ---- harness

        // One line per skyfaller on the map: def, class, ticks, vanilla's slide, the pose, the form and what it lands as.
        public static void Report(Map map)
        {
            foreach (Thing t in map.listerThings.AllThings)
            {
                Skyfaller s = t as Skyfaller;
                if (s == null)
                {
                    continue;
                }
                Vector3 vanilla = s.DrawPos;
                Vector3 land = s.TrueCenter();
                Thing lands;
                ModelEntry model;
                Form form = FormOf(s, out lands, out model);
                Vector3 drift;
                float h;
                Convert(vanilla.x - land.x, vanilla.z - land.z, out drift, out h);
                Log.Message("[IsoRim probe] skyfaller " + s.def.defName + " (" + s.GetType().Name + ") at " + s.Position + " ticks " + s.ticksToImpact
                    + " angle " + s.angle.ToString("0.0") + " slide (" + (vanilla.x - land.x).ToString("0.00") + "," + (vanilla.z - land.z).ToString("0.00") + ")"
                    + " -> drift (" + drift.x.ToString("0.00") + "," + drift.z.ToString("0.00") + ") height " + h.ToString("0.00")
                    + " form " + form + " lands " + (lands != null ? lands.def.defName : "-") + (model != null ? " model " + model.def.defName : ""));
            }
        }
    }
}
