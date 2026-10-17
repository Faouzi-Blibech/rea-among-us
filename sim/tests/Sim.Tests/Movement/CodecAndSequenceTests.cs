using Sim.Core;
using Sim.Movement;
using Xunit;

namespace Sim.Tests.Movement
{
    public class CodecAndSequenceTests
    {
        [Theory]
        [InlineData(-50f, 0)]
        [InlineData(50f, 65535)]
        [InlineData(-80f, 0)]
        [InlineData(80f, 65535)]
        [InlineData(0f, 32767)]
        public void EncodeAxis_ClampsAndTruncates(float v, int expected)
            => Assert.Equal((ushort)expected, PositionCodec.EncodeAxis(v));

        [Fact]
        public void RoundTrip_ErrorWithinOneStep()
        {
            for (float v = -50f; v <= 50f; v += 0.37f)
            {
                float back = PositionCodec.DecodeAxis(PositionCodec.EncodeAxis(v));
                Assert.InRange(System.Math.Abs(back - v), 0f, PositionCodec.Step + 1e-4f);
            }
            var q = PositionCodec.Encode(new Vec2(12.3f, -7.7f));
            var d = PositionCodec.Decode(q);
            Assert.InRange(d.X, 12.3f - 0.002f, 12.3f + 0.002f);
            Assert.InRange(d.Y, -7.7f - 0.002f, -7.7f + 0.002f);
        }

        [Theory]
        [InlineData(5, 4, true)]
        [InlineData(4, 4, false)]
        [InlineData(3, 4, false)]
        [InlineData(0, 65535, true)]          // wrap
        [InlineData(1, 65535, true)]
        [InlineData(32766, 65535, true)]      // distance 32767
        [InlineData(32767, 65535, false)]     // distance 32768
        [InlineData(65535, 0, false)]         // behind
        [InlineData(32767, 0, true)]
        [InlineData(32768, 0, false)]
        public void IsNewer_WrapAware(int candidate, int previous, bool expected)
            => Assert.Equal(expected, SequenceId.IsNewer((ushort)candidate, (ushort)previous));

        [Fact]
        public void BatchIds_WrapAtSixteenBits()
        {
            Assert.Equal((ushort)1, SequenceId.BatchId(65534, 3));
        }

        [Theory]
        [InlineData(1f, false, 2.5f)]
        [InlineData(1f, true, 3.0f)]
        [InlineData(2f, false, 5.0f)]
        [InlineData(0.5f, true, 1.5f)]
        public void TrueSpeed(float mod, bool ghost, float expected)
            => Assert.Equal(expected, MovementSpeed.TrueSpeed(mod, ghost), 4);
    }
}
