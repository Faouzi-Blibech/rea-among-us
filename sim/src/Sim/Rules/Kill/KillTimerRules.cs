using Sim.Core;
using Sim.Options;
using Sim.Roles;

namespace Sim.Rules.Kill
{
    public static class KillTimerRules
    {
        /// <summary>Spec s5: start-of-round timer is min(10, KillCooldown).</summary>
        public const float RoundStartSeconds = 10f;

        /// <summary>
        /// Clamped setter: no-op if the role cannot kill or KillCooldown &lt;= 0 (disabled);
        /// otherwise value in [0, KillCooldown]. Spec s5 SetKillTimer.
        /// </summary>
        public static void Set(PlayerState p, float seconds, GameOptions options)
        {
            if (!RoleTable.Get(p.Role).CanKill) return;
            float cd = options.KillCooldown;
            if (cd <= 0f) return;
            p.KillTimer = seconds < 0f ? 0f : seconds > cd ? cd : seconds;
        }

        public static void ResetForRoundStart(PlayerState p, GameOptions options) =>
            Set(p, RoundStartSeconds, options);

        public static void ResetAfterMeeting(PlayerState p, GameOptions options) =>
            Set(p, options.KillCooldown, options);

        /// <summary>
        /// Tick by dt only for a living kill-capable player while no meeting runs.
        /// Vents do not pause it (the spec reads no vent condition). Spec s5 FixedUpdate.
        /// </summary>
        public static void Tick(PlayerState p, float dt, GameOptions options, bool meetingActive)
        {
            if (meetingActive || !p.IsAlive || dt <= 0f) return;
            if (!RoleTable.Get(p.Role).CanKill) return;
            Set(p, p.KillTimer - dt, options);
        }
    }
}
