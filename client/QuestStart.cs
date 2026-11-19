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
