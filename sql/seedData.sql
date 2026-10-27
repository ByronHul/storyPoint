/*
================================================================================
檔案名稱: seedData.sql
用途說明: 初始化 Azure 多人參與 Story Point 系統之預設組織與帳號資料：
          1. 保留 HR 部門，並載入參照 phishing 專案資料庫內集英信誠 (mentorTrust) 之部門資料 (DB/INFra、管理、BI、開發)
          2. 保留 admin 與 raya 帳號，並載入集英信誠 (mentorTrust) 員工資料與預設登入帳密
          3. 載入系統標準 Planning Poker 撲克牌組設定 (tbCardDeck)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
修改歷程:
    2026-10-23 初版建立
    2026-10-23 依需求清理預設示範資料，僅保留 admin、raya 與 HR 部門，並同步載入 phishing 專案集英信誠 (mentorTrust) 之部門與員工資料
    2026-10-24 新增員工「是否為部門主管 (isDepartmentManager)」與「上層主管樹狀遞迴階層 (managerEmployeePk)」預設三層樹狀結構資料
================================================================================
*/

set nocount on;
go

-- ============================================================================
-- 1. 部門資料初始化 (storyPoint.tbDepartment：保留 HR 並載入集英信誠部門)
-- ============================================================================
exec storyPoint.spUpsertDepartment
    @departmentCode = 'MGMT',
    @departmentName = N'管理',
    @parentDepartmentPk = null,
    @managerName = N'胡百敬',
    @departmentNote = N'集英信誠 經營管理、行政與財務部門',
    @isActive = 1,
    @operatorAccount = 'system';

declare @rootDeptMgmtPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'MGMT');

exec storyPoint.spUpsertDepartment
    @departmentCode = 'HR',
    @departmentName = N'HR',
    @parentDepartmentPk = @rootDeptMgmtPk,
    @managerName = N'許立慧',
    @departmentNote = N'人力資源',
    @isActive = 1,
    @operatorAccount = 'system';

exec storyPoint.spUpsertDepartment
    @departmentCode = 'DB_INFRA',
    @departmentName = N'DB/INFra',
    @parentDepartmentPk = @rootDeptMgmtPk,
    @managerName = N'林英豪',
    @departmentNote = N'集英信誠 DB/INFra 資料庫與基礎架構顧問部門',
    @isActive = 1,
    @operatorAccount = 'system';

exec storyPoint.spUpsertDepartment
    @departmentCode = 'BI',
    @departmentName = N'BI',
    @parentDepartmentPk = @rootDeptMgmtPk,
    @managerName = N'黃雅玲',
    @departmentNote = N'集英信誠 BI 商業智慧與資料分析顧問部門',
    @isActive = 1,
    @operatorAccount = 'system';

exec storyPoint.spUpsertDepartment
    @departmentCode = 'DEV',
    @departmentName = N'開發',
    @parentDepartmentPk = @rootDeptMgmtPk,
    @managerName = N'黃振祖',
    @departmentNote = N'集英信誠 軟體開發與系統架構部門',
    @isActive = 1,
    @operatorAccount = 'system';
go

-- ============================================================================
-- 2. 員工與登入帳密初始化 (storyPoint.tbEmployee：保留 admin、raya 並載入集英信誠員工)
--    注意：若帳號已存在則傳入空密碼雜湊以無損保留既有已修改之密碼
-- ============================================================================
declare @deptHrPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'HR');
declare @deptDbInfraPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'DB_INFRA');
declare @deptMgmtPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'MGMT');
declare @deptBiPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'BI');
declare @deptDevPk int = (select top 1 pk from storyPoint.tbDepartment where departmentCode = 'DEV');

declare @adminDefaultHash varchar(256) = '0ef198f02d13c7364ebc9b7d548fd125e1678affd651c21ba25f23532acfc5b5';
declare @participantDefaultHash varchar(256) = 'b03ddf3ca2e714a6548e7495e2a03f5e824eaac9837cd7f159c67b90fb4b7342'; -- P@ssw0rd

if not exists (select 1 from storyPoint.tbEmployee where loginAccount = 'admin')
begin
    exec storyPoint.spUpsertEmployee
        @employeeCode = 'EMP001',
        @employeeName = N'胡資深架構師 (Byron)',
        @loginAccount = 'admin',
        @passwordHash = @adminDefaultHash,
        @emailAddress = 'byron.hu@company.com',
        @jobTitle = N'首席架構師 / Scrum Master',
        @departmentPk = @deptHrPk,
        @isDepartmentManager = 0,
        @managerEmployeePk = null,
        @roleName = 'admin',
        @isActive = 1,
        @operatorAccount = 'system';
end

if not exists (select 1 from storyPoint.tbEmployee where loginAccount = 'raya')
begin
    exec storyPoint.spUpsertEmployee
        @employeeCode = '250201',
        @employeeName = N'許立慧',
        @loginAccount = 'raya',
        @passwordHash = @adminDefaultHash,
        @emailAddress = 'raya.hsu@mentortrust.com',
        @jobTitle = N'部門經理 / 人資',
        @departmentPk = @deptHrPk,
        @isDepartmentManager = 1,
        @managerEmployeePk = null,
        @roleName = 'admin',
        @isActive = 1,
        @operatorAccount = 'system';
end
else
begin
    update storyPoint.tbEmployee
    set jobTitle = N'部門經理 / 人資',
        isDepartmentManager = 1,
        updatedAt = sysdatetime()
    where loginAccount = 'raya';
end

declare @mentorEmployeeSeed table (
    rowNum int identity(1,1) primary key,
    employeeCode varchar(64),
    employeeName nvarchar(128),
    loginAccount varchar(128),
    passwordHash varchar(256),
    emailAddress varchar(256),
    jobTitle nvarchar(128),
    departmentPk int,
    isDepartmentManager bit,
    roleName varchar(32)
);

insert into @mentorEmployeeSeed (
    employeeCode, employeeName, loginAccount, passwordHash, emailAddress, jobTitle, departmentPk, isDepartmentManager, roleName
)
values
    ('241101', N'陳昱達', 'kenny.chen',    @participantDefaultHash, 'Kenny.Chen@mentortrust.com',    N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('160101', N'柯乃云', 'grace.ko',      @participantDefaultHash, 'Grace.Ko@mentortrust.com',      N'行政助理',                       @deptMgmtPk,    0, 'participant'),
    ('120801', N'林英豪', 'john.yh.lin',   @participantDefaultHash, 'john.yh.lin@mentortrust.com',   N'部門經理 / 資料庫資深顧問',       @deptDbInfraPk, 1, 'participant'),
    ('250302', N'陳怡之', 'zoe.chen',      @participantDefaultHash, 'Zoe.Chen@mentortrust.com',      N'商業智慧執行顧問',               @deptBiPk,      0, 'participant'),
    ('240401', N'呂雯琳', 'wennifer.lu',   @participantDefaultHash, 'Wennifer.Lu@mentortrust.com',   N'商業智慧執行顧問',               @deptBiPk,      0, 'participant'),
    ('240305', N'田晨揚', 'young.tien',    @participantDefaultHash, 'Young.Tien@mentortrust.com',    N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('240901', N'楊翔賀', 'ian.yang',      @participantDefaultHash, 'Ian.Yang@mentortrust.com',      N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('210203', N'林鈺燕', 'ariel.lin',     @participantDefaultHash, 'Ariel.Lin@mentortrust.com',     N'商業智慧執行顧問',               @deptBiPk,      0, 'participant'),
    ('200701', N'陳皓澤', 'gary.chen',     @participantDefaultHash, 'Gary.Chen@mentortrust.com',     N'資深開發工程師',                 @deptDevPk,     0, 'participant'),
    ('200601', N'蔡明辰', 'mike.tsai',     @participantDefaultHash, 'Mike.Tsai@mentortrust.com',     N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('210101', N'莊孟儒', 'winnie.chuang', @participantDefaultHash, 'Winnie.Chuang@mentortrust.com', N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('130801', N'朱文彬', 'ben.ju',        @participantDefaultHash, 'Ben.Ju@mentortrust.com',        N'系統工程師',                     @deptDbInfraPk, 0, 'participant'),
    ('100101', N'陸雲中', 'kent.lu',       @participantDefaultHash, 'Kent.Lu@mentortrust.com',       N'ALM/TFS執行顧問、資料庫執行顧問', @deptDbInfraPk, 0, 'participant'),
    ('110602', N'林恩',   'bryan.lin',     @participantDefaultHash, 'Bryan.Lin@mentortrust.com',     N'BI資深執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('81201',  N'胡百敬', 'byron.hu',      @adminDefaultHash,       'Byron.Hu@mentortrust.com',      N'董事長兼部門經理',               @deptMgmtPk,    1, 'admin'),
    ('200201', N'陳輝瑞', 'ray.chen',      @participantDefaultHash, 'Ray.Chen@mentortrust.com',      N'資料庫資深顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('180601', N'楊雅筑', 'zoe.yang',      @participantDefaultHash, 'Zoe.Yang@mentortrust.com',      N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('160701', N'張月桂', 'karen.chang',   @participantDefaultHash, 'Karen.Chang@mentortrust.com',   N'業務助理',                       @deptMgmtPk,    0, 'participant'),
    ('170601', N'邱士宸', 'edison.chiu',   @participantDefaultHash, 'Edison.Chiu@mentortrust.com',   N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('190601', N'林沛汶', 'riley.lin',     @participantDefaultHash, 'Riley.Lin@mentortrust.com',     N'開發工程師',                     @deptDevPk,     0, 'participant'),
    ('230802', N'林絹雅', 'doris.lin',     @participantDefaultHash, 'Doris.Lin@mentortrust.com',     N'業務專員',                       @deptMgmtPk,    0, 'participant'),
    ('220703', N'簡郁倫', 'allen.jian',    @participantDefaultHash, 'Allen.Jian@mentortrust.com',    N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('220301', N'李功?',  'liv.lee',       @participantDefaultHash, 'Liv.Lee@mentortrust.com',       N'開發工程師',                     @deptDevPk,     0, 'participant'),
    ('230801', N'蘇婕妤', 'jessie.su',     @participantDefaultHash, 'Jessie.Su@mentortrust.com',     N'商業智慧執行顧問',               @deptBiPk,      0, 'participant'),
    ('211001', N'張棣棠', 'dillon.chang',  @participantDefaultHash, 'Dillon.Chang@mentortrust.com',  N'資料庫執行顧問',                 @deptDbInfraPk, 0, 'participant'),
    ('160302', N'邱宥樺', 'jill.chiu',     @participantDefaultHash, 'Jill.Chiu@mentortrust.com',     N'開發工程師',                     @deptDevPk,     0, 'participant'),
    ('140901', N'黃振祖', 'jtrue.huang',   @participantDefaultHash, 'Jtrue.Huang@mentortrust.com',   N'部門經理 / 資深架構師',           @deptDevPk,     1, 'participant'),
    ('160303', N'李靜芳', 'sandy.lee',     @participantDefaultHash, 'Sandy.Lee@mentortrust.com',     N'財務會計',                       @deptMgmtPk,    0, 'participant'),
    ('200202', N'謝僑育', 'jeremy.shie',   @participantDefaultHash, 'Jeremy.Shie@mentortrust.com',   N'商業智慧執行顧問',               @deptBiPk,      0, 'participant'),
    ('160601', N'黃雅玲', 'catty.huang',   @participantDefaultHash, 'Catty.Huang@mentortrust.com',   N'部門經理 / 商業智慧資深顧問',     @deptBiPk,      1, 'participant');

declare @idx int = 1;
declare @maxRow int = (select count(1) from @mentorEmployeeSeed);
declare @eCode varchar(64);
declare @eName nvarchar(128);
declare @eAcc varchar(128);
declare @eHash varchar(256);
declare @eMail varchar(256);
declare @eTitle nvarchar(128);
declare @eDept int;
declare @eIsMgr bit;
declare @eRole varchar(32);

while @idx <= @maxRow
begin
    declare @empAlreadyExists bit = 0;
    select
        @eCode = s.employeeCode,
        @eName = s.employeeName,
        @eAcc = s.loginAccount,
        @empAlreadyExists = case when exists (select 1 from storyPoint.tbEmployee where loginAccount = s.loginAccount or employeeCode = s.employeeCode) then 1 else 0 end,
        @eHash = case when exists (select 1 from storyPoint.tbEmployee where loginAccount = s.loginAccount or employeeCode = s.employeeCode) then '' else s.passwordHash end,
        @eMail = s.emailAddress,
        @eTitle = s.jobTitle,
        @eDept = s.departmentPk,
        @eIsMgr = s.isDepartmentManager,
        @eRole = s.roleName
    from @mentorEmployeeSeed as s
    where s.rowNum = @idx;

    declare @passIsMgr bit = case when @empAlreadyExists = 1 and @eIsMgr = 0 then null else @eIsMgr end;

    exec storyPoint.spUpsertEmployee
        @employeePk = null,
        @employeeCode = @eCode,
        @employeeName = @eName,
        @loginAccount = @eAcc,
        @passwordHash = @eHash,
        @emailAddress = @eMail,
        @jobTitle = @eTitle,
        @departmentPk = @eDept,
        @isDepartmentManager = @passIsMgr,
        @managerEmployeePk = null,
        @roleName = @eRole,
        @isActive = 1,
        @operatorAccount = 'system';

    set @idx = @idx + 1;
end

-- 將除 admin、byron (byron.hu)、raya 以外仍使用舊預設密碼雜湊之帳戶統一更新為新預設密碼 P@ssw0rd (絕不修改 admin、byron、raya 之密碼)
update storyPoint.tbEmployee
set passwordHash = @participantDefaultHash,
    updatedAt = sysdatetime()
where loginAccount not in ('admin', 'raya', 'byron', 'byron.hu')
  and passwordHash = '9b8769a4a742959a2d0298c36fb70623f2dfacda8436237df08d8dfd5b37374c';

-- ============================================================================
-- 2.1 建立全體員工「部門主管標記 (isDepartmentManager)」與「三層樹狀遞迴主管階層 (managerEmployeePk)」
--     第 1 層 (Root) : 胡百敬 (081201, 管理部董事長兼部門經理, managerEmployeePk = null, isDepartmentManager = 1)
--     第 2 層        : 各部門主管 (許立慧 HR、林英豪 DB/INFra、黃雅玲 BI、黃振祖 開發) + 管理部同仁 -> managerEmployeePk = 胡百敬.pk
--     第 3 層        : 各部門所屬一般同仁 -> managerEmployeePk = 該部門之部門主管.pk
-- ============================================================================
declare @byronPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'byron.hu');
declare @rayaPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'raya');
declare @johnLinPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'john.yh.lin');
declare @cattyHuangPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'catty.huang');
declare @jtrueHuangPk int = (select top 1 pk from storyPoint.tbEmployee where loginAccount = 'jtrue.huang');

-- 確保 5 位部門主管標記為 1，其餘同仁標記為 0 (若未手動設定其他主管)
update storyPoint.tbEmployee
set isDepartmentManager = case
        when loginAccount in ('byron.hu', 'raya', 'john.yh.lin', 'catty.huang', 'jtrue.huang') then 1
        else isDepartmentManager
    end,
    updatedAt = sysdatetime()
where loginAccount in ('byron.hu', 'raya', 'john.yh.lin', 'catty.huang', 'jtrue.huang', 'admin');

-- Root 節點：胡百敬 (managerEmployeePk = null)
update storyPoint.tbEmployee
set managerEmployeePk = null,
    isDepartmentManager = 1,
    updatedAt = sysdatetime()
where pk = @byronPk;

-- 第 2 層：各部門主管 (許立慧、林英豪、黃雅玲、黃振祖) 及管理部直屬同仁 -> 上層主管為 胡百敬
update storyPoint.tbEmployee
set managerEmployeePk = @byronPk,
    updatedAt = sysdatetime()
where pk in (@rayaPk, @johnLinPk, @cattyHuangPk, @jtrueHuangPk)
   or (departmentPk = @deptMgmtPk and pk <> @byronPk);

-- 第 3 層：HR 部門非主管同仁 -> 上層主管為 許立慧 (人資主管)
update storyPoint.tbEmployee
set managerEmployeePk = @rayaPk,
    isDepartmentManager = 0,
    updatedAt = sysdatetime()
where departmentPk = @deptHrPk
  and pk <> @rayaPk
  and isDepartmentManager = 0;

-- 第 3 層：DB/INFra 部門非主管同仁 -> 上層主管為 林英豪 (DB/INFra 部門主管)
update storyPoint.tbEmployee
set managerEmployeePk = @johnLinPk,
    updatedAt = sysdatetime()
where departmentPk = @deptDbInfraPk
  and pk <> @johnLinPk
  and isDepartmentManager = 0;

-- 第 3 層：BI 部門非主管同仁 -> 上層主管為 黃雅玲 (BI 部門主管)
update storyPoint.tbEmployee
set managerEmployeePk = @cattyHuangPk,
    updatedAt = sysdatetime()
where departmentPk = @deptBiPk
  and pk <> @cattyHuangPk
  and isDepartmentManager = 0;

-- 第 3 層：開發部門非主管同仁 -> 上層主管為 黃振祖 (開發部部門主管)
update storyPoint.tbEmployee
set managerEmployeePk = @jtrueHuangPk,
    updatedAt = sysdatetime()
where departmentPk = @deptDevPk
  and pk <> @jtrueHuangPk
  and isDepartmentManager = 0;
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
-- 4. 預設工項類型與主要特徵設定 (storyPoint.tbWorkItemKind：專案 / 維運 / 顧問時數 / 教育訓練 / 開發產品，支援增刪修查)
-- ============================================================================
if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'project')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        mainFeatureSummary, estimationTrait, timeAndDeliverableTrait, description, isActive
    )
    values (
        'project', N'專案', N'🏗️', 'blue', 10,
        N'階段性目標導向、跨模組需求開發與系統建置、明確里程碑與範圍界定',
        N'依功能複雜度、跨系統整合難度與未知風險綜合盲投估算 (常見 3 ~ 21 pt)',
        N'中長期 Sprint 或專案起訖週期｜交付上線系統與驗收文件｜達迄時自動轉已完成、扣點並通知部門主管與 HR',
        N'專案建置與功能開發工項，具備明確起訖時間，達迄時自動轉為已完成並扣除 Story Point 及寄發通知信。',
        1
    );
end
else
begin
    update storyPoint.tbWorkItemKind
    set mainFeatureSummary = isnull(nullif(ltrim(rtrim(mainFeatureSummary)), N''), N'階段性目標導向、跨模組需求開發與系統建置、明確里程碑與範圍界定'),
        estimationTrait = isnull(nullif(ltrim(rtrim(estimationTrait)), N''), N'依功能複雜度、跨系統整合難度與未知風險綜合盲投估算 (常見 3 ~ 21 pt)'),
        timeAndDeliverableTrait = isnull(nullif(ltrim(rtrim(timeAndDeliverableTrait)), N''), N'中長期 Sprint 或專案起訖週期｜交付上線系統與驗收文件｜達迄時自動轉已完成、扣點並通知部門主管與 HR'),
        updatedAt = sysdatetime()
    where kindCode = 'project';
end

if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'maintenance')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        mainFeatureSummary, estimationTrait, timeAndDeliverableTrait, description, isActive
    )
    values (
        'maintenance', N'維運', N'🛠️', 'amber', 20,
        N'常態性系統穩定度維持、例行健康巡檢、問題排查 (Troubleshooting) 與效能調校',
        N'依處理頻率、SLA 緊急程度、環境複雜度與影響範圍估算 (常見 0.5 ~ 5 pt)',
        N'短週期或月/季例行維運期間｜交付問題修復紀錄與巡檢報告｜達迄時自動轉已完成、扣點並通知部門主管與 HR',
        N'日常維運、例行巡檢與異常排除工項，具備起訖期間設定，達迄時自動轉態扣點並通知部門管理層與 HR。',
        1
    );
end
else
begin
    update storyPoint.tbWorkItemKind
    set mainFeatureSummary = isnull(nullif(ltrim(rtrim(mainFeatureSummary)), N''), N'常態性系統穩定度維持、例行健康巡檢、問題排查 (Troubleshooting) 與效能調校'),
        estimationTrait = isnull(nullif(ltrim(rtrim(estimationTrait)), N''), N'依處理頻率、SLA 緊急程度、環境複雜度與影響範圍估算 (常見 0.5 ~ 5 pt)'),
        timeAndDeliverableTrait = isnull(nullif(ltrim(rtrim(timeAndDeliverableTrait)), N''), N'短週期或月/季例行維運期間｜交付問題修復紀錄與巡檢報告｜達迄時自動轉已完成、扣點並通知部門主管與 HR'),
        updatedAt = sysdatetime()
    where kindCode = 'maintenance';
end

if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'consultingHour')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        mainFeatureSummary, estimationTrait, timeAndDeliverableTrait, description, isActive
    )
    values (
        'consultingHour', N'顧問時數', N'⏳', 'purple', 30,
        N'專業技術知識輸出、客戶架構診斷、技術顧問諮詢與專家駐點支援服務',
        N'依投入顧問人天/時數、專業技術深度與交付報告規模估算 (常見 1 ~ 8 pt)',
        N'單次駐點或顧問合約起訖期間｜交付架構建議書、健檢報告與時數簽認單｜達迄時自動轉已完成、扣點並通知部門主管與 HR',
        N'客戶架構諮詢、技術顧問與駐點時數工項，具備合約/顧問起訖時間，達迄時自動轉態扣點並通知部門管理層與 HR。',
        1
    );
end
else
begin
    update storyPoint.tbWorkItemKind
    set mainFeatureSummary = isnull(nullif(ltrim(rtrim(mainFeatureSummary)), N''), N'專業技術知識輸出、客戶架構診斷、技術顧問諮詢與專家駐點支援服務'),
        estimationTrait = isnull(nullif(ltrim(rtrim(estimationTrait)), N''), N'依投入顧問人天/時數、專業技術深度與交付報告規模估算 (常見 1 ~ 8 pt)'),
        timeAndDeliverableTrait = isnull(nullif(ltrim(rtrim(timeAndDeliverableTrait)), N''), N'單次駐點或顧問合約起訖期間｜交付架構建議書、健檢報告與時數簽認單｜達迄時自動轉已完成、扣點並通知部門主管與 HR'),
        updatedAt = sysdatetime()
    where kindCode = 'consultingHour';
end

if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'training')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        mainFeatureSummary, estimationTrait, timeAndDeliverableTrait, description, isActive
    )
    values (
        'training', N'教育訓練', N'🎓', 'green', 40,
        N'內部技術傳承、外部客戶課程講授、實機 Lab 教材編撰與新人培訓賦能',
        N'依課程授課時數、新教材研發比例、Lab 環境準備與授課難度估算 (常見 1 ~ 8 pt)',
        N'單日課程或培訓梯次起訖期間｜交付課程簡報、實作講義與完訓紀錄｜達迄時自動轉已完成、扣點並通知部門主管與 HR',
        N'內部技術傳承、外部課程講授與新人教育訓練工項，具備課程/培訓起訖時間，達迄時自動轉態扣點並通知部門管理層與 HR。',
        1
    );
end
else
begin
    update storyPoint.tbWorkItemKind
    set mainFeatureSummary = isnull(nullif(ltrim(rtrim(mainFeatureSummary)), N''), N'內部技術傳承、外部客戶課程講授、實機 Lab 教材編撰與新人培訓賦能'),
        estimationTrait = isnull(nullif(ltrim(rtrim(estimationTrait)), N''), N'依課程授課時數、新教材研發比例、Lab 環境準備與授課難度估算 (常見 1 ~ 8 pt)'),
        timeAndDeliverableTrait = isnull(nullif(ltrim(rtrim(timeAndDeliverableTrait)), N''), N'單日課程或培訓梯次起訖期間｜交付課程簡報、實作講義與完訓紀錄｜達迄時自動轉已完成、扣點並通知部門主管與 HR'),
        updatedAt = sysdatetime()
    where kindCode = 'training';
end

if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'productDevelopment')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        mainFeatureSummary, estimationTrait, timeAndDeliverableTrait, description, isActive
    )
    values (
        'productDevelopment', N'開發產品', N'🚀', 'red', 50,
        N'自有產品核心研發、技術架構創新、標準化模組封裝與產品版本迭代 (Roadmap)',
        N'依技術創新度、通用架構擴充性、自動化測試覆蓋率與產品化標準估算 (常見 3 ~ 21 pt)',
        N'雙週 Sprint 或產品版本起訖週期｜交付標準產品模組、API 與發行套件｜達迄時自動轉已完成、扣點並通知部門主管與 HR',
        N'自有產品研發、核心模組創新與產品版本迭代工項，具備開發階段起訖時間，達迄時自動轉態扣點並通知部門管理層與 HR。',
        1
    );
end
else
begin
    update storyPoint.tbWorkItemKind
    set mainFeatureSummary = isnull(nullif(ltrim(rtrim(mainFeatureSummary)), N''), N'自有產品核心研發、技術架構創新、標準化模組封裝與產品版本迭代 (Roadmap)'),
        estimationTrait = isnull(nullif(ltrim(rtrim(estimationTrait)), N''), N'依技術創新度、通用架構擴充性、自動化測試覆蓋率與產品化標準估算 (常見 3 ~ 21 pt)'),
        timeAndDeliverableTrait = isnull(nullif(ltrim(rtrim(timeAndDeliverableTrait)), N''), N'雙週 Sprint 或產品版本起訖週期｜交付標準產品模組、API 與發行套件｜達迄時自動轉已完成、扣點並通知部門主管與 HR'),
        updatedAt = sysdatetime()
    where kindCode = 'productDevelopment';
end
go

-- ============================================================================
-- 4.1 同步預設工項類型 (tbWorkItemKind：專案 / 維運 / 顧問時數 / 教育訓練 / 開發產品...等) 至工項代表類型 (tbWorkItemType)
--     確保「目標工項代表類型」下拉選單直接帶入專案、維運、顧問時數、教育訓練、開發產品...等代表類型，
--     並自動清理過往誤由盲投議程標題產生的 TYPE_20* 暫存紀錄
-- ============================================================================
declare @defaultDeckPk int = (
    select top 1 pk
    from storyPoint.tbCardDeck
    where isActive = 1
    order by isDefault desc, pk asc
);

update wit
set wit.typeName = wik.kindName,
    wit.categoryName = wik.kindName,
    wit.defaultWorkItemKind = wik.kindCode,
    wit.description = isnull(nullif(ltrim(rtrim(wik.mainFeatureSummary)), N''), isnull(wik.description, wit.description)),
    wit.isActive = wik.isActive,
    wit.updatedAt = sysdatetime()
from storyPoint.tbWorkItemType as wit
inner join storyPoint.tbWorkItemKind as wik
    on wit.typeCode = wik.kindCode;

insert into storyPoint.tbWorkItemType (
    typeCode, typeName, categoryName, defaultWorkItemKind, description,
    standardStoryPoint, defaultCardDeckPk, estimationStatus, isActive, createdAt, updatedAt
)
select
    wik.kindCode,
    wik.kindName,
    wik.kindName,
    wik.kindCode,
    isnull(nullif(ltrim(rtrim(wik.mainFeatureSummary)), N''), isnull(wik.description, wik.kindName)),
    null,
    @defaultDeckPk,
    'pending',
    1,
    sysdatetime(),
    sysdatetime()
from storyPoint.tbWorkItemKind as wik
where wik.isActive = 1
  and not exists (
      select 1
      from storyPoint.tbWorkItemType as wit
      where wit.typeCode = wik.kindCode
  )
order by wik.sortOrder asc, wik.pk asc;

declare @projectTypePk int = (
    select top 1 pk
    from storyPoint.tbWorkItemType
    where typeCode = 'project'
    order by pk asc
);

if @projectTypePk is not null
begin
    update wi
    set wi.workItemTypePk = isnull(kindWit.pk, @projectTypePk),
        wi.updatedAt = sysdatetime()
    from storyPoint.tbWorkItem as wi
    left join storyPoint.tbWorkItemType as curWit
        on wi.workItemTypePk = curWit.pk
    left join storyPoint.tbWorkItemType as kindWit
        on kindWit.typeCode = wi.workItemKind
    where curWit.pk is null
       or curWit.typeCode like 'TYPE[_]20%'
       or curWit.typeCode like 'TYPE[_]DEF[_]%';

    update vs
    set vs.workItemTypePk = isnull(kindWit.pk, @projectTypePk),
        vs.updatedAt = sysdatetime()
    from storyPoint.tbVotingSession as vs
    inner join storyPoint.tbWorkItemType as oldWit
        on vs.workItemTypePk = oldWit.pk
    left join storyPoint.tbWorkItemType as kindWit
        on kindWit.typeCode = oldWit.defaultWorkItemKind
    where oldWit.typeCode like 'TYPE[_]20%'
       or oldWit.typeCode like 'TYPE[_]DEF[_]%';

    update ph
    set ph.workItemTypePk = isnull(kindWit.pk, @projectTypePk)
    from storyPoint.tbPointHistory as ph
    inner join storyPoint.tbWorkItemType as oldWit
        on ph.workItemTypePk = oldWit.pk
    left join storyPoint.tbWorkItemType as kindWit
        on kindWit.typeCode = oldWit.defaultWorkItemKind
    where oldWit.typeCode like 'TYPE[_]20%'
       or oldWit.typeCode like 'TYPE[_]DEF[_]%';

    delete from storyPoint.tbWorkItemType
    where typeCode like 'TYPE[_]20%'
       or typeCode like 'TYPE[_]DEF[_]%';
end
go

-- ============================================================================
-- 5. 代表圖示庫預設清單 (storyPoint.tbWorkItemIcon：提供多種內建 Emoji 與彩色 SVG 代表圖示，並支援自行上傳新增)
-- ============================================================================
declare @presetIconTable table (
    iconCode varchar(64) not null,
    iconName nvarchar(128) not null,
    iconCategory nvarchar(64) not null,
    iconKind varchar(32) not null,
    iconValue nvarchar(512) not null,
    mimeType varchar(64) null,
    sortOrder int not null
);

insert into @presetIconTable (iconCode, iconName, iconCategory, iconKind, iconValue, mimeType, sortOrder)
values
    -- (A) 內建彩色向量 SVG 代表圖示 (12 款)
    ('svg_project_blueprint', N'專案藍圖 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/project-blueprint.svg', 'image/svg+xml', 10),
    ('svg_maintenance_gear', N'系統維運齒輪 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/maintenance-gear.svg', 'image/svg+xml', 20),
    ('svg_consulting_clock', N'顧問專業時數 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/consulting-clock.svg', 'image/svg+xml', 30),
    ('svg_training_cap', N'教育訓練學位帽 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/training-cap.svg', 'image/svg+xml', 40),
    ('svg_product_rocket', N'產品研發火箭 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/product-rocket.svg', 'image/svg+xml', 50),
    ('svg_database_server', N'SQL 資料庫叢集 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/database-server.svg', 'image/svg+xml', 60),
    ('svg_cloud_azure', N'Azure 雲端服務 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/cloud-azure.svg', 'image/svg+xml', 70),
    ('svg_security_shield', N'資安防護盾牌 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/security-shield.svg', 'image/svg+xml', 80),
    ('svg_bi_analytics', N'BI 商業智慧圖表 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/bi-analytics.svg', 'image/svg+xml', 90),
    ('svg_ai_brain', N'AI 智能引擎 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/ai-brain.svg', 'image/svg+xml', 100),
    ('svg_api_integration', N'API 跨系統整合 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/api-integration.svg', 'image/svg+xml', 110),
    ('svg_support_headset', N'技術支援客服 (彩色圖示)', N'彩色向量圖示 (SVG)', 'presetSvg', N'/icons/presets/support-headset.svg', 'image/svg+xml', 120),
    -- (B) 內建常用符號與 Emoji 代表圖示 (24 款)
    ('emoji_project', N'專案建置', N'核心工項符號', 'emoji', N'🏗️', null, 210),
    ('emoji_maintenance', N'系統維運', N'核心工項符號', 'emoji', N'🛠️', null, 220),
    ('emoji_consulting', N'顧問時數', N'核心工項符號', 'emoji', N'⏳', null, 230),
    ('emoji_training', N'教育訓練', N'核心工項符號', 'emoji', N'🎓', null, 240),
    ('emoji_product', N'開發產品', N'核心工項符號', 'emoji', N'🚀', null, 250),
    ('emoji_pin', N'標準通用', N'核心工項符號', 'emoji', N'📌', null, 260),
    ('emoji_code', N'軟體開發', N'技術與架構符號', 'emoji', N'💻', null, 270),
    ('emoji_database', N'資料庫與倉儲', N'技術與架構符號', 'emoji', N'🗄️', null, 280),
    ('emoji_cloud', N'雲端架構', N'技術與架構符號', 'emoji', N'☁️', null, 290),
    ('emoji_security', N'資安合規', N'技術與架構符號', 'emoji', N'🛡️', null, 300),
    ('emoji_bi', N'數據分析與報表', N'技術與架構符號', 'emoji', N'📊', null, 310),
    ('emoji_ai', N'AI 與自動化', N'技術與架構符號', 'emoji', N'🤖', null, 320),
    ('emoji_performance', N'效能調校', N'技術與架構符號', 'emoji', N'⚡', null, 330),
    ('emoji_api', N'API 介面整合', N'技術與架構符號', 'emoji', N'🔗', null, 340),
    ('emoji_mobile', N'行動與前端應用', N'技術與架構符號', 'emoji', N'📱', null, 350),
    ('emoji_bugfix', N'問題修復與除錯', N'管理與支援符號', 'emoji', N'🔧', null, 360),
    ('emoji_qa', N'測試與品質驗證', N'管理與支援符號', 'emoji', N'🧪', null, 370),
    ('emoji_release', N'版本封裝與部署', N'管理與支援符號', 'emoji', N'📦', null, 380),
    ('emoji_support', N'客戶駐點與支援', N'管理與支援符號', 'emoji', N'📞', null, 390),
    ('emoji_doc', N'技術文件與規範', N'管理與支援符號', 'emoji', N'📑', null, 400),
    ('emoji_milestone', N'策略目標與里程碑', N'管理與支援符號', 'emoji', N'🎯', null, 410),
    ('emoji_admin', N'部門行政與內控', N'管理與支援符號', 'emoji', N'🏢', null, 420),
    ('emoji_poc', N'創新研發與 POC', N'管理與支援符號', 'emoji', N'💡', null, 430),
    ('emoji_audit', N'健檢與問題排查', N'管理與支援符號', 'emoji', N'🔍', null, 440);

insert into storyPoint.tbWorkItemIcon (
    iconCode, iconName, iconCategory, iconKind, iconValue,
    mimeType, iconDataBase64, sortOrder, isCustomUpload, isActive, uploadedBy
)
select
    src.iconCode, src.iconName, src.iconCategory, src.iconKind, src.iconValue,
    src.mimeType, null, src.sortOrder, 0, 1, 'system'
from @presetIconTable as src
where not exists (
    select 1
    from storyPoint.tbWorkItemIcon as tgt
    where tgt.iconCode = src.iconCode
);
go
