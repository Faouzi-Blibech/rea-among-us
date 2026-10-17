using System.Collections.Generic;
using Sim.Core;
using Sim.Options;

namespace Sim.Rules.Assign
{
    public enum TaskCategory : byte { Common, Long, Short }

    /// <summary>One catalog entry handed in by the map content.</summary>
    public sealed class TaskDefinition
    {
        public TaskDefinition(int typeId, int steps, TaskCategory category)
        {
            TypeId = typeId; Steps = steps; Category = category;
        }
        public int TypeId { get; }
        public int Steps { get; }
        public TaskCategory Category { get; }
    }

    public static class TaskAssigner
    {
        public const int MaxIterations = 1000;

        /// <summary>
        /// Gives every player the same common tasks, then long and short tasks from shared round-robin
        /// pools with per-player type de-duplication. Task ids are list positions (common, long, short).
        /// Simplified: the "scan task at most three times" quirk is not implemented.
        /// Spec: meetings-votes-wins-tasks.md s8.2, s8.3.
        /// </summary>
        public static void Assign(IReadOnlyList<PlayerState> players, IReadOnlyList<TaskDefinition> catalog, GameOptions options, IRandom rng)
        {
            List<TaskDefinition> commonPool = Pool(catalog, TaskCategory.Common, rng);
            List<TaskDefinition> longPool = Pool(catalog, TaskCategory.Long, rng);
            List<TaskDefinition> shortPool = Pool(catalog, TaskCategory.Short, rng);

            int numCommon = options.NumCommonTasks, numLong = options.NumLongTasks, numShort = options.NumShortTasks;
            if (numCommon + numLong + numShort == 0) numShort = 1;

            var commons = new List<TaskDefinition>();
            int commonStart = 0;
            AddFromList(ref commonStart, numCommon, commons, new HashSet<int>(), commonPool, rng);
            // Pool too small or type-limited: top up with random leftovers ("not enough common tasks" stops it).
            var leftovers = new List<TaskDefinition>(commonPool);
            foreach (TaskDefinition c in commons) leftovers.Remove(c);
            while (commons.Count < numCommon && leftovers.Count > 0)
            {
                int i = rng.Next(leftovers.Count);
                commons.Add(leftovers[i]);
                leftovers.RemoveAt(i);
            }

            int longStart = 0, shortStart = 0;
            foreach (PlayerState p in players)
            {
                var used = new HashSet<int>();
                var list = new List<TaskDefinition>(commons);
                AddFromList(ref longStart, numLong, list, used, longPool, rng);
                AddFromList(ref shortStart, numShort, list, used, shortPool, rng);

                p.Tasks.Clear();
                for (int i = 0; i < list.Count; i++)
                    p.Tasks.Add(new TaskState(i, list[i].TypeId, list[i].Steps));
            }
        }

        private static List<TaskDefinition> Pool(IReadOnlyList<TaskDefinition> catalog, TaskCategory cat, IRandom rng)
        {
            var pool = new List<TaskDefinition>();
            foreach (TaskDefinition d in catalog) if (d.Category == cat) pool.Add(d);
            Shuffle(pool, rng);
            return pool;
        }

        private static void Shuffle(List<TaskDefinition> list, IRandom rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                TaskDefinition tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        private static void AddFromList(ref int start, int count, List<TaskDefinition> output, HashSet<int> used, List<TaskDefinition> pool, IRandom rng)
        {
            if (pool.Count == 0) return;
            int added = 0, guard = MaxIterations;
            while (added < count)
            {
                if (--guard == 0) return;
                if (start >= pool.Count)
                {
                    start = 0;
                    Shuffle(pool, rng);
                    bool allUsed = true;
                    foreach (TaskDefinition d in pool) if (!used.Contains(d.TypeId)) { allUsed = false; break; }
                    if (allUsed) used.Clear();
                }
                TaskDefinition t = pool[start++];
                if (used.Add(t.TypeId))
                {
                    output.Add(t);
                    added++;
                }
            }
        }
    }
}
