using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace TimeLapseCam.Services
{
    public class TranscriptionService
    {
        public static readonly string[] AvailableModels = { "tiny", "base", "small", "medium", "large" };

        public event EventHandler<string>? ProgressChanged;

        private static readonly string ModelsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TimeLapseCam", "WhisperModels");

        /// <summary>
        /// Transcribes the audio from a video or audio file using Whisper.net (native).
        /// Saves transcript as &lt;basename&gt;_transcript_&lt;model&gt;.txt alongside the source file.
        /// Returns the path to the created transcript file, or null on failure.
        /// </summary>
        public async Task<string?> TranscribeAsync(string mediaFilePath, string model, CancellationToken ct = default)
        {
            if (!File.Exists(mediaFilePath))
            {
                ReportProgress($"File not found: {mediaFilePath}");
                return null;
            }

            string dir = Path.GetDirectoryName(mediaFilePath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(mediaFilePath);
            string transcriptFileName = $"{baseName}_transcript_{model}.txt";
            string transcriptPath = Path.Combine(dir, transcriptFileName);

            // Step 1: Extract audio to 16kHz mono WAV (Whisper requires this format)
            string audioPath = mediaFilePath;
            bool tempAudioCreated = false;
            string ext = Path.GetExtension(mediaFilePath).ToLowerInvariant();

            if (ext != ".wav")
            {
                ReportProgress("Extracting audio...");
                audioPath = Path.Combine(Path.GetTempPath(), $"whisper_{baseName}_{Guid.NewGuid():N}.wav");

                // Ensure ffmpeg is available
                if (!await EnsureFfmpegAsync(ct))
                    return null;

                bool extracted = await ExtractAudioAsync(mediaFilePath, audioPath, ct);
                if (!extracted)
                {
                    ReportProgress("Failed to extract audio.");
                    return null;
                }
                tempAudioCreated = true;
            }

            try
            {
                // Step 2: Ensure model is downloaded
                ReportProgress($"Preparing model '{model}'...");
                string modelPath = await EnsureModelAsync(model, ct);
                if (string.IsNullOrEmpty(modelPath))
                {
                    ReportProgress($"Failed to download model '{model}'.");
                    return null;
                }

                // Step 3: Run transcription via Whisper.net
                ReportProgress($"Transcribing with '{model}'...");
                string transcript = await RunWhisperNetAsync(audioPath, modelPath, ct);

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    ReportProgress("No speech detected or transcription failed.");
                    return null;
                }

                // Step 4: Write annotated transcript
                string header = $"[Transcribed with Whisper model: {model}, Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}]";
                string content = $"{header}\n\n{transcript}";
                await File.WriteAllTextAsync(transcriptPath, content, ct);

                ReportProgress($"✅ Saved: {transcriptFileName}");
                return transcriptPath;
            }
            catch (OperationCanceledException)
            {
                ReportProgress("Transcription cancelled.");
                return null;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                Debug.WriteLine($"[Transcribe] COMException HR=0x{comEx.HResult:X8}: {comEx}");
                ReportProgress($"Error: COMException (0x{comEx.HResult:X8}) {comEx.Message}");
                return null;
            }
            catch (Exception ex)
            {
                string detail = ex.Message;
                if (string.IsNullOrWhiteSpace(detail))
                    detail = ex.GetType().Name;
                if (ex.InnerException != null)
                    detail += $" → {ex.InnerException.Message}";
                Debug.WriteLine($"[Transcribe] Error: {ex}");
                ReportProgress($"Error: {detail}");
                return null;
            }
            finally
            {
                if (tempAudioCreated && File.Exists(audioPath))
                {
                    try { File.Delete(audioPath); } catch { }
                }
            }
        }

        private async Task<string> RunWhisperNetAsync(string wavPath, string modelPath, CancellationToken ct)
        {
            ReportProgress("Loading Whisper engine (GPU/CLBlast)...");

            return await Task.Run(async () =>
            {
                Debug.WriteLine("[whisper] Creating factory...");
                using var factory = WhisperFactory.FromPath(modelPath);
                Debug.WriteLine("[whisper] Factory created. Building processor...");
                await using var processor = factory.CreateBuilder()
                    .WithLanguage("auto")
                    .Build();
                Debug.WriteLine("[whisper] Processor built. Opening audio file...");

                var segments = new List<string>();
                using var fileStream = File.OpenRead(wavPath);
                Debug.WriteLine($"[whisper] Audio opened ({fileStream.Length} bytes). Starting processing...");

                await foreach (var segment in processor.ProcessAsync(fileStream))
                {
                    ct.ThrowIfCancellationRequested();
                    string line = $"[{segment.Start:hh\\:mm\\:ss} --> {segment.End:hh\\:mm\\:ss}] {segment.Text.Trim()}";
                    segments.Add(line);
                    Debug.WriteLine($"[whisper] {line}");
                }

                Debug.WriteLine($"[whisper] Done. {segments.Count} segments.");
                return string.Join("\n", segments);
            }, ct);
        }

        /// <summary>
        /// Downloads the GGML model if not already cached locally.
        /// </summary>
        private async Task<string> EnsureModelAsync(string modelName, CancellationToken ct)
        {
            if (!Directory.Exists(ModelsFolder))
                Directory.CreateDirectory(ModelsFolder);

            string modelFileName = $"ggml-{modelName}.bin";
            string modelPath = Path.Combine(ModelsFolder, modelFileName);

            if (File.Exists(modelPath))
            {
                ReportProgress($"Model '{modelName}' ready.");
                return modelPath;
            }

            ReportProgress($"Downloading model '{modelName}' (first time only)...");

            try
            {
                GgmlType ggmlType = modelName switch
                {
                    "tiny" => GgmlType.Tiny,
                    "base" => GgmlType.Base,
                    "small" => GgmlType.Small,
                    "medium" => GgmlType.Medium,
                    "large" => GgmlType.LargeV1,
                    _ => GgmlType.Base
                };

                using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType);
                using var fileStream = File.Create(modelPath);
                await modelStream.CopyToAsync(fileStream, ct);

                ReportProgress($"✅ Model '{modelName}' downloaded.");
                return modelPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Model] Download error: {ex.Message}");
                ReportProgress($"Download failed: {ex.Message}");
                // Clean up partial download
                if (File.Exists(modelPath))
                    try { File.Delete(modelPath); } catch { }
                return string.Empty;
            }
        }

        /// <summary>
        /// Force re-downloads the specified model, replacing any cached version.
        /// </summary>
        public async Task<bool> ForceDownloadModelAsync(string modelName, CancellationToken ct = default)
        {
            if (!Directory.Exists(ModelsFolder))
                Directory.CreateDirectory(ModelsFolder);

            string modelFileName = $"ggml-{modelName}.bin";
            string modelPath = Path.Combine(ModelsFolder, modelFileName);

            // Delete existing
            if (File.Exists(modelPath))
            {
                try { File.Delete(modelPath); }
                catch (Exception ex)
                {
                    ReportProgress($"Cannot delete existing model: {ex.Message}");
                    return false;
                }
            }

            ReportProgress($"Re-downloading model '{modelName}'...");
            string result = await EnsureModelAsync(modelName, ct);
            return !string.IsNullOrEmpty(result);
        }

        /// <summary>
        /// Resolved path to ffmpeg binary. Null means not yet searched.
        /// </summary>
        private string? _ffmpegPath;

        /// <summary>
        /// Ensures ffmpeg is available. Checks common install locations first,
        /// then auto-installs via winget if truly missing.
        /// </summary>
        public async Task<bool> EnsureFfmpegAsync(CancellationToken ct = default)
        {
            // Try to find existing ffmpeg
            _ffmpegPath = FindFfmpegPath();

            if (_ffmpegPath != null)
            {
                ReportProgress($"✅ ffmpeg found: {_ffmpegPath}");
                return true;
            }

            ReportProgress("ffmpeg not found — installing via winget...");
            bool installed = await RunInstallerAsync("winget",
                "install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements", ct);

            // Re-search after install attempt (even if exit code != 0, sometimes it's already installed)
            _ffmpegPath = FindFfmpegPath();

            if (_ffmpegPath != null)
            {
                ReportProgress($"✅ ffmpeg ready: {_ffmpegPath}");
                return true;
            }

            ReportProgress("❌ Failed to install ffmpeg. Please install manually: winget install Gyan.FFmpeg");
            return false;
        }

        /// <summary>
        /// Always runs winget install for ffmpeg (install or upgrade), then resolves the path.
        /// Use this for the explicit "Install" button.
        /// </summary>
        public async Task<bool> InstallFfmpegAsync(CancellationToken ct = default)
        {
            ReportProgress("Installing ffmpeg via winget...");
            
            // Run winget install (may succeed or say "already installed")
            await RunInstallerAsync("winget",
                "install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements", ct);

            // Always re-search regardless of exit code (winget returns 1 for "already installed")
            _ffmpegPath = FindFfmpegPath();

            if (_ffmpegPath != null)
            {
                ReportProgress($"✅ ffmpeg ready: {_ffmpegPath}");
                return true;
            }

            ReportProgress("❌ ffmpeg not found after install. Try running manually: winget install Gyan.FFmpeg");
            return false;
        }

        /// <summary>
        /// Gets the resolved ffmpeg path (or "ffmpeg" as fallback for PATH).
        /// </summary>
        public string GetFfmpegPath() => _ffmpegPath ?? "ffmpeg";

        /// <summary>
        /// Searches common locations for ffmpeg.exe.
        /// </summary>
        private static string? FindFfmpegPath()
        {
            // 1. Check PATH via cmd
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        string first = output.Split('\n')[0].Trim();
                        if (File.Exists(first)) return first;
                    }
                }
            }
            catch { }

            // 2. Search WinGet Packages folder (Gyan.FFmpeg installs here)
            string wingetPackages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");

            if (Directory.Exists(wingetPackages))
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(wingetPackages, "Gyan.FFmpeg*"))
                    {
                        foreach (var file in Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories))
                        {
                            return file;
                        }
                    }
                }
                catch { }
            }

            // 3. Check common install paths
            string[] commonPaths = {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffmpeg.exe"),
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path)) return path;
            }

            return null;
        }

        public async Task<bool> IsCommandAvailableAsync(string command, CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command} -version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> RunInstallerAsync(string command, string args, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command} {args}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    ReportProgress("Could not start installer process.");
                    return false;
                }

                _ = Task.Run(async () =>
                {
                    while (!proc.StandardOutput.EndOfStream)
                    {
                        string? line = await proc.StandardOutput.ReadLineAsync(ct);
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Debug.WriteLine($"[install] {line}");
                            ReportProgress(line);
                        }
                    }
                }, ct);

                string stderr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);

                if (proc.ExitCode != 0)
                {
                    Debug.WriteLine($"[install] Exit code {proc.ExitCode}: {stderr}");
                    if (!string.IsNullOrWhiteSpace(stderr))
                        ReportProgress($"Error: {stderr.Trim()}");
                }

                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[install] Error: {ex.Message}");
                ReportProgress($"Error: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ExtractAudioAsync(string videoPath, string outputWavPath, CancellationToken ct)
        {
            try
            {
                string ffmpegExe = GetFfmpegPath();
                ReportProgress($"Using ffmpeg: {Path.GetFileName(ffmpegExe)}");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = $"-i \"{videoPath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 -y \"{outputWavPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    ReportProgress("Failed to start ffmpeg process.");
                    return false;
                }

                string stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    Debug.WriteLine($"[ffmpeg] Exit {process.ExitCode}: {stderr}");
                    // Show the last meaningful line to the user
                    string lastLine = stderr.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Unknown error";
                    ReportProgress($"ffmpeg error: {lastLine}");
                    return false;
                }

                bool exists = File.Exists(outputWavPath);
                if (!exists)
                    ReportProgress("ffmpeg completed but output file not found.");
                return exists;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // This happens when the exe path is wrong or inaccessible
                Debug.WriteLine($"[ffmpeg] Win32 Error: {ex.Message}");
                ReportProgress($"Cannot run ffmpeg: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ffmpeg] Error: {ex.Message}");
                ReportProgress($"ffmpeg error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds all transcript files for a given recording.
        /// </summary>
        public static List<TranscriptInfo> FindTranscripts(string mediaFilePath)
        {
            var results = new List<TranscriptInfo>();
            string dir = Path.GetDirectoryName(mediaFilePath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(mediaFilePath);
            string pattern = $"{baseName}_transcript_*.txt";

            if (!Directory.Exists(dir)) return results;

            foreach (string file in Directory.GetFiles(dir, pattern))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                string suffix = fileName.Substring(baseName.Length + "_transcript_".Length);
                results.Add(new TranscriptInfo
                {
                    FilePath = file,
                    ModelName = suffix,
                    CreatedDate = File.GetCreationTime(file)
                });
            }

            return results;
        }

        /// <summary>
        /// Reads the full transcript content including the annotation header.
        /// </summary>
        public static string ReadTranscript(string transcriptPath)
        {
            if (!File.Exists(transcriptPath)) return string.Empty;
            return File.ReadAllText(transcriptPath);
        }

        private void ReportProgress(string message)
        {
            ProgressChanged?.Invoke(this, message);
        }
    }

    public class TranscriptInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
    }
}
