using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace PowerWashVR.Input;

// Reads the headset and controllers straight from Unity's XR input layer
// (UnityEngine.XR.InputDevices), by calling the engine's internal calls
// directly.
//
// Why not the managed API: most of UnityEngine.XR.InputDevices is stripped from
// this build, and the parts that remain return structs by value, which
// Il2CppInterop marshals badly or not at all. Why not the Input System: every
// read through Il2CppInterop boxes a value on the IL2CPP heap. The internal
// calls are registered by UnityPlayer.dll no matter what the game's code kept,
// take plain pointers and allocate nothing, so a full read of both hands is a
// handful of native calls per frame.
//
// Unity 6 signature, confirmed against the shipped metadata:
//   bool TryGetFeatureValue_float_Injected(ulong deviceId, ref ManagedSpanWrapper usage, out float value)
// ManagedSpanWrapper is { void* begin; int length; } over the UTF-16 usage name.
internal static unsafe class XrDevices
{
    private struct Span
    {
        public char* Begin;
        public int Length;
    }

    private const int NodeLeftHand = 4, NodeRightHand = 5, NodeHead = 3, NodeCenterEye = 2;

    private static delegate* unmanaged<int, ulong> deviceAtNode;
    private static delegate* unmanaged<ulong, byte> isValid;
    private static delegate* unmanaged<ulong, uint> characteristics;
    private static delegate* unmanaged<ulong, Span*, Vector3*, byte> readVector3;
    private static delegate* unmanaged<ulong, Span*, Quaternion*, byte> readQuaternion;
    private static delegate* unmanaged<ulong, Span*, Vector2*, byte> readVector2;
    private static delegate* unmanaged<ulong, Span*, float*, byte> readFloat;
    private static delegate* unmanaged<ulong, Span*, byte*, byte> readBool;

    internal static bool Ready { get; private set; }

    internal static bool Resolve(MelonLogger.Instance log)
    {
        if (Ready)
            return true;

        deviceAtNode = (delegate* unmanaged<int, ulong>)Icall(log, "UnityEngine.XR.InputTracking::GetDeviceIdAtXRNode");
        isValid = (delegate* unmanaged<ulong, byte>)Icall(log, "UnityEngine.XR.InputDevices::IsDeviceValid");
        characteristics = (delegate* unmanaged<ulong, uint>)Icall(log,
            "UnityEngine.XR.InputDevices::GetDeviceCharacteristics");
        readVector3 = (delegate* unmanaged<ulong, Span*, Vector3*, byte>)Icall(log,
            "UnityEngine.XR.InputDevices::TryGetFeatureValue_Vector3f_Injected");
        readQuaternion = (delegate* unmanaged<ulong, Span*, Quaternion*, byte>)Icall(log,
            "UnityEngine.XR.InputDevices::TryGetFeatureValue_Quaternionf_Injected");
        readVector2 = (delegate* unmanaged<ulong, Span*, Vector2*, byte>)Icall(log,
            "UnityEngine.XR.InputDevices::TryGetFeatureValue_Vector2f_Injected");
        readFloat = (delegate* unmanaged<ulong, Span*, float*, byte>)Icall(log,
            "UnityEngine.XR.InputDevices::TryGetFeatureValue_float_Injected");
        readBool = (delegate* unmanaged<ulong, Span*, byte*, byte>)Icall(log,
            "UnityEngine.XR.InputDevices::TryGetFeatureValue_bool_Injected");

        Ready = deviceAtNode != null && isValid != null && characteristics != null && readVector3 != null && readQuaternion != null
            && readVector2 != null && readFloat != null && readBool != null;

        if (!Ready)
            log.Error("XR input: engine calls missing; tracking cannot work in this Unity build.");

        return Ready;
    }

    private static IntPtr Icall(MelonLogger.Instance log, string name)
    {
        var pointer = IL2CPP.il2cpp_resolve_icall(name);
        if (pointer == IntPtr.Zero)
            log.Error($"XR input: engine call not found: {name}");
        return pointer;
    }

    // UnityEngine.XR.InputDeviceCharacteristics.
    private const uint HeadMounted = 1, HeldInHand = 4, Controller = 64, Left = 256, Right = 512;

    internal static ulong Head()
    {
        var id = DeviceAt(NodeCenterEye);
        if (id == 0)
            id = DeviceAt(NodeHead);
        return id != 0 ? id : Scan(HeadMounted, 0);
    }

    internal static ulong Hand(bool right)
    {
        var id = DeviceAt(right ? NodeRightHand : NodeLeftHand);
        return id != 0 ? id : Scan(HeldInHand | Controller | (right ? Right : Left), right ? Left : Right);
    }

    private static ulong DeviceAt(int node)
    {
        var id = deviceAtNode(node);
        return id != 0 && isValid(id) != 0 ? id : 0;
    }

    // Fallback when the node lookup does not know the device: device ids are
    // small integers, so the live ones can simply be enumerated.
    private static ulong Scan(uint required, uint excluded)
    {
        for (var id = 1UL; id <= 64UL; id++)
        {
            if (isValid(id) == 0)
                continue;

            var flags = characteristics(id);
            if ((flags & required) == required && (flags & excluded) == 0)
                return id;
        }

        return 0;
    }

    internal static bool Read(ulong device, string usage, out Vector3 value)
    {
        fixed (char* chars = usage)
        fixed (Vector3* result = &value)
        {
            var span = new Span { Begin = chars, Length = usage.Length };
            return readVector3(device, &span, result) != 0;
        }
    }

    internal static bool Read(ulong device, string usage, out Quaternion value)
    {
        fixed (char* chars = usage)
        fixed (Quaternion* result = &value)
        {
            var span = new Span { Begin = chars, Length = usage.Length };
            return readQuaternion(device, &span, result) != 0;
        }
    }

    internal static bool Read(ulong device, string usage, out Vector2 value)
    {
        fixed (char* chars = usage)
        fixed (Vector2* result = &value)
        {
            var span = new Span { Begin = chars, Length = usage.Length };
            return readVector2(device, &span, result) != 0;
        }
    }

    internal static bool Read(ulong device, string usage, out float value)
    {
        fixed (char* chars = usage)
        fixed (float* result = &value)
        {
            var span = new Span { Begin = chars, Length = usage.Length };
            return readFloat(device, &span, result) != 0;
        }
    }

    internal static bool ReadButton(ulong device, string usage, out bool value)
    {
        byte raw = 0;
        bool ok;
        fixed (char* chars = usage)
        {
            var span = new Span { Begin = chars, Length = usage.Length };
            ok = readBool(device, &span, &raw) != 0;
        }

        value = ok && raw != 0;
        return ok;
    }
}

// One pose read through a list of usage names, remembering which one answers.
// Unity's CommonUsages names are tried first; the OpenXR feature names are the
// fallback if this provider only publishes those.
internal sealed class PoseFeature
{
    private readonly string[] positionNames;
    private readonly string[] rotationNames;
    private int positionIndex = -1;
    private int rotationIndex = -1;

    internal PoseFeature(string[] positionNames, string[] rotationNames)
    {
        this.positionNames = positionNames;
        this.rotationNames = rotationNames;
    }

    internal string Describe() =>
        $"{(positionIndex < 0 ? "?" : positionNames[positionIndex])}/{(rotationIndex < 0 ? "?" : rotationNames[rotationIndex])}";

    internal void Forget()
    {
        positionIndex = -1;
        rotationIndex = -1;
    }

    internal bool Read(ulong device, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        if (device == 0)
            return false;

        if (!ReadPosition(device, out position))
            return false;

        return ReadRotation(device, out rotation);
    }

    private bool ReadPosition(ulong device, out Vector3 position)
    {
        if (positionIndex >= 0 && XrDevices.Read(device, positionNames[positionIndex], out position))
            return true;

        for (var index = 0; index < positionNames.Length; index++)
        {
            if (XrDevices.Read(device, positionNames[index], out position))
            {
                positionIndex = index;
                return true;
            }
        }

        position = default;
        return false;
    }

    private bool ReadRotation(ulong device, out Quaternion rotation)
    {
        if (rotationIndex >= 0 && XrDevices.Read(device, rotationNames[rotationIndex], out rotation))
            return Valid(ref rotation);

        for (var index = 0; index < rotationNames.Length; index++)
        {
            if (XrDevices.Read(device, rotationNames[index], out rotation))
            {
                rotationIndex = index;
                return Valid(ref rotation);
            }
        }

        rotation = Quaternion.identity;
        return false;
    }

    // An untracked pose can come back as all zeros, which is not a rotation.
    private static bool Valid(ref Quaternion rotation)
    {
        var lengthSquared = rotation.x * rotation.x + rotation.y * rotation.y
            + rotation.z * rotation.z + rotation.w * rotation.w;

        if (lengthSquared < 0.5f)
        {
            rotation = Quaternion.identity;
            return false;
        }

        return true;
    }
}
