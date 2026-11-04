using Assets.Scripts._Data;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.UI.InGame.Levelup;
using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using Rewired;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Player 2's level-ups, using the game's own level-up screen.
// In the game: a level-up queues a Levelup encounter (EncounterWindows.AddEncounter), LevelupScreen.Open shows it
// (the game pauses, so both players stop), UpgradePicker builds offers, and the pick goes to
// PlayerInventory.AddUpgrade. The screen is driven by Rewired player 0, the game's only player.
//  - P2 levels up -> the mod queues a Levelup encounter too, and remembers which queued level-ups are P2's.
//  - P2's screen opens -> P1's joysticks, keyboard and mouse are taken off Rewired player 0 and P2's pad is put on,
//    so only P2 can choose. Swapped back when the screen closes.
//  - The screen runs in P2's inventory context (see SwapInWeapons): offers from P2's weapons/tomes, and the pick
//    (weapon or tome) lands in P2's own inventory.
//  - P2's XP bar shows the game's rainbow effect while P2 has a level-up waiting or open; P1's bar doesn't
//    switch to rainbow for P2's screen.
internal static class P2LevelUp
{
    public static bool P2ScreenOpen { get; private set; }
    public static int Pending { get; private set; }   // P2 level-ups queued, not yet opened

    private static readonly Queue<bool> owners = new();   // queued Levelup encounters, true = P2's
    private static bool queueingForP2;
    private static int addCallsDuringOnLevelUp;

    // Controller swap state
    private static readonly List<Joystick> removedJoysticks = new();
    private static bool hadKeyboard, hadMouse, swapped;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(EncounterWindows), nameof(EncounterWindows.AddEncounter)),
            prefix: new HarmonyMethod(typeof(P2LevelUp), nameof(BeforeAddEncounter)));
        harmony.Patch(AccessTools.Method(typeof(EncounterWindows), nameof(EncounterWindows.OnLevelUp)),
            prefix: new HarmonyMethod(typeof(P2LevelUp), nameof(BeforeOnLevelUp)),
            postfix: new HarmonyMethod(typeof(P2LevelUp), nameof(AfterOnLevelUp)));
        harmony.Patch(AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.Open)),
            prefix: new HarmonyMethod(typeof(P2LevelUp), nameof(BeforeOpen)),
            postfix: new HarmonyMethod(typeof(P2LevelUp), nameof(AfterOpen)));
        harmony.Patch(AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.OnClose)),
            postfix: new HarmonyMethod(typeof(P2LevelUp), nameof(AfterClose)));
        harmony.Patch(AccessTools.Method(typeof(LevelupScreen), nameof(LevelupScreen.CloseLevelupScreen)),
            postfix: new HarmonyMethod(typeof(P2LevelUp), nameof(AfterClose)));
        harmony.Patch(AccessTools.Method(typeof(XpBarUI), nameof(XpBarUI.OnLevelupShow)),
            prefix: new HarmonyMethod(typeof(P2LevelUp), nameof(BeforeP1BarRainbow)));
        ModLog.Info("P2LevelUp: level-up hooks applied");
    }

    // New run / scene: forget queued state, give controls back
    public static void Reset()
    {
        owners.Clear();
        Pending = 0;
        if (P2ScreenOpen) ModLog.Warn("P2 level-up screen still open on reset");
        P2ScreenOpen = false;
        SwapOutItemsHere();
        SwapOutWeapons();
        RestoreControls();
    }

    // Called by P2XpBar when P2 gains levels
    public static void QueueLevelUps(int count)
    {
        var ew = Object.FindObjectOfType<EncounterWindows>();
        if (ew == null)
        {
            ModLog.Warn("P2 level-up: EncounterWindows not found");
            return;
        }
        for (int i = 0; i < count; i++)
        {
            // Count it first: AddEncounter can open the screen right away (v0.15.0), and Open counts it down
            Pending++;
            queueingForP2 = true;
            try { ew.AddEncounter(EEncounter.Levelup); }
            finally { queueingForP2 = false; }
        }
        ModLog.Info($"P2 level-up queued ({Pending} waiting; queue owners: {string.Join(",", owners.Select(o => o ? "P2" : "P1"))})");
    }

    // ---------- Who owns each queued level-up ----------

    private static int addLogs;
    private static void BeforeAddEncounter(EEncounter rewardWindowType)
    {
        if (addLogs++ < 60) ModLog.Info($"[queue] AddEncounter {rewardWindowType} (P2's level-up {queueingForP2}); game {P2Interact.GameQueue()}");
        if (rewardWindowType != EEncounter.Levelup)
        {
            // Chests: whose window, decided now and kept until it opens (v0.49.2: a chest window queued while P1 was
            // down opened much later - P2's 3 s mark had run out and the reward went to P1)
            if (P2Interact.IsChestEncounter(rewardWindowType)) { P2Interact.OnChestQueued(rewardWindowType); return; }
            P2Rewards.OnAddEncounter(rewardWindowType);   // shrines, statues...: whose reward (P2Rewards)
            return;
        }
        owners.Enqueue(queueingForP2);
        addCallsDuringOnLevelUp++;
    }

    // P1's own level-ups: if OnLevelUp queues without going through AddEncounter (inlined), still record P1
    private static void BeforeOnLevelUp() => addCallsDuringOnLevelUp = 0;
    private static void AfterOnLevelUp()
    {
        if (addCallsDuringOnLevelUp == 0) owners.Enqueue(false);
    }

    private static void BeforeOpen(EEncounter encounterType)
    {
        bool p2;
        if (encounterType == EEncounter.Levelup)
        {
            // Owners of level-ups the game no longer has waiting (dropped at a stage change...) would make the next
            // screens the wrong player's (P2Interact.Resync)
            if (P2Interact.Resync(owners, P2Interact.WaitingInGame(e => e == EEncounter.Levelup), "level-ups") > 0)
                Pending = Math.Min(Pending, owners.Count(o => o));
            p2 = owners.Count > 0 && owners.Dequeue();
            if (!p2) return;
            Pending = Math.Max(0, Pending - 1);
        }
        else
        {
            // An upgrade picker that P2 earned (Moai, ...): P2's screen like P2's level-up
            p2 = P2Rewards.TakeOwner(encounterType);
            if (!p2) return;
            ModLog.Info($"P2's {encounterType} screen opening");
        }
        P2ScreenOpen = true;
        GiveControlsToP2();
        SwapInWeapons();
        P2Gold.SwapIn();   // shady guy purchases etc.: P2's gold
        // 1.0 alpha (moai_p2 test: P2 picked Slippery Ring on P2's Moai screen, P1's evasion went up): item offers land
        // in "the player's" item inventory - P2's for the screen, like P2's chest
        if (encounterType != EEncounter.Levelup && !P2Inventory.ItemsSwapped)
        {
            P2Inventory.SwapInItems();
            itemsSwappedHere = P2Inventory.ItemsSwapped;
        }
    }
    private static bool itemsSwappedHere;
    private static void SwapOutItemsHere()
    {
        if (!itemsSwappedHere) return;
        itemsSwappedHere = false;
        P2Inventory.SwapOutItems();
    }

    // ---------- P2's weapons and tomes for the whole screen ----------
    // Offers, the screen's inventory panel, levels and the pick (PlayerInventory.AddUpgrade) use P1's inventory's
    // weaponInventory / tomeInventory. While P2's screen is open those two fields hold P2's holders (P2Inventory),
    // so P2 gets offers for P2's weapons/tomes with the game's own values, and the pick lands in P2's holders.
    // The game is paused meanwhile. Afterwards P1's are put back and P1's stats are recomputed.
    private static bool holdersSwapped;
    private static Assets.Scripts.Inventory__Items__Pickups.Weapons.WeaponInventory p1Weapons;
    private static Assets.Scripts.Inventory__Items__Pickups.TomeInventory p1Tomes;

    private static void SwapInWeapons()
    {
        if (holdersSwapped || P2Inventory.Weapons == null) return;
        var inv = Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory;
        if (inv == null) return;
        P2Inventory.GuardsOn();   // keep P1's HUD / always-on weapons out of P2's picks while the screen is open
        p1Weapons = inv.weaponInventory;
        p1Tomes = inv.tomeInventory;
        inv.weaponInventory = P2Inventory.Weapons;
        inv.tomeInventory = P2Inventory.TomesInv;
        P2Inventory.EnterP2();    // offer texts and values with P2's stats
        holdersSwapped = true;
        ModLog.Info("P2 level-up uses P2's weapons and tomes");
    }

    private static void SwapOutWeapons()
    {
        if (!holdersSwapped) return;
        holdersSwapped = false;
        P2Inventory.Exit();
        var inv = Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory;
        if (inv != null)
        {
            inv.weaponInventory = p1Weapons;
            inv.tomeInventory = p1Tomes;
        }
        p1Weapons = null;
        p1Tomes = null;
        P2Inventory.GuardsOff();
        // P1's stats may have been touched by P2's tome event while swapped: recompute them from P1's own data
        try { inv?.playerStats?.ForceUpdateStats(); }
        catch (Exception e) { ModLog.Warn($"P1 stats refresh after P2's screen: {e.Message}"); }
        ModLog.Info("Back to P1's weapons and tomes");
    }

    private static void AfterOpen(LevelupScreen __instance, EEncounter encounterType)
    {
        if (PlayerJoin.P2Joystick == null) return;
        // TEMP (redesign: offers on the owner's side, inventory + stats stacked on the other): the real layout first
        try { UiDump.DumpOnce("levelup_dump.txt", __instance.transform.parent != null ? __instance.transform.parent : __instance.transform); }
        catch (Exception e) { ModLog.Warn($"Level-up dump: {e.Message}"); }
        bool p2 = P2ScreenOpen;
        if (encounterType != EEncounter.Levelup && !p2) return;   // other screens: only P2's get marked
        try
        {
            if (__instance.t_title != null)
                __instance.t_title.text = encounterType == EEncounter.Levelup
                    ? (p2 ? "PLAYER 2 LEVEL UP!" : "PLAYER 1 LEVEL UP!")
                    : "PLAYER 2: " + __instance.t_title.text;
        }
        catch (Exception e) { ModLog.Warn($"Level-up title: {e.Message}"); }
        try { TintWindow(__instance, p2); }
        catch (Exception e) { ModLog.Warn($"Level-up colours: {e.Message}"); }
        if (p2) ModLog.Info("P2 level-up screen open (P2 controls it)");
    }

    // ---------- Player colours (user: P1's screen blue, P2's red, to tell whose it is) ----------
    // The window's greenish images (green..teal hues, not grey) get the player's hue with their own saturation and
    // brightness. The upgrade cards (their colours show the rarity) and the Banish/Skip/Refresh buttons stay as they are.
    // Original colours are remembered per image, so each opening starts from the game's colours.
    private const float P1Hue = 0.60f, P2Hue = 0.0f;
    private static readonly Dictionary<IntPtr, Color> originalColors = new();
    private static bool tintDumped;

    private static void TintWindow(LevelupScreen screen, bool p2)
    {
        var root = screen.window != null ? screen.window.transform : screen.transform;
        var keep = new List<Transform>();
        var buttons = screen.upgradePicker?.buttons;
        if (buttons != null) foreach (var b in buttons) if (b != null) keep.Add(b.transform);
        foreach (var b in new Component[] { screen.b_skip, screen.b_refresh, screen.b_banish, screen.b_leave })
            if (b != null) keep.Add(b.transform);

        var dump = tintDumped ? null : new System.Text.StringBuilder();
        int tinted = 0;
        foreach (var g in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
        {
            if (g == null || g.TryCast<TMPro.TMP_Text>() != null) continue;
            if (keep.Any(k => g.transform.IsChildOf(k))) continue;
            if (!originalColors.TryGetValue(g.Pointer, out var orig))
            {
                orig = g.color;
                originalColors[g.Pointer] = orig;
            }
            Color.RGBToHSV(orig, out float h, out float s, out float v);
            bool greenish = h >= 0.22f && h <= 0.55f && s >= 0.12f;
            dump?.AppendLine($"{Path(g.transform, root)} [{g.GetIl2CppType().Name}] colour {orig} (h {h:F2} s {s:F2} v {v:F2}){(greenish ? " -> tinted" : "")}");
            if (!greenish) { g.color = orig; continue; }
            var c = Color.HSVToRGB(p2 ? P2Hue : P1Hue, s, v);
            c.a = orig.a;
            g.color = c;
            tinted++;
        }
        if (dump != null)
        {
            tintDumped = true;
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(ModLog.Dir, "levelup_colours.txt"), dump.ToString()); }
            catch { }
        }
        if (tintLogs++ < 6) ModLog.Info($"Level-up screen coloured for {(p2 ? "P2 (red)" : "P1 (blue)")}: {tinted} images");
    }
    private static int tintLogs;

    private static string Path(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static void AfterClose()
    {
        if (!P2ScreenOpen) return;
        P2ScreenOpen = false;
        SwapOutItemsHere();
        SwapOutWeapons();
        P2Gold.SwapOut();
        RestoreControls();
        ModLog.Info($"P2 level-up screen closed ({Pending} more waiting)");
    }

    // ---------- Controls ----------

    // P2's pad is on the game's player right now (P2's level-up / pause menu)
    public static bool ControlsWithP2 => swapped;

    internal static void GiveControlsToP2()
    {
        var joy = PlayerJoin.P2Joystick;
        if (joy == null || !ReInput.isReady || swapped) return;
        var p0 = ReInput.players.GetPlayer(0);
        removedJoysticks.Clear();
        foreach (var j in ReInput.controllers.GetJoysticks())
            if (j != null && j.id != joy.id && ReInput.controllers.IsJoystickAssignedToPlayer(j.id, 0))
                removedJoysticks.Add(j);
        foreach (var j in removedJoysticks) p0.controllers.RemoveController(j);
        hadKeyboard = p0.controllers.hasKeyboard;
        hadMouse = p0.controllers.hasMouse;
        p0.controllers.hasKeyboard = false;
        p0.controllers.hasMouse = false;
        p0.controllers.AddController(joy, true);
        swapped = true;
        ModLog.Info($"Controls -> P2: removed {removedJoysticks.Count} P1 joystick(s), keyboard/mouse off, P2 pad #{joy.id} on Rewired player 0");
    }

    internal static void RestoreControls()
    {
        if (!swapped || !ReInput.isReady) return;
        swapped = false;
        var p0 = ReInput.players.GetPlayer(0);
        var joy = PlayerJoin.P2Joystick;
        if (joy != null) ReInput.controllers.RemoveJoystickFromAllPlayers(joy, false);
        foreach (var j in removedJoysticks)
            if (j != null) p0.controllers.AddController(j, false);
        p0.controllers.hasKeyboard = hadKeyboard;
        p0.controllers.hasMouse = hadMouse;
        P2Movement.WaitForJumpRelease();   // the A that closed P2's menu is no jump
        ModLog.Info($"Controls -> P1: {removedJoysticks.Count} joystick(s) back, keyboard {hadKeyboard}, mouse {hadMouse}");
        removedJoysticks.Clear();
    }

    // P1's bar stays normal while P2's screen is open
    private static bool BeforeP1BarRainbow() => !P2ScreenOpen;
}
