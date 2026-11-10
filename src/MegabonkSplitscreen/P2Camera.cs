using Rewired;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Left/right splitscreen (user's choice): P1 on the left half, P2 on the right half, HUD unchanged (full screen).
//  - P1: the game's own camera (PlayerCamera.Instance.camera) keeps working as before, only its viewport becomes the
//    left half (Camera.rect).
//  - P2: a copy of P1's camera object (same render settings / post-processing), made inert like the other copies:
//    built under an inactive holder, game scripts and the AudioListener removed, child cameras removed, untagged
//    (so Camera.main stays P1's). Viewport = right half.
//  - Following P2: every frame the mod measures where P1's camera sits relative to P1's head, in camera space
//    (so zoom and bob carry over), and puts P2's camera at the same relative spot around P2's head, turned by
//    P2's yaw/pitch.
//  - P2's right stick turns P2's camera. No wall collision yet.
internal static class P2Camera
{
    private const float YawSpeed = 160f;     // degrees per second at full stick
    private const float PitchSpeed = 110f;
    private const float MinPitch = -35f, MaxPitch = 70f;
    private const float DeadZone = 0.2f;

    public static Camera Cam { get; private set; }
    public static float Yaw => yaw;
    public static float Pitch => pitch;

    private static Camera p1Cam;
    private static Transform body;
    private static float yaw, pitch;
    private static int rightXId = 2, rightYId = 3;
    private static bool learnedX, learnedY;

    public static void Start(GameObject p2Body)
    {
        body = p2Body.transform;
        var pc = PlayerCamera.Instance;
        p1Cam = pc != null ? pc.camera : null;
        if (p1Cam == null)
        {
            ModLog.Warn("P2Camera: P1's camera not found");
            return;
        }
        if (Cam != null) return;   // same stage, already made
        try
        {
            UiDump.DumpOnce("camera_dump.txt", p1Cam.transform);
            Cam = CopyCamera("MegabonkSplitscreen_P2Camera", out int removed);
            // P1's own view on the left while the shared view opens in the middle (merge/split transition only)
            sideCam = CopyCamera("MegabonkSplitscreen_P1SideCamera", out _);
            sideCam.enabled = false;
            // User: a run should open merged (the players start next to each other). The full screen stays P1's
            // (the game's portal camera plays as usual); the shared pose eases in once the entrance is over.
            if (mergeEnabled)
            {
                merge = 1f;
                wantMerged = true;
                poseWeight = 0f;
                Cam.enabled = false;
                p1Cam.rect = new Rect(0f, 0f, 1f, 1f);
            }
            else
            {
                Cam.rect = new Rect(0.5f, 0f, 0.5f, 1f);
                p1Cam.rect = new Rect(0f, 0f, 0.5f, 1f);
            }

            // Start looking where P1 looks
            var e = p1Cam.transform.rotation.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180f ? e.x - 360f : e.x;
            ModLog.Info($"P2 camera made ({removed} game scripts removed), split left/right; P1 camera offset3rdPerson {pc.offset3rdPerson}");
        }
        catch (Exception ex)
        {
            ModLog.Error($"P2 camera failed: {ex}");
        }
    }

    // An inert copy of P1's camera object (same render settings / post-processing): built under an inactive holder,
    // game scripts, AudioListener and child cameras removed, untagged (Camera.main stays P1's)
    private static Camera sideCam;

    private static Camera CopyCamera(string name, out int removed)
    {
        removed = 0;
        var holder = new GameObject(name + "Holder");
        holder.SetActive(false);
        var copy = Object.Instantiate(p1Cam.gameObject, holder.transform);
        copy.name = name;
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb.GetIl2CppType().Assembly.GetName().Name != "Assembly-CSharp") continue;
            Object.DestroyImmediate(mb);
            removed++;
        }
        foreach (var al in copy.GetComponentsInChildren<AudioListener>(true)) Object.DestroyImmediate(al);
        var main = copy.GetComponent<Camera>();
        foreach (var c in copy.GetComponentsInChildren<Camera>(true))
            if (c != null && c.Pointer != main.Pointer) Object.DestroyImmediate(c.gameObject);
        copy.tag = "Untagged";
        copy.transform.SetParent(null, true);
        Object.Destroy(holder);
        copy.SetActive(true);
        return main;
    }

    // ---------- Lego-style merge (user's choice) ----------
    // Close together (< MergeDistance): one shared view behind the midpoint of both players, pulled back to fit both,
    // full screen; either right stick turns it (P2's stick is added to P1's PlayerInput camera rotation, so both players
    // move relative to that view). Apart (> SplitDistance): the left/right split as before.
    // Smooth without a shader (the game can't compile new ones): over TransitionTime both cameras glide to the shared
    // pose while the divider slides to the right edge (merging), or the reverse (splitting).
    private static float mergeDistance = 24f, splitDistance = 30f;
    private const float OldSplitDistance = 15f;   // before v1.0.0-alpha: the close-up pull-back and the height limit
    private const float FitMargin = 3f;           // metres around the players in the shared view
    private const float TransitionTime = 0.6f;
    private static bool mergeEnabled = true;
    private static BepInEx.Configuration.ConfigEntry<bool> mergeEntry;

    // User: two camera modes - split screen all the time, or dynamic (merged while close, as before) - switched in the
    // pause menu and kept across sessions (the mod's config file is saved when the value is set)
    public static bool MergeEnabled
    {
        get => mergeEnabled;
        set
        {
            mergeEnabled = value;
            if (mergeEntry != null) mergeEntry.Value = value;
            ModLog.Info($"[cam] camera mode: {(value ? "dynamic (merged while close)" : "split screen all the time")} - saved");
        }
    }
    private static bool wantMerged;
    private static float merge;          // 0 = split .. 1 = merged (linear; smoothed when used)
    public static float Divider { get; private set; } = 0.5f;   // P1/P2 viewport border, 0.5 (split) .. 1 (merged)
    public static bool Merged => merge >= 0.999f;
    private static float lookX, lookY;   // P2's right stick this frame (degrees), for the shared view

    public static void Configure(BepInEx.Configuration.ConfigFile config)
    {
        mergeEntry = config.Bind("Camera", "MergeWhenClose", true,
            "Lego-style camera: one shared screen while the players are close, splitting when they move apart. " +
            "False = split screen all the time. Also switched in the pause menu (CAMERA button), saved here.");
        mergeEnabled = mergeEntry.Value;
        // v1.0.0-alpha (user: split at about 4x the distance): 15 -> 60, 12 -> 48. v1.0.4 (player: the shared screen
        // doesn't split soon enough): 60 -> 30, 48 -> 24 (40/32 tried first, user: a little more). A config still
        // holding an old default gets the new one; a value the player set stays.
        var splitEntry = config.Bind("Camera", "SplitDistance", 30f,
            "Distance between the players at which the shared screen splits.");
        var mergeDistEntry = config.Bind("Camera", "MergeDistance", 24f,
            "Distance at which the split screens merge again (a bit below SplitDistance so it doesn't flicker).");
        foreach (var old in new[] { 15f, 60f, 40f }) if (Mathf.Approximately(splitEntry.Value, old)) splitEntry.Value = 30f;
        foreach (var old in new[] { 12f, 48f, 32f }) if (Mathf.Approximately(mergeDistEntry.Value, old)) mergeDistEntry.Value = 24f;
        splitDistance = splitEntry.Value;
        mergeDistance = mergeDistEntry.Value;
        if (mergeDistance >= splitDistance) mergeDistance = splitDistance * 0.8f;
        ModLog.Info($"P2Camera: merge when close {mergeEnabled} (merge < {mergeDistance:F1}, split > {splitDistance:F1})");
    }

    // Called every frame (from P2Body.Update): P2's right stick. The cameras are placed in LateUpdate.
    public static void Update()
    {
        if (Cam == null || body == null || p1Cam == null) return;
        lookX = lookY = 0f;
        if (Time.timeScale > 0f) ReadLook(Time.unscaledDeltaTime);
    }

    // Called from Ticker.LateUpdate: after the game's PlayerCamera.Update has placed P1's camera this frame
    public static void LateUpdate()
    {
        if (Cam == null || body == null || p1Cam == null) return;
        var pc = PlayerCamera.Instance;
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        var p1Model = p1?.playerRenderer?.rendererObject?.transform;
        if (pc == null || p1Model == null) return;

        // Where P1's camera is relative to P1's head, in P1's camera space
        Vector3 p1Head = pc.GetPlayerHeadPosition();
        var p1Rot = p1Cam.transform.rotation;
        // v0.26.0: measured every frame, this fed back on itself (the game moves its camera smoothly from where it is, so
        // last frame's pulled-back shared pose leaked into the offset, which was pulled back again...) and the camera
        // flew off. Measured only while nothing overrides P1's camera (fully split); the last clean value is kept.
        // v0.26.2: the split views were shifted after a merge: the game moves its camera relative to where it is, so the
        // shared pose stayed in it. Now the game's camera is put back at the end of every frame (RestoreAfterRender), so
        // here it's always the game's own pose and can be measured every frame.
        // v0.33.1, from the [camvec] log: near a wall the game shrinks its camera distance (currentZ 12 -> 5.5 -> 3.7)
        // while its intended distance Get3rdPersonOffset() stays ~12.25 - so the free offset is P1's with its depth
        // scaled back to Get3rdPersonOffset() (user: P1's wall still pulled P2's camera in). Then smoothed (~0.15 s) so
        // P1's camera shake doesn't reach P2's (user), while zoom changes still come through.
        Vector3 raw = Quaternion.Inverse(p1Rot) * (p1Cam.transform.position - p1Head);
        Vector3 measuredRaw = raw;
        float free = 0f, cz = 0f;
        try { free = pc.Get3rdPersonOffset(); cz = pc.currentZ; } catch { }
        // v0.44.4 ([camwall2] log, user: P1's wall still pulled P2's camera in): the wall shortens the depth by exactly
        // (intended - currentZ), down to under 0.5 m against a wall - where the old ratio (only above 0.5) switched off
        // and P2's camera dropped from 12.2 to 8.8 m. The difference is added back instead.
        if (free > 0.5f && free > cz + 0.05f) raw.z -= free - Mathf.Max(0f, cz);
        smoothLocal = haveLocal ? Vector3.Lerp(smoothLocal, raw, 1f - Mathf.Exp(-7f * Time.unscaledDeltaTime)) : raw;
        cleanLocal = smoothLocal;
        LogCamVec(raw, free, cz, pc);
        haveLocal = true;
        gamePos = p1Cam.transform.position;
        Vector3 local = cleanLocal;
        // P2's head (head height measured from P1)
        Vector3 p2Head = body.position + (p1Head - p1Model.position);

        UpdateMerge(pc, p1Head, p2Head);
        float s = Mathf.SmoothStep(0f, 1f, merge);

        // Shared turning while merging/merged: P2's stick turns P1's (= the shared) camera; P2 looks where it looks
        var e = p1Rot.eulerAngles;
        float p1Yaw = e.y, p1Pitch = e.x > 180f ? e.x - 360f : e.x;
        if (wantMerged || s > 0f)
        {
            if ((lookX != 0f || lookY != 0f) && !P2Health.IsDead) TurnP1Camera(lookX, -lookY);   // a downed P2 doesn't turn it
            yaw = Mathf.LerpAngle(yaw, p1Yaw, Mathf.Max(s, wantMerged ? 0.15f : 0f));
            pitch = Mathf.Lerp(pitch, p1Pitch, Mathf.Max(s, wantMerged ? 0.15f : 0f));
            if (s >= 0.999f) { yaw = p1Yaw; pitch = p1Pitch; }
        }

        // P2's own follow pose
        var p2Rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 p2Pos = AvoidWalls(p2Head, p2Head + p2Rot * local, pc);
        P1CamDistance = measuredRaw.magnitude;
        P2CamDistance = (p2Pos - p2Head).magnitude;
        LogWallState(measuredRaw, raw, free, cz, pc, p1Head, p1Rot);

        // The shared pose: behind the midpoint, pulled back to fit both - or behind the one alive player while the other
        // is down (Solo: 1 = only P1, 2 = only P2)
        Vector3 mid = Solo == 1 ? p1Head : Solo == 2 ? p2Head : (p1Head + p2Head) * 0.5f;
        float sep = Solo != 0 ? 0f : Vector3.Distance(new Vector3(p1Head.x, 0f, p1Head.z), new Vector3(p2Head.x, 0f, p2Head.z));
        // Close: the old gentle pull-back (x1.6 at 15 m). Further (the split is at 60 m since v1.0.0-alpha): back as far
        // as the screen's horizontal field of view needs to keep both players in, with a margin (side by side is the
        // worst case)
        float pull = 1f + 0.6f * Mathf.Clamp01(sep / OldSplitDistance);
        float localLen = local.magnitude;
        if (localLen > 0.1f)
        {
            float halfV = p1Cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            float tanHalfH = Mathf.Tan(halfV) * aspect;
            pull = Mathf.Max(pull, (sep * 0.5f + FitMargin) / (tanHalfH * 0.85f) / localLen);
        }
        Vector3 sharedPos = AvoidWalls(mid, mid + p1Rot * (local * pull), pc);
        // Outside normal play (the portal entrance) the shared view is the game's own camera; it eases to the midpoint
        // view afterwards (and back if the game takes its camera over again)
        bool playing = SafeState(pc) == PlayerCamera.ECameraState.Player3rd;
        poseWeight = Mathf.MoveTowards(poseWeight, playing ? 1f : 0f, Time.unscaledDeltaTime / TransitionTime);
        sharedPos = Vector3.Lerp(gamePos, sharedPos, Mathf.SmoothStep(0f, 1f, poseWeight));

        Cam.transform.SetPositionAndRotation(Vector3.Lerp(p2Pos, sharedPos, s), Quaternion.Slerp(p2Rot, p1Rot, s));
        Cam.fieldOfView = p1Cam.fieldOfView;

        // Viewports, opening from the middle (user's choice): P1's own view [0, side], the shared view in the middle,
        // P2's view [1 - side, 1]; side = 0.5 (split) .. 0 (merged). The game's camera (Camera.main) shows the shared view
        // as soon as the middle opens, a copy shows P1's own view on the left meanwhile. Both sides glide towards the
        // shared pose while they close, so the edges match.
        SideWidth = 0.5f * (1f - s);
        Divider = 1f - SideWidth;   // P2's left edge (minimaps)
        bool transitioning = s > 0.001f && s < 0.999f;
        if (s <= 0.001f)
        {
            p1Cam.rect = new Rect(0f, 0f, 0.5f, 1f);   // the game's own pose for P1
        }
        else
        {
            p1Cam.transform.position = sharedPos;
            overridden = true;   // put back after this frame is drawn
            p1Cam.rect = s >= 0.999f ? new Rect(0f, 0f, 1f, 1f) : new Rect(SideWidth, 0f, 1f - 2f * SideWidth, 1f);
        }
        if (sideCam != null)
        {
            if (sideCam.enabled != transitioning) sideCam.enabled = transitioning;
            if (transitioning)
            {
                // From P1's clean follow pose (not the camera's current spot, which may still hold last frame's override)
                Vector3 p1Base = AvoidWalls(p1Head, p1Head + p1Rot * local, pc);
                sideCam.transform.SetPositionAndRotation(Vector3.Lerp(p1Base, sharedPos, s), p1Rot);
                sideCam.fieldOfView = p1Cam.fieldOfView;
                sideCam.rect = new Rect(0f, 0f, SideWidth, 1f);
            }
        }
        bool p2Visible = s < 0.999f;
        if (Cam.enabled != p2Visible) Cam.enabled = p2Visible;
        if (p2Visible) Cam.rect = s <= 0.001f ? new Rect(0.5f, 0f, 0.5f, 1f) : new Rect(1f - SideWidth, 0f, SideWidth, 1f);
    }

    // P1's camera distance without walls (user: when P1's camera got pushed in by a wall, P2's moved in too - P2's
    // camera copies P1's offset). A shorter measured offset is a wall only if a sphere cast from P1's head finds one on
    // the way to the remembered distance; otherwise it's a real zoom change and is taken over.
    private static Vector3 smoothLocal;
    private static int camFixLogs;
    private static void LogCamVec(Vector3 raw, float free, float cz, PlayerCamera pc)
    {
        if (camFixLogs >= 6 || !(free > 0.5f && free > cz + 0.05f)) return;
        camFixLogs++;
        ModLog.Info($"[camwall] P1's camera pulled in by a wall (currentZ {cz:F2}, intended {free:F2}): P2 uses depth {raw.z:F2}");
    }

    // TEMP [camwall2] (user: P1's camera pulled in by a wall still pulls P2's in): whenever P1's measured camera distance
    // changes by over 1 m - the game's values, what P2 took over, and whether a wall is behind P1's camera
    public static float P1CamDistance { get; private set; }
    public static float P2CamDistance { get; private set; }
    private static float lastLoggedLen = -1f;
    private static int wall2Logs;
    private static void LogWallState(Vector3 measured, Vector3 used, float free, float cz, PlayerCamera pc, Vector3 head, Quaternion rot)
    {
        float len = measured.magnitude;
        if (wall2Logs >= 40 || Mathf.Abs(len - lastLoggedLen) < 1f) return;
        lastLoggedLen = len;
        wall2Logs++;
        var gm = GameManager.Instance;
        int mask = gm != null ? gm.whatIsCameraCollision.value : 0;
        float radius = pc.cameraRadius > 0f ? pc.cameraRadius : 0.3f;
        string wall = "none";
        if (mask != 0 && len > 0.01f)
            foreach (var hit in Physics.SphereCastAll(head, radius, rot * (measured / len), 25f, mask, QueryTriggerInteraction.Ignore))
                if (hit.collider != null && hit.distance > 0f) { wall = $"'{hit.collider.name}' at {hit.distance:F2}"; break; }
        ModLog.Info($"[camwall2] P1 camera {len:F2} from head (camera space {measured}); used for P2 {used} (smoothed {smoothLocal}); " +
                    $"currentZ {cz:F2}, Get3rdPersonOffset {free:F2}, defaultZ {pc.defaultZ:F2}, state {pc.cameraState}; wall behind P1's camera: {wall}; P2 camera {P2CamDistance:F2} from P2's head");
    }

    // Autotest: turn P2's / P1's camera by a yaw angle
    // (merged / solo view: P2's stick turns the shared camera - the test does the same)
    public static void TurnP2(float degrees) { if (wantMerged && p1Cam != null) TurnP1Camera(degrees, 0f); else yaw += degrees; }
    public static void TurnP1(float degrees) { if (p1Cam != null) TurnP1Camera(degrees, 0f); }

    // Autotest: P1's camera looks along dir (so it sits on the other side of P1)
    public static void FaceP1(Vector3 dir)
    {
        if (p1Cam == null) return;
        float want = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z)).eulerAngles.y;
        TurnP1Camera(Mathf.DeltaAngle(p1Cam.transform.eulerAngles.y, want), 0f);
    }

    private static float freeLength = -1f;
    private static int wallLogs, vecLogs;
    private static float nextVecLog;
    private static float nextWallLog;
    private static Vector3 FreeOffset(Vector3 measured, Vector3 head, Quaternion rot, PlayerCamera pc)
    {
        float len = measured.magnitude;
        if (len < 0.01f) return measured;
        // TEMP [camvec]: the whole offset (not just its length) once a second, to see how the game avoids walls
        if (vecLogs < 40 && Time.unscaledTime >= nextVecLog)
        {
            vecLogs++;
            nextVecLog = Time.unscaledTime + 1f;
            float g3 = 0f;
            try { g3 = pc.Get3rdPersonOffset(); } catch { }
            ModLog.Info($"[camvec] P1 camera offset (camera space) {measured} len {len:F2}; currentZ {pc.currentZ:F2}, Get3rdPersonOffset() {g3:F2}, state {pc.cameraState}");
        }
        if (freeLength < 0f || len >= freeLength * 0.98f)
        {
            freeLength = len;
            return measured;
        }
        var gm = GameManager.Instance;
        int mask = gm != null ? gm.whatIsCameraCollision.value : 0;
        float radius = pc.cameraRadius > 0f ? pc.cameraRadius : 0.3f;
        Vector3 dir = rot * (measured / len);
        var p1Root = Assets.Scripts.Actors.Player.MyPlayer.Instance?.transform;
        bool wall = false;
        if (mask != 0)
            foreach (var hit in Physics.SphereCastAll(head, radius, dir, freeLength, mask, QueryTriggerInteraction.Ignore))
                if (hit.collider != null && (p1Root == null || !hit.collider.transform.IsChildOf(p1Root)) && hit.distance > 0f)
                {
                    wall = true;
                    break;
                }
        // TEMP (user: P1's camera pushed in by a wall still pulls P2's in): the game's own distance values next to
        // what this check decided, to find the reliable "free" distance
        if (wallLogs < 15 && Time.unscaledTime >= nextWallLog)
        {
            wallLogs++;
            nextWallLog = Time.unscaledTime + 1f;
            float g3 = 0f;
            try { g3 = pc.Get3rdPersonOffset(); } catch { }
            ModLog.Info($"[camwall] P1 camera {len:F2} from the head, remembered free {freeLength:F2}, wall found {wall}; game: currentZ {pc.currentZ:F2}, " +
                        $"defaultZ {pc.defaultZ:F2}, offset3rdPerson {pc.offset3rdPerson}, Get3rdPersonOffset() {g3:F2}, maxExtraZoomout {pc.maxExtraZoomoutDistance:F2}");
        }
        if (!wall)
        {
            freeLength = len;   // zoomed in
            return measured;
        }
        return measured / len * freeLength;
    }

    private static Vector3 gamePos;
    private static bool overridden;
    // P1's camera drawn from the shared spot this frame (merged / solo view) - and the game's own spot (Billboards: the
    // game aims its billboards at the game's spot)
    public static bool Overridden => overridden;
    public static Vector3 GamePos => gamePos;
    private static float poseWeight = 1f;   // 0 = shared view is the game's own camera (portal), 1 = midpoint view

    // Called at the end of every frame (after drawing): the game's camera goes back to the game's own position
    public static void RestoreAfterRender()
    {
        if (!overridden) return;
        overridden = false;
        if (p1Cam != null) p1Cam.transform.position = gamePos;
    }

    // One player down: only the other's view (Downed / P2Health; while the downed one rises out of the revive portal
    // the view stays on the helper until they're back)
    public static int Solo => P2Health.IsDead && !Downed.P1Down ? 1 : Downed.P1Down && !P2Health.IsDead ? 2 : 0;

    public static float SideWidth { get; private set; } = 0.5f;   // width of each player's own view (0.5 split .. 0 merged)
    public static float MergeAmount => Mathf.SmoothStep(0f, 1f, merge);

    private static void UpdateMerge(PlayerCamera pc, Vector3 p1Head, Vector3 p2Head)
    {
        float sep = Vector3.Distance(new Vector3(p1Head.x, 0f, p1Head.z), new Vector3(p2Head.x, 0f, p2Head.z));
        bool can = mergeEnabled && Mathf.Abs(p1Head.y - p2Head.y) < OldSplitDistance;   // very different heights: split
        // The game's camera busy (portal entrance): keep whatever it is (a run opens merged), decide again after
        bool decide = SafeState(pc) == PlayerCamera.ECameraState.Player3rd;
        bool before = wantMerged;
        // v1.0.4 (user: it shouldn't spam split/merge over and over): after a switch, no distance / height switch for
        // SwitchCooldown seconds. A player going down still switches at once.
        bool cooling = Time.unscaledTime - lastSwitch < SwitchCooldown;
        // v0.46.3 (user: when a player is dead, only show the alive player's camera): one full-screen view, centred on
        // the one alive (Solo, in the shared pose), whatever the distance
        if (Solo != 0) wantMerged = true;
        else if (cooling) { }
        else if (!can) wantMerged = false;
        else if (!decide) { }
        else if (sep < mergeDistance) wantMerged = true;
        else if (sep > splitDistance) wantMerged = false;
        if (wantMerged != before) lastSwitch = Time.unscaledTime;
        if (wantMerged != before && mergeLogs++ < 30)
            ModLog.Info($"[cam] {(wantMerged ? "merging" : "splitting")} (players {sep:F1} apart; P2 dead {P2Health.IsDead}, " +
                        $"camera state {SafeState(pc)}, height difference {Mathf.Abs(p1Head.y - p2Head.y):F1})");
        float step = Time.unscaledDeltaTime / TransitionTime;
        merge = Mathf.MoveTowards(merge, wantMerged ? 1f : 0f, step);
    }
    private static int mergeLogs;
    private const float SwitchCooldown = 2f;
    private static float lastSwitch = -100f;
    private static Vector3 cleanLocal;
    private static bool haveLocal;

    private static PlayerCamera.ECameraState SafeState(PlayerCamera pc)
    {
        try { return pc.GetCameraState(); }
        catch { return pc.cameraState; }
    }

    // P2's stick turns the shared view: added to P1's PlayerInput camera rotation (the game turns P1's camera from it).
    // Which component is yaw is worked out once by comparing with P1's camera.
    private static PlayerInput p1Input;
    private static int yawAxis = -1;   // 0 = x, 1 = y

    private static void TurnP1Camera(float dYaw, float dPitch)
    {
        if (p1Input == null)
        {
            var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
            p1Input = p1 != null ? p1.GetComponentInChildren<PlayerInput>() : null;
            if (p1Input == null) p1Input = Object.FindObjectOfType<PlayerInput>();
            if (p1Input == null) return;
        }
        var d = p1Input.desiredCameraRotation;
        if (yawAxis < 0)
        {
            float camYaw = p1Cam.transform.eulerAngles.y;
            yawAxis = Mathf.Abs(Mathf.DeltaAngle(d.y, camYaw)) <= Mathf.Abs(Mathf.DeltaAngle(d.x, camYaw)) ? 1 : 0;
            ModLog.Info($"[cam] P1 camera rotation {p1Input.cameraRotation} / desired {d}, camera euler {p1Cam.transform.eulerAngles} -> yaw is {(yawAxis == 1 ? "y" : "x")}");
        }
        if (yawAxis == 1) { d.y += dYaw; d.x += dPitch; }
        else { d.x += dYaw; d.y += dPitch; }
        p1Input.desiredCameraRotation = d;
    }

    // Like P1's camera: don't go through walls. A sphere of the game's camera radius is cast from P2's head towards the
    // wanted spot on the game's camera-collision layers; on a hit the camera stops just in front of it.
    private const float WallMargin = 0.1f, MinDistance = 0.5f;
    private static bool collisionLogged;

    private static Vector3 AvoidWalls(Vector3 head, Vector3 wanted, PlayerCamera pc)
    {
        var gm = GameManager.Instance;
        if (gm == null) return wanted;
        int mask = gm.whatIsCameraCollision.value;
        float radius = pc.cameraRadius > 0f ? pc.cameraRadius : 0.3f;
        if (!collisionLogged)
        {
            collisionLogged = true;
            ModLog.Info($"P2 camera collision: mask {mask}, radius {radius:F2}");
        }
        Vector3 dir = wanted - head;
        float dist = dir.magnitude;
        if (dist < 0.01f || mask == 0) return wanted;
        dir /= dist;
        float nearest = dist;
        foreach (var hit in Physics.SphereCastAll(head, radius, dir, dist, mask, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || hit.collider.transform.IsChildOf(body)) continue;
            if (hit.distance > 0f && hit.distance < nearest) nearest = hit.distance;
        }
        if (nearest >= dist) return wanted;
        return head + dir * Mathf.Max(MinDistance, nearest - WallMargin);
    }

    private static void ReadLook(float dt)
    {
        var joy = PlayerJoin.P2Joystick;
        if (joy == null) return;
        var ap = ReInput.controllers.polling.PollControllerForFirstAxis(ControllerType.Joystick, joy.id);
        if (ap.success)
        {
            string n = (ap.elementIdentifierName ?? "").ToLowerInvariant();
            if (n == "right stick x" && (!learnedX || rightXId != ap.elementIdentifierId))
            {
                learnedX = true;
                rightXId = ap.elementIdentifierId;
                ModLog.Info($"P2 right stick X = element id {rightXId}");
            }
            else if (n == "right stick y" && (!learnedY || rightYId != ap.elementIdentifierId))
            {
                learnedY = true;
                rightYId = ap.elementIdentifierId;
                ModLog.Info($"P2 right stick Y = element id {rightYId}");
            }
        }
        // v0.49.3 (user: P2 could turn the camera while P2's portal was spawning; P1 can't): not until P2 is in control
        if (P2Body.Entering) return;
        float x = joy.GetAxisById(rightXId), y = joy.GetAxisById(rightYId);
        if (Mathf.Abs(x) > DeadZone) lookX = x * YawSpeed * dt;
        if (Mathf.Abs(y) > DeadZone) lookY = y * PitchSpeed * dt;
        if (wantMerged || merge > 0f) return;   // the shared view: applied to P1's camera in LateUpdate
        yaw += lookX;
        pitch = Mathf.Clamp(pitch - lookY, MinPitch, MaxPitch);
    }

    // Stage end / new run: the camera goes with the scene; give P1 the full screen back if it's still there
    public static void Stop()
    {
        if (p1Cam != null) p1Cam.rect = new Rect(0f, 0f, 1f, 1f);
        if (Cam != null) Object.Destroy(Cam.gameObject);
        if (sideCam != null) Object.Destroy(sideCam.gameObject);
        Cam = null;
        sideCam = null;
        SideWidth = 0.5f;
        body = null;
        merge = 0f;
        wantMerged = false;
        Divider = 0.5f;
        p1Input = null;
        yawAxis = -1;
        haveLocal = false;
        freeLength = -1f;
    }
}
