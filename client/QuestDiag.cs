using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using System;
using System.Linq;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.UI;
using HarmonyLib;

namespace OpenworldAddon;

/// <summary>Logs every step of the quest hand-in (TURN IN) so the place where it stops shows in the log.</summary>
internal static class QuestDiag
{
    public static void Install()
    {
        var harmony = new Harmony("openworld.questdiag");
        var prefix = new HarmonyMethod(typeof(QuestDiag), nameof(Log));
        foreach (var type in new[] { typeof(QuestController), typeof(QuestControllerClientBackend), typeof(QuestControllerClientGame), typeof(QuestControllerClientLocalGame) })
        {
            Hook(harmony, prefix, type, "HandoverItem");
        }

        var after = new HarmonyMethod(typeof(QuestDiag), nameof(After));
        foreach (var method in typeof(QuestControllerClientLocalGame).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name == "HandoverItem")
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(QuestDiag), nameof(Fix)), postfix: after, finalizer: new HarmonyMethod(typeof(QuestDiag), nameof(Failed)));
            }
        }

        Hook(harmony, prefix, typeof(QuestObjectiveView), "QuestHandover");
        // the window's base classes: the accept button, its switch and the close paths
        foreach (var type in typeof(Player).Assembly.GetTypes())
        {
            if (type.Name == "BaseMessageWindow" || type.Name == "HandoverItemsWindow" || type.Name == "HandoverSelectItemsWindow" || type.Name == "HandoverQuestItemsWindow")
            {
                foreach (var name in new[] { "SetAcceptActive", "Accept", "Decline", "Close", "OnAccept", "OnAcceptClick", "OnDecline" })
                {
                    Hook(harmony, prefix, type, name);
                }
            }
        }

        foreach (var method in typeof(HandoverQuestItemsWindow).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name == "Show")
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(QuestDiag), nameof(Raise)));
            }
        }

        Hook(harmony, prefix, typeof(HandoverQuestItemsWindow), "Show");
        Hook(harmony, prefix, typeof(HandoverQuestItemsWindow), "Accept");
        Hook(harmony, prefix, typeof(HandoverQuestItemsWindow), "TrySelectItemToHandover");
    }

    private static void Hook(Harmony harmony, HarmonyMethod prefix, Type type, string name)
    {
        try
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name == name && !method.IsAbstract)
                {
                    harmony.Patch(method, prefix: prefix);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.LogError("Quest diagnostics for " + type.Name + "." + name + ": " + e.Message);
        }
    }

    private static void Describe(object[] args)
    {
        try
        {
            var condition = args.Length > 1 ? args[1] : null;
            var text = "Quest hand-in: condition " + condition?.GetType().Name;
            foreach (var name in new[] { "value", "Value", "minDurability", "onlyFoundInRaid" })
            {
                var member = (object)condition?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? condition?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = member is FieldInfo f ? f.GetValue(condition) : member is PropertyInfo pr && pr.GetIndexParameters().Length == 0 ? pr.GetValue(condition, null) : null;
                if (value != null) text += " " + name + "=" + value;
            }

            Plugin.Log(text);
            if (args.Length > 2 && args[2] is EFT.InventoryLogic.Item[] items)
            {
                foreach (var item in items)
                {
                    string parent;
                    try
                    {
                        parent = item.Parent == null ? "null" : (item.Parent.Container?.ID ?? "?") + " of " + (item.Parent.Container?.ParentItem?.ShortName ?? "?");
                    }
                    catch (Exception e)
                    {
                        parent = "NONE (" + e.Message + ")";
                    }

                    Plugin.Log("Quest hand-in: item " + item.ShortName.Localized() + " id=" + item.Id + " x" + item.StackObjectsCount + " max=" + item.StackMaxSize + " parent=" + parent);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log("Quest hand-in describe failed: " + e.Message);
        }
    }

    private static double Number(object target, string name)
    {
        var type = target?.GetType();
        var value = type?.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
                    ?? type?.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target, null);
        return value == null ? 0 : Convert.ToDouble(value);
    }

    /// <summary>
    /// The game discards every item it is given whole, and the window lists a stack twice. A stack bigger than what the objective
    /// still needs would vanish completely, and the second discard of the same stack throws. The list is made distinct, and what
    /// is more than needed is copied beforehand and given back after the hand-in.
    /// </summary>
    private static void Fix(Quest quest, ConditionItem condition, ref Item[] items, ref Item __state)
    {
        try
        {
            __state = null;
            if (items == null || items.Length == 0)
            {
                return;
            }

            var distinct = items.GroupBy(x => x.Id).Select(g => g.First()).ToArray();
            if (distinct.Length != items.Length)
            {
                Plugin.Log("Quest hand-in: " + (items.Length - distinct.Length) + " duplicate item(s) removed from the hand-in.");
                items = distinct;
            }

            var needed = Number(condition, "value");
            var checkers = quest?.GetType().GetProperty("ProgressCheckers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(quest, null) as System.Collections.IDictionary;
            if (checkers != null)
            {
                foreach (System.Collections.DictionaryEntry entry in checkers)
                {
                    if (ReferenceEquals(entry.Key, condition))
                    {
                        needed -= Number(entry.Value, "CurrentValue");
                    }
                }
            }

            // money (and quest items) skip the selection window: the game hands over everything it finds. Only as many stacks as
            // needed are taken, the last one may be more than needed.
            if (needed > 0 && items.All(x => x.StackMaxSize > 1))
            {
                var picked = new System.Collections.Generic.List<Item>();
                double sum = 0;
                foreach (var item in items)
                {
                    if (sum >= needed)
                    {
                        break;
                    }

                    picked.Add(item);
                    sum += item.StackObjectsCount;
                }

                if (picked.Count != items.Length)
                {
                    Plugin.Log("Quest hand-in: " + picked.Count + " of " + items.Length + " stack(s) are enough.");
                    items = picked.ToArray();
                }
            }

            var total = items.Sum(x => (double)x.StackObjectsCount);
            var surplus = (int)Math.Min(total - needed, items[items.Length - 1].StackObjectsCount);
            var player = GamePlayerOwner.MyPlayer;
            if (needed <= 0 || surplus <= 0 || player == null || items[items.Length - 1].StackMaxSize <= 1)
            {
                return;
            }

            var last = items[items.Length - 1];
            var copy = last.CloneItem(player.InventoryController);
            copy.StackObjectsCount = surplus;
            copy.UnlimitedCount = false;
            __state = copy;
            Plugin.Log("Quest hand-in: " + total + " handed in for " + needed + " needed; " + surplus + " of " + last.ShortName.Localized() + " will be given back.");
        }
        catch (Exception e)
        {
            Plugin.LogError("Quest hand-in fix failed: " + e);
        }
    }

    /// <summary>The selection window opens under the raised trader screen (sorting order 500): give it a canvas of its own above that.</summary>
    private static void Raise(HandoverQuestItemsWindow __instance)
    {
        try
        {
            var go = __instance.gameObject;
            var canvas = go.GetComponent<UnityEngine.Canvas>() ?? go.AddComponent<UnityEngine.Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 600;
            if (go.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
            {
                go.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            }

            Plugin.Log("Quest hand-in: the selection window was raised above the trader screen.");
        }
        catch (Exception e)
        {
            Plugin.LogError("Could not raise the selection window: " + e.Message);
        }
    }

    private static Exception Failed(Exception __exception)
    {
        if (__exception != null)
        {
            Plugin.LogError("Quest hand-in: HandoverItem threw at once: " + __exception);
        }

        return __exception;
    }

    private static void After(object[] __args, Item __state, ref System.Threading.Tasks.Task<IResult> __result)
    {
        var items = __args.Length > 2 ? __args[2] as EFT.InventoryLogic.Item[] : null;
        var text = "";
        if (items != null)
        {
            foreach (var item in items)
            {
                text += " [" + item.ShortName.Localized() + " x" + item.StackObjectsCount + " FiR=" + item.SpawnedInSession + "]";
            }
        }

        Plugin.Log("Quest hand-in: HandoverItem with " + (items?.Length ?? 0) + " item(s):" + text);
        __result = Watch(__result, __state);
    }

    private static async System.Threading.Tasks.Task<IResult> Watch(System.Threading.Tasks.Task<IResult> original, Item giveBack)
    {
        try
        {
            var finished = await System.Threading.Tasks.Task.WhenAny(original, System.Threading.Tasks.Task.Delay(8000));
            if (finished != original)
            {
                Plugin.Log("Quest hand-in: still no answer after 8 s.");
            }

            var result = await original;
            Plugin.Log("Quest hand-in result: " + (result == null ? "null" : result.Failed ? "FAILED: " + result.Error : "ok"));
            if (giveBack != null && result != null && !result.Failed)
            {
                var player = GamePlayerOwner.MyPlayer;
                var returned = player != null && await LocalTrade.Give(player, giveBack);
                Plugin.Log("Quest hand-in: " + giveBack.StackObjectsCount + " given back: " + returned);
                NotificationGate.Message(returned ? "The surplus " + giveBack.ShortName.Localized() + " x" + giveBack.StackObjectsCount + " came back to your inventory."
                    : "Could not return the surplus " + giveBack.ShortName.Localized() + ".");
            }

            return result;
        }
        catch (Exception e)
        {
            Plugin.LogError("Quest hand-in threw: " + e);
            throw;
        }
    }

    private static void Log(MethodBase __originalMethod, object[] __args)
    {
        if (__originalMethod.Name == "HandoverItem" && __originalMethod.DeclaringType == typeof(QuestControllerClientLocalGame))
        {
            Describe(__args);
        }

        var extra = __args != null && __args.Length == 1 && __args[0] is bool flag ? " = " + flag : "";
        Plugin.Log("Quest hand-in step: " + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + "(" + (__args?.Length ?? 0) + " args)" + extra);
    }
}
