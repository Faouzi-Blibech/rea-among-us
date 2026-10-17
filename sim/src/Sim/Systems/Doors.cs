using System;
using System.Collections.Generic;

namespace Sim.Systems
{
    public enum DoorMode : byte
    {
        None,
        /// <summary>Closed on command, reopened by players at a console (spec s7.1).</summary>
        Command,
        /// <summary>Closed doors reopen themselves after 10 s (spec s7.2).</summary>
        AutoReopen,
    }

    /// <summary>
    /// Room doors with a per-room cooldown. Spec: s7. Hardening: the cooldown is enforced here, and
    /// open requests need an eligible caller and a closed door.
    /// </summary>
    public sealed class DoorSystem : ISystem
    {
        public const float RoomCooldownSeconds = 30f;
        public const float InitialCooldownSeconds = 10f;
        public const float AutoOpenSeconds = 10f;

        private readonly int[] _doorRoom;
        private readonly bool[] _open;
        private readonly float[] _closedTimer;
        private readonly Dictionary<int, float> _roomCooldown = new Dictionary<int, float>();

        /// <param name="mode">Door style.</param>
        /// <param name="doorRooms">Room id of each door; the door id is the array index.</param>
        public DoorSystem(DoorMode mode, int[] doorRooms)
        {
            Mode = mode;
            _doorRoom = (int[])doorRooms.Clone();
            _open = new bool[_doorRoom.Length];
            _closedTimer = new float[_doorRoom.Length];
            for (int i = 0; i < _open.Length; i++) _open[i] = true;
        }

        public DoorMode Mode { get; }
        public int DoorCount => _doorRoom.Length;
        public float InitialCooldown { get; private set; }

        public bool IsDoorOpen(int doorId) => _open[doorId];

        /// <summary>
        /// Spec: dictionary-door maps never pause the sabotage cooldown; the auto-door variant does
        /// while any door is closed (inferred).
        /// </summary>
        public bool IsActive
        {
            get
            {
                if (Mode != DoorMode.AutoReopen) return false;
                foreach (var o in _open) if (!o) return true;
                return false;
            }
        }

        /// <summary>Spec: SetInitialSabotageCooldown.</summary>
        public void SetInitialCooldown() => InitialCooldown = InitialCooldownSeconds;

        /// <summary>Remaining cooldown fraction for UI. Spec: GetTimer.</summary>
        public float GetTimer(int room)
        {
            if (InitialCooldown > 0f) return InitialCooldown / InitialCooldownSeconds;
            return _roomCooldown.TryGetValue(room, out var t) ? t / RoomCooldownSeconds : 0f;
        }

        /// <summary>Close every door of a room. Spec: CloseDoorsOfType, plus the cooldown check the original left to the UI.</summary>
        public SystemResult TryCloseRoom(int room, bool eligible)
        {
            if (Mode == DoorMode.None) return SystemResult.UnavailableOnMap;
            if (!eligible) return SystemResult.NotEligible;
            bool known = false;
            for (int i = 0; i < _doorRoom.Length; i++) if (_doorRoom[i] == room) { known = true; break; }
            if (!known) return SystemResult.UnknownDoor;
            if (InitialCooldown > 0f) return SystemResult.OnCooldown;
            if (_roomCooldown.TryGetValue(room, out var t) && t > 0f) return SystemResult.OnCooldown;

            _roomCooldown[room] = RoomCooldownSeconds;
            for (int i = 0; i < _doorRoom.Length; i++)
            {
                if (_doorRoom[i] != room) continue;
                _open[i] = false;
                _closedTimer[i] = AutoOpenSeconds;
            }
            return SystemResult.Ok;
        }

        /// <summary>Player opens a closed door (command variant only). Spec: s7.1 UpdateSystem, s7.3.</summary>
        public SystemResult TryOpenDoor(int doorId, bool eligible)
        {
            if (Mode != DoorMode.Command) return SystemResult.UnavailableOnMap;
            if (doorId < 0 || doorId >= _doorRoom.Length) return SystemResult.UnknownDoor;
            if (!eligible) return SystemResult.NotEligible;
            if (_open[doorId]) return SystemResult.NotActive;
            _open[doorId] = true;
            return SystemResult.Ok;
        }

        public void Tick(float dt)
        {
            if (Mode == DoorMode.None) return;
            var rooms = new List<int>(_roomCooldown.Keys);
            foreach (var r in rooms) _roomCooldown[r] = Math.Min(Math.Max(_roomCooldown[r] - dt, 0f), RoomCooldownSeconds);

            if (Mode == DoorMode.AutoReopen)
            {
                for (int i = 0; i < _open.Length; i++)
                {
                    if (_open[i]) continue;
                    _closedTimer[i] -= dt;
                    if (_closedTimer[i] <= 0f) { _open[i] = true; _closedTimer[i] = 0f; }
                }
            }

            if (InitialCooldown > 0f) InitialCooldown -= dt;
        }
    }
}
