using Sim.Core;

namespace Sim.Systems
{
    /// <summary>Communications sabotage, both variants. Spec: s4.4.</summary>
    public interface ICommsSystem : ISystem
    {
        void Start();
        void Clear();
    }

    /// <summary>Simple on/off comms (spec: HudOverride variant): fixed by one fix action.</summary>
    public sealed class SimpleComms : ICommsSystem
    {
        public bool IsActive { get; private set; }
        public void Start() => IsActive = true;
        public void Clear() => IsActive = false;
        public void Tick(float dt) { }

        public SystemResult Fix(bool eligible)
        {
            if (!IsActive) return SystemResult.NotActive;
            if (!eligible) return SystemResult.NotEligible;
            IsActive = false;
            return SystemResult.Ok;
        }
    }

    /// <summary>
    /// Two-console window comms (spec: HqHud variant): both codes inside one 10 s window.
    /// No countdown, no game-over. The server generates the code (the original did it per client).
    /// </summary>
    public sealed class WindowComms : ICommsSystem
    {
        private readonly CodeWindow _window;

        public WindowComms(IRandom rng) { _window = new CodeWindow(rng); }

        public bool IsActive { get; private set; }
        public int TargetCode => _window.TargetCode;
        public float WindowTimer => _window.Timer;

        public void Start()
        {
            IsActive = true;
            _window.Begin();
        }

        public void Clear() => IsActive = false;

        public void Tick(float dt)
        {
            if (IsActive) _window.Tick(dt);
        }

        public SystemResult EnterCode(int console, int code, bool eligible)
        {
            if (!IsActive) return SystemResult.NotActive;
            if (!eligible) return SystemResult.NotEligible;
            var r = _window.Submit(console, code);
            if (r == SystemResult.Ok && _window.AllDone) IsActive = false;
            return r;
        }
    }
}
