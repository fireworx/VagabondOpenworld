using System.Collections;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace OpenworldAddon.Server;

/// <summary>The switches of VagabondOpenworld.config.json (next to the server DLL; created on the first start, everything off).</summary>
public sealed class OpenworldConfig
{
    /// <summary>Hideout stations are built at once (or after BuildSeconds).</summary>
    public bool InstantBuild { get; set; }
    public double BuildSeconds { get; set; }
    /// <summary>The trader loyalty levels a station level asks for are dropped.</summary>
    public bool IgnoreTraderRequirements { get; set; }
    /// <summary>The levels of other stations a station level asks for are dropped.</summary>
    public bool IgnoreStationRequirements { get; set; }
    /// <summary>Station numbers that stay a requirement even with IgnoreStationRequirements: 22 is the defective wall, which
    /// the gear rack, the gym and the trophy stations need before they can be built; 24 is the first weapon stand, which the second one needs.</summary>
    public List<int> KeepRequirementsFor { get; set; } = [22, 24];
    /// <summary>Crafts (not the endless ones like the bitcoin farm or the water collector) are done at once.</summary>
    public bool InstantCrafts { get; set; }
    /// <summary>A craft in the raid menu costs generator fuel: the original craft time times the generator's fuel use.
    /// Without enough fuel (or with the generator off) the craft is refused.</summary>
    public bool CraftsCostFuel { get; set; }
    /// <summary>Quest hand-over objectives take any item, found in raid or not.</summary>
    public bool HandoverIgnoresFoundInRaid { get; set; } = true;
}

public sealed class RulesRequest : SPTarkov.Server.Core.Models.Utils.IRequestData
{
    /// <summary>Seconds for the pure water production; 0 uses the server data.</summary>
    public double WaterSeconds { get; set; }
    /// <summary>Seconds for one bitcoin with one graphics card; 0 uses the server data.</summary>
    public double BitcoinSeconds { get; set; }
}

public sealed class RulesResponse
{
    public bool Ok { get; set; }
    public int Water { get; set; }
    public int Bitcoin { get; set; }
}

/// <summary>Reads the config and applies the hideout rules to the server's hideout data once it is loaded.</summary>
// a late start, after the database and the other mods have loaded
[Injectable(TypePriority = 100000)]
public class OpenworldRules(HideoutTable table) : IOnLoad
{
    private static readonly Dictionary<string, double> OriginalTimes = new();
    private static OpenworldConfig? _config;

    public static OpenworldConfig Config => _config ??= Read();

    private static OpenworldConfig Read()
    {
        var path = Path.Combine(OpenworldLog.Folder, "VagabondOpenworld.config.json");
        var options = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip };
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<OpenworldConfig>(File.ReadAllText(path), options) ?? new OpenworldConfig();
            }

            var fresh = new OpenworldConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, options));
            Console.WriteLine("[VagabondOpenworld] Wrote " + path + " (all hideout rules are off).");
            return fresh;
        }
        catch (Exception e)
        {
            Console.WriteLine("[VagabondOpenworld] Could not read the config, using the defaults: " + e.Message);
            return new OpenworldConfig();
        }
    }

    /// <summary>The craft time the recipe had before it was set to instant.</summary>
    public static double OriginalSeconds(string recipe, double current)
    {
        return OriginalTimes.TryGetValue(recipe, out var seconds) ? seconds : current;
    }

    private static readonly Dictionary<string, double> ContinuousOriginals = new();

    /// <summary>
    /// Sets the production time of the endless recipes of the water collector (area 6) and the bitcoin farm (area 20),
    /// from the client's BepInEx settings; 0 puts the server's own value back.
    /// </summary>
    public static RulesResponse Apply(RulesRequest request, HideoutTable hideout)
    {
        var response = new RulesResponse();
        try
        {
            var recipes = OpenworldHideout.Prop(OpenworldHideout.Prop(hideout, "Production"), "Recipes") as IEnumerable;
            foreach (var recipe in recipes?.Cast<object>() ?? [])
            {
                if (!(bool)(OpenworldHideout.Prop(recipe, "Continuous") ?? false))
                {
                    continue;
                }

                var area = OpenworldHideout.AsInt(OpenworldHideout.Prop(recipe, "AreaType"));
                var wanted = area == 6 ? request.WaterSeconds : area == 20 ? request.BitcoinSeconds : -1;
                var id = OpenworldHideout.Prop(recipe, "Id")?.ToString();
                if (wanted < 0 || id == null)
                {
                    continue;
                }

                var current = Convert.ToDouble(OpenworldHideout.Prop(recipe, "ProductionTime") ?? 0);
                if (!ContinuousOriginals.ContainsKey(id))
                {
                    ContinuousOriginals[id] = current;
                }

                var target = wanted > 0 ? wanted : ContinuousOriginals[id];
                if (Math.Abs(target - current) > 0.001)
                {
                    OpenworldCraft.Set(recipe, "ProductionTime", target);
                    OpenworldLog.Info("Production time of the endless recipe " + id + " (area " + area + ") is now " + target + " s (server data: " + ContinuousOriginals[id] + " s).");
                }

                if (area == 6)
                {
                    response.Water++;
                }
                else
                {
                    response.Bitcoin++;
                }
            }

            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("rules", e);
        }

        return response;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = Config;
            var stages = 0;
            var dropped = 0;
            var kept = 0;
            var crafts = 0;
            if (config.InstantBuild || config.IgnoreTraderRequirements || config.IgnoreStationRequirements)
            {
                foreach (var area in (OpenworldHideout.Prop(table, "Areas") as IEnumerable ?? Array.Empty<object>()).Cast<object>())
                {
                    if (OpenworldHideout.Prop(area, "Stages") is not IDictionary all)
                    {
                        continue;
                    }

                    foreach (var stage in all.Values)
                    {
                        stages++;
                        if (config.InstantBuild)
                        {
                            OpenworldCraft.Set(stage, "ConstructionTime", config.BuildSeconds);
                        }

                        if (OpenworldHideout.Prop(stage, "Requirements") is IList requirements)
                        {
                            for (var i = requirements.Count - 1; i >= 0; i--)
                            {
                                var kind = OpenworldHideout.Prop(requirements[i], "Type")?.ToString() ?? "";
                                var needed = kind.Equals("Area", StringComparison.OrdinalIgnoreCase) ? OpenworldHideout.AsInt(OpenworldHideout.Prop(requirements[i], "AreaType")) : -1;
                                if (needed >= 0 && (config.KeepRequirementsFor ?? []).Contains(needed))
                                {
                                    kept++;
                                    continue;
                                }

                                if ((config.IgnoreTraderRequirements && kind.Equals("TraderLoyalty", StringComparison.OrdinalIgnoreCase))
                                    || (config.IgnoreStationRequirements && kind.Equals("Area", StringComparison.OrdinalIgnoreCase)))
                                {
                                    requirements.RemoveAt(i);
                                    dropped++;
                                }
                            }
                        }
                    }
                }
            }

            if (config.InstantCrafts)
            {
                var recipes = OpenworldHideout.Prop(OpenworldHideout.Prop(table, "Production"), "Recipes") as IEnumerable;
                foreach (var recipe in recipes?.Cast<object>() ?? [])
                {
                    if ((bool)(OpenworldHideout.Prop(recipe, "Continuous") ?? false))
                    {
                        continue;
                    }

                    var id = OpenworldHideout.Prop(recipe, "Id")?.ToString();
                    var seconds = Convert.ToDouble(OpenworldHideout.Prop(recipe, "ProductionTime") ?? 0);
                    if (id != null && seconds > 0)
                    {
                        OriginalTimes[id] = seconds;
                        OpenworldCraft.Set(recipe, "ProductionTime", 0);
                        crafts++;
                    }
                }
            }

            Console.WriteLine("[VagabondOpenworld] Hideout rules: " + stages + " station levels checked, " + dropped + " requirements dropped, " + kept + " kept (stations " + string.Join(",", config.KeepRequirementsFor ?? []) + "), "
                + crafts + " crafts made instant" + (config.CraftsCostFuel ? ", crafts in the raid menu cost generator fuel." : "."));
        }
        catch (Exception e)
        {
            Console.WriteLine("[VagabondOpenworld] Hideout rules failed: " + e);
        }

        return Task.CompletedTask;
    }
}


/// <summary>Makes the quests' "hand over" objectives accept items that were not found in raid (the stash and delivered items are not).</summary>
[Injectable(TypePriority = 100001)]
public class OpenworldQuestRules(IServiceProvider services) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!OpenworldRules.Config.HandoverIgnoresFoundInRaid)
            {
                return Task.CompletedTask;
            }

            // the database service is looked up by name, so its exact namespace does not matter
            var type = typeof(SPTarkov.Server.Core.Models.Utils.IRequestData).Assembly.GetTypes().FirstOrDefault(x => x.Name == "DatabaseService" && x.IsClass);
            var database = type == null ? null : services.GetService(type);
            var changed = 0;
            if (database?.GetType().GetMethod("GetQuests", Type.EmptyTypes)?.Invoke(database, null) is IDictionary quests)
            {
                foreach (var quest in quests.Values)
                {
                    var conditions = OpenworldHideout.Prop(quest, "Conditions");
                    foreach (var name in new[] { "AvailableForFinish", "AvailableForStart", "Fail" })
                    {
                        if (OpenworldHideout.Prop(conditions, name) is not IEnumerable list)
                        {
                            continue;
                        }

                        foreach (var condition in list)
                        {
                            if (OpenworldHideout.Prop(condition, "ConditionType")?.ToString() == "HandoverItem"
                                && OpenworldHideout.Prop(condition, "OnlyFoundInRaid") is true)
                            {
                                OpenworldCraft.Set(condition, "OnlyFoundInRaid", false);
                                changed++;
                            }
                        }
                    }
                }
            }

            Console.WriteLine("[VagabondOpenworld] Quest hand-overs: " + changed + " objective(s) no longer need found-in-raid items.");
        }
        catch (Exception e)
        {
            Console.WriteLine("[VagabondOpenworld] Could not change the quest hand-overs: " + e.Message);
        }

        return Task.CompletedTask;
    }
}
