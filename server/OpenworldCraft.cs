using System.Collections;
using System.Reflection;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;

namespace OpenworldAddon.Server;

public sealed class CraftRequest : IRequestData
{
    public string Recipe { get; set; } = "";
    /// <summary>What the client could not pay from the carried inventory: taken from the stash on the server.</summary>
    public List<HideoutCostEntry> Take { get; set; } = [];
}

public sealed class CraftRecipe
{
    public string Id { get; set; } = "";
    public int Area { get; set; }
    public string Product { get; set; } = "";
    public int Count { get; set; }
    public int Seconds { get; set; }
    /// <summary>Consumed.</summary>
    public List<HideoutCostEntry> Items { get; set; } = [];
    /// <summary>Only needed, not consumed.</summary>
    public List<HideoutCostEntry> Tools { get; set; } = [];
    public List<HideoutOtherRequirement> Others { get; set; } = [];
    /// <summary>Set when crafts cost generator fuel and it is not enough (or the generator is off): the client does not offer the craft.</summary>
    public string? FuelError { get; set; }
}

public sealed class CraftReady
{
    public string Recipe { get; set; } = "";
    public int Area { get; set; }
    public string Product { get; set; } = "";
    public int Count { get; set; }
    /// <summary>Seconds still to go; 0 when it can be collected.</summary>
    public int Left { get; set; }
}

public sealed class CraftReadyResponse
{
    public bool Ok { get; set; }
    public List<CraftReady> Crafts { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class CraftListResponse
{
    public bool Ok { get; set; }
    /// <summary>After collecting a craft: "stash" or "mail".</summary>
    public string? Where { get; set; }
    public List<CraftRecipe> Recipes { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Crafting at the stations from inside a raid. The client pays what it carries, the server pays the rest out
/// of the stash and then starts the production the way the hideout screen does; the product is collected in
/// the hideout after the raid, like any other craft.
/// </summary>
public static class OpenworldCraft
{
    private static IEnumerable<object> Recipes(HideoutTable table)
    {
        var list = OpenworldHideout.Prop(OpenworldHideout.Prop(table, "Production"), "Recipes") as IEnumerable;
        return list?.Cast<object>() ?? [];
    }

    private static bool Running(PmcData pmc, string id)
    {
        return OpenworldHideout.Prop(pmc.Hideout, "Production") is IDictionary dict && dict.Contains(new MongoId(id));
    }

    private static bool Unlocked(PmcData pmc, object recipe, string id)
    {
        if (!(bool)(OpenworldHideout.Prop(recipe, "Locked") ?? false))
        {
            return true;
        }

        var unlocked = OpenworldHideout.Prop(OpenworldHideout.Prop(pmc, "UnlockedInfo"), "UnlockedProductionRecipe") as IEnumerable;
        return unlocked != null && unlocked.Cast<object>().Any(x => x.ToString() == id);
    }

    private static CraftRecipe? Describe(PmcData pmc, object recipe, List<Item> stash)
    {
        var id = OpenworldHideout.Prop(recipe, "Id")?.ToString() ?? "";
        if (id == "" || (bool)(OpenworldHideout.Prop(recipe, "Continuous") ?? false) || !Unlocked(pmc, recipe, id))
        {
            return null;
        }

        var result = new CraftRecipe
        {
            Id = id,
            Area = OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "AreaType")),
            Product = OpenworldHideout.Prop(recipe, "EndProduct")?.ToString() ?? "",
            Count = Math.Max(1, OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "Count"))),
            Seconds = OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "ProductionTime")),
        };

        var requirements = (OpenworldHideout.Prop(recipe, "Requirements") as IEnumerable)?.Cast<object>().ToList() ?? [];
        foreach (var need in requirements)
        {
            var kind = OpenworldHideout.Prop(need, "Type")?.ToString() ?? "";
            if (kind.Equals("Item", StringComparison.OrdinalIgnoreCase) || kind.Equals("Tool", StringComparison.OrdinalIgnoreCase))
            {
                var tpl = OpenworldHideout.Prop(need, "TemplateId")?.ToString() ?? "";
                var entry = new HideoutCostEntry
                {
                    Tpl = tpl,
                    Count = Math.Max(1, OpenworldHideout.AsInt(OpenworldHideout.Prop(need, "Count"))),
                    Stash = OpenworldHideout.InStash(stash, tpl),
                };
                (kind.Equals("Tool", StringComparison.OrdinalIgnoreCase) ? result.Tools : result.Items).Add(entry);
            }
            else if (!kind.Equals("Area", StringComparison.OrdinalIgnoreCase) && !kind.Equals("Skill", StringComparison.OrdinalIgnoreCase)
                     && !kind.Equals("TraderLoyalty", StringComparison.OrdinalIgnoreCase))
            {
                OpenworldLog.Trace("craft skipped: area " + result.Area + ", product " + result.Product + ", requirement type " + kind);
                return null; // fuel resources, quest conditions: not supported here
            }
        }

        result.Others = OpenworldHideout.Others(pmc, requirements);
        return result;
    }

    public static CraftListResponse List(MongoId sessionId, ProfileHelper profiles, HideoutTable table)
    {
        var response = new CraftListResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            OpenworldLog.Trace("hideout/recipes " + OpenworldHideout.Snapshot(pmc));
            if (pmc?.Hideout?.Areas == null)
            {
                response.Error = "No hideout in the profile";
                return response;
            }

            var stash = OpenworldHideout.StashItems(pmc);
            foreach (var recipe in Recipes(table))
            {
                var described = Describe(pmc, recipe, stash);
                if (described == null || Running(pmc, described.Id))
                {
                    continue;
                }

                if (OpenworldRules.Config.CraftsCostFuel)
                {
                    described.FuelError = OpenworldGenerator.TakeFuel(pmc, table, OpenworldRules.OriginalSeconds(described.Id, described.Seconds), false, false);
                }

                response.Recipes.Add(described);
            }

            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("craft", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    internal static void Set(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || !property.CanWrite)
        {
            OpenworldLog.Info("Craft: no property " + name + " on " + target.GetType().Name);
            return;
        }

        var type = property.PropertyType;
        var wanted = Nullable.GetUnderlyingType(type) ?? type;
        if (value is string text && wanted == typeof(MongoId))
        {
            value = new MongoId(text);
        }
        else if (value == null && wanted.IsClass)
        {
            value = Activator.CreateInstance(wanted);
        }
        else if (value != null && wanted.IsPrimitive)
        {
            value = Convert.ChangeType(value, wanted);
        }

        property.SetValue(target, value);
    }

    /// <summary>Calls a method of the hideout controller by name, building its arguments from their types.</summary>
    private static string? Call(HideoutController controller, string[] names, string action, PmcData pmc, string recipe,
        MongoId sessionId, ItemEventRouterResponse output)
    {
        var method = controller.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => names.Contains(m.Name))
            .OrderBy(m => Array.IndexOf(names, m.Name))
            .FirstOrDefault();
        if (method == null)
        {
            return "No method " + names[0] + " found on the hideout controller";
        }

        var args = new List<object?>();
        foreach (var parameter in method.GetParameters())
        {
            var type = parameter.ParameterType;
            if (type == typeof(PmcData))
            {
                args.Add(pmc);
            }
            else if (type == typeof(MongoId))
            {
                args.Add(sessionId);
            }
            else if (type == typeof(ItemEventRouterResponse))
            {
                args.Add(output);
            }
            else
            {
                var request = Activator.CreateInstance(type)!;
                Set(request, "Action", action);
                Set(request, "RecipeId", recipe);
                Set(request, "Items", null);
                Set(request, "Tools", null);
                Set(request, "Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                args.Add(request);
            }
        }

        var reply = method.Invoke(controller, args.ToArray());
        if (reply is Task task)
        {
            task.GetAwaiter().GetResult();
            reply = task.GetType().GetProperty("Result")?.GetValue(task);
        }

        var warnings = OpenworldHideout.Prop(reply ?? output, "Warnings") as IEnumerable;
        var first = warnings?.Cast<object>().FirstOrDefault();
        return first == null ? null : OpenworldHideout.Prop(first, "ErrorMessage")?.ToString() ?? "The hideout refused";
    }

    private static string? StartProduction(HideoutController controller, PmcData pmc, string recipe, MongoId sessionId, ItemEventRouterResponse output)
    {
        return Call(controller, ["SingleProductionStart", "StartProduction", "HideoutSingleProductionStart"], "HideoutSingleProductionStart", pmc, recipe, sessionId, output);
    }

    public static CraftListResponse Craft(MongoId sessionId, CraftRequest request, ProfileHelper profiles, HideoutTable table,
        HideoutController controller, EventOutputHolder outputs)
    {
        var response = new CraftListResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            if (pmc == null)
            {
                response.Error = "No profile";
                return response;
            }

            var stash = OpenworldHideout.StashItems(pmc);
            var recipe = Recipes(table).FirstOrDefault(r => OpenworldHideout.Prop(r, "Id")?.ToString() == request.Recipe);
            var described = recipe == null ? null : Describe(pmc, recipe, stash);
            if (described == null)
            {
                response.Error = "Unknown or unsupported recipe";
                return response;
            }

            if (Running(pmc, described.Id))
            {
                response.Error = "This recipe is already in production";
                return response;
            }

            // a station makes one thing at a time
            if (Productions(pmc, table).Any(p => p.Recipe != null && !(bool)(OpenworldHideout.Prop(p.Recipe, "Continuous") ?? false)
                && OpenworldHideout.AsInt(OpenworldHideout.Prop(p.Recipe, "AreaType")) == described.Area))
            {
                response.Error = "This station is busy: collect its finished craft first";
                return response;
            }

            if (described.Others.Any(x => !x.Met))
            {
                response.Error = "Other requirements are not met";
                return response;
            }

            var take = (request.Take ?? []).Where(t => t.Count > 0).ToList();
            foreach (var t in take)
            {
                var wanted = described.Items.Where(i => i.Tpl == t.Tpl).Sum(i => i.Count);
                if (t.Count > wanted || OpenworldHideout.InStash(stash, t.Tpl) < t.Count)
                {
                    response.Error = "Not enough of " + t.Tpl + " in the stash";
                    return response;
                }
            }

            var fuelSeconds = OpenworldRules.OriginalSeconds(described.Id, described.Seconds);
            if (OpenworldRules.Config.CraftsCostFuel)
            {
                var fuelError = OpenworldGenerator.TakeFuel(pmc, table, fuelSeconds, false);
                if (fuelError != null)
                {
                    response.Error = fuelError;
                    return response;
                }
            }

            OpenworldLog.Trace("hideout/craft " + described.Id + " (" + described.Product + "): from stash " + string.Join(", ", take.Select(t => t.Count + " x " + t.Tpl)) + "; " + OpenworldHideout.Snapshot(pmc));
            var error = StartProduction(controller, pmc, described.Id, sessionId, outputs.GetOutput(sessionId));
            if (error != null)
            {
                response.Error = error;
                return response;
            }

            // paid only after the hideout accepted the craft
            foreach (var t in take)
            {
                OpenworldHideout.TakeFromStash(pmc, stash, t.Tpl, t.Count);
                OpenworldLog.Info("Took " + t.Count + " x " + t.Tpl + " from the stash for a craft.");
                stash = OpenworldHideout.StashItems(pmc);
            }

            if (OpenworldRules.Config.CraftsCostFuel)
            {
                OpenworldGenerator.TakeFuel(pmc, table, fuelSeconds, true);
            }

            OpenworldLog.Info("Started crafting " + described.Product + " (" + described.Id + ").");
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("craft", e);
            response.Error = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message;
        }

        return response;
    }

    // ---------------------------------------------------------------------------------------------
    // Crafts that run or are finished.
    // ---------------------------------------------------------------------------------------------

    internal static List<(string Id, object Entry, object? Recipe)> Productions(PmcData pmc, HideoutTable table)
    {
        var result = new List<(string, object, object?)>();
        if (OpenworldHideout.Prop(pmc.Hideout, "Production") is not IDictionary dict)
        {
            return result;
        }

        var recipes = Recipes(table).ToDictionary(r => OpenworldHideout.Prop(r, "Id")?.ToString() ?? "", r => r);
        foreach (DictionaryEntry pair in dict)
        {
            var id = pair.Key.ToString() ?? "";
            if (pair.Value != null && recipes.TryGetValue(id, out var recipe))
            {
                result.Add((id, pair.Value, recipe));
            }
        }

        return result;
    }

    /// <summary>
    /// A continuous production (pure water) that has run longer than its production time can still hold no finished product:
    /// the hideout update does not always turn the progress into products. This does it: one product per full production time.
    /// Returns how many products wait afterwards.
    /// </summary>
    /// <summary>The bitcoin farm holds at most this many finished bitcoins; while it is full it produces nothing more.</summary>
    internal const int BitcoinCapacity = 3;

    internal static int CapProducts(object entry, int max)
    {
        if (OpenworldHideout.Prop(entry, "Products") is not IList list)
        {
            return 0;
        }

        if (list.Count >= max)
        {
            while (list.Count > max)
            {
                list.RemoveAt(list.Count - 1);
            }

            Set(entry, "Progress", 0.0);
        }

        return list.Count;
    }

    internal static int EnsureProducts(object entry, object recipe)
    {
        var products = OpenworldHideout.Prop(entry, "Products");
        if (products == null)
        {
            Set(entry, "Products", null);
            products = OpenworldHideout.Prop(entry, "Products");
        }

        if (products is not IList list)
        {
            return 0;
        }

        // the collector holds one finished product: while it waits, nothing more is produced (the progress stays at zero)
        if (list.Count > 0)
        {
            while (list.Count > 1)
            {
                list.RemoveAt(list.Count - 1);
            }

            Set(entry, "Progress", 0.0);
            return 1;
        }

        var seconds = OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "ProductionTime"));
        var product = OpenworldHideout.Prop(recipe, "EndProduct")?.ToString();
        if (seconds <= 0 || string.IsNullOrEmpty(product))
        {
            return 0;
        }

        var progress = Convert.ToDouble(OpenworldHideout.Prop(entry, "Progress") ?? 0.0);
        var made = progress >= seconds ? 1 : 0;
        if (made <= 0)
        {
            return 0;
        }

        var count = Math.Max(1, OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "Count")));
        for (var i = 0; i < made; i++)
        {
            list.Add(new Item { Id = new MongoId(), Template = new MongoId(product), Upd = new Upd { StackObjectsCount = count } });
        }

        Set(entry, "Progress", 0.0);
        OpenworldLog.Trace("continuous production: made " + made + " products from the progress");
        return made;
    }

    public static CraftReadyResponse Ready(MongoId sessionId, ProfileHelper profiles, HideoutTable table)
    {
        var response = new CraftReadyResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            if (pmc == null)
            {
                response.Error = "No profile";
                return response;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var (id, entry, recipe) in Productions(pmc, table))
            {
                if (recipe == null || (bool)(OpenworldHideout.Prop(recipe, "Continuous") ?? false))
                {
                    continue;
                }

                var seconds = OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "ProductionTime"));
                var start = Convert.ToInt64(OpenworldHideout.Prop(entry, "StartTimestamp") ?? 0L);
                var progress = Convert.ToDouble(OpenworldHideout.Prop(entry, "Progress") ?? 0.0);
                var left = start > 0 ? (int)Math.Max(0, start + seconds - now) : (int)Math.Max(0, seconds - progress);
                if (progress >= seconds)
                {
                    left = 0;
                }

                response.Crafts.Add(new CraftReady
                {
                    Recipe = id,
                    Area = OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "AreaType")),
                    Product = OpenworldHideout.Prop(recipe, "EndProduct")?.ToString() ?? "",
                    Count = Math.Max(1, OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "Count"))),
                    Left = left,
                });
            }

            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("craft", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    /// <summary>
    /// Takes a finished craft the way the hideout does (skill points, limits). The product lands in the stash,
    /// where "Open stash" reaches it in the raid; if the stash has no room it is mailed instead.
    /// </summary>
    public static CraftListResponse Collect(MongoId sessionId, CraftRequest request, ProfileHelper profiles, HideoutTable table,
        HideoutController controller, EventOutputHolder outputs, SPTarkov.Server.Core.Services.Commerce.MailSendService mail)
    {
        var response = new CraftListResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            if (pmc?.Inventory?.Items == null)
            {
                response.Error = "No profile";
                return response;
            }

            var continuous = request.Recipe == OpenworldGenerator.BitcoinRecipe
                || (bool)(OpenworldHideout.Prop(Recipes(table).FirstOrDefault(r => OpenworldHideout.Prop(r, "Id")?.ToString() == request.Recipe), "Continuous") ?? false);
            var ready = Ready(sessionId, profiles, table).Crafts.FirstOrDefault(c => c.Recipe == request.Recipe);
            if (!continuous && (ready == null || ready.Left > 0))
            {
                response.Error = "This craft is not finished";
                return response;
            }

            if (continuous && OpenworldHideout.Prop(pmc.Hideout, "Production") is IDictionary open && open.Contains(new MongoId(request.Recipe)))
            {
                if (request.Recipe == OpenworldGenerator.BitcoinRecipe)
                {
                    CapProducts(open[new MongoId(request.Recipe)]!, BitcoinCapacity);
                }

                var recipeData = Recipes(table).FirstOrDefault(r => OpenworldHideout.Prop(r, "Id")?.ToString() == request.Recipe);
                if (recipeData != null && request.Recipe != OpenworldGenerator.BitcoinRecipe)
                {
                    EnsureProducts(open[new MongoId(request.Recipe)]!, recipeData);
                }
            }

            OpenworldLog.Trace("hideout/collect " + request.Recipe + " before: " + OpenworldHideout.Snapshot(pmc));
            var before = new HashSet<string>(pmc.Inventory.Items.Select(x => x.Id.ToString()));
            var error = Call(controller, ["TakeProduction", "HideoutTakeProduction"], "HideoutTakeProduction", pmc, request.Recipe, sessionId, outputs.GetOutput(sessionId));
            if (error == null && continuous && request.Recipe != OpenworldGenerator.BitcoinRecipe
                && OpenworldHideout.Prop(pmc.Hideout, "Production") is IDictionary water && water.Contains(new MongoId(request.Recipe))
                && OpenworldHideout.Prop(water[new MongoId(request.Recipe)], "Products") is IList waiting)
            {
                // the hideout hands out one product per call and leaves it listed: take the listed products one by one
                // and remove each one, so every waiting product is given out exactly once
                var total = waiting.Count;
                var taken = 0;
                while (taken < total && waiting.Count > 0)
                {
                    if (taken > 0)
                    {
                        error = Call(controller, ["TakeProduction", "HideoutTakeProduction"], "HideoutTakeProduction", pmc, request.Recipe, sessionId, outputs.GetOutput(sessionId));
                        if (error != null)
                        {
                            break;
                        }
                    }

                    waiting.RemoveAt(0);
                    taken++;
                }

                OpenworldLog.Trace("hideout/collect: " + taken + " of " + total + " waiting products handed out");
            }

            var added = pmc.Inventory.Items.Where(x => !before.Contains(x.Id.ToString())).ToList();
            OpenworldLog.Trace("hideout/collect: the hideout added " + added.Count + " items" + (error != null ? ", warning: " + error : ""));
            if (added.Count > 0)
            {
                // the hideout put the product into the stash: it stays there
                response.Where = "stash";
            }
            else
            {
                // no room in the stash: the product comes by mail instead
                var recipe = Recipes(table).FirstOrDefault(r => OpenworldHideout.Prop(r, "Id")?.ToString() == request.Recipe);
                var product = OpenworldHideout.Prop(recipe, "EndProduct")?.ToString();
                if (recipe == null || string.IsNullOrEmpty(product))
                {
                    response.Error = error ?? "The hideout gave out nothing";
                    return response;
                }

                var count = Math.Max(1, OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "Count")));
                var parcel = new List<Item>
                {
                    new Item { Id = new MongoId(), Template = new MongoId(product), Upd = new Upd { StackObjectsCount = count } }
                };
                mail.SendDirectNpcMessageToPlayer(sessionId, "656f0f98d80a697f855d34b1",
                    SPTarkov.Server.Core.Models.Enums.MessageType.MessageWithItems,
                    "Your finished craft from the hideout (the stash was full).", parcel, 7 * 24 * 3600);
                response.Where = "mail";
            }

            // the craft is done and handed over: it must not be collectable again
            if (!continuous && OpenworldHideout.Prop(pmc.Hideout, "Production") is IDictionary running && running.Contains(new MongoId(request.Recipe)))
            {
                running.Remove(new MongoId(request.Recipe));
            }

            OpenworldLog.Trace("hideout/collect after: " + OpenworldHideout.Snapshot(pmc) + ", still listed: " + Ready(sessionId, profiles, table).Crafts.Any(c => c.Recipe == request.Recipe));
            OpenworldLog.Info("Collected craft " + request.Recipe + " into the " + response.Where + ".");
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("craft", e);
            response.Error = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message;
        }

        return response;
    }
}
