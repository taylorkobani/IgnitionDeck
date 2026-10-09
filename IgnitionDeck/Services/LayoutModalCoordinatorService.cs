using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IgnitionDeck.Services;

public sealed class LayoutModalCoordinatorService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public XamlRoot? XamlRoot { get; set; }
    public bool IsShowing { get; private set; }

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot = XamlRoot ?? throw new InvalidOperationException("MainLayout is not loaded.");
            IsShowing = true;
            return await dialog.ShowAsync();
        }
        finally { IsShowing = false; _gate.Release(); }
    }
}
