using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace OpenworldAddon;

/// <summary>
/// Delivery service at the traders: pick carried items, pay (grid cells of the picked items x rubles per slot) and the trader
/// sends them to the hideout as a message with the items attached, to be taken there.
/// </summary>
internal static class Delivery
{
    private static readonly List<Item> Outbox = new List<Item>();
    private static string _trader;
    private static bool _busy;

    private static int Cells(Item item)
    {
        return Math.Max(1, item.Template.Width * item.Template.Height);
    }

    private static string Name(Item item)
    {
        return item.ShortName.Localized() + (item.StackObjectsCount > 1 ? " x" + item.StackObjectsCount : "");
    }

    public static async void Open(string traderId)
    {
        _trader = traderId;
        Outbox.Clear();
        var groups = await Load();
        if (groups == null)
        {
            return;
        }

        MessagesPanel.Open("DELIVERY SERVICE", "CARRIED IN", "ADD", groups, Load, false);
    }

    private static Task<List<MessagesPanel.Group>> Load()
    {
        var player = GamePlayerOwner.MyPlayer;
        var groups = new List<MessagesPanel.Group>();
        if (player == null)
        {
            return Task.FromResult(groups);
        }

        var carried = LocalTrade.SendableItems(player);
        Outbox.RemoveAll(o => !carried.Any(c => c.Item == o));
        var cells = Outbox.Sum(Cells);
        var price = (long)cells * Plugin.RublesPerSlot;
        var money = FastTravel.CarriedRoubles(player);
        var enough = money >= price;

        var box = new MessagesPanel.Group { Name = "Delivery (" + Outbox.Count + ")", Key = "0" };
        box.Rows.Add(new MessagesPanel.Row
        {
            Label = cells + " slots x " + Plugin.RublesPerSlot.ToString("N0", CultureInfo.InvariantCulture) + " = "
                + price.ToString("N0", CultureInfo.InvariantCulture) + " roubles   (you carry " + money.ToString("N0", CultureInfo.InvariantCulture) + ")",
            Good = enough
        });
        foreach (var item in Outbox.ToList())
        {
            var it = item;
            box.Rows.Add(new MessagesPanel.Row
            {
                Item = it, Label = Name(it) + "   " + Cells(it) + (Cells(it) == 1 ? " slot" : " slots"), Verb = "REMOVE", NoBulk = true,
                Act = () => { Outbox.Remove(it); return Task.FromResult(true); }
            });
        }

        box.ActionLabel = "SEND";
        box.ActionEnabled = Outbox.Count > 0 && enough;
        box.Action = Send;
        groups.Add(box);

        foreach (var (item, container) in carried.Where(c => !Outbox.Contains(c.Item)))
        {
            var group = groups.FirstOrDefault(g => g.Name == container);
            if (group == null)
            {
                group = new MessagesPanel.Group { Name = container };
                groups.Add(group);
            }

            var it = item;
            group.Rows.Add(new MessagesPanel.Row
            {
                Item = it, Label = Name(it) + ItemFill.Text(it) + "   " + Cells(it) + (Cells(it) == 1 ? " slot" : " slots"), NoBulk = true,
                Act = () => { Outbox.Add(it); return Task.FromResult(true); }
            });
        }

        return Task.FromResult(groups);
    }

    private static async Task<bool> Send()
    {
        if (_busy || Outbox.Count == 0)
        {
            return false;
        }

        _busy = true;
        var guarded = false;
        var taken = new List<Item>();
        try
        {
            var player = GamePlayerOwner.MyPlayer;
            var price = (long)Outbox.Sum(Cells) * Plugin.RublesPerSlot;
            if (player == null || FastTravel.CarriedRoubles(player) < price)
            {
                NotificationGate.Warn("Not enough roubles carried.");
                return false;
            }

            StatsGuard.Begin(player.Profile);
            guarded = true;

            var flat = new JArray();
            foreach (var item in Outbox)
            {
                foreach (var part in JArray.Parse(Singleton<ItemFactory>.Instance.TreeToFlatItems(new[] { item }).ToJson()))
                {
                    flat.Add(part);
                }
            }

            foreach (var item in Outbox.ToList())
            {
                if (!await LocalTrade.Take(player, item, item.StackObjectsCount))
                {
                    foreach (var back in taken) await LocalTrade.Give(player, back);
                    NotificationGate.Warn("Could not take " + Name(item) + " out of your inventory.");
                    return false;
                }

                taken.Add(item);
            }

            var body = new JObject { ["Items"] = flat, ["TraderId"] = _trader };
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/mail/send", body.ToString(Formatting.None)));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Delivery failed: " + reply["Error"]);
                foreach (var back in taken) await LocalTrade.Give(player, back);
                NotificationGate.Warn("The delivery could not be sent.");
                return false;
            }

            if (!await FastTravel.Pay(player, price))
            {
                Plugin.LogError("Delivery sent but the roubles could not be taken (" + price + ").");
            }

            Plugin.Log("Delivery sent: " + taken.Count + " item(s), " + price + " roubles.");
            Outbox.Clear();
            NotificationGate.Message("Delivery sent. The trader's message with your items waits at the hideout.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Delivery failed: " + e);
            NotificationGate.Warn("Delivery failed, see log.");
            return false;
        }
        finally
        {
            if (guarded)
            {
                StatsGuard.Revert();
                StatsGuard.End();
            }

            _busy = false;
        }
    }
}
