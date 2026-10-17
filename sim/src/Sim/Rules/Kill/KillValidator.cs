using Sim.Core;
using Sim.Options;
using Sim.Roles;

namespace Sim.Rules.Kill
{
    public static class KillValidator
    {
        /// <summary>
        /// Ordered server checks for a kill request.
        /// Spec: kill-roles-and-abilities.md s3.2 (checks 2-14) plus s11 hardening: cooldown elapsed,
        /// distance &lt;= kill range, line of sight clear, target must be killable (s4.2).
        /// </summary>
        public static KillRejectReason Validate(
            PlayerState killer, PlayerState? target, GameOptions options, ILineOfSight los, KillContext ctx)
        {
            if (ctx.GameOver) return KillRejectReason.GameOver;
            if (killer.IsDead) return KillRejectReason.KillerDead;
            RoleDefinition kr = RoleTable.Get(killer.Role);
            if (killer.Team != Team.Impostor || kr.Team != Team.Impostor) return KillRejectReason.KillerNotImpostor;
            if (!kr.CanKill) return KillRejectReason.KillerRoleCannotKill;
            if (killer.Disconnected) return KillRejectReason.KillerDisconnected;
            if (killer.KillTimer > 0f) return KillRejectReason.OnCooldown;
            if (target == null) return KillRejectReason.TargetMissing;
            if (target.Id == killer.Id) return KillRejectReason.TargetIsSelf;
            if (target.IsDead) return KillRejectReason.TargetDead;
            if (target.Disconnected) return KillRejectReason.TargetDisconnected;
            if (!RoleTable.Get(target.Role).CanBeKilled) return KillRejectReason.TargetNotKillable;
            if (target.InVent) return KillRejectReason.TargetInVent;
            if (ctx.TargetBusy) return KillRejectReason.TargetBusy;
            if (ctx.MeetingActive) return KillRejectReason.MeetingActive;
            if (Vec2.Distance(killer.Position, target.Position) > options.KillRange) return KillRejectReason.OutOfRange;
            if (!los.Clear(killer.Position, target.Position)) return KillRejectReason.WallBetween;
            return KillRejectReason.None;
        }
    }
}
