using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace OpenworldAddon;

/// <summary>
/// The hideout stations that work with inserted items, from inside a raid: generator (fuel tanks, on/off, real-time runtime),
/// water collector (filters) and bitcoin farm (graphics cards, collecting bitcoins). Only stations that are built are shown.
/// </summary>
public static class StationsWindow
{
    private const int Generator = 4;
    private const int Water = 6;
    private const int Bitcoin = 20;

    private sealed class Station
    {
        public int Area;
        public string Title;
        public string Noun;      // what goes into the slots
        public string NounPlural;
    }

    private static readonly Station[] All =
    [
        new Station { Area = Generator, Title = "Generator", Noun = "fuel tank", NounPlural = "Fuel tanks" },
        new Station { Area = Water, Title = "Water collector", Noun = "water filter", NounPlural = "Water filters" },
        new Station { Area = Bitcoin, Title = "Bitcoin farm", Noun = "graphics card", NounPlural = "Graphics cards" },
    ];

    public static async void Open()
    {
        var groups = await Load();
        if (groups == null)
        {
            return;
        }

        MessagesPanel.Open("STATIONS", "STATIONS", "LOAD", groups, Load);
    }

    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? (int)span.TotalHours + " h " + span.Minutes + " min" : span.Minutes + " min";
    }

    private static Item Make(string tpl)
    {
        try
        {
            return Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), tpl, null);
        }
        catch (Exception e)
        {
            Plugin.LogError("Stations: no item for " + tpl + ": " + e.Message);
            return null;
        }
    }

    private static string Amount(double value)
    {
        return value > 0 ? "  " + Math.Round(value).ToString("0") + " units" : "";
    }

    private static async Task<JObject> Info(int area)
    {
        var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/station/info", JsonConvert.SerializeObject(new { Area = area })));
        if (!((bool?)reply["Ok"] ?? false))
        {
            Plugin.LogError("Station " + area + " info failed: " + reply["Error"]);
            return null;
        }

        return reply;
    }

    public static async Task<List<MessagesPanel.Group>> Load()
    {
        try
        {
            var player = GamePlayerOwner.MyPlayer;
            if (player == null)
            {
                return null;
            }

            Plugin.PushRules();
            var infos = new Dictionary<int, JObject>();
            foreach (var station in All)
            {
                var info = await Info(station.Area);
                if (info != null && (int)info["Level"] > 0) // only stations that are built
                {
                    infos[station.Area] = info;
                }
            }

            if (infos.Count == 0)
            {
                NotificationGate.Warn("None of these stations is built in your hideout yet.");
                return null;
            }

            var generatorOn = infos.TryGetValue(Generator, out var gen) && ((bool?)gen["Active"] ?? false);
            var groups = new List<MessagesPanel.Group>();
            foreach (var station in All.Where(x => infos.ContainsKey(x.Area)))
            {
                groups.AddRange(Build(station, infos[station.Area], player, generatorOn));
            }

            return groups;
        }
        catch (Exception e)
        {
            Plugin.LogError("Reading the stations failed: " + e);
            NotificationGate.Warn("Could not read the stations, see log.");
            return null;
        }
    }

    private static IEnumerable<MessagesPanel.Group> Build(Station station, JObject reply, Player player, bool generatorOn)
    {
        var area = station.Area;
        var level = (int)reply["Level"];
        var active = (bool?)reply["Active"] ?? false;
        var seconds = (double?)reply["Seconds"] ?? 0;
        var ready = (int?)reply["Ready"] ?? 0;
        var readyRecipe = (string)reply["ReadyRecipe"];
        var slots = reply["Slots"] as JArray ?? new JArray();
        var free = slots.Any(x => string.IsNullOrEmpty((string)x["Tpl"]));
        var filled = slots.Count(x => !string.IsNullOrEmpty((string)x["Tpl"]));

        var status = new MessagesPanel.Group
        {
            Key = "station" + area,
            Name = station.Title + "  LV " + level,
        };

        if (area == Generator)
        {
            status.Tint = active ? new Color32(120, 205, 120, 255) : (Color?)null;
            status.Rows.Add(new MessagesPanel.Row { Label = active ? "Running" : "Switched off", Good = active, Note = "Status" });
            status.Rows.Add(new MessagesPanel.Row
            {
                Label = seconds <= 0 ? "No fuel" : "Fuel lasts about " + Duration(seconds) + " (real time" + (active ? "" : ", once switched on") + ")",
                Good = seconds > 0,
                Note = "Status"
            });
            status.ActionLabel = active ? "SWITCH OFF" : "SWITCH ON";
            status.Action = () => Toggle(!active);
        }
        else
        {
            if (!generatorOn)
            {
                status.Rows.Add(new MessagesPanel.Row
                {
                    Label = area == Water ? "The generator is switched off: the water collector still works, but more than 14 times slower." : "The generator is switched off, this station does not work.",
                    Good = area == Water,
                    Note = "Status"
                });
            }

            if (area == Water)
            {
                status.Rows.Add(new MessagesPanel.Row
                {
                    Label = seconds <= 0 ? "No filter inserted" : generatorOn ? "Filters last about " + Duration(seconds) + " (real time, one after another)" : "Filters inserted: " + Duration(seconds) + " of filter time with power",
                    Good = seconds > 0,
                    Note = "Status"
                });
                status.Rows.Add(new MessagesPanel.Row
                {
                    Label = ready + (ready == 1 ? " pure water" : " pure water") + " ready",
                    Good = ready > 0,
                    Note = "Status",
                    Verb = "COLLECT",
                    NoBulk = true,
                    Act = ready > 0 ? () => Collect(readyRecipe) : (Func<Task<bool>>)null
                });
            }

            if (area == Bitcoin)
            {
                status.Rows.Add(new MessagesPanel.Row
                {
                    Label = filled + " graphics cards inserted, " + ready + (ready == 1 ? " bitcoin" : " bitcoins") + " ready",
                    Good = filled > 0,
                    Note = "Status",
                    Verb = "COLLECT",
                    NoBulk = true,
                    Act = ready > 0 ? () => Collect(readyRecipe) : (Func<Task<bool>>)null
                });
            }
        }

        // what is inserted, each with its unload
        foreach (var slot in slots.Where(x => !string.IsNullOrEmpty((string)x["Tpl"])))
        {
            var tpl = (string)slot["Tpl"];
            var item = Make(tpl);
            var index = (int)slot["Index"];
            status.Rows.Add(new MessagesPanel.Row
            {
                Item = item,
                Label = "Slot " + ((int)slot["Index"] + 1) + ": " + (item != null ? item.ShortName.Localized() : tpl) + Amount((double)slot["Value"]),
                Note = "Inserted",
                Verb = "UNLOAD",
                NoBulk = true,
                Good = true,
                Act = () => Unload(area, index)
            });
        }

        // what can be inserted into the empty slots
        var empty = slots.Count - filled;
        if (area == Water && level < 3)
        {
            // pure water, which uses the filters, only exists from level 3
            empty = 0;
        }

        var heading = empty > 0 ? empty + (empty == 1 ? " slot is empty, you can insert" : " slots are empty, you can insert") : "All slots are full";
        if (empty > 0)
        {
            var accepted = new HashSet<string>((reply["FuelTpls"] as JArray ?? new JArray()).Select(x => (string)x));
            var any = false;
            foreach (var entry in reply["Fuel"] as JArray ?? new JArray())
            {
                var id = (string)entry["Id"];
                var item = Make((string)entry["Tpl"]);
                any = true;
                status.Rows.Add(new MessagesPanel.Row
                {
                    Item = item,
                    Label = (item != null ? item.ShortName.Localized() : (string)entry["Tpl"]) + Amount((double)entry["Value"]) + "  (stash)",
                    Note = heading,
                    Act = () => LoadStash(area, id)
                });
            }

            foreach (var (item, container) in LocalTrade.SendableItems(player).Where(x => accepted.Contains(x.Item.TemplateId)))
            {
                var it = item;
                any = true;
                status.Rows.Add(new MessagesPanel.Row
                {
                    Item = it,
                    Label = it.ShortName.Localized() + ItemFill.Text(it) + "  (" + container + ")",
                    Note = heading,
                    Act = () => LoadCarried(area, it)
                });
            }

            if (!any)
            {
                status.Rows.Add(new MessagesPanel.Row { Label = "You have no " + station.Noun + "s in the stash or on you.", Good = false, Note = heading });
            }
        }
        else
        {
            status.Rows.Add(new MessagesPanel.Row
            {
                Label = area == Water && level < 3 ? "Pure water, which uses filters, needs level 3. Filters are of no use before." : "All slots are full.",
                Good = false,
                Note = area == Water && level < 3 ? "Filters" : heading
            });
        }

        yield return status;
    }

    private static async Task<bool> Post(string route, object body, string done, string failed)
    {
        try
        {
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/" + route, JsonConvert.SerializeObject(body)));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError(route + " failed: " + reply["Error"]);
                NotificationGate.Warn(failed + ": " + reply["Error"]);
                return false;
            }

            if (!string.IsNullOrEmpty(done))
            {
                NotificationGate.Message(done);
            }

            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError(route + " failed: " + e);
            NotificationGate.Warn(failed + ", see log.");
            return false;
        }
    }

    private static Task<bool> Toggle(bool on)
    {
        return Post("generator/toggle", new { On = on }, "The generator is " + (on ? "on." : "off."), "Could not switch the generator");
    }

    private static Task<bool> Collect(string recipe)
    {
        return Post("station/collect", new { Recipe = recipe }, "It is in your stash.", "Could not collect");
    }

    private static Task<bool> Unload(int area, int index)
    {
        return Post("generator/unload", new { Area = area, Index = index }, "The item is in your stash.", "Could not unload");
    }

    private static Task<bool> LoadStash(int area, string id)
    {
        return Post("generator/load", new { Area = area, StashId = id }, "Loaded.", "Could not load");
    }

    private static async Task<bool> LoadCarried(int area, Item item)
    {
        var guarded = false;
        try
        {
            var player = GamePlayerOwner.MyPlayer;
            if (player == null)
            {
                return false;
            }

            StatsGuard.Begin(player.Profile);
            guarded = true;
            var flat = JArray.Parse(Singleton<ItemFactory>.Instance.TreeToFlatItems(new[] { item }).ToJson());
            if (!await LocalTrade.Take(player, item, 1))
            {
                NotificationGate.Warn("Could not take the item out of your inventory.");
                return false;
            }

            var body = new JObject { ["Area"] = area, ["Items"] = flat };
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/generator/load", body.ToString(Formatting.None)));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Station load failed: " + reply["Error"]);
                await LocalTrade.Give(player, item); // put it back
                NotificationGate.Warn("Could not load: " + reply["Error"]);
                return false;
            }

            NotificationGate.Message("Loaded.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Loading the station failed: " + e);
            NotificationGate.Warn("Could not load, see log.");
            return false;
        }
        finally
        {
            if (guarded)
            {
                StatsGuard.Revert();
                StatsGuard.End();
            }
        }
    }
}
