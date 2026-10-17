using Sim.Core;
using Sim.Systems;

namespace Sim.Tests.Systems
{
    public class ShipSystemsTests
    {
        private static ShipSystems Make(MapProfile p, bool started = true)
        {
            var s = new ShipSystems(p, new SeededRandom(1));
            if (started) s.StartGame();
            return s;
        }

        [Fact]
        public void Profiles_expose_the_recovered_durations()
        {
            Assert.Equal(30f, MapProfile.Small.ReactorSeconds);
            Assert.Equal(30f, MapProfile.Small.OxygenSeconds);
            Assert.Equal(45f, MapProfile.Medium.ReactorSeconds);
            Assert.Equal(45f, MapProfile.Medium.OxygenSeconds);
            Assert.Equal(60f, MapProfile.Large.LabReactorSeconds);
            Assert.Equal(90f, MapProfile.Huge.HeliSeconds);
            Assert.Equal(60f, MapProfile.Jungle.ReactorSeconds);
            Assert.False(MapProfile.Jungle.HasLights);
        }

        [Theory]
        [InlineData(SabotageKind.Heli)]
        [InlineData(SabotageKind.Oxygen)]
        public void Sabotage_missing_on_map_is_rejected(SabotageKind kind)
        {
            var s = Make(MapProfile.Jungle);
            s.Tick(11f);
            Assert.Equal(SystemResult.UnavailableOnMap, s.RequestSabotage(kind, true));
        }

        [Fact]
        public void Initial_lockout_is_ten_seconds_then_sabotage_sets_thirty()
        {
            var s = Make(MapProfile.Small);
            Assert.Equal(SystemResult.OnCooldown, s.RequestSabotage(SabotageKind.Reactor, true));
            s.Tick(9.9f);
            Assert.Equal(SystemResult.OnCooldown, s.RequestSabotage(SabotageKind.Reactor, true));
            s.Tick(0.2f);
            Assert.Equal(SystemResult.Ok, s.RequestSabotage(SabotageKind.Reactor, true));
            Assert.Equal(30f, s.Cooldown.Timer);
            Assert.True(s.Reactor!.IsActive);
        }

        [Fact]
        public void Non_saboteur_is_rejected_before_anything_else()
        {
            var s = Make(MapProfile.Small);
            s.Tick(11f);
            Assert.Equal(SystemResult.NotEligible, s.RequestSabotage(SabotageKind.Reactor, false));
            Assert.True(s.Cooldown.IsReady);
        }

        [Fact]
        public void Cooldown_is_frozen_while_a_special_sabotage_is_active()
        {
            var s = Make(MapProfile.Small);
            s.Tick(11f);
            s.RequestSabotage(SabotageKind.Comms, true);
            s.Tick(20f);
            Assert.Equal(30f, s.Cooldown.Timer);
            s.SimpleComms!.Fix(true);
            s.Tick(10f);
            Assert.Equal(20f, s.Cooldown.Timer, 3);
            s.Tick(20f);
            Assert.Equal(SystemResult.Ok, s.RequestSabotage(SabotageKind.Lights, true));
        }

        [Fact]
        public void Meeting_blocks_requests_pauses_ticks_and_clears_critical_only()
        {
            var s = Make(MapProfile.Small);
            s.Tick(11f);
            s.RequestSabotage(SabotageKind.Reactor, true);
            s.Tick(5f);
            s.BeginMeeting();
            Assert.True(s.Reactor!.IsStopped);
            Assert.Equal(SystemResult.MeetingInProgress, s.RequestSabotage(SabotageKind.Lights, true));
            s.Tick(100f);
            Assert.Equal(30f, s.Cooldown.Timer); // paused
            s.EndMeeting();
            Assert.Equal(10f, s.Cooldown.Timer); // post-meeting lockout
        }

        [Fact]
        public void Meeting_start_keeps_lights_and_comms_sabotaged()
        {
            var s = Make(MapProfile.Medium);
            s.Tick(11f);
            s.RequestSabotage(SabotageKind.Comms, true);
            s.BeginMeeting();
            Assert.True(s.Comms!.IsActive);
            s.EndMeeting();
            s.Tick(11f);
            // Comms still active, cooldown frozen at the 10 s lockout.
            Assert.Equal(10f, s.Cooldown.Timer);
        }

        [Fact]
        public void Repair_clears_every_critical_sabotage_present()
        {
            var s = Make(MapProfile.Huge);
            s.Tick(11f);
            s.RequestSabotage(SabotageKind.Heli, true);
            s.RepairCriticalSabotages();
            Assert.True(s.Heli!.IsStopped);
        }

        [Fact]
        public void Oxygen_expiry_wins_and_resets_the_system()
        {
            var s = Make(MapProfile.Small);
            s.Tick(11f);
            s.RequestSabotage(SabotageKind.Oxygen, true);
            s.Tick(29f);
            Assert.False(s.PollSabotageWin());
            s.Tick(1.5f);
            Assert.True(s.PollSabotageWin());
            Assert.True(s.Oxygen!.IsStopped);
            Assert.False(s.PollSabotageWin());
        }

        [Fact]
        public void Lab_reactor_expiry_wins_on_large_profile()
        {
            var s = Make(MapProfile.Large);
            s.Tick(11f);
            Assert.Equal(SystemResult.UnavailableOnMap, s.RequestSabotage(SabotageKind.Reactor, true));
            Assert.Equal(SystemResult.Ok, s.RequestSabotage(SabotageKind.LabReactor, true));
            s.Tick(60.1f);
            Assert.True(s.PollSabotageWin());
        }

        [Fact]
        public void Dead_holder_releases_the_console()
        {
            var s = Make(MapProfile.Small);
            s.Tick(11f);
            s.RequestSabotage(SabotageKind.Reactor, true);
            s.Reactor!.Hold(1, 0, true);
            s.ReleasePlayer(1);
            s.Reactor.Hold(2, 1, true);
            Assert.True(s.Reactor.IsActive);
        }

        [Fact]
        public void Mushroom_mixup_heals_after_ten_seconds()
        {
            var s = Make(MapProfile.Jungle);
            s.Tick(11f);
            Assert.Equal(SystemResult.Ok, s.RequestSabotage(SabotageKind.MushroomMixup, true));
            s.Tick(9.9f);
            Assert.True(s.Mushroom!.IsActive);
            s.Tick(0.2f);
            Assert.False(s.Mushroom.IsActive);
        }
    }

    public class DoorTests
    {
        private static DoorSystem Command() => new DoorSystem(DoorMode.Command, new[] { 0, 0, 1 });
        private static DoorSystem Auto() => new DoorSystem(DoorMode.AutoReopen, new[] { 0, 0, 1 });

        [Fact]
        public void Close_respects_initial_then_room_cooldown()
        {
            var d = Command();
            d.SetInitialCooldown();
            Assert.Equal(SystemResult.OnCooldown, d.TryCloseRoom(0, true));
            d.Tick(10.1f);
            Assert.Equal(SystemResult.Ok, d.TryCloseRoom(0, true));
            Assert.False(d.IsDoorOpen(0));
            Assert.False(d.IsDoorOpen(1));
            Assert.True(d.IsDoorOpen(2));
            // Other rooms are independent.
            Assert.Equal(SystemResult.Ok, d.TryCloseRoom(1, true));
            Assert.Equal(SystemResult.OnCooldown, d.TryCloseRoom(0, true));
            d.Tick(29.9f);
            Assert.Equal(SystemResult.OnCooldown, d.TryCloseRoom(0, true));
            d.Tick(0.2f);
            Assert.Equal(SystemResult.Ok, d.TryCloseRoom(0, true));
        }

        [Fact]
        public void Command_doors_stay_closed_until_opened_and_never_pause_cooldown()
        {
            var d = Command();
            d.TryCloseRoom(0, true);
            d.Tick(60f);
            Assert.False(d.IsDoorOpen(0));
            Assert.False(d.IsActive);
            Assert.Equal(SystemResult.NotEligible, d.TryOpenDoor(0, false));
            Assert.Equal(SystemResult.Ok, d.TryOpenDoor(0, true));
            Assert.Equal(SystemResult.NotActive, d.TryOpenDoor(0, true)); // already open
            Assert.Equal(SystemResult.UnknownDoor, d.TryOpenDoor(9, true));
        }

        [Fact]
        public void Auto_doors_reopen_after_ten_seconds_and_count_as_active_meanwhile()
        {
            var d = Auto();
            d.TryCloseRoom(0, true);
            Assert.True(d.IsActive);
            d.Tick(9.9f);
            Assert.False(d.IsDoorOpen(0));
            d.Tick(0.2f);
            Assert.True(d.IsDoorOpen(0));
            Assert.False(d.IsActive);
            Assert.Equal(SystemResult.UnavailableOnMap, d.TryOpenDoor(0, true));
        }

        [Fact]
        public void Auto_door_cooldown_is_thirty_seconds_from_closing()
        {
            var d = Auto();
            d.TryCloseRoom(0, true);
            d.Tick(29.9f);
            Assert.Equal(SystemResult.OnCooldown, d.TryCloseRoom(0, true));
            d.Tick(0.2f);
            Assert.Equal(SystemResult.Ok, d.TryCloseRoom(0, true));
        }

        [Fact]
        public void Unknown_room_and_no_door_maps_are_rejected()
        {
            Assert.Equal(SystemResult.UnknownDoor, Command().TryCloseRoom(7, true));
            Assert.Equal(SystemResult.UnavailableOnMap,
                new ShipSystems(MapProfile.Medium, new SeededRandom(1)).RequestCloseDoors(0, true));
        }
    }
}
