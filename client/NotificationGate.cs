using System;
using System.Linq;
using System.Reflection;
using EFT.Communications;
using HarmonyLib;
using UnityEngine;

namespace OpenworldAddon;

/// <summary>
/// The experience and skill pop-ups in the bottom right that the trade operations cause are hidden:
/// while a trader screen of this addon is open and a short window after a trade operation is running,
/// game notifications are dropped. Outside the trader screen nothing is touched.
/// The addon's own messages go through Warn/Message and are always shown.
/// </summary>
public static class NotificationGate
{
    private static float _until;
    private static bool _own;

    // only while one of our trader screens is open: everywhere else the game's pop-ups stay untouched
    public static bool Suppressing => LocalTrade.Active && !_own && Time.unscaledTime < _until;

    /// <summary>Drop game notifications for the next few seconds.</summary>
    public static void Open(float seconds)
    {
        _until = Math.Max(_until, Time.unscaledTime + seconds);
    }

    public static void Warn(string text)
    {
        _own = true;
        try
        {
            NotificationManager.DisplayWarningNotification(text);
        }
        finally
        {
            _own = false;
        }
    }

    public static void Message(string text)
    {
        _own = true;
        try
        {
            NotificationManager.DisplayMessageNotification(text);
        }
        finally
        {
            _own = false;
        }
    }

    public static void Install()
    {
        try
        {
            var harmony = new Harmony("dev.local.vagabond-openworld.notifications");
            var prefix = new HarmonyMethod(typeof(NotificationGate).GetMethod(nameof(Prefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var patched = 0;
            foreach (var method in typeof(NotificationManager)
                         .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(m => m.Name.StartsWith("Display", StringComparison.Ordinal) && !m.IsGenericMethod))
            {
                try
                {
                    harmony.Patch(method, prefix: prefix);
                    patched++;
                }
                catch (Exception ex)
                {
                    Plugin.LogError("Could not patch " + method.Name + ": " + ex.Message);
                }
            }

            Plugin.Log("NotificationGate: patched " + patched + " notification method(s).");
        }
        catch (Exception ex)
        {
            Plugin.LogError("NotificationGate.Install: " + ex);
        }
    }

    private static bool Prefix()
    {
        return !Suppressing;
    }
}
