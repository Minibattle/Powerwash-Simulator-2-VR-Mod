using Il2CppInterop.Runtime;
using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PowerWashVR.Game;

// A laser from the controller to the menu panel, acting as a mouse.
//
// The ray is intersected with each canvas's plane, the hit is turned into the
// eye camera's screen position, and the canvas's graphics are tested there the
// way a mouse at that spot would be (see Test). Pointer
// events are then sent through ExecuteEvents' non-generic overloads (one
// interface, one event object), so this works without any input device and
// without the game's input bindings.
//
// Either hand can point; the last hand to pull its trigger owns the laser.
// Trigger clicks, the pointing hand's stick scrolls.
internal sealed class MenuPointer
{
    private const int PointerId = 0x5057;

    private readonly MelonLogger.Instance log;
    private PointerEventData? data;
    private EventSystem? system;

    private GameObject? hovered;
    private GameObject? enterTarget;
    private GameObject? pressTarget;
    private GameObject? dragTarget;
    private bool dragging;
    private Vector2 pressPosition;
    private float dragThreshold = 40f;

    private bool rightActive = true;
    private GameObject? laser;
    private GameObject? dot;
    private Transform? laserTransform;
    private Transform? dotTransform;
    private bool failed;
    private int clicksLogged;

    // Resolved on first use, once the game's UI assemblies are loaded.
    private static Il2CppSystem.Type EnterType => Types.Enter;
    private static Il2CppSystem.Type ExitType => Types.Exit;
    private static Il2CppSystem.Type DownType => Types.Down;
    private static Il2CppSystem.Type UpType => Types.Up;
    private static Il2CppSystem.Type ClickType => Types.Click;
    private static Il2CppSystem.Type ScrollType => Types.Scroll;
    private static Il2CppSystem.Type DragType => Types.Drag;
    private static Il2CppSystem.Type BeginDragType => Types.BeginDrag;
    private static Il2CppSystem.Type EndDragType => Types.EndDrag;

    private static class Types
    {
        internal static readonly Il2CppSystem.Type Enter = Il2CppType.Of<IPointerEnterHandler>();
        internal static readonly Il2CppSystem.Type Exit = Il2CppType.Of<IPointerExitHandler>();
        internal static readonly Il2CppSystem.Type Down = Il2CppType.Of<IPointerDownHandler>();
        internal static readonly Il2CppSystem.Type Up = Il2CppType.Of<IPointerUpHandler>();
        internal static readonly Il2CppSystem.Type Click = Il2CppType.Of<IPointerClickHandler>();
        internal static readonly Il2CppSystem.Type Scroll = Il2CppType.Of<IScrollHandler>();
        internal static readonly Il2CppSystem.Type Drag = Il2CppType.Of<IDragHandler>();
        internal static readonly Il2CppSystem.Type BeginDrag = Il2CppType.Of<IBeginDragHandler>();
        internal static readonly Il2CppSystem.Type EndDrag = Il2CppType.Of<IEndDragHandler>();
    }

    // Where the laser ends this frame, for drawing.
    private Vector3 rayOrigin, rayEnd;
    private bool rayHit;
    private bool visible;

    internal MenuPointer(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal void Update(PlayerRig rig, GameState state, UiPresenter ui, Controllers controllers,
        GameInput input, Settings settings)
    {
        // The clipboard: the free hand points at the board's list (and picks
        // a task); the clipboard itself scrolls with that hand's stick.
        var board = Clipboard.Canvas;
        clipboardMode = Clipboard.Held && board is not null && board != null && board.isActiveAndEnabled
            && rig.HasPose && !failed;
        var active = (state.Mode == UiMode.Menu && ui.Shown && rig.HasPose && !failed) || clipboardMode;

        if (!active)
        {
            Release();
            SetVisible(false);
            return;
        }

        try
        {
            Drive(rig, state, ui, controllers, input, settings);
        }
        catch (Exception exception)
        {
            failed = true;
            Release();
            SetVisible(false);
            log.Warning($"menu pointer: {exception.GetType().Name}: {exception.Message} - pointer off until restart");
        }
    }

    private void Drive(PlayerRig rig, GameState state, UiPresenter ui, Controllers controllers,
        GameInput input, Settings settings)
    {
        lastState = state;
        // Each new screen gets a few logged presses of its own.
        if (state.StateName != loggedState)
        {
            loggedState = state.StateName;
            diagnosticsLogged = Math.Min(diagnosticsLogged, 26);
            releasesLogged = Math.Min(releasesLogged, 26);
        }
        var leftHanded = settings.LeftHanded;

        // The hand that pulled its trigger last owns the laser (the free hand
        // only, while the clipboard is out).
        if (clipboardMode)
            rightActive = leftHanded;
        else if (input.RightTrigger.Pressed)
            rightActive = true;
        else if (input.LeftTrigger.Pressed)
            rightActive = false;

        ref var hand = ref rightActive ? ref controllers.Right : ref controllers.Left;
        var trigger = rightActive ? input.RightTrigger : input.LeftTrigger;

        if (!hand.Tracked)
        {
            SetVisible(false);
            return;
        }

        var toWorld = rig.TrackingToWorldRotation;
        var rotation = toWorld * (hand.AimTracked ? hand.AimRotation : hand.GripRotation);
        var origin = rig.TrackingToWorldPosition + toWorld * (hand.AimTracked ? hand.AimPosition : hand.GripPosition);
        // Laser Smoothing (VR SETTINGS): the aim follows the hand through a
        // short low-pass, which takes out tremor but keeps up with real moves.
        if (settings.LaserSmoothing)
        {
            var dt = Time.unscaledDeltaTime;
            if (!smoothValid || smoothRight != rightActive || Quaternion.Angle(smoothRotation, rotation) > 25f)
            {
                smoothRotation = rotation;
                smoothOrigin = origin;
            }
            else
            {
                smoothRotation = Quaternion.Slerp(smoothRotation, rotation, MathUtil.Damp(0.06f, dt));
                smoothOrigin = Vector3.Lerp(smoothOrigin, origin, MathUtil.Damp(0.03f, dt));
            }

            smoothValid = true;
            smoothRight = rightActive;
            rotation = smoothRotation;
            origin = smoothOrigin;
        }
        else
        {
            smoothValid = false;
        }

        var direction = rotation * Vector3.forward;

        var camera = rig.Camera!;
        dragThreshold = Mathf.Max(20f, camera.pixelHeight * 0.04f);
        EnsureEventObjects(state);
        if (data is null)
            return;

        // Nearest canvas hit along the ray.
        GameObject? target = null;
        var bestDistance = float.MaxValue;
        var bestScreen = Vector2.zero;
        RaycastResult? bestResult = null;

        var root = clipboardMode ? Clipboard.Canvas : ui.Root;
        if (root is not null)
            Test(root, clipboardMode ? null : ui.RootRaycaster, camera, origin, direction, ref target, ref bestDistance, ref bestScreen, ref bestResult);

        var others = ui.Others;
        var raycasters = ui.OtherRaycasters;
        for (var index = 0; !clipboardMode && index < others.Count; index++)
        {
            var canvas = others[index];
            if (canvas is null || canvas == null || !canvas.isActiveAndEnabled || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                continue;
            Test(canvas, raycasters[index], camera, origin, direction, ref target, ref bestDistance, ref bestScreen, ref bestResult);
        }

        // The laser ends on the panel plane even where nothing is hit.
        rayOrigin = origin;
        rayHit = bestDistance < float.MaxValue;
        if (!rayHit && root is not null && Plane(root.transform, origin, direction, out var planeDistance))
        {
            bestDistance = planeDistance;
            // Off every graphic (between a slider's handle and its track, say):
            // the pointer is still where the ray meets the panel, so a drag
            // keeps going instead of jumping to the corner.
            var onPlane = camera.WorldToScreenPoint(origin + direction * planeDistance);
            if (onPlane.z > 0f)
                bestScreen = new Vector2(onPlane.x, onPlane.y);
        }
        rayEnd = origin + direction * (bestDistance < float.MaxValue ? bestDistance : 2f);
        SetVisible(true);
        DrawLaser();

        var delta = bestScreen - data.position;
        data.position = bestScreen;
        data.delta = delta;
        if (target is not null && bestResult is not null)
            data.pointerCurrentRaycast = bestResult;

        Hover(target);

        if (trigger.Pressed)
            Press(target, bestScreen, bestResult);

        if (trigger.Down && pressTarget is not null)
            Drag();

        if (trigger.Released)
            Up(target);

        // The pointing hand's stick scrolls whatever is under the laser.
        var stick = hand.Stick;
        if (!clipboardMode && target is not null && Mathf.Abs(stick.y) > 0.2f)
            Scroll(target, stick.y);
    }

    // The graphics of each canvas, re-read twice a second while a menu is up.
    private sealed class GraphicCache
    {
        internal List<Graphic>? Graphics;   // raycast targets only
        internal float Expires;
    }

    private readonly Dictionary<IntPtr, GraphicCache> graphics = new();

    // What a mouse at this spot would hit, worked out the way GraphicRaycaster
    // does it (rect test, then the graphic's own Raycast for masks, alpha and
    // canvas groups), but without GraphicRaycaster's display rescaling, which
    // assumes the position is in window pixels rather than eye pixels.
    private void Test(Canvas canvas, GraphicRaycaster? raycaster, Camera camera, Vector3 origin, Vector3 direction,
        ref GameObject? target, ref float bestDistance, ref Vector2 bestScreen, ref RaycastResult? bestResult)
    {
        if (!Plane(canvas.transform, origin, direction, out var distance) || distance >= bestDistance)
            return;

        var hit = origin + direction * distance;
        var screen3 = camera.WorldToScreenPoint(hit);
        if (screen3.z <= 0f)
            return;

        var screen = new Vector2(screen3.x, screen3.y);
        // On the clipboard only what shows through the board's window counts.
        if (clipboardMode && (Clipboard.Window ?? canvas.transform.TryCast<RectTransform>()) is { } area
            && !RectTransformUtility.RectangleContainsScreenPoint(area, screen, camera))
            return;
        var now = Time.unscaledTime;
        if (!graphics.TryGetValue(canvas.Pointer, out var cache))
        {
            cache = new GraphicCache();
            graphics[canvas.Pointer] = cache;
        }

        if (cache.Graphics is null || now >= cache.Expires)
        {
            // Only what can be hit at all: most of a menu is text and
            // decoration that never takes a raycast.
            var all = canvas.GetComponentsInChildren<Graphic>(false);
            cache.Graphics ??= new List<Graphic>();
            cache.Graphics.Clear();
            for (var index = 0; index < all.Length; index++)
            {
                var graphic = all[index];
                if (graphic is not null && graphic != null && graphic.raycastTarget)
                    cache.Graphics.Add(graphic);
            }

            cache.Expires = now + 1f;
        }

        var list = cache.Graphics;
        Graphic? best = null;
        var bestOrder = int.MinValue;
        var bestDepth = int.MinValue;

        for (var index = 0; index < list.Count; index++)
        {
            var graphic = list[index];
            if (graphic is null || graphic == null || !graphic.isActiveAndEnabled)
                continue;

            var depth = graphic.depth;
            if (depth < 0)
                continue;

            var own = graphic.canvas;
            var order = own is null ? 0 : own.sortingOrder;
            if (order < bestOrder || (order == bestOrder && depth <= bestDepth))
                continue;

            if (!RectTransformUtility.RectangleContainsScreenPoint(graphic.rectTransform, screen, camera))
                continue;

            if (!graphic.Raycast(screen, camera))
                continue;

            best = graphic;
            bestOrder = order;
            bestDepth = depth;
        }

        if (best is null)
            return;

        var result = new RaycastResult
        {
            gameObject = best.gameObject,
            distance = distance,
            worldPosition = hit,
            screenPosition = screen,
            depth = bestDepth,
            sortingOrder = bestOrder,
        };

        if (raycaster is not null && raycaster != null)
            result.module = raycaster;

        target = best.gameObject;
        bestDistance = distance;
        bestScreen = screen;
        bestResult = result;
    }

    private static bool Plane(Transform panel, Vector3 origin, Vector3 direction, out float distance)
    {
        panel.GetPositionAndRotation(out var position, out var rotation);
        var normal = rotation * Vector3.forward;
        var facing = Vector3.Dot(direction, normal);
        distance = 0f;
        if (Mathf.Abs(facing) < 1e-4f)
            return false;

        distance = Vector3.Dot(position - origin, normal) / facing;
        return distance > 0.02f && distance < 20f;
    }

    private void EnsureEventObjects(GameState state)
    {
        var wanted = state.Viewport?.EventSystem;
        if (wanted is null || wanted == null)
            wanted = EventSystem.current;
        if (wanted is null || wanted == null)
            return;

        if (system is not null && system != null && system.Pointer == wanted.Pointer && data is not null)
            return;

        system = wanted;
        data = new PointerEventData(system) { pointerId = PointerId, button = PointerEventData.InputButton.Left };
        hovered = enterTarget = pressTarget = dragTarget = null;
        dragging = false;
    }

    private void Hover(GameObject? target)
    {
        var current = target;
        if (Same(current, hovered))
            return;

        hovered = current;

        var newEnter = current is null ? null : Handler(current, EnterType);
        if (Same(newEnter, enterTarget))
            return;

        if (enterTarget is not null && enterTarget != null)
        {
            var exit = Handler(enterTarget, ExitType);
            var handler = exit?.GetComponent(ExitType)?.TryCast<IPointerExitHandler>();
            if (handler is not null)
                ExecuteEvents.Execute(handler, data!);
        }

        enterTarget = newEnter;
        data!.pointerEnter = newEnter;

        if (newEnter is not null)
        {
            var handler = newEnter.GetComponent(EnterType)?.TryCast<IPointerEnterHandler>();
            if (handler is not null)
                ExecuteEvents.Execute(handler, data);
        }
    }

    private void Press(GameObject? target, Vector2 screen, RaycastResult? result)
    {
        Release();
        if (target is null)
            return;

        pressPosition = screen;
        pressFrame = Time.frameCount;
        data!.pressPosition = screen;
        if (result is not null)
            data.pointerPressRaycast = result;
        data.eligibleForClick = true;
        data.useDragThreshold = true;
        data.rawPointerPress = target;

        var down = Handler(target, DownType);
        var click = Handler(target, ClickType);
        pressTarget = down ?? click;
        data.pointerPress = pressTarget;
        data.pointerClick = click;

        if (down is not null)
        {
            var handler = down.GetComponent(DownType)?.TryCast<IPointerDownHandler>();
            if (handler is not null)
                ExecuteEvents.Execute(handler, data);
        }

        dragTarget = Handler(target, DragType);
        data.pointerDrag = dragTarget;
        dragging = false;

        if (Diag.Verbose && diagnosticsLogged < 30)
        {
            diagnosticsLogged++;
            Diagnose(target);
        }

        // The job complete screen's Replay Timelapse option (a prompt with no
        // button the pointer can press): replayed straight away.
        if (ReplayTimelapse(target))
        {
            pressTarget = null;
            return;
        }

        // The mod's own rows in the settings screen.
        if (VrSettings.Current?.Click(target) == true)
        {
            pressTarget = null;
            return;
        }

        // A prompt-bar entry (an action name with its glyph, no button of
        // its own) raises its action straight away.
        if (pressTarget is null)
            Prompt(target);

        if (Diag.Verbose && clicksLogged < 16)
        {
            clicksLogged++;
            log.Msg($"menu pointer: press on \"{target.name}\"  handler "
                + $"{(pressTarget is null ? "none" : $"\"{pressTarget.name}\"")}  button "
                + $"{(Button(target) is { } found ? found.GetIl2CppType().Name : "none")}"
                + $"{(Up<Il2CppFuturLab.FuturButton>(target) is { } futur ? $" (pointer events {(PointerEvents(futur) ? "on" : "off")})" : "")}");
        }
    }

    private void Drag()
    {
        if (dragTarget is null || dragTarget == null)
            return;

        if (!dragging)
        {
            // In eye pixels a steady hand still wobbles by dozens of pixels;
            // only a deliberate sweep (4 % of the view) starts a drag, so a
            // click on a button inside a scroll view stays a click. A slider
            // or scroll bar is dragged from the first movement.
            var direct = dragTarget.GetComponent<UnityEngine.UI.Slider>() is { } slider && slider != null
                || dragTarget.GetComponent<UnityEngine.UI.Scrollbar>() is { } bar && bar != null;
            var threshold = direct ? 2f : dragThreshold;
            if ((data!.position - pressPosition).sqrMagnitude < threshold * threshold)
                return;

            dragging = true;
            data.dragging = true;
            var begin = Handler(dragTarget, BeginDragType);
            var beginHandler = begin?.GetComponent(BeginDragType)?.TryCast<IBeginDragHandler>();
            if (beginHandler is not null)
                ExecuteEvents.Execute(beginHandler, data);
            data.eligibleForClick = false;
        }

        var handler = dragTarget.GetComponent(DragType)?.TryCast<IDragHandler>();
        if (handler is not null)
            ExecuteEvents.Execute(handler, data!);
    }

    private void Up(GameObject? target)
    {
        if (pressTarget is null || pressTarget == null)
        {
            Release();
            return;
        }

        var ups = 0;
        foreach (var component in All(pressTarget, UpType))
        {
            var up = component.TryCast<IPointerUpHandler>();
            if (up is null)
                continue;
            ExecuteEvents.Execute(up, data!);
            ups++;
        }

        // A click is a release over the same thing that was pressed. Every
        // click handler on it hears it, as with ExecuteEvents.Execute<T>.
        var click = target is null ? null : Handler(target, ClickType);
        var clicks = 0;
        var clickNote = "";
        if (click is null)
            clickNote = target is null ? "released over nothing" : $"no click handler under \"{target.name}\"";
        else if (!data!.eligibleForClick)
            clickNote = "not eligible (dragged)";
        else if (!Same(click, Handler(pressTarget, ClickType) ?? pressTarget))
            clickNote = $"released over another handler \"{click.name}\"";
        else
        {
            foreach (var component in All(click, ClickType))
            {
                var handler = component.TryCast<IPointerClickHandler>();
                if (handler is null)
                    continue;
                ExecuteEvents.Execute(handler, data);
                clicks++;
            }
        }


        // The game's context buttons (Start job, Back...) do not act on a
        // pointer click in this setup; they are completed here.
        var completed = dragging ? "drag, not completed" : Complete(pressTarget);

        if (Diag.Verbose && releasesLogged < 30)
        {
            releasesLogged++;
            log.Msg($"menu pointer: release on \"{(target is null ? "nothing" : target.name)}\" (pressed \"{pressTarget.name}\"): "
                + $"{ups} up handler(s), {clicks} click handler(s){(clickNote.Length > 0 ? $" [{clickNote}]" : "")}, {completed}");
        }

        if (dragging && dragTarget is not null && dragTarget != null)
        {
            var end = Handler(dragTarget, EndDragType);
            var endHandler = end?.GetComponent(EndDragType)?.TryCast<IEndDragHandler>();
            if (endHandler is not null)
                ExecuteEvents.Execute(endHandler, data!);
        }

        pressTarget = null;
        dragTarget = null;
        dragging = false;
        if (data is not null)
        {
            data.pointerPress = null;
            data.rawPointerPress = null;
            data.pointerDrag = null;
            data.dragging = false;
            data.eligibleForClick = false;
        }
    }

    private float nextScroll;
    private int activationsLogged;

    // Released over the same button that was pressed.
    private static bool SameButton(GameObject released, GameObject pressed)
    {
        var a = Button(released);
        var b = Button(pressed);
        return a is not null && b is not null && a.Pointer == b.Pointer;
    }

    private static Component? Button(GameObject node) =>
        (Component?)Up<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ClickableContextButton>(node)
        ?? Up<Il2CppFuturLab.FuturButton>(node);

    // The nearest T on the node or its parents (a plain walk: the generic
    // GetComponentInParent overloads are unreliable through the interop).
    private static T? Up<T>(GameObject node) where T : Component
    {
        var type = Il2CppInterop.Runtime.Il2CppType.Of<T>();
        var at = node.transform;
        for (var depth = 0; at is not null && at != null && depth < 10; depth++)
        {
            var found = at.GetComponent(type);
            if (found is not null && found != null)
                return found.TryCast<T>();
            at = at.parent;
        }

        return null;
    }

    private GameState? lastState;
    private string loggedState = "";

    // The game's own buttons switch their pointer events off unless it thinks
    // a mouse is in use, so a press that reached one is completed here:
    //
    //   context buttons (Start job, Back, the prompt bar)  their UI action is
    //       raised exactly as its bound key would raise it, matched by the
    //       button's action ID; failing that the button's own click handler
    //   other FuturButtons with pointer events off          Submit()
    private int diagnosticsLogged;
    private int releasesLogged;

    private static bool PointerEvents(Il2CppFuturLab.FuturButton button)
    {
        try
        {
            return button.m_pointerEventsEnabled;
        }
        catch
        {
            return true;
        }
    }

    private static readonly Il2CppSystem.Type ComponentType = Il2CppInterop.Runtime.Il2CppType.Of<Component>();

    // Every component on the object that implements the interface.
    private static List<Component> All(GameObject node, Il2CppSystem.Type type)
    {
        var list = new List<Component>();
        var found = node.GetComponents(type);
        if (found is null)
            return list;
        for (var index = 0; index < found.Length; index++)
        {
            var component = found[index];
            if (component is not null && component != null)
            {
                var behaviour = component.TryCast<Behaviour>();
                if (behaviour is null || behaviour.isActiveAndEnabled)
                    list.Add(component);
            }
        }

        return list;
    }

    // The clickable context button that owns this FuturButton: up the
    // hierarchy, else any in the scene whose button it is.
    private static Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ClickableContextButton? ContextOf(GameObject node,
        Il2CppFuturLab.FuturButton? futur, out string how)
    {
        how = "parent";
        var context = Up<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ClickableContextButton>(node);
        if (context is not null && context != null)
            return context;

        how = "scene";
        if (futur is null || futur == null)
            return null;

        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ClickableContextButton>());
        if (all is null)
            return null;

        for (var index = 0; index < all.Length; index++)
        {
            var candidate = all[index]?.TryCast<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ClickableContextButton>();
            if (candidate is null || candidate == null)
                continue;
            Il2CppFuturLab.FuturButton? own = null;
            try
            {
                own = candidate.m_button;
            }
            catch
            {
                // Field unreadable: compare by hierarchy instead.
            }

            if ((own is not null && own != null && own.Pointer == futur.Pointer)
                || futur.transform.IsChildOf(candidate.transform) || candidate.transform.IsChildOf(futur.transform))
                return candidate;
        }

        return null;
    }

    // What the release does beyond the pointer events:
    //   clickable context button   its click (the context widget hands it to
    //                              the screen), else its UI action by ID
    //   prompt entry (ContextButton) its UI action by ID
    //   FuturButton, pointer events off  Submit()
    private int pressFrame;

    // The social menu and the home screen's party bar: their buttons do not
    // react to the pointer at all.
    private static bool Social(Transform node)
    {
        for (var current = node; current is not null && current != null; current = current.parent)
        {
            var name = current.name;
            if (name.StartsWith("SocialMenu", StringComparison.Ordinal) || name.StartsWith("PartyContainer", StringComparison.Ordinal)
                || name.StartsWith("PartyMemberContainer", StringComparison.Ordinal) || name == "LeaveSessionButton" || name == "Invite_Button")
                return true;
        }

        return false;
    }

    // The dropdown box itself (e.g. the social menu's Session Type), not the
    // row it sits in (whose own button opens it with OnClick).
    private static bool Dropdown(Transform node) =>
        node.name.Contains("Dropdown", StringComparison.OrdinalIgnoreCase);

    private string Complete(GameObject pressed)
    {
        try
        {
            var futur = Up<Il2CppFuturLab.FuturButton>(pressed);
            if (futur is not null && futur != null)
            {
                // The game's button reacted to the pointer (on the press or the
                // release): done.
                if (Patches.LastClickedButton == futur.Pointer && Patches.LastClickedFrame >= pressFrame)
                    return "game button reacted (OnClick ran)";

                if (!futur.IsInteractable)
                    return "game button is not interactable (greyed out), left alone";

                // A button with the click sound tells for sure whether its
                // event ran (the sound hook sees every way it is raised):
                // not reacted means the pointer did not press it, so it is
                // pressed directly.
                var sound = futur.GetComponent<Il2CppFuturLab.PlayFmodEventOnButtonPress>();
                var soundKnown = sound is not null && sound != null && sound.m_button is { } own && own != null && own.Pointer == futur.Pointer;
                // A dropdown opens on the pointer's own click (outside the
                // button's OnClick): pressing it again would close it.
                if (soundKnown && (Dropdown(futur.transform) || Dropdown(pressed.transform)))
                    return "dropdown - pointer click only";
                if (soundKnown)
                {
                    futur.OnClick();
                    return $"OnClick() called directly - the pointer did not press it (Clicked {Listeners(futur.Clicked)})";
                }

                // A click-on-down button's pointer press runs its action
                // without OnClick (the hook cannot see it), so only buttons
                // known to ignore the pointer are pressed directly: those that
                // ignore pointer events, the context buttons, and the social
                // menu and party buttons (End Online Session, Invite...).
                if (!Patches.ButtonHookSeen && PointerEvents(futur) && !futur.name.Contains("CONTEXTBUTTON", StringComparison.OrdinalIgnoreCase)
                    && !Social(futur.transform))
                    return "plain game button, click hook not confirmed yet - pointer click only";
                if (Patches.ButtonHookSeen && PointerEvents(futur) && !Social(futur.transform)
                    && !futur.name.Contains("CONTEXTBUTTON", StringComparison.OrdinalIgnoreCase))
                    return "plain game button - pointer click only";

                // The click reached the pointer handlers but not the button
                // (its pointer events are off, or the UI Button refused it):
                // pressed the way the game's own submit path presses it.
                futur.OnClick();
                var reacted = Patches.LastClickedButton == futur.Pointer && Patches.LastClickedFrame == Time.frameCount;
                return $"OnClick() called directly (Clicked {Listeners(futur.Clicked)}){(reacted ? "" : " - OnClick hook did not see it")}";
            }

            var context = ContextOf(pressed, null, out var foundBy);
            if (context is not null && context != null)
            {
                context.OnButtonClicked();
                return $"context button ({foundBy}, action {context.ActionID}) clicked";
            }

            var prompt = Up<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ContextButton>(pressed);
            if (prompt is not null && prompt != null)
                return Raise(prompt);

            return "no game button";
        }
        catch (Exception exception)
        {
            return $"completion failed ({exception.GetType().Name}: {exception.Message})";
        }
    }

    private bool ReplayTimelapse(GameObject target)
    {
        try
        {
            var asked = false;
            var node = target.transform;
            for (var depth = 0; depth < 4 && node is not null && node != null && !asked; depth++, node = node.parent)
            {
                if (node.childCount > 12)
                    break;
                var texts = node.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(false);
                for (var index = 0; texts is not null && index < texts.Length && !asked; index++)
                {
                    var text = texts[index]?.text;
                    if (text is not null && text.Contains("replay", StringComparison.OrdinalIgnoreCase)
                        && (text.Contains("lapse", StringComparison.OrdinalIgnoreCase) || node.name.Contains("lapse", StringComparison.OrdinalIgnoreCase)
                            || text.Length < 24))
                        asked = true;
                }
            }

            if (!asked)
                return false;
            var screens = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.JobCompleteScreen>());
            for (var index = 0; screens is not null && index < screens.Length; index++)
            {
                var screen = screens[index]?.TryCast<Il2CppFuturLab.PW2.UI.JobCompleteScreen>();
                if (screen is null || screen == null || !screen.gameObject.activeInHierarchy)
                    continue;
                screen.RequestReplayTimelapse();
                log.Msg($"menu pointer: \"{target.name}\" - timelapse replay asked for");
                return true;
            }

            log.Msg($"menu pointer: \"{target.name}\" says replay, but no job complete screen is open");
        }
        catch (Exception exception)
        {
            log.Msg($"menu pointer: timelapse replay failed ({exception.GetType().Name}: {exception.Message})");
        }

        return false;
    }

    private void Prompt(GameObject target)
    {
        var prompt = Up<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ContextButton>(target);
        if (prompt is null || prompt == null)
            return;
        log.Msg($"menu pointer: prompt \"{target.name}\": {Raise(prompt)}");
    }

    private string Raise(Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ContextButton prompt)
    {
        var id = prompt.ActionID;
        return RaiseAction(id, out var name) ? $"prompt action {name} raised" : $"prompt with no matching UI action ({id})";
    }

    // Everything about what was pressed, for working out why a button does
    // nothing: the components up the hierarchy, the button's state and who
    // listens to it, and the UI actions with their IDs.
    private void Diagnose(GameObject target)
    {
        try
        {
            var lines = new List<string>();
            var at = target.transform;
            for (var depth = 0; at is not null && at != null && depth < 7; depth++)
            {
                var names = new List<string>();
                var components = at.GetComponents(ComponentType);
                for (var index = 0; components is not null && index < components.Length; index++)
                {
                    var component = components[index];
                    if (component is null || component == null)
                        continue;
                    var name = component.GetIl2CppType().Name;
                    if (name is "RectTransform" or "CanvasRenderer")
                        continue;
                    var behaviour = component.TryCast<Behaviour>();
                    names.Add(behaviour is not null && !behaviour.isActiveAndEnabled ? name + "(off)" : name);
                }

                lines.Add($"{new string(' ', depth)}\"{at.name}\"{(at.gameObject.activeInHierarchy ? "" : " (inactive)")}: {string.Join(", ", names)}");
                at = at.parent;
            }

            log.Msg($"menu pointer: diagnose \"{target.name}\" in state {lastState?.StateName ?? "?"}\n    " + string.Join("\n    ", lines));

            var futur = Up<Il2CppFuturLab.FuturButton>(target);
            if (futur is not null && futur != null)
            {
                var button = futur.m_button;
                var caption = "";
                try
                {
                    caption = futur.m_text is { } text && text != null ? text.text : "";
                }
                catch
                {
                    // No caption.
                }

                log.Msg($"menu pointer:   FuturButton \"{futur.name}\" caption \"{caption}\" interactable {futur.IsInteractable} "
                    + $"pointer events {PointerEvents(futur)} select events {futur.m_selectEventsEnabled} "
                    + $"click on down {futur.m_clickOnPointerDown} once {futur.m_canBePressedOnlyOnce} "
                    + $"Clicked {Listeners(futur.Clicked)}; UI Button "
                    + $"{(button is null || button == null ? "none" : $"\"{button.name}\" interactable {button.interactable} active {button.isActiveAndEnabled} onClick listeners {button.onClick.GetPersistentEventCount()} persistent")}");
            }

            var context = ContextOf(target, futur, out var how);
            if (context is not null && context != null)
                log.Msg($"menu pointer:   ClickableContextButton \"{context.name}\" found by {how}, action {context.ActionID}, "
                    + $"hidden {context.IsHidden}, Clicked {Listeners(context.Clicked)}");
            else
                log.Msg("menu pointer:   no ClickableContextButton owns it");

            var prompt = Up<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ContextButton>(target);
            if (prompt is not null && prompt != null)
                log.Msg($"menu pointer:   ContextButton \"{prompt.name}\" ({prompt.GetIl2CppType().Name}) action {prompt.ActionID}");

            if (!actionsLogged)
            {
                actionsLogged = true;
                var ui = lastState?.Viewport?.PlayerUIInput;
                if (ui is not null)
                {
                    foreach (var (name, action) in Actions(ui))
                        log.Msg($"menu pointer:   UI action {name}: {(action is null ? "none" : $"{action.ID} pressed {Listeners(action.Pressed)}")}");
                }
            }
        }
        catch (Exception exception)
        {
            log.Msg($"menu pointer: diagnose failed ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private bool actionsLogged;

    private static string Listeners(Il2CppSystem.Delegate? handler)
    {
        if (handler is null)
            return "none";
        try
        {
            var list = handler.GetInvocationList();
            var names = new List<string>();
            for (var index = 0; list is not null && index < list.Length; index++)
            {
                var entry = list[index];
                var method = entry?.Method;
                var owner = entry?.Target;
                names.Add($"{(owner is null ? "static" : owner.GetIl2CppType().Name)}.{method?.Name ?? "?"}");
            }

            return names.Count == 0 ? "none" : string.Join(" + ", names);
        }
        catch
        {
            return "some";
        }
    }

    private static (string Name, Il2CppFuturLab.PW2.UIAction? Action)[] Actions(Il2CppFuturLab.PW2.PwsPlayerUIInput ui) => new (string, Il2CppFuturLab.PW2.UIAction?)[]
    {
        ("Primary", ui.PrimaryAction), ("Secondary", ui.SecondaryAction), ("Tertiary", ui.TertiaryAction),
        ("Back", ui.BackAction), ("Cancel", ui.CancelAction), ("Submit", ui.SubmitAction),
        ("NextTab", ui.NextTabAction), ("PreviousTab", ui.PreviousTabAction),
        ("NextPage", ui.NextPageAction), ("PreviousPage", ui.PreviousPageAction),
        ("GameMenu", ui.GameMenuToggleAction), ("TaskList", ui.TaskListToggleAction),
    };

    private bool RaiseAction(Il2CppSystem.Guid id, out string name)
    {
        name = "";
        var ui = lastState?.Viewport?.PlayerUIInput;
        if (ui is null)
            return false;

        var wanted = id.ToString();
        var actions = Actions(ui);

        foreach (var (actionName, action) in actions)
        {
            if (action is null)
                continue;

            string actionId;
            try
            {
                actionId = action.ID.ToString();
            }
            catch
            {
                continue;
            }

            if (!string.Equals(actionId, wanted, StringComparison.Ordinal))
                continue;

            var pressed = action.Pressed;
            if (pressed is null)
                return false;

            pressed.Invoke();
            action.Released?.Invoke();
            name = actionName;
            return true;
        }

        return false;
    }

    private void Scroll(GameObject target, float amount)
    {
        var scroller = Handler(target, ScrollType);
        if (scroller is null)
            return;

        // Rate-limited: ScrollRect moves per event, not per second.
        if (Time.unscaledTime < nextScroll)
            return;
        nextScroll = Time.unscaledTime + 0.016f;

        data!.scrollDelta = new Vector2(0f, amount * 2.4f);
        var handler = scroller.GetComponent(ScrollType)?.TryCast<IScrollHandler>();
        if (handler is not null)
            ExecuteEvents.Execute(handler, data);
        data.scrollDelta = Vector2.zero;
    }

    private bool clipboardMode;
    private bool smoothValid, smoothRight;
    private Quaternion smoothRotation = Quaternion.identity;
    private Vector3 smoothOrigin;
    private UnityEngine.UI.ScrollRect? listScroll;
    private float nextListSearch;

    private void ScrollList(UiPresenter ui, float stick)
    {
        if (Mathf.Abs(stick) < 0.15f)
            return;

        if (Clipboard.List is { } clipboardList && clipboardList != null)
            listScroll = clipboardList;
        if ((listScroll is null || listScroll == null || !listScroll.isActiveAndEnabled) && Time.unscaledTime >= nextListSearch)
        {
            nextListSearch = Time.unscaledTime + 0.5f;
            listScroll = null;
            var root = ui.Root;
            var found = root is null ? null : root.GetComponentsInChildren<UnityEngine.UI.ScrollRect>(false);
            var bestHeight = 0f;
            for (var index = 0; found is not null && index < found.Length; index++)
            {
                var candidate = found[index];
                if (candidate is null || candidate == null || !candidate.vertical || candidate.content is null)
                    continue;
                var height = candidate.content.rect.height;
                if (height > bestHeight)
                {
                    bestHeight = height;
                    listScroll = candidate;
                }
            }
        }

        var list = listScroll;
        if (list is null || list == null || list.content is null)
            return;

        // About a screen per second at full tilt.
        var content = list.content.rect.height;
        var view = list.viewport is null ? content : list.viewport.rect.height;
        var range = Mathf.Max(1f, content - view);
        var step = stick * Time.unscaledDeltaTime * Mathf.Max(view, 200f) * 1.5f / range;
        list.verticalNormalizedPosition = Mathf.Clamp01(list.verticalNormalizedPosition + step);
    }

    // Ends any press in progress (menu closed, pointer lost).
    private void Release()
    {
        if (pressTarget is not null && pressTarget != null && data is not null)
        {
            var up = pressTarget.GetComponent(UpType)?.TryCast<IPointerUpHandler>();
            if (up is not null)
                ExecuteEvents.Execute(up, data);
        }

        if (enterTarget is not null && enterTarget != null && data is not null)
        {
            var exit = enterTarget.GetComponent(ExitType)?.TryCast<IPointerExitHandler>();
            if (exit is not null)
                ExecuteEvents.Execute(exit, data);
        }

        pressTarget = dragTarget = enterTarget = hovered = null;
        dragging = false;
    }

    // The nearest object up the hierarchy that handles the event.
    private static GameObject? Handler(GameObject start, Il2CppSystem.Type type)
    {
        var node = start.transform;
        for (var depth = 0; node is not null && node != null && depth < 16; depth++)
        {
            var component = node.GetComponent(type);
            if (component is not null && component != null)
            {
                var behaviour = component.TryCast<Behaviour>();
                if (behaviour is null || behaviour.isActiveAndEnabled)
                    return node.gameObject;
            }

            node = node.parent;
        }

        return null;
    }

    private static bool Same(UnityEngine.Object? a, UnityEngine.Object? b)
    {
        var aliveA = a is not null && a != null;
        var aliveB = b is not null && b != null;
        if (!aliveA || !aliveB)
            return aliveA == aliveB;
        return a!.Pointer == b!.Pointer;
    }

    private void SetVisible(bool show)
    {
        if (show == visible && (laser is not null || !show))
            return;

        visible = show;
        if (show && (laser is null || laser == null))
            Build();

        Props.SetActive(laser, show);
        Props.SetActive(dot, show);
    }

    private void Build()
    {
        laser = Props.Primitive(PrimitiveType.Cube, "PowerWashVR Laser", null, Props.Overlay(new Color(0.55f, 0.85f, 1f, 1f)));
        dot = Props.Primitive(PrimitiveType.Sphere, "PowerWashVR Cursor", null, Props.Overlay(Color.white));
        UnityEngine.Object.DontDestroyOnLoad(laser);
        UnityEngine.Object.DontDestroyOnLoad(dot);
        // World-space canvases sort by their sorting order (a dropdown's list
        // is its own canvas, sorted high): the laser and the dot sort above all.
        foreach (var part in new[] { laser, dot })
        {
            var renderer = part.GetComponent<Renderer>();
            if (renderer is null || renderer == null)
                continue;
            var layers = SortingLayer.layers;
            if (layers is not null && layers.Length > 0)
                renderer.sortingLayerID = layers[layers.Length - 1].id;
            renderer.sortingOrder = 32767;
        }
        laserTransform = laser.transform;
        dotTransform = dot.transform;
    }

    private void DrawLaser()
    {
        if (laserTransform is null || laserTransform == null || dotTransform is null)
            return;

        var span = rayEnd - rayOrigin;
        var length = span.magnitude;
        if (length < 1e-3f)
            return;

        laserTransform.SetPositionAndRotation(rayOrigin + span * 0.5f, Quaternion.LookRotation(span / length));
        laserTransform.localScale = new Vector3(0.003f, 0.003f, length);
        dotTransform.position = rayEnd;
        dotTransform.localScale = Vector3.one * (rayHit ? 0.027f : 0.015f);
    }
}
