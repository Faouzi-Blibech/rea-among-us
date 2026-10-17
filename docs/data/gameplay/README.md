# Gameplay tuning data

Serialized field values (ScriptableObjects and prefab MonoBehaviours) from the
Android build (Unity 2022.3.62f3, IL2CPP). Numbers and names only: no sprites,
audio, meshes or text dumps. Method bodies are not in this data; anything the
game computes in code is listed under "Not found".

## Regenerate

Inputs: `assets/bin/Data` from the base APK and from `UnityDataAssetPack.apk`
(both extracted; `*.splitN` files are concatenated in order by the script), the
`initialmaps_*` and `referencedatagroup_*` Addressables bundles, `libil2cpp.so`
and `global-metadata.dat`, and optionally the decompiled class stubs (`--cs-dir`,
used only for enum names and class hierarchies).

```
uv run --with UnityPy --with TypeTreeGeneratorAPI python -I tools/extract_gameplay_data.py \
  --data-dir <base apk>/assets/bin/Data --data-dir <asset pack>/assets/bin/Data \
  --bundle <aa>/initialmaps_assets_all.bundle \
  --bundle <aa>/referencedatagroup_assets_all_<hash>.bundle \
  --il2cpp <bin>/libil2cpp.so --metadata <bin>/global-metadata.dat \
  --cs-dir <cs>/DiffableCs/Assembly-CSharp --out-dir docs/data/gameplay
```

About 40 s. The scene and shared-asset files have stripped typetrees, so field
layouts are generated from the IL2CPP binary + metadata with UnityPy's
TypeTreeGenerator (this worked directly; no Cpp2IL step needed). The map bundle
and reference bundle carry their own typetrees.

## Files

Every entry is `{name, class, source, fields}` (`source` is the serialized file;
`root` is the prefab root for map-bundle objects). Floats are rounded to 4
decimals, enums are `{v, name}`, vectors/colors are arrays, references to other
settings/roles/tasks are `{ref: "Class/Name"}`, settings and ability buttons
referenced by a role are inlined. Same prefab can appear in several sources.

- `game_settings.json`: all `*GameSetting` ScriptableObjects (55): title,
  OptionName id, Value, Increment, ValidRange, suffix, plus role option ranges.
- `roles.json`: 15 `RoleBehaviour` prefabs (Role id, TeamType, CanVent,
  CanUseKillButton, CanBeKilled, MaxCount, NameColor, string ids, ability and
  option settings inlined) and the `RoleManager` role list.
- `ability_buttons.json`: AbilityButtonSettings / UseButtonSettings (type, string ids).
- `player_tuning.json`: PlayerControl, PlayerPhysics, DeadBody, LightSource, etc.
- `meeting_and_flow.json`: MeetingHud, ExileControllers, GameStartManager,
  HideAndSeekManager, NormalGameManager, EmergencyMinigame, VoteSpreader, etc.
- `tasks.json`: 160 task components from the maps bundle (class, TaskType, StartAt,
  EndAt, Length, MaxStep, ShowTaskTimer, minigame class, prefab root).
- `ship_tuning.json`: per-map ShipStatus scalars and task lists (Common/Long/Short/
  Special task names), plus system components found on map prefabs (DeconSystem,
  HeliSabotageSystem, MushroomMixupSabotageSystem, reactor rooms, ...).
- `quickchat.json`: 234 phrase assets (key id, order, tags, map/phase rules; no
  phrase text except 5 short samples) and the tag ScriptableObject that was readable.
- `cosmetics_counts.json`: counts of cosmetic ScriptableObjects in the reference bundle.
- `code_constants.json`: literal `const` values scraped from the class stubs
  (bit masks, sync rates, a few durations). Not serialized data.
- `summary.json`: counts, unreadable objects by class, MonoBehaviour class histogram.

## Key values found

- Player: `Speed` 2.5, `GhostSpeed` 3.0, `MaxReportDistance` 3.0. Option "player
  speed" is a multiplier: default 1.0, range 0.5 to 3.0 step 0.25.
- Option ranges and ScriptableObject values: kill cooldown 15 (range 10 to 60, step
  2.5), crew light 1.0, impostor light 1.5 (0.25 to 5), impostors 1 (1 to 3), meetings 1
  (0 to 9), common/short/long tasks 1/2/1 (max 2/5/3), discussion time step 15 (0 to 120),
  voting time step 15 (0 to 300), emergency cooldown step 5 (0 to 60), capacity 4 to 15.
  Caveat: some `Value` fields look like editor placeholders (voting time 1, emergency
  cooldown 1); treat `Value` as authoritative only where it matches the range.
- Kill distance is a string setting with three choices (Short, Medium, Long).
  The actual distances are not serialized.
- Role options (default, range): Engineer cooldown 30 (5-60), in-vent 15 (0-60);
  Scientist cooldown 15, battery 15; Shapeshifter cooldown 30, duration 0 (0-30);
  Guardian Angel cooldown 60, duration 10; Tracker cooldown 30, duration 15, delay 1;
  Noisemaker alert 10; Phantom cooldown 15, duration 30; Viper dissolve 15; Spirit
  Guide cooldown 20; Detective suspect limit 3; Judge task requirement 50 percent.
  Roles: Crewmate 0, Impostor 1, Scientist 2, Engineer 3, GuardianAngel 4,
  Shapeshifter 5, CrewmateGhost 6, ImpostorGhost 7, Noisemaker 8, Phantom 9,
  Tracker 10, Detective 12, Viper 18, Judge 19, SpiritGuide 21.
- Hide and Seek options: crew light 0.6, impostor light 0.4, seeker final speed 1.2,
  escape time 200, final escape time 50, max vent time 3, max vent uses 1.
- Ship (MapScale, SpawnRadius): Skeld 4.4/1.6, Mira 5.486/1.55, Polus 4.975/1.0,
  Airship 8.1125/1.55, Fungle 6.0/1.5. Light radius min 1.0, max 5.0 on all maps.
- Exile cutscene duration: Skeld 5, Mira 4, Polus 4, Airship 5, Fungle 3 seconds.
  Vote results time 5 (constant). Decon: DeconTime 3, DoorOpenTime 3 (Mira, Polus).
- Tasks: 82 distinct (class, TaskType) pairs. `MaxStep` is the step count (for
  example Clear Asteroids 20, Start Reactor 5, Reset Breakers 7, Fix Wiring 3, Sort
  Records 4, Clean O2 Filter 6). `Length` on the prefab is a Common/Short/Long tag
  only for some tasks; the per-map Common/Short/Long assignment is the lists in
  `ship_tuning.json`.

## Not found

- Sabotage timings (reactor and O2 countdowns, door sabotage cooldown), kill distance
  values, default game option presets (`NormalGameOptionsV0x`, `HideAndSeekGameOptions`),
  emergency meeting and kill cooldown logic: these are set in code, not serialized.
  Only the placeholders and ranges above are in data.
- 16 `QuickChatTag` objects and 165 `HardwareJoystickMap`/`ControllerButtonBehavior`
  objects could not be parsed with generated typetrees (see `summary.json`); phrase
  `tags` are therefore mostly empty and only one tag object is listed.
- Cross-bundle references to other Addressables bundles resolve to nothing.
- Other cosmetic bundles (initialcosmetics, event bundles) were not scanned; counts
  cover the reference bundle only.
