using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace OpenworldAddon;

/// <summary>
/// Fuel from the cars standing around: hold the siphon key next to a car (needs a silicone tube and a fuel tank with room
/// in what you carry). It runs for as many seconds as units are taken, the car holds a random small amount, and the fuel goes
/// into the tanks one after the other until the car is empty or the tanks are full. Cars are found by their door handles
/// (CarLock_Hand), the amount left is remembered for the raid.
/// </summary>
public static class VehicleFuel
{
    private const string Tube = "5d1b39a386f774252339976f";
    private static readonly string[] Tanks = ["5d1b371186f774253763a656", "5d1b36a186f7742523398433"]; // Expeditionary, Metal fuel tank
    private const float Reach = 3.5f;

    private sealed class Car
    {
        public Transform Root;
        public float Fuel;
    }

    private static readonly Dictionary<int, Car> Cars = new();
    private sealed class Spot
    {
        public Transform Root;
        public Bounds Box;
    }

    private static List<Spot> _spots;
    private static GameWorld _world;
    private static Car _near;
    private static float _nextLook;
    private static float _held;
    private static float _span;
    private static bool _busy;
    private static string _hint = "";
    private static Texture2D _white;

    /// <summary>
    /// The time to hold the key: the time (seconds per unit times the amount) times the 'Repair objective Time Multiplier' of the Plant Time Modifier mod
    /// (when it is installed, enabled and the option is on), so the time is set in one place like the game's repair actions.
    /// </summary>
    private static float Scaled(float seconds)
    {
        if (!Plugin.SiphonUsesPlantTime)
        {
            return seconds;
        }

        try
        {
            var info = BepInEx.Bootstrap.Chainloader.PluginInfos.FirstOrDefault(p => p.Key.Equals("com.utjan.PlantTimeModifier", StringComparison.OrdinalIgnoreCase)).Value;
            var config = info?.Instance?.Config;
            if (config != null)
            {
                if (config.TryGetEntry<bool>(new BepInEx.Configuration.ConfigDefinition("Main Settings", "Enable Mod"), out var enabled) && !enabled.Value)
                {
                    return seconds;
                }

                if (config.TryGetEntry<float>(new BepInEx.Configuration.ConfigDefinition("Main Settings", "Repair objective Time Multiplier"), out var factor))
                {
                    return Mathf.Max(0.05f, seconds * factor.Value);
                }
            }
        }
        catch (Exception)
        {
            // without the other mod the plain time is used
        }

        return seconds;
    }

    public static void Tick()
    {
        var world = Singleton<GameWorld>.Instance;
        var player = world?.MainPlayer;
        if (!Plugin.SiphonEnabled || player == null || !Plugin.IsInRaid())
        {
            Stop(false);
            _near = null;
            _hint = "";
            return;
        }

        if (world != _world)
        {
            // a new raid: everything is forgotten
            _world = world;
            _spots = null;
            Cars.Clear();
            Stop(false);
        }

        if (Time.time >= _nextLook)
        {
            _nextLook = Time.time + 0.4f;
            _near = Nearest(player);
        }

        if (_near == null || Cursor.lockState != CursorLockMode.Locked || _busy)
        {
            Stop(false);
            _hint = "";
            return;
        }

        var key = Plugin.SiphonKey;
        if (_near.Fuel <= 0.001f)
        {
            // rolled as empty (or taken empty): shown at once
            _hint = "This car has no fuel.";
            Stop(false);
            return;
        }

        _hint = "Hold [" + key + "]: siphon fuel";
        if (!Input.GetKey(key))
        {
            Stop(false);
            return;
        }

        if (_held == 0f)
        {
            if (!CanStart(player))
            {
                return;
            }

            // the time depends on the amount that will be taken
            _span = Mathf.Max(0.3f, Scaled(Mathf.Min(_near.Fuel, Free(player)) * Plugin.SiphonSeconds));
        }

        _held += Time.deltaTime;
        _hint = "Siphoning...";
        if (_held >= _span)
        {
            var car = _near;
            Stop(false);
            Finish(player, car);
        }
    }

    private static float Free(Player player)
    {
        return Room(LocalTrade.CarriedItems(player)).Sum(x => x.Item3);
    }

    public static void Draw()
    {
        if (string.IsNullOrEmpty(_hint))
        {
            return;
        }

        var width = 320f;
        var x = (Screen.width - width) / 2f;
        var y = Screen.height * 0.72f;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
        GUI.color = Color.black;
        GUI.Label(new Rect(x + 1, y + 1, width, 28), _hint, style);
        GUI.color = Color.white;
        GUI.Label(new Rect(x, y, width, 28), _hint, style);
        if (_held > 0f)
        {
            _white ??= Texture2D.whiteTexture;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(x, y + 30, width, 8), _white);
            GUI.color = new Color(0.9f, 0.75f, 0.2f, 0.95f);
            GUI.DrawTexture(new Rect(x, y + 30, width * Mathf.Clamp01(_held / _span), 8), _white);
            GUI.color = Color.white;
        }
    }

    /// <summary>
    /// The cars of the map: everything under VEHICLE/OFF (the drivable ones), plus cars found by their door handles
    /// (CarLock_Hand) or their body (car_housing_lod0, the wrecks). Each with the box of its parts.
    /// </summary>
    private static List<Spot> FindSpots()
    {
        var roots = new Dictionary<int, Transform>();
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
        {
            if (t == null)
            {
                continue;
            }

            Transform root = null;
            if (t.name == "CarLock_Hand")
            {
                root = t.parent?.parent;
            }
            else if (t.name == "car_housing_lod0")
            {
                root = t.parent;
            }
            else if (t.parent != null && t.parent.name == "OFF" && t.parent.parent != null && t.parent.parent.name == "VEHICLE")
            {
                root = t;
            }

            if (root != null)
            {
                roots[root.GetInstanceID()] = root;
            }
        }

        var list = new List<Spot>();
        foreach (var root in roots.Values)
        {
            var box = new Bounds(root.position, Vector3.one);
            var first = true;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (first)
                {
                    box = renderer.bounds;
                    first = false;
                }
                else
                {
                    box.Encapsulate(renderer.bounds);
                }
            }

            list.Add(new Spot { Root = root, Box = box });
        }

        Plugin.Log("Siphon: " + list.Count + " car(s) found on this map.");
        return list;
    }

    private static Car Nearest(Player player)
    {
        _spots ??= FindSpots();

        var origin = player.Transform.position;
        var camera = Camera.main != null ? Camera.main.transform : null;
        Spot best = null;
        var bestDistance = Reach;
        foreach (var spot in _spots)
        {
            if (spot.Root == null || !spot.Root.gameObject.activeInHierarchy)
            {
                continue;
            }

            var near = spot.Box.ClosestPoint(origin);
            var distance = Vector3.Distance(near, origin);
            if (distance >= bestDistance)
            {
                continue;
            }

            // only a car that is looked at, so F next to something else is left alone
            if (camera != null)
            {
                var toNear = (near - camera.position).normalized;
                var toCenter = (spot.Box.center - camera.position).normalized;
                if (Vector3.Dot(camera.forward, toNear) < 0.5f && Vector3.Dot(camera.forward, toCenter) < 0.7f)
                {
                    continue;
                }
            }

            bestDistance = distance;
            best = spot;
        }

        if (best == null)
        {
            return null;
        }

        var root = best.Root;
        var id = root.GetInstanceID();
        if (!Cars.TryGetValue(id, out var car))
        {
            var min = Mathf.Min(Plugin.SiphonMin, Plugin.SiphonMax);
            var max = Mathf.Max(Plugin.SiphonMin, Plugin.SiphonMax);
            // some cars simply have nothing left
            var none = UnityEngine.Random.value * 100f < Plugin.SiphonEmptyChance;
            car = new Car { Root = root, Fuel = none ? 0 : UnityEngine.Random.Range(min, max + 1) };
            Cars[id] = car;
            Plugin.Trace("Siphon: " + root.name + " holds " + car.Fuel + " units.");
        }

        return car;
    }

    private static bool CanStart(Player player)
    {
        var items = LocalTrade.CarriedItems(player);
        if (!items.Any(i => i.TemplateId.ToString() == Tube))
        {
            _hint = "You need a silicone tube.";
            return false;
        }

        if (!Room(items).Any())
        {
            _hint = "You need a fuel tank with room left.";
            return false;
        }

        return true;
    }

    /// <summary>The carried fuel tanks that are not full, the fullest one first.</summary>
    private static List<(Item Item, object Resource, float Free)> Room(IEnumerable<Item> items)
    {
        var list = new List<(Item, object, float)>();
        foreach (var item in items)
        {
            if (!Tanks.Contains(item.TemplateId.ToString()))
            {
                continue;
            }

            var resource = item.GetItemComponent<ResourceComponent>();
            if (resource == null)
            {
                continue;
            }

            var free = resource.MaxResource - resource.Value;
            if (free > 0.01f)
            {
                list.Add((item, resource, free));
            }
        }

        return list.OrderBy(x => x.Item3).ToList();
    }

    private static void Finish(Player player, Car car)
    {
        try
        {
            var filled = 0f;
            foreach (var (item, resource, free) in Room(LocalTrade.CarriedItems(player)))
            {
                if (car.Fuel <= 0.001f)
                {
                    break;
                }

                var add = Mathf.Min(free, car.Fuel);
                var component = (ResourceComponent)resource;
                if (!SetValue(component, component.Value + add))
                {
                    Plugin.LogError("Siphon: the fuel value of a tank could not be set.");
                    break;
                }

                car.Fuel -= add;
                filled += add;
            }

            if (filled <= 0f)
            {
                NotificationGate.Warn("The fuel tanks are full.");
                return;
            }

            Plugin.Log("Siphoned " + Math.Round(filled, 2) + " units from " + car.Root.name + ", " + Math.Round(car.Fuel, 2) + " left in the car.");
            NotificationGate.Message("Siphoned " + Math.Round(filled, 1) + " units of fuel" + (car.Fuel > 0.01f ? " (the car still has some)." : " (the car is empty)."));
        }
        catch (Exception e)
        {
            Plugin.LogError("Siphon failed: " + e);
        }
    }

    private static bool SetValue(ResourceComponent component, float value)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var property = typeof(ResourceComponent).GetProperty("Value", all);
        if (property != null && property.CanWrite)
        {
            property.SetValue(component, value);
            return true;
        }

        var field = typeof(ResourceComponent).GetFields(all).FirstOrDefault(f => f.FieldType == typeof(float) && f.Name.ToLowerInvariant().Contains("value"));
        if (field != null)
        {
            field.SetValue(component, value);
            return true;
        }

        return false;
    }

    private static void Stop(bool unused)
    {
        _held = 0f;
    }
}
