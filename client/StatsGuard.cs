using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using UnityEngine;

namespace OpenworldAddon;

/// <summary>
/// Listing the carried items in the trade screen, and handing over bought items and sale money, make the
/// game count them as picked up: loot counters, "Looting" experience and skill experience (Searching).
/// While a trade screen is open, put those values back to what they were when it opened.
/// </summary>
public static class StatsGuard
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private class SkillField
    {
        public object Target;
        public FieldInfo Field;
        public object Value;
    }

    private static IDictionary _live;
    private static Dictionary<object, object> _baseline;
    private static readonly Dictionary<object, bool> LootKeys = new Dictionary<object, bool>();
    private static List<SkillField> _skillFields;
    private static float _next;
    private static int _reverted;
    private static int _meaningful; // loot counters and skill points; skill timers do not count

    public static void Begin(Profile profile)
    {
        try
        {
            _live = null;
            _baseline = null;
            _skillFields = null;
            _reverted = 0;
            _meaningful = 0;
            LootKeys.Clear();

            var stats = Member(profile, "EftStats");
            var session = stats == null ? null : Member(stats, "SessionCounters");
            _live = session == null ? null : Member(session, "_counters") as IDictionary;
            if (_live != null)
            {
                _baseline = new Dictionary<object, object>();
                foreach (DictionaryEntry entry in _live)
                {
                    _baseline[entry.Key] = entry.Value;
                }
            }

            var skills = Member(profile, "Skills");
            var list = skills == null ? null : Member(skills, "Skills") as IEnumerable;
            if (list != null)
            {
                _skillFields = new List<SkillField>();
                foreach (var skill in list)
                {
                    if (skill == null)
                    {
                        continue;
                    }

                    for (var t = skill.GetType(); t != null && t != typeof(object); t = t.BaseType)
                    {
                        foreach (var field in t.GetFields(Any | BindingFlags.DeclaredOnly))
                        {
                            var type = field.FieldType;
                            if (!field.IsStatic && (type.IsPrimitive || type == typeof(DateTime) || type == typeof(TimeSpan)))
                            {
                                _skillFields.Add(new SkillField { Target = skill, Field = field, Value = field.GetValue(skill) });
                            }
                        }
                    }
                }
            }

            Plugin.Log("StatsGuard: baseline of " + (_baseline?.Count ?? 0) + " counters and " +
                       (_skillFields?.Count ?? 0) + " skill values.");
        }
        catch (Exception ex)
        {
            Plugin.LogError("StatsGuard.Begin: " + ex);
        }
    }

    /// <summary>Called every frame; reverts at most every 0.2 s while a trade screen of ours is open.</summary>
    public static void Tick()
    {
        if (!LocalTrade.Active || Time.unscaledTime < _next)
        {
            return;
        }

        _next = Time.unscaledTime + 0.2f;
        var before = _meaningful;
        Revert();
        if (_meaningful != before)
        {
            NotificationGate.Open(1f); // something was counted: hide the pop-ups it causes
        }
    }

    public static void End()
    {
        Revert();
        Plugin.Log("StatsGuard: reverted " + _reverted + " value(s) during this trade.");
        _live = null;
        _baseline = null;
        _skillFields = null;
        LootKeys.Clear();
    }

    public static void Revert()
    {
        if (_live == null && _skillFields == null)
        {
            return;
        }

        try
        {
            if (_live != null && _baseline != null)
            {
                var added = new List<object>();
                foreach (DictionaryEntry entry in _live)
                {
                    if (!_baseline.ContainsKey(entry.Key) && IsLootKey(entry.Key))
                    {
                        added.Add(entry.Key);
                    }
                }

                foreach (var key in added)
                {
                    _live.Remove(key);
                    _reverted++;
                    _meaningful++;
                }

                foreach (var pair in _baseline)
                {
                    if (!IsLootKey(pair.Key))
                    {
                        continue;
                    }

                    if (!_live.Contains(pair.Key) || !Equals(_live[pair.Key], pair.Value))
                    {
                        _live[pair.Key] = pair.Value;
                        _reverted++;
                        _meaningful++;
                    }
                }
            }

            if (_skillFields != null)
            {
                foreach (var item in _skillFields)
                {
                    if (!Equals(item.Field.GetValue(item.Target), item.Value))
                    {
                        item.Field.SetValue(item.Target, item.Value);
                        _reverted++;
                        var name = item.Field.Name;
                        if (name != "_fatigueTimer" && item.Field.FieldType != typeof(DateTime))
                        {
                            _meaningful++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError("StatsGuard.Revert: " + ex);
        }
    }

    private static bool IsLootKey(object key)
    {
        if (!LootKeys.TryGetValue(key, out var result))
        {
            result = KeyText(key).IndexOf("Loot", StringComparison.OrdinalIgnoreCase) >= 0;
            LootKeys[key] = result;
        }

        return result;
    }

    private static string KeyText(object key)
    {
        if (key == null)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var field in key.GetType().GetFields(Any))
        {
            var value = field.GetValue(key);
            if (value == null)
            {
                continue;
            }

            if (value is IEnumerable e && !(value is string))
            {
                parts.Add(string.Join("/", e.Cast<object>().Select(x => x?.ToString())));
            }
            else
            {
                parts.Add(value.ToString());
            }
        }

        return string.Join("/", parts);
    }

    private static object Member(object target, string name)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var prop = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
            if (prop != null && prop.GetIndexParameters().Length == 0 && prop.GetGetMethod(true) != null)
            {
                return prop.GetGetMethod(true).Invoke(target, null);
            }

            var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return field.GetValue(target);
            }
        }

        return null;
    }
}
