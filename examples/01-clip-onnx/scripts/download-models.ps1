# Download CLIP ViT-B/32 ONNX split encoders into ./src/ClipOnnx.App/models
# Requires: pip install huggingface_hub  OR  huggingface-cli

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root "src\ClipOnnx.App\models"
New-Item -ItemType Directory -Force -Path $models | Out-Null

$repo = "inference4j/clip-vit-base-patch32"
$files = @("vision_model.onnx", "text_model.onnx", "vocab.json", "merges.txt")

Write-Host "Downloading from Hugging Face: $repo → $models"
foreach ($f in $files) {
  $dest = Join-Path $models $f
  if (Test-Path $dest) {
    Write-Host "skip $f (exists)"
    continue
  }
  huggingface-cli download $repo $f --local-dir $models --local-dir-use-symlinks False
}

Write-Host "Done. Files in $models"
Get-ChildItem $models | Format-Table Name, Length
