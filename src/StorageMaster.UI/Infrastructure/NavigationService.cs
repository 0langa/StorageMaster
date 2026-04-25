using Microsoft.UI.Xaml.Controls;

namespace StorageMaster.UI.Infrastructure;

public sealed class NavigationService : INavigationService
{
    private Frame? _frame;

    public void Initialize(Frame frame) => _frame = frame;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public bool NavigateTo(Type pageType, object? parameter = null)
    {
        if (_frame is null) return false;

        // Avoid double-navigation to the same page type.
        if (_frame.CurrentSourcePageType == pageType) return true;

        return _frame.Navigate(pageType, parameter);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
            _frame.GoBack();
    }
}
