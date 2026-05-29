param(
    [string]$Output = "D:\WebSnapshots-main\output_precaution_run",
    [string]$Audit = "",
    [string]$Viewer = "viewer.htm",
    [string]$Run = "",
    [string]$Municipalities = "",
    [int]$Width = 1800,
    [int]$Height = 1100,
    [int]$MaxNavSlices = 10,
    [int]$MaxMainSlices = 6,
    [int]$SettleMs = 250
)

$ErrorActionPreference = "Stop"

$repo = $PSScriptRoot
$project = Join-Path $repo "_audit_tool\VisualAuditExpanded\VisualAuditExpanded.csproj"

if (-not (Test-Path -LiteralPath $project)) {
    throw "Expanded viewer audit tool was not found at $project"
}

if ([string]::IsNullOrWhiteSpace($Audit)) {
    $Audit = Join-Path $Output "_visual_quality_audit\scroll-matrix"
}

$toolArgs = @(
    "--output", $Output,
    "--audit", $Audit,
    "--viewer", $Viewer,
    "--width", "$Width",
    "--height", "$Height",
    "--max-nav-slices", "$MaxNavSlices",
    "--max-main-slices", "$MaxMainSlices",
    "--settle-ms", "$SettleMs"
)

if (-not [string]::IsNullOrWhiteSpace($Run)) {
    $toolArgs += @("--run", $Run)
}

if (-not [string]::IsNullOrWhiteSpace($Municipalities)) {
    $toolArgs += @("--municipalities", $Municipalities)
}

dotnet run --project $project -c Release -- @toolArgs
