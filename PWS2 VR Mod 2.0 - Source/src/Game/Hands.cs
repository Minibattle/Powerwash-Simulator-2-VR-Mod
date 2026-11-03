using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;

namespace PowerWashVR.Game;

// Floating hands, made from the character's own first-person arms - so they
// wear whatever gloves were picked in the customisation screen.
//
// The flat game's arms are one skinned mesh hanging off the camera. For VR the
// original stays hidden and is drawn twice instead, once per hand, each copy
// skinned to its own small set of proxy bones:
//
//   - the hand and its fingers are copies of the real hand bones, posed by
//     the mod;
//   - every other bone (forearm, upper arm, the other arm, the spine) points
//     at one collapsed proxy at the wrist.
//
// Vertices that belong to the arm therefore shrink to the wrist and vanish,
// and the cuff closes where the glove ends. There is no IK and nothing stretches.
//
// The poses come from the game's own animation, captured once and then held,
// so the hands never play the idle motion; the hidden original's Animator is
// then switched off.
//
//   washer hand  where the animation holds the washer grip, kept relative to
//                the washer, so it stays on the grip wherever the washer goes;
//   free hand    the mirror image of the washer hand relative to its
//                controller, fingers curling with the controller's trigger,
//                grip and thumb sensors between the bind pose (open) and the
//                captured pose (closed).
internal sealed class Hands
{
    private enum Phase
    {
        Searching,
        Capturing,
        Frozen,
        Failed,
    }

    private const float CollapsedScale = 1e-4f;

    private readonly MelonLogger.Instance log;
    private Phase phase = Phase.Searching;
    private float nextSearch;
    private float nextCheck;
    private IntPtr pivotPointer;
    private int failures;

    private readonly List<SkinnedMeshRenderer> sources = new();
    private readonly List<Animator> animators = new();

    private Side? washer;
    private Side? free;
    private Quaternion mirrorFix = Quaternion.identity;
    private bool mirrorFromBind;

    private float captureStart;
    private int stableFrames;
    private Vector3 lastRelPosition;
    private Quaternion lastRelRotation = Quaternion.identity;
    // The game's animation always holds the washer in the right hand: the
    // grip is measured there and mirrored onto the left hand when the washer
    // is in the left.
    private Side? holder, other;
    private bool capturedLeftHanded;

    private Vector3 washerRelPosition;
    private Quaternion washerRelRotation = Quaternion.identity;
    private Vector3 washerGripRelPosition;
    private Quaternion washerGripRelRotation = Quaternion.identity;
    private bool gripRelKnown;

    // Per finger: 0 thumb, 1 index, 2 middle, 3 ring, 4 pinky.
    private readonly float[] curl = new float[5];
    private readonly float[] curlTarget = new float[5];

    // Set by the belt while the free hand carries a nozzle.
    internal bool FreeHolding;

    // Set by the washer case: the free hand is not drawn while it holds it.
    internal bool FreeHidden;

    // Set by the washer case: the washer is away, so the washer hand curls its
    // fingers like the free hand; and hidden while it holds a prop.
    internal bool WasherFree;
    internal bool WasherHidden;
    // Physical Washer: the washer hand holds a belt nozzle (drawn in its place).
    internal bool WasherHandHolding;
    private bool washerWasFree;
    private readonly float[] washerCurl = new float[5];
    private const float FadeTime = 0.15f;

    // Between the free hand's thumb and index fingertips, after posing.
    internal bool HasPinch { get; private set; }
    internal Vector3 PinchPosition { get; private set; }
    internal Quaternion PinchRotation { get; private set; } = Quaternion.identity;
    private Transform? thumbTip, indexTip;

    // Read by the belt: where the free hand's palm is.
    internal Vector3 FreePalmPosition { get; private set; }
    internal Quaternion FreePalmRotation { get; private set; } = Quaternion.identity;
    internal bool Ready => phase == Phase.Frozen;
    internal bool Settled => phase is Phase.Frozen or Phase.Failed;

    private sealed class Bone
    {
        internal Transform Source = null!;
        internal Transform Proxy = null!;
        internal Quaternion Closed;
        internal Quaternion Open;
        internal int Group;   // 0 thumb, 1 index, 2 middle, 3 ring, 4 pinky; < 0 fixed
        internal Vector3 Axis;  // local bend axis, from bind pose to the captured grip
        internal int Depth;
        internal Quaternion BindMesh = Quaternion.identity;   // the bone's bind pose in mesh space
        internal bool HasBindMesh;
    }

    private sealed class Side
    {
        internal string Name = "";
        internal Transform Hand = null!;
        internal bool HasBind;
        internal Vector3 BindPosition;
        internal Quaternion BindRotation = Quaternion.identity;
        internal Vector3 HandScale = Vector3.one;

        internal GameObject Root = null!;
        internal Transform ProxyHand = null!;
        internal Transform Collapsed = null!;
        internal readonly Dictionary<IntPtr, Transform> Proxies = new();
        internal readonly List<Bone> Fingers = new();
        internal readonly List<SkinnedMeshRenderer> Copies = new();
        internal float Visible = 1f;
        internal readonly List<IntPtr> CopiedMesh = new();
    }

    internal Hands(MelonLogger.Instance log)
    {
        this.log = log;
    }

    private bool builtLeftHanded;

    internal void Reset()
    {
        // The game's arms animate again, so a new capture starts from its pose.
        foreach (var animator in animators)
        {
            if (animator is not null && animator != null)
                animator.enabled = true;
        }

        Destroy(washer);
        Destroy(free);
        washer = free = null;
        sources.Clear();
        animators.Clear();
        phase = Phase.Searching;
        pivotPointer = IntPtr.Zero;
        gripRelKnown = false;
        rawRelKnown = false;
        nextSearch = 0f;
    }

    // Timers and discovery; runs in Update.
    internal void Update(PlayerRig rig, Settings settings)
    {
        var pivot = rig.ArmsPivot;
        var now = Time.unscaledTime;

        if (pivot is null || !PlayerRig.Alive(pivot))
        {
            if (phase != Phase.Searching && phase != Phase.Failed)
                Reset();
            return;
        }

        if (phase != Phase.Searching && pivot.Pointer != pivotPointer)
            Reset();

        // The washer hand switched (Left Handed, or Physical Washer taken in
        // the other hand): built hands just swap roles, at once; otherwise
        // they are rebuilt.
        if (builtLeftHanded != settings.LeftHanded)
        {
            if (phase == Phase.Frozen && washer is not null && free is not null && rawRelKnown)
                SwapRoles(settings.LeftHanded);
            else if (phase != Phase.Searching)
                Reset();
            builtLeftHanded = settings.LeftHanded;
        }

        if (phase == Phase.Searching && now >= nextSearch)
        {
            nextSearch = now + 2f;
            Discover(rig, pivot, settings);
        }

        if (phase == Phase.Frozen && now >= nextCheck)
        {
            nextCheck = now + 0.5f;
            Maintain(settings);
        }
    }

    // Capture and pose; runs in LateUpdate (after the game's animation) and
    // again in onBeforeRender with the newest poses.
    internal void Apply(PlayerRig rig, Controllers controllers, Settings settings, float deltaTime, bool lateUpdate)
    {
        if (phase == Phase.Capturing && lateUpdate)
        {
            Capture(rig, settings);
            return;
        }

        if (phase != Phase.Frozen || washer is null || free is null || !rig.HasPose)
            return;

        if (!settings.Hands)
        {
            Show(washer, false);
            Show(free, false);
            return;
        }

        ref var washerHand = ref settings.LeftHanded ? ref controllers.Left : ref controllers.Right;
        ref var freeHand = ref settings.LeftHanded ? ref controllers.Right : ref controllers.Left;
        var toWorld = rig.TrackingToWorldRotation;
        var origin = rig.TrackingToWorldPosition;

        // Washer hand: on the washer's grip.
        Vector3 washerPosition;
        Quaternion washerRotation;
        // Physical Washer: with the washer on the belt or in the air the hand
        // stays on its controller.
        var away = PhysicalWasher.Away;
        if (rig.HasWasher && !away)
        {
            washerRotation = rig.WasherRotation * washerRelRotation;
            washerPosition = rig.WasherPosition + rig.WasherRotation * washerRelPosition;
        }
        else if (away && washerHand.Tracked)
        {
            // Physical Washer, nothing in that hand (also from the start,
            // before the washer was ever held): where the hand would be if
            // it held the washer, from its controller - the same placement
            // the rig gives the washer.
            var handRotation = washerHand.AimTracked ? washerHand.AimRotation : washerHand.GripRotation;
            var virtualRotation = toWorld * handRotation * settings.WasherRotation;
            var virtualPosition = origin + toWorld * washerHand.GripPosition + virtualRotation * settings.WasherOffset;
            washerRotation = virtualRotation * washerRelRotation;
            washerPosition = virtualPosition + virtualRotation * washerRelPosition;
            if (!gripRelKnown)
            {
                var grip = toWorld * washerHand.GripRotation;
                var inverse = Quaternion.Inverse(grip);
                washerGripRelRotation = inverse * washerRotation;
                washerGripRelPosition = inverse * (washerPosition - (origin + toWorld * washerHand.GripPosition));
                gripRelKnown = true;
            }
        }
        else if (gripRelKnown && washerHand.Tracked)
        {
            var grip = toWorld * washerHand.GripRotation;
            washerRotation = grip * washerGripRelRotation;
            washerPosition = origin + toWorld * washerHand.GripPosition + grip * washerGripRelPosition;
        }
        else
        {
            Show(washer, false);
            Show(free, false);
            return;
        }

        // How the washer hand sits on its controller, mirrored for the other.
        if (washerHand.Tracked && rig.HasWasher && !away)
        {
            var grip = toWorld * washerHand.GripRotation;
            var gripPosition = origin + toWorld * washerHand.GripPosition;
            var inverse = Quaternion.Inverse(grip);
            washerGripRelRotation = inverse * washerRotation;
            washerGripRelPosition = inverse * (washerPosition - gripPosition);
            gripRelKnown = true;
        }

        Pose(washer, washerPosition, washerRotation);
        if (WasherFree)
        {
            if (lateUpdate)
                Curl(washerHand, settings.LeftHanded, deltaTime, washerCurl, false);
            ApplyCurl(washer, washerCurl);
            washerWasFree = true;
        }
        else if (washerWasFree)
        {
            // Back on the washer grip: the captured hold.
            foreach (var bone in washer.Fingers)
                bone.Proxy.localRotation = bone.Closed;
            washerWasFree = false;
        }

        Fade(washer, washerHand.Tracked && !WasherHidden && !WasherHandHolding, lateUpdate ? deltaTime : 0f);

        if (freeHand.Tracked && gripRelKnown)
        {
            var grip = toWorld * freeHand.GripRotation;
            var gripPosition = origin + toWorld * freeHand.GripPosition;
            var relRotation = MathUtil.Mirror(washerGripRelRotation, Vector3.right) * mirrorFix * settings.FreeHandRotation;
            var relPosition = MathUtil.MirrorX(washerGripRelPosition);
            var rotation = grip * relRotation;
            var position = gripPosition + grip * relPosition;
            Pose(free, position, rotation);
            FreePalmPosition = gripPosition;
            FreePalmRotation = grip;

            if (lateUpdate)
                Curl(freeHand, !settings.LeftHanded, deltaTime, curl, FreeHolding);
            ApplyCurl(free, curl);
            Fade(free, !FreeHidden && !FreeHolding, lateUpdate ? deltaTime : 0f);

            HasPinch = thumbTip is not null && indexTip is not null;
            if (HasPinch)
            {
                PinchPosition = (thumbTip!.position + indexTip!.position) * 0.5f;
                PinchRotation = grip;
            }
        }
        else
        {
            Fade(free, false, lateUpdate ? deltaTime : 0f);
        }
    }

    // Hiding is a quick fade: the hand shrinks into the wrist over a few
    // frames rather than blinking out (the game's glove material is opaque,
    // so a true transparency fade is not available).
    private static void Fade(Side side, bool visible, float deltaTime)
    {
        var target = visible ? 1f : 0f;
        if (deltaTime > 0f)
            side.Visible = Mathf.MoveTowards(side.Visible, target, deltaTime / FadeTime);
        else if (!visible && side.Visible <= 0f)
            side.Visible = 0f;

        var eased = side.Visible * side.Visible * (3f - 2f * side.Visible);
        side.ProxyHand.localScale = side.HandScale * Mathf.Max(0.001f, eased);
        Show(side, side.Visible > 0.001f);
    }

    private void Discover(PlayerRig rig, Transform pivot, Settings settings)
    {
        sources.Clear();
        var skins = pivot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (skins is null || skins.Length == 0)
            return;

        SkinnedMeshRenderer? primary = null;
        var bones = new Dictionary<IntPtr, Transform>();
        var primaryIndex = new Dictionary<IntPtr, int>();

        for (var index = 0; index < skins.Length; index++)
        {
            var skin = skins[index];
            if (skin is null || skin == null)
                continue;

            var array = skin.bones;
            if (array is null || array.Length == 0)
                continue;

            sources.Add(skin);
            if (primary is null || array.Length > primary.bones.Length)
                primary = skin;

            for (var bone = 0; bone < array.Length; bone++)
            {
                var transform = array[bone];
                if (transform is not null && transform != null)
                    bones[transform.Pointer] = transform;
            }
        }

        if (primary is null || bones.Count == 0)
            return;

        var primaryBones = primary.bones;
        for (var index = 0; index < primaryBones.Length; index++)
        {
            var transform = primaryBones[index];
            if (transform is not null && transform != null)
                primaryIndex[transform.Pointer] = index;
        }

        // The two hands: a bone named like a hand with at least three child
        // bones (fingers). Fallback: any bone with four or more child bones.
        var candidates = new List<Transform>();
        foreach (var bone in bones.Values)
        {
            var name = bone.name.ToLowerInvariant();
            if ((name.Contains("hand") || name.Contains("wrist")) && !IsFingerName(name) && ChildBones(bone, bones) >= 3)
                candidates.Add(bone);
        }

        if (candidates.Count < 2)
        {
            candidates.Clear();
            foreach (var bone in bones.Values)
            {
                if (ChildBones(bone, bones) >= 4)
                    candidates.Add(bone);
            }
        }

        var camera = rig.CameraTransform;
        Transform? right = null, left = null;
        foreach (var candidate in candidates)
        {
            var side = SideOf(candidate, camera);
            if (side > 0 && (right is null || Better(candidate, right, bones)))
                right = candidate;
            else if (side < 0 && (left is null || Better(candidate, left, bones)))
                left = candidate;
        }

        LogSkeleton(pivot, bones, right, left);

        if (right is null || left is null)
        {
            if (++failures >= 3)
            {
                phase = Phase.Failed;
                log.Warning("hands: no two hand bones found in the first-person arms; hands are off. "
                    + "The bone list above tells what this game version calls them.");
            }

            return;
        }

        var bind = ReadBind(primary);
        var rightSide = BuildSide("R", right, bones, bind, primaryIndex);
        var leftSide = BuildSide("L", left, bones, bind, primaryIndex);

        washer = settings.LeftHanded ? leftSide : rightSide;
        free = settings.LeftHanded ? rightSide : leftSide;
        holder = rightSide;
        other = leftSide;
        capturedLeftHanded = settings.LeftHanded;

        // The correction that turns a mirrored washer-hand frame into the free
        // hand's bone frame, from the bind pose, where the two hands are each
        // other's mirror image.
        if (washer.HasBind && free.HasBind)
        {
            var normal = (washer.BindPosition - free.BindPosition).normalized;
            mirrorFix = Quaternion.Inverse(MathUtil.Mirror(washer.BindRotation, normal)) * free.BindRotation;
            mirrorFromBind = true;
        }
        else
        {
            mirrorFix = Quaternion.identity;
            mirrorFromBind = false;
        }

        animators.Clear();
        var found = pivot.GetComponentsInChildren<Animator>(true);
        if (found is not null)
        {
            for (var index = 0; index < found.Length; index++)
            {
                if (found[index] is not null && found[index] != null)
                    animators.Add(found[index]);
            }
        }

        pivotPointer = pivot.Pointer;
        phase = Phase.Capturing;
        captureStart = Time.unscaledTime;
        stableFrames = 0;
        log.Msg($"hands: washer hand \"{washer.Hand.name}\", free hand \"{free.Hand.name}\", "
            + $"{free.Fingers.Count} free finger bone(s), {sources.Count} mesh(es), {animators.Count} animator(s), "
            + $"mirror from {(mirrorFromBind ? "bind pose" : "nothing (no bind pose)")}");
    }

    private static bool IsFingerName(string name) =>
        name.Contains("finger") || name.Contains("thumb") || name.Contains("index") || name.Contains("middle")
        || name.Contains("ring") || name.Contains("pinky") || name.Contains("little") || name.Contains("palm");

    private static int ChildBones(Transform bone, Dictionary<IntPtr, Transform> bones)
    {
        var count = 0;
        for (var index = 0; index < bone.childCount; index++)
        {
            var child = bone.GetChild(index);
            if (child is not null && bones.ContainsKey(child.Pointer))
                count++;
        }

        return count;
    }

    private static bool Better(Transform candidate, Transform current, Dictionary<IntPtr, Transform> bones) =>
        ChildBones(candidate, bones) > ChildBones(current, bones);

    // +1 right, -1 left: from the name when it says, else from where the bone
    // sits relative to the camera.
    private static int SideOf(Transform bone, Transform? camera)
    {
        var name = bone.name;
        var lower = name.ToLowerInvariant();
        if (lower.Contains("right") || lower.StartsWith("r_") || lower.StartsWith("r.") || lower.EndsWith("_r")
            || lower.EndsWith(".r") || lower.Contains("_r_") || name.EndsWith("R", StringComparison.Ordinal) && !name.EndsWith("RR"))
            return 1;
        if (lower.Contains("left") || lower.StartsWith("l_") || lower.StartsWith("l.") || lower.EndsWith("_l")
            || lower.EndsWith(".l") || lower.Contains("_l_") || name.EndsWith("L", StringComparison.Ordinal))
            return -1;

        if (camera is null)
            return 0;

        return camera.InverseTransformPoint(bone.position).x >= 0f ? 1 : -1;
    }

    private sealed class BindPose
    {
        internal Vector3[] Position = Array.Empty<Vector3>();
        internal Quaternion[] Rotation = Array.Empty<Quaternion>();
    }

    private BindPose? ReadBind(SkinnedMeshRenderer primary)
    {
        try
        {
            var mesh = primary.sharedMesh;
            var poses = mesh is null ? null : mesh.bindposes;
            if (poses is null || poses.Length == 0)
                return null;

            var bind = new BindPose { Position = new Vector3[poses.Length], Rotation = new Quaternion[poses.Length] };
            for (var index = 0; index < poses.Length; index++)
            {
                // The bind pose maps mesh space into bone space; its inverse is
                // the bone's pose in mesh space.
                var inverse = poses[index].inverse;
                bind.Position[index] = inverse.MultiplyPoint3x4(Vector3.zero);
                bind.Rotation[index] = inverse.rotation;
            }

            return bind;
        }
        catch (Exception exception)
        {
            log.Msg($"hands: bind pose not readable ({exception.GetType().Name}); fingers will not open");
            return null;
        }
    }

    private Side BuildSide(string label, Transform hand, Dictionary<IntPtr, Transform> bones, BindPose? bind,
        Dictionary<IntPtr, int> index)
    {
        var side = new Side { Name = label, Hand = hand };

        if (bind is not null && index.TryGetValue(hand.Pointer, out var handIndex) && handIndex < bind.Rotation.Length)
        {
            side.HasBind = true;
            side.BindPosition = bind.Position[handIndex];
            side.BindRotation = bind.Rotation[handIndex];
        }

        side.Root = new GameObject($"PowerWashVR Hand {label}");
        UnityEngine.Object.DontDestroyOnLoad(side.Root);
        side.ProxyHand = new GameObject(hand.name).transform;
        side.ProxyHand.SetParent(side.Root.transform, false);
        side.Proxies[hand.Pointer] = side.ProxyHand;

        side.Collapsed = new GameObject("Collapsed").transform;
        side.Collapsed.SetParent(side.ProxyHand, false);
        side.Collapsed.localScale = Vector3.one * CollapsedScale;

        CopyChildren(side, hand, side.ProxyHand, bones, bind, index, -1);
        return side;
    }

    private void CopyChildren(Side side, Transform source, Transform proxyParent, Dictionary<IntPtr, Transform> bones,
        BindPose? bind, Dictionary<IntPtr, int> index, int group)
    {
        for (var child = 0; child < source.childCount; child++)
        {
            var original = source.GetChild(child);
            if (original is null || original == null)
                continue;

            var proxy = new GameObject(original.name).transform;
            proxy.SetParent(proxyParent, false);
            side.Proxies[original.Pointer] = proxy;

            var lower = original.name.ToLowerInvariant();
            var fingerGroup = group;
            if (fingerGroup < 0)
                fingerGroup = lower.Contains("thumb") ? 0 : lower.Contains("index") ? 1 : lower.Contains("middle") ? 2
                    : lower.Contains("ring") ? 3 : lower.Contains("pinky") || lower.Contains("little") ? 4 : 2;

            var bone = new Bone { Source = original, Proxy = proxy, Group = fingerGroup, Depth = Depth(original, side.Hand) };

            // Open = the bind pose's local rotation, where both the bone and
            // its parent have one.
            bone.Open = Quaternion.identity;
            var hasOpen = false;
            if (bind is not null && index.TryGetValue(original.Pointer, out var own) && index.TryGetValue(source.Pointer, out var parent)
                && own < bind.Rotation.Length && parent < bind.Rotation.Length)
            {
                bone.Open = Quaternion.Inverse(bind.Rotation[parent]) * bind.Rotation[own];
                bone.BindMesh = bind.Rotation[own];
                bone.HasBindMesh = true;
                hasOpen = true;
            }

            if (bones.ContainsKey(original.Pointer))
                side.Fingers.Add(bone);
            if (!hasOpen)
                bone.Group = -2;   // keeps its captured pose

            CopyChildren(side, original, proxy, bones, bind, index, fingerGroup);
        }
    }

    // Waits for the game's animation to settle into its idle hold, then takes
    // the pose and stops the animation.
    private void Capture(PlayerRig rig, Settings settings)
    {
        if (washer is null || free is null)
            return;

        var anchor = rig.Anchor;
        if (anchor is null || !PlayerRig.Alive(anchor) || !rig.HasAssemblyRest)
        {
            if (Time.unscaledTime - captureStart > 15f)
                Freeze(Vector3.zero, Quaternion.identity, false);
            return;
        }

        // The game hangs the washer anchor from the hand attachment bone, so
        // that bone, not the anchor (which may lag a frame), is the reference.
        var attach = rig.AttachPoint;
        Vector3 anchorPosition;
        Quaternion anchorRotation;
        if (attach is not null && PlayerRig.Alive(attach))
            attach.GetPositionAndRotation(out anchorPosition, out anchorRotation);
        else
            anchor.GetPositionAndRotation(out anchorPosition, out anchorRotation);
        var assemblyRotation = anchorRotation * rig.AssemblyRestRotation;
        var assemblyPosition = anchorPosition + anchorRotation * rig.AssemblyRestPosition;

        (holder ?? washer).Hand.GetPositionAndRotation(out var handPosition, out var handRotation);
        var inverse = Quaternion.Inverse(assemblyRotation);
        var relPosition = inverse * (handPosition - assemblyPosition);
        var relRotation = inverse * handRotation;

        var moved = (relPosition - lastRelPosition).magnitude;
        var turned = Quaternion.Angle(relRotation, lastRelRotation);
        stableFrames = moved < 0.002f && turned < 1.5f ? stableFrames + 1 : 0;
        lastRelPosition = relPosition;
        lastRelRotation = relRotation;

        var elapsed = Time.unscaledTime - captureStart;
        if ((stableFrames >= 20 && elapsed > 1f) || elapsed > 6f)
            Freeze(relPosition, relRotation, true);
    }

    // The right hand's hold on the washer as captured (before any mirroring
    // for the left hand), so either hand can take the washer.
    private Vector3 rawRelPosition;
    private Quaternion rawRelRotation = Quaternion.identity;
    private bool rawRelKnown;

    private (Vector3, Quaternion) HoldFor(bool leftHanded, Vector3 relPosition, Quaternion relRotation)
    {
        if (leftHanded && holder is not null && other is not null && holder.HasBind && other.HasBind)
        {
            var normal = (holder.BindPosition - other.BindPosition).normalized;
            var fix = Quaternion.Inverse(MathUtil.Mirror(holder.BindRotation, normal)) * other.BindRotation;
            return (MathUtil.MirrorX(relPosition), MathUtil.Mirror(relRotation, Vector3.right) * fix);
        }

        return (relPosition, relRotation);
    }

    // The washer moves to the other hand: roles swap, nothing is rebuilt.
    private void SwapRoles(bool leftHanded)
    {
        (washer, free) = (free, washer);
        capturedLeftHanded = leftHanded;
        (washerRelPosition, washerRelRotation) = HoldFor(leftHanded, rawRelPosition, rawRelRotation);
        if (washer!.HasBind && free!.HasBind)
        {
            var normal = (washer.BindPosition - free.BindPosition).normalized;
            mirrorFix = Quaternion.Inverse(MathUtil.Mirror(washer.BindRotation, normal)) * free.BindRotation;
        }

        // Both sides hold a closed grip already (the washer's, and its mirror
        // on the other hand); the new free hand gets its bend axes.
        foreach (var bone in washer.Fingers)
        {
            if (bone.Group >= 0)
                bone.Proxy.localRotation = bone.Closed;
        }

        Axes(free!);
        thumbTip = Tip(free!, 0);
        indexTip = Tip(free!, 1);
        gripRelKnown = false;
        washerWasFree = false;
        if (swapLogs++ < 5)
            log.Msg($"hands: washer hand now \"{washer.Hand.name}\" (swapped, not rebuilt)");
    }

    private int swapLogs;

    // Each joint bends about the axis the game's own grip bends it; a joint
    // the grip left straight borrows the average axis of the rest. Returns
    // the average open angle and how many bones were counted.
    private (float, int) Axes(Side side)
    {
        var openAngle = 0f;
        var counted = 0;
        var average = Vector3.zero;
        foreach (var bone in side.Fingers)
        {
            if (bone.Group < 0)
                continue;
            var delta = Quaternion.Inverse(bone.Open) * bone.Closed;
            delta.ToAngleAxis(out var angle, out var axis);
            if (angle > 180f)
            {
                angle = 360f - angle;
                axis = -axis;
            }

            openAngle += angle;
            counted++;
            if (angle > 4f && bone.Group != 0)
            {
                bone.Axis = axis.normalized;
                average += bone.Axis;
            }
        }

        average = average.sqrMagnitude > 1e-6f ? average.normalized : Vector3.forward;
        foreach (var bone in side.Fingers)
        {
            if (bone.Group >= 0 && bone.Axis == Vector3.zero)
                bone.Axis = bone.Group == 0 ? Vector3.Cross(average, Vector3.up).normalized : average;
            if (bone.Axis == Vector3.zero)
                bone.Axis = average;
        }

        return (openAngle, counted);
    }

    private void Freeze(Vector3 relPosition, Quaternion relRotation, bool fromAnimation)
    {
        rawRelPosition = relPosition;
        rawRelRotation = relRotation;
        rawRelKnown = true;
        // Left-handed: the right hand's hold on the washer, mirrored across the
        // washer's own left-right plane, in the left hand's bone frame.
        (relPosition, relRotation) = HoldFor(capturedLeftHanded, relPosition, relRotation);

        washerRelPosition = relPosition;
        washerRelRotation = relRotation;

        foreach (var side in new[] { washer!, free! })
        {
            side.HandScale = side.Hand.lossyScale;
            foreach (var bone in side.Fingers)
            {
                bone.Source.GetLocalPositionAndRotation(out var position, out var rotation);
                bone.Proxy.SetLocalPositionAndRotation(position, rotation);
                bone.Proxy.localScale = bone.Source.localScale;
                bone.Closed = rotation;
            }

            // Non-bone children (attachment points) keep their offsets too.
            CopyLocals(side.Hand, side.ProxyHand, side);
        }

        // The washer hand holds the grip; it does not need open fingers.
        // The free hand's capture comes from the game's idle arm, which barely
        // closes it, so its fist is the washer hand's grip, mirrored.
        var mirrored = MirrorGrip();

        // The washer hand shows its hold straight away (its capture may have
        // been the idle hand, now replaced by the mirrored grip).
        foreach (var bone in washer!.Fingers)
        {
            if (bone.Group >= 0)
                bone.Proxy.localRotation = bone.Closed;
        }

        var (openAngle, counted) = Axes(free!);
        // Both hands can become the free hand (Physical Washer): both get
        // their bend axes.
        Axes(washer!);

        thumbTip = Tip(free, 0);
        indexTip = Tip(free, 1);

        foreach (var side in new[] { washer, free })
            BuildCopies(side);

        // The hidden original arms stop animating: their reach (the game's
        // arm rig) must not drag the washer, and nothing of them is drawn.
        for (var index = 0; index < animators.Count; index++)
        {
            var animator = animators[index];
            if (animator is not null && animator != null)
                animator.enabled = false;
        }

        phase = Phase.Frozen;
        log.Msg($"hands: pose {(fromAnimation ? "captured from the game's animation" : "NOT captured (no washer anchor)")}, "
            + $"washer grip offset {relPosition.magnitude:0.000} m, free-hand curl range "
            + $"{(counted == 0 ? 0f : openAngle / counted):0} deg over {counted} bone(s), "
            + $"{mirrored} bone(s) closed from the washer hand's grip");
    }

    // Per finger bone: the washer hand's bend from its bind pose to the grip,
    // reflected through the plane between the two hands' bind positions and
    // expressed in the matching free-hand bone. Bones pair up by name with
    // the side marker taken out.
    // From the right hand (the game's hold) to the left, whichever of them
    // holds the washer in VR.
    private int MirrorGrip()
    {
        var washer = holder ?? this.washer;
        var free = other ?? this.free;
        if (washer is null || free is null || !washer.HasBind || !free.HasBind)
            return 0;

        var normal = (washer.BindPosition - free.BindPosition).normalized;
        if (normal.sqrMagnitude < 0.5f)
            return 0;

        var byName = new Dictionary<string, Bone>();
        foreach (var bone in washer.Fingers)
        {
            if (bone.Group >= 0 && bone.HasBindMesh)
                byName[Unsided(bone.Source.name)] = bone;
        }

        var done = 0;
        foreach (var bone in free.Fingers)
        {
            if (bone.Group < 0 || !bone.HasBindMesh || !byName.TryGetValue(Unsided(bone.Source.name), out var match))
                continue;

            var delta = Quaternion.Inverse(match.Open) * match.Closed;
            delta.ToAngleAxis(out var angle, out var axis);
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f)
                continue;
            if (angle > 180f)
            {
                angle = 360f - angle;
                axis = -axis;
            }

            var meshAxis = match.BindMesh * axis.normalized;
            var reflected = -(meshAxis - 2f * Vector3.Dot(meshAxis, normal) * normal);
            var local = Quaternion.Inverse(bone.BindMesh) * reflected;
            bone.Closed = bone.Open * Quaternion.AngleAxis(angle, local);
            done++;
        }

        return done;
    }

    private static string Unsided(string name)
    {
        var lower = name.ToLowerInvariant().Replace("right", "#").Replace("left", "#");
        if (lower.StartsWith("r_") || lower.StartsWith("l_") || lower.StartsWith("r.") || lower.StartsWith("l."))
            lower = "#" + lower.Substring(1);
        if (lower.EndsWith("_r") || lower.EndsWith("_l") || lower.EndsWith(".r") || lower.EndsWith(".l"))
            lower = lower.Substring(0, lower.Length - 1) + "#";
        return lower.Replace("_r_", "_#_").Replace("_l_", "_#_");
    }

    // The deepest bone of a finger, as its tip.
    private static Transform? Tip(Side side, int group)
    {
        Bone? best = null;
        foreach (var bone in side.Fingers)
        {
            if (bone.Group == group && (best is null || bone.Depth > best.Depth))
                best = bone;
        }

        return best?.Proxy;
    }

    private static int Depth(Transform bone, Transform hand)
    {
        var depth = 0;
        for (var node = bone; node is not null && node != null && node.Pointer != hand.Pointer && depth < 8; node = node.parent)
            depth++;
        return depth;
    }

    private static void CopyLocals(Transform source, Transform proxy, Side side)
    {
        for (var child = 0; child < source.childCount; child++)
        {
            var original = source.GetChild(child);
            if (original is null || original == null || !side.Proxies.TryGetValue(original.Pointer, out var copy))
                continue;

            original.GetLocalPositionAndRotation(out var position, out var rotation);
            copy.SetLocalPositionAndRotation(position, rotation);
            copy.localScale = original.localScale;
            CopyLocals(original, copy, side);
        }
    }

    // One renderer per source mesh, skinned to this hand's proxies.
    private void BuildCopies(Side side)
    {
        foreach (var copy in side.Copies)
        {
            if (copy is not null && copy != null)
                UnityEngine.Object.Destroy(copy.gameObject);
        }

        side.Copies.Clear();
        side.CopiedMesh.Clear();

        foreach (var source in sources)
        {
            if (source is null || source == null)
                continue;

            var holder = new GameObject($"Mesh {source.name}");
            holder.layer = source.gameObject.layer;
            holder.transform.SetParent(side.Root.transform, false);
            var copy = holder.AddComponent<SkinnedMeshRenderer>();
            side.Copies.Add(copy);
            side.CopiedMesh.Add(IntPtr.Zero);
            Sync(side, side.Copies.Count - 1, source);
        }
    }

    private void Sync(Side side, int index, SkinnedMeshRenderer source)
    {
        var copy = side.Copies[index];
        var mesh = source.sharedMesh;
        var meshPointer = mesh is null ? IntPtr.Zero : mesh.Pointer;

        // Materials follow every check (the gloves are a material swap); the
        // bone mapping only when the mesh itself changed.
        copy.sharedMaterials = source.sharedMaterials;

        if (meshPointer == side.CopiedMesh[index])
            return;

        side.CopiedMesh[index] = meshPointer;
        copy.sharedMesh = mesh;

        var original = source.bones;
        var mapped = new Transform[original.Length];
        for (var bone = 0; bone < original.Length; bone++)
        {
            var transform = original[bone];
            mapped[bone] = transform is not null && side.Proxies.TryGetValue(transform.Pointer, out var proxy)
                ? proxy
                : side.Collapsed;
        }

        copy.bones = mapped;
        copy.rootBone = side.ProxyHand;
        copy.updateWhenOffscreen = false;
        var size = 0.6f / Mathf.Max(1e-4f, side.HandScale.x);
        copy.localBounds = new Bounds(Vector3.zero, new Vector3(size, size, size));
        copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        copy.receiveShadows = source.receiveShadows;
        copy.quality = SkinQuality.Bone4;
    }

    private void Maintain(Settings settings)
    {
        for (var index = 0; index < animators.Count; index++)
        {
            var animator = animators[index];
            if (animator is not null && animator != null && animator.enabled)
                animator.enabled = false;
        }

        foreach (var side in new[] { washer, free })
        {
            if (side is null)
                continue;

            for (var index = 0; index < side.Copies.Count && index < sources.Count; index++)
            {
                var source = sources[index];
                if (source is not null && source != null && side.Copies[index] is not null && side.Copies[index] != null)
                    Sync(side, index, source);
            }
        }
    }

    // For the multiplayer body: the drawn hand's pose and its finger joints'
    // local rotations (by bone name), for the left or right hand.
    internal bool TryGetHand(bool left, out Transform? hand, List<(string Name, Quaternion Local)> fingers)
    {
        fingers.Clear();
        hand = null;
        if (phase != Phase.Frozen || washer is null || free is null)
            return false;
        var side = Matches(washer, left) ? washer : Matches(free, left) ? free : null;
        if (side is null || side.ProxyHand is null || side.ProxyHand == null)
            return false;
        hand = side.ProxyHand;
        foreach (var bone in side.Fingers)
        {
            if (bone.Proxy is not null && bone.Proxy != null)
                fingers.Add((bone.Proxy.name, bone.Proxy.localRotation));
        }

        return true;
    }

    private static bool Matches(Side side, bool left)
    {
        var name = side.Name.Length > 0 ? side.Name : side.ProxyHand is not null && side.ProxyHand != null ? side.ProxyHand.name : "";
        return left ? name.StartsWith("L", StringComparison.OrdinalIgnoreCase) : name.StartsWith("R", StringComparison.OrdinalIgnoreCase);
    }

    private static void Pose(Side side, Vector3 position, Quaternion rotation)
    {
        side.ProxyHand.SetPositionAndRotation(position, rotation);
    }

    private static void Show(Side? side, bool show)
    {
        if (side?.Root is not null && side.Root != null && side.Root.activeSelf != show)
            side.Root.SetActive(show);
    }

    // Finger curl from the controller: index with the trigger, the other three
    // with the grip, the thumb with the thumb sensors - between the bind pose
    // (open) and the game's captured hold (closed).
    private static readonly float[] tracked = new float[5];

    private static void Curl(in HandInput hand, bool left, float deltaTime, float[] into, bool holding)
    {
        // Index and Steam Frame controllers: every finger from the runtime's
        // hand tracking (the controller's finger sensors), as it is.
        if (!holding && PowerWashVR.OpenXR.Bindings.HasFingerTracking && PowerWashVR.OpenXR.HandTracking.TryGetCurls(left, tracked))
        {
            var fast = MathUtil.Damp(0.015f, deltaTime);
            for (var finger = 0; finger < into.Length && finger < tracked.Length; finger++)
                into[finger] = Mathf.Lerp(into[finger], tracked[finger], fast);
            return;
        }

        var thumb = holding ? 0.9f : hand.ThumbTouch ? 0.85f : hand.HasTouch ? 0.1f : 0.5f * hand.Grip;
        // As before (trigger, a light curl when resting on it); with the grip
        // fully pressed, resting on the trigger closes the index as a pull would.
        var index = holding ? 0.9f : Mathf.Max(hand.Trigger, hand.TriggerTouch ? (hand.Grip > 0.95f ? 1f : 0.3f) : 0f);
        var rest = holding ? 0.9f : hand.Grip;

        var blend = MathUtil.Damp(0.04f, deltaTime);
        into[0] = Mathf.Lerp(into[0], thumb, blend);
        into[1] = Mathf.Lerp(into[1], index, blend);
        for (var finger = 2; finger < into.Length; finger++)
            into[finger] = Mathf.Lerp(into[finger], rest, blend);
    }

    private static void ApplyCurl(Side side, float[] values)
    {
        foreach (var bone in side.Fingers)
        {
            if (bone.Group < 0 || bone.Group >= values.Length)
                continue;

            bone.Proxy.localRotation = Quaternion.Slerp(bone.Open, bone.Closed, values[bone.Group]);
        }
    }

    private static void Destroy(Side? side)
    {
        if (side?.Root is not null && side.Root != null)
            UnityEngine.Object.Destroy(side.Root);
    }

    private bool skeletonLogged;

    private void LogSkeleton(Transform pivot, Dictionary<IntPtr, Transform> bones, Transform? right, Transform? left)
    {
        if (skeletonLogged)
            return;

        skeletonLogged = true;
        var names = new List<string>();
        foreach (var bone in bones.Values)
            names.Add(bone.name);
        names.Sort(StringComparer.Ordinal);
        log.Msg($"hands: {bones.Count} arm bone(s) under \"{pivot.name}\": {string.Join(", ", names)}");
        log.Msg($"hands: right hand {(right is null ? "not found" : $"\"{right.name}\"")}, "
            + $"left hand {(left is null ? "not found" : $"\"{left.name}\"")}");
        foreach (var source in sources)
            log.Msg($"hands: mesh \"{source.name}\" with {source.bones.Length} bone(s), root "
                + $"\"{(source.rootBone is null ? "none" : source.rootBone.name)}\"");
    }
}
