// Adapted from ZeroMenuTime (https://github.com/r2rka1/ZeroMenuTime), MIT License, Copyright (c) 2026 r2rka1.
using System;
using System.Collections.Generic;
using System.Linq;
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
/// Takes what waits in the player's mail (quest items, rewards, insurance returns) into the carried
/// inventory during a raid. What does not fit stays in the mail. No Looting XP is kept for it.
/// </summary>
public static class Mail
{
    public sealed class Entry
    {
        public string RootId;
        public string Label;
        public Item Root;
    }

    private static bool _busy;

    /// <summary>One message that holds items.</summary>
    public sealed class Msg
    {
        public string Title;
        public string Sender;
        public string Text;
        public List<Entry> Entries = new List<Entry>();
    }

    private static string Short(string text, int max)
    {
        text = (text ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    /// <summary>Lists the messages that hold items (nothing is taken yet).</summary>
    public static async System.Threading.Tasks.Task<List<Msg>> Fetch()
    {
        try
        {
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/mail/list", "{}"));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Mail list failed: " + reply["Error"]);
                NotificationGate.Warn("Could not check messages.");
                return null;
            }

            var factory = Singleton<ItemFactory>.Instance;
            var result = new List<Msg>();
            foreach (var message in (reply["Messages"] as JArray ?? new JArray()).OrderByDescending(x => (long?)x["Time"] ?? 0))
            {
                var dialogId = (string)message["DialogId"] ?? "";
                var sender = dialogId == "656f0f98d80a697f855d34b1" ? "Hideout delivery" : (dialogId + " Nickname").Localized();
                var text = (string)message["Text"];
                if (string.IsNullOrWhiteSpace(text))
                {
                    var template = (string)message["TemplateId"];
                    text = string.IsNullOrWhiteSpace(template) ? "" : template.Localized();
                }

                var msg = new Msg { Sender = sender, Text = Short(text, 160), Title = sender + (text.Length > 0 ? ": " + Short(text, 40) : "") };
                foreach (var parcel in message["Parcels"] as JArray ?? new JArray())
                {
                    var rootId = (string)parcel["RootId"];
                    try
                    {
                        var flat = parcel["Items"].ToString().ParseJsonTo<FlatItem[]>();
                        var tree = factory.FlatItemsToTree(flat, true);
                        if (tree.Items == null || !tree.Items.TryGetValue(rootId, out var root) || root == null)
                        {
                            continue;
                        }

                        var count = root.StackObjectsCount;
                        msg.Entries.Add(new Entry
                        {
                            RootId = rootId,
                            Root = root,
                            Label = root.ShortName.Localized() + (count > 1 ? " x" + count : "")
                        });
                    }
                    catch (Exception e)
                    {
                        Plugin.LogError("Mail item " + rootId + " failed: " + e);
                    }
                }

                if (msg.Entries.Count > 0)
                {
                    msg.Title += " [" + msg.Entries.Count + "]";
                    result.Add(msg);
                }
            }

            return result;
        }
        catch (Exception e)
        {
            Plugin.LogError("Reading messages failed: " + e);
            NotificationGate.Warn("Could not check messages, see log.");
            return null;
        }
    }

    /// <summary>Takes one listed item into the carried inventory. True when it was taken.</summary>
    public static async System.Threading.Tasks.Task<bool> Take(Entry entry)
    {
        if (_busy)
        {
            return false;
        }

        _busy = true;
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

            if (!await LocalTrade.Give(player, entry.Root))
            {
                NotificationGate.Warn("No room for " + entry.Label + ". It stays in your messages.");
                return false;
            }

            StatsGuard.Revert();
            var confirm = JsonConvert.SerializeObject(new { RootIds = new[] { entry.RootId } });
            var confirmed = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/mail/taken", confirm));
            if (!((bool?)confirmed["Ok"] ?? false))
            {
                Plugin.LogError("Mail confirm failed: " + confirmed["Error"]);
            }

            Plugin.Log("Mail: took " + entry.Label);
            NotificationGate.Message("Took " + entry.Label + ".");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Taking mail item failed: " + e);
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

            _busy = false;
        }
    }

    /// <summary>Takes the item out of the inventory and has the server mail it to the hideout.</summary>
    public static async System.Threading.Tasks.Task<bool> Send(Item item, string label)
    {
        if (_busy)
        {
            return false;
        }

        _busy = true;
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

            var flat = JArray.Parse(Singleton<ItemFactory>.Instance.TreeToFlatItems(new[] { item }).ToJson());
            if (!await LocalTrade.Take(player, item, item.StackObjectsCount))
            {
                NotificationGate.Warn("Could not take " + label + " out of your inventory.");
                return false;
            }

            var body = new JObject { ["Items"] = flat };
            var reply = JObject.Parse(await RequestHandler.PostJsonAsync("/vagabond-openworld/mail/send", body.ToString(Formatting.None)));
            if (!((bool?)reply["Ok"] ?? false))
            {
                Plugin.LogError("Mail send failed: " + reply["Error"]);
                await LocalTrade.Give(player, item); // put it back
                NotificationGate.Warn("Could not send " + label + ".");
                return false;
            }

            Plugin.Log("Sent to hideout: " + label);
            NotificationGate.Message(label + " will be waiting in the hideout.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError("Sending item failed: " + e);
            NotificationGate.Warn("Could not send the item, see log.");
            return false;
        }
        finally
        {
            if (guarded)
            {
                StatsGuard.Revert();
                StatsGuard.End();
            }

            _busy = false;
        }
    }
}
