namespace Sim.Systems
{
    /// <summary>
    /// Shared cooldown between special sabotages. Spec: s3.
    /// Counts down only while no special sabotage is active; 30 s after a trigger, 10 s initial lockout.
    /// </summary>
    public sealed class SabotageCooldown
    {
        public const float AfterSabotageSeconds = 30f;
        public const float InitialSeconds = 10f;

        public float Timer { get; private set; }
        public bool Initial { get; private set; }

        /// <summary>Spec: SetInitialSabotageCooldown. Game start and (our choice) after each meeting.</summary>
        public void SetInitial()
        {
            Timer = InitialSeconds;
            Initial = true;
        }

        /// <summary>Spec: Deteriorate. <paramref name="anySpecialActive"/> freezes the timer.</summary>
        public void Tick(float dt, bool anySpecialActive)
        {
            if (Timer > 0f && !anySpecialActive)
            {
                Timer -= dt;
                if (Timer <= 0f) { Timer = 0f; Initial = false; }
            }
        }

        public bool IsReady => Timer <= 0f;

        /// <summary>Fill fraction for UI: Timer / 10 during the initial lockout, else Timer / 30.</summary>
        public float Percent => Timer / (Initial ? InitialSeconds : AfterSabotageSeconds);

        /// <summary>Called when a sabotage was accepted.</summary>
        public void Trigger()
        {
            Timer = AfterSabotageSeconds;
            Initial = false;
        }
    }
}
