# Marca uma release como depreciada ou com defeito (ou tira a marca), nos dois repos.
#   .\release-status.ps1 v0.1.0 deprecated "Não tem a atualização automática e o servidor não aceita mais esta versão."
#   .\release-status.ps1 v0.7.0 broken "O compartilhamento fecha sozinho depois de alguns segundos. Use a v0.7.1."
#   .\release-status.ps1 v0.7.0 ok
# A marca vai no começo das notas (um comentário que a landing page lê e um aviso visível no GitHub)
# e no título da release. A release e o MSI continuam lá: a página só os tira da lista principal.
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidateSet('deprecated', 'broken', 'ok')][string]$Status,
    [string]$Reason
)

$ErrorActionPreference = 'Stop'
# Sem isso o PowerShell 5.1 lê a saída do gh na página de código do console e estraga os acentos das notas.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repos = 'miguelsoeth/screencord.app', 'miguelsoeth/screencord'

if ($Tag -notmatch '^v\d+\.\d+\.\d+$') { throw "Tag inválida: $Tag (use vMAJOR.MINOR.PATCH)" }
if ($Status -ne 'ok' -and -not $Reason) { throw 'Diga o motivo: ele aparece na página e no GitHub.' }
if ($Reason -match '-->|[\r\n]') { throw 'O motivo é uma linha só e não pode ter "-->".' }

$block = switch ($Status) {
    'deprecated' {
        "<!-- screencord:status deprecated`n$Reason`n-->`n> [!WARNING]`n> **Versão depreciada.** $Reason Baixe a versão mais recente.`n<!-- /screencord:status -->`n`n"
    }
    'broken' {
        "<!-- screencord:status broken`n$Reason`n-->`n> [!CAUTION]`n> **Versão com defeito, não instale.** $Reason`n<!-- /screencord:status -->`n`n"
    }
    'ok' { '' }
}
$title = switch ($Status) {
    'deprecated' { "$Tag (depreciada)" }
    'broken' { "$Tag (com defeito, não instale)" }
    'ok' { $Tag }
}

foreach ($repo in $repos) {
    $body = gh release view $Tag --repo $repo --json body --jq .body
    if ($LASTEXITCODE) { Write-Warning "Release $Tag não encontrada em $repo"; continue }
    $body = ($body -join "`n") -replace '(?s)<!-- screencord:status .*?<!-- /screencord:status -->\s*', ''

    $notes = Join-Path ([IO.Path]::GetTempPath()) "screencord-notes-$Tag.md"
    [IO.File]::WriteAllText($notes, $block + $body, [Text.UTF8Encoding]::new($false))
    gh release edit $Tag --repo $repo --title $title --notes-file $notes | Out-Null
    if ($LASTEXITCODE) { throw "Falha ao editar $Tag em $repo" }
    Remove-Item $notes
    Write-Host "$repo ${Tag}: $title"
}
