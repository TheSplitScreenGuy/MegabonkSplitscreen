using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using HarmonyLib;
using Rewired;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// P2 interacts with the world (user: vases, chests, greed shrines).
// In the game, P1's DetectInteractables finds the interactable in range (interactableRange, whatIsInteractable layers)
// and the Interact button calls BaseInteractable.Interact(). For P2:
//  - every frame the nearest interactable within the same range of P2 (that says CanInteract) is P2's: outlined, and
//    a red "P2: <what>" prompt shows at the bottom of P2's view
//  - P2's interact button = the pad button P1's Interact action is bound to (Rewired map, read once), else X/Square
//  - pressing it calls Interact(); a greed shrine runs with P2's stat holder swapped in (its effect goes to P2);
//    a chest is marked as P2's: its window gets P2's item holder (the item goes to P2) and P2's controls
internal static class P2Interact
{
    private static BaseInteractable current;
    private static bool buttonWasDown;
    private static string[] buttonNames;
    private static float chestPendingUntil = -1f;
    public static bool ChestIsP2s { get; private set; }
    private static bool chestHandover, chestWaitRelease;

    public static void Apply(Harmony harmony)
    {
        int n = 0;
        foreach (var name in new[] { "Open", "OnClose" })
        {
            var m = AccessTools.DeclaredMethod(typeof(ChestWindowUi), name);
            if (m == null) continue;
            if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m))
            {
                ModLog.Warn($"P2Interact: ChestWindowUi.{name} shares native code, not hooked");
                continue;
            }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(P2Interact), name == "Open" ? nameof(AfterChestOpen) : nameof(AfterChestClose)));
            n++;
        }
        ModLog.Info($"P2Interact: {n} chest window hooks");
        ApplyFreeChest(harmony);
        ApplyPayFix(harmony);
        // TEMP [queue] (players: "on stage 2 the items all go to 1 person"): what the game does with waiting reward
        // windows at the end of a stage - the mod's owner lists must stay in step with the game's queue
        foreach (var name in new[] { "QueueEncountersForNextMap", "OnPortalClosed" })
        {
            var m = AccessTools.DeclaredMethod(typeof(Assets.Scripts.UI.InGame.Levelup.EncounterWindows), name);
            if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"[queue] EncounterWindows.{name} not hookable"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Interact), nameof(BeforeQueueChange)), postfix: new HarmonyMethod(typeof(P2Interact), nameof(AfterQueueChange)));
        }
    }

    // TEMP [chestpay] (user: both players' gold went negative; bug_gold_negative: two chests in flight -> P1 paid both):
    // which chest method takes the money, and from whose gold
    public static void ApplyPayProbe(Harmony harmony)
    {
        foreach (var name in new[] { "OnChestWindowClose", "OpenChestImplementation", "FixedUpdate" })
        {
            var m = AccessTools.DeclaredMethod(typeof(InteractableChest), name);
            if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"[chestpay] InteractableChest.{name} not hookable"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Interact), nameof(BeforePay)), finalizer: new HarmonyMethod(typeof(P2Interact), nameof(AfterPay)));
            ModLog.Info($"[chestpay] watching InteractableChest.{name}");
        }
    }
    private static void BeforePay(InteractableChest __instance, out (float gold, bool opening) __state)
    {
        __state = (MyPlayer.Instance?.inventory?._gold_k__BackingField ?? 0f, __instance != null && __instance.opening);
    }

    // 1.0.2 (user: both players' gold went negative; bug_gold_negative: P1 40 -> -62 with both chests bought together):
    // the game pays a chest in InteractableChest.OnChestWindowClose, which every opening chest runs when ANY chest window
    // closes - P2's chest was paid when P1's window closed, from P1's gold. Now each chest pays from its buyer's gold and
    // chest price, whoever's window is closing.
    public static bool PayFix = true;   // TEMP autotest switch ('fix chestpay off')
    private static void ApplyPayFix(Harmony harmony)
    {
        var m = AccessTools.DeclaredMethod(typeof(InteractableChest), "OnChestWindowClose");
        // One patch call only: with a separate probe patch on it too (and on the chest's FixedUpdate) no chest was paid at all
        if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn("[chestpay] InteractableChest.OnChestWindowClose not hookable - chests pay from whoever's window closes"); return; }
        harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Interact), nameof(BeforeChestPay)), finalizer: new HarmonyMethod(typeof(P2Interact), nameof(AfterChestPay)));
        ModLog.Info("[chestpay] each chest pays from its buyer's gold");
    }
    private static int payLogs2;
    private static void BeforeChestPay(InteractableChest __instance, out int __state)
    {
        __state = 0;
        if (__instance != null && __instance.opening && payLogs2++ < 30) ModLog.Info($"[chestpay] OnChestWindowClose: chest P2's {__instance != null && p2Chests.Contains(__instance.Pointer)}, opening {__instance?.opening}, P2's gold in {P2Gold.SwappedIn}, game gold {MyPlayer.Instance?.inventory?._gold_k__BackingField:F0}");
        if (!PayFix || __instance == null) return;
        bool p2s = p2Chests.Contains(__instance.Pointer);
        if (p2s && !P2Gold.SwappedIn) { P2Gold.SwapIn(); P2ChestPrice.SwapIn(); __state = 1; }
        else if (!p2s && P2Gold.SwappedIn) { bool g = P2Gold.BeginP1(); bool c = P2ChestPrice.BeginP1(); __state = (g ? 2 : 0) | (c ? 4 : 0); }
    }
    private static Exception AfterChestPay(Exception __exception, int __state)
    {
        if (__state == 1) { P2ChestPrice.SwapOut(); P2Gold.SwapOut(); }
        if ((__state & 4) != 0) P2ChestPrice.EndP1();
        if ((__state & 2) != 0) P2Gold.EndP1();
        return __exception;
    }
    private static int payLogs;
    private static Exception AfterPay(InteractableChest __instance, System.Reflection.MethodBase __originalMethod, (float gold, bool opening) __state, Exception __exception)
    {
        float now = MyPlayer.Instance?.inventory?._gold_k__BackingField ?? 0f;
        bool opening = __instance != null && __instance.opening;
        if ((now != __state.gold || opening != __state.opening) && payLogs++ < 40)
            ModLog.Info($"[chestpay] {__originalMethod?.Name} on chest {__instance?.Pointer.ToInt64():X} (P2's {p2Chests.Contains(__instance?.Pointer ?? IntPtr.Zero)}): game gold {__state.gold:F0} -> {now:F0}, opening {__state.opening} -> {opening}; " +
                        $"P2's gold in {P2Gold.SwappedIn}, P2's chest window {ChestIsP2s}, price now {Assets.Scripts.Inventory__Items__Pickups.GoldAndMoney.MoneyUtility.GetChestPrice()}");
        return __exception;
    }
    // Chests P2 bought (by pointer), for the probe and the fix
    private static readonly HashSet<IntPtr> p2Chests = new();

    private static void BeforeQueueChange(System.Reflection.MethodBase __originalMethod) =>
        ModLog.Info($"[queue] before EncounterWindows.{__originalMethod?.Name}: game {GameQueue()}; mod's chest owners [{string.Join(",", chestOwners.Select(o => o ? "P2" : "P1"))}], P2 level-ups waiting {P2LevelUp.Pending}");
    private static void AfterQueueChange(System.Reflection.MethodBase __originalMethod) =>
        ModLog.Info($"[queue] after EncounterWindows.{__originalMethod?.Name}: game {GameQueue()}");

    // Called every frame in a run (from P2Body.Update), also while paused (chest window)
    public static void Update()
    {
        UpdateChestControls();
        var joy = PlayerJoin.P2Joystick;
        var p1 = MyPlayer.Instance;
        // 1.0.2 (players: P2 mashing interact on a chest gave P1 a pile of chests): the game's windows pause its own clock
        // (MyTime.paused), not always Unity's - P2's presses reached the world under P2's own chest window
        if (joy == null || p1 == null || !P2Movement.Active || P2Health.IsDead || Time.timeScale <= 0f || (MashFix && Assets.Scripts.Utility.MyTime.paused))
        {
            if (joy != null && MashFix) buttonWasDown = ButtonHeld(joy);   // a press held over a window is no new press after it
            SetCurrent(null);
            return;
        }
        var detect = p1.GetComponentInChildren<DetectInteractables>();
        if (detect == null) return;

        // P2's chest price (P2ChestPrice): what P2's prompt shows and whether P2 can afford it
        P2ChestPrice.Run(() =>
        {
            SetCurrent(FindNearest(detect));
            bool down = ButtonHeld(joy);
            bool pressed = down && !buttonWasDown;
            buttonWasDown = down;
            if (pressed && current != null) InteractAsP2(current);
            UpdatePrompt();
        });
    }

    // Free chests (user: P2 picked up a free chest, only P1 could navigate its menu): an OpenChest opens the chest window
    // when a player's collider touches it (OnTriggerStay) - P2's body touching it makes the coming window P2's, as P2's
    // chest interaction does (P2's items, gold, chest price and controls)
    private static void ApplyFreeChest(Harmony harmony)
    {
        var m = AccessTools.DeclaredMethod(typeof(OpenChest), "OnTriggerStay");
        if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn("P2Interact: OpenChest.OnTriggerStay not hookable - P2's free chests open as P1's"); return; }
        harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Interact), nameof(BeforeFreeChest)));
        ModLog.Info("P2Interact: free chests touched by P2 open as P2's");
    }

    private static int freeLogs;
    private static bool BeforeFreeChest(OpenChest __instance, Collider other)
    {
        var body = P2Weapons.Body;
        if (__instance == null || other == null || __instance.pickedup) return true;
        // v0.49.2 (user: P1 came back from the dead with a pile of chest rewards): P1's object at the gravestone doesn't
        // pick up free chests while P1 is down
        var p1 = MyPlayer.Instance;
        if (Downed.P1Down && p1 != null && other.transform.IsChildOf(p1.transform)) return false;
        if (body == null) return true;
        bool p2s = other.transform.IsChildOf(body.transform) || (P2Movement.Pm != null && other.transform.IsChildOf(P2Movement.Pm.transform));
        if (!p2s) return true;
        bool can;
        try { can = __instance.CanPickup(); } catch { can = false; }
        if (!can) return true;
        chestPendingUntil = Time.unscaledTime + 3f;
        if (freeLogs++ < 6) ModLog.Info($"[chest] P2 touched a free chest ({__instance.chestType}) - its window is P2's");
        return true;
    }

    // Autotest: what P2's interact button does (no second pad needed) - the nearest interactable in reach of P2
    public static string TestInteract()
    {
        var detect = MyPlayer.Instance?.GetComponentInChildren<DetectInteractables>();
        if (detect == null) return "no DetectInteractables on P1";
        // TEMP [interact]: every interactable collider in P2's reach, with its tag (the game's DetectInteractables has a
        // tagInteractable - user: P2 could summon the boss over and over)
        try
        {
            Vector3 at = P2Movement.BodyPosition() + Vector3.up;
            float range = detect.interactableRange > 0f ? detect.interactableRange : 4f;
            var seen = new List<string>();
            foreach (var c in Physics.OverlapSphere(at, range, detect.whatIsInteractable.value, QueryTriggerInteraction.Collide))
            {
                var b = c != null ? c.GetComponentInParent<BaseInteractable>() : null;
                bool can = false;
                try { can = b != null && b.CanInteract(); } catch { }
                seen.Add($"'{c?.name}' tag '{c?.tag}' -> {(b != null ? $"{b.GetIl2CppType().Name} '{b.name}' can {can}" : "none")} {Vector3.Distance(c.ClosestPoint(at), at):F1} m");
            }
            ModLog.Info($"[interact] P2's reach (game tag '{detect.tagInteractable}'): {string.Join(" | ", seen)}");
        }
        catch (Exception e) { ModLog.Warn($"[interact] listing failed: {e.Message}"); }
        var bi = FindNearest(detect);
        if (bi == null) return "nothing in P2's reach";
        string what = $"{bi.GetIl2CppType().Name} '{bi.name}'";
        InteractAsP2(bi);
        return $"P2 interacted with {what}";
    }
    public static bool ChestHandedOver => chestHandover;
    // P2 is opening a chest or has its window open (ItemEvents: the chest events are P2's)
    public static bool P2OpeningChest => ChestIsP2s || (chestPendingUntil > 0f && Time.unscaledTime <= chestPendingUntil);
    public static bool ChestWaitingForRelease => chestWaitRelease;

    private static BaseInteractable FindNearest(DetectInteractables detect)
    {
        Vector3 p2 = P2Movement.BodyPosition() + Vector3.up;
        float range = detect.interactableRange > 0f ? detect.interactableRange : 4f;
        BaseInteractable best = null;
        float bestD = float.MaxValue;
        foreach (var c in Physics.OverlapSphere(p2, range, detect.whatIsInteractable.value, QueryTriggerInteraction.Collide))
        {
            if (c == null) continue;
            // Only colliders tagged like the game's own search wants (DetectInteractables.tagInteractable; user: P2 could
            // summon the boss over and over - [interact] log: the altar's collider goes 'Interactable' -> 'Untagged' once used)
            if (!string.IsNullOrEmpty(detect.tagInteractable) && !c.CompareTag(detect.tagInteractable)) continue;
            var bi = c.GetComponentInParent<BaseInteractable>();
            if (bi == null || !bi.isActiveAndEnabled) continue;
            bool can;
            try { can = bi.CanInteract(); }
            catch { can = false; }
            if (!can) continue;
            float d = (c.ClosestPoint(p2) - p2).sqrMagnitude;
            if (d < bestD) { bestD = d; best = bi; }
        }
        return best;
    }

    private static void SetCurrent(BaseInteractable bi)
    {
        if (current != null && bi != null && current.Pointer == bi.Pointer) return;
        if (current != null) SetOutline(current, false);
        current = bi;
        if (current != null) SetOutline(current, true);
    }

    private static void SetOutline(BaseInteractable bi, bool on)
    {
        try
        {
            if (bi == null || bi.outline == null) return;
            // Leave it on if P1 is looking at the same thing
            var p1Detect = MyPlayer.Instance?.GetComponentInChildren<DetectInteractables>();
            bool p1On = p1Detect?.currentInteractable != null && p1Detect.currentInteractable.Pointer == bi.Pointer;
            bi.outline.enabled = on || p1On;
        }
        catch { }
    }

    private static int interactLogs, mashLogs;
    private static IntPtr lastChest;
    private static float lastChestAt = -1f;
    // TEMP autotest switch ('fix chestmash off'): the extra-press guard above
    public static bool MashFix = true;
    // Autotest counters (chest_mash test): P2's presses on chests, chests P2 really opened, windows queued/opened per player
    public static int ChestPresses, ChestInteracts;
    public static readonly int[] Queued = new int[2], Opened = new int[2];
    public static string Counters() =>
        $"P2 presses on chests {ChestPresses}, P2 chest interactions {ChestInteracts}; windows queued P1 {Queued[0]} / P2 {Queued[1]}, opened P1 {Opened[0]} / P2 {Opened[1]}; " +
        $"owners waiting [{string.Join(",", chestOwners.Select(o => o ? "P2" : "P1"))}]; game queue {GameQueue()}";

    // The game's waiting reward windows (and the ones it keeps for the next map), for the logs
    public static string GameQueue()
    {
        try
        {
            var ew = Object.FindObjectOfType<Assets.Scripts.UI.InGame.Levelup.EncounterWindows>();
            var parts = new List<string>();
            if (ew?.rewardQueue != null) foreach (var e in ew.rewardQueue) parts.Add(e.ToString());
            var next = new List<string>();
            var nq = Assets.Scripts.UI.InGame.Levelup.EncounterWindows.nextMapQueue;
            if (nq != null) foreach (var e in nq) next.Add(e.ToString());
            return $"[{string.Join(",", parts)}] in progress {ew?.encounterInProgress}, next map [{string.Join(",", next)}]";
        }
        catch (Exception e) { return "? " + e.Message; }
    }
    // Everything P2 interacts with is paid from P2's gold (separate gold per player)
    private static void InteractAsP2(BaseInteractable bi) => P2Gold.Run(() => P2ChestPrice.Run(() => InteractAsP2Inner(bi)));

    private static void InteractAsP2Inner(BaseInteractable bi)
    {
        string type = bi.GetIl2CppType().Name;
        try
        {
            // Anything but vases and chests may open a reward window (greed altar, Moai, shady guy...): that's P2's
            if (bi.TryCast<InteractablePot>() == null && bi.TryCast<InteractableChest>() == null) P2Rewards.MarkP2(type);
            if (bi.TryCast<InteractableShrineGreed>() != null)
            {
                // The shrine's stat change lands in P2's stat holder (user: the effect goes to whoever used it)
                P2Inventory.AsP2("greed shrine", items: false, stats: true, () => bi.Interact());
                ModLog.Info("P2 used a greed shrine (effect on P2's stats)");
                return;
            }
            var chest = bi.TryCast<InteractableChest>();
            if (chest != null)
            {
                ChestPresses++;
                // 1.0.2 (players: "if p2 mashes the interact button on a chest, p1 gets spammed with free chests"): a press
                // while P2's chest is still opening, or its window is on its way, would buy/open it again - not passed on
                bool again = lastChest == chest.Pointer && Time.unscaledTime - lastChestAt < 1.5f;
                if (MashFix && (chest.opening || P2OpeningChest || again))
                {
                    if (mashLogs++ < 10) ModLog.Info($"[chest] P2's extra press on a chest ignored (opening {chest.opening}, P2's window pending/open {P2OpeningChest}, same chest again {again})");
                    return;
                }
                chestPendingUntil = Time.unscaledTime + 3f;   // its window opens now or right after
                bool ok = bi.Interact();
                if (!ok) chestPendingUntil = -1f;
                else { ChestInteracts++; lastChest = chest.Pointer; lastChestAt = Time.unscaledTime; p2Chests.Add(chest.Pointer); }
                ModLog.Info($"P2 opened a chest ({(ok ? "window opening" : "couldn't")}; opening {chest.opening}, game queue {GameQueue()})");
                ItemEvents.P2Interacted(bi, ok);   // P2's own items that react to interacting (Ghost)
                return;
            }
            // 1.0 alpha (user: XP from pots P2 broke went to P1): what the pot spawns is P2's
            if (bi.TryCast<Assets.Scripts.Inventory__Items__Pickups.Interactables.InteractablePot>() != null)
            {
                bool broke = false;
                P2Pickups.PotBrokenByP2(bi);
                broke = bi.Interact();
                if (interactLogs++ < 15) ModLog.Info($"P2 broke a pot: {broke}");
                ItemEvents.P2Interacted(bi, broke);
                return;
            }
            // Shrine of succ (user: P2 used it, the XP went to P1): its "pull all XP" in the next seconds is P2's
            if (bi.TryCast<InteractableShrineMagnet>() != null) P2Pickups.PullForP2(5f);
            bool done = bi.Interact();
            if (interactLogs++ < 15) ModLog.Info($"P2 interacted with {type}: {done}");
            ItemEvents.P2Interacted(bi, done);
        }
        catch (Exception e) { ModLog.Warn($"P2 interact with {type} failed: {e.Message}"); }
    }

    // ---------- P2's chest window ----------

    // Whose each queued chest window is, in queue order (v0.49.2: decided when the chest queues its window, kept until
    // the window opens - a window queued while P1 was down opened long after P2's 3 s mark ran out)
    private static readonly Queue<bool> chestOwners = new();
    public static bool IsChestEncounter(Assets.Scripts.UI.InGame.Rewards.EEncounter e) =>
        e == Assets.Scripts.UI.InGame.Rewards.EEncounter.ChestNormal || e == Assets.Scripts.UI.InGame.Rewards.EEncounter.ChestFree ||
        e == Assets.Scripts.UI.InGame.Rewards.EEncounter.ChestEvil || e == Assets.Scripts.UI.InGame.Rewards.EEncounter.ChestFreeCrypt ||
        e == Assets.Scripts.UI.InGame.Rewards.EEncounter.ChestGhost;
    public static void OnChestQueued(Assets.Scripts.UI.InGame.Rewards.EEncounter e)
    {
        bool p2 = P2OpeningChest && !ChestIsP2s;
        chestOwners.Enqueue(p2);
        Queued[p2 ? 1 : 0]++;
        if (p2) chestPendingUntil = -1f;   // taken into the queue
        if (queueLogs++ < 30) ModLog.Info($"[chest] {e} window queued for {(p2 ? "P2" : "P1")} ({chestOwners.Count} waiting; game queue {GameQueue()})");
    }
    private static int queueLogs;
    public static void Reset() { chestOwners.Clear(); p2Chests.Clear(); chestPendingUntil = -1f; Array.Clear(Queued); Array.Clear(Opened); ChestPresses = ChestInteracts = 0; }

    // How many reward windows the game still has waiting that match (the one opening is no longer in its queue)
    public static int WaitingInGame(Func<Assets.Scripts.UI.InGame.Rewards.EEncounter, bool> match)
    {
        try
        {
            var q = Object.FindObjectOfType<Assets.Scripts.UI.InGame.Levelup.EncounterWindows>()?.rewardQueue;
            if (q == null) return -1;
            int n = 0;
            foreach (var e in q) if (match(e)) n++;
            return n;
        }
        catch { return -1; }
    }

    // 1.0.2 (players: "sometimes on stage 2 the items all go to 1 person (could be player 1 or 2)"): the owner list is
    // a copy kept next to the game's queue. If the game drops waiting windows (a stage change, game over...) the copy
    // keeps owners for windows that will never open, and every later chest takes the wrong one. When a window opens the
    // copy may hold at most this one + the chests still waiting in the game's queue; older extras are dropped.
    public static int Resync<T>(Queue<T> owners, int waiting, string what)
    {
        if (waiting < 0 || owners.Count <= waiting + 1) return 0;
        int dropped = 0;
        while (owners.Count > waiting + 1) { owners.Dequeue(); dropped++; }
        ModLog.Info($"[queue] {what}: {dropped} stale owner(s) dropped (the game has {waiting} waiting besides the one opening)");
        return dropped;
    }

    private static void AfterChestOpen(ChestWindowUi __instance)
    {
        bool p2;
        Resync(chestOwners, WaitingInGame(IsChestEncounter), "chest windows");
        if (chestOwners.Count > 0) p2 = chestOwners.Dequeue();
        else p2 = chestPendingUntil >= 0f && Time.unscaledTime <= chestPendingUntil;   // opened without queueing
        Opened[p2 ? 1 : 0]++;
        if (queueLogs++ < 30) ModLog.Info($"[chest] window opening for {(p2 ? "P2" : "P1")} ({chestOwners.Count} still waiting; game queue {GameQueue()})");
        if (!p2) return;
        chestPendingUntil = -1f;
        ChestIsP2s = true;
        P2Inventory.SwapInItems();
        P2Gold.SwapIn();   // anything bought/rerolled in P2's chest window: P2's gold
        P2ChestPrice.SwapIn();   // and P2's chest price
        chestWaitRelease = true;   // controls once P2 lets go of the button (the press mustn't click in the window)
        try { if (__instance.t_itemName != null) __instance.t_itemName.color = new Color(1f, 0.35f, 0.35f, 1f); }
        catch { }
        ModLog.Info("P2's chest window open: the item goes to P2, P2 controls it");
    }

    private static void AfterChestClose()
    {
        if (!ChestIsP2s) return;
        ChestIsP2s = false;
        chestWaitRelease = false;
        if (chestHandover) P2LevelUp.RestoreControls();
        chestHandover = false;
        P2Inventory.SwapOutItems();
        P2Gold.SwapOut();
        P2ChestPrice.SwapOut();
        ModLog.Info("P2's chest window closed");
    }

    private static void UpdateChestControls()
    {
        if (!ChestIsP2s || !chestWaitRelease) return;
        var joy = PlayerJoin.P2Joystick;
        if (joy != null && ButtonHeld(joy)) return;
        chestWaitRelease = false;
        P2LevelUp.GiveControlsToP2();
        chestHandover = true;
    }

    // ---------- Button and prompt ----------

    // 1.0 alpha (user: on P2's pad Y was interact although the prompt shows X): the element the game's map for P2's
    // own pad puts Interact on (PadBindings, by id); the button name P1's binding has only if that isn't found
    private static bool ButtonHeld(Joystick joy)
    {
        if (!Autotest.Active && joy != null)
        {
            var b = PadBindings.For(joy, MyInputManager.Interact);
            if (b.Found) return PadBindings.Held(joy, b);
        }
        if (buttonNames == null) buttonNames = FindInteractButton();
        return P2Control.PadHeld(joy, buttonNames);
    }

    // The pad button P1's Interact action uses (the game's action name MyInputManager.Interact; read once)
    private static string[] FindInteractButton() => PadBindings.ButtonFor(MyInputManager.Interact, "interact", "x", "square");

    // P2's prompt = an inert copy of P1's (DetectInteractables.uiParent: the interact text and button glyph) placed
    // over P2's interactable like the game places P1's (user: P1's prompts sit over the objects, P2's didn't)
    private static GameObject promptCopy;
    private static TextMeshProUGUI promptText;
    private static Canvas promptCanvas;
    private static bool placementLogged, shownLogged;
    private static CanvasGroup promptGroup;
    private static IntPtr animTarget;
    private static float animStart;

    private static void UpdatePrompt()
    {
        var detect = MyPlayer.Instance?.GetComponentInChildren<DetectInteractables>();
        if (promptCopy == null && detect != null) MakePrompt(detect);
        if (promptCopy == null) return;
        bool show = current != null && !P2LevelUp.P2ScreenOpen;
        if (promptCopy.activeSelf != show) promptCopy.SetActive(show);
        if (!show) { animTarget = IntPtr.Zero; return; }
        string what;
        try { what = current.GetInteractString(); }
        catch { what = "Interact"; }
        if (promptText != null && promptText.text != what)
        {
            // The game's TextSizer (stripped from the copy) widens the text to fit; without it the words wrapped onto
            // three lines (user). No wrapping, and the box as wide as the text.
            promptText.text = what;
            promptText.enableWordWrapping = false;
            var size = promptText.GetPreferredValues(what);
            promptText.rectTransform.sizeDelta = new Vector2(size.x, Mathf.Max(size.y, promptText.rectTransform.sizeDelta.y));
        }

        Vector3 world = current.transform.position;
        try { world += current.GetOffset(); }
        catch { }
        var cam = P2Camera.Merged || P2Camera.Cam == null ? PlayerCamera.Instance?.camera : P2Camera.Cam;
        if (cam == null) return;
        // Fade/scale in like P1's (user): the game's own fromScale -> toScale over animateOverTime, alpha along with it.
        // (v0.28.1 copied the faded-out state and stayed invisible.)
        if (current.Pointer != animTarget) { animTarget = current.Pointer; animStart = Time.unscaledTime; }
        float dur = detect != null && detect.animateOverTime > 0.01f ? detect.animateOverTime : 0.15f;
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.unscaledTime - animStart) / dur));
        if (promptGroup != null) promptGroup.alpha = k;
        if (detect != null && detect.toScale != Vector3.zero)
            promptCopy.transform.localScale = Vector3.Lerp(detect.fromScale, detect.toScale, k);
        if (promptCanvas != null && promptCanvas.renderMode == RenderMode.WorldSpace)
        {
            promptCopy.transform.position = world;
            promptCopy.transform.rotation = cam.transform.rotation;
            if (!shownLogged)
            {
                shownLogged = true;
                ModLog.Info($"[prompt] P2's shown at {world}: active {promptCopy.activeInHierarchy}, alpha {promptGroup?.alpha}, scale {promptCopy.transform.lossyScale}, " +
                            $"canvas active {promptCanvas.gameObject.activeInHierarchy}, text '{promptText?.text}'");
            }
            return;
        }
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) { promptCopy.SetActive(false); return; }
        promptCopy.transform.position = new Vector3(sp.x, sp.y, promptCopy.transform.position.z);
        LogPlacement(detect);
    }

    // TEMP: where P1's prompt sits vs the same calculation with P1's camera (checks the placement method once)
    private static void LogPlacement(DetectInteractables detect)
    {
        if (placementLogged || detect?.currentInteractable == null || detect.uiParent == null) return;
        var bi = detect.currentInteractable;
        var p1Cam = PlayerCamera.Instance?.camera;
        if (p1Cam == null) return;
        Vector3 world = bi.transform.position;
        try { world += bi.GetOffset(); }
        catch { }
        placementLogged = true;
        ModLog.Info($"[prompt] P1's prompt at {detect.uiParent.position}, calculated {p1Cam.WorldToScreenPoint(world)} (canvas {promptCanvas?.renderMode})");
    }

    private static void MakePrompt(DetectInteractables detect)
    {
        try
        {
            var src = detect.uiParent;
            if (src == null) return;
            UiDump.DumpOnce("interact_prompt_dump.txt", src);
            var holder = new GameObject("MegabonkSplitscreen_P2PromptHolder");
            holder.SetActive(false);
            holder.transform.SetParent(src.parent, false);
            var copy = Object.Instantiate(src.gameObject, holder.transform);
            copy.name = "MegabonkSplitscreen_P2Prompt";
            // The text that the game writes (t_interact) in the copy, found by its place in the hierarchy
            var textT = detect.t_interact != null ? P2Select.Mirror(src, detect.t_interact.transform, copy.transform) : null;
            foreach (var m in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (m != null && m.GetIl2CppType().Assembly.GetName().Name == "Assembly-CSharp") Object.DestroyImmediate(m);
            copy.transform.SetParent(src.parent, false);
            copy.transform.SetSiblingIndex(src.GetSiblingIndex() + 1);
            Object.Destroy(holder);
            copy.transform.localScale = detect.toScale != Vector3.zero ? detect.toScale : src.localScale;
            promptText = textT != null ? textT.GetComponent<TextMeshProUGUI>() : copy.GetComponentInChildren<TextMeshProUGUI>(true);
            // The game's own colours (user: the gold amount showed red, which the game uses for "can't afford")
            promptCanvas = copy.GetComponentInParent<Canvas>();
            promptGroup = copy.GetComponent<CanvasGroup>();
            if (promptGroup != null) promptGroup.alpha = 1f;
            var scale = copy.transform.localScale;
            if (scale.sqrMagnitude < 0.0001f) copy.transform.localScale = Vector3.one;   // faded out = scaled to 0 too
            promptCopy = copy;
            copy.SetActive(false);
            ModLog.Info($"P2 prompt: copy of P1's '{src.name}' (canvas {promptCanvas?.renderMode}, text {(promptText != null)})");
        }
        catch (Exception e) { ModLog.Error($"P2 prompt failed: {e}"); }
    }

    public static void Stop()
    {
        SetCurrent(null);
        if (promptCopy != null) Object.Destroy(promptCopy);
        promptCopy = null;
        promptText = null;
    }
}
