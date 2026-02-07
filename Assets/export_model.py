from ultralytics import YOLO

# Load a model (will download if missing)
model = YOLO('yolov8n.pt') 

# Export the model
model.export(format='onnx', dynamic=True, simplify=True)
