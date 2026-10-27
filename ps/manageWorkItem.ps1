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
    - 'listWorkItemIcon'   : 查詢代表圖示庫 (tbWorkItemIcon，含內建 SVG 彩色圖示、Emoji 與使用者自訂上傳圖示)
    - 'uploadWorkItemIcon' : 自行上傳並新增代表圖示至代表圖示庫 (tbWorkItemIcon) 與 Web 圖示目錄
    - 'deleteWorkItemIcon' : 刪除自訂上傳的代表圖示
    - 'listWorkItemKind'   : 查詢工項類型設定 (專案/維運/顧問時數/教育訓練/開發產品/自訂類型)
    - 'upsertWorkItemKind' : 新增或修改工項類型 (支援指定 -iconFilePath 同時上傳自訂圖示)
    - 'deleteWorkItemKind' : 刪除工項類型
    - 'listWorkItemType'   : 查詢工項代表類型與標準 Story Point
    - 'upsertWorkItemType' : 新增或修改工項代表類型 (支援同步既有工項點數)
    - 'deleteWorkItemType' : 刪除工項代表類型
    - 'listWorkItem'       : 查詢各工項的 Story Point 與指派員工明細
    - 'upsertWorkItem'     : 建立或修改工項 (自動帶入代表類型點數或手動微調)
    - 'deleteWorkItem'     : 刪除實際工項
    - 'assignWorkItem'     : 修改員工賦予的工項與分攤比例
    - 'removeAssignment'   : 移除員工身上的指定工項
    - 'processExpired'     : 檢核並處理達到迄時之工項
.NOTES
    檔案名稱: manageWorkItem.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [validateset('listWorkItemIcon', 'uploadWorkItemIcon', 'deleteWorkItemIcon', 'listWorkItemKind', 'upsertWorkItemKind', 'deleteWorkItemKind', 'listWorkItemType', 'upsertWorkItemType', 'deleteWorkItemType', 'listWorkItem', 'upsertWorkItem', 'deleteWorkItem', 'assignWorkItem', 'removeAssignment', 'processExpired')]
    [string]$actionType = 'listWorkItem',

    [parameter(mandatory = $false)]
    [int]$workItemIconPk = 0,

    [parameter(mandatory = $false)]
    [string]$iconCode = '',

    [parameter(mandatory = $false)]
    [string]$iconName = '',

    [parameter(mandatory = $false)]
    [string]$iconCategory = '自訂上傳圖示',

    [parameter(mandatory = $false)]
    [string]$iconFilePath = '',

    [parameter(mandatory = $false)]
    [int]$workItemKindPk = 0,

    [parameter(mandatory = $false)]
    [string]$kindCode = '',

    [parameter(mandatory = $false)]
    [string]$kindName = '',

    [parameter(mandatory = $false)]
    [string]$badgeIcon = '📌',

    [parameter(mandatory = $false)]
    [string]$badgeColor = 'blue',

    [parameter(mandatory = $false)]
    [int]$sortOrder = 100,

    [parameter(mandatory = $false)]
    [string]$mainFeatureSummary = '',

    [parameter(mandatory = $false)]
    [string]$estimationTrait = '',

    [parameter(mandatory = $false)]
    [string]$timeAndDeliverableTrait = '',

    [parameter(mandatory = $false)]
    [int]$workItemTypePk = 0,

    [parameter(mandatory = $false)]
    [string]$typeCode = '',

    [parameter(mandatory = $false)]
    [string]$typeName = '',

    [parameter(mandatory = $false)]
    [string]$categoryName = '一般類別',

    [parameter(mandatory = $false)]
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

function saveCustomWorkItemIconFromFile {
    param(
        [string]$sourceFilePath,
        [string]$customName,
        [string]$customCategory = '自訂上傳圖示',
        [string]$opAccount = $env:username
    )
    if ([string]::isnullorempty($sourceFilePath) -or -not (test-path -literalpath $sourceFilePath)) {
        throw "找不到指定的圖示檔案：$sourceFilePath"
    }
    $fileInfo = get-item -literalpath $sourceFilePath
    $ext = $fileInfo.extension.tolowerinvariant()
    $allowedExts = @('.png', '.svg', '.jpg', '.jpeg', '.webp', '.ico', '.gif')
    if ($allowedExts -notcontains $ext) {
        throw "不支援的圖示檔案格式 ($ext)，請選擇 PNG、SVG、JPG、WEBP、ICO 或 GIF 圖檔。"
    }
    if ($fileInfo.length -gt 2097152) {
        throw '圖示檔案大小不可超過 2 MB。'
    }

    $bytes = [system.io.file]::readallbytes($fileInfo.fullname)
    $b64 = [convert]::tobase64string($bytes)
    $mimeType = switch ($ext) {
        '.svg'  { 'image/svg+xml' }
        '.png'  { 'image/png' }
        '.jpg'  { 'image/jpeg' }
        '.jpeg' { 'image/jpeg' }
        '.webp' { 'image/webp' }
        '.ico'  { 'image/x-icon' }
        '.gif'  { 'image/gif' }
        default { 'application/octet-stream' }
    }

    $stamp = (get-date).tostring('yyyyMMddHHmmssfff')
    $rawStem = [system.io.path]::getfilenamewithoutextension($fileInfo.name) -replace '[^a-zA-Z0-9_-]', ''
    if ([string]::isnullorempty($rawStem)) { $rawStem = 'icon' }
    if ($rawStem.length -gt 24) { $rawStem = $rawStem.substring(0, 24) }
    $savedFileName = "custom_${rawStem}_${stamp}${ext}"
    $relUrl = "/icons/custom/$savedFileName"

    $repoRoot = split-path -path $psscriptroot -parent
    $customDir = join-path -path $repoRoot -childpath 'web\storyPointPortal\wwwroot\icons\custom'
    if (-not (test-path -literalpath $customDir)) {
        $null = new-item -itemtype directory -path $customDir -force
    }
    $destPath = join-path -path $customDir -childpath $savedFileName
    [system.io.file]::writeallbytes($destPath, $bytes)

    $resolvedName = if (-not [string]::isnullorempty($customName)) { $customName } else { [system.io.path]::getfilenamewithoutextension($fileInfo.name) }
    $resolvedCat = if (-not [string]::isnullorempty($customCategory)) { $customCategory } else { '自訂上傳圖示' }
    $genCode = "custom_$stamp"

    $dsIcon = invokeStoredProcedureDataSet -procedureName 'storyPoint.spUpsertWorkItemIcon' -parameters @{
        iconPk = $null
        iconCode = $genCode
        iconName = $resolvedName
        iconCategory = $resolvedCat
        iconKind = 'customUpload'
        iconValue = $relUrl
        mimeType = $mimeType
        iconDataBase64 = $b64
        sortOrder = 5
        isCustomUpload = $true
        operatorAccount = $opAccount
    }
    writeSystemLog -logLevel 3 -sourceScript 'manageWorkItem.ps1' -objectName 'uploadWorkItemIcon' `
        -executionResult "上傳自訂代表圖示成功：$resolvedName ($relUrl)" -operatorAccount $opAccount
    return ,$dsIcon.tables[0]
}

switch ($actionType) {
    'listWorkItemIcon' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{}
        $iconTable = $ds.tables[9]
        if (-not [string]::isnullorempty($keyword)) {
            $kwEsc = $keyword.replace("'", "''")
            $dv = $iconTable.defaultview
            $dv.rowfilter = "iconCode like '%$kwEsc%' or iconName like '%$kwEsc%' or iconCategory like '%$kwEsc%' or iconValue like '%$kwEsc%'"
            $iconTable = $dv.totable()
        }
        writeSystemLog -logLevel 3 -sourceScript 'manageWorkItem.ps1' -objectName 'listWorkItemIcon' `
            -executionResult "查詢代表圖示庫完成，共 $($iconTable.rows.count) 筆代表圖示" -operatorAccount $operatorAccount
        return ,$iconTable
    }

    'uploadWorkItemIcon' {
        $savedIconDt = saveCustomWorkItemIconFromFile -sourceFilePath $iconFilePath -customName $iconName -customCategory $iconCategory -opAccount $operatorAccount
        return ,$savedIconDt
    }

    'deleteWorkItemIcon' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spDeleteWorkItemIcon' -parameters @{
            iconPk = $(if ($workItemIconPk -gt 0) { $workItemIconPk } else { $null })
            iconCode = $(if (-not [string]::isnullorempty($iconCode)) { $iconCode } else { $null })
            operatorAccount = $operatorAccount
        }
        if ($ds.tables.count -gt 0 -and $ds.tables[0].rows.count -gt 0) {
            $delVal = [string]$ds.tables[0].rows[0].deletedIconValue
            if ($delVal.startswith('/icons/custom/')) {
                $delFileName = [system.io.path]::getfilename($delVal)
                $repoRoot = split-path -path $psscriptroot -parent
                $delFullPath = join-path -path $repoRoot -childpath "web\storyPointPortal\wwwroot\icons\custom\$delFileName"
                if (test-path -literalpath $delFullPath) {
                    remove-item -literalpath $delFullPath -force -erroraction silentlycontinue
                }
            }
        }
        return ,$ds.tables[0]
    }

    'listWorkItemKind' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{}
        $kindTable = $ds.tables[8]
        if (-not [string]::isnullorempty($keyword)) {
            $kwEsc = $keyword.replace("'", "''")
            $dv = $kindTable.defaultview
            $dv.rowfilter = "kindCode like '%$kwEsc%' or kindName like '%$kwEsc%' or mainFeatureSummary like '%$kwEsc%' or estimationTrait like '%$kwEsc%' or timeAndDeliverableTrait like '%$kwEsc%' or description like '%$kwEsc%'"
            $kindTable = $dv.totable()
        }
        writeSystemLog -logLevel 3 -sourceScript 'manageWorkItem.ps1' -objectName 'listWorkItemKind' `
            -executionResult "查詢預設工項類型與主要特徵完成，共 $($kindTable.rows.count) 筆" -operatorAccount $operatorAccount
        return ,$kindTable
    }

    'upsertWorkItemKind' {
        $resolvedBadgeIcon = $badgeIcon
        if (-not [string]::isnullorempty($iconFilePath) -and (test-path -literalpath $iconFilePath)) {
            $upDt = saveCustomWorkItemIconFromFile -sourceFilePath $iconFilePath -customName $(if (-not [string]::isnullorempty($iconName)) { $iconName } else { $kindName }) -customCategory $iconCategory -opAccount $operatorAccount
            if ($null -ne $upDt -and $upDt.rows.count -gt 0) {
                $resolvedBadgeIcon = [string]$upDt.rows[0].iconValue
            }
        }
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spUpsertWorkItemKind' -parameters @{
            workItemKindPk = $(if ($workItemKindPk -gt 0) { $workItemKindPk } else { $null })
            kindCode = $kindCode
            kindName = $kindName
            badgeIcon = $resolvedBadgeIcon
            badgeColor = $badgeColor
            sortOrder = $sortOrder
            mainFeatureSummary = $(if (-not [string]::isnullorempty($mainFeatureSummary)) { $mainFeatureSummary } else { $null })
            estimationTrait = $(if (-not [string]::isnullorempty($estimationTrait)) { $estimationTrait } else { $null })
            timeAndDeliverableTrait = $(if (-not [string]::isnullorempty($timeAndDeliverableTrait)) { $timeAndDeliverableTrait } else { $null })
            description = $description
            isActive = $true
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'deleteWorkItemKind' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spDeleteWorkItemKind' -parameters @{
            workItemKindPk = $(if ($workItemKindPk -gt 0) { $workItemKindPk } else { $null })
            kindCode = $(if (-not [string]::isnullorempty($kindCode)) { $kindCode } else { $null })
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

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

    'deleteWorkItemType' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spDeleteWorkItemType' -parameters @{
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
            typeCode = $(if (-not [string]::isnullorempty($typeCode)) { $typeCode } else { $null })
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

    'deleteWorkItem' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spDeleteWorkItem' -parameters @{
            workItemPk = $(if ($workItemPk -gt 0) { $workItemPk } else { $null })
            itemCode = $(if (-not [string]::isnullorempty($itemCode)) { $itemCode } else { $null })
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
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
