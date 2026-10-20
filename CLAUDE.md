# AcadMcp2027: guia para o Claude

Plugin .NET 10 que roda **dentro do `acad.exe`** (AutoCAD 2027, R26.0) e expõe um servidor MCP em
`http://127.0.0.1:5100/mcp`: 32 ferramentas tipadas e `execute_csharp`. O uso para humanos está no `README.md`.
Este arquivo explica **como foi construído e por quê**.

## Arquitetura (o fluxo de uma chamada)

```
HTTP (Kestrel, thread pool) → SDK ModelContextProtocol.AspNetCore 2.2 (Streamable HTTP, Stateless)
  → [McpServerTool] em Tools/*.cs → ToolInvoker (log, tempo, erro → JSON)
  → MainThread.InvokeAsync (SynchronizationContext.Post) → DocScope (LockDocument + Transaction) → corpo da ferramenta
```

- **`Plugin.cs`:** `IExtensionApplication`. Captura o `SynchronizationContext` no **primeiro `Application.Idle`**, não no `Initialize`, onde ele pode ainda ser nulo. Depois sobe o servidor e aquece o Roslyn em background.
- **`Infrastructure/MainThread.cs`:** despacha trabalho para a thread do AutoCAD, com timeout. Se a thread não pegar o trabalho a tempo, ele é cancelado e **nunca roda atrasado**. Também tem `InvokeInCommandContextAsync`, que usa `ExecuteInCommandContextAsync` (necessário para `Editor.CommandAsync`).
- **`Infrastructure/DocScope.cs`:** faz o lock do documento e abre a transação. Commit no sucesso, abort na exceção. O lock de escrita recebe um `globalCommandName`, e isso faz **cada chamada virar um grupo de undo** (validado: um `U` desfaz a chamada inteira). Escrita é recusada se `doc.CommandInProgress` não estiver vazio.
- **`Server/ToolInvoker.cs`:** todo tool passa por `Read`, `Write`, `Raw`, `CommandContext` ou `Custom`. Devolve um `CallToolResult` com JSON `{ok, tool, durationMs, units, result}`, ou `isError` com `{type, message, errorStatus, details, stackTrace}`. Use `ToolException(msg, details)` para erros com dados estruturados.
- **`Server/McpHost.cs`:**
  - Kestrel só em loopback.
  - `LoopbackGuard` responde 403 se `Host` ou `Origin` não forem locais (proteção contra DNS rebinding e CSRF; o `execute_csharp` é execução remota de código).
  - Lifetime e logging do host substituídos, para não registrar handlers de console dentro do AutoCAD.
- **`Tools/`:**
  - `Acad.cs`: handles, cores, lineweights, layers, linetypes, espaços.
  - `EntityInfo.cs`: resumo e detalhes de entidades.
  - `Inputs.cs`: os records `P3`, `PolyVertex` e `Rect`.
  - `Scripting/`: o `execute_csharp`.

## Adicionar uma ferramenta

```csharp
[McpServerTool(Name = "minha_tool"), Description("O que faz, em PT-BR, com unidades e formatos.")]
public static Task<CallToolResult> MinhaTool([Description("...")] P3 ponto, string? layer = null) =>
    ToolInvoker.Write("minha_tool", new { ponto, layer }, s =>   // Read para consultas
    {
        var e = new Circle(ponto.ToPoint(), Vector3d.ZAxis, 1);
        Acad.AddToCurrentSpace(s, e, Style.Of(layer, null, null, null));
        return EntityInfo.Summary(s.Tr, e);
    });
```

As ferramentas são registradas por `WithToolsFromAssembly`; não há lista para manter. Regras:
- entrada e saída em **graus**, WCS e unidades do desenho;
- ferramentas que recebem vários handles são atômicas (use `Acad.IdsFromHandles`);
- erros de validação lançam `ArgumentException` (vão sem stack trace).

## Armadilhas já resolvidas (não repita)

| Sintoma | Causa | Solução |
|---|---|---|
| Kestrel ia precisar de um resolvedor de assemblies | `acdbmgd.runtimeconfig.json` do AutoCAD 2027 **já declara `Microsoft.AspNetCore.App`** | Basta `FrameworkReference`. Não troque por `HttpListener`. |
| Aviso MSB3277 (System.Drawing, VisualBasic) | As DLLs do AutoCAD compilam contra o WindowsDesktop | `UseWPF` + `UseWindowsForms` no csproj |
| `Color`/`Region` ambíguos | Usings implícitos de WinForms | `<Using Remove="System.Drawing" />` e `<Using Remove="System.Windows.Forms" />` |
| Classe `Json` não encontrada | Um assembly referenciado define um **namespace global `Json`** | O helper se chama `Fmt` |
| `TypeInfoResolver ... before being marked as read-only` | O SDK faz `MakeReadOnly()` nas opções | `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` |
| Schema com `anyOf NaN/Infinity` em todo número | `AllowNamedFloatingPointLiterals` nas opções passadas ao SDK | `Fmt.Options` (entrada/schema) separado de `OutputOptions` (saída) |
| `case Table` inalcançável | `Table` herda de `BlockReference` | Testar `Table` antes |
| `eNullObjectId` no `create_layer` | `LayerTableRecord` novo tem linetype nulo até o commit | Definir `GetLinetypeContinuousId` ao criar |
| Conflito de versão do Roslyn | O AutoCAD traz `Microsoft.CodeAnalysis` 4.10 | Pacote **fixado em 4.10.0** |

## `execute_csharp`

- **Compilação:** fora da thread principal (`Task.Run`), com cache por SHA-256 do código.
- **Execução:** na thread principal dentro do `DocScope`. Usa `RunAsync(...).Wait()`, então scripts com `await` que dependam da thread do AutoCAD dão deadlock (documentado).
- **Globais** (`ScriptGlobals`): `Doc`, `Db`, `Ed`, `Tr`, `Print`, `Id`, `Open<T>`, `ModelSpace`, `CurrentSpace`, `Add`, `Rad`, `Deg`.
- **Referências:** assemblies semente mais as referências diretas delas.
- **Debug:** `WithEmitDebugInformation` + `WithFilePath("execute_csharp.csx")` dão o número da linha no stack trace.
- **Validar correções sem redeploy:** rode o trecho com `execute_csharp` e `commit=false` no AutoCAD aberto. Nada persiste.

## Fluxo de trabalho

1. **Deploy exige o AutoCAD fechado**, porque o `acad.exe` trava a DLL. Peça ao usuário para fechar e rode `build-and-deploy.ps1`. Ele instala em `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcp.bundle`.
2. **Teste:** o usuário abre um **desenho vazio**; rode `tests\e2e-test.ps1`. São 35 passos, incluindo rollback e undo; o estado atual é 35/35. O log fica em `%APPDATA%\AcadMcp\logs\`.
3. **Build:** só `nullable` e `CA1416` viram erro. Mantenha 0 avisos.
4. **Ambiente:**
   - É PowerShell 5.1: arquivos `.ps1` com acentos precisam de **UTF-8 com BOM**.
   - `[IO.File]` com caminho relativo usa o diretório do processo, não o do PowerShell. Use caminhos absolutos.
   - O `gh` fica em `C:\Program Files\GitHub CLI\gh.exe`.
5. **Claude Desktop:** a configuração só aceita stdio, então a conexão passa por uma ponte: `uvx --with "mcp<2" mcp-proxy --transport streamablehttp http://127.0.0.1:5100/mcp`. O `mcp<2` é obrigatório porque o `mcp-proxy` atual quebra com `mcp` 2.x. Detalhes no README.

## Git (regra do dono do repositório)

- Commits **só na `main`**, a não ser que ele peça outra coisa.
- **Ele é o único autor**: Rafael Cruz `<185978906+rafaelcruzmartins@users.noreply.github.com>`.
- **Nunca** use `Co-Authored-By: Claude`, "Generated with Claude Code" ou atribuição parecida.
