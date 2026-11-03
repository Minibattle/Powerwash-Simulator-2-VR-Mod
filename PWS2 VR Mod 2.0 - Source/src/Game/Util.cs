using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PowerWashVR.Game;

// A button seen once per frame, with its edges.
internal struct Edge
{
    internal bool Down;
    internal bool Pressed;
    internal bool Released;

    internal void Update(bool down)
    {
        Pressed = down && !Down;
        Released = !down && Down;
        Down = down;
    }

    // Analogue input with hysteresis, so a trigger resting on the threshold
    // cannot chatter.
    internal void Update(float value, float on = 0.6f, float off = 0.4f) =>
        Update(Down ? value > off : value > on);

    internal void Clear() => this = default;
}

// Reads an object-reference field of an IL2CPP object straight from memory.
// The generated property getters allocate a wrapper per call; for values
// polled every frame, comparing raw pointers is free.
internal sealed unsafe class RawField
{
    private readonly int offset = -1;

    internal RawField(Type interopType, string fieldName)
    {
        try
        {
            var info = interopType.GetField("NativeFieldInfoPtr_" + fieldName,
                BindingFlags.NonPublic | BindingFlags.Static);
            if (info?.GetValue(null) is IntPtr field && field != IntPtr.Zero)
                offset = (int)IL2CPP.il2cpp_field_get_offset(field);
        }
        catch
        {
            offset = -1;
        }
    }

    internal bool Valid => offset > 0;

    internal IntPtr Read(IntPtr instance) =>
        instance == IntPtr.Zero || offset <= 0 ? IntPtr.Zero : *(IntPtr*)((byte*)instance + offset);
}

internal static class MathUtil
{
    // Mirror of a rotation across the plane whose normal is n (unit length).
    // For n = x this is (x, -y, -z, w).
    internal static Quaternion Mirror(Quaternion q, Vector3 n)
    {
        var v = new Vector3(q.x, q.y, q.z);
        var m = 2f * Vector3.Dot(v, n) * n - v;
        return new Quaternion(m.x, m.y, m.z, q.w);
    }

    internal static Vector3 MirrorX(Vector3 p) => new(-p.x, p.y, p.z);

    internal static float Damp(float smoothing, float deltaTime) =>
        smoothing <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / smoothing);

    internal static Vector2 Deadzone(Vector2 stick, float deadzone)
    {
        var magnitude = stick.magnitude;
        if (magnitude < deadzone)
            return Vector2.zero;

        var scaled = Mathf.Clamp01((magnitude - deadzone) / (1f - deadzone));
        return stick / magnitude * scaled;
    }
}

// Which way the body faces, taken to be where the head has been facing lately:
// it follows a turn of the head, but not a glance. Tracking space.
internal sealed class BodyFrame
{
    private float yaw;
    private bool known;

    internal float Yaw => yaw;
    internal Quaternion Rotation => Quaternion.Euler(0f, yaw, 0f);

    internal void Update(PlayerRig rig, float deltaTime)
    {
        if (!rig.HasPose)
            return;

        var headYaw = PlayerRig.Yaw(rig.HeadRotation);
        if (!known)
        {
            yaw = headYaw;
            known = true;
            return;
        }

        var difference = Mathf.DeltaAngle(yaw, headYaw);
        if (Mathf.Abs(difference) > 35f)
            yaw += (difference - Mathf.Sign(difference) * 35f) * MathUtil.Damp(0.25f, deltaTime) + difference * MathUtil.Damp(2f, deltaTime);
        else
            yaw += difference * MathUtil.Damp(2f, deltaTime);
    }
}
