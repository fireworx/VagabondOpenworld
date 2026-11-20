using System.Reflection;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;

namespace OpenworldAddon.Server;

public sealed class HideoutBuildRequest : IRequestData
{
    public int Area { get; set; }
    /// <summary>What the client could not pay from the carried inventory: taken from the stash on the server.</summary>
    public List<HideoutCostEntry> Take { get; set; } = [];
}

public sealed class HideoutCostEntry
{
    public string Tpl { get; set; } = "";
    public int Count { get; set; }
    /// <summary>How much of it lies in the stash (only filled in the list reply).</summary>
    public int Stash { get; set; }
}

/// <summary>A requirement of a station that is not material: another station, a skill, a trader level.</summary>
public sealed class HideoutOtherRequirement
{
    /// <summary>Area, Skill or TraderLoyalty.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The station number, the skill name or the trader id.</summary>
    public string Id { get; set; } = "";
    public int Level { get; set; }
    public bool Met { get; set; }
}

public sealed class HideoutAreaInfo
{
    public int Area { get; set; }
    public int Level { get; set; }
    public bool Maxed { get; set; }
    public List<HideoutCostEntry> Items { get; set; } = [];
    public List<HideoutOtherRequirement> Others { get; set; } = [];
}

public sealed class HideoutListResponse
{
    public bool Ok { get; set; }
    public List<HideoutAreaInfo> Areas { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Building hideout stations from inside a raid. The materials are taken from the carried inventory by
/// the client; the server checks the other requirements of the level (other stations, skills, trader
/// level) and runs its own "construction finished" step, which sets the level and its bonuses.
/// The way of raising a station is the one ZeroMenuTime uses (https://github.com/r2rka1/ZeroMenuTime, MIT).
/// </summary>
public static class OpenworldHideout
{
    internal static object? Prop(object? target, string name)
    {
        return target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
    }

    internal static int AsInt(object? value)
    {
        return value == null ? 0 : Convert.ToInt32(value);
    }

    /// <summary>The items that lie in the stash (directly or inside a container there), not the carried equipment.</summary>
    internal static List<Item> StashItems(PmcData pmc)
    {
        var all = pmc.Inventory?.Items ?? [];
        var stash = pmc.Inventory?.Stash?.ToString();
        var byId = new Dictionary<string, Item>();
        foreach (var item in all)
        {
            byId[item.Id.ToString()] = item;
        }

        var result = new List<Item>();
        if (stash == null)
        {
            return result;
        }

        foreach (var item in all)
        {
            var current = item;
            for (var i = 0; i < 64 && current.ParentId != null; i++)
            {
                var parent = current.ParentId.ToString();
                if (parent == stash)
                {
                    result.Add(item);
                    break;
                }

                if (!byId.TryGetValue(parent, out var up))
                {
                    break;
                }

                current = up;
            }
        }

        return result;
    }

    /// <summary>One line about the stash for the log: how many items lie in it and the sum of their stacks.</summary>
    internal static string Snapshot(PmcData? pmc)
    {
        if (pmc?.Inventory?.Items == null)
        {
            return "stash: no profile";
        }

        var stash = StashItems(pmc);
        return "stash: " + stash.Count + " items, stacks " + stash.Sum(StackOf) + ", profile items " + pmc.Inventory.Items.Count;
    }

    internal static int StackOf(Item item)
    {
        return (int)(item.Upd?.StackObjectsCount ?? 1);
    }

    internal static int InStash(List<Item> stash, string tpl)
    {
        return stash.Where(x => x.Template.ToString() == tpl).Sum(StackOf);
    }

    /// <summary>Takes count of tpl out of the stash; returns false (and changes nothing) if there is not enough.</summary>
    internal static bool TakeFromStash(PmcData pmc, List<Item> stash, string tpl, int count)
    {
        if (InStash(stash, tpl) < count)
        {
            return false;
        }

        var left = count;
        foreach (var item in stash.Where(x => x.Template.ToString() == tpl).OrderBy(StackOf).ToList())
        {
            if (left <= 0)
            {
                break;
            }

            var have = StackOf(item);
            if (have > left)
            {
                item.Upd ??= new Upd();
                item.Upd.StackObjectsCount = have - left;
                left = 0;
            }
            else
            {
                // gone, with whatever sits inside it
                var doomed = new HashSet<string> { item.Id.ToString() };
                bool grew;
                do
                {
                    grew = false;
                    foreach (var other in pmc.Inventory!.Items!)
                    {
                        if (other.ParentId != null && doomed.Contains(other.ParentId.ToString()) && doomed.Add(other.Id.ToString()))
                        {
                            grew = true;
                        }
                    }
                } while (grew);

                pmc.Inventory!.Items!.RemoveAll(x => doomed.Contains(x.Id.ToString()));
                left -= have;
            }
        }

        return true;
    }

    private static Stage? NextStage(HideoutTable table, object areaType, int level)
    {
        var station = table.Areas.FirstOrDefault(a => Equals(a.Type, areaType));
        Stage? next = null;
        return station?.Stages != null && station.Stages.TryGetValue((level + 1).ToString(), out next) ? next : null;
    }

    internal static List<HideoutOtherRequirement> Others(PmcData pmc, IEnumerable<object>? requirements)
    {
        var result = new List<HideoutOtherRequirement>();
        var levels = (pmc.Hideout?.Areas ?? []).ToDictionary(a => ((int)a.Type).ToString(), a => a.Level ?? 0);
        foreach (var need in requirements ?? [])
        {
            var kind = Prop(need, "Type")?.ToString() ?? "";
            if (kind.Equals("Area", StringComparison.OrdinalIgnoreCase))
            {
                var area = AsInt(Prop(need, "AreaType"));
                var wanted = AsInt(Prop(need, "RequiredLevel"));
                levels.TryGetValue(area.ToString(), out var have);
                result.Add(new HideoutOtherRequirement { Kind = "Area", Id = area.ToString(), Level = wanted, Met = have >= wanted });
            }
            else if (kind.Equals("Skill", StringComparison.OrdinalIgnoreCase))
            {
                var name = Prop(need, "SkillName")?.ToString() ?? "";
                var wanted = AsInt(Prop(need, "SkillLevel"));
                var skill = pmc.Skills?.Common?.FirstOrDefault(s => s.Id.ToString() == name);
                var have = (int)((skill?.Progress ?? 0) / 100);
                result.Add(new HideoutOtherRequirement { Kind = "Skill", Id = name, Level = wanted, Met = have >= wanted });
            }
            else if (kind.Equals("TraderLoyalty", StringComparison.OrdinalIgnoreCase))
            {
                var trader = Prop(need, "TraderId")?.ToString() ?? "";
                var wanted = AsInt(Prop(need, "LoyaltyLevel"));
                var have = 0;
                if (pmc.TradersInfo != null && pmc.TradersInfo.TryGetValue(new MongoId(trader), out var info))
                {
                    have = info.LoyaltyLevel ?? 0;
                }

                result.Add(new HideoutOtherRequirement { Kind = "TraderLoyalty", Id = trader, Level = wanted, Met = have >= wanted });
            }
        }

        return result;
    }

    public static HideoutListResponse List(MongoId sessionId, ProfileHelper profiles, HideoutTable table)
    {
        var response = new HideoutListResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            if (pmc?.Hideout?.Areas == null)
            {
                response.Error = "No hideout in the profile";
                return response;
            }

            var stash = StashItems(pmc);
            OpenworldLog.Trace("hideout/list " + Snapshot(pmc));
            foreach (var area in pmc.Hideout.Areas)
            {
                var level = area.Level ?? 0;
                var info = new HideoutAreaInfo { Area = (int)area.Type, Level = level };
                var next = NextStage(table, area.Type, level);
                if (next == null)
                {
                    info.Maxed = true;
                }
                else
                {
                    info.Items = (next.Requirements ?? [])
                        .Where(r => string.Equals(r.Type, "Item", StringComparison.OrdinalIgnoreCase) && (r.Count ?? 0) > 0)
                        .Select(r => new HideoutCostEntry { Tpl = r.TemplateId.ToString(), Count = r.Count ?? 0, Stash = InStash(stash, r.TemplateId.ToString()) })
                        .ToList();
                    info.Others = Others(pmc, next.Requirements);
                }

                response.Areas.Add(info);
            }

            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("hideout", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    public static HideoutListResponse Build(MongoId sessionId, HideoutBuildRequest request, ProfileHelper profiles,
        HideoutTable table, HideoutController controller, EventOutputHolder outputs)
    {
        var response = new HideoutListResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            var area = pmc?.Hideout?.Areas?.FirstOrDefault(a => (int)a.Type == request.Area);
            if (pmc == null || area == null)
            {
                response.Error = "No station numbered " + request.Area;
                return response;
            }

            var before = area.Level ?? 0;
            var next = NextStage(table, area.Type, before);
            if (next == null)
            {
                response.Error = "Already at its highest level";
                return response;
            }

            if (Others(pmc, next.Requirements).Any(x => !x.Met))
            {
                response.Error = "Other requirements are not met";
                return response;
            }

            // what the client could not pay from the carried inventory comes out of the stash
            var stash = StashItems(pmc);
            var take = (request.Take ?? []).Where(t => t.Count > 0).ToList();
            OpenworldLog.Trace("hideout/build area " + request.Area + " level " + before + ": from stash " + string.Join(", ", take.Select(t => t.Count + " x " + t.Tpl)) + "; " + Snapshot(pmc));
            foreach (var t in take)
            {
                var wanted = (next.Requirements ?? []).Where(r => string.Equals(r.Type, "Item", StringComparison.OrdinalIgnoreCase) && r.TemplateId.ToString() == t.Tpl).Sum(r => r.Count ?? 0);
                if (t.Count > wanted || InStash(stash, t.Tpl) < t.Count)
                {
                    response.Error = "Not enough of " + t.Tpl + " in the stash";
                    return response;
                }
            }

            foreach (var t in take)
            {
                TakeFromStash(pmc, stash, t.Tpl, t.Count);
                OpenworldLog.Info("Took " + t.Count + " x " + t.Tpl + " from the stash for " + area.Type + ".");
                stash = StashItems(pmc);
            }

            OpenworldLog.Trace("hideout/build after paying: " + Snapshot(pmc));

            // the server's own "construction finished" step: level, bonuses, a bigger stash, skill points
            controller.UpgradeComplete(pmc, new HideoutUpgradeCompleteRequestData
            {
                Action = "HideoutUpgradeComplete",
                AreaType = area.Type,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, sessionId, outputs.GetOutput(sessionId));

            if ((area.Level ?? 0) == before)
            {
                response.Error = "The level did not change";
                return response;
            }

            OpenworldLog.Info("" + area.Type + " is now level " + area.Level + ".");
            response.Ok = true;
        }
        catch (Exception e)
        {
            OpenworldLog.Error("hideout", e);
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }
}
