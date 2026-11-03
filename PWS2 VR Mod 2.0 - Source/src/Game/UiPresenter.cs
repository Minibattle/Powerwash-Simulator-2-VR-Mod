using System.Linq;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace PowerWashVR.Game;

// Gets the game's UI into the headset.
//
// Screen-space overlay canvases are never drawn into an XR eye buffer. The
// game's root canvas ("UIRoot") becomes ONE world-space panel, permanently -
// it is never switched between render modes, which is what made the HUD
// flicker before. It is shown in front of you whenever a menu, the equipment
// wheel or a loading screen is up, and collapsed (scale 0) while you play: the
// only HUD you see then is the wrist display (WristHud), which takes its two
// widgets out of this canvas.
//
// Other overlay canvases (fades, popups that are not under UIRoot) are drawn
// a fixed distance in front of the eyes.
internal sealed class UiPresenter
{
    private const float ReferenceWidth = 1920f, ReferenceHeight = 1080f;
    private const int ZTestAlways = 8;

    private readonly MelonLogger.Instance log;

    private Canvas? root;
    private IntPtr convertedRoot;
    private IntPtr appliedCamera;
    private bool shown;
    private float nextSearch;
    private float nextCheck;
    private float nextDepthWalk;
    private float turnedAwaySince = -1f;

    private Vector3 menuPosition;
    private Quaternion menuRotation = Quaternion.identity;

    private readonly HashSet<IntPtr> depthTouched = new();
    private readonly List<Canvas> others = new();
    private readonly List<GraphicRaycaster?> otherRaycasters = new();
    private GraphicRaycaster? rootRaycaster;

    private static readonly string[] ZTestProperties = { "unity_GUIZTestMode", "_ZTestMode", "_ZTest" };

    internal UiPresenter(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal Canvas? Root => PlayerRig.Alive(root) ? root : null;
    internal GraphicRaycaster? RootRaycaster => PlayerRig.Alive(rootRaycaster) ? rootRaycaster : null;
    internal bool Shown => shown && Root is not null;
    internal IReadOnlyList<Canvas> Others => others;
    internal IReadOnlyList<GraphicRaycaster?> OtherRaycasters => otherRaycasters;

    // What the state's name says, worked out once per state (not per frame).
    private string? classifiedState;
    private bool stSplash, stLoading, stVoid, stJobComplete, stPackSelection;

    private void ClassifyState(string stateName)
    {
        if (ReferenceEquals(stateName, classifiedState))
            return;
        classifiedState = stateName;
        stSplash = stateName.Contains("Splash", StringComparison.Ordinal);
        stLoading = stateName.Contains("Loading", StringComparison.Ordinal);
        stVoid = stLoading || stSplash || stateName.Contains("Quitting", StringComparison.Ordinal)
            || stateName.Contains("Connecting", StringComparison.Ordinal) || stateName.Contains("StartPlaying", StringComparison.Ordinal);
        stJobComplete = stateName.Contains("JobComplete", StringComparison.Ordinal);
        stPackSelection = stateName.Contains("JobPackSelection", StringComparison.Ordinal);
    }

    internal void Update(PlayerRig rig, Settings settings, UiMode mode, GameInput input, string stateName)
    {
        ClassifyState(stateName);
        this.input = input;
        atHand = mode == UiMode.Radial && input.WheelAtHand != GameInput.Side.None;
        // Every menu screen is half size; the wheels in front, the splash and
        // the first loading screens keep full size.
        var starting = stateName.Length == 0 || stSplash || (stLoading && !seenGameplay);
        if (mode == UiMode.Gameplay)
            seenGameplay = true;
        // The panel is laid out at the game's own reference size (as on a
        // monitor), so menus, splash and loading screens all take the full
        // width (before, menus were halved to make up for a 1920-wide layout).
        menuWidth = settings.MenuWidth;
        taskList = Clipboard.Open;
        var camera = rig.Camera;
        if (camera is null)
            return;

        var now = Time.unscaledTime;
        // The blue void: loading screens, quitting, and the first arrival in
        // the world until the view has been recentered on the body.
        LoadingVoid(camera, stVoid || (mode == UiMode.Gameplay && !PlayerRig.FirstRecenterApplied && FirstArrival(now)));
        if (now >= nextTutorialHide)
        {
            nextTutorialHide = now + 2f;
            HideTutorials();
        }
        // The boot scene's ring, switched off: looked for from launch through
        // the first three loading screens only.
        if ((loadingScreens == 0 || loadingNow) && loadingScreens <= 3)
        {
            if (now >= nextSpinnerScan)
            {
                nextSpinnerScan = now + 0.1f;
                HidePrebootSpinner();
            }
        }
        else if (spinner is not null && loadingScreens >= 3 && !loadingNow)
        {
            // After the first three loading screens it is switched back on.
            if (spinner != null)
                spinner.SetActive(true);
            spinner = null;
            log.Msg("ui: the boot loading ring (Preboot/Spinner) switched back on");
        }

        if (Root is null && now >= nextSearch)
        {
            nextSearch = now + 2f;
            FindCanvases();
        }

        var canvas = Root;
        if (canvas is null)
        {
            // Before the game's main canvas exists (splash, first loading
            // screen) its other overlay canvases still need to reach the eyes.
            if (now >= nextCheck)
            {
                nextCheck = now + 0.5f;
                ConvertOthers(camera, settings);
            }

            return;
        }

        // Screens that bring their own root canvas (the job-complete screen
        // and others made on demand) appear after the first search.
        if (now >= nextOthersScan)
        {
            nextOthersScan = now + (mode == UiMode.Menu ? 1f : 3f);
            RescanOthers();
        }

        // The game may reset canvases on scene changes; check twice a second.
        if (now >= nextCheck)
        {
            nextCheck = now + 0.5f;
            if (canvas.renderMode != RenderMode.WorldSpace)
                convertedRoot = IntPtr.Zero;
            ConvertOthers(camera, settings);
            MuteRaycasters();
        }

        if (convertedRoot != canvas.Pointer || appliedCamera != camera.Pointer)
        {
            ConvertRoot(canvas, camera, settings);
            convertedRoot = canvas.Pointer;
            appliedCamera = camera.Pointer;
            shown = false;
            canvas.transform.localScale = Vector3.zero;
        }

        if (now >= nextReticleCheck)
        {
            nextReticleCheck = now + 10f;
            HideReticles(canvas);
        }

        // The job-complete banner comes up while you still play: shown where
        // the pause menu goes, with only the banner (and its prompt) visible.
        var banner = mode == UiMode.Gameplay ? JobBanner(now) : null;
        var wantShown = mode != UiMode.Gameplay || banner is not null;
        if (banner is not null)
            menuWidth = settings.MenuWidth;
        if (wantShown != shown)
        {
            shown = wantShown;
            if (shown)
            {
                PlaceInFront(rig, settings);
                needsPlacing = !HeadReal(rig);
                var scale = menuWidth / panelWidth;
                canvas.transform.localScale = new Vector3(scale, scale, scale);
                nextDepthWalk = 0f;
            }
            else
            {
                canvas.transform.localScale = Vector3.zero;
            }
        }

        if (!shown)
        {
            nextWheelPromptHide = 0f;
            Isolate((Transform?)null);
            return;
        }

        if (banner is not null)
            Isolate(banner);
        else if (taskList && Clipboard.List is { } list && list != null)
            Isolate(list.transform, true);
        else
            Isolate(mode == UiMode.Radial ? input.WheelRoot : null);

        if (mode == UiMode.Radial && now >= nextWheelPromptHide)
        {
            nextWheelPromptHide = now + 1f;
            HideWheelPrompts();
        }

        if (mode != UiMode.Menu)
            HideTooltips(mode == UiMode.Radial ? input.WheelRoot : null, now);

        if (mode == UiMode.Menu && now >= nextPromptHide)
        {
            nextPromptHide = now + 1f;
            // The job-complete screen's Continue lives in its prompt bar:
            // that one stays.
            if (stJobComplete)
                ShowPromptBars();
            else
                HidePromptBars();
        }

        // Placed again once the headset's pose is real (at the very start it
        // is still at the floor, which left the first loading screen there),
        // and for each loading screen, so it comes up where you look.
        var loading = stLoading || stSplash;
        if (loading && !wasLoading)
        {
            needsPlacing = true;
            loadingScreens++;
        }

        loadingNow = loading;
        wasLoading = loading;
        if (needsPlacing && !atHand && !taskList && HeadReal(rig))
        {
            needsPlacing = false;
            PlaceInFront(rig, settings);
            if (placeLogs++ < 6)
                log.Msg($"ui: panel placed in front ({stateName}, head {rig.HeadPosition.y:0.00} m up)");
        }



        if (loading && layoutLogs < 3 && now >= nextLayoutLog)
        {
            nextLayoutLog = now + 2f;
            layoutLogs++;
            LogLayout(canvas, stateName);
        }

        // Diagnostics: the pack select screen's packs (reported invisible).
        if (stPackSelection)
        {
            if (packLogAt < 0f)
                packLogAt = now + 1.5f;
            else if (packLogAt > 0f && now >= packLogAt && packLogs < 3)
            {
                packLogAt = 0f;
                packLogs++;
                LogPacks(canvas);
            }
        }
        else
        {
            packLogAt = -1f;
        }

        if (!atHand && !taskList)
            FollowIfTurnedAway(rig, settings, now);
        Place(rig);

        // Spread over frames: a whole-UI walk in one frame would be a visible
        // hitch in the headset.
        if (pending is null && now >= nextDepthWalk)
        {
            nextDepthWalk = now + 6f;
            pending = canvas.GetComponentsInChildren<Graphic>(false);
            pendingIndex = 0;
        }

        if (pending is not null)
            ForceDepthStep();
    }

    // Re-applies the panel pose with the newest head data (onBeforeRender).
    internal void Reapply(PlayerRig rig)
    {
        if (shown)
            Place(rig);
    }

    private void FindCanvases()
    {
        root = null;
        rootRaycaster = null;
        depthTouched.Clear();
        others.Clear();
        otherRaycasters.Clear();
        pending = null;
        convertedRoot = IntPtr.Zero;

        var all = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
        if (all is null)
            return;

        for (var index = 0; index < all.Length; index++)
        {
            var canvas = all[index];
            if (canvas is null || canvas == null || !canvas.isRootCanvas)
                continue;

            var name = canvas.name;
            if (name.StartsWith("PowerWashVR", StringComparison.Ordinal))
                continue;

            if (root is null && name.StartsWith("UIRoot", StringComparison.Ordinal))
            {
                root = canvas;
                rootRaycaster = canvas.GetComponent<GraphicRaycaster>();
                Props.UiLayer = canvas.gameObject.layer;
                log.Msg($"ui: root canvas \"{name}\" ({canvas.renderMode}), raycaster "
                    + $"{(rootRaycaster is null ? "none" : "found")}");
                continue;
            }

            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                others.Add(canvas);
                otherRaycasters.Add(canvas.GetComponent<GraphicRaycaster>());
            }
        }
    }

    // The size the game's UI is laid out for (its canvas scaler's reference
    // resolution); the panel is that many units across so everything sits
    // in it as it does on a monitor.
    private float panelWidth = ReferenceWidth, panelHeight = ReferenceHeight;

    private void ConvertRoot(Canvas canvas, Camera camera, Settings settings)
    {
        try
        {
            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler is not null && scaler != null)
            {
                var reference = scaler.referenceResolution;
                if (scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize && reference.x > 100f && reference.y > 100f)
                {
                    // At 16:9, whatever the scaler matches.
                    // The canvas height a 16:9 screen gets: the scaler mixes
                    // the width and height ratios in log space.
                    var match = Mathf.Clamp01(scaler.matchWidthOrHeight);
                    var aspect = ReferenceWidth / ReferenceHeight;
                    var height = Mathf.Pow(reference.x / aspect, 1f - match) * Mathf.Pow(reference.y, match);
                    if (scaler.screenMatchMode != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight)
                        height = scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.Expand
                            ? Mathf.Max(reference.y, reference.x / aspect) : Mathf.Min(reference.y, reference.x / aspect);
                    panelHeight = height;
                    panelWidth = height * ReferenceWidth / ReferenceHeight;
                }

                log.Msg($"ui: root canvas scaler {scaler.uiScaleMode}, reference {reference.x:0}x{reference.y:0}, match {scaler.matchWidthOrHeight:0.##}, "
                    + $"screen match {scaler.screenMatchMode}; panel laid out at {panelWidth:0}x{panelHeight:0}");
            }
        }
        catch (Exception exception)
        {
            log.Msg($"ui: canvas scaler not read ({exception.GetType().Name})");
        }

        ShowLayer(camera, canvas.gameObject.layer);
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = camera;

        var rect = canvas.transform.TryCast<RectTransform>();
        if (rect is not null)
        {
            rect.sizeDelta = new Vector2(panelWidth, panelHeight);
            rect.pivot = new Vector2(0.5f, 0.5f);
        }

        // The root rect is driven by the canvas; children keep their own scale.
        for (var index = 0; index < canvas.transform.childCount; index++)
        {
            var child = canvas.transform.GetChild(index);
            if (child is not null && child != null)
                child.localScale = Vector3.one;
        }
    }

    // In front of the eyes, level, a little below eye height. Stored in
    // tracking space, so the panel stays put while you look around but
    // travels with you if the game moves the camera.
    private void PlaceInFront(PlayerRig rig, Settings settings)
    {
        var facing = Quaternion.Euler(0f, PlayerRig.Yaw(rig.HeadRotation), 0f);
        menuPosition = rig.HeadPosition + facing * new Vector3(0f, -0.12f, settings.MenuDistance);
        menuRotation = facing;
        turnedAwaySince = -1f;
    }

    // A panel left behind you comes back after you have looked away for a second.
    private void FollowIfTurnedAway(PlayerRig rig, Settings settings, float now)
    {
        var headYaw = PlayerRig.Yaw(rig.HeadRotation);
        var panelYaw = PlayerRig.Yaw(menuRotation);
        if (Mathf.Abs(Mathf.DeltaAngle(headYaw, panelYaw)) < 70f)
        {
            turnedAwaySince = -1f;
            return;
        }

        if (turnedAwaySince < 0f)
            turnedAwaySince = now;
        else if (now - turnedAwaySince > 1f)
            PlaceInFront(rig, settings);
    }

    private float nextSpinnerScan;

    // ---- Loading: a blue void ----
    // While any loading screen is up only the UI is drawn, on the game's own
    // dark blue, so a level still loading behind the panel is never seen.
    // Held at most 5 s into the first time in the world (should the
    // recenter never come).
    private float firstArrivalAt = -1f;

    private bool FirstArrival(float now)
    {
        if (firstArrivalAt < 0f)
            firstArrivalAt = now;
        return now - firstArrivalAt < 5f;
    }

    internal static readonly Color VoidBlue = new(13f / 255f, 26f / 255f, 49f / 255f, 1f);
    private Camera? voidCamera;

    // The eye camera is held on the UI alone (others must not copy it now).
    internal static bool VoidActive { get; private set; }
    private int voidMask;
    private CameraClearFlags voidClear;
    private Color voidColor;
    private bool voidLogged;

    private void LoadingVoid(Camera camera, bool loading)
    {
        if (voidCamera is not null && (!loading || voidCamera == null || voidCamera.Pointer != camera.Pointer))
        {
            // Loading over, or the game changed cameras: the old one back.
            if (voidCamera != null)
            {
                // Its own layers back, plus the UI's: a camera the game
                // switched to during loading had the UI added while it was
                // held on the UI alone, so its saved mask lacks it.
                var restored = voidMask | (1 << Props.UiLayer);
                foreach (var other in others)
                {
                    if (other is not null && other != null)
                        restored |= 1 << other.gameObject.layer;
                }

                voidCamera.cullingMask = restored;
                voidCamera.clearFlags = voidClear;
                voidCamera.backgroundColor = voidColor;
            }

            voidCamera = null;
        }

        VoidActive = loading;
        if (!loading)
            return;
        var mask = 1 << Props.UiLayer;
        if (voidCamera is null)
        {
            voidCamera = camera;
            voidMask = camera.cullingMask;
            voidClear = camera.clearFlags;
            voidColor = camera.backgroundColor;
            if (!voidLogged)
            {
                voidLogged = true;
                log.Msg("ui: loading - only the loading screen is drawn, on blue");
            }
        }

        if (camera.cullingMask != mask)
            camera.cullingMask = mask;
        if (camera.clearFlags != CameraClearFlags.SolidColor)
            camera.clearFlags = CameraClearFlags.SolidColor;
        if (camera.backgroundColor != VoidBlue)
            camera.backgroundColor = VoidBlue;
    }

    // ---- The base game's tutorials and hints: never shown in VR ----
    private float nextTutorialHide;
    private Transform? tutorialToast;
    private float nextTutorialSearch;
    private readonly List<Transform> tutorialWidgets = new();

    private void HideTutorials()
    {
        try
        {
            // Looked up again every 10 s at most (the HUD is rebuilt per level).
            var search = Time.unscaledTime >= nextTutorialSearch;
            if (search)
            {
                nextTutorialSearch = Time.unscaledTime + 10f;
                tutorialWidgets.Clear();
                var widgets = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.Widgets.HUD.TutorialWidget>());
                for (var index = 0; widgets is not null && index < widgets.Length; index++)
                {
                    var widget = widgets[index]?.TryCast<Il2CppFuturLab.PW2.UI.Widgets.HUD.TutorialWidget>();
                    if (widget is not null && widget != null)
                        tutorialWidgets.Add(widget.transform);
                }

                if ((tutorialToast is null || tutorialToast == null) && Root is { } canvas)
                    tutorialToast = Deep(canvas.transform, "NotificationBox_Tutorial");
            }

            foreach (var widget in tutorialWidgets)
            {
                if (widget is not null && widget != null && widget.localScale != Vector3.zero)
                    widget.localScale = Vector3.zero;
            }

            if (tutorialToast is not null && tutorialToast != null && tutorialToast.localScale != Vector3.zero)
                tutorialToast.localScale = Vector3.zero;
        }
        catch
        {
        }
    }

    private static Transform? Deep(Transform node, string name)
    {
        if (node.name == name)
            return node;
        for (var index = 0; index < node.childCount; index++)
        {
            var found = Deep(node.GetChild(index), name);
            if (found is not null)
                return found;
        }

        return null;
    }
    private bool prebootHidden, loadingNow;
    private int loadingScreens;
    private GameObject? spinner;

    // The ring: the boot scene's "Preboot/Spinner" (a mesh on the UI layer,
    // drawn below the loading panel in VR). Switched off while it exists.
    private void HidePrebootSpinner()
    {
        try
        {
            var found = GameObject.Find("Preboot/Spinner");
            if (found is null || found == null || !found.activeSelf)
                return;
            found.SetActive(false);
            spinner = found;
            if (!prebootHidden)
            {
                prebootHidden = true;
                log.Msg("ui: the boot loading ring (Preboot/Spinner) switched off");
            }
        }
        catch
        {
        }
    }

    private int layoutLogs;
    private float nextLayoutLog;

    // Diagnostics: what a splash / loading screen is made of (sizes in the
    // panel's units), to see what does not fit.
    private void LogLayout(Canvas canvas, string stateName)
    {
        try
        {
            var parts = new List<string>();
            var rects = canvas.GetComponentsInChildren<RectTransform>(false);
            for (var index = 0; rects is not null && index < rects.Length && parts.Count < 40; index++)
            {
                var rect = rects[index];
                if (rect is null || rect == null || rect.Pointer == canvas.transform.Pointer)
                    continue;
                var size = rect.rect.size;
                var scale = rect.lossyScale.x / Mathf.Max(1e-9f, canvas.transform.lossyScale.x);
                if (size.x * scale < 300f && size.y * scale < 300f)
                    continue;
                parts.Add($"{rect.name} {size.x * scale:0}x{size.y * scale:0}");
            }

            log.Msg($"ui: {stateName} layout (panel {panelWidth:0}x{panelHeight:0}): {string.Join(", ", parts)}");
        }
        catch
        {
        }
    }

    private float packLogAt = -1f;
    private int packLogs;

    private void LogPacks(Canvas canvas)
    {
        try
        {
            var parts = new List<string>();
            var scale = Mathf.Max(1e-9f, canvas.transform.lossyScale.x);
            foreach (var button in canvas.GetComponentsInChildren<Il2CppFuturLab.FuturButton>(true))
            {
                if (button is null || button == null || !button.name.Contains("Pack", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rect = button.transform.TryCast<RectTransform>();
                var alpha = 1f;
                for (var node = button.transform; node is not null && node != null; node = node.parent)
                {
                    var group = node.GetComponent<CanvasGroup>();
                    if (group is not null && group != null)
                        alpha *= group.alpha;
                }

                var at = canvas.transform.InverseTransformPoint(button.transform.position);
                var size = rect is null ? Vector2.zero : rect.rect.size * (rect.lossyScale.x / scale);
                var graphics = button.GetComponentsInChildren<Graphic>(false);
                var visible = 0;
                for (var index = 0; graphics is not null && index < graphics.Length; index++)
                {
                    var graphic = graphics[index];
                    if (graphic is not null && graphic != null && graphic.enabled && graphic.color.a > 0.01f && !graphic.canvasRenderer.cull)
                        visible++;
                }

                parts.Add($"{button.name}[{(button.gameObject.activeInHierarchy ? "on" : "off")} at {at.x:0},{at.y:0},{at.z:0} {size.x:0}x{size.y:0} alpha {alpha:0.00} {visible} graphic(s)]");
            }

            log.Msg($"ui: pack select: {(parts.Count == 0 ? "no pack buttons" : string.Join(", ", parts.Take(12)))}");
        }
        catch (Exception exception)
        {
            log.Msg($"ui: pack select not read ({exception.GetType().Name})");
        }
    }

    private bool needsPlacing = true, wasLoading;
    private int placeLogs;

    // A tracked head well off the floor (before tracking starts the pose is
    // the play-space origin).
    private static bool HeadReal(PlayerRig rig) => rig.HasPose && rig.HeadPosition.y > 0.4f;

    private GameInput? input;
    private bool atHand;
    private bool seenGameplay;
    private bool taskList;
    private float menuWidth = 1.7f;
    private const float WheelPanelWidth = 0.475f;

    private void Place(PlayerRig rig)
    {
        var canvas = Root;
        if (canvas is null)
            return;

        if (!rig.HasPose)
            return;

        // The nozzle and extension wheels float in front of their hand: the
        // whole panel is moved and shrunk so the wheel's centre lands there.
        var transform = canvas.transform;
        var wheel = input?.WheelTransform;
        if (atHand && input!.WheelTargetValid && wheel is not null)
        {
            var scale = WheelPanelWidth / ReferenceWidth;
            transform.localScale = new Vector3(scale, scale, scale);
            var local = transform.InverseTransformPoint(wheel.position);
            var rotation = input.WheelTargetRotation;
            transform.SetPositionAndRotation(input.WheelTargetPosition - rotation * (local * scale), rotation);
            return;
        }

        // The task list on the clipboard: the list's own view is fitted to
        // the board and everything else of the screen is collapsed.
        if (taskList && Clipboard.List is { } list && list != null)
        {
            var view = list.viewport is not null && list.viewport != null ? list.viewport : list.transform.TryCast<RectTransform>();
            if (view is not null)
            {
                var rect = view.rect;
                var centre = transform.InverseTransformPoint(view.TransformPoint(rect.center));
                var width = transform.InverseTransformVector(view.TransformVector(new Vector3(rect.width, 0f, 0f))).magnitude;
                var height = transform.InverseTransformVector(view.TransformVector(new Vector3(0f, rect.height, 0f))).magnitude;
                if (width > 1f && height > 1f)
                {
                    var scale = Mathf.Min(Clipboard.BoardWidth * 0.9f / width, Clipboard.BoardHeight * 0.82f / height);
                    transform.localScale = new Vector3(scale, scale, scale);
                    var rotation = Clipboard.BoardRotation;
                    // A little below the clip.
                    var target = Clipboard.BoardPosition - rotation * new Vector3(0f, 0.02f, 0f);
                    transform.SetPositionAndRotation(target - rotation * (centre * scale), rotation);
                    return;
                }
            }
        }

        var menuScale = menuWidth / panelWidth;
        if (Mathf.Abs(transform.localScale.x - menuScale) > 1e-6f)
            transform.localScale = new Vector3(menuScale, menuScale, menuScale);

        var toWorld = rig.TrackingToWorldRotation;
        canvas.transform.SetPositionAndRotation(rig.TrackingToWorldPosition + toWorld * menuPosition,
            toWorld * menuRotation);
    }

    private readonly Dictionary<IntPtr, List<(Transform Node, Vector3 Scale)>> otherScales = new();

    private void ConvertOthers(Camera camera, Settings settings)
    {
        for (var index = others.Count - 1; index >= 0; index--)
        {
            var canvas = others[index];
            if (canvas is null || canvas == null)
                continue;

            // A canvas caught as a root while it was being made (the gameplay
            // HUD, before it is put under UIRoot) is not one: give it back.
            if (!canvas.isRootCanvas || UnderRoot(canvas))
            {
                Release(canvas);
                others.RemoveAt(index);
                otherRaycasters.RemoveAt(index);
                continue;
            }

            if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera is not null
                && canvas.worldCamera.Pointer == camera.Pointer)
                continue;

            ShowLayer(camera, canvas.gameObject.layer);
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = settings.MenuDistance * 0.9f;

            // Shrunk so the edges of a full-screen canvas sit inside the view.
            if (!otherScales.TryGetValue(canvas.Pointer, out var saved))
            {
                saved = new List<(Transform, Vector3)>();
                otherScales[canvas.Pointer] = saved;
                for (var child = 0; child < canvas.transform.childCount; child++)
                {
                    var node = canvas.transform.GetChild(child);
                    if (node is not null && node != null)
                        saved.Add((node, node.localScale));
                }
            }

            foreach (var (node, _) in saved)
            {
                if (node is not null && node != null)
                    node.localScale = new Vector3(0.6f, 0.6f, 1f);
            }
        }
    }

    private bool UnderRoot(Canvas canvas)
    {
        var main = Root;
        if (main is null)
            return false;
        var top = canvas.transform.root;
        return top is not null && top != null && top.Pointer == main.transform.Pointer;
    }

    private void Release(Canvas canvas)
    {
        if (!otherScales.Remove(canvas.Pointer, out var saved))
            return;
        foreach (var (node, scale) in saved)
        {
            if (node is not null && node != null)
                node.localScale = scale;
        }

        log.Msg($"ui: \"{canvas.name}\" is part of the main UI again; left as it was");
    }


    private readonly List<(Transform Node, CanvasGroup? Group)> bannerWidgets = new();
    private float nextBannerPoll;
    private Transform[]? bannerShown;

    // The job-complete banner and its prompt are HUD widgets that fade in
    // (their canvas group) while you still play. Polled four times a second
    // during gameplay; the widgets are looked up again every few seconds
    // until found.
    private Transform[]? JobBanner(float now)
    {
        if (now < nextBannerPoll)
            return bannerShown;
        nextBannerPoll = now + 0.25f;

        bannerWidgets.RemoveAll(entry => entry.Node is null || entry.Node == null);
        if (bannerWidgets.Count == 0 && now >= nextBannerSearch)
        {
            nextBannerSearch = now + 3f;
            try
            {
                var banners = UnityEngine.Object.FindObjectsOfType(
                    Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.Widgets.HUD.JobCompletionBannerWidget>());
                for (var index = 0; banners is not null && index < banners.Length; index++)
                {
                    var widget = banners[index]?.TryCast<Il2CppFuturLab.PW2.UI.Widgets.HUD.JobCompletionBannerWidget>();
                    if (widget is not null && widget != null)
                        bannerWidgets.Add((widget.transform, widget.m_canvasGroup));
                }

                // The "Job finished - press to continue" prompt is left out:
                // only the banner is shown.
            }
            catch
            {
                // Next search.
            }
        }

        List<Transform>? up = null;
        foreach (var (node, group) in bannerWidgets)
        {
            if (!node.gameObject.activeInHierarchy)
                continue;
            var alpha = group is not null && group != null ? group.alpha : 1f;
            if (alpha > 0.02f)
                (up ??= new List<Transform>()).Add(node);
        }

        var was = bannerShown is not null;
        // A banner that never fades is not the job-complete moment (left
        // visible by the HUD): after 15 s it is no longer brought in front.
        if (up is null)
        {
            bannerSince = -1f;
        }
        else
        {
            if (bannerSince < 0f)
                bannerSince = now;
            if (now - bannerSince > 15f)
                up = null;
        }

        bannerShown = up?.ToArray();
        if (bannerShown is not null && !was && bannerLogs++ < 5)
            log.Msg($"ui: job complete banner up, shown in front ({string.Join(", ", bannerShown.Select(part => part.name))})");
        return bannerShown;
    }

    private float nextBannerSearch;
    private int bannerLogs;
    private float bannerSince = -1f;

    private float nextReticleCheck;
    private float nextOthersScan;

    private void RescanOthers()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            for (var index = 0; all is not null && index < all.Length; index++)
            {
                var canvas = all[index];
                if (canvas is null || canvas == null || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    continue;
                if (canvas.name.StartsWith("PowerWashVR", StringComparison.Ordinal) || (root is not null && canvas.Pointer == root.Pointer)
                    || UnderRoot(canvas) || canvas.transform.parent is not null)
                    continue;
                if (others.Any(known => known is not null && known != null && known.Pointer == canvas.Pointer))
                    continue;
                others.Add(canvas);
                otherRaycasters.Add(canvas.GetComponent<GraphicRaycaster>());
                log.Msg($"ui: new screen canvas \"{canvas.name}\" brought into view");
            }
        }
        catch
        {
            // Next scan.
        }
    }
    private float nextPromptHide;
    private float nextWheelPromptHide;
    private bool wheelPromptsLogged;

    // The wheels' "Release to select" and "/ Change ..." prompts name flat
    // controls: collapsed while a wheel is up (looked for once a second).
    private void HideWheelPrompts()
    {
        var count = 0;
        try
        {
            foreach (var type in new[]
            {
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuSelectPrompt>(),
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuSubOptionPrompt>(),
            })
            {
                var all = UnityEngine.Object.FindObjectsOfType(type);
                for (var index = 0; all is not null && index < all.Length; index++)
                {
                    var component = all[index]?.TryCast<Component>();
                    if (component is null || component == null || component.transform.localScale == Vector3.zero)
                        continue;
                    component.transform.localScale = Vector3.zero;
                    count++;
                }
            }
        }
        catch
        {
            // Next second.
        }

        if (count > 0 && !wheelPromptsLogged)
        {
            wheelPromptsLogged = true;
            log.Msg($"ui: {count} wheel prompt(s) hidden");
        }
    }
    // Tooltips (the wheels' info boxes and the like) describe flat controls
    // and float in the wrong place in VR: kept collapsed. The wheel is looked
    // over when its hierarchy changes; info box widgets anywhere once a
    // second; the found ones are held at zero scale every frame.
    private readonly List<Transform> tooltips = new();
    private readonly HashSet<IntPtr> tooltipKnown = new();
    private int tooltipHierarchy = -1;
    private IntPtr tooltipRoot;
    private float nextTooltipScan;
    private int tooltipLogs;
    private bool wheelTreeLogged;

    private static bool TooltipName(string name) =>
        name.Contains("tooltip", StringComparison.OrdinalIgnoreCase) || name.Contains("tool_tip", StringComparison.OrdinalIgnoreCase)
        || name.Contains("infobox", StringComparison.OrdinalIgnoreCase) || name.Contains("info_box", StringComparison.OrdinalIgnoreCase)
        || name.Contains("infopanel", StringComparison.OrdinalIgnoreCase) || name.Contains("info panel", StringComparison.OrdinalIgnoreCase)
        || name.Contains("hintbox", StringComparison.OrdinalIgnoreCase);

    private void AddTooltip(Transform node, string why)
    {
        if (!tooltipKnown.Add(node.Pointer))
            return;
        tooltips.Add(node);
        if (tooltipLogs++ < 20)
            log.Msg($"ui: tooltip \"{node.name}\" hidden ({why})");
    }

    private void HideTooltips(Transform? wheel, float now)
    {
        try
        {
            if (wheel is not null && wheel != null)
            {
                var size = wheel.hierarchyCount;
                if (size != tooltipHierarchy || wheel.Pointer != tooltipRoot)
                {
                    tooltipHierarchy = size;
                    tooltipRoot = wheel.Pointer;
                    var nodes = wheel.GetComponentsInChildren<Transform>(true);
                    if (!wheelTreeLogged && nodes is not null)
                    {
                        wheelTreeLogged = true;
                        var names = new List<string>();
                        for (var index = 0; index < nodes.Length && names.Count < 150; index++)
                        {
                            if (nodes[index] is { } n && n != null)
                                names.Add(n.name);
                        }

                        log.Msg($"ui: wheel parts: {string.Join(", ", names.Distinct())}");
                    }

                    for (var index = 0; nodes is not null && index < nodes.Length; index++)
                    {
                        var node = nodes[index];
                        if (node is not null && node != null && TooltipName(node.name))
                            AddTooltip(node, "wheel");
                    }
                }
            }

            if (now >= nextTooltipScan)
            {
                nextTooltipScan = now + 1f;
                var boxes = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.InfoBoxWidget>());
                for (var index = 0; boxes is not null && index < boxes.Length; index++)
                {
                    var box = boxes[index]?.TryCast<Component>();
                    if (box is not null && box != null)
                        AddTooltip(box.transform, "info box");
                }

                tooltips.RemoveAll(node => node is null || node == null);
            }

            for (var index = 0; index < tooltips.Count; index++)
            {
                var node = tooltips[index];
                if (node is not null && node != null && node.localScale != Vector3.zero)
                    node.localScale = Vector3.zero;
            }
        }
        catch
        {
            // Next frame.
        }
    }

    private int raycastersMuted;

    // The game's own UI raycasting follows a pointer that, with the mouse
    // gone, sits at the middle of the view: the head would hover and select
    // menu items. The laser does its own hit testing, so the game's
    // raycasters are switched off.
    private float nextMute;

    private void MuteRaycasters()
    {
        if (Time.unscaledTime < nextMute)
            return;
        nextMute = Time.unscaledTime + 2f;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<GraphicRaycaster>());
            var muted = 0;
            for (var index = 0; all is not null && index < all.Length; index++)
            {
                var raycaster = all[index]?.TryCast<GraphicRaycaster>();
                if (raycaster is null || raycaster == null || !raycaster.enabled)
                    continue;
                raycaster.enabled = false;
                muted++;
            }

            if (muted > 0 && raycastersMuted++ < 5)
                log.Msg($"ui: {muted} game UI raycaster(s) switched off (the head no longer hovers menus)");
        }
        catch
        {
            // Next check.
        }
    }
    private bool promptsLogged;

    // The menus' prompt bar (Close, Select, Reset Pack Progress, View
    // Messages...) names keys and pad buttons; in VR the laser does all of
    // it, so the bar is gone.
    private readonly List<Transform> hiddenBars = new();

    private void ShowPromptBars()
    {
        foreach (var bar in hiddenBars)
        {
            if (bar is not null && bar != null && bar.localScale == Vector3.zero)
                bar.localScale = Vector3.one;
        }

        hiddenBars.Clear();
    }

    private void HidePromptBars()
    {
        try
        {
            var bars = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.CommonContextButtonWidget>());
            var count = 0;
            for (var index = 0; bars is not null && index < bars.Length; index++)
            {
                var bar = bars[index]?.TryCast<Component>();
                if (bar is null || bar == null || bar.transform.localScale == Vector3.zero)
                    continue;
                bar.transform.localScale = Vector3.zero;
                hiddenBars.Add(bar.transform);
                count++;
            }

            if (count > 0 && !promptsLogged)
            {
                promptsLogged = true;
                log.Msg($"ui: {count} menu prompt bar(s) hidden");
            }
        }
        catch
        {
            // Try again next second.
        }
    }

    // While a wheel is up, the rest of the gameplay HUD (stance indicator,
    // prompts, equipment readouts) is collapsed so only the wheel shows.
    private readonly List<(Transform Node, Vector3 Scale)> isolated = new();
    private IntPtr isolatedFor;

    private void Isolate(Transform? wheel, bool toRoot = false) =>
        Isolate(wheel is null || wheel == null ? Array.Empty<Transform>() : new[] { wheel }, toRoot);

    private void Isolate(Transform[] keep, bool toRoot = false)
    {
        var pointer = IntPtr.Zero;
        foreach (var node in keep)
            pointer = (IntPtr)((long)pointer * 31 + (long)node.Pointer);
        if (pointer == isolatedFor)
            return;

        foreach (var (node, scale) in isolated)
        {
            if (node is not null && node != null)
                node.localScale = scale;
        }

        isolated.Clear();
        isolatedFor = pointer;
        if (pointer == IntPtr.Zero)
            return;

        // Walk up from each kept item to the gameplay HUD screen (or the root),
        // collapsing every sibling along the way.
        var path = new HashSet<IntPtr>();
        var tops = new List<(Transform Start, Transform Top)>();
        foreach (var start in keep)
        {
            for (var node = start; node is not null && node != null; node = node.parent)
            {
                path.Add(node.Pointer);
                if (toRoot ? node.parent is null || node.parent == null || (Root is { } canvasRoot && node.Pointer == canvasRoot.transform.Pointer)
                        : node.name.StartsWith("Screen_GameplayHUD", StringComparison.Ordinal))
                {
                    tops.Add((start, node));
                    break;
                }
            }
        }

        var done = new HashSet<IntPtr>();
        foreach (var (start, top) in tops)
        {
            for (var node = start.parent; node is not null && node != null; node = node.parent)
            {
                if (done.Add(node.Pointer))
                {
                    for (var index = 0; index < node.childCount; index++)
                    {
                        var child = node.GetChild(index);
                        if (child is null || child == null || path.Contains(child.Pointer)
                            || child.name.Contains("Radial", StringComparison.OrdinalIgnoreCase))
                            continue;
                        isolated.Add((child, child.localScale));
                        child.localScale = Vector3.zero;
                    }
                }

                if (node.Pointer == top.Pointer)
                    break;
            }
        }
    }

    // The flat game's crosshair (and its interaction reticle) are gone for
    // good in VR: you aim with the washer itself.
    private void HideReticles(Canvas canvas)
    {
        try
        {
            var widgets = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.ReticleWidget>());
            if (widgets is not null)
            {
                for (var index = 0; index < widgets.Length; index++)
                {
                    var widget = widgets[index]?.TryCast<Component>();
                    if (widget is not null && widget != null && widget.transform.localScale != Vector3.zero)
                        widget.transform.localScale = Vector3.zero;
                }
            }
        }
        catch
        {
            // Name search below still applies.
        }

        HideNamed(canvas.transform, 0);
        foreach (var other in others)
        {
            if (other is not null && other != null)
                HideNamed(other.transform, 0);
        }
    }

    private static void HideNamed(Transform node, int depth)
    {
        if (depth > 10)
            return;

        for (var index = 0; index < node.childCount; index++)
        {
            var child = node.GetChild(index);
            if (child is null || child == null)
                continue;

            var name = child.name;
            if (name.Contains("Reticle", StringComparison.OrdinalIgnoreCase) || name.Contains("Crosshair", StringComparison.OrdinalIgnoreCase))
            {
                if (child.localScale != Vector3.zero)
                    child.localScale = Vector3.zero;
                continue;
            }

            HideNamed(child, depth + 1);
        }
    }

    private static void ShowLayer(Camera camera, int layer)
    {
        var bit = 1 << layer;
        var mask = camera.cullingMask;
        if ((mask & bit) == 0)
            camera.cullingMask = mask | bit;
    }

    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Graphic>? pending;
    private int pendingIndex;

    // Menus are drawn over the world, so a wall close to the face cannot cut
    // through the panel.
    private void ForceDepthStep()
    {
        var graphics = pending!;
        var end = Math.Min(pendingIndex + 40, graphics.Length);

        for (var index = pendingIndex; index < end; index++)
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

            if (material is null || material == null || !depthTouched.Add(material.Pointer))
                continue;

            for (var property = 0; property < ZTestProperties.Length; property++)
            {
                try
                {
                    if (material.HasProperty(ZTestProperties[property]))
                        material.SetInt(ZTestProperties[property], ZTestAlways);
                }
                catch
                {
                    // Not every UI shader has every name.
                }
            }
        }

        pendingIndex = end;
        if (pendingIndex >= graphics.Length)
            pending = null;
    }
}
