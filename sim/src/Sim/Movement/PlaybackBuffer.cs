using System;
using System.Collections.Generic;
using Sim.Core;

namespace Sim.Movement
{
    /// <summary>
    /// Remote-player jitter buffer and snap barrier (presentation side; a client port keeps these constants).
    /// Deterministic given the sequence of Receive/Step(dt) calls. Spec: movement-network-options.md s2.4-2.7.
    /// </summary>
    public sealed class PlaybackBuffer
    {
        public const float VelocityCap = 10f;
        public const int PopCount = 6;
        public const int SkipFirst = 12;
        public const int SkipSecond = 14;
        /// <summary>Count above this selects the high rubberband target.</summary>
        public const int HealthyCount = 5;
        public const float RubberbandHigh = 0.995f;
        public const float RubberbandLow = 0.5f;
        public const float RubberbandRate = 3f;
        public const float PassEpsilon = 0.003f;
        public const float SnapDistanceMulti = 0.05f;
        public const float SnapDistanceSingle = 0.01f;

        private readonly Queue<Vec2> _queue = new Queue<Vec2>();
        private Vec2 _lastPosition;
        private float _idealSpeed;
        private Vec2? _tempSnap;

        public Vec2 Position { get; private set; }
        public Vec2 Velocity { get; private set; }
        public ushort LastSequenceId { get; private set; }
        public float Rubberband { get; private set; } = 1f;
        public int QueueCount => _queue.Count;
        public bool Paused { get; set; }
        /// <summary>True while a vent animation makes snaps deferred (s2.7).</summary>
        public bool Invisible { get; set; }

        public PlaybackBuffer(Vec2 start, ushort sequenceId = 0)
        {
            Position = start;
            _lastPosition = start;
            LastSequenceId = sequenceId;
            _queue.Enqueue(start); // never empty (Awake)
        }

        /// <summary>Accept a batch; stale or duplicate points are dropped silently. Spec s2.4.</summary>
        public void Receive(ushort firstSeq, IReadOnlyList<QuantizedPos> points)
        {
            if (Paused) return;
            Vec2 lastRead = Position;
            bool any = false;
            for (int i = 0; i < points.Count; i++)
            {
                ushort id = SequenceId.BatchId(firstSeq, i);
                Vec2 p = PositionCodec.Decode(points[i]);
                lastRead = p; any = true;
                if (SequenceId.IsNewer(id, LastSequenceId))
                {
                    LastSequenceId = id;
                    _queue.Enqueue(p);
                }
            }
            if (any) _tempSnap = Invisible ? lastRead : (Vec2?)null;
        }

        /// <summary>Spec s2.6: snap with minimum id lastSeq + 2. Returns whether it was accepted.</summary>
        public bool SnapTo(Vec2 pos) => SnapTo(pos, SequenceId.Add(LastSequenceId, 2));

        /// <summary>Spec s2.6: freeze in place and bump the id by one.</summary>
        public bool Halt() => SnapTo(Position, SequenceId.Add(LastSequenceId, 1));

        /// <summary>Spec s2.6: teleport with an explicit id; ignored unless newer than the last accepted id.</summary>
        public bool SnapTo(Vec2 pos, ushort minSid)
        {
            if (!SequenceId.IsNewer(minSid, LastSequenceId)) return false;
            if (Invisible) { _tempSnap = pos; return true; }
            _queue.Clear();
            LastSequenceId = minSid;
            _tempSnap = null;
            Position = pos;
            _lastPosition = pos;
            Velocity = Vec2.Zero;
            return true;
        }

        /// <summary>
        /// Spec s2.6: teleport RPC as sent by the authority. Optional local snap (lastSeq + 1),
        /// wire id = lastSeq(after) + 2.
        /// </summary>
        public ushort BuildSnapRpcId(Vec2 pos, bool applyLocally)
        {
            if (applyLocally) SnapTo(pos, SequenceId.Add(LastSequenceId, 1));
            return SequenceId.Add(LastSequenceId, 2);
        }

        /// <summary>Receiving side of the teleport RPC (same acceptance rule).</summary>
        public bool HandleSnapRpc(Vec2 pos, ushort id) => SnapTo(pos, id);

        /// <summary>One fixed tick of dt seconds, including the physics integration. Spec s2.5.</summary>
        public void Step(float dt)
        {
            if (Paused) return;
            if (_tempSnap.HasValue && !Invisible)
            {
                Vec2 v = _tempSnap.Value;
                _tempSnap = null;
                SnapTo(v);
            }
            if (_queue.Count == 0) return;
            SkipExcessive(dt);
            UpdateRubberband(dt);
            MoveToward(dt);
            Position = Position + Velocity * dt;
        }

        private void SkipExcessive(float dt)
        {
            if (_queue.Count < SkipFirst) return;
            Position = _queue.Peek();
            MoveToward(dt);
            if (_queue.Count >= SkipSecond)
            {
                Position = _queue.Peek();
                MoveToward(dt);
            }
        }

        private void UpdateRubberband(float dt)
        {
            float target = _queue.Count > HealthyCount ? RubberbandHigh : RubberbandLow;
            float t = dt * RubberbandRate;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            Rubberband += (target - Rubberband) * t;
        }

        private static bool DidPass(Vec2 next, Vec2 last, Vec2 current)
        {
            float err = Math.Abs(Vec2.Distance(last, current) - (Vec2.Distance(last, next) + Vec2.Distance(current, next)));
            return err < PassEpsilon;
        }

        private static Vec2 Direction(Vec2 from, Vec2 to)
        {
            float d = Vec2.Distance(from, to);
            return d <= 1e-5f ? Vec2.Zero : (to - from) * (1f / d);
        }

        private void MoveToward(float dt)
        {
            Vec2 next = _queue.Peek();
            Vec2 current = Position;
            bool extend = !DidPass(next, _lastPosition, current) && _queue.Count < PopCount;
            Vec2 dir;
            if (!extend)
            {
                if (_queue.Count > 1)
                {
                    Vec2 popped = _queue.Dequeue();
                    if (Vec2.Distance(Position, popped) > SnapDistanceMulti) Position = popped;
                    next = _queue.Peek();
                    _idealSpeed = dt > 0f ? Vec2.Distance(next, Position) / dt : 0f;
                    dir = Direction(Position, next);
                }
                else
                {
                    if (Vec2.Distance(Position, next) <= SnapDistanceSingle) return;
                    Position = next;
                    Velocity = Vec2.Zero;
                    _lastPosition = Position;
                    return;
                }
            }
            else
            {
                dir = Direction(current, next);
            }

            Vec2 vel = dir * (_idealSpeed * Rubberband);
            float sq = vel.X * vel.X + vel.Y * vel.Y;
            if (sq > VelocityCap * VelocityCap) vel = vel * (VelocityCap / MathF.Sqrt(sq));
            Velocity = vel;
            _lastPosition = Position;
        }
    }
}
