using System.Collections.Generic;
using Sim.Core;

namespace Sim.Roles
{
    /// <summary>A special ghost role offered to dead crew: per-game cap and percent chance (0..100).</summary>
    public readonly struct GhostRoleOption
    {
        public GhostRoleOption(RoleId role, int maxPerGame, int chancePercent)
        {
            Role = role; MaxPerGame = maxPerGame; ChancePercent = chancePercent;
        }

        public RoleId Role { get; }
        public int MaxPerGame { get; }
        public int ChancePercent { get; }
    }

    public static class GhostRoleAssigner
    {
        /// <summary>Default offer: one Guardian per game, 50 percent (tunable; spec gives no default here).</summary>
        public static readonly GhostRoleOption[] DefaultCrewCandidates =
        {
            new GhostRoleOption(RoleId.GuardianGhost, 1, 50),
        };

        /// <summary>
        /// Pick and apply the ghost role for a player who just died. Crew roll each candidate in order:
        /// skip if the cap is reached, else assign when rng.Next(101) &lt; chance. Impostors have no
        /// candidates. Fallback is the role's default ghost role. Already-ghost players are unchanged.
        /// Spec: kill-roles-and-abilities.md s8.
        /// </summary>
        public static RoleId Assign(
            PlayerState dead, IReadOnlyList<PlayerState> allPlayers,
            IReadOnlyList<GhostRoleOption> crewCandidates, IRandom rng)
        {
            RoleDefinition current = RoleTable.Get(dead.Role);
            if (current.IsGhost) return dead.Role;

            if (current.Team == Team.Crew)
            {
                foreach (GhostRoleOption opt in crewCandidates)
                {
                    int count = 0;
                    foreach (PlayerState p in allPlayers) if (p.Role == opt.Role) count++;
                    if (opt.MaxPerGame <= count) continue;
                    if (rng.Next(101) < opt.ChancePercent)
                    {
                        dead.Role = opt.Role;
                        return opt.Role;
                    }
                }
            }
            dead.Role = current.GhostRole;
            return current.GhostRole;
        }
    }
}
