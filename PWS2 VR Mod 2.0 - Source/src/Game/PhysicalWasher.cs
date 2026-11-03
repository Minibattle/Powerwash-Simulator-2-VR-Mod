using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;

namespace PowerWashVR.Game;

// Physical Washer (VR SETTINGS, off by default): the washer stays in a hand
// only while that hand's grip is held.
//
//  - Let go at the hip and it hangs on the belt, on the side of the hand that
//    last held it; let go anywhere else (or throw it) and it falls and
//    bounces, then comes back to the belt after 1.5 s (twice as fast as a
//    nozzle). It never comes back while a hand holds it.
//  - Either hand takes it from the belt or catches it in the air, so it can
//    be thrown from hand to hand. The hand holding it is the washer hand (the
//    Left Handed setting is not used): the belt side, the backpack's shoulder
//    and every control follow it.
//  - While no hand holds it there is no spraying, and both hands are free
//    hands with finger tracking; the clipboard can only be taken then.
//
// The washer itself is the game's: only where it is drawn changes (the rig
// places it at the hand, on the belt or on a small physics body).
internal sealed class PhysicalWasher
{
    private const float ReturnAfter = 1.5f;
    private const float GrabReach = 0.16f;
    private const float CatchReach = 0.3f;
    private const float DockReach = 0.22f;

    private readonly MelonLogger.Instance log;
    private readonly BodyFrame body;

    private enum State { Held, Docked, Free }
    private State state = State.Held;
    private bool heldLeft;
    private float freeSince;
    private Edge leftGrip, rightGrip;
    private readonly Vector3[] lastHand = new Vector3[2];
    private readonly Vector3[] handVelocity = new Vector3[2];
    private readonly bool[] lastKnown = new bool[2];
    private GameObject? bodyObject;
    private Rigidbody? rigidbody;
    private bool enabledLast;
    private int logs;

    // For the rest of the mod.
    internal static bool Active { get; private set; }
    internal static bool Holding => !Active || Instance?.state == State.Held;
    internal static bool Away => Active && Instance?.state != State.Held;
    // The free hand's grip is the washer's while that hand is at it.
    internal static bool ClaimsFree { get; private set; }

    // Set by the mod: the hand (0 left, 1 right) holds a nozzle or an item.
    internal static readonly bool[] HandBusy = new bool[2];
    internal static PhysicalWasher? Instance { get; private set; }

    // Where the washer is while no hand holds it (world).
    internal Vector3 Position { get; private set; }
    internal Quaternion Rotation { get; private set; } = Quaternion.identity;

    internal PhysicalWasher(MelonLogger.Instance log, BodyFrame body)
    {
        this.log = log;
        this.body = body;
        Instance = this;
    }

    private bool wasSuspended;
    private bool docked;

    // suspended: the washer is put away (the case is carried): the grip is
    // the case's then, and the washer is not let go of. Back from that with
    // the grip open, it goes onto the belt.
    internal void Update(PlayerRig rig, GameState game, Controllers controllers, Settings settings, Haptics haptics, float deltaTime, bool suspended)
    {
        var enabled = settings.PhysicalWasher;
        if (enabled != enabledLast)
        {
            enabledLast = enabled;
            // On: the washer starts on the belt (the grip is open, so it
            // would otherwise drop the moment play starts); off: in the hand.
            state = enabled ? State.Docked : State.Held;
            heldLeft = settings.PhysicalLeft;
            DestroyBody();
            log.Msg($"physical washer: {(enabled ? "on" : "off")}");
        }

        Active = enabled && rig.IsPlayer;
        ClaimsFree = false;
        if (!Active)
            return;
        if (state == State.Docked && !docked)
        {
            docked = true;
            Dock(rig, settings);
        }

        leftGrip.Update(controllers.Left.Grip, 0.6f, 0.35f);
        rightGrip.Update(controllers.Right.Grip, 0.6f, 0.35f);
        Track(0, controllers.Left, deltaTime);
        Track(1, controllers.Right, deltaTime);

        var playing = game.Mode is UiMode.Gameplay or UiMode.Radial;
        var toWorld = rig.TrackingToWorldRotation;
        Vector3 HandWorld(in HandInput hand) => rig.TrackingToWorldPosition + toWorld * hand.GripPosition;

        switch (state)
        {
            case State.Held:
            {
                ref var holder = ref heldLeft ? ref controllers.Left : ref controllers.Right;
                var grip = heldLeft ? leftGrip : rightGrip;
                // The other hand takes it straight from this one.
                ref var other = ref heldLeft ? ref controllers.Right : ref controllers.Left;
                var otherGrip = heldLeft ? rightGrip : leftGrip;
                var handle = Handle(rig, settings);
                if (playing && other.Tracked && handle.HasValue && (HandWorld(other) - handle.Value).magnitude < GrabReach)
                {
                    ClaimsFree = true;
                    if (otherGrip.Pressed)
                    {
                        Take(!heldLeft, settings, haptics, "handed over");
                        break;
                    }
                }

                if (suspended)
                    break;
                if (wasSuspended && !grip.Down)
                {
                    state = State.Docked;
                    Dock(rig, settings);
                    break;
                }

                if (!grip.Down && playing && holder.Tracked)
                    LetGo(rig, settings, haptics);
                break;
            }

            case State.Docked:
                Dock(rig, settings);
                TryTake(rig, controllers, settings, haptics, GrabReach, playing);
                break;

            case State.Free:
                if (rigidbody is null || rigidbody == null)
                {
                    state = State.Docked;
                    Dock(rig, settings);
                    break;
                }

                FollowBody(settings);
                var headWorld = rig.TrackingToWorldPosition + toWorld * rig.HeadPosition;
                // Far away, or under the level (nothing below it any more).
                var far = (Position - headWorld).sqrMagnitude > 60f * 60f || Underground();
                if (Time.unscaledTime - freeSince > ReturnAfter || far || Position.y < -500f)
                {
                    DestroyBody();
                    state = State.Docked;
                    Dock(rig, settings);
                    break;
                }

                TryTake(rig, controllers, settings, haptics, CatchReach, playing);
                break;
        }

        settings.PhysicalLeft = heldLeft;
        wasSuspended = suspended;
    }

    // Just before drawing: the belt moves with you.
    internal void Reapply(PlayerRig rig, Settings settings)
    {
        if (!Active)
            return;
        if (state == State.Docked)
            Dock(rig, settings);
        else if (state == State.Free)
            FollowBody(settings);
    }

    private float nextGroundCheck;

    private bool Underground()
    {
        if (Time.unscaledTime < nextGroundCheck)
            return false;
        nextGroundCheck = Time.unscaledTime + 0.2f;
        // From the middle of its own ball (a ray never hits a collider it
        // starts inside), so it finds the floor it rests on.
        var below = (bodyObject is not null && bodyObject != null ? bodyObject.transform.position : Position);
        return !Physics.Raycast(below, Vector3.down, 300f, ~(1 << 2), QueryTriggerInteraction.Ignore);
    }

    private void Track(int index, in HandInput hand, float deltaTime)
    {
        if (lastKnown[index] && deltaTime > 1e-4f)
            handVelocity[index] = Vector3.Lerp(handVelocity[index], (hand.GripPosition - lastHand[index]) / deltaTime, 0.5f);
        lastHand[index] = hand.GripPosition;
        lastKnown[index] = hand.Tracked;
    }

    // Where the hand holds the washer (the rig puts the washer at grip +
    // offset): the inverse of that, for the current washer pose.
    private static Vector3? Handle(PlayerRig rig, Settings settings)
    {
        if (!rig.HasWasher)
            return null;
        return rig.WasherPosition - rig.WasherRotation * settings.WasherOffset;
    }

    private void TryTake(PlayerRig rig, Controllers controllers, Settings settings, Haptics haptics, float reach, bool playing)
    {
        if (!playing)
            return;
        var handle = Position - Rotation * settings.WasherOffset;
        var toWorld = rig.TrackingToWorldRotation;
        foreach (var left in new[] { true, false })
        {
            ref var hand = ref left ? ref controllers.Left : ref controllers.Right;
            if (!hand.Tracked || HandBusy[left ? 0 : 1])
                continue;
            var position = rig.TrackingToWorldPosition + toWorld * hand.GripPosition;
            if ((position - handle).magnitude > reach)
                continue;
            // The hand that is the free hand now: its grip is the washer's.
            if (left != settings.LeftHanded)
                ClaimsFree = true;
            if ((left ? leftGrip : rightGrip).Pressed)
            {
                Take(left, settings, haptics, state == State.Free ? "caught" : "taken from the belt");
                return;
            }
        }
    }

    private void Take(bool left, Settings settings, Haptics haptics, string how)
    {
        DestroyBody();
        state = State.Held;
        heldLeft = left;
        settings.PhysicalLeft = left;
        haptics.Pulse(!left, 0.6f, 0.05f);
        if (logs++ < 20)
            log.Msg($"physical washer: {how} ({(left ? "left" : "right")} hand)");
    }

    private void LetGo(PlayerRig rig, Settings settings, Haptics haptics)
    {
        var handle = Handle(rig, settings);
        Position = rig.WasherPosition;
        Rotation = rig.WasherRotation;
        haptics.Pulse(!heldLeft, 0.25f, 0.03f);
        // At the hip: straight onto the belt.
        var dock = DockHandle(rig, settings);
        if (handle.HasValue && (handle.Value - dock).magnitude < DockReach)
        {
            state = State.Docked;
            Dock(rig, settings);
            if (logs++ < 20)
                log.Msg("physical washer: put on the belt");
            return;
        }

        var velocity = rig.TrackingToWorldRotation * handVelocity[heldLeft ? 0 : 1];
        if (velocity.sqrMagnitude > 15f * 15f)
            velocity = velocity.normalized * 15f;
        Launch(handle ?? Position, velocity);
        state = State.Free;
        freeSince = Time.unscaledTime;
        if (logs++ < 20)
            log.Msg($"physical washer: {(velocity.magnitude > 1.5f ? "thrown" : "dropped")} ({velocity.magnitude:0.0} m/s)");
    }

    // The belt, on the side of the hand that last held it: the handle at the
    // hip, the barrel down along the leg.
    private Vector3 DockHandle(PlayerRig rig, Settings settings)
    {
        var facing = body.Rotation;
        var side = heldLeft ? -1f : 1f;
        var centre = rig.HeadPosition + new Vector3(0f, -settings.BeltDrop, 0f) + facing * new Vector3(0f, 0f, 0.04f);
        var hip = centre + facing * new Vector3(0.3f * side, 0.02f, -0.02f);
        return rig.TrackingToWorldPosition + rig.TrackingToWorldRotation * hip;
    }

    private void Dock(PlayerRig rig, Settings settings)
    {
        var rotation = rig.TrackingToWorldRotation * body.Rotation * Quaternion.Euler(80f, 0f, 0f);
        Rotation = rotation;
        Position = DockHandle(rig, settings) + rotation * settings.WasherOffset;
    }

    private void Launch(Vector3 handle, Vector3 velocity)
    {
        DestroyBody();
        // The default layer: the game's level collides with it (a layer the
        // level ignores let it fall through the map).
        bodyObject = new GameObject("PowerWashVR Washer Body");
        UnityEngine.Object.DontDestroyOnLoad(bodyObject);
        bodyObject.transform.SetPositionAndRotation(handle, Rotation);
        var collider = bodyObject.AddComponent<SphereCollider>();
        collider.radius = 0.12f;
        rigidbody = bodyObject.AddComponent<Rigidbody>();
        rigidbody.mass = 2f;
        rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rigidbody.linearVelocity = velocity;
        rigidbody.angularVelocity = Vector3.Cross(Vector3.up, velocity) * 0.4f;
        IgnorePlayer(collider);
    }

    private void FollowBody(Settings settings)
    {
        if (bodyObject is null || bodyObject == null)
            return;
        bodyObject.transform.GetPositionAndRotation(out var handle, out var rotation);
        Rotation = rotation;
        Position = handle + rotation * settings.WasherOffset;
    }

    private void DestroyBody()
    {
        if (bodyObject is not null && bodyObject != null)
            UnityEngine.Object.Destroy(bodyObject);
        bodyObject = null;
        rigidbody = null;
    }

    private static void IgnorePlayer(Collider collider)
    {
        try
        {
            var character = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.PlayerCameraController>();
            var root = Instance is not null ? PlayerRoot : null;
            var own = (root ?? character?.transform.root)?.GetComponentsInChildren<Collider>(true);
            for (var index = 0; own is not null && index < own.Length; index++)
            {
                if (own[index] is { } other && other != null)
                    Physics.IgnoreCollision(collider, other, true);
            }
        }
        catch
        {
            // A bump off the player at worst.
        }
    }

    // The local player's root (set by the mod each frame).
    internal static Transform? PlayerRoot;
}
