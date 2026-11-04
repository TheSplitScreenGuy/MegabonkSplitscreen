using Assets.Scripts.Actors.Player;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MegabonkSplitscreen;

// Separate gold for each player (user). The game keeps one amount, PlayerInventory.gold (_gold_k__BackingField /
// _goldInt_k__BackingField), shown by KillsAndGoldCounter (t_gold, UpdateGoldCounter).
//  - P2's gold is the mod's own amount; gold coins P2 collects go to it (P2Pickups: the gold the game added to P1's
//    amount moves to P2's, P1's is put back)
//  - whenever P2 spends (P2's interactions: chests, shrines; P2's windows: shady guy, chest...) P2's amount is swapped
//    into the game for that moment and what's left goes back to P2's purse (SwapIn / SwapOut, nestable)
//  - P2's amount shows on P2's HUD panel: a copy of the game's gold row, its number replaced
internal static class P2Gold
{
    public static float Amount { get; private set; }
    private static int depth;
    public static bool SwappedIn => depth > 0;   // P2's gold is the game's right now (GoldPopups)
    private static float p1Held;

    public static void SwapIn()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (inv == null) return;
        if (depth++ > 0) return;
        p1Held = inv._gold_k__BackingField;
        Set(inv, Amount);
    }

    public static void SwapOut()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (depth == 0) return;
        if (--depth > 0) return;
        if (inv == null) return;
        Amount = inv._gold_k__BackingField;
        Set(inv, p1Held);
        Refresh();
    }

    // While P2's gold is in the game (a P2 window): P1's own amount in its place for one call (a P1 chest paid at the
    // close of P2's window - InteractableChest pays at whichever chest window closes next). Returns false if not needed.
    private static float p2Parked;
    private static bool p1In;
    public static bool BeginP1()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (inv == null || depth == 0 || p1In) return false;
        p1In = true;
        p2Parked = inv._gold_k__BackingField;
        Set(inv, p1Held);
        return true;
    }
    public static void EndP1()
    {
        var inv = MyPlayer.Instance?.inventory;
        if (!p1In) return;
        p1In = false;
        if (inv == null) return;
        p1Held = inv._gold_k__BackingField;
        Set(inv, p2Parked);
    }

    public static void Run(Action a)
    {
        SwapIn();
        try { a(); }
        finally { SwapOut(); }
    }

    // From P2Pickups: a P2 gold coin was applied to P1's amount
    public static void Collected(float before)
    {
        var inv = MyPlayer.Instance?.inventory;
        if (inv == null || depth > 0) return;
        float delta = inv._gold_k__BackingField - before;
        if (delta <= 0f) return;
        Amount += delta;
        Set(inv, before);
        Refresh();
        GoldPopups.MovedToP2(Mathf.RoundToInt(delta));   // the game's "$" popup counted it as P1's
        if (logs++ < 5) ModLog.Info($"[gold] P2 +{delta:F0} -> {Amount:F0} (P1 stays {before:F0})");
    }
    private static int logs;

    private static void Set(PlayerInventory inv, float value)
    {
        inv._gold_k__BackingField = value;
        inv._goldInt_k__BackingField = Mathf.FloorToInt(value);
    }

    // P1's counter shows P1's amount again
    private static void Refresh()
    {
        try
        {
            if (counter == null) counter = Object.FindObjectOfType<KillsAndGoldCounter>();   // cached: P2's passive swaps gold every frame
            counter?.UpdateGoldCounter();
        }
        catch { }
    }
    private static KillsAndGoldCounter counter;

    public static void ResetForNewRun()
    {
        Amount = 0f;
        depth = 0;
        hudCopy = null;
        counter = null;
        goldCounter = null;
        lastHudAmount = int.MinValue;
    }

    // ---------- P2's gold on P2's HUD panel ----------
    private static GameObject hudCopy;
    private static TextMeshProUGUI hudText;

    public static void UpdateHud(Transform panel)
    {
        if (panel == null) return;
        if (hudCopy == null) MakeHud(panel);
        if (hudText == null) return;
        // The game's text with its number replaced by P2's (keeps the coin icon tag and formatting).
        // 1.0 alpha [perf] (user: Steam Deck frame drops; this took ~1.3 ms a frame on the PC): the game's counter was
        // searched for in the whole scene every frame and the text rebuilt with a regex - now the counter is kept and
        // the text only rebuilt when P2's amount or the game's text changes
        if (goldCounter == null) goldCounter = Object.FindObjectOfType<KillsAndGoldCounter>()?.t_gold;
        string src = goldCounter != null ? goldCounter.text : "0";
        int amount = Mathf.FloorToInt(depth > 0 ? (MyPlayer.Instance?.inventory?._gold_k__BackingField ?? Amount) : Amount);
        if (amount == lastHudAmount && src == lastHudSource) return;
        lastHudAmount = amount;
        lastHudSource = src;
        string n = amount.ToString();
        var m = System.Text.RegularExpressions.Regex.Matches(src, @"\d+");
        hudText.text = m.Count > 0 ? src.Substring(0, m[m.Count - 1].Index) + n + src.Substring(m[m.Count - 1].Index + m[m.Count - 1].Length) : n;
    }
    private static TextMeshProUGUI goldCounter;
    private static int lastHudAmount = int.MinValue;
    private static string lastHudSource;

    private static void MakeHud(Transform panel)
    {
        var counter = Object.FindObjectOfType<KillsAndGoldCounter>();
        var row = counter?.t_gold != null ? counter.t_gold.transform.parent : null;   // 'Gold' {HorizontalLayoutGroup}
        if (row == null) return;
        var holder = new GameObject("MegabonkSplitscreen_P2GoldHolder");
        holder.SetActive(false);
        holder.transform.SetParent(panel, false);
        var copy = Object.Instantiate(row.gameObject, holder.transform);
        copy.name = "MegabonkSplitscreen_P2Gold";
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetIl2CppType().Assembly.GetName().Name == "Assembly-CSharp") Object.DestroyImmediate(mb);
        copy.transform.SetParent(panel, false);
        copy.transform.SetSiblingIndex(0);   // top of P2's column
        Object.Destroy(holder);
        copy.SetActive(true);
        hudText = copy.GetComponentInChildren<TextMeshProUGUI>(true);
        hudCopy = copy;
        ModLog.Info($"P2 gold on P2's HUD (copy of '{row.name}', text {(hudText != null)})");
    }
}
