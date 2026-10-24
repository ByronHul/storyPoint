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
declare @participantDefaultHash varchar(256) = '9b8769a4a742959a2d0298c36fb70623f2dfacda8436237df08d8dfd5b37374c'; -- pass123

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
-- 4. 預設工項類型設定 (storyPoint.tbWorkItemKind：專案 / 維運 / 顧問時數，支援增刪修)
-- ============================================================================
if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'project')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder, description, isActive
    )
    values (
        'project', N'專案', N'🏗️', 'blue', 10,
        N'專案建置與功能開發工項，具備明確起訖時間，達迄時自動轉為已完成並扣除 Story Point 及寄發通知信。',
        1
    );
end

if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'maintenance')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder, description, isActive
    )
    values (
        'maintenance', N'維運', N'🛠️', 'amber', 20,
        N'日常維運、例行巡檢與異常排除工項，具備起訖期間設定，達迄時自動轉態扣點並通知部門管理層與 HR。',
        1
    );
end

if not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = 'consultingHour')
begin
    insert into storyPoint.tbWorkItemKind (
        kindCode, kindName, badgeIcon, badgeColor, sortOrder, description, isActive
    )
    values (
        'consultingHour', N'顧問時數', N'⏳', 'purple', 30,
        N'客戶架構諮詢、技術顧問與驻點時數工項，具備合約/顧問起訖時間，達迄時自動轉態扣點並通知部門管理層與 HR。',
        1
    );
end
go
