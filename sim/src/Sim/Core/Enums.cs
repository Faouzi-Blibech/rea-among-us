namespace Sim.Core
{
    public enum Team : byte { Crew, Impostor }

    /// <summary>Our role set. Start small (blueprint M3/M6); add roles as new values.</summary>
    public enum RoleId : byte
    {
        Crew,
        Impostor,
        /// <summary>Crew who can use vents with a cooldown and a time limit inside.</summary>
        Mechanic,
        /// <summary>Impostor who can take another player's appearance for a while.</summary>
        Disguiser,
        /// <summary>Default role a dead crew player becomes.</summary>
        CrewGhost,
        /// <summary>Default role a dead impostor becomes.</summary>
        ImpostorGhost,
        /// <summary>Crew ghost that can shield one living player.</summary>
        GuardianGhost,
    }

    public enum DeathReason : byte { Kill, Exile, Disconnect }

    public enum GameOverReason : byte
    {
        CrewByVote,
        CrewByTasks,
        CrewByImpostorDisconnect,
        ImpostorByVote,
        ImpostorByKill,
        ImpostorBySabotage,
        ImpostorByCrewDisconnect,
    }

    public static class GameOverReasonExtensions
    {
        public static bool CrewWon(this GameOverReason r) =>
            r == GameOverReason.CrewByVote || r == GameOverReason.CrewByTasks || r == GameOverReason.CrewByImpostorDisconnect;
    }
}
