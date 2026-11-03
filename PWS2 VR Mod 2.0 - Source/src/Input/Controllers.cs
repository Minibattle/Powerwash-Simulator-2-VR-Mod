using UnityEngine;

namespace PowerWashVR.Input;

internal struct HandInput
{
    public bool Tracked;
    public Vector3 GripPosition;
    public Quaternion GripRotation;
    public bool AimTracked;
    public Vector3 AimPosition;
    public Quaternion AimRotation;

    public float Trigger;
    public float Grip;
    public Vector2 Stick;
    public bool StickClick;
    public bool Primary;
    public bool Secondary;
    public bool Menu;

    // Steam Frame controllers: the right controller's X / Y and both bumpers.
    public bool X;
    public bool Y;
    public bool Bumper;
    public bool DpadUp, DpadDown, DpadLeft, DpadRight;

    // Capacitive sensors. HasTouch is false on controllers without them, in
    // which case the finger curl falls back to the analogue values alone.
    public bool HasTouch;
    public bool TriggerTouch;
    public bool ThumbTouch;
}

// Headset and both controllers, read once per call and kept as plain structs.
// Poses are re-read in onBeforeRender, where the XR provider has just refreshed
// them for the frame about to be drawn; buttons are read once per frame.
internal sealed class Controllers
{
    internal Vector3 HeadPosition;
    internal Quaternion HeadRotation = Quaternion.identity;
    internal bool HeadTracked;

    internal HandInput Left;
    internal HandInput Right;

    internal ulong HeadDevice { get; private set; }
    internal ulong LeftDevice { get; private set; }
    internal ulong RightDevice { get; private set; }

    private int nextDeviceRefresh;

    private readonly PoseFeature head = new(
        new[] { "CenterEyePosition", "Center Eye Position", "DevicePosition", "Device Position" },
        new[] { "CenterEyeRotation", "Center Eye Rotation", "DeviceRotation", "Device Rotation" });

    private readonly PoseFeature leftGrip = GripFeature();
    private readonly PoseFeature rightGrip = GripFeature();
    private readonly PoseFeature leftAim = AimFeature();
    private readonly PoseFeature rightAim = AimFeature();

    private static PoseFeature GripFeature() => new(
        new[] { "DevicePosition", "devicepose/position", "devicePose/position" },
        new[] { "DeviceRotation", "devicepose/rotation", "devicePose/rotation" });

    private static PoseFeature AimFeature() => new(
        new[] { "PointerPosition", "pointer/position" },
        new[] { "PointerRotation", "pointer/rotation" });

    internal string Describe() =>
        $"head {(HeadDevice == 0 ? "none" : head.Describe())}   "
        + $"left {(LeftDevice == 0 ? "none" : leftGrip.Describe() + " " + leftAim.Describe())}   "
        + $"right {(RightDevice == 0 ? "none" : rightGrip.Describe() + " " + rightAim.Describe())}";

    internal string DescribeButtons() => $"left {Buttons(Left)}   right {Buttons(Right)}";

    private static string Buttons(in HandInput hand) =>
        $"trigger {hand.Trigger:0.00} grip {hand.Grip:0.00} stick ({hand.Stick.x:0.00},{hand.Stick.y:0.00})"
        + $"{(hand.Primary ? " primary" : "")}{(hand.Secondary ? " secondary" : "")}{(hand.StickClick ? " stickclick" : "")}"
        + $"{(hand.Menu ? " menu" : "")}{(hand.HasTouch ? $" touch[{(hand.TriggerTouch ? "T" : "-")}{(hand.ThumbTouch ? "t" : "-")}]" : " no-touch")}";

    private void RefreshDevices(bool force)
    {
        var frame = Time.frameCount;
        if (!force && frame < nextDeviceRefresh)
            return;

        nextDeviceRefresh = frame + 45;

        var headId = XrDevices.Head();
        var leftId = XrDevices.Hand(false);
        var rightId = XrDevices.Hand(true);

        if (headId != HeadDevice)
            head.Forget();
        if (leftId != LeftDevice)
        {
            leftGrip.Forget();
            leftAim.Forget();
        }

        if (rightId != RightDevice)
        {
            rightGrip.Forget();
            rightAim.Forget();
        }

        HeadDevice = headId;
        LeftDevice = leftId;
        RightDevice = rightId;
    }

    internal void ReadPoses()
    {
        RefreshDevices(false);

        // Keep the last good pose on a failed read: one dropped sample must not
        // snap the view or the washer to the origin.
        if (head.Read(HeadDevice, out var headPosition, out var headRotation))
        {
            HeadPosition = headPosition;
            HeadRotation = headRotation;
            HeadTracked = true;
        }
        else
        {
            HeadTracked = false;
            if (HeadDevice == 0)
                RefreshDevices(true);
        }

        ReadHandPose(ref Left, LeftDevice, leftGrip, leftAim);
        ReadHandPose(ref Right, RightDevice, rightGrip, rightAim);
    }

    private static void ReadHandPose(ref HandInput hand, ulong device, PoseFeature grip, PoseFeature aim)
    {
        if (grip.Read(device, out var position, out var rotation))
        {
            hand.GripPosition = position;
            hand.GripRotation = rotation;
            hand.Tracked = true;
        }
        else
        {
            hand.Tracked = false;
        }

        if (aim.Read(device, out var aimPosition, out var aimRotation))
        {
            hand.AimPosition = aimPosition;
            hand.AimRotation = aimRotation;
            hand.AimTracked = true;
        }
        else
        {
            hand.AimTracked = false;
        }
    }

    // Usage name first (Unity's CommonUsages), the action's own feature name
    // second. Whichever answers is remembered per hand.
    private sealed class ButtonNames
    {
        internal readonly FeatureName Trigger = new("Trigger", "trigger");
        internal readonly FeatureName TriggerClick = new("TriggerButton", "triggerpressed");
        internal readonly FeatureName Grip = new("Grip", "grip");
        internal readonly FeatureName Stick = new("Primary2DAxis", "thumbstick");
        internal readonly FeatureName StickClick = new("Primary2DAxisClick", "thumbstickclicked");
        internal readonly FeatureName Primary = new("PrimaryButton", "primarybutton");
        internal readonly FeatureName Secondary = new("SecondaryButton", "secondarybutton");
        internal readonly FeatureName Menu = new("MenuButton", "menu");
        internal readonly FeatureName X = new("FrameX", "framex");
        internal readonly FeatureName Y = new("FrameY", "framey");
        internal readonly FeatureName Bumper = new("Bumper", "bumper");
        internal readonly FeatureName DpadUp = new("DpadUp", "dpadup");
        internal readonly FeatureName DpadDown = new("DpadDown", "dpaddown");
        internal readonly FeatureName DpadLeft = new("DpadLeft", "dpadleft");
        internal readonly FeatureName DpadRight = new("DpadRight", "dpadright");
        internal readonly FeatureName TriggerTouch = new("TriggerTouch", "triggertouched");
        internal readonly FeatureName StickTouch = new("Primary2DAxisTouch", "thumbsticktouched");
        internal readonly FeatureName PrimaryTouch = new("PrimaryTouch", "primarytouched");
        internal readonly FeatureName SecondaryTouch = new("SecondaryTouch", "secondarytouched");
        internal readonly FeatureName ThumbrestTouch = new("ThumbrestTouch", "thumbresttouched");

        internal void Forget()
        {
            Trigger.Forget();
            TriggerClick.Forget();
            Grip.Forget();
            Stick.Forget();
            StickClick.Forget();
            Primary.Forget();
            Secondary.Forget();
            Menu.Forget();
            X.Forget();
            Y.Forget();
            Bumper.Forget();
            DpadUp.Forget();
            DpadDown.Forget();
            DpadLeft.Forget();
            DpadRight.Forget();
            TriggerTouch.Forget();
            StickTouch.Forget();
            PrimaryTouch.Forget();
            SecondaryTouch.Forget();
            ThumbrestTouch.Forget();
        }
    }

    private readonly ButtonNames leftButtons = new();
    private readonly ButtonNames rightButtons = new();
    private ulong leftButtonsDevice, rightButtonsDevice;

    internal void ReadButtons()
    {
        if (leftButtonsDevice != LeftDevice)
        {
            leftButtonsDevice = LeftDevice;
            leftButtons.Forget();
        }

        if (rightButtonsDevice != RightDevice)
        {
            rightButtonsDevice = RightDevice;
            rightButtons.Forget();
        }

        ReadHandButtons(ref Left, LeftDevice, leftButtons);
        ReadHandButtons(ref Right, RightDevice, rightButtons);
    }

    private static void ReadHandButtons(ref HandInput hand, ulong device, ButtonNames names)
    {
        if (device == 0)
        {
            hand.Trigger = 0f;
            hand.Grip = 0f;
            hand.Stick = Vector2.zero;
            hand.StickClick = hand.Primary = hand.Secondary = hand.Menu = false;
            hand.X = hand.Y = hand.Bumper = false;
            hand.DpadUp = hand.DpadDown = hand.DpadLeft = hand.DpadRight = false;
            hand.HasTouch = hand.TriggerTouch = hand.ThumbTouch = false;
            return;
        }

        if (!names.Trigger.Read(device, out hand.Trigger))
        {
            // The simple controller profile only has a select click.
            names.TriggerClick.Read(device, out bool pressed);
            hand.Trigger = pressed ? 1f : 0f;
        }

        names.Grip.Read(device, out hand.Grip);
        names.Stick.Read(device, out hand.Stick);
        names.StickClick.Read(device, out hand.StickClick);
        names.Primary.Read(device, out hand.Primary);
        names.Secondary.Read(device, out hand.Secondary);
        names.Menu.Read(device, out hand.Menu);
        names.X.Read(device, out hand.X);
        names.Y.Read(device, out hand.Y);
        names.Bumper.Read(device, out hand.Bumper);
        names.DpadUp.Read(device, out hand.DpadUp);
        names.DpadDown.Read(device, out hand.DpadDown);
        names.DpadLeft.Read(device, out hand.DpadLeft);
        names.DpadRight.Read(device, out hand.DpadRight);

        hand.HasTouch = names.TriggerTouch.Read(device, out hand.TriggerTouch);
        var thumb = false;
        if (names.StickTouch.Read(device, out bool touched))
            thumb |= touched;
        if (names.PrimaryTouch.Read(device, out touched))
            thumb |= touched;
        if (names.SecondaryTouch.Read(device, out touched))
            thumb |= touched;
        if (names.ThumbrestTouch.Read(device, out touched))
            thumb |= touched;
        hand.ThumbTouch = thumb || hand.Primary || hand.Secondary || hand.StickClick;
        hand.TriggerTouch |= hand.Trigger > 0.05f;
    }
}

internal sealed class FeatureName
{
    private readonly string[] names;
    private int index = -1;

    // A feature the bound profile does not have is looked up again only every
    // couple of seconds, not every frame.
    private int retryFrame;

    internal FeatureName(params string[] names)
    {
        this.names = names;
    }

    internal void Forget()
    {
        index = -1;
        retryFrame = 0;
    }

    private bool Skip() => index < 0 && Time.frameCount < retryFrame;

    private void Missed() => retryFrame = Time.frameCount + 120;

    internal bool Read(ulong device, out float value)
    {
        if (index >= 0 && XrDevices.Read(device, names[index], out value))
            return true;

        if (!Skip())
        {
            for (var candidate = 0; candidate < names.Length; candidate++)
            {
                if (XrDevices.Read(device, names[candidate], out value))
                {
                    index = candidate;
                    return true;
                }
            }

            index = -1;
            Missed();
        }

        value = 0f;
        return false;
    }

    internal bool Read(ulong device, out Vector2 value)
    {
        if (index >= 0 && XrDevices.Read(device, names[index], out value))
            return true;

        if (!Skip())
        {
            for (var candidate = 0; candidate < names.Length; candidate++)
            {
                if (XrDevices.Read(device, names[candidate], out value))
                {
                    index = candidate;
                    return true;
                }
            }

            index = -1;
            Missed();
        }

        value = Vector2.zero;
        return false;
    }

    internal bool Read(ulong device, out bool value)
    {
        if (index >= 0 && XrDevices.ReadButton(device, names[index], out value))
            return true;

        if (!Skip())
        {
            for (var candidate = 0; candidate < names.Length; candidate++)
            {
                if (XrDevices.ReadButton(device, names[candidate], out value))
                {
                    index = candidate;
                    return true;
                }
            }

            index = -1;
            Missed();
        }

        value = false;
        return false;
    }
}
