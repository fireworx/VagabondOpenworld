using System;
using System.Reflection;
using EFT.UI.Matchmaker;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.UI;

namespace OpenworldAddon;

/// <summary>
/// Map selection screen: the "SELECT TIME PHASE" panel has two toggles (the current phase and the next one).
/// The raid always uses the current phase, so the toggle that is not selected is hidden.
/// </summary>
internal static class TimePhase
{
    private static FieldInfo _am;
    private static FieldInfo _pm;
    private static bool _logged;

    internal static void Hide(object panel)
    {
        if (!Plugin.SkipRaidSettings || panel == null)
        {
            return;
        }

        try
        {
            _am ??= AccessTools.Field(panel.GetType(), "_amTimeToggle");
            _pm ??= AccessTools.Field(panel.GetType(), "_pmTimeToggle");
            var am = _am?.GetValue(panel) as Toggle;
            var pm = _pm?.GetValue(panel) as Toggle;
            if (am == null || pm == null)
            {
                return;
            }

            // keep the selected one; if none is on, keep the first
            var hide = am.isOn ? pm : pm.isOn ? am : pm;
            if (hide.gameObject.activeSelf)
            {
                hide.gameObject.SetActive(false);
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log("Time phase: hid the toggle " + hide.name + " (am on: " + am.isOn + ", pm on: " + pm.isOn + ").");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.LogError("Time phase patch failed: " + e.Message);
        }
    }
}

internal sealed class TimePhasePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(LocationConditionsPanel), "UpdateConditions");
    }

    [PatchPostfix]
    private static void Postfix(LocationConditionsPanel __instance)
    {
        TimePhase.Hide(__instance);
    }
}

internal sealed class TimePhaseSetPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(LocationConditionsPanel), "Set");
    }

    [PatchPostfix]
    private static void Postfix(LocationConditionsPanel __instance)
    {
        TimePhase.Hide(__instance);
    }
}
