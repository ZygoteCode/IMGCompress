using Carbonado.Core.Engine;
using Carbonado.Core.Logging;
using Carbonado.Core.Tools;

namespace Carbonado.App.Services;

/// <summary>
/// Connects the real engine to the UI's proxy (<see cref="DeferredBatchProcessor"/>) once the encoders are located, on
/// the background start after the first frame. The engine owns a private <see cref="ProcessRunner"/> (its own Job
/// Object), so shutdown kills every encoder of every running job at once.
/// </summary>
internal static class EngineBootstrap
{
    private static readonly Lock s_gate = new();
    private static EngineContext? s_context;
    private static BatchScheduler? s_scheduler;

    /// <summary>Creates the engine and attaches it; queued jobs start flowing. Idempotent.</summary>
    /// <exception cref="IOException">The engine's session folder cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The temp root is not writable.</exception>
    public static void TryAttach(AppServices services)
    {
        lock (s_gate)
        {
            if (services.Engine.IsAttached || s_context is not null)
            {
                return;
            }

            var locator = services.Tools.Locator ?? services.Tools.Locate();
            var options = new EngineOptions(MaxConcurrency: services.Settings.MaxConcurrency, TempRoot: services.Paths.TempRoot);
            s_context = EngineContext.Create(locator, options);
            s_scheduler = new BatchScheduler(new CompressionEngine(s_context), options);
            services.Engine.Attach(s_scheduler);
            Log.Info($"Engine attached ({s_context.Encoders.Available.Count} encoders, session {Path.GetFileName(s_context.SessionTemp)}).");
        }
    }

    /// <summary>Instant stop with the default 150 ms budget for the engine's temp folder.</summary>
    public static void Shutdown() => Shutdown(TimeSpan.FromMilliseconds(150));

    /// <summary>
    /// Instant stop: cancel every job and kill every encoder process now (job objects), then let the engine's session
    /// folder go in the background for at most <paramref name="tempBudget"/> (leftovers are wiped at the next start).
    /// </summary>
    public static void Shutdown(TimeSpan tempBudget)
    {
        EngineContext? context;
        BatchScheduler? scheduler;
        lock (s_gate)
        {
            context = s_context;
            scheduler = s_scheduler;
            s_context = null;
            s_scheduler = null;
        }

        try
        {
            scheduler?.CancelAll();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            Log.Warn($"Engine cancel on shutdown: {e.Message}");
        }

        context?.Runner.KillAll();
        ProcessRunner.Shared.KillAll();
        if (context is not null)
        {
            Task.Run(context.Dispose).Wait(tempBudget);
        }
    }
}
