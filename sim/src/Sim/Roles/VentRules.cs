using System.Collections.Generic;
using Sim.Core;
using Sim.Rules.Kill;

namespace Sim.Roles
{
    public enum VentRejectReason : byte
    {
        None,
        RoleCannotVent,
        Dead,
        Disconnected,
        MeetingActive,
        OnCooldown,
        BeingCleaned,
        TooFar,
        WallBetween,
        NotInVent,
        NotConnected,
    }

    /// <summary>Vent network: id to neighbour ids. Spec: s9.1 (vent arrow buttons).</summary>
    public sealed class VentGraph
    {
        private readonly Dictionary<int, Vec2> _pos = new Dictionary<int, Vec2>();
        private readonly Dictionary<int, List<int>> _links = new Dictionary<int, List<int>>();

        public void AddVent(int id, Vec2 position, IEnumerable<int> neighbours)
        {
            _pos[id] = position;
            _links[id] = new List<int>(neighbours);
        }

        public bool TryGetPosition(int id, out Vec2 position) => _pos.TryGetValue(id, out position);

        public IReadOnlyList<int> Neighbours(int id) =>
            _links.TryGetValue(id, out List<int>? l) ? l : (IReadOnlyList<int>)new int[0];

        public bool CanTravel(int from, int to) => _links.TryGetValue(from, out List<int>? l) && l.Contains(to);
    }

    public static class VentRules
    {
        /// <summary>Spec s9.1: centre-to-vent distance for use.</summary>
        public const float UseRadius = 0.75f;

        /// <summary>
        /// Can this player enter or exit the vent at ventPosition right now?
        /// Spec: kill-roles-and-abilities.md s9.1 (canVent, alive, radius 0.75, line of sight,
        /// cleaning block) and s7.5 (Mechanic cooldown blocks entry only). mechanic is required for the
        /// Mechanic role and ignored otherwise.
        /// </summary>
        public static VentRejectReason CanUse(
            PlayerState p, Vec2 ventPosition, ILineOfSight los, bool meetingActive,
            bool ventBeingCleaned, MechanicVent? mechanic)
        {
            if (!RoleTable.Get(p.Role).CanVent) return VentRejectReason.RoleCannotVent;
            if (p.IsDead) return VentRejectReason.Dead;
            if (p.Disconnected) return VentRejectReason.Disconnected;
            if (meetingActive) return VentRejectReason.MeetingActive;
            if (p.Role == RoleId.Mechanic && !p.InVent && (mechanic == null || !mechanic.CanEnter))
                return VentRejectReason.OnCooldown;
            if (ventBeingCleaned) return VentRejectReason.BeingCleaned;
            if (Vec2.Distance(p.Position, ventPosition) > UseRadius) return VentRejectReason.TooFar;
            if (!los.Clear(p.Position, ventPosition)) return VentRejectReason.WallBetween;
            return VentRejectReason.None;
        }

        /// <summary>Move between connected vents while inside the network.</summary>
        public static VentRejectReason TryTravel(PlayerState p, VentGraph graph, int fromVent, int toVent)
        {
            if (!p.InVent) return VentRejectReason.NotInVent;
            if (!graph.CanTravel(fromVent, toVent)) return VentRejectReason.NotConnected;
            if (graph.TryGetPosition(toVent, out Vec2 pos)) p.Position = pos;
            return VentRejectReason.None;
        }
    }
}
