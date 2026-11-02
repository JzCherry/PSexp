using PsExporter.Services;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    private PhotoshopDocumentEvents? _documentEvents;
    private long _lastDocumentEventAttemptVersion;
    public event EventHandler? DocumentChangeAvailable;
    public bool HasPendingDocumentChanges => _documentEvents is { } events && events.Version > _lastDocumentEventAttemptVersion;

    private void InitializeDocumentEvents()
    {
        _documentEvents = new PhotoshopDocumentEvents(_ps, _fileLogger);
        _documentEvents.Changed += OnDocumentEvent;
        _documentEvents.SetEnabled(AutoRefreshOnActivated);
    }

    private void OnDocumentEvent(object? sender, EventArgs args)
        => DocumentChangeAvailable?.Invoke(this, EventArgs.Empty);

    private async Task EnsureDocumentEventsAsync(Func<bool>? isForeground)
    {
        if (_documentEvents is null || !AutoRefreshOnActivated || !IsPsRunning || IsBusy
            || !string.IsNullOrEmpty(DocumentProbeError) || isForeground?.Invoke() == false) return;
        IsRefreshingStatus = true;
        try { await _documentEvents.EnsureSubscribedAsync(_documentProcessId); }
        finally { IsRefreshingStatus = false; RefreshCanExecute(); }
    }

    public Task StopDocumentEventsAsync()
    {
        if (_documentEvents is null) return Task.CompletedTask;
        _documentEvents.Changed -= OnDocumentEvent;
        return _documentEvents.StopAsync();
    }
}
