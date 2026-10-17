using Sim.Core;
using Sim.Options;
using Sim.Rules.Win;

namespace Sim.Tests.Win
{
    public class WinTests
    {
        private static GameState Make(int crew, int imps, bool ghostsDoTasks = true)
        {
            var s = new GameState(new GameOptions { GhostsDoTasks = ghostsDoTasks });
            byte id = 0;
            for (int i = 0; i < crew; i++) s.AddPlayer(id++);
            for (int i = 0; i < imps; i++) s.AddPlayer(id++).Team = Team.Impostor;
            return s;
        }

        private static void Task(PlayerState p, bool done) =>
            p.Tasks.Add(new TaskState(p.Tasks.Count, 1, 1) { StepsDone = done ? 1 : 0 });

        [Theory]
        [InlineData(3, 1, DeathReason.Kill, null)]
        [InlineData(2, 2, DeathReason.Kill, GameOverReason.ImpostorByKill)]
        [InlineData(2, 2, DeathReason.Exile, GameOverReason.ImpostorByVote)]
        [InlineData(1, 2, DeathReason.Disconnect, GameOverReason.ImpostorByCrewDisconnect)]
        [InlineData(3, 0, DeathReason.Exile, GameOverReason.CrewByVote)]
        public void DeathBasedOutcomes(int crew, int imps, DeathReason last, GameOverReason? expected)
        {
            var s = Make(crew, imps);
            s.LastDeathReason = last;
            Assert.Equal(expected, WinChecker.Evaluate(s, false));
        }

        [Fact]
        public void SabotageBeatsEverything()
        {
            var s = Make(3, 0);
            Assert.Equal(GameOverReason.ImpostorBySabotage, WinChecker.Evaluate(s, true));
        }

        [Fact]
        public void DeadAndDisconnectedDoNotCountAsAlive()
        {
            var s = Make(3, 1);
            s.Players[0].IsDead = true;
            s.Players[1].Disconnected = true;
            s.LastDeathReason = DeathReason.Kill;
            Assert.Equal(GameOverReason.ImpostorByKill, WinChecker.Evaluate(s, false));
        }

        [Fact]
        public void TasksCompleteGivesCrewWinAndZeroTasksDoesNot()
        {
            var s = Make(3, 1);
            Assert.Null(WinChecker.Evaluate(s, false));
            Task(s.Players[0], true); Task(s.Players[1], true);
            Assert.Equal(GameOverReason.CrewByTasks, WinChecker.Evaluate(s, false));
            Task(s.Players[2], false);
            Assert.Null(WinChecker.Evaluate(s, false));
        }

        [Fact]
        public void ImpostorTasksDoNotCount()
        {
            var s = Make(3, 1);
            Task(s.Players[0], true);
            Task(s.Players[3], false);
            Assert.Equal(GameOverReason.CrewByTasks, WinChecker.Evaluate(s, false));
        }

        [Theory]
        [InlineData(true, 2)]
        [InlineData(false, 1)]
        public void DeadTasksCountOnlyWhenGhostsDoTasks(bool ghosts, int expectedTotal)
        {
            var s = Make(3, 1, ghosts);
            Task(s.Players[0], true); Task(s.Players[1], false);
            s.Players[1].IsDead = true;
            Assert.Equal(expectedTotal, WinChecker.CountTasks(s).Total);
        }

        [Fact]
        public void DisconnectedTasksSkipped()
        {
            var s = Make(3, 1);
            Task(s.Players[0], true); Task(s.Players[1], false);
            s.Players[1].Disconnected = true;
            Assert.Equal(1, WinChecker.CountTasks(s).Total);
        }

        [Fact]
        public void EvaluateAndApplySetsResultAndPhase()
        {
            var s = Make(3, 0);
            WinChecker.EvaluateAndApply(s, false);
            Assert.Equal(GameOverReason.CrewByVote, s.Result);
            Assert.Equal(Phase.Ended, s.Phase);
        }
    }
}
