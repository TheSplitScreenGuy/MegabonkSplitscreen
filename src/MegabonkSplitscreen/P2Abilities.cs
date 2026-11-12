using System.Reflection;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesActives;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Rewired;
using UnityEngine;

namespace MegabonkSplitscreen;

// Player 2's character ability (user: "make p2 support every character's special ability", e.g. Megachad's Flex that
// ignores a hit; "make sure rewards that effect the ability also work").
//
// Each character has a passive (PassiveAbility: Flex, Bullseye, SpeedDemon, Shadowstep, ... 21 kinds), made by the
// game's PassiveAbilityFactory from CharacterData.passive and kept in PlayerInventory.passiveAbility. Most of them
// grow with the character level (OnLevelup) and add stat modifiers; some react to game events (a hit, an evade, an
// enemy dying). P2 gets its own one, made by the same factory from P2's character:
//  - Events: P2's passive subscribes to the game's global events in Init(), like P1's. Those events fire for P1, so
//    right after P2's Init every global event is compared with a snapshot taken before it; P2's new listeners are
//    taken off again (the bug class of v0.16-0.17's second inventory) and the mod calls them only for P2's own
//    happenings: health events while P2's health runs (P2GameHealth), movement events while P2's movement runs
//    (P2Movement), the level-up event on P2's level-ups. Enemy events (an enemy hit / killed / spawned) can't tell
//    which player caused them (no owner in the game's damage data), so P2's listeners stay on those: both passives
//    see every enemy event, and the hooks below let each passive react only to enemies nearer to its own player.
//  - Stats: during P2's stat context, P1's inventory holds P2's passive (P2Inventory), so P2's stats include P2's
//    passive bonuses and the game's "has passive X" checks (e.g. wall climbing) answer for P2.
//  - Static numbers (e.g. Flex's cooldown timer is one static number for the class): swapped in and out around
//    P2's calls, so P1 and P2 playing the same character keep their own cooldowns.
//  - Each frame P2's passive's Tick runs in P2's context.
// P1's active ability (AbilityDash), if P1 has one, is given to P2 the same way, on P2's pad button for P1's binding.
internal static class P2Abilities
{
    public static PassiveAbility Passive { get; private set; }
    public static bool FloatFix = true;   // Birdo / Roberto float for P2 (v1.0.5; P2Movement.FixedTick, FixedTick below)
    public static bool HasFloat
    {
        get { try { return Passive != null && Passive.GetPassiveType() == EPassive.Float; } catch { return false; } }
    }
    public static ActiveAbility Active { get; private set; }
    private static bool tried;
    private static List<PropertyInfo> allEvents;
    // P2's own listeners, per global event ("Type.A_Name"), taken off the event; the mod uses them for P2's happenings
    private static readonly Dictionary<string, Il2CppSystem.Delegate> routed = new();
    private static PropertyInfo[] passiveStatics = Array.Empty<PropertyInfo>();
    private static readonly Dictionary<PropertyInfo, object> p2Statics = new();
    private static readonly Dictionary<PropertyInfo, object> p1Statics = new();
    private static int contextDepth;
    private static PropertyInfo[] abilityEvents = Array.Empty<PropertyInfo>();
    private static readonly List<object> mutedAbilityEvents = new();
    private static readonly HashSet<string> loggedErrors = new();
    private static int levelLogs;
    private static string passiveName = "";
    private static bool usesGold;
    private static int staticLogs;
    private static IntPtr otherPtr;
    private static string otherName;

    // ---------- Hooks: enemy events go to the nearer player's passive ----------

    public static void Apply(Harmony harmony)
    {
        int n = 0;
        var pre = new HarmonyMethod(typeof(P2Abilities), nameof(BeforeEnemyEvent));
        var fin = new HarmonyMethod(typeof(P2Abilities), nameof(AfterEnemyEvent));
        Type[] all;
        try { all = typeof(PassiveAbility).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
        foreach (var t in all.Where(t => t.IsSubclassOf(typeof(PassiveAbility))))
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(Assets.Scripts.Actors.Enemies.Enemy)) continue;
                if (!(m.Name.Contains("Damage") || m.Name.Contains("Died"))) continue;   // hits and kills (not spawns)
                if (!P2Weapons.IsNativeCodeUnique(m) || P2Weapons.IsNativeCodeHooked(m))
                {
                    ModLog.Warn($"P2Abilities: {t.Name}.{m.Name} shares native code - not hooked (both players' passives see every enemy)");
                    continue;
                }
                harmony.Patch(m, prefix: pre, finalizer: fin);
                n++;
                ModLog.Info($"P2Abilities: enemy event {t.Name}.{m.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))}) hooked");
            }
        ModLog.Info($"P2Abilities: {n} enemy-event methods hooked");
    }

    // P2's passive reacts to enemies nearer P2, P1's to enemies nearer P1 (only when P2 is playing)
    private static bool BeforeEnemyEvent(PassiveAbility __instance, Assets.Scripts.Actors.Enemies.Enemy __0, out bool __state)
    {
        __state = false;
        var p2 = P2Movement.Pm;
        var p1 = MyPlayer.Instance;
        if (Passive == null || p2 == null || p1 == null || __0 == null) return true;
        bool mine = __instance.Pointer == Passive.Pointer;
        // P1's passive is only filtered when P2 has the same kind (otherwise P1's works exactly as in single player)
        if (!mine)
        {
            if (__instance.Pointer != otherPtr) { otherPtr = __instance.Pointer; otherName = SafeTypeName(__instance); }
            if (otherName != passiveName) return true;
        }
        Vector3 e = __0.transform.position;
        bool nearerP2 = (e - p2.transform.position).sqrMagnitude < (e - p1.transform.position).sqrMagnitude;
        if (mine != nearerP2) return false;
        if (mine) __state = Enter();
        return true;
    }

    private static Exception AfterEnemyEvent(Exception __exception, bool __state)
    {
        if (__state) Exit();
        return __exception;
    }

    // ---------- Lifetime ----------

    // Called every frame; makes P2's abilities once P2 is in the run (P2's movement and stats exist)
    public static void Update()
    {
        if (!tried && P2Inventory.Weapons != null && P2Movement.Pm != null && P2Inventory.P2Character != null)
        {
            tried = true;
            Create(P2Inventory.P2Character);
        }
        if (Time.deltaTime > 0f && !Assets.Scripts.Utility.MyTime.paused) Guard("aegis events", UpdateAegis);
        if (Passive == null && Active == null) return;
        if (Time.deltaTime <= 0f || Assets.Scripts.Utility.MyTime.paused || P2Health.IsDead) return;
        // P2's passive ticks in FixedTick (below), like the game's
        Guard("flex effect", UpdateFlexFx);
        if (Active != null)
        {
            Guard("active tick", () => InP2(() => Active.Tick()));
            Guard("active button", CheckActiveButton);
        }
    }

    private static void Create(CharacterData data)
    {
        if (allEvents == null) FindEvents();
        var inv = MyPlayer.Instance?.inventory;
        string p1Passive = inv?.passiveAbility != null ? SafeTypeName(inv.passiveAbility) : "none";
        string p1Active = inv?.activeAbility != null ? SafeTypeName(inv.activeAbility) : "none";
        ModLog.Info($"P2Abilities: P1 has passive {p1Passive}, active {p1Active}; P2 is {data.eCharacter}, passive data {(data.passive != null ? data.passive.ePassive.ToString() : "none")}");
        LogActions();

        if (data.passive != null)
        {
            try
            {
                Passive = MakeWithEvents("passive", () => PassiveAbilityFactory.CreatePassiveAbility(data.passive), a => a.Init());
                if (Passive != null)
                {
                    passiveName = SafeTypeName(Passive);
                    usesGold = passiveName.Contains("Stonks");
                    passiveStatics = StaticNumbers(Passive);
                    abilityEvents = typeof(PassiveAbility).GetProperties(BindingFlags.Public | BindingFlags.Static)
                        .Concat(ManagedType(Passive)?.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly) ?? Array.Empty<PropertyInfo>())
                        .Where(p => typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType) && p.CanWrite).ToArray();
                    // P2's starting static numbers = the class defaults P1's passive started with (e.g. Flex ready)
                    foreach (var p in passiveStatics) p2Statics[p] = p.GetValue(null);
                    ModLog.Info($"P2Abilities: P2 passive {SafeTypeName(Passive)} ({Passive.GetPassiveType()}); static numbers kept per player: " +
                                (passiveStatics.Length == 0 ? "none" : string.Join(", ", passiveStatics.Select(p => $"{p.Name}={p.GetValue(null)}"))) +
                                $"; events it fires (muted for P1's listeners during P2's calls): {string.Join(", ", abilityEvents.Select(p => p.Name))}");
                    P2Inventory.MarkDirty();
                }
            }
            catch (Exception e) { ModLog.Error($"P2Abilities: P2 passive failed: {e}"); Passive = null; }
        }

        if (inv?.activeAbility != null)
        {
            try
            {
                // AbilityDash is the only active (EAbiltiyActive.Dash)
                Active = MakeWithEvents("active", () => ActiveAbilityFactory.CreateAbility(EAbiltiyActive.Dash), a => a.Init());
                if (Active != null) ModLog.Info($"P2Abilities: P2 active {SafeTypeName(Active)}, cooldown {SafeCooldown(Active):F1} s");
            }
            catch (Exception e) { ModLog.Error($"P2Abilities: P2 active failed: {e}"); Active = null; }
        }
        MakeEffects();
    }

    // Makes the ability; if the factory didn't initialise it (no event changed), runs init. P2's new listeners are
    // taken off every global event and kept: routed (health, movement, level-up) or put back (everything else).
    private static T MakeWithEvents<T>(string what, Func<T> make, Action<T> init) where T : Il2CppObjectBase
    {
        var before = Snapshot();
        T made = default;
        InP2(() => made = make());
        if (made == null) { ModLog.Warn($"P2Abilities: factory gave no {what}"); return null; }
        bool factoryInited = Changed(before).Count > 0;
        if (!factoryInited) InP2(() => init(made));
        ModLog.Info($"P2Abilities: {what} init {(factoryInited ? "by the factory" : "by the mod")}");
        Route(what, before);
        return made;
    }

    // Game code just ran for P2 (since `before`): its new listeners on player events are taken off and routed to P2's
    // happenings only; others stay
    private static void Route(string what, Dictionary<string, Il2CppSystem.Delegate> before)
    {
        var changed = Changed(before);
        ModLog.Info($"P2Abilities: {what} subscribed to: " + (changed.Count == 0 ? "none" : string.Join(", ", changed.Select(c => Key(c)))));
        foreach (var p in changed)
        {
            var now = p.GetValue(null) as Il2CppSystem.Delegate;
            var old = before[Key(p)];
            var added = NewListeners(now, old);
            if (added == null) continue;
            string key = Key(p);
            if (!IsPlayerEvent(p))
            {
                ModLog.Info($"P2Abilities: {key} stays subscribed (not a player's own event: enemies, stage...; {Describe(added)})");
                continue;
            }
            p.SetValue(null, old);   // P1's happenings no longer reach P2's passive
            routed[key] = routed.TryGetValue(key, out var r) && r != null ? Il2CppSystem.Delegate.Combine(r, added) : added;
            ModLog.Info($"P2Abilities: {key} -> P2's listener taken off, called for P2 only ({Describe(added)})");
        }
    }

    // ---------- P2's items (P2Items): their listeners on player events join P2's routed ones ----------
    // An item's Init (inside the game's AddItem on P2's inventory) subscribes to static events like any other game code:
    // snapshot before, route after - player events (health, movement, xp, aegis) then reach P2's items for P2's own
    // happenings only, and P1's damage/jumps no longer trigger P2's items.
    public static Dictionary<string, Il2CppSystem.Delegate> SnapshotEvents()
    {
        if (allEvents == null) FindEvents();
        return Snapshot();
    }

    public static void RouteEvents(string what, Dictionary<string, Il2CppSystem.Delegate> before)
    {
        if (before == null) return;
        Route(what, before);
    }

    // Listeners of P2 items that P2 no longer has (removed) are dropped from the routed ones; returns how many
    public static int DropRoutedTargets(Func<Il2CppSystem.Object, bool> stale)
    {
        int n = 0;
        foreach (var key in routed.Keys.ToList())
        {
            var d = routed[key];
            if (d == null) continue;
            Il2CppSystem.Delegate keep = null;
            foreach (var l in d.GetInvocationList())
            {
                if (l.Target != null && stale(l.Target)) { n++; continue; }
                keep = keep == null ? l : Il2CppSystem.Delegate.Combine(keep, l);
            }
            routed[key] = keep;
        }
        return n;
    }

    // ---------- P2's character effects (user: the shield tome has an effect on the character; Flex's ready effect) ----------
    // Shield: P1's PlayerShieldEffects (shieldBreakFx / shieldChargeFx + sounds) reacts to PlayerHealth's damage/heal
    // events. P2 gets a real PlayerShieldEffects of its own on P2's body, holding inert copies of P1's particles and
    // sounds; its event listeners are routed (above), so the game's own code plays them for P2's hits and recharges.
    // Flex: PlayerEffects.flexFx is shown on P1 while P1's Flex is ready (OnFlexReady); P2 gets an inert copy shown
    // while P2's Flex is ready (HasFlex in P2's context).
    private static GameObject shieldFxHolder, flexFxCopy;
    private static int flexFxLogs;

    private static void MakeEffects()
    {
        var body = P2Weapons.Body;
        var p1Model = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
        if (body == null || p1Model == null) return;
        try
        {
            var p1fx = UnityEngine.Object.FindObjectOfType<PlayerShieldEffects>();
            if (p1fx != null)
            {
                var holder = new GameObject("MegabonkSplitscreen_P2ShieldFx");
                holder.SetActive(false);
                holder.transform.SetParent(body.transform, false);
                holder.transform.localPosition = p1Model.InverseTransformPoint(p1fx.transform.position);
                holder.transform.localRotation = Quaternion.Inverse(p1Model.rotation) * p1fx.transform.rotation;
                var fx = holder.AddComponent<PlayerShieldEffects>();
                var brk = CopyInert(p1fx.shieldBreakFx?.gameObject, p1fx.transform, holder.transform);
                var chg = CopyInert(p1fx.shieldChargeFx?.gameObject, p1fx.transform, holder.transform);
                var brkS = CopyInert(p1fx.shieldBreakSfx?.gameObject, p1fx.transform, holder.transform);
                var chgS = CopyInert(p1fx.shieldChargeSfx?.gameObject, p1fx.transform, holder.transform);
                void Fill()
                {
                    fx.shieldBreakFx = brk?.GetComponent<ParticleSystem>();
                    fx.shieldChargeFx = chg?.GetComponent<ParticleSystem>();
                    fx.shieldBreakSfx = brkS?.GetComponent<AudioSource>();
                    fx.shieldChargeSfx = chgS?.GetComponent<AudioSource>();
                }
                Fill();
                var before = Snapshot();
                holder.SetActive(true);   // Awake: subscribes to the health events
                Route("P2's shield effects", before);
                Fill();   // in case Awake looked its parts up itself
                shieldFxHolder = holder;
                ModLog.Info($"P2Abilities: P2 shield effects on P2's body (P1's on '{p1fx.name}': break {p1fx.shieldBreakFx?.name ?? "none"}, charge {p1fx.shieldChargeFx?.name ?? "none"})");
            }
            else ModLog.Info("P2Abilities: P1 has no PlayerShieldEffects");

            var pe = UnityEngine.Object.FindObjectOfType<PlayerEffects>();
            if (pe?.flexFx != null && Passive?.TryCast<Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations.PassiveAbilityFlex>() != null)
            {
                var holder = new GameObject("MegabonkSplitscreen_P2FlexFx");
                holder.transform.SetParent(body.transform, false);
                flexFxCopy = CopyInert(pe.flexFx, p1Model, holder.transform);
                if (flexFxCopy != null) flexFxCopy.SetActive(false);
                ModLog.Info($"P2Abilities: P2 Flex effect copied from '{pe.flexFx.name}' (P1's active {pe.flexFx.activeSelf})");
            }
        }
        catch (Exception e) { ModLog.Error($"P2Abilities: P2 effects failed: {e}"); }
    }

    // An inert copy of `src` (game scripts removed) under `parent`, at the same pose relative to `frame` that src has
    // relative to P1's (parent stands for frame on P2)
    private static GameObject CopyInert(GameObject src, Transform frame, Transform parent)
    {
        if (src == null) return null;
        var hold = new GameObject("hold");
        hold.SetActive(false);
        var copy = UnityEngine.Object.Instantiate(src, hold.transform);
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetIl2CppType().Assembly.GetName().Name == "Assembly-CSharp") UnityEngine.Object.DestroyImmediate(mb);
        copy.transform.SetParent(parent, false);
        copy.transform.localPosition = frame.InverseTransformPoint(src.transform.position);
        copy.transform.localRotation = Quaternion.Inverse(frame.rotation) * src.transform.rotation;
        copy.name = "MegabonkSplitscreen_P2_" + src.name;
        UnityEngine.Object.Destroy(hold);
        copy.SetActive(src.activeSelf);
        return copy;
    }

    private static void UpdateFlexFx()
    {
        if (flexFxCopy == null) return;
        var flex = Passive?.TryCast<Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations.PassiveAbilityFlex>();
        if (flex == null) return;
        bool ready = false;
        InP2(() => ready = flex.HasFlex());
        if (flexFxCopy.activeSelf == ready) return;
        flexFxCopy.SetActive(ready);
        if (flexFxLogs++ < 6)
        {
            var pe = UnityEngine.Object.FindObjectOfType<PlayerEffects>();
            ModLog.Info($"P2Abilities: P2's Flex {(ready ? "ready - effect on" : "used - effect off")} at {Time.time:F1} (P1's effect active {pe?.flexFx?.activeSelf})");
        }
    }

    // Events the mod fires for P2 itself: P1's health (P2GameHealth), P1's movement (P2Movement), P1's level-ups
    private static bool IsPlayerEvent(PropertyInfo p)
    {
        var t = p.DeclaringType;
        return t == typeof(Assets.Scripts.Inventory__Items__Pickups.PlayerHealth) || t == typeof(PlayerMovement)
            || t == typeof(Inventory__Items__Pickups.Xp_and_Levels.PlayerXp)
            // v0.44.8 (character sweep: Athena x2 ended with P2's AttackSpeed 2.5 vs P1's 1 - Athena's LockIn listens
            // to A_Used/A_Regen, which both players' Aegis fire): a player's own Aegis, routed below
            || t == typeof(Assets.Scripts.Game.Combat.ConstantAttacks.AegisAttack);
    }

    // ---------- Aegis events: each player's Aegis reaches that player's passive only ----------
    // AegisAttack.A_Used / A_Regen (Action<int>: the current shield count, no owner) fire from whichever Aegis
    // changed. No hooks (v0.44.8's hooks on the Aegis methods crashed the game at spawn: RegenShield shares its
    // machine code with SpawnPlayerPortal.StartPortal - crash dump): P2's passive listeners are routed off (above),
    // P1's passive listeners are taken off too, and each frame every player's Aegis count is compared with the last
    // one; a change calls that player's passive listeners with the new count (what both events pass).
    private static readonly string[] AegisEventNames = { "A_Used", "A_Regen" };
    private static readonly Dictionary<string, Il2CppSystem.Delegate> p1Aegis = new();
    private static int lastP1Aegis = -1, lastP2Aegis = -1, aegisLogs;

    // v0.44.10 (character sweep: Roberto x2 - P2's Hoarder timer ran 24 s in 9 s, P1's 8.9 s; 2.7 = frame rate /
    // physics rate): the game ticks passives on the physics step, so P2's passive ticks from the mod's FixedUpdate
    public static void FixedTick()
    {
        if (Passive == null || Time.deltaTime <= 0f || Assets.Scripts.Utility.MyTime.paused || P2Health.IsDead) return;
        // v1.0.5 (players: Birdo / Roberto's float worked on P1, not on P2): the passive's tick finds the player's
        // movement through P1's MyPlayer.playerMovement - P2's movement copy stands in there too while it ticks
        var p1 = MyPlayer.Instance;
        var own = p1 != null ? p1.playerMovement : null;
        bool swap = FloatFix && HasFloat && p1 != null && P2Movement.Pm != null;   // (Float characters only: other passives as before)
        if (swap) { p1.playerMovement = P2Movement.Pm; P2Movement.EnterInput(); }
        try { Guard("passive tick", () => InP2(() => Passive.Tick())); }
        finally { if (swap) { P2Movement.ExitInput(); p1.playerMovement = own; } }
    }
    private static float nextAegisScan;

    private static void UpdateAegis()
    {
        var t = typeof(Assets.Scripts.Game.Combat.ConstantAttacks.AegisAttack);
        // P1's passive listeners off the shared events (they come back on a new run or a passive re-init)
        if (Time.unscaledTime >= nextAegisScan)
        {
            nextAegisScan = Time.unscaledTime + 1f;
            foreach (var n in AegisEventNames)
            {
                var p = t.GetProperty(n);
                var d = p?.GetValue(null) as Il2CppSystem.Delegate;
                if (d == null) continue;
                Il2CppSystem.Delegate keep = null, taken = null;
                foreach (var l in d.GetInvocationList())
                {
                    string owner = "?";
                    try { owner = l.Method?.DeclaringType?.Name ?? "?"; } catch { }
                    if (owner.StartsWith("PassiveAbility")) taken = taken == null ? l : Il2CppSystem.Delegate.Combine(taken, l);
                    else keep = keep == null ? l : Il2CppSystem.Delegate.Combine(keep, l);
                }
                if (taken == null) continue;
                p.SetValue(null, keep?.Cast<Il2CppSystem.Action<int>>());
                p1Aegis[n] = p1Aegis.TryGetValue(n, out var had) && had != null ? Il2CppSystem.Delegate.Combine(had, taken) : taken;
                if (aegisLogs++ < 4) ModLog.Info($"P2Abilities: AegisAttack.{n}: P1's passive listener taken off, called for P1's own Aegis only ({Describe(taken)})");
            }
        }
        if (p1Aegis.Count == 0 && !routed.Keys.Any(k => k.StartsWith("AegisAttack."))) return;
        Assets.Scripts.Game.Combat.ConstantAttacks.AegisAttack a1 = null, a2 = null;
        var body = P2Weapons.Body;
        foreach (var a in UnityEngine.Object.FindObjectsOfType<Assets.Scripts.Game.Combat.ConstantAttacks.AegisAttack>())
        {
            if (a == null) continue;
            if (body != null && a.transform.IsChildOf(body.transform)) a2 = a; else a1 = a;
        }
        int n1 = a1 != null ? a1.currentAmount : -1, n2 = a2 != null ? a2.currentAmount : -1;
        if (n1 != lastP1Aegis)
        {
            if (lastP1Aegis >= 0 && n1 >= 0) CallAegis(p1Aegis, n1, null);
            lastP1Aegis = n1;
        }
        if (n2 != lastP2Aegis)
        {
            if (lastP2Aegis >= 0 && n2 >= 0)
            {
                var forP2 = new Dictionary<string, Il2CppSystem.Delegate>();
                foreach (var n in AegisEventNames) if (routed.TryGetValue("AegisAttack." + n, out var r) && r != null) forP2[n] = r;
                CallAegis(forP2, n2, InP2);
            }
            lastP2Aegis = n2;
        }
    }

    // Each passive listens to both events with the same handler (OnAegisChange): one call per distinct handler
    private static void CallAegis(Dictionary<string, Il2CppSystem.Delegate> listeners, int amount, Action<Action> wrap)
    {
        var seen = new HashSet<string>();
        foreach (var d in listeners.Values)
            foreach (var l in d.GetInvocationList())
            {
                string id = "?";
                try { id = $"{l.Target?.Pointer}:{l.Method?.Name}"; } catch { }
                if (!seen.Add(id)) continue;
                void Call() => l.Cast<Il2CppSystem.Action<int>>().Invoke(amount);
                try { if (wrap != null) wrap(Call); else Call(); }
                catch (Exception e) { if (loggedErrors.Add("aegis" + e.Message)) ModLog.Warn($"P2Abilities: Aegis listener failed: {e.Message}"); }
            }
        if (wrap != null) P2Inventory.MarkDirty();
    }

    public static void Reset()
    {
        // Event listeners of the old passive that stayed on enemy events: the passive's Cleanup takes them off
        if (Passive != null) Guard("passive cleanup", () => InP2(() => Passive.Cleanup()));
        if (Active != null) Guard("active cleanup", () => InP2(() => Active.Cleanup()));
        if (shieldFxHolder != null) UnityEngine.Object.Destroy(shieldFxHolder);   // its OnDestroy unsubscribes
        if (flexFxCopy != null) UnityEngine.Object.Destroy(flexFxCopy.transform.parent != null ? flexFxCopy.transform.parent.gameObject : flexFxCopy);
        shieldFxHolder = null;
        flexFxCopy = null;
        Passive = null;
        Active = null;
        tried = false;
        routed.Clear();
        p1Aegis.Clear();
        lastP1Aegis = lastP2Aegis = -1;
        p2Statics.Clear();
        passiveStatics = Array.Empty<PropertyInfo>();
        abilityEvents = Array.Empty<PropertyInfo>();
        activeButton = null;
        passiveName = "";
        usesGold = false;
        otherPtr = IntPtr.Zero;
        contextDepth = 0;
    }

    // ---------- P2 context ----------

    // P2's listener for a global event, for code that runs P2's happenings with the events swapped (null if none)
    // (as the event's own delegate class, so the property accepts it)
    public static object RoutedFor(PropertyInfo p)
    {
        if (routed.Count == 0 || !routed.TryGetValue(Key(p), out var d) || d == null) return null;
        try { return typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.Cast))!.MakeGenericMethod(p.PropertyType).Invoke(d, null); }
        catch (Exception e)
        {
            if (loggedErrors.Add("cast " + Key(p))) ModLog.Error($"P2Abilities: P2's listener for {Key(p)} can't be set: {e.InnerException?.Message ?? e.Message}");
            return null;
        }
    }

    // Static numbers and fired events swapped for P2's call; nests; true = entered (call Exit)
    public static bool Enter()
    {
        if (Passive == null) return false;
        if (contextDepth++ > 0) return true;
        p1Statics.Clear();
        foreach (var p in passiveStatics)
        {
            try { p1Statics[p] = p.GetValue(null); if (p2Statics.TryGetValue(p, out var v)) p.SetValue(null, v); }
            catch { }
        }
        mutedAbilityEvents.Clear();
        foreach (var p in abilityEvents)
        {
            object v = null;
            try { v = p.GetValue(null); p.SetValue(null, p.Name == "A_StatModified" ? StatModifiedForP2() : null); } catch { }
            mutedAbilityEvents.Add(v);
        }
        return true;
    }

    public static void Exit()
    {
        if (contextDepth == 0 || --contextDepth > 0) return;
        foreach (var p in passiveStatics)
        {
            try
            {
                object now = p.GetValue(null);
                // TEMP: P2's passive changed its timer (e.g. Flex used: flexReadyAtTime moves on)
                if (p2Statics.TryGetValue(p, out var was) && !Equals(was, now) && staticLogs++ < 10)
                    ModLog.Info($"P2Abilities: P2's {passiveName}.{p.Name} {was} -> {now} at {Time.time:F1}");
                p2Statics[p] = now;
                if (p1Statics.TryGetValue(p, out var v)) p.SetValue(null, v);
            }
            catch { }
        }
        for (int i = 0; i < abilityEvents.Length && i < mutedAbilityEvents.Count; i++)
        {
            try { abilityEvents[i].SetValue(null, mutedAbilityEvents[i]); } catch { }
        }
    }

    // A P2 passive stat change -> P2's stats are recomputed (P1's listener would recompute P1's)
    private static Il2CppSystem.Action<Assets.Scripts.Menu.Shop.EStat> statModified;
    private static Il2CppSystem.Action<Assets.Scripts.Menu.Shop.EStat> StatModifiedForP2() =>
        statModified ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Assets.Scripts.Menu.Shop.EStat>>(
            new Action<Assets.Scripts.Menu.Shop.EStat>(_ => P2Inventory.MarkDirty()));

    // Runs `a` as P2: P2's stats (and passive), P2's movement copy as "the" player, P2's gold, P2's static numbers
    private static void InP2(Action a)
    {
        var p1Pm = PlayerMovement.Instance;
        bool stats = P2Inventory.EnterP2();
        bool abil = Enter();
        try
        {
            if (P2Movement.Pm != null) PlayerMovement.Instance = P2Movement.Pm;
            if (usesGold) P2Gold.Run(a);   // Stonks: damage from P2's gold
            else a();
        }
        finally
        {
            if (p1Pm != null) PlayerMovement.Instance = p1Pm;
            if (abil) Exit();
            if (stats) P2Inventory.Exit();
        }
    }

    // ---------- Level-ups ----------

    // P2 reached `level`: P2's passive grows (the game's level-up event, routed to P2's listener only)
    public static void OnLevelUp(int level)
    {
        if (Passive == null && Active == null) return;
        int calls = 0;
        foreach (var kv in routed)
        {
            if (!kv.Key.EndsWith(".A_LevelUp")) continue;
            var act = kv.Value?.Cast<Il2CppSystem.Action<int>>();
            if (act == null) continue;
            Guard("level-up " + kv.Key, () => InP2(() => act.Invoke(level)));
            calls++;
        }
        if (calls == 0) Guard("level-up direct", () => InP2(() => CallLevelUp(level)));
        P2Inventory.MarkDirty();
        if (levelLogs++ < 6) ModLog.Info($"P2Abilities: P2 level {level} -> passive {(calls > 0 ? "level-up event" : "OnLevelup called directly")}; {DescribeModifiers()}");
    }

    // Fallback when the passive didn't subscribe to a level-up event: its own OnLevelup(int), if it has one
    private static void CallLevelUp(int level)
    {
        var t = ManagedType(Passive);
        var m = t?.GetMethod("OnLevelup", new[] { typeof(int) });
        if (m == null) return;
        var cast = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.Cast))!.MakeGenericMethod(t).Invoke(Passive, null);
        m.Invoke(cast, new object[] { level });
    }

    private static string DescribeModifiers()
    {
        try
        {
            var mods = Passive?.statModifiers;
            if (mods == null || mods.Count == 0) return "no stat modifiers";
            var parts = new List<string>();
            foreach (var kv in mods) parts.Add($"{kv.Key} -> P2 {P2Inventory.Get(kv.Key):F2}");
            return "stats " + string.Join(", ", parts);
        }
        catch (Exception e) { return "stats ? " + e.Message; }
    }

    // ---------- Active ability (dash) on P2's pad ----------

    private static string[] activeButton;
    private static bool activeWasDown;
    private static int useLogs;

    private static void CheckActiveButton()
    {
        var joy = PlayerJoin.P2Joystick;
        if (joy == null || P2LevelUp.P2ScreenOpen) return;
        activeButton ??= FindActiveButton();
        if (activeButton.Length == 0) return;
        bool down = P2Control.PadHeld(joy, activeButton);
        if (down && !activeWasDown)
        {
            bool ready = false;
            InP2(() => { ready = Active.IsReady(); if (ready) Active.Use(); });
            if (useLogs++ < 5) ModLog.Info($"P2Abilities: P2 pressed the ability button - {(ready ? "used" : "not ready")}");
        }
        activeWasDown = down;
    }

    // The pad button P1's ability action uses (action names logged by LogActions)
    private static string[] FindActiveButton()
    {
        try
        {
            var maps = ReInput.players.GetPlayer(0).controllers.maps;
            foreach (var a in AllActions())
            {
                string n = a.name ?? "";
                string l = n.ToLowerInvariant();
                if (!(l.Contains("ability") || l.Contains("dash") || l.Contains("special") || l.Contains("active"))) continue;
                var aem = maps.GetFirstButtonMapWithAction(ControllerType.Joystick, n, true);
                if (aem == null || string.IsNullOrEmpty(aem.elementIdentifierName)) continue;
                ModLog.Info($"P2 ability button = '{aem.elementIdentifierName}' (P1's '{n}' binding)");
                return new[] { aem.elementIdentifierName.Trim().ToLowerInvariant() };
            }
        }
        catch (Exception e) { ModLog.Warn($"P2 ability button lookup failed: {e.Message}"); }
        ModLog.Warn("P2 ability button: no ability action bound on P1's pad - P2 can't use the active ability");
        return Array.Empty<string>();
    }

    // TEMP: every Rewired action and P1's pad button for it (names, not guesses)
    private static void LogActions()
    {
        try
        {
            var maps = ReInput.players.GetPlayer(0).controllers.maps;
            var parts = new List<string>();
            foreach (var a in AllActions())
            {
                string b = "-";
                try { b = maps.GetFirstButtonMapWithAction(ControllerType.Joystick, a.name, true)?.elementIdentifierName ?? "-"; } catch { }
                parts.Add($"{a.name}={b}");
            }
            ModLog.Info($"[actions] Rewired actions (P1's pad binding): {string.Join(", ", parts)}");
        }
        catch (Exception e) { ModLog.Warn($"[actions] listing failed: {e.Message}"); }
    }

    // ---------- Helpers ----------

    // Rewired's actions by id (its action list is an IL2CPP interface the mod can't iterate directly)
    private static List<InputAction> AllActions()
    {
        var list = new List<InputAction>();
        for (int id = 0, misses = 0; id < 500 && misses < 50; id++)
        {
            InputAction a = null;
            try { a = ReInput.mapping.GetAction(id); } catch { }
            if (a == null) { misses++; continue; }
            misses = 0;
            list.Add(a);
        }
        return list;
    }

    private static void FindEvents()
    {
        allEvents = new List<PropertyInfo>();
        Type[] types;
        try { types = typeof(PlayerInventory).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        foreach (var t in types)
        {
            // v0.37.0 log: compiler lambda caches ("__c.__9__8_0") fill in lazily, looked like "init by the factory",
            // so P2's Flex was never initialised (no hit listener). Not events: skipped.
            if (t.Name.StartsWith("<") || t.Name.StartsWith("__c") || t.Name.Contains("DisplayClass")) continue;
            PropertyInfo[] props;
            try { props = t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var p in props)
                if (typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType) && p.CanRead && p.CanWrite && !t.IsGenericTypeDefinition && !p.Name.StartsWith("__9"))
                    allEvents.Add(p);
        }
    }

    private static string Key(PropertyInfo p) => $"{p.DeclaringType?.Name}.{p.Name}";

    private static Dictionary<string, Il2CppSystem.Delegate> Snapshot()
    {
        var d = new Dictionary<string, Il2CppSystem.Delegate>();
        foreach (var p in allEvents)
        {
            try { d[Key(p)] = p.GetValue(null) as Il2CppSystem.Delegate; } catch { }
        }
        return d;
    }

    private static List<PropertyInfo> Changed(Dictionary<string, Il2CppSystem.Delegate> before)
    {
        var list = new List<PropertyInfo>();
        foreach (var p in allEvents)
        {
            if (!before.TryGetValue(Key(p), out var old)) continue;
            Il2CppSystem.Delegate now;
            try { now = p.GetValue(null) as Il2CppSystem.Delegate; } catch { continue; }
            IntPtr a = now?.Pointer ?? IntPtr.Zero, b = old?.Pointer ?? IntPtr.Zero;
            if (a != b) list.Add(p);
        }
        return list;
    }

    // The listeners in `now` that weren't in `old`, as one delegate
    private static Il2CppSystem.Delegate NewListeners(Il2CppSystem.Delegate now, Il2CppSystem.Delegate old)
    {
        if (now == null) return null;
        var had = new HashSet<IntPtr>();
        if (old != null) foreach (var l in old.GetInvocationList()) had.Add(l.Pointer);
        Il2CppSystem.Delegate result = null;
        foreach (var l in now.GetInvocationList())
            if (!had.Contains(l.Pointer)) result = result == null ? l : Il2CppSystem.Delegate.Combine(result, l);
        return result;
    }

    private static string Describe(Il2CppSystem.Delegate d)
    {
        try
        {
            return string.Join(" + ", d.GetInvocationList().Select(l =>
            {
                string m = "?";
                try { m = l.Method != null ? $"{l.Method.DeclaringType?.Name}.{l.Method.Name}" : "?"; } catch { }
                return m;
            }));
        }
        catch { return "?"; }
    }

    // The passive's own static number fields (float/int/bool) - one value for the class, so swapped per player
    private static PropertyInfo[] StaticNumbers(Il2CppObjectBase o)
    {
        var t = ManagedType(o);
        if (t == null) return Array.Empty<PropertyInfo>();
        return t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite && (p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)))
            .ToArray();
    }

    // The interop (managed) class for an IL2CPP object's real class
    private static Type ManagedType(Il2CppObjectBase o)
    {
        string name;
        try { name = SafeTypeName(o); } catch { return null; }
        return typeof(PassiveAbility).Assembly.GetTypes().FirstOrDefault(t => t.Name == name && (t.IsSubclassOf(typeof(PassiveAbility)) || t.IsSubclassOf(typeof(ActiveAbility))));
    }

    private static string SafeTypeName(Il2CppObjectBase o)
    {
        try { return o.Cast<Il2CppSystem.Object>().GetIl2CppType().Name; } catch { return "?"; }
    }

    private static float SafeCooldown(ActiveAbility a)
    {
        float c = -1f;
        try { InP2(() => c = a.GetCooldown()); } catch { }
        return c;
    }

    private static void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            if (loggedErrors.Add(what + e.Message)) ModLog.Error($"P2Abilities {what} failed: {e}");
        }
    }
}
