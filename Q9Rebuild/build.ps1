$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll `
    /reference:WPF\PresentationCore.dll /reference:WPF\WindowsBase.dll /reference:System.Xaml.dll `
    "/out:$PSScriptRoot\Q9Rebuild.exe" "$PSScriptRoot\Engine.cs" "$PSScriptRoot\Panel.cs"
if ($LASTEXITCODE -ne 0) { throw '編譯失敗' }
