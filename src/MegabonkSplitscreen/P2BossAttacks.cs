using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Game.Combat.EnemySpecialAttacks.Implementations;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Enemy special attacks (boss spikes, slams, pillars...) and P2.
// User: Bark Vader's tree spike attack didn't hurt or knock back P2, and P1 got hit outside the red circle.
// [bossatk] log (v0.49.10): each attack's hit is its SpawnHitEffect (EnemySpecialAttackFollowing for Bark Vader's
// 'RootSpikes': radius 6, damage 103); inside it the game looks for "the player" in the sphere and hits P1 with
// PlayerHealth.DamagePlayerExternal(damage, knockback 25, up). P2's movement copy is on the player layer, so P2
// standing in the sphere made the game hit P1 wherever P1 was (P1 was launched again and again).
//  - while the game's hit runs, P2's collider is off: the game only finds P1 when P1 really is in it
//  - P2 inside the sphere takes the same hit through the game's own DamagePlayerExternal (P2Health.SpecialAttackHit):
//    the damage and knockback the game used for P1 in that hit, else the attack's damage and the last knockback seen
internal static class P2BossAttacks
{
    public static void Apply(Harmony harmony)
    {
        int n = 0;
        foreach (var t in new[] { typeof(EnemySpecialAttackPrefabSingle), typeof(EnemySpecialAttackFollowing), typeof(EnemySpecialAttackPrefabMulti),
                                  typeof(EnemySpecialAttackManyRandom), typeof(EnemyGroundSlam),
                                  // v1.0.5 (players: every ranged enemy's attack went for P1): cactus spit, sniper shots
                                  typeof(EnemySpecialAttackCactusProjectile), typeof(EnemySpecialAttackHitscanMultiple),
                                  typeof(Assets.Scripts.Game.Combat.EnemySpecialAttacks.EnemyProjectileMud) })
            n += Hook(harmony, AccessTools.DeclaredMethod(t, "SpawnHitEffect"), nameof(BeforeHit), nameof(AfterHit));
        foreach (var m in typeof(Assets.Scripts.Game.Combat.DamageUtility).GetMethods().Where(x => x.Name == "GetPlayerDamage" && x.DeclaringType == typeof(Assets.Scripts.Game.Combat.DamageUtility) && x.GetParameters().Length == 6))
            n += Hook(harmony, m, null, nameof(AfterPlayerDamage));
        // Bark Vader's orbs hit by colliding with a player (debuff_barkvader test: P1 got Bleed / BossPoison / Freeze from
        // them, P2 nothing): an orb hitting P2's collider runs its own hit as P2's (P2Health.AsP2Hit)
        foreach (var t in new[] { typeof(BossOrb), typeof(BossOrbShooty), typeof(BossOrbBleed) })
        {
            var m = AccessTools.DeclaredMethod(t, "OnCollisionEnter");
            if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2BossAttacks: {t.Name}.OnCollisionEnter not hooked"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2BossAttacks), nameof(BeforeOrbCollision)));
            n++;
        }
        // Anubis' lasers (user: the red one never went for P2 and neither hurt P2): both hit in OnTriggerStay(other) -
        // a laser touching P2's collider runs its own hit as P2's, with P2's own damage cooldown
        foreach (var t in new[] { typeof(EnemySpecialAttackTargetLaser), typeof(EnemySpecialAttackSpinningLasers) })
        {
            var m = AccessTools.DeclaredMethod(t, "OnTriggerStay");
            if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2BossAttacks: {t.Name}.OnTriggerStay not hooked"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2BossAttacks), nameof(BeforeLaserTrigger)));
            n++;
        }
        n += Hook(harmony, AccessTools.DeclaredMethod(typeof(EnemySpecialAttackTargetLaser), "FixedUpdate"), nameof(BeforeTargetLaserStep), nameof(AfterTargetLaserStep));
        ModLog.Info($"P2BossAttacks: {n} hooks");
    }

    internal static bool IsP2Collider(Collider col)
    {
        var body = P2Body.Body;
        return col != null && ((P2Movement.Pm?.playerCollider != null && col.Pointer == P2Movement.Pm.playerCollider.Pointer)
                               || (P2Movement.Pm != null && col.transform.IsChildOf(P2Movement.Pm.transform))
                               || (body != null && col.transform.IsChildOf(body.transform)));
    }

    // P2's own damage cooldown per laser (the game keeps one per laser, for "the player")
    private static readonly Dictionary<IntPtr, float> p2LaserReady = new();
    private static bool laserAsP2;
    private static bool BeforeLaserTrigger(MonoBehaviour __instance, Collider other, System.Reflection.MethodBase __originalMethod)
    {
        if (laserAsP2 || __instance == null || other == null || !P2Movement.Active || !IsP2Collider(other)) return true;
        if (P2Health.IsDead) return false;
        string kind = __instance.GetIl2CppType().Name;
        var tl = __instance.TryCast<EnemySpecialAttackTargetLaser>();
        var sl = tl == null ? __instance.TryCast<EnemySpecialAttackSpinningLasers>() : null;
        float p1Ready = tl != null ? tl.nextDamageReadyTime : sl != null ? sl.nextDamageReadyTime : 0f;
        p2LaserReady.TryGetValue(__instance.Pointer, out float p2Ready);
        if (tl != null) tl.nextDamageReadyTime = p2Ready; else if (sl != null) sl.nextDamageReadyTime = p2Ready;
        laserAsP2 = true;
        try
        {
            int hp0 = P2Health.Hp;
            P2Health.AsP2Hit(kind, () => __originalMethod.Invoke(__instance, new object[] { other }));
            if (P2Health.Hp != hp0) Log($"{kind} hit P2: hp {hp0} -> {P2Health.Hp}");
        }
        catch (Exception e) { Log($"laser hit as P2 failed: {e.Message}"); }
        finally
        {
            laserAsP2 = false;
            p2LaserReady[__instance.Pointer] = tl != null ? tl.nextDamageReadyTime : sl != null ? sl.nextDamageReadyTime : 0f;
            if (tl != null) tl.nextDamageReadyTime = p1Ready; else if (sl != null) sl.nextDamageReadyTime = p1Ready;
        }
        return false;
    }

    // The red laser chases "the player" by P1's transform ([laser] log: the boss's target was P2, the beam still went 55 m
    // to P1; swapping PlayerMovement.Instance or P1's rigidbody position for its step changed nothing, P1's transform did).
    // While the boss is after P2, P1's transform sits at P2's place for the laser's step only. P1's physics body must not
    // follow it (first try threw P1 up to 24 m): transform syncing is off for that moment and P1's body pose is put back.
    private static Vector3? swappedTf, swappedRbPos;
    private static bool syncWas;
    private static void BeforeTargetLaserStep(EnemySpecialAttackTargetLaser __instance)
    {
        swappedTf = swappedRbPos = null;
        var me = MyPlayer.Instance;
        if (__instance == null || me == null || !TargetsP2(__instance.enemy)) return;
        syncWas = Physics.autoSyncTransforms;
        Physics.autoSyncTransforms = false;
        swappedRbPos = me.playerMovement?.rb != null ? me.playerMovement.rb.position : null;
        swappedTf = me.transform.position;
        me.transform.position = P2Movement.Pm != null ? P2Movement.Pm.transform.position : P2Movement.BodyPosition();
    }

    private static Exception AfterTargetLaserStep(Exception __exception)
    {
        try
        {
            var me = MyPlayer.Instance;
            if (swappedTf.HasValue && me != null)
            {
                me.transform.position = swappedTf.Value;
                Physics.autoSyncTransforms = syncWas;
                if (swappedRbPos.HasValue && me.playerMovement?.rb != null) me.playerMovement.rb.position = swappedRbPos.Value;
            }
        }
        catch (Exception e) { Log($"target laser swap back failed: {e.Message}"); }
        swappedTf = swappedRbPos = null;
        return __exception;
    }

    private static bool TargetsP2(Enemy e)
    {
        var rb = P2Movement.Pm?.rb;
        var t = e?.target;
        return t != null && P2Movement.Active && !P2Health.IsDead
               && ((rb != null && t.Pointer == rb.Pointer) || (P2Body.Body != null && t.transform.IsChildOf(P2Body.Body.transform)));
    }

    private static bool orbAsP2;
    private static bool BeforeOrbCollision(MonoBehaviour __instance, Collision collision, System.Reflection.MethodBase __originalMethod)
    {
        if (orbAsP2 || __instance == null || collision == null || !P2Movement.Active) return true;
        var col = collision.collider;
        var body = P2Body.Body;
        bool p2s = col != null && ((P2Movement.Pm?.playerCollider != null && col.Pointer == P2Movement.Pm.playerCollider.Pointer)
                                   || (P2Movement.Pm != null && col.transform.IsChildOf(P2Movement.Pm.transform))
                                   || (body != null && col.transform.IsChildOf(body.transform)));
        if (!p2s) return true;
        if (P2Health.IsDead) return false;   // a downed P2 isn't hit (and P1 isn't either)
        orbAsP2 = true;
        try
        {
            bool done = P2Health.AsP2Hit(__instance.GetIl2CppType().Name, () => __originalMethod.Invoke(__instance, new object[] { collision }));
            Log($"{__instance.GetIl2CppType().Name} hit P2's '{col.name}' - run as P2's hit: {done}; P2 [{P2PowerUps.DescribeStatus()}]");
        }
        catch (Exception e) { Log($"orb hit as P2 failed: {e.Message}"); }
        finally { orbAsP2 = false; }
        return false;
    }

    private static int Hook(Harmony harmony, System.Reflection.MethodBase m, string pre, string post)
    {
        if (m == null) return 0;
        if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m))
        {
            ModLog.Warn($"P2BossAttacks: {m.DeclaringType?.Name}.{m.Name} shares native code or is hooked, not hooked");
            return 0;
        }
        harmony.Patch(m, prefix: pre != null ? new HarmonyMethod(typeof(P2BossAttacks), pre) : null,
            finalizer: post != null && pre != null ? new HarmonyMethod(typeof(P2BossAttacks), post) : null,
            postfix: post != null && pre == null ? new HarmonyMethod(typeof(P2BossAttacks), post) : null);
        return 1;
    }

    private static int logs;
    private static void Log(string s)
    {
        if (logs++ < 40) ModLog.Info("[bossatk] " + s);
    }

    // The damage the game computes for P1 during a hit (and the last one, for hits P1 wasn't in)
    private static bool inHit;
    private static float hitDamage = -1f, hitKnockback = -1f, lastKnockback = 25f;
    private static Vector3 hitDir, lastDir = Vector3.up;
    private static void AfterPlayerDamage(float damage, float knockback, Vector3 direction)
    {
        if (!inHit) return;
        hitDamage = damage;
        hitKnockback = knockback;
        hitDir = direction;
        lastKnockback = knockback;
        lastDir = direction;
    }

    // The debuffs the game gives during a hit (P2Debuffs tells), and per attack kind the last ones seen: P2 in the hit
    // gets the same (user: mud slowed P1 only - the game's SlowPlayer(3 s) runs inside MudSplash's hit, on P1's list)
    public static bool InHit => inHit;
    private static readonly List<(string kind, float secs)> hitDebuffs = new();
    private static readonly Dictionary<string, List<(string kind, float secs)>> learned = new()
    {
        ["MudSplash"] = new() { ("Slow", 3f) },   // [debuff] log v0.49.11: SlowPlayer(3.0 s) inside each MudSplash hit
    };
    public static void NoteDebuff(string kind, float secs) { if (inHit) hitDebuffs.Add((kind, secs)); }

    private static Collider p2Off;
    private static void BeforeHit()
    {
        inHit = true;
        hitDebuffs.Clear();
        hitDamage = hitKnockback = -1f;
        var col = P2Movement.Active ? P2Movement.Pm?.playerCollider : null;
        if (col != null && col.enabled) { col.enabled = false; p2Off = col; }
    }

    // P2Debuffs' log: the last special attack hit (a debuff right after it likely came from it)
    public static string LastHit { get; private set; } = "none";
    public static float LastHitAt { get; private set; } = -100f;

    private static Exception AfterHit(Exception __exception, EnemySpecialAttackPrefab __instance, object[] __args)
    {
        inHit = false;
        if (__instance != null) { LastHit = $"{__instance.eAttack} ({__instance.enemy?.enemyData?.enemyName})"; LastHitAt = Time.time; }
        if (p2Off != null) { p2Off.enabled = true; p2Off = null; }
        try { HitP2(__instance, __args); }
        catch (Exception e) { Log($"P2's hit failed: {e.Message}"); }
        return __exception;
    }

    private static void HitP2(EnemySpecialAttackPrefab atk, object[] args)
    {
        if (atk == null) return;
        string kindKey = atk.eAttack.ToString();
        if (hitDebuffs.Count > 0) learned[kindKey] = new List<(string, float)>(hitDebuffs);
        if (!P2Movement.Active || P2Health.IsDead) return;
        Vector3 pos = args != null && args.Length > 0 && args[0] is Vector3 v ? v : atk.transform.position;
        var sa = atk.specialAttack;
        float radius = sa != null ? sa.attackRadius : 0f;
        // A slice / shot along a line (EnemySpecialAttackHitscanMultiple: Bandit's slice, snipers; SpawnHitEffect(pos, dir)):
        // P2's distance to the line from pos along dir, up to the attack's range (v1.0.5 test: slices started 2.5 m from P2)
        var hs = atk.TryCast<EnemySpecialAttackHitscanMultiple>();
        Vector3 p2 = P2Movement.BodyPosition() + Vector3.up;
        float d;
        if (hs != null && args != null && args.Length > 1 && args[1] is Vector3 dir0 && dir0.sqrMagnitude > 0.0001f)
        {
            if (radius <= 0f) radius = 0.6f;
            float range = hs.maxRange > 0f ? hs.maxRange : 10f;
            Vector3 n = dir0.normalized;
            float t = Mathf.Clamp(Vector3.Dot(p2 - pos, n), 0f, range);
            d = Vector3.Distance(p2, pos + n * t);
        }
        else
        {
            if (radius <= 0f) return;
            // P2's capsule (centre at its body) reaching into the sphere
            d = Vector3.Distance(p2, pos);
        }
        bool p1Hit = hitDamage >= 0f;
        if (d > radius + (hs != null ? 0.6f : 1.5f))   // (a line: P2's body width; a sphere: the capsule's height too)
        {
            Log($"{atk.eAttack} hit at {pos} (radius {radius:F1}): P2 {d:F1} m - outside; P1 hit {p1Hit}");
            return;
        }
        float dmg = p1Hit ? hitDamage : (sa != null && atk.enemy != null ? sa.GetDamage(atk.enemy) : 0f);
        float kb = p1Hit ? hitKnockback : lastKnockback;
        Vector3 dir = p1Hit ? hitDir : lastDir;
        string source = atk.enemy?.enemyData != null ? atk.enemy.enemyData.enemyName.ToString() : atk.eAttack.ToString();
        if (p1Hit) P2Health.SpecialAttackHit(source, dmg, kb, dir, atk.enemy);
        else P2Explosions.GuardP1("P2's " + atk.eAttack, () => P2Health.SpecialAttackHit(source, dmg, kb, dir, atk.enemy));
        // the attack's debuffs on P2's own list
        var debuffs = hitDebuffs.Count > 0 ? hitDebuffs : learned.TryGetValue(kindKey, out var l) ? l : null;
        string given = "";
        if (debuffs != null && !P2Health.IsDead)
            foreach (var (k, s) in debuffs.ToList()) given += " " + P2Debuffs.Give(1, k, s) + ";";
        Log($"{atk.eAttack} hit at {pos} (radius {radius:F1}): P2 {d:F1} m - hit for {dmg:F0}, knockback {kb:F1} {dir}; P1 hit {p1Hit};{(given != "" ? given : " no debuff")}");
    }
}
