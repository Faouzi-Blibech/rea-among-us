using Sim.Options;
using Sim.Options.Presets;
using Xunit;

namespace Sim.Tests.Presets
{
    public class OptionPresetsTests
    {
        [Theory]
        [InlineData(4, 45f, 1, 1)]
        [InlineData(5, 30f, 1, 1)]
        [InlineData(6, 15f, 1, 1)]
        [InlineData(7, 35f, 2, 2)]
        [InlineData(8, 30f, 2, 2)]
        [InlineData(9, 25f, 2, 3)]
        [InlineData(10, 20f, 2, 3)]
        [InlineData(11, 20f, 2, 3)]
        [InlineData(12, 20f, 3, 3)]
        [InlineData(15, 20f, 3, 3)]
        public void Tables(int players, float kill, int rec, int max)
        {
            Assert.Equal(kill, OptionPresets.RecommendedKillCooldown(players));
            Assert.Equal(rec, OptionPresets.RecommendedImpostors(players));
            Assert.Equal(max, OptionPresets.MaxImpostors(players));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(3, 0)]
        [InlineData(16, 3)]
        [InlineData(100, 3)]
        public void MaxImpostors_EdgeCases(int players, int expected)
            => Assert.Equal(expected, OptionPresets.MaxImpostors(players));

        [Theory]
        [InlineData(0, 10, 1)]
        [InlineData(-2, 10, 1)]
        [InlineData(3, 6, 1)]
        [InlineData(2, 7, 2)]
        [InlineData(3, 8, 2)]
        [InlineData(3, 20, 3)]
        [InlineData(2, 3, 0)]
        public void AdjustedImpostors(int configured, int players, int expected)
            => Assert.Equal(expected, OptionPresets.AdjustedImpostors(configured, players));

        [Theory]
        [InlineData(2, 4)]
        [InlineData(20, 15)]
        [InlineData(10, 10)]
        public void ClampPlayers(int input, int expected)
            => Assert.Equal(expected, OptionPresets.ClampPlayers(input));

        [Fact]
        public void Apply_Offline_SetsImpostorsAndZeroEmergencyCooldown()
        {
            var o = new GameOptions { NumImpostors = 1, MaxPlayers = 12, GhostsDoTasks = false, KillCooldown = 99f };
            OptionPresets.Apply(o, 12, false);
            Assert.Equal(3, o.NumImpostors);
            Assert.Equal(0f, o.EmergencyCooldown);
            Assert.Equal(20f, o.KillCooldown);
            Assert.Equal(12, o.MaxPlayers);
            Assert.False(o.GhostsDoTasks);
        }

        [Fact]
        public void Apply_Online_KeepsImpostorsAndSetsCooldown15()
        {
            var o = new GameOptions { NumImpostors = 2, PlayerSpeedMod = 2f, ImpostorLightMod = 3f, TaskBarMode = TaskBarMode.Invisible };
            OptionPresets.Apply(o, 4, true);
            Assert.Equal(2, o.NumImpostors);
            Assert.Equal(15f, o.EmergencyCooldown);
            Assert.Equal(45f, o.KillCooldown);
            Assert.Equal(1f, o.PlayerSpeedMod);
            Assert.Equal(1.5f, o.ImpostorLightMod);
            Assert.Equal(TaskBarMode.Normal, o.TaskBarMode);
            Assert.Equal(1, o.NumCommonTasks);
            Assert.Equal(1, o.NumLongTasks);
            Assert.Equal(2, o.NumShortTasks);
            Assert.Equal(1, o.KillDistance);
        }
    }
}
