using Sim.Core;

namespace Sim.Rules.Kill
{
    /// <summary>Wall and closed-door test supplied by the map layer.</summary>
    public interface ILineOfSight
    {
        /// <summary>True when nothing solid blocks the straight segment a to b.</summary>
        bool Clear(Vec2 a, Vec2 b);
    }
}
