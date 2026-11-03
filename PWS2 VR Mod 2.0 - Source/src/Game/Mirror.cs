using System.Collections;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace PowerWashVR.Game;

// The game window shows the headset's right eye.
//
// URP's own mirror pass needs XR shaders that this build of the game does not
// ship, which is why the window froze on the last flat frame. Instead, at the
// end of each frame (after both eyes are drawn), the OpenXR display is asked
// for its mirror blit: the provider's native blit when it offers one, else a
// plain copy of the eye texture onto the back buffer. One blit, no second
// render of the scene.
internal sealed class Mirror
{
    private readonly MelonLogger.Instance log;
    private CommandBuffer? buffer;
    private object? routine;
    private bool failed;
    private bool loggedRoute;

    internal XRDisplaySubsystem? Display;

    internal Mirror(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal void Start()
    {
        buffer ??= new CommandBuffer { name = "PowerWashVR Mirror" };

        // Ask the pipeline to mirror the right eye itself as well; where its
        // mirror shader exists, that is the cheapest route of all.
        try
        {
            Display?.SetPreferredMirrorBlitMode(XRMirrorViewBlitMode.RightEye);
            UnityEngine.Experimental.Rendering.XRSystem.SetMirrorViewMode(XRMirrorViewBlitMode.RightEye);
        }
        catch (Exception exception)
        {
            log.Msg($"mirror: preferred mode not set ({exception.GetType().Name})");
        }

        if (routine is null)
            routine = MelonCoroutines.Start(EndOfFrame());
    }

    private IEnumerator EndOfFrame()
    {
        var wait = new WaitForEndOfFrame();
        while (!failed)
        {
            yield return wait;
            if (!loggedRoutine)
            {
                loggedRoutine = true;
                log.Msg("mirror: end-of-frame hook running");
            }

            Blit();
        }
    }

    private bool loggedRoutine;
    private string lastRoute = "";
    private int lastFrame = -1;
    private int refusals;

    // Also called after the eye camera has rendered (Patches), in case the
    // end-of-frame hook never runs in this game.
    // Once the end-of-frame hook is known to run, this does nothing: a copy
    // to the back buffer in the middle of the frame's rendering costs a stall.
    internal void AfterEyeCamera()
    {
        if (!loggedRoutine)
            Blit();
    }

    // While the smooth camera is on, its texture is shown instead of the eye.
    internal Func<RenderTexture?>? Override;

    private void Blit()
    {
        var display = Display;
        if (display is null || !display.running || buffer is null || Time.frameCount == lastFrame)
            return;

        try
        {
            buffer.Clear();
            var route = "";
            var mode = XRMirrorViewBlitMode.RightEye;

            var smooth = Override?.Invoke();
            if (smooth is not null && smooth != null)
            {
                buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                buffer.Blit(new RenderTargetIdentifier(smooth), new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                route = "smooth camera";
            }
            else if (display.GetMirrorViewBlitDesc(null, out var desc, mode) && desc.nativeBlitAvailable)
            {
                display.AddGraphicsThreadMirrorViewBlit(buffer, desc.nativeBlitInvalidStates, mode);
                route = "native mirror blit";
            }
            else
            {
                // The eye buffer itself: the last render pass is the right eye
                // in multi-pass (single-pass has one pass with two slices).
                var passes = display.GetRenderPassCount();
                RenderTexture? eye = null;
                if (passes > 0)
                    eye = display.GetRenderTextureForRenderPass(passes - 1);

                if (eye is null || eye == null)
                {
                    if (++refusals == 120)
                        log.Warning($"mirror: no eye buffer to copy ({passes} render pass(es)); the window will not update");
                    return;
                }

                var slice = passes == 1 && eye.volumeDepth > 1 ? 1 : 0;
                buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                // The window shows the middle of the eye at the window's own
                // shape (cropped, never stretched). Eye buffers are stored
                // bottom-up relative to the window: flipped.
                var windowAspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
                var eyeAspect = eye.height > 0 ? (float)eye.width / eye.height : 1f;
                var sx = 1f;
                var sy = 1f;
                if (windowAspect > eyeAspect)
                    sy = eyeAspect / windowAspect;
                else
                    sx = windowAspect / eyeAspect;

                buffer.Blit(new RenderTargetIdentifier(eye), new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget),
                    new Vector2(sx, -sy), new Vector2((1f - sx) * 0.5f, (1f + sy) * 0.5f), slice, 0);
                route = $"eye buffer copy, pass {passes - 1} of {passes}";
            }

            lastFrame = Time.frameCount;
            Graphics.ExecuteCommandBuffer(buffer);
            Note(route);
            if (route != lastRoute)
            {
                if (lastRoute.Length > 0)
                    log.Msg($"mirror: window now shows the {(route == "smooth camera" ? "smooth camera" : "right eye")}");
                lastRoute = route;
            }
        }
        catch (Exception exception)
        {
            failed = true;
            log.Warning($"mirror: {exception.GetType().Name}: {exception.Message} - the window will not update");
        }
    }

    private void Note(string route)
    {
        if (loggedRoute)
            return;

        loggedRoute = true;
        log.Msg($"mirror: right eye to the window ({route})");
    }
}
