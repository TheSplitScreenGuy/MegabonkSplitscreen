using Assets.Scripts.Actors.Player;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Doors that teleport "the player" inside a stage (1.0.2, players' reports on the Graveyard map: "if p1 or p2 dies on
// the first stage ... u also dont get revived if the other player goes through the door"; "if nobody dies and u both
// make it through the door, p2 will float thousands of meters above the map indefinitely").
// In the game: InteractableCrypt (into a crypt), InteractableCryptLeave (out of it) and InteractableGhostBossLeave (out
// of the ghost boss room) each have a Teleport() that moves P1 only (the game's one player). P2 stayed behind - in a
// crypt that is far from the map, or with the map's floor gone from under P2.
// Here, like the stage portal (Downed.OnStageStart): whoever uses the door, both players go -
//  - before the door's Teleport: a downed P1 is brought back (P1 is held at the spot it went down, the teleport would
//    be undone), the revive countdown / gravestones of the old place cleared
//  - after it (or after the game's MyPlayer.TeleportPlayerImmediate, if that is where P1 is moved): P2 is put next to
//    P1 at the new place, and revived there if it was down
internal static class DoorTeleports
{
    public static bool Fix = true;   // TEMP autotest switch ('fix doors off')
    private static Vector3 p1Before;
    private static int depth;
    private static int logs;

    public static void Apply(Harmony harmony)
    {
        var done = new List<string>();
        void Hook(Type t, string name)
        {
            var m = AccessTools.DeclaredMethod(t, name);
            if (m == null) { done.Add($"{t.Name}.{name} missing"); return; }
            if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { done.Add($"{t.Name}.{name} SKIPPED (shared native code)"); return; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(DoorTeleports), nameof(BeforeTeleport)),
                finalizer: new HarmonyMethod(typeof(DoorTeleports), nameof(AfterTeleport)));
            done.Add($"{t.Name}.{name}");
        }
        Hook(typeof(InteractableCrypt), "Teleport");
        Hook(typeof(InteractableCryptLeave), "Teleport");
        Hook(typeof(InteractableGhostBossLeave), "Teleport");
        Hook(typeof(MyPlayer), "TeleportPlayerImmediate");
        ModLog.Info($"DoorTeleports: {string.Join(", ", done)}");
    }

    private static void BeforeTeleport(System.Reflection.MethodBase __originalMethod)
    {
        if (depth++ > 0) return;
        var p1 = MyPlayer.Instance;
        p1Before = p1 != null ? p1.transform.position : Vector3.zero;
        if (logs++ < 20) ModLog.Info($"[door] {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}: P1 at {p1Before} (down {Downed.P1Down}), P2 at {(P2Movement.Pm != null ? P2Movement.Pm.transform.position.ToString() : "-")} (down {P2Health.IsDead})");
        if (!Fix || !PlayerJoin.HasP2) return;
        try { Downed.OnDoor(); }
        catch (Exception e) { ModLog.Error($"[door] revive before the teleport failed: {e}"); }
    }

    private static Exception AfterTeleport(Exception __exception, System.Reflection.MethodBase __originalMethod)
    {
        if (--depth > 0) return __exception;
        try
        {
            var p1 = MyPlayer.Instance;
            if (p1 == null) return __exception;
            Vector3 now = p1.transform.position;
            float moved = Vector3.Distance(now, p1Before);
            if (logs++ < 20) ModLog.Info($"[door] after {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}: P1 at {now} (moved {moved:F0} m)");
            if (!Fix || !PlayerJoin.HasP2) return __exception;
            if (moved > 5f) BringP2(p1);
            // bug_graveyard: the crypt door's Teleport only starts the move (a fade first) - P1 lands a moment later,
            // not through TeleportPlayerImmediate. Update() watches for P1's jump(s) for a few seconds.
            watchUntil = Time.unscaledTime + (moved > 5f ? 2f : 8f);
            watchFrom = now;
            following = moved > 5f;
        }
        catch (Exception e) { ModLog.Error($"[door] bringing P2 along failed: {e}"); }
        return __exception;
    }

    private static float watchUntil = -1f;
    private static Vector3 watchFrom;

    // Every frame in a run (P2Body.Update): after a door was used, P1's jump to the other side brings P2 along
    public static void Update()
    {
        if (watchUntil < 0f) return;
        var p1 = MyPlayer.Instance;
        if (p1 == null || !Fix) { watchUntil = -1f; return; }
        if (Vector3.Distance(p1.transform.position, watchFrom) > 30f)
        {
            // bug_graveyard: when P1 itself used the door the game moved P1 twice - first to a spot high above the map,
            // a moment later to the landing spot (P2 was left at the first and fell 200 m). So P2 follows every jump of
            // P1's for 2 s after the first one
            bool first = !following;
            following = true;
            watchFrom = p1.transform.position;
            watchUntil = Time.unscaledTime + 2f;
            ModLog.Info($"[door] P1 {(first ? "arrived" : "moved again")} at {p1.transform.position}");
            BringP2(p1);
            return;
        }
        if (Time.unscaledTime > watchUntil)
        {
            watchUntil = -1f;
            if (!following) ModLog.Info("[door] P1 didn't move within 8 s of using the door - P2 stays");
            following = false;
        }
    }
    private static bool following;

    // P2 next to P1 (on P1's right, on the ground under that spot), standing still; revived there if it was down
    private static void BringP2(MyPlayer p1)
    {
        var pm = P2Movement.Pm;
        if (pm == null) return;
        Vector3 at = p1.transform.position + p1.transform.right * 2f;
        if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out var hit, 10f, ~0, QueryTriggerInteraction.Ignore)) at = hit.point + Vector3.up * 0.2f;
        else at = p1.transform.position;   // no floor beside P1 (narrow corridor): P1's own spot
        pm.transform.position = at;
        if (pm.rb != null) { pm.rb.position = at; pm.rb.velocity = Vector3.zero; }
        P2Camera.TurnP2(Mathf.DeltaAngle(P2Camera.Yaw, p1.playerMovement?.orientation != null ? p1.playerMovement.orientation.eulerAngles.y : P2Camera.Yaw));
        bool wasDown = P2Health.IsDead;
        if (wasDown) Downed.Revive(1);
        ModLog.Info($"[door] P2 went through the door with P1: now at {at}{(wasDown ? ", revived" : "")}");
    }
}
