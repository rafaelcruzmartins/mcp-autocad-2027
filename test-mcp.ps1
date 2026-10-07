<#
.SYNOPSIS
  Teste rápido do servidor AcadMcp: /health, initialize, tools/list e uma chamada de ferramenta.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\test-mcp.ps1
  powershell -ExecutionPolicy Bypass -File .\test-mcp.ps1 -Tool get_drawing_info -Arguments '{}'
#>
param(
    [int]$Port = 5100,
    [string]$Tool = 'get_drawing_info',
    [string]$Arguments = '{}'
)

$ErrorActionPreference = 'Stop'
$base = "http://127.0.0.1:$Port"
$script:id = 0

function Invoke-Mcp([string]$method, $params) {
    $script:id++
    $body = @{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 20 -Compress
    $resp = Invoke-WebRequest -Uri "$base/mcp" -Method Post -UseBasicParsing -Body ([Text.Encoding]::UTF8.GetBytes($body)) `
        -ContentType 'application/json' -Headers @{ Accept = 'application/json, text/event-stream' }
    $text = [Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray())
    # Streamable HTTP may answer as SSE ("data: {...}") or plain JSON.
    $json = ($text -split "`n" | Where-Object { $_ -like 'data:*' } | ForEach-Object { $_.Substring(5).Trim() }) -join ''
    if (-not $json) { $json = $text }
    return $json | ConvertFrom-Json
}

Write-Host "== GET /health" -ForegroundColor Cyan
(Invoke-WebRequest "$base/health" -UseBasicParsing).Content

Write-Host "`n== initialize" -ForegroundColor Cyan
$init = Invoke-Mcp 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'test-mcp.ps1'; version = '1.0' } }
"{0} {1} (protocolo {2})" -f $init.result.serverInfo.name, $init.result.serverInfo.version, $init.result.protocolVersion

Write-Host "`n== tools/list" -ForegroundColor Cyan
(Invoke-Mcp 'tools/list' @{}).result.tools | ForEach-Object { " - $($_.name)" }

Write-Host "`n== tools/call $Tool" -ForegroundColor Cyan
$call = Invoke-Mcp 'tools/call' @{ name = $Tool; arguments = ($Arguments | ConvertFrom-Json) }
if ($call.error) { "Erro JSON-RPC: $($call.error.message)"; exit 1 }
if ($call.result.isError) { Write-Host '(isError = true)' -ForegroundColor Yellow }
$call.result.content | ForEach-Object { $_.text | ConvertFrom-Json | ConvertTo-Json -Depth 20 }
