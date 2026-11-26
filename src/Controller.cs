using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Unmouse;

// 설정 창에서 단축키를 녹화하는 동안에는 눌린 키를 모두 설정 창으로 넘긴다.
internal static class ShortcutRecording
{
    public static Action<int, Modifiers>? Handler;
}

internal sealed class Controller
{
    private enum Purpose { Click, Scroll, Grid }
    private enum State { Idle, Scanning, Picking, Scrolling }

    // 휠 한 칸의 delta는 120이고 대부분의 앱에서 약 100픽셀을 움직인다.
    private const double WheelPerPixel = 1.2;
    // WM_MOUSEWHEEL의 delta는 16비트 부호 있는 정수로 전달된다.
    private const int MaxWheelDelta = 32760;
    // 부드러운 스크롤을 쓰는 앱은 휠 delta를 누적하므로 같은 방향의 끝 이동을 이 시간 안에 다시 보내지 않는다.
    private const long EdgeWheelInterval = 500;
    // 새로 포커스된 창에서 라벨을 보여 준 뒤 다시 스캔하기 전에 기다리는 시간(ms)
    private static readonly int[] RescanDelays = { 400, 800 };
    // 긴 변이 이 길이 이하인 칸은 더 나누지 않고 클릭한다.
    private const double GridStopCell = 28;
    // 한 단계에서 칸을 이 길이보다 잘게 나누지 않는다.
    private const double GridFinestCell = 16;

    private readonly Dispatcher dispatcher;
    private readonly OverlayController overlay = new();
    // 네이티브 코드가 들고 있는 동안 수거되지 않게 필드로 유지한다.
    private readonly Native.HookProc keyboardProc;
    private readonly Native.HookProc mouseProc;
    private readonly Native.WinEventProc foregroundProc;
    private IntPtr keyboardHook;
    private IntPtr mouseHook;
    private IntPtr foregroundHook;

    private State state = State.Idle;
    private Purpose purpose = Purpose.Click;
    // 다시 스캔하는 스레드 풀 작업도 읽는다.
    private volatile int generation;
    // 포커스된 뒤에 이미 스캔한 창. 포커스가 바뀌면 비운다.
    private IntPtr scannedWindow;
    private List<LabelItem> items = new();
    private string typed = "";
    private readonly HashSet<int> swallowedKeys = new();

    private List<Target> scrollTargets = new();
    private Rect scrollFrame;
    private Interop.UIAutomationClient.IUIAutomationElement? scrollElement;
    // 위쪽과 아래쪽 끝으로 마지막에 휠을 보낸 시각
    private readonly long[] edgeWheelTime = new long[2];
    private readonly Dictionary<int, Vector> heldScroll = new();
    private bool scrollFast;
    private Vector scrollRemainder;
    private DispatcherTimer? scrollTimer;
    private readonly Stopwatch scrollClock = new();

    private Rect gridScreen;
    private double gridScale = 1;
    // 단계마다 고른 칸. 비어 있으면 아직 화면 전체를 나눈 단계다.
    private readonly List<Rect> gridPath = new();

    public Controller(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        keyboardProc = KeyboardProc;
        mouseProc = MouseProc;
        foregroundProc = (_, _, hwnd, _, _, _, _) =>
        {
            scannedWindow = IntPtr.Zero;
            Task.Run(() => UiaScanner.WarmUp(hwnd));
        };
    }

    public bool InstallHooks()
    {
        if (keyboardHook != IntPtr.Zero) return true;
        IntPtr module = Native.GetModuleHandle(null);
        keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
        foregroundHook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProc, 0, 0, 0);
        if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
        {
            RemoveHooks();
            return false;
        }
        return true;
    }

    public void RemoveHooks()
    {
        Deactivate();
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
        keyboardHook = IntPtr.Zero;
        mouseHook = IntPtr.Zero;
        foregroundHook = IntPtr.Zero;
        swallowedKeys.Clear();
    }

    // 훅 콜백이 오래 걸리면 Windows가 훅을 조용히 제거하므로 무거운 작업은 콜백 밖으로 미룬다.
    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != Native.Marker)
            {
                int message = wParam.ToInt32();
                bool down = message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN;
                bool swallow = false;
                try
                {
                    swallow = down ? HandleKeyDown(info) : HandleKeyUp(info);
                }
                catch (Exception)
                {
                }
                // 1을 반환하면 이벤트를 앱과 입력기에 전달하지 않는다.
                if (swallow) return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        int message = wParam.ToInt32();
        if (code >= 0 && message != Native.WM_MOUSEMOVE && state != State.Idle)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != Native.Marker)
            {
                bool wheel = message is Native.WM_MOUSEWHEEL or Native.WM_MOUSEHWHEEL;
                bool button = message is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN
                    or Native.WM_XBUTTONDOWN;
                if (button || (wheel && state != State.Scrolling)) Deactivate();
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static int KeyId(in Native.KBDLLHOOKSTRUCT info)
    {
        uint scan = info.scanCode;
        // 원격 데스크톱이나 다른 프로그램이 만든 입력은 스캔 코드가 비어 있을 수 있다.
        if (scan == 0) scan = Native.MapVirtualKey(info.vkCode, 0);
        return (int)(scan & 0xFF) | ((info.flags & Native.LLKHF_EXTENDED) != 0 ? 0x100 : 0);
    }

    private static bool IsModifier(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    private void Swallow(int key, Modifiers modifiers)
    {
        // Alt나 Win을 누른 사이에 전달된 키가 없으면 뗄 때 메뉴 바나 시작 메뉴가 열린다.
        if (swallowedKeys.Add(key) && (modifiers.Alt || modifiers.Win))
        {
            Native.SendKey(0xE8, false);
            Native.SendKey(0xE8, true);
        }
    }

    private bool HandleKeyUp(in Native.KBDLLHOOKSTRUCT info)
    {
        if (IsModifier(info.vkCode)) return false;
        int key = KeyId(info);
        heldScroll.Remove(key);
        return swallowedKeys.Remove(key);
    }

    private bool HandleKeyDown(in Native.KBDLLHOOKSTRUCT info)
    {
        if (IsModifier(info.vkCode)) return false;
        int key = KeyId(info);
        var modifiers = Modifiers.Current();
        // 전달하지 않은 키는 시스템의 키 상태에 반영되지 않아서 따로 확인한다.
        bool autorepeat = swallowedKeys.Contains(key) || (Native.GetAsyncKeyState((int)info.vkCode) & 0x8000) != 0;

        if (ShortcutRecording.Handler is { } record)
        {
            Swallow(key, modifiers);
            if (!autorepeat) record(key, modifiers);
            return true;
        }

        Purpose? requested = null;
        if (Settings.ClickHotkey.Matches(key, modifiers))
        {
            requested = Purpose.Click;
        }
        else if (Settings.ScrollEnabled && Settings.ScrollHotkey.Matches(key, modifiers))
        {
            requested = Purpose.Scroll;
        }
        else if (Settings.GridEnabled && Settings.GridHotkey.Matches(key, modifiers))
        {
            requested = Purpose.Grid;
        }

        if (requested is { } target)
        {
            Swallow(key, modifiers);
            if (autorepeat) return true;
            bool toggledOff = state != State.Idle && purpose == target;
            Deactivate();
            if (!toggledOff) Activate(target);
            return true;
        }
        // 모드가 끝난 뒤에도 누르고 있는 라벨 키의 반복 입력이 앱에 타이핑되지 않게 한다.
        if (state == State.Idle) return swallowedKeys.Contains(key);

        if (key == KeyCodes.Escape)
        {
            Deactivate();
            Swallow(key, modifiers);
            return true;
        }

        switch (state)
        {
            case State.Picking:
                return HandlePickingKey(key, modifiers);
            case State.Scrolling:
                return HandleScrollKey(key, modifiers, autorepeat);
            default:
                Swallow(key, modifiers);
                return true;
        }
    }

    private bool HandlePickingKey(int key, Modifiers modifiers)
    {
        if (key == KeyCodes.Backspace)
        {
            Swallow(key, modifiers);
            if (typed.Length == 0 && gridPath.Count > 0)
            {
                gridPath.RemoveAt(gridPath.Count - 1);
                ShowGrid();
                return true;
            }
            if (typed.Length > 0) typed = typed[..^1];
            overlay.Update(typed);
            return true;
        }
        if (gridPath.Count > 0 && (key == KeyCodes.Enter || key == KeyCodes.Space))
        {
            var cell = gridPath[^1];
            Swallow(key, modifiers);
            Deactivate();
            Clicker.Click(new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2), modifiers);
            return true;
        }
        if (KeyCodes.Character(key) is not { } character || !Settings.LabelCharacters.Contains(character))
        {
            // 라벨 키가 아닌 단축키(Alt+Tab 등)는 모드를 끝내고 원래 동작을 하게 둔다.
            if (modifiers.Ctrl || modifiers.Alt || modifiers.Win)
            {
                Deactivate();
                return false;
            }
            Swallow(key, modifiers);
            return true;
        }

        Swallow(key, modifiers);
        string candidate = typed + character;
        if (!items.Any(item => item.Label.StartsWith(candidate, StringComparison.Ordinal))) return true;
        typed = candidate;
        int index = items.FindIndex(item => item.Label == typed);
        if (index < 0)
        {
            overlay.Update(typed);
            return true;
        }
        var match = items[index].Target;
        if (purpose == Purpose.Grid && GridDivision(match.Frame, Settings.LabelCharacters.Count, gridScale) != (1, 1))
        {
            gridPath.Add(match.Frame);
            ShowGrid();
            return true;
        }
        Deactivate();
        Clicker.Click(match.Point, modifiers);
        return true;
    }

    private bool HandleScrollKey(int key, Modifiers modifiers, bool autorepeat)
    {
        bool arrows = Settings.ScrollArrowKeys;
        Vector? direction = null;
        double? jump = null;
        bool? edge = null;
        // 휠 delta와 같은 부호를 쓴다. 세로는 양수가 위쪽, 가로는 양수가 오른쪽이다.
        switch (key)
        {
            case 0x24: direction = new Vector(0, -1); break;  // j
            case 0x25: direction = new Vector(0, 1); break;  // k
            case 0x23: direction = new Vector(-1, 0); break;  // h
            case 0x26: direction = new Vector(1, 0); break;  // l
            case KeyCodes.Down when arrows: direction = new Vector(0, -1); break;
            case KeyCodes.Up when arrows: direction = new Vector(0, 1); break;
            case KeyCodes.Left when arrows: direction = new Vector(-1, 0); break;
            case KeyCodes.Right when arrows: direction = new Vector(1, 0); break;
            case 0x20: jump = -scrollFrame.Height / 2 * WheelPerPixel; break;  // d
            case 0x16: jump = scrollFrame.Height / 2 * WheelPerPixel; break;  // u
            case 0x22: edge = modifiers.Shift; break;  // g는 맨 위, Shift+g는 맨 아래
        }
        bool hasShortcutModifier = modifiers.Ctrl || modifiers.Alt || modifiers.Win;
        // 숫자 키 1~9의 스캔 코드는 0x02부터 이어진다.
        int digit = key - 0x02;
        if (!hasShortcutModifier && digit >= 0 && digit < 9 && digit < scrollTargets.Count)
        {
            Swallow(key, modifiers);
            SelectScrollTarget(digit);
            return true;
        }
        if (hasShortcutModifier || (direction == null && jump == null && edge == null))
        {
            // 스크롤 키가 아니면 모드를 끝내고 키를 그대로 전달해서 타이핑이 사라지지 않게 한다.
            Deactivate();
            return false;
        }

        Swallow(key, modifiers);
        if (direction is { } vector)
        {
            scrollFast = modifiers.Shift;
            if (!autorepeat) heldScroll[key] = vector;
        }
        else if (jump is { } amount && !autorepeat)
        {
            PostScroll(0, (int)amount);
        }
        else if (edge is { } bottom && !autorepeat)
        {
            ScrollToEdge(bottom);
        }
        return true;
    }

    private void ScrollToEdge(bool bottom)
    {
        var element = scrollElement;
        Task.Run(() =>
        {
            if (UiaScanner.ScrollToEdge(element, bottom)) return;
            // ScrollPattern이 없는 영역은 휠 이벤트 하나로 보낼 수 있는 최대 delta만 보낸다.
            int slot = bottom ? 1 : 0;
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref edgeWheelTime[slot]) < EdgeWheelInterval) return;
            Interlocked.Exchange(ref edgeWheelTime[slot], now);
            Native.SendMouse(Native.MOUSEEVENTF_WHEEL, bottom ? -MaxWheelDelta : MaxWheelDelta);
        });
    }

    private void Activate(Purpose requested)
    {
        purpose = requested;
        if (requested == Purpose.Grid)
        {
            BeginGrid();
            return;
        }
        state = State.Scanning;
        generation += 1;
        int current = generation;
        IntPtr foreground = Native.GetForegroundWindow();
        var mode = requested == Purpose.Click ? UiaScanner.Mode.Click : UiaScanner.Mode.Scroll;
        bool includeTaskbar = Settings.IncludeTaskbar;
        bool click = requested == Purpose.Click;
        // 새로 포커스된 창은 접근성 트리를 다 만들기 전이라 첫 스캔에 요소가 일부만 나올 수 있다.
        bool rescan = click && foreground != scannedWindow;
        scannedWindow = foreground;

        Task.Run(() =>
        {
            void Report(List<Target> targets, bool final)
            {
                dispatcher.InvokeAsync(() => Receive(current, targets, final));
            }
            var result = UiaScanner.Scan(foreground, mode, includeTaskbar);
            // 스크롤 영역은 숫자 키로 고르기 때문에 보여 준 뒤에는 바꾸지 않는다.
            if (!click)
            {
                Report(result.Targets, true);
                return;
            }
            Report(result.Targets, result.Late == null && !rescan);
            if (result.Late is { } late) Report(late.Result, !rescan);
            if (!rescan) return;
            for (int index = 0; index < RescanDelays.Length; index++)
            {
                Thread.Sleep(RescanDelays[index]);
                if (generation != current) return;
                var again = UiaScanner.Scan(foreground, mode, includeTaskbar).Targets;
                Report(again, index == RescanDelays.Length - 1);
            }
        });
    }

    // final은 이번 활성화에서 더 받을 결과가 없다는 뜻이다.
    private void Receive(int current, List<Target> targets, bool final)
    {
        if (generation != current) return;
        if (state == State.Scanning)
        {
            if (targets.Count > 0)
            {
                Present(targets);
            }
            else if (final)
            {
                state = State.Idle;
                generation += 1;
                Native.MessageBeep(0);
            }
        }
        else if (state == State.Picking && purpose == Purpose.Click)
        {
            AddTargets(targets);
        }
    }

    private void AddTargets(List<Target> targets)
    {
        static bool Same(Point first, Point second)
        {
            return Math.Abs(first.X - second.X) < 4 && Math.Abs(first.Y - second.Y) < 4;
        }
        var added = targets.Where(target => !items.Any(item => Same(item.Target.Point, target.Point))).ToList();
        if (added.Count == 0) return;
        var labels = LabelGenerator.Extend(
            items.Select(item => item.Label).ToList(), added.Count, Settings.LabelCharacters);
        items = labels.Zip(items.Select(item => item.Target).Concat(added), (label, target) => new LabelItem(label, target))
            .ToList();
        overlay.Show(items);
        if (typed.Length > 0) overlay.Update(typed);
    }

    private void Present(List<Target> targets)
    {
        if (purpose == Purpose.Scroll)
        {
            BeginScrolling(targets);
            return;
        }
        var labels = LabelGenerator.Labels(targets.Count, Settings.LabelCharacters);
        items = labels.Zip(targets, (label, target) => new LabelItem(label, target)).ToList();
        typed = "";
        overlay.Show(items);
        state = State.Picking;
    }

    private void BeginGrid()
    {
        // 포커스된 창이 있는 모니터를 나눈다. 창이 없으면 커서가 있는 모니터를 쓴다.
        IntPtr foreground = Native.GetForegroundWindow();
        IntPtr handle;
        if (foreground != IntPtr.Zero)
        {
            handle = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            Native.GetCursorPos(out var cursor);
            handle = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        }
        if (Screens.Get(handle) is not { } monitor) return;
        gridScreen = monitor.Bounds;
        gridScale = monitor.Scale;
        gridPath.Clear();
        ShowGrid();
    }

    // 고른 칸을 다시 나눌 열과 행의 수. 둘 다 1이면 더 나누지 않고 클릭한다.
    private static (int Columns, int Rows) GridDivision(Rect cell, int characters, double scale)
    {
        if (Math.Max(cell.Width, cell.Height) <= GridStopCell * scale) return (1, 1);
        double finest = GridFinestCell * scale;
        int maxColumns = Math.Max((int)Math.Round(cell.Width / finest, MidpointRounding.AwayFromZero), 1);
        int maxRows = Math.Max((int)Math.Round(cell.Height / finest, MidpointRounding.AwayFromZero), 1);
        // 두 번째 단계부터는 한 글자로 고르기 때문에 칸 수를 라벨 문자 수 이하로 둔다.
        // 그 안에서 칸의 긴 변이 가장 짧아지는 조합을 고른다.
        var best = (Columns: 1, Rows: 1);
        double bestSide = double.PositiveInfinity;
        for (int columns = 1; columns <= maxColumns; columns++)
        {
            for (int rows = 1; rows <= maxRows; rows++)
            {
                if (columns * rows > characters) continue;
                double side = Math.Max(cell.Width / columns, cell.Height / rows);
                if (side < bestSide)
                {
                    best = (columns, rows);
                    bestSide = side;
                }
            }
        }
        return best;
    }

    private void ShowGrid()
    {
        var characters = Settings.LabelCharacters;
        Rect? cell = gridPath.Count > 0 ? gridPath[^1] : null;
        var region = cell ?? gridScreen;
        int columns;
        int rows;
        if (cell == null)
        {
            columns = Math.Min(characters.Count, Math.Max((int)(region.Width / (90 * gridScale)), 2));
            rows = Math.Min(characters.Count, Math.Max((int)(region.Height / (60 * gridScale)), 2));
        }
        else
        {
            (columns, rows) = GridDivision(region, characters.Count, gridScale);
        }
        double width = region.Width / columns;
        double height = region.Height / rows;

        var cells = new List<LabelItem>(rows * columns);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                string label = cell == null
                    ? $"{characters[row]}{characters[column]}"
                    : characters[row * columns + column].ToString();
                var frame = new Rect(region.X + column * width, region.Y + row * height, width, height);
                cells.Add(new LabelItem(label, new Target(frame)));
            }
        }
        items = cells;
        typed = "";
        state = State.Picking;
        // 라벨이 많아서 그리는 데 시간이 걸린다. 훅 콜백 안에서 그리면 훅이 시간 초과로 제거된다.
        int current = generation;
        dispatcher.InvokeAsync(() =>
        {
            if (generation != current || state != State.Picking) return;
            overlay.Show(cells, cell, pointers: false, cells: true);
        });
    }

    private void BeginScrolling(List<Target> targets)
    {
        static double Area(Target target) => target.Frame.Width * target.Frame.Height;
        // 숫자 키 1~9로 고르기 때문에 큰 영역 9개까지만 쓴다.
        scrollTargets = targets.OrderByDescending(Area).Take(9)
            .OrderBy(target => target.Frame.Y).ThenBy(target => target.Frame.X).ToList();
        state = State.Scrolling;
        heldScroll.Clear();
        scrollRemainder = new Vector();

        // 커서가 놓인 영역 중 가장 작은 것을 먼저 고르고, 없으면 가장 큰 영역을 고른다.
        Native.GetCursorPos(out var position);
        var cursor = new Point(position.X, position.Y);
        var indices = Enumerable.Range(0, scrollTargets.Count).ToList();
        var underCursor = indices.Where(index => scrollTargets[index].Frame.Contains(cursor))
            .OrderBy(index => Area(scrollTargets[index])).Cast<int?>().FirstOrDefault();
        int largest = indices.OrderByDescending(index => Area(scrollTargets[index])).First();
        SelectScrollTarget(underCursor ?? largest, moveCursor: underCursor == null);

        scrollClock.Restart();
        scrollTimer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(8),
        };
        scrollTimer.Tick += (_, _) => ScrollTick();
        scrollTimer.Start();
    }

    private void SelectScrollTarget(int index, bool moveCursor = true)
    {
        var target = scrollTargets[index];
        scrollFrame = target.Frame;
        scrollElement = target.Element;
        heldScroll.Clear();

        // 휠 이벤트는 커서 아래의 창으로 전달되므로 커서를 선택한 영역 안으로 옮긴다.
        if (moveCursor) Native.SetCursorPos((int)target.Point.X, (int)target.Point.Y);

        var numbers = new List<LabelItem>();
        if (scrollTargets.Count > 1)
        {
            for (int offset = 0; offset < scrollTargets.Count; offset++)
            {
                var frame = scrollTargets[offset].Frame;
                var corner = new Rect(frame.X + 8, frame.Y + 8, 20, 20);
                numbers.Add(new LabelItem((offset + 1).ToString(), new Target(corner)));
            }
        }
        overlay.Show(numbers, target.Frame, pointers: false);
    }

    private void ScrollTick()
    {
        double elapsed = Math.Min(scrollClock.Elapsed.TotalMilliseconds, 50);
        scrollClock.Restart();
        if (state != State.Scrolling || heldScroll.Count == 0)
        {
            scrollRemainder = new Vector();
            return;
        }
        double speed = Settings.ScrollSpeed * (scrollFast ? 3 : 1) * elapsed * WheelPerPixel;
        foreach (var direction in heldScroll.Values) scrollRemainder += direction * speed;

        // 120보다 작은 delta를 무시하는 앱에서는 한 칸 단위로만 보낸다.
        double unit = Settings.ScrollWholeNotches ? 120 : 1;
        double dx = Math.Truncate(scrollRemainder.X / unit) * unit;
        double dy = Math.Truncate(scrollRemainder.Y / unit) * unit;
        scrollRemainder -= new Vector(dx, dy);
        if (dx != 0 || dy != 0) PostScroll((int)dx, (int)dy);
    }

    private static void PostScroll(int dx, int dy)
    {
        PostWheel(Native.MOUSEEVENTF_WHEEL, dy);
        PostWheel(Native.MOUSEEVENTF_HWHEEL, dx);
    }

    private static void PostWheel(uint flag, int delta)
    {
        for (int sent = 0; delta != 0 && sent < 40; sent++)
        {
            int chunk = Math.Clamp(delta, -MaxWheelDelta, MaxWheelDelta);
            Native.SendMouse(flag, chunk);
            delta -= chunk;
        }
    }

    private void Deactivate()
    {
        state = State.Idle;
        generation += 1;
        items = new List<LabelItem>();
        typed = "";
        gridPath.Clear();
        scrollTargets = new List<Target>();
        scrollElement = null;
        heldScroll.Clear();
        scrollTimer?.Stop();
        scrollTimer = null;
        overlay.Hide();
    }
}

internal static class Clicker
{
    // Shift 우클릭, Alt 더블클릭, Ctrl 컨트롤 클릭, Win 클릭 없이 커서만 이동
    public static void Click(Point point, Modifiers modifiers)
    {
        bool hoverOnly = modifiers.Win;
        bool rightClick = modifiers.Shift;
        int clickCount = modifiers.Alt ? 2 : 1;
        uint down = rightClick ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN;
        uint up = rightClick ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP;

        Task.Run(() =>
        {
            // 누르고 있는 Shift와 Alt가 클릭에 섞이면 Shift+우클릭, Alt+더블클릭으로 처리된다.
            if (modifiers.Shift) Release(0xA0, 0xA1);
            if (modifiers.Alt) Release(0xA4, 0xA5);
            Native.SetCursorPos((int)point.X, (int)point.Y);
            if (hoverOnly) return;
            // 커서가 도착한 뒤에 눌러야 hover 상태를 요구하는 요소도 클릭된다.
            Thread.Sleep(20);
            for (int count = 0; count < clickCount; count++)
            {
                Native.SendMouse(down);
                Thread.Sleep(10);
                Native.SendMouse(up);
                Thread.Sleep(10);
            }
        });
    }

    private static void Release(params int[] keys)
    {
        foreach (int vk in keys)
        {
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) Native.SendKey(vk, true);
        }
    }
}
