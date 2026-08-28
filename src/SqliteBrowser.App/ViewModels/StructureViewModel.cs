using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqliteBrowser.Core.Models;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.App.ViewModels;

/// <summary>
/// Backs the Structure tab: a schema tree grouped into Tables/Views/Indexes/Triggers, and the
/// selected object's DDL + column details. Schema-mutating commands that require user input (a DDL
/// dialog, a delete confirmation) are exposed as plain async methods here so they are independently
/// testable; <see cref="MainWindowViewModel"/> wires the dialogs and calls them.
/// </summary>
public sealed partial class StructureViewModel : ViewModelBase
{
    private DatabaseSession? _session;

    public ObservableCollection<SchemaGroupViewModel> Groups { get; } =
    [
        new SchemaGroupViewModel("Tables"),
        new SchemaGroupViewModel("Views"),
        new SchemaGroupViewModel("Indexes"),
        new SchemaGroupViewModel("Triggers"),
    ];

    private SchemaGroupViewModel TablesGroup => Groups[0];
    private SchemaGroupViewModel ViewsGroup => Groups[1];
    private SchemaGroupViewModel IndexesGroup => Groups[2];
    private SchemaGroupViewModel TriggersGroup => Groups[3];

    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private string? _selectedDdl;

    [ObservableProperty]
    private ObservableCollection<ColumnDefinition>? _selectedColumns;

    [ObservableProperty]
    private bool _isDatabaseOpen;

    public SchemaObjectViewModel? SelectedObject => SelectedNode as SchemaObjectViewModel;

    partial void OnSelectedNodeChanged(object? value) => _ = LoadSelectedDetailsAsync();

    /// <summary>Attaches (or detaches, when <paramref name="session"/> is <c>null</c>) the session this tab reflects.</summary>
    public void AttachSession(DatabaseSession? session)
    {
        _session = session;
        IsDatabaseOpen = session is not null;
        SelectedNode = null;
        SelectedDdl = null;
        SelectedColumns = null;

        foreach (var group in Groups)
        {
            group.Items.Clear();
            group.NotifyItemsChanged();
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
            var objects = await _session.GetDatabaseObjectsAsync().ConfigureAwait(true);

            foreach (var group in Groups)
            {
                group.Items.Clear();
            }

            foreach (var obj in objects.Where(o => !o.Name.StartsWith("sqlite_", StringComparison.Ordinal)))
            {
                var node = new SchemaObjectViewModel(obj);
                var group = obj.Type switch
                {
                    "table" => TablesGroup,
                    "view" => ViewsGroup,
                    "index" => IndexesGroup,
                    "trigger" => TriggersGroup,
                    _ => null,
                };

                group?.Items.Add(node);
            }

            foreach (var group in Groups)
            {
                group.NotifyItemsChanged();
            }

            StatusMessage = $"{objects.Count} schema object(s).";
        });
    }

    private async Task LoadSelectedDetailsAsync()
    {
        OnPropertyChanged(nameof(SelectedObject));
        if (_session is null || SelectedObject is not { } node)
        {
            SelectedDdl = null;
            SelectedColumns = null;
            return;
        }

        SelectedDdl = node.Sql ?? $"-- '{node.Name}' has no stored SQL (implicit object).";

        if (node.Type is "table" or "view")
        {
            await RunGuardedAsync(async () =>
            {
                var columns = await _session.GetColumnsAsync(node.Schema, node.Name).ConfigureAwait(true);
                SelectedColumns = new ObservableCollection<ColumnDefinition>(columns);
            });
        }
        else
        {
            SelectedColumns = null;
        }
    }

    /// <summary>Executes arbitrary DDL (e.g. from the New/Edit dialog) and refreshes the tree.</summary>
    public async Task ExecuteDdlAsync(string ddl)
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            var result = await _session.ExecuteSqlAsync(ddl).ConfigureAwait(true);
            StatusMessage = result.Message;
            NotifyMutated();
            await RefreshAsync().ConfigureAwait(true);
        });
    }

    /// <summary>Builds the <c>DROP ...</c> statement for the given node (does not execute it).</summary>
    public static string BuildDropStatement(SchemaObjectViewModel node)
    {
        string keyword = node.Type.ToUpperInvariant();
        return $"DROP {keyword} {SqlIdentifier.QuoteQualified(node.Schema, node.Name)};";
    }

    /// <summary>Drops the given schema object and refreshes the tree.</summary>
    public async Task DeleteObjectAsync(SchemaObjectViewModel node)
    {
        if (_session is null)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            await _session.ExecuteSqlAsync(BuildDropStatement(node)).ConfigureAwait(true);
            SelectedNode = null;
            StatusMessage = $"Dropped {node.Type} '{node.Name}'.";
            NotifyMutated();
            await RefreshAsync().ConfigureAwait(true);
        });
    }
}
