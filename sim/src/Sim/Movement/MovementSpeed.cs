namespace Sim.Movement
{
    /// <summary>Player speed. Spec: movement-network-options.md s3.</summary>
    public static class MovementSpeed
    {
        public const float BaseSpeed = 2.5f;
        public const float GhostSpeed = 3.0f;

        /// <summary>Units per second: 2.5 * mod alive, 3.0 * mod as a ghost. No clamp on the modifier.</summary>
        public static float TrueSpeed(float playerSpeedMod, bool isGhost)
            => (isGhost ? GhostSpeed : BaseSpeed) * playerSpeedMod;
    }
}
