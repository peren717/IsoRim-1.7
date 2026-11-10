using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // Diagnostics only (2026-11-09, a Linux report: models turn into flat sprites after a while, no log line). While the 3D view
    // and models are on, every time a thing with a model binding is drawn as its sprite (or not at all) instead of its model, the
    // reason is counted here; the first time for each reason and def it is said once in the log (at most Cap lines a session).
    // Every BeatSeconds, if anything fell back since the last summary, one line gives the counts by reason. Session-wide states
    // that turn 3D drawing off or back on mid-game (the view, the model settings, IsoRim's model material) are said once per flip.
    // Nothing is drawn differently: callers note what they already decided. Cheap: a counter, and a set lookup on a fallback.
    public static class FallbackLog
    {
        public enum Reason
        {
            None,
            NoModelLoaded,      // the binding names a model file, but none was loaded (missing, empty or unreadable)
            NoModelPicked,      // the entry has models, but none for this thing (its style, state, stuff or piece)
            MaterialMissing,    // IsoRim's model material was never made (its shader is missing)
            MaterialDestroyed,  // IsoRim's model material was destroyed during play
            BlockFull,          // the map block's mesh piece is full (MeshWriter.MaxVerts)
            NoMeshPiece,        // the map block gave no mesh piece for the model material
            CutUnderFloor,      // nothing of the model is left under the floor built over it
            RigMissing,         // a mech's or entity's model rig could not be built
            MovingMeshGone,     // a moving model's mesh or material was destroyed while still drawn
            MeshDropped,        // a mesh left out by the GPU check (MeshGuard says which, once per name)
            BakeFailed,         // the model could not be written, cause unknown
            Count
        }

        private static readonly string[] Why =
        {
            "",
            "its model file was not loaded (missing, empty or unreadable)",
            "none of its models fits this thing (style, state, stuff or piece)",
            "IsoRim's model material was never made (its shader is missing)",
            "IsoRim's model material was destroyed during play",
            "the map block's mesh is full",
            "the map block gave no mesh piece for models",
            "nothing of its model is left under the floor above",
            "its model rig could not be built",
            "a moving model's mesh or material was destroyed while still in use",
            "a mesh was left out by the GPU check",
            "its model could not be written"
        };

        private static readonly string[] Short =
        {
            "", "model not loaded", "no model fits", "model material missing", "model material destroyed", "block mesh full",
            "no mesh piece", "cut under floor", "rig missing", "moving mesh destroyed", "mesh dropped by GPU check", "bake failed"
        };

        public const int Cap = 30;
        internal static float BeatSeconds = 300f;   // the summary interval (the harness shortens it)

        private static readonly int[] sinceBeat = new int[(int)Reason.Count];
        private static int total;
        private static int lines;
        private static readonly HashSet<long> seen = new HashSet<long>();
        private static float lastBeat = -1f;

        // Set by ModelBaker.BakeModel where it gives up (read by BakeFailed); reset at each Bake.
        internal static Reason BakeWhy;

        // True while the per-thing notes count: the 3D view and models on.
        private static bool On
        {
            get { return View3D.Active && IsoRimMod.Settings.models3D; }
        }

        // A model bake that returned false at a call site that falls back (sprite: the game's sprite is drawn; else nothing).
        public static void BakeFailed(Thing t, bool sprite)
        {
            if (ModelBaker.Ghost.a > 0f || ModelBaker.DamageMarks > 0)
            {
                return;
            }
            Note(BakeWhy == Reason.None ? Reason.BakeFailed : BakeWhy, t, sprite);
        }

        // A thing with a model binding drawn as its sprite (sprite) or left out (!sprite) for this reason.
        public static void Note(Reason r, Thing t, bool sprite)
        {
            if (!On)
            {
                return;
            }
            int n = Interlocked.Increment(ref total);
            Interlocked.Increment(ref sinceBeat[(int)r]);
            if (lines >= Cap)
            {
                return;
            }
            ThingDef def = t != null ? t.def : null;
            long key = ((long)r << 32) | (uint)(def != null ? def.shortHash : 0);
            lock (seen)
            {
                if (lines >= Cap || !seen.Add(key))
                {
                    return;
                }
                lines++;
            }
            string where = t != null && t.Spawned ? " at (" + t.Position.x + ", " + t.Position.z + ")" : "";
            Log.Warning("[IsoRim] 3D model fallback: " + (def != null ? def.defName : "?") + where + " is drawn "
                + (sprite ? "as its flat sprite" : "without its model") + " because " + Why[(int)r] + ". Frame " + Time.frameCount
                + ", fallback " + n + " this session." + (lines >= Cap ? " (Line " + Cap + " of " + Cap + ": further fallbacks are only counted.)" : ""));
        }

        // A fallback with no thing at hand (a moving model's part, a dropped mesh): counted, said once per reason.
        public static void Note(Reason r, string what)
        {
            if (!On)
            {
                return;
            }
            int n = Interlocked.Increment(ref total);
            Interlocked.Increment(ref sinceBeat[(int)r]);
            if (lines >= Cap)
            {
                return;
            }
            lock (seen)
            {
                if (lines >= Cap || !seen.Add((long)r << 32))
                {
                    return;
                }
                lines++;
            }
            Log.Warning("[IsoRim] 3D model fallback: " + what + " is not drawn because " + Why[(int)r] + ". Frame " + Time.frameCount
                + ", fallback " + n + " this session." + (lines >= Cap ? " (Line " + Cap + " of " + Cap + ": further fallbacks are only counted.)" : ""));
        }

        // Counted for the summary only (the caller already says it: MeshGuard.Drop).
        public static void Count(Reason r)
        {
            if (On)
            {
                Interlocked.Increment(ref total);
                Interlocked.Increment(ref sinceBeat[(int)r]);
            }
        }

        // A def bound to a ModelDef that names model files, of which none was loaded.
        internal static bool Unloaded(ThingDef def)
        {
            ModelEntry entry;
            if (def == null || !ModelLibrary.TryGet(def, out entry) || entry.HasModel || entry.def.flat)
            {
                return false;
            }
            return !entry.def.model.NullOrEmpty() || (entry.def.models != null && entry.def.models.Count > 0);
        }

        // ------------------------------------------------------------ once-per-flip states

        private static bool seenOn, lastActive, lastReady;
        private static int lastSettings = -1;
        private static int materialState;   // 0 fine, 1 never made, 2 destroyed (said once per change)
        private static int clears;
        private static bool fireNoted;

        // A new or loaded game: the view's first switch-on is not a flip.
        public static void NewGame()
        {
            seenOn = false;
            lastActive = false;
            lastReady = false;
        }

        // Once a frame (View3D.TickInner): the view and setting flips, the material check, the summary.
        public static void Frame()
        {
            try
            {
                FrameInner();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[IsoRim] The 3D fallback diagnostics failed: " + e, 93623101);
            }
        }

        private static void FrameInner()
        {
            bool active = View3D.Active;
            if (active != lastActive)
            {
                if (seenOn)
                {
                    Log.Message("[IsoRim] The 3D view was turned " + (active ? "back on" : "off") + " (frame " + Time.frameCount + ").");
                }
                lastActive = active;
                seenOn |= active;
            }
            bool ready = View3D.Ready;
            if (ready != lastReady)
            {
                if (active && lastActive && seenOn && !ready)
                {
                    Log.Warning("[IsoRim] The 3D view lost its camera while on (frame " + Time.frameCount + "): figures are drawn as sprites until it is back.");
                }
                lastReady = ready;
            }

            IsoRimSettings st = IsoRimMod.Settings;
            int bits = (st.models3D ? 1 : 0) | (st.trees3D ? 2 : 0) | (st.crops3D ? 4 : 0) | (st.smallPlants3D ? 8 : 0)
                | (st.animals3D ? 16 : 0) | (st.pawns3D ? 32 : 0) | (st.mechs3D ? 64 : 0);
            if (bits != lastSettings)
            {
                if (lastSettings >= 0)
                {
                    SayFlip(bits, 1, "3D models");
                    SayFlip(bits, 2, "3D trees");
                    SayFlip(bits, 4, "3D crops");
                    SayFlip(bits, 8, "3D grass and wild plants");
                    SayFlip(bits, 16, "3D animals");
                    SayFlip(bits, 32, "3D people");
                    SayFlip(bits, 64, "3D mechanoids");
                }
                lastSettings = bits;
            }

            if ((Time.frameCount & 63) == 0)
            {
                int m = R3DMaterials.SolidState;
                if (m != materialState)
                {
                    if (m != 0)
                    {
                        Log.Warning("[IsoRim] " + (m == 2 ? "IsoRim's model material was destroyed during play" : "IsoRim's model material is missing")
                            + " (found at frame " + Time.frameCount + "): models are left out or drawn as sprites from now on.");
                    }
                    else
                    {
                        Log.Message("[IsoRim] IsoRim's model material is back (frame " + Time.frameCount + ").");
                    }
                    materialState = m;
                }
            }

            float now = Time.realtimeSinceStartup;
            if (lastBeat < 0f)
            {
                lastBeat = now;
            }
            else if (now - lastBeat >= BeatSeconds)
            {
                lastBeat = now;
                Beat();
            }
        }

        private static void SayFlip(int bits, int bit, string name)
        {
            if (((bits ^ lastSettings) & bit) != 0)
            {
                Log.Message("[IsoRim] The setting \"" + name + "\" was turned " + ((bits & bit) != 0 ? "on" : "off") + " (frame " + Time.frameCount + ").");
            }
        }

        private static void Beat()
        {
            System.Text.StringBuilder sb = null;
            for (int i = 1; i < (int)Reason.Count; i++)
            {
                int k = Interlocked.Exchange(ref sinceBeat[i], 0);
                if (k == 0)
                {
                    continue;
                }
                sb = sb ?? new System.Text.StringBuilder();
                sb.Append(sb.Length > 0 ? ", " : "").Append(Short[i]).Append(' ').Append(k);
            }
            if (sb == null)
            {
                return;
            }
            string span = BeatSeconds >= 60f ? Mathf.RoundToInt(BeatSeconds / 60f) + " minutes" : Mathf.RoundToInt(BeatSeconds) + " seconds";
            Log.Message("[IsoRim] 3D model fallbacks in the last " + span + ": " + sb + ". Frame "
                + Time.frameCount + ", " + total + " this session.");
        }

        // The moving model mesh cache thrown away because it was full (DynamicModels.PartsFor): said for the first few in a session.
        public static void CacheCleared(string what, int count)
        {
            if (count == 0 || clears >= 10)
            {
                return;
            }
            clears++;
            Log.Message("[IsoRim] " + what + " was cleared: " + count + " meshes, made again as needed (frame " + Time.frameCount + ")."
                + (clears == 10 ? " (Said 10 times; not again this session.)" : ""));
        }

        // The 3D fire particles turned off for the session (Effects.FireFxFailed).
        public static void FireOff(ThingDef fire)
        {
            if (fireNoted)
            {
                return;
            }
            fireNoted = true;
            Log.Warning("[IsoRim] 3D fire particles are off for this session: no fire look for " + (fire != null ? fire.defName : "?")
                + " (frame " + Time.frameCount + "). Fires are drawn as the game's sprites.");
        }

        // A model file that could not be read while playing (load-time failures are said in dev mode only, ModelLibrary.LoadFile).
        private static int loadFails;

        public static void LoadFailed(string path, Exception e)
        {
            if (Current.ProgramState != ProgramState.Playing || loadFails >= 10)
            {
                return;
            }
            loadFails++;
            Log.Warning("[IsoRim] Could not read model " + path + " while playing (frame " + Time.frameCount + "): " + e.Message
                + ". Things using it are drawn without it for this session.");
        }
    }
}
