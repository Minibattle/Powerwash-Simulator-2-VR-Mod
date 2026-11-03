using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.SubsystemsImplementation;
using UnityEngine.XR;

namespace PowerWashVR.OpenXR;

// The OpenXR loader, replacing Unity.XR.OpenXR's OpenXRLoader which this build
// does not contain. The order of calls is Unity's (OpenXRLoader.InitializeInternal
// and StartInternal); only the feature/analytics/editor plumbing is left out.
//
// Message pumping happens in Application.onBeforeRender, exactly like Unity's
// loader, so session state changes are handled at the same point in the frame.
internal sealed class XrSession
{
    internal enum State
    {
        Idle,
        WaitingForReady,
        Running,
        Stopped,
        Failed,
    }

    internal State Current { get; private set; } = State.Idle;
    internal bool Focused => lastEvent == Native.NativeEvent.XrFocused;
    internal bool SpaceChanged { get; set; }
    internal string FailureReason { get; private set; } = "";

    private readonly MelonLogger.Instance log;
    private XRDisplaySubsystem? display;
    internal XRDisplaySubsystem? Display => display;
    private XRInputSubsystem? input;
    private Native.NativeEvent lastEvent = Native.NativeEvent.XrIdle;
    private bool readyPending;
    private bool stoppingPending;
    private bool exitPending;

    // The runtime ended VR (SteamVR exited): the game is closed as Alt+F4
    // would (its own quit, with its save), before the runtime kills it.
    internal bool RuntimeEnded { get; private set; }
    private bool sessionBegun;

    // Native code holds this pointer for the life of the process.
    private static Native.ReceiveNativeEvent? callback;
    private static XrSession? instance;

    internal XrSession(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal static string PluginDirectory => Path.Combine(Application.dataPath, "Plugins", "x86_64");

    internal static bool FilesPresent(out string missing)
    {
        foreach (var file in new[] { "UnityOpenXR.dll", "openxr_loader.dll" })
        {
            if (!File.Exists(Path.Combine(PluginDirectory, file)))
            {
                missing = file;
                return false;
            }
        }

        var manifest = Path.Combine(Application.dataPath, "UnitySubsystems", "UnityOpenXR", "UnitySubsystemsManifest.json");
        if (!File.Exists(manifest))
        {
            missing = "UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json";
            return false;
        }

        missing = "";
        return true;
    }

    internal bool Initialize(Native.RenderMode renderMode, Native.LatencyOptimization latency)
    {
        log.Msg("OpenXR: initializing");
        instance = this;

        try
        {
            Native.SetSuccessfullyInitialized(false);

            var loader = Path.Combine(PluginDirectory, "openxr_loader.dll");
            if (!Native.LoadOpenXRLibrary(Native.Wide(loader)))
                return Fail("the OpenXR loader library could not be loaded");

            // OpenXRFeature.HookGetInstanceProcAddr with no features: hand the
            // default pointer straight back. This is what loads stage 1.
            var procAddress = Native.GetProcAddressPtr(true);
            if (procAddress == IntPtr.Zero)
                return Fail("the plugin returned no xrGetInstanceProcAddr");
            // Finger tracking needs the instance and the frame time.
            procAddress = HandTracking.Hook(procAddress);
            Native.SetProcAddressPtrAndLoadStage1(procAddress);

            RequestExtensions();

            if (!Native.InitializeSession())
                return Fail("session_InitializeSession failed - is the headset software running?");

            // Unity's RequestOpenXRFeatures slot. The instance is created after
            // this returns, so requests made here still count.
            RequestExtensions();

            callback = OnNativeEvent;
            Native.SetCallbacks(callback);

            // OpenXRSettings.ApplyRenderSettings, with this mod's choices.
            Native.SetSymmetricProjection(false);
            Native.SetRenderMode(renderMode);
            Native.SetLatencyOptimization(latency);
            Native.SetDepthSubmissionMode(0);
            Native.SetOptimizeBufferDiscards(false);

            Native.SetApplicationInfo(Application.productName, Application.version,
                VersionHash(Application.version), Application.unityVersion);

            display = Create<XRDisplaySubsystemDescriptor, XRDisplaySubsystem>("OpenXR Display");
            if (display is null)
                return Fail("the OpenXR display subsystem could not be created (see Player.log)");

            input = Create<XRInputSubsystemDescriptor, XRInputSubsystem>("OpenXR Input");
            if (input is null)
                return Fail("the OpenXR input subsystem could not be created (see Player.log)");

            if (!Native.CreateSessionIfNeeded())
                return Fail("no OpenXR session - headset not connected or runtime not ready");

            Current = State.WaitingForReady;
            log.Msg("OpenXR: initialized, waiting for the runtime to become ready");
            return true;
        }
        catch (DllNotFoundException exception)
        {
            return Fail($"UnityOpenXR.dll could not be loaded ({exception.Message})");
        }
        catch (EntryPointNotFoundException exception)
        {
            return Fail($"UnityOpenXR.dll is the wrong version ({exception.Message})");
        }
        catch (Exception exception)
        {
            return Fail($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // Called from Application.onBeforeRender.
    internal void Pump()
    {
        if (Current is State.Idle or State.Failed)
            return;

        Native.PumpMessageLoop();
        if (Current == State.Running)
            HandTracking.Start(log);

        if (readyPending)
        {
            readyPending = false;
            Start();
        }

        if (stoppingPending)
        {
            stoppingPending = false;
            if (sessionBegun)
            {
                log.Msg("OpenXR: runtime is stopping the session");
                Native.EndSession();
                sessionBegun = false;
            }

            // A stopped session can come back: the runtime sends XrReady again.
            if (Current == State.Running)
                Current = State.WaitingForReady;
        }

        if (exitPending)
        {
            exitPending = false;
            log.Msg("OpenXR: the runtime ended the session (VR exited); closing the game the normal way");
            StopSubsystems();
            Current = State.Stopped;
            RuntimeEnded = true;
        }
    }

    private void Start()
    {
        // StartInternal. Display before input: input needs the session object
        // the display creates.
        if (!Native.CreateSessionIfNeeded())
        {
            Fail("session_CreateSessionIfNeeded failed on XrReady");
            return;
        }

        if (display is null || input is null)
            return;

        if (!display.running)
            display.Start();

        if (!display.running)
        {
            Fail("the display subsystem did not start");
            return;
        }

        Native.BeginSession();
        sessionBegun = true;

        Bindings.Attach(log);

        if (!input.running)
            input.Start();

        Current = State.Running;
        log.Msg("OpenXR: running");
        LogRuntime();
    }

    internal void Stop()
    {
        if (Current is State.Idle or State.Failed or State.Stopped)
            return;

        StopSubsystems();
        if (sessionBegun)
        {
            Native.EndSession();
            sessionBegun = false;
            Native.PumpMessageLoop();
        }

        Current = State.Stopped;
    }

    private void StopSubsystems()
    {
        try
        {
            if (input is not null && input.running)
                input.Stop();
            if (display is not null && display.running)
                display.Stop();
        }
        catch (Exception exception)
        {
            log.Warning($"OpenXR: stopping subsystems threw {exception.GetType().Name}");
        }
    }

    internal bool TryGetRefreshRate(out float hz)
    {
        hz = 0f;
        try
        {
            return display is not null && display.running && display.TryGetDisplayRefreshRate(out hz) && hz > 0f;
        }
        catch
        {
            return false;
        }
    }

    private static void OnNativeEvent(Native.NativeEvent nativeEvent, ulong payload)
    {
        var session = instance;
        if (session is null)
            return;

        switch (nativeEvent)
        {
            case Native.NativeEvent.XrIdle:
            case Native.NativeEvent.XrReady:
            case Native.NativeEvent.XrSynchronized:
            case Native.NativeEvent.XrVisible:
            case Native.NativeEvent.XrFocused:
            case Native.NativeEvent.XrStopping:
            case Native.NativeEvent.XrExiting:
            case Native.NativeEvent.XrLossPending:
            case Native.NativeEvent.XrInstanceLossPending:
                session.lastEvent = nativeEvent;
                session.log.Msg($"OpenXR: {nativeEvent}");
                break;
        }

        switch (nativeEvent)
        {
            case Native.NativeEvent.XrReady:
                session.readyPending = true;
                break;
            case Native.NativeEvent.XrStopping:
                session.stoppingPending = true;
                break;
            case Native.NativeEvent.XrExiting:
            case Native.NativeEvent.XrLossPending:
            case Native.NativeEvent.XrInstanceLossPending:
                session.exitPending = true;
                break;
            case Native.NativeEvent.XrChangedSpaceApp:
                session.SpaceChanged = true;
                break;
        }
    }

    private void RequestExtensions()
    {
        foreach (var extension in Bindings.Extensions)
        {
            try
            {
                Native.RequestEnableExtensionString(extension);
            }
            catch
            {
                // Optional extensions only.
            }
        }
    }

    private void LogRuntime()
    {
        try
        {
            var name = Native.GetRuntimeName(out var pointer) && pointer != IntPtr.Zero
                ? Marshal.PtrToStringAnsi(pointer) ?? "?"
                : "?";
            var rate = TryGetRefreshRate(out var hz) ? $"{hz:0.#} Hz" : "unknown";
            log.Msg($"  runtime {name}   refresh {rate}   render mode {Native.GetRenderMode()}");
        }
        catch (Exception exception)
        {
            log.Msg($"  runtime info unavailable ({exception.GetType().Name})");
        }
    }

    internal void LogBindings()
    {
        log.Msg($"  bound profile: left {Bindings.CurrentProfile(Bindings.LeftHand)}   "
            + $"right {Bindings.CurrentProfile(Bindings.RightHand)}");
        log.Msg($"  eye buffer {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}   "
            + $"stereo {XRSettings.stereoRenderingMode}");
    }

    private bool Fail(string reason)
    {
        FailureReason = reason;
        Current = State.Failed;
        log.Error($"OpenXR: {reason}");
        return false;
    }

    private TSubsystem? Create<TDescriptor, TSubsystem>(string id)
        where TDescriptor : IntegratedSubsystemDescriptor<TSubsystem>
        where TSubsystem : IntegratedSubsystem
    {
        var descriptors = SubsystemDescriptorStore.s_IntegratedDescriptors;
        if (descriptors is null)
            return null;

        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            if (descriptor is null || !string.Equals(descriptor.id, id, StringComparison.Ordinal))
                continue;

            var typed = descriptor.TryCast<TDescriptor>();
            if (typed is null)
                return null;

            try
            {
                return typed.Create();
            }
            catch (NullReferenceException)
            {
                // Interop's Create casts a native null without checking.
                return null;
            }
        }

        log.Error($"  no subsystem descriptor \"{id}\" is registered - the manifest is missing");
        return null;
    }

    // Only informational for the runtime; any stable number will do.
    private static uint VersionHash(string version)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(version))
            hash = (hash ^ b) * 16777619u;
        return hash;
    }
}
