using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;

namespace PowerWashVR.OpenXR;

// Finger tracking through XR_EXT_hand_tracking: SteamVR drives it from the
// finger sensors of Index and Steam Frame controllers. Each finger's curl is
// worked out from its joints' bend and handed to Hands.
//
// The extension's functions need the OpenXR instance and a frame time, which
// Unity's plugin does not hand out: the loader's xrGetInstanceProcAddr is
// wrapped (before Unity loads anything through it) so that xrCreateInstance
// gives up the instance and xrWaitFrame the predicted display time.
internal static unsafe class HandTracking
{
    private const string Extension = "XR_EXT_hand_tracking";
    private const int JointCount = 26;
    private const int TypeCreateInfo = 1000051001, TypeLocateInfo = 1000051002, TypeLocations = 1000051003;

    private static IntPtr realGetProcAddr;
    private static delegate* unmanaged<ulong, byte*, IntPtr*, int> realGet;
    private static delegate* unmanaged<void*, ulong*, int> realCreateInstance;
    private static delegate* unmanaged<ulong, void*, void*, int> realWaitFrame;
    private static ulong instance;
    private static long displayTime;

    private static delegate* unmanaged<ulong, void*, ulong*, int> createTracker;
    private static delegate* unmanaged<ulong, void*, void*, int> locateJoints;
    private static ulong leftTracker, rightTracker;
    private static ulong appSpace;
    private static bool ready, failed;
    private static string status = "not started";

    private static readonly float[] leftCurls = new float[5], rightCurls = new float[5];
    private static int leftFrame = -1, rightFrame = -1;
    private static IntPtr joints;

    internal static string Status => status;

    // Wraps the loader's entry point; called before Unity's stage 1 load.
    internal static IntPtr Hook(IntPtr procAddress)
    {
        if (procAddress == IntPtr.Zero)
            return procAddress;
        realGetProcAddr = procAddress;
        realGet = (delegate* unmanaged<ulong, byte*, IntPtr*, int>)procAddress;
        delegate* unmanaged<ulong, byte*, IntPtr*, int> hook = &GetProcAddr;
        return (IntPtr)hook;
    }

    [UnmanagedCallersOnly]
    private static int GetProcAddr(ulong forInstance, byte* name, IntPtr* function)
    {
        var result = realGet(forInstance, name, function);
        if (result < 0 || function is null || *function == IntPtr.Zero || name is null)
            return result;
        var text = Marshal.PtrToStringAnsi((IntPtr)name);
        if (text == "xrCreateInstance")
        {
            realCreateInstance = (delegate* unmanaged<void*, ulong*, int>)*function;
            delegate* unmanaged<void*, ulong*, int> hook = &CreateInstance;
            *function = (IntPtr)hook;
        }
        else if (text == "xrWaitFrame")
        {
            realWaitFrame = (delegate* unmanaged<ulong, void*, void*, int>)*function;
            delegate* unmanaged<ulong, void*, void*, int> hook = &WaitFrame;
            *function = (IntPtr)hook;
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static int CreateInstance(void* info, ulong* created)
    {
        var result = realCreateInstance(info, created);
        if (result >= 0 && created is not null)
            instance = *created;
        return result;
    }

    [UnmanagedCallersOnly]
    private static int WaitFrame(ulong session, void* waitInfo, void* frameState)
    {
        var result = realWaitFrame(session, waitInfo, frameState);
        // XrFrameState: type, next, predictedDisplayTime (offset 16).
        if (result >= 0 && frameState is not null)
            System.Threading.Volatile.Write(ref displayTime, *(long*)((byte*)frameState + 16));
        return result;
    }

    // Once the session runs: the two hand trackers.
    internal static void Start(MelonLogger.Instance log)
    {
        if (ready || failed)
            return;
        try
        {
            if (instance == 0 || realGet == null)
            {
                Fail(log, "no OpenXR instance seen");
                return;
            }

            if (!Native.IsExtensionEnabled(Extension))
            {
                Fail(log, $"{Extension} not offered by the runtime");
                return;
            }

            IntPtr create = IntPtr.Zero, locate = IntPtr.Zero;
            fixed (byte* a = System.Text.Encoding.ASCII.GetBytes("xrCreateHandTrackerEXT\0"))
                realGet(instance, a, &create);
            fixed (byte* b = System.Text.Encoding.ASCII.GetBytes("xrLocateHandJointsEXT\0"))
                realGet(instance, b, &locate);
            if (create == IntPtr.Zero || locate == IntPtr.Zero)
            {
                Fail(log, "the hand tracking functions are missing");
                return;
            }

            createTracker = (delegate* unmanaged<ulong, void*, ulong*, int>)create;
            locateJoints = (delegate* unmanaged<ulong, void*, void*, int>)locate;

            // A frame time of our own when the runtime's frame time is not
            // seen (the performance counter, converted by the runtime).
            if (Native.IsExtensionEnabled("XR_KHR_win32_convert_performance_counter_time"))
            {
                IntPtr convert = IntPtr.Zero;
                fixed (byte* c = System.Text.Encoding.ASCII.GetBytes("xrConvertWin32PerformanceCounterToTimeKHR\0"))
                    realGet(instance, c, &convert);
                convertCounter = (delegate* unmanaged<ulong, long*, long*, int>)convert;
            }

            dataSource = Native.IsExtensionEnabled("XR_EXT_hand_tracking_data_source");

            // Not there yet: tried again on the next call.
            if (!GetSession(out var session) || session == 0 || !GetAppSpace(out appSpace) || appSpace == 0)
                return;

            leftTracker = Tracker(session, 1);
            rightTracker = Tracker(session, 2);
            joints = Marshal.AllocHGlobal(JointCount * 40);
            ready = leftTracker != 0 || rightTracker != 0;
            logger = log;
            status = ready ? "on" : "trackers refused";
            log.Msg($"hands: finger tracking {(ready ? "ready (used on Index and Steam Frame controllers)" : "not available: trackers refused")}"
                + $"; data source request {(dataSource ? "on" : "not offered")}, own frame time {(convertCounter != null ? "available" : "not offered")}");
            if (!ready)
                failed = true;
        }
        catch (Exception exception)
        {
            Fail(log, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Fail(MelonLogger.Instance log, string why)
    {
        failed = true;
        status = why;
        log.Msg($"hands: finger tracking not available ({why})");
    }

    private static delegate* unmanaged<ulong, long*, long*, int> convertCounter;
    private static bool dataSource;
    private static int locateLogs, locateFailures;
    private static float nextSampleLog;
    private static MelonLogger.Instance? logger;

    private static ulong Tracker(ulong session, int hand)
    {
        // XrHandTrackerCreateInfoEXT: type, next, hand (16), joint set (20).
        var info = stackalloc byte[24];
        new Span<byte>(info, 24).Clear();
        *(int*)info = TypeCreateInfo;
        // XR_EXT_hand_tracking_data_source: the controllers' finger sensors
        // first, then optical hand tracking.
        var source = stackalloc byte[32];
        var kinds = stackalloc int[2];
        if (dataSource)
        {
            new Span<byte>(source, 32).Clear();
            kinds[0] = 2;   // XR_HAND_TRACKING_DATA_SOURCE_CONTROLLER_EXT
            kinds[1] = 1;   // XR_HAND_TRACKING_DATA_SOURCE_UNOBSTRUCTED_EXT
            *(int*)source = 1000428000;
            *(uint*)(source + 16) = 2;
            *(int**)(source + 24) = kinds;
            *(void**)(info + 8) = source;
        }
        *(int*)(info + 16) = hand;
        *(int*)(info + 20) = 0;
        ulong tracker = 0;
        var result = createTracker(session, info, &tracker);
        return result >= 0 ? tracker : 0;
    }

    // The five curls (thumb, index, middle, ring, little), 0 open to 1 closed,
    // read once per frame per hand.
    internal static bool TryGetCurls(bool left, float[] into)
    {
        if (!ready)
            return false;
        var frame = Time.frameCount;
        var curls = left ? leftCurls : rightCurls;
        if ((left ? leftFrame : rightFrame) != frame)
        {
            if (!Locate(left ? leftTracker : rightTracker, curls))
                return false;
            if (left)
                leftFrame = frame;
            else
                rightFrame = frame;
        }

        Array.Copy(curls, into, Math.Min(into.Length, curls.Length));
        return true;
    }

    private static bool Locate(ulong tracker, float[] curls)
    {
        var time = System.Threading.Volatile.Read(ref displayTime);
        if (time == 0 && convertCounter != null)
        {
            var counter = System.Diagnostics.Stopwatch.GetTimestamp();
            long converted = 0;
            if (convertCounter(instance, &counter, &converted) >= 0)
                time = converted;
        }

        if (tracker == 0 || time == 0)
        {
            Note("no frame time");
            return false;
        }

        // XrHandJointsLocateInfoEXT: type, next, base space (16), time (24).
        var info = stackalloc byte[32];
        new Span<byte>(info, 32).Clear();
        *(int*)info = TypeLocateInfo;
        *(ulong*)(info + 16) = appSpace;
        *(long*)(info + 24) = time;

        // XrHandJointLocationsEXT: type, next, isActive (16), count (20), joints (24).
        var locations = stackalloc byte[32];
        new Span<byte>(locations, 32).Clear();
        *(int*)locations = TypeLocations;
        *(uint*)(locations + 20) = JointCount;
        *(IntPtr*)(locations + 24) = joints;

        var result = locateJoints(tracker, info, locations);
        if (result < 0)
        {
            Note($"locate failed ({result})");
            return false;
        }

        if (*(int*)(locations + 16) == 0)
        {
            Note("hand not active");
            return false;
        }

        // XrHandJointLocationEXT (40 bytes): flags, orientation xyzw (8), position, radius.
        Quaternion Joint(int index)
        {
            var at = (float*)((byte*)joints + index * 40 + 8);
            return new Quaternion(at[0], at[1], at[2], at[3]);
        }

        // Joints: 2-5 thumb, 6-10 index, 11-15 middle, 16-20 ring, 21-25 little
        // (metacarpal, proximal, intermediate, distal, tip; the thumb has no
        // intermediate). The bend between neighbouring joints adds up.
        curls[0] = Mathf.Clamp01((Quaternion.Angle(Joint(2), Joint(3)) + Quaternion.Angle(Joint(3), Joint(4)) - 10f) / 80f);
        for (var finger = 1; finger < 5; finger++)
        {
            var first = 6 + (finger - 1) * 5;
            var bend = Quaternion.Angle(Joint(first), Joint(first + 1)) + Quaternion.Angle(Joint(first + 1), Joint(first + 2))
                + Quaternion.Angle(Joint(first + 2), Joint(first + 3));
            curls[finger] = Mathf.Clamp01((bend - 10f) / 200f);
        }

        if (Time.unscaledTime >= nextSampleLog && logger is not null)
        {
            nextSampleLog = Time.unscaledTime + 30f;
            if (Diag.Verbose)
                logger.Msg($"hands: finger tracking live: curls {curls[0]:0.00} {curls[1]:0.00} {curls[2]:0.00} {curls[3]:0.00} {curls[4]:0.00}");
        }

        return true;
    }

    private static void Note(string why)
    {
        locateFailures++;
        if (logger is not null && (locateLogs < 5 || locateFailures % 3000 == 0))
        {
            locateLogs++;
            logger.Msg($"hands: finger tracking: {why} ({locateFailures} so far, frame time {(displayTime != 0 ? "seen" : "not seen")})");
        }
    }

    private static bool GetSession(out ulong session) => Native.GetXrSession(out session);

    private static bool GetAppSpace(out ulong space) => Native.GetAppSpace(out space);
}
