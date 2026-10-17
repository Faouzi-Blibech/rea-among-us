namespace Sim.Options.Presets
{
    /// <summary>
    /// Recommended option tables by player count and the impostor cap.
    /// Spec: movement-network-options.md s5.2-5.4. Tables are indexed by player count 0..15.
    /// </summary>
    public static class OptionPresets
    {
        public const int MinPlayers = 4;
        public const int MaxPlayersIndexed = 15;
        public const int FallbackMaxImpostors = 3;

        private static readonly int[] KillCooldowns = { 0, 0, 0, 0, 45, 30, 15, 35, 30, 25, 20, 20, 20, 20, 20, 20 };
        private static readonly int[] RecommendedImpostorTable = { 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 3 };
        private static readonly int[] MaxImpostorTable = { 0, 0, 0, 0, 1, 1, 1, 2, 2, 3, 3, 3, 3, 3, 3, 3 };

        /// <summary>Clamps to 4..15 (spec s5.3).</summary>
        public static int ClampPlayers(int players) => players < MinPlayers ? MinPlayers : players > MaxPlayersIndexed ? MaxPlayersIndexed : players;

        /// <summary>Spec s5.2 RecommendedKillCooldown (seconds).</summary>
        public static float RecommendedKillCooldown(int players) => KillCooldowns[ClampPlayers(players)];

        /// <summary>Spec s5.2 RecommendedImpostors.</summary>
        public static int RecommendedImpostors(int players) => RecommendedImpostorTable[ClampPlayers(players)];

        /// <summary>
        /// Spec s5.2/s5.4 cap lookup for the real player count: table value for 0..15 (0 below 4 players),
        /// fallback 3 for 16 or more.
        /// </summary>
        public static int MaxImpostors(int players)
        {
            if (players < 0) return 0;
            return players < MaxImpostorTable.Length ? MaxImpostorTable[players] : FallbackMaxImpostors;
        }

        /// <summary>
        /// Spec s5.4: 1 if configured is below 1, else min(configured, cap). The original yields 0
        /// below 4 players; callers wanting at least 1 should apply their own floor.
        /// </summary>
        public static int AdjustedImpostors(int configured, int players)
            => configured < 1 ? 1 : System.Math.Min(configured, MaxImpostors(players));

        /// <summary>
        /// Spec s5.3: apply the recommended values. Offline sets impostors from the table and emergency
        /// cooldown 0; online leaves the impostor count and sets the cooldown to 15.
        /// MaxPlayers and GhostsDoTasks are untouched.
        /// </summary>
        public static void Apply(GameOptions options, int players, bool isOnline)
        {
            int n = ClampPlayers(players);
            if (!isOnline)
            {
                options.NumImpostors = RecommendedImpostorTable[n];
                options.EmergencyCooldown = 0f;
            }
            else
            {
                options.EmergencyCooldown = 15f;
            }
            options.AnonymousVotes = false;
            options.ConfirmEjects = true;
            options.NumEmergencyMeetings = 1;
            options.PlayerSpeedMod = 1f;
            options.CrewLightMod = 1f;
            options.ImpostorLightMod = 1.5f;
            options.DiscussionTime = 15f;
            options.VotingTime = 120f;
            options.KillCooldown = KillCooldowns[n];
            options.NumCommonTasks = 1;
            options.NumLongTasks = 1;
            options.NumShortTasks = 2;
            options.KillDistance = 1;
            options.TaskBarMode = TaskBarMode.Normal;
        }
    }
}
