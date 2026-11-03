using System.Linq;
using Il2CppFuturLab.PW2;
using MelonLoader;
using Unity.Netcode;
using UnityEngine;

namespace PowerWashVR.Game;

// Multiplayer bodies between players who have the mod.
//
// The sender poses its OWN third-person body (the one other players see) in
// the frame's late update: arms reach to where the VR hands are (two-bone IK,
// hand turned like the drawn hand, finger joints copied from the drawn
// fingers), and the hips drop with the head when crouching - physically or
// with the mod's crouch - with the legs bent to keep the feet planted. The
// resulting joint rotations are sent about 15 times a second as a Netcode
// named message. Everyone with the mod applies them to that player's body
// after its animation; players without the mod never see the message.
//
// Netcode only lets clients message the host, so the host relays: this
// works when the host has the mod (VR or not).
internal sealed class BodySync
{
    private const string Channel = "PowerWashVR.Body.8";
    private const float SendInterval = 1f / 15f;
    private const float Timeout = 1f;
    private const byte Version = 8;

    private readonly MelonLogger.Instance log;

    // Canonical joints, the same list on every machine.
    private static readonly string[] Slots = BuildSlots();
    private static readonly Dictionary<string, string[]> Aliases = BuildAliases();

    private static string[] BuildSlots()
    {
        var list = new List<string> { "Hips" };
        foreach (var s in new[] { "L", "R" })
        {
            list.Add($"{s}_UpperArm");
            list.Add($"{s}_LowerArm");
            list.Add($"{s}_Hand");
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                for (var joint = 1; joint <= 3; joint++)
                    list.Add($"{s}_{finger}0{joint}");
            }

            list.Add($"{s}_UpperLeg");
            list.Add($"{s}_LowerLeg");
            list.Add($"{s}_Foot");
        }

        return list.ToArray();
    }

    private static Dictionary<string, string[]> BuildAliases()
    {
        var aliases = new Dictionary<string, string[]>
        {
            ["Hips"] = new[] { "Hips", "Pelvis", "Hip", "Root_Hips" },
        };
        foreach (var (s, word) in new[] { ("L", "Left"), ("R", "Right") })
        {
            aliases[$"{s}_UpperArm"] = new[] { $"{s}_UpperArm", $"{s}_UpArm", $"{s}_Arm", $"{word}UpperArm", $"{word}Arm" };
            aliases[$"{s}_LowerArm"] = new[] { $"{s}_LowerArm", $"{s}_Forearm", $"{s}_ForeArm", $"{word}LowerArm", $"{word}ForeArm" };
            aliases[$"{s}_Hand"] = new[] { $"{s}_Hand", $"{word}Hand" };
            aliases[$"{s}_UpperLeg"] = new[] { $"{s}_UpperLeg", $"{s}_Thigh", $"{s}_UpLeg", $"{word}UpperLeg", $"{word}UpLeg" };
            aliases[$"{s}_LowerLeg"] = new[] { $"{s}_LowerLeg", $"{s}_Calf", $"{s}_Shin", $"{s}_Leg", $"{word}LowerLeg", $"{word}Leg" };
            aliases[$"{s}_Foot"] = new[] { $"{s}_Foot", $"{s}_Ankle", $"{word}Foot" };
        }

        return aliases;
    }

    private sealed class Rig
    {
        internal IntPtr Visuals;
        internal readonly Transform?[] Bones = new Transform?[Slots.Length];
        internal Transform? Head;
        internal Transform? Anchor, LeftAttachment, RightAttachment, BackpackBone;
        internal bool Complete;
        // The bind-pose turn from a mirrored right hand to the left hand's
        // bone frame (as the VR hands do it), for a left-handed washer.
        internal bool BindRead, HasMirrorFix;
        internal Quaternion MirrorFix = Quaternion.identity;
        internal Vector3 MirrorNormal = Vector3.right;
        internal Transform? Assembly;
    }

    private sealed class ItemView
    {
        internal int Id = int.MinValue;
        internal readonly PoseTrack Track = new();
        internal Transform? Holder;
        internal bool Seen, Model;
    }

    private sealed class ThrownNozzle
    {
        internal readonly PoseTrack Track = new();
        internal Transform? Holder;
        internal bool Seen, Model, Logged;
    }

    private sealed class Remote
    {
        internal readonly Dictionary<int, ThrownNozzle> Thrown = new();
        internal readonly ItemView[] Items = { new(), new() };
        internal bool Grabbing, GrabRight;
        internal ulong GrabId;
        internal Transform? GrabItem;
        internal float NextGrabLook;
        internal readonly PoseTrack GrabTrack = new();
        internal bool WasherAway;
        internal readonly PoseTrack AwayTrack = new();
        internal PlayerCharacter? Character;
        internal Rig? Rig;
        internal ulong Mask;
        internal readonly Quaternion[] Target = new Quaternion[Slots.Length];
        internal readonly Quaternion[] Current = new Quaternion[Slots.Length];
        internal Vector3 HipsTarget, HipsCurrent;
        internal bool HasCurrent;
        internal bool WasherLeft;
        internal int Stance, AppliedStance = -1;
        internal float StanceAt;
        internal bool CaseOut, ClipOut;
        internal readonly PoseTrack CaseTrack = new(), ClipTrack = new(), HeldTrack = new(), NozzleTrack = new();
        internal int NozzleId = -1, NozzleShown = -2;
        internal bool NozzleModel, NozzleLogged, NozzleOut;
        internal bool WasherHidden, WasherOff;
        internal readonly List<Renderer> HiddenWasher = new();
        internal readonly List<(Transform Part, Vector3 Scale)> HiddenParts = new();
        internal int HiddenHierarchy = -1;
        internal Transform? NozzleHolder;
        internal int ExtensionId = -1, SoapId = -1, HeldKind;
        internal Transform? ExtensionHolder, SoapHolder;
        internal int ExtensionShown = -2, SoapShown = -2;
        internal bool ExtensionModel, SoapModel;
        internal Vector3 CasePosition, ClipPosition;
        internal Quaternion CaseRotation = Quaternion.identity, ClipRotation = Quaternion.identity;
        internal GameObject? CaseProp, ClipProp;
        internal IntPtr CaseMesh;
        internal float LastPacket = -10f;
        internal bool HoldKnown;
        internal Vector3 HoldRightPosition;
        internal Quaternion HoldRight = Quaternion.identity;
        // Where this mod last put the washer (to tell when the game has
        // copied it back onto the right hand since).
        internal Vector3 SetPosition;
        internal Quaternion SetRotation = Quaternion.identity;
        internal bool HasSet, HoldLogged;
    }

    // Just before drawing (after every late update, the game's included):
    // the washer's grip is read from where the game put it in the right hand,
    // and for a left-handed player it is mirrored into the left hand - across
    // the body, with the bind pose's turn between the two hands' bone frames
    // (the same mirror the VR hands use), so it sits in the left palm the way
    // it sits in the right. A player who put the washer away has it hidden.
    internal void BeforeRender()
    {
        foreach (var (_, remote) in remotes)
        {
            var rig = remote.Rig;
            if (rig is null || Time.unscaledTime - remote.LastPacket > Timeout)
                continue;
            try
            {
                HideWasher(remote, rig);
                PlaceWasher(remote, rig);
                PlaceGrabbed(remote, rig);
                // Carried things on the hands as they are drawn (after every
                // late update).
                if (remote.Character is not null && remote.Character != null)
                    ShowProps(remote, rig);
            }
            catch
            {
                // Next frame.
            }
        }
    }

    private void PlaceWasher(Remote remote, Rig rig)
    {
        var anchor = rig.Anchor;
        var rightHand = rig.Bones[RightHandSlot];
        var leftHand = rig.Bones[LeftHandSlot];
        if (anchor is null || anchor == null || rightHand is null || rightHand == null)
            return;

        var position = anchor.position;
        var rotation = anchor.rotation;
        // Moved since this mod last placed it (or never placed): that is the
        // game's own right-hand grip, this frame.
        var gameMoved = !remote.HasSet || (position - remote.SetPosition).sqrMagnitude > 1e-8f
            || Mathf.Abs(Quaternion.Dot(rotation, remote.SetRotation)) < 0.999999f;
        if (gameMoved)
        {
            var inverse = Quaternion.Inverse(rightHand.rotation);
            remote.HoldRight = inverse * rotation;
            remote.HoldRightPosition = inverse * (position - rightHand.position);
            remote.HoldKnown = true;
            remote.HasSet = false;
            if (!remote.HoldLogged)
            {
                remote.HoldLogged = true;
                log.Msg($"body sync: washer grip read from the game ({remote.HoldRightPosition.magnitude:0.000} m from the right hand)");
            }
        }

        if (remote.WasherAway)
        {
            // Out of their hands: the washer itself where they have it.
            rig.Assembly ??= anchor.Find("PowerWasher_Assembly");
            var assembly = rig.Assembly;
            remote.AwayTrack.Sample(Time.unscaledTime, out var target, out var targetRotation);
            if (assembly is not null && assembly != null && assembly.parent is not null && assembly.parent.Pointer == anchor.Pointer)
            {
                assembly.GetLocalPositionAndRotation(out var local, out var localRotation);
                var anchorRotation = targetRotation * Quaternion.Inverse(localRotation);
                anchor.SetPositionAndRotation(target - anchorRotation * Vector3.Scale(local, anchor.lossyScale), anchorRotation);
            }
            else
            {
                anchor.SetPositionAndRotation(target, targetRotation);
            }

            remote.SetPosition = anchor.position;
            remote.SetRotation = anchor.rotation;
            remote.HasSet = true;
            return;
        }

        if (!remote.WasherLeft || !remote.HoldKnown || leftHand is null || leftHand == null)
            return;

        EnsureMirror(rig);
        Quaternion turn;
        Vector3 offset;
        if (rig.HasMirrorFix)
        {
            var frame = leftHand.rotation * Quaternion.Inverse(rig.MirrorFix);
            turn = frame * MathUtil.Mirror(remote.HoldRight, rig.MirrorNormal);
            offset = frame * Reflect(remote.HoldRightPosition, rig.MirrorNormal);
        }
        else
        {
            // No bind pose: the attachment points' frames, mirrored on X.
            turn = leftHand.rotation * MathUtil.Mirror(remote.HoldRight, Vector3.right);
            offset = leftHand.rotation * MathUtil.MirrorX(remote.HoldRightPosition);
        }

        var placed = leftHand.position + offset;
        anchor.SetPositionAndRotation(placed, turn);
        remote.SetPosition = anchor.position;
        remote.SetRotation = anchor.rotation;
        remote.HasSet = true;
    }

    private static Vector3 Reflect(Vector3 v, Vector3 n) => v - 2f * Vector3.Dot(v, n) * n;

    // The bind pose of the two hand bones, from the body's skinned mesh.
    private static void EnsureMirror(Rig rig)
    {
        if (rig.BindRead)
            return;
        rig.BindRead = true;
        var right = rig.Bones[RightHandSlot];
        var left = rig.Bones[LeftHandSlot];
        if (right is null || left is null)
            return;
        try
        {
            var root = right.root;
            var meshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var index = 0; meshes is not null && index < meshes.Length; index++)
            {
                var mesh = meshes[index];
                var bones = mesh is null || mesh == null ? null : mesh.bones;
                var shared = mesh is null || mesh == null ? null : mesh.sharedMesh;
                if (bones is null || shared is null || shared == null)
                    continue;
                int r = -1, l = -1;
                for (var bone = 0; bone < bones.Length; bone++)
                {
                    var node = bones[bone];
                    if (node is null || node == null)
                        continue;
                    if (node.Pointer == right.Pointer)
                        r = bone;
                    else if (node.Pointer == left.Pointer)
                        l = bone;
                }

                var poses = shared.bindposes;
                if (r < 0 || l < 0 || poses is null || r >= poses.Length || l >= poses.Length)
                    continue;
                var rightBind = poses[r].inverse;
                var leftBind = poses[l].inverse;
                var normal = rightBind.MultiplyPoint3x4(Vector3.zero) - leftBind.MultiplyPoint3x4(Vector3.zero);
                if (normal.sqrMagnitude < 1e-6f)
                    continue;
                normal.Normalize();
                rig.MirrorNormal = normal;
                rig.MirrorFix = Quaternion.Inverse(MathUtil.Mirror(rightBind.rotation, normal)) * leftBind.rotation;
                rig.HasMirrorFix = true;
                return;
            }
        }
        catch
        {
            // Mirrored on the attachment frames instead.
        }
    }

    // The washer of a player who put it away (the case or clipboard out),
    // hidden on this machine; looked over again when the washer is rebuilt
    // (a nozzle swap).
    private static void HideWasher(Remote remote, Rig rig)
    {
        var anchor = rig.Anchor;
        if (anchor is null || anchor == null)
            return;
        if (remote.WasherHidden)
        {
            var size = anchor.hierarchyCount;
            if (!remote.WasherOff || size != remote.HiddenHierarchy)
            {
                remote.HiddenHierarchy = size;
                remote.WasherOff = true;
                remote.HiddenWasher.RemoveAll(renderer => renderer is null || renderer == null);
                remote.HiddenParts.RemoveAll(entry => entry.Part is null || entry.Part == null);
                for (var child = 0; child < anchor.childCount; child++)
                {
                    var part = anchor.GetChild(child);
                    if (part is null || part == null || remote.HiddenParts.Any(entry => entry.Part.Pointer == part.Pointer))
                        continue;
                    remote.HiddenParts.Add((part, part.localScale));
                }

                var renderers = anchor.GetComponentsInChildren<Renderer>(true);
                for (var index = 0; renderers is not null && index < renderers.Length; index++)
                {
                    var renderer = renderers[index];
                    if (renderer is null || renderer == null || renderer.forceRenderingOff)
                        continue;
                    remote.HiddenWasher.Add(renderer);
                }
            }

            // Held every frame, after the game's own late update; the pieces
            // are shrunk to nothing too (the game's item wheel shows the
            // washer again otherwise).
            for (var index = 0; index < remote.HiddenWasher.Count; index++)
            {
                var renderer = remote.HiddenWasher[index];
                if (renderer is not null && renderer != null && !renderer.forceRenderingOff)
                    renderer.forceRenderingOff = true;
            }

            foreach (var (part, _) in remote.HiddenParts)
            {
                if (part is not null && part != null && part.localScale != Vector3.zero)
                    part.localScale = Vector3.zero;
            }
        }
        else if (remote.WasherOff)
        {
            ShowWasher(remote);
        }
    }

    private static void ShowWasher(Remote remote)
    {
        foreach (var renderer in remote.HiddenWasher)
        {
            if (renderer is not null && renderer != null)
                renderer.forceRenderingOff = false;
        }

        remote.HiddenWasher.Clear();
        foreach (var (part, scale) in remote.HiddenParts)
        {
            if (part is not null && part != null)
                part.localScale = scale;
        }

        remote.HiddenParts.Clear();
        remote.WasherOff = false;
        remote.HiddenHierarchy = -1;
    }

    private static readonly int RightHandSlot = Array.IndexOf(Slots, "R_Hand");
    private static readonly int LeftHandSlot = Array.IndexOf(Slots, "L_Hand");

    // Two received poses and when they came: shown a packet interval behind,
    // moving smoothly between them (a little past the newest when late).
    private sealed class PoseTrack
    {
        private Vector3 fromPosition, toPosition;
        private Quaternion fromRotation = Quaternion.identity, toRotation = Quaternion.identity;
        private float fromTime, toTime;
        private int count;
        internal int Side = -1;

        internal void Reset() => count = 0;

        internal void Push(Vector3 position, Quaternion rotation, float time)
        {
            if (count == 0)
            {
                fromPosition = toPosition = position;
                fromRotation = toRotation = rotation;
                fromTime = time - SendInterval;
                toTime = time;
                count = 1;
                return;
            }

            fromPosition = toPosition;
            fromRotation = toRotation;
            fromTime = toTime;
            toPosition = position;
            toRotation = rotation;
            toTime = time;
            count = 2;
        }

        internal void Sample(float now, out Vector3 position, out Quaternion rotation)
        {
            var span = Mathf.Max(0.01f, toTime - fromTime);
            var shown = now - span;
            var t = Mathf.Clamp((shown - fromTime) / span, 0f, 1.3f);
            position = Vector3.LerpUnclamped(fromPosition, toPosition, t);
            rotation = Quaternion.SlerpUnclamped(fromRotation, toRotation, t);
        }
    }

    // The extension and soap nozzle on the case's sides, or in the hand.
    private NozzleModels? models;
    private readonly Dictionary<int, BaseEquipmentData> equipment = new();
    private float nextEquipmentScan;

    private void ShowCaseItems(Remote remote, Transform caseRoot, Transform root)
    {
        remote.ExtensionHolder = Item(remote.ExtensionHolder, caseRoot, "Extension");
        remote.SoapHolder = Item(remote.SoapHolder, caseRoot, "Soap");
        Model(remote.ExtensionHolder, remote.ExtensionId, ref remote.ExtensionShown, ref remote.ExtensionModel, false);
        Model(remote.SoapHolder, remote.SoapId, ref remote.SoapShown, ref remote.SoapModel, true);

        // The items in their hands are shown on their own (ShowItems).
        foreach (var (holder, local) in new[] { (remote.ExtensionHolder, Backpack.ExtensionLocal), (remote.SoapHolder, Backpack.SoapLocal) })
        {
            if (holder is not null)
                holder.SetLocalPositionAndRotation(local, Backpack.PropLocalRotation);
        }
    }

    private static Transform Item(Transform? holder, Transform parent, string name)
    {
        if (holder is not null && holder != null)
            return holder;
        var created = new GameObject($"PowerWashVR Remote {name}").transform;
        created.SetParent(parent, false);
        return created;
    }

    private void Model(Transform holder, int id, ref int shown, ref bool hasModel, bool soap)
    {
        if (id != shown)
        {
            shown = id;
            hasModel = false;
            for (var child = holder.childCount - 1; child >= 0; child--)
                UnityEngine.Object.Destroy(holder.GetChild(child).gameObject);
            if (id == -1)
                return;
        }

        if (hasModel || id == -1)
            return;
        var data = Equipment(id);
        if (data is null)
            return;
        models ??= new NozzleModels(log, 1.5f);
        var template = models.Get(data);
        if (template is null)
            return;
        for (var child = holder.childCount - 1; child >= 0; child--)
            UnityEngine.Object.Destroy(holder.GetChild(child).gameObject);
        var copy = UnityEngine.Object.Instantiate(template, holder, false);
        copy.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        copy.SetActive(true);
        hasModel = true;
    }

    // With modded players around, every nozzle and extension model is loaded
    // ahead (once per level, one a frame), so what they hold, throw or put on
    // their case shows at once.
    private int preloadedScene = int.MinValue;

    private void PreloadModels()
    {
        models ??= new NozzleModels(log, 1.5f);
        nozzleModels ??= new NozzleModels(log);
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (scene != preloadedScene)
        {
            preloadedScene = scene;
            nextEquipmentScan = 0f;
            Equipment(int.MinValue);
            var count = 0;
            foreach (var (_, data) in equipment)
            {
                if (data is null || data == null)
                    continue;
                if (data.TryCast<NozzleData>() is not null)
                {
                    nozzleModels.Preload(data);
                    models.Preload(data);
                    count++;
                }
                else if (data.TryCast<ExtensionData>() is not null)
                {
                    models.Preload(data);
                    count++;
                }
            }

            log.Msg($"body sync: {count} nozzle/extension model(s) queued to load for other players");
        }

        models.Tick();
        nozzleModels.Tick();
    }

    private BaseEquipmentData? Equipment(int id)
    {
        if (equipment.TryGetValue(id, out var known) && known != null)
            return known;
        if (Time.unscaledTime < nextEquipmentScan)
            return null;
        nextEquipmentScan = Time.unscaledTime + 5f;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<BaseEquipmentData>());
            for (var index = 0; all is not null && index < all.Length; index++)
            {
                var data = all[index]?.TryCast<BaseEquipmentData>();
                if (data is not null && data != null)
                    equipment[data.UniqueId] = data;
            }
        }
        catch
        {
            // Next scan.
        }

        return equipment.TryGetValue(id, out var found) ? found : null;
    }

    private readonly Dictionary<ulong, Remote> remotes = new();
    private Rig? own;
    private float nextSend;
    private float nextScan;
    private IntPtr registeredFor;
    private static BodySync? instance;
    private static CustomMessagingManager.HandleNamedMessageDelegate? handler;
    private int sent, received, relayed;
    private float nextStats;
    private bool ownLogged, firstSendLogged;
    private readonly HashSet<ulong> announced = new();
    private readonly List<(string Name, Quaternion Local)> fingerBuffer = new();
    private readonly byte[] buffer = new byte[8 + 8 + 8 + 6 + Slots.Length * 8 + 512];
    private float standingEye = 1.5f;
    private int recentersSeen = -1;
    private int ownStance = -1;
    private float ownStanceAt;
    private Backpack? backpack;
    private Belt? belt;
    private PowerWashVR.Input.Controllers? controllers;
    private int stanceLogs;

    internal BodySync(MelonLogger.Instance log)
    {
        this.log = log;
        instance = this;
    }

    // Every frame (VR or not): keeps the message handler registered and
    // applies other players' bodies.
    internal void ApplyRemotes()
    {
        var now = Time.unscaledTime;
        var manager = Manager();
        if (manager is null)
        {
            // Not in a session: the next one (a new lobby in the same run
            // restarts the same network manager with new messaging) is
            // registered from scratch.
            if (remotes.Count > 0)
                ClearRemotes();
            registeredFor = IntPtr.Zero;
            return;
        }

        Register(manager);
        if (remotes.Count == 0)
            return;
        PreloadModels();

        if (now >= nextScan)
        {
            nextScan = now + 2f;
            FindCharacters();
        }

        var dt = Time.unscaledDeltaTime;
        var t = 1f - Mathf.Exp(-dt * 18f);
        foreach (var (_, remote) in remotes)
        {
            if (remote.Character is null || remote.Character == null)
                continue;
            if (now - remote.LastPacket > Timeout)
            {
                // Gone quiet: back to what the game shows.
                if (remote.AppliedStance > 0)
                    SetStance(remote.Character, 0);
                remote.AppliedStance = -1;
                Props.SetActive(remote.CaseProp, false);
                Props.SetActive(remote.ClipProp, false);
                if (remote.NozzleHolder is not null && remote.NozzleHolder != null)
                    remote.NozzleHolder.gameObject.SetActive(false);
                if (remote.Rig?.BackpackBone is { } back && back != null)
                    back.localScale = Vector3.one;
                if (remote.WasherOff)
                    ShowWasher(remote);
                HideThrown(remote);
                HideItems(remote);
                continue;
            }
            var rig = remote.Rig;
            if (rig is null || !Fresh(rig, remote.Character))
            {
                if (remote.WasherOff)
                    ShowWasher(remote);
                remote.HasSet = false;
                remote.Rig = rig = Resolve(remote.Character);
                if (rig is null)
                    continue;
            }

            for (var slot = 0; slot < Slots.Length; slot++)
            {
                if ((remote.Mask & (1UL << slot)) == 0)
                    continue;
                var bone = rig.Bones[slot];
                if (bone is null || bone == null)
                    continue;
                remote.Current[slot] = remote.HasCurrent ? Quaternion.Slerp(remote.Current[slot], remote.Target[slot], t) : remote.Target[slot];
                bone.localRotation = remote.Current[slot];
            }

            // Crouching: the game's own crouch (or lying) animation, set on
            // this machine only.
            if (remote.Stance != remote.AppliedStance || now - remote.StanceAt > 2f)
            {
                if (remote.Stance != remote.AppliedStance && remote.AppliedStance >= 0 && stanceLogs++ < 20)
                    log.Msg($"body sync: player {remote.Character.OwnerClientId} shown {(CharacterStance)remote.Stance}");
                SetStance(remote.Character, remote.Stance);
                remote.AppliedStance = remote.Stance;
                remote.StanceAt = now;
            }

            ShowProps(remote, rig);

            // The washer's hand (and hiding it) is done just before drawing.
            remote.HasCurrent = true;
        }
    }

    // Late update while VR runs: pose the local player's own body and send it.
    // Set from the settings: the VR washer is in the left hand.
    internal bool WasherLeft;
    // Set every frame: the VR washer is put away or switched off.
    internal bool WasherHidden;
    // Set every frame: Physical Washer, no hand holds it (on the belt or in
    // the air); its world pose is sent.
    internal bool WasherAway;

    internal void SendOwn(PlayerRig rig, GameState state, Hands hands, Backpack pack, Belt beltIn, PowerWashVR.Input.Controllers controllers)
    {
        backpack = pack;
        belt = beltIn;
        this.controllers = controllers;
        var manager = Manager();
        var character = state.Character;
        if (manager is null || character is null || character == null || state.Mode == UiMode.Menu || !rig.HasPose)
            return;
        if (!manager.IsServer && !manager.IsConnectedClient)
            return;

        var now = Time.unscaledTime;
        if (now < nextSend)
            return;
        nextSend = now + SendInterval;

        // Nobody else in the session: nothing to send.
        var targets = Targets(manager);
        if (targets is not null && targets.Count == 0)
            return;
        if (targets is null && !OthersPresent())
            return;

        if (own is null || !Fresh(own, character))
        {
            own = Resolve(character);
            if (own is null)
                return;
            AlwaysAnimate(character);
            if (!ownLogged)
            {
                ownLogged = true;
                LogSkeleton(character);
            }
        }

        try
        {
            Pose(own, character, rig, hands);
            var length = Write(own, manager.LocalClientId, character);
            Send(manager, targets, length);
            sent++;
            if (!firstSendLogged)
            {
                firstSendLogged = true;
                log.Msg($"body sync: sending this player's arms, fingers and crouch ({length} bytes, {1f / SendInterval:0}/s) to "
                    + (targets is null ? "the host" : $"client(s) {string.Join(", ", targets)}"));
            }
        }
        catch (Exception exception)
        {
            if (sent < 3)
                log.Warning($"body sync: send failed ({exception.GetType().Name}: {exception.Message})");
            sent++;
        }

        if (now >= nextStats)
        {
            nextStats = now + 60f;
            if (sent + received > 0)
                log.Msg($"body sync: {sent} sent, {received} received, {relayed} relayed, {remotes.Count} modded player(s) seen");
        }
    }

    private static NetworkManager? Manager()
    {
        try
        {
            var manager = NetworkManager.Singleton;
            return manager is not null && manager != null && manager.IsListening ? manager : null;
        }
        catch
        {
            return null;
        }
    }

    // Everything shown for other players is put back before they are forgotten.
    private void ClearRemotes()
    {
        foreach (var (_, remote) in remotes)
        {
            try
            {
                if (remote.WasherOff)
                    ShowWasher(remote);
                HideThrown(remote);
                HideItems(remote);
                Props.SetActive(remote.CaseProp, false);
                Props.SetActive(remote.ClipProp, false);
                if (remote.NozzleHolder is not null && remote.NozzleHolder != null)
                    remote.NozzleHolder.gameObject.SetActive(false);
            }
            catch
            {
                // The objects may be gone with the old session.
            }
        }

        remotes.Clear();
    }

    private void Register(NetworkManager manager)
    {
        CustomMessagingManager? messaging;
        try
        {
            messaging = manager.CustomMessagingManager;
        }
        catch
        {
            return;
        }

        if (messaging is null || registeredFor == messaging.Pointer)
            return;
        try
        {
            handler ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(
                new Action<ulong, FastBufferReader>(OnMessage));
            messaging.RegisterNamedMessageHandler(Channel, handler);
            registeredFor = messaging.Pointer;
            ClearRemotes();
            announced.Clear();
            own = null;
            nextPeerScan = 0f;
            log.Msg($"body sync: listening for other modded players ({(manager.IsServer ? "host, relaying" : "client")})");
        }
        catch (Exception exception)
        {
            registeredFor = messaging.Pointer;
            log.Warning($"body sync: not available ({exception.GetType().Name}: {exception.Message})");
        }
    }

    // Everyone but this machine (host), or null for "the host" (client).
    // The other players are the owners of the other player characters,
    // looked up every two seconds.
    private readonly List<ulong> peers = new();
    private float nextPeerScan;

    private List<ulong>? Targets(NetworkManager manager)
    {
        if (!manager.IsServer)
            return null;
        if (Time.unscaledTime >= nextPeerScan)
        {
            nextPeerScan = Time.unscaledTime + 2f;
            peers.Clear();
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerCharacter>());
                for (var index = 0; all is not null && index < all.Length; index++)
                {
                    var character = all[index]?.TryCast<PlayerCharacter>();
                    if (character is null || character == null || character.IsOwner || !character.IsSpawned
                        || !character.gameObject.activeInHierarchy)
                        continue;
                    var id = character.OwnerClientId;
                    if (id != manager.LocalClientId && !peers.Contains(id))
                        peers.Add(id);
                }
            }
            catch
            {
                // Next scan.
            }
        }

        return peers;
    }

    // A client sends only while another player character exists.
    private float nextOthersCheck;
    private bool othersPresent;

    private bool OthersPresent()
    {
        if (Time.unscaledTime < nextOthersCheck)
            return othersPresent;
        nextOthersCheck = Time.unscaledTime + 2f;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerCharacter>());
            var others = 0;
            for (var index = 0; all is not null && index < all.Length; index++)
            {
                var character = all[index]?.TryCast<PlayerCharacter>();
                if (character is not null && character != null && !character.IsOwner && character.IsSpawned
                    && character.gameObject.activeInHierarchy)
                    others++;
            }

            othersPresent = others > 0;
        }
        catch
        {
            othersPresent = false;
        }

        return othersPresent;
    }

    private void Send(NetworkManager manager, List<ulong>? targets, int length)
    {
        var messaging = manager.CustomMessagingManager;
        if (messaging is null)
            return;
        var data = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(length);
        for (var index = 0; index < length; index++)
            data[index] = buffer[index];
        if (targets is null)
            SendTo(messaging, 0UL, data, length);
        else
        {
            foreach (var id in targets)
                SendTo(messaging, id, data, length);
        }
    }

    private static void SendTo(CustomMessagingManager messaging, ulong client, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> data, int length)
    {
        var writer = new FastBufferWriter(length + 16, Unity.Collections.Allocator.Temp, -1);
        try
        {
            writer.WriteBytesSafe(data, length, 0);
            messaging.SendNamedMessage(Channel, client, writer, NetworkDelivery.UnreliableSequenced);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void OnMessage(ulong sender, FastBufferReader reader)
    {
        try
        {
            instance?.Receive(sender, reader);
        }
        catch
        {
            // A bad packet is dropped.
        }
    }

    private void Receive(ulong sender, FastBufferReader reader)
    {
        var all = reader.ToArray();
        var start = reader.Position;
        if (all is null || all.Length - start < 24)
            return;
        var bytes = new byte[all.Length - start];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = all[start + index];
        if (bytes[0] != (byte)'P' || bytes[1] != (byte)'W' || bytes[2] != Version)
            return;

        var manager = Manager();
        if (manager is null)
            return;

        var origin = BitConverter.ToUInt64(bytes, 8);
        // The host relays a client's body to everyone else, stamped with the
        // client it really came from.
        if (manager.IsServer && sender != manager.LocalClientId)
        {
            origin = sender;
            var stamp = BitConverter.GetBytes(origin);
            Array.Copy(stamp, 0, bytes, 8, 8);
            var messaging = manager.CustomMessagingManager;
            var targets = Targets(manager);
            if (messaging is not null && targets is not null)
            {
                var data = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(bytes.Length);
                for (var index = 0; index < bytes.Length; index++)
                    data[index] = bytes[index];
                foreach (var id in targets)
                {
                    if (id == sender)
                        continue;
                    SendTo(messaging, id, data, bytes.Length);
                    relayed++;
                }
            }
        }

        if (origin == manager.LocalClientId)
            return;

        if (!remotes.TryGetValue(origin, out var remote))
        {
            remote = new Remote();
            remotes[origin] = remote;
            nextScan = 0f;
        }

        if (announced.Add(origin))
            log.Msg($"body sync: player {origin} has the mod - showing their arms, fingers and crouch");

        Read(bytes, remote);
        remote.LastPacket = Time.unscaledTime;
        received++;
    }

    private void FindCharacters()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<PlayerCharacter>());
            for (var index = 0; all is not null && index < all.Length; index++)
            {
                var character = all[index]?.TryCast<PlayerCharacter>();
                if (character is null || character == null || character.IsOwner)
                    continue;
                if (remotes.TryGetValue(character.OwnerClientId, out var remote) && (remote.Character is null || remote.Character == null
                    || remote.Character.Pointer != character.Pointer))
                {
                    remote.Character = character;
                    remote.Rig = null;
                    remote.HoldKnown = false;
                    remote.HasSet = false;
                    remote.HiddenWasher.Clear();
                    remote.HiddenParts.Clear();
                    remote.WasherOff = false;
                    remote.HasCurrent = false;
                }
            }
        }
        catch
        {
            // Next scan.
        }
    }

    private static bool Fresh(Rig rig, PlayerCharacter character)
    {
        var visuals = character.ThirdPersonVisuals;
        return visuals is not null && visuals != null && visuals.Pointer == rig.Visuals
            && rig.Bones.All(bone => bone is null || bone != null);
    }

    // The body's skeleton is under "Rig" (CHAR_MP_Default/Rig) with the same
    // bone names as the first-person arms (L_UpperArm, L_LowerArm, L_Hand,
    // L_Index01..03 ...). Names are matched exactly and only under the
    // skeleton: the visuals also hold a mesh called "head" and a debug hand
    // with its own "L_Hand".
    private static Rig? Resolve(PlayerCharacter character)
    {
        var visuals = character.ThirdPersonVisuals;
        if (visuals is null || visuals == null)
            return null;
        var all = visuals.GetComponentsInChildren<Transform>(true);
        Transform? skeleton = null;
        Transform? anchor = null;
        for (var index = 0; all is not null && index < all.Length; index++)
        {
            var node = all[index];
            if (node is null || node == null)
                continue;
            if (skeleton is null && node.name == "Rig")
                skeleton = node;
            else if (anchor is null && node.name == "EquipmentAnchor")
                anchor = node;
        }

        var transforms = (skeleton ?? visuals.transform).GetComponentsInChildren<Transform>(true);
        var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
        for (var index = 0; transforms is not null && index < transforms.Length; index++)
        {
            var node = transforms[index];
            if (node is not null && node != null && !byName.ContainsKey(node.name))
                byName[node.name] = node;
        }

        var rig = new Rig { Visuals = visuals.Pointer, Anchor = anchor };
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            var names = Aliases.TryGetValue(Slots[slot], out var list) ? list : new[] { Slots[slot] };
            foreach (var name in names)
            {
                if (byName.TryGetValue(name, out var bone))
                {
                    rig.Bones[slot] = bone;
                    break;
                }
            }
        }

        rig.Head = byName.TryGetValue("Head", out var head) ? head : null;
        rig.LeftAttachment = byName.TryGetValue("L_HandAttachment", out var leftAttach) ? leftAttach : null;
        rig.RightAttachment = byName.TryGetValue("R_HandAttachment", out var rightAttach) ? rightAttach : null;
        rig.BackpackBone = byName.TryGetValue("Backpack", out var pack) ? pack : null;
        rig.Complete = rig.Bones[0] is not null && rig.Head is not null;
        return rig.Bones.Any(bone => bone is not null) ? rig : null;
    }

    private void LogSkeleton(PlayerCharacter character)
    {
        try
        {
            var visuals = character.ThirdPersonVisuals;
            var transforms = visuals.GetComponentsInChildren<Transform>(true);
            var names = new List<string>();
            for (var index = 0; transforms is not null && index < transforms.Length && names.Count < 160; index++)
            {
                if (transforms[index] is { } node && node != null)
                    names.Add(node.name);
            }

            var missing = Slots.Where((_, slot) => own!.Bones[slot] is null).ToArray();
            log.Msg($"body sync: third-person skeleton: {string.Join(", ", names)}");
            log.Msg($"body sync: {Slots.Length - missing.Length} of {Slots.Length} joints found, head {(own!.Head is null ? "missing" : "found")}"
                + (missing.Length > 0 ? $"; missing {string.Join(", ", missing)}" : ""));
        }
        catch
        {
            // Diagnostics only.
        }
    }

    // ---- the local player's body ----

    private void Pose(Rig rig, PlayerCharacter character, PlayerRig vr, Hands hands)
    {
        var camera = vr.CameraTransform;
        if (camera is null)
            return;
        var root = character.transform;
        var up = root.up;
        // The frame of where the player faces (the head's yaw): the body's
        // root does not always turn with the view.
        var facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(camera.forward, up).sqrMagnitude > 1e-4f
            ? Vector3.ProjectOnPlane(camera.forward, up) : root.forward, up);
        var right = facing * Vector3.right;
        var eye = camera.position;
        var head = rig.Head;

        // Crouch, physical or the mod's: the eye height against the tallest
        // standing height seen (slowly forgotten), as the game's own stance
        // (its crouch and lying animations, legs and all). Set on this
        // player's body too, so the arms below are solved on the pose the
        // others will see.
        if (vr.TryFootY(out var footY))
        {
            var eyeNow = eye.y - footY;
            var standingNow = eyeNow + vr.CrouchDrop;
            // A recenter makes where the head is now the standing height.
            if (PlayerRig.Recenters != recentersSeen)
            {
                recentersSeen = PlayerRig.Recenters;
                standingEye = Mathf.Clamp(standingNow, 1.1f, 2.2f);
                log.Msg($"body sync: standing height reset by the recenter ({standingEye:0.00} m)");
            }

            standingEye = Mathf.Max(standingEye - SendInterval * 0.002f, Mathf.Clamp(standingNow, 1.1f, 2.2f));
            var ratio = eyeNow / standingEye;
            var stance = ratio < 0.42f ? 2 : ratio < 0.78f ? 1 : 0;
            // A little hysteresis against flicker at the edges.
            if (ownStance == 1 && stance == 0 && ratio < 0.83f)
                stance = 1;
            if (ownStance == 2 && stance == 1 && ratio < 0.47f)
                stance = 2;
            if (stance != ownStance && stanceLogs++ < 20)
                log.Msg($"body sync: stance {(CharacterStance)stance} (eye {eyeNow:0.00} m of {standingEye:0.00} m, ratio {ratio:0.00})");
            Stance = stance;
            var now = Time.unscaledTime;
            if (stance != ownStance || now - ownStanceAt > 2f)
            {
                SetStance(character, stance);
                ownStance = stance;
                ownStanceAt = now;
            }
        }

        // Arms: each hand to where the drawn VR hand is, relative to the head.
        // The body's animator always animates, so the arm starts from the
        // animation every time and nothing builds up between poses.
        for (var side = 0; side < 2; side++)
        {
            var left = side == 0;
            if (!hands.TryGetHand(left, out var hand, fingerBuffer) || hand is null)
            {
                vrHandValid[left ? 0 : 1] = false;
                continue;
            }

            vrHandPosition[left ? 0 : 1] = hand.position;
            vrHandRotation[left ? 0 : 1] = hand.rotation;
            vrHandValid[left ? 0 : 1] = true;
            var upper = rig.Bones[ArmSlots[side, 0]];
            var lower = rig.Bones[ArmSlots[side, 1]];
            var end = rig.Bones[ArmSlots[side, 2]];
            if (upper is null || lower is null || end is null)
                continue;

            var anchor = head is not null ? head.position + up * 0.1f : upper.position + up * 0.25f;
            var target = anchor + (hand.position - eye);
            // Elbow: behind the hand along the way the controller points (the
            // forearm), a little down and out - follows the hand wherever it
            // is, whichever way the player looks.
            var outward = left ? -right : right;
            var forearm = Forearm(vr, left);
            var hint = forearm.sqrMagnitude > 0.5f
                ? target - forearm * 0.3f - up * 0.12f + outward * 0.12f
                : (upper.position + target) * 0.5f - up * 0.35f + outward * 0.2f - (facing * Vector3.forward) * 0.2f;
            TwoBone(upper, lower, end, target, hint);
            end.rotation = hand.rotation;

            foreach (var (name, local) in fingerBuffer)
            {
                var slot = Array.IndexOf(Slots, name);
                if (slot >= 0 && rig.Bones[slot] is { } finger && finger != null)
                    finger.localRotation = local;
            }
        }
    }

    internal int Stance;
    // The drawn VR hands this frame (left, right): carried things are sent
    // relative to the hand holding them, and drawn on that hand by others.
    private readonly Vector3[] vrHandPosition = new Vector3[2];
    private readonly Quaternion[] vrHandRotation = { Quaternion.identity, Quaternion.identity };
    private readonly bool[] vrHandValid = new bool[2];

    // Hand byte (0 left, 1 right) and the pose relative to the nearer drawn
    // hand (offset at most MaxHandOffset).
    private int WriteHand(int at, Vector3 position, Quaternion rotation, Transform root)
    {
        var right = vrHandValid[1] && (!vrHandValid[0]
            || (position - vrHandPosition[1]).sqrMagnitude < (position - vrHandPosition[0]).sqrMagnitude);
        var side = right ? 1 : 0;
        if (!vrHandValid[side])
        {
            // No hand: relative to the body, marked 2.
            buffer[at++] = 2;
            return WritePose(at, root.InverseTransformPoint(position), Quaternion.Inverse(root.rotation) * rotation);
        }

        buffer[at++] = (byte)side;
        var inverse = Quaternion.Inverse(vrHandRotation[side]);
        var offset = inverse * (position - vrHandPosition[side]);
        if (offset.magnitude > MaxHandOffset)
            offset = offset.normalized * MaxHandOffset;
        return WritePose(at, offset, inverse * rotation);
    }

    private const float MaxHandOffset = 0.45f;

    // Side 3: a world pose (the case put down in the room).
    private int WriteHandWorld(int at, Vector3 position, Quaternion rotation)
    {
        buffer[at++] = 3;
        return WriteWorld(at, position, rotation);
    }

    // A world pose: three floats and the rotation as four shorts (20 bytes).
    private int WriteWorld(int at, Vector3 position, Quaternion rotation)
    {
        BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 4), position.x);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, at + 4, 4), position.y);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, at + 8, 4), position.z);
        at += 12;
        at = Short(at, rotation.x * 32767f);
        at = Short(at, rotation.y * 32767f);
        at = Short(at, rotation.z * 32767f);
        return Short(at, rotation.w * 32767f);
    }

    private static int ReadWorld(byte[] bytes, int at, out Vector3 position, out Quaternion rotation)
    {
        position = new Vector3(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4), BitConverter.ToSingle(bytes, at + 8));
        rotation = Normalised(new Quaternion(ReadShort(bytes, at + 12) / 32767f, ReadShort(bytes, at + 14) / 32767f,
            ReadShort(bytes, at + 16) / 32767f, ReadShort(bytes, at + 18) / 32767f));
        return at + 20;
    }

    private static int ReadHand(byte[] bytes, int at, PoseTrack track, float now)
    {
        var side = bytes[at];
        Vector3 position;
        Quaternion rotation;
        if (side == 3)
        {
            if (bytes.Length < at + 21)
                return bytes.Length;
            at = ReadWorld(bytes, at + 1, out position, out rotation);
        }
        else
        {
            if (bytes.Length < at + 15)
                return bytes.Length;
            at = ReadPose(bytes, at + 1, out position, out rotation);
        }
        if (side != track.Side)
        {
            track.Reset();
            track.Side = side;
        }

        track.Push(position, rotation, now);
        return at;
    }

    // Where a carried thing is drawn: on that player's hand bone.
    private static void OnHand(Rig rig, Transform root, PoseTrack track, Transform target)
    {
        track.Sample(Time.unscaledTime, out var position, out var rotation);
        if (track.Side == 3)
        {
            target.SetPositionAndRotation(position, rotation);
            return;
        }

        var hand = track.Side switch { 0 => rig.Bones[LeftHandSlot], 1 => rig.Bones[RightHandSlot], _ => null };
        if (hand is null || hand == null)
        {
            target.SetPositionAndRotation(root.TransformPoint(position), root.rotation * rotation);
            return;
        }

        target.SetPositionAndRotation(hand.position + hand.rotation * position, hand.rotation * rotation);
    }

    // World direction the controller points (its aim pose), or zero.
    private Vector3 Forearm(PlayerRig vr, bool left)
    {
        var c = controllers;
        if (c is null)
            return Vector3.zero;
        var hand = left ? c.Left : c.Right;
        if (!hand.Tracked)
            return Vector3.zero;
        var rotation = vr.TrackingToWorldRotation * (hand.AimTracked ? hand.AimRotation : hand.GripRotation);
        return rotation * Vector3.forward;
    }

    // The game's stance, on the body's visuals only (nothing networked; the
    // character's collider and camera are untouched).
    private static void SetStance(PlayerCharacter character, int stance)
    {
        try
        {
            var visuals = character.ThirdPersonVisuals;
            if (visuals is null || visuals == null)
                return;
            visuals.OnCharacterStanceChanged(character.CharacterController, (CharacterStance)stance);
        }
        catch
        {
            // Next time.
        }
    }

    // The body is hidden for this player: without this its animator could
    // stop and the arm pose would build on itself.
    private static void AlwaysAnimate(PlayerCharacter character)
    {
        try
        {
            var animators = character.ThirdPersonVisuals.GetComponentsInChildren<Animator>(true);
            for (var index = 0; animators is not null && index < animators.Length; index++)
            {
                if (animators[index] is { } animator && animator != null)
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }
        catch
        {
            // The arm may drift then.
        }
    }

    // ---- what the hands carry: the washer case and the clipboard ----

    private static void ShowProps(Remote remote, Rig rig)
    {
        var root = remote.Character!.transform;
        if (rig.BackpackBone is { } back && back != null)
        {
            var scale = remote.CaseOut ? Vector3.zero : Vector3.one;
            if (back.localScale != scale)
                back.localScale = scale;
        }

        if (remote.CaseOut)
        {
            EnsureCase(remote);
            if (remote.CaseProp is { } prop && prop != null)
            {
                Props.SetActive(prop, true);
                OnHand(rig, root, remote.CaseTrack, prop.transform);
                instance?.ShowCaseItems(remote, prop.transform, root);
            }
        }
        else
        {
            Props.SetActive(remote.CaseProp, false);
        }

        if (remote.ClipOut)
        {
            if (remote.ClipProp is null || remote.ClipProp == null)
            {
                remote.ClipProp = new GameObject("PowerWashVR Remote Clipboard");
                var board = Props.Primitive(PrimitiveType.Cube, "Board", remote.ClipProp.transform, Props.Lit(new Color(0.55f, 0.38f, 0.2f)));
                board.transform.localScale = new Vector3(Clipboard.BoardWidth, Clipboard.BoardHeight, 0.008f);
                var paper = Props.Primitive(PrimitiveType.Cube, "Paper", remote.ClipProp.transform, Props.Lit(new Color(0.95f, 0.95f, 0.92f)));
                paper.transform.localScale = new Vector3(Clipboard.BoardWidth * 0.86f, Clipboard.BoardHeight * 0.8f, 0.002f);
                paper.transform.localPosition = new Vector3(0f, -0.01f, -0.005f);
                var clip = Props.Primitive(PrimitiveType.Cube, "Clip", remote.ClipProp.transform, Props.Lit(new Color(0.75f, 0.76f, 0.8f)));
                clip.transform.localScale = new Vector3(Clipboard.BoardWidth * 0.4f, 0.035f, 0.02f);
                clip.transform.localPosition = new Vector3(0f, Clipboard.BoardHeight * 0.5f - 0.01f, -0.006f);
            }

            Props.SetActive(remote.ClipProp, true);
            OnHand(rig, root, remote.ClipTrack, remote.ClipProp.transform);
        }
        else
        {
            Props.SetActive(remote.ClipProp, false);
        }

        instance?.ShowNozzle(remote, root);
        instance?.ShowThrown(remote);
        instance?.ShowItems(remote, rig, root);
    }

    // The extension / nozzle items in their hands, wherever their case is.
    private void ShowItems(Remote remote, Rig rig, Transform root)
    {
        foreach (var view in remote.Items)
        {
            if (!view.Seen)
            {
                if (view.Holder is not null && view.Holder != null && view.Holder.gameObject.activeSelf)
                    view.Holder.gameObject.SetActive(false);
                continue;
            }

            if (view.Holder is null || view.Holder == null)
            {
                view.Holder = new GameObject("PowerWashVR Remote Held Item").transform;
                view.Model = false;
            }

            if (!view.Holder.gameObject.activeSelf)
                view.Holder.gameObject.SetActive(true);
            if (!view.Model)
            {
                for (var child = view.Holder.childCount - 1; child >= 0; child--)
                    UnityEngine.Object.Destroy(view.Holder.GetChild(child).gameObject);
                if (Equipment(view.Id) is { } data)
                {
                    models ??= new NozzleModels(log, 1.5f);
                    if (models.Get(data) is { } template)
                    {
                        var copy = UnityEngine.Object.Instantiate(template, view.Holder, false);
                        copy.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                        copy.SetActive(true);
                        view.Model = true;
                    }
                }
            }

            OnHand(rig, root, view.Track, view.Holder);
        }
    }

    private static void HideItems(Remote remote)
    {
        foreach (var view in remote.Items)
        {
            view.Seen = false;
            if (view.Holder is not null && view.Holder != null)
                view.Holder.gameObject.SetActive(false);
        }
    }

    // Nozzles the player threw or dropped, where they fell, until they are
    // back on the belt (or fitted).
    private void ShowThrown(Remote remote)
    {
        foreach (var (id, thrown) in remote.Thrown)
        {
            if (!thrown.Seen)
            {
                if (thrown.Holder is not null && thrown.Holder != null && thrown.Holder.gameObject.activeSelf)
                    thrown.Holder.gameObject.SetActive(false);
                continue;
            }

            if (thrown.Holder is null || thrown.Holder == null)
            {
                thrown.Holder = new GameObject("PowerWashVR Remote Thrown Nozzle").transform;
                thrown.Model = false;
            }

            if (!thrown.Holder.gameObject.activeSelf)
                thrown.Holder.gameObject.SetActive(true);
            if (!thrown.Model && Equipment(id) is { } data)
            {
                // Extensions at their real length (the nozzle cache shrinks
                // anything longer than a nozzle to a few centimetres).
                nozzleModels ??= new NozzleModels(log);
                models ??= new NozzleModels(log, 1.5f);
                var template = data.TryCast<NozzleData>() is not null ? nozzleModels.Get(data) : models.Get(data);
                if (template is not null)
                {
                    for (var child = thrown.Holder.childCount - 1; child >= 0; child--)
                        UnityEngine.Object.Destroy(thrown.Holder.GetChild(child).gameObject);
                    var copy = UnityEngine.Object.Instantiate(template, thrown.Holder, false);
                    copy.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                    copy.SetActive(true);
                    thrown.Model = true;
                }
            }

            if (!thrown.Logged)
            {
                thrown.Logged = true;
                log.Msg($"body sync: showing a thrown nozzle (nozzle {id})");
            }

            thrown.Track.Sample(Time.unscaledTime, out var position, out var rotation);
            thrown.Holder.SetPositionAndRotation(position, rotation);
        }
    }

    private static void HideThrown(Remote remote)
    {
        foreach (var thrown in remote.Thrown.Values)
        {
            thrown.Seen = false;
            if (thrown.Holder is not null && thrown.Holder != null && thrown.Holder.gameObject.activeSelf)
                thrown.Holder.gameObject.SetActive(false);
        }
    }

    private NozzleModels? nozzleModels;

    private void ShowNozzle(Remote remote, Transform root)
    {
        if (!remote.NozzleOut)
        {
            if (remote.NozzleHolder is not null && remote.NozzleHolder != null && remote.NozzleHolder.gameObject.activeSelf)
                remote.NozzleHolder.gameObject.SetActive(false);
            return;
        }

        if (remote.NozzleHolder is null || remote.NozzleHolder == null)
        {
            remote.NozzleHolder = new GameObject("PowerWashVR Remote Nozzle").transform;
            remote.NozzleShown = -2;
        }

        if (!remote.NozzleHolder.gameObject.activeSelf)
            remote.NozzleHolder.gameObject.SetActive(true);
        if (remote.NozzleId != remote.NozzleShown)
        {
            remote.NozzleShown = remote.NozzleId;
            remote.NozzleModel = false;
            for (var child = remote.NozzleHolder.childCount - 1; child >= 0; child--)
                UnityEngine.Object.Destroy(remote.NozzleHolder.GetChild(child).gameObject);
        }

        if (!remote.NozzleModel && Equipment(remote.NozzleId) is { } data)
        {
            nozzleModels ??= new NozzleModels(log);
            var template = nozzleModels.Get(data);
            if (template is not null)
            {
                for (var child = remote.NozzleHolder.childCount - 1; child >= 0; child--)
                    UnityEngine.Object.Destroy(remote.NozzleHolder.GetChild(child).gameObject);
                var copy = UnityEngine.Object.Instantiate(template, remote.NozzleHolder, false);
                copy.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.SetActive(true);
                remote.NozzleModel = true;
            }
        }

        if (!remote.NozzleLogged)
        {
            remote.NozzleLogged = true;
            log.Msg($"body sync: showing a player's belt nozzle in their hand (nozzle {remote.NozzleId}, "
                + $"{(remote.NozzleModel ? "game model" : Equipment(remote.NozzleId) is null ? "equipment unknown, token" : "token while the model loads")})");
        }

        OnHand(remote.Rig!, root, remote.NozzleTrack, remote.NozzleHolder);
    }

    // The case is that player's own backpack (mesh and outfit colours), at
    // the size the VR player holds it.
    private static void EnsureCase(Remote remote)
    {
        try
        {
            var source = remote.Character!.ThirdPersonVisuals?.m_backpackOutfitRenderer;
            Mesh? mesh = null;
            if (source is not null && source != null)
                mesh = source.TryCast<SkinnedMeshRenderer>()?.sharedMesh ?? source.GetComponent<MeshFilter>()?.sharedMesh;
            var pointer = mesh is null || mesh == null ? new IntPtr(-1) : mesh.Pointer;
            if (remote.CaseProp is not null && remote.CaseProp != null && remote.CaseMesh == pointer)
                return;
            if (remote.CaseProp is not null && remote.CaseProp != null)
                UnityEngine.Object.Destroy(remote.CaseProp);
            remote.CaseMesh = pointer;
            remote.CaseProp = new GameObject("PowerWashVR Remote Case");
            if (mesh is not null && mesh != null && source is not null)
            {
                var model = new GameObject("Case");
                model.transform.SetParent(remote.CaseProp.transform, false);
                model.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = model.AddComponent<MeshRenderer>();
                Backpack.CopyLook(source, renderer);
                model.transform.localScale = Vector3.one * Backpack.CaseScale;
                model.transform.localPosition = -mesh.bounds.center * Backpack.CaseScale;
            }
            else
            {
                var box = Props.Primitive(PrimitiveType.Cube, "Case", remote.CaseProp.transform, Props.Lit(new Color(0.75f, 0.45f, 0.1f)));
                box.transform.localScale = new Vector3(0.3f, 0.22f, 0.1f);
            }
        }
        catch
        {
            // Next frame.
        }
    }

    private static int Index(string slot) => Array.IndexOf(Slots, slot);

    // Upper arm, lower arm and hand slots of each side (left first), looked up once.
    private static readonly int[,] ArmSlots =
    {
        { Index("L_UpperArm"), Index("L_LowerArm"), Index("L_Hand") },
        { Index("R_UpperArm"), Index("R_LowerArm"), Index("R_Hand") },
    };

    // Two-bone IK: bend the middle joint for the reach, aim the chain at the
    // target, then turn it about that line so the middle joint faces the hint.
    private static void TwoBone(Transform a, Transform b, Transform c, Vector3 target, Vector3 hint)
    {
        var aPos = a.position;
        var bPos = b.position;
        var cPos = c.position;
        var lab = (bPos - aPos).magnitude;
        var lcb = (cPos - bPos).magnitude;
        if (lab < 1e-4f || lcb < 1e-4f)
            return;
        var toTarget = target - aPos;
        var lat = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(lab - lcb) + 1e-3f, (lab + lcb) * 0.999f);

        var ba = (aPos - bPos) / lab;
        var bc = (cPos - bPos) / lcb;
        var current = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ba, bc), -1f, 1f));
        var wanted = Mathf.Acos(Mathf.Clamp((lab * lab + lcb * lcb - lat * lat) / (2f * lab * lcb), -1f, 1f));
        var axis = Vector3.Cross(ba, bc);
        if (axis.sqrMagnitude < 1e-8f)
            axis = Vector3.Cross(toTarget, hint - aPos);
        if (axis.sqrMagnitude < 1e-8f)
            return;
        b.rotation = Quaternion.AngleAxis((wanted - current) * Mathf.Rad2Deg, axis.normalized) * b.rotation;

        var reach = c.position - aPos;
        if (reach.sqrMagnitude > 1e-8f && toTarget.sqrMagnitude > 1e-8f)
            a.rotation = Quaternion.FromToRotation(reach, toTarget) * a.rotation;

        var line = toTarget.normalized;
        var elbow = Vector3.ProjectOnPlane(b.position - aPos, line);
        var towards = Vector3.ProjectOnPlane(hint - aPos, line);
        if (elbow.sqrMagnitude > 1e-8f && towards.sqrMagnitude > 1e-8f)
            a.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(elbow, towards, line), line) * a.rotation;
    }

    // ---- wire format ----
    // 'P' 'W' version 0 | origin client (8) | joint mask (8) | hips position
    // (3 x int16, mm) | per joint in the mask: rotation (4 x int16).

    private bool nozzleSentLogged, grabLogged;

    // The object the local player holds (picked up with the game's
    // interaction), and the nearer of the body's hands.
    private static Transform? HeldItem(PlayerCharacter character, Rig rig, out ulong id, out bool right)
    {
        id = 0;
        right = false;
        try
        {
            var manager = character.InteractionManager;
            var item = manager is null || manager == null ? null : manager.GetHeldItem();
            if (item is null || item == null)
                return null;
            var network = item.NetworkObject;
            if (network is null || network == null)
                return null;
            id = network.NetworkObjectId;
            var left = rig.Bones[LeftHandSlot];
            var rightHand = rig.Bones[RightHandSlot];
            if (left is null || left == null || rightHand is null || rightHand == null)
                return null;
            var place = item.transform.position;
            right = (place - rightHand.position).sqrMagnitude < (place - left.position).sqrMagnitude;
            return item.transform;
        }
        catch
        {
            return null;
        }
    }
    private readonly List<(int Id, Transform Token)> thrownOut = new();
    private readonly List<(int Id, Transform Item)> itemsOut = new();

    private int Write(Rig rig, ulong origin, PlayerCharacter character)
    {
        var root = character.transform;
        var held = backpack?.HeldCase;
        var clip = Clipboard.Held;
        var at = 0;
        buffer[at++] = (byte)'P';
        buffer[at++] = (byte)'W';
        buffer[at++] = Version;
        var nozzleId = belt?.HeldId ?? -1;
        var nozzle = belt?.HeldToken;
        if (nozzle is null || nozzle == null)
            nozzle = null;
        if (nozzle is not null && !nozzleSentLogged)
        {
            nozzleSentLogged = true;
            log.Msg($"body sync: sending the belt nozzle in hand (id {nozzleId})");
        }
        belt?.Thrown(thrownOut);
        if (belt is null)
            thrownOut.Clear();
        // Items dropped off the put-down case fly like thrown nozzles.
        backpack?.Thrown(thrownOut);
        var grabbed = HeldItem(character, rig, out var grabbedId, out var grabbedRight);
        buffer[at++] = (byte)((WasherLeft ? 1 : 0) | (held is not null ? 2 : 0) | (clip ? 4 : 0) | (nozzle is not null ? 8 : 0)
            | (WasherHidden ? 16 : 0) | (thrownOut.Count > 0 ? 32 : 0) | (grabbed is not null ? 64 : 0)
            | (WasherAway && PhysicalWasher.Instance is not null ? 128 : 0));
        buffer[at++] = (byte)Stance;
        backpack?.HeldItems(itemsOut);
        if (backpack is null)
            itemsOut.Clear();
        // A second belt nozzle (one in each hand, Physical Washer) goes as an
        // item in its hand.
        if (belt?.SecondToken is { } second && second != null && itemsOut.Count < 2)
            itemsOut.Add((belt.SecondId, second));
        buffer[at++] = (byte)(itemsOut.Count > 0 ? 1 : 0);
        at = 8;
        BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 8), origin);
        at += 8;
        // Arms, hands and fingers only: hips and legs are the game's.
        var mask = 0UL;
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            if (Synced(slot) && rig.Bones[slot] is { } bone && bone != null)
                mask |= 1UL << slot;
        }

        BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 8), mask);
        at += 8;
        var hips = rig.Bones[0];
        var hipsPosition = hips is not null && hips != null ? hips.localPosition : Vector3.zero;
        at = Short(at, hipsPosition.x * 1000f);
        at = Short(at, hipsPosition.y * 1000f);
        at = Short(at, hipsPosition.z * 1000f);
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            if ((mask & (1UL << slot)) == 0)
                continue;
            var q = rig.Bones[slot]!.localRotation;
            at = Short(at, q.x * 32767f);
            at = Short(at, q.y * 32767f);
            at = Short(at, q.z * 32767f);
            at = Short(at, q.w * 32767f);
        }

        if (held is not null)
        {
            at = backpack!.Placed ? WriteHandWorld(at, held.position, held.rotation) : WriteHand(at, held.position, held.rotation, root);
            BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 4), backpack!.ExtensionId);
            BitConverter.TryWriteBytes(new Span<byte>(buffer, at + 4, 4), backpack.SoapId);
            at += 8;
        }
        if (clip)
            at = WriteHand(at, Clipboard.BoardPosition, Clipboard.BoardRotation, root);
        if (nozzle is not null)
        {
            BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 4), nozzleId);
            at += 4;
            at = WriteHand(at, nozzle.position, nozzle.rotation, root);
        }

        // Thrown nozzles, in world space (they fly free of the player).
        if (thrownOut.Count > 0)
        {
            buffer[at++] = (byte)thrownOut.Count;
            foreach (var (id, token) in thrownOut)
            {
                BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 4), id);
                var p = token.position;
                BitConverter.TryWriteBytes(new Span<byte>(buffer, at + 4, 4), p.x);
                BitConverter.TryWriteBytes(new Span<byte>(buffer, at + 8, 4), p.y);
                BitConverter.TryWriteBytes(new Span<byte>(buffer, at + 12, 4), p.z);
                at += 16;
                var q = token.rotation;
                at = Short(at, q.x * 32767f);
                at = Short(at, q.y * 32767f);
                at = Short(at, q.z * 32767f);
                at = Short(at, q.w * 32767f);
            }
        }

        // A picked-up object: which hand, its network id, its pose in that
        // hand (so others draw it there, not where their copy of the game
        // would hold it).
        if (grabbed is not null)
        {
            BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 8), grabbedId);
            at += 8;
            at = WriteHand(at, grabbed.position, grabbed.rotation, root);
            grabbedRight = buffer[at - 15] == 1;
            if (!grabLogged)
            {
                grabLogged = true;
                log.Msg($"body sync: sending a held object (\"{grabbed.name}\", {(grabbedRight ? "right" : "left")} hand)");
            }
        }

        // Physical Washer: the washer where it is (belt or air), world.
        if (WasherAway && PhysicalWasher.Instance is { } physical)
            at = WriteWorld(at, physical.Position, physical.Rotation);

        // Items in the hands (Physical Attachments), one per hand at most.
        if (itemsOut.Count > 0)
        {
            buffer[at++] = (byte)itemsOut.Count;
            foreach (var (id, item) in itemsOut)
            {
                BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 4), id);
                at = WriteHand(at + 4, item.position, item.rotation, root);
            }
        }

        return at;
    }

    private static bool Synced(int slot)
    {
        var name = Slots[slot];
        return slot != 0 && !name.EndsWith("Leg", StringComparison.Ordinal) && !name.EndsWith("Foot", StringComparison.Ordinal);
    }

    private int WritePose(int at, Vector3 position, Quaternion rotation)
    {
        at = Short(at, position.x * 1000f);
        at = Short(at, position.y * 1000f);
        at = Short(at, position.z * 1000f);
        at = Short(at, rotation.x * 32767f);
        at = Short(at, rotation.y * 32767f);
        at = Short(at, rotation.z * 32767f);
        return Short(at, rotation.w * 32767f);
    }

    private static int ReadPose(byte[] bytes, int at, out Vector3 position, out Quaternion rotation)
    {
        position = new Vector3(ReadShort(bytes, at), ReadShort(bytes, at + 2), ReadShort(bytes, at + 4)) / 1000f;
        rotation = Normalised(new Quaternion(ReadShort(bytes, at + 6) / 32767f, ReadShort(bytes, at + 8) / 32767f,
            ReadShort(bytes, at + 10) / 32767f, ReadShort(bytes, at + 12) / 32767f));
        return at + 14;
    }

    private static Quaternion Normalised(Quaternion q)
    {
        var length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return length > 1e-4f ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
    }

    private int Short(int at, float value)
    {
        var v = (short)Mathf.Clamp(Mathf.Round(value), short.MinValue, short.MaxValue);
        buffer[at] = (byte)(v & 0xFF);
        buffer[at + 1] = (byte)((v >> 8) & 0xFF);
        return at + 2;
    }

    private static float ReadShort(byte[] bytes, int at) => (short)(bytes[at] | (bytes[at + 1] << 8));

    private static void Read(byte[] bytes, Remote remote)
    {
        remote.WasherLeft = (bytes[3] & 1) != 0;
        remote.WasherHidden = (bytes[3] & 16) != 0;
        var caseOut = (bytes[3] & 2) != 0;
        var clipOut = (bytes[3] & 4) != 0;
        remote.Stance = Mathf.Clamp(bytes[4], 0, 2);
        var mask = BitConverter.ToUInt64(bytes, 16);
        var at = 24;
        if (bytes.Length < at + 6)
            return;
        remote.HipsTarget = new Vector3(ReadShort(bytes, at), ReadShort(bytes, at + 2), ReadShort(bytes, at + 4)) / 1000f;
        at += 6;
        for (var slot = 0; slot < Slots.Length; slot++)
        {
            if ((mask & (1UL << slot)) == 0)
                continue;
            if (bytes.Length < at + 8)
            {
                mask &= ~(1UL << slot);
                continue;
            }

            var q = new Quaternion(ReadShort(bytes, at) / 32767f, ReadShort(bytes, at + 2) / 32767f,
                ReadShort(bytes, at + 4) / 32767f, ReadShort(bytes, at + 6) / 32767f);
            at += 8;
            var length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            remote.Target[slot] = length > 1e-4f ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
        }

        remote.Mask = mask;
        var now = Time.unscaledTime;
        remote.CaseOut = caseOut && bytes.Length > at && bytes.Length >= at + (bytes[at] == 3 ? 21 : 15) + 8;
        if (remote.CaseOut)
        {
            at = ReadHand(bytes, at, remote.CaseTrack, now);
            remote.ExtensionId = BitConverter.ToInt32(bytes, at);
            remote.SoapId = BitConverter.ToInt32(bytes, at + 4);
            at += 8;
        }
        else
        {
            remote.CaseTrack.Reset();
        }

        remote.ClipOut = clipOut && bytes.Length >= at + 15;
        if (remote.ClipOut)
            at = ReadHand(bytes, at, remote.ClipTrack, now);
        else
        {
            remote.ClipTrack.Reset();
        }

        // A nozzle from the belt in the hand.
        var nozzleOut = (bytes[3] & 8) != 0 && bytes.Length >= at + 4 + 15;
        if (nozzleOut)
        {
            var id = BitConverter.ToInt32(bytes, at);
            if (id != remote.NozzleId)
                remote.NozzleTrack.Reset();
            at = ReadHand(bytes, at + 4, remote.NozzleTrack, now);
            remote.NozzleId = id;
            remote.NozzleOut = true;
        }
        else
        {
            remote.NozzleId = -1;
            remote.NozzleOut = false;
            remote.NozzleTrack.Reset();
        }


        // Thrown nozzles.
        foreach (var thrown in remote.Thrown.Values)
            thrown.Seen = false;
        if ((bytes[3] & 32) != 0 && bytes.Length > at)
        {
            var count = bytes[at++];
            for (var index = 0; index < count && bytes.Length >= at + 24; index++)
            {
                var id = BitConverter.ToInt32(bytes, at);
                var position = new Vector3(BitConverter.ToSingle(bytes, at + 4), BitConverter.ToSingle(bytes, at + 8), BitConverter.ToSingle(bytes, at + 12));
                var rotation = Normalised(new Quaternion(ReadShort(bytes, at + 16) / 32767f, ReadShort(bytes, at + 18) / 32767f,
                    ReadShort(bytes, at + 20) / 32767f, ReadShort(bytes, at + 22) / 32767f));
                at += 24;
                if (!remote.Thrown.TryGetValue(id, out var thrown))
                {
                    thrown = new ThrownNozzle();
                    remote.Thrown[id] = thrown;
                }

                thrown.Seen = true;
                thrown.Track.Push(position, rotation, now);
            }
        }

        foreach (var thrown in remote.Thrown.Values)
        {
            if (!thrown.Seen)
                thrown.Track.Reset();
        }

        // A held object.
        if ((bytes[3] & 64) != 0 && bytes.Length >= at + 8 + 15)
        {
            var id = BitConverter.ToUInt64(bytes, at);
            if (id != remote.GrabId || !remote.Grabbing)
            {
                remote.GrabTrack.Reset();
                remote.GrabItem = null;
            }

            at = ReadHand(bytes, at + 8, remote.GrabTrack, now);
            remote.GrabRight = remote.GrabTrack.Side == 1;
            remote.GrabId = id;
            remote.Grabbing = true;
        }
        else
        {
            remote.Grabbing = false;
            remote.GrabItem = null;
            remote.GrabTrack.Reset();
        }

        // Physical Washer: their washer on their belt or in the air.
        if ((bytes[3] & 128) != 0 && bytes.Length >= at + 20)
        {
            at = ReadWorld(bytes, at, out var awayPosition, out var awayRotation);
            if (!remote.WasherAway)
                remote.AwayTrack.Reset();
            remote.WasherAway = true;
            remote.AwayTrack.Push(awayPosition, awayRotation, now);
        }
        else
        {
            remote.WasherAway = false;
            remote.AwayTrack.Reset();
        }

        // Items in their hands.
        foreach (var view in remote.Items)
            view.Seen = false;
        if ((bytes[5] & 1) != 0 && bytes.Length > at)
        {
            var count = Math.Min((int)bytes[at++], remote.Items.Length);
            for (var index = 0; index < count && bytes.Length >= at + 4 + 15; index++)
            {
                var id = BitConverter.ToInt32(bytes, at);
                var view = remote.Items[index];
                if (view.Id != id)
                {
                    view.Id = id;
                    view.Model = false;
                    view.Track.Reset();
                }

                at = ReadHand(bytes, at + 4, view.Track, now);
                view.Seen = true;
            }
        }
    }

    // The held object to that player's hand, just before drawing (after the
    // game has put it where its copy of that player would hold it).
    private void PlaceGrabbed(Remote remote, Rig rig)
    {
        if (!remote.Grabbing)
            return;
        var hand = rig.Bones[remote.GrabRight ? RightHandSlot : LeftHandSlot];
        if (hand is null || hand == null)
            return;
        if (remote.GrabItem is null || remote.GrabItem == null)
        {
            remote.GrabItem = null;
            if (Time.unscaledTime < remote.NextGrabLook)
                return;
            remote.NextGrabLook = Time.unscaledTime + 0.25f;
            var manager = Manager();
            var spawned = manager?.SpawnManager?.SpawnedObjects;
            if (spawned is null || !spawned.TryGetValue(remote.GrabId, out var network) || network is null || network == null)
                return;
            remote.GrabItem = network.transform;
            if (grabShownLogs++ < 5)
                log.Msg($"body sync: holding \"{network.name}\" in a player's {(remote.GrabRight ? "right" : "left")} hand");
        }

        OnHand(rig, remote.Character!.transform, remote.GrabTrack, remote.GrabItem);
    }

    private int grabShownLogs;
}
