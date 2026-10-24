# Azure 多人參與 Story Point 估算與工項權重管理系統需求規格書 (requirement.md)

## 1. 系統核心目標
建立一套基於 **Azure PaaS** 與微軟技術堆疊（**SQL Server / Azure SQL Database**、**PowerShell**、**.NET 8**、**SignalR**）的「多人參與 Story Point 估算與工項權重管理系統」。
系統支援針對「工項代表類型 (`tbWorkItemType`)」或「個別特殊工項 (`tbWorkItem`)」進行多人即時盲投（Planning Poker），建立工項時可直接選擇已賦予 Story Point 的工項代表類型自動帶入點數（或由管理者手動微調），並支援管理者將工項賦予員工（含單人或多人分攤配額），事後可完整查詢每次訂出 Story Point 的多回合投票與決策過程、各工項與代表類型的 Story Point、每位員工身上的總 Story Point 數，以及各部門的總 Story Point 數。

---

## 2. 功能需求規格 (与 Agent Manager 訪談確認之完整需求)

### 2.1 部門與員工帳號管理 (Department & Employee Management)
* **獨立部門管理 (`storyPoint.tbDepartment`)**：
    * 存放部門代碼 (`departmentCode`)、部門名稱 (`departmentName`)、上層部門主鍵 (`parentDepartmentPk`，支援階層或平扁結構)、部門主管或備註、啟用狀態 (`isActive`)。
* **員工列表與登入帳密管理 (`storyPoint.tbEmployee`)**：
    * 存放員工編號 (`employeeCode`)、姓名 (`employeeName`)、登入帳號 (`loginAccount`)、密碼雜湊 (`passwordHash`，採用安全雜湊儲存)、電子郵件 (`emailAddress`)、職稱 (`jobTitle`)、所屬部門 (`departmentPk`)、系統角色 (`roleName`：`admin` 管理者 / `participant` 參與者)、在職/啟用狀態 (`isActive`)。
    * **完整支援員工帳號「增、刪、修、查與啟用/停用」功能**：
        1. **新增 (Create) 與修改 (Update)**：支援於管理表單新增員工帳號、透過表單右上角「⚡ 快速選擇既有帳號修改密碼」下拉選單、直接輸入既有 `loginAccount` 自動對應載入、或點擊列表「✏️ 修改」載入既有員工編號、姓名、登入帳號、重設密碼（含 `👁️ 顯示 / 🙈 隱藏` 切換）、調整部門/職稱/角色與啟用/停用狀態 (`storyPoint.spUpsertEmployee`)。`spUpsertEmployee` 優先依 `employeePk` 與 `loginAccount` 定位帳號並自動防止 `employeeCode` 唯一鍵衝突，確保修改 `admin` 等既有帳號密碼時 100% 寫入生效。
        2. **停用与啟用切換 (Disable / Enable)**：於員工列表提供明確的「🟢 啟用中 / 🚫 已停用」狀態徽章與一鍵「🚫 停用 / ✅ 啟用」按鈕 (`storyPoint.spSetEmployeeActiveStatus`)，停用後該帳號無法登入系統。
        3. **刪除 (Delete)**：於員工列表提供「🗑️ 刪除」按鈕 (`storyPoint.spDeleteEmployee`)，安全清理該員工之工項指派與投票關聯紀錄並刪除員工帳號（內建保護機制避免誤刪當前登入之管理者本人）。
        4. **多維度查詢篩選 (Search & Filter)**：於員工列表上方提供「關鍵字搜尋（員工編號/姓名/登入帳號/信箱）」、「依所屬部門篩選」、「依系統角色篩選（管理者/參與者）」與「依帳號狀態篩選（全部/啟用中/已停用）」即時查詢功能。
        5. **網頁與 PowerShell 批次載入員工資料 (Batch Import Employees)**：
            * 於 Web 管理後台「組織與員工帳密管理」提供「**📤 批次載入員工資料 (支援上傳 CSV 檔案或直接貼上 CSV / Excel 文字)**」面板。
            * 支援欄位：`員工編號 (employeeCode),姓名 (employeeName),登入帳號 (loginAccount),登入密碼 (plainPassword),電子郵件 (emailAddress),職稱 (jobTitle),部門代碼或部門名稱 (department),系統角色 (roleName),是否啟用 (isActive)`。
            * 若批次載入之員工所填寫的部門名稱或部門代碼尚不存在於 `storyPoint.tbDepartment`，系統會自動建立該部門並完成關聯指派；同時提供「📄 下載標準 CSV 批次載入範本 (`/employee/import/template`)」與「⚡ 一鍵帶入範例批次名單」功能。
    * 支援 **CSV / Excel 批次匯入與匯出員工名單**。
* **強制員工帳號密碼登入驗證頁（嚴禁未登入直接進入系統）**：
    1. **Web 入口強制導向帳號登入頁 (`/login`)**：使用者開啟網站根網址 (`/`) 或任何未授權頁面時，一律強制進入「員工帳號登入驗證頁 (`/login`)」，並自動清除任何舊瀏覽器殘留 Cookie，絕不允許未經登入直接進入系統儀表板。
    2. **嚴禁預設填入帳密或提供一鍵快速登入按鈕**：登入頁之「員工登入帳號 (`loginAccount`)」與「登入密碼 (`plainPassword`)」欄位預設一律空白（`value=""`），且移除任何一鍵快速帶入測試帳密之按鈕，強制使用者手動輸入有效之員工帳號與密碼並通過 `storyPoint.spVerifyEmployeeLogin` 驗證後方可登入。
    3. **禁止冒用他人身分投票**：投票出牌時強制綁定當前登入之員工本人 (`getCurrentEmployeePk()`)，不開放於畫面上直接切換他人身分代投。
    4. **PowerShell GUI (`ps/ui/uiMain.ps1`) 強制帳號登入驗證視窗**：啟動圖形化管理介面時，必須先通過「員工帳號密碼登入驗證對話框 (`showEmployeeLoginDialog`)」檢核，驗證通過後方可開啟主視窗。

### 2.2 角色權限劃分与資料隔離 (Role-Based Access Control & Data Isolation)
* **管理者 (`admin`)**：
    * 維護部門清單、員工列表與登入帳密（增、刪、修、查、停用與批次載入）。
    * 維護撲克牌組設定 (`storyPoint.tbCardDeck`) 與工項代表類型 (`storyPoint.tbWorkItemType`)。
    * 建立、修改、刪除實際工項 (`storyPoint.tbWorkItem`)，選擇工項代表類型自動帶入 Story Point 或手動調整點數。
    * 修改員工賦予的工項 (`storyPoint.tbWorkItemAssignment`)，設定工項負責人與點數配額/分攤比例，以及設定可參與投票的人員。
    * 發起、主持多人 Story Point 投票議程 (`storyPoint.tbVotingSession`)，控制「翻牌揭曉 (Reveal)」、「開啟下一回合重新投票 (Next Round)」與「拍板核定最終 Story Point (Finalize)」。
    * **查詢全公司資料**：可查詢所有投票過程具名紀錄、所有工項點數、全體員工個人與各部門總 Story Point 統計報表。
* **參與者 (`participant` - 個別一般員工資料隔離規範)**：
    * 使用個人帳號密碼登入系統。
    * **可以參與投票**：參與進行中的 Story Point 投票議程（針對工項代表類型或指定工項進行出牌、填寫自己的估算理由），以及查看「自己的出牌紀錄」與議程整體最終定案點數，但**不可看到其他員工個別投出的點數或留言明細**。
    * **只可看到自己的工項與自己的分數（嚴格禁止查看他人資料）**：
        * 在「工項查詢」中，個別員工**僅能看到賦予給自己的工項與自己的分配點數 (`allocatedStoryPoint`)**，不可看到未賦予給自己的其他員工工項，亦不可看到同工項其他共同負責人的名字與配額。
        * 在「點數統計」中，個別員工**僅能看到自己個人的工項數與 Story Point 統計**，不可看到其他員工的工項與分數，亦不可存取全公司員工列表或他人帳密。
        * **每個人皆可修改自己的登入帳號與密碼**：不論是管理者 (`admin`) 或參與者 (`participant`)，每個人皆可隨時透過頂部工具列「`🔑 修改我的帳密`」按鈕、主選單「`🔑 個人帳密設定`」或 PowerShell 模組修改**自己本人的登入帳號 (`loginAccount`) 與登入密碼 (`passwordHash`)**（`storyPoint.spUpdateEmployeeSelfCredential`），但絕不可修改其他員工的帳密。
        * 匯出 CSV 報表時，個別員工僅能匯出屬於自己的工項與個人點數資料。

### 2.3 工項代表類型與實際工項管理 (Work Item Type & Work Item)
* **工項代表類型 (`storyPoint.tbWorkItemType`)**：
    * 投票時主要針對「工項的代表類型」進行估算與定案（例如：「標準單表 CRUD API」、「複雜跨系統報表」、「前端互動表單頁面」、「批次排程腳本」等）。
    * 包含類型代碼 (`typeCode`)、類型名稱 (`typeName`)、分類 (`categoryName`)、複雜度與驗收標準說明 (`description`)、標準 Story Point (`standardStoryPoint`)、估算狀態 (`estimationStatus`：`pending` 待估算 / `voting` 投票中 / `finalized` 已定案)。
* **建立實際工項自動帶入點數或手動調整 (`storyPoint.tbWorkItem`)**：
    * 建立工項時，可選擇已經賦予 Story Point 的「工項代表類型 (`workItemTypePk`)」，系統即**自動帶入該類型的標準 Story Point** 至工項的 `storyPoint` 欄位。
    * 管理者亦可依個案複雜度**手動調整（覆寫）**該工項的 Story Point（系統記錄 `defaultTypePoint` 與 `isManualAdjusted` 標記，並將調整原因寫入點數變更歷程 `storyPoint.tbPointHistory`），或針對特殊工項單獨發起多人投票。
    * 若某個「工項代表類型」事後重新投票更新了標準 Story Point，預設僅影響未來新建工項，同時提供管理者勾選**「是否一併同步更新目前尚未完成（且未曾手動微調過點數）的既有工項點數」**，並完整記錄點數變更歷程。

### 2.4 多人參與 Story Point 盲投與過程追溯 (Multi-Round Planning Poker & Audit Trail)
* **可自訂撲克牌組 (`storyPoint.tbCardDeck`) 與牌面意義直接標示**：
    * 預設內建「標準費氏數列 (`0, 1, 2, 3, 5, 8, 13, 21, ?, ☕`)」與「修正費氏數列 (`0, 0.5, 1, 2, 3, 5, 8, 13, 20, 40, 100, ?, ☕`)」，資料庫欄位 (`cardValueCsv`、`cardValue`) 一律採用 `nvarchar` 確保 Unicode 特殊符號 (`☕`) 完整保存不變問號，並允許管理者自訂新增牌組。
    * **牌面除了顯示符號與點數，必須直接標示代表意義**：在「設定盲投回合即時預覽」與「多人即時盲投出牌桌面」的每一張撲克牌面上，除了顯示數字或符號外，下方皆直接標示繁體中文意義：
        * **`?`（疑問牌）**：牌面顯示 `?` 與 **「需求不明 / 需討論」**（代表對需求範圍或技術細節有疑問、資訊不足無法估算，需先補充討論）。
        * **`☕`（休息牌）**：牌面顯示 `☕` 與 **「暫停休息 / 稍後議」**（代表會議時間過長或資訊過載，建議中場休息或稍後再議）。
        * **數值牌面 (`0` ~ `100`)**：牌面除顯示點數外，同步標示複雜度意義（`0: 無需工時`、`0.5: 極小微調`、`1: 極簡變更`、`2: 小型工項`、`3: 標準一般`、`5: 中等複雜`、`8: 高複雜度`、`13: 大型跨模組`、`20/21: 極大工項`、`40: 巨型需拆解`、`100: 史詩級拆解`）。
* **多回合即時盲投機制 (`storyPoint.tbVotingSession` / `tbVotingRound` / `tbVoteRecord`)**：
    * 支援透過 **ASP.NET Core SignalR**（以及 AJAX 輪詢雙軌備援）實現多人即時同步互動。
    * **出牌階段 (`voting`)**：參與者針對「工項代表類型」或「特定工項」點選撲克牌出牌並可填寫簡短理由；翻牌前所有人的牌面皆為隱藏（僅顯示「已出牌 ✓」或「思考中...」），避免錨定效應。
    * **翻牌揭曉階段 (`revealed`)**：當全數出牌或管理者按下「翻牌揭曉」時，畫面同步翻開所有參與者的撲克牌，並自動計算本回合統計指標（平均點數、最高點、最低點、眾數、共識度指標）。
    * **多回合討論與重新投票**：若點數差距過大（例如有人投 2、有人投 13），最高與最低分者可說明理由，管理者可記錄回合討論摘要並按下「開啟下一回合投票」，進入第 2、3... 回合重新出牌。
    * **最終拍板定案 (`finalized`)**：達成共識後，由管理者輸入或選擇最終 Story Point 及核定說明，自動更新至該「工項代表類型」或「工項」，並寫入點數歷程表。
* **事後查詢訂出 Story Point 的完整過程**：
    * 任何時間皆可查詢每一場已完成或進行中的投票議程，完整檢視：估算目標（工項代表類型或工項）、主持管理者、參與人數、總回合數、**每一回合每位員工投出的點數與留言備註**、各回合統計數據、回合討論紀錄，以及最終拍板定案的點數與理由。

### 2.5 員工工項賦予與員工/部門 Story Point 統計查詢 (Assignment & Analytics)
* **管理者修改員工賦予的工項 (`storyPoint.tbWorkItemAssignment`)**：
    * 管理者可將工項指派給 1 位或多位員工負責，並可隨時新增、修改、移轉或移除員工身上的工項。
    * 支援設定每位負責員工在該工項的**分攤比例 (`allocationPercent`，預設單人 100%，多人可平分或自訂比例)** 與**實際分配到的 Story Point (`allocatedStoryPoint`)**（亦支援切換檢視「分配點數總和」與「參與工項原始總點數」）。
    * 當工項本身的 Story Point 因手動調整或類型同步而變動時，系統會依分攤比例自動重新計算並更新每位負責員工身上的 `allocatedStoryPoint`。
* **多維度查詢與報表匯出**：
    1. **各工項與代表類型的 Story Point 查詢**：可依工項代表類型、執行狀態、所屬部門、負責員工、關鍵字查詢所有工項的點數、來源（自動帶入或手動微調）與指派明細。
    2. **員工身上的 Story Point 統計查詢**：查詢每位員工目前被賦予的工項清單、進行中點數、已完成點數、個人總 Story Point 數（含分配點數與原始工項點數）。
    3. **部門總 Story Point 統計查詢**：依部門 (`tbDepartment`) 彙總該部門在職員工數、部門總指派工項數、部門總 Story Point 數（含待處理、進行中、已完成之部門點數拆解與人均 Story Point 負載）。
    4. **投票過程歷史查詢**：依工項代表類型或工項查詢歷次投票議程、各回合出牌明細與共識收斂過程。
    5. **報表匯出**：支援匯出繁體中文 CSV（可由 `bcp.exe` 載入之標準格式）與視覺化報表。

### 2.6 投票議程清單 Folder 分類展開收合與盲投回合開始前設定 (Agenda Folder Collapse & Pre-Round Setup)
* **投票議程清單分 Folder 展開收合與邊欄/邊項本身展開收合**：
    1. **分 Folder 展開收合**：於多人即時盲投大廳左側「📌 投票議程清單」中，自動將議程分為兩個可獨立展開與收合的資料夾（Folder）：
        * **📂 投票中與討論中議程 (`voting` / `revealed`)**：顯示目前進行中的盲投或翻牌討論中議程及場數徽章。
        * **📁 已拍板定案議程 (`finalized`)**：顯示已完成拍板定案的歷史議程及場數徽章。
    2. **投票議程邊欄本身可展開收合**：
        * 支援**水平收合/展開邊欄 (`◀ 縮起邊欄` / `▶ 展開投票議程清單`)**，收合後左側縮為垂直窄條，讓右側撲克牌出牌桌面放大為全寬檢視。
        * 支援**垂直收合/展開清單 (`▴ 收合清單` / `▾ 展開清單`)**，以及一鍵「➕ 全展開 / ➖ 全收合」所有 Folder 與邊項。
    3. **每個投票議程邊項本身亦可展開收合 (`▾ 邊項詳情` / `▴ 收合詳情`)**：
        * 每筆議程項目卡片除了點擊切換桌面外，邊項本身提供展開/收合按鈕，展開後直接呈現該回合之「估算標的」、「點數上下限範圍」、「共識計算規則」、「誰可參與（指定名單或全體人數）」、「回合重點說明」，以及快速「⚙️ 修改回合設定」按鈕。
* **多人盲投回合開始前「⚙️ 設定回合」主選單與完整設定選項**：
    1. **主選單入口**：於頂部主選單列 (`top-menubar`) 新增獨立主選單按鈕「**⚙️ 設定回合 (盲投開始前設定)**」，同時於「🃏 多人估算與過程」下拉選單內提供「**1-2. 設定盲投回合 (投什麼 / 誰可參與 / 點數範圍與規則)**」。支援發起全新盲投議程，或載入既有進行中議程動態調整回合設定。
    2. **投什麼 (What to vote on)**：
        * 選擇估算標的類型：`workItemType`（投工項的代表類型，定案後供工項自動帶入標準點數）或 `workItem`（投個別特定工項）。
        * 選擇目標代表類型或特定工項、設定盲投議程標題 (`sessionTitle`)，以及填寫「**本回合估算範圍與驗收重點說明 (`roundGoalNote`)**」顯示於出牌桌面。
    3. **最高與最低的 Story Point (Min & Max Story Point)**：
        * 設定本回合允許投出的**最低 Story Point (`minStoryPoint`)** 與 **最高 Story Point (`maxStoryPoint`)**、選擇撲克牌組 (`cardDeckPk`)，以及是否開放「**?** (疑問牌，`allowQuestionCard`)」與「**☕** (休息牌，`allowCoffeeCard`)」。
        * 提供**即時預覽可用撲克牌面**，並在盲投出牌桌面與後端 `storyPoint.spCastVote` 強制驗證出牌範圍，超出上下限的牌面自動標示為「範圍外」禁止誤投。
    4. **誰可以參與 (Who can participate)**：
        * 設定參與權限控管模式 (`restrictToParticipant`)：「🔒 僅限勾選的受邀參與者可以出牌投票」或「🌐 開放全體啟用中員工皆可出牌」。
        * 提供互動式員工勾選清單 (`participantPks` 寫入 `storyPoint.tbVotingParticipant`)，並支援「✅ 全選所有啟用員工」、「⬜ 清空全部勾選」、「🏢 依部門快速批次勾選參與者」與「🎯 僅勾選該工項已指派負責員工」。
    5. **建議的敏捷盲投回合相關進階設定選項**：
        * **🧮 共識點數自動計算規則 (`consensusRule`)**：支援 `nearestFibonacci`（對齊最接近費氏牌面值，推薦預設）、`average`（算術平均）、`median`（中位數）、`mode`（眾數）、`trimmedMean`（去頭去尾平均數）、`maxConservative`（保守取較高值），翻牌後自動算出建議定案點數並帶入核定欄位。
        * **⚠️ 共識分歧警示門檻 (`maxAllowedSpread`)**：設定最高分與最低分的最大允許落差（例如 `5 pt`）；翻牌揭曉時若落差超過門檻，系統自動顯示紅字警示建議請極端值成員說明後開啟下一回合重新盲投。
        * **⏱️ 單回合思考倒數計時限制 (`roundTimeLimitSecond`)**：支援 `60秒 / 120秒 / 180秒 / 300秒 / 不限時間`，於盲投桌面顯示即時倒數計時器。
        * **💬 出牌估算理由填寫規範 (`voteCommentPolicy`)**：支援 `optional`（選填）、`requiredExtreme`（投出最高/最低極端值或 `?` 疑問牌時必填理由）、`requiredAll`（全員出牌必填理由）。
        * **⚡ 全員完成出牌後自動翻牌揭曉 (`autoRevealWhenAllVoted`)**：當所有受邀參與者皆完成出牌時，由 `storyPoint.spCastVote` 自動將該回合與議程切換為 `revealed`（已翻牌揭曉）並計算統計值。
        * **⚡ 快速範本套用**：內建「標準 Scrum 範本」、「快速閃電估算範本」、「嚴謹架構評審範本」一鍵套用按鈕。

### 2.7 多格式批次匯入 (CSV / Excel) 與互動式報表匯出 (CSV / Excel / 互動 HTML)
* **匯入提供兩種選項 (CSV、Excel)**：
    1. **員工與登入帳密批次匯入 (`/employee/import`、`/employee/import/template?format=csv|excel`)**：
        * 支援選擇 **CSV (`.csv`)** 與 **Excel (`.xlsx` / `.xls` / `.xml`)** 兩種匯入選項，並提供 CSV 與 Excel (`.xlsx`) 兩種標準範本下載。
    2. **工項與 Story Point 批次匯入 (`/workitem/import`、`/workitem/import/template?format=csv|excel`)**：
        * 支援選擇 **CSV (`.csv`)** 與 **Excel (`.xlsx` / `.xls` / `.xml`)** 兩種匯入選項，自動對應工項代表類型、設定 Story Point、Sprint、執行狀態與負責員工指派，並提供 CSV 與 Excel (`.xlsx`) 兩種標準範本下載。
* **匯出提供三種選項 (CSV、Excel、HTML，且 HTML 具備排序、篩選、群組彙總功能)**：
    1. **統一匯出端點 (`/export/report?type=department|employee|workitem|history&format=csv|excel|html`)**：
        * **選項 1 - CSV (`.csv`)**：輸出含 UTF-8 with BOM 之標準 CSV 報表，支援 Excel 直接開啟與 `bcp.exe` 載入。
        * **選項 2 - Excel (`.xlsx`)**：輸出原生 OpenXML `.xlsx` 活頁簿（含標題列深藍底白字樣式與數值型態自動識別）。
        * **選項 3 - 互動式 HTML (`.html`)**：輸出可獨立離線開啟之繁體中文互動式分析報表，內建：
            * **欄位排序 (Sorting)**：點擊任一欄位表頭即可切換升冪 (`▲`) / 降冪 (`▼`) 排序（自動判別 Story Point 數值與文字/日期）。
            * **多條件即時篩選 (Filtering)**：支援全文關鍵字即時搜尋、指定欄位與欄位值下拉篩選、以及最小/最大 Story Point 點數範圍篩選與一鍵重設。
            * **動態群組彙總 (Group Aggregation)**：支援自由切換群組維度（如依所屬部門、代表類型、執行狀態、系統角色、異動來源等），自動計算並呈現「**群組彙總統計表**」（含各群組筆數、筆數佔比、Story Point 合計、點數佔比、平均點數、最小/最大點數），點擊群組列可快速篩選該群組，並於明細表提供可展開/收合之群組小計橫幅與頁尾總計列。

### 2.8 全域表格表頭排序與鎖定功能 (Universal Table Header Sorting & Locking)
* **凡是系統中任何 `<table>` 的表頭 (`<thead> / <th>`) 與匯出互動式 HTML 報表、PowerShell GUI 表格，一律提供「排序」與「鎖定」功能**：
    1. **表頭欄位排序功能 (`⇅` / `▲` / `▼`)**：
        * 點擊任一 `<th>` 表頭標題文字或排序圖示，即可切換該欄位的 **升冪 (`▲`)**、**降冪 (`▼`)** 與 **預設順序 (`⇅`)**。
        * 內建智慧型別判別：自動識別 Story Point 點數（如 `7.50 pt (2 件)`、`18.5 pt`）、件數/人數（`5 件`、`3 人`）、回合數（`第 2 回合`）、日期時間（`yyyy-MM-dd HH:mm:ss`）與繁體中文/自然英數編號（`EMP001`、`WI-2026-001`）。
        * 支援**多欄位排序與鎖定欄位優先排序**：當某欄位已被鎖定 (`🔒`) 且設定排序，或按住 `Shift` 點擊其他欄位時，支援多欄位階層排序。
    2. **表頭與欄位雙重鎖定功能 (`📌` / `🔒`)**：
        * **個別欄位鎖定 / 凍結 (`📌` ➔ `🔒`)**：每個 `<th>` 表頭右側皆提供獨立圖釘鎖定按鈕 (`📌` / `🔒`)。點擊鎖定後，該欄位（含表頭 `<th>` 與所有資料列 `<td>`）立即固定於表格左側（`position: sticky; left: ...px` 並標示深藍邊框與高亮底色），水平捲動時保持凍結不動，且同時鎖定其排序優先序；支援同時鎖定多個欄位自動累加計算偏移量。
        * **頂部表頭列垂直鎖定 (`🔒 表頭列已鎖定 Sticky Header`)**：所有表格預設啟用頂部表頭列 `position: sticky; top: 0` 垂直捲動鎖定，並於表格上方工具列 (`.sp-table-toolbar`) 提供一鍵切換「`🔒 表頭列已鎖定 (Sticky)` / `🔓 表頭列未鎖定`」與「`🔄 重設排序與解鎖`」按鈕，即時顯示目前排序欄位與已鎖定欄位名稱。

### 2.9 系統防崩潰保護與管理者系統/錯誤 Log 查詢中心 (Non-Crashing Error Guard & Admin Log Viewer)
* **發生錯誤絕對不能當掉 (Never Crash on Error)**：
    1. **三層式防崩潰保護架構**：
        * **第一層（SQL Server Stored Procedure `try...catch`）**：所有 Stored Procedure 內建 `begin try ... end try begin catch ... end catch`，發生錯誤時自動執行 `rollback transaction` 並呼叫 `storyPoint.spWriteLog` 將物件名稱、錯誤碼 (`error_number()`)、錯誤行號 (`error_line()`) 與錯誤訊息 (`error_message()`) 寫入 `storyPoint.tbLog`。
        * **第二層（資料服務與 Controller Action `try...catch` 攔截）**：`sqlDataService` 具備指數退避自動重試（Retry）與雙軌 Log 寫入（同時寫入 SQL Server `storyPoint.tbLog` 與本地 `logs/webPortal_yyyyMMdd.csv` + 記憶體 Ring Buffer）；`portalController` 所有 GET/POST 端點皆包裹 `try...catch`，發生異常時自動呼叫 `logPortalExceptionAsync` 並導回原頁籤顯示友善繁體中文提示與「🔍 前往系統與錯誤 Log 查詢中心檢視詳情」按鈕；若首頁資料庫載入異常則自動降級渲染「🛡️ 系統安全防護頁面（內嵌即時錯誤 Log 檢視表）」。
        * **第三層（ASP.NET Core 全域 Exception Guard Middleware）**：於 `Program.cs` 註冊全域例外攔截 Middleware，確保任何未預期的執行期例外皆被安全捕捉、寫入雙軌 Log 並導向友善防護畫面，**絕不出現瀏覽器原生 `HTTP ERROR 500` 當機畫面**。
* **管理者專屬「🛡️ 系統與錯誤 Log 查詢中心 (`/?tab=logs`)」**：
    1. **頂部主選單入口与即時錯誤計數徽章**：於管理者頂部導覽列新增「**🛡️ 系統與錯誤 Log 查詢**」主選單（即時顯示目前 `Level 1` 錯誤筆數徽章）與子選單。
    2. **多維度即時查詢與篩選 (`storyPoint.spGetSystemLog` / Result Set 8)**：
        * 顯示 KPI 指標卡（Log 總筆數、❌ 錯誤 Level 1 筆數、⚠️ 警告/資訊筆數、平均執行耗時 `ms`）。
        * 支援依「關鍵字（物件名稱/錯誤訊息/錯誤碼/帳號）」、「Log 嚴重層級（`1-錯誤`、`2-警告`、`3-資訊`、`4-除錯`）」、「來源模組 (`sourceName`)」與一鍵「❌ 只看錯誤異常」即時過濾，且表格表頭同樣具備排序與欄位鎖定功能。
    3. **管理者一鍵防崩潰與錯誤攔截模擬測試 (`/admin/log/testError`)**：
        * 提供三種測試情境（情境 A：Controller 執行期例外、情境 B：SQL Stored Procedure 參數異常、情境 C：全域 Middleware 未預期嚴重例外），讓管理者隨時驗證系統發生錯誤時不會當掉且完整寫入 Log。
    4. **系統 Log 三格式報表匯出 (`/export/report?type=log&format=csv|excel|html`)**：
        * 支援將系統與錯誤 Log 匯出為 **CSV**、**Excel (`.xlsx`)** 與 **互動式 HTML 報表（含排序、篩選、群組彙總與表頭鎖定）**。
    5. **系統 Log 清單 (`#sysLogDataTable`) 緊湊置頂排版與可摺疊 StackTrace 呈現**：
        * 所有資料儲存格 (`td`) 強制採用 `vertical-align: top`，並透過 `.syslog-row-err td` / `.syslog-row-warn td` 呈現錯誤紅底與警告黃底，避免超長錯誤訊息搭配 `vertical-align: middle` 導致前段欄位在可視區呈現大片空白。
        * 錯誤訊息若包含長篇 `StackTrace` 或超過 160 字，上方先顯示精簡錯誤摘要，完整堆疊明細收納於 `<details><summary>🔍 點擊展開 StackTrace 錯誤堆疊明細</summary>...</details>`，並限制 `執行物件 / 端點` 與 `執行結果` 欄寬自動換行，確保開啟 Log 查詢頁籤時表頭下方立即完整呈現多筆 Log 紀錄。
        * `storyPoint.tbLog.objectName` 與 `storyPoint.spWriteLog.@objectName` 採用 `nvarchar(128)`，確保繁體中文作業名稱寫入資料庫時不產生問號亂碼。

### 2.10 表單欄位單行精簡 Caption 對齊與滑鼠移入浮動 Tips 說明 (Single-Line Form Captions & Hover Floating Tooltips)
* **禁止欄位標題 (Caption / Label) 換成兩行導致水平輸入框不對齊**：
    1. **單行精簡 Caption 與嚴格水平對齊**：
        * 所有 `.form-row` 與 `.toolbar-grid` 採用 `align-items: end` 與 `min-width: 0`，且欄位 `<label>` 強制單行呈現（`white-space: nowrap; overflow: hidden; text-overflow: ellipsis; height: 20px; line-height: 20px;`），確保同一列的所有 `<input>`、`<select>` 與 `<button>` 輸入框 100% 水平對齊（例如管理者主持控制台之「本回合討論摘要備註」、「拍板定案點數」、「定案核定理由」等所有表單列）。
    2. **滑鼠移入時即時浮現完整項目說明 (`data-tip` + `#spFloatingTooltip`)**：
        * 將原本冗長的括號補充說明精簡為單行標題搭配 `ⓘ` 提示圖示（`.sp-tip-icon`），並透過 `data-tip` 與全域浮動提示元件 (`#spFloatingTooltip`)，於滑鼠移入（Hover）時立即浮現完整項目說明、計算依據與填寫規範。

### 2.11 預設示範資料清理暨集英信誠 (mentorTrust) 員工與部門同步規範
* **清理非必要示範資料，僅保留核心管理帳號與 HR 部門**：
    1. 清除所有預設示範之工項代表類型 (`storyPoint.tbWorkItemType`)、實際工項 (`storyPoint.tbWorkItem`)、工項指派 (`storyPoint.tbWorkItemAssignment`)、投票議程與回合出牌紀錄 (`storyPoint.tbVotingSession` / `tbVotingRound` / `tbVoteRecord` / `tbVotingSessionParticipant`) 及點數異動歷程 (`storyPoint.tbPointHistory`)。
    2. 員工帳號僅保留既有之 **`admin`** 與 **`raya`（許立慧，員工編號 `250201`）** 及其既有密碼雜湊設定；舊示範部門全數移除，僅保留 **`HR`** 部門。
* **參照 `phishing` 專案資料庫 (`phishingDefenseDb`) 匯入集英信誠 (`mentorTrust`) 部門與員工資料**：
    1. **同步新增集英信誠部門 (`storyPoint.tbDepartment`)**：除保留 `HR` 外，依 `phishing.tbRecipient` 中 `companyCode = 'mentorTrust'` 之部門結構新增 **`DB/INFra` (`DB_INFRA`)**、**`管理` (`MGMT`)**、**`BI` (`BI`)**、**`開發` (`DEV`)** 共 5 個有效部門。
    2. **同步新增集英信誠員工 (`storyPoint.tbEmployee`)**：將 `phishing.tbRecipient` 內屬於集英信誠 (`mentorTrust`) 之全體同仁（含員工編號、姓名、信箱、所屬部門、職稱與登入帳號）完整匯入 `storyPoint.tbEmployee`（總計 32 位啟用帳號：含保留於 `HR` 部門之 `admin` 與 `raya`，以及同步自集英信誠之 30 位同仁）。

### 2.12 全員完成投票即刻自動翻牌與全體畫面即時同步規範
* **全員投完票自動翻牌 (Automatic Card Reveal on All Voted)**：
    1. 投票議程 (`storyPoint.tbVotingSession`) 之 `autoRevealWhenAllVoted` 預設一律啟用 (`1` / `true`)。
    2. 在 `storyPoint.spCastVote`（以及 `storyPoint.spCreateVotingSession` 與 `storyPoint.spGetVotingProcessHistory` 自癒檢核）中，只要本回合所有有效必投參與者 (`storyPoint.tbVotingSessionParticipant.isRequiredVoter = 1` 且 `storyPoint.tbEmployee.isActive = 1`) 皆已完成出牌 (`@votedParticipantCount >= @requiredVoterCount`)，資料庫立即自動將當前 `storyPoint.tbVotingRound.roundStatus` 與 `storyPoint.tbVotingSession.sessionStatus` 轉換為 `'revealed'`（已翻牌揭曉）並寫入 `revealedAt` 時間戳記。
* **所有人畫面自動同步翻開撲克牌**：
    1. **盲投階段 (`sessionStatus = 'voting'`)**：撲克牌桌 (`voter-grid`) 與出牌明細中，除本人外其他已投票參與者一律顯示 `🎴 已出牌(盲投蓋牌)` 與 `(待全員投票後自動翻牌)`，維持盲投公平性。
    2. **全員投完翻牌後 (`sessionStatus in ('revealed', 'finalized')`)**：不論是管理者 (`admin`) 或一般參與者 (`participant`)，所有人的投票桌面 (`voter-grid`)、當回合統計橫幅（算術平均、最低分、最高分、分歧落差、共識規則建議點數）以及回合出牌紀錄皆同步翻開，顯示每位參與者投出的點數與估算理由（註：個人被指派工項與個人總 Story Point 統計仍維持個人隱私保護，僅本人與管理者可見）。
    3. **跨瀏覽器自動即時翻牌 (SignalR Push + Live Status Polling 雙重保障)**：當最後一位參與者送出選票觸發自動翻牌時，系統同時透過 SignalR (`onVotingSessionUpdated` 之 `autoRevealed` 事件) 與前端每 2 秒輕量狀態輪詢 (`GET /voting/liveStatus`) 偵測狀態變更，無須使用者手動點擊按鈕，所有已開啟之參與者與管理者畫面皆自動刷新並翻開撲克牌。
* **新投票議程自動更新、手動重新整理 (Refresh) 與已拍板定案議程預設收合**：
    1. **新議程自動偵測與自動切換呈現**：當有新的屬於該成員的投票議程建立（或既有議程新增該成員至參與名單）時，前端透過 SignalR (`sessionCreated` / `roundSettingUpdated`) 與 `GET /voting/liveStatus` 全議程清單比對 (`allSessionPks` / `sessionListSignature`) 自動偵測，立即自動更新並切換畫面以呈現新的投票議程。
    2. **提供手動重新整理 (`🔄 重新整理`) 按鈕**：於左側「📌 投票議程清單」標題列與右側投票議程桌面提供「`🔄 重新整理` / `🔄 重新整理議程`」按鈕 (`refreshVotingSessionsNow`)，點擊後立即向後端查詢並重新載入屬於該成員的最新投票議程清單與桌面。
    3. **「📁 已拍板定案議程」預設一律收合**：左側投票議程清單中之「`📁 已拍板定案議程`」資料夾 (`#folder-finalized`) 及其內部邊項詳情預設一律收合 (`collapsed`)，讓畫面聚焦於「`📂 投票中與討論中議程`」。
* **盲投回合設定外鍵完整性防呆與零代表類型/零工項時自動建立估算標的機制**：
    1. **前端下拉選單支援自動建立新代表類型/新工項**：於「`⚙️ 1-2. 設定盲投回合`」頁面中，「目標工項代表類型 (`#setupWorkItemTypePk`)」與「目標特定工項 (`#setupWorkItemPk`)」下拉選單除列出既有項目外，固定提供「`➕ 依下方「盲投議程標題」自動建立新工項代表類型`」與「`➕ 依下方「盲投議程標題」自動建立新實際工項`」選項（`value='0'`），即使資料庫處於 `0 種代表類型` 或 `0 個實際工項` 的初始狀態，下拉選單亦不會空白且可直接發起盲投議程。
    2. **Stored Procedure (`storyPoint.spCreateVotingSession` 等) 完整外鍵防呆**：執行建立或更新盲投議程時，自動驗證 `@workItemTypePk`、`@workItemPk`、`@cardDeckPk`、`@hostEmployeePk` 是否存在於對應主表；若未選擇、傳入 `0`/`null` 或因舊分頁殘留失效主鍵，系統自動依 `@sessionTitle` 比對同名代表類型/工項或自動新增對應之 `storyPoint.tbWorkItemType` / `storyPoint.tbWorkItem` 紀錄並綁定新 `pk`，徹底杜絕 `fkTbVotingSessionWorkItemType` 等外鍵衝突錯誤。
    3. **支援修改既有議程時清空重設當前盲投回合選票 (`@resetCurrentRoundVotes`)**：勾選「修改既有議程時，一併清空本回合出牌重新盲投」時，自動清除當前回合已投出之撲克牌紀錄並將回合狀態重設為 `voting`。

### 2.13 每個人皆可修改自己的登入帳號與密碼 (Self-Service Login Account & Password Update)
* **核心原則**：系統中每一位已登入的員工（包含管理者 `admin` 與一般參與者 `participant`）皆擁有自主修改**自己本人的登入帳號 (`loginAccount`) 與登入密碼 (`passwordHash`)** 的權限。
* **T-SQL 專屬預存程序 (`storyPoint.spUpdateEmployeeSelfCredential`)**：
    1. 支援傳入本人 `@employeePk`（或 `@currentLoginAccount`）、新的登入帳號 `@newLoginAccount`、新的密碼雜湊 `@newPasswordHash`（留空或 `null` 代表僅修改登入帳號不改密碼），以及選填之舊密碼雜湊驗證 `@currentPasswordHash`。
    2. 內建唯一性防呆檢核：確保修改後的 `@newLoginAccount` 不會與其他員工 (`pk <> @targetPk`) 的登入帳號重複；若重複則拋出明確繁體中文提示並拒絕覆寫他人帳號。
    3. 遵循全小寫 T-SQL 關鍵字、`set nocount on`、`try...catch` 例外處理，並自動呼叫 `storyPoint.spWriteLog` 將修改前後帳號與是否更新密碼完整寫入 `storyPoint.tbLog`。
* **Web 介面與無縫工作階段刷新 (`web/storyPointPortal`)**：
    1. **頂部固定入口與主選單入口**：
        * 於網頁右上角使用者資訊列 (`topbar-user`) 提供醒目的「**`🔑 修改我的帳密`**」按鈕 (`switchTab('myAccount')`)。
        * 於頂部主選單列 (`top-menubar`) 提供「**`🔑 修改我的帳密`**」快速按鈕，並於主選單 4（管理者為「`👥 組織與員工帳密管理`」內含 `4-0. 修改我的個人登入帳號與密碼`；參與者為「`🔑 個人帳密設定`」內含 `4-1. 修改我的個人登入帳號與密碼`）提供完整入口。
    2. **個人帳密修改表單 (`#pane-myAccount` 與參與者 `#pane-employees`)**：
        * 預設帶入本人目前登入帳號 (`newLoginAccount`)，提供新登入密碼 (`newPlainPassword`)、確認新密碼 (`confirmPlainPassword`) 與選填舊密碼驗證 (`currentPlainPassword`)，每個密碼欄位皆附帶 `👁️ 顯示 / 🙈 隱藏` 切換按鈕，且所有 `<label>` 標題皆為單行對齊並具備 `data-tip` 滑鼠移入浮動說明。
    3. **即時更新登入 Cookie (`refreshCurrentAuthCookieAsync`)**：
        * 透過 `POST /account/updateSelf`（或 `POST /employee/updateSelf`）完成帳密修改後，後端立即自動刷新當前瀏覽器的 Authentication Cookie Claims（更新 `ClaimTypes.NameIdentifier` 為新 `loginAccount`），使用者無須重新登入即可繼續操作，且右上角顯示之登入帳號即時同步更新。
* **PowerShell Console (`ps/manageEmployee.ps1`) 與 GUI (`ps/ui/uiMain.ps1`) 同步支援**：
    1. `ps/manageEmployee.ps1` 新增 `-actionType 'updateSelfCredential'`（搭配 `-employeePk`、`-newLoginAccount`、`-plainPassword`、`-currentPlainPassword`），直接呼叫 `storyPoint.spUpdateEmployeeSelfCredential`。
    2. `ps/ui/uiMain.ps1` 於頂部標題列右側新增「**`🔑 修改我的帳密`**」按鈕 (`showChangeOwnCredentialDialog`)，供任何登入者在 GUI 視窗中隨時修改自己的帳號與密碼。

### 2.14 盲投議程依個人區分「待參與」與「參與中」、從「待參與」點選進入「參與中」獨立網頁、多網頁並行參與不同 Session 與同 Session 防重複進入規範
* **核心原則**：
    1. **進行中盲投議程依個人狀態分為「📂 待參與」與「🟢 參與中」，由個人從「待參與」點選才進入「參與中」獨立網頁**：
        * 在主系統盲投大廳 (`/?tab=voting`) 左側「📌 投票議程清單」與右側「盲投議程總覽大廳」中，所有進行中的盲投議程（`sessionStatus in ('voting', 'revealed')`）不再混為單一「投票中與討論中議程」清單，而是**依每位登入員工個人 (`curEmpPk`) 目前是否已點選進入獨立網頁**，明確拆分為兩個分類區塊：
            1. **「📂 待參與」(`#folder-pending-join` / `#lobbyGridPending`)**：個人尚未點選進入獨立盲投網頁的進行中議程（包含新建立或剛被加入參與名單的議程，預設一律歸屬於「📂 待參與」）。
            2. **「🟢 參與中」(`#folder-participating` / `#lobbyGridParticipating`)**：個人已從「📂 待參與」主動點選開啟，目前正在專屬獨立網頁中進行的議程。
            3. **「📁 已拍板定案議程」(`#folder-finalized`)**：已完成核定點數的歷史議程（預設收合）。
        * **個人點選「待參與」進入「參與中」獨立網頁流程**：
            * 唯有當個人在「📂 待參與」清單或卡片中點選議程（點擊「`🚀 點選進入「參與中」獨立網頁`」按鈕或議程標題）時，系統才會在獨立網頁分頁開啟 `GET /voting/session?sessionPk={sessionPk}`，並將該筆議程對該名使用者而言由「📂 待參與」即時移入「🟢 參與中」區塊。
            * 當個人在獨立網頁中點擊「`🚪 結束參與並返回「待參與」(/voting/session/leave)`」、關閉該獨立網頁分頁、或於大廳「🟢 參與中」卡片點擊「`🚪 結束參與`」時，後端立即釋放該使用者對該議程的參與鎖定，並將該議程即時移回「📂 待參與」清單。
    2. **以專屬獨立網頁進行整個盲投 Session（非 Modal 彈窗、非大廳內嵌）**：
        * 當使用者從「📂 待參與」點選進入任一盲投議程 (`storyPoint.tbVotingSession`) 時，一律透過專屬獨立網頁路由 `GET /voting/session?sessionPk={sessionPk}`（或 `GET /voting/session/{sessionPk}`）開啟並進行整個 Session 的完整生命週期，絕不使用 Modal 對話框或內嵌於主大廳頁籤中。
        * 獨立盲投網頁內完整包含：綠色撲克牌桌與參與者座位區、單回合倒數計時器、撲克牌出牌區（含中文意義標示與理由輸入）、主持人控制台（立即翻牌揭曉、開啟下一回合重新盲投、達成共識拍板定案）、該 Session 歷次盲投回合統計表與具名出牌明細表（支援通用欄位排序 `⇅/▲/▼` 與標題列鎖定 `📌/🔒`）、以及 SignalR + 2 秒輪詢即時自動翻牌同步機制。
        * 在獨立網頁中執行出牌 (`POST /voting/cast`)、主持控制 (`POST /voting/control`) 或修改設定 (`POST /voting/create`) 時，皆帶入 `fromStandalone=true` 與 `tabInstanceId`，處理完成後直接重導向回該獨立盲投網頁，確保整個 Session 全程在該獨立網頁內完成。
        * **主持人控制台「開啟下一回合重新盲投」與「本回合討論紀錄」雙重保存與即時切換 (`storyPoint.spControlVotingSession` + `POST /voting/control`)**：
            * 在獨立盲投網頁主持人控制台中，輸入「本回合討論紀錄 / 下回合聚焦目標 (`roundDiscussionNote`)」並點擊「`🔄 2. 開啟下一回合重新盲投 (Next Round)`」（或「`👀 1. 立即翻牌揭曉`」、「`🏆 拍板定案`」）時，表單同時帶入 `controlAction` 與按鈕專屬 `submitAction`（後端亦相容舊版 `actionType` 欄位），並鎖定前端 `isAutoReloading` 防止輪詢或 SignalR 中斷表單送出。
            * `storyPoint.spControlVotingSession` 於執行 `nextRound` 時，會先將本回合之 `roundDiscussionNote` 與統計數據（投票數、最低、最高、平均值）完整寫入當前 `storyPoint.tbVotingRound` 紀錄中（於右側「本 Session 歷次盲投回合統計」之「回合討論紀錄」欄位永久留存），同時建立或重設下一回合 (`@currentRound + 1`) 為 `'voting'` 盲投狀態、將 `storyPoint.tbVotingSession.currentRoundNumber` 推進至下一回合，並同步將該討論紀錄更新為下一回合桌面頂部的「本回合聚焦目標 (`roundGoalNote`)」。
            * `storyPoint.spControlVotingSession` 於執行 `finalize`（拍板定案）時，會同時保存 `finalDecisionNote`（定案決議說明）至 `storyPoint.tbVotingSession` 並同步寫入最後一回合之 `storyPoint.tbVotingRound.roundDiscussionNote`；獨立盲投網頁在定案後會於「定案決議說明」輸入框回填顯示該文字，並同時在綠色撲克牌桌頂部、主持人控制台定案專區及右側「歷次盲投回合統計表」完整呈現拍板定案之點數與決議說明文字。
    3. **支援同一人同時從「待參與」點選多個不同議程，以多個獨立網頁並行進入「參與中」**：
        * 使用者可從「📂 待參與」分別點選多個「不同」的 `sessionPk`，在各自的獨立瀏覽器分頁 (`target="_blank"`) 同時開啟多個「🟢 參與中」獨立盲投網頁（例如：分頁 A 參與 `sessionPk=1`、分頁 B 同時參與 `sessionPk=2`），彼此獨立運作、互不干擾。
        * 獨立盲投網頁頂部亦提供「🌐 多網頁並行導覽列」，標示目前本人處於「🟢 參與中」與「📂 待參與」的各議程狀態，支援一鍵從「📂 待參與」開啟其他不同 Session 的獨立網頁。
    4. **嚴格禁止重複進入已在「參與中」的同一個盲投 Session (`同一個 session 不能重複進入`)**：
        * **後端即時參與鎖定 (`sqlDataService.votingSessionPresenceLease`)**：以 `(employeePk, votingSessionPk)` 為複合鍵建立執行緒安全的獨立網頁參與鎖定（45 秒背景心跳容錯 TTL，防背景分頁節流誤釋放；關閉或離開時立即釋放）。當某位員工已在分頁 `tabInstanceId_1` 點選進入 `sessionPk=X`（處於「🟢 參與中」）時，每 2 秒透過 `/voting/liveStatus` 或 `/voting/session/heartbeat` 續約心跳；同分頁重新整理（F5）或表單送出時透過短效 Reload Cookie (`sp_reload_inst_{sessionPk}`) 與 `tabInstanceId` 無縫續用同一鎖定。
        * **後端與前端雙重攔截重複進入**：當同一位員工嘗試對已在「🟢 參與中」的同一個 `sessionPk=X` 重複進入第二個網頁時：
            1. 在大廳「🟢 參與中」區塊中，按鈕狀態鎖定為「`🟢 參與中 (獨立網頁進行中，禁重複進入)`」，點擊時前端 (`openIndependentVotingSession` 結合 `BroadcastChannel('sp_voting_session_guard')`、`localStorage` 與後端 `participatingSessionPks` / `myActiveOpenSessionPks`) 立即阻擋重複開啟並提示切換至已開啟之「參與中」分頁。
            2. 若使用者直接於新分頁貼上 `/voting/session?sessionPk=X` 網址強制進入，後端 `votingSessionStandalonePage` 立即偵測到該 `(employeePk, sessionPk)` 已在「🟢 參與中」，回傳專屬「`🚫 同一個盲投 Session 禁止重複進入`」攔截頁面 (`buildDuplicateVotingSessionBlockedHtml`)，同時透過 `BroadcastChannel` 通知原本已開啟該 Session 的獨立網頁顯示提醒橫幅並聚焦。
    5. **獨立盲投網頁開啟下一輪重新盲投、本回合討論紀錄 (`roundDiscussionNote`) 與定案決議說明 (`finalDecisionNote`) 完整寫入與呈現**：
        * **主持人控制台動作與討論紀錄寫入**：於獨立盲投網頁 (`/voting/session?sessionPk=X`) 點擊「🔄 開啟下一回合重新盲投 (`nextRound`)」、「👁️ 公開翻牌揭曉本輪點數 (`reveal`)」或「✅ 拍板定案 (`finalize`)」時，透過隱藏欄位 `#hiddenControlAction` 搭配 `requestSubmit()` 確保 `controlAction` 與輸入之「📝 本回合討論紀錄 (`roundDiscussionNote`)」及「🏆 定案決議說明 (`finalDecisionNote`)」完整傳送至後端 `storyPoint.spControlVotingSession` 並寫入 `storyPoint.tbVotingRound.discussionNote` 與 `storyPoint.tbVotingSession.finalDecisionNote`。
        * **定案決議說明 (`finalDecisionNote`) 完整呈現**：凡有輸入「定案決議說明 (`finalDecisionNote`)」之議程，系統於以下所有介面同步醒目呈現 `🏆 定案決議說明`：
            1. 投票大廳 (`/?tab=voting`) 左側「📌 投票議程清單」之「📁 已拍板定案議程」卡片摘要與展開設定明細 (`#session-detail-{sPk}`)。
            2. 獨立盲投網頁 (`/voting/session?sessionPk=X`) 之綠色牌桌定案區塊、主持人/檢視者控制台及各回合歷程與定案紀錄表。
            3. 回合與範圍參與設定頁籤 (`#pane-roundSetup`) 之議程總覽表及查詢訂出 Story Point 過程頁籤 (`/?tab=history`)。

### 2.15 工項類型（專案 / 維運 / 顧問時數及自訂類型）完整增刪修 (CRUD)、起訖時間設定、到期自動轉態扣減 Story Point 與 Email 寄信通知部門管理層與 HR，以及職級含部門經理規範
* **工項類型 (`storyPoint.tbWorkItemKind`)、工項代表類型 (`storyPoint.tbWorkItemType`) 與實際工項 (`storyPoint.tbWorkItem`) 完整增、刪、修 (CRUD)**：
    1. **工項類型主檔 (`storyPoint.tbWorkItemKind`) 增、刪、修**：
        * 系統建立獨立資料表 `storyPoint.tbWorkItemKind`，預設內建三種核心工項類型：**`project`（🚀 專案）**、**`maintenance`（🛠️ 維運）**、**`consultingHour`（⏱️ 顧問時數）**，並支援管理員自由**新增 (`➕ 新增`)**、**修改 (`✏️ 修改`)** 與**刪除 (`🗑️ 刪除`)** 任何工項類型（包含類型代碼 `kindCode`、繁體中文名稱 `kindName`、圖示 `iconEmoji`、標籤配色 `badgeColor`、排序 `sortOrder` 與說明 `description`）。
        * 透過預存程序 `storyPoint.spUpsertWorkItemKind` 與 `storyPoint.spDeleteWorkItemKind`、Web 介面 (`/?tab=workitems` 及 `/?tab=types` 之「🗂️ 工項類型增、刪、修管理」專區，對應路由 `POST /workitem/kind/save` 與 `POST /workitem/kind/delete`) 以及 PowerShell (`ps/manageWorkItem.ps1` 之 `listWorkItemKind` / `upsertWorkItemKind` / `deleteWorkItemKind` 與 `ps/ui/tabWorkItem.ps1`) 提供完整增刪修功能；刪除任一工項類型時，原本歸屬該類型之工項與代表類型會自動安全轉移至預設或替代類型，確保資料完整性。
    2. **工項代表類型 (`storyPoint.tbWorkItemType`) 與實際工項 (`storyPoint.tbWorkItem`) 增、刪、修**：
        * 除新增與修改外，亦透過 `storyPoint.spDeleteWorkItemType` (`POST /workitem/type/delete`) 與 `storyPoint.spDeleteWorkItem` (`POST /workitem/delete`) 於 Web 介面及 PowerShell 提供代表類型與實際工項之完整刪除功能，並於 `storyPoint.tbPointHistory` 留存完整稽核紀錄。
    3. **起訖時間 (`startDate` ~ `endDate`)**：每一筆工項皆具備明確的「開始時間（起時 `startDate`）」與「結束時間（迄時 `endDate`）」，並於 Web 介面 (`/?tab=workitems`)、批次匯入/匯出 (CSV / Excel / 互動 HTML) 與 PowerShell (`ps/manageWorkItem.ps1` / `ps/ui/tabWorkItem.ps1`) 提供完整設定與查詢。
* **達到迄時自動轉換工項狀態、扣減 Story Point 並透過 Email 寄信給部門管理層與 HR**：
    1. **自動偵測與轉換狀態 (`storyPoint.spProcessExpiredWorkItem`)**：
        * 當系統時間達到或超過工項的迄時 (`endDate <= dateadd(hour, 8, sysutcdatetime())`) 且尚未處理 (`isExpiredProcessed = 0`) 時，系統（透過 `workItemExpiryBackgroundService` 背景定時排程、頁面載入/工項儲存自動觸發、或手動點擊 `POST /workitem/checkExpired`）自動將該工項之 `executionStatus` 轉換為 `'done'`（顯示為「已完成 (已到期扣點)」）。
    2. **自動扣減工項、員工與部門之 Story Point**：
        * 將該到期工項原本的點數完整記錄於 `originalStoryPoint` 與 `deductedStoryPoint`，並將工項目前點數扣減歸零 (`storyPoint = 0.00`)。
        * 同步將該工項指派給各負責員工 (`storyPoint.tbWorkItemAssignment`) 的配額點數記錄於 `originalAllocatedStoryPoint` 與 `deductedAllocatedStoryPoint`，並將有效配額扣減歸零 (`allocatedStoryPoint = 0.00`)，使負責員工與所屬部門身上的有效總 Story Point 立即扣除該筆到期工項點數，同時於 `storyPoint.tbPointHistory` 寫入 `changeSource = 'autoExpireDeduct'` 之完整稽核紀錄。
    3. **透過 Email 寄信通知部門管理層（部門經理）與 HR (`emailNotificationService` + `storyPoint.spMarkWorkItemExpiryNotified`)**：
        * 系統自動解析該到期工項負責員工所屬部門之**部門管理層**（職級 `jobTitle` 包含「部門經理」之主管，或對應 `tbDepartment.managerName` 之部門主管，以及管理部主管）與 **HR 部門 (`departmentCode = 'HR'` 或職稱含人資/HR)** 之電子郵件信箱。
        * 透過 Azure Communication Services (ACS) Email REST API (`mailProviderSetting`) 寄發繁體中文 HTML 通知信予部門管理層與 HR，信件內詳列工項代碼、名稱、工項類型（專案/維運/顧問時數）、起訖時間、原核定點數、本次自動扣除之 Story Point、以及受影響部門與負責員工明細，並將寄信時間與收件人名單記錄於 `expiryNotificationSent`、`expiryNotificationSentAt`、`expiryNotificationRecipient` 與 `storyPoint.tbLog`。
* **員工職級包含「部門經理」(`jobTitle`)**：
    1. 員工資料表 (`storyPoint.tbEmployee.jobTitle`)、預設種子資料 (`sql/seedData.sql`)、Web 員工管理表單 (`#pane-employees` 快速職級選單與 `datalist`)、員工清單職級篩選器 (`#filterEmpRank`) 及 PowerShell 員工管理介面 (`ps/ui/tabEmployee.ps1`) 均明確包含「**部門經理**」（以及「董事長兼部門經理」、「部門經理 / 資深架構師」、「部門經理 / 商業智慧資深顧問」、「部門經理 / 資料庫資深顧問」、「部門經理 / 人資」等職級），各部門主管帳號皆具備「部門經理」職級並顯示專屬徽章。

### 2.16 員工資料記載「是否為部門主管 (`isDepartmentManager`)」與「樹狀遞迴階層 (`managerEmployeePk`)」、支援同部門多位主管皆通知，且畫面僅呈現上一層主管
* **員工資料表 (`storyPoint.tbEmployee`) 新增部門主管標記與樹狀遞迴階層欄位**：
    1. **`isDepartmentManager` (`bit not null default(0)`)**：明確記載該名員工是否為部門主管（包含各部門主管與 HR 人資主管），且**同一個部門可以有多位主管 (`isDepartmentManager = 1`)**。
        * 預設種子資料 (`sql/seedData.sql`) 中，標記為 `isDepartmentManager = 1` 之部門主管為：
            * 管理部 (`MGMT`)：**胡百敬** (`081201`, `byron.hu`, `Byron.Hu@mentortrust.com`, 董事長兼部門經理)
            * HR 部門 (`HR`)：**許立慧** (`250201`, `raya`, `raya.hsu@mentortrust.com`, 部門經理 / 人資 — **人資主管**)
            * DB/INFra 部門 (`DB_INFRA`)：**林英豪** (`120801`, `john.yh.lin`, `Eric.Lin@mentortrust.com`, 部門經理 / 資料庫資深顧問)
            * BI 部門 (`BI`)：**蘇彥彰** (`180101`, `steven.su`, `Steven.Su@mentortrust.com`, 部門經理 / 商業智慧資深顧問)
            * 開發部門 (`DEV`)：**黃振祖** (`140901`, `jtrue.huang`, `Jtrue.Huang@mentortrust.com`, 部門經理 / 資深架構師)
        * 管理者可隨時將同一部門內的多位同仁皆設為部門主管 (`isDepartmentManager = 1`)。
    2. **`managerEmployeePk` (`int null` 自我參照外鍵 `fkTbEmployeeManager references storyPoint.tbEmployee(pk)`)**：用**樹狀遞迴階層 (Recursive Tree Hierarchy)** 記載每位員工的上一層主管：
        * **第 1 層 (L1 Root)**：`胡百敬` (`managerEmployeePk = null`, `isDepartmentManager = 1`)。
        * **第 2 層 (L2)**：各部門主管（`isDepartmentManager = 1`）以及管理部直屬同仁（`柯乃云`、`張月桂`），其 `managerEmployeePk` 指向 L1 `胡百敬.pk`。
        * **第 3 層 (L3)**：HR、DB/INFra、BI、開發各部門之一般同仁（`isDepartmentManager = 0`），其 `managerEmployeePk` 指向所屬部門之上一層部門主管 `pk`。
* **T-SQL Recursive CTE 檢視表與預存程序（若部門有多位主管，多位都通知）**：
    1. **`storyPoint.vwEmployeePointSummary` 與 `storyPoint.vwDepartmentPointSummary`**：
        * 透過遞迴 CTE 計算並輸出每位員工之 `isDepartmentManager`、`managerEmployeePk`、`managerEmployeeName`（上一層主管姓名）、`departmentManagerName`、`departmentManagerEmail`、`departmentManagerCount`、`hrManagerName`、`hrManagerEmail`。
        * 當同一個部門有多位部門主管 (`isDepartmentManager = 1`) 時，`departmentManagerName` 與 `departmentManagerEmail` 自動彙整該部門所有啟用中之部門主管（例如 `林英豪、陳輝瑞`），`vwDepartmentPointSummary.managerName` 亦同步串接所有部門主管姓名。
    2. **`storyPoint.spUpsertEmployee`**：支援同一個部門設定多位 `@isDepartmentManager = 1`，內建遞迴 CTE 循環參照防呆檢核 (`cycleCheckCte`)，並自動同步更新 `storyPoint.tbDepartment.managerName` 為該部門所有部門主管姓名清單。
    3. **`storyPoint.spProcessExpiredWorkItem` 到期通知信收件人精準解析（多位部門主管皆通知）**：
        * 當工項達到迄時 (`endDate`) 自動轉態並扣除 Story Point 時，透過 **T-SQL Recursive CTE (`empManagerHierarchyCte`)** 與同部門主管集合，將**該同仁所屬部門之所有部門主管 (`isDepartmentManager = 1`)**（若同部門有多位主管，則多位主管全部列入通知收件人）以及 **`HR` 部門之所有部門主管 (`isDepartmentManager = 1`，即人資主管)** 一併納入收件人清單寄發通知信。
* **Web 介面與 PowerShell 完整支援（只要呈現上一層主管即可）**：
    1. 在「📊 3-1. 員工與部門總 Story Point 統計 (`vwEmployeePointSummary`)」與「👥 4-1. 員工列表 (`#empDataTable`)」中，主管欄位**僅呈現該員工的「上一層主管 (`managerEmployeeName`)」即可**（不顯示從頂層 Root 串接至本人的冗長路徑），並在下方清楚列出到期通知對象（若部門有多位主管則完整顯示多位部門主管 + 人資主管）。
    2. PowerShell 模組 (`ps/manageEmployee.ps1` 與 `ps/ui/tabEmployee.ps1`) 同步支援 `-isDepartmentManager` 與 `-managerEmployeePk` 參數與介面控制項。

---

## 3. 系統架構與技術開發規範

### 3.1 雲端與雙棲整合架構
* **專案根目錄**：`c:\temp\storyPoint`
* **Azure PaaS 部署**：
    * **Azure 資源群組 (Resource Group)**：指定建立並使用 **`storyPoint`**（位於 `eastasia` 區域）。
    * **Azure App Service (.NET 8 ASP.NET Core MVC/Razor + SignalR)** 搭配 **Azure SQL Database**（同時 100% 相容本機 SQL Server 開發與測試環境）。
    * 提供 PowerShell 自動化部署腳本 (`ps/deployAzure.ps1`) 與資料庫初始化腳本 (`ps/initDatabase.ps1`)。
* **Web 網站與 PowerShell 雙介面並行**：
    1. **ASP.NET Core 8 Web 系統 (`web/storyPointPortal`)**：
        * **網頁上方主選單與子選單階層式下拉導覽列 (Top Bar Hierarchical Dropdown Menu - 單排精簡與浮動 Tips 說明)**：
            * **強制單排不換行與選項長度精簡**：頂部主選單列 (`.top-menubar-inner`、`.menu-group-list`) 與下拉子選單選項 (`.menu-sub-item`) 一律精簡文字長度並強制單排呈現（不換成兩排），將原本冗長的功能說明移至 `data-tip` **浮動說明工具提示 (`#spFloatingTooltip`)**，滑鼠移至主選單按鈕或子選單項目時即時浮動顯示完整功能說明：
            1. **【🃏 多人估算】主選單**：
                * 子選單 1-1：`🃏 1-1. 即時盲投大廳`（浮動 Tips 說明 Planning Poker 出牌、翻牌與定案）
                * 子選單 1-2：`⚙️ 1-2. 設定盲投回合`（浮動 Tips 說明盲投開始前設定標的、參與名單、點數範圍與共識規則）
                * 子選單 1-3：`📜 1-3. 估算過程查詢`（浮動 Tips 說明事後檢視每回合出牌明細與異動紀錄）
            2. **【⚙️ 設定回合】主選單快速入口**：直接切換至「設定盲投回合」頁面，搭配浮動 Tips 說明。
            3. **【📋 工項管理 / 📋 我的工項】主選單**：
                * 子選單 2-1：`📋 2-1. 工項與員工指派` / `📋 2-1. 我的工項查詢`
                * 子選單 2-2：`🏷️ 2-2. 工項代表類型` / `🏷️ 2-2. 代表類型參考`
                * 子選單 2-3 ~ 2-5：`2-3. 匯出工項 (CSV)`、`2-4. 匯出工項 (Excel)`、`2-5. 匯出工項 (HTML)`
            4. **【📊 統計報表 / 📊 我的報表】主選單**：
                * 子選單 3-1：`📊 3-1. 員工與部門統計` / `📊 3-1. 我的分數統計`
                * 子選單 3-2A ~ 3-3C：部門與員工點數三格式匯出（`CSV` / `Excel` / `HTML`）
            5. **【🔑 修改帳密】主選單快速入口**：直接切換至個人登入帳號與密碼修改頁面。
            6. **【👥 組織與員工 / 🔑 帳號設定】主選單**：
                * 子選單 4-0 / 4-1：`🔑 修改我的帳密`、`👥 4-1. 員工與組織管理`、`4-2A. 員工 CSV 範本`、`4-2B. 員工 Excel 範本`
            7. **【🛡️ 系統 Log】主選單（管理者可見）**：
                * 子選單 5-1 ~ 5-2C：`🛡️ 5-1. 系統 Log 查詢` 及 `CSV` / `Excel` / `HTML` 匯出。
        * 提供完整的登入驗證、多人即時 Planning Poker 投票大廳、工項代表類型管理、工項建立（自動帶入代表類型點數/手動調整）與員工賦予設定、投票過程時間軸查詢、員工個人與部門總 Story Point 統計儀表板。
    2. **PowerShell Console 核心模組 (`ps/*.ps1`) 與多 Tab GUI 管理工具 (`ps/ui/uiMain.ps1`)**：
        * 所有商業邏輯與資料操作封裝於獨立參數化的 Console `.ps1` 腳本，共用 `ps/common.ps1`。
        * 多 Tab WinForms/WPF 圖形介面 (`ps/ui/uiMain.ps1` 與各獨立 Tab `.ps1`) 全部透過呼叫對應的 Console `.ps1` 執行功能，支援完整模式 (`full`) 與簡化模式 (`simple`)。

### 3.2 T-SQL 資料庫開發規範
* **專屬 Schema**：統一使用 `storyPoint` Schema（嚴禁使用 `dbo`），所有物件存取強制使用兩節式名稱（例如：`storyPoint.tbEmployee`、`storyPoint.spCastVote`）。
* **命名慣例**：
    * 所有資料表 (`tb*`)、檢視表 (`vw*`)、預存程序 (`sp*`)、函數 (`fn*`)、欄位與變數一律採用 **Camel Case 單數命名**，不使用底線（underscore）。
    * 所有資料表必備主鍵，統一命名為 `pk` (`int identity(1,1)`)。
* **語法規範**：
    * **T-SQL 關鍵字強制全小寫**（`select`, `from`, `where`, `insert`, `update`, `delete`, `create or alter`, `begin try` 等）。
    * 嚴禁 `select *`，必須明確列出欄位。
    * 確保可重複執行（Idempotency）：資料表支援無損結構保留與 `drop table if exists`，View / Function / Stored Procedure 一律使用 `create or alter`。
    * 每個 Stored Procedure 包含標準繁體中文檔頭註解（用途、建立者、日期、參數、修改歷程）、第一行 `set nocount on`、完整 `try...catch` 例外處理，並呼叫 `storyPoint.spWriteLog` 將 SP 名稱、耗時、執行結果與錯誤明細寫入 `storyPoint.tbLog`。

### 3.3 PowerShell 與設定、Log 規範
* **命名與編碼**：變數、函數、參數、檔名、目錄皆採用單數 Camel Case；程式碼關鍵字全小寫；PowerShell 7 採 UTF-8 with BOM 儲存，Windows PowerShell 5.1 採 Unicode (UTF-16 LE) 儲存。
* **設定與連線加密**：所有設定存放於 `config.json`（並提供 `config/config.template.json`），連線字串包含明確 `Application Name=azureStoryPointSystem`，並提供 `ps/setEncryptedConfig.ps1` 將正式環境連線字串加密儲存。
* **錯誤重試與雙軌 Log 系統**：具備自動 Retry 機制、執行進度呈現 (`write-progress`)，並提供 **DB (`storyPoint.tbLog`)** 與 **CSV (`logs/` 目錄下含日期時間檔名，可直接由 `bcp.exe` 載入)** 雙軌 Log 記錄，遵循 .NET Log 層級 `0` (不寫) ~ `4` (任何訊息)。
* **Git 與 GitHub 版本控制**：
    * 專案根目錄包含 `.gitignore`，忽略系統暫存檔 (`Thumbs.db`, `.DS_Store`)、編輯器設定 (`.vscode/`, `.idea/`, `.vs/`)、建置輸出 (`bin/`, `obj/`, `publish/`)、執行期間 Log 與匯出報表 (`logs/`, `*.log`, `*.csv`)、開發測試暫存目錄 (`temp/`)，以及含敏感設定之 `config.json`。
    * 遠端儲存庫建立並推送至 GitHub Repository (`https://github.com/ByronHul/storyPoint`)，每次執行 `git commit` 與 `git push` 時，提交訊息與說明皆須清楚說明變更目的與具體變更內容。
