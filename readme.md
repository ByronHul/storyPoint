# Azure 多人參與 Story Point 估算與工項權重管理系統 (readme.md)

## 1. 系統簡介
本系統為基於 **Azure PaaS (`storyPoint` 資源群組)**、**SQL Server / Azure SQL Database**、**PowerShell** 與 **.NET 8 (ASP.NET Core + SignalR)** 建置之企業級敏捷 Story Point 估算與部門/員工工項點數管理平台。

### 核心功能特色
1. **多人即時盲投與翻牌 (Planning Poker)**：
   - 支援針對「**工項的代表類型 (`storyPoint.tbWorkItemType`)**」或「**個別實際工項 (`storyPoint.tbWorkItem`)**」發起多人多回合盲投。
   - 出牌階段隱藏他人點數，管理者可控制「翻牌揭曉」、「開啟下一回合重新投票」與「拍板核定最終點數」。
2. **建立工項自動帶入代表類型 Story Point 或手動調整**：
   - 建立工項時選擇已賦予 Story Point 的「工項代表類型」，系統自動帶入該類型的標準 Story Point；亦允許管理者依個案複雜度手動調整點數並留存異動歷程。
3. **管理者修改員工賦予的工項與分攤配額**：
   - 支援單人負責或多人依比例 (`allocationPercent`) 共同分攤工項 Story Point，工項點數異動時自動重算員工與部門點數。
4. **事後查詢訂出 Story Point 的完整過程**：
   - 完整保留並公開查詢每一場投票議程、每一回合每位參與者投出的點數、平均值、最高/最低值、估算理由、回合討論摘要與最終核定說明。
5. **查詢各工項、員工個人與部門總 Story Point 數**：
   - 即時統計每位員工身上的工項清單與總 Story Point 數 (`storyPoint.vwEmployeePointSummary`)，以及員工所屬部門的總 Story Point 數與人均負載 (`storyPoint.vwDepartmentPointSummary`)。
6. **存放員工列表與每個人的登入帳密**：
   - 內建部門資料表 (`storyPoint.tbDepartment`) 與員工帳密表 (`storyPoint.tbEmployee`)，區分「管理者 (`admin`)」與「參與者 (`participant`)」權限，並支援 CSV 匯入與匯出。

---

## 2. 專案目錄結構
```text
c:\temp\storyPoint\
├── .gitignore                        # Git 忽略規則 (排除暫存檔、Log、報表與敏感設定)
├── config.json                       # 系統主設定檔 (含資料庫、Azure RG storyPoint、Log 層級設定)
├── requirement.md                    # 系統完整需求規格書
├── readme.md                         # 系統說明與操作手冊
├── config\
│   └── config.template.json          # 設定檔範本
├── sql\
│   ├── schemaAndTable.sql            # 專屬 storyPoint Schema 與 12 張資料表 (含無損資料保留機制)
│   ├── viewAndFunction.sql           # 撲克牌數值解析函數與 4 張核心統計檢視表 (vw*)
│   ├── storedProcedure.sql           # 13 支標準預存程序 (sp*，全小寫關鍵字、try...catch、spWriteLog)
│   └── seedData.sql                  # 預設部門、管理員/參與者帳號、牌組、代表類型、工項與示範投票過程
├── ps\
│   ├── common.ps1                    # 共用核心函數 (DPAPI 加解密、Retry 重試、雙軌 DB/CSV Log、sp_executesql)
│   ├── setEncryptedConfig.ps1        # 正式環境 SQL 連線字串加密設定工具
│   ├── initDatabase.ps1              # 資料庫與 T-SQL 自動化初始化部署腳本
│   ├── manageEmployee.ps1            # 部門、員工列表與登入帳密管理 Console 模組
│   ├── manageWorkItem.ps1            # 工項代表類型、實際工項與員工賦予設定 Console 模組
│   ├── manageVotingSession.ps1       # 多人 Story Point 盲投、翻牌與過程查詢 Console 模組
│   ├── getStoryPointReport.ps1       # 員工/部門總 Story Point 統計與 BCP 相容 CSV 匯出模組
│   ├── deployAzure.ps1               # Azure 資源群組 (storyPoint)、Azure SQL 與 App Service 自動部署腳本
│   └── ui\
│       ├── uiMain.ps1                # PowerShell 多分頁 (Multi-tab) 圖形化管理主視窗 (支援 full / simplified)
│       ├── tabVoting.ps1             # [頁籤 1] 多人盲投與訂定過程查詢
│       ├── tabWorkItemType.ps1       # [頁籤 2] 工項代表類型設定
│       ├── tabWorkItem.ps1           # [頁籤 3] 工項建立與員工賦予
│       ├── tabEmployee.ps1           # [頁籤 4] 員工帳密與部門管理
│       └── tabReport.ps1             # [頁籤 5] 員工與部門總點數統計報表
└── web\
    └── storyPointPortal\             # ASP.NET Core 8 MVC + SignalR 網站專案
```

---

## 3. 快速啟動與驗證指令

### 3.1 初始化資料庫與 T-SQL 物件
```powershell
pwsh -NoProfile -File c:\temp\storyPoint\ps\initDatabase.ps1
```

### 3.2 啟動 PowerShell 多 Tab 圖形化管理介面
```powershell
pwsh -NoProfile -File c:\temp\storyPoint\ps\ui\uiMain.ps1 -uiMode full
```

### 3.3 啟動 .NET 8 Web 多人投票與管理系統
```powershell
dotnet run --project c:\temp\storyPoint\web\storyPointPortal\storyPointPortal.csproj --urls "http://localhost:5088"
```
預設測試帳號：
- **管理者 (`admin`)**：帳號 `admin` / 密碼 `admin123`（或 `alex.chen` / `admin123`）
- **參與者 (`participant`)**：帳號 `kevin.wang`、`emily.lee`、`david.chang`、`grace.lin` / 密碼皆為 `pass123`
