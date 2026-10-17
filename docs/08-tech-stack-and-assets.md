# 08 - Tech Stack, Packaging and Asset Inventory (Among Us mobile 2026.9.29)

Companion to `07-client-architecture.md`. Same conventions: **[O]** Observed, **[I]** Inferred; source shorthand:

| Tag | Meaning |
|---|---|
| `XAPK:` | `S\apk\x\manifest.json` (XAPK descriptor) |
| `MAN:` | decoded `S\manifest.xml` (base APK) |
| `APK:` | `unzip -l` of `com.innersloth.spacemafia.apk` (base) |
| `CFG:` | `unzip -l` of `config.arm64_v8a.apk` |
| `PACK:` | `unzip -l` of `UnityDataAssetPack.apk` (saved at `S\assetpack.txt`) |
| `CAT:` | `S\assets\catalog.json` (Addressables main catalog; keys decoded to `S\keys.json`) |
| `DL:` | `S\cs\DiffableCs\<Assembly>\` (assembly folder names and types) |
| `D:` | `S\cs\DiffableCs\Assembly-CSharp\` |
| `GGM:` | strings of `S\bin\globalgamemanagers` |

`S` = `C:\Users\faouz\AppData\Local\Temp\claude\C--Users-faouz-Documents-Projects-rea-among-us\faecfc7d-cde7-4c0c-be4d-7b34436891b7\scratchpad`. All sizes are bytes as listed in the zip index, rounded; "MB" = 10^6 bytes.

---

## 1. Tech stack summary

| Layer | Choice | Evidence |
|---|---|---|
| Engine | **Unity 2022.3.62f3** (2022.3 LTS) | `GGM` strings show `2022.3.62f3` [O] |
| Scripting backend | **IL2CPP** (C# compiled to native), arm64-v8a only | `lib/arm64-v8a/libil2cpp.so` 80.7 MB in `CFG`; `assets/bin/Data/Managed/Metadata/global-metadata.dat` 15.9 MB in `APK`; no `armeabi-v7a`/`x86` libs in the XAPK [O] |
| Rendering | Built-in render pipeline-style 2D sprite rendering, OpenGL ES 3.0 required, Vulkan optional | `MAN`: `uses-feature glEsVersion=0x00030000`, `android.hardware.vulkan.version required=false` [O]. No URP/HDRP assembly in the dump (no `Unity.RenderPipelines.*` folders) [O] |
| App activity | `com.innersloth.spacemafia.EosUnityPlayerActivity` (custom UnityPlayerActivity subclass for EOS), fixed landscape, non-resizeable | `MAN` [O] |
| Networking | **Hazel** (Innersloth's UDP reliability library) + HTTP(S) to a custom REST backend; local LAN via UDP broadcast | `DL:Hazel`; `Constants.GamePlayPort=22023`, `AnnouncementPort=22024`; `UnityWebRequest` usage; `Amongus/HTTP/RetryableWebRequest.cs` [O] (protocol: other docs) |
| Physics | Unity **Physics2D** (colliders, raycasts for vision / use targets), a few Rigidbody2D | `UnityEngine.Physics2DModule` in `DL`; `Collider2D` appears in 110 game-code files, `Rigidbody2D` in 7 [O] |
| UI | Custom `PassiveButton` / `UiElement` system on world-space sprites + **TextMeshPro**; `UnityEngine.UI` assembly also shipped | `DL:Unity.TextMeshPro`, `D:PassiveButtonManager.cs`, `D:HudManager.cs` [O] |
| Input | **Rewired** (Core + Android) | `DL:Rewired_Core` (99 types), `DL:Rewired_Android` [O] |
| Content | **Unity Addressables** + custom **Play Asset Delivery** provider + **Unity Cloud Content Delivery** remote catalog | section 5 [O] |
| Identity / cloud | **Epic Online Services** (auth, device-ID guest, KWS parental consent, inventory, player data storage, stats, achievements) | `libEOSSDK.so` 25 MB in `CFG`; `D:EOSManager.cs` [O] |
| Monetization | Google Play Billing 8.0.0 via **Unity IAP**; rewarded ads via **LevelPlay (ironSource)** + **Unity Ads** | section 3 [O] |
| Crash / telemetry | **Sentry** (Unity SDK + native NDK), auto-init off (initialized from code) | `libsentry.so`, `libsentry-android.so`; `MAN` `io.sentry.auto-init=false` [O] |
| Social | Discord Game SDK integration (rich presence, join requests), Twitch deep link | `D:DiscordManager.cs`, `D:TwitchManager`, `MAN` `<queries>` `tv.twitch.android.app` [O] |

---

## 2. Third-party libraries (managed side, from dump assembly names)

| Assembly (`DL:`) | Purpose in this game | Tag |
|---|---|---|
| `Hazel` (2 files: `Hazel/` namespace) | UDP reliable/unreliable transport and message reader/writer used by all game traffic (`MessageReader/MessageWriter` appear in `QuickChatNetData`, `LobbyBehaviour`) | O |
| `Rewired_Core`, `Rewired_Android` | Input abstraction: keyboard/gamepad/touch glyphs and remapping UI (`Rewired.UI.ControlMapper`) | O |
| `Sentry`, `Sentry.Unity`, `Sentry.Unity.Android`, `Sentry.Unity.Native`, `io.sentry.unity.runtime` + ~9 `Sentry.System.*` polyfills | Error reporting; options are a ScriptableObject `ScriptableSentryUnityOptions` stored as an Addressable (CAT type list) | O |
| `Newtonsoft.Json` | JSON for REST payloads and save files (`TrySaveToJsonFile`) | O assembly; I usage |
| `CsvHelper` | CSV parsing; the shipped `Among Us Credits.csv` is the likely consumer (`CreditsController`) | O assembly; I usage |
| `QRCoder` (5 files) | QR code generation; no call site found in `Assembly-CSharp` by name search. Possibly for sharing room codes/linking, or pulled in by another plugin | O assembly; usage unknown |
| `Unity.Addressables`, `Unity.ResourceManager`, `AddressablesPlayAssetDelivery` | Asset catalog/bundle loading, Google Play asset-pack aware bundle provider | O |
| `Unity.Services.Core` (+ 8 sub-assemblies: Configuration, Device, Environments.Internal, Internal, Registration, Scheduler, Telemetry, Threading) | Unity Gaming Services core; required by CCD / LevelPlay | O |
| `Unity.LevelPlay` (5 files) | ironSource LevelPlay mediation wrapper (rewarded/banner/interstitial classes; only rewarded used in `AdsManager`) | O |
| `UnityEngine.Purchasing` (+ `Common`, `Security`, `SecurityCore`, `Stores`, `AppleCore/Stub`, `WinRT*`) | Unity IAP; Google Play store module | O |
| `Unity.TextMeshPro` | All text; fonts in `Fonts & Materials` (74 catalog keys) | O |
| `Unity.ProBuilder` | Present in dump; no call site in game code found by name search (may be an editor leftover or used for level geometry meshes) | O assembly; usage unknown |
| `Unity.MemoryProfiler` | Debug profiling hook | O |
| `UnityEngine.*Module` assemblies | Physics2D, Physics, Tilemap, Grid, SpriteShape, SpriteMask, ParticleSystem, Animation, Audio, AndroidJNI, ScreenCapture, ImageConversion, IMGUI, InputLegacy, JSONSerialize, TextCore, GameCenter | O present; Tilemap/SpriteShape/Grid **use not confirmed** (no game-code references by name; they may just be the default module set) I |
| Platform SDK stubs in `Assembly-CSharp` | `GameCore/*` (Xbox GDK), `PSManager`, `SteamManager`, `SwitchManager`, `Win10Manager`; `GooglePlayGames/*` source folder | O |
| Beebyte obfuscator | `BuildSymbols.DisableObfuscator = "BEEBYTE_OBFUSCATOR_DISABLE"` and `[Obfuscation(Exclude=true)]` attributes (e.g. on `StringNames`) | O (so type names can still be partly scrambled in other builds) |
| Plugin vendors | PlayEveryWare EOS Unity plugin (scene paths `Assets/PlayEveryWare/*`) | O |

Mono class libraries shipped too (`mscorlib`, `System*`, `Mono.Security`) - normal for IL2CPP [O].

---

## 3. Android layer (from the manifest and dex)

### 3.1 SDK integrations

| Integration | Evidence | Role |
|---|---|---|
| Unity player | `classes*.dex`: `com/unity3d/player` (about 137 string hits) [O] | Engine host |
| EOS Android | `EOSAuthHandlerActivity` (`com.epicgames.mobile.eossdk`), `libEOSSDK.so` | Epic login redirect, native SDK |
| Google Play Games Services | `com.google.android.gms.games.APP_ID` meta-data, `unityVersion 0.10.14`, `NativeBridgeActivity`, `SignInHubActivity`, `GenericResolutionActivity` | Platform sign-in (links to EOS), achievements API available |
| Google Play Billing | `com.google.android.play.billingclient.version = 8.0.0`, `ProxyBillingActivity(V2)`, query intent `InAppBillingService.BIND` | In-app purchases (stars, ad removal) |
| Play Asset Delivery | `AssetPackExtractionService` (disabled by default), `ExtractionForegroundService`, `PlayCoreDialogWrapperActivity`, asset pack `UnityDataAssetPack` | Large content shipped as install-time asset pack |
| LevelPlay / ironSource | Activities `com.ironsource.sdk.controller.ControllerActivity`, `InterstitialActivity`, `OpenUrlActivity`, `TestSuiteActivity`; lifecycle providers; `assets/ad-viewer/omid-session-client-v1.js`, `omsdk-v1.js` (IAB Open Measurement) | Ad mediation |
| Unity Ads | `com.unity3d.services.ads.adunit.AdUnit*Activity` (4 variants), `AdsSdkInitializer` (androidx.startup), `FullScreenWebViewDisplay` | Ad network inside mediation |
| Sentry Android + NDK | `SentryInitProvider`, `SentryPerformanceProvider`, `libsentry*.so`; auto-init disabled | Crash reporting (managed + native) |
| AndroidX | `WorkManager` (`SystemJobService`, `SystemAlarmService`, `SystemForegroundService`, constraint proxies), `Room` (`MultiInstanceInvalidationService`), `startup`, `emoji2`, `profileinstaller` | Background work for SDKs (ads/Play), emoji rendering, baseline profile install |
| Firebase-style transport | `com.google.android.datatransport` (CCT backend, job scheduler) | Telemetry transport used by Google SDKs |
| OkHttp / Kotlin | `okhttp3/internal/publicsuffix/publicsuffixes.gz`, `kotlin.kotlin_builtins`, `kotlinx.coroutines` | Dependencies of the above SDKs |
| Deep links | Intent filters: scheme `amongus://`, `https://innersloth.com/*`, Discord SDK action | Join-by-link, Discord invite |
| Locale breadth | `XAPK` `locales_name` lists ~80 store locales (all with name "Among Us") | Store listing only; in-game languages are 16 (doc 07, section 4) |

(All rows [O]; "Role" column is partly [I].)

### 3.2 Permissions (declared in `MAN` / `XAPK`)

| Permission | Likely reason | Tag |
|---|---|---|
| `INTERNET`, `ACCESS_NETWORK_STATE` | Online play, REST, CDN, ad SDKs | O decl, I reason |
| `CHANGE_WIFI_MULTICAST_STATE` | LAN game discovery over UDP broadcast/multicast (Local mode, port 22024) | I |
| `com.android.vending.BILLING` | IAP | O |
| `com.google.android.gms.permission.AD_ID` | Advertising ID for ad SDKs (user can opt out / child mode via `InitLevelPlay(isChild)`) | O decl, I reason |
| `ACCESS_ADSERVICES_TOPICS`, `ACCESS_ADSERVICES_ATTRIBUTION` | Privacy Sandbox / ad attribution APIs, pulled by ad SDKs | I |
| `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC` | WorkManager foreground jobs / asset pack extraction foreground service (`foregroundServiceType=dataSync`) | I |
| `WAKE_LOCK`, `RECEIVE_BOOT_COMPLETED` | WorkManager rescheduling and SDK background work | I |
| `com.innersloth.spacemafia.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION` | AndroidX signature permission for non-exported dynamic receivers | I |
| (none for camera, microphone, location, storage, contacts) | No voice chat, no photos from device | O (absence in `MAN`) |

Manifest flags worth copying/understanding: `allowBackup=false`, `hardwareAccelerated=false` on application and activity (but `true` on ad activities) - the game's custom 2D rendering does not need Android's View-layer acceleration; Unity renders to its own surface [I]. `screenOrientation=11` (decoded value; Android constant 11 = user landscape) and `resizeableActivity=false` [O value, I interpretation]. `minSdk 26`, `targetSdk 36`, `compileSdk 36` [O: `XAPK`, `MAN`].

---

## 4. Build and package layout

| File (in XAPK) | Size | Contents | Source |
|---|---|---|---|
| `com.innersloth.spacemafia.apk` (base) | 14.3 MB (zip total 41.9 MB uncompressed entries) | Manifest, `classes.dex` 8.5 MB + `classes2.dex` 9.2 MB + `classes3.dex` 2.2 MB (Java/Kotlin SDKs), `resources.arsc`, `global-metadata.dat` 15.9 MB (IL2CPP metadata - the type/string table), ad-viewer JS, Unity managed resources `.dat` | `APK` [O] |
| `config.arm64_v8a.apk` (split by ABI) | 41.3 MB (134.8 MB uncompressed) | `libil2cpp.so` 80.7 MB (all game + library code), `libEOSSDK.so` 25.0 MB, `libunity.so` 19.1 MB, `libc++_shared.so` 9.2 MB, `libsentry.so` 0.74 MB, `libmain.so`, `lib_burst_generated.so` 5 KB (Burst present but essentially unused) | `CFG` [O] |
| `UnityDataAssetPack.apk` (Play Asset Delivery pack) | 1,060.7 MB | Everything in `assets/bin/Data` (Unity scenes `level0..level9`, `sharedassets*`, `globalgamemanagers`-like data, `.resource` streams) and `assets/aa/` (Addressables: 29 bundles + `catalog.json` 1.1 MB + `settings.json` + `link.xml`) | `PACK`, `XAPK` [O] |
| **Total download (XAPK `total_size`)** | **1.116 GB** | | `XAPK` [O] |

Observations:

- Code vs data: code is ~81 MB of native (`libil2cpp.so`) + 16 MB metadata + 20 MB dex; **data is 95% of the install** [O arithmetic from sizes].
- Unity player data (scenes, built-in assets) and Addressables are both in the **asset pack**, not the base APK: `PACK` shows 444 files under `assets/bin/Data/` (383 MB) and 29 bundles (676 MB) [O]. So the base APK is a thin launcher; the game cannot start without the asset pack. Install-time vs fast-follow delivery mode is not visible in the XAPK [unknown].
- `SceneChanger.disallowBasedOnAssetPackDownloads` suggests there is a fallback for on-demand packs [I] (see doc 07, 2.1).
- Large uncompressed streams: `sharedassets2.resource` 28.5 MB (probably audio/texture stream data), `sharedassets4.resource` 5.3 MB, `sharedassets9.resource` 1.9 MB (+ `sharedassets9.assets` split into 16 x 1 MiB parts) [O sizes, I contents]. Files are split in 1 MiB `.splitN` chunks, a Unity Android packaging convention for APK size limits [I].
- Environment flavors by compile symbols: `STEAMWORKS, EPICGAMESSTORE, ITCHIO, ITCHIO_ANDROID, MICROSOFT_GAME_CORE, CONSOLE, CONTROLLER, QA_BUILD, STAGING, EXTERNAL_PLAYTEST, DEBUG_TOOL` and CDN platform names `Steam, EGS, Itch, Win10, ItchAndroid, GPAndroid, iOS, PS4, PS5, Switch, XB1, XSX` (the Google Play build is `GPAndroid`) [O: D:Constants.cs]. The catalog is built per platform: path `assets/aa/GooglePlay/Android/` [O].

---

## 5. Addressables setup

- Catalog: `m_LocatorId = "AddressablesMainContentCatalog"`; providers include `PlayAssetDeliveryAssetBundleProvider` [O: CAT].
- 7,778 keys: 3,736 are `Assets/...` addresses, 29 are bundle file names (`<name>_assets_all_<hash>.bundle`), the rest are GUID-style or class-name keys. Resource types in catalog: `HatData, SkinData, VisorData, PetData, NamePlateData, BundleData, StarBundle, CosmeticGroups, CosmeticReleaseGroup, CosmeticVariantGroup, ReferenceData, TranslatedImageSet, HatViewData, SkinViewData, VisorViewData, NamePlateViewData, PreviewViewData, GameObject (prefabs), TextAsset, Texture2D, Sprite, Material, TMP_FontAsset, TMP_SpriteAsset, TMP_StyleSheet, TMP_Settings, AnimationClip, RuntimeAnimatorController, GlyphControllerMapCollection, GlyphCollection, LevelPlayMediationSettings, ScriptableSentryUnityOptions, DeveloperSettings, SceneInstance` [O: CAT `m_resourceTypes`]. Notably: **game config objects themselves are Addressable assets** (Sentry options, LevelPlay settings, developer settings), so they can be re-pointed per content release [I].
- Remote content: `assets/aa/settings.json` lists the catalog hash location on Unity CCD (`client_api/v1/buckets/<bucket>/release_by_badge/2026_9_29-2026_8_17_1125/...catalog_19.hash`) and a local cache path `{persistentDataPath}/com.unity.addressables/catalog_19.hash` [O: PACK]. In a normal Addressables setup the client compares the remote hash with the cached one and, if changed, downloads the new catalog; **new catalog entries can add content without a store update** [I].
- Cache wrapper: `AmongUsCaching.AmongUsCDNCache` (custom bundle download + catalog write/delete) [O: DL].
- Key-path convention (useful to emulate): `Assets/CDN/ReferenceData/<Type>/<id>.asset` for metadata; `Assets/Cosmetics/<Kind>/<Group>/<name>View.asset` for heavy assets; `Assets/Cosmicube/Cosmicubes/<Cube>/...` per cube [O].

---

## 6. Asset inventory

### 6.1 Bundle sizes (the 29 Addressables bundles, `PACK`)

| Bundle (file stem) | Size | What it holds (from `CAT` dependency edges and key paths) | Tag |
|---|---|---|---|
| `initialcosmetics_assets_all` | 265.8 MB | Base cosmetics (about 242 hat view-data, 42 skins, ~790 preview data refs) and the older cosmicubes (map/season cubes) | O (edges), I (grouping) |
| `initialmaps_assets_all` | 129.0 MB | Map prefabs: Skeld, April/Dleks, Mira HQ, Polus, Airship, Fungle (Skeld/Ship, HeadQuarters, PlanetMap, Airship, Map5/Fungle) and the `MatchMaking` prefab | O |
| `kamurocho_assets_all` | 40.0 MB | "16.0 Kamurocho" collab cube + its previews | O key folder |
| `pawsnclaws_assets_all` | 32.7 MB | "15.7 Paws" and "15.7 Claws" cubes | O |
| `hanami_assets_all` | 23.5 MB | "17.2 Hanami" cube | O |
| `cosmicubelny24_assets_all` | 21.4 MB | Lunar New Year 2024 free/paid cubes | O |
| `cosmica_assets_all` | 21.2 MB | "2026 Cosmic" cube part A | O |
| `racingcubeb_assets_all` | 18.3 MB | "17.0 Racing" cube B | O |
| `stardewb_assets_all` | 18.8 MB | "17.1 Stardew" cube B | O |
| `critrolecubeb_assets_all` | 19.1 MB | Critical Role cube B | O |
| `projectparasitecubeb_assets_all` | 18.6 MB | Project Parasite cube B | O |
| `critrolecubea_assets_all` | 15.1 MB | Critical Role cube A | O |
| `projectparasitecubea_assets_all` | 12.7 MB | Project Parasite cube A | O |
| `racingcubea_assets_all` | 10.9 MB | Racing cube A | O |
| `stardewa_assets_all` | 9.0 MB | Stardew cube A | O |
| `2025beanbuffer2_assets_all` | 7.9 MB | 2025 bean-store drop (hats, visors, skins, nameplates) | O |
| `2024beanbundle1_assets_all` | 3.3 MB | 2024 bean-store drop | O |
| `genshin_assets_all` | 2.5 MB | Collab hat + skin + nameplate (Paimon) | O |
| `referencedatagroup_assets_all_<hash>` | 2.3 MB | `ReferenceData` + all `HatData/SkinData/...` metadata + languages (16 TSV, censor lists, credits) + star bundles | O |
| `phoenixwright_assets_all` | 1.8 MB | Collab hat + skin | O |
| `edgeworth_assets_all` | 1.3 MB | Collab hat + skin | O |
| `0e13d00f..._unitybuiltinshaders` | 0.13 MB | Unity built-in shaders pack | O |
| `cosmicb_assets_all` | 0.25 MB | cosmic cube B (previews) | O |
| `2024_birthday_beans_assets_all` | 0.24 MB | Birthday 2024 bean items | O |
| `nye2025`, `nye2026`, `bday2026`, `anniversaryslothmask`, `impostormonth_2026` | 9 KB - 42 KB each | Single-item event cosmetics | O |
| **Total of the 29 bundles** | **676.1 MB** | | `PACK` sum [O] |
| Unity player data (`assets/bin/Data/*`, 444 files) | 383.3 MB | Scenes, shared assets, resource streams | `PACK` sum [O] |

Pattern: large collab cubes are split into "a" and "b" bundles (~2 files each), so Addressables can stream a collab in two halves; tiny event drops get their own bundle so they can be added with minimal download [I].

### 6.2 Cosmetic counts (unique catalog keys by folder)

| Folder | Keys |
|---|---|
| `Assets/Cosmetics/Previews/PreviewData` | 1,033 |
| `Assets/CDN/ReferenceData/Hats` | 584 |
| `Assets/CDN/ReferenceData/Skins` | 216 |
| `Assets/CDN/ReferenceData/Visors` | 185 |
| `Assets/CDN/ReferenceData/NamePlates` | 166 |
| `Assets/CDN/ReferenceData/Groups` | 144 |
| `Assets/CDN/ReferenceData/Pets` | 53 |
| `Assets/CDN/ReferenceData/Languages` | 46 |
| `Assets/CDN/ReferenceData/Bundles` / `StarBundles` | 11 / 5 |
| `Assets/Cosmetics/Hats` (view data) | 272 |
| `Assets/Cosmetics/Skins` / `NamePlates` / `Visors` / `Pets` | 51 / 42 / 39 / 17 |
| `Assets/Cosmicube/Cosmicubes/*` | 861 items in 28 named cubes + 1 empty prefab |

(Keys counted by us from decoded `m_KeyDataString`; counts are keys, not necessarily unique items - the same cosmetic can be addressed by data asset, view asset and preview asset.) [O]

### 6.3 What kind of content is it

- **2D sprites everywhere**: cosmetics are sprite sets (`HatViewData` etc.), pets are prefabs with sprite animators (`PetBehaviour`, `Animator` used in 14 game-code files), maps are prefabs of sprite renderers + `Collider2D`s + `SpriteMask`s (6 files) [O types; I composition].
- Vision/fog: HUD `ShadowQuad : MeshRenderer` plus `Constants.ShadowMask` layer mask, i.e. visibility is a mesh built per frame and rendered as a mask over the world [O fields; I technique].
- Player color: custom shader `Unlit/PlayerShader` (name constant `PlayerShader`), `Sprites/Default` as the generic sprite shader, materials `PlayerMaterial / MaskedPlayerMaterial / MaskedMaterial` in `HatManager` [O]. A dedicated 0.13 MB built-in shader bundle ships with Addressables [O].
- Animation: `Constants.AnimationFrameRate = 24` (hand-drawn frame animation at 24 fps) plus `AnimationClip` / `RuntimeAnimatorController` as Addressable types [O].
- Particles: `ParticleSystem` referenced in 9 files (weather, snow, sabotage effects; `SnowManager`, `WeatherControl`) [O].
- Audio: Unity `AudioMixer` (4 groups), clips referenced by serialized fields (task complete sounds, stingers, map themes); no FMOD/Wwise libs; the audio data lives in `.resource` streams inside the pack (no loose `.ogg/.wav/.mp3` files in the APKs) [O; contents I].
- Fonts: TextMeshPro SDF fonts in the `Fonts & Materials` catalog group (74 keys) incl. sprite assets for icons and per-language fonts [O keys, I per-language].
- Localization data: 16 TSV tables + 14 censor lists (CSV/TSV text; `CsvHelper`/`TextAsset`) [O].

### 6.4 Map content (pointer)

Only the six map prefab roots are visible in the index: `SkeldShip`, `AprilShip`, `MiraShip`, `PolusShip`, `Airship`, `FungleShip` (+ `FungleWaves`, `FungleWavesVariant`). Contents (rooms, task consoles, vents, doors) live inside `initialmaps` and are covered by the maps doc. `LobbyBehaviour.AllRooms` shows the lobby is a Skeld-style ship room set (`SkeldShipRoom[]`) [O].

---

## 7. Implications for a similar (original) game  -  DESIGN PROPOSAL (not observation)

### 7.1 What the evidence says about scale

- A ~15-player real-time social-deduction game is **light on simulation**: the heavy parts are art volume (cosmetics) and live-ops (store, events), not game code. Code plus engine is about 100 MB of a 1.1 GB install [O arithmetic].
- The client is a 2D top-down game with sprite maps, 2D colliders and a mesh-based fog of war. No 3D, no navmesh, no physics beyond overlap/raycast [I].
- Multiplayer is authoritative-host with a thin relay/matchmaking backend (other docs); account services come from a third party (EOS) rather than a bespoke login stack [I].

### 7.2 Engine options

| Option | Fits when | Notes |
|---|---|---|
| **Unity 2022.3 LTS or Unity 6** (same as original) | Team knows C#; wants Android+iOS+desktop from one code base and Addressables-style content delivery | Matches every system above 1:1; IL2CPP arm64 builds; larger base size. Biggest ecosystem for netcode (Netcode for GameObjects, Mirror, Fish-Net, Photon, or Hazel-like custom UDP) |
| **Godot 4** | Want open-source, small binaries, 2D-first tools (TileMap, Light2D with shadows for vision) | Built-in 2D lights/occluders can implement vision cones without a custom mesh; HTTP/ENet multiplayer built in. Mobile export fine; IAP/ads need plugins |
| **Defold / Phaser / Pixi + native shell** | Web-first or very small download | Weakest on store/ads/IAP ecosystem |
| **Custom (e.g. Kotlin/Swift + shared C++ core)** | Not recommended | High cost for UI, input, audio, store |

Recommendation (proposal): **Unity** if the team wants maximum parity and Addressables; **Godot 4** if license/size/simplicity matter. Either way, keep the simulation (rules, roles, tasks) in engine-agnostic code with a deterministic, serializable state so it can be tested without the renderer.

### 7.3 Minimal stack for a first playable clone

| Need | Minimal choice | Why (tied to evidence) |
|---|---|---|
| Engine + 2D | Unity 2D (sprites, Physics2D, TextMeshPro) or Godot 4 | Original is sprite + Collider2D based |
| Input | Engine-native touch + a single virtual joystick, buttons as UI; skip Rewired at first | Rewired is only needed for gamepad/remap; touch path is simple (doc 07, 3.2) |
| Netcode | Authoritative host or small dedicated server over UDP, with reliable + unreliable channels; room codes + simple matchmaking list | Original: Hazel UDP, room codes, host-authoritative [I] |
| Accounts | Anonymous device login at first; add platform sign-in later; if targeting under-13 players you need a COPPA/GDPR-K compliant gate (age screen + restricted chat) | Original embeds age gate + KWS + quick chat [O] |
| Chat | Quick-chat as enum ids first, free chat later behind age + report/ban tools | Doc 07, section 4.2 |
| Localization | Key-value tables per language, enum-keyed strings | Doc 07, section 4.4 |
| Content delivery | Ship base content in the install; add remote manifest + packs only when you have seasonal content | Original relies on CCD + asset packs because of 1 GB of cosmetics |
| Store/economy | One premium currency + one soft currency; server-authoritative inventory | Doc 07, section 6.2 |
| Monetization | Rewarded video only (cap per day) + cosmetic IAP; avoid gameplay-affecting purchases | Original: only rewarded ads, `MAX_ADS_PER_DAY = 2` [O] |
| Telemetry | Sentry (or equivalent) for crash/error | Original uses Sentry [O] |
| Build | arm64-only Android + AAB with asset packs if >150 MB | Original is arm64-only with PAD [O] |

### 7.4 Things to avoid copying

- Do not reproduce game names, art, audio, text strings, cosmetic names or collab IP from the dump; build original assets. This documentation records structure and counts only.
- Do not copy the "one shared binary for 12 platforms" macro-heavy approach unless you plan consoles; it creates the 1,600-type monolith we saw.
- Do not rely on weak local obfuscation (single-byte XOR) for anything that matters; keep entitlements server-side [O pattern, I advice].

---

## 8. Unknowns

- Exact Unity package versions (Addressables, Purchasing, LevelPlay, Rewired, Sentry); assemblies are named but their version attributes were not extracted.
- Whether asset pack delivery type is install-time or fast-follow (only the XAPK result is visible).
- Real purpose of `QRCoder`, `ProBuilder`, `CsvHelper`, Tilemap/SpriteShape modules (present as assemblies; no call sites found by name in the diffable dump).
- Shader inventory beyond the names in `Constants`; render texture usage; target frame rate and quality settings (in `ProjectSettings`-like data inside `globalgamemanagers`, not parsed).
- Audio asset counts and formats (bytes live in `.resource` files, not enumerated).
- Backend: REST routes, EOS deployment ids, CCD bucket policy - intentionally not examined here.

## Related docs

- Client architecture and flows: `docs/07-client-architecture.md`.
- Networking, protocol and server docs (written by other workers): `docs/02-*` to `docs/04-*`; roles `docs/05-roles.md`; tasks and maps: sibling docs in `docs/`.
