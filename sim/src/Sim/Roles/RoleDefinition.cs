using System.Collections.Generic;
using Sim.Core;

namespace Sim.Roles
{
    /// <summary>
    /// Static facts about a role. Spec: kill-roles-and-abilities.md s7.2 flag table, ability defaults
    /// from s6, s7.5, s7.10 (asset defaults).
    /// </summary>
    public sealed class RoleDefinition
    {
        public RoleDefinition(
            RoleId id, Team team, bool canKill, bool canBeKilled, bool canVent, bool tasksCount,
            RoleId ghostRole, bool isGhost, float abilityCooldown, float abilityDuration, int abilityUses)
        {
            Id = id; Team = team; CanKill = canKill; CanBeKilled = canBeKilled; CanVent = canVent;
            TasksCount = tasksCount; GhostRole = ghostRole; IsGhost = isGhost;
            AbilityCooldown = abilityCooldown; AbilityDuration = abilityDuration; AbilityUses = abilityUses;
        }

        public RoleId Id { get; }
        public Team Team { get; }
        public bool CanKill { get; }
        public bool CanBeKilled { get; }
        public bool CanVent { get; }
        /// <summary>Whether this role's tasks count toward the task bar.</summary>
        public bool TasksCount { get; }
        /// <summary>Default ghost role when a player holding this role dies.</summary>
        public RoleId GhostRole { get; }
        public bool IsGhost { get; }
        /// <summary>Seconds; 0 when the role has no ability.</summary>
        public float AbilityCooldown { get; }
        /// <summary>Seconds; for Mechanic the max time in a vent, for Disguiser the disguise length, for Guardian the shield length. 0 = unlimited / none.</summary>
        public float AbilityDuration { get; }
        /// <summary>-1 = unlimited, 0 = no ability.</summary>
        public int AbilityUses { get; }
    }

    public static class RoleTable
    {
        private static readonly Dictionary<RoleId, RoleDefinition> Defs = Build();

        public static RoleDefinition Get(RoleId id) => Defs[id];

        private static Dictionary<RoleId, RoleDefinition> Build()
        {
            var list = new[]
            {
                // id, team, canKill, canBeKilled, canVent, tasks, ghost, isGhost, cooldown, duration, uses
                new RoleDefinition(RoleId.Crew, Team.Crew, false, true, false, true, RoleId.CrewGhost, false, 0f, 0f, 0),
                new RoleDefinition(RoleId.Impostor, Team.Impostor, true, false, true, false, RoleId.ImpostorGhost, false, 0f, 0f, 0),
                // Engineer-like: cooldown 30 s, 15 s max in vent (s7.5).
                new RoleDefinition(RoleId.Mechanic, Team.Crew, false, true, true, true, RoleId.CrewGhost, false, 30f, 15f, -1),
                // Shapeshifter-like: cooldown 30 s, duration 0 = until reverted (s7.10 asset defaults).
                new RoleDefinition(RoleId.Disguiser, Team.Impostor, true, false, true, false, RoleId.ImpostorGhost, false, 30f, 0f, -1),
                new RoleDefinition(RoleId.CrewGhost, Team.Crew, false, false, false, true, RoleId.CrewGhost, true, 0f, 0f, 0),
                new RoleDefinition(RoleId.ImpostorGhost, Team.Impostor, false, false, false, false, RoleId.ImpostorGhost, true, 0f, 0f, 0),
                // Guardian-Angel-like: cooldown 60 s, shield 10 s (s6).
                new RoleDefinition(RoleId.GuardianGhost, Team.Crew, false, false, false, true, RoleId.GuardianGhost, true, 60f, 10f, -1),
            };
            var d = new Dictionary<RoleId, RoleDefinition>();
            foreach (RoleDefinition r in list) d[r.Id] = r;
            return d;
        }
    }
}
