using System.Text;

namespace Carbonado.Cli;

/// <summary>
/// <c>carbonado-cli</c>: the command-line front end of the Carbonado engine. Ctrl+C cancels the batch cleanly (no
/// partial outputs, every tool stopped); a second Ctrl+C ends the process at once.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached (output redirected by a host that forbids it): keep the default encoding.
        }

        using var cancel = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            if (!cancel.IsCancellationRequested)
            {
                e.Cancel = true;
                Console.Error.WriteLine("carbonado: cancelling…");
                cancel.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            return await CliRunner.RunAsync(args, Console.Out, Console.Error, cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }
}
