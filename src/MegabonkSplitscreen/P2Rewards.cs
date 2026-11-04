using Assets.Scripts.Actors.Player;
using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Reward windows that P2 earned are P2's (user: after P2 charged a charge shrine, P1 navigated its window).
// In the game every reward goes through EncounterWindows.AddEncounter(type) and opens later (one at a time): level-ups
// and upgrade pickers in LevelupScreen, stat rewards (charge shrine = RandomStats, greed altar, balance...) in EncounterUi,
// chests in ChestWindowUi (handled by P2Interact). Here:
//  - P2 "earns" the next reward when it interacts with a shrine/statue (P2Interact) or a charge shrine pays out while P2
//    is nearer to it than P1 (ChargeShrine.Reward prefix)
//  - AddEncounter (non-level-up) records each reward's owner in a queue (from P2LevelUp's existing AddEncounter hook)
//  - LevelupScreen opens a P2 one like P2's level-up (P2LevelUp.BeforeOpen asks TakeOwner); EncounterUi gets P2's
//    controls (once P2 lets go of the buttons) and P2's stat holder (the chosen stat goes to P2) until it closes
internal static class P2Rewards
{
    private static float markedUntil = -1f;
    private static readonly List<(EEncounter type, bool p2)> owners = new();
    private static bool encounterIsP2, waitRelease, handedOver;
    public static bool EncounterIsP2 => encounterIsP2;

    public static void Apply(Harmony harmony)
    {
        int n = 0;
        n += Hook(harmony, typeof(ChargeShrine), "Reward", nameof(BeforeChargeReward), prefix: true);
        n += Hook(harmony, typeof(EncounterUi), "Open", nameof(AfterEncounterOpen), prefix: false);
        n += Hook(harmony, typeof(EncounterUi), "OnClose", nameof(AfterEncounterClose), prefix: false);
        ModLog.Info($"P2Rewards: {n} of 3 hooks");
    }

    private static int Hook(Harmony harmony, Type t, string name, string patch, bool prefix)
    {
        var m = AccessTools.DeclaredMethod(t, name);
        if (m == null) return 0;
        if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m))
        {
            ModLog.Warn($"P2Rewards: {t.Name}.{name} shares native code, not hooked");
            return 0;
        }
        var hm = new HarmonyMethod(typeof(P2Rewards), patch);
        if (prefix) harmony.Patch(m, prefix: hm);
        else harmony.Patch(m, postfix: hm);
        return 1;
    }

    // P2 did something that gives a reward window: the next one queued (within a few seconds) is P2's
    public static void MarkP2(string why)
    {
        markedUntil = Time.unscaledTime + 3f;
        ModLog.Info($"P2 earned the next reward ({why})");
    }

    private static void BeforeChargeReward(ChargeShrine __instance)
    {
        try
        {
            var p1 = MyPlayer.Instance;
            if (__instance == null || p1 == null || !P2Movement.Active || P2Health.IsDead) return;
            Vector3 s = __instance.transform.position;
            float d1 = Vector3.Distance(p1.transform.position, s), d2 = Vector3.Distance(P2Movement.BodyPosition(), s);
            if (d2 < d1) MarkP2($"charge shrine, P2 {d2:F1} / P1 {d1:F1} away");
        }
        catch (Exception e) { ModLog.Warn($"P2Rewards charge shrine: {e.Message}"); }
    }

    // From P2LevelUp's AddEncounter prefix, for every non-level-up reward
    public static void OnAddEncounter(EEncounter type)
    {
        bool p2 = markedUntil > 0f && Time.unscaledTime <= markedUntil;
        if (p2) markedUntil = -1f;
        // Charge shrines (user: still went to P1 when P2 charged one; the Reward() hook never marked it): the shrine
        // that just completed, nearest to either player, belongs to whoever is nearer to it
        if (!p2 && type == EEncounter.RandomStats) p2 = ChargeShrineIsP2s();
        owners.Add((type, p2));
        if (p2) ModLog.Info($"Reward {type} queued for P2");
    }

    private static bool ChargeShrineIsP2s()
    {
        var p1 = MyPlayer.Instance;
        if (p1 == null || !P2Movement.Active) return false;
        Vector3 a = p1.transform.position, b = P2Movement.BodyPosition();
        ChargeShrine best = null;
        float bestD = float.MaxValue, d1 = 0f, d2 = 0f;
        foreach (var s in UnityEngine.Object.FindObjectsOfType<ChargeShrine>())
        {
            if (s == null || !s.completed) continue;
            float x = Vector3.Distance(a, s.transform.position), y = Vector3.Distance(b, s.transform.position);
            if (Mathf.Min(x, y) < bestD) { bestD = Mathf.Min(x, y); best = s; d1 = x; d2 = y; }
        }
        if (best == null) { ModLog.Info("[reward] RandomStats queued, no completed charge shrine found -> P1"); return false; }
        ModLog.Info($"[reward] RandomStats from a charge shrine: P1 {d1:F1} / P2 {d2:F1} away -> {(d2 < d1 ? "P2" : "P1")}");
        return d2 < d1;
    }

    // The window for `type` is opening: whose is it? (the oldest queued reward of that type)
    public static bool TakeOwner(EEncounter type)
    {
        // Owners of rewards of this type the game no longer has waiting are dropped, oldest first (P2Interact.Resync)
        int waiting = P2Interact.WaitingInGame(e => e == type);
        int mine = owners.Count(o => o.type == type);
        if (waiting >= 0 && mine > waiting + 1)
        {
            for (int k = 0; k < mine - waiting - 1; k++) owners.RemoveAt(owners.FindIndex(o => o.type == type));
            ModLog.Info($"[queue] {type}: {mine - waiting - 1} stale owner(s) dropped");
        }
        int i = owners.FindIndex(o => o.type == type);
        if (i < 0) return false;
        bool p2 = owners[i].p2;
        owners.RemoveAt(i);
        return p2;
    }

    private static void AfterEncounterOpen(EncounterUi __instance, EEncounter encounterType)
    {
        // TEMP (reward screen redesign): the real layout first
        try { UiDump.DumpOnce("reward_dump.txt", __instance.transform.parent != null ? __instance.transform.parent : __instance.transform); }
        catch (Exception e) { ModLog.Warn($"Reward dump: {e.Message}"); }
        if (!TakeOwner(encounterType)) return;
        encounterIsP2 = true;
        waitRelease = true;
        P2Inventory.SwapInStats();
        P2Gold.SwapIn();
        try { if (__instance.t_name != null) __instance.t_name.text = "PLAYER 2: " + __instance.t_name.text; }
        catch { }
        ModLog.Info($"P2's {encounterType} window open (P2 controls it, the reward goes to P2)");
    }

    private static void AfterEncounterClose()
    {
        if (!encounterIsP2) return;
        encounterIsP2 = false;
        waitRelease = false;
        if (handedOver) P2LevelUp.RestoreControls();
        handedOver = false;
        P2Inventory.SwapOutStats();
        P2Gold.SwapOut();
        ModLog.Info("P2's reward window closed");
    }

    // Called every frame in a run (also while paused): controls to P2 once P2 has let go of the buttons
    public static void Update()
    {
        if (!encounterIsP2 || !waitRelease) return;
        var joy = PlayerJoin.P2Joystick;
        if (joy != null && P2Control.PadHeld(joy, "a", "cross", "x", "square", "start", "menu", "options")) return;
        waitRelease = false;
        P2LevelUp.GiveControlsToP2();
        handedOver = true;
    }

    public static void Reset()
    {
        owners.Clear();
        markedUntil = -1f;
        if (encounterIsP2) AfterEncounterClose();
    }
}
