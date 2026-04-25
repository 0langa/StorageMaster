using Microsoft.UI.Xaml.Controls;

namespace StorageMaster.UI.Infrastructure;

public interface INavigationService
{
    void Initialize(Frame frame);
    bool NavigateTo(Type pageType, object? parameter = null);
    bool CanGoBack { get; }
    void GoBack();
}
