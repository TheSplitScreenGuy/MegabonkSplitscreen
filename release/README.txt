{RELEASE}
====================
MegabonkSplitscreen mod version {VERSION}

Local two-player splitscreen for Megabonk (same PC; two controllers, or keyboard and mouse for player 1). Player 2 has their own character, weapons, tomes,
items, gold, health and camera, and the run only ends when both players are down.

SplitBonk is an unofficial fan mod, not affiliated with or endorsed by the developers of Megabonk. It contains no game
code or assets: you need your own copy of Megabonk on Steam.
Developed by Claude (Anthropic's AI); playtesting and direction by TheSplitScreenGuy.
Modded runs are single-player only: leaderboards, online stats and achievements are switched off while the mod runs.


Install
-------
1. Find your Megabonk folder: in Steam, right-click Megabonk > Manage > Browse local files.
2. Extract everything in this zip into that folder, so that winhttp.dll sits next to Megabonk.exe and the BepInEx
   folder merges with any existing one. If asked, let it replace BepInEx\config\BepInEx.cfg: it switches BepInEx's
   console window off (see "If something's wrong").
3. Start Megabonk from Steam as usual. The first start takes a minute or two longer while BepInEx prepares itself;
   later starts are normal.


Steam Deck / Linux
------------------
The mod runs on the Windows version of the game through Proton:
1. Steam > Megabonk > Properties > Compatibility: tick "Force the use of a specific Steam Play compatibility tool" and
   pick the newest Proton. Wait for Steam's update - the game folder must then have Megabonk.exe (the Linux version,
   Megabonk.x86_64, doesn't load the mod).
2. Properties > General > Launch Options:   WINEDLLOVERRIDES="winhttp=n,b" %command%
3. Properties > General > Game Resolution: Native (otherwise the game's resolution setting can show "?").
4. Extract the zip with "Extract here" into the folder that has Megabonk.exe (Desktop Mode: Steam > Megabonk >
   Manage > Browse local files), so winhttp.dll sits next to Megabonk.exe.
5. Start the game from Steam (Game Mode or Desktop Mode). The first start takes a few minutes.
6. If the picture still looks off once you're in Megabonk, change the resolution in the game's own settings.
Player 1 can use the Deck's own controls or a controller (Bluetooth or USB); player 2 needs a controller of their own.


Playing
-------
- Two controllers: player 1 uses the first one, player 2 the second.
- Player 1 on keyboard and mouse: on the PLAYERS screen, click the button under PLAYER 1 (CONTROLLER / KEYBOARD +
  MOUSE). Then any controller's Start joins as player 2 - one controller is enough. The choice is remembered.
- After Play on the main menu, the PLAYERS screen opens: player 2 presses Start to join (B to leave), then CONFIRM.
- Each player picks a character; player 2 starts on player 1's pick.
- Pause menu > CAMERA: SPLIT (always two views) or DYNAMIC (one view while you're close together). Remembered.
- A downed player can be revived: stand at their gravestone until the ring fills.
- Player 2 stuck? Pause menu > PLAYER 2: GIVE UP (next to RESTART, press twice) puts player 2 down for a revive.


If something's wrong
--------------------
- A black BepInEx console window opens with the game, or player 2's controller isn't seen: open
  BepInEx\config\BepInEx.cfg and under [Logging.Console] set  Enabled = false . With that window open, Steam can switch
  the controllers to its desktop layout (keyboard and mouse) and the second controller can't be read.
- Anything else: send BepInEx\plugins\MegabonkSplitscreen\MegabonkSplitscreen.log (it's replaced on every start of
  the game, so copy it before playing again) with what you saw.


FAQ
---
Why doesn't SplitBonk just duplicate Player 1?

Megabonk is built around exactly one player. A lot of the game finds the player through a single global "the player"
reference: enemies, bosses, pickups, the HUD, the camera, saves and stats. It also announces events like "the
player's inventory was set up" or "the player's stats changed" to the whole game.

A straight copy of Player 1 announces itself as "the player" too: it takes over the game's single player reference,
and P1's own HUD and systems re-attach to it. (Early on, just creating a second inventory for P2 moved P1's HUD,
XP bar, health bar, pickup range and aura weapons over to P2.) And everything that looks for "the player" would still
find only one, so the copy wouldn't be targeted, damaged or rewarded properly anyway.

So Player 2 is built from the game's own parts, each used only where it's safe:
- the character's real model and animations,
- the game's own movement code (running, jumping, sliding, wall-climbing), driven by P2's controller,
- P2's own weapons, tomes, items and stats, calculated by the game's own code,
- the game's own health code (shields, regen, debuffs) on P2's values,
- and wherever the game only knows "the player" (bosses, pickups, power-ups, reward screens, magnets, pots), the mod
  briefly points the game at P2 for that one action, then puts P1 back.

P1 stays the game's real player, P2 gets the game's real behaviour wherever possible, and each one-player-only
feature can be adapted to P2 on its own without breaking P1.


Uninstall
---------
Delete winhttp.dll, doorstop_config.ini, .doorstop_version, the dotnet folder and the BepInEx folder from the Megabonk
folder (or just BepInEx\plugins\MegabonkSplitscreen to remove only this mod). Steam > Verify integrity of game files
puts the game back exactly as it was.


Included
--------
- MegabonkSplitscreen (BepInEx\plugins\MegabonkSplitscreen\MegabonkSplitscreen.dll)
- BepInEx {BEPINEX} (the mod loader, LGPL-2.1): https://github.com/BepInEx/BepInEx
  with Unity Doorstop {DOORSTOP} and its .NET runtime (dotnet folder)
No files of the game itself are included. Licenses of the included components: THIRD-PARTY-NOTICES.txt.
