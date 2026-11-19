using System.Reflection;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace OpenworldAddon;

// Runs before Vagabond's own prefix on the same method; returning false skips the vanilla extraction.
internal class ExfilEnterPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ExfiltrationPoint).GetMethod(
            "IPhysicsTrigger.OnTriggerEnter",
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    [PatchPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ExfiltrationPoint __instance, Collider col)
    {
        return !ZoneService.TryEnter(__instance, col);
    }
}

internal class ExfilExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ExfiltrationPoint).GetMethod(
            "IPhysicsTrigger.OnTriggerExit",
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    [PatchPostfix]
    private static void Postfix(ExfiltrationPoint __instance, Collider col)
    {
        ZoneService.Exit(__instance, col);
    }
}
