using PowerWashVR.OpenXR;
using UnityEngine;

namespace PowerWashVR;

// Controller vibration: a steady buzz in the washer hand while spraying, and
// short ticks for the mod's own interactions (grabbing a nozzle, attaching it,
// switching washers). Driven straight through the OpenXR provider, so it does
// not depend on the game's gamepad rumble settings.
internal sealed class Haptics
{
    private const float Refresh = 0.05f;
    private const float BuzzLength = 0.1f;

    private float washerSent = -1f;
    private float nextWasherSend;
    private float buzzSince = -1f;
    private bool fadedOut;

    internal Settings? Settings;

    // The in-game Vibration Intensity (Normal 1, Light 0.5, Off 0).
    internal static float UserScale = 1f;

    internal void Update(Settings settings, bool washing)
    {
        Settings = settings;
        var amplitude = settings.Haptics && washing ? settings.SprayHaptics * settings.HapticStrength * UserScale : 0f;

        // Rumble Fade Out: a buzz that has run for the chosen time stops, and
        // stays stopped until the rumble itself stops and starts again.
        var now = Time.unscaledTime;
        if (amplitude < 0.02f)
        {
            buzzSince = -1f;
            fadedOut = false;
        }
        else
        {
            if (buzzSince < 0f)
                buzzSince = now;
            if (now - buzzSince > settings.RumbleFadeSeconds)
                fadedOut = true;
            if (fadedOut)
                amplitude = 0f;
        }
        Drive(settings.LeftHanded ? Bindings.LeftDevices : Bindings.RightDevices, amplitude,
            ref washerSent, ref nextWasherSend);
    }

    // A single short impulse.
    internal void Pulse(bool rightHand, float amplitude, float duration)
    {
        var settings = Settings;
        if (settings is null || !settings.Haptics)
            return;

        var devices = rightHand ? Bindings.RightDevices : Bindings.LeftDevices;
        if (UserScale <= 0f)
            return;
        var strength = Mathf.Clamp01(amplitude * settings.HapticStrength * UserScale);
        try
        {
            for (var index = 0; index < devices.Length; index++)
                Native.SendHapticImpulse(devices[index], strength, 0f, duration);
        }
        catch
        {
            // Haptics are never worth breaking the frame over.
        }

        // The buzz is re-sent on its next refresh, after the tick.
        if (rightHand != settings.LeftHanded)
            nextWasherSend = Time.unscaledTime + duration;
    }

    internal void Stop()
    {
        StopAll(Bindings.LeftDevices);
        StopAll(Bindings.RightDevices);
        washerSent = -1f;
    }

    // A continuous buzz is a chain of short impulses, refreshed before each
    // one runs out and re-sent at once when the strength changes.
    private static void Drive(uint[] devices, float amplitude, ref float sent, ref float nextSend)
    {
        var now = Time.unscaledTime;

        if (amplitude < 0.02f)
        {
            if (sent != 0f)
            {
                StopAll(devices);
                sent = 0f;
            }

            return;
        }

        if (Mathf.Abs(amplitude - sent) < 0.03f && now < nextSend)
            return;

        try
        {
            for (var index = 0; index < devices.Length; index++)
                Native.SendHapticImpulse(devices[index], amplitude, 0f, BuzzLength);
        }
        catch
        {
            // See above.
        }

        sent = amplitude;
        nextSend = now + Refresh;
    }

    private static void StopAll(uint[] devices)
    {
        try
        {
            for (var index = 0; index < devices.Length; index++)
                Native.StopHaptics(devices[index]);
        }
        catch
        {
            // See above.
        }
    }
}
