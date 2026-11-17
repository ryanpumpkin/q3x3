param(
    [Parameter(Mandatory = $true)][string]$Reference,
    [ValidatePattern('^[SGPZC0-9.+/b-]+$')][string]$Keys = 'C303',
    [ValidateRange(200, 1000)][int]$Size = 300,
    [ValidateRange(0, 100000)][int]$X = 0,
    [ValidateRange(0, 100000)][int]$Y = 0,
    [ValidateRange(0, 100000)][int]$Height = 0,
    [ValidateRange(0, 255)][int]$Tolerance = 2,
    [string]$Output = (Join-Path $PSScriptRoot '../preview/parity')
)

$ErrorActionPreference = 'Stop'
$referencePath = (Resolve-Path -LiteralPath $Reference).Path
$outputPath = [IO.Path]::GetFullPath($Output)
$scratch = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('q9-check-' + [guid]::NewGuid()))
$exe = Join-Path $scratch.FullName 'ComparePanel.exe'
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /main:ComparePanel `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll `
        /reference:WPF\PresentationCore.dll /reference:WPF\WindowsBase.dll /reference:System.Xaml.dll `
        "/out:$exe" "$PSScriptRoot\Engine.cs" "$PSScriptRoot\Panel.cs" "$PSScriptRoot\ComparePanel.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Parity tool did not compile.' }
    & $exe $referencePath $outputPath "$PSScriptRoot/big5hkscs.map" $Keys $Size $X $Y $Height $Tolerance
    $result = $LASTEXITCODE
}
finally {
    foreach ($name in 'ComparePanel.exe', 'settings.ini') {
        Remove-Item -LiteralPath (Join-Path $scratch.FullName $name) -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $scratch.FullName -ErrorAction SilentlyContinue
}
exit $result
