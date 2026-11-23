using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace SystemExplorer.App;

internal static class Motion
{
    private static readonly UISettings UiSettings = new();

    public static bool IsEnabled => UiSettings.AnimationsEnabled;

    public static void AnimateSectionEntrance(UIElement element)
    {
        if (!IsEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0f, 12f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(220);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(180);

        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    public static void AnimateStaggeredChildren(Panel container, int stepMs = 42)
    {
        if (!IsEnabled) return;
        for (int i = 0; i < container.Children.Count; i++)
        {
            var child = container.Children[i];
            if (child.Visibility != Visibility.Visible) continue;
            ElementCompositionPreview.SetIsTranslationEnabled(child, true);
            var visual = ElementCompositionPreview.GetElementVisual(child);
            var compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));
            var delay = TimeSpan.FromMilliseconds(Math.Min(i, 6) * stepMs);

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(0f, 10f, 0f));
            slide.InsertKeyFrame(1f, Vector3.Zero, easing);
            slide.DelayTime = delay;
            slide.Duration = TimeSpan.FromMilliseconds(210);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, easing);
            fade.DelayTime = delay;
            fade.Duration = TimeSpan.FromMilliseconds(170);

            visual.StartAnimation("Translation", slide);
            visual.StartAnimation("Opacity", fade);
        }
    }

    public static void AnimateSubViewSwitch(UIElement element)
    {
        if (!IsEnabled || element.Visibility != Visibility.Visible) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(10f, 0f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(170);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0.25f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(150);

        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    public static void AnimateInspectorReveal(UIElement element)
    {
        if (!IsEnabled || element.Visibility != Visibility.Visible) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.0f, 0.0f), new Vector2(0.0f, 1.0f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(24f, 0f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(260);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0.35f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(200);

        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    public static void StartLivePulse(UIElement halo)
    {
        var visual = ElementCompositionPreview.GetElementVisual(halo);
        if (!IsEnabled)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = 0f;
            return;
        }
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(6f, 6f, 0f);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0.45f);
        opacity.InsertKeyFrame(0.6f, 0.0f);
        opacity.InsertKeyFrame(1f, 0.45f);
        opacity.Duration = TimeSpan.FromMilliseconds(1800);
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.85f, 0.85f, 1f));
        scale.InsertKeyFrame(0.6f, new Vector3(1.85f, 1.85f, 1f));
        scale.InsertKeyFrame(1f, new Vector3(0.85f, 0.85f, 1f));
        scale.Duration = TimeSpan.FromMilliseconds(1800);
        scale.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
    }

    public static void StopLivePulse(UIElement halo)
    {
        var visual = ElementCompositionPreview.GetElementVisual(halo);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 0f;
        visual.Scale = Vector3.One;
    }
}
