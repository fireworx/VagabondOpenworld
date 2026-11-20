// Adapted from ZeroMenuTime (https://github.com/r2rka1/ZeroMenuTime), MIT License, Copyright (c) 2026 r2rka1.
using System;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT.InventoryLogic;
using EFT.Quests;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using SPT.Reflection.Patching;

namespace OpenworldAddon;

/// <summary>
/// Accepting a quest in a raid only changes its status in the game: the items some quests give at
/// the start (and the trader's message) never come. The server makes them and mails them; they
/// show up under "Read messages".
/// </summary>
public class QuestStartItemsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(QuestControllerClientLocalGame), nameof(QuestControllerClientLocalGame.AcceptQuest));
    }

    [PatchPostfix]
    public static void Postfix(Quest quest, ref Task<IResult> __result)
    {
        if (!Plugin.IsInRaid())
        {
            return;
        }

        __result = After(__result, quest?.Id);
    }

    private static async Task<IResult> After(Task<IResult> original, string questId)
    {
        var result = await original;
        try
        {
            if (result != null && !result.Failed && !string.IsNullOrEmpty(questId))
            {
                var body = JsonConvert.SerializeObject(new { QuestId = questId });
                var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/quest/started", body));
                var count = (int?)reply["Count"] ?? 0;
                if (!((bool?)reply["Ok"] ?? false))
                {
                    Plugin.LogError("Starting items for quest " + questId + " not handed over: " + reply["Error"]);
                }
                else if (count > 0)
                {
                    Plugin.Log("Quest " + questId + " started; " + count + " starting item(s) are in your messages.");
                    NotificationGate.Message("Quest started. Its items are in your messages (Read messages).");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.LogError("Quest starting items failed: " + e.Message);
        }

        return result;
    }
}

/// <summary>
/// Diagnostics for handing items in to a trader's quest (TASKS tab, "TURN IN") during a raid: logs the call and what the game answers.
/// </summary>
public class QuestHandoverLogPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(QuestControllerClientLocalGame), nameof(QuestControllerClientLocalGame.HandoverItem));
    }

    [PatchPostfix]
    public static void Postfix(Quest quest, Item[] items, bool runNetworkTransaction, ref Task<IResult> __result)
    {
        Plugin.Log("Quest hand-over: quest " + quest?.Id + ", " + (items?.Length ?? 0) + " item(s), network=" + runNetworkTransaction + ", in raid=" + Plugin.IsInRaid());
        __result = After(__result);
    }

    private static async Task<IResult> After(Task<IResult> original)
    {
        try
        {
            var result = await original;
            Plugin.Log("Quest hand-over result: " + (result == null ? "null" : result.Failed ? "FAILED: " + result.Error : "ok"));
            return result;
        }
        catch (Exception e)
        {
            Plugin.LogError("Quest hand-over threw: " + e);
            throw;
        }
    }
}


/// <summary>
/// Handing in a quest in a raid: the game would build the reward items itself and look for room in the gear. The items are
/// made by the server instead and arrive as the trader's message (adapted from ZeroMenuTime, MIT, r2rka1).
/// </summary>
public class QuestRewardItemsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(QuestReward), nameof(QuestReward.TryAppendClaimResults));
    }

    [PatchPrefix]
    public static bool Prefix(QuestReward __instance, ref OperationResult __result, ref int clonedCount)
    {
        if (!Plugin.IsInRaid() || __instance.type != ERewardType.Item)
        {
            return true;
        }

        clonedCount = 0;
        __result = default(OperationResult);
        return false;
    }
}

public class QuestFinishRewardsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(QuestControllerClientLocalGame), nameof(QuestControllerClientLocalGame.FinishQuest));
    }

    [PatchPostfix]
    public static void Postfix(Quest quest, ref Task<OperationResult<ConditionalFinishResult<Quest>>> __result)
    {
        if (!Plugin.IsInRaid())
        {
            return;
        }

        __result = After(__result, quest?.Id);
    }

    private static async Task<OperationResult<ConditionalFinishResult<Quest>>> After(Task<OperationResult<ConditionalFinishResult<Quest>>> original, string questId)
    {
        var result = await original;
        try
        {
            if (!result.Failed && !string.IsNullOrEmpty(questId))
            {
                var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/quest/rewards", JsonConvert.SerializeObject(new { QuestId = questId })));
                if (!((bool?)reply["Ok"] ?? false))
                {
                    Plugin.LogError("Reward items for quest " + questId + " not handed over: " + reply["Error"]);
                    NotificationGate.Warn("The reward items of this quest could not be sent.");
                }
                else if (((int?)reply["Count"] ?? 0) > 0)
                {
                    Plugin.Log("Quest " + questId + " handed in; " + reply["Count"] + " reward item(s) are in your messages.");
                    NotificationGate.Message("Quest handed in. The reward items are in your messages (Read messages).");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.LogError("Quest reward items failed: " + e.Message);
        }

        return result;
    }
}
