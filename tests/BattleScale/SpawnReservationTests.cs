using System;
using System.Collections.Generic;
using MNBL.BattleScale.Core;

public static class SpawnReservationTests
{
    private static int checks;
    private static void Check(bool value) { checks++; if (!value) throw new Exception("Reservation assertion " + checks); }
    private static void Throws<T>(Action action) where T : Exception
    {
        checks++;
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + " at assertion " + checks);
    }

    public static int Run()
    {
        checks = 0;
        object mission = new object();
        var live = new HashSet<int>();
        var pool = new SpawnReservationLedger(3);
        Check(pool.Begin(mission, 0, 2, -1, -1, live.Contains)); // MountIndex(-1) is automatic.
        Check(pool.ReservedCount == 2 && pool.ScopeCount == 1);
        // A reentrant preparation callback occurs before the outer arm.
        Check(pool.Admit(mission, 0, -1, live.Contains) == 2);
        live.Add(2);
        pool.Arm(mission);
        Check(pool.Admit(mission, 1, -1, live.Contains) == 0);
        live.Add(0);
        // OnAgentCreated cannot take the reserved horse through either API.
        Check(!pool.Begin(mission, 2, 1, -1, -1, live.Contains));
        Check(pool.Admit(mission, 2, -1, live.Contains) == -1);
        Check(pool.ScopeCount == 1 && pool.ReservedCount == 1);
        pool.Arm(mission);
        Check(pool.Admit(mission, 2, -1, live.Contains) == 1);
        live.Add(1); pool.End(mission);
        Check(pool.ScopeCount == 0 && pool.ReservedCount == 0 && live.Count == 3);
        Check(!pool.Begin(mission, 3, 1, -1, -1, live.Contains));

        live.Clear(); pool = new SpawnReservationLedger(5);
        Check(pool.Begin(mission, 0, 2, 4, 0, live.Contains));
        Check(pool.Begin(mission, 0, 1, -1, -1, live.Contains));
        pool.Arm(mission);
        Check(pool.Admit(mission, 0, -1, live.Contains) == 1); // Skips both reserved explicit indices.
        live.Add(1); pool.End(mission);
        pool.Arm(mission);
        Check(pool.Admit(mission, 1, 4, live.Contains) == 4);
        live.Add(4);
        pool.Arm(mission);
        Check(pool.Admit(mission, 2, 0, live.Contains) == 0);
        live.Add(0); pool.End(mission);
        Check(pool.ReservedCount == 0);

        live.Clear(); pool = new SpawnReservationLedger(5);
        Throws<ArgumentException>(() => pool.Begin(mission, 0, 2, 0, 0, live.Contains));
        Check(pool.ReservedCount == 0 && pool.ScopeCount == 0);
        Throws<ArgumentOutOfRangeException>(() => pool.Begin(mission, 0, 2, -1, 5, live.Contains));
        Check(pool.ReservedCount == 0 && pool.ScopeCount == 0);
        Check(pool.Begin(mission, 0, 2, -1, -1, live.Contains));
        Throws<InvalidOperationException>(() => pool.Begin(new object(), 0, 1, -1, -1, live.Contains));
        pool.Arm(mission);
        Throws<ArgumentException>(() => pool.Admit(mission, 0, 4, live.Contains));
        // Failure consumes no slot, clears the arm, and finally releases the scope.
        Check(pool.ReservedCount == 2);
        pool.End(mission); Check(pool.ReservedCount == 0 && pool.ScopeCount == 0);
        Throws<InvalidOperationException>(() => pool.Arm(mission));
        Throws<InvalidOperationException>(() => pool.End(mission));

        pool = new SpawnReservationLedger(2);
        Check(pool.Begin(mission, 0, 2, -1, -1, live.Contains));
        try { throw new ApplicationException("stock initialization failure"); }
        catch (ApplicationException) { }
        finally { pool.End(mission); }
        Check(pool.ReservedCount == 0 && pool.ScopeCount == 0);
        live.Add(0);
        Check(!pool.Begin(mission, 1, 2, -1, -1, live.Contains));
        Check(pool.Begin(mission, 1, 1, -1, -1, live.Contains));
        pool.Arm(mission); Check(pool.Admit(mission, 1, -1, live.Contains) == 1);
        live.Add(1); pool.End(mission);
        Check(pool.Admit(mission, 2, -1, live.Contains) == -1);
        return checks;
    }
}
