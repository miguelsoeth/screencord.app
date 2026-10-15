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

## Release

Para lançar uma versão, a partir da `main` atualizada e com os testes passando:

```bash
git tag v0.2.0
git push origin v0.2.0
```

O `release.yml` então:
1. roda os testes;
2. gera o instalador MSI (`dotnet build installer/ScreenCord.Installer -p:AppVersion=<versão>`, que publica o app como exe único autocontido com a versão da tag);
3. cria a GitHub Release com notas geradas a partir dos commits e o `ScreenCord-Setup-<versão>-win64.msi` anexado para download.

Um MSI de versão maior substitui a instalação anterior (mesmo `UpgradeCode`), preservando a sessão do Discord.

O repositório é privado: o download da release exige acesso ao repo (`gh release download v0.2.0 --repo miguelsoeth/screencord.app`).

Não crie tags para testar o pipeline: cada tag gera uma release visível para quem tem acesso ao repo. Para refazer uma release com falha, apague a release e a tag (`gh release delete v0.2.0 --cleanup-tag`) e crie a tag de novo.

O link público em `https://154.16.50.20/download/ScreenCord-Setup.msi` (VPS) não é atualizado pelo pipeline: para trocá-lo, baixe o MSI da release e copie (`scp ScreenCord-Setup-0.2.0-win64.msi deploy@154.16.50.20:/srv/screencord/downloads/ScreenCord-Setup.msi`).

## Cuidados

- O app recusa `http` fora de localhost; a URL de produção da API é `https://154.16.50.20`.
- O `UpgradeCode` em `installer/ScreenCord.Installer/Package.wxs` nunca muda.
