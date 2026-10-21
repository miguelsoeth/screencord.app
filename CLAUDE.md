# ScreenCord (app desktop)

Aplicativo desktop do ScreenCord: WPF (.NET 10, Windows x64). A API central fica no repositório `miguelsoeth/screencord.api`. O README tem a documentação funcional (recursos, configuração do Discord, execução local).

## Estrutura

- `src/ScreenCord`: o aplicativo.
- `src/ScreenCord.Contracts`: DTOs compartilhados com a API. **Existe uma cópia idêntica no repo da API**: ao mudar um DTO, atualize os dois repositórios e trate como mudança de contrato (ver versionamento).
- `tests/ScreenCord.Tests`: testes (Discord e API simulados; os de mídia usam o Media Foundation de verdade).
- `installer/ScreenCord.Installer`: instalador MSI (WiX), fora do `ScreenCord.sln`.
- `screencord.ps1`: painel de desenvolvimento (sobe API e app locais).
- `.github/workflows/`: `ci.yml`, `release.yml`.

## Comandos

```bash
dotnet build ScreenCord.sln
dotnet test ScreenCord.sln              # sempre rode antes de commitar
dotnet run --project src/ScreenCord     # Development: API local (http://localhost:5080)
```

## Fluxo de git

- Trunk-based: `main` sempre pronta para release.
- Trabalhe em branches curtas (`feat/...`, `fix/...`, `chore/...`) e integre na `main` por PR com squash merge. O `ci.yml` roda os testes (em `windows-latest`) em todo PR e push na `main`.
- A `main` é protegida pelo ruleset "main protegida" (Settings → Rules): **push direto é recusado** (inclusive para o dono do repo), só entra por PR com squash merge, o check `test` do CI precisa passar, e não pode haver force push nem exclusão da branch. Não é exigida aprovação de revisor. Fluxo: `git switch -c fix/x` → commit → `git push -u origin fix/x` → `gh pr create --fill` → `gh pr merge --squash --delete-branch` depois do CI verde. Se renomear o job `test` do `ci.yml`, atualize o ruleset, senão nenhum PR consegue ser mesclado.
- Mensagens no padrão Conventional Commits, em português: `feat: convites para grupos`, `fix: token expirado não renovava`, `chore: ...`, `docs: ...`, `refactor: ...`, `test: ...`. Mudança que exige uma versão nova da API: `feat!:` e explique no corpo.
- Nunca commite segredos. `appsettings.Local.json` é configuração local e não vai para a publicação.

## Versionamento (SemVer por tag)

- A versão vem da tag git `vMAJOR.MINOR.PATCH`. Não edite a versão no `.csproj` (lá fica `0.0.0-dev`, usada só no desenvolvimento). O CI passa `-p:Version=` com a versão da tag; ela aparece nas propriedades do exe (Detalhes → Versão do produto).
- **MAJOR**: mudança incompatível para quem usa o app (ex.: passa a exigir uma API nova que quebra versões antigas). Enquanto estiver em `0.x`, quebras sobem o MINOR.
- **MINOR**: funcionalidade nova.
- **PATCH**: correção.
- Se a versão nova do app depende de algo novo na API, lance a API primeiro.

## Compatibilidade com a API e atualização automática

- Toda chamada à API (HTTP, hub SignalR e WebSocket de mídia) leva o cabeçalho `X-ScreenCord-Version` com a versão do build (`AppVersion.Current`; `ClientVersionContract` nos Contracts). A API recusa versões abaixo da sua `Clients:MinVersion` com **426** e código `client_outdated`. Builds de desenvolvimento (`0.0.0-dev`) são sempre aceitos.
- **Atualização automática** (`AppUpdater`): ao abrir, o app consulta `GET https://api.github.com/repos/miguelsoeth/screencord/releases/latest` (até 5 s; sem rede segue normalmente). Se a release for mais nova, baixa o `ScreenCord-Setup-<versão>-x64.msi` mostrando o progresso, fecha, um PowerShell oculto roda `msiexec /i ... /passive` e reabre o app. Só aceita o MSI com esse nome, do próprio repo, por HTTPS. Build de desenvolvimento nunca se atualiza. Se o msiexec falhar, o app reabre com `--UpdateFailed=<versão>` e não tenta essa versão de novo na mesma execução; o log do msiexec fica em `%LOCALAPPDATA%\ScreenCord\logs`.
- A 0.2.0 e a 0.3.0 têm o script de instalação quebrado (msiexec recebia o caminho errado e o app reabria em loop) e só aceitam o nome antigo `ScreenCord-Setup-<versão>-win64.msi`. **Nunca volte a publicar o MSI com esse nome**: é ele que as faz entrar em loop. Quem está nelas instala a versão nova à mão uma vez.
- **Relatório de erros** (`ErrorReporter`): os logs Warning ou acima vão em lotes, a cada 30 s, para `POST /telemetry/errors` da API (painel em `/admin` na API), com a pilha e o trace W3C. O `CentralApiClient` manda `traceparent` em cada chamada e guarda o trace na `DiscordException` (`TraceId`); um log com o campo `{TraceId}` também o leva. Cada entrada leva também o estado do app (`ErrorContext`: tempo aberto, memória, compartilhamento ativo com encoder e resolução, transmissões assistidas) e as últimas 30 linhas do log de Information para cima (o rastro). Cada relatório leva o computador (`DeviceInfo`: placas de vídeo, driver, telas, processador, memória). Para algo aparecer no painel junto com os erros, ponha em `ErrorContext.Set` (e tire ao terminar). Nada de nomes de janela nem dados de conta ali, e a pasta do usuário vira `%USERPROFILE%`. Limites por execução: 5 repetições do mesmo erro e 500 entradas. Desligado no Mockup e com `Telemetry:Enabled=false`. Logs nunca levam tokens: isso vale em dobro agora que eles saem do computador.
- Se a API responder 426 com o app aberto, ele tenta se atualizar na hora; sem versão nova publicada ainda, mostra o texto da API.
- Por isso a release pública mais recente (`--latest` no `release.yml`) é o que todos os usuários recebem: nunca publique lá uma versão que não funcione com a API em produção.
- A v0.1.0 não tem o atualizador nem manda o cabeçalho (a API trata a falta dele como 0.1.0): quem está nela precisa instalar a próxima versão à mão uma vez.

### Mudanças de contrato

1. **Compatível (o normal):** a API ganha campos opcionais, endpoints ou métodos do hub novos e continua atendendo o formato antigo. O app ignora campos que não conhece. Lance a API, depois o app; nada de versão mínima.
2. **Quebra com transição (expandir e contrair):** a API passa a aceitar o formato novo e o antigo ao mesmo tempo (se precisar, ramifique pela versão com `HttpContext.GetClientVersion()` na API). Lance a API, depois o app (que usa o formato novo). Quando os usuários já tiverem se atualizado (o app se atualiza ao abrir), suba `Clients:MinVersion` da API para a versão do app e remova o formato antigo numa release da API.
3. **Quebra sem transição possível:** numa release da API, faça a mudança e suba `Clients:MinVersion` para a versão do app que a acompanha; publique o app logo em seguida. Entre as duas releases, quem abrir o app vê o aviso de versão não aceita; depois da release do app, todos se atualizam sozinhos.

## Release

Para lançar uma versão, a partir da `main` atualizada e com os testes passando:

```bash
git tag v0.2.0
git push origin v0.2.0
```

O `release.yml` então:
1. roda os testes;
2. gera o instalador MSI (`dotnet build installer/ScreenCord.Installer -p:AppVersion=<versão>`, que publica o app como exe único autocontido com a versão da tag);
3. cria a GitHub Release com notas geradas a partir dos commits e o `ScreenCord-Setup-<versão>-x64.msi` anexado;
4. cria a mesma release (mesmas notas e MSI) no repo público `miguelsoeth/screencord`, usando o secret `PUBLIC_RELEASES_TOKEN` (fine-grained PAT com `Contents: read and write` só nesse repo).

Um MSI de versão maior substitui a instalação anterior (mesmo `UpgradeCode`), preservando a sessão do Discord.

Este repositório é privado, então o download público vem do `miguelsoeth/screencord`: ele guarda só a landing page (`index.html`, no GitHub Pages em https://miguelsoeth.github.io/screencord/) e as releases. A página lista as releases desse repo pela API do GitHub e aponta o botão para o MSI da mais recente. Não crie releases lá à mão.

Não crie tags para testar o pipeline: cada tag gera uma release visível para quem tem acesso ao repo. Para refazer uma release com falha, apague a release e a tag nos dois repos (`gh release delete v0.2.0 --cleanup-tag`, e o mesmo com `--repo miguelsoeth/screencord` se ela chegou a ser criada lá) e crie a tag de novo.

O instalador é distribuído só pelas GitHub Releases (o VPS não serve mais downloads).

## Idiomas

- O app tem português, inglês e espanhol. Todo texto da interface fica em `src/ScreenCord/Localization/Strings.cs`, com as três traduções lado a lado (`T("pt", "en", "es")`). No XAML: `{l:Tr Chave}` (ou `{l:Tr ChaveFormat, Path=Nome}` para textos com `{0}`); no C#: `Strings.Chave`. Nada de texto fixo nas telas. Logs ficam em português.
- A troca é ao vivo (botão de idioma no cabeçalho e no login): o `{l:Tr}` se atualiza sozinho e todo `ObservableObject` avisa a interface. Texto montado em code-behind precisa ouvir `Loc.LanguageChanged`.
- A escolha fica em `HKCU\Software\ScreenCord\Language` (`pt`, `en`, `es`). O instalador mostra a tela "Idioma" na primeira instalação, grava o mesmo valor e o lê de volta nas atualizações (inclusive a automática). Sem valor, vale o idioma do Windows (senão, inglês).
- Os testes rodam em português (o padrão do `Loc`). Testes que trocam o idioma ficam na coleção `LanguageCollection`, que não roda em paralelo; o `LocalizationTests` confere que toda chave do XAML existe e que as três línguas têm os mesmos `{0}`.

## Salas locais

- Ficam em `src/ScreenCord/Local`: funcionam sem a API (e sem conta do Discord, no "Usar sem conta"). A tela de grupos usa o `RoutedGroupsRealtime`, que manda entrar, sair e anunciar compartilhamento para a API ou para a sala local conforme o grupo; os amigos usam só a conexão da API (`ApiRealtime`).
- Portas fixas TCP 47830 e UDP 47831 (`LocalNetwork`): a regra do firewall depende delas. Não mude sem atualizar a regra (`FirewallRule`) e o README.
- O servidor das salas é um `TcpListener` com WebSocket próprio (`LocalServer`), de propósito: o `HttpListener` exigiria administrador para escutar fora do localhost.
- O protocolo da sala tem versão (`LocalMessage.CurrentProtocol`). Mudança incompatível no formato: suba a versão (salas de versões diferentes recusam a entrada com aviso para atualizar).
- Qualidade: pelo servidor vale `StreamLimits.Relay` (a API informa em `GET /limits`); nas salas locais, `StreamLimits.Direct`.

## Cuidados

- O app recusa `http` fora de localhost para a API; a URL de produção é `https://154.16.50.20`. As salas locais usam `wss://` direto entre os computadores da rede, com certificado autoassinado por execução (`LocalCertificate`) conferido pela impressão digital que vem no anúncio da sala e em cada compartilhamento.
- O instalador é por usuário (sem administrador). A regra do firewall é a única coisa que pede administrador: o próprio exe com `--configure-firewall`, chamado pelo instalador na primeira instalação e pelo botão "Liberar no firewall".
- O `UpgradeCode` em `installer/ScreenCord.Installer/Package.wxs` nunca muda.
