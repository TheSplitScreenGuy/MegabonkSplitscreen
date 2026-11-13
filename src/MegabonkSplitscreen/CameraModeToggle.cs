using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// The camera mode in the pause menu (user: two modes - split screen all the time, or as now: merged while close and
// split apart - a toggle in the game's pause menu, kept across sessions; P2Camera.MergeEnabled saves it).
// A copy of one of the pause menu's own buttons (same look), placed after it; the copy's game scripts and Selectable are
// taken off (they'd run the original button's action) and a plain Button toggles the mode and relabels it.
internal static class CameraModeToggle
{
    private static GameObject button;
    private static TextMeshProUGUI label;
    private static int logs, stateLogs;

    // Each frame in a run (P2Body.Update). v0.48.1: its own lookup through the always-active PauseHandler (v0.48.0 hung
    // off P2Pause, which stops early without P2's pad - the button never appeared)
    private static PauseUi ui;
    private static float nextFind;
    public static void Update()
    {
        if (ui == null && Time.unscaledTime >= nextFind)
        {
            nextFind = Time.unscaledTime + 1f;
            ui = Object.FindObjectOfType<PauseHandler>()?.pauseUi;
        }
        if (ui == null) { button = null; return; }
        // The open pause window: v0.48.1 waited for PauseUi.main and never saw it open - which object the menu really is
        // gets logged (names, not guesses), and whichever of main / mainWindow / current is open with buttons is used
        GameObject open = null;
        foreach (var go in new[] { ui.main, ui.mainWindow != null ? ui.mainWindow.gameObject : null, ui.current })
            if (go != null && go.activeInHierarchy && go.GetComponentsInChildren<MyButton>(false).Length > 0) { open = go; break; }
        bool paused = false;
        try { paused = ui.IsPaused(); } catch { }
        if (paused && stateLogs++ < 2)
            ModLog.Info($"[pause] menu open: main '{ui.main?.name}' active {ui.main?.activeInHierarchy}, mainWindow '{ui.mainWindow?.name}' active {ui.mainWindow?.gameObject.activeInHierarchy}, current '{ui.current?.name}' active {ui.current?.activeInHierarchy}; PauseUi '{ui.name}' active {ui.gameObject.activeInHierarchy}; using {(open != null ? $"'{open.name}'" : "none")}");
        if (open == null) { giveUpArmedUntil = -1f; return; }
        if (button == null) Make(open);
        PlaceGiveUp();
        PointArrows();
        if (label != null)
        {
            string want = P2Camera.MergeEnabled ? "CAMERA: DYNAMIC" : "CAMERA: SPLIT";
            if (label.text != want) label.text = want;
        }
        if (giveUpLabel != null)
        {
            string want = GiveUpText();
            if (giveUpLabel.text != want) giveUpLabel.text = want;
        }
    }

    // ---------- P2 give up (user: a pause menu option to force-kill P2 - an escape hatch, e.g. P2 stuck taking no
    // damage). Press once to arm, again within 3 s: P2 goes down (gravestone; P1 can revive) and the game resumes.
    private static GameObject giveUpButton;
    private static TextMeshProUGUI giveUpLabel;
    private static float giveUpArmedUntil = -1f;
    private static bool Armed => Time.unscaledTime <= giveUpArmedUntil;

    private static string GiveUpText()
    {
        if (!PlayerJoin.HasP2 || !P2Movement.Active) return "PLAYER 2: -";
        if (P2Health.IsDead) return "PLAYER 2 IS DOWN";
        if (Armed) return Downed.P1Down ? "AGAIN: RUN ENDS" : "AGAIN: DOWN P2";   // (user: "ENDS THE RUN" was too big)
        return "PLAYER 2: GIVE UP";
    }

    public static void GiveUp()
    {
        if (!PlayerJoin.HasP2 || !P2Movement.Active || P2Health.IsDead) return;
        if (!Armed)
        {
            giveUpArmedUntil = Time.unscaledTime + 3f;
            if (giveUpLabel != null) giveUpLabel.text = GiveUpText();
            return;
        }
        giveUpArmedUntil = -1f;
        try { ui?.Resume(); } catch (System.Exception e) { ModLog.Warn($"[pause] resume after P2 gave up: {e.Message}"); }
        P2Health.SetP2TestHealth(false);
        P2Health.TakeDamage(10_000_000, "gave up (pause menu)");
        ModLog.Info($"[down] P2 gave up from the pause menu: down {P2Health.IsDead} (P1 down {Downed.P1Down})");
    }

    // v0.48.4 (user: the 4 corner arrows didn't move onto the camera button while it was selected): the arrows
    // (SelectionArrow.OnButtonHover) are moved by the game's MyButton, which the copy doesn't have - done here when the
    // copy becomes the selected object
    private static bool wasSelected;
    private static int arrowLogs;
    private static void PointArrows()
    {
        if (button == null || button.GetComponent<MyButton>() != null) { wasSelected = false; return; }   // MyButton moves them itself
        var es = UnityEngine.EventSystems.EventSystem.current;
        bool selected = es != null && es.currentSelectedGameObject != null && es.currentSelectedGameObject.Pointer == button.Pointer;
        if (selected && !wasSelected)
        {
            var arrow = SelectionArrow.Instance;
            if (arrow != null)
            {
                try { arrow.OnButtonHover(button.GetComponent<RectTransform>()); }
                catch (System.Exception e) { if (arrowLogs++ < 3) ModLog.Warn($"[pause] arrows onto the camera button: {e.Message}"); }
            }
            else if (arrowLogs++ < 3) ModLog.Info("[pause] camera button selected, no SelectionArrow.Instance");
        }
        wasSelected = selected;
    }

    // The pause buttons' up/down links: Automatic needs nothing; Explicit ones (Settings down -> Restart) would skip
    // the copy, so Settings <-> copy <-> the next button are linked (mode logged)
    private static void WireNavigation(Selectable srcSel, Button copy)
    {
        if (srcSel == null) return;
        var nav = srcSel.navigation;
        ModLog.Info($"[pause] '{srcSel.name}' navigation {nav.mode}, down '{nav.selectOnDown?.name}', up '{nav.selectOnUp?.name}'");
        if (nav.mode != Navigation.Mode.Explicit) return;
        var next = nav.selectOnDown;
        var mine = nav;   // same left/right as Settings
        mine.selectOnUp = srcSel;
        mine.selectOnDown = next;
        copy.navigation = mine;
        nav.selectOnDown = copy;
        srcSel.navigation = nav;
        if (next != null)
        {
            var n2 = next.navigation;
            if (n2.mode == Navigation.Mode.Explicit && n2.selectOnUp != null && n2.selectOnUp.Pointer == srcSel.Pointer) { n2.selectOnUp = copy; next.navigation = n2; }
        }
        ModLog.Info($"[pause] links: '{srcSel.name}' <-> '{copy.name}' <-> '{next?.name}'");
    }

    public static void Toggle()
    {
        P2Camera.MergeEnabled = !P2Camera.MergeEnabled;
        if (label != null) label.text = P2Camera.MergeEnabled ? "CAMERA: DYNAMIC" : "CAMERA: SPLIT";
    }

    private static void Make(GameObject main)
    {
        // The pause menu's buttons: the game's MyButton (names logged once, to see what's copied)
        var buttons = main.GetComponentsInChildren<MyButton>(true).Where(b => b != null && b.gameObject.activeSelf).ToList();
        if (logs++ < 1)
            ModLog.Info($"[pause] pause menu '{main.name}': buttons [{string.Join(", ", buttons.Select(b => $"'{b.name}' (parent '{b.transform.parent?.name}', text '{b.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault()?.text}')"))}]");
        if (buttons.Count == 0) return;
        // After Settings ([pause] log: B_Resume, B_Map, B_Settings, B_Restart, B_Exit), else after the last
        var src = buttons.FirstOrDefault(b => b.name == "B_Settings") ?? buttons[buttons.Count - 1];
        button = Copy(src, src.transform, "MegabonkSplitscreen_CameraMode", Toggle, out label);
        if (button == null) return;
        // P2's give-up button right of Restart (user: the 7th button in the column pushed Exit off the screen) - out of the
        // column's layout, placed beside Restart every frame (PlaceGiveUp), linked left/right with it
        restartButton = buttons.FirstOrDefault(b => b.name == "B_Restart");
        if (PlayerJoin.HasP2 && restartButton != null)
        {
            giveUpButton = Copy(src, restartButton.transform, "MegabonkSplitscreen_P2GiveUp", GiveUp, out giveUpLabel, wire: false);
            if (giveUpLabel != null)
            {
                // Any of its labels fits the button: shrinks down to 60 % if needed (the game's TextSizer is off on copies)
                giveUpLabel.fontSizeMax = giveUpLabel.fontSize;
                giveUpLabel.fontSizeMin = giveUpLabel.fontSize * 0.6f;
                giveUpLabel.enableAutoSizing = true;
            }
            if (giveUpButton != null)
            {
                var le = giveUpButton.GetComponent<LayoutElement>() ?? giveUpButton.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                WireBeside(restartButton.GetComponent<Selectable>(), giveUpButton.GetComponent<Button>());
            }
        }
    }

    private static MyButton restartButton;
    private const float GiveUpGap = 24f;

    // Each frame while the menu is open: the give-up button right of Restart, same height (Restart's own size)
    private static void PlaceGiveUp()
    {
        if (giveUpButton == null || restartButton == null) return;
        var r = restartButton.GetComponent<RectTransform>();
        var g = giveUpButton.GetComponent<RectTransform>();
        if (r == null || g == null) return;
        g.sizeDelta = r.rect.size;
        float w = r.rect.width * r.lossyScale.x;
        g.position = r.position + Vector3.right * (w + GiveUpGap * r.lossyScale.x);
    }

    // Restart -> right = give up; give up: left = Restart, up/down like Restart's (Explicit navigation only; Automatic
    // finds the button beside it by itself)
    private static void WireBeside(Selectable restart, Button giveUp)
    {
        if (restart == null || giveUp == null) return;
        var nav = restart.navigation;
        ModLog.Info($"[pause] '{restart.name}' navigation {nav.mode} (give-up button beside it)");
        if (nav.mode != Navigation.Mode.Explicit) { var auto = giveUp.navigation; auto.mode = nav.mode; giveUp.navigation = auto; return; }
        var mine = nav;
        mine.selectOnLeft = restart;
        mine.selectOnRight = null;
        giveUp.navigation = mine;
        nav.selectOnRight = giveUp;
        restart.navigation = nav;
    }

    // A copy of the pause menu button `src`, placed right after `after`, running `onClick`; linked into the up/down
    // navigation after `after`
    private static GameObject Copy(MyButton src, Transform after, string name, System.Action onClick, out TextMeshProUGUI copyLabel, bool wire = true)
    {
        copyLabel = null;
        var hold = new GameObject("hold");
        hold.SetActive(false);
        var copy = Object.Instantiate(src.gameObject, hold.transform);
        copy.name = name;
        // v0.48.5 (user: no hover sound, no darkening like the other buttons): the copy keeps the game's MyButton
        // (hover sound PlaySfx, scale, the corner arrows via OnSelect) and its Button (the colour tint); only the
        // Button's click action is changed. Other game scripts and the label's LocalizeStringEvent go (names logged).
        var myButton = copy.GetComponent<MyButton>();
        var removed = new List<string>();
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || (myButton != null && mb.Pointer == myButton.Pointer)) continue;
            string asm = mb.GetIl2CppType().Assembly.GetName().Name, n = mb.GetIl2CppType().Name;
            if (asm == "Assembly-CSharp" || n == "LocalizeStringEvent") { removed.Add($"{n} on '{mb.name}'"); Object.DestroyImmediate(mb); }
        }
        var b2 = copy.GetComponent<Button>();
        if (myButton == null || b2 == null)
        {
            // Not the expected shape: the plain-Button fallback of v0.48.4
            ModLog.Warn($"[pause] '{src.name}' copy: MyButton {(myButton != null)}, Button {(b2 != null)} - plain button instead");
            if (myButton != null) Object.DestroyImmediate(myButton);
            foreach (var sel in copy.GetComponentsInChildren<Selectable>(true)) if (sel != null) Object.DestroyImmediate(sel);
            b2 = copy.AddComponent<Button>();
            b2.targetGraphic = copy.GetComponent<Graphic>() ?? copy.GetComponentsInChildren<Graphic>(true).FirstOrDefault();
            myButton = null;
        }
        // The copied Button's click calls (set in the editor, e.g. "open settings") are switched off, except the
        // MyButton's own ones (its click sound) - all logged by name
        var calls = new List<string>();
        for (int i = 0; i < b2.onClick.GetPersistentEventCount(); i++)
        {
            var t = b2.onClick.GetPersistentTarget(i);
            string m = b2.onClick.GetPersistentMethodName(i);
            bool own = myButton != null && t != null && t.Pointer == myButton.Pointer;
            if (!own) b2.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            calls.Add($"{t?.GetIl2CppType().Name}.{m} ({(own ? "kept" : "off")})");
        }
        b2.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
        copy.transform.SetParent(after.parent, false);
        copy.transform.SetSiblingIndex(after.GetSiblingIndex() + 1);
        Object.Destroy(hold);
        copyLabel = copy.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
        copy.SetActive(true);
        if (wire) WireNavigation(after.GetComponent<Selectable>(), b2);
        ModLog.Info($"[pause] button '{name}' added after '{after.name}' ({(copyLabel != null ? "label found" : "NO label")}); " +
                    $"MyButton kept {(myButton != null)}; removed [{string.Join(", ", removed)}]; click calls [{string.Join(", ", calls)}]; " +
                    $"colors normal {b2.colors.normalColor} highlighted {b2.colors.highlightedColor} selected {b2.colors.selectedColor}");
        return copy;
    }
}
