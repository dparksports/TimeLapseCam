using Microsoft.UI.Xaml;
using TimeLapseCam.ViewModels;
using Windows.Media.Playback;

namespace TimeLapseCam
{
    public sealed partial class ReviewWindow : Window
    {
        public ReviewViewModel ViewModel { get; }

        public ReviewWindow()
        {
            this.InitializeComponent();
            ViewModel = new ReviewViewModel();
            this.Title = "Review Recordings";
            
            // Player element needs a MediaPlayer
            // By default MediaPlayerElement creates one if AutoPlay=True?
            // Let's ensure one exists
            
            this.Activated += ReviewWindow_Activated;
        }

        private void ReviewWindow_Activated(object sender, WindowActivatedEventArgs args)
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
