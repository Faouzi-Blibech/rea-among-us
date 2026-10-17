namespace Sim.Systems
{
    /// <summary>
    /// Timed appearance scramble that heals itself after 10 s. Spec: s8.
    /// Only the timer is simulated here; the per-player outfit scramble is presentation.
    /// </summary>
    public sealed class MushroomMixup : ISystem
    {
        public const float HealSeconds = 10f;

        public float SecondsUntilHeal { get; private set; }
        public bool IsActive => SecondsUntilHeal > 0f;

        public void Trigger() => SecondsUntilHeal = HealSeconds;
        public void Clear() => SecondsUntilHeal = 0f;

        public void Tick(float dt)
        {
            if (SecondsUntilHeal > 0f)
            {
                SecondsUntilHeal -= dt;
                if (SecondsUntilHeal < 0f) SecondsUntilHeal = 0f;
            }
        }
    }
}
