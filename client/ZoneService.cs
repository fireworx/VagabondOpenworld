using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Achievements;
using EFT.Quests;
using EFT.UI;
using EFT.UI.Insurance;
using EFT.UI.Screens;
using SPT.Reflection.Utils;
using UnityEngine.SceneManagement;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.Interactive;
using UnityEngine;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace OpenworldAddon;

/// <summary>
/// Trader exfils no longer start an extraction. Standing inside the zone shows an
/// interaction prompt instead ("Open &lt;trader&gt;"), the raid keeps running.
/// </summary>
public static class ZoneService
{
    // identifier prefix the server gives hideout exfils (HideoutService.HideoutIdPrefix)
    private const string HideoutIdPrefix = "VGB_HO_";

    private enum ZoneKind
    {
        None,
        Trader,
        Hideout
    }

    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static ExfiltrationPoint _currentPoint;
    private static Collider _currentCollider;
    private static ZoneKind _currentKind;
    private static List<string> _currentTraderIds;

    // set while we deliberately let the vanilla extraction start ("Leave to hideout")
    private static ExfiltrationPoint _allowExtractPoint;

    // the interaction object we put into the owner's AvailableInteractionState
    private static object _promptValue;
    private static float _nextPromptRefresh;

    public static bool IsInTraderZone => _currentPoint != null;

    /// <summary>
    /// Called from the exfil trigger-enter prefix. Returns true if the original method must be skipped
    /// (i.e. no extraction countdown should start).
    /// </summary>
    public static bool TryEnter(ExfiltrationPoint point, Collider collider)
    {
        if (Plugin.IsHeadless() || point == null || collider == null)
        {
            return false;
        }

        var gameWorld = Singleton<GameWorld>.Instance;
        var player = gameWorld?.GetPlayerByCollider(collider);
        var mainPlayer = gameWorld?.MainPlayer;
        if (player == null || mainPlayer == null || !ReferenceEquals(player, mainPlayer))
        {
            return false;
        }

        // the player picked "Leave to hideout": let the vanilla extraction start
        if (ReferenceEquals(_allowExtractPoint, point))
        {
            return false;
        }

        var kind = ZoneKind.None;
        List<string> traderIds = null;

        if (Plugin.TraderZoneEnabled &&
            ZoneLookup.TryGetTraderExfil(point.Settings, out traderIds))
        {
            kind = ZoneKind.Trader;
        }
        else if (Plugin.HideoutZoneEnabled &&
                 ZoneLookup.TryGetCustomExfil(point.Settings, out var definition) &&
                 definition.Identifier != null &&
                 definition.Identifier.StartsWith(HideoutIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = ZoneKind.Hideout;
        }

        if (kind == ZoneKind.None)
        {
            return false;
        }

        if (!ReferenceEquals(_currentPoint, point))
        {
            _promptValue = null; // different zone -> rebuild the prompt
        }

        if (!ReferenceEquals(_currentPoint, point))
        {
            _promptBroken = false;
        }

        _currentPoint = point;
        _currentCollider = collider;
        _currentKind = kind;
        _currentTraderIds = traderIds;
        _nextPromptRefresh = 0f;
        ApplyPrompt(force: true);
        return true;
    }

    public static void Exit(ExfiltrationPoint point, Collider collider)
    {
        if (_currentPoint == null || !ReferenceEquals(point, _currentPoint))
        {
            return;
        }

        var gameWorld = Singleton<GameWorld>.Instance;
        var player = gameWorld?.GetPlayerByCollider(collider);
        var mainPlayer = gameWorld?.MainPlayer;
        if (player == null || mainPlayer == null || !ReferenceEquals(player, mainPlayer))
        {
            return;
        }

        Leave();
    }

    /// <summary>Called from Plugin.Update: keeps the prompt alive while the game rewrites the interaction state.</summary>
    private static bool _scenePreloaded;

    public static void Tick()
    {
        MessagesPanel.Tick();
        if (_unityLogUntil > 0f && Time.realtimeSinceStartup > _unityLogUntil)
        {
            _unityLogUntil = 0f;
            Application.logMessageReceived -= OnUnityLog;
        }

        var inRaid = Plugin.IsInRaid() && Singleton<GameWorld>.Instance?.MainPlayer != null;
        if (!inRaid)
        {
            _scenePreloaded = false;
        }
        else if (!_scenePreloaded && Plugin.TraderZoneEnabled && !MonoBehaviourSingleton<MenuUI>.Instantiated)
        {
            // the trader screens live in the menu scene: load it once per raid, like InRaidTraders does
            _scenePreloaded = true;
            Plugin.Log("Preloading the menu UI scene.");
            TryLoadMenuUiScene();
        }

        if (_currentPoint == null)
        {
            return;
        }

        if (!Plugin.IsInRaid() || Singleton<GameWorld>.Instance?.MainPlayer == null)
        {
            Leave();
            return;
        }

        if (Time.unscaledTime < _nextPromptRefresh)
        {
            return;
        }

        _nextPromptRefresh = Time.unscaledTime + 0.1f;
        ApplyPrompt(force: false);
    }

    private static void Leave()
    {
        ClearPrompt();
        TakeBackStash();
        _currentPoint = null;
        _currentCollider = null;
        _currentKind = ZoneKind.None;
        _currentTraderIds = null;
        _promptValue = null;
        _promptBroken = false;
        MessagesPanel.Close();
    }

    // "Read messages": opens the messages window.
    private static async void ShowMessages()
    {
        var entries = await Mail.Fetch();
        if (entries == null)
        {
            return;
        }

        if (entries.Count == 0)
        {
            NotificationGate.Message("No items are waiting in your messages.");
            return;
        }

        var groups = new List<MessagesPanel.Group>();
        foreach (var msg in entries)
        {
            var group = groups.FirstOrDefault(g => g.Name == msg.Sender);
            if (group == null)
            {
                group = new MessagesPanel.Group { Name = msg.Sender };
                groups.Add(group);
            }

            foreach (var entry in msg.Entries)
            {
                var e = entry;
                group.Rows.Add(new MessagesPanel.Row
                {
                    Item = e.Root,
                    Label = e.Label,
                    Note = msg.Text,
                    Act = () => Mail.Take(e)
                });
            }
        }

        MessagesPanel.Open("MESSAGES", "FROM", "TAKE", groups);
    }

    // ------------------------------------------------------------------------------------------
    // Step 4: open the trader screen in raid.
    // ------------------------------------------------------------------------------------------
    private static Inventory _stashLent;

    private static void TakeBackStash()
    {
        if (_stashLent == null)
        {
            return;
        }

        try
        {
            _stashLent.Stash = null;
            Plugin.Log("Took the lent stash back.");
        }
        catch (Exception ex)
        {
            Plugin.LogError("Could not take back the lent stash: " + ex.Message);
        }

        _stashLent = null;
    }

    private static bool _traderScreenOpen;
    private sealed class RaisedCanvas
    {
        public Canvas Canvas;
        public bool Override;
        public int Order;
    }

    private static readonly List<RaisedCanvas> _raised = new List<RaisedCanvas>();
    private static bool _menuRaised;
    private static int _openFrames;
    private static CanvasGroup _hudBreakout;

    /// <summary>
    /// The raid's HUD is drawn over the trader screen. While the screen is open its canvases are put in
    /// front of everything, and the HUD of the HudBreakout mod (when installed) is made invisible.
    /// </summary>
    private static void RaiseMenu(bool on)
    {
        try
        {
            if (on)
            {
                if (!_menuRaised)
                {
                    var ui = MonoBehaviourSingleton<MenuUI>.Instantiated ? MonoBehaviourSingleton<MenuUI>.Instance : null;
                    var start = ui?.TraderScreensGroup != null ? ui.TraderScreensGroup.transform : ui?.transform;
                    if (start == null)
                    {
                        return;
                    }

                    _raised.Clear();
                    foreach (var canvas in start.GetComponentsInParent<Canvas>(true))
                    {
                        _raised.Add(new RaisedCanvas { Canvas = canvas, Override = canvas.overrideSorting, Order = canvas.sortingOrder });
                    }

                    _menuRaised = true;
                    Plugin.Log("Trader screen: raising " + string.Join(", ", _raised.Select(r =>
                        r.Canvas.name + " (order " + r.Order + ", " + r.Canvas.renderMode + ", override " + r.Override + ")").ToArray()));
                }

                foreach (var raised in _raised)
                {
                    if (raised.Canvas != null)
                    {
                        raised.Canvas.overrideSorting = true;
                        raised.Canvas.sortingOrder = 500;
                    }
                }

                HideHudBreakout();
            }
            else if (_menuRaised)
            {
                _menuRaised = false;
                foreach (var raised in _raised)
                {
                    if (raised.Canvas != null)
                    {
                        raised.Canvas.overrideSorting = raised.Override;
                        raised.Canvas.sortingOrder = raised.Order;
                    }
                }

                _raised.Clear();
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError("RaiseMenu: " + ex.Message);
            _menuRaised = false;
        }
    }

    /// <summary>
    /// HudBreakout (https://github.com/Niharkanta1/SPT.HudBreakout) sets its transparency every frame in
    /// Update; setting it again in LateUpdate wins, so the HUD is not drawn while a trader screen is open.
    /// </summary>
    private static void HideHudBreakout()
    {
        if (_hudBreakout == null)
        {
            foreach (var group in Resources.FindObjectsOfTypeAll<CanvasGroup>())
            {
                if (group != null && group.gameObject.name == "HudBreakout" && group.gameObject.scene.IsValid())
                {
                    _hudBreakout = group;
                    break;
                }
            }
        }

        if (_hudBreakout != null)
        {
            _hudBreakout.alpha = 0f;
        }
    }

    /// <summary>
    /// While our trader screen is open: keep the mouse cursor visible and free (the raid hides and locks it).
    /// Called from Plugin.LateUpdate.
    /// </summary>
    public static void LateTick()
    {
        StatsGuard.Tick();
        MessagesPanel.LateTick();

        if (!_traderScreenOpen)
        {
            RaiseMenu(false);
            return;
        }

        RaiseMenu(true);

        var group = MonoBehaviourSingleton<MenuUI>.Instantiated
            ? MonoBehaviourSingleton<MenuUI>.Instance.TraderScreensGroup
            : null;
        _openFrames++;
        if (_openFrames > 10 && (group == null || !group.gameObject.activeInHierarchy))
        {
            _traderScreenOpen = false;
            _openFrames = 0;
            _unityLogUntil = Time.realtimeSinceStartup + 5f;
            return;
        }

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }

    private static bool _openingTrader;
    private static float _unityLogUntil;

    private static void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
        {
            Plugin.LogError("[Unity " + type + "] " + condition + "\n" + stackTrace);
        }
    }


    private static void OpenTrader(string traderId)
    {
        if (_openingTrader)
        {
            return;
        }

        Plugin.Instance.StartCoroutine(OpenTraderRoutine(traderId));
    }

    private static IEnumerator OpenTraderRoutine(string traderId)
    {
        _openingTrader = true;
        try
        {
            // 1. The trader screens live in the menu UI scene, which is not loaded in a raid.
            if (!MonoBehaviourSingleton<MenuUI>.Instantiated)
            {
                // already requested at raid start? then only wait, do not load a second copy
                if (!_scenePreloaded)
                {
                    _scenePreloaded = true;
                    if (!TryLoadMenuUiScene())
                    {
                        NotificationGate.Warn("Could not load the trader UI.");
                        yield break;
                    }
                }

                var deadline = Time.realtimeSinceStartup + 30f;
                while (!MonoBehaviourSingleton<MenuUI>.Instantiated && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (!MonoBehaviourSingleton<MenuUI>.Instantiated)
                {
                    var loaded = new List<string>();
                    for (var i = 0; i < SceneManager.sceneCount; i++)
                    {
                        loaded.Add(SceneManager.GetSceneAt(i).name);
                    }

                    Plugin.LogError("MenuUI did not appear after loading the menu UI scene. Loaded scenes: " +
                                    string.Join(", ", loaded) + "; MenuUI objects: " +
                                    Resources.FindObjectsOfTypeAll<MenuUI>().Length);
                    NotificationGate.Warn("Could not load the trader UI.");
                    yield break;
                }

                // let the screens register themselves
                yield return null;
            }

            // BepInEx does not mirror Unity's own log here, so forward errors while the screen opens
            if (Plugin.DebugUnityErrors)
            {
                Application.logMessageReceived -= OnUnityLog;
                Application.logMessageReceived += OnUnityLog;
                _unityLogUntil = Time.realtimeSinceStartup + 15f;
            }

            UnlockTrader(traderId);
            ShowTraderScreen(traderId);

            for (var i = 0; i < 5; i++)
            {
                yield return null;
            }

            _traderScreenOpen = true;
            _openFrames = 0;
            if (Plugin.DebugUnityErrors)
            {
                _unityLogUntil = Time.realtimeSinceStartup + 900f; // keep forwarding errors while the screen is open
            }

            try
            {
                var group = MonoBehaviourSingleton<MenuUI>.Instance?.TraderScreensGroup;
                Plugin.Log("Trader screen state: group=" + (group == null ? "null" : group.name) +
                           ", activeSelf=" + (group != null && group.gameObject.activeSelf) +
                           ", activeInHierarchy=" + (group != null && group.gameObject.activeInHierarchy));
            }
            catch (Exception ex)
            {
                Plugin.LogError("Trader screen diagnostics failed: " + ex.Message);
            }
        }
        finally
        {
            _openingTrader = false;
        }
    }

    private class UnlockResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Vagabond disables every trader in a raid except the exit's own one. Ask our server mod to enable
    /// this trader in the profile, then mirror it on the client-side trader object.
    /// </summary>
    private static void UnlockTrader(string traderId)
    {
        try
        {
            var json = RequestHandler.PostJson("/vagabond-openworld/unlock",
                JsonConvert.SerializeObject(new { TraderId = traderId }));
            var resp = JsonConvert.DeserializeObject<UnlockResponse>(json);
            if (resp == null || !resp.Ok)
            {
                Plugin.LogError($"Trader unlock refused: {resp?.Error}");
                return;
            }

            var session = ClientAppUtils.GetClientApp()?.Session;
            var trader = session?.Traders?.FirstOrDefault(t => t != null && t.Id == traderId);
            var info = trader?.Info;
            if (info == null)
            {
                return;
            }

            info.SetDisabled(false);

            foreach (var name in new[] { "Unlocked" })
            {
                var member = FindMember(info.GetType(), name);
                if (member != null && MemberType(member) == typeof(bool))
                {
                    SetValue(member, info, true);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Trader unlock failed: {ex}");
        }
    }

    private static void ShowTraderScreen(string traderId)
    {
        try
        {
            var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            var session = ClientAppUtils.GetClientApp()?.Session;
            if (mainPlayer == null || session == null)
            {
                NotificationGate.Warn("Trader screen: no player or session.");
                return;
            }

            var traders = session.Traders?.Where(t => t != null && t.Info != null).ToList();
            var trader = traders?.FirstOrDefault(t => string.Equals(t.Id, traderId, StringComparison.Ordinal));
            if (trader == null)
            {
                Plugin.LogError($"Trader {traderId} not found in the session.");
                NotificationGate.Warn($"{TraderName(traderId)} is not available.");
                return;
            }

            if (!trader.Info.Available)
            {
                NotificationGate.Warn(
                    $"{TraderName(traderId)} is not unlocked yet.");
                return;
            }

            // The trade screen is built around one stash grid. In a raid there is none, so lend it a stand-in
            // whose grid lists what the player carries; trades are settled locally (see LocalTrade).
            StatsGuard.Begin(mainPlayer.Profile); // before the stand-in stash lists anything
            NotificationGate.Open(3f);
            var inventory = mainPlayer.Profile.Inventory;
            var lentStash = false;
            if (inventory.Stash == null)
            {
                inventory.Stash = LocalTrade.BuildPackStash(mainPlayer);
                lentStash = true;
            }

            LocalTrade.Active = true;

            // Vagabond keeps traders disabled in a raid; standing at the trader counts, so enable while open.
            var wasDisabled = false;
            if (trader.Info != null)
            {
                wasDisabled = trader.Info.Disabled;
                trader.Info.SetDisabled(false);
            }

            var shownTraders = traders.Where(t => ReferenceEquals(t, trader)).ToList();
            var controller = new TraderScreensGroup.TraderScreenController(
                trader,
                shownTraders,
                mainPlayer.Profile,
                mainPlayer.InventoryController,
                mainPlayer.HealthController,
                FindByType<QuestController>(mainPlayer, session),
                FindByType<AchievementsController>(mainPlayer, session),
                session);
            controller.OnClose += () =>
            {
                LocalTrade.Active = false;
                StatsGuard.End();
                LocalTrade.ForgetPackView();
                if (lentStash)
                {
                    inventory.Stash = null; // keep stash items out of the end-of-raid save
                }

                if (trader.Info != null && wasDisabled)
                {
                    trader.Info.SetDisabled(true);
                }

                Plugin.Log("Closed trader screen.");
            };
            controller.ShowScreen(EScreenState.Queued);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to open the trader screen: {ex}");
            NotificationGate.Warn("Could not open the trader screen.");
        }
    }

    private static T FindByType<T>(params object[] sources) where T : class
    {
        foreach (var source in sources)
        {
            if (source == null)
            {
                continue;
            }

            for (var type = source.GetType(); type != null; type = type.BaseType)
            {
                foreach (var prop in type.GetProperties(AnyInstance | BindingFlags.DeclaredOnly))
                {
                    if (prop.GetIndexParameters().Length == 0 && typeof(T).IsAssignableFrom(prop.PropertyType))
                    {
                        try
                        {
                            if (prop.GetValue(source, null) is T value)
                            {
                                return value;
                            }
                        }
                        catch
                        {
                            // ignore getters that throw
                        }
                    }
                }

                foreach (var field in type.GetFields(AnyInstance | BindingFlags.DeclaredOnly))
                {
                    if (typeof(T).IsAssignableFrom(field.FieldType) && field.GetValue(source) is T value)
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Loads the menu UI scene additively, the same thing InRaidTraders does with the obfuscated
    /// class that holds the scene key. Found by name through reflection so it survives renames.
    /// </summary>
    // The class holding the assets manager is obfuscated and renamed between builds: find it by shape.
    private static object FindAssetsManager()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var type in typeof(MenuUI).Assembly.GetTypes())
        {
            try
            {
                var member = (MemberInfo)type.GetProperty("Manager", flags) ?? type.GetField("Manager", flags);
                if (member == null)
                {
                    continue;
                }

                var value = member is PropertyInfo p ? p.GetValue(null, null) : ((FieldInfo)member).GetValue(null);
                if (value != null && value.GetType().GetMethods(AnyInstance).Any(m => m.Name == "LoadScene"))
                {
                    return value;
                }
            }
            catch
            {
                // ignore types whose static init or getter throws
            }
        }

        return null;
    }

    /// <summary>Reads 'path' / 'rcid' from the (obfuscated) ResourceKey; falls back to parsing ToString().</summary>
    private static string KeyPart(object key, string name)
    {
        var member = FindMember(key.GetType(), name);
        if (member != null && MemberType(member) == typeof(string))
        {
            return (string)GetValue(member, key);
        }

        var match = System.Text.RegularExpressions.Regex.Match(key.ToString(), name + "='([^']*)'");
        return match.Success ? match.Groups[1].Value : key.ToString();
    }

    private static bool TryLoadMenuUiScene()
    {
        try
        {
            object sceneKey = null;
            foreach (var type in typeof(MenuUI).Assembly.GetTypes())
            {
                var member = (MemberInfo)type.GetField("MenuUIScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? type.GetProperty("MenuUIScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (member == null)
                {
                    continue;
                }

                sceneKey = member is FieldInfo f ? f.GetValue(null) : ((PropertyInfo)member).GetValue(null, null);
                if (sceneKey != null)
                {
                    break;
                }
            }

            if (sceneKey == null)
            {
                Plugin.LogError("Could not find the 'MenuUIScene' key in Assembly-CSharp.");
                return false;
            }

            var manager = FindAssetsManager();
            if (manager == null)
            {
                Plugin.LogError("Could not find the assets manager (static 'Manager' with LoadScene).");
                return false;
            }

            var candidates = manager.GetType().GetMethods(AnyInstance)
                .Where(m => m.Name == "LoadScene" &&
                            m.GetParameters().Any(x => x.ParameterType == typeof(LoadSceneMode)))
                .ToList();

            foreach (var load in candidates)
            {
                var parameters = load.GetParameters();
                var args = new object[parameters.Length];
                var keyPlaced = false;
                var stringIndex = 0;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var type = parameters[i].ParameterType;
                    if (type == typeof(LoadSceneMode))
                    {
                        args[i] = LoadSceneMode.Additive;
                    }
                    else if (!keyPlaced && type.IsInstanceOfType(sceneKey))
                    {
                        args[i] = sceneKey;
                        keyPlaced = true;
                    }
                    else if (type == typeof(string))
                    {
                        // LoadScene(string path, string rcid, ...): take both from the resource key
                        args[i] = KeyPart(sceneKey, stringIndex == 0 ? "path" : "rcid");
                        stringIndex++;
                        keyPlaced = true;
                    }
                    else if (type == typeof(bool))
                    {
                        args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : true;
                    }
                    else if (type.IsValueType && !parameters[i].HasDefaultValue)
                    {
                        args[i] = Activator.CreateInstance(type);
                    }
                    else
                    {
                        args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                    }
                }

                if (!keyPlaced)
                {
                    continue;
                }

                Plugin.Log("LoadScene args: " + string.Join(", ", args.Select(x => x == null ? "null" : x.ToString())));
                Plugin.Log("LoadScene via " + load.ReturnType.Name + " " + load.Name + "(" +
                           string.Join(", ", parameters.Select(x => x.ParameterType.Name)) + ") key=" + sceneKey);
                var result = load.Invoke(manager, args);
                if (result is System.Threading.Tasks.Task task)
                {
                    task.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            Plugin.LogError("LoadScene task failed: " + t.Exception);
                        }
                        else
                        {
                            Plugin.Log("LoadScene task finished: " + t.Status);
                        }
                    });
                }

                return true;
            }

            Plugin.LogError("No usable LoadScene overload. sceneKey type: " + sceneKey.GetType().FullName +
                            " (" + sceneKey + "); manager type: " + manager.GetType().FullName +
                            "; LoadScene-like methods: " + string.Join(" | ",
                                manager.GetType().GetMethods(AnyInstance)
                                    .Where(m => m.Name.IndexOf("Scene", StringComparison.OrdinalIgnoreCase) >= 0)
                                    .Select(m => m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ",
                                        m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")")));
            return false;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to load the menu UI scene: {ex}");
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Hideout zone menu
    // ------------------------------------------------------------------------------------------
    // "Send items to hideout": a window with what is carried, by container.
    private static void SendItemsToHideout()
    {
        try
        {
            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player == null)
            {
                NotificationGate.Warn("Send items: no player.");
                return;
            }

            var items = LocalTrade.SendableItems(player);
            if (items.Count == 0)
            {
                NotificationGate.Message("You carry nothing that can be sent.");
                return;
            }

            var groups = new List<MessagesPanel.Group>();
            foreach (var (item, container) in items)
            {
                var group = groups.FirstOrDefault(g => g.Name == container);
                if (group == null)
                {
                    group = new MessagesPanel.Group { Name = container };
                    groups.Add(group);
                }

                var it = item;
                var label = it.ShortName.Localized() + (it.StackObjectsCount > 1 ? " x" + it.StackObjectsCount : "");
                group.Rows.Add(new MessagesPanel.Row { Item = it, Label = label, Act = () => Mail.Send(it, label) });
            }

            MessagesPanel.Open("SEND TO HIDEOUT", "CARRIED IN", "SEND", groups);
        }
        catch (Exception ex)
        {
            Plugin.LogError("Send items to hideout failed: " + ex);
            NotificationGate.Warn("Could not list your items.");
        }
    }

    private static void LeaveRaid(ExfiltrationPoint point, Collider collider)
    {
        Plugin.Log($"Leave raid requested (point={(point == null ? "null" : point.name)}, collider={(collider == null ? "null" : collider.name)})");
        if (point == null || collider == null)
        {
            NotificationGate.Warn("Could not start the extraction (zone lost).");
            return;
        }

        var method = typeof(ExfiltrationPoint).GetMethod(
            "IPhysicsTrigger.OnTriggerEnter",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            Plugin.LogError("Could not find ExfiltrationPoint.IPhysicsTrigger.OnTriggerEnter.");
            NotificationGate.Warn("Could not start the extraction.");
            return;
        }

        ClearPrompt();
        _allowExtractPoint = point;
        try
        {
            // goes through our prefix, which lets it pass because of _allowExtractPoint
            method.Invoke(point, new object[] { collider });
            Plugin.Log("Leave raid: extraction trigger invoked.");
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to start the extraction at the hideout exfil: {ex}");
        }
        finally
        {
            _allowExtractPoint = null;
            Leave();
        }
    }

    private static string TraderName(string traderId)
    {
        try
        {
            var name = (traderId + " Nickname").Localized();
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, traderId + " Nickname"))
            {
                return name;
            }
        }
        catch
        {
            // fall through
        }

        return traderId;
    }

    // ------------------------------------------------------------------------------------------
    // Interaction prompt. The concrete EFT types (ActionsReturnClass etc.) are renamed between EFT
    // versions, so everything is built through reflection from the bindable's own value type.
    // ------------------------------------------------------------------------------------------
    private static GamePlayerOwner ResolveOwner()
    {
        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        return mainPlayer == null ? null : mainPlayer.gameObject.GetComponent<GamePlayerOwner>();
    }

    private static bool _promptBroken;

    /// <summary>
    /// BindableState.Value has no getter, so the current value is read from a backing field of the same type
    /// when there is one. Returns false when it cannot be read.
    /// </summary>
    private static bool TryGetCurrent(object bindable, Type valueType, out object current)
    {
        current = null;
        for (var t = bindable.GetType(); t != null; t = t.BaseType)
        {
            foreach (var field in t.GetFields(AnyInstance | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == valueType)
                {
                    current = field.GetValue(bindable);
                    return true;
                }
            }

            foreach (var prop in t.GetProperties(AnyInstance | BindingFlags.DeclaredOnly))
            {
                if (prop.PropertyType == valueType && prop.GetIndexParameters().Length == 0 &&
                    prop.GetGetMethod(true) != null)
                {
                    current = prop.GetGetMethod(true).Invoke(bindable, null);
                    return true;
                }
            }
        }

        return false;
    }

    private static void ApplyPrompt(bool force)
    {
        if (_promptBroken)
        {
            return;
        }

        try
        {
            var owner = ResolveOwner();
            var bindable = owner?.AvailableInteractionState;
            if (bindable == null || _currentKind == ZoneKind.None)
            {
                return;
            }

            var valueMember = FindMember(bindable.GetType(), "Value");
            if (valueMember == null)
            {
                return;
            }

            _promptValue ??= BuildPrompt(valueMember);
            if (_promptValue == null)
            {
                _promptBroken = true;
                return;
            }

            if (TryGetCurrent(bindable, MemberType(valueMember), out var current))
            {
                if (!force && ReferenceEquals(current, _promptValue))
                {
                    return;
                }
            }
            else if (!force)
            {
                // cannot compare: just set it again (same reference is a no-op for most bindables)
                SetValue(valueMember, bindable, _promptValue);
                return;
            }

            // null first so subscribers see a change
            SetValue(valueMember, bindable, null);
            SetValue(valueMember, bindable, _promptValue);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to show interaction prompt: {ex}");
            _promptBroken = true; // do not spam; the zone itself stays valid so the actions keep working
        }
    }

    private static void ClearPrompt()
    {
        if (_promptValue == null)
        {
            return;
        }

        try
        {
            var bindable = ResolveOwner()?.AvailableInteractionState;
            var valueMember = bindable == null ? null : FindMember(bindable.GetType(), "Value");
            if (valueMember == null)
            {
                return;
            }

            if (!TryGetCurrent(bindable, MemberType(valueMember), out var current) ||
                ReferenceEquals(current, _promptValue))
            {
                SetValue(valueMember, bindable, null);
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to clear interaction prompt: {ex}");
        }
    }

    private static object BuildPrompt(MemberInfo valueMember)
    {
        var valueType = MemberType(valueMember);
        var prompt = Activator.CreateInstance(valueType);

        var actionsMember = FindMember(valueType, "Actions");
        if (actionsMember == null)
        {
            Plugin.LogError($"Interaction type {valueType.FullName} has no 'Actions' member.");
            return null;
        }

        var actions = GetValue(actionsMember, prompt) as IList;
        if (actions == null)
        {
            Plugin.LogError("Interaction 'Actions' is not an initialised list.");
            return null;
        }

        var elementType = actions.GetType().IsGenericType
            ? actions.GetType().GetGenericArguments()[0]
            : null;
        if (elementType == null)
        {
            Plugin.LogError("Could not resolve the interaction action element type.");
            return null;
        }

        void AddAction(string label, System.Action action)
        {
            var element = Activator.CreateInstance(elementType);
            SetNamed(element, "Name", label);
            SetNamed(element, "Action", action);
            SetNamed(element, "Disabled", false);
            actions.Add(element);
        }

        if (_currentKind == ZoneKind.Hideout)
        {
            AddAction("Send items to hideout", SendItemsToHideout);
            AddAction("Read messages", ShowMessages);
            var leavePoint = _currentPoint;
            var leaveCollider = _currentCollider;
            AddAction("Leave to hideout", () => LeaveRaid(leavePoint, leaveCollider));
        }
        else if (_currentTraderIds != null)
        {
            foreach (var traderId in _currentTraderIds)
            {
                var id = traderId;
                AddAction($"Open {TraderName(id)}", () => OpenTrader(id));
            }

            AddAction("Read messages", ShowMessages);
        }

        return prompt;
    }

    private static void SetNamed(object target, string name, object value)
    {
        var member = FindMember(target.GetType(), name);
        if (member == null)
        {
            Plugin.LogError($"{target.GetType().FullName} has no '{name}' member.");
            return;
        }

        SetValue(member, target, value);
    }

    private static MemberInfo FindMember(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var prop = t.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (prop != null)
            {
                return prop;
            }

            var field = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }

    private static Type MemberType(MemberInfo member)
    {
        return member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
    }

    private static object GetValue(MemberInfo member, object target)
    {
        if (member is PropertyInfo p)
        {
            var getter = p.GetGetMethod(true);
            if (getter == null)
            {
                throw new InvalidOperationException($"{p.DeclaringType?.FullName}.{p.Name} has no getter.");
            }

            return getter.Invoke(target, null);
        }

        return ((FieldInfo)member).GetValue(target);
    }

    private static void SetValue(MemberInfo member, object target, object value)
    {
        if (member is PropertyInfo p)
        {
            var setter = p.GetSetMethod(true);
            if (setter == null)
            {
                throw new InvalidOperationException($"{p.DeclaringType?.FullName}.{p.Name} has no setter.");
            }

            setter.Invoke(target, new[] { value });
        }
        else
        {
            ((FieldInfo)member).SetValue(target, value);
        }
    }
}
