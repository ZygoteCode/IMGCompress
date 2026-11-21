using Carbonado.Core.Engine;
using Carbonado.Core.Persistence;

namespace Carbonado.App.Services;

/// <summary>Built-in profiles plus the user's custom profiles stored in <see cref="AppSettings"/>.</summary>
public sealed class ProfileCatalog : IProfileCatalog
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _persist;

    public ProfileCatalog(AppSettings settings, Action<AppSettings> persist)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(persist);
        _settings = settings;
        _persist = persist;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CompressionProfile> All => [.. BuiltInProfiles.All, .. _settings.CustomProfiles];

    public CompressionProfile Active => Find(_settings.ActiveProfileId) ?? BuiltInProfiles.Default;

    public CompressionProfile? Find(string id) =>
        BuiltInProfiles.Find(id) ?? _settings.CustomProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public void SetActive(string id)
    {
        if (Find(id) is null || string.Equals(_settings.ActiveProfileId, id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.ActiveProfileId = id;
        _persist(_settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveCustom(CompressionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (BuiltInProfiles.Find(profile.Id) is not null)
        {
            throw new InvalidOperationException($"'{profile.Id}' is a built-in profile.");
        }

        var custom = profile with { BuiltIn = false };
        int index = _settings.CustomProfiles.FindIndex(p => string.Equals(p.Id, custom.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _settings.CustomProfiles[index] = custom;
        }
        else
        {
            _settings.CustomProfiles.Add(custom);
        }

        _persist(_settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteCustom(string id)
    {
        int removed = _settings.CustomProfiles.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return;
        }

        if (string.Equals(_settings.ActiveProfileId, id, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ActiveProfileId = BuiltInProfiles.Default.Id;
        }

        _persist(_settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A fresh id for a copy of <paramref name="source"/> ("vs-copy", "vs-copy-2", …).</summary>
    public string NewIdFor(CompressionProfile source)
    {
        string stem = source.Id.Replace("-copy", string.Empty, StringComparison.Ordinal) + "-copy";
        string id = stem;
        for (int n = 2; Find(id) is not null; n++)
        {
            id = $"{stem}-{n}";
        }

        return id;
    }
}
