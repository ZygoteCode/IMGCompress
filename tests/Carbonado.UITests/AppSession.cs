using System.Diagnostics;
using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Carbonado.UITests;

/// <summary>
/// Launches the real Carbonado.exe with an isolated data folder and drives it through UI Automation.
/// Set CARBONADO_EXE to test a published build (e.g. artifacts\Carbonado\Carbonado.exe).
/// </summary>
public sealed class AppSession : IDisposable
{
    private AppSession(Application app, UIA3Automation automation, Window main, string dataDir)
    {
        App = app;
        Automation = automation;
        Main = main;
        DataDir = dataDir;
    }

    public Application App { get; }

    public UIA3Automation Automation { get; }

    public Window Main { get; }

    public string DataDir { get; }

    public int ProcessId => App.ProcessId;

    public static string ExecutablePath
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("CARBONADO_EXE");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return env;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Carbonado.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                throw new InvalidOperationException("Repository root not found.");
            }

            // Same configuration as the test assembly (Debug/Release).
            string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            string exe = Path.Combine(dir.FullName, "src", "Carbonado.App", "bin", config, "net11.0-windows", "Carbonado.exe");
            return File.Exists(exe) ? exe : throw new FileNotFoundException("Build Carbonado.App first.", exe);
        }
    }

    /// <summary>Starts the app (isolated data folder, never forwarding to a running instance) and waits for its window.</summary>
    public static AppSession Launch(IEnumerable<string>? args = null, TimeSpan? timeout = null, string? dataDir = null)
    {
        dataDir ??= Path.Combine(Path.GetTempPath(), "carbonado-uitests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dataDir);
        var psi = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExecutablePath)! };
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--new-instance");
        foreach (string a in args ?? [])
        {
            psi.ArgumentList.Add(a);
        }

        var app = Application.Launch(psi);
        var automation = new UIA3Automation();
        var main = app.GetMainWindow(automation, timeout ?? TimeSpan.FromSeconds(20))
                   ?? throw new TimeoutException("Main window did not appear.");
        return new AppSession(app, automation, main, dataDir);
    }

    /// <summary>Finds an element by AutomationId, polling until <paramref name="timeout"/>.</summary>
    public AutomationElement? Find(string automationId, TimeSpan? timeout = null) =>
        Retry.WhileNull(() => Main.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), timeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)).Result;

    public AutomationElement Get(string automationId, TimeSpan? timeout = null) =>
        Find(automationId, timeout) ?? throw new InvalidOperationException($"Element '{automationId}' not found.");

    /// <summary>Waits until no element with <paramref name="automationId"/> is visible.</summary>
    public bool WaitGone(string automationId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var e = Main.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (e is null || e.IsOffscreen)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    public void Click(string automationId) => Get(automationId).Click();

    /// <summary>Startup milestones written by the app (logs\startup-last.txt), in milliseconds since process start.</summary>
    public IReadOnlyDictionary<string, double> StartupMarks(TimeSpan? timeout = null)
    {
        string file = Path.Combine(DataDir, "logs", "startup-last.txt");
        var sw = Stopwatch.StartNew();
        while (!File.Exists(file) && sw.Elapsed < (timeout ?? TimeSpan.FromSeconds(10)))
        {
            Thread.Sleep(50);
        }

        if (!File.Exists(file))
        {
            return new Dictionary<string, double>();
        }

        return File.ReadAllLines(file)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => double.Parse(p[1], CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        try
        {
            if (!App.HasExited)
            {
                App.Close();
                App.WaitWhileMainHandleIsMissing(TimeSpan.FromMilliseconds(10));
                if (!WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    App.Kill();
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            Automation.Dispose();
            App.Dispose();
            TryDelete(DataDir);
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(App.ProcessId);
            return p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void TryDelete(string dir)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
