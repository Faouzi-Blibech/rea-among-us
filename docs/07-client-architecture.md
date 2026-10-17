# 07 - Client Architecture (Among Us mobile 2026.9.29)

Scope: scenes and flow, HUD and input, chat and localization, accounts and age gating, cosmetics and economy, content delivery, local persistence. Networking, gameplay rules, roles/tasks and maps live in other docs (see "Related docs"). The tech stack, build layout and asset inventory are in `08-tech-stack-and-assets.md`.

## 0. How to read this document

- Every claim carries a source tag. Evidence tag: **[O]** = Observed directly in the evidence, **[I]** = Inferred (reasoned from names/structure; may be wrong).
- Source shorthand (all paths are under the session scratchpad `S`):

| Tag | Meaning |
|---|---|
| `D:` | Cpp2IL diffable C# dump, `S\cs\DiffableCs\Assembly-CSharp\` (types, fields, enums, signatures; **no method bodies**) |
| `DL:` | Other dump assemblies, `S\cs\DiffableCs\<Assembly>\` |
| `MAN:` | Decoded manifest `S\manifest.xml` |
| `XAPK:` | `S\apk\x\manifest.json` |
| `CAT:` | Addressables catalog `S\assets\catalog.json` (keys decoded by us into `S\keys.json`) |
| `PACK:` | `unzip -l S\apk\x\UnityDataAssetPack.apk` (listing in `S\assetpack.txt`) |
| `GGM:` | `S\bin\globalgamemanagers` (Unity player data, strings) |

- Limitation that applies to everything below: the dump contains **no method bodies**. We know what exists, what fields hold, and enum values. We do not know the exact algorithms. Behaviors described as "does X" from a method name are **[I]**.
- Design proposals for our clone are always in sections titled "Clone note" and are not observations.

---

## 1. Client at a glance

| Aspect | Finding | Source |
|---|---|---|
| Engine | Unity 2022.3.62f3 (IL2CPP, arm64-v8a only on this build) | GGM strings `2022.3.62f3` [O]; `libil2cpp.so` + `global-metadata.dat` in XAPK [O] |
| Game code | One big `Assembly-CSharp` (about 1,600 types), namespaces `AmongUs.*` for the newer modules (Data, QuickChat, Matchmaking, GameOptions, GameModes) | `S\type-index.txt` [O] |
| Architecture style | Unity MonoBehaviour scene objects, `DestroyableSingleton<T>` managers, ScriptableObject-like data assets loaded through Addressables, coroutines (compiler-generated `<X>d__N` iterator classes everywhere) | D:ActiveInputManager.cs (`: DestroyableSingleton<ActiveInputManager>`), D:MainMenuManager.cs [O] |
| Scene count | 10 scenes in the player build | `PACK` has `Data/level0..level9` [O]; scene paths in GGM (see section 2) [O] |
| Build symbols | Same code base serves Steam, Epic, Itch, Win10/MS GDK, iOS, Android (Google Play and Itch), PS4/5, Switch, Xbox | D:Constants.cs `BuildSymbols`, `CDNPlatforms`, `Platforms` enum [O] |
| Platform enum | `Unknown, StandaloneEpicPC, StandaloneSteamPC, StandaloneMac, StandaloneWin10, StandaloneItch, IPhone, Android, Switch, Xbox, Playstation` (0..10) | D:Platforms.cs [O] |
| Build environment | `None, Editor, Development, Staging, Production`; this build has `environment = 4` (Production) | D:BuildEnvironment.cs, D:Constants.cs [O] |
| Version | `2026.9.29`, build number 7492, `AddressablesVersion = "2026_8_17_1125"`, broadcast version packed from year/month/day/revision | D:Constants.cs; `XAPK` [O] |

Singletons seen in `type-index.txt` (managers worth mirroring conceptually) [O]: `AmongUsClient` (network, other doc), `DataManager`, `AccountManager`, `EOSManager`, `AuthManager`, `InventoryManager`, `StoreManager`, `CosmicubeManager`, `HatManager`, `ReferenceDataManager`, `ProgressionManager`, `AchievementManager`, `AdsManager`, `SoundManager`, `VibrationManager`, `ControllerManager`, `ActiveInputManager`, `TranslationController`, `ServerManager`, `FriendsListManager`, `DiscordManager`, `TwitchManager`, `PlayerStorageManager`, `GameManager`/`NormalGameManager`/`HideAndSeekManager`, `GameOptionsManager`, `RoleManager`, `GameStartManager`, `EndGameManager`, `SceneChanger`.

---

## 2. Scenes and flow

### 2.1 Scenes in the build

Scene asset paths found as strings in `GGM` [O]:

| Scene file | Constant / role | Source |
|---|---|---|
| `PlayEveryWare/EarlyInitialization.unity` | Early init (PlayEveryWare = Epic EOS Unity plugin vendor); first scene [I: order, name] | GGM; assembly folder `PlayEveryWare` strings in CAT |
| `PlayEveryWare/SplashIntro.unity` | Splash/logo, hosts `SplashManager` [I: mapping] | GGM; D:SplashManager.cs |
| `MainMenu.unity` | `Constants.MAIN_MENU_SCENE = "MainMenu"` | D:Constants.cs [O] |
| `MatchMaking.unity` | `MATCHMAKING_SCENE = "MatchMaking"`; has `MMOnlineManager` (online sub-menu with help menu + back) [I: scene-to-class mapping] | D:Constants.cs, D:MMOnlineManager.cs |
| `FindAGame.unity` | `FIND_GAME_SCENE = "FindAGame"`; `FindAGameManager` | D:Constants.cs, D:FindAGameManager.cs |
| `OnlineGame.unity` | `ONLINE_SCENE = "OnlineGame"`; the in-room scene (lobby and gameplay both happen here) | D:Constants.cs; lobby/game in the same scene is **[I]**: `LobbyBehaviour` and `GameStartManager` are plain components and `GameStartManager.LobbyPrefab : LobbyBehaviour` is a *prefab reference* that is instantiated in-scene |
| `EndGame/EndGame.unity` | `EndGameManager` (win text, stinger sounds, player line-up) | D:EndGameManager.cs [O]; scene path GGM [O] |
| `Tutorial.unity` | `TUTORIAL = "Tutorial"`; `TutorialManager` | D:Constants.cs [O] |
| `HowToPlay.unity` | How-to-play pages incl. movement page with a demo finger/stick | D:HowToPlayScenePage.cs, D:HowToPlaySceneMovementPage*.cs [O] |
| `AnimationTestScene/AnimationTestScene.unity` | Dev/test scene shipped in build | GGM; CAT key `AnimationTestScene` [O] |

Maps (Skeld, Mira HQ, Polus, Dleks/April, Airship, Fungle) are **not** scenes. They are prefabs delivered by Addressables: `Assets/Ship/SkeldShip.prefab`, `Assets/Ship/AprilShip.prefab`, `Assets/HeadQuarters/MiraShip.prefab`, `Assets/PlanetMap/PolusShip.prefab`, `Assets/Airship/Airship.prefab`, `Assets/Map5/FungleShip.prefab` (+ `Assets/Fungle/FungleWaves*.prefab`) [O: CAT keys]. `MapNames` enum: `Skeld=0, MiraHQ=1, Polus=2, Dleks=3, Airship=4, Fungle=5` [O: D:MapNames.cs]. See map doc for contents.

`SceneChanger` (D:SceneChanger.cs) is the single scene-loading component: public `TargetScene` string, `BeginLoadingScene()`, `BeginLoadingSceneAdditive()`, `AllowFinishLoadingScene()` (async load with a "hold activation" step via `AsyncOperation loadOp`), a static `ChangeScene(string)`, a `BeforeSceneChange` UnityEvent, plus two gates: `disallowBasedOnSwitchParentalControls` and `disallowBasedOnAssetPackDownloads` [O]. The asset-pack gate means the client refuses to enter a scene until required Play Asset Delivery content is downloaded [I].

### 2.2 Flow

```
App start
  EarlyInitialization -> SplashIntro (SplashManager)
     - waits minimumSecondsBeforeSceneChange, loads ReferenceData (Addressables) "InitializeRefdata"
     - error popup if ref data fails (SplashErrorPopup)            [D:SplashManager.cs, O]
  -> MainMenu (MainMenuManager)
       login/age/consent flows run here (section 5): EOSManager + AccountManager
       buttons: Play, Online, Local, FreePlay, HowToPlay, Inventory, Shop, Account, News,
                Settings, Credits, Quit                              [D:MainMenuManager.cs fields, O]
       Online -> (MatchMaking scene or in-menu panel) -> Find Game | Create Game | Enter Code
       Local  -> LAN game (UDP broadcast, see 2.3)
       FreePlay -> offline practice game on one of the maps
       Find Game -> FindAGame scene (browse public lobbies with filters)
  -> OnlineGame scene
       connect + join room -> lobby (LobbyBehaviour, GameStartManager: room code, player counter, host rules, countdown)
       host starts -> role reveal (IntroCutscene) -> gameplay (HudManager)
       meeting (MeetingHud) <-> gameplay
       game over -> ExileController (per map variant) -> EndGame scene (EndGameManager) -> back to lobby or MainMenu
```

Supporting evidence:

- `MainMenuManager` serialized buttons: `playButton, PlayOnlineButton, findGameButton, createGameButton, entercodeField, redeemcodeField, playLocalButton, freePlayButton, howToPlayButton, inventoryButton, shopButton, myAccountButton, newsButton, settingsButton, creditsButton, quitButton, accountCTAButton`, plus panels `gameModeButtons, accountButtons, onlineButtons, enterCodeButtons, CreateGameOptions createGameScreen, AdDataCollectScreen AdsPolicy, EjectMainMenu ejectMenu` and sliding transitions (`TIME_SLIDE = 0.3`, offscreen X constants) [O: D:MainMenuManager.cs]. Public API: `OpenGameModeMenu, OpenOnlineMenu, OpenEnterCodeMenu, OpenRedeemCodeMenu, OpenFindGame, OpenCreateGame, OpenAccountMenu, OpenCredits, TransitionToInventory, TransitionToShop, CheckNewItems` [O].
- "Redeem code" is a main-menu feature (`RedeemCosmeticPopup`, `StringNames.RedeemCodeLabel`, `CodeAlreadyRedeemedLabel`) [O: D:RedeemCosmeticPopup.cs, D:StringNames.cs].
- `FindAGameManager`: server dropdown (`ServerDropdown`), refresh button with 1 s refresh time (`RefreshTimeSeconds = 1`), filter popup (`FilterPopUp`, filter names `"Map"`, `"Chat"`, `"Crossplay"`, `"Language"`), `GameSorter`, game containers, "more info" popup, clear-filter popup [O]. Filter model classes under `AmongUs.Matchmaking`: `GameFilter, BoolGameFilter, IntGameFilter, CategorizedGameFilter, MapGameFilter, ChatModeGameFilter, LanguageFilter, PlatformGameFilter, ModFilter, GameFilterSet` plus "Categorized*" option bucketers (kill cooldown, voting time, player speed, vision, etc.) [O: `Amongus/Matchmaking/`]. Filters are persisted per game mode (`classicFilterSet`, `hnsFilterSet`) [O: MultiplayerSettingsData].
- `GameStartManager`: `MinPlayers`, `PlayerCounter`, `GameRoomNameCode` (room code text), `StartButtonClient`, countdown `CountdownDuration = 5.0001` s, host info panel vs client info panel, rules preset text, view/edit rules panels, `ShareOnDiscordButton`, `InviteFriendsButton` [O]. `Constants.MinNumPlayers = 4`, `MaxNumPlayers = 15` [O: D:Constants.cs].
- `LobbyBehaviour`: spawn positions, drop-ship sound/animation, map theme music, **lobby timer extension** RPC (`RpcExtendLobbyTimer`, `HandleLobbyTimerExtensionRequest`, HUD `ShowLobbyTimer`, `LobbyTimerExtensionUI`) [O].
- Game modes: `GameModes { None, Normal, HideNSeek, NormalFools, SeekFools }` (the "Fools" ones are April-Fools variants [I]) [O: `Amongus/GameOptions/GameModes.cs`]. There is a dedicated `HideAndSeekManager` and a one-time "viewed Hide and Seek how-to-play" flag in onboarding data.
- Ports: gameplay `GamePlayPort = 22023`, local announcement `AnnouncementPort = 22024` [O: D:Constants.cs] (details in networking doc).
- Pre-lobby "min player warning" popups and "online help message" are persisted onboarding flags (section 8) [O].

### 2.3 Local and free play

- `playLocalButton` and `freePlayButton` exist on the main menu [O]. `Constants.LocalNetAddress = "127.0.0.1"` and an announcement port suggest the host announces on the LAN and clients listen [I]. The manifest requests `CHANGE_WIFI_MULTICAST_STATE`, consistent with LAN discovery [I].
- `GameStartManager` has a `LocalLabel` object distinct from the online room-code header, so lobby UI branches between online and local modes [O field, I meaning].

### Clone note (proposal)
- Keep the same coarse scene graph: Boot -> MainMenu -> Matchmaking/FindGame -> Room (lobby + game in one scene) -> EndGame. Maps as streamed prefabs/scenes rather than separate top-level scenes.
- One `SceneChanger`-like service with a "content ready" gate is a good pattern if any assets are downloaded after install.

---

## 3. HUD, input and mobile controls

### 3.1 HudManager composition

`HudManager` (D:HudManager.cs) is a singleton holding references to every in-game UI element [O]:

| Element | Field / type |
|---|---|
| Kill, Use, Report, Vent, Sabotage, Admin, Pet buttons | `KillButton, UseButton, ReportButton, ImpostorVentButton (VentButton), SabotageButton, AdminButton, PetButton` |
| Role abilities | `AbilityButton`, `SecondaryAbilityButton` (generic role-ability buttons; configured through `AbilityButtonSettings`, `UseButtonSettings`) |
| Task list | `TaskPanel : TaskPanelBehaviour`, `TaskStuff` container, `TaskCompleteOverlay`, sounds `TaskCompleteSound, TaskUpdateSound`; HUD builds the task text with a `StringBuilder tasksString` and a dirty timer `taskDirtyTimer` |
| Map | `MapButton` (+ `MapButtonGlyph`), `MatchInfoButton`, `ToggleMapVisible(MapOptions)`, `InitMap()`, `MapBehaviour` class exists |
| Chat | `Chat : ChatController` |
| Cameras | `PlayerCam : FollowerCamera`, separate `UICamera` |
| Fog of war | `ShadowQuad : MeshRenderer` (a mesh quad, not a sprite, drives the vision mask), `FullScreen : SpriteRenderer` overlay |
| Meeting | `MeetingPrefab : MeetingHud`, `OpenMeetingRoom(PlayerControl reporter)`, `discussEmblem`, `shhhEmblem` |
| Intro | `IntroPrefab : IntroCutscene`, `IsIntroDisplayed` |
| Alerts | `AlertFlash`, `DangerMeter`, `StartReactorFlash/StopReactorFlash`, `StartOxyFlash/StopOxyFlash`, `SetAlertOverlay` (critical sabotage flashing) |
| Misc | `KillOverlay`, `CrewmatesKilled : CrewmatesKilledTracker`, `Dialogue : DialogueBox`, `Notifier : NotificationPopper`, `roomTracker`, `GameMenu : OptionsMenuBehaviour`, `SettingsButton`, `GameLoadAnimation`, `LobbyTimerExtensionUI` |
| Console/controller | `consoleUIRoot`, `consoleUIObjects`, `menuNavigationPrompts` |

Methods: `SetHudActive(localPlayer, role, isActive)` (role decides which buttons to show), `ToggleUseAndPetButton(IUsable, canPlayNormally, canPet)`, `ShowPopUp(string)`, `NotifyOfDeath()`, `NotifyOfDisconnect(PlayerControl)`, `UpdateVoteTimerText(StringNames, int)`, `ShowTaskComplete()` [O]. HUD is therefore a role-driven button set (the role object, `RoleBehaviour`, chooses which of these to enable) [I].

### 3.2 Joystick and touch input

- `ControlTypes { VirtualJoystick=0, ScreenJoystick=1, Keyboard=2, Controller=3 }` [O: D:ControlTypes.cs]. The mobile-relevant two:
  - **VirtualJoystick**: a fixed on-screen stick. Fields: `InnerRadius, OuterRadius, Outer (CircleCollider2D), Inner (SpriteRenderer), IsDragged, IsRightJoystick`; outputs `DeltaL`/`DeltaR` vectors; `ToggleVisuals(bool)` [O: D:VirtualJoystick.cs]. A right stick exists (`joystickR`, `RightVJoystick`, `ToggleRightJoystick`) for roles needing aiming (e.g. a second thumb control) [I].
  - **ScreenJoystick**: "touch anywhere" floating stick, uses `touchId`, hit buffer of `Collider2D` [O: D:ScreenJoystick.cs].
- `VirtualJoystickController` polls Unity touch (`TouchState MyTouch`, `fingerId`) and exposes `DragPosition`, `DragStartPosition` and `DragState CheckDrag(Collider2D, deadZone)` with states `NoTouch, TouchStart, Holding, Dragging, Released` [O: D:VirtualJoystickController.cs, D:DragState.cs].
- HUD has `LeftStickDeadZone` and `RightStickDeadZone` Collider2D rects: touches starting on buttons are excluded from the stick [I].
- Stick scale is a user setting: `InputSettingsData.touchJoystickSize` default 1, with a slider (`OptionsMenuBehaviour.JoystickSizeSlider`, `FloatRange JoystickSizes`) and HUD `SetJoystickSize(float)` [O].
- `ActiveInputManager` switches live between `InputType { Joystick=0, Keyboard=1, Touch=2 }`, tracking last-change timestamps per device, firing `CurrentInputSourceChanged`; `ShouldEnableTouch()` and `PlatformDefault` choose the default [O]. HUD `SetTouchType(ControlTypes)` re-lays-out for the active type [O].
- Interactions are tap-on-button: all HUD buttons are `PassiveButton`/`UiElement` objects driven by a `PassiveButtonManager` raycast-style dispatcher (no Unity UI Button) [I from type names; `UnityEngine.UI` assembly exists but the menus use `PassiveButton`].
- Haptics: `VibrationManager` supports local and world-positioned vibrations with falloff curves, intensity constants `SMALL=0.3`, `MEDIUM=0.7`, and optional audio clip-driven rumble [O: D:VibrationManager.cs].

### 3.3 Rewired and controllers

Rewired (`Rewired_Core`, `Rewired_Android` assemblies) is the input abstraction. `AmongUsControlMapper`, `RewiredControllerSupport`, `ControllerManager`, `ControllerNavigation/ControllerNavMenu` give gamepad/keyboard navigation of menus; `DualshockLightManager`, `GlyphControllerMapCollection` supply glyphs [O]. `InputSettingsData.inputData` stores per-key Rewired user-data strings (`RewiredUserDataStore_PlayerData`) [O]. For mobile the touch path does not need Rewired [I].

### 3.4 Other HUD-adjacent UI

- `NotificationPopper` (lobby toasts): disconnect messages and settings-change messages (`AddSettingsChangeMessage(StringNames key, string value, ...)`, `AddRoleSettingsChangeMessage`), queue with `maxMessages`, per-type color/sound [O].
- `AnnouncementPopUp`, `AnnouncementPanel`, `NewsCountButton` + `PlayerAnnouncementData` (announcements fetched from server, read-id list persisted) [O].
- `OptionsMenuBehaviour` (settings): tabs (`TabGroup[] Tabs`), joystick/touch toggle sprites, joystick-size slider, `SoundSlider`, `MusicSlider`, toggles `CensorChatButton`, `EnableFriendInvitesButton`, `ColorBlindButton`, `StreamerModeButton`, `DisableMouseMovement`, language menu (`SettingsLanguageMenu`), bug-report popup [O: D:OptionsMenuBehaviour.cs].
- `SoundManager`: Unity `AudioMixer` with groups Music, Ambience, AmbienceHighPass, Sfx; volume statics; `CrossFadeSound(name, clip, maxVolume, duration=1.5)`; `GetSoundVolume(soundPos, listenerPos, falloffStart=2, falloffEnd=6, min)` = distance attenuation done by hand in world units [O: D:SoundManager.cs].

### Clone note (proposal)
- Provide a left floating/fixed stick + 4 to 6 context buttons (use, kill/ability, report, sabotage/vent, map, chat). Make stick size and handedness settings.
- Drive HUD from a "role defines which buttons exist" model rather than hard-coding per role.

---

## 4. Chat, quick chat, filtering and localization

### 4.1 Chat

- `ChatController` (D:ChatController.cs) [O]: states `ChatControllerState { Closed, Closing, Open, Opening }`; bubble pool (`ObjectPoolBehavior chatBubblePool`), `Scroller`, notify dot, `MAX_CHAT_SEND_RATE = 3` (rate limiter, with `sendRateMessageText` warning and `warningSound`), animation curves in/out, `AddChat(PlayerControl, string, bool censor = true)`, `AddChatNote(NetworkedPlayerInfo, ChatNoteTypes)` where `ChatNoteTypes { DidVote = 0 }` (the small "voted" marker), `AddChatWarning(string)`.
- Two input modes in one controller: `FreeChatInputField` and `QuickChatMenu` + `QuickChatPreviewField`, switched by `QuickChatModes { FreeChatOrQuickChat=1, QuickChatOnly=2 }` [O: D:InnerNet/QuickChatModes.cs]. The stored default is `DEFAULT_CHAT_MODE = 1`, i.e. `FreeChatOrQuickChat` [O: MultiplayerSettingsData]. Whether free chat is *actually* allowed also depends on the KWS permission `freeChatAllowed` and age (section 5) [O fields; I coupling].
- `censorChat` setting default true (`DEFAULT_CENSOR_CHAT`); ban button `BanMenu` in chat for hosts [O].
- A chat-language selector (`ChatLanguageMenu/Button/Set`) filters/tag chat by language [I].

### 4.2 Quick chat system

Namespace `AmongUs.QuickChat` (44 types, `Amongus/QuickChat/`) [O]. It is a structured, localization-safe phrase builder so that no free text is needed for minors:

| Piece | Role |
|---|---|
| `QuickChatTag` | A category/slot with `tagKey`, `inputKey`, color and `parsingKeys` (words that map free text to the tag) |
| `QuickChatPhrase` | `type` in `QuickChatPhraseType { Empty, PlayerId, SimplePhrase, ComplexPhrase }`, `phraseKey : StringNames`, `phraseInputs[]` (each a set of allowed tags), flags `isInput`, `isNegative`, `playerId` |
| `QuickChatData` | Holds `QuickChatTagAssets` + `QuickChatPhraseAssets`; `GetPhrases(context, tags)`, `GetVisibleTags()`; a dictionary tag -> `IQuickChatPhraseCollector` |
| Collectors | `...CrewPhraseCollector`, `...DeadPhraseCollector`, `...FavoritePhraseCollector`, `...LocationPhraseCollector`, `...RolePhraseCollector`, `...SystemPhraseCollector`, `...TaskPhraseCollector` (they fill the menu from live game state) |
| `QuickChatContext` | Snapshot: local player, living and dead players, current-map locations / systems / tasks / roles (`StringNames[]`), `QuickChatContextPhase { None, Lobby, Game }`, `GameModes`, `MapNames`, `meetingCount` |
| Rules | `QuickChatPhaseRules`, `...MapRules`, `...RoleRules`, `...ClassicPhraseRules`, `...HideAndSeekPhraseRules` (which phrases are legal in which phase/mode/map/role) |
| UI | `QuickChatMenu`, landing page, phrase page, breadcrumbs (`QuickChatBreadcrumbType { Tag, Inputs, End }`), preview field, vote timer, favorites icon |
| Wire | `QuickChatNetData.Serialize/Deserialize(MessageWriter/Reader)` sends **only StringNames ids and a player id**, not text; `RpcSendQuickChatMessage` is the RPC [O] |

Key design point **[I]**: because phrases travel as `StringNames` enum values, each receiver renders the sentence in their own language. Favorites are persisted as a `List<StringNames>` (`quickChatFavorites`, `DEFAULT_QUICK_CHAT_FAVORITES`) [O: MultiplayerSettingsData].

### 4.3 Censoring

Per-language censor word lists ship as Addressables text assets: `Assets/CDN/ReferenceData/Languages/<Lang>Censor.txt` for 14 languages (all of the 16 except SChinese and TChinese) [O: CAT keys]. `FreeChatInputField`/`ChatController.AddChat(..., censor)` consume them [I]. There is no profanity ML component in the dump [O: assembly list].

### 4.4 Localization

- `StringNames` enum: about 1,686 members, values spread in ranges (e.g. `RedeemCodeLabel = 4200 ... HorseModeToggle = 4300`) and decorated `[Obfuscation(Exclude=true)]` so names survive obfuscation [O: D:StringNames.cs].
- `SupportedLangs` (16): `English, Latam, Brazilian, Portuguese, Korean, Russian, Dutch, Filipino, French, German, Italian, Japanese, Spanish, SChinese, TChinese, Irish` [O: D:SupportedLangs.cs]. Language assets: 16 `.tsv` string tables + 16 `.asset` (`TranslatedImageSet`) + 14 censor lists under `Assets/CDN/ReferenceData/Languages/` [O: CAT].
- `TranslationController.GetString(StringNames id, params object[] parts)` -> `LanguageUnit.GetString(idString, parts)`, which looks up a `Dictionary<string,string> AllStrings` parsed from TSV (`ParseTSV`, `TryParseTsvLine`, `UnescapeCodes`); fallback language kept in `fallbackLanguage` (English) [O: D:TranslationController.cs, D:LanguageUnit.cs]. Parameters substitute positionally (`parts`) [I]. Images are localized too (`GetImage(ImageNames)`, `TranslatedImageSet`) [O].
- `TextTranslatorTMP` / `ImageTranslator` components subscribe via `TranslationController.ActiveTexts` (list of `ITranslatedText`) so a language change re-renders all live text [O fields, I behavior]. `DoesLanguageSupportBold(SupportedLangs)` handles CJK bold; `SelectDefaultLanguage()` maps device language to a pick [O].
- Cosmetic names use key prefix `"Cosmetic."`, bundles `"Bundle."` [O: `TranslationPrefix`].
- Persisted language: `LanguageSettingsData.language` (string, default `"none"`) + cached enum [O].
- Android store listing is localized into ~80 locales but the *app name* is the same in all [O: XAPK `locales_name`].

### Clone note (proposal)
- Use string-key localization with TSV/JSON tables, fallback to English, and ship quick-chat as enum/id messages from day one (cheap, safe, and avoids a chat moderation burden). Keep free chat optional and gated by age.

---

## 5. Accounts, identity, age gating, platform services

### 5.1 Login stack

| Layer | Evidence |
|---|---|
| **Epic Online Services (EOS)** is the identity/inventory/cloud-save backbone, even on Android | `libEOSSDK.so` (25 MB), `EosUnityPlayerActivity` main activity, `EOSAuthHandlerActivity` in manifest [O: MAN, XAPK]; `EOSManager` holds `productId, sandboxId, deploymentId, clientId, clientSecret` fields, `PlatformInterface`, `ProductUserId` (PUID) [O: D:EOSManager.cs] |
| **Device-ID login** = guest accounts | `BeginLoginFlowWithDeviceID`, `LoginWithDeviceID`, `deviceIDuserID`, `DeleteDeviceID`, `AccountLoginStatus.TempAccount` [O] |
| **Platform login** to link a full account | `LoginWithCorrectPlatform` -> platform auth token (`GetPlatformAuthToken`); on Android Google Play Games sign-in: `OnGoogleAuthenticated(SignInStatus)` [O]; GPGS plugin v0.10.14 (`com.google.android.gms.games.unityVersion` meta-data), `GooglePlayGames` dump folder, `NativeBridgeActivity` [O: MAN] |
| **Merge guest -> full** | `BeginMergeGuestAccountFlow`, `MergeGuestAccountIntoPlatform`, request type `MergeGuestAccount`, UI `AskToMergeGuest` [O] |
| **Offline** | `PlayOffline`, `ContinueInOfflineMode`, `AccountLoginStatus.Offline` [O] |
| Status enum | `AccountLoginStatus { Offline=0, LoggedIn=1, WaitingForParent=2, TempAccount=3 }` [O: D:EOSManager.cs] |
| Accounts UI | `AccountManager` (account tab, sign-in screen `SignInScreen`, `FullAccount`, `KidAccount`, delete-account popup `DeleteAccountPopupBox`/`DeleteAccountRequest`, `EditAccountUsername`, `CopyPUIDAccount`) [O]. Account deletion has a pending state (`isAccountWaitingForDeletion`) [O] |
| Auth to game servers | `AuthManager` (class present) and `UserIDToken`/`DeviceIDUserIDToken` strings on `EOSManager`; token flow itself not visible [O names, I use] |
| Other platforms | `SteamManager`, `Win10Manager`, `PSManager`, `SwitchManager`, `EpicManager` exist in the shared assembly and are inert on Android [O] |

EOS error enum `EOS_ERRORS` includes `FailedEpicAuthToken, UnsupportedPlatform, LinkAccountFail, ... FriendCodeFail=24` [O].

### 5.2 Age gate, parental consent (KWS), privacy

- **Age gate**: `AgeGateScreen` collects day/month/year with three screens (`EnterDateScreen`, `MoreInfoScreen`, `VerifyScreen`); `PlayerAgeData` persists `dateOfBirth` and `latestSetDate` [O]. `EOSManager.CheckAndShowAgePromptIfNeeded`, `IsMinorByDate(DateTime | y,m,d)`, `IsMinorOrWaiting()` [O].
- **KWS (Kids Web Services, Epic's parental-consent service)**: `GetAgeOfConsentFromKWS`, `KWSQueryAgeGateCallback`, `CreateKWSUser`, `QueryKWSPermissions`, `RequestPermissions`, `UpdateGuardianEmail`, `ResendGuardianEmail`; fields `ageOfConsent`, `kwsUserId`, `isKWSMinor`, `guardianEmail` (also in `PlayerAccountData`) [O]. Four permission flags stored on `AccountManager`: `freeChatAllowed`, `customDisplayName`, `friendsListAllowed`, `accountLinkingAllowed` (each `KWSPermissionStatus`) [O].
- Consequences: minors without parental approval get **quick-chat only**, no custom display name, no friends list, no account linking `IsFreechatAllowed()`, `IsFriendsListAllowed()`, `CanMinorSetCustomDisplayName()` [O signatures; I mapping]. The login status `WaitingForParent` is the in-between state [O].
- **Privacy policy / ToS**: `PrivacyPolicyScreen`, `ReferenceData.privacyPolicyVersion` vs `PlayerOnboardingData.LastAcceptedPrivacyPolicyVersion` (re-prompt when version bumps) [O]. URLs in `Constants` (privacy, terms, parent portal) [O].
- **Ad-data consent**: `AdDataCollectScreen` with `SetPersonalized()`/`SetNonPersonalized()`; `ShowAdsState { NotAccepted=0, Personalized=1, NonPersonalized=2, Purchased=4 }` persisted in `PlayerAdsData`; `ManageDataCollectionButton` in settings [O]. `AdsManager.InitLevelPlay(bool isChild)` initializes the ad SDK with a child flag; `RequestATTPermission` (iOS) [O].
- **Account-related data is stored encrypted-ish locally**: `encryptedAge`, `encryptedStore`, `encryptedAds`, `encryptedBan` byte arrays in `PlayerData` (section 8) [O].

### 5.3 Friends and friend codes

- Friend codes are server-issued short codes: `EOSManager.friendCode`, `ResponseFriendCode`, `FriendsListManager.friendcode`, `LobbyInvite`, requests for friend list / block list / "blocked by" / received friend request (`Assets/InnerNet/*` DTOs) [O]. Friends polling interval field `pollingInterval`; `RecentPlayedWithPlayer(PlayerName, Puid, FriendCode)` for adding people from recent games; `PlayerFriendData.cachedNames` (PUID -> display name cache) [O].
- Platform friends (`PlatformFriend`) and invites via Discord (`DiscordManager`: client id const, deep-link scheme `amongus`, join secrets, rich-presence states: menus, lobby host/client, playing) and Twitch (`TwitchManager`, `TwitchDeepLink`; manifest `queries` for `tv.twitch.android.app`) [O].
- Reporting a player: `PlayerReportRequest`; ban points: `PlayerBanData` (`POINTS_UNTIL_BAN_STARTS = 2`, `MINUTES_PER_BAN_POINT = 5`) - local record of a rejoin/leave penalty [O consts, I meaning: points accrue from leaving games early].

### 5.4 Achievements and stats

- `AchievementManager` unlocks via EOS achievements (keys such as `die_during_medscan`, `fix_own_sabotage`, `three_kills_before_meeting`, `win_always_correct_votes`, `block_kill_as_guardian`, `survive_crewmate`, `win_impostor_vote`, `kill_while_shifted`, `win_sabotage`, tiered `task_complete_easy/medium/hard` at 10/100/500, Hide and Seek `hns_*` at 10/50 and 100/200) and EOS stats (`tasksCompleted`, `impostorKills`, `MapWinsSkeld/Polus/Mira/Airship`, `hnsCrewmateWin`, `hnsImpostorKills`) [O: D:AchievementManager.cs]. `PlayerStatsData` caches 76-entry `StatID`, grouped as game-result / map / role stats, batch-ingests to EOS (`statsToIngest`, `SaveStats`), plus `level/xp/xpForNextLevel` [O: D:StatID.cs, D:PlayerStatsData.cs].
- Google Play Games achievements/leaderboards classes (`PlayGamesAchievement`, `PlayGamesLeaderboard`) are in the dump but there is no Among-Us-specific Play Games code path visible; Play Games is used primarily for sign-in [I].
- Progression: `ProgressionManager.MaxLevel = 99`, XP grants return `XpGrantResult` (old/new level, `LevelledUp`), currency grants return `CurrencyGrantResult` with a `Multiplier` (pods earned per game, possibly boosted) [O].

---

## 6. Cosmetics and economy

### 6.1 Cosmetic model

`CosmeticType { Hat=0, Accessory=1, Skin=2 }` is an older enum [O: D:CosmeticType.cs]. The live data classes (each a ScriptableObject-style asset [I]) are:

| Class | Notable fields | Empty id | View data |
|---|---|---|---|
| `HatData` | `InFront, NoBounce, BlocksVisors, RelatedSkin, StoreName`, `ViewDataRef` | `hat_NoHat` | `HatViewData` (sprites: main/back/climb/flip variants [I]) |
| `SkinData` | `StoreName`, `ViewDataRef` | `skin_None` | `SkinViewData` |
| `VisorData` | `behindHats` | `visor_EmptyVisor` | `VisorViewData` |
| `PetData` | `PetPrefabRef` (a full prefab with `PetBehaviour`, follows owner) | `pet_EmptyPet` | prefab |
| `NamePlateData` | `ViewDataRef` | `nameplate_NoPlate` | `NamePlateViewData` (shown behind name in meeting list) |
| `PreviewViewData` | store preview data per item (`PreviewData` asset ref, `PreviewCrewmateColor`) | | |

All inherit `CosmeticData` (sprite, `color`, `ProductId`, `epicId`, `BundleId`, `beanCost`, `starCost`, `paidOnMobile`, `limitedTime: LimitedTimeStartEnd`, `displayOrder`, `NotInStore`, `Free`, `freeRedeemableCosmetic`, `PreviewData`) and a base `UnlockableCosmetic` exposing `BeanCost/StarCost/ProdId/PaidOnMobile/LimitedTimeAvailable` [O: D:CosmeticData.cs]. The `HatViewData` etc. split ("data" vs "view data") means metadata/price is loaded at boot but heavy sprites load lazily via `AssetReference` [I].

- `HatManager` owns arrays `allHats, allSkins, allVisors, allPets, allNamePlates`, lookup by string id, "unlocked" queries, `RefreshFeatured()`, three shared materials (`DefaultShader`, `PlayerMaterial`, `MaskedPlayerMaterial`, `MaskedMaterial`) [O: D:HatManager.cs].
- `CosmeticGroups` = `CosmeticVariantGroup[]` (recolor families) and `CosmeticReleaseGroup[]` (named release dates used to sort store tabs) [O].
- `CosmeticsCache` (per-session cache of loaded `HatViewData`, `SkinViewData`, `VisorViewData`, `NamePlateViewData`, `PetBehaviour`) with `ClearUnusedCosmetics()` and an `AddressableAssetGroup allCachedAssets`; used to avoid re-loading during a match [O].
- `CosmeticsLayer`, `HatParent`, `SkinLayer`, `VisorLayer`, `PetBehaviour` attach a player's cosmetics to their sprite rig [O].
- **Player color**: `CrewmateColor` has 18 entries: Red, Blue, Green, Pink, Orange, Yellow, Black, White, Purple, Brown, Cyan, Lime, Maroon, Rose, Banana, Gray, Tan, Coral (ids 0-17) [O: D:CrewmateColor.cs]. `Palette` holds `PlayerColors / ShadowColors / TextColors / TextOutlineColors` (`Color32[]`) and a `VisorColor` [O: D:Palette.cs]. Tinting is done by a custom shader `Unlit/PlayerShader` (const `PlayerColorShaderPath`) using two color channels (base + shadow) [O const; I two-tone mechanism from `PlayerColors` + `ShadowColors`]. Chosen cosmetics persist as string ids plus a color byte (`PlayerCustomizationData`: name, colorID, pet, hat, skin, visor, namePlate) [O].

### 6.2 Currencies and store

- `CurrencyTypes { Stars=0, Beans=1, Pods=2 }` [O: D:Assets/InnerNet/CurrencyTypes.cs]:
  - **Stars**: premium currency bought with real money (Google Play IAP). Five star packs in the catalog: 20, 30, 50, 110, 300 stars (`Assets/CDN/ReferenceData/StarBundles/Star Bundle N.asset`) [O: CAT]. `StarBundle`: `productId` (store SKU), `starAmount`, `price`, `epicId`, `SteamAppId` [O].
  - **Beans**: soft currency earned by playing; displayed with `Constants.Beans`; inventory path `beans/normal` [O].
  - **Pods**: per-cosmicube collectible, earned, spent to unlock cells in a cube; inventory folder `pods` [O].
- Inventory service: `InventoryManager` reads the user's **EOS Inventory** (`InventoryInterface`, container `profile`), paths `beans/normal`, `stars/normal`, `stars/switch`, `player/xp`, `player/level`, `pods/*`; `ChangePodCount`, `UpdateStarCount`, `CheckUnlockedItems`, `CheckEquipped`, `ValidatePreviousReceipts`, `AddOutsideStarPurchase` [O: D:InventoryManager.cs]. Ownership of cosmetics is therefore **server-side**, not purely a local flag [I]; local `PlayerPurchasesData` + `PlayerStoreData.legacyPurchases` keep old pre-account IAP items (e.g. `hat_holiday2018`, `hats_halloween2019`, `pet_test`) [O].
- IAP: `StoreManager : IStoreListener` over **Unity Purchasing** (`IStoreController`, `ProcessPurchase`, `PurchaseFailureReason`, `ProcessOldReceipts`, `ProcessOldReceiptsRestore`, `InitiateStorePurchaseStar(button)`), receipts as DTOs (`InnerslothReceipt`, `EgsReceipt`, `iOSReceipt`) that are posted to the game backend to credit the EOS inventory (`InventoryManager.requestEndpoint/requestJson`) [O names; I server-validation flow]. `PurchaseStates { NotStarted, Started, Success, Fail }`; `RestorePurchases` button; `PurchasePopUp`, `PurchaseButton`, `BundlePurchaseButton` [O]. Manifest: Play Billing client 8.0.0 [O: MAN `com.google.android.play.billingclient.version`].
- Store tabs: `StoreTabButton`, `HatsTab, SkinsTab, VisorsTab, PetsTab, NameplatesTab, CubesTab`, with per-tab "last viewed" dates persisted (`PlayerStoreData.hatsViewDate`, ...) to show a "new items" badge (`NewStoreItemsIcon`, `CheckNewItems`) [O]. `ReferenceData` holds per-tab version strings (`StoreBundlesVersion, StoreHatsVersion, ...`) used to invalidate those badges/caches [O: D:ReferenceData.cs].
- Bundles: `BundleData { BundleSize (Wide|Large), productId, beanCost, starCost, cosmetics[], StoreImage, limitedTime }`; 11 legacy bundle assets exist (holiday 2018 hats, pet packs, map bundles for Airship/Mira/Polus - the old paid-map DLC) [O: CAT `ReferenceData/Bundles/*`]. `EOSManager.validateOldDLC`, `isRedeemingDLC` handle migrating old DLC ownership [O].
- Free / limited time: `limitedTime : LimitedTimeStartEnd`, `AprilFoolLimitedTime`, `Free`, `NotInStore`, `freeRedeemableCosmetic` + redeem codes (`RedeemCosmeticPopup`, `RedeemCurrency`) [O].

### 6.3 Cosmicubes

- A Cosmicube is a collectible "grid" of cosmetics tied to a theme/collab; players spend **pods** to unlock cells, or pay stars/beans for a cube (`CosmicubeData`: `starCost, beanCost, podId, Order, Free, NotInStore, DateCreated, XRange/YRange, StoreImageRef, CosmicubePrefabRef, podIconRef`) [O: D:CosmicubeData.cs]. UI: `CosmicubeMenu`, `CubesTab`, `CosmicubeCell`, `CubeCellPurchaseButton`, `CosmicubeUnlockConfirmMenu`. Progress: `CosmicubeManager.GetCompletionProgress/IsCompleted`. Active cube stored in `PlayerStoreData.activeCosmicube` and cloud `CloudPlayerPrefs.ActivePodType` [O].
- Catalog shows 28 named cubes (plus an empty cube prefab): map cubes (Polus, MIRA, Airship, Fungle), season cubes (Winter 2022 free, W21 free/paid, HW21 free/paid, LNY24 free/paid, FoodCube free/paid, "2026 Cosmic"), and collab cubes (Crit Role, Arcane, Destiny 2, Hololive, Pusheen, IndieCube2023, Project Parasite, 15.7 Paws and Claws, 16.0 Kamurocho, 17.0 Racing, 17.1 Stardew, 17.2 Hanami, Innersloth) [O: CAT key folders `Assets/Cosmicube/Cosmicubes/<name>`]. Free+paid pairs = a battle-pass-like free and premium track [I].
- Quality tiers are color-coded in `Palette` (`CosmicubeQuality_NamePlate/Hat/Visor/Skin/Pet/Kill`), so cubes can also award kill animations ("Kill" quality) [O names; I meaning].

### 6.4 Ads

- Only **rewarded video** is wired in code: `AdsManager` with a `LevelPlayRewardedAd` and `rewardedAdUnitId`, events `OnAdRewarded, OnAdLoaded, OnAdLoadFailed, ...`; `Constants.MAX_ADS_PER_DAY = 2`; `PlayerAdsData` tracks `adsWatched`, `lastDateWatchAd`, `CanWatchAdAgain`, `HoursLeftNewAd()`; remove-ads purchase flag `hasPurchasedAdRemoval` [O].
- Mediation: Unity LevelPlay (ironSource) + Unity Ads SDK (see `08` for manifest evidence). Banner/interstitial classes exist in the SDK assembly but no Among-Us call sites are visible (no fields for them in `AdsManager`) [O].
- Ad reward effect (what the player gets) is not visible in the dump; likely beans or a cosmetic unlock [I].

### 6.5 Content delivery of cosmetics (summary; detail in doc 08)

- Cosmetic **metadata** (`HatData` etc.) is in `referencedatagroup_assets_all` (2.3 MB) under `Assets/CDN/ReferenceData/...` (Hats 584 keys, Skins 216, Visors 185, NamePlates 166, Pets 53, Groups 144, Bundles 11, StarBundles 5, Languages 46) [O: CAT keys / PACK file]. **Sprites/prefabs** (`*ViewData`, previews, cube prefabs) are in large bundles: `initialcosmetics` (266 MB, shipped), per-collab bundles (e.g. `stardewa/b`, `racingcubea/b`, `pawsnclaws`, `kamurocho`, `hanami`, `critrolecubea/b`, `projectparasitecubea/b`, `cosmica/b`, `cosmicubelny24`) and small event bundles (`nye2025`, `nye2026`, `bday2026`, `2024_birthday_beans`, `2024beanbundle1`, `2025beanbuffer2`, `impostormonth_2026`, `anniversaryslothmask`, `genshin`, `edgeworth`, `phoenixwright`) [O: PACK, CAT].
- Because `Assets/CDN/...` keys exist, the live store is **data-driven**: designers add `HatData` assets and publish a new remote catalog; the client does not need an app update (see section 7) [I].

---

## 7. Content delivery architecture

- **Addressables 1.x** with provider list: `LegacyResourcesProvider`, `PlayAssetDeliveryAssetBundleProvider` (custom, from `AddressablesPlayAssetDelivery` assembly), `BundledAssetProvider`, etc. [O: CAT `m_ResourceProviderData`].
- Local catalog is `assets/aa/catalog.json` (1.1 MB) in the asset pack; `assets/aa/settings.json` points at a **remote catalog hash** on **Unity Cloud Content Delivery (CCD)**: URL pattern `https://<guid>.client-api.unity3dusercontent.com/client_api/v1/buckets/<guid>/release_by_badge/2026_9_29-2026_8_17_1125/entry_by_path/content/?path=/catalog_19.hash` with a local cache at `{persistentDataPath}/com.unity.addressables/catalog_19.hash` [O: PACK `assets/aa/settings.json`]. The bundle badge is `<appVersion>-<addressablesVersion>`; so each app release is matched to a content release [I].
- Custom cache layer `AmongUsCaching.AmongUsCDNCache` (`DownloadBundle(url, CachedAssetBundle)`, `LoadBundleFromCache`, `SaveAssetBundleCache`, `WriteCatalog`, `CheckLocalCatalog`, `DeleteCatalog`) and a `Constants.CCDBucketAccessToken` for private buckets [O: DL:AmongUsCaching].
- 29 bundles in the asset pack, 0.68 GB, plus the built-in Unity player data (levels, sharedassets, resources) of 0.38 GB, together 1.06 GB (see doc 08 for per-bundle sizes) [O: PACK].
- 3,736 `Assets/...` keys in the catalog, all of type `.asset`/`.prefab`/`.png`/`.tsv`/`.txt`/`.csv`: 1,455 cosmetics (incl. 1,033 preview-data assets), 1,411 CDN reference data, 861 cosmicube items, 8 map prefabs [O: CAT counts by path].

### Clone note (proposal)
- Even without Unity, plan content as "manifest + versioned packs": app ships an offline minimum set, downloads event/collab packs on demand, never gates core gameplay on a download.

---

## 8. Persistence (what is stored locally)

All via `AmongUs.Data` (D:Amongus/Data/). `DataManager` exposes two static roots: `DataManager.Player` (`PlayerData`) and `DataManager.Settings` (`SettingsData`), with `IsPlayerLoaded/IsSettingsLoaded` and `DeleteAll()` [O]. Both derive from `AbstractSaveData`, which has: dirty flag, `Load/Save/ForceLoad/ForceSave`, a **data version** with migration hook (`HandleVersionMigration`), per-"group" subobjects (`AbstractSaveDataGroup`), JSON helpers (`TryLoadFromJsonFile`, `TrySaveToJsonFile`) using Newtonsoft.Json, and `TrySerializeAndEncrypt/TryDecryptAndDeserialize` with a single-byte XOR constant (`XOR_KEY = 212`) = obfuscation, not security [O names/consts; I JSON-on-disk].

Files and keys:

| Store | Name | Notes | Source |
|---|---|---|---|
| Player data file | `player.amogus` | per-user path (`ShouldIncludeUserIdInPath`) so multiple accounts on one device do not collide | D:Constants.cs `PLAYER_FILENAME`; D:AbstractUserSaveData.cs [O] |
| Settings file | `settings.amogus` | device-wide settings | `SETTINGS_FILENAME` [O] |
| Legacy secure file | `secureNew` | pre-2021 save, read by `LegacySaveManager` / `LegacySecureDataFile` for migration | D:Constants.cs; D:Amongus/Data/Legacy [O] |
| Cloud prefs | `player.prefs` in EOS Player Data Storage | chunks of 40,960 bytes; `CloudPlayerPrefs { ActivePodType, receiptRedemptionAccounts, PlatformsLimitedItemsValidated }` | D:PlayerStorageManager.cs [O] |
| Server inventory | EOS Inventory `profile` | beans, stars, pods, xp, level, unlocked cosmetics | D:InventoryManager.cs [O] |
| Addressables cache | `{persistentDataPath}/com.unity.addressables/` | catalog + bundles | PACK settings.json [O] |

`PlayerData` groups [O: D:Amongus/Data/Player/]:

| Group | Contents |
|---|---|
| Account | login status, guardian email, "delete device id failed" |
| Customization | name, color id, pet, hat, skin, visor, nameplate (all string ids except color byte) |
| Onboarding | accepted privacy-policy version, viewed online help, min-player warning flags, Hide and Seek how-to-play flag |
| Age (encrypted blob) | date of birth, date set |
| Store (encrypted blob) | active cosmicube, per-tab last-viewed dates, legacy purchases |
| Ads (encrypted blob) | ads state (consent), watched count, last watch time, ad-removal purchased |
| Ban (encrypted blob) | ban points, previous game start time |
| Announcements | read ids, latest announcement number, cached list |
| Friends | puid -> cached display name |
| Purchases | per-item purchase markers |
| Stats | level/xp, stat caches, tracking flag |

`SettingsData` groups [O: D:Amongus/Data/Settings/]:

| Group | Fields (defaults) |
|---|---|
| Gameplay | `screenShake` (true), `streamerMode` (false) |
| Accessibility | `colorBlindMode` (false) |
| Audio | music / ambience / sfx volume (each 0.6) |
| Video | `vsync` (false) |
| Language | language string ("none") + cached `SupportedLangs` |
| Input | `inputMode: ControlTypes`, `mouseMovementEnabled`, `touchJoystickSize` (1.0), Rewired user-data map |
| Multiplayer | `crossPlay` (true), `allowFriendInvites` (true), `censorChat` (true), `chatMode` (1), quick-chat favorites list, last played game mode, raw host/search game-option byte blobs for Normal and Hide and Seek, matchmaking filter tags and filter sets, valid-filter refresh date |

Notable: game rules chosen by the host are stored as **raw bytes** (`rawNormalHostOptions`, `rawHideNSeekHostOptions`, plus search-options twins) [O] - i.e. the same binary option serialization used on the wire is reused for local persistence [I].

### Clone note (proposal)
- Two JSON files (profile, settings), schema-versioned with migration, cloud copy optional. Keep age/consent state separate and treat it as sensitive.

---

## 9. Unknowns and gaps

- Exact load order of scenes and which scene is index 0 (we have the set, not the order) [GGM strings only].
- How the Android-specific ad reward is granted and what price points the 5 star packs have (SKUs live in `StarBundle` asset contents inside the bundle, not in the catalog index).
- KWS/EOS server endpoints, backend REST routes, and auth token flows: not decoded (names only).
- No method bodies: sizes/timings (joystick dead zone radius, chat cooldown behavior beyond `MAX_CHAT_SEND_RATE = 3`) beyond constants are unknown.
- UI layout (anchors, safe areas): `AspectPosition` components exist but values are inside scene/prefab data not examined.

## Related docs

- Networking, protocol, matchmaking servers: `docs/02-*` through `docs/04-*` (written by other workers).
- Roles, tasks, maps: `docs/05-roles.md` and the other gameplay docs.
- Tech stack, packages, assets: `docs/08-tech-stack-and-assets.md`.
