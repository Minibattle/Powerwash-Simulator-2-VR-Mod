using Il2CppFuturLab.PW2;
using MelonLoader;
using PowerWashVR.Input;
using UnityEngine;

namespace PowerWashVR.Game;

// A belt of nozzles at the waist. Reach down with the free hand, squeeze the
// grip on a nozzle to take it, and touch it to the end of the washer to fit
// it. Letting go anywhere else puts it back.
//
// The belt carries every nozzle the current washer accepts that you own; the
// one on the washer is shown as an empty slot. Fitting goes through the
// game's own ConfigurationManager.UpdateNozzle, the same call the equipment
// wheel makes.
internal sealed class Belt
{
    private const float GrabReach = 0.1f;
    private const float AttachReach = 0.1f;
    // With the strap the nozzles sit on it at the waist; without it they
    // float a little further out, as before the strap existed.
    private float SlotRadius => strapShown ? 0.24f : 0.3f;
    private bool strapShown = true;
    private bool namesShown = true;
    private const float ArcDegrees = 130f;

    private readonly MelonLogger.Instance log;
    private GameObject? root;
    private Transform? rootTransform;
    private readonly List<Slot> slots = new();
    private float nextRefresh;
    private string signature = "";
    // Per hand (0 left, 1 right): the slot held and the slot lit.
    private readonly int[] held = { -1, -1 };
    private readonly int[] hover = { -1, -1 };

    private sealed class Slot
    {
        internal NozzleData Data = null!;
        internal int Id;
        internal GameObject Token = null!;
        internal Transform TokenTransform = null!;
        internal Props.Card Label = null!;
        internal Vector3 HomeLocal;
        internal bool Equipped;
        internal Material Material = null!;
        internal Color Color;
        internal bool HasModel;
        internal bool Thrown;
        internal float ThrownAt;
        internal Rigidbody? Body;
        internal Collider? Collider;
    }

    // A released nozzle (thrown, or let go away from the belt) falls and
    // bounces like an object and comes back to its place on the belt a few
    // seconds later.
    private const float ReturnAfter = 3f;
    private readonly Vector3[] lastHand = new Vector3[2];
    private readonly Vector3[] handVelocity = new Vector3[2];
    private readonly bool[] lastHandKnown = new bool[2];
    private PlayerRig? rigRef;

    private void Throw(Slot slot, Vector3 velocity)
    {
        try
        {
            var token = slot.TokenTransform;
            var position = token.position;
            var rotation = token.rotation;
            token.SetParent(null, true);
            token.SetPositionAndRotation(position, rotation);
            UnityEngine.Object.DontDestroyOnLoad(token.gameObject);
            var sphere = token.gameObject.AddComponent<SphereCollider>();
            sphere.radius = 0.035f / Mathf.Max(0.01f, token.lossyScale.x);
            slot.Collider = sphere;
            var body = token.gameObject.AddComponent<Rigidbody>();
            body.mass = 0.3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.linearVelocity = velocity;
            slot.Body = body;
            IgnorePlayer(sphere);
            slot.Thrown = true;
            slot.ThrownAt = Time.unscaledTime;
        }
        catch (Exception exception)
        {
            log.Msg($"belt: nozzle not thrown ({exception.GetType().Name}); back on the belt");
            Home(slot);
        }
    }

    // The player's own colliders would catch a nozzle let go inside them.
    private void IgnorePlayer(Collider collider)
    {
        try
        {
            var character = rigRef?.Controller;
            if (character is null || character == null)
                return;
            var own = character.transform.root.GetComponentsInChildren<Collider>(true);
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

    private void Home(Slot slot)
    {
        if (slot.Body is not null && slot.Body != null)
            UnityEngine.Object.Destroy(slot.Body);
        if (slot.Collider is not null && slot.Collider != null)
            UnityEngine.Object.Destroy(slot.Collider);
        slot.Body = null;
        slot.Collider = null;
        slot.Thrown = false;
        if (rootTransform is not null && rootTransform != null)
        {
            slot.TokenTransform.SetParent(rootTransform, false);
            slot.TokenTransform.SetLocalPositionAndRotation(slot.HomeLocal, HomeRotation);
        }
    }

    private void ReturnThrown()
    {
        var now = Time.unscaledTime;
        foreach (var slot in slots)
        {
            if (!slot.Thrown || slot.TokenTransform is null || slot.TokenTransform == null)
                continue;
            var far = rootTransform is not null && rootTransform != null
                && (slot.TokenTransform.position - rootTransform.position).sqrMagnitude > 60f * 60f;
            if (now - slot.ThrownAt > ReturnAfter || far || slot.TokenTransform.position.y < -500f)
                Home(slot);
        }
    }

    private readonly NozzleModels models;
    private readonly GameObject?[] lines = new GameObject?[2];
    private static readonly Quaternion HomeRotation = Quaternion.Euler(-90f, 0f, 0f);

    private readonly BodyFrame body;

    internal Belt(MelonLogger.Instance log, BodyFrame body)
    {
        this.log = log;
        this.body = body;
        models = new NozzleModels(log);
    }

    internal bool Holding => held[0] >= 0 || held[1] >= 0;

    // The nozzles hanging on the belt.
    internal void NozzleIds(HashSet<int> into)
    {
        foreach (var slot in slots)
        {
            if (slot.Data is not null && slot.Data != null)
                into.Add(slot.Data.UniqueId);
        }
    }

    // Nozzles thrown or dropped and not back yet (for the multiplayer body).
    internal void Thrown(List<(int Id, Transform Token)> into)
    {
        into.Clear();
        foreach (var slot in slots)
        {
            if (slot.Thrown && slot.TokenTransform is not null && slot.TokenTransform != null && into.Count < 6)
                into.Add((slot.Data.UniqueId, slot.TokenTransform));
        }
    }

    // The nozzle in the free hand (for the multiplayer body): its equipment id
    // and the token it is drawn in.
    // (The free hand's first; a second nozzle in the other hand is sent as an
    // item in that hand: SecondId/SecondToken.)
    internal int HeldId => SlotAt(First()) is { } slot ? slot.Data.UniqueId : -1;
    internal Transform? HeldToken => SlotAt(First())?.TokenTransform;
    internal int SecondId => SlotAt(Second()) is { } slot ? slot.Data.UniqueId : -1;
    internal Transform? SecondToken => SlotAt(Second())?.TokenTransform;
    private int freeIndex;

    private int First() => held[freeIndex] >= 0 ? held[freeIndex] : held[1 - freeIndex];
    private int Second() => held[freeIndex] >= 0 ? held[1 - freeIndex] : -1;
    private Slot? SlotAt(int index) => index >= 0 && index < slots.Count ? slots[index] : null;

    // Set by the mod: the backpack is carried or put down.
    internal static bool CaseOut;

    // The free grip belongs to the belt while the hand is at a nozzle.
    internal bool Claims => hover[freeIndex] >= 0 || held[freeIndex] >= 0;

    // Physical Washer: a nozzle in the washer hand (that hand is hidden).
    internal bool HeldByWasherHand { get; private set; }

    internal bool HoldsLeft => held[0] >= 0;
    internal bool HoldsRight => held[1] >= 0;

    internal void Update(PlayerRig rig, GameState state, Controllers controllers, GameInput input,
        Hands hands, Haptics haptics, Settings settings, float deltaTime)
    {
        // Shown while the backpack is out too (its washer wheel is a radial).
        var active = settings.Belt && (state.Mode == UiMode.Gameplay || (state.Mode == UiMode.Radial && CaseOut)) && rig.IsPlayer && rig.HasPose;
        if (!active)
        {
            Drop();
            foreach (var slot in slots)
            {
                if (slot.Thrown && slot.TokenTransform is not null && slot.TokenTransform != null)
                    Home(slot);
            }
            Props.SetActive(root, false);
            Props.SetActive(lines[0], false);
            Props.SetActive(lines[1], false);
            input.FreeHandBusy = false;
            hands.FreeHolding = false;
            HeldByWasherHand = false;
            return;
        }

        // Belt and Nozzle Names (VR SETTINGS).
        if (settings.BeltStrap != strapShown)
        {
            strapShown = settings.BeltStrap;
            signature = "";      // the nozzles move: rebuilt below
            nextRefresh = 0f;
            if (ring is not null && ring != null)
                UnityEngine.Object.Destroy(ring);
            ring = null;
        }

        if (settings.NozzleNames != namesShown)
        {
            namesShown = settings.NozzleNames;
            foreach (var slot in slots)
                Props.SetActive(slot.Label.Root, namesShown);
        }

        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 1f;
            Refresh(state);
            UpgradeModels();
        }

        if (slots.Count == 0)
        {
            Props.SetActive(root, false);
            return;
        }

        Props.SetActive(root, true);
        Place(rig, settings);

        // The free hand takes nozzles; with Physical Washer the washer hand
        // too while the washer is away, one in each hand. A hand keeps what
        // it holds even if its role changes.
        freeIndex = settings.LeftHanded ? 1 : 0;
        rigRef = rig;
        ReturnThrown();
        var toWorld = rig.TrackingToWorldRotation;
        var tip = NozzleTip(rig);
        var hoverChanged = false;

        for (var index = 0; index < 2; index++)
        {
            var left = index == 0;
            var isFree = index == freeIndex;
            ref var hand = ref left ? ref controllers.Left : ref controllers.Right;
            var grip = isFree ? input.FreeGrip : input.WasherGrip;

            // The hand's speed in the play space (so walking doesn't throw harder).
            if (lastHandKnown[index] && deltaTime > 1e-4f)
                handVelocity[index] = Vector3.Lerp(handVelocity[index], (hand.GripPosition - lastHand[index]) / deltaTime, 0.5f);
            lastHand[index] = hand.GripPosition;
            lastHandKnown[index] = hand.Tracked;

            var mayUse = isFree || PhysicalWasher.Away;
            var handPosition = rig.TrackingToWorldPosition + toWorld * hand.GripPosition;
            var handRotation = toWorld * hand.GripRotation;

            // Hover: the nearest slot within reach lights up.
            var nearest = -1;
            var nearestDistance = GrabReach;
            if (held[index] < 0 && mayUse && hand.Tracked)
            {
                for (var at = 0; at < slots.Count; at++)
                {
                    var slot = slots[at];
                    if (slot.Equipped || slot.Thrown || at == held[1 - index])
                        continue;

                    var distance = (slot.TokenTransform.position - handPosition).magnitude;
                    if (distance < nearestDistance)
                    {
                        nearest = at;
                        nearestDistance = distance;
                    }
                }
            }

            if (nearest != hover[index])
            {
                if (nearest >= 0)
                    haptics.Pulse(!left, 0.15f, 0.02f);
                hover[index] = nearest;
                hoverChanged = true;
            }

            if (grip.Pressed && nearest >= 0 && !PhysicalWasher.HandBusy[index])
            {
                held[index] = nearest;
                hover[index] = -1;
                hoverChanged = true;
                haptics.Pulse(!left, 0.5f, 0.04f);
            }

            if (held[index] < 0)
            {
                Props.SetActive(lines[index], false);
                continue;
            }

            var heldSlot = slots[held[index]];
            Hold(heldSlot, hands, handPosition, handRotation);
            DrawLine(index, heldSlot.TokenTransform.position, tip);
            if (tip.HasValue && (heldSlot.TokenTransform.position - tip.Value).magnitude < AttachReach)
            {
                Fit(state, heldSlot, haptics, settings);
                Drop(index);
            }
            else if (!grip.Down)
            {
                // Let go: back on the belt when let go at the belt, otherwise
                // thrown with the hand's speed.
                var home = rootTransform!.TransformPoint(heldSlot.HomeLocal);
                if ((heldSlot.TokenTransform.position - home).magnitude < GrabReach * 1.5f)
                {
                    Drop(index);
                }
                else
                {
                    held[index] = -1;
                    var velocity = toWorld * handVelocity[index];
                    if (velocity.sqrMagnitude > 15f * 15f)
                        velocity = velocity.normalized * 15f;
                    Throw(heldSlot, velocity);
                }
            }

            if (held[index] < 0)
                Props.SetActive(lines[index], false);
        }

        if (hoverChanged)
        {
            for (var at = 0; at < slots.Count; at++)
                slots[at].Label.SetTint(at == hover[0] || at == hover[1] ? new Color(0.15f, 0.45f, 0.75f, 0.9f)
                    : new Color(0.08f, 0.1f, 0.14f, 0.8f));
        }

        input.FreeHandBusy = held[freeIndex] >= 0;
        hands.FreeHolding = held[freeIndex] >= 0;
        HeldByWasherHand = held[1 - freeIndex] >= 0;
    }

    // In place of the hand (which fades out while it holds a nozzle),
    // pointing forward out of the grip.
    private static void Hold(Slot slot, Hands hands, Vector3 handPosition, Quaternion handRotation)
    {
        slot.TokenTransform.SetPositionAndRotation(handPosition + handRotation * new Vector3(0f, -0.01f, 0.03f), handRotation);
    }

    // A faint blue guide from the nozzle in the hand to the end of the washer.
    private void DrawLine(int index, Vector3 from, Vector3? to)
    {
        var line = lines[index];
        if (!to.HasValue || Props.HideHints)
        {
            Props.SetActive(line, false);
            return;
        }

        if (line is null || line == null)
        {
            line = Props.Primitive(PrimitiveType.Cube, "PowerWashVR Nozzle Guide", null,
                Props.Transparent(new Color(0.3f, 0.65f, 1f, 0.35f)));
            UnityEngine.Object.DontDestroyOnLoad(line);
            lines[index] = line;
        }

        var lineTransform = line.transform;
        Props.SetActive(line, true);
        var span = to.Value - from;
        var length = span.magnitude;
        if (length < 1e-3f)
            return;

        lineTransform.SetPositionAndRotation(from + span * 0.5f, Quaternion.LookRotation(span / length));
        lineTransform.localScale = new Vector3(0.004f, 0.004f, length);
    }

    // Swaps a slot's plain token for the game's own nozzle model once loaded.
    private void UpgradeModels()
    {
        foreach (var slot in slots)
        {
            if (slot.HasModel)
                continue;

            var template = models.Get(slot.Data);
            if (template is null)
                continue;

            for (var child = slot.TokenTransform.childCount - 1; child >= 0; child--)
                UnityEngine.Object.Destroy(slot.TokenTransform.GetChild(child).gameObject);

            var model = UnityEngine.Object.Instantiate(template, slot.TokenTransform, false);
            model.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            model.SetActive(true);
            slot.HasModel = true;
        }
    }

    // The newest poses, just before the frame is drawn.
    internal void Reapply(PlayerRig rig, Controllers controllers, Settings settings)
    {
        if (root is null || root == null || !root.activeSelf || !rig.HasPose)
            return;

        Place(rig, settings);
        for (var index = 0; index < 2; index++)
        {
            if (SlotAt(held[index]) is not { } slot)
                continue;
            ref var hand = ref index == 0 ? ref controllers.Left : ref controllers.Right;
            var handPosition = rig.TrackingToWorldPosition + rig.TrackingToWorldRotation * hand.GripPosition;
            var handRotation = rig.TrackingToWorldRotation * hand.GripRotation;
            Hold(slot, hands!, handPosition, handRotation);
            DrawLine(index, slot.TokenTransform.position, NozzleTip(rig));
        }
    }

    internal Hands? hands;

    private static Vector3? NozzleTip(PlayerRig rig)
    {
        if (WasherFixes.JetCount > 0)
            return WasherFixes.JetOrigin[0];
        if (rig.HasWasher)
            return rig.WasherPosition + rig.WasherRotation * new Vector3(0f, 0f, 0.6f);
        return null;
    }

    private void Fit(GameState state, Slot slot, Haptics haptics, Settings settings)
    {
        var configuration = state.Configuration;
        if (configuration is null)
            return;

        try
        {
            configuration.UpdateNozzle(slot.Data);
            haptics.Pulse(true, 0.8f, 0.06f);
            haptics.Pulse(false, 0.8f, 0.06f);
            log.Msg($"belt: fitted nozzle {slot.Label.Text?.text ?? slot.Id.ToString()}");
            nextRefresh = 0f;
        }
        catch (Exception exception)
        {
            log.Warning($"belt: nozzle change failed ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private void Drop()
    {
        Drop(0);
        Drop(1);
    }

    private void Drop(int index)
    {
        if (SlotAt(held[index]) is { } slot && slot.TokenTransform is not null && slot.TokenTransform != null)
        {
            slot.TokenTransform.SetParent(rootTransform, false);
            slot.TokenTransform.SetLocalPositionAndRotation(slot.HomeLocal, HomeRotation);
        }

        held[index] = -1;
    }

    // The belt turns with the body (BodyFrame).
    private void Place(PlayerRig rig, Settings settings)
    {
        var facing = body.Rotation;
        var position = rig.HeadPosition + new Vector3(0f, -settings.BeltDrop, 0f) + facing * new Vector3(0f, 0f, 0.04f);
        var toWorld = rig.TrackingToWorldRotation;
        rootTransform!.SetPositionAndRotation(rig.TrackingToWorldPosition + toWorld * position, toWorld * facing);

        // Each label faces the head.
        var head = rig.TrackingToWorldPosition + toWorld * rig.HeadPosition;
        foreach (var slot in slots)
        {
            var label = slot.Label.Root.transform;
            var away = label.position - head;
            if (away.sqrMagnitude > 1e-6f)
                label.rotation = Quaternion.LookRotation(away, toWorld * Vector3.up);
        }
    }

    // A plain belt around the waist that the nozzles hang on: a ring of
    // short dark segments just inside the nozzles, with a buckle at the front.
    private GameObject? ring;

    private void BuildRing()
    {
        ring = new GameObject("Strap");
        ring.transform.SetParent(rootTransform, false);
        const int segments = 32;
        var radius = SlotRadius - 0.03f;
        var strap = Props.Lit(new Color(0.12f, 0.09f, 0.07f, 1f));
        var length = 2f * Mathf.PI * radius / segments * 1.08f;
        for (var index = 0; index < segments; index++)
        {
            var angle = 360f * index / segments;
            var rotation = Quaternion.Euler(0f, angle, 0f);
            var piece = Props.Primitive(PrimitiveType.Cube, "Segment", ring.transform, strap);
            piece.transform.SetLocalPositionAndRotation(rotation * new Vector3(0f, 0f, radius), rotation);
            piece.transform.localScale = new Vector3(length, 0.045f, 0.01f);
        }

        var buckle = Props.Primitive(PrimitiveType.Cube, "Buckle", ring.transform, Props.Lit(new Color(0.7f, 0.68f, 0.6f, 1f)));
        buckle.transform.SetLocalPositionAndRotation(new Vector3(0f, 0f, radius + 0.008f), Quaternion.identity);
        buckle.transform.localScale = new Vector3(0.05f, 0.04f, 0.008f);
    }

    // The nozzle set, rebuilt only when it changes.
    private void Refresh(GameState state)
    {
        var configuration = state.Configuration;
        var current = configuration?.CurrentConfiguration;
        if (configuration is null || current is null)
            return;

        var compatible = current.CompatibleNozzles;
        if (compatible is null)
            return;

        var equipped = current.Nozzle;
        var equippedId = equipped is null ? -1 : equipped.UniqueId;

        var owned = new List<NozzleData>();
        var taken = new HashSet<int>();
        var key = new System.Text.StringBuilder();
        for (var index = 0; index < compatible.Count; index++)
        {
            var nozzle = compatible[index];
            if (nozzle is null || nozzle == null)
                continue;

            bool isOwned;
            try
            {
                isOwned = configuration.IsOwned(nozzle);
            }
            catch
            {
                isOwned = true;
            }

            if (!isOwned)
                continue;

            // Only the four standard tips: 0 red, 15 yellow, 25 green, 40 white.
            var degree = Degree(nozzle);
            if (degree < 0 || taken.Contains(degree))
                continue;

            taken.Add(degree);
            owned.Add(nozzle);
        }

        owned.Sort((a, b) => Degree(a).CompareTo(Degree(b)));
        foreach (var nozzle in owned)
            key.Append(nozzle.UniqueId).Append(',');

        key.Append('|').Append(equippedId);
        var now = key.ToString();
        if (string.Equals(now, signature, StringComparison.Ordinal))
            return;

        signature = now;
        Rebuild(owned, equippedId);
    }

    private void Rebuild(List<NozzleData> nozzles, int equippedId)
    {
        // A nozzle still in a hand stays there (fitting the other hand's
        // nozzle rebuilds the belt).
        var keep = new[] { SlotAt(held[0])?.Id ?? 0, SlotAt(held[1])?.Id ?? 0 };
        var kept = new[] { held[0] >= 0, held[1] >= 0 };
        Drop();
        try
        {
            RebuildSlots(nozzles, equippedId);
        }
        finally
        {
            for (var index = 0; index < 2; index++)
            {
                if (!kept[index])
                    continue;
                var at = slots.FindIndex(slot => slot.Id == keep[index] && !slot.Equipped);
                held[index] = at;
            }
        }
    }

    private void RebuildSlots(List<NozzleData> nozzles, int equippedId)
    {
        foreach (var slot in slots)
        {
            if (slot.Token is not null && slot.Token != null)
                UnityEngine.Object.Destroy(slot.Token);
            if (slot.Label.Root is not null && slot.Label.Root != null)
                UnityEngine.Object.Destroy(slot.Label.Root);
        }

        slots.Clear();
        hover[0] = hover[1] = -1;

        if (root is null || root == null)
        {
            root = new GameObject("PowerWashVR Belt");
            UnityEngine.Object.DontDestroyOnLoad(root);
            rootTransform = root.transform;
        }

        if (strapShown && (ring is null || ring == null))
            BuildRing();

        var count = nozzles.Count;
        for (var index = 0; index < count; index++)
        {
            var nozzle = nozzles[index];
            var angle = count == 1 ? 0f : -ArcDegrees * 0.5f + ArcDegrees * index / (count - 1);
            var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            var home = direction * SlotRadius;

            var degree = Degree(nozzle);
            var name = $"{degree}\u00b0";
            var color = DegreeColor(degree);
            var material = Props.Lit(color);

            // A holder whose +Z is the nozzle's long axis (standing up on the
            // belt); the plain token inside is replaced by the game's model.
            var token = new GameObject($"Nozzle {name}");
            token.transform.SetParent(rootTransform, false);
            token.transform.SetLocalPositionAndRotation(home, HomeRotation);

            var label = new Props.Card($"PowerWashVR Label {name}", rootTransform, new Vector2(0.11f, 0.03f));
            label.Root.transform.localPosition = home + new Vector3(0f, 0.06f, 0f);
            label.SetText(name);
            Props.SetActive(label.Root, namesShown);

            var equipped = nozzle.UniqueId == equippedId;
            token.SetActive(!equipped);
            label.SetTint(new Color(0.08f, 0.1f, 0.14f, 0.8f));
            if (equipped && label.Text is not null)
                label.Text.color = new Color(1f, 1f, 1f, 0.45f);

            slots.Add(new Slot
            {
                Data = nozzle,
                Id = nozzle.UniqueId,
                Token = token,
                TokenTransform = token.transform,
                Label = label,
                HomeLocal = home,
                Equipped = equipped,
                Material = material,
                Color = color,
            });
        }

        log.Msg($"belt: {count} nozzle(s)");
    }

    internal static string NameOf(GameContent content)
    {
        try
        {
            var localized = content.LocalizedName;
            var text = localized is null ? null : localized.GetLocalizedString();
            if (!string.IsNullOrWhiteSpace(text))
                return text!;
        }
        catch
        {
            // Not loaded yet, or no table entry.
        }

        try
        {
            var unique = content.UniqueName;
            if (!string.IsNullOrWhiteSpace(unique))
                return unique;
        }
        catch
        {
            // Fall through.
        }

        return content.name;
    }

    // Which standard tip this is, from its content name (for example
    // EQUIPMENT_SHOP_BRANDA_PROFESSIONAL_25DEGREE_TITLE); -1 for anything else.
    private static readonly System.Text.RegularExpressions.Regex DegreePattern =
        new(@"(?<![0-9])(0|15|25|40)[ _-]?(DEGREE|DEG|°)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly Dictionary<IntPtr, int> degrees = new();

    private static int Degree(NozzleData nozzle)
    {
        if (degrees.TryGetValue(nozzle.Pointer, out var cached))
            return cached;

        var degree = -1;
        foreach (var text in new[] { SafeUnique(nozzle), NameOf(nozzle), nozzle.name })
        {
            if (string.IsNullOrEmpty(text) || text.Contains("TURBO", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SOAP", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = DegreePattern.Match(text);
            if (match.Success)
            {
                degree = int.Parse(match.Groups[1].Value);
                break;
            }
        }

        degrees[nozzle.Pointer] = degree;
        return degree;
    }

    private static string SafeUnique(GameContent content)
    {
        try
        {
            return content.UniqueName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static Color DegreeColor(int degree) => degree switch
    {
        0 => new Color(0.85f, 0.15f, 0.12f),
        15 => new Color(0.95f, 0.8f, 0.15f),
        25 => new Color(0.2f, 0.75f, 0.25f),
        _ => new Color(0.92f, 0.92f, 0.92f),
    };

    // The game's nozzle colours: 0 red, 15 yellow, 25 green, 40 white; soap
    // and turbo get their own.
    private static Color ColorOf(string name, int id)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("soap"))
            return new Color(0.15f, 0.15f, 0.9f);
        if (lower.Contains("turbo"))
            return new Color(0.3f, 0.3f, 0.3f);
        if (lower.Contains("40"))
            return new Color(0.92f, 0.92f, 0.92f);
        if (lower.Contains("25"))
            return new Color(0.2f, 0.75f, 0.25f);
        if (lower.Contains("15"))
            return new Color(0.95f, 0.8f, 0.15f);
        if (lower.Contains("0"))
            return new Color(0.85f, 0.15f, 0.12f);

        var hue = (id * 0.618034f) % 1f;
        return Color.HSVToRGB(hue < 0f ? hue + 1f : hue, 0.6f, 0.9f);
    }
}
