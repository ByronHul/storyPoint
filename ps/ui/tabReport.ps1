<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - [Tab 5] 員工與所屬部門總 Story Point 統計報表頁籤 (tabReport.ps1)
.DESCRIPTION
    獨立實作「查詢員工身上有多少 Story Point、員工所屬部門的總 Story Point 數、以及匯出全套 CSV 報表」頁籤。
    所有功能一律呼叫 ps/getStoryPointReport.ps1。
.PARAMETER psRootPath
    ps 模組根目錄路徑
.PARAMETER statusLabel
    主視窗底部狀態列控制項
.OUTPUTS
    System.Windows.Forms.TabPage
.NOTES
    檔案名稱: tabReport.ps1
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
    $reportScript = join-path -path $psRootPath -childpath 'getStoryPointReport.ps1'
    $tabPage = new-object system.windows.forms.tabpage
    $tabPage.text = '5. 員工與部門總點數統計報表'
    $tabPage.padding = new-object system.windows.forms.padding(10)

    $btnRefresh = new-object system.windows.forms.button
    $btnRefresh.text = '📊 重新計算部門與員工總 Story Point'
    $btnRefresh.location = new-object system.drawing.point(12, 10)
    $btnRefresh.size = new-object system.drawing.size(260, 32)
    $btnRefresh.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnRefresh.forecolor = [system.drawing.color]::white

    $btnExportAll = new-object system.windows.forms.button
    $btnExportAll.text = '📥 匯出全套 CSV 報表 (BCP 相容)'
    $btnExportAll.location = new-object system.drawing.point(285, 10)
    $btnExportAll.size = new-object system.drawing.size(240, 32)

    $lblDept = new-object system.windows.forms.label
    $lblDept.text = '一、各部門總 Story Point 數與負載統計 (vwDepartmentPointSummary)：'
    $lblDept.location = new-object system.drawing.point(12, 52)
    $lblDept.autosize = $true

    $gridDept = new-object system.windows.forms.datagridview
    $gridDept.location = new-object system.drawing.point(12, 76)
    $gridDept.size = new-object system.drawing.size(1130, 210)
    $gridDept.readonly = $true
    $gridDept.allowusertoaddrows = $false
    $gridDept.autosizecolumnsmode = 'Fill'
    enableGridHeaderSortAndLock -grid $gridDept -statusLabel $statusLabel

    $lblEmp = new-object system.windows.forms.label
    $lblEmp.text = '二、各員工身上的工項與總 Story Point 數明細 (vwEmployeePointSummary)：'
    $lblEmp.location = new-object system.drawing.point(12, 298)
    $lblEmp.autosize = $true

    $gridEmp = new-object system.windows.forms.datagridview
    $gridEmp.location = new-object system.drawing.point(12, 322)
    $gridEmp.size = new-object system.drawing.size(1130, 300)
    $gridEmp.readonly = $true
    $gridEmp.allowusertoaddrows = $false
    $gridEmp.autosizecolumnsmode = 'Fill'
    enableGridHeaderSortAndLock -grid $gridEmp -statusLabel $statusLabel

    $loadReportData = {
        $ds = & $reportScript -reportType 'allDashboard'
        $gridDept.datasource = $ds.tables[0]
        $gridEmp.datasource = $ds.tables[1]
        if ($null -ne $statusLabel) {
            $statusLabel.text = "統計完成：共 $($ds.tables[0].rows.count) 個部門、$($ds.tables[1].rows.count) 位員工"
        }
    }

    $btnRefresh.add_click($loadReportData)
    $btnExportAll.add_click({
        $res = & $reportScript -reportType 'exportAllCsv'
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已匯出 4 份報表至 reports 目錄：$($res.departmentCsvPath)"
        }
    })

    $tabPage.controls.addrange(@(
        $btnRefresh, $btnExportAll,
        $lblDept, $gridDept,
        $lblEmp, $gridEmp
    ))

    return $tabPage
}
