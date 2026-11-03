using Il2CppFuturLab.PW2;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace PowerWashVR.Game;

internal enum UiMode
{
    // Walking around with the washer: HUD on the wrist, no menu panel.
    Gameplay,

    // The game's equipment wheel, opened by the mod during gameplay.
    Radial,

    // Anything else: pause menu, shop, job selection, title screen, loading.
    Menu,
}

// What the game is doing, read from its own screen state machine, and the
// handful of game objects the mod talks to.
//
// The current state is read as a raw pointer every frame and classified only
// when it changes, so this costs nothing while nothing happens.
internal sealed class GameState
{
    private readonly MelonLogger.Instance log;
    private readonly RawField currentState = new(typeof(PwsScreenViewport), "_CurrentState_k__BackingField");

    private PwsScreenViewport? viewport;
    private float nextViewportSearch;
    private IntPtr lastState = new(-1);
    private IntPtr lastClass;
    private readonly Dictionary<IntPtr, UiMode> classModes = new();

    private PwsPlayerInput? input;
    private IntPtr inputFor;
    private PlayerCharacter? character;
    private Transform? attach;

    internal UiMode Mode { get; private set; } = UiMode.Menu;
    internal string StateName { get; private set; } = "none";

    // The current screen state object (for patches that act on it).
    internal static IntPtr StatePointer { get; private set; }

    // Worked out once per state change.
    internal bool InHub { get; private set; }
    internal bool InFurnitureStorage { get; private set; }
    internal bool InPlacement { get; private set; }
    internal bool InSettings { get; private set; }
    internal bool OnLadder { get; private set; }

    internal PwsScreenViewport? Viewport => PlayerRig.Alive(viewport) ? viewport : null;
    internal PwsPlayerInput? Input => PlayerRig.Alive(input) ? input : null;
    internal PlayerCharacter? Character => PlayerRig.Alive(character) ? character : null;

    internal GameState(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal void Update(PlayerRig rig)
    {
        ResolveViewport();
        ResolvePlayer(rig);
        Classify(rig);
    }

    private void ResolveViewport()
    {
        if (PlayerRig.Alive(viewport) || Time.unscaledTime < nextViewportSearch)
            return;

        nextViewportSearch = Time.unscaledTime + 1f;
        try
        {
            var manager = PwsScreenManager.Instance;
            viewport = manager is null ? null : manager.MainViewport;
            if (PlayerRig.Alive(viewport))
                log.Msg("game: screen viewport found");
        }
        catch
        {
            viewport = null;
        }
    }

    private void ResolvePlayer(PlayerRig rig)
    {
        var controller = rig.Controller;
        if (controller is null)
        {
            input = null;
            character = null;
            inputFor = IntPtr.Zero;
            return;
        }

        if (controller.Pointer == inputFor && PlayerRig.Alive(input))
            return;

        inputFor = controller.Pointer;
        try
        {
            input = controller.PlayerInput;
            character = controller.m_character;
            attach = null;
            try
            {
                var visuals = PlayerRig.Alive(character) ? character!.FirstPersonVisuals : null;
                attach = visuals is null || visuals == null ? null : visuals.m_rigEquipmentAttachPoint;
            }
            catch
            {
                attach = null;
            }

            rig.AttachPoint = PlayerRig.Alive(attach) ? attach : null;
            Patches.InputPointer = PlayerRig.Alive(input) ? input!.Pointer : IntPtr.Zero;
            log.Msg($"game: player input {(PlayerRig.Alive(input) ? "found" : "NOT found")}, "
                + $"character {(PlayerRig.Alive(character) ? "found" : "NOT found")}");
        }
        catch (Exception exception)
        {
            input = null;
            log.Warning($"game: player input not reachable ({exception.GetType().Name})");
        }
    }

    private void Classify(PlayerRig rig)
    {
        var view = Viewport;
        var state = view is null || !currentState.Valid ? IntPtr.Zero : currentState.Read(view.Pointer);

        if (state == lastState)
        {
            // A player without a viewport state is still playing (this happens
            // for a frame or two during transitions).
            return;
        }

        lastState = state;
        StatePointer = state;
        var mode = UiMode.Menu;
        var name = "none";

        if (state != IntPtr.Zero)
        {
            var klass = IL2CPP.il2cpp_object_get_class(state);
            lastClass = klass;
            if (!classModes.TryGetValue(klass, out mode))
            {
                mode = UiMode.Menu;
                if (IsA(klass, Il2CppClassPointerStore<RadialMenuState>.NativeClassPtr))
                    mode = UiMode.Radial;

                else if (IsA(klass, Il2CppClassPointerStore<GameplayStateBase>.NativeClassPtr))
                    mode = UiMode.Gameplay;
                classModes[klass] = mode;
            }

            name = ClassName(klass);
        }
        else if (rig.IsPlayer && !currentState.Valid)
        {
            // The field could not be resolved in this game version: fall back
            // to "playing whenever there is a player".
            mode = UiMode.Gameplay;
        }

        if (mode != Mode || !string.Equals(name, StateName, StringComparison.Ordinal))
            log.Msg($"game: state {name} -> {mode}");

        Mode = mode;
        StateName = name;
        InHub = name.Contains("Hub", StringComparison.Ordinal);
        InFurnitureStorage = name.Contains("FurnitureInventory", StringComparison.Ordinal);
        InPlacement = name.Contains("FurniturePlacement", StringComparison.Ordinal);
        InSettings = name.Contains("Settings", StringComparison.Ordinal);
        OnLadder = name.Contains("Ladder", StringComparison.Ordinal);
    }

    private static bool IsA(IntPtr klass, IntPtr baseClass) =>
        baseClass != IntPtr.Zero && (klass == baseClass || IL2CPP.il2cpp_class_is_subclass_of(klass, baseClass, false));

    private static string ClassName(IntPtr klass)
    {
        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    internal ConfigurationManager? Configuration
    {
        get
        {
            try
            {
                var player = Character;
                var equipment = player?.EquipmentManager;
                return equipment is null || equipment == null ? null : equipment.ConfigurationManager;
            }
            catch
            {
                return null;
            }
        }
    }
}
