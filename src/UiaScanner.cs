using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Interop.UIAutomationClient;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Unmouse;

// 화면에 보이는 영역. 물리 픽셀 기준의 가상 화면 좌표다.
// Element는 ScrollPattern으로 세로 스크롤할 수 있는 영역에만 들어 있다.
internal readonly record struct Target(Rect Frame, IUIAutomationElement? Element = null)
{
    public Point Point => new(Frame.X + Frame.Width / 2, Frame.Y + Frame.Height / 2);
}

internal static class UiaScanner
{
    public enum Mode { Click, Scroll }

    private const int BoundingRectangleProperty = 30001;
    private const int ControlTypeProperty = 30003;
    private const int IsEnabledProperty = 30010;
    private const int IsOffscreenProperty = 30022;
    private const int IsExpandCollapsePatternAvailableProperty = 30028;
    private const int IsInvokePatternAvailableProperty = 30031;
    private const int IsScrollPatternAvailableProperty = 30034;
    private const int IsSelectionItemPatternAvailableProperty = 30036;
    private const int IsTogglePatternAvailableProperty = 30041;
    private const int HorizontallyScrollableProperty = 30057;
    private const int VerticallyScrollableProperty = 30058;

    private const int ScrollPatternId = 10004;
    // SetScrollPercent에서 해당 축을 움직이지 않게 하는 값
    private const double NoScroll = -1;

    private const int TextControlType = 50020;
    private const int DocumentControlType = 50030;

    // Button, CheckBox, ComboBox, Edit, Hyperlink, ListItem, MenuItem, RadioButton, Slider, TabItem,
    // TreeItem, DataItem, SplitButton, HeaderItem
    private static readonly HashSet<int> ClickableTypes = new()
    {
        50000, 50002, 50003, 50004, 50005, 50007, 50011, 50013, 50015, 50019, 50024, 50029, 50031, 50035,
    };
    private static readonly int[] ClickablePatterns =
    {
        IsInvokePatternAvailableProperty, IsTogglePatternAvailableProperty,
        IsSelectionItemPatternAvailableProperty, IsExpandCollapsePatternAvailableProperty,
    };

    // STA인 UI 스레드에서 만들면 모든 호출이 UI 스레드를 거치므로 스레드 풀에서만 접근한다.
    private static readonly Lazy<IUIAutomation> Automation = new(() =>
    {
        var automation = (IUIAutomation)new CUIAutomation8Class();
        if (automation is IUIAutomation2 extended)
        {
            extended.ConnectionTimeout = 1000;
            extended.TransactionTimeout = 1500;
        }
        return automation;
    });
    private static readonly Lazy<IUIAutomationCondition> ClickCondition = new(() =>
    {
        var automation = Automation.Value;
        IUIAutomationCondition? any = null;
        void Or(IUIAutomationCondition condition)
        {
            any = any == null ? condition : automation.CreateOrCondition(any, condition);
        }
        foreach (int type in ClickableTypes) Or(automation.CreatePropertyCondition(ControlTypeProperty, type));
        foreach (int pattern in ClickablePatterns) Or(automation.CreatePropertyCondition(pattern, true));
        var usable = automation.CreateAndCondition(
            automation.CreatePropertyCondition(IsEnabledProperty, true),
            automation.CreatePropertyCondition(IsOffscreenProperty, false));
        return automation.CreateAndCondition(usable, any!);
    });
    private static readonly Lazy<IUIAutomationCondition> ScrollCondition = new(() =>
    {
        var automation = Automation.Value;
        return automation.CreateAndCondition(
            automation.CreatePropertyCondition(IsOffscreenProperty, false),
            automation.CreateOrCondition(
                automation.CreatePropertyCondition(IsScrollPatternAvailableProperty, true),
                automation.CreatePropertyCondition(ControlTypeProperty, DocumentControlType)));
    });

    public static List<Target> Scan(IntPtr foreground, Mode mode, bool includeTaskbar, int timeout = 1500)
    {
        var windows = TopLevelWindows.Visible();
        var roots = new List<IntPtr>();
        // 메뉴가 열려 있으면 그 뒤의 창은 클릭할 수 없으므로 메뉴 항목만 보여 준다.
        if (mode == Mode.Click)
        {
            roots.AddRange(windows.Where(window => window.ClassName == "#32768").Select(window => window.Handle));
        }
        if (roots.Count == 0)
        {
            if (foreground != IntPtr.Zero)
            {
                roots.Add(foreground);
                // 드롭다운과 팝업은 포커스된 창이 소유한 별도의 최상위 창으로 뜬다.
                Native.GetWindowThreadProcessId(foreground, out uint process);
                roots.AddRange(windows.Where(window => window.Process == process && window.Owner != IntPtr.Zero)
                    .Select(window => window.Handle));
            }
            if (mode == Mode.Click && includeTaskbar)
            {
                roots.AddRange(windows
                    .Where(window => window.ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                    .Select(window => window.Handle));
            }
        }

        // 응답이 없는 앱의 호출은 취소할 수 없어서 제한 시간 안에 끝난 창의 결과만 쓴다.
        var tasks = roots.Distinct().Select(root => Task.Run(() => ScanRoot(root, mode))).ToArray();
        try
        {
            Task.WaitAll(tasks, timeout);
        }
        catch (AggregateException)
        {
        }
        var targets = tasks.Where(task => task.Status == TaskStatus.RanToCompletion)
            .SelectMany(task => task.Result).ToList();

        // 스크롤 영역을 노출하지 않는 앱은 창 전체를 스크롤 대상으로 쓴다.
        if (mode == Mode.Scroll && targets.Count == 0 && WindowFrame(foreground) is { } frame)
        {
            targets.Add(new Target(frame));
        }
        return Finish(targets);
    }

    // Chromium과 Electron은 요청받기 전에는 웹 콘텐츠의 접근성 트리를 만들지 않는다.
    // 트리가 만들어지는 데 시간이 걸려서 창이 포커스될 때 미리 요청해 둔다.
    public static void WarmUp(IntPtr hwnd)
    {
        if (!Native.ClassName(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return;
        try
        {
            var automation = Automation.Value;
            var document = automation.CreatePropertyCondition(ControlTypeProperty, DocumentControlType);
            automation.ElementFromHandle(hwnd)?.FindFirst(TreeScope.TreeScope_Descendants, document);
        }
        catch (Exception)
        {
        }
    }

    // 대상 앱의 응답을 기다리므로 스레드 풀에서 호출한다. 실패하면 false를 돌려준다.
    public static bool ScrollToEdge(IUIAutomationElement? element, bool bottom)
    {
        if (element == null) return false;
        try
        {
            if (element.GetCurrentPattern(ScrollPatternId) is not IUIAutomationScrollPattern pattern) return false;
            pattern.SetScrollPercent(NoScroll, bottom ? 100 : 0);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<Target> ScanRoot(IntPtr hwnd, Mode mode)
    {
        var targets = new List<Target>();
        try
        {
            // 화면 밖으로 스크롤된 요소를 보이는 요소로 보고하는 앱이 있어서 창 영역으로 자른다.
            if (WindowFrame(hwnd) is not { } clip) return targets;
            var automation = Automation.Value;
            var root = automation.ElementFromHandle(hwnd);
            if (root == null) return targets;

            var request = automation.CreateCacheRequest();
            request.AddProperty(BoundingRectangleProperty);
            request.AddProperty(ControlTypeProperty);
            if (mode == Mode.Scroll)
            {
                request.AddProperty(HorizontallyScrollableProperty);
                request.AddProperty(VerticallyScrollableProperty);
            }
            // 클릭 모드는 캐시한 속성만 읽으므로 요소마다 원격 참조를 만들지 않게 한다.
            // 스크롤 모드는 ScrollPattern을 호출해야 해서 참조를 유지한다.
            if (mode == Mode.Click)
            {
                request.AutomationElementMode = AutomationElementMode.AutomationElementMode_None;
            }

            var condition = mode == Mode.Click ? ClickCondition.Value : ScrollCondition.Value;
            var found = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, condition, request);
            int count = found?.Length ?? 0;
            for (int index = 0; index < count; index++)
            {
                var element = found!.GetElement(index);
                var bounds = element.CachedBoundingRectangle;
                double width = bounds.right - bounds.left;
                double height = bounds.bottom - bounds.top;
                if (width <= 0 || height <= 0) continue;
                var visible = Rect.Intersect(new Rect(bounds.left, bounds.top, width, height), clip);
                if (visible.IsEmpty || visible.Width < 3 || visible.Height < 3) continue;

                int type = element.CachedControlType;
                if (mode == Mode.Scroll)
                {
                    if (visible.Width < 50 || visible.Height < 50) continue;
                    bool vertical = element.GetCachedPropertyValue(VerticallyScrollableProperty) is true;
                    bool scrollable = type == DocumentControlType || vertical
                        || element.GetCachedPropertyValue(HorizontallyScrollableProperty) is true;
                    if (scrollable) targets.Add(new Target(visible, vertical ? element : null));
                    continue;
                }

                // 클릭 가능한 요소 안의 텍스트도 같은 동작을 보고해서 텍스트는 대상에서 뺀다.
                if (type == TextControlType) continue;
                // 내용을 감싸는 큰 컨테이너도 Invoke 패턴을 보고한다.
                if (!ClickableTypes.Contains(type) && width >= 200 && height >= 200) continue;
                targets.Add(new Target(visible));
            }
        }
        catch (Exception)
        {
            // 관리자 권한으로 실행된 창이나 응답이 없는 창은 읽지 못한다.
        }
        return targets;
    }

    private static Rect? WindowFrame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return null;
        var frame = Rect.Intersect(
            new Rect(rect.Left, rect.Top, Math.Max(rect.Right - rect.Left, 0), Math.Max(rect.Bottom - rect.Top, 0)),
            Screens.VirtualBounds);
        return frame.IsEmpty ? null : frame;
    }

    private static List<Target> Finish(List<Target> targets)
    {
        var result = new List<Target>();
        foreach (var target in targets.OrderBy(target => target.Frame.Y).ThenBy(target => target.Frame.X))
        {
            var point = target.Point;
            bool duplicated = result.Any(other =>
                Math.Abs(other.Point.X - point.X) < 4 && Math.Abs(other.Point.Y - point.Y) < 4);
            if (!duplicated) result.Add(target);
        }
        return result;
    }
}

internal readonly record struct TopLevelWindow(IntPtr Handle, string ClassName, uint Process, IntPtr Owner);

internal static class TopLevelWindows
{
    public static List<TopLevelWindow> Visible()
    {
        var windows = new List<TopLevelWindow>();
        Native.EnumWindows(
            (hwnd, _) =>
            {
                if (Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && !Native.IsCloaked(hwnd))
                {
                    Native.GetWindowThreadProcessId(hwnd, out uint process);
                    windows.Add(new TopLevelWindow(
                        hwnd, Native.ClassName(hwnd), process, Native.GetWindow(hwnd, Native.GW_OWNER)));
                }
                return true;
            },
            IntPtr.Zero);
        return windows;
    }
}

internal readonly record struct MonitorInfo(IntPtr Handle, Rect Bounds, double Scale);

internal static class Screens
{
    public static List<MonitorInfo> All()
    {
        var monitors = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(
            IntPtr.Zero, IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                if (Get(monitor) is { } info) monitors.Add(info);
                return true;
            },
            IntPtr.Zero);
        return monitors;
    }

    public static MonitorInfo? Get(IntPtr monitor)
    {
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return null;
        var bounds = info.rcMonitor;
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        return new MonitorInfo(
            monitor, new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), scale);
    }

    public static Rect VirtualBounds
    {
        get
        {
            var bounds = Rect.Empty;
            foreach (var monitor in All()) bounds = Rect.Union(bounds, monitor.Bounds);
            return bounds;
        }
    }
}
