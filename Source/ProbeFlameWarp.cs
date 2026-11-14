using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Harness-only key for the gravship cutscene's draw order (flamewarp, 2026-11-13). Written so the same file also builds
    // into the base commit (it reads ShipFlight's fields by name, and the flame and lens flare materials actually drawn
    // through ShipFlight.FlameMaterial / ShipFlight.LensFlare when those exist).
    //   flamequeues=1   on the first 3D cutscene frame of each takeoff and of each landing, logs the render queues the
    //                   engine holds for what the flight draws: the distortion and the scene grab it samples (Waylith's
    //                   SceneGrabRegistry), the thruster flames, lens flare, downwash, engine and extender glows, the
    //                   ship's shadow, the exhaust fleck, and the range of the 3D ship's own materials, as
    //                   "[IsoRim probe] flamequeues ..." lines; the postfix it puts on ShipFlight.Effects is taken off again
    //                   once a takeoff and a landing are logged, or by flamequeues=0
    public static partial class Probe
    {
        private static bool flameQueuesHooked;
        private static bool flameQueuesOn;
        private static bool flameQueuesTakeoffDone;
        private static bool flameQueuesLandingDone;

        private static bool FlameWarpKey(string key, string[] a, Map map)
        {
            if (key != "flamequeues")
            {
                return false;
            }
            flameQueuesOn = a.Length == 0 || a[0] == "1";
            flameQueuesTakeoffDone = false;
            flameQueuesLandingDone = false;
            if (!flameQueuesOn)
            {
                FlameQueuesUnhook("key set to 0");
            }
            else if (!flameQueuesHooked)
            {
                flameQueuesHooked = true;
                new Harmony(FlameQueuesHarmonyId).Patch(AccessTools.Method(typeof(ShipFlight), "Effects"),
                    postfix: new HarmonyMethod(typeof(Probe), nameof(FlameQueues_Postfix)));
            }
            return true;
        }

        private const string FlameQueuesHarmonyId = "isorim.probe.flamewarp";

        // Takes the probe's postfix off ShipFlight.Effects again and logs whether it is gone.
        private static void FlameQueuesUnhook(string why)
        {
            if (!flameQueuesHooked)
            {
                return;
            }
            flameQueuesHooked = false;
            MethodInfo effects = AccessTools.Method(typeof(ShipFlight), "Effects");
            new Harmony(FlameQueuesHarmonyId).Unpatch(effects, HarmonyPatchType.Postfix, FlameQueuesHarmonyId);
            HarmonyLib.Patches info = Harmony.GetPatchInfo(effects);
            bool left = info != null && info.Postfixes.Any(p => p.owner == FlameQueuesHarmonyId);
            Log.Message("[IsoRim probe] flamequeues unpatched (" + why + "): probe postfix on ShipFlight.Effects " + (left ? "STILL THERE" : "removed"));
        }

        public static void FlameQueues_Postfix(Gravship g, bool isTakeoff)
        {
            if (!flameQueuesOn || (isTakeoff ? flameQueuesTakeoffDone : flameQueuesLandingDone))
            {
                return;
            }
            if (isTakeoff)
            {
                flameQueuesTakeoffDone = true;
            }
            else
            {
                flameQueuesLandingDone = true;
            }
            try
            {
                string w = isTakeoff ? "takeoff" : "landing";
                Func<string, Material> stat = n => AccessTools.Field(typeof(ShipFlight), n)?.GetValue(null) as Material;
                Func<Material, string> q = m => m == null ? "null" : m.renderQueue + " (" + m.name + ", shader " + (m.shader != null ? m.shader.name : "null") + ")";
                Log.Message("[IsoRim probe] flamequeues " + w + " distortion " + q(stat("matDistortion")));
                Material distortionMat = stat("matDistortion");
                Waylith.Rendering.SceneGrabPoint ownGrab = distortionMat != null ? distortionMat.engineMaterial?.Shader?.SceneGrab : null;
                Log.Message("[IsoRim probe] flamequeues " + w + " distortion's own scene grab " + (ownGrab != null ? ownGrab.Name + " " + ownGrab.Queue : "none")
                    + ", ShipFlight.WarpedQueue " + (AccessTools.Property(typeof(ShipFlight), "WarpedQueue")?.GetValue(null) ?? "n/a"));
                foreach (string n in new[] { "Gravship", "Distortion", "DistortionBackground" })
                {
                    Waylith.Rendering.SceneGrabPoint p;
                    Log.Message("[IsoRim probe] flamequeues " + w + " scenegrab " + n + " " + (Waylith.Rendering.SceneGrabRegistry.TryResolve(n, out p) ? p.Queue + " (" + p.TexturePropertyName + ")" : "not defined"));
                }
                Log.Message("[IsoRim probe] flamequeues " + w + " lensflare " + q(stat("matLensFlare")));
                PropertyInfo drawnFlare = AccessTools.Property(typeof(ShipFlight), "LensFlare");
                if (drawnFlare != null)
                {
                    Log.Message("[IsoRim probe] flamequeues " + w + " lensflare drawn " + q(drawnFlare.GetValue(null) as Material));
                }
                Log.Message("[IsoRim probe] flamequeues " + w + " downwash " + q(stat("matDownwash")));
                Log.Message("[IsoRim probe] flamequeues " + w + " engineglow " + q(stat("matEngineGlow")));
                Log.Message("[IsoRim probe] flamequeues " + w + " extenderglow " + q(stat("matExtenderGlow")));
                PropertyInfo drawnExtender = AccessTools.Property(typeof(ShipFlight), "ExtenderGlow");
                if (drawnExtender != null)
                {
                    Log.Message("[IsoRim probe] flamequeues " + w + " extenderglow drawn " + q(drawnExtender.GetValue(null) as Material));
                }
                Log.Message("[IsoRim probe] flamequeues " + w + " extenders " + g.GravFieldExtenderPositions.Count());
                object snap = AccessTools.Field(typeof(ShipFlight), "snap")?.GetValue(null);
                if (snap != null)
                {
                    Log.Message("[IsoRim probe] flamequeues " + w + " shadow " + q(AccessTools.Field(snap.GetType(), "shadowMat")?.GetValue(snap) as Material));
                    List<Material> mats = AccessTools.Field(snap.GetType(), "mats")?.GetValue(snap) as List<Material>;
                    if (mats != null && mats.Count > 0)
                    {
                        List<int> queues = mats.Where(m => m != null).Select(m => m.renderQueue).ToList();
                        Log.Message("[IsoRim probe] flamequeues " + w + " ship materials " + queues.Count + ", queues " + queues.Min() + "-" + queues.Max()
                            + ", distinct " + string.Join(",", queues.Distinct().OrderBy(x => x)));
                    }
                }
                MethodInfo flameMat = AccessTools.Method(typeof(ShipFlight), "FlameMaterial");
                HashSet<ThingDef> seen = new HashSet<ThingDef>();
                foreach (KeyValuePair<Thing, PositionData.Data> kv in g.ThrusterPlacements)
                {
                    if (!seen.Add(kv.Key.def))
                    {
                        continue;
                    }
                    CompProperties_GravshipThruster props = kv.Key.TryGetComp<CompGravshipThruster>().Props;
                    Material used;
                    string how;
                    if (flameMat != null)
                    {
                        used = flameMat.Invoke(null, new object[] { props }) as Material;
                        how = "ShipFlight.FlameMaterial";
                    }
                    else
                    {
                        // The base build's request: vanilla's queue, read from the game (GravshipRenderer.ThrusterFlameRenderQueue).
                        FieldInfo vq = AccessTools.Field(typeof(GravshipRenderer), "ThrusterFlameRenderQueue");
                        int vanillaQueue = vq != null && vq.IsLiteral ? (int)vq.GetRawConstantValue() : 0;
                        MaterialRequest req = new MaterialRequest(props.FlameShaderType.Shader);
                        req.renderQueue = vanillaQueue;
                        used = MaterialPool.MatFrom(req);
                        how = "base request at GravshipRenderer.ThrusterFlameRenderQueue " + vanillaQueue;
                    }
                    Material plain = MaterialPool.MatFrom(new MaterialRequest(props.FlameShaderType.Shader));
                    Log.Message("[IsoRim probe] flamequeues " + w + " flame " + kv.Key.def.defName + " " + q(used) + " via " + how + "; shader's own " + q(plain));
                    FleckDef fleck = props.exhaustSettings != null ? props.exhaustSettings.ExhaustFleckDef : null;
                    Material ex = fleck != null && fleck.graphicData != null && fleck.graphicData.Graphic != null ? fleck.graphicData.Graphic.MatSingle : null;
                    Log.Message("[IsoRim probe] flamequeues " + w + " exhaust " + (fleck != null ? fleck.defName : "none") + " " + q(ex)
                        + " (enabled " + (props.exhaustSettings != null && props.exhaustSettings.enabled) + ")");
                }
            }
            catch (Exception e)
            {
                Log.Error("[IsoRim probe] flamequeues failed: " + e);
            }
            if (flameQueuesTakeoffDone && flameQueuesLandingDone)
            {
                FlameQueuesUnhook("takeoff and landing logged");
            }
        }
    }
}
