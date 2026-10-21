using System;
using System.Collections.Generic;
using Verse;

namespace IsoRim
{
    public static partial class Api
    {
        // Version 17. Call on the main thread when entering a camera mode that needs complete walls. Dispose on exit.
        // Requests are independent, never change settings, and expire on a map/game change. An optional followed pawn
        // also ends the request when it dies, despawns or leaves the map. Drawing updates on the next camera frame.
        public static IDisposable RequestFullWalls(Map map, Pawn followedPawn = null) => FullWalls.Request(map, followedPawn);
    }

    // One effective display rule shared by first person and add-on cameras, separate from the player's saved options.
    internal static class FullWalls
    {
        private static readonly List<WallRequest> requests = new List<WallRequest>();
        private static Map shownMap;
        private static bool required;

        internal static bool Required(Map map) => map != null && map == shownMap && required;

        internal static IDisposable Request(Map map, Pawn followedPawn)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || map != Find.CurrentMap)
            {
                throw new InvalidOperationException("Request complete walls only for the currently viewed map in a running game.");
            }
            if (followedPawn != null && (followedPawn.Destroyed || followedPawn.Dead || !followedPawn.Spawned || followedPawn.Map != map))
            {
                throw new ArgumentException("The followed pawn must be alive and spawned on the requested map.", nameof(followedPawn));
            }
            WallRequest request = new WallRequest { game = Current.Game, map = map, pawn = followedPawn };
            lock (requests)
            {
                requests.Add(request);
            }
            return request;
        }

        // After the camera is placed, before any geometry or cell texture is refreshed.
        internal static void Refresh()
        {
            Map map = Current.ProgramState == ProgramState.Playing && Current.Game != null ? Find.CurrentMap : null;
            bool hasRequests;
            lock (requests)
            {
                for (int i = requests.Count - 1; i >= 0; i--)
                {
                    WallRequest request = requests[i];
                    if (map == null || request.game != Current.Game || request.map != map
                        || request.pawn != null && (request.pawn.Destroyed || request.pawn.Dead || !request.pawn.Spawned || request.pawn.Map != map))
                    {
                        request.Dispose();
                    }
                }
                hasRequests = requests.Count > 0;
            }

            Map previous = shownMap;
            bool wasRequired = required;
            shownMap = map;
            required = map != null && View3D.Active && (FirstPerson.On || hasRequests);
            if (wasRequired && (previous != map || !required))
            {
                Cutaway.WallModeChanged(previous);
            }
            if (required && (previous != map || !wasRequired))
            {
                Cutaway.WallModeChanged(map);
            }
        }

        // No graphics work: also called from the Game constructor on its loading thread.
        internal static void Reset()
        {
            lock (requests)
            {
                for (int i = requests.Count - 1; i >= 0; i--)
                {
                    requests[i].Dispose();
                }
            }
            shownMap = null;
            required = false;
        }

        private sealed class WallRequest : IDisposable
        {
            internal Game game;
            internal Map map;
            internal Pawn pawn;

            public void Dispose()
            {
                lock (requests)
                {
                    requests.Remove(this);
                    game = null;
                    map = null;
                    pawn = null;
                }
            }
        }
    }
}
