using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using System.Diagnostics;

namespace TimeLapseCam.Services
{
    public class ObjectDetectionService : IDisposable
    {
        private InferenceSession? _session;
        private string[] _labels = Array.Empty<string>();
        private const int TargetSize = 640;

        public bool IsInitialized { get; private set; }

        public async Task InitializeAsync(string modelPath)
        {
            await Task.Run(() => 
            {
                try
                {
                    // Use CPU execution provider for broad compatibility. 
                    // Could be upgraded to CUDA or DirectML if available.
                    var options = new SessionOptions();
                    options.AppendExecutionProvider_CPU(0);
                    
                    _session = new InferenceSession(modelPath, options);

                    // COCO labels for YOLOv8
                    _labels = new string[] 
                    { 
                        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
                    "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
                    "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
                    "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
                    "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
                    "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
                    "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone",
                    "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear",
                    "hair drier", "toothbrush"
                };

                    IsInitialized = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load model: {ex.Message}");
                    IsInitialized = false;
                    throw;
                }
            });
        }

        public List<DetectionResult> Detect(Mat image, float confidenceThreshold = 0.5f)
        {
            if (!IsInitialized || image.Empty()) return new List<DetectionResult>();

            // Preprocess
            // Resize to 640x640, normalize to [0,1], swap RB, etc.
            // BlobFromImage handles resizing, swapping RBInt -> Float, normalizing (1/255 if scale=1/255)
            // YOLOv8 expects RGB, prioritized 640x640 input, normalized 0-1.
            
            // Note: OpenCV reads BGR. BlobFromImage with swapRB=true makes it RGB.
            using var blob = CvDnn.BlobFromImage(image, 1.0 / 255.0, new Size(TargetSize, TargetSize), new Scalar(0, 0, 0), true, false);
            
            // Create Input Tensor
            // BlobFromImage returns NCHW formatted blob (1, 3, 640, 640)
            // Typically OpenCvSharp Mat/Blob can be fed into Onnx via DenseTensor
            
            // Convert blob to DenseTensor<float>
            // This is the heavy part. We need to copy memory.
            // Shape: 1, 3, 640, 640
            
            float[] data = new float[1 * 3 * TargetSize * TargetSize];
            // Access blob data safely
            
            // Optimized way: 
            // The blob is a continuous block of floats.
            System.Runtime.InteropServices.Marshal.Copy(blob.Data, data, 0, data.Length);
            
            var inputTensor = new DenseTensor<float>(data, new[] { 1, 3, TargetSize, TargetSize });

            // Run Inference
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("images", inputTensor)
            };

            using var results = _session?.Run(inputs);
            if (results == null) return new List<DetectionResult>();
            
            // Postprocess
            // YOLOv8 output shape is (1, 84, 8400) usually [Batch, Box+Classes, Anchors]
            // We need to transpose to (1, 8400, 84) to make it easier to iterate, or just iterate directly.
            // 84 = 4 box coordinates (cx, cy, w, h) + 80 class probs
            
            var output = results.First().AsTensor<float>();
            // Verify shape
            // output.Dimensions[1] is 84? or 8400?
            // Usually [1, 84, 8400]
            
            int channels = output.Dimensions[1]; // 84
            int anchors = output.Dimensions[2];  // 8400

            var detections = new List<DetectionResult>();

            // Iterate over anchors
            for (int i = 0; i < anchors; i++)
            {
                // Find max class score
                float maxScore = 0;
                int maxClassId = -1;
                
                // Classes start at index 4
                for (int c = 0; c < 80; c++)
                {
                    float score = output[0, 4 + c, i];
                    if (score > maxScore)
                    {
                        maxScore = score;
                        maxClassId = c;
                    }
                }

                if (maxScore > confidenceThreshold)
                {
                    // Get Box
                    float cx = output[0, 0, i];
                    float cy = output[0, 1, i];
                    float w = output[0, 2, i];
                    float h = output[0, 3, i];

                    // Scale back to original image size
                    float xScale = (float)image.Width / TargetSize;
                    float yScale = (float)image.Height / TargetSize;

                    float x = (cx - w / 2) * xScale;
                    float y = (cy - h / 2) * yScale;
                    float width = w * xScale;
                    float height = h * yScale;

                    detections.Add(new DetectionResult
                    {
                        Label = _labels[maxClassId],
                        Confidence = maxScore,
                        Box = new Rect((int)x, (int)y, (int)width, (int)height)
                    });
                }
            }

            // Apply NMS (Non-Maximum Suppression)
            // OpenCV has a built-in NMSBoxes function but it takes Rect2d and scores
            // Implementing basic NMS or using OpenCV
            
            if (detections.Count == 0) return detections;
            
            var boxes = detections.Select(d => d.Box).ToArray();
            var scores = detections.Select(d => d.Confidence).ToArray();
            
            // Use OpenCV NMS
            // Requires Rect2d[]
            // CvDnn.NMSBoxes expects specific inputs
            
            // Manual simple NMS for now or skip if trusted model output usually handles it (YOLOv8 output is raw candidates, needs NMS)
            // We'll trust raw output for a moment or implement robust NMS later.
            // Actually, let's just return top K or basic filter.
            // To be robust, let's assume we return all and filter in UI or implement a simple NMS.
            
            return PerformNMS(detections);
        }

        private List<DetectionResult> PerformNMS(List<DetectionResult> detections, float iouThreshold = 0.45f)
        {
            var results = new List<DetectionResult>();
            var sorted = detections.OrderByDescending(d => d.Confidence).ToList();

            while (sorted.Count > 0)
            {
                var current = sorted[0];
                results.Add(current);
                sorted.RemoveAt(0);

                sorted.RemoveAll(d => CalculateIoU(current.Box, d.Box) > iouThreshold);
            }
            return results;
        }

        private float CalculateIoU(Rect box1, Rect box2)
        {
            int x1 = Math.Max(box1.X, box2.X);
            int y1 = Math.Max(box1.Y, box2.Y);
            int x2 = Math.Min(box1.Right, box2.Right);
            int y2 = Math.Min(box1.Bottom, box2.Bottom);

            int w = Math.Max(0, x2 - x1);
            int h = Math.Max(0, y2 - y1);

            float intersection = w * h;
            float union = box1.Width * box1.Height + box2.Width * box2.Height - intersection;

            return intersection / union;
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }

    public class DetectionResult
    {
        public string Label { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public Rect Box { get; set; }
    }
}
