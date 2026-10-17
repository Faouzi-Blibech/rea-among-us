using Sim.Core;

namespace Sim.Roles
{
    /// <summary>
    /// Guardian ghost shield: cooldown, shield duration, protects one living player at a time.
    /// Spec: kill-roles-and-abilities.md s6. The shield also ends at meeting start (s6) and the
    /// cooldown resets to 0 after a meeting. Duration expiry is our choice (spec: Unknown); 0 = until meeting.
    /// </summary>
    public sealed class GuardianAbility
    {
        private readonly float _cooldown;
        private readonly float _duration;
        private PlayerState? _shielded;

        public GuardianAbility(float cooldown, float duration)
        {
            _cooldown = cooldown;
            _duration = duration;
        }

        public static GuardianAbility FromDefaults()
        {
            RoleDefinition d = RoleTable.Get(RoleId.GuardianGhost);
            return new GuardianAbility(d.AbilityCooldown, d.AbilityDuration);
        }

        public float CooldownRemaining { get; private set; }
        public float ShieldRemaining { get; private set; }

        /// <summary>Checks follow s6 CheckProtect: guardian role, crew team, connected, target alive; plus dead user and cooldown.</summary>
        public AbilityRejectReason TryProtect(PlayerState guardian, PlayerState? target, bool meetingActive)
        {
            if (guardian.Role != RoleId.GuardianGhost || guardian.Team != Team.Crew) return AbilityRejectReason.WrongRole;
            if (guardian.Disconnected) return AbilityRejectReason.Disconnected;
            if (!guardian.IsDead) return AbilityRejectReason.WrongRole;
            if (meetingActive) return AbilityRejectReason.MeetingActive;
            if (CooldownRemaining > 0f) return AbilityRejectReason.OnCooldown;
            if (target == null || target.Id == guardian.Id || target.IsDead || target.Disconnected)
                return AbilityRejectReason.TargetInvalid;
            ClearShield();
            target.ProtectedBy = guardian.Id;
            _shielded = target;
            ShieldRemaining = _duration;
            CooldownRemaining = _cooldown;
            return AbilityRejectReason.None;
        }

        public void Tick(float dt, bool meetingActive)
        {
            if (meetingActive) return;
            if (CooldownRemaining > 0f) CooldownRemaining = CooldownRemaining - dt < 0f ? 0f : CooldownRemaining - dt;
            if (_shielded != null && _duration > 0f)
            {
                ShieldRemaining -= dt;
                if (ShieldRemaining <= 0f) ClearShield();
            }
        }

        /// <summary>Meeting start removes the shield (s1 table); meeting end clears the cooldown.</summary>
        public void OnMeetingStart() => ClearShield();

        public void OnMeetingEnd() => CooldownRemaining = 0f;

        private void ClearShield()
        {
            if (_shielded != null && _shielded.ProtectedBy.HasValue) _shielded.ProtectedBy = null;
            _shielded = null;
            ShieldRemaining = 0f;
        }
    }
}
