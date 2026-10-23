<#
.SYNOPSIS
    Azure 多人參與 Story Point 估算與工項權重管理系統 - 共用核心函數模組 (common.ps1)
.DESCRIPTION
    提供全系統 Console 腳本與多 Tab UI 共用的基礎設施函數：
    1. 讀取與儲存 JSON 設定檔 (config.json)
    2. 正式環境連線字串加密与解密 (DPAPI / SecureString)
    3. 遵循 .NET 標準層級 (0~4) 之雙軌 Log 系統 (SQL Server storyPoint.tbLog + 可由 bcp.exe 載入之 CSV 與 .log 檔)
    4. 具備自動 Retry 機制與執行進度呈現之高階包裝函數
    5. 透過 sp_executesql 完全參數化呼叫 SQL Server Stored Procedure 之資料存取函數
    6. 開放封閉原則 (OCP) 之報表匯出器註冊與密碼 SHA-256 雜湊計算函數
.NOTES
    檔案名稱: common.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
    編碼規範: PowerShell 7 (UTF-8 with BOM) / Windows PowerShell 5.1 (Unicode)
#>

set-strictmode -version latest

if (-not (get-variable -name 'scriptSessionTimestamp' -scope script -erroraction silentlycontinue)) {
    $script:scriptSessionTimestamp = (get-date).tostring('yyyyMMddHHmmss')
}

# 定義開放封閉原則 (Open-Closed Principle) 之報表匯出處理器註冊表
if (-not (get-variable -name 'reportExporterRegistry' -scope script -erroraction silentlycontinue)) {
    $script:reportExporterRegistry = @{}
}

<#
.SYNOPSIS
    將檔案重新儲存為具有 UTF-8 BOM 或 Unicode 編碼之格式
.DESCRIPTION
    確保所有產出之 .ps1、.csv、.log 檔案在繁體中文 Windows 环境下具備正確 BOM 編碼。
.PARAMETER filePath
    目標檔案路徑
.PARAMETER useUnicode
    是否強制使用 UTF-16 LE (Unicode) 編碼，預設為 $false (採用 UTF-8 with BOM)
#>
function saveFileWithStandardEncoding {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [string]$filePath,
        [parameter(mandatory = $false)]
        [string]$content = '',
        [parameter(mandatory = $false)]
        [bool]$useUnicode = $false
    )
    process {
        $dirPath = split-path -path $filePath -parent
        if (-not [string]::isnullorempty($dirPath) -and -not (test-path -path $dirPath)) {
            $null = new-item -itemtype directory -path $dirPath -force
        }
        if ($useUnicode) {
            $encodingObj = [system.text.encoding]::unicode
        }
        else {
            $encodingObj = new-object system.text.utf8encoding($true)
        }
        [system.io.file]::writealltext($filePath, $content, $encodingObj)
    }
}

<#
.SYNOPSIS
    取得專案根目錄路徑
.DESCRIPTION
    依據 common.ps1 所在之 ps 子目錄自動推算專案根目錄絕對路徑。
.OUTPUTS
    System.String 專案根目錄路徑
#>
function getProjectRootPath {
    [cmdletbinding()]
    param()
    begin {
        $currentDir = $psscriptroot
        if ([string]::isnullorempty($currentDir)) {
            $currentDir = (get-location).path
        }
    }
    process {
        if (test-path -path (join-path -path $currentDir -childpath 'config.json')) {
            return $currentDir
        }
        $parentDir = split-path -path $currentDir -parent
        if (test-path -path (join-path -path $parentDir -childpath 'config.json')) {
            return $parentDir
        }
        $grandParentDir = split-path -path $parentDir -parent
        if (-not [string]::isnullorempty($grandParentDir) -and (test-path -path (join-path -path $grandParentDir -childpath 'config.json'))) {
            return $grandParentDir
        }
        return $parentDir
    }
}

<#
.SYNOPSIS
    讀取系統設定檔 (預設為 config.json)
.DESCRIPTION
    從專案根目錄或指定路徑載入 JSON 格式設定檔，若 config.json 不存在則自動回退讀取 config/config.template.json。
.PARAMETER configPath
    設定檔完整或相對路徑，預設為 config.json
.OUTPUTS
    PSCustomObject 設定檔物件
#>
function getSystemConfig {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $false)]
        [string]$configPath = ''
    )
    process {
        $rootPath = getProjectRootPath
        if ([string]::isnullorempty($configPath)) {
            $configPath = join-path -path $rootPath -childpath 'config.json'
        }
        if (-not (test-path -path $configPath)) {
            $fallbackPath = join-path -path $rootPath -childpath 'config\config.template.json'
            if (test-path -path $fallbackPath) {
                $configPath = $fallbackPath
            }
            else {
                throw "找不到系統設定檔：$configPath"
            }
        }
        $rawJson = get-content -path $configPath -raw -encoding utf8
        return ($rawJson | convertfrom-json)
    }
}

<#
.SYNOPSIS
    加密敏感字串 (如正式環境 SQL Server 連線字串)
.DESCRIPTION
    使用 Windows Data Protection API (DPAPI) 將明文字串轉換為加密儲存格式，供正式環境部署使用。
.PARAMETER plainText
    欲加密的敏感明文字串
.OUTPUTS
    System.String 加密後的字串
#>
function protectSensitiveText {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [string]$plainText
    )
    process {
        $secureObj = convertto-securestring -string $plainText -asplaintext -force
        $encryptedText = convertfrom-securestring -securestring $secureObj
        return $encryptedText
    }
}

<#
.SYNOPSIS
    解密受保護的加密字串
.DESCRIPTION
    將透過 protectSensitiveText (DPAPI) 加密之字串還原為明文，供建立資料庫連線使用。
.PARAMETER encryptedText
    加密後的字串
.OUTPUTS
    System.String 解密後的明文字串
#>
function unprotectSensitiveText {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [string]$encryptedText
    )
    process {
        if ([string]::isnullorempty($encryptedText)) {
            return ''
        }
        $secureObj = convertto-securestring -string $encryptedText
        $bstr = [system.runtime.interopservices.marshal]::securestringtobstr($secureObj)
        try {
            return [system.runtime.interopservices.marshal]::ptrtostringbstr($bstr)
        }
        finally {
            [system.runtime.interopservices.marshal]::zerofreebstr($bstr)
        }
    }
}

<#
.SYNOPSIS
    計算字串之 SHA-256 小寫 Hex 雜湊值
.DESCRIPTION
    用於員工登入密碼雜湊計算，確保資料庫不儲存明文密碼。
.PARAMETER plainText
    欲雜湊之明文密碼
.OUTPUTS
    System.String 64 字元小寫 SHA-256 Hex 字串
#>
function getSha256Hash {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [string]$plainText
    )
    process {
        $sha256 = [system.security.cryptography.sha256]::create()
        try {
            $bytes = [system.text.encoding]::utf8.getbytes($plainText)
            $hashBytes = $sha256.computehash($bytes)
            $sb = new-object system.text.stringbuilder
            foreach ($b in $hashBytes) {
                $null = $sb.append($b.tostring('x2'))
            }
            return $sb.tostring()
        }
        finally {
            $sha256.dispose()
        }
    }
}

<#
.SYNOPSIS
    取得具備明確 Application Name 之 SQL Server 連線字串
.DESCRIPTION
    優先解密 encryptedConnectionString；若未設定則使用 plainConnectionStringForDev 或依 serverName / databaseName 組合，
    並強制確保包含 Application Name 設定。
.PARAMETER config
    設定檔物件 (若未傳入則自動讀取)
.OUTPUTS
    System.String 完整 SQL Server 連線字串
#>
function getSqlConnectionString {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $false)]
        [pscustomobject]$config = $null
    )
    process {
        if ($null -eq $config) {
            $config = getSystemConfig
        }
        $dbSetting = $config.databaseSetting
        $appName = if (-not [string]::isnullorempty($dbSetting.applicationName)) { $dbSetting.applicationName } else { 'azureStoryPointSystem' }
        $connStr = ''

        if (-not [string]::isnullorempty($dbSetting.encryptedConnectionString)) {
            try {
                $connStr = unprotectSensitiveText -encryptedText $dbSetting.encryptedConnectionString
            }
            catch {
                $connStr = ''
            }
        }

        if ([string]::isnullorempty($connStr) -and -not [string]::isnullorempty($dbSetting.plainConnectionStringForDev)) {
            $connStr = $dbSetting.plainConnectionStringForDev
        }

        if ([string]::isnullorempty($connStr)) {
            $srv = $dbSetting.serverName
            $db = $dbSetting.databaseName
            $connStr = "Server=$srv;Database=$db;Integrated Security=True;Application Name=$appName;TrustServerCertificate=True;"
        }

        if ($connStr -notmatch 'Application Name\s*=') {
            $connStr = $connStr.trimend(';') + ";Application Name=$appName;"
        }
        return $connStr
    }
}

<#
.SYNOPSIS
    寫入系統與錯誤雙軌 Log (檔案 .log + BCP 相容 CSV + SQL Server storyPoint.tbLog)
.DESCRIPTION
    遵守 .NET Log 層級 (0:不寫, 1:錯誤, 2:警告, 3:資訊, 4:任何訊息)。
    錯誤 Log 包含發生錯誤的 ps1 檔名、行號、嚴重層級、錯誤細節，寫入 logs/日期時間.log 與 logs/bcpLog_日期時間.csv，
    若設定啟用 DB Log 亦同步透過 storyPoint.spWriteLog 寫入資料庫。
.PARAMETER logLevel
    Log 層級 (1=錯誤, 2=警告, 3=資訊, 4=詳細除錯)
.PARAMETER sourceScript
    發生的 ps1 檔名或來源模組
.PARAMETER objectName
    操作的函數或 SP 物件名稱
.PARAMETER executionResult
    執行結果摘要訊息 (繁體中文)
.PARAMETER durationMillisecond
    耗時毫秒數
.PARAMETER errorLine
    發生錯誤的行號
.PARAMETER errorCode
    錯誤代碼
.PARAMETER errorMessage
    完整錯誤細節
.PARAMETER operatorAccount
    執行者帳號
#>
function writeSystemLog {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [int]$logLevel,
        [parameter(mandatory = $false)]
        [string]$sourceScript = 'common.ps1',
        [parameter(mandatory = $false)]
        [string]$objectName = 'general',
        [parameter(mandatory = $true)]
        [string]$executionResult,
        [parameter(mandatory = $false)]
        [int]$durationMillisecond = 0,
        [parameter(mandatory = $false)]
        [int]$errorLine = 0,
        [parameter(mandatory = $false)]
        [int]$errorCode = 0,
        [parameter(mandatory = $false)]
        [string]$errorMessage = '',
        [parameter(mandatory = $false)]
        [string]$operatorAccount = $env:username
    )
    process {
        try {
            $config = getSystemConfig
            $configuredLevel = [int]$config.logSetting.logLevel
            if ($configuredLevel -le 0 -or $logLevel -gt $configuredLevel) {
                return
            }

            $rootPath = getProjectRootPath
            $logDir = join-path -path $rootPath -childpath $config.logSetting.logDirectory
            if (-not (test-path -path $logDir)) {
                $null = new-item -itemtype directory -path $logDir -force
            }

            $nowStr = (get-date).tostring('yyyy-MM-dd HH:mm:ss.fff')
            $levelText = switch ($logLevel) {
                1 { '錯誤(Error)' }
                2 { '警告(Warning)' }
                3 { '資訊(Info)' }
                4 { '詳細(Verbose)' }
                default { '資訊(Info)' }
            }

            # 1. 寫入 logs 子目錄下之 日期時間.log 檔案
            $logFilePath = join-path -path $logDir -childpath "$($script:scriptSessionTimestamp).log"
            $logLine = "[$nowStr] [層級:$logLevel-$levelText] [檔案:$sourceScript] [行號:$errorLine] [物件:$objectName] [耗時:${durationMillisecond}ms] 結果:$executionResult | 錯誤細節:$errorMessage"
            add-content -path $logFilePath -value $logLine -encoding utf8

            # 2. 若啟用 CSV Log，寫入可直接透過 bcp.exe 載入 storyPoint.tbLog 之無表頭 Tab 分隔或標準 CSV 檔案
            if ($config.logSetting.enableCsvLog) {
                $csvFilePath = join-path -path $logDir -childpath "bcpLog_$($script:scriptSessionTimestamp).csv"
                $cleanResult = ($executionResult -replace '[\r\n,]', ' ')
                $cleanErr = ($errorMessage -replace '[\r\n,]', ' ')
                $csvLine = "0,$nowStr,$logLevel,$sourceScript,$objectName,$durationMillisecond,$cleanResult,$errorCode,$errorLine,$cleanErr,$operatorAccount"
                add-content -path $csvFilePath -value $csvLine -encoding utf8
            }

            # 3. 若啟用 DB Log，寫入 SQL Server storyPoint.tbLog
            if ($config.logSetting.enableDbLog) {
                $connStr = getSqlConnectionString -config $config
                $conn = new-object system.data.sqlclient.sqlconnection($connStr)
                try {
                    $conn.open()
                    $cmd = $conn.createcommand()
                    $cmd.commandtext = "exec sp_executesql N'exec storyPoint.spWriteLog @logLevel = @pLevel, @sourceName = @pSource, @objectName = @pObj, @durationMillisecond = @pDur, @executionResult = @pRes, @errorCode = @pErrCode, @errorLine = @pErrLine, @errorMessage = @pErrMsg, @operatorAccount = @pOp', N'@pLevel tinyint, @pSource varchar(128), @pObj nvarchar(128), @pDur int, @pRes nvarchar(256), @pErrCode int, @pErrLine int, @pErrMsg nvarchar(max), @pOp varchar(128)', @pLevel = @pLevel, @pSource = @pSource, @pObj = @pObj, @pDur = @pDur, @pRes = @pRes, @pErrCode = @pErrCode, @pErrLine = @pErrLine, @pErrMsg = @pErrMsg, @pOp = @pOp"
                    $null = $cmd.parameters.addwithvalue('@pLevel', [byte]$logLevel)
                    $null = $cmd.parameters.addwithvalue('@pSource', $sourceScript)
                    $null = $cmd.parameters.addwithvalue('@pObj', $objectName)
                    $null = $cmd.parameters.addwithvalue('@pDur', $durationMillisecond)
                    $null = $cmd.parameters.addwithvalue('@pRes', $executionResult)
                    $null = $cmd.parameters.addwithvalue('@pErrCode', $(if ($errorCode -gt 0) { $errorCode } else { [dbnull]::value }))
                    $null = $cmd.parameters.addwithvalue('@pErrLine', $(if ($errorLine -gt 0) { $errorLine } else { [dbnull]::value }))
                    $null = $cmd.parameters.addwithvalue('@pErrMsg', $(if (-not [string]::isnullorempty($errorMessage)) { $errorMessage } else { [dbnull]::value }))
                    $null = $cmd.parameters.addwithvalue('@pOp', $operatorAccount)
                    $null = $cmd.executenonquery()
                }
                catch {
                    # 若資料庫尚未初始化則僅保留檔案與 CSV Log
                }
                finally {
                    if ($conn.state -eq [system.data.connectionstate]::open) {
                        $conn.close()
                    }
                    $conn.dispose()
                }
            }
        }
        catch {
            # 忽略 Log 內部例外
        }
    }
}

<#
.SYNOPSIS
    具備自動 Retry 重試機制與執行進度呈現之高階執行函數
.DESCRIPTION
    當傳入之 ScriptBlock 發生例外錯誤時，自動依設定檔之 maxRetryCount 與 retryDelayMillisecond 進行重試，
    並將每次錯誤的 ps1 檔名、行號、嚴重層級與細節寫入雙軌 Log。
.PARAMETER operationTitle
    作業名稱 (用於呈現執行進度與 Log)
.PARAMETER scriptBlock
    欲執行的程式碼區塊
.PARAMETER maxRetryCount
    最大重試次數 (預設讀取 config.json)
.PARAMETER retryDelayMillisecond
    重試間隔毫秒數 (預設讀取 config.json)
#>
function invokeWithRetry {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [string]$operationTitle,
        [parameter(mandatory = $true)]
        [scriptblock]$scriptBlock,
        [parameter(mandatory = $false)]
        [int]$maxRetryCount = -1,
        [parameter(mandatory = $false)]
        [int]$retryDelayMillisecond = -1
    )
    process {
        $config = getSystemConfig
        if ($maxRetryCount -lt 0) {
            $maxRetryCount = [int]$config.databaseSetting.maxRetryCount
        }
        if ($retryDelayMillisecond -lt 0) {
            $retryDelayMillisecond = [int]$config.databaseSetting.retryDelayMillisecond
        }

        $attempt = 0
        $stopwatch = [system.diagnostics.stopwatch]::startnew()

        while ($true) {
            $attempt++
            try {
                write-progress -activity $operationTitle -status "正在執行 ($attempt/$($maxRetryCount + 1))..." -percentcomplete 35
                $result = & $scriptBlock
                $stopwatch.stop()
                write-progress -activity $operationTitle -status "執行完成" -percentcomplete 100 -completed

                writeSystemLog -logLevel 4 -sourceScript 'common.ps1' -objectName $operationTitle `
                    -executionResult "作業「$operationTitle」執行成功 (嘗試次數: $attempt)" `
                    -durationMillisecond ([int]$stopwatch.elapsedmilliseconds)
                return $result
            }
            catch {
                $errRecord = $_
                $errScript = if ($null -ne $errRecord.invocationinfo -and -not [string]::isnullorempty($errRecord.invocationinfo.scriptname)) {
                    split-path -path $errRecord.invocationinfo.scriptname -leaf
                }
                else {
                    'common.ps1'
                }
                $errLine = if ($null -ne $errRecord.invocationinfo) { [int]$errRecord.invocationinfo.scriptlinenumber } else { 0 }
                $errMsg = $errRecord.exception.message

                if ($attempt -le $maxRetryCount) {
                    writeSystemLog -logLevel 2 -sourceScript $errScript -objectName $operationTitle `
                        -executionResult "作業「$operationTitle」第 $attempt 次失敗，將於 ${retryDelayMillisecond}ms 後重試" `
                        -durationMillisecond ([int]$stopwatch.elapsedmilliseconds) -errorLine $errLine -errorCode 500 -errorMessage $errMsg
                    start-sleep -milliseconds $retryDelayMillisecond
                }
                else {
                    $stopwatch.stop()
                    write-progress -activity $operationTitle -status "執行失敗" -percentcomplete 100 -completed
                    writeSystemLog -logLevel 1 -sourceScript $errScript -objectName $operationTitle `
                        -executionResult "作業「$operationTitle」重試 $maxRetryCount 次後仍失敗" `
                        -durationMillisecond ([int]$stopwatch.elapsedmilliseconds) -errorLine $errLine -errorCode 500 -errorMessage $errMsg
                    throw
                }
            }
        }
    }
}

<#
.SYNOPSIS
    透過參數化與 sp_executesql 呼叫 SQL Server Stored Procedure 或查詢並回傳 DataSet
.DESCRIPTION
    不使用任何 OR Mapping 技術，全面採用 ADO.NET 參數化呼叫 Stored Procedure，支援回傳多個 DataTable (DataSet)。
.PARAMETER procedureName
    兩節式預存程序名稱 (如 'storyPoint.spGetStoryPointDashboard')
.PARAMETER parameters
    參數雜湊表 (Hashtable)
.OUTPUTS
    System.Data.DataSet 查詢結果集
#>
function invokeStoredProcedureDataSet {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [string]$procedureName,
        [parameter(mandatory = $false)]
        [hashtable]$parameters = @{}
    )
    process {
        return (invokeWithRetry -operationTitle "執行預存程序 $procedureName" -scriptBlock {
            $config = getSystemConfig
            $connStr = getSqlConnectionString -config $config
            $conn = new-object system.data.sqlclient.sqlconnection($connStr)
            try {
                $conn.open()
                $cmd = $conn.createcommand()
                $cmd.commandtype = [system.data.commandtype]::storedprocedure
                $cmd.commandtext = $procedureName
                $cmd.commandtimeout = [int]$config.databaseSetting.commandTimeoutSecond

                if ($null -ne $parameters) {
                    foreach ($key in $parameters.keys) {
                        $paramName = if ($key.startswith('@')) { $key } else { "@$key" }
                        $val = $parameters[$key]
                        if ($null -eq $val) {
                            $null = $cmd.parameters.addwithvalue($paramName, [dbnull]::value)
                        }
                        else {
                            $null = $cmd.parameters.addwithvalue($paramName, $val)
                        }
                    }
                }

                $adapter = new-object system.data.sqlclient.sqldataadapter($cmd)
                $dataSet = new-object system.data.dataset
                $null = $adapter.fill($dataSet)
                return ,$dataSet
            }
            finally {
                if ($conn.state -eq [system.data.connectionstate]::open) {
                    $conn.close()
                }
                $conn.dispose()
            }
        })
    }
}

<#
.SYNOPSIS
    透過 sp_executesql 參數化執行 T-SQL 語句並回傳 DataTable
.DESCRIPTION
    利用 sys.sp_executesql 執行參數化查詢，防止 SQL Injection。
.PARAMETER sqlText
    T-SQL 語句 (全小寫關鍵字)
.PARAMETER paramDefinition
    sp_executesql 參數型別定義字串 (如 '@deptPk int')
.PARAMETER parameters
    參數雜湊表
.OUTPUTS
    System.Data.DataTable
#>
function invokeParameterizedSql {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        [string]$sqlText,
        [parameter(mandatory = $false)]
        [string]$paramDefinition = '',
        [parameter(mandatory = $false)]
        [hashtable]$parameters = @{}
    )
    process {
        return (invokeWithRetry -operationTitle "執行參數化 SQL (sp_executesql)" -scriptBlock {
            $config = getSystemConfig
            $connStr = getSqlConnectionString -config $config
            $conn = new-object system.data.sqlclient.sqlconnection($connStr)
            try {
                $conn.open()
                $cmd = $conn.createcommand()
                $cmd.commandtimeout = [int]$config.databaseSetting.commandTimeoutSecond

                if ([string]::isnullorempty($paramDefinition) -or $null -eq $parameters -or $parameters.count -eq 0) {
                    $cmd.commandtype = [system.data.commandtype]::storedprocedure
                    $cmd.commandtext = 'sys.sp_executesql'
                    $null = $cmd.parameters.addwithvalue('@stmt', $sqlText)
                }
                else {
                    $cmd.commandtype = [system.data.commandtype]::storedprocedure
                    $cmd.commandtext = 'sys.sp_executesql'
                    $null = $cmd.parameters.addwithvalue('@stmt', $sqlText)
                    $null = $cmd.parameters.addwithvalue('@params', $paramDefinition)
                    foreach ($key in $parameters.keys) {
                        $paramName = if ($key.startswith('@')) { $key } else { "@$key" }
                        $val = $parameters[$key]
                        if ($null -eq $val) {
                            $null = $cmd.parameters.addwithvalue($paramName, [dbnull]::value)
                        }
                        else {
                            $null = $cmd.parameters.addwithvalue($paramName, $val)
                        }
                    }
                }

                $adapter = new-object system.data.sqlclient.sqldataadapter($cmd)
                $dt = new-object system.data.datatable
                $null = $adapter.fill($dt)
                return ,$dt
            }
            finally {
                if ($conn.state -eq [system.data.connectionstate]::open) {
                    $conn.close()
                }
                $conn.dispose()
            }
        })
    }
}

<#
.SYNOPSIS
    為 WinForms DataGridView 表格表頭啟用「點擊排序 (Sort)」與「右鍵鎖定/凍結欄位 (Lock/Freeze Column)」功能
.DESCRIPTION
    凡是 UI 中的表格 (DataGridView) 表頭皆提供：
    1. 左鍵點擊表頭：自動切換升冪 (▲) / 降冪 (▼) 排序 (Automatic SortMode)
    2. 右鍵點擊表頭：鎖定 (Freeze) 或解除鎖定該欄位 (將該欄位與左側欄位設為 Frozen 並於表頭標示 🔒)
.PARAMETER grid
    目標 System.Windows.Forms.DataGridView 控制項
.PARAMETER statusLabel
    狀態列標籤控制項 (選填)
#>
function enableGridHeaderSortAndLock {
    [cmdletbinding()]
    param(
        [parameter(mandatory = $true)]
        $grid,
        [parameter(mandatory = $false)]
        $statusLabel = $null
    )
    process {
        if ($null -eq $grid) {
            return
        }
        $grid.add_databindingcomplete({
            param($sender, $e)
            foreach ($col in $sender.columns) {
                $col.sortmode = [system.windows.forms.datagridviewsortmode]::automatic
                if ($col.headertext -notmatch '⇅|🔒') {
                    $col.headertext = "$($col.headertext) ⇅"
                }
            }
        })
        $grid.add_columnheadermouseclick({
            param($sender, $e)
            if ($e.button -eq [system.windows.forms.mousebuttons]::right -and $e.columnindex -ge 0) {
                $targetCol = $sender.columns[$e.columnindex]
                $newFrozenState = -not $targetCol.frozen
                if ($newFrozenState) {
                    $sender.autosizecolumnsmode = [system.windows.forms.datagridviewautosizecolumnsmode]::displayedcells
                    for ($i = 0; $i -le $e.columnindex; $i++) {
                        $c = $sender.columns[$i]
                        $c.frozen = $true
                        $cleanTitle = ($c.headertext -replace '\s*🔒', '' -replace '\s*⇅', '').trim()
                        $c.headertext = "$cleanTitle 🔒"
                    }
                    if ($null -ne $statusLabel) {
                        $statusLabel.text = "🔒 已鎖定表頭欄位至【$($targetCol.name)】（右鍵點擊表頭可解除鎖定）"
                    }
                }
                else {
                    for ($i = $e.columnindex; $i -lt $sender.columns.count; $i++) {
                        $c = $sender.columns[$i]
                        $c.frozen = $false
                        $cleanTitle = ($c.headertext -replace '\s*🔒', '' -replace '\s*⇅', '').trim()
                        $c.headertext = "$cleanTitle ⇅"
                    }
                    if ($null -ne $statusLabel) {
                        $statusLabel.text = "🔓 已解除表頭欄位【$($targetCol.name)】鎖定"
                    }
                }
            }
        })
    }
}
