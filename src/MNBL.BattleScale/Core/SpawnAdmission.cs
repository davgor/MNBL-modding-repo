using System;
using System.Collections.Generic;

namespace MNBL.BattleScale.Core
{
    public static class SpawnAdmission
    {
        public static int Check(int capacity, int occupied, int required, int requestedIndex, Func<int, bool> isOccupied)
        {
            if (capacity < 1 || occupied < 0 || occupied > capacity || required < 1 || required > 2)
                throw new ArgumentOutOfRangeException("Invalid native pool accounting.");
            if (requestedIndex < -1 || requestedIndex >= capacity)
                throw new ArgumentOutOfRangeException("requestedIndex");
            if (requestedIndex >= 0 && isOccupied(requestedIndex))
                throw new ArgumentException("Requested native agent slot is already occupied.", "requestedIndex");
            if (capacity - occupied < required) return -1;
            if (requestedIndex >= 0) return requestedIndex;
            for (int i = 0; i < capacity; i++) if (!isOccupied(i)) return i;
            throw new InvalidOperationException("Native pool count disagrees with slot headers.");
        }
    }

    // Thread confinement is enforced by the native adapter. A scope owns concrete
    // indices so nested automatic requests cannot steal an explicit mount slot.
    public sealed class SpawnReservationLedger
    {
        private sealed class Scope
        {
            internal object Mission;
            internal Queue<int> Slots;
            internal bool Armed;
        }
        private readonly int capacity;
        private readonly Stack<Scope> scopes = new Stack<Scope>();
        private readonly HashSet<int> reserved = new HashSet<int>();
        public int ReservedCount { get { return reserved.Count; } }
        public int ScopeCount { get { return scopes.Count; } }

        public SpawnReservationLedger(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
        }

        private void ValidateOwner(object mission)
        {
            if (mission == null || (scopes.Count != 0 && !ReferenceEquals(scopes.Peek().Mission, mission)))
                throw new InvalidOperationException("Spawn reservations belong to another mission.");
        }

        private int Select(int occupied, int required, int requested, Func<int, bool> isOccupied)
        {
            if (isOccupied == null) throw new ArgumentNullException("isOccupied");
            return SpawnAdmission.Check(capacity, checked(occupied + reserved.Count), required, requested,
                index => reserved.Contains(index) || isOccupied(index));
        }

        public bool Begin(object mission, int occupied, int required, int rider, int mount, Func<int, bool> isOccupied)
        {
            ValidateOwner(mission);
            int first = Select(occupied, required, rider, isOccupied);
            if (first < 0) return false;
            // Validate all selections before publishing a scope. Temporary claims
            // are undone even when explicit mount validation throws.
            reserved.Add(first);
            int second = -1;
            try
            {
                if (required == 2)
                {
                    second = Select(occupied, 1, mount, isOccupied);
                    if (second < 0) return false;
                }
            }
            finally { reserved.Remove(first); }
            var slots = new Queue<int>();
            slots.Enqueue(first);
            reserved.Add(first);
            if (second >= 0) { slots.Enqueue(second); reserved.Add(second); }
            scopes.Push(new Scope { Mission = mission, Slots = slots });
            return true;
        }

        public void Arm(object mission)
        {
            ValidateOwner(mission);
            if (scopes.Count == 0 || scopes.Peek().Armed || scopes.Peek().Slots.Count == 0)
                throw new InvalidOperationException("No reserved creation is available.");
            scopes.Peek().Armed = true;
        }

        public int Admit(object mission, int occupied, int requested, Func<int, bool> isOccupied)
        {
            ValidateOwner(mission);
            if (scopes.Count != 0 && scopes.Peek().Armed)
            {
                Scope scope = scopes.Peek();
                scope.Armed = false; // Clear before the native call or any callback.
                int selected = scope.Slots.Peek();
                if (requested < -1 || requested >= capacity || (requested >= 0 && requested != selected))
                    throw new ArgumentException("Native create differs from the reserved request.", "requested");
                if (occupied < 0 || occupied >= capacity || isOccupied(selected))
                    throw new InvalidOperationException("Native pool changed beneath a reserved spawn.");
                scope.Slots.Dequeue();
                if (!reserved.Remove(selected)) throw new InvalidOperationException("Lost spawn reservation.");
                return selected;
            }
            return Select(occupied, 1, requested, isOccupied);
        }

        public void End(object mission)
        {
            ValidateOwner(mission);
            if (scopes.Count == 0) throw new InvalidOperationException("No spawn scope to release.");
            foreach (int slot in scopes.Pop().Slots) reserved.Remove(slot);
        }
    }
}
