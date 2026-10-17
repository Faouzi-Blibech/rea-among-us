namespace Sim.Movement
{
    /// <summary>
    /// 16-bit wrap-around sequence ids for movement packets.
    /// Spec: movement-network-options.md s2.4.
    /// </summary>
    public static class SequenceId
    {
        public const int Window = 32767;

        /// <summary>True when the candidate is 1..32767 ahead of previous (mod 65536). Equal ids are stale.</summary>
        public static bool IsNewer(ushort candidate, ushort previous)
        {
            int d = (ushort)(candidate - previous);
            return d >= 1 && d <= Window;
        }

        public static ushort Add(ushort id, int delta) => (ushort)(id + delta);

        /// <summary>Id of point index in a batch that starts at first.</summary>
        public static ushort BatchId(ushort first, int index) => (ushort)(first + index);
    }
}
