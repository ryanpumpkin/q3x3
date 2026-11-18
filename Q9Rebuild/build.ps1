param(
    # 將本機原版九方檔案（DLL、皮膚、自定範本、說明書）編譯入 exe，之後唔使再裝原版。
    # 整出嚟嘅 exe 包含原版版權資料，只供自己用，唔好公開發佈。
    [switch]$Embed,
    [switch]$Setup,
    [string]$Install = 'C:\Program Files\Q9W10B5'
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'Q9Rebuild.exe'
$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/define:Q9_TSF',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:WPF\PresentationCore.dll', '/reference:WPF\WindowsBase.dll', '/reference:System.Xaml.dll',
    "/out:$out",
    # 公開嘅 Big5-HKSCS 對照表，內置咗就唔使同 exe 放埋一齊
    "/resource:$PSScriptRoot\big5hkscs.map,big5hkscs.map",
    "$PSScriptRoot\Engine.cs", "$PSScriptRoot\Panel.cs", "$PSScriptRoot\TsfProtocol.cs", "$PSScriptRoot\TsfHost.cs",
    "$PSScriptRoot\Lookup.cs"
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

    # 原版輸入法圖示（DLL 圖示資源 12）做 exe 圖示，安裝檔用佢做工作列輸入法圖示
    Add-Type @'
using System; using System.IO; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Q9IconExtract {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr info);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr info);
    static byte[] Read(IntPtr module, int id, int type) {
        IntPtr info = FindResource(module, (IntPtr)id, (IntPtr)type);
        if (info == IntPtr.Zero) throw new InvalidDataException("icon resource " + id + " not found");
        var bytes = new byte[SizeofResource(module, info)];
        Marshal.Copy(LockResource(LoadResource(module, info)), bytes, 0, bytes.Length);
        return bytes;
    }
    // RT_GROUP_ICON (14) + RT_ICON (3) entries -> .ico file
    public static void Save(string dll, int group, string ico) {
        IntPtr module = LoadLibraryEx(dll, IntPtr.Zero, 0x22);   // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE
        if (module == IntPtr.Zero) throw new IOException("cannot load " + dll);
        try {
            byte[] dir = Read(module, group, 14);
            int count = BitConverter.ToUInt16(dir, 4);
            var images = new List<byte[]>();
            for (int i = 0; i < count; i++) images.Add(Read(module, BitConverter.ToUInt16(dir, 6 + i * 14 + 12), 3));
            using (var w = new BinaryWriter(File.Create(ico))) {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)count);
                int offset = 6 + 16 * count;
                for (int i = 0; i < count; i++) {
                    w.Write(dir, 6 + i * 14, 8);
                    w.Write((uint)images[i].Length); w.Write((uint)offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
        }
        finally { FreeLibrary(module); }
    }
}
'@
    $icon = Join-Path ([IO.Path]::GetTempPath()) ('q9-icon-' + [guid]::NewGuid() + '.ico')
    [Q9IconExtract]::Save($dll, 12, $icon)
    $cscArgs += "/win32icon:$icon"
}

# 檔案數量多，用 response file 避開命令列長度限制
$rsp = Join-Path ([IO.Path]::GetTempPath()) ('q9-build-' + [guid]::NewGuid() + '.rsp')
try {
    [IO.File]::WriteAllLines($rsp, ($cscArgs | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding $true))
    & $compiler "@$rsp"
    if ($LASTEXITCODE -ne 0) { throw '編譯失敗' }
}
finally {
    Remove-Item -LiteralPath $rsp -ErrorAction SilentlyContinue
    if ($icon) { Remove-Item -LiteralPath $icon -ErrorAction SilentlyContinue }
}

if ($Setup) {
    $scratch = New-Item -ItemType Directory (Join-Path ([IO.Path]::GetTempPath()) ('q9-setup-' + [guid]::NewGuid()))
    $tsf = Join-Path $scratch.FullName 'Q9Tsf.dll'
    $reg32 = Join-Path $scratch.FullName 'Q9Register32.exe'
    try {
        $tsfArgs = @(
            '/nologo', '/target:library', '/platform:anycpu', '/optimize+', '/reference:System.Windows.Forms.dll',
            '/reference:System.Drawing.dll', "/out:$tsf",
            "$PSScriptRoot\Tsf.cs", "$PSScriptRoot\TsfModeButton.cs", "$PSScriptRoot\TsfProtocol.cs", "$PSScriptRoot\TsfIdentity.cs"
        )
        & $compiler @tsfArgs
        if ($LASTEXITCODE -ne 0) { throw 'Q9Tsf.dll compilation failed.' }
        $helperArgs = @(
            '/nologo', '/target:exe', '/platform:x86', '/optimize+', '/define:Q9_REGISTER32', "/out:$reg32",
            $(if ($Embed) { '/define:Q9_FULL' } else { '/define:Q9_DATAFREE' }),
            "$PSScriptRoot\Setup.cs", "$PSScriptRoot\TsfIdentity.cs"
        )
        & $compiler @helperArgs
        if ($LASTEXITCODE -ne 0) { throw 'Q9Register32.exe compilation failed.' }
        # -Embed -Setup: the bundled exe carries the original data, so the installer does not
        # require an installed original. Contains copyrighted data: personal use only.
        $setupOut = if ($Embed) { "$PSScriptRoot\Q9Setup-full.exe" } else { "$PSScriptRoot\Q9Setup.exe" }
        $setupArgs = @(
            '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/reference:System.Windows.Forms.dll',
            "/win32manifest:$PSScriptRoot\Setup.manifest", "/out:$setupOut",
            $(if ($Embed) { '/define:Q9_FULL' } else { '/define:Q9_DATAFREE' }),
            "/resource:$tsf,Q9Tsf.dll", "/resource:$reg32,Q9Register32.exe", "/resource:$out,Q9Rebuild.exe",
            "$PSScriptRoot\Setup.cs", "$PSScriptRoot\TsfIdentity.cs"
        )
        & $compiler @setupArgs
        if ($LASTEXITCODE -ne 0) { throw 'Q9Setup.exe compilation failed.' }
    }
    finally {
        foreach ($file in $tsf, $reg32) { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $scratch.FullName -ErrorAction SilentlyContinue
    }
}
