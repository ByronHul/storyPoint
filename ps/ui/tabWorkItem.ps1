<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - [Tab 3] 工項管理与員工賦予設定頁籤 (tabWorkItem.ps1)
.DESCRIPTION
    獨立實作「建立工項自動帶入代表類型 Story Point 或手動調整、以及修改員工賦予的工項與分攤比例」頁籤。
    所有功能一律呼叫 ps/manageWorkItem.ps1。
.PARAMETER psRootPath
    ps 模組根目錄路徑
.PARAMETER statusLabel
    主視窗底部狀態列控制項
.OUTPUTS
    System.Windows.Forms.TabPage
.NOTES
    檔案名稱: tabWorkItem.ps1
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
    $workItemScript = join-path -path $psRootPath -childpath 'manageWorkItem.ps1'
    $tabPage = new-object system.windows.forms.tabpage
    $tabPage.text = '3. 工項建立與員工賦予'
    $tabPage.padding = new-object system.windows.forms.padding(10)

    $lblCode = new-object system.windows.forms.label
    $lblCode.text = '工項代碼：'
    $lblCode.location = new-object system.drawing.point(12, 16)
    $lblCode.autosize = $true

    $txtCode = new-object system.windows.forms.textbox
    $txtCode.location = new-object system.drawing.point(85, 13)
    $txtCode.width = 120
    $txtCode.text = 'WI-2026-007'

    $lblTitle = new-object system.windows.forms.label
    $lblTitle.text = '工項標題：'
    $lblTitle.location = new-object system.drawing.point(215, 16)
    $lblTitle.autosize = $true

    $txtTitle = new-object system.windows.forms.textbox
    $txtTitle.location = new-object system.drawing.point(290, 13)
    $txtTitle.width = 250
    $txtTitle.text = '新增部門季度 Story Point 趨勢分析匯出'

    $lblTypePk = new-object system.windows.forms.label
    $lblTypePk.text = '代表類型 ID：'
    $lblTypePk.location = new-object system.drawing.point(550, 16)
    $lblTypePk.autosize = $true

    $txtTypePk = new-object system.windows.forms.textbox
    $txtTypePk.location = new-object system.drawing.point(640, 13)
    $txtTypePk.width = 50
    $txtTypePk.text = '4'

    $lblPoint = new-object system.windows.forms.label
    $lblPoint.text = '手動點數(留空自動帶入)：'
    $lblPoint.location = new-object system.drawing.point(700, 16)
    $lblPoint.autosize = $true

    $txtPoint = new-object system.windows.forms.textbox
    $txtPoint.location = new-object system.drawing.point(865, 13)
    $txtPoint.width = 60
    $txtPoint.text = ''

    $lblEmpPk = new-object system.windows.forms.label
    $lblEmpPk.text = '賦予員工 ID：'
    $lblEmpPk.location = new-object system.drawing.point(12, 50)
    $lblEmpPk.autosize = $true

    $txtEmpPk = new-object system.windows.forms.textbox
    $txtEmpPk.location = new-object system.drawing.point(105, 47)
    $txtEmpPk.width = 60
    $txtEmpPk.text = '3'

    $lblPct = new-object system.windows.forms.label
    $lblPct.text = '分攤比例(%)：'
    $lblPct.location = new-object system.drawing.point(180, 50)
    $lblPct.autosize = $true

    $txtPct = new-object system.windows.forms.textbox
    $txtPct.location = new-object system.drawing.point(275, 47)
    $txtPct.width = 60
    $txtPct.text = '100'

    $btnSaveWorkItem = new-object system.windows.forms.button
    $btnSaveWorkItem.text = '💾 建立/儲存工項並賦予員工'
    $btnSaveWorkItem.location = new-object system.drawing.point(355, 44)
    $btnSaveWorkItem.size = new-object system.drawing.size(210, 30)
    $btnSaveWorkItem.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnSaveWorkItem.forecolor = [system.drawing.color]::white

    $btnRefresh = new-object system.windows.forms.button
    $btnRefresh.text = '🔄 重新整理工項與點數清單'
    $btnRefresh.location = new-object system.drawing.point(580, 44)
    $btnRefresh.size = new-object system.drawing.size(200, 30)

    $gridItem = new-object system.windows.forms.datagridview
    $gridItem.location = new-object system.drawing.point(12, 88)
    $gridItem.size = new-object system.drawing.size(1130, 520)
    $gridItem.readonly = $true
    $gridItem.allowusertoaddrows = $false
    $gridItem.autosizecolumnsmode = 'Fill'
    enableGridHeaderSortAndLock -grid $gridItem -statusLabel $statusLabel

    $loadItemData = {
        $dt = & $workItemScript -actionType 'listWorkItem'
        $gridItem.datasource = $dt
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已載入 $($dt.rows.count) 筆工項與 Story Point 明細"
        }
    }

    $btnRefresh.add_click($loadItemData)
    $btnSaveWorkItem.add_click({
        $ptVal = if (-not [string]::isnullorempty($txtPoint.text)) { [decimal]$txtPoint.text } else { $null }
        $isManual = ($null -ne $ptVal)
        $null = & $workItemScript -actionType 'upsertWorkItem' `
            -itemCode $txtCode.text -itemTitle $txtTitle.text `
            -workItemTypePk ([int]$txtTypePk.text) -storyPoint $ptVal -forceManualAdjust $isManual `
            -employeePk ([int]$txtEmpPk.text) -allocationPercent ([decimal]$txtPct.text)
        & $loadItemData
    })

    $tabPage.controls.addrange(@(
        $lblCode, $txtCode, $lblTitle, $txtTitle, $lblTypePk, $txtTypePk, $lblPoint, $txtPoint,
        $lblEmpPk, $txtEmpPk, $lblPct, $txtPct, $btnSaveWorkItem, $btnRefresh, $gridItem
    ))

    return $tabPage
}
