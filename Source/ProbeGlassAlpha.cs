using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only check for Api 73 (a glass colour source's negative alpha = the bound ModelDef's opacity; Api.GlassOpacity):
    //   glassalphacheck=label   with the 3D view on: one thing whose bound model has a glass part and is not a door (the first such
    //     ThingDef by defName that can be spawned: the glass is drawn every frame, Glass.Draw), spawned at the map's centre with
    //     the camera on it; a colour source added for that thing only; then over real frames (each case checks the glass part
    //     IsoRim actually drew for it, seen at Glass.Submit; the thing, the source and the patch are removed at the end):
    //     a  the source answers a normal colour (alpha GlassAlphaNormal)   -> drawn in that colour and alpha, as before
    //     b  the source answers the tint with alpha GlassOpacityFromModel, the bound def's glassOpacity set to -1 (none)
    //                                                                       -> drawn in the tint at IsoRim's own Glass.Opacity,
    //                                                                          which is Api.GlassOpacity(thing)
    //     c  the bound def's glassOpacity set to GlassAlphaDef for the case -> b's answer follows it (0.25), Api.GlassOpacity too
    //     d  the source answers null                                       -> IsoRim's own look, as without a source
    //   CHECK lines (ProbeFixes.Check), then "[IsoRim probe] glassalphacheck <label>: total n PASS, m FAIL". Runs on the Api 17
    //   frame script runner (FwStart). The clean-up (the def's glassOpacity put back, the source removed, the patches undone,
    //   the thing removed) is the script's finally block: it runs when the script ends, when it throws (the exception leaves
    //   MoveNext through it), and when another script replaces it (FwStart disposes the old one).
    public static partial class Probe
    {
        private const int GlassAlphaSettle = 10;          // frames between a change and its check
        private const float GlassAlphaNormal = 0.7f;      // case a's alpha
        private const float GlassAlphaDef = 0.25f;        // case c's glassOpacity on the bound def
        private static readonly Color GlassAlphaTint = new Color(0.2f, 0.6f, 0.9f);

        private static Thing glassAlphaThing;
        private static Color? glassAlphaAnswer;          // what the source answers for the thing (null: passes)
        private static Thing glassAlphaDrawing;          // the thing Glass.Draw is drawing
        private static (bool custom, Color c, int n) glassAlphaSeen;
        private static Harmony glassAlphaHarmony;

        public static void GlassAlphaDraw_Prefix(Thing t) => glassAlphaDrawing = t;

        public static void GlassAlphaDraw_Finalizer() => glassAlphaDrawing = null;

        public static void GlassAlphaSubmit_Prefix(bool custom, Color c)
        {
            if (glassAlphaDrawing != null && glassAlphaDrawing == glassAlphaThing)
            {
                glassAlphaSeen = (custom, c, glassAlphaSeen.n + 1);
            }
        }

        private static Color? GlassAlphaSource(Thing t) => t == glassAlphaThing ? glassAlphaAnswer : null;

        private static bool GlassAlphaKey(string key, string[] a, Map map)
        {
            if (key != "glassalphacheck")
            {
                return false;
            }
            Api17Patch();
            FwStart(GlassAlphaScript(a[0], map));
            return true;
        }

        private static IEnumerator<int> GlassAlphaScript(string label, Map map)
        {
            string L = "glassalphacheck " + label + " ";
            int pass = 0, fail = 0;
            string Seen() => $"drawn {glassAlphaSeen.n}x, custom {glassAlphaSeen.custom}, colour {glassAlphaSeen.c}, Api.GlassOpacity {Api.GlassOpacity(glassAlphaThing):0.000}";
            void C(string what, bool ok)
            {
                if (ok)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Check(L + what, ok, Seen());
            }
            bool Near(float x, float y) => Mathf.Abs(x - y) < 0.001f;
            bool Tinted(Color c) => Near(c.r, GlassAlphaTint.r) && Near(c.g, GlassAlphaTint.g) && Near(c.b, GlassAlphaTint.b);

            ThingDef def = DefDatabase<ThingDef>.AllDefsListForReading.OrderBy(d => d.defName).FirstOrDefault(d =>
                d.category == ThingCategory.Building && !typeof(Building_Door).IsAssignableFrom(d.thingClass) && !d.IsBlueprint && !d.IsFrame
                && ModelLibrary.TryGet(d, out ModelEntry e) && e.glass != null && e.HasModel && !d.MadeFromStuff);
            if (def == null)
            {
                Check(L + "a building with a glass part", false, "none found");
                yield break;
            }
            ModelLibrary.TryGet(def, out ModelEntry entry);
            ModelDef md = entry.def;
            float defOpacity = md.glassOpacity;
            IntVec3 cell = map.Center;
            foreach (Thing old in cell.GetThingList(map).ToList())
            {
                if (old.def.destroyable && !(old is Pawn))
                {
                    old.Destroy();
                }
            }
            glassAlphaThing = GenSpawn.Spawn(ThingMaker.MakeThing(def), cell, map, WipeMode.Vanish);
            if (glassAlphaHarmony == null)
            {
                glassAlphaHarmony = new Harmony("peren.isorim.probe.glassalpha");
                glassAlphaHarmony.Patch(AccessTools.Method(typeof(Glass), "Draw", new[] { typeof(Thing), typeof(ModelEntry) }),
                    prefix: new HarmonyMethod(typeof(Probe), nameof(GlassAlphaDraw_Prefix)), finalizer: new HarmonyMethod(typeof(Probe), nameof(GlassAlphaDraw_Finalizer)));
                glassAlphaHarmony.Patch(AccessTools.Method(typeof(Glass), "Submit"), prefix: new HarmonyMethod(typeof(Probe), nameof(GlassAlphaSubmit_Prefix)));
            }
            Func<Thing, Color?> source = GlassAlphaSource;
            Api.AddGlassColor(source);
            Find.CameraDriver.JumpToCurrentMapLoc(cell);
            Log.Message("[IsoRim probe] " + L + "Api.Version " + Api.Version + ", thing " + def.defName + " (ModelDef " + md.defName + ", glassOpacity " + defOpacity + ") at " + cell);
            try
            {
                // a: a normal colour, as before Api 73
                glassAlphaAnswer = new Color(GlassAlphaTint.r, GlassAlphaTint.g, GlassAlphaTint.b, GlassAlphaNormal);
                glassAlphaSeen = default;
                yield return GlassAlphaSettle;
                C("a a normal source colour is drawn as it is", glassAlphaSeen.n > 0 && glassAlphaSeen.custom && Tinted(glassAlphaSeen.c) && Near(glassAlphaSeen.c.a, GlassAlphaNormal));

                // b: the tint only: the bound def's opacity
                glassAlphaAnswer = new Color(GlassAlphaTint.r, GlassAlphaTint.g, GlassAlphaTint.b, Api.GlassOpacityFromModel);
                md.glassOpacity = -1f;
                glassAlphaSeen = default;
                yield return GlassAlphaSettle;
                C($"b a negative alpha, the bound def setting none: drawn at IsoRim's own opacity ({Glass.Opacity:0.000})", glassAlphaSeen.n > 0 && glassAlphaSeen.custom && Tinted(glassAlphaSeen.c)
                    && Near(glassAlphaSeen.c.a, Glass.Opacity) && Near(Api.GlassOpacity(glassAlphaThing), Glass.Opacity));

                // c: the def's own value changed: the answer follows it
                md.glassOpacity = GlassAlphaDef;
                glassAlphaSeen = default;
                yield return GlassAlphaSettle;
                C($"c the bound def's glassOpacity set to {GlassAlphaDef}: drawn at it", glassAlphaSeen.n > 0 && glassAlphaSeen.custom && Tinted(glassAlphaSeen.c)
                    && Near(glassAlphaSeen.c.a, GlassAlphaDef) && Near(Api.GlassOpacity(glassAlphaThing), GlassAlphaDef));
                md.glassOpacity = defOpacity;

                // d: no answer: IsoRim's own look (the def's look, else its one glass)
                glassAlphaAnswer = null;
                glassAlphaSeen = default;
                yield return GlassAlphaSettle;
                bool own = !Glass.CustomLook(glassAlphaThing, md, null, out Color ownColour);
                C("d a source answering null leaves IsoRim's own look", glassAlphaSeen.n > 0 && glassAlphaSeen.custom == !own && (own || glassAlphaSeen.c == ownColour));
                C("e Api.GlassOpacity is NaN without a thing", float.IsNaN(Api.GlassOpacity(null)));
            }
            finally
            {
                md.glassOpacity = defOpacity;
                Api.RemoveGlassColor(source);
                glassAlphaHarmony?.UnpatchAll(glassAlphaHarmony.Id);
                glassAlphaHarmony = null;
                glassAlphaAnswer = null;
                if (glassAlphaThing != null && !glassAlphaThing.Destroyed)
                {
                    glassAlphaThing.Destroy();
                }
                glassAlphaThing = null;
            }
            Log.Message("[IsoRim probe] " + L.TrimEnd() + ": total " + pass + " PASS, " + fail + " FAIL");
        }
    }
}
