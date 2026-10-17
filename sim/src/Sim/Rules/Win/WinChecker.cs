using Sim.Core;

namespace Sim.Rules.Win
{
    /// <summary>Summed task counts that feed the crew task win.</summary>
    public readonly struct TaskProgress
    {
        public TaskProgress(int total, int completed) { Total = total; Completed = completed; }
        public int Total { get; }
        public int Completed { get; }
        /// <summary>Hardening: zero total tasks never counts as complete (the original would instant-win).</summary>
        public bool AllDone => Total > 0 && Completed >= Total;
    }

    /// <summary>End-of-game evaluation. Pure: reads state, returns a reason.</summary>
    public static class WinChecker
    {
        /// <summary>
        /// Counts tasks over connected players with a task list and a task-counting team (crew);
        /// dead players count only when ghosts do tasks.
        /// Spec: meetings-votes-wins-tasks.md s5.3
        /// </summary>
        public static TaskProgress CountTasks(GameState state)
        {
            int total = 0, done = 0;
            foreach (PlayerState p in state.Players)
            {
                if (p.Disconnected || p.Tasks.Count == 0) continue;
                if (p.IsDead && !state.Options.GhostsDoTasks) continue;
                if (p.Team != Team.Crew) continue;
                foreach (TaskState t in p.Tasks)
                {
                    total++;
                    if (t.Complete) done++;
                }
            }
            return new TaskProgress(total, done);
        }

        /// <summary>
        /// Evaluates the end criteria in the recovered order: expired critical sabotage, no impostors alive,
        /// impostors fewer than crew (then task completion), otherwise impostor win by last death reason.
        /// Returns null when the game continues.
        /// Spec: meetings-votes-wins-tasks.md s5.2
        /// </summary>
        public static GameOverReason? Evaluate(GameState state, bool criticalSabotageExpired)
        {
            if (criticalSabotageExpired) return GameOverReason.ImpostorBySabotage;

            int crew = 0, impostors = 0;
            foreach (PlayerState p in state.Players)
            {
                if (!p.IsAlive) continue;
                if (p.Team == Team.Impostor) impostors++; else crew++;
            }

            if (impostors < 1) return GameOverReason.CrewByVote;
            if (impostors < crew)
                return CountTasks(state).AllDone ? GameOverReason.CrewByTasks : (GameOverReason?)null;

            switch (state.LastDeathReason)
            {
                case DeathReason.Exile: return GameOverReason.ImpostorByVote;
                case DeathReason.Kill: return GameOverReason.ImpostorByKill;
                default: return GameOverReason.ImpostorByCrewDisconnect;
            }
        }

        /// <summary>Evaluates and, if the game is over, stores the result and moves the phase to Ended.</summary>
        public static GameOverReason? EvaluateAndApply(GameState state, bool criticalSabotageExpired)
        {
            if (state.Result.HasValue) return state.Result;
            GameOverReason? r = Evaluate(state, criticalSabotageExpired);
            if (r.HasValue)
            {
                state.Result = r;
                state.Phase = Phase.Ended;
            }
            return r;
        }
    }
}
