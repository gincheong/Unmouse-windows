using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Unmouse;

internal readonly record struct LabelItem(string Label, Target Target);

internal sealed class OverlayController
{
    private readonly List<OverlayWindow> windows = new();

    // highlight는 스크롤 모드에서 선택된 영역의 테두리로 그린다.
    // cells가 true면 그리드 모드처럼 각 대상의 경계선도 그린다.
    public void Show(IReadOnlyList<LabelItem> items, Rect? highlight = null, bool pointers = true, bool cells = false)
    {
        Sync();
        foreach (var window in windows)
        {
            var view = window.View;
            view.Items = items.Where(item => window.MonitorBounds.Contains(item.Target.Point)).ToList();
            view.Typed = "";
            view.Highlight = highlight;
            view.ShowsPointers = pointers;
            view.ShowsCells = cells;
            window.Present();
        }
    }

    public void Update(string typed)
    {
        foreach (var window in windows)
        {
            window.View.Typed = typed;
            window.View.InvalidateVisual();
        }
    }

    public void Hide()
    {
        foreach (var window in windows)
        {
            if (window.IsVisible) window.Hide();
        }
    }

    // 창을 새로 만들면 느려서 모니터 구성이 바뀔 때만 다시 만든다.
    private void Sync()
    {
        var monitors = Screens.All();
        if (monitors.Select(monitor => monitor.Bounds).SequenceEqual(windows.Select(window => window.MonitorBounds)))
        {
            return;
        }
        foreach (var window in windows) window.Close();
        windows.Clear();
        windows.AddRange(monitors.Select(monitor => new OverlayWindow(monitor.Bounds)));
    }
}

internal sealed class OverlayWindow : Window
{
    public Rect MonitorBounds { get; }
    public LabelsView View { get; }
    private readonly IntPtr handle;

    public OverlayWindow(Rect monitorBounds)
    {
        MonitorBounds = monitorBounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        View = new LabelsView { MonitorBounds = monitorBounds };
        Content = View;

        handle = new WindowInteropHelper(this).EnsureHandle();
        long style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
        style |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style));
        Place();
    }

    public void Present()
    {
        View.InvalidateVisual();
        if (!IsVisible) Show();
        // WPF는 DPI가 다른 모니터로 옮겨질 때 창 크기를 바꾸므로 보여 준 뒤에 다시 맞춘다.
        Place();
    }

    private void Place()
    {
        Native.SetWindowPos(
            handle, Native.HWND_TOPMOST, (int)MonitorBounds.X, (int)MonitorBounds.Y, (int)MonitorBounds.Width,
            (int)MonitorBounds.Height, Native.SWP_NOACTIVATE);
    }
}

internal sealed class LabelsView : FrameworkElement
{
    public IReadOnlyList<LabelItem> Items = Array.Empty<LabelItem>();
    public string Typed = "";
    public Rect? Highlight;
    public bool ShowsPointers = true;
    public bool ShowsCells;
    public Rect MonitorBounds;
    // 설정 창의 미리보기는 대상 좌표를 DIP로 준다.
    public bool DipCoordinates;

    private double scale = 1;
    private double pixelsPerDip = 1;

    private sealed record LabelLayout(FormattedText Text, Rect Box, double Radius, Point[]? Pointer, double Lip)
    {
        public Rect Face => Lip > 0
            ? new Rect(Box.X + 1, Box.Y + 1, Math.Max(Box.Width - 2, 0), Math.Max(Box.Height - Lip - 1, 0))
            : Box;
    }

    public LabelsView()
    {
        IsHitTestVisible = false;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        // 대상 좌표는 물리 픽셀이고 WPF는 DIP로 그린다.
        pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        scale = DipCoordinates ? 1 : pixelsPerDip;
        if (ShowsCells)
        {
            DrawGrid(context);
            return;
        }
        var style = Settings.LabelStyle;

        if (Highlight is { } highlight)
        {
            var rect = Local(highlight);
            if (rect.Width > 4 && rect.Height > 4)
            {
                context.DrawRoundedRectangle(null, MakePen(style.Background, 1, 3), Rect.Inflate(rect, -2, -2), 6, 6);
            }
        }

        var side = Color.FromRgb(
            (byte)(style.Background.R * 0.65), (byte)(style.Background.G * 0.65), (byte)(style.Background.B * 0.65));
        var sideBrush = MakeBrush(side);
        var faceBrush = MakeBrush(style.Background);
        var facePen = MakePen(Colors.White, 0.55, 0.75);
        var outlinePen = MakePen(style.Text, 0.45, 1);
        double lip = Math.Max(2, Round(style.FontSize * 0.2));

        foreach (var layout in Layouts(style, lip))
        {
            Geometry outline = new RectangleGeometry(layout.Box, layout.Radius, layout.Radius);
            if (layout.Pointer is { } pointer)
            {
                var arrow = new StreamGeometry();
                using (var figure = arrow.Open())
                {
                    figure.BeginFigure(pointer[0], true, true);
                    figure.LineTo(pointer[1], true, false);
                    figure.LineTo(pointer[2], true, false);
                }
                outline = Geometry.Combine(outline, arrow, GeometryCombineMode.Union, null);
            }
            var face = layout.Face;
            double faceRadius = Math.Min(Math.Max(layout.Radius - 1, 0), Math.Min(face.Width, face.Height) / 2);

            // 옆면과 윗면이 겹치는 부분의 색이 달라지지 않게 불투명도는 한 번에 적용한다.
            context.PushOpacity(style.BackgroundOpacity);
            context.DrawGeometry(sideBrush, null, outline);
            context.DrawRoundedRectangle(faceBrush, facePen, face, faceRadius, faceRadius);
            context.Pop();

            context.DrawGeometry(null, outlinePen, outline);
            context.DrawText(
                layout.Text,
                new Point(
                    face.X + face.Width / 2 - layout.Text.Width / 2,
                    face.Y + face.Height / 2 - layout.Text.Height / 2));
        }
    }

    // 그리드는 칸 크기가 정해져 있어서 라벨 설정의 글자 크기와 여백을 쓰지 않고 칸에 맞춰 그린다.
    private void DrawGrid(DrawingContext context)
    {
        var grid = Settings.GridStyle;
        var label = Settings.LabelStyle;
        var typeface = label.Typeface;
        var cells = Items.Where(item => item.Label.StartsWith(Typed, StringComparison.Ordinal))
            .Select(item => (item.Label, Cell: Local(item.Target.Frame))).ToList();

        var linePen = MakePen(grid.Line, grid.LineOpacity, 1);
        foreach (var (_, cell) in cells) context.DrawRectangle(null, linePen, cell);
        if (Highlight is { } highlight)
        {
            context.DrawRectangle(null, MakePen(grid.Line, 1, 2), Local(highlight));
        }

        var background = MakeBrush(grid.Background, grid.BackgroundOpacity);
        var textBrush = MakeBrush(grid.Text);
        var typedBrush = MakeBrush(grid.Text, 0.3);
        foreach (var (name, cell) in cells)
        {
            string text = label.Uppercase ? name.ToUpperInvariant() : name;
            // 작은 칸에서는 글자가 칸 안에 들어가도록 줄인다.
            double fitted = Math.Min(cell.Height * 0.85, cell.Width * 1.3 / text.Length);
            double size = Math.Max(Math.Min(grid.FontSize, fitted), 6);
            var formatted = MakeText(text, typeface, size, textBrush);
            if (Typed.Length > 0) formatted.SetForegroundBrush(typedBrush, 0, Math.Min(Typed.Length, text.Length));

            double centerX = cell.X + cell.Width / 2;
            double centerY = cell.Y + cell.Height / 2;
            var box = new Rect(
                centerX - formatted.Width / 2 - 2, centerY - formatted.Height / 2, formatted.Width + 4,
                formatted.Height);
            if (cell.Width > 2 && cell.Height > 2) box.Intersect(Rect.Inflate(cell, -1, -1));
            if (!box.IsEmpty) context.DrawRoundedRectangle(background, null, box, 3, 3);
            context.DrawText(formatted, new Point(centerX - formatted.Width / 2, centerY - formatted.Height / 2));
        }
    }

    private List<LabelLayout> Layouts(LabelStyle style, double lip)
    {
        var typeface = style.Typeface;
        var textBrush = MakeBrush(style.Text);
        var typedBrush = MakeBrush(style.Text, 0.3);
        bool pointed = ShowsPointers && style.Pointer;
        double arrowHeight = Math.Max(4, Round(style.FontSize * 0.4));
        double boundsWidth = MonitorBounds.Width / scale;
        double boundsHeight = MonitorBounds.Height / scale;
        var layouts = new List<LabelLayout>();

        foreach (var item in Items)
        {
            if (!item.Label.StartsWith(Typed, StringComparison.Ordinal)) continue;
            string text = style.Uppercase ? item.Label.ToUpperInvariant() : item.Label;
            var formatted = MakeText(text, typeface, style.FontSize, textBrush);
            if (Typed.Length > 0) formatted.SetForegroundBrush(typedBrush, 0, Math.Min(Typed.Length, text.Length));

            double faceHeight = Math.Ceiling(formatted.Height) + style.Padding.Height;
            double height = faceHeight + lip;
            // 한 글자 라벨도 삼각형이 붙을 평평한 구간이 남도록 너비를 높이 이상으로 잡는다.
            double width = Math.Max(Math.Ceiling(formatted.Width) + style.Padding.Width, faceHeight);
            double radius = Math.Min(style.CornerRadius, Math.Min(width, height) / 2);
            var frame = Local(item.Target.Frame);
            double midX = frame.X + frame.Width / 2;
            double midY = frame.Y + frame.Height / 2;
            double maxX = boundsWidth - width;
            double boxX = Math.Min(Math.Max(Round(midX - width / 2), 0), maxX);

            if (!pointed)
            {
                double centeredY = Math.Min(Math.Max(Round(midY - height / 2), 0), boundsHeight - height);
                layouts.Add(new LabelLayout(formatted, new Rect(boxX, centeredY, width, height), radius, null, lip));
                continue;
            }

            // 작은 요소는 아래쪽 가장자리를, 큰 요소는 중앙 근처를 가리켜서 라벨이 대상을 덜 가린다.
            double reach = Math.Max(Math.Min(frame.Height / 2 - 2, 10), 0);
            double tipY = Round(midY + reach);
            double boxY = tipY + arrowHeight;
            bool below = true;
            if (boxY + height > boundsHeight)
            {
                // 화면 아래에 자리가 없으면 라벨을 대상 위에 두고 아래로 가리킨다.
                tipY = Round(midY - reach);
                boxY = tipY - arrowHeight - height;
                below = false;
            }
            var box = new Rect(boxX, boxY, width, height);

            double halfWidth = Math.Max(Math.Min(arrowHeight * 0.7, (width - 2 * radius) / 2), 2);
            double boxMidX = box.X + box.Width / 2;
            // 상자가 화면 끝에 걸려 밀린 경우가 아니면 삼각형을 상자 정중앙에 둬서 좌우가 어긋나지 않게 한다.
            double arrowX = boxMidX;
            if (Math.Abs(midX - boxMidX) > 1)
            {
                double margin = radius + halfWidth;
                arrowX = Math.Min(
                    Math.Max(Round(midX), box.Left + margin), Math.Max(box.Right - margin, box.Left + margin));
            }
            // 둥근 모서리와 겹치는 구간에서도 삼각형이 상자와 이어지게 밑변을 상자 안쪽에 둔다.
            double inset = 1 + radius * 0.3;
            double baseY = below ? box.Top + inset : box.Bottom - inset;
            var pointer = new[]
            {
                new Point(arrowX, tipY), new Point(arrowX - halfWidth, baseY), new Point(arrowX + halfWidth, baseY),
            };
            layouts.Add(new LabelLayout(formatted, box, radius, pointer, lip));
        }
        return layouts;
    }

    private Rect Local(Rect target)
    {
        return new Rect(
            (target.X - MonitorBounds.X) / scale, (target.Y - MonitorBounds.Y) / scale,
            Math.Max(target.Width, 0) / scale, Math.Max(target.Height, 0) / scale);
    }

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private FormattedText MakeText(string text, Typeface typeface, double size, Brush brush)
    {
        return new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
    }

    private static SolidColorBrush MakeBrush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Color color, double opacity, double width)
    {
        var pen = new Pen(MakeBrush(color, opacity), width);
        pen.Freeze();
        return pen;
    }
}
