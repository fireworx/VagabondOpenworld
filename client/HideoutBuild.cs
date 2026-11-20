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
/// Building hideout stations from inside a raid. The window lists every station with what its next level
/// needs and what you carry and have in the stash; the materials are taken from the carried inventory
/// first, the rest out of the stash by the server, which also raises the level (and checks other stations, skills and trader levels).
/// </summary>
public static class HideoutBuild
{
    public static async void Open()
    {
        var groups = await Load();
        if (groups == null)
        {
            return;
        }

        MessagesPanel.Open("HIDEOUT", "STATIONS", "", groups, Load);
    }

    private static string Localized(string key, string fallback)
    {
        var text = key.Localized();
        return string.IsNullOrEmpty(text) || text == key ? fallback : text;
    }

    private static string AreaName(int area)
    {
        return Localized("hideout_area_" + area + "_name", "Station " + area);
    }

    private static Dictionary<string, int> Carried(Player player)
    {
        var counts = new Dictionary<string, int>();
        foreach (var (item, _) in LocalTrade.SendableItems(player))
        {
            counts[item.TemplateId] = counts.TryGetValue(item.TemplateId, out var have) ? have + item.StackObjectsCount : item.StackObjectsCount;
        }

        return counts;
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

            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/hideout/list", "{}"));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Hideout list failed: " + reply["Error"]);
                NotificationGate.Warn("Could not read the hideout.");
                return null;
            }

            var carried = Carried(player);
            var factory = Singleton<ItemFactory>.Instance;
            var ranked = new List<(int Rank, string Name, MessagesPanel.Group Group)>();
            foreach (var entry in (reply["Areas"] as JArray ?? new JArray()).OrderBy(x => (int)x["Area"]))
            {
                var area = (int)entry["Area"];
                var level = (int)entry["Level"];
                var maxed = (bool?)entry["Maxed"] ?? false;
                var name = AreaName(area);
                var group = new MessagesPanel.Group
                {
                    Key = area.ToString(),
                    Name = name + (maxed ? "  MAX" : "  LV " + level)
                };

                if (maxed)
                {
                    group.Rows.Add(new MessagesPanel.Row { Label = name + " is fully built." });
                    group.Tint = new Color32(231, 229, 212, 110);
                    ranked.Add((2, name, group));
                    continue;
                }

                var allMet = true;
                var costs = new List<(string Tpl, int Count, int Stash)>();
                foreach (var cost in entry["Items"] as JArray ?? new JArray())
                {
                    var tpl = (string)cost["Tpl"];
                    var need = (int)cost["Count"];
                    carried.TryGetValue(tpl, out var have);
                    var stash = (int?)cost["Stash"] ?? 0;
                    var good = have + stash >= need;
                    allMet &= good;
                    costs.Add((tpl, need, stash));
                    Item item = null;
                    try
                    {
                        item = factory.CreateItem(MongoID.Generate(), tpl, null);
                    }
                    catch (Exception e)
                    {
                        Plugin.LogError("Hideout: no item for " + tpl + ": " + e.Message);
                    }

                    var label = (item != null ? item.ShortName.Localized() : tpl) + "   " + (have + stash) + " / " + need
                        + (stash > 0 ? "   (" + have + " carried, " + stash + " stash)" : "");
                    group.Rows.Add(new MessagesPanel.Row { Item = item, Label = label, Good = good });
                }

                foreach (var other in entry["Others"] as JArray ?? new JArray())
                {
                    var met = (bool?)other["Met"] ?? false;
                    allMet &= met;
                    var kind = (string)other["Kind"];
                    var id = (string)other["Id"];
                    var wanted = (int)other["Level"];
                    string label;
                    if (kind == "Area")
                    {
                        label = AreaName(int.Parse(id)) + " level " + wanted;
                    }
                    else if (kind == "Skill")
                    {
                        label = Localized(id, id) + " skill level " + wanted;
                    }
                    else
                    {
                        label = Localized(id + " Nickname", id) + " loyalty level " + wanted;
                    }

                    group.Rows.Add(new MessagesPanel.Row { Label = label + (met ? "" : "  (missing)"), Good = met });
                }

                if (group.Rows.Count == 0)
                {
                    group.Rows.Add(new MessagesPanel.Row { Label = "Needs nothing." });
                }

                var a = area;
                var n = name;
                var c = costs;
                group.ActionLabel = level == 0 ? "BUILD" : "UPGRADE";
                group.ActionEnabled = allMet;
                group.Action = () => Build(a, n, c);
                if (allMet)
                {
                    group.Tint = new Color32(120, 205, 120, 255); // can be built now
                }

                ranked.Add((allMet ? 0 : 1, name, group));
            }

            // buildable first, then the rest, then the finished ones; each alphabetical
            return ranked.OrderBy(x => x.Rank).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Group).ToList();
        }
        catch (Exception e)
        {
            Plugin.LogError("Reading the hideout failed: " + e);
            NotificationGate.Warn("Could not read the hideout, see log.");
            return null;
        }
    }

    private static async Task<bool> Build(int area, string name, List<(string Tpl, int Count, int Stash)> costs)
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

            // materials first: out of what is carried, the rest is taken from the stash by the server
            var stock = LocalTrade.SendableItems(player).Select(x => x.Item).ToList();
            var fromStash = new List<object>();
            var plan = new List<(string Tpl, int Carried)>();
            foreach (var (tpl, count, stash) in costs)
            {
                var carried = Math.Min(count, stock.Where(x => x.TemplateId == tpl).Sum(x => x.StackObjectsCount));
                if (carried + stash < count)
                {
                    NotificationGate.Warn("Not enough material for " + name + ".");
                    return false;
                }

                plan.Add((tpl, carried));
                if (count > carried)
                {
                    fromStash.Add(new { Tpl = tpl, Count = count - carried });
                }
            }

            Plugin.Trace("Build " + name + ": " + string.Join(", ", costs.Select((c, i) => c.Count + " x " + c.Tpl + " (carried " + plan[i].Carried + ", stash " + c.Stash + ")")));
            foreach (var (tpl, carried) in plan)
            {
                var left = carried;
                foreach (var item in stock.Where(x => x.TemplateId == tpl).ToList())
                {
                    if (left <= 0)
                    {
                        break;
                    }

                    var take = Math.Min(left, item.StackObjectsCount);
                    if (!await LocalTrade.Take(player, item, take))
                    {
                        NotificationGate.Warn("Could not hand over the material for " + name + ".");
                        return false;
                    }

                    left -= take;
                }
            }

            var body = JsonConvert.SerializeObject(new { Area = area, Take = fromStash });
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/hideout/build", body));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Building " + name + " failed on the server: " + reply["Error"] + " (material was already taken)");
                NotificationGate.Warn("Could not build " + name + ": " + reply["Error"]);
                return false;
            }

            Plugin.Log("Hideout: built " + name + ".");
            NotificationGate.Message(name + " built.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Building failed: " + e);
            NotificationGate.Warn("Could not build, see log.");
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
