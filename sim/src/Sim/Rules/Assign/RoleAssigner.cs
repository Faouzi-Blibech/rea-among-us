using System;
using System.Collections.Generic;
using Sim.Core;

namespace Sim.Rules.Assign
{
    /// <summary>Lobby setting for one special role: max copies per game and percent chance per copy.</summary>
    public sealed class RoleSetting
    {
        public RoleSetting(RoleId role, Team team, int count, int chance)
        {
            Role = role; Team = team; Count = count; Chance = chance;
        }
        public RoleId Role { get; }
        public Team Team { get; }
        public int Count { get; }
        /// <summary>0..100. 100 = guaranteed, 0 = never.</summary>
        public int Chance { get; }
    }

    public static class RoleAssigner
    {
        /// <summary>MaxImpostors by player count 0..15. Spec: movement-network-options.md s5.2.</summary>
        private static readonly int[] MaxImpostorsTable = { 0, 0, 0, 0, 1, 1, 1, 2, 2, 3, 3, 3, 3, 3, 3, 3 };

        /// <summary>
        /// min(configured, table cap) with the cap 3 outside the table; a configured value below 1 yields 1.
        /// Below 4 players the cap is 0, so the result is 0 unless configured &lt; 1 (faithful to the spec).
        /// Spec: meetings-votes-wins-tasks.md s7.1.
        /// </summary>
        public static int AdjustedImpostorCount(int configured, int playerCount)
        {
            int cap = playerCount >= 0 && playerCount < MaxImpostorsTable.Length ? MaxImpostorsTable[playerCount] : 3;
            int result = Math.Min(configured, cap);
            if (configured < 1) result = 1;
            return result;
        }

        /// <summary>
        /// Assigns Role and Team to every player: impostor team first (capped at the adjusted count),
        /// then crew. Spec: s7, s7.2, s7.3.
        /// </summary>
        public static void Assign(IReadOnlyList<PlayerState> players, int configuredImpostors, IReadOnlyList<RoleSetting> settings, IRandom rng)
        {
            var pool = new List<PlayerState>(players);
            int impostors = Math.Min(AdjustedImpostorCount(configuredImpostors, players.Count), players.Count);
            AssignTeam(pool, settings, Team.Impostor, impostors, RoleId.Impostor, rng);
            AssignTeam(pool, settings, Team.Crew, int.MaxValue, RoleId.Crew, rng);
        }

        private static void AssignTeam(List<PlayerState> pool, IReadOnlyList<RoleSetting> settings, Team team, int teamMax, RoleId defaultRole, IRandom rng)
        {
            int assigned = 0;
            var bag = new List<RoleId>();

            // Guaranteed pass.
            foreach (RoleSetting s in settings)
                if (IsSpecial(s, team, defaultRole) && s.Chance >= 100)
                    for (int i = 0; i < s.Count; i++) bag.Add(s.Role);
            DrawFromBag(pool, bag, team, teamMax, ref assigned, rng);

            // Chance pass: one independent roll per copy.
            bag.Clear();
            foreach (RoleSetting s in settings)
            {
                if (!IsSpecial(s, team, defaultRole) || s.Chance <= 0 || s.Chance >= 100) continue;
                for (int i = 0; i < s.Count; i++)
                    if (rng.Next(101) < s.Chance) bag.Add(s.Role);
            }
            DrawFromBag(pool, bag, team, teamMax, ref assigned, rng);

            // Default fill.
            bag.Clear();
            while (bag.Count < pool.Count && assigned + bag.Count < teamMax) bag.Add(defaultRole);
            DrawFromBag(pool, bag, team, teamMax, ref assigned, rng);
        }

        private static bool IsSpecial(RoleSetting s, Team team, RoleId defaultRole) =>
            s.Team == team && s.Count > 0 && s.Role != defaultRole;

        private static void DrawFromBag(List<PlayerState> pool, List<RoleId> bag, Team team, int teamMax, ref int assigned, IRandom rng)
        {
            while (bag.Count > 0 && pool.Count > 0 && assigned < teamMax)
            {
                int bi = rng.Next(bag.Count);
                RoleId role = bag[bi];
                bag.RemoveAt(bi);
                int pi = rng.Next(pool.Count);
                PlayerState p = pool[pi];
                pool.RemoveAt(pi);
                p.Role = role;
                p.Team = team;
                assigned++;
            }
        }
    }
}
