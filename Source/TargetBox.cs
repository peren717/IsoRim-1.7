using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The target box: while the player aims (the game's targeter) or holds a designator that works on things, the thing
    // the pointer is on gets the corners of a 3D box round it, drawn as the game's own mouse-over bracket looks (its colour
    // and alpha read from GenUI.MouseoverBracketMaterial: thin white lines, no pulse; the same for aiming and designating).
    // THE one place for its rule, its look and which of the game's own marks it replaces.
    //
    // When (the game's own answers, never names):
    //   - targeter (Find.Targeter.IsTargeting): the target the game resolves under the pointer (Targeter.CurrentTargetUnderMouse,
    //     as TargeterUpdate passes it to the highlight) is a spawned Thing on this map, the targeting accepts it (its source's
    //     ValidateTarget, or an action targeting's own validator; messages these may post are held back), and the targeting
    //     draws no area round it (the source's verb: HighlightFieldRadiusAroundTarget > 0, or its ability's HasAreaOfEffect).
    //     A targeting with its own highlight drawer (highlightAction) keeps its own look. Cells (landing spots, jumps,
    //     drop points) are never boxed: the game resolves them as cells.
    //   - designator (Find.DesignatorManager.SelectedDesignator) whose class overrides Designator.CanDesignateThing (the
    //     base rejects every thing): the first thing in the cell the designator works on (Interaction.DesignatorMouseCell:
    //     the click's cell, a block's cell for the block designators, upper floors included) that its CanDesignateThing
    //     accepts, the cell itself accepted by CanDesignateCell (as the click checks it). Cell-only designators never box.
    // Look: the corners on the box's outline as the camera sees it only (SilhouetteEdges: the corners' convex hull on screen,
    // usually 6 of 8), each a bracket of the two outline edges meeting there, CornerShare of each edge long, LinePixels thick
    // on screen; footprint = the thing's rect (a pawn: one cell round where it is drawn) grown by Grow, height = the thing's
    // own (HeightOf), from the floor it stands on. Things are depth tested (corners
    // behind the thing are hidden by it); rock and wall cells (blocks baked into the map mesh, WallBoxes.IsWallForm) are
    // drawn over everything.
    // While a box is shown, the game's flat marks of that same target are left out: the targeting square of the thing
    // (GenDraw.DrawTargetingHighlight_Thing), position marks inside its footprint (DrawTargetHighlightWithLayer) and the
    // mouse-over bracket in its footprint (GenUI.RenderMouseoverBracket, IsoRim's lifted one too). Range rings, area
    // circles, cursor icons and drag highlights stay. Setting off (targetBox) or the 3D view off: exactly the game's look.
    // Cost: nothing unless a targeting or a designator is on; then one resolution and at most one box per frame.
    [StaticConstructorOnStartup]
    internal static class TargetBox
    {
        // ---- tuning (cells; one place)
        private const float CornerShare = 0.25f;   // each bracket bar: this share of the edge it lies along
        private const float Grow = 0.03f;          // the box past the thing's rect on every side and above its top
        private const float MinHeight = 0.2f;      // the box of a thing drawn flat (no height of its own)
        private const float LinePixels = 1f;       // line thickness on screen (pixels): about the game's mouse-over bracket's
                                                   // stroke at the default zoom (measured: BracketInfo, see the report)
        private const float OutlineTolerance = 0.5f;   // screen pixels: a corner this close to an edge's line counts as on it
        private const int ThingQueue = 3110;       // after the game's lighting overlay (3100), before the fog (3175)
        private const int OnTopQueue = 3650;       // over everything (as IsoRim's other overlays)

        // ---- harness counters
        internal static int Drawn;          // boxes drawn
        internal static int MarksHidden;    // game marks left out for the boxed target
        internal static int BracketsHidden;
        internal static float LastHeight;   // the last box's height (true cells, without Grow)
        internal static Rect LastFootprint; // the last box's footprint (cells, without Grow)

        // ---- this frame's answer
        private static int frame = -1;
        private static Thing target;
        private static bool onTop;
        private static Rect footprint;      // x/z, cells, without Grow
        private static bool failed;

        // The figure of the last target (a pawn, a corpse's pawn), for the drawers' note of its posed top (SelectionRings).
        // The last answer, never worked out from inside a draw.
        internal static Pawn Figure;

        // The thing boxed this frame, or null.
        internal static Thing Target
        {
            get
            {
                Resolve();
                return target;
            }
        }

        // Forgets every thing and map kept (Patches: a new or loaded game, no game running; Resolve: another map, no target).
        internal static void Reset()
        {
            frame = -1;
            target = null;
            Figure = null;
            modelOf = null;
            lastMap = null;
        }

        private static Map lastMap;

        private static void Resolve()
        {
            if (frame == Time.frameCount)
            {
                return;
            }
            Map current = Find.CurrentMap;
            if (current != lastMap)
            {
                Reset();
                lastMap = current;
            }
            frame = Time.frameCount;
            target = null;
            IsoRimSettings st = IsoRimMod.Settings;
            if (failed || st == null || !st.targetBox || !View3D.Ready)
            {
                Figure = null;
                modelOf = null;
                return;
            }
            Map map = Find.CurrentMap;
            Targeter targeter = Find.Targeter;
            DesignatorManager dm = Find.DesignatorManager;
            bool targeting = targeter != null && targeter.IsTargeting;
            Designator designator = !targeting && dm != null ? dm.SelectedDesignator : null;
            if (map == null || !targeting && designator == null || !RimWorld.Planet.WorldRendererUtility.DrawingMap)
            {
                Figure = null;
                modelOf = null;
                return;
            }
            try
            {
                Thing t = targeting ? FromTargeter(targeter, map) : FromDesignator(designator, map);
                if (t != null)
                {
                    target = t;
                    onTop = WallBoxes.IsWallForm(Forms.For(t.def));
                    footprint = FootprintOf(t);
                }
            }
            catch (Exception e)
            {
                failed = true;   // fail-soft: the game's own marks from now on
                Log.ErrorOnce("[IsoRim] Working out the target box failed; the game's own marks are shown: " + e, 93627001);
                target = null;
            }
            Figure = target as Pawn ?? (target is Corpse corpse ? corpse.InnerPawn : null);
            if (target == null)
            {
                modelOf = null;   // a later target (a grown plant again) works its model out anew
            }
        }

        // ---- the rule

        // The targeter's own resolution and checks (private in the game), looked up on first use (Resolve's fail-soft catch).
        private static Func<Targeter, bool, LocalTargetInfo> currentTarget;
        private static AccessTools.FieldRef<Targeter, Action<LocalTargetInfo>> highlightAction;
        private static AccessTools.FieldRef<Targeter, Func<LocalTargetInfo, bool>> targetValidator;

        private static Thing FromTargeter(Targeter targeter, Map map)
        {
            if (currentTarget == null)
            {
                currentTarget = AccessTools.MethodDelegate<Func<Targeter, bool, LocalTargetInfo>>(AccessTools.Method(typeof(Targeter), "CurrentTargetUnderMouse"));
                highlightAction = AccessTools.FieldRefAccess<Targeter, Action<LocalTargetInfo>>("highlightAction");
                targetValidator = AccessTools.FieldRefAccess<Targeter, Func<LocalTargetInfo, bool>>("targetValidator");
            }
            ITargetingSource source = targeter.targetingSource;
            if (source == null && highlightAction(targeter) != null)
            {
                return null;   // its own highlight drawer: its own look
            }
            // as TargeterUpdate resolves it for the highlight: hittable now for a targeting source
            LocalTargetInfo at = currentTarget(targeter, source != null);
            Thing t = at.Thing;
            if (t == null || !t.Spawned || t.Map != map)
            {
                return null;
            }
            if (source != null)
            {
                if (DrawsArea(source))
                {
                    return null;
                }
                quiet++;
                try
                {
                    // a verb marks only what it can hit now (range, line of sight), as Verb_CastBase.DrawHighlight and OnGUI do
                    Verb verb = source as Verb;
                    if (verb != null && !verb.CanHitTarget(at))
                    {
                        return null;
                    }
                    return source.ValidateTarget(at, false) ? t : null;
                }
                finally
                {
                    quiet--;
                }
            }
            Func<LocalTargetInfo, bool> valid = targetValidator(targeter);
            if (valid == null)
            {
                return t;
            }
            quiet++;
            try
            {
                return valid(at) ? t : null;
            }
            finally
            {
                quiet--;
            }
        }

        // The targeting marks an area round its target (the game's own answers: the verb's field radius, as Verb.DrawHighlight
        // draws it; an ability's effect radius, as Verb_CastAbility.DrawHighlight draws it).
        private static bool DrawsArea(ITargetingSource source)
        {
            Verb verb = source.GetVerb;
            if (verb == null)
            {
                return false;
            }
            if (verb.HighlightFieldRadiusAroundTarget(out bool _) > 0f)
            {
                return true;
            }
            Verb_CastAbility cast = verb as Verb_CastAbility;
            return cast != null && cast.ability != null && cast.ability.def != null && cast.ability.def.HasAreaOfEffect;
        }

        private static readonly Dictionary<Type, bool> designatesThings = new Dictionary<Type, bool>();

        // The designator's class overrides CanDesignateThing (the base one rejects everything): it works on things.
        private static bool DesignatesThings(Designator d)
        {
            Type type = d.GetType();
            bool yes;
            if (!designatesThings.TryGetValue(type, out yes))
            {
                System.Reflection.MethodInfo m = type.GetMethod(nameof(Designator.CanDesignateThing), new[] { typeof(Thing) });
                yes = m != null && m.GetBaseDefinition().DeclaringType == typeof(Designator) && m.DeclaringType != typeof(Designator);
                designatesThings[type] = yes;
            }
            return yes;
        }

        private static Thing FromDesignator(Designator d, Map map)
        {
            if (!DesignatesThings(d))
            {
                return null;
            }
            IntVec3 c = Interaction.DesignatorMouseCell();
            if (!c.InBounds(map))
            {
                return null;
            }
            quiet++;
            try
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    if (d.CanDesignateThing(things[i]).Accepted)
                    {
                        return d.CanDesignateCell(c).Accepted ? things[i] : null;
                    }
                }
                return null;
            }
            finally
            {
                quiet--;
            }
        }

        // ---- messages the game's checks post while asked every frame (held back)

        private static int quiet;

        internal static bool Quiet
        {
            get { return quiet > 0; }
        }

        // ---- size

        // The figure (a pawn, a corpse's pawn) whose posed body the drawers note, or null.
        private static Pawn FigureOf(Thing t)
        {
            return t as Pawn ?? (t is Corpse corpse ? corpse.InnerPawn : null);
        }

        // The thing's rect on the ground (cells, without Grow):
        //   - a figure: its body as the drawers posed it (SelectionRings.FigureBox: a muffalo's whole body, a person's body
        //     and head); not noted yet (the first frame, a 2D card): one cell round where it is drawn;
        //   - a plant drawn from a model: its occupied rect and the model as the bake places it (a tree's canopy);
        //   - anything else: its occupied rect.
        private static Rect FootprintOf(Thing t)
        {
            Pawn figure = FigureOf(t);
            if (figure != null)
            {
                Rect posed;
                float top;
                if (SelectionRings.FigureBox(figure, out posed, out top))
                {
                    return posed;
                }
                Vector3 p = t.DrawPos;
                return new Rect(p.x - 0.5f, p.z - 0.5f, 1f, 1f);
            }
            CellRect r = t.OccupiedRect();
            Rect rect = new Rect(r.minX, r.minZ, r.Width, r.Height);
            if (t is Plant && ModelOf(t))
            {
                rect = Rect.MinMaxRect(Mathf.Min(rect.xMin, modelRect.xMin), Mathf.Min(rect.yMin, modelRect.yMin),
                    Mathf.Max(rect.xMax, modelRect.xMax), Mathf.Max(rect.yMax, modelRect.yMax));
            }
            return rect;
        }

        // The thing's own height (true cells over the floor it stands on), at least MinHeight:
        //   - a figure: the top of its body as the drawers posed it (lying in bed, downed: low); not noted yet: IsoRim's pick
        //     height;
        //   - anything else, the highest of IsoRim's pick height (a model's top with its drawn parts, a turret's gun, a
        //     block's height as cut) and the top of its model as the bake places it (a grown tree) on what it stands on.
        internal static float HeightOf(Thing t)
        {
            Pawn figure = FigureOf(t);
            if (figure != null)
            {
                Rect posed;
                float top;
                return Mathf.Max(SelectionRings.FigureBox(figure, out posed, out top) ? top : Interaction.PickHeight(t), MinHeight);
            }
            float h = Interaction.PickHeight(t);
            if (ModelOf(t))
            {
                h = Mathf.Max(h, modelTop);
            }
            return Mathf.Max(h, MinHeight);
        }

        // The model of a thing drawn from one, as the bake places it (ModelBaker.PlacedBounds, no printing): modelTop (on what
        // it stands on) and modelRect, worked out once per target (Reset and Resolve forget it). False without a model.
        private static bool ModelOf(Thing t)
        {
            if (modelOf != t)
            {
                modelOf = t;
                ThingForm f = Forms.For(t);
                Vector3 lo = Vector3.zero, hi = Vector3.zero;
                hasModel = f.form == Form3D.Model && f.model != null && f.model.HasModel && ModelBaker.PlacedBounds(t, f.model, out lo, out hi);
                if (hasModel)
                {
                    modelTop = hi.y + Forms.BaseUnder(t, t.def.category == ThingCategory.Item);
                    modelRect = Rect.MinMaxRect(lo.x, lo.z, hi.x, hi.z);
                }
            }
            return hasModel;
        }

        private static Thing modelOf;   // the thing the model fields are for
        private static bool hasModel;
        private static float modelTop;
        private static Rect modelRect;

        // ---- the game's marks of the boxed target

        // GenDraw.DrawTargetingHighlight_Thing: its square of this thing.
        internal static bool HidesMarkOf(Thing t)
        {
            if (t == null || Target != t)
            {
                return false;
            }
            MarksHidden++;
            return true;
        }

        // GenDraw.DrawTargetHighlightWithLayer at a point: a mark inside the boxed target's footprint.
        internal static bool HidesMarkAt(Vector3 p)
        {
            if (Target == null || !footprint.Contains(new Vector2(p.x, p.z)))
            {
                return false;
            }
            MarksHidden++;
            return true;
        }

        // GenUI.RenderMouseoverBracket: the bracket's cell (the designators' pointer cell) inside the boxed target's footprint.
        internal static bool HidesBracket()
        {
            if (Target == null)
            {
                return false;
            }
            IntVec3 c = Interaction.DesignatorMouseCell();
            if (!footprint.Contains(new Vector2(c.x + 0.5f, c.z + 0.5f)))
            {
                return false;
            }
            BracketsHidden++;
            return true;
        }

        // The game's DrawMesh in GenDraw.DrawTargetingHighlight_Thing (Patches.TargetingSquare_Transpiler), unless boxed.
        public static void ThingSquare(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer, Thing t)
        {
            bool hide = false;
            try
            {
                hide = HidesMarkOf(t);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] Leaving out the game's target square failed: " + e, 93627004);
            }
            if (!hide)
            {
                Graphics.DrawMesh(mesh, position, rotation, material, layer);
            }
        }

        // ---- drawing (View3D, every frame while the 3D view is ready)

        private static Material thingMat, onTopMat;
        private static MaterialPropertyBlock props;
        private static readonly int colorId = Shader.PropertyToID("_Color");
        private static Mesh mesh;
        private static Vector4 meshKey = new Vector4(-1f, -1f, -1f, -1f);
        private static int meshEdges = -1;

        internal static void Draw()
        {
            try
            {
                DrawBox();
            }
            catch (Exception e)
            {
                failed = true;   // fail-soft: the game's own marks from now on
                Log.ErrorOnce("[IsoRim] Drawing the target box failed; the game's own marks are shown: " + e, 93627005);
            }
        }

        private static void DrawBox()
        {
            Thing t = Target;
            if (t == null)
            {
                return;
            }
            if (thingMat == null)
            {
                thingMat = R3DMaterials.Make(R3DMaterials.BlendDef, "IsoRim_TargetBox", ThingQueue);
                onTopMat = R3DMaterials.Make(R3DMaterials.OverlayDef, "IsoRim_TargetBoxOnTop", OnTopQueue);
                props = new MaterialPropertyBlock();
                if (thingMat == null || onTopMat == null)
                {
                    failed = true;
                    return;
                }
            }
            float h = HeightOf(t);
            float s = View3D.Squash;
            float w = footprint.width + 2f * Grow, d = footprint.height + 2f * Grow, top = h + Grow;
            // where the box shows (true space: its floor's and the layout's offset), for the outline and the line width
            Vector3 at = new Vector3(footprint.center.x, 0f, footprint.center.y) + Floors.ThingOffset(t);
            int edges = SilhouetteEdges(at, w, top, d);
            float bar = LinePixels * View3D.CellsPerPixel(at + new Vector3(0f, top * 0.5f, 0f));
            Vector4 key = new Vector4(w, d, top, bar);
            if (mesh == null || key != meshKey || edges != meshEdges)
            {
                meshKey = key;
                meshEdges = edges;
                BuildCorners(w, d, top / s, bar, bar / s, edges);
            }
            props.SetColor(colorId, BracketColour);
            // placed by its middle: the row of the matrix tells an upper floor's things from the ground's (Floors.Single_Prefix)
            Matrix4x4 m = Floors.ExtraMatrix(t) * Matrix4x4.Translate(new Vector3(footprint.center.x, 0f, footprint.center.y));
            Graphics.DrawMesh(mesh, m, onTop ? onTopMat : thingMat, 0, null, 0, props);
            Drawn++;
            LastHeight = h;
            LastFootprint = footprint;
            LastEdges = edges;
            LastCorners = 0;
            for (int k = 0; k < 8; k++)
            {
                for (int e = 0; e < 12; e++)
                {
                    if ((BoxEdges[e, 0] == k && (edges & 1 << (e * 2)) != 0) || (BoxEdges[e, 1] == k && (edges & 1 << (e * 2 + 1)) != 0))
                    {
                        LastCorners++;
                        break;
                    }
                }
            }
        }

        // ---- the outline seen from the camera

        // Corner k of the box: x by bit 0, height by bit 1, z by bit 2. Its 12 edges join corners one bit apart.
        private static readonly int[,] BoxEdges =
        {
            { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 },   // along x
            { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 },   // upright
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }    // along z
        };

        private static readonly Vector2[] screen = new Vector2[8];
        private static readonly List<int> order = new List<int>(8), hull = new List<int>(16);

        // The bars on the box's outline as the camera sees it (bit 2e + end: a bar from corner BoxEdges[e, end] along edge e):
        // the 8 corners projected (View3D.WorldToScreen, any camera: turned, tilted, perspective, first person), the corners
        // of their convex hull, and from each of those the edges that lie on the outline. Usually 6 corners with 2 bars each;
        // the corners inside the outline get none.
        private static int SilhouetteEdges(Vector3 at, float w, float top, float d)
        {
            for (int k = 0; k < 8; k++)
            {
                screen[k] = View3D.WorldToScreen(at + Corner(k, w, top, d));
            }
            // Andrew's monotone chain: the hull's corners in order, points on a hull side left out
            order.Clear();
            for (int k = 0; k < 8; k++)
            {
                order.Add(k);
            }
            order.Sort((a, b) => screen[a].x != screen[b].x ? screen[a].x.CompareTo(screen[b].x) : screen[a].y.CompareTo(screen[b].y));
            hull.Clear();
            for (int pass = 0; pass < 2; pass++)
            {
                int first = hull.Count;
                for (int i = 0; i < 8; i++)
                {
                    int k = order[pass == 0 ? i : 7 - i];
                    while (hull.Count - first >= 2 && Cross(screen[hull[hull.Count - 2]], screen[hull[hull.Count - 1]], screen[k]) <= 0f)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(k);
                }
                hull.RemoveAt(hull.Count - 1);   // each chain's last point starts the other
            }
            // A bar from a hull corner along each box edge that lies on the outline (every corner on one side of its line on
            // screen: the box seen square-on has outline sides made of several edges in one line).
            int mask = 0;
            for (int e = 0; e < 12; e++)
            {
                Vector2 pa = screen[BoxEdges[e, 0]], pb = screen[BoxEdges[e, 1]];
                float len = (pb - pa).magnitude;
                if (len < OutlineTolerance)
                {
                    continue;   // seen end-on
                }
                float lo = 0f, hi = 0f;
                for (int k = 0; k < 8; k++)
                {
                    float side = Cross(pa, pb, screen[k]) / len;
                    lo = Mathf.Min(lo, side);
                    hi = Mathf.Max(hi, side);
                }
                if (lo < -OutlineTolerance && hi > OutlineTolerance)
                {
                    continue;   // corners on both sides: inside the outline
                }
                for (int end = 0; end < 2; end++)
                {
                    if (hull.Contains(BoxEdges[e, end]))
                    {
                        mask |= 1 << (e * 2 + end);
                    }
                }
            }
            return mask;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        // ---- the look of the game's own mouse-over bracket

        private static Color? bracketColour;

        // The colour the game's mouse-over bracket shows in (GenUI.MouseoverBracketMaterial: its colour times its texture's
        // most opaque texel, so its alpha too); the material's colour alone if the texture cannot be read.
        internal static Color BracketColour
        {
            get
            {
                if (!bracketColour.HasValue)
                {
                    Material m = AccessTools.StaticFieldRefAccess<Material>(typeof(GenUI), "MouseoverBracketMaterial");
                    Color c = m != null ? m.color : Color.white;
                    try
                    {
                        Texture2D tex = m != null ? m.mainTexture as Texture2D : null;
                        Color32[] px = tex != null ? tex.GetPixels32() : null;
                        if (px != null && px.Length > 0)
                        {
                            Color32 best = px[0];
                            for (int i = 1; i < px.Length; i++)
                            {
                                if (px[i].a > best.a)
                                {
                                    best = px[i];
                                }
                            }
                            c *= (Color)best;
                            BracketInfo = "material colour " + m.color + ", texture " + tex.width + "x" + tex.height + ", most opaque texel " + best
                                + ", stroke " + StrokeTexels(px, tex.width, tex.height) + " texels";
                        }
                    }
                    catch (Exception e)
                    {
                        BracketInfo = "material colour " + c + ", texture not readable (" + e.GetType().Name + ")";
                    }
                    bracketColour = c;
                }
                return bracketColour.Value;
            }
        }

        internal static string BracketInfo = "";   // harness: what the bracket's look was read from
        internal static int LastEdges;             // harness: the last box's outline bars (SilhouetteEdges)
        internal static int LastCorners;           // harness: the corners of the last box that got bars

        // How thick the bracket texture's stroke is (texels): down the column of its first opaque texel from the top.
        private static int StrokeTexels(Color32[] px, int width, int height)
        {
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    if (px[y * width + x].a > 127)
                    {
                        int n = 0;
                        while (y - n >= 0 && px[(y - n) * width + x].a > 127)
                        {
                            n++;
                        }
                        return n;
                    }
                }
            }
            return 0;
        }

        // ---- the mesh

        private static readonly List<Vector3> verts = new List<Vector3>();
        private static readonly List<int> tris = new List<int>();
        private static readonly List<Color32> colours = new List<Color32>();

        // The box -w/2..w/2 by 0..hy (drawn units) by -d/2..d/2 as brackets on its outline: for each bar in edges (bit 2e + end:
        // from corner BoxEdges[e, end] along edge e, SilhouetteEdges) a bar CornerShare of the edge long, bar thick across (barY upright, so it
        // looks bar thick in true space), reaching half a bar past the corner so the two bars of a corner meet.
        private static void BuildCorners(float w, float d, float hy, float bar, float barY, int edges)
        {
            verts.Clear();
            tris.Clear();
            Vector3 half = new Vector3(bar * 0.5f, barY * 0.5f, bar * 0.5f);
            for (int e = 0; e < 12; e++)
            {
                for (int end = 0; end < 2; end++)
                {
                    if ((edges & 1 << (e * 2 + end)) == 0)
                    {
                        continue;
                    }
                    Vector3 c = Corner(BoxEdges[e, end], w, hy, d), o = Corner(BoxEdges[e, 1 - end], w, hy, d);
                    Vector3 along = (o - c) * CornerShare;
                    AddBar(Vector3.Min(c - half, c - half + along), Vector3.Max(c + half, c + half + along));
                }
            }
            if (mesh == null)
            {
                mesh = new Mesh { name = "IsoRim_TargetBox" };
            }
            mesh.Clear();
            mesh.SetVertices(verts);
            colours.Clear();
            for (int i = 0; i < verts.Count; i++)
            {
                colours.Add(new Color32(255, 255, 255, 255));
            }
            mesh.SetColors(colours);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
        }

        // Corner k of a box -w/2..w/2 by 0..h by -d/2..d/2.
        private static Vector3 Corner(int k, float w, float h, float d)
        {
            return new Vector3((k & 1) == 0 ? -w * 0.5f : w * 0.5f, (k & 2) == 0 ? 0f : h, (k & 4) == 0 ? -d * 0.5f : d * 0.5f);
        }

        // A box between two opposite corners.
        private static void AddBar(Vector3 lo, Vector3 hi)
        {
            int n = verts.Count;
            for (int k = 0; k < 8; k++)
            {
                verts.Add(new Vector3((k & 1) == 0 ? lo.x : hi.x, (k & 2) == 0 ? lo.y : hi.y, (k & 4) == 0 ? lo.z : hi.z));
            }
            int[] faces = BarFaces;
            for (int i = 0; i < faces.Length; i++)
            {
                tris.Add(n + faces[i]);
            }
        }

        // The six faces of a box whose corner k is (x: bit 0, y: bit 1, z: bit 2), two triangles each (drawn both sides).
        private static readonly int[] BarFaces =
        {
            0, 1, 3, 0, 3, 2,   4, 6, 7, 4, 7, 5,   // z low, z high
            0, 2, 6, 0, 6, 4,   1, 5, 7, 1, 7, 3,   // x low, x high
            0, 4, 5, 0, 5, 1,   2, 3, 7, 2, 7, 6    // y low, y high
        };
    }
}
