using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static void CheckPickerSizeInteraction(FrameworkElement picker, int maximum)
    {
        var header = (FrameworkElement)Field(picker, "_header");
        var slider = VisualDescendants(header).OfType<Slider>().Single();
        var number = VisualDescendants(header).OfType<TextBox>().Single();
        var toggle = VisualDescendants(header).OfType<CheckBox>().Single();
        double previous = slider.Value;
        bool? wasEnabled = toggle.IsChecked;
        try
        {
            CheckPickerHeaderRendering(picker);
            MovePickerPointer(slider, new Point(slider.ActualWidth * .75, slider.ActualHeight / 2));
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            Call(slider, "OnPreviewMouseLeftButtonDown", click); Pump();
            Assert(click.Handled && slider.Value > previous,
                "clicking the size rail moves directly to that position");
            double clicked = slider.Value;
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(-track.ActualWidth / 4, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(-track.ActualWidth / 4, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Pump();
            Assert(slider.Value < clicked && number.Text == ((int)slider.Value).ToString(CultureInfo.InvariantCulture),
                "dragging the size handle updates both the slider and numeric field");
            number.Text = maximum.ToString(CultureInfo.InvariantCulture); Pump();
            Assert(slider.Value == maximum && toggle.IsChecked == true,
                "entering a valid size updates the slider and enables the effect");
            number.Text = ""; number.Text = (maximum + 1).ToString(CultureInfo.InvariantCulture); Pump();
            Assert(slider.Value == maximum, "incomplete and out-of-range sizes preserve the last valid output");
            number.Text = maximum.ToString(CultureInfo.InvariantCulture); Pump();
            CheckPickerHeaderRendering(picker);
        }
        finally
        {
            number.Text = ((int)previous).ToString(CultureInfo.InvariantCulture);
            toggle.IsChecked = wasEnabled;
            MovePickerPointer(picker, new Point(2, 2));
            Pump();
        }
    }

    private static void CheckPickerHeaderRendering(FrameworkElement picker)
    {
        var header = (FrameworkElement)Field(picker, "_header");
        var slider = VisualDescendants(header).OfType<Slider>().Single();
        var number = VisualDescendants(header).OfType<TextBox>().Single();
        var image = new RenderTargetBitmap((int)Math.Ceiling(slider.ActualWidth),
            (int)Math.Ceiling(slider.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(slider);
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect((int)(image.PixelWidth * .85), image.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Assert(pixel[3] >= 30, "the size rail paints visible pixels away from its handle");
        Assert(number.Focus(), $"the numeric size field accepts keyboard focus (visible={number.IsVisible}, enabled={number.IsEnabled}, source={PresentationSource.FromVisual(number) is not null})");
        Pump();
        MovePickerPointer(number, new Point(number.ActualWidth / 2, number.ActualHeight / 2));
        number.UpdateLayout();
        var visibleButtons = VisualDescendants(number).OfType<Button>().Where(button => button.IsVisible).ToArray();
        Assert(number.IsMouseOver && number.IsKeyboardFocusWithin
            && visibleButtons.Length == 0,
            $"hovering and focusing the size input never covers its text with a clear button (hover={number.IsMouseOver}, focus={number.IsKeyboardFocusWithin}, buttons={string.Join(',', visibleButtons.Select(button => button.Name))})");
        var first = number.GetRectFromCharacterIndex(0);
        var last = number.GetRectFromCharacterIndex(number.Text.Length - 1, trailingEdge: true);
        Assert(!first.IsEmpty && !last.IsEmpty && first.Left >= 0 && last.Right <= number.ActualWidth
            && first.Top >= 0 && last.Bottom <= number.ActualHeight,
            "all size digits remain inside the visible text input");
    }

    private static void CheckPickerPaletteLayout(FrameworkElement picker)
    {
        var palette = (Panel)Field(picker, "_standardColorsPanel");
        var swatches = palette.Children.Cast<FrameworkElement>().ToArray();
        Rect Bounds(FrameworkElement swatch) => swatch.TransformToAncestor(palette).TransformBounds(new Rect(swatch.RenderSize));
        var first = Bounds(swatches[0]);
        Assert(swatches.Length == 12 && swatches.All(swatch => Math.Abs(Bounds(swatch).Top - first.Top) <= .5
            && Bounds(swatch).Left >= 0 && Bounds(swatch).Right <= palette.ActualWidth + .5
            && swatch.ActualWidth >= 16 && swatch.ActualHeight >= 16)
            && ((Border)swatches[^1]).Background is SolidColorBrush brush && brush.Color == Colors.Black,
            "all twelve standard colors, including black, fit one unclipped row");
    }
}
