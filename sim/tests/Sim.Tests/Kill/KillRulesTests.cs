using System.Collections.Generic;
using Sim.Core;
using Sim.Options;
using Sim.Rules.Kill;
using Xunit;

namespace Sim.Tests.Kill
{
    internal sealed class FakeLos : ILineOfSight
    {
        public bool Open = true;
        public bool Clear(Vec2 a, Vec2 b) => Open;
    }

    internal static class Make
    {
        public static PlayerState Imp(byte id = 0, float x = 0f)
        {
            var p = new PlayerState(id) { Role = RoleId.Impostor, Team = Team.Impostor, Position = new Vec2(x, 0) };
            return p;
        }

        public static PlayerState Crew(byte id, float x)
        {
            return new PlayerState(id) { Role = RoleId.Crew, Team = Team.Crew, Position = new Vec2(x, 0) };
        }
    }

    public class KillValidationTests
    {
        private static readonly KillContext Ok = new KillContext(false, false, false);

        public static IEnumerable<object[]> Cases()
        {
            yield return new object[] { "ok", KillRejectReason.None };
            yield return new object[] { "gameOver", KillRejectReason.GameOver };
            yield return new object[] { "killerDead", KillRejectReason.KillerDead };
            yield return new object[] { "crewKiller", KillRejectReason.KillerNotImpostor };
            yield return new object[] { "ghostKiller", KillRejectReason.KillerDead };
            yield return new object[] { "killerDisc", KillRejectReason.KillerDisconnected };
            yield return new object[] { "cooldown", KillRejectReason.OnCooldown };
            yield return new object[] { "noTarget", KillRejectReason.TargetMissing };
            yield return new object[] { "targetDead", KillRejectReason.TargetDead };
            yield return new object[] { "targetVent", KillRejectReason.TargetInVent };
            yield return new object[] { "targetBusy", KillRejectReason.TargetBusy };
            yield return new object[] { "meeting", KillRejectReason.MeetingActive };
            yield return new object[] { "far", KillRejectReason.OutOfRange };
            yield return new object[] { "wall", KillRejectReason.WallBetween };
            yield return new object[] { "targetImp", KillRejectReason.TargetNotKillable };
            yield return new object[] { "exactRange", KillRejectReason.None };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Validate(string scenario, KillRejectReason expected)
        {
            var opt = new GameOptions(); // range 1.8
            var los = new FakeLos();
            PlayerState k = Make.Imp();
            PlayerState? t = Make.Crew(1, 1f);
            var ctx = Ok;
            switch (scenario)
            {
                case "gameOver": ctx = new KillContext(true, false, false); break;
                case "killerDead": k.IsDead = true; break;
                case "crewKiller": k = Make.Crew(0, 0); break;
                case "ghostKiller": k.Role = RoleId.ImpostorGhost; k.IsDead = true; break;
                case "killerDisc": k.Disconnected = true; break;
                case "cooldown": k.KillTimer = 0.1f; break;
                case "noTarget": t = null; break;
                case "targetDead": t.IsDead = true; break;
                case "targetVent": t.InVent = true; break;
                case "targetBusy": ctx = new KillContext(false, false, true); break;
                case "meeting": ctx = new KillContext(false, true, false); break;
                case "far": t.Position = new Vec2(1.81f, 0); break;
                case "wall": los.Open = false; break;
                case "targetImp": t = Make.Imp(1, 1f); break;
                case "exactRange": t.Position = new Vec2(1.8f, 0); break;
            }
            Assert.Equal(expected, KillValidator.Validate(k, t, opt, los, ctx));
        }
    }

    public class KillResolveTests
    {
        private static readonly KillContext Ok = new KillContext(false, false, false);

        [Fact]
        public void SuccessKillsAndResetsTimerWithBody()
        {
            var opt = new GameOptions { KillCooldown = 20f };
            var k = Make.Imp();
            var t = Make.Crew(3, 1f);
            KillOutcome o = KillResolver.TryKill(k, t, opt, new FakeLos(), Ok);
            Assert.Equal(KillResult.Killed, o.Result);
            Assert.True(t.IsDead);
            Assert.Equal(20f, k.KillTimer);
            Assert.Equal(DeathReason.Kill, o.Death);
            Assert.Equal(3, o.Body!.Value.VictimId);
            Assert.Equal(new Vec2(1f, 0), o.Body.Value.Position);
        }

        [Fact]
        public void ShieldBlocksKillConsumesShieldAndHalvesTimer()
        {
            var opt = new GameOptions { KillCooldown = 20f };
            var k = Make.Imp();
            var t = Make.Crew(3, 1f);
            t.ProtectedBy = 9;
            KillOutcome o = KillResolver.TryKill(k, t, opt, new FakeLos(), Ok);
            Assert.Equal(KillResult.BlockedByShield, o.Result);
            Assert.False(t.IsDead);
            Assert.Null(t.ProtectedBy);
            Assert.Equal(10f, k.KillTimer);
            Assert.Null(o.Body);
        }

        [Fact]
        public void RejectedKillChangesNothing()
        {
            var opt = new GameOptions();
            var k = Make.Imp();
            k.KillTimer = 5f;
            var t = Make.Crew(1, 1f);
            KillOutcome o = KillResolver.TryKill(k, t, opt, new FakeLos(), Ok);
            Assert.Equal(KillResult.Rejected, o.Result);
            Assert.Equal(KillRejectReason.OnCooldown, o.Reason);
            Assert.False(t.IsDead);
            Assert.Equal(5f, k.KillTimer);
        }
    }

    public class KillTimerTests
    {
        [Theory]
        [InlineData(15f, 10f)]
        [InlineData(5f, 5f)]
        public void RoundStartIsMinOfTenAndCooldown(float cd, float expected)
        {
            var k = Make.Imp();
            KillTimerRules.ResetForRoundStart(k, new GameOptions { KillCooldown = cd });
            Assert.Equal(expected, k.KillTimer);
        }

        [Fact]
        public void AfterMeetingIsFullCooldown()
        {
            var k = Make.Imp();
            KillTimerRules.ResetAfterMeeting(k, new GameOptions { KillCooldown = 30f });
            Assert.Equal(30f, k.KillTimer);
        }

        [Theory]
        [InlineData(-5f, 0f)]
        [InlineData(99f, 15f)]
        [InlineData(7f, 7f)]
        public void SetClampsToZeroAndCooldown(float v, float expected)
        {
            var k = Make.Imp();
            KillTimerRules.Set(k, v, new GameOptions { KillCooldown = 15f });
            Assert.Equal(expected, k.KillTimer);
        }

        [Fact]
        public void ZeroCooldownDisablesTimer()
        {
            var k = Make.Imp();
            k.KillTimer = 3f;
            KillTimerRules.Set(k, 10f, new GameOptions { KillCooldown = 0f });
            Assert.Equal(3f, k.KillTimer);
        }

        [Theory]
        [InlineData(false, false, 4f)]
        [InlineData(true, false, 5f)]  // meeting
        [InlineData(false, true, 5f)]  // dead
        public void TickOnlyWhenAllowed(bool meeting, bool dead, float expected)
        {
            var k = Make.Imp();
            k.KillTimer = 5f;
            k.IsDead = dead;
            KillTimerRules.Tick(k, 1f, new GameOptions(), meeting);
            Assert.Equal(expected, k.KillTimer);
        }

        [Fact]
        public void TickDoesNotGoBelowZero()
        {
            var k = Make.Imp();
            k.KillTimer = 0.5f;
            KillTimerRules.Tick(k, 2f, new GameOptions(), false);
            Assert.Equal(0f, k.KillTimer);
        }

        [Fact]
        public void CrewTimerNeverChanges()
        {
            var c = Make.Crew(1, 0);
            KillTimerRules.Set(c, 5f, new GameOptions());
            Assert.Equal(0f, c.KillTimer);
        }
    }

    public class TargetSelectionTests
    {
        [Fact]
        public void ClosestWinsAndTiesBreakById()
        {
            var k = Make.Imp();
            var far = Make.Crew(1, 1.5f);
            var near2 = Make.Crew(5, 0.5f);
            var near1 = Make.Crew(4, -0.5f);
            var t = TargetSelector.FindKillTarget(k, new[] { k, far, near2, near1 }, new GameOptions(), new FakeLos());
            Assert.Equal(4, t!.Id);
        }

        [Fact]
        public void SkipsInvalidAndOutOfRangeAndImpostors()
        {
            var k = Make.Imp();
            var dead = Make.Crew(1, 0.2f); dead.IsDead = true;
            var vent = Make.Crew(2, 0.3f); vent.InVent = true;
            var ally = Make.Imp(3, 0.4f);
            var out1 = Make.Crew(4, 1.9f);
            var ok = Make.Crew(5, 1.8f);
            var t = TargetSelector.FindKillTarget(k, new[] { k, dead, vent, ally, out1, ok }, new GameOptions(), new FakeLos());
            Assert.Equal(5, t!.Id);
        }

        [Fact]
        public void WallsBlockUnlessIgnored()
        {
            var k = Make.Imp();
            var c = Make.Crew(1, 1f);
            var los = new FakeLos { Open = false };
            Assert.Null(TargetSelector.FindKillTarget(k, new[] { c }, new GameOptions(), los));
            Assert.Single(TargetSelector.InRangeSorted(k, new[] { c }, 1.8f, los, true, true));
        }
    }
}
