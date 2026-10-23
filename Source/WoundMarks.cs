using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Wounds on 3D person figures and animal models, as the game draws them on its sprites.
    //
    // The game's own wound drawer (PawnWoundDrawer) still decides everything: which hediffs show a wound, which picture
    // (fresh wound, bandage once tended, the missing eye and its scar, scarification, quills), its colour (skin tint, rotting),
    // and where it goes on the sprite of each facing (the body type's wound anchors, the head's; on animals round the
    // middle of the body, at most five). IsoRim asks it for all four facings and prints each facing's wounds onto the side
    // of the model that faces that way: south on the front, north on the back, east on the right side, west on the left.
    // Each wound is a small mesh made from the model's faces on that side, clipped to the wound's quad as the game places
    // it on the sprite, its picture projected straight on, a little out from the surface (as ApparelDecals). So the marks
    // turn, lie down and walk with the model. Wounds the game draws over apparel go on the outermost clothes too; the
    // others (scarification) only on bare skin. Head wounds go on the head (a hat covers them, as the game draws hats over).
    //
    // Worked out again only when the game itself would redraw its wounds (PawnOverlayDrawer.ClearCache: a hediff added,
    // removed, tended or changed; graphics made dirty; a corpse starts to rot), or when the figure's look or the animal's
    // model changes; the meshes of one material are merged, so a wounded pawn costs a few draws a frame and nothing more.
    // Drawing and building happen on the main thread only (People.Draw, Animals.Draw); the game's cache clears may come
    // from anywhere, so they only bump a per-pawn counter in a concurrent dictionary.
    internal static class WoundMarks
    {
        // Harness only (probe woundmarks=0|1): marks off for before/after shots in one run.
        internal static bool Enabled = true;

        // ---- invalidation (PawnOverlayDrawer.ClearCache postfix, Patches)

        private static readonly ConcurrentDictionary<int, int> versions = new ConcurrentDictionary<int, int>();
        private static AccessTools.FieldRef<PawnOverlayDrawer, Pawn> drawerPawn;
        private static bool drawerPawnTried;

        internal static void Cleared(PawnOverlayDrawer drawer)
        {
            if (!(drawer is PawnWoundDrawer))
            {
                return;
            }
            if (!drawerPawnTried)
            {
                drawerPawnTried = true;
                try
                {
                    drawerPawn = AccessTools.FieldRefAccess<PawnOverlayDrawer, Pawn>("pawn");
                }
                catch (Exception)
                {
                    drawerPawn = null;
                }
            }
            Pawn pawn = drawerPawn != null ? drawerPawn(drawer) : null;
            if (pawn != null)
            {
                versions.AddOrUpdate(pawn.thingIDNumber, 1, (k, v) => v + 1);
            }
        }

        private static int VersionOf(Pawn pawn)
        {
            int v;
            return versions.TryGetValue(pawn.thingIDNumber, out v) ? v : 0;
        }

        // ---- the game's wound draw calls, read through its own drawer

        // One wound as the game draws it on the sprite of one facing: the quad's placing in the sprite's plane (x right,
        // z up the picture), its half size, whether its picture is mirrored, the material to draw it with.
        private struct Call
        {
            public Matrix4x4 matrix;
            public Vector2 half;
            public bool flip;
            public bool overApparel;
            public Material material;
        }

        private static bool tried, ready;
        private static MethodInfo writeCache;
        private static ConstructorInfo keyMaker;
        private static Type callList;
        private static FieldInfo callMesh, callMatrix, callMaterial, callOver, callColour;
        private static readonly Dictionary<Mesh, bool> flipped = new Dictionary<Mesh, bool>();

        private static bool Ready
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    try
                    {
                        writeCache = AccessTools.Method(typeof(PawnWoundDrawer), "WriteCache");
                        Type key = AccessTools.Inner(typeof(PawnOverlayDrawer), "CacheKey");
                        Type call = AccessTools.Inner(typeof(PawnOverlayDrawer), "DrawCall");
                        keyMaker = key != null ? key.GetConstructor(new[] { typeof(Mesh), typeof(Rot4), typeof(PawnOverlayDrawer.OverlayLayer) }) : null;
                        callList = call != null ? typeof(List<>).MakeGenericType(call) : null;
                        if (call != null)
                        {
                            callMesh = AccessTools.Field(call, "overlayMesh");
                            callMatrix = AccessTools.Field(call, "matrix");
                            callMaterial = AccessTools.Field(call, "overlayMat");
                            callOver = AccessTools.Field(call, "displayOverApparel");
                            callColour = AccessTools.Field(call, "colorOverride");
                        }
                        ready = writeCache != null && keyMaker != null && callList != null && callMesh != null && callMatrix != null && callMaterial != null
                            && callOver != null && callColour != null;
                    }
                    catch (Exception e)
                    {
                        ready = false;
                        Log.Warning("[IsoRim] Wounds on 3D figures unavailable: " + e.Message);
                    }
                    if (!ready)
                    {
                        Log.Warning("[IsoRim] Wounds on 3D figures unavailable: the game's wound drawer was not found in the expected form.");
                    }
                }
                return ready;
            }
        }

        // The game's wounds of one facing and layer (its own WriteCache, as its render tree asks it), under a fixed random
        // state: the anchor it picks for a part is the same every time, and the game's own random numbers are left alone.
        private static void Calls(Pawn pawn, Rot4 facing, PawnOverlayDrawer.OverlayLayer layer, Mesh bodyMesh, List<Call> into)
        {
            PawnWoundDrawer drawer = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.WoundOverlays : null;
            if (drawer == null)
            {
                return;
            }
            IList list = (IList)Activator.CreateInstance(callList);
            PawnDrawParms parms = default(PawnDrawParms);
            parms.pawn = pawn;
            parms.facing = facing;
            parms.matrix = Matrix4x4.identity;
            parms.dead = pawn.Dead;
            Rand.PushState(Gen.HashCombineInt(pawn.thingIDNumber, facing.AsInt * 2 + (int)layer + 7121));
            try
            {
                writeCache.Invoke(drawer, new object[] { keyMaker.Invoke(new object[] { bodyMesh, facing, layer }), parms, list });
            }
            finally
            {
                Rand.PopState();
            }
            foreach (object c in list)
            {
                Mesh mesh = (Mesh)callMesh.GetValue(c);
                Material mat = (Material)callMaterial.GetValue(c);
                if (mesh == null || mesh == MeshPool.circle || mat == null || mat == BaseContent.BadMat || !(mat.mainTexture is Texture2D tex))
                {
                    continue;   // the game's debug circles (wound anchors shown from the health tab)
                }
                Color? colour = (Color?)callColour.GetValue(c);
                // Without the animal sprite's mask (the 3D mark is clipped by the model's own faces), in the colour the game
                // would draw it (skin tint, rotting, a mutant's wound colour).
                Material plain = MaterialPool.MatFrom(new MaterialRequest(tex, mat.shader, colour ?? mat.color));
                bool flip;
                if (!flipped.TryGetValue(mesh, out flip))
                {
                    Vector2[] uv = mesh.uv;
                    Vector3[] v = mesh.vertices;
                    flip = uv.Length > 0 && v.Length == uv.Length && (uv[0].x > 0.5f) == (v[0].x < 0f);
                    flipped[mesh] = flip;
                }
                into.Add(new Call
                {
                    matrix = (Matrix4x4)callMatrix.GetValue(c),
                    half = new Vector2(mesh.bounds.extents.x, mesh.bounds.extents.z),
                    flip = flip,
                    overApparel = (bool)callOver.GetValue(c),
                    material = plain,
                });
            }
        }

        // ---- the marks of one pawn

        internal struct Placed
        {
            public Mesh mesh;
            public Material material;
        }

        private sealed class Set
        {
            public int version = -1;
            public object owner;      // the look (people) or body model (animals) the meshes were made for
            public readonly List<Placed> body = new List<Placed>();
            public readonly List<Placed> head = new List<Placed>();
            public int marks;          // wounds placed (for the probe)
            public float buildMs;      // how long the last build took (for the probe)

            public void Forget()
            {
                foreach (Placed p in body)
                {
                    DynamicModels.Retire(p.mesh);
                }
                foreach (Placed p in head)
                {
                    DynamicModels.Retire(p.mesh);
                }
                body.Clear();
                head.Clear();
                marks = 0;
            }
        }

        private static readonly Dictionary<int, Set> sets = new Dictionary<int, Set>();

        // The pawn's set, worked out again when the game's wounds or the model changed; null off the main thread.
        private static Set SetFor(Pawn pawn, object owner, Action<Set> build)
        {
            if (!UnityData.IsInMainThread)
            {
                return null;
            }
            int version = VersionOf(pawn);
            Set s;
            if (sets.TryGetValue(pawn.thingIDNumber, out s) && s.version == version && s.owner == owner)
            {
                return s;
            }
            if (s == null)
            {
                if (sets.Count > 400)
                {
                    foreach (Set old in sets.Values)
                    {
                        old.Forget();   // pawns long gone; the ones still about are worked out again
                    }
                    sets.Clear();
                }
                s = new Set();
                sets[pawn.thingIDNumber] = s;
            }
            s.Forget();
            s.version = version;
            s.owner = owner;
            if (Ready)
            {
                try
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    build(s);
                    s.buildMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
                catch (Exception e)
                {
                    s.Forget();
                    Log.ErrorOnce("[IsoRim] Placing wounds on a 3D figure failed: " + e, 93626001);
                }
            }
            return s;
        }

        // For the probe: wounds placed and meshes of the pawn's set (-1: none made yet).
        internal static string Report(Pawn pawn)
        {
            Set s;
            if (pawn == null || !sets.TryGetValue(pawn.thingIDNumber, out s))
            {
                return "no set";
            }
            int tris = 0;
            foreach (Placed p in s.body)
            {
                tris += p.mesh != null ? p.mesh.triangles.Length / 6 : 0;
            }
            foreach (Placed p in s.head)
            {
                tris += p.mesh != null ? p.mesh.triangles.Length / 6 : 0;
            }
            return "marks " + s.marks + ", body meshes " + s.body.Count + ", head meshes " + s.head.Count + ", triangles " + tris + ", version " + s.version + ", built in " + s.buildMs.ToString("0.00") + " ms";
        }

        // ---- people (People.Draw)

        internal static void DrawPerson(Pawn pawn, People.Look look, Vector3 at, float yaw, Matrix4x4 bodyPlace, Matrix4x4 headPlace, bool body, bool head)
        {
            if (!Enabled || FigureScope.Measuring || look.alien != null || look.skull != null || look.upper == null || look.lower == null)
            {
                return;
            }
            Set s = SetFor(pawn, look, set => BuildPerson(pawn, look, set));
            if (s == null)
            {
                return;
            }
            if (body)
            {
                Draw(s.body, at, yaw, bodyPlace);
            }
            if (head)
            {
                Draw(s.head, at, yaw, headPlace);
            }
        }

        private static void BuildPerson(Pawn pawn, People.Look look, Set s)
        {
            BodyTypeDef type = pawn.story != null ? pawn.story.bodyType : null;
            Matrix4x4 bodyLocal = Matrix4x4.Scale(look.bodyScale);
            // Body: wounds over apparel on everything worn on the body; the others on bare skin only.
            List<Target> bodyTargets = new List<Target>();
            AddTarget(bodyTargets, look.lower, bodyLocal, !look.lowerWorn);
            AddTarget(bodyTargets, look.upper, bodyLocal, !look.upperWorn);
            AddTarget(bodyTargets, look.shirtWear, bodyLocal, false);
            AddTarget(bodyTargets, look.mid, bodyLocal, false);
            AddTarget(bodyTargets, look.legArmour, bodyLocal, false);
            AddTarget(bodyTargets, look.underCoat, bodyLocal, false);
            AddTarget(bodyTargets, look.coat, bodyLocal, false);
            AddTarget(bodyTargets, look.overCoat, bodyLocal, false);
            List<Target> headTargets = new List<Target>();
            Model headModel = look.headHidden ? null : (look.ownHead ?? People.Part("person_head"));
            float wide = look.ownHead != null ? 1f : look.headWide;
            Matrix4x4 headLocal = Matrix4x4.Scale(new Vector3(wide, 1f, 1f));
            AddTarget(headTargets, headModel, headLocal, true);
            if (headModel != null)
            {
                // The game draws head wounds over the hair and beard (under headgear): on them too, as People.Merged
                // places them (tucked under a hat).
                if (look.ownHair)
                {
                    AddTarget(headTargets, look.hair, Matrix4x4.identity, true);
                }
                else
                {
                    AddTarget(headTargets, HairTuck.Under(look.hair, look.hat, look.mouthWear, look.eyeWear), headLocal, true);
                    AddTarget(headTargets, HairTuck.Under(look.hairTop, look.hat, look.mouthWear, look.eyeWear), headLocal, true);
                }
                AddTarget(headTargets, HairTuck.Under(look.beard, look.hat, look.mouthWear, look.eyeWear), headLocal, true);
            }
            FigureBoxes.Box headBox = FigureBoxes.Head(look);

            GraphicMeshSet bodySet = HumanlikeMeshPoolUtility.GetHumanlikeBodySetForPawn(pawn);
            GraphicMeshSet headSet = HumanlikeMeshPoolUtility.GetHumanlikeHeadSetForPawn(pawn);
            Builder bodyOut = new Builder();
            Builder headOut = new Builder();
            List<Call> calls = new List<Call>();
            for (int r = 0; r < 4; r++)
            {
                Rot4 facing = new Rot4(r);
                calls.Clear();
                Calls(pawn, facing, PawnOverlayDrawer.OverlayLayer.Body, bodySet.MeshAt(facing), calls);
                Matrix4x4 sprite = FigureBoxes.BodySprite(look, type, facing);
                s.marks += Project(bodyTargets, facing, sprite, calls, bodyOut, true);
                if (headTargets.Count > 0)
                {
                    calls.Clear();
                    Calls(pawn, facing, PawnOverlayDrawer.OverlayLayer.Head, headSet.MeshAt(facing), calls);
                    s.marks += Project(headTargets, facing, FigureBoxes.HeadSpriteFacing(facing, wide, headBox.centre.z), calls, headOut, false);
                }
            }
            bodyOut.Finish(s.body);
            headOut.Finish(s.head);
        }

        // ---- animals (Animals.Draw)

        // body: the body model as drawn, turn: the model's own turn (ModelDef.rotation) and local: the rest of its placing
        // (pose and size, DrawLocal's local without turn); size: the animal's drawn size (cells per model unit).
        internal static void DrawAnimal(Pawn pawn, Model body, Vector3 at, float yaw, Matrix4x4 local, Matrix4x4 turn, float size)
        {
            if (!Enabled || FigureScope.Measuring || body == null || size <= 0f || (pawn.Dead && pawn.Corpse != null && pawn.Corpse.GetRotStage() == RotStage.Dessicated))
            {
                return;
            }
            Set s = SetFor(pawn, body, set => BuildAnimal(pawn, body, turn, size, set));
            if (s != null)
            {
                Draw(s.body, at, yaw, local);
            }
        }

        private static void BuildAnimal(Pawn pawn, Model body, Matrix4x4 turn, float size, Set s)
        {
            PawnRenderer renderer = pawn.Drawer != null ? pawn.Drawer.renderer : null;
            PawnRenderTree tree = renderer != null ? renderer.renderTree : null;
            if (tree == null)
            {
                return;
            }
            if (!tree.Resolved)
            {
                tree.EnsureInitialized(PawnRenderFlags.None);   // (the game's wound drawer reads the body graphic's mask)
            }
            Graphic graphic = renderer.BodyGraphic;
            if (graphic == null)
            {
                return;
            }
            List<Target> targets = new List<Target>();
            AddTarget(targets, body, turn, true);
            if (targets.Count == 0)
            {
                return;
            }
            Vector3 lo = targets[0].lo, hi = targets[0].hi;
            Vector3 centre = (lo + hi) * 0.5f;
            Builder output = new Builder();
            List<Call> calls = new List<Call>();
            for (int r = 0; r < 4; r++)
            {
                Rot4 facing = new Rot4(r);
                Mesh quad = graphic.MeshAt(facing);
                calls.Clear();
                Calls(pawn, facing, PawnOverlayDrawer.OverlayLayer.Body, quad, calls);
                if (calls.Count == 0)
                {
                    continue;
                }
                // The sprite's quad laid over the model's side as seen from that facing: the game puts the wounds round the
                // quad's middle, so their places are stretched from the quad onto the model's box. Each mark keeps the
                // share of the body the game's mark has of the sprite's animal (which fills about SpriteFill of its quad),
                // never more than its size in cells: a model smaller than its sprite (a duck) is not painted over.
                Vector3 ext = quad.bounds.extents;
                float across = facing.IsHorizontal ? (hi.z - lo.z) * 0.5f : (hi.x - lo.x) * 0.5f;
                float tall = (hi.y - lo.y) * 0.5f;
                float sx = ext.x > 0.01f ? across * size / ext.x : 1f;
                float sz = ext.z > 0.01f ? tall * size / ext.z : 1f;
                Matrix4x4 shrink = Matrix4x4.Scale(Vector3.one * Mathf.Clamp((sx + sz) * 0.5f / SpriteFill, 0.05f, 1f));
                for (int i = 0; i < calls.Count; i++)
                {
                    Call c = calls[i];
                    c.matrix.m03 *= sx;
                    c.matrix.m23 *= sz;
                    c.matrix = c.matrix * shrink;
                    calls[i] = c;
                }
                Matrix4x4 sprite = Matrix4x4.Translate(new Vector3(facing.IsHorizontal ? 0f : centre.x, centre.y, facing.IsHorizontal ? centre.z : 0f))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(FigureBoxes.SideTurn(facing), Vector3.up))
                    * Matrix4x4.Scale(Vector3.one / size);
                s.marks += Project(targets, facing, sprite, calls, output, true);
            }
            output.Finish(s.body);
        }

        // About how much of its sprite's quad an animal fills (the rest is the picture's empty margin).
        private const float SpriteFill = 0.75f;

        // ---- drawing

        private static void Draw(List<Placed> list, Vector3 at, float yaw, Matrix4x4 local)
        {
            if (list.Count == 0)
            {
                return;
            }
            float sq = View3D.Squash;
            Matrix4x4 placed = Matrix4x4.Translate(new Vector3(at.x, at.y / sq, at.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / sq, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * local;
            Matrix4x4 m = Floors.DrawnMatrix(FigureScope.Apply(placed));
            for (int i = 0; i < list.Count; i++)
            {
                Placed p = list[i];
                if (p.mesh != null && p.material != null)
                {
                    Graphics.DrawMesh(p.mesh, m, p.material, 0);
                }
            }
        }

        // ---- projecting the wounds onto the model

        // How far each mark stands out from the surface (model units, along the smoothed normals), the first and each
        // further one (overlapping wounds do not fight).
        private const float Lift = 0.004f;
        private const float LiftStep = 0.0015f;
        // A face takes the wounds of the side its normal points to most, if at least this much (a unit normal's share).
        private const float Facing = 0.2f;

        // A model's faces in the frame the marks are drawn in (its local placing applied), with their side.
        private sealed class Target
        {
            public Vector3[] p;       // corners, three per face
            public Vector3[] n;       // smoothed normals at the corners
            public int[] side;        // per face: the facing whose wounds it takes (-1: none)
            public bool skin;         // bare skin: also takes the wounds drawn under apparel
            public Vector3 lo, hi;
        }

        private static readonly Dictionary<(int, Matrix4x4), Target> targetCache = new Dictionary<(int, Matrix4x4), Target>();

        private static void AddTarget(List<Target> into, Model m, Matrix4x4 local, bool skin)
        {
            if (m == null || m.positions == null || m.positions.Length < 3)
            {
                return;
            }
            // (one per model and placing: the same body and garments are shared by many pawns)
            (int, Matrix4x4) key = (m.id, local);
            Target t;
            if (!targetCache.TryGetValue(key, out t))
            {
                if (targetCache.Count > 1024)
                {
                    targetCache.Clear();
                }
                t = MakeTarget(m, local);
                targetCache[key] = t;
            }
            into.Add(new Target { p = t.p, n = t.n, side = t.side, skin = skin, lo = t.lo, hi = t.hi });
        }

        private static readonly Vector3[] sideDir = { Vector3.back, Vector3.right, Vector3.forward, Vector3.left };   // north, east, south, west

        private static Target MakeTarget(Model m, Matrix4x4 local)
        {
            int count = m.positions.Length / 3 * 3;
            Target t = new Target { p = new Vector3[count], n = new Vector3[count], side = new int[count / 3] };
            Matrix4x4 normalLocal = local.inverse.transpose;
            t.lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            t.hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < count; i++)
            {
                t.p[i] = local.MultiplyPoint3x4(m.positions[i]);
                t.lo = Vector3.Min(t.lo, t.p[i]);
                t.hi = Vector3.Max(t.hi, t.p[i]);
            }
            Vector3 middle = (t.lo + t.hi) * 0.5f;
            Vector3[] faceNormal = new Vector3[count / 3];
            Dictionary<Vector3, Vector3> smooth = new Dictionary<Vector3, Vector3>();
            for (int i = 0; i < count; i += 3)
            {
                Vector3 a = t.p[i], b = t.p[i + 1], c = t.p[i + 2];
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-14f)
                {
                    continue;
                }
                Vector3 vn = m.normals != null && m.normals.Length >= count ? normalLocal.MultiplyVector(m.normals[i] + m.normals[i + 1] + m.normals[i + 2]) : Vector3.zero;
                Vector3 centroid = (a + b + c) / 3f;
                if (vn.sqrMagnitude > 1e-8f ? Vector3.Dot(n, vn) < 0f : Vector3.Dot(n, new Vector3(centroid.x - middle.x, 0f, centroid.z - middle.z)) < 0f)
                {
                    n = -n;   // outward, as the model's own normals (or away from its upright axis)
                }
                faceNormal[i / 3] = n.normalized;
                for (int k = i; k < i + 3; k++)
                {
                    Vector3 sum;
                    smooth.TryGetValue(t.p[k], out sum);
                    smooth[t.p[k]] = sum + n;   // (by area)
                }
            }
            for (int f = 0; f < count / 3; f++)
            {
                Vector3 n = faceNormal[f];
                int best = -1;
                float most = Facing;
                for (int d = 0; d < 4 && n != Vector3.zero; d++)
                {
                    float dot = Vector3.Dot(n, sideDir[d]);
                    if (dot >= most)
                    {
                        most = dot;
                        best = d;
                    }
                }
                t.side[f] = best;
                for (int k = f * 3; k < f * 3 + 3; k++)
                {
                    Vector3 sn;
                    Vector3 vnormal = smooth.TryGetValue(t.p[k], out sn) ? sn.normalized : n;
                    t.n[k] = Vector3.Dot(vnormal, n) < 0.3f ? n : vnormal;   // a knife edge: its own face's normal
                }
            }
            return t;
        }

        // The vertices of one face being clipped: frame position, normal, and place in the wound's quad (x, z).
        private struct V
        {
            public Vector3 p;
            public Vector3 n;
            public Vector2 q;

            public static V Lerp(V a, V b, float t)
            {
                return new V { p = Vector3.Lerp(a.p, b.p, t), n = Vector3.Lerp(a.n, b.n, t), q = Vector2.Lerp(a.q, b.q, t) };
            }
        }

        private static readonly List<V> poly = new List<V>(12);
        private static readonly List<V> next = new List<V>(12);

        // Prints one facing's wounds onto the faces of the targets turned that way. sprite maps the facing's sprite
        // (x right, y up the picture, z into it; the game's cells from the sprite's middle) into the frame. Returns the
        // wounds placed.
        private static int Project(List<Target> targets, Rot4 facing, Matrix4x4 sprite, List<Call> calls, Builder output, bool bodyLayer)
        {
            if (calls.Count == 0)
            {
                return 0;
            }
            Matrix4x4 toSprite = sprite.inverse;
            int side = facing.AsInt;
            int placed = 0;
            for (int ci = 0; ci < calls.Count; ci++)
            {
                Call c = calls[ci];
                // frame -> sprite plane (x, 0, y up the picture) -> the wound quad's own (x, z)
                Matrix4x4 toQuad = c.matrix.inverse * SpritePlane * toSprite;
                float lift = Lift + LiftStep * (output.Count(c.material) % 6);
                bool any = false;
                for (int ti = 0; ti < targets.Count; ti++)
                {
                    Target t = targets[ti];
                    if (bodyLayer && !c.overApparel && !t.skin)
                    {
                        continue;   // drawn under apparel: only on bare skin
                    }
                    for (int f = 0; f < t.side.Length; f++)
                    {
                        if (t.side[f] != side)
                        {
                            continue;
                        }
                        int i = f * 3;
                        Vector3 qa = toQuad.MultiplyPoint3x4(t.p[i]), qb = toQuad.MultiplyPoint3x4(t.p[i + 1]), qc = toQuad.MultiplyPoint3x4(t.p[i + 2]);
                        float hx = c.half.x, hz = c.half.y;
                        if ((qa.x > hx && qb.x > hx && qc.x > hx) || (qa.x < -hx && qb.x < -hx && qc.x < -hx)
                            || (qa.z > hz && qb.z > hz && qc.z > hz) || (qa.z < -hz && qb.z < -hz && qc.z < -hz))
                        {
                            continue;
                        }
                        poly.Clear();
                        poly.Add(new V { p = t.p[i], n = t.n[i], q = new Vector2(qa.x, qa.z) });
                        poly.Add(new V { p = t.p[i + 1], n = t.n[i + 1], q = new Vector2(qb.x, qb.z) });
                        poly.Add(new V { p = t.p[i + 2], n = t.n[i + 2], q = new Vector2(qc.x, qc.z) });
                        Clip(0, -hx, true);
                        Clip(0, hx, false);
                        Clip(1, -hz, true);
                        Clip(1, hz, false);
                        if (poly.Count < 3)
                        {
                            continue;
                        }
                        output.Add(c.material, poly, lift, c.half, c.flip);
                        any = true;
                    }
                }
                if (any)
                {
                    output.Placed(c.material);
                    placed++;
                }
            }
            return placed;
        }

        // The sprite's picture plane in the game's sprite space: (x right, y up) -> (x, 0, z).
        private static readonly Matrix4x4 SpritePlane = new Matrix4x4(
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));

        // One edge of the quad (Sutherland-Hodgman): keeps what lies at or past edge on axis (0 x, 1 z), low: >= edge.
        private static void Clip(int axis, float edge, bool low)
        {
            next.Clear();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                V a = poly[i], b = poly[(i + 1) % n];
                float da = (low ? 1f : -1f) * ((axis == 0 ? a.q.x : a.q.y) - edge);
                float db = (low ? 1f : -1f) * ((axis == 0 ? b.q.x : b.q.y) - edge);
                if (da >= 0f)
                {
                    next.Add(a);
                }
                if ((da >= 0f) != (db >= 0f))
                {
                    next.Add(V.Lerp(a, b, da / (da - db)));
                }
            }
            poly.Clear();
            poly.AddRange(next);
        }

        // The marks being made, one mesh per material.
        private sealed class Builder
        {
            private sealed class Part
            {
                public readonly List<Vector3> verts = new List<Vector3>();
                public readonly List<Vector2> uvs = new List<Vector2>();
                public readonly List<int> tris = new List<int>();
                public int placed;
            }

            private readonly Dictionary<Material, Part> parts = new Dictionary<Material, Part>();
            private readonly List<Material> order = new List<Material>();

            private Part Of(Material m)
            {
                Part p;
                if (!parts.TryGetValue(m, out p))
                {
                    p = new Part();
                    parts[m] = p;
                    order.Add(m);
                }
                return p;
            }

            public int Count(Material m)
            {
                Part p;
                return parts.TryGetValue(m, out p) ? p.placed : 0;
            }

            public void Placed(Material m)
            {
                Of(m).placed++;
            }

            public void Add(Material m, List<V> poly, float lift, Vector2 half, bool flip)
            {
                Part p = Of(m);
                int first = p.verts.Count;
                for (int k = 0; k < poly.Count; k++)
                {
                    V v = poly[k];
                    p.verts.Add(v.p + v.n.normalized * lift);
                    float u = v.q.x / (2f * half.x) + 0.5f;
                    p.uvs.Add(new Vector2(flip ? 1f - u : u, v.q.y / (2f * half.y) + 0.5f));
                }
                // A fan, both windings (the mark shows whatever the material culls).
                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    p.tris.Add(first);
                    p.tris.Add(first + k);
                    p.tris.Add(first + k + 1);
                    p.tris.Add(first);
                    p.tris.Add(first + k + 1);
                    p.tris.Add(first + k);
                }
            }

            public void Finish(List<Placed> into)
            {
                foreach (Material m in order)
                {
                    Part p = parts[m];
                    if (p.verts.Count == 0)
                    {
                        continue;
                    }
                    Mesh mesh = new Mesh();
                    if (p.verts.Count > 65000)
                    {
                        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    }
                    mesh.SetVertices(p.verts);
                    mesh.SetUVs(0, p.uvs);
                    List<Color32> colours = new List<Color32>(p.verts.Count);
                    List<Vector3> tags = new List<Vector3>(p.verts.Count);
                    for (int i = 0; i < p.verts.Count; i++)
                    {
                        colours.Add(new Color32(255, 255, 255, 255));
                        tags.Add(RoofClip.MovingTag);
                    }
                    mesh.SetColors(colours);
                    mesh.SetUVs(1, tags);
                    mesh.SetTriangles(p.tris, 0);
                    mesh.RecalculateBounds();
                    MeshGuard.Check(mesh, "wound marks");
                    into.Add(new Placed { mesh = mesh, material = m });
                }
            }
        }
    }
}
