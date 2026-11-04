using Assets.Scripts.Inventory__Items__Pickups.GoldAndMoney;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Separate chest prices (user: "theres a chest gold coin stat ... how much gold it costs to open a chest, make sure that
// is also tracked separately for p2"; "that ui element needs to be created for p2 as well, and p1's chest gold ui
// element needs to move to their screen").
// Game (interop): MoneyUtility keeps one run-wide count (static chestsPurchased, chestPriceIncrease); GetChestPrice()
// prices from it; IncreaseChestPrice() (a chest bought) raises it and fires A_ChestPriceIncreased; the HUD's
// KillsAndGoldCounter.t_chestPrice shows it (top right of the screen - P2's half in split screen).
//  - P2 has its own values, swapped into MoneyUtility while P2 deals with chests (P2's prompt, P2's interaction,
//    P2's chest window) - P2's purchases raise P2's price only. The price event is muted during the swap (P1's HUD
//    would show P2's price) and P1's number refreshed after. A reset of the game's values (new run/stage) resets P2's.
//  - HUD: P1's chest price moves to P1's half (after P1's kill counter, same row); P2 gets a copy at the original spot
//    (top right, above P2's gold) showing P2's price.
internal static class P2ChestPrice
{
    private static int p2Purchased;
    private static float p2Increase;
    private static bool inited;
    private static int depth;
    private static int heldPurchased;
    private static float heldIncrease;
    private static Il2CppSystem.Action heldEvent;
    private static int lastGamePurchased = -1;
    private static int logs;

    public static int P2Price { get; private set; }

    private static void Init()
    {
        if (inited) return;
        p2Purchased = MoneyUtility.chestsPurchased;
        p2Increase = MoneyUtility.chestPriceIncrease;
        inited = true;
        ModLog.Info($"[chestprice] P2 starts with the game's chest values: chestsPurchased {p2Purchased}, chestPriceIncrease {p2Increase:0.###}, base {MoneyUtility.chestBasePrice}, price {MoneyUtility.GetChestPrice()}");
    }

    // Runs `a` with P2's chest values in the game's place (nests)
    public static void Run(Action a)
    {
        SwapIn();
        try { a(); }
        finally { SwapOut(); }
    }

    public static void SwapIn()
    {
        if (depth++ > 0) return;
        Init();
        heldPurchased = MoneyUtility.chestsPurchased;
        heldIncrease = MoneyUtility.chestPriceIncrease;
        heldEvent = MoneyUtility.A_ChestPriceIncreased;
        MoneyUtility.A_ChestPriceIncreased = null;
        MoneyUtility.chestsPurchased = p2Purchased;
        MoneyUtility.chestPriceIncrease = p2Increase;
    }

    public static void SwapOut()
    {
        if (depth == 0 || --depth > 0) return;
        bool bought = MoneyUtility.chestsPurchased != p2Purchased || !Mathf.Approximately(MoneyUtility.chestPriceIncrease, p2Increase);
        if (bought && logs++ < 10)
            ModLog.Info($"[chestprice] P2's chest values {p2Purchased}/{p2Increase:0.###} -> {MoneyUtility.chestsPurchased}/{MoneyUtility.chestPriceIncrease:0.###} (P1's stay {heldPurchased}/{heldIncrease:0.###})");
        p2Purchased = MoneyUtility.chestsPurchased;
        p2Increase = MoneyUtility.chestPriceIncrease;
        MoneyUtility.chestsPurchased = heldPurchased;
        MoneyUtility.chestPriceIncrease = heldIncrease;
        MoneyUtility.A_ChestPriceIncreased = heldEvent;
        heldEvent = null;
        if (bought) RefreshP1();
    }

    // While P2's values are in the game: P1's for one call (P2Gold.BeginP1's chest price side)
    private static int p2ParkedPurchased;
    private static float p2ParkedIncrease;
    private static bool p1In;
    public static bool BeginP1()
    {
        if (depth == 0 || p1In) return false;
        p1In = true;
        p2ParkedPurchased = MoneyUtility.chestsPurchased;
        p2ParkedIncrease = MoneyUtility.chestPriceIncrease;
        MoneyUtility.chestsPurchased = heldPurchased;
        MoneyUtility.chestPriceIncrease = heldIncrease;
        return true;
    }
    public static void EndP1()
    {
        if (!p1In) return;
        p1In = false;
        heldPurchased = MoneyUtility.chestsPurchased;
        heldIncrease = MoneyUtility.chestPriceIncrease;
        MoneyUtility.chestsPurchased = p2ParkedPurchased;
        MoneyUtility.chestPriceIncrease = p2ParkedIncrease;
    }

    private static void RefreshP1()
    {
        try { counter?.OnChestPriceIncreased(); } catch { }
    }

    // ---------- HUD ----------
    private static KillsAndGoldCounter counter;
    private static RectTransform p1Elem, p2Elem;
    private static TextMeshProUGUI p2Text;
    private static Vector3 p1Home;
    private static bool moved;
    private static float nextFind, nextPrice;

    // Each frame in a run (P2Hud.Update)
    public static void Update()
    {
        if (depth > 0) return;
        if (counter == null)
        {
            p1Elem = p2Elem = null;
            p2Text = null;
            moved = false;
            if (Time.unscaledTime < nextFind) return;
            nextFind = Time.unscaledTime + 1f;
            counter = Object.FindObjectOfType<KillsAndGoldCounter>();
            if (counter == null || counter.t_chestPrice == null) { counter = null; return; }
            // The chest price's own block (icon + number): v0.49 took the text's parent, a full-width container (left
            // edge 0, never placed) - now the largest parent under 400 px wide; the chain is logged
            p1Elem = counter.t_chestPrice.rectTransform;
            var chain = new List<string>();
            for (var t = counter.t_chestPrice.transform.parent?.GetComponent<RectTransform>(); t != null; t = t.parent?.GetComponent<RectTransform>())
            {
                chain.Add($"'{t.name}' {t.rect.width:F0}x{t.rect.height:F0}");
                if (t.rect.width >= 400f || t.GetComponent<Canvas>() != null) break;
                p1Elem = t;
            }
            ModLog.Info($"[chestprice] P1's chest price text '{counter.t_chestPrice.name}' - parents {string.Join(" < ", chain)}; block used: '{p1Elem.name}'");
            p1Home = p1Elem.position;
            UiDump.DumpOnce("chestprice_dump.txt", counter.transform);   // TEMP: the counters as found
        }
        WatchGameReset();
        bool p2Playing = PlayerJoin.HasP2 && P2Movement.Active;
        Place(p2Playing);
        if (!p2Playing) { if (p2Elem != null && p2Elem.gameObject.activeSelf) p2Elem.gameObject.SetActive(false); return; }
        if (p2Elem == null) Build();
        if (p2Elem == null) return;
        bool show = p1Elem.gameObject.activeInHierarchy;
        if (p2Elem.gameObject.activeSelf != show) p2Elem.gameObject.SetActive(show);
        if (Time.unscaledTime >= nextPrice)
        {
            nextPrice = Time.unscaledTime + 0.25f;
            int price = 0;
            Run(() => { var bonus = P2Inventory.EnterP2(); try { price = MoneyUtility.GetChestPrice(); } finally { if (bonus) P2Inventory.Exit(); } });
            P2Price = price;
            if (p2Text != null) p2Text.text = WithNumber(counter.t_chestPrice.text, price);
        }
    }

    // The game reset its values (new run / new stage): P2's follow
    private static void WatchGameReset()
    {
        int now = MoneyUtility.chestsPurchased;
        if (inited && lastGamePurchased > 0 && now < lastGamePurchased)
        {
            p2Purchased = now;
            p2Increase = MoneyUtility.chestPriceIncrease;
            ModLog.Info($"[chestprice] the game reset its chest values ({lastGamePurchased} -> {now}): P2's reset too");
        }
        lastGamePurchased = now;
    }

    // P1's text with its number replaced (keeps the game's format) - the LAST number: the text starts with the coin
    // icon's sprite tag (v0.49 screenshot: replacing the first number changed the icon and kept P1's price)
    private static string WithNumber(string p1Text, int n)
    {
        var m = System.Text.RegularExpressions.Regex.Match(p1Text ?? "", @"\d+(?!.*\d)");
        return m.Success ? p1Text.Substring(0, m.Index) + n + p1Text.Substring(m.Index + m.Length) : n.ToString();
    }

    private static float Left(RectTransform rt) => rt.position.x - rt.rect.width * rt.pivot.x * rt.lossyScale.x;

    private static void Place(bool split)
    {
        if (p1Elem == null) return;
        if (split && !moved)
        {
            // The right end of P1's top row (timer, silver, gold, kills), just left of the game timer in the middle.
            // The text box is much wider than its right-aligned number (v0.49 screenshot): the visible text's edge
            // (TMP textBounds) is placed, 110 px left of the screen's middle
            var t = counter.t_chestPrice;
            t.ForceMeshUpdate();
            float visibleRight = t.rectTransform.position.x + t.textBounds.max.x * t.rectTransform.lossyScale.x;
            if (visibleRight <= 1f || t.textBounds.size.x <= 0.5f) return;   // layout not built yet: next frame
            float target = Screen.width * 0.5f - 110f * t.rectTransform.lossyScale.x;
            float dx = target - visibleRight;
            p1Elem.position = p1Home + new Vector3(dx, 0f, 0f);
            moved = true;
            ModLog.Info($"[chestprice] P1's chest price moved: visible right edge x {visibleRight:F0} -> {target:F0} (left of the middle timer)");
        }
        else if (!split && moved) { p1Elem.position = p1Home; moved = false; }
    }

    private static void Build()
    {
        var holder = new GameObject("MegabonkSplitscreen_ChestPriceHolder");
        holder.SetActive(false);
        holder.transform.SetParent(p1Elem.parent, false);
        var copy = Object.Instantiate(p1Elem.gameObject, holder.transform);
        copy.name = "MegabonkSplitscreen_P2ChestPrice";
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetIl2CppType().Assembly.GetName().Name == "Assembly-CSharp") Object.DestroyImmediate(mb);
        copy.transform.SetParent(p1Elem.parent, false);
        Object.Destroy(holder);
        p2Elem = copy.GetComponent<RectTransform>();
        p2Elem.position = p1Home;   // where P1's was: top right, P2's half
        // The copy's text: the one at the same path as P1's chest price text
        string path = counter.t_chestPrice.transform == p1Elem ? "" : RelPath(p1Elem, counter.t_chestPrice.transform);
        p2Text = (path == "" ? copy.transform : copy.transform.Find(path))?.GetComponent<TextMeshProUGUI>();
        copy.SetActive(true);
        ModLog.Info($"[chestprice] P2's chest price made at {p1Home} ({(p2Text != null ? "text found" : "NO text")})");
    }

    private static string RelPath(Transform root, Transform t)
    {
        var parts = new List<string>();
        while (t != null && t != root) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }

    public static void Reset()
    {
        if (p1Elem != null && moved) p1Elem.position = p1Home;
        moved = false;
        if (p2Elem != null) Object.Destroy(p2Elem.gameObject);
        p2Elem = null;
        counter = null;
        inited = false;
        lastGamePurchased = -1;
        depth = 0;
    }
}
