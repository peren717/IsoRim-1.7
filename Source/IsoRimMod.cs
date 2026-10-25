using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    public class IsoRimSettings : ModSettings
    {
        public float pitch = 55f;        // camera angle in degrees; 90 looks straight down
        public bool isometric = true;    // camera turned 45 degrees and lowered, as in isometric games
        public float isoPitch = 35.3f;   // camera angle while isometric; 35.3 is the true isometric angle
        public float wallHeight = 1.2f;    // height of a full wall, in cells
        public float squash = 0.003f;    // how far the game's flat draw layers are pressed together
        public float feetOffset = 0.3f;  // where a pawn's feet sit, as a fraction of sprite height below centre
        public float saturation = 0.85f; // custom look: how much of the colour is kept (1 = untouched)
        public float brightness = 0.95f; // custom look: how much of the brightness is kept (1 = untouched)
        public float treeShadowLength = 0.2f;   // shadow height per cell of tree, on the game's scale where a wall is 1
        public float cutHeight = 0.5f;   // how much is left of a wall that is cut down to show the room behind
        public float dissolveRadius = 3.5f; // radius around the mouse cursor within which walls are cut down

        public bool uprightPawns = true;
        public bool uprightPlants = true;
        public float plantCardSize = 1f;   // plants drawn from their sprite (cards), other than trees: how big
        public bool fullPawnDraw = false;  // people never drawn from the game's cached image in 3D (on by itself with Facial Animation)
        public bool faceDecal = true;       // with Facial Animation: its face on 3D heads instead of IsoRim's eyes (FaceDecal)
        public float faceDecalSize = 1f;    // its size on the head
        public float faceDecalHeight = 0f;  // and how far it is moved up (cells)
        public float faceDecalMouth = 1.6f; // the mouth's own size (Facial Animation's is a thin line)
        public float faceDecalMouthHeight = 0f; // and how far the mouth alone is moved up (cells)
        public List<FaceAdjust> faceAdjusts = new List<FaceAdjust>();   // the player's own placing per apparel (overrides the above)

        public FaceAdjust FaceAdjustOf(string apparel)
        {
            for (int i = 0; i < faceAdjusts.Count; i++)
            {
                if (faceAdjusts[i].apparel == apparel)
                {
                    return faceAdjusts[i];
                }
            }
            return null;
        }
        public bool walls3D = true;
        public bool texturedWalls = true;
        public bool autoExtrude = true;
        public bool thickItems = true;
        public bool itemsFromArt = true;
        public bool rawModels = true;    // raw materials (steel, wood, blocks ...) keep the mod's 3D models even with itemsFromArt
        public bool ghosts3D = true;
        public bool cutRock = true;
        public bool mapEdge3D = true;
        public float itemThickness = 1f;
        public bool workAnimations = true;
        public bool damage3D = true;
        public bool outlines = true;
        public bool texturedModels = true;
        public bool ruggedRock = true;
        public bool trees3D = true;
        public bool smallPlants3D = true;
        public bool groundStyle = true;
        public bool grassSoil = true;
        public bool floorStyle = true;
        public bool waterStyle = true;   // the mod's own stylised water
        public bool cutHover = true;     // walls under the pointer are cut down
        public bool cutRooms = false;    // walls in front of every roofed room are cut down
        public bool cutWholeWall = false;   // stacked floors: a lowered wall is lowered on every shown floor of its column (Cutaway.AddColumns)
        public bool buildSeeThrough = true; // with a designator in hand, what is near the pointer is drawn see-through (BuildSee)
        public float seeStrength = 0.85f;   // BuildSee: share of pixels dropped at the pointer (before: fixed 0.55)
        public float seeRadius = 7f;        // BuildSee: cells to where it ends, full strength to 3 cells less (before: fixed 4.5)
        public bool mineXray = true;        // MineXray: with the Mine tool on an upper floor, the floor below outlined
        public bool mineCrosshair = false;   // MineXray: a crosshair along x and z from the pointer's cell
        public int turn = 0;             // eighth turns of the camera, clockwise
        public bool snap45 = true;       // Q and E turn an eighth (45 degrees) at a time; otherwise a quarter
        public bool instantTurn = false; // Q and E (and the free-look snap) jump straight to the new angle, with no swing
        public bool freeLook = true;     // the view can be turned and tilted by dragging with the mouse
        public float freeYaw = 0f;       // degrees turned by free look, on top of the eighth turns
        public float lookSensitivity = 0.25f;   // degrees per pixel dragged
        public bool invertY = false;     // dragging up tilts toward top-down instead of lower
        public bool perspective = false; // perspective camera (Rim3D-like) instead of orthographic
        public float fov = 40f;          // perspective: vertical field of view, degrees
        public float viewDistance = 160f; // perspective: how far past the screen centre the map is drawn, cells
        public bool thingLabels = true;  // labels on things lying on the map (stack counts, quality)
        public bool weather3D = false;   // rain and snow falling in 3D, fog and haze stood up before the camera (Weather); off: the game's flat sheets
        public float weatherSize = 1f;   // how big rain and snow look on screen
        public float minZoom = 4f;       // how close the camera can zoom in (the game stops at 11)
        public float labelFade = 1.4f;   // perspective: labels fade out past this many times the distance to the screen centre (4 = never)
        public float fpFov = 70f;        // first person: vertical field of view, degrees
        public float fpEyeHeight = 0.95f; // first person: eye height, cells (a full wall is wallHeight)
        public bool sky = true;          // perspective: a sky behind the map instead of black
        public int skyStyle = 0;         // perspective sky: 0 painted, 1 classic (Sky.StyleNames)
        public bool distanceFog = true;  // perspective: the far map fades into the sky's horizon
        public float fogStrength = 1f;   // 1 = solid at the view distance (hides the edge)
        public float fogStart = 0.35f;   // where between the screen centre and the view distance the fog starts
        public bool stars = true;        // sky at night: stars
        public bool milkyWay = true;     // sky at night: the Milky Way band
        public bool planets = true;      // sky at night: three planets (realistic points)
        public bool gasGiant = true;     // sky: a ringed gas giant (sci-fi), faint by day
        public float gasGiantSize = 1f;
        public float starBrightness = 1f;
        public bool moonPhases = true;   // the moon waxes and wanes over a quadrum
        public bool lowSky = true;       // sun, moon and gas giant drawn lower, where the map camera can see them
        public bool antiAlias = true;    // FXAA pass over the 3D view (the engine has no MSAA)
        public float antiAliasStrength = 1f;
        public bool sharpen = false;     // sharpening in the same pass (contrast-adaptive), also without FXAA
        public float sharpenStrength = 0.5f;
        public int aniso = 16;           // anisotropic filtering of the ground and the mod's textures: 0 (as the textures come), 2, 4, 8, 16 (Aniso)
        public bool tiltShift = false;   // tilt-shift (TiltShift): top and bottom of the 3D view blurred, a miniature look
        public float tiltStrength = 0.55f;   // 0..1, 1 = sigma TiltShift.MaxSigma px at 1080 lines
        public float tiltCentre = 0.5f;      // sharp band centre, fraction of the screen height from the top (0.3..0.7)
        public float tiltWidth = 0.3f;       // sharp band width, fraction of the screen height (0.1..0.6)
        public bool tiltPop = false;         // slightly richer colours with tilt-shift (TiltShift.PopAmount)
        public int lookMode = 0;         // free look drag button: 0 = middle button with R3D_LookModifier (Ctrl), 1 = right button with nothing selected
        public bool pawns3D = true;
        public bool hairUnderHats = false;   // hair shown, tucked in, under headgear that covers only the upper head (People.HairUnderHat)
        public List<string> keepPaper = new List<string>();   // races (ThingDef defNames) kept as cards although an alien pack makes them 3D (Api 25, AlienFigures)
        public bool effects3D = true;
        public bool collapseDebris = true;
        public bool groundShadows = true;
        public bool treeShadows = true;
        public bool grade = true;        // colour grade on
        public string lookName = "Gentle";
        public float lookStrength = 1f;
        public string lutFile = "";      // a LUT file picked by name instead of a look's own
        public bool animals3D = true;
        public bool mechs3D = true;
        public bool heldWeapons3D = true;
        public bool doors3D = true;
        public bool models3D = true;
        public bool lightingFix = true;
        public bool fogLid = true;
        public bool showRoofs = false;
        public int roofStyle = 0;        // slate, terracotta, thatch, tin, flat roof in steel, marble, granite (SectionLayer_IsoRimRoofs.StyleNames)
        public bool sunMoves = true;
        public bool cutFrontOnly = false;        // only walls between the camera and the pointer are lowered
        public bool cutNearColonists = false;    // walls round colonists are lowered too
        public float colonistRadius = 2.5f;
        public bool treesNearPointer = true;     // trees near the pointer drop their leaves so what is under them shows
        public bool treesNearColonists = false;
        public float treeRadius = 2.5f;
        public bool roofHover = true;            // the roof of the room under the pointer is lifted off
        public bool roofOverColonists = false;   // and of every room a colonist is in     // the sun crosses the sky through the day; otherwise it stays put

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref pitch, "pitch", 55f);
            Scribe_Values.Look(ref isometric, "isometric", true);
            Scribe_Values.Look(ref isoPitch, "isoPitch", 35.3f);
            Scribe_Values.Look(ref wallHeight, "wallHeight", 1.2f);
            Scribe_Values.Look(ref squash, "squash", 0.02f);
            Scribe_Values.Look(ref feetOffset, "feetOffset", 0.3f);
            Scribe_Values.Look(ref saturation, "saturation", 0.85f);
            Scribe_Values.Look(ref brightness, "brightness", 0.95f);
            Scribe_Values.Look(ref treeShadowLength, "treeShadowLength", 0.2f);
            Scribe_Values.Look(ref uprightPawns, "uprightPawns", true);
            Scribe_Values.Look(ref uprightPlants, "uprightPlants", true);
            Scribe_Values.Look(ref plantCardSize, "plantCardSize", 1f);
            Scribe_Values.Look(ref fullPawnDraw, "fullPawnDraw", false);
            Scribe_Values.Look(ref faceDecal, "faceDecal", true);
            Scribe_Values.Look(ref faceDecalSize, "faceDecalSize", 1f);
            Scribe_Values.Look(ref faceDecalHeight, "faceDecalHeight", 0f);
            Scribe_Values.Look(ref faceDecalMouth, "faceDecalMouth", 1.6f);
            Scribe_Values.Look(ref faceDecalMouthHeight, "faceDecalMouthHeight", 0f);
            Scribe_Collections.Look(ref faceAdjusts, "faceAdjusts", LookMode.Deep);
            if (faceAdjusts == null)
            {
                faceAdjusts = new List<FaceAdjust>();
            }
            Scribe_Values.Look(ref walls3D, "walls3D", true);
            Scribe_Values.Look(ref texturedWalls, "texturedWalls", true);
            Scribe_Values.Look(ref autoExtrude, "autoExtrude", true);
            Scribe_Values.Look(ref thickItems, "thickItems", true);
            Scribe_Values.Look(ref itemsFromArt, "itemsFromArt", true);
            Scribe_Values.Look(ref rawModels, "rawModels", true);
            Scribe_Values.Look(ref ghosts3D, "ghosts3D", true);
            Scribe_Values.Look(ref cutRock, "cutRock", true);
            Scribe_Values.Look(ref mapEdge3D, "mapEdge3D", true);
            Scribe_Values.Look(ref itemThickness, "itemThickness", 1f);
            Scribe_Values.Look(ref workAnimations, "workAnimations", true);
            Scribe_Values.Look(ref damage3D, "damage3D", true);
            Scribe_Values.Look(ref outlines, "outlines", true);
            Scribe_Values.Look(ref texturedModels, "texturedModels", true);
            Scribe_Values.Look(ref ruggedRock, "ruggedRock", true);
            Scribe_Values.Look(ref trees3D, "trees3D", true);
            Scribe_Values.Look(ref smallPlants3D, "smallPlants3D", true);
            Scribe_Values.Look(ref groundStyle, "groundStyle", true);
            Scribe_Values.Look(ref grassSoil, "grassSoil", true);
            Scribe_Values.Look(ref floorStyle, "floorStyle", true);
            Scribe_Values.Look(ref waterStyle, "waterStyle", true);
            Scribe_Values.Look(ref cutHover, "cutHover", true);
            Scribe_Values.Look(ref cutRooms, "cutRooms", false);
            Scribe_Values.Look(ref cutWholeWall, "cutWholeWall", false);
            Scribe_Values.Look(ref buildSeeThrough, "buildSeeThrough", true);
            Scribe_Values.Look(ref seeStrength, "seeStrength", 0.85f);
            Scribe_Values.Look(ref seeRadius, "seeRadius", 7f);
            Scribe_Values.Look(ref mineXray, "mineXray", true);
            Scribe_Values.Look(ref mineCrosshair, "mineCrosshair", false);
            Scribe_Values.Look(ref turn, "turnSteps", 0);
            Scribe_Values.Look(ref snap45, "snap45", true);
            Scribe_Values.Look(ref instantTurn, "instantTurn", false);
            Scribe_Values.Look(ref freeLook, "freeLook", true);
            Scribe_Values.Look(ref freeYaw, "freeYaw", 0f);
            Scribe_Values.Look(ref lookSensitivity, "lookSensitivity", 0.25f);
            Scribe_Values.Look(ref invertY, "invertY", false);
            Scribe_Values.Look(ref lookMode, "lookMode", 0);
            Scribe_Values.Look(ref perspective, "perspective", false);
            Scribe_Values.Look(ref fov, "fov", 40f);
            Scribe_Values.Look(ref viewDistance, "viewDistance", 160f);
            Scribe_Values.Look(ref thingLabels, "thingLabels", true);
            Scribe_Values.Look(ref weather3D, "weather3D", false);
            Scribe_Values.Look(ref minZoom, "minZoom", 4f);
            Scribe_Values.Look(ref weatherSize, "weatherSize", 1f);
            Scribe_Values.Look(ref labelFade, "labelFade", 1.4f);
            Scribe_Values.Look(ref fpFov, "fpFov", 70f);
            Scribe_Values.Look(ref sky, "sky", true);
            Scribe_Values.Look(ref skyStyle, "skyStyle", 0);
            Scribe_Values.Look(ref distanceFog, "distanceFog", true);
            Scribe_Values.Look(ref fogStrength, "fogStrength", 1f);
            Scribe_Values.Look(ref fogStart, "fogStart", 0.35f);
            Scribe_Values.Look(ref stars, "stars", true);
            Scribe_Values.Look(ref milkyWay, "milkyWay", true);
            Scribe_Values.Look(ref planets, "planets", true);
            Scribe_Values.Look(ref gasGiant, "gasGiant", true);
            Scribe_Values.Look(ref gasGiantSize, "gasGiantSize", 1f);
            Scribe_Values.Look(ref starBrightness, "starBrightness", 1f);
            Scribe_Values.Look(ref moonPhases, "moonPhases", true);
            Scribe_Values.Look(ref lowSky, "lowSky", true);
            Scribe_Values.Look(ref antiAlias, "antiAlias", true);
            Scribe_Values.Look(ref antiAliasStrength, "antiAliasStrength", 1f);
            Scribe_Values.Look(ref sharpen, "sharpen", false);
            Scribe_Values.Look(ref sharpenStrength, "sharpenStrength", 0.5f);
            Scribe_Values.Look(ref aniso, "aniso", 16);
            Scribe_Values.Look(ref tiltShift, "tiltShift", false);
            Scribe_Values.Look(ref tiltStrength, "tiltStrength", 0.55f);
            Scribe_Values.Look(ref tiltCentre, "tiltCentre", 0.5f);
            Scribe_Values.Look(ref tiltWidth, "tiltWidth", 0.3f);
            Scribe_Values.Look(ref tiltPop, "tiltPop", false);
            Scribe_Values.Look(ref fpEyeHeight, "fpEyeHeight", 0.95f);
            Scribe_Values.Look(ref pawns3D, "pawns3D", true);
            Scribe_Values.Look(ref hairUnderHats, "hairUnderHats", false);
            Scribe_Collections.Look(ref keepPaper, "keepPaper", LookMode.Value);
            if (keepPaper == null)
            {
                keepPaper = new List<string>();
            }
            Scribe_Values.Look(ref effects3D, "effects3D", true);
            Scribe_Values.Look(ref collapseDebris, "collapseDebris", true);
            Scribe_Values.Look(ref cutHeight, "cutHeight", 0.5f);
            Scribe_Values.Look(ref dissolveRadius, "dissolveRadius", 3.5f);
            Scribe_Values.Look(ref groundShadows, "groundShadows", true);
            Scribe_Values.Look(ref treeShadows, "treeShadows", true);
            Scribe_Values.Look(ref grade, "grade", true);
            Scribe_Values.Look(ref lookName, "lookName", "Gentle");
            Scribe_Values.Look(ref lookStrength, "lookStrength", 1f);
            Scribe_Values.Look(ref lutFile, "lutFile", "");
            Scribe_Values.Look(ref animals3D, "animals3D", true);
            Scribe_Values.Look(ref mechs3D, "mechs3D", true);
            Scribe_Values.Look(ref heldWeapons3D, "heldWeapons3D", true);
            Scribe_Values.Look(ref doors3D, "doors3D", true);
            Scribe_Values.Look(ref models3D, "models3D", true);
            Scribe_Values.Look(ref lightingFix, "lightingFix", true);
            Scribe_Values.Look(ref fogLid, "fogLid", true);
            Scribe_Values.Look(ref showRoofs, "showRoofs", false);
            Scribe_Values.Look(ref roofStyle, "roofStyle", 0);
            Scribe_Values.Look(ref sunMoves, "sunMoves", true);
            Scribe_Values.Look(ref cutFrontOnly, "cutFrontOnly", false);
            Scribe_Values.Look(ref cutNearColonists, "cutNearColonists", false);
            Scribe_Values.Look(ref colonistRadius, "colonistRadius", 2.5f);
            Scribe_Values.Look(ref treesNearPointer, "treesNearPointer", true);
            Scribe_Values.Look(ref treesNearColonists, "treesNearColonists", false);
            Scribe_Values.Look(ref treeRadius, "treeRadius", 2.5f);
            Scribe_Values.Look(ref roofHover, "roofHover", true);
            Scribe_Values.Look(ref roofOverColonists, "roofOverColonists", false);
            if (lookName == null)
            {
                lookName = "Gentle";
            }
            if (lutFile == null)
            {
                lutFile = "";
            }
        }

        // Puts every option back to its default.
        public void Reset()
        {
            IsoRimSettings fresh = new IsoRimSettings();
            foreach (FieldInfo f in typeof(IsoRimSettings).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                f.SetValue(this, f.GetValue(fresh));
            }
        }
    }

    public class IsoRimMod : Mod
    {
        public static IsoRimSettings Settings;
        public static string RootDir;   // this mod's folder
        private static IsoRimMod instance;

        public IsoRimMod(ModContentPack content) : base(content)
        {
            instance = this;
            Settings = GetSettings<IsoRimSettings>();
            RootDir = content.RootDir;
        }

        // Writes the options to disk now (they are also written when the options window closes).
        public static void Save()
        {
            if (instance != null)
            {
                instance.WriteSettings();
            }
        }

        public override string SettingsCategory()
        {
            return "IsoRim";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            SettingsWindow.Draw(inRect, Settings);
        }
    }

    // The options screen: a header with the main switch, then six tabs, each in two columns of
    // titled sections.
    public static class SettingsWindow
    {
        private static int tab;
        private static int scrollTab = -1;
        private static Vector2 scroll;
        private static float contentHeight;
        private static readonly Color Muted = new Color(0.72f, 0.72f, 0.72f);
        private static readonly Color Rule = new Color(1f, 1f, 1f, 0.18f);
        private static readonly Color Accent = new Color(0.55f, 0.78f, 0.95f);
        private static readonly Color[] Palette =
        {
            new Color(0.42f, 0.55f, 0.30f), new Color(0.55f, 0.42f, 0.30f), new Color(0.58f, 0.57f, 0.55f), new Color(0.30f, 0.50f, 0.60f),
            new Color(0.88f, 0.70f, 0.58f), new Color(0.86f, 0.88f, 0.92f), new Color(0.72f, 0.22f, 0.20f), new Color(0.18f, 0.17f, 0.16f),
        };

        public static void Draw(Rect inRect, IsoRimSettings st)
        {
            Text.Font = GameFont.Small;
            Rect header = new Rect(inRect.x, inRect.y, inRect.width, 52f);
            Header(header, st);

            Rect body = new Rect(inRect.x, header.yMax + 36f, inRect.width, inRect.height - header.height - 36f);
            List<TabRecord> tabs = new List<TabRecord>
            {
                new TabRecord("R3D_Settings_TabView".Translate(), delegate { tab = 0; }, tab == 0),
                new TabRecord("R3D_Settings_TabSeeThrough".Translate(), delegate { tab = 4; }, tab == 4),
                new TabRecord("R3D_Settings_TabColour".Translate(), delegate { tab = 1; }, tab == 1),
                new TabRecord("R3D_Settings_TabWorld".Translate(), delegate { tab = 2; }, tab == 2),
                new TabRecord("R3D_Settings_TabObjects".Translate(), delegate { tab = 3; }, tab == 3),
                new TabRecord("R3D_Settings_TabGraphics".Translate(), delegate { tab = 5; }, tab == 5),
            };
            Widgets.DrawMenuSection(body);
            TabDrawer.DrawTabs(body, tabs);

            // Both columns scroll together when a tab is taller than the window (the height is
            // measured from the last frame).
            Rect inner = body.ContractedBy(14f);
            if (scrollTab != tab)
            {
                scrollTab = tab;
                scroll = Vector2.zero;
            }
            bool scrolls = contentHeight > inner.height;
            Rect view = new Rect(0f, 0f, inner.width - (scrolls ? 18f : 0f), Mathf.Max(contentHeight, inner.height));
            Widgets.BeginScrollView(inner, ref scroll, view);
            float gap = 24f;
            float colW = (view.width - gap) / 2f;
            Rect left = new Rect(0f, 0f, colW, 4000f);
            Rect right = new Rect(colW + gap, 0f, colW, 4000f);
            GUI.color = Rule;
            Widgets.DrawLineVertical(colW + gap / 2f, 0f, view.height);
            GUI.color = Color.white;

            // One column at a time: a listing opens a drawing group, and two open at once would
            // nest, putting everything in the second outside the first and drawing nothing.
            Listing_Standard l = new Listing_Standard();
            l.Begin(left);
            Tab(l, null, st);
            float leftH = l.CurHeight;
            l.End();
            Listing_Standard r = new Listing_Standard();
            r.Begin(right);
            Tab(null, r, st);
            float rightH = r.CurHeight;
            r.End();
            Widgets.EndScrollView();
            contentHeight = Mathf.Max(leftH, rightH) + 8f;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        // ---------------------------------------------------------------- header

        private static void Header(Rect rect, IsoRimSettings st)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.10f, 0.12f, 0.14f, 0.65f));
            GUI.color = Accent;
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 4f, rect.height), Accent);
            GUI.color = Color.white;

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.Font = GameFont.Small;
            GUI.color = Muted;
            string status = (View3D.Active ? "R3D_Settings_ViewIsOn" : "R3D_Settings_ViewIsOff").Translate()
                + "   ·   " + "R3D_Settings_TurnStep".Translate(st.snap45 ? "45°" : "90°");
            Widgets.Label(new Rect(rect.x + 16f, rect.y, rect.width - 300f, rect.height), status);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            Rect toggle = new Rect(rect.xMax - 270f, rect.y + 11f, 150f, 30f);
            if (View3D.LockedOn)
            {
                Widgets.ButtonText(toggle, "R3D_Settings_TurnViewOff".Translate(), true, false, false);
                TooltipHandler.TipRegion(toggle, "R3D_Settings_AlwaysOnWith".Translate(View3D.LockedBy));
            }
            else if (Widgets.ButtonText(toggle, (View3D.Active ? "R3D_Settings_TurnViewOff" : "R3D_Settings_TurnViewOn").Translate()))
            {
                View3D.Active = !View3D.Active;
            }
            Rect reset = new Rect(rect.xMax - 110f, rect.y + 11f, 100f, 30f);
            if (Widgets.ButtonText(reset, "R3D_Settings_ResetAll".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("R3D_Settings_ResetAllConfirm".Translate(), delegate
                {
                    bool roofs = st.showRoofs;
                    st.Reset();
                    if (roofs != st.showRoofs)
                    {
                        st.showRoofs = roofs;
                        Roofs.Set(!roofs);
                    }
                }));
            }
        }

        // ---------------------------------------------------------------- building blocks

        private static void Section(Listing_Standard l, string title, string note = null)
        {
            if (l.CurHeight > 1f)
            {
                l.Gap(10f);
            }
            Text.Font = GameFont.Medium;
            Rect r = l.GetRect(30f);
            Widgets.Label(r, title);
            Text.Font = GameFont.Small;
            GUI.color = Rule;
            Widgets.DrawLineHorizontal(r.x, r.yMax - 1f, r.width);
            GUI.color = Color.white;
            if (note != null)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Muted;
                l.Label(note);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
            l.Gap(4f);
        }

        private static void Note(Listing_Standard l, string text)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = Muted;
            l.Label(text);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // Per apparel placings of Facial Animation's face: each override with its own sliders, one to add more.
        private static void FaceAdjustsUI(Listing_Standard l, IsoRimSettings st)
        {
            FaceAdjust remove = null;
            for (int i = 0; i < st.faceAdjusts.Count; i++)
            {
                FaceAdjust a = st.faceAdjusts[i];
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.apparel);
                Rect row = l.GetRect(24f);
                Widgets.Label(row.LeftPart(0.7f), "    " + (def != null ? (string)def.LabelCap : a.apparel));
                if (Widgets.ButtonText(row.RightPart(0.25f), "R3D_Settings_FaceDecalRemove".Translate()))
                {
                    remove = a;
                }
                a.size = Slider(l, "  " + "R3D_Settings_FaceDecalSize".Translate(), a.size, 0.5f, 1.5f, (a.size * 100f).ToString("0") + "%");
                a.height = Slider(l, "  " + "R3D_Settings_FaceDecalHeight".Translate(), a.height, -0.15f, 0.15f, a.height.ToString("+0.000;-0.000;0"));
                a.mouthSize = Slider(l, "  " + "R3D_Settings_FaceDecalMouth".Translate(), a.mouthSize, 0.5f, 3f, (a.mouthSize * 100f).ToString("0") + "%");
                a.mouthHeight = Slider(l, "  " + "R3D_Settings_FaceDecalMouthHeight".Translate(), a.mouthHeight, -0.1f, 0.1f, a.mouthHeight.ToString("+0.000;-0.000;0"));
            }
            if (remove != null)
            {
                st.faceAdjusts.Remove(remove);
            }
            Rect add = l.GetRect(28f);
            if (Widgets.ButtonText(add.LeftPart(0.6f), "R3D_Settings_FaceDecalAddApparel".Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (ThingDef def in FaceApparel())
                {
                    if (st.FaceAdjustOf(def.defName) != null)
                    {
                        continue;
                    }
                    ThingDef chosen = def;
                    options.Add(new FloatMenuOption(def.LabelCap, delegate
                    {
                        // Starts from what the face has now with it on: the apparel's own (XML) placing over the global sliders.
                        FaceAdjust a = new FaceAdjust { apparel = chosen.defName, size = st.faceDecalSize, height = st.faceDecalHeight,
                            mouthSize = st.faceDecalMouth, mouthHeight = st.faceDecalMouthHeight };
                        FaceDecalAdjust ext = chosen.GetModExtension<FaceDecalAdjust>();
                        if (ext != null)
                        {
                            ext.Apply(a);
                        }
                        st.faceAdjusts.Add(a);
                    }));
                }
                if (options.Count > 0)
                {
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            l.Gap(4f);
        }

        // Apparel that can sit on or around the face: worn on the head, eyes or mouth, or over everything (helmets, hoods).
        private static List<ThingDef> faceApparel;

        private static List<ThingDef> FaceApparel()
        {
            if (faceApparel == null)
            {
                faceApparel = new List<ThingDef>();
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    ApparelProperties ap = def.apparel;
                    if (ap == null)
                    {
                        continue;
                    }
                    bool face = ap.layers != null && (ap.layers.Contains(ApparelLayerDefOf.Overhead) || ap.layers.Contains(ApparelLayerDefOf.EyeCover));
                    if (!face && ap.bodyPartGroups != null)
                    {
                        foreach (BodyPartGroupDef g in ap.bodyPartGroups)
                        {
                            if (g == BodyPartGroupDefOf.FullHead || g == BodyPartGroupDefOf.UpperHead || g == BodyPartGroupDefOf.Eyes || g.defName == "Teeth" || g.defName == "Mouth")
                            {
                                face = true;
                                break;
                            }
                        }
                    }
                    if (face)
                    {
                        faceApparel.Add(def);
                    }
                }
                faceApparel.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.CurrentCultureIgnoreCase));
            }
            return faceApparel;
        }

        private static float Slider(Listing_Standard l, string label, float value, float min, float max, string shown, string tip = null)
        {
            Rect r = l.GetRect(22f);
            Widgets.Label(r.LeftPart(0.72f), label);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = Accent;
            Widgets.Label(r.RightPart(0.28f), shown);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (tip != null)
            {
                TooltipHandler.TipRegion(r, tip);
            }
            return l.Slider(value, min, max);
        }

        private static void Check(Listing_Standard l, string label, ref bool value, string tip = null)
        {
            l.CheckboxLabeled(label, ref value, tip);
        }

        // A checkbox an addon has fixed: greyed out, showing the fixed value, with the addon's tooltip.
        private static void LockedCheck(Listing_Standard l, string label, bool value, string tip)
        {
            Rect rect = l.GetRect(Text.CalcHeight(label, l.ColumnWidth));
            GUI.color = Muted;
            Widgets.CheckboxLabeled(rect, label, ref value, true);
            GUI.color = Color.white;
            string text = tip.NullOrEmpty() ? "R3D_Settings_FixedBy".Translate("another mod").ToString() : tip;
            TooltipHandler.TipRegion(rect, text);
            l.Gap(l.verticalSpacing);
            if (Probe.ShowLockTips)
            {
                // Harness shots only (no pointer to hover with): the tooltip drawn as the game draws one, under the option.
                Vector2 size = Text.CalcSize(text) + new Vector2(16f, 8f);
                Rect box = new Rect(rect.x + 24f, rect.yMax + 2f, size.x, size.y);
                Widgets.DrawAtlas(box, ActiveTip.TooltipBGAtlas);
                Widgets.Label(box.ContractedBy(4f), text);
                l.Gap(size.y + 4f);
            }
        }

        private static string KeyOf(KeyBindingDef key)
        {
            if (key == null || key.MainKey == KeyCode.None)
            {
                return "-";
            }
            return key.MainKeyLabel;
        }

        // The free look drag button as words (IsoRimSettings.lookMode: 0 middle, 1 right).
        private static string DragButtonLabel(IsoRimSettings st)
        {
            return (st.lookMode == 1 ? "R3D_Settings_RightButton" : "R3D_Settings_MiddleButton").Translate();
        }

        // A length in cells, as shown next to a slider ("2.5 cells").
        private static string Cells(float value, string format)
        {
            return "R3D_Settings_Cells".Translate(value.ToString(format));
        }

        // Free look's own keys: "hold Ctrl and drag with the middle button" (without the hold key if it is unbound), or
        // "drag with the right button while nothing is selected".
        private static string LookKeysLabel(IsoRimSettings st)
        {
            if (st.lookMode == 1)
            {
                return "R3D_Settings_LookKeysRight".Translate();
            }
            KeyBindingDef mod = R3DKeyDefOf.R3D_LookModifier;
            return mod == null || mod.MainKey == KeyCode.None
                ? "R3D_Settings_LookKeysDrag".Translate(DragButtonLabel(st))
                : "R3D_Settings_LookKeysHoldDrag".Translate(mod.MainKeyLabel, DragButtonLabel(st));
        }

        private static void KeyRow(Listing_Standard l, KeyBindingDef key, string what)
        {
            Rect r = l.GetRect(22f);
            GUI.color = Accent;
            Widgets.Label(r.LeftPart(0.22f), KeyOf(key));
            GUI.color = Color.white;
            Widgets.Label(r.RightPart(0.78f), what);
        }

        // ---------------------------------------------------------------- tabs

        // Draws the chosen tab's left column (l set) or right column (r set).
        private static void Tab(Listing_Standard l, Listing_Standard r, IsoRimSettings st)
        {
            switch (tab)
            {
                case 0:
                    ViewTab(l, r, st);
                    break;
                case 1:
                    ColourTab(l, r, st);
                    break;
                case 2:
                    WorldTab(l, r, st);
                    break;
                case 4:
                    SeeThroughTab(l, r, st);
                    break;
                case 5:
                    GraphicsTab(l, r, st);
                    break;
                default:
                    ObjectsTab(l, r, st);
                    break;
            }
        }

        private static void ViewTab(Listing_Standard l, Listing_Standard r, IsoRimSettings st)
        {
            if (l != null)
            {
                Section(l, "R3D_Settings_Camera".Translate(), "R3D_Settings_CameraNote".Translate());
                Check(l, "R3D_Settings_Isometric".Translate(), ref st.isometric, "R3D_Settings_IsometricTip".Translate());
                Check(l, "R3D_Settings_Snap45".Translate(), ref st.snap45, "R3D_Settings_Snap45Tip".Translate());
                Check(l, "R3D_Settings_InstantTurn".Translate(), ref st.instantTurn, "R3D_Settings_InstantTurnTip".Translate());
                Check(l, "R3D_Settings_FreeLook".Translate(), ref st.freeLook, "R3D_Settings_FreeLookTip".Translate());
                if (st.freeLook)
                {
                    // The drag button (a mouse button: the game's keyboard options can't take one). The hold key is there.
                    KeyBindingDef hold = R3DKeyDefOf.R3D_LookModifier;
                    string middleTip = hold == null || hold.MainKey == KeyCode.None
                        ? "R3D_Settings_LookMiddleTipNoKey".Translate()
                        : "R3D_Settings_LookMiddleTip".Translate(hold.MainKeyLabel);
                    if (l.RadioButton("R3D_Settings_LookMiddle".Translate(), st.lookMode != 1, 12f, middleTip))
                    {
                        st.lookMode = 0;
                    }
                    if (l.RadioButton("R3D_Settings_LookRight".Translate(), st.lookMode == 1, 12f, "R3D_Settings_LookRightTip".Translate()))
                    {
                        st.lookMode = 1;
                    }
                    st.lookSensitivity = Slider(l, "R3D_Settings_LookSpeed".Translate(), st.lookSensitivity, 0.05f, 1f, "R3D_Settings_DegPerPixel".Translate(st.lookSensitivity.ToString("0.00")));
                    Check(l, "R3D_Settings_InvertY".Translate(), ref st.invertY, "R3D_Settings_InvertYTip".Translate());
                }
                st.fpFov = Slider(l, "R3D_Settings_FpFov".Translate(), st.fpFov, 40f, 100f, st.fpFov.ToString("0") + "°",
                    "R3D_Settings_FpFovTip".Translate(R3DKeyDefOf.R3D_FirstPerson != null ? R3DKeyDefOf.R3D_FirstPerson.MainKeyLabel : "F5", DragButtonLabel(st)));
                st.fpEyeHeight = Slider(l, "R3D_Settings_FpEyeHeight".Translate(), st.fpEyeHeight, 0.5f, 2f, Cells(st.fpEyeHeight, "0.00"));
                st.minZoom = Slider(l, "R3D_Settings_MinZoom".Translate(), st.minZoom, 2f, 11f, st.minZoom.ToString("0.0"), "R3D_Settings_MinZoomTip".Translate());
                Check(l, "R3D_Settings_Weather3D".Translate(), ref st.weather3D, "R3D_Settings_Weather3DTip".Translate());
                if (st.weather3D)
                {
                    st.weatherSize = Slider(l, "R3D_Settings_WeatherSize".Translate(), st.weatherSize, 0.25f, 3f, "×" + st.weatherSize.ToString("0.00"), "R3D_Settings_WeatherSizeTip".Translate());
                }
                Check(l, "R3D_Settings_ThingLabels".Translate(), ref st.thingLabels, "R3D_Settings_ThingLabelsTip".Translate());
                Check(l, "R3D_Settings_Perspective".Translate(), ref st.perspective, "R3D_Settings_PerspectiveTip".Translate());
                if (st.perspective)
                {
                    st.fov = Slider(l, "R3D_Settings_Fov".Translate(), st.fov, 15f, 90f, st.fov.ToString("0") + "°", "R3D_Settings_FovTip".Translate());
                    st.viewDistance = Slider(l, "R3D_Settings_ViewDistance".Translate(), st.viewDistance, 40f, 400f, Cells(st.viewDistance, "0"),
                        "R3D_Settings_ViewDistanceTip".Translate());
                    Check(l, "R3D_Settings_Sky".Translate(), ref st.sky, "R3D_Settings_SkyTip".Translate());
                    if (st.sky)
                    {
                        Rect sb = l.GetRect(28f);
                        if (Widgets.ButtonText(sb, "R3D_Settings_SkyStyle".Translate(SkyStyleLabel(st.skyStyle))))
                        {
                            List<FloatMenuOption> options = new List<FloatMenuOption>();
                            for (int i = 0; i < Sky.StyleNames.Length; i++)
                            {
                                int chosen = i;
                                options.Add(new FloatMenuOption(SkyStyleLabel(i), delegate { st.skyStyle = chosen; }));
                            }
                            Find.WindowStack.Add(new FloatMenu(options));
                        }
                    }
                    Check(l, "R3D_Settings_DistanceFog".Translate(), ref st.distanceFog, "R3D_Settings_DistanceFogTip".Translate());
                    if (st.distanceFog)
                    {
                        st.fogStart = Slider(l, "R3D_Settings_FogStart".Translate(), st.fogStart, 0f, 0.95f, (st.fogStart * 100f).ToString("0") + "%", "R3D_Settings_FogStartTip".Translate());
                        st.fogStrength = Slider(l, "R3D_Settings_FogStrength".Translate(), st.fogStrength, 0.2f, 1f, (st.fogStrength * 100f).ToString("0") + "%", "R3D_Settings_FogStrengthTip".Translate());
                    }
                    st.labelFade = Slider(l, "R3D_Settings_LabelFade".Translate(), st.labelFade, 1f, 4f, st.labelFade >= 4f ? "R3D_Settings_Never".Translate().ToString() : "×" + st.labelFade.ToString("0.0"),
                        "R3D_Settings_LabelFadeTip".Translate());
                }
                if (st.isometric)
                {
                    st.isoPitch = Slider(l, "R3D_Settings_IsoPitch".Translate(), st.isoPitch, View3D.MinPitch, View3D.MaxPitch, st.isoPitch.ToString("0") + "°",
                        "R3D_Settings_IsoPitchTip".Translate());
                }
                else
                {
                    st.pitch = Slider(l, "R3D_Settings_Pitch".Translate(), st.pitch, View3D.MinPitch, View3D.MaxPitch, st.pitch.ToString("0") + "°", "R3D_Settings_PitchTip".Translate());
                }
                if (View3D.LockedWallHeight > 0f)
                {
                    // Fixed by an addon (ProjectZ): greyed out, with who fixed it.
                    GUI.color = Muted;
                    Slider(l, "R3D_Settings_WallHeight".Translate(), View3D.LockedWallHeight, 0.5f, 4f, Cells(View3D.LockedWallHeight, "0.00"));
                    GUI.color = Color.white;
                    Note(l, "R3D_Settings_FixedBy".Translate(View3D.LockedBy));
                }
                else
                {
                    st.wallHeight = Slider(l, "R3D_Settings_WallHeight".Translate(), st.wallHeight, 0.5f, 4f, Cells(st.wallHeight, "0.0"));
                }

            }
            if (r != null)
            {
                Section(r, "R3D_Settings_Keys".Translate(), "R3D_Settings_KeysNote".Translate());
                KeyRow(r, R3DKeyDefOf.R3D_ToggleView, "R3D_Settings_KeyToggleView".Translate());
                KeyRow(r, R3DKeyDefOf.R3D_Isometric, "R3D_Settings_KeyIsometric".Translate());
                KeyRow(r, R3DKeyDefOf.R3D_TurnLeft, "R3D_Settings_KeyTurnLeft".Translate());
                KeyRow(r, R3DKeyDefOf.R3D_TurnRight, "R3D_Settings_KeyTurnRight".Translate());
                KeyRow(r, R3DKeyDefOf.R3D_TiltDown, "R3D_Settings_KeyTiltDown".Translate());
                KeyRow(r, R3DKeyDefOf.R3D_TiltUp, "R3D_Settings_KeyTiltUp".Translate());
                KeyRow(r, R3DKeyDefOf.R3D_ToggleRoofs, "R3D_Settings_KeyToggleRoofs".Translate());
                if (st.freeLook)
                {
                    Note(r, "R3D_Settings_FreeLookKeys".Translate(LookKeysLabel(st)));
                }
                Note(r, "R3D_Settings_CornerButtonsNote".Translate());

                if (st.perspective && st.sky)
                {
                    Section(r, "R3D_Settings_NightSky".Translate(), "R3D_Settings_NightSkyNote".Translate());
                    Check(r, "R3D_Settings_Stars".Translate(), ref st.stars, "R3D_Settings_StarsTip".Translate());
                    Check(r, "R3D_Settings_MilkyWay".Translate(), ref st.milkyWay, "R3D_Settings_MilkyWayTip".Translate());
                    Check(r, "R3D_Settings_Planets".Translate(), ref st.planets, "R3D_Settings_PlanetsTip".Translate());
                    Check(r, "R3D_Settings_GasGiant".Translate(), ref st.gasGiant, "R3D_Settings_GasGiantTip".Translate());
                    if (st.gasGiant)
                    {
                        st.gasGiantSize = Slider(r, "R3D_Settings_GasGiantSize".Translate(), st.gasGiantSize, 0.4f, 2.5f, "×" + st.gasGiantSize.ToString("0.0"));
                    }
                    Check(r, "R3D_Settings_LowSky".Translate(), ref st.lowSky, "R3D_Settings_LowSkyTip".Translate());
                    Check(r, "R3D_Settings_MoonPhases".Translate(), ref st.moonPhases, "R3D_Settings_MoonPhasesTip".Translate());
                    st.starBrightness = Slider(r, "R3D_Settings_StarBrightness".Translate(), st.starBrightness, 0.2f, 2f, (st.starBrightness * 100f).ToString("0") + "%", "R3D_Settings_StarBrightnessTip".Translate());
                }

                Section(r, "R3D_Settings_Advanced".Translate());
                st.feetOffset = Slider(r, "R3D_Settings_FeetOffset".Translate(), st.feetOffset, 0f, 0.5f, st.feetOffset.ToString("0.00"),
                    "R3D_Settings_FeetOffsetTip".Translate());
                st.squash = Slider(r, "R3D_Settings_Squash".Translate(), st.squash, 0.002f, 0.004f, st.squash.ToString("0.0000"),
                    "R3D_Settings_SquashTip".Translate());
            }
        }

        private static void GraphicsTab(Listing_Standard l, Listing_Standard r, IsoRimSettings st)
        {
            if (l != null)
            {
                Section(l, "R3D_Settings_Image".Translate());
                Check(l, "R3D_Settings_AntiAlias".Translate(), ref st.antiAlias, "R3D_Settings_AntiAliasTip".Translate());
                if (st.antiAlias)
                {
                    st.antiAliasStrength = Slider(l, "R3D_Settings_AntiAliasStrength".Translate(), st.antiAliasStrength, 0.2f, 1f, (st.antiAliasStrength * 100f).ToString("0") + "%");
                }
                Check(l, "R3D_Settings_Sharpen".Translate(), ref st.sharpen, "R3D_Settings_SharpenTip".Translate());
                if (st.sharpen)
                {
                    st.sharpenStrength = Slider(l, "R3D_Settings_SharpenStrength".Translate(), st.sharpenStrength, 0f, 1f, (st.sharpenStrength * 100f).ToString("0") + "%");
                }
                Check(l, "R3D_Settings_TiltShift".Translate(), ref st.tiltShift, "R3D_Settings_TiltShiftTip".Translate());
                if (st.tiltShift)
                {
                    st.tiltStrength = Slider(l, "R3D_Settings_TiltStrength".Translate(), st.tiltStrength, 0f, 1f, (st.tiltStrength * 100f).ToString("0") + "%");
                    st.tiltCentre = Slider(l, "R3D_Settings_TiltCentre".Translate(), st.tiltCentre, 0.3f, 0.7f, (st.tiltCentre * 100f).ToString("0") + "%");
                    st.tiltWidth = Slider(l, "R3D_Settings_TiltWidth".Translate(), st.tiltWidth, 0.1f, 0.6f, (st.tiltWidth * 100f).ToString("0") + "%");
                    Check(l, "R3D_Settings_TiltPop".Translate(), ref st.tiltPop, "R3D_Settings_TiltPopTip".Translate());
                }
            }
            if (r != null)
            {
                Section(r, "R3D_Settings_Textures".Translate());
                // Anisotropic filtering in steps: off, 2x, 4x, 8x, 16x (Aniso.Levels).
                int step = Mathf.Max(0, Array.IndexOf(Aniso.Levels, st.aniso));
                string shown = step == 0 ? "R3D_Settings_Off".Translate().ToString() : Aniso.Levels[step] + "x";
                int picked = Mathf.Clamp(Mathf.RoundToInt(Slider(r, "R3D_Settings_Aniso".Translate(), step, 0f, Aniso.Levels.Length - 1, shown, "R3D_Settings_AnisoTip".Translate())), 0, Aniso.Levels.Length - 1);
                if (picked != step)
                {
                    st.aniso = Aniso.Levels[picked];
                }
            }
        }

        private static void SeeThroughTab(Listing_Standard l, Listing_Standard r, IsoRimSettings st)
        {
            if (l != null)
            {
                Section(l, "R3D_Settings_Walls".Translate(), "R3D_Settings_WallsNote".Translate());
                Check(l, "R3D_Settings_CutHover".Translate(), ref st.cutHover);
                if (st.cutHover)
                {
                    st.dissolveRadius = Slider(l, "R3D_Settings_DissolveRadius".Translate(), st.dissolveRadius, 1.5f, 8f, Cells(st.dissolveRadius, "0.0"));
                    Check(l, "R3D_Settings_CutFrontOnly".Translate(), ref st.cutFrontOnly, "R3D_Settings_CutFrontOnlyTip".Translate());
                }
                Check(l, "R3D_Settings_CutWholeWall".Translate(), ref st.cutWholeWall, "R3D_Settings_CutWholeWallTip".Translate());
                Check(l, "R3D_Settings_BuildSee".Translate(), ref st.buildSeeThrough, "R3D_Settings_BuildSeeTip".Translate());
                if (st.buildSeeThrough)
                {
                    st.seeStrength = Slider(l, "R3D_Settings_SeeStrength".Translate(), st.seeStrength, 0.2f, 1f, (st.seeStrength * 100f).ToString("0") + "%", "R3D_Settings_SeeStrengthTip".Translate());
                    st.seeRadius = Slider(l, "R3D_Settings_SeeRadius".Translate(), st.seeRadius, 3f, 12f, Cells(st.seeRadius, "0.0"));
                }
                Check(l, "R3D_Settings_MineXray".Translate(), ref st.mineXray, "R3D_Settings_MineXrayTip".Translate());
                Check(l, "R3D_Settings_MineCrosshair".Translate(), ref st.mineCrosshair, "R3D_Settings_MineCrosshairTip".Translate());
                Check(l, "R3D_Settings_CutNearColonists".Translate(), ref st.cutNearColonists, "R3D_Settings_CutNearColonistsTip".Translate());
                if (st.cutNearColonists)
                {
                    st.colonistRadius = Slider(l, "R3D_Settings_ColonistRadius".Translate(), st.colonistRadius, 1f, 5f, Cells(st.colonistRadius, "0.0"));
                }
                Check(l, "R3D_Settings_CutRooms".Translate(), ref st.cutRooms, "R3D_Settings_CutRoomsTip".Translate());
                Check(l, "R3D_Settings_CutRock".Translate(), ref st.cutRock, "R3D_Settings_CutRockTip".Translate());
                st.cutHeight = Slider(l, "R3D_Settings_CutHeight".Translate(), st.cutHeight, 0.05f, 0.9f, (st.cutHeight * 100f).ToString("0") + "%");
            }
            if (r != null)
            {
                Section(r, "R3D_Settings_Trees".Translate(), "R3D_Settings_TreesNote".Translate());
                Check(r, "R3D_Settings_TreesNearPointer".Translate(), ref st.treesNearPointer);
                Check(r, "R3D_Settings_TreesNearColonists".Translate(), ref st.treesNearColonists);
                if (st.treesNearPointer || st.treesNearColonists)
                {
                    st.treeRadius = Slider(r, "R3D_Settings_TreeRadius".Translate(), st.treeRadius, 1f, 6f, Cells(st.treeRadius, "0.0"));
                }

                Section(r, "R3D_Settings_Roofs".Translate(), "R3D_Settings_SeeThroughRoofsNote".Translate());
                Check(r, "R3D_Settings_RoofHover".Translate(), ref st.roofHover);
                Check(r, "R3D_Settings_RoofOverColonists".Translate(), ref st.roofOverColonists);
            }
        }

        private static void ColourTab(Listing_Standard l, Listing_Standard r, IsoRimSettings st)
        {
            if (l != null)
            {
                Section(l, "R3D_Settings_ColourLook".Translate(), "R3D_Settings_ColourLookNote".Translate());
                Check(l, "R3D_Settings_Grade".Translate(), ref st.grade);
                if (st.grade)
                {
                    foreach (LookPreset p in Looks.Presets)
                    {
                        if (!st.lutFile.NullOrEmpty() && p.lutFile == st.lutFile)
                        {
                            // a file picked by name before a look used it: show it as that look (same picture)
                            st.lookName = p.name;
                            st.lutFile = "";
                        }
                    }
                    LookPreset look = Looks.Find(st.lookName);
                    Grade.Table table = st.lutFile.NullOrEmpty() ? null : Grade.TableFor(st.lutFile);
                    Grade.Table own = table == null && !look.lutFile.NullOrEmpty() ? Grade.TableFor(look.lutFile) : null;
                    Rect b = l.GetRect(30f);
                    if (Widgets.ButtonText(b, "R3D_Settings_LookButton".Translate(table != null ? st.lutFile + " (LUT)" : LookLabel(look))))
                    {
                        List<FloatMenuOption> options = new List<FloatMenuOption>();
                        foreach (LookPreset p in Looks.Presets)
                        {
                            LookPreset chosen = p;
                            options.Add(new FloatMenuOption(LookLabel(p), delegate
                            {
                                st.lookName = chosen.name;
                                st.lutFile = "";
                            }));
                        }
                        Grade.Rescan();
                        foreach (string f in Grade.Files)
                        {
                            if (Grade.UsedByLook(f))
                            {
                                continue;
                            }
                            string chosen = f;
                            options.Add(new FloatMenuOption(f + " (LUT)", delegate { st.lutFile = chosen; }));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                    if (table != null)
                    {
                        Note(l, "R3D_Settings_LutNote".Translate());
                        Preview(l, c => Grade.ApplyFile(table, st.lookStrength, c));
                        st.lookStrength = Slider(l, "R3D_Settings_Strength".Translate(), st.lookStrength, 0f, 1f, (st.lookStrength * 100f).ToString("0") + "%");
                    }
                    else
                    {
                        Note(l, LookDescription(look));
                        LookPreset current = Looks.Current();
                        if (own != null)
                        {
                            Preview(l, c => Grade.ApplyFile(own, st.lookStrength, c));
                        }
                        else
                        {
                            Preview(l, c => Looks.Apply(current, c));
                        }
                        if (look.name == Looks.CustomName)
                        {
                            st.saturation = Slider(l, "R3D_Settings_ColourKept".Translate(), st.saturation, 0.5f, 1f, (st.saturation * 100f).ToString("0") + "%");
                            st.brightness = Slider(l, "R3D_Settings_Brightness".Translate(), st.brightness, 0.6f, 1f, (st.brightness * 100f).ToString("0") + "%");
                        }
                        else
                        {
                            st.lookStrength = Slider(l, "R3D_Settings_Strength".Translate(), st.lookStrength, 0f, 1f, (st.lookStrength * 100f).ToString("0") + "%");
                        }
                    }
                }
            }
            if (r != null)
            {
                Section(r, "R3D_Settings_Shadows".Translate());
                Check(r, "R3D_Settings_GroundShadows".Translate(), ref st.groundShadows);
                Check(r, "R3D_Settings_TreeShadows".Translate(), ref st.treeShadows);
                if (st.treeShadows)
                {
                    st.treeShadowLength = Slider(r, "R3D_Settings_TreeShadowLength".Translate(), st.treeShadowLength, 0.05f, 0.5f, st.treeShadowLength.ToString("0.00"));
                }

                Section(r, "R3D_Settings_Light".Translate());
                Check(r, "R3D_Settings_LightingFix".Translate(), ref st.lightingFix);
                if (View3D.FogLidLocked)
                {
                    LockedCheck(r, "R3D_Settings_FogLid".Translate(), View3D.LockedFogLid, View3D.FogLidLockTip);   // set by an addon (Api.LockFogLid)
                }
                else
                {
                    Check(r, "R3D_Settings_FogLid".Translate(), ref st.fogLid);
                }
                Check(r, "R3D_Settings_SunMoves".Translate(), ref st.sunMoves, "R3D_Settings_SunMovesTip".Translate());
            }
        }

        private static void WorldTab(Listing_Standard l, Listing_Standard r, IsoRimSettings st)
        {
            if (l != null)
            {
                Section(l, "R3D_Settings_Ground".Translate(), "R3D_Settings_GroundNote".Translate());
                Check(l, "R3D_Settings_GroundStyle".Translate(), ref st.groundStyle);
                if (st.groundStyle)
                {
                    Check(l, "R3D_Settings_GrassSoil".Translate(), ref st.grassSoil, "R3D_Settings_GrassSoilTip".Translate());
                }
                Check(l, "R3D_Settings_FloorStyle".Translate(), ref st.floorStyle);

                Section(l, "R3D_Settings_Water".Translate());
                Check(l, "R3D_Settings_WaterStyle".Translate(), ref st.waterStyle, "R3D_Settings_WaterStyleTip".Translate());
            }
            if (r != null)
            {
                Section(r, "R3D_Settings_Roofs".Translate(), "R3D_Settings_RoofsNote".Translate());
                bool roofs = st.showRoofs;
                Check(r, "R3D_Settings_ShowRoofs".Translate(), ref roofs, "R3D_Settings_ShowRoofsTip".Translate());
                if (roofs != st.showRoofs)
                {
                    Roofs.Set(roofs);
                }
                Rect rb = r.GetRect(28f);
                if (Widgets.ButtonText(rb, "R3D_Settings_RoofColour".Translate(RoofStyleLabel(SectionLayer_IsoRimRoofs.Style))))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    for (int i = 0; i < SectionLayer_IsoRimRoofs.StyleNames.Length; i++)
                    {
                        int chosen = i;
                        options.Add(new FloatMenuOption(RoofStyleLabel(i), delegate
                        {
                            st.roofStyle = chosen;
                            Roofs.Rebuild();
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                Note(r, "R3D_Settings_RoofShapeNote".Translate());
            }
        }

        private static void ObjectsTab(Listing_Standard l, Listing_Standard r, IsoRimSettings st)
        {
            if (l != null)
            {
                Section(l, "R3D_Settings_BuildingsNature".Translate(), "R3D_Settings_BuildingsNatureNote".Translate());
                Check(l, "R3D_Settings_Walls3D".Translate(), ref st.walls3D);
                Check(l, "R3D_Settings_TexturedWalls".Translate(), ref st.texturedWalls);
                Check(l, "R3D_Settings_RuggedRock".Translate(), ref st.ruggedRock);
                Check(l, "R3D_Settings_Doors3D".Translate(), ref st.doors3D);
                Check(l, "R3D_Settings_Models3D".Translate(), ref st.models3D, "R3D_Settings_Models3DTip".Translate());
                Check(l, "R3D_Settings_Trees3D".Translate(), ref st.trees3D);
                Check(l, "R3D_Settings_SmallPlants3D".Translate(), ref st.smallPlants3D, "R3D_Settings_SmallPlants3DTip".Translate());
                Check(l, "R3D_Settings_AutoExtrude".Translate(), ref st.autoExtrude);
                Check(l, "R3D_Settings_ThickItems".Translate(), ref st.thickItems);
                if (st.thickItems)
                {
                    st.itemThickness = Slider(l, "R3D_Settings_ItemThickness".Translate(), st.itemThickness, 0f, 1f,
                        st.itemThickness <= 0.02f ? "R3D_Settings_Flat".Translate().ToString() : (st.itemThickness * 100f).ToString("0") + "%",
                        "R3D_Settings_ItemThicknessTip".Translate());
                }
                Check(l, "R3D_Settings_ItemsFromArt".Translate(), ref st.itemsFromArt,
                    "R3D_Settings_ItemsFromArtTip".Translate());
                Check(l, "R3D_Settings_RawModels".Translate(), ref st.rawModels, "R3D_Settings_RawModelsTip".Translate());
                Check(l, "R3D_Settings_UprightPlants".Translate(), ref st.uprightPlants);
                if (st.uprightPlants)
                {
                    st.plantCardSize = Slider(l, "R3D_Settings_PlantCardSize".Translate(), st.plantCardSize, 0.3f, 1.5f,
                        (st.plantCardSize * 100f).ToString("0") + "%", "R3D_Settings_PlantCardSizeTip".Translate());
                }
                Check(l, "R3D_Settings_MapEdge3D".Translate(), ref st.mapEdge3D, "R3D_Settings_MapEdge3DTip".Translate());
            }
            if (r != null)
            {
                Section(r, "R3D_Settings_CreaturesEffects".Translate());
                Check(r, "R3D_Settings_Pawns3D".Translate(), ref st.pawns3D);
                if (st.pawns3D)
                {
                    Check(r, "R3D_Settings_HairUnderHats".Translate(), ref st.hairUnderHats, "R3D_Settings_HairUnderHatsTip".Translate());
                    AliensUI.KeepAsPaper(r, st);   // (Api 25: only with other humanlike races loaded)
                }
                if (st.pawns3D && ModsConfig.IsActive("Nals.FacialAnimation"))
                {
                    Check(r, "R3D_Settings_FaceDecal".Translate(), ref st.faceDecal, "R3D_Settings_FaceDecalTip".Translate());
                    if (st.faceDecal)
                    {
                        st.faceDecalSize = Slider(r, "R3D_Settings_FaceDecalSize".Translate(), st.faceDecalSize, 0.5f, 1.5f, (st.faceDecalSize * 100f).ToString("0") + "%");
                        st.faceDecalHeight = Slider(r, "R3D_Settings_FaceDecalHeight".Translate(), st.faceDecalHeight, -0.15f, 0.15f, st.faceDecalHeight.ToString("+0.000;-0.000;0"));
                        st.faceDecalMouth = Slider(r, "R3D_Settings_FaceDecalMouth".Translate(), st.faceDecalMouth, 0.5f, 3f, (st.faceDecalMouth * 100f).ToString("0") + "%");
                        st.faceDecalMouthHeight = Slider(r, "R3D_Settings_FaceDecalMouthHeight".Translate(), st.faceDecalMouthHeight, -0.1f, 0.1f, st.faceDecalMouthHeight.ToString("+0.000;-0.000;0"));
                        FaceAdjustsUI(r, st);
                    }
                }
                Check(r, "R3D_Settings_Animals3D".Translate(), ref st.animals3D);
                Check(r, "R3D_Settings_Mechs3D".Translate(), ref st.mechs3D);
                Check(r, "R3D_Settings_HeldWeapons3D".Translate(), ref st.heldWeapons3D);
                Check(r, "R3D_Settings_WorkAnimations".Translate(), ref st.workAnimations, "R3D_Settings_WorkAnimationsTip".Translate());
                Check(r, "R3D_Settings_Ghosts3D".Translate(), ref st.ghosts3D, "R3D_Settings_Ghosts3DTip".Translate());
                Check(r, "R3D_Settings_Damage3D".Translate(), ref st.damage3D);
                Check(r, "R3D_Settings_Effects3D".Translate(), ref st.effects3D);
                Check(r, "R3D_Settings_CollapseDebris".Translate(), ref st.collapseDebris, "R3D_Settings_CollapseDebrisTip".Translate());
                Check(r, "R3D_Settings_UprightPawns".Translate(), ref st.uprightPawns, "R3D_Settings_UprightPawnsTip".Translate());
                if (st.uprightPawns && ModsConfig.IsActive("Nals.FacialAnimation"))
                {
                    Check(r, "R3D_Settings_FullPawnDraw".Translate(), ref st.fullPawnDraw, "R3D_Settings_FullPawnDrawTip".Translate());
                }

                Section(r, "R3D_Settings_ModelStyle".Translate());
                Check(r, "R3D_Settings_TexturedModels".Translate(), ref st.texturedModels);
                Check(r, "R3D_Settings_Outlines".Translate(), ref st.outlines);
            }
        }

        // A colour look's name and description in the game's language (R3D_Settings_Look_<name>, ..._Desc);
        // the English ones in Looks.cs if there is no key.
        private static string LookLabel(LookPreset p)
        {
            string key = "R3D_Settings_Look_" + p.name;
            return key.CanTranslate() ? key.Translate().ToString() : p.label;
        }

        private static string LookDescription(LookPreset p)
        {
            string key = "R3D_Settings_Look_" + p.name + "_Desc";
            return key.CanTranslate() ? key.Translate().ToString() : p.description;
        }

        // A roof style's name in the game's language (R3D_Settings_Roof_<StyleName>).
        private static string SkyStyleLabel(int style)
        {
            string name = Sky.StyleNames[Mathf.Clamp(style, 0, Sky.StyleNames.Length - 1)];
            return ("R3D_Settings_Sky_" + name).Translate();
        }

        private static string RoofStyleLabel(int style)
        {
            string name = SectionLayer_IsoRimRoofs.StyleNames[style];
            string key = "R3D_Settings_Roof_" + name;
            return key.CanTranslate() ? key.Translate().ToString() : name;
        }

        // Two rows of colour chips: as they are, and under the look.
        private static void Preview(Listing_Standard l, Func<Color, Color> grade)
        {
            Rect r = l.GetRect(34f);
            float w = r.width / Palette.Length;
            for (int i = 0; i < Palette.Length; i++)
            {
                Rect top = new Rect(r.x + i * w, r.y, w - 2f, 15f);
                Rect bottom = new Rect(r.x + i * w, r.y + 17f, w - 2f, 15f);
                Widgets.DrawBoxSolid(top, Palette[i]);
                Widgets.DrawBoxSolid(bottom, grade(Palette[i]));
            }
            TooltipHandler.TipRegion(r, "R3D_Settings_PreviewTip".Translate());
            l.Gap(2f);
        }
    }

    [DefOf]
    public static class R3DKeyDefOf
    {
        public static KeyBindingDef R3D_ToggleView;
        public static KeyBindingDef R3D_Isometric;
        public static KeyBindingDef R3D_TurnLeft;
        public static KeyBindingDef R3D_TurnRight;
        public static KeyBindingDef R3D_TiltUp;
        public static KeyBindingDef R3D_TiltDown;
        public static KeyBindingDef R3D_ToggleRoofs;
        public static KeyBindingDef R3D_ResetCamera;
        public static KeyBindingDef R3D_LookModifier;
        public static KeyBindingDef R3D_FirstPerson;

        static R3DKeyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(R3DKeyDefOf));
        }
    }

    // The mod's buttons for the bottom right corner. Loaded on the main thread at startup.
    [StaticConstructorOnStartup]
    public static class R3DIcons
    {
        public static readonly Texture2D View3D = ContentFinder<Texture2D>.Get("IsoRim/UI/View3D", false) ?? BaseContent.BadTex;
        public static readonly Texture2D Roofs = ContentFinder<Texture2D>.Get("IsoRim/UI/Roofs", false) ?? BaseContent.BadTex;
    }

    // Runs once on the main thread after all defs are loaded.
    [StaticConstructorOnStartup]
    public static class IsoRimStartup
    {
        static IsoRimStartup()
        {
            ModLog.Note("[IsoRim] Code loaded.");

            // 1.7 port testing: "-isorim3d" on the command line starts with the 3D view on
            // (used by the screenshot harness). The normal default (off) is unchanged.
            // A probe file whose first line is "#startoff" keeps it off (to film the first switch-on).
            if (GenCommandLine.CommandLineArgPassed("isorim3d") && !Probe.StartOff)
            {
                View3D.Active = true;
                View3D.HarnessStartsOn = true;
                Log.Message("[IsoRim] -isorim3d: 3D view starts on.");
            }
            // A gameplay addon that needs the 3D view locks it on (set in the addon's mod constructor, before this runs).
            if (View3D.LockedOn)
            {
                View3D.Active = true;
                ModLog.Note("[IsoRim] 3D view locked on by " + View3D.LockedBy + ".");
            }

            try
            {
                Patches.ApplyAll();
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] Could not apply patches: " + e);
            }

            try
            {
                ModelLibrary.LoadAll();
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim] Could not load models: " + e);
            }
        }
    }
}
