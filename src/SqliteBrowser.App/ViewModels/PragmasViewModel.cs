using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.App.ViewModels;

/// <summary>A single editable PRAGMA row shown on the Pragmas tab.</summary>
public sealed partial class PragmaRowViewModel(string name, string description) : ObservableObject
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>
/// Backs the Pragmas tab: loads and lets the user edit a curated set of commonly-tuned SQLite
/// PRAGMAs via <see cref="DatabaseSession.GetPragmaAsync"/>/<see cref="DatabaseSession.SetPragmaAsync"/>.
/// </summary>
public sealed partial class PragmasViewModel : ViewModelBase
{
    private DatabaseSession? _session;

    public ObservableCollection<PragmaRowViewModel> Pragmas { get; } =
    [
        new("foreign_keys", "Enforce foreign key constraints (ON/OFF)."),
        new("journal_mode", "Rollback journal mode (DELETE, WAL, MEMORY, OFF, ...)."),
        new("synchronous", "Disk sync durability level (OFF, NORMAL, FULL, EXTRA)."),
        new("cache_size", "Suggested page cache size (negative = KiB, positive = pages)."),
        new("auto_vacuum", "Auto-vacuum mode (NONE, FULL, INCREMENTAL)."),
        new("user_version", "User-defined schema version integer."),
        new("application_id", "User-defined 32-bit application identifier."),
        new("recursive_triggers", "Whether triggers may recursively fire (ON/OFF)."),
        new("temp_store", "Where temporary tables/indexes are stored (DEFAULT, FILE, MEMORY)."),
        new("secure_delete", "Overwrite deleted content with zeros (ON/OFF/FAST)."),
        new("busy_timeout", "Milliseconds to retry when the database is locked."),
        new("encoding", "Text encoding (read-only once the database has been created)."),
    ];

    [ObservableProperty]
    private bool _isDatabaseOpen;

    [ObservableProperty]
    private bool _isReadOnly;

    public void AttachSession(DatabaseSession? session)
    {
        _session = session;
        IsDatabaseOpen = session is not null;
        IsReadOnly = session?.IsReadOnly ?? false;
        foreach (var row in Pragmas)
        {
            row.Value = string.Empty;
        }

        if (session is not null)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            foreach (var row in Pragmas)
            {
                var value = await _session.GetPragmaAsync(row.Name).ConfigureAwait(true);
                row.Value = value.ScalarValue?.ToString() ?? string.Empty;
            }

            StatusMessage = "Pragmas refreshed.";
        });
    }

    /// <summary>Applies a single pragma's edited value.</summary>
    public async Task ApplyAsync(PragmaRowViewModel row)
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            await _session.SetPragmaAsync(row.Name, row.Value).ConfigureAwait(true);
            var refreshed = await _session.GetPragmaAsync(row.Name).ConfigureAwait(true);
            row.Value = refreshed.ScalarValue?.ToString() ?? string.Empty;
            StatusMessage = $"Applied {row.Name} = {row.Value}.";
            NotifyMutated();
        });
    }
}
