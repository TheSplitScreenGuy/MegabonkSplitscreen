using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace MegabonkSplitscreen;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "thesplitscreenguy.megabonksplitscreen";
    public const string Name = "MegabonkSplitscreen";
    public const string Version = "1.0.0-alpha";
    public const string ReleaseName = "SplitBonk V1.0 Alpha";   // the menu label, the README title, the release zip's name

    private static Harmony hooks;
    private static BepInEx.Configuration.ConfigFile config;
    public static bool HooksOn { get; private set; }

    public override void Load()
    {
        string pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        ModLog.Init(Log, pluginDir);
        UiDump.Init(pluginDir);
        EventDump.Init(pluginDir);
        Autotest.Init(pluginDir);   // only with 'autotest_run.txt' in this folder (renamed at once)
        ModLog.Info($"{Name} {Version} loading");

        // The online block has its own Harmony id (modded runs must not reach leaderboards, stats or achievements).
        OnlineBlock.Apply(new Harmony(Guid + ".online"));

        config = Config;
        hooks = new Harmony(Guid);
        ApplyHooks();
        CharacterSelectView.Init(Config);
        ModLog.DebugLogging = Config.Bind("Debug", "DebugLogging", false,
            "Write the mod's diagnostic log lines and UI dump files (for bug hunting; costs a little speed). Errors, warnings and the main events are always logged.").Value;
        P2Camera.Configure(Config);
        Billboards.Init();
        FallDamage.Init();
        AddComponent<Ticker>();

        ModLog.Info("Loaded");
    }

    private static void ApplyHooks()
    {
        PlayerJoin.Apply(hooks);
        Lobby.Apply(hooks);
        SelectionArrowFix.Apply(hooks);
        P2Body.Apply(hooks);
        P2Weapons.Apply(hooks);
        P2Health.Apply(hooks);
        P2Pickups.Apply(hooks);
        P2LevelUp.Apply(hooks);
        P2Inventory.Apply(hooks, config);
        P2Movement.Apply(hooks, config);
        FreezeDiag.Apply(hooks);
        P2Spawns.Apply(hooks, config);   // after the others: checks for shared native code
        P2Weapons.ApplyMeleeHooks(hooks);
        P2Weapons.ApplyReturnHooks(hooks);
        P2Interact.Apply(hooks);
        P2Explosions.Apply(hooks);
        P2Rewards.Apply(hooks);
        P2Popups.Apply(hooks);
        P2GameHealth.Apply(hooks, config);
        P2Abilities.Apply(hooks);
        Downed.Apply(hooks);
        P2Hazards.Apply(hooks);
        P2BossAttacks.Apply(hooks);
        P2Debuffs.Apply(hooks);
        P2EndScreen.Apply(hooks);
        GoldPopups.Apply(hooks);
        P2Weapons.ApplyAimHooks(hooks);
        P2Weapons.ApplyTargetingHooks(hooks);   // after the melee/return hooks (one hook per native function)
        Knockback.Apply(hooks);
        P2Items.Apply(hooks);                   // last: its projectile hit scopes only take methods nobody else needs
        // v0.25.3 dump: P2's starting AddWeapon -> UpdateStat -> CombatAura.OnStatUpdate (through these hooks) never
        // returned = the freeze when P2 took control. Not applied any more (kept for reference).
        // P2Weapons.ApplyConstantHooks(hooks);
        HooksOn = true;
    }

    // Note (crash dump, v0.18.3): Harmony "unpatch" leaves its generated wrapper around an IL2CPP method in place, so
    // hooks are never removed at runtime any more; methods that must stay unwrapped are simply never hooked.
}
