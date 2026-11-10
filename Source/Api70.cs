using System.Reflection;
using System.Runtime.CompilerServices;

namespace IsoRim
{
    public static partial class Api
    {
        // ---- copied method watches (Version 70)

        /// <summary>
        /// (Version 70) For an addon that had to copy a method of the game or of another mod: registers the original and the
        /// hash of the version that was copied (<see cref="CopyHash"/>, pasted as a string). Once the game's current loading
        /// step has finished (a registration in a Mod constructor: after all mods and defs have loaded; in a static
        /// constructor: after the static constructors), IsoRim hashes the method's IL as it is now and, if it differs, logs
        /// one warning: "[IsoRim] note: Type.Method has changed since this was copied; it may need an update." A null method
        /// (not found by the caller's lookup) gives one warning too. One line per registration per session (the same method,
        /// hash and note registered again is the same registration). Never throws. Registered with no loading step under way
        /// (in game), it is checked at once. Nothing is patched; Harmony patches on the method do not change its hash.
        /// </summary>
        /// <param name="original">The method that was copied, as looked up now (null when the lookup found nothing).</param>
        /// <param name="expectedHash">Its <see cref="CopyHash"/> when it was copied.</param>
        /// <param name="note">Where the copy is, for the warning: "My Pack, Ballistics.RayCast". Empty: the caller's assembly name.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WatchCopy(MethodBase original, string expectedHash, string note)
        {
            if (string.IsNullOrEmpty(note))
            {
                try
                {
                    note = Assembly.GetCallingAssembly().GetName().Name;
                }
                catch
                {
                    note = "A copied method";
                }
            }
            CopyWatch.Add(original, expectedHash, note);
        }

        /// <summary>
        /// (Version 70) The hash <see cref="WatchCopy"/> compares: 16 lowercase hex digits of a SHA-256 over the method's
        /// IL as compiled (its MethodBody, not Harmony's patched copy), with each metadata token written as the name of the
        /// member, type or string it refers to, so a rebuild of the same source that only moves tokens gives the same hash.
        /// The same for every run and machine with the same build of that assembly. Null for a null method, a method without
        /// an IL body (abstract, extern) or one that cannot be read. The dev-mode debug action IsoRim, "Log copied method
        /// hashes" prints it for every registered watch.
        /// </summary>
        public static string CopyHash(MethodBase method)
        {
            return CopyWatch.Hash(method, out _);
        }
    }
}
