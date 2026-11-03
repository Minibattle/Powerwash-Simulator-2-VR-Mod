using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace PowerWashVR.Game;

// Furniture placement in the hub, VR style.
//
// Holding the clipboard in the hub is the game's placement mode: the free hand
// grabs and moves furniture like any other object. The board shows the game's
// furniture limit (top left) and its furniture-storage icon (middle); pointing
// the free hand's laser at that icon and pulling the trigger opens the storage
// and puts the clipboard away. From there on ("No Clipboard Mode") placement
// mode stays on without the clipboard: an item taken from the storage is held
// by the free hand, and placing it - or pausing, which stores an unplaced
// item - ends it.
internal sealed class HubPlacement
{
    private readonly MelonLogger.Instance log;

    internal HubPlacement(MelonLogger.Instance log)
    {
        this.log = log;
    }

    // No Clipboard Mode (read by the pause handling in GameInput).
    internal static bool NoClipboard { get; private set; }

    private float nextToggle, nextHeldCheck;
    private bool holdingCached;
    private bool heldInNoClipboard;
    private int logs;

    // The two HUD widgets, borrowed onto the board while it is held.
    private sealed class Borrowed
    {
        internal RectTransform? Node;
        internal Transform? Parent;
        internal int Sibling;
        internal Vector3 Position, Scale;
        internal Quaternion Rotation;
        internal Vector2 AnchorMin, AnchorMax, Pivot, Anchored;
    }

    private readonly Borrowed limit = new();
    private float nextWidgetSearch, nextFit;

    // The storage button: built on the board (the game's indicator widget is a
    // whole-screen layer, its icon one small corner of it), using its icon.
    private GameObject? button;
    private RectTransform? buttonRect;
    private Image? buttonBack;
    private Sprite? icon;
    private bool iconSearched, hovered;

    internal void Update(GameState state, GameInput input, Clipboard clipboard)
    {
        var name = state.StateName;
        var storageOpen = state.InFurnitureStorage;
        var hub = state.InHub || storageOpen;
        var placing = state.InPlacement;
        var gameInput = state.Input;

        if (!hub)
        {
            if (NoClipboard)
                End("left the hub");
            GiveBack();
            return;
        }

        // What the hand holds, read ten times a second (not every frame).
        if (Time.unscaledTime >= nextHeldCheck)
        {
            nextHeldCheck = Time.unscaledTime + 0.1f;
            holdingCached = HeldItem(state);
        }

        var holding = holdingCached;

        // No Clipboard Mode: an item taken from the storage, then let go of
        // (placed), ends it.
        if (NoClipboard && !storageOpen)
        {
            if (holding)
                heldInNoClipboard = true;
            else if (heldInNoClipboard)
                End("item placed");
        }

        var want = (Clipboard.Held && state.Mode == UiMode.Gameplay) || NoClipboard;
        if (gameInput is not null && Time.unscaledTime >= nextToggle && !storageOpen)
        {
            // In and out of the game's placement mode as the held furniture
            // inventory button does it.
            if (want && !placing && state.InHub && name.Contains("HubGameplay", StringComparison.Ordinal))
            {
                nextToggle = Time.unscaledTime + 1f;
                gameInput.InvokeHeldFurnitureInventoryButton();
                Note("placement mode asked for (clipboard out)");
            }
            else if (!want && placing && !holding)
            {
                nextToggle = Time.unscaledTime + 1f;
                gameInput.InvokeHeldFurnitureInventoryButton();
                Note("placement mode left");
            }
        }

        // The board's furniture widgets, and the storage icon's click.
        if (Clipboard.Held && placing)
        {
            Borrow(state);
            var over = Pointed(buttonRect);
            if (over != hovered && buttonBack is not null && buttonBack != null)
            {
                hovered = over;
                buttonBack.color = over ? ButtonHover : ButtonColor;
            }

            if (input.FreeTrigger.Pressed && over && gameInput is not null)
            {
                gameInput.InvokeToggleFurnitureInventory();
                NoClipboard = true;
                heldInNoClipboard = false;
                GiveBack();
                clipboard.PutAway();
                Note("furniture storage opened from the clipboard; No Clipboard Mode on");
            }
        }
        else
        {
            GiveBack();
        }
    }

    // Pausing in No Clipboard Mode: the item in hand (not placed) is stored.
    internal static bool PauseStores(Il2CppFuturLab.PW2.PwsPlayerInput? input, bool holding)
    {
        if (!NoClipboard)
            return false;
        try
        {
            if (holding)
                input?.InvokeItemInteraction(Il2CppFuturLab.PW2.ItemInteraction.Remove);
        }
        catch
        {
        }

        NoClipboard = false;
        return true;
    }

    private void End(string why)
    {
        NoClipboard = false;
        heldInNoClipboard = false;
        Note($"No Clipboard Mode off ({why})");
    }

    private void Note(string message)
    {
        if (logs++ < 60)
            log.Msg($"placement: {message}");
    }

    private static bool HeldItem(GameState state)
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

    // The free hand's laser (the interaction aim) on a widget's rectangle.
    private static bool Pointed(RectTransform? node)
    {
        if (node is null || node == null || !Patches.AimValid || !node.gameObject.activeInHierarchy)
            return false;
        var direction = Patches.AimRotation * Vector3.forward;
        var plane = new Plane(node.forward, node.position);
        var ray = new Ray(Patches.AimPosition, direction);
        if (!plane.Raycast(ray, out var distance) || distance > 3f)
            return false;
        var local = node.InverseTransformPoint(ray.GetPoint(distance));
        return node.rect.Contains(new Vector2(local.x, local.y));
    }

    private void Borrow(GameState state)
    {
        onBoard = true;
        var board = Clipboard.Canvas;
        if (board is null || board == null)
            return;
        if ((limit.Node is null || limit.Node == null) && Time.unscaledTime >= nextWidgetSearch)
        {
            nextWidgetSearch = Time.unscaledTime + 2f;
            Find<Il2CppFuturLab.PW2.UI.Widgets.FurnitureInventoryLimitWidget>(limit);
        }

        // Top left: the limit; the middle: the storage icon.
        Put(limit, board.transform, new Vector2(0f, 1f), new Vector2(50f, -70f), 120f);
        if (Time.unscaledTime >= nextFit)
        {
            nextFit = Time.unscaledTime + 1f;
            Fit(limit.Node, board.transform.TryCast<RectTransform>());
        }

        Button(state, board.transform);
    }

    private static readonly Color ButtonColor = new(0.1f, 0.18f, 0.3f, 0.95f);
    private static readonly Color ButtonHover = new(0.2f, 0.45f, 0.75f, 1f);

    private void Button(GameState state, Transform board)
    {
        if (button is null || button == null)
        {
            if (!iconSearched)
            {
                iconSearched = true;
                icon = StorageIcon();
            }

            button = new GameObject("PowerWashVR Furniture Storage");
            button.layer = Props.UiLayer;
            buttonRect = button.AddComponent<RectTransform>();
            buttonRect.SetParent(board, false);
            buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.sizeDelta = new Vector2(300f, 300f);
            // Over the list, whatever order its canvases sort themselves in.
            var canvas = button.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 200;
            buttonBack = button.AddComponent<Image>();
            buttonBack.color = ButtonColor;
            buttonBack.raycastTarget = false;
            hovered = false;

            var picture = new GameObject("Icon");
            picture.layer = Props.UiLayer;
            var pictureRect = picture.AddComponent<RectTransform>();
            pictureRect.SetParent(buttonRect, false);
            pictureRect.anchorMin = pictureRect.anchorMax = pictureRect.pivot = new Vector2(0.5f, 0.5f);
            pictureRect.anchoredPosition = new Vector2(0f, 30f);
            pictureRect.sizeDelta = new Vector2(170f, 170f);
            var image = picture.AddComponent<Image>();
            image.raycastTarget = false;
            if (icon is not null && icon != null)
            {
                image.sprite = icon;
                image.preserveAspect = true;
                image.color = Color.white;
            }
            else
            {
                image.enabled = false;
            }

            var label = new GameObject("Label");
            label.layer = Props.UiLayer;
            var labelRect = label.AddComponent<RectTransform>();
            labelRect.SetParent(buttonRect, false);
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 0f);
            labelRect.pivot = new Vector2(0.5f, 0f);
            labelRect.anchoredPosition = new Vector2(0f, 14f);
            labelRect.sizeDelta = new Vector2(-20f, 80f);
            var text = label.AddComponent<TextMeshProUGUI>();
            if (Props.Font() is { } font)
                text.font = font;
            text.text = icon is null || icon == null ? "Furniture\nStorage" : "Furniture Storage";
            text.fontSize = icon is null || icon == null ? 48f : 30f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;
            Note($"storage button built on the clipboard (icon {(icon is null || icon == null ? "not found" : $"\"{icon.name}\"")})");
        }

        if (button.transform.parent is null || button.transform.parent.Pointer != board.Pointer)
            button.transform.SetParent(board, false);
        if (!button.activeSelf)
            button.SetActive(true);
    }

    // The storage icon among the indicator widget's pictures (not a key glyph
    // of its prompts).
    private Sprite? StorageIcon()
    {
        try
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.UI.Widgets.FurnitureInventoryIndicatorWidget>());
            var widget = found is null || found.Length == 0 ? null : found[0]?.TryCast<Il2CppFuturLab.PW2.UI.Widgets.FurnitureInventoryIndicatorWidget>();
            if (widget is null)
                return null;
            Sprite? best = null;
            var bestScore = int.MinValue;
            var seen = new List<string>();
            foreach (var image in widget.GetComponentsInChildren<Image>(true))
            {
                var sprite = image.sprite;
                if (sprite is null || sprite == null)
                    continue;
                var rect = image.rectTransform.rect;
                var spriteName = sprite.name ?? "";
                var nodeName = image.name ?? "";
                if (seen.Count < 24)
                    seen.Add($"{nodeName}[{spriteName}] {rect.width:0}x{rect.height:0}");
                if (image.GetComponentInParent<Il2CppFuturLab.PW2.UI.Buttons.ContextButtons.ContextButton>(true) is not null)
                    continue;
                if (rect.width < 32f || rect.width > 700f || rect.height > 700f)
                    continue;
                var both = (nodeName + " " + spriteName).ToLowerInvariant();
                var score = 0;
                if (both.Contains("icon"))
                    score += 4;
                if (both.Contains("furniture") || both.Contains("inventory") || both.Contains("storage") || both.Contains("box"))
                    score += 3;
                if (both.Contains("bg") || both.Contains("background") || both.Contains("glow") || both.Contains("shadow")
                    || both.Contains("frame") || both.Contains("outline") || both.Contains("fill") || both.Contains("radial"))
                    score -= 5;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = sprite;
                }
            }

            Note($"storage widget pictures: {string.Join("; ", seen)}");
            return best;
        }
        catch (Exception exception)
        {
            Note($"storage icon not found ({exception.GetType().Name})");
            return null;
        }
    }

    // Keeps a widget (with everything it draws) inside the board's edges.
    private static void Fit(RectTransform? node, RectTransform? board)
    {
        if (node is null || node == null || board is null || board == null)
            return;
        var corners = new Vector3[4];
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var part in node.GetComponentsInChildren<RectTransform>(false))
        {
            if (part.rect.width <= 0f || part.rect.width > 3000f)
                continue;
            part.GetWorldCorners(corners);
            foreach (var corner in corners)
            {
                var local = (Vector2)board.InverseTransformPoint(corner);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }
        }

        if (min.x > max.x)
            return;
        const float margin = 20f;
        var area = board.rect;
        var shift = Vector2.zero;
        if (min.x < area.xMin + margin)
            shift.x = area.xMin + margin - min.x;
        else if (max.x > area.xMax - margin)
            shift.x = area.xMax - margin - max.x;
        if (max.y > area.yMax - margin)
            shift.y = area.yMax - margin - max.y;
        else if (min.y < area.yMin + margin)
            shift.y = area.yMin + margin - min.y;
        if (shift.sqrMagnitude > 1f)
            node.anchoredPosition += shift;
    }

    private void Find<T>(Borrowed slot) where T : Component
    {
        if (slot.Node is not null && slot.Node != null)
            return;
        try
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>());
            if (found is null || found.Length == 0)
                return;
            var component = found[0]?.TryCast<T>();
            var rect = component is null ? null : component.transform.TryCast<RectTransform>();
            if (rect is null)
                return;
            slot.Node = rect;
            slot.Parent = rect.parent;
            slot.Sibling = rect.GetSiblingIndex();
            rect.GetLocalPositionAndRotation(out slot.Position, out slot.Rotation);
            slot.Scale = rect.localScale;
            slot.AnchorMin = rect.anchorMin;
            slot.AnchorMax = rect.anchorMax;
            slot.Pivot = rect.pivot;
            slot.Anchored = rect.anchoredPosition;
            Note($"\"{rect.name}\" ({rect.rect.width:0}x{rect.rect.height:0}) put on the clipboard");
        }
        catch (Exception exception)
        {
            Note($"{typeof(T).Name} not found ({exception.GetType().Name})");
        }
    }

    private static void Put(Borrowed slot, Transform board, Vector2 anchor, Vector2 offset, float width)
    {
        var node = slot.Node;
        if (node is null || node == null)
            return;
        if (node.parent is null || node.parent.Pointer != board.Pointer)
            node.SetParent(board, false);
        node.anchorMin = node.anchorMax = anchor;
        node.pivot = new Vector2(anchor.x, anchor.y);
        node.anchoredPosition = offset;
        node.localRotation = Quaternion.identity;
        var size = Mathf.Max(1f, node.rect.width);
        var scale = width / size;
        if (Mathf.Abs(node.localScale.x - scale) > 1e-4f)
            node.localScale = new Vector3(scale, scale, scale);
    }

    private bool onBoard;

    private void GiveBack()
    {
        // Nothing to hand back unless the board had them (most frames).
        if (!onBoard)
            return;
        onBoard = false;
        if (button is not null && button != null && button.activeSelf)
            button.SetActive(false);
        {
            var slot = limit;
            var node = slot.Node;
            if (node is null || node == null || slot.Parent is null || slot.Parent == null)
            {
                slot.Node = null;
                return;
            }

            if (node.parent is null || node.parent.Pointer != slot.Parent.Pointer)
            {
                node.SetParent(slot.Parent, false);
                node.SetSiblingIndex(slot.Sibling);
                node.anchorMin = slot.AnchorMin;
                node.anchorMax = slot.AnchorMax;
                node.pivot = slot.Pivot;
                node.anchoredPosition = slot.Anchored;
                node.localRotation = slot.Rotation;
                node.localScale = slot.Scale;
            }
        }
    }
}
