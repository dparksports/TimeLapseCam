using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TimeLapseCam.ViewModels;
using Windows.Media.Playback;

namespace TimeLapseCam.Views
{
    public sealed partial class ReviewPage : Page
    {
        public ReviewViewModel ViewModel { get; }

        public ReviewPage()
        {
            this.InitializeComponent();
            ViewModel = new ReviewViewModel();
            this.Loaded += ReviewPage_Loaded;
        }

        private void ReviewPage_Loaded(object sender, RoutedEventArgs e)
        {
             if (Player.MediaPlayer == null)
             {
                 Player.SetMediaPlayer(new MediaPlayer());
             }
             
             if (Player.MediaPlayer != null)
             {
                 ViewModel.SetMediaPlayer(Player.MediaPlayer);
             }
        }
    }
}
