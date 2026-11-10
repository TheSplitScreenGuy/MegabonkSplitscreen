using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Player 2's progression and stats, kept by the mod (redesign v0.18, user: "in line with player 1").
//
// v0.16-0.17 gave P2 a second game PlayerInventory. That class is built for one player: its parts hook into the
// game's global (static) events, so P1 and P2 kept triggering each other, and P1's upgrades crashed the game in the
// .NET runtime (coreclr c000001d) whenever that second inventory existed (bisect: no crash without it).
//
// Now P2 only has the two plain holders a game inventory uses inside: a WeaponInventory (P2's weapons) and a
// TomeInventory (P2's tome levels). P2's stats are computed by the mod with the game's own stat code
// (PlayerStatsNew.GetBaseValue / ApplyStatModifier / CheckFinalValue, StatComponents.GetFinalValue) from P2's
// character modifiers and P2's tomes at their levels - the same inputs the game uses for P1's character and tomes,
// so a tome gives P2 the same bonus it gives P1. (Not included: P1's shop/meta upgrades and items.)
//
// While the game works on one of P2's weapons (WeaponUtility.*(WeaponBase), WeaponBase.UpdateStat), the mod is in
// "P2 stat context" and PlayerStatsNew.GetStat/GetRawStat/GetUnclampedStat answer with P2's values. That hook
// replaces the lookup completely (it never wraps game code), so it can't cause the crash type we hunted.
// On P2's level-up screen P1's inventory temporarily holds P2's two holders (see P2LevelUp).
internal static class P2Inventory
{
    public static bool Enabled { get; private set; } = true;
    public static WeaponInventory Weapons { get; private set; }
    public static TomeInventory TomesInv { get; private set; }
    public static readonly List<TomeData> Tomes = new();   // P2's tomes in pick order (for P2's HUD)
    // P2's own items (chests P2 opens) and shrine/encounter stat changes (shrines P2 uses) - user's choice: they count
    // for P2 only. Item special effects (procs, timed) are not run for P2 yet.
    public static Assets.Scripts.Inventory__Items__Pickups.Items.ItemInventory Items { get; private set; }
    public static Assets.Scripts.Inventory__Items__Pickups.Stats.StatInventory StatsInv { get; private set; }

    public static void MarkDirty() => statsDirty = true;

    // Runs `action` with P1's inventory holding P2's item and/or stat holders (the game's code then changes P2's), then
    // puts P1's back, repairs P1's HUD and recomputes both players' stats
    public static void AsP2(string what, bool items, bool stats, Action action)
    {
        var inv = MyPlayer.Instance?.inventory;
        if (inv == null || (items && Items == null) || (stats && StatsInv == null)) { action(); return; }
        var realItems = inv.itemInventory;
        var realStats = inv.statInventory;
        GuardsOn();
        try
        {
            if (items) inv.itemInventory = Items;
            if (stats) inv.statInventory = StatsInv;
            action();
        }
        finally
        {
            inv.itemInventory = realItems;
            inv.statInventory = realStats;
            GuardsOff();
            statsDirty = true;
            try { inv.playerStats?.ForceUpdateStats(); }
            catch (Exception e) { LogOnce("P1 stats after " + what, e); }
        }
    }

    // For a window that stays open (P2's chest): swap in now, swap out on close
    private static Assets.Scripts.Inventory__Items__Pickups.Items.ItemInventory heldItems;
    public static bool ItemsSwapped => heldItems != null;
    public static void SwapInItems()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (inv == null || Items == null || heldItems != null) return;
        heldItems = inv.itemInventory;
        inv.itemInventory = Items;
        GuardsOn();
    }
    public static void SwapOutItems()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (heldItems == null) return;
        if (inv != null) inv.itemInventory = heldItems;
        heldItems = null;
        GuardsOff();
        statsDirty = true;
        try { inv?.playerStats?.ForceUpdateStats(); }
        catch (Exception e) { LogOnce("P1 stats after P2's chest", e); }
        ModLog.Info($"P2's items: {DescribeItems()}");
    }

    // Same for P2's stat holder (P2's reward windows: the chosen stat goes to P2)
    private static Assets.Scripts.Inventory__Items__Pickups.Stats.StatInventory heldStats;
    public static void SwapInStats()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (inv == null || StatsInv == null || heldStats != null) return;
        heldStats = inv.statInventory;
        inv.statInventory = StatsInv;
        GuardsOn();
    }
    public static void SwapOutStats()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (heldStats == null) return;
        if (inv != null) inv.statInventory = heldStats;
        heldStats = null;
        GuardsOff();
        statsDirty = true;
        try { inv?.playerStats?.ForceUpdateStats(); }
        catch (Exception e) { LogOnce("P1 stats after P2's reward", e); }
        LogStats("after P2's reward");
    }

    public static string DescribeItems()
    {
        if (Items?.items == null || Items.items.Count == 0) return "none";
        var parts = new List<string>();
        foreach (var kv in Items.items) parts.Add($"{kv.Key} x{(kv.Value != null ? kv.Value.amount : 0)}");
        return string.Join(", ", parts);
    }

    private static CharacterData p2Char;
    public static CharacterData P2Character => p2Char;
    private static bool statsDirty = true;
    private static readonly HashSet<string> loggedErrors = new();

    public static bool InP2Context => CountP2() > 0;

    public static void Apply(Harmony harmony, BepInEx.Configuration.ConfigFile config)
    {
        Enabled = config.Bind("Experimental", "P2OwnStats", true,
            "Give player 2 its own weapons, tomes and stats. Set to false if the game crashes; player 2 then keeps " +
            "only its starting weapon and level-up picks go to player 1.").Value;
        bool statHooks = config.Bind("Experimental", "P2ContextHooks", true,
            "Let player 2's weapons use player 2's stats (hooks on weapon stat methods). Only used when P2OwnStats = true.").Value;
        if (!Enabled)
        {
            ModLog.Info("P2Inventory: OFF (config Experimental.P2OwnStats = false)");
            return;
        }

        int n = 0;
        if (statHooks)
        {
            var fin = new HarmonyMethod(typeof(P2Inventory), nameof(ContextFinalizer));
            var pre = new HarmonyMethod(typeof(P2Inventory), nameof(ContextPrefixArg0));
            foreach (var m in typeof(WeaponUtility).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(WeaponBase)) continue;
                harmony.Patch(m, prefix: pre, finalizer: fin);
                n++;
            }
            var preI = new HarmonyMethod(typeof(P2Inventory), nameof(ContextPrefixInstance));
            foreach (var m in typeof(WeaponBase).GetMethods().Where(x => x.Name == "UpdateStat" && x.DeclaringType == typeof(WeaponBase)))
            {
                harmony.Patch(m, prefix: preI, finalizer: fin);
                n++;
            }
        }
        // v0.41.2 (autotest: P2's Shield tomes gave P1 a 75 shield - in P1's own stat table): adding a tome fires the
        // global TomeInventory.A_TomeUpgrade, and P1's PlayerStatsNew (QueueUpdateStatTome) books that tome into P1's
        // stats even when it went into P2's holder. Muted while a tome is added to P2's holder (P2's own stats are
        // recomputed from P2's holder anyway). Also on real P2 level-ups.
        int tomeHooks = 0;
        foreach (var name in new[] { nameof(TomeInventory.AddTome), nameof(TomeInventory.AddMaxedTome) })
        {
            var m = AccessTools.DeclaredMethod(typeof(TomeInventory), name);
            if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2Inventory: TomeInventory.{name} not hookable - P2's tomes may leak into P1's stats"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Inventory), nameof(BeforeAddTome)), finalizer: new HarmonyMethod(typeof(P2Inventory), nameof(AfterAddTome)));
            tomeHooks++;
        }
        ModLog.Info($"P2Inventory: ON (mod-side stats), stat context on {n} weapon methods{(statHooks ? "" : " (P2ContextHooks = false)")}, tome-event guard on {tomeHooks} methods");
    }

    private static int tomeMuteLogs;
    private static Assets.Scripts.Inventory__Items__Pickups.Stats.StatInventory tomeStatSwap;
    private static void BeforeAddTome(TomeInventory __instance, out Il2CppSystem.Action<ETome, EStat> __state)
    {
        __state = null;
        if (TomesInv == null || __instance == null || __instance.Pointer != TomesInv.Pointer) return;
        __state = TomeInventory.A_TomeUpgrade;
        TomeInventory.A_TomeUpgrade = null;
        // TEMP: who listens to the tome event (user: a P2 Shield tome didn't fill P2's shield bar)
        if (tomeMuteLogs < 1 && __state != null)
            ModLog.Info("[tome] tome event listeners: " + string.Join(", ", __state.GetInvocationList().Select(l => { try { return $"{l.Method?.DeclaringType?.Name}.{l.Method?.Name}"; } catch { return "?"; } })));
        statsDirty = true;
        // v0.42.1 (tome sweep: Chaos gave P1 two random stats and P2 none): a tome may write its stats straight into
        // "the player's" stat-change holder - P2's is in place while a tome goes into P2's holder
        var inv = MyPlayer.Instance?.inventory;
        if (inv != null && StatsInv != null && inv.statInventory != null && inv.statInventory.Pointer != StatsInv.Pointer)
        {
            tomeStatSwap = inv.statInventory;
            inv.statInventory = StatsInv;
        }
        if (tomeMuteLogs++ < 3) ModLog.Info("P2's tome added: the game's tome event muted (P1's stats don't take it)");
    }

    private static Exception AfterAddTome(Exception __exception, Il2CppSystem.Action<ETome, EStat> __state)
    {
        if (__state != null) TomeInventory.A_TomeUpgrade = __state;
        if (tomeStatSwap != null)
        {
            var inv = MyPlayer.Instance?.inventory;
            if (inv != null) inv.statInventory = tomeStatSwap;
            tomeStatSwap = null;
        }
        return __exception;
    }

    // ---------- Lifetime ----------

    public static void Ensure(CharacterData data)
    {
        if (!Enabled || Weapons != null || data == null) return;
        try
        {
            p2Char = data;
            Weapons = new WeaponInventory();
            if (Weapons.weapons == null) Weapons.weapons = new Il2CppSystem.Collections.Generic.Dictionary<EWeapon, WeaponBase>();
            TomesInv = new TomeInventory();
            if (TomesInv.tomeLevels == null) TomesInv.tomeLevels = new Il2CppSystem.Collections.Generic.Dictionary<ETome, int>();
            if (TomesInv.tomeUpgrade == null) TomesInv.tomeUpgrade = new Il2CppSystem.Collections.Generic.Dictionary<ETome, StatModifier>();
            if (TomesInv.statToTomes == null) TomesInv.statToTomes = new Il2CppSystem.Collections.Generic.Dictionary<EStat, Il2CppSystem.Collections.Generic.HashSet<ETome>>();
            Tomes.Clear();
            try
            {
                Items = new Assets.Scripts.Inventory__Items__Pickups.Items.ItemInventory();
                StatsInv = new Assets.Scripts.Inventory__Items__Pickups.Stats.StatInventory();
                ModLog.Info($"P2 item and stat holders created (items dict {Items.items != null}, permanent {StatsInv.permanentChanges != null}, temporary {StatsInv.temporaryChanges != null})");
            }
            catch (Exception e)
            {
                Items = null;
                StatsInv = null;
                ModLog.Error($"P2 item/stat holders failed (P1's count for P2 as before): {e.Message}");
            }
            statsDirty = true;
            ModLog.Info($"P2 progression created for {data.eCharacter}: max HP {MaxHp()}, damage {Get(EStat.DamageMultiplier):F2}");
            LogStats("P2 start");
        }
        catch (Exception e)
        {
            Weapons = null;
            TomesInv = null;
            ModLog.Error($"P2 progression creation failed: {e}");
        }
    }

    public static void ResetForNewRun()
    {
        if (heldItems != null) SwapOutItems();
        if (heldStats != null) SwapOutStats();
        // The game's end-of-run cleanup of P2's item holder (P1's gets it from the game): its own LateFixedUpdate
        // subscription and its items' static-event subscriptions go. v1.0.3 test: without it every finished run's P2
        // holder kept ticking ("OnLateFixedUpdate on other") for the rest of the session.
        if (Items != null)
        {
            var old = Items;
            Items = null;   // the mod's P2 item hooks treat it as nobody's while it cleans up
            static int N(Il2CppSystem.Delegate d) => d == null ? 0 : d.GetInvocationList().Length;
            int died0 = N(Assets.Scripts.Actors.Enemies.Enemy.A_EnemyDied), dmg0 = N(Assets.Scripts.Actors.Enemies.Enemy.A_Damage);
            try { old.Cleanup(); ModLog.Info($"P2's item holder of the last run cleaned up (the game's ItemInventory.Cleanup); Enemy.A_EnemyDied listeners {died0} -> {N(Assets.Scripts.Actors.Enemies.Enemy.A_EnemyDied)}, A_Damage {dmg0} -> {N(Assets.Scripts.Actors.Enemies.Enemy.A_Damage)}"); }
            catch (Exception e) { ModLog.Warn($"P2's item holder cleanup: {e.Message}"); }
        }
        Weapons = null;
        TomesInv = null;
        Items = null;
        StatsInv = null;
        Tomes.Clear();
        while (saved.Count > 0) Pop();
        p2Dict = null;
        p2Raw = null;
        statsDirty = true;
        lastWeaponCount = lastWeaponLevels = lastTomeLevels = -1;
    }

    // ---------- P2's stats, computed by the game ----------
    // v0.18.0 rebuilt the game's formula and got it wrong (P1 check: MaxHealth 120 vs 1, damage 1.00 vs 0.00 ->
    // P2 had 1 HP and 0 attack speed). Now the game computes them: P1's inventory briefly gets P2's character and
    // P2's tome holder, PlayerStatsNew.ForceUpdateStats() fills its stats table, the mod copies it, then P1's are put
    // back and recomputed (P1's HP/shield are saved and restored around it, so a lower P2 max can't cut P1's HP).
    // P2's values therefore come from the game's own code; shop upgrades count for both, items and shrine changes are
    // each player's own (P2's holders are swapped in too, v0.28.0).
    private static Il2CppSystem.Collections.Generic.Dictionary<EStat, float> p2Dict;
    private static float nextRecompute;
    private const float RecomputeEvery = 10f;   // picks up P1's item/shop changes too

    public static int MaxHp() => Mathf.Max(1, Mathf.RoundToInt(Get(EStat.MaxHealth)));

    public static float Get(EStat s)
    {
        if (statsDirty || p2Dict == null) Recompute();
        return p2Dict != null && p2Dict.ContainsKey(s) ? p2Dict[s] : 0f;
    }

    // v0.25.1 froze at the start of a run: P2's aura hook asked for P2's stats -> Recompute -> ForceUpdateStats fired
    // the stat event -> P2's aura handler -> its hook asked again (still none) -> Recompute ... forever. Not re-entrant now.
    public static bool Recomputing { get; private set; }

    private static void Recompute()
    {
        if (Recomputing || saved.Count > 0 || TomesInv == null || p2Char == null) return;   // never in the middle of a context
        Recomputing = true;
        try { RecomputeNow(); }
        finally { Recomputing = false; }
    }

    public static int RecomputeCount;   // TEMP [perf]: how often P2's stats are recomputed
    public static string LastRecomputeReason = "";
    private static void RecomputeNow()
    {
        RecomputeCount++;
        var inv = MyPlayer.Instance?.inventory;
        var ps = inv?.playerStats;
        var ph = inv?.playerHealth;
        if (ps == null) return;
        statsDirty = false;
        nextRecompute = Time.unscaledTime + RecomputeEvery;
        var realTomes = inv.tomeInventory;
        var realChar = inv.characterData;
        var realItems = inv.itemInventory;
        var realStatInv = inv.statInventory;
        var realPassive = inv.passiveAbility;
        var realStatus = inv.statusEffects;
        int hp = ph?.hp ?? 0, maxHp = ph?.maxHp ?? 0;
        float shield = ph?.shield ?? 0f, overheal = ph?.overheal ?? 0f, maxShield = ph?.maxShield ?? 0f;
        // v0.46.4 (user, P1 Cl4nk + P2 Calcium: P1's shield kept going to 0 and P1 kept getting a "+60 hp" effect):
        // the stat event (PlayerStatsNew.A_StatUpdate) told P1's PlayerHealth about P2's stats while they were in P1's
        // table - P1's max HP/shield dropped to P2's (shield cut), and back up afterwards (a heal popup for the
        // difference) every recompute. Nobody hears the recompute now: P2's values aren't anyone's news, and P1's
        // come out the same as before
        var statEvent = PlayerStatsNew.A_StatUpdate;
        PlayerStatsNew.A_StatUpdate = null;
        // v0.47.1 (user: P1's shield upgrades were slow to apply): P1's own stat changes that land during the recompute
        // (P1 just took a tome) were muted too - P1's table before, to announce what really changed for P1 afterwards
        var p1Before = new Dictionary<EStat, float>();
        foreach (var kv in ps.stats) p1Before[kv.Key] = kv.Value;
        try
        {
            inv.tomeInventory = TomesInv;
            inv.characterData = p2Char;
            // P2's own items and shrine changes (P1's no longer count for P2)
            if (Items != null) inv.itemInventory = Items;
            if (StatsInv != null) inv.statInventory = StatsInv;
            // P2's own character passive (its level bonuses), not P1's (P2Abilities)
            if (P2Abilities.Passive != null) inv.passiveAbility = P2Abilities.Passive;
            // P2's own power-up effects, not P1's (v0.44.0 log: P2 'got' P1's Haste)
            if (P2PowerUps.Status != null) inv.statusEffects = P2PowerUps.Status;
            ps.ForceUpdateStats();
            // v0.41.3 (autotest: with the tome event muted, P2's Shield tome left P2's Shield at 0 - ForceUpdateStats
            // doesn't redo every stat): every stat recalculated from the holders now in place
            UpdateAllStats(ps);
            var copy = new Il2CppSystem.Collections.Generic.Dictionary<EStat, float>();
            foreach (var kv in ps.stats) copy[kv.Key] = kv.Value;
            p2Dict = copy;
            // v0.41.11 (autotest: P2's stats panel showed Shield 0 with P2's shield up): the game's second table too
            var raw = new Il2CppSystem.Collections.Generic.Dictionary<EStat, float>();
            if (ps.rawStats != null) foreach (var kv in ps.rawStats) raw[kv.Key] = kv.Value;
            p2Raw = raw;
        }
        catch (Exception e) { LogOnce("recompute", e); }
        finally
        {
            inv.tomeInventory = realTomes;
            inv.characterData = realChar;
            inv.itemInventory = realItems;
            inv.statInventory = realStatInv;
            inv.passiveAbility = realPassive;
            inv.statusEffects = realStatus;
            try { ps.ForceUpdateStats(); UpdateAllStats(ps); }
            catch (Exception e) { LogOnce("P1 recompute", e); }
            PlayerStatsNew.A_StatUpdate = statEvent;
            if (ph != null)
            {
                // TEMP: anything of P1's health the recompute still changed (should be nothing now)
                if ((ph.maxHp != maxHp || Mathf.Abs(ph.maxShield - maxShield) > 0.01f || ph.hp != hp || Mathf.Abs(ph.shield - shield) > 0.01f) && p1ChangeLogs++ < 5)
                    ModLog.Info($"[stats] P2's recompute changed P1's health: max HP {maxHp} -> {ph.maxHp}, max shield {maxShield:F0} -> {ph.maxShield:F0}, HP {hp} -> {ph.hp}, shield {shield:F0} -> {ph.shield:F0} (put back)");
                ph.maxHp = maxHp;
                ph.maxShield = maxShield;
                ph.hp = hp;
                ph.shield = shield;
                ph.overheal = overheal;
            }
            // P1's own changes (not P2's - P1's table is P1's again here) told to everyone now, the game's way
            var changed = new List<EStat>();
            foreach (var kv in ps.stats)
                if (!p1Before.TryGetValue(kv.Key, out float was) || Mathf.Abs(was - kv.Value) > 0.0001f) changed.Add(kv.Key);
            foreach (var s in changed)
            {
                try { statEvent?.Invoke(s); }
                catch (Exception e) { LogOnce("P1 stat event " + s, e); }
            }
            if (changed.Count > 0 && p1EventLogs++ < 8) ModLog.Info($"[stats] P1's own stat changes during P2's recompute announced: {string.Join(", ", changed)}");
        }
    }
    private static int p1EventLogs;
    private static int p1ChangeLogs;

    private static EStat[] allStats;
    private static void UpdateAllStats(PlayerStatsNew ps)
    {
        allStats ??= (EStat[])Enum.GetValues(typeof(EStat));
        foreach (var s in allStats)
        {
            try { ps.UpdateStat(s); }
            catch (Exception e) { LogOnce("update stat " + s, e); }
        }
    }

    // TEMP: P2's key stats next to P1's
    private static void LogStats(string why)
    {
        var ps = MyPlayer.Instance?.inventory?.playerStats;
        if (ps == null || p2Dict == null) return;
        var lines = new List<string>();
        foreach (var s in new[] { EStat.MaxHealth, EStat.DamageMultiplier, EStat.AttackSpeed, EStat.SizeMultiplier, EStat.Projectiles, EStat.CritChance })
            lines.Add($"{s} P2 {(p2Dict.ContainsKey(s) ? p2Dict[s] : 0f):F2} / P1 {(ps.stats.ContainsKey(s) ? ps.stats[s] : 0f):F2}");
        ModLog.Info($"[stats] {why}: {string.Join("; ", lines)}");
    }

    // ---------- Stat context ----------
    // While the game works on a P2 weapon, P1's stat table (PlayerStatsNew.stats) is swapped for P2's copy, so every
    // stat lookup returns P2's values - no lookup hooks. A stack restores the previous table; P1's weapons always
    // get P1's table back, even inside a P2 action.
    // v0.37.0: the passive too - in P2's context P1's inventory holds P2's character passive, so the game's "has
    // passive X" checks and passive bonuses are P2's (P2Abilities); P1's table brings P1's passive back.
    private static readonly Stack<Il2CppSystem.Collections.Generic.Dictionary<EStat, float>> saved = new();
    private static readonly Stack<Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.PassiveAbility> savedPassive = new();
    private static readonly Stack<Il2CppSystem.Collections.Generic.Dictionary<EStat, float>> savedRaw = new();
    private static readonly Stack<PlayerStatusEffects> savedStatus = new();
    private static Il2CppSystem.Collections.Generic.Dictionary<EStat, float> p2Raw;

    private static PlayerStatsNew P1Stats() => MyPlayer.Instance?.inventory?.playerStats;

    private static bool Push(Il2CppSystem.Collections.Generic.Dictionary<EStat, float> table)
    {
        var ps = P1Stats();
        var inv = MyPlayer.Instance?.inventory;
        if (ps == null || table == null) return false;
        saved.Push(ps.stats);
        savedPassive.Push(inv?.passiveAbility);
        savedRaw.Push(ps.rawStats);
        savedStatus.Push(inv?.statusEffects);
        bool toP2 = p2Dict != null && table.Pointer == p2Dict.Pointer;
        // v0.44.1: P2's own power-up effects (P2PowerUps) - P1's status list stays P1's
        if (inv != null && P2PowerUps.Status != null)
            inv.statusEffects = toP2 ? P2PowerUps.Status : savedStatus.Last();
        ps.stats = table;
        if (toP2 && p2Raw != null) ps.rawStats = p2Raw;
        else if (!toP2 && savedRaw.Count > 0) ps.rawStats = savedRaw.Last();
        if (inv != null && P2Abilities.Passive != null)
            inv.passiveAbility = p2Dict != null && table.Pointer == p2Dict.Pointer ? P2Abilities.Passive : savedPassive.Last();
        return true;
    }

    private static void Pop()
    {
        if (saved.Count == 0) return;
        var ps = P1Stats();
        var prev = saved.Pop();
        var prevPassive = savedPassive.Count > 0 ? savedPassive.Pop() : null;
        var prevRaw = savedRaw.Count > 0 ? savedRaw.Pop() : null;
        var prevStatus = savedStatus.Count > 0 ? savedStatus.Pop() : null;
        var invS = MyPlayer.Instance?.inventory;
        if (invS != null && prevStatus != null) invS.statusEffects = prevStatus;
        if (ps != null) { ps.stats = prev; if (prevRaw != null) ps.rawStats = prevRaw; }
        var inv = MyPlayer.Instance?.inventory;
        if (inv != null && prevPassive != null) inv.passiveAbility = prevPassive;
    }

    private static int CountP2()
    {
        var ps = P1Stats();
        return ps != null && p2Dict != null && ps.stats != null && ps.stats.Pointer == p2Dict.Pointer ? 1 : 0;
    }

    public static bool EnterP2()
    {
        if (!Enabled || Weapons == null) return false;
        if (p2Dict == null) Recompute();
        return Push(p2Dict);
    }

    public static void Exit() => Pop();

    // The weapon decides the table: P2's weapon -> P2's; P1's weapon -> P1's (the bottom of the stack) if a P2 table
    // is active. __state: true = pushed, pop in the finalizer.
    private static bool Switch(WeaponBase w)
    {
        if (w == null || Weapons == null || p2Dict == null) return false;
        if (P2Weapons.IsP2(w)) return Push(p2Dict);
        if (CountP2() == 0 || saved.Count == 0) return false;
        return Push(saved.Last());   // the real P1 table is at the bottom of the stack
    }

    private static void ContextPrefixArg0(WeaponBase __0, out bool __state) => __state = Switch(__0);
    private static void ContextPrefixInstance(WeaponBase __instance, out bool __state) => __state = Switch(__instance);

    private static Exception ContextFinalizer(Exception __exception, bool __state)
    {
        if (__state) Pop();
        return __exception;
    }

    // ---------- Repairing P1 after P2 actions (no hooks) ----------
    // Adding to P2's weapon/tome holders fires the game's global "weapon added" / "tome upgraded" events, and P1's
    // weapon/tome HUD and always-on weapon manager (AuraAttacks) react to them as if they were P1's.
    // v0.17-0.18 skipped those handlers with temporary hooks. The crash dump (v0.18.3) showed the crash inside
    // Harmony's wrapper of InventoryHud.OnTomeAdded (DMD<InventoryHud::OnTomeAdded> -> il2cpp_runtime_invoke ->
    // ExecutionEngineException): "unpatching" leaves the wrapper in place, and calling the original through it crashes
    // the .NET runtime. So these handlers are never hooked now. Instead, when a P2 action is over (P2's starting weapon,
    // P2's level-up screen), P1's HUD is rebuilt and any always-on attack attached to P1 for a weapon P1 doesn't
    // have is removed - plain calls into the game, no wrappers.
    private static int actionDepth;

    public static void GuardsOn() => actionDepth++;

    public static void GuardsOff()
    {
        if (actionDepth == 0 || --actionDepth > 0) return;
        RepairP1();
    }

    private static void RepairP1()
    {
        var p1 = MyPlayer.Instance;
        var p1Weapons = p1?.inventory?.weaponInventory?.weapons;
        try
        {
            var hud = UnityEngine.Object.FindObjectOfType<InventoryHud>();
            int before = hud?.weaponContainers?.Count ?? -1;
            hud?.Refresh();
            ModLog.Info($"P1 HUD refreshed after a P2 action: weapon slots {before} -> {hud?.weaponContainers?.Count ?? -1} (P1 has {p1Weapons?.Count ?? -1} weapons)");
        }
        catch (Exception e) { LogOnce("P1 HUD refresh", e); }
        // v0.41.1 (autotest: P2 took Shield tomes and P1's max shield became 75 too): P1's health reacts to the stat
        // events a P2 action fires while P2's table is in place, and took P2's max values. P1's own values again
        // (P1's table - no P2 context here), HP and shield capped to them.
        try
        {
            var ph = p1?.inventory?.playerHealth;
            if (ph != null && saved.Count == 0)
            {
                float maxShieldBefore = ph.maxShield;
                int maxHpBefore = ph.maxHp;
                ph.UpdateMaxValues();
                foreach (var st in new[] { EStat.MaxHealth, EStat.Shield, EStat.HealthRegen }) ph.OnStatUpdate(st);
                if (ph.shield > ph.maxShield) ph.shield = ph.maxShield;
                if (ph.hp > ph.maxHp) ph.hp = ph.maxHp;
                if (Mathf.Abs(maxShieldBefore - ph.maxShield) > 0.01f || maxHpBefore != ph.maxHp)
                    ModLog.Info($"P1's health values back to P1's own after a P2 action: max shield {maxShieldBefore:F0} -> {ph.maxShield:F0}, max HP {maxHpBefore} -> {ph.maxHp}");
            }
        }
        catch (Exception e) { LogOnce("P1 health refresh", e); }
        try
        {
            var auras = p1?.playerConstantAttacks?.auras;
            if (auras == null || p1Weapons == null) return;
            var extra = new List<EWeapon>();
            foreach (var kv in auras) if (!p1Weapons.ContainsKey(kv.Key)) extra.Add(kv.Key);
            foreach (var w in extra)
            {
                var attack = auras[w];
                // P1's aegis slot pointed at that one too (P2's hits would use a destroyed shield)
                var aa = p1.playerConstantAttacks;
                if (attack != null && aa.aegisAttack != null && aa.aegisAttack.Pointer == attack.Pointer) aa.aegisAttack = null;
                if (attack != null) UnityEngine.Object.Destroy(attack.gameObject);
                auras.Remove(w);
                ModLog.Info($"Removed always-on attack {w} from P1 (it came from P2's weapon)");
            }
        }
        catch (Exception e) { LogOnce("P1 aura cleanup", e); }
    }

    // ---------- Each frame ----------

    public static void Update()
    {
        try { RememberOfferedTomes(); }
        catch (Exception e) { LogOnce("remember tomes", e); }
        if (Weapons == null) return;
        // Periodic refresh (P1's items/shop count for P2 too); not while a menu is open or a context is active
        if (Time.unscaledTime >= nextRecompute && !LevelupScreen.isLevelingUp && saved.Count == 0 && Time.timeScale > 0f)
        {
            statsDirty = true;
            RefreshWeaponStats();
        }
        try { CheckForNewPicks(); }
        catch (Exception e) { LogOnce("check picks", e); }
    }

    // Tome icons: remembered from the offer buttons of any open level-up screen
    private static readonly Dictionary<ETome, TomeData> tomeDataSeen = new();
    private static void RememberOfferedTomes()
    {
        if (!LevelupScreen.isLevelingUp) return;
        var buttons = UnityEngine.Object.FindObjectOfType<UpgradePicker>()?.buttons;
        if (buttons == null) return;
        foreach (var b in buttons)
        {
            var tome = b?.upgradable?.TryCast<TomeData>();
            if (tome != null) tomeDataSeen[tome.eTome] = tome;
        }
    }

    private static int lastWeaponCount = -1, lastWeaponLevels = -1, lastTomeLevels = -1;

    // P2's picks land in P2's holders; notice them by polling (no hooks on the pick path)
    private static void CheckForNewPicks()
    {
        int count = Weapons.weapons?.Count ?? 0, levels = 0;
        if (Weapons.weapons != null) foreach (var kv in Weapons.weapons) levels += kv.Value?.level ?? 0;
        if (count != lastWeaponCount || levels != lastWeaponLevels)
        {
            bool firstW = lastWeaponCount < 0;
            lastWeaponCount = count;
            lastWeaponLevels = levels;
            P2Weapons.Sync();        }

        int tomeLevels = 0;
        if (TomesInv.tomeLevels != null) foreach (var kv in TomesInv.tomeLevels) tomeLevels += kv.Value;
        if (tomeLevels == lastTomeLevels) return;
        bool first = lastTomeLevels < 0;
        lastTomeLevels = tomeLevels;
        foreach (var kv in TomesInv.tomeLevels)
        {
            if (Tomes.Any(t => t.eTome == kv.Key)) continue;
            // v0.41.4: the game's own tome data when no offer showed it first (autotest tomes were missing from P2's HUD)
            if (!tomeDataSeen.TryGetValue(kv.Key, out var data)) data = DataManager.Instance?.GetTome(kv.Key);
            if (data != null) Tomes.Add(data);
        }
        statsDirty = true;
        RefreshWeaponStats();
        LogStats("P2 tomes changed");
        if (!first)
            ModLog.Info($"P2 tomes: {string.Join(", ", Tomes.Select(t => $"{t.eTome} L{TomesInv.tomeLevels[t.eTome]}"))}; " +
                        $"max HP {MaxHp()}, damage x{Get(EStat.DamageMultiplier):F2}, attack speed {Get(EStat.AttackSpeed):F2}, size {Get(EStat.SizeMultiplier):F2}");
    }

    // P2's stats changed: let each P2 weapon recompute its values in P2's stat context
    private static void RefreshWeaponStats()
    {
        if (Weapons.weapons == null) return;
        if (statsDirty) Recompute();
        bool entered = EnterP2();
        try
        {
            // Only the stats the weapon has (its weaponStats): the game's UpdateStat throws KeyNotFound for any other
            // (v0.49.9 stage test log: ~280 'MaxHealth'/'Shield'/... errors per stage start)
            foreach (var kv in Weapons.weapons)
            {
                var ws = kv.Value?.weaponStats;
                if (ws == null) continue;
                foreach (EStat s in Enum.GetValues(typeof(EStat)))
                    if (ws.ContainsKey(s)) kv.Value.UpdateStat(s);
            }
        }
        catch (Exception e) { LogOnce("weapon stat refresh", e); }
        finally { if (entered) Exit(); }
    }

    private static void LogOnce(string what, Exception e)
    {
        if (loggedErrors.Add(what + e.Message)) ModLog.Error($"P2Inventory {what} failed: {e}");
    }
}
