using Carbonado.App.Startup;

namespace Carbonado.App;

/// <summary>
/// Entry point. A second launch forwards its arguments to the running window over a named pipe and exits before
/// any WPF initialisation (Explorer multi-select lands in one window).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupProfiler.Mark("main");
        var options = CommandLine.Parse(args);
        SingleInstance? instance = null;
        if (!options.NewInstance)
        {
            instance = SingleInstance.For(options.DataDir);
            if (!instance.IsPrimary)
            {
                bool forwarded = instance.Forward(args.Length > 0 ? args : ["--activate"]);
                instance.Dispose();
                if (forwarded)
                {
                    return 0;
                }

                // The primary instance did not answer (hung or closing): run as a new instance.
                instance = null;
            }
        }

        // Primary instance from here on: boot the render engine and read settings while WPF initialises.
        Warmup.StartRenderEngine();
        var app = new App(options, instance, AppServices.PreloadAsync(options));
        return app.Run();
    }
}
