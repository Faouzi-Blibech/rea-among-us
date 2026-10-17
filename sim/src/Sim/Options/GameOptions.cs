namespace Sim.Options
{
    public enum TaskBarMode : byte { Normal, MeetingsOnly, Invisible }

    /// <summary>
    /// Lobby options. Defaults follow the recovered values in docs/logic/movement-network-options.md
    /// section 5; tune after playtests.
    /// </summary>
    public sealed class GameOptions
    {
        public int MaxPlayers { get; set; } = 10;
        public int NumImpostors { get; set; } = 1;

        public float PlayerSpeedMod { get; set; } = 1f;
        public float CrewLightMod { get; set; } = 1f;
        public float ImpostorLightMod { get; set; } = 1.5f;

        public float KillCooldown { get; set; } = 15f;
        /// <summary>Index into <see cref="KillDistances"/>: 0 short, 1 medium, 2 long.</summary>
        public int KillDistance { get; set; } = 1;
        public static readonly float[] KillDistances = { 1.0f, 1.8f, 2.5f };

        public int NumEmergencyMeetings { get; set; } = 1;
        public float EmergencyCooldown { get; set; } = 15f;
        public float DiscussionTime { get; set; } = 15f;
        /// <summary>Seconds of voting; 0 or less means no limit.</summary>
        public float VotingTime { get; set; } = 120f;
        public bool ConfirmEjects { get; set; } = true;
        public bool AnonymousVotes { get; set; }

        public int NumCommonTasks { get; set; } = 1;
        public int NumLongTasks { get; set; } = 1;
        public int NumShortTasks { get; set; } = 2;
        public bool GhostsDoTasks { get; set; } = true;
        public TaskBarMode TaskBarMode { get; set; } = TaskBarMode.Normal;

        public float KillRange
        {
            get
            {
                int i = KillDistance < 0 ? 0 : KillDistance >= KillDistances.Length ? KillDistances.Length - 1 : KillDistance;
                return KillDistances[i];
            }
        }
    }
}
