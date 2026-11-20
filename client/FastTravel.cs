using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace OpenworldAddon;

/// <summary>
/// Fast travel service at the traders: lists the maps with the number of transits it takes to get there from the current
/// map, the price and the time, and checks whether the carried roubles cover it. The price is
/// (grid cells of the tactical vest + the backpack) x rubles per cell x number of transits; every transit takes a set
/// number of hours on the raid clock. The map network, the price and the hours are set in the BepInEx menu.
/// </summary>
internal static class FastTravel
{
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["lighthouse"] = "Lighthouse", ["shoreline"] = "Shoreline", ["reserve"] = "Reserve", ["woods"] = "Woods",
        ["customs"] = "Customs", ["factory"] = "Factory", ["interchange"] = "Interchange", ["streets"] = "Streets of Tarkov",
        ["groundzero"] = "Ground Zero"
    };

    /// <summary>The game's location ids -> the names used in the map network.</summary>
    private static string Key(string locationId)
    {
        var id = (locationId ?? "").ToLowerInvariant();
        if (id.StartsWith("factory4")) return "factory";
        if (id.StartsWith("sandbox")) return "groundzero";
        switch (id)
        {
            case "bigmap": return "customs";
            case "rezervbase": return "reserve";
            case "tarkovstreets": return "streets";
            default: return id;
        }
    }

    /// <summary>Parses "a:b,c;b:a,d" into a network where every link works both ways.</summary>
    private static Dictionary<string, HashSet<string>> Network()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Link(string a, string b)
        {
            if (!map.TryGetValue(a, out var set)) map[a] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(b);
        }

        foreach (var part in (Plugin.TransitNetwork ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = part.Split(':');
            if (halves.Length != 2)
            {
                continue;
            }

            var from = halves[0].Trim();
            foreach (var to in halves[1].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                Link(from, to);
                Link(to, from);
            }
        }

        return map;
    }

    /// <summary>Fewest transits from the map to every other map.</summary>
    private static Dictionary<string, int> Distances(string start, Dictionary<string, HashSet<string>> network)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [start] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var map = queue.Dequeue();
            if (!network.TryGetValue(map, out var next))
            {
                continue;
            }

            foreach (var other in next)
            {
                if (!result.ContainsKey(other))
                {
                    result[other] = result[map] + 1;
                    queue.Enqueue(other);
                }
            }
        }

        return result;
    }

    /// <summary>Grid cells of the tactical vest and the backpack the player wears.</summary>
    private static (int Vest, int Backpack) Slots(Player player)
    {
        int Cells(EquipmentSlot slot)
        {
            var item = player.Profile.Inventory.Equipment.GetSlot(slot)?.ContainedItem as CompoundItem;
            return item?.Grids == null ? 0 : item.Grids.Sum(g => g.GridWidth * g.GridHeight);
        }

        return (Cells(EquipmentSlot.TacticalVest), Cells(EquipmentSlot.Backpack));
    }

    internal static int CarriedRoubles(Player player)
    {
        return LocalTrade.CarriedItems(player).Where(x => x.TemplateId == Roubles).Sum(x => x.StackObjectsCount);
    }

    public static async void Open()
    {
        var groups = await Load();
        if (groups == null)
        {
            return;
        }

        MessagesPanel.Open("FAST TRAVEL", "MAPS", "TRAVEL", groups, Load, false);
    }

    private static Task<List<MessagesPanel.Group>> Load()
    {
        var player = GamePlayerOwner.MyPlayer;
        var here = Key(Singleton<GameWorld>.Instance?.LocationId);
        var network = Network();
        var (vest, backpack) = player == null ? (0, 0) : Slots(player);
        var slots = vest + backpack;
        var perTransit = (long)slots * Plugin.RublesPerSlot;
        var money = player == null ? 0 : CarriedRoubles(player);
        Plugin.Trace("Fast travel price: " + slots + " slots x " + Plugin.RublesPerSlot + " roubles.");

        var info = new MessagesPanel.Group { Name = "Price", Key = "0" };
        info.Rows.Add(new MessagesPanel.Row
        {
            Label = "You carry " + slots + " slots (vest " + vest + ", backpack " + backpack + "). One transit costs "
                + perTransit.ToString("N0", CultureInfo.InvariantCulture) + " roubles (" + Plugin.RublesPerSlot.ToString("N0", CultureInfo.InvariantCulture)
                + " per slot) and takes " + Plugin.HoursPerTransit.ToString("0.#", CultureInfo.InvariantCulture) + " h."
        });
        info.Rows.Add(new MessagesPanel.Row { Label = "Roubles you carry: " + money.ToString("N0", CultureInfo.InvariantCulture) });

        var groups = new List<MessagesPanel.Group> { info };
        if (!network.ContainsKey(here))
        {
            groups.Add(new MessagesPanel.Group
            {
                Name = "Destinations", Key = "1",
                Rows = { new MessagesPanel.Row { Label = "This map (" + here + ") is not part of the transit network.", Good = false } }
            });
            return Task.FromResult(groups);
        }

        var destinations = new MessagesPanel.Group { Name = "Destinations", Key = "1" };
        foreach (var pair in Distances(here, network).Where(p => p.Value > 0).OrderBy(p => p.Value).ThenBy(p => p.Key))
        {
            var jumps = pair.Value;
            var price = perTransit * jumps;
            var hours = jumps * Plugin.HoursPerTransit;
            var name = Names.TryGetValue(pair.Key, out var shown) ? shown : pair.Key;
            var affordable = money >= price;
            var target = pair.Key;
            destinations.Rows.Add(new MessagesPanel.Row
            {
                Label = name + "   " + jumps + (jumps == 1 ? " transit" : " transits") + ", " + hours.ToString("0.#", CultureInfo.InvariantCulture) + " h, "
                    + price.ToString("N0", CultureInfo.InvariantCulture) + " roubles" + (affordable ? "" : "   (not enough roubles)"),
                Good = affordable,
                Act = affordable ? () => Travel(target, jumps, price) : (Func<Task<bool>>)null
            });
        }

        groups.Add(destinations);
        return Task.FromResult(groups);
    }

    private static readonly Dictionary<string, string> LocationIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["lighthouse"] = "Lighthouse", ["shoreline"] = "Shoreline", ["reserve"] = "RezervBase", ["woods"] = "Woods",
        ["customs"] = "bigmap", ["factory"] = "factory4_day", ["interchange"] = "Interchange", ["streets"] = "TarkovStreets",
        ["groundzero"] = "Sandbox"
    };

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static FieldInfo FindField(Type type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            var f = type.GetField(name, All | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }

        return null;
    }

    /// <summary>Takes the roubles from the carried stacks; returns false (and gives nothing back needed) when it fails.</summary>
    internal static async Task<bool> Pay(Player player, long price)
    {
        var left = price;
        foreach (var stack in LocalTrade.CarriedItems(player).Where(x => x.TemplateId == Roubles).ToList())
        {
            if (left <= 0) break;
            var take = (int)Math.Min(left, stack.StackObjectsCount);
            if (!await LocalTrade.Take(player, stack, take)) return false;
            left -= take;
        }

        return left <= 0;
    }

    /// <summary>Starts the transit straight to the target map. The roubles are only taken once the transit is on its way.</summary>
    private static async Task<bool> Travel(string target, int jumps, long price)
    {
        try
        {
            var player = GamePlayerOwner.MyPlayer;
            if (player == null || !LocationIds.TryGetValue(target, out var locationId))
            {
                NotificationGate.Warn("Fast travel is not possible right now.");
                return false;
            }

            if (CarriedRoubles(player) < price)
            {
                NotificationGate.Warn("Not enough roubles carried.");
                return false;
            }

            // an existing transit point of this map gives the controller; without one a bare point is made
            var points = UnityEngine.Object.FindObjectsOfType<EFT.Interactive.TransitPoint>();
            Plugin.Log("Fast travel: " + points.Length + " transit points in the scene.");
            var point = points.FirstOrDefault();
            object controller = point == null ? null : FindField(typeof(EFT.Interactive.TransitPoint), "_controller")?.GetValue(point);
            if (controller == null)
            {
                var world = Singleton<GameWorld>.Instance;
                for (var type = world?.GetType(); type != null && controller == null; type = type.BaseType)
                {
                    foreach (var f in type.GetFields(All | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(TransitController).IsAssignableFrom(f.FieldType)) { controller = f.GetValue(world); if (controller != null) break; }
                    }

                    foreach (var pr in type.GetProperties(All | BindingFlags.DeclaredOnly))
                    {
                        if (pr.GetIndexParameters().Length == 0 && typeof(TransitController).IsAssignableFrom(pr.PropertyType))
                        {
                            try { controller = pr.GetValue(world, null); } catch { }
                            if (controller != null) break;
                        }
                    }
                }
            }

            var method = controller?.GetType().GetMethod("Transit", All);
            if (controller == null || method == null)
            {
                Plugin.Log("Fast travel: no transit controller found on this map.");
                NotificationGate.Warn("Fast travel is not available on this map. Nothing was charged.");
                return false;
            }

            var fakeObject = (GameObject)null;
            var parametersField = FindField(typeof(EFT.Interactive.TransitPoint), "parameters");
            if (point == null)
            {
                fakeObject = new GameObject("OpenworldFastTravel");
                fakeObject.SetActive(false);
                point = fakeObject.AddComponent<EFT.Interactive.TransitPoint>();
                var made = Activator.CreateInstance(parametersField.FieldType);
                parametersField.SetValue(point, made);
                SetMember(made, "id", 9999);
                SetMember(made, "active", true);
                SetMember(made, "name", "Fast travel");
            }

            var parameters = parametersField.GetValue(point);
            var locationField = parameters.GetType().GetField("location", All);
            var oldLocation = locationField.GetValue(parameters);
            var keysType = method.GetParameters()[3].ParameterType;
            var keys = Activator.CreateInstance(keysType);

            Plugin.Log("Fast travel to " + target + " (" + locationId + "), " + jumps + " transits, " + price + " roubles: starting.");
            if (!await Pay(player, price))
            {
                NotificationGate.Warn("Could not take the roubles. Fast travel was cancelled.");
                return false;
            }

            RaidClock.Advance(jumps * Plugin.HoursPerTransit);
            try
            {
                locationField.SetValue(parameters, locationId);
                method.Invoke(controller, new object[] { point, 1, Guid.NewGuid().ToString("N"), keys, player });
            }
            finally
            {
                locationField.SetValue(parameters, oldLocation);
                if (fakeObject != null) UnityEngine.Object.Destroy(fakeObject);
            }

            Plugin.Log("Fast travel: transit to " + target + " started.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Fast travel failed: " + (e.InnerException ?? e));
            NotificationGate.Warn("Fast travel failed (see the log).");
            return false;
        }
    }

    private static void SetMember(object target, string name, object value)
    {
        target.GetType().GetField(name, All)?.SetValue(target, value);
    }
}
