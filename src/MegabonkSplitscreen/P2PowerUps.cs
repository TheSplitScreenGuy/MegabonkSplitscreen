using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Player 2's power-ups (user: every pickupable power-up - magnet, clock, heart, ... - with its effects and visuals).
// In the game, Pickup.ApplyPickup fires Pickup.A_PickupTriggered and its listeners do the work (v0.44.0 log):
// PlayerHealth.OnPickup (heart), PlayerStatusEffects.OnPickupTriggered (Haste, Rage, Shield, Stonks... as timed
// status effects that change stats), EffectManager.OnPickup (effects, magnet), GoldFilterUI.OnPickup (screen filter),
// AchievementTracker / TrackStats (P1's records).
//  - P2 has its own PlayerStatusEffects (made once per run; its listener on the game's pickup event is taken off).
//    In P2's stat context P1's inventory holds it (P2Inventory), so P2's stats count P2's effects - and no longer P1's
//    (v0.44.0 log: P2 'got' P1's Haste and Shield).
//  - A power-up collected by P2 runs with the event muted, and each listener is called for P2: the heart on P2's
//    borrowed health, the status on P2's list, the effects with P2 standing in for "the player", the filter as is.
//    The record keepers are skipped.
internal static class P2PowerUps
{
    public static PlayerStatusEffects Status { get; private set; }
    private static int lastCount = -1;
    private static int logs;
    private static readonly HashSet<string> loggedErrors = new();

    public static bool IsPowerUp(EPickup e) => e != EPickup.Xp && e != EPickup.Gold && e != EPickup.Silver;

    public static void Ensure()
    {
        if (Status != null || MyPlayer.Instance == null) return;
        try
        {
            var before = Pickup.A_PickupTriggered;
            Status = new PlayerStatusEffects();
            if (Status.statusEffects == null) Status.statusEffects = new Il2CppSystem.Collections.Generic.Dictionary<EStatusEffect, StatusEffect>();
            // Its own listener on the pickup event (if the constructor added one) is taken off: P1's pickups aren't P2's
            var now = Pickup.A_PickupTriggered;
            if (now != null && (before == null || now.Pointer != before.Pointer))
            {
                Pickup.A_PickupTriggered = before;
                ModLog.Info("[powerup] P2's status list took a pickup listener - taken off (P1's pickups stay P1's)");
            }
            ModLog.Info("[powerup] P2's own status effect list made");
        }
        catch (Exception e) { ModLog.Error($"[powerup] P2 status list failed: {e}"); Status = null; }
    }

    // P2Debuffs: P2's list (made if needed) and a call on it the way P2's ticks run (muted, P2's stats)
    public static PlayerStatusEffects EnsureStatus() { Ensure(); return Status; }
    public static void OnStatus(Action a) { if (EnsureStatus() != null) Muted(a); }
    public static string DescribeStatus() => Describe();

    // A game call that gives "the player" status effects, with P2's list as the player's (P1's HUD/model not told)
    public static void OnStatusSwapped(Action a)
    {
        var inv = MyPlayer.Instance?.inventory;
        var mine = EnsureStatus();
        if (inv == null || mine == null) { a(); return; }
        var real = inv.statusEffects;
        inv.statusEffects = mine;
        try { Muted(a); }
        finally { inv.statusEffects = real; }
        P2Inventory.MarkDirty();
    }

    public static void Reset()
    {
        Status = null;
        lastCount = -1;
        foreach (var (v, _) in visuals) if (v != null) Object.Destroy(v.gameObject);
        visuals.Clear();
        bubble = null;   // went with P2's body
    }

    // Each unpaused frame: P2's effects run out on their own timers
    public static void Update()
    {
        if (!P2Movement.Active) return;
        Ensure();
        if (Status == null || Time.deltaTime <= 0f || Assets.Scripts.Utility.MyTime.paused) return;
        // Bleed/Poison/BossPoison damage "the player" in the tick: on P2's borrowed health (else P1 took it), P1 guarded
        // if that isn't possible
        if (HurtsP2()) { if (!P2Health.StatusTick(() => Muted(() => Status.Tick()))) P2Explosions.GuardP1("P2's debuff tick", () => Muted(() => Status.Tick())); }
        else Muted(() => Status.Tick());
        UpdateVisuals();
        int count = Status.statusEffects?.Count ?? 0;
        if (count != lastCount)
        {
            lastCount = count;
            P2Inventory.MarkDirty();
            if (logs++ < 10) ModLog.Info($"[powerup] P2's status effects now: {Describe()}");
        }
    }

    private static bool HurtsP2()
    {
        var d = Status?.statusEffects;
        return d != null && (d.ContainsKey(EStatusEffect.Bleed) || d.ContainsKey(EStatusEffect.Poison) || d.ContainsKey(EStatusEffect.BossPoison));
    }

    private static string Describe()
    {
        if (Status?.statusEffects == null || Status.statusEffects.Count == 0) return "none";
        var parts = new List<string>();
        foreach (var kv in Status.statusEffects) parts.Add($"{kv.Key} ({(kv.Value != null ? kv.Value.expirationTime - Time.time : 0f):F1} s)");
        return string.Join(", ", parts);
    }

    // P2's status list changes without P1's listeners hearing it (P1's HUD icons, P1's model colour)
    private static System.Reflection.PropertyInfo[] statusEvents;
    private static void Muted(Action a)
    {
        statusEvents ??= typeof(PlayerStatusEffects).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType) && p.CanWrite).ToArray();
        var saved = new List<object>();
        foreach (var p in statusEvents) { saved.Add(p.GetValue(null)); p.SetValue(null, null); }
        bool entered = P2Inventory.EnterP2();
        try { a(); }
        catch (Exception e) { if (loggedErrors.Add(e.Message)) ModLog.Warn($"[powerup] P2 status call failed: {e.Message}"); }
        finally
        {
            if (entered) P2Inventory.Exit();
            for (int i = 0; i < statusEvents.Length; i++) statusEvents[i].SetValue(null, saved[i]);
        }
    }

    // A power-up collected by P2 (P2Pickups): the game's ApplyPickup with its event muted, then each listener for P2
    private static bool applying;
    public static bool Applying => applying;
    public static void ApplyForP2(Pickup p)
    {
        if (p == null || applying) return;
        Ensure();
        var e = p.ePickup;
        // 1.0 alpha (user: P2's clock didn't slow time like P1's): it slows every enemy, so it works as P1's pickup -
        // on P2's own list nothing checked it
        if (e == EPickup.Time)
        {
            try { p.ApplyPickup(); } catch (Exception ex) { ModLog.Warn($"[powerup] P2's clock failed: {ex.Message}"); }
            ModLog.Info("[powerup] P2 collected the clock: applied as P1's (slows every enemy)");
            return;
        }
        var ev = Pickup.A_PickupTriggered;
        applying = true;
        Pickup.A_PickupTriggered = null;
        // 1.0 alpha (user: P2's magnet sent the XP to P1; magnet_p2 test: the gems are already flying to P1 when this
        // returns - the magnet pulls inside ApplyPickup): run it as a pull for P2
        try { if (e == EPickup.Magnet) P2Pickups.PullAsP2("magnet", () => p.ApplyPickup()); else p.ApplyPickup(); }
        catch (Exception ex) { ModLog.Warn($"[powerup] ApplyPickup for P2 failed: {ex.Message}"); }
        finally { Pickup.A_PickupTriggered = ev; applying = false; }
        if (ev == null) return;
        var done = new List<string>();
        foreach (var l in ev.GetInvocationList())
        {
            string type = "?";
            try { type = l.Method?.DeclaringType?.Name ?? "?"; } catch { }
            try
            {
                switch (type)
                {
                    case "PlayerHealth":
                        P2GameHealth.Borrow("power-up", h => Call(l, p));
                        done.Add("heart/health on P2");
                        break;
                    case "PlayerStatusEffects":
                        if (Status != null) { Muted(() => Status.OnPickupTriggered(p)); done.Add("status on P2"); }
                        break;
                    case "EffectManager":
                        // v0.44.2 (autotest screenshots: P1 had the shield bubble / speed lines, P2 none): the lasting
                        // visual the game makes here (StatusEffectPickup) follows "the player" - P2's is taken over
                        var before = new HashSet<IntPtr>(Object.FindObjectsOfType<StatusEffectPickup>().Select(x => x.Pointer));
                        AsP2Player(() => Call(l, p));
                        foreach (var v in Object.FindObjectsOfType<StatusEffectPickup>())
                            if (!before.Contains(v.Pointer)) TakeVisual(v);
                        done.Add("effects at P2");
                        break;
                    case "AchievementTracker":
                    case "TrackStats":
                    case "Shield":                 // P1's own visuals: a P2 pickup mustn't refresh them
                    case "StatusEffectPickup":
                        break;
                    default:
                        Call(l, p);
                        done.Add(type);
                        break;
                }
            }
            catch (Exception ex) { if (loggedErrors.Add(type + ex.Message)) ModLog.Warn($"[powerup] {type} for P2 failed: {ex.Message}"); }
        }
        P2Inventory.MarkDirty();
        if (logs++ < 20) ModLog.Info($"[powerup] P2 collected {e}: {string.Join(", ", done)}; P2's effects: {Describe()}");
    }

    // P2's lasting power-up visuals: their own update (which follows P1 and checks P1's status list) is off; they stay at
    // P2's feet and go when P2's effect runs out
    private static readonly List<(StatusEffectPickup v, EStatusEffect s)> visuals = new();
    private static void TakeVisual(StatusEffectPickup v)
    {
        v.enabled = false;
        visuals.Add((v, v.statusEffect));
        // Only a look for P2 (like the shield bubble copy): no colliders the game's enemies could take for "the player"
        int cols = 0;
        if (ShieldFix) foreach (var c in v.GetComponentsInChildren<Collider>(true)) if (c != null) { Object.Destroy(c); cols++; }
        if (logs++ < 20) ModLog.Info($"[powerup] P2's {v.statusEffect} visual '{v.name}' follows P2 ({cols} colliders taken off)");
    }

    // The shield power-up's bubble: P1's is the game's Shield component on P1; P2 gets an inert copy on P2's body,
    // shown while P2's Shield effect lasts (v0.44.3, autotest screenshot: P1 in the green bubble, P2 without)
    private static GameObject bubble;
    public static bool ShieldFix = true;   // TEMP autotest switch ('fix shield off'): colliders taken off P2's bubble copy
    private static void UpdateBubble()
    {
        bool want = Status?.statusEffects != null && Status.statusEffects.ContainsKey(EStatusEffect.Shield);
        var body = P2Weapons.Body;
        if (bubble == null && want && body != null)
        {
            var src = Object.FindObjectOfType<Shield>(true);
            var p1Model = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
            if (src == null || p1Model == null) return;
            var hold = new GameObject("hold");
            hold.SetActive(false);
            bubble = Object.Instantiate(src.gameObject, hold.transform);
            foreach (var mb in bubble.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetIl2CppType().Assembly.GetName().Name == "Assembly-CSharp") Object.DestroyImmediate(mb);
            // 1.0.2 (players: "if p2 has the shield power up and gets hit, p1 takes that damage ... ragdoll yeets p1 out of
            // the map"; bug_shield_powerup: P2 shielded -> P1 lost HP and was flung at up to 3500 m/s): the copy is only a
            // look - its colliders count as "the player" for the game's enemies, which then hit and push P1 from 300 m away
            var removed = new List<string>();
            if (ShieldFix)
            {
                foreach (var c in bubble.GetComponentsInChildren<Collider>(true))
                    if (c != null) { removed.Add($"{c.GetIl2CppType().Name} '{c.name}' layer {LayerMask.LayerToName(c.gameObject.layer)} trigger {c.isTrigger}"); Object.DestroyImmediate(c); }
                foreach (var r in bubble.GetComponentsInChildren<Rigidbody>(true))
                    if (r != null) { removed.Add($"Rigidbody '{r.name}'"); Object.DestroyImmediate(r); }
            }
            ModLog.Info($"[powerup] P2's shield bubble: removed from the copy: {(removed.Count == 0 ? "nothing" : string.Join(", ", removed))}");
            bubble.name = "MegabonkSplitscreen_P2ShieldBubble";
            bubble.transform.SetParent(body.transform, false);
            bubble.transform.localPosition = p1Model.InverseTransformPoint(src.transform.position);
            bubble.transform.localScale = src.defaultScale.sqrMagnitude > 0.001f ? src.defaultScale : src.transform.localScale;
            Object.Destroy(hold);
            ModLog.Info($"[powerup] P2's shield bubble copied from '{src.name}' (scale {bubble.transform.localScale})");
        }
        if (bubble != null && bubble.activeSelf != want) bubble.SetActive(want);
    }

    private static void UpdateVisuals()
    {
        UpdateBubble();
        var pm = P2Movement.Pm;
        for (int i = visuals.Count - 1; i >= 0; i--)
        {
            var (v, s) = visuals[i];
            bool keep = v != null && pm != null && Status?.statusEffects != null && Status.statusEffects.ContainsKey(s);
            if (!keep)
            {
                if (v != null) Object.Destroy(v.gameObject);
                visuals.RemoveAt(i);
                continue;
            }
            v.transform.position = v.useFeetPosition ? pm.transform.position : pm.transform.position + Vector3.up;
            if (v.rotateToPlayerVelocity && pm.rb != null && pm.rb.velocity.sqrMagnitude > 0.5f)
                v.transform.rotation = Quaternion.LookRotation(new Vector3(pm.rb.velocity.x, 0f, pm.rb.velocity.z).normalized + Vector3.forward * 0.0001f);
        }
    }

    private static void Call(Il2CppSystem.Delegate l, Pickup p) => l.Cast<Il2CppSystem.Action<Pickup>>().Invoke(p);

    // "The player" is P2 for the call: P2's movement copy, and P1's own object standing at P2's spot (put back after)
    private static void AsP2Player(Action a)
    {
        var p1 = MyPlayer.Instance;
        var pm = P2Movement.Pm;
        if (p1 == null || pm == null) { a(); return; }
        var p1Pm = PlayerMovement.Instance;
        Vector3 p1Pos = P2Items.P1Home(p1);
        try
        {
            PlayerMovement.Instance = pm;
            p1.transform.position = pm.transform.position;
            a();
        }
        finally
        {
            p1.transform.position = p1Pos;
            if (p1Pm != null) PlayerMovement.Instance = p1Pm;
        }
    }
}
