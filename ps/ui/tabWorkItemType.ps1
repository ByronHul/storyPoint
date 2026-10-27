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
    $votingScript = join-path -path $psRootPath -childpath 'manageVotingSession.ps1'
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

    $btnRevote = new-object system.windows.forms.button
    $btnRevote.text = '🔄 重新投票(開新輪)'
    $btnRevote.location = new-object system.drawing.point(378, 50)
    $btnRevote.size = new-object system.drawing.size(165, 30)
    $btnRevote.backcolor = [system.drawing.color]::fromargb(239, 246, 255)
    $btnRevote.forecolor = [system.drawing.color]::fromargb(29, 78, 216)

    $btnRestartAll = new-object system.windows.forms.button
    $btnRestartAll.text = '🆕 全部重新開始(第1輪)'
    $btnRestartAll.location = new-object system.drawing.point(552, 50)
    $btnRestartAll.size = new-object system.drawing.size(180, 30)
    $btnRestartAll.backcolor = [system.drawing.color]::fromargb(238, 242, 255)
    $btnRestartAll.forecolor = [system.drawing.color]::fromargb(67, 56, 202)

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
    } -onRevoteRow {
        param($rowItem)
        if ($null -ne $rowItem -and $rowItem.table.columns.contains('workItemTypePk')) {
            $ans = [system.windows.forms.messagebox]::show("確定要針對代表類型「$($rowItem.typeCode) - $($rowItem.typeName)」發起重新投票（開啟新一輪盲投並重設已定案狀態）嗎？", '確認重新投票', [system.windows.forms.messageboxbuttons]::yesno, [system.windows.forms.messageboxicon]::question)
            if ($ans -eq [system.windows.forms.dialogresult]::yes) {
                $null = & $votingScript -actionType 'revoteSession' -targetKind 'workItemType' -workItemTypePk ([int]$rowItem.workItemTypePk) -roundDiscussionNote '由代表類型管理頁籤發起重新投票'
                & $loadTypeData
                if ($null -ne $statusLabel) {
                    $statusLabel.text = "🔄 已針對代表類型【$($rowItem.typeCode) - $($rowItem.typeName)】開啟新一輪重新投票"
                }
            }
        }
    } -onRestartAllRow {
        param($rowItem)
        if ($null -ne $rowItem -and $rowItem.table.columns.contains('workItemTypePk')) {
            $ans = [system.windows.forms.messagebox]::show("確定要針對代表類型「$($rowItem.typeCode) - $($rowItem.typeName)」全部重新開始（清除過往回合並重設回第 1 回合）嗎？", '確認全部重新開始', [system.windows.forms.messageboxbuttons]::yesno, [system.windows.forms.messageboxicon]::question)
            if ($ans -eq [system.windows.forms.dialogresult]::yes) {
                $null = & $votingScript -actionType 'restartAllSession' -targetKind 'workItemType' -workItemTypePk ([int]$rowItem.workItemTypePk) -roundDiscussionNote '由代表類型管理頁籤發起全部重新開始'
                & $loadTypeData
                if ($null -ne $statusLabel) {
                    $statusLabel.text = "🆕 已針對代表類型【$($rowItem.typeCode) - $($rowItem.typeName)】全部重新開始（重設回第 1 回合）"
                }
            }
        }
    }

    $btnRefresh.add_click($loadTypeData)
    $btnRevote.add_click({
        if ($gridType.selectedrows.count -gt 0 -or $null -ne $gridType.currentrow) {
            $selRow = if ($gridType.selectedrows.count -gt 0) { $gridType.selectedrows[0] } else { $gridType.currentrow }
            if ($null -ne $selRow -and $null -ne $selRow.databounditem) {
                $rItem = $selRow.databounditem.row
                $null = & $votingScript -actionType 'revoteSession' -targetKind 'workItemType' -workItemTypePk ([int]$rItem.workItemTypePk) -roundDiscussionNote '由代表類型管理頁籤發起重新投票'
                & $loadTypeData
                if ($null -ne $statusLabel) {
                    $statusLabel.text = "🔄 已針對代表類型【$($rItem.typeCode) - $($rItem.typeName)】開啟新一輪重新投票"
                }
            }
        }
    })
    $btnRestartAll.add_click({
        if ($gridType.selectedrows.count -gt 0 -or $null -ne $gridType.currentrow) {
            $selRow = if ($gridType.selectedrows.count -gt 0) { $gridType.selectedrows[0] } else { $gridType.currentrow }
            if ($null -ne $selRow -and $null -ne $selRow.databounditem) {
                $rItem = $selRow.databounditem.row
                $null = & $votingScript -actionType 'restartAllSession' -targetKind 'workItemType' -workItemTypePk ([int]$rItem.workItemTypePk) -roundDiscussionNote '由代表類型管理頁籤發起全部重新開始'
                & $loadTypeData
                if ($null -ne $statusLabel) {
                    $statusLabel.text = "🆕 已針對代表類型【$($rItem.typeCode) - $($rItem.typeName)】全部重新開始（重設回第 1 回合）"
                }
            }
        }
    })
    $btnSave.add_click({
        $ptVal = if (-not [string]::isnullorempty($txtPoint.text)) { [decimal]$txtPoint.text } else { $null }
        $null = & $workItemScript -actionType 'upsertWorkItemType' `
            -typeCode $txtCode.text -typeName $txtName.text -standardStoryPoint $ptVal `
            -syncExistingOpenWorkItem $chkSync.checked
        & $loadTypeData
    })

    $tabPage.controls.addrange(@(
        $lblCode, $txtCode, $lblName, $txtName, $lblPoint, $txtPoint, $chkSync,
        $btnSave, $btnRefresh, $btnRevote, $btnRestartAll, $gridType
    ))

    return $tabPage
}
