using System.Collections.Concurrent;
using System.Linq;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using Vagabond.Common.Api;

namespace OpenworldAddon.Server;

public class ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "dev.local.vagabond-openworld";
    public string Name { get; init; } = "Vagabond Openworld";
    public string Author { get; init; } = "local";
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public List<string>? Contributors { get; init; }
    public List<string>? Incompatibilities { get; init; }

    // make sure Vagabond is loaded first
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["dev.oogabooga.spt-vagabond"] = new SemanticVersioning.Range(">=0.8.0")
    };
}

public sealed class UnlockRequest : IRequestData
{
    public string? TraderId { get; set; }
}

public sealed class UnlockResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Extra routes for the trader-zone client addon. Only uses Vagabond's public API
/// (Vagabond.Common.Api.Api), no Vagabond file is changed.
/// </summary>
[Injectable]
public class OpenworldRouter(JsonUtil jsonUtil, ProfileHelper profileHelper, SPTarkov.Server.Core.Services.Commerce.MailSendService mailSend,
    SPTarkov.Server.Core.Helpers.Quest.QuestHelper questHelper,
    SPTarkov.Server.Core.Helpers.Quest.QuestRewardHelper questRewards,
    SPTarkov.Server.Core.Routers.EventOutputHolder outputs,
    TraderHelper traderHelper,
    HandbookHelper handbookHelper) : StaticRouter(jsonUtil, [
    // exfil identifier -> trader ids bound to it (trader_locations.json + other mods' AddTraderLocations)
    new RouteAction(
        "/vagabond-openworld/map",
        (_, _, _, _, _) =>
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in Api.GetTraderExtractions())
            {
                if (string.IsNullOrWhiteSpace(loc.ExfilIdentifier) || string.IsNullOrWhiteSpace(loc.TraderId))
                {
                    continue;
                }

                if (!map.TryGetValue(loc.ExfilIdentifier, out var list))
                {
                    map[loc.ExfilIdentifier] = list = new List<string>();
                }

                if (!list.Contains(loc.TraderId))
                {
                    list.Add(loc.TraderId);
                }
            }

            return ValueTask.FromResult<object>(jsonUtil.Serialize(map) ?? "{}");
        },
        typeof(UnlockRequest) // body is ignored
    ),
    // enable a trader for the running raid so its screen can be opened (Vagabond disables all but
    // the trader of the exit you used, and only re-enables at raid end)
    new RouteAction<UnlockRequest>(
        "/vagabond-openworld/unlock",
        (_, payload, sessionID, _, _) =>
        {
            var response = new UnlockResponse();
            var traderId = payload?.TraderId;

            if (string.IsNullOrWhiteSpace(traderId) ||
                !Api.GetTraderExtractions().Any(t => string.Equals(t.TraderId, traderId, StringComparison.Ordinal)))
            {
                response.Error = "unknown trader";
            }
            else
            {
                var pmc = profileHelper.GetPmcProfile(sessionID);
                if (pmc?.TradersInfo != null && pmc.TradersInfo.TryGetValue(new MongoId(traderId), out var info))
                {
                    info.Disabled = false;
                    info.Unlocked = true;
                    response.Ok = true;
                }
                else
                {
                    response.Error = "trader not in profile";
                }
            }

            return ValueTask.FromResult(jsonUtil.Serialize(response) ?? "{}");
        }
    ),
    new RouteAction<TradeReport>(
        "/vagabond-openworld/trade",
        (_, payload, sessionID, _, _) =>
            ValueTask.FromResult(jsonUtil.Serialize(OpenworldMail.RecordTrade(sessionID, payload, profileHelper, traderHelper, handbookHelper)) ?? "{}")
    ),
    new RouteAction<QuestStartRequest>(
        "/vagabond-openworld/quest/started",
        (_, payload, sessionID, _, _) =>
            ValueTask.FromResult(jsonUtil.Serialize(OpenworldMail.QuestStarted(sessionID, payload, profileHelper, questHelper, questRewards, outputs, mailSend)) ?? "{}")
    ),
    new RouteAction<SendRequest>(
        "/vagabond-openworld/mail/send",
        (_, payload, sessionID, _, _) =>
            ValueTask.FromResult(jsonUtil.Serialize(OpenworldMail.Send(sessionID, payload, profileHelper, mailSend)) ?? "{}")
    ),
    new RouteAction<MailRequest>(
        "/vagabond-openworld/mail/list",
        (_, payload, sessionID, _, _) =>
            ValueTask.FromResult(jsonUtil.Serialize(OpenworldMail.List(sessionID, payload, profileHelper)) ?? "{}")
    ),
    new RouteAction<MailRequest>(
        "/vagabond-openworld/mail/taken",
        (_, payload, sessionID, _, _) =>
            ValueTask.FromResult(jsonUtil.Serialize(OpenworldMail.MarkTaken(sessionID, payload, profileHelper)) ?? "{}")
    ),
]){
}

/// <summary>Asks about, or reports taking, items that wait in the player's mail.</summary>
public sealed class MailRequest : IRequestData
{
    public List<string>? RootIds { get; set; }
}

/// <summary>Items the player sends to the hideout from inside a raid.</summary>
public sealed class SendRequest : IRequestData
{
    /// <summary>The items and everything in or on them; the top items have no parent.</summary>
    public List<Item> Items { get; set; } = [];
}

public sealed class MoneyAmount
{
    public string Tpl { get; set; } = "";
    public double Count { get; set; }
}

/// <summary>Sent by the client after a trade at a trader in a raid settled.</summary>
public sealed class TradeReport : IRequestData
{
    public string TraderId { get; set; } = "";
    public List<MoneyAmount> Money { get; set; } = [];
}

public sealed class TradeReportResponse
{
    public bool Ok { get; set; }
    public double SalesSum { get; set; }
    public int LoyaltyLevel { get; set; }
    public string? Error { get; set; }
}

public sealed class QuestStartRequest : IRequestData
{
    public string QuestId { get; set; } = "";
}

public sealed class QuestStartResponse
{
    public bool Ok { get; set; }
    public int Count { get; set; }
    public string? Error { get; set; }
}

public sealed class MailParcel
{
    public string RootId { get; set; } = "";
    public List<Item> Items { get; set; } = [];
}

public sealed class MailMessage
{
    public string DialogId { get; set; } = "";
    public string Text { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public long Time { get; set; }
    public List<MailParcel> Parcels { get; set; } = [];
}

public sealed class MailListResponse
{
    public bool Ok { get; set; }
    public List<MailMessage> Messages { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Mail attachments (quest rewards, insurance returns, ...) that the client can take into the inventory
/// during a raid. Adapted from ZeroMenuTime (https://github.com/r2rka1/ZeroMenuTime), MIT License,
/// Copyright (c) 2026 r2rka1.
/// </summary>
public static class OpenworldMail
{
    /// <summary>The BTR driver's mail is what the "send items to hideout" delivery produces: not taken back in raid.</summary>
    private const string BtrDriver = "656f0f98d80a697f855d34b1";
    // (items the player sent to the hideout from a raid are mailed by this sender and can be fetched back in raid)

    private static IEnumerable<(string DialogId, Message Message)> PendingWithDialog(SptProfile? profile)
    {
        if (profile?.DialogueRecords == null)
        {
            yield break;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var entry in profile.DialogueRecords)
        {
            if (entry.Value?.Messages == null)
            {
                continue;
            }

            foreach (var message in entry.Value.Messages)
            {
                if (message?.Items?.Data == null || message.Items.Data.Count == 0)
                {
                    continue;
                }

                if (message.DateTime.HasValue && message.MaxStorageTime.HasValue &&
                    message.DateTime + message.MaxStorageTime < now)
                {
                    continue;
                }

                yield return (entry.Key.ToString(), message);
            }
        }
    }

    private static IEnumerable<Message> Pending(SptProfile? profile) => PendingWithDialog(profile).Select(x => x.Message);

    private static List<Item> WithChildren(Item root, List<Item> all)
    {
        var group = new List<Item> { root };
        var ids = new HashSet<string> { root.Id.ToString() };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var item in all)
            {
                if (item.ParentId != null && ids.Contains(item.ParentId) && ids.Add(item.Id.ToString()))
                {
                    group.Add(item);
                    added = true;
                }
            }
        }

        return group;
    }

    private static List<Item> Roots(Message message)
    {
        var data = message.Items!.Data!;
        var ids = new HashSet<string>(data.Select(x => x.Id.ToString()));
        return data.Where(x => x.ParentId == null || !ids.Contains(x.ParentId)).ToList();
    }

    /// <summary>Adds the money that changed hands to the trader's sales total and levels the trader up (as ZeroMenuTime does).</summary>
    public static TradeReportResponse RecordTrade(MongoId sessionId, TradeReport report, ProfileHelper profiles,
        TraderHelper traders, HandbookHelper handbook)
    {
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            MongoId traderId = report.TraderId;
            if (pmc?.TradersInfo == null || !pmc.TradersInfo.TryGetValue(traderId, out var info))
            {
                return new TradeReportResponse { Error = "Unknown trader or profile" };
            }

            double roubles = 0;
            foreach (var money in report.Money)
            {
                if (money.Count > 0)
                {
                    roubles += handbook.InRoubles(money.Count, money.Tpl);
                }
            }

            var trader = traders.GetTrader(traderId, sessionId);
            var inTraderCurrency = trader?.Currency == null
                ? roubles
                : handbook.FromRoubles(roubles, trader.Currency.Value.GetCurrencyTpl());

            info.SalesSum = (info.SalesSum ?? 0) + inTraderCurrency;
            traders.LevelUp(traderId, pmc);

            return new TradeReportResponse { Ok = true, SalesSum = info.SalesSum ?? 0, LoyaltyLevel = info.LoyaltyLevel ?? 1 };
        }
        catch (Exception e)
        {
            return new TradeReportResponse { Error = e.Message };
        }
    }

    private static readonly ConcurrentDictionary<string, HashSet<string>> Started = new();

    /// <summary>
    /// A quest accepted in a raid only changes its status in the game, so its starting items and its
    /// message never came. They are made here and mailed by the quest's trader, as in the menu.
    /// Adapted from ZeroMenuTime (MIT, r2rka1).
    /// </summary>
    public static QuestStartResponse QuestStarted(MongoId sessionId, QuestStartRequest request, ProfileHelper profiles,
        SPTarkov.Server.Core.Helpers.Quest.QuestHelper quests,
        SPTarkov.Server.Core.Helpers.Quest.QuestRewardHelper rewards,
        SPTarkov.Server.Core.Routers.EventOutputHolder outputs,
        SPTarkov.Server.Core.Services.Commerce.MailSendService mail)
    {
        var response = new QuestStartResponse();
        try
        {
            var pmc = profiles.GetPmcProfile(sessionId);
            if (pmc == null || string.IsNullOrEmpty(request.QuestId))
            {
                response.Error = "No profile or no quest";
                return response;
            }

            MongoId questId = request.QuestId;
            var quest = quests.GetQuestFromDb(questId, pmc);
            if (quest == null)
            {
                response.Error = "Unknown quest";
                return response;
            }

            // started from the menu before: the items were given then
            var known = pmc.Quests?.FirstOrDefault(x => x.QId == questId);
            if (known != null && known.Status != SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.AvailableForStart &&
                known.Status != SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.Locked)
            {
                response.Ok = true;
                return response;
            }

            var handed = Started.GetOrAdd(sessionId.ToString(), _ => []);
            lock (handed)
            {
                if (!handed.Add(request.QuestId))
                {
                    response.Ok = true;
                    return response;
                }
            }

            var output = outputs.GetOutput(sessionId);
            var items = rewards.ApplyQuestReward(pmc, questId, SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.Started, sessionId, output).ToList();
            var ids = new HashSet<string>(items.Select(x => x.Id.ToString()));
            response.Count = items.Count(x => x.ParentId == null || !ids.Contains(x.ParentId));
            if (items.Count > 0)
            {
                var text = quests.GetMessageIdForQuestStart(quest.StartedMessageText, quest.Description);
                mail.SendLocalisedNpcMessageToPlayer(sessionId, quest.TraderId,
                    SPTarkov.Server.Core.Models.Enums.MessageType.QuestStart, text, items, 7 * 24 * 3600);
            }

            response.Ok = true;
        }
        catch (Exception e)
        {
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    /// <summary>Delivered by the BTR driver (as the game's own delivery does), kept for a week.</summary>
    public static MailListResponse Send(MongoId sessionId, SendRequest request, ProfileHelper profiles,
        SPTarkov.Server.Core.Services.Commerce.MailSendService mail)
    {
        var response = new MailListResponse();
        try
        {
            if (profiles.GetPmcProfile(sessionId) == null || request.Items == null || request.Items.Count == 0)
            {
                response.Error = "Nothing to send";
                return response;
            }

            mail.SendDirectNpcMessageToPlayer(sessionId, BtrDriver,
                SPTarkov.Server.Core.Models.Enums.MessageType.MessageWithItems,
                "Your items from the raid.", request.Items, 7 * 24 * 3600);
            response.Ok = true;
        }
        catch (Exception e)
        {
            response.Error = e.GetType().Name + ": " + e.Message;
        }

        return response;
    }

    public static MailListResponse List(MongoId sessionId, MailRequest request, ProfileHelper profiles)
    {
        var response = new MailListResponse();
        try
        {
            foreach (var (dialogId, message) in PendingWithDialog(profiles.GetFullProfile(sessionId)))
            {
                var entry = new MailMessage
                {
                    DialogId = dialogId,
                    Text = message.Text ?? "",
                    TemplateId = message.TemplateId ?? "",
                    Time = message.DateTime ?? 0
                };
                foreach (var root in Roots(message))
                {
                    var group = WithChildren(root, message.Items!.Data!);
                    // sent without its place in the mail, so the client can put it anywhere
                    group[0] = root with { ParentId = null, SlotId = null, Location = null };
                    entry.Parcels.Add(new MailParcel { RootId = root.Id.ToString(), Items = group });
                }

                response.Messages.Add(entry);
            }

            response.Ok = true;
        }
        catch (Exception e)
        {
            response.Error = e.Message;
        }

        return response;
    }

    public static MailListResponse MarkTaken(MongoId sessionId, MailRequest request, ProfileHelper profiles)
    {
        var response = new MailListResponse();
        try
        {
            var taken = new HashSet<string>(request.RootIds ?? []);
            var profile = profiles.GetFullProfile(sessionId);
            foreach (var message in Pending(profile).ToList())
            {
                var data = message.Items!.Data!;
                foreach (var root in Roots(message).Where(x => taken.Contains(x.Id.ToString())))
                {
                    var remove = new HashSet<string>(WithChildren(root, data).Select(x => x.Id.ToString()));
                    data.RemoveAll(x => remove.Contains(x.Id.ToString()));
                }

                if (data.Count == 0)
                {
                    message.RewardCollected = true;
                }
            }

            // clear the "new attachments" marker on conversations with nothing left to collect
            if (profile?.DialogueRecords != null)
            {
                foreach (var entry in profile.DialogueRecords)
                {
                    if (entry.Value != null &&
                        !Pending(profile).Any(m => ReferenceEquals(entry.Value.Messages?.FirstOrDefault(x => ReferenceEquals(x, m)), m)))
                    {
                        entry.Value.AttachmentsNew = 0;
                    }
                }
            }

            response.Ok = true;
        }
        catch (Exception e)
        {
            response.Error = e.Message;
        }

        return response;
    }
}
