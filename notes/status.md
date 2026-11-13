# MegabonkSplitscreen status

Goal: 2-player local splitscreen for Megabonk (single-player Unity game).

## RELEASE 1.0.6-alpha ("SplitBonk V1.0.6 Alpha", 2026-11-12)
Character select stick drift fix, P2 give-up button (README.md + release\README.txt mention it), TEMP diagnostics kept
in players' logs ([down] P1 watch after revives, [down] P2 no-damage watch). dist\SplitBonk-V1.0.6-Alpha.zip by
make_release.ps1 (231 files, 0 backslash names, console-off cfg, DLL = the build = installed, SHA256 375E9178...2301);
1.0.5 zip moved to dist\old. Open: P2 takes no damage after P1's first death (desert tier 3) - waiting for a log.

## User (2026-11-12): P2's extra jumps "don't feel right" (suspected the float/levitation fixes) - NOT reproduced
Measured with Feathers x2 (ExtraJumps 2, JumpHeight 9 -> 11.7 for both), 'jumps' = upward kicks (landings count too):
- GetJumpForce is the same for both (540 -> 702 with Feathers); P2's tap 9.82 m = P1's. Earlier "P2 5.7 m" results
  were one test spot with something overhead (always exactly 5.67 m) - not a difference.
- The 1.0.5 "jump held" fix ('fix hold off/on'): no change to P2's jumps.
- Tried and REMOVED: Jump Bhop answered with P2's own (unbound) button - wrong, P1 itself bunny-hops while holding
  jump (7 kicks in 2.5 s); a jump-press buffer for P2 like P1's PlayerInput (jumpBufferTicks 4) - no difference:
  tests\bug106_jumpbuffer.txt (natural spawns off; first run spoiled by P2 landing on a skeleton): every tap fired an
  extra jump with and without it (2 taps ~22 m, 3 taps ~52 m).
- User's idea: air jump while falling - does it cancel the fall? tests\bug106_airjump_momentum.txt ('jumps' logs each
  kick's vertical speed before -> after): the game's rule, the same for both players - falling: the speed is reset to
  the jump speed (P1 -14.9 -> 22.8); rising: the jump is added on top (P1 15.8 -> 38.5, P2 9.9 -> 32.7, +22.7/+22.8).
- User (as Calcium): an air jump sometimes "only jumped a tiny bit" while moving. tests\bug106_calcium_airjump.txt
  (running) vs bug106_calcium_still.txt (standing): the game's own rule, identical for P1 and P2 - standing still the
  fall is always reset (-13 -> +21.5); running, the air jump mostly only adds ~+18.7 to the fall (-20.7 -> -2.0 P1,
  -16.4 -> +2.3 P2), now and then a full reset. Not a mod difference.
- P2's ground jump looked weaker (17.2 vs 23.0 m/s take-off): tests\bug106_jump_swap.txt - it's the test spot: at
  'home 300's P2 spot BOTH players take off at 17.2 (5.67 m), at P1's spot both at 23.0 (9.82 m). No P2 difference.
- Test words: 'jumps <p> <secs> [label]', 'jumpforce', 'p1hold <secs>', 'p1tap' (doesn't fire yet - a single-step
  press is overwritten by the game's own input read). Next: the user's description of what feels off.
- Give-up labels: "AGAIN: DOWN P2" / "AGAIN: RUN ENDS" (user: "ENDS THE RUN" too big) + TMP auto-size down to 60 %.

## Pause menu: PLAYER 2: GIVE UP (user's idea, 2026-11-12 - escape hatch, e.g. P2 stuck taking no damage)
CameraModeToggle.Copy makes a second copy of the game's pause button under the camera button. One press arms it (label
"PRESS AGAIN TO DOWN P2", 3 s; "AGAIN: ENDS THE RUN" if P1 is down), the second downs P2 (P2Health.TakeDamage, test
health off) and resumes; "PLAYER 2 IS DOWN" while down. tests\pause_p2_giveup.txt: arm -> P2 up, confirm -> P2 down +
gravestone, menu closed, button inert while down, P1 revives P2. In the column it pushed Exit off the screen; user:
"move it to the right of restart" - DONE: out of the column's layout (LayoutElement.ignoreLayout), placed beside
B_Restart each frame (Restart's size, 24 px gap); Restart's navigation is Automatic, so right/left find it by
themselves (test: Restart -> right -> give up -> left -> Restart, Restart -> down -> Exit). Exit fully on screen again.
- Character select stick fix CONFIRMED by the user on real pads; the user's log shows the cause: polling 'Left Stick X'
  Negative while the stick read 0.00 (ignored now).

## User (2026-11-12, real pads): back at character select after a run, P2's cursor stuck in the skin row
User: "like the controller has stick drift to go left" (the skin row is left of P2's grid, so a constant left keeps the
cursor there). Log: P2's panel re-bound fine (21 of 21 buttons), skin row bound late. P2Select read the stick with
PollControllerForFirstAxis only - polling can report an axis that isn't pushed (seen before with released triggers).
FIX (untested on pads; autotests can't feed real axes): a polled stick direction counts only if the stick's own value
(joy.GetAxisById) is at least 0.5 that way; an ignored one is logged as [pads] (kept in players' logs).
User (real pads): no more drift into the skins, d-pad fine, but stick up/down did nothing. Log: X values ramp normally;
Y read with the opposite sign to polling's pole. Now only |value| >= 0.5 is checked, direction from polling (not yet
confirmed on pads).

## Player report after 1.0.5 (2026-11-12): P2 takes no damage after P1's first death (desert tier 3, stage 2, ghosts)
The user re-read the friend's text: it IS P2 (a middle "it was P1" reading was wrong). Autotests below didn't
reproduce either reading. New TEMP diagnostic kept in players' logs: P2Health.WatchNoDamage - an enemy within P2's
capsule for 2 s while P2's HP + shield don't drop -> one [down] warning with everything that decides P2's contact
damage (test health, game health, damage cooldown, evasion/armor, time freeze, paused, layer mask, the last contact
check's outcome, P1 down, P2's effects); max 6 per run. Checked: fires during the clock's freeze with "time freeze".
Earlier tests and readings:
- tests\bug106_p1down_desert.txt / bug106_p1killed_desert.txt (P2's damage, stage 2, P1 down): P2 hit normally.
- tests\bug106_p1revived_damage.txt (Forest: P1 down, revived by P2 at the gravestone): P1's colliders back on, damage
  from skeletons 135 -> 75 and ghosts -> 46.
- tests\bug106_revive_then_portal.txt (user's route: Desert tier 3 stage 1, P1 down, revived, portal): stage 2 P1 hit
  116 -> down.
- tests\bug106_down_portal_p2down.txt (user's route: P1 down on stage 1, P2 takes the portal = P1 revived by the new
  stage, then P2 down): P1 hit 135 -> 68, after P2 down 69 -> 2 -> 0. One run P1 ended 73 m up, out of reach - a desert
  tornado lifted P1 (the user watched it; game hazard).
- Diagnostics added (TEMP): [down] P1 watch for 60 s after every P1 revive (Downed.WatchP1, kept in players' logs) -
  P1's damage state 5 s in, and a warning with the full state if an enemy touches P1 for 2 s without HP lost;
  [hit0] (autotest only) for P2's game hits that took nothing; 'p1cols' test command.
- Found in passing, not changed: Revive.Clear (rise cut short) turns P1's colliders back on but leaves P1's rigidbody
  kinematic.

## Players' reports after 1.0.4 (2026-11-12) - tests\bug105_*.txt; test switches 'fix cactus|ladder|ranged|shrineboth|float|spawnside|controls on|off', 'nospawns on|off'
- Spawns "3:1, P1 gets 2-3, P2 a horde": REPRODUCED (enemies appeared near P1 32 / P2 96; at 45 s P1 1 vs P2 12
  around). Cause: GetEnemySpawnPositionBiased calls GetEnemySpawnPosition inside it and both were hooked - two coin
  flips, 3 of 4 enemies around P2. FIXED (P2Spawns: only the outermost call decides; one side per SpawnEnemy): 54/72,
  5 vs 5 around each player at 30 s.
- Charge shrine finished by both -> P1 (even with P2 in first): REPRODUCED (both 2.1 m from it, only one reward).
  User's wish: both get it. DONE (P2Rewards.ChargeShrineOwner: both inside the zone radius -> the game's reward is
  P1's, a second of the same kind is queued for P2 next frame; windows one after the other, each player controls
  theirs). Not done: both windows at the same time (the game shows one reward window at a time; the stats panel
  idea belongs to that - a bigger UI change, asked the user).
- Desert cactus hurt P1 when P2 touched it: REPRODUCED (P1 1000000 -> 999999, P2 untouched). DamagePlayerCollider.
  OnCollisionEnter hurts "the player". FIXED (P2World: P2's touch runs the cactus' hit as P2's, own refresh time).
- Ranged enemies aimed at P1: REPRODUCED (cactus spit landed 1.8 m from P1 while 285 m from P2; Bandit slices that
  hit P2's collider took P1's HP: P1 -926 at 300 m from every enemy). FIXED: every EnemySpecialAttackPrefab kind's
  Init and attack coroutine steps run with P2 as the player for an enemy chasing P2 (spit lands on P2: 0.0 m);
  cactus spit / Bandit slices / sniper shots go through P2BossAttacks' SpawnHitEffect hit (P2's collider off for the
  game's hit, P2 hit by distance - to the slice line for hitscan kinds). After: P1 never hit in 4 phases.
  SkeletonMage: no special attack seen.
- Birdo / Roberto float not working for P2 - FIXED, user confirmed on real pads. What the float is (measured on P1
  with the real jump button held - autotest 'p1jumpheld'): once falling (PlayerMovement.CanFloat), the fall stays at
  2.6 m/s while jump is held; no lift. The game does it in P1's PlayerInput physics step (the only per-physics-step
  MyInputManager.GetButton(Jump) - [p2input] count), a script P2's copy doesn't have. P2Movement.Float() does the
  same for P2 (Float passive, A held, CanFloat in P2's context). Pitfall: the test's own P1 input sends "jumping"
  every tick while a jump is held -> fake lifts (20 m); an early "fix" copied that lift for P2 - removed.
- Roberto's levitation (same report): his passive is Hoarder, so not the Float rule. Measured on P1 with the real jump
  button held (tests\bug105_roberto.txt): rising 18.5 m/s right after the jump, +2.4 m/s per second held, no limit in
  6 s (156 m up), no gold used, normal fall once let go. Where: P1's PlayerInventory.PhysicsTick (MyPlayer.FixedUpdate
  -> it asks IsHoldingJump / CanFloat every physics step - [p2input] probe); P2 has no PlayerInventory, so neither
  Birdo's nor Roberto's ran for P2. FIXED: P2Movement.RobertoLift (P2's character is Roberto, A held, in the air):
  P2 18 -> 24.7 m/s in 3 s, 65 m (P1 68 m); start nudged to 19.5 to close the 1 m/s gap.
  User (real pads): P1-Roberto has a limit and can't rise again after letting go; P2 had neither. Measured on P1
  (14 s hold): lift ~11.2 s (~360 m), then a 12.6 m/s fall while held (normal fall if not), holding again in the air =
  only the slow fall, full lift again after landing. P2 now the same (test: 21/43/156 m at 1/2/6 s like P1; top 341 m
  vs P1 ~368 - P2's lift ends ~1 s early, vy 0.0 at 11 s, cause not found; held fall 13.2 vs 12.6; rest identical).
- User: P2 took fall damage after flying up, falling, and slowing down before the ground. P2's fall damage
  (P2Movement.LandingDamage, the mod's own) used the fastest fall speed of the whole airtime. FIXED: the speed of the
  last tick in the air (tests\bug105_falldamage.txt: fell at 50.6, slowed, landed 18.9 -> no damage; a straight fall
  at 130.8 -> the game's fall-damage rule).
- Graveyard ladders, P2 can't climb - FIXED, user confirmed. Ladder.OnTriggerStay run with P2 as the player never set
  P2's ladder state; P2World calls the game's PlayerMovement.RefreshLadder(ladder) on P2's copy (onLadder True), and
  P2's movement code gets P2's held jump (P1's PlayerInput.holdingJump / IsHoldingJump / MyInputManager.GetButton(Jump)
  answer with P2's A while P2's movement code runs - P2Movement.EnterP2Input). Autotests can't climb (the test's input
  doesn't hold like a pad; 'makeladder' test ladders aren't real ladders).
- Catacombs jump pad launched P1, not P2 - FIXED, user confirmed: P2 touching a JumpPad -> the game's
  PlayerMovement.JumpPad(pad) on P2's copy (P2 launched 65 m/s up; the pad's own trigger code did nothing for P2).
- P2 took no lava damage - FIXED (test: fix off no damage, on P2 999936 -> 942434, P1 untouched): Lava / damaging
  Water steps run once more with P2 as the player on P2's borrowed health, per-player timers/flags kept apart.
- P1 used the catacombs leave door and landed in a void (user's own run, both Birdo, natural spawns off): NOT
  reproduced - tests\bug_graveyard_b.txt twice: P1 on the surface at y 1.85, P2 2 m away, normal screenshot. [door]
  (and [world], [shrine], [float]) lines are now kept in players' logs too (ModLog.KeptTags).
- Character select after the first run, P2's cursor back on Confirm: NOT reproduced (test P2 without a pad moved
  freely after the death screen's Menu; P2Select now runs on the test's presses without a pad). Likely real-pad cause
  found: a run ending while a P2 window (chest, reward, level-up) had the game's controls never ran its close hook -
  P2's pad stayed on the game's player through the menus (driving the game's own selection, P1's Confirm). FIXED as a
  safety net: outside a run the controls go back to P1 (P2Select.ReleaseStaleControls), P2Interact.Reset clears a
  P2 chest window's state.
- Gold minus / "irrational": NOT reproduced. Safety net: P2Gold checks every swapped amount (negative / NaN /
  infinite -> logged with a stack trace, set to 0).
- Regression: tests\items_deadlisteners.txt passes (items in all 3 runs, only the game's own 2 leftovers).

## Players' reports after 1.0.3 (2026-11-09) - TEMP commands in AutotestRestart.cs (eventcheck, srccount, invcheck)
- "After restarting once, Cheese / Cursed Doll / Soul Harvester / Slutty Cannon stop working with two players":
  death-screen restart (tests\items_restart.txt) and the pause menu's RestartRun (items_restart_midrun.txt): NOT
  reproduced. Quit to the MAIN MENU + new run (tests\items_mainmenu.txt, 'gomain' = the game's TransitionUI.LoadMenu):
  REPRODUCED - run 2 on-hit item lists 0 for both players, Cheese / Cannon / Doll no damage. Cause: ItemEvents had
  P1's ToxicBarrelEffect / HolyBookEffect listeners of ItemBase.A_ItemAdded in its own list when the run's scene went;
  their OnDestroy (-= Refresh) found nothing, and ItemEvents.Reset (at the next run start) put them back with their
  objects destroyed -> every AddItem threw in ToxicBarrelEffect.Refresh (NullReference, LogOutput.log) before the
  item was set up. FIXED: Reset only puts back listeners whose object exists (items: of P1's current inventory) -
  "reset: 5 P1 listener(s) ... not put back", run 2 on-hit 2/2 and all four items deal damage for both.
  Every item was affected (the throw was in AddItem itself), not only these four.
- Leftovers between runs - 'deadlisteners' scans all ~150 static game events (tests\items_deadlisteners.txt: 30
  listener items on both players, run 1 -> main menu -> run 2 -> death-screen restart -> run 3). FIXED:
  - P2's item holder was never cleaned up (kept ticking "on other"): P2Inventory.ResetForNewRun runs the game's
    ItemInventory.Cleanup on it.
  - MyPlayer.Instance points at the destroyed player between runs and C#'s ?. doesn't see it: P2Items.P1Items now
    uses Unity's check (the old run's items passed for P1's current ones).
  - The game's own Cleanup of Demonic Blood / Demonic Soul / Joe's Dagger subscribes them once more (A_EnemyDied
    10 -> 12 around P2's Cleanup): ItemEvents.Reset takes items of no current inventory and destroyed objects off
    the routed events. After: no item leftovers on any game event on either route, AddItem errors 0.
  - Left alone (the game's own, the mod doesn't touch them): destroyed DeathScreen.OnBossDefeated on
    InteractableBossSpawner.A_BossDefeated and FullMap.OnMapToggle on FullMapUi.A_Toggle, +2 per run.
  - Unexplained, probably a test artifact: P2's Holy Book did ~3000 damage in run 2 (after the main menu) vs ~30 in
    runs 1 and 3, all three times (Holy Book scales with healing; the test's god mode sets HP to 1,000,000).
- User: P1 shows the game's item popup (icon + amount over the player) when an item is added, P2 didn't. The muted
  ItemInventory.A_ItemAdded skipped EffectManager.OnItemAdded for P2. FIXED: P2Items.AfterChange plays it for P2's
  AddItem and moves the new 'ChestItem(Clone)' to the same spot in front of P2's camera (tests\item_pickup_fx.txt).
- P2 can't take damage in Forest stage 2: still not reproduced, waiting for players' details.
- User: after going back to the main menu, P2's skin row only had one 50-gold skin. Character select opened after a run
  fills P1's skin row a moment after the mod copies the panel ("P1's skin row has no containers"), so P2's row was
  never bound and kept the copied leftover. FIXED (P2Select.BindSkinsLate: bound once P1's row is filled, a
  script-free copy of P1's container as the template). tests\skins_mainmenu.txt: "bound late (after 1 tries)", P2's
  row shows P2's usable skins. Seen in passing, not changed: P2's info panel shows rank / runs text as copied
  (Rank 5 / 200 runs next to P1's Rank 2 / 32 runs) - BindInfo doesn't update those two texts.
- Player: the shared screen doesn't split soon enough. SplitDistance 60 -> 30, MergeDistance 48 -> 24 (40/32 first,
  user: a little more; P2Camera - configs holding an old default 60/48, 40/32 or 15/12 are moved to the new ones;
  user: no split/merge spam - 2 s cooldown after a switch, P2Camera.SwitchCooldown; a player going down still
  switches at once, a player's own value stays). In 1.0.4.
- Known, harmless: PlayerMovement.Update NRE spam (~150 at the main menu per visit).

## Players' reports after 1.0.2 (2026-11-03) - tests\bug_*.txt, new commands in AutotestBugs.cs
- Chest mash: REPRODUCED (8 presses -> 7 free P1 windows), FIXED (P2Interact: no presses while MyTime.paused, guard on
  opening/same chest). Jump after level-up pick: REPRODUCED (5.8 m), FIXED (P2Movement.FollowA/WaitForJumpRelease).
- Shield power-up hits P1 + yeets P1 ("sent into the sky"): REPRODUCED (P1 flung to 83 m), FIXED - the game's
  ShieldPickupEffect taken over for P2 had a collider enemies treat as P1 (P2PowerUps.TakeVisual removes colliders).
- Graveyard crypt (run starts ~2 km up; leave door moves P1 only): REPRODUCED (P2 left falling/reset forever), FIXED
  (DoorTeleports: P2 follows P1's jump(s) for 2 s, downed player revived). Verified: P2 down + P1 door, P1 down + P2
  door -> both 2 m apart. Nobody down, P1 uses the door (tests\bug_graveyard_b.txt, 2 runs, one with the game's
  two-step move): P2 2 m from P1 on the map both times.
- Stage 2 negative gold / items to one person: NOT reproduced (gold and owners correct on stage 2). Safeguard: owner
  lists resync with the game's rewardQueue when a window opens (P2Interact.Resync, P2LevelUp, P2Rewards).
- Dexecutioner: NOT reproduced (forced 50%: P1 7/15, P2 8/14 executions). User's idea "a visual effect only P1 gets":
  checked too - tests\bug_dexecutioner_fx.txt (chance forced to 100%, [execfx]): the game's 'ExecuteEffect' appears
  3 m from the executed enemy for P2's executions exactly like P1's; the user watched the run and saw it play for P2.
  Ask players what "executioner" meant / what looked wrong.
- Time freeze (clock) and P2 touching enemies: REPRODUCED (tests\bug_timefreeze.txt, fix off: frozen skeletons took P2
  100 -> 0 in 4 s, P1 untouched), FIXED - P2's contact check (P2Health.CheckContacts, the mod's own) now skips while
  GameManager.IsTimeFreeze(); tests\bug_timefreeze_on.txt: P2 100/100 through the freeze, after it the same ring kills
  P2 in 3 s (damage back). Side note: the autotest 'revive p2' left P2 at 0 HP once (P1 then died to natural spawns) -
  not looked into.
- Negative gold (user's play-test, P2 Robinette): REPRODUCED (tests\bug_gold_negative.txt, both players interact with
  their chest in the same frame: P1 40 -> -62). Cause: the game pays a chest in InteractableChest.OnChestWindowClose,
  which every chest on the map runs when ANY chest window closes; the opening chests pay then, from the gold in the
  game - P2's chest was paid by P1 at P1's window close, at P1's chest price. FIXED (P2Interact.ApplyPayFix): P2's
  chests (by pointer) pay with P2's gold + chest price swapped in, P1's chests with P1's (P2Gold/P2ChestPrice.BeginP1)
  when a P2 window is closing. Fix on: P1 paid its own 115, P2 its own 30, nobody twice. tests\bug_gold_single.txt:
  single purchases still charged right. PITFALL: a second, separate Harmony patch on OnChestWindowClose plus a probe
  on InteractableChest.FixedUpdate stopped the game charging ANY chest (free chests) - one patch call only.
- P2 didn't uncover the map's fog: REPRODUCED (tests\bug_fog_p2.txt, FullMap.visitedCords: P2 walked 80 m -> 0 cells,
  P1 110 m -> 13). FullMap.QueueRevealFog(P2's spot) (+ RevealFog) did nothing - the game reveals around "the player" in
  its FixedUpdate. FIXED (MapView): every 0.5 s FullMap.FixedUpdate + RevealFog run with P1's object and
  PlayerMovement.Instance at P2's spot (P2Items.AtP2Spot). Now P2 110 m -> 13 cells, P1 70 m -> 7; P1 not moved.
- TEMP switches: 'fix chestmash|jumprelease|doors|shield|timefreeze|chestpay off', [queue] / [chestpay] logs.
- RELEASE 1.0.3-alpha ("SplitBonk V1.0.3 Alpha"): the fixes above; dist\SplitBonk-V1.0.3-Alpha.zip by make_release.ps1
  (231 files, 0 backslash names, console-off cfg, DLL = the build = installed); 1.0.2 zip moved to dist\old.

## After the 1.0 alpha release: players' reports (2026-11-01)
Reports (characters unknown): (1) P2 fires at P1's target / in P1's direction; (2) knockback reversed - enemies
knocked into P2; (3) P2 can't grab pick-ups or XP; (4) P2 can't take the heart power-up; (5) at tier 2, P2 took no
damage from mobs. User: test every character and every projectile weapon; players far apart (~300 m) in tests.
- New autotest commands: `spawnside <p> <EEnemy> <n> <dist>` (arc behind a player, away from the other), `knock`
  (knockback directions near P2), `xpstate`, `unweapon <p> all`, `home <metres>` (open flat ground that far away),
  `godp1/godp2 off`, `health <label>`. Tests: aim_sides1/2 (30 weapons), aim_fix4, pickups_real, tier2_damage,
  charcheck_<P1>_<P2> (11 pairs = all 21 characters).
- (1) AIM, reproduced and FIXED: sweep with P1 unarmed, ~350 m apart, ring of 6 skeletons around P2, weapons x4 levels:
  26 of 30 weapons clean (0 damage at P1). Bugs: Hero Sword (38 dmg on P1's group) and Corrupt Sword (39) - their
  hitbox lives in their own fields (hitboxPos, movingProjectilePosition) worked out from "the player" at the start,
  so moving the slash object didn't move it; Space Noodle (50 dmg, 1 kill at P1) - LaserBeamAttack builds its damage
  box (center/rotation/halfExtents) in FixedUpdate from "the player", only GetBeamStart was moved; Bluetooth Dagger
  (0 damage anywhere at 370 m; fine at 30 m) - moves/hits relative to "the player". Fix (P2Weapons.ZoneHook): those
  methods run with P2 as the player (the shotgun/axe/banana pattern). aim_fix4: P1's group 0 for all four, both
  players armed still both deal damage. Guard: a zone is only added to a hook on the same family (one native
  function was shared with the empty PlayerMovement.Update).
- (2) KNOCKBACK, reproduced and FIXED (Knockback.cs): the game pushes a hit enemy away from "the player" (P1). With
  enemies around P2 (not only behind it), 2-6 pushes per weapon pointed into P2 (Sword, Axe, Tornado, Dexecutioner,
  Mine, Dice, Katana, Scythe...). Prefix on EnemyMovementRb.Knockback(DamageContainer / Vector3, float): an enemy
  nearer P2, P2's hit (P2Items.CurrentHitter, or no projectile), push lined up with "away from P1" -> turned to point
  away from P2. Sweep: "towards P2 after the mod" 0 for every weapon. Not changed: the returning banana pushes
  along its flight (also P1's behaviour); Space Noodle 3 pushes into P2 remain (laser, along the beam?) - open.
- (3) PICKUPS/XP, two causes found and FIXED (P2Pickups):
  a) P2's radius = P1's sphere measured once when P2 spawned x P2's / P1's CURRENT PickupRange (PickupRange is the
     radius in metres: Fox 9, Calcium 8). P1 + 5 Attraction tomes = 42.75 -> P2's radius would be 1.9 m. Now P1's
     sphere is read live (pickup_range: P2 still pulls a gem 6 m away after P1's tomes).
  b) players together: P1's sphere is a trigger (instant), P2's pull runs every 0.1 s - pickup_close baseline (4 m
     apart, 6 gems in a 3 m ring around P2): P1 got all 6, P2 0. Now a gem the game sends to P1 that is nearer P2 and
     inside P2's radius goes to P2 (not during P1's pull-all: magnet/shrine gems stay P1's - user's rule).
  Far apart, P2's own kills and walked-over gems were fine in every charcheck so far.
- (4) HEART, reproduced and FIXED: the game's Pickup.CanPickup for the heart asks "is THE player (P1) hurt?" - with P1
  at full HP P2 never got a heart (charcheck Megachad/Ogre: P2 102/140 walked over it, nothing; Fox/Calcium and
  SirOofie/Cl4nk healed because P1 was 1 HP short). P2CanTake: the heart asks about P2's HP, other pickups keep the
  game's check. VERIFIED heart_a: P1 120/120, P2 102/140 walks to a heart 7 m away (Ogre's PickupRange is only 5) -
  it flies to P2, P2 140/140. (Pitfall: 'nopowerups' in a script switches dropped hearts off after 0.5 s - the first
  heart_p1full run looked like a failure because of it; heart tests must not use it.)
- (5) TIER 2 DAMAGE: not reproduced. tier2_damage2 (P2 test health, 4 skeletons 3 m around P2): stage 1 -299 HP in
  6 s, stage 2 (after P1 took the exit portal) -985 in 6 s, -1217 in the next 6 s; tier2_damage without test health:
  P2 died on both stages. tier2_p2portal_damage (P2 levels up, takes the portal itself): stage 2 -1101 in 6 s.
  tier2_hud (normal HP): 2 skeletons kill P2 in 3 s on stage 1 and on stage 2. Still not reproduced after 4 routes -
  needs details from the players (character, P2 down before the portal?, merged/split view, what "no damage" looked like).
- Character sweep DONE (charcheck_*, all 21 characters, both armed, ~300 m apart): XP from own kills and walked-over
  gems, heart heals (with P1 hurt) for every character; aim/knockback clean. Low P2/P1 damage ratios (Vlad 0.47, Bush
  0.48) were the starting weapons: Vlad vs Vlad 1.76, Bush vs Bush 1.00. Tony McZoom's Bluetooth Dagger pushes along
  its flight (any direction for both players) - game behaviour, not P1-relative.
- Regression: aim_sides2 after the fixes - P1's group 0 for all 15 weapons, "towards P2 after the mod" 0 everywhere
  (Space Noodle 27 -> 72 on P2's ring, Bluetooth Dagger 0 -> 49).
- Co-op: coop_still (6 m apart, still, 90 s, the game's spawns): kills P1 37 / P2 46, XP 11 / 18 - balanced.
  coop_together (walking a route) skewed (P1 72 kills / P2 20) because the route ran into the map-edge walls - redo
  on open ground if needed.
- BANANA (user's play-test, P2 Megachad with a picked-up Bananarang: "doesn't fly at the nearest enemy"), FIXED:
  P2's banana is set up at P1's spot; its TryInit works out startDirection from there (towards a target near P2 - at
  300 m apart that's the P1 -> P2 line: field probe start (-0.71, 0, -0.70) = P1->P2 exactly); the re-aim afterwards
  didn't stick and the ring sweeps couldn't see it (a ring is hit whichever way it flies). Fix: P2's banana is moved
  to P2 right before its TryInit (MoveBananaBeforeInit, Priority.First), then TryInit / FindMovementDirection run with
  P2 as the player (zone). Zone on TryInit alone made some throws fail (banana at P1, "player" at P2). Probe
  (banana_aim / banana_megachad, flight in the first 0.3 s vs the nearest enemy): fix off 56-141 deg mean, 0-50% within
  30 deg; fix on 1-37 deg, 67-100%. banana_both: P1's bananas still hit P1's ring. TEMP: 'bananafix off|on' switch,
  'bananaprobe', [bananafields] (autotest only).
- User's play-test log (P2 Megachad, real pads): P2 reached level 11 - XP works in play. Warnings only the known ones.
- User committed the fixes (22a6c37). Then: version 1.0.1-alpha, "SplitBonk V1.0.1 Alpha"; dist\SplitBonk-V1.0.1-Alpha.zip
  made by make_release.ps1 (231 files, 0 backslash names, BepInEx/config/BepInEx.cfg console off, DLL = the build;
  zip SHA256 D5C604A2...CF34509); the 1.0 zip moved to dist\old. Same DLL installed in the game.
- FEATURE (player request: "1 person on mouse and keyboard, the other on the controller"; user: a toggle on the players
  screen): P1 input CONTROLLER / KEYBOARD + MOUSE, a button under the PLAYER 1 card (Lobby.cs), saved as
  [Players] Player1KeyboardMouse in thesplitscreenguy.megabonksplitscreen.cfg. Keyboard + mouse: P1 = keyboard (p1Id -1,
  no pad is P1's), every controller is taken off Rewired player 0 (also ones plugged in later - checked when the pad
  count changes, not while P2's level-up/pause menu has the controls), so any pad's Start joins as P2 - one controller
  is enough; P2 leaving keeps the pad free. Back to CONTROLLER: the pads (not P2's) go back to player 0, P1 = the next
  pad to press. Why it was needed (user's log): P1 was always a pad - clicking Play with the mouse left P1 "not known",
  and the only pad then became P1. Autotests ignore the setting (keyboard/mouse are off in autotests). Layout checked
  (autotest_lobby.png). First user test: the keyboard did nothing - the game's player had hasKeyboard AND hasMouse
  false (log before P2's level-up swap: "keyboard False, mouse False"); the game switches them off while it thinks a
  controller is in use. Fix: in keyboard + mouse mode PlayerJoin.KeepPadsOffP1 keeps them on every frame (not while
  P2's menu has the controls). User: "excellent" - works.
- RELEASE 1.0.2-alpha ("SplitBonk V1.0.2 Alpha"): the P1 keyboard + mouse toggle; dist\SplitBonk-V1.0.2-Alpha.zip by
  make_release.ps1 (231 files, 0 backslash names, console-off cfg, DLL = the build); 1.0.1 zip moved to dist\old.
- BepInEx console came back on the user's PC (BepInEx.cfg [Logging.Console] Enabled = true - BepInEx's default when it
  makes a new config). The 1.0 alpha zip shipped NO BepInEx.cfg, so every player started with the console on (and
  Steam Input could then hide P2's pad). Fixed for the next release: release\BepInEx.cfg (only the console setting,
  off) goes into the zip as BepInEx\config\BepInEx.cfg; make_release.ps1 refuses to finish without it; both READMEs say
  how to switch it off by hand. User's own BepInEx.cfg set back to off (user approved).

## Current step
- [x] Locate game folder, check backend (Mono or IL2CPP) and Unity version
- [x] .NET 8 SDK 8.0.426 installed by user (2026-10-17)
- [x] BepInEx 6.0.0-be.788+5b766a3 (Unity.IL2CPP-win-x64) installed 2026-10-17. Zip SHA256
      F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A, 227 files hash-verified.
      Added to game folder: winhttp.dll, doorstop_config.ini, .doorstop_version, BepInEx\, dotnet\
      (uninstall = delete those five).
- [x] First launch OK (2026-10-17): Unity base libs downloaded, Cpp2IL read metadata v29.1, interop
      generated into BepInEx\interop (108 files), chainloader started, 0 plugins. IL unstrip: 12251 ok / 1709 failed.
- [x] `tools/TypeDump` (in-box System.Reflection.Metadata, no NuGet) dumps classes/fields/methods:
      `notes/types/Assembly-CSharp.txt` (1221 types). Rerun after game updates.
- [x] User chose: block leaderboards, Steam stats AND achievements while the mod is installed.
- [x] Plugin v0.1.0 tested 2026-10-17 (short run, death, menu, quit): all 19 patches applied, no errors.
      Blocked: SteamStatsManager.QueueUpload, SetCachedStats; SteamAchievementsManager.TryUnlockAchievement
      (100+ calls right at startup = game re-checks every achievement on launch); Leaderboards.UploadScore
      on death. Zero hits on the SteamUserStats layer = nothing got past the game-level blocks.
- [ ] Player 2 join on character select (user's plan, 2026-10-17): main menu -> P1 clicks Play ->
      character select -> P2 presses Start -> (later) camera pulls back, P2 avatar spawns to P1's right
      (screen right), second character select panel on the right half; character info panel may need moving.
  - v0.2.0 test: CharacterMenu open/close hooks fire. Rewired has **1 player, 2 joysticks**: #0 'Steam Controller'
    (P1 at open), #1 'Xbox 360 Controller', both owned by Rewired player 0. NO presses were detected via
    `joy.buttons[i].justPressed`, so P2 never registered; the game itself switched to pad #2 (single Rewired
    player = last-pressed pad wins). Ruled out: reading `Controller.buttons` justPressed.
  - v0.2.1 test: still zero presses logged on any pad (PollForFirstButtonDown also never succeeded).
    Both pads expose 14 generic buttons "Button 0".."Button 13" (no 'Start' name). Pad 2 Start did not
    navigate menus, but that was not the mod (P2 never registered; Start is likely unbound in menus).
    Ruled out: PollForFirstButtonDown. Unverified: whether Ticker.Update runs at all.
  - v0.2.2 test (P2 spammed Start): Ticker runs (heartbeat OK). **Unity legacy Input sees Joystick1Button7**
    for each Start press, but Rewired `joy.GetButton(i)` stayed false for every button on both pads.
    So P2's pad reaches Unity; our per-joystick Rewired reads are empty. Suspect Steam Input (pad #0 is
    'Steam Controller'). Ruled out: Ticker not running; Rewired *Down timing (GetButton held also false).
  - User hardware: P1 = real Valve **Steam Controller** (Rewired pad #0), P2 = **Xbox 360 pad** (Rewired pad #1,
    Unity legacy Joystick1). Steam Input is ON for Megabonk. The Steam Controller only works through
    Steam Input, so turning Steam Input off is not an option for P1.
  - v0.2.3 test: Rewired reads DO work: `joy.GetButton` edge detection caught Button 0 held on pad #0 (Steam
    Controller) as character select opened, and Unity saw Joystick1Button0 at the same moment (so Unity's
    Joystick1 may be the Steam Controller, not the Xbox pad; the earlier Joystick1Button7 is ambiguous).
    Then **`Player.controllers.GetLastActiveController()` threw "Invalid call" via interop** and the Ticker
    disabled itself, so the rest of the test went unlogged. Second open of character select: game's
    currentController = Xbox pad #1, so the game does get Xbox input through Rewired.
    Avoid: Player+ControllerHelper.GetLastActiveController (use MyInputManager.currentController).
  - v0.2.4 test INVALID (my test steps): pressing A twice on P1 confirmed the character and left
    character select after 1-2 s (CharacterMenu.OnDisable), so the Xbox presses came after we stopped
    watching. Note: during that window Rewired player 0 GetAnyButton was true while per-pad reads were not.
    Test rule: on character select, P1 must not press A; only use P2's pad.
  - [x] v0.2.5 test 2026-10-17: **PLAYER 2 JOINED**. `ReInput.controllers.polling.PollControllerForFirstButton
    (Joystick, id)` saw button 7 'Start' on the Xbox pad (#1); joy.GetButton never saw any Xbox press.
    Polling also gives real names ('A', 'Start'). Pad #1 removed from Rewired player 0 -> game ignores it.
    Ruled out for reading pads: Controller.buttons[].justPressed, Controller.PollForFirstButtonDown,
    joy.GetButton(i) (missed the Xbox pad). **Use ReInput.controllers.polling for P2 input.**
  - [x] v0.3.0 (installed, SHA256 ED8B4F15...26DF4): TEMP diagnostics removed (heartbeat, Unity legacy
    check, method A, activity logs, button-name dump, InputLegacyModule ref). Join = polling only, logs
    non-P1 presses while watching. Not separately retested (cleanup only).
  - Known: P2 stays joined for the whole session; P2's pad is now invisible to the game, so P2's own
    character-select UI will have to be driven by the mod (polling) later.
  - v0.4.0 test: **no press detected at all** (no button lines, no join), so avatar/camera code never ran.
    Join detection regressed in the v0.3.0 cleanup (v0.3.0 was never tested on its own). Theory: a removed
    per-frame read (joy.GetButton/GetAxis/GetAnyButton, player0.GetAnyButton, or Unity legacy
    Input.GetKeyDown on joystick keys) mattered for polling. Alt theory: single press missed (v0.2.5 user
    pressed 3x; maybe not the first that was caught).
  - [x] v0.4.1 test 2026-10-17: **works with F9/F10 never pressed.** Join on the first Start press (3 s after
    open); P2 avatar spawned at P1 + 5.05 right (avatar width 3.16 x 1.6); camera marker
    (241.40, 20.96, 31.46) -> (243.93, 21.43, 25.43), fov 49; user: "worked perfectly".
    Bug found: the viewport log showed both avatars at x=0.61 because FindObjectOfType<CharacterDisplay>
    returned P2's clone (it has a CharacterDisplay). Fixed in v0.4.2 (FindP1Display skips the clone);
    this also prevents cloning the clone when character select is re-entered.
    **Open question:** why v0.4.0 missed the press. Differences vs v0.4.1: (1) v0.4.1 also polls P1's pad
    (v0.4.0 skipped P1 before polling), (2) v0.4.1 calls Input.GetKeyDown(F9/F10) every frame,
    (3) the v0.4.0 test used a single Start press. Input code left exactly as in v0.4.1 until bisected;
    TEMP F9/F10 diagnostics still in.
  - [ ] v0.4.2 (SHA256 E830CBBA...FE4F99): v0.4.1 + FindP1Display fix. Not separately tested.
  - [ ] v0.4.3 (installed, SHA256 3568F79B...11B4B4): + TEMP `UiDump.cs`, writes
    `BepInEx\plugins\MegabonkSplitscreen\ui_dump.txt` on first character-select open (parents of CharacterMenu,
    its subtree with RectTransform anchors/pos/size and component names, CharacterInfoUI if outside,
    SelectionArrow). Purpose: lay out the P2 panel.
  - ui_dump findings (1920x1080 overlay canvas 'UI', scale 1): W_Character (CharacterMenu) 646x868 centred at
    pos(-575,0) in 'Character' (has HorizontalLayoutGroup) -> screen x 62..708. **W_Stats (1) (CharacterInfoUI)
    is a CHILD of W_Character**, 570x750 at local (1175,0) -> screen x 1275..1845. Grid = ScrollRect >
    CharacterGrid (GridLayoutGroup) of CharacterPrefabUI (MyButtonCharacter) 135x135. Info has B_Confirm in
    its Footer, Skins (SkinSelection), Weapon/Passive rows. SelectionArrowNEW = full-screen overlay cursor.
    Screenshot with P2 joined: P1 avatar ~x815, P2 ~x1135, heads ~y505 from top, feet ~y750.
  - User's layout (2026-10-17): P1 grid at 75%, P1 info panel at 50% next to the grid and above P1's avatar;
    P2 gets the same on the right (mirrored).
  - v0.5.0 test: **layout correct** (screenshot matches the user's design). Two issues:
    (a) SelectionArrow highlight drawn at unscaled size (corners land on neighbouring buttons).
    (b) Join flaky: on the 1st visit (30 s, user pressed Start several times) polling saw NOTHING on any pad
        (not even P1's held A at open, which successful visits always log). 2nd visit worked instantly.
        So polling is dead for a whole visit, intermittently; not the Xbox pad. Likely the same as the v0.4.0 failure.
        Single-missed-press theory is out.
  - v0.5.1 test: 5 character-select visits, all "dead": both pads `poll=-1 any=0` the whole time while the
    game's Rewired player 0 GetAnyButton toggled with every press (incl. P2's Start spam); game
    currentController stayed #0. So presses reach the game's player but not via either Joystick object.
    Highlight log used up on main-menu buttons (ratio 1.00), so no data for the grid.
    Theory: Steam Input desktop layout (window unfocused, e.g. after alt-tab for screenshots) -> pads emit
    keyboard/mouse. Unverified.
  - v0.5.2 test: joined after a lot of spamming. Window WAS focused (focused=1 throughout), so the
    "unfocused" part of the theory is out. But Steam Input clearly remaps: P1's Steam Controller presses showed
    as `kb=1 lastActive=Keyboard#0`, trackpad as `Mouse#0`; during P2's Start spam Rewired's global
    lastActive said `Joystick#0` (Steam Controller) while #0/#1 per-pad reads stayed 0; then polling suddenly
    saw Start on #1 and P2 joined. Conclusion: Steam Input's virtual devices make per-joystick reads
    unreliable/misattributed. Highlight fix CONFIRMED in log: character buttons ratio 0.75 (165 -> 123.75),
    B_Confirm 0.5.
    User-found bug: P2 avatar re-textured when P1 changed character (model stayed orc). Causes: clone kept
    CharacterDisplay (listens to static selection events) and shared P1's runtime materials.
    Next experiment (user's system setting): Steam > Settings > Controller > turn OFF Steam Input for Xbox
    controllers; keep it for the Steam Controller. If join becomes reliable -> document in README.
    Fallback if not: read P2 directly via XInput (P/Invoke xinput1_4.dll).
  - Test with Steam Input OFF for Xbox (2026-10-17): P2 never joined. Every press, P1's AND P2's Start spam,
    showed as `kb=1 lastActive=Keyboard#0` (some Mouse#0); both pads' per-pad reads 0 the whole time. Near the
    end joystick #1 vanished from the [state] list (pad re-enumerated?).
    **New theory: Steam is applying its Desktop Configuration** (controller -> keyboard/mouse emulation: A=Enter,
    trackpad=mouse, etc.) instead of the game configuration, intermittently. The game still works because it
    accepts keyboard input, but the gamepads look idle to Rewired. Good visits = Steam had the game config active.
    Candidate trigger: BepInEx console window (BepInEx.cfg [Logging.Console] Enabled = true) â€” Steam may not
    treat the game as foreground. Unverified. Next: user turns Xbox Steam Input back ON; with permission,
    set Logging.Console Enabled = false; retest several visits.
  - 2026-10-17: user turned Xbox Steam Input back ON; BepInEx.cfg [Logging.Console] Enabled = false (user approved).
    Logs still go to LogOutput.log and our MegabonkSplitscreen.log.
  - [x] Retest: **ROOT CAUSE FOUND.** With the console off, P2 joined 0.8 s after opening on the first press;
    [state] shows real joystick input (`#1 poll=0 any=1`, lastActive=Joystick, kb=0). User: "worked perfectly".
    The BepInEx console window made Steam apply its Desktop Configuration (pads -> keyboard/mouse), so
    per-pad Rewired reads were idle. The v0.3.0 "regression" was this too, not the cleanup.
    Note: Rewired joystick ids swapped this launch (#1 = Steam Controller, #0 = Xbox, with hardwareName
    'Steam Controller' via Steam Input) â€” the code never hard-codes ids, keep it that way.
    Note: avatar width depends on P1's character at join (4.32 this time -> offset 6.90); spacing varies.
    v0.5.3 avatar detach confirmed in log (7 renderers).
  - User decision 2026-10-17: **keep TEMP diagnostics (F9/F10, [state], UiDump, highlight log) until
    everything is running well**; clean up at the end, then retest.
  - P2 panel plan: (1) inert mirrored copy of P1's window titled PLAYER 2; (2) P2 cursor via P2 pad polling,
    A picks -> P2 avatar + info update; (3) confirm/ready for both players.
  - v0.6.0 test: P2 panel renders on the right with "PLAYER 2" title and grid in place, but its info panel stayed
    at P1's (unmirrored) offset, half off-screen. Log: `VerificationException: Il2CppObjectBase.Cast: type argument
    'T' violates the constraint` thrown inside Layout at `menu.GetComponentInChildren<CharacterInfoUI>(true)`.
    Avoid generic GetComponentInChildren<T> on game types; FindObjectOfType<T> works.
  - [x] v0.6.1 tested 2026-10-17: **character select layout confirmed "perfect"** by user (screenshot): P1 grid
    left + info right of it; P2 info left of P2 grid + "PLAYER 2" grid right; avatars between. Panel step 1 done.
  - [ ] Next (ON HOLD per user, 2026-10-17): P2 panel step 2 = P2 cursor on the P2 grid (Xbox pad via polling),
    A picks -> P2 avatar + P2 info. Research so far (no code written): CharacterData has icon, weapon
    (WeaponData: icon, damageSourceName), passive (PassiveData), prefab, meshDefault; SkinData (icon, materials);
    PlayerRenderer.SetCharacter(CharacterData, PlayerInventory, Vector3 spawnDir), SetSkin(SkinData),
    SetHat(HatData). P1 grid children order = copy's order -> CharacterData via P1 child's MyButtonCharacter
    (GetComponent<T> works; avoid GetComponentInChildren<T>(bool)). MyButtonCharacter.canUseCharacter for locks.
    Idea for P2 highlight: copy's per-button 'CharacterSelectedOverlay' + scale/border tint. Stick/dpad via
    ReInput.controllers.polling.PollControllerForFirstAxis / button names.
  - [ ] v0.7.0 (installed 2026-10-17, SHA256 14F10551...B90E80): P2 panel step 2, `P2Select.cs`. Copy buttons
    matched to P1's live `menu.characterButtons` by sibling-index path (names like Header/Content repeat).
    P2 pad polling: d-pad button names ("...pad... up/down/left/right") or PollControllerForFirstAxis
    "left stick x/y" + pole -> cursor (copy's hoverOverlay on, scale 1.1), nearest button in that direction,
    repeat 0.35 s / 0.12 s. A ("a"/"cross") -> if P1 button canUseCharacter: Border tinted gold, Picked =
    CharacterData, avatar via `PlayerRenderer.SetCharacter(data, null, forward)` + own materials, info texts
    via GetName/GetDescription and icons. TEMP: logs each P2 pad button/axis name once. Not handled yet:
    grid scrolling, rank/runs/skins in P2 info (still P1's), B/leave.
    v0.7.0 test: cursor (stick X/Y both poles seen, 'A' index 0) + pick CL4NK + info panel WORK (screenshot).
    Bug: the old model (Robinette, cloned) stayed next to the new CL4NK model. Theory: PlayerRenderer.rendererObject
    is runtime-set, so the clone's copy is null and SetCharacter had nothing to replace.
  - [x] v0.7.1 tested 2026-10-17: user "perfect". ROOT CAUSE CONFIRMED: first swap logged rendererObject=null on
    the clone (runtime field, not copied by Instantiate); after we set it, the game's SetCharacter replaced the
    model itself on 14 picks, fallback never fired. **P2 character select picking done.**
  - v0.7.1 (installed, SHA256 B7DBB5F7...FF744CB): Setup finds the clone's model by mirroring P1's rendererObject path,
    sets pr.rendererObject on the clone before SetCharacter if null, logs before-swap state; next frame, if the old
    model survived, logs a warning and destroys it.
  - v0.6.1 (SHA256 A857F042...7F3BEF): info lookup via FindObjectOfType + Transform.Find by name;
    Layout wrapped in try/catch with logging.
  - v0.6.0 step 1 (SHA256 5603CB4E...8843D0): `P2Panel.cs`. Instantiate W_Character under an
    inactive holder (no Awake/OnEnable), DestroyImmediate every Assembly-CSharp MonoBehaviour then every
    Selectable, reparent to 'Character', title T_Title -> "PLAYER 2" (LocalizeStringEvent removed). Layout:
    grid right edge 62 from screen edge, same y/scale as P1; info child at mirrored local x. Sync(): shown
    exactly while P1's W_Character is active. Called from CharacterSelectView.Setup after ApplyP1.
  - [ ] v0.5.3 (installed, SHA256 FCAAB1E1...BD6730): `CharacterSelectView.Detach` destroys the clone's
    CharacterDisplay and gives each clone renderer its own materials (r.materials = r.materials).
  - v0.5.2 (SHA256 2CD772F8...2241AD): [state] adds focused (Application.isFocused), kb/mouse
    GetAnyButton, ReInput.controllers.GetLastActiveController() (try/catch). Highlight log only for scaled buttons.
  - v0.5.1 (SHA256 393218BE...47946C). User asked: grid top level with info
    panel top -> both tops at 110 from screen top (PanelTop). Also: `SelectionArrowFix` postfix on
    SelectionArrow.OnButtonHover(RectTransform) scales newSize by button.lossyScale/arrow.lossyScale (+ logs
    first 6 hovers: rect, ratio, newSize, sizeOffset, arrow sizeDelta). TEMP `[state]` line in PlayerJoin while
    watching, on change: per pad poll index / GetAnyButton / enabled, game p0 GetAnyButton, current controller.
  - v0.5.0 design (SHA256 CC54786C...E71A8): `CharacterSelectLayout.ApplyP1()` from
    CharacterSelectView.Setup: LayoutElement.ignoreLayout on W_Character, scale 0.75, left edge kept at 62;
    info localScale 0.5/0.75, centre = grid right + 20 gap, top at 110 from screen top.
  - P2 panel design notes: CharacterMenu.selectedCharacter is **static** and MyButtonCharacter.A_Select /
    A_Confirm are **static** events, so cloning the menu as-is would make P2's picks drive P1. Plan: copy
    visuals only; mod drives P2's cursor/selection from P2 pad polling; set P2 avatar via its own
    CharacterDisplay (the clone currently mirrors P1 because it listens to those static events).
    `SelectionArrow.Instance` = P1's highlight cursor (singleton).
  - v0.4.0 design (SHA256 35BAA4EC...91BB54): `CharacterSelectView.cs`. On P2 join
    (and on later character-select opens while P2 is joined): clone CharacterDisplay's GameObject (same
    parent) to P1 pos + camera-right * (avatar width * P2Spacing); move MenuCamera.characterSelectionTransform
    back by offset*CameraPullBack and right by offset*CameraCenterShift (assumes MenuCamera.Update eases
    toward currentTransform). Logs spawn/marker positions, and 2 s later the avatars' viewport x/y.
    Config: BepInEx\config\thesplitscreenguy.megabonksplitscreen.cfg [CharacterSelect] P2Spacing 1.6,
    CameraPullBack 1.2, CameraCenterShift 0.5. Clone mirrors P1's selection (it keeps CharacterDisplay).
  - [ ] Then: second character select panel on the right half (CharacterInfoUI may need moving).
- [ ] P2 body in the run (user's step, 2026-10-17: model + animation only, no controls yet).
  - Game: `SpawnPlayerPortal` (StartPortal, DoPortal coroutine, MovePlayer, fields open, movePlayer,
    playerStartPosition -> desiredPosition, static A_PortalOpen/A_PortalClosed). `MyPlayer.Instance.playerRenderer`
    (rendererObject = model, animator, characterData).
  - v0.8.0 (installed, SHA256 39C37F3A...DB0ED0): `P2Body.cs`. Postfix on StartPortal (only if P2 joined) -> next
    frame with P1's model ready: Instantiate(P2Select.Picked ?? P1's data).prefab, scale = P1 model lossyScale,
    animator gets P1's controller if it has none. Follows P1's recorded path 2.5 units behind (path starts 2.5 behind
    P1 opposite the portal move direction), model offset = P1's; copies P1 animator float/int/bool params each frame;
    1 s after portal.open goes false (or 20 s cap): stop + animator.Rebind(). P1 jump > 8 units resets the path.
    Logs prefab renderers/materials, animator/controller names, P1 animator parameter names.
    csproj: + UnityEngine.AnimationModule reference.
  - v0.8.0 test: P2 (Fox) spawned; prefab has its OWN controller (AnimatorFox2) and default materials (m_fox_default).
    User couldn't tell if it animated; asked to move P2 a bit to screen-right. Log: **`Animator.parameters` throws
    TypeLoadException** (Il2CppReferenceArray<AnimatorControllerParameter> violates constraint) -> copy never ran,
    and since it threw before the stop check, P2 kept following P1 the whole time (no "stopped" line).
  - [ ] v0.8.1 (installed, SHA256 936AC4C8...584628): params via parameterCount/GetParameter(i), matched by name
    hash (P2 controller differs), logs both param lists; fallback CrossFade P1's layer-0 state if that fails too;
    animation copy in its own try/catch; P2 placed 1.5 units along camera-right (level) from the path.
    Never tested (user asked for a separate P2 portal first; log still from v0.8.0).
  - [ ] v0.9.0 (installed, SHA256 BB1A40EF...0FF7EF3): user wants P2 through its OWN portal ~2 s after P1's.
    `P2Body.cs` rewritten: StartPortal postfix starts RECORDING P1 each frame (model pos/rot, animator params via
    GetParameter(i) or layer-0 state fallback) and P1 portal open/close times (logged). At +2 s: portal copy
    (inactive holder, Assembly-CSharp scripts stripped, blockObjectSpawns removed) placed 3.5 units across P1's walk
    direction on camera-right; its portalRender scaled 0 -> portalScale in 0.4 s, audioOpen/audioLoop played. P2 prefab
    REPLAYS the recording delayed 2 s + shifted; portal closes (audioClose, scale to 0, destroy) when replay reaches
    P1's close time; after replay end animator.Rebind() (idle). Cleanup on next StartPortal.
    csproj: + AudioModule, ParticleSystemModule.
  - [x] v0.9.0 tested 2026-10-17: user "perfect" (2 stages). Log: P1's controller (CalciumAController) has ZERO
    animator parameters (game plays states directly); P2 `Animator.GetParameter(i)` -> "Method unstripping failed",
    so the replay used the state-copy fallback (CrossFade P1's layer-0 state). Portal render 'Portal' scale 3.5,
    children [Portal, PassingParticles]; P1 portal open +0.00 s, closed +3.02 s.
    User: portals intersect -> move P2's 1 unit back.
- [ ] P2 controls in the run (user, 2026-10-17: movement first, no camera yet).
  - v0.10.0 (installed, SHA256 698C246A...A8C9ED): portal shift += -walkDir * 1. `P2Control.cs`: after the entrance,
    CharacterController on P2's model (r/h from P1's playerCollider, center at h/2 assuming feet pivot), gravity 30,
    left stick camera-relative (Camera.main), speed = PlayerMovement.currentMaxSpeed (fallback 8), turn 720 deg/s,
    skips when deltaTime 0 (paused). Stick via joy.GetAxisById, ids learned from polling names (default 0/1).
    Animation: learns P1's layer-0 state while grounded & speed > 3 (run) / < 0.3 (idle), 20 frames; fallback
    StringToHash names (Run/Idle...) checked with HasState; CrossFade on move/stop.
  - v0.10.0 test: P2 moves (stick ids X=0, Y=1 confirmed; speed 11.7; collider r 0.70 h 3.85; P1 model pivot = capsule
    bottom, feet pivot OK) but ALWAYS IDLE; P2 collides with something around P1. Log: "Run" (1748754976) and "Idle"
    found on P2 by name. Learning was wrong: during the portal P1's rb velocity ~0 (portal moves the transform), so
    states -1831344825 / 298403382 were learned as "idle"; run = -1831344825 learned only 23 s later.
  - [ ] v0.10.1 (installed, SHA256 6EE28838...33527C): P1 speed from position delta, no learning while P2Body records
    the portal; prefer learned state when it changes; KeepState re-Plays the wanted state if the controller leaves it
    (logs 5x); TEMP [anim] logs (P1 state changes with speed, P2 played states); Physics.IgnoreCollision(cc, every
    collider under MyPlayer + PlayerMovement objects), logged; TEMP [collide] log of colliders when blocked sideways.
  - v0.10.1 test: **collisions fixed** (user). P1 had 2 colliders: Player (CapsuleCollider) and DetectPickups
    (SphereCollider, NOT a trigger, layer Pickups) = the "sphere around P1". Animation: learned correctly
    (P1 run 1744665739, idle 2081823275 = "Idle"), but P2 snapped between idle and frame 0 of the walk: P2's controller
    left the run state for Idle right after each CrossFade and KeepState re-Played it from frame 0.
    Theory: P2's controller has parameters (P2 parameterCount > 0 since GetParameter was called; Calcium's had 0),
    e.g. a "moving" bool at its default, driving run -> idle. PlayerRenderer has `moving`/ForceMoving.
  - [ ] v0.10.2 (installed, SHA256 214EA133...310B7D): KeepState removed; on start/stop SetBool/SetFloat common names
    (moving/isMoving/walking/running/move..., speed/velocity/moveSpeed/movementSpeed...), grounded-style bools = true
    once; TEMP [anim] log when P2's controller goes to a state we didn't ask for (15x).
  - v0.10.2 test: user "animations good" (Monke), but Calcium walked once then idle. Log: **`Animator.SetFloat(string,
    float)` throws MissingMethodException (ReadOnlySpan.GetPinnableReference)** every frame -> Update aborted before
    PlayState; Monke worked only because SetBool("moving"...) ran first (its controller is param-driven); Calcium's
    controller has 0 params so it needed the CrossFade that never ran. Use hash overloads (StringToHash works).
- [ ] P2 jump + attacks (user, 2026-10-17). User: P2's weapons must be P2's character's, tracked separately from P1.
  - Weapons: WeaponInventory (Dictionary<EWeapon, WeaponBase>, Tick), WeaponBase(WeaponData) Use/IsCooldown/
    GetCooldown, WeaponUtility.WeaponAttack(WeaponBase), WeaponAttack.SetAttack(WeaponBase, MyPlayer) +
    GetProjectilePosition() (pooled attacks), ProjectileBase.Set(weaponBase, weaponAttack, idx).
  - v0.11.0 (installed, SHA256 6493AC86...32B74F): move params by hash. Jump on A (polled, edge), JumpSpeed 12,
    coyote 0.15 s, airborne/landed tracking; jump/fall states learned from P1 (!pm.grounded, vy >2 / < -2, 5 frames)
    or names Jump/Fall...; grounded params set on change. `P2Weapons.cs`: own list of WeaponBase (P2 pick's
    CharacterData.weapon), fired when Time - lastFired >= max(0.1, GetCooldown()) via Use(); if Use() set up no attack,
    WeaponUtility.WeaponAttack(w). SetAttack postfix marks attacks made while firing for P2 (pointer set; cleared
    otherwise); GetProjectilePosition postfix shifts by P2 body - P1 model. Logs adds, first 3 fires, first 3 shifts.
    Stats still P1's.
  - v0.11.0 test: **jump good** (user). P1 jump state 125937960, fall 998495048. Weapons: P2 Bone (Calcium) added,
    fired every 1.40 s, 1 attack set up each, but shots came **from P1**: the GetProjectilePosition postfix never ran
    (no "P2 shot position" line) - likely inlined by IL2CPP.
  - [x] v0.11.1 tested 2026-10-17 (P2 Fox): **P2's fireballs fire from P2** (user "good job"). Log: both hooks run;
    ProjectileBase.Set moved them (P1 pos -> P2 pos). Known, deferred by user: the cast/muzzle particle still shows
    at P1 - the WeaponAttack object itself stays at P1 ('FireballAttack(Clone)' set up at P1 + (0,1,1)); fix later by
    moving the attack's transform (and its muzzle) for P2 attacks.
- [ ] P2 damage + health + HUD (user, 2026-10-17): P2 health bar + weapon list beneath P1's, "Find the Boss Portal"
  objective box beneath P2's (screenshot: top-left HP bar 115/151, weapon/tome grid 3x2, objective box below).
  - How enemies target (from types, unverified): Enemy.target (Rigidbody, P1's) set in Enemy.FindTarget();
    EnemyMovementRb.GetTargetPosition steers to it; TeleportToPlayer for stragglers. Damage to P1 comes from
    MyPlayer.OnCollisionStay -> PlayerHealth.DamagePlayer(enemy, dir, flags), per-enemy cooldown (enemyAttackCooldown).
    EnemyManager.Instance.enemies (Dictionary<uint, Enemy>), collidersToEnemies. HUD: HealthUI (healthBar/shieldBar/
    overhealBar Transforms, t_hp, canvasGroup, followPlayer = the bar under P1), InventoryHud (weaponContainers of
    InventoryItemPrefabUI icon/t_level/lockedOverlay, tomeParent), ObjectiveUi.
  - v0.12.0 (installed, SHA256 D989DCEE...A9A77C): `P2Health.cs`: kinematic Rigidbody on P2 body; every 0.25 s + after
    Enemy.FindTarget each enemy's target = closer living player; contact check OverlapCapsule (Enemy layer) ->
    EnemyData.damage per enemy per enemyAttackCooldown; MaxHp = P1 maxHp at run start, kept across stages, reset when
    char select closes; 0 HP = down (body hidden, controls/weapons off, enemies back to P1). `P2Hud.cs`: inert copies
    of HealthUI (non-follow) and InventoryHud shifted down by P1 block height (measured from child rects) + 10;
    tome row hidden; ObjectiveUi moved by the same shift (ignoreLayout); bar localScale.x = hp/max, text, P2 weapon
    icons/levels; visibility/alpha synced. TEMP hud_dump.txt.
  - v0.12.0 test: **enemies chased P2** ("Enemies: 1 of 3 now chase P2"), **P2 took damage** (Goblin 15 per hit,
    ~0.3 s cooldown), **P2 went down** at 0 (model hidden). P2 MaxHp 150. HUD NOT visible: measured P1 block 1329 px
    because 'Inventory' (InventoryHud) is a full-screen VerticalLayoutGroup column holding 'Bars (1)' (HealthUI 250x30),
    'Weapons' (GridLayoutGroup 450x60), 'Tomes', 'ObjectiveUi' -> copies placed off-screen.
  - [ ] v0.12.1 (installed, SHA256 EDA68755...5C50C48): P2Hud copies only 'Bars (1)' and 'Weapons' and inserts them
    into the 'Inventory' column after 'Tomes' -> game layout places them under P1's block and pushes ObjectiveUi down.
    No manual shift/objective move. Fills only active weapon slots. Logs the column's child order.
  - [x] v0.12.1 tested: **P2 HUD visible** (user "good job"). User: P2 lacks P1's hit effect.
  - [ ] v0.12.2 (installed, SHA256 734295B3...C8910): on P2 hit, all P2 renderers' sharedMaterials = P1's
    PlayerRenderer.damageFlash for 0.12 s, then restored. Camera shake (CameraShakerController.OnDamage) left out on
    purpose (shared camera). Other possible P1 hit feedback not done: sound, damage numbers (DamageNumbers).
    Not tested yet - user moved on to the XP bar.
- [ ] Split XP bar (user, 2026-10-17): P1 blue from left edge to middle, P2 RED from right edge to middle.
  - XpBarUI (on 'Top'): xpBar RawImage, t_levelText, defaultColor, rainbow/defaultMaterial, desiredRatio (fill method
    unknown). PlayerXp (xp, level, AddXp, A_XpAdded), XpUtility (CurrentLevelProgress, XpToLevel...). Pickups: Pickup
    (ePickup, value, target, StartFollowingPlayer(Transform), ApplyPickup, OnTriggerStay), P1's DetectPickups sphere.
  - v0.13.0 (installed, SHA256 63BC06D8...9F6B): `P2XpBar.cs`. P1's bar reparented into a container that covers the
    bar's parent and is scaled x 0.5 around the bar's left edge (works whatever fill method the game uses). Red copy
    (Assembly-CSharp scripts stripped, defaultMaterial) in a container scaled x -0.5 around p = (right + 0.5 left)/1.5
    (mirror: right edge -> middle); fill = localScale.x of the copy with pivot x 0 = CurrentLevelProgress(P2 Xp).
    P2 Xp static, reset on new run; P2 does NOT collect XP yet. TEMP F11 = +half of the XP to next level for P2.
    TEMP xpbar_dump.txt.
  - [x] v0.13.0 tested: user "p1 bar is now half", "hit effects for p2 are good".
- [ ] P2 XP gems (user, 2026-10-17).
  - v0.14.0 (installed, SHA256 C5C552E5...601348): `P2Pickups.cs`. Every 0.1 s OverlapSphere (PickupManager
    .whatIsPickups, triggers included) around P2 with P1's DetectPickups radius; free XP gems (target null,
    !pickedUp, CanPickup) -> StartFollowingPlayer(P2 body), remembered. Prefix StartFollowingPlayer keeps P2 gems from
    being re-targeted to P1. ApplyPickup prefix/postfix: P2 gem -> PlayerXp.AddXp skipped (prefix returns false),
    value to P2XpBar.AddXp. Fallback: P2 gem within 1.2 of P2 and not applied -> ApplyPickup ourselves. Gold stays P1.
  - v0.14.0 test: P2 attracted gems and got XP (+1s, reached level 2), but **P1's bar filled too**, and the red bar
    was a sliver at the LEFT corner. Log: P1 'Bar' in 'XpBar' (1914 wide): anchors stretch (0,0)-(1,1), pivot x 0,
    **fill = localScale.x** (0 at run start) -> measured edges 0..0 through the transform -> mirror pivot 0.
  - [ ] v0.14.1 (installed, SHA256 428677ED...1AE85): bar edges from localPosition + unscaled rect (fallback 0..1).
    P2 gem: also skip PlayerInventory.AddXp, and snapshot/restore P1 inventory.pendingXp + playerXp.xp/leftOverXp around
    ApplyPickup (logs when a restore was needed).
  - [x] v0.14.1 tested: user "that works" (red bar from right, P1 bar unaffected by P2 gems).
- [ ] P2 level-up (user, 2026-10-17): P2's own upgrade screen only P2 controls, rainbow on P2's bar, both players
  frozen while open. User concern: P2 upgrading a weapon it doesn't have -> offers must come from P2's weapons.
  - Game: PlayerXp.A_LevelUp -> EncounterWindows.OnLevelUp -> AddEncounter(EEncounter.Levelup) -> rewardQueue ->
    LevelupScreen.Open(EEncounter) -> UpgradePicker.ShuffleUpgrades -> UpgradeButton -> UpgradePicker.SelectUpgrade ->
    PlayerInventory.AddUpgrade(IUpgradable, List<StatModifier>, ERarity); LevelupScreen.OnClose/CloseLevelupScreen;
    XpBarUI.OnLevelupShow/Hide + rainbow material.
  - v0.15.0 (installed, SHA256 AD86A2D2...ABD46): `P2LevelUp.cs`. P2 level gained -> ew.AddEncounter(Levelup) with
    owner flag; owners queue (AddEncounter prefix; OnLevelUp postfix enqueues P1 if AddEncounter wasn't hit). Open
    prefix: P2's -> controls swap (P1 joysticks removed from Rewired player 0, hasKeyboard/hasMouse false, P2 pad
    added) + MyPlayer inventory.weaponInventory swapped for a temp WeaponInventory holding P2's WeaponBase objects
    (P2-based offers). Title "PLAYER 2 LEVEL UP!". AddUpgrade prefix during P2's screen: WeaponData -> P2Weapons
    .AddOrUpgrade (WeaponBase.Upgrade(offer) or Add), skip P1; tomes shared. Close: swap back + controls back.
    XpBarUI.OnLevelupShow skipped during P2's screen. P2 bar: full + rainbow material while Pending/open.
  - v0.15.0 test: rainbow + "P2" screen opened, **controls swap worked** (1 P1 joystick removed and back, kb/mouse
    restored). Bugs: (1) AddEncounter opened the screen synchronously -> Open's Pending-- ran before Pending++ ->
    Pending stuck at 1 -> bar stayed full rainbow. Fix: Pending++ before AddEncounter. (2) HP tome went to P1 (by design
    then). User chose: **P2 gets its own stats now**.
  - [ ] v0.16.1 (installed, SHA256 0B72B9AB...19C40) = v0.16.0 + re-aim: user saw P2 fireballs flying in P1's aim
    direction (aimed from P1 at P1's enemy). Set + SuccessfullySpawned postfixes now re-aim P2 projectiles at the enemy
    nearest to P2 (direction + rotation, vertical component kept; ProjectileHoming.enemyTarget; ProjectileBasic
    .currentTarget if set). Logs first 5 re-aims. Untested.
  - v0.16.1 test: **game CRASHED** (hard, no exception logged) when P1 (Megachad) opened a chest and took an Aura
    upgrade; P2 = Calcium (Bone). P2 inventory created OK ("1 weapon(s), max HP 100"; ctor adds the starting weapon by
    itself). Last log lines: "P2 max HP now 120 (+20)" then "100 (-20)" = P2's PlayerHealth recomputed from P1's stats
    (global stat events + "the player's inventory"). Player.log (AppData\LocalLow\Ved\Megabonk, user allowed reading)
    has nothing (not flushed). Theory: an exception thrown by game code inside an owner-context-patched method
    escaped the detour into native code (crash) and the postfix Exit never ran.
  - [ ] v0.16.2 (installed, SHA256 E07224F8...C44C7F): owner-context patches use Harmony **finalizers** (always Exit,
    log + swallow the exception). New owner context on PlayerHealth UpdateMaxValues/OnStatUpdate/UpdateRegenValues/
    OnLevelUp and PlayerStatsNew UpdateStat/Tick/ForceUpdateStats/TryPopStatUpdatesQueue/QueueUpdateStat(Tome) by
    instance (P2's objects -> P2 context, others -> P1 context when in P2 context). TEMP log of every queued encounter.
  - v0.16.2 test: **crashed again**, at stage start (P1 Megachad, P2 CL4NK) between "P1 portal closed +3.03 s"
    and the end of recording, i.e. BEFORE P2's inventory exists (all owner prefixes return at once). So the patch
    MECHANICS crash, not the context logic. Both crashes: P1 Megachad (Aura = constant weapon) using hooked
    WeaponUtility methods. Suspect: object[] __args on native detours (boxing Vector3 etc.).
  - [ ] v0.16.3 (installed, SHA256 CA9E72FD...F79B32): owner prefix uses typed `WeaponBase __0` (no __args) and returns
    at once without an inventory; per-frame stats patches dropped (only UpdateStat/ForceUpdateStats kept). Config
    [Experimental] P2OwnStats (default true) disables all P2Inventory patches; P2Weapons falls back to a standalone
    starting WeaponBase. If it still crashes with P2OwnStats=false -> cause is elsewhere (bisect).
  - v0.16.3 test: stage start OK (progress). **P2 level-up worked: Agility Tome went to P2's inventory/HUD.** Then P1
    (Megachad) levelled up ("P2 max HP now 41 (+1)" = P2's PlayerHealth.OnLevelUp heard P1's global level-up) and took
    an **Aura upgrade -> crash** again. Listeners of global stat events: ConstantAttack subclasses (CombatAura =
    Megachad's Aura on P1, via AuraAttacks.auras) OnStatUpdate/OnWeaponStatUpdate, DetectPickups.OnStatUpdated, HUD.
    Theory: P2's stats ticked in P2's context -> global A_StatUpdate -> P1's CombatAura ran in P2's context, looked up
    the Aura in P2's inventory, threw; exceptions under a detoured frame kill the process.
  - [ ] v0.16.4 (installed, SHA256 F8F7E9EE...ADF3C0): P2 stats ticked in P1's context (skipped while P2 context is
    active); PlayerStatsNew owner patches removed; P2's PlayerHealth.OnLevelUp skipped (no P1 level-up bonus for P2).
  - v0.16.4 test (P1 ?, P2 Amog): **P2 new weapon from level-up works** (Lightning Staff, fires from P2). P1 taking a
    NEW weapon (Flamewalker) on P1's level-up -> **game FROZE** (had to kill it), no error logged; last line
    "Encounter queued: Levelup". Aura-upgrade result not reported yet.
  - [ ] v0.16.5 (installed, SHA256 23EC1D6F...8002B6): TEMP `FreezeDiag.cs`: logs WeaponInventory.AddWeapon (P1/P2,
    context), AuraAttacks.OnWeaponAdded/Refresh before/after, heartbeat every 2 s. Next bisect if unclear:
    config Experimental.P2OwnStats = false.
  - v0.16.5 test: no freeze this time (P1 got Katana, Lightning Staff; P2 got Size/Cooldown tomes), no Aura upgrade
    offered. But **P1's new weapons didn't appear on P1's HUD** (they show in the pause menu). Log: "[diag] AddWeapon
    Revolver to P1 (P2 context True)" at P2 inventory creation, and AuraAttacks.OnWeaponAdded reacted to it.
    ROOT CAUSE (likely for crashes/freeze too): creating P2's PlayerInventory fires the global "inventory initialized"
    event and P1's systems (InventoryHud, HealthUI, XpBarUI, DetectPickups, AuraAttacks, EncounterWindows,
    PlayerRenderer... 10 handlers named *InventoryInit*(PlayerInventory)) re-bound to P2's inventory.
  - [ ] v0.16.6 (installed, SHA256 28A0448C...2402DB): all *InventoryInit*(PlayerInventory) handlers (found by
    reflection) skipped while P2's inventory is being created; AuraAttacks.OnWeaponAdded/Removed/Toggle skip P2's
    weapons and P2 context (P2's always-on weapons don't work yet). FreezeDiag still on.
  - v0.16.6 test: P1 Aura upgrade -> **crash** again. Diag: "[diag] AddWeapon Aura to P1" -> AuraAttacks.OnWeaponAdded
    done -> crash before "AddWeapon done" (upgrade path: AddWeapon -> WeaponBase.Upgrade). All crashes = P1 upgrading an
    EXISTING weapon through hooked WeaponBase.Upgrade(List<StatModifier>); new weapons (P1 Katana etc., P2 picks) fine.
    BepInEx log has "Class::Init signatures have been exhausted, using a substitute!".
  - [ ] v0.16.7 (installed, SHA256 C17814DF...BA1112): WeaponBase hooks reduced to UpdateStat only (Upgrade, GetValue,
    GetCooldown, IsCooldown, Use, WeaponPassiveChanged unhooked); P2Weapons.Tick runs in P2 context itself.
    If an Aura upgrade still crashes: bisect with Experimental.P2OwnStats = false.
  - v0.16.7 test: P2 picked Quantity + Regen tomes fine; then **P1 picked a Regen tome -> crash** (right after P1's
    level-up opened at 14:35:49; heartbeat stopped). Not a weapon upgrade -> Upgrade-hook theory insufficient.
    Note: Il2CppInterop wraps managed hooks in try/catch itself, so "exception escaping a hook" may not be it either.
  - BISECT 1 (2026-10-17): config Experimental.P2OwnStats = **false** (set by me in the mod's own cfg). Disables all
    P2Inventory hooks/inventory; level-up screen hooks, pickups, XP etc. stay. Test: P1 picks tomes + Aura upgrades.
    No crash -> cause inside P2OwnStats; crash -> elsewhere (P2LevelUp/P2Pickups/FreezeDiag hooks).
  - BISECT 1 result: **no crash** with P2OwnStats=false (P1 took an Aura upgrade at 14:37:57, LightningStaff, FireStaff;
    played until P2 died). -> cause is inside P2OwnStats.
  - BISECT 2 (v0.16.8 installed, SHA256 DB853180...C74232; cfg set P2OwnStats=true, P2ContextHooks=false): new cfg Experimental.P2ContextHooks; test with
    P2OwnStats=true + P2ContextHooks=false (P2 inventory + guards + level-up context, but no hooks on WeaponUtility/
    WeaponBase.UpdateStat/PlayerHealth/GetPlayerInventory). Crash -> P2 inventory's own listeners; no crash -> context hooks.
  - BISECT 2 result: **crash** with context hooks off (P1 picked Aura size upgrade) -> cause = P2's PlayerInventory
    existing (its objects listening to global events) and/or the guard hooks / P2 stats tick / level-up context.
    Theory: two stat systems feed each other through global events (A_StatUpdate etc.) -> stack overflow (crash) or
    endless loop (freeze); all crashes were on stat changes (Aura size, Regen tome).
  - [ ] v0.16.9 (installed, SHA256 C2093972...F56CD; cfg unchanged: P2OwnStats=true, P2ContextHooks=false): FreezeDiag
    counts PlayerStatsNew.UpdateStat/ForceUpdateStats, PlayerHealth.OnStatUpdate/UpdateMaxValues, WeaponBase.UpdateStat
    calls by owner (P1/P2) per 2 s in the heartbeat, logs nesting >= 12 with the call chain (3x).
  - v0.16.9 result: crash on P1 Aura damage upgrade; **no deep nesting** (max 4) -> feedback-loop theory OUT. Crash
    inside AddWeapon(Aura) right after AuraAttacks.OnWeaponAdded. P2 max HP jumped to 121 (+81) at P1's level-up
    (context hooks off -> P2's health read P1's stats): P2's objects do react to P1's global events. P2 stats
    UpdateStat ~144/s constantly (queue never drains?).
    Current theory: P2's objects throw on P1's events (normal game exceptions, caught higher up by Unity), but the pick
    path ran through our hooked frames (PlayerInventory.AddUpgrade postfix since v0.16.0, FreezeDiag AddWeapon hooks)
    -> exception can't unwind through a detour -> hard crash. Fits bisect 1 (no P2 inventory -> nothing throws).
  - [ ] v0.17.0 (installed, SHA256 DB1A45A0...31717E; cfg P2OwnStats=true, P2ContextHooks=false): AddUpgrade hook
    removed (P2 picks found by polling P2 inventory: weapon count/levels -> Sync; tomes via tomeLevels + TomeData
    remembered from UpgradePicker.buttons while LevelupScreen.isLevelingUp); FreezeDiag hooks removed (heartbeat only).
    Expect: no crash, and the game's own exception + stack trace in Player.log (AppData\LocalLow\Ved\Megabonk).
  - v0.17.0 result: crash on P1 Aura size upgrade. Player.log not flushed; no Unity Crashes folder (user allowed
    checking %TEMP%\Ved\Megabonk\Crashes). **Windows Application event log**: faulting module **coreclr.dll**,
    ".NET Runtime internal error", exit code **c000001d** -> the .NET side (our hooks) died: a native game exception
    hit a hooked (managed) frame. Remaining hooks on P1's upgrade path were the guard prefixes on InventoryHud
    .OnWeaponAdded/OnTomeAdded, AuraAttacks.OnWeaponAdded/Removed/Toggle, PlayerHealth.OnLevelUp.
  - [ ] v0.17.1 (installed, SHA256 EBCA2EBA...CD747; cfg P2OwnStats=true, P2ContextHooks=false): guards are SCOPED:
    own Harmony id "<guid>.p2guards", GuardsOn() patches init handlers + AuraAttacks + InventoryHud handlers with a
    skip prefix, GuardsOff() UnpatchSelf; on only during P2 inventory creation, P2 starting-weapon AddWeapon and P2's
    level-up screen. PlayerHealth.OnLevelUp guard + LogEncounter removed. P1's picks now run through unhooked code.
  - v0.17.1 result: crash on P1 Aura upgrade still (guards scoped, context hooks off). Conclusion: a second game
    PlayerInventory can't coexist safely. User chose **redesign, "in line with player 1"**.
  - [ ] v0.18.0 (installed, SHA256 E62B479C...F23BC; cfg reset to P2OwnStats=true, P2ContextHooks=true): NO second
    PlayerInventory. P2 = new WeaponInventory() + new TomeInventory() (dicts created if null). P2 stats computed by
    the mod per EStat with the game's code: PlayerStatsNew.GetBaseValue + P1 playerStats.ApplyStatModifier(character
    statModifiers x1, tomeUpgrade[tome] x tomeLevels) + StatComponents.SetValues/GetFinalValue + CheckFinalValue.
    Stat context = depth counter (no inventory swap); PlayerStatsNew.GetStat/GetRawStat/GetUnclampedStat prefix returns
    P2's value in context (full replacement). WeaponUtility(__0)/WeaponBase.UpdateStat hooks: P2 weapon -> context on,
    P1 weapon -> context suspended. P2 tome change -> recompute + every P2 weapon UpdateStat(all stats) in context.
    Level-up: P1 inventory.weaponInventory/tomeInventory swapped to P2's holders while P2's screen is open (+ guards,
    + context), restored after, then P1 playerStats.ForceUpdateStats(). P2 max HP = round(P2 MaxHealth stat).
    TEMP "[stats] P1 formula check" log (game vs mine for P1). FreezeDiag = heartbeat only. csproj + Il2CppSystem.Core.
    Not covered: P1's shop/meta upgrades and items for P2; special tomes' extra effects (TomeUtility.CheckSpecialTomes
    acts on P1); character passives.
  - v0.18.0 result: **P1 Aura upgrade still crashed** (no second inventory!); P2 died in one hit + weapons didn't fire:
    my formula was wrong ("[stats] P1 formula check: MaxHealth game 120 / mine 1; DamageMultiplier 1.00 / 0.00;
    AttackSpeed 1.00 / 0.00"). MaxHealth stat = absolute max HP (P1 120 = maxHp 120).
  - [ ] v0.18.1 (installed, SHA256 8E9A01DD...CC1E8): (1) P2 stats computed BY THE GAME: P1 inventory gets P2's
    TomeInventory + characterData, playerStats.ForceUpdateStats(), copy ps.stats -> p2Dict, restore + ForceUpdateStats,
    P1 hp/shield/overheal saved+restored. Every 10 s + on P2 tome change. Includes P1's items/shop for P2. (2) Stat
    context = swap P1 playerStats.stats table for p2Dict (stack; P1 weapons get the bottom P1 table back); GetStat hooks
    removed. (3) **CRASH HUNT**: Plugin.SetGameplayHooks - all gameplay hooks UnpatchSelf while a P1 level-up/chest
    window is open (LevelupScreen.isLevelingUp or EncounterWindows.encounterInProgress, not P2's screen), re-applied
    after. OnlineBlock moved to its own Harmony id "<guid>.online" and is never removed. If no crash -> hooks were it;
    the game's own exception should then be in Player.log. Known side effect: a P2 level-up queued during a P1 menu
    isn't tracked (P1 would control it).
  - v0.18.1 result: P2 stats now correct ("[stats] P2 start: MaxHealth P2 120 / P1 120; Damage 1.00/1.00 ...") and
    P2's weapons fire (Revolver, then Flamewalker, Bow from P2 picks). **P1 Aura upgrade crashed WITH ALL GAMEPLAY HOOKS
    REMOVED** ("[hunt] gameplay hooks OFF" 15:09:04.9, crash 15:09:07, same coreclr offset 0x1d1fdd as every crash).
    "hooks back on" never logged. Only OnlineBlock (own id) was active -> hook theory OUT.
  - RE-CHECK of bisect 1 (single sample!): cfg P2OwnStats=false, P1 Megachad, 2-3 Aura upgrades. No crash -> P2's
    holders/objects trigger it; crash -> unrelated to P2 stats (next: mod fully off = vanilla check).
  - User skipped the re-check ("we've tried this before") and asked about P2's prefab: maybe its components react to
    OnWeaponAdded. P2 body = Instantiate(CharacterData.prefab), never stripped of game scripts.
  - [ ] v0.18.2 (installed, SHA256 FDF5EED8...47CB85E; cfg P2OwnStats back to true): on P2 body spawn logs "[body] ...
    game script(s)" for P2's body and P1's model, and writes p2body_dump.txt (full hierarchy + components of both).
  - v0.18.2 result (P2 CL4NK, P1 Megachad): P2 body = {Animator, AnimationFootstepFx} on the root, bones, two
    'B_revolver' MeshRenderers in the hands, 'Cube' SkinnedMeshRenderer. P1's model has the same two components.
    AnimationFootstepFx = only RightFoot/LeftFoot/Spawn(pos) (animation-event footstep effects), no events/weapon
    handlers. -> P2's body has nothing that listens to OnWeaponAdded; prefab theory OUT.
  - [ ] v0.18.3 (installed, SHA256 850FE6F5...371191): TEMP `EventDump.cs` (user's request): snapshots of every global
    event (static Il2CppSystem.Delegate properties in Assembly-CSharp) with each listener (method + target, labelled
    P1/P2/other: WeaponBase via P2Weapons.IsP2, P2 holders by pointer). At P2 spawn, when a P1 level-up/chest window
    opens, after P2's weapon/tome picks. Full lists -> events_dump.txt; P2-owned listeners summarised in the log.
  - v0.18.3 result: no P2-owned listener on any global event (151 events / 846 listeners). Still crashed.
  - **CRASH DUMP SOLVED IT** (user enabled WER LocalDumps -> H:\CrashDumps; dotnet-dump 10 installed; analyzed with
    `runtimes -v false` + `setclrpath <game>\dotnet` because BepInEx's mscordaccore fails the DAC signature check):
    main thread = System.ExecutionEngineException, stack: il2cpp_runtime_invoke <- DMD<InventoryHud::OnTomeAdded>
    (Harmony wrapper) <- (il2cpp -> managed) OnTomeAdded. ROOT CAUSE: my scoped "guard" hooks on InventoryHud
    .OnWeaponAdded/OnTomeAdded (+AuraAttacks); Harmony UnpatchSelf on IL2CPP leaves the DMD wrapper in place, and
    calling InventoryHud.OnTomeAdded(ETome, EStat) through it crashes coreclr. Guards were installed at every P2 spawn
    (starting weapon) -> every P1 tome/weapon-upgrade crashed. Bisect 1 (P2OwnStats off) never installed them.
    Also explains v0.16 crashes (those handlers hooked from start).
  - [ ] v0.19.0 (installed, SHA256 019B3FBB...BBCB5): guard hooks REMOVED. GuardsOn/Off now only count P2 actions; at the
    end RepairP1(): InventoryHud.Refresh() (logs weapon slot counts) and remove AuraAttacks.auras entries P1 doesn't own
    (Destroy + Remove). Hunt switch (SetGameplayHooks/UnpatchSelf) and EventDump.Snapshot calls removed.
  - [x] v0.19.0 tested: **crash fixed** - P1 Aura upgrades/tomes fine, P2 upgrades + weapons work (user). Issues:
    P2 bar rainbow doesn't reset; P2's always-on weapons (Aegis; Megachad's Aura as P2) have no effect. Log: P2's
    "fired Aegis: 0 attack(s)"; RepairP1 "Removed always-on attack Aegis from P1" (AuraAttacks had put it on P1).
  - [x] v0.19.1 tested 2026-10-17: user "it works" (rainbow reset, P2 always-on weapons, muzzle flashes at P2).
    NEXT per plan: left/right splitscreen cameras. Later: clean up TEMP diagnostics (FreezeDiag heartbeat, EventDump,
    UiDump, [anim]/[collide]/[stats]/[body] logs, F9/F10/F11 keys) + retest; consider unpinning the crash-dump setting.
  - v0.19.1 (installed, SHA256 9ADE52BD...7039C3): rainbow off restores the bar's own original material
    (XpBarUI.defaultMaterial may be empty). P2 always-on weapons: WeaponData.attack prefab has a ConstantAttack ->
    Instantiate under P2's body + ConstantAttack.Set(P2's WeaponBase) in P2 stat context (on Sync); skipped by the
    firing loop. + Muzzle flashes (user: CL4NK's flashes on P1): MoveAttack() shifts the WeaponAttack transform (and
    its AttackMuzzle if not a child) by P1->P2 offset on SetAttack and on each ProjectileBase.Set, only while nearer P1.
  - **Lessons: (1) Never "unpatch" at runtime on IL2CPP - the Harmony wrapper stays. (2) Never hook InventoryHud
    .OnTomeAdded/OnWeaponAdded (calling the original through the wrapper crashes the .NET runtime). (3) For native
    crashes: WER LocalDumps + dotnet-dump (runtimes -v false; setclrpath <game>\dotnet; setthread 0; clrstack -f).**
  - Lesson: **an IL2CPP exception thrown below a Harmony-detoured method crashes the game hard (no log)**; and global
    static events reach P1's objects even while the mod is in P2's context. Keep P2 context windows minimal.
  - v0.16.0 (never installed alone): `P2Inventory.cs`: P2 = `new PlayerInventory(P2 CharacterData,
    false)` (kept for the run, Cleanup on new run). Context switch: MyPlayer.Instance.inventory pointed at P2's inventory
    (stack, restored in postfix/finally); GameManager.GetPlayerInventory postfix returns P2's in P2 context. Owner
    context prefix/postfix on all WeaponUtility statics with WeaponBase arg0 and WeaponBase UpdateStat/GetValue/
    GetCooldown/IsCooldown/Use/Upgrade/WeaponPassiveChanged. P2's playerStats.Tick() each frame in P2 context.
    InventoryHud.OnWeaponAdded/OnTomeAdded skipped in P2 context. PlayerInventory.AddUpgrade postfix on P2's inventory:
    P2Weapons.Sync + record TomeData. P2Weapons now = P2 inventory's weaponInventory.weapons (starting weapon added
    via AddWeapon in P2 context), fired in P2 context. P2LevelUp: whole screen in P2 context (old AddUpgrade intercept
    + temp WeaponInventory removed). P2Health MaxHp = P2 inventory playerHealth.maxHp, synced (raise heals). P2 HUD:
    tome row copy filled from P2Inventory.Tomes + tomeInventory.GetTomeLevel. EStat is Assets.Scripts.Menu.Shop.EStat
    (DamageMultiplier, MaxHealth...).
- [ ] Left/right splitscreen (user, 2026-10-17). Game: PlayerCamera.Instance (camera, offset3rdPerson, ECameraState
  Portal/Player3rd/Player1st/Death, GetPlayerHeadPosition, PlayerCam(rot), deathCamera).
  - v0.20.0 (installed, SHA256 D934EC28...F004DA7): `P2Camera.cs`. On P2 body spawn: P1 camera rect = left half; P2 camera
    = copy of P1's camera GO (inactive holder, Assembly-CSharp scripts + AudioListener + child cameras removed, tag
    Untagged) with rect = right half. Each frame: local = inverse(P1 cam rot) * (P1 cam pos - P1 head); P2 cam =
    P2 head (body + P1's head-model offset) + rot(pitch,yaw) * local; FOV copied. P2 right stick (ids learned by polling
    "right stick x/y", default 2/3) turns yaw 160/s, pitch 110/s clamp -35..70. P2 movement relative to P2 camera.
    No wall collision. Stop() restores P1 full rect. TEMP camera_dump.txt.
  - [x] v0.20.0 tested 2026-10-17: user "crazy how good that feels". Open: P2 camera wall collision, death camera.
  - [ ] v0.20.1 (installed, SHA256 D05C1102...605390): P2 camera wall collision: Physics.SphereCastAll from P2 head to the
    wanted spot, mask GameManager.whatIsCameraCollision, radius PlayerCamera.cameraRadius (fallback 0.3), ignore P2's
    own colliders; on hit camera at max(0.5, hitDist - 0.1). Logs mask/radius once.
- [ ] P2 pause (user, 2026-10-17): P2 can pause + control the pause menu; banner "P1 PAUSE" blue / "P2 PAUSE" red.
  - Game: PauseUi (Pause/Resume/Toggle/CanPause/IsPaused, main/options/map windows), PauseHandler; input = Rewired p0.
  - v0.21.0 (installed, SHA256 979FDD49...AB026; includes untested v0.20.1 camera collision): `P2Pause.cs`. P2 Start
    edge (polling) when !paused, no level-up screen, PauseUi.CanPause() -> PauseUi.Pause(); after Start release
    P2LevelUp.GiveControlsToP2() (now internal); pause end -> RestoreControls. Paused without us = P1. Banner: own
    Canvas (overlay, sortingOrder 32000, DontDestroyOnLoad), TMP with the game's font, 64pt, top centre -120.
- [ ] Split minimaps (user): P1's map scaled down at the right edge of P1's half, P2's copy at the left edge of P2's half.
  - Game: MinimapCamera (minimapCamera, playerIcon, staticOrientation, A_RotationUpdated, enemy icons), texture shown
    by a HUD RawImage.
  - v0.22.0 (installed, SHA256 97E80383...F3794F; includes untested v0.20.1 collision + v0.21.0 pause): `P2Minimap.cs`.
    Find RawImage whose texture == minimapCamera.targetTexture; frame = largest ancestor <= 700 px without a Canvas;
    P1 frame x0.75, right edge at screen centre - 12; copy (scripts stripped) left edge at centre + 12, same y; P2 map
    camera = copy of minimap camera (scripts stripped) with its own RenderTexture, following P2 with P1's offset turned
    by the P1/P2 camera yaw difference; red copy of playerIcon follows P2. TEMP minimap_dump.txt.
  - [x] v0.22.0 tested 2026-10-17: user "minimaps are good, cameras are good, pause is good" (also covers v0.20.1
    wall collision and v0.21.0 pause). User: P2 walk speed and jump height differ from P1's with the same character.
- [ ] P2 speed/jump like P1 (user: same character, different feel).
  - Game: PlayerMovement (rigidbody, currentMaxSpeed, grounded, movementValues), PlayerMovementValues (maxRunSpeed,
    extraGravity, static GetMoveSpeedMultiplier(ECharacter), GetGravity), stats MoveSpeedMultiplier / JumpHeight.
  - v0.22.1 (installed, SHA256 D0478C7A...665BB7) also: character select re-entry (user: P2 closer + facing away) ->
    P1-P2 distance measured once per session and kept; P2 avatar faces the menu camera (renderer rotation +
    SetCharacter spawnDir = towards camera).
  - v0.22.1 test: user "none of the fixes worked". Log: 0.22.1 ran; run speed measured 8.1 -> 10.6 then dropped to 2.56
    (followed P1's recent pace); NO jump measured; only P1 paused (P2's Start did nothing: FindObjectOfType<PauseUi>
    skips the inactive pause menu, so P2 could only pause after P1 had paused once); only one character-select visit
    in that log (re-entry not visible).
  - [ ] v0.22.3 (installed, SHA256 3B8C7269...E8A8BE): run speed keeps the highest stable p90 (only rises); TEMP logs of
    P1 leaving the ground (3x) and rejected jumps with reasons (10x); PauseUi found via PauseHandler.pauseUi.
  - v0.22.3 test: **P2 pause works** before P1 paused (log "P2 paused the game" / "P2 pause ended, controls back to P1").
    Run speed stable (10.62). Jumps all rejected: "height 2.67, rise 0.42 s, fall 0.56 s, landed -2.08 vs take-off" -
    PlayerMovement.grounded stays true ~2 units into the jump (coyote/grace), so take-off y was measured late. Real P1
    jump (Megachad): h = apex - landing ~4.75, g_up = 2*2.67/0.42^2 ~30, v0 = sqrt(2*30*4.75) ~17, g_down ~30.
    P2 used v0 12 (h ~2.4). User: P2 also shows no jump animation. User: **pause label on main menu fixed**, **P2
    avatar faces the right way** on re-entry (distance fix not commented on).
  - [ ] v0.22.4 (installed, SHA256 4DB642CF...EEA332) also: P1 = the pad that pressed Play (PlayerJoin tracks the last
    pad with a new button press outside character select, before P2 joined; used if within 3 s of opening, else the
    game's currentController). User bug: if the other pad navigated, it became P1 and the Play pad had to join as P2.
  - v0.22.4 test: jump measured OK (h 4.75, v0 17.1, g ~30). P2 jump anim didn't play through: on the way down we wanted
    the "fall" state 998495048 but P2's controller kept going back to jump 125937960. Speed "doesn't match" (direction
    unknown; P2 target 10.57). P1 detection used the fallback (3 s window too short: camera fly-in).
  - [ ] v0.22.5 (installed, SHA256 3CDD17E2...C3CB65): air time = jump state only; stick full speed from 0.85 push
    (InverseLerp(deadzone, 0.85)); TEMP "[motion] speeds now: P1 / P2" log; P1-pad window 10 s + "Last pad pressed" log.
  - v0.22.5 test: user "didn't work"; log: P1 ~9.5-10.8 vs **P2 ~16.7** (target 10.61: mover overshoots); "Last pad
    pressed: nothing" (Steam Controller presses not seen by polling outside char select; fallback happened to be right).
  - User chose the **PlayerMovement route**. Game seam: PlayerInput -> InputState {moveHorizontal, moveVertical,
    rotationHorizontal, rotationVertical, jumping, crouching, holdingJump, holdingWallrun} -> PlayerMovement
    .SetBetterInput(InputState) + MovementTick(); PlayerMovement on 'Player' with MyPlayer, PlayerInput, Rigidbody,
    CapsuleCollider; fields playerCam, orientation, feet, head, rb, grounded; ResetState(ECharacter, dir); static
    Instance + events (A_Landed, A_Jumped, Shake, CameraBob, A_Grounded, ...).
  - [ ] v0.23.0 (installed, SHA256 2930170A...CB974D24): `P2Movement.cs`. Copy of P1's 'Player' (inactive holder; all
    Assembly-CSharp scripts except PlayerMovement removed; children not on the feet/head/orientation/playerCam paths
    removed; cameras/audio/renderers removed), placed at P2 body + P1's root-model offset, activated, PlayerMovement
    .Instance restored to P1, IgnoreCollision with P1's colliders, ResetState(P2 char) muted. Ticker.FixedUpdate ->
    FixedTick: InputState from P2 stick/A (jump queued per press, holdingJump), orientation = P2 yaw, playerCam = P2 cam,
    SetBetterInput + MovementTick with all PlayerMovement static delegates set to null (restored after); hooks on
    PlayerMovement Update/OnCollisionEnter/OnCollisionStay mute the same events for P2's copy only. P2Control: body
    follows the copy (BodyPosition), faces its velocity, animation from velocity/grounded (Animate shared), cc disabled.
    Config [Experimental] P2GameMovement (default true) -> false = old mover.
  - v0.23.0 test: "feels exactly the same" - the copy was made (25 camera/audio/renderer comps removed) but
    PlayerMovement.ResetState threw NullReferenceException -> fallback to the simple mover.
  - [ ] v0.23.1 (installed, SHA256 BD122C55...4FA5951): set-up steps each non-fatal: Initialize() (if !IsInitialized),
    new PlayerMovementValues + CreateMovement(rb, char) if movementValues null, ResetState; logs which parts exist
    (rb, collider, feet, head, orientation, playerCam, movementValues + maxRunSpeed vs P1, initialized).
  - v0.23.1 test: P2 can't move, stuck in jump pose. Log: "copy parts: rb False, collider False, ... movement values
    False ... initialized True"; 'movement values' + 'ResetState' failed (NRE); every tick NRE in PlayerMovement
    .CheckInput / GetSpeedHorizontal. rb/playerCollider are runtime-set -> empty in the copy; "inited" copied from P1.
  - [ ] v0.23.2 (installed, SHA256 E09D6158...A570C290): pm.rb / pm.playerCollider = copy's own components (rb non-kinematic,
    zero velocity), Initialize() always, then movement values + ResetState.
  - v0.23.2 test: all copy parts present, "P2 movement running: speed 0.00, grounded False"; P2 falls through the world
    (stuck in jump pose = airborne forever).
  - [ ] v0.23.3 (installed, SHA256 1F083E57...E5B4; + P2 body hidden (only renderers that were on) and replay delayed
    by PortalScaleTime until P2's portal is fully open - user saw P2 before the portal covered him): copy collider enabled/non-trigger, rb detectCollisions, layer =
    P1's; [move] logs P1 vs P2 collider/rb settings, all colliders of the copy, ground raycast under the copy; position
    log every 250 ticks; safety net: copy > 30 below P1 -> put back at P1's height near its x/z. Also PortalBack 1 -> 3.5
    (user: portals still intersect).
  - User asked why not use P1's own jump: PlayerMovement is single-player (Instance, MyInputManager input, global
    A_Jumped etc.). Possible experiment: clone PlayerMovement onto P2, restore Instance to P1, feed P2's input directly.
  - v0.22.4 jump part: jump measured with the LANDING height as baseline (h = apex -
    landing; g_up from the seen part of the rise; v0 = sqrt(2 g_up h); g_down = 2h/fall^2); P2 take-off: jump triggers
    (Jump/jump/JumpStart) + "in air" bools + Play(jump state) at once.
  - v0.22.1 details: `P1Motion.cs` measures P1: run speed = 90th percentile of grounded
    horizontal speed over 6 s (not during the portal walk); jumps landing within 0.6 of take-off: h, rise, fall ->
    take-off = (2h/rise^2)*rise, gravity up 2h/rise^2, down 2h/fall^2. P2 = measured x char multiplier ratio x
    MoveSpeedMultiplier ratio; jump speed x sqrt(JumpHeight ratio). Defaults 8 / 12 / 30 until measured. Logs [motion].
    + P2Pause: if PauseUi disappears (quit to main menu while paused) -> EndPause (banner off, controls back to P1);
    user saw "P2 PAUSE" stay on the main menu.
- Camera decision (user, 2026-10-17): **splitscreen first** (option 2: own half + own camera controls per player,
  HUD unchanged); Lego-style dynamic split (option 3) maybe later on top of it. Shared pull-back camera rejected.
  User: **left/right split** (P1 left half, P2 right half), not top/bottom.
  - v0.11.1 (installed, SHA256 F23FBFF1...E1F6C4): postfixes on ProjectileBase.Set and
    WeaponAttack.SuccessfullySpawnedProjectile move P2-attack projectiles by (P2 body - P1 model), once per
    projectile (pointer set, cleared on Set); logs which hooks run at all, first 5 moves, first 3 P2 SetAttack positions.
- Menu scene classes: `MenuCamera` (defaultTransform, characterSelectionTransform, currentTransform, camera;
  GoToCharacters/GoToMain/Update), `MainMenu.menuCamera`, `CharacterDisplay.playerRenderer` (PlayerRenderer:
  renderer SkinnedMeshRenderer, animator, hatTransform), `CharacterSpin.playerRenderer`.
  - v0.2.3 design (SHA256 17F887A7...C74E1): TEMP logs per pad GetAnyButton / any axis > 0.5 /
    enabled / isConnected, Rewired player 0 GetAnyButton and last active controller (on change only).
  - v0.2.2 design (installed, SHA256 3BFE1C5F...D73FE9): heartbeat ("Ticker: per-frame update is running",
    "Watching for P2" every 5 s), own edge detection via joy.GetButton(i), Start = raw index 7 or named start,
    TEMP Unity legacy Input.GetKeyDown(JoystickNButtonM) logging as an independent check. Ticker.Update made public.
  - v0.2.0 design: `PlayerJoin.cs`. Postfix on CharacterMenu.OnEnable/
        OnDisable sets InCharacterSelect and freezes P1's controller (MyInputManager.currentController at open).
        `Ticker` MonoBehaviour (BasePlugin.AddComponent) polls ReInput joysticks; logs every button press on
        non-P1 pads; a button named start/menu/options/+ registers P2: assigned to Rewired player 1 if the game
        defines one, else removed from all players. Logs Rewired player count + pad ownership on open and join.
- Character select classes: `CharacterMenu : Window` (static selectedCharacter, characterButtons, b_confirm,
  OnSelectCharacter, OnConfirmCharacter), `CharacterInfoUI` (info panel), `CharacterDisplay` (3D avatar,
  has playerRenderer), `CharacterSpin`, `SkinSelection`, `HatSelection`, `MainMenu`, `MapSelectionUi`.
- Rewired (obfuscated, but public API names survive): ReInput.players (playerCount, GetPlayer(id)),
  ReInput.controllers (GetJoysticks(), IsJoystickAssignedToPlayer, RemoveJoystickFromAllPlayers),
  Player.controllers.AddController(Controller, removeFromOtherPlayers), Controller.buttons[] with
  Button.name / justPressed. Dump: `notes/types/Rewired_Core.txt`.
      `OnlineBlock.cs` Harmony prefixes skip: SteamLeaderboardsManagerNew.QueueLeaderboardUpload/UploadLeaderboardScore,
      Leaderboards.UploadScore, SteamStatsManager.QueueUpload/TryUploadStats/SetCachedStats,
      SteamAchievementsManager.TryUnlockAchievement/QueueUpload/TryUploadAchievements, and Steamworks
      SteamUserStats SetStat/UpdateAvgRateStat/SetAchievement/ClearAchievement/IndicateAchievementProgress/
      StoreStats/ResetAllStats/UploadLeaderboardScore/AttachLeaderboardUGC. Bool calls return true (no retries).
      Log: `BepInEx\plugins\MegabonkSplitscreen\MegabonkSplitscreen.log`. Install: `tools/install.ps1`.
- Build note: net6.0 needs Microsoft.NETCore.App.Ref 6.0.36 (NuGet cache). First restore also pulled
  ASP.NET + WindowsDesktop ref packs (unapproved, unused); `DisableTransitiveFrameworkReferenceDownloads` added.

## Single-player assumptions found (class map)
- `MyPlayer.Instance` (static): the player object. Spawn(), StartPlayer(ECharacter, dir), OnDamage, OnLevelUp.
- `PlayerMovement.Instance` (static), `PlayerInput`, `PlayerRenderer`, `PlayerFx`, `PlayerSfxs.Instance`.
- `GameManager.Instance`: fields `player` (MyPlayer), `playerCamera` (PlayerCamera); GetPlayer(),
  GetPlayerMovement(), GetPlayerInventory(), OnDied(), A_GameOver, A_RunStarted, A_StageStarted.
- `PlayerCamera.Instance` (static); also CameraMovement, CameraFollowBean.
- `MyInputManager`: static `player` (a **Rewired Player**), static GetButton/GetAxis(string action) used
  everywhere. Only one Rewired player in use; Rewired supports more (good path for P2 input).
- `PlayerInventory`, `PlayerHealth`, `PlayerXp`, `PlayerStatsNew`, `WeaponInventory`, `ItemInventory`, `TomeInventory`.
- `PickupManager.Instance`, `EnemyManager.Instance`, `EnemySpawner`, `EnemyTargeting`, `EEnemyTargetingMode`.
- UI: `UiManager.Instance`, `UpgradePicker`/`UpgradeButton` (level-up), `PauseHandler`/`PauseUi`,
  `DeathScreen`, `XpBarUI`, `XpAndGoldHUD`, `InventoryHud`, `ItemsHud`, `MinimapPlayerIcon`.
- `Utility`-ish: `GetPositionAroundPlayer(float)` (line ~16620 of the dump).

## Online / leaderboards (to block)
- `SteamLeaderboardsManagerNew` (static): QueueLeaderboardUpload, UploadLeaderboardScore, CheckUploadQueue.
  Has `cheaters` set + `leaderboardBannedPlayers` board: the game tracks banned/cheating players.
- `Assets.Scripts.Steam.Leaderboards.UploadScore(int)` (older path), with score hashing.
- `Assets.Scripts.Steam.LeaderboardsNew.Sus.Check()` / `CheckMods(ref string reason)`: the game's own
  mod detection. **Leave it alone** (no hiding the mod); instead block uploads so nothing modded is submitted.
- Also present: SteamAchievementsManager, SteamStatsManager (stats upload), SteamRichPresenceManager,
  SteamTimelineManager, DiscordManager. Decide with the user what to do with achievements/stats.

## Findings
- Game folder: `H:\SteamLibrary\steamapps\common\Megabonk` (developer "Ved")
- Backend: **IL2CPP** (`GameAssembly.dll`, `il2cpp_data\Metadata\global-metadata.dat`, no `Managed\`)
- Unity **2023.2.22f1** (UnityPlayer.dll version + globalgamemanagers header)
- Game code lives in `Assembly-CSharp.dll` (compiled into GameAssembly.dll)
- Input: **Rewired** (Rewired_Core, Rewired_Windows, DirectInput + WindowsGamingInput). Rewired has
  built-in multi-player support (Player 0/1, controller assignment), which should make P2 input easier.
- Online: Steamworks.NET (`steam_api64.dll`, likely leaderboards/achievements), Discord Game SDK. To disable.
- Other libs: Addressables, Localization, TextMeshPro, Unity UI, Post-processing, MK.Toon, Coffee.UIParticle
- Build tools on PC: .NET SDK 3.1 and 5.0 only. BepInEx 6 IL2CPP plugins target net6.0, so a newer SDK is needed.

## Ruled out
_(none yet)_

## Plan
1. Mod loader: BepInEx + Harmony (pending backend check; ask before downloading)
2. Disable leaderboards / online submissions
3. Spawn player 2 on controller 2
4. Two cameras, half-screen viewports
5. Enemies, XP and pickups work with both players
6. Per-player HUD and level-up menu
7. Menus / pausing
8. Death and game-over rules
9. Performance check with two cameras

## v0.23.4 (SHA256 2BCA977F...2D82)
- v0.23.3 log: P2's PlayerMovement copy spawned inside P1's solid DetectPickups sphere (the ground ray hit it 0.71 below), then fell with vel (-14.5, -29.3, 0.7) and no input. Safety net kept putting it back.
- Diagnostics added (TEMP): everything under the copy (RaycastAll), Player-Ground layer rule and whatIsGround for P1/P2, solid colliders near the spawn, the first 12 OnCollisionEnter contacts of the copy, and the first 30 ticks of position/velocity.
- The P1/P2 IgnoreCollision pairs are applied again after the collider set-up, in case those changes reset them.


## v0.23.5
- v0.23.4 log: the copy starts grounded and simply falls straight down through 'CombinedCollisionMesh' (layer Ground) with gravity only, no sideways push. Physics.GetIgnoreLayerCollision(Player, Ground) = true, so in the layer matrix Player doesn't collide with Ground, yet P1 stands on it. whatIsGround 4352 is the same for both.
- Theory: P1's collision with the ground comes from per-object includeLayers/excludeLayers (collider/rigidbody). Fix: copy P1's include/exclude layers and layerOverridePriority onto P2's capsule and rb; log P1's values and every P1 collider's layer.


## v0.23.6
- v0.23.5 log: P1's include/exclude layers are all 0 (collider and rb), same as the copy; P2 still fell. OnCollisionEnter never fired for the copy (no contacts at all).
- Now: P2's capsule/rb includeLayers forced to whatIsGround | whatIsGroundOnly | Ground (priority 10). Logs what P1 stands on (RaycastAll under P1 with layer indices and collider types), P1's vel/grounded/constraints, every layer the matrix ignores for the Player layer, and per tick P1's y/vel y/kinematic/gravity next to P2's.


## v0.23.7
- v0.23.6 log: forcing includeLayers 4352 (Ground = layer 8) changed nothing. Physics.GetIgnoreLayerCollision(7, l) returned true for all 32 layers, even though P1 (layer 7) stands on Ground (layer 8) with gravity on, vel y 0/-0.00, and P2/P1 used to collide -> that icall result looks unreliable under IL2CPP, so it's ignored. P2's grounded goes True->False after 6 ticks (groundCancel) because OnCollisionStay never fires: no contacts at all.
- Clue: a ray from above P2's copy did NOT hit the copy's own capsule, while a ray under P1 DID hit 'MegabonkSplitscreen_P2Player' -> the copy's physics shape may not be at its transform.
- Now: logs transform vs rb.position vs collider bounds (setup + each early tick), Physics.SyncTransforms, collider off/on toggle, rb.position/rotation set to the transform, SyncTransforms again, IgnoreCollision with P1 re-applied.


## v0.23.8
- v0.23.7 log: CONFIRMED the copy's rb/collider were still at P1's position (237.78, 2.23, 120.49) while its transform was at P2's spot; SyncTransforms + rb.position fixed that (rb, bounds and transform agree every tick). P2 still falls straight through, no OnCollisionEnter ever, not even against P2's old body (layer Player, inside the copy).
- Game code has no contact modification / IgnoreCollision / simulationMode use (types dump).
- Now: logs Physics.GetIgnoreCollision for P1-ground, P2-ground, P2-oldBody; matrix sanity (Default-Default, Ground-Player); rb.SweepTest down for P1 and P2; ground collider priority/include/exclude. Control experiment: two fresh capsule rigidbodies (Player layer, Default layer) dropped beside P2, result logged after 3 s and then destroyed.


## v0.23.9
- v0.23.8 log: every bool-returning physics query (GetIgnoreCollision, GetIgnoreLayerCollision incl. Default-Default) returned True -> those icall bools are unreliable under this IL2CPP build; earlier 'matrix ignores everything' was bogus. Control: a fresh capsule rigidbody on the Player layer LANDED beside P2; one on Default fell through (Default doesn't collide with Ground, as expected). So the Player layer collides with Ground; only the copied rigidbody/collider never makes contact.
- Now: the copy's Rigidbody and CapsuleCollider are destroyed and replaced by new ones with P1's settings (radius/height/centre/direction/material, mass/drag/interp/cd/constraints) before Initialize; after ResetState: isNoclipping=false, Freeze(false), SetKinematic(false), physics flags re-applied; logs whether pm.rb/playerCollider are still the new parts.


## v0.23.10
- v0.23.9 log: fresh Rigidbody/CapsuleCollider (NoFriction material, P1's settings) are what pm.rb/playerCollider use, layer Player - and P2 still falls straight through, while the fresh test capsule beside it lands. So the game's PlayerMovement on the copy is doing something per tick.
- Bisect: for the first 100 FixedTicks the copy's PlayerMovement is disabled and not ticked (does it land on its own?), then back on. Logs y/vel/layer/include/exclude/enabled/trigger/kinematic/detect/noclip/scale every 10 ticks before and every tick for 40 ticks after.


## v0.23.11 (SHA256 C80197A8...8937)
- ROOT CAUSE of P2 falling through the world: v0.23.10 log showed the copy's localScale = (0,0,0) -> zero-size capsule, no contacts at all (fell even with PlayerMovement disabled). Bounds centre/raycast hits looked normal, which hid it. P1's root was copied while its scale was 0 (portal entrance).
- Fix: copy.localScale = P1's scale if normal, else 1 (before SetActive, so Awake sees it); after ResetState make sure pm.playerScale and the transform aren't zero; logs scale/playerScale/crouchScale for P1 and P2. Bisect switched off.
- Lesson: also check transform scale when a copied physics object behaves as if it had no collider.


## v0.23.12 (SHA256 7B9B9E8F...FA91A)
- v0.23.11 log: P1 root scale was (1,1,1) when copied, yet after set-up the copy's localScale, playerScale and crouchScale were all (0,0,0) (P1's are 1/1) -> something during SetActive/Initialize/ResetState zeroes them; my fallback copied the zero back.
- Now: playerScale/crouchScale/localScale taken from P1 after set-up; every FixedTick restores the scale if it drops to ~0 (logs the first 5 times with the tick); TEMP scale log after SetActive and after each set-up step.


## v0.24.0
- v0.23.12 CONFIRMED by user: P2 stands, walks and jumps with the game's PlayerMovement; speed/jump match P1 (Megachad). Log: the scale was zeroed by ResetState (sets localScale from playerScale, which is filled at runtime and was 0 on the copy). Now playerScale/crouchScale are set from P1 before ResetState too.
- Open: (1) sometimes a 'super jump'; (2) jump animation stays in the start pose; (3) P2 Megachad aura flickers (looks like z-fighting).
- The animator parameter names were guessed so far (Animator.parameters / GetParameter fail: 'Method unstripping failed'). Read-only scan of global-metadata.dat string literals gave candidates: VelocityX/Y/Z, grounded, isMoving, isRunning, isJumping, IsJumpHappened, Airborne, falling, landing, Sliding, IsSliding, IsCrouching, Wallrunning, isGrinding, isDashing...
- TEMP [params]: P1's animator GetFloat/GetBool for those names at 10 Hz for 90 s, logged on change with P1's world/local velocity, grounded and state -> tells which parameters the game drives and how.
- TEMP [jump]: every P2 jump's height/take-off vy/input sent/aerial+used jumps/onRamp/surface; P1's inputState.jumping pulse length in physics ticks.
- Aura: P2's always-on attacks are placed at P1's offset from the feet (P1's own aura of that weapon if P1 has it, else P1's AuraAttacks object) instead of exactly at the feet (logs the offset).


## v0.25.0
- v0.24.0 log: P1's real animator parameters = bools 'grounded', 'moving', 'jumping' (GetBool works; floats like VelocityY don't exist/stay 0). 'jumping' is on ~0.1 s at take-off; 'grounded' turns true just before landing (land state 998495048). P2 had 'jumping' on for the whole air time (guessed AirBools) -> controller restarted the jump = stuck start pose.
- P2 jumps: heights 2.74 / 4.02 / 22.29 (vy 13.3 / 16.0 / 37.3) vs P1 4.75 (~15). P1's jump input is on for 4 ticks. Cause: ReadA used PollControllerForFirstButton (one button only) -> A read as released/pressed mid-hold -> extra jumps during the grounded lag (super jump) and cut holdingJump (low jumps).
- Aura: P1's aura offset from the feet is 0.01 too, so not z-fighting; likely P2's aura per-frame/stat code ran outside P2's stat context (size alternating P1/P2).
- Changes: ReadA/PadHeld via PollControllerForAllButtons; game-movement animation drives only grounded/moving/jumping(0.1 s pulse) and lets the controller transition (guessed params cleared once); ConstantAttack subclasses' Update/FixedUpdate/LateUpdate/OnStatUpdate/OnWeaponStatUpdate/UpdateSize/UpdateCooldown run in P2's context for P2 weapons.
- NEW (user QOL): P2 confirms in character select with A on the already-picked character or Start (1 s guard after binding): CharacterMenu.OnConfirmCharacter(P1's selectedButton); after P2 releases the buttons, menu controls go to P2 (P2LevelUp.GiveControlsToP2); back to P1 when character select reopens or MyPlayer exists (run started).


## v0.25.1
- v0.25.0 crashed at launch (coreclr c0000005 during Plugin.Load -> AddComponent<Ticker> -> new GameObject). Dump (H:\CrashDumps\Megabonk.exe.24004.dmp, clrstack): the native call landed in DMD<PlayerMovement::Update>. Cause: IL2CPP merges methods with identical machine code (empty ones share one 'ret' function); PlayerMovement.Update is evidently empty, and some of the 34 new ConstantAttack hooks were on methods sharing that native function -> two Harmony detours on one native function -> runtime crash (and the hook ran for unrelated calls).
- Fix: ConstantAttack hooks moved to P2Weapons.ApplyConstantHooks, applied last; each candidate's native pointer (MethodInfo->methodPointer via Il2CppInteropUtils) is compared with every already-patched method (Harmony.GetAllPatchedMethods) and with the other candidates; shared ones are skipped and logged. csproj: + Il2CppInterop.Common reference.
- Lesson: before hooking small/possibly-empty IL2CPP methods, check that the native function isn't shared.


## v0.25.2 (SHA256 0801735D...E834)
- v0.25.1 launched (29 constant hooks, 5 OnWeaponStatUpdate skipped as shared native code) but froze when P2 took control: heartbeat stopped right after '[stats] P2 start'. Cause: endless loop - P2's aura hook -> EnterP2 -> p2Dict null -> Recompute -> ForceUpdateStats fires the stat event -> P2's aura OnStatUpdate hook -> EnterP2 -> Recompute ...
- Fix: Recompute is non-re-entrant (Recomputing flag); ConstantPrefix does nothing while Recomputing.
- Confirm: the carried-over pick sat under P2's cursor, so the first A confirmed (log: 'P2 confirmed ... P2 Calcium' then 'P2 picked Megachad'). A now confirms only a pick made during this visit; Start still confirms. The menu closes ~1.7 s after OnConfirmCharacter, so the handover now waits up to 5 s for character select to close.


## v0.25.3 (SHA256 576748DA...7584)
- v0.25.2: P2 confirm + map-select handover WORKED ('P2 menu control ended: the run started'). Same freeze right after '[stats] P2 start' -> the Recompute loop wasn't (all of) it.
- TEMP watchdog in FreezeDiag: background thread; after the first frame, if the Ticker's Update hasn't run for 15 s -> Environment.FailFast -> WER dump in H:\CrashDumps; read main thread (setthread 0, clrstack -f).


## v0.25.4
- v0.25.3 watchdog dump (Megabonk.exe.31256.dmp, main thread): Ticker -> P2Body.Replay -> P2Control.Take -> P2Weapons.Start -> WeaponInventory.AddWeapon (P2's starting weapon) -> DMD<WeaponBase::UpdateStat> -> DMD<CombatAura::OnStatUpdate> (the new always-on context hook) -> il2cpp_runtime_invoke, stuck in the game's native aura code. The watchdog works.
- Fix: ApplyConstantHooks no longer applied (code kept). Back to v0.24 behaviour for always-on attacks.
- TEMP [aura]: per frame, on change (max 60 lines), P2's and P1's CombatAura radius/oldRadius/scaleTimer/localScale/active/enabled -> find what the flicker is before fixing.


- User (v0.25.4): ignore P2's aura flicker for now (visual only). Parked; the TEMP [aura] log stays until the cleanup pass. Don't re-add always-on hooks (freeze).


## v0.26.0 - Lego-style merge camera (user's design)
- User choices: merged = one camera behind the midpoint, pulled back to fit both, either right stick turns it; split = the existing fixed left/right split; split at ~15 units. Shader transition: user has the Unity Editor (asset-bundle shader possible later) but wants the divider wipe tried first.
- P2Camera: config [Camera] MergeWhenClose (true), SplitDistance 15, MergeDistance 12 (hysteresis). merge 0..1 moves over 0.6 s (unscaled), SmoothStep. Ticker.LateUpdate -> P2Camera.LateUpdate (after the game's PlayerCamera.Update): P1 cam position lerps to the shared pose (rotation untouched = P1's, so P1's movement stays camera-relative); P2 cam lerps pos + slerps rot to it; divider 0.5 -> 1 (P1 rect width, P2 rect x), P2 camera disabled when fully merged. Shared pose = head midpoint + P1 rotation * P1's camera-local offset * (1 + 0.6*sep/SplitDistance), wall-avoided. Merge only in Player3rd state, P2 alive, height difference < SplitDistance.
- Shared turning: P2's stick added to P1's PlayerInput.desiredCameraRotation (yaw component detected once by comparing with the camera euler; logged '[cam] P1 camera rotation ...'). While merging, P2's yaw/pitch converge to P1's (P2 moves relative to the shared view; continues from it after splitting).
- P2Minimap: both maps follow P2Camera.Divider every frame (P1 right edge at divider-gap, P2 left edge at divider+gap); P2's hidden when divider > 0.98.
- TEMP [cam] logs: merging/splitting with distance.


## v0.26.1 (SHA256 6DAB4941...2401)
- v0.26.0: camera 'went flying off'. Cause: P1's camera-local offset was re-measured every frame from P1's camera AFTER the previous frame's override (the game moves its camera smoothly from where it is), then multiplied by the pull-back again -> exponential runaway. Also split at 6.3 apart (a can-merge condition went false; unknown which).
- Fix: offset measured only while merge == 0 (clean), kept during merging; P1's blend starts from its clean follow pose (head + rot * clean offset, wall-avoided) instead of the camera's current position. [cam] log now includes P2 dead / camera state / height difference.


## v0.26.2 (installed, SHA256 30E84E15...854B)
- User: merge camera 'pretty nice'. Super jumps still there: log shows ALWAYS take-off vy 37.3 / height 22.29 vs normal 13.3 / 2.74 (normal = P1's jump measured from the first non-grounded tick, which lags ~6 ticks) -> a fixed extra ~24 upward, not extra presses. Cause (likely): P2's visible body has an enabled kinematic collider (P2Health, layer Player) and is teleported onto the movement copy every frame; depenetration launches the copy. Fix: Physics.IgnoreCollision between all body colliders and the copy's colliders (logged pair count). The 'splitting 4.6 apart, height difference 15' lines were these super jumps.
- User: change the wipe direction -> chose 'from the middle outward': three viewports while transitioning: P1's own view [0, side] (new inert P1SideCamera copy), the shared view in the middle (the game's camera = Camera.main, shared pose), P2's view [1-side, 1]; side = 0.5*(1-s). Merged: game camera full screen, side/P2 cameras off. P1's minimap rides P1's view's right edge and lerps back to its original game position (saved when first scaled) by s; P2's rides P2's view's left edge, hidden at s >= 0.98.



## v0.26.3 (installed)
- v0.26.2 log: body<->copy IgnoreCollision applied (1 pair) but EVERY P2 jump is now take-off 37.3 / height 22.29 -> not the body. TEMP per-tick trace of the first 12 physics ticks after P2's jump input (y, vy, grounded, input jumping/holding, readyToJump, usedJumps, pm.jumping, fixedDeltaTime) and of P1's first 12 ticks (y, vy, grounded, input, readyToJump, usedJumps).
- User: split views looked shifted (not centred on the players) after merging. Cause: the game moves its camera relative to its current position, so the shared-pose override stayed in it (and P2's view copies P1's offset). Fix: render-only override - LateUpdate saves the game's position, sets the shared pose; a Ticker coroutine (WaitForEndOfFrame, WrapToIl2Cpp) puts the game's position back after drawing. The offset is measured every frame again.



## v0.26.4 (SHA256 C4A1EF6A...A853)
- User: split camera looks nice after the v0.26.3 render-only override (confirmed).
- v0.26.3 trace: P1 jumps normal (vy ~15.6 at tick 0, jumping input 4 ticks, readyToJump false for ~10 ticks). No P2 trace at all = P2's jump input was never true; user: 'I didn't click anything' -> the super jumps are spontaneous launches (always vy 37.3, every few s), not jumps. Suspect PlayerMovement.CheckStuck (unstuckForce/stuckTimer/stuckTimerMax).
- TEMP [launch]: logs when P2's vy rises > 15 in one tick (stuckTimer before/after, max, unstuckForce, pushed, ladder, wallclimb, dash, lastFallSpeed, ramp, pushMultiplier, P1's stuckTimer) and the stuck timer while it runs (P2 vel, grounded, stick; P1's).


## v0.26.5 (SHA256 0604B75D...0F29)
- v0.26.4 log: (1) all later [launch] lines were my own 30-below safety net (user walked P2 off a cliff -> 'kept moving upwards'); (2) P2's stuckTimer counted 0.28 -> 0.90 -> 1.0 while P2 stood still (vel 0, grounded, no input), P1's stays 0; unstuckForce 2000 / mass 60 = ~33 extra up = the 37.3 'super jumps'. (Why the copy counts as stuck is unknown - game code not visible.) Also: P2's real jumps come through without input.jumping ever reading true (holdingJump path?) - heights normal.
- Fix: Pm.stuckTimer = 0 before and after every MovementTick (no unstuck push for P2). Safety net now only at 400 below P1, puts P2 next to P1.
- Slope lean (user: P1 leans on slopes, P2 doesn't): P2's body rotation = FromToRotation(up, smoothed ground normal) * heading; ground normal = Pm.normalVector when grounded, else up; smoothing 1-exp(-10 dt). TEMP [slope] log compares P1 model lean / ground angle / renderer.smoothedNormal with P2's.


## v0.27.0 (SHA256 562D3D73...4A4D)
- User (v0.26.5): no more super jumps (unstuck push off = confirmed fix). No [slope] lines (P1 never on a >8 deg slope) - lean match still to check.
- User: far from P1, enemies only spawned around P1. Game: static SpawnPositions.GetEnemySpawnPosition / GetEnemySpawnPositionBiased pick around the one player; also GetEnemySpawnPositionAroundPoint(pos, minR, maxR, attempts, onlyGround, fromHeight), GetSpawnDistances(out min, out max). Summoners in Assets.Scripts.Game.Spawning.New (BaseSummoner.SpendCredits etc.).
- New P2Spawns.cs: postfix on both; when players > max(25, game max spawn distance) apart and P2 alive, every other result is replaced by GetEnemySpawnPositionAroundPoint(P2 body, min, max, 10, true, 20) (INVALID -> keep P1's). Config [Spawning] SpawnAroundP2 = true. Hooks skipped if the native code is shared (new P2Weapons.IsNativeCodeHooked). TEMP [spawn] logs (first 8 moves, failures).


## v0.27.1
- User: far apart the spawns shouldn't look thinned out. While Apart() (same rule as the redirect): postfix x SpawnMultiplierWhenApart (config, 2) on BaseSummoner.GetCreditsPerSecond and GetNumTargetEnemies (base + Stage/Swarm/SpecialSkeleton overrides), only for StageSummoner/SwarmSummoner/SpecialSkeletonSummoner instances (not boss/challenge); static maxEnemiesPerSecond/PerCycle raised while apart and put back (checked every 0.5 s from the credits hook; reset on character select close).
- Hooks only on native code that is unique in all of Assembly-CSharp (new P2Weapons.IsNativeCodeUnique: pointer -> count map built once, logged 'Native code map') and not already hooked; skipped ones logged.


## v0.27.2
- User (v0.27.1): enemies now spawn near P2 (confirmed).
- User: after P2 died and the run was restarted, P2 came out of the portal and vanished. Log: 'P2 health 0/120 (down)' at the restarted run's entrance - a pause/death-menu restart skips character select, where the per-run resets were. Fix: PlayerJoin.ResetRun (P2Health, P2XpBar, P2LevelUp, P2Inventory, P2Spawns) also on the game's static event MapController.A_NewRunStarted (subscribed with Delegate.Combine at load; no hook). Logs 'New run started (game event)'. Next-stage portals don't fire it.


## v0.27.3 (SHA256 905A17DB...F748)
- User: a round starts split, should start merged. Causes: P2Camera.Start set the split rects at P2's portal, and the Portal camera state forced wantMerged = false (then a 0.6 s wipe after the entrance).
- Fix: Start (merge enabled) begins merged (merge 1, P1 full rect, P2 cam off, poseWeight 0); non-Player3rd camera state no longer decides (keeps current); new poseWeight eases the shared pose from the game's own camera position (portal camera) to the midpoint view over 0.6 s once in Player3rd (and back if the game takes over its camera again).


## v0.27.4 (built, folded into v0.27.5)
- User: fix P2's compass. Minimap frame 'Minimap' {RawImage, MinimapUi} > 'Border' > N/S/E/W letters (TMP); MinimapUi (stripped from P2's copy) turns them with P1's camera, so P2's stayed fixed while P2's map image turns by the yaw delta.
- Fix: every frame P2's Border.localRotation = Rz(sign * yawDelta) * P1's; letters copy P1's local position/scale, rotation upright if P1's are (world z ~ 0) else turned along. sign learned from P1 (N letter's angle around the border centre vs P1 camera yaw changes, voting), logged '[compass]'.



## v0.27.5 (installed)
- User: colour the level-up screens blue (P1) / red (P2). LevelupScreen.Open postfix now runs for both players (when P2 has joined): title 'PLAYER 1/2 LEVEL UP!'; TintWindow: Graphics under LevelupScreen.window (not TMP text, not under UpgradePicker.buttons - rarity colours, user agreed - nor b_skip/b_refresh/b_banish/b_leave) with hue 0.22..0.55 and saturation >= 0.12 get hue 0.60 (P1) / 0.0 (P2), same s/v/alpha; originals remembered per pointer. TEMP: first opening writes levelup_colours.txt (path, type, colour, hsv, tinted). ModLog.Dir added.



## v0.27.6 / v0.27.7 (installed, SHA256 E84046BA...1F47)
- User: P2 Megachad picked up Revolver, it doesn't fire from P2. Log: 3 fires, each set up + attack moved to P2; first volley's projectiles moved/re-aimed (Goblin 11 away); later fires unlogged (caps). P1 was ~90 away. Suspect: ProjectileBase.TryInit/target search relative to P1 (no enemies near P1 -> no shots). TEMP [shots]: before each P2 fire, projectiles set up vs SuccessfullySpawned since the previous fire, enemies within 30 of P1 / P2, separation (30 logs).
- User: far apart, enemies spawned only around P2; wants spawns on both players at any distance. Likely cause: 'every other call' toggle aligned with the game calling GetEnemySpawnPosition more than once per enemy. Now a coin flip per call (own System.Random) at any distance; extra spawns (x2) still only while apart. TEMP [spawn] tally per 40 spawn positions.


## v0.27.8 (SHA256 CF079CE3...DEBC)
- User: P2's Frostwalker ring offset. Log: 'offset from the feet (6.00, 2.22, 0.00) (P1's AuraAttacks)' - P1 didn't own Frostwalker, fallback used the AuraAttacks object's position (6 to the side, 2.2 up). Fix: fallback = the feet (+0.01), like P1's own Aura (offset 0.01).
- [shots] (v0.27.7): P2's FireStaff fires normally at 47-85 apart (set 1 / spawned 1 per fire; a few early 0-spawned). Revolver not retested (user got no gun).
- [spawn] tally: 13 P1 / 27 P2 per 40 with the coin flip at 74 apart - watch more tallies before changing.


## v0.27.9 (SHA256 193BF4AF...306B)
- User: P2's Sword slashed at P1. P2 fires Sword (1.25 s), attack set up/moved (logs capped). Melee projectiles (ProjectileMelee etc.: forwardOffset/upOffset, MyUpdate/MyFixedUpdate/StepMovement) re-place themselves at THE player every frame, undoing the one-time move.
- Fix: p2Projectiles set (from ProjectileBase.Set for P2 attacks); postfix FollowP2 on MyUpdate/MyFixedUpdate/StepMovement of ProjectileMelee/HeroSword/CringeSword/Katana/Scythe/Dexecutioner/Whirlwind (only unique, unhooked native code): if the projectile is nearer P1 than P2 and within 20 of P1, map it into P2's frame (p2 + yawTurn*(pos - p1model), rotation turned); HeroSword/CringeSword movingProjectile too. TEMP [melee] logs (5). Damage zone may still be checked at P1 if computed inline - check with the user.


## v0.27.10 (folded into v0.28.0)
- User: when P1's camera is pushed in by a wall, P2's camera moves in too (P2 copies P1's camera-local offset). Fix: FreeOffset remembers P1's free distance; a shorter measured offset counts as a wall only if a sphere cast from P1's head (camera-collision mask, P1's colliders ignored) hits within the remembered distance -> keep the remembered length; otherwise it's a zoom change and is taken over. Used for P2's view and the merged shared pose.


- v0.27.10 also: user - boss health bar at the top was covered by the minimaps; wants boss bars at the bottom, stacking up. No boss-bar class in the game; new BossBars.cs: twice a second finds EnemyHpBar objects on screen-space canvases, re-anchors their container (parent) to the bottom (anchor/pivot y 0, y = 60) and, if it has a VerticalLayoutGroup, Upper->Lower alignment + reverseArrangement; TEMP bossbar_dump.txt + log of the old layout. csproj: + UnityEngine.TextRenderingModule. Reset with the run.



## v0.28.0 (SHA256 7D409A99...98AA) - includes v0.27.10 (camera wall fix, boss bars at the bottom)
- User: P2 interacts with vases, chests (item to P2, P2 drives the chest menu) and greed shrines. User choices: P2's own items, stats first (procs/timed item effects later); greed shrine effect to whoever used it.
- Game: DetectInteractables (interactableRange, whatIsInteractable, currentInteractable, TryInteract), BaseInteractable.Interact/CanInteract/GetInteractString/outline; InteractablePot, InteractableChest (-> ChestWindowUi encounter: Open/OnClose, Take/Discard/Banish, ChestOpening), InteractableShrineGreed (EffectStat -> StatInventory.ChangeStat). PlayerInventory has itemInventory, statInventory.
- P2Inventory: own ItemInventory + StatInventory (created with the progression), swapped into P1's inventory during Recompute (P1's items/shrine changes no longer count for P2); AsP2(what, items, stats, action); SwapInItems/SwapOutItems (+GuardsOn/Off, P1 ForceUpdateStats, logs P2's items).
- New P2Interact.cs: nearest CanInteract interactable within P1's interact range of P2 (OverlapSphere, P1's layer mask) -> outline + red 'P2: <interact string>' prompt at the bottom of P2's view; button = P1's Rewired joystick binding for 'Interact'/'Interaction'/'Use' (logged) else x/square; greed shrine via AsP2(stats); chest: 3 s pending -> ChestWindowUi.Open postfix swaps in P2's items, item name red, controls to P2 after the button is released; OnClose postfix restores controls and items. Hooks only on unique native code.
- Pending from user: exploding enemies knock P1 back (with 'knocked' vfx) but not P2. Free 'OpenChest' pickups (trigger with P1's collider) not handled for P2. P2 HUD has no items row yet.


## v0.28.1 (SHA256 55BCA946...83B0)
- v0.28.0 confirmed by user/log: P2 breaks vases, opens chests (items went to P2: SlipperyRing, OverpoweredLamp), button X from P1's 'Interact' binding.
- User: P2's prompt should sit over the object like P1's. Now an inert copy of DetectInteractables.uiParent (text + glyph; game scripts stripped; scale = toScale), text = GetInteractString in red, placed each frame at P2 camera (P1's when merged) WorldToScreenPoint(object + GetOffset()) (world-space canvas: at the point, facing the camera). TEMP: interact_prompt_dump.txt, '[prompt]' line comparing P1's prompt position with the same calculation.


## v0.28.2 (installed)
- v0.28.1: user saw no P2 prompts. Dump: P1's prompt = 'InputText' {RawImage, HorizontalLayoutGroup, LookAtCamera, CanvasGroup} (inactive at copy time) under world-space canvas 'InteractText'; children GlyphContainerNew/Glyph, 'Text (TMP)'. The copy kept the faded-out state. Fix: CanvasGroup.alpha = 1 (at creation and every frame), zero scale -> 1; TEMP '[prompt] P2's shown' state log once.

- v0.28.2 also: P2's prompt fades/scales in like P1's (DetectInteractables.fromScale -> toScale over animateOverTime, CanvasGroup alpha with it, restarts per target). New Billboards.cs (user: pickups like the clock/shield face P1's camera, flat in P2's view): before P2's camera draws, every active LookAtCamera is turned by rot(P2 cam) * inverse(rot(P1 cam)) (yaw only if xzOnly), restored after; via Camera.onPreCull/onPostRender and RenderPipelineManager.begin/endCameraRendering (logs which fires).



## v0.28.3 / v0.29.0 (installed, SHA256 EA89B0CD...C835)
- v0.28.2 log: Billboards fire via Camera.onPreCull (built-in pipeline; RenderPipelineManager callbacks 'Method unstripping failed'). User: a heart pickup still looked off in P2's view; chest prompt gold amount red (game: red = can't afford). v0.28.3: P2's prompt keeps the game's text colours; Billboards also turn non-spinning Pickups (animateRotation false, not pickedUp) for P2's camera (logs which).
- User: exploders knock P1 back (with 'knocked' vfx), not P2. Game: EffectManager.ExploderEnemy(enemy) -> ExploderDeath coroutine -> WarningSphere(position, radius, time, action) -> blast, PlayerMovement.PushPlayer(pushForce) on P1. v0.29.0 P2Explosions.cs: postfixes on ExploderEnemy (flag), WarningSphere (if <2 s after and within 15 of the exploder: schedule check at +time with radius), PlayerMovement.PushPlayer (learn P1's push strength/up share right after a blast); P1's hp drop right after a blast = blast damage. P2 within radius: P2Movement.Push (PushPlayer on P2's copy, muted) away from the centre with P1's strength (default 25), P2Health.TakeDamage (P1's blast damage, default 20). TEMP [blast] logs. The 'knocked' vfx not reproduced for P2.


## v0.29.1 (SHA256 31168A30...8DD0)
- User: P2's Knight sword (ProjectileMelee) shows at P2 but doesn't damage. v0.29.0 log: melee follow hooked 8 methods; skipped as shared native code incl. ProjectileMelee.MyFixedUpdate/StepMovement (also Katana all, Scythe/Dexecutioner Fixed+Step, Whirlwind). The hit check runs in the fixed update with the slash back at P1.
- Now also PREFIX FollowP2 on the hit checks of the melee types: CheckZone, CheckColliderCustom, CheckSpawnCollision, CheckCollision (only unique native code); hooked list logged. TEMP '[melee] P2 <type>.<method> runs (slash at, P2 at)' once per method.


## v0.29.2 (SHA256 73BD8288...4D20)
- User: P2's vase/Moai prompt text broke into 3 lines. The original 'Text (TMP)' has a TextSizer (stripped from the copy) that widens it. Now on text change: enableWordWrapping = false, sizeDelta.x = GetPreferredValues(text).x.


## v0.30.0 (SHA256 ABDBDC73...2D77)
- User: sword (melee hit-check prefixes) and prompts (no wrap) confirmed good.
- User: P2 activated a charge shrine, P1 navigated its window. Game: ChargeShrine (BaseInteractable; trigger zone OnTriggerEnter/Exit, Complete, Reward) -> EncounterWindows.AddEncounter(RandomStats) -> EncounterUi.Open/OnClose (generic stat reward window; EffectStat -> statInventory). New P2Rewards.cs: MarkP2 (3 s) from P2Interact (any interactable except pots/chests) and ChargeShrine.Reward prefix (P2 nearer than P1); P2LevelUp's AddEncounter prefix forwards non-level-up types to P2Rewards.OnAddEncounter (owner list); LevelupScreen.Open for a P2-owned non-level-up type (Moai...) = P2 screen like P2's level-up (controls, weapons/tomes swap, tint, 'PLAYER 2: ' title); EncounterUi.Open postfix for P2-owned: P2Inventory.SwapInStats, 'PLAYER 2: ' name, controls after buttons released; OnClose: restore, SwapOutStats (+LogStats).
- User: portal 'lightning' as P1 walks through - SpawnPlayerPortal.passingFx/audioPassing. Recording notes when P1's passingFx starts playing; P2's portal copy's passingFx (stopped at creation) + audioPassing play at the same replay time. Logs both.
- User: P2's weapons kept firing under a level-up screen: the game's menus pause MyTime (MyTime.paused), not necessarily Time.timeScale. P2Control (weapons/movement input), P2Health (contacts) skip while MyTime.paused; P2Movement holds P2's rb kinematic (velocity saved/restored) while paused.


## v0.30.1 (SHA256 0073224E...34CF)
- v0.30.0: user saw no lightning for P2, though the log said P1's passingFx started at +2.04 s and P2's copy 'played'. Now: P1's effect position relative to its portal (and active/scale) logged at that moment; P2's copy placed at the same portal-relative spot, inactive parents switched on, Play; state logged.
- User: Orc (Axe) as P2 - axes spawned on P1. ProjectileAxe flies startPosition -> desiredPosition (set at P1 on throw). Also log: SuccessfullySpawned ran before Set, and Set's 'pooled reset' made the first axe move twice. Fix: MoveProjectile at most once per frame per projectile (movedFrame dict, replaces the HashSet); ProjectileAxe.startPosition/desiredPosition shifted by the same P1->P2 offset.


## v0.30.2 (installed)
- v0.30.1 confirmed: P2's portal lightning shows (switched on + placed relative to the portal); axes spawn at P2.
- User: P2's axes aimed where P1 faces. Fix: in MoveProjectile, for ProjectileAxe the desiredPosition (relative to startPosition), pushDir, direction and rotation are turned by the yaw from P1's model facing to P2's body facing (FacingTurn); Reaim skips axes.
- User: tint P2's portal red. TintRed on the portal copy: _Color/_TintColor/_BaseColor/_EmissionColor/_MainColor of all renderer materials re-hued to red (s, v kept; near-white -> g,b x0.4); logged list.



## v0.30.3 (SHA256 2AEFB36E...1387)
- v0.30.2: axes aim by P2's facing (user: fine). Portal tint: 'no colour properties found' (none of the guessed names). Now the shader's real properties are enumerated (Shader.GetPropertyCount/Name/Type) and logged per material; every Color-type property is re-hued; particle start colours (single colour) too.
- User rule (saved in memory): log real names first instead of guessing name lists.


## v0.30.4 (SHA256 A85F52BD...5574)
- v0.30.3: portal red confirmed. Real props: all portal parts use 'Archanor VFX/Retro Arsenal/BasicGlow' (_Tint:Color, _ExtraGlow:Range, _TextureSample, _texcoord). User: a little brighter / slightly orange -> hue 0.04, _Tint value x1.3 (HDR), _ExtraGlow x1.3 (original logged), particle start colours same hue/brightness (clamped).


## v0.30.5 (installed)
- User: game froze (v0.30.4). Log: P2 died -> P2 paused (handover to P2) -> pause menu 'Highlight on B_No' (a confirm dialog) -> log ends. No watchdog dump; process later 'not responding'; dotnet-dump collect (Full and Heap) failed with PARTIAL_COPY -> likely hung while quitting/unloading (watchdog thread stopped). Asked the user what they chose.
- Watchdog now writes watchdog.txt directly (no ModLog lock) before FailFast. Portal brightness 1.3 -> 1.7 (user: orange good, brighter).



## v0.30.6 (SHA256 9264CE8E...3E04)
- User: the freeze was after P2 chose Quit (P2 = monkey, dead). PauseUi has Exit() (quit game) and GoToMain(); Quit + yes/no = likely Exit -> hang while the process exits (fits PARTIAL_COPY, no watchdog dump). Suspect: native camera callbacks (Camera.onPreCull/onPostRender, Billboards since v0.28.2) / game event delegate calling into the mod's runtime during shutdown.
- Fix: Ticker.OnApplicationQuit -> FreezeDiag.Stop, Billboards.Shutdown (restores the original onPreCull/onPostRender), PlayerJoin.Shutdown (restores MapController.A_NewRunStarted); logs 'Game quitting'. Needs a test: quit to desktop as P1 and as P2.


## v0.30.7 (SHA256 DCFB9B26...EDFE)
- v0.30.6 quit fix confirmed by log ('Game quitting: removing the mod's callbacks', no hang reported).
- Portal: user wants brighter, red/pinkish (more red): hue 0.965, brightness 2.2.
- User: P2 Ninja's katana never spawned. Log: per fire 'set up 1, spawned 0' -> ProjectileKatana.TryInit (target search) failed at P1 (all its methods share native code -> unhooked). Now: shared native code may be hooked ONCE when every sharer is a ProjectileBase type (P2Weapons.SharedOnlyWithin via a pointer -> types map; still never if already hooked); TryInit added to the melee hooks as prefix (FollowP2 moves it to P2 first) + AfterInit log of the result; ProjectileBase.Set prefix marks P2's projectiles before Set (TryInit may run inside); MoveProjectile skips projectiles already at P2.
- User: explosion didn't knock back P2 (Ninja). Log: PlayerMovement.PushPlayer not hookable (shared native code) and no [blast] at all - the warning-circle path never fired. Real source: CombatExplosion (radius, playerDamage, playerKnockback, enemyKnockback; Start applies to the player). Now every frame new CombatExplosion objects (FindObjectsOfType) are applied to P2 with their own radius/damage/knockback (push via P2Movement.Push); first 5 logged with values. Warning path log-only.


## v0.30.8 (SHA256 C279C048...77A3)
- v0.30.7 confirmed by user: P2 katana works, explosions knock P2 back, portal colour step ok.
- User: portal neon pink with red accents. Neon(): parts Sparks/Lightning/PassingParticles (renderer or particle system name) = accent hue 0.975 (red), everything else (Portal, Center, Circle) = hue 0.92 with saturation >= 0.85; brightness 2.2 (_Tint HDR, particles clamped), _ExtraGlow x2.2.


## v0.30.9
- User: try neon red. Surfaces hue 0.99, saturation >= 0.9; accents (sparks/lightning/passing) hue 0.02 (slightly orange); brightness 2.2.


## v0.31.0
- User: F1 test command, P2 gets ~a million health. P2Health: F1 toggles test health (MaxHp = Hp = 1,000,000; max held while on, hits still count down); off -> real max from P2's stats again, Hp capped. TEMP test key - remove in the cleanup pass (with F9/F10/F11).


## Plan (user, 2026-10-18)
- Make P2 100% operational first (open bugs, then remove TEMP diagnostics and retest), then 4 players: refactor static P2 classes into a per-player object, then quad-split camera/HUD/minimap/XP/character-select layouts (merge only when all players are together at first).

- User: neon red portal (v0.30.9 values: surfaces hue 0.99 s>=0.9, accents 0.02, brightness 2.2) looks great - final.


## v0.31.1
- User: slopes good; spawns fine - log totals over 560 positions: 279 P1 / 281 P2 (batches 11-29 .. 26-14, distances 12-485).
- User: boss curse spawned 2 small bosses (Bark), their bars still at the top. Log: BossBars never moved anything - the bars are not EnemyHpBars on a screen canvas. Diagnostic only (rule: log real names first): 2 s after an IsBoss() enemy appears, dump all root screen-space canvases to bosshud_dump.txt and log every EnemyHpBar (path, canvas mode, enemy).


## v0.32.0 (folded into v0.32.1)
- User: move P2's HUD (health, weapons, tomes) to the right, mirrored from P1's; duplicate the whole panel. hud_dump: 'Inventory' (full-screen, VerticalLayoutGroup, InventoryHud) holds 'Bars (1)' (HealthUI; HpBar/Current pivot x 0), 'Weapons'/'Tomes' (GridLayoutGroup of ItemContainer), 'ObjectiveUi'.
- P2Hud rebuilt: inert copy of the whole 'Inventory' column as a sibling ('MegabonkSplitscreen_P2Hud'), only P2's health/weapons/tomes rows kept (objective stays in P1's column, which no longer gets P2's rows); mirrored: every Horizontal/VerticalLayoutGroup childAlignment Left->Right + padding left/right swapped, GridLayoutGroup startCorner UpperRight + alignment right, rows anchored right, HP bar 'Current' pivot x 1. TEMP p2hud_dump.txt.



## v0.32.1 (SHA256 65BD3E54...D766) - includes v0.32.0 (mirrored P2 HUD on the right)
- User: P2 jumping on a slope goes upright, P1 keeps the slope angle -> P2's lean only updates while grounded (held in the air).
- User: P2 takes no fall damage. Game: PlayerHealth.OnPlayerLanded(speed) listens to static PlayerMovement.A_Landed (muted for P2's copy); PlayerHealth.minFallDamageSpeed/maxFallDamageSpeed, EStat.FallDamageReduction. MEASURING first (FallDamage.cs): own delegate combined into A_Landed (restored on quit) logs P1's landing speed -> HP lost, max HP, limits, reduction stat; P2Movement logs P2's lastFallSpeed/fallSpeed on landing. Formula next build.
- User: P1's camera pushed in by a wall still pulls P2's in (v0.27.10 FreeOffset not enough). TEMP [camwall]: measured length vs remembered free length, wall found, pc.currentZ/defaultZ/offset3rdPerson/Get3rdPersonOffset()/maxExtraZoomoutDistance.
- User: enemy hit sounds don't play for P2 far from P1 (one AudioListener on P1's camera; hit sounds are 3D AudioSources, AttackHit.enemyHitSfx / RandomSfx). New P2Audio.cs: each frame playing 3D sources nearer to P2's camera than to the listener and >= half their maxDistance from it -> 2D with volume = original x rolloff(distance to P2's camera) per their own rolloff; restored when they stop. Logs first 10.


## v0.32.2 (installed, SHA256 00E646F2...0750)
- User: revolver works for P2 (confirmed). HUD on the right + slope jump confirmed (v0.32.1).
- v0.32.1 measurements: P1 landings only at 16-20 (limits min 30, max 140) -> no fall-damage data yet; [camwall] once: 12.22 vs free 12.52 'wall found' -> the game doesn't just shorten near walls; no [audio], no [blast] lines.
- Boss bars: bosshud_dump.txt (boss run) shows they are 'TargetOfInterestUI' {VerticalLayoutGroup, TargetOfInterestUi}, top-centre (0.5,1) at y -140, rows 'Group1', 'Group1 (1)' (HorizontalLayoutGroup) of 'TOI_Prefab' (t_BossName, HpBar/Bar/Poison/Bloodmark, t_hp). BossBars.MoveTargetOfInterest: anchor/pivot bottom-centre, y 60, LowerCenter + reverseArrangement.
- User: explosions at P2 pushed P1. P2Movement.Push now sets PlayerMovement.Instance = P2's copy during PushPlayer. TEMP: exploder start log (distances), P1 speed jumps > 22 logged with exploder/P2-push info.
- User: P2's Aegis doesn't circle P2. P2Weapons.SpinConstants: P2's constant attacks rotate (fixed heading, own angle) at ConstantAttack.GetAuraRotationSpeed() unless IsManualRotation; [spin] logs value vs P1's.
- User: redesign level-up/charge-shrine/reward screens (offers on the owner's half, inventory + stats stacked/scaled on the other). TEMP dumps first: levelup_dump.txt (LevelupScreen's parent), reward_dump.txt (EncounterUi's parent).
- Camera: TEMP [camvec] full camera-space offset of P1's camera each second (40 lines) with currentZ/Get3rdPersonOffset/state.


- v0.32.2 also: user - P2's blood magic lands at P1's enemy's offset around P2. ProjectileBloodMagic has 'Enemy target' (picked near P1). MoveProjectile: for blood magic wait until target is set, then target = enemy nearest to P2, position = new target + (old placement - old target); Reaim skips it.


## v0.33.0 (SHA256 B66D1FCA...B218) - weapon audit (user: no whack-a-mole)
- All ProjectileBase subclasses classified from their fields (types dump):
  - strikes on an enemy: BloodMagic(target), Bluetooth(target,lastTarget), LightningBolt(no fields), Homing(enemyTarget), Basic(currentTarget) -> MoveProjectile: if a target field is set or the spawn point is within 2.5 of an enemy and > 6 from P1: re-anchor on the enemy nearest P2 with the same relative placement, target fields -> that enemy.
  - at the player: P1->P2 mapping incl. facing turn (Pos(v) = p2 + turn*(v - p1)) for the projectile and stored state: Axe/BlackHole start/desired/pushDir, Arrow pushDir, Shotgun/Sniper/Dexecutioner attackDir (also = new aim in Reaim), Whirlwind startDirection, Banana startDirection/movementVelocity, Hero/Cringe sword movingProjectileDir/Rotation, hitboxPos, movingProjectilePosition.
  - player-relative each frame (forward/up/colliderOffset): follow-P2 list + ProjectileShotgun.
  - returns to the owner: Banana -> GetMovementDirection postfix turns it to P2 when heading for P1 (angle < 12 deg); caught (ProjectileDone) near P2 after readyToCollectTime within sqrCollectDistance.
  - always-on: ProjectileDragonsBreath (ConstantAttack) enemyTarget/attackDir -> P2's nearest enemy each frame; ChunkersAttack.rotatingProjectiles brought to P2 if placed at P1 (logged). Rocket finds its own target after the move (no change).


## v0.34.0 (SHA256 D590105E...FF62)
- User: boss bars at the bottom work (TargetOfInterestUI move confirmed).
- Fall damage data (v0.33.0): P1 speed 42.7 -> 12 HP (max 147; limits 30/140); 38.2 and 42.6 -> 0 (right after damage: invulnerability). Rule used: round(100 * clamp01((speed - min)/(max - min)) * (1 - P2's FallDamageReduction)), min/max from P1's PlayerHealth. P2: lowest vy while airborne = landing speed; [fall] logs each P2 landing over the minimum.
- Camera wall, root cause from [camvec]: near walls the game shrinks currentZ (12 -> 5.5 -> 3.7) while Get3rdPersonOffset() stays ~12.25. P2 now uses P1's camera-space offset with depth scaled by Get3rdPersonOffset()/currentZ (sphere-cast FreeOffset no longer used), then smoothed (1-exp(-7 dt)) so P1's camera shake doesn't reach P2 (user: P1's shake affected P2).
- Reward screen redesign (levelup_dump.txt): EncounterWindows/{LevelupScreen/New/W_Offers, ChestOpeningWindow/W_Offers, MicrowaveWindow/W_Offers, ItemPickWindow/W_Offers, EncounterWindow/EncounterWindowNew} + InventoryOverlay/{W_Inventory, 'W_Stats (1)'} (ScaleOnAwake). New RewardLayout (Ticker.LateUpdate): open offer window anchored at x 0.25 (P1) / 0.75 (P2) by owner flags (P2LevelUp.P2ScreenOpen, P2Rewards.EncounterIsP2, P2Interact.ChestIsP2s); inventory then stats anchored top at the other half's centre, scale x0.65 (applied on top of the pop-in animation), stacked with a 16 px gap from y -60; originals restored when P2 isn't playing; reset per run.


## v0.35.0 (installed)
- User (v0.34.0): camera fixed; level-up layout works; P2 fall damage works. Charge shrines still went to P1: log had no 'charge shrine' mark from the ChargeShrine.Reward prefix -> now P2Rewards.OnAddEncounter(RandomStats) decides by the completed ChargeShrine nearest to either player ([reward] log with distances).
- Separate gold (user): P2Gold - mod-side amount; P2 attracts Gold pickups too (P2Pickups), the gold the game added to P1 (_gold_k__BackingField) moves to P2, P1's restored, KillsAndGoldCounter.UpdateGoldCounter refreshes P1's; P2's gold swapped into the game (nestable) during P2's interactions, P2's chest window, P2's level-up/shady-guy screens and P2's EncounterUi windows; HUD: copy of P1's 'Gold' row (KillsAndGold/Gold/T_Gold) at the top of P2's panel, number replaced; reset per run.
- Damage popups (user): P2Popups learns from P1's first hit (EffectManager.OnDamage prefix/finalizer flags; PopupText number/text overload prefixes record kind, colour, size, text, offset; [popup] log), then P2Health.TakeDamage shows the same popup above P2.



## v0.35.1 (SHA256 68995C20...8231)
- v0.35.0 log: charge shrines now correct (P2 5-7 away -> P2; P1-near -> P1). Popup learned: text '-32', red, size 20, offset (0,1,0); user: P2's popups show but faced P1's camera. Gold: no [gold] at all - P2 never got a coin.
- Gold, MEASURING: [gold] coin state for gold pickups near P2 (value, picked up, flying to whom, CanPickup) and PlayerInventory.ChangeGold(amount) calls (frame, frames since a P2 coin was applied); P2 coin application logs P1 gold before/after. Candidates: batched gold (maxGoldPerFrame/goldThisFrame), coins pre-targeted at P1, or gold paid on kill (EnemyData.GetGold).
- Billboards: every active TMPro.TextMeshPro (3D text: damage popups) turned for P2's camera too (looked up every frame).
- P2 shield (user): P2Health MaxShield = P2's Shield stat, Shield absorbs hits first, refills after P1's shieldRegenCooldownTime (else 3 s) - TEMP stand-in refill full over 4 s; [shield] logs P1's shield over time + PlayerHealth regen fields to match. HUD: copied ShieldBar row (HealthUI.shieldBar fill + t_shield text with P2's numbers), mirrored pivot, shown while MaxShield > 0.


## v0.36.0 (SHA256 3822DD17...5C85) - tome audit (user: every tome effect for P2; batch testing)
- Tomes only change stats; P2's table already has P2's tomes. Audit of who READS each stat for P2:
  - weapon stats (damage, size, duration, projectile speed/count/bounces, attack speed, crit, knockback, elite dmg, burn/freeze...): already via P2 context hooks.
  - movement (MoveSpeedMultiplier, JumpHeight, ExtraJumps): P2Movement.FixedTick now runs SetBetterInput/MovementTick inside P2Inventory.EnterP2.
  - defense/health (MaxHealth, HealthRegen, Shield, Thorns, Armor, Evasion, DamageReductionMultiplier, DamageCooldownMultiplier, Overheal, HealingMultiplier, FallDamageReduction): NEW P2GameHealth - P1's PlayerHealth briefly holds P2's state (all int/float/bool instance props, reflection) inside P2's stat context with PlayerHealth's static events muted, PlayerMovement.Instance = P2's copy, KillPlayer/PlayerDied skipped while borrowing (both must be hookable, else OFF); calls: Tick (every frame), UpdateMaxValues+UpdateRegenValues (1 s), DamagePlayer(enemy, dir, 0) for contacts, DamagePlayerExternal for explosions, OnPlayerLanded(speed) for falls; P2Health syncs Hp/MaxHp/Shield/MaxShield from it, flash + popup from HP+shield lost, P2 down at 0 HP. Config [Experimental] P2GameHealth (true). Old simple path kept as fallback. P2's stats made ready before the swap.
  - PickupRange: P2's radius = P1's sphere * P2/P1 PickupRange.
  - XpIncreaseMultiplier: P2 gems x P2's multiplier with fractional leftover (like PlayerXp.AddXp/leftOverXp).
  - Luck: P2's screens already run in P2's context. Run-wide stats (Difficulty, EliteSpawnIncrease, Powerup*, Silver, Chest*, Enemy*) stay shared.
  - NOT done: Lifesteal - DamageContainer has no owner (only damageSource), P2's hits may feed P1's lifesteal; GoldIncreaseMultiplier waits for the gold fix.
- User: P2's aura not tilted on slopes -> P2's always-on attacks rotate by FromToRotation(up, P2Control.SlopeUp) * spin.

## v0.37.0 (SHA256 619E243A...A91E) - character abilities for P2 (user: every character's special, e.g. Flex; plus rewards affecting them)
- Types dump: 21 PassiveAbility kinds (EPassive), made by PassiveAbilityFactory.CreatePassiveAbility(CharacterData.passive), held in PlayerInventory.passiveAbility; one active, AbilityDash (ActiveAbilityFactory.CreateAbility(EAbiltiyActive.Dash)), PlayerInventory.activeAbility. No ability/cooldown EStat exists: "rewards that affect the ability" = level scaling (OnLevelup) + the normal stats the passive reads (P2's in P2 context).
- New P2Abilities: P2's own passive from P2's character. Every static delegate in Assembly-CSharp is snapshotted around its creation/Init; P2's new listeners on PlayerHealth/PlayerMovement/PlayerXp events are taken off and only set during P2's health borrow (P2GameHealth), P2's movement tick (P2Movement.MuteOn) and P2's level-ups (P2XpBar -> PlayerXp.A_LevelUp listener, fallback OnLevelup via reflection). Other events (enemies, stage) stay subscribed; enemy damage/died methods on passives hooked (if unique code) so each passive reacts to enemies nearer its own player (P1's only filtered when P2 has the same kind).
- The passive's static numbers (Flex cooldown/flexReadyAtTime...) swapped per player; PassiveAbility/A_FlexReady-type events muted for P1 during P2 calls, A_StatModified -> P2Inventory.MarkDirty.
- P2Inventory: stat context Push/Pop also swaps inv.passiveAbility (HasPassive, passive bonuses); RecomputeNow uses P2's passive.
- Active: if P1 has an activeAbility, P2 gets a Dash, button = P1's pad binding for an action named *ability/dash/special/active*. TEMP [actions] log of all Rewired actions.
- P2Gold.Refresh caches the KillsAndGoldCounter (Stonks swaps gold each frame).
- Test (user): no flex fx for P2; P2's blasts affect P1; P2's lightning favours P1's enemies; P2 no gold.
  Log: P2's Flex never Init'ed - compiler lambda caches (__c.__9__8_0) changed during the factory call and looked like "factory inits". Rewired: Ability action has NO pad binding on P1 (dash only keyboard?). No gold coin ever near P2: kill gold = ChangeGold(1) directly.

## v0.37.1 (SHA256 A022FEEC...FD9FA)
- P2Abilities: compiler-generated types / __9 props excluded from the event snapshot -> mod runs Init (Flex hit listener). TEMP log when a P2 passive static (flexReadyAtTime) changes.
- Gold: Enemy.EnemyDied(...) prefix/finalizer marks the dying enemy nearer P2; ChangeGold(+n) during it is skipped for P1 and re-run with P2's purse (P2Gold.Run) in P2's stat context. [gold] log shows "enemy dying" per ChangeGold.
- Blasts: CombatExplosion.Start hooked (if unique): when P1 is outside the radius, P2's copy collider is off during it and P1's velocity/pushed/hp/shield are restored if changed (logged). P2's own push/damage from a P2-only blast also guarded; TEMP warn if P1's speed jumps within 0.6 s of a P2-only blast.
- Targeting: EnemyTargeting.* position args moved by P2-P1 when nearer P1, during a P2 projectile's Set/TryInit/FindMovementDirection/FindTarget (scope) or P2 stat context. [target] log.

## v0.37.2 (SHA256 F7B9CCBC...E88C) - Aegis (user: P2's spun at a different rate, no effect on enemies)
- Effect: the game's health code uses AuraAttacks.aegisAttack ("the player's" aegis = P1's). P2GameHealth.Borrow puts P2's AegisAttack (P2Weapons.P2Aegis) there during P2's hits. TEMP [aegis] log when P2's shield count changes. RepairP1 clears aegisAttack if it pointed at the removed P2-made aegis.
- Test (user): gold still flies to P1; shield tome nothing (no shield HUD); regen tome nothing. Log: ChangeGold never during EnemyDied; P2 maxShield 0 after Shield L1; regen ~1 HP / 50 s.

## v0.37.3 (SHA256 2969A0D5...879A)
- Gold: kill gold = MoneyFlying objects (MoneyUtility.flyingMoneyDict) flying to the player, paying via MoneyFlying.Pickup -> ChangeGold. First seen nearer P2 -> P2's: target = P2's body; Pickup hooked (if unique) -> its ChangeGold re-run with P2's purse.
- Health stats: 1 s borrow now UpdateMaxValues + OnStatUpdate(MaxHealth/Shield/HealthRegen) (was UpdateRegenValues(0f), maybe forcing 0); same in Init. TEMP [hpstats] log: P2 table value / game lookup in the borrow / P2 health fields.
- v0.37.4 (SHA256 9EE03B92...3526): TEST F2 toggles 1,000,000 HP for P1 (maxHp held every frame; off = UpdateMaxValues + clamp). Remove with the other test keys at cleanup.
- v0.37.5: P2 character effects (P2Abilities.MakeEffects). Shield: new GO on P2's body with AddComponent<PlayerShieldEffects>, fields = inert copies of P1's shieldBreakFx/shieldChargeFx/sfx; its Awake listeners (PlayerHealth A_TakeDamage/A_Heal) captured by Route() -> fired only in P2's borrow. Flex: inert copy of PlayerEffects.flexFx, active = P2's Flex HasFlex() (P2 context); log compares with P1's.
- Test (user, v0.37.5): Flex works (log: flexReadyAtTime moves on P2's hits, fx on/off). Gold flies to P2 but P2's counter doesn't rise. F2 seemed not to work (log: ON 1000000 - the game's stat refresh resets maxHp and caps hp).
- v0.37.6 (installed, SHA256 D16590FD...A49A): P2's money stays P2's after pickedUp until inactive (pickedUp may come before Pickup); fallback P2Gold.Collected if Pickup pays without ChangeGold; [gold] logs per payout. F2 holds maxHp AND hp every frame + A_MaxValuesChanged for P1's bar. Spin measuring skips pauses/level-up and slowed samples (log had factor 1.07/0.55 among ~20).
- Test (user, v0.37.6): gold still P1's; shrine of succ used by P2 -> XP to P1. Log: no MoneyFlying.Pickup call at all (not even P1's money).
- v0.37.7 (SHA256 F2BBE30B...20DC): MoneyFlying.Update/FixedUpdate hooked too (scope: P2's money's ChangeGold -> P2; else P1 gold gain moved via Collected); [gold] logs which method pays. Succ: P2Interact -> P2Pickups.PullForP2(5 s); PickupManager.PickupAllXp scoped (if unique): StartFollowingPlayer target -> P2's body (ref), gems marked P2's; [succ] log count.
- Test (user, v0.37.7): succ shrine works for P2; gold works (log: P2's money pays in MoneyFlying.FixedUpdate -> P2's purse). Lifesteal tome no effect for P2. New request: map overlay (P1 right trigger: FullMap + statsWindow full screen) -> only over P1's half, map smaller with stats under it (stretched); P2 gets the same on P2's half.
- v0.37.8: Lifesteal: PlayerHealth.OnEnemyDamaged prefix (if unique): enemy nearer P2 -> skipped for P1, run on P2's borrowed health when P2's Lifesteal > 0 ([lifesteal] log). New MapView.cs TEMP diagnostics: on the first map opens - mapCamera settings, FullMap fields, FullMapUi path/canvas, map_dump.txt (FullMapUi, statsWindow, mapDisplayTransform), P1's pad binding for MapOverlay, actions held.
- v0.37.8 map log: GameUI 1920x1080; 'Map' {FullMap} > 'FullMapUi' 750x750 left-middle (RawImage 'MapRender' = MapTexture from MapCamera ortho 300 at y 1000, whole world, culling layer 14) + 'W_Stats' right-middle (StatsContent: vertical StatEntry rows). P1 opens via Rewired 'MapOverlay' = Right Trigger (axis id 5).
- v0.38.0 (SHA256 009CE49A...D2B5): MapView panels per player under 'Map' (half screen each): inert map-frame copy x0.6 at top, W_Stats copy (StatEntry rows kept) under it as 2-column GridLayoutGroup, title "PLAYER n STATS"; rows refreshed every 0.5 s (P2's in P2 stat context). P1's panel while the game's map is open (game's frame/window scaled to 0); P2's while P2 holds Right Trigger (PollControllerForAllAxes, names logged); map camera kept on for P2; QueueRevealFog(P2) every 0.5 s. TEMP [map] icon check (map-camera objects near each player).
  Also CharacterDiag.cs TEMP [calcium]: movementValues of P1/P2 copy (shared object?), differing PlayerMovement numbers, game scripts on P1, P1 animator layers/params/states for 2 min. User next: P1 = Calcium. Not done yet: making P2's movement see P2's character (ResetState(eChar) may already do it - waiting for the log).
- v0.38.0 [calcium] log (P1 Calcium, P2 Monke): movementValues per player are separate objects with per-character values (P1 Calcium counterMovement 0.20, P2 Monke 1.00) -> P2 Calcium's slide should already be right. Animation: Calcium's model has the game scripts SkatingAnimations (animator, playerMovement, skateSfx, kick timing) + AnimationEffect; states: idle -1831344825, 298403382 / 2105233610 while moving, 84537014 in the air. P2's body = Instantiate(prefab) keeps those scripts, but SkatingAnimations took P1's movement and the mod's run/idle code fought it.
- Test (user, v0.38.0): maps work but the stats panel flashed at its old spot; wanted wider (not over the other half). P2's Aegis lacks the "blocked" text.
- v0.38.1 (SHA256 47C18207...9051): the game's FullMapUi/W_Stats invisible via CanvasGroup alpha 0 the whole time P2 plays (its ScaleOnAwake undid scale 0); copies reset to alpha 1; stats columns 400 wide (+40 gap), name text widened, window LayoutElements >= 300 set to the content width, then scaled to fit half - 2x30 and the room under the map.
- v0.38.2 (SHA256 BFF02FD3...05B4): Calcium only (explicit ECharacter check - user rule, memory character-specific-scripts): P2's SkatingAnimations linked every frame to P2Movement.Pm + P2's animator; P2Control's own animation code (states/params/jump) stands back while P2Body.GameAnimates. Popups made during P2GameHealth.Borrow (e.g. Aegis "blocked") moved from P1 to the same spot above P2 ([popup] log).
- Test (user, v0.38.2): P2 Calcium animations "seem broken". Log: SkatingAnimations linked; no P2 state data. Nothing else drives P2's animator after the entrance (Replay only during it).
- v0.38.3 (SHA256 0FF88C29...896E): TEMP [calcium] side-by-side log: P2/P1 animator state, SkatingAnimations.lastAnimationName, animator speed, speeds, and how much P2's script turned the body root since P2Control set it (the script sits on the body root on P2, on the model under 'Renderer' on P1).
- User: P2 Calcium stuck in one pose. v0.38.2 [params] log: P1 Calcium's animator uses bools grounded + moving (set by PlayerRenderer on P1's 'Renderer', which P2 lacks); v0.38.2 had stopped P2Control setting them for Calcium.
- v0.38.4: AnimateLikeP1 sets grounded/moving/jumping for Calcium too; only state plays (PlayState/jump Play/guessed names) stand back while SkatingAnimations runs. [calcium] side-by-side log still on.
- User: v0.38.4 fixed P2 Calcium's animations. New: level counter for P2; P1's level under P1's minimap, P2's under P2's.
- v0.39.0 (SHA256 4ADBD57F...3CFC): LevelLabels.cs - while P2 plays, the game's 'Top/T_Level' (XpBarUI.t_levelText) is reparented under P1's minimap frame (bottom centre, text top-aligned), restored when P2 isn't playing; P2's label = inert copy under P2's minimap copy (or under P1's label when P2's minimap is hidden/merged), text = P1's with the number replaced by XpToLevel(P2XpBar.Xp). Labels scale with the minimap frames (x0.75 in split).
- v0.39.1 (SHA256 0096042C...E907): user next tries the Monkey (wall climb). P2's copy's Unity calls (Update/OnCollisionEnter/Stay) now run in P2's stat context (HasPassive(WallClimb) answers for P2). TEMP [climb] log when P1 or P2 is Monke: passive check both, A_StartedWallClimb listeners, per player wallClimbing/state/grounded/wall contact changes with velocity, wallNormal, cooldowns, input, anim state. No climb animator param names in global-metadata (asset data) - states logged instead. NEXT: read [climb] log, make P2 Monkey climb/animate like P1 (gate on ECharacter.Monke).
- v0.40.0 (SHA256 5B544B5E...21BB): AUTOTEST mode (user OK'd building it). Autotest.cs: active only if BepInEx\plugins\MegabonkSplitscreen\autotest_run.txt exists at load (renamed to autotest_last.txt at once). Flow: MainMenu.GoToCharacterSelection -> PlayerJoin.JoinFirstFreePad (2nd pad stands in for P2) -> CharacterMenu.OnSelectCharacter(P1) + P2Select.PickCharacter(P2) -> OnConfirmCharacter -> MapSelectionUi.StartMap -> wait for P2Control.HasControl -> script. Input: P1 PlayerInput.move/jump written in LateUpdate+FixedUpdate; P2's PadHeld/ReadStick answer from the script (world dirs -> stick via P1 orientation / P2Camera.Yaw). Commands: chars, wait, go <p> x z|wall|other, stop, jump, hold, place, snap, shot, log, quit; 300 s limit; Application.runInBackground. Results: [test] log + autotest_result.txt + autotest_*.png. Launcher tools\autotest.ps1 <script> (steam://rungameid/3405340, waits for exit, prints results, removes an unread script). First test: tests\monkey_climb.txt. Launching needs the user's OK each session.
- Autotest bring-up (user OK'd launching, 2026-10-18): v0.40.1 waits up to 22 s for a 2nd pad (both pads show as 'Steam Controller' via Steam Input); v0.40.2 Confirm = press CharacterMenu.b_confirm, fallback MainMenu.GoToMapSelection (OnConfirmCharacter only moves the highlight; the fallback is what actually worked); v0.40.3 screenshots via CaptureScreenshotAsTexture at end of frame (CaptureScreenshot(path) missing ReadOnlySpan method); v0.40.4 own PNG encoder (ImageConversion.EncodeToPNG crashed natively - dump EncodeToPNG_Injected).
- MONKEY CLIMB FIXED (v0.40.3+, verified by autotest tests\monkey_climb.txt): [climb] log showed P1's input holdingWallrun True (game holds it while no wall-run button is bound, PlayerInput.hasWallrunActionBound false) but P2's InputState had holdingWallrun=false. Now P2's = !hasWallrunActionBound. Result: P2 climbs like P1 (vel (-4,15,0), up to ~64 m), screenshot shows the climb pose. A_StartedWallClimb's listener is PlayerRenderer.ForceWalkAnimation (P1 only) - P2's climb animation looked right without it.
- v0.40.4 installed (SHA256 0586198E...0AD0).
- AUTOTEST BATCH (user OK'd, 2026-10-18), v0.41.0-0.41.12, all verified by autotest logs/screenshots:
  - cheats/checks added: tome/weapon <p1|p2>, hurt, spawn <p> <EEnemy> n dist, stat, map <p> on|off, mapcheck, godp1.
  - P2 tomes: Shield 75, HealthRegen 130, Lifesteal 0.3 (correct); shield absorbs + recharges; regen 75->105->134 in 10 s; lifesteal queues heal per P2 hit ([lifesteal]).
  - FIXED tome leak: TomeInventory.A_TomeUpgrade muted while adding to P2's holder (hook on AddTome/AddMaxedTome) - P1's PlayerStatsNew booked P2's tome (P1 got a 75 shield); RecomputeNow now runs UpdateStat for every EStat (ForceUpdateStats alone left P2's Shield 0 without the event); RepairP1 re-runs P1's health max values.
  - Aegis: P2's shields used on P2's hits, "Block" popups moved to P2 ([aegis]/[popup]).
  - Shield VFX: P2 gets its own Outline (P1's mode/width, PlayerRenderer.shieldColor) on while P2's shield > 0 - screenshot shows cyan outline on P2 like P1's.
  - P2 HUD shows tomes given without a level-up offer (DataManager.GetTome fallback).
  - Map panels rebuilt: own box (title + two TMP columns) under the map in each half; values from a hidden copy of W_Stats (rows created via StatsUi.TryInit, stats set from StatsUi.statsToShow with StatEntry.Set); P2's values = StatsUi.FormatStat(stat, P2Inventory.Get(stat)) (P1 check: formatter matches the game's text). P2 context also swaps PlayerStatsNew.rawStats.
  - Merged view: P1's minimap glides to top centre (user request) - no longer under P2's bars.
  - Blasts: 4 spiders at P2 (P1 35 m away): P2 -120 HP and launched, P1 untouched, no guard warnings.
  - Lessons: GetWorldCorners doesn't return through interop; EncodeToPNG crashes (own PNG encoder); CaptureScreenshot(path) missing.
- v0.41.12 installed (SHA256 39E66B58...C613).
- SWEEPS (v0.42.x, 2026-10-18; user asked: every weapon and tome, stats/effects/VFX P1 vs P2):
  - Merged view: level labels beside each player's health bars (P1 right of P1's, P2 left of P2's mirrored), x0.7; split: under the minimaps (LevelLabels.PlaceForView).
  - Autotest additions: unweapon, clear, godp2, kills, dmg (damage dealt to the enemies spawned per player), mark/delta (every EStat change P1 vs P2), wcompare (WeaponUtility numbers P1 vs P2 + P2 firing list), xp on/off (XP OFF by default - level-up screens paused tests), open (both players onto open ground: no obstacle within 12 m), lone pad stands in for P2. TimeLimit 600 s. Tests: tests\tome_sweep.txt, chaos.txt, weapon_sweep1/2.txt.
  - Tomes: 22/27 identical deltas. Bounce/Balance/Gambler/Hoarder not in DataManager.tomeData (can't be given). Chaos FIXED: it writes random stats into "the player's" statInventory - P2's StatsInv swapped in during AddTome on P2's holder (also real level-ups).
  - Weapons: wcompare identical for all 30. Damage (open ground): fine for most. P2 does NO damage: Dragon's Breath (always-on ProjectileDragonsBreath), Shotgun (ProjectileShotgun: TryInit/CheckZone at P1's spot - game re-places at "the player"), Sniper. These are aimable weapons (game shows an "Aim" hint on P2's side) -> likely aim/position from P1. Axe/Bow/CorruptSword sometimes lower - placement noise likely. Aegis 0 for both (only reacts to hits; god modes).
  - FIXED P2 HUD weapon slots: slots that were off at copy time never showed (P2 missed weapons on the HUD) - now on when P2 has an item for them or P1's slot is on.
  - Autotest fix: P2's firing list refreshed after 'weapon p2' (remove+add kept the count, P2Weapons.Sync never ran).
  - NEXT (done below): fix Shotgun, Sniper, Dragon's Breath for P2.
- v0.43.0-0.43.7 (2026-10-18, autotest-driven, user away part of the time):
  - Shotgun: CheckZone (and the axe's per-frame code) runs with P2's movement copy as PlayerMovement.Instance AND P1's MyPlayer transform temporarily at P2's spot (restored in the finalizer, outermost call only) - P2 91-97 vs P1 ~91. GetShootingPosition/GetAttackDir postfixes map P1 -> P2 (flat aim at P2's nearest enemy).
  - Sniper: worked once players stood on open ground (33 vs 33).
  - Dragon's Breath: its Update moved it to P1 every frame (log: 25 m off) - put back on P2's body before FixedUpdate / after Update; FindClosestTarget -> P2's nearest. 93 vs 93.
  - Space Noodle (LaserBeamAttack): FindTarget -> P2's nearest within weapon range, GetBeamStart mapped to P2. 70 vs 87 (varies run to run).
  - Axe: now orbits P2 (log 2-7 m from P2) but P2 ~0.46 of P1's damage - likely the axe's static per-tick hit counters shared with P1 (numTimesEnemiesHitThisTick/damageThisTick). OPEN.
  - Aegis: P2 counter-hits register (46 vs 23 in one run, 0 in another - noisy).
  - Hit flash: only P2's character renderers (not always-on weapons / mod effects / particles) - user saw Chunkers and the breath tinted red.
  - P2 HUD: extra slots cloned when P2 has more items than copied slots (user: P2 capped at 4 tomes) - screenshot shows 8.
  - Autotest: keyboard/mouse input off during tests (user's typing reached the game); 'open' command; virtual P2 with no pad at all (PlayerJoin.VirtualP2/HasP2, JoinVirtual after 12 s); weapon windows 5 s.
  - Tome effects (tests\tome_effects.txt): Damage/Size/Quantity/ProjectileSpeed/Duration/Cooldown/Precision/Knockback x3 change both players' Revolver+Aura numbers identically, damage even (0.88-1.20), Aura rings same size in screenshots. Defense (tests\defense_tomes.txt): Armor/Evasion/Thorns equal stats, HP lost 21 vs 19, thorns 37 vs 31, EVADE popups above P2.
- v0.43.7 installed (SHA256 69C81AC6...6822).
- POWER-UPS (user 2026-10-18: test every power-up incl. VFX, screenshot as each is collected). v0.44.0: autotest 'pickup <p> <EPickup>' (PickupManager.SpawnPickup 1.5 m ahead); tests\powerups.txt.
  Baseline log: Pickup.A_PickupTriggered listeners = AchievementTracker.OnPickupTriggered, TrackStats.OnPickup, GoldFilterUI.OnPickup ('GoldFilter'), EffectManager.OnPickup ('Managers'), PlayerStatusEffects.OnPickupTriggered, PlayerHealth.OnPickup. EPickup: Xp Gold Health Nuke Time Shield Rage Haste Stonks Magnet Silver; EStatusEffect: Haste Rage Shield Stonks TimeFreeze Invulnerability Slow Freeze Bleed Poison BossPoison.
  Findings: P2 collects no power-up (P1 heart 75->119, P2 unchanged). BUG: P2's stats include P1's status effects (RecomputeNow uses P1's inventory.statusEffects) - P2 'got' Haste/Shield-pickup stats. Nuke/Time global (fine).
  DONE v0.44.1-0.44.3 (SHA256 8756D2C9...1344 installed): new P2PowerUps.cs - P2 attracts/collects power-ups (P2Pickups), P2's own PlayerStatusEffects (constructor's pickup listener taken off), swapped in for P2's stat context (P2Inventory Push/Pop + RecomputeNow); ApplyForP2: event muted, listeners called for P2 (PlayerHealth via Borrow, status on P2's list, EffectManager with P2 as the player + magnet PullForP2, GoldFilterUI as is; trackers and P1's Shield/StatusEffectPickup visuals skipped); new StatusEffectPickup visuals made during P2's pickup are disabled and kept at P2's feet until P2's effect ends; shield bubble = inert copy shown while P2 has Shield. Autotest verified: heart heals P2 like P1 (75->119), Haste/Rage stat changes identical, statuses timed on P2, nuke/magnet collected, screenshots show bubble + sparks on both. NOT checked yet: magnet pull result count, Stonks gold effect, TimeFreeze on P2's weapons, status HUD icons for P2.
  DONE v0.44.4 (SHA256 2F3788C9...E293E installed), user's playtest reports, verified by tests\shield_camera.txt:
    - Shield outline stuck after the shield broke: the hit flash saved P2's material lists WITH the outline's materials
      and restored them after the outline had removed its own -> the flash now takes the outline off first; the outline
      stays off while flashing. Screenshots: outline on both when full, gone on both when broken.
    - Outline on the aura etc.: the outline now covers P2's character renderers only (IsCharacterRenderer, like the flash).
    - Shield tome "didn't fill": game rule (P1's log) = recharge ~25/s, only 6 s after the last real hit; P2 follows it.
      P2 took the new max up to 1 s late (max values once a second) -> now at once when P2's health stats change.
      Test: P2 full 0.6 s after the tome, P1 0.9 s. Note godp2 (test health) skips P2's max updates - don't use it for shield tests.
    - P1's wall camera pulled P2's in: the old ratio fix was off when the game's currentZ fell below 0.5 (against a
      wall) -> depth now shifted by (Get3rdPersonOffset - currentZ). Test: P1's camera 0.96 m, P2's 12.26 m.
    - TEMP added: [camwall2] log, [tome] listener log, autotest commands shield / cam / backtowall.
  DONE v0.44.5-0.44.12, character sweep (tests\chars\<name>.txt, both players the same character; tools\charsweep.ps1
  with a 15 s cooldown between launches and a stop on any nvlddmkm GPU error - user, after a GPU hang + reboots):
    - Bananarang (user: bananas spawned from P2 then flew to P1): ProjectileBanana MyFixedUpdate/StepMovement/
      LateUpdate/CheckHitPlayer/CheckRadiusDamage run with P2 as the player (the old GetMovementDirection turn-back never fired).
    - Autotest: locked characters made usable in memory for the test only (user: progress will be wiped anyway);
      held through confirm + CharacterMenu.selectedCharacter set (P1 fell back to SirOofie); the run's real
      characters are checked at spawn (FAIL if not as asked). New commands: statdiff, passives (typed wrappers).
    - Athena's LockIn: AegisAttack.A_Used/A_Regen (Action<int>, no owner) reached both passives. v0.44.8 hooked the
      Aegis methods -> CRASH at spawn (dump: RegenShield shares native code with SpawnPlayerPortal.StartPortal).
      v0.44.9: no hooks - passive listeners taken off both events, each player's Aegis currentAmount polled per
      frame, a change calls that player's passive (P2's in P2's context).
    - Roberto's Hoarder timer ran 2.7x fast for P2 (frame rate / physics rate): P2's passive Tick now runs from the
      mod's FixedUpdate (PlayerJoin -> P2Abilities.FixedTick), like the game's. Calcium's timers now identical too.
    - Checked, not bugs: SilverIncreaseMultiplier P1 1.075 / P2 1 = a +0.075 permanent entry the game puts in P1's
      own stat holder at run start (silver is run-wide, P1's is read). Vlad's MaxHealth goes up AND down during
      fights for both (not in the stat holders) - momentary differences only. Damage/kill counts are noisy (enemy
      wandering, world spawns) - parity judged by passives + statdiff.
    - All 21 characters: passive classes and values equal for P1/P2 (timers aside), stats equal (silver aside).
    - TEMP: [watch] stat watcher in autotest scripts, [camwall2], [tome] listener log.
    - Final regression sweep (v0.44.12, all 21): passives equal for all; stats equal except momentary event-driven
      ones (Athena AttackSpeed from each player's own Aegis, Calcium speed resets on hits, Vlad's MaxHealth).
  DONE v0.44.13 TonyMcZoom (user: the hoverboard guy didn't render right for P2): [hover] log - the game's
    HoverAnimations sets its own object's local position/rotation; on P2's copy that object is P2's body root, so it
    pinned P2's body to P2's spawn spot. P2's copy is off; P2 gets P1's script's bob offset (same sine) in P2Control.
    Screenshot: P2 on the board in P2's view. (Lean/landing dip not copied - P1's would be wrong for P2.)
  DONE v0.44.14 billboards (user: check P2's billboard fx angles, also with P2's camera turned): tests\billboards.txt
    (rings of hearts/clocks around P2, P2's camera turned 90/180/270/315, P1's turned 120; 'billboards' measures
    each sprite's angle to its camera line in P1's and P2's view). Found: the game's LookAtCamera aims AT the
    camera's position, the mod's one-rotation-for-all left side sprites up to 90 deg off; pickups were turned too,
    swinging their LookAtCamera sprite child. Now: each LookAtCamera turned from its line to P1's camera onto its
    line to P2's; pickups with a LookAtCamera child not turned; restore in reverse. Result: 0.0 deg worst at every
    angle (73-75 sprites). v0.44.15 (user: P2's hearts looked rotated weird): FromToRotation's shortest turn added
    a roll around the line - now P1's upright look-frame is mapped onto P2's (world up kept); the test also checks
    roll (P2 vs P1, fail at 5 deg): 0.0 at every angle; screenshots: hearts upright in both views.
  DONE v0.44.16-0.44.18 damage numbers / FLEX / blocked popups (user: damage numbers looked off, FLEX angled wrong;
    my screenshots showed edge-on, then mirrored numbers in P2's view):
    - The LookAtCamera list was refreshed once a second; damage numbers live < 1 s -> looked up every frame.
    - Damage numbers were turned twice (as LookAtCamera and as 3D text) -> texts under a LookAtCamera skipped.
    - [bbtree] log: damage numbers are TextMeshProUGUI on the world-space 'DamageNumbersCanvas'. A canvas builds its
      geometry once a frame before any camera draws, so per-camera turning never reaches the screen
      (ForceUpdateCanvases didn't help). Now: in the mod's LateUpdate (after the game's LookAtCamera.Update), canvas
      sprites nearer P2 are aimed at P2's camera for real; P1 then sees P2's numbers aimed at P2's camera (small and
      far off in P1's half). Merged view: untouched.
    - The check now tells front from back (mirrored ones used to pass) and measures canvas sprites as drawn.
      tests\billboards_text.txt, flex_blocked.txt, flex_popup.txt: all 0.0; screenshots: P2's numbers and FLEX
      readable in P2's view. Seen once, not checked: P1's own FLEX popup looked mirrored in one P1 frame (maybe its
      first frame before the game's LookAtCamera turned it - vanilla?).
  DONE v0.44.19 performance (user: the game stutters a bit - logging or hooks?): TEMP [perf] timers (mod per frame,
    P2Body parts, billboards, map parts). Mod's own per-frame work 2.2-2.6 ms of a 6.9 ms frame -> 1.0-1.15 ms:
    P2Explosions' scene-wide CombatExplosion search every frame (~1 ms) -> 10x a second; MapView looked for the
    FullMap every frame while none was found (~0.8 ms) -> once a second. Logging is not it (~600 lines a run).
    Spikes of 60-110 ms in the tests line up with the autotest's spawn commands; real-play spikes not measured yet.
    Not timed: Harmony hooks inside the game's own code (weapons, stats context).
  DONE autotest 'open': ground ray from 400 m up (from 60 m above P1 it could start inside higher terrain); no clear
    spot for P2 -> next to P1 (user: P2 got stuck under the ground in a test run).
  Not measured: 3D damage texts
  1.0.0-alpha playtest fixes (user's notes after a few rounds; zip rebuilt, same 1.0.0-alpha; previous zip in
    dist\old\SplitBonk-V1.0-Alpha_before-playtest-fixes.zip):
    - Perf / logging (user: Steam Deck dropped frames): per-frame scene searches cached (gold HUD, billboards): mod
      4.1 -> 2.0 ms per frame, game frame 13.9 -> 6.9 ms (perf_steady). Logging: '[tag]' detail lines only with the new
      config [Debug] DebugLogging = true (or in autotest); file flushed once a second instead of every line.
    - Slide (user: the player can slide, P2 can't): P2's game movement gets crouching = P2's Slide binding (Rewired
      action MyInputManager.Slide on P2's pad). NOT POSSIBLE: P2's slide pose - P2's animator leaves the slide state
      at once and the value that keeps P1's in it can't be read or hooked (comment in P2Control).
    - Full map blank for P2 (right trigger): P2 opening it now runs the game's FullMap.OnMapToggle like P1's (MapView).
    - Regen tome (user: level 1 regen didn't work): P1's health tick runs per physics step (50/s), P2's ran per frame
      (2.4x heal) and then, per step, P2's regen timer never got past one step. P2's regen is now timed by the mod per
      physics step with the game's healInterval / healPerInterval for P2 and the game's Heal; P2Health.FixedTick.
      regen_tome: both +5 HP per 6 s at regen 50.
    - P2 interact (user: Y interacted, X shows in the prompt): P2's interact button = P2's own Rewired binding of the
      Interact action ([pads] log once at the menu). NOT CHECKED on the real pads.
    - Anubis (Pharaoh1/2/3): red laser = EnemySpecialAttackTargetLaser 'LaserTargetAttack', blue = SpinningLasers
      'LaserDiscoAttack'. Both hit in OnTriggerStay: P2's collider -> the hit runs as P2's (AsP2Hit, own damage
      cooldown). The red laser aims at P1's transform (PlayerMovement.Instance / rigidbody swaps did nothing): while the
      boss targets P2, P1's transform is at P2's place for the laser's FixedUpdate only (transform sync off, P1's body
      pose put back - the first try threw P1 24 m up). anubis_lasers: P2 hit 2/tick by red, 5/hit by blue, P1 untouched;
      P1 down (user: it went for P1's grave) -> the beam is on P2.
    - Chunkers / Aegis (user: don't rotate, jitter when turning): spin rate default x20 (was 1 until P1 had an always-on
      attack to measure - Aegis 3.5 deg/s); spin set after P2's body turns (P2Weapons.AfterBodyMoved), was before ->
      one frame of body turn each frame; Chunkers rock ring centred on P2 (was 1.1 m to one side). spin_chunkers_aegis.
    - Magnet (user: P2's magnet sent the XP to P1): the magnet pulls inside its own ApplyPickup (gems already flying to
      P1 after it); P2's runs as a pull for P2 (P2Pickups.PullAsP2). magnet_p2: 16 of 16 gems to P2.
      P1's magnet checked after (magnet_p1, XP off): works before and after P2's; the earlier "did nothing" was a
      level-up screen pausing the game.
  1.0.0-alpha second round (zip rebuilt; previous in dist\old\SplitBonk-V1.0-Alpha_before-moai-skins-buttons.zip):
    - Slide: works for Megachad too (flat and downhill, PC with real pads; the Deck report was a false alarm).
    - Alternative buttons (user: slide is also the left bumper, jump the right bumper): P2's jump = A + every element
      the game binds to Jump / JumpBhop on P2's pad, slide = every element bound to Slide (PadBindings.AllFor /
      HeldAny). [pads] 'game actions on P2's ...' logs the bindings once. NOT CHECKED on real pads (tests have none).
    - Moai (user: did we do Moai rewards for P2?): P2's screen opened, but the picked item went to P1 (moai_p2: P2 took
      Slippery Ring, P1's evasion went up). P2's non-level-up reward screens now swap P2's item inventory in like P2's
      chest (P2LevelUp). moai_p2: P2's pick in P2's items, P1's own Moai pick in P1's.
    - Skins (user: P2 has to "buy" skins): P2 could already use exactly the skins P1 can use without buying (save:
      available = unlocked and bought; Street/Fire Monke unlocked but not bought need buying for P1 too). User: P2's
      row now only shows the usable ones (no locks / prices). skins_owned.
    - Slide pose (user: look once more): the game's own PlayerRenderer.Update runs on P2 while P2 slides (+0.3 s): P1's
      renderer pointed at P2's animator / movement copy / its own state copy, everything put back (P2Control.
      GameRendererUpdate). slide_pose: P2's animator in P1's slide state 459278484 with the same timing. Running it all
      the time took P2's run/idle to state 998495048, so only while sliding. NOT CONFIRMED by eye yet.
      BUG it caused (user: P1 Megachad faced right while going straight, spun with nobody touching the pad): the
      borrowed update also turned P1's renderer object (p1_facing: P1's model 180/270 while P2 slid east, desired 0).
      Every transform from P1's renderer object down to the model keeps its rotation now; p1_facing: P1 stays at 0.
    - Map flicker (user: P2's map still flickered; live log: opened/closed 15 ms apart on every pull - polling reports
      the trigger only in the frame it moves): P2's trigger = its value (GetAxisById) once readable, else a press
      counts until the release is polled. 'P2 map:' lines are logged without debug. NOT CHECKED on real pads.
    - Map (user: P2's map stuck open from the second time; a player going down with the map open left it on screen):
      P2's trigger now counts only on its positive side (a released trigger rests at -1 once pressed - the likely cause;
      not seen in a log, [map] lines were debug-only). P1 down with the map open: the game's map is closed for P1
      (map_twice: count 1 -> 0, P1's panel hidden). P2 down already closed P2's.
    - 6-minute perf run (user; tests\perf_6min.txt, new autotest 'autopick': level-ups take offer 0, chests open+take;
      first version clicked the hidden level-up screen - fixed to isLevelingUp / P2's screen): mod 1.8 ms/frame early,
      a step to 3.3 ms when P2 took the Bananarang, 4.2-4.4 ms at 150-240 enemies. Cause: CatchBananas searched the
      scene for ProjectileBanana every frame; P2's bananas are now kept as objects. Rerun: 2.0-2.6 ms to the end
      (190-235 enemies); perf_banana (P2 Bananarang, ~64 enemies): 1.8 ms. Biggest remaining part: billboards ~0.8 ms.
      [perf] lines now also count P2 stat recomputes (0-2 per 300 frames - fine).
    - Pots (user: XP from pots P2 broke went to P1): the drops start flying to "the player" a moment after the break
      (neither the Interact call nor InteractablePot.SpawnStuff saw them). A pickup that starts following P1 within
      4 m of a pot P2 broke in the last 4 s goes to P2 (P2Pickups.PotBrokenByP2 / BeforeFollow). pot_p2: 5 pots,
      P2 xp 0 -> 20, P1 0. The pot's luck bonus stays P1's (giving it to P2 via AsP2 stopped the pot's drops).
    - Clock (user: P2's didn't slow time; should act like P1's, effect on both): EPickup.Time picked up by P2 is applied
      as P1's (P2PowerUps). pot_clock: P1 gets TimeFreeze 11.3 s from P2's clock; its light-ray effect fills both halves.
    - Reward screens (user: make the inventory/stats panels on the other half bigger; then "pick whichever keeps both
      readable"): RewardLayout sizes them to fit the half - side by side or stacked, whichever allows the bigger
      scale, at most the game's own size. At 1920x1080 that's side by side at x1.00 (was stacked at x0.65; stacked
      would be ~x0.7 and the stats panel narrow). reward_panels test: P1's level-up, P2's level-up, P2's Moai.
    - Minimap with a player down (user: "when p2 dies, the minimap uses p1's"): with one player down the single view
      follows the other, but the map was always P1's - wrong when P1 is down (centred on P1's gravestone). P1 down ->
      P2's map in the shared spot, P1's faded out (P2Minimap). minimap_down: P2 down -> P1's map on P1; P1 down ->
      P2's map on P2. (A Megachad P2 survived 'kill p2' - his Flex blocked the hit, the game's rule.)
    - Campfire item for P2 (user asked): works - the fire appears at P2, heals P2 75 -> 135 in ~4 s like P1
      (tests\campfire.txt).
    - Tried and dropped: a [Debug] NoEnemySpawns config (hooked SummonerController.Tick before the config was read).
  1.0.0-alpha gold popups (user: the $gold popup when a player earns gold showed for P1 on P2's screen - add one for
    P2, move P1's). GoldPopups.cs: the game's HUD 'T_Gold' (XpAndGoldText, prefix "$", centre +62 px). Each player's
    at the same spot relative to the middle of their view (P1's half / P2's half while split, gliding to the shared
    centre as the views merge, P2's 40 px below then). P2's = a copy (its own XpAndGoldText). XpAndGoldText.Add while
    P2Gold is swapped in -> P2's popup; P2Gold.Collected (a coin paid to P1 then moved) takes it off P1's and adds it
    to P2's. tests\gold_popups.txt (70 m apart): P1 $4 on P1's half; P2 $1 -> $8 on P2's half, P1's unchanged.
  1.0.0-alpha character select arrows (user: the 4 corner arrows round a character icon / Confirm don't appear for
    P2, and are a little too large for P1). [arrowstate]: the game sets the arrow's size again every frame (newSize 165
    for a 135 button shown at 101 px) and OnButtonHover(MyButton) calls the RectTransform one past the old hook.
    SelectionArrowFix now scales the arrow after SelectionArrow.Update by the selected button's PARENT's on-screen
    scale (x0.75; not the button's hover pop). P2: a copy of the game's arrows ('SelectionArrowNEW') made under a
    switched-off holder (its script's Awake took over SelectionArrow.Instance otherwise), tinted red; its own parts by
    path - the arrow rect (SelectionArrow.rectTransform, under 'Mask') moved onto P2's cursor (character, skin or
    Confirm) with the game's size rule (rect + sizeOffset 30), the 'Mask' unclipped and full screen. P2's panel's
    Confirm (copy of B_Confirm) is a cursor target now: picking a character moves P2's cursor onto it (like P1's), A
    there confirms; A on the picked character still confirms. tests\charselect_arrows.txt. TEMP [arrow] log.
  1.0.0-alpha F keys removed (user: remove the f commands from the build): F1 (P2 test health), F2 (P1 test health),
    F9/F10 (old pad-read diagnostics, code deleted), F11 (P2 half a level). The test-health functions stay for the
    autotest (godp1/godp2), no key reaches them.
  1.0.0-alpha camera (user: "increase the distance until it splits to about 4x"): SplitDistance 15 -> 60, MergeDistance
    12 -> 48 (config entries still holding 15/12 are moved to 60/48; the user's cfg migrated). The shared view now pulls
    back as far as the screen's horizontal FOV needs to keep both players in (side by side, 3 m margin), the old x1.6
    at 15 m as the minimum; the height limit for merging stays 15 m. tests\cam_split_distance.txt: merged at 5/25/45 m
    with both on screen (45: near the edges), split at 70, merged again at 30. Zip rebuilt (same 1.0.0-alpha).
  1.0.0-alpha = "SplitBonk V1.0 Alpha" (user: make the mod version and the release zip say 1.0 Alpha). Plugin.Version
    "1.0.0-alpha" (BepInEx log: Loading [MegabonkSplitscreen 1.0.0-alpha]); Plugin.ReleaseName "SplitBonk V1.0 Alpha"
    feeds the menu label, the README title ({RELEASE}) and the zip name: dist\SplitBonk-V1.0-Alpha.zip. Guid / plugin
    name / folder unchanged (existing installs and BepInEx.cfg keep working). Old dist\MegabonkSplitscreen-0.49.*.zip left.
  v0.49.15 DONE (user, for the SplitBonk V1.0 Alpha release): "SplitBonk V1.0 Alpha" in small text at the top left
    of the main menu (MenuLabel.cs): a copy of the game's version text (VersionNumber.t_version 'Version (1)' under
    'UI', font size 24) anchored top-left 20 px in, shown only while MainMenu.tabMenu is open. tests\menu_label.txt:
    shown on the main menu, hidden in the Players lobby.
  v0.49.14 DONE (user: give P2 its own summary on the end screen - character, kills, level, inventory, damage).
    P2EndScreen.cs. End screen = DeathScreen.statsWindow 'StatsWindows' (HorizontalLayoutGroup): W_Damage (560x930,
    GameOverDamageSourcesUi), W_Stats (700x930, DeathStatsUi), quests ([endscreen] log, endscreen_dump.txt).
    - Damage: RunStats.OnEnemyDamaged - a P2 hit (P2Items.HitOwnerOf) moves its damage from the shared
      RunStats.damageSources to P2's list (a source only P2 used leaves P1's list).
    - P2's panels = copies of W_Damage / W_Stats (under an inactive holder; Window script, B_Continue/B_Back off):
      damage rows from the game's (switched-off) 'Entry' template + DamageSourceEntry.Set; inventory by the game's own
      ShowInventory with P2's weapon/tome/item holders in P1's inventory for the call (its switched-off 'ItemContainer'
      template must stay - clearing it gave a null error); P2's portrait/name, kills (P2Pickups.KillsP2), level; P1's
      kills = the game's total - P2's. Gold/unlocks/silver/Confirm only on P1's.
    - Layout: DAMAGE pair stacked in one column (P1 top, P2 bottom); SUMMARY pair side by side full height (in halves
      P1's Confirm was cut off); the row scaled to the screen (2580 -> x0.73) and moved by where its windows really
      are after a forced layout pass (centre alignment didn't centre the overflow). Titles "PLAYER 1/2 ...".
    - tests\end_screen_death.txt (tier 1 death runs) and tests\final_win.txt (user: wins may change the save - they'll
      wipe it when the mod is done): both end screens show the 4 windows; Confirm (P1's pad) goes back to the menu
      (lobby, P2 still joined). The win unlocked Energy Core (Defeat a Final Boss 2 times). The "Missing localization
      for DamageSources.autotest" rows are the test's own kills (killbosses/killfinalboss), split by the nearer player.
      Not tested: a long run's full inventory.
  Final win (user: beat the boss at the end, what does the final screen show) - tests\final_win.txt ('killfinalboss',
    'screentext'): Bark Vader dies -> 'PortalFinal' (InteractablePortalFinal) -> the game's end screen: DAMAGE / SUMMARY /
    QUESTS. It is P1's only: summary = P1's character (Monke), kills, time alive, level, P1's inventory; the damage list
    mixes P2's weapons in (Firestaff) - the damage sources are shared; quests/unlocks counted for P1's character only.
    'Missing localization for DamageSources.autotest' = the test's own kill. !! The run UNLOCKED Sir Chadwell (Complete
    Forest Tier 3) and Fire Monke (Final Boss on Tier 3 as Monke) on the user's save - the test forced tier 3. Ask
    before tests that end a run.
  v0.49.13 (user: one player takes the portal while the other is dead - "ideally the dead player is revived in the
    next stage"). Before: P2 down in stage 1 -> still down (0 HP) in stage 2. FIX: Downed.OnStageStart from the new
    stage's spawn portal (P2Body.OnPortalStart): the old stage's revive countdown, arrow and gravestones cleared; a
    downed P1 is revived at once (full HP, weapons and pickup range back - before the portal moves P1, HoldP1 would pin
    P1 to the old stage's spot); a downed P2 is revived when P2's health starts on P2's new body after P2's entrance
    (P2Health.ReviveOnStart). tests\portal_while_down.txt (Forest tier 3): P2 down -> P1 through -> P2 100/100 in
    stage 2; P1 down -> P2 through -> P1 back in stage 3 (not down, model shown, at the new spawn).
  v0.49.12 (user: test each debuff's real source and fix the bleed leak; "that run looked good"):
    - Bleed leak: tests\debuff_sources.txt with [dmg] (every DamagePlayerExternal while a debuff is on): P1's bleed
      ticks unborrowed, P2's borrowed - each lost exactly its own 30; the 10 HP crossover didn't come back.
    - Scorpion touch (P2 / P1 alone, 40 m apart): only the touched one gets Poison. MinibossScorpion next to both: both
      Poison, same HP loss. Already right (P2's touches run the game's hit on P2's borrowed state/list).
    - FIX mud: MinibossPig 'PigMudSplash' (EnemyProjectileMud) calls SlowPlayer(3 s) inside its hit, on P1's list only.
      P2BossAttacks records the debuffs the game gives during a special attack's hit (P2Debuffs' hooks) and gives P2 in
      the hit the same (else the last ones seen for that attack kind; MudSplash seeded Slow 3 s). tests\debuff_mud.txt
      ('bossattack <attackName>' = the game's UseSpecialAttack): both Slow x0.50 3 s, same damage.
    - FIX Bark Vader's orbs (tests\debuff_barkvader.txt, real route to the final stage - TestFinalBoss() only sets a
      flag): BossOrbBleed = Bleed 5 s, BossOrb (following) = Freeze 1 s, BossOrbShooty = damage only. An orb hitting P2's
      collider now runs its own OnCollisionEnter as P2's hit (P2Health.AsP2Hit: borrowed health, P2's status list as the
      player's - P2PowerUps.OnStatusSwapped -, P2's movement as PlayerMovement.Instance). Both bleed / freeze now.
    - NOT CONFIRMED: BossPoison's source (8 s, 30000 = 3% max HP per s). Seen once on P1 right after a 90.5 'Enemy'
      hit while P1 stood on Bark Vader; standing on him again ('toboss <p> 1 top', 'touching <p>') touched nothing.
      P2 takes BossPoison right when it has it (debuffs_each).
    - Freeze source = Bark Vader's following orbs (no other found). BossPylon charges through OnTriggerEnter() (no
      collider argument) - P2 probably charges them; not tested.
  v0.49.11 (user: the 30000 was a poison debuff, P2 has none - find all debuffs and test each one):
    Player debuffs = EStatusEffect Slow, Freeze, Bleed, Poison, BossPoison (PlayerStatusEffects.SlowPlayer/FreezePlayer/
    BleedPlayer/PoisonPlayer/BossPoisonPlayer). P2 already has its own PlayerStatusEffects (P2PowerUps.Status, ticked).
    tests\debuffs_each.txt (each debuff on P1 alone, then P2 alone, 'debuff <p> <kind> <secs>' + 'statuses'):
    - Slow (MoveSpeedMultiplier x0.50) and Freeze (x0.08): same for P2. OK.
    - FIX: P2's Bleed/Poison/BossPoison ticks damaged P1 and not P2 (the tick hurts "the player"). P2's tick now runs on
      P2's borrowed health while one is on (P2Health.StatusTick). Now: Poison -40000 / BossPoison -120000 over 4 s each,
      the same for both, on their own turn only.
    - OPEN: Bleed still crosses over a little: during P1's bleed P2 lost 10, during P2's bleed P1 lost 10 (2 ticks of
      5) - another bleed damage path; log it next.
    Sources (debuffsources, the game's data): poison on touch = Scorpion, ScorpionMedium, MinibossScorpion (Desert).
      Debuff-looking attacks: MinibossScorpion PoisonSpikes (ManyRandom, PrefabSingle) + PoisonSpikeProjectiles
      (EnemySpecialAttackHitscanMultiple); MudSplash (EnemyProjectileMud: Ghostham, MinibossPig - slow?); Bark Vader
      (FinalFightController: bleed orbs BossOrbBleed, BossPoison - probably his touch); Freeze source not found yet.
      Not yet checked which attack gives which debuff ([debuff] log hooks are in: P2Debuffs.cs, TEMP).
    - Attack kinds still without P2's hit/collider fix: EnemySpecialAttackHitscanMultiple, SpinningLasers, TargetLaser
      (Pharaohs), GhostKingAttackDarkness, CactusProjectile, BossOrb*, BossMeteor, BossPylon. EnemyProjectileMud added.
  v0.49.10 (user: test stage 3 - "maybe tier 3"; Bark Vader's tree spikes didn't hurt/push P2; P1 got hit outside
    the red circle). tests\stage3_tier3.txt (Forest tier 3; tier 3 isn't unlocked on the test save - the run config is
    set directly): stage 1 (P1) -> 2 (P2) -> 3: IsTierFinalStage True, its altar is InteractableBossSpawnerFinal ->
    the final boss stage (own scene, arena floor 'B_FinalBoss', boss "Bark Vader"); P2 comes out of its portal there.
    - [bossatk] log: enemy special attacks hit inside their SpawnHitEffect (Bark Vader: EnemySpecialAttackFollowing
      'RootSpikes', radius 6-9, 103 damage, following P1); the game looks for "the player" in the sphere and calls
      PlayerHealth.DamagePlayerExternal(damage, knockback 25, up) on P1. P2's movement collider is on the player
      layer, so P2 in the sphere made the game hit P1 wherever P1 was (P1 launched again and again up to y 340).
    - FIX (P2BossAttacks.cs): SpawnHitEffect of EnemySpecialAttackPrefabSingle/Following/PrefabMulti/ManyRandom and
      EnemyGroundSlam runs with P2's collider off (P1 only hit when P1 is in it); P2 within radius + 1.5 m takes the
      same hit (P2Health.SpecialAttackHit: the game's DamagePlayerExternal with no knockback - it would push P1 - plus
      P2Movement.Push(dir * knockback)). Checked: both lose 103 per hit, both go up ~10 m, P1 not hit by far ones.
    - Not checked: P2 touching Bark Vader (P1's touch: 30000 = 3% of the test's max HP); other hit paths that may
      have the same P2-collider problem (DamagePlayerTrigger, DamagePlayerCollider, DamageZone, BossOrb*, BossMeteor,
      BossPylon); killing Bark Vader / the end of the run.
    - Test words: trackp1 <secs> (P1 position/velocity + one-frame jumps), stagestate shows tier final / final boss
      stage / the altars and portals.
  v0.49.9 (user: only tier 1 was tested - test tier 2 and what happens after stage 1's portal; "try it on forest"):
    A tier 1 run has no stage 2 (its altar only summons the boss, no portal - user). Tier 2: boss altar -> boss dies ->
    the altar's 'PortalBlue' (InteractablePortal) opens -> interact = next stage (Forest: StageForest1..3).
    - FIX: P2 could summon the boss again and again, and couldn't enter the opened portal (P2's nearest-interactable
      search took the used altar). [interact] log: the altar's collider goes tag 'Interactable' -> 'Untagged' once used;
      the game's DetectInteractables.tagInteractable is 'Interactable'. P2's search now takes only colliders with that tag.
    - FIX: ~280 KeyNotFound errors per stage start (WeaponBase.UpdateStat): P2's weapon stat refresh asked every weapon
      for every EStat; now only the weapon's own weaponStats keys.
    - Checked over the stage change (both portal paths): levels, weapon levels, items, gold kept for both; P2 comes out
      of its portal next to P1 with control and camera. Not a bug: P1's gold reads P2's while P2's level-up screen is open
      (the game's gold holds P2's for that screen's purchases).
    - Tests: tests\stage2_tier2.txt (Desert, P1 takes the portal), tests\stage2_p2portal.txt (Forest, P2 summons, a 2nd
      press must do nothing, P2 takes the portal, both level up first). New test words: 'chars A B tier 2 map Forest'
      (run config only - no click, so the user's saved map/tier stay), stagestate, killbosses, toportal <p> [far],
      addxp <p> <n> (with 'xp on'), pickoffer (A on the offer button; a level-up screen left open pauses the game).
    - Still open: the game's PlayerMovement.Update NRE spam on the main menu (~200 per launch, hooked method; harmless so far).
    TEMP: [interact] candidate list in P2Interact.TestInteract.
  v0.49.8 DONE (user: P2 can't select character skins, "the same way as p1, through the character select"): P2's
    copied skin row (CharacterInfoUI.skinSelection, 'Skins (1)') is rebuilt from copies of one copied SkinContainer for
    P2's character: DataManager.GetSkins sorted by SkinData.CompareTo, owned = MyAchievements.IsAvailable ([skins] log:
    same order and locks as P1's row). P2's cursor moves into the row like any button (screen-space nearest; the row is
    left of P2's grid, entering lands on the nearest = last skin), A on an owned skin picks it. Avatar: the game's
    PlayerRenderer.SetSkin (it uses m_player_preset (Instance)); P2's body in the run: skin.materials on the renderer with
    the same name. Each character remembers P2's skin this session. tests\p2_skin.txt ('allskins' = test-only: every
    skin usable; the test save owns only default skins): Gold Sir Oofie in the menu and the run. Hats: not done.
  v0.49.7 FIX (user: pressed Play with the Steam Controller, the Xbox pad became P1): log - "Last pad pressed outside
    character select: nothing" on the first visit after launch, so the fallback (the game's current controller = Xbox #1)
    was used and the Xbox pad's Start presses counted as P1's. Now with no Play press seen, P1 stays undecided: the first
    pad pressing a non-Start button is P1; Start with exactly one other pad = joins as P2 (the other is P1); a lone pad
    is P1. Autotest join keeps the old fallback. TEMP [pads] logs the first 30 presses outside character select, to see
    why the Play press was missed (not found yet - maybe Steam's desktop layout before the game had focus).
  v0.49.6 FIX (user: inconsistent character select, sometimes a player can't get to map select): the game's
    CharacterMenu.OnConfirmCharacter only moves the highlight to the Confirm button (B_Confirm, click calls
    [MainMenu.GoToMapSelection]); P2's confirm stopped there. P2Select now also submits B_Confirm 0.1 s later, like a pad.
    Menu tests (first line 'menuscript' = no automatic menu phases): tests\menu_p2_first.txt (P2 confirms first, P2 runs
    map select) and tests\menu_p1_first.txt (P1 first) both reach a run. P1's character A can't be faked (the game reads
    P1's pad itself); 'p1confirm' calls the same two game methods instead. Commands: p2press/p2join/p1pick/p2pick/
    p1press/p1confirm/lobbyconfirm/menustate/waitselected.
  v0.49.5 DONE (7): P2's jump/land dust - [dust] log: the game's PlayerFx (on P1) plays jumpFx/landFx from
    PlayerMovement.A_Jumped / A_Landed, which P2's movement routes to P2's own listeners. P2Dust.cs adds two mod listeners
    to P2's routed events: a copy of jumpFx ('PlayerJump') / landFx ('PlayerLand') at P2's feet (P1's landing camera
    shake stays P1's). The model's AnimationFootstepFx (foot-event dust) is kept on P2's model copy. tests\p2_dust.txt.
  v0.49.4 FIX (user: P1 extremely slow at round start until P2 died): since 0.49.1 P2's every-frame health tick runs
    with P1's object at P2's spot (AtP2Spot in Borrow); putting it back at its drawn (interpolated) position dragged
    P1's rigidbody back each frame. All such moves now return P1 to its rigidbody position (P2Items.P1Home) - also
    RunAsP2, item projectiles, P2's item changes, tornado, power-ups, bananas. tests\p1_speed.txt: 31.8 / 36.8 m in 3 s.
  v0.49.3 DONE: (1) P2's model hidden until it passes its portal (shown at P1's recorded passing point - [portal] log),
    (2) P2's camera doesn't turn during P2's entrance (P2Body.Entering), (6) the gravestone marker hides while paused.
    Tested: tests\small_fixes.txt (Desert - tests use the map last selected in the game). Still to do: (3), (4), (5).
  TODO (user 2026-10-19, small): (1) P2's character model can be seen behind the spawn portal while it opens (P1's is
    hidden until it comes out - compare P2Body's portal animation, AnimatePortal / when P2's model is shown); (2) P2 can
    turn the camera while P2's portal is spawning (P1 can't - P2Camera turning should wait until P2 is in control);
    (3) P1 has a small health + shield bar at its feet (world-space 'PlayerUI' child, a HealthUI with followPlayer -
    Downed hides it while P1 is down), P2 has none: give P2 a copy driven by P2's HP/shield (like P2Hud's bars);
    (4) the full map (right trigger) isn't uncovered where P2 goes - the game's fog of war (FogOfWar, QueueRevealFog,
    InitFogTexture) reveals around P1 only: also reveal around P2 (look up its reveal call/fields by name first);
    (5) user: with P1 down, the game's low-health sad music + red screen vignette stay until P1 is revived (likely
    because Downed now holds P1 at 1 HP) - fade both out 5 s after P1 goes down (find the low-HP music/post-process
    listeners by name first: MusicController, DeathPP, a low-HP volume...), back to normal on revive;
    (6) the revive arrow / gravestone marker (ReviveUi's overlay canvas, sortingOrder above the game's UI) draws over
    the pause menu - hide it while the game is paused (MyTime.paused / a menu window open), back when unpaused;
    (7) user: P1 (Monke at least, maybe all) gets a dust effect when jumping and landing, P2 doesn't. Likely a
    listener on PlayerMovement.A_Jumped / A_Grounded (P2's movement events are routed to P2's own listeners only) or
    the model's AnimationFootstepFx (game scripts are stripped from P2's model copy) - find which by name, then give
    P2 its own (an effect copy on P2's body, played on P2's jump/land).
  NEXT - HAZARDS FOR P2 (user: "are there any other environmental hazards, or stuff that can push the player around"):
    names from the interop (by class): Tornado (FIXED v0.49.2, P2Hazards); PlayerMovement.PushPlayer /
    PushPlayerButKeepMovement (explosions - P2Explosions pushes P2 itself; older note: exploding enemies knocked P1
    back but not P2 - recheck); JumpPad.OnTriggerEnter/GetForce; PlayerMovement.BouncePlayer(pushForce);
    PlayerMovement.OnLavaEnter/Exit + Water.SetFloorIsLava + GraveyardBossRoom.UpdateLava (lava); PlayerMovement.
    OnWaterEnter/Exit(Water) + WaterMovement; grind rails: DetectRails (a script on P1 - removed from P2's movement
    copy) + PlayerMovement.StartRail/StopRail, Rail; PlayerMovement.TeleportPlayerBackToBounds (out of bounds);
    PlayerStatusEffects.FreezePlayer/SlowPlayer/BossPoisonPlayer (enemy/boss debuffs - land on P2 or P1?); HealingZone.
    Probe: tests\hazards_probe.txt + autotest commands hazards / hazard <pad|water|rail> <p> / hazardstate.
    Forest (2026-10-19): 0 jump pads, 0 rails, 1 Water (big volume under the map - P1 and P2 both just fell, same),
    25 tornadoes. NEXT STEP: tests can't pick the map yet (Autotest.MapSelect takes the selected one - add a 'map
    <name>' script line: look up MapSelectionUi / runConfig.mapData names first), then probe jump pads / rails /
    lava on the maps that have them, and the enemy debuffs.
  v0.49.2 (user, playing with P1 down: P2 couldn't level up, got no shrine rewards, P2's clock did nothing, billboards
    didn't face P2's camera, tornadoes didn't touch P2, P1's weapons kept going, P1 came back with a pile of chest
    rewards): the game checks "the player" (P1) is alive before reward windows (EncounterWindows.LateUpdate pops its
    rewardQueue), pickups, power-ups - at 0 HP everything queued/failed. Downed now holds P1 at exactly 1 HP while
    down (death call still skipped; 0 again when P2 also goes down, so the game over still happens) + the reward queue
    hook as backstop. P1's weapons off while down (WeaponInventory.ToggleWeapon; auras follow), P1's pickup sphere off,
    P1's object at the grave can't take free chests. Chest windows keep their owner from queue time (P2Interact
    chestOwners via P2LevelUp's AddEncounter hook) - the old 3 s mark ran out for windows queued while P1 was down.
    Billboards: in the merged/solo view P1's camera is drawn from the shared spot, the game aimed at its own spot -
    turned for that drawing (and canvas numbers for real). P2Hazards.cs: Tornado.FixedUpdate run again for P2 near it
    (P2 as the player, the tornado's own body kept). Tests: tests\p1_down_rewards.txt, tests\p2_tornado.txt - all passed.
    Test helpers: p2levelup, shrine <p>, rewardqueue, tornado [near], tornadoes ('p2reward' opens an empty window -
    don't use: no offers without a real shrine).
  v0.49.1 (user feedback after playing 0.49.0): P2's popups now come from the game's EffectManager listeners
    (OnDamage(ph, dc, shieldDamage) / OnHeal) added to P2's routed PlayerHealth events in Borrow - blue shield numbers,
    green lifesteal heals (other P1 listeners like camera/controller shake stay P1's; mod's own popup only as fallback);
    free chests (OpenChest.OnTriggerStay touched by P2's body) open as P2's window; P2's body keeps a single shield
    Outline styled like P1's (a model copied after P1's 'Chad' outline exists brought a second one - the double shield);
    P2's test health no longer skips P2's max values (shield tome gave no shield in tests). Tests:
    tests\p2_popups_shield.txt, tests\p2_outlines.txt, tests\p2_shield_tome.txt. Not reproduced: the double shield itself.
  DONE v0.49.0 CHEST ITEMS - ITEM SWEEP: all 88 items the same for P1 and P2 (stats, procs, effects, heal, gold, no
    leaks). Last fixes: P2Weapons p2Bananas dropped a reused banana set up by P1 (P1's hits procced P2's items);
    P2Items.AtP2Spot (P1's object at P2's spot + new things under P1 moved to P2 + "(Clone)" effects that start playing
    in P2's scope stopped on P1 and played on a P2 copy - Electric Plug) used by RunAsP2 and P2GameHealth.Borrow; P2's
    item setup with P2 as the player (Scarf); XP orbs to P2 armed from P2's Shattered Wisdom; P2ItemFx.cs (copy of P1's
    Feet/ItemEffects - HolyBookEffect/ToxicBarrelEffect - and mirror particles; ItemHolyBook/ItemToxicBarrel.A_OnUse,
    ItemMirror.A_MirrorReady, ItemBase.A_ItemAdded routed by owner in ItemEvents). Chance items (Bonker 2%) and
    cooldown items (gloves: first proc after the cooldown - fight 10 s) can read as one-sided in a single run.
    Sweep design (user): players 40 m apart on fixed spots ('home', 'swap'), scenario per item (stat/fight/hurt/move/
    still), enemies only around the holder when needed, players still unless moving is the point; P1's 'hurt' uses
    the game's DamagePlayerExternal like P2's. TEMP logs to clean later: [items] calls / P1's hit / P2's hit, [itemfx].
  (was) IN PROGRESS v0.49 CHEST ITEMS (user 2026-10-19: chest rewards apply to P1 and P2 separately; tests for every chest
    reward on both players; P2 can pick the rewards of a chest P2 opened; P1's and P2's items apart in the HUD):
    - Found (interop dump + TEMP [items] counters): 86 ItemBase classes; P2's items only had their stat modifiers.
      P2's attacks/hits ran P1's items (PreAttack/PostDamage), P2's never ticked, P2's item listeners heard P1's
      events; the item events (ItemInventory/ItemBase A_ItemAdded/A_ItemRemoved/A_StatsChanged) are static.
    - P2Items.cs: static item events muted while P2's inventory changes (P1's HUD no longer shows P2's items); P2's
      item listeners on player events join P2Abilities' routed ones (SnapshotEvents/RouteEvents/DropRoutedTargets);
      RunAsP2 = P2's borrowed health + stats + items + stat holder + gold + movement, P1's object at P2's spot;
      FixedTick ticks P2's items; PreAttack in P2's context -> P2's items; PostDamage by hit owner: projectile
      scope (HitIn/HitOut on projectile FixedUpdate/Update/HitEnemy... - IL2CPP: only Unity's own calls reliably go
      through hooks; DamageContainers are reused, tagging them failed) else the nearer player. A_StatsChanged during
      P2's runs -> P2 dirty; a marker rides on the game's event too. P1Items: P1's own inventory also during swaps.
    - ItemEvents.cs (scan with every item): Enemy.A_EnemyDied/A_Damage/A_EnemyReleasedFromPool (hitter),
      InteractableChest.A_ChestOpened + OpenChest.A_Open (opener), DetectInteractables.A_Interacted (P1 only;
      P2Interact calls P2's), GameManager.A_StageStarted + MyTime.A_TimeScaleChange (both, each as itself) - both
      players' item listeners off the event, one mod listener dispatches by owner.
    - P2ItemsHud.cs: P1's row ('ItemsUi', GridLayoutGroup) = left half, P2's copy (game cells
      ItemsHudElementPrefab.Set/SetAmount) = right half; restored without P2.
    - Tests: tests\chest_p2.txt (P2 opens -> P2's window + controls, item to P2; P1's chest -> P1; credit cards stack
      for the opener only - PASSED v0.49), tests\items_probe.txt, tests\items\items_1-4.txt (tools\gen_itemtests.ps1;
      ItemCheck.cs isnap/icheck/idelta; tools\itemsweep.ps1). P1 test health now lets HP move (refill below half).
    - Autotest commands: item/unitem/allitems/items/itemscan/dmgsources/gold/givegold/chest/p1interact/p2interact/
      chestwin/chestprice/isnap/icheck/idelta/uinav/uiclick/restart/objsnap/objdiff/nopowerups.
    - Chest price (user: tracked separately for P2, HUD element for P2, P1's on P1's screen): P2ChestPrice.cs -
      MoneyUtility.chestsPurchased/chestPriceIncrease (static, one count) swapped to P2's during P2's prompt,
      interaction and chest window; A_ChestPriceIncreased muted during the swap, P1's refreshed after. HUD: P1's
      'T_ChestPrice' (sprite tag + number, right-aligned in a wide box) moved so its visible right edge sits 110 px left
      of the screen middle; P2's copy at the original top right. PASSED (tests\chest_p2.txt, tests\itemfx_probe.txt).
      IL2CPP lessons: 'as RectTransform' is always null (GetComponent/TryCast), GetWorldCorners gives zeros.
    - Item effects (user: some treasure reward vfx don't show on P2): children appearing on P1's object/model during
      P2's runs move to P2's body ([itemfx]); P2's ItemProjectiles (ghosts, orbs) step with P2 as the player
      (ItemProjectile.Set/FixedUpdate hooks) - PASSED: P2's Ghost/Bob's Dead ghosts stay with P2. ItemCheck also
      compares attached effects per item; one screenshot per item (fx_<item>) in the sweep.
    - Tests: every charge shrine is switched off by clearloot and reward windows (EncounterUi) auto-close (user: a
      charge shrine blocked a run); power-ups hidden ('nopowerups') - they showed as stat leaks.
  (old) TODO NEXT (user, 2026-10-18): after the death state is done, handle P2's chests and rewards (a chest opened during the
  revive test and stopped it; who opened it is unclear - P2 was down).
  DEATH STATE (user's plan, piece by piece with the user watching): the run ends only when both are down; a downed
  player gets a gravestone at the death spot, a green skull on map + minimap, an arrow on the other player's screen;
  the alive player stands there ~10 s (charge-shrine fx as the countdown); then a portal in the dead player's colour
  opens on the ground facing up and the dead player jumps out of it alive (user: collision off while coming out, so
  they pass through the ground and the portal).
    DONE piece 1 (v0.45.0-0.45.1, tests\down1.txt): Downed.cs. P1 at 0 HP while P2 is alive: KillPlayer/PlayerDied
    skipped (P2GameHealth's prefixes -> Downed.AllowP1Death), P1 down (model hidden, held still, enemies all go for
    P2). Held off (log): PlayerHealth.A_Died = SteamTimelineManager, TrackStats.OnDead, MusicController, GameManager.OnDied,
    PlayerInput, DeathPP, PlayerRenderer.OnDeath, MyPlayer.OnPlayerDied. P2 down -> P2Health.GoDown + Revive().
    Both down: the hold is lifted and the game's own CheckDead kills P1 (v0.45.0 called KillPlayer itself -> CRASH,
    dump: inside KillPlayer under P2's damage code). Autotest: kill/revive/downstate.
    P1 revive = half max HP (placeholder until piece 6).
    DONE piece 2 (v0.45.2-0.45.4, tests\down2.txt, tests\findassets.txt): Gravestones.cs - a copy of the game's
    'Gravestone' prefab (slab 1.2 m high, 3.3 m long, m_gravestone) at the death spot, scripts + collider taken off,
    centred on the spot, tilted with the slope, turned the way the player faced; on revive its own 'StoneExplosion'
    particles (Poof/Remains/Sparks) play. It carries the game's minimap icon 'MinimapIcon (1)' (8x8 quad, layer
    UiCamera, m_encounterGravestone) - piece 3 swaps it for the green skull. While P1 is down P1's 'PlayerUI'
    (world health bar) and 'MinimapIcon' (minimap arrow) are hidden too.
    Found for later ([findassets]): Skull textures, ChargeShrine + ChargeOuter/ChargeSparks/ChargeShrineFinishFx,
    PlayerSpawnPortal, PortalBlue, PortalArrow. User's extra: a "REVIVED" text mid-screen, spinning, impact frame.
    Gravestone stands (user: flat, then upside down): tipped -90 deg on X, lettered face towards the other player,
    60 % size, base on the ground.
    DONE pieces 3-7 (v0.46.x, tests\revive.txt, tests\down3.txt, tests\describe.txt):
    - 3 map skull: the gravestone's own map icon (shader Custom/MinimapIconInstancedCutoutColor: _MainTex/_Color)
      with the game's 'skull' texture in green, 14 m on the map, MinimapIcon script kept; P2's red map arrow is
      hidden while P2 is down (P1's already was).
    - 4 arrow (ReviveUi): green arrow in the alive player's view - over the gravestone when on screen, else at the
      view's edge pointing at it, with "P1 23 m"; hidden while charging. Merged view: the shared camera.
    - 5 countdown (Revive): the ChargeShrine's force-field sphere (its renderer is off in the prefab - turned on,
      green), its 'Circle'/'Progress' ring (resized from 0.3 m to 2.5 m, above the stone), its Start/Loop/Complete/
      Abort sounds and finish fx. 10 s within 4.5 m; outside the progress drains at 2x (user: goblins push the
      helper out - a reset was too harsh).
    - 6 portal: PlayerSpawnPortal copy turned to face up (P2's red via P2Body.TintRed), ring opens 0.6 s, the gravestone
      bursts; the downed player rises 0.7 s from 2.2 m under the ground (kinematic, colliders off - user), released
      1.2 m up at 15 upward (user: jump higher) with the game's airborne animation ("grounded" off, "jumping" pulse;
      user: their falling animation), full HP (user: they should be fully healed), portal closes after.
    - 7 REVIVED: spins in from 3x (-540 deg, ease-out-back), holds, swells and fades (1.8 s); impact frame: a white
      frame, a dark frame, fading wash. In the revived player's half - the whole screen while merged/merging.
    - Also: P2's flying bananas end when P2 goes down; P2's animator is held still while a menu pauses the game (user;
      not checked in a screenshot); the billboard pickup filter's interop error fixed (untyped GetComponentInChildren).
    - Autotest: tograve, go <p> grave, waitrevive <p>, clearloot, chest windows closed automatically ('keepwindows'
      to keep them), describe <prefab>, findassets <words>.
    - v0.46.3 solo view (user: when a player is dead, only show the alive player's camera): P2Camera.Solo - one
      player down forces the merged (full-screen) view, its pose behind the alive player's head instead of the
      midpoint; a downed P2's stick no longer turns it. tests\solo_cam.txt: P2 down -> P1's view full screen with the
      arrow over the revive dome; P1 down -> P2's view full screen. (The test's 'turn p2' now turns the shared camera.)
    - v0.46.4 (user, P1 Cl4nk + P2 Calcium: P1's shield kept going to 0, P1 kept getting a "+60 hp" effect):
      P2Inventory.RecomputeNow (every 10 s / when dirty) ran P2's stats through P1's PlayerStatsNew and its stat event
      (PlayerStatsNew.A_StatUpdate) told P1's PlayerHealth: max HP/shield to P2's (shield cut), then back (heal popup
      for the difference). The event is muted for the whole recompute; P1's maxHp/maxShield are put back too.
    - Leak sweep (user: test different character combos so nothing leaks; tools\leaksweep.ps1, tests\leak\*):
      6 mixed pairs (Cl4nk/Calcium both ways, Megachad/Vlad, Athena/Robinette, Ogre/Fox, Noelle/Bush) vs each
      character alone (A+A): every stat of both players equal at 0 s and 20 s; health untouched without hits (Cl4nk's
      50 shield stays). Only difference: Calcium's speed ramp (the baseline P1 was hit - resets it). Not covered:
      items, tomes, power-ups and level-ups across combos.
    - v0.46.5 (user: character/map selection in the tests can be faster): menu 3->1 s, character select 2->0.5 s,
      missing second pad 8/12->1/2 s, confirm fallback 4->0.6 s (the Confirm press never closes it alone), map
      2->0.5 s: menu to spawn ~14 s -> ~3.5 s per run.
    - v0.47.1: P1's own stat changes during P2's recompute are announced afterwards (user: P1's shield upgrades were
      slow - v0.46.4 had muted them too). Test: P1's max shield 0->25->50 within 0.3 s of each tome.
    - v0.48.x: revive jump 15 -> 22 (user: they get swarmed); revive zone 4.5 -> 6.75 m (user: 1.5x the sphere);
      arrow: the texture points UP (0 = up) - on screen 180 (down at the stone), off screen atan2 - 90 (user: it
      pointed up / the opposite way), 80 % size, label further in.
  DONE v0.47.0-0.48.3 LOBBY (user: a dedicated player screen after Play; P2 Start joins, model appears, B leaves;
    groundwork for 4 players; Confirm middle-bottom -> character selection; P2 defaults to P1's character):
    Lobby.cs - the first stage of character select when opened from MainMenu.GoToCharacterSelection (hooked; back from
    map select skips it). The game's character select UI (P1 grid/info, P2 panel - their parent area) is hidden by a
    CanvasGroup (alpha 0, not interactable), the game's SelectionArrow highlights switched off; 3D models stay. Four
    cards (P1 READY, P2 press START / JOINED (B to leave), P3/P4 coming later), CONFIRM button (P1's UISubmit or a
    click). PlayerJoin.Leave: pad back to Rewired player 0, CharacterSelectView.OnP2Left (avatar gone, camera marker
    back), P2Panel.Remove, P2Select.Unbind. On Confirm P2Select.DefaultToP1 (PickCharacter(selectedCharacter)).
    tests\lobby.txt ('lobbytest'): screenshots both in / P2 left (model + card gone, camera on P1) / back in.
    Not checked: the real pad paths (P2's B via PadHeld, P1's A via UISubmit) - the test calls the same functions.
    v0.48.4 (user: P1's stick picked characters behind the lobby): EventSystem.sendNavigationEvents off while the lobby
    is open (restored on close), and P1's selectedButton held (put back via OnSelectCharacter, [lobby] log). Built, not
    yet tested in game.
  OPEN FREEZE (user, v0.48.3, 23:48): P2 down, P1 died at P2's revive zone (9.4/10 s, stepped off) -> death screen ->
    the user pressed restart -> the game froze ~8 s after game over (Windows: AppHangB1, no dump; the mod's watchdog
    thread froze too - FailFast never ran; "New run started" never logged, so stuck during the reload).
    tests\restart_bothdown.txt (new 'restart' command: DeathScreen.Restart(); MapController.RestartRun is the static
    one) did NOT reproduce it (short run, killed by the test). Hang catchers: tools\autotest.ps1 (20 s silent log ->
    comsvcs MiniDump to H:\CrashDumps, closes the test game) and tools\hangwatch.ps1 for normal play.
  DONE v0.48.0-0.48.3 CAMERA MODE (user: two modes - split all the time, or dynamic as before - a pause menu toggle,
    kept across sessions): P2Camera.MergeEnabled (sets + saves MergeWhenClose in thesplitscreenguy.megabonksplitscreen.cfg);
    CameraModeToggle.cs: a copy of the pause menu's B_Settings button ([pause] log: B_Resume, B_Map, B_Settings,
    B_Restart, B_Exit) placed after it, game scripts/Selectable off, a Button toggling "CAMERA: DYNAMIC/SPLIT".
    (Found via PauseHandler; GetComponentInChildren<T>(true) throws the interop constraint error - plural used.)
    tests\cammode.txt: split stays split with the players together; dynamic merges again. Not checked: reaching the
    button with the pad's d-pad (the game's buttons may use their own navigation).
    v0.48.4 (user: the d-pad reaches the button but the 4 corner arrows don't move onto it): the arrows are moved by
    MyButton (taken off the copy) - CameraModeToggle.PointArrows calls SelectionArrow.Instance.OnButtonHover(rect) when
    the copy becomes EventSystem's selected object. Built, not yet tested in game.
    v0.48.5 VERIFIED by tests\cambutton.txt (d-pad Settings -> camera -> Restart -> back; selected = darker + arrows,
    same as Restart; Settings' PauseUi.GoToOptions off on the copy; navigation Automatic). Hover sound not checkable
    in a screenshot (MyButton is kept, which plays it). (was: user: no hover sound, no darkening like the others): the copy now
    keeps the game's MyButton (PlaySfx, OnSelect -> arrows, scale) and its Button (colour tint); only the Button's
    editor click calls are switched off (except MyButton's own) and Toggle added - names logged in [pause]. Explicit
    up/down links rewired Settings <-> camera <-> next (mode logged). PointArrows only for the plain-button fallback.
    Next: tools\install.ps1, then tools\autotest.ps1 tests\cambutton.txt (new 'uinav up|down' / 'uiclick' commands =
    the pad's move/submit events on the selected button) and check the screenshots + [pause] log.
    Not checked: a revive on a slope / near walls; both players reaching the same gravestone area while the second one
    dies mid-countdown; the zone's look in the merged view from far away. (none on screen during the checks; still turned by the
    cameras' difference).
  ORIGINAL PLAN: (1) P2Pickups attracts all power-ups near P2 too; (2) P2 own PlayerStatusEffects (new, remove its listener from Pickup.A_PickupTriggered), ticked in P2 context, MarkDirty on change; (3) P2's ApplyPickup: mute A_PickupTriggered and call each listener for P2 - PlayerHealth via P2GameHealth.Borrow, PlayerStatusEffects -> P2's instance, EffectManager with P2 as the player (MyPlayer transform at P2 + PullForP2 for magnet), GoldFilterUI as is, skip trackers; (4) swap inv.statusEffects to P2's in RecomputeNow and in P2GameHealth.Borrow; (5) rerun powerups.txt, compare screenshots pu_*.png.
- v0.42.8 installed (SHA256 35DBDFD2...C636).
- (v0.37.2 note) Spin: GetAuraRotationSpeed isn't deg/s (log: aura 2). P1's always-on attacks' real yaw rate is measured every 0.25 s (only while P1's model doesn't turn) -> factor = rate / game value; P2's spin = game value (read in P2 context) x factor. [spin] log.

