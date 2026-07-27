# Downloads MiniLM ONNX + vocab into Resources/Raw/models (run before first build).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root "src\MovieRecs.Maui\Resources\Raw\models"
New-Item -ItemType Directory -Force -Path $models | Out-Null

$onnx = Join-Path $models "all-MiniLM-L6-v2.onnx"
$vocab = Join-Path $models "vocab.txt"

if (-not (Test-Path $onnx) -or (Get-Item $onnx).Length -lt 1MB) {
    Write-Host "Downloading all-MiniLM-L6-v2.onnx ..."
    Invoke-WebRequest -Uri "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx" -OutFile $onnx -UseBasicParsing
}

if (-not (Test-Path $vocab)) {
    Write-Host "Downloading vocab.txt ..."
    Invoke-WebRequest -Uri "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt" -OutFile $vocab -UseBasicParsing
}

Write-Host "Models ready under $models"
Get-ChildItem $models | Format-Table Name, Length
