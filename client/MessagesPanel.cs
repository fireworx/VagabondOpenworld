// Window style adapted from ZeroMenuTime's hideout panel (https://github.com/r2rka1/ZeroMenuTime),
// MIT License, Copyright (c) 2026 r2rka1.
using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OpenworldAddon;

/// <summary>
/// The "Read messages" window: senders on the left (what you sent to the hideout is one entry),
/// the items they hold on the right with the game's own item pictures. Items are taken one by one
/// or all at once into the carried inventory.
/// </summary>
public static class MessagesPanel
{
    private const string CanvasName = "Openworld messages panel";

    private static readonly Color Ink = new Color32(231, 229, 212, 255);
    private static readonly Color Dim = new Color32(231, 229, 212, 140);
    private static readonly Color Gold = new Color32(197, 160, 72, 255);

    /// <summary>One line in the window: an item and what pressing its button does.</summary>
    public sealed class Row
    {
        public EFT.InventoryLogic.Item Item;
        public string Label;
        public string Note;
        public Func<System.Threading.Tasks.Task<bool>> Act;
    }

    /// <summary>One entry of the left column and the rows it holds.</summary>
    public sealed class Group
    {
        public string Name;
        public List<Row> Rows = new List<Row>();
    }

    private static GameObject _canvas;
    private static List<Group> _groups;
    private static string _selected, _title, _verb, _side;
    private static bool _busy;
    private static TMP_FontAsset _font;

    public static bool IsOpen => _canvas != null;

    public static void Open(string title, string side, string verb, List<Group> groups)
    {
        _title = title;
        _side = side;
        _verb = verb;
        _groups = groups;
        _selected = groups.Select(g => g.Name).FirstOrDefault();
        _busy = false;
        Build();
        GamePlayerOwner.SetIgnoreInputInNPCDialog(true);
        Listen(true);
    }

    public static void Close()
    {
        if (_canvas != null)
        {
            UnityEngine.Object.Destroy(_canvas);
            _canvas = null;
            GamePlayerOwner.SetIgnoreInputInNPCDialog(false);
            Listen(false);
        }

        _groups = null;
    }

    private static void Listen(bool on)
    {
        try
        {
            UIEventSystem.Instance.SetTemporaryStatus(on);
        }
        catch (Exception e)
        {
            Plugin.LogError("Messages panel: could not switch the interface's mouse: " + e.Message);
        }
    }

    /// <summary>Every frame (Update): Escape closes.</summary>
    public static void Tick()
    {
        if (_canvas != null && (GamePlayerOwner.MyPlayer == null || Input.GetKeyDown(KeyCode.Escape)))
        {
            Close();
        }
    }

    /// <summary>Every frame (LateUpdate): the raid keeps hiding the pointer.</summary>
    public static void LateTick()
    {
        if (_canvas != null)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    private static async void Do(Group group, Row row)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (await row.Act())
            {
                group.Rows.Remove(row);
            }
        }
        finally
        {
            _busy = false;
            Cleanup();
            Build();
        }
    }

    private static async void DoAll()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var group = _groups.FirstOrDefault(g => g.Name == _selected);
            foreach (var row in group?.Rows.ToArray() ?? new Row[0])
            {
                if (!await row.Act())
                {
                    return; // no room (or an error): the rest stays
                }

                group.Rows.Remove(row);
            }
        }
        finally
        {
            _busy = false;
            Cleanup();
            Build();
        }
    }

    private static void Cleanup()
    {
        if (_groups == null)
        {
            return;
        }

        _groups.RemoveAll(g => g.Rows.Count == 0);
        if (_selected == null || !_groups.Any(g => g.Name == _selected))
        {
            _selected = _groups.Select(g => g.Name).FirstOrDefault();
        }
    }

    // ---------------------------------------------------------------- building the window

    private static void Build()
    {
        if (_groups == null)
        {
            return;
        }

        var scrollPos = 1f;
        if (_canvas != null)
        {
            var old = _canvas.GetComponentsInChildren<ScrollRect>().LastOrDefault();
            if (old != null)
            {
                scrollPos = old.verticalNormalizedPosition;
            }

            UnityEngine.Object.Destroy(_canvas);
        }

        var stale = GameObject.Find(CanvasName);
        if (stale != null)
        {
            UnityEngine.Object.Destroy(stale);
        }

        _canvas = new GameObject(CanvasName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = _canvas.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 60;
        var scaler = _canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;

        var veil = Node(_canvas.transform, "veil");
        Stretch(veil);
        veil.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

        var window = Node(_canvas.transform, "window");
        window.anchorMin = window.anchorMax = window.pivot = new Vector2(0.5f, 0.5f);
        window.sizeDelta = new Vector2(1180f, 760f);
        window.gameObject.AddComponent<Image>().color = new Color32(14, 14, 13, 245);
        var edge = Node(window, "edge");
        edge.anchorMin = new Vector2(0f, 1f);
        edge.anchorMax = new Vector2(1f, 1f);
        edge.pivot = new Vector2(0.5f, 1f);
        edge.sizeDelta = new Vector2(0f, 3f);
        edge.gameObject.AddComponent<Image>().color = Gold;

        var column = Node(window, "column");
        Stretch(column);
        Vertical(column, 18f, new RectOffset(36, 36, 30, 26));

        var head = Node(column, "head");
        Horizontal(head, 16f);
        var title = Text(head, _title, 40f, Ink, true);
        title.characterSpacing = 4f;
        Flexible(title.gameObject, 1f, 0f);
        Button(head, "CLOSE  [ESC]", Close, true, 190f, false);

        var body = Node(column, "body");
        Horizontal(body, 34f).childAlignment = TextAnchor.UpperLeft;
        Flexible(body.gameObject, 1f, 1f);

        // left: the senders
        var left = Node(body, "left");
        Vertical(left, 8f, new RectOffset(0, 0, 0, 0));
        Fixed(left.gameObject, 330f);
        Heading(left, _side);
        foreach (var group in _groups)
        {
            var name = group.Name;
            Button(left, name.ToUpperInvariant() + "  " + group.Rows.Count, () =>
            {
                _selected = name;
                Build();
            }, true, 330f, name == _selected);
        }

        // right: what they hold
        var right = Node(body, "right");
        Vertical(right, 10f, new RectOffset(0, 0, 0, 0));
        Flexible(right.gameObject, 1f, 1f);

        var mine = _groups.FirstOrDefault(g => g.Name == _selected);
        var top = Node(right, "top");
        Horizontal(top, 12f);
        var heading = Text(top, (_selected ?? "").ToUpperInvariant(), 22f, Gold, true);
        heading.characterSpacing = 5f;
        Flexible(heading.gameObject, 1f, 0f);
        if (mine != null && mine.Rows.Count > 1)
        {
            Button(top, _verb + " ALL", DoAll, !_busy, 220f, false);
        }

        var content = Scroll(right);
        string lastNote = null;
        if (mine != null)
        {
            foreach (var row in mine.Rows)
            {
                if (!string.IsNullOrWhiteSpace(row.Note) && row.Note != lastNote)
                {
                    Text(content, row.Note, 15f, Dim, false).fontStyle = FontStyles.Italic;
                }

                lastNote = row.Note;
                var g = mine;
                var r = row;
                var line = Node(content, "row");
                Horizontal(line, 14f);
                Icon(line, r.Item, 56f);
                Flexible(Text(line, r.Label, 20f, Ink, false).gameObject, 1f, 0f);
                Button(line, _verb, () => Do(g, r), !_busy, 130f, false);
            }
        }

        var scroll = _canvas.GetComponentsInChildren<ScrollRect>().LastOrDefault();
        if (scroll != null)
        {
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = Mathf.Clamp01(scrollPos);
        }

        if (mine == null)
        {
            Text(content, "Nothing here.", 20f, Dim, false);
        }
    }

    // ---------------------------------------------------------------- small building blocks

    private static TMP_FontAsset Font()
    {
        if (_font == null)
        {
            TMP_FontAsset any = null;
            foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                any = any ?? font;
                if (font.name.IndexOf("Bender", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    font.name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _font = font;
                    break;
                }
            }

            _font = _font ?? any;
        }

        return _font;
    }

    private static RectTransform Node(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    private static VerticalLayoutGroup Vertical(RectTransform rect, float spacing, RectOffset padding)
    {
        var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = spacing;
        layout.padding = padding;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.UpperLeft;
        return layout;
    }

    private static HorizontalLayoutGroup Horizontal(RectTransform rect, float spacing)
    {
        var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.MiddleLeft;
        return layout;
    }

    private static void Flexible(GameObject go, float width, float height)
    {
        var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        element.flexibleWidth = width;
        element.flexibleHeight = height;
    }

    private static void Fixed(GameObject go, float width)
    {
        var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        element.preferredWidth = element.minWidth = width;
        element.flexibleWidth = 0f;
    }

    private static TextMeshProUGUI Text(RectTransform parent, string words, float size, Color colour, bool bold)
    {
        var rect = Node(parent, "text");
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        var font = Font();
        if (font != null)
        {
            label.font = font;
        }

        label.text = words;
        label.fontSize = size;
        label.color = colour;
        label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.enableWordWrapping = true;
        label.richText = false;
        label.raycastTarget = false;
        return label;
    }

    private static void Heading(RectTransform parent, string words)
    {
        var label = Text(parent, words, 17f, Gold, true);
        label.characterSpacing = 6f;
        var rule = Node(parent, "rule");
        rule.gameObject.AddComponent<Image>().color = new Color32(231, 229, 212, 40);
        var element = rule.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = element.minHeight = 1f;
    }

    /// <summary>The game's own picture of the item, in a dark square. It fills in when the game has drawn it.</summary>
    private static void Icon(RectTransform parent, EFT.InventoryLogic.Item item, float size)
    {
        var frame = Node(parent, "icon");
        frame.gameObject.AddComponent<Image>().color = new Color32(0, 0, 0, 120);
        var element = frame.gameObject.AddComponent<LayoutElement>();
        element.preferredWidth = element.minWidth = size;
        element.preferredHeight = element.minHeight = size;
        element.flexibleWidth = 0f;
        var picture = Node(frame, "picture");
        Stretch(picture);
        picture.offsetMin = new Vector2(3f, 3f);
        picture.offsetMax = new Vector2(-3f, -3f);
        var image = picture.gameObject.AddComponent<Image>();
        image.preserveAspect = true;
        image.raycastTarget = false;
        image.enabled = false;
        try
        {
            var view = picture.gameObject.AddComponent<MessagesIcon>();
            view.Source = EFT.UI.DragAndDrop.ItemViewFactory.LoadItemIcon(item);
            view.Target = image;
        }
        catch (Exception e)
        {
            Plugin.LogError("Messages panel: no picture for " + item.TemplateId + ": " + e.Message);
        }
    }

    /// <summary>A list that scrolls. Gives back the place to put its entries.</summary>
    private static RectTransform Scroll(RectTransform parent)
    {
        var scroll = Node(parent, "scroll");
        Flexible(scroll.gameObject, 1f, 1f);
        scroll.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
        var view = scroll.gameObject.AddComponent<ScrollRect>();
        var viewport = Node(scroll, "viewport");
        Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = Node(viewport, "content");
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = content.offsetMax = Vector2.zero;
        Vertical(content, 10f, new RectOffset(0, 8, 0, 0));
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        view.viewport = viewport;
        view.content = content;
        view.horizontal = false;
        view.vertical = true;
        view.movementType = ScrollRect.MovementType.Clamped;
        view.scrollSensitivity = 40f;
        return content;
    }

    private static void Button(RectTransform parent, string words, Action onClick, bool enabled, float width, bool selected)
    {
        var holder = Node(parent, "button " + words);
        var rest = selected ? new Color32(197, 160, 72, 70) : enabled ? new Color32(231, 229, 212, 28) : new Color32(231, 229, 212, 10);
        var image = holder.gameObject.AddComponent<Image>();
        image.color = rest;
        var element = holder.gameObject.AddComponent<LayoutElement>();
        element.preferredWidth = element.minWidth = width;
        element.preferredHeight = element.minHeight = 42f;
        element.flexibleWidth = 0f;
        var restInk = selected ? Gold : enabled ? Ink : new Color32(231, 229, 212, 70);
        var label = Text(holder, words, 18f, restInk, true);
        Stretch(label.rectTransform);
        label.alignment = TextAlignmentOptions.Center;
        label.characterSpacing = 3f;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        var button = holder.gameObject.AddComponent<MessagesButton>();
        button.Background = image;
        button.Label = label;
        button.Clicked = onClick;
        button.Usable = enabled;
        button.RestBack = rest;
        button.RestInk = restInk;
    }
}

/// <summary>Shows an item's picture as soon as the game has drawn it.</summary>
public class MessagesIcon : MonoBehaviour
{
    public ItemIcon Source;
    public Image Target;

    private void Update()
    {
        if (Source != null && Target != null && Source.Sprite != null && Target.sprite != Source.Sprite)
        {
            Target.sprite = Source.Sprite;
            Target.enabled = true;
        }
    }
}

/// <summary>A button of the messages window.</summary>
public class MessagesButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    public Image Background;
    public TextMeshProUGUI Label;
    public Action Clicked;
    public bool Usable;
    public Color RestBack, RestInk;

    public void OnPointerEnter(PointerEventData data)
    {
        if (Usable)
        {
            Background.color = new Color32(231, 229, 212, 235);
            Label.color = new Color32(14, 14, 13, 255);
            Sound(EUISoundType.ButtonOver);
        }
    }

    public void OnPointerExit(PointerEventData data)
    {
        if (Usable)
        {
            Background.color = RestBack;
            Label.color = RestInk;
        }
    }

    public void OnPointerClick(PointerEventData data)
    {
        if (Usable && Clicked != null)
        {
            Sound(EUISoundType.ButtonClick);
            Clicked();
        }
    }

    private static void Sound(EUISoundType kind)
    {
        try
        {
            if (Singleton<GUISounds>.Instantiated)
            {
                Singleton<GUISounds>.Instance.PlayUISound(kind);
            }
        }
        catch (Exception)
        {
        }
    }
}
