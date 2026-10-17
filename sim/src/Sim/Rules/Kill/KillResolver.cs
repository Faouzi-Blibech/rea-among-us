using Sim.Core;
using Sim.Options;

namespace Sim.Rules.Kill
{
    public static class KillResolver
    {
        /// <summary>Spec s5: the shield-blocked cooldown is half the kill cooldown.</summary>
        public const float BlockedCooldownFactor = 0.5f;

        /// <summary>
        /// Validate and apply a kill. Success: target dead, killer timer = full KillCooldown.
        /// Shielded target: kill cancelled, shield consumed (s11), killer timer = 0.5 x KillCooldown.
        /// Spec: kill-roles-and-abilities.md s3.3.
        /// </summary>
        public static KillOutcome TryKill(
            PlayerState killer, PlayerState? target, GameOptions options, ILineOfSight los, KillContext ctx)
        {
            KillRejectReason reason = KillValidator.Validate(killer, target, options, los, ctx);
            if (reason != KillRejectReason.None || target == null)
                return new KillOutcome(KillResult.Rejected, reason, null, null);

            if (target.ProtectedBy.HasValue)
            {
                target.ProtectedBy = null;
                KillTimerRules.Set(killer, BlockedCooldownFactor * options.KillCooldown, options);
                return new KillOutcome(KillResult.BlockedByShield, KillRejectReason.None, null, null);
            }

            target.IsDead = true;
            target.InVent = false;
            KillTimerRules.Set(killer, options.KillCooldown, options);
            return new KillOutcome(KillResult.Killed, KillRejectReason.None,
                new BodyRecord(target.Id, target.Position), DeathReason.Kill);
        }
    }
}
