using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Autotest mode (user: "launch the exe, then hijack controls for both players so you can test stuff").
// Off unless the file 'autotest_run.txt' is in the mod's folder when the game starts; it is renamed to
// 'autotest_last.txt' at once, so the next normal start is a normal game. The script drives the whole thing:
//  - menus: main menu -> character select (P2 joins with the first pad that isn't P1's, characters picked by name)
//    -> confirm -> map select -> the game's own StartMap; then it waits until P2 has control
//  - input: P1's PlayerInput values (move, jump) are written after the game's own input read each frame; P2's pad
//    reads (P2Control.ReadStick / PadHeld) answer from the script. Directions are world directions, turned into stick
//    values from each player's own camera/orientation every frame.
//  - evidence: 'snap' logs both players' state, 'shot' saves a screenshot; everything goes to the mod log as [test]
//    and to 'autotest_result.txt'; the game quits at the end (or after the time limit).
// Commands (one per line, # comments):
//   chars <P1 character> <P2 character>   (ECharacter names, e.g. Monke Calcium) - must come first
//   wait <seconds>
//   go <p1|p2> <x> <z> | go <p> wall | go <p> other | stop <p>
//   jump <p>            hold <p> <seconds>   (jump held)
//   place p2 near p1    (P2 moved next to P1)
//   snap [label]        shot <name>         log <text>        quit
internal static partial class Autotest
{
    public static bool Active { get; private set; }
    // v0.42.4 (weapon sweep: the first kills levelled both players up and the level-up screens paused the test): no
    // XP during an autotest unless the script says 'xp on'
    private static bool xpOn;
    public static bool NoXp => Active && !xpOn;
    private const float TimeLimit = 600f;   // the weapon sweeps run ~4 minutes

    private static readonly List<string> lines = new();
    private static int line;
    private static string dir;
    private static string phase = "menu";
    private static float phaseSince, waitUntil = -1f, startedAt, nextPadLog;
    private static string p1Char = "", p2Char = "";
    private static readonly System.Text.StringBuilder results = new();

    private class Pad
    {
        public Vector3 dir;          // world direction (y ignored), zero = none
        public string target;        // "wall" / "other" / null
        public float jumpUntil = -1f;
    }
    private static readonly Pad[] pads = { new Pad(), new Pad() };

    public static void Init(string pluginDir)
    {
        dir = pluginDir;
        string path = Path.Combine(pluginDir, "autotest_run.txt");
        if (!File.Exists(path)) return;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                string l = raw.Split('#')[0].Trim();
                if (l.Length > 0) lines.Add(l);
            }
            string last = Path.Combine(pluginDir, "autotest_last.txt");
            if (File.Exists(last)) File.Delete(last);
            File.Move(path, last);
            Active = true;
            startedAt = Time.realtimeSinceStartup;
            Application.runInBackground = true;   // keeps running while another window has focus
            Report($"AUTOTEST started: {lines.Count} script lines");
        }
        catch (Exception e) { ModLog.Error($"[test] autotest script failed to load: {e.Message}"); }
    }

    private static void Report(string s)
    {
        ModLog.Info($"[test] {s}");
        results.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
        try { File.WriteAllText(Path.Combine(dir, "autotest_result.txt"), results.ToString()); } catch { }
    }

    private static void SetPhase(string p)
    {
        phase = p;
        phaseSince = Time.unscaledTime;
        Report($"phase: {p}");
    }

    // ---------- Every frame (Ticker.Update) ----------
    public static void Update()
    {
        if (!Active) return;
        // v0.43.5 (an autotest run left character select by itself and opened the quit dialog: keystrokes - the user
        // typing in another window that the game had focus over - reached the game): the game's keyboard and mouse
        // input is off during an autotest (menus and both players are driven by the script)
        try
        {
            var kb = Rewired.ReInput.controllers?.Keyboard;
            if (kb != null && kb.enabled) { kb.enabled = false; Report("keyboard input off for the test"); }
            var mouse = Rewired.ReInput.controllers?.Mouse;
            if (mouse != null && mouse.enabled) mouse.enabled = false;
        }
        catch { }
        if (Time.realtimeSinceStartup - startedAt > TimeLimit) { Report($"TIME LIMIT ({TimeLimit:F0} s) reached"); Quit(); return; }
        try
        {
            switch (phase)
            {
                case "menu": Menu(); break;
                case "characters": Characters(); break;
                case "confirm": Confirm(); break;
                case "map": MapSelect(); break;
                case "spawn": WaitForPlayers(); break;
                case "script": RunScript(); break;
            }
        }
        catch (Exception e) { Report($"ERROR in phase {phase}: {e.Message}"); }
        try { TrackP1(); } catch { }
        try { BugWatchers(); } catch { }
    }

    // 'trackp1': P1's position and velocity twice a second, and every one-frame jump over 8 m
    private static float trackUntil = -1f, trackNext;
    private static Vector3? trackLast;
    private static void TrackP1()
    {
        if (Time.unscaledTime > trackUntil) return;
        var p1 = MyPlayer.Instance;
        var rb = p1?.playerMovement?.rb;
        if (p1 == null || rb == null) return;
        Vector3 pos = rb.position;
        if (trackLast.HasValue && (pos - trackLast.Value).magnitude > 8f)
            Report($"[track] P1 JUMPED {(pos - trackLast.Value).magnitude:F1} m in one frame: {trackLast.Value} -> {pos}, velocity {rb.velocity}");
        trackLast = pos;
        if (Time.unscaledTime < trackNext) return;
        trackNext = Time.unscaledTime + 0.5f;
        Report($"[track] P1 at {pos}, velocity {rb.velocity}, grounded {p1.playerMovement.grounded}; P2 at {(P2Movement.Active ? P2Movement.BodyPosition().ToString() : "-")}");
    }

    private static void Menu()
    {
        if (p1Char == "")
        {
            // 'chars' must be the first line
            var parts = lines.Count > 0 ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            // 'menuscript' (v0.49.6, user: character select navigation for P1 and P2 - who confirms first, someone
            // couldn't get to map select): the script itself drives the menus from the main menu on, no automatic steps
            if (parts.Length >= 1 && parts[0] == "menuscript")
            {
                if (Object.FindObjectOfType<MainMenu>() == null) return;
                if (phaseSince <= 0f) { phaseSince = Time.unscaledTime; return; }
                if (Time.unscaledTime - phaseSince < 2f) return;   // the main menu settles
                p1Char = "menuscript";
                line = 1;
                SetPhase("script");
                return;
            }
            if (parts.Length < 3 || parts[0] != "chars") { Report("FAIL: the first line must be 'chars <P1> <P2>' (or 'menuscript')"); Quit(); return; }
            p1Char = parts[1];
            p2Char = parts[2];
            // optional "tier <n>" / "map <EMap>" after the characters
            tierWanted = 0; mapWanted = "";
            for (int k = 3; k + 1 < parts.Length; k += 2)
            {
                if (parts[k] == "tier" && int.TryParse(parts[k + 1], out int tw)) tierWanted = tw;
                else if (parts[k] == "map") mapWanted = parts[k + 1];
            }
            line = 1;
            SetPhase("menu");
        }
        var mm = Object.FindObjectOfType<MainMenu>();
        // v0.46.5 (user: character and map selection in the tests can be faster): shorter settles throughout
        if (mm == null || Time.unscaledTime - phaseSince < 1f) return;   // the menu settles first
        mm.GoToCharacterSelection();
        SetPhase("characters");
    }

    private static void Characters()
    {
        if (!PlayerJoin.InCharacterSelect || Time.unscaledTime - phaseSince < 0.5f) return;
        var menu = Object.FindObjectOfType<CharacterMenu>();
        if (menu == null) return;
        if (!PlayerJoin.HasP2 && !lobbyHold)
        {
            // v0.40.1 (first run: only the Steam Controller was known at this point): pads can appear late
            if (!PlayerJoin.JoinFirstFreePad())
            {
                if (Time.unscaledTime >= nextPadLog)
                {
                    nextPadLog = Time.unscaledTime + 3f;
                    var names = new List<string>();
                    foreach (var j in Rewired.ReInput.controllers.GetJoysticks()) if (j != null) names.Add($"#{j.id} '{j.name}'");
                    Report($"waiting for a second pad - known: {string.Join(", ", names)}");
                }
                // v0.42.3 (a wireless pad switched itself off mid-session): P2's input comes from the script anyway, so
                // after a short wait the one pad there is stands in for P2 (menus and P1 are driven by code)
                if (Time.unscaledTime - phaseSince > 1f && PlayerJoin.JoinFirstFreePad(anyPad: true)) { Report("no second pad: P1's pad stands in for P2 (script input only)"); return; }
                // v0.43.7 (user: the pads go idle while away): no pad at all - P2 joins without one
                if (Time.unscaledTime - phaseSince > 2f && PlayerJoin.JoinVirtual()) { Report("no pad at all: P2 joins without one (script input only)"); return; }
                if (Time.unscaledTime - phaseSince > 22f) { Report("FAIL: P2 couldn't join"); Quit(); }
            }
            return;   // the join sets up P2's character select next frame
        }
        // The lobby (v0.47.0): screenshot it with both players in, then confirm it like P1 would
        if (Lobby.Active)
        {
            if (lobbyShotAt < 0f) { lobbyShotAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - lobbyShotAt < 1.5f) return;   // the camera flies over to fit P2's model
            // 'lobbytest' in the script: P2 leaves (what B does) and joins again, with screenshots
            bool full = lines.Any(l => l.Trim() == "lobbytest");
            // (lobbyHold keeps the test's own join from bringing P2 straight back before the 'P2 left' screenshot)
            switch (full ? lobbyStep : lobbyStep + 4)
            {
                case 0: Shot("lobby_both"); Report("lobby: both in"); break;
                case 1: lobbyHold = true; PlayerJoin.Leave(); Report($"lobby: P2 left (what B does) - P2 in: {PlayerJoin.HasP2}"); break;
                case 2: Shot("lobby_p2_left"); Report("lobby: screenshot with P2 out"); break;
                case 3: lobbyHold = false; Report("lobby: P2 joins again"); break;
                case 4: Shot("lobby"); Report($"lobby: P2 in: {PlayerJoin.HasP2}"); break;
                default: Lobby.Confirm(); Report("lobby confirmed"); break;
            }
            lobbyStep++;
            lobbyShotAt = Time.unscaledTime - 0.5f;   // a second for each step (screenshots land at the frame's end)
            return;
        }
        if (!Enum.TryParse(p1Char, true, out ECharacter c1) || !Enum.TryParse(p2Char, true, out ECharacter c2))
        { Report($"FAIL: unknown character '{p1Char}' or '{p2Char}'"); Quit(); return; }
        MyButtonCharacter b1 = null;
        foreach (var b in menu.characterButtons) if (b != null && b.characterData != null && b.characterData.eCharacter == c1) b1 = b;
        if (b1 == null) { Report($"FAIL: P1 character {c1} not in the menu"); Quit(); return; }
        // v0.44.6 (user: test the locked characters too; their progress gets wiped anyway): a locked character's
        // button is switched to usable in memory for this test run only - nothing is unlocked in the save
        foreach (var b in menu.characterButtons)
            if (b != null && b.characterData != null && (b.characterData.eCharacter == c1 || b.characterData.eCharacter == c2) && !b.canUseCharacter)
            {
                Report($"{b.characterData.eCharacter} is locked ({b.cantUseCharacterReason}) - usable for this test only");
                b.canUseCharacter = true;
                forced.Add(b);
            }
        forcedP1 = c1;
        menu.OnSelectCharacter(b1);
        if (!P2Select.PickCharacter(c2)) { if (Time.unscaledTime - phaseSince > 8f) { Report($"FAIL: P2 couldn't pick {c2}"); Quit(); } return; }
        Report($"characters: P1 {c1}, P2 {c2}");
        menu.OnConfirmCharacter(b1);
        confirmMenu = menu;
        SetPhase("confirm");
    }

    // v0.40.2 (log: OnConfirmCharacter only moved the highlight to 'B_Confirm'): the Confirm button itself is pressed;
    // if character select is still open after a few seconds, the main menu's own GoToMapSelection
    private static CharacterMenu confirmMenu;
    private static bool clickedConfirm, usedFallback;
    // v0.44.7 (user: in the locked run P1 was the knight, P2 the locked character): the buttons recompute their
    // lock each frame, so the override is held through the confirm step and P1's pick is set in the game's own
    // selection (CharacterMenu.selectedCharacter) too
    private static readonly List<MyButtonCharacter> forced = new();
    private static ECharacter? forcedP1;
    private static void HoldForcedPick()
    {
        foreach (var b in forced) if (b != null) b.canUseCharacter = true;
        if (forced.Count > 0 && forcedP1 is ECharacter c && CharacterMenu.selectedCharacter != c)
        {
            Report($"P1's selection was {CharacterMenu.selectedCharacter} - set back to {c}");
            CharacterMenu.selectedCharacter = c;
        }
    }
    private static void Confirm()
    {
        HoldForcedPick();
        if (Time.unscaledTime - phaseSince < 0.2f) return;
        if (!PlayerJoin.InCharacterSelect) { SetPhase("map"); return; }
        if (!clickedConfirm)
        {
            clickedConfirm = true;
            try { confirmMenu?.b_confirm?.OnClick(); Report("pressed Confirm"); }
            catch (Exception e) { Report($"Confirm press failed: {e.Message}"); }
            return;
        }
        if (!usedFallback && Time.unscaledTime - phaseSince > 0.6f)   // (every run's log: the Confirm press alone never closed it)
        {
            usedFallback = true;
            Report("character select still open - MainMenu.GoToMapSelection");
            Object.FindObjectOfType<MainMenu>()?.GoToMapSelection();
        }
        if (Time.unscaledTime - phaseSince > 12f) { Report("FAIL: character select didn't close"); Quit(); }
    }

    // Where the run is: map, stage, tier, and both players' state (user: test tier 2 and what happens after stage 1's portal)
    private static string StageState()
    {
        string S(Func<object> f) { try { return f()?.ToString() ?? "null"; } catch (Exception e) { return $"? ({e.GetType().Name})"; } }
        var p1 = MyPlayer.Instance;
        var ph = p1?.inventory?.playerHealth;
        return $"stage: map {S(() => Assets.Scripts.Managers.MapController.currentMap?.name)}, stage {S(() => Assets.Scripts.Managers.MapController.currentStage?.name)} " +
               $"(index {S(() => Assets.Scripts.Managers.MapController.GetStageIndex())}, last {S(() => Assets.Scripts.Managers.MapController.IsLastStage())}, " +
               $"tier final {S(() => Assets.Scripts.Managers.MapController.IsTierFinalStage())}, final boss stage {S(() => Assets.Scripts.Managers.MapController.IsFinalBossStage())}), altar {S(AltarState)}, " +
               $"tier {S(() => Assets.Scripts.Managers.MapController.runConfig?.mapTierIndex + 1)}, paused {S(() => Assets.Scripts.Utility.MyTime.paused)}, P2 level-ups waiting {P2LevelUp.Pending}; " +
               $"P1 {S(() => p1?.inventory?.characterData?.eCharacter)} at {S(() => p1?.transform.position)} hp {S(() => ph?.hp)}/{S(() => ph?.maxHp)} level {S(() => p1?.inventory?.playerXp?.level)}; " +
               $"P2 {S(() => P2Inventory.P2Character?.eCharacter)} at {S(() => P2Movement.Pm?.transform.position)} hp {P2Health.Hp:F0}/{P2Health.MaxHp:F0} xp {P2XpBar.Xp}, " +
               $"movement {P2Movement.Active}, control {P2Control.HasControl}, entering {P2Body.Entering}, camera {P2Camera.Cam != null}, " +
               $"P2 level {S(() => Inventory__Items__Pickups.XpUtility.XpToLevel(P2XpBar.Xp))}, " +
               $"weapons P1 [{S(() => Weapons(p1?.inventory?.weaponInventory))}] P2 [{S(() => Weapons(P2Inventory.Weapons))}], " +
               $"gold P1 {S(() => p1?.inventory?.gold)} P2 {P2Gold.Amount:F0}, items P1 [{S(P2Items.DescribeP1)}] P2 [{S(() => P2Items.Describe(P2Inventory.Items))}]";
    }

    // The stage's boss altar(s) and exit portal(s), normal and final kinds
    private static string AltarState()
    {
        var l = new List<string>();
        foreach (var b in Object.FindObjectsOfType<InteractableBossSpawner>()) l.Add($"BossSpawner '{b.name}'");
        foreach (var b in Object.FindObjectsOfType<InteractableBossSpawnerFinal>()) l.Add($"BossSpawnerFinal '{b.name}' done {b.done}");
        foreach (var b in Object.FindObjectsOfType<InteractablePortal>()) l.Add($"Portal '{b.name}' done {b.done}");
        foreach (var b in Object.FindObjectsOfType<InteractablePortalFinal>()) l.Add($"PortalFinal '{b.name}' done {b.done}");
        return l.Count == 0 ? "none" : string.Join(", ", l);
    }

    private static string Weapons(Assets.Scripts.Inventory__Items__Pickups.Weapons.WeaponInventory wi)
    {
        if (wi?.weapons == null) return "none";
        var l = new List<string>();
        foreach (var kv in wi.weapons) if (kv.Value != null) l.Add($"{kv.Key} L{kv.Value.level}");
        return string.Join(", ", l);
    }

    private static int tierWanted;   // 'chars <P1> <P2> tier <n> map <EMap>'
    private static string mapWanted = "";

    private static void MapSelect()
    {
        HoldForcedPick();
        if (Time.unscaledTime - phaseSince < 0.5f) return;
        var ui = Object.FindObjectOfType<MapSelectionUi>();
        if (ui == null || !ui.gameObject.activeInHierarchy) { if (Time.unscaledTime - phaseSince > 15f) { Report("FAIL: map select never opened"); Quit(); } return; }
        if (mapWanted != "" && ui.runConfig != null)
        {
            // The run's map only, from the game's map list (no click: that would save it as the user's last map)
            MapData md = null;
            var maps = DataManager.Instance?.maps;
            if (maps != null) for (int k = 0; k < maps.Count; k++) if (maps[k] != null && maps[k].eMap.ToString().Equals(mapWanted, StringComparison.OrdinalIgnoreCase)) md = maps[k];
            if (md == null || md.stages == null || md.stages.Length == 0) { Report($"FAIL: map '{mapWanted}' not found"); Quit(); return; }
            ui.runConfig.mapData = md;
            ui.runConfig.stageData = md.stages[0];
            Report($"map {md.eMap}: {md.stages.Length} stages [{string.Join(", ", md.stages.Select(s => s != null ? s.name : "null"))}]");
        }
        if (tierWanted > 0 && ui.runConfig != null)
        {
            // The run's tier only (no click on the tier button: that would save it as the user's last tier)
            var b = ui.tierSelectionGroup?.GetButton(tierWanted - 1);
            ui.runConfig.mapTierIndex = tierWanted - 1;
            Report($"tier {tierWanted}: tier button {(b != null ? $"'{b.name}' selectable {b.canSelect}" : "not found")}; run config tier index {ui.runConfig.mapTierIndex}");
        }
        Report($"map: starting {(ui.runConfig?.mapData != null ? ui.runConfig.mapData.name : "the selected map")}");
        ui.StartMap();
        SetPhase("spawn");
    }

    private static void WaitForPlayers()
    {
        if (MyPlayer.Instance == null || !P2Movement.Active || !P2Control.HasControl) { if (Time.unscaledTime - phaseSince > 90f) { Report("FAIL: the run didn't start with P2 in control"); Quit(); } return; }
        if (Time.unscaledTime - phaseSince < 1f) return;
        // The characters the run really has (v0.44.7: a forced pick fell back to the knight for P1 unnoticed)
        string got1 = MyPlayer.Instance?.inventory?.characterData?.eCharacter.ToString() ?? "?";
        string got2 = P2Inventory.P2Character?.eCharacter.ToString() ?? "?";
        Report($"run characters: P1 {got1}, P2 {got2}");
        if (!got1.Equals(p1Char, StringComparison.OrdinalIgnoreCase) || !got2.Equals(p2Char, StringComparison.OrdinalIgnoreCase))
        { Report($"FAIL: wanted P1 {p1Char}, P2 {p2Char}"); Quit(); return; }
        Report("both players in control: running the script");
        Snap("start");
        SetPhase("script");
    }

    // TEMP [watch] (character sweep: Vlad x2 ended with P1 MaxHealth 101 / P2 100 and DamageReductionMultiplier 0 / 1):
    // every stat change of either player while a script runs, with the sizes of both stat-change holders
    private static readonly Dictionary<Assets.Scripts.Menu.Shop.EStat, (float, float)> watched = new();
    private static float nextWatch;
    private static int watchLogs;
    private static float[] lastHealth;
    private static void WatchStats()
    {
        if (watchLogs >= 150 || Time.unscaledTime < nextWatch) return;
        nextWatch = Time.unscaledTime + 0.25f;
        var changes = new List<string>();
        foreach (Assets.Scripts.Menu.Shop.EStat s in Enum.GetValues(typeof(Assets.Scripts.Menu.Shop.EStat)))
        {
            var now = StatPair(s);
            if (watched.TryGetValue(s, out var was))
            {
                if (Mathf.Abs(was.Item1 - now.Item1) > 0.0005f) changes.Add($"P1 {s} {was.Item1:0.###}->{now.Item1:0.###}");
                if (Mathf.Abs(was.Item2 - now.Item2) > 0.0005f) changes.Add($"P2 {s} {was.Item2:0.###}->{now.Item2:0.###}");
            }
            watched[s] = now;
        }
        // Health numbers too (user: P1's shield went to 0 and P1 got "+60 hp" effects with no reason)
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        var hv = new[] { ph?.hp ?? 0f, ph?.maxHp ?? 0f, ph?.shield ?? 0f, ph?.maxShield ?? 0f, P2Health.Hp, P2Health.MaxHp, P2Health.Shield, P2Health.MaxShield };
        string[] hn = { "P1 hp", "P1 maxHp", "P1 shield", "P1 maxShield", "P2 hp", "P2 maxHp", "P2 shield", "P2 maxShield" };
        if (lastHealth != null)
            for (int i = 0; i < hv.Length; i++)
                if (Mathf.Abs(hv[i] - lastHealth[i]) > 0.5f) changes.Add($"{hn[i]} {lastHealth[i]:0}->{hv[i]:0}");
        lastHealth = hv;
        if (changes.Count == 0) return;
        watchLogs++;
        string Holder(Assets.Scripts.Inventory__Items__Pickups.Stats.StatInventory si)
        {
            if (si == null) return "none";
            var parts = new List<string>();
            if (si.permanentChanges != null)
                foreach (var kv in si.permanentChanges)
                {
                    float sum = 0f;
                    int n = 0;
                    if (kv.Value != null) foreach (var m in kv.Value) { if (m != null) sum += m.modification; n++; }
                    parts.Add($"{kv.Key} x{n} = {sum:0.###}");
                }
            return $"[{si.Pointer:X}] permanent {{{string.Join(", ", parts)}}}, temporary {si.temporaryChanges?.Count}";
        }
        ModLog.Info($"[watch] {Time.time:F2}: {string.Join(", ", changes)} | P1 holder {Holder(MyPlayer.Instance?.inventory?.statInventory)}, P2 holder {Holder(P2Inventory.StatsInv)}");
    }

    // User (a chest screen stopped the revive test twice): chest windows are closed by the test until a test wants
    // them ('keepwindows') - the window's own Leave button, else Discard
    private static bool keepWindows, noPowerUps;
    private static Vector3? homeA, homeB;
    private static float selWaitStart = -1f;
    private static float lobbyShotAt = -1f;
    private static int lobbyStep;
    private static bool lobbyHold;
    private static float reviveWaitStart = -1f;
    private static float nextWindowCheck;
    private static int windowLogs;
    // 'autopick' (user: a long perf run where no reward screen blocks the test): level-up screens take their first offer
    // and chests are opened and their item taken, like a player would, instead of being discarded
    private static bool autoPick;
    private static float levelupSeenAt = -1f;
    private static int autoPicks;
    private static void AutoPickLevelUp()
    {
        // Only while a level-up is really showing (pot_p2 test: the screen object stays active when hidden - the first
        // version kept "picking" offers on it, free upgrades for P1)
        var ls = LevelupScreen.isLevelingUp || P2LevelUp.P2ScreenOpen
            ? Object.FindObjectsOfType<LevelupScreen>().FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy) : null;
        if (ls == null) { levelupSeenAt = -1f; return; }
        if (levelupSeenAt < 0f) { levelupSeenAt = Time.unscaledTime; return; }
        if (Time.unscaledTime - levelupSeenAt < 0.6f) return;   // its opening animation
        var btns = ls.upgradePicker?.buttons;
        var b = btns != null && btns.Length > 0 ? btns[0] : null;
        if (b == null) return;
        var es = UnityEngine.EventSystems.EventSystem.current;
        UnityEngine.EventSystems.ExecuteEvents.Execute(b.gameObject, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
        levelupSeenAt = Time.unscaledTime;   // the next one (another level waiting) after its own animation
        autoPicks++;
        if (autoPicks <= 40) Report($"autopick: {(P2LevelUp.P2ScreenOpen ? "P2's" : "P1's")} level-up screen took offer 0 (#{autoPicks})");
    }

    private static void CloseWindows()
    {
        if (keepWindows || Time.unscaledTime < nextWindowCheck) return;
        nextWindowCheck = Time.unscaledTime + 0.25f;
        if (autoPick) AutoPickLevelUp();
        foreach (var w in Object.FindObjectsOfType<ChestWindowUi>())
        {
            if (w == null || !w.isActiveAndEnabled) continue;
            string how;
            if (autoPick && w.b_open != null && w.b_open.isActiveAndEnabled) { w.b_open.OnClick(); how = "Open"; }
            else if (autoPick && w.b_take != null && w.b_take.isActiveAndEnabled) { w.b_take.OnClick(); how = "Take"; }
            else if (w.b_leave != null && w.b_leave.isActiveAndEnabled) { w.b_leave.OnClick(); how = "Leave"; }
            else { try { w.DiscardButton(); how = "Discard"; } catch (Exception e) { how = "failed: " + e.Message; } }
            if (windowLogs++ < 10) Report($"chest window '{w.name}' closed by the test ({how})");
        }
        // nopowerups (item sweep: power-ups the skeletons dropped - rage, speed - showed as stat "leaks"): hidden at once
        if (noPowerUps)
            foreach (var pu in Object.FindObjectsOfType<Pickup>())
                if (pu != null && pu.gameObject.activeSelf && !pu.pickedUp && P2PowerUps.IsPowerUp(pu.ePickup)) pu.gameObject.SetActive(false);
        // User: a charge shrine's reward window blocked a test - the first offer is taken (the window then closes)
        foreach (var w in Object.FindObjectsOfType<EncounterUi>())
        {
            if (w == null || !w.isActiveAndEnabled) continue;
            // v0.49.2: ChooseOffer(0) threw (NullReference) and the window stayed - its first button is clicked instead
            string how;
            var btn = w.GetComponentsInChildren<UnityEngine.UI.Button>(false).FirstOrDefault(b => b != null && b.interactable);
            if (btn != null) { try { btn.onClick.Invoke(); how = $"clicked '{btn.name}'"; } catch (Exception e) { how = "click failed: " + e.Message; } }
            else how = "no button yet";
            if (windowLogs++ < 10) Report($"reward window '{w.name}' closed by the test ({how})");
        }
    }

    // The game froze when the user pressed restart on the death screen after both players were down (v0.48.3).
    // The names come from the interop dll's strings (DeathScreen, Restart, RestartRun); which class has them is looked
    // up and logged here, not guessed: an open DeathScreen's Restart(), else a static RestartRun() on any game class.
    private static void PressRestart()
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "Assembly-CSharp");
        if (asm == null) { Report("restart: Assembly-CSharp not loaded"); return; }
        Type[] types;
        try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var t in types.Where(t => t.Name == "RestartRun" || t.GetMethods(flags).Any(m => m.Name == "RestartRun" || (m.Name == "Restart" && t.Name.Contains("Death")))))
            Report($"restart: {t.FullName} has [{string.Join(", ", t.GetMethods(flags).Where(m => m.Name.Contains("Restart")).Select(m => (m.IsStatic ? "static " : "") + m.Name))}]");
        var ds = types.FirstOrDefault(t => t.Name == "DeathScreen");
        var restart = ds?.GetMethod("Restart", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (ds != null && restart != null)
        {
            var found = Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(ds)).FirstOrDefault(o => o != null && o.Cast<Component>().gameObject.activeInHierarchy);
            if (found != null)
            {
                Report($"restart: DeathScreen '{found.name}' open - calling its Restart()");
                restart.Invoke(Activator.CreateInstance(ds, found.Pointer), null);
                return;
            }
            Report("restart: no open DeathScreen");
        }
        var run = types.Select(t => t.GetMethod("RestartRun", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null)).FirstOrDefault(m => m != null);
        if (run == null) { Report("restart: no static RestartRun() found"); return; }
        Report($"restart: calling {run.DeclaringType?.Name}.RestartRun()");
        run.Invoke(null, null);
    }

    private static void RunScript()
    {
        WatchStats();
        CloseWindows();
        if (waitUntil > 0f && Time.unscaledTime < waitUntil) return;
        waitUntil = -1f;
        while (line < lines.Count)
        {
            string l = lines[line++];
            var a = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int pi = a.Length > 1 && a[1] == "p2" ? 1 : 0;
            switch (a[0])
            {
                case "wait":
                    waitUntil = Time.unscaledTime + Float(a, 1, 1f);
                    return;
                case "waitrevive":   // waitrevive <p>: until that player is back in action (at most 30 s)
                    if ((pi == 0 ? Downed.P1Down : P2Health.IsDead) && Time.unscaledTime - (reviveWaitStart < 0f ? reviveWaitStart = Time.unscaledTime : reviveWaitStart) < 30f)
                    {
                        line--;
                        waitUntil = Time.unscaledTime + 0.05f;
                        return;
                    }
                    Report($"P{pi + 1} {((pi == 0 ? Downed.P1Down : P2Health.IsDead) ? "still down after 30 s" : "is back")}");
                    reviveWaitStart = -1f;
                    break;
                case "go":
                    pads[pi].target = null;
                    if (a.Length > 2 && (a[2] == "wall" || a[2] == "other" || a[2] == "grave"))
                    {
                        pads[pi].target = a[2];
                        if (a[2] == "wall") pads[pi].dir = FindWall(pi);
                    }
                    else pads[pi].dir = new Vector3(Float(a, 2, 0f), 0f, Float(a, 3, 0f));
                    Report($"P{pi + 1} goes {(pads[pi].target ?? pads[pi].dir.ToString())}");
                    break;
                case "stop":
                    pads[pi].dir = Vector3.zero;
                    pads[pi].target = null;
                    break;
                case "jump":
                    pads[pi].jumpUntil = Time.unscaledTime + 0.15f;
                    break;
                case "hold":
                    pads[pi].jumpUntil = Time.unscaledTime + Float(a, 2, 1f);
                    break;
                case "home":   // home [metres]: P1 on the saved spot, P2 on the saved spot that far away (40 by default; saved at the first call)
                    Cheat(() =>
                    {
                        var p1 = MyPlayer.Instance;
                        var pm = P2Movement.Pm;
                        if (p1 == null || pm == null) return;
                        if (homeA == null)
                        {
                            homeA = p1.transform.position;
                            float apart = Float(a, 1, 40f);
                            Vector3 side = homeA.Value + p1.transform.right * apart;
                            if (Physics.Raycast(side + Vector3.up * 30f, Vector3.down, out var hit, 80f)) side = hit.point + Vector3.up;
                            // Far apart (user: 300 m so each player's fight is clearly their own): open, flat ground at that
                            // distance in any of 16 directions, else nearer in 25 m steps
                            if (apart > 60f)
                            {
                                Vector3? found = null;
                                for (float d = apart; d >= 60f && found == null; d -= 25f)
                                    for (int k = 0; k < 16 && found == null; k++)
                                        if (Clear(homeA.Value + Quaternion.Euler(0f, k * 22.5f, 0f) * p1.transform.right * d, out Vector3 g)) found = g + Vector3.up;
                                if (found != null) side = found.Value;
                                else Report($"home: no open ground {apart:F0} m away - P2 {apart:F0} m to P1's right as is");
                            }
                            homeB = side;
                        }
                        void Put(Transform t, Rigidbody rb, Vector3 at) { t.position = at; if (rb != null) { rb.position = at; rb.velocity = Vector3.zero; } }
                        Put(p1.transform, p1.playerMovement?.rb, homeA.Value);
                        Put(pm.transform, pm.rb, homeB.Value);
                        Report($"home: P1 at {homeA.Value}, P2 at {homeB.Value} ({Vector3.Distance(homeA.Value, homeB.Value):F0} m apart)");
                    });
                    break;
                case "swap":   // the two players change places (item sweep: the item's holder always fights on the same spot)
                    Cheat(() =>
                    {
                        var p1 = MyPlayer.Instance;
                        var pm = P2Movement.Pm;
                        if (p1 == null || pm == null) return;
                        Vector3 a1 = p1.transform.position, a2 = pm.transform.position;
                        p1.transform.position = a2;
                        if (p1.playerMovement?.rb != null) { p1.playerMovement.rb.position = a2; p1.playerMovement.rb.velocity = Vector3.zero; }
                        pm.transform.position = a1;
                        if (pm.rb != null) { pm.rb.position = a1; pm.rb.velocity = Vector3.zero; }
                        Report($"players swapped: P1 at {a2}, P2 at {a1}");
                    });
                    break;
                case "place":
                    PlaceP2NearP1(Float(a, 1, 3f));
                    break;
                case "toslope": Cheat(ToSlope); break;
                case "enemycount": Cheat(() => Report($"enemies alive: {Assets.Scripts.Managers.EnemyManager.Instance?.enemies?.Count ?? -1}")); break;   // both players at the top of a downhill near P1, cameras facing down it
                case "backtowall":   // P1's camera turned so it sits between P1 and the nearest wall
                    Cheat(() => P2Camera.FaceP1(-FindWall(0)));
                    break;
                case "shield":
                    Cheat(() =>
                    {
                        var ph = MyPlayer.Instance?.inventory?.playerHealth;
                        var pr = MyPlayer.Instance?.playerRenderer;
                        Report($"shields: P1 {ph?.shield:F1}/{ph?.maxShield:F1} (renderer shieldActive {pr?.shieldActive}, outline on {pr?.outline?.enabled}); P2 {P2Health.Shield:F1}/{P2Health.MaxShield}");
                    });
                    break;
                case "ring": Cheat(() => RingPickups(pi, a)); break;
                case "restart": Cheat(PressRestart); break;   // the death screen's restart, as the user pressed it
                case "kill":   // kill <p>: P1's HP to 0 (the game's own death check takes it from there); P2 hit for everything
                    Cheat(() =>
                    {
                        if (pi == 1) { P2Health.SetP2TestHealth(false); P2Health.TakeDamage(10_000_000, "autotest kill"); Report($"P2 killed: down {P2Health.IsDead}"); }
                        else
                        {
                            P2Health.SetP1TestHealth(false);
                            var ph = MyPlayer.Instance?.inventory?.playerHealth;
                            if (ph != null) { ph.hp = 0; ph.shield = 0f; }
                            Report("P1's HP set to 0");
                        }
                    });
                    break;
                case "revive": Cheat(() => { Downed.Revive(pi); Report($"P{pi + 1} revive asked"); }); break;
                case "downstate": Cheat(DownState); break;
                case "findassets": Cheat(() => FindAssets(a.Skip(1).ToArray())); break;
                case "tograve":   // tograve <p>: that player 2.5 m from the other's gravestone (no walking past chests)
                    Cheat(() =>
                    {
                        var g = Gravestones.Position(1 - pi);
                        if (g == null) { Report("tograve: no gravestone"); return; }
                        var pm = pi == 0 ? MyPlayer.Instance?.playerMovement : P2Movement.Pm;
                        Vector3 to = g.Value + Vector3.right * 2.5f;
                        if (Physics.Raycast(to + Vector3.up * 20f, Vector3.down, out RaycastHit h, 60f, ~0, QueryTriggerInteraction.Ignore)) to = h.point;
                        Teleport(pm, to);
                        Report($"P{pi + 1} put next to P{2 - pi}'s gravestone");
                    });
                    break;
                case "describe": Cheat(() => Describe(a.Length > 1 ? a[1] : "")); break;
                case "iteminfo":   // iteminfo <p> <EItem>: that player's item object, its own fields
                    Cheat(() =>
                    {
                        if (a.Length < 3 || !Enum.TryParse(a[2], true, out Assets.Scripts.Inventory__Items__Pickups.Items.EItem ie)) { Report("iteminfo: usage 'iteminfo <p> <EItem>'"); return; }
                        var inv = pi == 0 ? P2Items.P1Items : P2Inventory.Items;
                        var item = inv?.GetItem(ie);
                        Report($"iteminfo P{pi + 1} {ie}: {DescribeObject(item)}");
                    });
                    break;
                case "outlines":   // every Outline under each player (user: P2's Megachad showed two shields)
                    Cheat(() =>
                    {
                        string L(GameObject root) => root == null ? "-" : string.Join("; ", root.GetComponentsInChildren<Outline>(true).Select(o =>
                            $"'{o.name}' (parent '{o.transform.parent?.name}') enabled {o.enabled}, colour {o.OutlineColor}, width {o.OutlineWidth:F1}, {o.renderers?.Length ?? 0} renderers"));
                        Report($"outlines: P1 [{L(MyPlayer.Instance?.gameObject)}]; P2 [{L(P2Weapons.Body)}]; P2 shield {P2Health.Shield:F0}/{P2Health.MaxShield}");
                    });
                    break;
                case "procs":   // procs <p>: the items the game procs on hits / before attacks, for that player
                    Cheat(() =>
                    {
                        var inv = pi == 0 ? P2Items.P1Items : P2Inventory.Items;
                        string L(Il2CppSystem.Collections.Generic.HashSet<Assets.Scripts.Inventory__Items__Pickups.Items.EItem> s)
                        {
                            if (s == null) return "-";
                            var l = new List<string>();
                            foreach (var e in s) l.Add(e.ToString());
                            return string.Join(", ", l);
                        }
                        var sorted = new List<string>();
                        if (inv?.itemsWithOnHitSorted != null) foreach (var e in inv.itemsWithOnHitSorted) sorted.Add(e.ToString());
                        Report($"procs P{pi + 1}: on hit [{L(inv?.itemsWithOnHitProcs)}], sorted [{string.Join(", ", sorted)}], pre-attack [{L(inv?.itemsWithPreAttackProcs)}]");
                    });
                    break;
                case "xpdamagers":   // the XP orbs' damage parts (Shattered Wisdom)
                    Cheat(() =>
                    {
                        var parts = new List<string>();
                        foreach (var x in Object.FindObjectsOfType<XpDamager>())
                            if (x != null) parts.Add($"enabled {x.isEnabled}, damage {x.damage:F1}, radius {x.radius:F1}, flying to {(x.pickup?.target != null ? x.pickup.target.name : "-")}");
                        Report($"xp damagers ({parts.Count}): {string.Join("; ", parts.Take(8))}");
                    });
                    break;
                case "fxscan": Cheat(() => FxScan(a.Skip(1).ToArray())); break;   // item static events' listeners + named effects under P1
                case "objsnap": Cheat(ObjSnap); break;   // every game script object now
                case "objdiff": Cheat(ObjDiff); break;   // the new ones: class, where, fields pointing at P1
                case "turn":   // turn <p> <degrees>: the player's camera turns (yaw)
                    if (pi == 1) P2Camera.TurnP2(Float(a, 2, 90f)); else P2Camera.TurnP1(Float(a, 2, 90f));
                    Report($"P{pi + 1}'s camera turned {Float(a, 2, 90f):F0} deg");
                    break;
                case "billboards":   // measured on P2's next drawn frame
                    Billboards.RequestCheck(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "", Report);
                    break;
                case "cam":
                    Report($"cameras: P1's {P2Camera.P1CamDistance:F2} m from P1's head, P2's {P2Camera.P2CamDistance:F2} m from P2's head");
                    break;
                case "open": Cheat(OpenGround); break;
                case "pickup": Cheat(() => DropPickup(pi, a)); break;
                case "snap":
                    Snap(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "");
                    break;
                case "shot":
                    Shot(a.Length > 1 ? a[1] : $"shot{line}");
                    waitUntil = Time.unscaledTime + 0.3f;   // the capture is written at the end of the frame
                    return;
                case "lobbytest": break;   // read by the lobby phase
                case "pausemenu":   // pausemenu on|off: the game's pause menu (as P1's pause)
                    Cheat(() =>
                    {
                        var ui = Object.FindObjectOfType<PauseHandler>()?.pauseUi;
                        if (ui == null) { Report("pausemenu: no pause menu"); return; }
                        if (a.Length > 1 && a[1] == "off") ui.Resume(); else ui.Pause();
                        Report($"pause menu {(a.Length > 1 && a[1] == "off" ? "closed" : "opened")}");
                    });
                    break;
                case "cammode":   // cammode: presses the pause menu's camera button (the mode is saved)
                    Cheat(() => { CameraModeToggle.Toggle(); Report($"camera mode now {(P2Camera.MergeEnabled ? "dynamic" : "split")}"); });
                    break;
                case "uinav":   // uinav up|down: a d-pad move on the selected UI button (the input module's move event)
                case "uiclick":   // uiclick: the pad's A on the selected UI button (submit event)
                    Cheat(() =>
                    {
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        var sel = es?.currentSelectedGameObject;
                        if (sel == null) { Report($"{a[0]}: nothing selected"); return; }
                        if (a[0] == "uiclick")
                        {
                            UnityEngine.EventSystems.ExecuteEvents.Execute(sel, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                            Report($"uiclick on '{sel.name}'");
                            return;
                        }
                        var ev = new UnityEngine.EventSystems.AxisEventData(es)
                        {
                            moveDir = a.Length > 1 && a[1] == "up" ? UnityEngine.EventSystems.MoveDirection.Up : UnityEngine.EventSystems.MoveDirection.Down,
                            moveVector = a.Length > 1 && a[1] == "up" ? Vector2.up : Vector2.down
                        };
                        UnityEngine.EventSystems.ExecuteEvents.Execute(sel, ev, UnityEngine.EventSystems.ExecuteEvents.moveHandler);
                        Report($"uinav {(a.Length > 1 ? a[1] : "down")}: '{sel.name}' -> '{es.currentSelectedGameObject?.name}'");
                    });
                    break;
                case "log":
                    Report("note: " + string.Join(" ", a.Skip(1)));
                    break;
                case "tome": Cheat(() => GiveTome(a)); break;
                case "weapon": Cheat(() => GiveWeapon(a)); break;
                case "item": Cheat(() => GiveItem(pi, a)); break;   // item <p> <EItem> [n]: into that player's own item inventory
                case "itemscan": Cheat(() => Report("item listeners: " + P2Items.ScanListeners())); break;
                case "unitem":   // unitem <p> <EItem>: every one of that item out of that player's inventory
                    Cheat(() =>
                    {
                        if (a.Length < 3 || !Enum.TryParse(a[2], true, out Assets.Scripts.Inventory__Items__Pickups.Items.EItem ue)) { Report("unitem: usage 'unitem <p> <EItem>'"); return; }
                        Report(ItemCheck.Remove(pi, ue));
                    });
                    break;
                case "isnap": Cheat(() => ItemCheck.Take(a.Length > 1 ? a[1] : "?")); break;   // quiet: the check reports
                case "allitems": Cheat(() => GiveAllItems(pi)); break;   // allitems <p>: one of every item
                case "chest": Cheat(() => Report(BringChest(pi))); break;   // chest <p>: an unopened chest of the map next to that player
                case "givegold":   // givegold <p> <n>
                    Cheat(() =>
                    {
                        int g = (int)Float(a, 2, 100f);
                        var inv = MyPlayer.Instance?.inventory;
                        if (inv == null) return;
                        if (pi == 0) inv.ChangeGold(g); else P2Gold.Run(() => inv.ChangeGold(g));
                        Report($"P{pi + 1} got {g} gold: P1 {inv.gold:F0}, P2 {P2Gold.Amount:F0}");
                    });
                    break;
                case "p2interact": Cheat(() => Report(P2Interact.TestInteract())); break;
                case "p1interact":   // what P1's interact button does: the game's own current interactable
                    Cheat(() =>
                    {
                        var det = MyPlayer.Instance?.GetComponentInChildren<Assets.Scripts.Inventory__Items__Pickups.Interactables.DetectInteractables>();
                        var bi = det?.currentInteractable;
                        Report(bi == null ? "P1: nothing in reach" : $"P1 interacted with {bi.GetIl2CppType().Name}: {bi.Interact()}");
                    });
                    break;
                case "chestwin": Cheat(() => Report(ChestWindowState())); break;
                case "p2levelup": Cheat(() => { P2LevelUp.QueueLevelUps((int)Float(a, 1, 1f)); Report($"P2 level-up(s) queued: {(int)Float(a, 1, 1f)}"); }); break;
                case "p2reward":   // a charge-shrine-style stat reward for P2 (the game's RandomStats window)
                    Cheat(() =>
                    {
                        var ew = Object.FindObjectOfType<Assets.Scripts.UI.InGame.Levelup.EncounterWindows>();
                        if (ew == null) { Report("p2reward: no EncounterWindows"); return; }
                        P2Rewards.MarkP2("autotest");
                        ew.AddEncounter(Assets.Scripts.UI.InGame.Rewards.EEncounter.RandomStats);
                        Report($"p2reward: RandomStats queued for P2 (queue {ew.rewardQueue?.Count}, in progress {ew.encounterInProgress})");
                    });
                    break;
                case "shrine":   // shrine <p>: one of the map's charge shrines (also one 'clearloot' switched off) at that player's feet
                    Cheat(() =>
                    {
                        var s = Resources.FindObjectsOfTypeAll<ChargeShrine>().FirstOrDefault(x => x != null && x.gameObject.scene.IsValid() && !x.name.StartsWith("MegabonkSplitscreen"));
                        if (s == null) { Report("shrine: no charge shrine on the map"); return; }
                        var at = PlayerPos(pi);
                        if (Physics.Raycast(at + Vector3.up * 5f, Vector3.down, out var hit, 20f)) at = hit.point;
                        s.transform.position = at;
                        s.gameObject.SetActive(true);
                        Report($"shrine: '{s.name}' (charge time {s.chargeTime:F1} s) at P{pi + 1}'s feet");
                    });
                    break;
                case "skins":   // skins <ECharacter>: TEMP each skin's save state vs P2's row
                    if (a.Length > 1 && Enum.TryParse(a[1], true, out ECharacter sc)) Report(P2Select.DescribeSkins(sc)); else Report("skins: usage 'skins <ECharacter>'");
                    break;
                case "pickupsall":   // TEMP: every active XP/gold pickup: where, distance to each player, flying to whom
                    Cheat(() =>
                    {
                        Vector3 a1 = PlayerPos(0), a2 = PlayerPos(1);
                        var b2 = P2Body.Body?.transform;
                        var list = Object.FindObjectsOfType<Pickup>().Where(x => x != null && (x.ePickup == EPickup.Xp || x.ePickup == EPickup.Gold))
                            .Select(x => $"{x.ePickup} at {x.transform.position} (P1 {Vector3.Distance(x.transform.position, a1):F0} m, P2 {Vector3.Distance(x.transform.position, a2):F0} m) picked {x.pickedUp} to {(x.target == null ? "-" : b2 != null && x.target.Pointer == b2.Pointer ? "P2" : x.target.name)}").ToList();
                        Report($"pickups ({list.Count}): {string.Join("; ", list.Take(12))}; P1 at {a1}, P2 at {a2}");
                    });
                    break;
                case "pickupsnear":   // TEMP pickupsnear <p>: every pickup object within 12 m of that player, active or not
                    Cheat(() =>
                    {
                        var at0 = PlayerPos(pi);
                        var b2 = P2Body.Body?.transform;
                        var list = Resources.FindObjectsOfTypeAll<Pickup>().Where(x => x != null && x.gameObject.scene.IsValid() && (x.transform.position - at0).sqrMagnitude < 144f)
                            .Select(x => $"{x.ePickup} {x.GetValue()} {Vector3.Distance(x.transform.position, at0):F1} m (dy {x.transform.position.y - at0.y:F1}) active {x.gameObject.activeInHierarchy} picked {x.pickedUp} canPickup {x.CanPickup()} to {(x.target == null ? "-" : b2 != null && x.target.Pointer == b2.Pointer ? "P2" : x.target.name)}").ToList();
                        Report($"pickups near P{pi + 1} ({list.Count}): {string.Join("; ", list.Take(15))}; P2 xp {P2XpBar.Xp}, P1 xp {MyPlayer.Instance?.inventory?.playerXp?.xp}");
                    });
                    break;
                case "pot":   // pot <p>: the nearest unbroken pot moved 2 m in front of that player
                    Cheat(() =>
                    {
                        var at0 = PlayerPos(pi);
                        var pot = Object.FindObjectsOfType<Assets.Scripts.Inventory__Items__Pickups.Interactables.InteractablePot>()
                            .Where(x => x != null && !x.broken).OrderBy(x => (x.transform.position - at0).sqrMagnitude).FirstOrDefault();
                        if (pot == null) { Report("pot: no unbroken pot on the map"); return; }
                        Vector3 fwd = pi == 0 ? (MyPlayer.Instance?.playerMovement?.orientation?.forward ?? Vector3.forward) : (P2Movement.Pm?.orientation?.forward ?? Vector3.forward);
                        fwd.y = 0f;
                        var at = at0 + fwd.normalized * 2f;
                        if (Physics.Raycast(at + Vector3.up * 5f, Vector3.down, out var hit, 20f)) at = hit.point;
                        pot.transform.position = at;
                        Report($"pot: '{pot.name}' (xp {pot.GetXp()}, big {pot.isBig}, silver {pot.isSilver}) 2 m in front of P{pi + 1}");
                    });
                    break;
                case "moai":   // moai <p>: one of the map's Moai shrines moved to that player's feet
                    Cheat(() =>
                    {
                        var all = Resources.FindObjectsOfTypeAll<InteractableShrineMoai>().Where(x => x != null && x.gameObject.scene.IsValid()).ToList();
                        var s = all.FirstOrDefault(x => x.gameObject.activeInHierarchy) ?? all.FirstOrDefault();
                        if (s == null) { Report("moai: no Moai shrine on the map"); return; }
                        var at = PlayerPos(pi);
                        Vector3 fwd = pi == 0 ? (MyPlayer.Instance?.playerMovement?.orientation?.forward ?? Vector3.forward) : (P2Movement.Pm?.orientation?.forward ?? Vector3.forward);
                        fwd.y = 0f;
                        at += fwd.normalized * 2f;
                        if (Physics.Raycast(at + Vector3.up * 5f, Vector3.down, out var hit, 20f)) at = hit.point;
                        s.transform.position = at;
                        s.gameObject.SetActive(true);
                        Report($"moai: '{s.name}' ({all.Count} on the map) 2 m in front of P{pi + 1}, can interact {s.CanInteract()}");
                    });
                    break;
                case "p2press":   // p2press <button> [seconds]  (e.g. 'p2press a', 'p2press start', 'p2press dpad right')
                    {
                        var parts = a.Skip(1).ToList();
                        float secs = 0.15f;
                        if (parts.Count > 1 && float.TryParse(parts[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s)) { secs = s; parts.RemoveAt(parts.Count - 1); }
                        string btn = string.Join(" ", parts).ToLowerInvariant();
                        p2Buttons[btn] = Time.unscaledTime + secs;
                        Report($"P2 presses '{btn}' for {secs:F2} s");
                    }
                    break;
                case "p2join":   // P2 joins (character select / lobby): the second pad, else the one pad there, else none
                    Cheat(() =>
                    {
                        bool ok = PlayerJoin.JoinFirstFreePad() || PlayerJoin.JoinFirstFreePad(anyPad: true) || PlayerJoin.JoinVirtual();
                        Report($"p2join: {(ok ? "joined" : "failed")} (P2 in {PlayerJoin.HasP2})");
                    });
                    break;
                case "p1pick":   // p1pick <ECharacter>: P1's cursor on that character (the game's OnSelectCharacter)
                    Cheat(() =>
                    {
                        var menu = Object.FindObjectOfType<CharacterMenu>();
                        if (menu == null || a.Length < 2 || !Enum.TryParse(a[1], true, out ECharacter c)) { Report("p1pick: no character menu / usage 'p1pick <ECharacter>'"); return; }
                        var b = menu.characterButtons.ToArray().FirstOrDefault(x => x != null && x.characterData != null && x.characterData.eCharacter == c);
                        if (b == null) { Report($"p1pick: {c} not in the menu"); return; }
                        menu.OnSelectCharacter(b);
                        UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(b.gameObject);
                        Report($"P1 picks {c}");
                    });
                    break;
                case "p2pick":   // p2pick <ECharacter>: P2's cursor on that character (as P2's d-pad would)
                    Cheat(() =>
                    {
                        if (a.Length < 2 || !Enum.TryParse(a[1], true, out ECharacter c)) { Report("p2pick: usage 'p2pick <ECharacter>'"); return; }
                        Report($"P2 picks {c}: {P2Select.PickCharacter(c)}");
                    });
                    break;
                case "waitselected":   // waitselected <name> [seconds]: until the game's UI has that button selected
                    {
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        string want = a.Length > 1 ? a[1] : "";
                        bool ok = es?.currentSelectedGameObject != null && es.currentSelectedGameObject.name == want;
                        if (!ok && Time.unscaledTime - (selWaitStart < 0f ? selWaitStart = Time.unscaledTime : selWaitStart) < Float(a, 2, 15f))
                        {
                            line--;
                            waitUntil = Time.unscaledTime + 0.1f;
                            return;
                        }
                        Report(ok ? $"'{want}' selected" : $"'{want}' never selected (selected: '{es?.currentSelectedGameObject?.name}')");
                        selWaitStart = -1f;
                    }
                    break;
                case "p1press":   // what P1's A does on the selected button: its Button's click actions (a UI submit did nothing on
                                  // the character buttons - menu test v0.49.6)
                    Cheat(() =>
                    {
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        var sel = es?.currentSelectedGameObject;
                        if (sel == null) { Report("p1press: nothing selected"); return; }
                        var btn = sel.GetComponent<UnityEngine.UI.Button>();
                        if (btn != null && btn.interactable) { btn.onClick.Invoke(); Report($"P1 presses A on '{sel.name}' (its click actions)"); }
                        else
                        {
                            UnityEngine.EventSystems.ExecuteEvents.Execute(sel, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                            Report($"P1 presses A on '{sel.name}' (submit; button {(btn == null ? "none" : "not interactable")})");
                        }
                    });
                    break;
                case "stagestate": Cheat(() => Report(StageState())); break;
                case "finalboss": Cheat(() => { Assets.Scripts.Managers.MapController.TestFinalBoss(); Report("finalboss: the game's TestFinalBoss()"); }); break;
                case "touching":   // touching <p>: every collider in that player's capsule (layer, enemy or not)
                    Cheat(() =>
                    {
                        Vector3 c0 = PlayerPos(pi);
                        var em = Assets.Scripts.Managers.EnemyManager.Instance;
                        var l = new List<string>();
                        foreach (var col in Physics.OverlapCapsule(c0, c0 + Vector3.up * 3.7f, 1.2f, ~0, QueryTriggerInteraction.Collide))
                        {
                            if (col == null) continue;
                            bool isEnemy = em?.collidersToEnemies != null && em.collidersToEnemies.ContainsKey(col);
                            l.Add($"'{col.name}' (root '{col.transform.root.name}', layer {LayerMask.LayerToName(col.gameObject.layer)}, trigger {col.isTrigger}, enemy {isEnemy})");
                        }
                        Report($"P{pi + 1} touching: {string.Join(" | ", l)}");
                    });
                    break;
                case "p1slide": p1SlideUntil = Time.unscaledTime + Float(a, 1, 1f); Report($"P1 slides for {Float(a, 1, 1f):F1} s"); break;
                case "tickrate": Cheat(() => Report(AnimDiag.TickRate())); break;
                case "animhook": Cheat(() => Report(AnimDiag.Apply())); break;
                case "animprobe":   // which candidate bool names P1's animator has on right now (the game can't list them)
                    Cheat(() =>
                    {
                        var an = MyPlayer.Instance?.playerRenderer?.animator;
                        if (an == null) { Report("animprobe: no P1 animator"); return; }
                        var on = new List<string>();
                        foreach (var n in new[] { "sliding", "Sliding", "slide", "Slide", "isSliding", "IsSliding", "crouching", "Crouching", "crouch", "Crouch", "isCrouching", "slideAnim", "SlideAnim", "grounded", "moving", "jumping" })
                            try { if (an.GetBool(Animator.StringToHash(n))) on.Add(n); } catch { }
                        var mdl = MyPlayer.Instance.playerRenderer?.rendererObject;
                        var pr = MyPlayer.Instance.playerRenderer;
                        string rot = mdl != null ? $"model local euler {mdl.transform.localEulerAngles}, model vs player {(Quaternion.Inverse(MyPlayer.Instance.transform.rotation) * mdl.transform.rotation).eulerAngles}, renderer obj local pos {mdl.transform.localPosition}; renderer transform local euler {pr?.transform.localEulerAngles}" : "no model";
                        var st = an.GetCurrentAnimatorStateInfo(0);
                        var names = new List<string>();
                        foreach (var n in new[] { "Slide", "Sliding", "slide", "sliding", "Crouch", "Fall", "Falling", "Jump", "Jumping", "InAir", "Air", "Run", "Idle", "Land", "Dash", "Dive" })
                            try { if (st.IsName(n)) names.Add(n); } catch { }
                        var an2 = P2Body.Anim;
                        var st2 = an2 != null ? an2.GetCurrentAnimatorStateInfo(0) : default;
                        rot += $"; P1 state {st.shortNameHash} (full {st.fullPathHash}) named [{string.Join(", ", names)}]; P2 state {(an2 != null ? st2.shortNameHash.ToString() : "-")}";
                        var nums = new List<string>();
                        foreach (var n in new[] { "state", "State", "movementState", "MovementState", "moveState", "MoveState", "crouchState", "CrouchState", "speed", "Speed", "velocity", "Velocity", "slide", "Slide", "sliding", "Sliding", "crouch", "Crouch", "type", "Type", "mode", "Mode", "action", "Action" })
                        {
                            int hh = Animator.StringToHash(n);
                            try { int iv = an.GetInteger(hh); if (iv != 0) nums.Add($"int {n}={iv}"); } catch { }
                            try { float fv = an.GetFloat(hh); if (Mathf.Abs(fv) > 0.001f) nums.Add($"float {n}={fv:F2}"); } catch { }
                        }
                        rot += $"; P1 numbers non-zero [{string.Join(", ", nums)}]";
                        Report($"animprobe: {rot}; P1's animator bools on: [{string.Join(", ", on)}]; P1 IsSlidingAnimation {MyPlayer.Instance.playerMovement?.IsSlidingAnimation()}, P2 {P2Movement.Pm?.IsSlidingAnimation()}");
                    });
                    break;
                case "facing":   // TEMP: P1's model yaw vs its movement, orientation, camera and the renderer's own look direction
                    Cheat(() =>
                    {
                        var me = MyPlayer.Instance;
                        var pr = me?.playerRenderer;
                        var pm = me?.playerMovement;
                        float Y(Vector3 v) => v.sqrMagnitude < 0.0001f ? float.NaN : Quaternion.LookRotation(new Vector3(v.x, 0f, v.z)).eulerAngles.y;
                        Report($"facing: P1 model yaw {pr?.rendererObject?.transform.eulerAngles.y:F0} (renderer object yaw {pr?.transform.eulerAngles.y:F0}, model is under it {pr?.rendererObject?.transform.IsChildOf(pr.transform)}),renderer desired look {pr?.desiredLookRotation.eulerAngles.y:F0}, last move dir {Y(pr?.lastValidMoveDirection ?? Vector3.zero):F0}, " +
                               $"velocity {Y(pm?.rb?.velocity ?? Vector3.zero):F0} ({pm?.rb?.velocity.magnitude:F1}), orientation {pm?.orientation?.eulerAngles.y:F0}, camera {Camera.main?.transform.eulerAngles.y:F0}, moving {pr?.moving}, character {pr?.characterData?.eCharacter}; " +
                               $"P2 body yaw {P2Body.Body?.transform.eulerAngles.y:F0}, velocity {Y(P2Movement.Pm?.rb?.velocity ?? Vector3.zero):F0}; game renderer for P2 {P2Control.GameRendererOn}");
                    });
                    break;
                case "animwatch":animWatchUntil = Time.time + Float(a, 1, 1f); Report("animwatch on"); break;   // TEMP: both animators every frame
                case "gamerenderer":P2Control.GameRendererOn = a.Length < 2 || a[1] != "off"; Report($"P2 game animator update {(P2Control.GameRendererOn ? "on" : "off")}"); break;   // TEMP
                case "slidestate":   // both players' slide state: speed, crouching, height
                    Cheat(() =>
                    {
                        var m1 = MyPlayer.Instance?.playerMovement;
                        var m2 = P2Movement.Pm;
                        string D(PlayerMovement m) => m == null ? "-" : $"speed {m.rb?.velocity.magnitude:F1}, input crouching {m.inputState.crouching}, slide anim {m.IsSlidingAnimation()}, scale y {m.transform.localScale.y:F2}, grounded {m.grounded}";
                        string S(Animator an) { try { return an == null ? "-" : an.GetCurrentAnimatorStateInfo(0).shortNameHash.ToString(); } catch { return "?"; } }
                        Report($"slide state: P1 {D(m1)}, anim state {S(MyPlayer.Instance?.playerRenderer?.animator)}; P2 {D(m2)}, anim state {S(P2Body.Anim)} (game renderer {P2Control.GameRendererOn})");
                    });
                    break;
                case "goldpopups": Cheat(() => Report("gold popups: " + GoldPopups.Describe())); break;
                case "arrowstate":   // the game's corner-arrow cursor (SelectionArrow) vs the button it's on
                    Cheat(() =>
                    {
                        var ar = SelectionArrow.Instance;
                        if (ar == null) { Report("arrowstate: no SelectionArrow"); return; }
                        var rt = ar.rectTransform;
                        var sel = ar.selectedRect;
                        var es = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                        Report($"arrowstate: arrow '{ar.name}' active {ar.gameObject.activeInHierarchy} pos {rt?.position} sizeDelta {rt?.sizeDelta} lossy {rt?.lossyScale.x:F2}, " +
                               $"newSize {ar.newSize}, sizeOffset {ar.sizeOffset:F1}, parent '{rt?.parent?.name}'; selectedRect '{sel?.name}' pos {sel?.position} rect {sel?.rect.size} lossy {sel?.lossyScale.x:F2}; " +
                               $"selected '{es?.name}'; canvas {rt?.GetComponentInParent<Canvas>()?.renderMode}");
                    });
                    break;
                case "showstats":   // the end screen's stats window (the game's DeathScreen.ShowStats, as its Continue does)
                    Cheat(() =>
                    {
                        var ds = Object.FindObjectOfType<DeathScreen>();
                        if (ds == null) { Report("showstats: no death screen"); return; }
                        ds.ShowStats();
                        Report("showstats: stats window opened");
                    });
                    break;
                case "killfinalboss":   // Bark Vader dies (Enemy.Kill): the game's own end of the fight
                    Cheat(() =>
                    {
                        var ff = Object.FindObjectOfType<FinalFightController>();
                        var boss = ff?.boss;
                        if (boss == null) { Report("killfinalboss: no final boss"); return; }
                        boss.Kill("autotest");
                        Report($"killfinalboss: {boss.enemyData?.enemyName} killed (phase {ff.currentPhase})");
                    });
                    break;
                case "screentext":   // every visible text on screen (the end screen: what it says about each player)
                    Cheat(() =>
                    {
                        var l = new List<string>();
                        foreach (var t in Object.FindObjectsOfType<TMPro.TextMeshProUGUI>())
                            if (t != null && t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text) && t.canvasRenderer != null && t.canvasRenderer.GetAlpha() > 0.01f)
                                l.Add($"{t.transform.parent?.name}/{t.name}: '{t.text.Replace("\n", " ").Trim()}'");
                        Report($"screen text ({l.Count}): {string.Join(" | ", l.Take(120))}");
                    });
                    break;
                case "bossorbs":   // bossorbs <bleed|following|shooty>: Bark Vader's orbs now (FinalFightController.SpawnOrbs*)
                    Cheat(() =>
                    {
                        var ff = Object.FindObjectOfType<FinalFightController>();
                        if (ff == null) { Report("bossorbs: no final fight"); return; }
                        string k = a.Length > 1 ? a[1].ToLowerInvariant() : "bleed";
                        if (k == "following") ff.SpawnOrbsFollowing(); else if (k == "shooty") ff.SpawnOrbsShooty(); else ff.SpawnOrbsBleed();
                        Report($"bossorbs: {k} spawned (phase {ff.currentPhase})");
                    });
                    break;
                case "toboss":   // toboss <p> <m>: that player <m> metres from the final boss, on the arena side toward the spawn
                    Cheat(() =>
                    {
                        var ff = Object.FindObjectOfType<FinalFightController>();
                        var boss = ff?.boss;
                        if (boss == null) { Report("toboss: no final boss"); return; }
                        Vector3 b = boss.transform.position;
                        Vector3 from = PlayerPos(pi) - b; from.y = 0f;
                        from = from.sqrMagnitude > 0.01f ? from.normalized : Vector3.forward;
                        Vector3 at = b + from * Float(a, 2, 6f);
                        // the arena floor (Ground layer); "top": whatever is highest there - standing on the boss
                        bool top = a.Length > 3 && a[3] == "top";
                        if (Physics.Raycast(at + Vector3.up * 40f, Vector3.down, out var hit, 120f, top ? ~0 : 1 << 8)) at = hit.point + Vector3.up;
                        void Put(Transform t, Rigidbody rb) { t.position = at; if (rb != null) { rb.position = at; rb.velocity = Vector3.zero; } }
                        if (pi == 0) Put(MyPlayer.Instance.transform, MyPlayer.Instance.playerMovement?.rb);
                        else if (P2Movement.Pm != null) Put(P2Movement.Pm.transform, P2Movement.Pm.rb);
                        Report($"P{pi + 1} put {Float(a, 2, 6f):F0} m from {boss.enemyData?.enemyName} ({at})");
                    });
                    break;
                case "bossattack":   // bossattack <attackName>: the spawned enemy that has it uses it now (the game's UseSpecialAttack)
                    Cheat(() =>
                    {
                        string want = a.Length > 1 ? a[1] : "";
                        var em = Assets.Scripts.Managers.EnemyManager.Instance;
                        if (em?.enemies == null) { Report("bossattack: no enemies"); return; }
                        foreach (var kv in em.enemies)
                        {
                            var en = kv.Value;
                            var sas = en?.enemyData?.specialAttacks;
                            var ctl = en?.specialAttackController;
                            if (sas == null || ctl == null) continue;
                            foreach (var sa in sas)
                                if (sa != null && (sa.attackName == want || AttackKind(sa) == want))
                                {
                                    ctl.UseSpecialAttack(sa);
                                    Report($"bossattack: {en.enemyData.enemyName} uses '{want}'");
                                    return;
                                }
                        }
                        Report($"bossattack: no spawned enemy has '{want}'");
                    });
                    break;
                case "attacks":   // attacks <EEnemy>: that enemy's name and special attacks (name, prefab kind, radius)
                    Cheat(() =>
                    {
                        if (a.Length < 2 || !Enum.TryParse(a[1], true, out Actors.Enemies.EEnemy e)) { Report("attacks: usage 'attacks <EEnemy>'"); return; }
                        var data = DataManager.Instance?.GetEnemyData(e);
                        var list = new List<string>();
                        if (data?.specialAttacks != null)
                            foreach (var sa in data.specialAttacks)
                                if (sa != null) list.Add($"'{sa.attackName}' ({AttackKind(sa)}, radius {sa.attackRadius:F1}, cooldown {sa.attackCooldown:F1})");
                        Report($"attacks {e}: '{data?.enemyName}' - {(list.Count > 0 ? string.Join(", ", list) : "none")}");
                    });
                    break;
                case "gems": Cheat(() => GemReport(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")); break;   // TEMP
                case "spinprobe":Report(P2Weapons.SpinProbe(Float(a, 1, 1f))); break;   // TEMP: always-on attacks' yaw per frame
                case "debuffsources": Cheat(() => Report("debuff sources: " + P2Debuffs.Sources())); break;
                case "debuff":   // debuff <p> <Slow|Freeze|Bleed|Poison|BossPoison> <secs>: the game's own function on that player's list
                    Cheat(() => Report(P2Debuffs.Give(pi, a.Length > 2 ? a[2] : "", Float(a, 3, 5f))));
                    break;
                case "statuses":   // both players' status effects, speed, HP
                    Cheat(() =>
                    {
                        var p1 = MyPlayer.Instance;
                        var ph = p1?.inventory?.playerHealth;
                        Report($"statuses: P1 [{P2Debuffs.Describe(p1?.inventory?.statusEffects)}] hp {ph?.hp}/{ph?.maxHp}, speed {p1?.playerMovement?.rb?.velocity.magnitude:F1}, " +
                               $"MoveSpeed stat {p1?.inventory?.playerStats?.GetStat(Assets.Scripts.Menu.Shop.EStat.MoveSpeedMultiplier):F2}; " +
                               $"P2 [{P2PowerUps.DescribeStatus()}] hp {P2Health.Hp}/{P2Health.MaxHp}, speed {P2Movement.Pm?.rb?.velocity.magnitude:F1}, MoveSpeed stat {P2Inventory.Get(Assets.Scripts.Menu.Shop.EStat.MoveSpeedMultiplier):F2}");
                    });
                    break;
                case "trackp1":   // trackp1 <secs>: P1's position/velocity every 0.5 s, and any jump > 8 m in one frame (final boss stage: P1 left the arena)
                    trackUntil = Time.unscaledTime + Float(a, 1, 20f);
                    trackNext = 0f;
                    trackLast = null;
                    Report($"tracking P1 for {Float(a, 1, 20f):F0} s");
                    break;
                case "addxp":   // addxp <p> <n>: XP for that player, as from gems (needs 'xp on'; level-ups open their screens)
                    Cheat(() =>
                    {
                        int n = (int)Float(a, 2, 100f);
                        if (pi == 0) MyPlayer.Instance?.inventory?.playerXp?.AddXp(n);
                        else P2XpBar.AddXp(n);
                        Report($"P{pi + 1} +{n} xp");
                    });
                    break;
                case "pickoffer":   // pickoffer [i]: the open level-up screen takes offer i (default 0), as clicked
                    Cheat(() =>
                    {
                        var ls = Object.FindObjectsOfType<LevelupScreen>().FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy);
                        if (ls == null) { Report("pickoffer: no level-up screen open"); return; }
                        bool p2 = P2LevelUp.P2ScreenOpen;
                        // A pad's A on the offer button (v0.49.8: ChooseOffer(0) and UpgradePicker.AutoSelectUpgrade applied an
                        // upgrade but left the screen open)
                        int oi = (int)Float(a, 1, 0f);
                        var btns = ls.upgradePicker?.buttons;
                        var b = btns != null && oi < btns.Length ? btns[oi] : null;
                        if (b == null) { Report("pickoffer: no offer button"); return; }
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        UnityEngine.EventSystems.ExecuteEvents.Execute(b.gameObject, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                        Report($"pickoffer: {(p2 ? "P2's" : "P1's")} screen, A on offer {oi} ('{b.name}')");
                    });
                    break;
                case "bossportal":   // the stage's exit portal opens, as after the boss died (InteractableBossSpawner.portal)
                    Cheat(() =>
                    {
                        var sp = Object.FindObjectOfType<InteractableBossSpawner>();
                        if (sp == null || sp.portal == null) { Report($"bossportal: no boss spawner{(sp != null ? " portal" : "")} in this stage"); return; }
                        sp.portal.SetActive(true);
                        var ip = sp.portal.GetComponentInChildren<InteractablePortal>(true);
                        Report($"bossportal: '{sp.portal.name}' active {sp.portal.activeInHierarchy} at {sp.portal.transform.position}, InteractablePortal {(ip != null ? $"'{ip.name}' at {ip.transform.position}, done {ip.done}" : "none")}");
                        homeA = null;   // 'home' re-saves its spots in the next stage
                    });
                    break;
                case "killbosses":   // the stage boss(es) the altar spawned die (Enemy.Kill): the game opens the exit portal itself
                    Cheat(() =>
                    {
                        var sp = Object.FindObjectOfType<InteractableBossSpawner>();
                        var list = sp?.bossEnemies;
                        int n = 0;
                        if (list != null)
                            for (int i = 0; i < list.Count; i++)
                                if (list[i] != null) { try { list[i].Kill("autotest"); n++; } catch (Exception e) { Report($"killbosses: {e.Message}"); } }
                        Report($"killbosses: {n} boss(es) killed; portal {(sp?.portal != null ? $"active {sp.portal.activeInHierarchy}" : "none")}");
                        homeA = null;
                    });
                    break;
                case "toportal":   // toportal <p>: that player next to the exit portal (in interact reach)
                    Cheat(() =>
                    {
                        var sp0 = Object.FindObjectOfType<InteractableBossSpawner>();
                        Transform ip = sp0 != null ? sp0.transform : Object.FindObjectOfType<InteractablePortal>()?.transform;   // the altar; the portal opens on it
                        // a tier's last stage may have the final kinds instead
                        ip ??= Object.FindObjectOfType<InteractableBossSpawnerFinal>()?.transform ?? Object.FindObjectOfType<InteractablePortalFinal>()?.transform;
                        if (ip == null) { Report("toportal: no boss altar / portal"); return; }
                        // "far": 10 m off first, so walking back in is a real trigger entry (one placed inside never "enters")
                        float off = a.Length > 2 && a[2] == "far" ? 10f : 1.5f;
                        Vector3 at = ip.position + Vector3.up * 0.5f + (pi == 0 ? Vector3.right : Vector3.left) * off;
                        var pp = Object.FindObjectOfType<InteractablePortal>();
                        if (pp != null) Report($"portal: '{pp.name}' active {pp.gameObject.activeInHierarchy} at {pp.transform.position}, done {pp.done}");
                        void Put(Transform t, Rigidbody rb) { t.position = at; if (rb != null) { rb.position = at; rb.velocity = Vector3.zero; } }
                        if (pi == 0) Put(MyPlayer.Instance.transform, MyPlayer.Instance.playerMovement?.rb);
                        else if (P2Movement.Pm != null) Put(P2Movement.Pm.transform, P2Movement.Pm.rb);
                        Report($"P{pi + 1} placed at the portal ({at})");
                    });
                    break;
                case "allskins":   // P2 may use every skin (the test save owns only default skins)
                    P2Select.TestAllSkins = true;
                    Report("P2 may use every skin (test)");
                    break;
                case "p1confirm":   // P1's two A presses in character select: the character confirm, then the Confirm button
                    Cheat(() =>
                    {
                        var menu = Object.FindObjectOfType<CharacterMenu>();
                        var b = menu?.selectedButton;
                        if (menu == null || b == null) { Report("p1confirm: no character menu / no P1 pick"); return; }
                        menu.OnConfirmCharacter(b);
                        var cb = menu.b_confirm?.GetComponent<UnityEngine.UI.Button>();
                        if (cb != null) cb.onClick.Invoke();
                        Report($"P1 confirms {b.characterData?.eCharacter} (Confirm button {(cb != null ? "pressed" : "not found")})");
                    });
                    break;
                case "lobbyconfirm": Cheat(() => { Lobby.Confirm(); Report("lobby confirmed (what P1's A does there)"); }); break;
                case "play":   // the main menu's Play: a submit on the selected button if it's the Play one, else the game's own call
                    Cheat(() =>
                    {
                        var mm = Object.FindObjectOfType<MainMenu>();
                        if (mm == null) { Report("play: no main menu"); return; }
                        mm.GoToCharacterSelection();
                        Report("play: MainMenu.GoToCharacterSelection");
                    });
                    break;
                case "menustate":   // where the menus are and who controls them
                    Cheat(() => Report("menu state: " + MenuState()));
                    break;
                case "hazards":   // which hazard objects this map has (jump pads, water/lava, rails, tornadoes, healing zones)
                    Cheat(() => Report($"hazards on this map: jump pads {Object.FindObjectsOfType<JumpPad>().Length}, water {Object.FindObjectsOfType<Water>().Length}, " +
                        $"rails {Object.FindObjectsOfType<Rail>().Length}, tornadoes {Object.FindObjectsOfType<Tornado>().Length}, healing zones {Object.FindObjectsOfType<HealingZone>().Length}"));
                    break;
                case "hazard":   // hazard <pad|water|rail> <p>: that player dropped onto the nearest one; then 'hazardstate'
                    Cheat(() =>
                    {
                        string kind = a.Length > 1 ? a[1] : "pad";
                        int who = a.Length > 2 && a[2] == "p2" ? 1 : 0;
                        Vector3 from = PlayerPos(who);
                        Component target = kind switch
                        {
                            "water" => Object.FindObjectsOfType<Water>().OrderBy(x => (x.transform.position - from).sqrMagnitude).FirstOrDefault(),
                            "rail" => Object.FindObjectsOfType<Rail>().OrderBy(x => (x.transform.position - from).sqrMagnitude).FirstOrDefault(),
                            _ => Object.FindObjectsOfType<JumpPad>().OrderBy(x => (x.transform.position - from).sqrMagnitude).FirstOrDefault(),
                        };
                        if (target == null) { Report($"hazard {kind}: none on this map"); return; }
                        Vector3 at = target.transform.position + Vector3.up * 1.2f;
                        var col = target.GetComponentInChildren<Collider>();
                        if (col != null) at = col.bounds.center + Vector3.up * (kind == "water" ? 0f : col.bounds.extents.y + 0.6f);
                        if (who == 0) { var p1 = MyPlayer.Instance; p1.transform.position = at; if (p1.playerMovement?.rb != null) { p1.playerMovement.rb.position = at; p1.playerMovement.rb.velocity = Vector3.zero; } }
                        else { var pm2 = P2Movement.Pm; pm2.transform.position = at; if (pm2.rb != null) { pm2.rb.position = at; pm2.rb.velocity = Vector3.zero; } }
                        Report($"hazard {kind}: P{who + 1} dropped on '{target.name}' at {at}");
                    });
                    break;
                case "hazardstate":   // both players' speed, grounded, water/rail state (PlayerMovement's own fields, logged by name)
                    Cheat(() =>
                    {
                        string S(PlayerMovement m) => m == null ? "-" : $"vel {m.rb?.velocity}, grounded {m.grounded}, state {m.lastMovementState}";
                        Report($"hazard state: P1 {S(MyPlayer.Instance?.playerMovement)} | P2 {S(P2Movement.Pm)}");
                    });
                    break;
                case "rewardqueue":
                    Cheat(() =>
                    {
                        var ew = Object.FindObjectOfType<Assets.Scripts.UI.InGame.Levelup.EncounterWindows>();
                        Report($"reward queue: {ew?.rewardQueue?.Count} waiting, in progress {ew?.encounterInProgress}, P1 down {Downed.P1Down}, P1 hp {MyPlayer.Instance?.inventory?.playerHealth?.hp}, P2 level-up screen {P2LevelUp.P2ScreenOpen}");
                    });
                    break;
                case "tornado":   // the game's own tornado spawn (EffectManager.SpawnTornadoes)
                    Cheat(() =>
                    {
                        if (a.Length > 1 && a[1] == "near")
                        {
                            // The nearest tornado moved between the players (the game's storm puts them all over the map)
                            var p1 = MyPlayer.Instance;
                            var pm2 = P2Movement.Pm;
                            if (p1 == null || pm2 == null) return;
                            Vector3 mid = (p1.transform.position + pm2.transform.position) * 0.5f;
                            var t = Object.FindObjectsOfType<Tornado>().OrderBy(x => (x.transform.position - mid).sqrMagnitude).FirstOrDefault();
                            if (t == null) { Report("tornado near: none"); return; }
                            t.transform.position = mid;
                            if (t.rb != null) t.rb.position = mid;
                            Report($"tornado moved between the players at {mid}");
                            return;
                        }
                        EffectManager.Instance?.SpawnTornadoes((int)Float(a, 1, 1f));
                        Report("tornado(es) spawned");
                    });
                    break;
                case "tornadoes":   // each tornado: distance to both players, its force; both players' speed
                    Cheat(() =>
                    {
                        var p1 = MyPlayer.Instance;
                        var pm = P2Movement.Pm;
                        var parts = Object.FindObjectsOfType<Tornado>().Where(t => (p1 != null && Vector3.Distance(t.transform.position, p1.transform.position) < 40f) || (pm != null && Vector3.Distance(t.transform.position, pm.transform.position) < 40f)).Select(t => $"at {t.transform.position} force {t.force:F1} - P1 {(p1 != null ? Vector3.Distance(t.transform.position, p1.transform.position) : -1):F1} m, P2 {(pm != null ? Vector3.Distance(t.transform.position, pm.transform.position) : -1):F1} m").ToList();
                        Report($"tornadoes ({parts.Count}): {string.Join("; ", parts)} | P1 vel {p1?.playerMovement?.rb?.velocity}, P2 vel {pm?.rb?.velocity}");
                    });
                    break;
                case "freechest":   // freechest <p>: one of the game's free chests (OpenChest) dropped at that player's feet
                    Cheat(() =>
                    {
                        var src = Resources.FindObjectsOfTypeAll<Assets.Scripts.Inventory__Items__Pickups.Interactables.OpenChest>().FirstOrDefault(c => c != null);
                        if (src == null) { Report("freechest: no OpenChest found (prefab or scene)"); return; }
                        var at = PlayerPos(pi) + Vector3.up * 0.5f;
                        var go = Object.Instantiate(src.gameObject, at, Quaternion.identity);
                        go.SetActive(true);
                        var oc = go.GetComponent<Assets.Scripts.Inventory__Items__Pickups.Interactables.OpenChest>();
                        if (oc != null) { oc.pickedup = false; oc.readyForPickupTime = 0f; }
                        Report($"freechest: '{src.name}' ({(src.gameObject.scene.IsValid() ? "scene copy" : "prefab")}, {oc?.chestType}) dropped at P{pi + 1}");
                    });
                    break;
                case "chestprice":   // both players' chest price and purchase counts
                    Cheat(() => Report($"chest price: P1 {Assets.Scripts.Inventory__Items__Pickups.GoldAndMoney.MoneyUtility.GetChestPrice()} " +
                        $"(bought {Assets.Scripts.Inventory__Items__Pickups.GoldAndMoney.MoneyUtility.chestsPurchased}), P2 {P2ChestPrice.P2Price} (P2's HUD)"));
                    break;
                case "icheck": Cheat(() => Report(ItemCheck.Check(a.Length > 1 ? a[1] : "?"))); break;
                case "idelta": Cheat(() => Report(ItemCheck.Delta(a.Length > 1 ? a[1] : "?", a.Length > 2 ? a[2] : "?"))); break;
                case "items": Cheat(() => Report($"items: P1 [{P2Items.DescribeP1()}]; P2 [{P2Items.Describe(P2Inventory.Items)}]")); break;
                case "dmgsources": Cheat(() => Report("damage sources: " + DamageSources())); break;
                case "gold": Cheat(() => Report($"gold: P1 {MyPlayer.Instance?.inventory?.gold:F0}, P2 {P2Gold.Amount:F0}")); break;
                case "hurt": Cheat(() => Hurt(pi, (int)Float(a, 2, 10f))); break;
                case "spawn": Cheat(() => Spawn(pi, a)); break;
                case "stat": Cheat(() => Stat(pi, a)); break;
                case "map":
                    Cheat(() =>
                    {
                        bool on = a.Length > 2 && a[2] == "on";
                        if (pi == 1) { p2Trigger = on; Report($"P2 holds the map trigger: {on}"); }
                        else { Object.FindObjectOfType<FullMap>()?.OnMapToggle(on); Report($"P1's map toggled {(on ? "on" : "off")}"); }
                    });
                    break;
                case "mapcheck": Cheat(MapView.Describe); break;
                case "godp1": Cheat(() => { bool on = a.Length < 2 || a[1] != "off"; P2Health.SetP1TestHealth(on); Report($"P1 test health {(on ? "on" : "off")}"); }); break;
                case "xp": xpOn = a.Length > 1 && a[1] == "on"; Report($"XP {(xpOn ? "on" : "off")}"); break;
                case "godp2": Cheat(() => { bool on = a.Length < 2 || a[1] != "off"; P2Health.SetP2TestHealth(on); Report($"P2 test health {(on ? "on" : "off")}"); }); break;
                case "unweapon": Cheat(() => RemoveWeapon(a)); break;
                case "clear": Cheat(ClearEnemies); break;
                case "clearloot": Cheat(ClearLoot); break;
                case "nopowerups": noPowerUps = a.Length < 2 || a[1] != "off"; Report($"power-ups {(noPowerUps ? "hidden" : "allowed")}"); break;
                case "autopick": autoPick = a.Length < 2 || a[1] != "off"; Report($"autopick {(autoPick ? "on" : "off")}: level-ups take offer 0, chests open and take"); break;
                case "keepwindows":keepWindows = a.Length < 2 || a[1] != "off"; Report($"chest windows {(keepWindows ? "kept open" : "closed by the test")}"); break;
                case "kills":
                    Cheat(() =>
                    {
                        if (a.Length > 1 && a[1] == "reset") { P2Pickups.KillsP1 = P2Pickups.KillsP2 = 0; return; }
                        Report($"kills {(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")}: P1 {P2Pickups.KillsP1}, P2 {P2Pickups.KillsP2}");
                    });
                    break;
                case "dmg":
                    Cheat(() => { if (a.Length > 1 && a[1] == "reset") spawned.Clear(); else DamageReport(a.Length > 1 ? string.Join(" ", a.Skip(1)) : ""); });
                    break;
                case "bananafix":   // TEMP bananafix off|on: P2's banana aim fix (one run measures without and with it)
                    P2Weapons.BananaFix = a.Length < 2 || a[1] != "off";
                    Report($"P2 banana aim fix {(P2Weapons.BananaFix ? "on" : "off")}");
                    break;
                case "bananaprobe":   // TEMP bananaprobe [label]: P2's outward bananas vs the nearest enemy (resets)
                    Report($"banana {(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")}: {P2Weapons.BananaProbe(true)}");
                    break;
                case "spawnside":   // spawnside <p> <EEnemy> <count> <distance> [turn deg]: in an arc on the side away from the other player
                    Cheat(() => SpawnSide(pi, a));
                    break;
                case "knock":   // knock [reset|label]: enemy knockbacks near P2 (away from / towards P2)
                    Cheat(() => { if (a.Length > 1 && a[1] == "reset") Knockback.Reset(); else Report($"knock {(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")}: {Knockback.Report()}"); });
                    break;
                case "xpstate":   // xpstate [label]: both players' XP and level
                    Cheat(() =>
                    {
                        var px = MyPlayer.Instance?.inventory?.playerXp;
                        Report($"xp {(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")}: P1 {px?.xp} (level {px?.level}), P2 {P2XpBar.Xp} (level {Inventory__Items__Pickups.XpUtility.XpToLevel(P2XpBar.Xp)}); kills P1 {P2Pickups.KillsP1}, P2 {P2Pickups.KillsP2}");
                    });
                    break;
                case "statcompare":   // TEMP: every stat where P1's value (the game's) and P2's (P2's own table) differ
                    Cheat(() =>
                    {
                        var ps = MyPlayer.Instance?.inventory?.playerStats;
                        var diffs = new List<string>();
                        int same = 0;
                        foreach (Assets.Scripts.Menu.Shop.EStat s in Enum.GetValues(typeof(Assets.Scripts.Menu.Shop.EStat)))
                        {
                            float v1, v2;
                            try { v1 = ps?.GetStat(s) ?? float.NaN; v2 = P2Inventory.Get(s); } catch { continue; }
                            if (Mathf.Abs(v1 - v2) > 0.0001f) diffs.Add($"{s} P1 {v1:0.###} / P2 {v2:0.###}"); else same++;
                        }
                        Report($"statcompare {(a.Length > 1 ? a[1] : "")}: {same} same; differ: {(diffs.Count > 0 ? string.Join(", ", diffs) : "none")}");
                    });
                    break;
                case "mark": Cheat(MarkStats); break;
                case "statdiff": Cheat(() => StatDiff(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")); break;
                case "statdump":   // statdump <label>: both players' whole stat tables (compared across runs by tools\leaksweep.ps1)
                    Cheat(() =>
                    {
                        string label = a.Length > 1 ? a[1] : "";
                        var p1 = new List<string>(); var p2 = new List<string>();
                        foreach (Assets.Scripts.Menu.Shop.EStat s in Enum.GetValues(typeof(Assets.Scripts.Menu.Shop.EStat)))
                        {
                            var (v1, v2) = StatPair(s);
                            p1.Add($"{s}={v1:0.####}");
                            p2.Add($"{s}={v2:0.####}");
                        }
                        Report($"statdump {label} P1 {string.Join(";", p1)}");
                        Report($"statdump {label} P2 {string.Join(";", p2)}");
                    });
                    break;
                case "health":   // both players' HP and shield
                    Cheat(() =>
                    {
                        var ph = MyPlayer.Instance?.inventory?.playerHealth;
                        Report($"health {(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")}: P1 {ph?.hp}/{ph?.maxHp} shield {ph?.shield:F0}/{ph?.maxShield:F0}; P2 {P2Health.Hp}/{P2Health.MaxHp} shield {P2Health.Shield:F0}/{P2Health.MaxShield}");
                    });
                    break;
                case "passives": Cheat(() => Passives(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")); break;
                case "delta": Cheat(() => StatDelta(a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")); break;
                case "wcompare": Cheat(() => WeaponCompare(a)); break;
                case "quit":
                    Report("script done");
                    Quit();
                    return;
                default:
                    if (!BugCommand(a, pi)) Report($"unknown command: {l}");
                    break;
            }
        }
        Report("script done (end of file)");
        Quit();
    }

    private static float Float(string[] a, int i, float def) =>
        a.Length > i && float.TryParse(a[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : def;

    // ---------- Input ----------

    private static Vector3 PlayerPos(int pi) => pi == 0 ? (MyPlayer.Instance != null ? MyPlayer.Instance.transform.position : Vector3.zero)
                                                        : (P2Movement.Pm != null ? P2Movement.Pm.transform.position : Vector3.zero);

    private static Vector3 WantedDir(int pi)
    {
        var p = pads[pi];
        if (p.target == "other")
        {
            Vector3 d = PlayerPos(1 - pi) - PlayerPos(pi);
            d.y = 0f;
            return d.sqrMagnitude > 4f ? d.normalized : Vector3.zero;
        }
        if (p.target == "grave")   // go <p> grave: to the other player's gravestone, stopping 1.5 m from it
        {
            var g = Gravestones.Position(1 - pi);
            if (g == null) return Vector3.zero;
            Vector3 d = g.Value - PlayerPos(pi);
            d.y = 0f;
            return d.sqrMagnitude > 1.5f * 1.5f ? d.normalized : Vector3.zero;
        }
        return p.dir;
    }

    // World direction -> stick, relative to the player's facing frame (P1: its movement orientation; P2: P2's camera)
    private static Vector2 Stick(int pi)
    {
        Vector3 d = WantedDir(pi);
        if (d.sqrMagnitude < 0.0001f) return Vector2.zero;
        Quaternion frame;
        if (pi == 0)
        {
            var o = MyPlayer.Instance?.playerMovement?.orientation;
            frame = o != null ? Quaternion.Euler(0f, o.eulerAngles.y, 0f) : Quaternion.identity;
        }
        else frame = Quaternion.Euler(0f, P2Camera.Yaw, 0f);
        Vector3 local = Quaternion.Inverse(frame) * d.normalized;
        return new Vector2(local.x, local.z);
    }

    public static Vector2 P2Stick() => Stick(1);
    private static bool p2Trigger;
    public static bool P2Trigger => p2Trigger;

    // ---------- Cheats and checks ----------
    private static void Cheat(Action a)
    {
        try { a(); }
        catch (Exception e) { Report($"ERROR: {e.Message}"); }
    }

    // tome p2 <ETome> [levels]: the game's own AddTome on P2's tome holder, with the game's offer for that tome
    private static void GiveTome(string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out Assets.Scripts._Data.Tomes.ETome t)) { Report("tome: usage 'tome p2 <ETome> [levels]'"); return; }
        int n = (int)Float(a, 3, 1f);
        var data = DataManager.Instance?.GetTome(t);
        if (a[1] == "p1")
        {
            var inv = MyPlayer.Instance?.inventory;
            if (data == null || inv?.tomeInventory == null) { Report($"tome: no data for {t}"); return; }
            for (int i = 0; i < n; i++) inv.tomeInventory.AddTome(data, data.GetUpgradeOffer(Assets.Scripts.Inventory__Items__Pickups.ERarity.Common), Assets.Scripts.Inventory__Items__Pickups.ERarity.Common);
            Report($"P1 got tome {t} x{n}");
            return;
        }
        if (data == null || P2Inventory.TomesInv == null) { Report($"tome: no data for {t} or P2 has no tome holder"); return; }
        P2Inventory.GuardsOn();
        bool entered = P2Inventory.EnterP2();
        try
        {
            for (int i = 0; i < n; i++)
                P2Inventory.TomesInv.AddTome(data, data.GetUpgradeOffer(Assets.Scripts.Inventory__Items__Pickups.ERarity.Common), Assets.Scripts.Inventory__Items__Pickups.ERarity.Common);
        }
        finally { if (entered) P2Inventory.Exit(); P2Inventory.GuardsOff(); }
        P2Inventory.MarkDirty();
        Report($"P2 got tome {t} x{n} (level now {P2Inventory.TomesInv.GetTomeLevel(t)})");
    }

    // weapon p2 <EWeapon>: the game's own AddWeapon on P2's weapon holder (P2's weapon code picks it up)
    private static void GiveWeapon(string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out EWeapon w)) { Report("weapon: usage 'weapon p2 <EWeapon>'"); return; }
        var data = DataManager.Instance?.GetWeapon(w);
        if (a[1] == "p1")
        {
            var inv = MyPlayer.Instance?.inventory;
            if (data == null || inv?.weaponInventory == null) { Report($"weapon: no data for {w}"); return; }
            inv.weaponInventory.AddWeapon(data, new Il2CppSystem.Collections.Generic.List<Assets.Scripts.Inventory__Items__Pickups.Stats.StatModifier>());
            Report($"P1 got weapon {w}");
            return;
        }
        if (data == null || P2Inventory.Weapons == null) { Report($"weapon: no data for {w} or P2 has no weapon holder"); return; }
        P2Inventory.GuardsOn();
        bool entered = P2Inventory.EnterP2();
        try { P2Inventory.Weapons.AddWeapon(data, new Il2CppSystem.Collections.Generic.List<Assets.Scripts.Inventory__Items__Pickups.Stats.StatModifier>()); }
        finally { if (entered) P2Inventory.Exit(); P2Inventory.GuardsOff(); }
        // v0.42.7 (user: P2 didn't fire the test weapons): P2's firing list follows its holder only when the weapon
        // count/levels change - a remove + add keeps the count, so it's refreshed here
        P2Weapons.Sync();
        Report($"P2 got weapon {w} (firing: {P2Weapons.IsFiring(w)})");
    }

    // unweapon <p> <EWeapon>: out of the holder; P1's always-on effect via the game's AuraAttacks.OnWeaponRemoved,
    // P2's via P2Weapons.Sync
    private static void RemoveWeapon(string[] a)
    {
        // unweapon <p> all: every weapon that player has (each character starts with its own)
        if (a.Length >= 3 && a[2] == "all")
        {
            var held = a[1] == "p1" ? MyPlayer.Instance?.inventory?.weaponInventory?.weapons : P2Inventory.Weapons?.weapons;
            var keys = new List<EWeapon>();
            if (held != null) foreach (var kv in held) keys.Add(kv.Key);
            foreach (var k in keys) RemoveWeapon(new[] { a[0], a[1], k.ToString() });
            if (keys.Count == 0) Report($"unweapon: {a[1]} has no weapons");
            return;
        }
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out EWeapon w)) { Report("unweapon: usage 'unweapon <p> <EWeapon>'"); return; }
        if (a[1] == "p1")
        {
            var p1 = MyPlayer.Instance;
            var weapons = p1?.inventory?.weaponInventory?.weapons;
            if (weapons == null || !weapons.ContainsKey(w)) { Report($"unweapon: P1 has no {w}"); return; }
            var wb = weapons[w];
            weapons.Remove(w);
            try { p1.playerConstantAttacks?.OnWeaponRemoved(wb); } catch (Exception e) { Report($"unweapon P1 aura: {e.Message}"); }
            try { Object.FindObjectOfType<InventoryHud>()?.Refresh(); } catch { }
            Report($"P1 lost weapon {w}");
            return;
        }
        var p2w = P2Inventory.Weapons?.weapons;
        if (p2w == null || !p2w.ContainsKey(w)) { Report($"unweapon: P2 has no {w}"); return; }
        p2w.Remove(w);
        P2Weapons.Sync();
        Report($"P2 lost weapon {w}");
    }

    // item <p> <EItem> [n]: P1's straight into P1's item inventory; P2's with P2's inventory in P1's place (as P2's chest
    // window does), so the game's own AddItem runs on P2's
    private static void GiveItem(int pi, string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out Assets.Scripts.Inventory__Items__Pickups.Items.EItem e)) { Report("item: usage 'item <p> <EItem> [n]'"); return; }
        int n = (int)Float(a, 3, 1f);
        if (pi == 0)
        {
            var inv = MyPlayer.Instance?.inventory?.itemInventory;
            if (inv == null) { Report("item: P1 has no item inventory"); return; }
            inv.AddItem(e, n);
        }
        else
        {
            if (P2Inventory.Items == null) { Report("item: P2 has no item inventory"); return; }
            P2Inventory.AsP2("test item", items: true, stats: false, () => MyPlayer.Instance.inventory.itemInventory.AddItem(e, n));
        }
        Report($"P{pi + 1} got item {e} x{n}: P1 [{P2Items.DescribeP1()}], P2 [{P2Items.Describe(P2Inventory.Items)}]");
    }

    private static string MenuState()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var map = Object.FindObjectOfType<MapSelectionUi>();
        string Pads(int player)
        {
            try
            {
                var names = new List<string>();
                var owner = Rewired.ReInput.players.GetPlayer(player).controllers;
                foreach (var j in Rewired.ReInput.controllers.GetJoysticks()) if (j != null && owner.ContainsController(j)) names.Add($"#{j.id} '{j.name}'");
                return names.Count == 0 ? "none" : string.Join(", ", names);
            }
            catch { return "?"; }
        }
        string Safe(Func<string> f) { try { return f(); } catch (Exception e) { return "? (" + e.GetType().Name + ")"; } }
        string where = Safe(() => MyPlayer.Instance != null ? "IN A RUN" : map != null && map.gameObject.activeInHierarchy ? "map select" : PlayerJoin.InCharacterSelect ? (Lobby.Active ? "lobby" : "character select") : Object.FindObjectOfType<MainMenu>() != null ? "main menu" : "?");
        return $"{where}; P2 joined {PlayerJoin.HasP2}; P1's pick {Safe(() => CharacterMenu.selectedCharacter.ToString())}; {Safe(() => P2Select.State)}; " +
               $"game pads: player 0 [{Pads(0)}], player 1 [{Pads(1)}]; UI navigation {(es != null ? es.sendNavigationEvents.ToString() : "-")}, selected '{Safe(() => es?.currentSelectedGameObject?.name)}'";
    }

    private static void GiveAllItems(int pi)
    {
        int n = 0;
        foreach (Assets.Scripts.Inventory__Items__Pickups.Items.EItem e in Enum.GetValues(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.EItem)))
        {
            try
            {
                if (pi == 0) MyPlayer.Instance.inventory.itemInventory.AddItem(e, 1);
                else P2Inventory.AsP2("test all items", items: true, stats: false, () => MyPlayer.Instance.inventory.itemInventory.AddItem(e, 1));
                n++;
            }
            catch (Exception ex) { Report($"allitems: {e} failed: {ex.Message}"); }
        }
        Report($"P{pi + 1} got {n} items");
    }

    // An unopened chest of the map moved 2.5 m in front of the player (its price, and whether the player can pay)
    private static string BringChest(int pi)
    {
        Vector3 at = PlayerPos(pi);
        Assets.Scripts.Inventory__Items__Pickups.Chests.InteractableChest best = null;
        float bestD = float.MaxValue;
        foreach (var c in Object.FindObjectsOfType<Assets.Scripts.Inventory__Items__Pickups.Chests.InteractableChest>())
        {
            if (c == null || !c.isActiveAndEnabled || c.opening) continue;
            if (c.chestType != Assets.Scripts.Inventory__Items__Pickups.Interactables.EChest.Normal) continue;   // paid chests only (a free one cost nothing - gold tests)
            bool can;
            try { can = c.CanInteract(); } catch { can = false; }
            if (!can) continue;
            float d = (c.transform.position - at).sqrMagnitude;
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best == null) return "chest: no unopened chest on the map";
        // On the ground about the player's height, 2.5 m away (bug_stage2: straight down from 20 m up it landed on top of
        // something 11 m above P1 - out of reach); 8 directions tried, from just above the player's head
        var to = at + Vector3.right * 2.5f;
        for (int k = 0; k < 8; k++)
        {
            var c = at + Quaternion.Euler(0f, k * 45f, 0f) * Vector3.right * 2.5f;
            if (Physics.Raycast(c + Vector3.up * 2.5f, Vector3.down, out var h, 8f, ~0, QueryTriggerInteraction.Ignore) && h.point.y > at.y - 3.5f && h.point.y < at.y + 0.5f) { to = h.point; break; }   // (the player's position is ~1.85 m above its feet)
        }
        best.transform.position = to;
        int price = 0;
        try { price = best.GetPrice(); } catch { }
        return $"chest: {best.chestType} '{best.name}' moved from {Mathf.Sqrt(bestD):F0} m to P{pi + 1} at {to}, price {price}";
    }

    private static string ChestWindowState()
    {
        var w = Object.FindObjectOfType<ChestWindowUi>();
        var es = UnityEngine.EventSystems.EventSystem.current;
        bool open = w != null && w.window != null && w.window.gameObject.activeInHierarchy;
        return $"chest window: {(open ? "OPEN" : "closed")}, P2's {P2Interact.ChestIsP2s}, controls to P2 {P2Interact.ChestHandedOver} (waiting for release {P2Interact.ChestWaitingForRelease}), " +
               $"item {(w?.itemData != null ? w.itemData.eItem.ToString() : "-")}, selected '{es?.currentSelectedGameObject?.name}'; items P1 [{P2Items.DescribeP1()}], P2 [{P2Items.Describe(P2Inventory.Items)}]";
    }

    // The game's run-wide damage per source name (RunStats.damageSources: the game-over screen's list)
    private static string DamageSources()
    {
        var d = Assets.Scripts.Saves___Serialization.Progression.Stats.RunStats.damageSources;
        if (d == null || d.Count == 0) return "none";
        var parts = new List<string>();
        foreach (var kv in d) parts.Add($"{kv.Key} {(kv.Value != null ? kv.Value.damage : 0f):F0}");
        return string.Join(", ", parts);
    }

    // Damage dealt to the enemies spawned for each player ('dmg' reports, 'dmg reset' forgets them): HP lost, or the
    // full HP for one that died / went back to the pool
    private static readonly List<(Assets.Scripts.Actors.Enemies.Enemy e, int pi, float maxHp, IntPtr ptr)> spawned = new();
    private static void DamageReport(string label)
    {
        float[] dealt = new float[2];
        int[] dead = new int[2], count = new int[2];
        foreach (var (e, pi, max, _) in spawned)
        {
            count[pi]++;
            bool gone = e == null || !e.gameObject.activeInHierarchy || e.hp <= 0f;
            if (gone) { dead[pi]++; dealt[pi] += max; }
            else dealt[pi] += Mathf.Max(0f, max - e.hp);
        }
        float ratio = dealt[0] > 0.01f ? dealt[1] / dealt[0] : (dealt[1] > 0.01f ? 99f : 1f);
        string verdict = dealt[0] < 1f && dealt[1] < 1f ? "NO DAMAGE" : ratio < 0.5f ? "P2 LOW" : ratio > 2f ? "P2 HIGH" : "OK";
        Report($"dmg {label}: {verdict} - P1 {dealt[0]:F0} ({dead[0]}/{count[0]} dead), P2 {dealt[1]:F0} ({dead[1]}/{count[1]} dead), P2/P1 {ratio:F2}");
    }

    // clearloot: every interactable (chests, shrines, pots...) within 25 m of either player switched off - the revive
    // test was stopped twice by a chest window opening next to the gravestone
    private static void ClearLoot()
    {
        Vector3 a = PlayerPos(0), b = PlayerPos(1);
        int n = 0;
        foreach (var it in Object.FindObjectsOfType<BaseInteractable>())
        {
            if (it == null) continue;
            Vector3 p = it.transform.position;
            // Charge shrines anywhere (user: one blocked a test - they start by standing in them); the mod's own revive
            // zones are copies of them and stay
            bool shrine = it.TryCast<ChargeShrine>() != null && !it.name.StartsWith("MegabonkSplitscreen");
            if (!shrine && (p - a).sqrMagnitude > 25f * 25f && (p - b).sqrMagnitude > 25f * 25f) continue;
            it.gameObject.SetActive(false);
            n++;
        }
        Report($"cleared {n} interactables near the players (and every charge shrine)");
    }

    private static void ClearEnemies()
    {
        var em = Assets.Scripts.Managers.EnemyManager.Instance;
        if (em?.enemies == null) return;
        var all = new List<Assets.Scripts.Actors.Enemies.Enemy>();
        foreach (var kv in em.enemies) if (kv.Value != null) all.Add(kv.Value);
        int n = 0;
        foreach (var e in all) { try { e.ReleaseToPool(); n++; } catch { } }
        spawned.Clear();
        Report($"cleared {n} enemies");
    }

    // mark / delta: each player's whole stat table now, then which stats changed differently for P1 and P2
    private static readonly Dictionary<Assets.Scripts.Menu.Shop.EStat, (float p1, float p2)> marked = new();
    private static (float, float) StatPair(Assets.Scripts.Menu.Shop.EStat s) =>
        (MyPlayer.Instance?.inventory?.playerStats?.GetStat(s) ?? 0f, P2Inventory.Get(s));

    // statdiff [label]: every stat where P1's and P2's values differ (same character = should be none)
    private static void StatDiff(string label)
    {
        var diff = new List<string>();
        int n = 0;
        foreach (Assets.Scripts.Menu.Shop.EStat s in Enum.GetValues(typeof(Assets.Scripts.Menu.Shop.EStat)))
        {
            var (p1, p2) = StatPair(s);
            n++;
            if (Mathf.Abs(p1 - p2) > 0.0005f + 0.001f * Mathf.Abs(p1)) diff.Add($"{s} P1 {p1:0.###} / P2 {p2:0.###}");
        }
        Report($"statdiff {label}: {(diff.Count == 0 ? $"OK ({n} stats equal)" : "MISMATCH " + string.Join(", ", diff))}");
    }

    // passives: P1's and P2's passive ability - the game's class name and its own fields (names logged, not guessed)
    private static void Passives(string label)
    {
        var p1 = MyPlayer.Instance?.inventory?.passiveAbility;
        var p2 = P2Abilities.Passive;
        Report($"passives {label}: P1 {DescribeObject(p1)} | P2 {DescribeObject(p2)}");
    }

    private static string DescribeObject(Il2CppSystem.Object o)
    {
        if (o == null) return "none";
        // The interop wrapper of the object's real class, read through its typed properties (v0.44.5: the game's own
        // reflection gave garbage for number fields)
        var parts = new List<string>();
        string name = o.GetIl2CppType().Name;
        try
        {
            Type[] all;
            try { all = typeof(Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.PassiveAbility).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
            var mt = all.FirstOrDefault(t => t.Name == name && typeof(Il2CppSystem.Object).IsAssignableFrom(t));
            if (mt == null) return $"{name} (no wrapper)";
            var w = Activator.CreateInstance(mt, o.Pointer);
            for (var t = mt; t != null && t != typeof(Il2CppSystem.Object); t = t.BaseType)
                foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    var pt = p.PropertyType;
                    if (!(pt.IsPrimitive || pt.IsEnum) || p.GetIndexParameters().Length > 0) continue;
                    string v;
                    try { var x = p.GetValue(w); v = x is float f ? f.ToString("0.###") : x?.ToString() ?? "null"; } catch { v = "?"; }
                    parts.Add($"{p.Name}={v}");
                }
            return $"{name} ({string.Join(", ", parts)})";
        }
        catch (Exception e) { return $"{name} (unreadable: {e.Message})"; }
    }

    private static void MarkStats()
    {
        marked.Clear();
        foreach (Assets.Scripts.Menu.Shop.EStat s in Enum.GetValues(typeof(Assets.Scripts.Menu.Shop.EStat))) marked[s] = StatPair(s);
    }

    private static void StatDelta(string label)
    {
        var same = new List<string>();
        var diff = new List<string>();
        foreach (var kv in marked)
        {
            var (n1, n2) = StatPair(kv.Key);
            float d1 = n1 - kv.Value.p1, d2 = n2 - kv.Value.p2;
            if (Mathf.Abs(d1) < 0.0001f && Mathf.Abs(d2) < 0.0001f) continue;
            if (Mathf.Abs(d1 - d2) < 0.0005f + 0.001f * Mathf.Abs(d1)) same.Add($"{kv.Key} {d1:+0.###;-0.###}");
            else diff.Add($"{kv.Key} P1 {d1:+0.###;-0.###} / P2 {d2:+0.###;-0.###}");
        }
        Report($"delta {label}: {(diff.Count == 0 ? "OK" : "MISMATCH")} - same change: {(same.Count == 0 ? "none" : string.Join(", ", same))}{(diff.Count > 0 ? " | DIFFERENT: " + string.Join(", ", diff) : "")}");
    }

    // wcompare <EWeapon>: the game's weapon numbers for P1's and P2's copy (P2's run in P2's stat context)
    private static void WeaponCompare(string[] a)
    {
        if (a.Length < 2 || !Enum.TryParse(a[1], true, out EWeapon w)) { Report("wcompare: usage 'wcompare <EWeapon>'"); return; }
        var w1 = MyPlayer.Instance?.inventory?.weaponInventory?.weapons;
        var w2 = P2Inventory.Weapons?.weapons;
        if (w1 == null || w2 == null || !w1.ContainsKey(w) || !w2.ContainsKey(w)) { Report($"wcompare {w}: not held by both"); return; }
        WeaponBase b1 = w1[w], b2 = w2[w];
        var checks = new (string name, Func<WeaponBase, float> f)[]
        {
            ("damage", WeaponUtility.GetDamage), ("size", WeaponUtility.GetAttackSizeMultiplier), ("quantity", b => WeaponUtility.GetAttackQuantity(b)),
            ("bounces", b => WeaponUtility.GetProjectileBounces(b)), ("speed", WeaponUtility.GetProjectileSpeed), ("duration", WeaponUtility.GetDuration),
            ("cooldown", WeaponUtility.GetWeaponCooldown), ("crit", WeaponUtility.GetCritChance), ("critDmg", WeaponUtility.GetCritDamageMultiplier),
            ("knockback", WeaponUtility.GetKnockback), ("range", WeaponUtility.GetWeaponRange),
        };
        var diff = new List<string>();
        var vals = new List<string>();
        foreach (var (name, f) in checks)
        {
            float v1, v2;
            try { v1 = f(b1); v2 = f(b2); } catch (Exception e) { diff.Add($"{name} error {e.Message}"); continue; }
            vals.Add($"{name} {v1:0.###}");
            if (Mathf.Abs(v1 - v2) > 0.001f + 0.001f * Mathf.Abs(v1)) diff.Add($"{name} P1 {v1:0.###} / P2 {v2:0.###}");
        }
        if (!P2Weapons.IsFiring(w)) diff.Add("P2 NOT FIRING (not in P2's firing list)");
        Report($"wcompare {w}: {(diff.Count == 0 ? "OK" : "MISMATCH")} (levels P1 {b1.level} / P2 {b2.level}) -{string.Join(", ", vals)}{(diff.Count > 0 ? " | DIFFERENT: " + string.Join(", ", diff) : "")}");
    }

    // findassets <word> ...: every loaded GameObject (scene or prefab), mesh, texture and material whose name contains
    // one of the words - names logged, not guessed (death piece 2: is there a gravestone in the game?)
    private static void FindAssets(string[] words)
    {
        bool Match(string n) => n != null && words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        var found = new Dictionary<string, SortedSet<string>>();
        void Add(string kind, string what) { if (!found.TryGetValue(kind, out var s)) found[kind] = s = new SortedSet<string>(); if (s.Count < 60) s.Add(what); }
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            if (go != null && Match(go.name))
                Add("GameObject", $"{go.name} ({(go.scene.IsValid() ? "in scene" + (go.activeInHierarchy ? "" : ", inactive") : "prefab/asset")}, {go.GetComponentsInChildren<MeshRenderer>(true).Length} mesh renderers)");
        foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>()) if (m != null && Match(m.name)) Add("Mesh", $"{m.name} ({m.vertexCount} verts)");
        foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>()) if (t != null && Match(t.name)) Add("Texture", $"{t.name} {t.width}x{t.height}");
        foreach (var mt in Resources.FindObjectsOfTypeAll<Material>()) if (mt != null && Match(mt.name)) Add("Material", mt.name);
        Report($"findassets [{string.Join(", ", words)}]: " + (found.Count == 0 ? "nothing" : string.Join(" || ", found.Select(kv => $"{kv.Key} x{kv.Value.Count}: {string.Join("; ", kv.Value)}"))));
    }

    // describe <prefab name>: a loaded prefab's (or else scene object's) parts - components, active, renderers with their
    // materials and shader property names (death pieces 5/6: the charge shrine's progress, the spawn portal)
    // fxscan [effect names]: v0.49 sweep - effects of P2's Holy Book, Mirror, Toxic Barrel, Electric Plug, Power Gloves
    // showed on P1 although nothing new was attached to P1 during P2's runs: who plays them (the items' static events
    // and their listeners) and where the effect objects live under P1 (names logged, not guessed)
    private static void FxScan(string[] names)
    {
        var asm = typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase).Assembly;
        Type[] types;
        try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        var lines = new List<string>();
        foreach (var t in types.Where(t => typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase).IsAssignableFrom(t)))
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType)) continue;
                Il2CppSystem.Delegate d = null;
                try { d = p.GetValue(null) as Il2CppSystem.Delegate; } catch { }
                var who = d == null ? new List<string> { "no listeners" } : d.GetInvocationList().Select(l =>
                {
                    string m = "?", on = "";
                    try { m = $"{l.Method?.DeclaringType?.Name}.{l.Method?.Name}"; } catch { }
                    try { var c = l.Target?.TryCast<Component>(); if (c != null) on = $" on '{c.gameObject.name}' (parent '{c.transform.parent?.name}')"; } catch { }
                    return m + on;
                }).ToList();
                lines.Add($"{t.Name}.{p.Name}: {string.Join(", ", who)}");
            }
        Report("fxscan item events: " + string.Join("; ", lines));
        var p1 = MyPlayer.Instance;
        if (p1 == null || names.Length == 0) return;
        foreach (var n in names)
            foreach (var tr in p1.GetComponentsInChildren<Transform>(true).Where(x => x != null && x.name.Replace("(Clone)", "").Trim() == n).Take(2))
            {
                var path = new List<string>();
                for (var x = tr; x != null && x != p1.transform; x = x.parent) path.Insert(0, $"{x.name}[{string.Join(",", x.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(c => c != "Transform"))}]");
                Report($"fxscan '{n}' under P1: {string.Join(" / ", path)} (active {tr.gameObject.activeSelf})");
            }
    }

    // objsnap / objdiff (user: some item effects don't follow P2 - P2's Ghost item's ghosts flew around P1): which game
    // objects appeared, their class and the fields that point at P1 (names logged, not guessed)
    private static readonly HashSet<IntPtr> objSeen = new();
    private static void ObjSnap()
    {
        objSeen.Clear();
        foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>()) if (mb != null) objSeen.Add(mb.Pointer);
        Report($"objsnap: {objSeen.Count} objects");
    }

    private static void ObjDiff()
    {
        var p1 = MyPlayer.Instance;
        var p2 = P2Movement.Pm;
        var p1Ptrs = new HashSet<IntPtr>();
        if (p1 != null) { p1Ptrs.Add(p1.Pointer); p1Ptrs.Add(p1.transform.Pointer); p1Ptrs.Add(p1.gameObject.Pointer); }
        var p1Pm = PlayerMovement.Instance;
        if (p1Pm != null) { p1Ptrs.Add(p1Pm.Pointer); p1Ptrs.Add(p1Pm.transform.Pointer); }
        var model = p1?.playerRenderer?.rendererObject?.transform;
        if (model != null) p1Ptrs.Add(model.Pointer);
        var lines = new List<string>();
        foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null || objSeen.Contains(mb.Pointer)) continue;
            var it = mb.GetIl2CppType();
            if (it.Assembly.GetName().Name != "Assembly-CSharp") continue;
            Vector3 at = mb.transform.position;
            string near = $"{(p1 != null ? Vector3.Distance(at, p1.transform.position) : -1f):F1} m from P1, {(p2 != null ? Vector3.Distance(at, p2.transform.position) : -1f):F1} m from P2";
            var refs = new List<string>();
            var managed = Type.GetType(it.FullName + ", Assembly-CSharp") ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "Assembly-CSharp")?.GetType(it.FullName);
            if (managed != null)
            {
                object typed = null;
                try { typed = Activator.CreateInstance(managed, mb.Pointer); } catch { }
                for (var t = managed; typed != null && t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
                        object v;
                        try { v = p.GetValue(typed); } catch { continue; }
                        if (v is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase o && p1Ptrs.Contains(o.Pointer)) refs.Add($"{t.Name}.{p.Name} = P1's {p.PropertyType.Name}");
                    }
            }
            lines.Add($"'{mb.name}' {it.Name} ({near}){(refs.Count > 0 ? " -> " + string.Join(", ", refs) : "")}");
        }
        Report($"objdiff: {lines.Count} new game objects: {string.Join("; ", lines.Take(40))}");
    }

    private static void Describe(string name)
    {
        GameObject go = null;
        foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>())
            if (g != null && g.name == name) { go = g; if (!g.scene.IsValid()) break; }
        if (go == null) { Report($"describe {name}: not found"); return; }
        var sb = new System.Text.StringBuilder();
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
        {
            var comps = t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform");
            sb.Append($"\n   '{t.name}' (parent '{t.parent?.name}', active {t.gameObject.activeSelf}, layer {LayerMask.LayerToName(t.gameObject.layer)}, local pos {t.localPosition}, rot {t.localEulerAngles}, scale {t.localScale}) [{string.Join(", ", comps)}]");
            var r = t.GetComponent<Renderer>();
            if (r != null)
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var props = new List<string>();
                    for (int i = 0; m.shader != null && i < m.shader.GetPropertyCount(); i++) props.Add($"{m.shader.GetPropertyName(i)}:{m.shader.GetPropertyType(i)}");
                    sb.Append($"\n      material '{m.name}' shader '{m.shader?.name}' [{string.Join(", ", props)}]");
                }
            var img = t.GetComponent<UnityEngine.UI.Image>();
            if (img != null) sb.Append($"\n      Image sprite '{img.sprite?.name}', type {img.type}, fill {img.fillMethod} {img.fillAmount:F2}, colour {img.color}");
        }
        var cs = go.GetComponent<ChargeShrine>();
        if (cs != null)
            sb.Append($"\n   ChargeShrine: chargeTime {cs.chargeTime}, zoneRenderer '{cs.zoneRenderer?.name}', meshRenderer '{cs.meshRenderer?.name}', runeStone '{cs.runeStone?.name}', zoneColor {cs.zoneColor}, startColor {cs.startColor}, " +
                      $"circleProgress '{cs.circleProgress?.name}', circleParent '{cs.circleParent?.name}', finishParticles '{cs.finishParticles?.name}', altarIcon '{cs.altarIcon?.name}', healingZone '{cs.healingZone?.name}', minimapIcon '{cs.minimapIcon?.name}', audio start/loop/complete '{cs.audioStart?.name}'/'{cs.audioLoop?.name}'/'{cs.audioComplete?.name}'");
        var sp = go.GetComponent<SpawnPlayerPortal>();
        if (sp != null)
            sb.Append($"\n   SpawnPlayerPortal: portalRender '{sp.portalRender?.name}', portalScale {sp.portalScale}, passingFx '{sp.passingFx?.name}', openTime {sp.openTime}, moveTime {sp.moveTime}, audio open/loop/close/passing '{sp.audioOpen?.name}'/'{sp.audioLoop?.name}'/'{sp.audioClose?.name}'/'{sp.audioPassing?.name}'");
        Report($"describe {name} ({(go.scene.IsValid() ? "scene object" : "prefab")}):{sb}");
    }

    private static void DownState()
    {
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        var ds = Object.FindObjectOfType<DeathScreen>(true);
        string screen = ds == null ? "none" : $"active {ds.isActiveAndEnabled}, dead window {(ds.deadUiWindow != null ? ds.deadUiWindow.activeInHierarchy.ToString() : "?")}, alpha {(ds.canvasGroup != null ? ds.canvasGroup.alpha.ToString("F2") : "?")}";
        var model = MyPlayer.Instance?.playerRenderer?.rendererObject;
        Report($"down state: P1 down {Downed.P1Down} (hp {ph?.hp}/{ph?.maxHp}, IsDead {ph?.IsDead()}, model shown {model?.gameObject.activeSelf}, pos {MyPlayer.Instance?.transform.position}); " +
               $"P2 down {P2Health.IsDead} ({P2Health.Hp}/{P2Health.MaxHp}); time scale {Time.timeScale:F2}; death screen: {screen}");
    }

    private static void Hurt(int pi, int amount)
    {
        if (pi == 1) { P2Health.TakeDamage(amount, "autotest"); Report($"P2 hurt by {amount}: {P2Health.Hp}/{P2Health.MaxHp}"); return; }
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        if (ph == null) return;
        // v0.49 (item sweep): the game's own damage call, as P2's hurt uses - damage-taken items (Cactus, Toxic Barrel,
        // Mirror...) hear it the same way for both players
        try { ph.DamagePlayerExternal(amount, 0f, Vector3.zero, false, "autotest", default(Assets.Scripts.Actors.DcFlags), default(Assets.Scripts.Game.Combat.EDamageEffect), null); }
        catch (Exception e) { ph.hp = Mathf.Max(1, ph.hp - amount); Report($"P1 hurt: the game's damage call failed ({e.Message}) - HP lowered"); }
        Report($"P1 hurt by {amount}: {ph.hp}/{ph.maxHp}");
    }

    // spawn <p> <EEnemy> [count] [distance]: the game's EnemyManager.SpawnEnemy around that player
    // TEMP 'animwatch <secs>': at the end of each frame, each animator's state, time, transition and layers, and the
    // model's facing (P2's slide state showed in the log but not in the screenshot)
    private static float animWatchUntil = -1f;
    public static void AnimWatchLate()
    {
        if (Time.time > animWatchUntil) return;
        string D(Animator an, Transform model)
        {
            if (an == null) return "-";
            try
            {
                var st = an.GetCurrentAnimatorStateInfo(0);
                string s = $"state {st.shortNameHash} t {st.normalizedTime:F2}, in transition {an.IsInTransition(0)}";
                if (an.IsInTransition(0)) s += $" to {an.GetNextAnimatorStateInfo(0).shortNameHash}";
                s += $", layers {an.layerCount}, speed {an.speed:F1}, enabled {an.enabled}, ctrl '{an.runtimeAnimatorController?.name}', model yaw {(model != null ? model.eulerAngles.y : -1f):F0}";
                return s;
            }
            catch (Exception e) { return "error " + e.Message; }
        }
        var pr = MyPlayer.Instance?.playerRenderer;
        ModLog.Info($"[animwatch] f{Time.frameCount} P1 {D(pr?.animator, pr?.rendererObject?.transform)}; P2 {D(P2Body.Anim, P2Body.Body?.transform)}");
    }

    // the kind of a special attack (its prefab's attack component), e.g. EnemySpecialAttackTargetLaser
    private static string AttackKind(Assets.Scripts.Game.Combat.EnemySpecialAttacks.EnemySpecialAttack sa)
    {
        try { return sa?.attackPrefab?.GetComponent<Assets.Scripts.Game.Combat.EnemySpecialAttacks.Implementations.EnemySpecialAttackPrefab>()?.GetIl2CppType().Name ?? "-"; }
        catch { return "?"; }
    }

    private static void Spawn(int pi, string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out Actors.Enemies.EEnemy e)) { Report("spawn: usage 'spawn <p> <EEnemy> [count] [distance]'"); return; }
        int n = (int)Float(a, 3, 1f);
        float dist = Float(a, 4, 6f);
        var data = DataManager.Instance?.GetEnemyData(e);
        var em = Assets.Scripts.Managers.EnemyManager.Instance;
        if (data == null || em == null) { Report($"spawn: no data for {e}"); return; }
        Vector3 at = PlayerPos(pi);
        int made = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 pos = at + Quaternion.Euler(0f, i * 360f / n, 0f) * Vector3.forward * dist;
            var en = em.SpawnEnemy(data, pos, 0, true, Assets.Scripts.Actors.Enemies.EEnemyFlag.None, false, 1f);
            if (en != null) { made++; spawned.Add((en, pi, en.maxHp, en.Pointer)); }
        }
        Report($"spawned {made}/{n} {e} {dist:F0} m around P{pi + 1}");
    }

    // spawnside <p> <EEnemy> <count> <distance>: an arc (+-40 deg) on that player's side away from the other player -
    // the players' report "P2 shoots where P1 aims": each player gets enemies only behind them
    private static void SpawnSide(int pi, string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out Actors.Enemies.EEnemy e)) { Report("spawnside: usage 'spawnside <p> <EEnemy> [count] [distance]'"); return; }
        int n = (int)Float(a, 3, 4f);
        float dist = Float(a, 4, 8f);
        var data = DataManager.Instance?.GetEnemyData(e);
        var em = Assets.Scripts.Managers.EnemyManager.Instance;
        if (data == null || em == null) { Report($"spawnside: no data for {e}"); return; }
        Vector3 at = PlayerPos(pi), other = PlayerPos(1 - pi);
        Vector3 away = at - other;
        away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
        // optional 5th value: turn the arc by that many degrees (90 = beside the player, 180 = on the other player's side)
        away = Quaternion.Euler(0f, Float(a, 5, 0f), 0f) * away;
        int made = 0;
        for (int i = 0; i < n; i++)
        {
            float ang = n > 1 ? -40f + 80f * i / (n - 1) : 0f;
            Vector3 pos = at + Quaternion.Euler(0f, ang, 0f) * away * dist;
            if (Physics.Raycast(pos + Vector3.up * 20f, Vector3.down, out var hit, 60f, 1 << 8)) pos = hit.point + Vector3.up * 0.5f;
            var en = em.SpawnEnemy(data, pos, 0, true, Assets.Scripts.Actors.Enemies.EEnemyFlag.None, false, 1f);
            if (en != null) { made++; spawned.Add((en, pi, en.maxHp, en.Pointer)); }
        }
        Report($"spawned {made}/{n} {e} {dist:F0} m behind P{pi + 1} (away from P{2 - pi})");
    }

    // stat <p> <EStat>: that player's stat value (P2's from P2's own table)
    private static void Stat(int pi, string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out Assets.Scripts.Menu.Shop.EStat s)) { Report("stat: usage 'stat <p> <EStat>'"); return; }
        float v = pi == 1 ? P2Inventory.Get(s) : (MyPlayer.Instance?.inventory?.playerStats?.GetStat(s) ?? 0f);
        Report($"stat P{pi + 1} {s} = {v:F3}");
    }

    public static bool P2Held(string[] names)
    {
        if (names.Contains("a") && Time.unscaledTime < pads[1].jumpUntil) return true;
        foreach (var kv in p2Buttons) if (Time.unscaledTime < kv.Value && names.Contains(kv.Key)) return true;
        return false;
    }

    // p2press <button> [seconds]: P2's pad button held (menus: a, b, start, dpad up/down/left/right) - answers P2's
    // PadHeld and the menus' "first button down" read, the same names the pad reports
    private static readonly Dictionary<string, float> p2Buttons = new();
    public static string P2FirstButton()
    {
        foreach (var kv in p2Buttons) if (Time.unscaledTime < kv.Value) return kv.Key;
        return null;
    }

    // After the game's own input read (Ticker.LateUpdate and FixedUpdate): P1's input values from the script
    public static void ApplyP1()
    {
        if (!Active || phase != "script") return;
        var input = MyPlayer.Instance?.playerInput;
        if (input == null) return;
        Vector2 s = Stick(0);
        input.moveHorizontal = s.x;
        input.moveVertical = s.y;
        bool jump = Time.unscaledTime < pads[0].jumpUntil;
        input.jumping = jump;
        input.holdingJump = jump;
        input.sliding = Time.unscaledTime < p1SlideUntil;   // "p1slide <secs>" (1.0 alpha: slide test)
    }
    private static float p1SlideUntil = -1f;

    // ---------- Helpers ----------

    // The nearest wall around the player (a near-vertical surface within 40 m, not an enemy or a player)
    private static Vector3 FindWall(int pi)
    {
        Vector3 from = PlayerPos(pi) + Vector3.up * 1.5f;
        float best = float.MaxValue;
        Vector3 bestDir = Vector3.forward;
        string what = "none";
        for (int i = 0; i < 24; i++)
        {
            Vector3 d = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
            if (!Physics.Raycast(from, d, out RaycastHit hit, 40f, ~0, QueryTriggerInteraction.Ignore)) continue;
            if (Mathf.Abs(hit.normal.y) > 0.3f) continue;
            var col = hit.collider;
            if (col == null || col.GetComponentInParent<Assets.Scripts.Actors.Enemies.Enemy>() != null) continue;
            if (col.GetComponentInParent<PlayerMovement>() != null) continue;
            if (hit.distance < best) { best = hit.distance; bestDir = d; what = $"'{col.name}' {hit.distance:F1} m away, normal {hit.normal}"; }
        }
        Report($"P{pi + 1} nearest wall: {what}");
        return bestDir;
    }

    // 'open' (user: walls pushed the cameras in, the screenshots were hard to read): both players onto open ground -
    // P1 to the nearest spot with nothing within 12 m around it (ground found by a ray down), P2 30 m from there in a
    // direction that is just as clear
    private static void OpenGround()
    {
        var p1 = MyPlayer.Instance;
        var pm2 = P2Movement.Pm;
        if (p1 == null || pm2 == null) return;
        Vector3 start = p1.transform.position;
        Vector3? best = null;
        for (float r = 0f; r <= 120f && best == null; r += 10f)
            for (int k = 0; k < (r == 0f ? 1 : 16) && best == null; k++)
            {
                Vector3 c = start + Quaternion.Euler(0f, k * 22.5f, 0f) * Vector3.forward * r;
                if (Clear(c, out Vector3 ground)) best = ground;
            }
        if (best == null) { Report("open: no open ground found within 120 m"); return; }
        Vector3 a = best.Value;
        Vector3? b = null;
        for (int k = 0; k < 16 && b == null; k++)
        {
            Vector3 c = a + Quaternion.Euler(0f, k * 22.5f, 0f) * Vector3.forward * 30f;
            if (Clear(c, out Vector3 ground) && !Physics.Linecast(a + Vector3.up * 2f, ground + Vector3.up * 2f, ~0, QueryTriggerInteraction.Ignore)) b = ground;
        }
        // No clear spot for P2: next to P1 on P1's ground (v0.44.19: the old fallback, 30 m to the side at P1's height,
        // could be inside a hill)
        Vector3 p2At = b ?? a + Vector3.right * 4f;
        Teleport(p1.playerMovement, a);
        Teleport(pm2, p2At);
        Report($"open ground: P1 at {a}, P2 at {p2At}{(b == null ? " (no clear spot for P2: next to P1)" : "")}");
    }

    private static bool Clear(Vector3 at, out Vector3 ground)
    {
        ground = at;
        // v0.44.19 (user: P2's monkey got stuck under the ground; log: P2 placed at y 0.00): from only 60 m above P1 the
        // ray started inside higher terrain and hit a floor below it - cast from far above now
        if (!Physics.Raycast(at + Vector3.up * 400f, Vector3.down, out RaycastHit down, 800f, ~0, QueryTriggerInteraction.Ignore)) return false;
        if (down.normal.y < 0.85f) return false;   // not flat
        ground = down.point;
        Vector3 from = ground + Vector3.up * 1.5f;
        for (int i = 0; i < 12; i++)
        {
            Vector3 d = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;
            if (Physics.Raycast(from, d, 12f, ~0, QueryTriggerInteraction.Ignore)) return false;
        }
        return !Physics.Raycast(from, Vector3.up, 8f, ~0, QueryTriggerInteraction.Ignore);   // nothing overhead (trees)
    }

    private static void Teleport(PlayerMovement pm, Vector3 at)
    {
        if (pm == null) return;
        Vector3 to = at + Vector3.up * 1.5f;
        pm.transform.position = to;
        if (pm.rb != null) { pm.rb.position = to; pm.rb.velocity = Vector3.zero; }
    }

    // pickup <p> <EPickup>: the game's PickupManager.SpawnPickup 1.5 m in front of that player (it gets collected as the
    // player's own would). First time: who listens to the game's pickup event (Pickup.A_PickupTriggered)
    private static bool listenersLogged;
    private static void DropPickup(int pi, string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out EPickup e)) { Report("pickup: usage 'pickup <p> <EPickup>'"); return; }
        var pm = PickupManager.Instance;
        if (pm == null) { Report("pickup: no PickupManager"); return; }
        if (!listenersLogged)
        {
            listenersLogged = true;
            var ev = Pickup.A_PickupTriggered;
            string names = ev == null ? "none" : string.Join(", ", ev.GetInvocationList().Select(l =>
            {
                string m = "?";
                try { m = $"{l.Method?.DeclaringType?.Name}.{l.Method?.Name}"; } catch { }
                string owner = "";
                try { var mb = l.Target?.TryCast<MonoBehaviour>(); if (mb != null) owner = $" on '{mb.name}'"; } catch { }
                return m + owner;
            }));
            Report($"pickup event listeners: {names}");
        }
        Vector3 at = PlayerPos(pi);
        Vector3 fwd = pi == 0 ? (MyPlayer.Instance?.playerMovement?.orientation?.forward ?? Vector3.forward) : (P2Movement.Pm?.orientation?.forward ?? Vector3.forward);
        fwd.y = 0f;
        // pickup <p> <EPickup> <count> <dist>: that many in a ring <dist> m around the player
        if (a.Length > 4)
        {
            int n = (int)Float(a, 3, 1f), made = 0;
            float dist = Float(a, 4, 10f);
            for (int i = 0; i < n; i++)
            {
                Vector3 pos = at + Quaternion.Euler(0f, i * 360f / n, 0f) * Vector3.forward * dist;
                if (Physics.Raycast(pos + Vector3.up * 20f, Vector3.down, out var hit, 60f, 1 << 8)) pos = hit.point;
                if (pm.SpawnPickup(e, pos + Vector3.up * 0.5f, 1, false, 0f) != null) made++;
            }
            Report($"dropped {made}/{n} {e} {dist:F0} m around P{pi + 1}");
            return;
        }
        var p = pm.SpawnPickup(e, at + fwd.normalized * 1.5f + Vector3.up * 0.5f, 1, false, 0f);
        Report($"dropped {e} at P{pi + 1}'s feet ({(p != null ? "ok" : "failed")})");
    }

    // TEMP 'gems': the XP gems lying or flying - nearer which player, flying to whom
    private static void GemReport(string tag)
    {
        var p2 = P2Movement.Active ? P2Movement.BodyPosition() : Vector3.zero;
        var p1 = PlayerPos(0);
        var p2Body = P2Body.Body?.transform;
        int near1To1 = 0, near1To2 = 0, near2To1 = 0, near2To2 = 0, lying1 = 0, lying2 = 0;
        foreach (var p in Object.FindObjectsOfType<Pickup>())
        {
            if (p == null || p.ePickup != EPickup.Xp || !p.gameObject.activeInHierarchy || p.pickedUp) continue;
            bool nearP2 = (p.transform.position - p2).sqrMagnitude < (p.transform.position - p1).sqrMagnitude;
            bool toP2 = p.target != null && p2Body != null && p.target.Pointer == p2Body.Pointer;
            bool flying = p.target != null;
            if (!flying) { if (nearP2) lying2++; else lying1++; continue; }
            if (nearP2) { if (toP2) near2To2++; else near2To1++; }
            else { if (toP2) near1To2++; else near1To1++; }
        }
        Report($"gems {tag}: near P1 - lying {lying1}, to P1 {near1To1}, to P2 {near1To2}; near P2 - lying {lying2}, to P1 {near2To1}, to P2 {near2To2}; P2 xp {P2XpBar.Xp}");
    }

    // ring <p> <EPickup> [dist] [count]: pickups in a circle around the player, out of reach (billboard checks: some
    // stay in view whichever way the camera turns)
    private static void RingPickups(int pi, string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse(a[2], true, out EPickup e)) { Report("ring: usage 'ring <p> <EPickup> [dist] [count]'"); return; }
        var pm = PickupManager.Instance;
        if (pm == null) { Report("ring: no PickupManager"); return; }
        float dist = Float(a, 3, 9f);
        int count = (int)Float(a, 4, 8f);
        Vector3 at = PlayerPos(pi);
        int ok = 0;
        for (int i = 0; i < count; i++)
        {
            Vector3 d = Quaternion.Euler(0f, i * 360f / count, 0f) * Vector3.forward;
            if (pm.SpawnPickup(e, at + d * dist + Vector3.up * 1f, 1, false, 0f) != null) ok++;
        }
        Report($"ring of {ok}/{count} {e} {dist:F0} m around P{pi + 1}");
    }

    // place [m]: P2 that far to P1's right (default 3 m), on the ground there (user: in the item effect screenshots the
    // players stood so close that P2's ghosts looked like P1's - the sweep spreads them out)
    // 'toslope' (user: try sliding downhill): the nearest spot within 150 m of P1 where the ground (layer 8) keeps going
    // down at 12-35 deg for 25 m; P1 at the top, P2 3 m beside it, P1's camera (and P2's) facing downhill
    private static void ToSlope()
    {
        var p1 = MyPlayer.Instance;
        var pm = P2Movement.Pm;
        if (p1 == null || pm == null) { Report("toslope: no players"); return; }
        int groundMask = GameManager.Instance != null ? GameManager.Instance.whatIsGround.value : 1 << 8;   // (layer 8 alone found flat 'slopes' in the Forest)
        bool Ground(Vector3 at, out Vector3 g)
        {
            g = at;
            if (!Physics.Raycast(new Vector3(at.x, at.y + 80f, at.z), Vector3.down, out var hit, 200f, groundMask)) return false;
            g = hit.point;
            return true;
        }
        Vector3 c = p1.transform.position;
        for (float r = 10f; r <= 150f; r += 10f)
            for (int i = 0; i < 24; i++)
            {
                Vector3 at = c + Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward * r;
                if (!Ground(at, out var top)) continue;
                for (int d = 0; d < 8; d++)
                {
                    Vector3 dir = Quaternion.Euler(0f, d * 45f, 0f) * Vector3.forward;
                    Vector3 side = Vector3.Cross(Vector3.up, dir);
                    bool ok = true;
                    float prevY = top.y;
                    for (int s = 1; s <= 5 && ok; s++)
                    {
                        // P1's line and P2's line (3 m to the side) both go down steadily
                        // and open ground on both sides (not a trench): 4 m out on each side at about the same height
                        foreach (var o in new[] { Vector3.zero, side * 3f, side * -4f, side * 7f })
                        {
                            if (!Ground(top + o + dir * (5f * s), out var g)) { ok = false; break; }
                            float ang = Mathf.Atan2(top.y - g.y, 5f * s) * Mathf.Rad2Deg;
                            if (ang < 12f || ang > 35f) { ok = false; break; }
                            if (o == Vector3.zero) { if (g.y > prevY - 0.5f) { ok = false; break; } prevY = g.y; }
                        }
                    }
                    if (!ok) continue;
                    if (!Ground(top + side * 3f, out var top2)) continue;
                    void Put(Transform t, Rigidbody rb, Vector3 p) { p += Vector3.up; t.position = p; if (rb != null) { rb.position = p; rb.velocity = Vector3.zero; } }
                    Put(p1.transform, p1.playerMovement?.rb, top);
                    Put(pm.transform, pm.rb, top2);
                    P2Camera.FaceP1(dir);
                    P2Camera.TurnP2(Mathf.DeltaAngle(P2Camera.Yaw, Quaternion.LookRotation(dir).eulerAngles.y));
                    Report($"toslope: top at {top}, downhill {dir} ({Mathf.Atan2(top.y - prevY, 25f) * Mathf.Rad2Deg:F0} deg over 25 m), P2 3 m beside; P2 camera yaw {P2Camera.Yaw:F0} vs downhill {Quaternion.LookRotation(dir).eulerAngles.y:F0}");
                    return;
                }
            }
        Report("toslope: no downhill found within 150 m");
    }

    private static void PlaceP2NearP1(float dist = 3f)
    {
        var pm = P2Movement.Pm;
        var p1 = MyPlayer.Instance;
        if (pm == null || p1 == null) return;
        Vector3 to = p1.transform.position + p1.transform.right * dist + Vector3.up;
        if (dist > 3.5f && Physics.Raycast(to + Vector3.up * 30f, Vector3.down, out var hit, 80f)) to = hit.point + Vector3.up;
        pm.transform.position = to;
        if (pm.rb != null) { pm.rb.position = to; pm.rb.velocity = Vector3.zero; }
        Report($"P2 placed next to P1 at {to}");
    }

    private static void Snap(string label)
    {
        for (int pi = 0; pi < 2; pi++)
        {
            var pm = pi == 0 ? MyPlayer.Instance?.playerMovement : P2Movement.Pm;
            if (pm == null) continue;
            Vector3 v = pm.rb != null ? pm.rb.velocity : Vector3.zero;
            var anim = pi == 0 ? MyPlayer.Instance?.playerRenderer?.animator : P2Body.Anim;
            var ph1 = MyPlayer.Instance?.inventory?.playerHealth;
            string health = pi == 0
                ? $"hp {ph1?.hp}/{ph1?.maxHp}, shield {ph1?.shield:F0}/{ph1?.maxShield:F0}, gold {MyPlayer.Instance?.inventory?._gold_k__BackingField:F0}"
                : $"hp {P2Health.Hp}/{P2Health.MaxHp}, shield {P2Health.Shield:F0}/{P2Health.MaxShield}, gold {P2Gold.Amount:F0}";
            Report($"snap {label} P{pi + 1}: pos {pm.transform.position}, vel {v}, grounded {pm.grounded}, wallClimbing {pm.wallClimbing}, state {pm.lastMovementState}, " +
                   $"{health}, anim state {(anim != null ? anim.GetCurrentAnimatorStateInfo(0).shortNameHash : 0)}");
        }
    }

    // v0.40.3: ScreenCapture.CaptureScreenshot(path) fails in this interop build (ReadOnlySpan method missing); the
    // frame is grabbed as a texture at the end of the frame (Ticker's end-of-frame coroutine) and written by .NET
    private static string pendingShot;
    private static void Shot(string name) => pendingShot = name;

    public static void EndOfFrame()
    {
        if (pendingShot == null) return;
        string name = pendingShot;
        pendingShot = null;
        string path = Path.Combine(dir, $"autotest_{name}.png");
        try
        {
            // v0.40.4: ImageConversion.EncodeToPNG crashed the game (dump: EncodeToPNG_Injected) - the pixels are
            // read as plain colours and the PNG is written by the mod
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            Object.Destroy(tex);
            File.WriteAllBytes(path, Png(w, h, px));
            Report($"screenshot {path} ({w}x{h})");
        }
        catch (Exception e) { Report($"screenshot failed: {e.Message}"); }
    }

    // A minimal RGB PNG (rows top to bottom; Unity's pixels start at the bottom)
    private static byte[] Png(int w, int h, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32> px)
    {
        var raw = new byte[h * (w * 3 + 1)];
        int o = 0;
        for (int y = h - 1; y >= 0; y--)
        {
            raw[o++] = 0;   // no filter
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                var c = px[row + x];
                raw[o++] = c.r; raw[o++] = c.g; raw[o++] = c.b;
            }
        }
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 2;   // 8 bits, RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void BE(byte[] b, int i, int v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BE(len, 0, data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(td, 0);
        data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4];
        BE(crc, 0, (int)Crc(td));
        s.Write(crc);
    }

    private static uint[] crcTable;
    private static uint Crc(byte[] d)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                crcTable[n] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        foreach (var b in d) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void Quit()
    {
        Active = false;
        Report("quitting the game");
        Application.Quit();
    }
}
