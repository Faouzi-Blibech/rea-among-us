using System.Collections.Generic;
using Sim.Core;

namespace Sim.Rules.Meeting
{
    public enum MeetingPhase : byte { Discussion, Voting, Results, Proceed }

    public enum VoteKind : byte
    {
        /// <summary>Living voter who has not voted yet.</summary>
        None,
        /// <summary>Timed out; ignored by the tally.</summary>
        Missed,
        Skip,
        Player,
        /// <summary>Dead or disconnected; never votes.</summary>
        Dead,
    }

    public readonly struct Vote
    {
        public Vote(VoteKind kind, byte target = 0) { Kind = kind; Target = target; }
        public VoteKind Kind { get; }
        /// <summary>Target player id; meaningful only when Kind is Player.</summary>
        public byte Target { get; }
    }

    public enum VoteResult : byte
    {
        Accepted,
        WrongPhase,
        UnknownVoter,
        VoterDead,
        AlreadyVoted,
        InvalidTarget,
    }

    public sealed class MeetingOutcome
    {
        public MeetingOutcome(byte? exiled, bool tie, bool skipWon)
        {
            Exiled = exiled; Tie = tie; SkipWon = skipWon;
        }
        public byte? Exiled { get; }
        /// <summary>Shared top count, including skip tied with a player, or an empty tally.</summary>
        public bool Tie { get; }
        public bool SkipWon { get; }
    }

    /// <summary>
    /// One meeting: discussion, voting, results, proceed. Time is fed in through <see cref="Tick"/>.
    /// Spec: meetings-votes-wins-tasks.md s3.
    /// </summary>
    public sealed class MeetingSession
    {
        public const float ResultsSeconds = 5f;

        private readonly GameState _state;
        private readonly Dictionary<byte, Vote> _votes = new Dictionary<byte, Vote>();
        private float _timer;
        private float _resultsStartedAt;
        private bool _exileApplied;

        public MeetingSession(GameState state)
        {
            _state = state;
            foreach (PlayerState p in state.Players)
                _votes[p.Id] = new Vote(p.IsAlive ? VoteKind.None : VoteKind.Dead);
        }

        public MeetingPhase Phase { get; private set; } = MeetingPhase.Discussion;
        public MeetingOutcome? Outcome { get; private set; }
        public IReadOnlyDictionary<byte, Vote> Votes => _votes;

        /// <summary>
        /// Advances the clock. Discussion ends when DiscussionTime has elapsed (0 moves on at once);
        /// voting ends at VotingTime (if above 0) by marking non-voters as missed; results last 5 s,
        /// then the exile is applied (IsDead, LastDeathReason = Exile) and the phase is Proceed.
        /// Spec: s3.2.
        /// </summary>
        public void Tick(float dt)
        {
            if (Phase == MeetingPhase.Proceed) return;
            _timer += dt;

            if (Phase == MeetingPhase.Discussion && _timer >= _state.Options.DiscussionTime)
            {
                Phase = MeetingPhase.Voting;
                CheckForEndVoting();
            }
            if (Phase == MeetingPhase.Voting)
            {
                float v = _state.Options.VotingTime;
                if (v > 0f && _timer - _state.Options.DiscussionTime >= v) ForceSkipAll();
            }
            if (Phase == MeetingPhase.Results && _timer - _resultsStartedAt >= ResultsSeconds)
            {
                Phase = MeetingPhase.Proceed;
                ApplyExile();
            }
        }

        /// <summary>
        /// Casts a final vote. target null means skip. Hardening: phase, voter and target are validated.
        /// Spec: s3.3.
        /// </summary>
        public VoteResult CastVote(byte voterId, byte? target)
        {
            if (Phase != MeetingPhase.Voting) return VoteResult.WrongPhase;
            if (!_votes.TryGetValue(voterId, out Vote existing)) return VoteResult.UnknownVoter;
            if (existing.Kind == VoteKind.Dead) return VoteResult.VoterDead;
            if (existing.Kind != VoteKind.None) return VoteResult.AlreadyVoted;

            if (target.HasValue)
            {
                PlayerState? t = _state.Get(target.Value);
                if (t == null || !t.IsAlive) return VoteResult.InvalidTarget;
                _votes[voterId] = new Vote(VoteKind.Player, target.Value);
            }
            else
            {
                _votes[voterId] = new Vote(VoteKind.Skip);
            }
            CheckForEndVoting();
            return VoteResult.Accepted;
        }

        /// <summary>A player left mid-meeting: stop waiting for them. Spec: s3.4 step 1, s5.4.</summary>
        public void HandleDisconnect(byte playerId)
        {
            if (_votes.ContainsKey(playerId)) _votes[playerId] = new Vote(VoteKind.Dead);
            if (Phase == MeetingPhase.Voting) CheckForEndVoting();
        }

        /// <summary>Marks every living non-voter as missed and ends voting. Spec: s3.2 (ForceSkipAll).</summary>
        public void ForceSkipAll()
        {
            if (Phase != MeetingPhase.Voting) return;
            foreach (byte id in new List<byte>(_votes.Keys))
                if (_votes[id].Kind == VoteKind.None) _votes[id] = new Vote(VoteKind.Missed);
            CheckForEndVoting();
        }

        private void CheckForEndVoting()
        {
            if (Phase != MeetingPhase.Voting) return;
            foreach (Vote v in _votes.Values)
                if (v.Kind == VoteKind.None) return;
            Outcome = Tally(_votes.Values);
            Phase = MeetingPhase.Results;
            _resultsStartedAt = _timer;
        }

        /// <summary>
        /// Plurality tally: each Player/Skip vote counts 1; Dead, None and Missed are ignored.
        /// A shared top count (also skip vs player, or nothing counted) is a tie with no exile;
        /// skip winning alone exiles nobody. Spec: s3.4, s3.5.
        /// </summary>
        public static MeetingOutcome Tally(IEnumerable<Vote> votes)
        {
            var counts = new Dictionary<byte, int>();
            int skips = 0;
            foreach (Vote v in votes)
            {
                if (v.Kind == VoteKind.Skip) skips++;
                else if (v.Kind == VoteKind.Player)
                {
                    counts.TryGetValue(v.Target, out int c);
                    counts[v.Target] = c + 1;
                }
            }

            int max = skips;
            foreach (int c in counts.Values) if (c > max) max = c;
            if (max == 0) return new MeetingOutcome(null, true, false);

            int holders = skips == max ? 1 : 0;
            byte? leader = null;
            foreach (KeyValuePair<byte, int> kv in counts)
            {
                if (kv.Value != max) continue;
                holders++;
                leader = kv.Key;
            }
            if (holders > 1) return new MeetingOutcome(null, true, false);
            if (leader == null) return new MeetingOutcome(null, false, true);
            return new MeetingOutcome(leader, false, false);
        }

        private void ApplyExile()
        {
            if (_exileApplied) return;
            _exileApplied = true;
            if (Outcome != null && Outcome.Exiled.HasValue)
            {
                PlayerState? p = _state.Get(Outcome.Exiled.Value);
                if (p != null)
                {
                    p.IsDead = true;
                    _state.LastDeathReason = DeathReason.Exile;
                }
            }
        }
    }
}
