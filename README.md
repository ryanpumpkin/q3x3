# q3x3

九方 W10 專業版 1.1.0.0 輸入法嘅重建版（Windows x64 浮動面板）。

repo 本身唔包含任何九方原版檔案：執行時直接讀取本機已安裝嘅原版 `C:\Program Files\Q9W10B5`（碼表、皮膚、字資料），或者用 `-Embed` 喺編譯時將原版檔案內置入 exe，之後唔使再裝原版。內置咗嘅 exe 只可以自己用，唔好公開發佈。詳細用法、已重建同未重建嘅功能見 [Q9Rebuild/README.md](Q9Rebuild/README.md)。

```powershell
.\Q9Rebuild\build.ps1            # 執行時讀原版安裝目錄
.\Q9Rebuild\build.ps1 -Embed     # 將原版檔案內置入 exe
.\Q9Rebuild\Q9Rebuild.exe
```
