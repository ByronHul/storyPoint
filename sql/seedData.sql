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
================================================================================
*/

set nocount on;
go

-- ============================================================================
-- 1. 部門資料初始化 (storyPoint.tbDepartment：保留 HR 並載入集英信誠部門)
-- ============================================================================
exec storyPoint.spUpsertDepartment
    @departmentCode = 'HR',
    @departmentName = N'HR',
    @parentDepartmentPk = null,
    @managerName = N'許立慧',
    @departmentNote = N'人力資源',
    @isActive = 1,
    @operatorAccount = 'system';

exec storyPoint.spUpsertDepartment
    @departmentCode = 'DB_INFRA',
    @departmentName = N'DB/INFra',
    @parentDepartmentPk = null,
    @managerName = N'胡百敬',
    @departmentNote = N'集英信誠 DB/INFra 資料庫與基礎架構顧問部門',
    @isActive = 1,
    @operatorAccount = 'system';

exec storyPoint.spUpsertDepartment
    @departmentCode = 'MGMT',
    @departmentName = N'管理',
    @parentDepartmentPk = null,
    @managerName = N'胡百敬',
    @departmentNote = N'集英信誠 經營管理、行政與財務部門',
    @isActive = 1,
    @operatorAccount = 'system';

exec storyPoint.spUpsertDepartment
    @departmentCode = 'BI',
    @departmentName = N'BI',
    @parentDepartmentPk = null,
    @managerName = N'黃雅玲',
    @departmentNote = N'集英信誠 BI 商業智慧與資料分析顧問部門',
    @isActive = 1,
    @operatorAccount = 'system';

exec storyPoint.spUpsertDepartment
    @departmentCode = 'DEV',
    @departmentName = N'開發',
    @parentDepartmentPk = null,
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
        @jobTitle = N'人資',
        @departmentPk = @deptHrPk,
        @roleName = 'admin',
        @isActive = 1,
        @operatorAccount = 'system';
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
    roleName varchar(32)
);

insert into @mentorEmployeeSeed (
    employeeCode, employeeName, loginAccount, passwordHash, emailAddress, jobTitle, departmentPk, roleName
)
values
    ('241101', N'陳昱達', 'kenny.chen',    @participantDefaultHash, 'Kenny.Chen@mentortrust.com',    N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('160101', N'柯乃云', 'grace.ko',      @participantDefaultHash, 'Grace.Ko@mentortrust.com',      N'行政助理',                   @deptMgmtPk,    'participant'),
    ('120801', N'林英豪', 'john.yh.lin',   @participantDefaultHash, 'john.yh.lin@mentortrust.com',   N'資料庫資深顧問',             @deptDbInfraPk, 'participant'),
    ('250302', N'陳怡之', 'zoe.chen',      @participantDefaultHash, 'Zoe.Chen@mentortrust.com',      N'商業智慧執行顧問',           @deptBiPk,      'participant'),
    ('240401', N'呂雯琳', 'wennifer.lu',   @participantDefaultHash, 'Wennifer.Lu@mentortrust.com',   N'商業智慧執行顧問',           @deptBiPk,      'participant'),
    ('240305', N'田晨揚', 'young.tien',    @participantDefaultHash, 'Young.Tien@mentortrust.com',    N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('240901', N'楊翔賀', 'ian.yang',      @participantDefaultHash, 'Ian.Yang@mentortrust.com',      N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('210203', N'林鈺燕', 'ariel.lin',     @participantDefaultHash, 'Ariel.Lin@mentortrust.com',     N'商業智慧執行顧問',           @deptBiPk,      'participant'),
    ('200701', N'陳皓澤', 'gary.chen',     @participantDefaultHash, 'Gary.Chen@mentortrust.com',     N'資深開發工程師',             @deptDevPk,     'participant'),
    ('200601', N'蔡明辰', 'mike.tsai',     @participantDefaultHash, 'Mike.Tsai@mentortrust.com',     N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('210101', N'莊孟儒', 'winnie.chuang', @participantDefaultHash, 'Winnie.Chuang@mentortrust.com', N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('130801', N'朱文彬', 'ben.ju',        @participantDefaultHash, 'Ben.Ju@mentortrust.com',        N'系統工程師',                 @deptDbInfraPk, 'participant'),
    ('100101', N'陸雲中', 'kent.lu',       @participantDefaultHash, 'Kent.Lu@mentortrust.com',       N'ALM/TFS執行顧問、資料庫執行顧問', @deptDbInfraPk, 'participant'),
    ('110602', N'林恩',   'bryan.lin',     @participantDefaultHash, 'Bryan.Lin@mentortrust.com',     N'BI資深執行顧問',             @deptDbInfraPk, 'participant'),
    ('81201',  N'胡百敬', 'byron.hu',      @adminDefaultHash,       'Byron.Hu@mentortrust.com',      N'董事長及資料庫資深顧問',     @deptMgmtPk,    'admin'),
    ('200201', N'陳輝瑞', 'ray.chen',      @participantDefaultHash, 'Ray.Chen@mentortrust.com',      N'資料庫資深顧問',             @deptDbInfraPk, 'participant'),
    ('180601', N'楊雅筑', 'zoe.yang',      @participantDefaultHash, 'Zoe.Yang@mentortrust.com',      N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('160701', N'張月桂', 'karen.chang',   @participantDefaultHash, 'Karen.Chang@mentortrust.com',   N'業務助理',                   @deptMgmtPk,    'participant'),
    ('170601', N'邱士宸', 'edison.chiu',   @participantDefaultHash, 'Edison.Chiu@mentortrust.com',   N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('190601', N'林沛汶', 'riley.lin',     @participantDefaultHash, 'Riley.Lin@mentortrust.com',     N'開發工程師',                 @deptDevPk,     'participant'),
    ('230802', N'林絹雅', 'doris.lin',     @participantDefaultHash, 'Doris.Lin@mentortrust.com',     N'業務專員',                   @deptMgmtPk,    'participant'),
    ('220703', N'簡郁倫', 'allen.jian',    @participantDefaultHash, 'Allen.Jian@mentortrust.com',    N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('220301', N'李功?',  'liv.lee',       @participantDefaultHash, 'Liv.Lee@mentortrust.com',       N'開發工程師',                 @deptDevPk,     'participant'),
    ('230801', N'蘇婕妤', 'jessie.su',     @participantDefaultHash, 'Jessie.Su@mentortrust.com',     N'商業智慧執行顧問',           @deptBiPk,      'participant'),
    ('211001', N'張棣棠', 'dillon.chang',  @participantDefaultHash, 'Dillon.Chang@mentortrust.com',  N'資料庫執行顧問',             @deptDbInfraPk, 'participant'),
    ('160302', N'邱宥樺', 'jill.chiu',     @participantDefaultHash, 'Jill.Chiu@mentortrust.com',     N'開發工程師',                 @deptDevPk,     'participant'),
    ('140901', N'黃振祖', 'jtrue.huang',   @participantDefaultHash, 'Jtrue.Huang@mentortrust.com',   N'資深架構師',                 @deptDevPk,     'participant'),
    ('160303', N'李靜芳', 'sandy.lee',     @participantDefaultHash, 'Sandy.Lee@mentortrust.com',     N'財務會計',                   @deptMgmtPk,    'participant'),
    ('200202', N'謝僑育', 'jeremy.shie',   @participantDefaultHash, 'Jeremy.Shie@mentortrust.com',   N'商業智慧執行顧問',           @deptBiPk,      'participant'),
    ('160601', N'黃雅玲', 'catty.huang',   @participantDefaultHash, 'Catty.Huang@mentortrust.com',   N'商業智慧資深顧問',           @deptBiPk,      'participant');

declare @idx int = 1;
declare @maxRow int = (select count(1) from @mentorEmployeeSeed);
declare @eCode varchar(64);
declare @eName nvarchar(128);
declare @eAcc varchar(128);
declare @eHash varchar(256);
declare @eMail varchar(256);
declare @eTitle nvarchar(128);
declare @eDept int;
declare @eRole varchar(32);

while @idx <= @maxRow
begin
    select
        @eCode = s.employeeCode,
        @eName = s.employeeName,
        @eAcc = s.loginAccount,
        @eHash = case when exists (select 1 from storyPoint.tbEmployee where loginAccount = s.loginAccount or employeeCode = s.employeeCode) then '' else s.passwordHash end,
        @eMail = s.emailAddress,
        @eTitle = s.jobTitle,
        @eDept = s.departmentPk,
        @eRole = s.roleName
    from @mentorEmployeeSeed as s
    where s.rowNum = @idx;

    exec storyPoint.spUpsertEmployee
        @employeePk = null,
        @employeeCode = @eCode,
        @employeeName = @eName,
        @loginAccount = @eAcc,
        @passwordHash = @eHash,
        @emailAddress = @eMail,
        @jobTitle = @eTitle,
        @departmentPk = @eDept,
        @roleName = @eRole,
        @isActive = 1,
        @operatorAccount = 'system';

    set @idx = @idx + 1;
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
