using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexRelay.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var root = args.Length == 2 && args[0] == "--root"
            ? Path.GetFullPath(args[1])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RelayTray(root));
    }
}

internal sealed class RelayTray : ApplicationContext
{
    private const string TaskName = "CodexRelay";
    private readonly string _root;
    private readonly NotifyIcon _notify;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _start;
    private readonly Icon _readyIcon = CreateIcon(Color.FromArgb(44, 190, 127));
    private readonly Icon _warningIcon = CreateIcon(Color.FromArgb(239, 178, 61));
    private readonly Icon _stoppedIcon = CreateIcon(Color.FromArgb(224, 82, 82));
    private State _lastState = State.Unknown;

    public RelayTray(string root)
    {
        _root = root;
        _status = new ToolStripMenuItem("Проверка состояния…") { Enabled = false };
        _start = new ToolStripMenuItem("Запустить", null, async (_, _) => await ControlTask("/run"));
        var restart = new ToolStripMenuItem("Перезапустить", null, async (_, _) =>
        {
            await ControlTask("/end", showError: false);
            await Task.Delay(800);
            await ControlTask("/run");
        });
        var log = new ToolStripMenuItem("Открыть журнал", null, (_, _) => OpenLog());
        var exit = new ToolStripMenuItem("Закрыть значок", null, (_, _) => ExitThread());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _status, new ToolStripSeparator(), _start, restart,
            new ToolStripSeparator(), log, exit
        });
        _notify = new NotifyIcon
        {
            Icon = _warningIcon,
            Text = "Codex Relay: проверка",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notify.DoubleClick += (_, _) => OpenLog();
        _timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var (state, label) = Probe();
        _status.Text = label;
        _start.Enabled = state is State.Stopped or State.Unknown;
        _notify.Text = "Codex Relay: " + label;
        if (state == _lastState) return;
        _lastState = state;
        _notify.Icon = state switch
        {
            State.Ready => _readyIcon,
            State.Starting or State.Degraded => _warningIcon,
            _ => _stoppedIcon
        };
    }

    private (State, string) Probe()
    {
        var path = Path.Combine(_root, "logs", "health.txt");
        if (!File.Exists(path)) return (State.Unknown, "нет сигнала от бота");
        try
        {
            var parts = File.ReadAllText(path).Trim().Split('|');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], out var pid) ||
                !long.TryParse(parts[2], out var seconds))
                return (State.Unknown, "сигнал повреждён");
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return (State.Stopped, "остановлен");
            var reported = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (process.StartTime.ToUniversalTime() > reported.UtcDateTime.AddSeconds(2))
                return (State.Stopped, "остановлен");
            if (DateTimeOffset.UtcNow - reported > TimeSpan.FromSeconds(90))
                return (State.Degraded, "нет связи с Telegram");
            return parts[1] switch
            {
                "ready" => (State.Ready, "работает"),
                "starting" => (State.Starting, "запускается"),
                "error" => (State.Degraded, "нет связи с Telegram"),
                _ => (State.Unknown, "состояние неизвестно")
            };
        }
        catch (ArgumentException) { return (State.Stopped, "остановлен"); }
        catch (InvalidOperationException) { return (State.Stopped, "остановлен"); }
        catch (IOException) { return (State.Unknown, "сигнал недоступен"); }
        catch (UnauthorizedAccessException) { return (State.Unknown, "сигнал недоступен"); }
    }

    private async Task ControlTask(string verb, bool showError = true)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            info.ArgumentList.Add(verb);
            info.ArgumentList.Add("/tn");
            info.ArgumentList.Add(TaskName);
            using var process = Process.Start(info) ??
                throw new InvalidOperationException("Не удалось открыть schtasks.exe.");
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 && showError)
                throw new InvalidOperationException((await error).Trim());
            await Task.Delay(1000);
            Refresh();
        }
        catch (Exception ex)
        {
            if (showError)
                _notify.ShowBalloonTip(4000, "Codex Relay", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OpenLog()
    {
        var path = Path.Combine(_root, "logs", "relay.log");
        var target = File.Exists(path) ? path : _root;
        Process.Start(new ProcessStartInfo(File.Exists(path) ? "notepad.exe" : "explorer.exe", target)
            { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _notify.Visible = false;
        _timer.Dispose();
        _notify.ContextMenuStrip?.Dispose();
        _notify.Dispose();
        _readyIcon.Dispose();
        _warningIcon.Dispose();
        _stoppedIcon.Dispose();
        base.ExitThreadCore();
    }

    private static Icon CreateIcon(Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var baseBrush = new SolidBrush(Color.FromArgb(29, 39, 53));
            using var accentBrush = new SolidBrush(accent);
            graphics.FillEllipse(baseBrush, 1, 1, 30, 30);
            graphics.FillEllipse(accentBrush, 6, 6, 20, 20);
        }
        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private enum State { Unknown, Starting, Ready, Degraded, Stopped }
}