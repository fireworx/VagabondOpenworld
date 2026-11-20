using System;
using System.IO;
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
    private static ConfigEntry<bool> _hideoutAtTraders;
    private static ConfigEntry<float> _promptOffset;
    private static ConfigEntry<float> _extractionTime;
    private static ConfigEntry<bool> _debugUnityErrors;
    private static ConfigEntry<bool> _fileLog;
    private static ConfigEntry<bool> _skipRaidSettings;
    private static ConfigEntry<bool> _keepRaidTime;
    private static ConfigEntry<bool> _fastTravel;
    private static ConfigEntry<bool> _delivery;
    private static ConfigEntry<int> _rublesPerSlot;
    private static ConfigEntry<float> _hoursPerTransit;
    private static ConfigEntry<string> _transitNetwork;
    private static ConfigEntry<KeyboardShortcut> _scanKey;
    private static ConfigEntry<bool> _siphon;
    private static ConfigEntry<KeyCode> _siphonKey;
    private static ConfigEntry<float> _siphonSeconds;
    private static ConfigEntry<bool> _siphonPlant;
    private static ConfigEntry<int> _siphonEmpty;
    private static ConfigEntry<int> _siphonMin;
    private static ConfigEntry<int> _siphonMax;
    private static ConfigEntry<float> _waterSeconds;
    private static ConfigEntry<float> _bitcoinSeconds;
    // sorts after all other sections in the BepInEx menu (it lists them by name)
    private const string DebugSection = "Z Debug";
    private static string _logPath;
    private static readonly object LogGate = new object();

    public static bool TraderZoneEnabled => _traderZone?.Value ?? false;
    public static bool HideoutZoneEnabled => _hideoutZone?.Value ?? false;
    /// <summary>Tells the server the production times set in the BepInEx menu (0 = the server's own data).</summary>
    public static void PushRules()
    {
        try
        {
            var body = Newtonsoft.Json.JsonConvert.SerializeObject(new { WaterSeconds = _waterSeconds?.Value ?? 0f, BitcoinSeconds = _bitcoinSeconds?.Value ?? 0f });
            SPT.Common.Http.RequestHandler.PostJson("/vagabond-openworld/rules", body);
        }
        catch (Exception e)
        {
            LogError("Could not send the production times: " + e.Message);
        }
    }

    public static bool SiphonEnabled => _siphon?.Value ?? true;
    public static KeyCode SiphonKey => _siphonKey?.Value ?? KeyCode.F;
    public static float SiphonSeconds => _siphonSeconds?.Value ?? 1f;
    public static bool SiphonUsesPlantTime => _siphonPlant?.Value ?? true;
    public static int SiphonEmptyChance => _siphonEmpty?.Value ?? 25;
    public static int SiphonMin => _siphonMin?.Value ?? 1;
    public static int SiphonMax => _siphonMax?.Value ?? 15;
    public static bool DeliveryEnabled => _delivery?.Value ?? true;
    public static bool FastTravelEnabled => _fastTravel?.Value ?? true;
    public static int RublesPerSlot => _rublesPerSlot?.Value ?? 5000;
    public static float HoursPerTransit => _hoursPerTransit?.Value ?? 2.5f;
    public static string TransitNetwork => _transitNetwork?.Value;
    public static bool KeepRaidTime => _keepRaidTime?.Value ?? true;
    public static bool SkipRaidSettings => _skipRaidSettings?.Value ?? true;
    public static float ExtractionTime => _extractionTime?.Value ?? 20f;
    public static float PromptOffset => _promptOffset?.Value ?? 0f;
    public static bool HideoutAtTradersEnabled => _hideoutAtTraders?.Value ?? false;
    public static bool FileLogEnabled => _logPath != null;
    public static bool DebugUnityErrors => _debugUnityErrors?.Value ?? false;

    private void Awake()
    {
        Instance = this;
        _log = Logger;

        _traderZone = Config.Bind("General", "Trader Zone Interaction", true,
            "Trader exfils show an 'Open <trader>' interaction instead of extracting.");
        _hideoutZone = Config.Bind("General", "Hideout Zone Menu", true,
            "The hideout exfil offers 'Send items to hideout' / 'Leave raid' instead of extracting right away.");

        _extractionTime = Config.Bind("General", "Extraction Time", 20f,
            new ConfigDescription("Seconds of the countdown when you leave through the hideout exfil (Leave to hideout).",
                new AcceptableValueRange<float>(1f, 120f)));
        _promptOffset = Config.Bind("General", "Prompt Offset", 150f,
            "How far the zone menu (Open trader, Build hideout, ...) is moved down on the screen, in UI units. 0 leaves it where the game puts it.");
        _hideoutAtTraders = Config.Bind("General", "Hideout Functions At Traders", false,
            "Also offer Build hideout, Craft, Collect crafts, Open stash and Send items to hideout at the trader exfils (otherwise only at the hideout exfil).");

        _waterSeconds = Config.Bind("Hideout", "Pure Water Seconds", 0f,
            new ConfigDescription("How many seconds the water collector needs for one pure water. 0 takes the value from the server data.",
                new AcceptableValueRange<float>(0f, 100000f)));
        _bitcoinSeconds = Config.Bind("Hideout", "Bitcoin Seconds", 0f,
            new ConfigDescription("How many seconds the bitcoin farm needs for one bitcoin with one graphics card (more cards are faster). 0 takes the value from the server data.",
                new AcceptableValueRange<float>(0f, 1000000f)));

        _skipRaidSettings = Config.Bind("General", "Skip Raid Settings Screen", true,
            "Skip the pre-raid settings screen: from the map selection straight on, with the server's defaults. Do not run RaidSettingsSkipper at the same time.");
        _fastTravel = Config.Bind("Fast Travel", "Fast Travel At Traders", true,
            "The traders offer a fast travel service: the maps with the number of transits, the price and the time.");
        _delivery = Config.Bind("Fast Travel", "Delivery Service At Traders", true,
            "The traders deliver carried items to the hideout as a message; the price is Rubles Per Slot for every grid cell sent.");
        _rublesPerSlot = Config.Bind("Fast Travel", "Rubles Per Slot", 5000,
            new ConfigDescription("Price of one transit per grid cell of the tactical vest and the backpack you wear. The total is this x the cells x the number of transits.",
                new AcceptableValueRange<int>(0, 10000000)));
        _hoursPerTransit = Config.Bind("Fast Travel", "Hours Per Transit", 2.5f,
            new ConfigDescription("Hours on the raid clock that one transit takes.", new AcceptableValueRange<float>(0f, 24f)));
        _transitNetwork = Config.Bind("Fast Travel", "Transit Network",
            "lighthouse:shoreline,woods,reserve;shoreline:reserve,customs;reserve:woods,customs;customs:woods,factory,interchange;woods:factory,streets;interchange:streets;streets:groundzero",
            "Which maps are connected by a transit: 'map:neighbour,neighbour;map:...'. Every link works both ways. Names: lighthouse, shoreline, reserve, woods, customs, factory, interchange, streets, groundzero.");
        _keepRaidTime = Config.Bind("General", "Keep Raid Time", true,
            "The next raid starts at the time of day the last raid had when you left it (instead of a random time). Works together with Skip Raid Settings Screen.");
        _debugUnityErrors = Config.Bind(DebugSection, "Forward Unity Errors", false,
            "Copy Unity errors/exceptions into the BepInEx log while a trader screen is open (noisy, for bug reports).");

        _siphon = Config.Bind("Fuel", "Siphon Fuel From Cars", true,
            "Hold the siphon key (F) while looking at a standing car (with a silicone tube and a fuel tank with room in your inventory) to fill the tanks with its fuel.");
        _siphonKey = Config.Bind("Fuel", "Siphon Key", KeyCode.F, "Key to hold while looking at a car close by (F is the game's interact key).");
        _siphonSeconds = Config.Bind("Fuel", "Seconds Per Unit", 1f,
            new ConfigDescription("How long the key has to be held for every unit of fuel taken (1 unit = 1 second by default).", new AcceptableValueRange<float>(0.05f, 10f)));
        _siphonPlant = Config.Bind("Fuel", "Use Plant Time Modifier", true,
            "When the Plant Time Modifier mod is installed, the time is multiplied by its 'Repair objective Time Multiplier' (0 there = instant).");
        _siphonEmpty = Config.Bind("Fuel", "Empty Car Chance", 25,
            new ConfigDescription("Percent of the cars that have no fuel at all.", new AcceptableValueRange<int>(0, 100)));
        _siphonMin = Config.Bind("Fuel", "Fuel Per Car Min", 1,
            new ConfigDescription("Least fuel units a car holds (random per car and raid).", new AcceptableValueRange<int>(0, 100)));
        _siphonMax = Config.Bind("Fuel", "Fuel Per Car Max", 15,
            new ConfigDescription("Most fuel units a car holds.", new AcceptableValueRange<int>(0, 100)));
        _scanKey = Config.Bind(DebugSection, "Vehicle Scan Key", new KeyboardShortcut(KeyCode.F8),
            "In a raid: writes the vehicle-like objects within 40 m and the carried fuel tanks / silicone tube into the log (finding aid for the fuel idea).");

        _fileLog = Config.Bind(DebugSection, "Write Log File", false,
            "Write VagabondOpenworld.client.<date>_<time>.log (a new file for every game start) next to the plugin with the add-on's own lines and extra detail (for bug reports). Restart the game after changing it.");
        MoveOldDebugValues();

        if (_fileLog.Value)
        {
            try
            {
                _logPath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? ".", "VagabondOpenworld.client." + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
                File.WriteAllText(_logPath, "VagabondOpenworld client log, started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
            }
            catch (Exception e)
            {
                _logPath = null;
                _log.LogError("[Openworld] Could not start the log file: " + e.Message);
            }
        }

        if (IsHeadless())
        {
            Log("Headless instance, addon disabled.");
            return;
        }

        if (_logPath != null)
        {
            TypeDump.Write(Path.GetDirectoryName(_logPath) ?? ".");
        }

        NotificationGate.Install();
        EnablePatch(new SkipRaidSettingsScreenPatch(), "SkipRaidSettingsScreenPatch");
        EnablePatch(new SkipScavRaidSettingsScreenPatch(), "SkipScavRaidSettingsScreenPatch");
        EnablePatch(new TimePhasePatch(), "TimePhasePatch");
        EnablePatch(new RaidClockInitPatch(), "RaidClockInitPatch");
        EnablePatch(new TimePhaseSetPatch(), "TimePhaseSetPatch");
        EnablePatch(new ExfilEnterPatch(), "ExfilEnterPatch");
        EnablePatch(new ExfilExitPatch(), "ExfilExitPatch");
        EnablePatch(new LocalPurchasePatch(), "LocalPurchasePatch");
        EnablePatch(new LocalSellPatch(), "LocalSellPatch");
        EnablePatch(new CarriedPaymentPatch(), "CarriedPaymentPatch");
        EnablePatch(new DrawPackItemsPatch(), "DrawPackItemsPatch");
        EnablePatch(new QuestStartItemsPatch(), "QuestStartItemsPatch");
        EnablePatch(new QuestHandoverLogPatch(), "QuestHandoverLogPatch");
        EnablePatch(new QuestRewardItemsPatch(), "QuestRewardItemsPatch");
        EnablePatch(new QuestFinishRewardsPatch(), "QuestFinishRewardsPatch");
        QuestDiag.Install();
        EnablePatch(new BarterDealPatch(), "BarterDealPatch");
        EnablePatch(new WishlistCompat.RequestPatch(), "WishlistCompat.RequestPatch");
        Log("Loaded.");
    }

    /// <summary>The section was called "Debug" before: the values saved there are taken over once.</summary>
    private void MoveOldDebugValues()
    {
        try
        {
            // BepInEx keeps values it found in the file but no setting for in this (not public) dictionary
            var orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(Config) as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
            if (orphans == null)
            {
                return;
            }

            foreach (var entry in new ConfigEntryBase[] { _debugUnityErrors, _scanKey, _fileLog })
            {
                var old = new ConfigDefinition("Debug", entry.Definition.Key);
                if (orphans.TryGetValue(old, out var text))
                {
                    entry.SetSerializedValue(text);
                    orphans.Remove(old);
                }
            }

            Config.Save();
        }
        catch (Exception e)
        {
            LogError("Could not take over the old debug settings: " + e.Message);
        }
    }

    private static void EnablePatch(SPT.Reflection.Patching.ModulePatch patch, string name)
    {
        try
        {
            patch.Enable();
        }
        catch (Exception e)
        {
            LogError(name + " could not be enabled (another game version?): " + e.Message);
        }
    }

    private void Update()
    {
        ZoneService.Tick();
        RaidClock.Tick();
        WishlistCompat.Tick();
        VehicleFuel.Tick();
        if (_scanKey != null && _scanKey.Value.IsDown() && IsInRaid())
        {
            VehicleScan.Scan(40f);
        }
    }

    private void OnGUI()
    {
        VehicleFuel.Draw();
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

    public static void Log(string message)
    {
        _log?.LogInfo($"[Openworld] {message}");
        Trace(message);
    }

    public static void LogError(string message)
    {
        _log?.LogError($"[Openworld] {message}");
        Trace("ERROR " + message);
    }

    /// <summary>A detail line, only for the add-on's own log file (when switched on in the configuration).</summary>
    public static void Trace(string message)
    {
        if (_logPath == null)
        {
            return;
        }

        try
        {
            lock (LogGate)
            {
                File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // a log must never break the game
        }
    }
}
