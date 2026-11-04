using Assets.Scripts.Actors.Player;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Player 2's body in a run (visual only, no controls yet), arriving through its own portal.
//  1. When the game starts P1's spawn portal (SpawnPlayerPortal.StartPortal), the mod RECORDS P1's entrance
//     every frame: model position/rotation and animator values, plus when P1's portal closes.
//  2. Delay seconds later, a copy of the portal opens beside P1's (screen-right, across P1's walking direction)
//     and P2's model REPLAYS P1's recording, shifted sideways onto that portal. So P2 walks out of it exactly
//     the way P1 did. The copy closes when the replay reaches the moment P1's portal closed.
//  3. After the replay P2 stands idle where it ended.
//
// The portal copy has its SpawnPlayerPortal script removed (that script moves P1); the mod scales the
// portal's render in and out and plays its sounds itself. P2's model is the character's own prefab
// (CharacterData.prefab) with no game scripts, so nothing in the game reacts to it.
internal static class P2Body
{
    private const float Delay = 2.0f;          // P2's portal opens this long after P1's started
    private const float PortalSpacing = 3.5f;  // distance between the two portals (world units)
    private const float PortalBack = 3.5f;     // P2's portal this much further back than P1's (a portal width: no overlap)
    private const float RecordAfterClose = 1.0f;
    private const float MaxRecordTime = 20f;
    private const float PortalScaleTime = 0.4f;

    private struct Sample
    {
        public float t;
        public Vector3 pos;
        public Quaternion rot;
        public float[] values;
        public int state;
        public float stateTime;
    }

    private struct Param
    {
        public int hash;
        public AnimatorControllerParameterType type;
    }

    // P1 / recording
    private static SpawnPlayerPortal portal;
    private static bool recording;
    public static bool IsRecording => recording;
    private static float recordStart;
    private static float p1OpenedAt = -1f, p1ClosedAt = -1f;
    private static readonly List<Sample> samples = new();
    private static readonly List<Param> p1Params = new();
    private static bool paramsBroken;
    private static Transform p1Model;
    private static Animator p1Anim;

    // P2 / replay
    private static GameObject p2Portal;
    private static Transform p2PortalRender;
    private static Vector3 p2PortalScale;
    private static AudioSource p2AudioOpen, p2AudioLoop, p2AudioClose, p2AudioPassing;
    // The portal's "passing" effect (user: a lightning effect as P1 walks through, P2 should have it too): when P1's
    // started playing in the recording; P2's copy plays at the same point of the replay
    private static ParticleSystem p2PassingFx;
    private static float p1PassedAt = -1f;
    private static Vector3 p1PassingLocal;
    private static bool p2Passed;
    private static GameObject body;
    public static GameObject Body => body;
    private static Animator anim;
    private static readonly HashSet<int> p2ParamHashes = new();
    private static Vector3 shift;
    private static bool replaying;
    public static bool Entering => replaying;   // P2's portal entrance playing (P2 not in control yet)
    private static int cursor;
    private static int lastState;
    private static float portalOpenStart = -1f, portalCloseStart = -1f;
    private static bool portalCloseScheduled;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(SpawnPlayerPortal), nameof(SpawnPlayerPortal.StartPortal)),
            postfix: new HarmonyMethod(typeof(P2Body), nameof(OnPortalStart)));
        ModLog.Info("P2Body: watching the spawn portal");
    }

    private static void OnPortalStart(SpawnPlayerPortal __instance)
    {
        if (!PlayerJoin.HasP2) return;
        Cleanup();
        try { Downed.OnStageStart(); }
        catch (Exception e) { ModLog.Error($"[down] new stage revive failed: {e}"); }
        portal = __instance;
        recording = true;
        recordStart = Time.time;
        p1OpenedAt = p1ClosedAt = -1f;
        p1PassedAt = -1f;
        p2Passed = false;
        samples.Clear();
        p1Params.Clear();
        paramsBroken = false;
        p1Model = null;
        p1Anim = null;
        ModLog.Info($"Spawn portal started at {__instance.transform.position}; recording P1, P2's portal in {Delay} s");
    }

    // Called every frame by the Ticker. Each part guarded so one failure can't stop the others.
    public static void Update()
    {
        Guard("p1 test health", P2Health.UpdateP1TestHealth);
        Guard("record", Record);
        Guard("replay", Replay);
        Guard("portal", AnimatePortal);
        Guard("pause animation", FreezeWhilePaused);
        Guard("skating", LinkSkating);
        Guard("hover watch", WatchHover);
        Guard("shield outline", UpdateShieldOutline);
        Guard("control", P2Control.Update);
        Guard("camera", P2Camera.Update);
        Guard("pause", P2Pause.Update);
        Guard("camera mode button", CameraModeToggle.Update);
        Guard("minimap", P2Minimap.Update);
        Guard("bossbars", BossBars.Update);
        Guard("interact", P2Interact.Update);
        Guard("doors", DoorTeleports.Update);
        Guard("explosions", P2Explosions.Update);
        Guard("rewards", P2Rewards.Update);
        Guard("fall", FallDamage.Update);
        Guard("audio", P2Audio.Update);
        Guard("inventory", P2Inventory.Update);
        Guard("abilities", P2Abilities.Update);
        Guard("power-ups", P2PowerUps.Update);
        Guard("map", MapView.Update);
        Guard("levels", LevelLabels.Update);
        Guard("character diag", CharacterDiag.Update);
        Guard("health", P2Health.Update);
        Guard("revive", Revive.Update);
        Guard("revive portals", Revive.UpdateClosing);
        Guard("hud", P2Hud.Update);
        Guard("xpbar", P2XpBar.Update);
        if (Time.deltaTime > 0f) Guard("pickups", P2Pickups.Update);
        PerfReport();
    }

    private static readonly HashSet<string> loggedErrors = new();
    // TEMP [perf] (user: the game stutters a bit; [perf] log: the mod takes ~2.3 ms a frame, spikes up to 74 ms): time
    // per part over 300 frames (average and worst frame), logged by PerfReport
    private static readonly Dictionary<string, (double sum, double worst)> partTimes = new();
    private static readonly System.Diagnostics.Stopwatch partWatch = new();
    private static void Guard(string what, Action a)
    {
        partWatch.Restart();
        try { a(); }
        catch (Exception e)
        {
            if (loggedErrors.Add(what + e.Message)) ModLog.Error($"P2Body {what} failed: {e}");
        }
        double ms = partWatch.Elapsed.TotalMilliseconds;
        partTimes.TryGetValue(what, out var pt);
        partTimes[what] = (pt.sum + ms, Math.Max(pt.worst, ms));
    }

    // A piece inside a part, timed on its own stopwatch (the parts' one is shared) - shows in the same [perf] line
    public static void Sub(string what, Action a)
    {
        var w = System.Diagnostics.Stopwatch.StartNew();
        try { a(); }
        catch (Exception e) { if (loggedErrors.Add(what + e.Message)) ModLog.Error($"P2Body {what} failed: {e}"); }
        double ms = w.Elapsed.TotalMilliseconds;
        partTimes.TryGetValue(what, out var pt);
        partTimes[what] = (pt.sum + ms, Math.Max(pt.worst, ms));
    }

    private static int partFrames, partLogs;
    public static void PerfReport()
    {
        if (++partFrames < 300) return;
        if (partLogs++ < 6 || Autotest.Active)
            ModLog.Info("[perf] parts (avg / worst ms): " + string.Join(", ", partTimes.OrderByDescending(kv => kv.Value.sum)
                .Take(16).Select(kv => $"{kv.Key} {kv.Value.sum / partFrames:F2}/{kv.Value.worst:F1}")));
        partTimes.Clear();
        partFrames = 0;
    }

    // ---------- 1. Recording P1 ----------

    private static void Record()
    {
        if (!recording) return;
        float now = Time.time;
        float t = now - recordStart;

        if (p1Model == null)
        {
            var pr = MyPlayer.Instance != null ? MyPlayer.Instance.playerRenderer : null;
            if (pr == null || pr.rendererObject == null) return;   // P1 not built yet
            p1Model = pr.rendererObject.transform;
            p1Anim = pr.animator;
            ReadP1Params();
        }

        bool open = portal != null && portal.open;
        if (open && p1OpenedAt < 0f)
        {
            p1OpenedAt = t;
            ModLog.Info($"P1 portal open at +{t:F2} s");
        }
        if (p1PassedAt < 0f && portal != null && portal.passingFx != null && portal.passingFx.isPlaying)
        {
            p1PassedAt = t;
            var fx = portal.passingFx.transform;
            p1PassingLocal = portal.transform.InverseTransformPoint(fx.position);
            ModLog.Info($"P1 passed the portal (its passing effect started) at +{t:F2} s; effect at {fx.position} " +
                        $"(portal {portal.transform.position}, P1 {p1Model?.position}), active {fx.gameObject.activeInHierarchy}, scale {fx.lossyScale}");
        }
        if (!open && p1OpenedAt >= 0f && p1ClosedAt < 0f)
        {
            p1ClosedAt = t;
            ModLog.Info($"P1 portal closed at +{t:F2} s");
        }

        var s = new Sample { t = t, pos = p1Model.position, rot = p1Model.rotation };
        if (p1Anim != null)
        {
            if (!paramsBroken)
            {
                s.values = new float[p1Params.Count];
                for (int i = 0; i < p1Params.Count; i++)
                {
                    var p = p1Params[i];
                    s.values[i] = p.type switch
                    {
                        AnimatorControllerParameterType.Float => p1Anim.GetFloat(p.hash),
                        AnimatorControllerParameterType.Int => p1Anim.GetInteger(p.hash),
                        AnimatorControllerParameterType.Bool => p1Anim.GetBool(p.hash) ? 1f : 0f,
                        _ => 0f,
                    };
                }
            }
            var info = p1Anim.GetCurrentAnimatorStateInfo(0);
            s.state = info.shortNameHash;
            s.stateTime = info.normalizedTime;
        }
        samples.Add(s);

        // P2's portal opens Delay seconds after P1's started
        if (p2Portal == null && body == null && t >= Delay) StartP2();

        bool done = (p1ClosedAt >= 0f && t - p1ClosedAt > RecordAfterClose) || t > MaxRecordTime;
        if (done)
        {
            recording = false;
            ModLog.Info($"Recorded P1's entrance: {samples.Count} frames over {t:F2} s (portal open {p1OpenedAt:F2} s, closed {p1ClosedAt:F2} s)");
        }
    }

    // Animator.parameters (array) throws a TypeLoadException in the interop, so read them one by one.
    private static void ReadP1Params()
    {
        if (p1Anim == null) return;
        try
        {
            var names = new List<string>();
            for (int i = 0; i < p1Anim.parameterCount; i++)
            {
                var p = p1Anim.GetParameter(i);
                if (p.type == AnimatorControllerParameterType.Trigger) continue;
                p1Params.Add(new Param { hash = p.nameHash, type = p.type });
                names.Add($"{p.name}({p.type})");
            }
            ModLog.Info($"P1 animator ({p1Anim.runtimeAnimatorController?.name}) parameters: {string.Join(", ", names)}");
        }
        catch (Exception e)
        {
            paramsBroken = true;
            p1Params.Clear();
            ModLog.Warn($"Reading P1 animator parameters failed ({e.GetType().Name}: {e.Message}); P2 will copy P1's animation state instead");
        }
    }

    // ---------- 2. P2's portal and body ----------

    private static void StartP2()
    {
        if (portal == null || samples.Count == 0) return;
        var pr = MyPlayer.Instance != null ? MyPlayer.Instance.playerRenderer : null;
        var data = P2Select.Picked ?? (pr != null ? pr.characterData : null);
        if (data == null || data.prefab == null)
        {
            ModLog.Warn("P2Body: no character data/prefab for P2");
            recording = false;
            return;
        }

        // Sideways: across P1's walking direction, on the camera's right side
        Vector3 walk = portal.desiredPosition - portal.playerStartPosition;
        walk.y = 0f;
        if (walk.sqrMagnitude < 0.0001f) walk = samples[samples.Count - 1].pos - samples[0].pos;
        walk.y = 0f;
        if (walk.sqrMagnitude < 0.0001f) walk = p1Model.forward;
        Vector3 side = Vector3.Cross(Vector3.up, walk.normalized);
        var cam = Camera.main;
        if (cam != null && Vector3.Dot(side, cam.transform.right) < 0f) side = -side;
        // ...and a bit back (against the walking direction) so the two portals don't intersect
        shift = side * PortalSpacing - walk.normalized * PortalBack;

        MakePortal();
        MakeBody(data);
        replaying = true;
        cursor = 0;
        lastState = 0;
        ModLog.Info($"P2 portal opened at {(p2Portal != null ? p2Portal.transform.position.ToString() : "-")} (shift {shift}), P2 body {data.eCharacter} (picked: {P2Select.Picked != null})");
    }

    // P2's portal in P2's red (user): every colour property of the copy's materials (own instances) re-hued to red with
    // its saturation and brightness kept; near-white colours (the colour comes from the texture) get a red tint instead
    private static readonly string[] ColorProps = { "_Color", "_TintColor", "_BaseColor", "_EmissionColor", "_MainColor" };
    // User: red, "a little brighter, or slightly orange" (v0.30.3 was pure red, hue 0)
    // User: brighter still, red/pinkish but more red than pink (was 0.04 orange-red at 1.7)
    // User (after red-pink at hue 0.965): "more pinkish, like a neon pink, but still accents of red" -> the portal's
    // surfaces (Portal, Center, Circle) neon pink, saturated; the sparks/lightning/passing particles stay red
    // v0.30.9, user: "let's try neon red" -> surfaces saturated red, accents a touch towards orange (were pink 0.92 / 0.975)
    private const float PinkHue = 0.99f, AccentHue = 0.02f, PortalBrightness = 2.2f, NeonSaturation = 0.9f;
    private static readonly string[] AccentParts = { "Sparks", "Lightning", "PassingParticles" };
    private static bool IsAccent(string name) => AccentParts.Any(a => name.StartsWith(a));
    private static Color Neon(Color c, string part, bool clamp)
    {
        bool accent = IsAccent(part);
        Color.RGBToHSV(c, out _, out float s, out float v);
        float vv = clamp ? Mathf.Min(1f, v * PortalBrightness) : v * PortalBrightness;
        Color o = s < 0.1f
            ? (accent ? new Color(c.r, c.g * 0.45f, c.b * 0.25f) : new Color(c.r, c.g * 0.2f, c.b * 0.25f)) * (clamp ? 1f : PortalBrightness)
            : Color.HSVToRGB(accent ? AccentHue : PinkHue, accent ? s : Mathf.Max(s, NeonSaturation), vv, true);
        o.a = c.a;
        return o;
    }
    private static int glowLogs;
    public static void TintRed(GameObject root)
    {
        var changed = new List<string>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            foreach (var mat in r.materials)
            {
                if (mat == null) continue;
                // v0.30.2 found none of the usual names: ask the shader for its colour properties
                var props = new List<string>(ColorProps);
                try
                {
                    var sh = mat.shader;
                    int n = sh.GetPropertyCount();
                    var all = new List<string>();
                    for (int i = 0; i < n; i++)
                    {
                        string pn = sh.GetPropertyName(i);
                        var pt = sh.GetPropertyType(i);
                        all.Add($"{pn}:{pt}");
                        if (pt == UnityEngine.Rendering.ShaderPropertyType.Color && !props.Contains(pn)) props.Add(pn);
                    }
                    ModLog.Info($"P2 portal material {r.name}/{mat.name} shader '{sh.name}': {string.Join(", ", all)}");
                }
                catch (Exception e) { ModLog.Info($"P2 portal material {r.name}/{mat.name} shader '{mat.shader?.name}': properties not readable ({e.Message})"); }
                foreach (var prop in props)
                {
                    if (!mat.HasProperty(prop)) continue;
                    mat.SetColor(prop, Neon(mat.GetColor(prop), r.name, clamp: false));
                    changed.Add($"{r.name}/{mat.name}.{prop}");
                }
                // The shader's own glow strength (BasicGlow '_ExtraGlow'), a bit up as well (user: a little brighter)
                if (mat.HasProperty("_ExtraGlow"))
                {
                    float g = mat.GetFloat("_ExtraGlow");
                    mat.SetFloat("_ExtraGlow", g * PortalBrightness);
                    if (glowLogs++ < 3) ModLog.Info($"P2 portal {r.name}/{mat.name}: _ExtraGlow {g:F2} -> {g * PortalBrightness:F2}");
                }
            }
        }
        // Particle colours (start colour) too, in case the look comes from the particles rather than the material
        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (ps == null) continue;
            try
            {
                var main = ps.main;
                // (the interop's MinMaxGradient only offers the single colour)
                var c0 = main.startColor.color;
                if (c0.maxColorComponent < 0.01f) continue;
                main.startColor = new ParticleSystem.MinMaxGradient(Neon(c0, ps.name, clamp: true));
                changed.Add($"{ps.name} particles");
            }
            catch (Exception e) { ModLog.Info($"P2 portal particles {ps.name}: {e.Message}"); }
        }
        ModLog.Info($"P2 portal tinted red: {(changed.Count > 0 ? string.Join(", ", changed) : "no colour properties found")}");
    }

    private static void MakePortal()
    {
        var src = portal.gameObject;
        var holder = new GameObject("MegabonkSplitscreen_P2PortalHolder");
        holder.SetActive(false);
        var copy = Object.Instantiate(src, holder.transform);
        copy.name = "MegabonkSplitscreen_P2Portal";

        // Find our parts before the scripts that point at them are removed
        Transform Part(Component c) => c != null ? P2Select.Mirror(src.transform, c.transform, copy.transform) : null;
        p2PortalRender = Part(portal.portalRender);
        p2AudioOpen = Part(portal.audioOpen)?.GetComponent<AudioSource>();
        p2AudioLoop = Part(portal.audioLoop)?.GetComponent<AudioSource>();
        p2AudioClose = Part(portal.audioClose)?.GetComponent<AudioSource>();
        p2AudioPassing = Part(portal.audioPassing)?.GetComponent<AudioSource>();
        p2PassingFx = Part(portal.passingFx)?.GetComponent<ParticleSystem>();
        if (p2PassingFx != null) p2PassingFx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        if (portal.blockObjectSpawns != null)
        {
            var block = P2Select.Mirror(src.transform, portal.blockObjectSpawns.transform, copy.transform);
            if (block != null && block != copy.transform) Object.DestroyImmediate(block.gameObject);
        }

        int removed = 0;
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb.GetIl2CppType().Assembly.GetName().Name != "Assembly-CSharp") continue;
            // v0.49.5 (user: P1 gets dust when jumping/landing, P2 none): the model's AnimationFootstepFx stays - the
            // animation's foot events (LeftFoot/RightFoot) spawn the dust at the copy's own feet
            if (mb.GetIl2CppType().Name == "AnimationFootstepFx") continue;
            Object.DestroyImmediate(mb);
            removed++;
        }
        LogMovementListeners();

        TintRed(copy);
        copy.transform.SetParent(src.transform.parent, true);
        copy.transform.SetPositionAndRotation(src.transform.position + shift, src.transform.rotation);
        Object.Destroy(holder);

        p2PortalScale = portal.portalScale.sqrMagnitude > 0.0001f ? portal.portalScale
            : (p2PortalRender != null ? p2PortalRender.localScale : Vector3.one);
        if (p2PortalRender != null) p2PortalRender.localScale = Vector3.zero;
        copy.SetActive(true);
        p2Portal = copy;
        portalOpenStart = Time.time;
        portalCloseStart = -1f;
        portalCloseScheduled = false;
        if (p2AudioOpen != null) p2AudioOpen.Play();
        if (p2AudioLoop != null) p2AudioLoop.Play();

        var children = new List<string>();
        for (int i = 0; i < copy.transform.childCount; i++) children.Add(copy.transform.GetChild(i).name);
        ModLog.Info($"P2 portal copy: {removed} game scripts removed, render '{p2PortalRender?.name}' scale {p2PortalScale}, " +
                    $"children [{string.Join(", ", children)}]");
    }

    // TEMP: the game scripts (Assembly-CSharp) on a model, i.e. anything that could react to the game's events
    private static void LogScripts(GameObject root, string what)
    {
        var names = new List<string>();
        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            var t = mb.GetIl2CppType();
            if (t.Assembly.GetName().Name == "Assembly-CSharp") names.Add($"{t.Name} on '{mb.name}'");
        }
        ModLog.Info($"[body] {what}: {names.Count} game script(s){(names.Count > 0 ? ": " + string.Join(", ", names) : "")}");
    }

    private static bool bodyVisible = true;
    private static readonly List<Renderer> hiddenRenderers = new();   // only the ones that were on (some parts start off)
    private static void SetBodyVisible(bool on)
    {
        bodyVisible = on;
        if (body == null) return;
        if (!on)
        {
            hiddenRenderers.Clear();
            foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                    hiddenRenderers.Add(r);
                }
        }
        else
        {
            foreach (var r in hiddenRenderers) if (r != null) r.enabled = true;
            hiddenRenderers.Clear();
        }
    }

    private static SkatingAnimations skating;
    private static bool skatingLinked;
    // True while P2 is Calcium and the game's skating script animates P2 (P2Control then leaves the animator alone)
    public static Animator Anim => anim;
    public static bool GameAnimates => skating != null && skating.enabled;

    // User: P2 kept animating while a menu paused the game. The game's menus pause its own clock (MyTime.paused), not
    // Unity's, so P2's animator is held at speed 0 for the pause and given its speed back after
    private static float pausedAnimSpeed = -1f;
    private static void FreezeWhilePaused()
    {
        if (anim == null) return;
        bool paused = Assets.Scripts.Utility.MyTime.paused;
        if (paused && pausedAnimSpeed < 0f) { pausedAnimSpeed = anim.speed; anim.speed = 0f; }
        else if (!paused && pausedAnimSpeed >= 0f) { anim.speed = pausedAnimSpeed; pausedAnimSpeed = -1f; }
    }

    // P2's hover bob while P2 is TonyMcZoom (P1's HoverAnimations' offset from its parent, in world space)
    private static HoverAnimations p1Hover;
    public static Vector3 HoverOffset
    {
        get
        {
            if (p1Hover == null || p1Hover.transform.parent == null) return Vector3.zero;
            return p1Hover.transform.parent.TransformVector(p1Hover.transform.localPosition);
        }
    }

    // TEMP [hover]: once a second (8 times), where P2's HoverAnimations object, P2's body and P2's movement are
    private static float nextHoverLog;
    private static int hoverLogs;
    private static void WatchHover()
    {
        if (body == null || hoverLogs >= 8 || Time.time < nextHoverLog || P2Movement.Pm == null) return;
        var h = body.GetComponentInChildren<HoverAnimations>(true);
        if (h == null) return;
        nextHoverLog = Time.time + 1f;
        hoverLogs++;
        var pr = MyPlayer.Instance?.playerRenderer?.rendererObject?.transform;
        ModLog.Info($"[hover] P2's HoverAnimations '{h.name}' at {h.transform.position} (enabled {h.enabled}, player '{h.player?.name}'); P2 body {body.transform.position}, P2 movement {P2Movement.Pm.transform.position}; P1 model {pr?.position}");
    }

    // Each frame: the skating script uses P2's movement copy and P2's animator, never P1's
    private static void LinkSkating()
    {
        if (skating == null) return;
        var pm = P2Movement.Pm;
        if (pm == null) return;
        if (skating.playerMovement == null || skating.playerMovement.Pointer != pm.Pointer) skating.playerMovement = pm;
        if (anim != null && (skating.animator == null || skating.animator.Pointer != anim.Pointer)) skating.animator = anim;
        if (!skatingLinked)
        {
            skatingLinked = true;
            ModLog.Info("[calcium] P2's SkatingAnimations now follows P2's movement copy");
        }
        WatchSkating();
    }

    // TEMP (user: P2 Calcium's animations seem broken): P2's state and the skating script's choice next to P1's, when
    // either changes; and whether the script turns its own object (P2's: the body root, which the mod also turns)
    private static string lastSkate = "";
    private static int skateLogs;
    private static Quaternion skateRotAfterMod;
    private static void WatchSkating()
    {
        if (skateLogs >= 80 || anim == null) return;
        var p1 = MyPlayer.Instance;
        var p1Model = p1?.playerRenderer?.rendererObject;
        var p1Skate = p1Model != null ? p1Model.GetComponent<SkatingAnimations>() : null;
        var p1Anim = p1?.playerRenderer?.animator;
        Vector3 v2 = P2Movement.Velocity();
        Vector3 v1 = PlayerMovement.Instance?.rb != null ? PlayerMovement.Instance.rb.velocity : Vector3.zero;
        string now = $"P2 state {anim.GetCurrentAnimatorStateInfo(0).shortNameHash} '{skating.lastAnimationName}' anim speed {anim.speed:F2} | " +
                     $"P1 state {(p1Anim != null ? p1Anim.GetCurrentAnimatorStateInfo(0).shortNameHash : 0)} '{p1Skate?.lastAnimationName}' anim speed {(p1Anim != null ? p1Anim.speed : 0f):F2}";
        if (now == lastSkate) return;
        lastSkate = now;
        skateLogs++;
        float turned = Quaternion.Angle(skateRotAfterMod, skating.transform.rotation);
        ModLog.Info($"[calcium] {now}; P2 flat speed {new Vector2(v2.x, v2.z).magnitude:F1} grounded {P2Movement.Grounded()}, P1 flat speed {new Vector2(v1.x, v1.z).magnitude:F1}; " +
                    $"P2 script on '{skating.name}' (body root {skating.transform == body.transform}), turned {turned:F1} deg since the mod set it; P1 script on '{p1Skate?.name}'");
    }

    // P2's shield look (user: compare the VFX with P1's; autotest screenshot: P1's model has a cyan outline while P1
    // has shield - PlayerRenderer.ChangeShield switches its Outline with shieldColor). P2's model gets its own Outline
    // with P1's mode/width and shield colour, on while P2's shield is up.
    private static Outline p2Outline;
    private static int outlineLogs;

    public static void HideShieldOutline()
    {
        if (p2Outline != null && p2Outline.enabled) p2Outline.enabled = false;
    }

    private static void UpdateShieldOutline()
    {
        if (body == null || !P2Movement.Active) return;
        var pr = MyPlayer.Instance?.playerRenderer;
        if (pr == null) return;
        bool want = P2Health.Shield > 0.5f && !P2Health.IsDead && !P2Health.Flashing;
        if (p2Outline == null)
        {
            if (!want) return;
            var src = pr.outline;
            p2Outline = body.AddComponent<Outline>();
            if (src != null)
            {
                p2Outline.OutlineMode = src.OutlineMode;
                p2Outline.OutlineWidth = src.OutlineWidth;
            }
            p2Outline.OutlineColor = pr.shieldColor;
            // v0.41.5 (autotest screenshot: no outline on P2): its two materials may not load when the mod adds it -
            // P1's are copied, P2's own renderers set, and it is switched off and on so it applies them
            bool maskMissing = p2Outline.outlineMaskMaterial == null, fillMissing = p2Outline.outlineFillMaterial == null;
            if (src != null)
            {
                if (maskMissing && src.outlineMaskMaterial != null) p2Outline.outlineMaskMaterial = Object.Instantiate(src.outlineMaskMaterial);
                if (fillMissing && src.outlineFillMaterial != null) p2Outline.outlineFillMaterial = Object.Instantiate(src.outlineFillMaterial);
            }
            // v0.44.4 (user: the shield also outlined P2's aura): P2's character only, like the hit flash
            p2Outline.renderers = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Renderer>(
                body.GetComponentsInChildren<Renderer>(true).Where(r => r != null && P2Health.IsCharacterRenderer(r)).ToArray());
            p2Outline.enabled = false;
            p2Outline.enabled = true;
            p2Outline.needsUpdate = true;
            if (outlineLogs++ < 3)
                ModLog.Info($"[vfx] P2 shield outline made: P1's outline {(src != null ? $"mode {src.OutlineMode}, width {src.OutlineWidth:F1}, colour {src.OutlineColor}, enabled {src.enabled}, on '{src.name}' ({src.renderers?.Length} renderers)" : "none")}, " +
                            $"shield colour {pr.shieldColor}; P2's materials missing: mask {maskMissing}, fill {fillMissing}; P2 renderers {p2Outline.renderers?.Length}");
        }
        if (p2Outline.enabled != want)
        {
            p2Outline.OutlineColor = pr.shieldColor;
            p2Outline.enabled = want;
            if (outlineLogs++ < 8) ModLog.Info($"[vfx] P2 shield outline {(want ? "on" : "off")} (P2 shield {P2Health.Shield:F0}; P1's outline enabled {pr.outline?.enabled}, P1 shieldActive {pr.shieldActive})");
        }
        if (Time.unscaledTime >= nextOutlineCheck) CheckOutlines(pr);
    }

    // v0.49.1 (user: P2's Megachad showed two shields after the Shield tome, fixed itself later; 'outlines' test: the
    // game makes P1's model outline ('Chad', width 6) only once P1's shield first shows - a P2 model copied after that
    // carries a second Outline, and P2's own one was made with defaults (width 2) when P1 had none yet): once a second,
    // P2's body keeps only the mod's outline, which takes P1's mode and width once P1's exists
    private static float nextOutlineCheck;
    private static bool outlineStyled;
    private static void CheckOutlines(PlayerRenderer pr)
    {
        nextOutlineCheck = Time.unscaledTime + 1f;
        if (body == null || p2Outline == null) return;
        foreach (var o in body.GetComponentsInChildren<Outline>(true))
        {
            if (o == null || o.Pointer == p2Outline.Pointer) continue;
            if (outlineLogs++ < 12) ModLog.Info($"[vfx] a second outline on P2 ('{o.name}', enabled {o.enabled}) - removed (P2's own shield outline stays)");
            Object.Destroy(o);
        }
        var src = pr.outline;
        if (!outlineStyled && src != null)
        {
            p2Outline.OutlineMode = src.OutlineMode;
            p2Outline.OutlineWidth = src.OutlineWidth;
            p2Outline.needsUpdate = true;
            outlineStyled = true;
            ModLog.Info($"[vfx] P2 shield outline styled like P1's: mode {src.OutlineMode}, width {src.OutlineWidth:F1}");
        }
    }

    // Called by P2Control right after it sets P2's rotation
    public static void NoteRotationSet() { if (body != null) skateRotAfterMod = body.transform.rotation; }

    // P2's skin from character select (user: P2 can't select skins): the skin's materials on the model's skinned
    // renderer - the one the avatar's PlayerRenderer.SetSkin used (by name), else the first - with its own copies
    private static void ApplySkin(GameObject root, SkinData skin)
    {
        if (skin == null || skin.materials == null || skin.materials.Length == 0) return;
        SkinnedMeshRenderer target = null;
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            if (target == null) target = smr;
            if (smr.name == P2Select.SkinRendererName) { target = smr; break; }
        }
        if (target == null)
        {
            ModLog.Warn($"P2 skin: P2's body has no skinned renderer for {skin.name}");
            return;
        }
        target.materials = skin.materials;
        target.materials = target.materials;   // own copies
        ModLog.Info($"P2 skin: {skin.name} on P2's body ('{target.name}', {skin.materials.Length} materials)");
    }

    private static void MakeBody(CharacterData data)
    {
        body = Object.Instantiate(data.prefab);
        body.name = "MegabonkSplitscreen_P2Body";
        ApplySkin(body, P2Select.SkinFor(data));
        LogScripts(body, "P2 body (prefab copy)");
        if (p1Model != null) LogScripts(p1Model.gameObject, "P1 model");
        // TEMP (user's idea, crash hunt): every object + component of P2's body, next to P1's model (same kind of
        // prefab, created by the game's PlayerRenderer under the real player)
        UiDump.DumpOnce("p2body_dump.txt", body.transform, p1Model);
        P2Camera.Start(body);   // splitscreen: P2's camera on the right half from the moment P2 appears
        body.transform.localScale = p1Model.lossyScale;
        body.transform.SetPositionAndRotation(samples[0].pos + shift, samples[0].rot);
        // Hidden until P2's portal is fully open (user saw P2 standing there before the opening portal covered him)
        SetBodyVisible(false);

        var anims = body.GetComponentsInChildren<Animator>(true);
        anim = anims.Length > 0 ? anims[0] : null;

        // Calcium only (user): his skating animation is the game's own SkatingAnimations script on the model (v0.38.0
        // [calcium] log - his states change with speed/turns, not with the mod's run/idle). It stays on P2's copy, and
        // is pointed at P2's movement (its Awake took "the player's", P1's); the mod's own animation code stands back.
        skating = data.eCharacter == ECharacter.Calcium ? body.GetComponentInChildren<SkatingAnimations>(true) : null;
        skatingLinked = false;
        if (data.eCharacter != ECharacter.Calcium)
            foreach (var sa in body.GetComponentsInChildren<SkatingAnimations>(true)) sa.enabled = false;
        if (skating != null) ModLog.Info($"[calcium] P2 is Calcium: the game's SkatingAnimations drives P2's animation (animator {(skating.animator != null ? "set" : "none")})");
        // v0.44.13 (user: the hoverboard guy - TonyMcZoom - doesn't render right for P2; [hover] log): the game's
        // HoverAnimations sets the local position (bob) and rotation (lean) of the object it sits on. On P1 that's the
        // model under the player; on P2's copy it's P2's body root (no parent), so every frame it put P2's whole body
        // back at P2's spawn spot. P2's copy is switched off; P2's body gets the hover bob (P1's script's offset - the
        // same sine for both) on top of P2's position (P2Control). Every other character: no hover script at all.
        p1Hover = null;
        foreach (var h in body.GetComponentsInChildren<HoverAnimations>(true)) h.enabled = false;
        if (data.eCharacter == ECharacter.TonyMcZoom)
            foreach (var h in Object.FindObjectsOfType<HoverAnimations>(true))
                if (h != null && !h.transform.IsChildOf(body.transform)) p1Hover = h;
        // TEMP [hover]: what the game's HoverAnimations on P1's and P2's models point at
        foreach (var h in Object.FindObjectsOfType<HoverAnimations>(true))
        {
            if (h == null) continue;
            bool mine = h.transform.IsChildOf(body.transform);
            var t = h.transform;
            ModLog.Info($"[hover] {(mine ? "P2's" : "P1's")} HoverAnimations on '{t.name}' (parent '{t.parent?.name}'): player '{h.player?.name}' at {h.player?.position}, " +
                        $"own pos {t.position} local {t.localPosition}, defaultPos {h.defaultPos}, defaultRotation {h.defaultRotation}, height {h.height:F2}, enabled {h.enabled}, animator {(h.animator != null ? h.animator.name : "none")}");
        }
        if (anim != null && anim.runtimeAnimatorController == null && p1Anim != null)
            anim.runtimeAnimatorController = p1Anim.runtimeAnimatorController;

        // Only copy parameters P2's own controller has (each character has its own controller)
        p2ParamHashes.Clear();
        if (anim != null && !paramsBroken)
        {
            try
            {
                var names = new List<string>();
                for (int i = 0; i < anim.parameterCount; i++)
                {
                    var p = anim.GetParameter(i);
                    p2ParamHashes.Add(p.nameHash);
                    names.Add($"{p.name}({p.type})");
                }
                ModLog.Info($"P2 animator ({anim.runtimeAnimatorController?.name}) parameters: {string.Join(", ", names)}");
            }
            catch (Exception e)
            {
                paramsBroken = true;
                ModLog.Warn($"Reading P2 animator parameters failed: {e.Message}");
            }
        }
    }

    // ---------- 3. Replaying P1's recording on P2 ----------

    private static void Replay()
    {
        if (!replaying || body == null) return;
        // The moment in P1's recording P2 is at. The replay starts once P2's portal is fully open (P1's portal was
        // already open when P1's recording began); until then P2 stays hidden at the start.
        float t = Time.time - recordStart - Delay - PortalScaleTime;
        if (t < 0f) return;
        // v0.49.3 (user: P2's model could be seen behind the portal): P1 walks out from behind its portal, which covers
        // it; P2's is shown from the moment P1 passed through (the recorded passing point) - before that it's behind
        bool show = p1PassedAt < 0f || t >= p1PassedAt - 0.05f;
        if (bodyVisible != show)
        {
            SetBodyVisible(show);
            ModLog.Info($"[portal] P2's model {(show ? "shown" : "hidden")} at +{t:F2} s of the entrance (P1 passed its portal at +{p1PassedAt:F2} s)");
        }

        while (cursor + 1 < samples.Count && samples[cursor + 1].t <= t) cursor++;
        var a = samples[cursor];
        bool atEnd = cursor + 1 >= samples.Count;
        if (!atEnd)
        {
            var b = samples[cursor + 1];
            float k = Mathf.InverseLerp(a.t, b.t, t);
            body.transform.SetPositionAndRotation(Vector3.Lerp(a.pos, b.pos, k) + shift, Quaternion.Slerp(a.rot, b.rot, k));
        }
        else
        {
            body.transform.SetPositionAndRotation(a.pos + shift, a.rot);
        }
        ApplyAnimation(a);

        // P2 passes through: the portal's passing effect, like P1's
        if (!p2Passed && p1PassedAt >= 0f && t >= p1PassedAt)
        {
            p2Passed = true;
            if (p2PassingFx != null && p2Portal != null)
            {
                // Same spot relative to P2's portal as P1's had; switched on (the game may turn P1's on to play it)
                var fx = p2PassingFx.transform;
                fx.position = p2Portal.transform.TransformPoint(p1PassingLocal);
                for (var x = fx; x != null && x != p2Portal.transform.parent; x = x.parent)
                    if (!x.gameObject.activeSelf) x.gameObject.SetActive(true);
                p2PassingFx.Play(true);
            }
            if (p2AudioPassing != null) p2AudioPassing.Play();
            ModLog.Info($"P2 passed the portal: passing effect {(p2PassingFx != null ? $"played at {p2PassingFx.transform.position}, active {p2PassingFx.gameObject.activeInHierarchy}, playing {p2PassingFx.isPlaying}, scale {p2PassingFx.transform.lossyScale}" : "not found")}");
        }

        // Close P2's portal at the point where P1's closed
        if (!portalCloseScheduled && p1ClosedAt >= 0f && t >= p1ClosedAt)
        {
            portalCloseScheduled = true;
            portalCloseStart = Time.time;
            if (p2AudioLoop != null) p2AudioLoop.Stop();
            if (p2AudioClose != null) p2AudioClose.Play();
        }

        if (atEnd && !recording)
        {
            replaying = false;
            if (anim != null)
            {
                anim.Rebind();   // back to the controller's default state (idle)
                anim.speed = 1f;
            }
            if (!portalCloseScheduled)
            {
                portalCloseScheduled = true;
                portalCloseStart = Time.time;
            }
            ModLog.Info($"P2 entrance done, standing at {body.transform.position}");
            P2Control.Take(body, anim);
        }
    }

    private static void ApplyAnimation(Sample s)
    {
        if (anim == null || anim.runtimeAnimatorController == null) return;
        if (!paramsBroken && s.values != null)
        {
            for (int i = 0; i < p1Params.Count && i < s.values.Length; i++)
            {
                var p = p1Params[i];
                if (!p2ParamHashes.Contains(p.hash)) continue;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: anim.SetFloat(p.hash, s.values[i]); break;
                    case AnimatorControllerParameterType.Int: anim.SetInteger(p.hash, (int)s.values[i]); break;
                    case AnimatorControllerParameterType.Bool: anim.SetBool(p.hash, s.values[i] > 0.5f); break;
                }
            }
            return;
        }
        // Fallback: follow P1's state changes
        if (s.state == 0 || s.state == lastState) return;
        lastState = s.state;
        if (anim.HasState(0, s.state)) anim.CrossFade(s.state, 0.1f, 0, s.stateTime % 1f);
    }

    // ---------- Portal open/close animation ----------

    // TEMP (user: jump/land dust missing on P2): who listens to the game's movement events (P2's are routed to P2's
    // own listeners, so a P1 effect script here would explain it) - names logged once, not guessed
    private static bool movementListenersLogged;
    private static void LogMovementListeners()
    {
        if (movementListenersLogged) return;
        movementListenersLogged = true;
        var parts = new List<string>();
        foreach (var p in typeof(PlayerMovement).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (!typeof(Il2CppSystem.Delegate).IsAssignableFrom(p.PropertyType)) continue;
            Il2CppSystem.Delegate d = null;
            try { d = p.GetValue(null) as Il2CppSystem.Delegate; } catch { }
            var who = d == null ? "none" : string.Join(", ", d.GetInvocationList().Select(l => { try { return $"{l.Method?.DeclaringType?.Name}.{l.Method?.Name}"; } catch { return "?"; } }));
            parts.Add($"{p.Name}: {who}");
        }
        ModLog.Info($"[dust] PlayerMovement event listeners: {string.Join("; ", parts)}");
    }

    private static void AnimatePortal()
    {
        if (p2Portal == null) return;
        float now = Time.time;
        if (portalCloseStart >= 0f)
        {
            float k = Mathf.Clamp01((now - portalCloseStart) / PortalScaleTime);
            if (p2PortalRender != null) p2PortalRender.localScale = Vector3.Lerp(p2PortalScale, Vector3.zero, k);
            if (k >= 1f && (p2AudioClose == null || !p2AudioClose.isPlaying))
            {
                Object.Destroy(p2Portal);
                p2Portal = null;
                ModLog.Info("P2 portal closed");
            }
        }
        else if (portalOpenStart >= 0f && p2PortalRender != null)
        {
            float k = Mathf.Clamp01((now - portalOpenStart) / PortalScaleTime);
            p2PortalRender.localScale = Vector3.Lerp(Vector3.zero, p2PortalScale, k);
        }
    }

    // New stage / new run: drop the previous P2 body and portal
    private static void Cleanup()
    {
        P2Control.Release();
        P2Camera.Stop();
        P2Minimap.Stop();
        P2Interact.Stop();
        if (p2Portal != null) Object.Destroy(p2Portal);
        if (body != null) Object.Destroy(body);
        p2Portal = null;
        body = null;
        anim = null;
        skating = null;
        p2Outline = null;
        outlineStyled = false;
        replaying = false;
        portalOpenStart = portalCloseStart = -1f;
    }
}
