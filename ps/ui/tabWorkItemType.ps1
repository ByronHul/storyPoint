<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - [Tab 2] 工項代表類型與預設工項類型管理頁籤 (tabWorkItemType.ps1)
.DESCRIPTION
    獨立實作：
    1. 「預設工項類型 (tbWorkItemKind) 增、刪、修、查管理與主要特徵一覽表」(專案/維運/顧問時數/教育訓練/開發產品/自訂類型)
    2. 「工項代表類型 (tbWorkItemType) 查詢、新增與標準 Story Point 設定/同步」
    所有商業邏輯功能一律呼叫 ps/manageWorkItem.ps1 與 ps/manageVotingSession.ps1。
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
    $tabPage.text = '2-2 代表工項 / 2-3 預設類型'
    $tabPage.padding = new-object system.windows.forms.padding(8)

    $script:currentWorkItemTypePk = 0

    # =========================================================================
    # 上半部 (2-2)：工項代表類型 (tbWorkItemType) — 編輯投出 SP 的代表工項各種屬性與所屬預設類型
    # =========================================================================
    $grpType = new-object system.windows.forms.groupbox
    $grpType.text = '🏷️ 2-2. 工項代表類型：編輯投出 SP 的代表工項屬性（如：資料庫健檢）與所屬預設類型 (tbWorkItemType)'
    $grpType.location = new-object system.drawing.point(10, 8)
    $grpType.size = new-object system.drawing.size(1135, 315)

    $lblName = new-object system.windows.forms.label
    $lblName.text = '代表工項名稱：'
    $lblName.location = new-object system.drawing.point(12, 24)
    $lblName.autosize = $true

    $txtName = new-object system.windows.forms.textbox
    $txtName.location = new-object system.drawing.point(108, 21)
    $txtName.width = 185
    $txtName.text = '資料庫健檢'

    $lblDefKind = new-object system.windows.forms.label
    $lblDefKind.text = '屬於預設類型：'
    $lblDefKind.location = new-object system.drawing.point(302, 24)
    $lblDefKind.autosize = $true

    $cmbDefaultKind = new-object system.windows.forms.combobox
    $cmbDefaultKind.location = new-object system.drawing.point(398, 21)
    $cmbDefaultKind.width = 135
    $cmbDefaultKind.dropdownstyle = 'DropDownList'

    $lblCategory = new-object system.windows.forms.label
    $lblCategory.text = '分類屬性：'
    $lblCategory.location = new-object system.drawing.point(542, 24)
    $lblCategory.autosize = $true

    $txtCategory = new-object system.windows.forms.textbox
    $txtCategory.location = new-object system.drawing.point(612, 21)
    $txtCategory.width = 135
    $txtCategory.text = '資料庫健檢與維運'

    $lblPoint = new-object system.windows.forms.label
    $lblPoint.text = '投出 SP：'
    $lblPoint.location = new-object system.drawing.point(755, 24)
    $lblPoint.autosize = $true

    $txtPoint = new-object system.windows.forms.textbox
    $txtPoint.location = new-object system.drawing.point(818, 21)
    $txtPoint.width = 50
    $txtPoint.text = '8'

    $lblCode = new-object system.windows.forms.label
    $lblCode.text = '代碼：'
    $lblCode.location = new-object system.drawing.point(876, 24)
    $lblCode.autosize = $true

    $txtCode = new-object system.windows.forms.textbox
    $txtCode.location = new-object system.drawing.point(920, 21)
    $txtCode.width = 110
    $txtCode.text = 'TYPE_20261023034516'

    $chkSync = new-object system.windows.forms.checkbox
    $chkSync.text = '同步工項'
    $chkSync.location = new-object system.drawing.point(1038, 22)
    $chkSync.checked = $true
    $chkSync.autosize = $true

    $lblTypeDesc = new-object system.windows.forms.label
    $lblTypeDesc.text = '工作與驗收屬性：'
    $lblTypeDesc.location = new-object system.drawing.point(12, 54)
    $lblTypeDesc.autosize = $true

    $txtTypeDesc = new-object system.windows.forms.textbox
    $txtTypeDesc.location = new-object system.drawing.point(118, 51)
    $txtTypeDesc.width = 435
    $txtTypeDesc.text = '透過 script 收集狀態，分析健康狀態，提供健檢報告並完成現場或線上報告'

    $btnSave = new-object system.windows.forms.button
    $btnSave.text = '💾 儲存代表工項屬性'
    $btnSave.location = new-object system.drawing.point(562, 49)
    $btnSave.size = new-object system.drawing.size(150, 28)
    $btnSave.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnSave.forecolor = [system.drawing.color]::white

    $btnRefresh = new-object system.windows.forms.button
    $btnRefresh.text = '🔄 重新整理清單'
    $btnRefresh.location = new-object system.drawing.point(718, 49)
    $btnRefresh.size = new-object system.drawing.size(125, 28)

    $btnRevote = new-object system.windows.forms.button
    $btnRevote.text = '🔄 重新投票(開新輪)'
    $btnRevote.location = new-object system.drawing.point(849, 49)
    $btnRevote.size = new-object system.drawing.size(135, 28)
    $btnRevote.backcolor = [system.drawing.color]::fromargb(239, 246, 255)
    $btnRevote.forecolor = [system.drawing.color]::fromargb(29, 78, 216)

    $btnRestartAll = new-object system.windows.forms.button
    $btnRestartAll.text = '🆕 全部重投(第1輪)'
    $btnRestartAll.location = new-object system.drawing.point(990, 49)
    $btnRestartAll.size = new-object system.drawing.size(132, 28)
    $btnRestartAll.backcolor = [system.drawing.color]::fromargb(238, 242, 255)
    $btnRestartAll.forecolor = [system.drawing.color]::fromargb(67, 56, 202)

    $gridType = new-object system.windows.forms.datagridview
    $gridType.location = new-object system.drawing.point(12, 84)
    $gridType.size = new-object system.drawing.size(1110, 220)
    $gridType.readonly = $true
    $gridType.allowusertoaddrows = $false
    $gridType.autosizecolumnsmode = 'Fill'

    $grpType.controls.addrange(@(
        $lblName, $txtName, $lblDefKind, $cmbDefaultKind, $lblCategory, $txtCategory, $lblPoint, $txtPoint, $lblCode, $txtCode, $chkSync,
        $lblTypeDesc, $txtTypeDesc, $btnSave, $btnRefresh, $btnRevote, $btnRestartAll, $gridType
    ))

    # =========================================================================
    # 下半部 (2-3)：預設工項類型 (tbWorkItemKind) 增刪修查與主要特徵一覽表
    # =========================================================================
    $grpKind = new-object system.windows.forms.groupbox
    $grpKind.text = '🗂️ 2-3. 預設工項類型與特徵：增、刪、修、查管理與主要特徵一覽表 (tbWorkItemKind)'
    $grpKind.location = new-object system.drawing.point(10, 330)
    $grpKind.size = new-object system.drawing.size(1135, 290)

    $lblKindCode = new-object system.windows.forms.label
    $lblKindCode.text = '類型代碼：'
    $lblKindCode.location = new-object system.drawing.point(12, 24)
    $lblKindCode.autosize = $true

    $txtKindCode = new-object system.windows.forms.textbox
    $txtKindCode.location = new-object system.drawing.point(82, 21)
    $txtKindCode.width = 110
    $txtKindCode.text = 'project'

    $lblKindName = new-object system.windows.forms.label
    $lblKindName.text = '預設類型名稱：'
    $lblKindName.location = new-object system.drawing.point(200, 24)
    $lblKindName.autosize = $true

    $txtKindName = new-object system.windows.forms.textbox
    $txtKindName.location = new-object system.drawing.point(295, 21)
    $txtKindName.width = 110
    $txtKindName.text = '專案'

    $lblKindIcon = new-object system.windows.forms.label
    $lblKindIcon.text = '代表圖示：'
    $lblKindIcon.location = new-object system.drawing.point(410, 24)
    $lblKindIcon.autosize = $true

    $cmbKindIcon = new-object system.windows.forms.combobox
    $cmbKindIcon.location = new-object system.drawing.point(478, 21)
    $cmbKindIcon.width = 145
    $cmbKindIcon.dropdownstyle = 'DropDown'
    $cmbKindIcon.text = '🏗️'

    $btnUploadIcon = new-object system.windows.forms.button
    $btnUploadIcon.text = '📤 上傳圖示'
    $btnUploadIcon.location = new-object system.drawing.point(628, 19)
    $btnUploadIcon.size = new-object system.drawing.size(92, 26)
    $btnUploadIcon.backcolor = [system.drawing.color]::fromargb(240, 253, 244)
    $btnUploadIcon.forecolor = [system.drawing.color]::fromargb(21, 128, 61)

    $lblKindColor = new-object system.windows.forms.label
    $lblKindColor.text = '顏色：'
    $lblKindColor.location = new-object system.drawing.point(726, 24)
    $lblKindColor.autosize = $true

    $cmbKindColor = new-object system.windows.forms.combobox
    $cmbKindColor.location = new-object system.drawing.point(770, 21)
    $cmbKindColor.width = 75
    $cmbKindColor.dropdownstyle = 'DropDownList'
    [void]$cmbKindColor.items.addrange(@('blue', 'amber', 'purple', 'green', 'red', 'rose', 'teal', 'slate'))
    $cmbKindColor.selectedindex = 0

    $lblKindSort = new-object system.windows.forms.label
    $lblKindSort.text = '排序：'
    $lblKindSort.location = new-object system.drawing.point(852, 24)
    $lblKindSort.autosize = $true

    $txtKindSort = new-object system.windows.forms.textbox
    $txtKindSort.location = new-object system.drawing.point(895, 21)
    $txtKindSort.width = 40
    $txtKindSort.text = '10'

    $lblKindKw = new-object system.windows.forms.label
    $lblKindKw.text = '🔍查詢：'
    $lblKindKw.location = new-object system.drawing.point(942, 24)
    $lblKindKw.autosize = $true

    $txtKindKw = new-object system.windows.forms.textbox
    $txtKindKw.location = new-object system.drawing.point(998, 21)
    $txtKindKw.width = 65

    $btnSearchKind = new-object system.windows.forms.button
    $btnSearchKind.text = '查詢'
    $btnSearchKind.location = new-object system.drawing.point(1068, 19)
    $btnSearchKind.size = new-object system.drawing.size(55, 26)

    $lblMainFeature = new-object system.windows.forms.label
    $lblMainFeature.text = '⭐主要特徵：'
    $lblMainFeature.location = new-object system.drawing.point(12, 54)
    $lblMainFeature.autosize = $true

    $txtMainFeature = new-object system.windows.forms.textbox
    $txtMainFeature.location = new-object system.drawing.point(92, 51)
    $txtMainFeature.width = 285
    $txtMainFeature.text = '目標導向、具明確範疇與里程碑之新功能開發或系統建置'

    $lblEstTrait = new-object system.windows.forms.label
    $lblEstTrait.text = '📐估算特徵：'
    $lblEstTrait.location = new-object system.drawing.point(385, 54)
    $lblEstTrait.autosize = $true

    $txtEstTrait = new-object system.windows.forms.textbox
    $txtEstTrait.location = new-object system.drawing.point(465, 51)
    $txtEstTrait.width = 275
    $txtEstTrait.text = '依功能複雜度進行團隊盲投估算 (通常 3 ~ 21 pt)'

    $lblDelivTrait = new-object system.windows.forms.label
    $lblDelivTrait.text = '⏰時程交付：'
    $lblDelivTrait.location = new-object system.drawing.point(748, 54)
    $lblDelivTrait.autosize = $true

    $txtDelivTrait = new-object system.windows.forms.textbox
    $txtDelivTrait.location = new-object system.drawing.point(828, 51)
    $txtDelivTrait.width = 292
    $txtDelivTrait.text = '具明確起訖日與階段交付物，達迄時自動轉態扣點並發信'

    $lblKindDesc = new-object system.windows.forms.label
    $lblKindDesc.text = '📋適用情境：'
    $lblKindDesc.location = new-object system.drawing.point(12, 84)
    $lblKindDesc.autosize = $true

    $txtKindDesc = new-object system.windows.forms.textbox
    $txtKindDesc.location = new-object system.drawing.point(92, 81)
    $txtKindDesc.width = 520
    $txtKindDesc.text = '客戶導入專案、系統全新功能開發或跨部門里程碑交付工項'

    $btnSaveKind = new-object system.windows.forms.button
    $btnSaveKind.text = '➕ 新增/儲存預設類型'
    $btnSaveKind.location = new-object system.drawing.point(625, 79)
    $btnSaveKind.size = new-object system.drawing.size(165, 28)
    $btnSaveKind.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnSaveKind.forecolor = [system.drawing.color]::white

    $btnDeleteKind = new-object system.windows.forms.button
    $btnDeleteKind.text = '🗑️ 刪除預設類型'
    $btnDeleteKind.location = new-object system.drawing.point(800, 79)
    $btnDeleteKind.size = new-object system.drawing.size(145, 28)
    $btnDeleteKind.backcolor = [system.drawing.color]::fromargb(254, 242, 242)
    $btnDeleteKind.forecolor = [system.drawing.color]::fromargb(185, 28, 28)

    $btnResetKind = new-object system.windows.forms.button
    $btnResetKind.text = '🔄 顯示全部類型'
    $btnResetKind.location = new-object system.drawing.point(955, 79)
    $btnResetKind.size = new-object system.drawing.size(165, 28)

    $gridKind = new-object system.windows.forms.datagridview
    $gridKind.location = new-object system.drawing.point(12, 114)
    $gridKind.size = new-object system.drawing.size(1110, 165)
    $gridKind.readonly = $true
    $gridKind.allowusertoaddrows = $false
    $gridKind.autosizecolumnsmode = 'Fill'

    $grpKind.controls.addrange(@(
        $lblKindCode, $txtKindCode, $lblKindName, $txtKindName, $lblKindIcon, $cmbKindIcon, $btnUploadIcon,
        $lblKindColor, $cmbKindColor, $lblKindSort, $txtKindSort, $lblKindKw, $txtKindKw, $btnSearchKind,
        $lblMainFeature, $txtMainFeature, $lblEstTrait, $txtEstTrait, $lblDelivTrait, $txtDelivTrait,
        $lblKindDesc, $txtKindDesc, $btnSaveKind, $btnDeleteKind, $btnResetKind, $gridKind
    ))

    $loadIconData = {
        $curIconVal = $cmbKindIcon.text
        $dtIcon = & $workItemScript -actionType 'listWorkItemIcon'
        $cmbKindIcon.items.clear()
        if ($null -ne $dtIcon) {
            foreach ($ir in $dtIcon.rows) {
                $valStr = [string]$ir.iconValue
                if (-not [string]::isnullorempty($valStr) -and -not $cmbKindIcon.items.contains($valStr)) {
                    [void]$cmbKindIcon.items.add($valStr)
                }
            }
        }
        if (-not [string]::isnullorempty($curIconVal)) {
            $cmbKindIcon.text = $curIconVal
        } elseif ($cmbKindIcon.items.count -gt 0) {
            $cmbKindIcon.selectedindex = 0
        }
    }

    $loadKindData = {
        & $loadIconData
        $dtKind = & $workItemScript -actionType 'listWorkItemKind' -keyword $txtKindKw.text
        $gridKind.datasource = $dtKind
        $cmbDefaultKind.items.clear()
        foreach ($r in $dtKind.rows) {
            [void]$cmbDefaultKind.items.add([string]$r.kindCode)
        }
        if ($cmbDefaultKind.items.count -gt 0 -and $cmbDefaultKind.selectedindex -lt 0) {
            $cmbDefaultKind.selectedindex = 0
        }
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已載入 $($dtKind.rows.count) 種預設工項類型與主要特徵（代表圖示庫共 $($cmbKindIcon.items.count) 種圖示）"
        }
    }

    $loadTypeData = {
        & $loadKindData
        $dt = & $workItemScript -actionType 'listWorkItemType'
        $gridType.datasource = $dt
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已載入 $($gridKind.rows.count) 種預設工項類型（含主要特徵）與 $($dt.rows.count) 筆工項代表類型"
        }
    }

    enableGridHeaderSortAndLock -grid $gridKind -statusLabel $statusLabel -onEditRow {
        param($rowItem)
        if ($null -ne $rowItem) {
            $txtKindCode.text = [string]$rowItem.kindCode
            $txtKindName.text = [string]$rowItem.kindName
            if ($rowItem.table.columns.contains('badgeIcon') -and $rowItem.badgeIcon -ne [dbnull]::value) {
                $cmbKindIcon.text = [string]$rowItem.badgeIcon
            }
            if ($rowItem.table.columns.contains('badgeColor') -and $rowItem.badgeColor -ne [dbnull]::value) {
                $cmbKindColor.text = [string]$rowItem.badgeColor
            }
            if ($rowItem.table.columns.contains('sortOrder') -and $rowItem.sortOrder -ne [dbnull]::value) {
                $txtKindSort.text = [string]$rowItem.sortOrder
            }
            if ($rowItem.table.columns.contains('mainFeatureSummary') -and $rowItem.mainFeatureSummary -ne [dbnull]::value) {
                $txtMainFeature.text = [string]$rowItem.mainFeatureSummary
            }
            if ($rowItem.table.columns.contains('estimationTrait') -and $rowItem.estimationTrait -ne [dbnull]::value) {
                $txtEstTrait.text = [string]$rowItem.estimationTrait
            }
            if ($rowItem.table.columns.contains('timeAndDeliverableTrait') -and $rowItem.timeAndDeliverableTrait -ne [dbnull]::value) {
                $txtDelivTrait.text = [string]$rowItem.timeAndDeliverableTrait
            }
            if ($rowItem.table.columns.contains('description') -and $rowItem.description -ne [dbnull]::value) {
                $txtKindDesc.text = [string]$rowItem.description
            }
            if ($null -ne $statusLabel) {
                $statusLabel.text = "✏️ 已載入預設工項類型【$($rowItem.kindCode) - $($rowItem.kindName)】與主要特徵至上方表單"
            }
        }
    } -onDeleteRow {
        param($rowItem)
        if ($null -ne $rowItem) {
            $ans = [system.windows.forms.messagebox]::show("確定要刪除預設工項類型「$($rowItem.kindCode) - $($rowItem.kindName)」嗎？", '確認刪除預設工項類型', [system.windows.forms.messageboxbuttons]::yesno, [system.windows.forms.messageboxicon]::warning)
            if ($ans -eq [system.windows.forms.dialogresult]::yes) {
                $null = & $workItemScript -actionType 'deleteWorkItemKind' -kindCode ([string]$rowItem.kindCode)
                & $loadTypeData
            }
        }
    }

    $applyTypeRowToForm = {
        param($rowItem)
        if ($null -ne $rowItem) {
            if ($rowItem.table.columns.contains('workItemTypePk') -and $rowItem.workItemTypePk -ne [dbnull]::value) {
                $script:currentWorkItemTypePk = [int]$rowItem.workItemTypePk
            } else {
                $script:currentWorkItemTypePk = 0
            }
            $txtCode.text = [string]$rowItem.typeCode
            $txtName.text = [string]$rowItem.typeName
            if ($rowItem.table.columns.contains('defaultWorkItemKind') -and $rowItem.defaultWorkItemKind -ne [dbnull]::value) {
                $cmbDefaultKind.text = [string]$rowItem.defaultWorkItemKind
            }
            if ($rowItem.table.columns.contains('categoryName') -and $rowItem.categoryName -ne [dbnull]::value) {
                $txtCategory.text = [string]$rowItem.categoryName
            }
            if ($rowItem.table.columns.contains('standardStoryPoint') -and $rowItem.standardStoryPoint -ne [dbnull]::value) {
                $txtPoint.text = [string]$rowItem.standardStoryPoint
            } else {
                $txtPoint.text = ''
            }
            if ($rowItem.table.columns.contains('description') -and $rowItem.description -ne [dbnull]::value) {
                $txtTypeDesc.text = [string]$rowItem.description
            }
            if ($null -ne $statusLabel) {
                $statusLabel.text = "✏️ 已載入代表工項【$($rowItem.typeName) ($($rowItem.typeCode))】至 2-2 屬性編輯表單"
            }
        }
    }

    $gridType.add_cellclick({
        param($sender, $e)
        if ($e.rowindex -ge 0 -and $null -ne $gridType.rows[$e.rowindex].databounditem) {
            & $applyTypeRowToForm $gridType.rows[$e.rowindex].databounditem.row
        }
    })

    enableGridHeaderSortAndLock -grid $gridType -statusLabel $statusLabel -onEditRow {
        param($rowItem)
        & $applyTypeRowToForm $rowItem
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

    $btnUploadIcon.add_click({
        $dlg = new-object system.windows.forms.openfiledialog
        $dlg.title = '選擇要自行上傳新增的代表圖示檔案 (PNG / SVG / JPG / WEBP / ICO / GIF)'
        $dlg.filter = '圖示檔案 (*.png;*.svg;*.jpg;*.jpeg;*.webp;*.ico;*.gif)|*.png;*.svg;*.jpg;*.jpeg;*.webp;*.ico;*.gif'
        if ($dlg.showdialog() -eq [system.windows.forms.dialogresult]::ok) {
            $customName = if (-not [string]::isnullorempty($txtKindName.text)) { "$($txtKindName.text)圖示" } else { [system.io.path]::getfilenamewithoutextension($dlg.filename) }
            $upDt = & $workItemScript -actionType 'uploadWorkItemIcon' -iconFilePath $dlg.filename -iconName $customName
            & $loadIconData
            if ($null -ne $upDt -and $upDt.rows.count -gt 0) {
                $cmbKindIcon.text = [string]$upDt.rows[0].iconValue
                if ($null -ne $statusLabel) {
                    $statusLabel.text = "📤 已成功上傳並新增自訂代表圖示：$($upDt.rows[0].iconName) ($($upDt.rows[0].iconValue))"
                }
            }
        }
    })
    $btnSearchKind.add_click($loadKindData)
    $btnResetKind.add_click({
        $txtKindKw.text = ''
        & $loadKindData
    })
    $btnSaveKind.add_click({
        $sortVal = if (-not [string]::isnullorempty($txtKindSort.text)) { [int]$txtKindSort.text } else { 100 }
        $null = & $workItemScript -actionType 'upsertWorkItemKind' `
            -kindCode $txtKindCode.text -kindName $txtKindName.text -badgeIcon $cmbKindIcon.text `
            -badgeColor $cmbKindColor.text -sortOrder $sortVal `
            -mainFeatureSummary $txtMainFeature.text -estimationTrait $txtEstTrait.text `
            -timeAndDeliverableTrait $txtDelivTrait.text -description $txtKindDesc.text
        & $loadTypeData
    })
    $btnDeleteKind.add_click({
        if (-not [string]::isnullorempty($txtKindCode.text)) {
            $ans = [system.windows.forms.messagebox]::show("確定要刪除預設工項類型「$($txtKindCode.text) - $($txtKindName.text)」嗎？", '確認刪除預設工項類型', [system.windows.forms.messageboxbuttons]::yesno, [system.windows.forms.messageboxicon]::warning)
            if ($ans -eq [system.windows.forms.dialogresult]::yes) {
                $null = & $workItemScript -actionType 'deleteWorkItemKind' -kindCode $txtKindCode.text
                & $loadTypeData
            }
        }
    })

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
        $selKind = if (-not [string]::isnullorempty($cmbDefaultKind.text)) { $cmbDefaultKind.text } else { 'project' }
        $catVal = if (-not [string]::isnullorempty($txtCategory.text)) { $txtCategory.text } else { '一般代表類型' }
        $null = & $workItemScript -actionType 'upsertWorkItemType' `
            -workItemTypePk $script:currentWorkItemTypePk `
            -typeCode $txtCode.text -typeName $txtName.text -categoryName $catVal `
            -defaultWorkItemKind $selKind -description $txtTypeDesc.text `
            -standardStoryPoint $ptVal -syncExistingOpenWorkItem $chkSync.checked
        & $loadTypeData
    })

    $tabPage.controls.addrange(@($grpType, $grpKind))

    return $tabPage
}
