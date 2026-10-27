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
    2. **同步新增集英信誠員工 (`storyPoint.tbEmployee`) 與帳戶預設密碼規範**：將 `phishing.tbRecipient` 內屬於集英信誠 (`mentorTrust`) 之全體同仁（含員工編號、姓名、信箱、所屬部門、職稱與登入帳號）完整匯入 `storyPoint.tbEmployee`（總計 32 位啟用帳號：含保留於 `HR` 部門之 `admin` 與 `raya`，以及同步自集英信誠之 30 位同仁）。
    3. **所有帳戶預設密碼設定為 `P@ssw0rd`（`admin`、`byron` / `byron.hu` 與 `raya` 的密碼不修改）**：
        * 系統中所有員工帳戶（除 `admin`、`byron` / `byron.hu`、`raya` 外）之預設登入密碼統一設定為 **`P@ssw0rd`**（對應 SHA-256 雜湊值 `b03ddf3ca2e714a6548e7495e2a03f5e824eaac9837cd7f159c67b90fb4b7342`），包含資料庫現有一般參與者帳戶、種子腳本 (`sql/seedData.sql`)、`storyPoint.spUpsertEmployee` 新增預設值、Web 批次匯入預設密碼與 PowerShell (`ps/manageEmployee.ps1` / `ps/ui/tabEmployee.ps1`) 預設密碼。
        * **`admin`**、**`byron` (`byron.hu`)** 與 **`raya`** 三個帳戶之既有密碼雜湊嚴格保護、絕不修改。

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

### 2.15 工項類型（專案 / 維運 / 顧問時數 / 教育訓練 / 開發產品及自訂類型）完整增刪修 (CRUD)、起訖時間設定、到期自動轉態扣減 Story Point 與 Email 寄信通知部門管理層與 HR，以及職級含部門經理規範
* **工項類型 (`storyPoint.tbWorkItemKind`)、工項代表類型 (`storyPoint.tbWorkItemType`) 與實際工項 (`storyPoint.tbWorkItem`) 完整增、刪、修 (CRUD)**：
    1. **工項類型主檔 (`storyPoint.tbWorkItemKind`) 增、刪、修**：
        * 系統建立獨立資料表 `storyPoint.tbWorkItemKind`，預設內建五種核心工項類型：**`project`（🏗️ 專案，排序 10）**、**`maintenance`（🛠️ 維運，排序 20）**、**`consultingHour`（⏳ 顧問時數，排序 30）**、**`training`（🎓 教育訓練，排序 40）**、**`productDevelopment`（🚀 開發產品，排序 50）**，並支援管理員自由**新增 (`➕ 新增`)**、**修改 (`✏️ 修改`)** 與**刪除 (`🗑️ 刪除`)** 任何工項類型（包含類型代碼 `kindCode`、繁體中文名稱 `kindName`、圖示 `badgeIcon`、標籤配色 `badgeColor`、排序 `sortOrder` 與說明 `description`）。
        * 透過預存程序 `storyPoint.spUpsertWorkItemKind` 與 `storyPoint.spDeleteWorkItemKind`、Web 介面 (`/?tab=workitems` 及 `/?tab=types` 之「🗂️ 工項類型增、刪、修管理」專區，對應路由 `POST /workitem/kind/save` 與 `POST /workitem/kind/delete`) 以及 PowerShell (`ps/manageWorkItem.ps1` 之 `listWorkItemKind` / `upsertWorkItemKind` / `deleteWorkItemKind` 與 `ps/ui/tabWorkItem.ps1`) 提供完整增刪修功能；刪除任一工項類型時，原本歸屬該類型之工項與代表類型會自動安全轉移至預設或替代類型，確保資料完整性。
    2. **工項代表類型 (`storyPoint.tbWorkItemType`) 與實際工項 (`storyPoint.tbWorkItem`) 增、刪、修**：
        * 除新增與修改外，亦透過 `storyPoint.spDeleteWorkItemType` (`POST /workitem/type/delete`) 與 `storyPoint.spDeleteWorkItem` (`POST /workitem/delete`) 於 Web 介面及 PowerShell 提供代表類型與實際工項之完整刪除功能，並於 `storyPoint.tbPointHistory` 留存完整稽核紀錄。
    3. **起訖時間 (`startDate` ~ `endDate`) 與「起訖時間快速設定」**：
        * 每一筆工項皆具備明確的「開始時間（起時 `startDate`）」與「結束時間（迄時 `endDate`）」，並於 Web 介面 (`/?tab=workitems`)、批次匯入/匯出 (CSV / Excel / 互動 HTML) 與 PowerShell (`ps/manageWorkItem.ps1` / `ps/ui/tabWorkItem.ps1`) 提供完整設定與查詢。
        * **起訖時間快速設定 (`#wiDurationQty` + `#wiDurationUnit`)**：改為提供「**數量**（手動輸入 `<input type='number'>`，預設值為 `1`）」與「**單位**（下拉 `<select>` options：**`天`**、**`周`**、**`月`**、**`季`**、**`年`**）」，當變更數量、單位或開始時間（起時）時，系統自動依據起時加計對應之天/周/月/季/年計算並帶入結束時間（迄時）。
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

### 2.17 表格操作最佳化：全系統表格即時篩選、純滑鼠右鍵選取紀錄與操作選單（不使用下拉選單），且單筆編輯直接彈出 Modal 視窗（不捲動網頁）
* **全系統表格提供多維度與欄位級即時篩選**：
    1. **通用表格工具列即時篩選 (`.sp-table-toolbar-filter`)**：
        * 系統中所有包含 `<thead>` 與 `<tbody>` 的 `<table>` 皆於表頭上方工具列內建：
            * `🏷️ 欄位選擇下拉選單`（可選「全部欄位」或指定單一表頭欄位）。
            * `🎯 欄位既有值快速下拉選單`（當選擇指定欄位時，自動列出該欄不重複之欄位值供一鍵選取過濾）。
            * `🔍 關鍵字快速篩選輸入框`（即時過濾符合關鍵字之資料列）。
            * `📊 顯示筆數統計徽章`（即時顯示 `顯示 X / Y 筆`）與 `✖ 清除篩選` 按鈕。
    2. **重點業務表格專屬多維度組合篩選列**：
        * **工項清單 (`#workItemDataTable` — 各工項的類型、起訖時間、Story Point 與到期自動扣點寄信一覽表)**：提供「🔍 關鍵字快速篩選」、「🏷️ 工項類型篩選（專案/維運/顧問時數/自訂類型）」、「📐 所屬代表類型篩選」、「⚡ 執行與到期狀態篩選（待處理/進行中/已完成/已達迄時自動扣點/有效計點中）」、「👤 負責員工 / 部門篩選」及「🔄 清除全部篩選條件」。
        * **工項代表類型一覽表 (`#workItemTypeDataTable`)**：提供「🔍 關鍵字快速篩選」、「🏷️ 預設工項類型篩選」、「📊 估算狀態篩選（已核定 / 待投票定案）」。
        * **員工與組織管理一覽表 (`#empDataTable`)** 與 **系統 Log 稽核表 (`#sysLogDataTable`)**：與通用表格工具列篩選無縫雙向整合。
* **所有的 `table` 全面提供「滑鼠右鍵 (`contextmenu`)」選取紀錄與操作選單，不使用下拉選單按鈕，且編輯紀錄時直接彈出 Modal 視窗（不捲動網頁）**：
    1. **純滑鼠右鍵選取與操作（移除列內下拉選單按鈕與最右側操作欄）**：
        * 全面移除表格列內的 `⚡ 操作 ▾` 下拉式按鈕與最右側操作欄。
        * 使用者在**全系統任何 `<table>` 的任一筆資料列 (`tbody tr`) 按下滑鼠右鍵 (`contextmenu`)**，即可自動反白選取該筆紀錄 (`.sp-context-active-row`)，並在滑鼠游標位置直接彈出右鍵操作選單 (`#spRowContextMenu`)，選擇對該筆紀錄的操作行為。
    2. **編輯該筆紀錄內容時，直接彈出置中 Modal 視窗 (`#spRecordEditModalOverlay` / `#spRecordEditModal`) 做單筆編輯，絕不捲動網頁位置**：
        * 當使用者從右鍵選單點選「✏️ 單筆編輯...」或相關修改動作時，**禁止使用 `window.scrollTo` 捲動網頁到上方或特定網頁位置**，而是**直接原地彈出置中的單筆編輯 Modal 視窗**，於 Modal 視窗內完成單筆紀錄修改與儲存：
            * **工項紀錄 (`workitem`)**：
                * `✏️ 單筆編輯此工項設定（彈出 Modal 視窗）` ➔ 彈出 `openWorkItemEditModal` 單筆編輯工項代碼、工項類型、標題、起訖時間（其中「起訖時間快速設定」完整比照主表單設定方式，提供 `數量` 數值輸入框 + `單位：天/周/月/季/年` 下拉選單，隨即依起時自動計算迄時）、代表類型、Story Point、Sprint、狀態與微調原因，且於 Modal 底部直接提供 `🔄 重新投票此工項` 按鈕。
                * `👥 修改此工項之賦予員工與配額（彈出 Modal 視窗）` ➔ 彈出 `openWorkItemAssignModal` 單筆調整負責員工與分攤比例。
                * `🔄 重新投票此工項點數（重啟新一輪多人盲投議程）` ➔ 直接對該工項發起或重啟新一輪盲投議程 (`triggerRevoteFromTarget`)。
                * `🃏 針對此工項發起「多人盲投估算」（彈出 Modal 視窗）` ➔ 彈出 `openSessionSetupEditModal` 直接發起或調整重新投票回合設定。
                * `⏰ 設為已到期並彈出 Modal 編輯（測試自動扣點寄信）` ➔ 彈出 `openWorkItemEditModal` 並預填已到期時間。
                * `🗑️ 刪除此工項紀錄`。
            * **工項類型紀錄 (`workitemKind`)**：
                * `✏️ 單筆編輯此工項類型（彈出 Modal 視窗）` ➔ 彈出 `openWorkItemKindEditModal` 單筆編輯類型代碼、名稱、圖示、顏色、排序與說明。
                * `🔍 篩選套用此類型的所有工項`、`🗑️ 刪除此工項類型`。
            * **工項代表類型紀錄 (`workitemType`)**：
                * `✏️ 單筆編輯此工項代表類型（彈出 Modal 視窗）` ➔ 彈出 `openWorkItemTypeEditModal` 單筆編輯代表類型與標準 Story Point，且於 Modal 底部直接提供 `🔄 重新投票此代表類型` 按鈕。
                * `🔄 重新投票此代表類型標準點數（重啟新一輪多人盲投議程）` ➔ 直接對該代表類型發起或重啟新一輪盲投議程 (`triggerRevoteFromTarget`)，重設已定案狀態並進入盲投。
                * `🃏 針對此代表類型發起「多人盲投估算」（彈出 Modal 視窗）`、`🔍 檢視套用此代表類型的所有工項`、`🗑️ 刪除此工項代表類型`。
            * **員工紀錄 (`employee`，含員工管理表與統計報表員工表)**：
                * `✏️ 單筆編輯員工資料、部門主管與登入帳密（彈出 Modal 視窗）` ➔ 彈出 `openEmployeeEditModal` 單筆編輯員工資料、部門、職稱、是否為部門主管、上層主管與登入帳密。
                * `📋 篩選檢視此員工負責的所有工項`、`🚫 停用 / ✅ 啟用此員工帳號`、`🗑️ 永久刪除此員工帳號`。
            * **部門紀錄 (`department`，統計報表部門彙總表)**：
                * `✏️ 單筆編輯此部門資料與主管設定（彈出 Modal 視窗）` ➔ 彈出 `openDepartmentEditModal` 單筆編輯部門代碼、名稱、主管與職掌備註。
                * `👥 篩選檢視此部門的所有員工`。
            * **盲投議程紀錄 (`session`)**：
                * `🔄 重新投票此盲投議程（開啟新一輪盲投並重設已定案狀態）` ➔ 直接開啟新一輪盲投並將已定案議程重設回盲投進行中 (`triggerRevoteFromTarget`)。
                * `🔁 清空本回合已出牌紀錄並重新投票（保持同一回合號）` ➔ 清空當前盲投回合已出牌紀錄並重新盲投。
                * `✏️ 單筆編輯此盲投議程回合設定（彈出 Modal 視窗）` ➔ 彈出 `openSessionSetupEditModal` 單筆編輯議程設定。
                * `🚀 開啟此盲投 Session 獨立網頁`。
            * **所有表格共通右鍵項目（含投票歷程表、點數異動表、系統 Log 表及獨立盲投網頁表格）**：
                * `🪟 彈出 Modal 視窗檢視此筆紀錄完整明細` (`openRowDetailReadonlyModal`)、`🔍 以此儲存格值篩選【欄位名稱】`、`🔄 清除本表格所有篩選條件並重設排序`、`📋 複製本列紀錄文字至剪貼簿`。
    3. **PowerShell GUI (`ps/common.ps1` 與 `ps/ui/*.ps1`) 同步支援**：所有 `DataGridView` 皆掛載右鍵 `ContextMenuStrip` 快捷選單，右鍵點選「✏️ 單筆編輯此筆紀錄（彈出 Modal 視窗）」即直接彈出置中 WinForms Modal 對話框 (`Form.ShowDialog()`) 進行單筆編輯與儲存，並支援右鍵刪除、以此儲存格值篩選表格、清除篩選與複製整列紀錄。

### 2.18 全面優化操作介面與流程（表格置頂呈現、雙擊與快捷鍵彈出 Modal、單一 Modal 整合工項與指派、一鍵發起盲投與快速出牌/定案）
* **1. 精簡各分頁版面（新增置頂快捷指令列 `.sp-page-action-bar`，將大型管理/匯入表單收合於可展開抽屜 `.sp-collapsible-drawer`，讓資料表格直接置頂呈現）**：
    * 於「工項管理 (`#pane-workitems`)」、「工項代表類型 (`#pane-types`)」與「組織與員工管理 (`#pane-employees`)」分頁頂部配置精簡的**快捷指令列 (`.sp-page-action-bar`)**，提供一鍵彈出 Modal 新增紀錄（`➕ 新增工項與指派 (彈出 Modal)`、`👥 指派員工與分攤配額 (Modal)`、`🗂️ 新增工項類型 (Modal)`、`➕ 新增工項代表類型 (彈出 Modal)`、`➕ 新增員工帳號 (彈出 Modal)`、`🏢 新增/修改部門 (Modal)`）及抽屜面板展開/收合按鈕（`toggleAdminDrawer`）。
    * 原本佔用大量垂直畫面的新增、批次匯入與組織樹面板預設收合於 `.sp-collapsible-drawer.collapsed` 容器中，使各分頁的核心資料表格（`#workItemDataTable`、`#workItemTypeDataTable`、`#empDataTable`）一進入分頁即直接置頂呈現，免捲動即可立即瀏覽、篩選與操作。
* **2. 優化表格互動體驗（滑鼠左鍵單擊選取列、雙擊列直接彈出單筆編輯 Modal、鍵盤快捷鍵 `Enter` / `F2` / `Esc`）**：
    * **滑鼠左鍵單擊 (`click`)**：點選任一表格資料列 (`tbody tr`) 即高亮度反白選取該筆紀錄 (`.sp-context-active-row`)。
    * **滑鼠左鍵雙擊 (`dblclick`)**：雙擊任一表格資料列 (`tbody tr`) 直接觸發 `triggerDefaultRowEditOrView(tr)` 彈出對應之置中單筆編輯 Modal 視窗（管理員針對 `workitem`、`workitemKind`、`workitemType`、`employee`、`department`、`session` 彈出編輯 Modal，一般參與者或唯讀紀錄彈出 `openRowDetailReadonlyModal` 明細檢視 Modal），完全不捲動網頁。
    * **鍵盤快捷鍵 (`keydown`)**：選取任一表格列後，按 `Enter` 或 `F2` 直接彈出該列單筆編輯/明細 Modal 視窗；按 `Escape` 立即關閉右鍵選單或 Modal 視窗。
    * **PowerShell GUI (`ps/common.ps1`) 同步支援雙擊列彈出 Modal**：於 `enableGridHeaderSortAndLock` 新增 `$grid.add_celldoubleclick` 事件，雙擊 `DataGridView` 任一資料列即自動彈出單筆編輯/檢視 Modal 視窗 (`Form.ShowDialog()`)。
* **3. 優化「多人盲投估算」流程（大廳一鍵彈出 Modal 快速發起盲投、預設規則範本、一鍵出牌與一鍵採用推薦點數定案）**：
    * **大廳快速發起盲投**：於即時盲投大廳 (`#pane-voting`) 側邊欄與頂部標題列新增 `⚡ 快速發起盲投議程 (彈出 Modal)` 按鈕 (`openSessionSetupEditModal(0)`)，管理員不需離開大廳即可彈出 Modal 建立新盲投議程。
    * **回合設定 Modal 範本與全選控制**：`openSessionSetupEditModal` 內建「📐 標準 Scrum (0.5~21pt / 180秒)」、「🚀 快速閃電 (1~13pt / 60秒)」、「🏛️ 嚴謹評審 (0.5~34pt / 300秒)」一鍵套用範本按鈕、「✅ 全選成員 / ⬜ 全不選」按鈕，以及「🚀 建立議程並立即前往盲投大廳」與「💾 僅儲存於回合設定」雙模式送出按鈕。
    * **獨立盲投網頁 (`/voting/session`) 快速出牌與定案**：
        * 投票者支援**雙擊撲克牌面直接送出盲投**，並提供「`⚡ 點選牌面即直接送出盲投`」勾選開關，點選撲克牌即可一鍵完成蓋牌投票。
        * 主持人控制台新增「`🏆 3. 一鍵採用推薦點數 ({suggestedFinalPt} pt) 拍板定案`」按鈕，一鍵帶入共識演算法推薦點數並完成定案回寫。
* **4. 優化「工項管理與員工指派」流程（於單筆新增/編輯工項 Modal 直接整合負責員工與分攤配額指派）**：
    * `openWorkItemEditModal` 直接整合「**👥 負責員工與點數分攤配額指派**」設定區塊（包含 `primaryEmployeePk` 負責員工下拉選單與 `primaryAllocationPercent` 分攤比例輸入框，以及 `🔄 切換至多人分攤配額設定 Modal` 快捷切換鈕），讓管理員在單一 Modal 視窗內即可一次完成工項建立/修改與員工配額指派。
    * `openWorkItemAssignModal` 支援從頂部快捷指令列直接開啟並透過下拉選單選擇任一目標工項進行多人分攤配額管理。
* **5. 優化「組織與員工管理」介面**：
    * 於頂部快捷指令列整合 `➕ 新增員工帳號 (彈出 Modal)`、`🏢 新增/修改部門 (Modal)`、`🔑 修改我本人的帳密` 與 `🌳 組織與部門主管樹狀階層檢視` 切換鈕；並在組織樹狀圖每張部門主管卡片標題列新增 `🏢 修改部門` 快捷按鈕，點擊即可直接彈出 `openDepartmentEditModal` 編修該部門設定。

### 2.19 全系統安全登出機制 (`🚪 登出系統` / `🚪 登出`)
* **Web 介面全方位登出入口（任何畫面與捲動位置皆可一鍵安全登出）**：
    1. **頂部使用者資訊列 (`.topbar-user`)**：醒目紅色按鈕 `🚪 登出系統` (`href="/logout"` + `performSystemLogout(event)`)。
    2. **置頂懸浮主選單列 (`.top-menubar` Sticky Bar)**：
        * 右側位置狀態列 (`.current-location-bar`) 常駐顯示 `🚪 登出` 按鈕，當使用者向下捲動長表格時仍固定於畫面頂端隨時可按。
        * 「👥 組織與員工」（管理者）與「🔑 帳號設定」（一般參與者）下拉子選單內建 `🚪 4-3. 登出系統 (切換帳號)` 項目。
    3. **個人帳密設定頁面 (`#pane-myAccount` 與參與者 `#pane-employees`)**：卡片標題列右側提供 `🚪 登出系統` 按鈕。
    4. **獨立盲投 Session 網頁 (`/voting/session`)**：頂部 `<header>` 除「`🚪 結束參與並返回大廳`」外，直接提供「`🚪 登出系統`」按鈕，一鍵釋放該議程鎖定並登出返回登入頁。
* **後端與前端登出安全清理程序 (`GET/POST /logout` + `sqlDataService.releaseAllVotingSessionLeasesForEmployee`)**：
    1. 自動釋放該名員工在伺服器端持有的所有盲投 Session 活躍鎖定 (`releaseAllVotingSessionLeasesForEmployee(empPk)`)。
    2. 自動寫入系統稽核 Log (`storyPoint.tbLog`，物件名稱 `portalController.logout`) 記錄登出之員工姓名、帳號與 PK。
    3. 徹底清除所有登入驗證 Cookie (`sp_emp_pk`、`sp_emp_code`、`sp_emp_name`、`sp_emp_dept`、`sp_emp_role`、`sp_emp_login`)。
    4. 前端自動清除 `localStorage` 內所有 `sp_active_session_lock_*` 鎖定，並透過 `BroadcastChannel('sp_voting_session_guard')` 廣播 `USER_LOGGED_OUT` 訊息，使同瀏覽器開啟的其他獨立盲投分頁同步安全導回 `/login` 登入畫面。
* **PowerShell 多 Tab GUI 介面 (`ps/ui/uiMain.ps1`) 同步提供 `🚪 登出系統` 按鈕**：
    1. 於主視窗頂部導覽列 (`$topPanel`) 右上角「`🔑 修改我的帳密`」旁新增醒目紅色「`🚪 登出系統` (`$btnLogout`)」按鈕。
    2. 點擊「`🚪 登出系統`」時，自動將登出紀錄寫入 `storyPoint.tbLog` (`uiMain.logout`)，關閉目前主視窗並自動返回「員工帳號密碼登入對話框 (`showEmployeeLoginDialog`)」，支援隨時切換不同員工帳號重新登入或結束程式。

### 2.20 系統線上說明與操作畫面搜尋手冊 (`📖 線上說明` / `/?tab=help` / `GET /help` / `ps/ui/tabHelp.ps1`)
* **多管道線上說明入口**：
    1. **Web 主系統內嵌頁籤 (`/?tab=help` / `#pane-help`)**：
        * 於頂部使用者資訊列 (`.topbar-user`) 提供「`📖 線上說明`」按鈕。
        * 於頂部置頂主選單列 (`.top-menubar`) 新增「`📖 線上說明`」主選單與下拉快速章節導覽 (`6-1` ~ `6-8`)，並於右側位置狀態列 (`.current-location-bar`) 常駐提供「`📖 說明`」按鈕。
    2. **Web 獨立線上說明專頁 (`GET /help`，支援未登入與已登入存取)**：
        * 於員工登入頁 (`/login`) 與獨立盲投 Session 網頁 (`/voting/session`) 頂部皆提供「`📖 線上說明`」按鈕，支援於新分頁直接開啟 `/help`（亦支援 `/help?q=關鍵字&cat=分類&topic=主題ID` 參數直接定位）。
    3. **PowerShell 多 Tab GUI 線上說明頁籤 (`ps/ui/tabHelp.ps1` 與 `ps/ui/uiMain.ps1`)**：
        * 於 `ps/ui/uiMain.ps1` 頂部導覽列新增「`📖 線上說明`」按鈕，並新增第 6 個獨立頁籤「`6. 📖 線上說明與搜尋` (`tabHelp.ps1`)」，支援關鍵字即時搜尋、分類過濾、步驟詳解與右側實機操作畫面 (`PictureBox`) 預覽及點擊放大檢視。
* **全文關鍵字即時搜尋與高亮標示機制**：
    1. **多關鍵字即時檢索 (`#helpSearchInput`)**：支援輸入單一或多個空白分隔關鍵字（如 `盲投 自動翻牌`、`待參與 參與中`、`滑鼠右鍵 Modal`、`起訖時間 到期自動扣點`、`部門主管 上一層主管`、`表頭排序 欄位鎖定`），即時比對所有主題標題、步驟說明、操作畫面標註與常見問題 FAQ。
    2. **關鍵字黃底高亮度標示 (`<mark class="help-highlight">`)**：搜尋時自動將符合的關鍵字於標題、操作步驟與畫面標註文字中以醒目黃底高亮反白，並即時顯示符合的主題數與 FAQ 數。
    3. **分類篩選、角色篩選、熱門搜尋關鍵字標籤與可縮合章節目錄樹 (`TOC`) 及主題卡片**：
        * 支援依分類與適用角色過濾，提供熱門關鍵字一鍵搜尋標籤。
        * **多層次展開／縮合機制**：
            * 左側「`📑 線上說明章節目錄`」支援**整體側邊欄向左縮合隱藏 (`◀ 隱藏` / `◀ 縮合左側章節目錄`)** 以放大右側閱讀空間、**目錄清單上下縮合 (`🔼 縮合` / `🔽 展開`)**，以及**依功能分類資料夾展開／縮合 (`➕ 全展開` / `➖ 全縮合` 或單擊各分類標題)**。
            * 右側每個說明主題卡片 (`.help-topic-card`) 與常見問題 (`.help-faq-item`) 均支援**點擊標題列個別縮合／展開**，並於頂部提供「`🔽 全部展開主題` / `🔼 全部縮合主題`」與「`➕ 展開全部 FAQ` / `➖ 縮合全部 FAQ`」按鈕。
* **搭配 12 幅高解析度實機操作畫面與編號標註指引 (`wwwroot/help/images/screen_01_*.png` ~ `screen_12_*.png`)，並於首章詳盡說明 Story Point 的概念、用途、如何評估複雜度與讓團隊/個人工作量透明（不討論 Scrum 的 Sprint）**：
    1. **主題 00（認識 Story Point：核心概念、用途、如何評估複雜度與讓團隊／個人工作量透明）**：
        * **核心概念**：清楚說明 Story Point 是衡量任務客觀的「綜合相對複雜度」，而非估算「幾小時或幾天」，消除資深與資淺人員的速度落差爭論，且不需綁定或討論 Scrum 的 Sprint。
        * **如何評估複雜度（三大維度 ＋ 費氏數列評分尺規）**：詳列 `① 技術複雜度 (Complexity)`、`② 工作投入規模 (Effort / Volume)`、`③ 未知風險與不確定性 (Risk / Uncertainty)` 三大評估維度，並提供 `0.5~1 點 (極簡基準尺)`、`2~3 點 (標準常規)`、`5~8 點 (中高複雜度)`、`13~21+ 點 (大型高風險建議先拆解)`、`? (需求不明需釐清)` 視覺化對照表。
        * **核心用途（讓團隊與個人的工作量完全透明化）**：說明如何打破「只算工項件數（張數）」造成的勞逸不均盲點，透過 Story Point 乘上重要性權重 (`weightMultiplier`) 計算加權總分，於統計報表中即時透明呈現個人與各部門的「**進行中點數（當前在途負載）**」與「**已完成點數（累積實質產出貢獻）**」。
    2. 每個說明主題皆搭配對應之**繁體中文實機操作畫面截圖**（涵蓋：① 員工登入驗證與安全登出、② 自助修改個人帳密、③ 多人盲投大廳待參與/參與中分類、④ 獨立盲投 Session 綠色牌桌與自動翻牌/定案控制台、⑤ 設定盲投回合與牌面預覽、⑥ 估算過程與異動稽核查詢、⑦ 工項管理與起訖時間/到期自動扣點寄信、⑧ 表格滑鼠右鍵選單與雙擊單筆編輯 Modal、⑨ 工項代表類型與工項類型 CRUD、⑩ 員工/部門點數統計與表頭排序鎖定/三格式匯出、⑪ 組織部門主管樹狀階層與 CSV/Excel 批次匯入、⑫ 系統防崩潰 Log 查詢中心與測試）。
    3. 每幅操作畫面下方皆附帶**編號標註說明 (`①` `②` `③` `④`)** 與**畫面元件示意列**，點擊圖片或「`🔍 放大畫面`」按鈕即可開啟**全螢幕高解析度 Lightbox 放大檢視視窗 (`#spHelpScreenLightbox`)**（支援切換上一張/下一張操作畫面），並提供「`⚡ 立即切換至此功能畫面` / `🪟 直接彈出 Modal`」一鍵操作按鈕。

### 2.21 重新投票機制（支援「🔄 重啟新一輪盲投」與「🆕 全部重新開始（重設回第 1 回合並重新設定投票人等所有相關選項）」）
* **核心原則**：針對任何「已定案 (`finalized`)」、「已翻牌揭曉 (`revealed`)」或「盲投進行中 (`voting`)」之**工項代表類型 (`workItemType`)**、**特定工項 (`workItem`)** 與**盲投議程 (`votingSession`)**，系統除了支援**重啟新一輪盲投 (`nextRound` / `revote`)** 與**清空當前回合重投 (`resetCurrent` / `resetRound`)** 外，更完整支援「**`🆕 全部重新開始（重設回第 1 回合並重新設定所有相關選項）`** (`restartAllRounds` / `restartAll`)」，讓團隊可隨時清除過往回合紀錄、回到第 1 回合，並重新設定所有相關選項（包含：**投票人名單 `participantPks` / `participantPkCsv`**、**參與權限模式 `restrictToParticipant`**、**撲克牌組 `cardDeckPk`**、**允許出牌點數下限與上限 `minStoryPoint` / `maxStoryPoint`**、**允許 `?` 需求不明牌 / `☕` 休息牌**、**共識演算法 `consensusRule`（費氏無條件進位、最接近費氏、算術平均、中位數、眾數、嚴格全員一致）**、**最大點數分歧閾值 `maxAllowedSpread`**、**單回合倒數計時秒數 `roundTimeLimitSecond`**、**出牌理由填寫規定 `voteCommentPolicy`**、**全體投完自動翻牌 `autoRevealWhenAllVoted`**、**議程標題 `sessionTitle`** 與**DoD 估算基準備註 `roundGoalNote`**）。
* **五種重新投票與重設執行模式 (`revoteMode`)**：
    1. **`🆕 全部重新開始 (`restartAll`)`**：清除此議程過往所有回合與出牌紀錄，重設回 **第 1 回合 (`Round 1`)** 重新盲投，將已定案之代表類型或工項重設為「盲投進行中」，並套用重新設定之**投票人名單**與**所有盲投回合選項**。
    2. **`🔄 重啟新一輪盲投 (`nextRound`)`**：保留過往回合歷史紀錄，直接開啟下一輪新回合 (`Round N+1`) 並套用最新設定的投票人名單與所有選項。
    3. **`🔁 重設當前回合 (`resetCurrent`)`**：保持目前回合號不變，僅清空本回合已投出的撲克牌選票，並套用最新設定的投票人名單與所有選項。
    4. **`💾 僅更新設定與投票人 (`keepVotes`)`**：保留目前所有回合與已出牌紀錄，僅更新投票人名單與盲投選項設定。
    5. **`➕ 另建全新盲投議程 Session (`createNewSession`)`**：保留舊議程不動，針對該工項或代表類型另外建立一場全新編號 (`VOTE-*`) 的盲投議程。
* **Web 介面全方位「🔄 重啟新一輪」與「🆕 全部重新開始（重設投票人與所有選項）」操作入口**：
    1. **表格滑鼠右鍵操作選單 (`#spRowContextMenu`，加寬選單並強制右側徽章 `white-space: nowrap` 單行不斷字)**：
        * **工項 (`rowKind === 'workitem'`)**：
            * `🔄 重新投票：重啟新一輪盲投（保留歷史回合）`
            * `🆕 重新投票：全部重新開始（重設投票人與所有選項）` ➔ 彈出 `openSessionSetupEditModal` 並預選 `restartAll`（全部重新開始），可直接重新勾選投票人、部門快速圈選、自動勾選該工項負責員工，以及調整所有牌組與規則選項。
            * `⚙️ 進入「設定盲投回合」完整重設此工項所有投票選項` ➔ 切換至 `#pane-roundSetup` 並自動載入該工項對應議程與 `restartAll` 模式。
        * **工項代表類型 (`rowKind === 'workitemType'`)**：
            * `🔄 重新投票：重啟新一輪盲投（保留歷史回合）`
            * `🆕 重新投票：全部重新開始（重設投票人與所有選項）` ➔ 彈出 `openSessionSetupEditModal` 並預選 `restartAll`。
            * `⚙️ 進入「設定盲投回合」完整重設此代表類型所有選項`。
        * **盲投議程 (`rowKind === 'session'`)**：
            * `🔄 重新投票：重啟新一輪盲投（開啟下一回合）`
            * `🆕 重新投票：全部重新開始（重設投票人與所有選項）` ➔ 彈出 `openSessionSetupEditModal(sessionPk, { revoteMode: 'restartAll' })`。
            * `⚡ 快速全部重新開始（直接清空歷史重設回第 1 回合）`
            * `🔁 清空本回合已出牌紀錄並重新投票（保持同回合）`
            * `✏️ 單筆編輯此盲投議程與投票人設定（彈出 Modal）`
    2. **單筆編輯與盲投回合設定 Modal 視窗 (`openSessionSetupEditModal`、`openWorkItemEditModal`、`openWorkItemTypeEditModal`)**：
        * `openSessionSetupEditModal` 完整整合：
            * **🔄 重新投票 / 議程重設模式 (`revoteMode`)** 選擇器（`🆕 全部重新開始`、`🔄 重啟新一輪`、`🔁 重設當前回合`、`💾 僅更新設定與投票人`、`➕ 另建全新 Session`）。
            * **👥 重新設定參與盲投之投票人名單 (`participantPks`) 與權限模式 (`restrictToParticipant`)**：提供全體在職員工核取方塊清單、依部門批次全選/取消、全選/全不選、**📌 僅勾選此工項負責員工 (`modalSelectWorkItemAssignees`)**，以及即時統計已勾選投票人數徽章 (`#modalVoterCountBadge`)。
            * **🃏 撲克牌組、點數上下限、`?`/`☕` 特殊牌與即時有效撲克牌預覽列 (`#modalDeckPreviewBar`)**。
            * **⚖️ 全部 6 種共識決演算法、最大分歧閾值、單回合限時秒數、出牌理由規定與全體投完自動翻牌開關**。
        * `openWorkItemEditModal` 與 `openWorkItemTypeEditModal` 底部皆同時提供「**`🔄 重啟新一輪投票`**」與「**`🆕 全部重新開始 (重設投票人與選項)`**」按鈕。
    3. **即時盲投大廳 (`1-1` `#pane-voting`)、設定盲投回合 (`1-2` `#pane-roundSetup`)、估算過程查詢 (`1-3` `#pane-history`) 與獨立盲投網頁 (`/voting/session`)**：
        * 左側議程清單與大廳卡片同時提供「`🔄 重啟新一輪`」與「`🆕 全部重新開始 (重設選項)`」按鈕。
        * `1-2. 設定盲投回合` 內建「**🔄 重新投票與重設執行模式 (`#setupRevoteModeSelect`)**」，支援直接於主表單針對任何既有或已定案議程執行「🆕 全部重新開始（重設回第 1 回合並重新設定投票人等所有選項）」。
        * `1-3. 估算過程查詢` 議程卡片提供「`🔄 重啟新一輪`」、「`🆕 全部重新開始 (重設投票人與所有選項)`」與「`⚡ 快速全部重設回第 1 輪`」按鈕。
        * 獨立盲投 Session 網頁 (`/voting/session`) 主持人控制台除提供「`🔄 2. 開啟下一回合`」、「`🔁 3. 清空本輪重投`」、「`🆕 4. 全部重新開始 (重設回第 1 回合)`」按鈕外，更內嵌可展開之「**`🆕 重新投票並重新設定所有相關選項（投票人名單、權限模式、牌組、點數上下限、共識規則、計時...）`**」完整重設表單，主持人免離開盲投牌桌即可直接重設投票人與所有選項並從第 1 回合重新開始。
* **T-SQL 預存程序 (`storyPoint.spCreateVotingSession`、`storyPoint.spControlVotingSession`、`storyPoint.spGetStoryPointDashboard`)**：
    1. `storyPoint.spCreateVotingSession` 新增 `@restartAllRounds bit = 0` 與 `@openNextRound bit = 0` 參數：當 `@restartAllRounds = 1` 時，自動清除該議程既有之 `storyPoint.tbVoteRecord` 與 `storyPoint.tbVotingRound`，重建全新的第 1 回合 (`roundNumber = 1`)，將 `storyPoint.tbVotingSession` 重設為 `currentRoundNumber = 1, sessionStatus = 'voting', finalStoryPoint = null, finalizedAt = null`，同步更新所有回合設定參數與 `storyPoint.tbVotingSessionParticipant` 投票人名單，並將對應 `tbWorkItemType` 或 `tbWorkItem` 狀態重設回 `'voting'` 及寫入 `storyPoint.tbPointHistory` (`changeSource = 'revoteRestartAll'`)。
    2. `storyPoint.spControlVotingSession` 新增 `@controlAction = 'restartAll'`，支援一鍵將指定議程、代表類型或工項清除歷史回合並重設回第 1 回合重新盲投。
* **PowerShell Console (`ps/manageVotingSession.ps1`) 與 GUI (`ps/common.ps1`、`ps/ui/tabWorkItemType.ps1`、`ps/ui/tabWorkItem.ps1`、`ps/ui/tabVoting.ps1`) 同步支援**：
    1. `ps/manageVotingSession.ps1` 新增 `-actionType 'restartAllSession'` 及 `-restartAllRounds`、`-openNextRound` 參數。
    2. `ps/common.ps1` 之 `enableGridHeaderSortAndLock` 右鍵選單同時提供 `🔄 重新投票此筆紀錄（重啟新一輪多人盲投）` (`-onRevoteRow`) 與 `🆕 重新投票：全部重新開始（重設回第 1 回合）` (`-onRestartAllRow`)，且於盲投議程、代表類型與工項頁籤均提供對應按鈕與右鍵操作。

### 2.22 網站與桌面管理介面專屬圖示 (`favicon.ico` / `favorit.ico` / `favicon.svg` / `favicon.png`)
* **圖示視覺設計**：
    1. 採用「Story Point 估算 / Planning Poker 撲克牌」專屬主題設計：深藍至寶藍圓角漸層底座（`#0f172a` ➔ `#2563eb`）、後方金色盲投撲克牌與前方亮白主撲克牌（印有醒目藍色 `SP` 字樣與紅色菱形點數徽章）。
    2. 封裝為標準 32-bit ARGB 多解析度（`16x16`、`32x32`、`48x48`、`64x64`）圖示檔案，同時部署於 `web/storyPointPortal/wwwroot/favicon.ico`、`web/storyPointPortal/wwwroot/favorit.ico`、`web/storyPointPortal/wwwroot/favicon.svg` 與 `web/storyPointPortal/wwwroot/favicon.png`。
* **全系統頁面與桌面 GUI 套用**：
    1. Web 系統所有頁面（員工登入頁 `/login`、主系統各頁籤 `/`、獨立盲投 Session 網頁 `/voting/session`、重複進入攔截頁、獨立線上說明專頁 `/help`、匯出之互動式 HTML 報表及全域防當機保護頁面）之 `<head>` 皆嵌入 `<link rel="icon">` 與 `<link rel="shortcut icon">` 指向 `/favicon.ico`、`/favorit.ico` 與 `/favicon.svg`。
    2. PowerShell 多 Tab GUI (`ps/ui/uiMain.ps1`) 之主視窗、員工登入驗證視窗與個人帳密修改視窗同步套用 `favicon.ico` 作為視窗與工作列圖示。

### 2.23 刪除盲投回合與投票議程機制（`storyPoint.spDeleteVotingRound` / `storyPoint.spDeleteVotingSession`）
* **核心需求**：管理者可隨時刪除「**單一盲投回合 (`storyPoint.tbVotingRound`)**」或刪除「**整場盲投回合／投票議程設定 (`storyPoint.tbVotingSession`)**」：
    1. **刪除整場盲投回合／議程 (`storyPoint.spDeleteVotingSession`，`POST /voting/delete` 及 `POST /voting/session/delete`)**：
        * 刪除指定議程底下的所有出牌紀錄 (`storyPoint.tbVoteRecord`)、所有盲投回合 (`storyPoint.tbVotingRound`) 與受邀參與者名單 (`storyPoint.tbVotingSessionParticipant`)。
        * 保留已封存的點數異動歷史 (`storyPoint.tbPointHistory`) 但自動解除 `votingSessionPk` 外鍵關聯。
        * 自動同步更新關聯之工項代表類型 (`storyPoint.tbWorkItemType.lastVotingSessionPk` 與 `estimationStatus`) 及個別工項 (`storyPoint.tbWorkItem.estimationStatus`) 狀態，並釋放伺服器端活躍分頁租約。
    2. **刪除單一盲投回合 (`storyPoint.spDeleteVotingRound`，`POST /voting/round/delete`)**：
        * 支援依 `@votingRoundPk` 或 `@votingSessionPk + @roundNumber` 刪除特定盲投回合及其出牌紀錄 (`storyPoint.tbVoteRecord`)。
        * 若該議程僅剩唯一 1 個回合且 `@deleteSessionIfLastRound = 1`，自動轉呼叫 `storyPoint.spDeleteVotingSession` 一併刪除整場盲投議程。
        * 若該議程有多個回合，刪除指定回合後會以兩階段負數轉換法自動重整後續回合編號（避免觸發 `uqTbVotingRoundSessionRound` 唯一索引衝突），並同步更新 `storyPoint.tbVotingSession.currentRoundNumber` 與 `sessionStatus`。
* **全方位 UI 操作入口**：
    1. **表格滑鼠右鍵選單 (`#spRowContextMenu`)**：
        * 於「1-2. 設定盲投回合」下方的「📋 各投票議程的回合設定總覽」表格 (`rowKind === 'session'`) 按滑鼠右鍵，直接提供「`🗑️ 刪除當前回合（第 N 回合，退回上一回合）`」（多回合時顯示）與「`🗑️ 刪除此盲投回合／議程 (VOTE-*)`」紅色危險操作項。
        * 於「1-3. 查詢 Story Point 訂定歷程」各回合出牌明細列 (`rowKind === 'votingRoundVote'`) 按滑鼠右鍵，提供「`🗑️ 刪除此盲投回合（第 N 回合）`」與「`🗑️ 刪除整場盲投議程 (VOTE-*)`」。
    2. **單筆編輯與重設盲投回合 Modal (`openSessionSetupEditModal`)**：底部工具列於編輯既有議程 (`sPk > 0`) 時提供「`🗑️ 刪除此盲投回合／議程`」按鈕。
    3. **1-2. 設定盲投回合 (`#pane-roundSetup`)**：上方「選擇既有投票議程載入重設」選取任一既有議程時，表單底部顯示「`🗑️ 刪除此回合／議程` (`#setupDeleteSessionBtn`)」按鈕。
    4. **1-1. 多人即時盲投大廳 (`#pane-voting`)**：左側議程清單與大廳卡片均提供管理員專屬「`🗑️ 刪除回合`」按鈕。
    5. **1-3. 查詢 Story Point 訂定歷程 (`#pane-history`)**：每張議程卡片標題列提供「`🗑️ 刪除此議程`」按鈕，且每個回合區塊標題列右側直接提供「`🗑️ 刪除第 N 回合`」按鈕。
    6. **獨立盲投 Session 網頁 (`/voting/session`)**：主持人控制台提供「`🗑️ 6. 刪除本回合 (第 N 回合)`」與「`🗑️ 7. 刪除此盲投議程`」按鈕，且右側「📊 本 Session 歷次盲投回合統計」表格每一回合列皆提供「`🗑️ 刪除`」按鈕。
    7. **PowerShell Console (`ps/manageVotingSession.ps1`) 與多 Tab GUI (`ps/ui/tabVoting.ps1`)**：新增 `-actionType 'deleteRound'` 與 `'deleteSession'`，並於 GUI 提供「`🗑️ 刪除此回合/議程`」按鈕及表格右鍵刪除功能。

### 2.24 預設工項類型增、刪、修、查管理與表列工項類型主要特徵（`storyPoint.tbWorkItemKind`）
* **核心需求**：提供對「**預設工項類型 (`storyPoint.tbWorkItemKind`)**」的完整**增 (Create)、刪 (Delete)、修 (Update)、查 (Query/Filter)** 功能，並以結構化表格清楚**表列每一種工項類型的主要特徵**：
    1. **資料表結構擴充 (`storyPoint.tbWorkItemKind`)**：
        * 除原有之 `kindCode`（類型代碼）、`kindName`（預設類型名稱）、`badgeIcon`（圖示）、`badgeColor`（標籤主題色）、`sortOrder`（排序）、`description`（適用情境說明）外，無損新增三個結構化主要特徵欄位：
            * **`mainFeatureSummary` (`nvarchar(512)`)**：⭐ **主要特徵 (核心工作性質)**
            * **`estimationTrait` (`nvarchar(256)`)**：📐 **點數估算與計量特徵**
            * **`timeAndDeliverableTrait` (`nvarchar(256)`)**：⏰ **時程週期與交付/到期特徵**
    2. **五種預設工項類型與主要特徵定義（支援隨時新增、修改、刪除與查詢自訂類型）**：
        | 排序 | 類型代碼 | 預設工項類型 | ⭐ 主要特徵 (核心工作性質) | 📐 點數估算與計量特徵 | ⏰ 時程週期與交付/到期特徵 | 📋 適用情境說明 |
        | :---: | :--- | :--- | :--- | :--- | :--- | :--- |
        | 10 | `project` | 🏗️ **專案** | 目標導向、具明確範疇與里程碑之新功能開發或系統建置 | 依功能複雜度、跨模組整合難度進行團隊盲投估算 (通常 3 ~ 21 pt) | 具明確專案起訖日與階段驗收交付物，達迄時自動轉態扣點並通知主管與 HR | 客戶導入專案、系統全新功能開發或跨部門里程碑交付工項 |
        | 20 | `maintenance` | 🛠️ **維運** | 穩定度導向、既有系統問題排除、例行巡檢、效能調校與安全性修補 | 依問題排查深度與影響範圍套用維運代表類型標準點數 (通常 0.5 ~ 5 pt) | 具 SLA 處理時限與改善報告，達迄時自動結案扣點並寄送通知 | 既有系統日常維運、例外錯誤修復、資料庫調校與例行巡檢工項 |
        | 30 | `consultingHour` | ⏳ **顧問時數** | 專業知識輸出導向、架構諮詢、技術評估、POC 驗證與客戶駐點支援 | 依投入時數區間與專業深度換算標準 Story Point (通常 1 ~ 8 pt) | 依約定諮詢區間設定起訖時間並產出顧問建議書或會議紀錄，達迄時自動扣點 | 提供內部或外部客戶技術架構諮詢、POC 評估、問題診斷與顧問服務時數工項 |
        | 40 | `training` | 🎓 **教育訓練** | 知識傳承與賦能導向、教材編撰、實機 Lab 演練、內訓與客戶操作培訓 | 依課程時數、教材深度與實機環境準備複雜度估算點數 (通常 1 ~ 5 pt) | 以開課/受訓起訖時程為準，交付簡報教材與課後測驗，達迄時自動扣點歸檔 | 內部技術傳承講座、新人培訓課程、教材編撰與客戶端系統操作教育訓練工項 |
        | 50 | `productDevelopment` | 🚀 **開發產品** | 產品化與長期演進導向、自有核心模組研發、通用架構設計與高重用性元件開發 | 依架構前瞻性、高可用度要求與自動化測試覆蓋率進行盲投估算 (通常 5 ~ 21 pt) | 配合產品 Roadmap 與版本發佈週期設定起訖時間，達迄時自動轉態扣點並發信 | 自有核心產品研發、新世代架構演進、共用引擎模組與產品路線圖功能開發工項 |
    3. **全方位 Web 介面與 Modal 整合入口**：
        * **主選單「📋 工項管理」子選單 `🗂️ 2-3. 預設工項類型與特徵`** 與 **`🏷️ 2-2. 工項代表類型` (`/?tab=types`)**：直接置頂顯示「**🗂️ 預設工項類型增、刪、修、查管理與主要特徵一覽表 (`#wikCrudCard_types`)**」（不隱藏於收合抽屜），提供關鍵字即時查詢 (`#filterWikKeyword_types`)、標籤顏色篩選 (`#filterWikColor_types`)、套用狀態篩選 (`#filterWikUsage_types`)、新增/修改表單、主要特徵對照表 (`#wikDataTable_types`) 以及每一列直接操作按鈕（`🏷️ 查代表類型`、`📋 查工項`、`✏️ 修改`、`🗑️ 刪除`）與滑鼠雙擊/右鍵選單。
        * **「✏️ 單筆編輯工項代表類型 (`tbWorkItemType`)」Modal 視窗 (`openWorkItemTypeEditModal`) 與「➕ 新增/編輯工項」Modal 視窗 (`openWorkItemEditModal`)**：
            * 在「`預設工項類型`」下拉選單旁直接提供 `➕ 新增`、`✏️ 修改`、`🗑️ 刪除`、`📋 特徵表` 快捷按鈕。
            * 當選擇不同預設工項類型時，下方即時呈現該預設工項類型的**主要特徵摘要卡 (`#modalTypeKindFeatureBanner`)**。
            * 視窗內直接內嵌「**📋 預設工項類型主要特徵一覽表 (`#modalTypeKindFeatureTableWrap`)**」，表列所有預設工項類型與其三大主要特徵，並支援在視窗內一鍵 `✅ 選用`、`✏️ 修改`、`🗑️ 刪除` 或 `➕ 新增預設工項類型`。
    4. **PowerShell Console (`ps/manageWorkItem.ps1`) 與多 Tab GUI (`ps/ui/tabWorkItemType.ps1`) 同步支援**：
        * `ps/manageWorkItem.ps1` 支援 `-mainFeatureSummary`、`-estimationTrait`、`-timeAndDeliverableTrait` 及 `-keyword` 參數。
        * `ps/ui/tabWorkItemType.ps1` 上半部直接呈現「🗂️ 一、預設工項類型增、刪、修、查管理與主要特徵一覽表 (`tbWorkItemKind`)」，支援完整增刪修查與表列主要特徵。

### 2.25 各盲投 Session 參與者可退出「待參與」與「參與中」的 Session，退出後該 Session 就不再參與該工項的投票（`storyPoint.spWithdrawVotingSession`），且全面改以滑鼠右鍵快捷選單提供操作功能（不使用佔空間按鈕）
* **核心需求**：
    1. 各盲投 Session 參與者（不論一般參與者或管理員/主持人）皆能隨時退出位於「**📂 待參與**」與「**🟢 參與中**」的盲投 Session，退出後該參與者與該 Session 就不再參與該工項（或工項代表類型）的投票。
    2. **全面改用滑鼠右鍵的快捷選單 (`contextmenu` ➔ `#spRowContextMenu`) 提供這些功能，不使用佔用大量畫面空間的卡片內嵌按鈕**：
        * 移除左側邊欄「📌 投票議程清單」（`📂 待參與`、`🟢 參與中`、`📁 已拍板定案議程`）、右側盲投大廳議程卡片 (`#lobbyGridPending`、`#lobbyGridParticipating`)、投票歷程查詢頁籤 (`#pane-history`) 以及表格列內佔空間的大量操作按鈕（如 `🚪 退出`、`🚀 點選進入「參與中」獨立網頁`、`🚪 退出 Session (不再參與投票)`、`↩️ 暫離移回待參與`、`🔄 重啟新一輪`、`🆕 全部重新開始(重設選項)`、`⚙️ 完整設定頁`、`🗑️ 刪除回合` 等），讓卡片與表格版面保持精簡清爽。
        * 在任何盲投 Session 卡片（`.session-item-card`、`.lobby-session-card`、`.sp-context-card`）、獨立盲投網頁牌桌或表格資料列上**按下滑鼠右鍵 (`contextmenu`)**，立即彈出智慧快捷選單 (`#spRowContextMenu`)，集中提供：
            * `🚀 點選進入「參與中」獨立盲投網頁`（或 `🟢 參與中（已在獨立網頁進行中・點擊聚焦或提醒）`／`🔍 檢視此已定案 Session 獨立盲投網頁`）
            * `↩️ 暫離「參與中」獨立網頁（移回「📂 待參與」清單，保留投票資格）`（當該 Session 目前處於本人「🟢 參與中」時動態顯示）
            * `🚪 退出此盲投 Session（不再參與該工項投票）`（當該 Session 為「📂 待參與」或「🟢 參與中」未定案狀態時顯示）
            * `📋 展開／收合左側邊欄此議程詳細資訊`
            * 管理者專屬右鍵項目：`✏️ 單筆編輯此盲投議程回合設定`、`🔄 重新投票 — 重啟新一輪盲投`、`🆕 重新投票 — 全部重新開始（重設選項）`、`🔁 重新投票 — 清空本回合已出牌紀錄`、`⚙️ 前往「1-2. 設定盲投回合」完整頁面`、`🗑️ 刪除當前回合`、`🗑️ 刪除此盲投回合／議程`。
        * 同時支援在議程卡片上**滑鼠左鍵雙擊 (`dblclick`)** 直接進入該場「🟢 參與中」獨立盲投網頁。
    3. **預存程序 `storyPoint.spWithdrawVotingSession` 與 `storyPoint.spGetVotingProcessHistory`**：
        * 參與者執行退出時，將其從該盲投議程之受邀參與者名單 (`storyPoint.tbVotingSessionParticipant`) 移除（若原議程為全體員工模式 `restrictToParticipant = 0`，自動先將其他在職同仁寫入名單並設為 `restrictToParticipant = 1`），並清除該參與者於本場議程已投出的選票紀錄 (`storyPoint.tbVoteRecord`)，重新計算各回合出牌人數與平均/最低/最高點數。
        * `storyPoint.spGetVotingProcessHistory` 輸出 `isViewerParticipant` 欄位；一旦參與者退出該 Session，該 Session 立即從其「**📂 待參與**」與「**🟢 參與中**」清單中徹底移除，不再參與該工項的投票。
        * **剩餘參與者自動翻牌檢核**：若退出後該 Session 仍有其他剩餘參與者 (`@remainingParticipantCount > 0`)，且剩餘參與者皆已完成出牌，系統立即自動觸發翻牌揭曉 (`sessionStatus = 'revealed'`)。
        * **無剩餘參與者自動結束清理**：若該 Session 所有參與者皆已退出 (`@remainingParticipantCount <= 0`)，系統自動結束並移除該筆盲投 Session，同步復原關聯之工項 (`storyPoint.tbWorkItem.estimationStatus`) 或工項代表類型 (`storyPoint.tbWorkItemType.estimationStatus`) 狀態，使該 Session 不再參與該工項的投票。
    4. **PowerShell Console (`ps/manageVotingSession.ps1`) 與多 Tab GUI (`ps/ui/tabVoting.ps1` / `ps/common.ps1`)**：
        * `ps/manageVotingSession.ps1` 支援 `-actionType 'withdrawSession'`。
        * `ps/ui/tabVoting.ps1` 與 `ps/common.ps1` 之 `enableGridHeaderSortAndLock` 將「`🚪 退出此盲投 Session（不再參與該工項投票）`」、「`🔄 重新投票此筆紀錄`」、「`🆕 重新投票：全部重新開始`」與「`🗑️ 刪除此筆紀錄`」整合於 `DataGridView` 滑鼠右鍵快捷選單 (`ContextMenuStrip`)，移除頂部佔空間的操作按鈕。

### 2.26 提供多種代表圖示的 Icon 庫，並支援使用者自行上傳新增代表圖示（`storyPoint.tbWorkItemIcon`、`storyPoint.spUpsertWorkItemIcon`、`storyPoint.spDeleteWorkItemIcon`）
* **核心需求**：於預設工項類型 (`storyPoint.tbWorkItemKind.badgeIcon`) 與工項代表類型管理中，提供豐富的**多種代表圖示 (Icon)** 供選擇，並支援使用者**自行上傳新增自訂代表圖示**：
    1. **代表圖示庫資料表 (`storyPoint.tbWorkItemIcon`) 與無損保留機制**：
        * 將 `storyPoint.tbWorkItemKind.badgeIcon` 擴充為 `nvarchar(512)`，同時相容 Emoji 符號與自訂/預設圖示路徑 (`/icons/presets/*.svg`、`/icons/custom/*`)。
        * 建立 `storyPoint.tbWorkItemIcon` 資料表（搭配 `storyPoint.tbPreserveWorkItemIcon` 無損升級保留），儲存圖示代碼 (`iconCode`)、圖示名稱 (`iconName`)、分類 (`iconCategory`)、圖示類型 (`iconKind`: `emoji` / `image`)、圖示值 (`iconValue`)、MIME 類型 (`contentType`)、Base64 編碼內容 (`iconDataBase64`) 與自訂上傳標記 (`isCustomUpload`)。
        * 內建 **36 種多元代表圖示**：包含 **12 款高解析度彩色向量 SVG 圖示**（專案藍圖、維運齒輪、顧問時鐘、教育學位帽、產品火箭、資料庫主機、雲端架構、資安防護盾、商業智慧分析、AI 智慧核心、API 系統整合、客服駐點耳機，存放於 `wwwroot/icons/presets/*.svg`）以及 **24 種分類 Emoji 代表符號**（專案與建置、維運與支援、顧問與會議、培訓與知識、產品與研發、雲端與資料、商務與管理）。
        * 建立預存程序 `storyPoint.spUpsertWorkItemIcon` 與 `storyPoint.spDeleteWorkItemIcon`，並於 `storyPoint.spGetStoryPointDashboard` 新增第 10 個結果集回傳 `storyPoint.tbWorkItemIcon` 代表圖示清單。
    2. **Web 介面代表圖示選擇器 (`🎨 圖示庫`) 與自行上傳新增 (`📤 上傳新增`)**：
        * 於「預設工項類型管理卡片 (`#wikCrudCard_types`)」與「單筆新增/編輯預設工項類型 Modal (`openWorkItemKindEditModal`)」的「**代表圖示**」欄位提供：
            * **即時圖示預覽框 (`.sp-icon-preview-box`)**：即時顯示目前選用的彩色 SVG/PNG 圖片圖示或 Emoji。
            * **分類下拉快速選單 (`#wikIconSelect_*`)**：依「彩色向量圖示」、「自訂上傳圖示」與各業務分類分組列出所有代表圖示供一鍵切換。
            * **展開式視覺化代表圖示庫 (`🎨 圖示庫` / `#wikIconGalleryPanel_*`)**：提供分類篩選頁籤（「全部圖示」、「🖼️ 彩色向量圖示 (SVG)」、「📤 自訂上傳圖示」、「😀 符號與 Emoji」）與視覺化圖示格線磚 (`.sp-icon-tile`)，點擊任一圖示立即套用。
            * **自行上傳新增代表圖示 (`📤 上傳新增` / `POST /workitem/icon/upload`)**：支援上傳 `.png`、`.svg`、`.jpg`、`.jpeg`、`.webp`、`.ico`、`.gif` 圖檔（最大 2 MB），上傳後同時儲存至 `wwwroot/icons/custom/` 與 SQL Server `storyPoint.tbWorkItemIcon`（含 Base64 持久化備援與 `GET /icons/custom/{fileName}` 自動還原機制），並立即加入圖示庫與自動套用至目前表單；自訂上傳圖示亦支援點擊右上角 `✖` 刪除 (`POST /workitem/icon/delete`)。
        * 全站所有工項類型徽章、工項代表類型表格、實際工項列表、Modal 彈窗與主要特徵比較表均自動辨識圖片圖示路徑與 Emoji，渲染清晰對齊的 `<img class="sp-kind-icon-img" />` 或符號徽章。
    3. **PowerShell Console (`ps/manageWorkItem.ps1`) 與多 Tab GUI (`ps/ui/tabWorkItemType.ps1`) 同步支援**：
        * `ps/manageWorkItem.ps1` 新增 `-actionType 'listWorkItemIcon'`、`'uploadWorkItemIcon'`、`'deleteWorkItemIcon'`，且 `'upsertWorkItemKind'` 支援 `-iconFilePath` 參數直接上傳圖示檔案並套用。
        * `ps/ui/tabWorkItemType.ps1` 將圖示欄位升級為可下拉選擇多種代表圖示的 `ComboBox` (`$cmbKindIcon`)，並新增「`📤 上傳圖示`」按鈕 (`OpenFileDialog`) 供使用者自行選擇圖檔上傳新增。

### 2.27 「目標工項代表類型」全面帶入專案、維運、顧問時數、教育訓練、開發產品...等工項類型（`storyPoint.tbWorkItemType` 與 `storyPoint.tbWorkItemKind` 雙向同步）
* **核心需求**：「**1-2. 設定盲投回合**」與盲投回合設定 Modal 視窗的「**目標工項代表類型 (`workItemTypePk`)**」下拉選單，以及工項管理中的代表類型清單，必須帶入「**專案 (`project`)、維運 (`maintenance`)、顧問時數 (`consultingHour`)、教育訓練 (`training`)、開發產品 (`productDevelopment`)...等**」工項類型（包含於預設工項類型管理新增或修改之自訂類型），且不得將盲投議程標題誤自動建立為 `TYPE_2026...` 代表類型：
    1. **資料庫雙向同步與種子資料正規化 (`sql/seedData.sql`、`sql/storedProcedure.sql`)**：
        * `sql/seedData.sql`（Section 4.1）與 `storyPoint.spGetStoryPointDashboard` 自動將 `storyPoint.tbWorkItemKind` 定義之 `project`（🏗️ 專案）、`maintenance`（🛠️ 維運）、`consultingHour`（⏳ 顧問時數）、`training`（🎓 教育訓練）、`productDevelopment`（🚀 開發產品）及所有自訂工項類型完整同步至 `storyPoint.tbWorkItemType`，並依 `sortOrder` (`10, 20, 30, 40, 50...`) 排序輸出。
        * 自動清理舊版誤由盲投議程標題產生的 `TYPE_20%` / `TYPE_DEF_%` 紀錄，並將原本關聯之 `tbWorkItem`、`tbVotingSession`、`tbPointHistory` 安全對應至「專案、維運、顧問時數、教育訓練、開發產品」代表類型。
        * `storyPoint.spUpsertWorkItemKind`、`storyPoint.spDeleteWorkItemKind` 與 `storyPoint.spUpsertWorkItemType` 雙向保持 `tbWorkItemKind` 與 `tbWorkItemType` 同步；新增、修改或刪除任何工項類型時，「目標工項代表類型」選單即自動同步更新。
        * `storyPoint.spCreateVotingSession` 移除以 `@sessionTitle` 自動新增 `TYPE_yyyyMMddHHmmss` 之邏輯，確保發起或重設盲投回合時直接綁定所選之工項代表類型（專案、維運、顧問時數、教育訓練、開發產品...等）。
    2. **Web 介面與右鍵選單整合 (`web/storyPointPortal/controller/portalController.cs`)**：
        * 「1-2. 設定盲投回合 (`#setupWorkItemTypePk`)」與「快速發起/重設盲投議程 Modal (`#modalSetupWorkItemTypePk`)」的「**目標工項代表類型**」下拉選單直接帶入：`🏗️ 專案 (project)`、`🛠️ 維運 (maintenance)`、`⏳ 顧問時數 (consultingHour)`、`🎓 教育訓練 (training)`、`🚀 開發產品 (productDevelopment)` 等項目及其目前標準點數。
        * 於「預設工項類型 (`rowKind === 'workitemKind'`)」表格列按滑鼠右鍵，直接提供「`🃏 針對此工項代表類型發起多人盲投估算`」與「`⚙️ 前往「1-2. 設定盲投回合」設定此代表類型`」快捷操作。

### 2.28 選單與功能明確分離：「2-2. 工項代表類型」專責編輯投出 SP 的代表工項各項屬性與所屬預設類型；「2-3. 預設工項類型與特徵」獨立專頁管理預設工項類型與主要特徵
* **核心需求**：「**2-2. 工項代表類型**」與「**2-3. 預設工項類型與特徵**」兩項子選單必須各自獨立運作且職責明確分離：
    1. **「🏷️ 2-2. 工項代表類型 (`/?tab=types` / `#pane-types`)」— 專責編輯投出 SP 的代表工項（例如：「資料庫健檢」）之各種屬性與屬於哪種預設工項類型**：
        * 頁面直接置頂呈現「**🏷️ 編輯投出 Story Point 的代表工項屬性與所屬預設工項類型 (`#typeAttributeEditorCard`)**」完整編輯面板（不將預設工項類型 CRUD 混入 2-2，且預設自動載入「資料庫健檢」等已投出 SP 之代表工項供直接檢視與修改）。
        * 提供頂部「**⚡ 點擊快速切換要編輯的代表工項**」按鈕列 (`.quick-type-pill-btn`) 與下拉選單 (`#quickTypeEditSelector`)，點擊上方按鈕或下方「工項代表類型一覽表 (`#workItemTypeDataTable`)」任一列即同步載入該代表工項之完整屬性：
            1. **代表工項名稱 (`typeName`)**：如 `資料庫健檢`、`資料庫效能調校` 等。
            2. **屬於哪種預設工項類型 (`defaultWorkItemKind`)**：明確設定該代表工項歸屬於哪一種預設工項類型（`🏗️ 專案 project`、`🛠️ 維運 maintenance`、`⏳ 顧問時數 consultingHour`、`🎓 教育訓練 training`、`🚀 開發產品 productDevelopment` 或自訂類型），切換時即時顯示該預設工項類型的三大主要特徵預覽橫幅 (`#mainTypeKindFeatureBanner`)。
            3. **技術 / 業務分類屬性 (`categoryName`)**：設定代表工項之專業領域或業務分類屬性（如 `資料庫健檢與維運`、`資料平台架構` 等，附帶常用分類 `datalist`）。
            4. **投出 / 核定標準 Story Point (`standardStoryPoint`)**：編輯盲投核定或手動設定之標準點數（如 `8 pt`）。
            5. **代表工項代碼 (`typeCode`)**：管理唯一識別代碼（如 `TYPE_20261023034516`）。
            6. **預設盲投撲克牌組 (`defaultCardDeckPk`)**：設定針對該代表工項發起重新盲投時預設採用的撲克牌組。
            7. **工作內容、複雜度與驗收標準屬性說明 (`description` / DoD)**：詳述該代表工項的執行步驟、複雜度評估基準與交付驗收條件（如 `透過 script 收集狀態，分析健康狀態，提供健檢報告並完成現場或線上報告`）。
            8. **同步更新關聯未結案工項 (`syncExistingOpenWorkItem`)**：勾選並儲存時，`storyPoint.spUpsertWorkItemType` 自動將最新設定的「所屬預設工項類型 (`workItemKind`)」與「標準點數 (`storyPoint`)」同步更新至所有套用此代表工項且尚未結案之實際工項 (`storyPoint.tbWorkItem`) 與負責員工配額 (`storyPoint.tbWorkItemAssignment`)。
        * 下方「📋 已投出 / 待估算之代表工項與標準 Story Point 一覽表 (`#workItemTypeDataTable`)」每一列直接提供「`✏️ 載入表單編輯`」、「`🪟 彈出編輯`」、「`🃏 重新盲投`」與「`🗑️ 刪除`」按鈕，並支援滑鼠左鍵單擊載入上方屬性編輯器、雙擊彈出 Modal 及滑鼠右鍵快捷選單。
    2. **「🗂️ 2-3. 預設工項類型與特徵 (`/?tab=kinds` / `#pane-kinds`)」— 獨立選單與專屬頁面**：
        * 點擊頂部主選單「📋 工項管理 ▾」下的「**`🗂️ 2-3. 預設工項類型與特徵`**」(`switchTab('kinds')`) 時，立即切換至獨立分頁 `#pane-kinds`（網址更新為 `/?tab=kinds`、選單項目 `2-3` 高亮反白、右上角目前位置徽章顯示 `🗂️ 2-3. 預設工項類型與特徵`）。
        * 頁面完整呈現「**🗂️ 預設工項類型增、刪、修、查管理與主要特徵一覽表 (`#wikCrudCard_kinds`)**」（管理 `storyPoint.tbWorkItemKind` 之專案、維運、顧問時數、教育訓練、開發產品與自訂類型、36 種代表圖示庫與自訂上傳圖示、以及三大主要特徵欄位），並提供一鍵切換回「`🏷️ 前往 2-2. 工項代表類型（編輯投出 SP 的代表工項屬性）`」之導覽按鈕。
    3. **發起新盲投議程時自動保護既有代表工項不被誤覆寫 (`storyPoint.spCreateVotingSession`)**：
        * 當發起新的代表工項盲投議程 (`@sessionPk <= 0` 且非重投既有議程) 時，若盲投議程標題描述的是新的代表工項名稱（與下拉選單預設值不同），系統自動匹配或建立專屬之 `storyPoint.tbWorkItemType` 代表工項紀錄，確保既有已定案之代表工項（如 `資料庫健檢 8.00 pt`）不會因新建其他盲投議程而被誤洗掉點數或狀態。

### 2.29 三層工項架構與職責分工（`3-3 工項類型與基本規範` ➔ `3-2 基本工項管理` ➔ `3-1 一般工項與員工指派`）、多人盲投回合設定連動與線上說明手冊同步更新
* **核心架構與三層責任分工**：
    1. **3-3. 工項類型與基本規範 (`storyPoint.tbWorkItemKind`，`/?tab=kinds` / `#pane-kinds`)**：
        * 專門編輯與管理「**工項類型**」（如：`🏗️ 專案 project`、`🛠️ 維運 maintenance`、`⏳ 顧問時數 consultingHour`、`🎓 教育訓練 training`、`🚀 開發產品 productDevelopment` 及自訂類型）。
        * 資料表 `storyPoint.tbWorkItemKind` 新增 `minStoryPoint decimal(8,2) not null default(0.50)` 與 `maxStoryPoint decimal(8,2) not null default(21.00)` 欄位，明確定義每一種工項類型的「**一般工項基本規範 (`mainFeatureSummary` / `estimationTrait` / `timeAndDeliverableTrait` / `description`)**」以及「**一般 Story Point 範圍上下限 (`minStoryPoint` ~ `maxStoryPoint`)**」：
            * `🏗️ 專案 (project)`：一般 SP 範圍 **`3 ~ 21 pt`**
            * `🛠️ 維運 (maintenance)`：一般 SP 範圍 **`0.5 ~ 5 pt`**
            * `⏳ 顧問時數 (consultingHour)`：一般 SP 範圍 **`1 ~ 8 pt`**
            * `🎓 教育訓練 (training)`：一般 SP 範圍 **`1 ~ 8 pt`**
            * `🚀 開發產品 (productDevelopment)`：一般 SP 範圍 **`3 ~ 21 pt`**
    2. **3-2. 基本工項管理 (`storyPoint.tbWorkItemType`，`/?tab=types` / `#pane-types`)**：
        * 專門編輯與管理「**基本工項**」（如：`資料庫健檢`、`資料庫效能調校`、`標準 API 與資料模組開發`、`客戶架構診斷與技術諮詢`、`SQL Server 實機培訓課程`、`核心產品通用模組研發` 等）。
        * **基本工項沒有起訖時間 (`startDate` / `endDate`)**。
        * **基本工項可以套用「3-3 工項類型」**（如套用「專案」或「維運」），套用後自動顯示該類型的一般工項基本規範，並提示該類型的一般 Story Point 範圍 (`minStoryPoint ~ maxStoryPoint pt`)。
        * **多人盲投（投票）主要是投「基本工項」**，投出共識後成為該基本工項的標準 Story Point。
    3. **3-1. 一般工項與員工指派 (`storyPoint.tbWorkItem`，`/?tab=workitems` / `#pane-workitems`)**：
        * 專門編輯與管理「**一般工項**」（如：`集保健檢`）。
        * **只有一般工項才有起訖時間 (`startDate ~ endDate`)**、負責員工指派與到期自動轉態/扣點/寄信。
        * 建立或編輯一般工項（如 `集保健檢`）時，選擇「**基本工項（如：資料庫健檢）**」即自動帶入其所屬「**工項類型**」與「**標準 Story Point**」；使用者仍可針對該一般工項**微調 Story Point**（手動修改點數則標記 `isManualAdjusted = 1`「已微調」，未來基本工項重新投票定案時不會覆蓋已微調的一般工項）。
* **多人盲投回合設定 (`2. ⚙️ 設定回合` / `1-2. 設定盲投回合` 與快速發起盲投 Modal)**：
    1. 投票主要投「**基本工項 (`workItemType`)**」。
    2. 先選「**目標工項代表類型 / 工項類型（專案、維運、顧問時數、教育訓練、開發產品...）**」➔ 自動帶出該類型的**一般工項基本規範**與**一般 SP 範圍 (`minStoryPoint ~ maxStoryPoint pt`)**（並自動帶入本回合的最低/最高允許點數 `minStoryPoint` / `maxStoryPoint`），接著再選擇或輸入要投票的「**目標基本工項（如：資料庫健檢）**」。
* **主選單編號 (`1 ~ 8`) 與線上說明手冊 (`8. 📖 線上說明` / `/help`) 全面同步更新**：
    1. 頂部主選單統一編號為：`1. 🃏 多人估算 (1-1 ~ 1-3)`、`2. ⚙️ 設定回合`、`3. 📋 工項管理 (3-1 一般工項、3-2 基本工項、3-3 工項類型與基本規範、3-4 ~ 3-6 匯出)`、`4. 📊 統計報表 (4-1 ~ 4-3)`、`5. 🔑 修改帳密`、`6. 👥 組織與員工 (6-1 ~ 6-3)`、`7. 🛡️ 系統 Log (7-1 ~ 7-2)`、`8. 📖 線上說明 (8-1 ~ 8-8)`。
    2. 線上說明中心 (`#pane-help` 與 `/help`) 同步更新所有章節標題、選單編號、三層工項架構圖解、熱門搜尋關鍵字標籤與常見問題 FAQ (`Q0-3`)。

### 2.30 表格 (Table) 選取資料列按鍵盤 `Delete` 鍵確認並刪除紀錄機制
* **操作行為規範**：
    1. **選取資料列 (`table tbody tr`)**：在全站任何表格（包含 `3-1. 一般工項與員工指派 #workItemDataTable`、`3-2. 基本工項管理 #workItemTypeDataTable`、`3-3. 工項類型與基本規範 #wikDataTable_*`、`1-2. 設定盲投回合 #roundSetupSessionTable`、`1-3. 估算過程查詢` 之盲投回合/出牌明細與 `tbPointHistory` 點數異動稽核歷程表、`4-1. 員工與部門統計`、`6-1. 員工與組織管理 #empDataTable`、`7-1. 系統 Log 查詢 #sysLogDataTable`、Modal 內之類型規範表，以及獨立盲投視窗 `/voting/session` 之回合統計表）中，以滑鼠左鍵單擊或右鍵選取某筆資料列 (`row`) 時，該列會維持高亮選取狀態 (`.sp-context-active-row`)，並自動解除表單輸入框焦點 (`blur`)，確保鍵盤事件直接作用於選取列。
    2. **按下鍵盤 `Delete` 鍵觸發確認並刪除**：當表格內已選取某筆資料列（且游標不在 `<input>`、`<textarea>`、`<select>` 輸入狀態）時，按下鍵盤 `Delete` (`Del` / `keyCode 46`) 鍵，系統立即針對該筆紀錄彈出 `window.confirm(...)` 確認對話框；若使用者按下「確定」(`confirm === true`)，立即送出刪除請求並從資料庫刪除該筆紀錄（刪除後自動停留在原操作分頁 `returnTab`）：
        * 基本工項 (`workitemType`) ➔ 呼叫 `/workitem/type/delete` (`storyPoint.spDeleteWorkItemType`)。
        * 一般工項 (`workitem`) ➔ 呼叫 `/workitem/delete` (`storyPoint.spDeleteWorkItem`)。
        * 工項類型 (`workitemKind`) ➔ 呼叫 `/workitem/kind/delete` (`storyPoint.spDeleteWorkItemKind`，自動移轉備援類型並至少保留 1 筆)。
        * 員工帳號 (`employee`) ➔ 呼叫 `/employee/delete` (`storyPoint.spDeleteEmployee`，保護目前登入本人不可自刪)。
        * 組織部門 (`department`) ➔ 呼叫 `/employee/department/delete` (`storyPoint.spDeleteDepartment`，自動將所屬員工移轉至備援部門)。
        * 盲投議程 (`session`) 與盲投回合 (`votingRound` / `votingRoundVote`) ➔ 呼叫 `/voting/delete` (`storyPoint.spDeleteVotingSession`) 或 `/voting/round/delete` (`storyPoint.spDeleteVotingRound`)。
        * 點數異動稽核歷程 (`pointHistory`) 與系統 Log (`systemLog`) ➔ 呼叫 `/history/delete` (`storyPoint.spDeletePointHistory`) 或 `/admin/log/delete` (`storyPoint.spDeleteSystemLog`)。
    3. **PowerShell 多 Tab GUI (`ps/common.ps1` `enableGridHeaderSortAndLock`) 同步支援**：在 `DataGridView` 選取任一資料列按下鍵盤 `Delete` 鍵時，同步觸發 `$onDeleteRow` 詢問確認並執行刪除。

### 2.31 新增基本工項與一般工項盲投議程之參照設定規範（新建立與投基本工項不需要參考既有基本工項，只要參照類型；一般工項才設定要參照的基本工項）
* **核心需求**：
    1. **新建立與投「基本工項 (`targetKind = 'workItemType'`)」時，不需要參考既有基本工項，只要設定「參照工項類型 (`defaultWorkItemKind`，如：專案、維運、顧問時數、教育訓練、開發產品...)」**：
        * 於「1-2. 設定盲投回合 (`#pane-roundSetup`)」與「快速發起/重設盲投議程 Modal (`openSessionSetupEditModal`)」中，當估算標的類型選擇「**🏷️ 投『基本工項』**」時：
            * **不顯示「目標基本工項」下拉選單**（新建立與投基本工項不需要參考既有基本工項，`workItemTypePk` 設為隱藏欄位 `<input type="hidden">`）。
            * 僅需選擇「**參照工項類型 (專案、維運、顧問時數、教育訓練、開發產品...｜帶出基本規範與一般 SP 範圍)** (`#setupWorkItemKind`)」，並於下方「**盲投議程標題（要投票的基本工項名稱，如：資料庫健檢）** (`#setupSessionTitle`)」直接輸入新基本工項名稱。
        * 後端 `storyPoint.spCreateVotingSession` 自動擷取乾淨的新基本工項名稱，並以所選之 `@defaultWorkItemKind` 建立專屬之基本工項 (`storyPoint.tbWorkItemType`)。
    2. **若是新增「一般工項 (`targetKind = 'workItem'`)」的投票，應該設定「該一般工項要參照的基本工項 (`referencedWorkItemTypePk`，如：資料庫健檢)」**：
        * 當估算標的類型選擇「**📋 投『一般工項』**」時：
            * 由使用者選擇「**參照的基本工項 (如：資料庫健檢｜決定所屬工項類型、基本規範與基準點數)** (`#setupItemRefTypePk`, `name='referencedWorkItemTypePk'`)」，並於下方橫幅 (`#setupItemRefNormBanner`) 即時顯示所參照基本工項的標準點數、所屬工項類型、基本規範與一般 SP 範圍，同步帶入本回合允許點數上下限。
        * 後端 `POST /voting/create` (`portalController.cs`) 與 `storyPoint.spCreateVotingSession` (`sql/storedProcedure.sql`) 完整接收並保存 `@refBasicTypePk`（所參照的基本工項 PK），在建立新一般工項 (`storyPoint.tbWorkItem`) 時自動寫入 `workItemTypePk = @refBasicTypePk`、承接該基本工項之 `workItemKind` 與 `defaultTypePoint` / `storyPoint`，並於 `storyPoint.vwVotingSessionSummary` 與 `storyPoint.spGetVotingProcessHistory` 輸出 `referencedWorkItemTypePk`、`referencedWorkItemTypeCode`、`referencedWorkItemTypeName`。

### 2.32 即時呈現已經有哪些人進場了（盲投 Session 進場人員名單、座位進場狀態與大廳即時進場同步）
* **核心需求**：在多人盲投 Session 進行過程中，系統必須清楚且即時呈現**已經有哪些人進場了（已進場人數、已進場人員名單、進場時間）**以及**尚未進場人員名單**，讓主持人與參與者能明確區分「已進場思考中」與「尚未進場」。
* **資料庫與後端雙軌即時追蹤**：
    1. **T-SQL 資料表與預存程序擴充 (`sql/schemaAndTable.sql`、`sql/viewAndFunction.sql`、`sql/storedProcedure.sql`)**：
        * `storyPoint.tbVotingSessionParticipant` 新增 `hasEnteredSession` (`bit`)、`enteredAt` (`datetime2(3)`) 與 `lastHeartbeatAt` (`datetime2(3)`) 欄位（並具備無損升級保留機制）。
        * 新增預存程序 `storyPoint.spUpdateVotingParticipantPresence`，在參與者進入獨立盲投網頁 (`/voting/session`)、出牌 (`/voting/cast`)、暫離 (`/voting/session/leave`) 或登出 (`/logout`) 時同步更新進場狀態。
        * `storyPoint.vwVotingSessionSummary` 與 `storyPoint.spGetVotingProcessHistory` 輸出 `enteredParticipantCount`（已進場人數）、`enteredParticipantNameCsv`（已進場人員名單）、`notEnteredParticipantNameCsv`（尚未進場人員名單）及每位參與者的 `hasEnteredSession` / `isEnteredEffective` / `enteredAt`。
    2. **記憶體內即時 Lease 整合 (`web/storyPointPortal/service/sqlDataService.cs`)**：
        * `sqlDataService.votingSessionPresenceLease` 記錄參與者進入獨立盲投網頁的即時租約與進場時間 (`enteredAtLocal`)，並提供 `getActivePresentLeasesForSession` 與 `getAllActiveSessionLeasesBySessionPk` 供 Web 端點與 `/voting/liveStatus` 即時查詢。
* **前端介面呈現規範**：
    1. **獨立盲投 Session 網頁 (`/voting/session`) 綠色撲克牌桌**：
        * **右上角即時統計徽章**：並排呈現「`🟢 已進場：X / Y 人`」與「`🃏 本回合已出牌：A / Y 人`」。
        * **即時進場人員名單看板 (`#standalonePresenceBar`)**：於撲克牌桌上方醒目列出「**🟢 已經進場人員 (X / Y 人)**」（顯示姓名、`(您)`、部門、進場時間 `HH:mm` 以及 `🃏 已出牌` / `💭 思考中` 狀態標籤）與「**⏳ 尚未進場人員 (Z 人)**」（當全員皆已進場時顯示 `🎉 全員皆已進場到齊！`）。
        * **個人座位卡片 (`.seat-card`)**：每個座位頂部顯示「`🟢 已進場 (HH:mm)`」（綠底鮮明外框）或「`⏳ 尚未進場`」（虛線半透明外框）；尚未出牌時，已進場者顯示「`💭 思考中...` / `🟢 已進場・尚未出牌`」，未進場者顯示「`🚪 待進場` / `⏳ 尚未進場・未出牌`」。
        * **SignalR 與每 2 秒 `/voting/liveStatus` 無縫即時更新**：當其他受邀同仁進入或暫離該盲投 Session 時，透過 SignalR (`participantEntered` / `participantLeft`) 與 `/voting/liveStatus` 即時動態更新牌桌上的已進場人數、已進場人員名單與座位卡片狀態（即使使用者正在輸入出牌理由亦即時更新 DOM 不中斷輸入）。
    2. **即時盲投大廳 (`/?tab=voting`) 與設定盲投回合 (`/?tab=roundSetup`)**：
        * 左側投票議程邊欄、「待參與 / 參與中」大廳卡片及回合設定總覽表皆同步顯示「`🟢 已進場：X / Y 人（已進場名單）｜⏳ 未進場：...`」，並隨每 2 秒輪詢即時更新。
    3. **PowerShell 模組 (`ps/manageVotingSession.ps1`)**：
        * 支援 `-actionType 'updatePresence'` 呼叫 `storyPoint.spUpdateVotingParticipantPresence` 更新及查詢進場人員名單。

### 2.33 已定案基本工項與已定案盲投議程結案保護，以及投「基本工項」時僅需選擇「參照工項類型」
* **核心需求**：
    1. **投「基本工項」時，投的就是基本工項本身，無須也不可再顯示「目標基本工項」下拉選單**：
        * 在「`2. 設定回合` (`#pane-roundSetup`)」與「`⚡ 快速發起／重設盲投議程 Modal` (`openSessionSetupEditModal`)」的「`1️⃣ 投什麼 (What to vote on)`」區塊中：
            * 當估算標的類型選擇「`🏷️ 投『基本工項』 (workItemType)`」時，因為投的就是基本工項本身（於下方「`盲投議程標題（要投票的基本工項名稱，如：資料庫健檢）`」輸入要投票的基本工項名稱），右方區塊 (`#setupBoxType` / `#modalSetupTypeBox`) **僅顯示「`參照工項類型（專案、維運、顧問時數、教育訓練、開發產品...｜帶出基本規範與一般 SP 範圍）`」下拉選單與基本規範橫幅**，不再顯示冗餘且易混淆的「`目標基本工項`」下拉選單（既有未定案議程之 `workItemTypePk` 改由隱藏欄位 `<input type="hidden" name="workItemTypePk" id="setupWorkItemTypePk" />` 傳遞）。
            * 當估算標的類型選擇「`📋 投『一般工項』 (workItem)`」時，因為投的就是一般工項本身（於下方「`盲投議程標題（要投票的一般工項名稱，如：集保健檢）`」輸入要投票的一般工項名稱），右方區塊 (`#setupBoxItem` / `#modalSetupWiBox`) **僅顯示「`參照的基本工項（如：資料庫健檢｜決定所屬工項類型、基本規範與基準點數）`」下拉選單與規範橫幅**（既有未定案議程之 `workItemPk` 由隱藏欄位 `<input type="hidden" name="workItemPk" id="setupWorkItemPk" />` 傳遞）。
    2. **已定案盲投議程 (`sessionStatus = 'finalized'`) 與已定案基本工項 (`estimationStatus = 'finalized'` / `standardStoryPoint is not null`) 已決定該基本工項的 Story Point 並結束投票，不得再出現於「2. 設定回合 (`1-2. 設定盲投回合`)」或「1-1. 即時盲投大廳」**：
        * 在「`2. 設定回合` (`#pane-roundSetup`)」的「`1. 選擇目標盲投議程` (`#setupSessionSelector`)」下拉選單與下方「`📋 各投票議程的回合設定總覽` (`#roundSetupSessionTable`)」中，**僅列出進行中（未定案）之盲投議程**，完全排除「`🔄 已定案議程`」。
        * 當盲投議程拍板定案 (`controlAction = 'finalize'`) 時，後端立即釋放該議程所有參與者之活躍分頁鎖定 (`sqlDataService.releaseAllLeasesForSession`)，使該議程同步從即時盲投大廳之「待參與／參與中」結束移除，僅保留於「`1-3. 估算過程查詢`」供歷史稽核查閱。
    3. **建立新基本工項盲投議程時嚴格禁止覆寫既有基本工項 (`storyPoint.spCreateVotingSession`)**：
        * 當發起新的基本工項盲投議程 (`@sessionPk <= 0` 且非重新投票模式) 時，若標題解析出的新基本工項名稱 (`@autoTypeName`) 與傳入之基本工項名稱完全不同，預存程序 `storyPoint.spCreateVotingSession` 自動將 `@workItemTypePk` 視為新基本工項，獨立建立專屬之 `storyPoint.tbWorkItemType` 紀錄，確保新投完的基本工項（如 `test`）與既有基本工項（如 `資料庫健檢`）各自獨立顯示於「`3-2. 基本工項管理`」表格中。

### 2.34 基本工項 (`3-2. 基本工項管理` / `storyPoint.tbWorkItemType`) 全面支援重新投票
* **核心需求**：於「**3-2. 基本工項管理 (`/?tab=types`)**」中，所有基本工項（不論是「待投票定案」或「已拍板定案具標準 Story Point，如：`TYPE-DB-HC - 資料庫健檢 13.00 pt`」）皆可隨時發起**重新投票**：
    1. **滑鼠右鍵快捷選單 (`rowKind === 'workitemType'`)**：
        * 在任何一筆基本工項資料列上按滑鼠右鍵，固定顯示三種重新投票操作（不受是否已有 `standardStoryPoint` 限制）：
            * `🆕 重新投票 — 全部重新開始（重新設定投票人名單與所有相關選項）`
            * `🔄 重新投票 — 重啟新一輪盲投（沿用原設定直接開啟下一輪）`
            * `⚙️ 前往「1-2. 設定盲投回合」完整頁面重新設定並開始`
    2. **上方屬性編輯表單與單筆編輯 Modal 視窗 (`openWorkItemTypeEditModal`)**：
        * 選取任何既有基本工項 (`pk > 0`) 時，上方編輯表單與單筆編輯 Modal 皆顯示「`🔄 重啟新一輪盲投`」與「`🃏 重新盲投設定 (全部重新開始)` / `🆕 全部重新開始(重設投票人與所有選項)`」按鈕。
    3. **後端控制器與預存程序 (`portalController.cs`、`storyPoint.spCreateVotingSession`、`storyPoint.spControlVotingSession`)**：
        * 針對已定案基本工項發起「重啟新一輪」或「全部重新開始」時，自動載入該基本工項之原盲投議程（或為種子基本工項自動建立盲投議程），將基本工項狀態重設為 `estimationStatus = 'voting'`、`standardStoryPoint = null`，並將重投前之標準點數完整記錄於 `storyPoint.tbPointHistory` 稽核歷程表。

### 2.29 Refresh 與背景自動輪詢時畫面不閃爍規範 (Non-Flickering Seamless Refresh & Live Polling)
* **消除背景輪詢簽章不一致導致的循環重載**：
    1. 大廳前端 `currentSessionListSignature` 與後端 `/voting/liveStatus` 之 `sessionListSignature` 必須採用完全一致的欄位結構 (`{votingSessionPk}:{sessionStatus}:{currentRoundNumber}:{currentRoundVoteCount}:{participantCount}:{isViewerParticipant}`)，進場人數 (`enteredCount`) 異動由前端 DOM 即時差異更新 (`updateLobbySessionPresenceDisplay`) 處理，不得觸發整頁重新載入。
    2. 後端 `/voting/liveStatus` 僅在呼叫端帶有獨立盲投網頁 `tabInstanceId` 時，才將呼叫者本身視為已在該獨立網頁進場，避免使用者停留於大廳 (`/?tab=voting`) 輪詢時誤判進場人數造成畫面數值來回跳動。
* **無閃爍平滑局部更新 (Soft Refresh via `fetch` + `DOMParser`)**：
    1. 無論使用者手動點擊「`🔄 重新整理` / `🔄 重新整理議程清單` / `🔄 重新整理本頁`」按鈕，或由 SignalR 推播與背景輪詢觸發更新，大廳 (`softRefreshLobbyDom`) 與獨立盲投 Session 網頁 (`softRefreshStandaloneSessionDom`) 一律透過背景 `fetch` 搭配 `DOMParser` 原地更新 DOM 區塊，不執行整頁跳轉 (`window.location.href`)，徹底消除瀏覽器白屏與畫面閃爍。
    2. 局部更新時完整保留使用者當前的畫面捲軸位置 (`window.scrollY`)、左側邊欄寬度與清單收合狀態、各資料夾 (`待參與` / `參與中`) 與議程卡片詳情的展開/收合狀態，以及獨立盲投網頁中尚未送出的選牌與出牌理由輸入內容。
    3. 即時進場名單與座位狀態更新 (`updateLobbySessionPresenceDisplay`、`updateStandalonePresenceDom`) 僅在實際內容有變更時才寫入 `innerHTML` / `textContent`，避免週期性無意義的 DOM 重繪。

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
            1. **【1. 🃏 多人估算】主選單**：
                * 子選單 1-1：`🃏 1-1. 即時盲投大廳`（浮動 Tips 說明 Planning Poker 出牌、翻牌與定案）
                * 子選單 1-2：`⚙️ 1-2. 設定盲投回合`（浮動 Tips 說明盲投開始前設定標的、參與名單、點數範圍與共識規則）
                * 子選單 1-3：`📜 1-3. 估算過程查詢`（浮動 Tips 說明事後檢視每回合出牌明細與異動紀錄）
            2. **【2. ⚙️ 設定回合】主選單快速入口**：直接切換至「設定盲投回合」頁面，搭配浮動 Tips 說明；點選「設定回合」時僅反白「⚙️ 設定回合」按鈕本身，不得一併反白旁邊的「🃏 多人估算」主選單。
            3. **【3. 📋 工項管理 / 3. 📋 我的工項】主選單**：
                * 子選單 3-1：`📋 3-1. 一般工項與員工指派` / `📋 3-1. 我的一般工項查詢`（如：集保健檢，套用基本工項 SP、可微調、具起訖時間）
                * 子選單 3-2：`🏷️ 3-2. 基本工項管理` / `🏷️ 3-2. 基本工項參考`（如：資料庫健檢，無起訖時間，套用工項類型，為投票主要標的）
                * 子選單 3-3：`🗂️ 3-3. 工項類型與基本規範`（如：專案、維運、顧問時數...，定義一般工項基本規範與告知一般 SP 範圍）
                * 子選單 3-4 ~ 3-6：`3-4. 匯出一般工項 (CSV)`、`3-5. 匯出一般工項 (Excel)`、`3-6. 匯出一般工項 (HTML)`
            4. **【4. 📊 統計報表 / 4. 📊 我的報表】主選單**：
                * 子選單 4-1：`📊 4-1. 員工與部門統計` / `📊 4-1. 我的分數統計`
                * 子選單 4-2A ~ 4-3C：部門與員工點數三格式匯出（`CSV` / `Excel` / `HTML`）
            5. **【5. 🔑 修改帳密】主選單快速入口**：直接切換至個人登入帳號與密碼修改頁面；點選「修改帳密」或「修改我的帳密」時僅反白「🔑 修改帳密」按鈕本身，不得一併反白旁邊的「👥 組織與員工」或「🔑 帳號設定」主選單。
            6. **【6. 👥 組織與員工 / 6. 🔑 帳號設定】主選單**：
                * 子選單 6-0 / 6-1：`🔑 6-0. 修改我的帳密`、`👥 6-1. 員工與組織管理`、`6-2A. 員工 CSV 範本`、`6-2B. 員工 Excel 範本`、`🚪 6-3. 登出系統`
            7. **【7. 🛡️ 系統 Log】主選單（管理者可見）**：
                * 子選單 7-1 ~ 7-2C：`🛡️ 7-1. 系統 Log 查詢` 及 `CSV` / `Excel` / `HTML` 匯出。
            8. **【8. 📖 線上說明】主選單**：
                * 子選單 8-1 ~ 8-8：完整線上說明與操作畫面搜尋手冊。
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
