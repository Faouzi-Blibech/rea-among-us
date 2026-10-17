using System.Collections.Generic;
using Sim.Core;

namespace Sim.Movement
{
    /// <summary>One movement update: positions with ids FirstSeq + i.</summary>
    public sealed class MovementPacket
    {
        public ushort FirstSeq { get; }
        public QuantizedPos[] Positions { get; }
        public MovementPacket(ushort firstSeq, QuantizedPos[] positions) { FirstSeq = firstSeq; Positions = positions; }
        public ushort LastSeq => SequenceId.BatchId(FirstSeq, Positions.Length - 1);
    }

    /// <summary>
    /// Owner side of position replication: samples once per fixed tick, flushes every 0.1 s.
    /// Pure data out, no sockets. Spec: movement-network-options.md s2.3.
    /// </summary>
    public sealed class MovementSender
    {
        public const float SendInterval = 0.1f;
        public const float MoveThreshold = 0.0001f;
        public const int DirtyOnMove = 2;

        private readonly List<Vec2> _queue = new List<Vec2>();
        private Vec2 _lastSent;
        private float _timer;
        private int _dirty;

        public ushort LastSequenceId { get; private set; }
        public bool Paused { get; set; }

        public MovementSender(Vec2 startPosition, ushort startSequenceId = 0)
        {
            _lastSent = startPosition;
            LastSequenceId = startSequenceId;
            _dirty = 3; // an enabled object is sent a few times (s2.1)
        }

        /// <summary>
        /// Advance one fixed tick of dt seconds with the body at the given position.
        /// Returns a packet when the 0.1 s flush fires and the object is dirty, else null.
        /// </summary>
        public MovementPacket? Tick(Vec2 position, float dt)
        {
            if (Paused) return null;
            if (Vec2.Distance(position, _lastSent) > MoveThreshold)
            {
                _queue.Add(position);
                _dirty = DirtyOnMove;
            }

            _timer += dt;
            if (_timer < SendInterval - 1e-6f) return null;
            _timer = 0f;
            if (_dirty <= 0) return null;
            _dirty--;
            return Flush();
        }

        private MovementPacket Flush()
        {
            if (_queue.Count == 0) _queue.Add(_lastSent); // at least one point (trailing duplicate)
            var pts = new QuantizedPos[_queue.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = PositionCodec.Encode(_queue[i]);
            _lastSent = _queue[_queue.Count - 1];
            _queue.Clear();
            ushort first = SequenceId.Add(LastSequenceId, 1);
            var packet = new MovementPacket(first, pts);
            LastSequenceId = packet.LastSeq;
            return packet;
        }
    }
}
