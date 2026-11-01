using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using Inventory__Items__Pickups.Xp_and_Levels;
using UnityEngine;

namespace MegabonkSplitscreen;

// P2's items (user: chest rewards apply to P1 and P2 separately; P1's and P2's items apart in the HUD).
// Game (interop dump): ItemInventory (items dict, AddItem/RemoveItem, Tick, PreAttack, PostDamage, OnLateFixedUpdate)
// and 86 ItemBase subclasses. The "item added/removed/stats changed" events are STATIC (ItemInventory.A_ItemAdded,
// A_ItemRemoved, A_StatsChanged; ItemBase.A_ItemAdded, A_ItemRemoved) - so an item going into P2's inventory was
// announced like one of P1's (P1's ItemsHud.OnItemAdded(EItem) shows it).
//  - Mute: while P2's inventory adds/removes an item, those static events are off (as for P2's tomes).
//  - TEMP diagnostics: which inventory the game calls for attacks/hits/ticks (and in P2's context or not), and which
//    static game events P1's / P2's item objects listen to - names logged, before any routing is built on them.
internal static class P2Items
{
    private static readonly string[] Muted = { "A_ItemAdded", "A_ItemRemoved", "A_StatsChanged" };
    private static int muteLogs;

    public static void Apply(Harmony harmony)
    {
        int n = 0;
        foreach (var m in typeof(ItemInventory).GetMethods().Where(m => m.DeclaringType == typeof(ItemInventory) && (m.Name == "AddItem" || m.Name == "RemoveItem")))
        {
            if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2Items: ItemInventory.{m.Name}({m.GetParameters().Length}) not hookable - P2's items may show in P1's HUD"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Items), nameof(BeforeChange)), finalizer: new HarmonyMethod(typeof(P2Items), nameof(AfterChange)));
            n++;
        }
        int d = 0;
        foreach (var (name, hook) in new[] { ("PreAttack", nameof(BeforePreAttack)), ("PostDamage", nameof(BeforePostDamage)), ("Tick", nameof(CountCall)), ("OnLateFixedUpdate", nameof(CountCall)) })
        {
            var m = AccessTools.DeclaredMethod(typeof(ItemInventory), name);
            if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { ModLog.Warn($"P2Items: ItemInventory.{name} not hookable - {(hook == nameof(CountCall) ? "diagnostic skipped" : "P2's hits/attacks use P1's items")}"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Items), hook));
            if (hook != nameof(CountCall)) routedHooks++;
            d++;
        }
        ApplyHitScopes(harmony);
        ApplyItemProjectiles(harmony);
        ModLog.Info($"P2Items: event mute on {n} add/remove methods, attack/hit routing on {routedHooks} methods, {d} hooks in all");
    }

    // ---------- Whose hit ----------
    // The game's DamageContainers are reused across attacks (v0.49 probe: tagging P2's attack containers sent P1's hits
    // to P2's items too), so the owner comes from what is hitting: while a projectile's hit/step methods run, the
    // projectile's owner (P2Weapons' list of P2's shots) is "the hitter". Hits outside any projectile (auras,
    // explosions) go by the nearer player. Hooks only where safe (native code unique, or shared only among projectile
    // methods and not hooked yet); a method P2Weapons already hooks gets the scope added to its own patch set.
    // v0.49 probe: game-to-game calls (FixedUpdate -> MyFixedUpdate -> HitEnemy) mostly skip IL2CPP hooks; Unity's own
    // calls (FixedUpdate, Update, OnTrigger*) go through them - those first
    private static readonly string[] HitMethods = { "FixedUpdate", "Update", "LateUpdate", "OnTriggerEnter", "OnTriggerStay", "OnCollisionEnter",
        "HitEnemy", "CheckCollision", "CheckZone", "CheckColliderCustom", "CheckSpawnCollision", "StepMovement", "MyFixedUpdate", "MyUpdate" };
    private static readonly List<bool> hitters = new();   // stack: true = P2's projectile
    // The projectile hitting right now: true = P2's, false = P1's, null = no projectile hit running (auras, explosions)
    public static bool? CurrentHitter => hitters.Count > 0 ? hitters[hitters.Count - 1] : null;
    private static void ApplyHitScopes(Harmony harmony)
    {
        var hooked = new List<string>();
        var skipped = new List<string>();
        var pb = typeof(Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles.ProjectileBase);
        var patched = new HashSet<System.Reflection.MethodBase>(Harmony.GetAllPatchedMethods());
        foreach (var t in pb.Assembly.GetTypes().Where(t => pb.IsAssignableFrom(t)))
            foreach (var name in HitMethods)
            {
                var m = AccessTools.DeclaredMethod(t, name);
                if (m == null || m.IsAbstract) continue;
                string label = $"{t.Name}.{name}";
                bool ok = patched.Contains(m) || (!P2Weapons.IsNativeCodeHooked(m) && (P2Weapons.IsNativeCodeUnique(m) || P2Weapons.SharedOnlyWithin(m, pb)));
                if (!ok) { skipped.Add(label); continue; }
                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Items), nameof(HitIn)), finalizer: new HarmonyMethod(typeof(P2Items), nameof(HitOut)));
                    patched.Add(m);
                    hooked.Add(label);
                }
                catch (Exception e) { skipped.Add($"{label} ({e.Message})"); }
            }
        ModLog.Info($"P2Items: hit owner scope on {hooked.Count} projectile methods ({string.Join(", ", hooked)}); skipped {(skipped.Count > 0 ? string.Join(", ", skipped) : "none")}");
    }

    private static void HitIn(Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles.ProjectileBase __instance, out bool __state)
    {
        __state = __instance != null;
        if (__state) hitters.Add(P2Weapons.IsP2Projectile(__instance));
    }

    private static Exception HitOut(Exception __exception, bool __state)
    {
        if (__state && hitters.Count > 0) hitters.RemoveAt(hitters.Count - 1);
        return __exception;
    }

    // ---------- Shattered Wisdom: XP orbs flying to P2 ----------
    // v0.49 sweep + probe (P1 129 / P2 0): an XP orb's XpDamager (hurts enemies on its way to the player) is switched on
    // when the orb appears, from P1's items. An orb the mod sends to P2 follows P2's: on (with the game's damage) when P2
    // has Shattered Wisdom, off when not (P1's Shattered Wisdom then doesn't arm P2's orbs).
    private static int xpLogs;
    public static void ArmXpOrb(Pickup p)
    {
        if (p == null || P2Inventory.Items == null) return;
        var xd = p.GetComponent<XpDamager>();
        if (xd == null) return;
        bool has = P2Inventory.Items.GetAmount(EItem.ShatteredWisdom) > 0;
        bool was = xd.isEnabled;
        xd.isEnabled = has;
        if (has)
            try { xd.damage = Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemShatteredWisdom.GetDamage(); } catch { }
        if (was != has && xpLogs++ < 6) ModLog.Info($"[items] XP orb to P2: its damage part {(has ? $"on ({xd.damage:F1}, P2's Shattered Wisdom)" : "off (P2 has no Shattered Wisdom)")}");
    }

    // ---------- P2's item projectiles (ghosts, orbs...) ----------
    // v0.49 probe (user: P2's Ghost item's ghosts flew around P1): items shoot ItemProjectile objects ('GhostProjectile',
    // Energy Core's ItemProjectileOrb) - set up with Set(pos, ...), then each physics step they look for targets and
    // move relative to "the player" (P1). One set up during P2's item run is P2's: while it steps, P1's object stands at
    // P2's spot and P2's movement copy is "the player", and its hits are P2's.
    private static readonly HashSet<IntPtr> p2ItemShots = new();
    private static void ApplyItemProjectiles(Harmony harmony)
    {
        var t = typeof(ItemProjectile);
        var done = new List<string>();
        foreach (var (name, pre, fin, post) in new (string, string, string, string)[] { ("Set", null, null, nameof(AfterItemShotSet)), ("FixedUpdate", nameof(ItemShotIn), nameof(ItemShotOut), null) })
        {
            var m = AccessTools.DeclaredMethod(t, name);
            if (m == null || !P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m)) { done.Add($"{name} SKIPPED"); continue; }
            harmony.Patch(m, prefix: pre != null ? new HarmonyMethod(typeof(P2Items), pre) : null, postfix: post != null ? new HarmonyMethod(typeof(P2Items), post) : null,
                finalizer: fin != null ? new HarmonyMethod(typeof(P2Items), fin) : null);
            done.Add(name);
        }
        ModLog.Info($"P2Items: item projectiles: {string.Join(", ", done)}");
    }

    private static int shotLogs;
    private static void AfterItemShotSet(ItemProjectile __instance)
    {
        if (__instance == null) return;
        if (!RunningAsP2) { p2ItemShots.Remove(__instance.Pointer); return; }
        p2ItemShots.Add(__instance.Pointer);
        // Orbs circle a target set from "the player": P2's body
        var orb = __instance.TryCast<Assets.Scripts.Inventory__Items__Pickups.Items.ItemProjectiles.ItemProjectileOrb>();
        if (orb != null && P2Weapons.Body != null) orb.orbitTarget = P2Weapons.Body.transform;
        if (shotLogs++ < 8) ModLog.Info($"[itemfx] P2's item projectile '{__instance.name}' ({__instance.damageSource}) - steps with P2 as the player");
    }

    private struct ShotState { public bool on; public Vector3 p1Pos; public PlayerMovement pm; }
    private static void ItemShotIn(ItemProjectile __instance, out ShotState __state)
    {
        __state = default;
        if (__instance == null || !p2ItemShots.Contains(__instance.Pointer) || RunningAsP2) return;
        var p1 = MyPlayer.Instance;
        var pm = P2Movement.Pm;
        if (p1 == null || pm == null) return;
        __state = new ShotState { on = true, p1Pos = P1Home(p1), pm = PlayerMovement.Instance };
        p1.transform.position = pm.transform.position;
        PlayerMovement.Instance = pm;
        hitters.Add(true);
    }

    private static Exception ItemShotOut(Exception __exception, ShotState __state)
    {
        if (!__state.on) return __exception;
        if (hitters.Count > 0) hitters.RemoveAt(hitters.Count - 1);
        if (__state.pm != null) PlayerMovement.Instance = __state.pm;
        if (MyPlayer.Instance != null) MyPlayer.Instance.transform.position = __state.p1Pos;
        return __exception;
    }

    // Whose hit this is: 2 = P2's, 1 = P1's
    private static int hitByScope, hitByNear;
    private static int HitOwner(Assets.Scripts.Actors.DamageContainer dc) => HitOwnerOf(dc?.enemy);
    public static int HitOwnerOf(Assets.Scripts.Actors.Enemies.Enemy e)
    {
        if (RunningAsP2 || P2Inventory.InP2Context) return 2;
        if (hitters.Count > 0) { hitByScope++; return hitters[hitters.Count - 1] ? 2 : 1; }
        hitByNear++;
        var pm = P2Movement.Pm;
        var p1 = MyPlayer.Instance;
        if (e == null || pm == null || p1 == null || P2Health.IsDead) return 1;
        Vector3 at = e.transform.position;
        return (at - pm.transform.position).sqrMagnitude < (at - p1.transform.position).sqrMagnitude ? 2 : 1;
    }
    private static int routedHooks;

    private static bool IsP2s(ItemInventory inv) => inv != null && P2Inventory.Items != null && inv.Pointer == P2Inventory.Items.Pointer;
    private static bool IsP1s(ItemInventory inv)
    {
        var p1 = P1Items;
        return inv != null && p1 != null && inv.Pointer == p1.Pointer && !IsP2s(inv);
    }

    // P1's own item inventory, also while P2's is in P1's slot (P2's chest window, P2's item runs, test gives) -
    // v0.49 chest test: reading the slot then filed P2's credit card as P1's
    private static ItemInventory p1Real;
    public static ItemInventory P1Items
    {
        get
        {
            var now = MyPlayer.Instance?.inventory?.itemInventory;
            if (now != null && !IsP2s(now)) p1Real = now;
            else if (now == null) p1Real = null;
            return p1Real;
        }
    }

    // ---------- Mute + listener routing while P2's inventory changes ----------
    private class Change
    {
        public object[] muted;
        public Dictionary<string, Il2CppSystem.Delegate> events;
        public List<IntPtr> kids;
        public bool statsEntered;
        public PlayerMovement p1Pm;
        public Vector3 p1Pos;
        public bool moved;
    }
    private static int changeDepth;

    private static void BeforeChange(ItemInventory __instance, out Change __state)
    {
        __state = null;
        if (!IsP2s(__instance) || changeDepth++ > 0) return;   // AddItem(e, n) may call AddItem(e): the outer one does it
        __state = new Change { events = P2Abilities.SnapshotEvents(), muted = new object[Muted.Length * 2], kids = P1Kids() };
        // Only ItemInventory's (P1's HUD); ItemBase.A_ItemAdded stays on - ItemEvents sends it to P2's item effects
        for (int i = 0; i < Muted.Length; i++) __state.muted[i] = Swap(typeof(ItemInventory), Muted[i], null);
        if (muteLogs++ < 4) ModLog.Info("[items] P2's inventory changing: the game's static item events muted (P1's HUD doesn't take it)");
        P2Inventory.MarkDirty();
        // v0.49 sweep (Scarf: P1 +0.575 damage, P2 none): an item sets itself up from "the player" (grounded, position,
        // stats) - P2 is the player while P2's item is added/removed
        __state.statsEntered = P2Inventory.EnterP2();
        __state.p1Pm = PlayerMovement.Instance;
        if (P2Movement.Pm != null) PlayerMovement.Instance = P2Movement.Pm;
        var p1 = MyPlayer.Instance;
        if (p1 != null && P2Movement.Pm != null && !RunningAsP2)
        {
            __state.p1Pos = P1Home(p1);
            p1.transform.position = P2Movement.Pm.transform.position;
            __state.moved = true;
        }
    }

    private static Exception AfterChange(ItemInventory __instance, Exception __exception, Change __state)
    {
        if (IsP2s(__instance) && changeDepth > 0) changeDepth--;
        // Either player's new item: its listeners on shared events go to the owner routing right away
        if (P2Inventory.Items != null && (IsP1s(__instance) || (IsP2s(__instance) && changeDepth == 0)))
            try { ItemEvents.Keep(now: true); } catch (Exception e) { ModLog.Warn($"[items] event routing after an item change: {e.Message}"); }
        if (__state == null) return __exception;
        if (__state.moved && MyPlayer.Instance != null) MyPlayer.Instance.transform.position = __state.p1Pos;
        if (__state.p1Pm != null) PlayerMovement.Instance = __state.p1Pm;
        if (__state.statsEntered) P2Inventory.Exit();
        try
        {
            for (int i = 0; i < Muted.Length; i++) Swap(typeof(ItemInventory), Muted[i], __state.muted[i]);
            MoveNewToP2(__state.kids, "item change");   // an effect the item's Init attached to "the player"
            // The item's own subscriptions (its Init): player events -> P2's happenings only (P2Abilities' routing)
            P2Abilities.RouteEvents("P2's item", __state.events);
            // A removed item's routed listeners go
            var alive = new HashSet<IntPtr>();
            if (P2Inventory.Items?.items != null) foreach (var kv in P2Inventory.Items.items) if (kv.Value != null) alive.Add(kv.Value.Pointer);
            int dropped = P2Abilities.DropRoutedTargets(t => t.TryCast<ItemBase>() != null && !alive.Contains(t.Pointer));
            if (dropped > 0) ModLog.Info($"[items] {dropped} routed listener(s) of P2's removed item(s) dropped");
            P2Hud.ItemsChanged();
        }
        catch (Exception e) { ModLog.Warn($"[items] after P2's item change: {e.Message}"); }
        return __exception;
    }

    // ---------- P2's item effects on P2 (user: some treasure reward vfx don't get applied to p2) ----------
    // While P2's items run as P2, "the player" is P1's object standing at P2's spot: an effect an item attaches to the
    // player lands under P1's object (or P1's model) and then follows P1. New children there during P2's runs / P2's
    // item changes are moved to P2's body, same local pose (names logged).
    // Where P1's object goes back to after standing at P2's spot for a moment: P1's PHYSICS position (v0.49.3, user: P1
    // was extremely slow at the start of a round until P2 died - P2's health tick runs every frame at P2's spot, and
    // putting P1's object back to its drawn (interpolated, a little behind) position dragged P1's rigidbody back each
    // frame). The rigidbody's own position leaves P1's movement exactly as it was.
    public static Vector3 P1Home(MyPlayer p1)
    {
        var rb = p1?.playerMovement?.rb;
        return rb != null ? rb.position : p1 != null ? p1.transform.position : Vector3.zero;
    }

    // Everything under P1's object (v0.49 sweep: Power Gloves' effect sat deeper than P1's direct children)
    private static List<IntPtr> P1Kids()
    {
        var list = new List<IntPtr>();
        var p1 = MyPlayer.Instance;
        if (p1 == null) return list;
        foreach (var t in p1.GetComponentsInChildren<Transform>(true)) if (t != null) list.Add(t.Pointer);
        return list;
    }

    private static int fxLogs;
    public static int FxMoved { get; private set; }
    private static void MoveNewToP2(List<IntPtr> before, string what)
    {
        var body = P2Weapons.Body;
        var p1 = MyPlayer.Instance;
        if (body == null || p1 == null || before == null) return;
        var had = new HashSet<IntPtr>(before);
        // The top of each new branch (its parent was there before)
        var moved = new List<Transform>();
        foreach (var t in p1.GetComponentsInChildren<Transform>(true))
            if (t != null && !had.Contains(t.Pointer) && t.parent != null && had.Contains(t.parent.Pointer)) moved.Add(t);
        foreach (var c in moved)
        {
            // Onto the part of P2's body with the parent's name (P2's model is a copy of P1's), else P2's body
            var parent = c.parent;
            Transform onto = parent == p1.transform ? body.transform : FindNamed(body.transform, parent.name) ?? body.transform;
            Vector3 lp = c.localPosition;
            Quaternion lr = c.localRotation;
            Vector3 ls = c.localScale;
            c.SetParent(onto, false);
            c.localPosition = lp;
            c.localRotation = lr;
            c.localScale = ls;
            FxMoved++;
            if (fxLogs++ < 30) ModLog.Info($"[itemfx] '{c.name}' appeared on P1 (under '{parent.name}') during P2's {what} - moved to P2's '{onto.name}'");
        }
    }

    private static Transform FindNamed(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t != null && t.name == name) return t;
        return null;
    }

    // P1's object at P2's spot for `a` (the game's "the player" code then works around P2), and whatever `a` attached
    // to P1 moved to P2 - for P2's item runs and P2's borrowed health (P2's damage/heal/hit events: Holy Book, Mirror,
    // Toxic Barrel, Electric Plug effects showed on P1 in the v0.49 sweep). Nests.
    private static int spotDepth;
    public static void AtP2Spot(string what, Action a)
    {
        var p1 = MyPlayer.Instance;
        var pm = P2Movement.Pm;
        if (spotDepth > 0 || p1 == null || pm == null) { a(); return; }
        Vector3 p1Pos = P2Items.P1Home(p1);
        var kids = P1Kids();
        var playing = PlayingUnderP1();
        spotDepth++;
        try
        {
            p1.transform.position = pm.transform.position;
            a();
        }
        finally
        {
            spotDepth--;
            p1.transform.position = p1Pos;
            MoveNewToP2(kids, what);
            PlayStartedOnP2(playing, what);
        }
    }

    // v0.49 probe: Electric Plug's effect is one object under P1, made by P1's plug and replayed for every proc - P2's
    // plug replayed P1's. An effect under P1 that starts playing during P2's scope is P2's: stopped on P1, and a copy on
    // P2 (one per source effect, made the first time) plays instead. P1's own stays P1's.
    private static HashSet<IntPtr> PlayingUnderP1()
    {
        var s = new HashSet<IntPtr>();
        var p1 = MyPlayer.Instance;
        if (p1 == null) return s;
        foreach (var ps in p1.GetComponentsInChildren<ParticleSystem>(false)) if (ps != null && ps.isPlaying) s.Add(ps.Pointer);
        return s;
    }

    private static readonly Dictionary<IntPtr, GameObject> p2FxCopies = new();
    private static void PlayStartedOnP2(HashSet<IntPtr> before, string what)
    {
        var p1 = MyPlayer.Instance;
        var body = P2Weapons.Body;
        if (p1 == null || body == null) return;
        var started = new HashSet<IntPtr>();
        foreach (var ps in p1.GetComponentsInChildren<ParticleSystem>(false))
        {
            if (ps == null || !ps.isPlaying || before.Contains(ps.Pointer)) continue;
            // The effect's own top: up to the child of P1's object (or of a part P2's body has too, e.g. 'Feet')
            var top = ps.transform;
            while (top.parent != null && top.parent != p1.transform && FindNamed(body.transform, top.parent.name) == null) top = top.parent;
            // Only spawned effects ('(Clone)': item effects); P1's own built-in ones (footstep dust...) may start during
            // P2's frequent scopes (P2's health tick) and stay P1's
            if (!top.name.EndsWith("(Clone)")) continue;
            if (!started.Add(top.Pointer)) continue;
            var rootPs = top.GetComponent<ParticleSystem>();
            try { if (rootPs != null) { rootPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); } else ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); } catch { }
            if (!p2FxCopies.TryGetValue(top.Pointer, out var copy) || copy == null)
            {
                var onto = top.parent == p1.transform ? body.transform : FindNamed(body.transform, top.parent.name) ?? body.transform;
                copy = UnityEngine.Object.Instantiate(top.gameObject, onto);
                copy.name = top.gameObject.name;   // same name (the sweep compares effect names)
                foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && mb.GetIl2CppType().Assembly.GetName().Name == "Assembly-CSharp" && mb.GetIl2CppType().Name != "RandomSfx") UnityEngine.Object.DestroyImmediate(mb);
                copy.transform.localPosition = top.localPosition;
                copy.transform.localRotation = top.localRotation;
                copy.transform.localScale = top.localScale;
                p2FxCopies[top.Pointer] = copy;
                if (fxLogs++ < 30) ModLog.Info($"[itemfx] '{top.name}' (under P1's '{top.parent.name}') played for P2's {what} - stopped on P1, P2's copy under '{onto.name}'");
            }
            if (!copy.activeSelf) copy.SetActive(true);
            var cps = copy.GetComponent<ParticleSystem>() ?? copy.GetComponentInChildren<ParticleSystem>();
            try { cps?.Play(true); } catch { }
        }
    }

    // ---------- P2's items run as P2 ----------
    // P2's health (borrowed: heals, damage taken, its events reach P2's listeners), P2's stats, P2's items in P1's
    // inventory, P2's gold, P2's movement copy as "the player" and P1's object standing at P2's spot (items that work
    // around the player: Campfire, Golden Sneakers, Energy Core's orbs...) - all put back after.
    private static int runDepth, runErrors;
    public static bool RunningAsP2 => runDepth > 0;
    public static void RunAsP2(string what, Action a)
    {
        if (runDepth > 0) { a(); return; }
        var p1 = MyPlayer.Instance;
        var inv = p1?.inventory;
        if (inv == null || P2Inventory.Items == null) return;
        void Inner()
        {
            var realItems = inv.itemInventory;
            // v0.49 chest test: P2's Credit Card Red put its damage into P1's stats - items may write into "the
            // player's" stat holder (like a greed shrine): P2's for P2's run
            var realStats = inv.statInventory;
            Vector3 p1Pos = P2Items.P1Home(p1);
            var pm = P2Movement.Pm;
            runDepth++;
            // v0.49 (item sweep: P2's Spiky Shield never raised P2's Thorns): an item changing its stats announces it on
            // the static ItemInventory.A_StatsChanged (no owner) - P1's stats listen; for P2's run it marks P2's
            var p1StatsChanged = ItemInventory.A_StatsChanged;
            ItemInventory.A_StatsChanged = P2StatsMarker();
            try
            {
                inv.itemInventory = P2Inventory.Items;
                if (P2Inventory.StatsInv != null) inv.statInventory = P2Inventory.StatsInv;
                AtP2Spot(what, () => P2Gold.Run(a));
            }
            catch (Exception e) { if (runErrors++ < 6) ModLog.Warn($"[items] P2's {what} failed: {e.Message}"); }
            finally
            {
                runDepth--;
                ItemInventory.A_StatsChanged = p1StatsChanged;
                p1.transform.position = p1Pos;
                inv.itemInventory = realItems;
                inv.statInventory = realStats;
            }
        }
        if (!P2GameHealth.Borrow("items " + what, _ => Inner()))
        {
            // No borrowed health (P2GameHealth off or already borrowing): P2's stats and movement at least
            var p1Pm = PlayerMovement.Instance;
            bool entered = P2Inventory.EnterP2();
            try { if (P2Movement.Pm != null) PlayerMovement.Instance = P2Movement.Pm; Inner(); }
            finally { if (p1Pm != null) PlayerMovement.Instance = p1Pm; if (entered) P2Inventory.Exit(); }
        }
    }

    private static Il2CppSystem.Action<Assets.Scripts.Menu.Shop.EStat> statsMarker;
    private static Il2CppSystem.Action<Assets.Scripts.Menu.Shop.EStat> P2StatsMarker() =>
        statsMarker ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Assets.Scripts.Menu.Shop.EStat>>(
            new Action<Assets.Scripts.Menu.Shop.EStat>(_ => P2Inventory.MarkDirty()));

    // P2's items may also change their stats from shared events (an enemy dying: Demonic Soul...) outside P2's runs:
    // the mod's marker rides along on the game's event too (checked once a second; cheap - it only sets a flag)
    private static float nextMarkerCheck;
    private static void KeepMarker()
    {
        if (Time.unscaledTime < nextMarkerCheck || RunningAsP2) return;
        nextMarkerCheck = Time.unscaledTime + 1f;
        var marker = P2StatsMarker();
        var now = ItemInventory.A_StatsChanged;
        if (now != null) foreach (var l in now.GetInvocationList()) if (l.Pointer == marker.Pointer) return;
        ItemInventory.A_StatsChanged = now == null ? marker : Il2CppSystem.Delegate.Combine(now, marker).Cast<Il2CppSystem.Action<Assets.Scripts.Menu.Shop.EStat>>();
    }

    // Each physics step (Ticker.FixedUpdate), like the game ticks P1's: P2's items' timed effects
    public static void FixedTick()
    {
        _ = P1Items;   // keeps P1's own inventory known (read outside any swap)
        if (P2Inventory.Items == null) return;   // no P2 this run: the game's events stay as they are
        KeepMarker();
        ItemEvents.Keep();
        var items = P2Inventory.Items;
        if (items?.items == null || items.items.Count == 0 || P2Health.IsDead || !P2Movement.Active) return;
        RunAsP2("tick", () => items.Tick());
    }

    // Attacks: P2's weapon sets up its attack in P2's stat context (WeaponUtility hooks), or P2's projectile is
    // hitting -> P2's items' PreAttack (damage bonuses etc.) instead of P1's
    private static bool BeforePreAttack(ItemInventory __instance, Assets.Scripts.Actors.DamageContainer __0, Assets.Scripts.Inventory__Items__Pickups.Stats.StatComponents __1,
        ref Assets.Scripts.Inventory__Items__Pickups.Stats.StatComponents __result, System.Reflection.MethodBase __originalMethod)
    {
        CountCall(__instance, __originalMethod);
        bool p2s = P2Inventory.InP2Context || (hitters.Count > 0 && hitters[hitters.Count - 1]);
        if (!IsP1s(__instance) || !p2s || P2Inventory.Items == null) return true;
        attacksP2++;
        var p2 = P2Inventory.Items;
        Assets.Scripts.Inventory__Items__Pickups.Stats.StatComponents r = __1;
        RunAsP2("attack", () => r = p2.PreAttack(__0, __1));
        __result = r;
        return false;
    }

    // Hits: a hit by P2's attack procs P2's items (P1's don't); others stay P1's
    private static int hitP2, hitP1, attacksP2, hitLogs, p1HitLogs;
    private static bool BeforePostDamage(ItemInventory __instance, Assets.Scripts.Actors.DamageContainer __0, System.Reflection.MethodBase __originalMethod)
    {
        CountCall(__instance, __originalMethod);
        if (!IsP1s(__instance) || __0 == null || P2Inventory.Items == null) return true;
        int byScope = hitByScope;
        if (HitOwner(__0) != 2)
        {
            hitP1++;
            // TEMP (P1's Bonker never procs, P2's does): what P1's routed hits are
            if (p1HitLogs++ < 8) ModLog.Info($"[items] P1's hit: source '{__0.damageSource}', {__0.damage:F1} dmg, proc {__0.procCoefficient:F2}, crit {__0.crit}, flags {__0.flags}, owner by {(hitByScope > byScope ? "projectile" : "nearer player")}; P1's on-hit items {P1Items?.itemsWithOnHitProcs?.Count}");
            return true;
        }
        // TEMP: what P2's routed hits are
        if (hitLogs++ < 12) ModLog.Info($"[items] P2's hit: source '{__0.damageSource}', {__0.damage:F1} dmg, proc {__0.procCoefficient:F2}, owner by {(RunningAsP2 ? "P2 item run" : P2Inventory.InP2Context ? "P2 context" : hitByScope > byScope ? "projectile" : "nearer player")}");
        hitP2++;
        var p2 = P2Inventory.Items;
        if (p2.itemsWithOnHitProcs != null && p2.itemsWithOnHitProcs.Count > 0) RunAsP2("hit", () => p2.PostDamage(__0));
        return false;
    }

    // Sets a static event property, returns its old value
    private static object Swap(Type t, string prop, object value)
    {
        var p = t.GetProperty(prop, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (p == null) return null;
        var old = p.GetValue(null);
        p.SetValue(null, value);
        return old;
    }

    // ---------- TEMP: who the game calls ----------
    private static readonly Dictionary<string, int> calls = new();
    private static float nextReport;
    private static void CountCall(ItemInventory __instance, System.Reflection.MethodBase __originalMethod)
    {
        var p1 = MyPlayer.Instance?.inventory?.itemInventory;
        string who = IsP2s(__instance) ? "P2's" : p1 != null && __instance != null && __instance.Pointer == p1.Pointer ? "P1's" : "other";
        string key = $"{__originalMethod.Name} on {who}{(P2Inventory.InP2Context ? " in P2 context" : "")}";
        calls[key] = calls.TryGetValue(key, out int c) ? c + 1 : 1;
        if (Time.unscaledTime < nextReport) return;
        nextReport = Time.unscaledTime + 5f;
        ModLog.Info($"[items] calls (5 s): {string.Join(", ", calls.Select(kv => $"{kv.Key} x{kv.Value}"))}; hits P2's {hitP2} / P1's {hitP1} (owner by projectile {hitByScope}, by nearer player {hitByNear}); P2 attack setups {attacksP2}");
        calls.Clear();
        hitP2 = hitP1 = hitByScope = hitByNear = attacksP2 = 0;
    }

    // ---------- TEMP: which static game events item objects listen to ----------
    public static string ScanListeners()
    {
        var owners = new Dictionary<IntPtr, string>();
        void Add(ItemInventory inv, string who)
        {
            if (inv?.items == null) return;
            foreach (var kv in inv.items) if (kv.Value != null) owners[kv.Value.Pointer] = $"{who} {kv.Key}";
        }
        Add(MyPlayer.Instance?.inventory?.itemInventory, "P1's");
        Add(P2Inventory.Items, "P2's");
        if (owners.Count == 0) return "no items";
        var found = new List<string>();
        var asm = typeof(ItemInventory).Assembly;
        Type[] types;
        try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        foreach (var t in types)
        {
            System.Reflection.PropertyInfo[] props;
            try { props = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var p in props)
            {
                if (!typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
                Il2CppSystem.Delegate d;
                try { d = p.GetValue(null) as Il2CppSystem.Delegate; } catch { continue; }
                if (d == null) continue;
                foreach (var l in d.GetInvocationList())
                {
                    var target = l?.Target;
                    if (target == null || !owners.TryGetValue(target.Pointer, out var who)) continue;
                    string method;
                    try { method = l.Method?.Name; } catch { method = "?"; }
                    found.Add($"{who} -> {t.Name}.{p.Name} ({method})");
                }
            }
        }
        return found.Count == 0 ? "no item listens to a static game event" : string.Join("; ", found);
    }

    public static void Reset()
    {
        ItemEvents.Reset();
        foreach (var c in p2FxCopies.Values) if (c != null) UnityEngine.Object.Destroy(c);
        p2FxCopies.Clear();
        hitters.Clear();
        changeDepth = runDepth = 0;
    }

    public static string DescribeP1() => Describe(P1Items);
    public static string Describe(ItemInventory inv)
    {
        if (inv?.items == null || inv.items.Count == 0) return "none";
        var parts = new List<string>();
        foreach (var kv in inv.items) parts.Add($"{kv.Key} x{(kv.Value != null ? kv.Value.amount : 0)}");
        return string.Join(", ", parts);
    }
}
