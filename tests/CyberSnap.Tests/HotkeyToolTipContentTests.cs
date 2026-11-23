using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using CyberSnap.Helpers;

namespace CyberSnap.Tests;

/// <summary>
/// The tooltip helper has to draw the same keycap whether the localized text still
/// carries the shortcut as a trailing parenthetical or not, and it must never leave
/// an empty label or a stray gap in front of the badge.
/// </summary>
public class HotkeyToolTipContentTests
{
    [Fact]
    public void ExtractsTrailingParentheticalIntoKeycap()
    {
        OnSta(() =>
        {
            // The parenthesis alone is enough: the fallback shortcut is null so the
            // tooltip cannot silently depend on it.
            var content = HotkeyToolTipContent.WithShortcutHint("Copy and close (Ctrl+Enter)", shortcut: null);

            var panel = Assert.IsType<StackPanel>(content);
            Assert.Equal(Orientation.Horizontal, panel.Orientation);
            Assert.Equal(2, panel.Children.Count);

            var label = Assert.IsType<TextBlock>(panel.Children[0]);
            Assert.Equal("Copy and close", label.Text);

            var badge = Assert.IsType<Border>(panel.Children[1]);
            var shortcut = Assert.IsType<TextBlock>(badge.Child);
            Assert.Equal("Ctrl+Enter", shortcut.Text);
        });
    }

    [Fact]
    public void UsesFallbackShortcutWhenTranslationDropsParenthetical()
    {
        OnSta(() =>
        {
            const string label = "Copy the text and keep this window open.";

            var content = HotkeyToolTipContent.WithShortcutHint(label, "Ctrl+Shift+C");

            var panel = Assert.IsType<StackPanel>(content);
            Assert.Equal(2, panel.Children.Count);

            var labelText = Assert.IsType<TextBlock>(panel.Children[0]);
            Assert.Equal(label, labelText.Text);

            var badge = Assert.IsType<Border>(panel.Children[1]);
            var shortcut = Assert.IsType<TextBlock>(badge.Child);
            Assert.Equal("Ctrl+Shift+C", shortcut.Text);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLabelIsOmittedInsteadOfLeavingAGapBeforeTheBadge(string blank)
    {
        OnSta(() =>
        {
            foreach (FrameworkElement content in new[]
                     {
                         HotkeyToolTipContent.WithShortcut(blank, "Ctrl+C"),
                         HotkeyToolTipContent.WithShortcutHint(blank, "Ctrl+C")
                     })
            {
                var panel = Assert.IsType<StackPanel>(content);

                // Only the badge remains, so its own left margin cannot show up as an
                // empty gap at the start of the tooltip.
                var badge = Assert.IsType<Border>(Assert.Single(panel.Children));
                var shortcut = Assert.IsType<TextBlock>(badge.Child);
                Assert.Equal("Ctrl+C", shortcut.Text);
                Assert.NotEmpty(shortcut.Text);
            }
        });
    }

    private static void OnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured != null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }
}
