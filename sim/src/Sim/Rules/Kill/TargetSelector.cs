using System.Collections.Generic;
using Sim.Core;
using Sim.Options;
using Sim.Roles;

namespace Sim.Rules.Kill
{
    public static class TargetSelector
    {
        /// <summary>
        /// Valid targets within range sorted by distance ascending (ties by player id for determinism).
        /// Shared filter: alive, connected, not self, not in vent. When requireKillable, the target role
        /// must be killable (impostor team excluded). Range check inclusive. Spec s4.1, s4.2.
        /// </summary>
        public static List<PlayerState> InRangeSorted(
            PlayerState origin, IEnumerable<PlayerState> players, float range,
            ILineOfSight los, bool requireKillable, bool ignoreWalls)
        {
            var found = new List<(float d, PlayerState p)>();
            foreach (PlayerState p in players)
            {
                if (p.Id == origin.Id || p.IsDead || p.Disconnected || p.InVent) continue;
                if (requireKillable && !RoleTable.Get(p.Role).CanBeKilled) continue;
                float d = Vec2.Distance(origin.Position, p.Position);
                if (d > range) continue;
                if (!ignoreWalls && !los.Clear(origin.Position, p.Position)) continue;
                found.Add((d, p));
            }
            found.Sort((a, b) =>
            {
                int c = a.d.CompareTo(b.d);
                return c != 0 ? c : a.p.Id.CompareTo(b.p.Id);
            });
            var result = new List<PlayerState>(found.Count);
            foreach (var f in found) result.Add(f.p);
            return result;
        }

        /// <summary>Closest killable target within kill range with line of sight, or null.</summary>
        public static PlayerState? FindKillTarget(
            PlayerState killer, IEnumerable<PlayerState> players, GameOptions options, ILineOfSight los)
        {
            List<PlayerState> l = InRangeSorted(killer, players, options.KillRange, los, true, false);
            return l.Count > 0 ? l[0] : null;
        }
    }
}
