using System.Linq;
using RimWorld;
using Verse;

namespace IsoRim
{
    // Harness-only key for slithering animals (Api 81, ModelDef.slither; Slither.cs). Spawn with mech=x,z,<kind>,... (ProbeMechs),
    // walk with goto= / walkfilm (ProbeScene).
    //   slithercheck=label   every spawned pawn whose model has slither: posture, moving, its gait, whether its last draw was
    //                        bent (Slither.LastBent: frames ago, reach, phase), and how its corpses are drawn
    public static partial class Probe
    {
        // Appended to each walkfilm frame's log line (ProbeMechs): every slithering-capable pawn, whether it was drawn bent this
        // frame or the last (Slither.LastBent), its gait, reach and phase.
        private static string SlitherFilmNote()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return "";
            }
            System.Text.StringBuilder sb = null;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                ModelEntry e;
                if (!ModelLibrary.TryGet(p.def, out e) || e.def.slither <= 0f)
                {
                    continue;
                }
                float yaw, phase, gait;
                Animals.LastState(p, out yaw, out phase, out gait);
                int age;
                float reach, bentPhase;
                bool known = Slither.LastBent(p, out age, out reach, out bentPhase);
                sb = sb ?? new System.Text.StringBuilder();
                sb.Append(" | slither " + p.def.defName + p.thingIDNumber + " " + p.Position + " moving " + (p.pather != null && p.pather.Moving)
                    + " gait " + gait.ToString("0.00") + " bent " + (known && age >= 0 && age <= 1) + (known ? " reach " + reach.ToString("0.0000") + " phase " + bentPhase.ToString("0.00") : "")
                    + CopiesNote(p) + " wounds [" + WoundMarks.Report(p) + "]");
            }
            return sb != null ? sb.ToString() : "";
        }

        // The animal's bent copies (Slither.Copies): how many (parts + loose), whether each is a mesh of its own, and the shared
        // meshes' corner hash (must not change while it walks).
        private static string CopiesNote(Pawn p)
        {
            int parts, loose, shared;
            bool own;
            return Slither.Copies(p, out parts, out loose, out own, out shared)
                ? " copies " + parts + "+" + loose + " (made u" + Slither.CopiesMadeAt(p) + ") ownMeshes " + own + " sharedHash " + shared.ToString("x8")
                : " copies none";
        }

        private static bool SlitherKey(string key, string[] a, Map map)
        {
            if (key != "slithercheck")
            {
                return false;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.OrderBy(x => x.Position.z).ThenBy(x => x.Position.x))
            {
                ModelEntry e;
                if (!ModelLibrary.TryGet(p.def, out e) || e.def.slither <= 0f)
                {
                    continue;
                }
                float yaw, phase, gait;
                Animals.LastState(p, out yaw, out phase, out gait);
                int age;
                float reach, bentPhase;
                bool known = Slither.LastBent(p, out age, out reach, out bentPhase);
                // bent now: drawn bent in the last few frames (the probe runs between frames)
                bool bentNow = known && age >= 0 && age <= 2;
                Log.Message("[IsoRim probe] slithercheck " + a[0] + " " + p.def.defName + " " + p.ageTracker.CurLifeStage.defName + " " + p.Position
                    + " posture " + p.GetPosture() + " moving " + (p.pather != null && p.pather.Moving) + " carried " + (p.ParentHolder is Pawn_CarryTracker)
                    + " gait " + gait.ToString("0.000") + " slither " + e.def.slither.ToString("0.000") + " bentNow " + bentNow
                    + (known ? " lastBent " + age + " frames ago reach " + reach.ToString("0.0000") + " phase " + bentPhase.ToString("0.000") : " neverBent")
                    + CopiesNote(p));
            }
            foreach (Corpse c in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>())
            {
                ModelEntry e;
                if (c.InnerPawn == null || !ModelLibrary.TryGet(c.InnerPawn.def, out e) || e.def.slither <= 0f)
                {
                    continue;
                }
                int age;
                float reach, bentPhase;
                bool known = Slither.LastBent(c.InnerPawn, out age, out reach, out bentPhase);
                Log.Message("[IsoRim probe] slithercheck " + a[0] + " corpse " + c.InnerPawn.def.defName + " " + c.Position
                    + " bentNow " + (known && age >= 0 && age <= 2));
            }
            return true;
        }
    }
}
