using Assets.Scripts.Actors.Player;
using Rewired;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// The map + stats overlay, one per player (user: P1's right trigger shows the map and stats over the whole screen;
// wanted: only over P1's half, the map smaller with the stats under it, stretched sideways; P2 the same on P2's half).
//
// The game's overlay (v0.37.8 dump, 1920x1080 canvas 'GameUI'): object 'Map' {FullMap} holds 'FullMapUi' (750x750,
// left middle: RawImage 'MapRender' showing MapTexture, rendered by FullMap.mapCamera - a top-down camera over the
// whole world, so the image is the same for both players) and 'W_Stats' (the stats window, right middle: 'StatsContent'
// a vertical list of StatEntry rows, each one refreshing its stat with StatEntry.RefreshValues). P1 opens it with the
// Rewired action 'MapOverlay' (P1's pad: Right Trigger, an axis), FullMap.IsMapOpen().
//
// While P2 plays, each player gets a panel on their half of the screen: an inert copy of the map frame (same map
// texture) scaled down at the top, and a copy of the stats window under it in two columns (GridLayoutGroup instead of
// the vertical list). The StatEntry rows stay alive in the copy and are refreshed every half second - in P2's stat
// context for P2's panel, so they show P2's stats. P1's panel shows while the game's map is open (the game's own frame
// and window are hidden meanwhile); P2's while P2 holds the right trigger. P2's exploring also reveals the map's fog.
internal static class MapView
{
    private const float MapScale = 0.6f;
    private const float TopMargin = 70f;
    private const float RefreshEvery = 0.5f;
    private const float SideMargin = 30f;       // the stats window keeps this far from each edge of its half

    private class Panel
    {
        public GameObject root;
        public GameObject stats;                 // the hidden copy of the game's window (its rows do the work)
        public TMPro.TextMeshProUGUI left, right;  // the shown columns
        public float nextRefresh;
        public bool shown;
        public bool rowsAssigned;    // each copied row given its stat (StatEntry.Set)
    }

    private static FullMap map;
    private static readonly Panel[] panels = new Panel[2];
    private static bool gameHidden;
    private static bool p2OpenedGame;   // P2's map holds one of the game's "maps open"
    private static bool wasOpen;
    private static int openLogs, p2ToggleLogs;
    private static float nextReveal;
    private static int fogLogs;

    // TEMP [perf] ([perf] log: the map view took ~0.7 ms of every frame): time per part - 0 map lookups, 1 P2's
    // trigger, 2 panels, 3 hiding the game's window
    private static readonly System.Diagnostics.Stopwatch mapWatch = new();
    private static readonly double[] mapParts = new double[4];
    private static int mapFrames, mapLogs, mapMissLogs;
    private static float nextMapFind;
    private static void PerfMark(int i)
    {
        mapParts[i] += mapWatch.Elapsed.TotalMilliseconds;
        mapWatch.Restart();
        if (i < 3) return;
        if (++mapFrames < 300) return;
        if (mapLogs++ < 4) ModLog.Info($"[perf] map parts (avg ms): lookups {mapParts[0] / mapFrames:F3}, P2 trigger {mapParts[1] / mapFrames:F3}, panels {mapParts[2] / mapFrames:F3}, hide game {mapParts[3] / mapFrames:F3}");
        Array.Clear(mapParts, 0, 4);
        mapFrames = 0;
    }

    public static void Update()
    {
        mapWatch.Restart();
        if (map == null)
        {
            // v0.44.19 ([perf] log: ~0.8 ms of every frame went to this scene-wide search while the map isn't found):
            // looked for once a second
            if (Time.unscaledTime < nextMapFind) return;
            nextMapFind = Time.unscaledTime + 1f;
            map = Object.FindObjectOfType<FullMap>();
            if (map == null) { if (mapMissLogs++ < 2) ModLog.Info("[perf] map: no FullMap found (looked again in 1 s)"); return; }
        }
        // 1.0 alpha (user: P2's full map was blank - all fog, no icons): switching the map camera on wasn't enough, the
        // game draws the fog and icons only while its map is open. P2's map now opens the game's own (OnMapToggle,
        // which counts maps open: FullMap.mapsOpen); P1's is open when the count is more than P2's own.
        bool p1Open;
        try { p1Open = map.mapsOpen - (p2OpenedGame ? 1 : 0) > 0; }
        catch { return; }
        if (p1Open != wasOpen)
        {
            wasOpen = p1Open;
            if (openLogs++ < 4) ModLog.Info($"[map] P1's map {(p1Open ? "opened" : "closed")}");
        }

        bool split = P2Movement.Active && P2Weapons.Body != null;
        // 1.0 alpha (user: a player going down with the map open left it on screen; map_twice test: P1 down -> the game's
        // map stayed open, count 1, P1's panel shown - P1 can't close it while down): P1's map closes when P1 goes down
        if (p1Open && Downed.P1Down)
        {
            try
            {
                map.OnMapToggle(false);
                ModLog.Info($"P1 is down: P1's map closed (the game's map count now {map.mapsOpen})");
                p1Open = map.mapsOpen - (p2OpenedGame ? 1 : 0) > 0;
                wasOpen = p1Open;
            }
            catch (Exception e) { ModLog.Warn($"[map] closing P1's map while down failed: {e.Message}"); }
        }
        PerfMark(0);
        bool p2Open = split && !P2Health.IsDead && !P2LevelUp.P2ScreenOpen && P2TriggerHeld();
        PerfMark(1);

        if (split && (p1Open || p2Open)) EnsurePanels();
        Show(0, split && p1Open && !Downed.P1Down, inP2: false);
        Show(1, p2Open, inP2: true);
        PerfMark(2);
        // v0.38.1 (user: the stats panel showed at its old place, then jumped): hidden the whole time P2 plays, so the
        // game's own frame/window never shows for a frame before the mod reacts
        HideGame(split);
        PerfMark(3);

        // P2 holding the map: the game's own map open for P2 too (its camera, fog and icons)
        if (p2Open != p2OpenedGame)
        {
            try
            {
                map.OnMapToggle(p2Open);
                p2OpenedGame = p2Open;
                // (user: P2's map got stuck the second time - every toggle logged, with what decided it)
                if (p2ToggleLogs++ < 60) ModLog.Info($"P2 map: {(p2Open ? "opened" : "closed")} (the game's map count now {map.mapsOpen}; trigger {P2TriggerHeld()}, P2 down {P2Health.IsDead}, P2 screen {P2LevelUp.P2ScreenOpen}, IsMapOpen {map.IsMapOpen()})");
            }
            catch (Exception e) { ModLog.Warn($"[map] P2's map toggle failed: {e.Message}"); p2OpenedGame = false; }
        }

        // P2's exploring reveals the map's fog too
        if (split && Time.time >= nextReveal)
        {
            nextReveal = Time.time + 0.5f;
            // 1.0.2 (user: P2 doesn't uncover the fog; bug_fog_p2: P2 walked 80 m, 0 cells revealed - P1 13 for 110 m):
            // QueueRevealFog(P2's spot) did nothing, also with RevealFog right after (the game reveals around "the player"
            // in its own FixedUpdate) - now that FixedUpdate runs once more with P1's object and movement at P2's spot
            try
            {
                var p1Pm = PlayerMovement.Instance;
                P2Items.AtP2Spot("fog", () =>
                {
                    try
                    {
                        if (P2Movement.Pm != null) PlayerMovement.Instance = P2Movement.Pm;
                        map.FixedUpdate();
                        map.RevealFog();
                    }
                    finally { if (p1Pm != null) PlayerMovement.Instance = p1Pm; }
                });
            }
            catch (Exception e) { if (fogLogs++ < 3) ModLog.Warn($"[map] P2's fog reveal failed: {e.Message}"); }
        }
    }

    // ---------- P2's right trigger ----------

    private static readonly HashSet<string> axisNamesSeen = new();
    private static bool P2TriggerHeld()
    {
        if (Autotest.Active) return Autotest.P2Trigger;
        var joy = PlayerJoin.P2Joystick;
        if (joy == null) return false;
        // 1.0 alpha (user: P2's map flickers; log: opened and closed again 15 ms later on every pull - polling reports the
        // trigger in the frame it moves, not while it's held): the trigger's own value when it can be read, else a
        // press counts until the release (the negative side) is reported
        bool polledPress = false, polledRelease = false;
        try
        {
            var all = ReInput.controllers.polling.PollControllerForAllAxes(ControllerType.Joystick, joy.id);
            if (all != null)
            {
                var e = all.GetEnumerator();
                var move = e.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (move.MoveNext())
                {
                    var info = e.Current;
                    if (!info.success) continue;
                    string n = (info.elementIdentifierName ?? "").Trim().ToLowerInvariant();
                    if (axisNamesSeen.Add(n + info.axisPole)) ModLog.Info($"P2 map: pad axis seen '{info.elementIdentifierName}' (id {info.elementIdentifierId}, {info.axisPole})");
                    if (n != "right trigger" && n != "rt") continue;
                    triggerId = info.elementIdentifierId;
                    if (info.axisPole == Pole.Positive) polledPress = true; else polledRelease = true;
                }
            }
        }
        catch (Exception ex) { if (axisNamesSeen.Add("error")) ModLog.Warn($"P2 map: trigger poll failed: {ex.Message}"); }
        if (polledPress) triggerLatched = true;
        else if (polledRelease) triggerLatched = false;
        float value = float.NaN;
        if (triggerId >= 0) { try { value = joy.GetAxisById(triggerId); } catch (Exception ex) { if (axisNamesSeen.Add("value error")) ModLog.Warn($"P2 map: trigger value read failed: {ex.Message}"); } }
        bool held = !float.IsNaN(value) && valueWorks ? value > 0.3f : triggerLatched;
        if (!float.IsNaN(value) && value > 0.3f && !valueWorks) { valueWorks = true; ModLog.Info($"P2 map: the trigger's value can be read ({value:F2}) - used from now on"); }
        if ((polledPress || polledRelease || held != lastHeld) && triggerLogs++ < 80)
            ModLog.Info($"P2 map: trigger poll press {polledPress} release {polledRelease}, value {value:F2}, latched {triggerLatched} -> held {held}");
        lastHeld = held;
        return held;
    }
    private static int triggerId = -1, triggerLogs;
    private static bool triggerLatched, valueWorks, lastHeld;

    // ---------- Panels ----------

    private static void EnsurePanels()
    {
        if (panels[0] != null && panels[0].root != null) return;
        var ui = map.mapDisplayTransform;            // 'FullMapUi'
        var statsWin = map.statsWindow;              // 'W_Stats'
        if (ui == null || statsWin == null) return;
        for (int i = 0; i < 2; i++)
        {
            try { panels[i] = Build(i, ui, statsWin); }
            catch (Exception e) { ModLog.Error($"[map] panel {i + 1} failed: {e}"); }
        }
        LogMapIcons();
    }

    private static Panel Build(int player, Transform ui, GameObject statsWin)
    {
        var parent = map.transform;   // 'Map' (full screen, under GameUI)
        var root = new GameObject($"MegabonkSplitscreen_MapPanelP{player + 1}");
        var rt = root.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(player * 0.5f, 0f);
        rt.anchorMax = new Vector2(player * 0.5f + 0.5f, 1f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.SetAsLastSibling();

        // The map frame, scaled down at the top centre of the half
        var mapCopy = CopyStripped(ui.gameObject, rt, keepStatEntries: false);
        var mrt = mapCopy.GetComponent<RectTransform>();
        mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 1f);
        mrt.pivot = new Vector2(0.5f, 1f);
        mrt.localScale = Vector3.one * MapScale;
        mrt.anchoredPosition = new Vector2(0f, -TopMargin);
        float mapHeight = mrt.sizeDelta.y * MapScale;

        // v0.41.6 (autotest screenshot: v0.38.1's two-column rework of the game's window showed one stray row - its
        // nested auto-size layouts don't take a grid): the game's stats window stays a hidden copy whose rows
        // (StatEntry.RefreshValues) work out each stat's name and value; the mod shows those texts in its own box
        // under the map: a title and two text columns, as wide as the half allows.
        var statsCopy = CopyStripped(statsWin, rt, keepStatEntries: true);
        var hide = statsCopy.GetComponent<CanvasGroup>() ?? statsCopy.AddComponent<CanvasGroup>();
        hide.alpha = 0f;
        hide.blocksRaycasts = false;

        float halfWidth = rt.rect.width > 10f ? rt.rect.width : 960f;
        float height = rt.rect.height > 10f ? rt.rect.height : 1080f;
        float statsTop = TopMargin + mapHeight + 40f;
        float boxW = halfWidth - 2f * SideMargin, boxH = Mathf.Max(200f, height - statsTop - 20f);
        var sample = statsCopy.GetComponentsInChildren<StatEntry>(false).FirstOrDefault()?.t_stat;

        var box = new GameObject("MegabonkSplitscreen_StatsBox");
        var brt = box.AddComponent<RectTransform>();
        brt.SetParent(rt, false);
        brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 1f);
        brt.pivot = new Vector2(0.5f, 1f);
        brt.sizeDelta = new Vector2(boxW, boxH);
        brt.anchoredPosition = new Vector2(0f, -statsTop);
        var bg = box.AddComponent<Image>();
        bg.color = new Color(0.07f, 0.07f, 0.09f, 0.85f);

        var title = Text(brt, "Title", sample, 28f, TMPro.TextAlignmentOptions.Top, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -44f), new Vector2(0f, -8f));
        title.text = $"PLAYER {player + 1} STATS";
        float colW = (boxW - 3f * 20f) / 2f;
        var left = Text(brt, "Left", sample, 19f, TMPro.TextAlignmentOptions.TopLeft, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(20f, 12f), new Vector2(20f + colW, -52f));
        var right = Text(brt, "Right", sample, 19f, TMPro.TextAlignmentOptions.TopLeft, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(40f + colW, 12f), new Vector2(40f + 2f * colW, -52f));

        root.SetActive(false);
        ModLog.Info($"[map] panel for P{player + 1}: map {mrt.sizeDelta} x{MapScale}; stats box {boxW:F0}x{boxH:F0} at {statsTop:F0} from the top of a {halfWidth:F0}x{height:F0} half, " +
                    $"{statsCopy.GetComponentsInChildren<StatEntry>(false).Length} rows, font {(sample != null ? sample.font?.name : "default")}");
        return new Panel { root = root, stats = statsCopy, left = left, right = right };
    }

    // A text in the stats box, between two anchor points with offsets (min = bottom-left, max = top-right)
    private static TMPro.TextMeshProUGUI Text(RectTransform parent, string name, TMPro.TextMeshProUGUI style, float size, TMPro.TextAlignmentOptions align,
                                             Vector2 anchorMin, Vector2 anchorMax, Vector2 offMin, Vector2 offMax)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offMin;
        rt.offsetMax = offMax;
        var t = go.AddComponent<TMPro.TextMeshProUGUI>();
        if (style != null) { t.font = style.font; t.color = style.color; }
        t.fontSize = size;
        t.alignment = align;
        t.enableWordWrapping = false;
        t.richText = true;
        t.overflowMode = TMPro.TextOverflowModes.Overflow;
        return t;
    }

    // A copy made under an inactive holder (no game script wakes up), game scripts removed except the stat rows
    private static GameObject CopyStripped(GameObject src, Transform parent, bool keepStatEntries)
    {
        var hold = new GameObject("hold");
        hold.SetActive(false);
        hold.transform.SetParent(parent, false);
        var copy = Object.Instantiate(src, hold.transform);
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb.GetIl2CppType().Assembly.GetName().Name != "Assembly-CSharp") continue;
            if (keepStatEntries && mb.TryCast<StatEntry>() != null) continue;
            Object.DestroyImmediate(mb);
        }
        // The game's original may carry the mod's invisibility (HideGame): never the copy
        var cg = copy.GetComponent<CanvasGroup>();
        if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; }
        copy.transform.SetParent(parent, false);
        copy.name = "MegabonkSplitscreen_" + src.name;
        Object.Destroy(hold);
        copy.SetActive(true);
        return copy;
    }

    private static void Show(int i, bool show, bool inP2)
    {
        var p = panels[i];
        if (p?.root == null) return;
        if (p.root.activeSelf != show) p.root.SetActive(show);
        if (show && !p.shown) p.nextRefresh = 0f;
        p.shown = show;
        if (!show || Time.unscaledTime < p.nextRefresh) return;
        p.nextRefresh = Time.unscaledTime + RefreshEvery;
        Refresh(p, inP2);
    }

    private static int refreshErrors;
    private static void Refresh(Panel p, bool inP2)
    {
        // v0.41.7 (autotest: the copy had 2 rows - it was made the moment the map first opened, before the game had
        // filled its window): remade from the game's window once that one has more rows
        var gameWin = map?.statsWindow;
        if (gameWin != null)
        {
            int mine = p.stats.GetComponentsInChildren<StatEntry>(false).Length;
            int game = gameWin.GetComponentsInChildren<StatEntry>(false).Length;
            if (game < 5)
            {
                // The game's window makes its rows when it really opens (StatsUi.TryInit): asked to now
                try { gameWin.GetComponentInChildren<StatsUi>(true)?.TryInit(); } catch (Exception e) { if (refreshErrors++ < 3) ModLog.Warn($"[map] StatsUi.TryInit failed: {e.Message}"); }
                game = gameWin.GetComponentsInChildren<StatEntry>(false).Length;
            }
            if (game > mine)
            {
                var parent = p.stats.transform.parent;
                Object.Destroy(p.stats);
                p.stats = CopyStripped(gameWin, parent, keepStatEntries: true);
                var cg = p.stats.GetComponent<CanvasGroup>() ?? p.stats.AddComponent<CanvasGroup>();
                cg.alpha = 0f;
                cg.blocksRaycasts = false;
                // v0.41.9 (autotest: every copied row read "Attack Damage") - which stat a row shows isn't copied by
                // Instantiate: each copy row takes the stat of the game's row in the same place
                // v0.41.10: the game's rows didn't know theirs yet either - the list comes from StatsUi.statsToShow (in
                // row order, spacer rows aside), set with the game's own StatEntry.Set
                p.rowsAssigned = false;
                if (refreshLogs < 4) ModLog.Info($"[map] stats copy remade: {mine} -> {game} rows");
            }
            // 1.0 alpha (user: every value in P2's panel read 120 - P2's max HP): the rows' stats were only given when
            // the copy was remade; a copy that was complete from the start kept every row on the first stat
            if (!p.rowsAssigned)
            {
                var ui = gameWin.GetComponentInChildren<StatsUi>(true);
                var show = ui?.statsToShow;
                var rows = p.stats.GetComponentsInChildren<StatEntry>(false).Where(r => !r.name.StartsWith("Spacer")).ToList();
                int set = 0;
                if (show != null)
                    for (int k = 0; k < rows.Count && k < show.Count; k++) { rows[k].Set(show[k]); set++; }
                p.rowsAssigned = set > 0;
                if (refreshLogs < 6) ModLog.Info($"[map] stats copy rows given their stats: {set} of {rows.Count} (game's list {show?.Count ?? -1})");
            }
        }
        var lines = new List<string>();
        bool entered = inP2 && P2Inventory.EnterP2();
        try
        {
            foreach (var row in p.stats.GetComponentsInChildren<StatEntry>(false))
            {
                try { row.RefreshValues(); } catch (Exception e) { if (refreshErrors++ < 3) ModLog.Warn($"[map] stat row refresh failed: {e.Message}"); }
                string name = row.t_stat != null ? row.t_stat.text : "";
                string value = row.t_value != null ? row.t_value.text : "";
                // v0.41.12 (autotest: P2's Shield row read 0 with P2's shield up - the rows don't read P2's table):
                // P2's value = P2's own stat through the game's own formatter; P1's row text is checked against the
                // same formatter once, to show both read the same value
                bool spacer = row.name.StartsWith("Spacer") || string.IsNullOrWhiteSpace(name);
                if (!spacer)
                {
                    try
                    {
                        if (inP2) value = StatsUi.FormatStat(row.stat, P2Inventory.Get(row.stat));
                        else if (formatChecks < 40)
                        {
                            formatChecks++;
                            string mine = StatsUi.FormatStat(row.stat, MyPlayer.Instance?.inventory?.playerStats?.GetStat(row.stat) ?? 0f);
                            if (mine != value) ModLog.Info($"[map] P1 {row.stat}: game shows '{value}', formatter on GetStat gives '{mine}'");
                        }
                    }
                    catch (Exception e) { if (refreshErrors++ < 3) ModLog.Warn($"[map] FormatStat failed: {e.Message}"); }
                }
                lines.Add(string.IsNullOrWhiteSpace(name) ? "" : $"{name}<pos=78%>{value}");   // a spacer row stays a gap
            }
        }
        finally { if (entered) P2Inventory.Exit(); }
        // Two columns, the first one taking the extra line; a gap doesn't start a column
        int half = (lines.Count + 1) / 2;
        var a = lines.Take(half).ToList();
        var b = lines.Skip(half).SkipWhile(string.IsNullOrEmpty).ToList();
        if (p.left != null) p.left.text = string.Join("\n", a);
        if (p.right != null) p.right.text = string.Join("\n", b);
        if (refreshLogs++ < 2) ModLog.Info($"[map] stats box filled: {lines.Count} lines ({a.Count} + {b.Count}), first '{lines.FirstOrDefault(l => l.Length > 0)}'");
    }
    private static int refreshLogs, formatChecks;

    // While P2 plays, the game's own frame and window are invisible (a CanvasGroup at alpha 0 - the window's own
    // ScaleOnAwake animation sets its scale when it opens, which undid v0.38.0's scale 0 for a frame)
    private static void HideGame(bool hide)
    {
        if (map == null) return;
        foreach (var t in new[] { map.mapDisplayTransform, map.statsWindow != null ? map.statsWindow.transform : null })
        {
            if (t == null) continue;
            var cg = t.GetComponent<CanvasGroup>();
            if (cg == null)
            {
                if (!hide) continue;
                cg = t.gameObject.AddComponent<CanvasGroup>();
            }
            float want = hide ? 0f : 1f;
            if (cg.alpha != want) cg.alpha = want;
            cg.blocksRaycasts = !hide;
        }
        gameHidden = hide;
    }

    // TEMP: which world objects the map camera shows near each player (the player icons)
    private static void LogMapIcons()
    {
        try
        {
            var cam = map.mapCamera;
            var p1 = MyPlayer.Instance;
            var b = P2Weapons.Body;
            if (cam == null || p1 == null) return;
            var near1 = new List<string>();
            var near2 = new List<string>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || (cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                Vector3 pos = r.transform.position;
                if ((pos - p1.transform.position).sqrMagnitude < 15f * 15f) near1.Add($"{r.name} (layer {r.gameObject.layer})");
                if (b != null && (pos - b.transform.position).sqrMagnitude < 15f * 15f) near2.Add($"{r.name} (layer {r.gameObject.layer})");
            }
            ModLog.Info($"[map] map-camera objects near P1: {(near1.Count == 0 ? "none" : string.Join(", ", near1.Take(10)))}; near P2: {(near2.Count == 0 ? "none" : string.Join(", ", near2.Take(10)))}");
        }
        catch (Exception e) { ModLog.Warn($"[map] icon check failed: {e.Message}"); }
    }

    // Autotest 'mapcheck': each panel's state and screen area, and the game's own frame/window visibility
    public static void Describe()
    {
        var parts = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var p = panels[i];
            if (p?.root == null) { parts.Add($"P{i + 1} panel: not built"); continue; }
            // (GetWorldCorners doesn't pass its results back through the interop: position + scaled size instead)
            var srt = p.left != null ? p.left.transform.parent.GetComponent<RectTransform>() : p.stats.GetComponent<RectTransform>();
            Vector3 s = srt.lossyScale;
            float w = srt.rect.width * s.x, h = srt.rect.height * s.y;
            float left = srt.position.x - srt.pivot.x * w, top = srt.position.y + (1f - srt.pivot.y) * h;
            parts.Add($"P{i + 1} panel: shown {p.root.activeInHierarchy}, stats x {left:F0}..{left + w:F0} y {top - h:F0}..{top:F0} ({srt.rect.width:F0}x{srt.rect.height:F0} x{s.x:F2})");
        }
        float gameAlpha = map?.statsWindow?.GetComponent<CanvasGroup>()?.alpha ?? -1f;
        string count; try { count = $"{map?.mapsOpen}, IsMapOpen {map?.IsMapOpen()}"; } catch (Exception e) { count = e.Message; }
        ModLog.Info($"[test] mapcheck: {string.Join("; ", parts)}; game's maps open {count}, P2 holds one {p2OpenedGame}; game's stats window alpha {gameAlpha:F1}; screen {Screen.width}x{Screen.height}");
    }

    public static void Reset()
    {
        if (gameHidden) HideGame(false);
        for (int i = 0; i < 2; i++)
        {
            if (panels[i]?.root != null) Object.Destroy(panels[i].root);
            panels[i] = null;
        }
        if (p2OpenedGame && map != null) { try { map.OnMapToggle(false); } catch { } }
        p2OpenedGame = false;
        map = null;
        wasOpen = false;
    }
}
