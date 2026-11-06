using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Services;
using enki.Views;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class MotionUiTests
{
    public MotionUiTests() => HeadlessApp.EnsureInitialized();

    [Fact]
    public void MotionMode_UpdatesExistingTransitionsAndKeepsToggleLayoutFixed()
    {
        MotionService.Apply(false);
        var toggle = new ToggleSwitch();
        var window = new Window { Content = toggle };
        window.Show();
        try
        {
            window.UpdateLayout();
            var knob = toggle.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "SwitchKnob");
            var transition = Assert.Single(knob.Transitions!.OfType<TransformOperationsTransition>());
            Assert.Equal(TimeSpan.FromMilliseconds(150), transition.Duration);
            var margin = knob.Margin;
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(margin, knob.Margin);
            MotionService.Apply(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TimeSpan.FromMilliseconds(90), transition.Duration);
            Assert.DoesNotContain(knob.Transitions!, t => t is ThicknessTransition);
        }
        finally { window.Close(); MotionService.Apply(false); }
    }

    [Fact]
    public void Reveal_RapidNavigationAndDetach_RestoreVisibleContent()
    {
        MotionService.Apply(false);
        var content = new Border { Width = 200, Height = 100, Background = Brushes.White };
        var window = new Window { Content = content };
        window.Show();
        try
        {
            MotionReveal.SetRevision(content, 1);
            MotionReveal.SetRevision(content, 2);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, content.Opacity);
            window.Content = content;
            MotionReveal.SetRevision(content, 3);
            Dispatcher.UIThread.RunJobs();
            content.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, content.Opacity);
            content.IsVisible = true;
            window.UpdateLayout();
            Assert.True(content.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }
}
