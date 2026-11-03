using Il2CppFuturLab.PW2;
using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;

namespace PowerWashVR.Game;

// Head and washer tracking.
//
// The flat game's first-person rig is
//
//     ... / HeadTurn (yaw, PlayerCameraController.m_horizontalLook)
//             / PlayerCamera (pitch, the rendering camera)
//                 / EquipmentAnchor / PowerWasher_Assembly   (the washer)
//                 / Rig_PlayerArmsPivot                      (both arms, one mesh)
//
// The mod writes HeadTurn's yaw and the camera's local pose from the headset,
// and the washer's WORLD pose from the controller, so where you look and where
// you spray are independent.
//
// It writes twice per frame: once in LateUpdate, so gameplay code in the next
// frame sees a current pose, and once in Application.onBeforeRender, with the
// poses the XR provider has just predicted for the frame being drawn. The
// second write is what keeps the view and the washer locked to the headset's
// own frame timing; writing only from LateUpdate shows up as jitter on
// runtimes with a long prediction window.
internal sealed class PlayerRig
{
    private readonly MelonLogger.Instance log;

    private Camera? camera;
    private Transform? cameraTransform;
    private Transform? headTurn;
    private PlayerCameraController? controller;
    private float nextResolve;

    internal Camera? Camera => Alive(camera) ? camera : null;
    internal Transform? CameraTransform => Alive(cameraTransform) ? cameraTransform : null;
    internal PlayerCameraController? Controller => Alive(controller) ? controller : null;
    internal Transform? Anchor { get; private set; }
    internal Transform? Assembly { get; private set; }
    internal Transform? ArmsPivot { get; private set; }
    internal bool IsPlayer => Controller is not null && Alive(headTurn);

    // Tracking space -> world, valid after ApplyHead.
    internal Quaternion TrackingToWorldRotation { get; private set; } = Quaternion.identity;
    internal Vector3 TrackingToWorldPosition { get; private set; }
    internal bool HasPose { get; private set; }
    internal Vector3 HeadPosition => lastHead;
    internal Quaternion HeadRotation { get; private set; } = Quaternion.identity;

    internal Vector3 WasherPosition { get; private set; }
    internal Quaternion WasherRotation { get; private set; } = Quaternion.identity;
    internal bool HasWasher { get; private set; }

    private float bodyYaw;
    private Vector3 headBase;
    private bool baseCaptured;
    private bool recenterPending = true;

    // The game's own contribution, detected by comparing against what this
    // class wrote last: if a value differs, the game moved it.
    private Quaternion gameYaw = Quaternion.identity;
    private Quaternion wroteHeadTurn = new(0f, 0f, 0f, 0f);
    private Vector3 cameraRest;
    private Vector3 wroteCameraPosition = new(float.NaN, 0f, 0f);
    private Vector3 gamePosition;
    private Vector3 wroteFreePosition = new(float.NaN, 0f, 0f);
    private Quaternion wroteFreeRotation = new(0f, 0f, 0f, 0f);

    internal PlayerRig(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal static bool Alive(UnityEngine.Object? value) => value is not null && value != null;

    internal void RequestRecenter() => recenterPending = true;

    // Counts recenters (the multiplayer body takes each as the new standing height).
    internal static int Recenters;

    // The one recenter after the first load into the world (Mod): asked for
    // at this recenter count, done once the count has moved past it.
    internal static int FirstRecenterMark = -1;
    internal static bool FirstRecenterApplied => FirstRecenterMark >= 0 && Recenters > FirstRecenterMark;

    internal void Turn(float degrees)
    {
        if (degrees == 0f)
            return;

        // Turn about the head, not about the body's pivot: shift the reference
        // so the head stays where it is in the world.
        if (baseCaptured && lastHeadValid)
        {
            var offset = lastHead - headBase;
            offset = Quaternion.Euler(0f, -degrees, 0f) * offset;
            headBase = lastHead - offset;
        }

        bodyYaw += degrees;
    }

    private Vector3 lastHead;
    private bool lastHeadValid;

    internal void Reset()
    {
        camera = null;
        cameraTransform = null;
        headTurn = null;
        controller = null;
        Anchor = Assembly = ArmsPivot = null;
        HasPose = HasWasher = false;
        wroteHeadTurn = new Quaternion(0f, 0f, 0f, 0f);
        wroteCameraPosition = new Vector3(float.NaN, 0f, 0f);
        wroteFreePosition = new Vector3(float.NaN, 0f, 0f);
        wroteFreeRotation = new Quaternion(0f, 0f, 0f, 0f);
        gameYaw = Quaternion.identity;
        nextResolve = 0f;
    }

    // Cheap when nothing changed: one Camera.main call and a few null checks.
    // Searches run on timers and only while something is missing.
    internal void Resolve()
    {
        var now = Time.unscaledTime;
        var main = Camera.main;

        // Splash and boot screens have a camera that is not tagged as the main
        // one; without it the first screens never reach the headset properly.
        if (main is null || main == null)
            main = Fallback(now);

        if (main is null || main == null)
        {
            if (!Alive(camera))
                HasPose = false;
            return;
        }

        if (!Alive(camera) || main.Pointer != camera!.Pointer)
        {
            Reset();
            camera = main;
            cameraTransform = main.transform;
            cameraTransform.GetLocalPositionAndRotation(out cameraRest, out _);
            gamePosition = cameraRest;
            nextResolve = 0f;
            log.Msg($"rig: camera \"{main.name}\"");
        }

        if (!Alive(controller) && now >= nextResolve)
        {
            nextResolve = now + 1f;
            controller = FindController(main);

            if (controller is not null)
            {
                headTurn = controller.m_horizontalLook;
                if (!Alive(headTurn))
                    headTurn = cameraTransform!.parent;

                wroteHeadTurn = new Quaternion(0f, 0f, 0f, 0f);
                wroteCameraPosition = new Vector3(float.NaN, 0f, 0f);
                log.Msg($"rig: player found, look pivot \"{(Alive(headTurn) ? headTurn!.name : "none")}\"");
            }
        }

        if (Alive(controller) && (Assembly is null || !Alive(Assembly)) && now >= nextWasherSearch)
        {
            nextWasherSearch = now + 0.1f;
            ResolveWasher();
            if (Assembly is not null)
                log.Msg($"rig: washer found   arms {(ArmsPivot is null ? "not found" : "found")}");
        }
    }

    private float nextWasherSearch;
    private Camera? fallback;
    private float nextFallback;

    private Camera? Fallback(float now)
    {
        if (Alive(fallback) && fallback!.isActiveAndEnabled)
            return fallback;

        if (now < nextFallback)
            return null;

        nextFallback = now + 0.5f;
        fallback = null;
        var all = Camera.allCameras;
        if (all is null)
            return null;

        for (var index = 0; index < all.Length; index++)
        {
            var candidate = all[index];
            if (candidate is null || candidate == null || candidate.targetTexture is not null)
                continue;
            if (candidate.stereoTargetEye == StereoTargetEyeMask.None)
                continue;
            if (fallback is null || candidate.depth > fallback.depth)
                fallback = candidate;
        }

        return fallback;
    }

    private void ResolveWasher()
    {
        var cam = CameraTransform;
        if (cam is null)
            return;

        Anchor = cam.Find("EquipmentAnchor");
        Assembly = Anchor is null ? null : Anchor.Find("PowerWasher_Assembly");
        ArmsPivot = cam.Find("Rig_PlayerArmsPivot");

        // Where the game keeps the washer under its anchor, read before the
        // mod first moves it: the hands are fitted to the washer from this.
        HasAssemblyRest = false;
        if (Assembly is not null && Assembly != null)
        {
            Assembly.GetLocalPositionAndRotation(out var position, out var rotation);
            AssemblyRestPosition = position;
            AssemblyRestRotation = rotation;
            HasAssemblyRest = true;
        }
    }

    internal bool HasAssemblyRest { get; private set; }
    internal Vector3 AssemblyRestPosition { get; private set; }
    internal Quaternion AssemblyRestRotation { get; private set; } = Quaternion.identity;

    private static PlayerCameraController? FindController(Camera main)
    {
        var all = UnityEngine.Object.FindObjectsByType<PlayerCameraController>(FindObjectsSortMode.None);
        if (all is null)
            return null;

        for (var index = 0; index < all.Length; index++)
        {
            var candidate = all[index];
            if (candidate is null || candidate == null)
                continue;

            var own = candidate.m_camera;
            if (own is not null && own != null && own.Pointer == main.Pointer)
                return candidate;
        }

        return null;
    }

    internal void ApplyHead(Controllers controllers)
    {
        var cam = CameraTransform;
        if (cam is null)
        {
            HasPose = false;
            return;
        }

        if (!controllers.HeadTracked && !lastHeadValid)
            return;

        var hmdPosition = controllers.HeadPosition;
        var hmdRotation = controllers.HeadRotation;
        lastHead = hmdPosition;
        lastHeadValid = true;
        HeadRotation = hmdRotation;

        var hmdYaw = Yaw(hmdRotation);

        if (recenterPending || !baseCaptured)
        {
            recenterPending = false;

            baseCaptured = true;
            headBase = hmdPosition;
            bodyYaw = -hmdYaw;
            Recenters++;
            log.Msg("rig: view recentered");
        }

        var offset = hmdPosition - headBase;

        if (IsPlayer)
        {
            var turn = headTurn!;
            turn.GetLocalPositionAndRotation(out _, out var foundTurn);
            if (Differs(foundTurn, wroteHeadTurn))
                gameYaw = Quaternion.Euler(0f, Yaw(foundTurn), 0f);

            // The yaw pivot gets body yaw plus head yaw, so the game moves the
            // player in the direction the head faces.
            var turnRotation = gameYaw * Quaternion.Euler(0f, bodyYaw + hmdYaw, 0f);
            turn.localRotation = turnRotation;
            wroteHeadTurn = turnRotation;

            // The camera's target is expressed in world space and converted
            // into whatever its parent is, so any nodes between the yaw pivot
            // and the camera (a pitch node, a rig offset) cannot skew it.
            var pivotParent = turn.parent;
            var pivotParentRotation = Quaternion.identity;
            if (pivotParent is not null)
                pivotParent.GetPositionAndRotation(out _, out pivotParentRotation);

            var basis = pivotParentRotation * gameYaw * Quaternion.Euler(0f, bodyYaw, 0f);

            var cameraParent = cam.parent;
            var cameraParentRotation = Quaternion.identity;
            var cameraParentPosition = Vector3.zero;
            if (cameraParent is not null)
                cameraParent.GetPositionAndRotation(out cameraParentPosition, out cameraParentRotation);
            var toParent = Quaternion.Inverse(cameraParentRotation);

            cam.GetLocalPositionAndRotation(out var foundPosition, out _);
            if (Differs(foundPosition, wroteCameraPosition))
                cameraRest = foundPosition;

            // Room scale: the view is where your head is. The height is your
            // real height on top of the game's STANDING eye height - the
            // game's crouch changes its collider, not your view.
            if (cameraRest.y > standingRestY || !standingKnown)
            {
                standingRestY = cameraRest.y;
                standingKnown = true;
            }

            var rest = new Vector3(cameraRest.x, standingRestY, cameraRest.z);
            var position = rest + toParent * (basis * offset);

            // The mod's crouch lowers the view only.
            var now = Time.unscaledTime;
            if (now != crouchTime)
            {
                crouchDrop = Mathf.Lerp(crouchDrop, crouchTarget, MathUtil.Damp(0.08f, now - crouchTime));
                crouchTime = now;
            }

            position.y -= crouchDrop;
            lastBasis = basis;

            cam.SetLocalPositionAndRotation(position, toParent * (basis * hmdRotation));
            wroteCameraPosition = position;
        }
        else
        {
            // Any other camera (main menu, cut-scenes): the headset pose on top
            // of whatever the game is doing with the camera, yaw only, so the
            // horizon stays level.
            cam.GetLocalPositionAndRotation(out var foundPosition, out var foundRotation);
            if (Differs(foundPosition, wroteFreePosition) || Differs(foundRotation, wroteFreeRotation))
            {
                gamePosition = foundPosition;
                gameYaw = Quaternion.Euler(0f, Yaw(foundRotation), 0f);
            }

            var basis = gameYaw * Quaternion.Euler(0f, bodyYaw, 0f);
            var position = gamePosition + basis * offset;
            var rotation = basis * hmdRotation;
            cam.SetLocalPositionAndRotation(position, rotation);
            wroteFreePosition = position;
            wroteFreeRotation = rotation;
        }

        cam.GetPositionAndRotation(out var worldPosition, out var worldRotation);
        var toWorld = worldRotation * Quaternion.Inverse(hmdRotation);
        TrackingToWorldRotation = toWorld;
        TrackingToWorldPosition = worldPosition - toWorld * hmdPosition;
        HasPose = true;
    }

    // The game reads the look angles for things other than the transforms
    // (aim assists, interaction, networking). Keep its pitch in step.
    internal void SyncLookAngles()
    {
        var player = Controller;
        var cam = CameraTransform;
        if (player is null || cam is null)
            return;

        cam.GetPositionAndRotation(out _, out var rotation);
        var pitch = rotation.eulerAngles.x;
        if (pitch > 180f)
            pitch -= 360f;
        player.VerticalLookRotation = pitch;

        // Other players see where this one looks from the physical
        // controller's look angles (sent with LookChangedRpc): the head's yaw
        // (snap and smooth turning included) and pitch, as a flat player's
        // mouse would have set them.
        try
        {
            if (!Alive(physical))
                physical = player.m_controller;
            var pivot = headTurn;
            if (Alive(physical))
            {
                var parent = pivot is not null && Alive(pivot) ? pivot.parent : null;
                var local = parent is not null && Alive(parent) ? Quaternion.Inverse(parent.rotation) * rotation : rotation;
                var yaw = local.eulerAngles.y;
                if (yaw > 180f)
                    yaw -= 360f;
                physical!.VerticalLookRotation = pitch;
                physical.HorizontalLookRotation = yaw;
                player.HorizontalLookRotation = yaw;
            }
        }
        catch
        {
            // Not every controller exposes these.
        }
    }

    // The crouch stage (0 standing, 1 crouching, 2 near the floor), matching
    // the game's stances so other players see it.
    internal int CrouchStage => crouchStage;

    internal void SetCrouchStage(int stage)
    {
        crouchStage = ((stage % 3) + 3) % 3;
        var eye = EyeHeight();
        crouchTarget = crouchStage switch
        {
            1 => eye * 0.5f,
            2 => Mathf.Max(0f, eye - 0.3f),
            _ => 0f,
        };
    }

    internal void ApplyWasher(in HandInput hand, Settings settings)
    {
        HasWasher = false;

        var assembly = Assembly;
        if (assembly is null || !Alive(assembly) || !HasPose || !hand.Tracked)
            return;

        var toWorld = TrackingToWorldRotation;
        var handRotation = hand.AimTracked ? hand.AimRotation : hand.GripRotation;
        var rotation = toWorld * handRotation * settings.WasherRotation;
        var position = TrackingToWorldPosition + toWorld * hand.GripPosition + rotation * settings.WasherOffset;

        WasherPosition = position;
        WasherRotation = rotation;
        HasWasher = true;
        Place(assembly);
    }

    // The washer somewhere other than a hand (Physical Washer: on the belt or
    // flying).
    internal void ApplyWasherPose(Vector3 position, Quaternion rotation)
    {
        HasWasher = false;
        var assembly = Assembly;
        if (assembly is null || !Alive(assembly) || !HasPose)
            return;
        WasherPosition = position;
        WasherRotation = rotation;
        HasWasher = true;
        Place(assembly);
    }

    // Where the game hangs the washer from: the first-person rig's hand
    // attachment, which EquipmentAnchor copies every frame. Driving only the
    // washer let the arm's reach drag it back; once the hands are set up
    // (DriveAnchor), the attachment and the anchor are moved too, so nothing
    // in the game's rig can pull the washer away from the controller.
    internal Transform? AttachPoint;
    internal bool DriveAnchor;

    private void Place(Transform assembly)
    {
        var anchor = Anchor;
        if (DriveAnchor && HasAssemblyRest && anchor is not null && Alive(anchor))
        {
            var scale = anchor.lossyScale;
            var anchorRotation = WasherRotation * Quaternion.Inverse(AssemblyRestRotation);
            var anchorPosition = WasherPosition - anchorRotation * Vector3.Scale(AssemblyRestPosition, scale);

            var attach = AttachPoint;
            if (attach is not null && Alive(attach))
                attach.SetPositionAndRotation(anchorPosition, anchorRotation);
            anchor.SetPositionAndRotation(anchorPosition, anchorRotation);
        }

        assembly.SetPositionAndRotation(WasherPosition, WasherRotation);
    }

    // Re-asserts the last washer pose. Called from the game's own pre-render
    // camera callback, which runs after onBeforeRender and may move the rig.
    internal void ReapplyWasher()
    {
        if (!HasWasher)
            return;

        var assembly = Assembly;
        if (assembly is not null && Alive(assembly))
            Place(assembly);
    }

    private static float Safe(float value) => Mathf.Abs(value) < 1e-5f ? 1f : value;

    private float standingRestY;
    private int crouchStage;
    private float crouchTarget;
    private float crouchDrop;
    private float crouchTime;

    // Standing -> half height -> just above the floor -> standing.
    internal void CycleCrouch()
    {
        crouchStage = (crouchStage + 1) % 3;
        var eye = EyeHeight();
        crouchTarget = crouchStage switch
        {
            1 => eye * 0.5f,
            2 => Mathf.Max(0f, eye - 0.3f),
            _ => 0f,
        };
    }

    internal bool Crouched => crouchStage != 0;

    // Back to standing (the cycle starts again from there).
    internal void Stand()
    {
        crouchStage = 0;
        crouchTarget = 0f;
    }

    // How high the standing view is above the game's feet.
    // ---- going under things when you are low ----
    // The game's controller is never changed: its capsule keeps its height,
    // radius and centre (resizing it made the game run its own crouch and
    // the view jittered). While your head is low, that capsule only stops
    // colliding (it is made a trigger, so the controller still reads it as
    // before), and a second capsule on the same body does the colliding: same
    // bottom, same radius, its top just over your head. So the floor, steps
    // and ground checks are as they were, and you fit under anything - the
    // level's own geometry included - that your head fits under. Standing up
    // gives the game's capsule back once there is room for it.
    private CapsuleCollider? proxy;
    private CapsuleCollider? proxyFor;
    private bool proxyOn;
    private readonly List<(Collider Collider, bool WasTrigger)> tall = new();
    private int hitboxLogs;

    internal void UpdateHitbox(bool playing)
    {
        try
        {
            if (!Alive(physical))
                physical = Controller?.m_controller;
            var cam = CameraTransform;
            if (!Alive(physical) || cam is null)
            {
                StandUp(force: true);
                return;
            }

            // In menus (or without a pose) it stays as it is: going solid
            // under a table would push you out.
            if (!playing || !HasPose)
                return;

            var controller = physical!;
            var capsule = controller.m_collision;
            if (capsule is null || capsule == null || capsule.direction != 1)
                return;
            if (proxyFor is not null && (proxyFor == null || proxyFor.Pointer != capsule.Pointer))
            {
                StandUp(force: true);
                proxy = null;
                headSphere = null;
            }

            var scale = Mathf.Abs(capsule.transform.lossyScale.y);
            if (scale < 1e-4f)
                return;
            var foot = controller.FootPosition.y;
            var full = capsule.height * scale;
            var head = cam.position.y - foot;
            var wanted = Mathf.Max(capsule.radius * scale * 2f + 0.05f, head + HeadClearance);
            var low = wanted < full - 0.1f;

            if (low)
            {
                EnsureProxy(capsule);
                // Bottom where the game's is; top over your head.
                var bottom = capsule.center.y - capsule.height * 0.5f;
                var height = wanted / scale;
                proxy!.height = height;
                proxy.radius = capsule.radius;
                proxy.center = new Vector3(capsule.center.x, bottom + height * 0.5f, capsule.center.z);
                if (!proxyOn)
                {
                    proxyOn = true;
                    proxy.enabled = true;
                    TallOff(controller, foot + wanted);
                    if (hitboxLogs++ < 6)
                        log.Msg($"rig: low collider on ({wanted:0.00} m of {full:0.00} m)");
                }

                if (!capsule.isTrigger)
                    capsule.isTrigger = true;
                PlaceHead(capsule, cam.position, scale);
            }
            else if (proxyOn)
            {
                // Grow to your head while there is no room yet to stand.
                if (Room(capsule, foot, full))
                {
                    StandUp(force: false);
                }
                else
                {
                    var bottom = capsule.center.y - capsule.height * 0.5f;
                    var height = Mathf.Min(wanted, full) / scale;
                    if (FreeAbove(capsule, foot, height * scale))
                    {
                        proxy!.height = height;
                        proxy.center = new Vector3(capsule.center.x, bottom + height * 0.5f, capsule.center.z);
                    }

                    if (!capsule.isTrigger)
                        capsule.isTrigger = true;
                    PlaceHead(capsule, cam.position, scale);
                }
            }
        }
        catch (Exception exception)
        {
            if (hitboxLogs++ < 6)
                log.Msg($"rig: low collider not available ({exception.GetType().Name}: {exception.Message})");
        }
    }

    // Above the eyes to the top of the head, with a margin so the view does
    // not reach into what is overhead.
    private const float HeadClearance = 0.24f;
    private const float HeadRadius = 0.2f;
    private SphereCollider? headSphere;

    // Your head itself, wherever you lean (room scale puts it off the body's
    // axis): a sphere on the same body at the view, so walking your head into
    // something overhead stops you instead of clipping through it.
    private void PlaceHead(CapsuleCollider capsule, Vector3 headWorld, float scale)
    {
        if (headSphere is null || headSphere == null)
        {
            headSphere = capsule.gameObject.AddComponent<SphereCollider>();
            headSphere.sharedMaterial = capsule.sharedMaterial;
        }

        var body = capsule.transform;
        var axis = body.position;
        var offset = headWorld - axis;
        var flat = new Vector3(offset.x, 0f, offset.z);
        // Not further out than an arm's lean.
        if (flat.magnitude > 0.5f)
            headWorld = new Vector3(axis.x, headWorld.y, axis.z) + flat.normalized * 0.5f;
        // Centred a little above the eyes, so its top clears the skull.
        headWorld.y += HeadClearance - HeadRadius;
        headSphere.center = body.InverseTransformPoint(headWorld);
        headSphere.radius = HeadRadius / scale;
        if (!headSphere.enabled)
            headSphere.enabled = true;
    }

    private void EnsureProxy(CapsuleCollider capsule)
    {
        if (proxy is not null && proxy != null)
            return;
        proxy = capsule.gameObject.AddComponent<CapsuleCollider>();
        proxy.direction = 1;
        proxy.sharedMaterial = capsule.sharedMaterial;
        proxy.enabled = false;
        proxyFor = capsule;
        proxyOn = false;
    }

    // The player's other solid colliders that reach above the low capsule
    // stop colliding too while it is on.
    private void TallOff(Il2CppFuturLab.PW2.PhysicalCharacterController controller, float topY)
    {
        tall.Clear();
        var all = controller.transform.root.GetComponentsInChildren<Collider>(false);
        for (var index = 0; all is not null && index < all.Length; index++)
        {
            var other = all[index];
            if (other is null || other == null || other.isTrigger || !other.enabled)
                continue;
            if (proxy is not null && other.Pointer == proxy.Pointer)
                continue;
            if (headSphere is not null && other.Pointer == headSphere.Pointer)
                continue;
            if (proxyFor is not null && other.Pointer == proxyFor.Pointer)
                continue;
            if (other.bounds.max.y <= topY + 0.02f)
                continue;
            tall.Add((other, other.isTrigger));
            other.isTrigger = true;
        }
    }

    private void StandUp(bool force)
    {
        if (!proxyOn)
            return;
        if (!force && Alive(physical))
        {
            var capsule = physical!.m_collision;
            if (capsule is not null && capsule != null && !Room(capsule, physical.FootPosition.y, capsule.height * Mathf.Abs(capsule.transform.lossyScale.y)))
                return;
        }

        proxyOn = false;
        try
        {
            if (proxyFor is not null && proxyFor != null)
                proxyFor.isTrigger = false;
            foreach (var (other, wasTrigger) in tall)
            {
                if (other is not null && other != null)
                    other.isTrigger = wasTrigger;
            }

            tall.Clear();
            if (proxy is not null && proxy != null)
                proxy.enabled = false;
            if (headSphere is not null && headSphere != null)
                headSphere.enabled = false;
            if (hitboxLogs++ < 6)
                log.Msg("rig: full collider back");
        }
        catch
        {
            // Put back on the next try.
        }
    }

    // Room for the game's full capsule (the floor under it left out).
    private static bool Room(CapsuleCollider capsule, float foot, float full) => FreeAbove(capsule, foot, full);

    private static bool FreeAbove(CapsuleCollider capsule, float foot, float height)
    {
        var radius = capsule.radius * Mathf.Abs(capsule.transform.lossyScale.x) * 0.95f;
        var position = capsule.transform.position;
        var low = new Vector3(position.x, foot + radius + 0.08f, position.z);
        var high = new Vector3(position.x, foot + Mathf.Max(height - radius, radius + 0.08f), position.z);
        var mask = ~((1 << capsule.gameObject.layer) | (1 << 2));
        return !Physics.CheckCapsule(low, high, radius, mask, QueryTriggerInteraction.Ignore);
    }

    // For the multiplayer body: the mod's current crouch drop and the feet.
    internal float CrouchDrop => crouchDrop;

    internal bool TryFootY(out float y)
    {
        y = 0f;
        try
        {
            if (!Alive(physical))
                physical = Controller?.m_controller;
            if (!Alive(physical))
                return false;
            y = physical!.FootPosition.y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private float EyeHeight()
    {
        try
        {
            if (!Alive(physical))
                physical = Controller?.m_controller;
            var cam = CameraTransform;
            if (Alive(physical) && cam is not null)
            {
                var height = cam.position.y + crouchDrop - physical!.FootPosition.y;
                if (height > 0.5f && height < 2.5f)
                    return height;
            }
        }
        catch
        {
            // Falls back below.
        }

        return 1.65f;
    }
    private bool standingKnown;
    private Quaternion lastBasis = Quaternion.identity;
    private Il2CppFuturLab.PW2.PhysicalCharacterController? physical;
    private Rigidbody? body;

    // Brings the game's body back under your head, once: on crouch and on
    // recenter only, never per frame. The view does not move: the body moves
    // by your offset and the offset is taken out of the tracking reference.
    internal void BodyToHead()
    {
        if (!IsPlayer || !lastHeadValid || !baseCaptured)
            return;

        try
        {
            if (!Alive(physical))
            {
                physical = Controller?.m_controller;
                body = Alive(physical) ? physical!.Rigidbody : null;
            }

            if (!Alive(body))
                return;

            var flat = lastHead - headBase;
            flat.y = 0f;
            var step = lastBasis * flat;
            step.y = 0f;
            if (step.sqrMagnitude < 0.0004f || step.sqrMagnitude > 9f)
                return;

            body!.position += step;
            body.transform.position += step;
            headBase += flat;
        }
        catch
        {
            // Leaves the body where it was.
        }
    }


    internal static float Yaw(Quaternion rotation)
    {
        // Yaw of the forward vector, robust when looking straight up or down.
        var forward = rotation * Vector3.forward;
        if (forward.x * forward.x + forward.z * forward.z < 1e-6f)
        {
            var up = rotation * Vector3.up;
            forward = -up * Mathf.Sign(forward.y);
        }

        return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }

    private static bool Differs(Quaternion a, Quaternion b) =>
        Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) < 0.99999f;

    private static bool Differs(Vector3 a, Vector3 b) =>
        float.IsNaN(b.x) || (a - b).sqrMagnitude > 1e-8f;
}
