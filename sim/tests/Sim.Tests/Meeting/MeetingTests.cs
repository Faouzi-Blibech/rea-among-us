using Sim.Core;
using Sim.Options;
using Sim.Rules.Meeting;

namespace Sim.Tests.Meeting
{
    public class MeetingTests
    {
        private static GameState Make(int players, float discussion = 15f, float voting = 120f)
        {
            var s = new GameState(new GameOptions { DiscussionTime = discussion, VotingTime = voting });
            for (byte i = 0; i < players; i++) s.AddPlayer(i);
            s.Players[0].Team = Team.Impostor;
            s.RoundTime = 100f;
            foreach (var p in s.Players) p.RemainingEmergencies = 1;
            return s;
        }

        [Theory]
        [InlineData(ReportResult.Ok, 0f, false)]
        [InlineData(ReportResult.BodyTooFar, 3.5f, false)]
        [InlineData(ReportResult.Ok, 3.0f, false)]
        [InlineData(ReportResult.ReporterDead, 1f, true)]
        public void BodyReport(ReportResult expected, float dist, bool reporterDead)
        {
            var s = Make(4);
            s.Players[1].IsDead = reporterDead;
            Assert.Equal(expected, ReportValidator.ValidateBodyReport(s, s.Players[1], new Vec2(dist, 0)));
        }

        [Fact]
        public void BodyReportRejectedDuringMeetingOrGameOver()
        {
            var s = Make(4);
            s.Phase = Phase.Meeting;
            Assert.Equal(ReportResult.MeetingAlreadyActive, ReportValidator.ValidateBodyReport(s, s.Players[1], Vec2.Zero));
            s.Phase = Phase.Ended;
            Assert.Equal(ReportResult.GameOver, ReportValidator.ValidateBodyReport(s, s.Players[1], Vec2.Zero));
        }

        [Theory]
        [InlineData(ReportResult.Ok, 100f, 0f, 1, false)]
        [InlineData(ReportResult.EmergencyLocked, 14.9f, 0f, 1, false)]
        [InlineData(ReportResult.Ok, 15f, 0f, 1, false)]
        [InlineData(ReportResult.EmergencyCooldown, 100f, 0.1f, 1, false)]
        [InlineData(ReportResult.NoEmergenciesLeft, 100f, 0f, 0, false)]
        [InlineData(ReportResult.CriticalSabotageActive, 100f, 0f, 1, true)]
        public void Emergency(ReportResult expected, float roundTime, float cooldown, int left, bool sabotage)
        {
            var s = Make(4);
            s.RoundTime = roundTime;
            s.Players[1].RemainingEmergencies = left;
            Assert.Equal(expected, ReportValidator.ValidateEmergency(s, s.Players[1], cooldown, sabotage));
        }

        [Fact]
        public void ReportRefusedWhenTasksAlreadyComplete()
        {
            var s = Make(4);
            var t = new TaskState(0, 1, 1) { StepsDone = 1 };
            s.Players[1].Tasks.Add(t);
            Assert.Equal(ReportResult.TasksAlreadyComplete, ReportValidator.ValidateBodyReport(s, s.Players[1], Vec2.Zero));
        }

        [Fact]
        public void StateMachineRunsThroughAndAppliesExile()
        {
            var s = Make(3, 10f, 30f);
            var m = new MeetingSession(s);
            Assert.Equal(VoteResult.WrongPhase, m.CastVote(0, 1));
            m.Tick(9.9f);
            Assert.Equal(MeetingPhase.Discussion, m.Phase);
            m.Tick(0.2f);
            Assert.Equal(MeetingPhase.Voting, m.Phase);
            Assert.Equal(VoteResult.Accepted, m.CastVote(0, 2));
            Assert.Equal(VoteResult.Accepted, m.CastVote(1, 2));
            Assert.Equal(MeetingPhase.Voting, m.Phase);
            Assert.Equal(VoteResult.Accepted, m.CastVote(2, null));
            Assert.Equal(MeetingPhase.Results, m.Phase);
            Assert.Equal((byte?)2, m.Outcome!.Exiled);
            m.Tick(4.9f);
            Assert.Equal(MeetingPhase.Results, m.Phase);
            Assert.False(s.Players[2].IsDead);
            m.Tick(0.2f);
            Assert.Equal(MeetingPhase.Proceed, m.Phase);
            Assert.True(s.Players[2].IsDead);
            Assert.Equal(DeathReason.Exile, s.LastDeathReason);
        }

        [Fact]
        public void ZeroDiscussionMovesToVotingOnFirstTick()
        {
            var m = new MeetingSession(Make(3, 0f));
            m.Tick(0.01f);
            Assert.Equal(MeetingPhase.Voting, m.Phase);
        }

        [Fact]
        public void VotingTimeoutMarksNonVotersMissed()
        {
            var s = Make(3, 5f, 20f);
            var m = new MeetingSession(s);
            m.Tick(5f);
            m.CastVote(0, 1);
            m.Tick(19.9f);
            Assert.Equal(MeetingPhase.Voting, m.Phase);
            m.Tick(0.2f);
            Assert.Equal(MeetingPhase.Results, m.Phase);
            Assert.Equal(VoteKind.Missed, m.Votes[1].Kind);
            Assert.Equal((byte?)1, m.Outcome!.Exiled);
        }

        [Fact]
        public void UnlimitedVotingNeverTimesOut()
        {
            var m = new MeetingSession(Make(3, 5f, 0f));
            m.Tick(5f);
            m.Tick(100000f);
            Assert.Equal(MeetingPhase.Voting, m.Phase);
        }

        [Fact]
        public void AllMissedIsTieWithNoExile()
        {
            var m = new MeetingSession(Make(3, 0f, 10f));
            m.Tick(0f);
            m.Tick(10f);
            Assert.True(m.Outcome!.Tie);
            Assert.Null(m.Outcome.Exiled);
        }

        [Fact]
        public void VoteValidation()
        {
            var s = Make(4, 0f);
            s.Players[3].IsDead = true;
            var m = new MeetingSession(s);
            m.Tick(0.01f);
            Assert.Equal(VoteResult.VoterDead, m.CastVote(3, 1));
            Assert.Equal(VoteResult.UnknownVoter, m.CastVote(9, 1));
            Assert.Equal(VoteResult.InvalidTarget, m.CastVote(0, 3));
            Assert.Equal(VoteResult.InvalidTarget, m.CastVote(0, 9));
            Assert.Equal(VoteResult.Accepted, m.CastVote(0, 1));
            Assert.Equal(VoteResult.AlreadyVoted, m.CastVote(0, 2));
        }

        [Fact]
        public void DisconnectDoesNotBlockEnd()
        {
            var s = Make(3, 0f);
            var m = new MeetingSession(s);
            m.Tick(0.01f);
            m.CastVote(0, 1);
            m.CastVote(1, 1);
            m.HandleDisconnect(2);
            Assert.Equal(MeetingPhase.Results, m.Phase);
        }

        public static TheoryData<string, byte?, bool, bool> TallyCases() => new TheoryData<string, byte?, bool, bool>
        {
            // votes: p=player id, s=skip, m=missed, d=dead
            { "p1,p1,p2", 1, false, false },
            { "p1,p2", null, true, false },
            { "p1,s", null, true, false },
            { "s,s,p1", null, false, true },
            { "s,p1,p1,m,d", 1, false, false },
            { "m,m,d", null, true, false },
            { "p1,p1,p2,p2,s", null, true, false },
        };

        [Theory]
        [MemberData(nameof(TallyCases))]
        public void Tally(string spec, byte? exiled, bool tie, bool skipWon)
        {
            var votes = new System.Collections.Generic.List<Vote>();
            foreach (string tok in spec.Split(','))
            {
                if (tok == "s") votes.Add(new Vote(VoteKind.Skip));
                else if (tok == "m") votes.Add(new Vote(VoteKind.Missed));
                else if (tok == "d") votes.Add(new Vote(VoteKind.Dead));
                else votes.Add(new Vote(VoteKind.Player, byte.Parse(tok.Substring(1))));
            }
            MeetingOutcome o = MeetingSession.Tally(votes);
            Assert.Equal(exiled, o.Exiled);
            Assert.Equal(tie, o.Tie);
            Assert.Equal(skipWon, o.SkipWon);
        }
    }
}
