using System;
using Sim.Core;
using Sim.Options;

namespace Sim.Systems
{
    /// <summary>Electrical lights: five switches and a fading light level. Spec: s6.</summary>
    public sealed class LightsSystem : ISystem
    {
        public const int NumSwitches = 5;
        public const int SwitchMask = 0x1F;
        public const int MaxValue = 255;
        /// <summary>Spec: 3 of 255 per 0.03 s step.</summary>
        public const int StepSize = 3;
        public const float StepSeconds = 0.03f;
        public const float MinLightRadius = 1f;
        public const float MaxLightRadius = 5f;

        private readonly IRandom _rng;
        private float _timer;

        /// <summary>Starts fixed with a random switch layout and full light.</summary>
        public LightsSystem(IRandom rng)
        {
            _rng = rng;
            int start = rng.Next(SwitchMask + 1);
            Expected = start;
            Actual = start;
            Value = MaxValue;
        }

        public int Expected { get; private set; }
        public int Actual { get; private set; }
        /// <summary>0..255.</summary>
        public int Value { get; private set; }
        /// <summary>0..1.</summary>
        public float Level => Value / (float)MaxValue;
        public bool IsActive => Expected != Actual;

        /// <summary>
        /// Saboteur breaks the lights: bit 2 is always flipped, the other four with probability 0.5.
        /// Spec: s3 lights mask. Returns the flipped mask.
        /// </summary>
        public int Sabotage()
        {
            int mask = 1 << 2;
            for (int i = 0; i < NumSwitches; i++)
                if (i != 2 && _rng.Next(2) == 1) mask |= 1 << i;
            Actual ^= mask;
            return mask;
        }

        /// <summary>A player flips one switch (index 0..4). Spec: s6 UpdateSystem.</summary>
        public SystemResult FlipSwitch(int index, bool eligible)
        {
            if (index < 0 || index >= NumSwitches) return SystemResult.InvalidConsole;
            if (!eligible) return SystemResult.NotEligible;
            Actual ^= 1 << index;
            return SystemResult.Ok;
        }

        /// <summary>
        /// Moves Value toward 255 when switches match, toward 0 otherwise.
        /// Deviation: remainder time is carried, so the fade speed does not depend on the tick length
        /// (the original dropped it; 85 steps of 0.03 s = 2.55 s end to end).
        /// </summary>
        public void Tick(float dt)
        {
            _timer += dt;
            while (_timer >= StepSeconds)
            {
                _timer -= StepSeconds;
                Value = Expected == Actual ? Math.Min(Value + StepSize, MaxValue) : Math.Max(Value - StepSize, 0);
            }
        }

        /// <summary>
        /// Vision radius. Spec: s6.1. Dead: Max (no option); impostor: Max x ImpostorLightMod;
        /// crew: (Min + level x (Max - Min)) x CrewLightMod. Pass level = 1 on maps without lights.
        /// </summary>
        public static float VisionRadius(float level, bool isImpostor, bool isDead, GameOptions options,
            float minRadius = MinLightRadius, float maxRadius = MaxLightRadius)
        {
            if (isDead) return maxRadius;
            if (isImpostor) return maxRadius * options.ImpostorLightMod;
            float l = Math.Min(Math.Max(level, 0f), 1f);
            return (minRadius + l * (maxRadius - minRadius)) * options.CrewLightMod;
        }
    }
}
