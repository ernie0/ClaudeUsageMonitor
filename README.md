# ClaudeUsageMonitor｜Claude 額度小幫手

在 Windows 桌面放一個小浮窗，隨時看 **Claude 訂閱方案的 5 小時額度、每週額度、各模型每週額度還剩多少、什麼時候重置、照目前速度會不會在重置前用完**。工具開著時會累積使用曲線，可匯出 CSV。

這是**非官方工具**，不是 Anthropic 的產品，也未受其背書。不會幫你增加額度，也不會替你送出任何提問。設計參考 [hayabusasean/CodexUsageMonitor](https://github.com/hayabusasean/CodexUsageMonitor)（MIT），是它的 Claude 版本。

## 適合誰

- Windows 10／11（64 位元）。
- 電腦上已安裝**官方 Claude Code**（原生安裝程式或 WinGet 版的 `claude.exe`），而且用 **Claude Pro／Max／Team 訂閱帳號登入**。
- 只用 API 金鑰、Bedrock、Vertex 等登入方式沒有「方案額度」，本工具會顯示原因後停止查詢。
- 只在瀏覽器登入 claude.ai 不夠；本工具是透過電腦上的 Claude Code 查詢。

## 畫面

以下截圖都是用 `--screenshot` 的示範資料產生，不是真實帳號。

**標準浮窗**：左兩塊是 5 小時與本週額度，右側小卡是各模型的本週額度，底部一句話講結論。

![標準浮窗](docs/screenshots/overlay-settled.png)

**小動畫**：額度重置時噴紙屑並浮出「+60% 已恢復」、用掉額度時浮出「−1%」、滑鼠指到的卡片會浮起。

![小動畫](docs/screenshots/overlay-effects.png)

**精簡浮窗**：只顯示比較緊的那個額度。

![精簡浮窗](docs/screenshots/overlay-compact.png)

**使用紀錄**：曲線顏色跟著剩餘量變化，橘、紅區是偏低與告急，斜線是工具沒開著的時段。

![使用紀錄](docs/screenshots/history.png)

**額度詳情**：每個額度的重置時間、使用速度、照目前速度會不會用完、每小時可用多少；資料來源預設收起。

![額度詳情](docs/screenshots/details.png)

**設定**

![設定](docs/screenshots/settings.png)

## 安裝、更新與移除

| 項目 | 說明 |
| --- | --- |
| 版本 | 0.1.0（第一版） |
| 安裝 | 解壓縮 `ClaudeUsageMonitor-v0.1.0-win-x64.zip` 到固定資料夾（例如 `D:\工具\ClaudeUsageMonitor`），雙擊 `ClaudeUsageMonitor.exe`。可對照同名 `.sha256` 檔核對 SHA-256。 |
| 開機自動啟動 | 設定 →「登入 Windows 時自動啟動」（寫入目前使用者的啟動項目，不需要系統管理員）。 |
| 更新 | 先從選單「離開」，用新版 exe 覆蓋舊檔再開啟。設定與紀錄存在 `%LOCALAPPDATA%\ClaudeUsageMonitor`，不在 exe 旁邊，更新不會動到。 |
| 移除 | 先在設定取消開機自動啟動、從選單「離開」，再刪除程式資料夾；要連紀錄一起刪就刪 `%LOCALAPPDATA%\ClaudeUsageMonitor`。 |

## 怎麼用

1. 解壓縮後雙擊 `ClaudeUsageMonitor.exe`（不需要另裝 .NET，也不需要系統管理員權限）。
2. 浮窗預設出現在螢幕右下角，約每 2 分鐘更新一次（設定可改 60～900 秒）。額度連續沒有變化時會自動放慢（最多 10 分鐘一次），鎖定螢幕時 15 分鐘一次；一偵測到 Claude Code 有動作、解鎖或從睡眠醒來就立即更新。
3. 在浮窗或右下角系統匣圖示上**按右鍵**開選單：立即更新、切換精簡／標準、使用紀錄、額度詳情、設定、官方用量頁、開啟資料夾、離開。
4. 浮窗可拖曳；雙擊開額度詳情；右下角的 ⌃／⌄ 切換標準與精簡模式；點系統匣圖示可顯示／隱藏浮窗。
5. 要真正停止記錄，請從選單選「離開」。

每個額度是一塊彩色卡片，**不用讀數字也看得出狀況**：

| 卡片顏色 | 標籤 | 意思 |
| --- | --- | --- |
| 綠 | 充足 | 剩一半以上 |
| 紫 | 尚可 | 剩 50% 以下 |
| 橘 | 偏低 | 剩餘低於「先提醒」門檻（預設 20%） |
| 紅 | 告急／用完 | 剩餘低於「再提醒」門檻（預設 5%），告急時卡片會緩慢閃動 |

| 畫面上的字 | 意思 |
| --- | --- |
| 圓環與百分比 | **剩餘**額度（圓環越滿剩越多），不是已使用比例。 |
| `2時12分後重置` | 官方回報的重置時間倒數。 |
| `每小時 −1.0%` | 依工具開著時的觀察推估的使用速度。 |
| 底部提示條 | 一句話結論，例如「照目前速度，本週額度 明天 09:27 用完，早於重置」或「額度撐得到重置」。 |
| `速度觀察中…` | 觀察時間太短：需要連續觀察 15 分鐘以上且用掉 2 個百分點，或連續觀察 1 小時。 |
| 右側小卡 | 各模型的每週額度（例如 Fable）。 |
| 標題列圓點 | 綠：剛更新；珊瑚色光暈：查詢中；黃：資料過時；紅：查詢失敗。 |
| 系統匣數字 | 5 小時與本週兩者中**較少**的剩餘百分比，底色同卡片顏色。 |

小動畫（遵循 Windows「動畫效果」設定，關閉時全部停用；沒有動作時不重繪、不耗 CPU）：開啟時圓環從 0 轉到目前值；查詢中星芒旋轉、卡片有掃光；每次更新完星芒彈一下、狀態燈泛出漣漪；額度減少時浮出「−1%」；額度重置時噴出彩色紙屑；滑鼠移到卡片上會浮起，點一下有水波紋；紀錄圖表的曲線會畫出來。

額度詳情視窗的「資料來源與紀錄位置」預設收起，需要時點開。

**提醒**：剩餘低於設定門檻（預設 20%、5%）時，每個重置週期各通知一次；曾經偏低的額度恢復時也會通知。

## 它怎麼取得額度

本工具不讀登入檔、不直接呼叫 Anthropic 的伺服器。每次更新時它會：

1. 找到電腦上的 `claude.exe`，確認它有 **Anthropic, PBC 的有效數位簽章**（沒有就拒絕執行）。
2. 以 Agent SDK 的串流模式短暫啟動它，只送兩個唯讀的控制請求：`initialize`（取得登入帳號與方案）與 `get_usage`（取得方案額度，也就是 Claude Code 裡 `/usage` 的同一份資料），查完立即結束。
3. 啟動時不載入使用者／專案設定（不會跑 hooks、外掛、MCP 伺服器）、不寫入對話紀錄（`--no-session-persistence`），也**不送出任何提問，所以不耗用模型額度**。

每次查詢約 1.5 秒，查完子程序就結束，不常駐。

## 資料與隱私

- 紀錄存在 `%LOCALAPPDATA%\ClaudeUsageMonitor`：`settings.json`（設定）、`history\*.jsonl`（每次觀察的百分比與重置時間）、`last_good.json`、`status.json`（給其他程式讀的目前狀態）、`alerts.json`、`logs\`（只記錯誤代碼）。預設保留 90 天。
- 帳號只以「本機隨機鹽＋email＋組織」的雜湊值區分，**不保存 email**。Claude Code 的錯誤輸出一律丟棄，不寫入紀錄。
- 本工具本身不連網、沒有遙測、不上傳任何資料。連網的是官方 Claude Code（照它平常的方式連到 Anthropic）。
- 為了在你開始使用時立刻更新，工具會監看 `~/.claude/projects` 資料夾**有沒有檔案變動**；只用來觸發更新，不讀取任何對話內容。
- 已知副作用：每次查詢時，Claude Code 會更新它自己 `~/.claude.json` 裡的功能旗標快取時間（`cachedGrowthBookFeaturesAt`），這和平常每次啟動 Claude Code 一樣。
- CSV 匯出含使用時間，分享前請先檢查。

## 限制

- `get_usage` 是 Claude Code 標示為「實驗性」的介面，日後 Claude Code 改版可能改變格式；改變時工具會顯示「回應無法辨識」而不是顯示錯的數字。
- 燒速與「何時用完」是觀察推估，不是官方數字；關機、休眠或工具關閉期間不會補資料（圖上以斜線標示「未監測」）。
- 只支援原生 `claude.exe`；如果 Claude Code 是用 npm 安裝的 Node 版本、沒有 `claude.exe`，請在設定指定 `claude.exe` 位置或改用官方安裝程式。

## 給開發者

```powershell
# 需要 .NET 10 SDK
dotnet build src\ClaudeUsageMonitor.csproj
.\build.ps1                     # 發佈自帶執行環境的單一 exe，產生 dist\ClaudeUsageMonitor-v<版本>-win-x64.zip 與 .sha256
```

| 參數 | 用途 |
| --- | --- |
| `--demo` | 用合成資料執行（資料放在暫存資料夾），不需要 Claude 帳號 |
| `--probe <檔案.json>` | 實際查詢一次，把結果寫到檔案後結束（排錯用，不寫入紀錄） |
| `--screenshot <資料夾>` | 用示範資料把每個視窗輸出成 PNG 後結束 |
| 環境變數 `CLAUDE_USAGE_MONITOR_HOME` | 改變資料資料夾位置（測試用） |

程式結構：`ClaudeUsageClient`（啟動 claude.exe、控制請求）→ `UsageParser`（解析）→ `MonitorService`（排程、紀錄、提醒）→ `OverlayForm`／`HistoryForm`／`DetailsForm`／`SettingsForm`（畫面）。`tools\make_icon.py` 產生程式圖示。
