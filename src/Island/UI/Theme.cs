using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Island.UI;

public static class Theme
{
    public static readonly SolidColorBrush Secondary = Brush("#A0A0A5"), Control = Brush("#242426"), Orange = Brush("#FF922B"), Blue = Brush("#0A84FF");
    public static SolidColorBrush Brush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    public static TextBlock Text(string text, double size = 13, Brush? color = null, bool bold = false)
        => new() { Text = text, FontFamily = new("Segoe UI Variable, Segoe UI"), FontSize = size, Foreground = color ?? Brushes.White, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
    public static void Place(Canvas canvas, FrameworkElement item, double x, double y, double width, double height)
    { item.Width = width; item.Height = height; Canvas.SetLeft(item, x); Canvas.SetTop(item, y); canvas.Children.Add(item); }
    public static Button Button(string label, Action action, string? icon = null, Brush? color = null, bool filled = false)
    {
        var button = new Button
        {
            Content = icon == null ? Text(label, 12, color, true) : Icon(icon, color), ToolTip = label,
            Foreground = color ?? Brushes.White, Background = filled ? Control : Brushes.Transparent,
            BorderThickness = new(0), Padding = new(4), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Template = ButtonTemplate, FocusVisualStyle = FocusStyle
        };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    private static readonly ControlTemplate ButtonTemplate = BuildTemplate();
    public static Button HoverNavigation(Button button, Action navigate)
    {
        button.MouseEnter += (_, _) => { if (button.IsEnabled && System.Windows.Input.Mouse.Captured == null) navigate(); };
        return button;
    }
    private static ControlTemplate BuildTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "plate"; border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#363638"), "plate")); template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .55)); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .35)); template.Triggers.Add(disabled);
        return template;
    }
    private static readonly Style FocusStyle = MakeFocus();
    private static Style MakeFocus()
    {
        var rectangle = new FrameworkElementFactory(typeof(Rectangle)); rectangle.SetValue(Shape.StrokeProperty, Brushes.White); rectangle.SetValue(Shape.StrokeThicknessProperty, 1.0); rectangle.SetValue(Rectangle.RadiusXProperty, 10.0); rectangle.SetValue(Rectangle.RadiusYProperty, 10.0);
        var style = new Style(typeof(System.Windows.Controls.Control)); style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.Control)) { VisualTree = rectangle })); return style;
    }
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["music"] = "M9,17 L9,5 L19,3 L19,15 M9,8 L19,6 M9,17 C9,20 3,21 3,18 C3,15 9,15 9,17 M19,15 C19,18 13,19 13,16 C13,13 19,13 19,15",
        ["timer"] = "M12,5 A8,8 0 1 1 6,7 M12,1 L12,3 M9,1 L15,1 M12,8 L12,13 L8,10",
        ["clipboard"] = "M8,4 L5,4 L5,22 L19,22 L19,4 L16,4 M9,2 L15,2 L15,6 L9,6 Z",
        ["note"] = "M14,3 L4,3 L4,21 L20,21 L20,11 M10,15 L11,11 L19,3 L22,6 L14,14 Z M17,5 L20,8",
        ["play"] = "M7,3 L20,12 L7,21 Z",
        ["pause"] = "M8,4 L8,20 M16,4 L16,20",
        ["close"] = "M6,6 L18,18 M18,6 L6,18",
        ["plus"] = "M12,4 L12,20 M4,12 L20,12",
        ["next"] = "M4,5 L13,12 L4,19 Z M13,5 L22,12 L13,19 Z",
        ["previous"] = "M20,5 L11,12 L20,19 Z M11,5 L2,12 L11,19 Z",
        ["volume"] = "M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z M16,8 C19,10 19,14 16,16 M19,4 C25,8 25,16 19,20",
        ["list"] = "M8,5 L22,5 M8,12 L22,12 M8,19 L22,19 M2,5 L3,5 M2,12 L3,12 M2,19 L3,19",
        ["sparkles"] = "M13,2 L16,9 L23,12 L16,15 L13,22 L10,15 L3,12 L10,9 Z M4,1 L4,7 M1,4 L7,4",
        ["shuffle"] = "M3,5 C10,5 13,19 21,19 M17,15 L21,19 L17,23 M3,19 C7,19 9,15 11,12 M13,8 C15,5 18,5 21,5 M17,1 L21,5 L17,9",
        ["repeat"] = "M4,9 L4,5 L20,5 L17,2 M20,15 L20,19 L4,19 L7,22 M20,5 L17,8 M4,19 L7,16",
        ["star"] = "M12,2 L15,9 L23,10 L17,15 L19,23 L12,19 L5,23 L7,15 L1,10 L9,9 Z",
        ["copy"] = "M8,8 L21,8 L21,22 L8,22 Z M16,5 L16,2 L3,2 L3,16 L5,16",
        ["back"] = "M15,4 L7,12 L15,20",
        ["expand"] = "M3,9 L3,3 L9,3 M15,3 L21,3 L21,9 M21,15 L21,21 L15,21 M9,21 L3,21 L3,15",
        ["send"] = "M12,21 L12,3 M5,10 L12,3 L19,10",
        ["settings"] = "M12,3 A9,9 0 1 1 11.99,3 M12,8 A4,4 0 1 1 11.99,8"
    };
    private static readonly Dictionary<string, Geometry> GeometryCache = new();
    public static Viewbox Icon(string name, Brush? color = null, double size = 18)
    {
        if (!GeometryCache.TryGetValue(name, out var geometry)) { geometry = Geometry.Parse(Paths.GetValueOrDefault(name, Paths["music"])); geometry.Freeze(); GeometryCache[name] = geometry; }
        var path = new Path { Data = geometry, Stroke = color ?? Brushes.White, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        if (name is "play" or "next" or "previous") path.Fill = color ?? Brushes.White;
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Child = canvas, Width = size, Height = size, IsHitTestVisible = false };
    }
    public static TextBox Input(string value, string name)
    {
        var input = new TextBox { Text = value, Background = Control, Foreground = Brushes.White, BorderThickness = new(0), Padding = new(8, 5, 8, 5), FontSize = 13, CaretBrush = Brushes.White, SelectionBrush = Blue, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, name); return input;
    }
}

// Digits are laid out only on a one-second value change; the compositor moves
// their retained visuals. No strings or text layout run in the motion callback.
public sealed class RollingText : Canvas
{
    private string text = "";
    private readonly double size;
    private readonly Brush color;
    public RollingText(double size, Brush color) { this.size = size; this.color = color; ClipToBounds = true; IsHitTestVisible = false; }
    public void Set(string value, bool reduced)
    {
        if (value == text) return;
        string previous = text; text = value; Children.Clear();
        double x = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i]; double width = c == ':' ? size * .29 : size * .56;
            bool changed = i < previous.Length && previous[i] != c && char.IsDigit(c) && !reduced;
            var label = Theme.Text(c.ToString(), size, color); label.FontWeight = FontWeights.Light;
            var transform = new TranslateTransform(); label.RenderTransform = transform; Theme.Place(this, label, x, 0, width + 1, size * 1.4);
            if (changed)
            {
                var old = Theme.Text(previous[i].ToString(), size, color); old.FontWeight = FontWeights.Light; var outgoing = new TranslateTransform(); old.RenderTransform = outgoing; Theme.Place(this, old, x, 0, width + 1, size * 1.4);
                var animation = new DoubleAnimation(size, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                transform.BeginAnimation(TranslateTransform.YProperty, animation);
                outgoing.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -size, animation.Duration) { EasingFunction = animation.EasingFunction });
                old.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, animation.Duration));
            }
            x += width;
        }
        AutomationProperties.SetName(this, value);
    }
}
