using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqliteBrowser.Core.Models;

namespace SqliteBrowser.App.ViewModels;

/// <summary>A leaf node in the Structure tab's schema tree: one table, view, index or trigger.</summary>
public sealed class SchemaObjectViewModel(DatabaseObject databaseObject)
{
    public DatabaseObject DatabaseObject { get; } = databaseObject;

    public string Schema => DatabaseObject.Schema;

    public string Type => DatabaseObject.Type;

    public string Name => DatabaseObject.Name;

    public string TableName => DatabaseObject.TableName;

    public string? Sql => DatabaseObject.Sql;

    /// <summary>Display text shown in the tree, qualified with the schema when it isn't "main".</summary>
    public string DisplayName => Schema is "main" ? Name : $"{Schema}.{Name}";
}

/// <summary>A group node in the Structure tab's schema tree (Tables / Views / Indexes / Triggers).</summary>
public sealed partial class SchemaGroupViewModel(string groupName) : ObservableObject
{
    public string GroupName { get; } = groupName;

    public ObservableCollection<SchemaObjectViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    public string DisplayName => $"{GroupName} ({Items.Count})";

    public void NotifyItemsChanged() => OnPropertyChanged(nameof(DisplayName));
}
