<#
.SYNOPSIS
    工項代表類型、實際工項與員工賦予設定管理 Console 模組 (manageWorkItem.ps1)
.DESCRIPTION
    提供：
    1. 查詢與維護「工項代表類型 (tbWorkItemType)」(投票時投工項的代表類型)
    2. 建立與修改「實際工項 (tbWorkItem)」(選擇已賦予 Story Point 的工項代表類型即自動帶入點數，或手動調整)
    3. 管理者修改「員工賦予的工項 (tbWorkItemAssignment)」(設定負責員工與點數分攤比例)
.PARAMETER actionType
    執行動作：
    - 'listWorkItemType' : 查詢工項代表類型與標準 Story Point
    - 'upsertWorkItemType' : 新增或修改工項代表類型 (支援同步既有工項點數)
    - 'listWorkItem'     : 查詢各工項的 Story Point 與指派員工明細
    - 'upsertWorkItem'   : 建立或修改工項 (自動帶入代表類型點數或手動微調)
    - 'assignWorkItem'   : 修改員工賦予的工項與分攤比例
    - 'removeAssignment' : 移除員工身上的指定工項
.NOTES
    檔案名稱: manageWorkItem.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [validateset('listWorkItemType', 'upsertWorkItemType', 'listWorkItem', 'upsertWorkItem', 'assignWorkItem', 'removeAssignment', 'processExpired')]
    [string]$actionType = 'listWorkItem',

    [parameter(mandatory = $false)]
    [int]$workItemTypePk = 0,

    [parameter(mandatory = $false)]
    [string]$typeCode = '',

    [parameter(mandatory = $false)]
    [string]$typeName = '',

    [parameter(mandatory = $false)]
    [string]$categoryName = '一般類別',

    [parameter(mandatory = $false)]
    [validateset('project', 'maintenance', 'consultingHour')]
    [string]$defaultWorkItemKind = 'project',

    [parameter(mandatory = $false)]
    [string]$description = '',

    [parameter(mandatory = $false)]
    [nullable[decimal]]$standardStoryPoint = $null,

    [parameter(mandatory = $false)]
    [bool]$syncExistingOpenWorkItem = $false,

    [parameter(mandatory = $false)]
    [int]$workItemPk = 0,

    [parameter(mandatory = $false)]
    [string]$itemCode = '',

    [parameter(mandatory = $false)]
    [string]$itemTitle = '',

    [parameter(mandatory = $false)]
    [string]$itemDescription = '',

    [parameter(mandatory = $false)]
    [validateset('project', 'maintenance', 'consultingHour')]
    [string]$workItemKind = 'project',

    [parameter(mandatory = $false)]
    [nullable[datetime]]$startDate = $null,

    [parameter(mandatory = $false)]
    [nullable[datetime]]$endDate = $null,

    [parameter(mandatory = $false)]
    [string]$sprintName = '2026-Q4 Sprint 1',

    [parameter(mandatory = $false)]
    [validateset('high', 'medium', 'low')]
    [string]$priorityLevel = 'medium',

    [parameter(mandatory = $false)]
    [nullable[decimal]]$storyPoint = $null,

    [parameter(mandatory = $false)]
    [bool]$forceManualAdjust = $false,

    [parameter(mandatory = $false)]
    [string]$adjustmentReason = '',

    [parameter(mandatory = $false)]
    [validateset('', 'todo', 'inProgress', 'done')]
    [string]$executionStatus = '',

    [parameter(mandatory = $false)]
    [int]$employeePk = 0,

    [parameter(mandatory = $false)]
    [int]$departmentPk = 0,

    [parameter(mandatory = $false)]
    [string]$assignmentRole = '主要負責',

    [parameter(mandatory = $false)]
    [decimal]$allocationPercent = 100.00,

    [parameter(mandatory = $false)]
    [string]$assignmentNote = '',

    [parameter(mandatory = $false)]
    [bool]$replaceSingleAssignee = $false,

    [parameter(mandatory = $false)]
    [string]$keyword = '',

    [parameter(mandatory = $false)]
    [string]$operatorAccount = $env:username
)

. (join-path -path $psscriptroot -childpath 'common.ps1')

switch ($actionType) {
    'listWorkItemType' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
        }
        writeSystemLog -logLevel 3 -sourceScript 'manageWorkItem.ps1' -objectName 'listWorkItemType' `
            -executionResult "查詢工項代表類型完成，共 $($ds.tables[2].rows.count) 筆" -operatorAccount $operatorAccount
        return ,$ds.tables[2]
    }

    'upsertWorkItemType' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spUpsertWorkItemType' -parameters @{
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
            typeCode = $typeCode
            typeName = $typeName
            categoryName = $categoryName
            defaultWorkItemKind = $defaultWorkItemKind
            description = $description
            standardStoryPoint = $standardStoryPoint
            syncExistingOpenWorkItem = $syncExistingOpenWorkItem
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'listWorkItem' {
        $null = invokeExpiredWorkItemCheck -operatorAccount $operatorAccount
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{
            departmentPk = $(if ($departmentPk -gt 0) { $departmentPk } else { $null })
            employeePk = $(if ($employeePk -gt 0) { $employeePk } else { $null })
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
            executionStatus = $(if (-not [string]::isnullorempty($executionStatus)) { $executionStatus } else { $null })
            keyword = $(if (-not [string]::isnullorempty($keyword)) { $keyword } else { $null })
        }
        writeSystemLog -logLevel 3 -sourceScript 'manageWorkItem.ps1' -objectName 'listWorkItem' `
            -executionResult "查詢各工項 Story Point 完成，共 $($ds.tables[3].rows.count) 筆工項" -operatorAccount $operatorAccount
        return ,$ds.tables[3]
    }

    'upsertWorkItem' {
        $resolvedStatus = if ([string]::isnullorempty($executionStatus)) { 'todo' } else { $executionStatus }
        $resolvedStart = if ($null -ne $startDate) { $startDate } else { get-date }
        $resolvedEnd = if ($null -ne $endDate) { $endDate } else { $resolvedStart.adddays(14) }
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spUpsertWorkItem' -parameters @{
            workItemPk = $(if ($workItemPk -gt 0) { $workItemPk } else { $null })
            itemCode = $itemCode
            itemTitle = $itemTitle
            itemDescription = $itemDescription
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
            workItemKind = $workItemKind
            startDate = $resolvedStart
            endDate = $resolvedEnd
            sprintName = $sprintName
            priorityLevel = $priorityLevel
            storyPoint = $storyPoint
            forceManualAdjust = $forceManualAdjust
            adjustmentReason = $adjustmentReason
            executionStatus = $resolvedStatus
            dueDate = $resolvedEnd.date
            operatorAccount = $operatorAccount
        }
        $savedItem = $ds.tables[0]

        # 若同時指定了員工，一併執行賦予指派
        if ($employeePk -gt 0 -and $savedItem.rows.count -gt 0) {
            $newWiPk = [int]$savedItem.rows[0].pk
            $assignMode = if ($replaceSingleAssignee) { 'replaceSingle' } else { 'upsert' }
            $null = invokeStoredProcedureDataSet -procedureName 'storyPoint.spAssignWorkItemToEmployee' -parameters @{
                actionType = $assignMode
                workItemPk = $newWiPk
                employeePk = $employeePk
                assignmentRole = $assignmentRole
                allocationPercent = $allocationPercent
                assignmentNote = $assignmentNote
                operatorAccount = $operatorAccount
            }
        }

        # 儲存後立即檢核是否達到迄時：若已達迄時自動轉態為已完成、扣減 Story Point 並寄信給部門管理層與 HR
        $null = invokeExpiredWorkItemCheck -operatorAccount $operatorAccount
        return ,$savedItem
    }

    'processExpired' {
        $expiredDt = invokeExpiredWorkItemCheck -workItemPk $workItemPk -operatorAccount $operatorAccount
        return ,$expiredDt
    }

    'assignWorkItem' {
        $assignMode = if ($replaceSingleAssignee) { 'replaceSingle' } else { 'upsert' }
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spAssignWorkItemToEmployee' -parameters @{
            actionType = $assignMode
            workItemPk = $workItemPk
            employeePk = $employeePk
            assignmentRole = $assignmentRole
            allocationPercent = $allocationPercent
            assignmentNote = $assignmentNote
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'removeAssignment' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spAssignWorkItemToEmployee' -parameters @{
            actionType = 'remove'
            workItemPk = $workItemPk
            employeePk = $employeePk
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }
}
