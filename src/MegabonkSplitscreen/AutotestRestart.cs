using System.Reflection;
using UnityEngine;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;

namespace MegabonkSplitscreen;

// TEMP autotest commands for the players' report "after restarting once, Moldy Cheese, Cursed Doll, Soul Harvester and
// Slutty Cannon stop working with two players":
//   eventcheck [label]       each shared event the mod routes: who listens - the mod's forwarder, current P1 / P2
//                            items, and anything else (an item of no current inventory = left over from an earlier run)
//                            plus both players' on-hit item lists
//   srccount reset|<label>   damage dealt per damage source since the reset (the game's Enemy.A_Damage), to see whether
//                            the items' own damage happens at all
internal static partial class Autotest
{
    private static string EventCheck()
    {
        var p1Items = Set(P2Items.P1Items);
        var p2Items = Set(P2Inventory.Items);
        var parts = new List<string>();
        foreach (var (type, name) in new[] { (typeof(Enemy), "A_EnemyDied"), (typeof(Enemy), "A_Damage"), (typeof(Enemy), "A_EnemyReleasedFromPool"), (typeof(ItemBase), "A_ItemAdded") })
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            Il2CppSystem.Delegate ev = null;
            try { ev = p?.GetValue(null) as Il2CppSystem.Delegate; } catch { }
            if (ev == null) { parts.Add($"{name}: none"); continue; }
            int total = 0, mine = 0, cur1 = 0, cur2 = 0;
            var others = new List<string>();
            foreach (var l in ev.GetInvocationList())
            {
                total++;
                var t = l.Target;
                string tn = "?";
                try { tn = t != null ? t.GetIl2CppType().Name : "static"; } catch { }
                var uo = t?.TryCast<UnityEngine.Object>();
                if (!ReferenceEquals(uo, null) && !uo) { others.Add($"DESTROYED {tn}"); continue; }
                if (t != null && p1Items.Contains(t.Pointer)) { cur1++; continue; }
                if (t != null && p2Items.Contains(t.Pointer)) { cur2++; continue; }
                if (tn.StartsWith("Item")) { others.Add($"OLD {tn}"); continue; }
                if (tn.Contains("DisplayClass") || tn == "?" || tn.Contains("Delegate")) { mine++; continue; }
                others.Add(tn);
            }
            parts.Add($"{name}: {total} listeners (P1 items {cur1}, P2 items {cur2}, other/mod {mine}, {string.Join(" ", others)})");
        }
        int OnHit(ItemInventory inv) => inv?.itemsWithOnHitProcs?.Count ?? -1;
        parts.Add($"on-hit items P1 {OnHit(P2Items.P1Items)} / P2 {OnHit(P2Inventory.Items)}; P1 [{P2Items.DescribeP1()}]; P2 [{P2Items.Describe(P2Inventory.Items)}]");
        return string.Join("; ", parts);
    }

    // Every player object and its item inventory (run 2 after the main menu: an inventory of neither player ticks)
    private static string InvCheck()
    {
        var parts = new List<string>();
        var inst = MyPlayer.Instance;
        parts.Add($"MyPlayer.Instance {(inst != null ? $"'{inst.name}' {inst.Pointer:X} scene '{inst.gameObject.scene.name}' inv {inst.inventory?.itemInventory?.Pointer:X}" : "null")}");
        parts.Add($"PlayerMovement.Instance {(PlayerMovement.Instance != null ? $"{PlayerMovement.Instance.Pointer:X} '{PlayerMovement.Instance.gameObject.scene.name}'" : "null")}");
        parts.Add($"P2 inv {P2Inventory.Items?.Pointer:X}");
        foreach (var mp in UnityEngine.Resources.FindObjectsOfTypeAll<MyPlayer>())
        {
            var inv = mp.inventory?.itemInventory;
            parts.Add($"MyPlayer '{mp.name}' {mp.Pointer:X} scene '{mp.gameObject.scene.name}' active {mp.gameObject.activeInHierarchy} inv {inv?.Pointer:X} items [{P2Items.Describe(inv)}] onhit {inv?.itemsWithOnHitProcs?.Count} sorted {inv?.itemsWithOnHitSorted?.Count}");
        }
        return string.Join("; ", parts);
    }

    // Every static event of the game (Assembly-CSharp): listeners whose object is gone - destroyed scene objects, items
    // / item holders of neither player's current inventory (left over from an earlier run)
    // floatcheck: Birdo/Roberto float (PlayerMovement.CanFloat) for both players, normally and in P2's stat context
    private static string FloatCheck()
    {
        var p1Pm = MyPlayer.Instance?.playerMovement;
        var p2Pm = P2Movement.Pm;
        string F(PlayerMovement m) { try { return m == null ? "-" : m.CanFloat().ToString(); } catch (Exception e) { return "err " + e.Message; } }
        string M(PlayerMovement m) => m == null ? "-" : $"y {m.transform.position.y:F1}, vy {m.rb?.velocity.y:F1}, grounded {m.grounded}, jumping {m.jumping}, state {m.lastMovementState}";
        string s = $"P1 CanFloat {F(p1Pm)} ({M(p1Pm)}) | P2 CanFloat {F(p2Pm)} ({M(p2Pm)})";
        bool inP2 = P2Inventory.EnterP2();
        try { s += $" | in P2's context: P1 {F(p1Pm)}, P2 {F(p2Pm)}"; }
        finally { if (inP2) P2Inventory.Exit(); }
        s += $" | passives: P1 {MyPlayer.Instance?.inventory?.passiveAbility?.GetPassiveType()}, P2 {P2Abilities.Passive?.GetPassiveType()}";
        return s;
    }

    // enemysplit: who the enemies are near / chasing (players' report: P2 has a horde, P1 2-3)
    private static string EnemySplit()
    {
        var em = Assets.Scripts.Managers.EnemyManager.Instance;
        Vector3 a1 = MyPlayer.Instance.transform.position, a2 = P2Movement.BodyPosition();
        int total = 0, near1 = 0, near2 = 0, chase1 = 0, chase2 = 0, close1 = 0, close2 = 0;
        var names = new Dictionary<string, int>();
        if (em?.enemies != null)
            foreach (var kv in em.enemies)
            {
                var e = kv.Value;
                if (e == null || e.IsDead()) continue;
                total++;
                Vector3 p = e.transform.position;
                float d1 = Vector3.Distance(p, a1), d2 = Vector3.Distance(p, a2);
                if (d1 < d2) near1++; else near2++;
                if (d1 < 30f) close1++;
                if (d2 < 30f) close2++;
                if (P2Health.TargetsP2(e)) chase2++; else chase1++;
            }
        return $"{total} enemies: nearer P1 {near1} / P2 {near2}; within 30 m: P1 {close1} / P2 {close2}; chasing P1 {chase1} / P2 {chase2}; players {Vector3.Distance(a1, a2):F0} m apart; since reset: {P2Spawns.Counts()}";
    }

    private static readonly HashSet<IntPtr> seenP1 = new(), seenP2 = new();
    private static string DeadListeners()
    {
        var p1Inv = MyPlayer.Instance?.inventory?.itemInventory;
        var p2Inv = P2Inventory.Items;
        if (p1Inv != null) seenP1.Add(p1Inv.Pointer);
        if (p2Inv != null) seenP2.Add(p2Inv.Pointer);
        bool Current(ItemInventory inv) => inv != null && ((p1Inv != null && inv.Pointer == p1Inv.Pointer) || (p2Inv != null && inv.Pointer == p2Inv.Pointer));
        var found = new List<string>();
        int events = 0, listeners = 0;
        foreach (var t in typeof(ItemBase).Assembly.GetTypes())
        {
            PropertyInfo[] props;
            try { props = t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { continue; }
            foreach (var p in props)
            {
                if (!typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
                Il2CppSystem.Delegate ev;
                try { ev = p.GetValue(null) as Il2CppSystem.Delegate; } catch { continue; }
                if (ev == null) continue;
                events++;
                var bad = new List<string>();
                foreach (var l in ev.GetInvocationList())
                {
                    listeners++;
                    var target = l.Target;
                    if (target == null) continue;
                    string tn = "?";
                    try { tn = target.GetIl2CppType().Name; } catch { }
                    var uo = target.TryCast<UnityEngine.Object>();
                    if (!ReferenceEquals(uo, null)) { if (!uo) bad.Add($"destroyed {tn}.{l.Method?.Name}"); continue; }
                    var item = target.TryCast<ItemBase>();
                    if (item != null)
                    {
                        var ir = item.itemInventoryRef;
                        string whose = ir == null ? "no holder" : seenP1.Contains(ir.Pointer) ? "P1's" : seenP2.Contains(ir.Pointer) ? "P2's" : "unknown";
                        if (!Current(ir)) bad.Add($"old {whose} item {tn}.{l.Method?.Name}");
                        else if (item.TryCast<ItemDemonicBlood>() != null || item.TryCast<ItemDemonicSoul>() != null || item.TryCast<ItemJoesDagger>() != null)
                            bad.Add($"live {(p1Inv != null && ir.Pointer == p1Inv.Pointer ? "P1's" : "P2's")} item ON THE EVENT {tn}.{l.Method?.Name}");
                        continue;
                    }
                    var inv = target.TryCast<ItemInventory>();
                    if (inv != null && !Current(inv)) bad.Add($"old item holder .{l.Method?.Name}");
                }
                if (bad.Count > 0) found.Add($"{t.Name}.{p.Name}: {string.Join(", ", bad.GroupBy(b => b).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key))}");
            }
        }
        return $"{events} events, {listeners} listeners; {(found.Count == 0 ? "no dead listeners" : string.Join("; ", found))}";
    }

    private static HashSet<IntPtr> Set(ItemInventory inv)
    {
        var s = new HashSet<IntPtr>();
        if (inv?.items != null) foreach (var kv in inv.items) if (kv.Value != null) s.Add(kv.Value.Pointer);
        return s;
    }

    private static readonly Dictionary<string, float> srcCounts = new();
    private static Il2CppSystem.Action<Enemy, DamageContainer> srcCounter;
    private static void EnsureSrcCounter()
    {
        srcCounter ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Enemy, DamageContainer>>(
            new Action<Enemy, DamageContainer>((e, dc) =>
            {
                int owner = 0;
                try { owner = P2Items.HitOwnerOf(e); } catch { }
                string k = $"P{(owner == 2 ? 2 : 1)} {dc?.damageSource ?? "?"}";
                srcCounts[k] = (srcCounts.TryGetValue(k, out var v) ? v : 0f) + (dc?.damage ?? 0f);
            }));
        var now = Enemy.A_Damage;
        if (now != null) foreach (var l in now.GetInvocationList()) if (l.Pointer == srcCounter.Pointer) return;
        Enemy.A_Damage = now == null ? srcCounter : Il2CppSystem.Delegate.Combine(now, srcCounter).Cast<Il2CppSystem.Action<Enemy, DamageContainer>>();
    }
}
