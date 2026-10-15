<#
.SYNOPSIS
    Painel de desenvolvimento do ScreenCord: sobe, para, recompila e mostra os logs da API e do app.

.DESCRIPTION
    Sem parâmetros abre um menu interativo (setas para navegar, Enter para executar, Esc para sair).
    Com -Action executa uma ação direta e termina, útil em scripts.

    O app pode usar a API local (http://localhost:5080) ou a API de produção oficial (Api:BaseUrl do
    appsettings.json). A escolha fica salva em .screencord\ e vale a partir da próxima vez que o app subir;
    no menu, troque pela opção "API do app"; na linha de comando, use -Api local|prod.

    Os processos rodam em segundo plano e continuam vivos depois que o menu fecha.
    PIDs e logs ficam em .screencord\ (ignorada pelo Git).

.EXAMPLE
    .\screencord.ps1

.EXAMPLE
    .\screencord.ps1 start-all

.EXAMPLE
    .\screencord.ps1 start-app -Api prod
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('menu', 'status', 'start-api', 'stop-api', 'start-app', 'stop-app', 'restart-app', 'start-all', 'stop-all',
                 'build', 'rebuild', 'test', 'logs-api', 'logs-app')]
    [string]$Action = 'menu',

    # API usada pelo app: 'local' (a do screencord.api rodando aqui) ou 'prod' (a oficial). Fica salva para as próximas vezes.
    [ValidateSet('local', 'prod')]
    [string]$Api
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$StateDir = Join-Path $Root '.screencord'
$LogDir = Join-Path $StateDir 'logs'
# A API fica no repositório screencord.api, clonado ao lado deste (ou em $env:SCREENCORD_API_ROOT).
$ApiRoot = if ($env:SCREENCORD_API_ROOT) { $env:SCREENCORD_API_ROOT } else { Join-Path (Split-Path $Root -Parent) 'screencord.api' }
$Solutions = @(Join-Path $Root 'ScreenCord.sln')
if (Test-Path (Join-Path $ApiRoot 'ScreenCord.Api.sln')) { $Solutions += Join-Path $ApiRoot 'ScreenCord.Api.sln' }
$ApiPort = 5080
$ApiUrl = "http://localhost:$ApiPort"

# A URL de produção vem do appsettings.json do app, a mesma que vai para a publicação.
$ProdApiUrl = (Get-Content (Join-Path $Root 'src\ScreenCord\appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Api.BaseUrl

# Cada destino define o ambiente do app e a URL passada por linha de comando (que prevalece sobre os appsettings,
# inclusive o appsettings.Local.json).
$ApiTargets = [ordered]@{
    local = @{ Key = 'local'; Name = 'local';    Phrase = 'a API local';       Url = $ApiUrl;     Environment = 'Development' }
    prod  = @{ Key = 'prod';  Name = 'produção'; Phrase = 'a API de produção'; Url = $ProdApiUrl; Environment = 'Production' }
}
$ApiTargetFile = Join-Path $StateDir 'api-target'
# Destino com que o app em execução foi aberto (a escolha salva pode ter mudado depois).
$RunningAppTargetFile = Join-Path $StateDir 'app.api-target'

# Encoding dos logs gerados pelos processos .NET redirecionados para arquivo.
$LogEncoding = New-Object System.Text.UTF8Encoding($false)

$Components = [ordered]@{
    api = @{
        Name     = 'API'
        WorkDir  = Join-Path $ApiRoot 'src\ScreenCord.Api'
        Output   = Join-Path $ApiRoot 'src\ScreenCord.Api\bin\Debug\net10.0\ScreenCord.Api.dll'
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
# API usada pelo app
# ---------------------------------------------------------------------------

function Read-TargetFile([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    $key = (Get-Content $Path -Raw).Trim()
    if ($ApiTargets.Contains($key)) { return $ApiTargets[$key] }
    return $null
}

# Padrão: API local, como antes da opção existir.
function Get-ApiTarget {
    $target = Read-TargetFile $ApiTargetFile
    if ($target) { return $target }
    return $ApiTargets['local']
}

function Set-ApiTarget([string]$Key) {
    Initialize-StateDir
    Set-Content -Path $ApiTargetFile -Value $Key -Encoding ASCII
    $target = $ApiTargets[$Key]
    $text = "O app vai usar $($target.Phrase) ($($target.Url))."
    if (Get-TrackedProcess 'app') { $text += " Reinicie o app para aplicar." }
    Set-Message $text 'Green'
}

function Switch-ApiTarget {
    if ((Get-ApiTarget).Key -eq 'local') { Set-ApiTarget 'prod' } else { Set-ApiTarget 'local' }
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
    # Checagem rápida antes: Get-NetTCPConnection é lento e o status do menu é atualizado periodicamente.
    $listening = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object { $_.Port -eq $Port }
    if (-not $listening) { return $null }
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
        $target = Get-ApiTarget
        $previousEnv = $env:DOTNET_ENVIRONMENT
        $env:DOTNET_ENVIRONMENT = $target.Environment
        try {
            $proc = Start-Process -FilePath $c.Output `
                -ArgumentList @("--Api:BaseUrl=$($target.Url)") `
                -WorkingDirectory $c.WorkDir `
                -RedirectStandardOutput $c.OutLog -RedirectStandardError $c.ErrLog `
                -PassThru
        } finally {
            $env:DOTNET_ENVIRONMENT = $previousEnv
        }
        Set-Content -Path $RunningAppTargetFile -Value $target.Key -Encoding ASCII
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
    if ($target.Key -eq 'local' -and -not (Test-ApiHealthy)) {
        Set-Message "App aberto (PID $($proc.Id)) com a API local, mas ela não está respondendo em $ApiUrl. Suba a API." 'Yellow'
    } else {
        Set-Message "App aberto (PID $($proc.Id)) com $($target.Phrase) ($($target.Url))." 'Green'
    }
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

function Restart-App {
    if (Get-TrackedProcess 'app') { Stop-Component 'app' }
    return Start-Component 'app'
}

# A API local só é necessária quando o app vai usá-la.
function Start-All {
    if ((Get-ApiTarget).Key -eq 'local') {
        if (-not (Start-Component 'api')) { return $false }
    }
    return Start-Component 'app'
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
    $ok = $true
    foreach ($solution in $Solutions) {
        Write-Host "Compilando $(Split-Path $solution -Leaf)..." -ForegroundColor Cyan
        if (-not (Invoke-Dotnet @('build', $solution, '--nologo', '-v', 'q'))) { $ok = $false }
    }
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
    $ok = $true
    foreach ($solution in $Solutions) {
        Write-Host "Rodando os testes de $(Split-Path $solution -Leaf)..." -ForegroundColor Cyan
        if (-not (Invoke-Dotnet @('test', $solution, '--nologo', '-v', 'q'))) { $ok = $false }
    }
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
# Status
# ---------------------------------------------------------------------------

# Uma linha da tela é uma lista de trechos coloridos.
function New-Segment([string]$Text, [string]$Foreground = 'Gray', [string]$Background = '') {
    return @{ Text = $Text; Foreground = $Foreground; Background = $Background }
}

function Get-StatusSegments([string]$Key) {
    $c = $Components[$Key]
    $proc = Get-TrackedProcess $Key
    $segments = @(New-Segment ("  {0,-5}" -f $c.Name) 'White')
    $running = $null

    if ($proc) {
        $segments += New-Segment ([string][char]0x25CF + ' rodando') 'Green'
        $extra = "  PID $($proc.Id)"
        if ($Key -eq 'api') {
            $extra += "  $ApiUrl"
        } else {
            $running = Read-TargetFile $RunningAppTargetFile
            if ($running) { $extra += "  usando $($running.Phrase) ($($running.Url))" }
        }
        $segments += New-Segment $extra 'DarkGray'
        if ($Key -eq 'app' -and $running -and $running.Key -ne (Get-ApiTarget).Key) {
            $segments += New-Segment '  reinicie para trocar de API' 'Yellow'
        }
    } else {
        $segments += New-Segment ([string][char]0x25CB + ' parado') 'DarkGray'
        if ($Key -eq 'api') {
            $owner = Get-PortOwner $ApiPort
            if ($owner) { $segments += New-Segment "  (porta $ApiPort ocupada por '$($owner.ProcessName)', PID $($owner.Id))" 'Yellow' }
        } else {
            $target = Get-ApiTarget
            $segments += New-Segment "  vai usar $($target.Phrase) ($($target.Url))" 'DarkGray'
        }
    }
    return ,$segments
}

function Write-Segments($Segments) {
    foreach ($s in $Segments) {
        if ($s.Background) { Write-Host $s.Text -NoNewline -ForegroundColor $s.Foreground -BackgroundColor $s.Background }
        else { Write-Host $s.Text -NoNewline -ForegroundColor $s.Foreground }
    }
    Write-Host ''
}

function Show-Status {
    foreach ($key in $Components.Keys) { Write-Segments (Get-StatusSegments $key) }
}

# ---------------------------------------------------------------------------
# Menu
# ---------------------------------------------------------------------------

# Inline: a ação roda sem sair do menu (sem limpar a tela). Pause: espera uma tecla antes de voltar ao menu.
$MenuItems = @(
    @{ Label = 'Subir tudo';                 Run = { Start-All | Out-Null } }
    @{ Label = 'Parar tudo';                 Run = { Stop-Component 'app'; Stop-Component 'api'; Set-Message 'API e app parados.' 'Green' } }
    @{ Label = 'Subir API';                  Run = { Start-Component 'api' | Out-Null } }
    @{ Label = 'Parar API';                  Run = { Stop-Component 'api' } }
    @{ Label = 'Subir app';                  Run = { Start-Component 'app' | Out-Null } }
    @{ Label = 'Parar app';                  Run = { Stop-Component 'app' } }
    @{ Label = 'Reiniciar app';              Run = { Restart-App | Out-Null } }
    @{ Label = { "API do app: $((Get-ApiTarget).Name)  (Enter alterna)" }; Run = { Switch-ApiTarget }; Inline = $true }
    @{ Label = 'Recompilar tudo';            Run = { Invoke-Rebuild | Out-Null }; Pause = $true }
    @{ Label = 'Logs da API (ao vivo)';      Run = { Show-Logs 'api' } }
    @{ Label = 'Logs do app (ao vivo)';      Run = { Show-Logs 'app' } }
    @{ Label = 'Rodar testes';               Run = { Invoke-Tests | Out-Null }; Pause = $true }
    @{ Label = 'Sair';                       Run = $null }
)

# O menu é desenhado por cima do que já está na tela (sem Clear-Host) e, ao navegar, só as duas linhas
# de item afetadas são reescritas: limpar e redesenhar tudo a cada tecla é o que fazia a tela piscar.
$script:StatusLines = @()
$script:StatusTop = 0
$script:ItemTop = 0
$script:DrawnLines = 0
$script:DrawnWidth = 0
$script:NeedsClear = $true

function Test-ItemFlag($Item, [string]$Name) {
    return $Item.ContainsKey($Name) -and $Item[$Name]
}

function Get-ItemLabel([int]$Index) {
    $label = $MenuItems[$Index].Label
    if ($label -is [scriptblock]) { return [string](& $label) }
    return $label
}

function Get-ItemSegments([int]$Index, [bool]$IsSelected) {
    $label = Get-ItemLabel $Index
    if ($IsSelected) { return ,@(New-Segment ('  ' + [char]0x276F + " $label ") 'Black' 'Cyan') }
    return ,@(New-Segment "    $label" 'Gray')
}

function Update-StatusLines {
    $lines = New-Object System.Collections.ArrayList
    foreach ($key in $Components.Keys) { [void]$lines.Add((Get-StatusSegments $key)) }
    $script:StatusLines = $lines
}

function Get-StatusSignature {
    return (@($script:StatusLines | ForEach-Object { (@($_) | ForEach-Object { $_.Text }) -join '' }) -join "`n")
}

# Escreve uma linha inteira na posição dada, completando com espaços para apagar o que havia antes.
function Write-FrameLine([int]$Row, $Segments) {
    if ($Row -ge [Console]::BufferHeight) { return }
    # Uma coluna a menos: escrever na última coluna faz alguns consoles quebrarem a linha.
    $width = [Math]::Max(1, [Console]::WindowWidth - 1)
    [Console]::SetCursorPosition(0, $Row)
    $used = 0
    foreach ($s in $Segments) {
        $text = $s.Text
        if ($used + $text.Length -gt $width) { $text = $text.Substring(0, [Math]::Max(0, $width - $used)) }
        if ($text.Length -eq 0) { continue }
        if ($s.Background) { Write-Host $text -NoNewline -ForegroundColor $s.Foreground -BackgroundColor $s.Background }
        else { Write-Host $text -NoNewline -ForegroundColor $s.Foreground }
        $used += $text.Length
    }
    if ($used -lt $width) { Write-Host (' ' * ($width - $used)) -NoNewline }
}

function Show-Menu([int]$Selected) {
    if ($script:NeedsClear) {
        Clear-Host
        $script:NeedsClear = $false
        $script:DrawnLines = 0
    }

    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add(@())
    [void]$lines.Add(@((New-Segment '  Screen' 'White'), (New-Segment 'Cord' 'Blue'), (New-Segment '  painel de desenvolvimento' 'DarkGray')))
    [void]$lines.Add(@(New-Segment ('  ' + ([string][char]0x2500 * 44)) 'DarkGray'))
    $script:StatusTop = $lines.Count
    foreach ($status in $script:StatusLines) { [void]$lines.Add($status) }
    [void]$lines.Add(@())

    $script:ItemTop = $lines.Count
    for ($i = 0; $i -lt $MenuItems.Count; $i++) { [void]$lines.Add((Get-ItemSegments $i ($i -eq $Selected))) }

    [void]$lines.Add(@())
    [void]$lines.Add(@(New-Segment ('  ' + [char]0x2191 + '/' + [char]0x2193 + ' navegar   Enter executar   Esc sair') 'DarkGray'))
    if ($script:Message) {
        [void]$lines.Add(@())
        foreach ($line in ($script:Message -split "`n")) { [void]$lines.Add(@(New-Segment "  $line" $script:MessageColor)) }
    }

    $total = [Math]::Max($lines.Count, $script:DrawnLines)
    for ($row = 0; $row -lt $total; $row++) {
        $segments = if ($row -lt $lines.Count) { $lines[$row] } else { @() }
        Write-FrameLine $row $segments
    }
    $script:DrawnLines = $lines.Count
    $script:DrawnWidth = [Console]::WindowWidth
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
        try { [Console]::CursorVisible = $false } catch { }
        Update-StatusLines
        Show-Menu $selected

        while ($true) {
            # Enquanto espera uma tecla, acompanha o status (ex.: o app fechado pela própria janela) e o tamanho do console.
            $nextRefresh = (Get-Date).AddSeconds(2)
            while (-not [Console]::KeyAvailable) {
                Start-Sleep -Milliseconds 50
                if ([Console]::WindowWidth -ne $script:DrawnWidth) {
                    $script:NeedsClear = $true
                    Show-Menu $selected
                }
                if ((Get-Date) -ge $nextRefresh) {
                    $before = Get-StatusSignature
                    Update-StatusLines
                    if ((Get-StatusSignature) -ne $before) {
                        for ($i = 0; $i -lt $script:StatusLines.Count; $i++) { Write-FrameLine ($script:StatusTop + $i) $script:StatusLines[$i] }
                    }
                    $nextRefresh = (Get-Date).AddSeconds(2)
                }
            }

            $key = [Console]::ReadKey($true)
            $previous = $selected
            switch ($key.Key) {
                'UpArrow'   { $selected = ($selected - 1 + $MenuItems.Count) % $MenuItems.Count }
                'DownArrow' { $selected = ($selected + 1) % $MenuItems.Count }
                'Home'      { $selected = 0 }
                'End'       { $selected = $MenuItems.Count - 1 }
                'Escape'    { return }
                'Q'         { return }
                'Enter' {
                    $item = $MenuItems[$selected]
                    if ($null -eq $item.Run) { return }

                    $script:Message = ''
                    if (Test-ItemFlag $item 'Inline') {
                        try { & $item.Run } catch { Set-Message "Erro: $($_.Exception.Message)" 'Red' }
                    } else {
                        try { [Console]::CursorVisible = $true } catch { }
                        Clear-Host
                        Write-Host "> $(Get-ItemLabel $selected)" -ForegroundColor Cyan
                        try { & $item.Run } catch { Set-Message "Erro: $($_.Exception.Message)" 'Red' }
                        if (Test-ItemFlag $item 'Pause') { Wait-AnyKey }
                        try { [Console]::CursorVisible = $false } catch { }
                        $script:NeedsClear = $true
                    }
                    Update-StatusLines
                    Show-Menu $selected
                }
            }

            if ($selected -ne $previous) {
                Write-FrameLine ($script:ItemTop + $previous) (Get-ItemSegments $previous $false)
                Write-FrameLine ($script:ItemTop + $selected) (Get-ItemSegments $selected $true)
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
    if ($Api) { Set-ApiTarget $Api }

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
        'status'      { Show-Status }
        'start-api'   { if (-not (Start-Component 'api')) { $exitCode = 1 } }
        'stop-api'    { Stop-Component 'api' }
        'start-app'   { if (-not (Start-Component 'app')) { $exitCode = 1 } }
        'stop-app'    { Stop-Component 'app' }
        'restart-app' { if (-not (Restart-App)) { $exitCode = 1 } }
        'start-all'   { if (-not (Start-All)) { $exitCode = 1 } }
        'stop-all'    { Stop-Component 'app'; Stop-Component 'api' }
        'build'       { if (-not (Invoke-Build)) { $exitCode = 1 } }
        'rebuild'     { if (-not (Invoke-Rebuild)) { $exitCode = 1 } }
        'test'        { if (-not (Invoke-Tests)) { $exitCode = 1 } }
        'logs-api'    { Show-Logs 'api' }
        'logs-app'    { Show-Logs 'app' }
    }
    exit $exitCode
} finally {
    [Console]::OutputEncoding = $previousEncoding
}
