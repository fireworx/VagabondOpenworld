using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace OpenworldAddon;

/// <summary>
/// Addon for the Vagabond mod: trader exits no longer end the raid, they offer the trader screen;
/// the hideout exit offers a menu. Vagabond itself is not modified, its exfil trigger is patched on top.
/// </summary>
[BepInPlugin("dev.local.vagabond-openworld", "Vagabond Openworld", "1.0.0")]
[BepInDependency("dev.oogabooga.spt-vagabond")]
[BepInDependency("com.egbog.openbarters", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    public static Plugin Instance { get; private set; }
    private static ManualLogSource _log;

    private static ConfigEntry<bool> _traderZone;
    private static ConfigEntry<bool> _hideoutZone;
    private static ConfigEntry<bool> _debugUnityErrors;

    public static bool TraderZoneEnabled => _traderZone?.Value ?? false;
    public static bool HideoutZoneEnabled => _hideoutZone?.Value ?? false;
    public static bool DebugUnityErrors => _debugUnityErrors?.Value ?? false;

    private void Awake()
    {
        Instance = this;
        _log = Logger;

        _traderZone = Config.Bind("General", "Trader Zone Interaction", true,
            "Trader exfils show an 'Open <trader>' interaction instead of extracting.");
        _hideoutZone = Config.Bind("General", "Hideout Zone Menu", true,
            "The hideout exfil offers 'Send items to hideout' / 'Leave raid' instead of extracting right away.");

        _debugUnityErrors = Config.Bind("Debug", "Forward Unity Errors", false,
            "Copy Unity errors/exceptions into the BepInEx log while a trader screen is open (noisy, for bug reports).");

        if (IsHeadless())
        {
            Log("Headless instance, addon disabled.");
            return;
        }

        NotificationGate.Install();
        new ExfilEnterPatch().Enable();
        new ExfilExitPatch().Enable();
        new LocalPurchasePatch().Enable();
        new LocalSellPatch().Enable();
        new CarriedPaymentPatch().Enable();
        new DrawPackItemsPatch().Enable();
        new QuestStartItemsPatch().Enable();
        new BarterDealPatch().Enable();
        Log("Loaded.");
    }

    private void Update()
    {
        ZoneService.Tick();
    }

    private void LateUpdate()
    {
        ZoneService.LateTick();
    }

    public static bool IsInRaid()
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        return gameWorld?.ExfiltrationController != null && !string.IsNullOrWhiteSpace(gameWorld.LocationId);
    }

    public static bool IsHeadless()
    {
        return Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    public static void Log(string message) => _log?.LogInfo($"[Openworld] {message}");
    public static void LogError(string message) => _log?.LogError($"[Openworld] {message}");
}
