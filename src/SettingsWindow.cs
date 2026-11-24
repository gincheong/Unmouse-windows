using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Unmouse;

internal sealed class SettingsWindow : Window
{
    private readonly Action quit;
    private readonly List<LabelsView> previews = new();
    private int selectedTab;
    private Button? recordingButton;
    private Action? stopRecording;

    public SettingsWindow(Action quit)
    {
        this.quit = quit;
        Width = 500;
        Height = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            Settings.Changed -= OnSettingsChanged;
            stopRecording?.Invoke();
        };
        Rebuild();
    }

    private static string Tr(string english, string korean) => Settings.Tr(english, korean);

    private void OnSettingsChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var preview in previews) preview.InvalidateVisual();
        });
    }

    // 언어를 바꾸거나 기본값으로 되돌리면 모든 탭을 다시 만든다.
    private void Rebuild()
    {
        stopRecording?.Invoke();
        previews.Clear();
        Title = Tr("Unmouse Settings", "Unmouse 설정");

        var tabs = new TabControl { Margin = new Thickness(8) };
        tabs.Items.Add(Tab(Tr("General", "일반"), GeneralTab()));
        tabs.Items.Add(Tab(Tr("Grid", "그리드"), GridTab()));
        tabs.Items.Add(Tab(Tr("Scroll", "스크롤"), ScrollTab()));
        tabs.Items.Add(Tab(Tr("Labels", "라벨"), LabelTab()));
        tabs.SelectedIndex = selectedTab;
        tabs.SelectionChanged += (_, args) =>
        {
            // 탭 안의 콤보 상자가 보낸 이벤트도 여기까지 올라온다.
            if (ReferenceEquals(args.OriginalSource, tabs)) selectedTab = tabs.SelectedIndex;
        };
        Content = tabs;
    }

    private void RebuildLater() => Dispatcher.InvokeAsync(Rebuild);

    private Panel GeneralTab()
    {
        var panel = new StackPanel();
        var languages = new[] { ("en", "English"), ("ko", "한국어") };
        panel.Children.Add(Row(Tr("Language", "언어"), Combo(languages, Settings.Language, value =>
        {
            Settings.Set("language", value);
            RebuildLater();
        })));
        panel.Children.Add(Divider());
        panel.Children.Add(Row(Tr("Click mode shortcut", "클릭 모드 단축키"), Shortcut("clickHotkey")));
        panel.Children.Add(Check(Tr("Show labels on taskbar items", "작업 표시줄 항목에도 라벨 표시"), "includeTaskbar"));
        panel.Children.Add(Footer(Tr(
            "Press the last label character with Shift to right-click, Alt to double-click, Ctrl to Ctrl-click, or Win to move the cursor without clicking.",
            "라벨의 마지막 글자를 Shift와 함께 누르면 우클릭, Alt는 더블클릭, Ctrl은 컨트롤 클릭, Win은 클릭 없이 커서만 이동입니다.")));
        panel.Children.Add(Divider());

        var login = new CheckBox
        {
            Content = Tr("Launch at login", "로그인 시 실행"), IsChecked = LoginItem.Enabled,
            Margin = new Thickness(0, 4, 0, 4),
        };
        login.Click += (_, _) =>
        {
            LoginItem.Enabled = login.IsChecked == true;
            login.IsChecked = LoginItem.Enabled;
        };
        panel.Children.Add(login);
        panel.Children.Add(Footer(Tr(
            "Registers the current location of Unmouse.exe. Turn this off and on again after moving the file.",
            "Unmouse.exe의 현재 위치를 등록합니다. 파일을 옮긴 뒤에는 껐다가 다시 켜세요.")));
        panel.Children.Add(Divider());

        string version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        panel.Children.Add(Footer(Tr($"Version {version}", $"버전 {version}")));
        panel.Children.Add(ActionButton(Tr("Quit Unmouse", "Unmouse 종료"), quit));
        return panel;
    }

    private Panel GridTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Check(Tr("Enable grid mode", "그리드 모드 사용"), "gridEnabled"));
        panel.Children.Add(Row(Tr("Grid mode shortcut", "그리드 모드 단축키"), Shortcut("gridHotkey")));
        panel.Children.Add(Footer(Tr(
            "Divides the screen that has the focused window into a grid. Type two characters to select a cell, which is then divided again. One more character clicks that position. Enter or Space clicks the center of the selected cell, and Backspace goes back one step. Modifier keys work as in click mode.",
            "포커스된 창이 있는 화면을 격자로 나눕니다. 두 글자로 칸을 고르면 그 칸을 다시 나누고, 한 글자를 더 누르면 그 위치를 클릭합니다. Enter나 Space는 고른 칸의 중앙을 클릭하고, Backspace는 이전 단계로 돌아갑니다. 수식키는 클릭 모드와 같습니다.")));
        panel.Children.Add(Divider());

        var samples = new[] { "sa", "sd", "sf" }
            .Select((label, index) => new LabelItem(label, new Target(new Rect(1 + index * 64, 1, 64, 44)))).ToList();
        panel.Children.Add(Row(Tr("Preview", "미리보기"), Preview(samples, 194, 46, cells: true)));
        panel.Children.Add(SliderRow(Tr("Font size", "글자 크기"), "gridFontSize", 8, 24, 1, Whole));
        panel.Children.Add(Row(Tr("Background color", "배경색"), ColorButton("gridBackgroundColor")));
        panel.Children.Add(SliderRow(Tr("Background opacity", "배경 불투명도"), "gridBackgroundOpacity", 0, 1, 0.05, Percent));
        panel.Children.Add(Row(Tr("Text color", "글자색"), ColorButton("gridTextColor")));
        panel.Children.Add(Row(Tr("Line color", "선 색"), ColorButton("gridLineColor")));
        panel.Children.Add(SliderRow(Tr("Line opacity", "선 불투명도"), "gridLineOpacity", 0, 1, 0.05, Percent));
        panel.Children.Add(Footer(Tr(
            "Grid labels use the font and weight from the Labels tab, but not its font size or padding. In small cells the text shrinks to fit.",
            "그리드 라벨은 라벨 탭의 서체와 굵기를 따르고, 글자 크기와 여백은 따르지 않습니다. 작은 칸에서는 글자가 칸에 맞게 줄어듭니다.")));
        panel.Children.Add(ActionButton(Tr("Reset to defaults", "기본값으로 되돌리기"), () =>
        {
            Settings.Reset(Settings.GridStyleKeys);
            RebuildLater();
        }));
        return panel;
    }

    private Panel ScrollTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Check(Tr("Enable scroll mode", "스크롤 모드 사용"), "scrollEnabled"));
        panel.Children.Add(Row(Tr("Scroll mode shortcut", "스크롤 모드 단축키"), Shortcut("scrollHotkey")));
        panel.Children.Add(SliderRow(Tr("Speed", "속도"), "scrollSpeed", 0.5, 6, 0.5, value => $"{value:0.0} px/ms"));
        panel.Children.Add(Check(Tr("Also scroll with arrow keys", "방향키로도 스크롤"), "scrollArrowKeys"));
        panel.Children.Add(Check(
            Tr("Scroll one wheel notch at a time", "휠 한 칸 단위로 스크롤"), "scrollWholeNotches"));
        panel.Children.Add(Footer(Tr(
            "J/K scroll down and up, H/L left and right, D/U half a page, G to the top, Shift+G to the bottom. Hold Shift to scroll at 3x speed. With several scroll areas, scrolling starts in the one under the cursor, and number keys switch areas. ESC or any other key turns it off. Turn on notch scrolling for apps that do not respond to smooth scrolling.",
            "J/K 아래·위, H/L 왼쪽·오른쪽, D/U 반 페이지, G 맨 위, Shift+G 맨 아래. Shift와 함께 누르면 3배 속도로 움직입니다. 영역이 여러 개면 커서가 놓인 영역부터 시작하고 숫자 키로 바꿉니다. ESC나 다른 키를 누르면 끝납니다. 부드러운 스크롤에 반응하지 않는 앱에서는 휠 한 칸 단위 스크롤을 켜세요.")));
        return panel;
    }

    private Panel LabelTab()
    {
        var panel = new StackPanel();
        var samples = new[] { "s", "sa", "dk" }
            .Select((label, index) => new LabelItem(label, new Target(new Rect(20 + index * 80, 2, 40, 8)))).ToList();
        panel.Children.Add(Row(Tr("Preview", "미리보기"), Preview(samples, 260, 88, cells: false)));
        panel.Children.Add(Divider());

        var characters = new TextBox { Text = Settings.Str("labelCharacters"), Padding = new Thickness(2) };
        characters.TextChanged += (_, _) => Settings.Set("labelCharacters", characters.Text);
        panel.Children.Add(Row(Tr("Label characters", "라벨 문자"), characters));

        var fonts = new List<(string, string)>
        {
            (Settings.MonospacedFontFamily, Tr("System Monospaced", "시스템 고정폭")),
            (Settings.SystemFontFamily, Tr("System", "시스템")),
        };
        fonts.AddRange(Fonts.SystemFontFamilies.Select(family => family.Source)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => (name, name)));
        panel.Children.Add(Row(Tr("Font", "서체"), Combo(
            fonts, Settings.Str("labelFontFamily"), value => Settings.Set("labelFontFamily", value))));
        panel.Children.Add(Row(Tr("Weight", "굵기"), Combo(
            Settings.FontWeightOptions, Settings.Str("labelFontWeight"),
            value => Settings.Set("labelFontWeight", value))));
        panel.Children.Add(SliderRow(Tr("Font size", "글자 크기"), "labelFontSize", 8, 24, 1, Whole));
        panel.Children.Add(SliderRow(Tr("Horizontal padding", "가로 여백"), "labelPaddingWidth", 0, 24, 1, Whole));
        panel.Children.Add(SliderRow(Tr("Vertical padding", "세로 여백"), "labelPaddingHeight", 0, 16, 1, Whole));
        panel.Children.Add(Row(Tr("Background color", "배경색"), ColorButton("labelBackgroundColor")));
        panel.Children.Add(SliderRow(Tr("Background opacity", "배경 불투명도"), "labelBackgroundOpacity", 0, 1, 0.05, Percent));
        panel.Children.Add(Row(Tr("Text color", "글자색"), ColorButton("labelTextColor")));
        panel.Children.Add(SliderRow(Tr("Corner radius", "모서리 둥글기"), "labelCornerRadius", 0, 10, 1, Whole));
        panel.Children.Add(Check(Tr("Show in uppercase", "대문자로 표시"), "labelUppercase"));
        panel.Children.Add(Check(Tr("Point at the target with an arrow", "화살표로 대상 가리키기"), "labelPointer"));
        panel.Children.Add(ActionButton(Tr("Reset to defaults", "기본값으로 되돌리기"), () =>
        {
            Settings.Reset(Settings.LabelStyleKeys);
            RebuildLater();
        }));
        return panel;
    }

    private static string Whole(double value) => $"{value:0}";

    private static string Percent(double value) => $"{Math.Round(value * 100):0}%";

    private static TabItem Tab(string header, Panel content)
    {
        return new TabItem
        {
            Header = header,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), Content = content,
            },
        };
    }

    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private static FrameworkElement Footer(string text)
    {
        return new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11,
            Margin = new Thickness(0, 4, 0, 4),
        };
    }

    private static FrameworkElement Divider() => new Separator { Margin = new Thickness(0, 8, 0, 8) };

    private static FrameworkElement ActionButton(string title, Action action)
    {
        var button = new Button
        {
            Content = title, Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 4),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static FrameworkElement Check(string label, string key)
    {
        var box = new CheckBox { Content = label, IsChecked = Settings.Flag(key), Margin = new Thickness(0, 4, 0, 4) };
        box.Click += (_, _) => Settings.Set(key, box.IsChecked == true);
        return box;
    }

    private static FrameworkElement SliderRow(
        string label, string key, double minimum, double maximum, double step, Func<double, string> format)
    {
        var slider = new Slider
        {
            Minimum = minimum, Maximum = maximum, TickFrequency = step, IsSnapToTickEnabled = true,
            Value = Settings.Num(key), VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock
        {
            Width = 72, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Text = format(slider.Value),
        };
        slider.ValueChanged += (_, args) =>
        {
            // 0.05 단위로 더한 값에 부동소수점 오차가 남는다.
            double rounded = Math.Round(args.NewValue, 2);
            value.Text = format(rounded);
            Settings.Set(key, rounded);
        };
        var dock = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        dock.Children.Add(value);
        dock.Children.Add(slider);
        return Row(label, dock);
    }

    private static ComboBox Combo(
        IEnumerable<(string Value, string Title)> options, string current, Action<string> changed)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
        foreach (var (value, title) in options)
        {
            var item = new ComboBoxItem { Content = title, Tag = value };
            combo.Items.Add(item);
            if (value == current) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string value }) changed(value);
        };
        return combo;
    }

    private static FrameworkElement ColorButton(string key)
    {
        var swatch = new Border
        {
            Width = 48, Height = 16, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Settings.ColorOf(key)),
        };
        var button = new Button
        {
            Content = swatch, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) =>
        {
            var current = Settings.ColorOf(key);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B), FullOpen = true,
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var picked = dialog.Color;
            Settings.Set(key, $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}");
            swatch.Background = new SolidColorBrush(Settings.ColorOf(key));
        };
        return button;
    }

    private FrameworkElement Preview(List<LabelItem> samples, double width, double height, bool cells)
    {
        var view = new LabelsView
        {
            DipCoordinates = true, MonitorBounds = new Rect(0, 0, width, height), Width = width, Height = height,
            HorizontalAlignment = HorizontalAlignment.Left, Items = samples, ShowsCells = cells,
        };
        previews.Add(view);
        return view;
    }

    private FrameworkElement Shortcut(string key)
    {
        var button = new Button
        {
            Content = Settings.HotkeyOf(key).ToString(), Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140,
        };
        button.Click += (_, _) =>
        {
            bool wasRecording = recordingButton == button;
            stopRecording?.Invoke();
            if (wasRecording) return;

            void Stop()
            {
                ShortcutRecording.Handler = null;
                recordingButton = null;
                stopRecording = null;
                button.Content = Settings.HotkeyOf(key).ToString();
            }
            recordingButton = button;
            stopRecording = Stop;
            button.Content = Tr("Press a shortcut (ESC to cancel)", "키 조합을 누르세요 (ESC 취소)");
            ShortcutRecording.Handler = (code, modifiers) =>
            {
                if (code == KeyCodes.Escape)
                {
                    Stop();
                    return;
                }
                // 수식키 없는 일반 키를 단축키로 잡으면 그 키로 타이핑을 못 하게 된다.
                bool usable = modifiers.Ctrl || modifiers.Alt || modifiers.Win || KeyCodes.IsFunctionKey(code);
                if (!usable || KeyCodes.Name(code) == null)
                {
                    Native.MessageBeep(0);
                    return;
                }
                Settings.Set(key, new Hotkey(code, modifiers).ToString());
                Stop();
            };
        };
        return button;
    }
}
