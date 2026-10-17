namespace Sim.Systems
{
    /// <summary>Common surface of every ship system state machine. Spec: sabotage-and-systems.md s2.1.</summary>
    public interface ISystem
    {
        /// <summary>True while the system is in its sabotaged state (pauses the shared sabotage cooldown).</summary>
        bool IsActive { get; }

        /// <summary>Advance by dt seconds. Spec: s2.2.</summary>
        void Tick(float dt);
    }

    /// <summary>Why a system request was accepted or rejected.</summary>
    public enum SystemResult : byte
    {
        Ok,
        /// <summary>The target system is not currently sabotaged / closed.</summary>
        NotActive,
        AlreadyActive,
        InvalidConsole,
        WrongCode,
        /// <summary>Caller is dead, not an allowed role, or out of range (geometry is checked by the caller).</summary>
        NotEligible,
        OnCooldown,
        MeetingInProgress,
        UnavailableOnMap,
        UnknownDoor,
    }

    /// <summary>Sabotages a saboteur can trigger through the shared cooldown. Spec: s3 (target byte table).</summary>
    public enum SabotageKind : byte
    {
        /// <summary>Two-console meltdown (spec: system 3).</summary>
        Reactor,
        /// <summary>Code-entry meltdown (spec: system 8).</summary>
        Oxygen,
        Lights,
        Comms,
        /// <summary>Second meltdown on the Large profile (spec: system 21).</summary>
        LabReactor,
        /// <summary>Appearance scramble (spec: system 57).</summary>
        MushroomMixup,
        /// <summary>Window-based meltdown (spec: system 58).</summary>
        Heli,
    }
}
