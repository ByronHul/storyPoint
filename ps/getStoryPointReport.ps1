<#
.SYNOPSIS
    查詢工項、員工與部門總 Story Point 統計與報表匯出 Console 模組 (getStoryPointReport.ps1)
.DESCRIPTION
    遵循開放封閉原則 (Open-Closed Principle)，提供查詢與匯出：
    1. 各部門總 Story Point 數與負載統計
    2. 各員工身上的工項與總 Story Point 數
    3. 各工項與代表類型的 Story Point 明細
    4. 訂出 Story Point 的投票過程歷史紀錄
    支援直接回傳 DataSet 或匯出至 reports/ 子目錄下含日期時間之 CSV 報表 (可直接由 bcp.exe 載入)。
.PARAMETER reportType
    報表類型：
    - 'allDashboard'    : 回傳完整儀表板 DataSet (6 張統計表)
    - 'departmentPoint' : 部門總 Story Point 統計表
    - 'employeePoint'   : 員工身上的 Story Point 統計表
    - 'workItemPoint'   : 各工項 Story Point 清單
    - 'votingProcess'   : 訂出 Story Point 的投票過程明細
    - 'exportAllCsv'    : 將所有報表批次匯出為含日期時間之繁體中文 CSV 檔案
.NOTES
    檔案名稱: getStoryPointReport.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [validateset('allDashboard', 'departmentPoint', 'employeePoint', 'workItemPoint', 'votingProcess', 'exportAllCsv')]
    [string]$reportType = 'allDashboard',

    [parameter(mandatory = $false)]
    [int]$departmentPk = 0,

    [parameter(mandatory = $false)]
    [int]$employeePk = 0,

    [parameter(mandatory = $false)]
    [int]$workItemTypePk = 0,

    [parameter(mandatory = $false)]
    [string]$executionStatus = '',

    [parameter(mandatory = $false)]
    [string]$keyword = '',

    [parameter(mandatory = $false)]
    [int]$viewerEmployeePk = 0,

    [parameter(mandatory = $false)]
    [bool]$isViewerAdmin = $true,

    [parameter(mandatory = $false)]
    [string]$operatorAccount = $env:username
)

. (join-path -path $psscriptroot -childpath 'common.ps1')

$dashboardDs = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{
    departmentPk = $(if ($departmentPk -gt 0) { $departmentPk } else { $null })
    employeePk = $(if ($employeePk -gt 0) { $employeePk } else { $null })
    workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
    executionStatus = $(if (-not [string]::isnullorempty($executionStatus)) { $executionStatus } else { $null })
    keyword = $(if (-not [string]::isnullorempty($keyword)) { $keyword } else { $null })
    viewerEmployeePk = $(if ($viewerEmployeePk -gt 0) { $viewerEmployeePk } else { $null })
    isViewerAdmin = $isViewerAdmin
}

switch ($reportType) {
    'allDashboard' {
        return ,$dashboardDs
    }
    'departmentPoint' {
        return ,$dashboardDs.tables[0]
    }
    'employeePoint' {
        return ,$dashboardDs.tables[1]
    }
    'workItemPoint' {
        return ,$dashboardDs.tables[3]
    }
    'votingProcess' {
        $voteDs = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetVotingProcessHistory' -parameters @{}
        return ,$voteDs
    }
    'exportAllCsv' {
        $config = getSystemConfig
        $rootPath = getProjectRootPath
        $repDir = join-path -path $rootPath -childpath $config.logSetting.reportOutputDirectory
        if (-not (test-path -path $repDir)) {
            $null = new-item -itemtype directory -path $repDir -force
        }

        $ts = (get-date).tostring('yyyyMMddHHmmss')
        $deptCsv = join-path -path $repDir -childpath "departmentStoryPoint_$ts.csv"
        $empCsv = join-path -path $repDir -childpath "employeeStoryPoint_$ts.csv"
        $itemCsv = join-path -path $repDir -childpath "workItemStoryPoint_$ts.csv"
        $voteCsv = join-path -path $repDir -childpath "votingProcessHistory_$ts.csv"

        $deptRows = foreach ($r in $dashboardDs.tables[0].rows) {
            [pscustomobject]@{
                departmentCode = $r.departmentCode
                departmentName = $r.departmentName
                managerName = $r.managerName
                activeEmployeeCount = $r.activeEmployeeCount
                distinctWorkItemCount = $r.distinctWorkItemCount
                totalDepartmentStoryPoint = $r.totalDepartmentStoryPoint
                todoDepartmentStoryPoint = $r.todoDepartmentStoryPoint
                inProgressDepartmentStoryPoint = $r.inProgressDepartmentStoryPoint
                doneDepartmentStoryPoint = $r.doneDepartmentStoryPoint
                averageStoryPointPerEmployee = $r.averageStoryPointPerEmployee
            }
        }
        saveFileWithStandardEncoding -filePath $deptCsv -content (($deptRows | convertto-csv -notypeinformation) -join "`r`n")

        $empRows = foreach ($r in $dashboardDs.tables[1].rows) {
            [pscustomobject]@{
                employeeCode = $r.employeeCode
                employeeName = $r.employeeName
                departmentName = $r.departmentName
                jobTitle = $r.jobTitle
                assignedItemCount = $r.assignedItemCount
                totalAllocatedStoryPoint = $r.totalAllocatedStoryPoint
                todoAllocatedStoryPoint = $r.todoAllocatedStoryPoint
                inProgressAllocatedStoryPoint = $r.inProgressAllocatedStoryPoint
                doneAllocatedStoryPoint = $r.doneAllocatedStoryPoint
                workItemListSummary = $r.workItemListSummary
            }
        }
        saveFileWithStandardEncoding -filePath $empCsv -content (($empRows | convertto-csv -notypeinformation) -join "`r`n")

        $itemRows = foreach ($r in $dashboardDs.tables[3].rows) {
            [pscustomobject]@{
                itemCode = $r.itemCode
                itemTitle = $r.itemTitle
                typeCode = $r.typeCode
                typeName = $r.typeName
                defaultTypePoint = $r.defaultTypePoint
                storyPoint = $r.storyPoint
                pointSourceText = $r.pointSourceText
                executionStatusText = $r.executionStatusText
                assigneeSummary = $r.assigneeSummary
                departmentSummary = $r.departmentSummary
            }
        }
        saveFileWithStandardEncoding -filePath $itemCsv -content (($itemRows | convertto-csv -notypeinformation) -join "`r`n")

        $voteDs = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetVotingProcessHistory' -parameters @{}
        $voteRows = foreach ($r in $voteDs.tables[2].rows) {
            [pscustomobject]@{
                sessionCode = $r.sessionCode
                roundNumber = $r.roundNumber
                roundStatus = $r.roundStatus
                employeeCode = $r.employeeCode
                employeeName = $r.employeeName
                departmentName = $r.departmentName
                displayCardValue = $r.displayCardValue
                numericPointValue = $r.numericPointValue
                voteComment = $r.voteComment
                votedAt = $r.votedAt
            }
        }
        saveFileWithStandardEncoding -filePath $voteCsv -content (($voteRows | convertto-csv -notypeinformation) -join "`r`n")

        writeSystemLog -logLevel 3 -sourceScript 'getStoryPointReport.ps1' -objectName 'exportAllCsv' `
            -executionResult "成功匯出部門、員工、工項與投票過程共 4 份 CSV 報表至 $repDir" -operatorAccount $operatorAccount

        return [pscustomobject]@{
            status = 'success'
            departmentCsvPath = $deptCsv
            employeeCsvPath = $empCsv
            workItemCsvPath = $itemCsv
            votingProcessCsvPath = $voteCsv
        }
    }
}
