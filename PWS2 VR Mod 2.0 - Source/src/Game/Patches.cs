using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppFuturLab.PW2;
using MelonLoader;

namespace PowerWashVR.Game;

// The few places where the game's own code has to be told about VR. Every
// patch is a no-op unless VR is running.
internal static class Patches
{
    internal static bool VrActive;

    internal static Action? CameraPreRender;
    internal static Action? CameraPostRender;

    // While the mod runs a wheel, every caller of RadialMenuUI.UpdateInput -
    // the game's own update included - gets the mod's direction in controller
    // mode. With two writers disagreeing (the game's mouse/keyboard path follows
    // the view), the highlight flipped back and forth and replayed its sound.
    internal static bool WheelForced;
    internal static UnityEngine.Vector2 WheelDirection;

    // The washer is driven by the controllers, not by the game's bindings:
    // while VR runs, BaseInput.Fire of the local player reads this.
    internal static bool FireWanted;
    internal static IntPtr InputPointer;
    internal static long FireReads;

    private static MelonLogger.Instance? patchLog;
    private static bool packsInstalled;

    // Job packs: online the screen is filled twice (local data, then the
    // live-service data); an empty second fill wiped the packs. Also without
    // VR (Auto Detect SteamVR found no SteamVR), where nothing else is patched.
    internal static void InstallPacks(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        if (packsInstalled)
            return;
        packsInstalled = true;
        patchLog ??= log;
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.JobPackSelectionState), "PopulateScreen", null),
            nameof(BeforePopulatePacks), "JobPackSelectionState.PopulateScreen");
        // The slider's preview sets the pipeline's render scale directly: the
        // setter itself is held while VR runs, and FSR 1 (VR or flat) keeps
        // its own scale.
        Prefix(harmony, log, typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset).GetProperty("renderScale")?.GetSetMethod(),
            nameof(BeforeRenderScaleSet), "UniversalRenderPipelineAsset.renderScale (set)");
    }

    internal static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        patchLog = log;
        // The game's settings are written by one call, with the finished
        // JSON: while VR runs it writes your own (flat) values instead, so
        // the game's settings are never VR's, crash or not.
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.SettingsSaveManager), "TrySave", new[] { typeof(string) }),
            nameof(BeforeSettingsWrite), "SettingsSaveManager.TrySave(string)");
        // Render scale: changing it under the headset re-creates the eye
        // buffers, which crashes the game. In VR the new value is kept for
        // the next start (applied as the game starts, before VR).
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.Settings.RenderScaleSettingStrategy), "HandleSettingChanged", null),
            nameof(BeforeRenderScale), "RenderScaleSettingStrategy.HandleSettingChanged");
        // The game writes every setting into a log line before it saves them;
        // in VR that line crashed the game (crash dumps: an access violation
        // inside GetDataLogString's formatting of a setting's name). It is
        // only a log line, so in VR it is left empty.
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.SettingsSaveManager), "GetDataLogString", null),
            nameof(BeforeDataLogString), "SettingsSaveManager.GetDataLogString");
        InstallPacks(harmony, log);
        // No "unbound controls" warning, and no tutorial steps, in VR.
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.SettingsScreenState), "SendUnboundControlsWarningToastIfNeeded", Type.EmptyTypes),
            nameof(SkipInVr), "SettingsScreenState.SendUnboundControlsWarningToastIfNeeded");
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.KeyRebindingController), "AreControlsUnbound", Type.EmptyTypes),
            nameof(NotUnboundInVr), "KeyRebindingController.AreControlsUnbound");
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.UI.Widgets.HUD.TutorialWidget), "DisplayNewTutorialStep", null),
            nameof(SkipInVr), "TutorialWidget.DisplayNewTutorialStep");
        // Furniture storage: clicking an item that is out in the hub stores it
        // (the game only does that with its "store" button on the hovered item).
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.UI.FurnitureInventoryItem), "OnItemClicked", Type.EmptyTypes),
            nameof(BeforeFurnitureItemClicked), "FurnitureInventoryItem.OnItemClicked");
        // Leaving the pause menu re-applies the graphics settings, which sets
        // the window's resolution (SavedGraphicsSettings.Apply): under the
        // headset that re-creates the eye buffers and crashes the game. The
        // window does not matter in VR, so its resolution is left alone.
        foreach (var method in typeof(UnityEngine.Screen).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name == "SetResolution" && method.ReturnType == typeof(void)
                && method.GetParameters().All(parameter => !parameter.ParameterType.IsByRef))
                Prefix(harmony, log, method, nameof(BeforeSetResolution),
                    $"Screen.SetResolution({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})");
        }

        Prefix(harmony, log, Find(typeof(WashEquipment), "SetWashDirection", Type.EmptyTypes),
            nameof(SkipWhileVr), "WashEquipment.SetWashDirection");
        Postfix(harmony, log, Find(typeof(WashEquipment), "RaycastUpdate", Type.EmptyTypes),
            nameof(AfterRaycastUpdate), "WashEquipment.RaycastUpdate");
        Postfix(harmony, log, Find(typeof(PlayerCameraController), "OnPlayerCameraPreRender", null),
            nameof(AfterCameraPreRender), "PlayerCameraController.OnPlayerCameraPreRender");
        Postfix(harmony, log, typeof(BaseInput).GetProperty("Fire", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod(),
            nameof(AfterFire), "BaseInput.get_Fire");
        Postfix(harmony, log, Find(typeof(PlayerCameraController), "OnPlayerCameraPostRender", null),
            nameof(AfterCameraPostRender), "PlayerCameraController.OnPlayerCameraPostRender");

        // Interaction (pick up, ladders, placement) aims from the free hand
        // instead of the head: the selector raycasts from the player camera's
        // transform, which is moved to the hand while the interaction manager
        // runs and put back right after.
        // (The selector's own queries have out-parameters of game objects,
        // which Il2CppInterop's patch trampolines cannot pass - patching them
        // made every query throw.)
        var manager = typeof(Il2CppFuturLab.PW2.PlayerInteractionManager);
        foreach (var name in new[] { "Update", "FixedUpdate", "LateUpdate", "RefreshTargetAndUpdatePrompts" })
        {
            var target = Find(manager, name, Type.EmptyTypes);
            Prefix(harmony, log, target, nameof(BeforeInteraction), $"PlayerInteractionManager.{name}");
            Postfix(harmony, log, target, nameof(AfterSelect), $"PlayerInteractionManager.{name} (after)");
        }
        var onInput = Find(manager, "OnInteractInput", null);
        Prefix(harmony, log, onInput, nameof(BeforeInteraction), "PlayerInteractionManager.OnInteractInput");
        Postfix(harmony, log, onInput, nameof(AfterSelect), "PlayerInteractionManager.OnInteractInput (after)");

        // The game shows its first-person washer only while its player camera
        // renders (pre-render puts it on the first-person layer, post-render
        // takes it off). The smooth camera renders at another time, so the
        // first-person state is put back after the player camera is done.
        Prefix(harmony, log, Find(typeof(PlayerCameraController), "OnPlayerCameraPreRender", null),
            nameof(BeforePlayerPreRender), "PlayerCameraController.OnPlayerCameraPreRender (before)");
        Postfix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.PowerWasherAssembler), "SetVisualsLayer", null),
            nameof(AfterSetVisualsLayer), "PowerWasherAssembler.SetVisualsLayer");
        Postfix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.CharacterVisualsFirstPerson), "SetRenderersEnabled", null),
            nameof(AfterSetRenderersEnabled), "CharacterVisualsFirstPerson.SetRenderersEnabled");
        Postfix(harmony, log, Find(typeof(PlayerCameraController), "OnPlayerCameraPostRender", null),
            nameof(KeepFirstPerson), "PlayerCameraController.OnPlayerCameraPostRender (first person)");
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI), "UpdateInput",
                new[] { typeof(UnityEngine.Vector2), typeof(Il2CppFuturLab.PW2.UI.RadialMenuInputMode) }),
            nameof(BeforeWheelInput), "RadialMenuUI.UpdateInput");
        // The shell grass is drawn with Graphics.DrawProceduralIndirect from
        // Update, which in multi-pass stereo lands in one eye only; in VR it
        // is not generated or drawn at all (which also saves its compute work).
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.ShellTextureGeometry), "Update", Type.EmptyTypes),
            nameof(SkipGrass), "ShellTextureGeometry.Update");
        // Which game button really reacted to a click (the pointer only
        // reports that it delivered one).
        Postfix(harmony, log, Find(typeof(Il2CppFuturLab.FuturButton), "OnClick", Type.EmptyTypes),
            nameof(AfterButtonClick), "FuturButton.OnClick");
        // Almost every game button plays its click sound from its Clicked
        // event, whichever way the event was raised (also the click-on-down
        // pointer path that skips OnClick): proof the button reacted.
        Postfix(harmony, log, Find(typeof(Il2CppFuturLab.PlayFmodEventOnButtonPress), "OnButtonClick", Type.EmptyTypes),
            nameof(AfterButtonSound), "PlayFmodEventOnButtonPress.OnButtonClick");
        // Graphics settings applied while VR runs must not swap the render
        // pipeline asset under the XR session (the game crashed there).
        Prefix(harmony, log, Find(typeof(Il2CppFuturLab.PW2.GraphicsPipelineManager), "ApplySettings", null),
            nameof(BeforePipelineApply), "GraphicsPipelineManager.ApplySettings");
        Prefix(harmony, log, Find(typeof(PlayerCharacter), "SetCameraMode", new[] { typeof(PlayerCameraMode) }),
            nameof(BeforeSetCameraMode), "PlayerCharacter.SetCameraMode");

        // Split screen (couch play) has no place in VR: its button is hidden
        // and cannot start a session.
        var couch = typeof(Il2CppFuturLab.PW2.UI.CouchPlayWidget);
        Prefix(harmony, log, Find(couch, "OnStartCouchPlayButtonClicked", Type.EmptyTypes),
            nameof(BlockCouchPlay), "CouchPlayWidget.OnStartCouchPlayButtonClicked");
        Postfix(harmony, log, Find(couch, "WidgetAwake", Type.EmptyTypes),
            nameof(HideCouchPlay), "CouchPlayWidget.WidgetAwake");
        Postfix(harmony, log, Find(couch, "UpdateCouchPlayVisuals", null),
            nameof(HideCouchPlay), "CouchPlayWidget.UpdateCouchPlayVisuals");
    }

    private static bool BlockCouchPlay() => !VrActive;



    private static void HideCouchPlay(Il2CppFuturLab.PW2.UI.CouchPlayWidget __instance)
    {
        if (!VrActive)
            return;
        try
        {
            var go = __instance.gameObject;
            var element = go.GetComponent<UnityEngine.UI.LayoutElement>() ?? go.AddComponent<UnityEngine.UI.LayoutElement>();
            element.ignoreLayout = true;
            go.transform.localScale = UnityEngine.Vector3.zero;
        }
        catch
        {
            // Cosmetic only; the start button stays blocked either way.
        }
    }

    private static MethodInfo? Find(Type type, string name, Type[]? parameters)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            return parameters is null ? type.GetMethod(name, flags) : type.GetMethod(name, flags, parameters);
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }

    private static void Prefix(HarmonyLib.Harmony harmony, MelonLogger.Instance log, MethodInfo? target,
        string handler, string label) => Patch(harmony, log, target, handler, label, true);

    private static void Postfix(HarmonyLib.Harmony harmony, MelonLogger.Instance log, MethodInfo? target,
        string handler, string label) => Patch(harmony, log, target, handler, label, false);

    private static void Patch(HarmonyLib.Harmony harmony, MelonLogger.Instance log, MethodInfo? target,
        string handler, string label, bool prefix)
    {
        if (target is null)
        {
            log.Warning($"patch: {label} not found in this game version");
            return;
        }

        try
        {
            var method = new HarmonyMethod(typeof(Patches).GetMethod(handler, BindingFlags.Static | BindingFlags.NonPublic));
            if (prefix)
                harmony.Patch(target, prefix: method);
            else
                harmony.Patch(target, postfix: method);
        }
        catch (Exception exception)
        {
            log.Warning($"patch: {label} failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // The game turns the jet toward the screen crosshair here. In VR the jet
    // leaves along the barrel instead.
    internal static long GrassSkips;
    internal static IntPtr LastClickedButton;
    internal static int LastClickedFrame = -1;

    private static void AfterButtonClick(Il2CppFuturLab.FuturButton __instance)
    {
        LastClickedButton = __instance is null ? IntPtr.Zero : __instance.Pointer;
        LastClickedFrame = UnityEngine.Time.frameCount;
        ButtonHookSeen = true;
    }

    internal static bool ButtonHookSeen;
    internal static bool SoundHookSeen;

    private static void AfterButtonSound(Il2CppFuturLab.PlayFmodEventOnButtonPress __instance)
    {
        try
        {
            var button = __instance is null ? null : __instance.m_button;
            if (button is null || button == null)
                return;
            LastClickedButton = button.Pointer;
            LastClickedFrame = UnityEngine.Time.frameCount;
            SoundHookSeen = true;
        }
        catch
        {
            // The OnClick hook still covers OnClick.
        }
    }

    internal static int PipelineApplySkips;

    // The game applies graphics settings by swapping in a different URP
    // asset (one per preset and anti-aliasing / HDR choice); doing that under
    // a running XR session crashed the game. In VR the parts that can change
    // in place are applied to the asset in use (shadows, shadow distance and
    // resolution) and the swap is skipped; textures, models, fog and the rest
    // are applied by the game's other systems as usual.
    private static bool BeforePipelineApply(Il2CppFuturLab.PW2.SavedGraphicsSettings __0,
        ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (!VrActive)
            return true;

        PipelineApplySkips++;
        try
        {
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                ?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
            if (asset is not null && asset != null && __0 is not null)
            {
                asset.shadowDistance = __0.URPCustom_ShadowDistance;
                Il2CppFuturLab.FuturExtensions.SetMainLightShadowsEnabled(asset, __0.URPCustom_ShadowsEnabled);
                Il2CppFuturLab.FuturExtensions.SetMainLightShadowResolution(asset,
                    (UnityEngine.Rendering.Universal.ShadowResolution)__0.URPCustom_ShadowResolution);
                PipelineApplied = $"shadows {__0.URPCustom_ShadowsEnabled}, distance {__0.URPCustom_ShadowDistance:0}, "
                    + $"resolution {__0.URPCustom_ShadowResolution} on \"{asset.name}\"";
            }
        }
        catch (Exception exception)
        {
            PipelineApplied = $"failed ({exception.GetType().Name}: {exception.Message})";
        }

        __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        return false;
    }

    internal static string PipelineApplied = "";

    // The render scale asked for while VR runs (shown in the log; used from
    // the next start).
    internal static float DeferredRenderScale = -1f;

    private static bool SkipInVr() => !VrActive;

    private static bool BeforeFurnitureItemClicked(Il2CppFuturLab.PW2.UI.FurnitureInventoryItem __instance)
    {
        if (!VrActive)
            return true;
        try
        {
            var data = __instance.FurniturePlacementData;
            var placement = data.PlacementState;
            if (placement != Il2CppFuturLab.PW2.UI.FurnitureUIPlacementState.DirtyInWorld
                && placement != Il2CppFuturLab.PW2.UI.FurnitureUIPlacementState.CleanInWorld)
                return true;
            var pointer = GameState.StatePointer;
            if (pointer == IntPtr.Zero)
                return true;
            var state = new Il2CppSystem.Object(pointer).TryCast<Il2CppFuturLab.PW2.FurnitureInventoryState>();
            if (state is null)
                return true;
            state.m_lastHoveredFurnitureItem = __instance;
            state.SecondaryActionPressed();
            patchLog?.Msg($"placement: furniture that was out stored from the storage ({placement})");
            return false;
        }
        catch (Exception exception)
        {
            patchLog?.Warning($"placement: storing the clicked furniture failed ({exception.GetType().Name}: {exception.Message})");
            return true;
        }
    }

    private static IntPtr packState;
    private static int packCount;

    private static bool BeforePopulatePacks(Il2CppFuturLab.PW2.JobPackSelectionState __instance,
        Il2CppSystem.Collections.Generic.List<Il2CppFuturLab.PW2.UI.CampaignProgressData> __0)
    {
        try
        {
            var count = __0 is null ? 0 : __0.Count;
            if (__instance.Pointer != packState)
            {
                packState = __instance.Pointer;
                packCount = 0;
            }

            if (count == 0 && packCount > 0)
            {
                patchLog?.Msg($"job packs: an empty refill skipped (the {packCount} pack(s) shown stay)");
                return false;
            }

            patchLog?.Msg($"job packs: {count} pack(s) shown");
            packCount = Math.Max(packCount, count);
        }
        catch (Exception exception)
        {
            patchLog?.Msg($"job packs: not checked ({exception.GetType().Name})");
        }

        return true;
    }

    private static bool NotUnboundInVr(ref bool __result)
    {
        if (!VrActive)
            return true;
        __result = false;
        return false;
    }

    internal static int DataLogSkips;

    internal static string DataLogKinds = "";

    private static bool BeforeDataLogString(
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppFuturLab.Settings.SettingType, Il2CppSystem.Object> __0, ref string __result)
    {
        if (!VrActive)
            return true;
        // Diagnostics (once): each setting's value class, read without
        // formatting anything.
        if (DataLogSkips == 0 && __0 is not null)
        {
            try
            {
                var parts = new List<string>();
                foreach (var entry in __0)
                {
                    var value = entry.Value;
                    var klass = value is null ? IntPtr.Zero : Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(value.Pointer);
                    var name = klass == IntPtr.Zero ? "null"
                        : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(klass)) ?? "?";
                    parts.Add($"{(int)entry.Key}:{name}");
                }

                DataLogKinds = string.Join(" ", parts);
                patchLog?.Msg($"settings: saving in VR; value classes: {DataLogKinds}");
                DataLogKinds = "";
            }
            catch (Exception exception)
            {
                DataLogKinds = $"not read ({exception.GetType().Name})";
            }
        }

        try
        {
            if (__0 is not null)
                VrSettings.Current?.Repair(__0);
        }
        catch (Exception exception)
        {
            patchLog?.Warning($"settings: save check failed ({exception.GetType().Name}: {exception.Message})");
        }

        DataLogSkips++;
        __result = "";
        return false;
    }

    internal static int ResolutionSkips;

    private static bool BeforeSetResolution()
    {
        if (!VrActive)
            return true;
        ResolutionSkips++;
        return false;
    }

    private static int renderScaleBlocks;

    // FSR 1 (VR VIDEO): the render scale it upscales from, fixed for the
    // session (0 when off). While it is on, the pipeline keeps that scale
    // whatever the Render Scale setting asks for.
    internal static float FsrScale;
    internal static float RequestedRenderScale = -1f;
    private static bool forcingScale;

    internal static void ForceRenderScale(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset asset, float scale)
    {
        forcingScale = true;
        try
        {
            asset.renderScale = scale;
        }
        finally
        {
            forcingScale = false;
        }
    }

    private static bool BeforeRenderScaleSet(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset __instance, float __0)
    {
        if (forcingScale)
            return true;
        // What the game itself asks for (put back when flat FSR is switched off).
        RequestedRenderScale = __0;
        if (FsrScale > 0f)
        {
            if (UnityEngine.Mathf.Abs(__0 - FsrScale) < 1e-4f)
                return true;
            if (!VrActive || VrSettings.Applying)
                ForceRenderScale(__instance, FsrScale);
            else
                DeferredRenderScale = __0;
            return false;
        }

        if (!VrActive || VrSettings.Applying)
            return true;
        try
        {
            if (UnityEngine.Mathf.Abs(__instance.renderScale - __0) < 1e-4f)
                return true;
        }
        catch
        {
        }

        DeferredRenderScale = __0;
        renderScaleBlocks++;
        return false;
    }

    internal static int RenderScaleBlocks => renderScaleBlocks;

    private static bool BeforeRenderScale(float __0)
    {
        if (!VrActive || VrSettings.Applying)
            return true;
        DeferredRenderScale = __0;
        return false;
    }

    private static bool writingFlat;

    // The game's settings file is never written while VR runs (nor while
    // the game closes after a VR session): VR's values go to the mod's own
    // files (UserData/PowerWashVR) instead, and the game's file keeps your
    // own settings untouched.
    // The one deliberate write to the game's file in VR: your own values,
    // put back after an older version left VR's there.
    internal static bool SaveToGameFile(Il2CppFuturLab.PW2.SettingsSaveManager saver,
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppFuturLab.Settings.SettingType, Il2CppSystem.Object> data)
    {
        writingFlat = true;
        try
        {
            return saver.TrySave(data);
        }
        finally
        {
            writingFlat = false;
        }
    }

    private static bool BeforeSettingsWrite(Il2CppFuturLab.PW2.SettingsSaveManager __instance, string __0, ref bool __result)
    {
        if (writingFlat)
            return true;
        if (!VrSettings.KeepsGameFileClosed)
            return true;
        try
        {
            VrSettings.Current?.SaveVr();
        }
        catch
        {
            // The VR files are written again on the next save.
        }

        __result = true;
        return false;
    }

    private static void BeforeSettingsSave(
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppFuturLab.Settings.SettingType, Il2CppSystem.Object> __0)
    {
        try
        {
            VrSettings.Current?.BeforeGameSave(__0);
        }
        catch
        {
            // The game saves as it would have.
        }
    }

    private static void AfterSettingsSave(
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppFuturLab.Settings.SettingType, Il2CppSystem.Object> __0)
    {
        try
        {
            VrSettings.Current?.AfterGameSave(__0);
        }
        catch
        {
            // Nothing to put back.
        }
    }

    // No exclusive full screen while VR runs (the window is only a mirror).
    private static void AfterWindowModes(ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray __result)
    {
        try
        {
            if (!VrActive || VrSettings.UnfilteredOptions || __result is null)
                return;
            var kept = __result.Where(value => value is null || !value.Contains("Exclusive", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (kept.Length != __result.Length && kept.Length > 0)
                __result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(kept);
        }
        catch
        {
            // Leave the list as the game made it.
        }
    }

    private static bool SkipGrass()
    {
        if (!VrActive)
            return true;
        GrassSkips++;
        return false;
    }

    private static bool SkipWhileVr() => !(VrActive && WasherFixes.Active);

    // The wash job is built from m_nozzleInstances. Origin and direction come
    // from the hand-held nozzle. NozzleInstance is a struct, so each element is
    // a copy and has to be stored back.
    private static void AfterRaycastUpdate(WashEquipment __instance)
    {
        if (!VrActive || !WasherFixes.Active || __instance.Pointer != WasherFixes.EquipmentPointer)
            return;

        try
        {
            var instances = __instance.m_nozzleInstances;
            if (instances is null)
                return;

            var count = Math.Min(instances.Length, WasherFixes.JetCount);
            for (var index = 0; index < count; index++)
            {
                var instance = instances[index];
                if (instance is null)
                    continue;

                instance.Origin = WasherFixes.JetOrigin[index];
                instance.Direction = WasherFixes.JetDirection[index];
                instances[index] = instance;
            }
        }
        catch
        {
            // Never let a failure here surface inside the game's own update.
        }
    }

    internal static bool AimValid;
    internal static UnityEngine.Vector3 AimPosition;
    internal static UnityEngine.Quaternion AimRotation = UnityEngine.Quaternion.identity;
    private static int aimDepth;
    private static UnityEngine.Vector3 savedPosition;
    private static UnityEngine.Quaternion savedRotation;
    private static UnityEngine.Transform? savedTransform;

    // The local player's interaction manager (for the palm line).
    internal static Il2CppFuturLab.PW2.PlayerInteractionManager? Interaction;
    // The local player character (set every frame).
    internal static IntPtr LocalPlayer;

    private static void BeforeInteraction(Il2CppFuturLab.PW2.PlayerInteractionManager __instance)
    {
        try
        {
            if (!VrActive || !AimValid || __instance is null || aimDepth++ > 0)
                return;
            // Only this machine's player: in multiplayer the other players'
            // managers run too (aiming theirs from this hand took the laser's
            // target away, and could leave the local one unaimed).
            if (LocalPlayer != IntPtr.Zero)
            {
                var player = __instance.m_player;
                if (player is null || player == null || player.Pointer != LocalPlayer)
                    return;
            }

            var selector = __instance.InteractionSelector;
            // Only the local player's selector aims from the camera.
            if (selector is null || selector == null || selector.TryCast<Il2CppFuturLab.PW2.PlayerCameraInteractionSelector>() is null)
                return;
            Interaction = __instance;
            // The selector casts from the player camera's transform (not its
            // own): that is what is moved to the hand for the call.
            var camera = selector.TryCast<Il2CppFuturLab.PW2.PlayerCameraInteractionSelector>()!.m_playerCharacter?.PlayerCamera;
            if (camera is null || camera == null)
                return;
            var transform = camera.transform;
            transform.GetPositionAndRotation(out savedPosition, out savedRotation);
            savedTransform = transform;
            transform.SetPositionAndRotation(AimPosition, AimRotation);
        }
        catch
        {
            savedTransform = null;
        }
    }

    private static void AfterSelect()
    {
        try
        {
            if (aimDepth > 0 && --aimDepth == 0 && savedTransform is not null && savedTransform != null)
                savedTransform.SetPositionAndRotation(savedPosition, savedRotation);
        }
        catch
        {
            // Restored by the next head update anyway.
        }
        finally
        {
            if (aimDepth == 0)
                savedTransform = null;
        }
    }

    internal static bool KeepFirstPersonVisuals;
    private static bool inPreRender, restoring;
    // What the player camera's pre-render did this frame, exactly (which
    // washers went to which layer, which visuals were switched on or off), so
    // it can be repeated after its post-render for the smooth camera. Only
    // repeating "on" for everything showed parts the game had switched off
    // (a second washer and hands while it swaps nozzles).
    private static readonly List<(Il2CppFuturLab.PW2.PowerWasherAssembler Assembler, Il2CppFuturLab.PW2.GameLayer Layer)> fpLayers = new();
    private static readonly List<(Il2CppFuturLab.PW2.CharacterVisualsFirstPerson Visuals, bool On)> fpRenderers = new();

    // In multiplayer every player's camera controller runs these for every
    // camera: only the local player's are recorded and repeated (another
    // player's pre-render used to clear the list, and the smooth camera lost
    // the washer).
    internal static IntPtr LocalController;
    private static bool recordingLocal;

    private static bool IsLocal(PlayerCameraController? controller) =>
        controller is not null && (LocalController == IntPtr.Zero || controller.Pointer == LocalController);

    private static void BeforePlayerPreRender(PlayerCameraController __instance)
    {
        inPreRender = true;
        recordingLocal = IsLocal(__instance);
        if (!recordingLocal)
            return;
        fpLayers.Clear();
        fpRenderers.Clear();
    }

    private static void AfterSetVisualsLayer(Il2CppFuturLab.PW2.PowerWasherAssembler __instance, Il2CppFuturLab.PW2.GameLayer __0)
    {
        if (!inPreRender || !recordingLocal || restoring || __instance is null)
            return;
        // A plain loop: this runs in every pre-render (no closure each time).
        var pointer = __instance.Pointer;
        for (var index = fpLayers.Count - 1; index >= 0; index--)
        {
            var assembler = fpLayers[index].Assembler;
            if (assembler is null || assembler.Pointer == pointer || assembler == null)
                fpLayers.RemoveAt(index);
        }

        fpLayers.Add((__instance, __0));
    }

    private static void AfterSetRenderersEnabled(Il2CppFuturLab.PW2.CharacterVisualsFirstPerson __instance, bool __0)
    {
        if (!inPreRender || !recordingLocal || restoring || __instance is null)
            return;
        var pointer = __instance.Pointer;
        for (var index = fpRenderers.Count - 1; index >= 0; index--)
        {
            var visuals = fpRenderers[index].Visuals;
            if (visuals is null || visuals.Pointer == pointer || visuals == null)
                fpRenderers.RemoveAt(index);
        }

        fpRenderers.Add((__instance, __0));
    }

    private static void KeepFirstPerson(PlayerCameraController __instance)
    {
        inPreRender = false;
        if (!VrActive || !KeepFirstPersonVisuals || restoring || !IsLocal(__instance))
            return;
        restoring = true;
        try
        {
            foreach (var (assembler, layer) in fpLayers)
            {
                if (assembler is not null && assembler != null && assembler.gameObject.activeInHierarchy)
                    assembler.SetVisualsLayer(layer);
            }

            foreach (var (visuals, on) in fpRenderers)
            {
                if (visuals is not null && visuals != null)
                    visuals.SetRenderersEnabled(on);
            }
        }
        catch
        {
            // The smooth camera then simply misses the washer.
        }
        finally
        {
            restoring = false;
        }
    }

    private static void AfterCameraPostRender(UnityEngine.Camera __1)
    {
        if (!VrActive || __1 is null || __1.stereoTargetEye == UnityEngine.StereoTargetEyeMask.None)
            return;
        CameraPostRender?.Invoke();
    }

    private static void AfterCameraPreRender()
    {
        inPreRender = false;
        if (VrActive)
            CameraPreRender?.Invoke();
    }

    // In VR the view is always your own eyes: the game's third-person camera
    // (crouching, some stances) is replaced by first person.
    private static void BeforeSetCameraMode(ref PlayerCameraMode __0)
    {
        if (VrActive && __0 == PlayerCameraMode.ThirdPerson)
            __0 = PlayerCameraMode.FirstPerson;
    }

    private static void BeforeWheelInput(ref UnityEngine.Vector2 __0, ref Il2CppFuturLab.PW2.UI.RadialMenuInputMode __1)
    {
        if (!VrActive || !WheelForced)
            return;

        __0 = WheelDirection;
        __1 = Il2CppFuturLab.PW2.UI.RadialMenuInputMode.ConsoleController;
    }

    private static void AfterFire(BaseInput __instance, ref bool __result)
    {
        if (!VrActive || InputPointer == IntPtr.Zero || __instance.Pointer != InputPointer)
            return;

        FireReads++;
        __result = FireWanted;
    }
}
