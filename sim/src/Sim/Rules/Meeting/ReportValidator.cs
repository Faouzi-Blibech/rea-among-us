using Sim.Core;
using Sim.Rules.Win;

namespace Sim.Rules.Meeting
{
    public enum ReportResult : byte
    {
        Ok,
        GameOver,
        MeetingAlreadyActive,
        CriticalSabotageActive,
        ReporterDead,
        BodyTooFar,
        EmergencyLocked,
        EmergencyCooldown,
        NoEmergenciesLeft,
        TasksAlreadyComplete,
    }

    /// <summary>Server-side validation of body reports and emergency button presses.</summary>
    public static class ReportValidator
    {
        public const float MaxReportDistance = 3.0f;
        public const float MinEmergencyRoundTime = 15f;

        /// <summary>
        /// Validates a body report. Spec: meetings-votes-wins-tasks.md s2.1.
        /// Hardening: the original host skipped the distance check.
        /// </summary>
        public static ReportResult ValidateBodyReport(GameState state, PlayerState reporter, Vec2 bodyPosition)
        {
            ReportResult common = CheckCommon(state, reporter);
            if (common != ReportResult.Ok) return common;
            if (Vec2.Distance(reporter.Position, bodyPosition) > MaxReportDistance) return ReportResult.BodyTooFar;
            return ReportResult.Ok;
        }

        /// <summary>
        /// Validates an emergency button press. Spec: meetings-votes-wins-tasks.md s2.1 and s10.
        /// Hardening: round-time lock, cooldown and emergency count were client-only in the original.
        /// The sabotage check is a server-wide "critical sabotage active" flag, not the reporter's task list.
        /// </summary>
        public static ReportResult ValidateEmergency(GameState state, PlayerState reporter, float emergencyCooldownRemaining, bool criticalSabotageActive)
        {
            ReportResult common = CheckCommon(state, reporter);
            if (common != ReportResult.Ok) return common;
            if (criticalSabotageActive) return ReportResult.CriticalSabotageActive;
            if (state.RoundTime < MinEmergencyRoundTime) return ReportResult.EmergencyLocked;
            if (emergencyCooldownRemaining > 0f) return ReportResult.EmergencyCooldown;
            if (reporter.RemainingEmergencies <= 0) return ReportResult.NoEmergenciesLeft;
            return ReportResult.Ok;
        }

        private static ReportResult CheckCommon(GameState state, PlayerState reporter)
        {
            if (state.Result.HasValue || state.Phase == Phase.Ended) return ReportResult.GameOver;
            if (state.Phase == Phase.Meeting) return ReportResult.MeetingAlreadyActive;
            if (reporter.IsDead || reporter.Disconnected) return ReportResult.ReporterDead;
            if (WinChecker.CountTasks(state).AllDone) return ReportResult.TasksAlreadyComplete;
            return ReportResult.Ok;
        }
    }
}
