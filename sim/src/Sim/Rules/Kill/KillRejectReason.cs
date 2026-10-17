namespace Sim.Rules.Kill
{
    /// <summary>Why the server refused a kill request. Order follows the validation order.</summary>
    public enum KillRejectReason : byte
    {
        None,
        GameOver,
        KillerDead,
        KillerNotImpostor,
        KillerRoleCannotKill,
        KillerDisconnected,
        OnCooldown,
        TargetMissing,
        TargetIsSelf,
        TargetDead,
        TargetDisconnected,
        TargetNotKillable,
        TargetInVent,
        TargetBusy,
        MeetingActive,
        OutOfRange,
        WallBetween,
    }
}
