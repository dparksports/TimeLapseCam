using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using TimeLapseCam.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using System.Diagnostics;

namespace TimeLapseCam.ViewModels
{
    public partial class ReviewViewModel : ObservableObject
    {
        private readonly EventLogService _eventLogService;

        [ObservableProperty]
        private ObservableCollection<RecordingFile> _recordings = new();

        [ObservableProperty]
        private RecordingFile? _selectedRecording;

        [ObservableProperty]
        private ObservableCollection<EventItem> _events = new();

        [ObservableProperty]
        private EventItem? _selectedEvent;
        
        [ObservableProperty] 
        private IMediaPlaybackSource? _playbackSource;

        private MediaPlayer? _mediaPlayer;

        public ReviewViewModel()
        {
            _eventLogService = new EventLogService();
            LoadRecordings();
        }

        public void SetMediaPlayer(MediaPlayer player)
        {
            _mediaPlayer = player;
        }

        private void LoadRecordings()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string folder = Path.Combine(documents, "TimeLapseCam");
            if (Directory.Exists(folder))
            {
                var files = Directory.GetFiles(folder, "*.mp4")
                                     .OrderByDescending(f => File.GetCreationTime(f))
                                     .Select(f => new RecordingFile { FilePath = f, Name = Path.GetFileName(f), CreationTime = File.GetCreationTime(f) });
                
                Recordings = new ObservableCollection<RecordingFile>(files);
            }
        }

        async partial void OnSelectedRecordingChanged(RecordingFile? value)
        {
            if (value != null)
            {
                // Load Video
                try 
                {
                    Debug.WriteLine($"[ReviewVM] Loading video: {value.FilePath}");
                    // Use StorageFile to avoid Uri parsing issues with local paths
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(value.FilePath);
                    PlaybackSource = MediaSource.CreateFromStorageFile(file);
                }
                catch (Exception ex)
                {
                     Debug.WriteLine($"[ReviewVM] Error loading video: {ex.Message}");
                }

                // Load Events
                string jsonPath = Path.ChangeExtension(value.FilePath, ".json");
                var events = _eventLogService.LoadLog(jsonPath);
                Events = new ObservableCollection<EventItem>(events);
            }
        }

        partial void OnSelectedEventChanged(EventItem? value)
        {
            if (value != null && _mediaPlayer != null)
            {
                // Jump to timestamp - 5 seconds context
                var seekTime = value.Timestamp - TimeSpan.FromSeconds(5);
                if (seekTime < TimeSpan.Zero) seekTime = TimeSpan.Zero;
                
                _mediaPlayer.PlaybackSession.Position = seekTime;
                _mediaPlayer.Play();
            }
        }
    }

    public class RecordingFile
    {
        public string FilePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime CreationTime { get; set; }
    }
}
