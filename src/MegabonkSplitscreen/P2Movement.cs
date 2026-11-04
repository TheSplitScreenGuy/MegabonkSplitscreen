using System.Reflection;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Player.Movement;
using HarmonyLib;
using Rewired;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// P2 moves with the game's own movement code (user: "go the PlayerMovement route"; the mod's own mover never matched P1).
// In the game, PlayerInput reads the controller into an InputState, PlayerMovement.SetBetterInput() takes it and
// PlayerMovement.MovementTick() does one physics step on P1's Rigidbody (speed, jump, gravity, slopes, friction).
//  - P2 gets an inert copy of P1's 'Player' object: everything removed except PlayerMovement, the Rigidbody, the collider
//    and the child points PlayerMovement uses (feet/head/orientation). Built under an inactive holder; after it wakes up,
//    PlayerMovement.Instance ("the player") is pointed back at P1's. ResetState(P2's character) sets it up.
//  - Every physics step: an InputState from P2's pad (left stick, A = jump, held A = holdingJump), SetBetterInput,
//    MovementTick. Its "forward" is P2's camera (playerCam / orientation).
//  - The global events PlayerMovement fires (A_Landed, A_Jumped, Shake, CameraBob, ...) are muted while P2's copy runs -
//    in our MovementTick call, and via small hooks on the Unity callbacks of P2's copy (Update, collisions) - so P1
//    doesn't get P2's fall damage, camera shake or effects. The hooks do nothing for P1's PlayerMovement.
//  - P2's visible body follows the copy (P2Control), animations follow its real velocity and grounded state.
// Config [Experimental] P2GameMovement = false brings back the mod's simple mover.
internal static class P2Movement
{
    public static bool Enabled { get; private set; } = true;
    public static PlayerMovement Pm { get; private set; }
    public static bool Active => Pm != null && Pm.gameObject.activeInHierarchy;

    private static List<PropertyInfo> staticEvents;
    private static int muteDepth;
    private static readonly List<object> mutedValues = new();
    private static bool jumpQueued, aHeld;
    private static int logs;
    private static readonly HashSet<string> loggedErrors = new();

    public static void Apply(Harmony harmony, BepInEx.Configuration.ConfigFile config)
    {
        Enabled = config.Bind("Experimental", "P2GameMovement", true,
            "Player 2 moves with the game's own movement code (same speed/jump as player 1). " +
            "Set to false to use the mod's simple mover.").Value;
        if (!Enabled)
        {
            ModLog.Info("P2Movement: OFF (config), simple mover");
            return;
        }
        staticEvents = typeof(PlayerMovement).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType) && p.GetSetMethod() != null).ToList();

        // Unity calls these on P2's copy by itself: mute the global events while they run for P2's copy
        var pre = new HarmonyMethod(typeof(P2Movement), nameof(MutePrefix));
        var fin = new HarmonyMethod(typeof(P2Movement), nameof(MuteFinalizer));
        foreach (var name in new[] { "Update", "OnCollisionEnter", "OnCollisionStay" })
            foreach (var m in typeof(PlayerMovement).GetMethods().Where(x => x.Name == name && x.DeclaringType == typeof(PlayerMovement)))
                harmony.Patch(m, prefix: pre, finalizer: fin);
        // TEMP (P2 falls through the world): what P2's copy touches
        harmony.Patch(AccessTools.Method(typeof(PlayerMovement), "OnCollisionEnter"),
            prefix: new HarmonyMethod(typeof(P2Movement), nameof(LogContact)));
        ModLog.Info($"P2Movement: ON, {staticEvents.Count} PlayerMovement events muted for P2");
    }

    // ---------- Lifetime ----------

    // Called when P2 takes control (after the entrance), at P2's body position
    public static void Start(Transform body, CharacterData character)
    {
        Stop();
        if (!Enabled) return;
        var p1Pm = PlayerMovement.Instance;
        var p1Model = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
        if (p1Pm == null || p1Model == null || body == null) return;
        try
        {
            var src = p1Pm.gameObject;
            var holder = new GameObject("MegabonkSplitscreen_P2PlayerHolder");
            holder.SetActive(false);
            var copy = Object.Instantiate(src, holder.transform);
            copy.name = "MegabonkSplitscreen_P2Player";
            var pm = copy.GetComponent<PlayerMovement>();

            // Keep only what PlayerMovement uses
            var keep = new HashSet<IntPtr> { copy.transform.Pointer };
            foreach (var t in new[] { pm.feet, pm.head, pm.orientation, pm.playerCam })
                for (var x = t; x != null && x.IsChildOf(copy.transform); x = x.parent) keep.Add(x.Pointer);
            int removedScripts = 0;
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.Pointer == pm.Pointer) continue;
                if (mb.GetIl2CppType().Assembly.GetName().Name != "Assembly-CSharp") continue;
                Object.DestroyImmediate(mb);
                removedScripts++;
            }
            var drop = new List<GameObject>();
            for (int i = 0; i < copy.transform.childCount; i++)
            {
                var child = copy.transform.GetChild(i);
                if (!keep.Contains(child.Pointer)) drop.Add(child.gameObject);
            }
            foreach (var g in drop) Object.DestroyImmediate(g);
            // Nothing visible or audible may stay (e.g. if a kept child carries a camera)
            int removedOther = 0;
            foreach (var c in copy.GetComponentsInChildren<Camera>(true)) { Object.DestroyImmediate(c); removedOther++; }
            foreach (var a in copy.GetComponentsInChildren<AudioListener>(true)) { Object.DestroyImmediate(a); removedOther++; }
            foreach (var a in copy.GetComponentsInChildren<AudioSource>(true)) { Object.DestroyImmediate(a); removedOther++; }
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) { Object.DestroyImmediate(r); removedOther++; }
            if (removedOther > 0) ModLog.Info($"P2 movement copy: removed {removedOther} camera/audio/renderer components");

            // Same place as P2's body (the body is P1's model offset away from the player object)
            Vector3 rootOffset = MyPlayer.Instance.transform.position - p1Model.position;
            copy.transform.SetParent(null, true);
            copy.transform.SetPositionAndRotation(body.position + rootOffset, src.transform.rotation);
            // v0.23.10: the copy's scale was (0,0,0) -> a zero-size capsule that touches nothing, so P2 fell through the
            // world. P1's root was copied mid-entrance; use P1's current scale if it's normal, else 1.
            var p1Scale = src.transform.localScale;
            ModLog.Info($"[move] P1 root scale when copied: {p1Scale}");
            copy.transform.localScale = p1Scale.x > 0.5f && p1Scale.y > 0.5f && p1Scale.z > 0.5f ? p1Scale : Vector3.one;
            Object.Destroy(holder);

            copy.SetActive(true);   // Awake runs here and claims PlayerMovement.Instance...
            PlayerMovement.Instance = p1Pm;   // ...which goes straight back to P1
            Pm = pm;
            ModLog.Info($"[move] scale after SetActive: {copy.transform.localScale}, playerScale {pm.playerScale}");   // TEMP

            if (P2Camera.Cam != null) pm.playerCam = P2Camera.Cam.transform;
            // P1 and P2 don't push each other
            var mine = copy.GetComponentsInChildren<Collider>(true);
            foreach (var p1Col in src.GetComponentsInChildren<Collider>(true))
                foreach (var c in mine)
                    if (p1Col != null && c != null) Physics.IgnoreCollision(p1Col, c, true);

            // Set-up like the game would (v0.23.0: ResetState threw a NullReferenceException on the bare copy and the
            // whole thing fell back to the old mover): Initialize, movement values for P2's character, then ResetState.
            // Each step may fail on its own without cancelling the copy.
            var eChar = character != null ? character.eCharacter : p1Pm.currentCharacter;
            // The copy's own physics parts: PlayerMovement's rb / playerCollider are set at runtime, so Instantiate left
            // them empty (v0.23.1: "rb False, collider False" -> every step failed and P2 couldn't move)
            pm.rb = copy.GetComponent<Rigidbody>();
            pm.playerCollider = copy.GetComponent<CapsuleCollider>();
            if (pm.rb != null)
            {
                pm.rb.isKinematic = false;
                pm.rb.detectCollisions = true;
                pm.rb.velocity = Vector3.zero;
            }
            // v0.23.2: P2 fell through the world. Make the collider solid and log both players' physics set-up.
            var p1Capsule = src.GetComponent<CapsuleCollider>();
            var p1Rb = src.GetComponent<Rigidbody>();
            if (pm.playerCollider != null)
            {
                pm.playerCollider.enabled = true;
                pm.playerCollider.isTrigger = false;
            }
            copy.layer = src.layer;
            // v0.23.4: the layer matrix says Player and Ground don't collide, yet P1 stands on the ground. P1 gets it from
            // the per-object include/exclude layers, which aren't on the copy -> take P1's.
            ModLog.Info($"[move] P1 layers: collider include {p1Capsule?.includeLayers.value} exclude {p1Capsule?.excludeLayers.value}, " +
                        $"rb include {p1Rb?.includeLayers.value} exclude {p1Rb?.excludeLayers.value}; " +
                        $"P2 before: collider include {pm.playerCollider?.includeLayers.value} exclude {pm.playerCollider?.excludeLayers.value}, " +
                        $"rb include {pm.rb?.includeLayers.value} exclude {pm.rb?.excludeLayers.value}");
            if (p1Capsule != null && pm.playerCollider != null)
            {
                pm.playerCollider.includeLayers = p1Capsule.includeLayers;
                pm.playerCollider.excludeLayers = p1Capsule.excludeLayers;
                pm.playerCollider.layerOverridePriority = p1Capsule.layerOverridePriority;
            }
            if (p1Rb != null && pm.rb != null)
            {
                pm.rb.includeLayers = p1Rb.includeLayers;
                pm.rb.excludeLayers = p1Rb.excludeLayers;
            }
            // v0.23.5: P1's include/exclude layers are all 0 too, and P2 still fell. Force P2's capsule to contact the
            // ground layers (includeLayers overrides the layer matrix).
            int groundMask = p1Pm.whatIsGround.value | p1Pm.whatIsGroundOnly.value;
            int groundIdx = LayerMask.NameToLayer("Ground");
            if (groundIdx >= 0) groundMask |= 1 << groundIdx;
            if (pm.playerCollider != null)
            {
                pm.playerCollider.includeLayers = groundMask;
                pm.playerCollider.layerOverridePriority = 10;
            }
            if (pm.rb != null) pm.rb.includeLayers = groundMask;
            ModLog.Info($"[move] forced P2 ground mask {groundMask} (Ground layer index {groundIdx}, whatIsGroundOnly {p1Pm.whatIsGroundOnly.value})");
            // v0.23.6: a ray under P2's copy missed the copy's own capsule, while a ray under P1 hit it -> the physics shape
            // may not be where the transform is. Log both, then rebuild the shape at the transform.
            ModLog.Info($"[move] before sync: transform {copy.transform.position}, rb {pm.rb?.position}, collider centre {pm.playerCollider?.bounds.center}, " +
                        $"scale {copy.transform.lossyScale}, P1 at {src.transform.position}");
            Physics.SyncTransforms();
            if (pm.playerCollider != null) { pm.playerCollider.enabled = false; pm.playerCollider.enabled = true; }
            if (pm.rb != null) { pm.rb.position = copy.transform.position; pm.rb.rotation = copy.transform.rotation; }
            Physics.SyncTransforms();
            ModLog.Info($"[move] after sync: transform {copy.transform.position}, rb {pm.rb?.position}, collider centre {pm.playerCollider?.bounds.center}, " +
                        $"attached rb is the copy's {(pm.playerCollider?.attachedRigidbody != null && pm.playerCollider.attachedRigidbody.Pointer == pm.rb?.Pointer)}");
            // The enable toggle can drop ignore pairs: P1 and P2 still mustn't push each other
            foreach (var p1Col in src.GetComponentsInChildren<Collider>(true))
                foreach (var c in copy.GetComponentsInChildren<Collider>(true))
                    if (p1Col != null && c != null) Physics.IgnoreCollision(p1Col, c, true);
            // v0.23.7: the shape is at the transform now and P2 still touches nothing (not even P2's old body, inside it).
            // Per-pair rules and a test sweep, P1 vs P2.
            Collider ground = null;
            if (Physics.Raycast(src.transform.position + Vector3.up, Vector3.down, out var gHit, 30f, 1 << 8, QueryTriggerInteraction.Ignore))
                ground = gHit.collider;
            var oldBody = body != null ? body.GetComponentInChildren<Collider>() : null;
            string Pair(Collider a, Collider b) => a == null || b == null ? "n/a" : Physics.GetIgnoreCollision(a, b).ToString();
            bool p1Sweep = p1Rb != null && p1Rb.SweepTest(Vector3.down, out var s1, 5f, QueryTriggerInteraction.Ignore);
            bool p2Sweep = pm.rb != null && pm.rb.SweepTest(Vector3.down, out var s2, 5f, QueryTriggerInteraction.Ignore);
            ModLog.Info($"[move] pair ignores: P1-ground {Pair(p1Capsule, ground)}, P2-ground {Pair(pm.playerCollider, ground)}, P2-oldBody {Pair(pm.playerCollider, oldBody)}; " +
                        $"matrix sanity Default-Default {Physics.GetIgnoreLayerCollision(0, 0)}, Ground-Player {Physics.GetIgnoreLayerCollision(8, 7)}; " +
                        $"sweep down hits: P1 {p1Sweep}, P2 {p2Sweep}; priorities P1 {p1Capsule?.layerOverridePriority}, ground {ground?.layerOverridePriority} " +
                        $"(ground include {ground?.includeLayers.value} exclude {ground?.excludeLayers.value}, rb {ground?.attachedRigidbody != null})");
            // TEMP control experiment: do fresh rigidbodies (Player layer / Default layer) land here?
            testBodies.Clear();
            foreach (int layer in new[] { src.layer, 0 })
            {
                var t = new GameObject($"MegabonkSplitscreen_FallTest_{LayerMask.LayerToName(layer)}");
                t.layer = layer;
                t.transform.position = copy.transform.position + copy.transform.right * (layer == 0 ? 4f : 2f) + Vector3.up;
                var cap = t.AddComponent<CapsuleCollider>();
                cap.radius = 0.5f; cap.height = 2f;
                var trb = t.AddComponent<Rigidbody>();
                trb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                trb.constraints = RigidbodyConstraints.FreezeRotation;
                testBodies.Add((t, t.transform.position.y));
            }
            testCheckAt = Time.time + 3f;
            // What P1 itself stands on, and its physics state, for comparison
            var p1Below = Physics.RaycastAll(src.transform.position + Vector3.up * 3f, Vector3.down, 30f, -1, QueryTriggerInteraction.Ignore)
                .OrderBy(h => h.distance)
                .Select(h => $"'{h.collider.name}' {h.distance - 3f:F2} (layer {h.collider.gameObject.layer} {LayerMask.LayerToName(h.collider.gameObject.layer)}, {h.collider.GetIl2CppType().Name})");
            ModLog.Info($"[move] under P1: {string.Join(", ", p1Below)}; P1 vel {p1Rb?.velocity}, grounded {p1Pm.grounded}, " +
                        $"constraints {p1Rb?.constraints}, P1 layer {src.layer}; matrix ignores for layer {src.layer}: " +
                        string.Join(",", Enumerable.Range(0, 32).Where(l => Physics.GetIgnoreLayerCollision(src.layer, l))));
            // P1's object may sit on another layer than its root reports at runtime; log every P1 collider's layer
            ModLog.Info($"[move] P1 colliders: {string.Join(", ", src.GetComponentsInChildren<Collider>(true).Select(c => $"{c.name} layer {LayerMask.LayerToName(c.gameObject.layer)}"))}");
            ModLog.Info($"[move] P1 collider {DescribeCol(p1Capsule)}, rb {DescribeRb(p1Rb)}, layer {LayerMask.LayerToName(src.layer)}");
            ModLog.Info($"[move] P2 collider {DescribeCol(pm.playerCollider)}, rb {DescribeRb(pm.rb)}, layer {LayerMask.LayerToName(copy.layer)}");
            var others = copy.GetComponentsInChildren<Collider>(true);
            ModLog.Info($"[move] P2 copy colliders: {string.Join(", ", others.Select(c => $"{c.name}:{c.GetIl2CppType().Name}{(c.enabled ? "" : "(off)")}{(c.isTrigger ? "(trigger)" : "")}"))}; " +
                        $"P1 has {src.GetComponentsInChildren<Collider>(true).Length}");
            var below = Physics.RaycastAll(copy.transform.position + Vector3.up * 3f, Vector3.down, 30f, -1, QueryTriggerInteraction.Ignore)
                .OrderBy(h => h.distance)
                .Select(h => $"'{h.collider.name}' {h.distance - 3f:F2} (layer {LayerMask.LayerToName(h.collider.gameObject.layer)})");
            ModLog.Info($"[move] everything under P2's copy: {string.Join(", ", below)}");
            // What P2's capsule overlaps at spawn (a solid P1 part here would shove P2 away)
            var touching = Physics.OverlapSphere(copy.transform.position, 2.5f, -1, QueryTriggerInteraction.Ignore)
                .Where(c => c != null && c.transform.root != copy.transform)
                .Select(c => $"'{c.name}' (root '{c.transform.root.name}', layer {LayerMask.LayerToName(c.gameObject.layer)})");
            ModLog.Info($"[move] solid colliders near P2's spawn: {string.Join(", ", touching)}");
            // Re-apply after the collider changes above, in case they reset the ignore pairs
            foreach (var p1Col in src.GetComponentsInChildren<Collider>(true))
                foreach (var c in copy.GetComponentsInChildren<Collider>(true))
                    if (p1Col != null && c != null) Physics.IgnoreCollision(p1Col, c, true);
            int playerLayer = src.layer, groundLayer = LayerMask.NameToLayer("Ground");
            ModLog.Info($"[move] layer rules: Player-Ground collide {(groundLayer >= 0 ? (!Physics.GetIgnoreLayerCollision(playerLayer, groundLayer)).ToString() : "no Ground layer")}; " +
                        $"P1's whatIsGround {p1Pm.whatIsGround.value}, P2's {pm.whatIsGround.value}");
            spawnPoint = copy.transform.position;
            // v0.23.8: a fresh capsule rigidbody on the Player layer lands right here, the copied one never touches anything.
            // Swap the copied physics parts for brand-new ones set up like P1's.
            ReplacePhysics(copy, pm, p1Capsule, p1Rb);
            foreach (var p1Col in src.GetComponentsInChildren<Collider>(true))
                foreach (var c in copy.GetComponentsInChildren<Collider>(true))
                    if (p1Col != null && c != null) Physics.IgnoreCollision(p1Col, c, true);
            // v0.26.1 super jumps (always take-off 37.3 instead of ~17, height 22): P2's visible body has its own collider
            // (kinematic, for enemy contact) and is teleported onto this copy every frame; after a jump it landed inside
            // the copy and the depenetration launched the copy. P2's two bodies ignore each other now.
            int bodyPairs = 0;
            foreach (var bc in body.GetComponentsInChildren<Collider>(true))
                foreach (var c in copy.GetComponentsInChildren<Collider>(true))
                    if (bc != null && c != null) { Physics.IgnoreCollision(bc, c, true); bodyPairs++; }
            ModLog.Info($"[move] P2's body and movement copy ignore each other ({bodyPairs} collider pairs)");
            // Always: the "initialized" flag was copied from P1 and skipped it
            Step("Initialize", () => pm.Initialize());
            if (pm.movementValues == null)
                Step("movement values", () =>
                {
                    var mv = new PlayerMovementValues();
                    mv.CreateMovement(pm.rb, eChar);
                    pm.movementValues = mv;
                });
            // v0.23.12: ResetState sets the scale from playerScale, which the game fills in at runtime (0 on the copy)
            if (p1Pm.playerScale.y > 0.5f)
            {
                pm.playerScale = p1Pm.playerScale;
                pm.crouchScale = p1Pm.crouchScale;
            }
            Step("ResetState", () => pm.ResetState(eChar, body.forward));
            PlayerMovement.Instance = p1Pm;
            // The game's set-up may switch collisions off (P1's portal turns them back on; P2 has no portal script)
            Step("physics on", () =>
            {
                pm.isNoclipping = false;
                pm.Freeze(false);
                pm.SetKinematic(false);
                if (pm.rb != null) { pm.rb.isKinematic = false; pm.rb.detectCollisions = true; pm.rb.useGravity = true; }
                if (pm.playerCollider != null) { pm.playerCollider.enabled = true; pm.playerCollider.isTrigger = false; }
            });
            // PlayerMovement keeps its own standing scale (crouching rescales the player); make sure it isn't zero either
            // v0.23.11: P1's scale was 1 when copied, but the copy's scale, playerScale and crouchScale were all 0 after
            // set-up -> take P1's values outright
            if (p1Pm.playerScale.y > 0.5f)
            {
                pm.playerScale = p1Pm.playerScale;
                pm.crouchScale = p1Pm.crouchScale;
            }
            else pm.playerScale = pm.crouchScale = Vector3.one;
            copy.transform.localScale = pm.playerScale;
            goodScale = pm.playerScale;
            ModLog.Info($"[move] scale after set-up: {copy.transform.localScale}, playerScale {pm.playerScale}, crouchScale {pm.crouchScale} (P1's {p1Pm.playerScale} / {p1Pm.crouchScale})");
            ModLog.Info($"[move] after set-up: rb is {(pm.rb != null ? (pm.rb.Pointer == copy.GetComponent<Rigidbody>()?.Pointer ? "the new one" : "SOMETHING ELSE") : "null")}, " +
                        $"collider is {(pm.playerCollider != null ? (pm.playerCollider.Pointer == copy.GetComponent<CapsuleCollider>()?.Pointer ? "the new one" : "SOMETHING ELSE") : "null")}, " +
                        $"layer {LayerMask.LayerToName(copy.layer)}");
            ModLog.Info($"P2 movement copy parts: rb {pm.rb != null}, collider {pm.playerCollider != null}, feet {pm.feet != null}, " +
                        $"head {pm.head != null}, orientation {pm.orientation != null}, playerCam {pm.playerCam != null}, " +
                        $"movement values {pm.movementValues != null} (max run {pm.movementValues?.maxRunSpeed:F2} vs P1 {p1Pm.movementValues?.maxRunSpeed:F2}), " +
                        $"initialized {SafeBool(pm.IsInitialized)}");
            ModLog.Info($"P2 movement = game's PlayerMovement copy ({removedScripts} scripts and {drop.Count} child objects removed), " +
                        $"character {(character != null ? character.eCharacter.ToString() : "P1's")}, at {copy.transform.position}");
        }
        catch (Exception e)
        {
            ModLog.Error($"P2 movement setup failed, using the simple mover: {e}");
            Stop();
        }
    }

    private static Vector3 spawnPoint;
    private static int rescueLogs;

    private static string DescribeCol(CapsuleCollider c) => c == null ? "none" :
        $"(enabled {c.enabled}, trigger {c.isTrigger}, r {c.radius:F2}, h {c.height:F2}, centre {c.center})";
    private static string DescribeRb(Rigidbody r) => r == null ? "none" :
        $"(kinematic {r.isKinematic}, gravity {r.useGravity}, collisions {r.detectCollisions}, mass {r.mass:F1}, interp {r.interpolation}, cd {r.collisionDetectionMode})";

    private static void Step(string what, Action a)
    {
        try { Muted(a); }
        catch (Exception e) { ModLog.Warn($"P2 movement set-up step '{what}' failed: {e.Message.Split('\n')[0]}"); }
        if (Pm != null) ModLog.Info($"[move] scale after step '{what}': {Pm.transform.localScale}, playerScale {Pm.playerScale}");   // TEMP
    }

    private static string SafeBool(Func<bool> f)
    {
        try { return f().ToString(); }
        catch { return "?"; }
    }

    public static void Stop()
    {
        if (Pm != null) Object.Destroy(Pm.gameObject);
        Pm = null;
        jumpQueued = false;
    }

    // ---------- Input (every frame) + physics step (every FixedUpdate) ----------

    // Called every frame by P2Control: remember jump presses so a short tap isn't missed between physics steps
    public static void ReadInput(bool aDown)
    {
        if (waitRelease)
        {
            if (aDown) return;
            waitRelease = false;
        }
        if (aDown && !aHeld) jumpQueued = true;
        aHeld = aDown;
    }

    // 1.0.2 (players: "when selecting powerups, p2 always jumps after selection"): P2's input isn't read while the game
    // is paused, so the A that picked the offer looked like a new press the moment the screen closed. While paused A is
    // only followed (no jump), and after P2's menu gives the controls back a held A must be let go before it jumps.
    public static void FollowA(bool aDown)
    {
        if (!JumpReleaseFix) return;
        aHeld = aDown;
        jumpQueued = false;
    }
    public static void WaitForJumpRelease()
    {
        if (!JumpReleaseFix) return;
        waitRelease = true;
        jumpQueued = false;
        aHeld = false;
    }
    private static bool waitRelease;
    public static bool JumpReleaseFix = true;   // TEMP autotest switch ('fix jumprelease off')

    // On while no wall-run button is bound (P1's PlayerInput.hasWallrunActionBound), like the game does for P1
    private static bool WallrunHeld()
    {
        var input = Assets.Scripts.Actors.Player.MyPlayer.Instance?.playerInput;
        return input == null || !input.hasWallrunActionBound;
    }

    // Called from Ticker.FixedUpdate
    public static void FixedTick()
    {
        CheckFallTest();
        if (!Active || P2Health.IsDead) return;
        // The game's own pause (menus) may leave Unity's physics running: P2 holds still like P1 until it ends
        if (Assets.Scripts.Utility.MyTime.paused)
        {
            if (!heldForPause && Pm?.rb != null)
            {
                heldForPause = true;
                heldVelocity = Pm.rb.velocity;
                Pm.rb.isKinematic = true;
            }
            return;
        }
        if (heldForPause)
        {
            heldForPause = false;
            if (Pm?.rb != null)
            {
                Pm.rb.isKinematic = false;
                Pm.rb.velocity = heldVelocity;
            }
        }
        var joy = PlayerJoin.P2Joystick;
        if (joy == null && !PlayerJoin.VirtualP2) return;   // (autotest: P2 without a pad, script input)
        try
        {
            Vector2 stick = P2Control.StickNow;
            if (stick.magnitude < 0.2f) stick = Vector2.zero;
            var input = new InputState
            {
                moveHorizontal = stick.x,
                moveVertical = stick.y,
                rotationHorizontal = P2Camera.Yaw,
                rotationVertical = P2Camera.Pitch,
                jumping = jumpQueued,
                holdingJump = aHeld,
                crouching = SlideHeld(),   // 1.0 alpha (user: the player can slide - P2 couldn't): P2's Slide binding
                // v0.40.3 (autotest: P2 Monkey stopped at the wall; P1's input had holdingWallrun True all along - the
                // game holds it on by itself while no wall-run button is bound): P2's follows the same rule
                holdingWallrun = WallrunHeld(),
            };
            jumpQueued = false;
            if (Pm.orientation != null) Pm.orientation.rotation = Quaternion.Euler(0f, P2Camera.Yaw, 0f);
            if (P2Camera.Cam != null && Pm.playerCam != P2Camera.Cam.transform) Pm.playerCam = P2Camera.Cam.transform;
            var p1Pm = PlayerMovement.Instance;
            // TEMP bisect: the first 100 ticks (~2 s) without the game's script on P2's copy -> does it land on its own?
            bool bisect = false;   // v0.23.10 bisect done (the cause was the copy's zero scale)
            // Something zeroes the copy's scale (v0.23.11): keep it at the standing scale and say when it happens
            if (Pm.transform.localScale.y < 0.05f && goodScale.y > 0.5f)
            {
                if (scaleFixes++ < 5) ModLog.Warn($"[move] P2's copy scale was {Pm.transform.localScale} at tick {logs}, playerScale {Pm.playerScale} -> restored");
                Pm.transform.localScale = goodScale;
                if (Pm.playerScale.y < 0.5f) Pm.playerScale = goodScale;
            }
            if (Pm.enabled == bisect) Pm.enabled = !bisect;
            // v0.26.4 [launch] log: the "super jumps" were the game's unstuck push - P2's stuckTimer counted up while P2
            // stood still (P1's stays 0), at 1.0 unstuckForce 2000 (/ mass 60 = ~33 up) launched P2. Off for P2.
            Pm.stuckTimer = 0f;
            // In P2's stat context: the movement code reads movement speed, jump height, extra jumps... from the
            // stats - P2's tomes (user: every tome effect should work for P2)
            bool entered = P2Inventory.EnterP2();
            try
            {
                if (!bisect)
                    Muted(() =>
                    {
                        Pm.SetBetterInput(input);
                        Pm.MovementTick();
                    });
            }
            finally { if (entered) P2Inventory.Exit(); }
            Pm.stuckTimer = 0f;
            if ((logs >= 100 && logs < 140) || logs % 10 == 0 && logs < 100)
                ModLog.Info($"[move] state tick {logs}: y {Pm.transform.position.y:F2}, vel y {Velocity().y:F2}, layer {Pm.gameObject.layer}, " +
                            $"col exclude {Pm.playerCollider?.excludeLayers.value} include {Pm.playerCollider?.includeLayers.value}, " +
                            $"rb exclude {Pm.rb?.excludeLayers.value}, col enabled {Pm.playerCollider?.enabled}, trigger {Pm.playerCollider?.isTrigger}, " +
                            $"kinematic {Pm.rb?.isKinematic}, detect {Pm.rb?.detectCollisions}, noclip {Pm.isNoclipping}, scale {Pm.transform.localScale}");
            if (PlayerMovement.Instance != p1Pm) PlayerMovement.Instance = p1Pm;
            TrackJumps(p1Pm, input.jumping);
            if (logs < 30 || (logs % 250 == 50 && logs < 2000))   // TEMP: first 30 ticks, to see where the fall starts
                ModLog.Info($"P2 movement running (tick {logs}): pos {Pm.transform.position}, vel {Velocity()}, grounded {Pm.grounded}; P1 y {p1Pm.transform.position.y:F2}, vel y {(p1Pm.rb != null ? p1Pm.rb.velocity.y : 0f):F2}; P2 rb {Pm.rb?.position}, collider centre {Pm.playerCollider?.bounds.center}");
            logs++;

            // Last resort only (user, v0.26.4: walking off a cliff P2 kept getting pulled up - this net used to trigger at
            // 30 below P1 and put P2 back at P1's height; the game's own movement code handles falling and the void).
            // Far below anything playable: next to P1.
            var p1 = MyPlayer.Instance;
            if (p1 != null && Pm.transform.position.y < p1.transform.position.y - 400f)
            {
                var back = p1.transform.position + Vector3.up;
                Pm.transform.position = back;
                if (Pm.rb != null)
                {
                    Pm.rb.position = back;
                    Pm.rb.velocity = Vector3.zero;
                }
                if (rescueLogs++ < 5) ModLog.Warn($"[move] P2 fell through the world, put back at {back}");
            }
        }
        catch (Exception e)
        {
            if (loggedErrors.Add(e.Message)) ModLog.Error($"P2 movement tick failed: {e}");
        }
    }

    // Where P2's visible body goes (feet position like P1's model), and what P2Control's animations need
    public static Vector3 BodyPosition()
    {
        var p1 = MyPlayer.Instance;
        var p1Model = p1?.playerRenderer?.rendererObject?.transform;
        Vector3 rootOffset = p1 != null && p1Model != null ? p1.transform.position - p1Model.position : Vector3.zero;
        return Pm.transform.position - rootOffset;
    }

    public static Vector3 Velocity() => Pm?.rb != null ? Pm.rb.velocity : Vector3.zero;
    public static bool Grounded() => Pm != null && Pm.grounded;

    // A push through the game's own PlayerMovement.PushPlayer on P2's copy (explosions), its events muted like the tick
    public static float LastPushAt { get; private set; } = -100f;

    // P2's Slide buttons: every element the game's map for P2's pad puts the Slide action on (B and the left bumper -
    // PadBindings); tests: "slide"
    private static bool SlideHeld()
    {
        if (Autotest.Active) return Autotest.P2Held(new[] { "slide" });
        var joy = PlayerJoin.P2Joystick;
        return joy != null && PadBindings.HeldAny(joy, PadBindings.AllFor(joy, MyInputManager.Slide));
    }

    public static void Push(Vector3 force)
    {
        if (Pm == null) return;
        LastPushAt = Time.time;
        var p1Pm = PlayerMovement.Instance;
        // P2's copy is "the" player during the push: the game's push code may go through PlayerMovement.Instance
        // (user: explosions at P2 pushed P1 back)
        try
        {
            PlayerMovement.Instance = Pm;
            Muted(() => Pm.PushPlayer(force));
        }
        finally { if (p1Pm != null) PlayerMovement.Instance = p1Pm; }
    }
    // The ground's slope under P2 (the game's own value), straight up in the air
    public static Vector3 GroundNormal() => Pm != null && Pm.grounded && Pm.normalVector.sqrMagnitude > 0.5f ? Pm.normalVector.normalized : Vector3.up;

    // ---------- Muting P1-facing events while P2's copy runs ----------

    private static void Muted(Action a)
    {
        MuteOn();
        try { a(); }
        finally { MuteOff(); }
    }

    private static void MuteOn()
    {
        if (muteDepth++ > 0 || staticEvents == null) return;
        mutedValues.Clear();
        foreach (var p in staticEvents)
        {
            object v = null;
            // P2's passive's own listeners (P2Abilities) hear P2's movement events
            // + P2's jump / landing dust (P2Dust)
            try { v = p.GetValue(null); p.SetValue(null, P2Dust.With(p, P2Abilities.RoutedFor(p))); } catch { }
            mutedValues.Add(v);
        }
        abilitiesEntered = P2Abilities.Enter();
    }
    private static bool abilitiesEntered;

    private static void MuteOff()
    {
        if (muteDepth == 0 || --muteDepth > 0 || staticEvents == null) return;
        for (int i = 0; i < staticEvents.Count && i < mutedValues.Count; i++)
        {
            try { staticEvents[i].SetValue(null, mutedValues[i]); } catch { }
        }
        mutedValues.Clear();
        if (abilitiesEntered) { abilitiesEntered = false; P2Abilities.Exit(); }
    }

    // Brand-new Rigidbody + CapsuleCollider on the copy, with P1's settings (the copied ones never made contact)
    private static void ReplacePhysics(GameObject copy, PlayerMovement pm, CapsuleCollider p1Cap, Rigidbody p1Rb)
    {
        var oldCol = copy.GetComponent<CapsuleCollider>();
        var oldRb = copy.GetComponent<Rigidbody>();
        if (oldCol != null) Object.DestroyImmediate(oldCol);
        if (oldRb != null) Object.DestroyImmediate(oldRb);

        var rb = copy.AddComponent<Rigidbody>();
        var cap = copy.AddComponent<CapsuleCollider>();
        if (p1Cap != null)
        {
            cap.radius = p1Cap.radius;
            cap.height = p1Cap.height;
            cap.center = p1Cap.center;
            cap.direction = p1Cap.direction;
            cap.sharedMaterial = p1Cap.sharedMaterial;
        }
        if (p1Rb != null)
        {
            rb.mass = p1Rb.mass;
            rb.drag = p1Rb.drag;
            rb.angularDrag = p1Rb.angularDrag;
            rb.interpolation = p1Rb.interpolation;
            rb.collisionDetectionMode = p1Rb.collisionDetectionMode;
            rb.constraints = p1Rb.constraints;
        }
        rb.useGravity = true;
        rb.isKinematic = false;
        rb.position = copy.transform.position;
        pm.rb = rb;
        pm.playerCollider = cap;
        Physics.SyncTransforms();
        ModLog.Info($"[move] new physics parts on P2's copy: capsule r {cap.radius:F2} h {cap.height:F2}, material '{cap.sharedMaterial?.name}', " +
                    $"rb mass {rb.mass:F1} drag {rb.drag:F2} constraints {rb.constraints}");
    }

    // TEMP (user: P2 sometimes does a super jump): every P2 jump's take-off speed and height, and how the game feeds
    // P1's jump input (one-tick pulse or held), to compare
    private static bool p2Air, p2SentJump;
    private static float p2TakeoffY, p2ApexY, p2TakeoffVy;
    private static int p2JumpLogs, p1PulseTicks, p1PulseLogs;
    private static bool p1WasGroundedAtPress;
    private static int p2Trace, p1Trace, traceLogs, p1TraceLogs, launchLogs, stuckLogs;
    private static float lastVy, lastStuckTimer, p2MinVy;
    private static int landLogs;

    private static void LandingDamage(float speed)
    {
        var ph = MyPlayer.Instance?.inventory?.playerHealth;
        float min = ph != null && ph.minFallDamageSpeed > 0f ? ph.minFallDamageSpeed : 30f;
        float max = ph != null && ph.maxFallDamageSpeed > min ? ph.maxFallDamageSpeed : 140f;
        if (speed <= min || P2Health.IsDead) return;
        // The game's own fall-damage rule when P2's health runs on the game's code (else the measured formula below)
        if (P2Health.GameLanded(speed)) { if (landLogs++ < 15) ModLog.Info($"[fall] P2 landed at speed {speed:F1} (game's rule)"); return; }
        float reduction = Mathf.Clamp01(P2Inventory.Get(Assets.Scripts.Menu.Shop.EStat.FallDamageReduction));
        int dmg = Mathf.RoundToInt(100f * Mathf.Clamp01((speed - min) / (max - min)) * (1f - reduction));
        if (landLogs++ < 15) ModLog.Info($"[fall] P2 landed at speed {speed:F1}: {dmg} damage (limits {min:F0}-{max:F0}, reduction {reduction:F2})");
        if (dmg > 0) P2Health.TakeDamage(dmg, "fall");
    }
    private static int fallLogs;

    private static void TrackJumps(PlayerMovement p1Pm, bool sentJump)
    {
        if (Pm == null) return;
        float y = Pm.transform.position.y;
        bool air = !Pm.grounded;
        if (sentJump) p2SentJump = true;
        // TEMP v0.26.4 (user: the super jumps happen without pressing anything; the game never got a jump press):
        // catch the launch - vertical speed up by > 15 in one tick - with the game's "unstuck" values before/after
        float vyNow = Velocity().y;
        if (vyNow - lastVy > 15f && launchLogs < 8)
        {
            launchLogs++;
            ModLog.Info($"[launch] P2 launched: vy {lastVy:F1} -> {vyNow:F1} at y {y:F2}, jump input {sentJump}, grounded {Pm.grounded}; " +
                        $"stuckTimer {lastStuckTimer:F2} -> {Pm.stuckTimer:F2} (max {Pm.stuckTimerMax:F2}), unstuckForce {Pm.unstuckForce:F1}, " +
                        $"pushed {Pm.pushed}, onLadder {Pm.onLadder}, wallClimbing {Pm.wallClimbing}, isDashing {Pm.isDashing}, " +
                        $"lastFallSpeed {Pm.lastFallSpeed:F1}, onRamp {Pm.onRamp}, pushMultiplier {Pm.pushMultiplier:F2}, P1 stuckTimer {(p1Pm != null ? p1Pm.stuckTimer : 0f):F2}");
        }
        lastVy = vyNow;
        lastStuckTimer = Pm.stuckTimer;
        // Also: the stuck timer itself, a few times while it runs (P2 and P1)
        if (Pm.stuckTimer > 0.01f && stuckLogs < 10 && Time.frameCount % 30 == 0)
        {
            stuckLogs++;
            ModLog.Info($"[launch] P2 stuck timer {Pm.stuckTimer:F2}/{Pm.stuckTimerMax:F2}, vel {Velocity()}, grounded {Pm.grounded}, input move ({P2Control.StickNow.x:F2}, {P2Control.StickNow.y:F2}); " +
                        $"P1 stuck timer {(p1Pm != null ? p1Pm.stuckTimer : 0f):F2}");
        }
        // TEMP v0.26.3: the first 12 physics ticks of a jump, P2 and P1, to see where P2's extra ~24 upward comes from
        if (sentJump && traceLogs < 6) { p2Trace = 12; traceLogs++; }
        if (p2Trace > 0)
        {
            ModLog.Info($"[jump] P2 tick {12 - p2Trace}: y {y:F2} vy {Velocity().y:F2}, grounded {Pm.grounded}, input jumping {sentJump} holding {aHeld}, " +
                        $"readyToJump {Pm.readyToJump} usedJumps {Pm.usedJumps} jumping {Pm.jumping}, dt {Time.fixedDeltaTime:F4}");
            p2Trace--;
        }
        if (p1Pm != null)
        {
            if (p1Pm.inputState.jumping && p1PulseTicks == 0 && p1TraceLogs < 4) { p1Trace = 12; p1TraceLogs++; }
            if (p1Trace > 0)
            {
                ModLog.Info($"[jump] P1 tick {12 - p1Trace}: y {p1Pm.transform.position.y:F2} vy {(p1Pm.rb != null ? p1Pm.rb.velocity.y : 0f):F2}, grounded {p1Pm.grounded}, " +
                            $"input jumping {p1Pm.inputState.jumping} holding {p1Pm.inputState.holdingJump}, readyToJump {p1Pm.readyToJump} usedJumps {p1Pm.usedJumps}");
                p1Trace--;
            }
        }
        if (air && !p2Air)
        {
            p2TakeoffY = p2ApexY = y;
            p2TakeoffVy = Velocity().y;
            p2MinVy = 0f;
        }
        if (air)
        {
            p2ApexY = Mathf.Max(p2ApexY, y);
            p2MinVy = Mathf.Min(p2MinVy, Velocity().y);
        }
        // Fall damage (user: P2 took none). The game's rule, measured on P1 (v0.32.x [fall] log): nothing below
        // PlayerHealth.minFallDamageSpeed (30), then 0..100 damage up to maxFallDamageSpeed (140) - speed 42.7 cost
        // P1 12 HP (11.5 -> 12), independent of max HP. P2's own FallDamageReduction applies.
        if (!air && p2Air) LandingDamage(-p2MinVy);
        if (!air && p2Air && p2JumpLogs < 25 && p2ApexY - p2TakeoffY > 0.5f)
        {
            p2JumpLogs++;
            ModLog.Info($"[jump] P2 jump: height {p2ApexY - p2TakeoffY:F2}, take-off vy {p2TakeoffVy:F1}, jump input sent {p2SentJump}, " +
                        $"aerial jumps {Pm.aerialJumps}, used jumps {Pm.usedJumps}, on ramp {Pm.onRamp}, surface {Pm.surface}");
        }
        // TEMP [fall]: P2's landing speeds (the game's fall damage listens to A_Landed, muted for P2's copy)
        if (!air && p2Air && fallLogs < 25 && Mathf.Abs(Pm.lastFallSpeed) > 10f)
        {
            fallLogs++;
            ModLog.Info($"[fall] P2 landed: lastFallSpeed {Pm.lastFallSpeed:F1}, fallSpeed {Pm.fallSpeed:F1}");
        }
        if (!air) p2SentJump = false;
        p2Air = air;

        // P1: how many ticks the game's input says "jumping"
        if (p1Pm == null) return;
        bool p1Jumping = p1Pm.inputState.jumping;
        if (p1Jumping)
        {
            if (p1PulseTicks == 0) p1WasGroundedAtPress = p1Pm.grounded;
            p1PulseTicks++;
        }
        else if (p1PulseTicks > 0)
        {
            if (p1PulseLogs++ < 10)
                ModLog.Info($"[jump] P1 jump input was on for {p1PulseTicks} physics ticks (grounded at press {p1WasGroundedAtPress}, holdingJump now {p1Pm.inputState.holdingJump})");
            p1PulseTicks = 0;
        }
    }

    private static Vector3 goodScale = Vector3.zero;
    private static bool heldForPause;
    private static Vector3 heldVelocity;
    private static int scaleFixes;

    private static readonly List<(GameObject go, float startY)> testBodies = new();
    private static float testCheckAt = -1f;

    private static void CheckFallTest()
    {
        if (testCheckAt < 0f || Time.time < testCheckAt) return;
        testCheckAt = -1f;
        foreach (var (go, startY) in testBodies)
        {
            if (go == null) continue;
            ModLog.Info($"[move] fall test '{go.name}': started y {startY:F2}, now y {go.transform.position.y:F2} " +
                        $"({(go.transform.position.y < startY - 5f ? "FELL THROUGH" : "landed")})");
            Object.Destroy(go);
        }
        testBodies.Clear();
    }

    private static int contactLogs;
    private static void LogContact(PlayerMovement __instance, Collision other)
    {
        if (Pm == null || __instance.Pointer != Pm.Pointer || contactLogs >= 12 || other == null) return;
        contactLogs++;
        var c = other.collider;
        Vector3 n = other.contactCount > 0 ? other.GetContact(0).normal : Vector3.zero;
        ModLog.Info($"[move] P2 copy touched '{c?.name}' (layer {(c != null ? LayerMask.LayerToName(c.gameObject.layer) : "?")}), normal {n}, at {__instance.transform.position}");
    }

    // v0.39.1: P2's copy's own Unity calls (collisions: a wall touched -> CanWallClimb...) run in P2's stat context too,
    // like its movement tick - P2's passive (the Monkey's wall climb) and P2's stats answer, not P1's
    private static readonly Stack<bool> contextEntered = new();
    private static void MutePrefix(PlayerMovement __instance, out bool __state)
    {
        __state = Pm != null && __instance.Pointer == Pm.Pointer;
        if (!__state) return;
        MuteOn();
        contextEntered.Push(P2Inventory.EnterP2());
    }

    private static Exception MuteFinalizer(Exception __exception, bool __state)
    {
        if (__state)
        {
            if (contextEntered.Count > 0 && contextEntered.Pop()) P2Inventory.Exit();
            MuteOff();
            var p1 = MyPlayer.Instance?.GetComponent<PlayerMovement>();
            if (p1 != null && PlayerMovement.Instance != p1) PlayerMovement.Instance = p1;
        }
        return __exception;
    }
}
