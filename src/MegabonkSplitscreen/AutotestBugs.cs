using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Autotest commands for the players' 1.0.2 reports (chest mashing, gold on stage 2, Dexecutioner, crypt doors, jumps
// after menus). Unknown words in Autotest's switch come here.
//   fix <chestmash|jumprelease|doors> on|off   the fix switched off/on (one run measures without and with it)
//   chestq                                      chest presses / windows queued and opened per player, the game's queue
//   goldwatch on|off                            every change of P1's / P2's gold reported (who had the game's gold)
//   execs [reset]                               executions per player (hits with isExecute) + Dexecutioner chances seen
//   crypt                                       the map's crypt doors (enter/leave/ghost boss): where, usable, players
//   tocrypt <p> enter|leave|ghost               that player 2.5 m from that door
//   watchy <p> <secs>                           that player's highest point above the start in the next seconds
//   p2pos                                       P2's position, velocity, grounded (P2's movement copy)
internal static partial class Autotest
{
    private static bool BugCommand(string[] a, int pi)
    {
        switch (a[0])
        {
            case "fix":
                {
                    bool on = a.Length < 3 || a[2] != "off";
                    string what = a.Length > 1 ? a[1] : "";
                    if (what == "chestmash") P2Interact.MashFix = on;
                    else if (what == "jumprelease") P2Movement.JumpReleaseFix = on;
                    else if (what == "doors") DoorTeleports.Fix = on;
                    else if (what == "shield") P2PowerUps.ShieldFix = on;
                    else if (what == "timefreeze") P2Health.TimeFreezeFix = on;
                    else if (what == "chestpay") P2Interact.PayFix = on;
                    else if (what == "cactus") P2World.CactusFix = on;
                    else if (what == "ladder") P2World.LadderFix = on;
                    else if (what == "ranged") P2World.RangedFix = on;
                    else if (what == "shrineboth") P2Rewards.BothFix = on;
                    else if (what == "float") P2Abilities.FloatFix = on;
                    else if (what == "spawnside") P2Spawns.PerEnemyFix = on;
                    else if (what == "hold") P2Movement.HoldFix = on;
                    else if (what == "pad") P2World.PadFix = on;
                    else if (what == "lava") P2World.LavaFix = on;
                    else if (what == "controls") P2Select.ControlsFix = on;
                    else { Report($"fix: unknown '{what}'"); return true; }
                    Report($"fix {what} {(on ? "ON" : "OFF")}");
                }
                return true;
            case "runrestart":   // mid-run restart, like the pause menu's / quick-reset's (the game's MapController.RestartRun)
                Cheat(() => { Report("runrestart: the game's MapController.RestartRun()"); Assets.Scripts.Managers.MapController.RestartRun(); });
                return true;
            case "gomain":   // quit to the main menu (the game's TransitionUI.LoadMenu; PauseUi.GoToMain only shows the pause window's main page)
                Cheat(() => { var t = Object.FindObjectOfType<TransitionUI>(); Report($"gomain: TransitionUI {(t != null ? "found" : "missing")}"); t?.LoadMenu(); });
                return true;
            case "needpads": return true;   // (read at the join: P2 waits for its own pad)
            case "handover":   // the run is the user's from here: the script ends, keyboard / mouse on, real pads drive both players
                {
                    var p2Joy = PlayerJoin.P2Joystick;
                    string p2 = p2Joy != null ? $"P2 on pad #{p2Joy.id} '{p2Joy.name}'" : PlayerJoin.VirtualP2 ? "P2 has NO pad (joined without one - no second pad was on)" : "no P2";
                    try
                    {
                        var kb = Rewired.ReInput.controllers?.Keyboard; if (kb != null) kb.enabled = true;
                        var mouse = Rewired.ReInput.controllers?.Mouse; if (mouse != null) mouse.enabled = true;
                    }
                    catch { }
                    Report($"handover: the run is the player's now ({p2})");
                    EndForPlayer();
                }
                return true;
            case "p1jumpheld":   // p1jumpheld on|off: the game reads P1's real jump button as held
                P2Movement.TestP1JumpHeld = a.Length < 2 || a[1] != "off";
                Report($"p1jumpheld {(P2Movement.TestP1JumpHeld ? "ON" : "OFF")}");
                return true;
            case "nospawns":   // nospawns on|off: the game's own summoners spawn nothing (the test's 'spawn' still works)
                P2Spawns.TestNoSummonerSpawns = a.Length < 2 || a[1] != "off";
                Report($"nospawns {(P2Spawns.TestNoSummonerSpawns ? "ON" : "OFF")}");
                return true;
            case "floatcheck": Cheat(() => Report($"floatcheck {(a.Length > 1 ? a[1] : "")}: {FloatCheck()}")); return true;
            case "enemysplit": Cheat(() => { if (a.Length > 1 && a[1] == "reset") P2Spawns.ResetCounts(); Report($"enemysplit {(a.Length > 1 ? a[1] : "")}: {EnemySplit()}"); }); return true;
            case "hp":   // both players' HP
                Cheat(() => Report($"hp {(a.Length > 1 ? a[1] : "")}: P1 {MyPlayer.Instance?.inventory?.playerHealth?.hp}/{MyPlayer.Instance?.inventory?.playerHealth?.maxHp}, P2 {P2Health.Hp}/{P2Health.MaxHp}"));
                return true;
            case "tocactus":   // tocactus <p>: that player against the nearest damaging map object (DamagePlayerCollider), pushed into it
            case "toladder":   // toladder <p>: that player inside the nearest ladder's trigger, at its bottom
                Cheat(() =>
                {
                    bool cactus = a[0] == "tocactus";
                    int who = a.Length > 1 && a[1] == "p2" ? 1 : 0;
                    Vector3 from = PlayerPos(who);
                    Component target = cactus
                        ? Object.FindObjectsOfType<DamagePlayerCollider>().OrderBy(x => (x.transform.position - from).sqrMagnitude).FirstOrDefault()
                        : Object.FindObjectsOfType<Ladder>().OrderBy(x => (x.transform.position - from).sqrMagnitude).FirstOrDefault();
                    int count = cactus ? Object.FindObjectsOfType<DamagePlayerCollider>().Length : Object.FindObjectsOfType<Ladder>().Length;
                    if (target == null) { Report($"{a[0]}: none on this map"); return; }
                    var col = target.GetComponentInChildren<Collider>();
                    var b = col != null ? col.bounds : new Bounds(target.transform.position, Vector3.one);
                    Vector3 at, vel = Vector3.zero;
                    if (cactus)
                    {
                        // beside it on the side facing the player, 0.3 m off its bounds, moving into it
                        Vector3 side = from - b.center; side.y = 0f;
                        if (side.sqrMagnitude < 0.01f) side = Vector3.forward;
                        side.Normalize();
                        float ext = Mathf.Max(b.extents.x, b.extents.z);
                        at = new Vector3(b.center.x, b.min.y + 1.2f, b.center.z) + side * (ext + 0.6f);
                        vel = -side * 6f;
                    }
                    else
                    {
                        // in front of the ladder (its forward or back, 'toladder <p> back'), walking into it
                        Vector3 f = target.transform.forward; f.y = 0f; f = f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
                        if (a.Length > 2 && a[2] == "back") f = -f;
                        at = new Vector3(b.center.x, b.min.y + 1.2f, b.center.z) - f * 0.8f;
                        Vector3 toward = b.center - at; toward.y = 0f;   // (user: the old dir walked away from it)
                        pads[who].dir = toward.sqrMagnitude > 0.0001f ? toward.normalized : f;
                        pads[who].target = null;
                    }
                    var pm = who == 0 ? MyPlayer.Instance.playerMovement : P2Movement.Pm;
                    if (who == 0) MyPlayer.Instance.transform.position = at;
                    pm.transform.position = at;
                    if (pm.rb != null) { pm.rb.position = at; pm.rb.velocity = vel; }
                    Report($"{a[0]}: P{who + 1} at '{target.name}' ({count} on this map) - collider '{col?.name}' {col?.GetIl2CppType().Name} trigger {col?.isTrigger}, bounds centre {b.center} size {b.size}; placed at {at}, velocity {vel}");
                });
                return true;
            case "makeladder":   // makeladder <p> [flip]: a ladder (trigger + the game's Ladder script) 1.5 m ahead of that player along +x, the player walking into it
                Cheat(() =>
                {
                    int who = a.Length > 1 && a[1] == "p2" ? 1 : 0;
                    bool flip = a.Length > 2 && a[2] == "flip";
                    Vector3 at = PlayerPos(who);
                    Vector3 d = Vector3.right;
                    var go = new GameObject("MegabonkSplitscreen_TestLadder");
                    go.transform.position = at + d * 1.5f + Vector3.up * 5f;
                    go.transform.rotation = Quaternion.LookRotation(flip ? d : -d);   // forward toward / away from the player
                    int layer = LayerMask.NameToLayer("Ladder");
                    var real = Object.FindObjectOfType<Ladder>();
                    if (real != null) layer = real.gameObject.layer;
                    if (layer >= 0) go.layer = layer;
                    var box = go.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = new Vector3(2f, 12f, 0.5f);
                    go.AddComponent<Ladder>();
                    // visible for a player's own test: a plank the ladder's size (its own collider removed)
                    var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    var pc = plank.GetComponent<Collider>(); if (pc != null) Object.Destroy(pc);
                    plank.name = "MegabonkSplitscreen_TestLadderPlank";
                    plank.transform.SetParent(go.transform, false);
                    plank.transform.localScale = new Vector3(2f, 12f, 0.2f);
                    pads[who].dir = d;
                    pads[who].target = null;
                    Report($"makeladder: at {go.transform.position}, forward {go.transform.forward}, layer {go.layer} ('{LayerMask.LayerToName(go.layer)}'), P{who + 1} walks {d}");
                });
                return true;
            case "climbstate":
                Cheat(() =>
                {
                    string S(PlayerMovement m) => m == null ? "-" : $"y {m.transform.position.y:F2}, vy {m.rb?.velocity.y:F1}, onLadder {m.onLadder}, grounded {m.grounded}, state {m.lastMovementState}";
                    Report($"climb {(a.Length > 1 ? a[1] : "")}: P1 {S(MyPlayer.Instance?.playerMovement)} | P2 {S(P2Movement.Pm)}");
                });
                return true;
            case "toshrine":   // toshrine <p1|p2|both>: onto the nearest charge shrine (both: P1 at its centre, P2 1.5 m from it)
                Cheat(() =>
                {
                    string whoS = a.Length > 1 ? a[1] : "both";
                    var s = Object.FindObjectsOfType<ChargeShrine>().Where(x => x != null && !x.completed).OrderBy(x => (x.transform.position - PlayerPos(0)).sqrMagnitude).FirstOrDefault();
                    if (s == null) { Report("toshrine: no charge shrine left"); return; }
                    Vector3 c = s.transform.position + Vector3.up * 1.5f;
                    void Put(int who, Vector3 at)
                    {
                        var pm = who == 0 ? MyPlayer.Instance.playerMovement : P2Movement.Pm;
                        if (who == 0) MyPlayer.Instance.transform.position = at;
                        pm.transform.position = at;
                        if (pm.rb != null) { pm.rb.position = at; pm.rb.velocity = Vector3.zero; }
                    }
                    if (whoS != "p2") Put(0, c + Vector3.right * 1.5f);
                    if (whoS != "p1") Put(1, c - Vector3.right * 1.5f);
                    float r = -1f; try { if (s.zoneRenderer != null) r = s.zoneRenderer.bounds.extents.x; } catch { }
                    Report($"toshrine {whoS}: '{s.name}' at {s.transform.position}, charge time {s.chargeTime:F1}, zone radius {r:F1}");
                });
                return true;
            case "shrinestate":
                Cheat(() =>
                {
                    var list = Object.FindObjectsOfType<ChargeShrine>().OrderBy(x => (x.transform.position - PlayerPos(0)).sqrMagnitude).Take(2)
                        .Select(x => $"'{x.name}' {Vector3.Distance(x.transform.position, PlayerPos(0)):F0} m from P1: progress {x.chargeProgress:F2}, charging {x.charging}, completed {x.completed}, reward given {x.rewardGiven}");
                    Report($"shrinestate: {string.Join(" | ", list)}");
                });
                return true;
            case "deathmenu":   // the death screen's menu button (the game's DeathScreen.GoToMenu)
                Cheat(() => { var ds = Object.FindObjectOfType<DeathScreen>(); Report($"deathmenu: DeathScreen {(ds != null ? "found" : "missing")}"); ds?.GoToMenu(); });
                return true;
            case "deadlisteners": Cheat(() => Report($"deadlisteners {(a.Length > 1 ? a[1] : "")}: {DeadListeners()}")); return true;
            case "invcheck": Cheat(() => Report($"invcheck {(a.Length > 1 ? a[1] : "")}: {InvCheck()}")); return true;
            case "eventcheck": Cheat(() => Report($"eventcheck {(a.Length > 1 ? a[1] : "")}: {EventCheck()}")); return true;
            case "srccount":   // srccount reset | <label>: damage per source (the game's Enemy.A_Damage) since the reset
                Cheat(() =>
                {
                    EnsureSrcCounter();
                    if (a.Length > 1 && a[1] == "reset") { srcCounts.Clear(); Report("srccount reset"); return; }
                    Report($"srccount {(a.Length > 1 ? a[1] : "")}: {(srcCounts.Count == 0 ? "nothing" : string.Join(", ", srcCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:F0}")))}");
                });
                return true;
            case "chestq": Cheat(() => Report($"chestq {(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")}: {P2Interact.Counters()}")); return true;
            case "goldwatch":
                goldWatch = a.Length < 2 || a[1] != "off";
                lastGold1 = lastGold2 = float.NaN;
                Report($"gold watch {(goldWatch ? "on" : "off")}");
                return true;
            case "execs": Cheat(() => Execs(a.Length > 1 && a[1] == "reset", string.Join(" ", a.Skip(1)))); return true;
            case "dexchance":   // dexchance <0..1> | off: every Dexecutioner swing's execution chance forced (test only: 2% is too rare to compare)
                forcedChance = a.Length > 1 && a[1] != "off" ? Float(a, 1, 1f) : -1f;
                Report($"Dexecutioner execution chance {(forcedChance >= 0f ? $"forced to {forcedChance:F2}" : "the game's")}");
                return true;
            case "crypt": Cheat(DescribeCrypts); return true;
            case "tocrypt": Cheat(() => ToCrypt(pi, a.Length > 2 ? a[2] : "leave")); return true;
            case "watchy":
                {
                    float secs = Float(a, 2, 3f);
                    var p = PlayerPos(pi);
                    watchY[pi] = (Time.unscaledTime + secs, p.y, p.y);
                    Report($"watching P{pi + 1}'s height for {secs:F1} s (start y {p.y:F2})");
                }
                return true;
            case "freeze":   // freeze [label]: the game's time freeze state and both players' HP
                Cheat(() =>
                {
                    bool f = GameManager.Instance != null && GameManager.Instance.IsTimeFreeze();
                    var ph = MyPlayer.Instance?.inventory?.playerHealth;
                    Report($"freeze {string.Join(" ", a.Skip(1))}: time frozen {f}; P1 {ph?.hp}/{ph?.maxHp}, P2 {P2Health.Hp}/{P2Health.MaxHp}");
                });
                return true;
            case "fog":   // fog [label]: how much of the map's fog is revealed (the game's FullMap)
                Cheat(() =>
                {
                    var m = Object.FindObjectOfType<FullMap>();
                    int vis = 0;
                    if (m?.visitedCords != null) foreach (var b in m.visitedCords) if (b) vis++;
                    Report($"fog {string.Join(" ", a.Skip(1))}: revealed cells {m?.revealedIndices?.Count ?? -1}, visited {vis}, reveal radius {m?.revealRadius:F0}; P1 at {PlayerPos(0)}, P2 at {PlayerPos(1)}");
                });
                return true;
            case "p2pos":
                Cheat(() =>
                {
                    var pm = P2Movement.Pm;
                    Report(pm == null ? "p2pos: no P2 movement" : $"p2pos: P2 at {pm.transform.position}, velocity {pm.rb?.velocity}, grounded {pm.grounded}, kinematic {pm.rb?.isKinematic}; P1 at {PlayerPos(0)}");
                });
                return true;
        }
        return false;
    }

    // ---------- Per frame (Autotest.Update) ----------
    private static bool goldWatch;
    private static float lastGold1 = float.NaN, lastGold2 = float.NaN;
    private static int goldLogs;
    private static readonly (float until, float startY, float maxY)[] watchY = new (float, float, float)[2];

    private static void BugWatchers()
    {
        if (phase != "script") return;
        if (goldWatch)
        {
            var inv = MyPlayer.Instance?.inventory;
            if (inv != null)
            {
                float g1 = inv._gold_k__BackingField, g2 = P2Gold.Amount;
                if (!float.IsNaN(lastGold1) && (g1 != lastGold1 || g2 != lastGold2) && goldLogs++ < 80)
                    Report($"[gold] game gold {lastGold1:F0} -> {g1:F0}, P2's purse {lastGold2:F0} -> {g2:F0} (P2's gold swapped in: {P2Gold.SwappedIn}; " +
                           $"P2 chest window {P2Interact.ChestIsP2s}, P2 screen {P2LevelUp.P2ScreenOpen}, reward {P2Rewards.EncounterIsP2}, stage {Assets.Scripts.Managers.MapController.currentStage?.name})");
                lastGold1 = g1;
                lastGold2 = g2;
            }
        }
        for (int i = 0; i < 2; i++)
        {
            var w = watchY[i];
            if (w.until <= 0f) continue;
            float y = PlayerPos(i).y;
            if (y > w.maxY) w.maxY = y;
            if (Time.unscaledTime >= w.until)
            {
                Report($"watchy P{i + 1}: highest {w.maxY - w.startY:F2} m above the start (start y {w.startY:F2}, top y {w.maxY:F2}, now {y:F2})");
                w.until = 0f;
            }
            watchY[i] = w;
        }
        if (execOn || forcedChance >= 0f) SampleDexecutioners();
    }

    // ---------- Executions ----------
    private static bool execOn;
    private static Il2CppSystem.Action<Assets.Scripts.Actors.Enemies.Enemy, Assets.Scripts.Actors.DamageContainer> execListener;
    private static readonly int[] hits = new int[2], executes = new int[2], executeEffects = new int[2];
    private static readonly float[] maxChance = { -1f, -1f };
    private static readonly int[] dexSeen = new int[2];
    private static float nextDexSample;

    private static void Execs(bool reset, string label)
    {
        if (execListener == null)
        {
            execListener = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Assets.Scripts.Actors.Enemies.Enemy, Assets.Scripts.Actors.DamageContainer>>(
                new Action<Assets.Scripts.Actors.Enemies.Enemy, Assets.Scripts.Actors.DamageContainer>(OnEnemyDamage));
            var ev = Assets.Scripts.Actors.Enemies.Enemy.A_Damage;
            Assets.Scripts.Actors.Enemies.Enemy.A_Damage = ev == null ? execListener
                : Il2CppSystem.Delegate.Combine(ev, execListener).Cast<Il2CppSystem.Action<Assets.Scripts.Actors.Enemies.Enemy, Assets.Scripts.Actors.DamageContainer>>();
            execOn = true;
        }
        if (reset)
        {
            Array.Clear(hits); Array.Clear(executes); Array.Clear(executeEffects); Array.Clear(dexSeen);
            maxChance[0] = maxChance[1] = -1f;
            Report("execs: counting from now");
            return;
        }
        Report($"execs {label}: P1 hits {hits[0]}, executions {executes[0]} (execute effect {executeEffects[0]}), Dexecutioner chance {maxChance[0]:F3} ({dexSeen[0]} seen); " +
               $"P2 hits {hits[1]}, executions {executes[1]} (execute effect {executeEffects[1]}), Dexecutioner chance {maxChance[1]:F3} ({dexSeen[1]} seen)");
    }

    private static void OnEnemyDamage(Assets.Scripts.Actors.Enemies.Enemy e, Assets.Scripts.Actors.DamageContainer dc)
    {
        try
        {
            if (e == null || dc == null) return;
            int who = P2Items.HitOwnerOf(e) == 2 ? 1 : 0;
            hits[who]++;
            if (dc.isExecute) { executes[who]++; if (pendingFx.Count < 40) pendingFx.Add((who, e.transform.position, Time.unscaledTime)); }
            if (dc.damageEffect == Assets.Scripts.Game.Combat.EDamageEffect.Execute) executeEffects[who]++;
        }
        catch { }
    }

    // After each execution: where the game's execute effect (PoolManager.executeFx clones) showed up - next to the
    // executed enemy, or somewhere else (user: maybe P1 gets a visual effect that P2 doesn't)
    private static readonly List<(int who, Vector3 at, float t)> pendingFx = new();
    private static string execFxName;
    private static int fxLogs;
    private static void CheckExecuteFx()
    {
        if (pendingFx.Count == 0 || Time.unscaledTime - pendingFx[0].t < 0.15f) return;
        var (who, at, _) = pendingFx[0];
        pendingFx.RemoveAt(0);
        if (execFxName == null) execFxName = Object.FindObjectOfType<Assets.Scripts.Objects.Pooling.PoolManager>()?.executeFx?.name ?? "";
        if (execFxName == "" || fxLogs++ >= 16) return;
        var fx = Object.FindObjectsOfType<Transform>().Where(t => t != null && t.gameObject.activeInHierarchy && t.name.StartsWith(execFxName))
            .Select(t => t.position).ToList();
        string near = fx.Count == 0 ? "none active" : string.Join(", ", fx.OrderBy(p => (p - at).sqrMagnitude).Take(3).Select(p =>
            $"{Vector3.Distance(p, at):F1} m from the enemy (P1 {Vector3.Distance(p, PlayerPos(0)):F0} m, P2 {Vector3.Distance(p, PlayerPos(1)):F0} m)"));
        Report($"[execfx] P{who + 1}'s execution at {at}: '{execFxName}' effects {fx.Count} - nearest: {near}");
        if (execShots[who]++ < 2) Shot($"execfx_p{who + 1}_{execShots[who]}");   // the moment itself, as the player sees it
    }
    private static readonly int[] execShots = new int[2];

    private static void SampleDexecutioners()
    {
        CheckExecuteFx();
        if (Time.unscaledTime < nextDexSample) return;
        nextDexSample = forcedChance >= 0f ? 0f : Time.unscaledTime + 0.2f;   // forced: every frame (before its hits)
        foreach (var d in Object.FindObjectsOfType<ProjectileDexecutioner>())
        {
            if (d == null) continue;
            int who = P2Weapons.IsP2(d.weaponBase) ? 1 : 0;
            dexSeen[who]++;
            if (d.executionChance > maxChance[who]) maxChance[who] = d.executionChance;
            if (forcedChance >= 0f) d.executionChance = forcedChance;
        }
    }
    private static float forcedChance = -1f;

    // ---------- Crypt doors (Graveyard) ----------
    private static List<BaseInteractable> Doors(string kind)
    {
        IEnumerable<BaseInteractable> all = kind switch
        {
            "enter" => Resources.FindObjectsOfTypeAll<InteractableCrypt>().Select(x => (BaseInteractable)x),
            "ghost" => Resources.FindObjectsOfTypeAll<InteractableGhostBossLeave>().Select(x => (BaseInteractable)x),
            _ => Resources.FindObjectsOfTypeAll<InteractableCryptLeave>().Select(x => (BaseInteractable)x),
        };
        return all.Where(x => x != null && x.gameObject.scene.IsValid()).ToList();
    }

    private static void DescribeCrypts()
    {
        string Can(BaseInteractable b) { try { return b.CanInteract().ToString(); } catch (Exception e) { return "? " + e.GetType().Name; } }
        var parts = new List<string>();
        foreach (var kind in new[] { "enter", "leave", "ghost" })
            foreach (var d in Doors(kind))
            {
                string extra = "";
                var c = d.TryCast<InteractableCrypt>();
                if (c != null) { try { extra = $", done {c.isDone}, open {c.IsOpen()}"; } catch { } }
                var l = d.TryCast<InteractableCryptLeave>();
                if (l != null) extra = $", used {l.hasInteracted}, sends to {l.teleportPosition}";
                var g = d.TryCast<InteractableGhostBossLeave>();
                if (g != null) extra = $", used {g.hasInteracted}, boss dead {g.isBossDead}, sends to {g.teleportPosition}";
                parts.Add($"{kind} '{d.name}' at {d.transform.position} active {d.gameObject.activeInHierarchy}, can {Can(d)}{extra} (P1 {Vector3.Distance(d.transform.position, PlayerPos(0)):F0} m, P2 {Vector3.Distance(d.transform.position, PlayerPos(1)):F0} m)");
            }
        string Grave(int i) { var p = Gravestones.Position(i); return p == null ? "-" : p.Value.ToString(); }
        Report($"crypt: stage {Assets.Scripts.Managers.MapController.currentStage?.name}; P1 at {PlayerPos(0)} (down {Downed.P1Down}, grave {Grave(0)}), P2 at {PlayerPos(1)} (down {P2Health.IsDead}, grave {Grave(1)}); doors ({parts.Count}): {string.Join(" | ", parts)}");
    }

    private static void ToCrypt(int pi, string kind)
    {
        var at0 = PlayerPos(pi);
        var d = Doors(kind).Where(x => x.gameObject.activeInHierarchy).OrderBy(x => (x.transform.position - at0).sqrMagnitude).FirstOrDefault()
                ?? Doors(kind).OrderBy(x => (x.transform.position - at0).sqrMagnitude).FirstOrDefault();
        if (d == null) { Report($"tocrypt: no '{kind}' door on the map"); return; }
        var pm = pi == 0 ? MyPlayer.Instance?.playerMovement : P2Movement.Pm;
        // On the player's side of the door (bug_graveyard: the door's forward pointed out of the crypt - both players fell
        // 2 km), on a floor found within 4 m below; 8 directions tried from the player's side round
        Vector3 toward = at0 - d.transform.position;
        toward.y = 0f;
        toward = toward.sqrMagnitude > 0.01f ? toward.normalized : -d.transform.forward;
        // (a second run put P1 on top of something and P1 fell out: only a floor near the door's own height counts)
        float dy = d.transform.position.y;
        Vector3 to = d.transform.position + toward * 1.5f + Vector3.up * 0.5f;
        for (int k = 0; k < 8; k++)
        {
            Vector3 c = d.transform.position + Quaternion.Euler(0f, (k % 2 == 0 ? 1 : -1) * (k + 1) / 2 * 45f, 0f) * toward * 2.5f;
            if (Physics.Raycast(c + Vector3.up * 2f, Vector3.down, out RaycastHit h, 6f, ~0, QueryTriggerInteraction.Ignore) && h.point.y > dy - 3f && h.point.y < dy + 1.5f) { to = h.point; break; }
        }
        Teleport(pm, to);
        Report($"P{pi + 1} put 2.5 m from the '{kind}' door '{d.name}' at {d.transform.position} (now {PlayerPos(pi)})");
    }
}
