<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - [Tab 2] 工項代表類型管理頁籤 (tabWorkItemType.ps1)
.DESCRIPTION
    獨立實作「工項代表類型查詢、新增與標準 Story Point 設定/同步」頁籤。
    所有功能一律呼叫 ps/manageWorkItem.ps1。
.PARAMETER psRootPath
    ps 模組根目錄路徑
.PARAMETER statusLabel
    主視窗底部狀態列控制項
.OUTPUTS
    System.Windows.Forms.TabPage
.NOTES
    檔案名稱: tabWorkItemType.ps1
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
    $tabPage.text = '2. 工項代表類型設定'
    $tabPage.padding = new-object system.windows.forms.padding(10)

    $lblCode = new-object system.windows.forms.label
    $lblCode.text = '代表類型代碼：'
    $lblCode.location = new-object system.drawing.point(12, 16)
    $lblCode.autosize = $true

    $txtCode = new-object system.windows.forms.textbox
    $txtCode.location = new-object system.drawing.point(110, 13)
    $txtCode.width = 160
    $txtCode.text = 'TYPE_STD_CRUD'

    $lblName = new-object system.windows.forms.label
    $lblName.text = '類型名稱：'
    $lblName.location = new-object system.drawing.point(285, 16)
    $lblName.autosize = $true

    $txtName = new-object system.windows.forms.textbox
    $txtName.location = new-object system.drawing.point(360, 13)
    $txtName.width = 230
    $txtName.text = '標準單表 CRUD API 與預存程序'

    $lblPoint = new-object system.windows.forms.label
    $lblPoint.text = '標準 Story Point：'
    $lblPoint.location = new-object system.drawing.point(605, 16)
    $lblPoint.autosize = $true

    $txtPoint = new-object system.windows.forms.textbox
    $txtPoint.location = new-object system.drawing.point(725, 13)
    $txtPoint.width = 70
    $txtPoint.text = '3'

    $chkSync = new-object system.windows.forms.checkbox
    $chkSync.text = '同步更新未完成且未微調之既有工項'
    $chkSync.location = new-object system.drawing.point(810, 14)
    $chkSync.autosize = $true

    $btnSave = new-object system.windows.forms.button
    $btnSave.text = '💾 儲存代表類型'
    $btnSave.location = new-object system.drawing.point(12, 50)
    $btnSave.size = new-object system.drawing.size(150, 30)
    $btnSave.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnSave.forecolor = [system.drawing.color]::white

    $btnRefresh = new-object system.windows.forms.button
    $btnRefresh.text = '🔄 重新整理代表類型清單'
    $btnRefresh.location = new-object system.drawing.point(175, 50)
    $btnRefresh.size = new-object system.drawing.size(190, 30)

    $gridType = new-object system.windows.forms.datagridview
    $gridType.location = new-object system.drawing.point(12, 92)
    $gridType.size = new-object system.drawing.size(1130, 520)
    $gridType.readonly = $true
    $gridType.allowusertoaddrows = $false
    $gridType.autosizecolumnsmode = 'Fill'

    $loadTypeData = {
        $dt = & $workItemScript -actionType 'listWorkItemType'
        $gridType.datasource = $dt
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已載入 $($dt.rows.count) 筆工項代表類型（支援於表格按滑鼠右鍵開啟快捷選單執行編輯、刪除或篩選）"
        }
    }

    enableGridHeaderSortAndLock -grid $gridType -statusLabel $statusLabel -onEditRow {
        param($rowItem)
        if ($null -ne $rowItem) {
            $txtCode.text = [string]$rowItem.typeCode
            $txtName.text = [string]$rowItem.typeName
            if ($rowItem.table.columns.contains('standardStoryPoint') -and $rowItem.standardStoryPoint -ne [dbnull]::value) {
                $txtPoint.text = [string]$rowItem.standardStoryPoint
            }
            if ($null -ne $statusLabel) {
                $statusLabel.text = "✏️ 已透過滑鼠快捷選單載入代表類型【$($rowItem.typeCode) - $($rowItem.typeName)】至上方表單"
            }
        }
    } -onDeleteRow {
        param($rowItem)
        if ($null -ne $rowItem) {
            $ans = [system.windows.forms.messagebox]::show("確定要刪除工項代表類型「$($rowItem.typeCode) - $($rowItem.typeName)」嗎？", '確認刪除代表類型', [system.windows.forms.messageboxbuttons]::yesno, [system.windows.forms.messageboxicon]::warning)
            if ($ans -eq [system.windows.forms.dialogresult]::yes) {
                $null = & $workItemScript -actionType 'deleteWorkItemType' -typeCode ([string]$rowItem.typeCode)
                & $loadTypeData
            }
        }
    }

    $btnRefresh.add_click($loadTypeData)
    $btnSave.add_click({
        $ptVal = if (-not [string]::isnullorempty($txtPoint.text)) { [decimal]$txtPoint.text } else { $null }
        $null = & $workItemScript -actionType 'upsertWorkItemType' `
            -typeCode $txtCode.text -typeName $txtName.text -standardStoryPoint $ptVal `
            -syncExistingOpenWorkItem $chkSync.checked
        & $loadTypeData
    })

    $tabPage.controls.addrange(@(
        $lblCode, $txtCode, $lblName, $txtName, $lblPoint, $txtPoint, $chkSync,
        $btnSave, $btnRefresh, $gridType
    ))

    return $tabPage
}
