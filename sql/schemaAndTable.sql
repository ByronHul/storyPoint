/*
================================================================================
檔案名稱: schemaAndTable.sql
用途說明: 建立 Azure 多人參與 Story Point 估算與工項權重管理系統之專屬 schema (storyPoint) 及所有資料表 (tb*)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
修改歷程:
    2026-10-23 初版建立，嚴格遵守單數駝峰式命名、全小寫關鍵字、兩節式名稱、主鍵 pk identity，
               並支援可重複執行 (drop table if exists + 無損資料保留機制)
================================================================================
*/

set nocount on;
go

-- 建立專屬 schema: storyPoint (嚴禁使用預設 schema)
if not exists (select 1 from sys.schemas where name = 'storyPoint')
begin
    exec sp_executesql N'create schema storyPoint';
end
go

-- ============================================================================
-- 階段一：在執行 drop table if exists 前，自動將既有資料複製至暫存保留表 (tbPreserve*)
-- 確保重複執行腳本時可無損還原既有業務資料
-- ============================================================================
drop table if exists storyPoint.tbPreservePointHistory;
drop table if exists storyPoint.tbPreserveVoteRecord;
drop table if exists storyPoint.tbPreserveVotingRound;
drop table if exists storyPoint.tbPreserveVotingSessionParticipant;
drop table if exists storyPoint.tbPreserveVotingSession;
drop table if exists storyPoint.tbPreserveWorkItemAssignment;
drop table if exists storyPoint.tbPreserveWorkItem;
drop table if exists storyPoint.tbPreserveWorkItemType;
drop table if exists storyPoint.tbPreserveWorkItemKind;
drop table if exists storyPoint.tbPreserveCardDeck;
drop table if exists storyPoint.tbPreserveEmployee;
drop table if exists storyPoint.tbPreserveDepartment;
drop table if exists storyPoint.tbPreserveLog;
go

if object_id('storyPoint.tbDepartment', 'u') is not null
begin
    select
        pk, departmentCode, departmentName, parentDepartmentPk, managerName,
        departmentNote, isActive, createdAt, updatedAt
    into storyPoint.tbPreserveDepartment
    from storyPoint.tbDepartment;
end

if object_id('storyPoint.tbEmployee', 'u') is not null
begin
    select
        pk, employeeCode, employeeName, loginAccount, passwordHash,
        emailAddress, jobTitle, departmentPk, roleName, isActive,
        lastLoginTime, createdAt, updatedAt
    into storyPoint.tbPreserveEmployee
    from storyPoint.tbEmployee;
end

if object_id('storyPoint.tbCardDeck', 'u') is not null
begin
    select
        pk, deckCode, deckName, cardValueCsv, description,
        isDefault, isActive, createdAt, updatedAt
    into storyPoint.tbPreserveCardDeck
    from storyPoint.tbCardDeck;
end

if object_id('storyPoint.tbWorkItemKind', 'u') is not null
begin
    select
        pk, kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        description, isActive, createdAt, updatedAt
    into storyPoint.tbPreserveWorkItemKind
    from storyPoint.tbWorkItemKind;
end

if object_id('storyPoint.tbWorkItemType', 'u') is not null
begin
    if col_length('storyPoint.tbWorkItemType', 'defaultWorkItemKind') is null
        alter table storyPoint.tbWorkItemType add defaultWorkItemKind varchar(64) not null constraint dfMigrateTypeKind default ('project');
end
go

if object_id('storyPoint.tbWorkItemType', 'u') is not null
begin
    select
        pk, typeCode, typeName, categoryName, defaultWorkItemKind, description,
        standardStoryPoint, defaultCardDeckPk, estimationStatus,
        lastVotingSessionPk, isActive, createdAt, updatedAt
    into storyPoint.tbPreserveWorkItemType
    from storyPoint.tbWorkItemType;
end

if object_id('storyPoint.tbWorkItem', 'u') is not null
begin
    if col_length('storyPoint.tbWorkItem', 'workItemKind') is null
        alter table storyPoint.tbWorkItem add workItemKind varchar(64) not null constraint dfMigrateWorkItemKind default ('project');
    if col_length('storyPoint.tbWorkItem', 'startDate') is null
        alter table storyPoint.tbWorkItem add startDate datetime2(0) null;
    if col_length('storyPoint.tbWorkItem', 'endDate') is null
        alter table storyPoint.tbWorkItem add endDate datetime2(0) null;
    if col_length('storyPoint.tbWorkItem', 'originalStoryPoint') is null
        alter table storyPoint.tbWorkItem add originalStoryPoint decimal(8,2) null;
    if col_length('storyPoint.tbWorkItem', 'deductedStoryPoint') is null
        alter table storyPoint.tbWorkItem add deductedStoryPoint decimal(8,2) not null constraint dfMigrateDeductedPoint default (0.00);
    if col_length('storyPoint.tbWorkItem', 'isExpiredProcessed') is null
        alter table storyPoint.tbWorkItem add isExpiredProcessed bit not null constraint dfMigrateExpiredProcessed default (0);
    if col_length('storyPoint.tbWorkItem', 'expiredProcessedAt') is null
        alter table storyPoint.tbWorkItem add expiredProcessedAt datetime2(3) null;
    if col_length('storyPoint.tbWorkItem', 'expiryNotificationSent') is null
        alter table storyPoint.tbWorkItem add expiryNotificationSent bit not null constraint dfMigrateExpiryNotified default (0);
    if col_length('storyPoint.tbWorkItem', 'expiryNotificationSentAt') is null
        alter table storyPoint.tbWorkItem add expiryNotificationSentAt datetime2(3) null;
    if col_length('storyPoint.tbWorkItem', 'expiryNotificationRecipient') is null
        alter table storyPoint.tbWorkItem add expiryNotificationRecipient nvarchar(1024) null;
end
go

if object_id('storyPoint.tbWorkItem', 'u') is not null
begin
    select
        pk, itemCode, itemTitle, itemDescription, workItemTypePk,
        workItemKind, sprintName, priorityLevel, defaultTypePoint, storyPoint,
        originalStoryPoint, deductedStoryPoint, isManualAdjusted, adjustmentReason,
        estimationStatus, executionStatus, startDate, endDate, dueDate,
        isExpiredProcessed, expiredProcessedAt, expiryNotificationSent,
        expiryNotificationSentAt, expiryNotificationRecipient,
        createdByEmployeePk, isActive, createdAt, updatedAt
    into storyPoint.tbPreserveWorkItem
    from storyPoint.tbWorkItem;
end

if object_id('storyPoint.tbWorkItemAssignment', 'u') is not null
begin
    if col_length('storyPoint.tbWorkItemAssignment', 'originalAllocatedStoryPoint') is null
        alter table storyPoint.tbWorkItemAssignment add originalAllocatedStoryPoint decimal(8,2) null;
    if col_length('storyPoint.tbWorkItemAssignment', 'deductedAllocatedStoryPoint') is null
        alter table storyPoint.tbWorkItemAssignment add deductedAllocatedStoryPoint decimal(8,2) not null constraint dfMigrateAssignDeductedPoint default (0.00);
end
go

if object_id('storyPoint.tbWorkItemAssignment', 'u') is not null
begin
    select
        pk, workItemPk, employeePk, assignmentRole, allocationPercent,
        allocatedStoryPoint, originalAllocatedStoryPoint, deductedAllocatedStoryPoint,
        assignmentNote, assignedByEmployeePk, assignedAt, updatedAt
    into storyPoint.tbPreserveWorkItemAssignment
    from storyPoint.tbWorkItemAssignment;
end

if object_id('storyPoint.tbVotingSession', 'u') is not null
begin
    if col_length('storyPoint.tbVotingSession', 'minStoryPoint') is null
        alter table storyPoint.tbVotingSession add minStoryPoint decimal(8,2) not null constraint dfMigrateMinPoint default (0.00);
    if col_length('storyPoint.tbVotingSession', 'maxStoryPoint') is null
        alter table storyPoint.tbVotingSession add maxStoryPoint decimal(8,2) not null constraint dfMigrateMaxPoint default (21.00);
    if col_length('storyPoint.tbVotingSession', 'maxAllowedSpread') is null
        alter table storyPoint.tbVotingSession add maxAllowedSpread decimal(8,2) not null constraint dfMigrateMaxSpread default (5.00);
    if col_length('storyPoint.tbVotingSession', 'consensusRule') is null
        alter table storyPoint.tbVotingSession add consensusRule varchar(32) not null constraint dfMigrateConsensusRule default ('nearestFibonacci');
    if col_length('storyPoint.tbVotingSession', 'autoRevealWhenAllVoted') is null
        alter table storyPoint.tbVotingSession add autoRevealWhenAllVoted bit not null constraint dfMigrateAutoReveal default (1);
    if col_length('storyPoint.tbVotingSession', 'roundTimeLimitSecond') is null
        alter table storyPoint.tbVotingSession add roundTimeLimitSecond int not null constraint dfMigrateTimeLimit default (120);
    if col_length('storyPoint.tbVotingSession', 'allowQuestionCard') is null
        alter table storyPoint.tbVotingSession add allowQuestionCard bit not null constraint dfMigrateAllowQuestion default (1);
    if col_length('storyPoint.tbVotingSession', 'allowCoffeeCard') is null
        alter table storyPoint.tbVotingSession add allowCoffeeCard bit not null constraint dfMigrateAllowCoffee default (1);
    if col_length('storyPoint.tbVotingSession', 'voteCommentPolicy') is null
        alter table storyPoint.tbVotingSession add voteCommentPolicy varchar(32) not null constraint dfMigrateCommentPolicy default ('optional');
    if col_length('storyPoint.tbVotingSession', 'restrictToParticipant') is null
        alter table storyPoint.tbVotingSession add restrictToParticipant bit not null constraint dfMigrateRestrictParticipant default (1);
    if col_length('storyPoint.tbVotingSession', 'roundGoalNote') is null
        alter table storyPoint.tbVotingSession add roundGoalNote nvarchar(1024) null;
end
go

if object_id('storyPoint.tbVotingSession', 'u') is not null
begin
    select
        pk, sessionCode, sessionTitle, targetKind, workItemTypePk,
        workItemPk, cardDeckPk, hostEmployeePk, currentRoundNumber,
        sessionStatus, finalStoryPoint, finalDecisionNote,
        minStoryPoint, maxStoryPoint, maxAllowedSpread, consensusRule,
        cast(1 as bit) as autoRevealWhenAllVoted, roundTimeLimitSecond, allowQuestionCard,
        allowCoffeeCard, voteCommentPolicy, restrictToParticipant, roundGoalNote,
        startedAt, finalizedAt, createdAt, updatedAt
    into storyPoint.tbPreserveVotingSession
    from storyPoint.tbVotingSession;
end

if object_id('storyPoint.tbVotingSessionParticipant', 'u') is not null
begin
    select
        pk, votingSessionPk, employeePk, isRequiredVoter, joinedAt
    into storyPoint.tbPreserveVotingSessionParticipant
    from storyPoint.tbVotingSessionParticipant;
end

if object_id('storyPoint.tbVotingRound', 'u') is not null
begin
    select
        pk, votingSessionPk, roundNumber, roundStatus, averagePoint,
        minPoint, maxPoint, voteCount, roundDiscussionNote,
        startedAt, revealedAt
    into storyPoint.tbPreserveVotingRound
    from storyPoint.tbVotingRound;
end

if object_id('storyPoint.tbVoteRecord', 'u') is not null
begin
    select
        pk, votingRoundPk, votingSessionPk, roundNumber, employeePk,
        cardValue, numericPointValue, voteComment, votedAt, updatedAt
    into storyPoint.tbPreserveVoteRecord
    from storyPoint.tbVoteRecord;
end

if object_id('storyPoint.tbPointHistory', 'u') is not null
begin
    select
        pk, targetKind, workItemTypePk, workItemPk, votingSessionPk,
        oldStoryPoint, newStoryPoint, changeSource, changeReason,
        changedByEmployeePk, changedAt
    into storyPoint.tbPreservePointHistory
    from storyPoint.tbPointHistory;
end

if object_id('storyPoint.tbLog', 'u') is not null
begin
    select
        pk, logTime, logLevel, sourceName, objectName,
        durationMillisecond, executionResult, errorCode, errorLine,
        errorMessage, operatorAccount
    into storyPoint.tbPreserveLog
    from storyPoint.tbLog;
end
go

-- ============================================================================
-- 階段二：依外鍵相依反序執行 drop table if exists (符合可重複執行規範)
-- ============================================================================
drop table if exists storyPoint.tbPointHistory;
drop table if exists storyPoint.tbVoteRecord;
drop table if exists storyPoint.tbVotingRound;
drop table if exists storyPoint.tbVotingSessionParticipant;
drop table if exists storyPoint.tbVotingSession;
drop table if exists storyPoint.tbWorkItemAssignment;
drop table if exists storyPoint.tbWorkItem;
drop table if exists storyPoint.tbWorkItemType;
drop table if exists storyPoint.tbWorkItemKind;
drop table if exists storyPoint.tbCardDeck;
drop table if exists storyPoint.tbEmployee;
drop table if exists storyPoint.tbDepartment;
drop table if exists storyPoint.tbLog;
go

-- ============================================================================
-- 階段三：建立所有業務與系統資料表
-- ============================================================================

-- 1. 系統與 SP 執行紀錄表 (storyPoint.tbLog)
create table storyPoint.tbLog (
    pk int identity(1,1) not null,
    logTime datetime2(3) not null constraint dfTbLogLogTime default (sysdatetime()),
    logLevel tinyint not null constraint dfTbLogLogLevel default (3),
    sourceName varchar(128) not null,
    objectName nvarchar(128) not null,
    durationMillisecond int not null constraint dfTbLogDuration default (0),
    executionResult nvarchar(256) not null,
    errorCode int null,
    errorLine int null,
    errorMessage nvarchar(max) null,
    operatorAccount varchar(128) null,
    constraint pkTbLog primary key clustered (pk asc)
);
go

create nonclustered index ixTbLogLogTime on storyPoint.tbLog (logTime desc, logLevel asc);
go

-- 2. 部門資料表 (storyPoint.tbDepartment)
create table storyPoint.tbDepartment (
    pk int identity(1,1) not null,
    departmentCode varchar(64) not null,
    departmentName nvarchar(128) not null,
    parentDepartmentPk int null,
    managerName nvarchar(128) null,
    departmentNote nvarchar(512) null,
    isActive bit not null constraint dfTbDepartmentIsActive default (1),
    createdAt datetime2(3) not null constraint dfTbDepartmentCreatedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbDepartmentUpdatedAt default (sysdatetime()),
    constraint pkTbDepartment primary key clustered (pk asc),
    constraint uqTbDepartmentCode unique nonclustered (departmentCode asc),
    constraint fkTbDepartmentParent foreign key (parentDepartmentPk) references storyPoint.tbDepartment (pk)
);
go

-- 3. 員工與登入帳密資料表 (storyPoint.tbEmployee)
create table storyPoint.tbEmployee (
    pk int identity(1,1) not null,
    employeeCode varchar(64) not null,
    employeeName nvarchar(128) not null,
    loginAccount varchar(128) not null,
    passwordHash varchar(256) not null,
    emailAddress varchar(256) not null,
    jobTitle nvarchar(128) not null,
    departmentPk int not null,
    roleName varchar(32) not null constraint dfTbEmployeeRoleName default ('participant'),
    isActive bit not null constraint dfTbEmployeeIsActive default (1),
    lastLoginTime datetime2(3) null,
    createdAt datetime2(3) not null constraint dfTbEmployeeCreatedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbEmployeeUpdatedAt default (sysdatetime()),
    constraint pkTbEmployee primary key clustered (pk asc),
    constraint uqTbEmployeeCode unique nonclustered (employeeCode asc),
    constraint uqTbEmployeeLoginAccount unique nonclustered (loginAccount asc),
    constraint fkTbEmployeeDepartment foreign key (departmentPk) references storyPoint.tbDepartment (pk)
);
go

create nonclustered index ixTbEmployeeDepartment on storyPoint.tbEmployee (departmentPk asc, isActive asc);
go

-- 4. 撲克牌組設定資料表 (storyPoint.tbCardDeck)
create table storyPoint.tbCardDeck (
    pk int identity(1,1) not null,
    deckCode varchar(64) not null,
    deckName nvarchar(128) not null,
    cardValueCsv nvarchar(256) not null,
    description nvarchar(512) null,
    isDefault bit not null constraint dfTbCardDeckIsDefault default (0),
    isActive bit not null constraint dfTbCardDeckIsActive default (1),
    createdAt datetime2(3) not null constraint dfTbCardDeckCreatedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbCardDeckUpdatedAt default (sysdatetime()),
    constraint pkTbCardDeck primary key clustered (pk asc),
    constraint uqTbCardDeckCode unique nonclustered (deckCode asc)
);
go

-- 4.5 工項類型設定資料表 (storyPoint.tbWorkItemKind)
-- 支援增刪修工項類型（預設含：project 專案、maintenance 維運、consultingHour 顧問時數，並可自訂新增）
create table storyPoint.tbWorkItemKind (
    pk int identity(1,1) not null,
    kindCode varchar(64) not null,
    kindName nvarchar(128) not null,
    badgeIcon nvarchar(32) not null constraint dfTbWorkItemKindIcon default (N'📌'),
    badgeColor varchar(32) not null constraint dfTbWorkItemKindColor default ('blue'),
    sortOrder int not null constraint dfTbWorkItemKindSortOrder default (100),
    description nvarchar(512) null,
    isActive bit not null constraint dfTbWorkItemKindIsActive default (1),
    createdAt datetime2(3) not null constraint dfTbWorkItemKindCreatedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbWorkItemKindUpdatedAt default (sysdatetime()),
    constraint pkTbWorkItemKind primary key clustered (pk asc),
    constraint uqTbWorkItemKindCode unique nonclustered (kindCode asc)
);
go

-- 5. 工項代表類型資料表 (storyPoint.tbWorkItemType)
-- 投票時可針對工項代表類型進行 Story Point 估算，建立實際工項時可選擇此類型自動帶入點數
create table storyPoint.tbWorkItemType (
    pk int identity(1,1) not null,
    typeCode varchar(64) not null,
    typeName nvarchar(128) not null,
    categoryName nvarchar(128) not null,
    defaultWorkItemKind varchar(64) not null constraint dfTbWorkItemTypeDefaultKind default ('project'),
    description nvarchar(1024) null,
    standardStoryPoint decimal(8,2) null,
    defaultCardDeckPk int null,
    estimationStatus varchar(32) not null constraint dfTbWorkItemTypeStatus default ('pending'),
    lastVotingSessionPk int null,
    isActive bit not null constraint dfTbWorkItemTypeIsActive default (1),
    createdAt datetime2(3) not null constraint dfTbWorkItemTypeCreatedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbWorkItemTypeUpdatedAt default (sysdatetime()),
    constraint pkTbWorkItemType primary key clustered (pk asc),
    constraint uqTbWorkItemTypeCode unique nonclustered (typeCode asc),
    constraint fkTbWorkItemTypeCardDeck foreign key (defaultCardDeckPk) references storyPoint.tbCardDeck (pk)
);
go

-- 6. 實際工項資料表 (storyPoint.tbWorkItem)
-- workItemKind 對應 storyPoint.tbWorkItemKind.kindCode (預設含 project 專案 / maintenance 維運 / consultingHour 顧問時數，並支援自訂)
create table storyPoint.tbWorkItem (
    pk int identity(1,1) not null,
    itemCode varchar(64) not null,
    itemTitle nvarchar(256) not null,
    itemDescription nvarchar(max) null,
    workItemTypePk int null,
    workItemKind varchar(64) not null constraint dfTbWorkItemKind default ('project'),
    sprintName nvarchar(128) null,
    priorityLevel varchar(32) not null constraint dfTbWorkItemPriority default ('medium'),
    defaultTypePoint decimal(8,2) null,
    storyPoint decimal(8,2) not null constraint dfTbWorkItemStoryPoint default (0),
    originalStoryPoint decimal(8,2) null,
    deductedStoryPoint decimal(8,2) not null constraint dfTbWorkItemDeductedPoint default (0.00),
    isManualAdjusted bit not null constraint dfTbWorkItemIsManual default (0),
    adjustmentReason nvarchar(512) null,
    estimationStatus varchar(32) not null constraint dfTbWorkItemEstimationStatus default ('finalized'),
    executionStatus varchar(32) not null constraint dfTbWorkItemExecutionStatus default ('todo'),
    startDate datetime2(0) null,
    endDate datetime2(0) null,
    dueDate date null,
    isExpiredProcessed bit not null constraint dfTbWorkItemExpiredProcessed default (0),
    expiredProcessedAt datetime2(3) null,
    expiryNotificationSent bit not null constraint dfTbWorkItemExpiryNotified default (0),
    expiryNotificationSentAt datetime2(3) null,
    expiryNotificationRecipient nvarchar(1024) null,
    createdByEmployeePk int null,
    isActive bit not null constraint dfTbWorkItemIsActive default (1),
    createdAt datetime2(3) not null constraint dfTbWorkItemCreatedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbWorkItemUpdatedAt default (sysdatetime()),
    constraint pkTbWorkItem primary key clustered (pk asc),
    constraint uqTbWorkItemCode unique nonclustered (itemCode asc),
    constraint fkTbWorkItemType foreign key (workItemTypePk) references storyPoint.tbWorkItemType (pk),
    constraint fkTbWorkItemCreatedBy foreign key (createdByEmployeePk) references storyPoint.tbEmployee (pk)
);
go

create nonclustered index ixTbWorkItemTypeAndStatus on storyPoint.tbWorkItem (workItemTypePk asc, executionStatus asc, isActive asc);
go

-- 7. 員工工項賦予與點數分攤資料表 (storyPoint.tbWorkItemAssignment)
create table storyPoint.tbWorkItemAssignment (
    pk int identity(1,1) not null,
    workItemPk int not null,
    employeePk int not null,
    assignmentRole nvarchar(64) not null constraint dfTbAssignmentRole default (N'主要負責'),
    allocationPercent decimal(5,2) not null constraint dfTbAssignmentPercent default (100.00),
    allocatedStoryPoint decimal(8,2) not null constraint dfTbAssignmentPoint default (0.00),
    originalAllocatedStoryPoint decimal(8,2) null,
    deductedAllocatedStoryPoint decimal(8,2) not null constraint dfTbAssignmentDeductedPoint default (0.00),
    assignmentNote nvarchar(512) null,
    assignedByEmployeePk int null,
    assignedAt datetime2(3) not null constraint dfTbAssignmentAssignedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbAssignmentUpdatedAt default (sysdatetime()),
    constraint pkTbWorkItemAssignment primary key clustered (pk asc),
    constraint uqTbWorkItemAssignment unique nonclustered (workItemPk asc, employeePk asc),
    constraint fkTbAssignmentWorkItem foreign key (workItemPk) references storyPoint.tbWorkItem (pk),
    constraint fkTbAssignmentEmployee foreign key (employeePk) references storyPoint.tbEmployee (pk),
    constraint fkTbAssignmentAssignedBy foreign key (assignedByEmployeePk) references storyPoint.tbEmployee (pk)
);
go

create nonclustered index ixTbWorkItemAssignmentEmployee on storyPoint.tbWorkItemAssignment (employeePk asc, workItemPk asc);
go

-- 8. 多人 Story Point 投票議程資料表 (storyPoint.tbVotingSession)
-- targetKind: 'workItemType' (工項代表類型投票) 或 'workItem' (個別工項投票)
create table storyPoint.tbVotingSession (
    pk int identity(1,1) not null,
    sessionCode varchar(64) not null,
    sessionTitle nvarchar(256) not null,
    targetKind varchar(32) not null constraint dfTbVotingSessionTargetKind default ('workItemType'),
    workItemTypePk int null,
    workItemPk int null,
    cardDeckPk int not null,
    hostEmployeePk int not null,
    currentRoundNumber int not null constraint dfTbVotingSessionCurrentRound default (1),
    sessionStatus varchar(32) not null constraint dfTbVotingSessionStatus default ('voting'),
    finalStoryPoint decimal(8,2) null,
    finalDecisionNote nvarchar(1024) null,
    minStoryPoint decimal(8,2) not null constraint dfTbVotingSessionMinPoint default (0.00),
    maxStoryPoint decimal(8,2) not null constraint dfTbVotingSessionMaxPoint default (21.00),
    maxAllowedSpread decimal(8,2) not null constraint dfTbVotingSessionMaxSpread default (5.00),
    consensusRule varchar(32) not null constraint dfTbVotingSessionConsensusRule default ('nearestFibonacci'),
    autoRevealWhenAllVoted bit not null constraint dfTbVotingSessionAutoReveal default (1),
    roundTimeLimitSecond int not null constraint dfTbVotingSessionTimeLimit default (120),
    allowQuestionCard bit not null constraint dfTbVotingSessionAllowQuestion default (1),
    allowCoffeeCard bit not null constraint dfTbVotingSessionAllowCoffee default (1),
    voteCommentPolicy varchar(32) not null constraint dfTbVotingSessionCommentPolicy default ('optional'),
    restrictToParticipant bit not null constraint dfTbVotingSessionRestrictParticipant default (1),
    roundGoalNote nvarchar(1024) null,
    startedAt datetime2(3) not null constraint dfTbVotingSessionStartedAt default (sysdatetime()),
    finalizedAt datetime2(3) null,
    createdAt datetime2(3) not null constraint dfTbVotingSessionCreatedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbVotingSessionUpdatedAt default (sysdatetime()),
    constraint pkTbVotingSession primary key clustered (pk asc),
    constraint uqTbVotingSessionCode unique nonclustered (sessionCode asc),
    constraint fkTbVotingSessionWorkItemType foreign key (workItemTypePk) references storyPoint.tbWorkItemType (pk),
    constraint fkTbVotingSessionWorkItem foreign key (workItemPk) references storyPoint.tbWorkItem (pk),
    constraint fkTbVotingSessionCardDeck foreign key (cardDeckPk) references storyPoint.tbCardDeck (pk),
    constraint fkTbVotingSessionHost foreign key (hostEmployeePk) references storyPoint.tbEmployee (pk)
);
go

-- 9. 投票議程授權參與員工資料表 (storyPoint.tbVotingSessionParticipant)
create table storyPoint.tbVotingSessionParticipant (
    pk int identity(1,1) not null,
    votingSessionPk int not null,
    employeePk int not null,
    isRequiredVoter bit not null constraint dfTbSessionParticipantRequired default (1),
    joinedAt datetime2(3) not null constraint dfTbSessionParticipantJoinedAt default (sysdatetime()),
    constraint pkTbVotingSessionParticipant primary key clustered (pk asc),
    constraint uqTbVotingSessionParticipant unique nonclustered (votingSessionPk asc, employeePk asc),
    constraint fkTbSessionParticipantSession foreign key (votingSessionPk) references storyPoint.tbVotingSession (pk),
    constraint fkTbSessionParticipantEmployee foreign key (employeePk) references storyPoint.tbEmployee (pk)
);
go

-- 10. 投票回合紀錄資料表 (storyPoint.tbVotingRound)
create table storyPoint.tbVotingRound (
    pk int identity(1,1) not null,
    votingSessionPk int not null,
    roundNumber int not null,
    roundStatus varchar(32) not null constraint dfTbVotingRoundStatus default ('voting'),
    averagePoint decimal(8,2) null,
    minPoint decimal(8,2) null,
    maxPoint decimal(8,2) null,
    voteCount int not null constraint dfTbVotingRoundVoteCount default (0),
    roundDiscussionNote nvarchar(1024) null,
    startedAt datetime2(3) not null constraint dfTbVotingRoundStartedAt default (sysdatetime()),
    revealedAt datetime2(3) null,
    constraint pkTbVotingRound primary key clustered (pk asc),
    constraint uqTbVotingRoundSessionRound unique nonclustered (votingSessionPk asc, roundNumber asc),
    constraint fkTbVotingRoundSession foreign key (votingSessionPk) references storyPoint.tbVotingSession (pk)
);
go

-- 11. 員工各回合出牌明細資料表 (storyPoint.tbVoteRecord)
create table storyPoint.tbVoteRecord (
    pk int identity(1,1) not null,
    votingRoundPk int not null,
    votingSessionPk int not null,
    roundNumber int not null,
    employeePk int not null,
    cardValue nvarchar(32) not null,
    numericPointValue decimal(8,2) null,
    voteComment nvarchar(512) null,
    votedAt datetime2(3) not null constraint dfTbVoteRecordVotedAt default (sysdatetime()),
    updatedAt datetime2(3) not null constraint dfTbVoteRecordUpdatedAt default (sysdatetime()),
    constraint pkTbVoteRecord primary key clustered (pk asc),
    constraint uqTbVoteRecordRoundEmployee unique nonclustered (votingRoundPk asc, employeePk asc),
    constraint fkTbVoteRecordRound foreign key (votingRoundPk) references storyPoint.tbVotingRound (pk),
    constraint fkTbVoteRecordSession foreign key (votingSessionPk) references storyPoint.tbVotingSession (pk),
    constraint fkTbVoteRecordEmployee foreign key (employeePk) references storyPoint.tbEmployee (pk)
);
go

create nonclustered index ixTbVoteRecordSession on storyPoint.tbVoteRecord (votingSessionPk asc, roundNumber asc, employeePk asc);
go

-- 12. Story Point 訂定與異動歷程資料表 (storyPoint.tbPointHistory)
create table storyPoint.tbPointHistory (
    pk int identity(1,1) not null,
    targetKind varchar(32) not null,
    workItemTypePk int null,
    workItemPk int null,
    votingSessionPk int null,
    oldStoryPoint decimal(8,2) null,
    newStoryPoint decimal(8,2) not null,
    changeSource varchar(64) not null,
    changeReason nvarchar(1024) null,
    changedByEmployeePk int null,
    changedAt datetime2(3) not null constraint dfTbPointHistoryChangedAt default (sysdatetime()),
    constraint pkTbPointHistory primary key clustered (pk asc),
    constraint fkTbPointHistoryWorkItemType foreign key (workItemTypePk) references storyPoint.tbWorkItemType (pk),
    constraint fkTbPointHistoryWorkItem foreign key (workItemPk) references storyPoint.tbWorkItem (pk),
    constraint fkTbPointHistoryVotingSession foreign key (votingSessionPk) references storyPoint.tbVotingSession (pk),
    constraint fkTbPointHistoryChangedBy foreign key (changedByEmployeePk) references storyPoint.tbEmployee (pk)
);
go

-- ============================================================================
-- 階段四：若暫存保留表 (tbPreserve*) 存在資料，自動無損還原回新建資料表
-- ============================================================================
if object_id('storyPoint.tbPreserveDepartment', 'u') is not null
begin
    set identity_insert storyPoint.tbDepartment on;
    insert into storyPoint.tbDepartment (
        pk, departmentCode, departmentName, parentDepartmentPk, managerName,
        departmentNote, isActive, createdAt, updatedAt
    )
    select
        pk, departmentCode, departmentName, parentDepartmentPk, managerName,
        departmentNote, isActive, createdAt, updatedAt
    from storyPoint.tbPreserveDepartment;
    set identity_insert storyPoint.tbDepartment off;
end

if object_id('storyPoint.tbPreserveEmployee', 'u') is not null
begin
    set identity_insert storyPoint.tbEmployee on;
    insert into storyPoint.tbEmployee (
        pk, employeeCode, employeeName, loginAccount, passwordHash,
        emailAddress, jobTitle, departmentPk, roleName, isActive,
        lastLoginTime, createdAt, updatedAt
    )
    select
        pk, employeeCode, employeeName, loginAccount, passwordHash,
        emailAddress, jobTitle, departmentPk, roleName, isActive,
        lastLoginTime, createdAt, updatedAt
    from storyPoint.tbPreserveEmployee;
    set identity_insert storyPoint.tbEmployee off;
end

if object_id('storyPoint.tbPreserveCardDeck', 'u') is not null
begin
    set identity_insert storyPoint.tbCardDeck on;
    insert into storyPoint.tbCardDeck (
        pk, deckCode, deckName, cardValueCsv, description,
        isDefault, isActive, createdAt, updatedAt
    )
    select
        pk, deckCode, deckName,
        case
            when cardValueCsv like '%?,?' then stuff(cast(cardValueCsv as nvarchar(256)), len(cardValueCsv), 1, N'☕')
            else cast(cardValueCsv as nvarchar(256))
        end,
        description,
        isDefault, isActive, createdAt, updatedAt
    from storyPoint.tbPreserveCardDeck;
    set identity_insert storyPoint.tbCardDeck off;
end

if object_id('storyPoint.tbPreserveWorkItemKind', 'u') is not null
begin
    set identity_insert storyPoint.tbWorkItemKind on;
    insert into storyPoint.tbWorkItemKind (
        pk, kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        description, isActive, createdAt, updatedAt
    )
    select
        pk, kindCode, kindName, badgeIcon, badgeColor, sortOrder,
        description, isActive, createdAt, updatedAt
    from storyPoint.tbPreserveWorkItemKind;
    set identity_insert storyPoint.tbWorkItemKind off;
end

if object_id('storyPoint.tbPreserveWorkItemType', 'u') is not null
begin
    set identity_insert storyPoint.tbWorkItemType on;
    insert into storyPoint.tbWorkItemType (
        pk, typeCode, typeName, categoryName, defaultWorkItemKind, description,
        standardStoryPoint, defaultCardDeckPk, estimationStatus,
        lastVotingSessionPk, isActive, createdAt, updatedAt
    )
    select
        pk, typeCode, typeName, categoryName, isnull(defaultWorkItemKind, 'project'), description,
        standardStoryPoint, defaultCardDeckPk, estimationStatus,
        lastVotingSessionPk, isActive, createdAt, updatedAt
    from storyPoint.tbPreserveWorkItemType;
    set identity_insert storyPoint.tbWorkItemType off;
end

if object_id('storyPoint.tbPreserveWorkItem', 'u') is not null
begin
    set identity_insert storyPoint.tbWorkItem on;
    insert into storyPoint.tbWorkItem (
        pk, itemCode, itemTitle, itemDescription, workItemTypePk,
        workItemKind, sprintName, priorityLevel, defaultTypePoint, storyPoint,
        originalStoryPoint, deductedStoryPoint, isManualAdjusted, adjustmentReason,
        estimationStatus, executionStatus, startDate, endDate, dueDate,
        isExpiredProcessed, expiredProcessedAt, expiryNotificationSent,
        expiryNotificationSentAt, expiryNotificationRecipient,
        createdByEmployeePk, isActive, createdAt, updatedAt
    )
    select
        pk, itemCode, itemTitle, itemDescription, workItemTypePk,
        isnull(workItemKind, 'project'), sprintName, priorityLevel, defaultTypePoint, storyPoint,
        originalStoryPoint, isnull(deductedStoryPoint, 0.00), isManualAdjusted, adjustmentReason,
        estimationStatus, executionStatus,
        isnull(startDate, cast(createdAt as datetime2(0))),
        case
            when endDate is not null then endDate
            when dueDate is not null then dateadd(second, 86399, cast(dueDate as datetime2(0)))
            else null
        end,
        dueDate,
        isnull(isExpiredProcessed, 0), expiredProcessedAt, isnull(expiryNotificationSent, 0),
        expiryNotificationSentAt, expiryNotificationRecipient,
        createdByEmployeePk, isActive, createdAt, updatedAt
    from storyPoint.tbPreserveWorkItem;
    set identity_insert storyPoint.tbWorkItem off;
end

if object_id('storyPoint.tbPreserveWorkItemAssignment', 'u') is not null
begin
    set identity_insert storyPoint.tbWorkItemAssignment on;
    insert into storyPoint.tbWorkItemAssignment (
        pk, workItemPk, employeePk, assignmentRole, allocationPercent,
        allocatedStoryPoint, originalAllocatedStoryPoint, deductedAllocatedStoryPoint,
        assignmentNote, assignedByEmployeePk, assignedAt, updatedAt
    )
    select
        pk, workItemPk, employeePk, assignmentRole, allocationPercent,
        allocatedStoryPoint, originalAllocatedStoryPoint, isnull(deductedAllocatedStoryPoint, 0.00),
        assignmentNote, assignedByEmployeePk, assignedAt, updatedAt
    from storyPoint.tbPreserveWorkItemAssignment;
    set identity_insert storyPoint.tbWorkItemAssignment off;
end

if object_id('storyPoint.tbPreserveVotingSession', 'u') is not null
begin
    set identity_insert storyPoint.tbVotingSession on;
    insert into storyPoint.tbVotingSession (
        pk, sessionCode, sessionTitle, targetKind, workItemTypePk,
        workItemPk, cardDeckPk, hostEmployeePk, currentRoundNumber,
        sessionStatus, finalStoryPoint, finalDecisionNote,
        minStoryPoint, maxStoryPoint, maxAllowedSpread, consensusRule,
        autoRevealWhenAllVoted, roundTimeLimitSecond, allowQuestionCard,
        allowCoffeeCard, voteCommentPolicy, restrictToParticipant, roundGoalNote,
        startedAt, finalizedAt, createdAt, updatedAt
    )
    select
        pk, sessionCode, sessionTitle, targetKind, workItemTypePk,
        workItemPk, cardDeckPk, hostEmployeePk, currentRoundNumber,
        sessionStatus, finalStoryPoint, finalDecisionNote,
        minStoryPoint, maxStoryPoint, maxAllowedSpread, consensusRule,
        cast(1 as bit), roundTimeLimitSecond, allowQuestionCard,
        allowCoffeeCard, voteCommentPolicy, restrictToParticipant, roundGoalNote,
        startedAt, finalizedAt, createdAt, updatedAt
    from storyPoint.tbPreserveVotingSession;
    set identity_insert storyPoint.tbVotingSession off;
end

if object_id('storyPoint.tbPreserveVotingSessionParticipant', 'u') is not null
begin
    set identity_insert storyPoint.tbVotingSessionParticipant on;
    insert into storyPoint.tbVotingSessionParticipant (
        pk, votingSessionPk, employeePk, isRequiredVoter, joinedAt
    )
    select
        pk, votingSessionPk, employeePk, isRequiredVoter, joinedAt
    from storyPoint.tbPreserveVotingSessionParticipant;
    set identity_insert storyPoint.tbVotingSessionParticipant off;
end

if object_id('storyPoint.tbPreserveVotingRound', 'u') is not null
begin
    set identity_insert storyPoint.tbVotingRound on;
    insert into storyPoint.tbVotingRound (
        pk, votingSessionPk, roundNumber, roundStatus, averagePoint,
        minPoint, maxPoint, voteCount, roundDiscussionNote,
        startedAt, revealedAt
    )
    select
        pk, votingSessionPk, roundNumber, roundStatus, averagePoint,
        minPoint, maxPoint, voteCount, roundDiscussionNote,
        startedAt, revealedAt
    from storyPoint.tbPreserveVotingRound;
    set identity_insert storyPoint.tbVotingRound off;
end

if object_id('storyPoint.tbPreserveVoteRecord', 'u') is not null
begin
    set identity_insert storyPoint.tbVoteRecord on;
    insert into storyPoint.tbVoteRecord (
        pk, votingRoundPk, votingSessionPk, roundNumber, employeePk,
        cardValue, numericPointValue, voteComment, votedAt, updatedAt
    )
    select
        pk, votingRoundPk, votingSessionPk, roundNumber, employeePk,
        cardValue, numericPointValue, voteComment, votedAt, updatedAt
    from storyPoint.tbPreserveVoteRecord;
    set identity_insert storyPoint.tbVoteRecord off;
end

if object_id('storyPoint.tbPreservePointHistory', 'u') is not null
begin
    set identity_insert storyPoint.tbPointHistory on;
    insert into storyPoint.tbPointHistory (
        pk, targetKind, workItemTypePk, workItemPk, votingSessionPk,
        oldStoryPoint, newStoryPoint, changeSource, changeReason,
        changedByEmployeePk, changedAt
    )
    select
        pk, targetKind, workItemTypePk, workItemPk, votingSessionPk,
        oldStoryPoint, newStoryPoint, changeSource, changeReason,
        changedByEmployeePk, changedAt
    from storyPoint.tbPreservePointHistory;
    set identity_insert storyPoint.tbPointHistory off;
end

if object_id('storyPoint.tbPreserveLog', 'u') is not null
begin
    set identity_insert storyPoint.tbLog on;
    insert into storyPoint.tbLog (
        pk, logTime, logLevel, sourceName, objectName,
        durationMillisecond, executionResult, errorCode, errorLine,
        errorMessage, operatorAccount
    )
    select
        pk, logTime, logLevel, sourceName, objectName,
        durationMillisecond, executionResult, errorCode, errorLine,
        errorMessage, operatorAccount
    from storyPoint.tbPreserveLog;
    set identity_insert storyPoint.tbLog off;
end
go

drop table if exists storyPoint.tbPreservePointHistory;
drop table if exists storyPoint.tbPreserveVoteRecord;
drop table if exists storyPoint.tbPreserveVotingRound;
drop table if exists storyPoint.tbPreserveVotingSessionParticipant;
drop table if exists storyPoint.tbPreserveVotingSession;
drop table if exists storyPoint.tbPreserveWorkItemAssignment;
drop table if exists storyPoint.tbPreserveWorkItem;
drop table if exists storyPoint.tbPreserveWorkItemType;
drop table if exists storyPoint.tbPreserveWorkItemKind;
drop table if exists storyPoint.tbPreserveCardDeck;
drop table if exists storyPoint.tbPreserveEmployee;
drop table if exists storyPoint.tbPreserveDepartment;
drop table if exists storyPoint.tbPreserveLog;
go
