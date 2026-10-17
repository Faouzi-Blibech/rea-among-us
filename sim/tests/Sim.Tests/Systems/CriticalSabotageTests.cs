using Sim.Core;
using Sim.Systems;

namespace Sim.Tests.Systems
{
    /// <summary>Returns queued values, then 0.</summary>
    internal sealed class ScriptedRandom : IRandom
    {
        private readonly Queue<int> _values;
        public ScriptedRandom(params int[] values) { _values = new Queue<int>(values); }
        public int Next(int maxExclusive) => _values.Count > 0 ? _values.Dequeue() % maxExclusive : 0;
    }

    public class CriticalSabotageTests
    {
        [Theory]
        [InlineData(30f)]
        [InlineData(45f)]
        [InlineData(60f)]
        public void Countdown_starts_at_duration_and_expires_below_zero(float duration)
        {
            var r = new HoldMeltdown(duration);
            Assert.True(r.IsStopped);
            Assert.Equal(SystemResult.Ok, r.Start());
            Assert.Equal(duration, r.Countdown);
            r.Tick(duration);
            Assert.False(r.CountdownExpired); // exactly zero is not yet below zero
            r.Tick(0.01f);
            Assert.True(r.CountdownExpired);
        }

        [Fact]
        public void Start_while_active_is_ignored()
        {
            var r = new HoldMeltdown(30f);
            r.Start();
            r.Tick(5f);
            Assert.Equal(SystemResult.AlreadyActive, r.Start());
            Assert.Equal(25f, r.Countdown);
        }

        [Fact]
        public void Reactor_needs_two_different_consoles_at_once()
        {
            var r = new HoldMeltdown(30f);
            r.Start();
            Assert.Equal(SystemResult.Ok, r.Hold(1, 0, true));
            Assert.True(r.IsActive);
            // Same player switching consoles does not count twice.
            Assert.Equal(SystemResult.Ok, r.Hold(1, 1, true));
            Assert.True(r.IsActive);
            Assert.Equal(1, r.UserCount);
            // Two players on the same console do not fix it either.
            r.Hold(2, 1, true);
            Assert.True(r.IsActive);
            // Second console held by a different player fixes it.
            r.Hold(3, 0, true);
            Assert.True(r.IsStopped);
        }

        [Fact]
        public void Reactor_release_breaks_the_pair()
        {
            var r = new HoldMeltdown(30f);
            r.Start();
            r.Hold(1, 0, true);
            r.Release(1); // holder died / let go
            Assert.Equal(SystemResult.Ok, r.Hold(2, 1, true));
            Assert.True(r.IsActive);
        }

        [Theory]
        [InlineData(2, true, SystemResult.InvalidConsole)]
        [InlineData(-1, true, SystemResult.InvalidConsole)]
        [InlineData(0, false, SystemResult.NotEligible)]
        public void Reactor_hold_validation(int console, bool eligible, SystemResult expected)
        {
            var r = new HoldMeltdown(30f);
            r.Start();
            Assert.Equal(expected, r.Hold(1, console, eligible));
        }

        [Fact]
        public void Reactor_hold_when_idle_is_rejected()
        {
            Assert.Equal(SystemResult.NotActive, new HoldMeltdown(30f).Hold(1, 0, true));
        }

        [Fact]
        public void Oxygen_progress_persists_and_needs_the_server_code()
        {
            var o = new CodeMeltdown(30f, new ScriptedRandom(12345));
            o.Start();
            Assert.Equal(12345, o.TargetCode);
            Assert.Equal(SystemResult.WrongCode, o.EnterCode(0, 1, true));
            Assert.Equal(SystemResult.Ok, o.EnterCode(0, 12345, true));
            o.Tick(20f); // progress is not time-limited
            Assert.True(o.IsActive);
            // Same console twice does not count as two.
            o.EnterCode(0, 12345, true);
            Assert.True(o.IsActive);
            o.EnterCode(1, 12345, true);
            Assert.True(o.IsStopped);
        }

        [Fact]
        public void Oxygen_restart_forgets_progress()
        {
            var o = new CodeMeltdown(30f, new ScriptedRandom(5, 6));
            o.Start();
            o.EnterCode(0, 5, true);
            o.Clear();
            o.Start();
            Assert.Equal(0, o.CompletedCount);
            Assert.Equal(6, o.TargetCode);
        }

        [Fact]
        public void Heli_needs_both_codes_inside_one_window()
        {
            // Codes: first window 111, second window 222.
            var h = new WindowMeltdown(90f, new ScriptedRandom(111, 222));
            h.Start();
            Assert.Equal(90f, h.Countdown);
            Assert.Equal(SystemResult.Ok, h.EnterCode(0, 111, true));
            h.Tick(9.9f); // still in the window (entry refreshed it)
            Assert.Equal(SystemResult.Ok, h.EnterCode(1, 111, true));
            Assert.True(h.IsStopped); // reset to idle on fix (deviation from original)
        }

        [Fact]
        public void Heli_window_expiry_changes_code_and_forgets_entries()
        {
            var h = new WindowMeltdown(90f, new ScriptedRandom(111, 222));
            h.Start();
            h.EnterCode(0, 111, true);
            h.Tick(10.1f);
            Assert.Equal(222, h.TargetCode);
            Assert.Equal(SystemResult.WrongCode, h.EnterCode(1, 111, true));
            Assert.Equal(SystemResult.Ok, h.EnterCode(1, 222, true));
            Assert.True(h.IsActive); // console 0 was forgotten
            Assert.Equal(90f - 10.1f, h.Countdown, 3);
        }

        [Fact]
        public void Heli_countdown_expires()
        {
            var h = new WindowMeltdown(90f, new ScriptedRandom(1));
            h.Start();
            for (int i = 0; i < 91; i++) h.Tick(1f);
            Assert.True(h.CountdownExpired);
        }
    }
}
