using CommunityToolkit.Mvvm.ComponentModel;

namespace SqliteBrowser.App.ViewModels;

/// <summary>
/// Shared view-model plumbing: a busy flag (drives spinners / disables actions during async work),
/// an error banner that stays visible until the next action clears it, and a helper that runs an
/// async action while maintaining both — so failures are always surfaced to the user and never
/// silently swallowed.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    private int _busyDepth;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Whether <see cref="ErrorMessage"/> currently holds a message that should be shown to the user.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>
    /// Invoked after any operation that may have changed the attached <c>DatabaseSession</c>'s dirty
    /// state (an edit, DDL execution, commit, revert, ...) so the owning <see cref="MainWindowViewModel"/>
    /// can refresh its own <c>IsDirty</c>/status display without every tab needing a back-reference to it.
    /// </summary>
    public Action? OnMutated { get; set; }

    protected void NotifyMutated() => OnMutated?.Invoke();

    /// <summary>
    /// Runs <paramref name="action"/> guarded by <see cref="IsBusy"/>. Any exception (other than a
    /// cooperative cancellation) is captured in <see cref="ErrorMessage"/> rather than thrown back
    /// into the UI thread's synchronization context or swallowed.
    /// </summary>
    /// <remarks>
    /// Calls can legitimately nest: e.g. loading the table list may synchronously select the first
    /// table, whose change handler kicks off loading that table's page while the outer call is still
    /// in flight. A depth counter (rather than a single boolean early-return) lets every nested call
    /// still run its own body, while <see cref="IsBusy"/> stays <c>true</c> for the whole outer span.
    /// </remarks>
    protected async Task RunGuardedAsync(Func<Task> action)
    {
        _busyDepth++;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            _busyDepth--;
            if (_busyDepth == 0)
            {
                IsBusy = false;
            }
        }
    }
}
