using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Infrastructure;

public sealed class NavigationService : INavigationService
{
    private Frame? _frame;
    private Type? _currentPageType;
    private object? _currentParameter;

    public event EventHandler<NavigationChangedEventArgs>? Navigated;

    public void Initialize(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += OnFrameNavigated;
    }

    public bool CanGoBack => _frame?.CanGoBack ?? false;
    public Type? CurrentPageType => _currentPageType;
    public object? CurrentParameter => _currentParameter;

    public bool NavigateTo(Type pageType, object? parameter = null)
    {
        if (_frame is null) return false;

        var normalizedParameter = NormalizeParameter(parameter);

        // Avoid only true duplicate navigations. Same page with different route
        // data must still navigate so Results/Duplicates can reload the target.
        if (_frame.CurrentSourcePageType == pageType &&
            Equals(NormalizeParameter(_currentParameter), normalizedParameter))
        {
            return true;
        }

        return _frame.Navigate(pageType, parameter);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
            _frame.GoBack();
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        _currentPageType = e.SourcePageType;
        _currentParameter = NormalizeParameter(e.Parameter);
        Navigated?.Invoke(this, new NavigationChangedEventArgs(_currentPageType, _currentParameter));
    }

    private static object? NormalizeParameter(object? parameter) => parameter switch
    {
        string text when string.IsNullOrWhiteSpace(text) => null,
        _ => parameter,
    };
}
