using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace OpenworldAddon;

/// <summary>
/// Wishlist Extended counts the items in the stash and on the player from a cache that it only throws away when the game
/// reports an item change. Our menus change items through the server (mail, stash window, stations, crafts, quests,
/// delivery), which the game does not report, so the counts went stale. After every such request this tells
/// Wishlist Extended (when it is installed) to re-read the stash counts. Nothing is referenced at build time, it is all by name.
/// </summary>
public static class WishlistCompat
{
    private static readonly string[] Routes =
    [
        "/mail/send", "/mail/taken", "/stash/take", "/stash/remove", "/generator/load", "/station/", "/hideout/craft", "/hideout/collect",
        "/hideout/build", "/quest/rewards", "/quest/started", "/trade"
    ];

    private static volatile bool _pending;
    private static bool _searched;
    private static MethodInfo _invalidateStash;

    public static bool Installed => Find();

    public static void Request()
    {
        _pending = true;
    }

    /// <summary>Called every frame from the plugin; does the rebuild on the main thread.</summary>
    public static void Tick()
    {
        if (!_pending)
        {
            return;
        }

        _pending = false;
        if (!Find())
        {
            return;
        }

        try
        {
            _invalidateStash?.Invoke(null, null);
            Plugin.Trace("Wishlist Extended told to re-read the stash counts.");
        }
        catch (Exception e)
        {
            Plugin.Trace("Wishlist Extended refresh failed: " + (e.InnerException ?? e).Message);
        }
    }

    private static bool Find()
    {
        if (_searched)
        {
            return _invalidateStash != null;
        }

        _searched = true;
        try
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.IndexOf("WishlistExtended", StringComparison.OrdinalIgnoreCase) >= 0)
                .SelectMany(a =>
                {
                    try
                    {
                        return a.GetTypes();
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        return e.Types.Where(t => t != null).ToArray();
                    }
                })
                .ToList();

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _invalidateStash = types.FirstOrDefault(t => t.FullName == "WishlistExtended.Features.StashService")?.GetMethod("InvalidateCache", all);
            Plugin.Log(_invalidateStash != null ? "Wishlist Extended found, its counts are refreshed after our changes." : "Wishlist Extended not found.");
        }
        catch (Exception e)
        {
            Plugin.Trace("Wishlist Extended lookup failed: " + e.Message);
        }

        return _invalidateStash != null;
    }

    /// <summary>Hooks the add-on's own requests: when one that changes items is done, a refresh is asked for.</summary>
    internal class RequestPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(SPT.Common.Http.RequestHandler), "PostJsonAsync");
        }

        [PatchPostfix]
        private static void Postfix(string path, ref Task<string> __result)
        {
            try
            {
                if (path == null || !path.StartsWith("/vagabond-openworld/") || !Routes.Any(r => path.Contains(r)) || __result == null)
                {
                    return;
                }

                __result.ContinueWith(_ => Request(), TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (Exception)
            {
                // a compatibility hook must never break a request
            }
        }
    }
}
