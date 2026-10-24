/*
================================================================================
檔案名稱: storedProcedure.sql
用途說明: 建立 Azure 多人參與 Story Point 系統之所有預存程序 (storyPoint.sp*)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明: 各預存程序內均具備詳盡參數說明與標準註解區塊
修改歷程:
    2026-10-23 初版建立，全小寫關鍵字、兩節式名稱、禁止 select *、第一行 set nocount on、
               完整 try...catch 例外處理並統一呼叫 storyPoint.spWriteLog 記錄執行耗時與結果
================================================================================
*/

set nocount on;
go

-- ============================================================================
-- 1. 標準寫入系統與 SP 執行紀錄預存程序 (storyPoint.spWriteLog)
-- ============================================================================
/*
用途說明: 將 Stored Procedure 或外部系統的執行過程、耗時、結果與錯誤寫入 storyPoint.tbLog
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @logLevel           tinyint       : Log 層級 (0:不寫, 1:錯誤, 2:警告, 3:資訊, 4:除錯)
    @sourceName         varchar(128)  : 來源模組名稱 (如 tsql, powershell, webPortal)
    @objectName         varchar(128)  : 執行物件或預存程序名稱
    @durationMillisecond int          : 執行耗時 (毫秒)
    @executionResult    nvarchar(256) : 執行結果摘要
    @errorCode          int           : 錯誤碼 (無錯誤時為 null)
    @errorLine          int           : 錯誤行號 (無錯誤時為 null)
    @errorMessage       nvarchar(max) : 錯誤訊息明細
    @operatorAccount    varchar(128)  : 操作人員帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spWriteLog
    @logLevel tinyint = 3,
    @sourceName varchar(128) = 'tsql',
    @objectName nvarchar(128),
    @durationMillisecond int = 0,
    @executionResult nvarchar(256),
    @errorCode int = null,
    @errorLine int = null,
    @errorMessage nvarchar(max) = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    begin try
        if @logLevel > 0
        begin
            insert into storyPoint.tbLog (
                logTime, logLevel, sourceName, objectName, durationMillisecond,
                executionResult, errorCode, errorLine, errorMessage, operatorAccount
            )
            values (
                sysdatetime(),
                @logLevel,
                isnull(@sourceName, 'tsql'),
                isnull(@objectName, N'unknown'),
                isnull(@durationMillisecond, 0),
                isnull(@executionResult, N'完成'),
                @errorCode,
                @errorLine,
                @errorMessage,
                isnull(@operatorAccount, 'system')
            );
        end
    end try
    begin catch
        -- 防止 Log 寫入本身失敗導致上層交易中斷
        declare @ignoreError int = error_number();
    end catch
end
go

-- ============================================================================
-- 2. 員工登入驗證預存程序 (storyPoint.spVerifyEmployeeLogin)
-- ============================================================================
/*
用途說明: 驗證員工登入帳號與密碼雜湊，成功時更新最後登入時間並回傳員工與部門角色資訊
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @loginAccount varchar(128) : 員工登入帳號
    @passwordHash varchar(256) : 密碼 SHA-256 雜湊值
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spVerifyEmployeeLogin
    @loginAccount varchar(128),
    @passwordHash varchar(256)
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @employeePk int = null;

    begin try
        select
            @employeePk = e.pk
        from storyPoint.tbEmployee as e
        where e.loginAccount = @loginAccount
          and e.passwordHash = @passwordHash
          and e.isActive = 1;

        if @employeePk is not null
        begin
            update storyPoint.tbEmployee
            set lastLoginTime = sysdatetime()
            where pk = @employeePk;

            set @durationMs = datediff(millisecond, @startTime, sysdatetime());
            exec storyPoint.spWriteLog
                @logLevel = 3,
                @sourceName = 'tsql',
                @objectName = 'storyPoint.spVerifyEmployeeLogin',
                @durationMillisecond = @durationMs,
                @executionResult = N'員工登入驗證成功',
                @operatorAccount = @loginAccount;
        end
        else
        begin
            set @durationMs = datediff(millisecond, @startTime, sysdatetime());
            exec storyPoint.spWriteLog
                @logLevel = 2,
                @sourceName = 'tsql',
                @objectName = 'storyPoint.spVerifyEmployeeLogin',
                @durationMillisecond = @durationMs,
                @executionResult = N'員工登入驗證失敗：帳號不存在、已停用或密碼錯誤',
                @operatorAccount = @loginAccount;
        end

        select
            e.pk as employeePk,
            e.employeeCode,
            e.employeeName,
            e.loginAccount,
            e.emailAddress,
            e.jobTitle,
            e.departmentPk,
            d.departmentCode,
            d.departmentName,
            e.isDepartmentManager,
            e.managerEmployeePk,
            e.roleName,
            e.isActive,
            e.lastLoginTime
        from storyPoint.tbEmployee as e
        inner join storyPoint.tbDepartment as d
            on e.departmentPk = d.pk
        where e.pk = @employeePk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spVerifyEmployeeLogin',
            @durationMillisecond = @durationMs,
            @executionResult = N'執行發生例外錯誤',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @loginAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 3. 新增或修改部門資料預存程序 (storyPoint.spUpsertDepartment)
-- ============================================================================
/*
用途說明: 新增或更新部門資料 (tbDepartment)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @departmentPk       int           : 部門主鍵 (0 或 null 代表依代碼新增或更新)
    @departmentCode     varchar(64)   : 部門代碼
    @departmentName     nvarchar(128) : 部門名稱
    @parentDepartmentPk int           : 上層部門主鍵
    @managerName        nvarchar(128) : 部門主管姓名
    @departmentNote     nvarchar(512) : 部門備註
    @isActive           bit           : 是否啟用
    @operatorAccount    varchar(128)  : 操作者帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spUpsertDepartment
    @departmentPk int = null,
    @departmentCode varchar(64),
    @departmentName nvarchar(128),
    @parentDepartmentPk int = null,
    @managerName nvarchar(128) = null,
    @departmentNote nvarchar(512) = null,
    @isActive bit = 1,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@departmentPk, 0);

    begin try
        if @targetPk <= 0
        begin
            select @targetPk = pk
            from storyPoint.tbDepartment
            where departmentCode = @departmentCode;
        end

        if isnull(@targetPk, 0) > 0 and exists (select 1 from storyPoint.tbDepartment where pk = @targetPk)
        begin
            update storyPoint.tbDepartment
            set departmentCode = @departmentCode,
                departmentName = @departmentName,
                parentDepartmentPk = case when isnull(@parentDepartmentPk, 0) <= 0 or @parentDepartmentPk = @targetPk then null else @parentDepartmentPk end,
                managerName = @managerName,
                departmentNote = @departmentNote,
                isActive = isnull(@isActive, 1),
                updatedAt = sysdatetime()
            where pk = @targetPk;
        end
        else
        begin
            insert into storyPoint.tbDepartment (
                departmentCode, departmentName, parentDepartmentPk, managerName,
                departmentNote, isActive, createdAt, updatedAt
            )
            values (
                @departmentCode,
                @departmentName,
                case when isnull(@parentDepartmentPk, 0) <= 0 then null else @parentDepartmentPk end,
                @managerName,
                @departmentNote,
                isnull(@isActive, 1),
                sysdatetime(),
                sysdatetime()
            );
            set @targetPk = scope_identity();
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertDepartment',
            @durationMillisecond = @durationMs,
            @executionResult = N'部門資料儲存成功',
            @operatorAccount = @operatorAccount;

        select
            d.pk,
            d.departmentCode,
            d.departmentName,
            d.parentDepartmentPk,
            d.managerName,
            d.departmentNote,
            d.isActive
        from storyPoint.tbDepartment as d
        where d.pk = @targetPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertDepartment',
            @durationMillisecond = @durationMs,
            @executionResult = N'部門資料儲存失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 4. 新增或修改員工與登入帳密預存程序 (storyPoint.spUpsertEmployee)
-- ============================================================================
/*
用途說明: 新增或更新員工資料與登入帳密 (tbEmployee)，支援設定是否為部門主管 (@isDepartmentManager)
          與樹狀遞迴階層之上層主管員工主鍵 (@managerEmployeePk)，並內建遞迴循環參照防呆與自動部門主管樹狀關聯。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @employeePk          int           : 員工主鍵 (0 或 null 代表依員工編號/帳號新增或更新)
    @employeeCode        varchar(64)   : 員工編號
    @employeeName        nvarchar(128) : 員工姓名
    @loginAccount        varchar(128)  : 登入帳號
    @passwordHash        varchar(256)  : 密碼雜湊值 (修改時若傳空值則不變更密碼)
    @emailAddress        varchar(256)  : 電子郵件
    @jobTitle            nvarchar(128) : 職稱
    @departmentPk        int           : 所屬部門主鍵
    @isDepartmentManager bit           : 是否為部門主管 (1=部門主管, 0=一般同仁; null 時自動依既有設定或職稱推斷)
    @managerEmployeePk   int           : 樹狀遞迴階層之上層直屬/部門主管員工主鍵 (-1=設為頂層根節點 null; 0 或 null=自動依部門主管建立樹狀階層)
    @roleName            varchar(32)   : 系統角色 ('admin' 或 'participant')
    @isActive            bit           : 是否在職啟用
    @operatorAccount     varchar(128)  : 操作者帳號
修改歷程:
    2026-10-23 初版建立
    2026-10-24 新增 @isDepartmentManager (是否為部門主管) 與 @managerEmployeePk (樹狀遞迴階層上層主管)
*/
create or alter procedure storyPoint.spUpsertEmployee
    @employeePk int = null,
    @employeeCode varchar(64),
    @employeeName nvarchar(128),
    @loginAccount varchar(128),
    @passwordHash varchar(256) = null,
    @emailAddress varchar(256),
    @jobTitle nvarchar(128),
    @departmentPk int,
    @isDepartmentManager bit = null,
    @managerEmployeePk int = null,
    @roleName varchar(32) = 'participant',
    @isActive bit = 1,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@employeePk, 0);
    declare @resolvedIsDeptMgr bit = 0;
    declare @resolvedManagerPk int = null;
    declare @effectiveDeptPk int = @departmentPk;

    begin try
        if @targetPk <= 0
        begin
            select top 1 @targetPk = pk
            from storyPoint.tbEmployee
            where loginAccount = @loginAccount;
        end

        if @targetPk <= 0
        begin
            select top 1 @targetPk = pk
            from storyPoint.tbEmployee
            where employeeCode = @employeeCode;
        end

        if isnull(@targetPk, 0) > 0 and exists (select 1 from storyPoint.tbEmployee where pk = @targetPk)
        begin
            declare @safeEmployeeCode varchar(64) = @employeeCode;
            if exists (select 1 from storyPoint.tbEmployee where employeeCode = @safeEmployeeCode and pk <> @targetPk)
            begin
                select @safeEmployeeCode = employeeCode
                from storyPoint.tbEmployee
                where pk = @targetPk;
            end

            declare @safeLoginAccount varchar(128) = @loginAccount;
            if exists (select 1 from storyPoint.tbEmployee where loginAccount = @safeLoginAccount and pk <> @targetPk)
            begin
                select @safeLoginAccount = loginAccount
                from storyPoint.tbEmployee
                where pk = @targetPk;
            end

            declare @existingIsDeptMgr bit = 0;
            declare @existingManagerPk int = null;
            declare @existingDeptPk int = 1;
            declare @existingJobTitle nvarchar(128) = N'';

            select
                @existingIsDeptMgr = isDepartmentManager,
                @existingManagerPk = managerEmployeePk,
                @existingDeptPk = departmentPk,
                @existingJobTitle = jobTitle
            from storyPoint.tbEmployee
            where pk = @targetPk;

            set @effectiveDeptPk = case when isnull(@departmentPk, 0) > 0 then @departmentPk else @existingDeptPk end;
            declare @effectiveTitle nvarchar(128) = isnull(nullif(ltrim(rtrim(@jobTitle)), ''), @existingJobTitle);

            if @isDepartmentManager is not null
            begin
                set @resolvedIsDeptMgr = @isDepartmentManager;
            end
            else if @existingIsDeptMgr = 1 or @effectiveTitle like N'%部門經理%' or @effectiveTitle like N'%董事長%'
            begin
                set @resolvedIsDeptMgr = 1;
            end
            else
            begin
                set @resolvedIsDeptMgr = 0;
            end

            if @managerEmployeePk = -1
            begin
                set @resolvedManagerPk = null;
            end
            else if isnull(@managerEmployeePk, 0) > 0 and @managerEmployeePk <> @targetPk and exists (select 1 from storyPoint.tbEmployee where pk = @managerEmployeePk)
            begin
                set @resolvedManagerPk = @managerEmployeePk;
            end
            else if @existingManagerPk is not null and @existingDeptPk = @effectiveDeptPk and @existingManagerPk <> @targetPk and @existingIsDeptMgr = @resolvedIsDeptMgr
            begin
                set @resolvedManagerPk = @existingManagerPk;
            end

            update storyPoint.tbEmployee
            set employeeCode = isnull(nullif(ltrim(rtrim(@safeEmployeeCode)), ''), employeeCode),
                employeeName = isnull(nullif(ltrim(rtrim(@employeeName)), ''), employeeName),
                loginAccount = isnull(nullif(ltrim(rtrim(@safeLoginAccount)), ''), loginAccount),
                passwordHash = case
                    when isnull(ltrim(rtrim(@passwordHash)), '') <> '' then ltrim(rtrim(@passwordHash))
                    else passwordHash
                end,
                emailAddress = case
                    when isnull(ltrim(rtrim(@emailAddress)), '') in ('', 'user@company.com') then emailAddress
                    else ltrim(rtrim(@emailAddress))
                end,
                jobTitle = @effectiveTitle,
                departmentPk = @effectiveDeptPk,
                isDepartmentManager = @resolvedIsDeptMgr,
                roleName = case when @roleName in ('admin', 'participant') then @roleName else roleName end,
                isActive = isnull(@isActive, isActive),
                updatedAt = sysdatetime()
            where pk = @targetPk;
        end
        else
        begin
            if @isDepartmentManager is not null
            begin
                set @resolvedIsDeptMgr = @isDepartmentManager;
            end
            else if isnull(@jobTitle, N'') like N'%部門經理%' or isnull(@jobTitle, N'') like N'%董事長%'
            begin
                set @resolvedIsDeptMgr = 1;
            end
            else
            begin
                set @resolvedIsDeptMgr = 0;
            end

            if isnull(@managerEmployeePk, 0) > 0 and exists (select 1 from storyPoint.tbEmployee where pk = @managerEmployeePk)
            begin
                set @resolvedManagerPk = @managerEmployeePk;
            end

            insert into storyPoint.tbEmployee (
                employeeCode, employeeName, loginAccount, passwordHash,
                emailAddress, jobTitle, departmentPk, isDepartmentManager,
                managerEmployeePk, roleName, isActive,
                createdAt, updatedAt
            )
            values (
                @employeeCode,
                @employeeName,
                @loginAccount,
                isnull(nullif(ltrim(rtrim(@passwordHash)), ''), '8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918'),
                @emailAddress,
                @jobTitle,
                @departmentPk,
                @resolvedIsDeptMgr,
                @resolvedManagerPk,
                case when @roleName in ('admin', 'participant') then @roleName else 'participant' end,
                isnull(@isActive, 1),
                sysdatetime(),
                sysdatetime()
            );
            set @targetPk = scope_identity();
            set @effectiveDeptPk = @departmentPk;
        end

        -- 若未明確指定頂層根節點 (@managerEmployeePk <> -1) 且 @resolvedManagerPk 仍為 null，自動依部門主管建立樹狀遞迴階層
        if isnull(@managerEmployeePk, 0) <> -1 and @resolvedManagerPk is null
        begin
            if @resolvedIsDeptMgr = 0
            begin
                -- 一般同仁自動掛載於同部門之「部門主管 (isDepartmentManager = 1)」之下
                select top 1 @resolvedManagerPk = e.pk
                from storyPoint.tbEmployee as e
                where e.departmentPk = @effectiveDeptPk
                  and e.isDepartmentManager = 1
                  and e.isActive = 1
                  and e.pk <> @targetPk
                order by e.pk asc;
            end
            else
            begin
                -- 部門主管 (除最高董事長/頂層根節點外) 自動掛載於頂層最高主管之下，構成多層樹狀遞迴階層
                declare @isTopChairman bit = case when isnull(@jobTitle, N'') like N'%董事長%' then 1 else 0 end;
                if @isTopChairman = 0
                begin
                    select top 1 @resolvedManagerPk = e.pk
                    from storyPoint.tbEmployee as e
                    where e.isDepartmentManager = 1
                      and e.isActive = 1
                      and e.pk <> @targetPk
                      and (e.managerEmployeePk is null or e.jobTitle like N'%董事長%')
                    order by
                        case when e.jobTitle like N'%董事長%' then 0 else 1 end asc,
                        e.pk asc;
                end
            end
        end

        -- 透過遞迴 CTE 檢核設定 @resolvedManagerPk 是否會造成樹狀循環參照 (Cycle Detection)
        if @resolvedManagerPk is not null
        begin
            declare @createsCycle bit = 0;
            ;with cycleCheckCte as (
                select
                    e.pk as currentPk,
                    e.managerEmployeePk as nextParentPk,
                    1 as depthLevel
                from storyPoint.tbEmployee as e
                where e.pk = @resolvedManagerPk

                union all

                select
                    p.pk as currentPk,
                    p.managerEmployeePk as nextParentPk,
                    c.depthLevel + 1 as depthLevel
                from cycleCheckCte as c
                inner join storyPoint.tbEmployee as p
                    on c.nextParentPk = p.pk
                where c.nextParentPk is not null
                  and c.nextParentPk <> c.currentPk
                  and c.currentPk <> @targetPk
                  and c.depthLevel < 15
            )
            select top 1 @createsCycle = 1
            from cycleCheckCte
            where currentPk = @targetPk or nextParentPk = @targetPk;

            if @createsCycle = 1 or @resolvedManagerPk = @targetPk
            begin
                set @resolvedManagerPk = null;
            end
        end

        update storyPoint.tbEmployee
        set managerEmployeePk = @resolvedManagerPk,
            updatedAt = sysdatetime()
        where pk = @targetPk;

        -- 若該員工被設定為部門主管 (isDepartmentManager = 1)，讓同部門尚未指定上層主管之一般同仁自動掛載於此部門主管下
        if @resolvedIsDeptMgr = 1
        begin
            update storyPoint.tbEmployee
            set managerEmployeePk = @targetPk,
                updatedAt = sysdatetime()
            where departmentPk = @effectiveDeptPk
              and pk <> @targetPk
              and isDepartmentManager = 0
              and (
                  managerEmployeePk is null
                  or not exists (
                      select 1
                      from storyPoint.tbEmployee as curMgr
                      where curMgr.pk = storyPoint.tbEmployee.managerEmployeePk
                        and curMgr.departmentPk = @effectiveDeptPk
                        and curMgr.isActive = 1
                  )
              );
        end
        else
        begin
            -- 若該員工取消部門主管身分 (isDepartmentManager = 0)，且原本掛載於其下之同部門同仁需移轉至同部門其他有效部門主管
            declare @otherDeptMgrPk int = null;
            select top 1 @otherDeptMgrPk = e.pk
            from storyPoint.tbEmployee as e
            where e.departmentPk = @effectiveDeptPk
              and e.isDepartmentManager = 1
              and e.isActive = 1
              and e.pk <> @targetPk
            order by e.pk asc;

            if @otherDeptMgrPk is not null
            begin
                update storyPoint.tbEmployee
                set managerEmployeePk = @otherDeptMgrPk,
                    updatedAt = sysdatetime()
                where departmentPk = @effectiveDeptPk
                  and managerEmployeePk = @targetPk
                  and pk <> @otherDeptMgrPk;
            end
        end

        -- 同步更新部門之 managerName（支援同一個部門有多位部門主管，以「、」串接所有啟用中之部門主管姓名）
        update d
        set d.managerName = isnull(mgrAgg.managerNameCsv, d.managerName),
            d.updatedAt = sysdatetime()
        from storyPoint.tbDepartment as d
        outer apply (
            select
                string_agg(e.employeeName, N'、') within group (order by e.pk asc) as managerNameCsv
            from storyPoint.tbEmployee as e
            where e.departmentPk = d.pk
              and e.isDepartmentManager = 1
              and e.isActive = 1
        ) as mgrAgg
        where d.pk = @effectiveDeptPk
           or mgrAgg.managerNameCsv is not null;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertEmployee',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工資料、部門主管註記與樹狀遞迴階層儲存成功',
            @operatorAccount = @operatorAccount;

        select
            v.employeePk,
            v.employeeCode,
            v.employeeName,
            v.loginAccount,
            v.emailAddress,
            v.jobTitle,
            v.roleName,
            v.roleDisplayName,
            v.departmentPk,
            v.departmentCode,
            v.departmentName,
            v.isDepartmentManager,
            v.isDepartmentManagerText,
            v.managerEmployeePk,
            v.managerEmployeeCode,
            v.managerEmployeeName,
            v.departmentManagerEmployeePk,
            v.departmentManagerName,
            v.departmentManagerEmail,
            v.departmentManagerCount,
            v.hrManagerName,
            v.hrManagerEmail,
            v.hierarchyLevel,
            v.hierarchyPath,
            v.isActive,
            v.assignedItemCount,
            v.totalAllocatedStoryPoint
        from storyPoint.vwEmployeePointSummary as v
        where v.employeePk = @targetPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertEmployee',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工資料與帳密儲存失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 4A. 員工自助修改個人登入帳號與密碼預存程序 (storyPoint.spUpdateEmployeeSelfCredential)
-- ============================================================================
/*
用途說明: 允許任何已登入員工 (包含管理者 admin 與一般參與者 participant) 自助修改自己的登入帳號 (loginAccount) 與登入密碼 (passwordHash)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @employeePk          int          : 當前登入員工本人主鍵 (若未傳入則依 @operatorAccount 定位)
    @newLoginAccount     varchar(128) : 欲更新之新登入帳號 (若傳空值或與原帳號相同則維持原帳號)
    @newPasswordHash     varchar(256) : 欲更新之新密碼雜湊值 (若傳空值則維持原密碼不變)
    @currentPasswordHash varchar(256) : 目前密碼雜湊值 (選填：若有傳入則驗證原密碼是否相符)
    @operatorAccount     varchar(128) : 操作者目前登入帳號
修改歷程:
    2026-10-23 初版建立，支援每個人皆可修改自己的帳/密並防止與他人帳號重複
*/
create or alter procedure storyPoint.spUpdateEmployeeSelfCredential
    @employeePk int = null,
    @newLoginAccount varchar(128) = null,
    @newPasswordHash varchar(256) = null,
    @currentPasswordHash varchar(256) = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@employeePk, 0);
    declare @cleanNewAccount varchar(128) = nullif(ltrim(rtrim(@newLoginAccount)), '');
    declare @cleanNewPwdHash varchar(256) = nullif(ltrim(rtrim(@newPasswordHash)), '');
    declare @cleanCurrentPwdHash varchar(256) = nullif(ltrim(rtrim(@currentPasswordHash)), '');
    declare @cleanOperator varchar(128) = isnull(nullif(ltrim(rtrim(@operatorAccount)), ''), 'system');

    begin try
        if @targetPk <= 0 and @cleanOperator <> 'system'
        begin
            select top 1 @targetPk = pk
            from storyPoint.tbEmployee
            where loginAccount = @cleanOperator;
        end

        if isnull(@targetPk, 0) <= 0 or not exists (select 1 from storyPoint.tbEmployee where pk = @targetPk)
        begin
            ;throw 50011, N'找不到當前登入的員工帳號紀錄，請重新登入後再試。', 1;
        end

        if @cleanCurrentPwdHash is not null
        begin
            if not exists (
                select 1
                from storyPoint.tbEmployee
                where pk = @targetPk
                  and passwordHash = @cleanCurrentPwdHash
            )
            begin
                ;throw 50012, N'輸入的目前登入密碼不正確，請重新確認。', 1;
            end
        end

        if @cleanNewAccount is not null
        begin
            if exists (
                select 1
                from storyPoint.tbEmployee
                where loginAccount = @cleanNewAccount
                  and pk <> @targetPk
            )
            begin
                ;throw 50013, N'此登入帳號已被其他員工使用，請改用其他登入帳號。', 1;
            end
        end

        update storyPoint.tbEmployee
        set loginAccount = isnull(@cleanNewAccount, loginAccount),
            passwordHash = case
                when @cleanNewPwdHash is not null then @cleanNewPwdHash
                else passwordHash
            end,
            updatedAt = sysdatetime()
        where pk = @targetPk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpdateEmployeeSelfCredential',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工自助修改個人登入帳密成功',
            @operatorAccount = @cleanOperator;

        select
            e.pk as employeePk,
            e.employeeCode,
            e.employeeName,
            e.loginAccount,
            e.emailAddress,
            e.jobTitle,
            e.departmentPk,
            d.departmentCode,
            d.departmentName,
            e.roleName,
            e.isActive,
            e.updatedAt
        from storyPoint.tbEmployee as e
        inner join storyPoint.tbDepartment as d
            on e.departmentPk = d.pk
        where e.pk = @targetPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpdateEmployeeSelfCredential',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工自助修改個人登入帳密失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @cleanOperator;
        throw;
    end catch
end
go

-- ============================================================================
-- 4B. 切換員工帳號啟用/停用狀態預存程序 (storyPoint.spSetEmployeeActiveStatus)
-- ============================================================================
/*
用途說明: 啟用或停用指定員工帳號 (tbEmployee.isActive)，停用後該帳號無法登入系統
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @employeePk      int          : 員工主鍵
    @isActive        bit          : 1 為啟用，0 為停用
    @operatorAccount varchar(128) : 操作者帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spSetEmployeeActiveStatus
    @employeePk int,
    @isActive bit,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;

    begin try
        update storyPoint.tbEmployee
        set isActive = isnull(@isActive, 0),
            updatedAt = sysdatetime()
        where pk = @employeePk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spSetEmployeeActiveStatus',
            @durationMillisecond = @durationMs,
            @executionResult = N'切換員工帳號啟用/停用狀態成功',
            @operatorAccount = @operatorAccount;

        select
            v.employeePk,
            v.employeeCode,
            v.employeeName,
            v.loginAccount,
            v.isActive
        from storyPoint.vwEmployeePointSummary as v
        where v.employeePk = @employeePk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spSetEmployeeActiveStatus',
            @durationMillisecond = @durationMs,
            @executionResult = N'切換員工帳號啟用/停用狀態失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 4C. 刪除員工帳號預存程序 (storyPoint.spDeleteEmployee)
-- ============================================================================
/*
用途說明: 安全刪除指定員工帳號 (tbEmployee)，並清理其關聯之工項指派、投票紀錄與外鍵參照
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @employeePk      int          : 欲刪除之員工主鍵
    @operatorAccount varchar(128) : 操作者帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spDeleteEmployee
    @employeePk int,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @deletedName nvarchar(128) = N'';
    declare @deletedAccount varchar(128) = '';
    declare @fallbackAdminPk int = null;

    begin try
        select
            @deletedName = employeeName,
            @deletedAccount = loginAccount
        from storyPoint.tbEmployee
        where pk = @employeePk;

        if isnull(@deletedAccount, '') = ''
        begin
            ;throw 50002, N'找不到欲刪除的員工帳號。', 1;
        end

        select top 1 @fallbackAdminPk = pk
        from storyPoint.tbEmployee
        where pk <> @employeePk
          and roleName = 'admin'
          and isActive = 1
        order by pk asc;

        if @fallbackAdminPk is null
        begin
            select top 1 @fallbackAdminPk = pk
            from storyPoint.tbEmployee
            where pk <> @employeePk
            order by pk asc;
        end

        -- 清理或移轉外鍵關聯紀錄以確保可安全刪除員工帳號 (含樹狀遞迴階層下屬之上層主管移轉)
        declare @deletedEmpParentMgrPk int = null;
        select @deletedEmpParentMgrPk = managerEmployeePk
        from storyPoint.tbEmployee
        where pk = @employeePk;

        if @deletedEmpParentMgrPk = @employeePk
        begin
            set @deletedEmpParentMgrPk = null;
        end

        update storyPoint.tbEmployee
        set managerEmployeePk = @deletedEmpParentMgrPk,
            updatedAt = sysdatetime()
        where managerEmployeePk = @employeePk;

        update storyPoint.tbPointHistory
        set changedByEmployeePk = null
        where changedByEmployeePk = @employeePk;

        update storyPoint.tbWorkItem
        set createdByEmployeePk = null
        where createdByEmployeePk = @employeePk;

        update storyPoint.tbWorkItemAssignment
        set assignedByEmployeePk = null
        where assignedByEmployeePk = @employeePk;

        delete from storyPoint.tbWorkItemAssignment
        where employeePk = @employeePk;

        delete from storyPoint.tbVoteRecord
        where employeePk = @employeePk;

        delete from storyPoint.tbVotingSessionParticipant
        where employeePk = @employeePk;

        if @fallbackAdminPk is not null
        begin
            update storyPoint.tbVotingSession
            set hostEmployeePk = @fallbackAdminPk
            where hostEmployeePk = @employeePk;
        end
        else
        begin
            delete from storyPoint.tbVotingRound
            where votingSessionPk in (select pk from storyPoint.tbVotingSession where hostEmployeePk = @employeePk);

            delete from storyPoint.tbVotingSession
            where hostEmployeePk = @employeePk;
        end

        delete from storyPoint.tbEmployee
        where pk = @employeePk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteEmployee',
            @durationMillisecond = @durationMs,
            @executionResult = N'刪除員工帳號成功',
            @operatorAccount = @operatorAccount;

        select
            @employeePk as deletedEmployeePk,
            @deletedAccount as deletedLoginAccount,
            @deletedName as deletedEmployeeName;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteEmployee',
            @durationMillisecond = @durationMs,
            @executionResult = N'刪除員工帳號失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 5. 新增或修改撲克牌組設定預存程序 (storyPoint.spUpsertCardDeck)
-- ============================================================================
/*
用途說明: 新增或修改 Story Point 撲克牌組設定 (tbCardDeck)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @cardDeckPk      int           : 牌組主鍵
    @deckCode        varchar(64)   : 牌組代碼
    @deckName        nvarchar(128) : 牌組名稱
    @cardValueCsv    varchar(256)  : 逗號分隔之牌面值 (如 '0,1,2,3,5,8,13,21,?,☕')
    @description     nvarchar(512) : 說明
    @isDefault       bit           : 是否為系統預設牌組
    @isActive        bit           : 是否啟用
    @operatorAccount varchar(128)  : 操作者帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spUpsertCardDeck
    @cardDeckPk int = null,
    @deckCode varchar(64),
    @deckName nvarchar(128),
    @cardValueCsv nvarchar(256),
    @description nvarchar(512) = null,
    @isDefault bit = 0,
    @isActive bit = 1,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@cardDeckPk, 0);

    begin try
        if @targetPk <= 0
        begin
            select @targetPk = pk
            from storyPoint.tbCardDeck
            where deckCode = @deckCode;
        end

        if @isDefault = 1
        begin
            update storyPoint.tbCardDeck
            set isDefault = 0;
        end

        if isnull(@targetPk, 0) > 0 and exists (select 1 from storyPoint.tbCardDeck where pk = @targetPk)
        begin
            update storyPoint.tbCardDeck
            set deckCode = @deckCode,
                deckName = @deckName,
                cardValueCsv = @cardValueCsv,
                description = @description,
                isDefault = isnull(@isDefault, 0),
                isActive = isnull(@isActive, 1),
                updatedAt = sysdatetime()
            where pk = @targetPk;
        end
        else
        begin
            insert into storyPoint.tbCardDeck (
                deckCode, deckName, cardValueCsv, description,
                isDefault, isActive, createdAt, updatedAt
            )
            values (
                @deckCode,
                @deckName,
                @cardValueCsv,
                @description,
                isnull(@isDefault, 0),
                isnull(@isActive, 1),
                sysdatetime(),
                sysdatetime()
            );
            set @targetPk = scope_identity();
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertCardDeck',
            @durationMillisecond = @durationMs,
            @executionResult = N'撲克牌組設定儲存成功',
            @operatorAccount = @operatorAccount;

        select
            cd.pk,
            cd.deckCode,
            cd.deckName,
            cd.cardValueCsv,
            cd.description,
            cd.isDefault,
            cd.isActive
        from storyPoint.tbCardDeck as cd
        where cd.pk = @targetPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertCardDeck',
            @durationMillisecond = @durationMs,
            @executionResult = N'撲克牌組設定儲存失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 6. 新增或修改工項代表類型預存程序 (storyPoint.spUpsertWorkItemType)
-- ============================================================================
/*
用途說明: 新增或更新「工項代表類型 (tbWorkItemType)」，支援同步更新未手動微調之未完成工項點數
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemTypePk          int            : 工項代表類型主鍵
    @typeCode                varchar(64)    : 代表類型代碼
    @typeName                nvarchar(128)  : 代表類型名稱
    @categoryName            nvarchar(128)  : 分類名稱
    @description             nvarchar(1024) : 複雜度與驗收標準說明
    @standardStoryPoint      decimal(8,2)   : 標準 Story Point (若尚未定案可為 null)
    @defaultCardDeckPk       int            : 預設使用之撲克牌組主鍵
    @syncExistingOpenWorkItem bit           : 當標準點數變更時，是否一併同步更新未完成且未手動調整過的既有工項
    @changedByEmployeePk     int            : 異動管理者主鍵
    @isActive                bit            : 是否啟用
    @operatorAccount         varchar(128)   : 操作者帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spUpsertWorkItemType
    @workItemTypePk int = null,
    @typeCode varchar(64),
    @typeName nvarchar(128),
    @categoryName nvarchar(128),
    @defaultWorkItemKind varchar(64) = 'project',
    @description nvarchar(1024) = null,
    @standardStoryPoint decimal(8,2) = null,
    @defaultCardDeckPk int = null,
    @syncExistingOpenWorkItem bit = 0,
    @changedByEmployeePk int = null,
    @isActive bit = 1,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@workItemTypePk, 0);
    declare @oldPoint decimal(8,2) = null;
    declare @newStatus varchar(32) = case when @standardStoryPoint is not null then 'finalized' else 'pending' end;
    declare @cleanInputKind nvarchar(128) = ltrim(rtrim(isnull(cast(@defaultWorkItemKind as nvarchar(128)), N'')));
    declare @cleanKind varchar(64) = null;

    begin try
        if @cleanInputKind <> N''
        begin
            select top 1 @cleanKind = kindCode
            from storyPoint.tbWorkItemKind
            where isActive = 1
              and (kindCode = @cleanInputKind or kindName = @cleanInputKind);
        end

        if @cleanKind is null
        begin
            set @cleanKind = case
                when @cleanInputKind in (N'maintenance', N'維運') then 'maintenance'
                when @cleanInputKind in (N'consultingHour', N'顧問時數') then 'consultingHour'
                when @cleanInputKind in (N'project', N'專案') then 'project'
                when @cleanInputKind <> N'' then cast(@cleanInputKind as varchar(64))
                when @categoryName like N'%維運%' then 'maintenance'
                when @categoryName like N'%顧問%' or @categoryName like N'%時數%' then 'consultingHour'
                else 'project'
            end;
        end

        if @targetPk <= 0
        begin
            select @targetPk = pk
            from storyPoint.tbWorkItemType
            where typeCode = @typeCode;
        end

        if isnull(@defaultCardDeckPk, 0) <= 0 or not exists (select 1 from storyPoint.tbCardDeck where pk = @defaultCardDeckPk)
        begin
            select top 1 @defaultCardDeckPk = pk
            from storyPoint.tbCardDeck
            where isActive = 1
            order by isDefault desc, pk asc;
        end

        if @changedByEmployeePk is not null and not exists (select 1 from storyPoint.tbEmployee where pk = @changedByEmployeePk)
        begin
            set @changedByEmployeePk = null;
            select top 1 @changedByEmployeePk = pk
            from storyPoint.tbEmployee
            where loginAccount = @operatorAccount;
        end

        if isnull(@targetPk, 0) > 0 and exists (select 1 from storyPoint.tbWorkItemType where pk = @targetPk)
        begin
            select @oldPoint = standardStoryPoint
            from storyPoint.tbWorkItemType
            where pk = @targetPk;

            update storyPoint.tbWorkItemType
            set typeCode = @typeCode,
                typeName = @typeName,
                categoryName = @categoryName,
                defaultWorkItemKind = @cleanKind,
                description = @description,
                standardStoryPoint = @standardStoryPoint,
                defaultCardDeckPk = @defaultCardDeckPk,
                estimationStatus = case
                    when @standardStoryPoint is not null then 'finalized'
                    when estimationStatus = 'voting' then 'voting'
                    else 'pending'
                end,
                isActive = isnull(@isActive, 1),
                updatedAt = sysdatetime()
            where pk = @targetPk;
        end
        else
        begin
            insert into storyPoint.tbWorkItemType (
                typeCode, typeName, categoryName, defaultWorkItemKind, description,
                standardStoryPoint, defaultCardDeckPk, estimationStatus,
                isActive, createdAt, updatedAt
            )
            values (
                @typeCode,
                @typeName,
                @categoryName,
                @cleanKind,
                @description,
                @standardStoryPoint,
                @defaultCardDeckPk,
                @newStatus,
                isnull(@isActive, 1),
                sysdatetime(),
                sysdatetime()
            );
            set @targetPk = scope_identity();
        end

        -- 若點數有變更且有設定標準點數，寫入點數歷程表
        if @standardStoryPoint is not null and isnull(@oldPoint, -1) <> @standardStoryPoint
        begin
            insert into storyPoint.tbPointHistory (
                targetKind, workItemTypePk, workItemPk, votingSessionPk,
                oldStoryPoint, newStoryPoint, changeSource, changeReason,
                changedByEmployeePk, changedAt
            )
            values (
                'workItemType',
                @targetPk,
                null,
                null,
                @oldPoint,
                @standardStoryPoint,
                'adminTypeSetting',
                N'管理者設定或更新工項代表類型標準 Story Point',
                @changedByEmployeePk,
                sysdatetime()
            );

            -- 若管理者勾選同步更新未完成且未手動調整過之既有工項
            if @syncExistingOpenWorkItem = 1
            begin
                insert into storyPoint.tbPointHistory (
                    targetKind, workItemTypePk, workItemPk, votingSessionPk,
                    oldStoryPoint, newStoryPoint, changeSource, changeReason,
                    changedByEmployeePk, changedAt
                )
                select
                    'workItem',
                    @targetPk,
                    wi.pk,
                    null,
                    wi.storyPoint,
                    @standardStoryPoint,
                    'typeSync',
                    N'隨工項代表類型標準 Story Point 變更同步更新',
                    @changedByEmployeePk,
                    sysdatetime()
                from storyPoint.tbWorkItem as wi
                where wi.workItemTypePk = @targetPk
                  and wi.isManualAdjusted = 0
                  and wi.executionStatus <> 'done'
                  and wi.isActive = 1
                  and wi.storyPoint <> @standardStoryPoint;

                update storyPoint.tbWorkItem
                set defaultTypePoint = @standardStoryPoint,
                    storyPoint = @standardStoryPoint,
                    updatedAt = sysdatetime()
                where workItemTypePk = @targetPk
                  and isManualAdjusted = 0
                  and executionStatus <> 'done'
                  and isActive = 1;

                -- 同步重算被更新工項之員工分配點數
                update wia
                set wia.allocatedStoryPoint = cast(round(wi.storyPoint * (wia.allocationPercent / 100.00), 2) as decimal(8,2)),
                    wia.updatedAt = sysdatetime()
                from storyPoint.tbWorkItemAssignment as wia
                inner join storyPoint.tbWorkItem as wi
                    on wia.workItemPk = wi.pk
                where wi.workItemTypePk = @targetPk
                  and wi.isManualAdjusted = 0
                  and wi.executionStatus <> 'done'
                  and wi.isActive = 1;
            end
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertWorkItemType',
            @durationMillisecond = @durationMs,
            @executionResult = N'工項代表類型儲存成功',
            @operatorAccount = @operatorAccount;

        select
            wit.pk,
            wit.typeCode,
            wit.typeName,
            wit.categoryName,
            wit.defaultWorkItemKind,
            isnull(
                wik.kindName,
                case
                    when wit.defaultWorkItemKind = 'maintenance' then N'維運'
                    when wit.defaultWorkItemKind = 'consultingHour' then N'顧問時數'
                    when wit.defaultWorkItemKind = 'project' then N'專案'
                    else cast(wit.defaultWorkItemKind as nvarchar(128))
                end
            ) as defaultWorkItemKindText,
            wit.description,
            wit.standardStoryPoint,
            wit.defaultCardDeckPk,
            wit.estimationStatus,
            wit.lastVotingSessionPk,
            wit.isActive
        from storyPoint.tbWorkItemType as wit
        left join storyPoint.tbWorkItemKind as wik
            on wit.defaultWorkItemKind = wik.kindCode
        where wit.pk = @targetPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertWorkItemType',
            @durationMillisecond = @durationMs,
            @executionResult = N'工項代表類型儲存失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 7. 建立或修改實際工項預存程序 (storyPoint.spUpsertWorkItem)
-- ============================================================================
/*
用途說明: 建立或更新實際工項 (tbWorkItem)，支援設定工項類型 (專案/維運/顧問時數) 與起訖時間 (startDate ~ endDate)。
          建立工項時，若選擇已賦予 Story Point 的工項代表類型 (@workItemTypePk)，
          系統會自動帶入該類型的標準 Story Point；若管理者傳入不同的 @storyPoint 或標記手動調整，
          則採用手動調整值並記錄點數變更歷程，同時自動重算指派給員工的 Story Point。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemPk          int           : 工項主鍵
    @itemCode            varchar(64)   : 工項代碼
    @itemTitle           nvarchar(256) : 工項標題
    @itemDescription     nvarchar(max) : 工項詳細說明與驗收條件
    @workItemTypePk      int           : 選擇的工項代表類型主鍵
    @workItemKind        varchar(32)   : 工項性質類型 ('project'=專案, 'maintenance'=維運, 'consultingHour'=顧問時數)
    @sprintName          nvarchar(128) : 所屬衝刺或專案階段名稱
    @priorityLevel       varchar(32)   : 優先級 ('high', 'medium', 'low')
    @storyPoint          decimal(8,2)  : 指定點數 (若傳 null 則完全由工項代表類型自動帶入)
    @forceManualAdjust   bit           : 是否強制視為手動調整
    @adjustmentReason    nvarchar(512) : 手動調整點數說明
    @executionStatus     varchar(32)   : 執行狀態 ('todo', 'inProgress', 'done')
    @startDate           datetime2(0)  : 工項起時 (開始日期時間)
    @endDate             datetime2(0)  : 工項迄時 (結束日期時間，達到迄時將自動轉態並扣除 Story Point)
    @dueDate             date          : 預計完成日
    @createdByEmployeePk int           : 建立/修改管理者主鍵
    @isActive            bit           : 是否啟用
    @operatorAccount     varchar(128)  : 操作者帳號
修改歷程:
    2026-10-23 初版建立
    2026-10-23 新增 @workItemKind (專案/維運/顧問時數) 與 @startDate/@endDate 起訖時間設定
*/
create or alter procedure storyPoint.spUpsertWorkItem
    @workItemPk int = null,
    @itemCode varchar(64),
    @itemTitle nvarchar(256),
    @itemDescription nvarchar(max) = null,
    @workItemTypePk int = null,
    @workItemKind nvarchar(128) = null,
    @sprintName nvarchar(128) = null,
    @priorityLevel varchar(32) = 'medium',
    @storyPoint decimal(8,2) = null,
    @forceManualAdjust bit = 0,
    @adjustmentReason nvarchar(512) = null,
    @executionStatus varchar(32) = 'todo',
    @startDate datetime2(0) = null,
    @endDate datetime2(0) = null,
    @dueDate date = null,
    @createdByEmployeePk int = null,
    @isActive bit = 1,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@workItemPk, 0);
    declare @typeStandardPoint decimal(8,2) = null;
    declare @typeDefaultKind varchar(64) = 'project';
    declare @resolvedKind varchar(64) = null;
    declare @cleanInputKind nvarchar(128) = ltrim(rtrim(isnull(@workItemKind, N'')));
    declare @resolvedStoryPoint decimal(8,2) = 0.00;
    declare @isManual bit = 0;
    declare @oldPoint decimal(8,2) = null;
    declare @changeSource varchar(64) = 'autoFromType';
    declare @nowTw datetime2(0) = cast(dateadd(hour, 8, sysutcdatetime()) as datetime2(0));
    declare @resolvedStartDate datetime2(0) = @startDate;
    declare @resolvedEndDate datetime2(0) = @endDate;
    declare @resolvedDueDate date = @dueDate;

    begin try
        if @targetPk <= 0
        begin
            select @targetPk = pk
            from storyPoint.tbWorkItem
            where itemCode = @itemCode;
        end

        if isnull(@workItemTypePk, 0) > 0 and exists (select 1 from storyPoint.tbWorkItemType where pk = @workItemTypePk)
        begin
            select
                @typeStandardPoint = standardStoryPoint,
                @typeDefaultKind = isnull(defaultWorkItemKind, 'project')
            from storyPoint.tbWorkItemType
            where pk = @workItemTypePk;
        end
        else
        begin
            set @workItemTypePk = null;
        end

        if @cleanInputKind <> N''
        begin
            select top 1 @resolvedKind = kindCode
            from storyPoint.tbWorkItemKind
            where isActive = 1
              and (kindCode = @cleanInputKind or kindName = @cleanInputKind);
        end

        if @resolvedKind is null
        begin
            set @resolvedKind = case
                when @cleanInputKind in (N'project', N'專案') then 'project'
                when @cleanInputKind in (N'maintenance', N'維運') then 'maintenance'
                when @cleanInputKind in (N'consultingHour', N'顧問時數') then 'consultingHour'
                when @cleanInputKind <> N'' then cast(@cleanInputKind as varchar(64))
                when @workItemTypePk is not null then @typeDefaultKind
                else 'project'
            end;
        end

        if @resolvedEndDate is null and @resolvedDueDate is not null
        begin
            set @resolvedEndDate = dateadd(second, 86399, cast(@resolvedDueDate as datetime2(0)));
        end

        if @resolvedDueDate is null and @resolvedEndDate is not null
        begin
            set @resolvedDueDate = cast(@resolvedEndDate as date);
        end

        if @createdByEmployeePk is not null and not exists (select 1 from storyPoint.tbEmployee where pk = @createdByEmployeePk)
        begin
            set @createdByEmployeePk = null;
            select top 1 @createdByEmployeePk = pk
            from storyPoint.tbEmployee
            where loginAccount = @operatorAccount;
        end

        -- 決定最終 Story Point：若未傳入 @storyPoint 或與代表類型標準點數相同且未強制手動調整，則自動帶入代表類型點數
        if @storyPoint is null
        begin
            set @resolvedStoryPoint = isnull(@typeStandardPoint, 0.00);
            set @isManual = 0;
            set @changeSource = case when @workItemTypePk is not null then 'autoFromType' else 'initialCreate' end;
        end
        else if @workItemTypePk is not null and @typeStandardPoint is not null and @storyPoint = @typeStandardPoint and @forceManualAdjust = 0
        begin
            set @resolvedStoryPoint = @typeStandardPoint;
            set @isManual = 0;
            set @changeSource = 'autoFromType';
        end
        else
        begin
            set @resolvedStoryPoint = @storyPoint;
            set @isManual = case when @workItemTypePk is not null and isnull(@typeStandardPoint, -1) <> @storyPoint then 1 else @forceManualAdjust end;
            set @changeSource = case when @isManual = 1 then 'manualAdjust' else 'directSet' end;
        end

        if isnull(@targetPk, 0) > 0 and exists (select 1 from storyPoint.tbWorkItem where pk = @targetPk)
        begin
            select
                @oldPoint = storyPoint,
                @resolvedStartDate = isnull(@resolvedStartDate, isnull(startDate, @nowTw))
            from storyPoint.tbWorkItem
            where pk = @targetPk;

            -- 若管理者重新設定點數 (> 0) 或將狀態設回 todo/inProgress，或是將迄時延後至未來，則重置到期扣點旗標以便達到迄時可自動執行轉態扣點與寄信
            declare @shouldResetExpiry bit = case
                when @resolvedStoryPoint > 0 and isnull(@executionStatus, 'todo') in ('todo', 'inProgress') then 1
                when @resolvedEndDate is not null and @resolvedEndDate > @nowTw then 1
                else 0
            end;

            update storyPoint.tbWorkItem
            set itemCode = @itemCode,
                itemTitle = @itemTitle,
                itemDescription = @itemDescription,
                workItemTypePk = @workItemTypePk,
                workItemKind = @resolvedKind,
                sprintName = @sprintName,
                priorityLevel = isnull(@priorityLevel, 'medium'),
                defaultTypePoint = @typeStandardPoint,
                storyPoint = @resolvedStoryPoint,
                originalStoryPoint = case when @resolvedStoryPoint > 0 then @resolvedStoryPoint else originalStoryPoint end,
                deductedStoryPoint = case when @shouldResetExpiry = 1 then 0.00 else deductedStoryPoint end,
                isManualAdjusted = @isManual,
                adjustmentReason = @adjustmentReason,
                executionStatus = isnull(@executionStatus, 'todo'),
                startDate = @resolvedStartDate,
                endDate = @resolvedEndDate,
                dueDate = @resolvedDueDate,
                isExpiredProcessed = case when @shouldResetExpiry = 1 then 0 else isExpiredProcessed end,
                expiredProcessedAt = case when @shouldResetExpiry = 1 then null else expiredProcessedAt end,
                expiryNotificationSent = case when @shouldResetExpiry = 1 then 0 else expiryNotificationSent end,
                expiryNotificationSentAt = case when @shouldResetExpiry = 1 then null else expiryNotificationSentAt end,
                isActive = isnull(@isActive, 1),
                updatedAt = sysdatetime()
            where pk = @targetPk;
        end
        else
        begin
            set @resolvedStartDate = isnull(@resolvedStartDate, @nowTw);

            insert into storyPoint.tbWorkItem (
                itemCode, itemTitle, itemDescription, workItemTypePk,
                workItemKind, sprintName, priorityLevel, defaultTypePoint, storyPoint,
                originalStoryPoint, deductedStoryPoint, isManualAdjusted, adjustmentReason,
                estimationStatus, executionStatus, startDate, endDate, dueDate,
                isExpiredProcessed, expiryNotificationSent,
                createdByEmployeePk, isActive, createdAt, updatedAt
            )
            values (
                @itemCode,
                @itemTitle,
                @itemDescription,
                @workItemTypePk,
                @resolvedKind,
                @sprintName,
                isnull(@priorityLevel, 'medium'),
                @typeStandardPoint,
                @resolvedStoryPoint,
                @resolvedStoryPoint,
                0.00,
                @isManual,
                @adjustmentReason,
                'finalized',
                isnull(@executionStatus, 'todo'),
                @resolvedStartDate,
                @resolvedEndDate,
                @resolvedDueDate,
                0,
                0,
                @createdByEmployeePk,
                isnull(@isActive, 1),
                sysdatetime(),
                sysdatetime()
            );
            set @targetPk = scope_identity();
        end

        -- 若點數為新建或發生異動，記錄至點數歷程表
        if @oldPoint is null or @oldPoint <> @resolvedStoryPoint
        begin
            insert into storyPoint.tbPointHistory (
                targetKind, workItemTypePk, workItemPk, votingSessionPk,
                oldStoryPoint, newStoryPoint, changeSource, changeReason,
                changedByEmployeePk, changedAt
            )
            values (
                'workItem',
                @workItemTypePk,
                @targetPk,
                null,
                @oldPoint,
                @resolvedStoryPoint,
                @changeSource,
                isnull(@adjustmentReason, case when @isManual = 1 then N'管理者手動調整工項 Story Point' else N'選擇工項代表類型自動帶入 Story Point' end),
                @createdByEmployeePk,
                sysdatetime()
            );
        end

        -- 自動依分攤比例重新計算該工項下所有被賦予員工的 Story Point
        update storyPoint.tbWorkItemAssignment
        set allocatedStoryPoint = cast(round(@resolvedStoryPoint * (allocationPercent / 100.00), 2) as decimal(8,2)),
            originalAllocatedStoryPoint = case
                when @resolvedStoryPoint > 0 then cast(round(@resolvedStoryPoint * (allocationPercent / 100.00), 2) as decimal(8,2))
                else originalAllocatedStoryPoint
            end,
            deductedAllocatedStoryPoint = case
                when @resolvedStoryPoint > 0 then 0.00
                else deductedAllocatedStoryPoint
            end,
            updatedAt = sysdatetime()
        where workItemPk = @targetPk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertWorkItem',
            @durationMillisecond = @durationMs,
            @executionResult = N'工項資料儲存並同步重算員工點數成功',
            @operatorAccount = @operatorAccount;

        select
            v.pk,
            v.itemCode,
            v.itemTitle,
            v.workItemTypePk,
            v.typeCode,
            v.typeName,
            v.workItemKind,
            v.workItemKindText,
            v.defaultTypePoint,
            v.storyPoint,
            v.originalStoryPoint,
            v.deductedStoryPoint,
            v.isManualAdjusted,
            v.pointSourceText,
            v.executionStatus,
            v.startDate,
            v.endDate,
            v.dueDate,
            v.isExpiredProcessed,
            v.expiryNotificationSent,
            v.assigneeSummary
        from storyPoint.vwWorkItemDetail as v
        where v.pk = @targetPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertWorkItem',
            @durationMillisecond = @durationMs,
            @executionResult = N'工項資料儲存失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 8. 管理者修改員工賦予的工項預存程序 (storyPoint.spAssignWorkItemToEmployee)
-- ============================================================================
/*
用途說明: 管理者將工項賦予給員工、修改員工的分攤比例/點數，或移除員工身上的工項指派
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @actionType           varchar(32)   : 'upsert' (新增或修改指派)、'remove' (移除該員工指派)、'replaceSingle' (改由該單一員工獨力負責 100%)
    @workItemPk           int           : 工項主鍵
    @employeePk           int           : 被賦予工項之員工主鍵
    @assignmentRole       nvarchar(64)  : 負責角色說明 (如 '主要負責', '協同開發', '測試驗收')
    @allocationPercent    decimal(5,2)  : 點數分攤百分比 (預設 100.00)
    @assignmentNote       nvarchar(512) : 指派備註
    @assignedByEmployeePk int           : 執行指派之管理者主鍵
    @operatorAccount      varchar(128)  : 操作者帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spAssignWorkItemToEmployee
    @actionType varchar(32) = 'upsert',
    @workItemPk int,
    @employeePk int,
    @assignmentRole nvarchar(64) = N'主要負責',
    @allocationPercent decimal(5,2) = 100.00,
    @assignmentNote nvarchar(512) = null,
    @assignedByEmployeePk int = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @workItemPoint decimal(8,2) = 0.00;
    declare @allocatedPoint decimal(8,2) = 0.00;

    begin try
        if @assignedByEmployeePk is not null and not exists (select 1 from storyPoint.tbEmployee where pk = @assignedByEmployeePk)
        begin
            select top 1 @assignedByEmployeePk = pk
            from storyPoint.tbEmployee
            where loginAccount = @operatorAccount;
        end

        select @workItemPoint = storyPoint
        from storyPoint.tbWorkItem
        where pk = @workItemPk;

        if @actionType = 'remove'
        begin
            delete from storyPoint.tbWorkItemAssignment
            where workItemPk = @workItemPk
              and employeePk = @employeePk;
        end
        else
        begin
            if @actionType = 'replaceSingle'
            begin
                delete from storyPoint.tbWorkItemAssignment
                where workItemPk = @workItemPk
                  and employeePk <> @employeePk;
                set @allocationPercent = 100.00;
            end

            set @allocatedPoint = cast(round(isnull(@workItemPoint, 0.00) * (isnull(@allocationPercent, 100.00) / 100.00), 2) as decimal(8,2));

            if exists (select 1 from storyPoint.tbWorkItemAssignment where workItemPk = @workItemPk and employeePk = @employeePk)
            begin
                update storyPoint.tbWorkItemAssignment
                set assignmentRole = isnull(@assignmentRole, N'主要負責'),
                    allocationPercent = isnull(@allocationPercent, 100.00),
                    allocatedStoryPoint = @allocatedPoint,
                    assignmentNote = @assignmentNote,
                    assignedByEmployeePk = @assignedByEmployeePk,
                    updatedAt = sysdatetime()
                where workItemPk = @workItemPk
                  and employeePk = @employeePk;
            end
            else
            begin
                insert into storyPoint.tbWorkItemAssignment (
                    workItemPk, employeePk, assignmentRole, allocationPercent,
                    allocatedStoryPoint, assignmentNote, assignedByEmployeePk,
                    assignedAt, updatedAt
                )
                values (
                    @workItemPk,
                    @employeePk,
                    isnull(@assignmentRole, N'主要負責'),
                    isnull(@allocationPercent, 100.00),
                    @allocatedPoint,
                    @assignmentNote,
                    @assignedByEmployeePk,
                    sysdatetime(),
                    sysdatetime()
                );
            end
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spAssignWorkItemToEmployee',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工工項賦予設定變更成功',
            @operatorAccount = @operatorAccount;

        select
            wia.pk as assignmentPk,
            wia.workItemPk,
            wi.itemCode,
            wi.itemTitle,
            wi.storyPoint as workItemTotalPoint,
            wia.employeePk,
            e.employeeCode,
            e.employeeName,
            d.departmentName,
            wia.assignmentRole,
            wia.allocationPercent,
            wia.allocatedStoryPoint,
            wia.assignmentNote,
            wia.updatedAt
        from storyPoint.tbWorkItemAssignment as wia
        inner join storyPoint.tbWorkItem as wi
            on wia.workItemPk = wi.pk
        inner join storyPoint.tbEmployee as e
            on wia.employeePk = e.pk
        inner join storyPoint.tbDepartment as d
            on e.departmentPk = d.pk
        where wia.workItemPk = @workItemPk
        order by wia.pk asc;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spAssignWorkItemToEmployee',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工工項賦予設定變更失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 9. 建立或更新多人 Story Point 投票議程與盲投回合設定預存程序 (storyPoint.spCreateVotingSession)
-- ============================================================================
/*
用途說明: 建立新的多人 Story Point 投票議程 (tbVotingSession) 及第 1 回合 (tbVotingRound)，或更新既有議程的盲投回合設定。
          支援針對「工項代表類型 (workItemType)」或「個別實際工項 (workItem)」進行投票，
          並完整設定誰可以參與、最高/最低 Story Point、撲克牌組、共識分歧門檻、建議定案演算法、
          全員出牌自動翻牌、回合倒數計時限制、特殊功能牌啟用與出牌理由必填規範。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @votingSessionPk        int            : 投票議程主鍵 (若傳入 > 0 則更新既有議程回合設定；若 <= 0 或 null 則建立新議程)
    @sessionCode            varchar(64)    : 投票議程代碼
    @sessionTitle           nvarchar(256)  : 投票議程標題
    @targetKind             varchar(32)    : 'workItemType' (代表類型) 或 'workItem' (個別工項)
    @workItemTypePk         int            : 工項代表類型主鍵
    @workItemPk             int            : 個別工項主鍵
    @cardDeckPk             int            : 使用之撲克牌組主鍵
    @hostEmployeePk         int            : 主持管理者主鍵
    @participantPkCsv       varchar(max)   : 參與投票員工主鍵逗號清單 (空字串代表全部在職員工)
    @minStoryPoint          decimal(8,2)   : 本回合允許最低 Story Point 下限
    @maxStoryPoint          decimal(8,2)   : 本回合允許最高 Story Point 上限
    @maxAllowedSpread       decimal(8,2)   : 共識分歧警示門檻 (最高與最低點數最大容許差距)
    @consensusRule          varchar(32)    : 建議定案點數計算規則 ('nearestFibonacci','average','median','mode','trimmedMean','maxConservative')
    @autoRevealWhenAllVoted bit            : 全員完成出牌後是否自動翻牌揭曉
    @roundTimeLimitSecond   int            : 每回合思考時間限制秒數 (0 為不限時)
    @allowQuestionCard      bit            : 是否允許投出 '?' 疑問牌
    @allowCoffeeCard        bit            : 是否允許投出 '☕' 休息/拆解牌
    @voteCommentPolicy      varchar(32)    : 出牌理由規範 ('optional','requiredAll','requiredExtreme')
    @restrictToParticipant  bit            : 是否僅限勾選之授權參與人員出牌
    @roundGoalNote          nvarchar(1024) : 本回合估算範圍與驗收重點提示說明
    @resetCurrentRoundVotes bit            : 更新既有議程設定時是否一併清空並重設當前盲投回合已投出之選票
    @operatorAccount        varchar(128)   : 操作者帳號
修改歷程:
    2026-10-23 初版建立
    2026-10-23 擴充支援盲投回合完整設定選項與既有議程設定更新
    2026-10-23 強化外鍵完整性防呆與零工項代表類型/零實際工項時自動建立估算標的機制
*/
create or alter procedure storyPoint.spCreateVotingSession
    @votingSessionPk int = null,
    @sessionCode varchar(64) = null,
    @sessionTitle nvarchar(256),
    @targetKind varchar(32) = 'workItemType',
    @workItemTypePk int = null,
    @workItemPk int = null,
    @cardDeckPk int = null,
    @hostEmployeePk int,
    @participantPkCsv varchar(max) = null,
    @minStoryPoint decimal(8,2) = 0.00,
    @maxStoryPoint decimal(8,2) = 21.00,
    @maxAllowedSpread decimal(8,2) = 5.00,
    @consensusRule varchar(32) = 'nearestFibonacci',
    @autoRevealWhenAllVoted bit = 1,
    @roundTimeLimitSecond int = 120,
    @allowQuestionCard bit = 1,
    @allowCoffeeCard bit = 1,
    @voteCommentPolicy varchar(32) = 'optional',
    @restrictToParticipant bit = 1,
    @roundGoalNote nvarchar(1024) = null,
    @resetCurrentRoundVotes bit = 0,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @sessionPk int = isnull(@votingSessionPk, 0);
    declare @cleanMin decimal(8,2) = isnull(@minStoryPoint, 0.00);
    declare @cleanMax decimal(8,2) = isnull(@maxStoryPoint, 21.00);

    begin try
        if @cleanMin < 0 set @cleanMin = 0.00;
        if @cleanMax < @cleanMin
        begin
            declare @tmpPt decimal(8,2) = @cleanMin;
            set @cleanMin = @cleanMax;
            set @cleanMax = @tmpPt;
        end

        if isnull(@cardDeckPk, 0) <= 0 or not exists (select 1 from storyPoint.tbCardDeck where pk = @cardDeckPk)
        begin
            select top 1 @cardDeckPk = pk
            from storyPoint.tbCardDeck
            where isActive = 1
            order by isDefault desc, pk asc;

            if isnull(@cardDeckPk, 0) <= 0
            begin
                insert into storyPoint.tbCardDeck (
                    deckCode, deckName, cardValueCsv, description, isDefault, isActive, createdAt, updatedAt
                )
                values (
                    'FIBO_STD', N'標準費氏數列撲克牌', '0,0.5,1,2,3,5,8,13,21,34,55,89,?,☕',
                    N'Scrum Planning Poker 標準費氏數列牌組', 1, 1, sysdatetime(), sysdatetime()
                );
                set @cardDeckPk = scope_identity();
            end
        end

        if isnull(@hostEmployeePk, 0) <= 0 or not exists (select 1 from storyPoint.tbEmployee where pk = @hostEmployeePk)
        begin
            set @hostEmployeePk = null;
            select top 1 @hostEmployeePk = pk
            from storyPoint.tbEmployee
            where loginAccount = @operatorAccount
            order by isActive desc, pk asc;

            if @hostEmployeePk is null
            begin
                select top 1 @hostEmployeePk = pk
                from storyPoint.tbEmployee
                order by isActive desc, case when roleName = 'admin' then 0 else 1 end asc, pk asc;
            end
        end

        if @targetKind = 'workItemType'
        begin
            set @workItemPk = null;

            if isnull(@workItemTypePk, 0) <= 0 or not exists (select 1 from storyPoint.tbWorkItemType where pk = @workItemTypePk)
            begin
                set @workItemTypePk = null;
                declare @autoTypeName nvarchar(128) = ltrim(rtrim(replace(replace(isnull(@sessionTitle, N''), N'【代表類型估算】', N''), N'標準點數訂定', N'')));
                if @autoTypeName = N''
                begin
                    set @autoTypeName = N'未命名工項代表類型';
                end

                select top 1 @workItemTypePk = pk
                from storyPoint.tbWorkItemType
                where typeName = @autoTypeName
                order by isActive desc, pk desc;

                if @workItemTypePk is null
                begin
                    declare @autoTypeCode varchar(64) = 'TYPE_' + format(sysdatetime(), 'yyyyMMddHHmmss');
                    if exists (select 1 from storyPoint.tbWorkItemType where typeCode = @autoTypeCode)
                    begin
                        set @autoTypeCode = @autoTypeCode + '_' + right(cast(abs(checksum(newid())) as varchar(16)), 3);
                    end

                    insert into storyPoint.tbWorkItemType (
                        typeCode, typeName, categoryName, description,
                        standardStoryPoint, defaultCardDeckPk, estimationStatus, isActive, createdAt, updatedAt
                    )
                    values (
                        @autoTypeCode,
                        left(@autoTypeName, 128),
                        N'一般代表類型',
                        isnull(nullif(ltrim(rtrim(@roundGoalNote)), N''), N'由盲投回合設定自動建立之工項代表類型'),
                        null,
                        @cardDeckPk,
                        'voting',
                        1,
                        sysdatetime(),
                        sysdatetime()
                    );
                    set @workItemTypePk = scope_identity();
                end
            end

            update storyPoint.tbWorkItemType
            set estimationStatus = 'voting',
                updatedAt = sysdatetime()
            where pk = @workItemTypePk;
        end
        else
        begin
            set @targetKind = 'workItem';
            set @workItemTypePk = null;

            if isnull(@workItemPk, 0) <= 0 or not exists (select 1 from storyPoint.tbWorkItem where pk = @workItemPk)
            begin
                set @workItemPk = null;
                declare @autoItemTitle nvarchar(256) = ltrim(rtrim(replace(isnull(@sessionTitle, N''), N'【特定工項估算】', N'')));
                if @autoItemTitle = N''
                begin
                    set @autoItemTitle = N'未命名實際工項';
                end

                select top 1 @workItemPk = pk
                from storyPoint.tbWorkItem
                where itemTitle = @autoItemTitle
                order by isActive desc, pk desc;

                if @workItemPk is null
                begin
                    declare @fallbackTypePk int = null;
                    select top 1 @fallbackTypePk = pk
                    from storyPoint.tbWorkItemType
                    where isActive = 1
                    order by pk asc;

                    if @fallbackTypePk is null
                    begin
                        declare @fallbackTypeCode varchar(64) = 'TYPE_DEF_' + format(sysdatetime(), 'yyyyMMddHHmmss');
                        insert into storyPoint.tbWorkItemType (
                            typeCode, typeName, categoryName, description,
                            standardStoryPoint, defaultCardDeckPk, estimationStatus, isActive, createdAt, updatedAt
                        )
                        values (
                            @fallbackTypeCode,
                            N'通用預設工項代表類型',
                            N'一般代表類型',
                            N'系統自動建立之預設工項代表類型',
                            null,
                            @cardDeckPk,
                            'pending',
                            1,
                            sysdatetime(),
                            sysdatetime()
                        );
                        set @fallbackTypePk = scope_identity();
                    end

                    declare @autoItemCode varchar(64) = 'WI-' + format(sysdatetime(), 'yyyyMMdd-HHmmss');
                    if exists (select 1 from storyPoint.tbWorkItem where itemCode = @autoItemCode)
                    begin
                        set @autoItemCode = @autoItemCode + '-' + right(cast(abs(checksum(newid())) as varchar(16)), 3);
                    end

                    insert into storyPoint.tbWorkItem (
                        itemCode, itemTitle, itemDescription, workItemTypePk, sprintName,
                        priorityLevel, defaultTypePoint, storyPoint, isManualAdjusted,
                        estimationStatus, executionStatus, createdByEmployeePk, isActive, createdAt, updatedAt
                    )
                    values (
                        @autoItemCode,
                        left(@autoItemTitle, 256),
                        isnull(nullif(ltrim(rtrim(@roundGoalNote)), N''), N'由盲投回合設定自動建立之實際工項'),
                        @fallbackTypePk,
                        N'待排程 Sprint',
                        'medium',
                        0.00,
                        0.00,
                        0,
                        'voting',
                        'todo',
                        @hostEmployeePk,
                        1,
                        sysdatetime(),
                        sysdatetime()
                    );
                    set @workItemPk = scope_identity();
                end
            end

            update storyPoint.tbWorkItem
            set estimationStatus = 'voting',
                updatedAt = sysdatetime()
            where pk = @workItemPk;
        end

        if @sessionPk > 0 and exists (select 1 from storyPoint.tbVotingSession where pk = @sessionPk)
        begin
            update storyPoint.tbVotingSession
            set sessionTitle = @sessionTitle,
                targetKind = @targetKind,
                workItemTypePk = @workItemTypePk,
                workItemPk = @workItemPk,
                cardDeckPk = @cardDeckPk,
                minStoryPoint = @cleanMin,
                maxStoryPoint = @cleanMax,
                maxAllowedSpread = isnull(@maxAllowedSpread, 5.00),
                consensusRule = isnull(nullif(ltrim(rtrim(@consensusRule)), ''), 'nearestFibonacci'),
                autoRevealWhenAllVoted = isnull(@autoRevealWhenAllVoted, 1),
                roundTimeLimitSecond = isnull(@roundTimeLimitSecond, 120),
                allowQuestionCard = isnull(@allowQuestionCard, 1),
                allowCoffeeCard = isnull(@allowCoffeeCard, 1),
                voteCommentPolicy = isnull(nullif(ltrim(rtrim(@voteCommentPolicy)), ''), 'optional'),
                restrictToParticipant = isnull(@restrictToParticipant, 1),
                roundGoalNote = @roundGoalNote,
                updatedAt = sysdatetime()
            where pk = @sessionPk;

            if isnull(@resetCurrentRoundVotes, 0) = 1
            begin
                declare @resetRoundNum int = 1;
                declare @resetRoundPk int = 0;

                select @resetRoundNum = currentRoundNumber
                from storyPoint.tbVotingSession
                where pk = @sessionPk;

                select @resetRoundPk = pk
                from storyPoint.tbVotingRound
                where votingSessionPk = @sessionPk
                  and roundNumber = @resetRoundNum;

                if @resetRoundPk > 0
                begin
                    delete from storyPoint.tbVoteRecord
                    where votingRoundPk = @resetRoundPk;

                    update storyPoint.tbVotingRound
                    set roundStatus = 'voting',
                        voteCount = 0,
                        averagePoint = null,
                        minPoint = null,
                        maxPoint = null,
                        revealedAt = null,
                        startedAt = sysdatetime()
                    where pk = @resetRoundPk;
                end

                update storyPoint.tbVotingSession
                set sessionStatus = 'voting',
                    updatedAt = sysdatetime()
                where pk = @sessionPk;
            end

            delete from storyPoint.tbVotingSessionParticipant
            where votingSessionPk = @sessionPk;
        end
        else
        begin
            if isnull(ltrim(rtrim(@sessionCode)), '') = ''
            begin
                set @sessionCode = 'VOTE-' + format(sysdatetime(), 'yyyyMMdd-HHmmss');
            end
            if exists (select 1 from storyPoint.tbVotingSession where sessionCode = @sessionCode)
            begin
                set @sessionCode = @sessionCode + '-' + right(cast(abs(checksum(newid())) as varchar(16)), 3);
            end

            insert into storyPoint.tbVotingSession (
                sessionCode, sessionTitle, targetKind, workItemTypePk,
                workItemPk, cardDeckPk, hostEmployeePk, currentRoundNumber,
                sessionStatus, minStoryPoint, maxStoryPoint, maxAllowedSpread,
                consensusRule, autoRevealWhenAllVoted, roundTimeLimitSecond,
                allowQuestionCard, allowCoffeeCard, voteCommentPolicy,
                restrictToParticipant, roundGoalNote, startedAt, createdAt, updatedAt
            )
            values (
                @sessionCode,
                @sessionTitle,
                @targetKind,
                @workItemTypePk,
                @workItemPk,
                @cardDeckPk,
                @hostEmployeePk,
                1,
                'voting',
                @cleanMin,
                @cleanMax,
                isnull(@maxAllowedSpread, 5.00),
                isnull(nullif(ltrim(rtrim(@consensusRule)), ''), 'nearestFibonacci'),
                isnull(@autoRevealWhenAllVoted, 1),
                isnull(@roundTimeLimitSecond, 120),
                isnull(@allowQuestionCard, 1),
                isnull(@allowCoffeeCard, 1),
                isnull(nullif(ltrim(rtrim(@voteCommentPolicy)), ''), 'optional'),
                isnull(@restrictToParticipant, 1),
                @roundGoalNote,
                sysdatetime(),
                sysdatetime(),
                sysdatetime()
            );
            set @sessionPk = scope_identity();

            -- 建立第 1 回合
            insert into storyPoint.tbVotingRound (
                votingSessionPk, roundNumber, roundStatus, voteCount, startedAt
            )
            values (
                @sessionPk, 1, 'voting', 0, sysdatetime()
            );
        end

        -- 建立授權參與投票之員工清單
        if isnull(ltrim(rtrim(@participantPkCsv)), '') <> ''
        begin
            insert into storyPoint.tbVotingSessionParticipant (
                votingSessionPk, employeePk, isRequiredVoter, joinedAt
            )
            select distinct
                @sessionPk,
                try_cast(ltrim(rtrim(s.value)) as int),
                1,
                sysdatetime()
            from string_split(@participantPkCsv, ',') as s
            inner join storyPoint.tbEmployee as e
                on e.pk = try_cast(ltrim(rtrim(s.value)) as int)
            where e.isActive = 1;
        end
        else
        begin
            insert into storyPoint.tbVotingSessionParticipant (
                votingSessionPk, employeePk, isRequiredVoter, joinedAt
            )
            select
                @sessionPk,
                e.pk,
                1,
                sysdatetime()
            from storyPoint.tbEmployee as e
            where e.isActive = 1;
        end

        -- 若更新既有議程參與名單後，當前盲投回合已全員完成投票，則自動翻牌揭曉
        declare @curRoundNum int = 1;
        declare @curSessStatus varchar(32) = '';
        declare @curRoundPk int = 0;
        declare @reqCount int = 0;
        declare @votedCount int = 0;

        select
            @curRoundNum = currentRoundNumber,
            @curSessStatus = sessionStatus
        from storyPoint.tbVotingSession
        where pk = @sessionPk;

        if @curSessStatus = 'voting' and isnull(@autoRevealWhenAllVoted, 1) = 1
        begin
            select @curRoundPk = pk
            from storyPoint.tbVotingRound
            where votingSessionPk = @sessionPk
              and roundNumber = @curRoundNum;

            select @reqCount = count(1)
            from storyPoint.tbVotingSessionParticipant as vsp
            inner join storyPoint.tbEmployee as e
                on vsp.employeePk = e.pk
            where vsp.votingSessionPk = @sessionPk
              and vsp.isRequiredVoter = 1
              and e.isActive = 1;

            select @votedCount = count(distinct vrec.employeePk)
            from storyPoint.tbVoteRecord as vrec
            inner join storyPoint.tbVotingSessionParticipant as vsp
                on vrec.votingSessionPk = vsp.votingSessionPk
               and vrec.employeePk = vsp.employeePk
            inner join storyPoint.tbEmployee as e
                on vsp.employeePk = e.pk
            where vrec.votingRoundPk = @curRoundPk
              and vsp.isRequiredVoter = 1
              and e.isActive = 1;

            if @reqCount > 0 and @votedCount >= @reqCount
            begin
                update storyPoint.tbVotingRound
                set roundStatus = 'revealed',
                    revealedAt = isnull(revealedAt, sysdatetime())
                where pk = @curRoundPk;

                update storyPoint.tbVotingSession
                set sessionStatus = 'revealed',
                    updatedAt = sysdatetime()
                where pk = @sessionPk;
            end
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spCreateVotingSession',
            @durationMillisecond = @durationMs,
            @executionResult = N'建立或更新多人 Story Point 投票議程與盲投回合設定成功',
            @operatorAccount = @operatorAccount;

        select
            v.votingSessionPk,
            v.sessionCode,
            v.sessionTitle,
            v.targetKind,
            v.targetDisplayName,
            v.cardDeckPk,
            v.deckName,
            v.cardValueCsv,
            v.minStoryPoint,
            v.maxStoryPoint,
            v.maxAllowedSpread,
            v.consensusRule,
            v.consensusRuleText,
            v.autoRevealWhenAllVoted,
            v.roundTimeLimitSecond,
            v.allowQuestionCard,
            v.allowCoffeeCard,
            v.voteCommentPolicy,
            v.voteCommentPolicyText,
            v.restrictToParticipant,
            v.roundGoalNote,
            v.participantCount,
            v.currentRoundNumber,
            v.sessionStatus,
            v.sessionStatusText
        from storyPoint.vwVotingSessionSummary as v
        where v.votingSessionPk = @sessionPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spCreateVotingSession',
            @durationMillisecond = @durationMs,
            @executionResult = N'建立或更新多人 Story Point 投票議程失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 10. 參與者針對當前投票回合出牌預存程序 (storyPoint.spCastVote)
-- ============================================================================
/*
用途說明: 參與者或管理者在當前盲投回合投出撲克牌點數與理由 (tbVoteRecord)，
          並自動檢核回合設定（參與者授權、最高/最低 Story Point 範圍、特殊牌限制、出牌理由規範、全員完成自動翻牌）
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @votingSessionPk int           : 投票議程主鍵
    @employeePk      int           : 投票員工主鍵
    @cardValue       varchar(32)   : 投出的撲克牌面值 (如 '1', '3', '5', '8', '?', '☕')
    @voteComment     nvarchar(512) : 估算理由或備註
    @operatorAccount varchar(128)  : 操作者帳號
修改歷程:
    2026-10-23 初版建立
    2026-10-23 加入盲投回合規則檢核與全員投票自動翻牌機制
*/
create or alter procedure storyPoint.spCastVote
    @votingSessionPk int,
    @employeePk int,
    @cardValue nvarchar(32),
    @voteComment nvarchar(512) = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @currentRound int = 1;
    declare @sessionStatus varchar(32) = '';
    declare @roundPk int = 0;
    declare @numericPoint decimal(8,2) = null;
    declare @minPointLimit decimal(8,2) = 0.00;
    declare @maxPointLimit decimal(8,2) = 100.00;
    declare @allowQuestion bit = 1;
    declare @allowCoffee bit = 1;
    declare @commentPolicy varchar(32) = 'optional';
    declare @restrictParticipant bit = 1;
    declare @autoReveal bit = 0;
    declare @cleanCard nvarchar(32) = ltrim(rtrim(isnull(@cardValue, N'')));
    declare @cleanComment nvarchar(512) = ltrim(rtrim(isnull(@voteComment, N'')));

    begin try
        if isnull(@employeePk, 0) <= 0 or not exists (select 1 from storyPoint.tbEmployee where pk = @employeePk)
        begin
            select top 1 @employeePk = pk
            from storyPoint.tbEmployee
            where loginAccount = @operatorAccount
              and isActive = 1
            order by pk asc;
        end

        select
            @currentRound = currentRoundNumber,
            @sessionStatus = sessionStatus,
            @minPointLimit = isnull(minStoryPoint, 0.00),
            @maxPointLimit = isnull(maxStoryPoint, 100.00),
            @allowQuestion = isnull(allowQuestionCard, 1),
            @allowCoffee = isnull(allowCoffeeCard, 1),
            @commentPolicy = isnull(voteCommentPolicy, 'optional'),
            @restrictParticipant = isnull(restrictToParticipant, 1),
            @autoReveal = isnull(autoRevealWhenAllVoted, 0)
        from storyPoint.tbVotingSession
        where pk = @votingSessionPk;

        if @sessionStatus <> 'voting'
        begin
            ;throw 50001, N'目前議程不在出牌投票階段（已翻牌或已定案），無法變更出牌。', 1;
        end

        -- 檢核是否僅限授權參與者投票
        if @restrictParticipant = 1 and not exists (
            select 1
            from storyPoint.tbVotingSessionParticipant
            where votingSessionPk = @votingSessionPk
              and employeePk = @employeePk
        )
        begin
            ;throw 50003, N'該員工不在本回合授權參與名單內，請由管理者於「設定盲投回合」中勾選授權後再出牌。', 1;
        end

        -- 檢核特殊牌限制
        if @cleanCard = '?' and @allowQuestion = 0
        begin
            ;throw 50004, N'本回合設定不開放投出「? (疑問牌)」，請選擇數值 Story Point 撲克牌。', 1;
        end

        if @cleanCard = N'☕' and @allowCoffee = 0
        begin
            ;throw 50005, N'本回合設定不開放投出「☕ (休息/拆解牌)」，請選擇數值 Story Point 撲克牌。', 1;
        end

        set @numericPoint = storyPoint.fnParseCardPoint(@cleanCard);

        -- 檢核最高與最低 Story Point 上下限
        if @numericPoint is not null
        begin
            if @numericPoint < @minPointLimit or @numericPoint > @maxPointLimit
            begin
                declare @rangeErr nvarchar(256) = concat(
                    N'投出的點數 (',
                    cast(@numericPoint as varchar(32)),
                    N' pt) 超出本回合設定範圍（最低 ',
                    cast(@minPointLimit as varchar(32)),
                    N' pt ～ 最高 ',
                    cast(@maxPointLimit as varchar(32)),
                    N' pt）。'
                );
                ;throw 50006, @rangeErr, 1;
            end
        end

        -- 檢核出牌理由必填規範
        if @commentPolicy = 'requiredAll' and @cleanComment = N''
        begin
            ;throw 50007, N'本回合設定為「全員必填估算理由」，請於備註欄簡短填寫您的估算依據後再送出。', 1;
        end

        if @commentPolicy = 'requiredExtreme' and @cleanComment = N'' and (
            @cleanCard in ('?', N'☕')
            or (@numericPoint is not null and (@numericPoint <= @minPointLimit or @numericPoint >= @maxPointLimit))
        )
        begin
            ;throw 50008, N'本回合設定當投出「最低/最高邊界點數或疑問牌」時必須填寫估算理由，請補充說明後送出。', 1;
        end

        select @roundPk = pk
        from storyPoint.tbVotingRound
        where votingSessionPk = @votingSessionPk
          and roundNumber = @currentRound;

        -- 若開放自由參與且該員工尚不在參與者名單中，自動補入
        if not exists (select 1 from storyPoint.tbVotingSessionParticipant where votingSessionPk = @votingSessionPk and employeePk = @employeePk)
        begin
            insert into storyPoint.tbVotingSessionParticipant (
                votingSessionPk, employeePk, isRequiredVoter, joinedAt
            )
            values (
                @votingSessionPk, @employeePk, 1, sysdatetime()
            );
        end

        if exists (select 1 from storyPoint.tbVoteRecord where votingRoundPk = @roundPk and employeePk = @employeePk)
        begin
            update storyPoint.tbVoteRecord
            set cardValue = @cleanCard,
                numericPointValue = @numericPoint,
                voteComment = @voteComment,
                updatedAt = sysdatetime()
            where votingRoundPk = @roundPk
              and employeePk = @employeePk;
        end
        else
        begin
            insert into storyPoint.tbVoteRecord (
                votingRoundPk, votingSessionPk, roundNumber, employeePk,
                cardValue, numericPointValue, voteComment, votedAt, updatedAt
            )
            values (
                @roundPk,
                @votingSessionPk,
                @currentRound,
                @employeePk,
                @cleanCard,
                @numericPoint,
                @voteComment,
                sysdatetime(),
                sysdatetime()
            );
        end

        -- 更新本回合已出牌人數與統計值
        update vr
        set vr.voteCount = isnull(stat.cnt, 0),
            vr.averagePoint = stat.avgPt,
            vr.minPoint = stat.minPt,
            vr.maxPoint = stat.maxPt
        from storyPoint.tbVotingRound as vr
        outer apply (
            select
                count(pk) as cnt,
                cast(round(avg(numericPointValue), 2) as decimal(8,2)) as avgPt,
                min(numericPointValue) as minPt,
                max(numericPointValue) as maxPt
            from storyPoint.tbVoteRecord
            where votingRoundPk = @roundPk
        ) as stat
        where vr.pk = @roundPk;

        -- 當所有必投參與者皆已完成出牌時，自動翻牌揭曉（全員投票自動翻牌）
        declare @requiredVoterCount int = 0;
        declare @votedParticipantCount int = 0;
        declare @isAutoRevealed bit = 0;

        select @requiredVoterCount = count(1)
        from storyPoint.tbVotingSessionParticipant as vsp
        inner join storyPoint.tbEmployee as e
            on vsp.employeePk = e.pk
        where vsp.votingSessionPk = @votingSessionPk
          and vsp.isRequiredVoter = 1
          and e.isActive = 1;

        select @votedParticipantCount = count(distinct vrec.employeePk)
        from storyPoint.tbVoteRecord as vrec
        inner join storyPoint.tbVotingSessionParticipant as vsp
            on vrec.votingSessionPk = vsp.votingSessionPk
           and vrec.employeePk = vsp.employeePk
        inner join storyPoint.tbEmployee as e
            on vsp.employeePk = e.pk
        where vrec.votingRoundPk = @roundPk
          and vsp.isRequiredVoter = 1
          and e.isActive = 1;

        if @requiredVoterCount > 0 and @votedParticipantCount >= @requiredVoterCount
        begin
            update storyPoint.tbVotingRound
            set roundStatus = 'revealed',
                revealedAt = isnull(revealedAt, sysdatetime())
            where pk = @roundPk;

            update storyPoint.tbVotingSession
            set sessionStatus = 'revealed',
                autoRevealWhenAllVoted = 1,
                updatedAt = sysdatetime()
            where pk = @votingSessionPk;

            set @sessionStatus = 'revealed';
            set @isAutoRevealed = 1;
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spCastVote',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工投出 Story Point 成功',
            @operatorAccount = @operatorAccount;

        select
            vrec.pk as voteRecordPk,
            vrec.votingSessionPk,
            vrec.roundNumber,
            vrec.employeePk,
            e.employeeName,
            vrec.cardValue,
            vrec.numericPointValue,
            vrec.voteComment,
            vrec.updatedAt,
            @sessionStatus as sessionStatus,
            @isAutoRevealed as isAutoRevealed,
            @votedParticipantCount as votedParticipantCount,
            @requiredVoterCount as requiredVoterCount
        from storyPoint.tbVoteRecord as vrec
        inner join storyPoint.tbEmployee as e
            on vrec.employeePk = e.pk
        where vrec.votingRoundPk = @roundPk
          and vrec.employeePk = @employeePk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spCastVote',
            @durationMillisecond = @durationMs,
            @executionResult = N'員工投出 Story Point 失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 11. 管理者控制投票議程狀態預存程序 (storyPoint.spControlVotingSession)
-- ============================================================================
/*
用途說明: 管理者控制多人投票議程：
          1. 'reveal'    : 翻牌揭曉本回合所有人的點數並結算平均/最高/最低值
          2. 'nextRound' : 記錄本回合討論摘要並開啟下一回合重新盲投
          3. 'finalize'  : 拍板定案最終 Story Point，自動回寫至「工項代表類型」或「實際工項」並記錄點數歷程
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @votingSessionPk          int             : 投票議程主鍵
    @controlAction            varchar(32)     : 'reveal' | 'nextRound' | 'finalize'
    @roundDiscussionNote      nvarchar(1024)  : 本回合討論紀錄
    @finalStoryPoint          decimal(8,2)    : 定案時之最終 Story Point
    @finalDecisionNote        nvarchar(1024)  : 定案核定理由說明
    @syncExistingOpenWorkItem bit             : 若為工項代表類型定案，是否同步更新未完成且未手動微調之既有工項
    @operatorEmployeePk       int             : 操作管理者員工主鍵
    @operatorAccount          varchar(128)    : 操作者帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spControlVotingSession
    @votingSessionPk int,
    @controlAction varchar(32),
    @roundDiscussionNote nvarchar(1024) = null,
    @finalStoryPoint decimal(8,2) = null,
    @finalDecisionNote nvarchar(1024) = null,
    @syncExistingOpenWorkItem bit = 0,
    @operatorEmployeePk int = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @currentRound int = 1;
    declare @targetKind varchar(32) = '';
    declare @workItemTypePk int = null;
    declare @workItemPk int = null;
    declare @oldPoint decimal(8,2) = null;
    declare @cleanAction varchar(32) = ltrim(rtrim(isnull(@controlAction, '')));
    declare @cleanDiscussionNote nvarchar(1024) = nullif(ltrim(rtrim(isnull(@roundDiscussionNote, N''))), N'');
    declare @cleanFinalDecisionNote nvarchar(1024) = nullif(ltrim(rtrim(isnull(@finalDecisionNote, N''))), N'');

    begin try
        if @operatorEmployeePk is not null and not exists (select 1 from storyPoint.tbEmployee where pk = @operatorEmployeePk)
        begin
            set @operatorEmployeePk = null;
            select top 1 @operatorEmployeePk = pk
            from storyPoint.tbEmployee
            where loginAccount = @operatorAccount;
        end

        select
            @currentRound = isnull(currentRoundNumber, 1),
            @targetKind = targetKind,
            @workItemTypePk = workItemTypePk,
            @workItemPk = workItemPk
        from storyPoint.tbVotingSession
        where pk = @votingSessionPk;

        if not exists (
            select 1
            from storyPoint.tbVotingRound
            where votingSessionPk = @votingSessionPk
              and roundNumber = @currentRound
        )
        begin
            insert into storyPoint.tbVotingRound (
                votingSessionPk, roundNumber, roundStatus, voteCount, roundDiscussionNote, startedAt
            )
            values (
                @votingSessionPk, @currentRound, 'voting', 0, @cleanDiscussionNote, sysdatetime()
            );
        end

        if @cleanAction = 'reveal'
        begin
            update vr
            set vr.roundStatus = 'revealed',
                vr.revealedAt = isnull(vr.revealedAt, sysdatetime()),
                vr.roundDiscussionNote = isnull(@cleanDiscussionNote, vr.roundDiscussionNote),
                vr.voteCount = isnull(stat.cnt, 0),
                vr.averagePoint = stat.avgPt,
                vr.minPoint = stat.minPt,
                vr.maxPoint = stat.maxPt
            from storyPoint.tbVotingRound as vr
            outer apply (
                select
                    count(pk) as cnt,
                    cast(round(avg(numericPointValue), 2) as decimal(8,2)) as avgPt,
                    min(numericPointValue) as minPt,
                    max(numericPointValue) as maxPt
                from storyPoint.tbVoteRecord
                where votingRoundPk = vr.pk
            ) as stat
            where vr.votingSessionPk = @votingSessionPk
              and vr.roundNumber = @currentRound;

            update storyPoint.tbVotingSession
            set sessionStatus = 'revealed',
                updatedAt = sysdatetime()
            where pk = @votingSessionPk;
        end
        else if @cleanAction = 'nextRound'
        begin
            update vr
            set vr.roundStatus = 'revealed',
                vr.revealedAt = isnull(vr.revealedAt, sysdatetime()),
                vr.roundDiscussionNote = isnull(@cleanDiscussionNote, vr.roundDiscussionNote),
                vr.voteCount = isnull(stat.cnt, 0),
                vr.averagePoint = stat.avgPt,
                vr.minPoint = stat.minPt,
                vr.maxPoint = stat.maxPt
            from storyPoint.tbVotingRound as vr
            outer apply (
                select
                    count(pk) as cnt,
                    cast(round(avg(numericPointValue), 2) as decimal(8,2)) as avgPt,
                    min(numericPointValue) as minPt,
                    max(numericPointValue) as maxPt
                from storyPoint.tbVoteRecord
                where votingRoundPk = vr.pk
            ) as stat
            where vr.votingSessionPk = @votingSessionPk
              and vr.roundNumber = @currentRound;

            declare @nextRound int = @currentRound + 1;

            if not exists (select 1 from storyPoint.tbVotingRound where votingSessionPk = @votingSessionPk and roundNumber = @nextRound)
            begin
                insert into storyPoint.tbVotingRound (
                    votingSessionPk, roundNumber, roundStatus, voteCount, startedAt
                )
                values (
                    @votingSessionPk, @nextRound, 'voting', 0, sysdatetime()
                );
            end
            else
            begin
                delete from storyPoint.tbVoteRecord
                where votingSessionPk = @votingSessionPk
                  and roundNumber = @nextRound;

                update storyPoint.tbVotingRound
                set roundStatus = 'voting',
                    voteCount = 0,
                    averagePoint = null,
                    minPoint = null,
                    maxPoint = null,
                    revealedAt = null,
                    startedAt = sysdatetime()
                where votingSessionPk = @votingSessionPk
                  and roundNumber = @nextRound;
            end

            update storyPoint.tbVotingSession
            set currentRoundNumber = @nextRound,
                sessionStatus = 'voting',
                roundGoalNote = isnull(@cleanDiscussionNote, roundGoalNote),
                updatedAt = sysdatetime()
            where pk = @votingSessionPk;
        end
        else if @cleanAction = 'finalize'
        begin
            declare @effectiveFinalNote nvarchar(1024) = isnull(@cleanFinalDecisionNote, @cleanDiscussionNote);
            declare @effectiveRoundNote nvarchar(1024) = isnull(@cleanDiscussionNote, @cleanFinalDecisionNote);

            update vr
            set vr.roundStatus = 'revealed',
                vr.revealedAt = isnull(vr.revealedAt, sysdatetime()),
                vr.roundDiscussionNote = isnull(@effectiveRoundNote, vr.roundDiscussionNote),
                vr.voteCount = isnull(stat.cnt, 0),
                vr.averagePoint = stat.avgPt,
                vr.minPoint = stat.minPt,
                vr.maxPoint = stat.maxPt
            from storyPoint.tbVotingRound as vr
            outer apply (
                select
                    count(pk) as cnt,
                    cast(round(avg(numericPointValue), 2) as decimal(8,2)) as avgPt,
                    min(numericPointValue) as minPt,
                    max(numericPointValue) as maxPt
                from storyPoint.tbVoteRecord
                where votingRoundPk = vr.pk
            ) as stat
            where vr.votingSessionPk = @votingSessionPk
              and vr.roundNumber = @currentRound;

            update storyPoint.tbVotingSession
            set sessionStatus = 'finalized',
                finalStoryPoint = @finalStoryPoint,
                finalDecisionNote = isnull(@effectiveFinalNote, finalDecisionNote),
                finalizedAt = sysdatetime(),
                updatedAt = sysdatetime()
            where pk = @votingSessionPk;

            if @targetKind = 'workItemType' and @workItemTypePk is not null
            begin
                select @oldPoint = standardStoryPoint
                from storyPoint.tbWorkItemType
                where pk = @workItemTypePk;

                update storyPoint.tbWorkItemType
                set standardStoryPoint = @finalStoryPoint,
                    estimationStatus = 'finalized',
                    lastVotingSessionPk = @votingSessionPk,
                    updatedAt = sysdatetime()
                where pk = @workItemTypePk;

                insert into storyPoint.tbPointHistory (
                    targetKind, workItemTypePk, workItemPk, votingSessionPk,
                    oldStoryPoint, newStoryPoint, changeSource, changeReason,
                    changedByEmployeePk, changedAt
                )
                values (
                    'workItemType',
                    @workItemTypePk,
                    null,
                    @votingSessionPk,
                    @oldPoint,
                    @finalStoryPoint,
                    'votingFinalized',
                    isnull(@finalDecisionNote, N'透過多人 Story Point 投票議程拍板定案'),
                    @operatorEmployeePk,
                    sysdatetime()
                );

                if @syncExistingOpenWorkItem = 1
                begin
                    update storyPoint.tbWorkItem
                    set defaultTypePoint = @finalStoryPoint,
                        storyPoint = @finalStoryPoint,
                        updatedAt = sysdatetime()
                    where workItemTypePk = @workItemTypePk
                      and isManualAdjusted = 0
                      and executionStatus <> 'done'
                      and isActive = 1;

                    update wia
                    set wia.allocatedStoryPoint = cast(round(wi.storyPoint * (wia.allocationPercent / 100.00), 2) as decimal(8,2)),
                        wia.updatedAt = sysdatetime()
                    from storyPoint.tbWorkItemAssignment as wia
                    inner join storyPoint.tbWorkItem as wi
                        on wia.workItemPk = wi.pk
                    where wi.workItemTypePk = @workItemTypePk
                      and wi.isManualAdjusted = 0
                      and wi.executionStatus <> 'done'
                      and wi.isActive = 1;
                end
            end
            else if @targetKind = 'workItem' and @workItemPk is not null
            begin
                select @oldPoint = storyPoint
                from storyPoint.tbWorkItem
                where pk = @workItemPk;

                update storyPoint.tbWorkItem
                set storyPoint = @finalStoryPoint,
                    isManualAdjusted = 1,
                    adjustmentReason = isnull(@finalDecisionNote, N'透過個別工項多人投票議程核定'),
                    estimationStatus = 'finalized',
                    updatedAt = sysdatetime()
                where pk = @workItemPk;

                update storyPoint.tbWorkItemAssignment
                set allocatedStoryPoint = cast(round(@finalStoryPoint * (allocationPercent / 100.00), 2) as decimal(8,2)),
                    updatedAt = sysdatetime()
                where workItemPk = @workItemPk;

                insert into storyPoint.tbPointHistory (
                    targetKind, workItemTypePk, workItemPk, votingSessionPk,
                    oldStoryPoint, newStoryPoint, changeSource, changeReason,
                    changedByEmployeePk, changedAt
                )
                values (
                    'workItem',
                    @workItemTypePk,
                    @workItemPk,
                    @votingSessionPk,
                    @oldPoint,
                    @finalStoryPoint,
                    'votingFinalized',
                    isnull(@finalDecisionNote, N'透過個別工項多人投票議程拍板定案'),
                    @operatorEmployeePk,
                    sysdatetime()
                );
            end
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spControlVotingSession',
            @durationMillisecond = @durationMs,
            @executionResult = N'投票議程控制操作完成',
            @operatorAccount = @operatorAccount;

        select
            v.votingSessionPk,
            v.sessionCode,
            v.sessionTitle,
            v.targetKind,
            v.targetDisplayName,
            v.currentRoundNumber,
            v.sessionStatus,
            v.sessionStatusText,
            v.finalStoryPoint,
            v.finalDecisionNote,
            v.currentRoundAveragePoint,
            v.currentRoundMinPoint,
            v.currentRoundMaxPoint,
            v.currentRoundVoteCount
        from storyPoint.vwVotingSessionSummary as v
        where v.votingSessionPk = @votingSessionPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spControlVotingSession',
            @durationMillisecond = @durationMs,
            @executionResult = N'投票議程控制操作失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 12. 事後查詢訂出 Story Point 的完整過程預存程序 (storyPoint.spGetVotingProcessHistory)
-- ============================================================================
/*
用途說明: 查詢單一或全部投票議程的完整訂定過程，包含：
          Result Set 1: 投票議程總覽與最終核定點數 (vwVotingSessionSummary)
          Result Set 2: 各回合統計與討論摘要 (tbVotingRound)
          Result Set 3: 各回合每位員工投出的牌面與理由明細 (tbVoteRecord；若當前仍在盲投中則對非本人遮罩牌面)
          Result Set 4: 參與者名單與本回合出牌狀態 (tbVotingSessionParticipant)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @votingSessionPk int : 指定投票議程主鍵 (若傳 null 則回傳所有議程)
    @workItemTypePk  int : 依工項代表類型篩選
    @workItemPk      int : 依個別工項篩選
    @viewerEmployeePk int: 檢視者員工主鍵 (用於盲投階段顯示本人已選牌面)
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spGetVotingProcessHistory
    @votingSessionPk int = null,
    @workItemTypePk int = null,
    @workItemPk int = null,
    @viewerEmployeePk int = null,
    @isViewerAdmin bit = 1
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;

    begin try
        -- 自動檢核：若有進行中 ('voting') 之議程其當前盲投回合所有有效受邀參與者皆已投票，自動將該回合與議程切換為 'revealed' (自動翻牌)
        update vr
        set vr.roundStatus = 'revealed',
            vr.revealedAt = isnull(vr.revealedAt, sysdatetime())
        from storyPoint.tbVotingRound as vr
        inner join storyPoint.tbVotingSession as vs
            on vr.votingSessionPk = vs.pk
           and vr.roundNumber = vs.currentRoundNumber
        cross apply (
            select count(1) as reqCnt
            from storyPoint.tbVotingSessionParticipant as vsp
            inner join storyPoint.tbEmployee as e
                on vsp.employeePk = e.pk
            where vsp.votingSessionPk = vs.pk
              and vsp.isRequiredVoter = 1
              and e.isActive = 1
        ) as req
        cross apply (
            select count(distinct vrec.employeePk) as votedCnt
            from storyPoint.tbVoteRecord as vrec
            inner join storyPoint.tbVotingSessionParticipant as vsp
                on vrec.votingSessionPk = vsp.votingSessionPk
               and vrec.employeePk = vsp.employeePk
            inner join storyPoint.tbEmployee as e
                on vsp.employeePk = e.pk
            where vrec.votingRoundPk = vr.pk
              and vsp.isRequiredVoter = 1
              and e.isActive = 1
        ) as vt
        where vs.sessionStatus = 'voting'
          and req.reqCnt > 0
          and vt.votedCnt >= req.reqCnt;

        update vs
        set vs.sessionStatus = 'revealed',
            vs.autoRevealWhenAllVoted = 1,
            vs.updatedAt = sysdatetime()
        from storyPoint.tbVotingSession as vs
        inner join storyPoint.tbVotingRound as vr
            on vr.votingSessionPk = vs.pk
           and vr.roundNumber = vs.currentRoundNumber
        where vs.sessionStatus = 'voting'
          and vr.roundStatus = 'revealed';

        -- 若已定案 ('finalized') 之議程有填寫定案決議說明 (finalDecisionNote)，但最後一回合尚未填入回合討論紀錄 (roundDiscussionNote)，自動將定案決議說明同步補入最後一回合紀錄
        update vr
        set vr.roundDiscussionNote = vs.finalDecisionNote
        from storyPoint.tbVotingRound as vr
        inner join storyPoint.tbVotingSession as vs
            on vr.votingSessionPk = vs.pk
           and vr.roundNumber = vs.currentRoundNumber
        where vs.sessionStatus = 'finalized'
          and isnull(ltrim(rtrim(vs.finalDecisionNote)), N'') <> N''
          and isnull(ltrim(rtrim(vr.roundDiscussionNote)), N'') = N'';

        -- Result Set 1: 投票議程總覽
        select
            v.votingSessionPk,
            v.sessionCode,
            v.sessionTitle,
            v.targetKind,
            v.targetKindText,
            v.workItemTypePk,
            v.typeCode,
            v.typeName,
            v.workItemPk,
            v.itemCode,
            v.itemTitle,
            v.targetDisplayName,
            v.cardDeckPk,
            v.deckCode,
            v.deckName,
            v.cardValueCsv,
            v.hostEmployeePk,
            v.hostEmployeeName,
            v.currentRoundNumber,
            v.sessionStatus,
            v.sessionStatusText,
            v.finalStoryPoint,
            v.finalDecisionNote,
            v.minStoryPoint,
            v.maxStoryPoint,
            v.maxAllowedSpread,
            v.consensusRule,
            v.consensusRuleText,
            v.autoRevealWhenAllVoted,
            v.roundTimeLimitSecond,
            v.allowQuestionCard,
            v.allowCoffeeCard,
            v.voteCommentPolicy,
            v.voteCommentPolicyText,
            v.restrictToParticipant,
            v.roundGoalNote,
            v.participantCount,
            v.startedAt,
            v.finalizedAt,
            v.totalRoundCount,
            v.totalVoteRecordCount,
            v.currentRoundStatus,
            case when @isViewerAdmin = 1 or v.sessionStatus in ('revealed', 'finalized') then v.currentRoundAveragePoint else null end as currentRoundAveragePoint,
            case when @isViewerAdmin = 1 or v.sessionStatus in ('revealed', 'finalized') then v.currentRoundMinPoint else null end as currentRoundMinPoint,
            case when @isViewerAdmin = 1 or v.sessionStatus in ('revealed', 'finalized') then v.currentRoundMaxPoint else null end as currentRoundMaxPoint,
            v.currentRoundVoteCount,
            v.currentRoundDiscussionNote
        from storyPoint.vwVotingSessionSummary as v
        where (@votingSessionPk is null or v.votingSessionPk = @votingSessionPk)
          and (@workItemTypePk is null or v.workItemTypePk = @workItemTypePk)
          and (@workItemPk is null or v.workItemPk = @workItemPk)
          and (
              @isViewerAdmin = 1
              or v.restrictToParticipant = 0
              or v.hostEmployeePk = @viewerEmployeePk
              or exists (
                  select 1
                  from storyPoint.tbVotingSessionParticipant as vsp
                  where vsp.votingSessionPk = v.votingSessionPk
                    and vsp.employeePk = @viewerEmployeePk
              )
              or exists (
                  select 1
                  from storyPoint.tbWorkItemAssignment as wia
                  where wia.workItemPk = v.workItemPk
                    and wia.employeePk = @viewerEmployeePk
              )
              or exists (
                  select 1
                  from storyPoint.tbVoteRecord as vrec
                  where vrec.votingSessionPk = v.votingSessionPk
                    and vrec.employeePk = @viewerEmployeePk
              )
          )
        order by
            case
                when v.sessionStatus = 'voting' then 0
                when v.sessionStatus = 'revealed' then 1
                else 2
            end asc,
            v.votingSessionPk desc;

        -- Result Set 2: 各回合統計紀錄 (盲投進行中隱藏統計分數；一旦翻牌揭曉或定案，所有參與者畫面皆顯示翻牌統計結果)
        select
            vr.pk as votingRoundPk,
            vr.votingSessionPk,
            vs.sessionCode,
            vr.roundNumber,
            vr.roundStatus,
            case
                when vr.roundStatus = 'voting' and vs.sessionStatus = 'voting' then null
                else vr.averagePoint
            end as averagePoint,
            case
                when vr.roundStatus = 'voting' and vs.sessionStatus = 'voting' then null
                else vr.minPoint
            end as minPoint,
            case
                when vr.roundStatus = 'voting' and vs.sessionStatus = 'voting' then null
                else vr.maxPoint
            end as maxPoint,
            vr.voteCount,
            vr.roundDiscussionNote,
            vr.startedAt,
            vr.revealedAt
        from storyPoint.tbVotingRound as vr
        inner join storyPoint.tbVotingSession as vs
            on vr.votingSessionPk = vs.pk
        where (@votingSessionPk is null or vr.votingSessionPk = @votingSessionPk)
          and (@workItemTypePk is null or vs.workItemTypePk = @workItemTypePk)
          and (@workItemPk is null or vs.workItemPk = @workItemPk)
        order by vr.votingSessionPk desc, vr.roundNumber asc;

        -- Result Set 3: 各回合員工出牌明細 (盲投階段其他人顯示已蓋牌；全員投完自動翻牌或定案後，所有人畫面同步翻開顯示各參與者牌面)
        select
            vrec.pk as voteRecordPk,
            vrec.votingRoundPk,
            vrec.votingSessionPk,
            vs.sessionCode,
            vrec.roundNumber,
            vr.roundStatus,
            vrec.employeePk,
            e.employeeCode,
            e.employeeName,
            e.jobTitle,
            d.departmentName,
            case
                when vr.roundStatus = 'voting' and vs.sessionStatus = 'voting' and isnull(@viewerEmployeePk, -1) <> vrec.employeePk
                then N'🎴 已出牌(盲投蓋牌)'
                when vrec.cardValue = N'?' then N'? (需求不明需討論)'
                when vrec.cardValue = N'☕' then N'☕ (暫停休息)'
                else concat(vrec.cardValue, N' pt')
            end as displayCardValue,
            case
                when vr.roundStatus = 'voting' and vs.sessionStatus = 'voting' and isnull(@viewerEmployeePk, -1) <> vrec.employeePk
                then null
                else vrec.numericPointValue
            end as numericPointValue,
            case
                when vr.roundStatus = 'voting' and vs.sessionStatus = 'voting' and isnull(@viewerEmployeePk, -1) <> vrec.employeePk
                then N'(待全員投票翻牌後揭曉)'
                else isnull(vrec.voteComment, N'')
            end as voteComment,
            vrec.votedAt,
            vrec.updatedAt
        from storyPoint.tbVoteRecord as vrec
        inner join storyPoint.tbVotingRound as vr
            on vrec.votingRoundPk = vr.pk
        inner join storyPoint.tbVotingSession as vs
            on vrec.votingSessionPk = vs.pk
        inner join storyPoint.tbEmployee as e
            on vrec.employeePk = e.pk
        inner join storyPoint.tbDepartment as d
            on e.departmentPk = d.pk
        where (@votingSessionPk is null or vrec.votingSessionPk = @votingSessionPk)
          and (@workItemTypePk is null or vs.workItemTypePk = @workItemTypePk)
          and (@workItemPk is null or vs.workItemPk = @workItemPk)
        order by vrec.votingSessionPk desc, vrec.roundNumber asc, e.pk asc;

        -- Result Set 4: 參與者清單與當前最新回合出牌桌面狀態 (盲投階段他人蓋牌；全員投完自動翻牌後所有人畫面同步翻開全部人的牌)
        select
            vsp.votingSessionPk,
            vsp.employeePk,
            e.employeeCode,
            e.employeeName,
            e.jobTitle,
            d.departmentName,
            case when curVote.pk is not null then 1 else 0 end as hasVotedCurrentRound,
            case
                when curVote.pk is null then N'⏳ 尚未出牌'
                when vs.sessionStatus = 'voting' and isnull(@viewerEmployeePk, -1) <> vsp.employeePk then N'🎴 已出牌(盲投蓋牌)'
                when curVote.cardValue = N'?' then N'? (需求不明)'
                when curVote.cardValue = N'☕' then N'☕ (暫停休息)'
                else concat(curVote.cardValue, N' pt')
            end as currentRoundCardDisplay,
            case
                when curVote.pk is null then N''
                when vs.sessionStatus = 'voting' and isnull(@viewerEmployeePk, -1) <> vsp.employeePk then N'(待全員投票後自動翻牌)'
                else isnull(curVote.voteComment, N'')
            end as currentRoundCommentDisplay
        from storyPoint.tbVotingSessionParticipant as vsp
        inner join storyPoint.tbVotingSession as vs
            on vsp.votingSessionPk = vs.pk
        inner join storyPoint.tbEmployee as e
            on vsp.employeePk = e.pk
        inner join storyPoint.tbDepartment as d
            on e.departmentPk = d.pk
        left join storyPoint.tbVotingRound as curRound
            on curRound.votingSessionPk = vs.pk
           and curRound.roundNumber = vs.currentRoundNumber
        left join storyPoint.tbVoteRecord as curVote
            on curVote.votingRoundPk = curRound.pk
           and curVote.employeePk = vsp.employeePk
        where (@votingSessionPk is null or vsp.votingSessionPk = @votingSessionPk)
        order by vsp.votingSessionPk desc, e.departmentPk asc, e.pk asc;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spGetVotingProcessHistory',
            @durationMillisecond = @durationMs,
            @executionResult = N'查詢投票過程紀錄失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = 'system';
        throw;
    end catch
end
go

-- ============================================================================
-- 13. 綜合查詢工項、員工與部門 Story Point 儀表板報表預存程序 (storyPoint.spGetStoryPointDashboard)
-- ============================================================================
/*
用途說明: 一次回傳系統全方位 Story Point 查詢與統計資料：
          Result Set 1: 各部門總 Story Point 數與負載統計 (vwDepartmentPointSummary)
          Result Set 2: 各員工身上的工項數與總 Story Point 數 (vwEmployeePointSummary)
          Result Set 3: 各工項代表類型與標準 Story Point 清單 (tbWorkItemType)
          Result Set 4: 各實際工項的 Story Point 與指派明細 (vwWorkItemDetail)
          Result Set 5: 員工工項賦予關聯明細 (tbWorkItemAssignment)
          Result Set 6: Story Point 訂定與異動歷程紀錄 (tbPointHistory)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @departmentPk    int           : 篩選特定部門主鍵 (null 為全部)
    @employeePk      int           : 篩選特定員工主鍵 (null 為全部)
    @workItemTypePk  int           : 篩選特定工項代表類型主鍵 (null 為全部)
    @executionStatus varchar(32)   : 篩選工項執行狀態 (null 或 '' 為全部)
    @keyword         nvarchar(128) : 關鍵字搜尋
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spGetStoryPointDashboard
    @departmentPk int = null,
    @employeePk int = null,
    @workItemTypePk int = null,
    @executionStatus varchar(32) = null,
    @keyword nvarchar(128) = null,
    @viewerEmployeePk int = null,
    @isViewerAdmin bit = 1
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @cleanStatus varchar(32) = nullif(ltrim(rtrim(isnull(@executionStatus, ''))), '');
    declare @cleanKeyword nvarchar(128) = nullif(ltrim(rtrim(isnull(@keyword, ''))), '');
    declare @effectiveEmpPk int = case when @isViewerAdmin = 0 then @viewerEmployeePk else @employeePk end;

    begin try
        -- Result Set 1: 部門總 Story Point 數統計 (僅管理者可看部門與他人總分統計)
        select
            d.departmentPk,
            d.departmentCode,
            d.departmentName,
            d.parentDepartmentPk,
            d.parentDepartmentName,
            d.managerName,
            d.managerEmailCsv,
            d.departmentManagerCount,
            d.departmentNote,
            d.isActive,
            d.totalEmployeeCount,
            d.activeEmployeeCount,
            d.distinctWorkItemCount,
            d.assignmentRecordCount,
            d.totalDepartmentStoryPoint,
            d.todoDepartmentStoryPoint,
            d.inProgressDepartmentStoryPoint,
            d.doneDepartmentStoryPoint,
            d.totalDeductedDepartmentStoryPoint,
            d.distinctWorkItemRawPointSum,
            d.averageStoryPointPerEmployee
        from storyPoint.vwDepartmentPointSummary as d
        where @isViewerAdmin = 1
          and (@departmentPk is null or d.departmentPk = @departmentPk)
        order by d.totalDepartmentStoryPoint desc, d.departmentPk asc;

        -- Result Set 2: 員工身上的總 Story Point 數、部門主管註記與樹狀遞迴階層統計 (一般員工僅可看到自己)
        select
            e.employeePk,
            e.employeeCode,
            e.employeeName,
            e.loginAccount,
            e.emailAddress,
            e.jobTitle,
            e.roleName,
            e.roleDisplayName,
            e.departmentPk,
            e.departmentCode,
            e.departmentName,
            e.isDepartmentManager,
            e.isDepartmentManagerText,
            e.managerEmployeePk,
            e.managerEmployeeCode,
            e.managerEmployeeName,
            e.managerEmailAddress,
            e.departmentManagerEmployeePk,
            e.departmentManagerName,
            e.departmentManagerEmail,
            e.departmentManagerCount,
            e.hrManagerEmployeePk,
            e.hrManagerName,
            e.hrManagerEmail,
            e.hierarchyLevel,
            e.hierarchyPath,
            e.hierarchyIndentName,
            e.hierarchySortKey,
            e.directSubordinateCount,
            e.isActive,
            e.assignedItemCount,
            e.todoItemCount,
            e.inProgressItemCount,
            e.doneItemCount,
            e.totalAllocatedStoryPoint,
            e.todoAllocatedStoryPoint,
            e.inProgressAllocatedStoryPoint,
            e.doneAllocatedStoryPoint,
            e.totalDeductedStoryPoint,
            e.totalRawWorkItemPoint,
            e.workItemListSummary
        from storyPoint.vwEmployeePointSummary as e
        where (@departmentPk is null or e.departmentPk = @departmentPk)
          and (@effectiveEmpPk is null or e.employeePk = @effectiveEmpPk)
          and (
              @cleanKeyword is null
              or e.employeeCode like '%' + @cleanKeyword + '%'
              or e.employeeName like '%' + @cleanKeyword + '%'
              or e.loginAccount like '%' + @cleanKeyword + '%'
              or e.departmentName like '%' + @cleanKeyword + '%'
              or e.jobTitle like N'%' + @cleanKeyword + N'%'
              or e.hierarchyPath like N'%' + @cleanKeyword + N'%'
          )
        order by e.totalAllocatedStoryPoint desc, e.departmentPk asc, e.hierarchySortKey asc, e.employeePk asc;

        -- Result Set 3: 工項代表類型清單
        select
            wit.pk as workItemTypePk,
            wit.typeCode,
            wit.typeName,
            wit.categoryName,
            wit.defaultWorkItemKind,
            isnull(
                wik.kindName,
                case
                    when wit.defaultWorkItemKind = 'maintenance' then N'維運'
                    when wit.defaultWorkItemKind = 'consultingHour' then N'顧問時數'
                    when wit.defaultWorkItemKind = 'project' then N'專案'
                    else cast(wit.defaultWorkItemKind as nvarchar(128))
                end
            ) as defaultWorkItemKindText,
            isnull(
                wik.badgeIcon,
                case
                    when wit.defaultWorkItemKind = 'maintenance' then N'🛠️'
                    when wit.defaultWorkItemKind = 'consultingHour' then N'⏳'
                    else N'🏗️'
                end
            ) as defaultWorkItemKindIcon,
            isnull(
                wik.badgeColor,
                case
                    when wit.defaultWorkItemKind = 'maintenance' then 'amber'
                    when wit.defaultWorkItemKind = 'consultingHour' then 'purple'
                    else 'blue'
                end
            ) as defaultWorkItemKindColor,
            wit.description,
            wit.standardStoryPoint,
            wit.defaultCardDeckPk,
            cd.deckName as defaultCardDeckName,
            wit.estimationStatus,
            case
                when wit.estimationStatus = 'finalized' then N'已定案'
                when wit.estimationStatus = 'voting' then N'投票估算中'
                else N'待估算'
            end as estimationStatusText,
            wit.lastVotingSessionPk,
            (
                select count(1)
                from storyPoint.tbWorkItem as wi
                where wi.workItemTypePk = wit.pk
                  and wi.isActive = 1
                  and (
                      @isViewerAdmin = 1
                      or exists (
                          select 1
                          from storyPoint.tbWorkItemAssignment as subWia
                          where subWia.workItemPk = wi.pk
                            and subWia.employeePk = @viewerEmployeePk
                      )
                  )
            ) as linkedWorkItemCount,
            wit.isActive,
            wit.updatedAt
        from storyPoint.tbWorkItemType as wit
        left join storyPoint.tbCardDeck as cd
            on wit.defaultCardDeckPk = cd.pk
        left join storyPoint.tbWorkItemKind as wik
            on wit.defaultWorkItemKind = wik.kindCode
        where wit.isActive = 1
          and (@workItemTypePk is null or wit.pk = @workItemTypePk)
        order by wit.pk asc;

        -- Result Set 4: 各工項的 Story Point 與指派彙總 (一般員工僅可看到自己的工項與自己的分數，不可看到他人)
        select
            wi.pk as workItemPk,
            wi.itemCode,
            wi.itemTitle,
            wi.itemDescription,
            wi.workItemTypePk,
            wi.typeCode,
            wi.typeName,
            wi.categoryName,
            wi.workItemKind,
            wi.workItemKindText,
            wi.workItemKindIcon,
            wi.workItemKindColor,
            wi.sprintName,
            wi.priorityLevel,
            wi.defaultTypePoint,
            case
                when @isViewerAdmin = 1 then wi.storyPoint
                else isnull(myAssign.allocatedStoryPoint, wi.storyPoint)
            end as storyPoint,
            wi.storyPoint as rawWorkItemTotalPoint,
            wi.originalStoryPoint,
            wi.deductedStoryPoint,
            wi.isManualAdjusted,
            wi.pointSourceText,
            wi.adjustmentReason,
            wi.estimationStatus,
            wi.executionStatus,
            case
                when wi.executionStatus = 'todo' then N'待處理'
                when wi.executionStatus = 'inProgress' then N'進行中'
                when wi.executionStatus = 'done' then N'已完成'
                else wi.executionStatus
            end as executionStatusText,
            wi.startDate,
            wi.endDate,
            wi.dueDate,
            wi.isExpiredProcessed,
            wi.expiredProcessedAt,
            wi.expiryNotificationSent,
            wi.expiryNotificationSentAt,
            wi.expiryNotificationRecipient,
            wi.createdByEmployeeName,
            case when @isViewerAdmin = 1 then wi.assigneeCount else 1 end as assigneeCount,
            case
                when @isViewerAdmin = 1 then wi.assigneeSummary
                else concat(
                    myAssign.employeeName,
                    N'(',
                    myAssign.departmentName,
                    N': ',
                    cast(myAssign.allocatedStoryPoint as varchar(32)),
                    N' pt',
                    case
                        when wi.isExpiredProcessed = 1 and myAssign.deductedAllocatedStoryPoint > 0
                        then concat(N' [已扣 ', cast(myAssign.deductedAllocatedStoryPoint as varchar(32)), N' pt]')
                        else N''
                    end,
                    N' / ',
                    cast(myAssign.allocationPercent as varchar(32)),
                    N'%)'
                )
            end as assigneeSummary,
            case
                when @isViewerAdmin = 1 then wi.departmentSummary
                else isnull(myAssign.departmentName, N'-')
            end as departmentSummary,
            wi.isActive,
            wi.createdAt,
            wi.updatedAt
        from storyPoint.vwWorkItemDetail as wi
        outer apply (
            select top 1
                subWia.allocatedStoryPoint,
                subWia.deductedAllocatedStoryPoint,
                subWia.allocationPercent,
                subEmp.employeeName,
                subDept.departmentName
            from storyPoint.tbWorkItemAssignment as subWia
            inner join storyPoint.tbEmployee as subEmp
                on subWia.employeePk = subEmp.pk
            inner join storyPoint.tbDepartment as subDept
                on subEmp.departmentPk = subDept.pk
            where subWia.workItemPk = wi.pk
              and subWia.employeePk = @viewerEmployeePk
        ) as myAssign
        where wi.isActive = 1
          and (@workItemTypePk is null or wi.workItemTypePk = @workItemTypePk)
          and (@cleanStatus is null or wi.executionStatus = @cleanStatus)
          and (
              @departmentPk is null
              or exists (
                  select 1
                  from storyPoint.tbWorkItemAssignment as wia
                  inner join storyPoint.tbEmployee as emp
                      on wia.employeePk = emp.pk
                  where wia.workItemPk = wi.pk
                    and emp.departmentPk = @departmentPk
              )
          )
          and (
              @effectiveEmpPk is null
              or exists (
                  select 1
                  from storyPoint.tbWorkItemAssignment as wia
                  where wia.workItemPk = wi.pk
                    and wia.employeePk = @effectiveEmpPk
              )
          )
          and (
              @cleanKeyword is null
              or wi.itemCode like '%' + @cleanKeyword + '%'
              or wi.itemTitle like '%' + @cleanKeyword + '%'
              or wi.typeName like '%' + @cleanKeyword + '%'
              or wi.workItemKindText like N'%' + @cleanKeyword + N'%'
          )
        order by wi.pk desc;

        -- Result Set 5: 員工工項賦予明細 (一般員工僅可看到自己的賦予明細)
        select
            wia.pk as assignmentPk,
            wia.workItemPk,
            wi.itemCode,
            wi.itemTitle,
            wi.storyPoint as workItemStoryPoint,
            wi.executionStatus,
            wia.employeePk,
            e.employeeCode,
            e.employeeName,
            e.departmentPk,
            d.departmentName,
            wia.assignmentRole,
            wia.allocationPercent,
            wia.allocatedStoryPoint,
            wia.originalAllocatedStoryPoint,
            wia.deductedAllocatedStoryPoint,
            wia.assignmentNote,
            wia.assignedAt,
            wia.updatedAt
        from storyPoint.tbWorkItemAssignment as wia
        inner join storyPoint.tbWorkItem as wi
            on wia.workItemPk = wi.pk
        inner join storyPoint.tbEmployee as e
            on wia.employeePk = e.pk
        inner join storyPoint.tbDepartment as d
            on e.departmentPk = d.pk
        where wi.isActive = 1
          and (@departmentPk is null or e.departmentPk = @departmentPk)
          and (@effectiveEmpPk is null or wia.employeePk = @effectiveEmpPk)
        order by wia.workItemPk desc, wia.pk asc;

        -- Result Set 6: 點數訂定與異動歷程紀錄 (一般員工僅可看到自己的工項或代表類型歷程)
        select top 100
            ph.pk as historyPk,
            ph.targetKind,
            case when ph.targetKind = 'workItemType' then N'工項代表類型' else N'實際工項' end as targetKindText,
            ph.workItemTypePk,
            wit.typeCode,
            wit.typeName,
            ph.workItemPk,
            wi.itemCode,
            wi.itemTitle,
            ph.votingSessionPk,
            vs.sessionCode,
            ph.oldStoryPoint,
            ph.newStoryPoint,
            ph.changeSource,
            case
                when ph.changeSource = 'votingFinalized' then N'多人投票拍板定案'
                when ph.changeSource = 'autoFromType' then N'代表類型自動帶入'
                when ph.changeSource = 'manualAdjust' then N'管理者手動微調'
                when ph.changeSource = 'typeSync' then N'隨代表類型同步更新'
                when ph.changeSource = 'adminTypeSetting' then N'代表類型直接設定'
                when ph.changeSource = 'autoExpireDeduct' then N'迄時到期自動扣點'
                else ph.changeSource
            end as changeSourceText,
            ph.changeReason,
            isnull(emp.employeeName, N'系統自動/管理員') as changedByEmployeeName,
            ph.changedAt
        from storyPoint.tbPointHistory as ph
        left join storyPoint.tbWorkItemType as wit
            on ph.workItemTypePk = wit.pk
        left join storyPoint.tbWorkItem as wi
            on ph.workItemPk = wi.pk
        left join storyPoint.tbVotingSession as vs
            on ph.votingSessionPk = vs.pk
        left join storyPoint.tbEmployee as emp
            on ph.changedByEmployeePk = emp.pk
        where @isViewerAdmin = 1
           or ph.targetKind = 'workItemType'
           or exists (
               select 1
               from storyPoint.tbWorkItemAssignment as subWia
               where subWia.workItemPk = ph.workItemPk
                 and subWia.employeePk = @viewerEmployeePk
           )
        order by ph.pk desc;

        -- Result Set 7: 撲克牌組設定清單 (供設定盲投回合選用)
        select
            cd.pk as cardDeckPk,
            cd.deckCode,
            cd.deckName,
            cd.cardValueCsv,
            cd.description,
            cd.isDefault,
            cd.isActive
        from storyPoint.tbCardDeck as cd
        where cd.isActive = 1
        order by cd.isDefault desc, cd.pk asc;

        -- Result Set 8: 系統與錯誤執行紀錄清單 (供管理者於後台查詢錯誤與 SP 執行 Log)
        select top 250
            lg.pk as logPk,
            lg.logTime,
            lg.logLevel,
            case
                when lg.logLevel = 1 then N'1-錯誤 (Error)'
                when lg.logLevel = 2 then N'2-警告 (Warning)'
                when lg.logLevel = 3 then N'3-資訊 (Info)'
                when lg.logLevel = 4 then N'4-除錯 (Debug)'
                else N'0-未分類'
            end as logLevelText,
            lg.sourceName,
            lg.objectName,
            lg.durationMillisecond,
            lg.executionResult,
            lg.errorCode,
            lg.errorLine,
            isnull(lg.errorMessage, N'') as errorMessage,
            lg.operatorAccount
        from storyPoint.tbLog as lg
        where @isViewerAdmin = 1
        order by lg.pk desc;

        -- Result Set 9: 工項類型設定清單 (支援增刪修：專案 / 維運 / 顧問時數 / 自訂類型)
        select
            wik.pk as workItemKindPk,
            wik.kindCode,
            wik.kindName,
            wik.badgeIcon,
            wik.badgeColor,
            wik.sortOrder,
            wik.description,
            (
                select count(1)
                from storyPoint.tbWorkItem as wi
                where wi.workItemKind = wik.kindCode
                  and wi.isActive = 1
            ) as linkedWorkItemCount,
            (
                select count(1)
                from storyPoint.tbWorkItemType as wit
                where wit.defaultWorkItemKind = wik.kindCode
                  and wit.isActive = 1
            ) as linkedTypeCount,
            wik.isActive,
            wik.updatedAt
        from storyPoint.tbWorkItemKind as wik
        where wik.isActive = 1
        order by wik.sortOrder asc, wik.pk asc;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spGetStoryPointDashboard',
            @durationMillisecond = @durationMs,
            @executionResult = N'查詢 Story Point 儀表板報表失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = 'system';
        throw;
    end catch
end
go

-- ============================================================================
-- 14. 管理者查詢系統與錯誤 Log 預存程序 (storyPoint.spGetSystemLog)
-- ============================================================================
/*
用途說明: 供管理者查詢 storyPoint.tbLog 系統執行與錯誤紀錄，支援依層級、來源模組、關鍵字與筆數篩選
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @logLevel        tinyint       : 篩選 Log 層級 (null=全部, 1=錯誤, 2=警告, 3=資訊, 4=除錯)
    @sourceName      varchar(128)  : 篩選來源模組 (null=全部, 如 tsql, webPortal, powershell)
    @keyword         nvarchar(128) : 關鍵字搜尋 (比對 objectName, executionResult, errorMessage, operatorAccount)
    @topCount        int           : 回傳最大筆數 (預設 300)
    @operatorAccount varchar(128)  : 查詢操作人員帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spGetSystemLog
    @logLevel tinyint = null,
    @sourceName varchar(128) = null,
    @keyword nvarchar(128) = null,
    @topCount int = 300,
    @operatorAccount varchar(128) = 'admin'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;

    begin try
        declare @cleanSource varchar(128) = nullif(ltrim(rtrim(@sourceName)), '');
        declare @cleanKeyword nvarchar(128) = nullif(ltrim(rtrim(@keyword)), N'');
        declare @safeTop int = case when isnull(@topCount, 300) <= 0 then 300 when @topCount > 2000 then 2000 else @topCount end;

        select top (@safeTop)
            lg.pk as logPk,
            lg.logTime,
            lg.logLevel,
            case
                when lg.logLevel = 1 then N'1-錯誤 (Error)'
                when lg.logLevel = 2 then N'2-警告 (Warning)'
                when lg.logLevel = 3 then N'3-資訊 (Info)'
                when lg.logLevel = 4 then N'4-除錯 (Debug)'
                else N'0-未分類'
            end as logLevelText,
            lg.sourceName,
            lg.objectName,
            lg.durationMillisecond,
            lg.executionResult,
            lg.errorCode,
            lg.errorLine,
            isnull(lg.errorMessage, N'') as errorMessage,
            lg.operatorAccount
        from storyPoint.tbLog as lg
        where (@logLevel is null or lg.logLevel = @logLevel)
          and (@cleanSource is null or lg.sourceName = @cleanSource)
          and (
              @cleanKeyword is null
              or lg.objectName like '%' + @cleanKeyword + '%'
              or lg.executionResult like N'%' + @cleanKeyword + N'%'
              or isnull(lg.errorMessage, N'') like N'%' + @cleanKeyword + N'%'
              or lg.operatorAccount like '%' + @cleanKeyword + '%'
          )
        order by lg.pk desc;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spGetSystemLog',
            @durationMillisecond = @durationMs,
            @executionResult = N'查詢系統 Log 失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 15. 自動檢核達到迄時之工項、轉換狀態並扣除 Story Point 預存程序 (storyPoint.spProcessExpiredWorkItem)
-- ============================================================================
/*
用途說明: 檢核所有啟用中且已設定迄時 (endDate <= 台灣時間 UTC+8) 尚未執行到期處理之工項 (tbWorkItem)：
          1. 自動轉換該工項狀態 (executionStatus -> 'done' 已完成)。
          2. 自動扣掉該工項之 Story Point (storyPoint -> 0.00，並紀錄 originalStoryPoint 與 deductedStoryPoint)。
          3. 同步扣掉該工項賦予員工之分攤點數 (allocatedStoryPoint -> 0.00，並紀錄 originalAllocatedStoryPoint 與 deductedAllocatedStoryPoint)。
          4. 寫入點數異動歷程 (tbPointHistory, changeSource = 'autoExpireDeduct') 與系統紀錄 (tbLog)。
          5. 回傳本次到期或待寄信之工項明細，以及對應之「部門管理層 (部門經理/主管)」與「HR」收件人 Email 清單供寄發通知信。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemPk      int          : 指定檢核單一工項主鍵 (null 代表檢核全系統所有到期工項)
    @operatorAccount varchar(128) : 觸發檢核之操作者或系統排程帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spProcessExpiredWorkItem
    @workItemPk int = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @nowTw datetime2(3) = dateadd(hour, 8, sysutcdatetime());
    declare @processedCount int = 0;

    declare @expiredItem table (
        workItemPk int primary key,
        itemCode varchar(64),
        itemTitle nvarchar(256),
        workItemKind varchar(64),
        workItemKindText nvarchar(128),
        workItemTypePk int null,
        startDate datetime2(0) null,
        endDate datetime2(0) null,
        oldExecutionStatus varchar(32),
        originalPoint decimal(8,2),
        deductedPoint decimal(8,2)
    );

    begin try
        insert into @expiredItem (
            workItemPk, itemCode, itemTitle, workItemKind, workItemKindText,
            workItemTypePk, startDate, endDate, oldExecutionStatus,
            originalPoint, deductedPoint
        )
        select
            wi.pk,
            wi.itemCode,
            wi.itemTitle,
            wi.workItemKind,
            isnull(
                wik.kindName,
                case
                    when wi.workItemKind = 'maintenance' then N'維運'
                    when wi.workItemKind = 'consultingHour' then N'顧問時數'
                    when wi.workItemKind = 'project' then N'專案'
                    else cast(wi.workItemKind as nvarchar(128))
                end
            ),
            wi.workItemTypePk,
            wi.startDate,
            wi.endDate,
            wi.executionStatus,
            case
                when wi.storyPoint > 0 then wi.storyPoint
                else isnull(wi.originalStoryPoint, isnull(wi.defaultTypePoint, 0.00))
            end,
            case
                when wi.storyPoint > 0 then wi.storyPoint
                else isnull(wi.deductedStoryPoint, isnull(wi.originalStoryPoint, 0.00))
            end
        from storyPoint.tbWorkItem as wi
        left join storyPoint.tbWorkItemKind as wik
            on wi.workItemKind = wik.kindCode
        where wi.isActive = 1
          and (@workItemPk is null or wi.pk = @workItemPk)
          and wi.endDate is not null
          and wi.endDate <= @nowTw
          and wi.isExpiredProcessed = 0;

        select @processedCount = count(1)
        from @expiredItem;

        if @processedCount > 0
        begin
            -- 1. 扣除員工分攤之 Story Point 並保留原始點數與扣除點數紀錄
            update wia
            set wia.originalAllocatedStoryPoint = case
                    when wia.allocatedStoryPoint > 0 then wia.allocatedStoryPoint
                    else isnull(wia.originalAllocatedStoryPoint, wia.allocatedStoryPoint)
                end,
                wia.deductedAllocatedStoryPoint = case
                    when wia.allocatedStoryPoint > 0 then wia.allocatedStoryPoint
                    else isnull(wia.deductedAllocatedStoryPoint, isnull(wia.originalAllocatedStoryPoint, 0.00))
                end,
                wia.allocatedStoryPoint = 0.00,
                wia.updatedAt = sysdatetime()
            from storyPoint.tbWorkItemAssignment as wia
            inner join @expiredItem as ex
                on wia.workItemPk = ex.workItemPk;

            -- 2. 自動轉換工項狀態為 'done' (已完成) 並扣除工項 Story Point 為 0.00
            update wi
            set wi.originalStoryPoint = ex.originalPoint,
                wi.deductedStoryPoint = ex.deductedPoint,
                wi.storyPoint = 0.00,
                wi.executionStatus = 'done',
                wi.isExpiredProcessed = 1,
                wi.expiredProcessedAt = @nowTw,
                wi.adjustmentReason = concat(
                    case
                        when isnull(ltrim(rtrim(wi.adjustmentReason)), N'') = N'' then N''
                        else concat(ltrim(rtrim(wi.adjustmentReason)), N'；')
                    end,
                    N'[迄時自動轉態扣點] 達迄時 (',
                    convert(varchar(19), wi.endDate, 120),
                    N') 自動轉為已完成並扣除 ',
                    cast(ex.deductedPoint as varchar(32)),
                    N' pt'
                ),
                wi.updatedAt = sysdatetime()
            from storyPoint.tbWorkItem as wi
            inner join @expiredItem as ex
                on wi.pk = ex.workItemPk;

            -- 3. 寫入點數異動歷程表 (tbPointHistory)
            insert into storyPoint.tbPointHistory (
                targetKind, workItemTypePk, workItemPk, votingSessionPk,
                oldStoryPoint, newStoryPoint, changeSource, changeReason,
                changedByEmployeePk, changedAt
            )
            select
                'workItem',
                ex.workItemTypePk,
                ex.workItemPk,
                null,
                ex.originalPoint,
                0.00,
                'autoExpireDeduct',
                concat(
                    N'工項【',
                    ex.itemCode,
                    N' ',
                    ex.itemTitle,
                    N'】(',
                    ex.workItemKindText,
                    N') 達到迄時 (',
                    convert(varchar(19), ex.endDate, 120),
                    N')，自動轉換狀態為已完成並扣掉 ',
                    cast(ex.deductedPoint as varchar(32)),
                    N' Story Point'
                ),
                null,
                sysdatetime()
            from @expiredItem as ex;

            set @durationMs = datediff(millisecond, @startTime, sysdatetime());
            declare @summaryMsg nvarchar(256) = concat(N'自動處理達到迄時工項共 ', cast(@processedCount as varchar(16)), N' 筆：已轉換狀態為已完成並扣除 Story Point');
            exec storyPoint.spWriteLog
                @logLevel = 3,
                @sourceName = 'tsql',
                @objectName = 'storyPoint.spProcessExpiredWorkItem',
                @durationMillisecond = @durationMs,
                @executionResult = @summaryMsg,
                @operatorAccount = @operatorAccount;
        end

        -- 4. 透過樹狀遞迴階層 CTE (empManagerHierarchyCte) 解析每位負責同仁之「部門主管」以及「人資主管 (HR 部門主管)」
        ;with empManagerHierarchyCte as (
            -- 錨點成員 (Anchor Member): 從每位在職員工自身出發
            select
                baseEmp.pk as startEmployeePk,
                baseEmp.departmentPk as startDepartmentPk,
                baseEmp.isDepartmentManager as startIsDeptMgr,
                baseEmp.pk as currentEmpPk,
                baseEmp.managerEmployeePk as nextManagerPk,
                baseEmp.employeeName as currentEmpName,
                ltrim(rtrim(baseEmp.emailAddress)) as currentEmpEmail,
                baseEmp.jobTitle as currentEmpJobTitle,
                baseEmp.departmentPk as currentDeptPk,
                baseEmp.isDepartmentManager as currentIsDeptMgr,
                baseEmp.isActive as currentIsActive,
                0 as hopDistance,
                cast(concat('/', cast(baseEmp.pk as varchar(16)), '/') as varchar(512)) as visitedPath
            from storyPoint.tbEmployee as baseEmp

            union all

            -- 遞迴成員 (Recursive Member): 沿 managerEmployeePk 向上追溯直屬主管與部門主管樹狀階層
            select
                cte.startEmployeePk,
                cte.startDepartmentPk,
                cte.startIsDeptMgr,
                mgr.pk as currentEmpPk,
                mgr.managerEmployeePk as nextManagerPk,
                mgr.employeeName as currentEmpName,
                ltrim(rtrim(mgr.emailAddress)) as currentEmpEmail,
                mgr.jobTitle as currentEmpJobTitle,
                mgr.departmentPk as currentDeptPk,
                mgr.isDepartmentManager as currentIsDeptMgr,
                mgr.isActive as currentIsActive,
                cte.hopDistance + 1 as hopDistance,
                cast(concat(cte.visitedPath, cast(mgr.pk as varchar(16)), '/') as varchar(512)) as visitedPath
            from empManagerHierarchyCte as cte
            inner join storyPoint.tbEmployee as mgr
                on cte.nextManagerPk = mgr.pk
            where cte.nextManagerPk is not null
              and cte.nextManagerPk <> cte.currentEmpPk
              and charindex('/' + cast(mgr.pk as varchar(16)) + '/', cte.visitedPath) = 0
              and cte.hopDistance < 15
        )
        select
            v.pk as workItemPk,
            v.itemCode,
            v.itemTitle,
            v.itemDescription,
            v.workItemTypePk,
            v.typeCode,
            v.typeName,
            v.workItemKind,
            v.workItemKindText,
            v.sprintName,
            v.priorityLevel,
            v.startDate,
            v.endDate,
            v.dueDate,
            isnull(ex.oldExecutionStatus, 'inProgress') as oldExecutionStatus,
            v.executionStatus as newExecutionStatus,
            N'已完成' as newExecutionStatusText,
            isnull(v.originalStoryPoint, v.deductedStoryPoint) as originalStoryPoint,
            v.deductedStoryPoint,
            v.storyPoint as remainingStoryPoint,
            v.assigneeSummary,
            v.departmentSummary,
            v.isExpiredProcessed,
            v.expiredProcessedAt,
            v.expiryNotificationSent,
            isnull(mgrRecipient.managerEmailCsv, '') as departmentManagerEmailCsv,
            isnull(mgrRecipient.managerNameCsv, N'該同仁部門主管') as departmentManagerNameCsv,
            isnull(hrRecipient.hrEmailCsv, '') as hrEmailCsv,
            isnull(hrRecipient.hrNameCsv, N'人資主管') as hrNameCsv,
            isnull(allRecipient.allEmailCsv, '') as allRecipientEmailCsv
        from storyPoint.vwWorkItemDetail as v
        left join @expiredItem as ex
            on v.pk = ex.workItemPk
        outer apply (
            -- 針對該工項每位負責同仁，由樹狀遞迴階層向上定位並納入同部門所有「部門主管 (isDepartmentManager = 1)」
            -- 若同一個部門有多位主管，則多位部門主管全部列入通知收件人
            select
                string_agg(m.emailAddress, ',') within group (order by m.employeePk asc) as managerEmailCsv,
                string_agg(concat(m.employeeName, N'(', m.departmentName, N' ', m.jobTitle, N')'), N'、') within group (order by m.employeePk asc) as managerNameCsv
            from (
                select distinct
                    resolvedMgr.employeePk,
                    resolvedMgr.emailAddress,
                    resolvedMgr.employeeName,
                    resolvedMgr.jobTitle,
                    resolvedMgr.departmentName
                from storyPoint.tbWorkItemAssignment as wia
                cross apply (
                    select distinct
                        cand.employeePk,
                        cand.emailAddress,
                        cand.employeeName,
                        cand.jobTitle,
                        cand.departmentName
                    from (
                        -- 1. 由樹狀遞迴階層 (empManagerHierarchyCte) 向上尋找同部門標記為部門主管 (isDepartmentManager = 1) 之主管；若該部門無主管則向上取上層部門主管
                        select
                            h.currentEmpPk as employeePk,
                            h.currentEmpEmail as emailAddress,
                            h.currentEmpName as employeeName,
                            h.currentEmpJobTitle as jobTitle,
                            d.departmentName
                        from empManagerHierarchyCte as h
                        inner join storyPoint.tbDepartment as d
                            on h.currentDeptPk = d.pk
                        where h.startEmployeePk = wia.employeePk
                          and h.currentIsActive = 1
                          and isnull(h.currentEmpEmail, '') <> ''
                          and h.currentIsDeptMgr = 1
                          and (
                              h.currentDeptPk = h.startDepartmentPk
                              or not exists (
                                  select 1
                                  from storyPoint.tbEmployee as anySameDeptMgr
                                  where anySameDeptMgr.departmentPk = h.startDepartmentPk
                                    and anySameDeptMgr.isDepartmentManager = 1
                                    and anySameDeptMgr.isActive = 1
                                    and isnull(ltrim(rtrim(anySameDeptMgr.emailAddress)), '') <> ''
                              )
                          )
                          and (h.hopDistance >= 1 or h.startIsDeptMgr = 1)

                        union all

                        -- 2. 若該同仁所屬部門有多位主管 (isDepartmentManager = 1)，將該部門所有啟用中之部門主管一併納入通知
                        select
                            deptMgr.pk as employeePk,
                            ltrim(rtrim(deptMgr.emailAddress)) as emailAddress,
                            deptMgr.employeeName,
                            deptMgr.jobTitle,
                            dept.departmentName
                        from storyPoint.tbEmployee as assignEmp
                        inner join storyPoint.tbEmployee as deptMgr
                            on assignEmp.departmentPk = deptMgr.departmentPk
                        inner join storyPoint.tbDepartment as dept
                            on deptMgr.departmentPk = dept.pk
                        where assignEmp.pk = wia.employeePk
                          and deptMgr.isActive = 1
                          and deptMgr.isDepartmentManager = 1
                          and isnull(ltrim(rtrim(deptMgr.emailAddress)), '') <> ''
                    ) as cand
                ) as resolvedMgr
                where wia.workItemPk = v.pk

                union

                -- 若該工項尚未指派任何同仁，則取建立者之部門主管或頂層部門主管
                select distinct
                    topMgr.pk as employeePk,
                    ltrim(rtrim(topMgr.emailAddress)) as emailAddress,
                    topMgr.employeeName,
                    topMgr.jobTitle,
                    topDept.departmentName
                from storyPoint.tbEmployee as topMgr
                inner join storyPoint.tbDepartment as topDept
                    on topMgr.departmentPk = topDept.pk
                where not exists (select 1 from storyPoint.tbWorkItemAssignment as wiaEmpty where wiaEmpty.workItemPk = v.pk)
                  and topMgr.isActive = 1
                  and topMgr.isDepartmentManager = 1
                  and topMgr.managerEmployeePk is null
                  and isnull(ltrim(rtrim(topMgr.emailAddress)), '') <> ''
            ) as m
        ) as mgrRecipient
        outer apply (
            -- 解析「人資主管」：僅選取 HR 人資部門且員工資料記載為部門主管 (isDepartmentManager = 1) 之主管
            select
                string_agg(h.emailAddress, ',') within group (order by h.employeePk asc) as hrEmailCsv,
                string_agg(concat(h.employeeName, N'(', h.departmentName, N' ', h.jobTitle, N')'), N'、') within group (order by h.employeePk asc) as hrNameCsv
            from (
                select distinct
                    e.pk as employeePk,
                    ltrim(rtrim(e.emailAddress)) as emailAddress,
                    e.employeeName,
                    e.jobTitle,
                    d.departmentName
                from storyPoint.tbEmployee as e
                inner join storyPoint.tbDepartment as d
                    on e.departmentPk = d.pk
                where e.isActive = 1
                  and isnull(ltrim(rtrim(e.emailAddress)), '') <> ''
                  and e.isDepartmentManager = 1
                  and (
                      d.departmentCode = 'HR'
                      or d.departmentName = N'HR'
                      or e.jobTitle like N'%人資%'
                      or e.jobTitle like N'%HR%'
                  )
            ) as h
        ) as hrRecipient
        outer apply (
            select
                string_agg(u.emailAddress, ',') within group (order by u.emailAddress asc) as allEmailCsv
            from (
                select distinct ltrim(rtrim(s.value)) as emailAddress
                from string_split(
                    concat(isnull(mgrRecipient.managerEmailCsv, ''), ',', isnull(hrRecipient.hrEmailCsv, '')),
                    ','
                ) as s
                where ltrim(rtrim(s.value)) <> ''
            ) as u
        ) as allRecipient
        where v.isActive = 1
          and (
              ex.workItemPk is not null
              or (
                  v.isExpiredProcessed = 1
                  and v.expiryNotificationSent = 0
                  and (@workItemPk is null or v.pk = @workItemPk)
              )
          )
        order by v.pk desc;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spProcessExpiredWorkItem',
            @durationMillisecond = @durationMs,
            @executionResult = N'處理到期工項自動轉態與扣點失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 16. 標記到期工項已寄送部門管理層與 HR Email 通知預存程序 (storyPoint.spMarkWorkItemExpiryNotified)
-- ============================================================================
/*
用途說明: 當系統完成寄發到期工項通知信給部門管理層與 HR 後，更新工項之 expiryNotificationSent 狀態並寫入 tbLog
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemPk      int            : 工項主鍵
    @recipientCsv    nvarchar(1024) : 實際寄送之收件人清單
    @isSuccess       bit            : 是否寄送成功 (1=成功, 0=失敗)
    @errorMessage    nvarchar(max)  : 若失敗時之錯誤原因
    @operatorAccount varchar(128)   : 操作者或系統帳號
修改歷程:
    2026-10-23 初版建立
*/
create or alter procedure storyPoint.spMarkWorkItemExpiryNotified
    @workItemPk int,
    @recipientCsv nvarchar(1024),
    @isSuccess bit = 1,
    @errorMessage nvarchar(max) = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @nowTw datetime2(3) = dateadd(hour, 8, sysutcdatetime());
    declare @itemCode varchar(64) = '';
    declare @itemTitle nvarchar(256) = N'';

    begin try
        select
            @itemCode = itemCode,
            @itemTitle = itemTitle
        from storyPoint.tbWorkItem
        where pk = @workItemPk;

        update storyPoint.tbWorkItem
        set expiryNotificationSent = isnull(@isSuccess, 1),
            expiryNotificationSentAt = case when isnull(@isSuccess, 1) = 1 then @nowTw else expiryNotificationSentAt end,
            expiryNotificationRecipient = @recipientCsv,
            updatedAt = sysdatetime()
        where pk = @workItemPk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @logLevel tinyint = case when isnull(@isSuccess, 1) = 1 then 3 else 2 end;
        declare @logResult nvarchar(256) = case
            when isnull(@isSuccess, 1) = 1
            then concat(N'工項【', @itemCode, N' ', @itemTitle, N'】迄時到期扣點通知信已寄送予部門管理層與 HR: ', left(isnull(@recipientCsv, N''), 140))
            else concat(N'工項【', @itemCode, N' ', @itemTitle, N'】迄時到期扣點通知信寄送失敗')
        end;

        exec storyPoint.spWriteLog
            @logLevel = @logLevel,
            @sourceName = 'emailService',
            @objectName = 'storyPoint.spMarkWorkItemExpiryNotified',
            @durationMillisecond = @durationMs,
            @executionResult = @logResult,
            @errorMessage = @errorMessage,
            @operatorAccount = @operatorAccount;

        select
            wi.pk as workItemPk,
            wi.itemCode,
            wi.expiryNotificationSent,
            wi.expiryNotificationSentAt,
            wi.expiryNotificationRecipient
        from storyPoint.tbWorkItem as wi
        where wi.pk = @workItemPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'emailService',
            @objectName = 'storyPoint.spMarkWorkItemExpiryNotified',
            @durationMillisecond = @durationMs,
            @executionResult = N'更新到期工項寄信狀態失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 17. 新增或修改工項類型預存程序 (storyPoint.spUpsertWorkItemKind)
-- ============================================================================
/*
用途說明: 新增或更新工項類型 (tbWorkItemKind，如：專案 project、維運 maintenance、顧問時數 consultingHour 或自訂工項類型)。
          若修改既有類型之代碼 (kindCode)，自動同步更新關聯之工項代表類型 (tbWorkItemType.defaultWorkItemKind) 與實際工項 (tbWorkItem.workItemKind)。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemKindPk  int           : 工項類型主鍵 (0 或 null 代表依 kindCode 新增或更新)
    @kindCode        varchar(64)   : 工項類型代碼 (如 project, maintenance, consultingHour, supportCase)
    @kindName        nvarchar(128) : 工項類型顯示名稱 (如 專案, 維運, 顧問時數)
    @badgeIcon       nvarchar(32)  : 徽章圖示 (如 🏗️, 🛠️, ⏳, 📌)
    @badgeColor      varchar(32)   : 徽章顏色 ('blue', 'amber', 'purple', 'green', 'red')
    @sortOrder       int           : 顯示排序
    @description     nvarchar(512) : 類型說明與適用情境
    @isActive        bit           : 是否啟用
    @operatorAccount varchar(128)  : 操作者帳號
修改歷程:
    2026-10-23 初版建立，支援工項類型增、修
*/
create or alter procedure storyPoint.spUpsertWorkItemKind
    @workItemKindPk int = null,
    @kindCode varchar(64),
    @kindName nvarchar(128),
    @badgeIcon nvarchar(32) = N'📌',
    @badgeColor varchar(32) = 'blue',
    @sortOrder int = 100,
    @description nvarchar(512) = null,
    @isActive bit = 1,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@workItemKindPk, 0);
    declare @cleanCode varchar(64) = ltrim(rtrim(isnull(@kindCode, '')));
    declare @cleanName nvarchar(128) = ltrim(rtrim(isnull(@kindName, N'')));
    declare @cleanIcon nvarchar(32) = isnull(nullif(ltrim(rtrim(@badgeIcon)), N''), N'📌');
    declare @cleanColor varchar(32) = case
        when @badgeColor in ('blue', 'amber', 'purple', 'green', 'red') then @badgeColor
        else 'blue'
    end;
    declare @oldCode varchar(64) = null;

    begin try
        if @cleanCode = '' or @cleanName = N''
        begin
            ;throw 50031, N'工項類型代碼與名稱不可為空白。', 1;
        end

        if @targetPk <= 0
        begin
            select top 1 @targetPk = pk
            from storyPoint.tbWorkItemKind
            where kindCode = @cleanCode;
        end

        if isnull(@targetPk, 0) > 0 and exists (select 1 from storyPoint.tbWorkItemKind where pk = @targetPk)
        begin
            select @oldCode = kindCode
            from storyPoint.tbWorkItemKind
            where pk = @targetPk;

            if exists (select 1 from storyPoint.tbWorkItemKind where kindCode = @cleanCode and pk <> @targetPk)
            begin
                ;throw 50032, N'工項類型代碼已存在於其他類型，請使用不同的類型代碼。', 1;
            end

            update storyPoint.tbWorkItemKind
            set kindCode = @cleanCode,
                kindName = @cleanName,
                badgeIcon = @cleanIcon,
                badgeColor = @cleanColor,
                sortOrder = isnull(@sortOrder, 100),
                description = @description,
                isActive = isnull(@isActive, 1),
                updatedAt = sysdatetime()
            where pk = @targetPk;

            if @oldCode is not null and @oldCode <> @cleanCode
            begin
                update storyPoint.tbWorkItemType
                set defaultWorkItemKind = @cleanCode,
                    updatedAt = sysdatetime()
                where defaultWorkItemKind = @oldCode;

                update storyPoint.tbWorkItem
                set workItemKind = @cleanCode,
                    updatedAt = sysdatetime()
                where workItemKind = @oldCode;
            end
        end
        else
        begin
            insert into storyPoint.tbWorkItemKind (
                kindCode, kindName, badgeIcon, badgeColor, sortOrder,
                description, isActive, createdAt, updatedAt
            )
            values (
                @cleanCode,
                @cleanName,
                @cleanIcon,
                @cleanColor,
                isnull(@sortOrder, 100),
                @description,
                isnull(@isActive, 1),
                sysdatetime(),
                sysdatetime()
            );
            set @targetPk = scope_identity();
        end

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertWorkItemKind',
            @durationMillisecond = @durationMs,
            @executionResult = N'工項類型儲存成功',
            @operatorAccount = @operatorAccount;

        select
            wik.pk as workItemKindPk,
            wik.kindCode,
            wik.kindName,
            wik.badgeIcon,
            wik.badgeColor,
            wik.sortOrder,
            wik.description,
            wik.isActive,
            wik.updatedAt
        from storyPoint.tbWorkItemKind as wik
        where wik.pk = @targetPk;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spUpsertWorkItemKind',
            @durationMillisecond = @durationMs,
            @executionResult = N'工項類型儲存失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 18. 刪除工項類型預存程序 (storyPoint.spDeleteWorkItemKind)
-- ============================================================================
/*
用途說明: 刪除指定之工項類型 (tbWorkItemKind)，若有既有工項或代表類型引用該類型，則安全轉移至備援類型 (@fallbackKindCode，預設 'project' 或第一筆啟用類型) 後實體刪除該工項類型。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemKindPk   int          : 欲刪除之工項類型主鍵 (若傳 0 或 null 可依 @kindCode 定位)
    @kindCode         varchar(64)  : 欲刪除之工項類型代碼
    @fallbackKindCode varchar(64)  : 若有關聯工項時轉移之目標類型代碼 (預設 'project')
    @operatorAccount  varchar(128) : 操作者帳號
修改歷程:
    2026-10-23 初版建立，支援工項類型刪除
*/
create or alter procedure storyPoint.spDeleteWorkItemKind
    @workItemKindPk int = null,
    @kindCode varchar(64) = null,
    @fallbackKindCode varchar(64) = 'project',
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@workItemKindPk, 0);
    declare @targetCode varchar(64) = null;
    declare @targetName nvarchar(128) = null;
    declare @safeFallback varchar(64) = isnull(nullif(ltrim(rtrim(@fallbackKindCode)), ''), 'project');

    begin try
        if @targetPk <= 0 and isnull(ltrim(rtrim(@kindCode)), '') <> ''
        begin
            select top 1 @targetPk = pk
            from storyPoint.tbWorkItemKind
            where kindCode = ltrim(rtrim(@kindCode));
        end

        select
            @targetCode = kindCode,
            @targetName = kindName
        from storyPoint.tbWorkItemKind
        where pk = @targetPk;

        if @targetCode is null
        begin
            ;throw 50033, N'找不到欲刪除的工項類型紀錄。', 1;
        end

        if @safeFallback = @targetCode or not exists (select 1 from storyPoint.tbWorkItemKind where kindCode = @safeFallback and pk <> @targetPk and isActive = 1)
        begin
            select top 1 @safeFallback = kindCode
            from storyPoint.tbWorkItemKind
            where pk <> @targetPk
              and isActive = 1
            order by sortOrder asc, pk asc;

            set @safeFallback = isnull(@safeFallback, 'project');
        end

        update storyPoint.tbWorkItemType
        set defaultWorkItemKind = @safeFallback,
            updatedAt = sysdatetime()
        where defaultWorkItemKind = @targetCode;

        update storyPoint.tbWorkItem
        set workItemKind = @safeFallback,
            updatedAt = sysdatetime()
        where workItemKind = @targetCode;

        delete from storyPoint.tbWorkItemKind
        where pk = @targetPk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @delMsg nvarchar(256) = concat(N'工項類型【', @targetCode, N' - ', @targetName, N'】已刪除');
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteWorkItemKind',
            @durationMillisecond = @durationMs,
            @executionResult = @delMsg,
            @operatorAccount = @operatorAccount;

        select
            @targetPk as deletedWorkItemKindPk,
            @targetCode as deletedKindCode,
            @targetName as deletedKindName,
            @safeFallback as reassignedToKindCode;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteWorkItemKind',
            @durationMillisecond = @durationMs,
            @executionResult = N'刪除工項類型失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 19. 刪除工項代表類型預存程序 (storyPoint.spDeleteWorkItemType)
-- ============================================================================
/*
用途說明: 刪除指定之工項代表類型 (tbWorkItemType)，解除關聯工項之 workItemTypePk 外鍵 (保留工項既有 Story Point)，並清除關聯後刪除該代表類型。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemTypePk  int          : 欲刪除之工項代表類型主鍵 (若傳 0 或 null 可依 @typeCode 定位)
    @typeCode        varchar(64)  : 欲刪除之工項代表類型代碼
    @operatorAccount varchar(128) : 操作者帳號
修改歷程:
    2026-10-23 初版建立，支援工項代表類型刪除
*/
create or alter procedure storyPoint.spDeleteWorkItemType
    @workItemTypePk int = null,
    @typeCode varchar(64) = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@workItemTypePk, 0);
    declare @targetCode varchar(64) = null;
    declare @targetName nvarchar(128) = null;

    begin try
        if @targetPk <= 0 and isnull(ltrim(rtrim(@typeCode)), '') <> ''
        begin
            select top 1 @targetPk = pk
            from storyPoint.tbWorkItemType
            where typeCode = ltrim(rtrim(@typeCode));
        end

        select
            @targetCode = typeCode,
            @targetName = typeName
        from storyPoint.tbWorkItemType
        where pk = @targetPk;

        if @targetCode is null
        begin
            ;throw 50034, N'找不到欲刪除的工項代表類型紀錄。', 1;
        end

        update storyPoint.tbWorkItem
        set workItemTypePk = null,
            updatedAt = sysdatetime()
        where workItemTypePk = @targetPk;

        update storyPoint.tbPointHistory
        set workItemTypePk = null
        where workItemTypePk = @targetPk;

        update storyPoint.tbVotingSession
        set workItemTypePk = null,
            updatedAt = sysdatetime()
        where workItemTypePk = @targetPk;

        delete from storyPoint.tbWorkItemType
        where pk = @targetPk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @delTypeMsg nvarchar(256) = concat(N'工項代表類型【', @targetCode, N' - ', @targetName, N'】已刪除');
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteWorkItemType',
            @durationMillisecond = @durationMs,
            @executionResult = @delTypeMsg,
            @operatorAccount = @operatorAccount;

        select
            @targetPk as deletedWorkItemTypePk,
            @targetCode as deletedTypeCode,
            @targetName as deletedTypeName;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteWorkItemType',
            @durationMillisecond = @durationMs,
            @executionResult = N'刪除工項代表類型失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go

-- ============================================================================
-- 20. 刪除實際工項預存程序 (storyPoint.spDeleteWorkItem)
-- ============================================================================
/*
用途說明: 刪除指定之實際工項 (tbWorkItem)，一併清除其員工指派明細 (tbWorkItemAssignment) 與點數異動外鍵，確保員工與部門點數即時扣除更新。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
參數說明:
    @workItemPk      int          : 欲刪除之工項主鍵 (若傳 0 或 null 可依 @itemCode 定位)
    @itemCode        varchar(64)  : 欲刪除之工項代碼
    @operatorAccount varchar(128) : 操作者帳號
修改歷程:
    2026-10-23 初版建立，支援工項刪除
*/
create or alter procedure storyPoint.spDeleteWorkItem
    @workItemPk int = null,
    @itemCode varchar(64) = null,
    @operatorAccount varchar(128) = 'system'
as
begin
    set nocount on;
    declare @startTime datetime2(3) = sysdatetime();
    declare @durationMs int = 0;
    declare @targetPk int = isnull(@workItemPk, 0);
    declare @targetCode varchar(64) = null;
    declare @targetTitle nvarchar(256) = null;

    begin try
        if @targetPk <= 0 and isnull(ltrim(rtrim(@itemCode)), '') <> ''
        begin
            select top 1 @targetPk = pk
            from storyPoint.tbWorkItem
            where itemCode = ltrim(rtrim(@itemCode));
        end

        select
            @targetCode = itemCode,
            @targetTitle = itemTitle
        from storyPoint.tbWorkItem
        where pk = @targetPk;

        if @targetCode is null
        begin
            ;throw 50035, N'找不到欲刪除的工項紀錄。', 1;
        end

        delete from storyPoint.tbWorkItemAssignment
        where workItemPk = @targetPk;

        update storyPoint.tbPointHistory
        set workItemPk = null
        where workItemPk = @targetPk;

        update storyPoint.tbVotingSession
        set workItemPk = null,
            updatedAt = sysdatetime()
        where workItemPk = @targetPk;

        delete from storyPoint.tbWorkItem
        where pk = @targetPk;

        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @delItemMsg nvarchar(256) = concat(N'實際工項【', @targetCode, N' - ', @targetTitle, N'】已刪除');
        exec storyPoint.spWriteLog
            @logLevel = 3,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteWorkItem',
            @durationMillisecond = @durationMs,
            @executionResult = @delItemMsg,
            @operatorAccount = @operatorAccount;

        select
            @targetPk as deletedWorkItemPk,
            @targetCode as deletedItemCode,
            @targetTitle as deletedItemTitle;
    end try
    begin catch
        set @durationMs = datediff(millisecond, @startTime, sysdatetime());
        declare @errCode int = error_number();
        declare @errLine int = error_line();
        declare @errMsg nvarchar(max) = error_message();
        exec storyPoint.spWriteLog
            @logLevel = 1,
            @sourceName = 'tsql',
            @objectName = 'storyPoint.spDeleteWorkItem',
            @durationMillisecond = @durationMs,
            @executionResult = N'刪除實際工項失敗',
            @errorCode = @errCode,
            @errorLine = @errLine,
            @errorMessage = @errMsg,
            @operatorAccount = @operatorAccount;
        throw;
    end catch
end
go
