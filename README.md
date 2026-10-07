# AcadMcp: servidor MCP para AutoCAD 2027

Plugin .NET 10 que roda dentro do `acad.exe` e expõe um servidor **MCP (Model Context Protocol)** em
`http://127.0.0.1:5100/mcp`. Com ele, o Claude controla o AutoCAD de duas formas:

- **ferramentas tipadas**, para consultar, criar e modificar o desenho;
- **`execute_csharp`**, que compila e executa C# arbitrário na thread do AutoCAD, com acesso total à API .NET.

## Requisitos

- Windows 64 bits com **AutoCAD 2027** completo (não LT), versão R26.0.
- **.NET 10 SDK**, só para compilar (`dotnet --list-sdks`).
- AutoCAD em `C:\Program Files\Autodesk\AutoCAD 2027`. Se estiver em outro lugar, use `dotnet build -p:AcadDir="D:\...\AutoCAD 2027"`.

## Instalação

1. Feche o AutoCAD. A DLL fica travada enquanto ele está aberto.
2. Rode:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build-and-deploy.ps1
   ```
   O script compila em Release e instala o bundle em
   `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcp.bundle`. O `PackageContents.xml` aceita só R26.0 e carrega o plugin na inicialização.
3. Abra o AutoCAD. Se aparecer o aviso de **executável não assinado**, escolha **Sempre carregar**.
4. A linha de comando deve mostrar `AcadMcp: servidor MCP em http://127.0.0.1:5100/mcp`.

Para desinstalar, apague a pasta `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcp.bundle`.

### Comandos no AutoCAD

| Comando | O que faz |
|---|---|
| `MCPSTART` | Inicia o servidor. Ele já inicia sozinho ao carregar, se `AutoStart` estiver ligado. |
| `MCPSTOP` | Para o servidor. |
| `MCPSTATUS` | Mostra URL, número de chamadas e erros, e o caminho da configuração e do log. |

### Configuração

O arquivo `%APPDATA%\AcadMcp\config.json` é criado na primeira execução:

```json
{ "Port": 5100, "AutoStart": true, "CallTimeoutSeconds": 120 }
```

- A variável de ambiente `ACADMCP_PORT` sobrepõe a porta.
- Evite a faixa 5001-5050, usada pelo MCP oficial da Autodesk.
- Depois de mudar a porta, rode `MCPSTOP` e `MCPSTART`.

## Conectar o Claude

### Claude Code

```bash
claude mcp add --transport http autocad http://127.0.0.1:5100/mcp
```

Confira com `claude mcp list`. Dentro de uma sessão, `/mcp` mostra o status e as ferramentas.

### Claude Desktop

A configuração local do Claude Desktop (`claude_desktop_config.json`) só aceita servidores stdio. Por isso a conexão passa por
uma ponte stdio ↔ HTTP. Há duas opções.

**Opção A, com `uv` (Python), sem Node.js.** Usa o [`mcp-proxy`](https://pypi.org/project/mcp-proxy/). O `--with "mcp<2"` é
necessário porque a versão mais recente do mcp-proxy quebra com a biblioteca `mcp` 2.x.

```json
{
  "mcpServers": {
    "autocad": {
      "command": "C:\\Users\\SEU_USUARIO\\.local\\bin\\uvx.exe",
      "args": ["--with", "mcp<2", "mcp-proxy", "--transport", "streamablehttp", "http://127.0.0.1:5100/mcp"]
    }
  }
}
```

**Opção B, com Node.js.** Usa o [`mcp-remote`](https://www.npmjs.com/package/mcp-remote):

```json
{
  "mcpServers": {
    "autocad": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:5100/mcp"]
    }
  }
}
```

**Onde fica o arquivo:**
- Instalação normal: `%APPDATA%\Claude\claude_desktop_config.json`.
- Instalação pela Microsoft Store: `%LOCALAPPDATA%\Packages\Claude_<id>\LocalCache\Roaming\Claude\claude_desktop_config.json`.
- O caminho mais fácil é abrir pelo próprio Claude Desktop: **Configurações → Desenvolvedor → Editar configuração**.

Se já houver outros servidores em `mcpServers`, acrescente `"autocad"` ao lado deles.

**Depois de editar:**
1. Abra o AutoCAD primeiro.
2. **Feche o Claude Desktop por completo**, pelo ícone na bandeja do Windows → Sair. Fechar só a janela não basta.
3. Abra o Claude Desktop de novo.

Se o Claude Desktop iniciar com o AutoCAD fechado, o servidor `autocad` aparece como desconectado. Nesse caso, abra o AutoCAD e reinicie o Claude Desktop.

Os "conectores personalizados" da interface do Claude não servem neste caso: eles acessam o servidor a partir da nuvem e não alcançam o `127.0.0.1` da sua máquina.

## Ferramentas

Todas devolvem JSON estruturado:

- **Sucesso:** `{ ok: true, tool, durationMs, units, result }`.
- **Erro:** `isError: true` com `{ ok: false, error: { type, message, errorStatus, details, stackTrace } }`.

Coordenadas são sempre em **WCS e em unidades do desenho**. O campo `units.drawing` informa as unidades (ex.: `Millimeters`). Ângulos de entrada e saída são em **graus**.

**Leitura**

| Ferramenta | Descrição |
|---|---|
| `get_drawing_info` | Arquivo, unidades, limites, extents, layer e layout correntes, contagem de entidades por tipo. |
| `list_layers`, `list_blocks`, `list_layouts`, `list_text_styles`, `list_dim_styles` | Tabelas de símbolos e dicionários do desenho. |
| `query_entities` | Filtros por tipo, layer (com curingas), nome de bloco e região (`crossing` ou `window`), no model space, paper space ou layout. Paginada. |
| `get_entity_properties` | Tudo de uma entidade: geometria completa, atributos, propriedades dinâmicas, XData, dicionário de extensão. |
| `get_selection` | O que o usuário selecionou no AutoCAD. |
| `get_block_attributes` | Todas as referências de um bloco (inclusive dinâmicas) com seus atributos. |

**Escrita.** Cada chamada é **um grupo de undo**: `U` desfaz a chamada inteira.

| Ferramenta | Descrição |
|---|---|
| `create_line`, `create_polyline` (com bulge), `create_circle`, `create_arc`, `create_text`, `create_mtext` | Aceitam `layer` (criado se não existir), `color`, `linetype`, `lineweight`. |
| `create_dimension` | Tipos `linear`, `aligned`, `radial` e `diametric`. As duas últimas podem usar o handle de um círculo ou arco. |
| `create_table` | Tabela a partir de uma matriz de strings. |
| `insert_block` | Insere bloco com atributos. Pode importar a definição de um DWG (`sourceFile`). |
| `set_block_attributes` | Altera atributos de uma ou mais referências de bloco. |
| `create_layer` | Cria um layer ou atualiza um existente. |
| `set_entity_properties` | Layer, cor, linetype, espessura, escala de linetype, transparência. |
| `move_entities`, `copy_entities` (com arranjo), `rotate_entities`, `scale_entities`, `mirror_entities`, `erase_entities` | Operam por handles e são atômicas: um handle inválido cancela a chamada inteira. |
| `run_command` | Último recurso: comando de linha via `Editor.CommandAsync`. Retorna os handles criados. |
| `save_drawing`, `zoom_extents` | Salvar ou "salvar como", e zoom extents. |

**Execução de código**

`execute_csharp` compila o código com Roslyn (fora da thread do AutoCAD) e executa na thread principal, dentro de um lock do documento e de uma transação aberta.

- **Globais:** `Doc`, `Db`, `Ed`, `Tr`, `Print(obj)`.
- **Helpers:** `Id(handle)`, `Open<T>(handle, mode)`, `ModelSpace()`, `CurrentSpace()`, `Add(entity)`, `Rad()`, `Deg()`.
- **Commit e rollback:** commit se o script terminar sem exceção, rollback se lançar exceção. Com `commit=false`, sempre faz rollback (bom para testes).
- **Retorno:** saída do `Print`, valor de retorno convertido para JSON, ou o erro com stack trace e a linha do script. Erros de compilação vêm com linha e coluna.
- **Cache:** scripts idênticos reutilizam a compilação (cache por SHA-256).

```csharp
// Exemplo: numerar todas as salas (blocos SALA) da esquerda para a direita
var bt = (BlockTable)Tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
var refs = ((BlockTableRecord)Tr.GetObject(bt["SALA"], OpenMode.ForRead))
    .GetBlockReferenceIds(true, false).Cast<ObjectId>()
    .Select(id => (BlockReference)Tr.GetObject(id, OpenMode.ForRead))
    .OrderBy(b => b.Position.X).ToList();
var n = 1;
foreach (var br in refs)
    foreach (ObjectId aid in br.AttributeCollection)
    {
        var ar = (AttributeReference)Tr.GetObject(aid, OpenMode.ForWrite);
        if (ar.Tag == "NUMERO") ar.TextString = (n++).ToString("000");
    }
return refs.Count;
```

**Limitações:** não use `await` nem loops infinitos. O código roda de forma síncrona na thread do AutoCAD e não dá para interrompê-lo.

## Exemplos de pedidos ao Claude

- "Que unidades este desenho usa e quantas entidades tem em cada layer?"
- "Desenhe uma planta retangular de 12 × 8 m com paredes de 15 cm no layer PAREDES e cote as duas dimensões."
- "Liste todos os blocos LUMINARIA com seus atributos e monte uma tabela com a quantidade por tipo."
- "Selecionei algumas linhas; mova-as 500 mm para a direita e mude para o layer EIXOS."
- "Escreva um script que encontre polilinhas fechadas no layer AMBIENTES e coloque um texto com a área em m² no centro de cada uma."

## Arquitetura

```
Claude ──HTTP (Streamable HTTP, JSON-RPC)──► Kestrel 127.0.0.1:5100/mcp   (thread pool)
                                               │  SDK oficial ModelContextProtocol.AspNetCore 2.2
                                               ▼
                                        ToolInvoker  (log, tempo, erros → JSON)
                                               │  SynchronizationContext.Post
                                               ▼
                               Thread principal do AutoCAD (contexto de aplicação)
                               DocScope: LockDocument(undo group) + Transaction
                               (run_command / save_drawing: ExecuteInCommandContextAsync)
```

- **Kestrel dentro do AutoCAD.** O AutoCAD 2027 já declara `Microsoft.AspNetCore.App` no próprio runtimeconfig, então o Kestrel roda dentro do processo sem conflito de dependências.
- **Roslyn fixado em 4.10.0.** É a mesma versão que o AutoCAD 2027 traz, para não haver choque de versões.
- **Thread principal e timeout.** O `SynchronizationContext` é capturado no primeiro `Application.Idle`. Se o AutoCAD não ficar livre em `CallTimeoutSeconds` (por exemplo, com um diálogo modal aberto), a chamada falha **sem executar nada depois**.
- **Comando em andamento.** Ferramentas de escrita recusam rodar enquanto há um comando ativo, pedindo para finalizar ou cancelar com Esc.

### Segurança

- O servidor escuta **só em 127.0.0.1**.
- Requisições com `Host` ou `Origin` que não sejam loopback são **recusadas com 403**. Isso impede que uma página aberta no navegador acesse o servidor (DNS rebinding ou POST de outro site).
- `execute_csharp` executa **código arbitrário** com as permissões do seu usuário. Só conecte clientes em que você confia. Se não quiser o servidor ativo, use `MCPSTOP` ou `"AutoStart": false`.

## Logs e diagnóstico

- `%APPDATA%\AcadMcp\logs\acadmcp-AAAA-MM-DD.log` registra cada chamada com parâmetros, duração e erros (com stack trace), além dos avisos do Kestrel e do SDK MCP.
- `GET http://127.0.0.1:5100/health` responde com status e contadores.
- `test-mcp.ps1` faz um teste rápido: initialize, lista de ferramentas e uma chamada.
- `tests\e2e-test.ps1` é o teste completo. Rode com um **desenho vazio** aberto; ele exercita todas as ferramentas, o rollback e o undo.

| Problema | Causa provável |
|---|---|
| Nenhuma mensagem do AcadMcp ao abrir o AutoCAD | O bundle não foi carregado. Confira a pasta do bundle e o aviso de segurança (`SECURELOAD`). |
| `ERRO ao iniciar ... porta` | A porta está em uso. Mude `Port` no `config.json` e rode `MCPSTART`. |
| Chamada demora e dá timeout | Há um diálogo modal aberto ou um comando esperando entrada. Feche o diálogo ou pressione Esc. |
| `O AutoCAD está executando o comando ...` | Finalize ou cancele o comando atual. |

## Estrutura

```
AcadMcp/
├─ build-and-deploy.ps1      compila + instala o bundle
├─ test-mcp.ps1              teste rápido
├─ tests/e2e-test.ps1        teste ponta a ponta
├─ bundle/PackageContents.xml
└─ src/AcadMcp/
   ├─ Plugin.cs, Commands.cs, Config.cs
   ├─ Infrastructure/        MainThread, DocScope, Logger, Fmt (JSON/unidades)
   ├─ Server/                McpHost (Kestrel + MCP), ToolInvoker
   └─ Tools/                 Drawing, Query, Create, Block, Modify, Command; Scripting/ (execute_csharp)
```
