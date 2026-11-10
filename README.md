# SplitBonk — Megabonk local splitscreen

> **V1.0.4 Alpha** (mod version `1.0.4-alpha`). Developed by Claude (Anthropic's AI): all the code, investigation and
> tests. Playtesting and direction by TheSplitScreenGuy.
>
> SplitBonk is an unofficial fan mod, not affiliated with or endorsed by the developers of Megabonk. It contains no
> game code or assets; you need your own copy of the game.

A BepInEx mod that adds 2-player local splitscreen to Megabonk: same PC (or Steam Deck), two controllers, or keyboard and mouse for player 1 and a controller for player 2.
Player 2 has their own character, weapons, tomes, items, gold, XP, health and camera, and the run only ends when
both players are down.

## Features
- **Joining:** after Play on the main menu, a PLAYERS screen opens. Player 2 presses Start on a second controller
  to join (B to leave), then CONFIRM.
- **Player 1 on keyboard and mouse:** click the button under PLAYER 1 to switch between CONTROLLER and
  KEYBOARD + MOUSE (remembered). With keyboard + mouse, every controller is free for player 2, so one controller is
  enough.
- **Character select:** each player has their own half of the screen, character pick and skin. P2 starts on P1's
  pick, and P2's skin row lists the skins your save can use.
- **Cameras:**
  - SPLIT: always two views.
  - DYNAMIC: one shared view while you're close together, splitting when you move apart.
  - Switch in the pause menu (CAMERA). The choice is remembered.
- **Player 2 plays like player 1:** the game's own movement code runs for P2 (running, jumping, sliding with the
  slide pose, wall-climbing). P2 also has:
  - the character's passive ability
  - their own weapons, tomes and items, with stats calculated by the game
  - the game's health code: shields, regen, armor, evasion, lifesteal, debuffs
- **Separate progress:** XP, levels, gold, kills and level-up screens are per player. Level-ups, chests, shrines,
  Moai and other reward screens open on the half of the player who earned them, and that player controls them.
- **Pickups and power-ups:**
  - XP and gold go to whoever collects them.
  - The magnet pulls to the player who picked it up, and pots P2 breaks drop for P2.
  - Hearts, shields, haste, rage and stonks work per player.
  - The clock slows every enemy, whoever picks it up.
- **Enemies and bosses:**
  - Enemies chase the nearer living player. While you're far apart, some spawn around P2.
  - Boss attacks hit and target either player. Tested: Bark Vader's spikes and orbs, Anubis' red and blue lasers.
- **HUD:** each player has their own health bar, weapons and tomes, XP bar, level, gold, minimap and full map
  (P2: right trigger), with their own stats panel.
- **Down and revive:** a downed player becomes a gravestone; stand at it until the ring fills to revive them.
  The run ends when both players are down. The end screen shows both players' summaries.

## Online safety
Modded runs are single-player only. While the mod is installed it blocks everything a modded run could send to Steam:
- leaderboard score uploads
- Steam stats
- Steam achievements

Reading leaderboards still works. Remove the mod to get uploads back.

## Requirements
- Megabonk (Steam, Windows version; on Steam Deck / Linux through Proton)
- Two controllers, or keyboard and mouse for player 1 plus one controller for player 2. On Steam Deck, player 1 can use the Deck's own controls or a controller (Bluetooth or USB);
  player 2 needs a controller of their own.
- BepInEx 6 bleeding edge, IL2CPP x64 (tested with `6.0.0-be.788`). The release zip includes it.

## Install (release zip)
1. Find your Megabonk folder: in Steam, right-click Megabonk > Manage > Browse local files.
2. Extract everything in `SplitBonk-V1.0.4-Alpha.zip` into that folder, so that `winhttp.dll` sits next to
   `Megabonk.exe`.
3. Start Megabonk from Steam as usual. The first start takes a minute or two longer while BepInEx prepares itself.

### Steam Deck / Linux
1. Steam > Megabonk > Properties > Compatibility: force the newest Proton (the Linux build doesn't load the mod).
   Wait for Steam's update; the folder must then have `Megabonk.exe`.
2. Properties > General > Launch Options: `WINEDLLOVERRIDES="winhttp=n,b" %command%` (type the quotes by hand).
3. Properties > General > Game Resolution: Native.
4. Extract the zip into the folder with `Megabonk.exe` (Desktop Mode: Manage > Browse local files).
5. Start the game from Steam. The first start takes a few minutes.
6. If the picture still looks off once you're in Megabonk, change the resolution in the game's own settings.

### Uninstall
Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, the `dotnet` folder and the `BepInEx` folder (or
only `BepInEx\plugins\MegabonkSplitscreen` to remove just this mod). Steam > Verify integrity of game files puts the
game back exactly as it was.

## Settings
`BepInEx\config\thesplitscreenguy.megabonksplitscreen.cfg` is created on the first start. Edit it with the game closed.

| Section | Setting | Default | What it does |
|---|---|---|---|
| Camera | `MergeWhenClose` | `true` | Dynamic camera (one view when close). Also switched in the pause menu. |
| Camera | `SplitDistance` / `MergeDistance` | `30` / `24` | Distance at which the shared view splits / merges again. |
| Spawning | `SpawnAroundP2` | `true` | While apart, every other enemy spawns around player 2. |
| Spawning | `SpawnMultiplierWhenApart` | `2` | More enemies while apart (`1` = off). |
| Debug | `DebugLogging` | `false` | Detailed log lines and UI dumps for bug hunting (costs a little speed). |
| Experimental | `P2OwnStats`, `P2ContextHooks`, `P2GameMovement`, `P2GameHealth` | `true` | Fallbacks if something breaks: switch one off to go back to a simpler version of that part of player 2. |
| CharacterSelect | `P2Spacing`, `CameraPullBack`, `CameraCenterShift` | | Where P2's avatar and the menu camera sit. |

## Reporting a problem
After a run with a problem, quit the game and send `BepInEx\plugins\MegabonkSplitscreen\MegabonkSplitscreen.log` from
the Megabonk folder, with what you saw. The log is replaced on every start of the game, so grab it **before** starting
the game again. For hard-to-catch bugs, set `DebugLogging = true` first.

If a black BepInEx console window opens with the game, or player 2's controller isn't detected, switch the console
off: `BepInEx\config\BepInEx.cfg`, `[Logging.Console]`, `Enabled = false`. With a console window open, Steam can
switch controllers to its desktop layout (keyboard and mouse), and the second pad can't be seen. The release zip
ships this setting already off; it can come back if BepInEx's config is deleted, or if you installed an older zip.

## Known limitations (alpha)
- Two players only (a 4-player version is planned).
- Player 2's active ability button: the game has no controller binding for it, so P2 can't trigger it from a pad.
- A few small run-start bonuses from your save (for example the shop's silver-gain upgrade) count for player 1 only.

## FAQ

### Why doesn't SplitBonk just duplicate Player 1?
Megabonk is built around exactly one player. A lot of the game finds the player through a single global "the player"
reference: enemies, bosses, pickups, the HUD, the camera, saves and stats. It also announces events like "the
player's inventory was set up" or "the player's stats changed" to the whole game.

If you copy Player 1 wholesale, the copy announces itself as "the player" too:
- The game's single "the player" reference gets taken over by the copy, so the game starts treating Player 2 as the
  main player.
- P1's own systems hear the copy's events and re-attach to it. We saw this early on: just creating a second inventory
  for P2 made P1's HUD, XP bar, health bar, pickup range and aura weapons switch over to P2's inventory, and new weapons
  P1 picked up stopped showing on P1's HUD.
- Every game feature that looks for "the player" would still find only one, so the duplicate wouldn't be targeted,
  damaged or rewarded properly anyway.

So SplitBonk builds Player 2 out of the game's own parts, each used only where it's safe:
- **Body:** the character's real model and animations from the game's own character prefab.
- **Movement:** a stripped copy of the game's movement component, with everything except the movement code removed,
  driven by P2's controller. Running, jumping, sliding and wall-climbing use the game's real physics.
- **Weapons, tomes, items, stats:** P2 has its own holders, and the game's own code calculates P2's stats from them.
- **Health, shields, regen, debuffs:** the game's own health code, run on P2's values.
- **Everything that only knows "the player"** (bosses, pickups, power-ups, reward screens, magnets, pots): for that
  one call, the mod briefly points the game at P2, then puts P1 back.

The result: P1 stays the game's real player, so the game's systems don't get confused. P2 gets the game's real
behaviour wherever possible. And each one-player-only feature can be adapted to P2 on its own without breaking P1.
That's also why the patch notes keep listing individual things being "made to work for P2": each one is a spot where
the game assumed a single player.

## Building
.NET 8 SDK. `dotnet build src/MegabonkSplitscreen -c Release`, then `tools/install.ps1` (game must be closed).
The project compiles against the DLLs in the game's `BepInEx` folder (`GameDir` in the .csproj).

- `tools/make_release.ps1` builds the release zip (`dist/SplitBonk-V1.0.4-Alpha.zip`). Its README comes from
  `release/README.txt`.
- `tools/autotest.ps1 tests/<name>.txt` runs a scripted test game that drives both players. `tests/` has the scripts,
  for example `perf_6min.txt` for a 6-minute performance run.
