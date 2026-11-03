using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PowerWashVR.Game;

// Small helpers to build the mod's own scene objects: tokens, labels and the
// laser. Everything is created once and only moved afterwards, so nothing here
// runs per frame except transform writes.
internal static class Props
{
    private static Shader? unlit;
    private static Shader? lit;
    private static TMP_FontAsset? font;
    private static bool fontSearched;
    private static float nextFontSearch;

    internal static int UiLayer = 5;

    // Hide Interaction Hints (VR SETTINGS): no blue guide lines.
    internal static bool HideHints;

    private static Shader? FindShader(ref Shader? cache, params string[] names)
    {
        if (cache is not null && cache != null)
            return cache;

        foreach (var name in names)
        {
            var shader = Shader.Find(name);
            if (shader is not null && shader != null)
            {
                cache = shader;
                return shader;
            }
        }

        return null;
    }

    internal static Material Unlit(Color color)
    {
        var shader = FindShader(ref unlit, "Universal Render Pipeline/Unlit", "UI/Default", "Sprites/Default")
            ?? FindShader(ref lit, "Universal Render Pipeline/Lit");
        var material = shader is null ? new Material(Canvas.GetDefaultCanvasMaterial()) : new Material(shader);
        SetColor(material, color);
        return material;
    }

    // Alpha-blended, unlit: the UI and sprite shaders are always in the build.
    internal static Material Transparent(Color color)
    {
        var shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
        var material = shader is null ? Unlit(color) : new Material(shader);
        SetColor(material, color);
        material.renderQueue = 3000;
        return material;
    }

    // Drawn after everything else and through it: the menu laser must never
    // disappear behind a menu (a dropdown's own canvas sorts above the panel).
    internal static Material Overlay(Color color)
    {
        var material = Transparent(color);
        material.renderQueue = 4900;
        if (material.HasProperty("unity_GUIZTestMode"))
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
        material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
        return material;
    }

    internal static Material Lit(Color color)
    {
        var shader = FindShader(ref lit, "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit");
        if (shader is null)
            return Unlit(color);

        var material = new Material(shader);
        SetColor(material, color);
        return material;
    }

    internal static void SetColor(Material material, Color color)
    {
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
    }

    // A primitive without its collider: the mod's objects must never block
    // the game's physics or its interaction raycasts.
    internal static GameObject Primitive(PrimitiveType type, string name, Transform? parent, Material material)
    {
        var shape = GameObject.CreatePrimitive(type);
        shape.name = name;
        var collider = shape.GetComponent<Collider>();
        if (collider is not null && collider != null)
            UnityEngine.Object.Destroy(collider);

        if (parent is not null)
            shape.transform.SetParent(parent, false);

        var renderer = shape.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return shape;
    }

    // The game's own UI font, borrowed from any text already on screen.
    internal static TMP_FontAsset? Font()
    {
        if (font is not null && font != null)
            return font;

        if (fontSearched && Time.unscaledTime < nextFontSearch)
            return null;

        fontSearched = true;
        nextFontSearch = Time.unscaledTime + 3f;

        var texts = UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None);
        if (texts is null)
            return null;

        for (var index = 0; index < texts.Length; index++)
        {
            var candidate = texts[index]?.font;
            if (candidate is not null && candidate != null)
            {
                font = candidate;
                break;
            }
        }

        return font;
    }

    // A label card: a world-space canvas with a tinted panel and one line of
    // text. Size is in metres.
    internal sealed class Card
    {
        internal readonly GameObject Root;
        internal readonly Image Panel;
        internal readonly TextMeshProUGUI? Text;
        private string shown = "";
        private Color tint;

        internal Card(string name, Transform? parent, Vector2 size, float pixelsPerMetre = 1000f)
        {
            Root = new GameObject(name);
            Root.layer = UiLayer;
            if (parent is not null)
                Root.transform.SetParent(parent, false);

            var canvas = Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = Root.GetComponent<RectTransform>();
            rect.sizeDelta = size * pixelsPerMetre;
            Root.transform.localScale = Vector3.one / pixelsPerMetre;

            var panelObject = new GameObject("Panel");
            panelObject.layer = UiLayer;
            panelObject.transform.SetParent(Root.transform, false);
            Panel = panelObject.AddComponent<Image>();
            Stretch(panelObject.GetComponent<RectTransform>());

            var textObject = new GameObject("Text");
            textObject.layer = UiLayer;
            textObject.transform.SetParent(Root.transform, false);
            try
            {
                Text = textObject.AddComponent<TextMeshProUGUI>();
                var gameFont = Font();
                if (gameFont is not null)
                    Text.font = gameFont;
                Text.alignment = TextAlignmentOptions.Center;
                Text.fontSize = size.y * pixelsPerMetre * 0.45f;
                Text.enableAutoSizing = true;
                Text.fontSizeMin = 8f;
                Text.fontSizeMax = size.y * pixelsPerMetre * 0.5f;
                Text.color = Color.white;
                Text.raycastTarget = false;
                var textRect = textObject.GetComponent<RectTransform>();
                Stretch(textRect);
                textRect.offsetMin = new Vector2(6f, 2f);
                textRect.offsetMax = new Vector2(-6f, -2f);
            }
            catch
            {
                Text = null;
            }

            Panel.raycastTarget = false;
            SetTint(new Color(0.08f, 0.1f, 0.14f, 0.85f));
        }

        internal void SetText(string value)
        {
            if (Text is null || string.Equals(value, shown, StringComparison.Ordinal))
                return;

            shown = value;
            Text.text = value;
        }

        internal void SetTint(Color color)
        {
            if (color == tint)
                return;

            tint = color;
            Panel.color = color;
        }

        internal void SetActive(bool active)
        {
            if (Root.activeSelf != active)
                Root.SetActive(active);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }

    internal static void SetActive(GameObject? target, bool active)
    {
        if (target is not null && target != null && target.activeSelf != active)
            target.SetActive(active);
    }
}
