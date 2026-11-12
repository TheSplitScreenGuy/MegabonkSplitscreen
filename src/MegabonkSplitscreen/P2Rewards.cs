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
        var reward = AccessTools.DeclaredMethod(typeof(ChargeShrine), "Reward");
        if (reward != null && P2Weapons.IsNativeCodeUnique(reward) && !P2Weapons.IsNativeCodeHooked(reward))
        {
            harmony.Patch(reward, prefix: new HarmonyMethod(typeof(P2Rewards), nameof(BeforeChargeReward)),
                          postfix: new HarmonyMethod(typeof(P2Rewards), nameof(AfterChargeReward)));
            n++;
        }
        else ModLog.Warn("P2Rewards: ChargeShrine.Reward shares native code, not hooked");
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

    // v1.0.5 (players: both finished charging a shrine together and it went to P1, even when P2 was in first; user: let
    // both get the reward): both players inside the shrine's zone when it pays out -> the game's reward is P1's and a
    // second one of the same kind is queued for P2 (P2's window opens after P1's, P2 controls it)
    public static bool BothFix = true;
    private static int shrineLogs;
    private static EEncounter? extraForP2;                       // queued for P2 next frame (not inside the game's AddEncounter)
    private static readonly HashSet<int> shrinesPaid = new();   // (instance ids: never reused, unlike pointers)   // each charge shrine decides once

    private static float ZoneRadius(ChargeShrine s)
    {
        try { if (s.zoneRenderer != null) return Mathf.Max(s.zoneRenderer.bounds.extents.x, s.zoneRenderer.bounds.extents.z); } catch { }
        return 6f;
    }
    private static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

    // (logs only now - the owner is decided when the reward is queued, ChargeShrineOwner; this hook didn't always run)
    private static void BeforeChargeReward(ChargeShrine __instance)
    {
        try
        {
            var p1 = MyPlayer.Instance;
            if (__instance == null || p1 == null || !P2Movement.Active || shrineLogs++ >= 20) return;
            Vector3 s = __instance.transform.position;
            ModLog.Info($"[shrine] ChargeShrine.Reward: P1 {Flat(p1.transform.position, s):F1} m, P2 {Flat(P2Movement.BodyPosition(), s):F1} m from it, zone radius {ZoneRadius(__instance):F1}");
        }
        catch (Exception e) { ModLog.Warn($"P2Rewards charge shrine: {e.Message}"); }
    }
    private static void AfterChargeReward() { }

    // From P2LevelUp's AddEncounter prefix, for every non-level-up reward
    public static void OnAddEncounter(EEncounter type)
    {
        bool p2 = markedUntil > 0f && Time.unscaledTime <= markedUntil;
        if (p2) markedUntil = -1f;
        // Charge shrines (user: still went to P1 when P2 charged one; the Reward() hook never marked it): the shrine
        // that just completed, nearest to either player, belongs to whoever is nearer to it - or to both (BothFix)
        if (!p2 && type == EEncounter.RandomStats)
        {
            int who = ChargeShrineOwner();
            if (who == 3)
            {
                owners.Add((type, false));
                extraForP2 = type;   // P2's own, queued next frame
                return;
            }
            p2 = who == 2;
        }
        owners.Add((type, p2));
        if (p2) ModLog.Info($"Reward {type} queued for P2");
    }

    // 1 = P1's, 2 = P2's, 3 = both were in the zone (BothFix)
    private static int ChargeShrineOwner()
    {
        var p1 = MyPlayer.Instance;
        if (p1 == null || !P2Movement.Active) return 1;
        Vector3 a = p1.transform.position, b = P2Movement.BodyPosition();
        ChargeShrine best = null;
        float bestD = float.MaxValue, d1 = 0f, d2 = 0f;
        foreach (var s in UnityEngine.Object.FindObjectsOfType<ChargeShrine>())
        {
            if (s == null || !s.completed || shrinesPaid.Contains(s.GetInstanceID())) continue;
            float x = Flat(a, s.transform.position), y = Flat(b, s.transform.position);
            if (Mathf.Min(x, y) < bestD) { bestD = Mathf.Min(x, y); best = s; d1 = x; d2 = y; }
        }
        if (best == null) { ModLog.Info("[reward] RandomStats queued, no completed charge shrine found -> P1"); return 1; }
        shrinesPaid.Add(best.GetInstanceID());
        float r = ZoneRadius(best);
        bool both = d1 <= r + 0.5f && d2 <= r + 0.5f && !Downed.P1Down && !P2Health.IsDead;
        int who = both && BothFix ? 3 : d2 < d1 ? 2 : 1;
        ModLog.Info($"[reward] RandomStats from a charge shrine: P1 {d1:F1} / P2 {d2:F1} away, zone radius {r:F1}{(both ? ", both inside" : "")} -> {(who == 3 ? "BOTH" : who == 2 ? "P2" : "P1")}{(both && !BothFix ? " (fix off)" : "")}");
        return who;
    }

    // Every frame in a run: P2's own reward of a shrine both players charged
    private static void QueueExtraForP2()
    {
        if (extraForP2 == null) return;
        var type = extraForP2.Value;
        extraForP2 = null;
        try
        {
            var ew = UnityEngine.Object.FindObjectOfType<Assets.Scripts.UI.InGame.Levelup.EncounterWindows>();
            if (ew == null) { ModLog.Warn("[shrine] no EncounterWindows for P2's reward"); return; }
            MarkP2("charge shrine, both players in the zone");
            ew.AddEncounter(type);
            ModLog.Info($"[shrine] P2's own {type} reward queued (both were in the zone)");
        }
        catch (Exception e) { ModLog.Warn($"[shrine] P2's reward failed: {e.Message}"); }
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
        QueueExtraForP2();
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
        extraForP2 = null;
        shrinesPaid.Clear();
        if (encounterIsP2) AfterEncounterClose();
    }
}
