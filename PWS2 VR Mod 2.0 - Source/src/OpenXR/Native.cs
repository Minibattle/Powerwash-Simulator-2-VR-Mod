using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerWashVR.OpenXR;

// P/Invoke surface of UnityOpenXR.dll (com.unity.xr.openxr 1.18.0).
//
// The game ships no XR provider, but Unity registers the provider's subsystem
// descriptors from Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json
// at startup. Everything Unity's managed loader does on top of that is a plain
// P/Invoke into this library, so the mod takes the loader's place.
//
// Every signature below is transcribed from the package source
// (OpenXRLoader.cs, OpenXRLoaderBase.cs, Settings/OpenXRRenderSettings.cs,
// input/OpenXRInput.cs, Features/OpenXRFeature.cs). Marshalling attributes are
// copied exactly; bool returns are U1, bool parameters are I1.
internal static class Native
{
    internal const string Library = "UnityOpenXR";

    internal enum NativeEvent
    {
        XrSetupConfigValues,
        XrSystemIdChanged,
        XrInstanceChanged,
        XrSessionChanged,
        XrBeginSession,
        XrSessionStateChanged,
        XrChangedSpaceApp,
        XrEndSession,
        XrDestroySession,
        XrDestroyInstance,
        XrIdle,
        XrReady,
        XrSynchronized,
        XrVisible,
        XrFocused,
        XrStopping,
        XrExiting,
        XrLossPending,
        XrInstanceLossPending,
        XrRestartRequested,
        XrRequestRestartLoop,
        XrRequestGetSystemLoop,
        XrExtensionsReady,
    }

    internal enum RenderMode
    {
        MultiPass = 0,
        SinglePassInstanced = 1,
    }

    internal enum LatencyOptimization
    {
        PrioritizeRendering = 0,
        PrioritizeInputPolling = 1,
    }

    internal delegate void ReceiveNativeEvent(NativeEvent nativeEvent, ulong payload);

    private static bool resolverInstalled;

    // CoreCLR does not search Data/Plugins/x86_64 for P/Invoke targets, so the
    // library is resolved by full path.
    internal static void InstallResolver(Assembly assembly, string pluginDirectory)
    {
        if (resolverInstalled)
            return;

        resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
        {
            if (!string.Equals(name, Library, StringComparison.Ordinal))
                return IntPtr.Zero;

            return NativeLibrary.TryLoad(Path.Combine(pluginDirectory, "UnityOpenXR.dll"), out var handle)
                ? handle
                : IntPtr.Zero;
        });
    }

    internal static byte[] Wide(string value) => Encoding.Unicode.GetBytes(value + '\0');

    // ---- loader -----------------------------------------------------------

    [DllImport(Library, EntryPoint = "main_LoadOpenXRLibrary")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool LoadOpenXRLibrary(byte[] loaderPath);

    [DllImport(Library, EntryPoint = "NativeConfig_SetCallbacks")]
    internal static extern void SetCallbacks(ReceiveNativeEvent callback);

    [DllImport(Library, EntryPoint = "NativeConfig_SetApplicationInfo", CharSet = CharSet.Ansi)]
    internal static extern void SetApplicationInfo(string applicationName, string applicationVersion,
        uint applicationVersionHash, string engineVersion);

    [DllImport(Library, EntryPoint = "session_SetSuccessfullyInitialized")]
    internal static extern void SetSuccessfullyInitialized([MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(Library, EntryPoint = "NativeConfig_GetProcAddressPtr")]
    internal static extern IntPtr GetProcAddressPtr([MarshalAs(UnmanagedType.I1)] bool loaderDefault);

    [DllImport(Library, EntryPoint = "NativeConfig_SetProcAddressPtrAndLoadStage1")]
    internal static extern void SetProcAddressPtrAndLoadStage1(IntPtr function);

    [DllImport(Library, EntryPoint = "session_InitializeSession")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool InitializeSession();

    [DllImport(Library, EntryPoint = "session_CreateSessionIfNeeded")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool CreateSessionIfNeeded();

    [DllImport(Library, EntryPoint = "session_BeginSession")]
    internal static extern void BeginSession();

    [DllImport(Library, EntryPoint = "session_EndSession")]
    internal static extern void EndSession();

    [DllImport(Library, EntryPoint = "messagepump_PumpMessageLoop")]
    internal static extern void PumpMessageLoop();

    [DllImport(Library, EntryPoint = "unity_ext_RequestEnableExtensionString", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool RequestEnableExtensionString(string extension);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetXRSession")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetXrSession(out ulong session);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetAppSpace")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetAppSpace(out ulong space);

    [DllImport(Library, EntryPoint = "unity_ext_IsExtensionEnabled", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool IsExtensionEnabled(string extension);

    [DllImport(Library, EntryPoint = "NativeConfig_IsSessionFocused")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool IsSessionFocused();

    [DllImport(Library, EntryPoint = "NativeConfig_GetRuntimeName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetRuntimeName(out IntPtr runtimeName);

    // ---- render settings (OpenXRSettings.ApplyRenderSettings) --------------

    [DllImport(Library, EntryPoint = "NativeConfig_SetRenderMode")]
    internal static extern void SetRenderMode(RenderMode renderMode);

    [DllImport(Library, EntryPoint = "NativeConfig_GetRenderMode")]
    internal static extern RenderMode GetRenderMode();

    [DllImport(Library, EntryPoint = "NativeConfig_SetLatencyOptimization")]
    internal static extern void SetLatencyOptimization(LatencyOptimization latencyOptimization);

    [DllImport(Library, EntryPoint = "NativeConfig_SetDepthSubmissionMode")]
    internal static extern void SetDepthSubmissionMode(int depthSubmissionMode);

    [DllImport(Library, EntryPoint = "NativeConfig_SetSymmetricProjection")]
    internal static extern void SetSymmetricProjection([MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(Library, EntryPoint = "NativeConfig_SetOptimizeBufferDiscards")]
    internal static extern void SetOptimizeBufferDiscards([MarshalAs(UnmanagedType.I1)] bool enabled);

    // ---- interaction profiles ---------------------------------------------

    [DllImport(Library, EntryPoint = "Internal_StringToPath")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool StringToPath([MarshalAs(UnmanagedType.LPStr)] string path, out ulong pathId);

    [DllImport(Library, EntryPoint = "Internal_GetCurrentInteractionProfile")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetCurrentInteractionProfile(ulong pathId, out ulong interactionProfile);

    // ---- input provider (input/OpenXRInput.cs) ----------------------------

    [StructLayout(LayoutKind.Explicit)]
    internal struct SerializedGuid
    {
        [FieldOffset(0)] public ulong Low;
        [FieldOffset(8)] public ulong High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct SerializedBinding
    {
        public ulong ActionId;
        public string Path;
    }

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_RegisterDeviceDefinition", CharSet = CharSet.Ansi)]
    internal static extern ulong RegisterDeviceDefinition(string userPath, string interactionProfile,
        [MarshalAs(UnmanagedType.I1)] bool isAdditive, uint characteristics, string name,
        string manufacturer, string serialNumber);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_CreateActionSet", CharSet = CharSet.Ansi)]
    internal static extern ulong CreateActionSet(string name, string localizedName, SerializedGuid guid);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_CreateAction", CharSet = CharSet.Ansi)]
    internal static extern ulong CreateAction(ulong actionSetId, string name, string localizedName,
        uint actionType, SerializedGuid guid, string[] userPaths, uint userPathCount,
        [MarshalAs(UnmanagedType.I1)] bool isAdditive, string[]? usages, uint usageCount);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_SuggestBindings", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool SuggestBindings(string interactionProfile,
        SerializedBinding[] serializedBindings, uint serializedBindingCount);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_AttachActionSets")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool AttachActionSets();

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_SendHapticImpulseNoISX",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SendHapticImpulse(uint deviceId, float amplitude, float frequency, float duration);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_StopHapticsNoISX",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void StopHaptics(uint deviceId);
}
