using Rewired;
using UnityEngine;

namespace MegabonkSplitscreen;

// Player 2's controls in a run: left stick walks P2's body (after its portal entrance).
//  - Movement: a Unity CharacterController on P2's model (slopes, steps, walls) plus simple gravity.
//    Direction is relative to the main camera (P1's; P2 has no camera yet): stick up = away from the camera.
//    Speed = P1's current max speed.
//  - Animation: the game drives its animators by state, not parameters (P1's controller has none), so the mod
//    learns which state P1 is in while running and while standing (by watching P1's speed) and plays the same
//    state on P2 when P2 moves / stops. Falls back to common state names until P1 has moved.
//  - Stick values: read from P2's pad by element id. The ids are learned from Rewired polling
//    ("Left Stick X"/"Left Stick Y"); until then the usual XInput ids 0/1 are used.
internal static class P2Control
{
    private const float DeadZone = 0.2f;
    private const float Gravity = 30f;
    private const float TurnSpeed = 720f;          // degrees per second
    private const float FallbackSpeed = 8f;
    private const int LearnFrames = 20;            // frames in a state before it counts as P1's run/idle state

    private static GameObject body;
    private static Animator anim;
    private static CharacterController cc;
    private static float fallSpeed;
    private static bool moving;
    private static int playingState;
    private static bool loggedFirstMove;

    private static int stickXId = 0, stickYId = 1;
    private static bool idsLearnedX, idsLearnedY;

    // P1 animation learning (state hash -> frames seen)
    private static readonly Dictionary<int, int> runCounts = new();
    private static readonly Dictionary<int, int> idleCounts = new();
    private static int learnedRun, learnedIdle;

    private static readonly string[] RunNames = { "Run", "Running", "run", "Walk", "Move", "Locomotion" };
    private static readonly string[] IdleNames = { "Idle", "idle", "Stand" };

    public static void Take(GameObject p2Body, Animator p2Anim)
    {
        body = p2Body;
        anim = p2Anim;
        fallSpeed = 0f;
        moving = false;
        playingState = 0;
        loggedFirstMove = false;
        if (anim != null) anim.applyRootMotion = false;

        // Collider sized like P1's capsule; the model's pivot is taken to be at the feet
        float radius = 0.5f, height = 2f;
        var pm = PlayerMovement.Instance;
        var cap = pm != null ? pm.playerCollider : null;
        if (cap != null)
        {
            float s = cap.transform.lossyScale.x;
            radius = Mathf.Max(0.2f, cap.radius * s);
            height = Mathf.Max(radius * 2f, cap.height * cap.transform.lossyScale.y);
        }
        cc = body.GetComponent<CharacterController>();
        if (cc == null) cc = body.AddComponent<CharacterController>();
        float scale = Mathf.Max(0.01f, body.transform.lossyScale.y);
        cc.radius = radius / scale;
        cc.height = height / scale;
        cc.center = new Vector3(0f, height / 2f / scale, 0f);
        cc.stepOffset = Mathf.Min(0.5f, height / 3f) / scale;
        cc.slopeLimit = 50f;
        IgnoreP1();
        airborne = wasAirborne = aWasDown = paramsReset = false;
        facing = Quaternion.Euler(0f, p2Body.transform.eulerAngles.y, 0f);
        slopeUp = Vector3.up;
        P2Inventory.Ensure(P2Select.Picked ?? Assets.Scripts.Actors.Player.MyPlayer.Instance?.playerRenderer?.characterData);
        P2Weapons.Start(body);
        P2Health.Start(body, cc);
        P2Hud.Start();
        P2XpBar.Start();
        P2Pickups.Start(body);
        P2Minimap.Start(body);
        P2Movement.Start(body.transform, P2Select.Picked ?? Assets.Scripts.Actors.Player.MyPlayer.Instance?.playerRenderer?.characterData);
        if (P2Movement.Active) cc.enabled = false;   // the game's movement copy does the physics now        stateLogs = 0;
        lastSeenState = 0;
        SetGroundedParams(true);

        string p1Feet = "?";
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (cap != null && p1 != null && p1.playerRenderer != null && p1.playerRenderer.rendererObject != null)
            p1Feet = $"P1 capsule bottom y {cap.bounds.min.y:F2}, P1 model y {p1.playerRenderer.rendererObject.transform.position.y:F2}";
        ModLog.Info($"P2 controls on: collider r {radius:F2} h {height:F2} ({p1Feet}), speed {Speed():F1}, " +
                    $"state names found on P2: run {FindNamed(RunNames)}, idle {FindNamed(IdleNames)}");
    }

    public static void Release()
    {
        P2Movement.Stop();
        P2Weapons.Stop();
        P2Health.Stop();
        P2Pickups.Stop();
        if (P2LevelUp.P2ScreenOpen) P2LevelUp.Reset();   // never leave P1 without controls
        body = null;
        anim = null;
        cc = null;
    }

    // Called every frame (from P2Body.Update).
    public static void Update()
    {
        LearnFromP1();
        P1Motion.Update();
        if (body == null || cc == null || P2Health.IsDead || !body.activeInHierarchy) return;
        var joy = PlayerJoin.P2Joystick;
        if (joy == null && !PlayerJoin.VirtualP2) return;   // (autotest: P2 without a pad, script input)

        float dt = Time.deltaTime;
        // Paused (level-up screen, pause menu). The game's menus pause its own clock (MyTime.paused), not always
        // Unity's (user: P2's weapons kept firing under a level-up screen)
        if (dt <= 0f || Assets.Scripts.Utility.MyTime.paused)
        {
            // A followed under menus (P2Movement.FollowA): the press that picks an offer is no jump afterwards
            if (P2Movement.JumpReleaseFix) { bool held = ReadA(joy); aWasDown = held; P2Movement.FollowA(held); }
            return;
        }
        P2Body.Sub("control: weapons", P2Weapons.Tick);

        Vector2 stick = ReadStick(joy);
        StickNow = stick;
        if (P2Movement.Active)
        {
            P2Body.Sub("control: follow movement", () => FollowGameMovement(joy, dt));
            P2Body.Sub("control: spin always-on", P2Weapons.AfterBodyMoved);
            return;
        }
        Vector3 move = Vector3.zero;
        if (stick.magnitude > DeadZone)
        {
            var cam = P2Camera.Cam != null ? P2Camera.Cam : Camera.main;   // relative to P2's own camera (splitscreen)
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right;
            fwd.y = 0f;
            right.y = 0f;
            fwd.Normalize();
            right.Normalize();
            move = (fwd * stick.y + right * stick.x);
            // Full speed from ~85% stick push: pads rarely reach 1.0, especially diagonally, so P2 ran slower than P1
            float push = Mathf.Clamp01(Mathf.InverseLerp(DeadZone, 0.85f, stick.magnitude));
            move = move.sqrMagnitude > 0.0001f ? move.normalized * push : Vector3.zero;
        }

        // Jump: A, on the press, only from the ground (no double jump yet)
        bool aDown = ReadA(joy);
        float now = Time.time;
        if (cc.isGrounded) lastGroundedAt = now;
        bool onGround = now - lastGroundedAt < CoyoteTime;   // isGrounded flickers; a short grace keeps it stable
        if (aDown && !aWasDown && onGround && !airborne)
        {
            fallSpeed = P1Motion.JumpSpeed();
            StartJumpAnimation();
            airborne = true;
            jumpedAt = now;
            lastGroundedAt = -1f;
            onGround = false;
            if (!loggedJump)
            {
                loggedJump = true;
                ModLog.Info($"P2 jump (speed {P1Motion.JumpSpeed():F1}), jump state {JumpState()}, fall state {FallState()}");
            }
        }
        aWasDown = aDown;
        if (!airborne && !onGround)
        {
            airborne = true;   // walked off a ledge
            jumpedAt = now;
        }
        else if (airborne && cc.isGrounded && now - jumpedAt > 0.1f)
        {
            airborne = false;  // landed
        }

        if (cc.isGrounded && fallSpeed < 0f) fallSpeed = -2f;   // keep pressed to the ground
        fallSpeed -= (fallSpeed > 0f ? P1Motion.RiseGravity() : P1Motion.FallGravity()) * dt;   // measured from P1
        Vector3 velocity = move * Speed() + Vector3.up * fallSpeed;
        Vector3 before = body.transform.position;
        cc.Move(velocity * dt);
        CompareSpeeds(before, dt, stick.magnitude);

        if (move.sqrMagnitude > 0.0001f)
        {
            var target = Quaternion.LookRotation(move, Vector3.up);
            body.transform.rotation = Quaternion.RotateTowards(body.transform.rotation, target, TurnSpeed * dt);
            if (!loggedFirstMove)
            {
                loggedFirstMove = true;
                ModLog.Info($"P2 first move: stick {stick}, grounded {cc.isGrounded}, at {body.transform.position}");
            }
        }

        if ((cc.collisionFlags & CollisionFlags.Sides) != 0) LogBlocker();
        P2Weapons.AfterBodyMoved();

        Animate(move.sqrMagnitude > 0.0001f);
    }

    // Run/idle/jump animation from the current state (shared by both movers)
    private static void Animate(bool nowMoving)
    {
        if (P2Body.GameAnimates) return;
        int want = nowMoving ? RunState() : IdleState();
        if (airborne)
        {
            // The jump state for the whole air time, like P1 (P1 shows it until just before landing). v0.22.4 switched to
            // the "fall" state on the way down and P2's controller kept pulling back to the jump state, so it never played through.
            int air = JumpState();
            if (air != 0) want = air;
        }
        if (airborne != wasAirborne)
        {
            wasAirborne = airborne;
            SetGroundedParams(!airborne);
        }
        if (nowMoving != moving || playingState <= 0 || want != playingState)   // <= 0: nothing known yet, retry
        {
            if (nowMoving != moving || playingState <= 0) SetMoveParams(nowMoving);
            moving = nowMoving;
            PlayState(want);
        }
        WatchState();
    }

    // P2 moved by the game's PlayerMovement copy (P2Movement): the body follows it, faces where it goes, and the
    // animation follows its real velocity and grounded state
    public static Vector2 StickNow { get; private set; }
    public static Vector3 SlopeUp => slopeUp;   // the ground's (smoothed) up under P2, for things that lean with it
    private static Quaternion facing = Quaternion.identity;   // P2's heading (yaw only)
    private static Vector3 slopeUp = Vector3.up;
    private const float SlopeSmoothing = 10f;

    // TEMP: how much P1's model leans compared with the ground's slope, to match P2's (logged a few times on slopes)
    private static int slopeLogs;
    private static float nextSlopeLog;
    private static void CompareSlope()
    {
        if (slopeLogs >= 6 || Time.time < nextSlopeLog) return;
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        var p1Pm = PlayerMovement.Instance;
        var model = p1?.playerRenderer?.rendererObject?.transform;
        if (model == null || p1Pm == null || !p1Pm.grounded) return;
        float groundAngle = Vector3.Angle(Vector3.up, p1Pm.normalVector);
        if (groundAngle < 8f) return;
        nextSlopeLog = Time.time + 2f;
        slopeLogs++;
        ModLog.Info($"[slope] P1 on a {groundAngle:F0} deg slope: P1 model leans {Vector3.Angle(Vector3.up, model.up):F0} deg " +
                    $"(renderer smoothedNormal {p1.playerRenderer.smoothedNormal}); P2 leans {Vector3.Angle(Vector3.up, body.transform.up):F0} deg on {Vector3.Angle(Vector3.up, P2Movement.GroundNormal()):F0}");
    }
    private static void FollowGameMovement(Joystick joy, float dt)
    {
        bool aDown = ReadA(joy);
        if (aDown && !aWasDown && P2Movement.Grounded()) jumpPulseUntil = Time.time + JumpPulse;
        aWasDown = aDown;
        P2Movement.ReadInput(aDown);

        body.transform.position = P2Movement.BodyPosition() + P2Body.HoverOffset;   // + Tony's hover bob
        Vector3 v = P2Movement.Velocity();
        Vector3 flat = new Vector3(v.x, 0f, v.z);
        bool nowMoving = flat.magnitude > 0.6f;
        if (nowMoving)
        {
            var target = Quaternion.LookRotation(flat.normalized, Vector3.up);
            facing = Quaternion.RotateTowards(facing, target, TurnSpeed * dt);
        }
        // Lean with the slope like P1's model (user): the ground normal from the game's movement code, smoothed
        // In the air the lean stays as it was (user: jumping on a slope, P1 keeps the slope's angle, P2 went upright)
        if (P2Movement.Grounded())
            slopeUp = Vector3.Slerp(slopeUp, P2Movement.GroundNormal(), 1f - Mathf.Exp(-SlopeSmoothing * dt));
        body.transform.rotation = Quaternion.FromToRotation(Vector3.up, slopeUp) * facing;
        P2Body.NoteRotationSet();
        CompareSlope();
        airborne = !P2Movement.Grounded();
        fallSpeed = v.y;
        AnimateLikeP1(nowMoving, !airborne);
        // Only while P2 slides, plus a moment after so the game itself clears the slide value (slide_pose test: running
        // through it all the time also took P2's run/idle to another state, 998495048, unlike P1's)
        if (P2Movement.Pm != null && P2Movement.Pm.IsSlidingAnimation()) slideAnimUntil = Time.time + SlideAnimTail;
        if (GameRendererOn && !P2Body.GameAnimates && Time.time < slideAnimUntil) GameRendererUpdate();
    }

    // The game's real animator parameters, measured on P1 (v0.24.0 [params] log): bools "grounded", "moving" and
    // "jumping"; "jumping" is on for ~0.1 s at take-off only. (Before, guessed names were set and "jumping" stayed on
    // for the whole air time, so the controller kept restarting the jump: P2 stayed in the jump's start pose.)
    // The controller does its own transitions (idle/run/jump/land) from these, like P1's.
    private const float JumpPulse = 0.1f;
    private static float jumpPulseUntil = -1f;
    private static readonly int HGrounded = Animator.StringToHash("grounded");
    private static readonly int HMoving = Animator.StringToHash("moving");
    private static readonly int HJumping = Animator.StringToHash("jumping");
    private static bool paramsReset;

    // Revive's rise out of the portal (user: make them do their falling animation): P2 in the air - the take-off
    // pulse first, then "grounded" off (the controller's own fall)
    public static void AirAnimation(bool takeOff)
    {
        if (anim == null) return;
        if (takeOff) jumpPulseUntil = Time.time + JumpPulse;
        anim.SetBool(HGrounded, false);
        anim.SetBool(HMoving, false);
        anim.SetBool(HJumping, Time.time < jumpPulseUntil);
    }

    private static void AnimateLikeP1(bool nowMoving, bool grounded)
    {
        // v0.38.4 (user: P2 Calcium stuck in one pose): Calcium's controller also runs on "grounded"/"moving" (v0.38.2
        // [params] log: P1's PlayerRenderer sets them every frame; P2 has no PlayerRenderer), so these three parameters
        // are set for him too - only the mod's own state changes stand back while his skating script runs
        if (anim == null) return;
        if (!paramsReset)
        {
            // Clear what the guessing code may have left on (only the names that exist matter)
            SetMoveParams(false);
            SetGroundedParams(true);
            paramsReset = true;
        }
        moving = nowMoving;
        anim.SetBool(HGrounded, grounded);
        anim.SetBool(HMoving, nowMoving);
        anim.SetBool(HJumping, Time.time < jumpPulseUntil);
    }

    // The game's own animator update for P2 (1.0 alpha, user: look into the slide pose once more): P1's
    // PlayerRenderer.Update sets P1's animator values every frame from P1's movement - including the unreadable one
    // that holds the slide pose. It runs once more per frame on P2's: P1's renderer pointed at P2's animator, P2's
    // movement copy and P2's own copy of the renderer's state, then everything put back. P2's body, P1's model and
    // P1's hips/torso keep their transforms (only the animator values are wanted).
    public static bool GameRendererOn = true;   // TEMP test switch: autotest 'gamerenderer on|off'
    private static float slideAnimUntil = -1f;
    private const float SlideAnimTail = 0.3f;
    private static bool p2Moving;
    private static float p2StoppedMovingAt;
    private static Quaternion p2LookRot = Quaternion.identity;
    private static Vector3 p2SmoothedNormal = Vector3.up, p2LastMoveDir = Vector3.forward;
    private static int rendererErrors;
    private static void GameRendererUpdate()
    {
        var pr = Assets.Scripts.Actors.Player.MyPlayer.Instance?.playerRenderer;
        var pm = P2Movement.Pm;
        if (pr == null || pm == null || anim == null || body == null) return;
        var a0 = pr.animator; var m0 = pr.playerMovement; var r0 = pr.rendererObject;
        bool mv0 = pr.moving; float st0 = pr.stoppedMovingAtTime; var look0 = pr.desiredLookRotation;
        var n0 = pr.smoothedNormal; var dir0 = pr.lastValidMoveDirection;
        Transform hips = pr.hips, torso = pr.torso, model = r0 != null ? r0.transform : null;
        Quaternion hipsRot = hips != null ? hips.localRotation : Quaternion.identity, torsoRot = torso != null ? torso.localRotation : Quaternion.identity;
        Quaternion modelRot = model != null ? model.rotation : Quaternion.identity;
        // p1_facing test (user: P1 faced right / spun while P2 slid): the borrowed update also turned P1's own objects -
        // the renderer's object and everything between it and the model keep their rotations
        var keep = new List<(Transform t, Quaternion r)>();
        for (var t = model; t != null; t = t.parent) { keep.Add((t, t.localRotation)); if (t == pr.transform) break; }
        if (!keep.Any(k => k.t == pr.transform)) keep.Add((pr.transform, pr.transform.localRotation));
        Vector3 bodyPos = body.transform.position; Quaternion bodyRot = body.transform.rotation;
        try
        {
            pr.animator = anim; pr.playerMovement = pm; pr.rendererObject = body;
            pr.moving = p2Moving; pr.stoppedMovingAtTime = p2StoppedMovingAt; pr.desiredLookRotation = p2LookRot;
            pr.smoothedNormal = p2SmoothedNormal; pr.lastValidMoveDirection = p2LastMoveDir;
            pr.Update();
        }
        catch (Exception e) { if (rendererErrors++ < 3) ModLog.Warn($"P2 game animator update failed: {e.Message}"); }
        finally
        {
            p2Moving = pr.moving; p2StoppedMovingAt = pr.stoppedMovingAtTime; p2LookRot = pr.desiredLookRotation;
            p2SmoothedNormal = pr.smoothedNormal; p2LastMoveDir = pr.lastValidMoveDirection;
            pr.animator = a0; pr.playerMovement = m0; pr.rendererObject = r0;
            pr.moving = mv0; pr.stoppedMovingAtTime = st0; pr.desiredLookRotation = look0;
            pr.smoothedNormal = n0; pr.lastValidMoveDirection = dir0;
            if (hips != null) hips.localRotation = hipsRot;
            if (torso != null) torso.localRotation = torsoRot;
            for (int i = keep.Count - 1; i >= 0; i--) if (keep[i].t != null) keep[i].t.localRotation = keep[i].r;
            if (model != null) model.rotation = modelRot;
            body.transform.SetPositionAndRotation(bodyPos, bodyRot);
        }
    }

    // Slide pose (1.0 alpha, user: the player can slide - P2 couldn't): P2 slides (P2Movement: the Slide button), but
    // in its running pose. [animprobe]/[slideanim] (slide test): P1's animator goes to its own slide state (459278484)
    // and stays there on a value the game sets every frame; that value's name can't be read (GetParameter and
    // .parameters fail through the interop; none of ~40 bool/int/float names matched) and the Animator setters can't
    // be hooked (shared native code). Putting P2's animator in that state only lasted a frame - the controller left it
    // at once for 998495048 (held each frame: flickering) - so it's left alone.
    private const float JumpSpeed = 12f;
    private const float CoyoteTime = 0.15f;
    private static bool airborne, wasAirborne, aWasDown, loggedJump;
    private static float lastGroundedAt = -1f, jumpedAt;

    // A on P2's pad, via polling like the rest of P2's input
    // v0.24.0: "first button" polling reports one button only, so with another button held too, A read as released
    // and pressed again mid-hold -> extra jumps in the ticks the game still counts as grounded (super jump, height 22
    // instead of 4.75) and a cut "holding jump" (low jumps). All held buttons are checked now.
    // Jump: A, plus every other button the game binds to Jump on P2's pad (user: the right bumper jumps too)
    internal static bool ReadA(Joystick joy) => PadHeld(joy, "a", "cross")
        || (!Autotest.Active && joy != null && (PadBindings.HeldAny(joy, PadBindings.AllFor(joy, MyInputManager.Jump))
                                                || PadBindings.HeldAny(joy, PadBindings.AllFor(joy, MyInputManager.JumpBhop))));

    internal static bool PadHeld(Joystick joy, params string[] names)
    {
        if (Autotest.Active) return Autotest.P2Held(names);   // the test script plays P2
        var all = ReInput.controllers.polling.PollControllerForAllButtons(ControllerType.Joystick, joy.id);
        if (all == null) return false;
        var e = all.GetEnumerator();   // an IL2CPP enumerable: MoveNext lives on the non-generic interface
        var move = e.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (move.MoveNext())
        {
            var bp = e.Current;
            if (!bp.success) continue;
            string n = (bp.elementIdentifierName ?? "").Trim().ToLowerInvariant();
            if (names.Contains(n)) return true;
        }
        return false;
    }

    // P2's controller has parameters (P1's Calcium one had none, P2's GetParameter was called and failed), and
    // it left the run state for idle right after every CrossFade (v0.10.1 log): most likely a "moving"-style
    // parameter still at its default. The names can't be read (GetParameter isn't available in this build),
    // so the usual names are set on start/stop only; names the controller lacks just give a Unity warning.
    private static readonly string[] MoveBools = { "moving", "Moving", "isMoving", "IsMoving", "walking", "Walking",
        "isWalking", "IsWalking", "running", "Running", "isRunning", "IsRunning", "move", "Move" };
    private static readonly string[] MoveFloats = { "speed", "Speed", "velocity", "Velocity", "moveSpeed", "MoveSpeed",
        "movementSpeed", "MovementSpeed", "move", "Move" };
    private static readonly string[] GroundBools = { "grounded", "Grounded", "isGrounded", "IsGrounded", "onGround", "OnGround" };

    // By hash: Animator.SetFloat(string, float) throws MissingMethodException (ReadOnlySpan.GetPinnableReference)
    // in this interop build, which aborted Update every frame in v0.10.2 (Calcium then never got its run state).
    private static int[] Hashes(string[] names) => names.Select(Animator.StringToHash).ToArray();
    private static int[] moveBoolHashes, moveFloatHashes, groundBoolHashes;

    private static void SetMoveParams(bool on)
    {
        if (anim == null || P2Body.GameAnimates) return;
        moveBoolHashes ??= Hashes(MoveBools);
        moveFloatHashes ??= Hashes(MoveFloats);
        foreach (var h in moveBoolHashes) anim.SetBool(h, on);
        foreach (var h in moveFloatHashes) anim.SetFloat(h, on ? Speed() : 0f);
    }

    private static void SetGroundedParams(bool grounded)
    {
        if (anim == null || P2Body.GameAnimates) return;
        groundBoolHashes ??= Hashes(GroundBools);
        airBoolHashes ??= Hashes(AirBools);
        foreach (var h in groundBoolHashes) anim.SetBool(h, grounded);
        foreach (var h in airBoolHashes) anim.SetBool(h, !grounded);
    }

    // P2's jump start (user: P1 shows a jump animation, P2 doesn't): the usual jump triggers, in case the controller is
    // parameter-driven like the run, and the jump state started at once instead of a short crossfade.
    private static readonly string[] AirBools = { "jumping", "Jumping", "isJumping", "IsJumping", "inAir", "InAir",
        "airborne", "Airborne", "falling", "Falling", "isFalling", "IsFalling" };
    private static readonly string[] JumpTriggers = { "Jump", "jump", "JumpStart", "jumpStart" };
    private static int[] airBoolHashes, jumpTriggerHashes;

    private static void StartJumpAnimation()
    {
        if (anim == null || P2Body.GameAnimates) return;
        jumpTriggerHashes ??= Hashes(JumpTriggers);
        foreach (var h in jumpTriggerHashes) anim.SetTrigger(h);
        SetGroundedParams(false);
        int state = JumpState();
        if (state != 0)
        {
            anim.Play(state, 0, 0f);
            playingState = state;
        }
    }

    // TEMP: log (a few times) when P2's controller moves on to a state we didn't ask for
    private static int stateLogs;
    private static int lastSeenState;
    private static void WatchState()
    {
        if (anim == null || stateLogs >= 15 || anim.IsInTransition(0)) return;
        int cur = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        if (cur == lastSeenState) return;
        lastSeenState = cur;
        if (cur == playingState) return;
        stateLogs++;
        ModLog.Info($"[anim] P2 controller went to state {cur} (wanted {playingState}, moving {moving})");
    }

    // TEMP: when P2 is blocked sideways, log what it bumps into (once per collider)
    private static readonly HashSet<string> loggedBlockers = new();
    private static void LogBlocker()
    {
        Vector3 c = body.transform.TransformPoint(cc.center);
        float r = cc.radius * body.transform.lossyScale.x + 0.2f;
        foreach (var col in Physics.OverlapSphere(c, r + cc.height * body.transform.lossyScale.y / 2f))
        {
            if (col == null || col.isTrigger || col.gameObject == body) continue;
            string key = $"{col.name}|{col.GetIl2CppType().Name}|{LayerMask.LayerToName(col.gameObject.layer)}";
            if (loggedBlockers.Add(key)) ModLog.Info($"[collide] P2 blocked near '{col.name}' ({col.GetIl2CppType().Name}, layer {LayerMask.LayerToName(col.gameObject.layer)})");
        }
    }

    // P1 and P2 shouldn't push each other: ignore every collider on P1's objects.
    private static void IgnoreP1()
    {
        var roots = new List<GameObject>();
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (p1 != null) roots.Add(p1.gameObject);
        var pm = PlayerMovement.Instance;
        if (pm != null && (p1 == null || pm.gameObject != p1.gameObject)) roots.Add(pm.gameObject);
        var names = new List<string>();
        foreach (var root in roots)
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null) continue;
                Physics.IgnoreCollision(cc, col, true);
                names.Add($"{col.name}({col.GetIl2CppType().Name}{(col.isTrigger ? ", trigger" : "")})");
            }
        ModLog.Info($"P2 ignores {names.Count} P1 colliders: {string.Join(", ", names)}");
    }

    // P1's measured run speed, scaled by the game's P2/P1 differences (P1Motion)
    private static float Speed() => P1Motion.RunSpeed();

    // TEMP: P1's and P2's actual ground speeds side by side while both run (user: speeds don't match)
    private static float nextCompare;
    private static int compareLogs;
    private static Vector3 p1Prev;
    private static void CompareSpeeds(Vector3 p2Before, float dt, float stickMag)
    {
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (p1 == null || dt <= 0f) return;
        Vector3 p1Now = p1.transform.position;
        Vector3 d1 = p1Now - p1Prev, d2 = body.transform.position - p2Before;
        p1Prev = p1Now;
        d1.y = 0f;
        d2.y = 0f;
        float s1 = d1.magnitude / dt, s2 = d2.magnitude / dt;
        if (Time.time < nextCompare || compareLogs >= 12 || s1 < 3f || s2 < 3f || s1 > 40f) return;
        nextCompare = Time.time + 2f;
        compareLogs++;
        ModLog.Info($"[motion] speeds now: P1 {s1:F2}, P2 {s2:F2} (P2 target {Speed():F2}, stick {stickMag:F2})");
    }

    public static bool HasControl => body != null;

    private static Vector2 ReadStick(Joystick joy)
    {
        if (Autotest.Active) return Autotest.P2Stick();
        // Learn the stick's element ids from polling (only reports the strongest axis, which is enough)
        var ap = ReInput.controllers.polling.PollControllerForFirstAxis(ControllerType.Joystick, joy.id);
        if (ap.success)
        {
            string n = (ap.elementIdentifierName ?? "").ToLowerInvariant();
            if (n == "left stick x" && (!idsLearnedX || stickXId != ap.elementIdentifierId))
            {
                idsLearnedX = true;
                stickXId = ap.elementIdentifierId;
                ModLog.Info($"P2 stick X = element id {stickXId}");
            }
            else if (n == "left stick y" && (!idsLearnedY || stickYId != ap.elementIdentifierId))
            {
                idsLearnedY = true;
                stickYId = ap.elementIdentifierId;
                ModLog.Info($"P2 stick Y = element id {stickYId}");
            }
        }
        return new Vector2(joy.GetAxisById(stickXId), joy.GetAxisById(stickYId));
    }

    // ---------- Animation ----------

    // P1's speed is measured from how far P1 actually moved: the portal moves P1's transform directly, so the
    // rigidbody velocity read ~0 while P1 walked out and the walk state got learned as "idle" (v0.10.0 log).
    // Nothing is learned during the portal entrance.
    private static Vector3 p1LastPos;
    private static bool p1HasLast;
    private static int p1LastState;

    private static void LearnFromP1()
    {
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        var p1Anim = p1 != null && p1.playerRenderer != null ? p1.playerRenderer.animator : null;
        float dt = Time.deltaTime;
        if (p1 == null || p1Anim == null || dt <= 0f)
        {
            p1HasLast = false;
            return;
        }
        Vector3 pos = p1.transform.position;
        Vector3 delta = pos - p1LastPos;
        bool hadLast = p1HasLast;
        p1LastPos = pos;
        p1HasLast = true;
        if (!hadLast || P2Body.IsRecording) return;

        float vy = delta.y / dt;
        SampleP1Params(p1Anim, p1);
        delta.y = 0f;
        float speed = delta.magnitude / dt;
        int state = p1Anim.GetCurrentAnimatorStateInfo(0).shortNameHash;

        // In the air: going up = jump state, coming down = fall state
        var pm = PlayerMovement.Instance;
        if (pm != null && !pm.grounded)
        {
            if (vy > 2f) Count(jumpCounts, state, ref learnedJump, "jump", 5);
            else if (vy < -2f) Count(fallCounts, state, ref learnedFall, "fall", 5);
            return;
        }
        if (state != p1LastState)
        {
            // TEMP: map P1's states to what P1 is doing
            ModLog.Info($"[anim] P1 state {state} at speed {speed:F1}");
            p1LastState = state;
        }
        if (speed > 3f) Count(runCounts, state, ref learnedRun, "run");
        else if (speed < 0.3f) Count(idleCounts, state, ref learnedIdle, "idle");
    }

    // TEMP: P1's real animator values for the parameter-like names in the game's string table (the parameter list
    // itself can't be read in this build), logged when they change, with P1's velocity, to see which ones the game
    // drives and how. Names that don't exist read 0/false (and Unity warns in Player.log).
    private static readonly string[] ProbeFloats = { "VelocityX", "VelocityY", "VelocityZ", "Velocity", "speed", "Speed", "moveSpeed", "fallSpeed" };
    private static readonly string[] ProbeBools = { "grounded", "onGround", "moving", "isMoving", "isRunning", "Running", "isJumping",
        "jumping", "IsJumpHappened", "Airborne", "falling", "landing", "Sliding", "IsSliding", "IsCrouching", "crouching",
        "Wallrunning", "Wallrun", "isGrinding", "isDashing", "Dash", "Idle", "Run", "Jump" };
    private static int[] probeFloatHashes, probeBoolHashes;
    private static string lastProbe = "";
    private static float nextProbe, probeUntil = -1f;
    private static int probeLogs;

    private static void SampleP1Params(Animator p1Anim, Assets.Scripts.Actors.Player.MyPlayer p1)
    {
        if (probeUntil < 0f) probeUntil = Time.time + 90f;
        if (Time.time > probeUntil || Time.time < nextProbe || probeLogs >= 150) return;
        nextProbe = Time.time + 0.1f;
        probeFloatHashes ??= Hashes(ProbeFloats);
        probeBoolHashes ??= Hashes(ProbeBools);
        var parts = new List<string>();
        for (int i = 0; i < ProbeFloats.Length; i++)
        {
            float f = p1Anim.GetFloat(probeFloatHashes[i]);
            if (Mathf.Abs(f) > 0.01f) parts.Add($"{ProbeFloats[i]}={f:F1}");
        }
        for (int i = 0; i < ProbeBools.Length; i++)
            if (p1Anim.GetBool(probeBoolHashes[i])) parts.Add(ProbeBools[i]);
        string now = string.Join(" ", parts);
        if (now == lastProbe) return;
        lastProbe = now;
        probeLogs++;
        var pm = PlayerMovement.Instance;
        Vector3 v = pm?.rb != null ? pm.rb.velocity : Vector3.zero;
        Vector3 local = p1.playerRenderer?.rendererObject != null ? p1.playerRenderer.rendererObject.transform.InverseTransformDirection(v) : Vector3.zero;
        ModLog.Info($"[params] P1: {(now.Length > 0 ? now : "(all 0/false)")} | vel {v} local {local}, grounded {pm?.grounded}, state {p1Anim.GetCurrentAnimatorStateInfo(0).shortNameHash}");
    }

    private static readonly Dictionary<int, int> jumpCounts = new();
    private static readonly Dictionary<int, int> fallCounts = new();
    private static int learnedJump, learnedFall;
    private static readonly string[] JumpNames = { "Jump", "jump", "JumpStart", "Jumping" };
    private static readonly string[] FallNames = { "Fall", "fall", "Falling", "InAir", "Airborne" };
    private static int JumpState() => Usable(learnedJump) ? learnedJump : FindNamed(JumpNames);
    private static int FallState() => Usable(learnedFall) ? learnedFall : FindNamed(FallNames);

    private static void Count(Dictionary<int, int> counts, int state, ref int learned, string what, int minFrames = LearnFrames)
    {
        counts.TryGetValue(state, out int c);
        counts[state] = ++c;
        if (state != learned && c >= minFrames && (learned == 0 || c > counts.GetValueOrDefault(learned)))
        {
            learned = state;
            ModLog.Info($"Learned P1's {what} animation state: {state}");
        }
    }

    private static int RunState() => Usable(learnedRun) ? learnedRun : FindNamed(RunNames);
    private static int IdleState() => Usable(learnedIdle) ? learnedIdle : FindNamed(IdleNames);

    private static bool Usable(int hash) => hash != 0 && anim != null && anim.HasState(0, hash);

    private static int FindNamed(string[] names)
    {
        if (anim == null) return 0;
        foreach (var n in names)
        {
            int h = Animator.StringToHash(n);
            if (anim.HasState(0, h)) return h;
        }
        return 0;
    }

    private static int playLogs;

    private static void PlayState(int hash)
    {
        if (anim == null || P2Body.GameAnimates) return;
        if (hash == 0)
        {
            if (playingState != -1) ModLog.Info($"P2: no {(moving ? "run" : "idle")} animation state known yet");
            playingState = -1;
            return;
        }
        if (hash == playingState) return;
        playingState = hash;
        anim.CrossFade(hash, 0.15f, 0);
        if (playLogs++ < 10)
            ModLog.Info($"[anim] P2 {(moving ? "run" : "idle")} -> state {hash} ({(hash == learnedRun || hash == learnedIdle ? "learned from P1" : "by name")})");
    }
}
