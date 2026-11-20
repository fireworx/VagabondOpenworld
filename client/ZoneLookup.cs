using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using SPT.Common.Http;
using Vagabond.Common.Data;
using Vagabond.Common.Definitions;
using Vagabond.Common.Enums;
using VagabondPlugin = Vagabond.Client.Vagabond;

namespace OpenworldAddon;

/// <summary>Maps an exfil in the scene to Vagabond's custom exfil definition and its traders.</summary>
public static class ZoneLookup
{
    private static Dictionary<string, List<string>> _traderMap;
    private static float _nextFetch;
    private static bool _fence;

    /// <summary>True when Vagabond's setting puts Fence at the player's hideout (asked from our server together with the map).</summary>
    public static bool FenceAtHideout
    {
        get
        {
            GetTraderMap();
            return _fence;
        }
    }

    /// <summary>exfil identifier -> trader ids, from our server mod (Vagabond's public API).</summary>
    private static Dictionary<string, List<string>> GetTraderMap()
    {
        if (_traderMap != null && _traderMap.Count > 0)
        {
            return _traderMap;
        }

        if (UnityEngine.Time.realtimeSinceStartup < _nextFetch)
        {
            return _traderMap;
        }

        _nextFetch = UnityEngine.Time.realtimeSinceStartup + 5f;
        try
        {
            var json = RequestHandler.PostJson("/vagabond-openworld/map", "{}");
            var map = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            Plugin.PushRules();
            try
            {
                _fence = (bool?)Newtonsoft.Json.Linq.JObject.Parse(RequestHandler.PostJson("/vagabond-openworld/fence", "{}"))["Enabled"] ?? false;
            }
            catch (Exception fenceError)
            {
                Plugin.LogError("Could not ask for Fence at the hideout: " + fenceError.Message);
            }

            if (Plugin.FileLogEnabled)
            {
                try
                {
                    // the client log is on: switch the server's log on too and learn where it writes
                    Plugin.Log("Server log: " + RequestHandler.PostJson("/vagabond-openworld/log", "{}"));
                }
                catch (Exception ex)
                {
                    Plugin.LogError("Could not switch the server log on: " + ex.Message);
                }
            }

            _traderMap = map == null
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Could not fetch the trader map (is the server mod installed?): {ex.Message}");
        }

        return _traderMap;
    }

    public static bool TryGetCustomExfil(ExitTriggerSettings settings, out CustomExfil definition)
    {
        definition = null;
        if (string.IsNullOrWhiteSpace(settings?.Name))
        {
            return false;
        }

        var locationId = Singleton<GameWorld>.Instance?.LocationId;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return false;
        }

        var raid = VagabondLocations.NormaliseMapName(locationId);
        var all = VagabondPlugin.State?.CustomExfils;
        if (raid == RaidLocation.Nil || all == null ||
            !all.TryGetValue(raid, out var mapExfils) ||
            !mapExfils.TryGetValue(locationId, out var definitions) ||
            definitions == null)
        {
            return false;
        }

        definition = definitions.FirstOrDefault(def =>
            !def.IsTransit &&
            string.Equals(def.DisplayName, settings.Name, StringComparison.OrdinalIgnoreCase));
        return definition != null;
    }

    public static bool TryGetTraderExfil(ExitTriggerSettings settings, out List<string> traderIds)
    {
        traderIds = null;
        if (!TryGetCustomExfil(settings, out var match))
        {
            return false;
        }

        var map = GetTraderMap();
        if (map == null || !map.TryGetValue(match.Identifier, out var ids) || ids == null || ids.Count == 0)
        {
            return false;
        }

        traderIds = ids;
        return true;
    }
}
