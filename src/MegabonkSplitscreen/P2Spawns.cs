using Assets.Scripts.Game.Spawning;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Enemies spawn around P2 too (user: far from P1, P2 saw no enemies spawning).
// In the game, SpawnPositions.GetEnemySpawnPosition / GetEnemySpawnPositionBiased pick a spot around the one player
// (P1). While the players are far apart (more than the game's maximum spawn distance, or FarDistance), every other
// spawn position is replaced by one around P2, from the game's own GetEnemySpawnPositionAroundPoint with the game's
// spawn distances (GetSpawnDistances). Close together the game's ring already covers both, so nothing changes.
internal static class P2Spawns
{
    private const float FarDistance = 25f;

    private static bool enabled = true;
    private static bool toP2;
    private static int moveLogs, failLogs;

    public static void Apply(Harmony harmony, BepInEx.Configuration.ConfigFile config)
    {
        enabled = config.Bind("Spawning", "SpawnAroundP2", true,
            "When the players are far apart, every other enemy spawns around player 2 instead of player 1.").Value;
        if (!enabled)
        {
            ModLog.Info("P2Spawns: OFF (config Spawning.SpawnAroundP2 = false)");
            return;
        }
        int n = 0;
        foreach (var name in new[] { nameof(SpawnPositions.GetEnemySpawnPosition), nameof(SpawnPositions.GetEnemySpawnPositionBiased) })
        {
            var m = AccessTools.Method(typeof(SpawnPositions), name);
            if (m == null) continue;
            // Never on native code another hook already uses (v0.25.0 launch crash)
            if (P2Weapons.IsNativeCodeHooked(m))
            {
                ModLog.Warn($"P2Spawns: {name} shares native code with a hooked method, not hooked");
                continue;
            }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Spawns), nameof(BeforeSpawnPosition)), postfix: new HarmonyMethod(typeof(P2Spawns), nameof(AfterSpawnPosition)));
            n++;
        }
        ModLog.Info($"P2Spawns: {n} spawn position methods hooked");
        ApplyMoreSpawns(harmony, config);
        // v1.0.5 (players: P1 gets 2-3 enemies, P2 a horde): one side per spawned enemy (the game may ask for several
        // positions per enemy), and where enemies really appear / go, counted per player
        foreach (var m in typeof(Assets.Scripts.Managers.EnemyManager).GetMethods().Where(x => x.Name == "SpawnEnemy" && x.DeclaringType == typeof(Assets.Scripts.Managers.EnemyManager)))
        {
            if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2Spawns: SpawnEnemy({m.GetParameters().Length}) not hookable"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Spawns), nameof(BeforeSpawnEnemy)), postfix: new HarmonyMethod(typeof(P2Spawns), nameof(AfterSpawnEnemy)));
        }
        var rem = AccessTools.DeclaredMethod(typeof(Assets.Scripts.Managers.EnemyManager), "RemoveEnemy");
        if (rem != null && P2Weapons.IsNativeCodeUnique(rem) && !P2Weapons.IsNativeCodeHooked(rem))
            harmony.Patch(rem, prefix: new HarmonyMethod(typeof(P2Spawns), nameof(BeforeRemoveEnemy)));
    }

    public static bool PerEnemyFix = true;
    private static int spawnDepth;
    private static bool enemySideP2;
    public static readonly int[] Appeared = new int[2], Removed = new int[2], RemovedDead = new int[2];
    public static bool TestNoSummonerSpawns;   // autotest 'nospawns on': the game's own summoners spawn nothing
    private static bool BeforeSpawnEnemy(System.Reflection.MethodBase __originalMethod)
    {
        if (TestNoSummonerSpawns && __originalMethod.GetParameters().Length == 5) return false;
        if (spawnDepth++ == 0) enemySideP2 = coin.NextDouble() < 0.5;
        return true;
    }
    private static void AfterSpawnEnemy(Assets.Scripts.Actors.Enemies.Enemy __result, System.Reflection.MethodBase __originalMethod)
    {
        if (TestNoSummonerSpawns && __originalMethod.GetParameters().Length == 5) return;
        if (spawnDepth > 0) spawnDepth--;
        if (__result == null || !P2Movement.Active || P2Health.IsDead) return;
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (p1 == null) return;
        Vector3 at = __result.transform.position;
        Appeared[(at - P2Movement.BodyPosition()).sqrMagnitude < (at - p1.transform.position).sqrMagnitude ? 1 : 0]++;
    }
    private static void BeforeRemoveEnemy(Assets.Scripts.Actors.Enemies.Enemy enemy)
    {
        if (enemy == null || !P2Movement.Active) return;
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (p1 == null) return;
        Vector3 at = enemy.transform.position;
        int side = (at - P2Movement.BodyPosition()).sqrMagnitude < (at - p1.transform.position).sqrMagnitude ? 1 : 0;
        Removed[side]++;
        bool dead = false;
        try { dead = enemy.IsDead(); } catch { }
        if (dead) RemovedDead[side]++;
    }
    public static string Counts() => $"appeared near P1 {Appeared[0]} / P2 {Appeared[1]}; removed near P1 {Removed[0]} (dead {RemovedDead[0]}) / P2 {Removed[1]} (dead {RemovedDead[1]})";
    public static void ResetCounts() { Array.Clear(Appeared); Array.Clear(Removed); Array.Clear(RemovedDead); }

    // ---------- More enemies while apart (user: far apart, the spawns shouldn't look thinned out) ----------
    // The game's summoners earn credits (GetCreditsPerSecond) and spend them on enemies up to a target count
    // (GetNumTargetEnemies), limited per second / per cycle (static maxEnemiesPerSecond / maxEnemiesPerCycle).
    // While apart, all three are multiplied (SpawnMultiplierWhenApart) for the summoners that spawn around the
    // players (stage, swarm, special skeletons - not bosses or shrine challenges); half of those spawns go to P2.
    private static float multiplier = 2f;
    private static readonly HashSet<string> AroundPlayers = new() { "StageSummoner", "SwarmSummoner", "SpecialSkeletonSummoner" };
    private static int baseMaxPerSecond = -1, baseMaxPerCycle = -1;
    private static bool wasApart;

    private static void ApplyMoreSpawns(Harmony harmony, BepInEx.Configuration.ConfigFile config)
    {
        multiplier = config.Bind("Spawning", "SpawnMultiplierWhenApart", 2f,
            "How many more enemies spawn while the players are far apart (2 = double, 1 = off).").Value;
        if (multiplier <= 1.01f) { ModLog.Info("P2Spawns: more spawns while apart OFF"); return; }
        var hooked = new List<string>();
        var skipped = new List<string>();
        void Hook(Type t, string name, string post)
        {
            var m = AccessTools.DeclaredMethod(t, name);
            if (m == null) return;
            string label = $"{t.Name}.{name}";
            // Small methods (e.g. "return 50") can share machine code with unrelated ones: only unique code
            if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { skipped.Add(label); return; }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(P2Spawns), post));
            hooked.Add(label);
        }
        Hook(typeof(Assets.Scripts.Game.Spawning.New.BaseSummoner), "GetCreditsPerSecond", nameof(AfterCredits));
        foreach (var t in new[] { typeof(Assets.Scripts.Game.Spawning.New.BaseSummoner),
                                  typeof(Assets.Scripts.Game.Spawning.New.Summoners.StageSummoner),
                                  typeof(Assets.Scripts.Game.Spawning.New.Summoners.SwarmSummoner),
                                  typeof(Assets.Scripts.Game.Spawning.New.Summoners.SpecialSkeletonSummoner) })
            Hook(t, "GetNumTargetEnemies", nameof(AfterTargetEnemies));
        ModLog.Info($"P2Spawns: x{multiplier:F1} spawns while apart; hooked {string.Join(", ", hooked)}; " +
                    $"skipped (shared native code) {(skipped.Count > 0 ? string.Join(", ", skipped) : "none")}");
    }

    // Players far enough apart that each needs their own spawns
    public static bool Apart()
    {
        if (!enabled || !P2Movement.Active || P2Health.IsDead) return false;
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (p1 == null) return false;
        float min = 0f, max = 0f;
        try { SpawnPositions.GetSpawnDistances(out min, out max); }
        catch { }
        return Vector3.Distance(p1.transform.position, P2Movement.BodyPosition()) >= Mathf.Max(FarDistance, max);
    }

    private static bool Counts(Assets.Scripts.Game.Spawning.New.BaseSummoner s)
    {
        if (s == null) return false;
        try { return AroundPlayers.Contains(s.GetIl2CppType().Name); }
        catch { return false; }
    }

    private static void AfterCredits(Assets.Scripts.Game.Spawning.New.BaseSummoner __instance, ref float __result)
    {
        UpdateCaps();
        if (wasApart && Counts(__instance)) __result *= multiplier;
    }

    private static void AfterTargetEnemies(Assets.Scripts.Game.Spawning.New.BaseSummoner __instance, ref int __result)
    {
        if (wasApart && Counts(__instance)) __result = Mathf.RoundToInt(__result * multiplier);
    }

    // The per-second / per-cycle caps are plain static values: raised while apart, put back after
    private static float nextCapCheck;
    private static void UpdateCaps()
    {
        if (Time.unscaledTime < nextCapCheck) return;
        nextCapCheck = Time.unscaledTime + 0.5f;
        bool apart = Apart();
        if (apart == wasApart) return;
        wasApart = apart;
        try
        {
            if (baseMaxPerSecond < 0)
            {
                baseMaxPerSecond = Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerSecond;
                baseMaxPerCycle = Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerCycle;
            }
            Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerSecond = apart ? Mathf.RoundToInt(baseMaxPerSecond * multiplier) : baseMaxPerSecond;
            Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerCycle = apart ? Mathf.RoundToInt(baseMaxPerCycle * multiplier) : baseMaxPerCycle;
            if (capLogs++ < 10)
                ModLog.Info($"[spawn] players {(apart ? "apart: more" : "together: normal")} spawns (per second {Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerSecond}, " +
                            $"per cycle {Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerCycle})");
        }
        catch (Exception e) { if (capLogs++ < 10) ModLog.Warn($"[spawn] caps failed: {e.Message}"); }
    }
    private static int capLogs;

    // A new run: the caps go back to the game's values (they're static and outlive the stage)
    public static void ResetForNewRun()
    {
        if (baseMaxPerSecond >= 0)
        {
            Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerSecond = baseMaxPerSecond;
            Assets.Scripts.Game.Spawning.New.BaseSummoner.maxEnemiesPerCycle = baseMaxPerCycle;
        }
        wasApart = false;
    }

    private static bool inside;
    private static readonly System.Random coin = new();
    private static int talliedP1, talliedP2, tallyLogs;

    private static void Tally(bool p2, float sep)
    {
        if (p2) talliedP2++; else talliedP1++;
        if (talliedP1 + talliedP2 < 40 || tallyLogs >= 15) return;
        tallyLogs++;
        ModLog.Info($"[spawn] last 40 spawn positions: {talliedP1} around P1, {talliedP2} around P2 (players {sep:F0} apart)");
        talliedP1 = talliedP2 = 0;
    }

    // v1.0.5 (players: "the enemy scaling is 3:1, P1 gets 2-3 enemies, P2 a horde"; test: 32 appeared near P1, 96 near
    // P2): GetEnemySpawnPositionBiased calls GetEnemySpawnPosition inside it, and both were hooked - the inner call's
    // coin gave P2 half the time and the outer one's the other half of the rest: 3 of 4 enemies around P2. Only the
    // outermost call decides now.
    private static int posDepth;
    private static void BeforeSpawnPosition() { if (posDepth > 8) posDepth = 0; posDepth++; }   // (a game exception skips the postfix: never stuck)

    private static void AfterSpawnPosition(ref Vector3 __result)
    {
        if (posDepth > 0) posDepth--;
        if (PerEnemyFix && posDepth > 0) return;   // an inner call: the outer one decides
        if (inside || !P2Movement.Active || P2Health.IsDead) return;
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (p1 == null || __result == SpawnPositions.INVALID_POS) return;
        Vector3 p1Pos = p1.transform.position;
        Vector3 p2Pos = P2Movement.BodyPosition();
        float min = 0f, max = 0f;
        try { SpawnPositions.GetSpawnDistances(out min, out max); }
        catch { }
        float sep = Vector3.Distance(p1Pos, p2Pos);
        // User (v0.27.5): enemies spawned only around P2 when far apart, and should always spawn on both players.
        // The old "every other call" could pair up with the game asking more than once per enemy, so it's a coin flip
        // per call now (own random, the game's isn't touched), at any distance.
        toP2 = PerEnemyFix && spawnDepth > 0 ? enemySideP2 : coin.NextDouble() < 0.5;
        Tally(toP2, sep);
        if (!toP2) return;
        inside = true;
        try
        {
            float lo = min > 0f ? min : 20f, hi = max > lo ? max : lo + 20f;
            var pos = SpawnPositions.GetEnemySpawnPositionAroundPoint(p2Pos, lo, hi, 10, true, 20f);
            if (pos == SpawnPositions.INVALID_POS)
            {
                if (failLogs++ < 5) ModLog.Info($"[spawn] no spot found around P2 ({lo:F0}-{hi:F0}), kept P1's");
                return;
            }
            if (moveLogs++ < 8)
                ModLog.Info($"[spawn] moved to P2: was {Vector3.Distance(__result, p1Pos):F0} from P1, now {Vector3.Distance(pos, p2Pos):F0} from P2 " +
                            $"(game distances {min:F0}-{max:F0}, players {sep:F0} apart)");
            __result = pos;
        }
        catch (Exception e)
        {
            if (failLogs++ < 5) ModLog.Warn($"[spawn] around P2 failed: {e.Message}");
        }
        finally { inside = false; }
    }
}
