using System.Reflection;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using UnityEngine;

namespace MegabonkSplitscreen;

// Item listeners on the game's shared (non-player) events go to the player who caused the event.
// v0.49 scan ('itemscan' with every item on both players) - the static events items subscribe to besides the player
// events P2Abilities already routes (health, movement):
//   Enemy.A_EnemyDied / A_Damage (Enemy, DamageContainer), Enemy.A_EnemyReleasedFromPool (Enemy): Borgar, Demonic
//     Blood/Soul, Cursed Doll, Soul Harvester, Demon Blade, Joe's Dagger, Kevin -> the hitter / killer
//   InteractableChest.A_ChestOpened, OpenChest.A_Open (no args): credit cards -> who opened the chest
//   DetectInteractables.A_Interacted (BaseInteractable, bool): Ghost -> P1's interactions fire it; P2's don't (P2Interact
//     calls Interact directly), so the mod calls P2's Ghost for P2's
//   GameManager.A_StageStarted, MyTime.A_TimeScaleChange (no args): Speed Boi, Gas Mask -> both, each as itself
// Both players' item listeners are taken off these events and kept here; one mod listener per event decides the owner
// and calls only that player's (P2's as P2: P2Items.RunAsP2). The game's own listeners stay on the event untouched.
internal static class ItemEvents
{
    private class Route
    {
        public PropertyInfo prop;
        public string key;
        public Il2CppSystem.Delegate p1, p2, forwarder;
        public Action<object> fixedP2;   // the mod's own P2 side (P2's mirror copy)
    }
    private static readonly List<Route> routes = new();
    private static int logs;

    private static Route R(string key) => routes.FirstOrDefault(r => r.key == key);

    private static void Ensure()
    {
        if (routes.Count > 0) return;
        void Add(Type t, string name, Il2CppSystem.Delegate fwd)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (p == null) { ModLog.Warn($"[itemevents] {t.Name}.{name} not found"); return; }
            routes.Add(new Route { prop = p, key = $"{t.Name}.{name}", forwarder = fwd });
        }
        Add(typeof(Enemy), "A_EnemyDied", Conv<Il2CppSystem.Action<Enemy, DamageContainer>>(new Action<Enemy, DamageContainer>((e, dc) =>
            Dispatch("Enemy.A_EnemyDied", OwnerOfHit(e, dc), d => d.Cast<Il2CppSystem.Action<Enemy, DamageContainer>>().Invoke(e, dc)))));
        Add(typeof(Enemy), "A_Damage", Conv<Il2CppSystem.Action<Enemy, DamageContainer>>(new Action<Enemy, DamageContainer>((e, dc) =>
            Dispatch("Enemy.A_Damage", OwnerOfHit(e, dc), d => d.Cast<Il2CppSystem.Action<Enemy, DamageContainer>>().Invoke(e, dc)))));
        Add(typeof(Enemy), "A_EnemyReleasedFromPool", Conv<Il2CppSystem.Action<Enemy>>(new Action<Enemy>(e =>
            Dispatch("Enemy.A_EnemyReleasedFromPool", OwnerOfHit(e, null), d => d.Cast<Il2CppSystem.Action<Enemy>>().Invoke(e)))));
        Add(typeof(Assets.Scripts.Inventory__Items__Pickups.Chests.InteractableChest), "A_ChestOpened", Conv<Il2CppSystem.Action>(new Action(() =>
            Dispatch("InteractableChest.A_ChestOpened", ChestOwner(), d => d.Cast<Il2CppSystem.Action>().Invoke()))));
        Add(typeof(OpenChest), "A_Open", Conv<Il2CppSystem.Action>(new Action(() =>
            Dispatch("OpenChest.A_Open", ChestOwner(), d => d.Cast<Il2CppSystem.Action>().Invoke()))));
        Add(typeof(DetectInteractables), "A_Interacted", Conv<Il2CppSystem.Action<BaseInteractable, bool>>(new Action<BaseInteractable, bool>((bi, ok) =>
            Dispatch("DetectInteractables.A_Interacted", 1, d => d.Cast<Il2CppSystem.Action<BaseInteractable, bool>>().Invoke(bi, ok)))));
        Add(typeof(GameManager), "A_StageStarted", Conv<Il2CppSystem.Action>(new Action(() =>
            Dispatch("GameManager.A_StageStarted", 0, d => d.Cast<Il2CppSystem.Action>().Invoke()))));
        Add(typeof(Assets.Scripts.Utility.MyTime), "A_TimeScaleChange", Conv<Il2CppSystem.Action>(new Action(() =>
            Dispatch("MyTime.A_TimeScaleChange", 0, d => d.Cast<Il2CppSystem.Action>().Invoke()))));
        // Item effects played by the players' own effect scripts (P2ItemFx; fxscan v0.49): P1's ItemEffects / PlayerEffects
        // and P2's copies - the effect goes to whoever's item fired it
        Add(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemHolyBook), "A_OnUse", Conv<Il2CppSystem.Action<float>>(new Action<float>(r =>
            Dispatch("ItemHolyBook.A_OnUse", P2Scope() ? 2 : 1, d => d.Cast<Il2CppSystem.Action<float>>().Invoke(r)))));
        Add(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemToxicBarrel), "A_OnUse", Conv<Il2CppSystem.Action<float>>(new Action<float>(r =>
            Dispatch("ItemToxicBarrel.A_OnUse", P2Scope() ? 2 : 1, d => d.Cast<Il2CppSystem.Action<float>>().Invoke(r)))));
        Add(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemMirror), "A_MirrorReady", Conv<Il2CppSystem.Action<bool>>(new Action<bool>(ready =>
            Dispatch("ItemMirror.A_MirrorReady", P2Scope() ? 2 : 1, d => d.Cast<Il2CppSystem.Action<bool>>().Invoke(ready), ready))));
        var mirrorRoute = R("ItemMirror.A_MirrorReady");
        if (mirrorRoute != null) mirrorRoute.fixedP2 = o => P2ItemFx.P2MirrorReady((bool)o);
        Add(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase), "A_ItemAdded", Conv<Il2CppSystem.Action<Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase>>(
            new Action<Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase>(it =>
                Dispatch("ItemBase.A_ItemAdded", it?.itemInventoryRef != null && P2Inventory.Items != null && it.itemInventoryRef.Pointer == P2Inventory.Items.Pointer ? 2 : 1,
                    d => d.Cast<Il2CppSystem.Action<Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase>>().Invoke(it)))));
        ModLog.Info($"[itemevents] owner routing for {routes.Count} shared events: {string.Join(", ", routes.Select(r => r.key))}");
    }

    // P2's happening: P2's item run, P2's borrowed health (P2 hit/healed), P2's stat context
    private static bool P2Scope() => P2Items.RunningAsP2 || P2GameHealth.Borrowing || P2Inventory.InP2Context;

    private static T Conv<T>(Delegate d) where T : Il2CppSystem.Delegate => Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<T>(d);

    // 0 = both (each as itself), 1 = P1's items, 2 = P2's items
    private static void Dispatch(string key, int owner, Action<Il2CppSystem.Delegate> call, object arg = null)
    {
        var r = R(key);
        if (r == null) return;
        if (owner != 2 && r.p1 != null) Guard(key + " P1", () => call(r.p1));
        if (owner != 1 && r.fixedP2 != null) Guard(key + " P2 (mod)", () => r.fixedP2(arg));
        if (owner != 1 && r.p2 != null && !P2Health.IsDead)
        {
            var d = r.p2;
            P2Items.RunAsP2(key, () => Guard(key + " P2", () => call(d)));
            P2Inventory.MarkDirty();   // an item may have written into P2's stat holder
        }
        if (owner == 2 && logs++ < 20) ModLog.Info($"[itemevents] {key} -> P2's items{(r.p2 == null ? " (none listening)" : "")}");
    }

    private static readonly HashSet<string> errors = new();
    private static void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { if (errors.Add(what)) ModLog.Warn($"[itemevents] {what}: {e.Message}"); }
    }

    private static int OwnerOfHit(Enemy e, DamageContainer dc) => P2Items.HitOwnerOf(e);

    private static int ChestOwner() => P2Interact.P2OpeningChest || P2Items.RunningAsP2 || P2Inventory.InP2Context ? 2 : 1;

    // P2 interacted (P2Interact): P2's Ghost & co. hear it - the game only fires it for P1's interactions
    public static void P2Interacted(BaseInteractable bi, bool ok)
    {
        var r = R("DetectInteractables.A_Interacted");
        if (r?.p2 == null) return;
        var d = r.p2;
        P2Items.RunAsP2("interacted", () => Guard("A_Interacted P2", () => d.Cast<Il2CppSystem.Action<BaseInteractable, bool>>().Invoke(bi, ok)));
        P2Inventory.MarkDirty();
        if (logs++ < 20) ModLog.Info($"[itemevents] P2 interacted with {bi?.GetIl2CppType().Name}: P2's item listeners called");
    }

    // Every second and after any item change: item listeners off the events (to P1's / P2's lists), the mod's
    // listener on; listeners of items a player no longer has are dropped
    private static float nextKeep;
    public static void Keep(bool now = false)
    {
        if (!now && Time.unscaledTime < nextKeep) return;
        nextKeep = Time.unscaledTime + 1f;
        Ensure();
        var p1Items = Owners(P2Items.P1Items);
        var p2Items = Owners(P2Inventory.Items);
        foreach (var p in P2ItemFx.P1Owned) p1Items.Add(p);   // P1's effect scripts (ItemEffects, PlayerEffects)
        foreach (var p in P2ItemFx.P2Owned) p2Items.Add(p);   // P2's copies
        foreach (var r in routes)
        {
            Il2CppSystem.Delegate ev;
            try { ev = r.prop.GetValue(null) as Il2CppSystem.Delegate; } catch { continue; }
            Il2CppSystem.Delegate keep = null;
            bool hasFwd = false, changed = false;
            if (ev != null)
                foreach (var l in ev.GetInvocationList())
                {
                    if (l.Pointer == r.forwarder.Pointer) { hasFwd = true; keep = Add(keep, l); continue; }
                    var t = l.Target;
                    if (t != null && p1Items.Contains(t.Pointer)) { r.p1 = AddNew(r.p1, l); changed = true; continue; }
                    if (t != null && p2Items.Contains(t.Pointer)) { r.p2 = AddNew(r.p2, l); changed = true; continue; }
                    keep = Add(keep, l);
                }
            r.p1 = Prune(r.p1, p1Items);
            r.p2 = Prune(r.p2, p2Items);
            bool want = r.p1 != null || r.p2 != null;
            if (want && !hasFwd) { keep = Add(keep, r.forwarder); changed = true; }
            if (!want && hasFwd) { keep = Remove(keep, r.forwarder); changed = true; }
            if (!changed) continue;
            try { r.prop.SetValue(null, keep == null ? null : CastTo(keep, r.prop.PropertyType)); }
            catch (Exception e) { if (errors.Add("set " + r.key)) ModLog.Warn($"[itemevents] {r.key} can't be set: {e.Message}"); }
            if (logs++ < 30) ModLog.Info($"[itemevents] {r.key}: P1's items {Count(r.p1)}, P2's items {Count(r.p2)}, routed by owner");
        }
    }

    private static HashSet<IntPtr> Owners(ItemInventory inv)
    {
        var s = new HashSet<IntPtr>();
        if (inv?.items != null) foreach (var kv in inv.items) if (kv.Value != null) s.Add(kv.Value.Pointer);
        return s;
    }
    private static Il2CppSystem.Delegate Add(Il2CppSystem.Delegate a, Il2CppSystem.Delegate b) => a == null ? b : Il2CppSystem.Delegate.Combine(a, b);
    private static Il2CppSystem.Delegate AddNew(Il2CppSystem.Delegate a, Il2CppSystem.Delegate b)
    {
        if (a != null) foreach (var l in a.GetInvocationList()) if (l.Target?.Pointer == b.Target?.Pointer && l.Method?.Name == b.Method?.Name) return a;
        return Add(a, b);
    }
    private static Il2CppSystem.Delegate Remove(Il2CppSystem.Delegate a, Il2CppSystem.Delegate b)
    {
        if (a == null) return null;
        Il2CppSystem.Delegate keep = null;
        foreach (var l in a.GetInvocationList()) if (l.Pointer != b.Pointer) keep = Add(keep, l);
        return keep;
    }
    private static Il2CppSystem.Delegate Prune(Il2CppSystem.Delegate a, HashSet<IntPtr> alive)
    {
        if (a == null) return null;
        Il2CppSystem.Delegate keep = null;
        foreach (var l in a.GetInvocationList()) if (l.Target != null && alive.Contains(l.Target.Pointer)) keep = Add(keep, l);
        return keep;
    }
    private static int Count(Il2CppSystem.Delegate d) => d == null ? 0 : d.GetInvocationList().Length;
    private static object CastTo(Il2CppSystem.Delegate d, Type t) =>
        typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod(nameof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase.Cast))!.MakeGenericMethod(t).Invoke(d, null);

    // A P1 listener that may go back on the game's event: its object still exists and still belongs to P1's player.
    // v1.0.3 report "after restarting once, Cheese / Cursed Doll / Soul Harvester / Slutty Cannon stop working": quit to
    // the main menu, new run - the run's scene went away while P1's ToxicBarrelEffect / HolyBookEffect listeners sat in
    // the mod's list, so their own OnDestroy (-= Refresh) found nothing to take off; Reset then put them back on
    // ItemBase.A_ItemAdded with their objects destroyed. Every AddItem of the next run threw inside the game's
    // ToxicBarrelEffect.Refresh (NullReference in get_gameObject) before the item was filed as on-hit / set up - for
    // both players. The restart buttons reset before the scene goes, so they never hit it.
    private static bool Alive(Il2CppSystem.Delegate l)
    {
        var t = l?.Target;
        if (t == null) return false;
        var uo = t.TryCast<UnityEngine.Object>();
        if (!ReferenceEquals(uo, null)) return uo;   // Unity's own check: false for a destroyed scene object
        var item = t.TryCast<ItemBase>();
        if (item != null)
        {
            var p1 = P2Items.P1Items;   // an item of an earlier run's inventory: gone with it
            return p1 != null && item.itemInventoryRef != null && item.itemInventoryRef.Pointer == p1.Pointer;
        }
        return true;
    }

    // A listener on the game's event left over from an earlier run: a destroyed object, or an item of neither player's
    // current inventory (anything else - the game's own listeners - stays)
    private static bool Leftover(Il2CppSystem.Delegate l)
    {
        var t = l?.Target;
        if (t == null) return false;
        var uo = t.TryCast<UnityEngine.Object>();
        if (!ReferenceEquals(uo, null)) return !uo;
        var item = t.TryCast<ItemBase>();
        if (item == null) return false;
        var inv = item.itemInventoryRef;
        var p1 = P2Items.P1Items;
        var p2 = P2Inventory.Items;
        return inv == null || !((p1 != null && inv.Pointer == p1.Pointer) || (p2 != null && inv.Pointer == p2.Pointer));
    }

    public static void Reset()
    {
        // The mod's listeners come off the events; P1's listeners go back on (P1's effect scripts stay with P1's player)
        // - only those whose objects are still there
        int dropped = 0;
        foreach (var r in routes)
        {
            try
            {
                var ev = r.prop.GetValue(null) as Il2CppSystem.Delegate;
                // Also off the event itself: items of no current player's inventory (v1.0.3 scan: the game's own Cleanup of
                // Demonic Blood / Demonic Soul / Joe's Dagger subscribes them once more - 10 -> 12 listeners when P2's
                // holder was cleaned up - so last run's items kept answering every enemy death) and destroyed objects
                Il2CppSystem.Delegate keep = null;
                foreach (var l in Remove(ev, r.forwarder)?.GetInvocationList() ?? Array.Empty<Il2CppSystem.Delegate>())
                    if (Leftover(l)) dropped++; else keep = Add(keep, l);
                if (r.p1 != null)
                    foreach (var l in r.p1.GetInvocationList())
                        if (Alive(l)) keep = Add(keep, l); else dropped++;
                r.prop.SetValue(null, keep == null ? null : CastTo(keep, r.prop.PropertyType));
            }
            catch { }
            r.p1 = r.p2 = null;
        }
        if (dropped > 0) ModLog.Info($"[itemevents] reset: {dropped} listener(s) of objects / items gone with the last run taken off");
    }
}
