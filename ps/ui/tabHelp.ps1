<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - [Tab 6] 線上說明與操作畫面搜尋頁籤 (tabHelp.ps1)
.DESCRIPTION
    提供系統完整線上說明、關鍵字即時搜尋、分類篩選與實機操作畫面圖解預覽（含點擊放大檢視）。
.PARAMETER psRootPath
    ps 模組根目錄路徑
.PARAMETER statusLabel
    主視窗底部狀態列控制項
.OUTPUTS
    System.Windows.Forms.TabPage
.NOTES
    檔案名稱: tabHelp.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-27
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $true)]
    [string]$psRootPath,
    [parameter(mandatory = $false)]
    $statusLabel = $null
)

process {
    $projectRoot = split-path -path $psRootPath -parent
    $imgRoot = join-path -path $projectRoot -childpath 'web\storyPointPortal\wwwroot\help\images'

    $tabPage = new-object system.windows.forms.tabpage
    $tabPage.text = '6. 📖 線上說明與搜尋'
    $tabPage.padding = new-object system.windows.forms.padding(10)

    $lblSearch = new-object system.windows.forms.label
    $lblSearch.text = '🔍 關鍵字搜尋：'
    $lblSearch.location = new-object system.drawing.point(12, 16)
    $lblSearch.autosize = $true

    $txtSearch = new-object system.windows.forms.textbox
    $txtSearch.location = new-object system.drawing.point(115, 13)
    $txtSearch.width = 300
    $txtSearch.text = ''

    $lblCat = new-object system.windows.forms.label
    $lblCat.text = '📂 分類篩選：'
    $lblCat.location = new-object system.drawing.point(430, 16)
    $lblCat.autosize = $true

    $cmbCat = new-object system.windows.forms.combobox
    $cmbCat.location = new-object system.drawing.point(520, 13)
    $cmbCat.width = 210
    $cmbCat.dropdownstyle = 'DropDownList'
    $null = $cmbCat.items.addrange(@(
        '全部章節',
        'Story Point 核心概念與工作量透明',
        '登入驗證與帳密安全',
        '多人即時盲投與回合設定',
        '工項、代表類型與到期扣點',
        '表格右鍵 Modal、排序鎖定與報表',
        '組織主管階層、批次匯入與系統 Log'
    ))
    $cmbCat.selectedindex = 0

    $btnClear = new-object system.windows.forms.button
    $btnClear.text = '🔄 清除搜尋'
    $btnClear.location = new-object system.drawing.point(745, 11)
    $btnClear.size = new-object system.drawing.size(110, 28)

    $btnOpenWebHelp = new-object system.windows.forms.button
    $btnOpenWebHelp.text = '🌐 開啟網頁版線上說明 (/help)'
    $btnOpenWebHelp.location = new-object system.drawing.point(870, 11)
    $btnOpenWebHelp.size = new-object system.drawing.size(220, 28)
    $btnOpenWebHelp.backcolor = [system.drawing.color]::fromargb(13, 148, 136)
    $btnOpenWebHelp.forecolor = [system.drawing.color]::white

    $gridHelp = new-object system.windows.forms.datagridview
    $gridHelp.location = new-object system.drawing.point(12, 50)
    $gridHelp.size = new-object system.drawing.size(1130, 195)
    $gridHelp.readonly = $true
    $gridHelp.allowusertoaddrows = $false
    $gridHelp.selectionmode = 'FullRowSelect'
    $gridHelp.multiselect = $false
    $gridHelp.autosizecolumnsmode = 'Fill'

    $rtbDetail = new-object system.windows.forms.richtextbox
    $rtbDetail.location = new-object system.drawing.point(12, 255)
    $rtbDetail.size = new-object system.drawing.size(540, 360)
    $rtbDetail.readonly = $true
    $rtbDetail.backcolor = [system.drawing.color]::fromargb(248, 250, 252)
    $rtbDetail.font = new-object system.drawing.font('Microsoft JhengHei UI', 9.5)

    $picBox = new-object system.windows.forms.picturebox
    $picBox.location = new-object system.drawing.point(565, 255)
    $picBox.size = new-object system.drawing.size(577, 360)
    $picBox.sizemode = 'Zoom'
    $picBox.borderstyle = 'FixedSingle'
    $picBox.backcolor = [system.drawing.color]::fromargb(15, 23, 42)
    $picBox.cursor = [system.windows.forms.cursors]::Hand

    $helpTopics = @(
        [pscustomobject]@{ topicCode = '00'; category = 'Story Point 核心概念與工作量透明'; title = '認識 Story Point：概念、用途、如何評估複雜度與讓團隊/個人工作量透明'; keywords = 'story point 故事點 概念 用途 複雜度 評估 技術複雜度 工作規模 未知風險 費氏數列 權重 工作量透明 進行中點數 已完成點數'; screenFile = 'screen_09_stats_table.png'; detail = "【主題 00：認識 Story Point — 概念、用途、複雜度評估與工作量透明化】`r`n① 核心概念：Story Point 評估的是任務客觀的「綜合相對複雜度」，而非估算「幾小時或幾天」，消除資深與資淺人員的速度差異爭論。`r`n② 如何評估複雜度（三大維度）：(1) 技術複雜度 (邏輯難度與架構影響)、(2) 工作投入規模 (修改範圍與測試工作量)、(3) 未知風險與不確定性 (相依性與變動風險)。`r`n③ 費氏數列評分尺規：0.5~1點(極簡基準尺)、2~3點(標準常規功能)、5~8點(中高複雜度跨模組)、13~21點+(大型高風險建議先拆解)、?(需求不明需先釐清)。`r`n④ 核心用途（讓團隊與個人工作量透明）：打破「只看工項件數」的盲點，搭配重要性權重乘數計算加權總分，即時透明呈現個人與部門的「進行中點數（在途負載）」與「已完成點數（實質產出貢獻）」。" },
        [pscustomobject]@{ topicCode = '01'; category = '登入驗證與帳密安全'; title = '員工帳號密碼強制登入驗證與安全登出'; keywords = '登入 密碼 login 登出 logout 角色 admin participant'; screenFile = 'screen_01_login.png'; detail = "【主題 01：員工帳號密碼強制登入驗證與安全登出】`r`n① 輸入員工登入帳號 (loginAccount)。`r`n② 輸入登入密碼 (支援點擊 👁️ 顯示確認)。`r`n③ 點擊「🔐 驗證身分並登入系統」通過 spVerifyEmployeeLogin 檢核。`r`n④ 作業完畢點擊右上角紅色「🚪 登出系統」安全釋放盲投鎖定並清除憑證。" },
        [pscustomobject]@{ topicCode = '02'; category = '登入驗證與帳密安全'; title = '自助修改個人登入帳號與密碼（免重登）'; keywords = '修改帳密 修改密碼 個人帳密 spUpdateEmployeeSelfCredential'; screenFile = 'screen_11_my_account.png'; detail = "【主題 02：自助修改個人登入帳號與密碼】`r`n① 點擊頂部「🔑 修改我的帳密」。`r`n② 修改「我的登入帳號」或輸入「新登入密碼 / 確認新密碼」。`r`n③ 點擊「💾 儲存修改我的登入帳號與密碼」，自動更新資料庫與登入 Cookie 免重新登入。" },
        [pscustomobject]@{ topicCode = '03'; category = '多人即時盲投與回合設定'; title = '多人即時盲投大廳：待參與 vs 參與中分類'; keywords = '盲投大廳 待參與 參與中 已拍板定案 資料夾收合 重新整理'; screenFile = 'screen_02_voting_lobby.png'; detail = "【主題 03：多人即時盲投大廳】`r`n① 左側投票議程清單自動依個人狀態分為「📂 待參與」、「🟢 參與中」與「📁 已拍板定案（預設收合）」。`r`n② 從「📂 待參與」點擊「🚀 點選進入『參與中』獨立網頁」開始盲投。`r`n③ 已開啟之議程自動移入「🟢 參與中」並啟動防重複進入保護。" },
        [pscustomobject]@{ topicCode = '04'; category = '多人即時盲投與回合設定'; title = '獨立盲投 Session 網頁：撲克牌意義、全員投完自動翻牌與定案'; keywords = '獨立網頁 撲克牌 疑問牌 ? 休息牌 ☕ 自動翻牌 下一回合 拍板定案 多網頁並行'; screenFile = 'screen_03_voting_standalone.png'; detail = "【主題 04：獨立盲投 Session 網頁】`r`n① 撲克牌面標示中文意義 (? 需求不明、☕ 暫停休息、0~100 複雜度)，支援雙擊或一鍵快投。`r`n② 當全員完成出牌時，spCastVote 自動翻牌揭曉並透過 SignalR 即時同步所有人畫面。`r`n③ 主持人可輸入討論紀錄開啟下一輪重新盲投，或點擊「🏆 一鍵採用推薦點數拍板定案」。`r`n④ 支援同時開啟多個獨立網頁參與不同 Session，但同一 Session 禁止重複進入。" },
        [pscustomobject]@{ topicCode = '05'; category = '多人即時盲投與回合設定'; title = '設定盲投回合：標的、點數範圍、受邀名單與共識規則'; keywords = '設定回合 估算標的 最低點數 最高點數 共識規則 倒數計時 快速範本'; screenFile = 'screen_04_round_setup.png'; detail = "【主題 05：設定盲投回合】`r`n① 支援一鍵套用「標準 Scrum / 快速閃電 / 嚴謹評審」範本。`r`n② 選擇估算標的（工項代表類型或特定工項；若為 0 筆支援自動建立新代表類型/工項）。`r`n③ 設定最低/最高 Story Point、共識演算法與計時秒數，下方即時預覽可用撲克牌面。`r`n④ 勾選受邀參與員工（支援全選或依部門批次勾選）並儲存啟動。" },
        [pscustomobject]@{ topicCode = '06'; category = '多人即時盲投與回合設定'; title = '事後查詢訂出 Story Point 的過程與點數異動歷程'; keywords = '估算過程 投票歷史 出牌明細 回合統計 點數異動歷程 tbPointHistory'; screenFile = 'screen_05_history.png'; detail = "【主題 06：估算過程與點數異動稽核查詢】`r`n① 查閱每場議程各回合的最低/最高/平均點數、回合討論紀錄與定案決議說明。`r`n② 查閱各回合參與者具名出牌點數與估算理由（一般員工僅顯示本人紀錄）。`r`n③ 查閱 tbPointHistory 點數異動稽核歷程表。" },
        [pscustomobject]@{ topicCode = '07'; category = '工項、代表類型與到期扣點'; title = '工項管理、代表類型自動帶點、起訖時間與到期自動扣點寄信'; keywords = '工項管理 代表類型自動帶入 起訖時間 天 周 月 季 年 到期自動扣點 Email 寄信 部門主管 HR'; screenFile = 'screen_06_workitems.png'; detail = "【主題 07：工項管理與到期自動扣點寄信】`r`n① 建立工項選擇「工項代表類型」即自動帶入標準 Story Point（或手動微調）。`r`n② 起訖時間快速設定：輸入【數量】+ 選擇【天/周/月/季/年】自動計算迄時。`r`n③ 單一 Modal 整合負責員工與分攤比例指派。`r`n④ 達到迄時自動轉為已完成、扣除工項與員工/部門有效點數歸零，並寄發 Email 通知該部門所有部門主管與 HR 主管。" },
        [pscustomobject]@{ topicCode = '08'; category = '表格右鍵 Modal、排序鎖定與報表'; title = '全系統表格即時篩選、滑鼠右鍵選單與雙擊彈出單筆編輯 Modal'; keywords = '滑鼠右鍵 右鍵選單 雙擊 Modal 視窗 單筆編輯 不捲動網頁 表格篩選'; screenFile = 'screen_07_context_modal.png'; detail = "【主題 08：表格滑鼠右鍵選單與雙擊單筆編輯 Modal】`r`n① 表格工具列提供「指定欄位 + 欄位既有值下拉 + 關鍵字」即時過濾。`r`n② 在資料列「雙擊滑鼠左鍵」（或按 Enter / F2）直接原地彈出置中單筆編輯 Modal 視窗，絕不上下捲動網頁。`r`n③ 在資料列按「滑鼠右鍵」彈出專屬操作選單（單筆編輯、指派員工、發起盲投、設為已到期測試、篩選、複製、刪除）。" },
        [pscustomobject]@{ topicCode = '09'; category = '工項、代表類型與到期扣點'; title = '工項代表類型標準點數管理與工項類型 CRUD'; keywords = '工項代表類型 標準點數 同步既有工項 工項類型 專案 維運 顧問時數 教育訓練 開發產品'; screenFile = 'screen_08_types.png'; detail = "【主題 09：工項代表類型與工項類型管理】`r`n① 管理工項代表類型與標準 Story Point，支援勾選同步更新未完成且未微調之既有工項。`r`n② 管理工項類型主檔 (tbWorkItemKind)：預設含專案、維運、顧問時數、教育訓練、開發產品，支援完整增刪修。" },
        [pscustomobject]@{ topicCode = '10'; category = '表格右鍵 Modal、排序鎖定與報表'; title = '員工與部門總點數統計、表頭排序鎖定與 CSV/Excel/HTML 匯出'; keywords = '統計報表 部門總點數 員工總點數 表頭排序 欄位鎖定 Sticky 匯出 CSV Excel 互動 HTML 群組彙總'; screenFile = 'screen_09_stats_table.png'; detail = "【主題 10：統計報表、表頭排序鎖定與三格式匯出】`r`n① 即時統計各部門與每位員工的進行中/已完成/總 Story Point 與上一層主管。`r`n② 點擊表頭切換升冪 ▲ / 降冪 ▼ 排序；點擊 📌 切換為 🔒 將該欄凍結固定於左側。`r`n③ 支援匯出 CSV、Excel (.xlsx) 與內建排序/篩選/動態群組彙總之互動式 HTML 報表。" },
        [pscustomobject]@{ topicCode = '11'; category = '組織主管階層、批次匯入與系統 Log'; title = '組織與員工管理：多位部門主管、上一層主管樹狀階層與批次匯入'; keywords = '員工管理 部門主管 isDepartmentManager 樹狀階層 上一層主管 批次匯入 CSV Excel'; screenFile = 'screen_10_employees_org.png'; detail = "【主題 11：組織與員工帳密管理】`r`n① 支援設定同部門多位部門主管 (isDepartmentManager=1) 與樹狀遞迴上一層主管 (managerEmployeePk)。`r`n② 列表僅精簡呈現上一層主管，工項到期時同部門所有主管與 HR 主管皆會收到通知。`r`n③ 支援上傳 CSV 或 Excel (.xlsx) 批次匯入員工名單與自動建立部門。" },
        [pscustomobject]@{ topicCode = '12'; category = '組織主管階層、批次匯入與系統 Log'; title = '系統防崩潰保護、錯誤 Log 查詢中心與 PowerShell GUI'; keywords = '系統 Log 錯誤 Log tbLog 防崩潰 StackTrace 錯誤測試 PowerShell uiMain.ps1'; screenFile = 'screen_12_logs.png'; detail = "【主題 12：系統防崩潰 Log 查詢中心與 PowerShell 工具】`r`n① 三層防崩潰保護（SQL TRY-CATCH + Controller 攔截 + 全域 Middleware）確保絕不當機。`r`n② 管理者可依關鍵字、Log 嚴重層級、來源模組即時過濾 tbLog 並展開 StackTrace，或執行一鍵防崩潰測試。" }
    )

    $refreshHelpGrid = {
        $kw = $txtSearch.text.trim().tolowerinvariant()
        $cat = [string]$cmbCat.selecteditem
        $dt = new-object system.data.datatable
        $null = $dt.columns.add('主題編號')
        $null = $dt.columns.add('功能分類')
        $null = $dt.columns.add('線上說明主題標題')
        $null = $dt.columns.add('搜尋關鍵字')
        $null = $dt.columns.add('操作畫面檔名')
        $null = $dt.columns.add('步驟詳解內容')

        foreach ($item in $helpTopics) {
            if ($cat -ne '全部章節' -and $item.category -ne $cat) { continue }
            $blob = "$($item.topicCode) $($item.category) $($item.title) $($item.keywords) $($item.detail)".tolowerinvariant()
            if (-not [string]::isnullorempty($kw)) {
                $tokens = $kw -split '\s+'
                $allMatched = $true
                foreach ($tk in $tokens) {
                    if (-not [string]::isnullorempty($tk) -and $blob.indexof($tk) -lt 0) {
                        $allMatched = $false
                        break
                    }
                }
                if (-not $allMatched) { continue }
            }
            $row = $dt.newrow()
            $row['主題編號'] = $item.topicCode
            $row['功能分類'] = $item.category
            $row['線上說明主題標題'] = $item.title
            $row['搜尋關鍵字'] = $item.keywords
            $row['操作畫面檔名'] = $item.screenFile
            $row['步驟詳解內容'] = $item.detail
            $dt.rows.add($row)
        }

        $gridHelp.datasource = $dt
        if ($gridHelp.columns.contains('步驟詳解內容')) {
            $gridHelp.columns['步驟詳解內容'].visible = $false
        }
        if ($null -ne $statusLabel) {
            $statusLabel.text = "📖 線上說明搜尋結果：共找到 $($dt.rows.count) 個相符的操作畫面主題（點選任一列可檢視步驟與實機操作畫面，點擊右側圖片可放大檢視）"
        }
    }

    $gridHelp.add_selectionchanged({
        if ($gridHelp.selectedrows.count -gt 0) {
            $selRow = $gridHelp.selectedrows[0]
            $detailTxt = [string]$selRow.cells['步驟詳解內容'].value
            $imgFile = [string]$selRow.cells['操作畫面檔名'].value
            $rtbDetail.text = "$detailTxt`r`n`r`n🖼️ 搭配實機操作畫面檔名：$imgFile（點擊右側畫面可開啟全螢幕放大檢視視窗）"
            $fullImgPath = join-path -path $imgRoot -childpath $imgFile
            if (test-path -path $fullImgPath) {
                try {
                    if ($null -ne $picBox.image) { $picBox.image.dispose() }
                    $bytes = [system.io.file]::readallbytes($fullImgPath)
                    $ms = new-object system.io.memorystream(,$bytes)
                    $picBox.image = [system.drawing.image]::fromstream($ms)
                    $picBox.tag = $fullImgPath
                }
                catch { }
            }
        }
    })

    $picBox.add_click({
        $imgPath = [string]$picBox.tag
        if (-not [string]::isnullorempty($imgPath) -and (test-path -path $imgPath)) {
            $zoomForm = new-object system.windows.forms.form
            $zoomForm.text = "🖥️ 實機操作畫面放大檢視 - $(split-path -path $imgPath -leaf)"
            $zoomForm.size = new-object system.drawing.size(1180, 760)
            $zoomForm.startposition = 'CenterParent'
            $zoomPic = new-object system.windows.forms.picturebox
            $zoomPic.dock = 'Fill'
            $zoomPic.sizemode = 'Zoom'
            $zoomPic.backcolor = [system.drawing.color]::fromargb(15, 23, 42)
            $bytes = [system.io.file]::readallbytes($imgPath)
            $ms = new-object system.io.memorystream(,$bytes)
            $zoomPic.image = [system.drawing.image]::fromstream($ms)
            $zoomForm.controls.add($zoomPic)
            $null = $zoomForm.showdialog()
        }
    })

    $txtSearch.add_textchanged($refreshHelpGrid)
    $cmbCat.add_selectedindexchanged($refreshHelpGrid)
    $btnClear.add_click({
        $txtSearch.text = ''
        $cmbCat.selectedindex = 0
        & $refreshHelpGrid
    })
    $btnOpenWebHelp.add_click({
        start-process 'http://localhost:5088/help'
    })

    enableGridHeaderSortAndLock -grid $gridHelp -statusLabel $statusLabel

    $tabPage.controls.addrange(@(
        $lblSearch, $txtSearch, $lblCat, $cmbCat,
        $btnClear, $btnOpenWebHelp, $gridHelp, $rtbDetail, $picBox
    ))

    & $refreshHelpGrid
    return $tabPage
}
