using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace TimeLapseCam.Services
{
    public class AudioService : IDisposable
    {
        private WasapiCapture? _capture;
        private WaveFileWriter? _writer;
        private string _outputFilePath = string.Empty;
        private bool _isRecording;

        public event EventHandler<float>? AudioLevelChanged; // RMS amplitude (0.0 to 1.0)

        public List<string> GetInputDevices()
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return devices.Select(d => d.FriendlyName).ToList();
        }

        public void StartRecording(string? deviceName, string outputFilePath)
        {
            if (_isRecording) StopRecording();

            try 
            {
                var enumerator = new MMDeviceEnumerator();
                MMDevice? device = null;
                
                if (!string.IsNullOrEmpty(deviceName))
                {
                    device = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                                       .FirstOrDefault(d => d.FriendlyName == deviceName);
                }

                if (device == null)
                {
                    // Fallback to default
                     device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                }

                _capture = new WasapiCapture(device);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                // Ensure directory exists
                string? dir = System.IO.Path.GetDirectoryName(outputFilePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) 
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                _outputFilePath = outputFilePath;
                _writer = new WaveFileWriter(outputFilePath, _capture.WaveFormat);

                _capture.StartRecording();
                _isRecording = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Audio Start Failed: {ex.Message}");
                // Ensure cleanup
                StopRecording();
                // Re-throw with more context if possible, or just let it bubble up
                throw new InvalidOperationException($"Audio Error: {ex.Message}", ex);
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_writer != null)
            {
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                
                // Analyze audio level (RMS)
                // Assuming 16-bit PCM or Float. Wasapi uses Float strictly? 
                // WasapiCapture defaults to the device's mix format, usually IEEE Float (32-bit).
                // Let's check WaveFormat. Encoding.

                if (_capture?.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    float max = 0;
                    // Process samples. 32-bit float = 4 bytes per sample.
                    for (int i = 0; i < e.BytesRecorded; i += 4)
                    {
                        float sample = BitConverter.ToSingle(e.Buffer, i);
                        float abs = Math.Abs(sample);
                        if (abs > max) max = abs;
                    }
                    AudioLevelChanged?.Invoke(this, max); 
                }
                else if (_capture?.WaveFormat.Encoding == WaveFormatEncoding.Pcm)
                {
                     // 16-bit PCM
                     if (_capture.WaveFormat.BitsPerSample == 16)
                     {
                        float max = 0;
                        for (int i = 0; i < e.BytesRecorded; i += 2)
                        {
                            short sample = BitConverter.ToInt16(e.Buffer, i);
                            float norm = sample / 32768f;
                            float abs = Math.Abs(norm);
                            if (abs > max) max = abs;
                        }
                        AudioLevelChanged?.Invoke(this, max);
                     }
                }
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }
            _isRecording = false;
            
            if (e.Exception != null)
            {
                Debug.WriteLine($"Audio Recording Error: {e.Exception.Message}");
            }
        }

        public void StopRecording()
        {
            if (_capture != null)
            {
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }

        public void Dispose()
        {
            StopRecording();
        }
    }
}
