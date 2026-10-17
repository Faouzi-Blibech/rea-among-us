# Map layout data

Gameplay-relevant object names, component class names, ids, links and 2D world
positions for each map, extracted from the Android Addressables map bundle
(Unity 2022.3.62f3, `initialmaps_assets_all.bundle`). Only names, class names
and numbers are stored. No sprites, textures, audio or meshes are in the repo.

## Regenerate

```
uv run --with UnityPy python -I tools/extract_map_layout.py \
  <path>/initialmaps_assets_all.bundle docs/data/maps \
  --cs-dir <path>/DiffableCs/Assembly-CSharp
```

`--cs-dir` is optional; it is only read for `SystemTypes.cs`, `TaskTypes.cs`
and `ImageNames.cs` to turn enum integers into names. Without it enum fields
are plain integers. The script only reads the bundle; it never executes
anything from it. The run used for these files printed one counts line per map
and exited 0.

## Files

`skeld.json, mira.json, polus.json, airship.json, fungle.json, dleks.json,
lobby.json` plus `summary.json` (counts and the full component-class histogram
per map prefab).

`dleks` is the `AprilShip` prefab: the Skeld layout mirrored on X (same
objects, x negated; confirmed by comparing vent positions). No separate
dropship prefab exists in this bundle.

## Counts

| map | rooms | vents | consoles | consoles_other | doors | cameras | transport |
|---|---|---|---|---|---|---|---|
| skeld | 21 | 14 | 49 | 15 | 13 | 4 | 0 |
| mira | 18 | 11 | 42 | 16 | 4 | 0 | 0 |
| polus | 14 | 12 | 54 | 22 | 28 | 6 | 0 |
| airship | 21 | 12 | 100 | 14 | 54 | 6 | 9 |
| fungle | 15 | 10 | 66 | 0 | 16 | 0 | 11 |
| dleks | 21 | 14 | 49 | 15 | 13 | 4 | 0 |
| lobby | 1 | 0 | 2 | 0 | 0 | 0 | 0 |

Rooms include hallway/outdoor areas that have a room component. `consoles` are
task/system consoles (`Console`, `SystemConsole`, `OptionsConsole`, ...);
`consoles_other` are `MapConsole`, `VentCleaningConsole`, `DeconControl`;
`doors` include `DoorConsole`/`OpenDoorConsole` (door switch panels) as well as
door objects; `transport` is ladders, ziplines, moving platforms and their
consoles. Cameras are `SurvCamera`; Fungle's cameras are not separate
`SurvCamera` objects in this bundle (none found).

## Schema (each map file)

```
map, prefab, units ("unity_world_units"), root_world_pos [x,y],
meta   : ship_status_class, MapScale, Max/MinLightRadius, EmergencyCooldown,
         SpawnRadius, Type, emergency_button {name,pos,path},
         InitialSpawnCenter / MeetingSpawnCenter / MeetingSpawnCenter2
         {local, world}, Common/Long/Short/SpecialTasks (task prefab names)
rooms, vents, consoles, consoles_other, doors, cameras, transport : [ entry ]
entry  : name, class, pos [x,y], path (hierarchy path), fields {...}
rooms  : + area {collider, paths [[x,y]...] (world polygon), bbox [x0,y0,x1,y1]}
vents  : + id, links {left, right, center}  (target vent ids)
```

`fields` holds the serialized component fields that are non-default: numbers
rounded to 3 decimals, enums as `{v, name}`, object references as
`{ref: <GameObject name>}`, vectors as arrays. Zero/false/empty/null values are
omitted, so a missing `ConsoleId` or `Id` means 0. Lists are sorted by path.

## Coordinates

Unity world units (use
`MapScale` for minimap scaling). X right, Y up. World position composes the
Transform hierarchy (position, scale, Z rotation; 3D rotation and Z depth are
ignored). Prefab roots sit at (0,0). Spawn centers in `meta` are given both as
the raw ShipStatus local value and transformed to world.

## Recovered vs not recovered

Recovered: the bundle's MonoBehaviour typetrees are present, so no Cpp2IL step
was needed. Vent ids and Left/Right/Center links, console `Room`, `ConsoleId`
and `TaskTypes` (with names), room ids and polygon areas, door rooms, ladder
`Destination`/`IsTop`, spawn centers, emergency button position, task lists.

Not recovered or not exported: sprites/art/audio (deliberately), minigame
internals, vent link targets other than by id, Z depth, per-task data that
lives on separate task prefabs (only their names appear in `meta`), moving
platform/zipline endpoint vectors beyond the raw fields, and Fungle cameras.
Vent group semantics (left/right/center) are as serialized; confirm in play.
