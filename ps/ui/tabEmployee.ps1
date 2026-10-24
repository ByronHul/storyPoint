<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - [Tab 4] 員工列表、登入帳密與部門管理頁籤 (tabEmployee.ps1)
.DESCRIPTION
    獨立實作「員工列表、每個人的登入帳密、所屬部門與角色管理」頁籤。
    所有功能一律呼叫 ps/manageEmployee.ps1。
.PARAMETER psRootPath
    ps 模組根目錄路徑
.PARAMETER statusLabel
    主視窗底部狀態列控制項
.OUTPUTS
    System.Windows.Forms.TabPage
.NOTES
    檔案名稱: tabEmployee.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $true)]
    [string]$psRootPath,
    [parameter(mandatory = $false)]
    $statusLabel = $null
)

process {
    $empScript = join-path -path $psRootPath -childpath 'manageEmployee.ps1'
    $tabPage = new-object system.windows.forms.tabpage
    $tabPage.text = '4. 員工帳密與部門管理'
    $tabPage.padding = new-object system.windows.forms.padding(10)

    $lblCode = new-object system.windows.forms.label
    $lblCode.text = '員工編號：'
    $lblCode.location = new-object system.drawing.point(12, 16)
    $lblCode.autosize = $true

    $txtCode = new-object system.windows.forms.textbox
    $txtCode.location = new-object system.drawing.point(85, 13)
    $txtCode.width = 95
    $txtCode.text = 'EMP009'

    $lblName = new-object system.windows.forms.label
    $lblName.text = '姓名：'
    $lblName.location = new-object system.drawing.point(190, 16)
    $lblName.autosize = $true

    $txtName = new-object system.windows.forms.textbox
    $txtName.location = new-object system.drawing.point(240, 13)
    $txtName.width = 120
    $txtName.text = '吳宗憲 (Sam)'

    $lblAcc = new-object system.windows.forms.label
    $lblAcc.text = '登入帳號：'
    $lblAcc.location = new-object system.drawing.point(370, 16)
    $lblAcc.autosize = $true

    $txtAcc = new-object system.windows.forms.textbox
    $txtAcc.location = new-object system.drawing.point(445, 13)
    $txtAcc.width = 110
    $txtAcc.text = 'sam.wu'

    $lblPwd = new-object system.windows.forms.label
    $lblPwd.text = '登入密碼：'
    $lblPwd.location = new-object system.drawing.point(565, 16)
    $lblPwd.autosize = $true

    $txtPwd = new-object system.windows.forms.textbox
    $txtPwd.location = new-object system.drawing.point(640, 13)
    $txtPwd.width = 100
    $txtPwd.text = 'pass123'

    $lblDept = new-object system.windows.forms.label
    $lblDept.text = '部門 ID：'
    $lblDept.location = new-object system.drawing.point(750, 16)
    $lblDept.autosize = $true

    $txtDept = new-object system.windows.forms.textbox
    $txtDept.location = new-object system.drawing.point(815, 13)
    $txtDept.width = 45
    $txtDept.text = '2'

    $lblRole = new-object system.windows.forms.label
    $lblRole.text = '角色：'
    $lblRole.location = new-object system.drawing.point(860, 16)
    $lblRole.autosize = $true

    $cmbRole = new-object system.windows.forms.combobox
    $cmbRole.location = new-object system.drawing.point(905, 13)
    $cmbRole.width = 85
    $cmbRole.dropdownstyle = 'DropDownList'
    $null = $cmbRole.items.addrange(@('participant', 'admin'))
    $cmbRole.selectedindex = 0

    $lblTitle = new-object system.windows.forms.label
    $lblTitle.text = '職級：'
    $lblTitle.location = new-object system.drawing.point(995, 16)
    $lblTitle.autosize = $true

    $cmbTitle = new-object system.windows.forms.combobox
    $cmbTitle.location = new-object system.drawing.point(1040, 13)
    $cmbTitle.width = 105
    $cmbTitle.dropdownstyle = 'DropDown'
    $null = $cmbTitle.items.addrange(@('部門經理', '董事長兼部門經理', '部門經理 / 資深架構師', '部門經理 / 人資', '專案經理 (PM)', '資深工程師', '工程師', '人資專員 (HR)'))
    $cmbTitle.text = '部門經理'

    $btnSaveEmp = new-object system.windows.forms.button
    $btnSaveEmp.text = '💾 儲存員工與帳密'
    $btnSaveEmp.location = new-object system.drawing.point(12, 48)
    $btnSaveEmp.size = new-object system.drawing.size(165, 30)
    $btnSaveEmp.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnSaveEmp.forecolor = [system.drawing.color]::white

    $btnRefresh = new-object system.windows.forms.button
    $btnRefresh.text = '🔄 重新整理員工清單'
    $btnRefresh.location = new-object system.drawing.point(190, 48)
    $btnRefresh.size = new-object system.drawing.size(175, 30)

    $btnExportCsv = new-object system.windows.forms.button
    $btnExportCsv.text = '📥 匯出員工與點數 CSV'
    $btnExportCsv.location = new-object system.drawing.point(380, 48)
    $btnExportCsv.size = new-object system.drawing.size(190, 30)

    $lblTargetPk = new-object system.windows.forms.label
    $lblTargetPk.text = '目標員工 ID：'
    $lblTargetPk.location = new-object system.drawing.point(585, 54)
    $lblTargetPk.autosize = $true

    $txtTargetPk = new-object system.windows.forms.textbox
    $txtTargetPk.location = new-object system.drawing.point(675, 50)
    $txtTargetPk.width = 50
    $txtTargetPk.text = '3'

    $btnDisableEmp = new-object system.windows.forms.button
    $btnDisableEmp.text = '🚫 停用帳號'
    $btnDisableEmp.location = new-object system.drawing.point(735, 48)
    $btnDisableEmp.size = new-object system.drawing.size(110, 30)

    $btnEnableEmp = new-object system.windows.forms.button
    $btnEnableEmp.text = '✅ 啟用帳號'
    $btnEnableEmp.location = new-object system.drawing.point(855, 48)
    $btnEnableEmp.size = new-object system.drawing.size(110, 30)

    $btnDeleteEmp = new-object system.windows.forms.button
    $btnDeleteEmp.text = '🗑️ 刪除帳號'
    $btnDeleteEmp.location = new-object system.drawing.point(975, 48)
    $btnDeleteEmp.size = new-object system.drawing.size(110, 30)
    $btnDeleteEmp.backcolor = [system.drawing.color]::fromargb(220, 38, 38)
    $btnDeleteEmp.forecolor = [system.drawing.color]::white

    $chkIsDeptMgr = new-object system.windows.forms.checkbox
    $chkIsDeptMgr.text = '部門主管'
    $chkIsDeptMgr.location = new-object system.drawing.point(1090, 52)
    $chkIsDeptMgr.autosize = $true

    $lblMgrPk = new-object system.windows.forms.label
    $lblMgrPk.text = '上層主管 ID：'
    $lblMgrPk.location = new-object system.drawing.point(1170, 54)
    $lblMgrPk.autosize = $true

    $txtMgrPk = new-object system.windows.forms.textbox
    $txtMgrPk.location = new-object system.drawing.point(1255, 50)
    $txtMgrPk.width = 45
    $txtMgrPk.text = '0'

    $btnImportCsv = new-object system.windows.forms.button
    $btnImportCsv.text = '📤 批次載入 CSV'
    $btnImportCsv.location = new-object system.drawing.point(1155, 10)
    $btnImportCsv.size = new-object system.drawing.size(115, 30)
    $btnImportCsv.backcolor = [system.drawing.color]::fromargb(22, 163, 74)
    $btnImportCsv.forecolor = [system.drawing.color]::white

    $gridEmp = new-object system.windows.forms.datagridview
    $gridEmp.location = new-object system.drawing.point(12, 88)
    $gridEmp.size = new-object system.drawing.size(1290, 520)
    $gridEmp.readonly = $true
    $gridEmp.allowusertoaddrows = $false
    $gridEmp.autosizecolumnsmode = 'Fill'
    enableGridHeaderSortAndLock -grid $gridEmp -statusLabel $statusLabel

    $loadEmpData = {
        $dt = & $empScript -actionType 'listEmployee'
        $gridEmp.datasource = $dt
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已載入 $($dt.rows.count) 位員工（含部門主管標記與樹狀遞迴階層）與個人 Story Point 負載"
        }
    }

    $gridEmp.add_cellclick({
        param($sender, $e)
        if ($e.rowindex -ge 0 -and $null -ne $sender.rows[$e.rowindex].databounditem) {
            $rowItem = $sender.rows[$e.rowindex].databounditem.row
            if ($null -ne $rowItem) {
                $txtTargetPk.text = [string]$rowItem.employeePk
                $txtCode.text = [string]$rowItem.employeeCode
                $txtName.text = [string]$rowItem.employeeName
                $txtAcc.text = [string]$rowItem.loginAccount
                $txtDept.text = [string]$rowItem.departmentPk
                $cmbTitle.text = [string]$rowItem.jobTitle
                $chkIsDeptMgr.checked = [bool]$rowItem.isDepartmentManager
                $txtMgrPk.text = if ($null -ne $rowItem.managerEmployeePk -and $rowItem.managerEmployeePk -ne [dbnull]::value) { [string]$rowItem.managerEmployeePk } else { '0' }
                $txtPwd.text = ''
                if ([string]$rowItem.roleName -eq 'admin') {
                    $cmbRole.selectedindex = 1
                }
                else {
                    $cmbRole.selectedindex = 0
                }
                if ($null -ne $statusLabel) {
                    $statusLabel.text = "已載入員工【$($rowItem.employeeCode) - $($rowItem.employeeName) ($($rowItem.loginAccount))】至上方表單，可直接修改部門主管/樹狀上層主管或密碼後點擊「💾 儲存員工與帳密」"
                }
            }
        }
    })

    $btnRefresh.add_click($loadEmpData)
    $btnSaveEmp.add_click({
        $titleVal = if (-not [string]::isnullorempty($cmbTitle.text)) { $cmbTitle.text.trim() } else { '部門經理' }
        $mgrPkInt = 0
        [void][int]::tryparse($txtMgrPk.text, [ref]$mgrPkInt)
        $null = & $empScript -actionType 'upsertEmployee' `
            -employeeCode $txtCode.text.trim() -employeeName $txtName.text.trim() `
            -loginAccount $txtAcc.text.trim() -plainPassword $txtPwd.text.trim() `
            -emailAddress "$($txtAcc.text.trim())@company.com" -jobTitle $titleVal `
            -departmentPk ([int]$txtDept.text) `
            -isDepartmentManager $chkIsDeptMgr.checked -managerEmployeePk $mgrPkInt `
            -roleName $cmbRole.selecteditem.tostring()
        & $loadEmpData
    })
    $btnExportCsv.add_click({
        $res = & $empScript -actionType 'exportCsv'
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已匯出 CSV：$($res.csvFilePath)"
        }
    })
    $btnDisableEmp.add_click({
        $null = & $empScript -actionType 'setActive' -employeePk ([int]$txtTargetPk.text) -isActive $false
        & $loadEmpData
    })
    $btnEnableEmp.add_click({
        $null = & $empScript -actionType 'setActive' -employeePk ([int]$txtTargetPk.text) -isActive $true
        & $loadEmpData
    })
    $btnDeleteEmp.add_click({
        $null = & $empScript -actionType 'deleteEmployee' -employeePk ([int]$txtTargetPk.text)
        & $loadEmpData
    })
    $btnImportCsv.add_click({
        $dlg = new-object system.windows.forms.openfiledialog
        $dlg.filter = 'CSV 檔案 (*.csv)|*.csv|所有檔案 (*.*)|*.*'
        $dlg.title = '選擇要批次載入的員工名單 CSV 檔案'
        if ($dlg.showdialog() -eq [system.windows.forms.dialogresult]::ok) {
            $res = & $empScript -actionType 'importCsv' -csvFilePath $dlg.filename
            & $loadEmpData
            if ($null -ne $statusLabel) {
                $statusLabel.text = "已成功批次載入 $($res.importedCount) 筆員工資料！"
            }
        }
    })

    $tabPage.controls.addrange(@(
        $lblCode, $txtCode, $lblName, $txtName, $lblAcc, $txtAcc, $lblPwd, $txtPwd,
        $lblDept, $txtDept, $lblRole, $cmbRole, $lblTitle, $cmbTitle, $btnImportCsv,
        $btnSaveEmp, $btnRefresh, $btnExportCsv,
        $lblTargetPk, $txtTargetPk, $btnDisableEmp, $btnEnableEmp, $btnDeleteEmp,
        $chkIsDeptMgr, $lblMgrPk, $txtMgrPk,
        $gridEmp
    ))

    return $tabPage
}
