using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace OpenworldAddon;

/// <summary>
/// Keeps the time of day of the raid: while you are in a raid its hour is written down, and the next raid starts at that hour
/// (random time is switched off for it). The hour is kept in BepInEx\config\VagabondOpenworld.raidtime.txt.
/// </summary>
internal static class RaidClock
{
    private static int? _saved;
    public static int SavedMinute;
    private static bool _loaded;
    private static float _next;
    private static bool _hold;

    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "VagabondOpenworld.raidtime.txt");

    public static int? Saved
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var parts = File.ReadAllText(FilePath).Trim().Split(':');
                        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
                        {
                            _saved = Math.Max(0, Math.Min(23, hour));
                            if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
                            {
                                SavedMinute = Math.Max(0, Math.Min(59, minute));
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.LogError("Could not read the saved raid time: " + e.Message);
                }
            }

            return _saved;
        }
    }

    /// <summary>The hour on the clock of the raid that is running, or null.</summary>
    private static DateTime? CurrentTime()
    {
        var game = Singleton<AbstractGame>.Instance;
        if (game == null)
        {
            return null;
        }

        // the field is looked up for the running game every time (the hideout game is another generic type with its own field)
        FieldInfo field = null;
        for (var type = game.GetType(); type != null && field == null; type = type.BaseType)
        {
            field = type.GetField("_gameDateTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
        }

        var clock = field?.GetValue(game) as GameDateTime;
        return clock?.Calculate();
    }

    /// <summary>Called every frame; writes the hour down every few seconds while a raid is running.</summary>
    public static void Tick()
    {
        if (!Plugin.KeepRaidTime || Time.realtimeSinceStartup < _next || !Plugin.IsInRaid())
        {
            return;
        }

        if (_hold)
        {
            return;
        }

        _next = Time.realtimeSinceStartup + 5f;
        try
        {
            var now = CurrentTime();
            if (now == null)
            {
                return;
            }

            var hour = now.Value.Hour;
            var minute = now.Value.Minute;
            if (hour == Saved && minute == SavedMinute)
            {
                return;
            }

            _saved = hour;
            SavedMinute = minute;
            File.WriteAllText(FilePath, hour.ToString(CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture));
            Plugin.Trace("Raid time is " + hour + ":" + minute.ToString("00") + " now (saved).");
        }
        catch (Exception e)
        {
            Plugin.LogError("Could not save the raid time: " + e.Message);
        }
    }

    /// <summary>Moves the saved time of day forward (fast travel): current world time + hours; it is kept until the next raid starts.</summary>
    public static void Advance(float hours)
    {
        try
        {
            var now = CurrentTime();
            if (now == null)
            {
                return;
            }

            var target = now.Value.AddHours(hours);
            _loaded = true;
            _saved = target.Hour;
            SavedMinute = target.Minute;
            _hold = true;
            File.WriteAllText(FilePath, target.Hour.ToString(CultureInfo.InvariantCulture) + ":" + target.Minute.ToString("00", CultureInfo.InvariantCulture));
            Plugin.Log("Raid time moved forward by " + hours.ToString("0.#", CultureInfo.InvariantCulture) + " h to " + target.Hour + ":" + target.Minute.ToString("00") + ".");
        }
        catch (Exception e)
        {
            Plugin.LogError("Could not move the raid time: " + e.Message);
        }
    }

    /// <summary>Starts the next raid at the saved hour.</summary>
    public static void Apply(RaidSettings settings)
    {
        _hold = false;
        if (!Plugin.KeepRaidTime || Saved == null || settings == null)
        {
            return;
        }

        var weather = settings.TimeAndWeatherSettings;
        weather.IsRandomTime = false;
        weather.HourOfDay = Saved.Value;
        settings.TimeAndWeatherSettings = weather;
        Plugin.Log("The raid starts at " + Saved.Value + ":" + SavedMinute.ToString("00") + " (time of the last raid).");
    }
}

/// <summary>
/// The raid's own clock is set when the weather is initialised (the same place the "ImmersiveRaidTime" mod uses):
/// right after it the game clock is moved to the saved time of day, keeping its speed.
/// </summary>
internal sealed class RaidClockInitPatch : SPT.Reflection.Patching.ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return HarmonyLib.AccessTools.Method(typeof(BaseLocalGame<EftGamePlayerOwner>), "InitWeather");
    }

    [SPT.Reflection.Patching.PatchPostfix]
    private static void Postfix(object __instance)
    {
        try
        {
            if (!Plugin.KeepRaidTime || RaidClock.Saved == null)
            {
                return;
            }

            FieldInfo field = null;
            for (var type = __instance.GetType(); type != null && field == null; type = type.BaseType)
            {
                field = type.GetField("_gameDateTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            }

            var clock = field?.GetValue(__instance) as GameDateTime;
            if (clock == null)
            {
                Plugin.Log("Raid clock: the game clock was not found.");
                return;
            }

            var current = clock.Calculate();
            var target = current.Date + new TimeSpan(RaidClock.Saved.Value, RaidClock.SavedMinute, 0);
            clock.Reset(clock.RealDateTime(), target, clock.TimeFactor, true);
            Plugin.Log("Raid clock: " + current.ToString("HH:mm") + " -> " + clock.Calculate().ToString("HH:mm") + " (time of the last raid).");
        }
        catch (Exception e)
        {
            Plugin.LogError("Raid clock patch failed: " + (e.InnerException ?? e).Message);
        }
    }
}
