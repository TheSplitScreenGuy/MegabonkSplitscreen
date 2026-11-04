using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Managers;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Player 2's health, and enemies treating P2 as a player.
//  - HP: P2's own pool; max HP from P2's own stats (P2Inventory: P2's character + tomes). No regen yet.
//    Kept across stages, reset when character select closes (new run).
//  - Targeting: in the game every enemy chases Enemy.target, a Rigidbody (P1's, set by Enemy.FindTarget).
//    P2's body gets a kinematic Rigidbody, and every RetargetInterval each enemy's target is set to the
//    closer living player (also right after the game's own FindTarget).
//  - Damage: the game damages P1 from MyPlayer.OnCollisionStay (touching an enemy). P2 has no MyPlayer, so the mod
//    checks for enemies touching P2 itself and subtracts EnemyData.damage, at most once per enemy per cooldown.
//  - At 0 HP P2 is down: hidden, no controls or weapons, enemies go back to P1 (revive rules: later).
internal static class P2Health
{
    private const float RetargetInterval = 0.25f;
    private const float FallbackEnemyCooldown = 0.5f;
    private const float ContactMargin = 0.25f;

    public static int Hp { get; private set; }
    public static int MaxHp { get; private set; }
    public static bool IsDead { get; private set; }

    private static GameObject body;
    private static Rigidbody rb;
    private static CharacterController cc;
    private static float nextRetarget;
    private static readonly Dictionary<IntPtr, float> enemyReadyAt = new();
    private static int enemyLayerMask = -1;
    private static int hitLogs, retargetLogs;
    private static bool runStarted;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Enemy), nameof(Enemy.FindTarget)),
            postfix: new HarmonyMethod(typeof(P2Health), nameof(OnFindTarget)));
        ModLog.Info("P2Health: enemy targeting hook applied");
    }

    // New run (character select closed): full health next time P2 spawns
    public static void ResetForNewRun() => runStarted = false;

    public static bool ReviveOnStart;

    public static void Start(GameObject p2Body, CharacterController p2cc)
    {
        body = p2Body;
        cc = p2cc;
        rb = body.GetComponent<Rigidbody>();
        if (rb == null) rb = body.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        enemyReadyAt.Clear();
        if (enemyLayerMask == -1)
        {
            int layer = LayerMask.NameToLayer("Enemy");
            enemyLayerMask = layer >= 0 ? 1 << layer : 0;
        }

        if (!runStarted)
        {
            runStarted = true;
            // P2's own max HP (P2's character + tomes, P2Inventory); P1's if P2's stats are off
            var ph = P1Health();
            MaxHp = P2Inventory.Weapons != null ? P2Inventory.MaxHp() : (ph != null && ph.maxHp > 0 ? ph.maxHp : 100);
            Hp = MaxHp;
            IsDead = false;
            P2GameHealth.Init();
            SyncFromGame();
        }
        if (IsDead && ReviveOnStart) Revive();   // down when the stage ended: back in the new one (Downed.OnStageStart)
        ReviveOnStart = false;
        if (IsDead) body.SetActive(false);
        ModLog.Info($"P2 health {Hp}/{MaxHp}{(IsDead ? " (down)" : "")}, enemy layer mask {enemyLayerMask}");
    }

    public static void Stop()
    {
        RetargetAll(toP1Only: true);
        flashUntil = -1f;
        UpdateFlash();
        body = null;
        rb = null;
        cc = null;
    }

    private static Assets.Scripts.Inventory__Items__Pickups.PlayerHealth P1Health()
    {
        var p1 = MyPlayer.Instance;
        return p1 != null && p1.inventory != null ? p1.inventory.playerHealth : null;
    }

    // Called every frame from P2Body.Update.
    // The game's health tick for P2 (regen, shield recharge, overheal) once per physics step, like P1's. 1.0 alpha
    // ([tickrate]: P1's PlayerHealth.Tick runs 50/s = the physics steps, frames were 140/s; user: the regen tome
    // didn't work right - P2's regen 50 healed 2.4x P1's): it ran once per frame before.
    public static void FixedTick()
    {
        if (body == null || !P2GameHealth.On || IsDead || Assets.Scripts.Utility.MyTime.paused) return;
        // [regen] log: P2's regen timer (healingTime) never got past one step (P1's ran up to healInterval and healed) -
        // something resets it between steps. The regen is timed here instead, with the game's own values for P2
        // (healInterval / healPerInterval from P2's stats) and the game's Heal; the game's own regen timer is held at 0
        // so it can't heal P2 as well. Tick still does the rest (shield recharge, overheal).
        P2GameHealth.Set("healingTime", 0f);
        P2GameHealth.Borrow("tick", h => h.Tick());
        float interval = P2GameHealth.Get<float>("healInterval"), perInterval = P2GameHealth.Get<float>("healPerInterval");
        if (interval > 0f && perInterval > 0f)
        {
            regenTimer += Time.fixedDeltaTime;
            if (regenTimer >= interval)
            {
                regenTimer = Mathf.Min(regenTimer - interval, interval);   // a shorter interval (new tome) heals once, not twice
                P2GameHealth.Borrow("regen", h => { if (h.CanHeal()) h.Heal(perInterval, false); });
            }
        }
        else regenTimer = 0f;
        AfterGameHealth("regen");
    }
    private static float regenTimer;

    public static void Update()
    {
        if (body == null) return;
        UpdateFlash();
        if (P2GameHealth.On)
        {
            // The game's own health code for P2: regen, shield recharge, overheal (Tick); max values from P2's stats
            if (!IsDead && Time.deltaTime > 0f && !Assets.Scripts.Utility.MyTime.paused)
            {
                // v0.44.4 (user: the Shield tome didn't fill P2's shield bar): a change in P2's health stats (a tome)
                // is taken over at once instead of up to 1 s later; the fill itself is the game's recharge rule
                float statKey = P2Inventory.Get(EStat.Shield) + 1000f * P2Inventory.Get(EStat.MaxHealth) + 0.001f * P2Inventory.Get(EStat.HealthRegen);
                if (statKey != lastHealthStatKey) { lastHealthStatKey = statKey; nextMaxUpdate = 0f; }
                // v0.49.1 (test: P2's Shield tome gave no shield with P2's test health on): the max values follow P2's
                // stats in test health too - only max HP is held at the test value
                if (Time.time >= nextMaxUpdate)
                {
                    nextMaxUpdate = Time.time + 1f;
                    // v0.37.3 (user: shield and regen tomes did nothing for P2; log: max shield stayed 0): the game's own
                    // stat-change handler for the health stats (UpdateRegenValues(0f) may have forced regen to 0)
                    P2GameHealth.Borrow("max values", h =>
                    {
                        int hpWas = h.hp;
                        h.UpdateMaxValues();
                        foreach (var st in new[] { EStat.MaxHealth, EStat.Shield, EStat.HealthRegen }) h.OnStatUpdate(st);
                        // the game's update caps HP to the real max: test health keeps its own
                        if (testHealth) { h.maxHp = TestHealth; h.hp = Mathf.Clamp(hpWas, 1, TestHealth); }
                        LogHealthStats(h);
                    });
                }
                if (testHealth) P2GameHealth.Set("maxHp", TestHealth);
                // (the tick itself: FixedTick, like P1's)
            }
            WatchP1Shield(P1Health());
        }
        else
        {
            if (testHealth) MaxHp = TestHealth;   // hits still count down visibly
            else SyncMaxHp();
            if (!Assets.Scripts.Utility.MyTime.paused) UpdateShield(Time.deltaTime);
        }
        if (Time.deltaTime <= 0f || Assets.Scripts.Utility.MyTime.paused) return;
        float now = Time.time;
        if (now >= nextRetarget)
        {
            nextRetarget = now + RetargetInterval;
            RetargetAll(toP1Only: IsDead);
        }
        if (!IsDead) CheckContacts(now);
    }

    // TEMP: each link from P2's stat to P2's health numbers, logged when something changes: P2's table (mod) / the
    // game's lookup during the borrow / P2's health fields after the game's update
    private static string lastHealthStats;
    private static int healthStatLogs;
    private static void LogHealthStats(Assets.Scripts.Inventory__Items__Pickups.PlayerHealth h)
    {
        if (healthStatLogs >= 15) return;
        var ps = Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory?.playerStats;
        string game = "?";
        try { game = $"{ps?.GetStat(EStat.Shield):F1}/{ps?.GetStat(EStat.HealthRegen):F2}/static {Assets.Scripts.Inventory.Stats.PlayerStats.GetStat(EStat.Shield):F1}"; }
        catch (Exception e) { game = "error " + e.Message; }
        string now = $"P2 table Shield {P2Inventory.Get(EStat.Shield):F1} HealthRegen {P2Inventory.Get(EStat.HealthRegen):F2}; game lookup (Shield/Regen) {game}; " +
                     $"health maxShield {h.maxShield:F1} shield {h.shield:F1} maxHp {h.maxHp}, healingPerMinute {h.healingPerMinute:F2} healPerInterval {h.healPerInterval:F2} healInterval {h.healInterval:F2} healingValue {h.healingValue:F2}";
        if (now == lastHealthStats) return;
        lastHealthStats = now;
        healthStatLogs++;
        ModLog.Info($"[hpstats] {now}");
    }

    // TEST (user): F2 toggles 1,000,000 health for P1. The game recomputes P1's max HP from P1's stats now and then,
    // so it's held every frame while on; off = the game's own max again (HP capped to it).
    private static bool p1TestHealth;
    public static void SetP2TestHealth(bool on)   // autotest: like F1
    {
        if (testHealth != on) ToggleTestHealth();
    }

    // Autotest only (godp1 / kill p1); the F1/F2 keys were taken out for the 1.0 alpha (user: remove the F commands)
    public static void SetP1TestHealth(bool on)
    {
        p1TestHealth = on;
        var ph = P1Health();
        if (on && ph != null) { ph.maxHp = TestHealth; ph.hp = TestHealth; }
    }
    public static void UpdateP1TestHealth()
    {
        var ph = P1Health();
        // v0.37.6 (user: F2 didn't seem to work): the game's stat refresh puts P1's max back AND caps P1's HP to it, so
        // both are held every frame (P1 can't die while on); P1's health bar is told when it had to be put back
        // v0.49 (item sweep: P1's HP never moved, so P1's healing items couldn't be compared with P2's): like P2's test
        // health now - the high max is held, HP is free to go down and up; put back where it was when the game's
        // refresh capped it, and refilled only below half (P1 still can't die)
        if (p1TestHealth && ph != null)
        {
            bool wasMax = ph.maxHp != TestHealth;
            if (wasMax) { ph.maxHp = TestHealth; ph.hp = p1LastHp > 0 ? p1LastHp : TestHealth; RefreshP1Bar(ph); }
            else if (ph.hp < TestHealth / 2) ph.hp = TestHealth;
            p1LastHp = ph.hp;
        }
        else p1LastHp = 0;
    }
    private static int p1LastHp;

    private static void RefreshP1Bar(Assets.Scripts.Inventory__Items__Pickups.PlayerHealth ph)
    {
        if (ph == null) return;
        try { Assets.Scripts.Inventory__Items__Pickups.PlayerHealth.A_MaxValuesChanged?.Invoke(ph); } catch { }
    }

    // P2's max HP follows P2's own stats (e.g. an HP tome); a raise also heals by the same amount, like P1's bar
    private const int TestHealth = 1_000_000;
    private static bool testHealth;
    private static float nextMaxUpdate, lastHealthStatKey = -1f;

    // ---------- With the game's health code (P2GameHealth) ----------
    private static void SyncFromGame()
    {
        if (!P2GameHealth.On) return;
        Hp = P2GameHealth.Get<int>("hp");
        MaxHp = P2GameHealth.Get<int>("maxHp");
        Shield = P2GameHealth.Get<float>("shield");
        MaxShield = Mathf.RoundToInt(P2GameHealth.Get<float>("maxShield"));
    }

    // After a borrowed call: P2's new numbers, the hit's flash/popup if P2 lost something, P2 down at 0
    private static void AfterGameHealth(string source, int hpBefore = -1, float shieldBefore = -1f)
    {
        SyncFromGame();
        if (hpBefore >= 0)
        {
            int lost = Mathf.RoundToInt(hpBefore + shieldBefore - Hp - Shield);
            if (lost > 0)
            {
                Flash();
                // The game's own popup (red hp / blue shield) when its EffectManager heard P2's hit; else the mod's
                if (!P2GameHealth.GamePopups) P2Popups.Show(lost);
                if (hitLogs++ < 8) ModLog.Info($"P2 hit by {source} for {lost} (game rules): {Hp}/{MaxHp}, shield {Shield:F0}/{MaxShield}");
            }
        }
        if (Hp <= 0 && !IsDead) GoDown(source);
    }

    // A hit through the game's code; false = not handled (then the mod's own rules apply)
    private static bool GameHit(string source, Action<Assets.Scripts.Inventory__Items__Pickups.PlayerHealth> call)
    {
        if (!P2GameHealth.On || IsDead || body == null) return false;
        int hp0 = Hp;
        float sh0 = Shield;
        if (!P2GameHealth.Borrow(source, call)) return false;
        AfterGameHealth(source, hp0, sh0);
        return true;
    }

    // An enemy special attack (boss spikes, slams...) hits P2: the game's own external hit with the attack's damage and
    // knockback (P2's movement copy takes the push), else the mod's damage and a push
    public static void SpecialAttackHit(string source, float damage, float knockback, Vector3 dir, Assets.Scripts.Actors.Enemies.Enemy enemy)
    {
        if (IsDead || body == null) return;
        // the game's knockback in DamagePlayerExternal goes to P1's movement ([bossatk] v0.49.10: P2's height never
        // changed), so damage with no knockback there and P2's own push (P1's velocity jumped by exactly dir * knockback)
        if (!GameHit(source, h => h.DamagePlayerExternal(damage, 0f, dir, false, source, default(Assets.Scripts.Actors.DcFlags),
                default(Assets.Scripts.Game.Combat.EDamageEffect), enemy)))
            TakeDamage(Mathf.Max(1, Mathf.RoundToInt(damage)), source);
        if (knockback > 0f && !IsDead) P2Movement.Push(dir * knockback);
    }

    // P2's status tick while a damaging debuff is on (P2PowerUps): the tick's damage goes to "the player" - run on P2's
    // borrowed health (debuffs_each test v0.49.11: P2's Bleed/Poison/BossPoison hurt P1 and not P2)
    public static bool StatusTick(Action tick) => GameHit("debuff", _ => tick());

    // A game hit meant for "the player" run as P2's: P2's borrowed health and status list (debuffs land on P2's), and
    // P2's movement copy as PlayerMovement.Instance (the knockback pushes P2). P2BossAttacks: Bark Vader's orbs.
    public static bool AsP2Hit(string source, Action hit)
    {
        var p1Pm = PlayerMovement.Instance;
        try
        {
            if (P2Movement.Pm != null) PlayerMovement.Instance = P2Movement.Pm;
            return GameHit(source, _ => P2PowerUps.OnStatusSwapped(hit));
        }
        finally { if (p1Pm != null) PlayerMovement.Instance = p1Pm; }
    }

    // P2's fall damage through the game's own rule (PlayerHealth.OnPlayerLanded)
    public static bool GameLanded(float speed) => GameHit("fall", h => h.OnPlayerLanded(speed));

    private static void ToggleTestHealth()
    {
        testHealth = !testHealth;
        if (P2GameHealth.On)
        {
            if (testHealth) { P2GameHealth.Set("maxHp", TestHealth); P2GameHealth.Set("hp", TestHealth); }
            else
            {
                P2GameHealth.Borrow("max values", h => h.UpdateMaxValues());
                P2GameHealth.Set("hp", Mathf.Min(P2GameHealth.Get<int>("hp"), P2GameHealth.Get<int>("maxHp")));
            }
            SyncFromGame();
            ModLog.Info($"P2 test health {(testHealth ? "ON" : "OFF")} ({Hp}/{MaxHp})");
            return;
        }
        if (testHealth)
        {
            MaxHp = Hp = TestHealth;
            ModLog.Info("F1: P2 test health ON (1,000,000)");
        }
        else
        {
            MaxHp = 0;   // SyncMaxHp sets the real max again
            SyncMaxHp();
            Hp = Mathf.Min(Hp, MaxHp);
            ModLog.Info($"P2 test health OFF ({Hp}/{MaxHp})");
        }
    }

    private static void SyncMaxHp()
    {
        if (P2Inventory.Weapons == null) return;
        int max = P2Inventory.MaxHp();
        if (max == MaxHp) return;
        int diff = max - MaxHp;
        MaxHp = max;
        if (!IsDead) Hp = Mathf.Clamp(Hp + Mathf.Max(0, diff), 0, MaxHp);
        ModLog.Info($"P2 max HP now {MaxHp} ({(diff >= 0 ? "+" : "")}{diff}), HP {Hp}");
    }

    // ---------- Targeting ----------

    private static void OnFindTarget(Enemy __instance)
    {
        if (body == null || IsDead || rb == null) return;
        Retarget(__instance, P1Rb());
    }

    private static Rigidbody P1Rb()
    {
        var pm = PlayerMovement.Instance;
        return pm != null ? pm.rb : null;
    }

    private static void RetargetAll(bool toP1Only)
    {
        var em = EnemyManager.Instance;
        var p1rb = P1Rb();
        if (em == null || em.enemies == null || p1rb == null) return;
        int toP2 = 0, total = 0;
        foreach (var kv in em.enemies)
        {
            var e = kv.Value;
            if (e == null) continue;
            total++;
            if (toP1Only || rb == null)
            {
                if (e.target != null && rb != null && e.target.Pointer == rb.Pointer) e.target = p1rb;
                continue;
            }
            if (Retarget(e, p1rb)) toP2++;
        }
        if (!toP1Only && retargetLogs < 5 && total > 0 && toP2 > 0)
        {
            retargetLogs++;
            ModLog.Info($"Enemies: {toP2} of {total} now chase P2");
        }
    }

    // Points the enemy at the closer player. Returns true if that's P2.
    private static bool Retarget(Enemy e, Rigidbody p1rb)
    {
        if (p1rb == null || rb == null) return false;
        Vector3 pos = e.transform.position;
        // P1 down (Downed): everyone goes for P2
        bool p2Closer = Downed.P1Down || (rb.position - pos).sqrMagnitude < (p1rb.position - pos).sqrMagnitude;
        var want = p2Closer ? rb : p1rb;
        if (e.target == null || e.target.Pointer != want.Pointer) e.target = want;
        return p2Closer;
    }

    // ---------- Damage ----------

    // TEMP autotest switch ('fix timefreeze off')
    public static bool TimeFreezeFix = true;
    private static int freezeLogs;

    private static void CheckContacts(float now)
    {
        if (cc == null || enemyLayerMask == 0) return;
        // 1.0.2 (players: "when a time freeze power up is used, p2 still takes damage from physically touching enemies"):
        // the game's frozen enemies don't hurt anyone (GameManager.IsTimeFreeze) - P2's touches are the mod's own check
        bool frozen = false;
        try { frozen = GameManager.Instance != null && GameManager.Instance.IsTimeFreeze(); } catch { }
        if (frozen && TimeFreezeFix)
        {
            if (freezeLogs++ < 3) ModLog.Info("[freeze] time freeze on: no enemy touches for P2");
            return;
        }
        var t = body.transform;
        float scale = t.lossyScale.y;
        float r = cc.radius * t.lossyScale.x + ContactMargin;
        Vector3 bottom = t.TransformPoint(cc.center) - Vector3.up * (cc.height * scale / 2f - cc.radius * scale);
        Vector3 top = t.TransformPoint(cc.center) + Vector3.up * (cc.height * scale / 2f - cc.radius * scale);
        var em = EnemyManager.Instance;
        if (em == null) return;

        float cooldown = MyPlayer.Instance != null && MyPlayer.Instance.enemyAttackCooldown > 0f
            ? MyPlayer.Instance.enemyAttackCooldown : FallbackEnemyCooldown;

        foreach (var col in Physics.OverlapCapsule(bottom, top, r, enemyLayerMask))
        {
            if (col == null || !em.collidersToEnemies.ContainsKey(col)) continue;
            var e = em.collidersToEnemies[col];
            if (e == null || e.enemyData == null || e.hp <= 0f) continue;
            if (enemyReadyAt.TryGetValue(e.Pointer, out float ready) && now < ready) continue;
            enemyReadyAt[e.Pointer] = now + cooldown;
            // The game's own enemy hit (armor, evasion, shield, thorns, damage cooldown, knockback), else the mod's
            var enemy = e;
            Vector3 dir = body.transform.position - e.transform.position;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
            if (!GameHit(e.enemyData.enemyName.ToString(), h => h.DamagePlayer(enemy, dir, default(Assets.Scripts.Actors.DcFlags))))
                TakeDamage(Mathf.Max(1, e.enemyData.damage), e.enemyData.enemyName.ToString());
            if (IsDead) return;
        }
    }

    // ---------- Hit flash ----------
    // P1's model flashes on a hit (PlayerRenderer.OnDamage swaps in its damageFlash material for a moment).
    // P2 has no PlayerRenderer, so the mod does the same with P1's damageFlash material.
    private const float FlashTime = 0.12f;
    private static float flashUntil = -1f;
    private static readonly List<(Renderer r, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> mats)> flashed = new();
    private static bool flashLogged;

    private static void Flash()
    {
        var pr = MyPlayer.Instance != null ? MyPlayer.Instance.playerRenderer : null;
        var flashMat = pr != null ? pr.damageFlash : null;
        if (flashMat == null || body == null) return;
        if (flashed.Count == 0)
        {
            // v0.44.4 (user: the shield outline stayed after the shield broke): the outline's materials sit in these
            // lists while it's on - saved here, the flash put them back after the outline had taken them off. It's
            // taken off first (back after the flash while P2 still has shield).
            P2Body.HideShieldOutline();
            foreach (var r in body.GetComponentsInChildren<Renderer>(true))
            {
                // v0.43.3 (user: P2's hit flash turned the Chunkers and the fire breath red): only P2's character, like
                // P1's PlayerRenderer - not P2's always-on weapons, the mod's effect objects, or particles/lines/trails
                if (r == null || !IsCharacterRenderer(r)) continue;
                var orig = r.sharedMaterials;
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(orig.Length);
                for (int i = 0; i < orig.Length; i++) arr[i] = flashMat;
                flashed.Add((r, orig));
                r.sharedMaterials = arr;
            }
            if (!flashLogged)
            {
                flashLogged = true;
                ModLog.Info($"P2 hit flash: material '{flashMat.name}' on {flashed.Count} renderers");
            }
        }
        flashUntil = Time.time + FlashTime;
    }

    public static bool Flashing => flashed.Count > 0;

    public static bool IsCharacterRenderer(Renderer r)
    {
        if (r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<LineRenderer>() != null || r.TryCast<TrailRenderer>() != null) return false;
        if (r.GetComponentInParent<Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack>() != null) return false;
        for (var t = r.transform; t != null && t != body.transform; t = t.parent)
            if (t.name.StartsWith("MegabonkSplitscreen_")) return false;
        return true;
    }

    private static void UpdateFlash()
    {
        if (flashed.Count == 0 || Time.time < flashUntil) return;
        foreach (var (r, mats) in flashed)
            if (r != null) r.sharedMaterials = mats;
        flashed.Clear();
    }

    // ---------- Shield (user: P2 needs one) ----------
    // Size from P2's own Shield stat; hits take it down before HP; it refills after a pause without hits.
    // The game's refill (P1's PlayerHealth: shieldRegenCooldownTime, shieldHealingPerTick, healInterval) isn't
    // measured yet - TEMP stand-in: after the cooldown (P1's value, else 3 s) it refills to full over 4 s; P1's shield
    // over time is logged ([shield]) to match it.
    public static int MaxShield { get; private set; }
    public static float Shield { get; private set; }
    private static float lastHitAt = -100f;
    private const float StandInRefillTime = 4f;

    private static void UpdateShield(float dt)
    {
        int max = Mathf.Max(0, Mathf.RoundToInt(P2Inventory.Get(Assets.Scripts.Menu.Shop.EStat.Shield)));
        if (max != MaxShield)
        {
            if (max > MaxShield) Shield += max - MaxShield;   // a new shield point arrives filled
            MaxShield = max;
            ModLog.Info($"P2 max shield now {MaxShield}");
        }
        Shield = Mathf.Clamp(Shield, 0f, MaxShield);
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        float cooldown = ph != null && ph.shieldRegenCooldownTime > 0f ? ph.shieldRegenCooldownTime : 3f;
        if (!IsDead && MaxShield > 0 && Shield < MaxShield && Time.time - lastHitAt > cooldown)
            Shield = Mathf.Min(MaxShield, Shield + MaxShield / StandInRefillTime * dt);
        WatchP1Shield(ph);
    }

    // TEMP: P1's shield over time, to copy the game's refill
    private static float p1LastShield = -1f, nextShieldLog;
    private static int shieldLogs;
    private static void WatchP1Shield(Assets.Scripts.Inventory__Items__Pickups.PlayerHealth ph)
    {
        if (ph == null || shieldLogs >= 30 || Time.time < nextShieldLog) return;
        nextShieldLog = Time.time + 0.25f;
        if (Mathf.Abs(ph.shield - p1LastShield) < 0.01f) return;
        p1LastShield = ph.shield;
        shieldLogs++;
        ModLog.Info($"[shield] P1 shield {ph.shield:F2}/{ph.maxShield:F2} at {Time.time:F2}; regen cooldown {ph.shieldRegenCooldownTime:F2}, " +
                    $"recharge at {ph.shieldRechargeAtTime:F2}, healing per tick {ph.shieldHealingPerTick:F3}, value {ph.shieldHealingValue:F3}, interval {ph.healInterval:F2}");
    }

    internal static void TakeDamage(int amount, string source)
    {
        if (IsDead || body == null) return;
        // Explosions etc.: the game's external-damage rule (armor, shield...) when the game's health code runs P2's
        float dmg = amount;
        if (GameHit(source, h => h.DamagePlayerExternal(dmg, 0f, Vector3.zero, false, source, default(Assets.Scripts.Actors.DcFlags),
                default(Assets.Scripts.Game.Combat.EDamageEffect), null))) return;
        Flash();
        P2Popups.Show(amount);
        lastHitAt = Time.time;
        if (Shield > 0f)
        {
            int absorbed = Mathf.Min(amount, Mathf.FloorToInt(Shield));
            Shield -= absorbed;
            amount -= absorbed;
            if (amount <= 0) return;
        }
        Hp = Mathf.Max(0, Hp - amount);
        if (hitLogs++ < 8) ModLog.Info($"P2 hit by {source} for {amount}: {Hp}/{MaxHp}");
        if (Hp > 0) return;
        GoDown(source);
    }

    private static void GoDown(string source)
    {
        IsDead = true;
        DownAt = body.transform.position;
        ModLog.Info($"P2 is down (killed by {source}) at {DownAt}");
        var p1 = MyPlayer.Instance;
        Gravestones.Place(1, DownAt, p1 != null ? p1.transform.position - DownAt : body.transform.forward);   // face towards P1
        body.SetActive(false);
        RetargetAll(toP1Only: true);
        P2Weapons.OnP2Down();
        Downed.OnP2Down();   // P1 down too: the run ends
    }

    public static Vector3 DownAt { get; private set; }

    // Revive's rise out of the portal: P2's body shows while P2 is still down
    public static void ShowForRise() { if (body != null) body.SetActive(true); }

    // Back in action (Downed.Revive): full HP (v0.46.2, user: they should be fully healed), P2's body where it is now
    public static void Revive()
    {
        if (!IsDead || body == null) return;
        Hp = Mathf.Max(1, MaxHp);
        if (P2GameHealth.On) { P2GameHealth.Set("hp", Hp); SyncFromGame(); }
        IsDead = false;
        body.SetActive(true);
        ModLog.Info($"P2 revived with {Hp}/{MaxHp} HP");
    }
}
