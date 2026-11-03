using System.Linq;
using Il2CppFuturLab.PW2;
using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;

namespace PowerWashVR.Game;

// The washer case, carried over the shoulder on the free-hand side.
//
// Reach up beside your ear with the free hand and squeeze the grip: the case
// comes off the shoulder into that hand (which fades out while it holds the
// case) and the game's washer wheel opens, aimed with that hand's stick. While
// you hold the case the washer is put away, so the washer hand is a free hand
// with finger tracking. The case carries two props:
//
//   right side   the short extension - take it with the washer hand and the
//                extension wheel opens in front of that hand
//   left side    the soap nozzle - take it and the nozzle wheel opens there
//
// A prop's wheel replaces the washer wheel while you hold the prop (there is
// only ever one wheel); letting go of it takes the highlighted item and brings
// the washer wheel back. Letting go of the case takes the highlighted washer.
// The case is only drawn while you hold it.
internal sealed class Backpack
{
    private const float ShoulderReach = 0.2f;
    private const float PropReach = 0.12f;
    internal const float CaseScale = 0.55f;

    private readonly MelonLogger.Instance log;
    private readonly BodyFrame body;
    private readonly NozzleModels models;
    private GameObject? root;
    private Transform? rootTransform;
    private GameObject? model;
    private IntPtr modelMesh;
    private float nextRefresh;
    private bool atShoulder;
    private bool held;
    private GameInput.Prop prop;

    private readonly PropSlot extension = new() { Kind = GameInput.Prop.Extension, Local = new Vector3(0.24f, 0f, 0f) };
    private readonly PropSlot soap = new() { Kind = GameInput.Prop.Soap, Local = new Vector3(-0.24f, 0f, 0f) };
    // Both slots, for the per-frame loops (no array made each frame).
    private PropSlot[]? bothSlots;
    private PropSlot[] BothSlots => bothSlots ??= new[] { extension, soap };

    private readonly List<(Renderer? Renderer, bool Third)> washerRenderers = new();
    private bool washerHidden;

    private sealed class PropSlot
    {
        internal GameInput.Prop Kind;
        internal Vector3 Local;
        internal BaseEquipmentData? Data;
        internal int ShownId = -1;
        internal bool HasModel;
        internal Transform? Holder;
        internal float GrowAt;
    }

    internal Backpack(MelonLogger.Instance log, BodyFrame body)
    {
        this.log = log;
        this.body = body;
        models = new NozzleModels(log, 1.5f);
    }

    // The free grip belongs to the case at the shoulder and while holding it.
    internal bool Holds(bool left) => inHand[left ? 0 : 1] is not null;

    internal bool Claims => atShoulder || held || (placed && nearPlaced) || inHand[freeIndex] is not null;
    internal bool Held => held;
    // The washer is put away (the case or the clipboard is out).
    internal bool WasherHidden => washerHidden;

    // The case in the hand (for the multiplayer body), and the renderer of the
    // character's backpack it is made from.
    // What the case carries (equipment ids, -1 none) and what of it is in the
    // hand: 0 nothing, 1 the extension, 2 the soap nozzle.
    // What the case shows (an item in a hand is not on it).
    // (A prop taken in the washer hand without Physical Attachments is sent
    // as an item in that hand instead.)
    internal int ExtensionId => extension.Data is null || HeldKind == 1 ? -1 : extension.Data.UniqueId;
    internal int SoapId => soap.Data is null || HeldKind == 2 ? -1 : soap.Data.UniqueId;
    internal int HeldKind => prop == GameInput.Prop.Extension ? 1 : prop == GameInput.Prop.Soap ? 2 : 0;
    internal Transform? HeldProp => HeldKind == 0 ? null : (HeldKind == 1 ? extension : soap).Holder;
    internal static readonly Vector3 ExtensionLocal = new(0.24f, 0f, 0f), SoapLocal = new(-0.24f, 0f, 0f);
    internal static readonly Quaternion PropLocalRotation = Quaternion.Euler(-90f, 0f, 0f);

    internal Transform? HeldCase => (held || placed) && rootTransform is not null && rootTransform != null && root is not null && root.activeSelf ? rootTransform : null;

    internal void Update(PlayerRig rig, GameState state, Controllers controllers, GameInput input,
        Haptics haptics, Hands hands, Settings settings)
    {
        current = this;
        lastRig = rig;
        WasherGrace(rig);
        var playing = state.Mode is UiMode.Gameplay or UiMode.Radial;
        // The clipboard is out: the washer is put away and that hand is a hand.
        var active = settings.Backpack && playing && rig.IsPlayer && rig.HasPose && !Clipboard.Held;
        if (!active)
        {
            // The washer is put away while the task list hangs from the wrist
            // too, and that hand is a hand.
            var listOpen = Clipboard.Held && rig.IsPlayer;
            // Pausing (or anything that leaves play) ends the put-down mode.
            if (placed && !Clipboard.Held)
                Pick(input, hands, rig, false);
            Release(input, hands, rig, listOpen);
            hands.WasherFree = listOpen;
            // The hand holding the clipboard is not drawn (it would go
            // through the board).
            hands.WasherHidden = listOpen;
            atShoulder = false;
            Props.SetActive(root, false);
            return;
        }

        EnsureRoot();
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 2f;
            RefreshModel(state);
            RefreshProps(state, settings.PhysicalAttachments);
        }

        ref var free = ref settings.LeftHanded ? ref controllers.Right : ref controllers.Left;
        ref var washer = ref settings.LeftHanded ? ref controllers.Left : ref controllers.Right;
        var freeIsRight = settings.LeftHanded;
        var toWorld = rig.TrackingToWorldRotation;

        var wasAtShoulder = atShoulder;
        var shoulder = Shoulder(rig, settings);
        // Also while the case is put down: reaching back takes it into the hand.
        atShoulder = !held && state.Mode == UiMode.Gameplay && free.Tracked
            && !PhysicalWasher.HandBusy[settings.LeftHanded ? 1 : 0]
            && (free.GripPosition - shoulder).magnitude < (wasAtShoulder ? ShoulderReach * 1.3f : ShoulderReach);
        if (atShoulder && !wasAtShoulder)
            haptics.Pulse(freeIsRight, 0.3f, 0.03f);

        nearPlaced = false;
        if (atShoulder && input.FreeGrip.Pressed)
        {
            if (placed)
                Pick(input, hands, rig, true);
            held = true;
            atShoulder = false;
            haptics.Pulse(freeIsRight, 0.6f, 0.05f);
        }
        else if (held && !input.FreeGrip.Down)
        {
            // Physical Attachments: let go away from the shoulder and the case
            // stays there, in the room (moving with your play space).
            var atBack = (free.GripPosition - shoulder).magnitude < ShoulderReach * 1.5f;
            if (settings.PhysicalAttachments && !atBack && free.Tracked)
            {
                Release(input, hands, rig);
                Put(rig);
                haptics.Pulse(freeIsRight, 0.4f, 0.04f);
            }
            else
            {
                Release(input, hands, rig);
                haptics.Pulse(freeIsRight, 0.25f, 0.03f);
            }
        }

        if (placed && !settings.PhysicalAttachments)
            Pick(input, hands, rig, false);

        Props.SetActive(root, held || placed);
        models.Tick();
        Grow();
        UpdateDiscards();
        if (held)
        {
            // With Physical Washer the washer stays out (and in its hand).
            // With Physical Attachments the washer stays in its hand too.
            if (!PhysicalWasher.Active && !settings.PhysicalAttachments)
                HideWasher(rig, true);
            else if (washerHidden)
                HideWasher(rig, false);
            Place(rig, settings, controllers);

            if (!settings.PhysicalAttachments)
            {
                // The washer hand takes a prop off the case.
                var hand = rig.TrackingToWorldPosition + toWorld * washer.GripPosition;
                if (prop == GameInput.Prop.None && input.WasherGrip.Pressed)
                {
                    foreach (var slot in BothSlots)
                    {
                        if (slot.Data is null || slot.Holder is null)
                            continue;
                        if ((slot.Holder.position - hand).magnitude < PropReach)
                        {
                            prop = slot.Kind;
                            haptics.Pulse(!freeIsRight, 0.5f, 0.04f);
                            break;
                        }
                    }
                }
                else if (prop != GameInput.Prop.None && !input.WasherGrip.Down)
                {
                    Return(prop);
                    prop = GameInput.Prop.None;
                    haptics.Pulse(!freeIsRight, 0.25f, 0.03f);
                }

                if (prop != GameInput.Prop.None)
                    HoldProp(rig, controllers, settings);
            }
        }
        else if (placed)
        {
            if (washerHidden)
                HideWasher(rig, false);
            PlacePut(rig);
        }
        else if (washerHidden)
        {
            // Back from the clipboard (or anything else that put the washer
            // away while the case was not out): the washer comes back.
            HideWasher(rig, false);
        }

        if (settings.PhysicalAttachments || inHand[0] is not null || inHand[1] is not null)
            Items(rig, state, input, haptics, settings, controllers, settings.PhysicalAttachments && (held || placed), held);

        var freeItem = inHand[freeIndex] is not null;
        var washerItem = inHand[1 - freeIndex] is not null;
        input.CaseHeld = held;
        // With Physical Attachments an item never opens a wheel.
        input.PropHeld = held && !settings.PhysicalAttachments ? prop : GameInput.Prop.None;
        input.WasherHandBusy = held;
        if (freeItem)
            input.FreeHandBusy = true;
        hands.FreeHidden = held || freeItem;
        hands.WasherFree = held && !PhysicalWasher.Active && !settings.PhysicalAttachments;
        hands.WasherHidden = (held && prop != GameInput.Prop.None && !settings.PhysicalAttachments) || washerItem;
    }

    // ---- Physical Attachments: the case put down in the room ----
    // It keeps its place in your play space, so walking with the stick takes
    // it along and walking in the room does not. The free hand takes the
    // extension or the soap nozzle off it and fits it to the washer (the blue
    // guide shows where); letting one go anywhere else drops it, and the next
    // one grows in its place a moment later - that is how you go through
    // them. Taking the case and putting it back over the shoulder (or
    // pausing) ends it.
    private bool placed;
    private bool nearPlaced;
    private Vector3 placedPosition;
    private Quaternion placedRotation = Quaternion.identity;
    private const float CaseReach = 0.175f;
    // The placed case's grab point sits a little above its middle.
    private const float CaseGripRaise = 0.06f;
    private const float AttachReach = 0.1f;

    // The two long extensions are taken from a little further away (25 %).
    private static float Reach(PropSlot slot)
    {
        var name = slot.Data?.name ?? "";
        var long_ = slot.Kind == GameInput.Prop.Extension
            && (name.Contains("Long", StringComparison.OrdinalIgnoreCase) || name.Contains("Extra", StringComparison.OrdinalIgnoreCase));
        return long_ ? PropReach * 1.25f : PropReach;
    }
    private const float GrowDelay = 0.12f, GrowTime = 0.25f;
    private const float DiscardLife = 1.5f;
    private GameObject? line;
    private Transform? lineTransform;
    internal Belt? Belt;

    internal bool Placed => placed;

    private void Put(PlayerRig rig)
    {
        if (rootTransform is null)
            return;
        var inverse = Quaternion.Inverse(rig.TrackingToWorldRotation);
        placedPosition = inverse * (rootTransform.position - rig.TrackingToWorldPosition);
        placedRotation = inverse * rootTransform.rotation;
        placed = true;
        if (putLogs++ < 10)
            log.Msg("backpack: put down");
    }

    private int putLogs;

    // Ends the put-down mode: back over the shoulder (or carried again).
    private void Pick(GameInput input, Hands? hands, PlayerRig rig, bool carry)
    {
        if (prop != GameInput.Prop.None)
            Return(prop);
        prop = GameInput.Prop.None;
        if (placed && !carry && !washerHidden)
            thirdGraceUntil = Time.unscaledTime + ThirdPersonGrace;
        placed = false;
        Props.SetActive(line, false);
        if (hands is not null)
            hands.FreeHidden = false;
        if (carry)
            held = true;
        if (putLogs++ < 10)
            log.Msg(carry ? "backpack: picked up" : "backpack: back on the shoulder");
    }

    private void PlacePut(PlayerRig rig)
    {
        if (rootTransform is null)
            return;
        var toWorld = rig.TrackingToWorldRotation;
        rootTransform.SetPositionAndRotation(rig.TrackingToWorldPosition + toWorld * placedPosition, toWorld * placedRotation);
    }

    // Physical Attachments: items off the case, one per hand, both hands at
    // once. An item is taken with a hand that is empty: not the hand
    // carrying the case, and the washer hand only while it holds no washer
    // (Physical Washer let go of it, or the washer is put away while the case
    // is carried). Once taken it stays in that hand - the case can go back on
    // the shoulder meanwhile - until it is fitted to the washer (the next one
    // in line appears on the case at once) or dropped / thrown (the next one
    // grows in a moment later). It does not go back onto the case.
    private sealed class HeldItem
    {
        internal Transform? Holder;
        internal BaseEquipmentData? Data;
        internal GameInput.Prop Kind;
    }

    private readonly HeldItem?[] inHand = new HeldItem?[2];
    private readonly Vector3[] lastHand = new Vector3[2];
    private readonly Vector3[] handVelocity = new Vector3[2];
    private readonly bool[] lastKnown = new bool[2];
    private readonly GameObject?[] guides = new GameObject?[2];
    private int freeIndex;

    private void Items(PlayerRig rig, GameState state, GameInput input, Haptics haptics, Settings settings,
        Controllers controllers, bool canTake, bool carrying)
    {
        var deltaTime = Time.unscaledDeltaTime;
        TrackHand(0, controllers.Left, deltaTime);
        TrackHand(1, controllers.Right, deltaTime);
        var toWorld = rig.TrackingToWorldRotation;
        freeIndex = settings.LeftHanded ? 1 : 0;

        for (var index = 0; index < 2; index++)
        {
            var left = index == 0;
            var isFree = index == freeIndex;
            var grip = isFree ? input.FreeGrip : input.WasherGrip;
            ref var hand = ref left ? ref controllers.Left : ref controllers.Right;
            var item = inHand[index];
            if (item is not null)
            {
                if (item.Holder is null || item.Holder == null)
                {
                    inHand[index] = null;
                    Guide(index, null, null);
                    continue;
                }

                HoldAt(rig, item.Holder, hand);
                // Fitted only to a washer you can see.
                var tip = washerHidden ? null : NozzleTip(rig);
                Guide(index, item.Holder.position, tip);
                if (tip.HasValue && (item.Holder.position - tip.Value).magnitude < AttachReach)
                {
                    Attach(state, item.Data, item.Kind, haptics);
                    UnityEngine.Object.Destroy(item.Holder.gameObject);
                    inHand[index] = null;
                    Guide(index, null, null);
                    continue;
                }

                if (!grip.Down)
                {
                    var velocity = toWorld * handVelocity[index];
                    if (velocity.sqrMagnitude > 15f * 15f)
                        velocity = velocity.normalized * 15f;
                    inHand[index] = null;
                    Discard(item.Holder, item.Data?.UniqueId ?? -1, velocity);
                    Guide(index, null, null);
                    haptics.Pulse(!left, 0.25f, 0.03f);
                }

                continue;
            }

            Guide(index, null, null);
            if (!canTake || rootTransform is null || !hand.Tracked)
                continue;
            if (carrying && isFree)
                continue;
            // The washer hand takes nothing while it holds the washer (with
            // Physical Washer it may once the washer is away).
            if (!isFree && !PhysicalWasher.Away)
                continue;

            var world = rig.TrackingToWorldPosition + toWorld * hand.GripPosition;
            PropSlot? nearest = null;
            var best = float.MaxValue;
            foreach (var slot in BothSlots)
            {
                if (slot.Data is null || slot.Holder is null || slot.Holder.localScale.x < 0.5f)
                    continue;
                var distance = (slot.Holder.position - world).magnitude;
                if (distance < Reach(slot) && distance < best)
                {
                    best = distance;
                    nearest = slot;
                }
            }

            var nearCase = placed && isFree && (rootTransform.position + rootTransform.up * CaseGripRaise - world).magnitude < CaseReach;
            if (isFree && (nearest is not null || nearCase))
                nearPlaced = true;
            if (!grip.Pressed || PhysicalWasher.HandBusy[index])
                continue;
            if (nearest is not null)
            {
                // The item comes off as its own object; the next one in line
                // starts growing in its place straight away.
                var holder = nearest.Holder!;
                holder.SetParent(null, true);
                UnityEngine.Object.DontDestroyOnLoad(holder.gameObject);
                inHand[index] = new HeldItem { Holder = holder, Data = nearest.Data, Kind = nearest.Kind };
                nearest.Holder = null;
                Next(state, nearest);
                Show(nearest);
                nearest.GrowAt = Time.unscaledTime;
                if (nearest.Holder is not null)
                    nearest.Holder.localScale = Vector3.zero;
                haptics.Pulse(!left, 0.5f, 0.04f);
            }
            else if (nearCase)
            {
                Pick(input, null, rig, true);
                haptics.Pulse(!left, 0.6f, 0.05f);
            }
        }
    }


    // Items in the hands (for the multiplayer body): equipment id and object.
    internal void HeldItems(List<(int Id, Transform Item)> into)
    {
        into.Clear();
        foreach (var item in inHand)
        {
            if (item?.Holder is { } holder && holder != null && item.Data is not null)
                into.Add((item.Data.UniqueId, holder));
        }

        // Without Physical Attachments: the prop taken off the case.
        if (HeldKind != 0 && (HeldKind == 1 ? extension : soap) is { Data: { } data, Holder: { } propHolder }
            && propHolder != null && into.Count < 2)
            into.Add((data.UniqueId, propHolder));
    }

    private void TrackHand(int index, in HandInput hand, float deltaTime)
    {
        if (lastKnown[index] && deltaTime > 1e-4f)
            handVelocity[index] = Vector3.Lerp(handVelocity[index], (hand.GripPosition - lastHand[index]) / deltaTime, 0.5f);
        lastHand[index] = hand.GripPosition;
        lastKnown[index] = hand.Tracked;
    }

    private static void HoldAt(PlayerRig rig, Transform holder, in HandInput hand)
    {
        if (holder is null || holder == null)
            return;
        var toWorld = rig.TrackingToWorldRotation;
        var rotation = toWorld * hand.GripRotation;
        holder.SetPositionAndRotation(rig.TrackingToWorldPosition + toWorld * hand.GripPosition + rotation * new Vector3(0f, 0f, 0.04f), rotation);
    }

    // The blue guide from an item in a hand to the end of the washer.
    private void Guide(int index, Vector3? from, Vector3? to)
    {
        var guide = guides[index];
        if (!from.HasValue || !to.HasValue || Props.HideHints)
        {
            Props.SetActive(guide, false);
            return;
        }

        if (guide is null || guide == null)
        {
            guide = Props.Primitive(PrimitiveType.Cube, "PowerWashVR Attachment Guide", null,
                Props.Transparent(new Color(0.3f, 0.65f, 1f, 0.35f)));
            UnityEngine.Object.DontDestroyOnLoad(guide);
            guides[index] = guide;
        }

        Props.SetActive(guide, true);
        var span = to.Value - from.Value;
        var length = span.magnitude;
        if (length < 1e-3f)
            return;
        guide.transform.SetPositionAndRotation(from.Value + span * 0.5f, Quaternion.LookRotation(span / length));
        guide.transform.localScale = new Vector3(0.004f, 0.004f, length);
    }

    // Fitted to the washer: the game's own extension / nozzle change.
    private void Attach(GameState state, BaseEquipmentData? data, GameInput.Prop kind, Haptics haptics)
    {
        var configuration = state.Configuration;
        if (configuration is null || data is null)
            return;
        try
        {
            if (kind == GameInput.Prop.Extension && data.TryCast<ExtensionData>() is { } extensionData)
                configuration.UpdateExtension(extensionData);
            else if (data.TryCast<NozzleData>() is { } nozzleData)
                configuration.UpdateNozzle(nozzleData);
            haptics.Pulse(true, 0.8f, 0.06f);
            haptics.Pulse(false, 0.8f, 0.06f);
            log.Msg($"backpack: fitted \"{data.name}\"");
        }
        catch (Exception exception)
        {
            log.Warning($"backpack: fitting failed ({exception.GetType().Name}: {exception.Message})");
        }
    }

    // ---- going through the items: drop one, the next grows in ----
    private int discardLogs;

    private sealed class Discarded
    {
        internal GameObject? Object;
        internal int Id;
        internal float Until;
    }

    private readonly List<Discarded> discarded = new();

    // Dropped items still falling (for the multiplayer body): id and object.
    internal void Thrown(List<(int Id, Transform Token)> into)
    {
        foreach (var item in discarded)
        {
            if (item.Object is not null && item.Object != null && into.Count < 8)
                into.Add((item.Id, item.Object.transform));
        }
    }

    // A let-go item falls and bounces for a moment, then is gone.
    private void Discard(Transform holder, int id, Vector3 velocity)
    {
        var copy = holder.gameObject;
        holder.SetParent(null, true);
        copy.SetActive(true);
        var scale = Mathf.Max(0.01f, holder.lossyScale.x);
        // A capsule along the item's long side (+Z), as long as the model.
        var length = 0f;
        var renderers = copy.GetComponentsInChildren<Renderer>(true);
        for (var index = 0; renderers is not null && index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer is null || renderer == null)
                continue;
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
            length = Mathf.Max(length, renderer.bounds.size.magnitude);
        }

        Collider shape;
        if (length > 0.2f)
        {
            var capsule = copy.AddComponent<CapsuleCollider>();
            capsule.direction = 2;
            capsule.radius = 0.035f / scale;
            capsule.height = Mathf.Min(length, 1.5f) * 0.8f / scale;
            shape = capsule;
        }
        else
        {
            var sphere = copy.AddComponent<SphereCollider>();
            sphere.radius = 0.04f / scale;
            shape = sphere;
        }

        if (discardLogs++ < 20)
            log.Msg($"backpack: dropped \"{copy.name}\" ({renderers?.Length ?? 0} part(s), {length:0.00} m, scale {scale:0.00}, {velocity.magnitude:0.0} m/s)");
        var body = copy.AddComponent<Rigidbody>();
        body.mass = 0.4f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.linearVelocity = velocity;
        IgnorePlayer(shape);
        discarded.Add(new Discarded { Object = copy, Id = id, Until = Time.unscaledTime + DiscardLife });
    }

    private void UpdateDiscards()
    {
        for (var index = discarded.Count - 1; index >= 0; index--)
        {
            var item = discarded[index];
            if (item.Object is null || item.Object == null || Time.unscaledTime > item.Until)
            {
                if (discardLogs < 24)
                {
                    discardLogs++;
                    log.Msg(item.Object is null || item.Object == null
                        ? "backpack: a dropped item was destroyed early"
                        : $"backpack: dropped item gone at {item.Object.transform.position} (active {item.Object.activeInHierarchy})");
                }

                if (item.Object is not null && item.Object != null)
                    UnityEngine.Object.Destroy(item.Object);
                discarded.RemoveAt(index);
            }
        }
    }

    private void Grow()
    {
        foreach (var slot in BothSlots)
        {
            if (slot.Holder is null || slot.GrowAt <= 0f)
                continue;
            var t = Mathf.Clamp01((Time.unscaledTime - slot.GrowAt) / GrowTime);
            var eased = t * t * (3f - 2f * t);
            slot.Holder.localScale = Vector3.one * eased;
            if (t >= 1f)
                slot.GrowAt = 0f;
        }
    }

    // The next owned extension (or, for the soap slot, the next owned nozzle
    // that is not one of the belt's).
    private void Next(GameState state, PropSlot slot)
    {
        var options = Options(state, slot.Kind);
        if (options.Count == 0)
            return;
        var at = slot.Data is null ? -1 : options.FindIndex(item => item.UniqueId == slot.Data.UniqueId);
        slot.Data = options[(at + 1) % options.Count];
        if (putLogs++ < 30)
            log.Msg($"backpack: next {(slot.Kind == GameInput.Prop.Extension ? "extension" : "nozzle")} \"{slot.Data.name}\"");
    }

    private List<BaseEquipmentData> Options(GameState state, GameInput.Prop kind)
    {
        var result = new List<BaseEquipmentData>();
        var configuration = state.Configuration;
        var current = configuration?.CurrentConfiguration;
        if (configuration is null || current is null)
            return result;
        try
        {
            var belt = new HashSet<int>();
            Belt?.NozzleIds(belt);
            if (kind == GameInput.Prop.Extension)
            {
                var list = current.CompatibleExtensions;
                for (var index = 0; list is not null && index < list.Count; index++)
                {
                    var item = list[index];
                    if (item is not null && item != null && Owned(configuration, item))
                        result.Add(item);
                }
            }
            else
            {
                var list = current.CompatibleNozzles;
                for (var index = 0; list is not null && index < list.Count; index++)
                {
                    var item = list[index];
                    if (item is not null && item != null && Owned(configuration, item) && !belt.Contains(item.UniqueId))
                        result.Add(item);
                }
            }
        }
        catch
        {
            // Nothing to go through then.
        }

        return result;
    }

    private static bool Owned(ConfigurationManager configuration, BaseEquipmentData item)
    {
        try
        {
            return configuration.IsOwned(item);
        }
        catch
        {
            return true;
        }
    }

    private static Vector3? NozzleTip(PlayerRig rig)
    {
        if (WasherFixes.JetCount > 0)
            return WasherFixes.JetOrigin[0];
        if (rig.HasWasher)
            return rig.WasherPosition + rig.WasherRotation * new Vector3(0f, 0f, 0.6f);
        return null;
    }

    private void DrawLine(Vector3 from, Vector3? to)
    {
        if (!to.HasValue)
        {
            Props.SetActive(line, false);
            return;
        }

        if (line is null || line == null)
        {
            line = Props.Primitive(PrimitiveType.Cube, "PowerWashVR Attachment Guide", null,
                Props.Transparent(new Color(0.3f, 0.65f, 1f, 0.35f)));
            UnityEngine.Object.DontDestroyOnLoad(line);
            lineTransform = line.transform;
        }

        Props.SetActive(line, true);
        var span = to.Value - from;
        var length = span.magnitude;
        if (length < 1e-3f)
            return;
        lineTransform!.SetPositionAndRotation(from + span * 0.5f, Quaternion.LookRotation(span / length));
        lineTransform.localScale = new Vector3(0.004f, 0.004f, length);
    }

    private static void IgnorePlayer(Collider collider)
    {
        try
        {
            var own = PhysicalWasher.PlayerRoot?.GetComponentsInChildren<Collider>(true);
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

    private static Backpack? current;
    private PlayerRig? lastRig;

    // Before the eyes and the smooth camera draw: the put-away washer stays
    // hidden whatever the game swapped in since.
    internal static void BeforeCamera()
    {
        var self = current;
        if (self is null || self.lastRig is null)
            return;
        if (self.washerHidden)
            self.HideWasher(self.lastRig, true);
        else
            self.WasherGrace(self.lastRig);
    }

    internal void Reapply(PlayerRig rig, Settings settings, Controllers controllers)
    {
        // Just before drawing: anything the game swapped in since stays hidden.
        if (washerHidden)
            HideWasher(rig, true);
        else
            WasherGrace(rig);
        for (var index = 0; index < 2 && rig.HasPose; index++)
        {
            var item = inHand[index];
            if (item?.Holder is null || item.Holder == null)
                continue;
            HoldAt(rig, item.Holder, index == 0 ? controllers.Left : controllers.Right);
            Guide(index, item.Holder.position, washerHidden ? null : NozzleTip(rig));
        }

        if (placed && root is not null && root != null && rig.HasPose)
        {
            PlacePut(rig);
            return;
        }

        if (!held || root is null || root == null || !rig.HasPose)
            return;

        Place(rig, settings, controllers);
        if (prop != GameInput.Prop.None && !settings.PhysicalAttachments)
            HoldProp(rig, controllers, settings);
    }

    private void Release(GameInput input, Hands hands, PlayerRig rig, bool keepWasherHidden = false)
    {
        if (prop != GameInput.Prop.None)
            Return(prop);

        // The game rebuilds this player's third-person washer when the case
        // is let go; with the washer never hidden (physical modes) the grace
        // that keeps that copy out of the spectator camera starts here too.
        if (held && !washerHidden && !keepWasherHidden)
            thirdGraceUntil = Time.unscaledTime + ThirdPersonGrace;

        held = false;
        prop = GameInput.Prop.None;
        input.CaseHeld = false;
        input.PropHeld = GameInput.Prop.None;
        input.WasherHandBusy = false;
        hands.FreeHidden = false;
        hands.WasherFree = false;
        hands.WasherHidden = false;
        HideWasher(rig, keepWasherHidden);
    }

    // Up beside the ear, a little behind it.
    private Vector3 Shoulder(PlayerRig rig, Settings settings)
    {
        var side = settings.LeftHanded ? 1f : -1f;
        return rig.HeadPosition + body.Rotation * new Vector3(0.15f * side, -0.04f, -0.05f);
    }

    // Hanging from the free hand by its handle, turned toward the face.
    private void Place(PlayerRig rig, Settings settings, Controllers controllers)
    {
        ref var free = ref settings.LeftHanded ? ref controllers.Right : ref controllers.Left;
        var toWorld = rig.TrackingToWorldRotation;
        var grip = rig.TrackingToWorldPosition + toWorld * free.GripPosition;
        var head = rig.TrackingToWorldPosition + toWorld * rig.HeadPosition;
        var up = toWorld * Vector3.up;

        var position = grip - up * 0.12f;
        var toHead = head - position;
        toHead -= up * Vector3.Dot(toHead, up);
        var facing = toHead.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(-toHead.normalized, up) : toWorld;
        rootTransform!.SetPositionAndRotation(position, facing);
    }

    private void HoldProp(PlayerRig rig, Controllers controllers, Settings settings)
    {
        var slot = prop == GameInput.Prop.Extension ? extension : soap;
        if (slot.Holder is null)
            return;

        ref var washer = ref settings.LeftHanded ? ref controllers.Left : ref controllers.Right;
        var toWorld = rig.TrackingToWorldRotation;
        var rotation = toWorld * washer.GripRotation;
        slot.Holder.SetPositionAndRotation(rig.TrackingToWorldPosition + toWorld * washer.GripPosition + rotation * new Vector3(0f, 0f, 0.04f),
            rotation);
    }

    private void Return(GameInput.Prop kind)
    {
        var slot = kind == GameInput.Prop.Extension ? extension : soap;
        if (slot.Holder is null || slot.Holder == null)
            return;
        if (rootTransform is not null && rootTransform != null && slot.Holder.parent != rootTransform)
            slot.Holder.SetParent(rootTransform, false);
        slot.Holder.SetLocalPositionAndRotation(slot.Local, Quaternion.Euler(-90f, 0f, 0f));
    }

    // The washer is put away while the case is out: everything under the
    // hand's equipment anchor is kept from drawing (the game's own switches
    // are left alone). Taking something off the case makes the game rebuild
    // or swap the washer, so the anchor is looked over again the moment its
    // hierarchy changes, and the switch is held every frame.
    private int washerHierarchy = -1;
    private readonly Transform?[] washerRoots = new Transform?[2];

    // After the case is put away the hand's washer comes back at once, but
    // this player's own third-person washer (never meant for this player's
    // cameras) stays hidden a moment longer: the game rebuilds it then, and
    // the smooth camera caught the rebuilt one as a second washer.
    private const float ThirdPersonGrace = 1.2f;
    private float thirdGraceUntil;

    private void HideWasher(PlayerRig rig, bool hide)
    {
        if (!hide)
        {
            if (!washerHidden)
                return;
            washerHidden = false;
            RestoreWasher(thirdToo: false);
            thirdGraceUntil = Time.unscaledTime + ThirdPersonGrace;
            return;
        }

        if (!washerHidden)
            nextVisibleCheck = Time.unscaledTime + 0.5f;
        washerHidden = true;
        thirdGraceUntil = 0f;
        HoldWasher(rig, first: true);

        if (Time.unscaledTime >= nextVisibleCheck)
        {
            nextVisibleCheck = Time.unscaledTime + 1f;
            ReportVisible(rig);
        }
    }

    // Every frame and before each camera: the grace period after putting the
    // case away.
    private void WasherGrace(PlayerRig rig)
    {
        if (washerHidden || thirdGraceUntil <= 0f)
            return;
        if (Time.unscaledTime < thirdGraceUntil)
        {
            HoldWasher(rig, first: false);
            return;
        }

        thirdGraceUntil = 0f;
        RestoreWasher(thirdToo: true);
    }

    private void RestoreWasher(bool thirdToo)
    {
        for (var index = washerRenderers.Count - 1; index >= 0; index--)
        {
            var (renderer, third) = washerRenderers[index];
            if (third && !thirdToo)
                continue;
            if (renderer is not null && renderer != null)
                renderer.forceRenderingOff = false;
            washerRenderers.RemoveAt(index);
        }

        for (var index = washerParts.Count - 1; index >= 0; index--)
        {
            var (part, scale, third) = washerParts[index];
            if (third && !thirdToo)
                continue;
            if (part is not null && part != null)
                part.localScale = scale;
            washerParts.RemoveAt(index);
        }

        washerHierarchy = -1;
    }

    private void HoldWasher(PlayerRig rig, bool first)
    {
        // The hand's anchor (looked up again: the game can replace it when it
        // swaps equipment) and this player's own third-person washer.
        washerRoots[0] = null;
        washerRoots[1] = null;
        var cam = rig.CameraTransform;
        if (cam is not null && cam != null)
        {
            if (first)
                washerRoots[0] = cam.Find("EquipmentAnchor");
            washerRoots[1] = cam.root.Find("Third Person Visuals/EquipmentAnchor");
        }

        if (first && (washerRoots[0] is null || washerRoots[0] == null))
            washerRoots[0] = rig.Anchor is { } anchor && anchor != null ? anchor : rig.Assembly;

        var signature = first ? 17 : 19;
        foreach (var root in washerRoots)
            signature = signature * 31 + (root is not null && root != null ? root.hierarchyCount * 7 + (int)(root.Pointer.ToInt64() & 0xFFFF) : 0);
        if (signature != washerHierarchy)
        {
            washerHierarchy = signature;
            washerRenderers.RemoveAll(entry => entry.Renderer is null || entry.Renderer == null);
            washerParts.RemoveAll(entry => entry.Part is null || entry.Part == null);
            for (var slot = 0; slot < washerRoots.Length; slot++)
            {
                var root = washerRoots[slot];
                if (root is null || root == null)
                    continue;
                var third = slot == 1;
                // Each piece in the anchor is also shrunk to nothing: whatever
                // the game switches on there while the case is out stays unseen.
                for (var child = 0; child < root.childCount; child++)
                {
                    var part = root.GetChild(child);
                    if (part is null || part == null || washerParts.Any(entry => entry.Part!.Pointer == part.Pointer))
                        continue;
                    washerParts.Add((part, part.localScale, third));
                }

                var found = root.GetComponentsInChildren<Renderer>(true);
                for (var index = 0; found is not null && index < found.Length; index++)
                {
                    var renderer = found[index];
                    if (renderer is null || renderer == null || renderer.forceRenderingOff)
                        continue;
                    washerRenderers.Add((renderer, third));
                }
            }
        }

        for (var index = 0; index < washerRenderers.Count; index++)
        {
            var renderer = washerRenderers[index].Renderer;
            if (renderer is not null && renderer != null && !renderer.forceRenderingOff)
                renderer.forceRenderingOff = true;
        }

        for (var index = 0; index < washerParts.Count; index++)
        {
            var part = washerParts[index].Part;
            if (part is not null && part != null && part.localScale != Vector3.zero)
                part.localScale = Vector3.zero;
        }
    }

    private readonly List<(Transform? Part, Vector3 Scale, bool Third)> washerParts = new();
    private float nextVisibleCheck;
    private readonly HashSet<string> visibleLogged = new();

    // Diagnostics: anything still drawn near the head while the washer is away
    // (named once each).
    private void ReportVisible(PlayerRig rig)
    {
        if (visibleLogged.Count > 20)
            return;
        try
        {
            var cam = rig.CameraTransform;
            if (cam is null)
                return;
            var renderers = cam.root.GetComponentsInChildren<Renderer>(false);
            var head = cam.position;
            for (var index = 0; renderers is not null && index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                if (renderer is null || renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.isVisible)
                    continue;
                if (renderer.transform.IsChildOf(rootTransform!) || (renderer.bounds.center - head).magnitude > 1.2f)
                    continue;
                var path = renderer.name;
                for (var node = renderer.transform.parent; node is not null && node != null && node != cam.root; node = node.parent)
                    path = node.name + "/" + path;
                if (visibleLogged.Add(path))
                    log.Msg($"backpack: still drawn while the washer is away: \"{path}\" ({renderer.GetIl2CppType().Name}, layer {renderer.gameObject.layer})");
            }
        }
        catch
        {
            // Diagnostics only.
        }
    }

    // The short extension and the soap nozzle the current washer takes.
    private void RefreshProps(GameState state, bool cycling)
    {
        var configuration = state.Configuration;
        var current = configuration?.CurrentConfiguration;
        if (configuration is null || current is null)
            return;

        try
        {
            // A choice made by going through them is kept while the washer
            // still takes it.
            // Without Physical Attachments nothing cycles: the first in line.
            if (!cycling || !Keeps(configuration, current.CompatibleExtensions, extension.Data))
                extension.Data = Pick(configuration, current.CompatibleExtensions, "SHORT");
            if (!cycling || !Keeps(configuration, current.CompatibleNozzles, soap.Data))
                soap.Data = Pick(configuration, current.CompatibleNozzles, "SOAP", required: true);
        }
        catch
        {
            // Keeps whatever was found before.
        }

        foreach (var slot in BothSlots)
            Show(slot);

        // Everything the slots can go through, loaded ahead.
        foreach (var kind in new[] { GameInput.Prop.Extension, GameInput.Prop.Soap })
        {
            foreach (var item in Options(state, kind))
                models.Preload(item);
        }
    }

    private static bool Keeps<T>(ConfigurationManager configuration, Il2CppSystem.Collections.Generic.List<T>? list, BaseEquipmentData? chosen)
        where T : BaseEquipmentData
    {
        if (list is null || chosen is null)
            return false;
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] is { } item && item != null && item.UniqueId == chosen.UniqueId)
                return true;
        }

        return false;
    }

    private static BaseEquipmentData? Pick<T>(ConfigurationManager configuration,
        Il2CppSystem.Collections.Generic.List<T>? list, string word, bool required = false) where T : BaseEquipmentData
    {
        if (list is null)
            return null;

        BaseEquipmentData? first = null;
        for (var index = 0; index < list.Count; index++)
        {
            var item = list[index];
            if (item is null || item == null)
                continue;

            bool owned;
            try
            {
                owned = configuration.IsOwned(item);
            }
            catch
            {
                owned = true;
            }

            if (!owned)
                continue;

            string name;
            try
            {
                name = (item.UniqueName ?? "") + " " + item.name;
            }
            catch
            {
                name = item.name;
            }

            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
                return item;
            first ??= item;
        }

        return required ? null : first;
    }

    private void Show(PropSlot slot)
    {
        if (rootTransform is null)
            return;

        if (slot.Holder is null || slot.Holder == null)
        {
            slot.Holder = new GameObject($"Prop {slot.Kind}").transform;
            slot.Holder.SetParent(rootTransform, false);
            slot.ShownId = -1;
            Return(slot.Kind);
        }

        var id = slot.Data is null ? -1 : slot.Data.UniqueId;
        if (id != slot.ShownId)
        {
            slot.ShownId = id;
            slot.HasModel = false;
            for (var child = slot.Holder.childCount - 1; child >= 0; child--)
                UnityEngine.Object.Destroy(slot.Holder.GetChild(child).gameObject);

            if (slot.Data is not null)
            {
            }
        }

        if (slot.Data is not null && !slot.HasModel)
        {
            var template = models.Get(slot.Data);
            if (template is not null)
            {
                for (var child = slot.Holder.childCount - 1; child >= 0; child--)
                    UnityEngine.Object.Destroy(slot.Holder.GetChild(child).gameObject);
                var copy = UnityEngine.Object.Instantiate(template, slot.Holder, false);
                copy.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.SetActive(true);
                slot.HasModel = true;
            }
        }
    }

    private void EnsureRoot()
    {
        if (root is not null && root != null)
            return;

        root = new GameObject("PowerWashVR Washer Case");
        UnityEngine.Object.DontDestroyOnLoad(root);
        rootTransform = root.transform;
        root.SetActive(false);
    }

    private MeshRenderer? caseRenderer;

    // The outfit's colours are on the backpack renderer (property blocks and
    // its current materials), not in the shared mesh: copied across.
    private void CopyColours(GameState state)
    {
        if (caseRenderer is null || caseRenderer == null)
            return;
        try
        {
            var source = state.Character?.ThirdPersonVisuals?.m_backpackOutfitRenderer;
            if (source is null || source == null)
                return;
            CopyLook(source, caseRenderer);
        }
        catch
        {
            // Next refresh.
        }
    }

    internal static void CopyLook(Renderer source, Renderer target)
    {
        target.sharedMaterials = source.sharedMaterials;
        var block = new MaterialPropertyBlock();
        source.GetPropertyBlock(block);
        target.SetPropertyBlock(block);
        var count = source.sharedMaterials?.Length ?? 0;
        for (var index = 0; index < count; index++)
        {
            if (!source.HasPropertyBlock())
                break;
            var slot = new MaterialPropertyBlock();
            source.GetPropertyBlock(slot, index);
            if (!slot.isEmpty)
                target.SetPropertyBlock(slot, index);
        }
    }

    // The character's backpack cosmetic, as a static mesh; a plain case if
    // there is none.
    private void RefreshModel(GameState state)
    {
        Mesh? mesh = null;
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>? materials = null;

        try
        {
            var visuals = state.Character?.ThirdPersonVisuals;
            var renderer = visuals is null ? null : visuals.m_backpackOutfitRenderer;
            if (renderer is not null && renderer != null)
            {
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                if (skinned is not null)
                    mesh = skinned.sharedMesh;
                else
                    mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                materials = renderer.sharedMaterials;
            }
        }
        catch
        {
            mesh = null;
        }

        var pointer = mesh is null || mesh == null ? new IntPtr(-1) : mesh.Pointer;
        if (pointer == modelMesh && model is not null && model != null)
        {
            CopyColours(state);
            return;
        }

        modelMesh = pointer;
        if (model is not null && model != null)
            UnityEngine.Object.Destroy(model);

        var size = mesh is null || mesh == null ? 0f : mesh.bounds.size.magnitude;
        if (mesh is not null && mesh != null && materials is not null && size > 0.05f && size < 1.2f)
        {
            model = new GameObject("Case");
            model.transform.SetParent(rootTransform, false);
            model.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = model.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            caseRenderer = renderer;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Centred on the shoulder, whatever the mesh's own origin.
            var bounds = mesh.bounds;
            model.transform.localScale = Vector3.one * CaseScale;
            model.transform.localPosition = -bounds.center * CaseScale;
            CopyColours(state);
            log.Msg($"backpack: using the character's backpack \"{mesh.name}\" ({bounds.size.x:0.00}x{bounds.size.y:0.00}x{bounds.size.z:0.00})");
        }
        else
        {
            model = Props.Primitive(PrimitiveType.Cube, "Case", rootTransform, Props.Lit(new Color(0.75f, 0.45f, 0.1f)));
            model.transform.localScale = new Vector3(0.3f, 0.22f, 0.1f);
        }
    }
}
