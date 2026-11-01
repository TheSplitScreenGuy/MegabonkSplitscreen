using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.GoldAndMoney;
using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using Inventory__Items__Pickups.Xp_and_Levels;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Player 2 collecting XP gems.
// In the game P1 has a pickup sphere (DetectPickups); gems inside it start flying to P1 (Pickup.StartFollowingPlayer)
// and on arrival Pickup.ApplyPickup hands them to P1's inventory (XP -> P1's PlayerXp.AddXp).
//  - Attract: every AttractInterval, XP gems within P2's pickup radius (same as P1's sphere) that aren't already
//    flying to someone are sent to P2 with the game's own StartFollowingPlayer, and remembered as P2's.
//  - Keep: if P1's sphere later tries to take a gem that is flying to P2, that call is skipped.
//  - Collect: while a P2 gem is applied, P1's PlayerXp.AddXp is skipped and the value goes to P2's XP instead.
//    If a P2 gem reaches P2 but the game never applies it (it may only apply on touching P1), the mod applies it.
// Gold coins too (v0.35.0, separate gold): their value moves to P2's purse (P2Gold). Other pickups stay with P1.
internal static class P2Pickups
{
    private const float AttractInterval = 0.1f;
    private const float ArriveDistance = 1.2f;

    private static GameObject body;
    private static float nextAttract;
    private static float radius = -1f;          // P1's sphere when P2 spawned (fallback if the sphere is gone)
    private static SphereCollider p1Sphere;     // P1's pickup sphere, read live
    private static readonly Dictionary<IntPtr, Pickup> p2Gems = new();
    private static bool applyingForP2;
    private static int pendingValue;
    private static int logs;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Pickup), nameof(Pickup.StartFollowingPlayer)),
            prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeFollow)));
        harmony.Patch(AccessTools.Method(typeof(Pickup), nameof(Pickup.ApplyPickup)),
            prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeApply)),
            postfix: new HarmonyMethod(typeof(P2Pickups), nameof(AfterApply)));
        harmony.Patch(AccessTools.Method(typeof(PlayerXp), nameof(PlayerXp.AddXp)),
            prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeAddXp)));
        harmony.Patch(AccessTools.Method(typeof(PlayerInventory), nameof(PlayerInventory.AddXp)),
            prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeAddXp)));
        var cg = AccessTools.Method(typeof(PlayerInventory), nameof(PlayerInventory.ChangeGold));
        var pullAll = AccessTools.DeclaredMethod(typeof(PickupManager), nameof(PickupManager.PickupAllXp));
        if (pullAll != null && P2Weapons.IsNativeCodeUnique(pullAll) && !P2Weapons.IsNativeCodeHooked(pullAll))
            harmony.Patch(pullAll, prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforePullAll)),
                finalizer: new HarmonyMethod(typeof(P2Pickups), nameof(AfterPullAll)));
        else ModLog.Warn("P2Pickups: PickupManager.PickupAllXp shares native code, not hooked (shrine of succ stays P1's)");
        bool goldHooked = cg != null && P2Weapons.IsNativeCodeUnique(cg) && !P2Weapons.IsNativeCodeHooked(cg);
        if (goldHooked) harmony.Patch(cg, prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeChangeGold)));
        else ModLog.Warn("P2Pickups: ChangeGold not hookable (shared native code)");
        // v0.37.1 log: no gold coin ever came near P2 - kill gold goes straight into the purse (ChangeGold(1) per kill)
        // while the enemy dies. The dying enemy nearer P2 -> P2's kill -> P2's gold.
        if (goldHooked)
        {
            int n = 0;
            foreach (var m in typeof(Assets.Scripts.Actors.Enemies.Enemy).GetMethods().Where(x => x.Name == "EnemyDied" && x.DeclaringType == typeof(Assets.Scripts.Actors.Enemies.Enemy)))
            {
                if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2Pickups: Enemy.EnemyDied({m.GetParameters().Length} args) shares native code, not hooked"); continue; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeEnemyDied)),
                    finalizer: new HarmonyMethod(typeof(P2Pickups), nameof(AfterEnemyDied)));
                n++;
            }
            ModLog.Info($"P2Pickups: kill gold by the nearer player ({n} death methods hooked)");
            // v0.37.3 log: ChangeGold never ran during a death - kill gold flies to the player as MoneyFlying objects
            // (MoneyUtility.flyingMoneyDict) and pays on arrival (MoneyFlying.Pickup)
            var pick = AccessTools.DeclaredMethod(typeof(MoneyFlying), "Pickup");
            if (pick != null && P2Weapons.IsNativeCodeUnique(pick) && !P2Weapons.IsNativeCodeHooked(pick))
            {
                harmony.Patch(pick, prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeMoneyPickup)),
                    finalizer: new HarmonyMethod(typeof(P2Pickups), nameof(AfterMoneyPickup)));
                ModLog.Info("P2Pickups: flying money near P2 goes to P2 (MoneyFlying.Pickup hooked)");
            }
            else ModLog.Warn("P2Pickups: MoneyFlying.Pickup shares native code, not hooked - flying money stays P1's");
            // v0.37.7 log: Pickup never ran (not even for P1's money) - the payment happens inside the money's own
            // Update/FixedUpdate. While P2's money runs those, its payment is P2's.
            foreach (var name in new[] { "Update", "FixedUpdate" })
            {
                var m = AccessTools.DeclaredMethod(typeof(MoneyFlying), name);
                if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2Pickups: MoneyFlying.{name} shares native code, not hooked"); continue; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Pickups), nameof(BeforeMoneyPickup)),
                    finalizer: new HarmonyMethod(typeof(P2Pickups), nameof(AfterMoneyPickup)));
                ModLog.Info($"P2Pickups: MoneyFlying.{name} hooked (P2's money pays P2)");
            }
        }
        ModLog.Info("P2Pickups: pickup hooks applied");
    }

    public static void Start(GameObject p2Body)
    {
        body = p2Body;
        p2Gems.Clear();
        var dp = Object.FindObjectOfType<DetectPickups>();
        var sc = dp != null ? dp.collider : null;
        p1Sphere = sc;
        radius = sc != null ? sc.radius * Mathf.Max(sc.transform.lossyScale.x, 0.01f) : 4f;
        ModLog.Info($"P2 pickup radius {radius:F1} (P1's sphere {(sc != null ? "found" : "not found, default")})");
    }

    public static void Stop()
    {
        body = null;
        p2Gems.Clear();
    }

    // Called every unpaused frame from P2Control.
    public static void Update()
    {
        if (body == null || P2Health.IsDead) return;
        float now = Time.time;
        Vector3 p2 = body.transform.position + Vector3.up;   // roughly P2's middle

        try { UpdateMoney(); }
        catch (Exception e) { if (moneyErrors++ < 3) ModLog.Warn($"[gold] flying money update failed: {e.Message}"); }

        if (now >= nextAttract)
        {
            nextAttract = now + AttractInterval;
            Attract(p2);
        }

        // Fallback: collect P2 gems that reached P2
        var arrived = new List<Pickup>();
        foreach (var kv in p2Gems)
        {
            var p = kv.Value;
            if (p == null || !p.gameObject.activeInHierarchy || p.pickedUp) continue;
            if ((p.transform.position - p2).sqrMagnitude < ArriveDistance * ArriveDistance) arrived.Add(p);
        }
        foreach (var p in arrived)
        {
            if (logs++ < 3) ModLog.Info("P2 gem arrived but wasn't applied by the game; applying it");
            p.ApplyPickup();
        }
    }

    private static void Attract(Vector3 p2)
    {
        var pm = PickupManager.Instance;
        if (pm == null) return;
        // P1's sphere grows with P1's PickupRange stat (DetectPickups.UpdateRadius): P2's radius = P1's sphere NOW scaled
        // by P2's / P1's PickupRange (user: every tome effect should work for P2 - the attraction tome).
        // 1.0 alpha+ (players: "P2 can't grab pick-ups or XP"): the sphere was measured once, when P2 spawned, then divided
        // by P1's CURRENT range - each pickup-range upgrade of P1's shrank P2's radius (P1 x3 range = P2 a third).
        float sphere = p1Sphere != null ? p1Sphere.radius * Mathf.Max(p1Sphere.transform.lossyScale.x, 0.01f) : radius;
        float r = sphere;
        var p1Stats = Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory?.playerStats;
        float p1Range = p1Stats != null && !P2Inventory.InP2Context ? p1Stats.GetStat(Assets.Scripts.Menu.Shop.EStat.PickupRange) : 0f;
        float p2Range = P2Inventory.Get(Assets.Scripts.Menu.Shop.EStat.PickupRange);
        if (p1Range > 0.01f && p2Range > 0.01f) r = sphere * p2Range / p1Range;
        lastP2Radius = r;
        if (rangeLogs < 3 && Mathf.Abs(r - lastLoggedRange) > 0.5f) { rangeLogs++; lastLoggedRange = r; ModLog.Info($"P2 pickup radius {r:F1} (P1's sphere now {sphere:F1}, PickupRange P1 {p1Range:F2} / P2 {p2Range:F2})"); }
        foreach (var col in Physics.OverlapSphere(p2, r, pm.whatIsPickups.value, QueryTriggerInteraction.Collide))
        {
            if (col == null) continue;
            var p = col.GetComponent<Pickup>();
            if (p == null && col.transform.parent != null) p = col.transform.parent.GetComponent<Pickup>();
            // TEMP (user: P2 gains no gold): how gold coins near P2 look
            if (p != null && p.ePickup == EPickup.Gold && goldSeenLogs < 12 && goldSeen.Add(p.Pointer))
            {
                goldSeenLogs++;
                string tgt = p.target == null ? "none" : (body != null && p.target.Pointer == body.transform.Pointer ? "P2" : p.target.name);
                ModLog.Info($"[gold] coin near P2: value {p.GetValue()}, picked up {p.pickedUp}, flying to {tgt}, can pick up {p.CanPickup()}");
            }
            // XP gems and gold coins (user: separate gold per player)
            // (v0.44.1: power-ups too - user: P2 should collect the heart, magnet, clock...)
            if (p == null || !Collectable(p) || p.pickedUp || p.target != null || !P2CanTake(p)) continue;
            p2Gems[p.Pointer] = p;
            p.StartFollowingPlayer(body.transform);
            P2Items.ArmXpOrb(p);
            if (logs++ < 3) ModLog.Info($"P2 attracts XP gem worth {p.GetValue()}");
        }
    }

    // ---------- "Pull all XP" for P2 (shrine of succ used by P2) ----------
    private static float pullUntil = -1f;
    private static int pullDepth, pulled;
    public static void PullForP2(float seconds)
    {
        pullUntil = Time.time + seconds;
        ModLog.Info($"[succ] P2 used the shrine of succ: the next pull-all-XP within {seconds:F0} s goes to P2");
    }

    // A game call that pulls gems to "the player" run as P2's pull: every gem it sends off flies to P2 and is P2's
    // (P2's magnet - P2PowerUps)
    public static void PullAsP2(string what, Action pull)
    {
        if (body == null || P2Health.IsDead) { pull(); return; }
        pullDepth++;
        pulled = 0;
        try { pull(); }
        finally
        {
            pullDepth--;
            ModLog.Info($"[succ] {what} for P2: {pulled} gems sent to P2");
        }
    }

    // Pots P2 broke (1.0 alpha, user: their XP went to P1). pot_p2 test: the drops appear a moment later and start
    // flying to "the player" - neither P2's Interact call nor the pot's SpawnStuff saw them. So a pickup that starts
    // following P1 right next to a pot P2 just broke goes to P2 (BeforeFollow).
    private static readonly List<(Vector3 pos, float until)> p2PotSpots = new();
    private const float PotDropRadius = 4f, PotDropTime = 4f;
    public static void PotBrokenByP2(Component pot)
    {
        if (pot == null) return;
        p2PotSpots.RemoveAll(s => Time.time > s.until);
        p2PotSpots.Add((pot.transform.position, Time.time + PotDropTime));
    }
    private static bool NearP2Pot(Vector3 p)
    {
        foreach (var s in p2PotSpots)
            if (Time.time <= s.until && (s.pos - p).sqrMagnitude <= PotDropRadius * PotDropRadius) return true;
        return false;
    }
    // __state: 1 = P2's pull, 2 = P1's (P1's magnet / shrine: those gems stay P1's - user: whoever takes the magnet gets all)
    private static int p1PullDepth;
    private static void BeforePullAll(out int __state)
    {
        bool p2s = body != null && Time.time < pullUntil;
        if (!p2s) { __state = 2; p1PullDepth++; return; }
        __state = 1;
        pullDepth++;
        pulled = 0;
        pullUntil = -1f;
    }

    private static Exception AfterPullAll(Exception __exception, int __state)
    {
        if (__state == 1)
        {
            pullDepth--;
            ModLog.Info($"[succ] pull-all-XP for P2: {pulled} gems sent to P2");
        }
        else if (__state == 2 && p1PullDepth > 0) p1PullDepth--;
        return __exception;
    }

    // P2's pickup radius as last worked out (Attract)
    private static float lastP2Radius = -1f;
    private static bool Collectable(Pickup p) => p.ePickup == EPickup.Xp || p.ePickup == EPickup.Gold || P2PowerUps.IsPowerUp(p.ePickup);

    // Can P2 take this pickup now? The game's CanPickup asks about "the player" (P1): for the heart that's "is P1 hurt?" -
    // 1.0 alpha+ (players: P2 couldn't take the heart; charcheck Megachad/Ogre: P2 at 102/140 walked over a heart while
    // P1 was at full HP, nothing happened) - so the heart asks about P2's health; every other pickup keeps the game's check
    private static bool P2CanTake(Pickup p)
    {
        if (p.ePickup == EPickup.Health) return !P2Health.IsDead && P2Health.Hp < P2Health.MaxHp;
        return p.CanPickup();
    }

    private static bool BeforeFollow(Pickup __instance, ref Transform target)
    {
        // P2's pull-all: every gem goes to P2 (and counts as P2's)
        if (pullDepth > 0 && body != null && __instance != null && target != null && target.Pointer != body.transform.Pointer)
        {
            target = body.transform;
            p2Gems[__instance.Pointer] = __instance;
            P2Items.ArmXpOrb(__instance);
            pulled++;
            return true;
        }
        // Spawned by P2's action (a pot P2 broke): goes to P2
        if (body != null && __instance != null && target != null && target.Pointer != body.transform.Pointer
            && p2PotSpots.Count > 0 && !P2Health.IsDead && NearP2Pot(__instance.transform.position))
        {
            target = body.transform;
            p2Gems[__instance.Pointer] = __instance;
            P2Items.ArmXpOrb(__instance);
            return true;
        }
        // A gem already flying to P2 stays P2's
        if (body != null && p2Gems.ContainsKey(__instance.Pointer) && target != null && target.Pointer != body.transform.Pointer)
            return false;
        // 1.0 alpha+ (players: "P2 can't grab pick-ups or XP"): P1's pickup sphere is a trigger that takes a gem the moment
        // it touches it, while P2's pull runs every 0.1 s - with the players together P1 won nearly every gem, even ones
        // lying nearer P2. A gem the game sends to P1 that is nearer P2 and inside P2's radius goes to P2 (not during
        // P1's pull-all: magnet/shrine gems stay P1's)
        var p1 = MyPlayer.Instance;
        if (body != null && p1 != null && __instance != null && target != null && target.Pointer != body.transform.Pointer
            && p1PullDepth == 0 && lastP2Radius > 0f && !P2Health.IsDead && !__instance.pickedUp && Collectable(__instance) && P2CanTake(__instance))
        {
            Vector3 g = __instance.transform.position, b = body.transform.position + Vector3.up;
            float toP2 = (g - b).sqrMagnitude;
            if (toP2 <= lastP2Radius * lastP2Radius && toP2 < (g - p1.transform.position).sqrMagnitude)
            {
                target = body.transform;
                p2Gems[__instance.Pointer] = __instance;
                P2Items.ArmXpOrb(__instance);
                if (nearerLogs++ < 5) ModLog.Info($"P2 takes a {__instance.ePickup} the game sent to P1 (nearer P2: {Mathf.Sqrt(toP2):F1} m)");
                return true;
            }
        }
        // Pooled gems: a gem going to P1 is no longer P2's
        if (body == null || target == null || target.Pointer != body.transform.Pointer) p2Gems.Remove(__instance.Pointer);
        return true;
    }

    // v0.14.0: P1 still got P2's XP although PlayerXp.AddXp was skipped - P1's inventory also has AddXp and a
    // pendingXp queue processed later. So also skip PlayerInventory.AddXp, and snapshot/restore P1's queued and
    // total XP around each P2 gem, whichever path the game takes.
    private static int p1PendingBefore, p1XpBefore;
    private static float p1LeftOverBefore;
    private static int restoreLogs;

    private static int goldSeenLogs, changeLogs, rangeLogs, moneyErrors, nearerLogs;
    private static float lastLoggedRange = -1f;
    private static float p2XpLeftOver;
    private static int xpMultLogs;
    private static readonly HashSet<IntPtr> goldSeen = new();
    public static int LastP2GoldFrame { get; private set; } = -10;

    // The enemy dying right now, if it is nearer P2 than P1 (P2's kill)
    private static int dyingDepth;
    public static int KillsP1, KillsP2;
    private static bool dyingNearP2;
    private static void BeforeEnemyDied(Assets.Scripts.Actors.Enemies.Enemy __instance, out bool __state)
    {
        __state = false;
        if (__instance == null || body == null || P2Health.IsDead || MyPlayer.Instance == null) return;
        Vector3 e = __instance.transform.position;
        __state = true;
        dyingDepth++;
        dyingNearP2 = (e - body.transform.position).sqrMagnitude < (e - MyPlayer.Instance.transform.position).sqrMagnitude;
        // Autotest kill counts per player (by the nearer player, like the gold); outermost death call only
        if (dyingDepth == 1) { if (dyingNearP2) KillsP2++; else KillsP1++; }
    }

    private static Exception AfterEnemyDied(Exception __exception, bool __state)
    {
        if (__state && --dyingDepth <= 0) { dyingDepth = 0; dyingNearP2 = false; }
        return __exception;
    }

    // ---------- Flying money (kill gold) ----------
    // Each money object is P2's if it first shows up nearer P2 (the kill was at P2); it then flies to P2, and its
    // payment (ChangeGold inside MoneyFlying.Pickup) goes to P2's purse
    private static readonly HashSet<IntPtr> moneySeen = new();
    private static readonly HashSet<IntPtr> p2Money = new();
    private static bool moneyForP2;
    private static int moneyLogs;

    private static void UpdateMoney()
    {
        var dict = MoneyUtility.flyingMoneyDict;
        var p1 = MyPlayer.Instance;
        if (dict == null || p1 == null || body == null) return;
        Vector3 a = p1.transform.position, b = body.transform.position;
        foreach (var kv in dict)
        {
            var mf = kv.Value;
            // v0.37.6 (user: the money flew to P2 but P2's counter didn't go up): "picked up" may come before the
            // payment (Pickup), so a picked-up one stays P2's until it is gone
            if (mf == null) continue;
            if (!mf.gameObject.activeInHierarchy) { moneySeen.Remove(mf.Pointer); p2Money.Remove(mf.Pointer); continue; }
            if (mf.pickedUp) continue;
            if (moneySeen.Add(mf.Pointer))
            {
                Vector3 m = mf.transform.position;
                if ((m - b).sqrMagnitude < (m - a).sqrMagnitude)
                {
                    p2Money.Add(mf.Pointer);
                    if (moneyLogs < 5 && ++moneyLogs > 0) ModLog.Info($"[gold] flying money worth {mf.value} at {m} is P2's (target was {(mf.target != null ? mf.target.name : "none")})");
                }
            }
            if (p2Money.Contains(mf.Pointer) && (mf.target == null || mf.target.Pointer != body.transform.Pointer)) mf.target = body.transform;
        }
        if (moneySeen.Count > 1000) { moneySeen.Clear(); p2Money.Clear(); }
    }

    private static float moneyP1GoldBefore;
    private static bool moneyPaidToP2;
    private static int arriveLogs;
    // (Pickup / Update / FixedUpdate of a money object: one at a time, they don't nest)
    private static void BeforeMoneyPickup(MoneyFlying __instance, System.Reflection.MethodBase __originalMethod)
    {
        moneyForP2 = __instance != null && p2Money.Contains(__instance.Pointer);
        moneyPaidToP2 = false;
        if (!moneyForP2) return;
        moneyP1GoldBefore = MyPlayer.Instance?.inventory?._gold_k__BackingField ?? 0f;
        if (seenMoneyCalls.Add(__originalMethod?.Name ?? "?"))
            ModLog.Info($"[gold] P2's money runs MoneyFlying.{__originalMethod?.Name}");
    }
    private static readonly HashSet<string> seenMoneyCalls = new();

    private static Exception AfterMoneyPickup(MoneyFlying __instance, System.Reflection.MethodBase __originalMethod, Exception __exception)
    {
        if (moneyForP2)
        {
            // Paid some other way than ChangeGold: whatever P1's amount gained moves to P2's purse
            float p1Now = MyPlayer.Instance?.inventory?._gold_k__BackingField ?? 0f;
            if (!moneyPaidToP2 && p1Now > moneyP1GoldBefore)
            {
                if (arriveLogs++ < 5) ModLog.Info($"[gold] P2's money paid P1's amount directly in {__originalMethod?.Name} (+{p1Now - moneyP1GoldBefore:F0}) - moved to P2");
                P2Gold.Collected(moneyP1GoldBefore);
            }
            else if (moneyPaidToP2 && arriveLogs++ < 5) ModLog.Info($"[gold] P2's money paid P2 in {__originalMethod?.Name}: P2 now {P2Gold.Amount:F0}");
            if (__instance != null && (moneyPaidToP2 || __instance.pickedUp && !__instance.gameObject.activeInHierarchy)) p2Money.Remove(__instance.Pointer);
        }
        moneyForP2 = false;
        return __exception;
    }

    // Kill gold for P2's kills: the game's own ChangeGold runs again with P2's purse and P2's stats in place (P2's gold
    // gain stat counts if the game applies it there), P1's call is skipped
    private static bool givingP2Gold;
    private static int killGoldLogs;
    private static bool BeforeChangeGold(PlayerInventory __instance, int amount)
    {
        if (changeLogs < 30 && (moneyForP2 || changeLogs < 5) && ++changeLogs > 0)   // TEMP
            ModLog.Info($"[gold] ChangeGold({amount}) frame {Time.frameCount}; enemy dying {dyingDepth > 0} (nearer P2 {dyingNearP2}); P2's money paying {moneyForP2}");
        if (givingP2Gold || amount <= 0 || !((dyingDepth > 0 && dyingNearP2) || moneyForP2) || __instance == null) return true;
        if (MyPlayer.Instance?.inventory == null || __instance.Pointer != MyPlayer.Instance.inventory.Pointer) return true;
        givingP2Gold = true;
        float before = P2Gold.Amount;
        bool entered = P2Inventory.EnterP2();
        try { P2Gold.Run(() => __instance.ChangeGold(amount)); }
        catch (Exception e) { ModLog.Warn($"[gold] P2 kill gold failed: {e.Message}"); }
        finally
        {
            if (entered) P2Inventory.Exit();
            givingP2Gold = false;
        }
        moneyPaidToP2 = true;
        if (killGoldLogs++ < 5) ModLog.Info($"[gold] P2's kill: +{amount} -> P2 {before:F0} -> {P2Gold.Amount:F0}");
        return false;
    }

    private static bool goldForP2;
    private static float p1GoldBefore;
    private static int goldApplyLogs;

    private static bool BeforeApply(Pickup __instance)
    {
        // A power-up flying to P2: applied for P2 (P2PowerUps calls ApplyPickup again with the game's event muted)
        if (!P2PowerUps.Applying && P2PowerUps.IsPowerUp(__instance.ePickup) && p2Gems.Remove(__instance.Pointer))
        {
            P2PowerUps.ApplyForP2(__instance);
            return false;
        }
        bool p2s = p2Gems.Remove(__instance.Pointer);
        goldForP2 = p2s && __instance.ePickup == EPickup.Gold;
        if (goldForP2) p1GoldBefore = Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory?._gold_k__BackingField ?? 0f;
        applyingForP2 = p2s && __instance.ePickup == EPickup.Xp;
        pendingValue = applyingForP2 ? __instance.GetValue() : 0;
        if (!applyingForP2) return true;
        var inv = Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory;
        if (inv == null) return true;
        p1PendingBefore = inv.pendingXp;
        if (inv.playerXp != null)
        {
            p1XpBefore = inv.playerXp.xp;
            p1LeftOverBefore = inv.playerXp.leftOverXp;
        }
        return true;
    }

    private static void AfterApply()
    {
        if (goldForP2)
        {
            goldForP2 = false;
            LastP2GoldFrame = Time.frameCount;
            if (goldApplyLogs++ < 8)
                ModLog.Info($"[gold] P2 coin applied frame {Time.frameCount}: P1 gold {p1GoldBefore:F0} -> {Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory?._gold_k__BackingField:F0}");
            P2Gold.Collected(p1GoldBefore);
        }
        if (!applyingForP2) return;
        applyingForP2 = false;
        // P2's XP gain stat, like the game's PlayerXp.AddXp (it multiplies and keeps the fraction in leftOverXp)
        float mult = Mathf.Max(0f, P2Inventory.Get(Assets.Scripts.Menu.Shop.EStat.XpIncreaseMultiplier));
        if (mult <= 0.001f) mult = 1f;
        p2XpLeftOver += pendingValue * mult;
        int whole = Mathf.FloorToInt(p2XpLeftOver);
        p2XpLeftOver -= whole;
        if (xpMultLogs++ < 3) ModLog.Info($"P2 gem {pendingValue} x XP gain {mult:F2} -> {whole}");
        P2XpBar.AddXp(whole);
        var inv = Assets.Scripts.Actors.Player.MyPlayer.Instance?.inventory;
        if (inv == null) return;
        bool restored = false;
        if (inv.pendingXp != p1PendingBefore)
        {
            inv.pendingXp = p1PendingBefore;
            restored = true;
        }
        if (inv.playerXp != null && inv.playerXp.xp != p1XpBefore)
        {
            inv.playerXp.xp = p1XpBefore;
            inv.playerXp.leftOverXp = p1LeftOverBefore;
            restored = true;
        }
        if (restored && restoreLogs++ < 3) ModLog.Info("P2 gem: P1's XP was changed by the game, restored");
    }

    // While a P2 gem is applied, P1 doesn't get its XP
    // (autotest: no XP at all unless the script turns it on - level-up screens pause the game mid-test)
    private static bool BeforeAddXp() => !applyingForP2 && !Autotest.NoXp;
}
