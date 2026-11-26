using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace Unmouse;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Settings.Load();
        if (args.Contains("--scan")) return RunScan(args.Contains("--scroll"));

        using var mutex = AcquireSingleInstance(args.Contains(Elevation.RestartArgument));
        if (mutex == null) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new Controller(app.Dispatcher);
        if (!controller.InstallHooks())
        {
            MessageBox.Show("Failed to install the keyboard hook.", "Unmouse");
            return 1;
        }
        Settings.Watch();

        void RestartElevated()
        {
            if (Elevation.StartElevated()) app.Shutdown();
        }
        SettingsWindow? settings = null;
        void OpenSettings()
        {
            if (settings == null)
            {
                settings = new SettingsWindow(() => app.Shutdown(), RestartElevated);
                settings.Closed += (_, _) => settings = null;
            }
            settings.Show();
            settings.Activate();
        }
        using var tray = new Tray(OpenSettings, RestartElevated, () => app.Shutdown());
        app.Run();
        controller.RemoveHooks();
        mutex.ReleaseMutex();
        return 0;
    }

    private static Mutex? AcquireSingleInstance(bool restarting)
    {
        try
        {
            var mutex = new Mutex(false, @"Local\Unmouse.SingleInstance");
            try
            {
                // 관리자 권한으로 다시 시작한 경우에는 이전 프로세스가 끝날 때까지 기다린다.
                if (mutex.WaitOne(restarting ? 5000 : 0)) return mutex;
            }
            catch (AbandonedMutexException)
            {
                return mutex;
            }
            mutex.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            // 관리자 권한으로 실행 중인 프로세스가 만든 뮤텍스는 일반 권한으로 열지 못한다.
        }
        return null;
    }

    // 사용법: Unmouse.exe --scan [--scroll]. 3초 뒤에 포커스된 창에서 라벨이 붙을 요소 수와 스캔 시간을 보여 준다.
    private static int RunScan(bool scroll)
    {
        Thread.Sleep(3000);
        IntPtr foreground = Native.GetForegroundWindow();
        var mode = scroll ? UiaScanner.Mode.Scroll : UiaScanner.Mode.Click;
        var clock = Stopwatch.StartNew();
        var targets = UiaScanner.Scan(foreground, mode, includeTaskbar: false, timeout: 10_000).Targets;
        clock.Stop();
        MessageBox.Show(
            $"{Native.ClassName(foreground)}: targets {targets.Count}, {clock.ElapsedMilliseconds}ms", "Unmouse");
        return 0;
    }
}

internal sealed class Tray : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon icon;

    public Tray(Action openSettings, Action restartElevated, Action quit)
    {
        var settings = new System.Windows.Forms.ToolStripMenuItem();
        settings.Click += (_, _) => openSettings();
        var login = new System.Windows.Forms.ToolStripMenuItem();
        login.Click += (_, _) => LoginItem.Enabled = !LoginItem.Enabled;
        var elevate = new System.Windows.Forms.ToolStripMenuItem { Visible = !Elevation.IsElevated };
        elevate.Click += (_, _) => restartElevated();
        var exit = new System.Windows.Forms.ToolStripMenuItem();
        exit.Click += (_, _) => quit();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(settings);
        menu.Items.Add(login);
        menu.Items.Add(elevate);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        // 언어 설정과 로그인 항목 상태는 메뉴를 열 때마다 다시 읽는다.
        menu.Opening += (_, _) =>
        {
            settings.Text = Settings.Tr("Settings…", "설정…");
            login.Text = Settings.Tr("Launch at Login", "로그인 시 실행");
            login.Checked = LoginItem.Enabled;
            elevate.Text = Settings.Tr("Restart as Administrator", "관리자 권한으로 다시 시작");
            exit.Text = Settings.Tr("Quit Unmouse", "Unmouse 종료");
        };

        icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = Elevation.IsElevated ? "Unmouse (Administrator)" : "Unmouse",
            ContextMenuStrip = menu, Visible = true,
        };
        icon.DoubleClick += (_, _) => openSettings();

        if (!Elevation.IsElevated && Settings.Flag("adminHint"))
        {
            icon.BalloonTipClicked += (_, _) => restartElevated();
            icon.ShowBalloonTip(
                8000, "Unmouse",
                Settings.Tr(
                    "Unmouse does not work in windows that run as administrator. Click here to restart it as administrator.",
                    "관리자 권한으로 실행된 창에서는 Unmouse가 동작하지 않습니다. 여기를 누르면 관리자 권한으로 다시 시작합니다."),
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}

internal static class Elevation
{
    public const string RestartArgument = "--restart";

    public static readonly bool IsElevated = Check();

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // UAC 창에서 취소하면 false를 돌려준다.
    public static bool StartElevated()
    {
        if (Environment.ProcessPath is not { } path) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true, Verb = "runas", Arguments = RestartArgument,
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

internal static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Unmouse";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    // exe를 옮긴 뒤에는 등록된 경로가 맞지 않으므로 꺼진 것으로 본다.
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string == Command;
        }
        set
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (value)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
