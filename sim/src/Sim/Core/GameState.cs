using System.Collections.Generic;
using System.Linq;
using Sim.Options;

namespace Sim.Core
{
    public enum Phase : byte { Lobby, Playing, Meeting, Ended }

    /// <summary>Authoritative match state owned by the server. Clients hold a filtered copy.</summary>
    public sealed class GameState
    {
        public GameState(GameOptions options) { Options = options; }

        public GameOptions Options { get; }
        public Phase Phase { get; set; } = Phase.Lobby;
        /// <summary>Seconds since the round started; reset after each meeting.</summary>
        public float RoundTime { get; set; }
        public List<PlayerState> Players { get; } = new List<PlayerState>();
        public GameOverReason? Result { get; set; }
        /// <summary>Reason of the most recent death; picks the impostor win reason.</summary>
        public DeathReason? LastDeathReason { get; set; }

        public PlayerState? Get(byte id) => Players.FirstOrDefault(p => p.Id == id);
        public PlayerState AddPlayer(byte id) { var p = new PlayerState(id); Players.Add(p); return p; }
    }
}
