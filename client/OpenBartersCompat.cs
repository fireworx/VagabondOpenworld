using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace OpenworldAddon;

/// <summary>
/// Open Barters (https://github.com/egbog/OpenBarters), when it is loaded, replaces the deal button of a
/// barter with its own purchase, which asks the server to take the items from the stash. In a raid that
/// has to happen here instead: the items are taken from the character's inventory. Everything of that
/// mod is reached by reflection, so it stays an optional companion.
/// </summary>
public static class OpenBartersCompat
{
    public const string PluginGuid = "com.egbog.openbarters";

    private static Type _controller;
    private static bool _looked;

    private static Type Controller
    {
        get
        {
            if (!_looked)
            {
                _looked = true;
                _controller = AccessTools.TypeByName("OpenBarters.Controllers.OpenBarterController");
            }

            return _controller;
        }
    }

    public static bool Present => Controller != null;

    private static object Static(string name)
    {
        var type = Controller;
        var prop = type?.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (prop != null)
        {
            return prop.GetValue(null);
        }

        return type?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    /// <summary>The barter grid of Open Barters is switched on and shown.</summary>
    public static bool BarterModeOn()
    {
        try
        {
            var method = Controller?.GetMethod("ToggleActive", BindingFlags.Public | BindingFlags.Static);
            return method != null && (bool)method.Invoke(null, null);
        }
        catch (Exception e)
        {
            Plugin.LogError("OpenBarters: could not read the barter switch: " + e.Message);
            return false;
        }
    }

    /// <summary>Pays for the selected offer with the barter basket (and the exact requirements), from the carried inventory.</summary>
    public static async Task BarterPurchase()
    {
        try
        {
            var current = Static("Current");
            var assortment = Static("TraderAssortment") as Assortment;
            if (current == null || assortment == null)
            {
                Plugin.LogError("OpenBarters: no barter in progress.");
                return;
            }

            if (assortment.TransactionInProgress)
            {
                return;
            }

            var type = current.GetType();
            var basketValue = GetAmount(type.GetMethod("GetBasketValue").Invoke(current, null));
            var requiredValue = (int)type.GetMethod("GetRequiredValue").Invoke(current, null);
            var canTrade = (bool)Controller.GetMethod("CanTrade", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { basketValue, requiredValue });
            if (!canTrade)
            {
                NotificationGate.Warn("The items you put in are not worth enough.");
                return;
            }

            var basket = type.GetField("BarterItems").GetValue(current) as IDictionary;
            var extra = new System.Collections.Generic.List<TradingItemReference>();
            if (basket != null)
            {
                foreach (DictionaryEntry pair in basket)
                {
                    var clone = (Item)pair.Key;
                    var original = (Item)pair.Value;
                    extra.Add(new TradingItemReference { Count = clone.StackObjectsCount, Item = original });
                }
            }

            var result = await LocalTrade.Purchase(assortment, extra.ToArray());
            if (result != null && result.Succeed)
            {
                type.GetMethod("ClearBarterItems").Invoke(current, null);
            }
        }
        catch (Exception e)
        {
            Plugin.LogError("OpenBarters purchase failed: " + e);
        }
    }

    private static int GetAmount(object price)
    {
        // Trader.ItemPrice is a struct with an Amount
        var type = price.GetType();
        var field = type.GetField("Amount");
        if (field != null)
        {
            return Convert.ToInt32(field.GetValue(price));
        }

        return Convert.ToInt32(type.GetProperty("Amount").GetValue(price));
    }
}

/// <summary>Runs before Open Barters' own deal button handling, but only for a barter in a raid trade.</summary>
public class BarterDealPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(TraderDealScreen), "OnDealButtonClickAsync");
    }

    [PatchPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(ref Task __result, TraderDealScreen __instance)
    {
        if (!LocalTrade.Active || !OpenBartersCompat.Present || __instance.TradeMode != ETradeMode.Purchase ||
            !OpenBartersCompat.BarterModeOn())
        {
            return true;
        }

        __result = OpenBartersCompat.BarterPurchase();
        return false;
    }
}
