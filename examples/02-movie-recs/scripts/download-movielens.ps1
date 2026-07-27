# Downloads MovieLens ml-latest-small CSVs into Resources/Raw/movielens.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "src\MovieRecs.Maui\Resources\Raw\movielens"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$movies = Join-Path $dest "movies.csv"
$ratings = Join-Path $dest "ratings.csv"
if ((Test-Path $movies) -and (Test-Path $ratings)) {
    Write-Host "MovieLens CSVs already present."
    exit 0
}

$zip = Join-Path $env:TEMP "ml-latest-small.zip"
$extract = Join-Path $env:TEMP "ml-latest-small-extract"
Write-Host "Downloading MovieLens ml-latest-small ..."
Invoke-WebRequest -Uri "https://files.grouplens.org/datasets/movielens/ml-latest-small.zip" -OutFile $zip -UseBasicParsing
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $extract -Force
Copy-Item (Join-Path $extract "ml-latest-small\movies.csv") $movies -Force
Copy-Item (Join-Path $extract "ml-latest-small\ratings.csv") $ratings -Force
Write-Host "MovieLens ready under $dest"
