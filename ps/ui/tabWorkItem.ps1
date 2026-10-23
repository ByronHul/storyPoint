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
    $txtEmpPk.width = 50
    $txtEmpPk.text = '3'

    $lblPct = new-object system.windows.forms.label
    $lblPct.text = '分攤(%)：'
    $lblPct.location = new-object system.drawing.point(165, 50)
    $lblPct.autosize = $true

    $txtPct = new-object system.windows.forms.textbox
    $txtPct.location = new-object system.drawing.point(235, 47)
    $txtPct.width = 45
    $txtPct.text = '100'

    $lblKind = new-object system.windows.forms.label
    $lblKind.text = '類型：'
    $lblKind.location = new-object system.drawing.point(290, 50)
    $lblKind.autosize = $true

    $cmbKind = new-object system.windows.forms.combobox
    $cmbKind.location = new-object system.drawing.point(338, 47)
    $cmbKind.width = 95
    $cmbKind.dropdownstyle = 'DropDown'
    $null = $cmbKind.items.addrange(@('專案', '維運', '顧問時數'))
    $cmbKind.selectedindex = 0

    $lblStart = new-object system.windows.forms.label
    $lblStart.text = '起時：'
    $lblStart.location = new-object system.drawing.point(442, 50)
    $lblStart.autosize = $true

    $dtpStart = new-object system.windows.forms.datetimepicker
    $dtpStart.location = new-object system.drawing.point(488, 47)
    $dtpStart.width = 135
    $dtpStart.format = [system.windows.forms.datetimepickerformat]::custom
    $dtpStart.customformat = 'yyyy-MM-dd HH:mm'
    $dtpStart.value = (get-date)

    $lblEnd = new-object system.windows.forms.label
    $lblEnd.text = '迄時：'
    $lblEnd.location = new-object system.drawing.point(630, 50)
    $lblEnd.autosize = $true

    $dtpEnd = new-object system.windows.forms.datetimepicker
    $dtpEnd.location = new-object system.drawing.point(676, 47)
    $dtpEnd.width = 135
    $dtpEnd.format = [system.windows.forms.datetimepickerformat]::custom
    $dtpEnd.customformat = 'yyyy-MM-dd HH:mm'
    $dtpEnd.value = (get-date).adddays(14)

    $btnSaveWorkItem = new-object system.windows.forms.button
    $btnSaveWorkItem.text = '💾 儲存工項'
    $btnSaveWorkItem.location = new-object system.drawing.point(820, 44)
    $btnSaveWorkItem.size = new-object system.drawing.size(110, 30)
    $btnSaveWorkItem.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnSaveWorkItem.forecolor = [system.drawing.color]::white

    $btnDeleteWorkItem = new-object system.windows.forms.button
    $btnDeleteWorkItem.text = '🗑️ 刪除工項'
    $btnDeleteWorkItem.location = new-object system.drawing.point(935, 44)
    $btnDeleteWorkItem.size = new-object system.drawing.size(95, 30)

    $btnRefresh = new-object system.windows.forms.button
    $btnRefresh.text = '⚡ 檢核到期/重整'
    $btnRefresh.location = new-object system.drawing.point(1035, 44)
    $btnRefresh.size = new-object system.drawing.size(125, 30)

    # 工項類型 (tbWorkItemKind) 增刪修控制項列
    $lblKindCrud = new-object system.windows.forms.label
    $lblKindCrud.text = '工項類型增刪修 - 代碼：'
    $lblKindCrud.location = new-object system.drawing.point(12, 84)
    $lblKindCrud.autosize = $true

    $txtKindCode = new-object system.windows.forms.textbox
    $txtKindCode.location = new-object system.drawing.point(165, 81)
    $txtKindCode.width = 110
    $txtKindCode.text = 'supportCase'

    $lblKindName = new-object system.windows.forms.label
    $lblKindName.text = '類型名稱：'
    $lblKindName.location = new-object system.drawing.point(285, 84)
    $lblKindName.autosize = $true

    $txtKindName = new-object system.windows.forms.textbox
    $txtKindName.location = new-object system.drawing.point(355, 81)
    $txtKindName.width = 120
    $txtKindName.text = '駐點技術支援'

    $btnUpsertKind = new-object system.windows.forms.button
    $btnUpsertKind.text = '➕ 新增/修改工項類型'
    $btnUpsertKind.location = new-object system.drawing.point(485, 78)
    $btnUpsertKind.size = new-object system.drawing.size(145, 28)

    $btnDeleteKind = new-object system.windows.forms.button
    $btnDeleteKind.text = '🗑️ 刪除工項類型'
    $btnDeleteKind.location = new-object system.drawing.point(636, 78)
    $btnDeleteKind.size = new-object system.drawing.size(125, 28)

    $gridItem = new-object system.windows.forms.datagridview
    $gridItem.location = new-object system.drawing.point(12, 116)
    $gridItem.size = new-object system.drawing.size(1130, 492)
    $gridItem.readonly = $true
    $gridItem.allowusertoaddrows = $false
    $gridItem.autosizecolumnsmode = 'Fill'
    enableGridHeaderSortAndLock -grid $gridItem -statusLabel $statusLabel

    $loadItemData = {
        $kindDt = & $workItemScript -actionType 'listWorkItemKind'
        if ($null -ne $kindDt -and $kindDt.rows.count -gt 0) {
            $cmbKind.items.clear()
            foreach ($kr in $kindDt.rows) {
                $null = $cmbKind.items.add($kr.kindName.tostring())
            }
            if ($cmbKind.items.count -gt 0) { $cmbKind.selectedindex = 0 }
        }
        $dt = & $workItemScript -actionType 'listWorkItem'
        $gridItem.datasource = $dt
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已載入 $($dt.rows.count) 筆工項與 $($kindDt.rows.count) 種工項類型（支援增刪修）"
        }
    }

    $btnRefresh.add_click({
        $expDt = & $workItemScript -actionType 'processExpired'
        & $loadItemData
        if ($null -ne $statusLabel -and $null -ne $expDt -and $expDt.rows.count -gt 0) {
            $statusLabel.text = "⏰ 已處理 $($expDt.rows.count) 筆達到迄時的工項，自動轉為已完成、扣除 Story Point 並寄發 Email 給部門管理層與 HR！"
        }
    })
    $btnSaveWorkItem.add_click({
        $ptVal = if (-not [string]::isnullorempty($txtPoint.text)) { [decimal]$txtPoint.text } else { $null }
        $isManual = ($null -ne $ptVal)
        $kindCode = $cmbKind.text.trim()
        $null = & $workItemScript -actionType 'upsertWorkItem' `
            -itemCode $txtCode.text -itemTitle $txtTitle.text `
            -workItemTypePk ([int]$txtTypePk.text) -workItemKind $kindCode `
            -startDate $dtpStart.value -endDate $dtpEnd.value `
            -storyPoint $ptVal -forceManualAdjust $isManual `
            -employeePk ([int]$txtEmpPk.text) -allocationPercent ([decimal]$txtPct.text)
        & $loadItemData
    })
    $btnDeleteWorkItem.add_click({
        $null = & $workItemScript -actionType 'deleteWorkItem' -itemCode $txtCode.text.trim()
        & $loadItemData
    })
    $btnUpsertKind.add_click({
        $null = & $workItemScript -actionType 'upsertWorkItemKind' `
            -kindCode $txtKindCode.text.trim() -kindName $txtKindName.text.trim()
        & $loadItemData
    })
    $btnDeleteKind.add_click({
        $null = & $workItemScript -actionType 'deleteWorkItemKind' `
            -kindCode $txtKindCode.text.trim()
        & $loadItemData
    })

    $tabPage.controls.addrange(@(
        $lblCode, $txtCode, $lblTitle, $txtTitle, $lblTypePk, $txtTypePk, $lblPoint, $txtPoint,
        $lblEmpPk, $txtEmpPk, $lblPct, $txtPct, $lblKind, $cmbKind, $lblStart, $dtpStart, $lblEnd, $dtpEnd,
        $btnSaveWorkItem, $btnDeleteWorkItem, $btnRefresh,
        $lblKindCrud, $txtKindCode, $lblKindName, $txtKindName, $btnUpsertKind, $btnDeleteKind,
        $gridItem
    ))

    return $tabPage
}
