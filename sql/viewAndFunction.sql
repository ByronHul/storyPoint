/*
================================================================================
檔案名稱: viewAndFunction.sql
用途說明: 建立 Azure 多人參與 Story Point 系統之純量函數 (fn*) 與統計檢視表 (vw*)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
修改歷程:
    2026-10-23 初版建立，提供撲克牌點數轉換函數、工項明細檢視、員工點數統計檢視、
               部門總點數統計檢視、以及投票過程明細檢視
================================================================================
*/

set nocount on;
go

-- ============================================================================
-- 1. 撲克牌字串轉換為數值點數函數 (storyPoint.fnParseCardPoint)
-- 用途: 將 '0', '0.5', '1', '2', '3', '5', '8', '13', '21' 轉為 decimal(8,2)，
--       若為 '?' 或 '☕' 等非數值牌則回傳 null，不納入平均數計算
-- ============================================================================
create or alter function storyPoint.fnParseCardPoint (
    @cardValue nvarchar(32)
)
returns decimal(8,2)
as
begin
    declare @trimmedValue nvarchar(32) = ltrim(rtrim(isnull(@cardValue, N'')));
    declare @numericResult decimal(8,2) = null;

    if @trimmedValue <> N'' and @trimmedValue not in (N'?', N'☕', N'coffee', N'pass', N'infinity')
    begin
        set @numericResult = try_cast(@trimmedValue as decimal(8,2));
    end

    return @numericResult;
end
go

-- ============================================================================
-- 2. 工項完整明細與負責員工彙總檢視表 (storyPoint.vwWorkItemDetail)
-- ============================================================================
create or alter view storyPoint.vwWorkItemDetail
as
select
    wi.pk,
    wi.itemCode,
    wi.itemTitle,
    wi.itemDescription,
    wi.workItemTypePk,
    isnull(wit.typeCode, '') as typeCode,
    isnull(wit.typeName, N'未分類(自訂工項)') as typeName,
    isnull(wit.categoryName, N'一般') as categoryName,
    wi.workItemKind,
    isnull(
        wik.kindName,
        case
            when wi.workItemKind = 'maintenance' then N'維運'
            when wi.workItemKind = 'consultingHour' then N'顧問時數'
            when wi.workItemKind = 'training' then N'教育訓練'
            when wi.workItemKind = 'productDevelopment' then N'開發產品'
            when wi.workItemKind = 'project' then N'專案'
            else cast(wi.workItemKind as nvarchar(128))
        end
    ) as workItemKindText,
    isnull(
        wik.badgeIcon,
        case
            when wi.workItemKind = 'maintenance' then N'🛠️'
            when wi.workItemKind = 'consultingHour' then N'⏳'
            when wi.workItemKind = 'training' then N'🎓'
            when wi.workItemKind = 'productDevelopment' then N'🚀'
            else N'🏗️'
        end
    ) as workItemKindIcon,
    isnull(
        wik.badgeColor,
        case
            when wi.workItemKind = 'maintenance' then 'amber'
            when wi.workItemKind = 'consultingHour' then 'purple'
            when wi.workItemKind = 'training' then 'green'
            when wi.workItemKind = 'productDevelopment' then 'red'
            else 'blue'
        end
    ) as workItemKindColor,
    wi.sprintName,
    wi.priorityLevel,
    wi.defaultTypePoint,
    wi.storyPoint,
    wi.originalStoryPoint,
    wi.deductedStoryPoint,
    wi.isManualAdjusted,
    case
        when wi.isExpiredProcessed = 1 then concat(N'迄時到期自動扣點 (原 ', cast(isnull(wi.originalStoryPoint, wi.deductedStoryPoint) as varchar(32)), N' pt → 扣 ', cast(wi.deductedStoryPoint as varchar(32)), N' pt)')
        when wi.isManualAdjusted = 1 then N'手動調整'
        when wi.workItemTypePk is not null then N'代表類型自動帶入'
        else N'獨立估算/設定'
    end as pointSourceText,
    wi.adjustmentReason,
    wi.estimationStatus,
    wi.executionStatus,
    case
        when wi.executionStatus = 'expired' or wi.isExpiredProcessed = 1 then N'已到期結案(已扣點)'
        when wi.executionStatus = 'done' then N'已完成'
        when wi.executionStatus = 'inProgress' then N'進行中'
        else N'待處理'
    end as executionStatusText,
    wi.startDate,
    wi.endDate,
    wi.dueDate,
    wi.isExpiredProcessed,
    wi.expiredProcessedAt,
    wi.expiryNotificationSent,
    wi.expiryNotificationSentAt,
    wi.expiryNotificationRecipient,
    wi.createdByEmployeePk,
    isnull(empCreator.employeeName, N'系統管理員') as createdByEmployeeName,
    isnull(assignStat.assigneeCount, 0) as assigneeCount,
    isnull(assignStat.assigneeSummary, N'尚未指派') as assigneeSummary,
    isnull(assignStat.departmentSummary, N'-') as departmentSummary,
    wi.isActive,
    wi.createdAt,
    wi.updatedAt
from storyPoint.tbWorkItem as wi
left join storyPoint.tbWorkItemType as wit
    on wi.workItemTypePk = wit.pk
left join storyPoint.tbWorkItemKind as wik
    on wi.workItemKind = wik.kindCode
left join storyPoint.tbEmployee as empCreator
    on wi.createdByEmployeePk = empCreator.pk
outer apply (
    select
        count(wia.pk) as assigneeCount,
        string_agg(
            concat(
                e.employeeName,
                N'(',
                d.departmentName,
                N': ',
                cast(wia.allocatedStoryPoint as varchar(32)),
                N' pt',
                case
                    when wia.deductedAllocatedStoryPoint > 0
                    then concat(N' [已扣 ', cast(wia.deductedAllocatedStoryPoint as varchar(32)), N' pt]')
                    else N''
                end,
                N' / ',
                cast(wia.allocationPercent as varchar(32)),
                N'%)'
            ),
            N'、'
        ) within group (order by wia.pk asc) as assigneeSummary,
        string_agg(d.departmentName, N'、') within group (order by wia.pk asc) as departmentSummary
    from storyPoint.tbWorkItemAssignment as wia
    inner join storyPoint.tbEmployee as e
        on wia.employeePk = e.pk
    inner join storyPoint.tbDepartment as d
        on e.departmentPk = d.pk
    where wia.workItemPk = wi.pk
) as assignStat;
go

-- ============================================================================
-- 3. 員工個人 Story Point 負載、樹狀遞迴階層與部門主管解析檢視表 (storyPoint.vwEmployeePointSummary)
-- 用途: 1. 查詢每位員工身上有多少工項與多少 Story Point (含分攤點數、已到期扣點與原始工項總點數)
--       2. 透過 T-SQL 遞迴 CTE (empHierarchyCte / upwardManagerCte) 展開員工組織樹狀遞迴階層 (hierarchyLevel / hierarchyPath)
--       3. 透過樹狀遞迴階層解析每位同仁對應之「部門主管 (departmentManager*)」與「人資主管 (hrManager*)」
-- ============================================================================
create or alter view storyPoint.vwEmployeePointSummary
as
with empHierarchyCte as (
    -- 錨點成員 (Anchor Member): 樹狀遞迴根節點 (無上層主管、或上層主管為自己/不存在)
    select
        e.pk as employeePk,
        e.managerEmployeePk,
        1 as hierarchyLevel,
        cast(e.employeeName as nvarchar(1024)) as hierarchyPath,
        cast(right('00000' + cast(e.pk as varchar(16)), 5) as varchar(512)) as hierarchySortKey,
        cast(concat('/', cast(e.pk as varchar(16)), '/') as varchar(512)) as visitedPkPath
    from storyPoint.tbEmployee as e
    where e.managerEmployeePk is null
       or e.managerEmployeePk = e.pk
       or not exists (select 1 from storyPoint.tbEmployee as p where p.pk = e.managerEmployeePk)

    union all

    -- 遞迴成員 (Recursive Member): 由上而下遞迴展開各階層部屬，並防範循環參照
    select
        c.pk as employeePk,
        c.managerEmployeePk,
        cte.hierarchyLevel + 1 as hierarchyLevel,
        cast(concat(cte.hierarchyPath, N' ➔ ', c.employeeName) as nvarchar(1024)) as hierarchyPath,
        cast(concat(cte.hierarchySortKey, '.', right('00000' + cast(c.pk as varchar(16)), 5)) as varchar(512)) as hierarchySortKey,
        cast(concat(cte.visitedPkPath, cast(c.pk as varchar(16)), '/') as varchar(512)) as visitedPkPath
    from storyPoint.tbEmployee as c
    inner join empHierarchyCte as cte
        on c.managerEmployeePk = cte.employeePk
    where c.managerEmployeePk <> c.pk
      and charindex('/' + cast(c.pk as varchar(16)) + '/', cte.visitedPkPath) = 0
      and cte.hierarchyLevel < 15
),
upwardManagerCte as (
    -- 錨點成員: 從每位員工自身出發向上遞迴尋找其「部門主管」
    select
        e.pk as startEmployeePk,
        e.departmentPk as startDepartmentPk,
        e.isDepartmentManager as startIsDeptMgr,
        e.pk as currentEmpPk,
        e.managerEmployeePk as nextManagerPk,
        e.employeeName as currentEmpName,
        e.emailAddress as currentEmpEmail,
        e.jobTitle as currentEmpJobTitle,
        e.departmentPk as currentDeptPk,
        e.isDepartmentManager as currentIsDeptMgr,
        e.isActive as currentIsActive,
        0 as hopDistance,
        cast(concat('/', cast(e.pk as varchar(16)), '/') as varchar(512)) as upwardVisitedPath
    from storyPoint.tbEmployee as e

    union all

    -- 遞迴成員: 沿 managerEmployeePk 向上追溯上層主管與部門主管
    select
        u.startEmployeePk,
        u.startDepartmentPk,
        u.startIsDeptMgr,
        mgr.pk as currentEmpPk,
        mgr.managerEmployeePk as nextManagerPk,
        mgr.employeeName as currentEmpName,
        mgr.emailAddress as currentEmpEmail,
        mgr.jobTitle as currentEmpJobTitle,
        mgr.departmentPk as currentDeptPk,
        mgr.isDepartmentManager as currentIsDeptMgr,
        mgr.isActive as currentIsActive,
        u.hopDistance + 1 as hopDistance,
        cast(concat(u.upwardVisitedPath, cast(mgr.pk as varchar(16)), '/') as varchar(512)) as upwardVisitedPath
    from upwardManagerCte as u
    inner join storyPoint.tbEmployee as mgr
        on u.nextManagerPk = mgr.pk
    where u.nextManagerPk is not null
      and u.nextManagerPk <> u.currentEmpPk
      and charindex('/' + cast(mgr.pk as varchar(16)) + '/', u.upwardVisitedPath) = 0
      and u.hopDistance < 15
)
select
    e.pk as employeePk,
    e.employeeCode,
    e.employeeName,
    e.loginAccount,
    e.emailAddress,
    e.jobTitle,
    e.roleName,
    case when e.roleName = 'admin' then N'管理者' else N'參與者' end as roleDisplayName,
    e.departmentPk,
    d.departmentCode,
    d.departmentName,
    e.isDepartmentManager,
    case
        when e.isDepartmentManager = 1 and (d.departmentCode = 'HR' or d.departmentName = N'HR') then N'🤝 人資主管 (部門主管)'
        when e.isDepartmentManager = 1 then N'👑 部門主管'
        else N'一般同仁'
    end as isDepartmentManagerText,
    e.managerEmployeePk,
    isnull(directMgr.employeeCode, '') as managerEmployeeCode,
    isnull(directMgr.employeeName, N'頂層根節點(無上層)') as managerEmployeeName,
    isnull(directMgr.emailAddress, '') as managerEmailAddress,
    resolvedDeptMgr.deptMgrPk as departmentManagerEmployeePk,
    isnull(resolvedDeptMgr.deptMgrNameCsv, isnull(d.managerName, N'未設定')) as departmentManagerName,
    isnull(resolvedDeptMgr.deptMgrEmailCsv, '') as departmentManagerEmail,
    isnull(resolvedDeptMgr.deptMgrCount, 0) as departmentManagerCount,
    hrMgr.hrMgrPk as hrManagerEmployeePk,
    isnull(hrMgr.hrMgrNameCsv, N'許立慧') as hrManagerName,
    isnull(hrMgr.hrMgrEmailCsv, '') as hrManagerEmail,
    isnull(treeNode.hierarchyLevel, 1) as hierarchyLevel,
    isnull(treeNode.hierarchyPath, e.employeeName) as hierarchyPath,
    concat(
        replicate(N'　', case when isnull(treeNode.hierarchyLevel, 1) > 1 then isnull(treeNode.hierarchyLevel, 1) - 1 else 0 end),
        case when isnull(treeNode.hierarchyLevel, 1) > 1 then N'└─ ' else N'🌳 ' end,
        e.employeeName
    ) as hierarchyIndentName,
    isnull(treeNode.hierarchySortKey, right('00000' + cast(e.pk as varchar(16)), 5)) as hierarchySortKey,
    isnull(subStat.directSubordinateCount, 0) as directSubordinateCount,
    e.isActive,
    isnull(empWork.assignedItemCount, 0) as assignedItemCount,
    isnull(empWork.todoItemCount, 0) as todoItemCount,
    isnull(empWork.inProgressItemCount, 0) as inProgressItemCount,
    isnull(empWork.doneItemCount, 0) as doneItemCount,
    isnull(empWork.totalAllocatedStoryPoint, 0.00) as totalAllocatedStoryPoint,
    isnull(empWork.todoAllocatedStoryPoint, 0.00) as todoAllocatedStoryPoint,
    isnull(empWork.inProgressAllocatedStoryPoint, 0.00) as inProgressAllocatedStoryPoint,
    isnull(empWork.doneAllocatedStoryPoint, 0.00) as doneAllocatedStoryPoint,
    isnull(empWork.totalDeductedStoryPoint, 0.00) as totalDeductedStoryPoint,
    isnull(empWork.totalRawWorkItemPoint, 0.00) as totalRawWorkItemPoint,
    isnull(empWork.workItemListSummary, N'無指派工項') as workItemListSummary
from storyPoint.tbEmployee as e
inner join storyPoint.tbDepartment as d
    on e.departmentPk = d.pk
left join storyPoint.tbEmployee as directMgr
    on e.managerEmployeePk = directMgr.pk
left join empHierarchyCte as treeNode
    on treeNode.employeePk = e.pk
outer apply (
    -- 透過向上樹狀遞迴與同部門主管集合解析該同仁的部門主管（支援同一個部門有多位主管：同部門多層級樹狀主管與同部門多位並列主管）
    select
        min(case when dm.rn = 1 then dm.deptMgrPk end) as deptMgrPk,
        string_agg(dm.deptMgrName, N'、') within group (order by dm.deptMgrPk asc) as deptMgrNameCsv,
        string_agg(dm.deptMgrEmail, ',') within group (order by dm.deptMgrPk asc) as deptMgrEmailCsv,
        count(dm.deptMgrPk) as deptMgrCount
    from (
        select
            dedup.deptMgrPk,
            dedup.deptMgrName,
            dedup.deptMgrEmail,
            dedup.bestPriority,
            dedup.bestDist,
            row_number() over (order by dedup.bestPriority asc, dedup.bestDist asc, dedup.deptMgrPk asc) as rn
        from (
            select
                cand.deptMgrPk,
                max(cand.deptMgrName) as deptMgrName,
                max(cand.deptMgrEmail) as deptMgrEmail,
                min(cand.matchPriority) as bestPriority,
                min(cand.sortDist) as bestDist
            from (
                -- 1. 沿樹狀遞迴向上尋找同部門且 isDepartmentManager = 1 之各層部門主管
                select
                    u.currentEmpPk as deptMgrPk,
                    u.currentEmpName as deptMgrName,
                    ltrim(rtrim(u.currentEmpEmail)) as deptMgrEmail,
                    case
                        when e.isDepartmentManager = 0 and u.hopDistance >= 1 and u.currentIsDeptMgr = 1 and u.currentDeptPk = e.departmentPk then 1
                        when e.isDepartmentManager = 1 and u.hopDistance = 0 and u.currentIsDeptMgr = 1 then 2
                        when e.isDepartmentManager = 1 and u.hopDistance >= 1 and u.currentIsDeptMgr = 1 and u.currentDeptPk = e.departmentPk then 3
                        else 4
                    end as matchPriority,
                    u.hopDistance as sortDist
                from upwardManagerCte as u
                where u.startEmployeePk = e.pk
                  and u.currentIsActive = 1
                  and u.currentIsDeptMgr = 1
                  and (
                      u.currentDeptPk = e.departmentPk
                      or not exists (
                          select 1
                          from storyPoint.tbEmployee as anySameDeptMgr
                          where anySameDeptMgr.departmentPk = e.departmentPk
                            and anySameDeptMgr.isDepartmentManager = 1
                            and anySameDeptMgr.isActive = 1
                      )
                  )
                  and (u.hopDistance >= 1 or e.isDepartmentManager = 1)

                union all

                -- 2. 同一個部門可以有多位主管：納入同部門所有啟用中且設定為 isDepartmentManager = 1 之部門主管
                select
                    sameDeptMgr.pk as deptMgrPk,
                    sameDeptMgr.employeeName as deptMgrName,
                    ltrim(rtrim(sameDeptMgr.emailAddress)) as deptMgrEmail,
                    5 as matchPriority,
                    99 as sortDist
                from storyPoint.tbEmployee as sameDeptMgr
                where sameDeptMgr.departmentPk = e.departmentPk
                  and sameDeptMgr.isDepartmentManager = 1
                  and sameDeptMgr.isActive = 1
            ) as cand
            group by cand.deptMgrPk
        ) as dedup
    ) as dm
) as resolvedDeptMgr
outer apply (
    -- 解析人資主管 (HR 部門且 isDepartmentManager = 1 之所有主管，支援多位人資主管)
    select
        min(he.pk) as hrMgrPk,
        string_agg(he.employeeName, N'、') within group (order by he.pk asc) as hrMgrNameCsv,
        string_agg(ltrim(rtrim(he.emailAddress)), ',') within group (order by he.pk asc) as hrMgrEmailCsv
    from storyPoint.tbEmployee as he
    inner join storyPoint.tbDepartment as hd
        on he.departmentPk = hd.pk
    where he.isActive = 1
      and (hd.departmentCode = 'HR' or hd.departmentName = N'HR' or he.jobTitle like N'%人資%' or he.jobTitle like N'%HR%')
      and he.isDepartmentManager = 1
) as hrMgr
outer apply (
    select count(sub.pk) as directSubordinateCount
    from storyPoint.tbEmployee as sub
    where sub.managerEmployeePk = e.pk
      and sub.pk <> e.pk
      and sub.isActive = 1
) as subStat
outer apply (
    select
        count(wia.pk) as assignedItemCount,
        sum(case when wi.executionStatus = 'todo' then 1 else 0 end) as todoItemCount,
        sum(case when wi.executionStatus = 'inProgress' then 1 else 0 end) as inProgressItemCount,
        sum(case when wi.executionStatus = 'done' then 1 else 0 end) as doneItemCount,
        sum(wia.allocatedStoryPoint) as totalAllocatedStoryPoint,
        sum(case when wi.executionStatus = 'todo' then wia.allocatedStoryPoint else 0.00 end) as todoAllocatedStoryPoint,
        sum(case when wi.executionStatus = 'inProgress' then wia.allocatedStoryPoint else 0.00 end) as inProgressAllocatedStoryPoint,
        sum(case when wi.executionStatus = 'done' then wia.allocatedStoryPoint else 0.00 end) as doneAllocatedStoryPoint,
        sum(wia.deductedAllocatedStoryPoint) as totalDeductedStoryPoint,
        sum(wi.storyPoint) as totalRawWorkItemPoint,
        string_agg(
            concat(
                wi.itemCode,
                N' ',
                wi.itemTitle,
                N' (',
                cast(wia.allocatedStoryPoint as varchar(32)),
                N' pt',
                case
                    when wi.isExpiredProcessed = 1 and wia.deductedAllocatedStoryPoint > 0
                    then concat(N'，迄時扣 ', cast(wia.deductedAllocatedStoryPoint as varchar(32)), N' pt')
                    else N''
                end,
                N')'
            ),
            N'；'
        ) within group (order by wi.pk desc) as workItemListSummary
    from storyPoint.tbWorkItemAssignment as wia
    inner join storyPoint.tbWorkItem as wi
        on wia.workItemPk = wi.pk
    where wia.employeePk = e.pk
      and wi.isActive = 1
) as empWork;
go

-- ============================================================================
-- 4. 部門總 Story Point 數與負載統計檢視表 (storyPoint.vwDepartmentPointSummary)
-- 用途: 查詢員工所屬部門的總 Story Point 數、部門主管、待處理/進行中/已完成點數及人均點數
-- ============================================================================
create or alter view storyPoint.vwDepartmentPointSummary
as
select
    d.pk as departmentPk,
    d.departmentCode,
    d.departmentName,
    d.parentDepartmentPk,
    isnull(parentDept.departmentName, N'頂層部門') as parentDepartmentName,
    isnull(deptMgrEmp.managerNameCsv, isnull(d.managerName, N'未指派')) as managerName,
    isnull(deptMgrEmp.managerEmailCsv, '') as managerEmailCsv,
    isnull(deptMgrEmp.managerCount, 0) as departmentManagerCount,
    d.departmentNote,
    d.isActive,
    isnull(empStat.totalEmployeeCount, 0) as totalEmployeeCount,
    isnull(empStat.activeEmployeeCount, 0) as activeEmployeeCount,
    isnull(deptWork.distinctWorkItemCount, 0) as distinctWorkItemCount,
    isnull(deptWork.assignmentRecordCount, 0) as assignmentRecordCount,
    isnull(deptWork.totalDepartmentStoryPoint, 0.00) as totalDepartmentStoryPoint,
    isnull(deptWork.todoDepartmentStoryPoint, 0.00) as todoDepartmentStoryPoint,
    isnull(deptWork.inProgressDepartmentStoryPoint, 0.00) as inProgressDepartmentStoryPoint,
    isnull(deptWork.doneDepartmentStoryPoint, 0.00) as doneDepartmentStoryPoint,
    isnull(deptWork.totalDeductedDepartmentStoryPoint, 0.00) as totalDeductedDepartmentStoryPoint,
    isnull(deptWork.distinctWorkItemRawPointSum, 0.00) as distinctWorkItemRawPointSum,
    case
        when isnull(empStat.activeEmployeeCount, 0) > 0
        then cast(round(isnull(deptWork.totalDepartmentStoryPoint, 0.00) / empStat.activeEmployeeCount, 2) as decimal(8,2))
        else 0.00
    end as averageStoryPointPerEmployee
from storyPoint.tbDepartment as d
left join storyPoint.tbDepartment as parentDept
    on d.parentDepartmentPk = parentDept.pk
outer apply (
    select
        string_agg(e.employeeName, N'、') within group (order by e.pk asc) as managerNameCsv,
        string_agg(ltrim(rtrim(e.emailAddress)), ',') within group (order by e.pk asc) as managerEmailCsv,
        count(e.pk) as managerCount
    from storyPoint.tbEmployee as e
    where e.departmentPk = d.pk
      and e.isDepartmentManager = 1
      and e.isActive = 1
) as deptMgrEmp
outer apply (
    select
        count(e.pk) as totalEmployeeCount,
        sum(case when e.isActive = 1 then 1 else 0 end) as activeEmployeeCount
    from storyPoint.tbEmployee as e
    where e.departmentPk = d.pk
) as empStat
outer apply (
    select
        count(distinct wia.workItemPk) as distinctWorkItemCount,
        count(wia.pk) as assignmentRecordCount,
        sum(wia.allocatedStoryPoint) as totalDepartmentStoryPoint,
        sum(case when wi.executionStatus = 'todo' then wia.allocatedStoryPoint else 0.00 end) as todoDepartmentStoryPoint,
        sum(case when wi.executionStatus = 'inProgress' then wia.allocatedStoryPoint else 0.00 end) as inProgressDepartmentStoryPoint,
        sum(case when wi.executionStatus = 'done' then wia.allocatedStoryPoint else 0.00 end) as doneDepartmentStoryPoint,
        sum(wia.deductedAllocatedStoryPoint) as totalDeductedDepartmentStoryPoint,
        (
            select sum(distinctWi.storyPoint)
            from storyPoint.tbWorkItem as distinctWi
            where distinctWi.isActive = 1
              and exists (
                  select 1
                  from storyPoint.tbWorkItemAssignment as subWia
                  inner join storyPoint.tbEmployee as subEmp
                      on subWia.employeePk = subEmp.pk
                  where subWia.workItemPk = distinctWi.pk
                    and subEmp.departmentPk = d.pk
              )
        ) as distinctWorkItemRawPointSum
    from storyPoint.tbWorkItemAssignment as wia
    inner join storyPoint.tbEmployee as e
        on wia.employeePk = e.pk
    inner join storyPoint.tbWorkItem as wi
        on wia.workItemPk = wi.pk
    where e.departmentPk = d.pk
      and wi.isActive = 1
) as deptWork;
go

-- ============================================================================
-- 5. 投票議程與訂出 Story Point 過程總覽檢視表 (storyPoint.vwVotingSessionSummary)
-- ============================================================================
create or alter view storyPoint.vwVotingSessionSummary
as
select
    vs.pk as votingSessionPk,
    vs.sessionCode,
    vs.sessionTitle,
    vs.targetKind,
    case when vs.targetKind = 'workItemType' then N'基本工項估算' else N'一般工項估算' end as targetKindText,
    vs.workItemTypePk,
    isnull(wit.typeCode, refWit.typeCode) as typeCode,
    isnull(wit.typeName, refWit.typeName) as typeName,
    vs.workItemPk,
    wi.itemCode,
    wi.itemTitle,
    wi.workItemTypePk as referencedWorkItemTypePk,
    refWit.typeCode as referencedWorkItemTypeCode,
    refWit.typeName as referencedWorkItemTypeName,
    case
        when vs.targetKind = 'workItemType' then concat(N'[基本工項] ', isnull(wit.typeCode, ''), N' - ', isnull(wit.typeName, ''), case when wit.defaultWorkItemKind is not null then concat(N' (參照類型: ', isnull(wik.kindName, wit.defaultWorkItemKind), N')') else N'' end)
        else concat(N'[一般工項] ', isnull(wi.itemCode, ''), N' - ', isnull(wi.itemTitle, ''), case when refWit.typeName is not null then concat(N' (參照基本工項: ', refWit.typeName, N')') else N'' end)
    end as targetDisplayName,
    vs.cardDeckPk,
    cd.deckCode,
    cd.deckName,
    cd.cardValueCsv,
    vs.hostEmployeePk,
    hostEmp.employeeName as hostEmployeeName,
    vs.currentRoundNumber,
    vs.sessionStatus,
    case
        when vs.sessionStatus = 'voting' then N'出牌投票中(盲投隱藏)'
        when vs.sessionStatus = 'revealed' then N'已翻牌揭曉(討論中)'
        when vs.sessionStatus = 'finalized' then N'已拍板定案'
        else vs.sessionStatus
    end as sessionStatusText,
    vs.finalStoryPoint,
    vs.finalDecisionNote,
    vs.minStoryPoint,
    vs.maxStoryPoint,
    vs.maxAllowedSpread,
    vs.consensusRule,
    case
        when vs.consensusRule = 'nearestFibonacci' then N'對齊最接近費氏牌面'
        when vs.consensusRule = 'average' then N'算術平均數'
        when vs.consensusRule = 'median' then N'中位數'
        when vs.consensusRule = 'mode' then N'眾數(多數決)'
        when vs.consensusRule = 'trimmedMean' then N'去頭去尾平均'
        when vs.consensusRule = 'maxConservative' then N'保守取較高值'
        else N'對齊最接近費氏牌面'
    end as consensusRuleText,
    vs.autoRevealWhenAllVoted,
    vs.roundTimeLimitSecond,
    vs.allowQuestionCard,
    vs.allowCoffeeCard,
    vs.voteCommentPolicy,
    case
        when vs.voteCommentPolicy = 'requiredAll' then N'全員必填估算理由'
        when vs.voteCommentPolicy = 'requiredExtreme' then N'極端值與疑問牌必填'
        else N'自由選填理由'
    end as voteCommentPolicyText,
    vs.restrictToParticipant,
    vs.roundGoalNote,
    isnull(partStat.participantCount, 0) as participantCount,
    vs.startedAt,
    vs.finalizedAt,
    isnull(roundStat.totalRoundCount, 1) as totalRoundCount,
    isnull(roundStat.totalVoteRecordCount, 0) as totalVoteRecordCount,
    curRound.pk as currentVotingRoundPk,
    curRound.roundStatus as currentRoundStatus,
    curRound.averagePoint as currentRoundAveragePoint,
    curRound.minPoint as currentRoundMinPoint,
    curRound.maxPoint as currentRoundMaxPoint,
    curRound.voteCount as currentRoundVoteCount,
    curRound.roundDiscussionNote as currentRoundDiscussionNote
from storyPoint.tbVotingSession as vs
inner join storyPoint.tbCardDeck as cd
    on vs.cardDeckPk = cd.pk
inner join storyPoint.tbEmployee as hostEmp
    on vs.hostEmployeePk = hostEmp.pk
left join storyPoint.tbWorkItemType as wit
    on vs.workItemTypePk = wit.pk
left join storyPoint.tbWorkItemKind as wik
    on wik.kindCode = wit.defaultWorkItemKind
left join storyPoint.tbWorkItem as wi
    on vs.workItemPk = wi.pk
left join storyPoint.tbWorkItemType as refWit
    on wi.workItemTypePk = refWit.pk
left join storyPoint.tbVotingRound as curRound
    on curRound.votingSessionPk = vs.pk
   and curRound.roundNumber = vs.currentRoundNumber
outer apply (
    select
        count(distinct vr.roundNumber) as totalRoundCount,
        count(vrec.pk) as totalVoteRecordCount
    from storyPoint.tbVotingRound as vr
    left join storyPoint.tbVoteRecord as vrec
        on vrec.votingRoundPk = vr.pk
    where vr.votingSessionPk = vs.pk
) as roundStat
outer apply (
    select
        count(vsp.pk) as participantCount
    from storyPoint.tbVotingSessionParticipant as vsp
    where vsp.votingSessionPk = vs.pk
) as partStat;
go
