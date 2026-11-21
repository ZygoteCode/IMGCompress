using System.Reflection;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.App.Startup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Pages.About;

/// <summary>About page: version, measured startup time, licenses and project link.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/ZygoteCode/IMGCompress";
    private readonly IShellLauncher _shell;
    private readonly Lazy<string> _licenses = new(LoadLicenses);

    public AboutViewModel(IShellLauncher shell)
    {
        _shell = shell;
        Loc.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public string Version { get; } =
        typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "7.0.0";

    public string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    public string StartupText => StartupProfiler.FirstFrameMs is { } ms
        ? Loc.Format("L.About.Startup", ms.ToString("0", Loc.Culture), (StartupProfiler.InteractiveMs ?? ms).ToString("0", Loc.Culture))
        : string.Empty;

    public string Licenses => _licenses.Value;

    [RelayCommand]
    private void OpenRepository() => _shell.OpenUrl(RepositoryUrl);

    private static string LoadLicenses()
    {
        using var stream = typeof(AboutViewModel).Assembly.GetManifestResourceStream("ThirdPartyNotices.md");
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
