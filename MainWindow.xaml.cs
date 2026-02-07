using Microsoft.UI.Xaml;
using TimeLapseCam.ViewModels;

namespace TimeLapseCam
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            this.InitializeComponent();
            ViewModel = new MainViewModel();
            this.Title = "TimeLapse Webcam Recorder";
            
            // Handle Closed event to cleanup
            this.Closed += MainWindow_Closed;
            
            // Start initialization
            _ = ViewModel.InitializeAsync(CameraPreview);
        }

        private void ReviewButton_Click(object sender, RoutedEventArgs e)
        {
            var reviewWin = new ReviewWindow();
            reviewWin.Activate();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // Dispose services? 
            // Better to handle in ViewModel Dispose if needed.
            // But MainViewModel is not IDisposable currently.
            // We can rely on process termination or add Dispose.
        }
    }
}
