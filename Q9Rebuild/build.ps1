param(
    # 將本機原版九方檔案（DLL、皮膚、自定範本、說明書）編譯入 exe，之後唔使再裝原版。
    # 整出嚟嘅 exe 包含原版版權資料，只供自己用，唔好公開發佈。
    [switch]$Embed,
    [string]$Install = 'C:\Program Files\Q9W10B5'
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'Q9Rebuild.exe'
$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll',
    '/reference:WPF\PresentationCore.dll', '/reference:WPF\WindowsBase.dll', '/reference:System.Xaml.dll',
    "/out:$out",
    # 公開嘅 Big5-HKSCS 對照表，內置咗就唔使同 exe 放埋一齊
    "/resource:$PSScriptRoot\big5hkscs.map,big5hkscs.map",
    "$PSScriptRoot\Engine.cs", "$PSScriptRoot\Panel.cs"
)

if ($Embed) {
    $root = (Resolve-Path -LiteralPath $Install).Path.TrimEnd('\')
    $dll = Join-Path $root 'q9w10b5tsf64.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw "搵唔到 $dll" }
    $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    if ($hash -ne '02755C6AC5DD4D4518734FD7FB7E069B67F4225147555997A1B79A5DA3C3DEE1') {
        throw "原裝 DLL 版本與已分析版本不符（$hash）"
    }

    $files = @(Get-Item -LiteralPath $dll)
    $files += Get-ChildItem -LiteralPath $root -File |
        Where-Object { $_.Name -match '^(B5|GB)_(Q9PHRASE|Q9UserRelChar|yourAlwaysUse)\.txt$' -or $_.Name -eq 'q9help.chm' }
    $skins = Join-Path $root 'SKINready\default'
    foreach ($size in Get-ChildItem -LiteralPath $skins -Directory) {
        $normal = Join-Path $size.FullName 'normal'
        if (Test-Path -LiteralPath $normal) { $files += Get-ChildItem -LiteralPath $normal -File }
    }
    if (-not ($files | Where-Object { $_.Name -eq 'q9.skin' })) { throw "搵唔到 $skins 入面嘅皮膚" }

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $cscArgs += "/resource:$($file.FullName),Q9/$relative"
    }
    $size = ($files | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("內置 {0} 個原版檔案（{1:N1} MB），只供自己用，唔好公開發佈呢個 exe。" -f $files.Count, $size)
}

# 檔案數量多，用 response file 避開命令列長度限制
$rsp = Join-Path ([IO.Path]::GetTempPath()) ('q9-build-' + [guid]::NewGuid() + '.rsp')
try {
    [IO.File]::WriteAllLines($rsp, ($cscArgs | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding $true))
    & $compiler "@$rsp"
    if ($LASTEXITCODE -ne 0) { throw '編譯失敗' }
}
finally { Remove-Item -LiteralPath $rsp -ErrorAction SilentlyContinue }
