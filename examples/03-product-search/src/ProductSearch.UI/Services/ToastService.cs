namespace ProductSearch.UI.Services;

/// <summary>Lightweight toast notifications for DaisyUI host.</summary>
public sealed class ToastService
{
    public event Action? OnChange;

    public IReadOnlyList<ToastMessage> Messages => _messages;

    private readonly List<ToastMessage> _messages = [];
    private int _nextId;

    public void Show(string message, ToastLevel level = ToastLevel.Info)
    {
        var toast = new ToastMessage(++_nextId, message, level);
        _messages.Add(toast);
        OnChange?.Invoke();
        _ = DismissAfterDelayAsync(toast.Id);
    }

    public void Dismiss(int id)
    {
        if (_messages.RemoveAll(m => m.Id == id) > 0)
            OnChange?.Invoke();
    }

    private async Task DismissAfterDelayAsync(int id)
    {
        try
        {
            await Task.Delay(5000).ConfigureAwait(false);
            Dismiss(id);
        }
        catch
        {
            // component may be disposed
        }
    }
}

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record ToastMessage(int Id, string Message, ToastLevel Level);
