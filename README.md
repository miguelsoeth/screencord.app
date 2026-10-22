<p align="center">
  <img src="https://raw.githubusercontent.com/miguelsoeth/screencord/main/icon.png" width="96" alt="">
</p>

<h1 align="center">ScreenCord</h1>

<p align="center">
  <strong>Compartilhe a tela com os amigos em até 1080p a 60 fps. Grátis, código aberto, e funciona até sem internet, na rede local.</strong>
</p>

<p align="center">
  <a href="https://github.com/miguelsoeth/screencord/releases/latest"><img src="https://img.shields.io/github/v/release/miguelsoeth/screencord?label=baixar&style=for-the-badge" alt="Baixar a versão mais recente"></a>
  <a href="https://screencord.miguelsoeth.online/"><img src="https://img.shields.io/badge/site-screencord-5c78ff?style=for-the-badge" alt="Site"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/miguelsoeth/screencord.app?style=for-the-badge" alt="Licença GPL-3.0"></a>
</p>

<p align="center">
  <img src="https://raw.githubusercontent.com/miguelsoeth/screencord/main/img/sala-ao-vivo.jpg" width="820" alt="Janela do ScreenCord com uma transmissão ao vivo e a lista de grupos com quem está conectado">
</p>

O ScreenCord é um app para Windows para assistir à tela dos amigos: entre com a sua conta do Discord, adicione os amigos, crie um grupo e clique em **AO VIVO** para ver quem está transmitindo. Sem assinatura, sem anúncios.

> O ScreenCord é um projeto independente, **não oficial**, sem ligação com o Discord. Ele usa a conta do Discord só para identificar você (escopo `identify`): não lê suas mensagens, servidores nem lista de amigos.

## Destaques

- **Tela inteira ou uma janela**, com o som junto, e a resolução e os quadros que você escolher.
- **Grupos com os amigos**: veja quem está conectado e quem está ao vivo, e assista em tela cheia.
- **Salas locais, sem servidor**: na mesma rede (LAN, Hamachi, Radmin ou Tailscale), a tela vai direto entre os computadores, até **1080p a 60 fps**. Dá para usar até **sem conta**, só com um nome.
- **Atualiza sozinho**: ao abrir, o app instala a versão nova. Em Configurações → Atualizações dá para desligar e escolher na hora (atualizar, agora não ou pular a versão); só uma versão que o servidor não aceita mais precisa ser instalada para continuar.
- **Português, inglês e espanhol**, com troca na hora.

| Amigos | Compartilhar | Sala local |
|---|---|---|
| <img src="https://raw.githubusercontent.com/miguelsoeth/screencord/main/img/amigos.jpg" alt="Lista de amigos"> | <img src="https://raw.githubusercontent.com/miguelsoeth/screencord/main/img/compartilhar.jpg" alt="Escolha de tela, resolução e quadros"> | <img src="https://raw.githubusercontent.com/miguelsoeth/screencord/main/img/sala-local.jpg" alt="Sala local na rede"> |

## Como usar

1. Baixe o `ScreenCord-Setup-<versão>-x64.msi` na [versão mais recente](https://github.com/miguelsoeth/screencord/releases/latest) e instale (sem administrador; Windows 10 2004 ou mais recente, ou Windows 11, x64).
2. Entre com o Discord (ou **Usar sem conta**, para as salas locais).
3. Adicione os amigos pelo @ (eles também precisam ter o ScreenCord) e crie um grupo.
4. Entre na sala e clique em compartilhar. Quem estiver na sala clica em **AO VIVO** para assistir.

**"O Windows protegeu o computador"?** O instalador ainda não é assinado: clique em **Mais informações → Executar assim mesmo**. O código está todo aqui, e o instalador é gerado pelo [pipeline público](.github/workflows/release.yml) a partir dele. Veja a [Code signing policy](#code-signing-policy).

**Pelo servidor ou direto?** Nos grupos pela internet a tela passa pelo servidor do projeto, até 720p a 30 fps. Nas salas locais vai direto entre os computadores, até 1080p a 60 fps.

## Ajude o projeto

- Deixe uma ⭐ aqui no GitHub: é o que faz o projeto aparecer para mais gente.
- Encontrou um problema ou tem uma ideia? [Abra uma issue](https://github.com/miguelsoeth/screencord.app/issues).
- Quer contribuir com código? Veja [Para desenvolvedores](#para-desenvolvedores) abaixo.
- Apoie no [Ko-fi](https://ko-fi.com/miguelsoeth).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> Pedido em análise: até ele ser aprovado, o instalador ainda sai sem assinatura.

- **O que é assinado:** só o `ScreenCord.exe` e o instalador `ScreenCord-Setup-<versão>-x64.msi`, gerados pelo [`release.yml`](.github/workflows/release.yml) no GitHub Actions a partir do código deste repositório. Cada assinatura é aprovada à mão, release por release.
- **Autores** (mudam o código sem revisão extra): [Miguel Soeth](https://github.com/miguelsoeth).
- **Revisores** (revisam os pull requests de quem não é autor): [Miguel Soeth](https://github.com/miguelsoeth).
- **Aprovadores** (aprovam cada pedido de assinatura): [Miguel Soeth](https://github.com/miguelsoeth).
- **Privacidade:** o que o app envia, para onde e como desligar está em [Privacidade](#privacidade). Ele não manda nada para outros sistemas além dos descritos lá.

<details>
<summary><strong>English</strong></summary>

ScreenCord is a free, open-source Windows app to share your screen with friends at up to 1080p 60 fps. Sign in with Discord (identity only), add friends, create a group and click **LIVE** to watch. Local rooms work without any server, peer to peer on the same network (LAN, Hamachi, Radmin, Tailscale), even without an account. Available in English, Portuguese and Spanish. [Download the latest version](https://github.com/miguelsoeth/screencord/releases/latest). Unofficial project, not affiliated with Discord.

**Code signing policy:** free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org) (application pending: until it is approved, the installer is still unsigned). Only `ScreenCord.exe` and the MSI built by the public [`release.yml`](.github/workflows/release.yml) from this repository are signed, each release approved by hand. Committers, reviewers and approvers: [Miguel Soeth](https://github.com/miguelsoeth). Privacy: see [Privacidade](#privacidade) (error reports, which can be turned off in the app's Settings menu, under Privacy; the update check on GitHub; Discord sign-in).

</details>

---

# Para desenvolvedores

Este repositório contém o **aplicativo desktop** (`src/ScreenCord`), seus testes e os contratos compartilhados (`src/ScreenCord.Contracts`). A API central fica em [screencord.api](https://github.com/miguelsoeth/screencord.api), e a página de download e as releases em [screencord](https://github.com/miguelsoeth/screencord). Os contratos existem nos dois repositórios: ao mudar um DTO, atualize os dois.

Sistema com dois componentes:

- **Aplicativo desktop** (C# / .NET 10 / WPF): conecta uma conta do Discord via OAuth2, mantém os grupos e compartilha a tela (Media Foundation).
- **API central** (ASP.NET Core / .NET 10 / SQLite): cadastra quem entra com o Discord, mantém as amizades **entre usuários deste sistema** e repassa as transmissões dos grupos.

> **Importante:** os "amigos" aqui são do ScreenCord, não a sua lista de amigos do Discord. A API oficial do Discord não permite ler a lista de amigos (veja [Limitações reais da API do Discord](#limitações-reais-da-api-do-discord)). Para alguém aparecer na busca, essa pessoa precisa ter entrado neste sistema com a conta dela.

## Recursos em detalhe

- Login pelo navegador com **OAuth2 Authorization Code + PKCE (S256)**, como cliente público (sem `client_secret`).
- Perfil via `GET /users/@me`: nome de exibição, nome de usuário, ID, avatar e URL do avatar.
- **Cadastro automático**: ao conectar, o app faz login na API central, que cria o usuário (ou atualiza nome e avatar) com os dados confirmados pelo Discord.
- **Amigos no sistema**: no menu do botão de amigos (ao lado de atualizar perfil, com uma bolinha vermelha quando há solicitações recebidas), buscar usuários por nome (`@fulano`), enviar solicitação, aceitar, recusar, cancelar e remover amigo (com confirmação).
- **Grupos (salas de presença em tempo real)**: lista compacta na lateral direita, uma linha por grupo com nome, indicador de conexão e a contagem `conectados/integrantes` (ex.: `1/2`). Quem está conectado aparece listado (foto e nome) logo abaixo do grupo; a seta à esquerda recolhe ou expande essa lista. Clicar no nome conecta você à sala; clicar na contagem abre os integrantes (foto, nome e quem está conectado) e permite adicionar amigos. Qualquer integrante adiciona os próprios amigos ou sai do grupo; só quem criou o grupo vê **Excluir grupo**, que o apaga para todos (com confirmação). Entradas e saídas aparecem na hora para todos.
- **Compartilhamento de tela**: na sala em que você está, o botão de compartilhar (ao lado de "Sair da sala") abre a escolha de **tela inteira ou janela**, **resolução**, **taxa de quadros** e **compartilhar áudio** ou não. Nos grupos, a tela passa pelo servidor e vale o limite dele: 720p a 30 fps, até 2500 kbps (o app lê de `GET /limits`). Nas [salas locais](#salas-locais) a tela vai direto entre os computadores: até 1080p a 60 fps e 5000 kbps. As opções acima do limite aparecem desligadas. A taxa de bits não é escolhida: começa no teto e se adapta à conexão de quem compartilha, sem passar dele. O padrão é a maior resolução do caminho a 30 fps, com áudio; a última escolha é lembrada enquanto o app está aberto. Durante o compartilhamento, a engrenagem ao lado de parar abre a mesma escolha para **alterar as configurações** (ou a tela/janela): o compartilhamento recomeça por baixo dos panos e quem está assistindo continua vendo, já com a nova configuração. Quem compartilha aparece com o selo **AO VIVO** na lista de conectados; clicar nele mostra a transmissão na área principal da janela, uma de cada vez (abrir outra troca a atual; o **X** da barra para de assistir), com volume, mudo e **tela cheia** (botão, F11 ou duplo clique; Esc, F11 ou duplo clique para sair). Clicar em AO VIVO de outro grupo entra na sala dele antes. Sair da sala, trocar de sala ou perder a conexão encerra o compartilhamento e fecha a transmissão que você assistia.
- **Salas locais (sem servidor)**: para quem está na mesma rede (LAN, Hamachi, Radmin ou Tailscale). O **+** dos grupos cria uma sala hospedada no seu computador; as salas da rede aparecem sozinhas na lista (selo **LOCAL**), ou dá para entrar pelo IP de quem hospeda. A tela vai direto entre os computadores, até 1080p a 60 fps e 5 Mbps, e tudo funciona com a API fora do ar. **Usar sem conta** (na tela inicial) entra só com um nome, sem Discord, e mostra apenas as salas locais. Detalhes em [Salas locais](#salas-locais).
- Sessão do Discord reutilizada entre execuções (tokens cifrados com DPAPI), com renovação automática.
- Tratamento de falta de conexão, timeout, token inválido, autorização negada, HTTP 429 e indisponibilidade — tanto do Discord quanto da API central.

## Requisitos

- Windows 10 (versão 2004 ou mais recente) ou Windows 11, x64 (aplicativo). A API roda em qualquer sistema com .NET 10.
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)

## 1. Configurar o aplicativo no Discord Developer Portal

1. Acesse <https://discord.com/developers/applications> e clique em **New Application**.
2. Em **General Information**, copie o **Application ID** (é o *Client ID*; não é um segredo).
3. Na aba **OAuth2**:
   - Em **Redirects**, adicione exatamente `http://127.0.0.1:47821/callback`.
   - Ative **Public Client** (permite trocar o código com PKCE, sem `client_secret`).
   - Salve.
4. **Não** use o *Client Secret* nem a *Public Key*: nenhum dos dois é necessário.

O único escopo solicitado é `identify`.

## 2. Configuração

O mesmo Application ID vai nos dois componentes. No aplicativo ele já está em `src/ScreenCord/appsettings.json` (não é um segredo). Na API local, fica em `src/ScreenCord.Api/appsettings.Local.json` no repositório **screencord.api** (ignorado pelo Git):

```json
{
  "Discord": { "ClientId": "SEU_APPLICATION_ID" }
}
```

A API usa o Client ID para recusar tokens do Discord emitidos para outros aplicativos.

### Produção e desenvolvimento

| Arquivo | Quando vale | `Api:BaseUrl` |
|---|---|---|
| `appsettings.json` | sempre (é o único que vai para a publicação) | `https://api.screencord.miguelsoeth.online` (produção) |
| `appsettings.Development.json` | com `DOTNET_ENVIRONMENT=Development`: `dotnet run` (via `Properties/launchSettings.json`) | `http://localhost:5080` |
| `appsettings.Local.json` | se existir; ignorado pelo Git, sobrepõe os outros e nunca é publicado | o que você quiser (ex.: um túnel) |

Argumentos de linha de comando (ex.: `--Api:BaseUrl=https://...`) valem acima de todos esses arquivos; é assim que o `screencord.ps1` escolhe a API do app.

A API de produção roda num VPS em `https://api.screencord.miguelsoeth.online`, com certificado da Let's Encrypt renovado automaticamente pelo Caddy. O app valida o certificado normalmente; nada de TLS é desativado. Para conferir se ela está no ar: `GET https://api.screencord.miguelsoeth.online/health` devolve `{"status":"ok","version":"..."}`.

Para rodar o executável de Debug direto contra a API local, defina `DOTNET_ENVIRONMENT=Development` antes; sem isso ele usa a produção.

Opções do aplicativo:

| Chave | Padrão | Descrição |
|---|---|---|
| `Discord:ClientId` | Application ID do ScreenCord | Application ID. Obrigatório. |
| `Discord:RedirectPort` | `47821` | Porta do callback local. Se mudar, atualize o redirect no portal. |
| `Discord:Scopes` | `identify` | Escopos separados por espaço. |
| `Discord:LoginTimeoutSeconds` | `300` | Espera máxima pela autorização no navegador. |
| `Api:BaseUrl` | `https://api.screencord.miguelsoeth.online` (Development: `http://localhost:5080`) | Endereço da API central. Precisa ser `https`, exceto para localhost. |

Opções da API:

| Chave | Padrão | Descrição |
|---|---|---|
| `Discord:ClientId` | *(vazio)* | Application ID. Obrigatório. |
| `ConnectionStrings:Default` | `Data Source=screencord.db` | Banco SQLite. |
| `Session:LifetimeDays` | `30` | Validade da sessão da API. |

## 3. Executar em modo Debug

### Painel de desenvolvimento (recomendado)

```bash
powershell -ExecutionPolicy Bypass -File .\screencord.ps1
```

Menu navegável com as setas (↑/↓, Enter, Esc) para subir, parar e recompilar a API e o app, acompanhar os logs ao vivo e rodar os testes. Os processos rodam em segundo plano e continuam vivos ao fechar o menu; PIDs e logs ficam em `.screencord/`. A API é procurada no clone do **screencord.api** ao lado deste (`..\screencord.api`); para outro lugar, defina `SCREENCORD_API_ROOT`.

O app pode usar a **API local** (`http://localhost:5080`, padrão) ou a **API de produção** oficial (o `Api:BaseUrl` do `appsettings.json`): troque na opção "API do app" do menu. A escolha fica salva em `.screencord/` e vale a partir da próxima vez que o app subir (use "Reiniciar app"). Com a produção, "Subir tudo" abre só o app, sem subir a API local.

Também aceita ações diretas: `status`, `start-api`, `stop-api`, `start-app`, `stop-app`, `restart-app`, `start-all`, `stop-all`, `build`, `rebuild`, `test`, `logs-api`, `logs-app`, e `-Api local|prod` para escolher (e salvar) a API do app. Exemplos: `.\screencord.ps1 start-all`, `.\screencord.ps1 restart-app -Api prod`.

### Manualmente

Em um terminal, a API, a partir do clone do **screencord.api** (fica em `http://localhost:5080`; o banco é criado na primeira execução):

```bash
dotnet run --project src/ScreenCord.Api
```

Em outro, o aplicativo (em Development, portanto usando a API local):

```bash
dotnet run --project src/ScreenCord
```

Para rodar o app contra a API de produção, use `dotnet run --project src/ScreenCord --no-launch-profile`.

Sem a API no ar, o perfil continua funcionando e a seção "Amigos" mostra um erro com o botão "Tentar novamente".

Para testar os grupos em tempo real, abra duas instâncias do aplicativo com contas diferentes que sejam amigas, crie um grupo em uma, adicione a outra pela contagem de integrantes e clique no nome do grupo nas duas. Para testar o compartilhamento de tela, clique no botão de compartilhar em uma delas e no selo AO VIVO na outra.

Para testar a amizade entre duas contas na mesma máquina, entre com a primeira, clique em **Desconectar**, entre com a segunda, busque a primeira pelo nome de usuário e envie a solicitação.

## 4. Executar os testes

```bash
dotnet test ScreenCord.sln
```

Nenhum teste acessa o Discord: as respostas são simuladas. Os testes da API sobem o servidor real em memória, com SQLite em memória. Os testes de mídia codificam e decodificam vídeo de verdade com o Media Foundation do Windows (usando o encoder de hardware, se houver).

## 5. Publicar

Cada versão é publicada pelo CI a partir de uma tag `vX.Y.Z` (veja o `CLAUDE.md`): a [GitHub Release](https://github.com/miguelsoeth/screencord.app/releases) traz o instalador `ScreenCord-Setup-X.Y.Z-x64.msi` (detalhes abaixo). A mesma release é criada no repo [miguelsoeth/screencord](https://github.com/miguelsoeth/screencord), de onde o app se atualiza e que tem a página de download: <https://screencord.miguelsoeth.online/>.

Para gerar só o executável (único, sem exigir .NET instalado):

```bash
dotnet publish src/ScreenCord -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/app
```

Distribua `ScreenCord.exe` com o `appsettings.json` que sai na pasta: ele já aponta para a API de produção (`https://api.screencord.miguelsoeth.online`). `appsettings.Development.json` e `appsettings.Local.json` não são copiados para a publicação.

### Instalador (MSI)

O jeito recomendado de distribuir é o instalador. Ele publica o app e empacota tudo num único MSI (~55 MB, com o runtime do .NET embutido: quem instala não precisa ter o .NET):

```bash
dotnet build installer/ScreenCord.Installer -c Release -p:AppVersion=0.1.0
```

O arquivo sai em `installer/ScreenCord.Installer/bin/Release/ScreenCord-Setup.msi`. O projeto usa o WiX Toolset 6 (baixado pelo NuGet; não precisa instalar nada) e fica fora do `ScreenCord.sln`, para o `dotnet test` não publicar o app a cada execução.

- Instala só para o usuário atual, em `%LocalAppData%\Programs\ScreenCord`, **sem pedir administrador**. Cria atalhos no Menu Iniciar e na Área de Trabalho e oferece abrir o app no fim.
- A versão do MSI é a passada em `-p:AppVersion` (use a da release; sem ela, `0.0.0`). Um MSI de versão maior substitui o anterior (o `UpgradeCode` em `Package.wxs` nunca deve mudar). A sessão do Discord, em `%LocalAppData%\ScreenCord`, é preservada.
- Desinstala em Configurações → Aplicativos. Instalação silenciosa: `msiexec /i ScreenCord-Setup.msi /qn`.
- O MSI ainda não é assinado: o Windows SmartScreen mostra "O Windows protegeu o computador" na primeira vez. O usuário clica em **Mais informações → Executar assim mesmo**. O pedido de assinatura grátis está no SignPath Foundation (ver [Code signing policy](#code-signing-policy)).
- Ao desinstalar (não nas atualizações), o instalador roda `ScreenCord.exe --remove-firewall`: se as regras do firewall existirem, ele pede a permissão do Windows e as apaga; recusar não impede a desinstalação.
- Os termos mostrados na instalação ficam em `installer/ScreenCord.Installer/License.rtf`.

API:

```bash
dotnet publish src/ScreenCord.Api -c Release -o publish/api
```

Antes de expor a API fora da sua máquina:

- Sirva **somente por HTTPS** (o app recusa `http` que não seja localhost). O token do Discord trafega até a API no login.
- O proxy reverso precisa repassar WebSockets (`/hubs/groups` e `/media/...`) e não deve guardar as respostas em buffer, senão o vídeo atrasa. Conte com a banda de saída do compartilhamento de tela (taxa × espectadores).
- Atrás de proxy reverso, configure *forwarded headers*, senão o limite de requisições por IP enxerga só o IP do proxy.
- O banco é criado com `EnsureCreated`. Para evoluir o esquema sem perder dados, adote migrações do EF Core. Para mais de uma instância, troque o SQLite por um banco servidor.
- Faça backup do arquivo `screencord.db`.

## API central

Com a API rodando em Development (o padrão do `screencord.ps1` e do `dotnet run`), a documentação interativa fica em:

- Swagger UI: <http://localhost:5080/swagger>
- Documento OpenAPI: <http://localhost:5080/openapi/v1.json>

Fora de Development as duas rotas não são publicadas.

Todas as rotas, exceto `/health` e `/auth/discord`, exigem `Authorization: Bearer <token de sessão>`.

| Método e rota | Descrição |
|---|---|
| `GET /health` | Verificação de disponibilidade. |
| `POST /auth/discord` | Corpo `{ "accessToken": "<token do Discord>" }`. Confirma o token com o Discord, cria/atualiza o usuário e devolve `{ token, expiresAtUtc, user }`. |
| `POST /auth/logout` | Encerra a sessão atual. |
| `GET /me` | Usuário da sessão. |
| `GET /users/search?q=` | Busca por nome de usuário ou de exibição (mínimo 2 caracteres, até 20 resultados, sem o próprio usuário). Cada item traz `relationship`. |
| `GET /friends` | Amigos confirmados. |
| `DELETE /friends/{userId}` | Desfaz a amizade. |
| `GET /friends/requests` | Solicitações `incoming` e `outgoing`. |
| `POST /friends/requests` | Corpo `{ "userId": "<guid>" }`. Envia a solicitação; se a outra pessoa já tinha pedido, confirma a amizade. |
| `POST /friends/requests/{userId}/accept` | Aceita a solicitação recebida de `userId`. |
| `DELETE /friends/requests/{userId}` | Recusa uma solicitação recebida ou cancela uma enviada. |
| `GET /groups` | Seus grupos, com `memberCount`, `connectedUsers` (id, nome e avatar de quem está na sala) e `presenceVersion`. |
| `POST /groups` | Corpo `{ "name": "..." }` (1 a 40 caracteres). Cria o grupo com você como integrante. Cada conta pode ter até 5 grupos criados por ela (contam os que ela ainda integra); acima disso, 409 `group_limit`. |
| `GET /groups/{groupId}` | Integrantes, cada um com `isConnected`. Só para integrantes. |
| `POST /groups/{groupId}/members` | Corpo `{ "userId": "<guid>" }`. Adiciona um amigo seu (limite de 10 integrantes). |
| `DELETE /groups/{groupId}/members/me` | Sai do grupo; o grupo é apagado quando o último integrante sai. |
| `DELETE /groups/{groupId}` | Exclui o grupo para todos (só quem criou; 403 `not_group_owner` para os outros). Todos saem da sala, os compartilhamentos terminam e cada integrante recebe `GroupRemoved`. `GET /groups/{groupId}` traz `canDelete`. |

`relationship` é `None`, `Friend`, `PendingOutgoing` ou `PendingIncoming`. Erros retornam `{ "code": "...", "message": "..." }`.

### Tempo real (SignalR)

O hub `/hubs/groups` mantém uma conexão persistente (WebSocket, com fallback automático do SignalR), autenticada com o mesmo token de sessão no cabeçalho `Authorization` — o token nunca vai na URL.

| Mensagem | Direção | Descrição |
|---|---|---|
| `JoinGroup(groupId)` | app → API | Entra na sala (saindo da anterior). Retorna a presença atual, ou `null` se você não for integrante. |
| `LeaveGroup()` | app → API | Sai da sala atual. Fechar a conexão tem o mesmo efeito. |
| `PresenceChanged(presence)` | API → app | `{ groupId, connectedUsers, version }`, enviado a todos os integrantes a cada entrada ou saída. |
| `GroupChanged(group)` | API → app | Grupo criado ou com integrantes alterados (inclusive para quem acabou de ser adicionado). |
| `GroupRemoved(groupId)` | API → app | Você saiu do grupo (avisa as suas outras janelas). |
| `StartScreenShare(start)` | app → API | `{ width, height, frameRate, bitrateKbps, hasAudio }`. Começa a compartilhar na sala atual; retorna `{ userId, width, height, frameRate, bitrateKbps, hasAudio }`, ou `null` se a conexão não estiver numa sala ou a configuração passar dos limites de `GET /limits` (padrão: até 720p, 30 fps, 2500 kbps). |
| `StopScreenShare()` | app → API | Encerra o compartilhamento desta conexão. Sair da sala, trocar de sala ou desconectar também encerra. |

A presença (`PresenceChanged` e os grupos de `GET /groups`) traz também `screenShares`: quem está compartilhando na sala e com que configuração.

`version` cresce a cada mudança de presença (inclusive início e fim de compartilhamento); o app descarta notificações mais antigas que a última aplicada, então mensagens fora de ordem não deixam a lista errada. Ao reconectar, o app recarrega os grupos e volta para a sala em que estava. A presença fica em memória numa instância da API; para rodar várias instâncias seria preciso um backplane (ex.: Redis).

### Compartilhamento de tela

```
quem compartilha                          API                              quem assiste
Windows Graphics Capture ─┐
  (tela ou janela)        │ GPU: BGRA → NV12, escala   WebSocket /media/{grupo}/{id}   WebSocket /media/{grupo}/{id}
H.264 (Media Foundation) ─┼──────────────────────────► repasse sem decodificar ───────► H.264 → BGRA → janela
Opus (áudio por processo)─┘                             (uma fila por espectador)         Opus → WASAPI
```

- **Captura**: Windows Graphics Capture (tela ou janela, com o cursor; no Windows 11, sem a borda amarela). No Windows 10, onde essa borda não pode ser tirada, telas são capturadas com o Desktop Duplication do DXGI (o cursor é desenhado por cima); janelas continuam com a borda. A conversão para NV12 e a mudança de tamanho são feitas na GPU (processador de vídeo do Direct3D 11). A largura segue a proporção da tela ou janela; a altura é a resolução escolhida, sem ampliar origens menores. Se a janela mudar de tamanho, a imagem é encaixada com faixas pretas.
- **Vídeo**: H.264 com taxa de bits constante, baixa latência e sem quadros B, pelo encoder de hardware da placa (NVIDIA, Intel, AMD) quando existe, com o encoder por software do Windows como alternativa. Quadro-chave a cada 4 s e **sob demanda**: quando alguém começa a assistir, a API pede um quadro-chave a quem compartilha, e a imagem aparece em uma fração de segundo. Com a tela parada, o ritmo cai para 2 quadros por segundo (1 s depois da última mudança), em vez de reenviar o mesmo quadro na taxa cheia.
- **Áudio**: Opus 48 kHz estéreo a 128 kbps, em pacotes de 20 ms. Tela inteira: tudo o que toca no computador **menos o próprio ScreenCord** (quem compartilha e também assiste alguém não devolve o áudio da outra pessoa). Janela: só o áudio do programa dono da janela (e dos processos filhos dele, ex.: navegadores). Usa a captura de áudio por processo do Windows 10 2004+; em versões anteriores, a tela inteira captura o dispositivo padrão inteiro e a janela fica sem áudio. A captura começa antes do anúncio na sala: se falhar, o compartilhamento é anunciado sem áudio e quem compartilha vê o aviso. Detalhes da falha ficam no log (`%LOCALAPPDATA%\ScreenCord\logs\screencord-AAAAMMDD.log`, um arquivo por dia, últimos 7 dias).
- **Canal de mídia**: um WebSocket por compartilhamento em `/media/{groupId}/{sharerId}`, autenticado pelo mesmo cabeçalho `Authorization` (o token nunca vai na URL). Quem compartilha conecta no próprio id depois de `StartScreenShare` (antes disso recebe 409); quem assiste conecta no id de quem compartilha e precisa estar na sala (403 se não estiver). Cada mensagem binária é um pacote: 1 byte de tipo (1 vídeo, 2 áudio, 3 pedido de quadro-chave, 4 pausar o vídeo, 5 retomar o vídeo), 1 byte de flags (bit 0 = quadro-chave), 8 bytes de carimbo de tempo em µs (little-endian) e o conteúdo. Vídeo vai em H.264 Annex B com SPS/PPS antes de cada quadro-chave; pacotes acima de 4 MB são recusados.
- **Rede lenta**: a API mantém uma fila por espectador com cerca de 1 s de vídeo. Se ela enche, o espectador perde quadros até o próximo quadro-chave (pedido na hora) em vez de acumular atraso, sem afetar os outros. Do lado de quem compartilha vale o mesmo para a fila de envio. Quem assiste e fica sem imagem pede outro quadro-chave sozinho.
- **Janela minimizada**: quem minimiza o ScreenCord enquanto assiste avisa a API, que para de mandar o vídeo para essa pessoa e mantém só o áudio. Ao restaurar, o vídeo volta pelo próximo quadro-chave, pedido na hora.
- **Bitrate adaptativo**: a qualidade escolhida é o teto. Se a fila de envio de quem compartilha não esvazia (ou estoura), a taxa do encoder cai 30% (até 1/5 da escolhida) e volta a subir aos poucos quando a rede folga. Só olha a conexão de quem compartilha: um espectador com rede ruim continua perdendo quadros sem baixar a qualidade dos outros.

Limitações: a mídia passa pela API, então o tráfego de saída do servidor é a taxa escolhida vezes o número de espectadores (ex.: 2500 kbps para 4 pessoas = 10 Mbps); por isso o limite baixo, e a alta qualidade fica nas salas locais. Para muitos espectadores, o caminho seria WebRTC com um SFU. Conteúdo protegido (ex.: vídeos com DRM em alguns navegadores) aparece preto, como em qualquer captura do Windows. Janelas minimizadas não geram imagem até serem restauradas.

## Salas locais

Salas que não dependem da API: o computador de quem cria a sala é o anfitrião (guarda quem está nela e quem está compartilhando) e a mídia vai direto de quem compartilha para cada espectador, sem passar por servidor. Servem para LAN, Hamachi, Radmin e Tailscale; quem entra precisa alcançar o IP do anfitrião (e o de quem compartilha).

- **Portas**: TCP 47830 (sala e mídia, WebSocket sobre TLS) e UDP 47831 (anúncios). São fixas porque a regra do firewall é por porta.
- **Descoberta**: o anfitrião anuncia a sala a cada 2 s no broadcast de cada interface de rede local ou VPN (o broadcast geral só sai pela interface principal do Windows). Hamachi e Radmin repassam broadcast; o Tailscale não, e ali se entra pelo IP. Uma sala some da lista 7 s depois do último anúncio.
- **Sala**: mensagens JSON (`hello`, `state`, `share`, `unshare`, `closed`) por WebSocket em `/room`. Até 20 pessoas. Quando o anfitrião sai, a sala acaba para todos.
- **Mídia**: quem compartilha anuncia na sala os próprios endereços (`ip:47830`), o id do canal e uma chave aleatória; quem assiste tenta todos os endereços ao mesmo tempo, fica com o primeiro que responder e apresenta a chave no cabeçalho `X-ScreenCord-Key` em `/media/{id}`. O formato dos pacotes é o mesmo do canal com a API, e quem compartilha faz o papel do relay: fila por espectador, descarte até o próximo quadro-chave para quem atrasa, pausa do vídeo de quem minimizou. Não há plano B pelo servidor: se não der para conectar direto, o app explica o que conferir.
- **Firewall**: a regra "ScreenCord - salas locais" (TCP 47830) e "ScreenCord - descoberta de salas" (UDP 47831) libera o exe em todos os perfis de rede (Hamachi e Radmin costumam aparecer como rede "Pública"), só para as faixas de rede local e das VPNs (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 25.0.0.0/8, 26.0.0.0/8, 100.64.0.0/10). Criar a regra exige administrador: o instalador pede a permissão do Windows na primeira instalação (atualizações não perguntam), e o app mostra "Liberar no firewall" ao criar uma sala ou compartilhar numa se a regra não existir. Os dois rodam `ScreenCord.exe --configure-firewall` elevado. Ao desinstalar, o instalador apaga as regras (com o mesmo pedido de permissão); se a pessoa recusar, dá para apagá-las à mão num terminal como administrador: `netsh advfirewall firewall delete rule name="ScreenCord - salas locais"` e o mesmo com `name="ScreenCord - descoberta de salas"`.
- **Perfil local**: `%LocalAppData%\ScreenCord\local-profile.json` guarda o id deste computador nas salas, o nome do "Usar sem conta" e se o app estava sendo usado assim (para voltar direto às salas locais ao abrir). Com conta do Discord, as salas usam o nome e a foto do Discord.
- **Cifragem**: sala e mídia vão por TLS (`wss://`), com um certificado autoassinado (ECDSA P-256) criado a cada execução do app; a chave nunca é salva. Sem autoridade que assine o certificado, quem conecta confere a impressão digital (SHA-256): o anúncio da sala leva a do anfitrião, e cada compartilhamento leva a de quem compartilha (no estado da sala, que já chega cifrado). Quem entra pela lista só aceita o anfitrião anunciado; quem entra pelo IP digitado aceita o certificado que vier (fica cifrado, sem confirmar quem responde). Um anúncio UDP pode ser falsificado por alguém na rede, então isso protege bem contra quem só escuta o tráfego e dificulta, sem impedir, quem se coloca no meio.
- **Segurança (por enquanto)**: qualquer um na mesma rede pode entrar numa sala e usar o nome que quiser. Senha de sala fica para uma segunda etapa.

## Arquitetura

```
src/ScreenCord            Aplicativo WPF
├── Models/          DTOs do Discord e modelos de exibição
├── Services/        OAuth2, API do Discord, perfil, cliente da API central (amigos, grupos), conexão SignalR e compartilhamento de tela
├── Media/           Captura de tela, H.264 (Media Foundation), áudio Opus/WASAPI e canal de mídia
├── Security/        PKCE, sessão do Discord e armazenamento DPAPI
├── Infrastructure/  Opções, DI, erros centralizados, rate limit, navegador
├── ViewModels/      Estado e comandos (MVVM)
├── Views/ Themes/   Janela e tema escuro
src/ScreenCord.Api        API central
├── Auth/            Verificação do token do Discord e autenticação por sessão
├── Data/            EF Core: Users, Friendships, Sessions, Groups, GroupMembers
├── Endpoints/       Rotas de login, busca, amizades e grupos; canal de mídia (WebSocket)
└── Realtime/        Hub SignalR de grupos, rastreador de presença e repasse da mídia
src/ScreenCord.Contracts  DTOs compartilhados entre app e API
tests/ScreenCord.Tests, tests/ScreenCord.Api.Tests
```

Como o login chega à API:

1. O app obtém o access token do Discord (PKCE, no navegador).
2. O app envia esse token a `POST /auth/discord`.
3. A API chama `GET /oauth2/@me` no Discord, que informa o usuário **e para qual aplicativo o token foi emitido**. Tokens de outro aplicativo, sem o escopo `identify` ou inválidos são recusados.
4. A API cria ou atualiza o usuário e devolve um token de sessão próprio. O token do Discord não é armazenado.
5. O app usa só o token de sessão nas demais chamadas e o mantém apenas em memória; ao reabrir, repete o passo 2 silenciosamente.

## Segurança

- A senha do Discord nunca passa pelo aplicativo nem pela API.
- Cliente público com PKCE; nenhum `client_secret` em lugar nenhum.
- `state` aleatório de 256 bits, comparado em tempo constante.
- Callback local só em `127.0.0.1`, só `GET` no caminho configurado, página estática, encerrado ao fim do login.
- Tokens do Discord persistidos somente cifrados com DPAPI. Eles são enviados apenas para `discord.com` e, no login, para a API central configurada (HTTPS obrigatório fora de localhost).
- Na API: tokens de sessão opacos de 256 bits, guardados só como hash SHA-256; verificação do aplicativo de origem do token do Discord; limite de requisições (10 logins/min por IP, 120 req/min por usuário); curingas da busca tratados como texto; índice único por par de usuários contra amizades duplicadas.
- Tokens e códigos de autorização não são gravados em log.
- Dados recebidos (do Discord e da API) são validados antes de irem para a tela ou para URLs.

## Privacidade

Qualquer usuário autenticado pode encontrar qualquer outro usuário do sistema pela busca (nome de usuário, nome de exibição, ID do Discord e avatar). Informe isso a quem for usar o sistema. A API guarda somente esses dados de perfil, as datas de criação/último login e as relações de amizade.

**Relatório de erros (ligado por padrão, como dizem os termos do instalador).** O aplicativo envia à API os avisos e erros do próprio log (mensagem, pilha da exceção, o trace da requisição que falhou e as últimas linhas do log antes deles), com a versão, o Windows, o estado do app (tempo aberto, memória, compartilhamento ativo), o hardware (placas de vídeo, telas, processador, memória) e um id aleatório da instalação (`%LOCALAPPDATA%\ScreenCord\install-id`). Não vão tokens, dados da conta, nomes de janelas nem imagens da tela. A API guarda os relatórios por até 30 dias; o painel fica nela (`/admin`).

Para desligar: botão **Configurações** (a engrenagem, no cabeçalho e na tela de login) → em **Privacidade**, desmarque **Enviar relatórios de erro**. Vale na hora (o que esperava envio é descartado) e fica salvo em `HKCU\Software\ScreenCord\Telemetry` (`0` desligado, `1` ou ausente ligado). `"Telemetry": { "Enabled": false }` no `appsettings.json` desliga de vez, acima da escolha no app.

**Atualização automática.** Ao abrir (e no "Procurar atualizações" do Sobre), o app consulta a API do GitHub (`api.github.com`) para ver se há uma versão nova em `miguelsoeth/screencord` e, se houver, baixa o instalador de lá. Vai só o pedido em si, com a versão do app; nada da conta nem do computador. O GitHub recebe o seu IP, como em qualquer acesso: veja a [política de privacidade do GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

**Discord.** O login é feito no site do Discord, no seu navegador, só com o escopo `identify`, e as fotos de perfil vêm do CDN do Discord (`cdn.discordapp.com`): veja a [política de privacidade do Discord](https://discord.com/privacy). No "Usar sem conta", o app não fala com o Discord.

**Salas locais.** O nome, a foto e a tela vão só para os computadores da sala, direto pela rede, sem passar pelo servidor.

Fora isso, o app não envia nada a outros sistemas.

## Limitações reais da API do Discord

Verificado na documentação oficial (<https://docs.discord.com/developers>) em outubro de 2026:

- **Não há endpoint HTTP oficial para listar amigos via OAuth2.** O escopo `relationships.read` pertence ao Discord Social SDK (SDK nativo voltado a jogos) e não é utilizável aqui. Por isso este sistema mantém a própria relação de amigos.
- Endpoints internos do cliente Discord, tokens de usuário, scraping e automação de interface violam os termos do Discord e **não** são usados.
- **PKCE** não aparece na página principal de OAuth2; o modo de cliente público está descrito na documentação do Social SDK (opção *Public Client*). Sem ela, a troca do código falha com `invalid_client`.
- A renovação por refresh token sem `client_secret` depende do mesmo modo. Se o Discord recusar, a sessão local é apagada e o app volta para "Conectar com Discord".
- Avatares animados são exibidos como imagem estática (PNG).

## Licença

O ScreenCord é software livre sob a [GNU General Public License v3.0](LICENSE): dá para usar, estudar, modificar e redistribuir, desde que as versões modificadas distribuídas também fiquem sob a GPL-3.0, com o código-fonte. As licenças dos componentes de terceiros estão em [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt); o instalador leva os dois arquivos.

O nome **ScreenCord**, o logo e os mascotes não fazem parte da licença: um fork precisa de outro nome e outra identidade visual.

Para relatar uma vulnerabilidade, veja [SECURITY.md](SECURITY.md).
