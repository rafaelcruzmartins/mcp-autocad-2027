<#
.SYNOPSIS
  Teste ponta a ponta do AcadMcp contra um AutoCAD aberto com um desenho VAZIO.
  Exercita todas as ferramentas, valida erros/rollback e verifica que cada chamada vira um grupo de undo.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tests\e2e-test.ps1
  powershell -ExecutionPolicy Bypass -File .\tests\e2e-test.ps1 -SavePath C:\Temp\acadmcp-teste.dwg
#>
param(
    [int]$Port = 5100,
    [string]$SavePath = ''
)

$ErrorActionPreference = 'Stop'
$base = "http://127.0.0.1:$Port/mcp"
$script:rpcId = 0
$script:pass = 0
$script:fail = 0

function Invoke-Tool([string]$name, [hashtable]$arguments = @{}) {
    $script:rpcId++
    $body = @{ jsonrpc = '2.0'; id = $script:rpcId; method = 'tools/call'; params = @{ name = $name; arguments = $arguments } } |
        ConvertTo-Json -Depth 30 -Compress
    $resp = Invoke-WebRequest -Uri $base -Method Post -UseBasicParsing -TimeoutSec 180 `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json, text/event-stream' }
    $text = [Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray())
    $json = ($text -split "`n" | Where-Object { $_ -like 'data:*' } | ForEach-Object { $_.Substring(5).Trim() }) -join ''
    if (-not $json) { $json = $text }
    $rpc = $json | ConvertFrom-Json
    if ($rpc.error) { throw "JSON-RPC: $($rpc.error.message)" }
    $payload = $rpc.result.content[0].text | ConvertFrom-Json
    $payload | Add-Member -NotePropertyName isError -NotePropertyValue ([bool]$rpc.result.isError) -Force
    return $payload
}

function Step([string]$title, [scriptblock]$body) {
    try {
        $msg = & $body
        Write-Host ("  OK   {0}  {1}" -f $title, $msg) -ForegroundColor Green
        $script:pass++
    } catch {
        Write-Host ("  FAIL {0}  {1}" -f $title, $_.Exception.Message) -ForegroundColor Red
        $script:fail++
    }
}

function Assert($cond, [string]$message) { if (-not $cond) { throw $message } }
function Ok($r) { if ($r.isError -or -not $r.ok) { throw "erro: $($r.error.message)" }; $r.result }
function ModelCount() { (Ok (Invoke-Tool 'get_drawing_info')).entityCounts.modelSpaceTotal }

Write-Host "== AcadMcp e2e em $base" -ForegroundColor Cyan
$h = @{}

Step 'get_drawing_info' {
    $r = Ok (Invoke-Tool 'get_drawing_info')
    Assert ($r.entityCounts.modelSpaceTotal -eq 0) "o desenho não está vazio ($($r.entityCounts.modelSpaceTotal) entidades); abra um desenho novo"
    "unidades=$($r.units.insunits)"
}
Step 'create_layer' {
    $r = Ok (Invoke-Tool 'create_layer' @{ name = 'MCP-TESTE'; color = '1'; lineweight = '0.35'; description = 'teste e2e' })
    Assert ($r.color -eq '1') "cor=$($r.color)"; "created=$($r.created)"
}
Step 'create_line' {
    $r = Ok (Invoke-Tool 'create_line' @{ start = @{ x = 0; y = 0 }; end = @{ x = 100; y = 0 }; layer = 'MCP-TESTE' })
    $h.line = $r.handle; Assert ($r.geometry.length -eq 100) "length=$($r.geometry.length)"; "handle=$($r.handle)"
}
Step 'create_polyline' {
    $r = Ok (Invoke-Tool 'create_polyline' @{ vertices = @(@{ x = 0; y = 0 }, @{ x = 100; y = 0 }, @{ x = 100; y = 50; bulge = 1 }, @{ x = 0; y = 50 }); closed = $true; layer = 'MCP-TESTE' })
    $h.pline = $r.handle; "area=$($r.geometry.area)"
}
Step 'create_circle' {
    $r = Ok (Invoke-Tool 'create_circle' @{ center = @{ x = 50; y = 25 }; radius = 10; color = 'blue' })
    $h.circle = $r.handle; "r=$($r.geometry.radius)"
}
Step 'create_arc' {
    $r = Ok (Invoke-Tool 'create_arc' @{ center = @{ x = 200; y = 0 }; radius = 30; startAngle = 0; endAngle = 90 })
    Assert ([math]::Abs($r.geometry.totalAngleDeg - 90) -lt 1e-6) "total=$($r.geometry.totalAngleDeg)"; "len=$($r.geometry.length)"
}
Step 'create_text' {
    $r = Ok (Invoke-Tool 'create_text' @{ text = 'Olá MCP'; position = @{ x = 0; y = 70 }; height = 5; justify = 'MiddleCenter' })
    "justify=$($r.geometry.justify)"
}
Step 'create_mtext' {
    $r = Ok (Invoke-Tool 'create_mtext' @{ text = "Linha 1`nLinha 2"; location = @{ x = 0; y = 100 }; height = 3; width = 60 })
    "text=$($r.geometry.text -replace "`r?`n",' | ')"
}
Step 'create_dimension linear' {
    $r = Ok (Invoke-Tool 'create_dimension' @{ kind = 'linear'; point1 = @{ x = 0; y = 0 }; point2 = @{ x = 100; y = 0 }; dimLinePoint = @{ x = 50; y = -15 } })
    Assert ([math]::Abs($r.geometry.measurement - 100) -lt 1e-6) "medida=$($r.geometry.measurement)"; "medida=$($r.geometry.measurement)"
}
Step 'create_dimension radial' {
    $r = Ok (Invoke-Tool 'create_dimension' @{ kind = 'radial'; entityHandle = $h.circle; angle = 30 })
    "medida=$($r.geometry.measurement)"
}
Step 'create_table' {
    $r = Ok (Invoke-Tool 'create_table' @{ position = @{ x = 300; y = 100 }; data = @(@('Item', 'Qtd'), @('Luminária', '12'), @('Tomada', '30')); textHeight = 2.5 })
    Assert ($r.geometry.rows -eq 3 -and $r.geometry.columns -eq 2) "rows=$($r.geometry.rows) cols=$($r.geometry.columns)"; "3x2"
}
Step 'execute_csharp (cria bloco com atributo)' {
    $code = @'
var bt = (BlockTable)Tr.GetObject(Db.BlockTableId, OpenMode.ForWrite);
if (!bt.Has("MCP_BLOCO")) {
    var btr = new BlockTableRecord { Name = "MCP_BLOCO", Origin = Point3d.Origin };
    bt.Add(btr); Tr.AddNewlyCreatedDBObject(btr, true);
    var c = new Circle(Point3d.Origin, Vector3d.ZAxis, 5); btr.AppendEntity(c); Tr.AddNewlyCreatedDBObject(c, true);
    var ad = new AttributeDefinition(new Point3d(0, -8, 0), "000", "NUMERO", "Número", Db.Textstyle) { Height = 2.5 };
    btr.AppendEntity(ad); Tr.AddNewlyCreatedDBObject(ad, true);
}
Print("bloco ok");
return bt["MCP_BLOCO"];
'@
    $r = Ok (Invoke-Tool 'execute_csharp' @{ code = $code })
    Assert ($r.output -match 'bloco ok') "output=$($r.output)"; "return=$($r.returnValue.handle) cache=$($r.compiledFromCache)"
}
Step 'insert_block' {
    $r = Ok (Invoke-Tool 'insert_block' @{ name = 'MCP_BLOCO'; position = @{ x = 150; y = 50 }; attributes = @{ NUMERO = '101' } })
    $h.block = $r.handle; Assert ($r.geometry.attributes.NUMERO -eq '101') "attr=$($r.geometry.attributes.NUMERO)"; "NUMERO=101"
}
Step 'set_block_attributes' {
    $r = Ok (Invoke-Tool 'set_block_attributes' @{ handles = @($h.block); attributes = @{ NUMERO = '202' } })
    "changed=$($r.results[0].changed.Count)"
}
Step 'get_block_attributes' {
    $r = Ok (Invoke-Tool 'get_block_attributes' @{ blockName = 'MCP_BLOCO' })
    Assert ($r.references[0].attributes.NUMERO -eq '202') "attr=$($r.references[0].attributes.NUMERO)"; "refs=$($r.totalReferences)"
}
Step 'query_entities (layer)' {
    $r = Ok (Invoke-Tool 'query_entities' @{ layers = @('MCP-TESTE') })
    Assert ($r.totalMatched -eq 2) "matched=$($r.totalMatched)"; "matched=2"
}
Step 'query_entities (tipo + região window)' {
    $r = Ok (Invoke-Tool 'query_entities' @{ types = @('circle'); region = @{ min = @{ x = 0; y = 0 }; max = @{ x = 100; y = 100 } }; regionMode = 'window' })
    Assert ($r.totalMatched -eq 1) "matched=$($r.totalMatched)"; "matched=1"
}
Step 'get_entity_properties' {
    $r = Ok (Invoke-Tool 'get_entity_properties' @{ handle = $h.block })
    Assert ($r.attributes.Count -eq 1) "attrs=$($r.attributes.Count)"; "type=$($r.type) space=$($r.space)"
}
Step 'set_entity_properties' {
    $r = Ok (Invoke-Tool 'set_entity_properties' @{ handles = @($h.line); color = '#FF8800'; lineweight = '0.5'; transparency = '30' })
    "ok"
}
Step 'move_entities' {
    Ok (Invoke-Tool 'move_entities' @{ handles = @($h.circle); displacement = @{ x = 10; y = 0 } }) | Out-Null
    $c = Ok (Invoke-Tool 'get_entity_properties' @{ handle = $h.circle })
    Assert ($c.geometry.center.x -eq 60) "centro.x=$($c.geometry.center.x)"; "centro.x=60"
}
Step 'copy_entities (count=2)' {
    $r = Ok (Invoke-Tool 'copy_entities' @{ handles = @($h.circle); displacement = @{ x = 0; y = 30 }; count = 2 })
    $h.copies = @($r.newHandles | ForEach-Object { $_ }); "novos=$($h.copies -join ',')"
}
Step 'rotate_entities' { Ok (Invoke-Tool 'rotate_entities' @{ handles = @($h.line); basePoint = @{ x = 0; y = 0 }; angle = 90 }) | Out-Null; 'ok' }
Step 'scale_entities' { Ok (Invoke-Tool 'scale_entities' @{ handles = @($h.pline); basePoint = @{ x = 0; y = 0 }; factor = 0.5 }) | Out-Null; 'ok' }
Step 'mirror_entities' {
    $r = Ok (Invoke-Tool 'mirror_entities' @{ handles = @($h.block); point1 = @{ x = 0; y = 0 }; point2 = @{ x = 0; y = 10 } })
    "cópia=$($r.handles -join ',')"
}
Step 'erase_entities' { $r = Ok (Invoke-Tool 'erase_entities' @{ handles = @($h.copies[-1]) }); "apagadas=$($r.count)" }
Step 'list_layers/blocks/layouts/text/dim' {
    $a = (Ok (Invoke-Tool 'list_layers')).count
    $b = (Ok (Invoke-Tool 'list_blocks')).count
    $c = (Ok (Invoke-Tool 'list_layouts')).count
    $d = (Ok (Invoke-Tool 'list_text_styles')).count
    $e = (Ok (Invoke-Tool 'list_dim_styles')).count
    "layers=$a blocos=$b layouts=$c textstyles=$d dimstyles=$e"
}
Step 'get_selection' { $r = Ok (Invoke-Tool 'get_selection'); "count=$($r.count)" }

Step 'execute_csharp erro de compilação' {
    $r = Invoke-Tool 'execute_csharp' @{ code = 'int x = "texto";' }
    Assert $r.isError 'deveria falhar'; "$($r.error.details.compilationErrors[0].id) linha $($r.error.details.compilationErrors[0].line)"
}
Step 'execute_csharp exceção => rollback' {
    $before = ModelCount
    $r = Invoke-Tool 'execute_csharp' @{ code = "Add(new Circle(Point3d.Origin, Vector3d.ZAxis, 1));`nPrint(""antes do erro"");`nthrow new InvalidOperationException(""boom"");" }
    Assert $r.isError 'deveria falhar'
    Assert ($r.error.details.output -match 'antes do erro') 'saída parcial ausente'
    Assert ((ModelCount) -eq $before) 'rollback não aconteceu'
    "rollback ok, linha=$($r.error.details.scriptLine)"
}
Step 'execute_csharp commit=false' {
    $before = ModelCount
    $r = Ok (Invoke-Tool 'execute_csharp' @{ code = 'return Add(new Circle(Point3d.Origin, Vector3d.ZAxis, 1));'; commit = $false })
    Assert ((ModelCount) -eq $before) 'não deveria persistir'; 'nada persistido'
}
Step 'execute_csharp cache' {
    $null = Invoke-Tool 'execute_csharp' @{ code = 'return Db.Insunits;'; commit = $false }
    $r = Ok (Invoke-Tool 'execute_csharp' @{ code = 'return Db.Insunits;'; commit = $false })
    Assert $r.compiledFromCache 'deveria vir do cache'; "return=$($r.returnValue)"
}
Step 'undo = 1 grupo por chamada' {
    $before = ModelCount
    Ok (Invoke-Tool 'execute_csharp' @{ code = 'for (var i = 0; i < 3; i++) Add(new Circle(new Point3d(400 + i * 10, 0, 0), Vector3d.ZAxis, 3)); return 3;' }) | Out-Null
    Assert ((ModelCount) -eq $before + 3) 'círculos não criados'
    Ok (Invoke-Tool 'run_command' @{ command = '_.U' }) | Out-Null
    $after = ModelCount
    Assert ($after -eq $before) "após U: $after (esperado $before)"
    'U desfez os 3 círculos de uma vez'
}
Step 'run_command (_.LINE)' {
    $r = Ok (Invoke-Tool 'run_command' @{ command = '_.LINE'; args = @('500,0', '600,0', '') })
    Assert ($r.createdCount -eq 1) "criadas=$($r.createdCount)"; "handle=$($r.created[0].handle)"
}
Step 'zoom_extents' { Ok (Invoke-Tool 'zoom_extents') | Out-Null; 'ok' }
Step 'erro claro (handle inválido)' {
    $r = Invoke-Tool 'get_entity_properties' @{ handle = 'FFFFFFF' }
    Assert $r.isError 'deveria falhar'; $r.error.message
}
if ($SavePath) {
    Step 'save_drawing' { $r = Ok (Invoke-Tool 'save_drawing' @{ path = $SavePath; overwrite = $true }); "path=$($r.path)" }
}

Write-Host ("`n== {0} OK, {1} FAIL" -f $script:pass, $script:fail) -ForegroundColor ($(if ($script:fail) { 'Red' } else { 'Green' }))
exit $script:fail
