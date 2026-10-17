using Sim.Core;

namespace Sim.Roles
{
    public enum AbilityRejectReason : byte
    {
        None,
        WrongRole,
        Dead,
        Disconnected,
        OnCooldown,
        AlreadyActive,
        MeetingActive,
        NoUsesLeft,
        TargetInvalid,
        TargetNotVisible,
    }

    /// <summary>
    /// Disguiser (shapeshifter-like) state: cooldown and disguise duration.
    /// Spec: kill-roles-and-abilities.md s7.10. Hardening: the original host skips cooldown checks.
    /// </summary>
    public sealed class DisguiserAbility
    {
        private readonly float _cooldown;
        private readonly float _duration;

        public DisguiserAbility(float cooldown, float duration)
        {
            _cooldown = cooldown;
            _duration = duration;
        }

        public static DisguiserAbility FromDefaults()
        {
            RoleDefinition d = RoleTable.Get(RoleId.Disguiser);
            return new DisguiserAbility(d.AbilityCooldown, d.AbilityDuration);
        }

        public float CooldownRemaining { get; private set; }
        public float DurationRemaining { get; private set; }
        public bool IsDisguised { get; private set; }
        public byte? DisguisedAs { get; private set; }

        public AbilityRejectReason TryDisguise(PlayerState user, PlayerState? target, bool meetingActive)
        {
            if (user.Role != RoleId.Disguiser) return AbilityRejectReason.WrongRole;
            if (user.IsDead) return AbilityRejectReason.Dead;
            if (user.Disconnected) return AbilityRejectReason.Disconnected;
            if (meetingActive) return AbilityRejectReason.MeetingActive;
            if (IsDisguised) return AbilityRejectReason.AlreadyActive;
            if (CooldownRemaining > 0f) return AbilityRejectReason.OnCooldown;
            if (target == null || target.Id == user.Id || target.Disconnected) return AbilityRejectReason.TargetInvalid;
            IsDisguised = true;
            DisguisedAs = target.Id;
            DurationRemaining = _duration;
            return AbilityRejectReason.None;
        }

        /// <summary>Revert (manual, timer, or forced); starts the cooldown.</summary>
        public void Revert()
        {
            if (!IsDisguised) return;
            IsDisguised = false;
            DisguisedAs = null;
            DurationRemaining = 0f;
            CooldownRemaining = _cooldown;
        }

        /// <summary>Advance timers; no ticking during a meeting. Duration 0 means until reverted.</summary>
        public void Tick(float dt, bool meetingActive)
        {
            if (meetingActive) return;
            if (IsDisguised)
            {
                if (_duration > 0f)
                {
                    DurationRemaining -= dt;
                    if (DurationRemaining <= 0f) Revert();
                }
            }
            else if (CooldownRemaining > 0f)
            {
                CooldownRemaining = CooldownRemaining - dt < 0f ? 0f : CooldownRemaining - dt;
            }
        }
    }
}
