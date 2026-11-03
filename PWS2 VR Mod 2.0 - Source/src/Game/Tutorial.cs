using MelonLoader;
using UnityEngine;

namespace PowerWashVR.Game;

// The VR tutorial hints, once per start of the game (Disable Tutorials in VR
// SETTINGS turns them off). After the first level load: a line out of each
// shoulder to a card - grab there for the backpack / the clipboard - each
// gone once done; then a line from the face for the dirt scan, gone once a
// scan is done.
internal sealed class Tutorial
{
    private readonly MelonLogger.Instance log;
    private readonly BodyFrame body;

    private sealed class Hint
    {
        internal GameObject? Line;
        internal Props.Card? Card;
        internal bool Done, Shown;
    }

    private readonly Hint backpack = new(), clipboard = new(), scan = new();
    private bool started;
    private int scansAtStart = -1;

    internal Tutorial(MelonLogger.Instance log, BodyFrame body)
    {
        this.log = log;
        this.body = body;
    }

    private bool finished, hiddenWhileOff;

    internal void Update(PlayerRig rig, GameState state, Settings settings, bool caseHeld)
    {
        // All hints done: nothing more to do for this run of the game.
        if (finished)
            return;
        if (backpack.Done && clipboard.Done && scan.Done)
        {
            finished = true;
            return;
        }

        // Switched off: hidden once, then nothing each frame.
        if (settings.DisableTutorials)
        {
            if (!hiddenWhileOff)
            {
                hiddenWhileOff = true;
                Hide(backpack);
                Hide(clipboard);
                Hide(scan);
            }

            return;
        }

        hiddenWhileOff = false;
        var playing = state.Mode == UiMode.Gameplay && rig.IsPlayer && rig.HasPose && !UiPresenter.VoidActive;
        // Only on loading into a level (the hub does not count).
        if (!started && playing && PlayerRig.FirstRecenterApplied && !state.InHub)
        {
            started = true;
            log.Msg("tutorial: shoulder hints shown");
        }

        // No backpack in VR (its setting off): nothing to show for it.
        if (!settings.Backpack && !backpack.Done)
            backpack.Done = true;
        if (caseHeld && !backpack.Done)
            Finish(backpack, "backpack");
        if (Clipboard.Held && !clipboard.Done)
            Finish(clipboard, "clipboard");

        var show = started && playing && !settings.DisableTutorials;
        var shouldersDone = backpack.Done && clipboard.Done;
        if (shouldersDone && scansAtStart < 0)
            scansAtStart = GameInput.Scans;
        if (shouldersDone && !scan.Done && GameInput.Scans > scansAtStart)
            Finish(scan, "dirt scan");

        // Backpack: beside the free hand's ear; clipboard: the washer hand's.
        var freeSide = settings.LeftHanded ? 1f : -1f;
        Place(backpack, rig, show && !backpack.Done, new Vector3(0.15f * freeSide, -0.04f, -0.05f),
            new Vector3(0.32f * freeSide, 0.02f, 0.38f), "Grab here (behind your shoulder) to take out the backpack");
        Place(clipboard, rig, show && !clipboard.Done, new Vector3(-0.15f * freeSide, -0.04f, -0.05f),
            new Vector3(-0.32f * freeSide, 0.02f, 0.38f), "Grab here (behind your shoulder) to take out the clipboard");
        var frame = PowerWashVR.OpenXR.Bindings.IsFrame;
        var scanText = frame
            ? $"Press the {(settings.LeftHanded ? "right" : "left")} bumper to scan for dirt"
            : "Bring your hand up to your face and pull the trigger to scan for dirt";
        Place(scan, rig, show && shouldersDone && !scan.Done, new Vector3(0.08f * freeSide, -0.06f, 0.12f),
            new Vector3(0.2f * freeSide, -0.12f, 0.42f), scanText);
    }

    private static void Hide(Hint hint)
    {
        Props.SetActive(hint.Line, false);
        hint.Card?.SetActive(false);
    }

    private void Finish(Hint hint, string what)
    {
        hint.Done = true;
        Props.SetActive(hint.Line, false);
        hint.Card?.SetActive(false);
        log.Msg($"tutorial: {what} done");
    }

    // A line from a point by the head (body frame) out to a card facing the eyes.
    private void Place(Hint hint, PlayerRig rig, bool visible, Vector3 from, Vector3 to, string text)
    {
        if (!visible)
        {
            if (hint.Shown)
            {
                hint.Shown = false;
                Hide(hint);
            }

            return;
        }

        hint.Shown = true;

        if (hint.Line is null || hint.Line == null)
        {
            hint.Line = Props.Primitive(PrimitiveType.Cube, "PowerWashVR Tutorial Line", null,
                Props.Transparent(new Color(1f, 1f, 1f, 0.6f)));
            UnityEngine.Object.DontDestroyOnLoad(hint.Line);
        }

        if (hint.Card is null || hint.Card.Root == null)
        {
            hint.Card = new Props.Card("PowerWashVR Tutorial", null, new Vector2(0.24f, 0.06f));
            UnityEngine.Object.DontDestroyOnLoad(hint.Card.Root);
        }

        hint.Card.SetText(text);
        Props.SetActive(hint.Line, true);
        hint.Card.SetActive(true);

        var toWorld = rig.TrackingToWorldRotation;
        var bodyRotation = body.Rotation;
        var head = rig.HeadPosition;
        var start = rig.TrackingToWorldPosition + toWorld * (head + bodyRotation * from);
        var end = rig.TrackingToWorldPosition + toWorld * (head + bodyRotation * to);
        var span = end - start;
        var length = span.magnitude;
        if (length > 1e-3f)
        {
            var line = hint.Line.transform;
            line.SetPositionAndRotation(start + span * 0.5f, Quaternion.LookRotation(span / length));
            line.localScale = new Vector3(0.004f, 0.004f, length);
        }

        var eyes = rig.TrackingToWorldPosition + toWorld * head;
        var card = hint.Card.Root.transform;
        card.position = end;
        var facing = card.position - eyes;
        if (facing.sqrMagnitude > 1e-6f)
            card.rotation = Quaternion.LookRotation(facing, toWorld * Vector3.up);
    }
}
