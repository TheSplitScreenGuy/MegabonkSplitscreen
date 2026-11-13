using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Player 2 picking a character on the P2 panel (the inert copy of P1's grid, see P2Panel).
//  - P2's pad (read via Rewired polling, like the join) moves a cursor over the copy's buttons:
//    d-pad or left stick, with key-repeat while held
//  - A picks the character under the cursor (if P1's matching button says it is usable):
//    P2's avatar changes and P2's info panel shows that character's name, weapon and passive
// Each copy button is matched to P1's live button by its position in the hierarchy, so the
// character data and lock state always come from the game's own buttons.
internal static class P2Select
{
    public static CharacterData Picked { get; private set; }

    private static readonly Color PickedBorder = new(1f, 0.75f, 0.2f, 1f);
    private const float CursorScale = 1.1f;
    private const float RepeatDelay = 0.35f;
    private const float RepeatInterval = 0.12f;

    private class Slot
    {
        public MyButtonCharacter p1;
        public RectTransform rt;
        public GameObject hover;
        public Image border;
        public Color borderColor;
    }

    private static readonly List<Slot> slots = new();
    private static GameObject boundPanel;
    private static int cursor = -1;
    private static int pickedSlot = -1;

    // Info panel parts in the copy (null when not found)
    private static TextMeshProUGUI tName, tDesc, tWeaponName, tWeaponDesc, tPassiveName, tPassiveDesc;
    private static RawImage iWeapon, iPassive, iCharacter;

    // Input state
    private static Vector2Int heldDir;
    private static float nextRepeat;
    private static string prevButton;
    private static readonly HashSet<string> seenInputs = new();   // TEMP: log each pad element name once

    public static void Bind(CharacterMenu menu, GameObject panel)
    {
        slots.Clear();
        cursor = -1;
        skinCursor = -1;
        pickedSlot = -1;
        boundPanel = panel;
        boundMenu = menu;
        boundAt = Time.unscaledTime;
        pickedThisVisit = false;

        var buttons = menu.characterButtons;
        int count = buttons != null ? buttons.Count : 0;
        for (int i = 0; i < count; i++)
        {
            var b = buttons[i];
            if (b == null) continue;
            var t = Mirror(menu.transform, b.transform, panel.transform);
            var rt = t != null ? t.TryCast<RectTransform>() : null;
            if (rt == null) continue;

            var slot = new Slot { p1 = b, rt = rt };
            if (b.hoverOverlay != null)
            {
                var h = Mirror(menu.transform, b.hoverOverlay.transform, panel.transform);
                slot.hover = h != null ? h.gameObject : null;
            }
            var borderT = t.Find("Border");
            slot.border = borderT != null ? borderT.GetComponent<Image>() : null;
            if (slot.border != null) slot.borderColor = slot.border.color;
            slots.Add(slot);
        }

        BindInfo(menu, panel);
        onConfirm = false;
        var cb = menu.b_confirm;
        var ct = cb != null ? Mirror(menu.transform, cb.transform, panel.transform) : null;
        confirmRt = ct != null ? ct.TryCast<RectTransform>() : null;
        ModLog.Info($"P2 select: P2's Confirm button {(confirmRt != null ? $"'{confirmRt.name}' found" : "not found in the copy")}");
        ModLog.Info($"P2 select: {slots.Count} of {count} character buttons matched in the P2 panel");

        // Coming back to character select after a run: the panel and avatar are new, re-apply P2's pick
        if (Picked != null)
        {
            int idx = slots.FindIndex(s => s.p1.characterData != null && s.p1.characterData.Pointer == Picked.Pointer);
            if (idx >= 0) Pick(idx, quiet: true);
        }
    }

    // P2 left the lobby: no panel, no pick (a new P2 starts on P1's character again)
    public static void Unbind()
    {
        slots.Clear();
        boundPanel = null;
        cursor = -1;
        pickedSlot = -1;
        Picked = null;
        PickedSkin = null;
        skinCursor = -1;
        onConfirm = false;
        if (p2Arrow != null) Object.Destroy(p2Arrow);
        p2Arrow = null;
        p2ArrowPart = null;
        skinCopies.Clear();
        skinGrid = null;
    }

    // Lobby confirmed: P2's pick starts on P1's character (user: in the new screen default P2's character to P1's)
    public static void DefaultToP1()
    {
        if (Picked != null) return;
        if (!PickCharacter(CharacterMenu.selectedCharacter)) ModLog.Info($"P2 select: couldn't default P2 to P1's {CharacterMenu.selectedCharacter}");
        else ModLog.Info($"P2 select: P2 starts on P1's character ({CharacterMenu.selectedCharacter})");
    }

    // Finds the copy of `target` (somewhere under rootA) under rootB by walking the same child indices.
    // Names repeat in this UI ("Header", "Content"), so a name path could pick the wrong one.
    internal static Transform Mirror(Transform rootA, Transform target, Transform rootB)
    {
        var path = new List<int>();
        for (var t = target; t != rootA; t = t.parent)
        {
            if (t == null) return null;   // target isn't under rootA
            path.Add(t.GetSiblingIndex());
        }
        var cur = rootB;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (path[i] >= cur.childCount) return null;
            cur = cur.GetChild(path[i]);
        }
        return cur;
    }

    private static void BindInfo(CharacterMenu menu, GameObject panel)
    {
        var info = Object.FindObjectOfType<CharacterInfoUI>();   // P1's; the copy's script was removed
        if (info == null)
        {
            ModLog.Warn("P2 select: P1's info panel not found, P2 info won't update");
            return;
        }
        TextMeshProUGUI Text(TextMeshProUGUI src)
        {
            var t = src != null ? Mirror(menu.transform, src.transform, panel.transform) : null;
            return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
        }
        RawImage Img(RawImage src)
        {
            var t = src != null ? Mirror(menu.transform, src.transform, panel.transform) : null;
            return t != null ? t.GetComponent<RawImage>() : null;
        }
        tName = Text(info.t_name);
        tDesc = Text(info.t_description);
        tWeaponName = Text(info.t_weaponName);
        tWeaponDesc = Text(info.t_weaponDesc);
        tPassiveName = Text(info.t_passiveName);
        tPassiveDesc = Text(info.t_passiveDesc);
        iWeapon = Img(info.i_weapon);
        iPassive = Img(info.i_passive);
        iCharacter = Img(info.i_character);
        int found = new Object[] { tName, tDesc, tWeaponName, tWeaponDesc, tPassiveName, tPassiveDesc, iWeapon, iPassive, iCharacter }
            .Count(o => o != null);
        ModLog.Info($"P2 select: {found} of 9 info panel parts found in the copy");
        try { BindSkins(info, menu, panel); }
        catch (Exception e) { ModLog.Warn($"P2 skins: binding the skin row failed: {e.Message}"); }
    }

    // ---------- P2's skin (user: P2 can't select character skins) ----------
    // The game's skins per character: DataManager.GetSkins (SkinData: icon, materials), sorted like P1's row
    // (SkinSelection sorts with SkinData.CompareTo). P2's shoulder buttons step through the skins P2 may use; the copied
    // skin row shows P2's character's skins (copies of one copied SkinContainer); the skin goes on P2's avatar
    // (PlayerRenderer.SetSkin) and on P2's body in the run (P2Body). Each character remembers P2's last skin.
    public static SkinData PickedSkin { get; private set; }
    public static string SkinRendererName { get; private set; }   // the model's skinned renderer the skin goes on
    private static readonly Dictionary<ECharacter, int> skinIndex = new();
    private static Transform skinGrid;
    private static GameObject skinTemplate;
    private static List<int> iconPath, lockedPath, notSelectedPath, purchasePath, pricePath;
    private static readonly List<GameObject> skinCopies = new();
    private static bool skinsCompared;

    private static List<int> PathOf(Transform root, Component c)
    {
        if (c == null) return null;
        var path = new List<int>();
        for (var t = c.transform; t != root; t = t.parent)
        {
            if (t == null) return null;
            path.Add(t.GetSiblingIndex());
        }
        path.Reverse();
        return path;
    }
    private static Transform Follow(Transform root, List<int> path)
    {
        if (path == null) return null;
        var cur = root;
        foreach (int i in path)
        {
            if (i >= cur.childCount) return null;
            cur = cur.GetChild(i);
        }
        return cur;
    }

    private static void BindSkins(CharacterInfoUI info, CharacterMenu menu, GameObject panel)
    {
        foreach (var g in skinCopies) if (g != null) Object.Destroy(g);
        skinCopies.Clear();
        skinGrid = null;
        skinMenu = menu;
        skinPanel = panel;
        lateTries = 0;
        var list = info.skinSelection != null ? info.skinSelection.containers : null;
        if (list == null || list.Count == 0 || list[0] == null)
        {
            // v1.0.4 (user: after going back to the main menu P2 only had one 50-gold skin): character select opened
            // again after a run fills P1's skin row a moment later, so the row is bound late (BindSkinsLate)
            ModLog.Info("P2 skins: P1's skin row not filled yet - P2's row is bound once it is");
            return;
        }
        var c0 = list[0];
        var copy = Mirror(menu.transform, c0.transform, panel.transform);
        if (copy == null)
        {
            ModLog.Info("P2 skins: P1's skin container not in the copy - P2's row is bound from P1's row");
            return;
        }
        Paths(c0);
        skinGrid = copy.parent;
        skinTemplate = copy.gameObject;
        for (int i = 0; i < skinGrid.childCount; i++) skinGrid.GetChild(i).gameObject.SetActive(false);   // P1's skins, copied
        ModLog.Info($"P2 skins: skin row bound ('{skinGrid.name}', {list.Count} copied containers hidden; icon {iconPath != null}, locked {lockedPath != null}, not-selected {notSelectedPath != null})");
    }

    private static CharacterMenu skinMenu;
    private static GameObject skinPanel;
    private static int lateTries;

    // P2's row from P1's row as it is now: the copy's grid (same place as P1's grid), a script-free copy of P1's first
    // skin container as the template, every child the copy had hidden (P1's row as it was when copied)
    private static void BindSkinsLate()
    {
        if (skinGrid != null || skinMenu == null || skinPanel == null || lateTries > 600) return;
        lateTries++;
        var list = Object.FindObjectOfType<CharacterInfoUI>()?.skinSelection?.containers;
        if (list == null || list.Count == 0 || list[0] == null) return;
        var c0 = list[0];
        var grid = Mirror(skinMenu.transform, c0.transform.parent, skinPanel.transform);
        if (grid == null) { if (lateTries == 1) ModLog.Warn("P2 skins: P1's skin grid not found in the copy"); lateTries = 1000; return; }
        Paths(c0);
        var hold = new GameObject("hold");
        hold.SetActive(false);
        var tpl = Object.Instantiate(c0.gameObject, hold.transform);
        foreach (var mb in tpl.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetIl2CppType().Assembly.GetName().Name == "Assembly-CSharp") Object.DestroyImmediate(mb);
        for (int i = 0; i < grid.childCount; i++) grid.GetChild(i).gameObject.SetActive(false);   // the copy's old row
        tpl.SetActive(false);
        tpl.transform.SetParent(grid, false);
        tpl.name = "MegabonkSplitscreen_P2SkinTemplate";
        Object.Destroy(hold);
        skinGrid = grid;
        skinTemplate = tpl;
        ModLog.Info($"P2 skins: skin row bound late ('{grid.name}', after {lateTries} tries; icon {iconPath != null}, locked {lockedPath != null}, not-selected {notSelectedPath != null})");
        if (Picked != null) SkinForPick(Picked);   // P2's row for P2's current character
    }

    private static void Paths(SkinContainer c0)
    {
        var root = c0.transform;
        iconPath = PathOf(root, c0.skinIcon);
        lockedPath = PathOf(root, c0.locked != null ? c0.locked.transform : null);
        notSelectedPath = PathOf(root, c0.notSelectedOverlay != null ? c0.notSelectedOverlay.transform : null);
        purchasePath = PathOf(root, c0.purchaseOverlay != null ? c0.purchaseOverlay.transform : null);
        pricePath = PathOf(root, c0.t_price);
    }

    // The skins P2's row shows: only the ones P2 may use (1.0 alpha, user: locked / not-bought skins aren't shown -
    // P1's row lists them with a lock or a price, P2's leaves them out)
    private static List<SkinData> Skins(ECharacter c) => AllSkins(c).Where(Owned).ToList();

    private static List<SkinData> AllSkins(ECharacter c)
    {
        var res = new List<SkinData>();
        var dm = DataManager.Instance;
        var src = dm != null ? dm.GetSkins(c) : null;
        if (src == null) return res;
        for (int i = 0; i < src.Count; i++) if (src[i] != null) res.Add(src[i]);
        res.Sort((a, b) => a.CompareTo(b));
        return res;
    }

    public static bool TestAllSkins;   // autotest "allskins"
    private static bool Owned(SkinData s)
    {
        if (TestAllSkins) return true;
        try { return Assets.Scripts.Saves___Serialization.Progression.Unlocks.MyAchievements.IsAvailable(s); }
        catch { return true; }
    }

    // TEMP autotest 'skins <ECharacter>': each skin's state in the save next to what P2's row uses
    public static string DescribeSkins(ECharacter c)
    {
        var parts = new List<string>();
        foreach (var s in AllSkins(c))
        {
            string st;
            try
            {
                var A = Assets.Scripts.Saves___Serialization.Progression.Unlocks.MyAchievements.IsAvailable(s);
                var P = Assets.Scripts.Saves___Serialization.Progression.Unlocks.MyAchievements.IsPurchased(s);
                var U = Assets.Scripts.Saves___Serialization.Progression.Unlocks.MyAchievements.IsUnlocked(s, out string _);
                st = $"available {A}, purchased {P}, unlocked {U}, price {s.GetPrice()}, P2 may use {Owned(s)}";
            }
            catch (Exception e) { st = "error " + e.Message; }
            parts.Add($"{SkinName(s)}: {st}");
        }
        // P1's own row, when it shows this character
        var list = Object.FindObjectOfType<CharacterInfoUI>()?.skinSelection?.containers;
        if (list != null && list.Count > 0 && list[0]?.skin != null && list[0].skin.character == c)
            for (int i = 0; i < list.Count; i++)
                if (list[i]?.skin != null && list[i].gameObject.activeInHierarchy)
                    parts.Add($"P1 row {SkinName(list[i].skin)}: locked {list[i].IsLocked()}, need purchase {list[i].NeedPurchase()}");
        return $"skins {c}: {string.Join(" | ", parts)} | P2's row: {string.Join(", ", Skins(c).Select(SkinName))}";
    }

    private static string SkinName(SkinData s)
    {
        if (s == null) return "none";
        try { return $"{s.GetName()} ({s.skinType})"; }
        catch { return s.name; }
    }

    // TEMP [skins]: P2's skin list and ownership check next to P1's own row (for P1's character), once
    private static void CompareWithP1(List<SkinData> mine, ECharacter c)
    {
        if (skinsCompared) return;
        var sel = Object.FindObjectOfType<CharacterInfoUI>()?.skinSelection;
        var list = sel != null ? sel.containers : null;
        if (list == null || list.Count == 0 || list[0]?.skin == null || list[0].skin.character != c) return;
        skinsCompared = true;
        var p1 = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var k = list[i];
            if (k == null || k.skin == null || !k.gameObject.activeInHierarchy) continue;
            p1.Add($"{SkinName(k.skin)} locked {k.IsLocked()} needPurchase {k.NeedPurchase()}");
        }
        ModLog.Info($"[skins] P1's row for {c}: {string.Join(" | ", p1)}");
        ModLog.Info($"[skins] P2's list for {c}: {string.Join(" | ", mine.Select(s => $"{SkinName(s)} available {Owned(s)}"))}");
    }

    // Picked a character: P2's last skin for it (or the first), on the avatar and the row
    private static void SkinForPick(CharacterData data)
    {
        var skins = Skins(data.eCharacter);
        CompareWithP1(skins, data.eCharacter);
        skinIndex.TryGetValue(data.eCharacter, out int idx);
        if (idx < 0 || idx >= skins.Count || !Owned(skins[idx])) idx = 0;
        UseSkin(data, skins, idx);
    }

    private static void UseSkin(CharacterData data, List<SkinData> skins, int idx)
    {
        PickedSkin = idx >= 0 && idx < skins.Count ? skins[idx] : null;
        skinIndex[data.eCharacter] = idx;
        if (PickedSkin != null) SkinRendererName = CharacterSelectView.SetP2Skin(PickedSkin) ?? SkinRendererName;
        ShowSkinRow(skins, idx);
    }

    private static void ShowSkinRow(List<SkinData> skins, int selected)
    {
        foreach (var g in skinCopies) if (g != null) { g.SetActive(false); Object.Destroy(g); }
        skinCopies.Clear();
        if (skinGrid == null || skinTemplate == null) return;
        for (int i = 0; i < skins.Count; i++)
        {
            var g = Object.Instantiate(skinTemplate, skinGrid);
            g.name = $"MegabonkSplitscreen_P2Skin{i}";
            g.SetActive(true);
            var t = g.transform;
            bool owned = Owned(skins[i]);
            var icon = Follow(t, iconPath)?.GetComponent<RawImage>();
            if (icon != null) icon.texture = skins[i].GetIcon();
            Follow(t, lockedPath)?.gameObject.SetActive(!owned);
            Follow(t, notSelectedPath)?.gameObject.SetActive(i != selected);
            Follow(t, purchasePath)?.gameObject.SetActive(false);
            Follow(t, pricePath)?.gameObject.SetActive(false);
            skinCopies.Add(g);
        }
        // the cursor stays on its skin (the row was rebuilt)
        if (skinCursor >= skinCopies.Count) skinCursor = -1;
        if (skinCursor >= 0) ScaleSkin(skinCursor, CursorScale);
    }

    // P2's body in the run: P2's skin if it belongs to P2's character
    public static SkinData SkinFor(CharacterData data) =>
        PickedSkin != null && data != null && PickedSkin.character == data.eCharacter ? PickedSkin : null;

    // ---------- P2 confirms (user: P2 can press confirm, pick the map and start the game) ----------
    // A on P2's already-picked character, or Start, confirms like P1's Confirm button (the game's
    // CharacterMenu.OnConfirmCharacter with P1's selected character). Once P2 lets go of the buttons, the game's menu
    // controls go to P2's pad (the handover the pause and level-up screens use), so P2 drives the map select and Start
    // with the game's own UI. Controls go back to P1 when character select opens again (P2 backed out) or the run starts.
    private static CharacterMenu boundMenu;
    private static float boundAt;
    private static bool handoverPending, handedOver;
    public static string State => $"P2 pick {SafeName(Picked)}, skin {SkinName(PickedSkin)} (cursor on '{CursorRect?.name}'{(onConfirm ? " = CONFIRM" : "")}, slots {slots.Count} (active {slots.Count(s => s.rt != null && s.rt.gameObject.activeInHierarchy)}), cursor slot {cursor}, skin cursor {skinCursor} of {skinCopies.Count}, picked this visit {pickedThisVisit}), confirm pending {handoverPending}, map select controls P2's {handedOver}";
    private static float pendingSince;
    private const float HandoverWait = 5f;   // the game takes a moment to leave character select after Confirm
    private static bool startWasDown;
    // v0.25.1: the carried-over pick from the last round sat under the cursor, so P2's first A confirmed instead of
    // picking. A only confirms a character P2 picked during this visit.
    private static bool pickedThisVisit;
    private const float StartGuard = 1f;   // P2 joined with Start: that press mustn't confirm

    private static void Confirm()
    {
        if (Picked == null || boundMenu == null) return;
        var p1Button = boundMenu.selectedButton;
        if (p1Button == null)
        {
            ModLog.Warn("P2 confirm: P1 has no character selected, ignored");
            return;
        }
        try
        {
            boundMenu.OnConfirmCharacter(p1Button);
            // v0.49.6 (user: sometimes the player who confirmed couldn't get to map select): OnConfirmCharacter only moves
            // the game's highlight to its Confirm button ('B_Confirm', autotest log v0.40.2) - P1's second A press is
            // what goes on. For P2 that press is made too: a pad's "submit" on the Confirm button, next frame
            submitConfirmAt = Time.unscaledTime + 0.1f;
            handoverPending = true;
            pendingSince = Time.unscaledTime;
            ModLog.Info($"P2 confirmed character select (P1 {SafeName(p1Button.characterData)}, P2 {SafeName(Picked)}); map select goes to P2 once the buttons are released");
        }
        catch (Exception e) { ModLog.Error($"P2 confirm failed: {e}"); }
    }

    private static float submitConfirmAt = -1f;
    private static bool confirmLogged;
    private static void SubmitConfirm()
    {
        if (submitConfirmAt < 0f || Time.unscaledTime < submitConfirmAt) return;
        submitConfirmAt = -1f;
        var cb = boundMenu?.b_confirm;
        if (cb == null || !PlayerJoin.InCharacterSelect) return;
        try
        {
            var btn = cb.GetComponent<UnityEngine.UI.Button>();
            if (!confirmLogged && btn != null)
            {
                confirmLogged = true;
                var calls = new List<string>();
                for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++) calls.Add($"{btn.onClick.GetPersistentTarget(i)?.GetIl2CppType().Name}.{btn.onClick.GetPersistentMethodName(i)}");
                ModLog.Info($"P2 confirm: the game's Confirm button '{cb.name}' (interactable {btn.interactable}) - its click calls [{string.Join(", ", calls)}]");
            }
            var es = UnityEngine.EventSystems.EventSystem.current;
            UnityEngine.EventSystems.ExecuteEvents.Execute(cb.gameObject, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
            ModLog.Info("P2 confirm: Confirm button pressed (submit, like a pad)");
        }
        catch (Exception e) { ModLog.Warn($"P2 confirm: pressing Confirm failed: {e.Message}"); }
    }

    private static void UpdateHandover(Joystick joy)
    {
        SubmitConfirm();
        // Hand over once character select has closed (map select open) and P2 has let go of the buttons
        if (handoverPending && !P2Control.PadHeld(joy, "a", "cross", "start", "menu", "options"))
        {
            if (!PlayerJoin.InCharacterSelect || boundPanel == null || !boundPanel.activeInHierarchy)
            {
                handoverPending = false;
                P2LevelUp.GiveControlsToP2();
                handedOver = true;
                ModLog.Info($"P2 confirm: map select is P2's ({Time.unscaledTime - pendingSince:F1} s after confirming)");
            }
            else if (Time.unscaledTime - pendingSince > HandoverWait)
            {
                handoverPending = false;
                ModLog.Info("P2 confirm: character select didn't close, no handover");
            }
        }
        // The run started: P1 plays with P1's pad again
        if (handedOver && Assets.Scripts.Actors.Player.MyPlayer.Instance != null) EndHandover("the run started");
    }

    // From PlayerJoin when character select opens (P2 backed out of the map select)
    public static void OnCharacterSelectOpened()
    {
        handoverPending = false;
        if (handedOver) EndHandover("character select opened again");
    }

    private static void EndHandover(string why)
    {
        handedOver = false;
        P2LevelUp.RestoreControls();
        ModLog.Info($"P2 menu control ended: {why}");
    }

    // Called every frame by the Ticker.
    // ---------- P2's corner arrows ----------
    // User: the 4 corner arrows round a character icon or the Confirm button don't appear for P2. A copy of the game's
    // arrows (SelectionArrow.Instance, 'SelectionArrowNEW'; its script off) in P2's red, on whatever P2's cursor is on:
    // centred on it, the game's size rule (button rect + sizeOffset), scaled like P1's (SelectionArrowFix.ScaleFor).
    private static int arrowErrors;
    private static GameObject p2Arrow;
    private static RectTransform p2ArrowRt, p2ArrowPart;
    private static float arrowOffset = 30f;
    private static readonly Color P2ArrowTint = new(1f, 0.35f, 0.35f, 1f);

    private static RectTransform CursorRect =>
        onConfirm ? confirmRt : skinCursor >= 0 ? SkinRt(skinCursor) : cursor >= 0 && cursor < slots.Count ? slots[cursor].rt : null;

    private static void UpdateArrow()
    {
        bool show = PlayerJoin.HasP2 && PlayerJoin.InCharacterSelect && !Lobby.Active && !handoverPending && !handedOver
                    && boundPanel != null && boundPanel.activeInHierarchy;
        var target = show ? CursorRect : null;
        if (target == null || !target.gameObject.activeInHierarchy)
        {
            if (p2Arrow != null && p2Arrow.activeSelf) p2Arrow.SetActive(false);
            return;
        }
        if (p2Arrow == null)
        {
            var src = SelectionArrow.Instance;
            if (src == null) return;
            arrowOffset = src.sizeOffset;
            // made under a switched-off holder: the copy's SelectionArrow script must never wake up (its Awake takes over
            // SelectionArrow.Instance, and removing it afterwards took the copy with it - made 4 times, never shown)
            var holder = new GameObject("MegabonkSplitscreen_ArrowHolder");
            holder.SetActive(false);
            holder.transform.SetParent(src.transform.parent, false);
            p2Arrow = Object.Instantiate(src.gameObject, holder.transform);
            p2Arrow.name = "MegabonkSplitscreen_P2Arrow";
            var script = p2Arrow.GetComponent<SelectionArrow>();
            if (script != null) Object.DestroyImmediate(script);
            p2Arrow.transform.SetParent(src.transform.parent, false);
            Object.Destroy(holder);
            // [arrow] log: the script's object is a full-screen layer; the corners are its arrow rect (SelectionArrow.
            // rectTransform) inside a clipping 'Mask' (SelectionArrow.mask) - the copy's same parts, by path
            var root = src.transform;
            var arrowPart = Follow(p2Arrow.transform, PathOf(root, src.rectTransform));
            var maskPart = src.mask != null ? Follow(p2Arrow.transform, PathOf(root, src.mask)) : null;
            p2ArrowPart = arrowPart != null ? arrowPart.TryCast<RectTransform>() : null;
            if (maskPart != null)
            {
                // no clipping for P2's corners: the mask spans the screen and doesn't cut
                foreach (var mb in maskPart.GetComponents<Behaviour>())
                    if (mb != null && (mb.GetIl2CppType().Name == "Mask" || mb.GetIl2CppType().Name == "RectMask2D")) mb.enabled = false;
                var mrt = maskPart.TryCast<RectTransform>();
                if (mrt != null) { mrt.anchorMin = Vector2.zero; mrt.anchorMax = Vector2.one; mrt.offsetMin = mrt.offsetMax = Vector2.zero; mrt.localScale = Vector3.one; }
            }
            if (p2ArrowPart != null) { p2ArrowPart.anchorMin = p2ArrowPart.anchorMax = new Vector2(0.5f, 0.5f); p2ArrowPart.pivot = new Vector2(0.5f, 0.5f); }
            foreach (var g in p2Arrow.GetComponentsInChildren<Graphic>(true)) if (g != null) g.color = g.color * P2ArrowTint;
            p2ArrowRt = p2Arrow.transform.TryCast<RectTransform>();
            ModLog.Info($"P2 select: P2's corner arrows made (copy of '{src.name}', size offset {arrowOffset:F0})");
        }
        var part = p2ArrowPart != null ? p2ArrowPart : p2ArrowRt;
        if (part == null) return;
        if (!p2Arrow.activeSelf) p2Arrow.SetActive(true);
        part.position = target.position;
        part.sizeDelta = target.rect.size + Vector2.one * arrowOffset;
        float k = SelectionArrowFix.ScaleFor(target, part.parent);
        part.localScale = new Vector3(k, k, 1f);
        p2ArrowRt = part;   // (for the log)
        // TEMP [arrow] (the arrows showed far from P2's button): the numbers when the target changes
        if (target.Pointer != lastArrowTarget && arrowLogs++ < 8)
        {
            lastArrowTarget = target.Pointer;
            var par = p2ArrowRt.parent;
            ModLog.Info($"[arrow] P2 target '{target.name}' pos {target.position} rect {target.rect.size} lossy {target.lossyScale.x:F2}; arrow pos {p2ArrowRt.position} " +
                        $"anchored {p2ArrowRt.anchoredPosition} anchors {p2ArrowRt.anchorMin}-{p2ArrowRt.anchorMax} pivot {p2ArrowRt.pivot} size {p2ArrowRt.sizeDelta}; " +
                        $"parent '{par?.name}' pos {par?.position} lossy {par?.lossyScale} ({par?.GetComponents<Component>().Length} parts); P1 arrow pos {SelectionArrow.Instance?.rectTransform?.position}");
        }
    }
    private static IntPtr lastArrowTarget;
    private static int arrowLogs;

    // v1.0.5 (players: after the first run P2's character select cursor kept going back to Confirm, P2 couldn't pick):
    // a run that ended while a P2 window (chest, reward, level-up, pause) had the game's controls never got its close
    // hook - P2's pad stayed on the game's player through the menus, driving the game's own selection (on P1's Confirm).
    // Outside a run nothing but P2's map-select handover may keep them.
    public static bool ControlsFix = true;
    private static void ReleaseStaleControls()
    {
        if (!ControlsFix || handoverPending || handedOver || !P2LevelUp.ControlsWithP2) return;
        var p1 = Assets.Scripts.Actors.Player.MyPlayer.Instance;
        if (p1 != null) return;   // (Unity's check: the last run's destroyed player counts as none)
        ModLog.Warn("P2 select: the game's controls were still P2's outside a run (a P2 window was open when the run ended) - back to P1");
        P2LevelUp.RestoreControls();
    }

    public static void Update()
    {
        try { ReleaseStaleControls(); } catch (Exception e) { if (arrowErrors++ < 3) ModLog.Warn($"P2 select controls: {e.Message}"); }
        try { UpdateArrow(); }
        catch (Exception e) { if (arrowErrors++ < 3) ModLog.Warn($"P2 arrows: {e.Message}"); }
        if (skinGrid == null && boundPanel != null && boundPanel.activeInHierarchy)
            try { BindSkinsLate(); } catch (Exception e) { lateTries = 1000; ModLog.Warn($"P2 skins: late binding failed: {e.Message}"); }
        var joy = PlayerJoin.P2Joystick;
        if (joy == null && !Autotest.Active) return;   // (a test's P2 without a pad: the script's presses drive the cursor)
        if (joy != null) UpdateHandover(joy);
        if (Lobby.Active) return;   // the lobby first (P2's Start there is the join, B is leave)
        if (boundPanel == null || !boundPanel.activeInHierarchy || slots.Count == 0) return;
        if (!PlayerJoin.InCharacterSelect || handoverPending || handedOver) return;

        if (cursor < 0 || cursor >= slots.Count) SetCursor(FirstVisible());

        var polling = ReInput.controllers.polling;
        string button;
        if (Autotest.Active) button = Autotest.P2FirstButton();   // the menu test presses P2's buttons
        else
        {
            var bp = polling.PollControllerForFirstButton(ControllerType.Joystick, joy.id);
            button = bp.success ? (bp.elementIdentifierName ?? "").Trim().ToLowerInvariant() : null;
            if (button != null && seenInputs.Add("button:" + button))
                ModLog.Info($"P2 pad button seen: '{bp.elementIdentifierName}' (index {bp.elementIndex})");
        }

        // Start: confirm (not the press P2 joined with)
        bool startDown = joy != null && P2Control.PadHeld(joy, "start", "menu", "options");
        bool startPressed = startDown && !startWasDown;
        startWasDown = startDown;
        if (startPressed && Time.unscaledTime - boundAt > StartGuard)
        {
            if (Picked == null) Pick(cursor, quiet: false);
            Confirm();
            prevButton = button;
            return;
        }

        // A: pick (on the press, not while held); A on the character P2 already picked = confirm; A on a skin = that skin
        if (button != prevButton && (button == "a" || button == "cross"))
        {
            if (onConfirm) { if (Picked != null) Confirm(); }
            else if (skinCursor >= 0) PickSkinAt(skinCursor);
            else if (cursor == pickedSlot && Picked != null && pickedThisVisit) Confirm();
            else
            {
                Pick(cursor, quiet: false);
                pickedThisVisit = Picked != null && cursor == pickedSlot;
                if (pickedThisVisit) SetConfirmCursor();   // like P1's: the highlight goes to Confirm
            }
        }
        prevButton = button;

        Vector2Int dir = button != null ? DirFromName(button) : Vector2Int.zero;
        if (dir == Vector2Int.zero && !Autotest.Active && joy != null)   // (the test moves P2's cursor with d-pad presses)
        {
            var ap = polling.PollControllerForFirstAxis(ControllerType.Joystick, joy.id);
            if (ap.success)
            {
                string axis = (ap.elementIdentifierName ?? "").ToLowerInvariant();
                if (seenInputs.Add("axis:" + axis + ":" + ap.axisPole))
                    ModLog.Info($"P2 pad axis seen: '{ap.elementIdentifierName}' {ap.axisPole}");
                // v1.0.6 (user: back at character select after a run, P2's cursor went left by itself into the skin row,
                // "like stick drift"): polling can report an axis that isn't pushed (seen before with released triggers).
                // The stick must really be pushed at least half-way (the direction stays polling's: the user's pad read
                // Y with the opposite sign to polling's pole, and the first version of this check dropped every up/down)
                float v = joy.GetAxisById(ap.elementIdentifierId);
                bool pushed = Mathf.Abs(v) >= StickPush;
                if (pushed) dir = DirFromAxis(axis, ap.axisPole);
                else if (axis.Contains("left stick") && staleAxisLogs++ < 5)
                    ModLog.Info($"[pads] P2's character select: polling said '{ap.elementIdentifierName}' {ap.axisPole}, the stick reads {v:F2} - ignored");
            }
        }

        float now = Time.unscaledTime;
        if (dir == Vector2Int.zero)
        {
            heldDir = Vector2Int.zero;
        }
        else if (dir != heldDir)
        {
            heldDir = dir;
            nextRepeat = now + RepeatDelay;
            Move(dir);
        }
        else if (now >= nextRepeat)
        {
            nextRepeat = now + RepeatInterval;
            Move(dir);
        }
    }

    private const float StickPush = 0.5f;
    private static int staleAxisLogs;

    // D-pad buttons, e.g. "D-Pad Up"
    private static Vector2Int DirFromName(string n)
    {
        if (!n.Contains("pad") && !n.Contains("dpad")) return Vector2Int.zero;
        if (n.EndsWith("up")) return Vector2Int.up;
        if (n.EndsWith("down")) return Vector2Int.down;
        if (n.EndsWith("left")) return Vector2Int.left;
        if (n.EndsWith("right")) return Vector2Int.right;
        return Vector2Int.zero;
    }

    // Left stick, e.g. "Left Stick X" / "Left Stick Y" (Rewired: Y positive = up)
    private static Vector2Int DirFromAxis(string n, Pole pole)
    {
        if (!n.Contains("left stick")) return Vector2Int.zero;
        int sign = pole == Pole.Positive ? 1 : -1;
        if (n.EndsWith("x")) return new Vector2Int(sign, 0);
        if (n.EndsWith("y")) return new Vector2Int(0, sign);
        return Vector2Int.zero;
    }

    private static int FirstVisible()
    {
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].rt != null && slots[i].rt.gameObject.activeInHierarchy) return i;
        return -1;
    }

    // Nearest visible button in the pressed direction (screen space), preferring ones in line. The skin row's copies
    // are buttons too (user: P2 selects skins the same way as P1, through character select): the cursor moves into the
    // row like any other button.
    private static void Move(Vector2Int dir)
    {
        if (cursor < 0) return;
        var fromRt = onConfirm ? confirmRt : skinCursor >= 0 ? SkinRt(skinCursor) : slots[cursor].rt;
        if (fromRt == null) { SetCursor(cursor); return; }
        Vector3 from = fromRt.position;
        Vector2 d = dir;
        int best = -1, bestSkin = -1;   // bestSkin -2 = P2's Confirm button
        float bestScore = float.MaxValue;
        void Consider(RectTransform rt, int ci, int si)
        {
            if (rt == null || rt == fromRt || !rt.gameObject.activeInHierarchy) return;
            Vector2 delta = rt.position - from;
            float along = Vector2.Dot(delta, d);
            if (along < 1f) return;
            float side = Mathf.Abs(d.x != 0 ? delta.y : delta.x);
            float score = along + side * 2f;
            if (score < bestScore)
            {
                bestScore = score;
                best = ci;
                bestSkin = si;
            }
        }
        for (int i = 0; i < slots.Count; i++) Consider(slots[i].rt, i, -1);
        for (int i = 0; i < skinCopies.Count; i++) Consider(SkinRt(i), -1, i);
        Consider(confirmRt, -1, -2);
        if (bestSkin == -2) SetConfirmCursor();
        else if (bestSkin >= 0) SetSkinCursor(bestSkin);
        else if (best >= 0) SetCursor(best);
    }

    // P2's Confirm button (the copy of P1's B_Confirm in P2's panel; user: the corner arrows go round a character or
    // the Confirm button for P1, not for P2). Like P1's: picking a character moves the cursor onto it; A there confirms.
    private static RectTransform confirmRt;
    private static bool onConfirm;
    private static void SetConfirmCursor()
    {
        if (confirmRt == null) return;
        SetSkinCursor(-1);
        if (cursor >= 0 && cursor < slots.Count) ShowCursor(slots[cursor], false);
        onConfirm = true;
    }

    private static void SetCursor(int i)
    {
        onConfirm = false;
        SetSkinCursor(-1);
        if (cursor >= 0 && cursor < slots.Count) ShowCursor(slots[cursor], false);
        cursor = i;
        if (cursor >= 0) ShowCursor(slots[cursor], true);
    }

    // The cursor on a skin (-1 = on the characters). The character keeps its hover look off meanwhile.
    private static int skinCursor = -1;
    private static RectTransform SkinRt(int i) =>
        i >= 0 && i < skinCopies.Count && skinCopies[i] != null ? skinCopies[i].transform.TryCast<RectTransform>() : null;
    private static void SetSkinCursor(int i)
    {
        ScaleSkin(skinCursor, 1f);
        skinCursor = i;
        if (i < 0) return;
        onConfirm = false;
        if (cursor >= 0 && cursor < slots.Count) ShowCursor(slots[cursor], false);
        ScaleSkin(i, CursorScale);
    }
    private static void ScaleSkin(int i, float k)
    {
        var rt = SkinRt(i);
        if (rt != null) rt.localScale = new Vector3(k, k, 1f);
    }

    private static void PickSkinAt(int i)
    {
        if (Picked == null) return;
        var skins = Skins(Picked.eCharacter);
        if (i < 0 || i >= skins.Count) return;
        if (!Owned(skins[i]))
        {
            ModLog.Info($"P2 skin: {SkinName(skins[i])} is locked");
            return;
        }
        UseSkin(Picked, skins, i);
        ModLog.Info($"P2 skin: {SkinName(PickedSkin)} ({i + 1} of {skins.Count})");
    }

    private static void ShowCursor(Slot s, bool on)
    {
        if (s.rt == null) return;
        if (s.hover != null) s.hover.SetActive(on);
        float k = on ? CursorScale : 1f;
        s.rt.localScale = new Vector3(k, k, 1f);
    }

    // Autotest: P2 picks a character by its id
    public static bool PickCharacter(ECharacter c)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            var data = slots[i].p1?.characterData;
            if (data == null || data.eCharacter != c) continue;
            Pick(i, quiet: false);
            return Picked != null && Picked.eCharacter == c;
        }
        return false;
    }

    private static void Pick(int i, bool quiet)
    {
        if (i < 0 || i >= slots.Count) return;
        var s = slots[i];
        var data = s.p1.characterData;
        if (data == null)
        {
            ModLog.Warn("P2 select: button has no character data");
            return;
        }
        if (!s.p1.canUseCharacter)
        {
            ModLog.Info($"P2 select: {SafeName(data)} is locked ({s.p1.cantUseCharacterReason})");
            return;
        }

        if (pickedSlot >= 0 && pickedSlot < slots.Count && slots[pickedSlot].border != null)
            slots[pickedSlot].border.color = slots[pickedSlot].borderColor;
        pickedSlot = i;
        if (s.border != null) s.border.color = PickedBorder;

        Picked = data;
        CharacterSelectView.SetP2Character(data);
        UpdateInfo(data);
        try { SkinForPick(data); }
        catch (Exception e) { ModLog.Warn($"P2 skin for {SafeName(data)} failed: {e.Message}"); }
        if (!quiet) ModLog.Info($"P2 picked {SafeName(data)}");
    }

    private static void UpdateInfo(CharacterData data)
    {
        Try("name", () => { if (tName != null) tName.text = data.GetName(); });
        Try("description", () => { if (tDesc != null) tDesc.text = data.GetDescription(); });
        Try("portrait", () => { if (iCharacter != null) iCharacter.texture = data.GetIcon(); });
        var w = data.weapon;
        if (w != null)
        {
            Try("weapon name", () => { if (tWeaponName != null) tWeaponName.text = w.GetName(); });
            Try("weapon description", () => { if (tWeaponDesc != null) tWeaponDesc.text = w.GetDescription(); });
            Try("weapon icon", () => { if (iWeapon != null) iWeapon.texture = w.GetIcon(); });
        }
        var p = data.passive;
        if (p != null)
        {
            Try("passive name", () => { if (tPassiveName != null) tPassiveName.text = p.GetName(); });
            Try("passive description", () => { if (tPassiveDesc != null) tPassiveDesc.text = p.GetDescription(); });
            Try("passive icon", () => { if (iPassive != null) iPassive.texture = p.icon; });
        }
    }

    private static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { ModLog.Warn($"P2 info {what} failed: {e.Message}"); }
    }

    private static string SafeName(CharacterData d)
    {
        try { return d.GetName(); }
        catch { return d.eCharacter.ToString(); }
    }
}
