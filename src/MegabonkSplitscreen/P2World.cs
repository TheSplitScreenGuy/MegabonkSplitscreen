using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Game.Combat.EnemySpecialAttacks.Implementations;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Map objects and enemy attacks that only know "the player" (P1), v1.0.5 players' reports:
//  - Cactus (desert; DamagePlayerCollider.OnCollisionEnter): P2 touching one hurt P1. P2's touch now runs the cactus'
//    own hit as P2's (P2Health.AsP2Hit), with P2's own refresh time.
//  - Ladders (graveyard; Ladder.OnTriggerStay): P2 couldn't climb - the ladder put "the player" on it. P2 in a ladder's
//    trigger runs it with P2's movement copy as the player.
//  - Ranged enemies (EnemySpecialAttackPrefab and its kinds: cactus spit, sniper shots, spike projectiles...): every
//    shot was aimed at P1. An attack of an enemy chasing P2 sets itself up (Init, and its attack coroutine's steps)
//    with P2 as the player: P1's object at P2's spot, P2's movement copy as PlayerMovement.Instance.
// Each has a test switch (autotest 'fix cactus|ladder|ranged on|off').
internal static class P2World
{
    public static bool CactusFix = true, LadderFix = true, RangedFix = true;
    private static int logs;

    private static void Log(string s) { if (logs++ < 80) ModLog.Info("[world] " + s); }

    public static void Apply(Harmony harmony)
    {
        var hooked = new List<string>();
        var skipped = new List<string>();
        void Hook(System.Reflection.MethodBase m, string pre, string fin, string label)
        {
            if (m == null) return;
            if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { skipped.Add(label); return; }
            harmony.Patch(m, prefix: pre != null ? new HarmonyMethod(typeof(P2World), pre) : null,
                          finalizer: fin != null ? new HarmonyMethod(typeof(P2World), fin) : null);
            hooked.Add(label);
        }
        Hook(AccessTools.DeclaredMethod(typeof(DamagePlayerCollider), "OnCollisionEnter"), nameof(BeforeCactus), null, "DamagePlayerCollider.OnCollisionEnter");
        Hook(AccessTools.DeclaredMethod(typeof(Ladder), "OnTriggerStay"), nameof(BeforeLadder), null, "Ladder.OnTriggerStay");
        Hook(AccessTools.DeclaredMethod(typeof(JumpPad), "OnTriggerEnter"), nameof(BeforeJumpPad), null, "JumpPad.OnTriggerEnter");
        foreach (var t in new[] { typeof(Lava), typeof(Water) })
            foreach (var name in new[] { "FixedUpdate", "Update" })
            {
                var m = AccessTools.DeclaredMethod(t, name);
                if (m == null) continue;
                if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { skipped.Add($"{t.Name}.{name}"); continue; }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(P2World), nameof(AfterHazardStep)));
                hooked.Add($"{t.Name}.{name}");
            }
        var asm = typeof(EnemySpecialAttackPrefab).Assembly;
        foreach (var t in asm.GetTypes().Where(t => typeof(EnemySpecialAttackPrefab).IsAssignableFrom(t)))
        {
            Hook(AccessTools.DeclaredMethod(t, "Init"), nameof(BeforeAttackStep), nameof(AfterAttackStep), $"{t.Name}.Init");
            foreach (var nt in t.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (nt.Name.Contains("_d__")) Hook(AccessTools.DeclaredMethod(nt, "MoveNext"), nameof(BeforeCoroutineStep), nameof(AfterAttackStep), $"{t.Name}.{nt.Name}.MoveNext");
        }
        ModLog.Info($"P2World: hooked {string.Join(", ", hooked)}; skipped (shared native code) {(skipped.Count > 0 ? string.Join(", ", skipped) : "none")}");
    }

    // ---------- Cactus ----------
    private static readonly Dictionary<IntPtr, float> p2CactusReady = new();
    private static bool cactusAsP2;
    private static bool BeforeCactus(DamagePlayerCollider __instance, Collision collision, System.Reflection.MethodBase __originalMethod)
    {
        if (cactusAsP2 || __instance == null || collision == null || !P2Movement.Active) return true;
        if (!P2BossAttacks.IsP2Collider(collision.collider)) return true;
        if (!CactusFix) { Log($"P2 touched '{__instance.name}' (fix off: the game's hit runs - P1's hp {MyPlayer.Instance?.inventory?.playerHealth?.hp})"); return true; }
        if (P2Health.IsDead) return false;
        float p1Ready = __instance.readyAtTime;
        p2CactusReady.TryGetValue(__instance.Pointer, out float p2Ready);
        __instance.readyAtTime = p2Ready;
        cactusAsP2 = true;
        try
        {
            int hp0 = P2Health.Hp;
            P2Health.AsP2Hit("cactus", () => __originalMethod.Invoke(__instance, new object[] { collision }));
            Log($"P2 touched '{__instance.name}': run as P2's hit, P2 hp {hp0} -> {P2Health.Hp}, P1 hp {MyPlayer.Instance?.inventory?.playerHealth?.hp}");
        }
        catch (Exception e) { Log($"cactus hit as P2 failed: {e.Message}"); }
        finally
        {
            cactusAsP2 = false;
            p2CactusReady[__instance.Pointer] = __instance.readyAtTime;
            __instance.readyAtTime = p1Ready;
        }
        return false;
    }

    // ---------- Ladders ----------
    private static bool ladderAsP2;
    private static int ladderLogs;
    private static bool BeforeLadder(Ladder __instance, Collider other, System.Reflection.MethodBase __originalMethod)
    {
        if (ladderAsP2 || __instance == null || other == null || !P2Movement.Active) return true;
        if (!P2BossAttacks.IsP2Collider(other)) return true;
        if (!LadderFix)
        {
            if (ladderLogs++ < 6) Log($"P2 in ladder '{__instance.name}' (fix off: the game's step runs - P1 onLadder {MyPlayer.Instance?.playerMovement?.onLadder}, P2 onLadder {P2Movement.Pm.onLadder})");
            return true;
        }
        if (P2Health.IsDead) return false;
        var p1 = MyPlayer.Instance;
        var p1Pm = PlayerMovement.Instance;
        var p1Own = p1 != null ? p1.playerMovement : null;
        bool p1Was = p1Own != null && p1Own.onLadder;
        ladderAsP2 = true;
        try
        {
            PlayerMovement.Instance = P2Movement.Pm;
            if (p1 != null) p1.playerMovement = P2Movement.Pm;
            // (user, real pads: still couldn't climb; test: the ladder's own trigger code with P2 as the player never set
            // P2's ladder state - like the jump pad, the game's own call on P2's movement copy: RefreshLadder(ladder))
            P2Movement.Pm.RefreshLadder(__instance.transform);
        }
        catch (Exception e) { Log($"ladder step as P2 failed: {e.Message}"); }
        finally
        {
            if (p1 != null) p1.playerMovement = p1Own;
            if (p1Pm != null) PlayerMovement.Instance = p1Pm;
            ladderAsP2 = false;
        }
        if (ladderLogs++ < 6) Log($"P2 in ladder '{__instance.name}': run with P2 as the player - P2 onLadder {P2Movement.Pm.onLadder}, P1 onLadder {p1Was} -> {p1Own?.onLadder}");
        return false;
    }

    // ---------- Jump pads (user: a launch pad in the graveyard catacombs launched P1, not P2) ----------
    public static bool PadFix = true;
    private static bool padAsP2;
    private static bool BeforeJumpPad(JumpPad __instance, Collider other, System.Reflection.MethodBase __originalMethod)
    {
        if (padAsP2 || !PadFix || __instance == null || other == null || !P2Movement.Active) return true;
        if (!P2BossAttacks.IsP2Collider(other)) return true;
        if (P2Health.IsDead) return false;
        var p1 = MyPlayer.Instance;
        var p1Pm = PlayerMovement.Instance;
        var p1Own = p1 != null ? p1.playerMovement : null;
        Vector3 v0 = P2Movement.Pm.rb != null ? P2Movement.Pm.rb.velocity : Vector3.zero;
        padAsP2 = true;
        try
        {
            PlayerMovement.Instance = P2Movement.Pm;
            if (p1 != null) p1.playerMovement = P2Movement.Pm;
            // (graveyard test: the pad's own trigger code with P2 as the player left P2 still - it finds the player its own
            // way; the game's launch itself is PlayerMovement.JumpPad(pad), called on P2's movement copy, plus the spring)
            P2Movement.Pm.JumpPad(__instance);
            try { __instance.AnimateSpring(); } catch { }
        }
        catch (Exception e) { Log($"jump pad as P2 failed: {e.Message}"); }
        finally
        {
            if (p1 != null) p1.playerMovement = p1Own;
            if (p1Pm != null) PlayerMovement.Instance = p1Pm;
            padAsP2 = false;
        }
        Log($"P2 on jump pad '{__instance.name}': run with P2 as the player - P2's speed {v0} -> {(P2Movement.Pm.rb != null ? P2Movement.Pm.rb.velocity : Vector3.zero)}");
        return false;
    }

    // ---------- Lava (user: P2 took no damage in lava) ----------
    // Lava and damaging Water (floor is lava) check "the player" each step and hurt P1. After the game's own step, the
    // step runs once more with P2 as the player: P1's object at P2's spot, P2's movement copy as the player, on P2's
    // borrowed health (P2Health.AsP2Hit). The object's per-player state (damage / splash timers, player-inside flags,
    // last position) is kept per player, and its camera / water-filter events are muted for P2's run.
    public static bool LavaFix = true;
    private static bool hazardAsP2;
    private static readonly string[] PerPlayerFields = { "nextDamageTime", "nextSplashTime", "isPlayerUnder", "isCameraUnder", "lastPos" };
    private static readonly Dictionary<IntPtr, Dictionary<string, object>> p2HazardState = new();
    private static readonly Dictionary<Type, (System.Reflection.PropertyInfo[] fields, System.Reflection.PropertyInfo[] events)> hazardProps = new();
    private static int lavaLogs;

    private static void AfterHazardStep(MonoBehaviour __instance, System.Reflection.MethodBase __originalMethod)
    {
        if (hazardAsP2 || !LavaFix || __instance == null || !P2Movement.Active || P2Health.IsDead) return;
        var water = __instance.TryCast<Water>();
        if (water != null && !water.isDamageZone) return;   // plain water: swimming is P2's movement copy's own
        var p1 = MyPlayer.Instance;
        if (p1 == null) return;
        var t = __instance.GetType();
        if (!hazardProps.TryGetValue(t, out var props))
        {
            var all = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var stat = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            props = (all.Where(p => PerPlayerFields.Contains(p.Name) && p.CanWrite).ToArray(),
                     stat.Where(p => typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType) && p.CanWrite).ToArray());
            hazardProps[t] = props;
        }
        // P1's state out, P2's in
        var p1State = new Dictionary<string, object>();
        foreach (var p in props.fields) p1State[p.Name] = p.GetValue(__instance);
        if (!p2HazardState.TryGetValue(__instance.Pointer, out var p2State))
        {
            p2State = new Dictionary<string, object>(p1State);
            if (p2State.ContainsKey("isPlayerUnder")) p2State["isPlayerUnder"] = false;
            if (p2State.ContainsKey("nextDamageTime")) p2State["nextDamageTime"] = 0f;
            p2HazardState[__instance.Pointer] = p2State;
        }
        foreach (var p in props.fields) if (p2State.TryGetValue(p.Name, out var v)) p.SetValue(__instance, v);
        var muted = props.events.Select(p => p.GetValue(null)).ToArray();
        foreach (var p in props.events) p.SetValue(null, null);
        var p1Pm = PlayerMovement.Instance;
        var p1Own = p1.playerMovement;
        Vector3 p1Pos = P2Items.P1Home(p1);
        int hp0 = P2Health.Hp;
        hazardAsP2 = true;
        try
        {
            PlayerMovement.Instance = P2Movement.Pm;
            p1.playerMovement = P2Movement.Pm;
            p1.transform.position = P2Movement.Pm.transform.position;
            P2Health.AsP2Hit(t.Name, () => __originalMethod.Invoke(__instance, null));
        }
        catch (Exception e) { if (lavaLogs++ < 5) Log($"{t.Name} step for P2 failed: {e.Message}"); }
        finally
        {
            p1.transform.position = p1Pos;
            p1.playerMovement = p1Own;
            if (p1Pm != null) PlayerMovement.Instance = p1Pm;
            for (int i = 0; i < props.events.Length; i++) props.events[i].SetValue(null, muted[i]);
            foreach (var p in props.fields) { p2State[p.Name] = p.GetValue(__instance); p.SetValue(__instance, p1State[p.Name]); }
            hazardAsP2 = false;
        }
        if (P2Health.Hp != hp0 && lavaLogs++ < 12) Log($"{t.Name} '{__instance.name}' hurt P2: hp {hp0} -> {P2Health.Hp}");
    }

    public static void Reset() => p2HazardState.Clear();

    // ---------- Ranged enemy attacks ----------
    private class AimState
    {
        public MyPlayer p1;
        public Vector3 p1Pos;
        public PlayerMovement p1Pm, p1Own;
        public EnemySpecialAttackPrefab attack;
    }
    private static int aimDepth;
    private static readonly Dictionary<Type, System.Reflection.PropertyInfo> thisProps = new();

    private static void BeforeAttackStep(EnemySpecialAttackPrefab __instance, out AimState __state) => __state = Enter(__instance);

    // An attack coroutine's step (sniper volleys pick their target spots in it) of an enemy chasing P2: aimed with P2
    // as the player. The shot's hit itself comes later (SpawnHitEffect, P2BossAttacks: P2's collider off for the game's
    // hit, P2 hit by distance) - P1's object is back home by then.
    private static void BeforeCoroutineStep(object __instance, out AimState __state)
    {
        __state = null;
        try
        {
            var t = __instance.GetType();
            if (!thisProps.TryGetValue(t, out var p))
            {
                p = t.GetProperty("__4__this");
                thisProps[t] = p;
            }
            __state = Enter(p?.GetValue(__instance) as EnemySpecialAttackPrefab);
        }
        catch { }
    }

    private static int aimLogs;
    private static AimState Enter(EnemySpecialAttackPrefab atk)
    {
        if (atk == null || aimDepth > 0 || !P2Movement.Active) return null;
        Enemy e = null;
        try { e = atk.enemy; } catch { }
        if (!P2Health.TargetsP2(e)) return null;
        var p1 = MyPlayer.Instance;
        if (p1 == null) return null;
        if (!RangedFix) return new AimState { attack = atk };   // (fix off: only the aim is logged)
        var s = new AimState { p1 = p1, p1Pos = P2Items.P1Home(p1), p1Pm = PlayerMovement.Instance, p1Own = p1.playerMovement, attack = atk };
        aimDepth++;
        PlayerMovement.Instance = P2Movement.Pm;
        p1.playerMovement = P2Movement.Pm;
        p1.transform.position = P2Movement.Pm.transform.position;
        return s;
    }

    private static Exception AfterAttackStep(Exception __exception, AimState __state, System.Reflection.MethodBase __originalMethod)
    {
        if (__state == null) return __exception;
        if (__state.p1 != null)
        {
            aimDepth--;
            __state.p1.transform.position = __state.p1Pos;
            __state.p1.playerMovement = __state.p1Own;
            if (__state.p1Pm != null) PlayerMovement.Instance = __state.p1Pm;
        }
        if (aimLogs++ < 30)
            try
            {
                var atk = __state.attack;
                Vector3 aim = atk.transform.position;
                var cactus = atk.TryCast<EnemySpecialAttackCactusProjectile>();
                if (cactus != null) aim = cactus.impactPos;
                if (atk.TryCast<EnemySpecialAttackHitscanMultiple>() != null) return __exception;   // (its shots: P2BossAttacks' hit log)
                Vector3 a1 = MyPlayer.Instance.transform.position, a2 = P2Movement.BodyPosition();
                Log($"{atk.GetIl2CppType().Name} ({__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}) of {atk.enemy?.enemyData?.enemyName} chasing P2, fix {(__state.p1 != null ? "on" : "OFF")}: aim {aim} - {Vector3.Distance(aim, a1):F1} m from P1, {Vector3.Distance(aim, a2):F1} m from P2");
            }
            catch { }
        return __exception;
    }
}
