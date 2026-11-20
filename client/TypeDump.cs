using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace OpenworldAddon;

/// <summary>
/// Writes the game's menu and hideout class names with their methods into VagabondOpenworld.types.txt (only while the
/// file log is on). It is only a list for finding the right place to hook, nothing in the game is changed.
/// </summary>
public static class TypeDump
{
    private static readonly string[] Words = ["QuestsScreen", "QuestController", "Handover", "HandOver", "QuestView", "QuestObjective", "QuestCondition", "ConditionItem", "QuestItem", "QuestProgress"];

    public static void Write(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "VagabondOpenworld.types5.txt");
            if (File.Exists(path))
            {
                return; // once is enough
            }

            // the game's classes and Vagabond's own
            var types = new System.Collections.Generic.List<Type>();
            foreach (var assembly in new[] { typeof(EFT.Player).Assembly, typeof(Vagabond.Client.Vagabond).Assembly })
            {
                try
                {
                    types.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException e)
                {
                    types.AddRange(e.Types.Where(x => x != null));
                }
            }

            var text = new StringBuilder();
            foreach (var type in types.Where(t => Words.Any(w => t.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(t => t.FullName))
            {
                text.AppendLine((type.IsEnum ? "enum " : type.IsInterface ? "interface " : "class ") + type.FullName
                    + (type.BaseType != null ? " : " + type.BaseType.Name : ""));
                if (type.IsEnum)
                {
                    text.AppendLine("    " + string.Join(", ", Enum.GetNames(type)));
                    continue;
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                             .Where(m => !m.IsSpecialName).Take(60))
                {
                    text.AppendLine("    " + method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Take(40))
                {
                    text.AppendLine("    field " + field.FieldType.Name + " " + field.Name);
                }
            }

            File.WriteAllText(path, text.ToString());
            Plugin.Log("Wrote " + path);
        }
        catch (Exception e)
        {
            Plugin.LogError("Type list failed: " + e.Message);
        }
    }
}
