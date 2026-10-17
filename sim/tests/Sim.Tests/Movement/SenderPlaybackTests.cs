using System.Collections.Generic;
using Sim.Core;
using Sim.Movement;
using Xunit;

namespace Sim.Tests.Movement
{
    public class SenderPlaybackTests
    {
        private const float Dt = 0.02f;

        private static List<MovementPacket> RunSender(MovementSender s, int ticks, System.Func<int, Vec2> pos)
        {
            var list = new List<MovementPacket>();
            for (int i = 0; i < ticks; i++)
            {
                var p = s.Tick(pos(i), Dt);
                if (p != null) list.Add(p);
            }
            return list;
        }

        private static QuantizedPos[] Pts(params float[] xs)
        {
            var r = new QuantizedPos[xs.Length];
            for (int i = 0; i < xs.Length; i++) r[i] = PositionCodec.Encode(new Vec2(xs[i], 0f));
            return r;
        }

        [Fact]
        public void Sender_MovingBatchesFivePointsEveryTenthOfASecond()
        {
            var s = new MovementSender(new Vec2(0, 0), 10);
            var packets = RunSender(s, 50, i => new Vec2((i + 1) * 0.05f, 0f)); // 1 s of movement
            Assert.Equal(10, packets.Count);
            Assert.All(packets, p => Assert.Equal(5, p.Positions.Length));
            Assert.Equal((ushort)11, packets[0].FirstSeq);
            Assert.Equal((ushort)16, packets[1].FirstSeq);
        }

        [Fact]
        public void Sender_StoppingSendsOneTrailingDuplicateThenNothing()
        {
            var s = new MovementSender(new Vec2(0, 0));
            var moving = RunSender(s, 5, i => new Vec2((i + 1) * 0.1f, 0f));
            Assert.Single(moving);
            var still = RunSender(s, 50, i => new Vec2(0.5f, 0f));
            // dirty counter 2: first flush after moving was one decrement; one more duplicate follows
            Assert.Single(still);
            Assert.Single(still[0].Positions);
            Assert.Equal(moving[0].Positions[4], still[0].Positions[0]);
            Assert.Equal((ushort)(moving[0].LastSeq + 1), still[0].FirstSeq);
        }

        [Fact]
        public void Sender_SubThresholdJitterNeverQueues()
        {
            var s = new MovementSender(new Vec2(0, 0));
            // initial dirty (3) gives a few startup packets, then silence
            var packets = RunSender(s, 100, i => new Vec2(0.00005f, 0f));
            Assert.Equal(3, packets.Count);
            Assert.All(packets, p => Assert.Single(p.Positions));
        }

        [Fact]
        public void Receiver_DropsStaleAndDuplicateIds()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0), 10);
            b.Receive(11, Pts(1, 2, 3));
            Assert.Equal((ushort)13, b.LastSequenceId);
            int count = b.QueueCount;
            b.Receive(11, Pts(1, 2, 3)); // duplicate
            b.Receive(12, Pts(2, 3, 4)); // overlap: only id 14 is new
            Assert.Equal(count + 1, b.QueueCount);
            Assert.Equal((ushort)14, b.LastSequenceId);
        }

        [Fact]
        public void Receiver_AcceptsAcrossWrap()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0), 65534);
            b.Receive(65535, Pts(1, 2, 3));
            Assert.Equal((ushort)1, b.LastSequenceId);
            Assert.Equal(4, b.QueueCount);
        }

        [Fact]
        public void Playback_SteadyStreamFollowsSender()
        {
            var s = new MovementSender(new Vec2(0, 0));
            var b = new PlaybackBuffer(new Vec2(0, 0));
            Vec2 truth = new Vec2(0, 0);
            for (int i = 0; i < 300; i++)
            {
                truth = new Vec2(truth.X + 2.5f * Dt, 0f);
                var p = s.Tick(truth, Dt);
                if (p != null) b.Receive(p.FirstSeq, p.Positions);
                b.Step(Dt);
            }
            Assert.True(b.Position.X > truth.X - 1.5f && b.Position.X <= truth.X + 0.05f, "pos " + b.Position.X + " truth " + truth.X);
            Assert.True(b.QueueCount < PlaybackBuffer.SkipFirst);
            Assert.True(b.Velocity.Length <= PlaybackBuffer.VelocityCap + 1e-3f);
        }

        [Theory]
        [InlineData(12, 3)]  // one skip + regular pop... queue 1 start + 12 = 13 -> pops
        [InlineData(20, 3)]
        public void SkipAhead_PopsAtMostThreePerTick(int received, int maxPops)
        {
            var b = new PlaybackBuffer(new Vec2(0, 0));
            var xs = new float[received];
            for (int i = 0; i < received; i++) xs[i] = (i + 1) * 0.1f;
            b.Receive(1, Pts(xs));
            int before = b.QueueCount;
            b.Step(Dt);
            int pops = before - b.QueueCount;
            Assert.InRange(pops, 2, maxPops);
        }

        [Fact]
        public void Playback_NoSkipBelowTwelve()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0));
            b.Receive(1, Pts(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f)); // count 11
            b.Step(Dt);
            Assert.Equal(10, b.QueueCount); // single regular pop
        }

        [Fact]
        public void Playback_VelocityCapped()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0));
            // far points, 6+ in queue so pops happen: ideal speed would be huge
            b.Receive(1, Pts(10, 20, 30, 40, 45, 48, 49));
            b.Step(Dt);
            Assert.True(b.Velocity.Length <= PlaybackBuffer.VelocityCap + 1e-3f);
        }

        [Fact]
        public void Rubberband_ConvergesTowardTargets()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0));
            b.Step(Dt); // count 1 -> target 0.5, t = 0.06
            Assert.Equal(1f + (0.5f - 1f) * 0.06f, b.Rubberband, 4);
            b.Receive(1, Pts(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f)); // count 8 -> target 0.995
            float before = b.Rubberband;
            b.Step(Dt);
            Assert.Equal(before + (0.995f - before) * 0.06f, b.Rubberband, 4);
        }

        [Fact]
        public void LastPointNeverDequeued_AndSnapsToItWhenFar()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0));
            b.Receive(1, Pts(5f));
            for (int i = 0; i < 100; i++) b.Step(Dt);
            Assert.Equal(1, b.QueueCount);
            Assert.InRange(b.Position.X, 5f - 0.011f, 5f + 0.011f);
        }

        [Fact]
        public void Snap_RejectsOlderPacketsAndUsesLastSeqPlusTwo()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0), 100);
            Assert.True(b.SnapTo(new Vec2(20, 20)));
            Assert.Equal((ushort)102, b.LastSequenceId);
            Assert.Equal(new Vec2(20, 20), b.Position);
            Assert.Equal(0, b.QueueCount);
            // in-flight packet from before the teleport (ids 101..103): only 103 is newer
            b.Receive(101, Pts(1, 2, 3));
            Assert.Equal(1, b.QueueCount);
            Assert.Equal((ushort)103, b.LastSequenceId);
        }

        [Theory]
        [InlineData(100, false)]
        [InlineData(99, false)]
        [InlineData(101, true)]
        public void SnapWithExplicitId_RequiresNewer(int id, bool accepted)
        {
            var b = new PlaybackBuffer(new Vec2(0, 0), 100);
            Assert.Equal(accepted, b.HandleSnapRpc(new Vec2(9, 9), (ushort)id));
            Assert.Equal(accepted ? new Vec2(9, 9) : new Vec2(0, 0), b.Position);
        }

        [Fact]
        public void Halt_BumpsByOne_RpcId()
        {
            var b = new PlaybackBuffer(new Vec2(1, 1), 50);
            Assert.True(b.Halt());
            Assert.Equal((ushort)51, b.LastSequenceId);
            // RpcSnapTo: local snap at +1 then wire id = after + 2 (old + 3)
            ushort id = b.BuildSnapRpcId(new Vec2(4, 4), true);
            Assert.Equal((ushort)54, id);
            var other = new PlaybackBuffer(new Vec2(0, 0), 51);
            Assert.False(new PlaybackBuffer(new Vec2(0, 0), 54).HandleSnapRpc(new Vec2(4, 4), id));
            Assert.True(other.HandleSnapRpc(new Vec2(4, 4), id));
            // without local application: old + 2
            var c = new PlaybackBuffer(new Vec2(0, 0), 51);
            Assert.Equal((ushort)53, c.BuildSnapRpcId(new Vec2(1, 1), false));
        }

        [Fact]
        public void Snap_DeferredWhileInvisible()
        {
            var b = new PlaybackBuffer(new Vec2(0, 0), 10) { Invisible = true };
            Assert.True(b.SnapTo(new Vec2(7, 7)));
            Assert.Equal(new Vec2(0, 0), b.Position);
            Assert.Equal((ushort)10, b.LastSequenceId);
            b.Invisible = false;
            b.Step(Dt);
            Assert.Equal(new Vec2(7, 7), b.Position);
            Assert.Equal((ushort)12, b.LastSequenceId);
        }

        [Fact]
        public void Playback_IsDeterministic()
        {
            Vec2 Run()
            {
                var b = new PlaybackBuffer(new Vec2(0, 0));
                for (int i = 0; i < 60; i++)
                {
                    if (i % 5 == 0) b.Receive((ushort)(1 + i), Pts(i * 0.1f, i * 0.1f + 0.02f, i * 0.1f + 0.04f, i * 0.1f + 0.06f, i * 0.1f + 0.08f));
                    b.Step(Dt);
                }
                return b.Position;
            }
            Assert.Equal(Run(), Run());
        }
    }
}
