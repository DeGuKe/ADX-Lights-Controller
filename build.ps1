$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $projectRoot 'src\AdxLightsController.cs'
$dist = Join-Path $projectRoot 'dist'
$output = Join-Path $dist 'ADX-Lights-Controller.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Cannot find the .NET Framework C# compiler (csc.exe).'
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $compiler /nologo /target:winexe "/out:$output" `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.dll `
    /reference:System.Core.dll `
    $source
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }

$check = Start-Process -FilePath $output -ArgumentList '--self-test' -PassThru -Wait -WindowStyle Hidden
if ($check.ExitCode -ne 0) { throw "Self-test failed: $($check.ExitCode)" }

Write-Host "Built and checked: $output"
