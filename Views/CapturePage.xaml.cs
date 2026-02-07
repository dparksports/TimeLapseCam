using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TimeLapseCam.ViewModels;

namespace TimeLapseCam.Views
{
    public sealed partial class CapturePage : Page
    {
        public MainViewModel ViewModel { get; }

        public CapturePage()
        {
            this.InitializeComponent();
            ViewModel = new MainViewModel(); // Singleton or DI preferred in real app, but new instance for now is fine if scoped to page
            
            // Start initialization
            // We should cleanup when navigating away?
            this.Loaded += CapturePage_Loaded;
            this.Unloaded += CapturePage_Unloaded;
        }

        private void CapturePage_Loaded(object sender, RoutedEventArgs e)
        {
             _ = ViewModel.InitializeAsync(CameraPreview);
        }

        private void CapturePage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Stop preview?
            // ViewModel.Cleanup();
        }
    }
}
