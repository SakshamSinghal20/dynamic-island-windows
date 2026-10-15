using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Island.UI;

// Retains Slider's keyboard and UI Automation range semantics, but draws the
// six-point track without laying out a native thumb every display frame.
public sealed class PlaybackSlider : Slider
{
    public event Action? ScrubStarted;
    public event Action<double>? ValueCommitted;
    private bool scrubbing;
    public PlaybackSlider()
    {
        Template = new ControlTemplate(typeof(Slider)); Focusable = true; Cursor = Cursors.Hand;
        Background = Brushes.Transparent; SmallChange = .02; LargeChange = .1;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double y = ActualHeight / 2, width = Math.Max(0, ActualWidth - 10);
        double fraction = Maximum <= Minimum ? 0 : Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        dc.DrawRoundedRectangle(Theme.Control, null, new Rect(5, y - 3, width, 6), 3, 3);
        dc.DrawRoundedRectangle(IsEnabled ? Brushes.White : Theme.Secondary, null, new Rect(5, y - 3, width * fraction, 6), 3, 3);
        if (IsMouseOver || IsMouseCaptured || IsKeyboardFocused) dc.DrawEllipse(Brushes.White, null, new Point(5 + width * fraction, y), 5, 5);
    }
    protected override void OnValueChanged(double oldValue, double newValue) { base.OnValueChanged(oldValue, newValue); InvalidateVisual(); }
    private void Seek(MouseEventArgs e) => SetCurrentValue(ValueProperty, Minimum + Math.Clamp((e.GetPosition(this).X - 5) / Math.Max(1, ActualWidth - 10), 0, 1) * (Maximum - Minimum));
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    { Focus(); scrubbing = true; ScrubStarted?.Invoke(); CaptureMouse(); Seek(e); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Seek(e); }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (scrubbing) { Seek(e); scrubbing = false; ReleaseMouseCapture(); ValueCommitted?.Invoke(Value); }
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); if (scrubbing) { scrubbing = false; ValueCommitted?.Invoke(Value); } }
    protected override void OnKeyUp(KeyEventArgs e)
    { base.OnKeyUp(e); if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) ValueCommitted?.Invoke(Value); }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
}
