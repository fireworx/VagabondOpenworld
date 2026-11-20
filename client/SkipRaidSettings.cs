// Adapted from RaidSettingsSkipper (https://github.com/awnova/FikaRaidSettingsSkipper), MIT License, Copyright (c) 2026 awnova.
// See THIRD-PARTY-NOTICES.md.
using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Common.Http;
using SPT.Common.Utils;
using SPT.Custom.Models;
using SPT.Custom.Patches;
using SPT.Reflection.Patching;
using SPT.SinglePlayer.Patches.ScavMode;

namespace OpenworldAddon;

/// <summary>
/// Skips the pre-raid settings screen (between the map selection and the raid) for PMC and Scav raids. A skipped screen
/// never runs its own Show and Close, so what SPT does there is replayed here.
/// </summary>
internal static class RaidSettingsScreen
{
    internal static bool ShouldSkip => Plugin.SkipRaidSettings;

    // what SPT's SetPreRaidSettingsScreenDefaultsPatch and CopyPmcQuestsAndWishlistToPlayerScavPatch do when the screen shows
    internal static void OnScreenShown(RaidSettings offlineRaidSettings)
    {
        offlineRaidSettings.RaidMode = ERaidMode.Online;

        try
        {
            var defaults = Json.Deserialize<DefaultRaidSettings>(RequestHandler.GetJson("/singleplayer/settings/raid/menu"));
            if (defaults != null)
            {
                offlineRaidSettings.BotSettings.BotAmount = defaults.AiAmount;
                offlineRaidSettings.WavesSettings.BotAmount = defaults.AiAmount;
                offlineRaidSettings.WavesSettings.BotDifficulty = defaults.AiDifficulty;
                offlineRaidSettings.WavesSettings.IsBosses = defaults.BossEnabled;
                offlineRaidSettings.BotSettings.IsScavWars = false;
                offlineRaidSettings.WavesSettings.IsTaggedAndCursed = defaults.TaggedAndCursed;
                offlineRaidSettings.TimeAndWeatherSettings.IsRandomWeather = defaults.RandomWeather;
                offlineRaidSettings.TimeAndWeatherSettings.IsRandomTime = defaults.RandomTime;
            }
        }
        catch (Exception e)
        {
            Plugin.LogError("Could not load the server's raid settings defaults: " + e.Message);
        }

        RaidClock.Apply(offlineRaidSettings);

        try
        {
            CopyPmcQuestsAndWishlistToPlayerScavPatch.PatchPostfix();
        }
        catch (Exception e)
        {
            Plugin.LogError("Could not copy PMC quests and wishlist to the scav profile: " + e.Message);
        }
    }

    // what the screen's Close does
    internal static void OnScreenClosed(RaidSettings raidSettings, RaidSettings offlineRaidSettings)
    {
        raidSettings.RaidMode = offlineRaidSettings.RaidMode;
        RaidClock.Apply(raidSettings);
    }
}

internal sealed class SkipRaidSettingsScreenPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        if (AccessTools.Method(typeof(MainMenuShowOperation), nameof(MainMenuShowOperation.CG_method_80)) == null
            || AccessTools.Field(typeof(MainMenuShowOperation), nameof(MainMenuShowOperation.raidSettings_0)) == null
            || AccessTools.Field(typeof(MainMenuShowOperation), nameof(MainMenuShowOperation.raidSettings_1)) == null)
        {
            return null;
        }

        return AccessTools.Method(typeof(MainMenuShowOperation), nameof(MainMenuShowOperation.method_50));
    }

    [PatchPrefix]
    private static bool Prefix(MainMenuShowOperation __instance)
    {
        if (!RaidSettingsScreen.ShouldSkip)
        {
            return true;
        }

        RaidSettingsScreen.OnScreenShown(__instance.raidSettings_1);
        __instance.CG_method_80();
        RaidSettingsScreen.OnScreenClosed(__instance.raidSettings_0, __instance.raidSettings_1);
        return false;
    }
}

internal sealed class SkipScavRaidSettingsScreenPatch : ModulePatch
{
    private static Func<MainMenuShowOperation> _getMenuController;
    private static Action _showNextScreen;

    protected override MethodBase GetTargetMethod()
    {
        var patch = typeof(LoadOfflineRaidScreenPatch);
        var getMenuController = AccessTools.Method(patch, "GetMenuController");
        var showNextScreen = AccessTools.Method(patch, "LoadOfflineRaidNextScreen");
        if (getMenuController == null || showNextScreen == null)
        {
            return null;
        }

        _getMenuController = AccessTools.MethodDelegate<Func<MainMenuShowOperation>>(getMenuController);
        _showNextScreen = AccessTools.MethodDelegate<Action>(showNextScreen);
        return AccessTools.Method(patch, "LoadOfflineRaidScreenForScav");
    }

    [PatchPrefix]
    private static bool Prefix()
    {
        if (!RaidSettingsScreen.ShouldSkip)
        {
            return true;
        }

        var menuController = _getMenuController();
        if (menuController == null)
        {
            return true;
        }

        RaidSettingsScreen.OnScreenShown(menuController.raidSettings_1);
        _showNextScreen();
        RaidSettingsScreen.OnScreenClosed(menuController.raidSettings_0, menuController.raidSettings_1);
        return false;
    }
}
