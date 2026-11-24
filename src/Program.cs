using System;
using System.Diagnostics;
using System.Linq;
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

        using var mutex = new Mutex(true, @"Local\Unmouse.SingleInstance", out bool created);
        if (!created) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new Controller(app.Dispatcher);
        if (!controller.InstallHooks())
        {
            MessageBox.Show("Failed to install the keyboard hook.", "Unmouse");
            return 1;
        }
        Settings.Watch();

        SettingsWindow? settings = null;
        void OpenSettings()
        {
            if (settings == null)
            {
                settings = new SettingsWindow(() => app.Shutdown());
                settings.Closed += (_, _) => settings = null;
            }
            settings.Show();
            settings.Activate();
        }
        using var tray = new Tray(OpenSettings, () => app.Shutdown());
        app.Run();
        controller.RemoveHooks();
        return 0;
    }

    // 사용법: Unmouse.exe --scan [--scroll]. 3초 뒤에 포커스된 창에서 라벨이 붙을 요소 수와 스캔 시간을 보여 준다.
    private static int RunScan(bool scroll)
    {
        Thread.Sleep(3000);
        IntPtr foreground = Native.GetForegroundWindow();
        var mode = scroll ? UiaScanner.Mode.Scroll : UiaScanner.Mode.Click;
        var clock = Stopwatch.StartNew();
        var targets = UiaScanner.Scan(foreground, mode, includeTaskbar: false, timeout: 10_000);
        clock.Stop();
        MessageBox.Show(
            $"{Native.ClassName(foreground)}: targets {targets.Count}, {clock.ElapsedMilliseconds}ms", "Unmouse");
        return 0;
    }
}

internal sealed class Tray : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon icon;

    public Tray(Action openSettings, Action quit)
    {
        var settings = new System.Windows.Forms.ToolStripMenuItem();
        settings.Click += (_, _) => openSettings();
        var login = new System.Windows.Forms.ToolStripMenuItem();
        login.Click += (_, _) => LoginItem.Enabled = !LoginItem.Enabled;
        var exit = new System.Windows.Forms.ToolStripMenuItem();
        exit.Click += (_, _) => quit();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(settings);
        menu.Items.Add(login);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        // 언어 설정과 로그인 항목 상태는 메뉴를 열 때마다 다시 읽는다.
        menu.Opening += (_, _) =>
        {
            settings.Text = Settings.Tr("Settings…", "설정…");
            login.Text = Settings.Tr("Launch at Login", "로그인 시 실행");
            login.Checked = LoginItem.Enabled;
            exit.Text = Settings.Tr("Quit Unmouse", "Unmouse 종료");
        };

        icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, Text = "Unmouse", ContextMenuStrip = menu, Visible = true,
        };
        icon.DoubleClick += (_, _) => openSettings();
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
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
