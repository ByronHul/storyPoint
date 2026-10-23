/*
================================================================================
檔案名稱: seedData.sql
用途說明: 初始化 Azure 多人參與 Story Point 系統之預設資料 (部門、管理員與參與者帳號、
          撲克牌組、工項代表類型、實際工項與員工賦予、以及示範投票議程與多回合過程紀錄)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
修改歷程:
    2026-10-23 初版建立，全小寫關鍵字、兩節式名稱、具備條件檢查確保可重複執行不重複插入
================================================================================
*/

set nocount on;
go

-- ============================================================================
-- 1. 預設部門資料 (storyPoint.tbDepartment)
-- ============================================================================
if not exists (select 1 from storyPoint.tbDepartment where departmentCode = 'DEPT_IT_ARCH')
begin
    insert into storyPoint.tbDepartment (
        departmentCode, departmentName, parentDepartmentPk, managerName, departmentNote, isActive
    )
    values
        ('DEPT_IT_ARCH', N'雲端架構與核心研發部', null, N'胡總監 (Byron)', N'負責 Azure 雲端架構、核心 API 與資料庫規劃', 1),
        ('DEPT_WEB_DEV', N'全端應用開發部', null, N'陳經理 (Alex)', N'負責 .NET 網站前後端與商業邏輯實作', 1),
        ('DEPT_QA_OPS', N'品質保證與維運部', null, N'林經理 (Grace)', N'負責自動化測試、PowerShell 部署與系統維運', 1),
        ('DEPT_PM_BIZ', N'產品規劃與專案管理部', null, N'張經理 (Irene)', N'負責需求訪談、User Story 撰寫與敏捷衝刺規劃', 1);
end
go

-- ============================================================================
-- 2. 預設員工與登入帳密 (storyPoint.tbEmployee)
-- 預設密碼對照 (皆採用標準 SHA-256 小寫 Hex 雜湊)：
--   admin123 -> 240be518fabd2724ddb6f04eeb1da5967448d7e831c08c8fa822809f74c720a9
--   pass123  -> 9b8769a4a742959a2d0298c36fb70623f2dfacda8436237df08d8dfd5b37374c
-- ============================================================================
declare @deptArchPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'DEPT_IT_ARCH');
declare @deptWebPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'DEPT_WEB_DEV');
declare @deptQaPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'DEPT_QA_OPS');
declare @deptPmPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'DEPT_PM_BIZ');

declare @adminHash varchar(256) = '240be518fabd2724ddb6f04eeb1da5967448d7e831c08c8fa822809f74c720a9'; -- admin123
declare @userHash varchar(256) = '9b8769a4a742959a2d0298c36fb70623f2dfacda8436237df08d8dfd5b37374c';  -- pass123

if not exists (select 1 from storyPoint.tbEmployee where loginAccount = 'admin')
begin
    insert into storyPoint.tbEmployee (
        employeeCode, employeeName, loginAccount, passwordHash, emailAddress, jobTitle, departmentPk, roleName, isActive
    )
    values
        ('EMP001', N'胡資深架構師 (Byron)', 'admin', @adminHash, 'byron.hu@company.com', N'首席架構師 / Scrum Master', @deptArchPk, 'admin', 1),
        ('EMP002', N'陳大文 (Alex)', 'alex.chen', @adminHash, 'alex.chen@company.com', N'全端開發技術經理', @deptWebPk, 'admin', 1),
        ('EMP003', N'王小明 (Kevin)', 'kevin.wang', @userHash, 'kevin.wang@company.com', N'資深後端工程師', @deptArchPk, 'participant', 1),
        ('EMP004', N'李雅婷 (Emily)', 'emily.lee', @userHash, 'emily.lee@company.com', N'資深前端與 .NET 工程師', @deptWebPk, 'participant', 1),
        ('EMP005', N'張家豪 (David)', 'david.chang', @userHash, 'david.chang@company.com', N'全端工程師', @deptWebPk, 'participant', 1),
        ('EMP006', N'林淑芬 (Grace)', 'grace.lin', @userHash, 'grace.lin@company.com', N'資深測試與 DevOps 工程師', @deptQaPk, 'participant', 1),
        ('EMP007', N'黃志明 (Jason)', 'jason.huang', @userHash, 'jason.huang@company.com', N'自動化測試工程師', @deptQaPk, 'participant', 1),
        ('EMP008', N'周佳穎 (Irene)', 'irene.chou', @adminHash, 'irene.chou@company.com', N'資深產品經理 (Product Owner)', @deptPmPk, 'admin', 1);
end
go

-- ============================================================================
-- 3. 預設撲克牌組設定 (storyPoint.tbCardDeck)
-- ============================================================================
if not exists (select 1 from storyPoint.tbCardDeck where deckCode = 'fibonacciStandard')
begin
    insert into storyPoint.tbCardDeck (
        deckCode, deckName, cardValueCsv, description, isDefault, isActive
    )
    values
        ('fibonacciStandard', N'標準費氏數列牌組 (Standard Fibonacci)', N'0,1,2,3,5,8,13,21,?,☕', N'敏捷 Scrum 最常使用之標準費氏數列，含 ?(需求不明需討論) 與 ☕(暫停休息)', 1, 1),
        ('fibonacciModified', N'修正費氏數列牌組 (Modified Fibonacci)', N'0,0.5,1,2,3,5,8,13,20,40,100,?,☕', N'包含 0.5 半點與 20/40/100 大顆粒度點數之修正費氏數列，含 ?(需求不明需討論) 與 ☕(暫停休息)', 0, 1);
end
else
begin
    update storyPoint.tbCardDeck
    set cardValueCsv = N'0,1,2,3,5,8,13,21,?,☕',
        description = N'敏捷 Scrum 最常使用之標準費氏數列，含 ?(需求不明需討論) 與 ☕(暫停休息)',
        updatedAt = sysdatetime()
    where deckCode = 'fibonacciStandard';

    update storyPoint.tbCardDeck
    set cardValueCsv = N'0,0.5,1,2,3,5,8,13,20,40,100,?,☕',
        description = N'包含 0.5 半點與 20/40/100 大顆粒度點數之修正費氏數列，含 ?(需求不明需討論) 與 ☕(暫停休息)',
        updatedAt = sysdatetime()
    where deckCode = 'fibonacciModified';
end
go

-- ============================================================================
-- 4. 預設工項代表類型 (storyPoint.tbWorkItemType)
-- 投票時投工項的代表類型，建立工項時選擇此類型即可自動帶入 Story Point
-- ============================================================================
declare @defaultDeckPk int = (select top 1 pk from storyPoint.tbCardDeck where deckCode = 'fibonacciStandard');

if not exists (select 1 from storyPoint.tbWorkItemType where typeCode = 'TYPE_STD_CRUD')
begin
    insert into storyPoint.tbWorkItemType (
        typeCode, typeName, categoryName, description, standardStoryPoint, defaultCardDeckPk, estimationStatus, isActive
    )
    values
        ('TYPE_UI_ADJUST', N'小型前端介面與文案調整', N'前端與介面', N'單一頁面樣式微調、欄位驗證或繁體中文文案修改，無資料庫異動', 1.00, @defaultDeckPk, 'finalized', 1),
        ('TYPE_STD_CRUD', N'標準單表 CRUD API 與預存程序', N'後端與資料庫', N'包含單一資料表建立、標準增刪改查 Stored Procedure 及對應頁面串接', 3.00, @defaultDeckPk, 'finalized', 1),
        ('TYPE_PS_MODULE', N'PowerShell 自動化腳本與批次工具', N'自動化與維運', N'符合模組化與雙軌 Log 規範之 PowerShell Console 腳本與多 Tab UI 串接', 5.00, @defaultDeckPk, 'finalized', 1),
        ('TYPE_COMPLEX_REPORT', N'跨部門多維度統計報表與匯出', N'資料分析與報表', N'涉及多張資料表彙總檢視表 (View)、動態篩選與 CSV/Excel 報表匯出', 8.00, @defaultDeckPk, 'finalized', 1),
        ('TYPE_REALTIME_HUB', N'多人即時互動模組 (SignalR / 跨系統整合)', N'核心架構', N'涉及多人即時雙向同步通訊、多回合狀態機控制與高併發例外處理', 13.00, @defaultDeckPk, 'finalized', 1),
        ('TYPE_AI_AGENT_INT', N'AI 智能輔助估算與外部 Webhook 整合', N'創新功能', N'尚待團隊透過 Planning Poker 投票訂出標準 Story Point 之新代表類型', null, @defaultDeckPk, 'voting', 1);
end
go

-- ============================================================================
-- 5. 示範多人投票議程與多回合過程紀錄 (storyPoint.tbVotingSession / Round / VoteRecord)
-- 讓使用者一進入系統即可查詢「訂出 Story Point 的完整過程」以及進行中的投票議程
-- ============================================================================
declare @hostPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'admin');
declare @alexPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'alex.chen');
declare @kevinPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'kevin.wang');
declare @emilyPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'emily.lee');
declare @davidPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'david.chang');
declare @gracePk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'grace.lin');
declare @jasonPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'jason.huang');
declare @irenePk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'irene.chou');

declare @deckPk int = (select top 1 pk from storyPoint.tbCardDeck where deckCode = 'fibonacciStandard');

declare @typeCrudPk int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_STD_CRUD');
declare @typeReportPk int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_COMPLEX_REPORT');
declare @typeRealtimePk int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_REALTIME_HUB');
declare @typeAiPk int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_AI_AGENT_INT');

if not exists (select 1 from storyPoint.tbVotingSession where sessionCode = 'VOTE-202610-001')
begin
    -- 議程 1：已定案 (兩回合收斂示範 - 跨部門多維度統計報表與匯出 -> 定案 8 點)
    insert into storyPoint.tbVotingSession (
        sessionCode, sessionTitle, targetKind, workItemTypePk, workItemPk,
        cardDeckPk, hostEmployeePk, currentRoundNumber, sessionStatus,
        finalStoryPoint, finalDecisionNote, startedAt, finalizedAt
    )
    values (
        'VOTE-202610-001',
        N'【代表類型估算】跨部門多維度統計報表與匯出 (TYPE_COMPLEX_REPORT)',
        'workItemType',
        @typeReportPk,
        null,
        @deckPk,
        @hostPk,
        2,
        'finalized',
        8.00,
        N'第 1 回合因對 BCP 相容 CSV 匯出與部門階層彙總複雜度看法不同，點數落在 5~13；經討論確認共用檢視表結構後，第 2 回合全體達成 8 點共識定案。',
        dateadd(hour, -48, sysdatetime()),
        dateadd(hour, -47, sysdatetime())
    );
    declare @sess1Pk int = scope_identity();

    update storyPoint.tbWorkItemType set lastVotingSessionPk = @sess1Pk where pk = @typeReportPk;

    insert into storyPoint.tbVotingSessionParticipant (votingSessionPk, employeePk, isRequiredVoter)
    values
        (@sess1Pk, @hostPk, 1), (@sess1Pk, @alexPk, 1), (@sess1Pk, @kevinPk, 1),
        (@sess1Pk, @emilyPk, 1), (@sess1Pk, @davidPk, 1), (@sess1Pk, @gracePk, 1);

    -- 議程 1 第 1 回合 (意見分歧：5, 8, 13)
    insert into storyPoint.tbVotingRound (
        votingSessionPk, roundNumber, roundStatus, averagePoint, minPoint, maxPoint,
        voteCount, roundDiscussionNote, startedAt, revealedAt
    )
    values (
        @sess1Pk, 1, 'revealed', 8.17, 5.00, 13.00, 6,
        N'第 1 回合翻牌：Kevin 投 5 點認為 SQL View 可快速處理；Emily 與 Grace 投 13 點擔心前端多重篩選與匯出格式驗證耗時。主持決定釐清規格後進行第 2 回合。',
        dateadd(minute, -2880, sysdatetime()),
        dateadd(minute, -2865, sysdatetime())
    );
    declare @s1r1Pk int = scope_identity();

    insert into storyPoint.tbVoteRecord (votingRoundPk, votingSessionPk, roundNumber, employeePk, cardValue, numericPointValue, voteComment)
    values
        (@s1r1Pk, @sess1Pk, 1, @hostPk, '8', 8.00, N'包含 SQL 檢視表與前後端報表串接，約需 8 點'),
        (@s1r1Pk, @sess1Pk, 1, @alexPk, '8', 8.00, N'Web 端與 PowerShell 匯出可共用 Stored Procedure'),
        (@s1r1Pk, @sess1Pk, 1, @kevinPk, '5', 5.00, N'後端 T-SQL 聚合查詢結構單純'),
        (@s1r1Pk, @sess1Pk, 1, @emilyPk, '13', 13.00, N'若包含多維度動態篩選與圖表呈現，前端工作量較大'),
        (@s1r1Pk, @sess1Pk, 1, @davidPk, '5', 5.00, N'參考過往報表模組約 5 點'),
        (@s1r1Pk, @sess1Pk, 1, @gracePk, '13', 13.00, N'需驗證大量員工與部門分攤比例加總的邊界測試案例');

    -- 議程 1 第 2 回合 (收斂達成共識：8 點)
    insert into storyPoint.tbVotingRound (
        votingSessionPk, roundNumber, roundStatus, averagePoint, minPoint, maxPoint,
        voteCount, roundDiscussionNote, startedAt, revealedAt
    )
    values (
        @sess1Pk, 2, 'revealed', 8.00, 8.00, 8.00, 6,
        N'第 2 回合翻牌：確認計算邏輯統一封裝於 SQL Server 檢視表與預存程序後，全數投出 8 點達成一致共識！',
        dateadd(minute, -2860, sysdatetime()),
        dateadd(minute, -2850, sysdatetime())
    );
    declare @s1r2Pk int = scope_identity();

    insert into storyPoint.tbVoteRecord (votingRoundPk, votingSessionPk, roundNumber, employeePk, cardValue, numericPointValue, voteComment)
    values
        (@s1r2Pk, @sess1Pk, 2, @hostPk, '8', 8.00, N'邏輯收斂於 DB SP，8 點合理'),
        (@s1r2Pk, @sess1Pk, 2, @alexPk, '8', 8.00, N'同意 8 點'),
        (@s1r2Pk, @sess1Pk, 2, @kevinPk, '8', 8.00, N'加上匯出與雙軌 Log 驗證，調升為 8 點'),
        (@s1r2Pk, @sess1Pk, 2, @emilyPk, '8', 8.00, N'DB 直接回傳彙總結果，前端呈現降至 8 點'),
        (@s1r2Pk, @sess1Pk, 2, @davidPk, '8', 8.00, N'同意 8 點標準'),
        (@s1r2Pk, @sess1Pk, 2, @gracePk, '8', 8.00, N'測試腳本可重用 common.ps1，同意 8 點');

    -- 議程 2：已定案 (多人即時互動模組 -> 定案 13 點)
    insert into storyPoint.tbVotingSession (
        sessionCode, sessionTitle, targetKind, workItemTypePk, workItemPk,
        cardDeckPk, hostEmployeePk, currentRoundNumber, sessionStatus,
        finalStoryPoint, finalDecisionNote, startedAt, finalizedAt
    )
    values (
        'VOTE-202610-002',
        N'【代表類型估算】多人即時互動模組 SignalR (TYPE_REALTIME_HUB)',
        'workItemType',
        @typeRealtimePk,
        null,
        @deckPk,
        @hostPk,
        1,
        'finalized',
        13.00,
        N'全體評估包含即時連線同步、盲投牌面遮罩與多回合狀態切換，一致核定標準點數為 13 點。',
        dateadd(hour, -24, sysdatetime()),
        dateadd(hour, -23, sysdatetime())
    );
    declare @sess2Pk int = scope_identity();

    update storyPoint.tbWorkItemType set lastVotingSessionPk = @sess2Pk where pk = @typeRealtimePk;

    insert into storyPoint.tbVotingSessionParticipant (votingSessionPk, employeePk, isRequiredVoter)
    values
        (@sess2Pk, @hostPk, 1), (@sess2Pk, @alexPk, 1), (@sess2Pk, @kevinPk, 1), (@sess2Pk, @emilyPk, 1);

    insert into storyPoint.tbVotingRound (
        votingSessionPk, roundNumber, roundStatus, averagePoint, minPoint, maxPoint,
        voteCount, roundDiscussionNote, startedAt, revealedAt
    )
    values (
        @sess2Pk, 1, 'revealed', 13.00, 13.00, 13.00, 4,
        N'第一回合全員投出 13 點，無異議直接定案。',
        dateadd(minute, -1440, sysdatetime()),
        dateadd(minute, -1425, sysdatetime())
    );
    declare @s2r1Pk int = scope_identity();

    insert into storyPoint.tbVoteRecord (votingRoundPk, votingSessionPk, roundNumber, employeePk, cardValue, numericPointValue, voteComment)
    values
        (@s2r1Pk, @sess2Pk, 1, @hostPk, '13', 13.00, N'包含 SignalR Hub 與斷線重連處理'),
        (@s2r1Pk, @sess2Pk, 1, @alexPk, '13', 13.00, N'前後端狀態同步複雜度高，13 點'),
        (@s2r1Pk, @sess2Pk, 1, @kevinPk, '13', 13.00, N'支援多回合盲投與權限控管，13 點'),
        (@s2r1Pk, @sess2Pk, 1, @emilyPk, '13', 13.00, N'翻牌動畫與即時渲染，13 點');

    -- 議程 3：目前進行中投票議程 (voting 盲投階段，方便使用者登入後立即體驗出牌、翻牌與定案)
    insert into storyPoint.tbVotingSession (
        sessionCode, sessionTitle, targetKind, workItemTypePk, workItemPk,
        cardDeckPk, hostEmployeePk, currentRoundNumber, sessionStatus,
        finalStoryPoint, finalDecisionNote, startedAt, finalizedAt
    )
    values (
        'VOTE-202610-003',
        N'【進行中盲投】AI 智能輔助估算與外部 Webhook 整合 (TYPE_AI_AGENT_INT)',
        'workItemType',
        @typeAiPk,
        null,
        @deckPk,
        @hostPk,
        1,
        'voting',
        null,
        null,
        dateadd(minute, -30, sysdatetime()),
        null
    );
    declare @sess3Pk int = scope_identity();

    update storyPoint.tbWorkItemType set lastVotingSessionPk = @sess3Pk where pk = @typeAiPk;

    insert into storyPoint.tbVotingSessionParticipant (votingSessionPk, employeePk, isRequiredVoter)
    select @sess3Pk, pk, 1 from storyPoint.tbEmployee where isActive = 1;

    insert into storyPoint.tbVotingRound (
        votingSessionPk, roundNumber, roundStatus, averagePoint, minPoint, maxPoint,
        voteCount, roundDiscussionNote, startedAt, revealedAt
    )
    values (
        @sess3Pk, 1, 'voting', 6.33, 5.00, 8.00, 3,
        N'目前正在進行第 1 回合盲投，已有 3 位成員完成出牌，等待其餘成員出牌或由管理者翻牌揭曉。',
        dateadd(minute, -30, sysdatetime()),
        null
    );
    declare @s3r1Pk int = scope_identity();

    insert into storyPoint.tbVoteRecord (votingRoundPk, votingSessionPk, roundNumber, employeePk, cardValue, numericPointValue, voteComment)
    values
        (@s3r1Pk, @sess3Pk, 1, @kevinPk, '8', 8.00, N'需處理外部 Webhook 簽章驗證與非同步佇列重試'),
        (@s3r1Pk, @sess3Pk, 1, @emilyPk, '5', 5.00, N'介面端僅需呈現建議點數卡片與歷程比對'),
        (@s3r1Pk, @sess3Pk, 1, @gracePk, '?', null, N'想先確認是否包含第三方 API 流量限制壓測');
end
go

update storyPoint.tbVotingSession
set minStoryPoint = 1.00,
    maxStoryPoint = 21.00,
    maxAllowedSpread = 5.00,
    consensusRule = 'nearestFibonacci',
    roundTimeLimitSecond = 120,
    roundGoalNote = isnull(roundGoalNote, N'包含外部 Webhook 簽章驗證、SQL Server 預存程序日誌記錄與異常重試機制，請依 1 ~ 21 pt 範圍出牌估算。')
where sessionCode = 'VOTE-202610-003';
go

-- ============================================================================
-- 6. 預設實際工項與員工賦予設定 (storyPoint.tbWorkItem & tbWorkItemAssignment)
-- 展示：選擇工項代表類型自動帶入 Story Point、或手動調整，並賦予給不同部門員工
-- ============================================================================
declare @creatorPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'admin');
declare @empByron int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'admin');
declare @empAlex int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'alex.chen');
declare @empKevin int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'kevin.wang');
declare @empEmily int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'emily.lee');
declare @empDavid int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'david.chang');
declare @empGrace int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'grace.lin');
declare @empJason int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'jason.huang');
declare @empIrene int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'irene.chou');

declare @tUi int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_UI_ADJUST');
declare @tCrud int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_STD_CRUD');
declare @tPs int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_PS_MODULE');
declare @tReport int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_COMPLEX_REPORT');
declare @tHub int = (select top 1 pk from storyPoint.tbWorkItemType where typeCode = 'TYPE_REALTIME_HUB');

if not exists (select 1 from storyPoint.tbWorkItem where itemCode = 'WI-2026-001')
begin
    -- 工項 1：自動帶入 TYPE_STD_CRUD (3 點)
    exec storyPoint.spUpsertWorkItem
        @itemCode = 'WI-2026-001',
        @itemTitle = N'部門與員工帳號密碼管理模組 (含雜湊驗證與權限控管)',
        @itemDescription = N'實作部門資料表與員工登入帳密儲存、SHA-256 雜湊驗證及管理者增刪改查介面。',
        @workItemTypePk = @tCrud,
        @sprintName = N'2026-Q4 Sprint 1',
        @priorityLevel = 'high',
        @storyPoint = null, -- 自動帶入代表類型 3.00 點
        @executionStatus = 'done',
        @dueDate = '2026-10-25',
        @createdByEmployeePk = @creatorPk,
        @operatorAccount = 'admin';

    declare @wi1 int = (select top 1 pk from storyPoint.tbWorkItem where itemCode = 'WI-2026-001');
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi1, @employeePk = @empKevin, @assignmentRole = N'主要負責', @allocationPercent = 100.00, @assignmentNote = N'負責 DB Schema 與登入驗證 SP', @assignedByEmployeePk = @creatorPk;

    -- 工項 2：自動帶入 TYPE_REALTIME_HUB (13 點)，由 2 位員工共同分攤 (60% / 40%)
    exec storyPoint.spUpsertWorkItem
        @itemCode = 'WI-2026-002',
        @itemTitle = N'多人即時 Planning Poker 盲投與翻牌同步引擎',
        @itemDescription = N'建立 SignalR Hub 支援多人即時出牌、盲投遮罩、管理者一鍵翻牌、開啟下一回合與最終拍板定案。',
        @workItemTypePk = @tHub,
        @sprintName = N'2026-Q4 Sprint 1',
        @priorityLevel = 'high',
        @storyPoint = null, -- 自動帶入代表類型 13.00 點
        @executionStatus = 'inProgress',
        @dueDate = '2026-10-30',
        @createdByEmployeePk = @creatorPk,
        @operatorAccount = 'admin';

    declare @wi2 int = (select top 1 pk from storyPoint.tbWorkItem where itemCode = 'WI-2026-002');
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi2, @employeePk = @empByron, @assignmentRole = N'架構與後端主導', @allocationPercent = 50.00, @assignmentNote = N'負責 SignalR Hub 與多回合投票 SP (6.5 pt)', @assignedByEmployeePk = @creatorPk;
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi2, @employeePk = @empEmily, @assignmentRole = N'前端撲克牌互動介面', @allocationPercent = 50.00, @assignmentNote = N'負責即時翻牌視覺介面與動態更新 (6.5 pt)', @assignedByEmployeePk = @creatorPk;

    -- 工項 3：選擇 TYPE_STD_CRUD (原 3 點)，但因包含工項代表類型連動與歷史重算，手動調整為 5 點
    exec storyPoint.spUpsertWorkItem
        @itemCode = 'WI-2026-003',
        @itemTitle = N'工項建立自動帶入代表類型 Story Point 與員工分攤指派模組',
        @itemDescription = N'建立工項時選擇代表類型自動帶入點數，支援管理者手動調整點數並自動重算員工與部門總點數。',
        @workItemTypePk = @tCrud,
        @sprintName = N'2026-Q4 Sprint 1',
        @priorityLevel = 'high',
        @storyPoint = 5.00, -- 手動調整為 5.00 點
        @forceManualAdjust = 1,
        @adjustmentReason = N'除標準 CRUD 外，需實作代表類型點數自動帶入與員工分攤比例連動重算，故由 3 點手動調升為 5 點',
        @executionStatus = 'inProgress',
        @dueDate = '2026-11-02',
        @createdByEmployeePk = @creatorPk,
        @operatorAccount = 'admin';

    declare @wi3 int = (select top 1 pk from storyPoint.tbWorkItem where itemCode = 'WI-2026-003');
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi3, @employeePk = @empAlex, @assignmentRole = N'主要負責', @allocationPercent = 60.00, @assignmentNote = N'負責後端商業邏輯 (3 pt)', @assignedByEmployeePk = @creatorPk;
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi3, @employeePk = @empDavid, @assignmentRole = N'協同開發', @allocationPercent = 40.00, @assignmentNote = N'負責指派表單互動介面 (2 pt)', @assignedByEmployeePk = @creatorPk;

    -- 工項 4：自動帶入 TYPE_COMPLEX_REPORT (8 點)
    exec storyPoint.spUpsertWorkItem
        @itemCode = 'WI-2026-004',
        @itemTitle = N'員工個人與各部門總 Story Point 統計儀表板及投票過程追溯查詢',
        @itemDescription = N'提供查詢每位員工身上賦予的工項與總 Story Point、各部門總點數統計，以及事後查詢每場投票各回合出牌過程。',
        @workItemTypePk = @tReport,
        @sprintName = N'2026-Q4 Sprint 2',
        @priorityLevel = 'high',
        @storyPoint = null, -- 自動帶入代表類型 8.00 點
        @executionStatus = 'inProgress',
        @dueDate = '2026-11-05',
        @createdByEmployeePk = @creatorPk,
        @operatorAccount = 'admin';

    declare @wi4 int = (select top 1 pk from storyPoint.tbWorkItem where itemCode = 'WI-2026-004');
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi4, @employeePk = @empDavid, @assignmentRole = N'主要負責', @allocationPercent = 50.00, @assignmentNote = N'負責統計圖表與篩選器 (4 pt)', @assignedByEmployeePk = @creatorPk;
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi4, @employeePk = @empIrene, @assignmentRole = N'需求驗收與指標定義', @allocationPercent = 50.00, @assignmentNote = N'負責部門 KPI 與敏捷指標核對 (4 pt)', @assignedByEmployeePk = @creatorPk;

    -- 工項 5：自動帶入 TYPE_PS_MODULE (5 點)
    exec storyPoint.spUpsertWorkItem
        @itemCode = 'WI-2026-005',
        @itemTitle = N'PowerShell Console 自動化模組、Azure 部署與多 Tab 管理工具',
        @itemDescription = N'開發 common.ps1、資料庫部署、DPAPI 加密設定、BCP CSV 雙軌 Log 以及 WinForms 多分頁管理介面。',
        @workItemTypePk = @tPs,
        @sprintName = N'2026-Q4 Sprint 2',
        @priorityLevel = 'medium',
        @storyPoint = null, -- 自動帶入代表類型 5.00 點
        @executionStatus = 'todo',
        @dueDate = '2026-11-10',
        @createdByEmployeePk = @creatorPk,
        @operatorAccount = 'admin';

    declare @wi5 int = (select top 1 pk from storyPoint.tbWorkItem where itemCode = 'WI-2026-005');
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi5, @employeePk = @empGrace, @assignmentRole = N'主要負責', @allocationPercent = 60.00, @assignmentNote = N'負責 PowerShell 核心模組與 Azure 部署 (3 pt)', @assignedByEmployeePk = @creatorPk;
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi5, @employeePk = @empJason, @assignmentRole = N'自動化驗證', @allocationPercent = 40.00, @assignmentNote = N'負責多 Tab UI 整合測試 (2 pt)', @assignedByEmployeePk = @creatorPk;

    -- 工項 6：自動帶入 TYPE_UI_ADJUST (1 點)
    exec storyPoint.spUpsertWorkItem
        @itemCode = 'WI-2026-006',
        @itemTitle = N'繁體中文敏捷估算操作指引與撲克牌ショートカット鍵盤熱鍵優化',
        @itemDescription = N'於投票大廳新增鍵盤數字快捷鍵出牌支援與繁體中文估算基準說明提示。',
        @workItemTypePk = @tUi,
        @sprintName = N'2026-Q4 Sprint 2',
        @priorityLevel = 'low',
        @storyPoint = null, -- 自動帶入代表類型 1.00 點
        @executionStatus = 'todo',
        @dueDate = '2026-11-12',
        @createdByEmployeePk = @creatorPk,
        @operatorAccount = 'admin';

    declare @wi6 int = (select top 1 pk from storyPoint.tbWorkItem where itemCode = 'WI-2026-006');
    exec storyPoint.spAssignWorkItemToEmployee @actionType = 'upsert', @workItemPk = @wi6, @employeePk = @empEmily, @assignmentRole = N'主要負責', @allocationPercent = 100.00, @assignmentNote = N'負責前端熱鍵綁定 (1 pt)', @assignedByEmployeePk = @creatorPk;
end
go
