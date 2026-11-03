using MelonLoader;
using PowerWashVR.Game;
using PowerWashVR.Input;
using PowerWashVR.OpenXR;
using UnityEngine;

[assembly: MelonInfo(typeof(PowerWashVR.Mod), "PowerWash VR", PowerWashVR.Mod.Version, "PowerWashVR")]
[assembly: MelonGame("FuturLab", "PowerWash Simulator 2")]
[assembly: HarmonyDontPatchAll]

namespace PowerWashVR;

// PowerWash Simulator 2 in VR.
//
// Frame order, and why:
//
//   Update                                  controller buttons -> the game's own
//                                           player input object; housekeeping
//                                           on timers
//   LateUpdate                              poses -> camera, washer, wash jets,
//                                           hands, belt, backpack, menus
//   onBeforeRender                          OpenXR message pump, then poses
//                                           re-read and everything that follows
//                                           a controller re-applied with the
//                                           prediction for the frame being drawn
//   camera pre-render                       washer re-asserted after the game's
//                                           own pre-render adjustments
//
// Nothing searches the scene per frame; lookups run on timers and only while
// something is missing.
public sealed class Mod : MelonMod
{
    public const string Version = "2.0";

    private Settings settings = null!;
    private XrSession session = null!;
    private readonly Controllers controllers = new();
    private readonly Haptics haptics = new();
    private readonly BodyFrame body = new();
    private PlayerRig rig = null!;
    private WasherFixes washer = null!;
    private UiPresenter ui = null!;
    private RenderTweaks render = null!;
    private GameState state = null!;
    private GameInput input = null!;
    private MenuPointer pointer = null!;
    private WristHud wrist = null!;
    private Hands hands = null!;
    private Belt belt = null!;
    private Backpack backpack = null!;
    private Mirror mirror = null!;
    private BodyHider bodyHider = null!;

    private bool running;
    private System.Action? beforeRender;

    // Auto start: wait until the game has settled, measured in frames.
    private int framesSeen;
    private float firstFrameTime = -1f;
    private readonly float[] recentFrames = new float[30];
    private bool autoStartDone;
    private float reportAt = -1f;

    // Auto Detect SteamVR: with SteamVR not running at startup the mod does
    // nothing at all for the session (no patches, no hooks, no settings).
    private bool inert;

    private static bool SteamVrRunning()
    {
        foreach (var name in new[] { "vrserver", "vrmonitor", "vrcompositor" })
        {
            try
            {
                var found = System.Diagnostics.Process.GetProcessesByName(name);
                var any = found.Length > 0;
                foreach (var process in found)
                    process.Dispose();
                if (any)
                    return true;
            }
            catch
            {
                // Try the next name.
            }
        }

        return false;
    }

    public override void OnInitializeMelon()
    {
        settings = new Settings();
        flatSettings = new FlatSettings(LoggerInstance);
        bodySync = new BodySync(LoggerInstance);
        // Other players' washers, put in the right hand just before drawing
        // (VR or not).
        bodyBeforeRender = new System.Action(() =>
        {
            try { bodySync.BeforeRender(); } catch { }
        });
        Application.add_onBeforeRender(bodyBeforeRender);
        vrSettings = new VrSettings(LoggerInstance);
        if (settings.AutoDetectSteamVR && !SteamVrRunning())
        {
            inert = true;
            Patches.InstallPacks(HarmonyInstance, LoggerInstance);
            LoggerInstance.Msg($"PowerWash VR {Version}: SteamVR is not running and Auto Detect SteamVR is on - "
                + "no VR this session; only other VR players' arms, fingers and crouching are shown.");
            return;
        }

        session = new XrSession(LoggerInstance);
        rig = new PlayerRig(LoggerInstance);
        washer = new WasherFixes(LoggerInstance);
        ui = new UiPresenter(LoggerInstance);
        render = new RenderTweaks(LoggerInstance);
        state = new GameState(LoggerInstance);
        input = new GameInput(LoggerInstance);
        pointer = new MenuPointer(LoggerInstance);
        wrist = new WristHud(LoggerInstance);
        hands = new Hands(LoggerInstance);
        belt = new Belt(LoggerInstance, body);
        belt.hands = hands;
        backpack = new Backpack(LoggerInstance, body);
        physicalWasher = new PhysicalWasher(LoggerInstance, body);
        backpack.Belt = belt;
        mirror = new Mirror(LoggerInstance);
        bodyHider = new BodyHider(LoggerInstance);
        bodyHider.InstallCameraHooks();
        smoothCamera = new SmoothCamera(LoggerInstance);
        clipboard = new Clipboard(LoggerInstance, body);
        mirror.Override = () => smoothCamera.Output;
        haptics.Settings = settings;

        Native.InstallResolver(typeof(Mod).Assembly, XrSession.PluginDirectory);
        Patches.Install(HarmonyInstance, LoggerInstance);
        Prompts.Install(HarmonyInstance, LoggerInstance);
        Patches.CameraPreRender = OnCameraPreRender;
        Patches.CameraPostRender = () => mirror.AfterEyeCamera();

        if (!XrSession.FilesPresent(out var missing))
            LoggerInstance.Error($"OpenXR files are not installed ({missing} missing). Run Install.ps1 again.");

        if (StartMarkerPresent())
        {
            autoStartDone = true;
            LoggerInstance.Warning("The last automatic VR start did not finish, so this one is skipped. "
                + $"Press {settings.StartKey} to start VR by hand.");
        }

        LoggerInstance.Msg($"PowerWash VR {Version} loaded. "
            + (settings.AutoStart ? "VR starts automatically once the game has loaded." : $"Press {settings.StartKey} to start VR."));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    private bool closeSent;

    // As Alt+F4: the window is asked to close, so the game quits its own way.
    private void CloseGame()
    {
        if (closeSent)
            return;
        closeSent = true;
        try
        {
            var window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (window == IntPtr.Zero)
                window = GetActiveWindow();
            if (window != IntPtr.Zero && PostMessage(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero))
            {
                LoggerInstance.Msg("VR exited: closing the game (as Alt+F4)");
                return;
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"VR exited: window close failed ({exception.GetType().Name}); quitting");
        }

        Application.Quit();
    }

    public override void OnUpdate()
    {
        if (session is not null && session.RuntimeEnded && !closeSent)
            CloseGame();

        // Without VR (Auto Detect found no SteamVR, or VR did not start): the
        // game's settings get the Auto Detect SteamVR row.
        if (inert || !running)
        {
            try { flatSettings.Update(settings); } catch (Exception e) { Report("Settings", e); }
            // After a VR session that never finished, your own settings come back.
            if ((inert || session.Current == XrSession.State.Failed) && Time.unscaledTime >= nextFlatRecovery)
            {
                nextFlatRecovery = Time.unscaledTime + 1f;
                try { vrSettings.RecoverFlat(); } catch (Exception e) { Report("Settings", e); }
            }
        }

        if (inert)
            return;
        try
        {
            if (UnityEngine.Input.GetKeyDown(settings.StartKey) && session.Current is XrSession.State.Idle or XrSession.State.Failed)
                StartVr();

            if (!autoStartDone && settings.AutoStart && session.Current == XrSession.State.Idle && Settled())
            {
                autoStartDone = true;
                StartVr();
            }

            UpdateRunning();

            if (!running)
                return;

            // The runtime's own recenter (holding the headset's system button).
            if (session.SpaceChanged)
            {
                session.SpaceChanged = false;
                rig.BodyToHead();
                rig.RequestRecenter();
            }

            rig.Resolve();
            state.Update(rig);

            // The first time the player is in the world, the view is put on
            // the body once (as the runtime's recenter does): the game spawns
            // the body before the headset pose is known.
            if (!firstRecenterDone && state.Mode == UiMode.Gameplay && rig.IsPlayer && rig.HasPose)
            {
                if (firstRecenterAt < 0f)
                    firstRecenterAt = Time.unscaledTime + 1f;
                else if (Time.unscaledTime >= firstRecenterAt)
                {
                    firstRecenterDone = true;
                    PlayerRig.FirstRecenterMark = PlayerRig.Recenters;
                    rig.BodyToHead();
                    rig.RequestRecenter();
                    LoggerInstance.Msg("rig: recentered once after the first load");
                }
            }
            vrSettings.Update(state, settings, true);

            if (Time.unscaledTime >= nextDeviceCheck)
            {
                nextDeviceCheck = Time.unscaledTime + 5f;
                FlatDevices(false);
                NoOcclusionMesh();
            }

            try
            {
                controllers.ReadButtons();
                input.Update(controllers, state, rig, settings, haptics, Time.unscaledDeltaTime);
                if (input.TurnDelta != 0f)
                    rig.Turn(input.TurnDelta);
            }
            catch (Exception exception)
            {
                Report("Input", exception);
            }

            render.Update(settings, rig);
            hands.Update(rig, settings);
            var equipment = washer.Equipment;
            haptics.Update(settings, state.Mode == UiMode.Gameplay && equipment is not null && equipment.IsWashing);

            if (Diag.Verbose && reportAt > 0f && Time.unscaledTime >= reportAt)
            {
                reportAt = reportsLeft-- > 0 ? Time.unscaledTime + 20f : -1f;
                if (reportsLeft == 2)
                    session.LogBindings();
                LoggerInstance.Msg($"  tracking: {controllers.Describe()}");
                LoggerInstance.Msg($"  buttons: {controllers.DescribeButtons()}   state {state.StateName} ({state.Mode})"
                    + $"   fire reads {Patches.FireReads}");
            }
        }
        catch (Exception exception)
        {
            Report("Update", exception);
        }
    }

    public override void OnLateUpdate()
    {
        // Other VR players' bodies, VR or not.
        try { bodySync.ApplyRemotes(); } catch (Exception e) { Report("Body sync", e); }
        if (inert || !running)
            return;

        // In VR the mouse is left free (the game locks it to the window for
        // flat play; nobody is looking at that window).
        if (Cursor.lockState != CursorLockMode.None)
            Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible)
            Cursor.visible = true;

        try
        {
            var deltaTime = Time.unscaledDeltaTime;
            controllers.ReadPoses();

            {
                rig.ApplyHead(controllers);
                rig.DriveAnchor = hands.Settled;
                try { physicalWasher.Update(rig, state, controllers, settings, haptics, deltaTime, backpack.WasherHidden); } catch (Exception e) { Report("Physical washer", e); }
                PlaceWasher();
                washer.Update(rig);
                rig.SyncLookAngles();
                body.Update(rig, deltaTime);
            }
        }
        catch (Exception exception)
        {
            Report("LateUpdate", exception);
        }

        // Each system on its own, so one failing cannot take the others down.
        var dt = Time.unscaledDeltaTime;
        try { hands.Apply(rig, controllers, settings, dt, true); } catch (Exception e) { Report("Hands", e); }
        Belt.CaseOut = backpack.Held || backpack.Placed;
        Props.HideHints = settings.HideInteractionHints;
        try { belt.Update(rig, state, controllers, input, hands, haptics, settings, dt); } catch (Exception e) { Report("Belt", e); }
        try { backpack.Update(rig, state, controllers, input, haptics, hands, settings); } catch (Exception e) { Report("Backpack", e); }
        input.FreeGripClaimed = belt.Claims || backpack.Claims || PhysicalWasher.ClaimsFree;
        input.WasherAway = PhysicalWasher.Away;
        input.PhysicalWasherOn = PhysicalWasher.Active;
        // No hand on the washer: the washer hand is a free hand too.
        if (PhysicalWasher.Away)
            hands.WasherFree = true;
        hands.WasherHandHolding = belt.HeldByWasherHand;
        PhysicalWasher.HandBusy[0] = belt.HoldsLeft || backpack.Holds(true);
        PhysicalWasher.HandBusy[1] = belt.HoldsRight || backpack.Holds(false);
        PhysicalWasher.PlayerRoot = state.Character is { } player && player != null ? player.transform.root : null;

        // Interaction aims from the free hand (Patches): from the palm, along
        // where the controller points.
        {
            ref var free = ref settings.LeftHanded ? ref controllers.Right : ref controllers.Left;
            Patches.AimValid = rig.HasPose && free.Tracked && rig.IsPlayer;
            if (Patches.AimValid)
            {
                var toWorld = rig.TrackingToWorldRotation;
                Patches.AimRotation = toWorld * (free.AimTracked ? free.AimRotation : free.GripRotation);
                Patches.AimPosition = rig.TrackingToWorldPosition + toWorld * free.GripPosition;
            }
        }
        try { clipboard.Update(rig, state, controllers, input, haptics, ui, settings); } catch (Exception e) { Report("Clipboard", e); }
        try { (tutorial ??= new Tutorial(LoggerInstance, body)).Update(rig, state, settings, backpack.Held); } catch (Exception e) { Report("Tutorial", e); }
        try { (hubPlacement ??= new HubPlacement(LoggerInstance)).Update(state, input, clipboard); } catch (Exception e) { Report("Placement", e); }
        try { palmLine.Update(state); } catch (Exception e) { Report("Palm line", e); }
        try { (taskMarker ??= new TaskMarker(LoggerInstance)).Update(rig, state); } catch (Exception e) { Report("Task marker", e); }
        bodySync.WasherLeft = settings.LeftHanded;
        bodySync.WasherAway = PhysicalWasher.Away;
        Patches.LocalController = rig.Controller is { } own ? own.Pointer : IntPtr.Zero;
        Patches.LocalPlayer = state.Character is { } me && me != null ? me.Pointer : IntPtr.Zero;
        if (Patches.Interaction is { } interaction && (interaction == null
            || (Patches.LocalPlayer != IntPtr.Zero && interaction.m_player is { } who && who != null && who.Pointer != Patches.LocalPlayer)))
            Patches.Interaction = null;
        bodySync.WasherHidden = backpack.WasherHidden || hands.WasherHidden
            || (rig.Assembly is { } assembly && assembly != null && !assembly.gameObject.activeInHierarchy);
        try { bodySync.SendOwn(rig, state, hands, backpack, belt, controllers); } catch (Exception e) { Report("Body sync", e); }
        try { rig.UpdateHitbox(settings.CrouchHitbox && state.Mode is UiMode.Gameplay or UiMode.Radial && !state.OnLadder); } catch (Exception e) { Report("Collider", e); }
        try { input.LateUpdate(controllers, state, rig, settings); } catch (Exception e) { Report("Wheel", e); }
        try { ui.Update(rig, settings, state.Mode, input, state.StateName); } catch (Exception e) { Report("Menus", e); }
        try { pointer.Update(rig, state, ui, controllers, input, settings); } catch (Exception e) { Report("Pointer", e); }
        try { wrist.Update(rig, state, controllers, settings); } catch (Exception e) { Report("Wrist", e); }
        try { bodyHider.Update(rig, state); } catch (Exception e) { Report("Body", e); }
        try { smoothCamera.Update(rig, settings, true); } catch (Exception e) { Report("Smooth camera", e); }
    }

    private void OnBeforeRender()
    {
        try
        {
            session.Pump();

            if (!running || session.Current != XrSession.State.Running)
                return;

            controllers.ReadPoses();
            rig.ApplyHead(controllers);
            physicalWasher.Reapply(rig, settings);
            PlaceWasher();
            washer.Reapply();
        }
        catch (Exception exception)
        {
            Report("BeforeRender", exception);
            return;
        }

        try
        {
            hands.Apply(rig, controllers, settings, 0f, false);
            belt.Reapply(rig, controllers, settings);
            backpack.Reapply(rig, settings, controllers);
            wrist.Reapply(rig, controllers, settings);
            taskMarker?.Reapply(rig);
            clipboard.Reapply(rig, controllers, settings);
            smoothCamera.Reapply(rig);
            ui.Reapply(rig);
        }
        catch (Exception exception)
        {
            Report("BeforeRender (props)", exception);
        }
    }

    private void OnCameraPreRender()
    {

        rig.ReapplyWasher();
        washer.Reapply();
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        if (inert || !running)
            return;

        washer.Reset();
        render.OnSceneLoaded();
    }

    public override void OnApplicationQuit()
    {
        if (inert)
            return;
        try
        {
            vrSettings?.Shutdown();
        }
        catch
        {
            // The marker recovers the flat settings next start.
        }

        haptics.Stop();
        FlatDevices(true);
    }

    // An error that repeats every frame must not flood the log or the disk.
    // Counted per place, so one noisy system cannot hide another's first error.
    private readonly Dictionary<string, int> errorCounts = new();

    private void Report(string where, Exception exception)
    {
        errorCounts.TryGetValue(where, out var count);
        errorCounts[where] = ++count;
        if (count <= 3 || count % 1000 == 0)
            LoggerInstance.Error($"{where} (error #{count}): {exception}");
    }

    private int reportsLeft;
    private float nextDeviceCheck;
    private bool loggedDevices;

    // In VR the game answers only to the controllers: the keyboard and mouse
    // are switched off in the Input System (and back on when VR stops), so
    // neither can move the view, steer a wheel or click a menu behind your back.
    private void FlatDevices(bool enable)
    {
        try
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            foreach (var device in new UnityEngine.InputSystem.InputDevice?[] { keyboard, mouse })
            {
                if (device is null || device.enabled == enable)
                    continue;
                if (enable)
                    UnityEngine.InputSystem.InputSystem.EnableDevice(device);
                else
                    UnityEngine.InputSystem.InputSystem.DisableDevice(device);
            }

            if (!enable && !loggedDevices)
            {
                loggedDevices = true;
                LoggerInstance.Msg("input: keyboard and mouse ignored while in VR");
            }
        }
        catch (Exception exception)
        {
            if (!loggedDevices)
            {
                loggedDevices = true;
                LoggerInstance.Warning($"input: keyboard/mouse not switched ({exception.GetType().Name})");
            }
        }
    }

    private VrSettings vrSettings = null!;
    private SmoothCamera smoothCamera = null!;
    private Clipboard clipboard = null!;
    private readonly PalmLine palmLine = new();
    private TaskMarker? taskMarker;
    private BodySync bodySync = null!;
    private FlatSettings flatSettings = null!;
    private System.Action? bodyBeforeRender;
    private bool occlusionLogged;

    // The runtime's hidden-area mesh (the lens edges no one sees) is drawn by
    // the render pipeline with a shader this game's build does not carry: on
    // some headsets it showed as a pink shape in the world. Not drawn at all.
    private void NoOcclusionMesh()
    {
        try
        {
            if (UnityEngine.Experimental.Rendering.XRSystem.GetOcclusionMeshScale() != 0f)
            {
                UnityEngine.Experimental.Rendering.XRSystem.SetOcclusionMeshScale(0f);
                if (!occlusionLogged)
                {
                    occlusionLogged = true;
                    LoggerInstance.Msg("render: headset hidden-area mesh switched off");
                }
            }
        }
        catch (Exception exception)
        {
            if (!occlusionLogged)
            {
                occlusionLogged = true;
                LoggerInstance.Msg($"render: hidden-area mesh switch not available ({exception.GetType().Name})");
            }
        }
    }
    private float nextFlatRecovery;

    // The mod's crouch stages are the game's stances (standing, crouching,
    // laying), which the game networks: other players, flat or VR, see it.
    // When the game changes the stance itself (standing up to sprint or
    // jump), the view follows.
    private int sentStance = -1;
    private float sentStanceAt;

    private void SyncStance()
    {
        try
        {
            var controller = state.Character?.CharacterController;
            if (controller is null || controller == null || !rig.IsPlayer)
                return;

            var game = (int)controller.CharacterStance;
            var wanted = rig.CrouchStage;
            if (wanted != sentStance)
            {
                var ok = controller.TrySetStance((Il2CppFuturLab.PW2.CharacterStance)wanted, false);
                if (!ok)
                    ok = controller.TrySetStance((Il2CppFuturLab.PW2.CharacterStance)wanted, true);
                sentStance = wanted;
                sentStanceAt = Time.unscaledTime;
                if (stanceLogs++ < 10)
                    LoggerInstance.Msg($"rig: stance {(Il2CppFuturLab.PW2.CharacterStance)wanted} {(ok ? "set" : "refused by the game")}");
            }
            else if (game != wanted && Time.unscaledTime - sentStanceAt > 0.5f)
            {
                rig.SetCrouchStage(game);
                sentStance = game;
                sentStanceAt = Time.unscaledTime;
                if (stanceLogs++ < 10)
                    LoggerInstance.Msg($"rig: the game changed the stance to {(Il2CppFuturLab.PW2.CharacterStance)game}; the view follows");
            }
        }
        catch (Exception exception)
        {
            if (stanceLogs++ < 10)
                LoggerInstance.Msg($"rig: stance not synced ({exception.GetType().Name})");
        }
    }

    private int stanceLogs;
    private bool firstRecenterDone;
    private float firstRecenterAt = -1f;

    private ref HandInput WasherHand() => ref settings.LeftHanded ? ref controllers.Left : ref controllers.Right;

    // In the washer hand, or (Physical Washer) on the belt or in the air.
    private void PlaceWasher()
    {
        if (PhysicalWasher.Away)
            rig.ApplyWasherPose(physicalWasher.Position, physicalWasher.Rotation);
        else
            rig.ApplyWasher(WasherHand(), settings);
    }

    private PhysicalWasher physicalWasher = null!;
    private Tutorial? tutorial;
    private HubPlacement? hubPlacement;

    private void StartVr()
    {
        if (!XrSession.FilesPresent(out var missing))
        {
            LoggerInstance.Error($"Cannot start VR: {missing} is missing. Run Install.ps1 again.");
            return;
        }

        if (!RuntimeAvailable())
        {
            LoggerInstance.Warning($"No OpenXR runtime is active. Start SteamVR (or your headset's PC software), "
                + $"then press {settings.StartKey}.");
            return;
        }

        if (!XrDevices.Resolve(LoggerInstance))
            return;

        HookFrame();

        WriteStartMarker();
        if (!session.Initialize(settings.RenderMode, settings.Latency))
        {
            ClearStartMarker();
            LoggerInstance.Error($"VR did not start: {session.FailureReason}");
        }
    }

    // Registered only when VR is first started, so a player who never starts
    // VR runs the game with nothing of this mod in its frame.
    private void HookFrame()
    {
        if (beforeRender is not null)
            return;

        // Held in a field so the converted delegate can never be collected.
        beforeRender = new System.Action(OnBeforeRender);
        Application.add_onBeforeRender(beforeRender);
    }

    // Tracks the session reaching and leaving the running state.
    private void UpdateRunning()
    {
        var nowRunning = session.Current == XrSession.State.Running;
        if (nowRunning == running)
            return;

        running = nowRunning;
        Patches.VrActive = nowRunning;

        if (nowRunning)
        {
            ClearStartMarker();

            // The headset, not the window, is what the player is looking at.
            Application.runInBackground = true;
            try
            {
                UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior =
                    UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
            }
            catch (Exception exception)
            {
                LoggerInstance.Warning($"Input focus behaviour not changed ({exception.GetType().Name})");
            }

            input.Reset();
            FlatDevices(false);
            render.OnVrStarted(settings);
            mirror.Display = session.Display;
            mirror.Start();
            rig.RequestRecenter();
            reportAt = Time.unscaledTime + 5f;
            reportsLeft = 3;
            LoggerInstance.Msg("VR is running.");
            NoOcclusionMesh();
        }
        else
        {
            haptics.Stop();
            input.Reset();
            FlatDevices(true);
            vrSettings.Update(state, settings, false);
            smoothCamera.Update(rig, settings, false);
            LoggerInstance.Msg("VR stopped.");
        }
    }

    private bool Settled()
    {
        if (firstFrameTime < 0f)
            firstFrameTime = Time.unscaledTime;

        recentFrames[framesSeen % recentFrames.Length] = Time.unscaledDeltaTime;
        framesSeen++;

        if (framesSeen < 120 || Time.unscaledTime - firstFrameTime < 6f)
            return false;

        for (var index = 0; index < recentFrames.Length; index++)
        {
            if (recentFrames[index] > 0.1f)
                return false;
        }

        return true;
    }

    private static bool RuntimeAvailable()
    {
        try
        {
            var runtime = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime", null) as string;
            return !string.IsNullOrEmpty(runtime);
        }
        catch
        {
            // Unreadable is not the same as absent; let OpenXR decide.
            return true;
        }
    }

    // A native hang during start-up cannot be caught. The marker makes sure it
    // cannot happen twice in a row: if it is still there at load, the previous
    // automatic start never finished, and this launch stays flat.
    private static string MarkerPath =>
        Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PowerWashVR", "starting.marker");

    private static bool StartMarkerPresent()
    {
        try
        {
            return File.Exists(MarkerPath);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteStartMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("O"));
        }
        catch
        {
            // Without the marker the start still works; only the safety net is gone.
        }
    }

    private static void ClearStartMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
                File.Delete(MarkerPath);
        }
        catch
        {
            // Harmless: costs one skipped automatic start.
        }
    }
}
