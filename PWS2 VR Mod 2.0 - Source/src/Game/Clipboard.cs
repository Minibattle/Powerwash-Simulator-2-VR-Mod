using System.Linq;
using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;
using UnityEngine.UI;

namespace PowerWashVR.Game;

// The task list on a clipboard. Reach the washer hand up beside its ear (or
// behind the head) and hold grip: a clipboard comes out in that hand, the
// washer is put away, and the game's own task list is shown on the board,
// trimmed to its list of tasks. The other hand points a laser at it to pick
// a task as in the flat game, and its stick scrolls the list. Letting go of
// the grip puts the clipboard away and brings the washer back.
internal sealed class Clipboard
{
    internal const float BoardWidth = 0.30f;
    internal const float BoardHeight = 0.40f;
    private const float EarReach = 0.2f;

    private readonly MelonLogger.Instance log;
    private readonly BodyFrame body;
    private GameObject? board;
    private Transform? boardTransform;

    private float heldSince;

    private float nextListSearch;

    // Grip held with the clipboard out.
    internal static bool Held { get; private set; }

    // The game's task list is open and shown on the clipboard.
    internal static bool Open { get; private set; }

    internal static Vector3 BoardPosition { get; private set; }
    internal static Quaternion BoardRotation { get; private set; } = Quaternion.identity;

    // The task list itself: the game's scroll view of tasks.
    internal static ScrollRect? List { get; private set; }

    internal Clipboard(MelonLogger.Instance log, BodyFrame body)
    {
        this.log = log;
        this.body = body;
    }

    internal void Update(PlayerRig rig, GameState state, Controllers controllers, GameInput input, Haptics haptics,
        UiPresenter ui, Settings settings)
    {
        ref var hand = ref settings.LeftHanded ? ref controllers.Left : ref controllers.Right;
        ref var other = ref settings.LeftHanded ? ref controllers.Right : ref controllers.Left;
        var washerIsRight = !settings.LeftHanded;
        var player = rig.IsPlayer && rig.HasPose && hand.Tracked;

        // With Physical Washer only a hand without the washer takes it.
        var near = player && state.Mode == UiMode.Gameplay && !Held && !(PhysicalWasher.Active && PhysicalWasher.Holding) && AtEar(rig, settings, hand);
        input.WasherGripClaimed = near || Held;

        if (awayUntilRelease && !input.WasherGrip.Down)
            awayUntilRelease = false;
        if (near && input.WasherGrip.Pressed && !awayUntilRelease)
        {
            Held = true;
            heldSince = Time.unscaledTime;
            haptics.Pulse(washerIsRight, 0.5f, 0.05f);
            log.Msg("clipboard: out");
            // The list is borrowed and shown by the board itself; the game's
            // own task-list toggle (which also changes its input and screen
            // state) is left alone.
        }

        if (Held && (!input.WasherGrip.Down || !player))
        {
            Held = false;
            haptics.Pulse(washerIsRight, 0.25f, 0.03f);
            GiveBack();

            log.Msg("clipboard: put away");
        }

        if (Held && !Alive(widget) && Time.unscaledTime >= nextListSearch && Time.unscaledTime - heldSince < 2f)
        {
            nextListSearch = Time.unscaledTime + 0.15f;
            Borrow();
        }

        Place(rig, hand, settings);
        if (Held)
        {
            Fit();
            Scroll(other.Stick.y, Time.unscaledDeltaTime);
        }
    }

    private bool listOpened;
    private bool awayUntilRelease;

    // Put away by the mod (the furniture storage opened from it): taken again
    // only after the grip has been let go.
    internal void PutAway()
    {
        if (!Held)
            return;
        Held = false;
        awayUntilRelease = true;
        GiveBack();
        log.Msg("clipboard: put away (furniture storage)");
    }

    private static bool Alive(UnityEngine.Object? value) => value is not null && value != null;

    private void Toggle(GameState state)
    {
        try
        {
            state.Input?.InvokeToggleTaskList();
        }
        catch (Exception exception)
        {
            log.Msg($"clipboard: task list not toggled ({exception.GetType().Name})");
        }
    }

    // The game's task list widget, moved onto the clipboard while it is out
    // and given back afterwards exactly as it was.
    private RectTransform? widget;
    private Transform? widgetParent;
    private int widgetSibling;
    private Vector2 widgetAnchorMin, widgetAnchorMax, widgetPivot, widgetPosition, widgetSize;
    private Vector3 widgetScale;
    private Quaternion widgetRotation;
    private ScrollRect? widgetScroll;
    private float scroll;
    private bool structureLogged;

    // The board's canvas: a masked view the size of the board's writing area.
    private GameObject? canvasObject;
    private RectTransform? viewport;
    private RectTransform? content;
    private const float PixelsPerMetre = 2000f;
    private const float ViewWidth = BoardWidth * 0.9f;
    private const float ViewHeight = BoardHeight * 0.76f;

    internal static Canvas? Canvas { get; private set; }
    internal static RectTransform? Window { get; private set; }

    private Il2CppFuturLab.PW2.UI.TaskListWidget? list;

    private void Borrow()
    {
        var found = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.TaskListWidget>());
        Il2CppFuturLab.PW2.UI.TaskListWidget? best = null;
        for (var index = 0; found is not null && index < found.Length; index++)
        {
            var candidate = found[index]?.TryCast<Il2CppFuturLab.PW2.UI.TaskListWidget>();
            if (candidate is null || candidate == null || !candidate.gameObject.scene.IsValid())
                continue;
            if (best is null || candidate.gameObject.activeInHierarchy)
                best = candidate;
        }

        var rect = best?.transform.TryCast<RectTransform>();
        if (best is null || rect is null)
            return;

        EnsureCanvas();
        list = best;
        widget = rect;
        widgetParent = rect.parent;
        widgetSibling = rect.GetSiblingIndex();
        widgetAnchorMin = rect.anchorMin;
        widgetAnchorMax = rect.anchorMax;
        widgetPivot = rect.pivot;
        widgetPosition = rect.anchoredPosition;
        widgetSize = rect.sizeDelta;
        widgetScale = rect.localScale;
        widgetRotation = rect.localRotation;
        var size = rect.rect.size;
        wasActive = rect.gameObject.activeSelf;

        try
        {
            widgetScroll = best.m_scrollRect;
        }
        catch
        {
            widgetScroll = rect.GetComponentInChildren<ScrollRect>(true);
        }

        // A fixed layout size on the board (its own anchors stretched it
        // across the HUD), scaled to the board's width.
        rect.SetParent(content, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(ListWidth, ListHeight);
        rect.localRotation = Quaternion.identity;
        rect.anchoredPosition3D = Vector3.zero;
        var fit = ViewWidth * PixelsPerMetre / ListWidth;
        rect.localScale = new Vector3(fit, fit, 1f);
        if (!rect.gameObject.activeSelf)
            rect.gameObject.SetActive(true);
        scroll = 0f;
        alignX = alignY = 0f;
        Content(0f);

        // The list grows with its tasks on a flat screen (a size fitter):
        // on the board its window keeps the board's size and clips the rest.
        try
        {
            resizer = best.m_scrollResizer;
            resizerWasOn = resizer is not null && resizer != null && resizer.enabled;
            if (resizerWasOn)
                resizer!.enabled = false;
        }
        catch
        {
            resizer = null;
        }

        // The list's own canvases sort themselves (WashListUnit at -1): drawn
        // before this canvas, under the board, and out of reach of the
        // board's clipping. Made part of this canvas while on the board, so
        // they draw after the board and are clipped to its window.
        sortedCanvases.Clear();
        var nested = rect.GetComponentsInChildren<Canvas>(true);
        for (var index = 0; nested is not null && index < nested.Length; index++)
        {
            var canvas = nested[index];
            if (canvas is null || canvas == null)
                continue;
            sortedCanvases.Add((canvas, canvas.overrideSorting, canvas.sortingOrder));
            canvas.overrideSorting = false;
        }

        var listView = Alive(widgetScroll) ? widgetScroll!.viewport : null;
        addedMask = null;
        if (listView is not null && listView != null && listView.GetComponent<RectMask2D>() is null && listView.GetComponent<Mask>() is null)
            addedMask = listView.gameObject.AddComponent<RectMask2D>();

        // Shown the way the game shows it when its task list is opened.
        try
        {
            best.HandleTaskListToggled(true);
        }
        catch (Exception exception)
        {
            log.Msg($"clipboard: list not opened ({exception.GetType().Name}: {exception.Message})");
        }

        try
        {
            best.ToggleCanvasVisibility(true);
        }
        catch
        {
            // Not every version has it.
        }

        nextTidy = 0f;
        if (diagnosticsLeft-- > 0)
        {
            Diagnose(rect, size);
            log.Msg($"clipboard: {sortedCanvases.Count} list canvas(es) folded into the board "
                + $"(were {string.Join(", ", sortedCanvases.Select(entry => $"{entry.Canvas.name} override {entry.Override} order {entry.Order}"))})");
        }
    }

    private bool wasActive;
    private readonly List<(Canvas Canvas, bool Override, int Order)> sortedCanvases = new();
    private ContentSizeFitter? resizer;
    private bool resizerWasOn;
    private RectMask2D? addedMask;
    private int diagnosticsLeft = 3;
    private const float ListWidth = 900f;
    private const float ListHeight = 1200f;

    // What the borrowed list looks like once on the board, for finding out
    // why something does not show.
    private void Diagnose(RectTransform rect, Vector2 originalSize)
    {
        try
        {
            var groups = rect.GetComponentsInChildren<CanvasGroup>(true);
            var hiddenGroups = 0;
            for (var index = 0; groups is not null && index < groups.Length; index++)
            {
                if (groups[index] is { } group && group != null && group.alpha < 0.01f)
                    hiddenGroups++;
            }

            var canvases = rect.GetComponentsInChildren<Canvas>(true);
            var canvasInfo = new List<string>();
            for (var index = 0; canvases is not null && index < canvases.Length; index++)
            {
                var canvas = canvases[index];
                if (canvas is not null && canvas != null)
                    canvasInfo.Add($"{canvas.name}:{(canvas.enabled ? "on" : "off")}/order {canvas.sortingOrder}/override {canvas.overrideSorting}/root {canvas.isRootCanvas}");
            }

            var items = rect.GetComponentsInChildren<Il2CppFuturLab.PW2.UI.TaskList.TaskListItem>(false);
            log.Msg($"clipboard: borrowed \"{rect.name}\" (was {originalSize.x:0}x{originalSize.y:0}, active {wasActive}) from \"{Path(widgetParent)}\"; "
                + $"{items?.Length ?? 0} task item(s) shown, {hiddenGroups} transparent group(s), canvases [{string.Join(", ", canvasInfo)}], "
                + $"scroll view {(widgetScroll is null ? "none" : $"\"{widgetScroll.name}\"")}");
        }
        catch (Exception exception)
        {
            log.Msg($"clipboard: diagnose failed ({exception.GetType().Name})");
        }
    }

    private void GiveBack()
    {
        var rect = widget;
        var owner = list;
        widget = null;
        widgetScroll = null;
        list = null;
        if (!Alive(rect))
            return;

        try
        {
            if (owner is not null && owner != null)
            {
                owner.HandleTaskListToggled(false);
                owner.ToggleCanvasVisibility(false);
            }
        }
        catch
        {
            // Hidden by its parent anyway.
        }

        if (!Alive(widgetParent))
        {
            UnityEngine.Object.Destroy(rect!.gameObject);
            return;
        }

        if (addedMask is not null && addedMask != null)
            UnityEngine.Object.Destroy(addedMask);
        addedMask = null;
        foreach (var (canvas, overrideSorting, order) in sortedCanvases)
        {
            if (canvas is null || canvas == null)
                continue;
            canvas.overrideSorting = overrideSorting;
            canvas.sortingOrder = order;
        }

        sortedCanvases.Clear();
        if (resizerWasOn && resizer is not null && resizer != null)
            resizer.enabled = true;
        resizer = null;

        // Parts hidden on the board show again where the game keeps them.
        foreach (var (node, scale) in collapsed)
        {
            if (node is not null && node != null)
                node.localScale = scale;
        }

        collapsed.Clear();
        rect.SetParent(widgetParent, false);
        rect.SetSiblingIndex(widgetSibling);
        rect.anchorMin = widgetAnchorMin;
        rect.anchorMax = widgetAnchorMax;
        rect.pivot = widgetPivot;
        rect.sizeDelta = widgetSize;
        rect.anchoredPosition = widgetPosition;
        rect.localScale = widgetScale;
        rect.localRotation = widgetRotation;
        if (rect.gameObject.activeSelf != wasActive)
            rect.gameObject.SetActive(wasActive);
    }

    // The other hand's stick: the list's own scroll view if it has one, else
    // the whole list slides behind the board's mask.
    private void Scroll(float stick, float deltaTime)
    {
        if (!Alive(widget) || Mathf.Abs(stick) < 0.15f)
            return;

        if (Alive(widgetScroll) && widgetScroll!.content is not null)
        {
            var list = widgetScroll;
            var contentHeight = list.content.rect.height;
            var view = list.viewport is not null && list.viewport != null ? list.viewport.rect.height : contentHeight;
            var range = Mathf.Max(1f, contentHeight - view);
            list.verticalNormalizedPosition = Mathf.Clamp01(list.verticalNormalizedPosition + stick * deltaTime * Mathf.Max(view, 200f) * 1.5f / range);
            return;
        }

        Content(scroll - stick * deltaTime * ViewHeight * PixelsPerMetre * 1.2f);
    }

    private void Content(float offset)
    {
        if (!Alive(widget) || content is null)
            return;
        var height = widget!.rect.height * widget.localScale.y;
        var max = Mathf.Max(0f, height - ViewHeight * PixelsPerMetre);
        scroll = Mathf.Clamp(offset, 0f, max);
        content.anchoredPosition = new Vector2(alignX, scroll + alignY);
    }

    // The list's own visible area (its scroll view) is what gets centred on
    // the board and sized to it: the widget around it is laid out for the
    // corner of a flat screen, not for the middle of a board.
    private float alignX, alignY;

    private void Fit()
    {
        if (!Alive(widget) || content is null || canvasObject is null)
            return;
        // The widget's flat-screen parts (its blue background...) are hidden
        // from the first frame on the board, not once the list has settled.
        Tidy();

        var area = Alive(widgetScroll) && widgetScroll!.viewport is not null && widgetScroll.viewport != null
            ? widgetScroll.viewport
            : Alive(widgetScroll) ? widgetScroll!.transform.TryCast<RectTransform>() : widget;
        if (area is null)
            return;

        var root = viewport!.transform;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        area.GetWorldCorners(corners);
        float left = float.MaxValue, right = float.MinValue, top = float.MinValue;
        for (var index = 0; index < 4; index++)
        {
            var local = root.InverseTransformPoint(corners[index]);
            left = Mathf.Min(left, local.x);
            right = Mathf.Max(right, local.x);
            top = Mathf.Max(top, local.y);
        }

        var width = right - left;
        if (width < 1f)
            return;

        // Width first (the next frame sees the new size), then position.
        var wanted = ViewWidth * PixelsPerMetre;
        var factor = wanted / width;
        if (Mathf.Abs(factor - 1f) > 0.02f)
        {
            var scale = widget!.localScale.x * factor;
            widget.localScale = new Vector3(scale, scale, 1f);
            return;
        }

        // Height: the list's own window (which clips its tasks) ends at the
        // bottom of the board's window.
        float bottom = float.MaxValue;
        for (var index = 0; index < 4; index++)
            bottom = Mathf.Min(bottom, root.InverseTransformPoint(corners[index]).y);
        var height = top - bottom;
        var viewHeight = ViewHeight * PixelsPerMetre;
        if (height > viewHeight + 1f && widget!.localScale.y > 1e-4f)
        {
            var size = widget.sizeDelta;
            widget.sizeDelta = new Vector2(size.x, Mathf.Max(200f, size.y - (height - viewHeight) / widget.localScale.y));
            return;
        }

        var centre = (left + right) * 0.5f;
        var viewTop = ViewHeight * PixelsPerMetre * 0.5f;
        if (Mathf.Abs(centre) > 0.5f || Mathf.Abs(top - viewTop) > 0.5f)
        {
            alignX -= centre;
            alignY += viewTop - top;
            content.anchoredPosition = new Vector2(alignX, scroll + alignY);
            if (fitLogs++ < 3)
                log.Msg($"clipboard: list \"{area.name}\" fitted to the board (moved {-centre:0},{viewTop - top:0} px)");
        }
    }

    private int fitLogs;
    private float nextTidy;
    private readonly HashSet<IntPtr> throughMaterials = new();

    // Only the list shows on the board: the widget's other parts (its
    // header, prompt buttons, backgrounds laid out for a flat screen) are not
    // drawn, and the list draws over the board whatever the depth.
    private void Tidy()
    {
        if (Time.unscaledTime < nextTidy || !Alive(widget))
            return;
        nextTidy = Time.unscaledTime + 0.5f;

        // The game may sort its canvases again (opening the list does).
        var resorted = 0;
        foreach (var (canvas, _, _) in sortedCanvases)
        {
            if (canvas is not null && canvas != null && canvas.overrideSorting)
            {
                canvas.overrideSorting = false;
                resorted++;
            }
        }

        if (resorted > 0 && resortLogs++ < 3)
            log.Msg($"clipboard: {resorted} list canvas(es) had sorted themselves again; folded back into the board");

        var keep = Alive(widgetScroll) ? widgetScroll!.transform : widget!.transform;
        var culled = 0;
        for (var node = keep; node is not null && node != null && node.Pointer != widget!.Pointer; node = node.parent)
        {
            var parent = node.parent;
            if (parent is null || parent == null)
                break;
            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index);
                if (child is null || child == null || child.Pointer == node.Pointer || child.localScale == Vector3.zero)
                    continue;
                collapsed.Add((child, child.localScale));
                child.localScale = Vector3.zero;
                culled++;
            }
        }

        var graphics = keep.GetComponentsInChildren<Graphic>(true);
        for (var index = 0; graphics is not null && index < graphics.Length; index++)
        {
            var graphic = graphics[index];
            if (graphic is null || graphic == null)
                continue;
            Material? material;
            try
            {
                material = graphic.materialForRendering;
            }
            catch
            {
                continue;
            }

            if (material is null || material == null || !throughMaterials.Add(material.Pointer))
                continue;
            foreach (var name in new[] { "unity_GUIZTestMode", "_ZTestMode", "_ZTest" })
            {
                if (material.HasProperty(name))
                    material.SetInt(name, (int)UnityEngine.Rendering.CompareFunction.Always);
            }
        }

        if (culled > 0 && tidyLogs++ < 3)
            log.Msg($"clipboard: {culled} part(s) outside the task list hidden");
    }

    private int tidyLogs, resortLogs;
    private readonly List<(Transform Node, Vector3 Scale)> collapsed = new();

    private void EnsureCanvas()
    {
        if (Alive(canvasObject))
            return;

        canvasObject = new GameObject("PowerWashVR Clipboard List");
        canvasObject.layer = Props.UiLayer;
        UnityEngine.Object.DontDestroyOnLoad(canvasObject);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 20;
        Canvas = canvas;
        // The board itself is part of the canvas, drawn first: the list is
        // always on top of it, whatever the game's UI shaders do with depth.
        var root = canvasObject.GetComponent<RectTransform>();
        root.sizeDelta = new Vector2(BoardWidth * PixelsPerMetre, BoardHeight * PixelsPerMetre);
        root.pivot = new Vector2(0.5f, 0.5f);
        var scale = 1f / PixelsPerMetre;
        root.localScale = new Vector3(scale, scale, scale);

        var plate = new GameObject("Board");
        plate.layer = Props.UiLayer;
        var plateRect = plate.AddComponent<RectTransform>();
        plateRect.SetParent(root, false);
        plateRect.anchorMin = Vector2.zero;
        plateRect.anchorMax = Vector2.one;
        plateRect.sizeDelta = Vector2.zero;
        var plateImage = plate.AddComponent<Image>();
        plateImage.color = new Color(0.55f, 0.38f, 0.22f, 1f);
        plateImage.raycastTarget = false;

        var clip = new GameObject("Clip");
        clip.layer = Props.UiLayer;
        var clipRect = clip.AddComponent<RectTransform>();
        clipRect.SetParent(root, false);
        clipRect.anchorMin = clipRect.anchorMax = new Vector2(0.5f, 1f);
        clipRect.pivot = new Vector2(0.5f, 1f);
        clipRect.sizeDelta = new Vector2(BoardWidth * 0.4f * PixelsPerMetre, 0.035f * PixelsPerMetre);
        var clipImage = clip.AddComponent<Image>();
        clipImage.color = new Color(0.75f, 0.76f, 0.8f, 1f);
        clipImage.raycastTarget = false;

        var view = new GameObject("Viewport");
        view.layer = Props.UiLayer;
        viewport = view.AddComponent<RectTransform>();
        viewport.SetParent(root, false);
        viewport.anchorMin = viewport.anchorMax = new Vector2(0.5f, 0.5f);
        viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.sizeDelta = new Vector2(ViewWidth * PixelsPerMetre, ViewHeight * PixelsPerMetre);
        viewport.anchoredPosition = new Vector2(0f, -0.01f * PixelsPerMetre);
        view.AddComponent<RectMask2D>();
        Window = viewport;

        var holder = new GameObject("Content");
        holder.layer = Props.UiLayer;
        content = holder.AddComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = content.anchorMax = new Vector2(0.5f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = Vector2.zero;
        canvasObject.SetActive(false);
    }

    private static string Path(Transform? node)
    {
        if (node is null || node == null)
            return "none";
        var path = node.name;
        var parent = node.parent;
        for (var depth = 0; parent is not null && parent != null && depth < 6; depth++)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }

    internal void Reapply(PlayerRig rig, Controllers controllers, Settings settings)
    {
        ref var hand = ref settings.LeftHanded ? ref controllers.Left : ref controllers.Right;
        Place(rig, hand, settings);
    }

    // Beside the washer hand's ear, or anywhere behind the head.
    private bool AtEar(PlayerRig rig, Settings settings, in HandInput hand)
    {
        var side = settings.LeftHanded ? -1f : 1f;
        var ear = rig.HeadPosition + body.Rotation * new Vector3(0.15f * side, -0.04f, -0.05f);
        if ((hand.GripPosition - ear).magnitude < EarReach)
            return true;

        var local = Quaternion.Inverse(body.Rotation) * (hand.GripPosition - rig.HeadPosition);
        return local.z < -0.12f && local.y > -0.35f && local.y < 0.2f && Mathf.Abs(local.x) < 0.3f;
    }

    // Held in the hand like a clipboard: upright, a little in front of the
    // grip, its face turned to the eyes.
    private void Place(PlayerRig rig, in HandInput hand, Settings settings)
    {
        if (!Held || !rig.HasPose || !hand.Tracked)
        {
            Props.SetActive(board, false);
            Props.SetActive(canvasObject, false);
            return;
        }

        EnsureBoard();
        var toWorld = rig.TrackingToWorldRotation;
        var grip = toWorld * hand.GripRotation;
        var gripPosition = rig.TrackingToWorldPosition + toWorld * hand.GripPosition;
        var head = rig.TrackingToWorldPosition + toWorld * rig.HeadPosition;

        var position = gripPosition + grip * new Vector3(0f, 0.06f, 0.1f) + Vector3.up * (BoardHeight * 0.35f);
        var away = position - head;
        away.y *= 0.5f;
        if (away.sqrMagnitude < 1e-4f)
            away = grip * Vector3.forward;
        var rotation = Quaternion.LookRotation(away.normalized, Vector3.up);

        BoardPosition = position;
        BoardRotation = rotation;
        // With the list on it, the board is drawn by the list's canvas (board
        // first, list over it); the solid board only stands in until then.
        var listShown = Alive(canvasObject) && Alive(widget);
        Props.SetActive(board, !listShown);
        boardTransform!.SetPositionAndRotation(position + rotation * new Vector3(0f, 0f, 0.008f), rotation);
        if (Alive(canvasObject))
        {
            Props.SetActive(canvasObject, listShown);
            var camera = rig.Camera;
            if (Canvas is not null && camera is not null && Canvas.worldCamera != camera)
                Canvas.worldCamera = camera;
            canvasObject!.transform.SetPositionAndRotation(position, rotation);
        }
    }

    private void EnsureBoard()
    {
        if (board is not null && board != null)
            return;

        board = new GameObject("PowerWashVR Clipboard");
        UnityEngine.Object.DontDestroyOnLoad(board);
        boardTransform = board.transform;
        var plate = Props.Primitive(PrimitiveType.Cube, "Board", boardTransform, Props.Lit(new Color(0.55f, 0.38f, 0.22f, 1f)));
        plate.transform.localScale = new Vector3(BoardWidth, BoardHeight, 0.008f);
        var clip = Props.Primitive(PrimitiveType.Cube, "Clip", boardTransform, Props.Lit(new Color(0.75f, 0.76f, 0.8f, 1f)));
        clip.transform.localScale = new Vector3(BoardWidth * 0.4f, 0.035f, 0.02f);
        clip.transform.localPosition = new Vector3(0f, BoardHeight * 0.5f - 0.01f, -0.006f);
        board.SetActive(false);
    }


}
