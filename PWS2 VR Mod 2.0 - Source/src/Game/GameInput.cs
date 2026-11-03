using Il2CppFuturLab.PW2;
using Il2CppFuturLab.PW2.UI;
using Il2CppFuturLab.PW2.UI.RadialMenu;
using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;

namespace PowerWashVR.Game;

// The controllers, mapped straight onto the game's own player input object.
//
// Nothing goes through the game's input bindings: movement is written into
// BaseInput.MovementRaw, buttons call the same Invoke* methods the game's
// input callbacks call, and spraying is answered in the BaseInput.Fire getter.
// A player who rebound their gamepad or keyboard is unaffected, and so is the
// mod.
//
// Right-handed layout (LeftHanded swaps the hands):
//
//   washer trigger          spray
//   washer grip             continuous spray on/off
//   washer stick            turn; click = crouch
//   A                       jump
//   B (hold)                extension wheel; the FREE stick picks, release takes
//   X (hold)                nozzle wheel; the WASHER stick picks, release takes
//   free stick              move; click = sprint
//   Y                       pause menu
//   free menu button        task list
//   free trigger            at the face: dirt finder; at the washer: rotate nozzle
//   free grip               interact / pick up - unless the hand is at the belt
//                           or the washer case, which take the grip first
//
// In menus the controllers point at the panel instead (see MenuPointer); B
// goes back, Y closes the pause menu, the free stick flicks tabs.
internal sealed class GameInput
{
    private readonly MelonLogger.Instance log;

    // Edges for this frame, read by the other systems in LateUpdate.
    internal Edge WasherTrigger, WasherGrip, WasherStickClick, WasherPrimary, WasherSecondary;
    // The triggers by controller, for the menu laser: switching Left Handed
    // with a trigger held must not look like a new press on the other role.
    internal Edge LeftTrigger, RightTrigger;
    internal Edge FreeTrigger, FreeGrip, FreeStickClick, FreePrimary, FreeSecondary, FreeMenu;
    internal Edge RightBumper, LeftBumper, FrameY, DpadUp, DpadDown, DpadLeft, DpadRight;
    private bool frame;

    internal float TurnDelta { get; private set; }
    internal bool ContinuousSpray { get; private set; }
    internal bool WasherAway;       // Physical Washer: no hand holds it
    internal static int Scans;      // dirt scans asked for (tutorial)
    internal bool PhysicalWasherOn; // Physical Washer: the grip holds the washer
    internal bool WasherHandBusy;   // set by the case while the washer hand is at a card
    internal bool FreeHandBusy;     // set by the belt while the hand holds a nozzle
    internal bool FreeGripClaimed;  // set by the belt and the case: the grip is theirs
    internal bool WasherGripClaimed;  // set by the clipboard: the washer grip is its

    private bool snapArmed = true;
    private bool sprinting;
    private float stillTime;
    private Vector2 wroteMovement;
    private bool jumpSent;
    private bool interactSent;
    private Vector2 wheelDirection;

    // ---- Equipment wheels -------------------------------------------------
    //
    // At most one of the game's wheels is open at a time. What should be open
    // follows from what you are holding, strongest first: a prop taken off the
    // washer case, the case itself, then the B / X buttons. A wheel stays open
    // while its holder is held and takes the highlighted item when it is let
    // go; a wheel pushed aside by a stronger holder is closed without taking
    // anything. A new wheel opens only after the game has left the previous
    // one for a few frames (opening in the same frame does nothing).

    internal enum Prop
    {
        None,
        Extension,
        Soap,
    }

    internal enum Side
    {
        None,
        Washer,
        Free,
    }

    private enum WheelHold
    {
        None,
        ButtonB,   // extensions, aimed with the free stick, at the washer hand
        ButtonX,   // nozzles, aimed with the washer stick, at the free hand
        Case,      // washers, aimed with the free (holding) hand's stick
        Prop,      // extension or nozzle, aimed with the washer (holding) hand's stick, at that hand
    }

    // Set by the washer case each frame.
    internal bool CaseHeld;
    internal Prop PropHeld;

    private WheelHold buttonWheel;
    private WheelHold openHold;
    private EquipmentType openType;
    private float openedAt;
    private bool seenRadial;
    private WheelHold blocked;
    private int settleFrames;

    internal Side WheelAtHand => AtOf(openHold);
    internal bool WheelOpen => openHold != WheelHold.None;

    private WheelHold Desired() => PropHeld != Prop.None ? WheelHold.Prop : CaseHeld ? WheelHold.Case : buttonWheel;

    private EquipmentType TypeOf(WheelHold hold) => hold switch
    {
        WheelHold.ButtonB => EquipmentType.Extension,
        WheelHold.ButtonX => EquipmentType.Nozzle,
        WheelHold.Case => EquipmentType.PowerWasher,
        WheelHold.Prop => PropHeld == Prop.Extension ? EquipmentType.Extension : EquipmentType.Nozzle,
        _ => EquipmentType.None,
    };

    private bool buttonsLeft;
    private Vector2 moveStick, turnStick;

    // Which stick aims a wheel. The button wheels aim with the stick of the
    // other controller than their button (B: left stick, X: right stick, or
    // mirrored with Swap Hand Buttons); a held object aims with its hand.
    private bool AimFromLeft(WheelHold hold, bool leftHanded) => hold switch
    {
        // Steam Frame: X and Y are both on the right, so both wheels aim with
        // the left stick.
        WheelHold.ButtonB => frame || !buttonsLeft,
        WheelHold.ButtonX => frame || buttonsLeft,
        _ => AimOf(hold) == Side.Free ? !leftHanded : leftHanded,
    };

    private static Side AimOf(WheelHold hold) => hold switch
    {
        WheelHold.ButtonB => Side.Free,
        WheelHold.ButtonX => Side.Washer,
        WheelHold.Case => Side.Free,
        WheelHold.Prop => Side.Washer,
        _ => Side.None,
    };

    // Only the wheels of the case's props float at the hand; the button
    // wheels and the washer wheel sit on the panel in front of you.
    private static Side AtOf(WheelHold hold) => hold == WheelHold.Prop ? Side.Washer : Side.None;

    private bool Held(WheelHold hold) => hold switch
    {
        WheelHold.ButtonB => frame ? FrameY.Down : WasherSecondary.Down,
        WheelHold.ButtonX => FreePrimary.Down,
        WheelHold.Case => CaseHeld,
        WheelHold.Prop => PropHeld != Prop.None && TypeOf(WheelHold.Prop) == openType,
        _ => false,
    };

    private void Wheels(PwsPlayerInput input, UiMode mode, GameState state)
    {
        var desired = Desired();
        if (blocked != WheelHold.None && desired != blocked)
            blocked = WheelHold.None;

        if (openHold != WheelHold.None)
        {
            var radial = mode == UiMode.Radial;
            seenRadial |= radial;
            var closedByGame = seenRadial ? !radial : Time.unscaledTime - openedAt > 0.8f;

            if (closedByGame)
            {
                if (desired == openHold)
                    blocked = openHold;
                openHold = WheelHold.None;
                settleFrames = 0;
            }
            else if (!Held(openHold))
            {
                Close(input, true, state);
            }
            else if (desired != openHold)
            {
                Close(input, false, state);
            }
        }

        if (openHold == WheelHold.None && desired != WheelHold.None && desired != blocked)
        {
            settleFrames = mode == UiMode.Gameplay ? settleFrames + 1 : 0;
            if (settleFrames >= 3)
                Open(input, desired);
        }
    }

    private void Open(PwsPlayerInput input, WheelHold hold)
    {
        var type = TypeOf(hold);
        try
        {
            input.HandleEquipmentHeld(type);
            openHold = hold;
            openType = type;
            openedAt = Time.unscaledTime;
            seenRadial = false;
            wheelDirection = Vector2.zero;
        }
        catch (Exception exception)
        {
            blocked = hold;
            log.Warning($"input: {type} wheel did not open ({exception.GetType().Name})");
        }
    }

    // take: the game takes the highlighted item (holder let go); otherwise the
    // wheel is dismissed as it is.
    private void Close(PwsPlayerInput input, bool take, GameState state)
    {
        try
        {
            if (!take)
            {
                // Closing a wheel can still apply what it highlights; point it
                // back at what is already equipped first, so nothing changes.
                KeepEquipped(state);
            }

            input.HandleEquipmentReleased(openType);
        }
        catch (Exception exception)
        {
            log.Warning($"input: {openType} wheel did not close ({exception.GetType().Name})");
        }

        openHold = WheelHold.None;
        settleFrames = 0;
    }

    private int tabArmed = 0;
    private RadialMenuUI? wheelUi;
    private float nextWheelSearch;
    private int edgesLogged;

    internal GameInput(MelonLogger.Instance log)
    {
        this.log = log;
    }


    internal void Update(Controllers controllers, GameState state, PlayerRig rig, Settings settings,
        Haptics haptics, float deltaTime)
    {
        var leftHanded = settings.LeftHanded;
        ref var washer = ref leftHanded ? ref controllers.Left : ref controllers.Right;
        ref var free = ref leftHanded ? ref controllers.Right : ref controllers.Left;

        // Triggers and grips go with the hands (washer / free). The sticks
        // stay where they are whichever hand holds the washer: left moves
        // (click = sprint), right turns (click = crouch). The face buttons
        // stay on their controllers too unless Swap Hand Buttons is on:
        // "main" is the controller with jump and the extension wheel.
        buttonsLeft = settings.SwapHandButtons;
        ref var main = ref buttonsLeft ? ref controllers.Left : ref controllers.Right;
        ref var other = ref buttonsLeft ? ref controllers.Right : ref controllers.Left;
        moveStick = controllers.Left.Stick;
        turnStick = controllers.Right.Stick;

        LeftTrigger.Update(controllers.Left.Trigger);
        RightTrigger.Update(controllers.Right.Trigger);
        WasherTrigger.Update(washer.Trigger);
        WasherGrip.Update(washer.Grip, 0.7f, 0.45f);
        WasherStickClick.Update(controllers.Right.StickClick);
        // Steam Frame: A / B / X / Y are all on the right controller (X / Y
        // do what the Touch's X / Y do), both menu buttons pause, and each
        // controller has a bumper.
        frame = PowerWashVR.OpenXR.Bindings.IsFrame;
        WasherPrimary.Update(frame ? controllers.Right.Primary : main.Primary);
        WasherSecondary.Update(frame ? controllers.Right.Secondary : main.Secondary);
        RightBumper.Update(controllers.Right.Bumper);
        LeftBumper.Update(controllers.Left.Bumper);
        FrameY.Update(frame && controllers.Right.Y);
        DpadUp.Update(frame && controllers.Left.DpadUp);
        DpadDown.Update(frame && controllers.Left.DpadDown);
        DpadLeft.Update(frame && controllers.Left.DpadLeft);
        DpadRight.Update(frame && controllers.Left.DpadRight);
        FreeTrigger.Update(free.Trigger);
        FreeGrip.Update(free.Grip, 0.7f, 0.45f);
        FreeStickClick.Update(controllers.Left.StickClick);
        FreePrimary.Update(frame ? controllers.Right.X : other.Primary);
        // Steam Frame: Y is the extension wheel; pausing is on menu / view.
        FreeSecondary.Update(!frame && other.Secondary);
        // A menu button on either controller, whatever the button layout.
        FreeMenu.Update(controllers.Left.Menu || controllers.Right.Menu);
        LogEdges();

        TurnDelta = 0f;
        var input = state.Input;
        var mode = state.Mode;

        if (input is null)
        {
            Patches.FireWanted = false;
            openHold = buttonWheel = WheelHold.None;
            if (mode == UiMode.Menu)
                MenuButtons(state, controllers.Left.Stick);
            return;
        }

        // The B / X wheels are asked for in play, and only while held.
        // Control Preset: Default has both button wheels; Less Buttons none;
        // R Stick Scan keeps X's wheel and gives B to crouching.
        var preset = settings.ControlPreset;
        if (mode == UiMode.Gameplay && !CaseHeld && buttonWheel == WheelHold.None)
        {
            // Steam Frame: Y's extension wheel in Default and R Stick Scan.
            if (frame ? FrameY.Pressed && preset != 1 : WasherSecondary.Pressed && preset == 0)
                buttonWheel = WheelHold.ButtonB;
            else if (FreePrimary.Pressed && preset != 1)
                buttonWheel = WheelHold.ButtonX;
        }

        if ((buttonWheel == WheelHold.ButtonB && !(frame ? FrameY.Down : WasherSecondary.Down)) || (buttonWheel == WheelHold.ButtonX && !FreePrimary.Down))
            buttonWheel = WheelHold.None;

        if (mode is UiMode.Gameplay or UiMode.Radial)
            Wheels(input, mode, state);
        else if (openHold != WheelHold.None)
            openHold = WheelHold.None;

        // Aim before the game's own wheel update runs (see Patches).
        Patches.WheelForced = mode == UiMode.Radial;
        if (Patches.WheelForced)
        {
            var aimFromLeft = AimFromLeft(openHold, leftHanded);
            if (openHold == WheelHold.None)
                aimFromLeft = true;
            var stick = aimFromLeft ? controllers.Left.Stick : controllers.Right.Stick;
            if (stick.sqrMagnitude > 0.16f)
                wheelDirection = stick.normalized;
            Patches.WheelDirection = wheelDirection;
        }

        switch (mode)
        {
            case UiMode.Gameplay:
                Gameplay(state, input, rig, settings, haptics, washer, free, deltaTime);
                break;
            case UiMode.Radial:
                Radial(input, settings, washer, deltaTime);
                break;
            default:
                StopGameplay(input);
                MenuButtons(state, controllers.Left.Stick);
                break;
        }
    }

    private void Gameplay(GameState state, PwsPlayerInput input, PlayerRig rig, Settings settings, Haptics haptics,
        in HandInput washer, in HandInput free, float deltaTime)
    {
        // Movement, relative to where the head faces (the game moves along
        // HeadTurn, which carries the head's yaw).
        // While the clipboard is out, that stick scrolls the list instead.
        var stick = Clipboard.Held ? Vector2.zero : MathUtil.Deadzone(moveStick, 0.15f);
        // Written every frame: the game keeps the last value it was given.
        input.MovementRaw = stick;
        wroteMovement = stick;

        if (FreeStickClick.Pressed && !Clipboard.Held)
        {
            sprinting = !sprinting;
            // Sprint to Stand: sprinting gets you up from the crouch.
            if (sprinting && settings.SprintToStand && rig.Crouched)
            {
                rig.Stand();
                rig.BodyToHead();
            }
        }
        stillTime = stick.sqrMagnitude < 0.04f ? stillTime + deltaTime : 0f;
        if (stillTime > 0.4f)
            sprinting = false;
        if (input.Sprint != sprinting)
            input.Sprint = sprinting;

        // Holding an item that turns (the game's D-pad rotation): the turn
        // stick (and the Steam Frame's D-pad) turns the item instead.
        // The adaptable nozzle's width: right stick up / down (Steam Frame:
        // D-pad right / left), not while something is held in the world.
        if (buttonWheel == WheelHold.None && !HoldsWorldItemCached(state) && AdaptableEquipped(state))
            AdjustWidth(state, frame, turnStick.y, deltaTime);

        // Steam Frame: its D-pad turns the item and the stick keeps turning you.
        var rotating = RotatableHeld(state);
        if (rotating)
            RotateItem(input, frame ? 0f : turnStick.x, deltaTime);

        // Turning (not while a wheel is wanted: its stick aims the wheel).
        if (buttonWheel == WheelHold.None && !CaseHeld && !(rotating && !frame))
            Turn(turnStick, settings, deltaTime);

        // The mod's own crouch: standing -> half height -> just above the
        // floor -> standing. Only the view moves; the game's stance is left alone.
        // Steam Frame: B crouches; the right stick click crouches too, or
        // scans for dirt with R Stick Scan.
        var rStickScan = settings.ControlPreset == 2;
        var crouch = frame ? WasherSecondary.Pressed || (!rStickScan && WasherStickClick.Pressed)
            : rStickScan ? WasherSecondary.Pressed : WasherStickClick.Pressed;
        if (crouch)
        {
            rig.CycleCrouch();
            rig.BodyToHead();
        }

        // Steam Frame bumpers: the free hand's scans for dirt, the washer
        // hand's toggles the spray (left / right, swapped when left handed).
        var scanBumper = settings.LeftHanded ? RightBumper : LeftBumper;
        if (frame && scanBumper.Pressed)
        {
            input.InvokeHighlightDirt(); Scans++;
            haptics.Pulse(settings.LeftHanded, 0.3f, 0.05f);
        }

        if (rStickScan && WasherStickClick.Pressed && !crouch)
        {
            input.InvokeHighlightDirt(); Scans++;
            haptics.Pulse(!settings.LeftHanded, 0.3f, 0.05f);
        }

        if (WasherPrimary.Pressed)
        {
            input.InvokeJump(true);
            jumpSent = true;
        }
        else if (WasherPrimary.Released && jumpSent)
        {
            input.InvokeJump(false);
            jumpSent = false;
        }

        // Interact on the free grip, unless the belt or the case has it.
        if (FreeGrip.Pressed && !FreeGripClaimed && !FreeHandBusy)
        {
            input.InvokeItemInteraction(ItemInteraction.PickUp);
            interactSent = true;
        }
        else if (FreeGrip.Released && interactSent)
        {
            input.InvokeCancelInteraction(ItemInteraction.Use);
            interactSent = false;
        }

        // Pausing lets go of anything held in the world first (a ladder, a
        // carried item): the item is let go this frame, the menu opens the next.
        if (menuAfterDrop)
        {
            menuAfterDrop = false;
            input.ToggleGameMenu();
        }
        else if (FreeSecondary.Pressed || FreeMenu.Pressed)
        {
            // No Clipboard Mode: an unplaced item is stored, the mode ends.
            if (HubPlacement.PauseStores(input, HoldsWorldItem(state)))
            {
                interactSent = false;
                menuAfterDrop = true;
                log.Msg("input: furniture stored before pausing (No Clipboard Mode ended)");
            }
            else if (HoldsWorldItem(state))
            {
                input.InvokeItemInteraction(ItemInteraction.PickUp);
                input.InvokeCancelInteraction(ItemInteraction.Use);
                interactSent = false;
                menuAfterDrop = true;
                log.Msg("input: let go of the held item before pausing");
            }
            else
            {
                input.ToggleGameMenu();
            }
        }

        // The free trigger: held up to the face it scans for dirt, at the
        // washer it turns the nozzle.
        if (FreeTrigger.Pressed && !FreeHandBusy && !Clipboard.Held)
        {
            if (FreeHandAtFace(rig, free))
            {
                input.InvokeHighlightDirt(); Scans++;
                haptics.Pulse(settings.LeftHanded, 0.3f, 0.05f);
            }
            else if (FreeHandNearWasher(rig, free, settings))
            {
                input.InvokeRotateNozzle();
                haptics.Pulse(!settings.LeftHanded, 0.4f, 0.05f);
                haptics.Pulse(settings.LeftHanded, 0.4f, 0.05f);
            }
        }

        // Spraying.
        // Steam Frame: only the washer hand's bumper toggles it (its grip does nothing).
        var sprayBumper = settings.LeftHanded ? LeftBumper : RightBumper;
        // With Physical Washer the grip holds the washer, so it does not
        // toggle spraying (the Frame's bumper still does).
        if (((!frame && !PhysicalWasherOn && WasherGrip.Pressed && !WasherGripClaimed) || (frame && sprayBumper.Pressed)) && !WasherHandBusy && !CaseHeld)
        {
            ContinuousSpray = !ContinuousSpray;
            haptics.Pulse(!settings.LeftHanded, ContinuousSpray ? 0.6f : 0.3f, 0.04f);
        }

        // The trigger stops a toggled spray; held, it keeps spraying as a
        // normal trigger spray until it is let go.
        if (ContinuousSpray && WasherTrigger.Pressed)
        {
            ContinuousSpray = false;
            haptics.Pulse(!settings.LeftHanded, 0.3f, 0.04f);
        }

        if (Clipboard.Held || WasherAway)
            ContinuousSpray = false;
        var fire = (WasherTrigger.Down || ContinuousSpray) && !WasherHandBusy && !CaseHeld && !Clipboard.Held && !WasherAway && washer.Tracked;
        Patches.FireWanted = fire;
        try
        {
            if (input._Fire_k__BackingField != fire)
                input._Fire_k__BackingField = fire;
        }
        catch
        {
            // The getter patch is the main route; the field is a backstop.
        }
    }

    private void Radial(PwsPlayerInput input, Settings settings, in HandInput washer, float deltaTime)
    {
        Patches.FireWanted = false;
        ZeroMovement(input);

        // The triggers step through the tiers (A / D on a keyboard).
        var leftTrigger = settings.LeftHanded ? WasherTrigger : FreeTrigger;
        var rightTrigger = settings.LeftHanded ? FreeTrigger : WasherTrigger;
        try
        {
            // The game's own previous / next sub-item handler (what A / D call).
            if (leftTrigger.Pressed)
                input.RadialNavigateSubMenu(-1);
            if (rightTrigger.Pressed)
                input.RadialNavigateSubMenu(1);
        }
        catch
        {
            // Wheels without tiers ignore it.
        }

    }

    // The wheel is aimed in LateUpdate, after the game's own wheel update has
    // run for the frame, and the game is told the same thing through its own
    // stick value with the wheel in controller mode - so both writers agree,
    // the highlight cannot flicker and the pointer stays on the wheel's centre
    // instead of drifting with the mouse. The other hand's stick aims; the
    // last direction is held while the stick rests.
    internal void LateUpdate(Controllers controllers, GameState state, PlayerRig rig, Settings settings)
    {
        WheelTargetValid = false;
        if (openHold == WheelHold.None || state.Mode != UiMode.Radial)
            return;

        var washerIsLeft = settings.LeftHanded;
        var aimFromLeft = AimFromLeft(openHold, washerIsLeft);
        var stick = aimFromLeft ? controllers.Left.Stick : controllers.Right.Stick;
        PlaceWheel(controllers, rig, settings);
        if (stick.sqrMagnitude > 0.16f)
            wheelDirection = stick.normalized;
        Patches.WheelDirection = wheelDirection;

        try
        {
            var radial = state.Viewport?.CurrentState?.TryCast<RadialMenuState>();
            if (radial is not null && radial.m_inputMode != RadialMenuInputMode.ConsoleController)
                radial.m_inputMode = RadialMenuInputMode.ConsoleController;

            var input = state.Input;
            if (input is not null)
            {
                input.RadialWheelNavigateRaw = wheelDirection;
                input.RadialWheelNavigateAltRaw = Vector2.zero;
            }
        }
        catch
        {
            // The wheel still works through UpdateInput below.
        }

        var ui = WheelUi();
        if (ui is null)
            return;

        try
        {
            // No pointer arrow on the wheel: the highlighted segment says it.
            var pip = ui.m_radialPipVisual;
            if (pip is not null && pip != null && pip.localScale != Vector3.zero)
                pip.localScale = Vector3.zero;

            ui.UpdateInput(wheelDirection, RadialMenuInputMode.ConsoleController);
        }
        catch
        {
            // One bad frame on the wheel is not worth stopping for.
        }
    }

    // Where a wheel that belongs to a hand floats: just in front of that hand,
    // toward the eyes, turned to face them.
    internal bool WheelTargetValid { get; private set; }
    internal Vector3 WheelTargetPosition { get; private set; }
    internal Quaternion WheelTargetRotation { get; private set; } = Quaternion.identity;
    internal Transform? WheelRoot => PlayerRig.Alive(wheelUi) ? wheelUi!.transform : null;

    // The wheel's centre: its entries root (the segments are laid out around it).
    internal Transform? WheelTransform
    {
        get
        {
            if (!PlayerRig.Alive(wheelUi))
                return null;
            try
            {
                var entries = wheelUi!.m_entriesRoot;
                return entries is not null && entries != null ? entries : wheelUi.transform;
            }
            catch
            {
                return wheelUi!.transform;
            }
        }
    }

    private void PlaceWheel(Controllers controllers, PlayerRig rig, Settings settings)
    {
        var at = AtOf(openHold);
        if (at == Side.None || !rig.HasPose)
            return;

        var left = at == Side.Free ? !settings.LeftHanded : settings.LeftHanded;
        ref var hand = ref left ? ref controllers.Left : ref controllers.Right;
        if (!hand.Tracked)
            return;

        var toWorld = rig.TrackingToWorldRotation;
        var handPosition = rig.TrackingToWorldPosition + toWorld * hand.GripPosition;
        var head = rig.TrackingToWorldPosition + toWorld * rig.HeadPosition;
        var toHead = head - handPosition;
        var distance = toHead.magnitude;
        if (distance < 0.05f)
            return;

        var up = toWorld * Vector3.up;
        var target = handPosition + toHead / distance * Mathf.Min(0.15f, distance * 0.4f) + up * 0.06f;
        WheelTargetPosition = target;
        WheelTargetRotation = Quaternion.LookRotation(target - head, up);
        WheelTargetValid = WheelUi() is not null;
    }

    private void KeepEquipped(GameState state)
    {
        try
        {
            var current = state.Configuration?.CurrentConfiguration;
            var ui = WheelUi();
            if (current is null || ui is null)
                return;

            BaseEquipmentData? equipped = openType switch
            {
                EquipmentType.PowerWasher => current.PressureGun,
                EquipmentType.Extension => current.Extension,
                EquipmentType.Nozzle => current.Nozzle,
                _ => null,
            };

            if (equipped is not null)
                ui._CurrentSelection_k__BackingField = equipped;
        }
        catch
        {
            // Falls back to whatever the wheel had.
        }
    }

    private RadialMenuUI? WheelUi()
    {
        if (PlayerRig.Alive(wheelUi))
            return wheelUi;

        if (Time.unscaledTime < nextWheelSearch)
            return null;

        nextWheelSearch = Time.unscaledTime + 0.25f;
        wheelUi = UnityEngine.Object.FindObjectOfType<RadialMenuUI>();
        return PlayerRig.Alive(wheelUi) ? wheelUi : null;
    }

    private void StopGameplay(PwsPlayerInput input)
    {
        Patches.FireWanted = false;
        ZeroMovement(input);
        if (sprinting)
        {
            sprinting = false;
            input.Sprint = false;
        }

        if (jumpSent)
        {
            input.InvokeJump(false);
            jumpSent = false;
        }

        if (interactSent)
        {
            input.InvokeCancelInteraction(ItemInteraction.Use);
            interactSent = false;
        }

        openHold = buttonWheel = WheelHold.None;
    }

    private void ZeroMovement(PwsPlayerInput input)
    {
        if (wroteMovement.x == 0f && wroteMovement.y == 0f)
            return;

        input.MovementRaw = Vector2.zero;
        wroteMovement = Vector2.zero;
    }

    // Menu buttons that are not pointing: back, close, tabs.
    private void MenuButtons(GameState state, Vector2 freeStick)
    {
        var ui = state.Viewport?.PlayerUIInput;
        if (ui is null)
            return;

        if (WasherSecondary.Pressed)
        {
            if (!Fire(ui.BackAction))
                Fire(ui.CancelAction);
        }

        if (FreeSecondary.Pressed || FreeMenu.Pressed)
        {
            if (!Fire(ui.GameMenuToggleAction) && !Fire(ui.BackAction))
                Fire(ui.CancelAction);
        }

        // Tabs on a flick of the free stick.
        var x = freeStick.x;
        if (tabArmed == 0 && Mathf.Abs(x) > 0.7f)
        {
            tabArmed = x > 0 ? 1 : -1;
            Fire(x > 0 ? ui.NextTabAction : ui.PreviousTabAction);
        }
        else if (tabArmed != 0 && Mathf.Abs(x) < 0.3f)
        {
            tabArmed = 0;
        }
    }

    // Raises a UI action's Pressed event the way the game does when its bound
    // button goes down. False if nothing listens.
    private static bool Fire(UIAction? action)
    {
        if (action is null)
            return false;

        try
        {
            if (!action.m_enabled)
                return false;

            var pressed = action.Pressed;
            if (pressed is null)
                return false;

            pressed.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool menuAfterDrop;

    // ---- The adaptable nozzle ----
    private float nextAdaptableCheck, widthRepeat;
    private int widthHeld;
    private bool widthFirst;
    private bool adaptable, heldCached;
    private float nextHeldCached;

    private bool HoldsWorldItemCached(GameState state)
    {
        if (Time.unscaledTime >= nextHeldCached)
        {
            nextHeldCached = Time.unscaledTime + 0.2f;
            heldCached = HoldsWorldItem(state);
        }

        return heldCached;
    }

    private bool AdaptableEquipped(GameState state)
    {
        if (Time.unscaledTime < nextAdaptableCheck)
            return adaptable;
        nextAdaptableCheck = Time.unscaledTime + 0.5f;
        try
        {
            var nozzle = state.Configuration?.CurrentConfiguration?.Nozzle;
            adaptable = nozzle is not null && nozzle != null && nozzle.name.Contains("Adaptable", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            adaptable = false;
        }

        return adaptable;
    }

    private void AdjustWidth(GameState state, bool frameControls, float stickY, float deltaTime)
    {
        var step = 0;
        // Held, it keeps stepping (first step at once, then every 0.15 s
        // after a short pause).
        var direction = 0;
        if (frameControls)
            direction = DpadRight.Down ? 1 : DpadLeft.Down ? -1 : 0;
        else if (Mathf.Abs(stickY) > 0.6f)
            direction = stickY > 0f ? 1 : -1;

        if (direction == 0)
        {
            widthRepeat = 0f;
            widthHeld = 0;
        }
        else
        {
            if (direction != widthHeld)
            {
                widthHeld = direction;
                widthRepeat = 0f;
                widthFirst = true;
            }

            widthRepeat -= deltaTime;
            if (widthRepeat <= 0f)
            {
                step = direction;
                widthRepeat = widthFirst ? 0.35f : 0.15f;
                widthFirst = false;
            }
        }

        if (step == 0)
            return;
        try
        {
            var equipment = state.Character?.EquipmentManager;
            if (equipment is not null && equipment != null)
                equipment.SetDynamicScale(step);
        }
        catch
        {
            // Not changed.
        }
    }

    private static bool HoldsWorldItem(GameState state)
    {
        try
        {
            var manager = state.Character is { } character ? character.InteractionManager : null;
            var item = manager is null || manager == null ? null : manager.GetHeldItem();
            return item is not null && item != null;
        }
        catch
        {
            return false;
        }
    }

    // ---- Rotating a held item ----
    private float nextHeldCheck, rotateRepeat;
    private IntPtr heldChecked;
    private bool heldRotatable, rotateLogged;

    // Looked up a few times a second, not every frame.
    private bool RotatableHeld(GameState state)
    {
        if (Time.unscaledTime < nextHeldCheck)
            return heldRotatable;
        nextHeldCheck = Time.unscaledTime + 0.2f;
        try
        {
            var character = state.Character;
            var manager = character is null ? null : character.InteractionManager;
            var item = manager is null || manager == null ? null : manager.GetHeldItem();
            if (item is null || item == null)
            {
                heldChecked = IntPtr.Zero;
                heldRotatable = false;
                return false;
            }

            if (item.Pointer != heldChecked)
            {
                heldChecked = item.Pointer;
                var term = item.GetRotateTerm(character!);
                heldRotatable = term is not null && term != null && !term.IsEmpty;
                if (!rotateLogged || heldRotatable)
                {
                    rotateLogged = true;
                    log.Msg($"input: holding \"{item.name}\" ({(heldRotatable ? "turns with the turn stick" : "does not turn")})");
                }
            }
        }
        catch
        {
            heldRotatable = false;
        }

        return heldRotatable;
    }

    // One of the game's rotation steps per flick, repeating while held.
    private void RotateItem(Il2CppFuturLab.PW2.PwsPlayerInput input, float stickX, float deltaTime)
    {
        var direction = 0;
        if (DpadLeft.Pressed)
            direction = -1;
        else if (DpadRight.Pressed)
            direction = 1;

        if (Mathf.Abs(stickX) > 0.6f)
        {
            rotateRepeat -= deltaTime;
            if (rotateRepeat <= 0f)
            {
                direction = stickX > 0f ? 1 : -1;
                rotateRepeat = rotateRepeat < -1f ? 0.35f : 0.25f;
            }
        }
        else
        {
            rotateRepeat = 0f;
        }

        if (direction == 0)
            return;
        try
        {
            var step = input.m_amountOfRotationThroughWheel;
            input.InvokeItemRotated(direction * (step != 0 ? step : 45));
        }
        catch
        {
            // Not turned.
        }
    }

    private void Turn(Vector2 stick, Settings settings, float deltaTime)
    {
        // Turn Mode Off: no stick turning (turn by turning around).
        if (settings.TurnOff)
            return;
        var x = stick.x;
        var absX = Mathf.Abs(x);

        if (settings.SnapTurn)
        {
            if (snapArmed && absX > 0.7f)
            {
                snapArmed = false;
                TurnDelta = Mathf.Sign(x) * settings.SnapAngle;
            }
            else if (!snapArmed && absX < 0.3f)
            {
                snapArmed = true;
            }

            return;
        }

        if (absX > 0.2f)
        {
            var scaled = (absX - 0.2f) / 0.8f;
            TurnDelta = Mathf.Sign(x) * scaled * scaled * settings.SmoothTurnSpeed * deltaTime;
        }
    }

    private static bool FreeHandAtFace(PlayerRig rig, in HandInput free) =>
        free.Tracked && rig.HasPose && (free.GripPosition - rig.HeadPosition).sqrMagnitude < 0.22f * 0.22f;

    private static bool FreeHandNearWasher(PlayerRig rig, in HandInput free, Settings settings)
    {
        if (!rig.HasWasher || !rig.HasPose || !free.Tracked)
            return false;

        var hand = rig.TrackingToWorldPosition + rig.TrackingToWorldRotation * free.GripPosition;
        var near = settings.NozzleRotateReach;

        // Anywhere along the washer: its body, or the nozzle end.
        if ((hand - rig.WasherPosition).sqrMagnitude < near * near)
            return true;

        return WasherFixes.JetCount > 0 && (hand - WasherFixes.JetOrigin[0]).sqrMagnitude < near * near;
    }

    internal void Reset()
    {
        Patches.FireWanted = false;
        ContinuousSpray = false;
        openHold = buttonWheel = WheelHold.None;
        sprinting = false;
        wroteMovement = new Vector2(float.NaN, 0f);
        jumpSent = interactSent = false;
    }

    // The first presses after VR starts are logged: if a button does nothing in
    // game, the log says whether the controller reported it.
    private void LogEdges()
    {
        if (edgesLogged >= 24)
            return;

        Note(WasherTrigger, "washer trigger");
        Note(WasherGrip, "washer grip");
        Note(WasherStickClick, "washer stick click");
        Note(WasherPrimary, "A");
        Note(WasherSecondary, "B");
        Note(FreeTrigger, "free trigger");
        Note(FreeGrip, "free grip");
        Note(FreeStickClick, "free stick click");
        Note(FreePrimary, "X");
        Note(FreeSecondary, "Y");
        Note(FreeMenu, "menu");
    }

    private void Note(in Edge edge, string name)
    {
        if (!edge.Pressed || edgesLogged >= 24)
            return;

        edgesLogged++;
        if (Diag.Verbose)
            log.Msg($"input: {name} pressed   fire reads {Patches.FireReads}");
    }
}
