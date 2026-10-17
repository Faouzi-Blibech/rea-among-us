using System;

namespace Sim.Core
{
    /// <summary>Engine-agnostic 2D vector. Map units; one unit is roughly one player width.</summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float X;
        public readonly float Y;

        public Vec2(float x, float y) { X = x; Y = y; }

        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public float Length => MathF.Sqrt(X * X + Y * Y);
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);

        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => a + (b - a) * t;

        public bool Equals(Vec2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Vec2 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X}, {Y})";
    }
}
