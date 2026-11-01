using HarmonyLib;
using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// The player lobby (user: a dedicated player screen after the main menu's Play - player 2 presses Start and the model
// appears, B leaves; groundwork for 4 players, so first everyone confirms who plays; a Confirm button in the middle
// near the bottom moves on to character selection; P2 starts on P1's character).
// It is the first stage of the game's character select screen (the 3D models and camera are already there):
//  - character select opened from the main menu (MainMenu.GoToCharacterSelection) -> the lobby; coming back from the
//    map select goes straight to character selection
//  - the game's character select UI (P1's grid and info, P2's panel) is hidden and inert (a CanvasGroup on their area);
//    the 3D avatars stay: P2's appears next to P1's when P2 joins (PlayerJoin), showing P1's character
//  - four player cards: P1 joined, P2 "press START" / joined ("B to leave"), P3/P4 coming later
//  - Confirm: P1's submit (pad A / Enter) or a click; then character select's UI comes back and P2's pick starts on P1's
internal static class Lobby
{
    public static bool Active { get; private set; }
    private static bool fromMainMenu;
    private static GameObject root;
    private static CanvasGroup hiddenGroup;
    private static bool hiddenGroupAdded;
    private static readonly Card[] cards = new Card[4];
    private static Button confirm;
    private static float openedAt;
    private static bool p2BWasDown;
    private const int MaxPlayers = 4;
    private static readonly Color[] PlayerColors = { new(0.3f, 0.6f, 1f), new(1f, 0.3f, 0.3f), new(0.35f, 0.9f, 0.35f), new(1f, 0.85f, 0.25f) };

    private class Card { public Image bg; public TextMeshProUGUI title, status; }

    public static void Apply(Harmony harmony)
    {
        var m = AccessTools.DeclaredMethod(typeof(MainMenu), nameof(MainMenu.GoToCharacterSelection));
        if (m != null && P2Weapons.IsNativeCodeUnique(m) && !P2Weapons.IsNativeCodeHooked(m))
        {
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(Lobby), nameof(BeforeCharacterSelection)));
            ModLog.Info("Lobby: shown after the main menu's Play");
        }
        else ModLog.Warn("Lobby: MainMenu.GoToCharacterSelection not hookable - the lobby opens with every character select");
        hookOk = m != null && P2Weapons.IsNativeCodeUnique(m);
    }
    private static bool hookOk;

    private static void BeforeCharacterSelection() => fromMainMenu = true;

    // From PlayerJoin when character select opens
    public static void OnCharacterSelectOpened(CharacterMenu menu)
    {
        bool show = fromMainMenu || !hookOk;
        fromMainMenu = false;
        if (!show) return;
        Active = true;
        openedAt = Time.unscaledTime;
        p2BWasDown = true;   // the press that may still be down doesn't count
        // v0.48.4 (user: P1 could pick characters in the background with the stick): the UI's navigation is off while
        // the lobby is open, and P1's pick is held where it was
        lobbyMenu = menu;
        heldButton = menu != null ? menu.selectedButton : null;
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es != null) { navWas = es.sendNavigationEvents; es.sendNavigationEvents = false; navOff = true; }
        Hide(menu, true);
        Build();
        root.SetActive(true);
        Refresh();
        ModLog.Info($"[lobby] opened ({(PlayerJoin.HasP2 ? "P2 already in" : "waiting for P2")})");
    }

    // From PlayerJoin when character select closes (back to the main menu, or on to the map)
    public static void OnCharacterSelectClosed()
    {
        if (!Active) return;
        Active = false;
        if (root != null) root.SetActive(false);
        Hide(null, false);
    }

    // Each frame (Ticker): P2's B leaves, P1's submit confirms, the cards follow who is in
    public static void Update()
    {
        if (!Active) return;
        Refresh();
        HoldP1Pick();
        // The game's selection highlight (SelectionArrow corners) of the hidden character grid showed through
        // (lobby screenshot) - off while the lobby is open, back after
        foreach (var arrow in Object.FindObjectsOfType<SelectionArrow>())
            if (arrow != null && arrow.gameObject.activeSelf) { arrow.gameObject.SetActive(false); hiddenArrows.Add(arrow.gameObject); }
        var joy = PlayerJoin.P2Joystick;
        if (joy != null)
        {
            bool b = P2Control.PadHeld(joy, "b", "circle");
            if (b && !p2BWasDown) { PlayerJoin.Leave(); ModLog.Info("[lobby] P2 left (B)"); }
            p2BWasDown = b;
        }
        else p2BWasDown = false;
        if (Time.unscaledTime - openedAt > 0.4f)
        {
            bool submit = false;
            try { submit = ReInput.players.GetPlayer(0).GetButtonDown("UISubmit"); } catch { }
            if (submit) Confirm();
        }
    }

    public static void Confirm()
    {
        if (!Active) return;
        Active = false;
        if (root != null) root.SetActive(false);
        Hide(null, false);
        if (PlayerJoin.HasP2) P2Select.DefaultToP1();
        ModLog.Info($"[lobby] confirmed: {(PlayerJoin.HasP2 ? 2 : 1)} player(s) - on to character selection");
    }

    // ---------- The game's character select UI: hidden and inert during the lobby ----------
    private static readonly List<GameObject> hiddenArrows = new();
    private static CharacterMenu lobbyMenu;
    private static MyButtonCharacter heldButton;
    private static bool navOff, navWas;
    private static int holdLogs;

    // P1's pick held while the lobby is open (anything that still changed it is put back)
    private static void HoldP1Pick()
    {
        if (lobbyMenu == null || heldButton == null) return;
        var now = lobbyMenu.selectedButton;
        if (now == null || now.Pointer == heldButton.Pointer) return;
        try { lobbyMenu.OnSelectCharacter(heldButton); } catch { }
        if (holdLogs++ < 5) ModLog.Info($"[lobby] P1's pick changed behind the lobby ({now.characterData?.eCharacter}) - put back to {heldButton.characterData?.eCharacter}");
    }

    private static void Hide(CharacterMenu menu, bool hide)
    {
        if (!hide)
        {
            foreach (var a in hiddenArrows) if (a != null) a.SetActive(true);
            hiddenArrows.Clear();
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (navOff && es != null) es.sendNavigationEvents = navWas;
            navOff = false;
            lobbyMenu = null;
            heldButton = null;
        }
        if (hide)
        {
            var area = menu != null ? menu.transform.parent : null;
            if (area == null) return;
            hiddenGroup = area.GetComponent<CanvasGroup>();
            hiddenGroupAdded = hiddenGroup == null;
            if (hiddenGroupAdded) hiddenGroup = area.gameObject.AddComponent<CanvasGroup>();
        }
        if (hiddenGroup == null) return;
        hiddenGroup.alpha = hide ? 0f : 1f;
        hiddenGroup.interactable = !hide;
        hiddenGroup.blocksRaycasts = !hide;
        if (!hide)
        {
            if (hiddenGroupAdded) Object.Destroy(hiddenGroup);
            hiddenGroup = null;
        }
    }

    // ---------- The lobby's own screen ----------
    private static void Build()
    {
        if (root != null) return;
        root = new GameObject("MegabonkSplitscreen_Lobby");
        Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 900;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();

        Text("Title", root.transform, "PLAYERS", 72f, new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(900f, 100f), Color.white);
        Text("Hint", root.transform, "Players: press START to join  -  B to leave", 30f, new Vector2(0.5f, 1f), new Vector2(0f, -160f), new Vector2(1200f, 50f), new Color(1f, 1f, 1f, 0.8f));

        // Four cards along the lower part of the screen
        const float cardW = 330f, cardH = 120f, gap = 30f;
        float total = MaxPlayers * cardW + (MaxPlayers - 1) * gap;
        for (int i = 0; i < MaxPlayers; i++)
        {
            var go = new GameObject($"Card P{i + 1}");
            go.transform.SetParent(root.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(cardW, cardH);
            rt.anchoredPosition = new Vector2(-total / 2f + cardW / 2f + i * (cardW + gap), 260f);
            var bg = go.AddComponent<Image>();
            var card = new Card { bg = bg };
            card.title = Text("Title", go.transform, $"PLAYER {i + 1}", 40f, new Vector2(0.5f, 0.5f), new Vector2(0f, 22f), new Vector2(cardW, 50f), PlayerColors[i]);
            card.status = Text("Status", go.transform, "", 26f, new Vector2(0.5f, 0.5f), new Vector2(0f, -28f), new Vector2(cardW, 40f), Color.white);
            cards[i] = card;
        }

        // P1's input toggle under P1's card (1.0.1, player request + user: keyboard/mouse or controller for P1)
        var tgo = new GameObject("P1 Input");
        tgo.transform.SetParent(root.transform, false);
        var trt = tgo.AddComponent<RectTransform>();
        trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0f);
        trt.pivot = new Vector2(0.5f, 0.5f);
        trt.sizeDelta = new Vector2(cardW, 56f);
        trt.anchoredPosition = new Vector2(-total / 2f + cardW / 2f, 260f - cardH / 2f - 40f);
        var timg = tgo.AddComponent<Image>();
        timg.color = new Color(PlayerColors[0].r * 0.25f, PlayerColors[0].g * 0.25f, PlayerColors[0].b * 0.25f, 0.95f);
        var tbtn = tgo.AddComponent<Button>();
        tbtn.targetGraphic = timg;
        tbtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => ToggleP1Input()));
        inputLabel = Text("Label", tgo.transform, "", 26f, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(cardW, 56f), Color.white);

        // Confirm, in the middle near the bottom
        var cgo = new GameObject("Confirm");
        cgo.transform.SetParent(root.transform, false);
        var crt = cgo.AddComponent<RectTransform>();
        crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0f);
        crt.pivot = new Vector2(0.5f, 0.5f);
        crt.sizeDelta = new Vector2(420f, 90f);
        crt.anchoredPosition = new Vector2(0f, 110f);
        var cimg = cgo.AddComponent<Image>();
        cimg.color = new Color(0.15f, 0.55f, 0.2f, 0.95f);
        confirm = cgo.AddComponent<Button>();
        confirm.targetGraphic = cimg;
        confirm.onClick.AddListener((UnityEngine.Events.UnityAction)(() => Confirm()));
        Text("Label", cgo.transform, "CONFIRM", 48f, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 90f), Color.white);
        root.SetActive(false);
        ModLog.Info("[lobby] screen built");
    }

    private static TextMeshProUGUI inputLabel;

    private static void ToggleP1Input()
    {
        bool kbm = !PlayerJoin.P1KeyboardMouse;
        PlayerJoin.SetP1Input(kbm);
        ModLog.Info($"[lobby] P1 input switched to {(kbm ? "keyboard + mouse" : "controller")}");
        Refresh();
    }

    private static void Refresh()
    {
        if (cards[0] == null) return;
        bool kbm = PlayerJoin.P1KeyboardMouse;
        if (inputLabel != null) inputLabel.text = kbm ? "P1: KEYBOARD + MOUSE  (click)" : "P1: CONTROLLER  (click)";
        for (int i = 0; i < MaxPlayers; i++)
        {
            bool joined = i == 0 || (i == 1 && PlayerJoin.HasP2);
            bool comingLater = i >= 2;
            var c = cards[i];
            c.bg.color = joined ? new Color(PlayerColors[i].r * 0.35f, PlayerColors[i].g * 0.35f, PlayerColors[i].b * 0.35f, 0.9f) : new Color(0f, 0f, 0f, 0.55f);
            c.title.color = comingLater ? new Color(1f, 1f, 1f, 0.3f) : PlayerColors[i];
            c.status.text = i == 0 ? (kbm ? "READY  (keyboard + mouse)" : "READY  (controller)") : joined ? "JOINED  (B to leave)" : comingLater ? "coming later" : "press START to join";
            c.status.color = comingLater ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
        }
    }

    private static TextMeshProUGUI Text(string name, Transform parent, string text, float size, Vector2 anchor, Vector2 pos, Vector2 box, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        var font = Object.FindObjectOfType<TextMeshProUGUI>()?.font;   // the game's font
        if (font != null) t.font = font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = TextAlignmentOptions.Center;
        t.enableWordWrapping = false;
        t.raycastTarget = false;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = box;
        rt.anchoredPosition = pos;
        return t;
    }
}
