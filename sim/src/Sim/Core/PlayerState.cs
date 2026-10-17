using System.Collections.Generic;

namespace Sim.Core
{
    public sealed class TaskState
    {
        public TaskState(int id, int typeId, int steps) { Id = id; TypeId = typeId; Steps = steps; }

        /// <summary>Index into the player's task list (unique per player).</summary>
        public int Id { get; }
        /// <summary>Content id of the task definition.</summary>
        public int TypeId { get; }
        public int Steps { get; }
        public int StepsDone { get; set; }
        public bool Complete => StepsDone >= Steps;
    }

    public sealed class PlayerState
    {
        public PlayerState(byte id) { Id = id; }

        public byte Id { get; }
        public RoleId Role { get; set; } = RoleId.Crew;
        public Team Team { get; set; } = Team.Crew;
        public bool IsDead { get; set; }
        public bool Disconnected { get; set; }
        public Vec2 Position { get; set; }
        public bool InVent { get; set; }

        /// <summary>Seconds until this player may kill again. Server-owned.</summary>
        public float KillTimer { get; set; }
        public int RemainingEmergencies { get; set; }
        /// <summary>Player id of the guardian shielding this player, or null.</summary>
        public byte? ProtectedBy { get; set; }

        public List<TaskState> Tasks { get; } = new List<TaskState>();

        public bool IsAlive => !IsDead && !Disconnected;
    }
}
