<#
.SYNOPSIS
    員工列表、登入帳密與部門管理 Console 模組 (manageEmployee.ps1)
.DESCRIPTION
    提供管理部門、查詢員工列表及身上的 Story Point、新增/修改員工與登入帳密、
    驗證帳號密碼、以及 CSV 批次匯入與匯出員工名單之完整參數化模組。
.PARAMETER actionType
    執行動作：
    - 'listEmployee'     : 查詢員工清單與個人總 Story Point 數
    - 'listDepartment'   : 查詢部門清單與部門總 Story Point 數
    - 'upsertDepartment' : 新增或修改部門
    - 'upsertEmployee'   : 新增或修改員工與登入帳密
    - 'verifyLogin'      : 驗證員工登入帳號密碼
    - 'exportCsv'        : 匯出員工與 Story Point 統計至 CSV
    - 'importCsv'        : 從 CSV 檔案批次匯入員工與帳密
.NOTES
    檔案名稱: manageEmployee.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [validateset('listEmployee', 'listDepartment', 'upsertDepartment', 'upsertEmployee', 'setActive', 'deleteEmployee', 'verifyLogin', 'exportCsv', 'importCsv')]
    [string]$actionType = 'listEmployee',

    [parameter(mandatory = $false)]
    [int]$departmentPk = 0,

    [parameter(mandatory = $false)]
    [string]$departmentCode = '',

    [parameter(mandatory = $false)]
    [string]$departmentName = '',

    [parameter(mandatory = $false)]
    [int]$parentDepartmentPk = 0,

    [parameter(mandatory = $false)]
    [string]$managerName = '',

    [parameter(mandatory = $false)]
    [string]$departmentNote = '',

    [parameter(mandatory = $false)]
    [int]$employeePk = 0,

    [parameter(mandatory = $false)]
    [string]$employeeCode = '',

    [parameter(mandatory = $false)]
    [string]$employeeName = '',

    [parameter(mandatory = $false)]
    [string]$loginAccount = '',

    [parameter(mandatory = $false)]
    [string]$plainPassword = '',

    [parameter(mandatory = $false)]
    [string]$emailAddress = '',

    [parameter(mandatory = $false)]
    [string]$jobTitle = '',

    [parameter(mandatory = $false)]
    [validateset('admin', 'participant')]
    [string]$roleName = 'participant',

    [parameter(mandatory = $false)]
    [bool]$isActive = $true,

    [parameter(mandatory = $false)]
    [string]$csvFilePath = '',

    [parameter(mandatory = $false)]
    [string]$keyword = '',

    [parameter(mandatory = $false)]
    [string]$operatorAccount = $env:username
)

. (join-path -path $psscriptroot -childpath 'common.ps1')

switch ($actionType) {
    'listDepartment' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{
            departmentPk = $(if ($departmentPk -gt 0) { $departmentPk } else { $null })
        }
        writeSystemLog -logLevel 3 -sourceScript 'manageEmployee.ps1' -objectName 'listDepartment' `
            -executionResult "查詢部門清單與總 Story Point 完成，共 $($ds.tables[0].rows.count) 個部門" -operatorAccount $operatorAccount
        return ,$ds.tables[0]
    }

    'listEmployee' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{
            departmentPk = $(if ($departmentPk -gt 0) { $departmentPk } else { $null })
            employeePk = $(if ($employeePk -gt 0) { $employeePk } else { $null })
            keyword = $(if (-not [string]::isnullorempty($keyword)) { $keyword } else { $null })
        }
        writeSystemLog -logLevel 3 -sourceScript 'manageEmployee.ps1' -objectName 'listEmployee' `
            -executionResult "查詢員工清單與個人 Story Point 完成，共 $($ds.tables[1].rows.count) 位員工" -operatorAccount $operatorAccount
        return ,$ds.tables[1]
    }

    'upsertDepartment' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spUpsertDepartment' -parameters @{
            departmentPk = $(if ($departmentPk -gt 0) { $departmentPk } else { $null })
            departmentCode = $departmentCode
            departmentName = $departmentName
            parentDepartmentPk = $(if ($parentDepartmentPk -gt 0) { $parentDepartmentPk } else { $null })
            managerName = $managerName
            departmentNote = $departmentNote
            isActive = $isActive
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'upsertEmployee' {
        $pwdHash = if (-not [string]::isnullorempty($plainPassword)) {
            getSha256Hash -plainText $plainPassword
        }
        else {
            ''
        }
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spUpsertEmployee' -parameters @{
            employeePk = $(if ($employeePk -gt 0) { $employeePk } else { $null })
            employeeCode = $employeeCode
            employeeName = $employeeName
            loginAccount = $loginAccount
            passwordHash = $pwdHash
            emailAddress = $emailAddress
            jobTitle = $jobTitle
            departmentPk = $departmentPk
            roleName = $roleName
            isActive = $isActive
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'setActive' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spSetEmployeeActiveStatus' -parameters @{
            employeePk = $employeePk
            isActive = $isActive
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'deleteEmployee' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spDeleteEmployee' -parameters @{
            employeePk = $employeePk
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'verifyLogin' {
        $pwdHash = getSha256Hash -plainText $plainPassword
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spVerifyEmployeeLogin' -parameters @{
            loginAccount = $loginAccount
            passwordHash = $pwdHash
        }
        return ,$ds.tables[0]
    }

    'exportCsv' {
        $config = getSystemConfig
        $rootPath = getProjectRootPath
        if ([string]::isnullorempty($csvFilePath)) {
            $repDir = join-path -path $rootPath -childpath $config.logSetting.reportOutputDirectory
            if (-not (test-path -path $repDir)) {
                $null = new-item -itemtype directory -path $repDir -force
            }
            $ts = (get-date).tostring('yyyyMMddHHmmss')
            $csvFilePath = join-path -path $repDir -childpath "employeeStoryPoint_$ts.csv"
        }

        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{
            departmentPk = $(if ($departmentPk -gt 0) { $departmentPk } else { $null })
        }
        $empTable = $ds.tables[1]

        $exportList = foreach ($row in $empTable.rows) {
            [pscustomobject]@{
                employeeCode = $row.employeeCode
                employeeName = $row.employeeName
                loginAccount = $row.loginAccount
                emailAddress = $row.emailAddress
                jobTitle = $row.jobTitle
                departmentCode = $row.departmentCode
                departmentName = $row.departmentName
                roleName = $row.roleName
                isActive = $row.isActive
                assignedItemCount = $row.assignedItemCount
                totalAllocatedStoryPoint = $row.totalAllocatedStoryPoint
                todoAllocatedStoryPoint = $row.todoAllocatedStoryPoint
                inProgressAllocatedStoryPoint = $row.inProgressAllocatedStoryPoint
                doneAllocatedStoryPoint = $row.doneAllocatedStoryPoint
            }
        }

        $csvLines = $exportList | convertto-csv -notypeinformation
        saveFileWithStandardEncoding -filePath $csvFilePath -content ($csvLines -join "`r`n")

        writeSystemLog -logLevel 3 -sourceScript 'manageEmployee.ps1' -objectName 'exportCsv' `
            -executionResult "成功匯出員工與 Story Point CSV 報表：$csvFilePath" -operatorAccount $operatorAccount

        return [pscustomobject]@{
            status = 'success'
            csvFilePath = $csvFilePath
            rowCount = $empTable.rows.count
        }
    }

    'importCsv' {
        if (-not (test-path -path $csvFilePath)) {
            throw "找不到欲匯入的 CSV 檔案：$csvFilePath"
        }
        $rows = import-csv -path $csvFilePath -encoding utf8
        $totalCount = @($rows).count
        $idx = 0

        # 取得現有部門對照
        $deptDs = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetStoryPointDashboard' -parameters @{}
        $deptMap = @{}
        foreach ($dRow in $deptDs.tables[0].rows) {
            $deptMap[$dRow.departmentCode] = [int]$dRow.departmentPk
            $deptMap[$dRow.departmentName] = [int]$dRow.departmentPk
        }

        foreach ($item in $rows) {
            $idx++
            write-progress -activity '批次匯入員工名單' -status "正在處理 $($item.employeeName) ($idx/$totalCount)..." -percentcomplete ([int](($idx / [math]::max($totalCount, 1)) * 100))

            $resolvedDeptPk = 1
            if ($null -ne $item.departmentCode -and $deptMap.containskey($item.departmentCode)) {
                $resolvedDeptPk = $deptMap[$item.departmentCode]
            }
            elseif ($null -ne $item.departmentName -and $deptMap.containskey($item.departmentName)) {
                $resolvedDeptPk = $deptMap[$item.departmentName]
            }

            $rawPwd = if ($null -ne $item.plainPassword -and -not [string]::isnullorempty($item.plainPassword)) { $item.plainPassword } else { 'pass123' }
            $pwdHash = getSha256Hash -plainText $rawPwd

            $null = invokeStoredProcedureDataSet -procedureName 'storyPoint.spUpsertEmployee' -parameters @{
                employeeCode = $item.employeeCode
                employeeName = $item.employeeName
                loginAccount = $item.loginAccount
                passwordHash = $pwdHash
                emailAddress = $item.emailAddress
                jobTitle = $item.jobTitle
                departmentPk = $resolvedDeptPk
                roleName = $(if ($item.roleName -eq 'admin') { 'admin' } else { 'participant' })
                isActive = $true
                operatorAccount = $operatorAccount
            }
        }
        write-progress -activity '批次匯入員工名單' -status '匯入完成' -percentcomplete 100 -completed

        writeSystemLog -logLevel 3 -sourceScript 'manageEmployee.ps1' -objectName 'importCsv' `
            -executionResult "成功從 $csvFilePath 批次匯入 $totalCount 筆員工資料" -operatorAccount $operatorAccount

        return [pscustomobject]@{
            status = 'success'
            importedCount = $totalCount
        }
    }
}
