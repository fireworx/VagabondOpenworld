using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;

namespace OpenworldAddon;

/// <summary>
/// The "fill level" of an item as the game shows it in the corner of the item picture: 60/60 for water,
/// 3/3 for a medical item, durability, bullets in a magazine. Read by name from the item's components,
/// so a different game version only loses the number, never the window.
/// </summary>
public static class ItemFill
{
    private static readonly string[][] Pairs =
    {
        new[] { "HpResource", "MaxHpResource" },
        new[] { "HpPercent", "MaxResource" },
        new[] { "Value", "MaxResource" },
        new[] { "Durability", "MaxDurability" },
    };

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly System.Collections.Generic.HashSet<string> Logged = new System.Collections.Generic.HashSet<string>();

    /// <summary>One log line per item kind, so the log shows what was found (or why not) without flooding.</summary>
    private static void Note(Item item, string text)
    {
        try
        {
            if (Logged.Add(item.TemplateId))
            {
                Plugin.Trace("ItemFill v3: " + item.ShortName.Localized() + " [" + item.GetType().Name + "] " + text);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool Number(object target, string name, out double value)
    {
        value = 0;
        try
        {
            var type = target.GetType();
            var property = type.GetProperty(name, Any);
            var raw = property != null && property.GetIndexParameters().Length == 0
                ? property.GetValue(target, null)
                : type.GetField(name, Any)?.GetValue(target);
            if (raw == null || raw is string || raw is bool)
            {
                return false;
            }

            value = Convert.ToDouble(raw);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static object Get(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, Any);
        return property != null && property.GetIndexParameters().Length == 0 ? property.GetValue(target, null) : type.GetField(name, Any)?.GetValue(target);
    }

    private static System.Collections.Generic.Dictionary<string, double> Numbers(object component)
    {
        var result = new System.Collections.Generic.Dictionary<string, double>();
        var type = component.GetType();
        foreach (var property in type.GetProperties(Any))
        {
            if (property.GetIndexParameters().Length == 0 && Number(component, property.Name, out var value))
            {
                result[property.Name] = value;
            }
        }

        foreach (var field in type.GetFields(Any))
        {
            var name = field.Name.TrimStart('_');
            if (!result.ContainsKey(name) && Number(component, field.Name, out var value))
            {
                result[name] = value;
            }
        }

        return result;
    }

    /// <summary>The current and the full amount of the item, if it has one.</summary>
    public static bool Read(Item item, out double current, out double max)
    {
        current = max = 0;
        if (item == null)
        {
            return false;
        }

        try
        {
            if (item.GetType().Name.IndexOf("Magazine", StringComparison.OrdinalIgnoreCase) >= 0
                && Number(item, "Count", out current) && Number(item, "MaxCount", out max) && max > 0)
            {
                Note(item, "magazine " + current + "/" + max);
                return true;
            }

            var components = Get(item, "Components") as IEnumerable;
            if (components == null)
            {
                Note(item, "no Components member; members: " + string.Join(",", item.GetType().GetMembers(Any).Select(m => m.Name).Distinct().Take(60).ToArray()));
                return false;
            }

            var seen = new System.Text.StringBuilder();
            foreach (var component in components)
            {
                if (component == null)
                {
                    continue;
                }

                // first the known pairs, then any "X" next to a "MaxX"
                foreach (var pair in Pairs)
                {
                    if (Number(component, pair[0], out current) && Number(component, pair[1], out max) && max > 1)
                    {
                        Note(item, component.GetType().Name + "." + pair[0] + "/" + pair[1] + " = " + current + "/" + max);
                        return true;
                    }
                }

                var numbers = Numbers(component);
                foreach (var entry in numbers)
                {
                    if (entry.Key.StartsWith("Max", StringComparison.Ordinal) && entry.Key.Length > 3
                        && numbers.TryGetValue(entry.Key.Substring(3), out current) && entry.Value > 1)
                    {
                        max = entry.Value;
                        Note(item, component.GetType().Name + "." + entry.Key + " = " + current + "/" + max);
                        return true;
                    }
                }

                seen.Append(component.GetType().Name).Append('{').Append(string.Join(",", numbers.Select(x => x.Key + "=" + x.Value).ToArray())).Append("} ");
            }

            Note(item, "no fill level found; components: " + seen);
        }
        catch (Exception e)
        {
            Plugin.Trace("ItemFill failed: " + e.Message);
        }

        current = max = 0;
        return false;
    }

    private static string Show(double current, double max)
    {
        return Math.Round(current).ToString("0") + "/" + Math.Round(max).ToString("0");
    }

    /// <summary>"  60/60" for an item the player carries, or an empty text.</summary>
    public static string Text(Item item)
    {
        return Read(item, out var current, out var max) ? "  " + Show(current, max) : "";
    }

    /// <summary>For an item made from its template (full): the given current amount against its full amount.</summary>
    public static string Text(Item fresh, double current)
    {
        return Read(fresh, out _, out var max) ? "  " + Show(current, max) : "";
    }
}
