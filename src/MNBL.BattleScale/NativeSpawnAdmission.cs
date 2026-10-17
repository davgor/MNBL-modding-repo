using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MNBL.BattleScale.Core;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MNBL.BattleScale
{
    public sealed class NativeSpawnCapacityException : InvalidOperationException
    {
        public NativeSpawnCapacityException() : base("Native agent capacity exhausted; no agent was allocated by this request.") { }
    }

    // Entry calls are inserted into a hash-bound offline managed-engine copy.
    // This protects the managed public API; arbitrary direct native calls are outside it.
    public static class NativeSpawnAdmission
    {
        private const int Capacity = 4080;
        private static int ownerThread;
        private static readonly SpawnReservationLedger reservations = new SpawnReservationLedger(Capacity);
        private static readonly PropertyInfo pointer = typeof(Mission).GetProperty("Pointer", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static void EnableForValidatedProfile()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            if (ownerThread != 0 && ownerThread != current) throw new InvalidOperationException("Spawn admission owner changed.");
            ownerThread = current;
        }

        private static IntPtr GetMission(Mission mission)
        {
            if (ownerThread == 0 || Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Spawning requires the validated mission thread.");
            if (mission == null || mission.IsFinalized || mission != Mission.Current || pointer == null)
                throw new InvalidOperationException("Spawning requires the current live mission.");
            UIntPtr value = (UIntPtr)pointer.GetValue(mission, null);
            if (value == UIntPtr.Zero) throw new InvalidOperationException("Mission has no native storage.");
            return new IntPtr(unchecked((long)value.ToUInt64()));
        }

        private static int Occupied(IntPtr native)
        {
            return Marshal.ReadInt32(IntPtr.Add(native, 0xc56750 + Capacity * 2));
        }

        private static bool SlotOccupied(IntPtr native, int index)
        {
            return Marshal.ReadInt32(IntPtr.Add(native, 0x2d50 + index * 0xc60 + 0x10)) >= 0;
        }

        public static void BeginSpawn(Mission mission, AgentBuildData data)
        {
            IntPtr native = GetMission(mission);
            if (data == null) throw new ArgumentNullException("data");
            if (data.AgentCharacter == null) throw new ArgumentException("Spawn requires a character.", "data");
            // Random equipment is selected later by stock code. Reserve conservatively
            // without consuming its RNG or modifying the request/character equipment.
            Equipment equipment = data.AgentOverridenSpawnEquipment;
            if (equipment == null && (data.AgentFixedEquipment || data.AgentCharacter.IsHero))
                equipment = data.AgentCivilianEquipment ? data.AgentCharacter.FirstCivilianEquipment : data.AgentCharacter.FirstBattleEquipment;
            bool mount = !data.AgentNoHorses;
            if (mount && equipment != null)
            {
                ItemObject item = equipment[EquipmentIndex.Horse].Item;
                mount = item != null && item.HasHorseComponent && item.HorseComponent.IsRideable;
            }
            if (!reservations.Begin(mission, Occupied(native), mount ? 2 : 1,
                data.AgentIndexOverriden ? data.AgentIndex : -1,
                mount && data.AgentMountIndexOverriden ? data.AgentMountIndex : -1,
                index => SlotOccupied(native, index))) throw new NativeSpawnCapacityException();
        }

        public static void ArmReservedCreate(bool useReservation)
        {
            if (!useReservation) return;
            Mission mission = Mission.Current;
            GetMission(mission);
            reservations.Arm(mission);
        }

        public static void EndSpawn(Mission mission)
        {
            // Finally must release reservations even if stock initialization
            // failed or a callback finalized the mission. Never dereference it.
            if (ownerThread == 0 || Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Spawn scope exited on another thread.");
            reservations.End(mission);
        }

        // This second guard is immediately before every managed native-create call,
        // including SpawnMonster and callbacks that allocate during another spawn.
        // No rollback of unrelated stock initialization exceptions is claimed.
        public static int ValidateNativeCreate(Mission mission, int requestedIndex)
        {
            IntPtr native = GetMission(mission);
            int selected = reservations.Admit(mission, Occupied(native), requestedIndex,
                index => SlotOccupied(native, index));
            if (selected < 0) throw new NativeSpawnCapacityException();
            return selected;
        }
    }
}
