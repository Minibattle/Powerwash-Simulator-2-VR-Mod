using MelonLoader;

namespace PowerWashVR.OpenXR;

// Controller input, registered the way Unity's own interaction features do it
// (input/OpenXRInput.cs AttachActionSets): one action set per interaction
// profile, one device definition per hand per profile, and usages that match
// UnityEngine.XR.CommonUsages. That last part is what lets the mod read the
// controllers through UnityEngine.XR.InputDevices by usage name - "Trigger",
// "Primary2DAxis", "DevicePosition" - without touching the Input System.
//
// The runtime picks ONE of the suggested profiles per hand. Only that profile's
// devices become live, so registering several costs nothing at runtime.
internal static class Bindings
{
    internal const string LeftHand = "/user/hand/left";
    internal const string RightHand = "/user/hand/right";

    // UnityEngine.XR.InputDeviceCharacteristics.
    private const uint HeldInHand = 4, TrackedDevice = 32, Controller = 64, Left = 256, Right = 512;

    private enum ActionType : uint
    {
        Binary = 0,
        Axis1D = 1,
        Axis2D = 2,
        Pose = 3,
        Vibrate = 4,
    }

    private sealed record Action(string Name, ActionType Type, string Usage, string? LeftPath, string? RightPath);

    private sealed record Profile(string Path, string SetName, string DeviceName, string Manufacturer,
        string? RequiredExtension, Action[] Actions);

    private static Action Both(string name, ActionType type, string usage, string path) =>
        new(name, type, usage, path, path);

    private static readonly Action[] TouchActions =
    {
        Both("devicepose", ActionType.Pose, "Device", "/input/grip/pose"),
        Both("pointer", ActionType.Pose, "Pointer", "/input/aim/pose"),
        Both("trigger", ActionType.Axis1D, "Trigger", "/input/trigger/value"),
        Both("grip", ActionType.Axis1D, "Grip", "/input/squeeze/value"),
        Both("thumbstick", ActionType.Axis2D, "Primary2DAxis", "/input/thumbstick"),
        Both("thumbstickclicked", ActionType.Binary, "Primary2DAxisClick", "/input/thumbstick/click"),
        new("primarybutton", ActionType.Binary, "PrimaryButton", "/input/x/click", "/input/a/click"),
        new("secondarybutton", ActionType.Binary, "SecondaryButton", "/input/y/click", "/input/b/click"),
        new("menu", ActionType.Binary, "MenuButton", "/input/menu/click", null),
        // Capacitive sensors, for the free hand's finger curl.
        Both("triggertouched", ActionType.Binary, "TriggerTouch", "/input/trigger/touch"),
        Both("thumbsticktouched", ActionType.Binary, "Primary2DAxisTouch", "/input/thumbstick/touch"),
        new("primarytouched", ActionType.Binary, "PrimaryTouch", "/input/x/touch", "/input/a/touch"),
        new("secondarytouched", ActionType.Binary, "SecondaryTouch", "/input/y/touch", "/input/b/touch"),
        Both("thumbresttouched", ActionType.Binary, "ThumbrestTouch", "/input/thumbrest/touch"),
        Both("haptic", ActionType.Vibrate, "Haptic", "/output/haptic"),
    };

    private static readonly Action[] IndexActions =
    {
        Both("devicepose", ActionType.Pose, "Device", "/input/grip/pose"),
        Both("pointer", ActionType.Pose, "Pointer", "/input/aim/pose"),
        Both("trigger", ActionType.Axis1D, "Trigger", "/input/trigger/value"),
        Both("grip", ActionType.Axis1D, "Grip", "/input/squeeze/value"),
        Both("thumbstick", ActionType.Axis2D, "Primary2DAxis", "/input/thumbstick"),
        Both("thumbstickclicked", ActionType.Binary, "Primary2DAxisClick", "/input/thumbstick/click"),
        Both("primarybutton", ActionType.Binary, "PrimaryButton", "/input/a/click"),
        Both("secondarybutton", ActionType.Binary, "SecondaryButton", "/input/b/click"),
        Both("triggertouched", ActionType.Binary, "TriggerTouch", "/input/trigger/touch"),
        Both("thumbsticktouched", ActionType.Binary, "Primary2DAxisTouch", "/input/thumbstick/touch"),
        Both("primarytouched", ActionType.Binary, "PrimaryTouch", "/input/a/touch"),
        Both("secondarytouched", ActionType.Binary, "SecondaryTouch", "/input/b/touch"),
        Both("haptic", ActionType.Vibrate, "Haptic", "/output/haptic"),
    };

    // Steam Frame: A / B / X / Y and the menu button on the right controller,
    // a d-pad and the view button on the left, a bumper on each.
    private static readonly Action[] FrameActions =
    {
        Both("devicepose", ActionType.Pose, "Device", "/input/grip/pose"),
        Both("pointer", ActionType.Pose, "Pointer", "/input/aim/pose"),
        Both("trigger", ActionType.Axis1D, "Trigger", "/input/trigger/value"),
        Both("grip", ActionType.Axis1D, "Grip", "/input/squeeze/value"),
        Both("thumbstick", ActionType.Axis2D, "Primary2DAxis", "/input/thumbstick"),
        Both("thumbstickclicked", ActionType.Binary, "Primary2DAxisClick", "/input/thumbstick/click"),
        new("primarybutton", ActionType.Binary, "PrimaryButton", null, "/input/a/click"),
        new("secondarybutton", ActionType.Binary, "SecondaryButton", null, "/input/b/click"),
        new("framex", ActionType.Binary, "FrameX", null, "/input/x/click"),
        new("framey", ActionType.Binary, "FrameY", null, "/input/y/click"),
        new("menu", ActionType.Binary, "MenuButton", "/input/view/click", "/input/menu/click"),
        Both("bumper", ActionType.Binary, "Bumper", "/input/bumper/click"),
        new("dpadup", ActionType.Binary, "DpadUp", "/input/dpad_up/click", null),
        new("dpaddown", ActionType.Binary, "DpadDown", "/input/dpad_down/click", null),
        new("dpadleft", ActionType.Binary, "DpadLeft", "/input/dpad_left/click", null),
        new("dpadright", ActionType.Binary, "DpadRight", "/input/dpad_right/click", null),
        Both("triggertouched", ActionType.Binary, "TriggerTouch", "/input/trigger/touch"),
        Both("thumbsticktouched", ActionType.Binary, "Primary2DAxisTouch", "/input/thumbstick/touch"),
        new("primarytouched", ActionType.Binary, "PrimaryTouch", null, "/input/a/touch"),
        new("secondarytouched", ActionType.Binary, "SecondaryTouch", null, "/input/b/touch"),
        Both("haptic", ActionType.Vibrate, "Haptic", "/output/haptic"),
    };

    internal const string FrameProfile = "/interaction_profiles/valve/frame_controller_valve";
    internal const string IndexProfile = "/interaction_profiles/valve/index_controller";

    private static readonly Action[] SimpleActions =
    {
        Both("devicepose", ActionType.Pose, "Device", "/input/grip/pose"),
        Both("pointer", ActionType.Pose, "Pointer", "/input/aim/pose"),
        Both("triggerpressed", ActionType.Binary, "TriggerButton", "/input/select/click"),
        Both("menu", ActionType.Binary, "MenuButton", "/input/menu/click"),
        Both("haptic", ActionType.Vibrate, "Haptic", "/output/haptic"),
    };

    private static readonly Profile[] Profiles =
    {
        new("/interaction_profiles/oculus/touch_controller", "pwvrtouch", "Oculus Touch Controller OpenXR",
            "Oculus", null, TouchActions),
        new("/interaction_profiles/valve/index_controller", "pwvrindex", "Index Controller OpenXR",
            "Valve", null, IndexActions),
        new(FrameProfile, "pwvrframe", "Steam Frame Controller OpenXR",
            "Valve", "XR_VALVE_frame_controller_interaction", FrameActions),
        new("/interaction_profiles/khr/simple_controller", "pwvrsimple", "KHR Simple Controller OpenXR",
            "Khronos", null, SimpleActions),
    };

    internal static readonly string[] Extensions = { "XR_VALVE_frame_controller_interaction", "XR_EXT_hand_tracking", "XR_EXT_hand_tracking_data_source", "XR_KHR_win32_convert_performance_counter_time" };

    // The bound profile (left hand), looked up every two seconds.
    private static string currentProfile = "";
    private static float nextProfileCheck;

    internal static string Current
    {
        get
        {
            if (UnityEngine.Time.unscaledTime >= nextProfileCheck)
            {
                nextProfileCheck = UnityEngine.Time.unscaledTime + 2f;
                var left = CurrentProfile(LeftHand);
                currentProfile = left.StartsWith("/", StringComparison.Ordinal) ? left : CurrentProfile(RightHand);
            }

            return currentProfile;
        }
    }

    internal static bool IsFrame => Current == FrameProfile;
    internal static bool HasFingerTracking => Current is FrameProfile or IndexProfile;

    // Provider device ids per hand, for haptics. Only the profile the runtime
    // bound answers; impulses sent to the others are no-ops.
    internal static uint[] LeftDevices = Array.Empty<uint>();
    internal static uint[] RightDevices = Array.Empty<uint>();

    private static bool attached;

    internal static string ProfileNames()
    {
        var names = new string[Profiles.Length];
        for (var index = 0; index < Profiles.Length; index++)
            names[index] = Profiles[index].Path;
        return string.Join(", ", names);
    }

    internal static bool Attach(MelonLogger.Instance log)
    {
        if (attached)
            return true;

        var left = new List<uint>();
        var right = new List<uint>();
        var userPaths = new[] { LeftHand, RightHand };
        var anyProfile = false;

        foreach (var profile in Profiles)
        {
            if (profile.RequiredExtension is not null && !Native.IsExtensionEnabled(profile.RequiredExtension))
            {
                log.Msg($"  input: skipping {profile.Path} ({profile.RequiredExtension} not enabled by the runtime)");
                continue;
            }

            var leftId = Native.RegisterDeviceDefinition(LeftHand, profile.Path, false,
                HeldInHand | TrackedDevice | Controller | Left, profile.DeviceName, profile.Manufacturer, "");
            var rightId = Native.RegisterDeviceDefinition(RightHand, profile.Path, false,
                HeldInHand | TrackedDevice | Controller | Right, profile.DeviceName, profile.Manufacturer, "");

            if (leftId == 0 || rightId == 0)
            {
                log.Warning($"  input: device definitions refused for {profile.Path}");
                continue;
            }

            left.Add((uint)leftId);
            right.Add((uint)rightId);

            var set = Native.CreateActionSet(profile.SetName, profile.DeviceName, default);
            if (set == 0)
            {
                log.Warning($"  input: action set refused for {profile.Path}");
                continue;
            }

            var bindings = new List<Native.SerializedBinding>();
            var optional = new List<Native.SerializedBinding>();
            foreach (var action in profile.Actions)
            {
                var id = Native.CreateAction(set, action.Name, action.Name, (uint)action.Type, default,
                    userPaths, (uint)userPaths.Length, false, new[] { action.Usage }, 1);

                if (id == 0)
                {
                    log.Warning($"  input: action {action.Name} refused for {profile.Path}");
                    continue;
                }

                // The touch sensors are a nicety: if a runtime refuses one of
                // their paths, the profile is suggested again without them.
                var into = action.Usage.EndsWith("Touch", StringComparison.Ordinal) ? optional : bindings;
                if (action.LeftPath is not null)
                    into.Add(new Native.SerializedBinding { ActionId = id, Path = LeftHand + action.LeftPath });
                if (action.RightPath is not null)
                    into.Add(new Native.SerializedBinding { ActionId = id, Path = RightHand + action.RightPath });
            }

            var all = new List<Native.SerializedBinding>(bindings);
            all.AddRange(optional);
            var accepted = Native.SuggestBindings(profile.Path, all.ToArray(), (uint)all.Count);
            if (!accepted && optional.Count > 0)
            {
                log.Msg($"  input: {profile.Path} refused the touch sensors; suggesting it without them");
                accepted = Native.SuggestBindings(profile.Path, bindings.ToArray(), (uint)bindings.Count);
            }
            else
            {
                bindings = all;
            }

            anyProfile |= accepted;
            log.Msg($"  input: {(accepted ? "suggested" : "REFUSED  ")} {bindings.Count,2} bindings for {profile.Path}");
        }

        LeftDevices = left.ToArray();
        RightDevices = right.ToArray();

        if (!anyProfile)
        {
            log.Error("  input: every interaction profile was refused; controllers will not work.");
            return false;
        }

        attached = Native.AttachActionSets();
        log.Msg(attached ? "  input: action sets attached" : "  input: AttachActionSets FAILED");
        return attached;
    }

    internal static string CurrentProfile(string hand)
    {
        try
        {
            if (!Native.StringToPath(hand, out var handPath)
                || !Native.GetCurrentInteractionProfile(handPath, out var current) || current == 0)
                return "none";

            foreach (var profile in Profiles)
            {
                if (Native.StringToPath(profile.Path, out var id) && id == current)
                    return profile.Path;
            }

            return $"unknown (0x{current:x})";
        }
        catch (Exception exception)
        {
            return $"unreadable ({exception.GetType().Name})";
        }
    }
}
