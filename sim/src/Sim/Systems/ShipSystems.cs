using System.Collections.Generic;
using Sim.Core;

namespace Sim.Systems
{
    /// <summary>
    /// All systems of one match, built from a <see cref="MapProfile"/>, plus the shared sabotage cooldown.
    /// Spec: s1-s5. Systems missing on the map are null.
    /// </summary>
    public sealed class ShipSystems
    {
        private readonly List<ISystem> _specials = new List<ISystem>();
        private readonly List<ISystem> _early = new List<ISystem>();
        private readonly List<ISystem> _late = new List<ISystem>();

        public ShipSystems(MapProfile profile, IRandom rng, int[]? doorRooms = null)
        {
            Profile = profile;
            Cooldown = new SabotageCooldown();

            // Tick order follows the spec (s2.2): ids below the cooldown system first, then it, then 57/58.
            if (profile.ReactorSeconds > 0f) { Reactor = new HoldMeltdown(profile.ReactorSeconds); _early.Add(Reactor); }
            if (profile.LabReactorSeconds > 0f) { LabReactor = new HoldMeltdown(profile.LabReactorSeconds); _early.Add(LabReactor); }
            if (profile.HasLights) { Lights = new LightsSystem(rng); _early.Add(Lights); }
            if (profile.OxygenSeconds > 0f) { Oxygen = new CodeMeltdown(profile.OxygenSeconds, rng); _early.Add(Oxygen); }
            if (profile.Comms == CommsKind.Simple) { var c = new SimpleComms(); SimpleComms = c; Comms = c; }
            else if (profile.Comms == CommsKind.Window) { var c = new WindowComms(rng); WindowComms = c; Comms = c; }
            if (Comms != null) _early.Add(Comms);
            if (profile.Doors != DoorMode.None)
            {
                if (doorRooms == null)
                {
                    doorRooms = new int[profile.DefaultDoorRooms];
                    for (int i = 0; i < doorRooms.Length; i++) doorRooms[i] = i;
                }
                Doors = new DoorSystem(profile.Doors, doorRooms);
                _early.Add(Doors);
            }
            if (profile.HasMushroomMixup) { Mushroom = new MushroomMixup(); _late.Add(Mushroom); }
            if (profile.HeliSeconds > 0f) { Heli = new WindowMeltdown(profile.HeliSeconds, rng); _late.Add(Heli); }

            _specials.AddRange(_early);
            _specials.AddRange(_late);
        }

        public MapProfile Profile { get; }
        public SabotageCooldown Cooldown { get; }
        public HoldMeltdown? Reactor { get; }
        public HoldMeltdown? LabReactor { get; }
        public CodeMeltdown? Oxygen { get; }
        public WindowMeltdown? Heli { get; }
        public LightsSystem? Lights { get; }
        public ICommsSystem? Comms { get; }
        public SimpleComms? SimpleComms { get; }
        public WindowComms? WindowComms { get; }
        public DoorSystem? Doors { get; }
        public MushroomMixup? Mushroom { get; }

        /// <summary>While true nothing ticks and sabotage requests are refused. Spec: s5.</summary>
        public bool MeetingActive { get; private set; }

        /// <summary>Any special sabotage active (freezes the shared cooldown). Spec: s3 AnyActive.</summary>
        public bool AnySpecialActive
        {
            get
            {
                foreach (var s in _specials) if (s.IsActive) return true;
                return false;
            }
        }

        /// <summary>Game start: the 10 s lockout for sabotages and doors. Spec: s3, s7.</summary>
        public void StartGame()
        {
            Cooldown.SetInitial();
            Doors?.SetInitialCooldown();
        }

        public void Tick(float dt)
        {
            if (MeetingActive) return;
            foreach (var s in _early) s.Tick(dt);
            Cooldown.Tick(dt, AnySpecialActive);
            foreach (var s in _late) s.Tick(dt);
        }

        /// <summary>
        /// A saboteur asks for a sabotage. Order: role, map, meeting, cooldown (spec s3 steps 2-3).
        /// Hardening: the original checked neither role nor target availability.
        /// </summary>
        public SystemResult RequestSabotage(SabotageKind kind, bool callerMaySabotage)
        {
            if (!callerMaySabotage) return SystemResult.NotEligible;
            if (!Exists(kind)) return SystemResult.UnavailableOnMap;
            if (MeetingActive) return SystemResult.MeetingInProgress;
            if (!Cooldown.IsReady) return SystemResult.OnCooldown;

            switch (kind)
            {
                case SabotageKind.Reactor: Reactor!.Start(); break;
                case SabotageKind.LabReactor: LabReactor!.Start(); break;
                case SabotageKind.Oxygen: Oxygen!.Start(); break;
                case SabotageKind.Heli: Heli!.Start(); break;
                case SabotageKind.Lights: Lights!.Sabotage(); break;
                case SabotageKind.Comms: Comms!.Start(); break;
                case SabotageKind.MushroomMixup: Mushroom!.Trigger(); break;
            }
            Cooldown.Trigger();
            return SystemResult.Ok;
        }

        /// <summary>Close the doors of a room (not a sabotage-cooldown action). Spec: s7.</summary>
        public SystemResult RequestCloseDoors(int room, bool callerMaySabotage)
        {
            if (Doors == null) return SystemResult.UnavailableOnMap;
            if (MeetingActive) return SystemResult.MeetingInProgress;
            return Doors.TryCloseRoom(room, callerMaySabotage);
        }

        public bool Exists(SabotageKind kind)
        {
            switch (kind)
            {
                case SabotageKind.Reactor: return Reactor != null;
                case SabotageKind.LabReactor: return LabReactor != null;
                case SabotageKind.Oxygen: return Oxygen != null;
                case SabotageKind.Heli: return Heli != null;
                case SabotageKind.Lights: return Lights != null;
                case SabotageKind.Comms: return Comms != null;
                case SabotageKind.MushroomMixup: return Mushroom != null;
                default: return false;
            }
        }

        /// <summary>Spec: s5 RepairCriticalSabotages. Lights, comms, doors and the cooldown are untouched.</summary>
        public void RepairCriticalSabotages()
        {
            LabReactor?.Clear();
            Reactor?.Clear();
            Oxygen?.Clear();
            Heli?.Clear();
        }

        /// <summary>Meeting starts: critical sabotages are cleared and everything pauses. Spec: s5.</summary>
        public void BeginMeeting()
        {
            MeetingActive = true;
            RepairCriticalSabotages();
        }

        /// <summary>Meeting over: resume and set the 10 s lockout (our design choice, s5 notes).</summary>
        public void EndMeeting()
        {
            MeetingActive = false;
            Cooldown.SetInitial();
            Doors?.SetInitialCooldown();
        }

        /// <summary>A holder died or left: release every console they hold. Spec: s4 notes.</summary>
        public void ReleasePlayer(int playerId)
        {
            Reactor?.Release(playerId);
            LabReactor?.Release(playerId);
        }

        /// <summary>
        /// Win check, called before the other win checks. Spec: s4.5. Returns true when a countdown
        /// ran out (impostors win by sabotage) and resets that system.
        /// </summary>
        public bool PollSabotageWin()
        {
            if (Oxygen != null && Oxygen.CountdownExpired) { Oxygen.Clear(); return true; }
            foreach (var c in new CriticalSabotage?[] { Reactor, LabReactor, Heli })
            {
                if (c != null && c.CountdownExpired) { c.Clear(); return true; }
            }
            return false;
        }

        /// <summary>Convenience: the game-over reason when <see cref="PollSabotageWin"/> is true.</summary>
        public static GameOverReason WinReason => GameOverReason.ImpostorBySabotage;
    }
}
