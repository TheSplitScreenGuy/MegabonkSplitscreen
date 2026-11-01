using HarmonyLib;
using Rewired;
using UnityEngine;

namespace MegabonkSplitscreen;

// Player 2 joining: while the character select screen (CharacterMenu) is open, watch every
// gamepad that isn't player 1's. Pressing Start on one registers it as player 2 and takes it away
// from the game's single Rewired player, so it no longer drives player 1's menus.
//
// "Player 1's controller" is whatever the game's input manager was using when character select
// opened (P1 just clicked Play with it). Captured once, because while every pad belongs to the same
// Rewired player, P2's button presses would otherwise make P2's pad look like the active one.
internal static class PlayerJoin
{
    public static bool InCharacterSelect { get; private set; }
    public static Joystick P2Joystick { get; private set; }
    // Autotest with no pad at all (user: the pads go idle while away): P2 exists without one - its input comes from
    // the test script, and every input read asks the script first
    public static bool VirtualP2 { get; private set; }
    public static bool HasP2 => P2Joystick != null || VirtualP2;

    public static bool JoinVirtual()
    {
        if (!InCharacterSelect || HasP2) return false;
        VirtualP2 = true;
        ModLog.Info("PLAYER 2 JOINED without a pad (autotest: script input only)");
        CharacterSelectView.OnP2Joined();
        return true;
    }

    private static ControllerType p1Type;
    private static int p1Id = -1;

    // Start is raw button 7 on XInput pads; the name check covers other pad types.
    private const int RawStartIndex = 7;
    private static readonly string[] StartNames = { "start", "menu", "options", "+", "plus" };

    // Which button index each pad was holding last frame (-1 = none), for detecting new presses.
    private static readonly Dictionary<int, int> prevHeldIndex = new();

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CharacterMenu), nameof(CharacterMenu.OnEnable)),
            postfix: new HarmonyMethod(typeof(PlayerJoin), nameof(OnMenuOpened)));
        harmony.Patch(AccessTools.Method(typeof(CharacterMenu), nameof(CharacterMenu.OnDisable)),
            postfix: new HarmonyMethod(typeof(PlayerJoin), nameof(OnMenuClosed)));
        ModLog.Info("PlayerJoin: watching character select");
        ListenForNewRuns();
    }

    private static void OnMenuOpened(CharacterMenu __instance)
    {
        UiDump.DumpCharacterSelect(__instance);
        InCharacterSelect = true;
        prevHeldIndex.Clear();
        lastJoinState = null;
        // P1 = the pad that pressed Play: the last pad with a button press just before character select opened.
        // (Before, the game's "current controller" was used; if the other pad had touched the menus, that one became
        // P1 and the pad that pressed Play had to join as P2 - user.) Fallback: the game's current controller.
        Controller c = null;
        // 10 s: the camera flies to character select first (3 s was too short in v0.22.4 -> fallback used)
        if (lastPressedJoy != null && Time.unscaledTime - lastPressedAt < 10f) c = lastPressedJoy;
        ModLog.Info($"Last pad pressed outside character select: {Describe(lastPressedJoy)} {(lastPressedJoy != null ? $"{Time.unscaledTime - lastPressedAt:F1} s ago" : "")}");
        // 1.0.1 (player request; user: a toggle on the players screen): P1 set to keyboard + mouse -> every pad is free to
        // join as P2 (none of them is P1's)
        if (P1KeyboardMouse)
        {
            SetP1KeyboardMouse("the players screen's P1 input setting");
            LogRewiredState();
            PadBindings.LogPadsOnce();
            CharacterSelectView.OnMenuOpened();
            P2Select.OnCharacterSelectOpened();
            Lobby.OnCharacterSelectOpened(__instance);
            return;
        }
        // No pad seen pressing Play (user: first visit after launch, the Steam Controller's Play press wasn't seen and
        // the game's current controller was the Xbox pad, so the Xbox pad became P1): P1 stays undecided until a pad
        // presses something here (see DecideP1)
        p1Known = c != null;
        p1Type = c != null ? c.type : ControllerType.Keyboard;
        p1Id = c != null ? c.id : -1;
        if (p1Known) ModLog.Info($"Character select opened. Player 1 is using: {Describe(c)} (pressed Play)");
        else ModLog.Info($"Character select opened. Player 1 not known yet (no pad seen pressing Play; the game's current controller is {Describe(MyInputManager.currentController)}) - the first pad to press a button decides");
        LogRewiredState();
        PadBindings.LogPadsOnce();
        CharacterSelectView.OnMenuOpened();
        P2Select.OnCharacterSelectOpened();
        Lobby.OnCharacterSelectOpened(__instance);
    }

    // Outside character select (before P2 joined): remember which pad pressed a button last
    private static Joystick lastPressedJoy;
    private static float lastPressedAt;
    private static readonly Dictionary<int, int> menuPrevIndex = new();
    private static int menuPressLogs;

    // ---------- P1's input: controller or keyboard + mouse (players screen toggle, saved in the config) ----------
    private static BepInEx.Configuration.ConfigEntry<bool> p1KbmSetting;
    public static void Configure(BepInEx.Configuration.ConfigFile config)
    {
        p1KbmSetting = config.Bind("Players", "Player1KeyboardMouse", false,
            "Player 1 plays with keyboard and mouse (every controller is then free for player 2). Also switched on the players screen.");
    }

    public static bool P1KeyboardMouse => p1KbmSetting != null && p1KbmSetting.Value && !Autotest.Active;

    // The players screen's toggle
    public static void SetP1Input(bool keyboardMouse)
    {
        if (p1KbmSetting == null) return;
        p1KbmSetting.Value = keyboardMouse;   // saved by BepInEx
        if (keyboardMouse) SetP1KeyboardMouse("switched on the players screen");
        else
        {
            // Back to a controller: the pads that aren't P2's go back to the game's player; P1 = the next pad to press
            // something (if P2 hasn't joined yet)
            GivePadsToP1();
            p1Known = false;
            ModLog.Info("Players screen: Player 1 uses a controller again (the first pad to press a button, if P2 hasn't joined)");
        }
    }

    private static void SetP1KeyboardMouse(string why)
    {
        p1Known = true;
        p1Type = ControllerType.Keyboard;
        p1Id = -1;   // no joystick matches: every pad can join as P2
        int n = TakePadsFromP1();
        ModLog.Info($"Player 1 is using keyboard and mouse ({why}); {n} controller(s) taken off the game's player - any controller's Start joins as player 2");
    }

    // Keyboard + mouse: no controller drives the game's player (P1), so P1's menus and character only follow the
    // keyboard and mouse. Returns how many were taken off.
    private static int TakePadsFromP1()
    {
        int n = 0;
        try
        {
            var p0 = ReInput.players.GetPlayer(0);
            foreach (var j in ReInput.controllers.GetJoysticks())
                if (j != null && p0.controllers.ContainsController(j)) { p0.controllers.RemoveController(j); n++; }
        }
        catch (Exception e) { ModLog.Warn($"P1 keyboard+mouse: taking the controllers off failed: {e.Message}"); }
        return n;
    }

    private static void GivePadsToP1()
    {
        try
        {
            var p0 = ReInput.players.GetPlayer(0);
            foreach (var j in ReInput.controllers.GetJoysticks())
                if (j != null && (P2Joystick == null || j.id != P2Joystick.id) && !p0.controllers.ContainsController(j)) p0.controllers.AddController(j, false);
        }
        catch (Exception e) { ModLog.Warn($"P1 controller: giving the controllers back failed: {e.Message}"); }
    }

    // Keyboard + mouse: a controller plugged in later is assigned to the game's player by Rewired - taken off again
    // (checked when the number of controllers changes; P2's level-up/pause menus put P2's pad on the game's player on
    // purpose, so not while one of them has the controls)
    private static int lastJoyCount = -1;
    private static int kbmOnLogs;
    private static void KeepPadsOffP1()
    {
        if (!P1KeyboardMouse || P2LevelUp.ControlsWithP2) { lastJoyCount = -1; return; }
        // User's test: the keyboard did nothing - the game's player had keyboard AND mouse switched off
        // (log: "Controls -> P1: ... keyboard False, mouse False" before P2's level-up even touched them). The game
        // seems to switch them off while it thinks a controller is in use; in keyboard + mouse mode they stay on.
        try
        {
            var c0 = ReInput.players.GetPlayer(0).controllers;
            if (!c0.hasKeyboard || !c0.hasMouse)
            {
                if (kbmOnLogs++ < 10) ModLog.Info($"P1 keyboard + mouse: the game's player had keyboard {c0.hasKeyboard}, mouse {c0.hasMouse} - switched on");
                c0.hasKeyboard = true;
                c0.hasMouse = true;
            }
        }
        catch (Exception e) { if (kbmOnLogs++ < 10) ModLog.Warn($"P1 keyboard + mouse: switching them on failed: {e.Message}"); }
        int count = ReInput.controllers.joystickCount;
        if (count == lastJoyCount) return;
        lastJoyCount = count;
        int n = TakePadsFromP1();
        if (n > 0) ModLog.Info($"Player 1 on keyboard and mouse: {n} newly connected controller(s) taken off the game's player");
    }

    private static void TrackLastPressed()
    {
        foreach (var joy in ReInput.controllers.GetJoysticks())
        {
            if (joy == null) continue;
            var poll = ReInput.controllers.polling.PollControllerForFirstButton(ControllerType.Joystick, joy.id);
            int index = poll.success ? poll.elementIndex : -1;
            if (!menuPrevIndex.TryGetValue(joy.id, out int prev)) prev = -1;
            menuPrevIndex[joy.id] = index;
            if (index >= 0 && index != prev)
            {
                // TEMP [pads]: which presses are seen before character select (one missed Play press made the wrong pad P1)
                if (menuPressLogs++ < 30) ModLog.Info($"[pads] button {index} '{poll.elementIdentifierName}' on {Describe(joy)} (outside character select)");
                lastPressedJoy = joy;
                lastPressedAt = Time.unscaledTime;
            }
        }
    }

    private static void OnMenuClosed()
    {
        InCharacterSelect = false;
        Lobby.OnCharacterSelectClosed();
        ResetRun();
        ModLog.Info("Character select closed");
    }

    // P2's per-run state starts fresh. From character select closing, and from the game's own "new run" event: a
    // restart from the pause/death menu skips character select (user: after P2 died and the run was restarted, P2
    // came out of the portal and vanished - still dead from the last run). Next stages don't fire it (progress stays).
    private static void ResetRun()
    {
        P2Health.ResetForNewRun();
        Downed.Reset();
        Revive.Reset();
        ReviveUi.Reset();
        P2XpBar.ResetForNewRun();
        P2LevelUp.Reset();
        P2Abilities.Reset();
        MapView.Reset();
        P2PowerUps.Reset();
        LevelLabels.Reset();
        CharacterDiag.Reset();   // before P2's stats go (its cleanup runs in P2's context)
        P2Inventory.ResetForNewRun();
        P2Spawns.ResetForNewRun();
        BossBars.Reset();
        P2Explosions.Reset();
        P2Rewards.Reset();
        RewardLayout.Reset();
        P2Gold.ResetForNewRun();
        P2Items.Reset();
        P2ItemsHud.Reset();
        P2ChestPrice.Reset();
        P2ItemFx.Reset();
        P2Interact.Reset();
        P2EndScreen.Reset();
    }

    private static void OnNewRunStarted()
    {
        try
        {
            ResetRun();
            ModLog.Info("New run started (game event): P2's run state reset");
        }
        catch (Exception e) { ModLog.Error($"New run reset failed: {e}"); }
    }

    private static Il2CppSystem.Action gameNewRunListeners;

    // Quitting: the game's own listeners back, without the mod's
    public static void Shutdown()
    {
        try { Assets.Scripts.Managers.MapController.A_NewRunStarted = gameNewRunListeners; }
        catch { }
    }

    private static void ListenForNewRuns()
    {
        try
        {
            Il2CppSystem.Action a = (Action)OnNewRunStarted;
            var current = Assets.Scripts.Managers.MapController.A_NewRunStarted;
            gameNewRunListeners = current;
            Assets.Scripts.Managers.MapController.A_NewRunStarted = current == null
                ? a
                : Il2CppSystem.Delegate.Combine(current, a).Cast<Il2CppSystem.Action>();
            ModLog.Info("PlayerJoin: listening for the game's new-run event");
        }
        catch (Exception e) { ModLog.Error($"PlayerJoin: couldn't listen for new runs: {e}"); }
    }

    // Called every frame by the Ticker component.
    // Uses Rewired's polling system ("which button is held on this pad?"), Rewired's API for
    // press-to-join screens. Reading joy.GetButton(i) / *Down flags directly missed the Xbox pad
    // entirely here (Steam Input setup), while polling saw it and also reports real names ("Start").
    public static void Update()
    {
        if (!InCharacterSelect && P2Joystick == null && ReInput.isReady) TrackLastPressed();
        if (ReInput.isReady) KeepPadsOffP1();
        if (!InCharacterSelect || P2Joystick != null || !ReInput.isReady) return;

        foreach (var joy in ReInput.controllers.GetJoysticks())
        {
            if (joy == null) continue;
            var poll = ReInput.controllers.polling.PollControllerForFirstButton(ControllerType.Joystick, joy.id);
            int index = poll.success ? poll.elementIndex : -1;
            if (!prevHeldIndex.TryGetValue(joy.id, out int prevIndex)) prevIndex = -1;
            prevHeldIndex[joy.id] = index;
            if (index < 0 || index == prevIndex) continue;

            string name = poll.elementIdentifierName ?? "";
            bool start = index == RawStartIndex || IsStart(name);
            if (!p1Known && DecideP1(joy, start)) return;
            bool isP1 = IsPlayer1(joy);
            // TEMP: P1's presses logged too, to show polling works at all
            ModLog.Info($"Button {index} '{name}' pressed on {Describe(joy)}{(isP1 ? " [P1]" : "")}");
            if (!isP1 && start)
            {
                Register(joy);
                return;
            }
        }

        LogJoinState();
    }

    // TEMP: polling sometimes sees nothing for a whole character-select visit. Log a compact state line
    // whenever it changes: per pad the polled button index (-1 = none), GetAnyButton and enabled;
    // the game's own player 0 GetAnyButton; and the game's current controller.
    private static string lastJoinState;
    private static bool lastActiveBroken;

    // ReInput.controllers.GetLastActiveController() may throw through interop like the Player version did.
    private static string LastActiveDescription()
    {
        if (lastActiveBroken) return "n/a";
        try
        {
            var c = ReInput.controllers.GetLastActiveController();
            return c == null ? "none" : $"{c.type}#{c.id}";
        }
        catch (Exception e)
        {
            lastActiveBroken = true;
            ModLog.Warn($"GetLastActiveController unusable: {e.Message}");
            return "n/a";
        }
    }
    private static void LogJoinState()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var joy in ReInput.controllers.GetJoysticks())
        {
            if (joy == null) continue;
            if (!prevHeldIndex.TryGetValue(joy.id, out int idx)) idx = -1;
            sb.Append($"#{joy.id} poll={idx} any={(joy.GetAnyButton() ? 1 : 0)} en={(joy.enabled ? 1 : 0)}; ");
        }
        var cur = MyInputManager.currentController;
        sb.Append($"game p0any={(ReInput.players.GetPlayer(0).GetAnyButton() ? 1 : 0)} cur=#{(cur != null ? cur.id : -1)}");
        // Steam Input theory: unfocused window -> desktop layout -> pads send keyboard/mouse instead
        var kb = ReInput.controllers.Keyboard;
        var mouse = ReInput.controllers.Mouse;
        sb.Append($"; focused={(Application.isFocused ? 1 : 0)} kb={(kb != null && kb.GetAnyButton() ? 1 : 0)} mouse={(mouse != null && mouse.GetAnyButton() ? 1 : 0)}");
        sb.Append($"; lastActive={LastActiveDescription()}");
        string s = sb.ToString();
        if (s == lastJoinState) return;
        lastJoinState = s;
        ModLog.Info($"[state] {s}");
    }

    private static void Register(Joystick joy)
    {
        P2Joystick = joy;
        ModLog.Info($"PLAYER 2 JOINED with {Describe(joy)}");

        // Give the pad to Rewired player 1 if the game defines one; otherwise just take it off player 0.
        if (ReInput.players.playerCount > 1)
        {
            ReInput.players.GetPlayer(1).controllers.AddController(joy, true);
            ModLog.Info("Assigned P2's pad to Rewired player 1");
        }
        else
        {
            ReInput.controllers.RemoveJoystickFromAllPlayers(joy, false);
            ModLog.Info("Game has only one Rewired player; removed P2's pad from it");
        }
        LogRewiredState();
        CharacterSelectView.OnP2Joined();
    }

    // P2 leaves (Lobby: B on P2's pad): the pad goes back to the game's player, P2's avatar and panel go, the camera
    // goes back to P1 alone
    public static void Leave()
    {
        if (!HasP2) return;
        var joy = P2Joystick;
        if (joy != null)
        {
            try
            {
                if (ReInput.players.playerCount > 1) ReInput.players.GetPlayer(1).controllers.RemoveController(joy);
                // P1 on keyboard + mouse: the pad stays free (it would drive P1 otherwise)
                if (!P1KeyboardMouse) ReInput.players.GetPlayer(0).controllers.AddController(joy, false);
            }
            catch (Exception e) { ModLog.Warn($"P2 leave: giving the pad back failed: {e.Message}"); }
        }
        ModLog.Info($"PLAYER 2 LEFT ({Describe(joy)})");
        P2Joystick = null;
        VirtualP2 = false;
        prevHeldIndex.Clear();
        CharacterSelectView.OnP2Left();
        P2Panel.Remove();
        P2Select.Unbind();
    }

    private static bool IsPlayer1(Controller c) => c.type == p1Type && c.id == p1Id;

    private static bool p1Known;

    // P1 not known yet: a pad pressing anything but Start is P1 (it's driving the game's menu). Start = joining: with
    // exactly one other pad, that one is P1 and this one joins as P2; a lone pad pressing Start is P1.
    // Returns true when P2 joined.
    private static bool DecideP1(Joystick joy, bool start)
    {
        Joystick p1 = joy;
        if (start)
        {
            var others = new List<Joystick>();
            foreach (var j in ReInput.controllers.GetJoysticks())
                if (j != null && j.id != joy.id) others.Add(j);
            if (others.Count == 1) p1 = others[0];
        }
        p1Known = true;
        p1Type = p1.type;
        p1Id = p1.id;
        ModLog.Info($"Player 1 decided: {Describe(p1)} ({(p1 == joy ? "pressed a button first" : "the other pad pressed Start to join")})");
        if (p1 == joy) return false;
        Register(joy);
        return true;
    }

    // Autotest: P1 not known -> the game's current controller, like before
    private static void P1FromGame()
    {
        if (p1Known) return;
        var c = MyInputManager.currentController;
        p1Known = true;
        p1Type = c != null ? c.type : ControllerType.Keyboard;
        p1Id = c != null ? c.id : -1;
        ModLog.Info($"Player 1 (autotest join): the game's current controller {Describe(c)}");
    }

    // Autotest: the first pad that isn't P1's joins as P2 (its input then comes from the test script)
    public static bool JoinFirstFreePad(bool anyPad = false)
    {
        if (!InCharacterSelect || P2Joystick != null || !ReInput.isReady) return false;
        P1FromGame();
        foreach (var joy in ReInput.controllers.GetJoysticks())
        {
            if (joy == null || (!anyPad && IsPlayer1(joy))) continue;
            Register(joy);
            return true;
        }
        return false;
    }

    private static bool IsStart(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return StartNames.Any(s => n == s || n.StartsWith(s + " "));
    }

    private static void LogRewiredState()
    {
        if (!ReInput.isReady)
        {
            ModLog.Warn("Rewired not ready");
            return;
        }
        int players = ReInput.players.playerCount;
        ModLog.Info($"Rewired: {players} player(s), {ReInput.controllers.joystickCount} joystick(s)");
        foreach (var joy in ReInput.controllers.GetJoysticks())
        {
            var owners = new List<int>();
            for (int p = 0; p < players; p++)
                if (ReInput.controllers.IsJoystickAssignedToPlayer(joy.id, p)) owners.Add(p);
            ModLog.Info($"  {Describe(joy)} -> Rewired player(s) [{string.Join(", ", owners)}]");
        }
    }

    private static string Describe(Controller c) =>
        c == null ? "nothing" : $"{c.type} #{c.id} '{c.name}' ({c.hardwareName})";
}

// A tiny always-alive component that gives the mod a per-frame Update.
public class Ticker : MonoBehaviour
{
    public Ticker(IntPtr ptr) : base(ptr) { }

    private static readonly HashSet<string> seenErrors = new();

    // Physics step for P2's movement copy (the game's PlayerMovement), like P1's runs in FixedUpdate
    public void FixedUpdate()
    {
        frameWatch.Start();
        try { AnimDiag.FixedSteps++; Autotest.ApplyP1(); Downed.HoldP1(); P2Body.Sub("fixed: movement", P2Movement.FixedTick); P2Body.Sub("fixed: abilities", P2Abilities.FixedTick); P2Body.Sub("fixed: items", P2Items.FixedTick); P2Body.Sub("fixed: health", P2Health.FixedTick); }
        catch (Exception e)
        {
            if (seenErrors.Add(e.Message)) ModLog.Error($"Ticker.FixedUpdate: {e}");
        }
        frameWatch.Stop();
    }


    // The game is quitting: take the mod's callbacks off the game and Unity, stop the watchdog
    public void OnApplicationQuit()
    {
        ModLog.Info("Game quitting: removing the mod's callbacks");
        ModLog.Flush();
        FreezeDiag.Stop();
        Billboards.Shutdown();
        PlayerJoin.Shutdown();
        FallDamage.Shutdown();
    }

    // End of every frame, after drawing: undo the render-only camera override (P2Camera)
    public void Start()
    {
        StartCoroutine(BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions.WrapToIl2Cpp(EndOfFrame()));
    }

    private static System.Collections.IEnumerator EndOfFrame()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            try { Autotest.EndOfFrame(); P2Camera.RestoreAfterRender(); }
            catch (Exception e)
            {
                if (seenErrors.Add(e.Message)) ModLog.Error($"Ticker end of frame: {e}");
            }
        }
    }

    // After the game's own Update (its PlayerCamera places P1's camera there): the Lego-style shared/split view
    public void LateUpdate()
    {
        frameWatch.Start();
        try { P2Body.Sub("late: cameras", P2Camera.LateUpdate); GoldPopups.LateUpdate(); P2Body.Sub("late: billboards", Billboards.LateTick); ReviveUi.LateTick(); RewardLayout.LateUpdate(); Autotest.ApplyP1(); Downed.HoldP1(); if (Autotest.Active) { P2Weapons.ProbeLate(); Autotest.AnimWatchLate(); } }
        catch (Exception e)
        {
            if (seenErrors.Add(e.Message)) ModLog.Error($"Ticker.LateUpdate: {e}");
        }
        PerfEnd();   // end of the mod's frame work (FixedUpdate's time is added to the next frame)
    }

    // TEMP [perf] (user: the game stutters a bit - logging or hooks?): the mod's own per-frame time (Update, LateUpdate,
    // FixedUpdate; not the hooks inside the game's code), average and worst frame over 300 frames
    private static readonly System.Diagnostics.Stopwatch frameWatch = new();
    private static double worstMs, sumMs;
    private static int perfFrames, perfLogs;
    private static float spikeTime;
    private static int recomputesAtLastLog = -1;
    private static void PerfEnd()
    {
        frameWatch.Stop();
        double ms = frameWatch.Elapsed.TotalMilliseconds;
        frameWatch.Reset();
        sumMs += ms;
        if (perfFrames == 0 && recomputesAtLastLog < 0) recomputesAtLastLog = P2Inventory.RecomputeCount;
        if (ms > worstMs) { worstMs = ms; spikeTime = Time.time; }
        if (++perfFrames < 300) return;
        if (perfLogs++ < 8 || Autotest.Active) ModLog.Info($"[perf] mod per frame: {sumMs / perfFrames:F2} ms average, worst {worstMs:F1} ms (at {spikeTime:F1}); game frame {Time.unscaledDeltaTime * 1000f:F1} ms; P2 stat recomputes {P2Inventory.RecomputeCount - recomputesAtLastLog} in {perfFrames} frames");
        recomputesAtLastLog = P2Inventory.RecomputeCount;
        perfFrames = 0; sumMs = 0; worstMs = 0;
    }

    public void Update()
    {
        frameWatch.Start();
        try
        {
            PlayerJoin.Update();
            MenuLabel.Update();
            Lobby.Update();
            CharacterSelectView.Update();
            FreezeDiag.Update();
            P2Select.Update();
            P2Body.Update();
            Autotest.Update();
        }
        catch (Exception e)
        {
            // Keep running; log each distinct error once so one bad call can't flood the log or stop the mod
            if (seenErrors.Add(e.Message)) ModLog.Error($"Ticker: {e}");
        }
        frameWatch.Stop();
    }
}
