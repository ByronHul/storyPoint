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

### 2.10 表單欄位單行精簡 Caption 對齊與滑鼠移入浮動 Tips 說明 (Single-Line Form Captions & Hover Floating Tooltips)
* **禁止欄位標題 (Caption / Label) 換成兩行導致水平輸入框不對齊**：
    1. **單行精簡 Caption 與嚴格水平對齊**：
        * 所有 `.form-row` 與 `.toolbar-grid` 採用 `align-items: end` 與 `min-width: 0`，且欄位 `<label>` 強制單行呈現（`white-space: nowrap; overflow: hidden; text-overflow: ellipsis; height: 20px; line-height: 20px;`），確保同一列的所有 `<input>`、`<select>` 與 `<button>` 輸入框 100% 水平對齊（例如管理者主持控制台之「本回合討論摘要備註」、「拍板定案點數」、「定案核定理由」等所有表單列）。
    2. **滑鼠移入時即時浮現完整項目說明 (`data-tip` + `#spFloatingTooltip`)**：
        * 將原本冗長的括號補充說明精簡為單行標題搭配 `ⓘ` 提示圖示（`.sp-tip-icon`），並透過 `data-tip` 與全域浮動提示元件 (`#spFloatingTooltip`)，於滑鼠移入（Hover）時立即浮現完整項目說明、計算依據與填寫規範。

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
        * **網頁上方主選單與子選單階層式下拉導覽列 (Top Bar Hierarchical Dropdown Menu)**：將原本平鋪換行的頁籤按鈕改為整合於網頁頂部導覽列之「主選單 + 子選單下拉選單」分類設計：
            1. **【🃏 多人估算與過程】主選單**：
                * 子選單 1-1：`🃏 多人即時盲投大廳 (Planning Poker)`
                * 子選單 1-2：`📜 查詢訂出 Story Point 的過程`
            2. **【📋 工項與類型管理】主選單**：
                * 子選單 2-1：`📋 工項點數查詢與賦予員工設定`
                * 子選單 2-2：`🏷️ 工項代表類型管理`
            3. **【📊 點數統計與報表】主選單**：
                * 子選單 3-1：`📊 員工與部門總 Story Point 統計`
                * 子選單 3-2：`📥 匯出部門 / 員工 / 工項 CSV 報表`
            4. **【👥 組織與帳密管理】主選單**：
                * 子選單 4-1：`👥 員工列表與登入帳密管理`
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
    * 遠端儲存庫建立並推送至 GitHub Repository (`https://github.com/ByronHul/storyPoint`)，提交訊息清楚說明變更目的。
