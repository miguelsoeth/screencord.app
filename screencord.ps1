<#
.SYNOPSIS
    Painel de desenvolvimento do ScreenCord: sobe, para, recompila e mostra os logs da API e do app.

.DESCRIPTION
    Sem parâmetros abre um menu interativo (setas para navegar, Enter para executar, Esc para sair).
    Com -Action executa uma ação direta e termina, útil em scripts.

    Os processos rodam em segundo plano e continuam vivos depois que o menu fecha.
    PIDs e logs ficam em .screencord\ (ignorada pelo Git).

.EXAMPLE
    .\screencord.ps1

.EXAMPLE
    .\screencord.ps1 start-all
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('menu', 'status', 'start-api', 'stop-api', 'start-app', 'stop-app', 'start-all', 'stop-all',
                 'build', 'rebuild', 'test', 'logs-api', 'logs-app')]
    [string]$Action = 'menu'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$StateDir = Join-Path $Root '.screencord'
$LogDir = Join-Path $StateDir 'logs'
$Solution = Join-Path $Root 'ScreenCord.sln'
$ApiPort = 5080
$ApiUrl = "http://localhost:$ApiPort"

# Encoding dos logs gerados pelos processos .NET redirecionados para arquivo.
$LogEncoding = New-Object System.Text.UTF8Encoding($false)

$Components = [ordered]@{
    api = @{
        Name     = 'API'
        WorkDir  = Join-Path $Root 'src\ScreenCord.Api'
        Output   = Join-Path $Root 'src\ScreenCord.Api\bin\Debug\net10.0\ScreenCord.Api.dll'
        PidFile  = Join-Path $StateDir 'api.pid'
        OutLog   = Join-Path $LogDir 'api.log'
        ErrLog   = Join-Path $LogDir 'api.err.log'
    }
    app = @{
        Name     = 'App'
        WorkDir  = Join-Path $Root 'src\ScreenCord\bin\Debug\net10.0-windows10.0.22621.0'
        Output   = Join-Path $Root 'src\ScreenCord\bin\Debug\net10.0-windows10.0.22621.0\ScreenCord.exe'
        PidFile  = Join-Path $StateDir 'app.pid'
        OutLog   = Join-Path $LogDir 'app.log'
        ErrLog   = Join-Path $LogDir 'app.err.log'
    }
}

$script:Message = ''
$script:MessageColor = 'Gray'

function Set-Message([string]$Text, [string]$Color = 'Gray') {
    $script:Message = $Text
    $script:MessageColor = $Color
    if ($Action -ne 'menu' -and $Text) { Write-Host $Text -ForegroundColor $Color }
}

function Initialize-StateDir {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
}

# ---------------------------------------------------------------------------
# Processos
# ---------------------------------------------------------------------------

# Retorna o processo registrado, conferindo o horário de início para não confundir com um PID reaproveitado.
function Get-TrackedProcess([string]$Key) {
    $c = $Components[$Key]
    if (-not (Test-Path $c.PidFile)) { return $null }

    $parts = (Get-Content $c.PidFile -Raw).Trim() -split '\|'
    if ($parts.Count -ne 2) { return $null }

    $proc = Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue
    if ($null -eq $proc) { return $null }
    try {
        if ($proc.StartTime.ToUniversalTime().Ticks -ne [long]$parts[1]) { return $null }
    } catch {
        return $null
    }
    return $proc
}

function Save-TrackedProcess([string]$Key, $Process) {
    $ticks = $Process.StartTime.ToUniversalTime().Ticks
    Set-Content -Path $Components[$Key].PidFile -Value "$($Process.Id)|$ticks" -Encoding ASCII
}

function Clear-TrackedProcess([string]$Key) {
    Remove-Item -Path $Components[$Key].PidFile -Force -ErrorAction SilentlyContinue
}

function Get-PortOwner([int]$Port) {
    try {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1
        return Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
    } catch {
        return $null
    }
}

function Test-ApiHealthy {
    try {
        $response = Invoke-WebRequest -Uri "$ApiUrl/health" -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Read-LogTail([string]$Path, [int]$Lines) {
    if (-not (Test-Path $Path)) { return @() }
    # FileShare.ReadWrite: o processo continua escrevendo no arquivo enquanto lemos.
    $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        $reader = New-Object System.IO.StreamReader($stream, $LogEncoding)
        $all = $reader.ReadToEnd() -split "`r?`n" | Where-Object { $_ -ne '' }
        return @($all | Select-Object -Last $Lines)
    } finally {
        $stream.Dispose()
    }
}

function Start-Component([string]$Key) {
    $c = $Components[$Key]
    Initialize-StateDir

    $running = Get-TrackedProcess $Key
    if ($running) {
        Set-Message "$($c.Name) já está rodando (PID $($running.Id))." 'Yellow'
        return $true
    }

    if (-not (Test-Path $c.Output)) {
        Write-Host "Compilação não encontrada para $($c.Name); compilando..." -ForegroundColor Cyan
        if (-not (Invoke-Build)) { return $false }
    }

    if ($Key -eq 'api') {
        $owner = Get-PortOwner $ApiPort
        if ($owner) {
            Set-Message "A porta $ApiPort já está em uso por '$($owner.ProcessName)' (PID $($owner.Id)). Feche esse processo antes de subir a API." 'Red'
            return $false
        }
    }

    # Logs da execução anterior são preservados com o sufixo .old.
    foreach ($log in @($c.OutLog, $c.ErrLog)) {
        if (Test-Path $log) { Move-Item -Force -Path $log -Destination "$log.old" }
    }

    if ($Key -eq 'api') {
        # A dll é executada diretamente (sem 'dotnet run') para que o PID registrado seja o da própria API.
        $previousEnv = $env:ASPNETCORE_ENVIRONMENT
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        try {
            $proc = Start-Process -FilePath 'dotnet' `
                -ArgumentList @("`"$($c.Output)`"", '--urls', $ApiUrl) `
                -WorkingDirectory $c.WorkDir `
                -RedirectStandardOutput $c.OutLog -RedirectStandardError $c.ErrLog `
                -WindowStyle Hidden -PassThru
        } finally {
            $env:ASPNETCORE_ENVIRONMENT = $previousEnv
        }
    } else {
        $proc = Start-Process -FilePath $c.Output `
            -WorkingDirectory $c.WorkDir `
            -RedirectStandardOutput $c.OutLog -RedirectStandardError $c.ErrLog `
            -PassThru
    }

    Save-TrackedProcess $Key $proc

    if ($Key -eq 'api') {
        Write-Host "Aguardando a API responder em $ApiUrl..." -ForegroundColor Cyan
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            if ($proc.HasExited) {
                Clear-TrackedProcess $Key
                $tail = (Read-LogTail $c.ErrLog 5) + (Read-LogTail $c.OutLog 5)
                Set-Message ("A API encerrou ao iniciar. Últimas linhas:`n" + ($tail -join "`n")) 'Red'
                return $false
            }
            if (Test-ApiHealthy) {
                Set-Message "API no ar em $ApiUrl (PID $($proc.Id))." 'Green'
                return $true
            }
            Start-Sleep -Milliseconds 500
        }
        Set-Message "A API foi iniciada (PID $($proc.Id)), mas não respondeu em 30 s. Veja os logs." 'Yellow'
        return $true
    }

    Start-Sleep -Milliseconds 800
    if ($proc.HasExited) {
        Clear-TrackedProcess $Key
        Set-Message "O app encerrou logo após abrir. Veja os logs." 'Red'
        return $false
    }
    Set-Message "App aberto (PID $($proc.Id))." 'Green'
    return $true
}

function Stop-Component([string]$Key) {
    $c = $Components[$Key]
    $proc = Get-TrackedProcess $Key
    if ($null -eq $proc) {
        Clear-TrackedProcess $Key
        Set-Message "$($c.Name) não está rodando." 'Yellow'
        return
    }

    # O app recebe primeiro um pedido de fechamento normal; a API (sem janela) é encerrada diretamente.
    if ($Key -eq 'app' -and $proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(3000)) { Stop-Process -Id $proc.Id -Force }
    } else {
        Stop-Process -Id $proc.Id -Force
        $null = $proc.WaitForExit(5000)
    }

    Clear-TrackedProcess $Key
    Set-Message "$($c.Name) parado." 'Green'
}

# ---------------------------------------------------------------------------
# Build e testes
# ---------------------------------------------------------------------------

function Invoke-Dotnet([string[]]$Arguments) {
    # Saída nativa direto no console; em PowerShell 5.1, redirecionar stderr transformaria avisos em erros.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & dotnet @Arguments | Out-Host
        return $LASTEXITCODE -eq 0
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-Build {
    Write-Host "Compilando a solução..." -ForegroundColor Cyan
    $ok = Invoke-Dotnet @('build', $Solution, '--nologo', '-v', 'q')
    if ($ok) { Set-Message 'Compilação concluída.' 'Green' } else { Set-Message 'A compilação falhou. Veja os erros acima.' 'Red' }
    return $ok
}

# Para o que estiver rodando (as dlls ficam travadas), compila e sobe de novo o que estava no ar.
function Invoke-Rebuild {
    $wasRunning = @()
    foreach ($key in $Components.Keys) {
        if (Get-TrackedProcess $key) {
            $wasRunning += $key
            Write-Host "Parando $($Components[$key].Name)..." -ForegroundColor Cyan
            Stop-Component $key
        }
    }

    if (-not (Invoke-Build)) { return $false }

    foreach ($key in $wasRunning) {
        if (-not (Start-Component $key)) { return $false }
    }

    $summary = if ($wasRunning.Count -gt 0) { "Recompilado; reiniciado: " + (($wasRunning | ForEach-Object { $Components[$_].Name }) -join ', ') + '.' } else { 'Recompilado.' }
    Set-Message $summary 'Green'
    return $true
}

function Invoke-Tests {
    $running = @($Components.Keys | Where-Object { Get-TrackedProcess $_ })
    if ($running.Count -gt 0) {
        Write-Host "Aviso: com API/app rodando, a compilação dos testes pode falhar por arquivos em uso. Use 'Recompilar tudo' antes, se precisar." -ForegroundColor Yellow
    }
    Write-Host "Rodando os testes..." -ForegroundColor Cyan
    $ok = Invoke-Dotnet @('test', $Solution, '--nologo', '-v', 'q')
    if ($ok) { Set-Message 'Todos os testes passaram.' 'Green' } else { Set-Message 'Há testes falhando (ou a compilação falhou). Veja acima.' 'Red' }
    return $ok
}

# ---------------------------------------------------------------------------
# Logs
# ---------------------------------------------------------------------------

function Write-LogLine([string]$Line, [bool]$IsError) {
    $color = 'Gray'
    if ($IsError -or $Line -match '^\s*(fail|crit)[:\[]|exception') { $color = 'Red' }
    elseif ($Line -match '^\s*warn[:\[]') { $color = 'Yellow' }
    elseif ($Line -match '^\s*info[:\[]') { $color = 'DarkCyan' }
    Write-Host $Line -ForegroundColor $color
}

function Show-Logs([string]$Key) {
    $c = $Components[$Key]
    Clear-Host
    Write-Host "Logs: $($c.Name)   (ao vivo - Esc ou Q para voltar)" -ForegroundColor Cyan
    Write-Host ("-" * 60) -ForegroundColor DarkGray

    $files = @($c.OutLog, $c.ErrLog)
    if (-not ($files | Where-Object { Test-Path $_ })) {
        Write-Host "Ainda não há logs. Suba o $($c.Name) primeiro." -ForegroundColor Yellow
    }

    $positions = @{}
    foreach ($f in $files) {
        foreach ($line in (Read-LogTail $f 40)) { Write-LogLine $line ($f -eq $c.ErrLog) }
        $positions[$f] = if (Test-Path $f) { (Get-Item $f).Length } else { 0 }
    }

    while ($true) {
        try {
            while ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                if ($key.Key -eq 'Escape' -or $key.Key -eq 'Q') { return }
            }
        } catch [System.InvalidOperationException] {
            # Sem console interativo (entrada redirecionada): só acompanha, Ctrl+C encerra.
        }

        foreach ($f in $files) {
            if (-not (Test-Path $f)) { continue }
            $length = (Get-Item $f).Length
            # Arquivo recriado (o componente foi reiniciado): volta ao início.
            if ($length -lt $positions[$f]) { $positions[$f] = 0 }
            if ($length -gt $positions[$f]) {
                $stream = [System.IO.File]::Open($f, 'Open', 'Read', 'ReadWrite')
                try {
                    $null = $stream.Seek($positions[$f], 'Begin')
                    $reader = New-Object System.IO.StreamReader($stream, $LogEncoding)
                    $text = $reader.ReadToEnd()
                    $positions[$f] = $stream.Position
                } finally {
                    $stream.Dispose()
                }
                foreach ($line in ($text -split "`r?`n" | Where-Object { $_ -ne '' })) { Write-LogLine $line ($f -eq $c.ErrLog) }
            }
        }
        Start-Sleep -Milliseconds 300
    }
}

# ---------------------------------------------------------------------------
# Status e menu
# ---------------------------------------------------------------------------

function Write-StatusLine([string]$Key) {
    $c = $Components[$Key]
    $proc = Get-TrackedProcess $Key
    Write-Host ("  {0,-4}" -f $c.Name) -NoNewline -ForegroundColor White
    if ($proc) {
        Write-Host ([char]0x25CF + ' rodando') -NoNewline -ForegroundColor Green
        $extra = "  PID $($proc.Id)"
        if ($Key -eq 'api') { $extra += "  $ApiUrl" }
        Write-Host $extra -ForegroundColor DarkGray
    } else {
        Write-Host ([char]0x25CB + ' parado') -NoNewline -ForegroundColor DarkGray
        if ($Key -eq 'api') {
            $owner = Get-PortOwner $ApiPort
            if ($owner) { Write-Host "  (porta $ApiPort ocupada por '$($owner.ProcessName)', PID $($owner.Id))" -NoNewline -ForegroundColor Yellow }
        }
        Write-Host ''
    }
}

function Show-Status {
    foreach ($key in $Components.Keys) { Write-StatusLine $key }
}

$MenuItems = @(
    @{ Label = 'Subir tudo';                 Run = { Start-Component 'api' | Out-Null; Start-Component 'app' | Out-Null } }
    @{ Label = 'Parar tudo';                 Run = { Stop-Component 'app'; Stop-Component 'api'; Set-Message 'API e app parados.' 'Green' } }
    @{ Label = 'Subir API';                  Run = { Start-Component 'api' | Out-Null } }
    @{ Label = 'Parar API';                  Run = { Stop-Component 'api' } }
    @{ Label = 'Subir app';                  Run = { Start-Component 'app' | Out-Null } }
    @{ Label = 'Parar app';                  Run = { Stop-Component 'app' } }
    @{ Label = 'Recompilar tudo';            Run = { Invoke-Rebuild | Out-Null }; Pause = $true }
    @{ Label = 'Logs da API (ao vivo)';      Run = { Show-Logs 'api' } }
    @{ Label = 'Logs do app (ao vivo)';      Run = { Show-Logs 'app' } }
    @{ Label = 'Rodar testes';               Run = { Invoke-Tests | Out-Null }; Pause = $true }
    @{ Label = 'Sair';                       Run = $null }
)

function Show-Menu([int]$Selected) {
    Clear-Host
    Write-Host ''
    Write-Host '  Screen' -NoNewline -ForegroundColor White
    Write-Host 'Cord' -NoNewline -ForegroundColor Blue
    Write-Host '  painel de desenvolvimento' -ForegroundColor DarkGray
    Write-Host ('  ' + ([string][char]0x2500 * 44)) -ForegroundColor DarkGray
    Show-Status
    Write-Host ''

    for ($i = 0; $i -lt $MenuItems.Count; $i++) {
        $label = $MenuItems[$i].Label
        if ($i -eq $Selected) {
            Write-Host ("  " + [char]0x276F + " $label ") -ForegroundColor Black -BackgroundColor Cyan
        } else {
            Write-Host "    $label" -ForegroundColor Gray
        }
    }

    Write-Host ''
    Write-Host ("  " + [char]0x2191 + '/' + [char]0x2193 + ' navegar   Enter executar   Esc sair') -ForegroundColor DarkGray
    if ($script:Message) {
        Write-Host ''
        foreach ($line in ($script:Message -split "`n")) { Write-Host "  $line" -ForegroundColor $script:MessageColor }
    }
}

function Wait-AnyKey {
    Write-Host ''
    Write-Host 'Pressione qualquer tecla para voltar ao menu...' -ForegroundColor DarkGray
    $null = [Console]::ReadKey($true)
}

function Start-Menu {
    $selected = 0
    $previousCursor = $true
    try { $previousCursor = [Console]::CursorVisible } catch { }

    try {
        while ($true) {
            try { [Console]::CursorVisible = $false } catch { }
            Show-Menu $selected

            $key = [Console]::ReadKey($true)
            switch ($key.Key) {
                'UpArrow'   { $selected = ($selected - 1 + $MenuItems.Count) % $MenuItems.Count; continue }
                'DownArrow' { $selected = ($selected + 1) % $MenuItems.Count; continue }
                'Home'      { $selected = 0; continue }
                'End'       { $selected = $MenuItems.Count - 1; continue }
                'Escape'    { return }
                'Q'         { return }
                'Enter' {
                    $item = $MenuItems[$selected]
                    if ($null -eq $item.Run) { return }

                    try { [Console]::CursorVisible = $true } catch { }
                    Clear-Host
                    Write-Host "> $($item.Label)" -ForegroundColor Cyan
                    $script:Message = ''
                    try {
                        & $item.Run
                    } catch {
                        Set-Message "Erro: $($_.Exception.Message)" 'Red'
                    }
                    if ($item.ContainsKey('Pause') -and $item.Pause) { Wait-AnyKey }
                }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $previousCursor } catch { }
        Clear-Host
        Write-Host 'Painel fechado. O que estava rodando continua em segundo plano:' -ForegroundColor DarkGray
        Show-Status
    }
}

# ---------------------------------------------------------------------------
# Entrada
# ---------------------------------------------------------------------------

$previousEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    Initialize-StateDir

    $exitCode = 0
    switch ($Action) {
        'menu' {
            try {
                $null = [Console]::KeyAvailable
            } catch {
                Write-Host 'O menu interativo precisa de um console de verdade. Use uma ação direta, ex.: .\screencord.ps1 status' -ForegroundColor Yellow
                exit 1
            }
            Start-Menu
        }
        'status'    { Show-Status }
        'start-api' { if (-not (Start-Component 'api')) { $exitCode = 1 } }
        'stop-api'  { Stop-Component 'api' }
        'start-app' { if (-not (Start-Component 'app')) { $exitCode = 1 } }
        'stop-app'  { Stop-Component 'app' }
        'start-all' { if (-not ((Start-Component 'api') -and (Start-Component 'app'))) { $exitCode = 1 } }
        'stop-all'  { Stop-Component 'app'; Stop-Component 'api' }
        'build'     { if (-not (Invoke-Build)) { $exitCode = 1 } }
        'rebuild'   { if (-not (Invoke-Rebuild)) { $exitCode = 1 } }
        'test'      { if (-not (Invoke-Tests)) { $exitCode = 1 } }
        'logs-api'  { Show-Logs 'api' }
        'logs-app'  { Show-Logs 'app' }
    }
    exit $exitCode
} finally {
    [Console]::OutputEncoding = $previousEncoding
}
