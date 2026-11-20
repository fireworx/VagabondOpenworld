using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using JsonType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace OpenworldAddon;

/// <summary>
/// Reads the stash from inside a raid. Taking an item moves it out of the stash on the server (as mail),
/// and it is picked up straight away into the carried inventory like any mail item.
/// </summary>
public static class StashWindow
{
    private const int MaxRows = 200;

    public static async void Open()
    {
        var groups = await Load();
        if (groups == null)
        {
            return;
        }

        MessagesPanel.Open("STASH", "CATEGORY", "TAKE", groups, Load);
    }

    private static string Localized(string key, string fallback)
    {
        var text = key.Localized();
        return string.IsNullOrEmpty(text) || text == key ? fallback : text;
    }

    public static async Task<List<MessagesPanel.Group>> Load()
    {
        try
        {
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/stash/list", "{}"));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Stash list failed: " + reply["Error"]);
                NotificationGate.Warn("Could not read the stash.");
                return null;
            }

            var factory = Singleton<ItemFactory>.Instance;
            var byCategory = new SortedDictionary<string, List<(string Name, MessagesPanel.Row Row)>>();
            foreach (var entry in reply["Items"] as JArray ?? new JArray())
            {
                var tpl = (string)entry["Tpl"];
                Item item;
                try
                {
                    item = factory.CreateItem(MongoID.Generate(), tpl, null);
                }
                catch (Exception e)
                {
                    Plugin.LogError("Stash: no item for " + tpl + ": " + e.Message);
                    continue;
                }

                var count = (int)entry["Count"];
                var inside = (int)entry["Inside"];
                if (count > 1)
                {
                    item.StackObjectsCount = count;
                }

                var name = item.ShortName.Localized();
                var cur = (double?)entry["Cur"];
                var label = name + (count > 1 ? "  x" + count : "") + (cur.HasValue ? ItemFill.Text(item, cur.Value) : ItemFill.Text(item)) + (inside > 0 ? "  (+" + inside + " parts/contents)" : "");
                var id = (string)entry["Id"];
                var row = new MessagesPanel.Row { Item = item, Label = label, Act = () => Take(id, name) };
                var parent = item.Template?.ParentId;
                var category = string.IsNullOrEmpty(parent) ? "Other" : Localized(parent + " Name", "Other");
                if (!byCategory.TryGetValue(category, out var list))
                {
                    byCategory[category] = list = new List<(string, MessagesPanel.Row)>();
                }

                list.Add((name, row));
            }

            var groups = new List<MessagesPanel.Group>();
            foreach (var pair in byCategory)
            {
                var group = new MessagesPanel.Group { Name = pair.Key + "  (" + pair.Value.Count + ")", Key = pair.Key };
                foreach (var entry in pair.Value.OrderBy(x => x.Name).Take(MaxRows))
                {
                    group.Rows.Add(entry.Row);
                }

                if (pair.Value.Count > MaxRows)
                {
                    group.Rows.Add(new MessagesPanel.Row { Label = "... and " + (pair.Value.Count - MaxRows) + " more not shown" });
                }

                groups.Add(group);
            }

            if (groups.Count == 0)
            {
                groups.Add(new MessagesPanel.Group { Name = "Empty", Rows = { new MessagesPanel.Row { Label = "The stash is empty." } } });
            }

            return groups;
        }
        catch (Exception e)
        {
            Plugin.LogError("Reading the stash failed: " + e);
            NotificationGate.Warn("Could not read the stash, see log.");
            return null;
        }
    }

    /// <summary>Takes an item out of the stash straight into the carried inventory. It leaves the stash only once it is in.</summary>
    private static async Task<bool> Take(string id, string name)
    {
        var guarded = false;
        try
        {
            var player = GamePlayerOwner.MyPlayer;
            if (player == null)
            {
                return false;
            }

            var body = JsonConvert.SerializeObject(new { Id = id });
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/stash/take", body));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Taking " + name + " from the stash failed: " + reply["Error"]);
                NotificationGate.Warn("Could not take " + name + ": " + reply["Error"]);
                return false;
            }

            var flat = reply["Items"].ToString().ParseJsonTo<FlatItem[]>();
            var tree = Singleton<ItemFactory>.Instance.FlatItemsToTree(flat, true);
            if (tree.Items == null || !tree.Items.TryGetValue(id, out var root) || root == null)
            {
                Plugin.LogError("Stash item " + id + " did not come back as an item tree.");
                NotificationGate.Warn("Could not take " + name + ", see log.");
                return false;
            }

            StatsGuard.Begin(player.Profile);
            guarded = true;
            if (!await LocalTrade.Give(player, root))
            {
                NotificationGate.Warn("No room for " + name + ". It stays in the stash.");
                return false;
            }

            StatsGuard.Revert();
            var removed = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/stash/remove", body));
            Plugin.Trace("Stash take " + name + " (" + id + "): into the inventory, removed from the stash = " + ((bool?)removed["Ok"] ?? false));
            if (!((bool?)removed["Ok"] ?? false))
            {
                Plugin.LogError("Removing " + name + " from the stash failed: " + removed["Error"] + " (it is in the inventory and still in the stash)");
            }

            NotificationGate.Message("Took " + name + ".");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Taking from the stash failed: " + e);
            NotificationGate.Warn("Could not take the item, see log.");
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
