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
            when wi.workItemKind = 'project' then N'專案'
            else cast(wi.workItemKind as nvarchar(128))
        end
    ) as workItemKindText,
    isnull(
        wik.badgeIcon,
        case
            when wi.workItemKind = 'maintenance' then N'🛠️'
            when wi.workItemKind = 'consultingHour' then N'⏳'
            else N'🏗️'
        end
    ) as workItemKindIcon,
    isnull(
        wik.badgeColor,
        case
            when wi.workItemKind = 'maintenance' then 'amber'
            when wi.workItemKind = 'consultingHour' then 'purple'
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
-- 3. 員工個人 Story Point 負載與工項統計檢視表 (storyPoint.vwEmployeePointSummary)
-- 用途: 查詢每位員工身上有多少工項與多少 Story Point (含分攤點數、已到期扣點與原始工項總點數)
-- ============================================================================
create or alter view storyPoint.vwEmployeePointSummary
as
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
-- 用途: 查詢員工所屬部門的總 Story Point 數、待處理/進行中/已完成點數及人均點數
-- ============================================================================
create or alter view storyPoint.vwDepartmentPointSummary
as
select
    d.pk as departmentPk,
    d.departmentCode,
    d.departmentName,
    d.parentDepartmentPk,
    isnull(parentDept.departmentName, N'頂層部門') as parentDepartmentName,
    d.managerName,
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
    case when vs.targetKind = 'workItemType' then N'工項代表類型估算' else N'個別工項估算' end as targetKindText,
    vs.workItemTypePk,
    wit.typeCode,
    wit.typeName,
    vs.workItemPk,
    wi.itemCode,
    wi.itemTitle,
    case
        when vs.targetKind = 'workItemType' then concat(N'[代表類型] ', isnull(wit.typeCode, ''), N' - ', isnull(wit.typeName, ''))
        else concat(N'[實際工項] ', isnull(wi.itemCode, ''), N' - ', isnull(wi.itemTitle, ''))
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
left join storyPoint.tbWorkItem as wi
    on vs.workItemPk = wi.pk
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
