using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TimeLapseCam.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.Diagnostics;
using OpenCvSharp;

namespace TimeLapseCam.ViewModels
{
    public partial class ReviewViewModel : ObservableObject
    {
        private readonly EventLogService _eventLogService;
        private readonly ObjectDetectionService _detectionService;
        private readonly TranscriptionService _transcriptionService;
        private CancellationTokenSource? _transcriptionCts;

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

        // Transcription properties
        [ObservableProperty]
        private ObservableCollection<string> _whisperModels = new(TranscriptionService.AvailableModels);

        [ObservableProperty]
        private string _selectedWhisperModel = SettingsHelper.Get<string>("WhisperModel", "base") ?? "base";

        [ObservableProperty]
        private string _transcriptionStatus = "";

        [ObservableProperty]
        private bool _isTranscribing;

        [ObservableProperty]
        private int _transcriptionProgress;

        [ObservableProperty]
        private bool _showDebugLog = SettingsHelper.Get<bool>("ShowTranscriptDebugLog", false);

        [ObservableProperty]
        private string _transcriptionDebugLog = "";

        [ObservableProperty]
        private string _transcriptText = "";

        [ObservableProperty]
        private ObservableCollection<TranscriptInfo> _transcripts = new();

        [ObservableProperty]
        private TranscriptInfo? _selectedTranscript;

        [ObservableProperty]
        private ObservableCollection<TranscriptSegment> _transcriptSegments = new();

        [ObservableProperty]
        private TranscriptSegment? _selectedSegment;

        private MediaPlayer? _mediaPlayer;

        // Store XamlRoot for file picker
        private Microsoft.UI.Xaml.XamlRoot? _xamlRoot;

        public ReviewViewModel()
        {
            _eventLogService = new EventLogService();
            _detectionService = new ObjectDetectionService();
            _transcriptionService = new TranscriptionService();
            _transcriptionService.ProgressChanged += (_, msg) =>
            {
                TranscriptionStatus = msg;
            };
            LoadRecordings();
        }

        public void SetMediaPlayer(MediaPlayer player)
        {
            _mediaPlayer = player;
        }

        public void SetXamlRoot(Microsoft.UI.Xaml.XamlRoot root)
        {
            _xamlRoot = root;
        }

        private void LoadRecordings()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string folder = Path.Combine(documents, "TimeLapseCam");
            if (Directory.Exists(folder))
            {
                var videoExtensions = new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm" };
                var files = Directory.GetFiles(folder)
                                     .Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                     .OrderByDescending(f => File.GetCreationTime(f))
                                     .Select(f =>
                                     {
                                         var rec = new RecordingFile
                                         {
                                             FilePath = f,
                                             Name = Path.GetFileName(f),
                                             CreationTime = File.GetCreationTime(f)
                                         };
                                         rec.HasTranscript = TranscriptionService.FindTranscripts(f).Count > 0;
                                         return rec;
                                     });
                
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
                    var file = await StorageFile.GetFileFromPathAsync(value.FilePath);
                    PlaybackSource = MediaSource.CreateFromStorageFile(file);
                }
                catch (Exception ex)
                {
                     Debug.WriteLine($"[ReviewVM] Error loading video: {ex.Message}");
                }

                // Load existing events
                string jsonPath = Path.ChangeExtension(value.FilePath, ".json");
                var events = _eventLogService.LoadLog(jsonPath);
                Events = new ObservableCollection<EventItem>(events);
                AnalysisStatus = events.Count > 0 ? $"{events.Count} events" : "Click Detect to analyze";

                // Load transcripts
                var transcripts = TranscriptionService.FindTranscripts(value.FilePath);
                Transcripts = new ObservableCollection<TranscriptInfo>(transcripts);
                TranscriptText = "";
                SelectedTranscript = transcripts.FirstOrDefault();
            }
        }

        partial void OnSelectedTranscriptChanged(TranscriptInfo? value)
        {
            if (value != null)
            {
                TranscriptText = TranscriptionService.ReadTranscript(value.FilePath);
                var segments = TranscriptionService.ParseSegments(TranscriptText);
                TranscriptSegments = new ObservableCollection<TranscriptSegment>(segments);
            }
            else
            {
                TranscriptText = "";
                TranscriptSegments.Clear();
            }
        }

        partial void OnSelectedSegmentChanged(TranscriptSegment? value)
        {
            if (value != null && _mediaPlayer != null)
            {
                _mediaPlayer.PlaybackSession.Position = value.Start;
                _mediaPlayer.Play();
            }
        }

        partial void OnSelectedWhisperModelChanged(string value)
        {
            SettingsHelper.Set("WhisperModel", value);
        }

        [RelayCommand]
        private async Task Analyze()
        {
            if (SelectedRecording == null)
            {
                AnalysisStatus = "Select a recording first";
                return;
            }
            
            string jsonPath = Path.ChangeExtension(SelectedRecording.FilePath, ".json");
            Events.Clear();
            await AnalyzeVideoAsync(SelectedRecording.FilePath, jsonPath);
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
                        var msg = $"Detected {result.Label} ({result.Confidence:P0})";
                        _eventLogService.LogEvent("Object", msg, timestamp);
                    }

                    frameIndex++;
                }
            });

            var analyzedEvents = _eventLogService.LoadLog(jsonPath);
            Events = new ObservableCollection<EventItem>(analyzedEvents);
            AnalysisStatus = analyzedEvents.Count > 0 ? $"Found {analyzedEvents.Count} events" : "No events detected";
        }

        [RelayCommand]
        private async Task TranscribeSelected()
        {
            var selected = Recordings.Where(r => r.IsSelected).ToList();

            if (selected.Count == 0)
            {
                if (SelectedRecording != null)
                    selected.Add(SelectedRecording);
                else
                {
                    TranscriptionStatus = "Select recordings to transcribe";
                    return;
                }
            }

            string model = SelectedWhisperModel ?? "base";
            _transcriptionCts = new CancellationTokenSource();
            var ct = _transcriptionCts.Token;

            IsTranscribing = true;
            TranscriptionProgress = 0;
            TranscriptionStatus = $"Transcribing {selected.Count} file(s) with '{model}'...";

            // Forward progress from the service to the UI + debug log
            TranscriptionDebugLog = "";
            ShowDebugLog = SettingsHelper.Get<bool>("ShowTranscriptDebugLog", false);
            void OnProgress(object? s, string msg)
            {
                TranscriptionStatus = msg;
                TranscriptionDebugLog += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            }
            _transcriptionService.ProgressChanged += OnProgress;

            int success = 0;
            int total = selected.Count;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var rec = selected[i];
                    TranscriptionStatus = $"[{i + 1}/{total}] {rec.Name}";
                    TranscriptionProgress = (int)((double)i / total * 100);

                    var result = await _transcriptionService.TranscribeAsync(rec.FilePath, model, ct);
                    if (result != null)
                    {
                        success++;
                        rec.HasTranscript = true;
                    }
                }
            }
            finally
            {
                _transcriptionService.ProgressChanged -= OnProgress;
            }

            TranscriptionProgress = 100;
            IsTranscribing = false;
            TranscriptionStatus = $"Done: {success}/{total} transcribed with '{model}'";

            if (SelectedRecording != null)
            {
                var transcripts = TranscriptionService.FindTranscripts(SelectedRecording.FilePath);
                Transcripts = new ObservableCollection<TranscriptInfo>(transcripts);
                SelectedTranscript = transcripts.FirstOrDefault(t => t.ModelName == model) ?? transcripts.FirstOrDefault();
            }
        }

        [RelayCommand]
        private async Task ImportVideo()
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".mp4");
                picker.FileTypeFilter.Add(".avi");
                picker.FileTypeFilter.Add(".mkv");
                picker.FileTypeFilter.Add(".mov");
                picker.FileTypeFilter.Add(".wmv");
                picker.FileTypeFilter.Add(".webm");
                picker.FileTypeFilter.Add(".wav");
                picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;

                // Initialize picker with window handle (required for WinUI 3)
                var window = App.MainWindow;
                if (window == null)
                {
                    TranscriptionStatus = "Cannot open file picker — no window available.";
                    return;
                }

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0) return;

                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string folder = Path.Combine(documents, "TimeLapseCam");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                int imported = 0;
                foreach (var file in files)
                {
                    string destPath = Path.Combine(folder, file.Name);

                    // Avoid overwriting
                    if (File.Exists(destPath))
                    {
                        string nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
                        string ext = Path.GetExtension(file.Name);
                        destPath = Path.Combine(folder, $"{nameNoExt}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    }

                    File.Copy(file.Path, destPath, overwrite: false);
                    imported++;
                }

                TranscriptionStatus = $"✅ Imported {imported} file(s)";
                LoadRecordings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Import] Error: {ex}");
                TranscriptionStatus = $"Import failed: {ex.Message}";
            }
        }

        [RelayCommand]
        private void Refresh()
        {
            LoadRecordings();
            TranscriptionStatus = "Refreshed";
        }

        [RelayCommand]
        private async Task DownloadModel()
        {
            string model = SelectedWhisperModel ?? "base";
            TranscriptionStatus = $"Downloading model '{model}'...";
            bool success = await _transcriptionService.ForceDownloadModelAsync(model);
            if (success)
                TranscriptionStatus = $"✅ Model '{model}' downloaded successfully.";
            else
                TranscriptionStatus = $"❌ Failed to download model '{model}'.";
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

    public class RecordingFile : ObservableObject
    {
        public string FilePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime CreationTime { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _hasTranscript;
        public bool HasTranscript
        {
            get => _hasTranscript;
            set => SetProperty(ref _hasTranscript, value);
        }

        public string DisplayName => HasTranscript ? $"📝 {Name}" : Name;
    }
}
