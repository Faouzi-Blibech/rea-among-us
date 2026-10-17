using System;
using System.Collections.Generic;
using Sim.Core;

namespace Sim.Systems
{
    /// <summary>
    /// Base of the sabotages that end the game when the countdown passes zero.
    /// Spec: sabotage-and-systems.md s4.1-4.3, s4.5.
    /// </summary>
    public abstract class CriticalSabotage : ISystem
    {
        /// <summary>Countdown sentinel meaning "stopped / idle". Spec: summary table.</summary>
        public const float StoppedValue = 10000f;

        public float Duration { get; }
        public float Countdown { get; protected set; } = StoppedValue;

        protected CriticalSabotage(float duration) { Duration = duration; }

        /// <summary>Spec: IsActive = Countdown below 10000.</summary>
        public bool IsActive => Countdown < StoppedValue;

        /// <summary>True once the countdown ran below zero: impostors win by sabotage. Spec: s4.5.</summary>
        public bool CountdownExpired => Countdown < 0f;

        /// <summary>True when idle (countdown at the sentinel).</summary>
        public bool IsStopped => Countdown >= StoppedValue;

        /// <summary>Start the sabotage. Ignored while already active (reactor rule). Spec: s4.1.</summary>
        public SystemResult Start()
        {
            if (IsActive) return SystemResult.AlreadyActive;
            Countdown = Duration;
            OnStarted();
            return SystemResult.Ok;
        }

        /// <summary>Repair / cancel: back to the stopped sentinel.</summary>
        public void Clear()
        {
            Countdown = StoppedValue;
            OnCleared();
        }

        public void Tick(float dt)
        {
            if (!IsActive) return;
            Countdown -= dt;
            OnActiveTick(dt);
        }

        protected virtual void OnStarted() { }
        protected virtual void OnCleared() { }
        protected virtual void OnActiveTick(float dt) { }
    }

    /// <summary>
    /// Reactor-style meltdown: fixed when two different consoles are held at the same time.
    /// Spec: s4.1. Hardening: a player holds at most one console, so one player never counts twice.
    /// </summary>
    public sealed class HoldMeltdown : CriticalSabotage
    {
        public const int ConsoleCount = 2;
        private readonly Dictionary<int, int> _heldByPlayer = new Dictionary<int, int>();

        public HoldMeltdown(float duration) : base(duration) { }

        /// <summary>Number of distinct consoles currently held. Spec: UserCount.</summary>
        public int UserCount
        {
            get
            {
                int mask = 0;
                foreach (var c in _heldByPlayer.Values) mask |= 1 << c;
                int n = 0;
                for (int i = 0; i < ConsoleCount; i++) if ((mask & (1 << i)) != 0) n++;
                return n;
            }
        }

        protected override void OnStarted() => _heldByPlayer.Clear();
        protected override void OnCleared() => _heldByPlayer.Clear();

        /// <summary>
        /// Player presses and holds a console. When two distinct consoles are held the sabotage ends.
        /// <paramref name="eligible"/> = alive and in range (checked by the caller).
        /// </summary>
        public SystemResult Hold(int playerId, int console, bool eligible)
        {
            if (!IsActive) return SystemResult.NotActive;
            if (console < 0 || console >= ConsoleCount) return SystemResult.InvalidConsole;
            if (!eligible) return SystemResult.NotEligible;
            _heldByPlayer[playerId] = console;
            if (UserCount >= ConsoleCount) Clear();
            return SystemResult.Ok;
        }

        /// <summary>Player lets go. Also used when a holder dies or disconnects.</summary>
        public void Release(int playerId) => _heldByPlayer.Remove(playerId);
    }

    /// <summary>
    /// Oxygen-style meltdown: two different consoles each need the right code once; progress persists.
    /// Spec: s4.2. The server owns the code (the original trusted the client minigame).
    /// </summary>
    public sealed class CodeMeltdown : CriticalSabotage
    {
        public const int ConsoleCount = 2;
        public const int CodeRange = 100000;
        private readonly IRandom _rng;
        private readonly HashSet<int> _completed = new HashSet<int>();

        public CodeMeltdown(float duration, IRandom rng) : base(duration) { _rng = rng; }

        public int TargetCode { get; private set; }
        public int CompletedCount => _completed.Count;

        protected override void OnStarted()
        {
            _completed.Clear();
            TargetCode = _rng.Next(CodeRange);
        }

        protected override void OnCleared() => _completed.Clear();

        public SystemResult EnterCode(int console, int code, bool eligible)
        {
            if (!IsActive) return SystemResult.NotActive;
            if (console < 0 || console >= ConsoleCount) return SystemResult.InvalidConsole;
            if (!eligible) return SystemResult.NotEligible;
            if (code != TargetCode) return SystemResult.WrongCode;
            _completed.Add(console);
            if (_completed.Count >= ConsoleCount) Clear();
            return SystemResult.Ok;
        }
    }

    /// <summary>
    /// Shared "two codes inside a rolling window" logic used by the helicopter sabotage and the window comms.
    /// Spec: s4.3, s4.4 (Mira/Fungle). Each accepted entry refreshes the window to its full length;
    /// when the window runs out the code changes and entries are forgotten.
    /// </summary>
    public sealed class CodeWindow
    {
        public const float WindowSeconds = 10f;
        public const int ConsoleCount = 2;
        private readonly IRandom _rng;
        private readonly HashSet<int> _completed = new HashSet<int>();

        public CodeWindow(IRandom rng) { _rng = rng; }

        public int TargetCode { get; private set; }
        public float Timer { get; private set; }
        public int CompletedCount => _completed.Count;
        public bool AllDone => _completed.Count >= ConsoleCount;

        public void Begin()
        {
            _completed.Clear();
            NewCode();
        }

        public void Tick(float dt)
        {
            Timer -= dt;
            if (Timer <= 0f)
            {
                _completed.Clear();
                NewCode();
            }
        }

        public SystemResult Submit(int console, int code)
        {
            if (console < 0 || console >= ConsoleCount) return SystemResult.InvalidConsole;
            if (code != TargetCode) return SystemResult.WrongCode;
            _completed.Add(console);
            Timer = WindowSeconds;
            return SystemResult.Ok;
        }

        private void NewCode()
        {
            TargetCode = _rng.Next(CodeMeltdown.CodeRange);
            Timer = WindowSeconds;
        }
    }

    /// <summary>
    /// Window-style meltdown (heli-style): both consoles must be completed within one code window.
    /// Spec: s4.3. Deviation: the countdown returns to idle on fix (the original left a stale value).
    /// </summary>
    public sealed class WindowMeltdown : CriticalSabotage
    {
        private readonly CodeWindow _window;

        public WindowMeltdown(float duration, IRandom rng) : base(duration) { _window = new CodeWindow(rng); }

        public int TargetCode => _window.TargetCode;
        public float WindowTimer => _window.Timer;

        protected override void OnStarted() => _window.Begin();

        protected override void OnActiveTick(float dt) => _window.Tick(dt);

        public SystemResult EnterCode(int console, int code, bool eligible)
        {
            if (!IsActive) return SystemResult.NotActive;
            if (!eligible) return SystemResult.NotEligible;
            var r = _window.Submit(console, code);
            if (r == SystemResult.Ok && _window.AllDone) Clear();
            return r;
        }
    }
}
