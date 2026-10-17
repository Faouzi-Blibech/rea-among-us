namespace Sim.Rules.Kill
{
    /// <summary>Round-level and target-transient flags the kill rules cannot read from PlayerState.</summary>
    public readonly struct KillContext
    {
        public KillContext(bool gameOver, bool meetingActive, bool targetBusy)
        {
            GameOver = gameOver;
            MeetingActive = meetingActive;
            TargetBusy = targetBusy;
        }

        public bool GameOver { get; }
        public bool MeetingActive { get; }
        /// <summary>Target is on a ladder, on a moving platform or in the enter-vent animation.</summary>
        public bool TargetBusy { get; }
    }
}
