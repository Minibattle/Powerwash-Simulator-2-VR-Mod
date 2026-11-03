using UnityEngine;

namespace PowerWashVR.Game;

// A blue line out of the free hand's palm while it points at something it can
// pick up or use (Patches aims the game's interaction from that pose). Hidden
// otherwise; one raycast a frame only while a target exists.
internal sealed class PalmLine
{
    private GameObject? line;
    private Transform? lineTransform;
    private bool visible;
    private static readonly Color Blue = new(0.2f, 0.55f, 1f, 1f);

    internal void Update(GameState state)
    {
        var target = Target(state);
        if (target is null)
        {
            Show(false);
            return;
        }

        var origin = Patches.AimPosition;
        var forward = Patches.AimRotation * Vector3.forward;
        var along = Vector3.Dot(target.position - origin, forward);
        var length = Mathf.Clamp(along, 0.05f, 4f);
        if (Physics.Raycast(origin, forward, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore) && hit.distance > 0.02f)
            length = hit.distance;

        Show(true);
        if (lineTransform is null || lineTransform == null)
            return;
        lineTransform.SetPositionAndRotation(origin + forward * (length * 0.5f), Patches.AimRotation);
        lineTransform.localScale = new Vector3(0.004f, 0.004f, length);
    }

    private static Transform? Target(GameState state)
    {
        if (!Patches.AimValid || state.Mode != UiMode.Gameplay || Clipboard.Held)
            return null;
        var manager = Patches.Interaction;
        if (manager is null || manager == null)
            return null;
        try
        {
            var item = manager.m_targetItem;
            return item is null || item == null ? null : item.transform;
        }
        catch
        {
            return null;
        }
    }

    private void Show(bool show)
    {
        if (show == visible && (!show || (line is not null && line != null)))
            return;
        visible = show;
        if (show && (line is null || line == null))
        {
            line = Props.Primitive(PrimitiveType.Cube, "PowerWashVR Palm Line", null, Props.Unlit(Blue));
            UnityEngine.Object.DontDestroyOnLoad(line);
            lineTransform = line.transform;
        }

        Props.SetActive(line, show);
    }
}
