using Actors.Enemies;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Player;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Enemy knockback from P2's hits (players' report after the 1.0 alpha: "knockback appears to be reversed - enemies
// are knocked into you when hit, not away from you"). The game pushes a hit enemy with EnemyMovementRb.Knockback,
// along a direction it works out for "the player" (P1). For P2's hits the push should point away from P2.
//  - Probe (always counted, logged as [knock] in debug/autotest): per knockback of an enemy nearer P2 than P1, whether
//    the push points away from P2, towards P2, or sideways, and whether it lines up with "away from P1".
//  - Fix: a push on an enemy nearer P2, during P2's hit (P2's projectile, or no projectile running and the enemy is
//    P2's by distance), that points away from P1 rather than away from P2, is turned to point away from P2 (same
//    strength, same up/down part).
internal static class Knockback
{
    public static int Away, Toward, Side, Fixed, Seen;
    public static int TowardAfter;   // pushes still pointing into P2 after the mod's turn
    private static int logs;

    public static void Apply(Harmony harmony)
    {
        var hooked = new List<string>();
        foreach (var m in typeof(EnemyMovementRb).GetMethods().Where(x => x.Name == "Knockback" && x.DeclaringType == typeof(EnemyMovementRb)))
        {
            var ps = m.GetParameters();
            string label = $"Knockback({string.Join(", ", ps.Select(p => p.ParameterType.Name))})";
            if (P2Weapons.IsNativeCodeHooked(m) || !P2Weapons.IsNativeCodeUnique(m)) { ModLog.Warn($"Knockback: {label} shares native code, not hooked"); continue; }
            string hook = ps.Length == 1 ? nameof(BeforeKnockbackDc) : nameof(BeforeKnockbackDir);
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(Knockback), hook));
            hooked.Add(label);
        }
        ModLog.Info($"Knockback: P2's hits push enemies away from P2 ({string.Join(", ", hooked)})");
    }

    public static void Reset() => Away = Toward = Side = Fixed = Seen = TowardAfter = 0;

    public static string Report() => $"enemy knockbacks near P2: {Seen} - the game's push: away from P2 {Away}, towards P2 {Toward}, sideways {Side}; turned by the mod {Fixed}; towards P2 after the mod {TowardAfter}";

    private static void BeforeKnockbackDc(EnemyMovementRb __instance, DamageContainer dc)
    {
        if (dc == null) return;
        Vector3 d = dc.direction;
        if (Turn(__instance, ref d, dc.damageSource)) dc.direction = d;
    }

    private static void BeforeKnockbackDir(EnemyMovementRb __instance, ref Vector3 dir) => Turn(__instance, ref dir, "dir");

    // True if the direction was changed
    private static bool Turn(EnemyMovementRb mov, ref Vector3 dir, string source)
    {
        var body = P2Weapons.Body;
        var p1 = MyPlayer.Instance;
        if (mov == null || body == null || p1 == null) return false;
        Vector3 e = mov.transform.position, a = p1.transform.position, b = body.transform.position;
        Vector3 fromP2 = Flat(e - b), fromP1 = Flat(e - a);
        if (fromP2.sqrMagnitude >= fromP1.sqrMagnitude || fromP2.sqrMagnitude < 0.01f) return false;   // P1's enemy
        Vector3 flat = Flat(dir);
        if (flat.sqrMagnitude < 0.0001f) return false;

        float cosP2 = Vector3.Dot(flat.normalized, fromP2.normalized);
        float cosP1 = fromP1.sqrMagnitude > 0.01f ? Vector3.Dot(flat.normalized, fromP1.normalized) : 0f;
        Seen++;
        if (cosP2 > 0.5f) Away++; else if (cosP2 < -0.5f) Toward++; else Side++;

        // P2's hit? (a P2 projectile is hitting, or no projectile and the enemy is P2's by distance)
        bool? hitter = P2Items.CurrentHitter;
        bool p2Hit = hitter ?? true;
        bool wrong = p2Hit && cosP2 < 0.7f && cosP1 > 0.95f;   // lines up with "away from P1", not away from P2
        if (logs < 40 && ModLog.Debug)
        {
            logs++;
            ModLog.Info($"[knock] {mov.name} ({source}): push {dir} - from P2 {fromP2.magnitude:F1} m, from P1 {fromP1.magnitude:F1} m; " +
                        $"cos away-from-P2 {cosP2:F2}, away-from-P1 {cosP1:F2}; hitter {(hitter == null ? "none" : hitter.Value ? "P2's shot" : "P1's shot")}{(wrong ? " -> turned away from P2" : "")}");
        }
        if (!wrong)
        {
            if (cosP2 < -0.5f) TowardAfter++;
            return false;
        }
        float horiz = flat.magnitude;
        Vector3 turned = fromP2.normalized * horiz;
        dir = new Vector3(turned.x, dir.y, turned.z);
        Fixed++;
        return true;
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
}
