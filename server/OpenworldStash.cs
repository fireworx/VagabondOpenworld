using System.Reflection;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;

namespace OpenworldAddon.Server;

public sealed class StashEntry
{
    public string Id { get; set; } = "";
    public string Tpl { get; set; } = "";
    public int Count { get; set; }
    /// <summary>How many items sit inside it (a case, a weapon's parts).</summary>
    public int Inside { get; set; }
    /// <summary>The current fill level (water left, medical uses, durability, bullets in a magazine), if it has one.</summary>
    public double? Cur { get; set; }
}

public sealed class StashListResponse
{
    public bool Ok { get; set; }
    public List<StashEntry> Items { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class StashTakeRequest : IRequestData
{
    public string Id { get; set; } = "";
}

public sealed class StashTakeResponse
{
    public bool Ok { get; set; }
    /// <summary>The item and everything in or on it; the top item has no parent.</summary>
    public List<Item> Items { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Reading the stash from inside a raid and taking items out of it. A taken item leaves the stash and is
/// mailed (sender: the BTR driver) so the client can pick it up into the carried inventory.
/// </summary>
public static class OpenworldStash
{
    private const string BtrDriver = "656f0f98d80a697f855d34b1";

    public static StashListResponse List(MongoId sessionId, ProfileHelper profiles)
    {
        var response = new StashListResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            if (pmc?.Inventory?.Items == null)
            {
                response.Error = "No inventory";
                return response;
            }

            var stash = pmc.Inventory.Stash?.ToString();
            var all = pmc.Inventory.Items;
            foreach (var item in all.Where(x => x.ParentId != null && x.ParentId == stash && x.SlotId == "hideout"))
            {
                response.Items.Add(new StashEntry
                {
                    Id = item.Id.ToString(),
                    Tpl = item.Template.ToString(),
                    Count = OpenworldHideout.StackOf(item),
                    Inside = CountBelow(item.Id.ToString(), all),
                    Cur = Fill(item, all),
                });
            }

            OpenworldLog.Trace("stash/list " + response.Items.Count + " roots; " + OpenworldHideout.Snapshot(pmc));
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("stash", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    private static double? Number(object? target, string name)
    {
        var value = OpenworldHideout.Prop(target, name);
        return value == null ? null : Convert.ToDouble(value);
    }

    /// <summary>The current fill level of an item: from its saved state, or the bullets in its magazine.</summary>
    private static double? Fill(Item item, List<Item> all)
    {
        var upd = item.Upd;
        var level = Number(OpenworldHideout.Prop(upd, "MedKit"), "HpResource")
                    ?? Number(OpenworldHideout.Prop(upd, "FoodDrink"), "HpPercent")
                    ?? Number(OpenworldHideout.Prop(upd, "Resource"), "Value")
                    ?? Number(OpenworldHideout.Prop(upd, "Repairable"), "Durability");
        if (level != null)
        {
            return level;
        }

        var bullets = all.Where(x => x.ParentId == item.Id.ToString() && x.SlotId == "cartridges").Sum(OpenworldHideout.StackOf);
        return bullets > 0 ? bullets : null;
    }

    private static int CountBelow(string id, List<Item> all)
    {
        var seen = new HashSet<string> { id };
        var queue = new Queue<string>([id]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in all.Where(x => x.ParentId == current))
            {
                if (seen.Add(child.Id.ToString()))
                {
                    queue.Enqueue(child.Id.ToString());
                }
            }
        }

        return seen.Count - 1;
    }

    private static (Item? Root, HashSet<string> Ids) Tree(PmcData? pmc, string id)
    {
        var items = pmc?.Inventory?.Items;
        var stash = pmc?.Inventory?.Stash?.ToString();
        var root = items?.FirstOrDefault(x => x.Id.ToString() == id && x.ParentId == stash && x.SlotId == "hideout");
        var ids = new HashSet<string>();
        if (root == null || items == null)
        {
            return (null, ids);
        }

        ids.Add(root.Id.ToString());
        bool grew;
        do
        {
            grew = false;
            foreach (var other in items)
            {
                if (other.ParentId != null && ids.Contains(other.ParentId) && ids.Add(other.Id.ToString()))
                {
                    grew = true;
                }
            }
        } while (grew);

        return (root, ids);
    }

    /// <summary>Hands out an item from the stash. It stays there until the client confirms with Remove.</summary>
    public static StashTakeResponse Take(MongoId sessionId, StashTakeRequest request, ProfileHelper profiles)
    {
        var response = new StashTakeResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            var (root, ids) = Tree(pmc, request.Id);
            if (pmc?.Inventory?.Items == null || root == null)
            {
                response.Error = "That item is not in the stash";
                return response;
            }

            response.Items = pmc.Inventory.Items.Where(x => ids.Contains(x.Id.ToString()))
                .GroupBy(x => x.Id.ToString()).Select(g => g.First())
                .Select(x => x.Id == root.Id ? x with { ParentId = null, SlotId = null, Location = null } : x)
                .ToList();
            OpenworldLog.Trace("stash/take " + root.Template + ": handed out " + response.Items.Count + " items (" + ids.Count + " ids)");
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("stash", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    /// <summary>The client has the item in its inventory now: it leaves the stash.</summary>
    public static StashTakeResponse Remove(MongoId sessionId, StashTakeRequest request, ProfileHelper profiles)
    {
        var response = new StashTakeResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            var (root, ids) = Tree(pmc, request.Id);
            if (pmc?.Inventory?.Items == null || root == null)
            {
                response.Error = "That item is not in the stash";
                return response;
            }

            OpenworldLog.Trace("stash/remove " + root.Template + " (" + ids.Count + " ids) before: " + OpenworldHideout.Snapshot(pmc));
            pmc.Inventory.Items.RemoveAll(x => ids.Contains(x.Id.ToString()));
            OpenworldLog.Info("Took " + root.Template + " out of the stash (" + ids.Count + " items).");
            OpenworldLog.Trace("stash/remove after: " + OpenworldHideout.Snapshot(pmc));
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("stash", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    /// <summary>
    /// Puts carried items into the stash (the hideout finds a free place for them). What does not fit is
    /// mailed instead. Returns "stash", "mail" or "both".
    /// </summary>
    public static string Put(MongoId sessionId, PmcData pmc, List<Item> items, IServiceProvider services,
        SPTarkov.Server.Core.Services.Commerce.MailSendService mail, ItemEventRouterResponse output)
    {
        // the inventory helper is looked up by name, so its exact namespace does not matter
        var helperType = typeof(ProfileHelper).Assembly.GetTypes().FirstOrDefault(t => t.Name == "InventoryHelper" && t.IsClass);
        var inventory = helperType == null ? null : services.GetService(helperType);
        if (inventory == null)
        {
            OpenworldLog.Trace("send: no InventoryHelper found, mailing the items");
        }

        // An item id must exist once in the profile. Items that were carried from the start of the raid are still in
        // the profile's equipment under their id: give those a new id for the stash instead of dropping them.
        var present = new HashSet<string>((pmc.Inventory?.Items ?? []).Select(x => x.Id.ToString()));
        var seen = new HashSet<string>();
        var renamed = new Dictionary<string, string>();
        var distinct = new List<Item>();
        foreach (var item in items)
        {
            var id = item.Id.ToString();
            if (!seen.Add(id))
            {
                OpenworldLog.Trace("send: item " + id + " (" + item.Template + ") came twice, the second one is left out");
                continue;
            }

            if (present.Contains(id))
            {
                renamed[id] = new MongoId().ToString();
            }

            distinct.Add(item);
        }

        if (renamed.Count > 0)
        {
            OpenworldLog.Trace("send: " + renamed.Count + " item ids were already in the profile and got new ids");
        }

        items = distinct.Select(item =>
        {
            var id = item.Id.ToString();
            var parent = item.ParentId != null && renamed.TryGetValue(item.ParentId, out var movedParent) ? movedParent : item.ParentId;
            return renamed.TryGetValue(id, out var fresh)
                ? item with { Id = new MongoId(fresh), ParentId = parent }
                : parent != item.ParentId ? item with { ParentId = parent } : item;
        }).ToList();
        var all = items.ToDictionary(x => x.Id.ToString());
        var roots = items.Where(x => x.ParentId == null || !all.ContainsKey(x.ParentId)).ToList();
        var failed = new List<Item>();
        var placed = 0;
        foreach (var root in roots)
        {
            var group = new List<Item> { root };
            var ids = new HashSet<string> { root.Id.ToString() };
            for (var i = 0; i < group.Count; i++)
            {
                foreach (var child in items.Where(x => x.ParentId == group[i].Id.ToString()))
                {
                    if (ids.Add(child.Id.ToString()))
                    {
                        group.Add(child);
                    }
                }
            }

            var top = root with { ParentId = null, SlotId = null, Location = null };
            group[0] = top;
            if (inventory != null && TryPlace(sessionId, pmc, group, inventory, output))
            {
                placed++;
            }
            else
            {
                failed.AddRange(group);
            }
        }

        OpenworldLog.Trace("send: " + placed + " of " + roots.Count + " roots went into the stash; " + OpenworldHideout.Snapshot(pmc));
        if (failed.Count > 0)
        {
            mail.SendDirectNpcMessageToPlayer(sessionId, BtrDriver,
                SPTarkov.Server.Core.Models.Enums.MessageType.MessageWithItems,
                "Your items from the raid (the stash had no room).", failed, 7 * 24 * 3600);
        }

        return roots.Count == 0 ? "none" : failed.Count == 0 ? "stash" : placed == 0 ? "mail" : "both";
    }

    private static bool TryPlace(MongoId sessionId, PmcData pmc, List<Item> group, object inventory, ItemEventRouterResponse output)
    {
        try
        {
            var method = inventory.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name is "AddItemToStash" or "AddItemsToStash")
                .OrderBy(m => m.Name == "AddItemToStash" ? 0 : 1)
                .FirstOrDefault();
            if (method == null)
            {
                OpenworldLog.Trace("send: no AddItemToStash on the inventory helper");
                return false;
            }

            var args = new List<object?>();
            foreach (var parameter in method.GetParameters())
            {
                var type = parameter.ParameterType;
                if (type == typeof(MongoId))
                {
                    args.Add(sessionId);
                }
                else if (type == typeof(PmcData))
                {
                    args.Add(pmc);
                }
                else if (type == typeof(ItemEventRouterResponse))
                {
                    args.Add(output);
                }
                else
                {
                    var request = Activator.CreateInstance(type)!;
                    OpenworldCraft.Set(request, "ItemWithModsToAdd", group);
                    OpenworldCraft.Set(request, "ItemsWithModsToAdd", new List<List<Item>> { group });
                    OpenworldCraft.Set(request, "FoundInRaid", false);
                    OpenworldCraft.Set(request, "UseSortingTable", false);
                    args.Add(request);
                }
            }

            method.Invoke(inventory, args.ToArray());
            var root = group[0].Id.ToString();
            return pmc.Inventory?.Items?.Any(x => x.Id.ToString() == root && x.ParentId != null) ?? false;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("stash put", e.InnerException ?? e);
            return false;
        }
    }
}
