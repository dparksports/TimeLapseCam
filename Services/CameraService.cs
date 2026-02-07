using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using Windows.Graphics.Imaging;

namespace TimeLapseCam.Services
{
    public class CameraService
    {
        private MediaCapture? _mediaCapture;
        private MediaFrameReader? _frameReader;
        private bool _isInitialized;
        public string? CurrentCameraId { get; private set; }

        public event EventHandler<SoftwareBitmap>? FrameArrived;

        public async Task<List<DeviceInformation>> GetAvailableCamerasAsync()
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices.ToList();
        }

        public async Task InitializeAsync(DeviceInformation? cameraDevice = null)
        {
            if (_mediaCapture != null)
            {
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }

            _mediaCapture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video, // We handle audio separately via NAudio for better control
                MemoryPreference = MediaCaptureMemoryPreference.Cpu // Better for OpenCV/Onnx interop
            };

            if (cameraDevice != null)
            {
                settings.VideoDeviceId = cameraDevice.Id;
                CurrentCameraId = cameraDevice.Id;
            }
            else
            {
                CurrentCameraId = null;
            }

            try
            {
                await _mediaCapture.InitializeAsync(settings);
                _isInitialized = true;
                
                // Initialize Frame Reader for AI
                await InitializeFrameReaderAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Camera Initialization Failed: {ex.Message}");
                _isInitialized = false;
                throw;
            }
        }

        public async Task StartPreviewAsync()
        {
            if (!_isInitialized || _mediaCapture == null) return;
            
            // Preview is handled via FrameArrived event and MainViewModel binding
            
            // Start the frame reader
            if (_frameReader != null)
            {
                await _frameReader.StartAsync();
            }
        }

        public async Task StopPreviewAsync()
        {
            if (_frameReader != null)
            {
                await _frameReader.StopAsync();
            }
            // Stop preview logic primarily happens by disposing the source or setting it to null in the UI
        }

        private async Task InitializeFrameReaderAsync()
        {
            // Find a source that offers video frames
            if (_mediaCapture == null) return;
            
            var frameSource = _mediaCapture.FrameSources.Values
                .FirstOrDefault(source => source.Info.SourceKind == MediaFrameSourceKind.Color);

            if (frameSource != null)
            {
                // Create a frame reader for the source
                _frameReader = await _mediaCapture.CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8);
                _frameReader.FrameArrived += OnFrameArrived;
            }
        }

        private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            using (var frameReference = sender.TryAcquireLatestFrame())
            {
                if (frameReference != null)
                {
                    var videoFrame = frameReference.VideoMediaFrame;
                    if (videoFrame != null && videoFrame.SoftwareBitmap != null)
                    {
                        // Clone the bitmap because the frame reference will be disposed
                        // Note: In high perf scenarios, we might want to process directly or use a pool.
                        // For low FPS AI, cloning is acceptable for simplicity to avoid race conditions.
                        // However, SoftwareBitmap copy is not trivial. 
                        // Better: Invoke event with the bitmap and let the subscriber handle it synchronously or copy it.
                        // We will invoke and let the subscriber handle the copy/use context.
                        FrameArrived?.Invoke(this, videoFrame.SoftwareBitmap);
                    }
                }
            }
        }

        public void Dispose()
        {
            _frameReader?.Dispose();
            _mediaCapture?.Dispose();
        }
    }
}
