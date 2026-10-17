using System.Collections.Generic;
using System.Linq;
using Sim.Core;
using Sim.Options;
using Sim.Rules.Assign;

namespace Sim.Tests.Assign
{
    public class AssignTests
    {
        private static List<PlayerState> Players(int n) =>
            Enumerable.Range(0, n).Select(i => new PlayerState((byte)i)).ToList();

        [Theory]
        [InlineData(1, 3, 0)]   // below 4 players: cap 0
        [InlineData(2, 4, 1)]
        [InlineData(3, 6, 1)]
        [InlineData(3, 7, 2)]
        [InlineData(3, 9, 3)]
        [InlineData(3, 15, 3)]
        [InlineData(5, 40, 3)]  // outside table: cap 3
        [InlineData(0, 10, 1)]  // configured below 1 -> 1
        [InlineData(-2, 5, 1)]
        public void AdjustedImpostorCount(int configured, int players, int expected) =>
            Assert.Equal(expected, RoleAssigner.AdjustedImpostorCount(configured, players));

        [Theory]
        [InlineData(1, 10, 1)]
        [InlineData(2, 10, 2)]
        [InlineData(3, 5, 1)]
        public void ImpostorsAssignedFirstAndCapped(int configured, int players, int expectedImps)
        {
            var ps = Players(players);
            RoleAssigner.Assign(ps, configured, new RoleSetting[0], new SeededRandom(1));
            Assert.Equal(expectedImps, ps.Count(p => p.Team == Team.Impostor && p.Role == RoleId.Impostor));
            Assert.Equal(players - expectedImps, ps.Count(p => p.Team == Team.Crew && p.Role == RoleId.Crew));
        }

        [Fact]
        public void GuaranteedRolesAlwaysAssigned()
        {
            for (int seed = 0; seed < 30; seed++)
            {
                var ps = Players(8);
                var settings = new[]
                {
                    new RoleSetting(RoleId.Mechanic, Team.Crew, 2, 100),
                    new RoleSetting(RoleId.Disguiser, Team.Impostor, 1, 100),
                };
                RoleAssigner.Assign(ps, 2, settings, new SeededRandom(seed));
                Assert.Equal(2, ps.Count(p => p.Role == RoleId.Mechanic));
                Assert.Equal(1, ps.Count(p => p.Role == RoleId.Disguiser));
                Assert.Equal(1, ps.Count(p => p.Role == RoleId.Impostor));
            }
        }

        [Fact]
        public void GuaranteedImpostorRoleWinsSlotsUnderTightCap()
        {
            var ps = Players(8);
            var settings = new[] { new RoleSetting(RoleId.Disguiser, Team.Impostor, 3, 100) };
            RoleAssigner.Assign(ps, 1, settings, new SeededRandom(5));
            Assert.Equal(1, ps.Count(p => p.Team == Team.Impostor));
            Assert.Equal(1, ps.Count(p => p.Role == RoleId.Disguiser));
        }

        [Fact]
        public void ZeroChanceNeverAndMidChanceRolledPerCopy()
        {
            int never = 0, some = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var ps = Players(10);
                var settings = new[]
                {
                    new RoleSetting(RoleId.Mechanic, Team.Crew, 1, 0),
                    new RoleSetting(RoleId.GuardianGhost, Team.Crew, 1, 50),
                };
                RoleAssigner.Assign(ps, 1, settings, new SeededRandom(seed));
                never += ps.Count(p => p.Role == RoleId.Mechanic);
                some += ps.Count(p => p.Role == RoleId.GuardianGhost);
            }
            Assert.Equal(0, never);
            Assert.InRange(some, 60, 140);
        }

        [Fact]
        public void RoleDistributionIsUniformOverPlayers()
        {
            var hits = new int[6];
            for (int seed = 0; seed < 600; seed++)
            {
                var ps = Players(6);
                RoleAssigner.Assign(ps, 1, new RoleSetting[0], new SeededRandom(seed));
                hits[ps.First(p => p.Team == Team.Impostor).Id]++;
            }
            Assert.All(hits, h => Assert.InRange(h, 60, 140));
        }

        // ----- tasks -----

        private static List<TaskDefinition> Catalog() => new List<TaskDefinition>
        {
            new TaskDefinition(1, 1, TaskCategory.Common), new TaskDefinition(2, 1, TaskCategory.Common),
            new TaskDefinition(10, 3, TaskCategory.Long), new TaskDefinition(11, 3, TaskCategory.Long),
            new TaskDefinition(12, 3, TaskCategory.Long),
            new TaskDefinition(20, 1, TaskCategory.Short), new TaskDefinition(21, 1, TaskCategory.Short),
            new TaskDefinition(22, 1, TaskCategory.Short), new TaskDefinition(23, 1, TaskCategory.Short),
            new TaskDefinition(24, 1, TaskCategory.Short),
        };

        [Theory]
        [InlineData(1, 1, 2)]
        [InlineData(2, 2, 3)]
        [InlineData(0, 0, 0)]
        public void ListShapeAndIds(int common, int lng, int shrt)
        {
            for (int seed = 0; seed < 20; seed++)
            {
                var ps = Players(5);
                var opt = new GameOptions { NumCommonTasks = common, NumLongTasks = lng, NumShortTasks = shrt };
                TaskAssigner.Assign(ps, Catalog(), opt, new SeededRandom(seed));
                int expected = common + lng + shrt == 0 ? 1 : common + lng + shrt;
                foreach (var p in ps)
                {
                    Assert.Equal(expected, p.Tasks.Count);
                    Assert.Equal(Enumerable.Range(0, expected), p.Tasks.Select(t => t.Id));
                    Assert.Equal(p.Tasks.Count, p.Tasks.Select(t => t.TypeId).Distinct().Count());
                }
                // everyone shares the commons
                Assert.All(ps, p => Assert.Equal(ps[0].Tasks.Take(common).Select(t => t.TypeId), p.Tasks.Take(common).Select(t => t.TypeId)));
                // order: common, long, short
                var first = ps[0].Tasks.Select(t => t.TypeId).ToList();
                for (int i = 0; i < common && i < first.Count; i++) Assert.True(first[i] < 10);
                for (int i = common; i < common + lng; i++) Assert.InRange(first[i], 10, 12);
            }
        }

        [Fact]
        public void LongTasksRoundRobinAcrossPlayers()
        {
            var ps = Players(3);
            var opt = new GameOptions { NumCommonTasks = 0, NumLongTasks = 1, NumShortTasks = 0 };
            TaskAssigner.Assign(ps, Catalog(), opt, new SeededRandom(3));
            Assert.Equal(3, ps.Select(p => p.Tasks[0].TypeId).Distinct().Count());
        }

        [Fact]
        public void SmallPoolWrapsAndTerminates()
        {
            var cat = new List<TaskDefinition>
            {
                new TaskDefinition(1, 1, TaskCategory.Short), new TaskDefinition(1, 1, TaskCategory.Short),
            };
            var ps = Players(4);
            var opt = new GameOptions { NumCommonTasks = 0, NumLongTasks = 0, NumShortTasks = 3 };
            TaskAssigner.Assign(ps, cat, opt, new SeededRandom(1)); // must return despite impossible request
            Assert.All(ps, p => Assert.True(p.Tasks.Count >= 1));
        }

        [Fact]
        public void EmptyCatalogGivesNoTasks()
        {
            var ps = Players(2);
            TaskAssigner.Assign(ps, new List<TaskDefinition>(), new GameOptions(), new SeededRandom(1));
            Assert.All(ps, p => Assert.Empty(p.Tasks));
        }
    }
}
