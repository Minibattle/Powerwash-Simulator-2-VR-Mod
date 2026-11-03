using Il2CppFuturLab.PW2.UI.Widgets.HUD;
using Il2CppInterop.Runtime;
using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;

namespace PowerWashVR.Game;

// The only HUD while playing: job progress and the remaining tasks, on the
// inside of the free wrist.
//
// The game's own two widgets are moved into a small world-space canvas that
// follows the wrist, so they keep updating exactly as the game drives them.
// The rest of the HUD stays in the (collapsed) root canvas. Each widget's
// original parent is remembered: when that parent goes away the widget is
// destroyed with it, as the game expects, and a new one is picked up on the
// next search.
internal sealed class WristHud
{
    private const float PixelsPerMetre = 3400f;
    private float wristScale = 1f;
    private const float ItemWidth = 460f;
    private const float ItemScale = 0.5f;
    private const float ItemShift = 0.03f;
    private bool leftHanded;

    private readonly MelonLogger.Instance log;
    private GameObject? root;
    private Transform? rootTransform;
    private float nextSearch;
    private Canvas? rootCanvas;
    private IntPtr canvasCamera;

    private sealed class Moved
    {
        internal RectTransform? Widget;
        internal Transform? OriginalParent;
        internal string Kind = "";
    }

    private readonly Moved progress = new() { Kind = "job progress" };
    private readonly Moved tasks = new() { Kind = "remaining tasks" };
    // The name and clean bar of what the washer points at, under the circle.
    private readonly Moved item = new() { Kind = "pointed-at item" };
    private bool hub, hubApplied = true;
    private bool washingFurniture;
    private float nextFurnitureCheck;

    // The hub's piece of furniture waiting to be (or being) cleaned.
    private bool DirtyFurnitureOut()
    {
        try
        {
            var pointer = GameState.StatePointer;
            if (pointer == IntPtr.Zero)
                return false;
            var hubState = new Il2CppSystem.Object(pointer).TryCast<Il2CppFuturLab.PW2.HubGameplayStateBase>();
            var furniture = hubState?.m_washableFurniture;
            var dirty = furniture is not null && furniture != null && !furniture.IsClean;
            if (dirty != washingFurniture)
                log.Msg($"wrist: furniture {(dirty ? "to clean - progress shown" : "clean or gone - progress hidden")}");
            return dirty;
        }
        catch
        {
            return false;
        }
    }

    internal WristHud(MelonLogger.Instance log)
    {
        this.log = log;
    }

    // The full task list (the game's own task list screen) hangs under the
    // wrist display while it is open; the washer hand taps the display to
    // open and close it.
    internal static bool TaskListOpen { get; private set; }
    internal static bool TaskPanelValid { get; private set; }
    internal static Vector3 TaskPanelPosition { get; private set; }
    internal static Quaternion TaskPanelRotation { get; private set; } = Quaternion.identity;

    private bool tapArmed = true;
    private Vector3 panelPosition;
    private Quaternion panelRotation = Quaternion.identity;

    internal void Update(PlayerRig rig, GameState state, Controllers controllers, Settings settings)
    {
        TaskListOpen = string.Equals(state.StateName, "TaskListState", StringComparison.Ordinal) && rig.IsPlayer;
        var playing = (state.Mode == UiMode.Gameplay || TaskListOpen) && rig.IsPlayer && rig.Camera is not null;

        if (playing && Time.unscaledTime >= nextSearch)
        {
            nextSearch = Time.unscaledTime + 1f;
            Keep(progress, Il2CppType.Of<JobProgressWidget>(), 0);
            Keep(item, Il2CppType.Of<ItemProgressWidget>(), 2);
            Keep(tasks, Il2CppType.Of<RemainingTasksWidget>(), 1);
            foreach (var moved in new[] { progress, item, tasks })
            {
                if (Alive(moved))
                    OnTop(moved.Widget!);
            }

        }

        // In the hub there is no job: no circle and no item bar - except
        // while a dirty piece of furniture is out to be cleaned.
        var inHub = state.InHub;
        if (inHub && Time.unscaledTime >= nextFurnitureCheck)
        {
            nextFurnitureCheck = Time.unscaledTime + 0.5f;
            washingFurniture = DirtyFurnitureOut();
        }

        hub = inHub && !washingFurniture;
        if (hub != hubApplied)
        {
            hubApplied = hub;
            Restack();
        }

        // Hidden by scale, not by deactivating: the game keeps animating its
        // widgets and must not find them inactive.
        visible = playing && (Alive(progress) || Alive(tasks) || Alive(item));
        if (visible)
        {
            Place(rig, controllers, settings);
        }
        else if (rootTransform is not null && rootTransform != null)
        {
            rootTransform.localScale = Vector3.zero;
        }

        if (!visible)
            TaskPanelValid = false;
    }

    // The washer hand's controller touching the display opens or closes the
    // full list; it has to move away again before the next tap counts.
    private void Tap(PlayerRig rig, GameState state, Controllers controllers, Settings settings)
    {
        ref var tapper = ref settings.LeftHanded ? ref controllers.Left : ref controllers.Right;
        if (!tapper.Tracked)
            return;

        var toWorld = rig.TrackingToWorldRotation;
        var tip = rig.TrackingToWorldPosition + toWorld * (tapper.GripPosition + tapper.GripRotation * new Vector3(0f, -0.01f, 0.05f));
        var distance = (tip - panelPosition).magnitude;
        if (!tapArmed)
        {
            tapArmed = distance > 0.13f;
            return;
        }

        if (distance > 0.07f)
            return;

        tapArmed = false;
        try
        {
            if (TaskListOpen)
            {
                // In the list's own screen the game listens to its UI action.
                var action = state.Viewport?.PlayerUIInput?.TaskListToggleAction;
                if (action?.Pressed is { } pressed)
                {
                    pressed.Invoke();
                    action.Released?.Invoke();
                }
                else
                {
                    state.Input?.InvokeToggleTaskList();
                }
            }
            else
            {
                var input = state.Input;
                if (input is null)
                    return;
                input.InvokeToggleTaskList();
            }

            log.Msg($"wrist: task list {(TaskListOpen ? "closed" : "opened")} by a tap");
        }
        catch (Exception exception)
        {
            log.Msg($"wrist: task list not toggled ({exception.GetType().Name})");
        }
    }

    private bool visible;

    // Drawn over the hand and the washer (the wrist display overlaps the
    // hand): every graphic in the moved widgets gets a copy of its material
    // that ignores depth. Checked once a second for parts the game adds.
    private readonly HashSet<IntPtr> onTop = new();
    private readonly Dictionary<IntPtr, Material> overMaterials = new();
    private static readonly int ZTestId = Shader.PropertyToID("unity_GUIZTestMode");
    private static readonly int TmpZTestId = Shader.PropertyToID("_ZTestMode");
    private bool onTopLogged;

    private Material Over(Material source)
    {
        if (overMaterials.TryGetValue(source.Pointer, out var known) && known != null)
            return known;
        var copy = new Material(source) { name = source.name + " (over hand)" };
        if (copy.HasProperty(ZTestId))
            copy.SetInt(ZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);
        if (copy.HasProperty(TmpZTestId))
            copy.SetFloat(TmpZTestId, (float)UnityEngine.Rendering.CompareFunction.Always);
        // Drawn after everything else in the frame (the hand's material is
        // see-through for its fade, so it was drawn after the display and
        // over it): the overlay queue.
        copy.renderQueue = 4000;
        overMaterials[source.Pointer] = copy;
        overMaterials[copy.Pointer] = copy;
        return copy;
    }

    private void OnTop(RectTransform widget)
    {
        try
        {
            var count = 0;
            var graphics = widget.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
            for (var index = 0; graphics is not null && index < graphics.Length; index++)
            {
                var graphic = graphics[index];
                if (graphic is null || graphic == null)
                    continue;
                var text = graphic.TryCast<Il2CppTMPro.TMP_Text>();
                if (text is not null)
                {
                    // Text can change font (and material) with the language:
                    // looked at every time.
                    var shared = text.fontSharedMaterial;
                    if (shared is not null && shared != null && !(overMaterials.TryGetValue(shared.Pointer, out var mine) && mine.Pointer == shared.Pointer))
                    {
                        text.fontSharedMaterial = Over(shared);
                        count++;
                    }

                    continue;
                }

                if (!onTop.Add(graphic.Pointer))
                    continue;
                var material = graphic.material;
                if (material is null || material == null)
                    material = UnityEngine.UI.Graphic.defaultGraphicMaterial;
                graphic.material = Over(material);
                count++;
            }

            if (count > 0 && !onTopLogged)
            {
                onTopLogged = true;
                var sample = "";
                for (var index = 0; graphics is not null && index < graphics.Length && sample.Length == 0; index++)
                {
                    var rendering = graphics[index]?.materialForRendering;
                    if (rendering is not null && rendering != null)
                        sample = $"; e.g. \"{rendering.name}\" queue {rendering.renderQueue}, depth test "
                            + $"{(rendering.HasProperty(ZTestId) ? rendering.GetInt(ZTestId).ToString() : "n/a")}";
                }

                log.Msg($"wrist: {count} part(s) drawn over the hand{sample}");
            }
        }
        catch (Exception exception)
        {
            if (!onTopLogged)
            {
                onTopLogged = true;
                log.Msg($"wrist: not drawn over the hand ({exception.GetType().Name})");
            }
        }
    }

    internal void Reapply(PlayerRig rig, Controllers controllers, Settings settings)
    {
        if (visible)
            Place(rig, controllers, settings);
    }

    private static bool Alive(Moved moved) => moved.Widget is not null && moved.Widget != null;

    private void Keep(Moved moved, Il2CppSystem.Type type, int slot)
    {
        // The widget's home went away (a new level built a new HUD): the
        // widget goes with it.
        if (Alive(moved) && (moved.OriginalParent is null || moved.OriginalParent == null))
        {
            UnityEngine.Object.Destroy(moved.Widget!.gameObject);
            moved.Widget = null;
        }

        if (Alive(moved))
            return;

        var found = UnityEngine.Object.FindObjectsOfType(type);
        if (found is null || found.Length == 0)
            return;

        for (var index = 0; index < found.Length; index++)
        {
            var component = found[index]?.TryCast<Component>();
            if (component is null || component == null)
                continue;

            var rect = component.transform.TryCast<RectTransform>();
            if (rect is null || (root is not null && rect.IsChildOf(root.transform)))
                continue;

            Adopt(moved, rect, slot);
            return;
        }
    }

    private void Adopt(Moved moved, RectTransform widget, int slot)
    {
        EnsureRoot();

        moved.OriginalParent = widget.parent;
        var size = widget.rect.size;
        if (size.x < 1f || size.y < 1f)
            size = new Vector2(420f, 160f);

        widget.SetParent(rootTransform, false);
        widget.anchorMin = widget.anchorMax = new Vector2(0.5f, 0.5f);
        widget.pivot = new Vector2(0.5f, 1f);
        widget.sizeDelta = size;
        widget.localScale = Vector3.one;
        widget.localRotation = Quaternion.identity;
        widget.anchoredPosition3D = Vector3.zero;
        moved.Widget = widget;

        // Stack: progress on top, the item bar, then the tasks.
        Restack();

        log.Msg($"wrist: {moved.Kind} widget moved to the wrist ({size.x:0}x{size.y:0}), "
                + $"from \"{Path(moved.OriginalParent)}\"");
    }

    // Progress on top, the task list under it at a smaller size, the pair
    // centred on the wrist.
    private void Restack()
    {
        var items = new List<(RectTransform Widget, float Scale)>();
        foreach (var hidden in new[] { progress, item })
        {
            if (Alive(hidden) && hub)
                hidden.Widget!.localScale = Vector3.zero;
        }

        if (Alive(progress) && !hub)
            items.Add((progress.Widget!, 1f));
        if (Alive(item) && !hub)
        {
            // The game's panel is 926 px wide (about 16 cm here): narrowed
            // to the circle's width so its ends stay off the hand.
            var bar = item.Widget!;
            if (Mathf.Abs(bar.sizeDelta.x - ItemWidth) > 1f)
                bar.sizeDelta = new Vector2(ItemWidth, bar.sizeDelta.y);
            items.Add((bar, ItemScale));
        }
        if (Alive(tasks))
            items.Add((tasks.Widget!, 0.45f));

        var total = 0f;
        foreach (var (widget, scale) in items)
            total += widget.rect.height * scale + 10f;

        var y = total * 0.5f;
        foreach (var (widget, scale) in items)
        {
            widget.localScale = new Vector3(scale, scale, 1f);
            // The item bar a little toward the side away from the hand: the
            // canvas reads left to right, so -X is your left; mirrored
            // for left-handed play (the display is then on the right wrist).
            var x = item.Widget is not null && widget.Pointer == item.Widget.Pointer
                ? (leftHanded ? 1f : -1f) * ItemShift * PixelsPerMetre / wristScale : 0f;
            widget.anchoredPosition3D = new Vector3(x, y, 0f);
            y -= widget.rect.height * scale + 10f;
        }
    }

    private void EnsureRoot()
    {
        if (root is not null && root != null)
            return;

        root = new GameObject("PowerWashVR Wrist");
        root.layer = Props.UiLayer;
        UnityEngine.Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        rootCanvas = canvas;
        canvasCamera = IntPtr.Zero;
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 10;
        var rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(600f, 600f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rootTransform = root.transform;
    }

    // On the inside of the forearm, just behind the wrist, facing up out of
    // the palm side so turning the hand toward you brings it into view.
    private void Place(PlayerRig rig, Controllers controllers, Settings settings)
    {
        if (rootTransform is null || !rig.HasPose)
            return;

        ref var free = ref settings.LeftHanded ? ref controllers.Right : ref controllers.Left;
        if (!free.Tracked)
            return;

        var camera = rig.Camera;
        if (camera is not null && rootCanvas is not null && camera.Pointer != canvasCamera)
        {
            rootCanvas.worldCamera = camera;
            canvasCamera = camera.Pointer;
        }

        var side = settings.LeftHanded ? -1f : 1f;
        var toWorld = rig.TrackingToWorldRotation;
        var gripRotation = toWorld * free.GripRotation;
        var gripPosition = rig.TrackingToWorldPosition + toWorld * free.GripPosition;

        // Grip space (Unity): +X to the right, +Y up, +Z forward.
        // On the back of the wrist, like a watch: behind the hand and on the
        // side away from the palm (the palm side was measured to be +X for the
        // left hand in the first test).
        var position = gripPosition + gripRotation * new Vector3(-0.04f * side, 0.0f, -0.15f);
        // Turned the right way up when the wrist is raised to read it.
        var rotation = gripRotation * Quaternion.Euler(0f, 90f * side, 0f) * Quaternion.Euler(-15f, 0f, 0f) * Quaternion.Euler(0f, 0f, 180f);

        rootTransform.SetPositionAndRotation(position, rotation);
        panelPosition = position;
        panelRotation = rotation;

        // The full list: just under the display, in the same plane.
        TaskPanelPosition = position + rotation * new Vector3(0f, -0.17f, 0f);
        TaskPanelRotation = rotation;
        TaskPanelValid = true;
        if ((Mathf.Abs(settings.WristScale - wristScale) > 1e-3f && settings.WristScale > 0.01f) || settings.LeftHanded != leftHanded)
        {
            if (settings.WristScale > 0.01f)
                wristScale = settings.WristScale;
            leftHanded = settings.LeftHanded;
            Restack();
        }

        var scale = settings.WristScale / PixelsPerMetre;
        rootTransform.localScale = new Vector3(scale, scale, scale);
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
}
