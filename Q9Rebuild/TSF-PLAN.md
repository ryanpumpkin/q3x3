# 計劃：變做真正輸入法（TSF）＋安裝檔

狀態：**未開始**，淨係計劃。2026-11-17 寫。

## 目標

- 重建版好似原版九方咁，喺「設定 → 時間與語言 → 語言」嘅輸入法清單出現，用 Win+Space 切換。
- 唔再靠全域鍵盤攔截（`SetWindowsHookEx`）同 `SendInput` 送字，改用 TSF 正式寫字入程式，連以系統管理員身分執行嘅視窗都打到字。
- 有一個 `Q9Setup.exe` 負責安裝、升級、解除安裝，喺「已安裝的應用程式」入面出現。

## 已決定

- **用 C#（.NET Framework 4），唔裝新工具。** 用 Windows 本身有嘅 `csc.exe`（同 `build.ps1` 一樣）。部機冇 Visual Studio、C++ 編譯器、Inno Setup、WiX。
- 代價：每個用九方打字嘅程式都要載入 .NET CLR，第一次切換會慢少少；Store 程式（AppContainer）可能載入唔到，要實機試。
- 唔包原版檔案：安裝檔要先檢查 `C:\Program Files\Q9W10B5` 存在，冇就停。

## 原版點樣註冊（2026-11-17 喺本機查到）

| 項目 | 值 |
| --- | --- |
| TIP CLSID | `{99BAE846-9DEE-4F12-A0C2-5267C4B5831F}` |
| 語言 | `0x0404`（繁體中文（台灣）），**唔係** 0x0C04 |
| Profile GUID | `{B4EA56DD-12CF-4DA4-9E54-4D3EF8B898CD}` |
| 名稱 | 九方W10專業版 |
| 圖示 | `q9w10b5tsf64.dll`，IconIndex `-12` |
| 64 位元 | `HKCR\CLSID\{…}\InprocServer32` = `q9w10b5tsf64.dll`，Apartment |
| 32 位元 | `HKLM\SOFTWARE\WOW6432Node\Classes\CLSID\{…}\InprocServer32` = `q9w10b5tsf32.dll` |
| CTF 註冊 | `HKLM\SOFTWARE\Microsoft\CTF\TIP\{…}`，WOW6432Node 都有一份 |

原版登記嘅 category：

- `{34745C63-B2F0-4784-8B67-5E12C8701A31}` GUID_TFCAT_TIP_KEYBOARD
- `{13A016DF-560B-46CD-947A-4C3AF1E0E35D}` TIPCAP_IMMERSIVESUPPORT
- `{25504FB4-7BAB-4BC1-9C69-CF81890F0EF5}` TIPCAP_SYSTRAYSUPPORT
- `{49D2F9CF-1F5E-11D7-A6D3-00065B84435C}` TIPCAP_UIELEMENTENABLED
- `{49D2F9CE-1F5E-11D7-A6D3-00065B84435C}` TIPCAP_INPUTMODECOMPARTMENT
- `{364215D9-75BC-11D7-A6EF-00065B84435C}` TIPCAP_COMLESS
- `{CCF05DD7-4A87-11D7-A6E2-00065B84435C}` TIPCAP_WOW16
- `{046B8C80-1647-40F7-9B21-B93B81AABC1B}` DISPLAYATTRIBUTEPROVIDER

用戶語言清單（`Get-WinUserLanguageList`）：zh-Hant-TW 入面有原版 `0404:{99BAE846…}{B4EA56DD…}`。

**重建版要用自己嘅新 CLSID 同 profile GUID**，唔好撞原版，兩個可以並存。語言跟原版用 0x0404，因為用戶已經有 zh-Hant-TW。

## 架構

```
用戶程式（記事本、Word…）                     Q9Rebuild.exe（一個，常駐）
┌──────────────────────────┐   WM_COPYDATA    ┌──────────────────────────┐
│ Q9Tsf.dll（C# COM，細）   │ ───────────────▶ │ IPC 視窗 "Q9Rebuild.IPC"  │
│ ITfTextInputProcessor     │  按鍵、焦點       │ Engine + 面板（現有）      │
│ ITfKeyEventSink           │ ◀─────────────── │ 上字時送文字返去           │
│ 用 edit session 寫字      │   要上屏嘅字      │                          │
└──────────────────────────┘                  └──────────────────────────┘
```

DLL 只做「收鍵、轉發、寫字」，所有輸入法邏輯同面板留喺 exe。咁樣 DLL 細、載入快，而且狀態（關聯字、最近用字）全部程式共用一份。

### Q9Tsf.dll（新檔 `Tsf.cs`，編譯做 AnyCPU class library）

- COM 可見 class，實作：
  - `ITfTextInputProcessor`：`Activate` 時 advise key event sink、thread focus sink，建立一個 message-only 視窗收 exe 送返嚟嘅字；`Deactivate` 時全部 unadvise。
  - `ITfKeyEventSink`：`OnTestKeyDown`／`OnKeyDown`／`OnKeyUp`。
  - `ITfThreadFocusSink`：`OnSetThreadFocus`／`OnKillThreadFocus` → 通知 exe 顯示／隱藏面板。
  - `ITfEditSession`、`ITfCompositionSink`：寫字用。
- 寫字：`RequestEditSession(TF_ES_READWRITE)` → `ITfInsertAtSelection.InsertTextAtSelection(TF_IAS_QUERYONLY)` 攞位置 → `ITfContextComposition.StartComposition` → `ITfRange.SetText` → collapse 去尾、`SetSelection` → `EndComposition`。`StartComposition` 失敗就直接 `InsertTextAtSelection`。
- 要自己用 `[ComImport]` 宣告 TSF interface。**自己實作嘅 interface 要完整準確；只係呼叫嘅 interface，用唔到嘅方法都要用佔位方法保持 vtable 次序。** 主要 IID：
  - ITfTextInputProcessor `aa80e7f7-2021-11d2-93e0-0060b067b86e`
  - ITfThreadMgr `aa80e801-…`、ITfKeystrokeMgr `aa80e7f0-…`、ITfKeyEventSink `aa80e7f5-…`
  - ITfSource `4ea48a35-60ae-446f-8fd6-e6a8d82459f7`、ITfThreadFocusSink `c0f1db0c-3a20-405c-a303-96b6010a885f`
  - ITfContext `aa80e7fd-…`、ITfDocumentMgr `aa80e7f4-…`、ITfEditSession `aa80e803-…`、ITfRange `aa80e7ff-…`
  - ITfInsertAtSelection `55ce16ba-3014-41c1-9ceb-fade1446ac6c`、ITfContextComposition `d40c8aae-ac92-4fc7-9a11-0ee0e23aa39b`
  - ITfComposition `20168d64-5a8f-4a5a-b7bd-cfa29f4d0fd9`、ITfCompositionSink `a781718c-579a-4b15-a280-32b8577acc5e`
  - 註冊用：ITfInputProcessorProfiles `1f02b6c5-7842-4ee6-8a0b-9a24183a95ca`（CLSID `33c53a50-f456-4884-b049-85fd643ecfed`）、ITfCategoryMgr `c3acefb5-f69d-4905-938f-fcadcf4be830`（CLSID `a4b544a1-438d-4b41-9325-869523e2d6c7`）

### 按鍵流程（最要小心嘅地方）

- `Engine.Key()` 一按就改狀態，冇辦法淨係「問下食唔食」。所以 **`OnTestKeyDown` 已經叫 exe 真正處理**，DLL 記住結果同要上屏嘅字；跟住嘅 `OnKeyDown` 同一隻鍵就直接用記住嘅結果，喺 edit session 寫字。冇 Test 直接嚟 `OnKeyDown` 就即刻處理。同一隻鍵連續 Test 兩次唔好處理兩次。
- `OnTestKeyDown` 入面唔准改文件，所以字一定要等到 `OnKeyDown` 先寫。
- 食咗嘅鍵，DLL 記低，`OnKeyUp` 都食埋（等於而家嘅 `heldKeys`）。
- Shift 按下／放開都轉發，exe 照用現有邏輯判斷「快按 Shift」，但唔食 Shift。
- 擴展鍵：lParam 第 24 位 → 而家 `MapKey` 嘅 `flags & 1`。
- 修飾鍵照用 `GetAsyncKeyState`（全域，跨程序都準）。

### IPC（WM_COPYDATA，`dwData` = 指令）

| 方向 | 指令 | 內容 | 回傳 |
| --- | --- | --- | --- |
| DLL → exe | KEY_DOWN | vk、lParam、DLL 視窗 hwnd | 1 = 食咗 |
| DLL → exe | KEY_UP | 同上 | 1 = 食咗 |
| DLL → exe | FOCUS | DLL 視窗 hwnd | |
| DLL → exe | BLUR | DLL 視窗 hwnd | |
| exe → DLL | COMMIT | UTF-16 文字 | |

- exe 用 `FindWindowEx(HWND_MESSAGE, 0, null, "Q9Rebuild.IPC")` 搵到。
- exe 喺處理 KEY_DOWN 期間用 `SendMessage` 送 COMMIT 返 DLL 視窗係得嘅（DLL 執行緒等緊 SendMessage 時會處理送入嚟嘅訊息）。DLL 喺按鍵處理中收到就放入待寫，唔係（例如滑鼠點面板上字）就即刻用 `ThreadMgr.GetFocus().GetTop()` 嘅 context 開 edit session。
- **UIPI**：DLL 喺管理員程式入面時，exe 送唔到訊息入去，DLL 要對自己視窗 `ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW)`；exe 嘅 IPC 視窗都要做。
- 用 `SendMessageTimeout`（例如 200ms），exe 冇反應時唔好令用戶程式卡住，當冇食鍵。

### Q9Rebuild.exe 要改

- `HookCallback` 入面嘅判斷抽出嚟做 `bool ProcessKey(int vk, bool down, int flags)`，鍵盤攔截同 TSF 共用。
- 新增 `--tsf` 模式：唔裝鍵盤攔截，開 IPC 視窗；面板預設隱藏，收到 FOCUS 先顯示，BLUR 之後延遲約 200ms，前景唔係自己程序（例如開緊右鍵選單）先隱藏。
- `Deliver()`：TSF 模式送 COMMIT 去最後 FOCUS 嗰個 DLL 視窗；冇或者送失敗就照舊用 `Native.Type`。
- 單一執行個體：用 named mutex。
- 舊嘅獨立模式（冇安裝都用得）保留。

### DLL 自動開 exe

exe 未行緊時 DLL 喺 `Activate` 開 `Q9Rebuild.exe --tsf`（同 DLL 同一個資料夾）。以下情況**唔好開**：SYSTEM 帳戶（例如登入畫面 LogonUI）、管理員程式入面（會開咗個管理員 exe）、AppContainer、或者 DLL 本身喺 Q9Rebuild.exe 入面（自己程序入面直接唔食鍵）。

## Q9Setup.exe（新檔 `Setup.cs`）

- C# winexe，manifest `requireAdministrator`，將 `Q9Rebuild.exe`、`Q9Tsf.dll`、`big5hkscs.map` 當 embedded resource 包入去。
- **安裝**：
  1. 檢查原版 `C:\Program Files\Q9W10B5`。
  2. 複製去 `C:\Program Files\Q9Rebuild\`，連自己（做解除安裝用）。
  3. 手寫 COM 註冊（唔用 regasm），64 位元同 32 位元（`RegistryView.Registry32`）各一份：`CLSID\{新}\InprocServer32` = `mscoree.dll`、`ThreadingModel=Apartment`、`Class`、`Assembly`、`RuntimeVersion=v4.0.30319`、`CodeBase=file:///…Q9Tsf.dll`。
  4. 用 `ITfInputProcessorProfiles.Register` + `AddLanguageProfile(0x0404, …)` 同 `ITfCategoryMgr.RegisterCategory` 登記 TSF。64 位元同 32 位元嘅 CTF 都要有，即係要喺 32 位元程序再做一次（例如 setup 用 32 位元 `rundll32`／自己開一個 x86 小程式），或者直接照抄原版嘅 registry 結構寫入 WOW6432Node。
  5. 用 `input.dll` 嘅 `InstallLayoutOrTip("0404:{CLSID}{Profile}", 0)` 加入目前用戶嘅輸入法清單。
  6. `HKLM\…\Run` 加 `Q9Rebuild.exe --tsf`，即刻開一次。
  7. `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Q9Rebuild` 寫 DisplayName、UninstallString（`Q9Setup.exe /uninstall`）、DisplayIcon、Publisher。
- **升級**：DLL 可能俾其他程式鎖住，先將舊檔改名做 `.old`（鎖住嘅 DLL 可以改名），再複製新檔，`.old` 用 `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` 重開機刪。
- **解除安裝**：`InstallLayoutOrTip(…, ILOT_UNINSTALL)`、刪 CTF 同 CLSID 註冊、Run、Uninstall 項目，關 exe，刪檔（鎖住嘅重開機刪）。`%APPDATA%\Q9Rebuild` 用戶資料保留。
- `build.ps1` 加多兩步：編譯 `Q9Tsf.dll`（`/target:library /platform:anycpu`），再編譯 `Q9Setup.exe`（`/resource:` 包埋三個檔）。

## 要實機測試

- 記事本、WordPad／Word、Chrome／Edge、VS Code、Windows Terminal、cmd。
- 以系統管理員身分執行嘅程式。
- 32 位元程式（例如舊版 32 位元軟件）。
- Store 程式同開始功能表搜尋：.NET CLR 未必載入到。載入唔到唔可以令程式死機；如果唔得，考慮唔登記 IMMERSIVESUPPORT。
- 滑鼠點面板上字、右鍵選單、快按 Shift、Win+Space 切去其他輸入法再切返嚟。
- 原版九方同重建版並存。
- 升級（覆蓋安裝）同解除安裝之後清單同 registry 乾淨。

## 參考

- Microsoft「Text Services Framework」文件，同 GitHub 上 Windows-classic-samples 入面嘅 `SampleIME`（C++，但流程一樣）。
