using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TimeLapseCam.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.Core;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenCvSharp;

// Actually, likely need manual conversion or a helper.
// OpenCvSharp.Extensions usually supports System.Drawing.Bitmap.
// For SoftwareBitmap (WinRT), we might need to access buffer directly.

namespace TimeLapseCam.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly CameraService _cameraService;
        private readonly AudioService _audioService;
        private readonly ObjectDetectionService _detectionService;
        private readonly EventLogService _eventLogService;

        [ObservableProperty]
        private bool _isRecording;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        private string _recordButtonText = "Start Recording";

        [ObservableProperty]
        private ObservableCollection<EventItem> _recentEvents = new();

        private VideoWriter? _videoWriter;
        private DateTime _recordingStartTime;
        private string _tempVideoPath = string.Empty;
        private string _tempAudioPath = string.Empty;
        private string _finalVideoPath = string.Empty;
        private int _frameWidth;
        private int _frameHeight;
        private DateTime _lastDetectionTime;

        public MainViewModel()
        {
            _cameraService = new CameraService();
            _audioService = new AudioService();
            _detectionService = new ObjectDetectionService();
            _eventLogService = new EventLogService();

            _cameraService.FrameArrived += OnFrameArrived;
            _audioService.AudioLevelChanged += OnAudioLevelChanged;
        }

        [ObservableProperty]
        private Microsoft.UI.Xaml.Media.ImageSource? _previewSource;

        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        public bool IsInitialized { get; private set; }

        public async Task InitializeAsync()
        {
            StatusMessage = "Initializing...";
            try
            {
                await _cameraService.InitializeAsync();
                await _cameraService.StartPreviewAsync();
                
                // Load Model
                string modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yolov8n.onnx");
                if (File.Exists(modelPath))
                {
                    await _detectionService.InitializeAsync(modelPath);
                    StatusMessage = "Ready (AI Loaded)";
                }
                else
                {
                    StatusMessage = "Ready (AI Model Missing - Detection Disabled)";
                }
                IsInitialized = true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        [RelayCommand]
        private void ToggleRecording()
        {
            if (IsRecording)
            {
                StopRecording();
            }
            else
            {
                StartRecording();
            }
        }

        private void StartRecording()
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string folder = Path.Combine(documents, "TimeLapseCam");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                _finalVideoPath = Path.Combine(folder, $"Recording_{timestamp}.mp4");
                _tempVideoPath = Path.Combine(Path.GetTempPath(), $"temp_vid_{timestamp}.mp4");
                _tempAudioPath = Path.Combine(Path.GetTempPath(), $"temp_audio_{timestamp}.wav");

                _eventLogService.Initialize(_finalVideoPath);

                // Start Audio
                _audioService.StartRecording(null, _tempAudioPath); // Uses default device

                // Video Writer initialized on first frame to get size
                _videoWriter = null; 

                _recordingStartTime = DateTime.Now;
                IsRecording = true;
                RecordButtonText = "Stop Recording";
                StatusMessage = "Recording...";
                _eventLogService.LogEvent("System", "Recording Started", TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                // Detailed parsing of the error
                string source = "Unknown";
                if (ex.StackTrace?.Contains("AudioService") == true) source = "Audio";
                if (ex.StackTrace?.Contains("VideoWriter") == true) source = "VideoWriter";
                
                StatusMessage = $"[v0.3] Start Failed ({source}): {ex.Message}";
                IsRecording = false;
                Debug.WriteLine($"Start Recording Error: {ex}");
            }
        }

        private async void StopRecording()
        {
            IsRecording = false;
            RecordButtonText = "Start Recording";
            StatusMessage = "Stopping...";

            try
            {
                _audioService.StopRecording();
                _videoWriter?.Dispose();
                _videoWriter = null;

                _eventLogService.LogEvent("System", "Recording Stopped", DateTime.Now - _recordingStartTime);

                StatusMessage = "Processing/Merging...";
                
                // Check if files exist
                if (File.Exists(_tempVideoPath) && File.Exists(_tempAudioPath))
                {
                     await MergeMediaAsync(_tempVideoPath, _tempAudioPath, _finalVideoPath);
                     StatusMessage = $"Saved: {Path.GetFileName(_finalVideoPath)}";
                }
                else
                {
                    StatusMessage = "Error: Temp files missing.";
                }

                // Cleanup
                // File.Delete(_tempVideoPath); // Keep for debug if needed
                // File.Delete(_tempAudioPath);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Stop Failed: {ex.Message}";
            }
        }

        private async Task MergeMediaAsync(string videoPath, string audioPath, string destination)
        {
            try 
            {
                // Unpackaged app might struggle with MediaComposition if not careful with codecs
                // Video: Created by OpenCV (avc1/mp4)
                // Audio: Created by NAudio (PCM/wav)
                
                var composition = new MediaComposition();
                var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
                var audioFile = await StorageFile.GetFileFromPathAsync(audioPath);
                
                var videoClip = await MediaClip.CreateFromFileAsync(videoFile);
                var audioTrack = await BackgroundAudioTrack.CreateFromFileAsync(audioFile);
                
                composition.Clips.Add(videoClip);
                composition.BackgroundAudioTracks.Add(audioTrack);
                
                var destFile = await StorageFile.GetFileFromPathAsync(Path.GetDirectoryName(destination));
                // Creates file in the folder (need generic access or StorageFolder)
                // actually CreateFromFileAsync expects existing file for read? No.
                
                // Simplify: use StorageFolder to create file
                var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination));
                var resultFile = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.ReplaceExisting);
                
                await composition.RenderToFileAsync(resultFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Merge Error: {ex.Message}");
                // Fallback: Just move video file if merge fails
                File.Copy(videoPath, destination, true);
                StatusMessage = "Merge Failed. Video saved without audio.";
            }
        }

        private void OnFrameArrived(object? sender, SoftwareBitmap bitmap)
        {
            if (bitmap == null) return;

            // Update Frame Size if needed
            if (_frameWidth == 0)
            {
                _frameWidth = bitmap.PixelWidth;
                _frameHeight = bitmap.PixelHeight;
            }

            // 1. Record Video Frame (1 FPS Logic)
            if (IsRecording)
            {
                // Simple logic: Record 1 frame every second
                // Check elapsed time since LAST recorded frame
                // Note: OnFrameArrived fires at ~30fps
                
                // Better: Check if (Now - LastWriteTime) > 1 second
                // But we want "Time Lapse".
                // If we want 1 FPS output that MATCHES real-time duration:
                // We write 1 frame every second.
                // The VideoWriter must be configured at 1 FPS.
                
                InitializeWriterIfNeeded();
                
                if ((DateTime.Now - _lastFrameWriteTime).TotalSeconds >= 1.0)
                {
                    WriteFrame(bitmap);
                    _lastFrameWriteTime = DateTime.Now;
                }
            }

            // Update UI Preview
            _dispatcherQueue.TryEnqueue(async () => 
            {
                 // Create SoftwareBitmapSource
                 if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                 {
                     // Convert if needed (heavy on UI thread, but necessary for XAML)
                     var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                     var source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
                     await source.SetBitmapAsync(converted);
                     PreviewSource = source;
                     // converted.Dispose(); // SoftwareBitmapSource takes ownership? No, we should rely on GC or Dispose if we are sure.
                 }
                 else
                 {
                     var source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
                     await source.SetBitmapAsync(bitmap);
                     PreviewSource = source;
                 }
            });

            // 2. Object Detection (Every 500ms)
            if (_detectionService.IsInitialized && (DateTime.Now - _lastDetectionTime).TotalMilliseconds > 500)
            {
                DetectObjects(bitmap);
                _lastDetectionTime = DateTime.Now;
            }
        }

        private DateTime _lastFrameWriteTime;

        private void InitializeWriterIfNeeded()
        {
            if (_videoWriter == null)
            {
                // 1 FPS, Color
                // Codec: Use "avc1" (H.264). Requires OpenH264 or Cisco DLL or system codec.
                // Or "mp4v" (MPEG-4) which is safer.
                _videoWriter = new VideoWriter(_tempVideoPath, FourCC.H264, 1, new OpenCvSharp.Size(_frameWidth, _frameHeight), true);
            }
        }

        private unsafe void WriteFrame(SoftwareBitmap bitmap)
        {
            if (_videoWriter == null || !_videoWriter.IsOpened()) return;

            // Convert SoftwareBitmap to Mat
            using var mat = SoftwareBitmapToMat(bitmap);
            if (mat != null)
            {
                _videoWriter.Write(mat);
            }
        }

        private unsafe void DetectObjects(SoftwareBitmap bitmap)
        {
            // Convert to Mat
            using var mat = SoftwareBitmapToMat(bitmap);
            if (mat == null) return;

            var results = _detectionService.Detect(mat);
            foreach (var result in results)
            {
                // Filter?
                if (result.Label == "person" || result.Label == "cat" || result.Label == "dog")
                {
                    // Log
                    var time = IsRecording ? (DateTime.Now - _recordingStartTime) : TimeSpan.Zero;
                    var msg = $"Detected {result.Label} ({result.Confidence:P0})";
                    
                    // Update UI
                    // Must be on UI thread
                    // For now, just log to service
                    if (IsRecording)
                    {
                        _eventLogService.LogEvent("Object", msg, time);
                    }
                    
                    // TODO: Draw bounding box on Preview? 
                    // Complex with overlay. We can update a "Detections" observable property instead.
                }
            }
        }
        
        private unsafe Mat? SoftwareBitmapToMat(SoftwareBitmap? bitmap)
        {
            if (bitmap == null) return null;
            
            // Ensure BGRA8
            SoftwareBitmap safeBitmap = bitmap;
            bool disposeSafeBitmap = false;
            
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                safeBitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                disposeSafeBitmap = true;
            }

            try
            {
                using var buffer = safeBitmap.LockBuffer(BitmapBufferAccessMode.Read);
                using var reference = buffer.CreateReference();
                
                // Get pointer to pixel data
                // This requires the IMemoryBufferByteAccess interface which is not directly exposed in C# WinRT without casting.
                // However, we can use CopyToBuffer to a standard byte array as a safe fallback if we want to avoid complex COM interop in a single file.
                // Given the constraints, let's use the CopyToBuffer method to a managed array, then pin it for OpenCV.
                // It's slightly slower but safer and requires less boilerplate.
                
                int w = safeBitmap.PixelWidth;
                int h = safeBitmap.PixelHeight;
                
                // Mat expects a contiguous block. 
                // SoftwareBitmap might have stride (padding).
                // buffer.GetPlaneDescription(0) gives stride.
                var plane = buffer.GetPlaneDescription(0);
                int stride = plane.Stride;
                
                // If stride == width * 4, we can copy the whole block.
                // If not, we have to copy row by row.
                
                // Create Mat
                var mat = new Mat(h, w, MatType.CV_8UC4);
                
                // We need to access the raw pointer of the Mat to copy INTO it.
                // Or we can create a Mat from the byte array.
                
                // Let's use the COM interface approach if possible, but standard .NET 8 WinRT projection might handle it?
                // Actually, `buffer` object in WinUI 3 is `BitmapBuffer`.
                // Accessing underlying pointer is tricky without `IMemoryBufferByteAccess`.
                
                // FALLBACK: Copy detailed
                // 1. Get bytes from SoftwareBitmap
                // Since we can't easily get the pointer in pure managed C# without Unsafe/CSWinRT helpers:
                // We will use a temporary byte array.
                
                byte[] data = new byte[stride * h];
                safeBitmap.CopyToBuffer(data.AsBuffer());
                
                // 2. Copy to Mat
                // Mat has `SetArray` but that's for 1D.
                // We can use `Marshal.Copy` to move data from `data` array to `mat.Data`.
                
                System.Runtime.InteropServices.Marshal.Copy(data, 0, mat.Data, data.Length);
                
                return mat;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Conversion Failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (disposeSafeBitmap) safeBitmap.Dispose();
            }
        }

        private void OnAudioLevelChanged(object? sender, float level)
        {
            // Detect "Clap" or loud noise
            if (level > 0.8 && IsRecording)
            {
                var time = DateTime.Now - _recordingStartTime;
                _eventLogService.LogEvent("Sound", "Loud Noise Detected", time);
            }
        }
    }
}
