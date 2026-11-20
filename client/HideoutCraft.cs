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

namespace OpenworldAddon;

/// <summary>
/// Crafting at the hideout stations from inside a raid. Ingredients come from what you carry first, the
/// rest out of the stash; the craft runs in the hideout and is collected there after the raid.
/// </summary>
public static class HideoutCraft
{
    public static async void Open()
    {
        var groups = await Load();
        if (groups == null)
        {
            return;
        }

        MessagesPanel.Open("CRAFTING", "STATIONS", "CRAFT", groups, Load, false);
    }

    private static string Localized(string key, string fallback)
    {
        var text = key.Localized();
        return string.IsNullOrEmpty(text) || text == key ? fallback : text;
    }

    private static string Time(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? (int)span.TotalHours + "h " + span.Minutes + "m" : span.Minutes + "m " + span.Seconds + "s";
    }

    private static Item Make(ItemFactory factory, string tpl)
    {
        try
        {
            return factory.CreateItem(MongoID.Generate(), tpl, null);
        }
        catch (Exception e)
        {
            Plugin.LogError("Craft: no item for " + tpl + ": " + e.Message);
            return null;
        }
    }

    private static string Name(Item item, string tpl)
    {
        return item != null ? item.ShortName.Localized() : tpl;
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

            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/hideout/recipes", "{}"));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Recipe list failed: " + reply["Error"]);
                NotificationGate.Warn("Could not read the recipes.");
                return null;
            }

            // a station makes one thing at a time: what runs or waits there is shown with its COLLECT button
            // and the other recipes of that station are hidden until it is collected
            var running = new Dictionary<int, (string Recipe, int Left)>();
            var runningCrafts = new List<JToken>();
            var readyReply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/hideout/ready", "{}"));
            foreach (var craft in readyReply["Crafts"] as JArray ?? new JArray())
            {
                running[(int)craft["Area"]] = ((string)craft["Recipe"], (int)craft["Left"]);
                runningCrafts.Add(craft);
            }

            var carried = new Dictionary<string, int>();
            foreach (var (item, _) in LocalTrade.SendableItems(player))
            {
                carried[item.TemplateId] = carried.TryGetValue(item.TemplateId, out var have) ? have + item.StackObjectsCount : item.StackObjectsCount;
            }

            var factory = Singleton<ItemFactory>.Instance;
            var byArea = new SortedDictionary<int, List<(bool Can, MessagesPanel.Row Row)>>();
            foreach (var recipe in reply["Recipes"] as JArray ?? new JArray())
            {
                if ((recipe["Others"] as JArray ?? new JArray()).Any(o => !((bool?)o["Met"] ?? false)))
                {
                    continue; // station level, skill or trader level still missing: not offered
                }

                var area = (int)recipe["Area"];
                var id = (string)recipe["Id"];
                var productTpl = (string)recipe["Product"];
                var busy = running.TryGetValue(area, out var active);
                if (busy && active.Recipe != id)
                {
                    continue;
                }

                var product = Make(factory, productTpl);
                var can = true;
                var needs = new List<string>();
                var plan = new List<(string Tpl, int Need, int Stash)>();
                foreach (var cost in recipe["Items"] as JArray ?? new JArray())
                {
                    var tpl = (string)cost["Tpl"];
                    var need = (int)cost["Count"];
                    var stash = (int?)cost["Stash"] ?? 0;
                    carried.TryGetValue(tpl, out var have);
                    can &= have + stash >= need;
                    plan.Add((tpl, need, stash));
                    needs.Add(Name(Make(factory, tpl), tpl) + " " + Math.Min(need, have + stash) + "/" + need);
                }

                foreach (var tool in recipe["Tools"] as JArray ?? new JArray())
                {
                    var tpl = (string)tool["Tpl"];
                    carried.TryGetValue(tpl, out var have);
                    var ok = have + ((int?)tool["Stash"] ?? 0) >= (int)tool["Count"];
                    can &= ok;
                    needs.Add("tool " + Name(Make(factory, tpl), tpl) + (ok ? "" : " missing"));
                }

                var fuelError = (string)recipe["FuelError"];
                if (!string.IsNullOrEmpty(fuelError))
                {
                    can = false;
                    needs.Add(fuelError);
                }

                var count = (int)recipe["Count"];
                var label = Name(product, productTpl) + (count > 1 ? " x" + count : "") + "   " + Time((int)recipe["Seconds"])
                    + (needs.Count > 0 ? "\n" + string.Join(", ", needs) : "");
                var row = new MessagesPanel.Row { Item = product, Label = label, Good = can };
                if (busy)
                {
                    var rrecipe = id;
                    var rproduct = Name(product, productTpl);
                    row.Label = rproduct + (count > 1 ? " x" + count : "") + (active.Left > 0 ? "   " + Time(active.Left) + " left" : "   ready");
                    row.Good = active.Left == 0;
                    row.Verb = "COLLECT";
                    row.NoBulk = true;
                    if (active.Left == 0)
                    {
                        row.Act = () => Collect(rrecipe, rproduct);
                    }
                }
                else if (can)
                {
                    var rid = id;
                    var rplan = plan;
                    var rname = Name(product, productTpl);
                    row.Act = () => Craft(rid, rname, rplan);
                }

                if (!byArea.TryGetValue(area, out var list))
                {
                    byArea[area] = list = new List<(bool, MessagesPanel.Row)>();
                }

                list.Add((can, row));
            }

            // what runs or waits at a station: shown first, with its COLLECT button
            foreach (var craft in runningCrafts)
            {
                var area = (int)craft["Area"];
                var tpl = (string)craft["Product"];
                var product = Make(factory, tpl);
                var count = (int)craft["Count"];
                var left = (int)craft["Left"];
                var recipeId = (string)craft["Recipe"];
                var name = Name(product, tpl);
                var row = new MessagesPanel.Row
                {
                    Item = product,
                    Label = name + (count > 1 ? " x" + count : "") + (left > 0 ? "   " + Time(left) + " left" : "   ready"),
                    Good = left == 0,
                    Verb = "COLLECT",
                    NoBulk = true
                };
                if (left == 0)
                {
                    row.Act = () => Collect(recipeId, name);
                }

                if (!byArea.TryGetValue(area, out var list))
                {
                    byArea[area] = list = new List<(bool, MessagesPanel.Row)>();
                }

                list.Insert(0, (true, row));
            }

            var groups = new List<MessagesPanel.Group>();
            foreach (var pair in byArea)
            {
                var group = new MessagesPanel.Group
                {
                    Key = pair.Key.ToString(),
                    Name = Localized("hideout_area_" + pair.Key + "_name", "Station " + pair.Key)
                };
                var first = pair.Value.Take(running.ContainsKey(pair.Key) ? 1 : 0).ToList();
                foreach (var entry in first.Concat(pair.Value.Skip(first.Count).OrderByDescending(x => x.Can)))
                {
                    group.Rows.Add(entry.Row);
                }

                groups.Add(group);
            }

            if (groups.Count == 0)
            {
                groups.Add(new MessagesPanel.Group
                {
                    Name = "Nothing to craft",
                    Rows = { new MessagesPanel.Row { Label = "No recipe is available at your station levels." } }
                });
            }

            return groups;
        }
        catch (Exception e)
        {
            Plugin.LogError("Reading the recipes failed: " + e);
            NotificationGate.Warn("Could not read the recipes, see log.");
            return null;
        }
    }

    private static async Task<bool> Craft(string recipe, string name, List<(string Tpl, int Need, int Stash)> plan)
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

            var stock = LocalTrade.SendableItems(player).Select(x => x.Item).ToList();
            var fromStash = new List<object>();
            var pay = new List<(string Tpl, int Carried)>();
            foreach (var (tpl, need, stash) in plan)
            {
                var carried = Math.Min(need, stock.Where(x => x.TemplateId == tpl).Sum(x => x.StackObjectsCount));
                if (carried + stash < need)
                {
                    NotificationGate.Warn("Not enough material for " + name + ".");
                    return false;
                }

                pay.Add((tpl, carried));
                if (need > carried)
                {
                    fromStash.Add(new { Tpl = tpl, Count = need - carried });
                }
            }

            Plugin.Trace("Craft " + name + ": " + string.Join(", ", plan.Select((c, i) => c.Need + " x " + c.Tpl + " (carried " + pay[i].Carried + ", stash " + c.Stash + ")")));
            foreach (var (tpl, carried) in pay)
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

            var body = JsonConvert.SerializeObject(new { Recipe = recipe, Take = fromStash });
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/hideout/craft", body));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Crafting " + name + " failed on the server: " + reply["Error"] + " (carried material was already taken)");
                NotificationGate.Warn("Could not craft " + name + ": " + reply["Error"]);
                return false;
            }

            Plugin.Log("Hideout: started crafting " + name + ".");
            NotificationGate.Message(name + " is in production. Collect it in the hideout.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Crafting failed: " + e);
            NotificationGate.Warn("Could not craft, see log.");
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

    // ---------------------------------------------------------------------------------------------
    // Crafts that run or are finished.
    // ---------------------------------------------------------------------------------------------

    public static async void OpenReady()
    {
        var groups = await LoadReady();
        if (groups == null)
        {
            return;
        }

        MessagesPanel.Open("CRAFTS", "STATIONS", "COLLECT", groups, LoadReady);
    }

    public static async Task<List<MessagesPanel.Group>> LoadReady()
    {
        try
        {
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/hideout/ready", "{}"));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Craft list failed: " + reply["Error"]);
                NotificationGate.Warn("Could not read the crafts.");
                return null;
            }

            var factory = Singleton<ItemFactory>.Instance;
            var byArea = new SortedDictionary<int, List<MessagesPanel.Row>>();
            foreach (var craft in (reply["Crafts"] as JArray ?? new JArray()).OrderBy(x => (int)x["Left"]))
            {
                var area = (int)craft["Area"];
                var tpl = (string)craft["Product"];
                var product = Make(factory, tpl);
                var count = (int)craft["Count"];
                var left = (int)craft["Left"];
                var name = Name(product, tpl) + (count > 1 ? " x" + count : "");
                var row = new MessagesPanel.Row
                {
                    Item = product,
                    Label = name + (left > 0 ? "   " + Time(left) + " left" : "   ready"),
                    Good = left == 0
                };
                if (left == 0)
                {
                    var recipe = (string)craft["Recipe"];
                    row.Act = () => Collect(recipe, name);
                }

                if (!byArea.TryGetValue(area, out var list))
                {
                    byArea[area] = list = new List<MessagesPanel.Row>();
                }

                list.Add(row);
            }

            var groups = new List<MessagesPanel.Group>();
            foreach (var pair in byArea)
            {
                var group = new MessagesPanel.Group
                {
                    Key = pair.Key.ToString(),
                    Name = Localized("hideout_area_" + pair.Key + "_name", "Station " + pair.Key)
                };
                group.Rows.AddRange(pair.Value);
                groups.Add(group);
            }

            if (groups.Count == 0)
            {
                groups.Add(new MessagesPanel.Group
                {
                    Name = "No crafts",
                    Rows = { new MessagesPanel.Row { Label = "Nothing is being crafted." } }
                });
            }

            return groups;
        }
        catch (Exception e)
        {
            Plugin.LogError("Reading the crafts failed: " + e);
            NotificationGate.Warn("Could not read the crafts, see log.");
            return null;
        }
    }

    private static async Task<bool> Collect(string recipe, string name)
    {
        try
        {
            var body = JsonConvert.SerializeObject(new { Recipe = recipe });
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/hideout/collect", body));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Collecting " + name + " failed: " + reply["Error"]);
                NotificationGate.Warn("Could not collect " + name + ": " + reply["Error"]);
                return false;
            }

            Plugin.Trace("Collect " + name + ": reply " + reply);
            var mailed = (string)reply["Where"] == "mail";
            Plugin.Log("Hideout: collected " + name + " into the " + (mailed ? "mail" : "stash") + ".");
            NotificationGate.Message(mailed
                ? name + " is waiting in your messages (the stash is full). Use Read messages to take it."
                : name + " is in your stash. Use Open stash to take it.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Collecting failed: " + e);
            NotificationGate.Warn("Could not collect, see log.");
            return false;
        }
    }
}
