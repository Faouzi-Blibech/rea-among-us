namespace Sim.Systems
{
    public enum CommsKind : byte { None, Simple, Window }

    /// <summary>
    /// Which systems a map has and their durations (data, not code branches). Spec: s1.
    /// A duration of 0 means the system does not exist on that map.
    /// </summary>
    public sealed class MapProfile
    {
        public string Name { get; }
        public float ReactorSeconds { get; }
        public float OxygenSeconds { get; }
        /// <summary>Second hold-meltdown (spec: Polus Laboratory, system 21).</summary>
        public float LabReactorSeconds { get; }
        public float HeliSeconds { get; }
        public bool HasLights { get; }
        public CommsKind Comms { get; }
        public DoorMode Doors { get; }
        public bool HasMushroomMixup { get; }
        /// <summary>Default door layout: this many rooms with one door each (override with real geometry).</summary>
        public int DefaultDoorRooms { get; }

        public MapProfile(string name, float reactor, float oxygen, float labReactor, float heli, bool lights,
            CommsKind comms, DoorMode doors, bool mushroom, int defaultDoorRooms)
        {
            Name = name;
            ReactorSeconds = reactor;
            OxygenSeconds = oxygen;
            LabReactorSeconds = labReactor;
            HeliSeconds = heli;
            HasLights = lights;
            Comms = comms;
            Doors = doors;
            HasMushroomMixup = mushroom;
            DefaultDoorRooms = defaultDoorRooms;
        }

        /// <summary>Mirrors the original Skeld: reactor 30, oxygen 30, lights, simple comms, self-reopening doors.</summary>
        public static readonly MapProfile Small =
            new MapProfile("Small", 30f, 30f, 0f, 0f, true, CommsKind.Simple, DoorMode.AutoReopen, false, 6);

        /// <summary>Mirrors the original Mira: reactor 45, oxygen 45, lights, window comms, no doors.</summary>
        public static readonly MapProfile Medium =
            new MapProfile("Medium", 45f, 45f, 0f, 0f, true, CommsKind.Window, DoorMode.None, false, 0);

        /// <summary>Mirrors the original Polus: lab meltdown 60, lights, simple comms, command doors.</summary>
        public static readonly MapProfile Large =
            new MapProfile("Large", 0f, 0f, 60f, 0f, true, CommsKind.Simple, DoorMode.Command, false, 5);

        /// <summary>Mirrors the original Airship: window meltdown 90, lights, simple comms, command doors.</summary>
        public static readonly MapProfile Huge =
            new MapProfile("Huge", 0f, 0f, 0f, 90f, true, CommsKind.Simple, DoorMode.Command, false, 6);

        /// <summary>Mirrors the original Fungle: reactor 60, window comms, command doors, no lights, appearance scramble.</summary>
        public static readonly MapProfile Jungle =
            new MapProfile("Jungle", 60f, 0f, 0f, 0f, false, CommsKind.Window, DoorMode.Command, true, 5);

        public static readonly MapProfile[] All = { Small, Medium, Large, Huge, Jungle };
    }
}
