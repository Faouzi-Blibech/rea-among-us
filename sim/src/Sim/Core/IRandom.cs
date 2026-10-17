using System;

namespace Sim.Core
{
    /// <summary>Randomness source for rules. Inject a seeded one for replays and tests.</summary>
    public interface IRandom
    {
        /// <summary>Uniform integer in [0, maxExclusive).</summary>
        int Next(int maxExclusive);
    }

    public sealed class SeededRandom : IRandom
    {
        private readonly Random _rng;
        public SeededRandom(int seed) { _rng = new Random(seed); }
        public int Next(int maxExclusive) => _rng.Next(maxExclusive);
    }
}
