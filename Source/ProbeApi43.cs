using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness-only check for Api 43 (ApparelModelDef.shown) on the ap9fix api43 fixture (ProbeApi9.cs; stand mode):
    //   ap9check43=label,N   after N frames: per fixture pawn, its shield item, the binding's shown value and on how many
    //                        frames People.Draw showed the held shield, with PASS/FAIL lines. Expected:
    //     Api43Test_ShieldWeapon (shown Weapon, own DrawWornExtras draws nothing)   every frame drafted and standing
    //                                                                              (knife and rifle), never undrafted
    //     Api43Test_ShieldAlways (shown Always, same class)                          every frame standing (undrafted too)
    //     Api43Test_ShieldDefault (Default, same class)                              never (the gap Weapon closes)
    //     Api43Test_ShieldDrawsOwn (Default, its DrawWornExtras always draws)        every frame drafted and standing
    //     Apparel_ShieldBelt (Default, plain Apparel class)                          every frame drafted and standing
    //   Lying pawns are never asked (People.Draw shows held shields standing only) and must show nothing.
    public static partial class Probe
    {
        private static bool ap43Patched;
        private static int ap43Frames = -1, ap43Count;
        private static string ap43Label;
        private static readonly Dictionary<int, int> ap43Asked = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> ap43Shown = new Dictionary<int, int>();

        private static bool Api43CheckKey(string key, string[] a, Map map)
        {
            if (key != "ap9check43")
            {
                return false;
            }
            if (!ap43Patched)
            {
                ap43Patched = true;
                new Harmony("peren.isorim.probe.ap43check").Patch(AccessTools.Method(typeof(ApparelModels), nameof(ApparelModels.ShieldShown)),
                    postfix: new HarmonyMethod(typeof(Probe), nameof(Api43ShieldShown_Postfix)));
            }
            ap43Label = a[0];
            ap43Frames = I(a[1]);
            ap43Count = 0;
            ap43Asked.Clear();
            ap43Shown.Clear();
            return true;
        }

        public static void Api43ShieldShown_Postfix(Pawn pawn, bool __result)
        {
            if (ap43Frames < 0 || pawn == null)
            {
                return;
            }
            ap43Asked[pawn.thingIDNumber] = (ap43Asked.TryGetValue(pawn.thingIDNumber, out int n) ? n : 0) + 1;
            if (__result)
            {
                ap43Shown[pawn.thingIDNumber] = (ap43Shown.TryGetValue(pawn.thingIDNumber, out int s) ? s : 0) + 1;
            }
        }

        private static void Api43CheckUpdate(Map map)
        {
            if (ap43Frames < 0 || ++ap43Count < ap43Frames)
            {
                return;
            }
            ap43Frames = -1;
            string L = "[IsoRim probe] ap9check43 " + ap43Label + ": ";
            int pass = 0, fail = 0;
            Log.Message(L + "frames " + ap43Count + ", Api.Version " + Api.Version + ", bindings " + ApparelModels.Count);
            foreach (Pawn p in ap9Spawned.OrderBy(x => ap9Roles[x.thingIDNumber]))
            {
                string role = ap9Roles[p.thingIDNumber];
                People.Look look = People.LastLook(p);
                Apparel item = null;
                ApparelModels.Binding b = null;
                foreach (Apparel ap in p.apparel.WornApparel)
                {
                    ApparelModels.Binding bb = ApparelModels.For(ap.def);
                    if (bb != null && bb.slot == ApparelModels.Slot.Shield)
                    {
                        item = ap;
                        b = bb;
                    }
                }
                int asked = ap43Asked.TryGetValue(p.thingIDNumber, out int na) ? na : 0;
                int shown = ap43Shown.TryGetValue(p.thingIDNumber, out int ns) ? ns : 0;
                List<string> fails = new List<string>();
                bool expect = false;
                if (b == null || b.ModelFor("male") == null)
                {
                    fails.Add("no shield binding or model");
                }
                else
                {
                    bool standing = !p.Downed;
                    switch (item.def.defName)
                    {
                        case "Api43Test_ShieldWeapon": expect = standing && p.Drafted; break;
                        case "Api43Test_ShieldAlways": expect = standing; break;
                        case "Api43Test_ShieldDrawsOwn": expect = standing && p.Drafted; break;
                        case "Api43Test_ShieldDefault": expect = false; break;
                        default: expect = standing && p.Drafted; break;   // Apparel_ShieldBelt: plain Apparel class
                    }
                    if (look == null)
                    {
                        fails.Add("not drawn");
                    }
                    else if (look.shield != b.ModelFor(People.BuildOf(p.story.bodyType)))
                    {
                        fails.Add("shield not the bound model");
                    }
                    if (expect && (asked == 0 || shown < asked))
                    {
                        fails.Add("shield not shown");
                    }
                    if (!expect && shown > 0)
                    {
                        fails.Add("shield shown");
                    }
                }
                if (fails.Count == 0)
                {
                    pass++;
                }
                else
                {
                    fail++;
                }
                Log.Message(L + role + " " + p.LabelShort + ": item " + (item != null ? item.def.defName : "-") + ", shown " + (b != null ? b.def.shown.ToString() : "-")
                    + (b != null && b.def.alwaysShown ? " (alwaysShown)" : "") + ", drawsOwn " + (b != null && b.drawsOwn) + ", drafted " + p.Drafted + ", downed " + p.Downed
                    + ", expect " + expect + ", shield asked " + asked + " shown " + shown + " -> " + (fails.Count == 0 ? "PASS" : "FAIL (" + string.Join(", ", fails) + ")"));
            }
            Log.Message(L + "total " + pass + " PASS, " + fail + " FAIL");
        }
    }
}
