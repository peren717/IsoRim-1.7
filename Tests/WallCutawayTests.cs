using System;
using IsoRim;
using Verse;

internal static class WallCutawayTests
{
    private static int passed;

    private static void Main()
    {
        Run("native first-person entry and exit refresh wall display", NativeView);
        Run("rejected first-person entry leaves wall display alone", RejectedEntry);
        Run("independent requests work in either release order", RequestOrders);
        Run("native and addon views do not cancel each other", NativeAndAddon);
        Run("map changes expire old requests permanently", MapChange);
        Run("new game identity expires requests even with the same map", GameChange);
        Run("menu entry expires requests", Menu);
        Run("reset invalidates old handles without affecting new requests", Reset);
        Run("invalid requests fail without changing display", InvalidRequests);
        Run("pawn death, destruction, despawn and map change end requests", PawnLifecycle);
        Run("native pawn loss ends first person", NativePawnLoss);
        Run("stable camera frames do not invalidate drawing repeatedly", StableFrames);
        Run("disabling the 3D view suspends the override", ViewOff);
        Run("a missing game expires requests", NoGame);
        Run("loading-thread reset safely overlaps request disposal", LoadingReset);
        Console.WriteLine($"PASS: {passed} headless lifecycle scenarios (engine stubs; no in-game rendering).");
    }

    private static void Run(string name, Action test)
    {
        FullWalls.Reset();
        FirstPerson.Reset();
        Current.Game = new Game();
        Current.ProgramState = ProgramState.Playing;
        Find.CurrentMap = new Map();
        Find.Selector.NumSelected = 0;
        Find.Selector.SingleSelectedThing = null;
        View3D.Active = true;
        Cutaway.Changes.Clear();
        test();
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Enter()
    {
        Find.Selector.NumSelected = 1;
        Find.Selector.SingleSelectedThing = new Pawn { Map = Find.CurrentMap };
        FirstPerson.Toggle();
        FullWalls.Refresh();
        Check(FirstPerson.On && FullWalls.Required(Find.CurrentMap), "First person must request whole walls.");
    }

    private static void NativeView()
    {
        Map map = Find.CurrentMap;
        Enter();
        Check(Cutaway.Changes.Count == 1 && Cutaway.Changes[0] == map, "Entry must invalidate existing wall display.");
        FirstPerson.Leave(null);
        FullWalls.Refresh();
        Check(!FullWalls.Required(map), "Exiting must restore normal wall display.");
        Check(Cutaway.Changes.Count == 2 && Cutaway.Changes[1] == map, "Exit must invalidate wall display again.");
    }

    private static void RejectedEntry()
    {
        FirstPerson.Toggle();
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap) && Cutaway.Changes.Count == 0, "No selected pawn must not change walls.");
    }

    private static void RequestOrders()
    {
        for (int order = 0; order < 2; order++)
        {
            IDisposable a = Api.RequestFullWalls(Find.CurrentMap);
            IDisposable b = Api.RequestFullWalls(Find.CurrentMap);
            FullWalls.Refresh();
            (order == 0 ? a : b).Dispose();
            FullWalls.Refresh();
            Check(FullWalls.Required(Find.CurrentMap), "Releasing one request must preserve the other.");
            (order == 0 ? b : a).Dispose();
            FullWalls.Refresh();
            Check(!FullWalls.Required(Find.CurrentMap), "The last release must restore the normal display.");
            a.Dispose();
            b.Dispose();
        }
    }

    private static void NativeAndAddon()
    {
        Enter();
        IDisposable addon = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        FirstPerson.Leave(null);
        FullWalls.Refresh();
        Check(FullWalls.Required(Find.CurrentMap), "Leaving first person must preserve an addon request.");
        Enter();
        addon.Dispose();
        FullWalls.Refresh();
        Check(FullWalls.Required(Find.CurrentMap), "Addon exit must preserve native first person.");
        FirstPerson.Leave(null);
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap), "No requester remains.");
    }

    private static void MapChange()
    {
        Map previous = Find.CurrentMap;
        IDisposable request = Api.RequestFullWalls(previous);
        FullWalls.Refresh();
        Find.CurrentMap = new Map();
        FullWalls.Refresh();
        Check(!FullWalls.Required(previous) && !FullWalls.Required(Find.CurrentMap), "A map switch must end the request.");
        Find.CurrentMap = previous;
        FullWalls.Refresh();
        Check(!FullWalls.Required(previous), "Returning must not reactivate an expired request.");
        request.Dispose();
    }

    private static void GameChange()
    {
        using IDisposable request = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        Current.Game = new Game();
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap), "A different game must discard old requests.");
    }

    private static void Menu()
    {
        using IDisposable request = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        Current.ProgramState = ProgramState.Entry;
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap), "The menu must not retain the display override.");
        Current.ProgramState = ProgramState.Playing;
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap), "Returning must not resurrect a request.");
    }

    private static void Reset()
    {
        IDisposable old = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        FullWalls.Reset();
        using IDisposable fresh = Api.RequestFullWalls(Find.CurrentMap);
        old.Dispose();
        FullWalls.Refresh();
        Check(FullWalls.Required(Find.CurrentMap), "Disposing a stale handle must not cancel a new one.");
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void InvalidRequests()
    {
        Expect<ArgumentNullException>(() => Api.RequestFullWalls(null));
        Expect<InvalidOperationException>(() => Api.RequestFullWalls(new Map()));
        Expect<ArgumentException>(() => Api.RequestFullWalls(Find.CurrentMap, new Pawn { Map = Find.CurrentMap, Dead = true }));
        Current.ProgramState = ProgramState.Entry;
        Expect<InvalidOperationException>(() => Api.RequestFullWalls(Find.CurrentMap));
        FullWalls.Refresh();
        Check(Cutaway.Changes.Count == 0, "Invalid requests must not change wall display.");
    }

    private static void PawnLifecycle()
    {
        Action<Pawn>[] end = { p => p.Dead = true, p => p.Destroyed = true, p => p.Spawned = false, p => p.Map = new Map() };
        foreach (Action<Pawn> change in end)
        {
            Pawn pawn = new Pawn { Map = Find.CurrentMap };
            using IDisposable request = Api.RequestFullWalls(Find.CurrentMap, pawn);
            FullWalls.Refresh();
            Check(FullWalls.Required(Find.CurrentMap), "A valid followed pawn must enable the request.");
            change(pawn);
            FullWalls.Refresh();
            Check(!FullWalls.Required(Find.CurrentMap), "Losing the followed pawn must end the request.");
        }
    }

    private static void NativePawnLoss()
    {
        Action<Pawn>[] end = { p => p.Dead = true, p => p.Destroyed = true, p => p.Spawned = false, p => p.Map = new Map() };
        foreach (Action<Pawn> change in end)
        {
            Enter();
            change(FirstPerson.Pawn);
            FirstPerson.Update();
            FullWalls.Refresh();
            Check(!FirstPerson.On && !FullWalls.Required(Find.CurrentMap), "Losing the native pawn must restore wall display.");
        }
    }

    private static void StableFrames()
    {
        using IDisposable request = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        for (int frame = 0; frame < 100; frame++) FullWalls.Refresh();
        Check(Cutaway.Changes.Count == 1, "An unchanged policy must not repeatedly rebuild walls.");
        Check(!FullWalls.Required(new Map()), "An override must not affect other maps.");
    }

    private static void ViewOff()
    {
        using IDisposable request = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        View3D.Active = false;
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap), "Flat view must not keep the override active.");
        Check(Cutaway.Changes.Count == 2, "Turning off must invalidate the previous drawing state.");
    }

    private static void NoGame()
    {
        using IDisposable request = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        Current.Game = null;
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap), "Missing game must end the request.");
        Current.Game = new Game();
        FullWalls.Refresh();
        Check(!FullWalls.Required(Find.CurrentMap), "New game must not revive the request.");
    }

    private static void LoadingReset()
    {
        IDisposable[] handles = new IDisposable[100];
        for (int i = 0; i < handles.Length; i++) handles[i] = Api.RequestFullWalls(Find.CurrentMap);
        System.Threading.Tasks.Parallel.Invoke(
            () => FullWalls.Reset(),
            () => { foreach (IDisposable handle in handles) handle.Dispose(); });
        using IDisposable fresh = Api.RequestFullWalls(Find.CurrentMap);
        FullWalls.Refresh();
        Check(FullWalls.Required(Find.CurrentMap), "A fresh request must work after concurrent loading cleanup.");
    }
}
