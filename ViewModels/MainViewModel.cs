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
using Windows.Devices.Enumeration;

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
        private SecurityAuditLog? _auditLog;

        [ObservableProperty]
        private bool _isRecording;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        private string _recordButtonText = "Start Recording";

        [ObservableProperty]
        private ObservableCollection<EventItem> _recentEvents = new();

        [ObservableProperty]
        private string _googleDriveStatus = "Checking...";

        private VideoWriter? _videoWriter;
        private DateTime _recordingStartTime;
        private string _tempVideoPath = string.Empty;
        private string _tempAudioPath = string.Empty;
        private string _finalVideoPath = string.Empty;
        private int _frameWidth;
        private int _frameHeight;

        // Smart upload state
        private int _personFrameCounter = 0;
        private const int PersonUploadFrameLimit = 100;
        private DateTime _lastHourlySnapshot = DateTime.MinValue;
        private DateTime _lastDetectionTime = DateTime.MinValue;
        private bool _modelLoaded = false;


        public MainViewModel()
        {
            _cameraService = new CameraService();
            _audioService = new AudioService();
            _detectionService = new ObjectDetectionService();
            _eventLogService = new EventLogService();

            _cameraService.FrameArrived += OnFrameArrived;
            _audioService.AudioLevelChanged += OnAudioLevelChanged;

            // Check Google Drive status and initialize audit log
            CheckGoogleDriveStatus();
            InitializeAuditLog();
        }

        private void InitializeAuditLog()
        {
            try
            {
                string localDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string? drivePath = FindGoogleDriveFolder();
                _auditLog = new SecurityAuditLog(localDocs, drivePath);
                _auditLog.Log("STARTUP", $"Application started, Google Drive: {(drivePath != null ? drivePath : "not detected")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AuditLog] Init error: {ex.Message}");
            }
        }

        private void CheckGoogleDriveStatus()
        {
            string? drivePath = FindGoogleDriveFolder();
            if (drivePath != null)
            {
                GoogleDriveStatus = $"☁️ Google Drive: ✓ ({drivePath})";
            }
            else
            {
                GoogleDriveStatus = "⚠️ Google Drive: Not detected — Install Google Drive Desktop for cloud backup";
            }
        }

        [ObservableProperty]
        private ObservableCollection<DeviceInformation> _cameras = new();

        [ObservableProperty]
        private DeviceInformation? _selectedCamera;

        async partial void OnSelectedCameraChanged(DeviceInformation? value)
        {
            if (value != null && IsInitialized && value.Id != _cameraService.CurrentCameraId)
            {
                await InitializeCameraAsync(value);
            }
        }

        [ObservableProperty]
        private Microsoft.UI.Xaml.Media.ImageSource? _previewSource;

        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        public bool IsInitialized { get; private set; }

        public async Task InitializeAsync()
        {
            StatusMessage = "Loading cameras...";
            try
            {
                var devices = await _cameraService.GetAvailableCamerasAsync();
                Cameras.Clear();
                foreach (var device in devices) Cameras.Add(device);

                var defaultCamera = devices.FirstOrDefault();
                SelectedCamera = defaultCamera;

                StatusMessage = "Starting camera...";
                await InitializeCameraAsync(defaultCamera);
                IsInitialized = true;
                StatusMessage = "Ready";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }


        private async Task InitializeCameraAsync(DeviceInformation? camera)
        {
            StatusMessage = "Switching Camera...";
            try
            {
                await _cameraService.InitializeAsync(camera);
                await _cameraService.StartPreviewAsync();
                StatusMessage = "Ready";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Camera Error: {ex.Message}";
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
                _personFrameCounter = 0;
                _lastHourlySnapshot = DateTime.Now; // Start hourly timer from now
                IsRecording = true;
                RecordButtonText = "Stop Recording";
                StatusMessage = "Recording...";
                _eventLogService.LogEvent("System", "Recording Started", TimeSpan.Zero);
                _auditLog?.Log("RECORDING_START", $"Recording started: {_finalVideoPath}");

                // Load detection model in background (non-blocking)
                _ = EnsureDetectionModelAsync();
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
                _auditLog?.Log("RECORDING_STOP", $"Recording stopped after {(DateTime.Now - _recordingStartTime).TotalMinutes:F1} minutes");

                StatusMessage = "Processing/Merging...";
                
                // Check if files exist
                if (File.Exists(_tempVideoPath) && File.Exists(_tempAudioPath))
                {
                     await MergeMediaAsync(_tempVideoPath, _tempAudioPath, _finalVideoPath);
                     StatusMessage = $"Saved: {Path.GetFileName(_finalVideoPath)}";
                     
                     // Auto-backup to Google Drive if available
                     _ = BackupToGoogleDriveAsync(_finalVideoPath);
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

        private async Task BackupToGoogleDriveAsync(string localFilePath)
        {
            try
            {
                string? drivePath = FindGoogleDriveFolder();
                if (drivePath == null) return;

                string backupFolder = Path.Combine(drivePath, "TimeLapseCam");
                if (!Directory.Exists(backupFolder)) Directory.CreateDirectory(backupFolder);

                string destPath = Path.Combine(backupFolder, Path.GetFileName(localFilePath));
                
                await Task.Run(() => File.Copy(localFilePath, destPath, true));
                
                // Also copy the event log if it exists
                string jsonPath = Path.ChangeExtension(localFilePath, ".json");
                if (File.Exists(jsonPath))
                {
                    string destJson = Path.Combine(backupFolder, Path.GetFileName(jsonPath));
                    File.Copy(jsonPath, destJson, true);
                }

                Debug.WriteLine($"[GoogleDrive] Backed up to: {destPath}");
                StatusMessage += " | Cloud backup ✓";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GoogleDrive] Backup failed: {ex.Message}");
            }
        }

        private string? FindGoogleDriveFolder()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            // Check email-specific folders FIRST — these are the real synced folders
            // e.g. "My Drive (user@gmail.com)", "Google Drive (user@gmail.com)"
            try
            {
                foreach (var dir in Directory.GetDirectories(userProfile, "My Drive (*"))
                {
                    Debug.WriteLine($"[GoogleDrive] Found (email): {dir}");
                    return dir;
                }
                foreach (var dir in Directory.GetDirectories(userProfile, "Google Drive (*"))
                {
                    Debug.WriteLine($"[GoogleDrive] Found (email): {dir}");
                    return dir;
                }
            }
            catch { }

            // Fallback: check plain folder names
            string[] candidates = new[]
            {
                Path.Combine(userProfile, "Google Drive"),
                Path.Combine(userProfile, "My Drive"),
                Path.Combine(userProfile, "GoogleDrive"),
            };

            foreach (var path in candidates)
            {
                if (Directory.Exists(path))
                {
                    Debug.WriteLine($"[GoogleDrive] Found: {path}");
                    return path;
                }
            }

            // Check for mounted drive letter (G:\ etc) via registry
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Google\DriveFS\Share");
                if (key != null)
                {
                    var mountPoint = key.GetValue("MountPoint") as string;
                    if (!string.IsNullOrEmpty(mountPoint) && Directory.Exists(mountPoint))
                        return Path.Combine(mountPoint, "My Drive");
                }
            }
            catch { }

            // Check all drive letters for Google Drive
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Network)
                {
                    string gDrive = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                    if (Directory.Exists(gDrive)) return gDrive;
                }
            }

            return null;
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
                Debug.WriteLine($"[Merge] Composition rendered to {resultFile.Path}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Merge Error: {ex}");
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

                    // Smart detection + upload (runs at 1fps alongside frame writes)
                    _ = Task.Run(() => SmartDetectAndUpload(bitmap));
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


        }

        private DateTime _lastFrameWriteTime;

        private async Task EnsureDetectionModelAsync()
        {
            if (_modelLoaded) return;
            string modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yolov8n.onnx");
            if (File.Exists(modelPath))
            {
                await _detectionService.InitializeAsync(modelPath);
                _modelLoaded = true;
            }
        }

        private unsafe void SmartDetectAndUpload(SoftwareBitmap bitmap)
        {
            try
            {
                if (!_modelLoaded || !IsRecording) return;

                using var mat = SoftwareBitmapToMat(bitmap);
                if (mat == null) return;

                var results = _detectionService.Detect(mat);
                bool personDetected = false;

                foreach (var result in results)
                {
                    if (result.Label == "person" && result.Confidence > 0.4f)
                    {
                        personDetected = true;
                        break;
                    }
                }

                if (personDetected)
                {
                    // Person detected — upload this frame immediately
                    if (_personFrameCounter < PersonUploadFrameLimit)
                    {
                        _personFrameCounter++;
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
                        SaveAndUploadFrame(mat, $"ALERT_{timestamp}.jpg");
                        
                        if (_personFrameCounter == 1)
                        {
                            _eventLogService.LogEvent("Security", "Person detected — uploading frames", DateTime.Now - _recordingStartTime);
                            _auditLog?.Log("PERSON_DETECTED", "Person detected in frame — starting alert upload");
                        }
                        
                        _dispatcherQueue?.TryEnqueue(() =>
                        {
                            StatusMessage = $"🚨 Person detected! Saving frame {_personFrameCounter}/{PersonUploadFrameLimit} to local + cloud";
                        });
                        
                        // After uploading all alert frames, disconnect Google Drive for security
                        if (_personFrameCounter >= PersonUploadFrameLimit)
                        {
                            DisconnectGoogleDrive();
                        }
                    }
                }
                else
                {
                    // No person — reset counter, do hourly snapshot
                    if (_personFrameCounter > 0)
                    {
                        int saved = _personFrameCounter;
                        _dispatcherQueue?.TryEnqueue(() =>
                        {
                            StatusMessage = $"Recording... ({saved} alert frames saved to local + cloud)";
                        });
                    }
                    _personFrameCounter = 0;

                    if ((DateTime.Now - _lastHourlySnapshot).TotalHours >= 1.0)
                    {
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                        SaveAndUploadFrame(mat, $"Snapshot_{timestamp}.jpg");
                        _lastHourlySnapshot = DateTime.Now;
                        _dispatcherQueue?.TryEnqueue(() =>
                        {
                            StatusMessage = "Recording... (hourly snapshot saved to local + cloud)";
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Security] Detection error: {ex.Message}");
            }
        }

        private void SaveAndUploadFrame(Mat frame, string fileName)
        {
            try
            {
                string dateFolder = DateTime.Now.ToString("yyyy-MM-dd");

                // Save locally
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string localFolder = Path.Combine(documents, "TimeLapseCam", "Alerts", dateFolder);
                if (!Directory.Exists(localFolder)) Directory.CreateDirectory(localFolder);
                string localPath = Path.Combine(localFolder, fileName);
                Cv2.ImWrite(localPath, frame);

                // Save to Google Drive
                string? drivePath = FindGoogleDriveFolder();
                if (drivePath != null)
                {
                    string driveFolder = Path.Combine(drivePath, "TimeLapseCam", "Alerts", dateFolder);
                    if (!Directory.Exists(driveFolder)) Directory.CreateDirectory(driveFolder);
                    string destPath = Path.Combine(driveFolder, fileName);
                    Cv2.ImWrite(destPath, frame);
                    
                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        GoogleDriveStatus = $"☁️ Saved: {destPath}";
                    });
                }
                else
                {
                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        GoogleDriveStatus = "⚠️ Google Drive: Not detected — saving locally only";
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Security] Save frame error: {ex.Message}");
            }
        }


        /// <summary>
        /// Disconnect Google Drive after uploading alert evidence.
        /// Kills the Google Drive sync process so an intruder cannot delete cloud copies.
        /// </summary>
        private void DisconnectGoogleDrive()
        {
            try
            {
                _auditLog?.Log("SECURITY_LOCKDOWN", $"Disconnecting Google Drive — {PersonUploadFrameLimit} alert frames uploaded, protecting cloud evidence");

                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("GoogleDriveFS"))
                {
                    proc.Kill();
                }

                _dispatcherQueue?.TryEnqueue(() =>
                {
                    GoogleDriveStatus = "🔒 Google Drive disconnected — evidence secured in cloud";
                    StatusMessage = $"🔒 {PersonUploadFrameLimit} alert frames secured. Google Drive disconnected to protect evidence.";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Security] Failed to disconnect Google Drive: {ex.Message}");
            }
        }

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
