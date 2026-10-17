using Sim.Core;

namespace Sim.Rules.Kill
{
    public enum KillResult : byte { Rejected, Killed, BlockedByShield }

    /// <summary>Dead body left by a kill. Spec: s3.3 (body at victim position, ParentId = victim).</summary>
    public readonly struct BodyRecord
    {
        public BodyRecord(byte victimId, Vec2 position) { VictimId = victimId; Position = position; }
        public byte VictimId { get; }
        public Vec2 Position { get; }
    }

    public readonly struct KillOutcome
    {
        public KillOutcome(KillResult result, KillRejectReason reason, BodyRecord? body, DeathReason? death)
        {
            Result = result; Reason = reason; Body = body; Death = death;
        }

        public KillResult Result { get; }
        public KillRejectReason Reason { get; }
        /// <summary>Set only when Result is Killed.</summary>
        public BodyRecord? Body { get; }
        /// <summary>The recorded last death reason; set only when Result is Killed.</summary>
        public DeathReason? Death { get; }
    }
}
