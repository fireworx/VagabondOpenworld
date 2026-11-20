using System;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace OpenworldAddon;

/// <summary>
/// Only a finding aid for the fuel idea (nothing in the game is changed): on a key press it writes the objects around
/// the player whose name looks like a vehicle, and the carried fuel tanks and tubes, into the log.
/// </summary>
public static class VehicleScan
{
    private static readonly string[] Words =
    [
        "car", "truck", "bus", "van", "tractor", "uaz", "zil", "kamaz", "ural", "vehicle", "veh_", "auto", "moskvich", "lada",
        "jeep", "ambulance", "trailer", "minibus", "btr", "bmp", "tank", "lorry", "wreck", "pickup", "tow", "sedan", "taxi", "gaz"
    ];

    // words that only look like vehicles
    private static readonly string[] Skip = ["carpet", "cartridge", "carton", "scar", "card", "cargo", "carry", "stank", "tower", "town"];

    // Expeditionary fuel tank, Metal fuel tank, Silicone tube
    private static readonly string[] Wanted = ["5d1b371186f774253763a656", "5d1b36a186f7742523398433", "5d1b39a386f774252339976f"];

    public static void Scan(float radius)
    {
        try
        {
            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player == null)
            {
                return;
            }

            var origin = player.Transform.position;
            var text = new StringBuilder("Vehicle scan at " + origin + " (" + Singleton<GameWorld>.Instance.LocationId + "), radius " + radius + ":");
            Plugin.Log(text.ToString());

            var shown = 0;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || shown >= 250)
                {
                    continue;
                }

                var distance = Vector3.Distance(t.position, origin);
                if (distance > radius || !Looks(t.name))
                {
                    continue;
                }

                // only the top of a vehicle (the parent does not look like one), so the parts do not flood the log
                if (t.parent != null && Looks(t.parent.name))
                {
                    continue;
                }

                shown++;
                var components = string.Join(",", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).Take(10));
                Plugin.Log("  " + Path(t) + " | " + Math.Round(distance, 1) + " m | pos " + t.position + " | active " + t.gameObject.activeInHierarchy
                           + " | layer " + LayerMask.LayerToName(t.gameObject.layer) + " | children " + t.childCount + " | " + components);
            }

            Plugin.Log("Vehicle scan: " + shown + " object(s).");

            foreach (var item in LocalTrade.CarriedItems(player))
            {
                var tpl = item.TemplateId.ToString();
                if (Wanted.Contains(tpl))
                {
                    var resource = item.GetItemComponent<ResourceComponent>();
                    Plugin.Log("  carried " + tpl + " | " + item.ShortName + " | resource " + (resource != null ? resource.Value + "/" + resource.MaxResource : "none"));
                }
            }
        }
        catch (Exception e)
        {
            Plugin.LogError("Vehicle scan failed: " + e);
        }
    }

    private static bool Looks(string name)
    {
        var lower = (name ?? "").ToLowerInvariant();
        return !Skip.Any(lower.Contains) && Words.Any(lower.Contains);
    }

    private static string Path(Transform t)
    {
        var path = t.name;
        for (var i = 0; i < 3 && t.parent != null; i++)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }

        return path;
    }
}
