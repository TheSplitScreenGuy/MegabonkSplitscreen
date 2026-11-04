using Assets.Scripts.Actors.Player;
using UnityEngine;

namespace MegabonkSplitscreen;

// Who is down (user's plan: the run ends only when both players are down; a downed player is brought back by the
// other one - gravestone, map skull, arrow, a 10 s revive countdown, then a portal they jump out of).
// Piece 1: the run only ends when both are down.
//  - P1 at 0 HP while P2 is alive: the game's KillPlayer / PlayerDied are skipped (no death screen, no game over -
//    the game's own chain is CheckDead -> KillPlayer -> PlayerDied -> PlayerHealth.A_Died), and P1 is down: P1's
//    model hidden, P1's movement held still, every enemy goes for P2.
//  - P2 at 0 HP while P1 is alive: P2's existing down state (P2Health.GoDown).
//  - The second one going down: the game's own death (death screen, game over).
internal static class Downed
{
    public static bool P1Down { get; private set; }
    public static bool AnyDown => P1Down || P2Health.IsDead;
    private static bool allowGameOver;
    private static Vector3 p1DownAt;
    private static int skipLogs, gameOverLogs;

    // Prefix on PlayerHealth.KillPlayer / PlayerDied for P1 (not while P2's health is borrowed): false = skipped
    public static bool AllowP1Death()
    {
        if (allowGameOver)
        {
            if (gameOverLogs++ < 2) ModLog.Info("[down] the game's own death runs (both players down)");
            return true;
        }
        bool p2Alive = PlayerJoin.HasP2 && P2Movement.Active && !P2Health.IsDead;
        if (!p2Alive) return true;   // P2 isn't playing or is down too: the game's own death
        if (!P1Down) GoDownP1();
        else if (skipLogs++ < 3) ModLog.Info("[down] P1 still down: the game's death call skipped again");
        return false;
    }

    private static void GoDownP1()
    {
        var p1 = MyPlayer.Instance;
        P1Down = true;
        p1DownAt = p1 != null ? p1.transform.position : Vector3.zero;
        // The gravestone's face looks towards P2 (the one who'll come for P1)
        var p2Body = P2Weapons.Body;
        Gravestones.Place(0, p1DownAt, p2Body != null ? p2Body.transform.position - p1DownAt : Vector3.forward);
        SetP1Visible(false);
        SetP1Gear(false);
        ModLog.Info($"[down] P1 is down at {p1DownAt} - P2 is alive, the run goes on");
        // User: every listener of P1's death must be held off - who would have heard it (names logged, not guessed)
        LogDeathListeners();
        LogP1Leftovers(p1);
    }

    // TEMP (screenshot: P1's small health bar and shadow stayed at the death spot): what is still drawn of P1's player
    // object outside the hidden model
    private static void LogP1Leftovers(MyPlayer p1)
    {
        if (p1 == null) return;
        var model = p1.playerRenderer?.rendererObject?.transform;
        var parts = new List<string>();
        foreach (var r in p1.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled || (model != null && r.transform.IsChildOf(model))) continue;
            parts.Add($"'{r.name}' (parent '{r.transform.parent?.name}', {r.GetIl2CppType().Name}, layer {LayerMask.LayerToName(r.gameObject.layer)})");
        }
        foreach (var c in p1.GetComponentsInChildren<Canvas>(false)) if (c != null) parts.Add($"canvas '{c.name}' (parent '{c.transform.parent?.name}', {c.renderMode})");
        ModLog.Info($"[down] P1's parts still shown while down: {(parts.Count == 0 ? "none" : string.Join(", ", parts))}");
    }

    private static void LogDeathListeners()
    {
        string Names(Il2CppSystem.Delegate d) => d == null ? "none" : string.Join(", ", d.GetInvocationList().Select(l =>
        {
            try { return $"{l.Method?.DeclaringType?.Name}.{l.Method?.Name}"; } catch { return "?"; }
        }));
        try
        {
            ModLog.Info($"[down] held off: PlayerHealth.A_Died listeners: {Names(Assets.Scripts.Inventory__Items__Pickups.PlayerHealth.A_Died)}; " +
                        $"GameManager.A_GameOver listeners: {Names(GameManager.A_GameOver)}");
        }
        catch (Exception e) { ModLog.Warn($"[down] listener log failed: {e.Message}"); }
    }

    // From P2Health.GoDown: P2 down too -> the game's own death for P1. v0.45.0 called KillPlayer from here and the
    // game crashed inside it (dump: KillPlayer under P2's damage code). Now P1's death is just no longer held off: P1
    // is still at 0 HP, so the game's own death check (PlayerHealth.CheckDead, about once a second) kills P1 itself.
    public static void OnP2Down()
    {
        if (!P1Down) return;
        allowGameOver = true;
        // P1 was held at 1 HP while down (HoldP1): to 0 now, so the game's death check ends the run
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        if (ph != null) ph.hp = 0;
        SetP1Visible(true);   // the game's death camera shows P1's model
        ModLog.Info("[down] P2 is down too: both players are down - the game's own death check ends the run");
    }

    // Back in action (for now from the autotest; later from the revive portal)
    public static void Revive(int player)
    {
        if (player == 0)
        {
            if (!P1Down) return;
            var p1 = MyPlayer.Instance;
            var ph = p1?.inventory?.playerHealth;
            if (ph != null) ph.hp = Mathf.Max(1, ph.maxHp);   // v0.46.2 (user: they should be fully healed): full HP
            P1Down = false;
            SetP1Visible(true);
            SetP1Gear(true);
            Gravestones.Remove(0, burst: true);
            ModLog.Info($"[down] P1 revived with {ph?.hp}/{ph?.maxHp} HP");
        }
        else { P2Health.Revive(); Gravestones.Remove(1, burst: true); }
    }

    // A new stage's spawn portal (P2Body.OnPortalStart; user: when one player takes the portal while the other is down,
    // the downed one should be revived in the next stage - portal_while_down test: P2 was still down in stage 2). The
    // old stage's countdown and gravestones went with its scene. P1: back now, before the portal moves P1 (HoldP1 would
    // pin P1 to the old stage's spot). P2: P2's new body doesn't exist yet - revived when P2's health starts on it.
    public static void OnStageStart()
    {
        if (!P1Down && !P2Health.IsDead) return;
        MegabonkSplitscreen.Revive.Reset();
        ReviveUi.Reset();
        Gravestones.Reset();
        if (P1Down) { allowGameOver = false; Revive(0); ModLog.Info("[down] new stage: P1 revived"); }
        if (P2Health.IsDead) { P2Health.ReviveOnStart = true; ModLog.Info("[down] new stage: P2 comes back with P2's entrance"); }
    }

    // A door that teleports the players (DoorTeleports, Graveyard crypts): like a new stage - the old place's countdown
    // and gravestones go, a downed P1 is back now (before the game moves P1); a downed P2 is revived once it is moved
    public static void OnDoor()
    {
        if (!P1Down && !P2Health.IsDead) return;
        MegabonkSplitscreen.Revive.Reset();
        ReviveUi.Reset();
        if (P1Down) { allowGameOver = false; Revive(0); ModLog.Info("[down] door: P1 revived"); }
    }

    // Each physics step and frame while P1 is down: P1 stays where P1 went down
    // Revive's rise out of the portal: P1's model shows while P1 is still down
    public static void ShowP1ForRise() => SetP1Visible(true);

    public static void HoldP1()
    {
        if (!P1Down) return;
        // v0.49.2 (user: with P1 down, P2 couldn't level up, got no shrine rewards, P2's clock did nothing): the game
        // checks "the player" (P1) is alive before rewards, pickups, power-ups... - at 0 HP everything waited. P1 is
        // held at 1 HP while down (P1's death call stays skipped while P2 is alive; at 0 HP again after a hit, put back)
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        if (ph != null && !allowGameOver && ph.hp != 1) ph.hp = 1;   // exactly 1: P1's regen doesn't heal a downed P1
        if (MegabonkSplitscreen.Revive.Rising(0)) return;   // the rise moves P1 itself
        var p1 = MyPlayer.Instance;
        var input = p1?.playerInput;
        if (input != null) { input.moveHorizontal = 0f; input.moveVertical = 0f; input.jumping = false; input.holdingJump = false; }
        var rb = p1?.playerMovement?.rb;
        if (rb != null) { rb.velocity = Vector3.zero; rb.position = p1DownAt; }
    }

    private static void SetP1Visible(bool on)
    {
        var p1 = MyPlayer.Instance;
        var model = p1?.playerRenderer?.rendererObject;
        if (model != null && model.gameObject.activeSelf != on) model.gameObject.SetActive(on);
        // v0.45.3 ([down] log - P1's parts still shown while down): the floating health bar (world-space canvas
        // 'PlayerUI') and P1's minimap arrow ('MinimapIcon') go too; the gravestone marks the spot
        if (p1 == null) return;
        foreach (var name in new[] { "PlayerUI", "MinimapIcon" })
        {
            var t = p1.transform.Find(name);
            if (t != null && t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        }
    }

    // v0.49.2 (user: "we should disable weapons for p1 when they are dead"; P1 came back with a pile of chest rewards):
    // while down, P1's weapons are off (the game's WeaponInventory.ToggleWeapon - P1's auras follow it through
    // AuraAttacks.OnWeaponToggle) and P1's pickup range (DetectPickups' sphere) is off, so P1's gravestone spot neither
    // kills nor collects. Exactly what was switched off comes back.
    private static readonly List<EWeapon> gearOff = new();
    private static Collider pickupOff;
    private static void SetP1Gear(bool on)
    {
        var p1 = MyPlayer.Instance;
        var wi = p1?.inventory?.weaponInventory;
        try
        {
            if (!on)
            {
                gearOff.Clear();
                if (wi?.weapons != null)
                {
                    var keys = new List<EWeapon>();
                    foreach (var kv in wi.weapons) keys.Add(kv.Key);
                    foreach (var w in keys) { wi.ToggleWeapon(w, false); gearOff.Add(w); }
                }
                var dp = p1?.GetComponentsInChildren<DetectPickups>(true).FirstOrDefault();
                if (dp?.collider != null && dp.collider.enabled) { dp.collider.enabled = false; pickupOff = dp.collider; }
                ModLog.Info($"[down] P1's weapons off while down ({string.Join(", ", gearOff)}), pickup range {(pickupOff != null ? "off" : "not found")}");
            }
            else
            {
                foreach (var w in gearOff) if (wi?.weapons != null && wi.weapons.ContainsKey(w)) wi.ToggleWeapon(w, true);
                if (pickupOff != null) pickupOff.enabled = true;
                if (gearOff.Count > 0 || pickupOff != null) ModLog.Info($"[down] P1's weapons back on ({string.Join(", ", gearOff)}), pickup range on");
                gearOff.Clear();
                pickupOff = null;
            }
        }
        catch (Exception e) { ModLog.Warn($"[down] P1's weapons {(on ? "on" : "off")} failed: {e.Message}"); }
    }

    // v0.49.2 (user: with P1 down, P2 couldn't level up or get shrine rewards; P1 got a pile of rewards when back): the
    // game opens the next queued reward window from EncounterWindows.LateUpdate, which waits while "the player" is dead -
    // P1 held at 0 HP while down. For that call P1 counts as alive (1 HP), then back to what it was.
    public static void Apply(HarmonyLib.Harmony harmony)
    {
        var m = HarmonyLib.AccessTools.DeclaredMethod(typeof(Assets.Scripts.UI.InGame.Levelup.EncounterWindows), "LateUpdate");
        if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn("[down] EncounterWindows.LateUpdate not hookable - rewards wait while P1 is down"); return; }
        harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(Downed), nameof(BeforeRewardQueue)), finalizer: new HarmonyLib.HarmonyMethod(typeof(Downed), nameof(AfterRewardQueue)));
        ModLog.Info("[down] reward windows open while P1 is down (EncounterWindows.LateUpdate)");
    }

    private static int queueLogs;
    private static void BeforeRewardQueue(Assets.Scripts.UI.InGame.Levelup.EncounterWindows __instance, out int __state)
    {
        __state = int.MinValue;
        if (!P1Down) return;
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        if (ph == null || ph.hp > 0) return;
        __state = ph.hp;
        ph.hp = 1;
        if (__instance?.rewardQueue != null && __instance.rewardQueue.Count > 0 && queueLogs++ < 6)
            ModLog.Info($"[down] P1 down, {__instance.rewardQueue.Count} reward window(s) queued - opened for P2 now");
    }

    private static Exception AfterRewardQueue(Exception __exception, int __state)
    {
        if (__state != int.MinValue) { var ph = MyPlayer.Instance?.inventory?.playerHealth; if (ph != null) ph.hp = __state; }
        return __exception;
    }

    public static void Reset()
    {
        SetP1Gear(true);
        Gravestones.Reset();
        P1Down = false;
        allowGameOver = false;
        skipLogs = gameOverLogs = 0;
    }
}
