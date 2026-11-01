using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using UnityEngine;

namespace MegabonkSplitscreen;

// Player 2's weapons, kept apart from P1's: they live in P2's own game inventory (P2Inventory), which starts with
// P2's character's starting weapon and gets P2's level-up picks.
//  - Each weapon is the game's own WeaponBase object; the mod fires it whenever its cooldown has passed, in P2's
//    inventory context (P2's stats).
//  - The game builds every attack for MyPlayer (P1). Attacks set up while the mod is firing a P2 weapon
//    (WeaponAttack.SetAttack) are marked as P2's, and for those the projectile spawn point
//    (WeaponAttack.GetProjectilePosition) is moved from P1 to P2. Attack objects are pooled and reused,
//    so the mark is set or cleared on every SetAttack.
// Kills go to the shared run.
internal static class P2Weapons
{
    private const float MinCooldown = 0.1f;

    private class Slot
    {
        public WeaponBase weapon;
        public float lastFired = -999f;
        public int fired;
        public bool? isConstant;        // always-on weapon (Aegis, Aura, ...): not fired, attached to P2's body
        public GameObject constant;     // its attack object under P2's body
    }

    // Always-on weapons: the attack prefab (WeaponData.attack) carries a ConstantAttack. For P1 the game's AuraAttacks
    // creates it under the player and links it to the weapon; P2 has no AuraAttacks, so the mod does the same under
    // P2's body (in P2's stat context, so its values are P2's). v0.19.0: P2's Aegis "fired" with 0 attacks and no shields.
    private static bool IsConstant(Slot s)
    {
        if (s.isConstant == null)
        {
            var prefab = s.weapon.weaponData?.attack;
            s.isConstant = prefab != null && prefab.GetComponent<Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack>() != null;
        }
        return s.isConstant.Value;
    }

    // Where P1's always-on attack sits relative to P1's model (feet): P1's own one of that weapon if P1 has it, else the
    // AuraAttacks object that P1's are created under
    private static Vector3 P1ConstantOffset(EWeapon w)
    {
        var p1 = MyPlayer.Instance;
        var model = p1?.playerRenderer?.rendererObject?.transform;
        var aa = p1?.playerConstantAttacks;
        if (p1 == null || model == null || aa == null) return Vector3.zero;
        // Without P1's own one: at the feet like P1's (user: P2's Frostwalker sat off to the side; the AuraAttacks
        // object it fell back to is 6 to the side and 2.2 up, P1's always-on attacks are placed at the feet)
        Vector3 at = model.position + Vector3.up * 0.01f;
        string from = "at the feet";
        if (aa.auras != null && aa.auras.ContainsKey(w) && aa.auras[w] != null)
        {
            at = aa.auras[w].transform.position;
            from = $"P1's own {w}";
        }
        var offset = at - model.position;
        ModLog.Info($"P2 always-on {w}: offset from the feet {offset} ({from})");
        return offset;
    }

    private static void AttachConstant(Slot s)
    {
        if (body == null || s.constant != null) return;
        var prefab = s.weapon.weaponData.attack;
        bool entered = P2Inventory.EnterP2();
        try
        {
            var go = UnityEngine.Object.Instantiate(prefab, body.transform);
            go.name = $"MegabonkSplitscreen_P2_{s.weapon.weaponData.eWeapon}";
            // Same height above the feet as P1's always-on attacks (user: P2's aura flickered like z-fighting with the
            // ground; at the body's origin it sat exactly on the floor)
            go.transform.localPosition = body.transform.InverseTransformVector(P1ConstantOffset(s.weapon.weaponData.eWeapon));
            go.transform.localRotation = Quaternion.identity;
            constantLocal[go.GetComponent<Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack>()?.Pointer ?? IntPtr.Zero] = go.transform.localPosition;
            var ca = go.GetComponent<Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack>();
            ca?.Set(s.weapon);
            s.constant = go;
            ModLog.Info($"P2 always-on weapon {s.weapon.weaponData.eWeapon} attached to P2 ({ca?.GetIl2CppType().Name ?? "no ConstantAttack"})");
        }
        catch (Exception e) { LogOnce("attach " + s.weapon.weaponData.eWeapon, e); }
        finally { if (entered) P2Inventory.Exit(); }
    }

    private static readonly List<Slot> slots = new();
    private static GameObject body;
    public static GameObject Body => body;
    private static bool firingForP2;
    private static int setAttacksDuringFire;
    private static readonly HashSet<IntPtr> p2Attacks = new();
    private static int shiftLogs;
    private static readonly HashSet<string> loggedErrors = new();

    public static IReadOnlyList<WeaponBase> Bases => slots.Select(s => s.weapon).ToList();
    public static IReadOnlyList<(WeaponData data, int level)> Levels => slots.Select(s => (s.weapon.weaponData, s.weapon.level)).ToList();

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(WeaponAttack), nameof(WeaponAttack.SetAttack)),
            postfix: new HarmonyMethod(typeof(P2Weapons), nameof(OnSetAttack)));
        harmony.Patch(AccessTools.Method(typeof(WeaponAttack), nameof(WeaponAttack.GetProjectilePosition)),
            postfix: new HarmonyMethod(typeof(P2Weapons), nameof(OnGetProjectilePosition)));
        // GetProjectilePosition never ran for P2's attacks in v0.11.0 (likely inlined by IL2CPP), so also move
        // each P2 projectile right after it is set up; whichever hook runs first moves it, once.
        harmony.Patch(AccessTools.Method(typeof(ProjectileBase), nameof(ProjectileBase.Set)),
            prefix: new HarmonyMethod(typeof(P2Weapons), nameof(BeforeProjectileSet)),
            postfix: new HarmonyMethod(typeof(P2Weapons), nameof(OnProjectileSet)));
        harmony.Patch(AccessTools.Method(typeof(WeaponAttack), nameof(WeaponAttack.SuccessfullySpawnedProjectile)),
            postfix: new HarmonyMethod(typeof(P2Weapons), nameof(OnProjectileSpawned)));
        ModLog.Info("P2Weapons: attack hooks applied");
    }

    // P2's always-on attacks (Aura, Aegis, ...) run their own per-frame code and stat handlers outside P2's stat
    // context, so e.g. P2's aura took P1's size every frame and P2's on the next (user: P2's aura flickered).
    // Each such method runs in P2's context when the attack belongs to a P2 weapon.
    // Applied LAST and only on native code nobody else shares (v0.25.0 crashed at launch, dump: IL2CPP merges methods
    // with identical machine code, e.g. all empty ones; some of these shared the native function of the already
    // hooked, empty PlayerMovement.Update, and two Harmony hooks on one native function crash the runtime).
    public static void ApplyConstantHooks(Harmony harmony)
    {
        var taken = new HashSet<IntPtr>();
        foreach (var pm in Harmony.GetAllPatchedMethods())
        {
            var p = NativePointer(pm);
            if (p != IntPtr.Zero) taken.Add(p);
        }
        var caType = typeof(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack);
        var names = new HashSet<string> { "Update", "FixedUpdate", "LateUpdate", "OnStatUpdate", "OnWeaponStatUpdate", "UpdateSize", "UpdateCooldown" };
        var candidates = new List<(System.Reflection.MethodInfo m, IntPtr p)>();
        foreach (var t in caType.Assembly.GetTypes().Where(t => t == caType || t.IsSubclassOf(caType)))
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                           System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                         .Where(m => names.Contains(m.Name) && !m.IsAbstract && !m.ContainsGenericParameters))
                candidates.Add((m, NativePointer(m)));
        // Shared among the candidates themselves = merged code (likely empty): skip those too
        var counts = candidates.GroupBy(c => c.p).ToDictionary(g => g.Key, g => g.Count());
        int hooked = 0;
        var skipped = new List<string>();
        foreach (var (m, p) in candidates)
        {
            string name = $"{m.DeclaringType?.Name}.{m.Name}";
            if (p == IntPtr.Zero || taken.Contains(p) || counts[p] > 1)
            {
                skipped.Add(name);
                continue;
            }
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Weapons), nameof(ConstantPrefix)),
                    finalizer: new HarmonyMethod(typeof(P2Weapons), nameof(ConstantFinalizer)));
                taken.Add(p);
                hooked++;
            }
            catch (Exception e) { ModLog.Warn($"P2Weapons: couldn't hook {name}: {e.Message}"); }
        }
        ModLog.Info($"P2Weapons: {hooked} always-on attack methods run in P2's context for P2's weapons; " +
                    $"skipped {skipped.Count} with shared native code: {string.Join(", ", skipped)}");
    }

    // True if no other method in Assembly-CSharp shares m's native function (built once: pointer -> count)
    private static Dictionary<IntPtr, int> nativeCounts;
    public static bool IsNativeCodeUnique(System.Reflection.MethodBase m)
    {
        var p = NativePointer(m);
        if (p == IntPtr.Zero) return false;
        if (nativeCounts == null)
        {
            nativeCounts = new Dictionary<IntPtr, int>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            Type[] types;
            try { types = m.DeclaringType.Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                System.Reflection.MethodInfo[] ms;
                try { ms = t.GetMethods(flags); }
                catch { continue; }
                foreach (var mi in ms)
                {
                    if (mi.IsAbstract || mi.ContainsGenericParameters) continue;
                    var q = NativePointer(mi);
                    if (q == IntPtr.Zero) continue;
                    nativeCounts.TryGetValue(q, out int c);
                    nativeCounts[q] = c + 1;
                    if (!nativeUsers.TryGetValue(q, out var users)) nativeUsers[q] = users = new List<Type>();
                    users.Add(t);
                }
            }
            ModLog.Info($"Native code map: {nativeCounts.Count} functions");
        }
        return nativeCounts.TryGetValue(p, out int n) && n == 1;
    }

    // Shared native code is safe to hook ONCE (the crash was two hooks on one function) if every method sharing it
    // belongs to `family` (the patch then only ever sees those types, and checks the instance itself)
    private static readonly Dictionary<IntPtr, List<Type>> nativeUsers = new();
    public static bool SharedOnlyWithin(System.Reflection.MethodBase m, Type family)
    {
        if (!IsNativeCodeUnique(m) && nativeCounts == null) return false;
        var p = NativePointer(m);
        return p != IntPtr.Zero && nativeUsers.TryGetValue(p, out var users) && users.All(u => family.IsAssignableFrom(u));
    }

    // True if m's native function is already used by a hooked method (IL2CPP merges identical machine code)
    public static bool IsNativeCodeHooked(System.Reflection.MethodBase m)
    {
        var p = NativePointer(m);
        if (p == IntPtr.Zero) return false;
        foreach (var pm in Harmony.GetAllPatchedMethods())
            if (NativePointer(pm) == p) return true;
        return false;
    }

    // ---------- Melee attacks follow P2 (user: P2's sword slashed at P1) ----------
    // Melee-style projectiles (slashes, spins) place themselves in front of THE player (P1) every frame, so moving them
    // once when they're set up isn't enough. After the game's own per-frame placement, a P2 slash is mapped from P1's
    // frame to P2's: same offset and turn relative to P2's position and facing. Only while it's near P1 (a flying shot
    // is never pulled back), and only on native code no other method shares.
    private static readonly HashSet<IntPtr> p2Projectiles = new();
    // Projectiles with player-relative offsets (forwardOffset / upOffset / colliderOffset in the types dump)
    private static readonly string[] MeleeTypes = { "ProjectileMelee", "ProjectileHeroSword", "ProjectileCringeSword",
        "ProjectileKatana", "ProjectileScythe", "ProjectileDexecutioner", "ProjectileWhirlwind", "ProjectileShotgun" };
    private const float MeleeRange = 20f;

    public static void ApplyMeleeHooks(Harmony harmony)
    {
        var hooked = new List<string>();
        var skipped = new List<string>();
        var asm = typeof(ProjectileBase).Assembly;
        // After the game's own placement (postfix), and right before its hit checks (prefix): v0.27.9's slash showed at
        // P2 but did no damage (user) - ProjectileMelee's MyFixedUpdate/StepMovement share native code (not hookable),
        // so the hit check ran with the slash put back at P1
        foreach (var t in asm.GetTypes().Where(t => MeleeTypes.Contains(t.Name) && typeof(ProjectileBase).IsAssignableFrom(t)))
            foreach (var (name, before) in new[] { ("MyUpdate", false), ("MyFixedUpdate", false), ("StepMovement", false),
                                                   ("TryInit", true),
                                                   ("CheckZone", true), ("CheckColliderCustom", true), ("CheckSpawnCollision", true), ("CheckCollision", true) })
            {
                var m = AccessTools.DeclaredMethod(t, name);
                if (m == null) continue;
                string label = $"{t.Name}.{name}";
                // v0.30.6: the Katana's methods all share native code with other projectiles (all skipped, so its
                // target search ran at P1 and failed: set up 1 / spawned 0). Shared only among projectile types and
                // not hooked yet = one hook is safe (FollowP2 only acts on P2's projectiles).
                bool ok = !IsNativeCodeHooked(m) && (IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(ProjectileBase)));
                if (!ok) { skipped.Add(label); continue; }
                try
                {
                    var hm = new HarmonyMethod(typeof(P2Weapons), nameof(FollowP2));
                    if (before) harmony.Patch(m, prefix: hm);
                    else harmony.Patch(m, postfix: hm);
                    if (name == "TryInit") harmony.Patch(m, postfix: new HarmonyMethod(typeof(P2Weapons), nameof(AfterInit)));
                    hooked.Add(label);
                }
                catch (Exception e) { skipped.Add($"{label} ({e.Message})"); }
            }
        ModLog.Info($"P2Weapons: melee follow-P2 on {hooked.Count} methods ({string.Join(", ", hooked)}); skipped {(skipped.Count > 0 ? string.Join(", ", skipped) : "none")}");
    }

    private static int followLogs;
    // TEMP: did a P2 melee projectile's target search succeed (first few)
    private static int initLogs;
    private static void AfterInit(ProjectileBase __instance, bool __result)
    {
        if (__instance == null || !p2Projectiles.Contains(__instance.Pointer) || initLogs++ >= 8) return;
        ModLog.Info($"[melee] P2 {__instance.GetIl2CppType().Name}.TryInit -> {__result} at {__instance.transform.position} (P2 {body?.transform.position})");
    }

    private static readonly HashSet<string> followSeen = new();
    private static void FollowP2(ProjectileBase __instance, System.Reflection.MethodBase __originalMethod)
    {
        if (__instance == null || body == null || !p2Projectiles.Contains(__instance.Pointer)) return;
        string via = __originalMethod?.Name ?? "?";
        if (followSeen.Add($"{__instance.GetIl2CppType().Name}.{via}"))
            ModLog.Info($"[melee] P2 {__instance.GetIl2CppType().Name}.{via} runs (slash at {__instance.transform.position}, P2 {body.transform.position})");
        var pr = MyPlayer.Instance != null ? MyPlayer.Instance.playerRenderer : null;
        var p1Model = pr != null && pr.rendererObject != null ? pr.rendererObject.transform : null;
        if (p1Model == null) return;
        Vector3 p1 = p1Model.position, p2 = body.transform.position;
        var turn = Quaternion.Euler(0f, Mathf.DeltaAngle(p1Model.eulerAngles.y, body.transform.eulerAngles.y), 0f);
        bool moved = Remap(__instance.transform, p1, p2, turn);
        var hero = __instance.TryCast<ProjectileHeroSword>();
        if (hero?.movingProjectile != null && !hero.movingProjectile.transform.IsChildOf(__instance.transform))
            Remap(hero.movingProjectile.transform, p1, p2, turn);
        var cringe = __instance.TryCast<ProjectileCringeSword>();
        if (cringe?.movingProjectile != null && !cringe.movingProjectile.transform.IsChildOf(__instance.transform))
            Remap(cringe.movingProjectile.transform, p1, p2, turn);
        if (moved && followLogs++ < 5)
            ModLog.Info($"[melee] P2 {__instance.GetIl2CppType().Name} placed at P2: {__instance.transform.position} (P2 {p2})");
    }

    private static bool Remap(Transform t, Vector3 p1, Vector3 p2, Quaternion turn)
    {
        Vector3 pos = t.position;
        if ((pos - p1).sqrMagnitude >= (pos - p2).sqrMagnitude || (pos - p1).sqrMagnitude > MeleeRange * MeleeRange) return false;
        t.SetPositionAndRotation(p2 + turn * (pos - p1), turn * t.rotation);
        return true;
    }

    // The IL2CPP method's native function (first field of its MethodInfo)
    private static IntPtr NativePointer(System.Reflection.MethodBase m)
    {
        try
        {
            var f = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
            if (f == null) return IntPtr.Zero;
            var mi = (IntPtr)f.GetValue(null);
            return mi == IntPtr.Zero ? IntPtr.Zero : System.Runtime.InteropServices.Marshal.ReadIntPtr(mi);
        }
        catch { return IntPtr.Zero; }
    }

    private static void ConstantPrefix(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack __instance, out bool __state)
    {
        __state = false;
        // Not while P2's stats are being worked out (that fires the stat event these handlers listen to)
        if (P2Inventory.Recomputing) return;
        try { __state = __instance != null && IsP2(__instance.weaponBase) && P2Inventory.EnterP2(); }
        catch { }
    }

    private static Exception ConstantFinalizer(Exception __exception, bool __state)
    {
        if (__state) P2Inventory.Exit();
        return __exception;
    }

    private static readonly HashSet<string> hooksSeen = new();

    private static void HookSeen(string name)
    {
        if (hooksSeen.Add(name)) ModLog.Info($"P2Weapons: hook {name} runs");
    }

    // Known as P2's before Set runs (its TryInit may run inside it, and the move before TryInit needs to know)
    private static void BeforeProjectileSet(ProjectileBase __instance, WeaponAttack weaponAttack)
    {
        if (__instance == null) return;
        if (weaponAttack != null && p2Attacks.Contains(weaponAttack.Pointer)) { p2Projectiles.Add(__instance.Pointer); p2Scope++; }
        else p2Projectiles.Remove(__instance.Pointer);
    }

    // ---------- Aimed weapons (v0.43.0) ----------
    // Weapon sweep (autotest): P2's Shotgun, Sniper and Dragon's Breath dealt no damage. The game asks each projectile
    // where it shoots from and where it aims (ProjectileShotgun/ProjectileSniper.GetShootingPosition / GetAttackDir) -
    // around "the player" (P1). For P2's projectiles the answers are turned into P2's: the shooting point moved by the
    // P1 -> P2 offset (P2's facing), the aim towards P2's nearest enemy. Dragon's Breath (always-on, on P2's body) picks
    // its target in FindClosestTarget - around P1; for P2's it is set to P2's nearest enemy afterwards.
    private static int aimHookLogs;
    private static readonly HashSet<string> aimSeen = new();

    public static void ApplyAimHooks(Harmony harmony)
    {
        var done = new List<string>();
        void Hook(Type t, string name, string post, int args = -1)
        {
            var m = t.GetMethods().FirstOrDefault(x => x.Name == name && x.DeclaringType == t && (args < 0 || x.GetParameters().Length == args));
            if (m == null) { done.Add($"{t.Name}.{name} missing"); return; }
            bool ok = !IsNativeCodeHooked(m) && (IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(ProjectileBase)) || SharedOnlyWithin(m, typeof(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack)));
            if (!ok) { done.Add($"{t.Name}.{name} SKIPPED (shared native code)"); return; }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(P2Weapons), post));
            done.Add($"{t.Name}.{name}");
        }
        Hook(typeof(ProjectileShotgun), "GetShootingPosition", nameof(AfterShootingPosition));
        Hook(typeof(ProjectileShotgun), "GetAttackDir", nameof(AfterAttackDir));
        Hook(typeof(ProjectileSniper), "GetShootingPosition", nameof(AfterShootingPosition));
        Hook(typeof(ProjectileSniper), "GetAttackDir", nameof(AfterAttackDir));
        Hook(typeof(ProjectileDragonsBreath), "FindClosestTarget", nameof(AfterBreathTarget));
        // v0.43.4 (user: P2's green laser - Space Noodle - aimed at a P1 enemy)
        Hook(typeof(LaserBeamAttack), "FindTarget", nameof(AfterLaserTarget));
        Hook(typeof(LaserBeamAttack), "GetBeamStart", nameof(AfterLaserStart));
        // TEMP: where P2's axes head (sweep: P2's Axe did half of P1's damage)
        Hook(typeof(ProjectileAxe), "GetMovementDirection", nameof(AfterAxeDirection));
        // v0.43.2 (shotgun still 0 damage: its cone is checked in CheckZone, which never asked for a shooting
        // position) - P2's shotgun checks with P2's movement copy as "the player"
        var cz = AccessTools.DeclaredMethod(typeof(ProjectileShotgun), "CheckZone");
        if (cz != null)
        {
            harmony.Patch(cz, prefix: new HarmonyMethod(typeof(P2Weapons), nameof(BeforeP2Zone)), finalizer: new HarmonyMethod(typeof(P2Weapons), nameof(AfterP2Zone)));
            done.Add("ProjectileShotgun.CheckZone (P2 as the player)");
        }
        // v0.43.6 (autotest screenshot: P2's axes flew off away from P2 while P1's circled P1 - the game steers them
        // around "the player" every frame): P2's axes run their per-frame code with P2 as the player too
        foreach (var name in new[] { "MyFixedUpdate", "MyUpdate", "LateUpdate", "CheckDamage" })
        {
            var m = AccessTools.DeclaredMethod(typeof(ProjectileAxe), name);
            if (m == null) continue;
            bool ok = !IsNativeCodeHooked(m) && (IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(ProjectileBase)));
            if (!ok) { done.Add($"ProjectileAxe.{name} SKIPPED (shared native code)"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Weapons), nameof(BeforeP2Zone)), finalizer: new HarmonyMethod(typeof(P2Weapons), nameof(AfterP2Zone)));
            done.Add($"ProjectileAxe.{name} (P2 as the player)");
        }
        // v0.44.5 (user: bananas spawn from P2 then fly to P1; log: the GetMovementDirection turn-back never fired - the
        // game steers by its own dirToPlayer/distToPlayer): P2's bananas run their per-frame code with P2 as the player
        foreach (var name in new[] { "MyFixedUpdate", "StepMovement", "LateUpdate", "CheckHitPlayer", "CheckRadiusDamage" })
        {
            var m = AccessTools.DeclaredMethod(typeof(ProjectileBanana), name);
            if (m == null) continue;
            bool ok = !IsNativeCodeHooked(m) && (IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(ProjectileBase)));
            if (!ok) { done.Add($"ProjectileBanana.{name} SKIPPED (shared native code)"); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Weapons), nameof(BeforeP2Zone)), finalizer: new HarmonyMethod(typeof(P2Weapons), nameof(AfterP2Zone)));
            done.Add($"ProjectileBanana.{name} (P2 as the player)");
        }
        // v0.43.1 (user: P2's Dragon's Breath fired from P1; log: it found P2's enemies and attacked, hit nothing):
        // P2's breath is put back on P2's body before its hit checks (FixedUpdate) and after its Update
        foreach (var (name, pre) in new[] { ("FixedUpdate", true), ("Update", false) })
        {
            var m = AccessTools.DeclaredMethod(typeof(ProjectileDragonsBreath), name);
            if (m == null) continue;
            bool ok = !IsNativeCodeHooked(m) && (IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack)));
            if (!ok) { done.Add($"ProjectileDragonsBreath.{name} SKIPPED (shared native code)"); continue; }
            var hm = new HarmonyMethod(typeof(P2Weapons), nameof(KeepBreathOnP2));
            if (pre) harmony.Patch(m, prefix: hm); else harmony.Patch(m, postfix: hm);
            done.Add($"ProjectileDragonsBreath.{name} (kept on P2)");
        }
        // 1.0 alpha+ (players' report: "P2 fires at P1's target / in P1's direction"; aim sweep 300 m apart, P1 unarmed:
        // P2's Hero Sword / Corrupt Sword / Space Noodle damaged enemies at P1, P2's Bluetooth Dagger hit nothing):
        //  - the swords keep their hitbox in their own fields (hitboxPos, movingProjectilePosition), worked out from "the
        //    player" when the slash starts - moving the slash object didn't move them
        //  - the laser builds its damage box (center / rotation / halfExtents) in FixedUpdate from its target and "the
        //    player" - only GetBeamStart was moved
        //  - the dagger flies and hits relative to "the player" (it worked 30 m from P1, not 370 m)
        // So their start, per-frame and hit code runs with P2 as the player, like the shotgun, axe and banana above.
        foreach (var t in new[] { typeof(ProjectileHeroSword), typeof(ProjectileCringeSword), typeof(ProjectileBluetooth) })
            foreach (var name in new[] { "TryInit", "FindMovementDirection", "MyUpdate", "MyFixedUpdate", "StepMovement", "CheckZone", "CheckColliderCustom", "CheckSpawnCollision", "HitTarget" })
                ZoneHook(harmony, AccessTools.DeclaredMethod(t, name), false, done);
        foreach (var name in new[] { "Update", "FixedUpdate", "StartLaser", "RenderLaser", "FindTarget" })
            ZoneHook(harmony, AccessTools.DeclaredMethod(typeof(LaserBeamAttack), name), true, done);
        // User (P2's banana didn't fly at the nearest enemy; banana_aim: it flew along the P1 -> P2 line even after the
        // re-aim set its startDirection): the banana works out its throw direction in TryInit / FindMovementDirection from
        // "the player" (P1). They run with P2 as the player now. (Not GetMovementDirection: its turn-back hook needs P1
        // where P1 really is.)
        foreach (var name in new[] { "TryInit", "FindMovementDirection" })
            ZoneHook(harmony, AccessTools.DeclaredMethod(typeof(ProjectileBanana), name), false, done);
        // banana_aim (zone on TryInit alone): some throws never happened - the banana is set up at P1's spot and TryInit,
        // with "the player" now at P2 300 m away, gave up. So P2's banana is moved to P2 first (before the zone: it needs
        // P1 where P1 really is)
        var bananaInit = AccessTools.DeclaredMethod(typeof(ProjectileBanana), "TryInit");
        if (bananaInit != null && Harmony.GetAllPatchedMethods().Contains(bananaInit))
        {
            harmony.Patch(bananaInit, prefix: new HarmonyMethod(typeof(P2Weapons), nameof(MoveBananaBeforeInit)) { priority = Priority.First });
            done.Add("ProjectileBanana.TryInit (moved to P2 first)");
        }
        else done.Add("ProjectileBanana.TryInit move SKIPPED (zone not on it)");
        ModLog.Info($"P2Weapons: aimed-weapon hooks: {string.Join(", ", done)}");
    }

    // Adds "P2 as the player" around m. IL2CPP merges methods with identical machine code, and two Harmony hooks on one
    // native function crash: if a method sharing m's native code is already patched, the zone is added to THAT patch
    // (its detour also runs for m's calls); a native function gets the zone once.
    private static readonly HashSet<IntPtr> zoneNatives = new();
    private static void ZoneHook(Harmony harmony, System.Reflection.MethodInfo m, bool constant, List<string> done)
    {
        if (m == null) return;
        string label = $"{m.DeclaringType?.Name}.{m.Name}";
        IntPtr native = NativePointer(m);
        if (native != IntPtr.Zero && zoneNatives.Contains(native)) { done.Add($"{label} (zone shared)"); return; }
        System.Reflection.MethodBase target = null;
        if (native != IntPtr.Zero)
            foreach (var pm in Harmony.GetAllPatchedMethods())
                if (NativePointer(pm) == native) { target = pm; break; }
        if (target == null)
        {
            bool ok = !IsNativeCodeHooked(m) && (IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(ProjectileBase))
                                                || SharedOnlyWithin(m, typeof(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack)));
            if (!ok) { done.Add($"{label} SKIPPED (shared native code)"); return; }
            target = m;
        }
        // The patch's instance parameter must fit the patched method's type: only projectiles (or only always-on attacks).
        // v1.0.1 test: HeroSword.FindMovementDirection shares its (empty) code with PlayerMovement.Update - the zone
        // must never go on a player method.
        var family = constant ? typeof(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack) : typeof(ProjectileBase);
        if (target.DeclaringType == null || !family.IsAssignableFrom(target.DeclaringType)) { done.Add($"{label} SKIPPED (shares code with {target.DeclaringType?.Name}.{target.Name})"); return; }
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(P2Weapons), constant ? nameof(BeforeP2ZoneConst) : nameof(BeforeP2Zone)),
                finalizer: new HarmonyMethod(typeof(P2Weapons), constant ? nameof(AfterP2ZoneConst) : nameof(AfterP2Zone)));
            if (native != IntPtr.Zero) zoneNatives.Add(native);
            done.Add(target == m ? $"{label} (P2 as the player)" : $"{label} (P2 as the player, via {target.DeclaringType?.Name}.{target.Name})");
        }
        catch (Exception e) { done.Add($"{label} FAILED ({e.Message})"); }
    }

    private static bool IsP2Shot(ProjectileBase p) => p != null && body != null && p2Projectiles.Contains(p.Pointer);
    // P2's projectile (shots and P2's bananas) - P2Items: whose items a hit procs
    public static bool IsP2Projectile(ProjectileBase p) => IsP2Shot(p) || (p != null && body != null && p2Bananas.Contains(p.Pointer));

    private static void AfterShootingPosition(ProjectileBase __instance, ref Vector3 __result, System.Reflection.MethodBase __originalMethod)
    {
        if (!IsP2Shot(__instance)) return;
        var p1m = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
        if (p1m == null) return;
        Vector3 before = __result;
        if ((before - p1m.position).sqrMagnitude > (before - body.transform.position).sqrMagnitude) return;   // already at P2
        __result = body.transform.position + FacingTurn() * (before - p1m.position);
        AimLog(__instance, __originalMethod, $"shooting point {before} -> {__result}");
    }

    private static void AfterAttackDir(ProjectileBase __instance, ref Vector3 __result, System.Reflection.MethodBase __originalMethod)
    {
        if (!IsP2Shot(__instance)) return;
        Vector3 before = __result;
        Vector3 from = body.transform.position + Vector3.up;
        var e = NearestEnemyToP2(body.transform.position);
        Vector3 dir;
        if (e != null)
        {
            // Flat, like the game's own aim at P1's enemies (v0.43.1: the aim at the enemy's middle pointed the
            // shotgun's cone steeply up - y 0.83 - and it hit nothing on the ground)
            Vector3 aim = e.transform.position - from;
            aim.y = 0f;
            dir = aim.sqrMagnitude > 0.0001f ? aim.normalized : FacingTurn() * before;
        }
        else dir = FacingTurn() * before;
        __result = dir;
        AimLog(__instance, __originalMethod, $"aim {before} -> {dir} (at {(e != null ? $"{e.enemyData?.enemyName} {Vector3.Distance(e.transform.position, body.transform.position):F1} m from P2" : "nothing")}; projectile at {__instance.transform.position}, P2 {body.transform.position})");
    }

    private static void AfterBreathTarget(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack __instance)
    {
        var breath = __instance?.TryCast<ProjectileDragonsBreath>();
        if (breath == null || body == null || !breath.transform.IsChildOf(body.transform)) return;
        var before = breath.enemyTarget;
        var e = NearestEnemyToP2(body.transform.position);
        if (e != null && (e.transform.position - body.transform.position).sqrMagnitude <= breath.range * breath.range * 4f)
        {
            breath.enemyTarget = e;
            Vector3 d = e.transform.position - breath.transform.position;
            d.y = 0f;
            if (d.sqrMagnitude > 0.01f) breath.attackDir = d.normalized;
        }
        else breath.enemyTarget = null;
        if (AimLogDue("breath"))
            ModLog.Info($"[aim] P2's Dragon's Breath FindClosestTarget: game picked {(before != null ? before.enemyData?.enemyName + $" {Vector3.Distance(before.transform.position, body.transform.position):F1} m from P2" : "nothing")} -> {(breath.enemyTarget != null ? breath.enemyTarget.enemyData?.enemyName + " near P2" : "none in range")}; active {breath.isActive}, attacking {breath.IsAttacking()}, range {breath.range:F1}");
    }

    // P2's projectile code that asks "where is the player?" (shotgun cone, axe orbit): P2's movement copy is "the player"
    // and P1's own object stands at P2's spot for the call; both put back in the finalizer (outermost call only)
    private static void BeforeP2Zone(ProjectileBase __instance, System.Reflection.MethodBase __originalMethod, out PlayerMovement __state)
    {
        __state = null;
        if (!(IsP2Shot(__instance) || (__instance != null && body != null && p2Bananas.Contains(__instance.Pointer)))) return;
        // TEMP test switch: the banana's throw-direction zone off (banana_aim measures without and with it)
        if (!BananaFix && (__originalMethod?.Name == "TryInit" || __originalMethod?.Name == "FindMovementDirection") && __instance.TryCast<ProjectileBanana>() != null) return;
        __state = EnterZone();
    }

    // P2's banana to P2's spot before its TryInit (runs first, before the zone)
    private static void MoveBananaBeforeInit(ProjectileBase __instance)
    {
        if (!BananaFix || !IsP2Shot(__instance) || zoneMoved) return;
        MoveProjectile(__instance, "before TryInit");
    }

    // P2's always-on attacks (Space Noodle's laser) that ask "where is the player?"
    private static void BeforeP2ZoneConst(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack __instance, out PlayerMovement __state)
    {
        __state = OnP2Body(__instance) ? EnterZone() : null;
    }

    private static Exception AfterP2ZoneConst(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack __instance, Exception __exception, PlayerMovement __state)
    {
        if (__state == null) return __exception;
        ExitZone(__state);
        if (AimLogDue("zone" + __instance.GetIl2CppType().Name)) ModLog.Info($"[aim] P2's {__instance.GetIl2CppType().Name} (P2 as the player): enemy HP within 25 m of P2 {zoneHpBefore:F0} -> {EnemyHpNearP2():F0}");
        return __exception;
    }

    // P2's movement copy becomes PlayerMovement.Instance and P1's object stands at P2's spot; null if already in one
    private static PlayerMovement EnterZone()
    {
        if (P2Movement.Pm == null || zoneMoved) return null;
        var __state = PlayerMovement.Instance;
        PlayerMovement.Instance = P2Movement.Pm;
        if (ModLog.Debug) zoneHpBefore = EnemyHpNearP2();   // log only (sums every enemy: not in normal play, it runs per frame)
        // v0.43.3 (P2 as "the player" movement wasn't enough - no HP lost): P1's own object stands at P2's spot for
        // this check only (put back in the finalizer, same frame - nothing else runs in between)
        var p1 = MyPlayer.Instance;
        if (p1 != null)
        {
            zoneP1Pos = P2Items.P1Home(p1);
            zoneMoved = true;
            p1.transform.position = P2Movement.Pm.transform.position;
        }
        return __state;
    }

    private static void ExitZone(PlayerMovement before)
    {
        PlayerMovement.Instance = before;
        if (zoneMoved && MyPlayer.Instance != null) MyPlayer.Instance.transform.position = zoneP1Pos;
        zoneMoved = false;
    }
    private static Vector3 zoneP1Pos;
    private static bool zoneMoved;

    private static Exception AfterP2Zone(ProjectileBase __instance, Exception __exception, PlayerMovement __state)
    {
        if (__state == null) return __exception;
        ExitZone(__state);
        if (AimLogDue("zone" + __instance.GetIl2CppType().Name)) ModLog.Info($"[aim] P2's {__instance.GetIl2CppType().Name} (P2 as the player): enemy HP within 25 m of P2 {zoneHpBefore:F0} -> {EnemyHpNearP2():F0}, at {__instance.transform.position} ({Vector3.Distance(__instance.transform.position, body.transform.position):F1} m from P2)");
        return __exception;
    }
    private static float zoneHpBefore;

    private static float EnemyHpNearP2()
    {
        var em = Assets.Scripts.Managers.EnemyManager.Instance;
        if (em?.enemies == null || body == null) return 0f;
        float sum = 0f;
        foreach (var kv in em.enemies)
            if (kv.Value != null && (kv.Value.transform.position - body.transform.position).sqrMagnitude < 625f) sum += kv.Value.hp;
        return sum;
    }

    private static bool OnP2Body(Component c) => c != null && body != null && c.transform.IsChildOf(body.transform);

    private static void AfterLaserTarget(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack __instance)
    {
        var laser = __instance?.TryCast<LaserBeamAttack>();
        if (laser == null || !OnP2Body(laser)) return;
        var before = laser.target;
        var e = NearestEnemyToP2(body.transform.position);
        float range = 60f;
        try { if (laser.weaponBase != null) range = Mathf.Max(5f, WeaponUtility.GetWeaponRange(laser.weaponBase)); } catch { }
        laser.target = e != null && (e.transform.position - body.transform.position).sqrMagnitude <= range * range ? e : null;
        if (AimLogDue("laser"))
            ModLog.Info($"[aim] P2's laser FindTarget: game picked {(before != null ? $"{before.enemyData?.enemyName} {Vector3.Distance(before.transform.position, body.transform.position):F1} m from P2" : "nothing")} -> {(laser.target != null ? $"{laser.target.enemyData?.enemyName} {Vector3.Distance(laser.target.transform.position, body.transform.position):F1} m from P2" : "none")}; shooting {laser.isShooting}");
    }

    private static void AfterLaserStart(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack __instance, ref Vector3 __result)
    {
        if (!OnP2Body(__instance)) return;
        var p1m = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
        if (p1m == null) return;
        Vector3 before = __result;
        if ((before - p1m.position).sqrMagnitude > (before - body.transform.position).sqrMagnitude) return;
        __result = body.transform.position + FacingTurn() * (before - p1m.position);
        if (AimLogDue("laserstart")) ModLog.Info($"[aim] P2's laser beam start {before} -> {__result}");
    }

    private static void AfterAxeDirection(ProjectileBase __instance, Vector3 __result)
    {
        if (!IsP2Shot(__instance)) return;
        var axe = __instance.TryCast<ProjectileAxe>();
        var p1 = MyPlayer.Instance;
        if (axe == null || p1 == null || !AimLogDue("axe")) return;
        Vector3 pos = axe.transform.position;
        Vector3 toP1 = p1.transform.position - pos, toP2 = body.transform.position - pos;
        ModLog.Info($"[aim] P2's axe at {pos} ({toP2.magnitude:F1} m from P2, {toP1.magnitude:F1} from P1), heading {__result}: angle to P2 {Vector3.Angle(__result, toP2):F0}, to P1 {Vector3.Angle(__result, toP1):F0}; start {axe.startPosition}, desired {axe.desiredPosition}, moveTimer {axe.moveTimer:F2}");
    }

    // Where each always-on attack was placed on P2's body (AttachConstant)
    private static readonly Dictionary<IntPtr, Vector3> constantLocal = new();
    private static void KeepBreathOnP2(Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack __instance, System.Reflection.MethodBase __originalMethod)
    {
        if (__instance == null || body == null || !constantLocal.TryGetValue(__instance.Pointer, out var local)) return;
        var t = __instance.transform;
        Vector3 want = body.transform.TransformPoint(local);
        if ((t.position - want).sqrMagnitude < 0.01f) return;
        if (AimLogDue("breathpos" + __originalMethod?.Name))
            ModLog.Info($"[aim] P2's Dragon's Breath was at {t.position} ({Vector3.Distance(t.position, want):F1} m off; parent '{t.parent?.name}') around {__originalMethod?.Name} - put back on P2 at {want}");
        if (t.parent != body.transform) t.SetParent(body.transform, true);
        t.localPosition = local;
    }

    private static void AimLog(ProjectileBase p, System.Reflection.MethodBase m, string what)
    {
        string key = $"{p.GetIl2CppType().Name}.{m?.Name}";
        if (AimLogDue(key)) ModLog.Info($"[aim] P2's {key}: {what}");
    }

    // TEMP: one line per kind every 0.75 s, 60 in all (the first test only logged the moment the weapons were given)
    private static readonly Dictionary<string, float> aimLogAt = new();
    private static bool AimLogDue(string key)
    {
        if (aimHookLogs >= 60) return false;
        if (aimLogAt.TryGetValue(key, out float t) && Time.time - t < 0.75f) return false;
        aimLogAt[key] = Time.time;
        aimHookLogs++;
        return true;
    }

    // ---------- Enemy targeting for P2 (v0.37.1) ----------
    // User: P2's lightning favoured P1's enemies. The game picks targets with EnemyTargeting.*(position, ...) around
    // the projectile's spawn point - P1, because P2's shots are set up at P1 and moved afterwards. While the game works
    // on a P2 projectile (its Set/TryInit/target search) or in P2's stat context, a search position nearer P1 than P2
    // is moved by P2 - P1: the same search, around P2. Covers every weapon that picks targets this way.
    private static int p2Scope;
    private static int targetLogs;

    public static void ApplyTargetingHooks(Harmony harmony)
    {
        var hooked = new List<string>();
        var skipped = new List<string>();
        foreach (var m in typeof(EnemyTargeting).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            var ps = m.GetParameters();
            int idx = Array.FindIndex(ps, p => p.ParameterType == typeof(Vector3));
            if (idx < 0 || idx > 2) continue;
            if (IsNativeCodeHooked(m) || !(IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(EnemyTargeting)))) { skipped.Add(m.Name); continue; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Weapons), "TargetPos" + idx));
            hooked.Add($"{m.Name}(pos #{idx})");
        }
        // The projectile's own later target searches (chain lightning hopping on): P2 scope while they run
        var asm = typeof(ProjectileBase).Assembly;
        foreach (var t in asm.GetTypes().Where(t => typeof(ProjectileBase).IsAssignableFrom(t) && !MeleeTypes.Contains(t.Name)))
            foreach (var name in new[] { "TryInit", "FindMovementDirection", "FindTarget", "FindNewTarget" })
            {
                var m = AccessTools.DeclaredMethod(t, name);
                if (m == null) continue;
                if (IsNativeCodeHooked(m) || !(IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(ProjectileBase)))) { skipped.Add($"{t.Name}.{name}"); continue; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(P2Weapons), nameof(ScopeIn)), finalizer: new HarmonyMethod(typeof(P2Weapons), nameof(ScopeOut)));
                hooked.Add($"{t.Name}.{name}");
            }
        ModLog.Info($"P2Weapons: targeting around P2 on {hooked.Count} methods ({string.Join(", ", hooked)}); skipped {(skipped.Count > 0 ? string.Join(", ", skipped) : "none")}");
    }

    private static void ScopeIn(ProjectileBase __instance, out bool __state)
    {
        __state = __instance != null && p2Projectiles.Contains(__instance.Pointer);
        if (__state) p2Scope++;
    }

    private static Exception ScopeOut(Exception __exception, bool __state)
    {
        if (__state && p2Scope > 0) p2Scope--;
        return __exception;
    }

    private static void TargetPos0(ref Vector3 __0) => ToP2(ref __0);
    private static void TargetPos1(ref Vector3 __1) => ToP2(ref __1);
    private static void TargetPos2(ref Vector3 __2) => ToP2(ref __2);

    private static void ToP2(ref Vector3 pos)
    {
        if (body == null || (p2Scope == 0 && !P2Inventory.InP2Context)) return;
        var p1 = MyPlayer.Instance;
        if (p1 == null) return;
        Vector3 a = p1.transform.position, b = body.transform.position;
        if ((pos - a).sqrMagnitude >= (pos - b).sqrMagnitude) return;   // already around P2
        Vector3 before = pos;
        pos += b - a;
        if (targetLogs++ < 8) ModLog.Info($"[target] P2's enemy search moved from {before} to {pos} (scope {p2Scope}, P2 context {P2Inventory.InP2Context})");
    }

    private static void OnProjectileSet(ProjectileBase __instance, WeaponAttack weaponAttack)
    {
        if (__instance != null && weaponAttack != null && p2Attacks.Contains(weaponAttack.Pointer) && p2Scope > 0) p2Scope--;
        HookSeen("ProjectileBase.Set");
        bool p2Shot = weaponAttack != null && p2Attacks.Contains(weaponAttack.Pointer);
        if (p2Shot) p2Projectiles.Add(__instance.Pointer);
        else
        {
            p2Projectiles.Remove(__instance.Pointer);
            // v0.49 item sweep (P1's Bonker / Lightning Gloves never procced): the game reuses banana objects - one P2
            // threw before, now thrown by P1, still counted as P2's (its hits procced P2's items, it flew back to P2)
            p2Bananas.Remove(__instance.Pointer);
            p2BananaObjs.Remove(__instance.Pointer);
        }
        if (p2Shot)
        {
            shotSets++;
            MoveAttack(weaponAttack);   // the game may have put the attack back at P1 for this shot
            MoveProjectile(__instance, "Set");
            Reaim(__instance, "Set");
        }
    }

    private static void OnProjectileSpawned(WeaponAttack __instance, ProjectileBase projectile)
    {
        HookSeen("WeaponAttack.SuccessfullySpawnedProjectile");
        if (projectile == null || !p2Attacks.Contains(__instance.Pointer)) return;
        shotSpawned++;
        MoveProjectile(projectile, "SuccessfullySpawned");
        Reaim(projectile, "SuccessfullySpawned");
    }

    // The game aims each shot from P1's position (P1's nearest enemy); moving the shot to P2 kept P1's direction
    // (user, v0.15.0). Re-aim P2's shots at the enemy nearest to P2: direction + rotation, and the target for
    // homing / target-following projectiles. The shot's vertical angle is kept.
    private static int aimLogs;
    private static void Reaim(ProjectileBase p, string via)
    {
        if (body == null) return;
        if (p.TryCast<ProjectileAxe>() != null) return;   // axes go by facing (turned in MoveProjectile), not at an enemy
        if (p.TryCast<ProjectileBloodMagic>() != null) return;   // retargeted in MoveProjectile
        var target = NearestEnemyToP2(p.transform.position);
        if (target == null) return;
        Vector3 pos = p.transform.position;
        Vector3 aim = target.transform.position + Vector3.up * Mathf.Max(0.5f, target.meshHeight * 0.5f) - pos;
        Vector3 old = p.direction;
        Vector3 flat = new Vector3(aim.x, 0f, aim.z);
        if (flat.sqrMagnitude < 0.0001f) return;
        float horiz = new Vector2(old.x, old.z).magnitude;
        Vector3 dir = old.sqrMagnitude > 0.0001f
            ? (flat.normalized * Mathf.Max(horiz, 0.01f) + Vector3.up * old.y).normalized
            : aim.normalized;
        p.direction = dir;
        p.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        var homing = p.TryCast<ProjectileHoming>();
        if (homing != null) homing.enemyTarget = target;
        var basic = p.TryCast<ProjectileBasic>();
        if (basic != null && basic.currentTarget != null) basic.currentTarget = target.gameObject;
        // Stored aim directions follow the new aim (they were set at P1 towards P1's enemy)
        var sniper = p.TryCast<ProjectileSniper>();
        if (sniper != null) sniper.attackDir = dir;
        var shotgun = p.TryCast<ProjectileShotgun>();
        if (shotgun != null) shotgun.attackDir = dir;
        var dex = p.TryCast<ProjectileDexecutioner>();
        if (dex != null) dex.attackDir = dir;
        // 1.0 alpha+ (user: P2's banana doesn't fly at the nearest enemy): the banana steers by its own startDirection /
        // movementVelocity, worked out by the game from P1's position (towards a target near P2 - from 300 m away that is
        // roughly "from P1 to P2") and only turned by the facing difference when moved. They follow the new aim now.
        var banana = p.TryCast<ProjectileBanana>();
        if (banana != null && BananaFix)
        {
            Vector3 flatDir = new Vector3(dir.x, 0f, dir.z).normalized;
            banana.startDirection = flatDir * Mathf.Max(banana.startDirection.magnitude, 0.01f) + Vector3.up * banana.startDirection.y;
            banana.movementVelocity = flatDir * new Vector3(banana.movementVelocity.x, 0f, banana.movementVelocity.z).magnitude + Vector3.up * banana.movementVelocity.y;
        }
        if (aimLogs++ < 5) ModLog.Info($"P2 projectile {p.name} re-aimed via {via} at {target.enemyData?.enemyName} ({flat.magnitude:F1} away): {old} -> {dir}");
    }

    private static Assets.Scripts.Actors.Enemies.Enemy NearestEnemyToP2(Vector3 from)
    {
        var em = Assets.Scripts.Managers.EnemyManager.Instance;
        if (em?.enemies == null) return null;
        Assets.Scripts.Actors.Enemies.Enemy best = null;
        float bestD = float.MaxValue;
        foreach (var kv in em.enemies)
        {
            var e = kv.Value;
            if (e == null || e.hp <= 0f || !e.gameObject.activeInHierarchy) continue;
            float d = (e.transform.position - from).sqrMagnitude;
            if (d < bestD)
            {
                bestD = d;
                best = e;
            }
        }
        return best;
    }

    private static int moveLogs;
    // Once per shot: the game calls SuccessfullySpawnedProjectile before Set for some weapons, and Set forgot the first
    // move (pooled reuse) -> the Orc's axes were moved twice (v0.30.0 log). Now at most once per frame per projectile.
    private static readonly Dictionary<IntPtr, int> movedFrame = new();
    private static void MoveProjectile(ProjectileBase p, string via)
    {
        if (body == null) return;
        if (movedFrame.TryGetValue(p.Pointer, out int f) && f == Time.frameCount) return;
        // All weapon projectiles, by how the game places them (user: no whack-a-mole per weapon; the fields come from
        // the types dump, v0.32.3):
        //  1. on an enemy (strikes: blood magic, lightning bolt, bluetooth...): the enemy was picked near P1 -> the
        //     enemy nearest P2, same placement relative to it, target fields pointed at it
        //  2. at the player (everything else): P1 -> P2 offset, and the stored points/directions turned from P1's
        //     frame (position + facing) to P2's: axe/black hole paths, attack/push/start directions, sword hitboxes
        var blood = p.TryCast<ProjectileBloodMagic>();
        var blue = p.TryCast<ProjectileBluetooth>();
        if ((blood != null && blood.target == null) || (blue != null && blue.target == null)) return;   // target not chosen yet: next hook
        var p1m = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
        if (p1m == null) return;
        movedFrame[p.Pointer] = Time.frameCount;
        if (movedFrame.Count > 2000) movedFrame.Clear();
        Vector3 before = p.transform.position, p1 = p1m.position, p2 = body.transform.position;
        // Already at P2 (moved before its TryInit, v0.30.7): not again
        if ((before - p1).sqrMagnitude >= (before - p2).sqrMagnitude) return;

        var anchor = NearestEnemyToP2(before);   // (nearest enemy to the spawn point)
        bool onEnemy = anchor != null && (anchor.transform.position - before).sqrMagnitude < 2.5f * 2.5f && (before - p1).sqrMagnitude > 6f * 6f;
        if (blood != null || blue != null || onEnemy)
        {
            var oldTarget = blood?.target ?? blue?.target ?? anchor;
            var t2 = NearestEnemyToP2(p2);
            if (t2 == null || oldTarget == null) return;
            Vector3 rel = before - oldTarget.transform.position;
            SetPosition(p, t2.transform.position + rel);
            if (blood != null) blood.target = t2;
            if (blue != null) { blue.target = t2; blue.lastTarget = t2.gameObject; }
            var homing = p.TryCast<ProjectileHoming>();
            if (homing != null) homing.enemyTarget = t2;
            var basic = p.TryCast<ProjectileBasic>();
            if (basic != null) basic.currentTarget = t2.gameObject;
            if (moveLogs++ < 8) ModLog.Info($"P2 {p.GetIl2CppType().Name} (strikes an enemy) moved to {t2.enemyData?.enemyName} near P2 ({Vector3.Distance(t2.transform.position, p2):F1} away)");
            return;
        }

        var turn = FacingTurn();
        Vector3 Pos(Vector3 v) => p2 + turn * (v - p1);   // a point in P1's frame -> the same point in P2's
        SetPosition(p, Pos(before));
        p.direction = turn * p.direction;
        p.transform.rotation = turn * p.transform.rotation;
        var axe = p.TryCast<ProjectileAxe>();
        if (axe != null) { axe.startPosition = Pos(axe.startPosition); axe.desiredPosition = Pos(axe.desiredPosition); axe.pushDir = turn * axe.pushDir; }
        var hole = p.TryCast<ProjectileBlackHole>();
        if (hole != null) { hole.startPosition = Pos(hole.startPosition); hole.desiredPosition = Pos(hole.desiredPosition); hole.pushDir = turn * hole.pushDir; }
        var arrow = p.TryCast<ProjectileArrow>();
        if (arrow != null) arrow.pushDir = turn * arrow.pushDir;
        var shotgun = p.TryCast<ProjectileShotgun>();
        if (shotgun != null) shotgun.attackDir = turn * shotgun.attackDir;
        var sniper = p.TryCast<ProjectileSniper>();
        if (sniper != null) sniper.attackDir = turn * sniper.attackDir;
        var dex = p.TryCast<ProjectileDexecutioner>();
        if (dex != null) dex.attackDir = turn * dex.attackDir;
        var whirl = p.TryCast<ProjectileWhirlwind>();
        if (whirl != null) whirl.startDirection = turn * whirl.startDirection;
        var banana = p.TryCast<ProjectileBanana>();
        if (banana != null) { banana.startDirection = turn * banana.startDirection; banana.movementVelocity = turn * banana.movementVelocity; p2Bananas.Add(p.Pointer); p2BananaObjs[p.Pointer] = banana; bananaStart.Remove(p.Pointer); }
        var hero = p.TryCast<ProjectileHeroSword>();
        if (hero != null) { hero.movingProjectileDir = turn * hero.movingProjectileDir; hero.movingProjectileRotation = turn * hero.movingProjectileRotation; hero.hitboxPos = Pos(hero.hitboxPos); hero.movingProjectilePosition = Pos(hero.movingProjectilePosition); }
        var cringe = p.TryCast<ProjectileCringeSword>();
        if (cringe != null) { cringe.movingProjectileDir = turn * cringe.movingProjectileDir; cringe.movingProjectileRotation = turn * cringe.movingProjectileRotation; cringe.hitboxPos = Pos(cringe.hitboxPos); cringe.movingProjectilePosition = Pos(cringe.movingProjectilePosition); }
        if (moveLogs++ < 8) ModLog.Info($"P2 projectile {p.GetIl2CppType().Name} moved via {via}: {before} -> {p.transform.position}");
    }

    private static void SetPosition(ProjectileBase p, Vector3 pos)
    {
        p.transform.position = pos;
        var rb = p.GetComponent<Rigidbody>();
        if (rb != null) rb.position = pos;
    }

    // ---------- Projectiles that come back to the owner (banana) ----------
    // The banana returns to THE player (P1) and is caught there. P2's: its movement direction is turned towards P2
    // once it heads back (GetMovementDirection postfix: the game's direction points at P1), and it's caught near P2.
    private static readonly HashSet<IntPtr> p2Bananas = new();
    // The banana objects themselves (1.0 alpha, 6-minute perf test: looking them up with a scene-wide search every frame
    // cost ~1.5 ms a frame once P2 had the Bananarang)
    private static readonly Dictionary<IntPtr, ProjectileBanana> p2BananaObjs = new();

    public static void ApplyReturnHooks(Harmony harmony)
    {
        var m = AccessTools.DeclaredMethod(typeof(ProjectileBanana), "GetMovementDirection");
        if (m == null) return;
        bool ok = !IsNativeCodeHooked(m) && (IsNativeCodeUnique(m) || SharedOnlyWithin(m, typeof(ProjectileBase)));
        if (!ok) { ModLog.Warn("P2Weapons: ProjectileBanana.GetMovementDirection not hookable (shared native code)"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(typeof(P2Weapons), nameof(AfterBananaDirection)));
        ModLog.Info("P2Weapons: P2's bananas return to P2");
    }

    // TEMP test switch (autotest 'bananafix off|on'): one run measures P2's banana without and with the fix
    public static bool BananaFix = true;

    // TEMP probe ('bananaprobe'): P2's bananas on their way OUT - angle between their heading and the direction to the
    // enemy nearest the banana, sampled per frame
    private static int probeSamples;
    private static float probeAngleSum;
    private static int probeWithin30;
    public static string BananaProbe(bool reset)
    {
        string s = probeSamples == 0 ? "no outward banana frames" :
            $"{probeSamples} bananas: first 0.3 s of flight vs the nearest enemy, mean angle {probeAngleSum / probeSamples:F0} deg, within 30 deg {100f * probeWithin30 / probeSamples:F0}%";
        if (reset) { probeSamples = probeWithin30 = 0; probeAngleSum = 0f; }
        return s;
    }

    private static int bananaLogs, fieldLogs;
    private static void AfterBananaDirection(ProjectileBase __instance, ref Vector3 __result)
    {
        if (__instance == null || body == null || !p2Bananas.Contains(__instance.Pointer)) return;
        // TEMP (banana_aim: the re-aim's startDirection didn't steer it): the first frames of the first bananas, every field
        if (Autotest.Active && fieldLogs < 60 && Time.frameCount % 3 == 0 && MyPlayer.Instance != null
            && (MyPlayer.Instance.transform.position - body.transform.position).sqrMagnitude > 100f * 100f)
        {
            var b = __instance.TryCast<ProjectileBanana>();
            if (b != null)
            {
                fieldLogs++;
                var e0 = NearestEnemyToP2(b.transform.position);
                ModLog.Info($"[bananafields] {b.Pointer.ToInt64() % 10000} pos {b.transform.position} result {__result} start {b.startDirection} vel {b.movementVelocity} dir {b.direction} " +
                            $"rb {(b.rb != null ? b.rb.velocity.ToString() : "-")} toPlayer {b.dirToPlayer} dist {b.distToPlayer:F1} close {b.isCloseToPlayer}; nearest enemy dir " +
                            $"{(e0 != null ? (e0.transform.position - b.transform.position).normalized.ToString() : "-")}");
            }
        }
        var p1m = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
        if (p1m == null || __result.sqrMagnitude < 0.0001f) return;
        Vector3 pos = __instance.transform.position;
        Vector3 toP1 = p1m.position - pos, toP2 = body.transform.position + Vector3.up - pos;
        // Heading for P1 = on its way back: go to P2 instead, same speed
        if (Vector3.Angle(__result, toP1) > 12f)
        {
            return;
        }
        __result = toP2.normalized * __result.magnitude;
        if (bananaLogs++ < 3) ModLog.Info($"[banana] P2's banana turned back towards P2 ({toP2.magnitude:F1} away)");
    }

    // Caught by P2 when back (called from Tick)
    // TEMP probe (autotest): each P2 banana's real flight - where it is 0.3 s after it was first seen vs the direction to
    // the enemy nearest its starting point (the steering hook is called too rarely to measure the heading)
    private static readonly Dictionary<IntPtr, (Vector3 pos, float time, Vector3 toEnemy, bool measured)> bananaStart = new();
    private static void ProbeBanana(ProjectileBanana b)
    {
        if (!Autotest.Active) return;
        if (!bananaStart.TryGetValue(b.Pointer, out var s))
        {
            var e = NearestEnemyToP2(b.transform.position);
            bananaStart[b.Pointer] = (b.transform.position, Time.time, e != null ? e.transform.position - b.transform.position : Vector3.zero, false);
            return;
        }
        if (s.measured || Time.time - s.time < 0.3f || s.toEnemy == Vector3.zero) return;
        Vector3 flight = b.transform.position - s.pos;
        float ang = Vector3.Angle(new Vector3(flight.x, 0f, flight.z), new Vector3(s.toEnemy.x, 0f, s.toEnemy.z));
        probeSamples++;
        probeAngleSum += ang;
        if (ang <= 30f) probeWithin30++;
        bananaStart[b.Pointer] = (s.pos, s.time, s.toEnemy, true);
    }

    private static void CatchBananas()
    {
        if (p2Bananas.Count == 0) return;
        var done = new List<IntPtr>();
        foreach (var kv in p2BananaObjs)
        {
            var b = kv.Value;
            if (b == null || !b.gameObject.activeInHierarchy || !p2Bananas.Contains(kv.Key)) continue;
            ProbeBanana(b);
            float d = Vector3.Distance(b.transform.position, body.transform.position + Vector3.up);
            if (Time.time > b.readyToCollectTime && b.sqrCollectDistance > 0f && d * d <= b.sqrCollectDistance)
            {
                done.Add(b.Pointer);
                try { b.ProjectileDone(); } catch { }
            }
        }
        foreach (var k in done) { p2Bananas.Remove(k); p2BananaObjs.Remove(k); }
        // ones no longer P2's (re-thrown by P1) or long gone
        if (p2BananaObjs.Count > p2Bananas.Count + 20)
            foreach (var k in p2BananaObjs.Keys.Where(k => !p2Bananas.Contains(k)).ToList()) p2BananaObjs.Remove(k);
        if (p2Bananas.Count > 200) { p2Bananas.Clear(); p2BananaObjs.Clear(); }
    }

    // From P1's facing to P2's (yaw only)
    private static Quaternion FacingTurn()
    {
        var pr = MyPlayer.Instance != null ? MyPlayer.Instance.playerRenderer : null;
        var p1Model = pr != null && pr.rendererObject != null ? pr.rendererObject.transform : null;
        if (p1Model == null || body == null) return Quaternion.identity;
        return Quaternion.Euler(0f, Mathf.DeltaAngle(p1Model.eulerAngles.y, body.transform.eulerAngles.y), 0f);
    }

    private static Vector3 P1ToP2()
    {
        var pr = MyPlayer.Instance != null ? MyPlayer.Instance.playerRenderer : null;
        var p1Model = pr != null && pr.rendererObject != null ? pr.rendererObject.transform : null;
        return p1Model != null && body != null ? body.transform.position - p1Model.position : Vector3.zero;
    }

    // P2's weapons live in P2's weapon holder (P2Inventory.Weapons); the mod only fires them.
    public static void Start(GameObject p2Body)
    {
        Stop();
        body = p2Body;
        var holder = P2Inventory.Weapons;
        if (holder?.weapons == null)
        {
            // P2's own inventory is off (config) or failed: a standalone starting weapon, as before v0.16
            var data = P2Select.Picked ?? MyPlayer.Instance?.playerRenderer?.characterData;
            if (data?.weapon == null) return;
            try
            {
                var w = new WeaponBase(data.weapon);
                slots.Add(new Slot { weapon = w });
                p2WeaponPtrs.Clear();
                p2WeaponPtrs.Add(w.Pointer);
                ModLog.Info($"P2Weapons: no P2 inventory, standalone {data.weapon.eWeapon}");
            }
            catch (Exception e) { ModLog.Error($"P2Weapons: standalone weapon failed: {e}"); }
            return;
        }
        if (holder.weapons.Count == 0)
        {
            // The character's starting weapon, added the game's way in P2's stat context (kept off P1's HUD)
            var data = P2Select.Picked ?? MyPlayer.Instance?.playerRenderer?.characterData;
            if (data?.weapon != null)
            {
                P2Inventory.GuardsOn();
                bool entered = P2Inventory.EnterP2();
                try { holder.AddWeapon(data.weapon, new Il2CppSystem.Collections.Generic.List<Assets.Scripts.Inventory__Items__Pickups.Stats.StatModifier>()); }
                catch (Exception e) { ModLog.Error($"P2Weapons: adding starting weapon failed: {e}"); }
                finally
                {
                    if (entered) P2Inventory.Exit();
                    P2Inventory.GuardsOff();
                }
            }
        }
        Sync();
    }

    // Mirror P2's inventory weapons into the firing list (keeps each weapon's cooldown timer)
    public static void Sync()
    {
        var weapons = P2Inventory.Weapons?.weapons;
        if (weapons == null) return;
        var current = new List<WeaponBase>();
        foreach (var kv in weapons) if (kv.Value != null) current.Add(kv.Value);
        var keep = slots.Where(s => current.Any(w => w.Pointer == s.weapon.Pointer)).ToList();
        // A weapon taken away (autotest 'unweapon'): its always-on object goes too
        foreach (var gone in slots.Where(s => !keep.Contains(s)))
            if (gone.constant != null) UnityEngine.Object.Destroy(gone.constant);
        foreach (var w in current)
            if (!keep.Any(s => s.weapon.Pointer == w.Pointer)) keep.Add(new Slot { weapon = w });
        slots.Clear();
        slots.AddRange(keep);
        p2WeaponPtrs.Clear();
        foreach (var s in slots) p2WeaponPtrs.Add(s.weapon.Pointer);
        foreach (var s in slots)
            if (IsConstant(s) && s.constant == null) AttachConstant(s);
        ModLog.Info($"P2 weapons: {string.Join(", ", slots.Select(s => $"{s.weapon.weaponData.eWeapon} L{s.weapon.level}{(IsConstant(s) ? " (always-on)" : "")}"))}");
    }

    private static readonly HashSet<IntPtr> p2WeaponPtrs = new();
    public static bool IsP2(WeaponBase w) => w != null && p2WeaponPtrs.Contains(w.Pointer);
    public static bool IsFiring(EWeapon w) => slots.Any(s => s.weapon?.weaponData != null && s.weapon.weaponData.eWeapon == w);

    // The weapons themselves belong to P2's inventory and stay; only the firing list is cleared
    public static void Stop()
    {
        slots.Clear();
        p2Attacks.Clear();
        body = null;
    }

    // Called every unpaused frame from P2Control.
    // TEMP (user: P2's aura flickers): what changes on P2's always-on attacks from frame to frame, P1's beside it
    private static int auraLogs;
    private static string lastAura = "";
    private static void WatchAura()
    {
        if (auraLogs >= 60) return;
        foreach (var s in slots)
        {
            if (s.constant == null) continue;
            var ca = s.constant.GetComponent<CombatAura>();
            string Describe(CombatAura a) => a == null ? "-" :
                $"radius {a.radius:F2} old {a.oldRadius:F2} scaleTimer {a.scaleTimer:F2}, scale {a.transform.localScale}, active {a.gameObject.activeInHierarchy}, enabled {a.enabled}";
            CombatAura p1 = null;
            var auras = MyPlayer.Instance?.playerConstantAttacks?.auras;
            if (auras != null && auras.ContainsKey(s.weapon.weaponData.eWeapon))
                p1 = auras[s.weapon.weaponData.eWeapon]?.GetComponent<CombatAura>();
            string now = $"P2 {Describe(ca)} | P1 {Describe(p1)}";
            if (now == lastAura) continue;
            lastAura = now;
            auraLogs++;
            ModLog.Info($"[aura] frame {Time.frameCount}: {now}");
        }
    }

    // P2's always-on attacks turn like P1's (user: P2's Aegis shields didn't circle P2). P1's are turned by the game's
    // AuraAttacks manager, which only knows P1's; P2's turn here at the game's own rate (ConstantAttack.
    // GetAuraRotationSpeed, unless IsManualRotation), around a fixed heading (P2's body turns with P2's facing).
    private static readonly Dictionary<IntPtr, float> spinAngle = new();
    private static int spinLogs;
    private static readonly HashSet<string> spinSeen = new();

    // P2's Aegis (the game's health code uses "the player's" aegis: P2GameHealth puts P2's in place during P2's hits)
    public static Assets.Scripts.Game.Combat.ConstantAttacks.AegisAttack P2Aegis()
    {
        foreach (var s in slots)
        {
            if (s.constant == null) continue;
            var a = s.constant.GetComponent<Assets.Scripts.Game.Combat.ConstantAttacks.AegisAttack>();
            if (a != null) return a;
        }
        return null;
    }

    // v0.37.2 (user: P2's Aegis turned at a different rate than P1's): GetAuraRotationSpeed isn't degrees per second
    // as the mod assumed (log: 2 for the aura). P1's real turn rate is measured instead - each of P1's always-on
    // attacks, its yaw change over 0.25 s (only while P1's model doesn't turn, so P1's facing doesn't count) divided by
    // its game value = the factor; P2's turn at game value x factor. 1.0 alpha (user: P2's Aegis didn't turn - P1 had
    // no always-on attack to measure, the default 1 made it 3.5 deg/s): default 20, measured on P1's Aegis (-70 deg/s
    // at game value -3.5).
    private static float spinFactor = 20f;
    private static bool spinMeasured;
    private static float measureAt = -1f, p1ModelYawAt;
    private static readonly Dictionary<IntPtr, float> p1YawAt = new();
    private static int factorLogs;

    private static void MeasureP1Spin()
    {
        var p1 = MyPlayer.Instance;
        var auras = p1?.playerConstantAttacks?.auras;
        var model = p1?.playerRenderer?.rendererObject?.transform;
        if (auras == null || model == null) return;
        float now = Time.time;
        // v0.37.6 log: occasional samples of ~2 deg/s (factor 1.07 / 0.55 instead of ~20), likely across a pause or
        // menu: no samples then, and a new start after
        if (Assets.Scripts.Utility.MyTime.paused || Time.timeScale <= 0f || LevelupScreen.isLevelingUp) { measureAt = -1f; return; }
        if (measureAt < 0f || now - measureAt < 0.25f)
        {
            if (measureAt < 0f) Sample(auras, model, now);
            return;
        }
        float dt = now - measureAt;
        bool steady = Mathf.Abs(Mathf.DeltaAngle(p1ModelYawAt, model.eulerAngles.y)) < 1f;
        foreach (var kv in auras)
        {
            var ca = kv.Value;
            if (ca == null || !p1YawAt.TryGetValue(ca.Pointer, out float was)) continue;
            float speed = 0f;
            bool manual = false;
            try { manual = ca.IsManualRotation(); speed = ca.GetAuraRotationSpeed(); } catch { }
            if (manual || Mathf.Abs(speed) < 0.001f || !steady) continue;
            float rate = Mathf.DeltaAngle(was, ca.transform.eulerAngles.y) / dt;
            if (Mathf.Abs(rate) < 0.5f) continue;   // too slow to measure (4 samples a second: up to 720 deg/s without wrapping)
            float f = rate / speed;
            if (spinMeasured && f < spinFactor * 0.5f) continue;   // a slowed sample (just after a pause), not the real rate
            if (!spinMeasured || Mathf.Abs(f - spinFactor) > 0.05f * Mathf.Abs(spinFactor))
                if (factorLogs++ < 6) ModLog.Info($"[spin] P1's {kv.Key} turns {rate:F1} deg/s at game value {speed:F2} -> factor {f:F2}");
            spinFactor = f;
            spinMeasured = true;
            break;
        }
        Sample(auras, model, now);
    }

    private static void Sample(Il2CppSystem.Collections.Generic.Dictionary<EWeapon, Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack> auras, Transform model, float now)
    {
        measureAt = now;
        p1ModelYawAt = model.eulerAngles.y;
        p1YawAt.Clear();
        foreach (var kv in auras) if (kv.Value != null) p1YawAt[kv.Value.Pointer] = kv.Value.transform.eulerAngles.y;
    }

    // After P2's body has turned and moved this frame (P2Control). 1.0 alpha (user: P2's Chunkers / Aegis jittered
    // when P2 turned; [spinprobe]: they were turned before the body, so each frame's body turn carried them along)
    public static void AfterBodyMoved()
    {
        if (body == null) return;
        SpinConstants(Time.deltaTime);
    }

    private static void SpinConstants(float dt)
    {
        try { MeasureP1Spin(); } catch (Exception e) { LogOnce("measure spin", e); }
        foreach (var s in slots)
        {
            if (s.constant == null) continue;
            var ca = s.constant.GetComponent<Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack>();
            if (ca == null) continue;
            AimConstant(s, ca);
            bool manual = false;
            float speed = 0f;
            // The game value in P2's stat context (it may grow with P2's stats/weapon level)
            bool entered = P2Inventory.EnterP2();
            try { manual = ca.IsManualRotation(); speed = ca.GetAuraRotationSpeed(); }
            catch { }
            finally { if (entered) P2Inventory.Exit(); }
            if (manual) continue;
            spinAngle.TryGetValue(s.constant.Pointer, out float a);
            a = (a + speed * spinFactor * dt) % 360f;
            spinAngle[s.constant.Pointer] = a;
            // Tilted with the ground under P2, like P1's (user: P2's aura stayed flat on slopes)
            s.constant.transform.rotation = Quaternion.FromToRotation(Vector3.up, P2Control.SlopeUp) * Quaternion.Euler(0f, a, 0f);
            if (spinLogs < 8 && speed != 0f && spinSeen.Add($"{s.weapon.weaponData.eWeapon}/{spinMeasured}"))
            {
                spinLogs++;
                // P1's own one of the same weapon, measured, to compare
                var auras = MyPlayer.Instance?.playerConstantAttacks?.auras;
                var w = s.weapon.weaponData.eWeapon;
                string p1 = auras != null && auras.ContainsKey(w) && auras[w] != null ? $"P1's at yaw {auras[w].transform.eulerAngles.y:F0}" : "P1 has none";
                ModLog.Info($"[spin] P2's {w} turns at {speed * spinFactor:F1} deg/s (game value {speed:F2} x factor {spinFactor:F2}, {(spinMeasured ? "measured on P1" : "not measured yet")}); {p1}");
            }
        }
    }

    // TEMP test-only (autotest 'spinprobe <secs>'): every frame at the end of LateUpdate, each always-on attack's yaw
    // and, for Chunkers, where its rocks are around the body - P2's and P1's (user: P2's Chunkers / Aegis don't
    // turn and jitter when P2 turns)
    private static float probeUntil = -1f;
    private static readonly Dictionary<string, float> probeYaw = new();
    public static string SpinProbe(float secs)
    {
        probeUntil = Time.time + secs;
        probeYaw.Clear();
        return $"spinprobe {secs:F1} s: spin factor {spinFactor:F2} ({(spinMeasured ? "measured" : "not measured")})";
    }
    public static void ProbeLate()
    {
        if (Time.time > probeUntil) return;
        var parts = new List<string>();
        void One(string who, Transform owner, Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack ca)
        {
            if (ca == null || owner == null) return;
            string key = who + ca.GetIl2CppType().Name;
            float y = ca.transform.eulerAngles.y;
            float dy = probeYaw.TryGetValue(key, out float was) ? Mathf.DeltaAngle(was, y) : 0f;
            probeYaw[key] = y;
            string s = $"{key} yaw {y:F0} (+{dy:F1})";
            var chunk = ca.TryCast<ChunkersAttack>();
            var rp = chunk?.rotatingProjectiles;
            if (rp != null)
            {
                Vector3 rel = rp.transform.position - owner.position;
                s += $", rocks object {(rp.transform.IsChildOf(ca.transform) ? "child" : "not child")} at {rel} yaw {rp.transform.eulerAngles.y:F0}, rotationSpeed {rp.rotationSpeed:F1}, active {rp.isActive}";
                if (rp.prefabs != null && rp.prefabs.Count > 0 && rp.prefabs[0] != null)
                {
                    Vector3 r0 = rp.prefabs[0].transform.position - owner.position;
                    float ang = Mathf.Atan2(r0.x, r0.z) * Mathf.Rad2Deg;
                    float da = probeYaw.TryGetValue(key + "rock", out float wasA) ? Mathf.DeltaAngle(wasA, ang) : 0f;
                    probeYaw[key + "rock"] = ang;
                    s += $", rock0 at {r0.magnitude:F2} m angle {ang:F0} (+{da:F1}), sim space {rp.prefabs[0].main.simulationSpace}";
                }
            }
            parts.Add(s);
        }
        var p1 = MyPlayer.Instance;
        var p1Model = p1?.playerRenderer?.rendererObject?.transform;
        if (p1?.playerConstantAttacks?.auras != null)
            foreach (var kv in p1.playerConstantAttacks.auras) One("P1 ", p1Model, kv.Value);
        if (body != null)
            foreach (var s in slots)
                if (s.constant != null) One("P2 ", body.transform, s.constant.GetComponent<Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack>());
        float by = body != null ? body.transform.eulerAngles.y : 0f;
        float bdy = probeYaw.TryGetValue("body", out float bw) ? Mathf.DeltaAngle(bw, by) : 0f;
        probeYaw["body"] = by;
        ModLog.Info($"[spinprobe] f{Time.frameCount} P2 body yaw {by:F0} (+{bdy:F1}); {string.Join("; ", parts)}");
    }

    // Always-on attacks with their own aim or parts (types dump): Dragon's breath keeps an enemyTarget/attackDir
    // (chosen around the player = P1) -> P2's nearest enemy; Chunkers' orbiting rocks (RotatingProjectiles) are
    // brought back to P2 if the game placed them at P1
    private static readonly HashSet<string> constantNotes = new();
    private static void AimConstant(Slot s, Assets.Scripts.Game.Combat.ConstantAttacks.ConstantAttack ca)
    {
        var breath = ca.TryCast<ProjectileDragonsBreath>();
        if (breath != null)
        {
            var e = NearestEnemyToP2(body.transform.position);
            if (e != null && (e.transform.position - body.transform.position).sqrMagnitude <= breath.range * breath.range * 4f)
            {
                breath.enemyTarget = e;
                Vector3 d = e.transform.position - s.constant.transform.position;
                d.y = 0f;
                if (d.sqrMagnitude > 0.01f) breath.attackDir = d.normalized;
            }
            if (constantNotes.Add("breath")) ModLog.Info($"[weapons] P2's Dragon's breath aims at P2's nearest enemy (range {breath.range:F1})");
        }
        var chunk = ca.TryCast<ChunkersAttack>();
        if (chunk?.rotatingProjectiles != null)
        {
            var rp = chunk.rotatingProjectiles.transform;
            var p1m = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
            if (p1m != null && (rp.position - p1m.position).sqrMagnitude < (rp.position - body.transform.position).sqrMagnitude)
            {
                rp.position = body.transform.position + (rp.position - p1m.position);
                if (constantNotes.Add("chunk")) ModLog.Info("[weapons] P2's Chunkers rocks were at P1: brought to P2 (every frame)");
            }
            else if (constantNotes.Add("chunkok")) ModLog.Info("[weapons] P2's Chunkers rocks orbit P2 on their own");
            // Centred on P2 like P1's ([spinprobe]: P2's rocks circled a point 1.1 m to one side, P1's straight above P1)
            Vector3 rel = rp.position - body.transform.position;
            if (rel.x * rel.x + rel.z * rel.z > 0.0025f) rp.position = body.transform.position + Vector3.up * rel.y;
        }
    }

    // P2 down (revive test log: P2's bananas in the air kept flying and hitting - they're only caught in Tick, which
    // stops while P2 is down): P2's thrown projectiles end now
    public static void OnP2Down()
    {
        int n = 0;
        foreach (var kv in p2BananaObjs)
            if (kv.Value != null && kv.Value.gameObject.activeInHierarchy && p2Bananas.Contains(kv.Key)) { try { kv.Value.ProjectileDone(); n++; } catch { } }
        p2Bananas.Clear();
        p2BananaObjs.Clear();
        if (n > 0) ModLog.Info($"[down] P2's {n} flying banana(s) ended");
    }

    public static void Tick()
    {
        p2Scope = 0;   // safety: a game exception may skip a scope's end
        if (body == null) return;
        WatchAura();
        CatchBananas();
        float now = Time.time;
        bool entered = P2Inventory.EnterP2();   // P2's stats for the cooldowns
        try
        {
            foreach (var s in slots)
            {
                try
                {
                    if (IsConstant(s)) continue;   // always-on: its attached object does the work
                    float cd = Mathf.Max(MinCooldown, s.weapon.GetCooldown());
                    if (now - s.lastFired < cd) continue;
                    s.lastFired = now;
                    Fire(s);
                }
                catch (Exception e)
                {
                    LogOnce("tick " + s.weapon.weaponData.eWeapon, e);
                }
            }
        }
        finally
        {
            if (entered) P2Inventory.Exit();
        }
    }

    // TEMP (user: P2's revolver doesn't fire): per P2 fire, how many projectiles the game set up and how many it
    // actually spawned since the previous fire, with the enemies near each player
    private static int shotSets, shotSpawned, shotLogs;
    private static void LogShots(Slot s)
    {
        if (shotLogs >= 30) return;
        shotLogs++;
        var p1 = MyPlayer.Instance;
        Vector3 p1Pos = p1 != null ? p1.transform.position : Vector3.zero, p2Pos = body.transform.position;
        int near1 = 0, near2 = 0;
        var em = Assets.Scripts.Managers.EnemyManager.Instance;
        if (em?.enemies != null)
            foreach (var kv in em.enemies)
            {
                var e = kv.Value;
                if (e == null || e.hp <= 0f) continue;
                if ((e.transform.position - p1Pos).sqrMagnitude < 30f * 30f) near1++;
                if ((e.transform.position - p2Pos).sqrMagnitude < 30f * 30f) near2++;
            }
        ModLog.Info($"[shots] before P2's {s.weapon.weaponData.eWeapon} fire #{s.fired + 1}: {shotSets} projectiles set up, {shotSpawned} spawned since the last fire; " +
                    $"enemies within 30: near P1 {near1}, near P2 {near2}; players {Vector3.Distance(p1Pos, p2Pos):F0} apart");
        shotSets = shotSpawned = 0;
    }

    private static void Fire(Slot s)
    {
        LogShots(s);
        setAttacksDuringFire = 0;
        firingForP2 = true;
        bool entered = P2Inventory.EnterP2();   // P2's stats for the attack
        try
        {
            s.weapon.Use();
            // If Use() only starts the cooldown and doesn't build an attack, build it the way the game does
            if (setAttacksDuringFire == 0) WeaponUtility.WeaponAttack(s.weapon);
        }
        finally
        {
            if (entered) P2Inventory.Exit();
            firingForP2 = false;
        }
        s.fired++;
        if (s.fired <= 3)
            ModLog.Info($"P2 fired {s.weapon.weaponData.eWeapon} (#{s.fired}): {setAttacksDuringFire} attack(s) set up, " +
                        $"cooldown {s.weapon.GetCooldown():F2} s");
    }

    private static int setAttackLogs;

    // The attack object (and its muzzle flash / cast effect) is set up at P1; only the projectiles were moved, so CL4NK's
    // muzzle flashes and the Fox's cast effect showed on P1 (user). Move the attack object, and its muzzle if that's a
    // separate object, by the P1 -> P2 offset - only while it is still nearer to P1, so it's never moved twice.
    private static int attackMoveLogs;
    private static void MoveAttack(WeaponAttack a)
    {
        if (a == null || body == null) return;
        var pr = MyPlayer.Instance != null ? MyPlayer.Instance.playerRenderer : null;
        var p1Model = pr != null && pr.rendererObject != null ? pr.rendererObject.transform : null;
        if (p1Model == null) return;
        Vector3 offset = P1ToP2();
        if (offset.sqrMagnitude < 0.01f) return;
        bool moved = ShiftIfNearP1(a.transform, p1Model.position, offset);
        var muzzle = a.muzzle;
        if (muzzle != null && !muzzle.transform.IsChildOf(a.transform))
            moved |= ShiftIfNearP1(muzzle.transform, p1Model.position, offset);
        if (moved && attackMoveLogs++ < 3) ModLog.Info($"P2 attack '{a.name}' moved to P2: {a.transform.position}");
    }

    private static bool ShiftIfNearP1(Transform t, Vector3 p1, Vector3 offset)
    {
        Vector3 p2 = p1 + offset;
        if ((t.position - p1).sqrMagnitude >= (t.position - p2).sqrMagnitude) return false;
        t.position += offset;
        return true;
    }

    private static void OnSetAttack(WeaponAttack __instance)
    {
        if (firingForP2)
        {
            setAttacksDuringFire++;
            p2Attacks.Add(__instance.Pointer);
            if (setAttackLogs++ < 3)
                ModLog.Info($"P2 attack '{__instance.name}' set up at {__instance.transform.position} (P1 at {MyPlayer.Instance?.transform.position}, P2 at {body?.transform.position})");
            MoveAttack(__instance);
        }
        else
        {
            p2Attacks.Remove(__instance.Pointer);
        }
    }

    // Move P2's shots from P1's position to P2's (same offset from the character as P1's shots have)
    private static void OnGetProjectilePosition(WeaponAttack __instance, ref Vector3 __result)
    {
        HookSeen("WeaponAttack.GetProjectilePosition");
        if (body == null || !p2Attacks.Contains(__instance.Pointer)) return;
        Vector3 before = __result;
        __result += P1ToP2();
        if (shiftLogs++ < 3) ModLog.Info($"P2 shot position {before} -> {__result}");
    }

    private static void LogOnce(string what, Exception e)
    {
        if (loggedErrors.Add(what + e.Message)) ModLog.Error($"P2Weapons {what} failed: {e}");
    }
}
