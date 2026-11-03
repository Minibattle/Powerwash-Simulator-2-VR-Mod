using Il2CppFuturLab.PW2.UI.Widgets.HUD;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace PowerWashVR.Game;

// The game's task finder: when only a few things are left to clean, it marks
// the nearest one with an icon on the flat screen. In VR that screen is not
// shown, so the same icon is drawn in the world over that part, always on top
// and the same size at any distance. The game still decides when it shows and
// which part it marks (its widget's visibility and target are read).
internal sealed class TaskMarker
{
    private readonly MelonLogger.Instance log;
    private TaskFinderWidget? widget;
    private float nextSearch;
    private GameObject? root;
    private Transform? rootTransform;
    private Image? image;
    private IntPtr targetFor, spriteFor;
    private Vector3 targetCentre;
    private bool shown;
    private int logs;

    internal TaskMarker(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal void Update(PlayerRig rig, GameState state)
    {
        var target = Target(state);
        if (target is null || rig.CameraTransform is null)
        {
            Show(false);
            return;
        }

        if (target.Pointer != targetFor)
        {
            targetFor = target.Pointer;
            targetCentre = Centre(target);
            if (logs++ < 10)
                log.Msg($"task marker: on \"{target.name}\"");
        }

        Ensure();
        Sprite();
        Show(true);
        Face(rig);
    }

    // Just before drawing: turned to the head again.
    internal void Reapply(PlayerRig rig)
    {
        if (shown)
            Face(rig);
    }

    private Transform? Target(GameState state)
    {
        if (state.Mode != UiMode.Gameplay)
            return null;
        try
        {
            if ((widget is null || widget == null) && Time.unscaledTime >= nextSearch)
            {
                nextSearch = Time.unscaledTime + 1f;
                widget = UnityEngine.Object.FindObjectOfType<TaskFinderWidget>();
            }

            if (widget is null || widget == null)
                return null;
            var group = widget.m_canvasGroup;
            if (group is not null && group != null && group.alpha < 0.01f)
                return null;
            var marker = widget.m_marker;
            var washTarget = marker is null || marker == null ? null : marker.m_target;
            if (washTarget is null || washTarget == null || !washTarget.gameObject.activeInHierarchy)
                return null;
            return washTarget.transform;
        }
        catch
        {
            return null;
        }
    }

    // The middle of the part's renderers (its pivot may be anywhere).
    private static Vector3 Centre(Transform target)
    {
        try
        {
            var renderers = target.GetComponentsInChildren<Renderer>(false);
            var any = false;
            var bounds = new Bounds();
            for (var index = 0; renderers is not null && index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                if (renderer is null || renderer == null || !renderer.enabled)
                    continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (any)
                return bounds.center;
        }
        catch
        {
            // The pivot then.
        }

        return target.position;
    }

    private void Ensure()
    {
        if (root is not null && root != null)
            return;

        root = new GameObject("PowerWashVR Task Marker");
        root.layer = Props.UiLayer;
        UnityEngine.Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 20;
        var rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(100f, 100f);
        rootTransform = root.transform;

        var icon = new GameObject("Icon");
        icon.layer = Props.UiLayer;
        icon.transform.SetParent(rootTransform, false);
        image = icon.AddComponent<Image>();
        image.raycastTarget = false;
        image.preserveAspect = true;
        image.color = new Color(1f, 0.55f, 0.1f, 1f);
        var iconRect = icon.GetComponent<RectTransform>();
        iconRect.sizeDelta = new Vector2(100f, 100f);
        // Drawn over walls and dirt alike, like the game's flat marker.
        try
        {
            var material = new Material(Graphic.defaultGraphicMaterial);
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            image.material = material;
        }
        catch
        {
            // Depth-tested then.
        }

        root.SetActive(false);
    }

    // The game's own icon (the furniture one for furniture).
    private void Sprite()
    {
        try
        {
            var source = widget?.m_marker?.m_markerIcon;
            var sprite = source is null || source == null ? null : source.sprite;
            if (image is null || sprite is null || sprite == null || sprite.Pointer == spriteFor)
                return;
            spriteFor = sprite.Pointer;
            image.sprite = sprite;
            image.color = source!.color.a > 0.01f ? new Color(source.color.r, source.color.g, source.color.b, 1f) : Color.white;
        }
        catch
        {
            // The plain square then.
        }
    }

    private void Face(PlayerRig rig)
    {
        var head = rig.CameraTransform;
        if (rootTransform is null || head is null)
            return;
        var position = targetCentre;
        var toHead = head.position - position;
        var distance = Mathf.Max(0.3f, toHead.magnitude);
        // About 3 degrees across wherever it is.
        var size = distance * 0.055f;
        rootTransform.SetPositionAndRotation(position, Quaternion.LookRotation(-toHead.normalized, Vector3.up));
        rootTransform.localScale = Vector3.one * (size / 100f);
    }

    private void Show(bool show)
    {
        if (show == shown)
            return;
        shown = show;
        if (root is not null && root != null)
            root.SetActive(show);
        if (!show)
            targetFor = IntPtr.Zero;
    }
}
