<#
.SYNOPSIS
    資料庫與 T-SQL 物件自動化建置與初始化腳本 (initDatabase.ps1)
.DESCRIPTION
    自動檢查並建立目標資料庫 (storyPointDb)，依序執行：
    1. sql/schemaAndTable.sql (包含 storyPoint Schema、無損資料保留與所有 tb* 資料表)
    2. sql/viewAndFunction.sql (包含 fn* 與 vw* 統計檢視表)
    3. sql/storedProcedure.sql (包含所有 sp* 預存程序)
    4. sql/seedData.sql (可選，預設載入部門、帳號、牌組、代表類型、示範投票過程與工項資料)
.PARAMETER skipSeedData
    若指定此開關，則僅部署結構與 SP，不執行 seedData.sql
.NOTES
    檔案名稱: initDatabase.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [switch]$skipSeedData
)

. (join-path -path $psscriptroot -childpath 'common.ps1')

function splitSqlBatches {
    param([string]$rawSql)
    $batches = [system.text.regularexpressions.regex]::split($rawSql, '^\s*go\s*$', [system.text.regularexpressions.regexoptions]::multiline -bor [system.text.regularexpressions.regexoptions]::ignorecase)
    return ($batches | where-object { -not [string]::isnullorwhitespace($_) })
}

invokeWithRetry -operationTitle '初始化 Story Point 資料庫與 T-SQL 物件' -scriptBlock {
    $config = getSystemConfig
    $rootPath = getProjectRootPath
    $connStr = getSqlConnectionString -config $config

    # 1. 檢查並建立資料庫 (若連線至本機或自建 SQL Server)
    $builder = new-object system.data.sqlclient.sqlconnectionstringbuilder($connStr)
    $targetDb = [string]$builder['Initial Catalog']
    if ([string]::isnullorempty($targetDb)) {
        $targetDb = $config.databaseSetting.databaseName
    }

    if ($connStr -notmatch 'database\.windows\.net') {
        $masterBuilder = new-object system.data.sqlclient.sqlconnectionstringbuilder($connStr)
        $masterBuilder['Initial Catalog'] = 'master'

        $masterConn = new-object system.data.sqlclient.sqlconnection($masterBuilder.connectionstring)
        try {
            $masterConn.open()
            $checkCmd = $masterConn.createcommand()
            $checkCmd.commandtext = "if db_id(@dbName) is null begin declare @sql nvarchar(512) = N'create database [' + replace(@dbName, N']', N']]') + N']'; exec sp_executesql @sql; end"
            $null = $checkCmd.parameters.addwithvalue('@dbName', $targetDb)
            $null = $checkCmd.executenonquery()
        }
        catch {
            # 若無建立 master DB 權限則直接略過，連線至既有資料庫
        }
        finally {
            if ($masterConn.state -eq [system.data.connectionstate]::open) {
                $masterConn.close()
            }
            $masterConn.dispose()
        }
    }

    # 2. 依序執行 SQL 腳本檔案
    $sqlFiles = @(
        'sql\schemaAndTable.sql',
        'sql\viewAndFunction.sql',
        'sql\storedProcedure.sql'
    )
    if (-not $skipSeedData.ispresent) {
        $sqlFiles += 'sql\seedData.sql'
    }

    $conn = new-object system.data.sqlclient.sqlconnection($connStr)
    try {
        $conn.open()
        $totalFiles = $sqlFiles.count
        $currentIdx = 0

        foreach ($relPath in $sqlFiles) {
            $currentIdx++
            $fullSqlPath = join-path -path $rootPath -childpath $relPath
            $pct = [int](($currentIdx / $totalFiles) * 100)
            write-progress -activity '部署 SQL 腳本' -status "正在執行 $relPath ($currentIdx/$totalFiles)..." -percentcomplete $pct

            $rawSql = get-content -path $fullSqlPath -raw -encoding utf8
            $batches = splitSqlBatches -rawSql $rawSql

            foreach ($batch in $batches) {
                $cmd = $conn.createcommand()
                $cmd.commandtimeout = 120
                $cmd.commandtext = $batch
                $null = $cmd.executenonquery()
            }
        }
        write-progress -activity '部署 SQL 腳本' -status '全部 SQL 腳本執行完成' -percentcomplete 100 -completed
    }
    finally {
        if ($conn.state -eq [system.data.connectionstate]::open) {
            $conn.close()
        }
        $conn.dispose()
    }

    writeSystemLog -logLevel 3 -sourceScript 'initDatabase.ps1' -objectName 'storyPointDb' `
        -executionResult "成功完成資料庫 [$targetDb] 所有資料表、檢視表、預存程序與預設資料部署"

    return [pscustomobject]@{
        status = 'success'
        databaseName = $targetDb
        executedScripts = ($sqlFiles -join ', ')
        message = "資料庫 [$targetDb] 初始化與 T-SQL 部署成功！"
    }
}
