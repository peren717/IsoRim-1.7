using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using W = Waylith.Rendering;

namespace IsoRim
{
    // Photos from any pose without touching the shown view (Api 76, Api.CaptureView): a "fake camera".
    //
    // How: on the frame a photo is taken, after the game has drawn the shown map (a Map.MapUpdate postfix), IsoRim installs
    // the photo's camera in its own view state (View3D.BeginPhoto: the fields every reader of the view uses, the culling
    // planes, the pitch and yaw upright sprites are turned by) and runs the same drawing again: the map's blocks
    // (MapDrawer.DrawMapMesh, culled for the photo by Cull; a block waiting to be rebuilt is drawn as last built, never
    // rebuilt for the photo), the things (DynamicDrawManager.DrawDynamicThings: figures, models, cards and sprites turned to
    // the photo's camera), the flecks, IsoRim's own parts drawn every frame (View3D.DrawFrameParts) and debris, the sky
    // (perspective), and the map overlays when asked. While it runs, every draw of the shown view's pass (MapMain) is put on
    // the photo's own layer (IsoRimPhoto, routed to the engine's CaptureOnly pass, so the shown view never draws it) and given
    // the photo's per-view shader values in a property block (View3D.ViewValues: _IsoView, _IsoEye, _IsoCard; _ScreenParams;
    // a still _WaterOutputTex; _IsoSee off without overlays); a draw for another pass (the game's water-depth prepass, which
    // only the shown camera's ripple field uses) is left out. A property block is copied when the draw is queued and wins over
    // the material and the globals, so the shown view's globals and materials are never written. Then the shown view's state
    // is put back (the same frame). The photo's camera (a Camera enabled for that one frame, with its own target, culling
    // only that layer) is rendered by the engine at the end of the frame (CameraCaptureFeature, synchronous; for that render
    // only, the planet-sun globals the engine takes a capture's sun from say what the map's sun says, see OnPreCull) and read
    // back on the next one, after the shown view's anti-aliasing / sharpening and colour grade are run over it with
    // Graphics.Blit (IsoRimPhotoAA, IsoRimPhotoGrade: the same shader bodies) and the engine's final saturation.
    //
    // Not in photos: what only exists as a full-screen pass of the shown view (ambient occlusion, screen-space sun shadows,
    // tilt-shift and depth of field), the weather (rain, snow, curtains), the game's screen overlays, IMGUI. Built for the
    // shown view and kept as it is: the cut-away walls and roofs, the map edge, what is lit for the view's facing. See
    // Docs/Api.md part 54.
    //
    // Engine members touched (all public): UnityEngine.Graphics.DrawMesh / DrawMeshInstanced / DrawMeshInstancedIndirect (a
    // Harmony prefix at load on the public overloads every other overload ends in, HookDraws), LayerMask.NameToLayer,
    // LayerPassRouter.RegisterLayerMask / Resolve, RenderPassRegistry.Register, Camera (targetTexture, matrices, onPreCull,
    // onPostRender), MaterialPropertyBlock.Clone, Graphics.Blit, Texture2D.ReadPixels. A frame without a photo pays one flag
    // test per draw call (about 8 ns, 0.015 ms a frame at 2000 draws); the per-frame step (Map.MapUpdate postfix), the layer
    // and the camera exist only from the first request on.
    [StaticConstructorOnStartup]
    internal static class Photos
    {
        public const string LayerName = "IsoRimPhoto";
        public const string PassName = "CaptureOnly";      // the engine's pass for draws only capture cameras see
        public const string MainPassName = "MapMain";      // the engine's pass of the shown map view (its default pass)
        public const int MinSide = 16;                     // photo size limits, pixels
        public const int MaxSide = 4096;
        private const int WaitFrames = 120;                // a photo that can't be drawn this long (no map shown) answers null
        private const int RenderFrames = 3;                // its camera not rendered this many frames after it was drawn: null
        private const string AADef = "IsoRimPhotoAA";
        private const string GradeDef = "IsoRimPhotoGrade";

        private sealed class Asked
        {
            public Map map;
            public ViewPose pose;
            public int width, height;
            public bool overlays, post;
            public Pawn eyesOf;
            public Action<Texture2D> done;
            public int since;
        }

        private static readonly List<Asked> waiting = new List<Asked>();
        private static Asked shooting;
        private static int shotFrame;
        private static float shotSaturation = 1f;
        private static volatile bool rendered;
        private static int renderedFrame = -1;

        public static volatile bool Broken;

        // Harness/diagnostics.
        public static int Taken, Delivered, Failed, LastClones, LastDraws, DirtyDrawn;
        public static double LastPassMs, LastReadMs, LastBakeMs;
        private static readonly Stopwatch bakeWatch = new Stopwatch();

        // Read by the draw prefixes while a photo's draws are made.
        internal static bool Drawing;
        internal static Pawn EyesOf;
        private static int layer = -1;
        private static MaterialPropertyBlock viewProps;
        private static MaterialPropertyBlock lastCopy;
        private static int clones, draws;

        private static Camera cam;
        private static RenderTexture target, spare;
        private static Material aaMat, gradeMat;
        private static bool hooked, triedPost;
        private static readonly int screenParamsId = Shader.PropertyToID("_ScreenParams");
        private static readonly AccessTools.FieldRef<OverlayDrawer, Dictionary<Thing, OverlayTypes>> overlaysOf =
            AccessTools.FieldRefAccess<OverlayDrawer, Dictionary<Thing, OverlayTypes>>("overlaysToDraw");
        private static readonly List<KeyValuePair<Thing, OverlayTypes>> overlaysWere = new List<KeyValuePair<Thing, OverlayTypes>>();

        public static bool Busy
        {
            get { return shooting != null || waiting.Count > 0; }
        }

        public static bool Request(Map map, ViewPose pose, CaptureViewOptions options, Action<Texture2D> done)
        {
            if (done == null)
            {
                throw new ArgumentNullException(nameof(done));
            }
            ViewPose clean;
            if (map == null || Broken || Current.ProgramState != ProgramState.Playing || !View3D.Active || !ViewPoses.Clean(pose, out clean))
            {
                return false;
            }
            if (!Hook())
            {
                return false;
            }
            CaptureViewOptions o = options ?? new CaptureViewOptions();
            waiting.Add(new Asked
            {
                map = map,
                pose = clean,
                width = Side(o.Width, Screen.width),
                height = Side(o.Height, Screen.height),
                overlays = o.IncludeOverlays,
                post = o.ColourPost,
                eyesOf = o.FirstPersonPawn,
                done = done,
                since = Time.frameCount
            });
            return true;
        }

        private static int Side(int asked, int screen)
        {
            return Mathf.Clamp(asked > 0 ? asked : screen, MinSide, MaxSide);
        }

        // A new game: open requests are answered with null.
        public static void Reset()
        {
            if (!Busy)
            {
                return;
            }
            List<Asked> all = new List<Asked>(waiting);
            if (shooting != null)
            {
                all.Add(shooting);
            }
            waiting.Clear();
            shooting = null;   // (a camera drawn this frame still renders once and switches itself off: OnPostRender)
            for (int i = 0; i < all.Count; i++)
            {
                Answer(all[i], null);
            }
        }

        // ---------------------------------------------------------------- set up (the first request)

        private static bool Hook()
        {
            if (hooked)
            {
                return !Broken;
            }
            hooked = true;
            try
            {
                layer = LayerMask.NameToLayer(LayerName);
                if (layer < 0 || layer > 31)
                {
                    Fail("no free layer", null);
                    return false;
                }
                if (drawEntries == 0)
                {
                    Fail("no Graphics draw entry point was hooked at load", null);
                    return false;
                }
                W.LayerPassRouter.RegisterLayerMask(1 << layer, W.RenderPassRegistry.Register(PassName));
                new Harmony(HarmonyId).Patch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)), postfix: new HarmonyMethod(typeof(Photos), nameof(MapUpdate_Postfix)));
                Camera.onPreCull += OnPreCull;
                Camera.onPostRender += OnPostRender;
                ModLog.Note("[IsoRim] Photos (Api.CaptureView): layer " + layer + " routed to the " + PassName + " pass, " + drawEntries + " draw entry points.");
                return true;
            }
            catch (Exception e)
            {
                Fail("setting up failed", e);
                return false;
            }
        }

        public const string HarmonyId = "peren.isorim.captureview";
        private static int drawEntries;
        private static byte mainPass;

        // At load (Patches.ApplyAll), before the game's drawing code is compiled: the public Graphics draws every other draw
        // overload ends in (the ones that queue the draw or call past the public overloads: DrawMesh with a property block and
        // a render queue, DrawMeshInstanced with a layer, DrawMeshInstancedIndirect, the engine-material and DrawPropertyBlock
        // overloads) get the photo prefix. Hooked this early because the short forwarding overloads are compiled into their
        // callers; a draw entry hooked later would be missed by every caller already compiled. While no photo is drawn the
        // prefix only tests a flag.
        internal static void HookDraws(Harmony h)
        {
            mainPass = W.RenderPassRegistry.Register(MainPassName);
            HarmonyMethod withProps = new HarmonyMethod(typeof(Photos), nameof(DrawProps_Prefix)) { priority = Priority.Last };
            HarmonyMethod layerOnly = new HarmonyMethod(typeof(Photos), nameof(DrawLayer_Prefix)) { priority = Priority.Last };
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Graphics)))
            {
                if (!m.IsPublic || !m.IsStatic || m.ContainsGenericParameters
                    || (m.Name != "DrawMesh" && m.Name != "DrawMeshInstanced" && m.Name != "DrawMeshInstancedIndirect"))
                {
                    continue;
                }
                bool hasLayer = false, hasProps = false;
                foreach (ParameterInfo p in m.GetParameters())
                {
                    hasLayer |= p.Name == "layer" && p.ParameterType == typeof(int);
                    hasProps |= p.Name == "properties" && p.ParameterType == typeof(MaterialPropertyBlock);
                }
                if (!hasLayer || ForwardsToPublicDraw(m))
                {
                    continue;   // passes its layer (or 0) on to an overload that is hooked
                }
                h.Patch(m, prefix: hasProps ? withProps : layerOnly);
                drawEntries++;
            }
            if (drawEntries == 0)
            {
                Log.Warning("[IsoRim] Photos (Api.CaptureView): no Graphics draw entry point found; photos will answer null.");
            }
            h.Patch(AccessTools.Method(typeof(Section), nameof(Section.DrawSection)), prefix: new HarmonyMethod(typeof(Photos), nameof(DrawSection_Prefix)),
                finalizer: new HarmonyMethod(typeof(Photos), nameof(DrawSection_Finalizer)));
        }

        // (At load, with the draws.) A photo never rebuilds a block of the map: what is built for the view's facing would be
        // built for the photo's. A block waiting for its rebuild is drawn by the game's own Section.DrawSection as last built
        // (its "dirty" mark held off for that call, every other hook on it running as always); the shown view rebuilds it.
        public static void DrawSection_Prefix(Section __instance, out bool __state)
        {
            __state = Drawing && Cull.anyDirtyOf(__instance);
            if (__state)
            {
                Cull.anyDirtyOf(__instance) = false;
                DirtyDrawn++;
            }
        }

        public static Exception DrawSection_Finalizer(Section __instance, bool __state, Exception __exception)
        {
            if (__state)
            {
                Cull.anyDirtyOf(__instance) = true;
            }
            return __exception;
        }

        // The method calls another public Graphics draw (it only forwards to it).
        private static bool ForwardsToPublicDraw(MethodInfo m)
        {
            try
            {
                foreach (KeyValuePair<System.Reflection.Emit.OpCode, object> ins in PatchProcessor.ReadMethodBody(m))
                {
                    if (ins.Value is MethodInfo callee && callee != m && callee.DeclaringType == typeof(Graphics) && callee.IsPublic
                        && (callee.Name == "DrawMesh" || callee.Name == "DrawMeshInstanced" || callee.Name == "DrawMeshInstancedIndirect"))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static void Fail(string what, Exception e)
        {
            Broken = true;
            Log.Warning("[IsoRim] Photos (Api.CaptureView) are off for this session: " + what + (e != null ? ": " + e : "."));
        }

        // ---------------------------------------------------------------- the draws of a photo

        // Every public Graphics draw with a layer, while a photo's draws are made: a draw of the shown view's pass (MapMain)
        // goes on the photo's layer with the photo's view values; a draw for another pass (the water-depth prepass the
        // game's water draws for the shown camera, the world, a capture-only draw) is left out of the photo (false: not
        // queued at all, so that pass never gets it twice). Nested overloads see the block made by the outer one and keep it.
        public static bool DrawLayer_Prefix(ref int layer)
        {
            if (!Drawing)
            {
                return true;
            }
            if (layer != Photos.layer && W.LayerPassRouter.Resolve(layer) != mainPass)
            {
                return false;
            }
            layer = Photos.layer;
            draws++;
            return true;
        }

        public static bool DrawProps_Prefix(ref int layer, ref MaterialPropertyBlock properties)
        {
            if (!Drawing)
            {
                return true;
            }
            if (layer != Photos.layer && W.LayerPassRouter.Resolve(layer) != mainPass)
            {
                return false;
            }
            layer = Photos.layer;
            draws++;
            if (properties == viewProps || (properties != null && properties == lastCopy))
            {
                return true;
            }
            if (properties == null)
            {
                properties = viewProps;
                return true;
            }
            // The caller's own values and the photo's view on top (the caller's block is left as it was).
            MaterialPropertyBlock copy = properties.Clone();
            ViewInto(copy);
            clones++;
            lastCopy = copy;
            properties = copy;
            return true;
        }

        private static View3D.ViewFrame viewFrame;
        private static Asked viewOf;

        // The photo's per-view values: its camera's (as the shown view's globals), the engine's screen size for the photo's
        // size, a still water surface (the game's ripple field is drawn in the shown view's screen space, so read at the
        // photo's pixels it would give the water a broken surface) and, without overlays, nothing see-through round the
        // pointer while placing.
        private static void ViewInto(MaterialPropertyBlock into)
        {
            View3D.ViewValues(viewFrame, into);
            into.SetVector(screenParamsId, new Vector4(viewOf.width, viewOf.height, 1f + 1f / viewOf.width, 1f + 1f / viewOf.height));
            into.SetTexture(waterRipplesId, stillWater);
            if (!viewOf.overlays)
            {
                into.SetVector(BuildSee.SeeId, BuildSee.Off);
            }
        }

        // The ripple field's "no ripple" (its height 0.5 everywhere: TerrainWater.frag.glsl reads height - 0.5).
        private static readonly int waterRipplesId = Shader.PropertyToID("_WaterOutputTex");
        private static Texture2D stillWater;

        private static void MakeViewProps(View3D.ViewFrame f, Asked a)
        {
            viewFrame = f;
            viewOf = a;
            if (stillWater == null)
            {
                stillWater = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                stillWater.name = "IsoRim_PhotoStillWater";
                stillWater.SetPixels32(new[] { new Color32(128, 128, 128, 255) });
                stillWater.Apply(false);
            }
            viewProps = viewProps ?? new MaterialPropertyBlock();
            viewProps.Clear();
            ViewInto(viewProps);
        }

        // After the game drew the shown map (Map.MapUpdate): the next photo's draws, if one waits and the map is shown.
        public static void MapUpdate_Postfix(Map __instance)
        {
            if (waiting.Count == 0 || shooting != null || __instance != Find.CurrentMap)
            {
                return;
            }
            Asked a = null;
            for (int i = 0; i < waiting.Count; i++)
            {
                if (waiting[i].map == __instance)
                {
                    a = waiting[i];
                    waiting.RemoveAt(i);
                    break;
                }
            }
            if (a == null)
            {
                return;
            }
            if (!CanDraw())
            {
                waiting.Insert(0, a);
                return;
            }
            try
            {
                Shoot(a);
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] A photo (Api.CaptureView) failed: " + e);
                if (cam != null)
                {
                    cam.enabled = false;
                }
                shooting = null;
                Answer(a, null);
            }
        }

        // The shown map is drawn in 3D this frame, and no gravship capture (whose camera sees every layer the shown one does)
        // runs.
        private static bool CanDraw()
        {
            return View3D.Ready && WorldRendererUtility.DrawingMap && Find.Camera != null
                && !WorldComponent_GravshipController.CutsceneInProgress && !WorldComponent_GravshipController.GravshipRenderInProgess;
        }

        private static void Shoot(Asked a)
        {
            Stopwatch watch = Stopwatch.StartNew();
            View3D.ViewFrame f = View3D.PhotoFrame(a.pose, a.width / (float)a.height);
            EnsureTarget(a.width, a.height);
            EnsureCamera();
            MakeViewProps(f, a);
            Map map = a.map;
            Dictionary<Thing, OverlayTypes> overlays = overlaysOf(map.overlayDrawer);
            overlaysWere.Clear();
            overlaysWere.AddRange(overlays);
            Cards.BeginPhoto();
            FireFx.BeginPhoto();
            clones = 0;
            draws = 0;
            lastCopy = null;
            View3D.BeginPhoto(f);
            EyesOf = a.eyesOf;
            Drawing = true;
            try
            {
                map.mapDrawer.DrawMapMesh();
                map.dynamicDrawManager.DrawDynamicThings();
                map.flecks.FleckManagerDraw();
                View3D.DrawFrameParts();
                CollapseDebris.Draw();
                View3D.DrawPhotoSky(Find.Camera);
                if (a.overlays)
                {
                    SelectionDrawer.DrawSelectionOverlays();
                    map.designationManager.DrawDesignations();
                    map.overlayDrawer.DrawAllOverlays();
                }
            }
            finally
            {
                Drawing = false;
                EyesOf = null;
                lastCopy = null;
                View3D.EndPhoto();
                FireFx.EndPhoto();
                // The status icons waiting for the shown view's pass are as they were: none the things asked for while drawn
                // for the photo is left, none drawn (and so cleared) for the photo is lost.
                overlays.Clear();
                for (int i = 0; i < overlaysWere.Count; i++)
                {
                    overlays[overlaysWere[i].Key] = overlaysWere[i].Value;
                }
                overlaysWere.Clear();
            }
            Matrix4x4 proj = f.Projection(f.orthoSize, f.aspect);
            cam.worldToCameraMatrix = f.worldToCamera;
            cam.projectionMatrix = proj;
            cam.cullingMatrix = proj * f.worldToCamera;
            cam.backgroundColor = Find.Camera.backgroundColor;
            cam.targetTexture = target;
            rendered = false;
            cam.enabled = true;
            shooting = a;
            shotFrame = Time.frameCount;
            shotSaturation = SceneImage.CompositeSaturation;
            Taken++;
            LastClones = clones;
            LastDraws = draws;
            LastPassMs = watch.Elapsed.TotalMilliseconds;
        }

        private static void EnsureTarget(int w, int h)
        {
            if (target == null || target.width != w || target.height != h)
            {
                if (target != null)
                {
                    target.Release();
                }
                if (spare != null)
                {
                    spare.Release();
                    spare = null;
                }
                target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                target.name = "IsoRim_Photo";
                target.Create();
            }
        }

        private static void EnsureCamera()
        {
            if (cam != null)
            {
                return;
            }
            GameObject go = new GameObject("IsoRim_PhotoCamera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.Color;
            cam.cullingMask = 1 << layer;   // the photo's draws only
            cam.depth = -100f;
        }

        // ---------------------------------------------------------------- rendered (end of the frame), read back (next frame)

        // ---- the frame's sun of the photo's render.
        // The engine renders a capture camera with its frame sun taken from the planet sun globals (CameraCaptureFeature
        // .ResolveSunDirection), the map view with the map's cast vector (_CastVect, MapRenderFeature). The capture's frame
        // uniforms (the sun among them) reach the shown view's frame too: with a different sun the shown map's sun shadows
        // are cast another way for that frame. So for the photo's render only, the planet sun says what the map's says (the
        // photo's shadows are then right too), and is put back right after. Only the world's planet shaders read those two
        // globals, and the world was captured before (no binding of the map view or the photo reads them).
        private static bool sunSwapped, hadSunOn, hadSunDir;
        private static float sunOnWas;
        private static Vector4 sunDirWas;

        private static void OnPreCull(Camera c)
        {
            if (c == null || c != cam)
            {
                return;
            }
            Vector4 cast;
            if (!Shader.TryGetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, out cast))
            {
                cast = new Vector4(0f, 1f, 0f, 0f);   // what the map view takes without one (MapRenderFeature)
            }
            hadSunOn = Shader.TryGetGlobalFloat(ShaderPropertyIDs.PlanetSunLightEnabled, out sunOnWas);
            hadSunDir = Shader.TryGetGlobalVector(ShaderPropertyIDs.PlanetSunLightDirection, out sunDirWas);
            Shader.SetGlobalVector(ShaderPropertyIDs.PlanetSunLightDirection, cast);
            Shader.SetGlobalFloat(ShaderPropertyIDs.PlanetSunLightEnabled, 1f);
            bakeWatch.Restart();
            sunSwapped = true;
        }

        private static void RestoreSun()
        {
            if (!sunSwapped)
            {
                return;
            }
            sunSwapped = false;
            if (hadSunDir)
            {
                Shader.SetGlobalVector(ShaderPropertyIDs.PlanetSunLightDirection, sunDirWas);
            }
            // As both readers saw it before: with no "enabled" global the direction alone decided (none: no planet sun).
            Shader.SetGlobalFloat(ShaderPropertyIDs.PlanetSunLightEnabled, hadSunOn ? sunOnWas : hadSunDir ? 1f : 0f);
        }

        // The engine rendered a capture camera (CameraCaptureFeature, end of the frame, main thread).
        private static void OnPostRender(Camera c)
        {
            if (c != null && c == cam)
            {
                RestoreSun();
                LastBakeMs = bakeWatch.Elapsed.TotalMilliseconds;
                renderedFrame = Time.frameCount;   // the engine skips capture cameras some frames (a long event): only the photo's own frame counts
                rendered = true;
                cam.enabled = false;   // one frame only
            }
        }

        // Once a frame from View3D.TickInner, before anything is drawn: the photo rendered at the end of the last frame is read
        // back and handed over; requests that can't be drawn are given up.
        public static void Frame()
        {
            if (shooting == null && waiting.Count == 0)
            {
                return;
            }
            RestoreSun();   // (normally put back by OnPostRender already)
            int now = Time.frameCount;
            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                Asked w = waiting[i];
                if (now - w.since > WaitFrames || w.map == null || w.map.Disposed || !View3D.Active)
                {
                    waiting.RemoveAt(i);
                    Answer(w, null);
                }
            }
            if (shooting == null)
            {
                return;
            }
            Asked a = shooting;
            if (rendered && renderedFrame != shotFrame)
            {
                // Rendered on a later frame than its draws were made in (they are gone by then): a blank picture.
                shooting = null;
                Answer(a, null);
                return;
            }
            if (!rendered)
            {
                if (now - shotFrame > RenderFrames)
                {
                    shooting = null;
                    cam.enabled = false;
                    Answer(a, null);
                }
                return;
            }
            shooting = null;
            Texture2D tex = null;
            // Graphics.Blit leaves its target as RenderTexture.active (until the engine's next frame): put back what was active,
            // or the interface drawn this frame (icons drawn with a material) would go into the photo's target.
            RenderTexture activeWas = RenderTexture.active;
            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                RenderTexture picture = a.post ? Post(target) : target;
                tex = SceneImage.ReadOut(picture, "IsoRim_Photo", shotSaturation);
                LastReadMs = watch.Elapsed.TotalMilliseconds;
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] Reading a photo (Api.CaptureView) failed: " + e);
                tex = null;
            }
            finally
            {
                RenderTexture.active = activeWas;
            }
            Answer(a, tex);
        }

        // The shown view's colour passes over the photo, in its order: anti-aliasing and sharpening, then the grade.
        private static RenderTexture Post(RenderTexture picture)
        {
            if (!triedPost)
            {
                triedPost = true;
                Shader aa = R3DMaterials.ShaderOf(AADef);
                Shader grade = R3DMaterials.ShaderOf(GradeDef);
                aaMat = aa != null ? new Material(aa) { name = "IsoRim_PhotoAA" } : null;
                gradeMat = grade != null ? new Material(grade) { name = "IsoRim_PhotoGrade" } : null;
                if (aaMat == null || gradeMat == null)
                {
                    Log.Warning("[IsoRim] Photos (Api.CaptureView): the colour pass shaders are missing; photos are not graded.");
                }
            }
            RenderTexture from = picture;
            if (aaMat != null && AntiAlias.SetUp(aaMat))
            {
                from = Blit(from, aaMat);
            }
            if (gradeMat != null && Grade.SetUp(gradeMat))
            {
                from = Blit(from, gradeMat);
            }
            return from;
        }

        // One pass from the picture into the other target of the pair (target / spare).
        private static RenderTexture Blit(RenderTexture from, Material mat)
        {
            if (spare == null || spare.width != from.width || spare.height != from.height)
            {
                if (spare != null)
                {
                    spare.Release();
                }
                spare = new RenderTexture(from.width, from.height, 0, RenderTextureFormat.ARGB32);
                spare.name = "IsoRim_PhotoPost";
                spare.Create();
            }
            RenderTexture to = from == target ? spare : target;
            Graphics.Blit(from, to, mat);
            return to;
        }

        private static void Answer(Asked a, Texture2D tex)
        {
            if (tex != null)
            {
                Delivered++;
            }
            else
            {
                Failed++;
            }
            try
            {
                a.done(tex);
            }
            catch (Exception e)
            {
                Log.Warning("[IsoRim] A photo callback (Api.CaptureView) failed: " + e);
            }
        }
    }
}
