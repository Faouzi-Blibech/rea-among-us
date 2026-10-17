using System.Collections.Generic;
using Sim.Core;
using Sim.Roles;
using Xunit;

namespace Sim.Tests.Kill
{
    internal sealed class FixedRandom : IRandom
    {
        private readonly int _v;
        public FixedRandom(int v) { _v = v; }
        public int Next(int maxExclusive) => _v;
    }

    public class RoleTableTests
    {
        [Theory]
        [InlineData(RoleId.Crew, Team.Crew, false, false, RoleId.CrewGhost)]
        [InlineData(RoleId.Impostor, Team.Impostor, true, true, RoleId.ImpostorGhost)]
        [InlineData(RoleId.Mechanic, Team.Crew, false, true, RoleId.CrewGhost)]
        [InlineData(RoleId.Disguiser, Team.Impostor, true, true, RoleId.ImpostorGhost)]
        public void Flags(RoleId id, Team team, bool canKill, bool canVent, RoleId ghost)
        {
            RoleDefinition d = RoleTable.Get(id);
            Assert.Equal(team, d.Team);
            Assert.Equal(canKill, d.CanKill);
            Assert.Equal(canVent, d.CanVent);
            Assert.Equal(ghost, d.GhostRole);
        }

        [Fact]
        public void AbilityDefaultsFollowSpec()
        {
            Assert.Equal(30f, RoleTable.Get(RoleId.Mechanic).AbilityCooldown);
            Assert.Equal(15f, RoleTable.Get(RoleId.Mechanic).AbilityDuration);
            Assert.Equal(60f, RoleTable.Get(RoleId.GuardianGhost).AbilityCooldown);
            Assert.Equal(10f, RoleTable.Get(RoleId.GuardianGhost).AbilityDuration);
        }
    }

    public class VentTests
    {
        [Theory]
        [InlineData(0.75f, true, RoleId.Impostor, false, VentRejectReason.None)]
        [InlineData(0.76f, true, RoleId.Impostor, false, VentRejectReason.TooFar)]
        [InlineData(0.5f, false, RoleId.Impostor, false, VentRejectReason.WallBetween)]
        [InlineData(0.5f, true, RoleId.Crew, false, VentRejectReason.RoleCannotVent)]
        [InlineData(0.5f, true, RoleId.Impostor, true, VentRejectReason.BeingCleaned)]
        public void CanUse(float dist, bool open, RoleId role, bool cleaned, VentRejectReason expected)
        {
            var p = new PlayerState(1) { Role = role, Position = new Vec2(dist, 0) };
            var los = new FakeLos { Open = open };
            Assert.Equal(expected, VentRules.CanUse(p, Vec2.Zero, los, false, cleaned, null));
        }

        [Fact]
        public void DeadAndMeetingBlockVenting()
        {
            var p = new PlayerState(1) { Role = RoleId.Impostor, IsDead = true };
            Assert.Equal(VentRejectReason.Dead, VentRules.CanUse(p, Vec2.Zero, new FakeLos(), false, false, null));
            p.IsDead = false;
            Assert.Equal(VentRejectReason.MeetingActive, VentRules.CanUse(p, Vec2.Zero, new FakeLos(), true, false, null));
        }

        [Fact]
        public void MechanicCooldownBlocksEntryButNotExit()
        {
            var m = MechanicVent.FromDefaults();
            m.OnEnter();
            m.OnExit(); // cooldown 30
            var p = new PlayerState(1) { Role = RoleId.Mechanic };
            Assert.Equal(VentRejectReason.OnCooldown, VentRules.CanUse(p, Vec2.Zero, new FakeLos(), false, false, m));
            p.InVent = true;
            Assert.Equal(VentRejectReason.None, VentRules.CanUse(p, Vec2.Zero, new FakeLos(), false, false, m));
            m.Tick(30f, false, false);
            p.InVent = false;
            Assert.Equal(VentRejectReason.None, VentRules.CanUse(p, Vec2.Zero, new FakeLos(), false, false, m));
        }

        [Fact]
        public void MechanicForcedOutOnTimeoutOrComms()
        {
            var m = new MechanicVent(30f, 15f);
            m.OnEnter();
            Assert.False(m.Tick(14f, true, false));
            Assert.True(m.Tick(1.5f, true, false));

            var m2 = new MechanicVent(30f, 15f);
            m2.OnEnter();
            Assert.True(m2.Tick(0.1f, true, true));
        }

        [Fact]
        public void MechanicZeroMaxTimeIsUnlimited()
        {
            var m = new MechanicVent(30f, 0f);
            m.OnEnter();
            Assert.False(m.Tick(1000f, true, false));
        }

        [Fact]
        public void MeetingEndClearsMechanicCooldown()
        {
            var m = new MechanicVent(30f, 15f);
            m.OnExit();
            m.OnMeetingEnd();
            Assert.True(m.CanEnter);
        }

        [Fact]
        public void GraphTravelFollowsNeighbours()
        {
            var g = new VentGraph();
            g.AddVent(1, new Vec2(0, 0), new[] { 2 });
            g.AddVent(2, new Vec2(5, 5), new[] { 1, 3 });
            g.AddVent(3, new Vec2(9, 9), new[] { 2 });
            var p = new PlayerState(1) { Role = RoleId.Impostor, InVent = true };
            Assert.Equal(VentRejectReason.NotConnected, VentRules.TryTravel(p, g, 1, 3));
            Assert.Equal(VentRejectReason.None, VentRules.TryTravel(p, g, 1, 2));
            Assert.Equal(new Vec2(5, 5), p.Position);
            p.InVent = false;
            Assert.Equal(VentRejectReason.NotInVent, VentRules.TryTravel(p, g, 2, 3));
        }
    }

    public class DisguiserTests
    {
        [Fact]
        public void CooldownStartsOnRevertAndGatesNextUse()
        {
            var a = new DisguiserAbility(30f, 10f);
            var user = new PlayerState(0) { Role = RoleId.Disguiser, Team = Team.Impostor };
            var t = new PlayerState(1);
            Assert.Equal(AbilityRejectReason.None, a.TryDisguise(user, t, false));
            Assert.Equal(AbilityRejectReason.AlreadyActive, a.TryDisguise(user, t, false));
            a.Tick(10f, false);
            Assert.False(a.IsDisguised);
            Assert.Equal(AbilityRejectReason.OnCooldown, a.TryDisguise(user, t, false));
            a.Tick(30f, false);
            Assert.Equal(AbilityRejectReason.None, a.TryDisguise(user, t, false));
        }

        [Fact]
        public void ZeroDurationNeverExpiresAndMeetingPausesTimers()
        {
            var a = new DisguiserAbility(30f, 0f);
            var user = new PlayerState(0) { Role = RoleId.Disguiser };
            a.TryDisguise(user, new PlayerState(1), false);
            a.Tick(500f, false);
            Assert.True(a.IsDisguised);
            a.Revert();
            a.Tick(100f, true);
            Assert.Equal(30f, a.CooldownRemaining);
        }

        [Theory]
        [InlineData(RoleId.Impostor, false, AbilityRejectReason.WrongRole)]
        [InlineData(RoleId.Disguiser, true, AbilityRejectReason.Dead)]
        public void RoleAndAliveChecks(RoleId role, bool dead, AbilityRejectReason expected)
        {
            var a = new DisguiserAbility(30f, 10f);
            var user = new PlayerState(0) { Role = role, IsDead = dead };
            Assert.Equal(expected, a.TryDisguise(user, new PlayerState(1), false));
        }
    }

    public class GuardianTests
    {
        private static PlayerState Ghost() =>
            new PlayerState(9) { Role = RoleId.GuardianGhost, Team = Team.Crew, IsDead = true };

        [Fact]
        public void ProtectSetsShieldAndCooldown()
        {
            var a = new GuardianAbility(60f, 10f);
            var t = new PlayerState(1);
            Assert.Equal(AbilityRejectReason.None, a.TryProtect(Ghost(), t, false));
            Assert.Equal((byte?)9, t.ProtectedBy);
            Assert.Equal(AbilityRejectReason.OnCooldown, a.TryProtect(Ghost(), new PlayerState(2), false));
        }

        [Fact]
        public void ShieldExpiresByDurationAndAtMeeting()
        {
            var a = new GuardianAbility(60f, 10f);
            var t = new PlayerState(1);
            a.TryProtect(Ghost(), t, false);
            a.Tick(9f, false);
            Assert.NotNull(t.ProtectedBy);
            a.Tick(1f, false);
            Assert.Null(t.ProtectedBy);

            a.OnMeetingEnd();
            a.TryProtect(Ghost(), t, false);
            a.OnMeetingStart();
            Assert.Null(t.ProtectedBy);
            a.OnMeetingEnd();
            Assert.Equal(0f, a.CooldownRemaining);
        }

        [Theory]
        [InlineData(false, false, false)] // living guardian
        [InlineData(true, true, false)]   // dead target
        [InlineData(true, false, true)]   // disconnected target
        public void InvalidUsers(bool guardianDead, bool targetDead, bool targetDisc)
        {
            var g = Ghost(); g.IsDead = guardianDead;
            var t = new PlayerState(1) { IsDead = targetDead, Disconnected = targetDisc };
            var a = new GuardianAbility(60f, 10f);
            Assert.NotEqual(AbilityRejectReason.None, a.TryProtect(g, t, false));
        }
    }

    public class GhostRoleTests
    {
        private static List<PlayerState> Players(params PlayerState[] p) => new List<PlayerState>(p);

        [Theory]
        [InlineData(49, RoleId.GuardianGhost)]
        [InlineData(50, RoleId.CrewGhost)]
        [InlineData(100, RoleId.CrewGhost)]
        public void ChanceRollIsStrictlyLess(int roll, RoleId expected)
        {
            var dead = new PlayerState(1) { Role = RoleId.Crew, IsDead = true };
            RoleId r = GhostRoleAssigner.Assign(dead, Players(dead), GhostRoleAssigner.DefaultCrewCandidates, new FixedRandom(roll));
            Assert.Equal(expected, r);
            Assert.Equal(expected, dead.Role);
        }

        [Fact]
        public void PerGameCapSkipsCandidate()
        {
            var existing = new PlayerState(2) { Role = RoleId.GuardianGhost, IsDead = true };
            var dead = new PlayerState(1) { Role = RoleId.Crew, IsDead = true };
            RoleId r = GhostRoleAssigner.Assign(dead, Players(existing, dead), GhostRoleAssigner.DefaultCrewCandidates, new FixedRandom(0));
            Assert.Equal(RoleId.CrewGhost, r);
        }

        [Fact]
        public void ImpostorsAlwaysGetImpostorGhost()
        {
            var dead = new PlayerState(1) { Role = RoleId.Disguiser, Team = Team.Impostor, IsDead = true };
            RoleId r = GhostRoleAssigner.Assign(dead, Players(dead), GhostRoleAssigner.DefaultCrewCandidates, new FixedRandom(0));
            Assert.Equal(RoleId.ImpostorGhost, r);
        }

        [Fact]
        public void AlreadyGhostUnchanged()
        {
            var dead = new PlayerState(1) { Role = RoleId.CrewGhost, IsDead = true };
            Assert.Equal(RoleId.CrewGhost, GhostRoleAssigner.Assign(dead, Players(dead), GhostRoleAssigner.DefaultCrewCandidates, new FixedRandom(0)));
        }

        [Fact]
        public void HundredPercentFailsOnlyOnRollOneHundred()
        {
            var opts = new[] { new GhostRoleOption(RoleId.GuardianGhost, 5, 100) };
            var d1 = new PlayerState(1) { Role = RoleId.Crew };
            var d2 = new PlayerState(2) { Role = RoleId.Crew };
            Assert.Equal(RoleId.GuardianGhost, GhostRoleAssigner.Assign(d1, Players(d1), opts, new FixedRandom(99)));
            Assert.Equal(RoleId.CrewGhost, GhostRoleAssigner.Assign(d2, Players(d2), opts, new FixedRandom(100)));
        }
    }
}
