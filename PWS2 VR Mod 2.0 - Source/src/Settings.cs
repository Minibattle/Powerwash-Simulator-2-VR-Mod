using MelonLoader;
using UnityEngine;

namespace PowerWashVR;

// Every option lives in UserData/MelonPreferences.cfg under [PowerWashVR].
// Close the game before editing that file; MelonLoader rewrites it on exit.
internal sealed class Settings
{
    private readonly MelonPreferences_Entry<bool> autoStart;
    private readonly MelonPreferences_Entry<string> startKey;

    private readonly MelonPreferences_Entry<string> renderMode;
    private readonly MelonPreferences_Entry<string> latencyMode;
    private readonly MelonPreferences_Entry<float> resolutionScale;
    private readonly MelonPreferences_Entry<string> disabledRenderFeatures;
    private readonly MelonPreferences_Entry<string> disabledPostEffects;
    private readonly MelonPreferences_Entry<bool> smoothPlayerBody;
    private readonly MelonPreferences_Entry<bool> ambientOcclusion;
    private readonly MelonPreferences_Entry<bool> lightBeams;
    private readonly MelonPreferences_Entry<bool> gpuResidentDrawer;


    private readonly MelonPreferences_Entry<bool> leftHanded;
    private readonly MelonPreferences_Entry<bool> snapTurn;
    private readonly MelonPreferences_Entry<bool> turnOff;
    private readonly MelonPreferences_Entry<float> snapAngle;
    private readonly MelonPreferences_Entry<float> smoothTurnSpeed;
    private readonly MelonPreferences_Entry<bool> swapHandButtons;
    private readonly MelonPreferences_Entry<bool> beltStrap;
    private readonly MelonPreferences_Entry<bool> nozzleNames;
    private readonly MelonPreferences_Entry<bool> laserSmoothing;
    private readonly MelonPreferences_Entry<bool> disableTutorials;
    private readonly MelonPreferences_Entry<bool> hideInteractionHints;
    private readonly MelonPreferences_Entry<int> rumbleFadeOut;
    private readonly MelonPreferences_Entry<bool> sprintToStand;
    private readonly MelonPreferences_Entry<bool> crouchHitbox;
    private readonly MelonPreferences_Entry<bool> physicalWasher;
    private readonly MelonPreferences_Entry<bool> physicalAttachments;
    private readonly MelonPreferences_Entry<bool> autoDetectSteamVr;
    private readonly MelonPreferences_Entry<int> controlPreset;
    private readonly MelonPreferences_Entry<bool> smoothCamera;
    private readonly MelonPreferences_Entry<float> smoothCameraFov;
    private readonly MelonPreferences_Entry<float> smoothCameraSmoothing;

    private readonly MelonPreferences_Entry<float> washerPitch;
    private readonly MelonPreferences_Entry<float> washerYaw;
    private readonly MelonPreferences_Entry<float> washerRoll;
    private readonly MelonPreferences_Entry<float> washerOffsetX;
    private readonly MelonPreferences_Entry<float> washerOffsetY;
    private readonly MelonPreferences_Entry<float> washerOffsetZ;

    private readonly MelonPreferences_Entry<bool> hands;
    private readonly MelonPreferences_Entry<float> freeHandPitch;
    private readonly MelonPreferences_Entry<float> freeHandYaw;
    private readonly MelonPreferences_Entry<float> freeHandRoll;

    private readonly MelonPreferences_Entry<float> menuDistance;
    private readonly MelonPreferences_Entry<float> menuWidth;
    private readonly MelonPreferences_Entry<float> wristScale;

    private readonly MelonPreferences_Entry<bool> belt;
    private readonly MelonPreferences_Entry<float> beltDrop;
    private readonly MelonPreferences_Entry<int> beltHeight;
    private readonly MelonPreferences_Entry<int> fsr;
    private readonly MelonPreferences_Entry<int> flatFsr;
    private readonly MelonPreferences_Entry<bool> detailedLog;
    private readonly MelonPreferences_Entry<bool> backpack;
    private readonly MelonPreferences_Entry<float> nozzleRotateReach;

    private readonly MelonPreferences_Entry<bool> haptics;
    private readonly MelonPreferences_Entry<float> hapticStrength;
    private readonly MelonPreferences_Entry<float> sprayHaptics;

    internal Settings()
    {
        var c = MelonPreferences.CreateCategory("PowerWashVR", "PowerWash VR");

        autoStart = c.CreateEntry("AutoStart", true,
            description: "Start VR automatically once the game has finished loading.");
        startKey = c.CreateEntry("StartKey", "F8",
            description: "Starts VR by hand if it did not start automatically.");

        renderMode = c.CreateEntry("RenderMode", "MultiPass",
            description: "MultiPass (safe) or SinglePassInstanced (faster on the CPU, but only works if the "
                + "game's shaders support it - try it, and switch back if one eye renders wrong). Read at VR start.");
        latencyMode = c.CreateEntry("LatencyMode", "Rendering",
            description: "Rendering (more GPU headroom) or InputPolling (slightly lower motion latency). Read at VR start.");
        resolutionScale = c.CreateEntry("ResolutionScale", 1.0f,
            description: "Eye-buffer resolution relative to what the headset runtime recommends. "
                + "0.8 is a big performance win with little visible loss.");
        disabledRenderFeatures = c.CreateEntry("DisabledRenderFeatures", "Buto,LightScattering",
            description: "Comma-separated renderer feature type names (substring match) switched off in VR. "
                + "The defaults are the volumetric fog and light scattering passes: expensive, and they only "
                + "render correctly in one eye. Empty keeps everything.");
        disabledPostEffects = c.CreateEntry("DisabledPostEffects",
            "MotionBlur,DepthOfField,LensDistortion,PaniniProjection,ChromaticAberration,FilmGrain",
            description: "Comma-separated post-processing effects switched off in VR (uncomfortable or wrong in a headset).");
        ambientOcclusion = c.CreateEntry("AmbientOcclusion", false,
            description: "Screen-space ambient occlusion. Costly at headset resolution (it runs per eye); off by default.");
        lightBeams = c.CreateEntry("LightBeams", false,
            description: "The volumetric light beams and their dust. They draw in one eye only in VR; off by default.");
        gpuResidentDrawer = c.CreateEntry("GpuResidentDrawer", false,
            description: "Unity's GPU Resident Drawer and its GPU occlusion culling. With multi-pass stereo they "
                + "cull instanced objects (grass) for one eye only; off by default.");
        smoothPlayerBody = c.CreateEntry("SmoothPlayerBody", true,
            description: "Interpolate the player's physics body so walking does not judder at headset refresh rates.");

        leftHanded = c.CreateEntry("LeftHanded", false,
            description: "Hold the washer in the left hand (moves every control to the other hand).");
        PhysicalLeft = leftHanded.Value;
        snapTurn = c.CreateEntry("SnapTurn", false, description: "Snap turning instead of smooth turning.");
        turnOff = c.CreateEntry("TurnOff", false, description: "No stick turning at all (Turn Mode Off).");
        snapAngle = c.CreateEntry("SnapAngle", 30f, description: "Degrees per snap turn.");
        smoothTurnSpeed = c.CreateEntry("SmoothTurnSpeed", 120f, description: "Degrees per second at full stick.");
        swapHandButtons = c.CreateEntry("SwapHandButtons", false,
            description: "Face buttons swapped between the controllers (pause on the right, jump on the left...).");
        smoothCamera = c.CreateEntry("SmoothCamera", false,
            description: "The desktop window shows a separate, smoothed camera instead of the right eye (costs a third render).");
        smoothCameraFov = c.CreateEntry("SmoothCameraFov", 110f, description: "Field of view of the smooth camera.");
        smoothCameraSmoothing = c.CreateEntry("SmoothCameraSmoothing", 0.25f,
            description: "Seconds the smooth camera takes to catch up with the head.");

        washerPitch = c.CreateEntry("WasherPitch", -5.58f, description: "Washer angle in the hand, degrees.");
        washerYaw = c.CreateEntry("WasherYaw", -0.07f, description: "Washer angle in the hand, degrees.");
        washerRoll = c.CreateEntry("WasherRoll", 1.02f, description: "Washer angle in the hand, degrees.");
        washerOffsetX = c.CreateEntry("WasherOffsetX", -0.0015f, description: "Washer position in the hand, metres.");
        washerOffsetY = c.CreateEntry("WasherOffsetY", -0.0076f, description: "Washer position in the hand, metres.");
        washerOffsetZ = c.CreateEntry("WasherOffsetZ", -0.0131f, description: "Washer position in the hand, metres.");

        hands = c.CreateEntry("Hands", true,
            description: "Show your character's own hands (with the gloves you picked), without arms.");
        freeHandPitch = c.CreateEntry("FreeHandPitch", 0f, description: "Extra rotation of the free hand, degrees.");
        freeHandYaw = c.CreateEntry("FreeHandYaw", 0f, description: "Extra rotation of the free hand, degrees.");
        freeHandRoll = c.CreateEntry("FreeHandRoll", 0f, description: "Extra rotation of the free hand, degrees.");

        menuDistance = c.CreateEntry("MenuDistance", 1.4f, description: "Metres to the menu panel.");
        menuWidth = c.CreateEntry("MenuWidth", 1.7f, description: "Width of the menu panel, metres.");
        wristScale = c.CreateEntry("WristScale", 1.0f, description: "Size of the wrist display.");

        belt = c.CreateEntry("Belt", true, description: "Nozzle belt at the waist.");
        beltStrap = c.CreateEntry("BeltStrap", true,
            description: "A strap around the waist that the nozzles hang on (off: the nozzles float a little further out, with no strap).");
        nozzleNames = c.CreateEntry("NozzleLabels", false, description: "The degree labels over the belt's nozzles (off by default).");
        laserSmoothing = c.CreateEntry("LaserSmoothing", true, description: "Steadies the menu laser against hand shake.");
        disableTutorials = c.CreateEntry("DisableTutorials", false, description: "No VR tutorial hints (shoulder grabs, dirt scan) after the first level load.");
        hideInteractionHints = c.CreateEntry("HideInteractionHints", false, description: "No blue guide line from a held nozzle or extension to the washer.");
        rumbleFadeOut = c.CreateEntry("RumbleFadeOut", 1, description: "Stop a continuous rumble after: 0 = 2 s, 1 = 5 s, 2 = 1 minute, 3 = off (never).");
        autoDetectSteamVr = c.CreateEntry("AutoDetectSteamVR", false, description: "Only load the mod when SteamVR is already running when the game starts; otherwise the game runs as if no mod were installed.");
        physicalWasher = c.CreateEntry("PhysicalWasher", false, description: "Hold grip to keep the washer in your hand; let go to drop it (it goes back on your belt), throw it between hands.");
        physicalAttachments = c.CreateEntry("PhysicalAttachments", true, description: "The backpack can be put down in the room; take its extension and soap nozzle with the other hand and fit them to the washer.");
        crouchHitbox = c.CreateEntry("CrouchHitbox", true, description: "While your head is low (for real or with the mod's crouch), a shorter collider replaces the player's for collisions so you fit under anything your head fits under (level geometry included). The game's controller values are not changed.");
        sprintToStand = c.CreateEntry("SprintToStand", true, description: "Clicking the stick to sprint also stands you up from the mod's crouch.");
        controlPreset = c.CreateEntry("ControlPreset", 0,
            description: "0 Default; 1 Less Buttons (no button wheels); 2 R Stick Scan (B crouches, right stick click scans for dirt).");
        beltDrop = c.CreateEntry("BeltDrop", 0.62f, description: "How far below your eyes the belt sits with Belt Height on Default, metres.");
        beltHeight = c.CreateEntry("BeltHeight", 0, description: "0 Default (BeltDrop), 1 High, 2 Higher (about chest height).");
        detailedLog = c.CreateEntry("DetailedLog", false, description: "Write troubleshooting detail to the log (menu clicks, button presses, settings rows). Off for normal play.");
        Diag.Verbose = detailedLog.Value;
        flatFsr = c.CreateEntry("FlatFsrMode", 0, description: "AMD FSR 1 upscaling without VR (flat screen), same values as FsrMode; off by default. Applied straight away.");
        fsr = c.CreateEntry("FsrMode", 2, description: "AMD FSR 1 upscaling in VR: 0 Off, 1 Ultra Quality (77%), 2 Quality (67%), 3 Balanced (59%), 4 Performance (50%). Applied at the next start.");
        backpack = c.CreateEntry("Backpack", true, description: "Washer backpack over the shoulder.");
        nozzleRotateReach = c.CreateEntry("NozzleRotateReach", 0.25f,
            description: "How close the free hand must be to the washer for its trigger to rotate the nozzle, metres.");

        haptics = c.CreateEntry("Haptics", true, description: "Controller vibration.");
        hapticStrength = c.CreateEntry("HapticStrength", 1.0f, description: "0 to 1.");
        sprayHaptics = c.CreateEntry("SprayHaptics", 0.2f, description: "Vibration of the washer hand while spraying, 0 to 1.");
    }

    internal bool AutoStart => autoStart.Value;
    internal KeyCode StartKey => Key(startKey.Value, KeyCode.F8);

    internal OpenXR.Native.RenderMode RenderMode =>
        string.Equals(renderMode.Value?.Trim(), "SinglePassInstanced", StringComparison.OrdinalIgnoreCase)
            ? OpenXR.Native.RenderMode.SinglePassInstanced
            : OpenXR.Native.RenderMode.MultiPass;

    internal OpenXR.Native.LatencyOptimization Latency =>
        string.Equals(latencyMode.Value?.Trim(), "InputPolling", StringComparison.OrdinalIgnoreCase)
            ? OpenXR.Native.LatencyOptimization.PrioritizeInputPolling
            : OpenXR.Native.LatencyOptimization.PrioritizeRendering;

    internal float ResolutionScale => Mathf.Clamp(resolutionScale.Value, 0.3f, 2f);
    internal string[] DisabledRenderFeatures => List(disabledRenderFeatures.Value);
    internal string[] DisabledPostEffects => List(disabledPostEffects.Value);
    internal bool SmoothPlayerBody => smoothPlayerBody.Value;
    internal bool AmbientOcclusion => ambientOcclusion.Value;
    internal bool LightBeams => lightBeams.Value;
    internal bool GpuResidentDrawer => gpuResidentDrawer.Value;

    // With Physical Washer on, the hand that last held the washer is the
    // washer hand (the Left Handed setting is not used).
    internal bool LeftHanded => PhysicalWasher ? PhysicalLeft : leftHanded.Value;
    internal bool LeftHandedSetting => leftHanded.Value;
    internal bool PhysicalLeft;
    internal bool PhysicalWasher => physicalWasher.Value;
    internal bool PhysicalAttachments => physicalAttachments.Value;
    internal void SetPhysicalWasher(bool value)
    {
        if (value && !physicalWasher.Value)
            PhysicalLeft = leftHanded.Value;
        Save(physicalWasher, value);
    }

    internal void SetPhysicalAttachments(bool value) => Save(physicalAttachments, value);

    internal void SetLeftHanded(bool value)
    {
        leftHanded.Value = value;
        MelonPreferences.Save();
    }
    internal bool SnapTurn => snapTurn.Value;
    internal bool TurnOff => turnOff.Value;
    internal void SetTurnMode(int mode)
    {
        Save(turnOff, mode == 2);
        if (mode != 2)
            Save(snapTurn, mode == 1);
    }
    internal float SnapAngle => snapAngle.Value;
    internal float SmoothTurnSpeed => smoothTurnSpeed.Value;
    internal bool SwapHandButtons => swapHandButtons.Value;
    internal bool SmoothCamera => smoothCamera.Value;
    internal float SmoothCameraFov => smoothCameraFov.Value;
    internal float SmoothCameraSmoothing => smoothCameraSmoothing.Value;

    internal void SetSnapTurn(bool value) => Save(snapTurn, value);
    internal void SetSnapAngle(float value) => Save(snapAngle, value);
    internal void SetSmoothTurnSpeed(float value) => Save(smoothTurnSpeed, value);
    internal void SetSwapHandButtons(bool value) => Save(swapHandButtons, value);
    internal void SetSmoothCamera(bool value) => Save(smoothCamera, value);
    internal void SetSmoothCameraFov(float value) => Save(smoothCameraFov, value);

    private static void Save<T>(MelonPreferences_Entry<T> entry, T value)
    {
        entry.Value = value;
        MelonPreferences.Save();
    }

    internal Quaternion WasherRotation => Quaternion.Euler(washerPitch.Value, washerYaw.Value, washerRoll.Value);
    internal Vector3 WasherOffset => new(washerOffsetX.Value, washerOffsetY.Value, washerOffsetZ.Value);

    internal bool Hands => hands.Value;
    internal Quaternion FreeHandRotation => Quaternion.Euler(freeHandPitch.Value, freeHandYaw.Value, freeHandRoll.Value);

    internal float MenuDistance => Mathf.Clamp(menuDistance.Value, 0.4f, 6f);
    internal float MenuWidth => Mathf.Clamp(menuWidth.Value, 0.3f, 8f);
    internal float WristScale => Mathf.Clamp(wristScale.Value, 0.2f, 4f);

    internal bool Belt => belt.Value;
    internal bool BeltStrap => beltStrap.Value;
    internal bool NozzleNames => nozzleNames.Value;
    internal bool LaserSmoothing => laserSmoothing.Value;
    internal bool DisableTutorials => disableTutorials.Value;
    internal bool HideInteractionHints => hideInteractionHints.Value;
    internal void SetDisableTutorials(bool value) => Save(disableTutorials, value);
    internal void SetHideInteractionHints(bool value) => Save(hideInteractionHints, value);
    internal int ControlPreset => Mathf.Clamp(controlPreset.Value, 0, 2);
    internal void SetControlPreset(int value) => Save(controlPreset, value);
    internal void SetBeltStrap(bool value) => Save(beltStrap, value);
    internal void SetNozzleNames(bool value) => Save(nozzleNames, value);
    internal void SetLaserSmoothing(bool value) => Save(laserSmoothing, value);
    internal int RumbleFadeOut => Mathf.Clamp(rumbleFadeOut.Value, 0, 3);
    internal void SetRumbleFadeOut(int value) => Save(rumbleFadeOut, Mathf.Clamp(value, 0, 3));
    // Seconds of continuous rumble before it fades out; infinity when off.
    internal float RumbleFadeSeconds => RumbleFadeOut switch { 0 => 2f, 1 => 5f, 2 => 60f, _ => float.PositiveInfinity };
    internal bool SprintToStand => sprintToStand.Value;
    internal bool CrouchHitbox => crouchHitbox.Value;
    internal bool AutoDetectSteamVR => autoDetectSteamVr.Value;
    internal void SetAutoDetectSteamVR(bool value) => Save(autoDetectSteamVr, value);
    internal void SetSprintToStand(bool value) => Save(sprintToStand, value);
    internal int BeltHeight => Mathf.Clamp(beltHeight.Value, 0, 2);
    internal void SetBeltHeight(int value) => Save(beltHeight, Mathf.Clamp(value, 0, 2));
    internal float BeltDrop => BeltHeight switch { 1 => 0.48f, 2 => 0.34f, _ => Mathf.Clamp(beltDrop.Value, 0.2f, 1.2f) };
    internal int Fsr => Mathf.Clamp(fsr.Value, 0, 4);
    internal void SetFsr(int value) => Save(fsr, Mathf.Clamp(value, 0, 4));
    internal int FlatFsr => Mathf.Clamp(flatFsr.Value, 0, 4);
    internal void SetFlatFsr(int value) => Save(flatFsr, Mathf.Clamp(value, 0, 4));
    // The render scale FSR 1 upscales from (0 when off).
    internal static float FsrScale(int mode) => mode switch { 1 => 0.77f, 2 => 0.67f, 3 => 0.59f, 4 => 0.5f, _ => 0f };
    internal bool Backpack => backpack.Value;
    internal float NozzleRotateReach => Mathf.Clamp(nozzleRotateReach.Value, 0.05f, 1f);

    internal bool Haptics => haptics.Value;
    internal float HapticStrength => Mathf.Clamp01(hapticStrength.Value);
    internal float SprayHaptics => Mathf.Clamp01(sprayHaptics.Value);

    private static KeyCode Key(string? name, KeyCode fallback) =>
        Enum.TryParse<KeyCode>(name, true, out var key) ? key : fallback;

    private static string[] List(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
