using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TimeLapseCam.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using System.Diagnostics;
using OpenCvSharp;

namespace TimeLapseCam.ViewModels
{
    public partial class ReviewViewModel : ObservableObject
    {
        private readonly EventLogService _eventLogService;
        private readonly ObjectDetectionService _detectionService;

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

        [ObservableProperty]
        private string _analysisStatus = "";

        private MediaPlayer? _mediaPlayer;

        public ReviewViewModel()
        {
            _eventLogService = new EventLogService();
            _detectionService = new ObjectDetectionService();
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
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(value.FilePath);
                    PlaybackSource = MediaSource.CreateFromStorageFile(file);
                }
                catch (Exception ex)
                {
                     Debug.WriteLine($"[ReviewVM] Error loading video: {ex.Message}");
                }

                // Load existing events or run detection
                string jsonPath = Path.ChangeExtension(value.FilePath, ".json");
                var events = _eventLogService.LoadLog(jsonPath);
                
                if (events.Count > 0)
                {
                    Events = new ObservableCollection<EventItem>(events);
                    AnalysisStatus = $"{events.Count} events";
                }
                else
                {
                    Events.Clear();
                    await AnalyzeVideoAsync(value.FilePath, jsonPath);
                }
            }
        }

        private async Task AnalyzeVideoAsync(string videoPath, string jsonPath)
        {
            AnalysisStatus = "Loading AI model...";
            
            if (!_detectionService.IsInitialized)
            {
                string modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yolov8n.onnx");
                if (!File.Exists(modelPath))
                {
                    AnalysisStatus = "AI model not found";
                    return;
                }
                await _detectionService.InitializeAsync(modelPath);
            }

            AnalysisStatus = "Analyzing video...";
            _eventLogService.Initialize(videoPath);

            await Task.Run(() =>
            {
                using var capture = new VideoCapture(videoPath);
                if (!capture.IsOpened()) return;

                double fps = capture.Fps;
                if (fps <= 0) fps = 1;

                int frameIndex = 0;
                using var frame = new Mat();
                
                while (capture.Read(frame))
                {
                    if (frame.Empty()) break;

                    var timestamp = TimeSpan.FromSeconds(frameIndex / fps);
                    var results = _detectionService.Detect(frame);
                    
                    foreach (var result in results)
                    {
                        if (result.Label == "person" || result.Label == "cat" || result.Label == "dog")
                        {
                            var msg = $"Detected {result.Label} ({result.Confidence:P0})";
                            _eventLogService.LogEvent("Object", msg, timestamp);
                        }
                    }

                    frameIndex++;
                }
            });

            var analyzedEvents = _eventLogService.LoadLog(jsonPath);
            Events = new ObservableCollection<EventItem>(analyzedEvents);
            AnalysisStatus = analyzedEvents.Count > 0 ? $"Found {analyzedEvents.Count} events" : "No events detected";
        }

        partial void OnSelectedEventChanged(EventItem? value)
        {
            if (value != null && _mediaPlayer != null)
            {
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
