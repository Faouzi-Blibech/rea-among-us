using Sim.Options;
using Sim.Systems;

namespace Sim.Tests.Systems
{
    public class LightsCommsTests
    {
        public static IEnumerable<object[]> VisionCases()
        {
            // level, impostor, dead, crewMod, impMod, expected
            yield return new object[] { 1f, false, false, 1f, 1.5f, 5f };
            yield return new object[] { 0f, false, false, 1f, 1.5f, 1f };
            yield return new object[] { 0.5f, false, false, 1f, 1.5f, 3f };
            yield return new object[] { 0.5f, false, false, 0.5f, 1.5f, 1.5f };
            yield return new object[] { 0f, true, false, 0.5f, 1.5f, 7.5f };   // lights ignored
            yield return new object[] { 0f, true, true, 0.5f, 1.5f, 5f };      // dead wins over impostor, no multiplier
            yield return new object[] { 0f, false, true, 0.1f, 1.5f, 5f };
            yield return new object[] { 3f, false, false, 1f, 1.5f, 5f };      // level clamped
        }

        [Theory]
        [MemberData(nameof(VisionCases))]
        public void Vision_radius(float level, bool imp, bool dead, float crewMod, float impMod, float expected)
        {
            var o = new GameOptions { CrewLightMod = crewMod, ImpostorLightMod = impMod };
            Assert.Equal(expected, LightsSystem.VisionRadius(level, imp, dead, o), 4);
        }

        [Fact]
        public void Lights_start_fixed_and_full()
        {
            var l = new LightsSystem(new ScriptedRandom(9));
            Assert.False(l.IsActive);
            Assert.Equal(255, l.Value);
        }

        [Theory]
        [InlineData(0, 0b00100)]               // no other bit drawn: only switch 2
        [InlineData(0b1111, 0b11111)]          // all other draws true
        public void Sabotage_always_flips_switch_two(int drawBits, int expectedMask)
        {
            var draws = new int[4];
            for (int i = 0; i < 4; i++) draws[i] = (drawBits >> i) & 1;
            // First Next() is the initial layout (0), then 4 draws for switches 0,1,3,4.
            var l = new LightsSystem(new ScriptedRandom(new[] { 0 }.Concat(draws).ToArray()));
            int mask = l.Sabotage();
            Assert.Equal(expectedMask, mask);
            Assert.True(l.IsActive);
        }

        [Fact]
        public void Lights_fade_out_and_back_in_by_the_recovered_rate()
        {
            var l = new LightsSystem(new ScriptedRandom(0));
            l.Sabotage();
            l.Tick(0.03f * 10 + 0.001f);
            Assert.Equal(255 - 30, l.Value);
            l.Tick(10f);
            Assert.Equal(0, l.Value);
            Assert.Equal(0f, l.Level);
        }

        [Fact]
        public void Repair_is_flipping_the_same_switches_in_any_order()
        {
            var l = new LightsSystem(new ScriptedRandom(0, 1, 0, 1, 0));
            int mask = l.Sabotage(); // bits 1, 2, 4... whichever were drawn
            l.Tick(5f);
            Assert.Equal(0, l.Value);
            for (int i = LightsSystem.NumSwitches - 1; i >= 0; i--)
                if ((mask & (1 << i)) != 0) Assert.Equal(SystemResult.Ok, l.FlipSwitch(i, true));
            Assert.False(l.IsActive);
            l.Tick(5f);
            Assert.Equal(255, l.Value);
        }

        [Theory]
        [InlineData(5, true, SystemResult.InvalidConsole)]
        [InlineData(0, false, SystemResult.NotEligible)]
        public void Flip_validation(int index, bool eligible, SystemResult expected)
        {
            var l = new LightsSystem(new ScriptedRandom(0));
            Assert.Equal(expected, l.FlipSwitch(index, eligible));
        }

        [Fact]
        public void Simple_comms_start_and_fix()
        {
            var c = new SimpleComms();
            Assert.Equal(SystemResult.NotActive, c.Fix(true));
            c.Start();
            Assert.Equal(SystemResult.NotEligible, c.Fix(false));
            Assert.True(c.IsActive);
            Assert.Equal(SystemResult.Ok, c.Fix(true));
            Assert.False(c.IsActive);
        }

        [Fact]
        public void Window_comms_needs_both_codes_within_ten_seconds()
        {
            var c = new WindowComms(new ScriptedRandom(10, 20));
            c.Start();
            Assert.Equal(SystemResult.Ok, c.EnterCode(0, 10, true));
            c.Tick(10.5f); // window passed: code rolled to 20, progress lost
            Assert.Equal(SystemResult.WrongCode, c.EnterCode(1, 10, true));
            Assert.Equal(SystemResult.Ok, c.EnterCode(1, 20, true));
            Assert.True(c.IsActive);
            Assert.Equal(SystemResult.Ok, c.EnterCode(0, 20, true));
            Assert.False(c.IsActive);
        }
    }
}
