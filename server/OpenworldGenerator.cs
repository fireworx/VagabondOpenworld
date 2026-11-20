using System.Collections;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;

namespace OpenworldAddon.Server;

public sealed class GeneratorSlotInfo
{
    public int Index { get; set; }
    public string? Tpl { get; set; }
    public double Value { get; set; }
}

public sealed class GeneratorFuelInfo
{
    public string Id { get; set; } = "";
    public string Tpl { get; set; } = "";
    public double Value { get; set; }
}

public sealed class GeneratorResponse
{
    public bool Ok { get; set; }
    public int Level { get; set; }
    public bool Active { get; set; }
    public List<GeneratorSlotInfo> Slots { get; set; } = [];
    /// <summary>Fuel tanks that lie in the stash.</summary>
    public List<GeneratorFuelInfo> Fuel { get; set; } = [];
    public List<string> FuelTpls { get; set; } = [];
    /// <summary>Real seconds the fuel in the generator lasts while it runs.</summary>
    public double Seconds { get; set; }
    /// <summary>Finished products waiting in the station (bitcoins).</summary>
    public int Ready { get; set; }
    /// <summary>The recipe whose finished product Ready counts; send it to station/collect.</summary>
    public string? ReadyRecipe { get; set; }
    public string? Error { get; set; }
}

public sealed class StationRequest : IRequestData
{
    public int Area { get; set; } = 4;
}

public sealed class GeneratorLoadRequest : IRequestData
{
    public int Area { get; set; } = 4;
    /// <summary>A fuel tank from the stash ...</summary>
    public string? StashId { get; set; }
    /// <summary>... or one the player carries (already taken out of the carried inventory by the client).</summary>
    public List<Item>? Items { get; set; }
}

public sealed class GeneratorUnloadRequest : IRequestData
{
    public int Area { get; set; } = 4;
    public int Index { get; set; }
}

public sealed class GeneratorToggleRequest : IRequestData
{
    public bool On { get; set; }
}

/// <summary>
/// The hideout generator from inside a raid: which fuel tanks it holds, loading tanks into its slots, switching
/// it on and off, and how long the fuel lasts in real time.
/// </summary>
public static class OpenworldGenerator
{
    private const int GeneratorArea = 4;
    private const int WaterArea = 6;
    public const int BitcoinArea = 20;
    public const string BitcoinRecipe = "5d5c205bd582a50d042a3c0e";

    // what each station takes, with the amount a full one holds (0 = not a resource)
    private static readonly Dictionary<string, double> Tanks = new()
    {
        ["5d1b371186f774253763a656"] = 60,  // Expeditionary fuel tank
        ["5d1b36a186f7742523398433"] = 100, // Metal fuel tank
    };

    private static readonly Dictionary<string, double> Filters = new()
    {
        ["5d1b385e86f774252167b98a"] = 100, // Water filter
    };

    private static readonly Dictionary<string, double> Cards = new()
    {
        ["57347ca924597744596b4e71"] = 0, // Graphics card
    };

    private static Dictionary<string, double> Accepted(int area)
    {
        return area == WaterArea ? Filters : area == BitcoinArea ? Cards : Tanks;
    }

    private static double? Number(object? target, string name)
    {
        var value = OpenworldHideout.Prop(target, name);
        return value == null ? null : Convert.ToDouble(value);
    }

    private static double FuelOf(string tpl, object? upd, int area = GeneratorArea)
    {
        return Number(OpenworldHideout.Prop(upd, "Resource"), "Value") ?? (Accepted(area).TryGetValue(tpl, out var full) ? full : 0);
    }

    private static object? Area(PmcData pmc, int areaType)
    {
        return pmc.Hideout?.Areas?.FirstOrDefault(a => (int)a.Type == areaType);
    }

    /// <summary>Hideout Management at elite level (5100 points): extra slots and 25 % less filter use.</summary>
    private static bool IsElite(PmcData? pmc)
    {
        var common = OpenworldHideout.Prop(OpenworldHideout.Prop(pmc, "Skills"), "Common") as IEnumerable;
        var skill = common?.Cast<object>().FirstOrDefault(x => OpenworldHideout.Prop(x, "Id")?.ToString() == "HideoutManagement");
        return (Number(skill, "Progress") ?? 0) >= 5100;
    }

    private static int SlotCount(HideoutTable table, object? area, int level, PmcData? pmc, int areaType)
    {
        var fromTable = 0;
        var station = table.Areas.FirstOrDefault(a => a.Type != null && (int)a.Type.Value == areaType);
        var stages = OpenworldHideout.Prop(station, "Stages") as IDictionary;
        object? stage = null;
        if (stages != null && stages.Contains(level.ToString()))
        {
            stage = stages[level.ToString()];
            fromTable = OpenworldHideout.AsInt(OpenworldHideout.Prop(stage, "Slots"));
        }

        var inProfile = (OpenworldHideout.Prop(area, "Slots") as IEnumerable)?.Cast<object>().Count() ?? 0;
        // the profile holds an entry for every slot the generator can ever have (8), the table has no count:
        // level 1 has 2 slots, so assume two per level
        var count = fromTable > 0 ? fromTable : areaType == GeneratorArea ? Math.Max(2, level * 2) : inProfile > 0 ? inProfile : level;
        var elite = IsElite(pmc);
        if (fromTable == 0 && areaType == GeneratorArea && level >= 3 && elite)
        {
            count += 2; // Hideout Management at elite level: two more slots
        }
        else if (fromTable == 0 && areaType == WaterArea)
        {
            // one filter slot, one more at level 3, one more with elite Hideout Management
            count = Math.Min(inProfile > 0 ? inProfile : 3, 1 + (level >= 3 ? 1 : 0) + (elite ? 1 : 0));
        }

        OpenworldLog.Trace("station " + areaType + " slots (elite management: " + elite + "): table " + fromTable + " (stage found: " + (stage != null) + ", stages: " + (stages?.Count ?? -1) + "), profile " + inProfile + " -> " + count
            + (stage == null ? "" : "; stage props: " + string.Join(",", stage.GetType().GetProperties().Select(x => x.Name))));
        return count;
    }

    private static double FlowPerSecond(HideoutTable table)
    {
        var rate = Number(OpenworldHideout.Prop(table, "Settings"), "GeneratorFuelFlowRate");
        return rate is > 0 ? rate.Value : 1.0 / 867.0; // one unit lasts about 14 min 27 s
    }

    private static object? SlotAt(object area, int index)
    {
        if (OpenworldHideout.Prop(area, "Slots") is not IEnumerable slots)
        {
            return null;
        }

        foreach (var slot in slots)
        {
            if (OpenworldHideout.AsInt(OpenworldHideout.Prop(slot, "LocationIndex")) == index)
            {
                return slot;
            }
        }

        return null;
    }

    private static object? FirstItem(object? slot)
    {
        return (OpenworldHideout.Prop(slot, "Items") as IEnumerable)?.Cast<object>().FirstOrDefault();
    }

    /// <summary>
    /// Checks (and, with apply, takes) the generator fuel for something that would have run for the given seconds.
    /// Returns null when it is fine, otherwise why not.
    /// </summary>
    public static string? TakeFuel(PmcData pmc, HideoutTable table, double seconds, bool apply, bool trace = true)
    {
        var area = Area(pmc, GeneratorArea);
        if (area == null || OpenworldHideout.AsInt(OpenworldHideout.Prop(area, "Level")) == 0)
        {
            return "The generator is not built";
        }

        if (!(bool)(OpenworldHideout.Prop(area, "Active") ?? false))
        {
            return "The generator is switched off";
        }

        var need = seconds * FlowPerSecond(table);
        var count = SlotCount(table, area, OpenworldHideout.AsInt(OpenworldHideout.Prop(area, "Level")), pmc, GeneratorArea);
        var tanks = new List<object>();
        double have = 0;
        for (var i = 0; i < count; i++)
        {
            var resource = OpenworldHideout.Prop(OpenworldHideout.Prop(FirstItem(SlotAt(area, i)), "Upd"), "Resource");
            var value = Number(resource, "Value");
            if (resource != null && value > 0)
            {
                tanks.Add(resource);
                have += value.Value;
            }
        }

        if (trace)
        {
            OpenworldLog.Trace("fuel: " + tanks.Count + " tanks with fuel of " + count + " slots, have " + Math.Round(have, 2) + ", need " + Math.Round(need, 2));
        }

        if (have + 1e-9 < need)
        {
            return "Not enough generator fuel (needs " + Math.Round(need, 2) + ", has " + Math.Round(have, 2) + ")";
        }

        if (apply)
        {
            var left = need;
            foreach (var resource in tanks)
            {
                var value = Number(resource, "Value") ?? 0;
                var take = Math.Min(value, left);
                OpenworldCraft.Set(resource, "Value", value - take);
                left -= take;
                if (left <= 0)
                {
                    break;
                }
            }

            OpenworldLog.Info("A craft used " + Math.Round(need, 2) + " units of generator fuel.");
        }

        return null;
    }

    public static GeneratorResponse Info(MongoId sessionId, StationRequest request, ProfileHelper profiles, HideoutTable table)
    {
        var response = new GeneratorResponse();
        OpenworldLog.Trace("generator build v3");
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            var area = pmc == null ? null : Area(pmc, request.Area);
            if (pmc == null || area == null)
            {
                response.Error = "Station not found in the hideout";
                return response;
            }

            response.Level = OpenworldHideout.AsInt(OpenworldHideout.Prop(area, "Level"));
            response.Active = (bool)(OpenworldHideout.Prop(area, "Active") ?? false);
            response.FuelTpls = Accepted(request.Area).Keys.ToList();
            double total = 0;
            var slotCount = SlotCount(table, area, response.Level, pmc, request.Area);
            for (var i = 0; i < slotCount; i++)
            {
                var item = FirstItem(SlotAt(area, i));
                var tpl = OpenworldHideout.Prop(item, "Template")?.ToString();
                var info = new GeneratorSlotInfo { Index = i };
                if (item != null && tpl != null)
                {
                    info.Tpl = tpl;
                    info.Value = FuelOf(tpl, OpenworldHideout.Prop(item, "Upd"), request.Area);
                    total += info.Value;
                }

                response.Slots.Add(info);
            }

            foreach (var item in OpenworldHideout.StashItems(pmc).Where(x => Accepted(request.Area).ContainsKey(x.Template.ToString())))
            {
                response.Fuel.Add(new GeneratorFuelInfo { Id = item.Id.ToString(), Tpl = item.Template.ToString(), Value = FuelOf(item.Template.ToString(), item.Upd, request.Area) });
            }

            var rate = FlowPerSecond(table);
            // a water filter (100 units) lasts 8 h 12 min = 295.2 s per unit, 25 % longer with elite Hideout Management
            response.Seconds = request.Area == GeneratorArea ? total / rate : request.Area == WaterArea ? total * 295.2 / (IsElite(pmc) ? 0.75 : 1) : 0;
            if (request.Area == BitcoinArea && OpenworldHideout.Prop(pmc.Hideout, "Production") is IDictionary running && running.Contains(new MongoId(BitcoinRecipe)))
            {
                response.Ready = OpenworldCraft.CapProducts(running[new MongoId(BitcoinRecipe)]!, OpenworldCraft.BitcoinCapacity);
                response.ReadyRecipe = BitcoinRecipe;
            }
            else
            {
                // continuous production of this station (pure water): its finished products wait in Products
                foreach (var (id, entry, recipe) in OpenworldCraft.Productions(pmc, table))
                {
                    if (recipe == null || OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "AreaType")) != request.Area || !(bool)(OpenworldHideout.Prop(recipe, "Continuous") ?? false))
                    {
                        continue;
                    }

                    var count = OpenworldCraft.EnsureProducts(entry, recipe);
                    OpenworldLog.Trace("station " + request.Area + " continuous " + id + ": " + count + " products waiting, progress " + OpenworldHideout.Prop(entry, "Progress") + " of " + OpenworldHideout.Prop(recipe, "ProductionTime") + " s, start " + OpenworldHideout.Prop(entry, "StartTimestamp") + ", now " + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (count > 0)
                    {
                        response.Ready += count;
                        response.ReadyRecipe ??= id;
                    }
                }

                // finished crafts of this station
                foreach (var craft in OpenworldCraft.Ready(sessionId, profiles, table).Crafts.Where(x => x.Area == request.Area && x.Left == 0))
                {
                    response.Ready += craft.Count;
                    response.ReadyRecipe ??= craft.Recipe;
                }
            }

            OpenworldLog.Trace("station/info " + request.Area + " level " + response.Level + ", active " + response.Active + ", fuel " + total + ", rate/s " + rate + ", seconds " + response.Seconds);
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("generator", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    public static GeneratorResponse Toggle(MongoId sessionId, GeneratorToggleRequest request, ProfileHelper profiles, HideoutTable table)
    {
        var response = new GeneratorResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            var area = pmc == null ? null : Area(pmc, GeneratorArea);
            if (area == null)
            {
                response.Error = "No generator in the hideout";
                return response;
            }

            OpenworldCraft.Set(area, "Active", request.On);
            OpenworldLog.Info("The generator is now " + (request.On ? "on" : "off") + ".");
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("generator", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    public static GeneratorResponse Load(MongoId sessionId, GeneratorLoadRequest request, ProfileHelper profiles, HideoutTable table)
    {
        var response = new GeneratorResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            var area = pmc == null ? null : Area(pmc, request.Area);
            if (pmc?.Inventory?.Items == null || area == null)
            {
                response.Error = "Station not found in the hideout";
                return response;
            }

            var level = OpenworldHideout.AsInt(OpenworldHideout.Prop(area, "Level"));
            if (request.Area == WaterArea && level < 3)
            {
                response.Error = "Filters are only used from level 3 (pure water)";
                return response;
            }

            var count = SlotCount(table, area, level, pmc, request.Area);
            var free = Enumerable.Range(0, count).FirstOrDefault(i => FirstItem(SlotAt(area, i)) == null, -1);
            if (free < 0)
            {
                response.Error = "All slots are full";
                return response;
            }

            Item? source;
            if (!string.IsNullOrEmpty(request.StashId))
            {
                source = OpenworldHideout.StashItems(pmc).FirstOrDefault(x => x.Id.ToString() == request.StashId);
            }
            else
            {
                source = request.Items?.FirstOrDefault();
            }

            if (source == null || !Accepted(request.Area).ContainsKey(source.Template.ToString()))
            {
                response.Error = "This station does not take that item";
                return response;
            }

            // the slot entry
            var slots = OpenworldHideout.Prop(area, "Slots") as IList;
            if (slots == null)
            {
                OpenworldCraft.Set(area, "Slots", null);
                slots = OpenworldHideout.Prop(area, "Slots") as IList;
            }

            var slot = SlotAt(area, free);
            if (slot == null)
            {
                var slotType = slots!.GetType().GetGenericArguments()[0];
                slot = Activator.CreateInstance(slotType)!;
                OpenworldCraft.Set(slot, "LocationIndex", free);
                slots.Add(slot);
            }

            OpenworldCraft.Set(slot, "Items", null);
            var items = (IList)OpenworldHideout.Prop(slot, "Items")!;
            items.Clear();
            var entry = Activator.CreateInstance(items.GetType().GetGenericArguments()[0])!;
            OpenworldCraft.Set(entry, "Id", source.Id.ToString());
            OpenworldCraft.Set(entry, "Template", source.Template.ToString());
            OpenworldCraft.Set(entry, "Upd", source.Upd);
            items.Add(entry);

            if (!string.IsNullOrEmpty(request.StashId))
            {
                pmc.Inventory.Items.RemoveAll(x => x.Id.ToString() == request.StashId);
            }

            OpenworldLog.Info("Loaded an item (" + FuelOf(source.Template.ToString(), source.Upd, request.Area) + ") into station " + request.Area + " slot " + free + ".");
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("generator", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    /// <summary>Takes the tank out of a slot (empty or not) and puts it into the stash, or mails it if it does not fit.</summary>
    public static GeneratorResponse Unload(MongoId sessionId, GeneratorUnloadRequest request, ProfileHelper profiles, HideoutTable table,
        IServiceProvider services, SPTarkov.Server.Core.Services.Commerce.MailSendService mail, SPTarkov.Server.Core.Models.Eft.ItemEvent.ItemEventRouterResponse output)
    {
        var response = new GeneratorResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            var area = pmc == null ? null : Area(pmc, request.Area);
            var slot = area == null ? null : SlotAt(area, request.Index);
            var entry = FirstItem(slot);
            var tpl = OpenworldHideout.Prop(entry, "Template")?.ToString();
            if (pmc == null || entry == null || tpl == null)
            {
                response.Error = "That slot is empty";
                return response;
            }

            var item = new Item
            {
                Id = new MongoId(),
                Template = new MongoId(tpl),
                Upd = OpenworldHideout.Prop(entry, "Upd") as Upd
            };
            (OpenworldHideout.Prop(slot, "Items") as IList)?.Clear();
            var where = OpenworldStash.Put(sessionId, pmc, [item], services, mail, output);
            OpenworldLog.Info("Took an item (" + FuelOf(tpl, item.Upd, request.Area) + ") out of station " + request.Area + " slot " + request.Index + " -> " + where + ".");
            response.Ok = where != "none";
            if (!response.Ok)
            {
                response.Error = "Could not store the item";
            }
        }
        catch (Exception e)
        {
            OpenworldLog.Error("generator", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }
}
