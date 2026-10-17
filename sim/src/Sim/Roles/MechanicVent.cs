namespace Sim.Roles
{
    /// <summary>
    /// Mechanic vent limits: cooldown to enter, max time inside, forced exit on comms sabotage.
    /// Spec: kill-roles-and-abilities.md s7.5. The cooldown start point is Unknown in the spec;
    /// we start it on exit.
    /// </summary>
    public sealed class MechanicVent
    {
        private readonly float _cooldown;
        private readonly float _maxTime;

        public MechanicVent(float cooldown, float maxTimeInVent)
        {
            _cooldown = cooldown;
            _maxTime = maxTimeInVent;
        }

        public static MechanicVent FromDefaults()
        {
            RoleDefinition d = RoleTable.Get(Core.RoleId.Mechanic);
            return new MechanicVent(d.AbilityCooldown, d.AbilityDuration);
        }

        public float CooldownRemaining { get; private set; }
        public float TimeInVentRemaining { get; private set; }

        /// <summary>Entering is blocked while cooling down; leaving never is (s7.5 CanUse).</summary>
        public bool CanEnter => CooldownRemaining <= 0f;

        public void OnEnter() => TimeInVentRemaining = _maxTime;

        public void OnExit() => CooldownRemaining = _cooldown;

        /// <summary>Cooldown is cleared after a meeting (s7.5 OnVotingComplete).</summary>
        public void OnMeetingEnd() => CooldownRemaining = 0f;

        /// <summary>
        /// Advance timers (skip while a meeting runs). Returns true when the player must be forced out:
        /// in-vent time ran out (max time 0 means unlimited) or comms are sabotaged.
        /// </summary>
        public bool Tick(float dt, bool inVent, bool commsSabotaged)
        {
            if (CooldownRemaining > 0f) CooldownRemaining = CooldownRemaining - dt < 0f ? 0f : CooldownRemaining - dt;
            if (!inVent) return false;
            if (_maxTime > 0f) TimeInVentRemaining -= dt;
            return (_maxTime > 0f && TimeInVentRemaining < 0f) || commsSabotaged;
        }
    }
}
