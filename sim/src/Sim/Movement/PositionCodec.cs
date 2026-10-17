using System;
using Sim.Core;

namespace Sim.Movement
{
    /// <summary>A position quantized to two 16-bit values for the wire.</summary>
    public readonly struct QuantizedPos : IEquatable<QuantizedPos>
    {
        public readonly ushort X;
        public readonly ushort Y;
        public QuantizedPos(ushort x, ushort y) { X = x; Y = y; }
        public bool Equals(QuantizedPos other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is QuantizedPos q && Equals(q);
        public override int GetHashCode() => HashCode.Combine(X, Y);
    }

    /// <summary>
    /// Position quantization over [-50, 50] on both axes with 65535 steps.
    /// Spec: movement-network-options.md s1 (Quantization), s2.2.
    /// </summary>
    public static class PositionCodec
    {
        public const float Min = -50f;
        public const float Max = 50f;
        public const float Range = Max - Min;
        public const float Steps = 65535f;
        /// <summary>Size of one quantization step in map units (about 0.001526).</summary>
        public const float Step = Range / Steps;

        /// <summary>Clamps to the range, then truncates. NaN encodes as the minimum.</summary>
        public static ushort EncodeAxis(float v)
        {
            if (float.IsNaN(v)) return 0;
            float t = (v - Min) / Range;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return (ushort)(t * Steps);
        }

        public static float DecodeAxis(ushort u) => Min + (u / Steps) * Range;

        public static QuantizedPos Encode(Vec2 p) => new QuantizedPos(EncodeAxis(p.X), EncodeAxis(p.Y));
        public static Vec2 Decode(QuantizedPos q) => new Vec2(DecodeAxis(q.X), DecodeAxis(q.Y));
    }
}
