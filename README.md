<p align="center"><img src="src/PoeFree.App/Assets/poefree.png" width="96" alt="PoeFree"></p>

<h1 align="center">PoeFree</h1>

<p align="center">
Patcher <b>gratuito e de código aberto</b> para desempenho e visibilidade no <b>Path of Exile 1 e 2</b>.<br>
Sem conta, sem chave de ativação, sem limite diário, sem servidor, sem telemetria.
</p>

> 🇧🇷 Português abaixo · 🇺🇸 [English](#english)

---

## O que é

O PoeFree modifica os arquivos do jogo no seu PC para remover efeitos pesados ou que poluem a tela: partículas, névoa, clima, sombras, bloom, efeitos de MTX, cadáveres e mais. Ele também adiciona recursos de visibilidade: zoom de câmera, mapa revelado, minimapa com alto contraste, cores nos mods perigosos de mapa e ícones de minimapa para monstros por raridade ou para qualquer objeto.

É uma reimplementação aberta (licença AGPL-3.0) das funções de ferramentas como o PoeSmoother. Para saber como ela foi feita e o que cada opção altera, veja [docs/COMO-FUNCIONA.md](docs/COMO-FUNCIONA.md).

> ⚠️ **Aviso:** modificar os arquivos do jogo vai contra os Termos de Uso da Grinding Gear Games. Ferramentas desse tipo são usadas há anos, mas **use por sua conta e risco**.

## Funções

| Aba | O que faz |
|---|---|
| **Opções** | 28 opções em 7 categorias (Desempenho, Ambiente, Efeitos, Som, Câmera, Minimapa, Interface), com predefinições *Desempenho*, *FPS máximo* e *Visibilidade*. |
| **Skills** | Lista todas as pastas `metadata/effects/spells/*` do jogo. Você pode **remover os efeitos** de cada skill ou **protegê-la** de todas as opções. |
| **Cores de Mods** | Pesquisa nas descrições de stats do jogo, escolhe uma cor e colore todas as traduções da linha. Traz uma lista sugerida (reflect, no regen etc.). Importa e exporta JSON no formato `Line/Red/Green/Blue` usado por outras ferramentas. |
| **Minimapa** | Ícones por raridade de monstro (normal, mágico, raro, único) e ícones personalizados para qualquer objeto `.ot` (baús, santuários, NPCs...). Mostra a lista de ícones lida do próprio jogo. |
| **Lista de Ignorados** | Caminhos que nenhuma opção pode modificar, um por linha. Linhas com `#` ficam desativadas. Aceita `*`. |
| **Explorador** | Pesquisa entre os mais de 1,2 milhão de arquivos do jogo, mostra o conteúdo, extrai um arquivo ou pasta e substitui arquivos. |
| **Diagnóstico** | Testa todas as opções nos arquivos atuais do jogo, sem gravar nada, e marca as que pararam de funcionar. Roda sozinho quando o jogo é atualizado. |
| **Log** | Mostra o que foi feito. Também fica salvo em `%APPDATA%\PoeFree\logs`. |

Outras funções:

- **Restaurar original**: volta ao arquivo `_.index.bin` original, usando o backup local ou o índice oficial baixado do CDN da GGG. Também apaga os bundles criados pelos mods, inclusive os do PoeSmoother (`PsCoreLib_*`).
- **Restaurar antes de aplicar**: cada aplicação parte dos arquivos originais, então desmarcar uma opção realmente desfaz a mudança.
- **Perfis**: salve e carregue suas configurações em JSON.
- **Verificar arquivos do jogo**: abre o PackCheck (standalone) ou a verificação da Steam.
- Detecta instalações **Standalone**, **Steam** e **Epic** do PoE1 e do PoE2.
- **Avisos de compatibilidade**: quando o jogo atualiza, o PoeFree verifica sozinho as opções. Se alguma não encontrar mais os arquivos ou não conseguir alterá-los, aparece um aviso no topo, na opção e depois de aplicar.
- **Aviso de nova versão**: ao iniciar, consulta os releases do GitHub (pode desligar na aba Sobre). É uma consulta anônima à API pública; nenhum dado seu é enviado.
- Interface em **português e inglês**.
- **Linha de comando** (`poefree-cli.exe`) para automação.

## Como usar

1. Baixe a release (ou compile, veja abaixo) e abra o `PoeFree.exe`.
2. Na primeira vez, clique em **Baixar oo2core**. O Oodle é o descompressor proprietário usado pelo jogo; o PoeFree não o redistribui e o baixa do release oficial da [LibGGPK3](https://github.com/aianlinb/LibGGPK3). Ele também procura cópias `oo2core_*_win64.dll` instaladas por outros jogos, e você pode escolher o arquivo manualmente.
3. Escolha o jogo, que é detectado automaticamente, ou clique em **Procurar...**:
   - Standalone: `...\Grinding Gear Games\Path of Exile\Content.ggpk`
   - Steam/Epic: `...\steamapps\common\Path of Exile\Bundles2\_.index.bin`
4. Marque as opções, **feche o jogo** e clique em **Aplicar**. Leva alguns segundos.
5. Para desfazer, clique em **Restaurar original**. Quando o jogo atualiza, os arquivos voltam ao original automaticamente; basta aplicar de novo.

> Se a instalação está em `C:\Program Files (x86)` e aparecer erro de permissão, execute o PoeFree como administrador.

### Linha de comando

```text
poefree-cli detect
poefree-cli patches
poefree-cli apply "C:\...\Content.ggpk" --preset performance --enable camera.zoom --zoom 2.0
poefree-cli apply "D:\Steam\steamapps\common\Path of Exile\Bundles2\_.index.bin" --profile meu-perfil.json
poefree-cli apply <arquivo> --preset maximum --dry-run --dump C:\saida   # não altera nada, só mostra/extrai o resultado
poefree-cli restore <arquivo>
poefree-cli check <arquivo>      # testa todas as opções (só leitura); código de saída 3 se alguma quebrou
poefree-cli update               # procura versão nova no GitHub
poefree-cli ls <arquivo> metadata/environmentsettings/
poefree-cli cat <arquivo> metadata/characters/character.ot
poefree-cli extract <arquivo> shaders C:\shaders
poefree-cli icons <arquivo>
poefree-cli oodle download
```

## Compilar

Requer o [.NET SDK 10](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test                      # testes unitários
.\publish.ps1                    # gera publish\win-x64\PoeFree.exe e poefree-cli.exe (arquivo único)
```

Testes de integração (opcionais): aponte `POEFREE_SANDBOX` para uma pasta no formato Steam (`Bundles2\_.index.bin` + bundles) e `POEFREE_OODLE` para o `oo2core.dll`. Os testes trabalham em cópias temporárias e montam um `Content.ggpk` sintético para testar o formato standalone.

## Estrutura

```text
src/PoeFree.Core   biblioteca: acesso ao GGPK/bundles, backup/restauração, motor de patches e todas as opções
src/PoeFree.App    interface gráfica (Avalonia)
src/PoeFree.Cli    linha de comando
tests/             testes (xUnit)
docs/              documentação técnica
```

Para criar uma opção nova, adicione uma classe que herda de `Patch` (pastas, extensões e uma função `Transform(texto) -> texto`) e registre em `PatchRegistry`. Exemplos em `src/PoeFree.Core/Patching/Patches`.

## Quando o jogo atualiza

1. O launcher da GGG volta os arquivos ao original: abra o PoeFree e clique em **Aplicar** de novo.
2. O PoeFree roda o **Diagnóstico** sozinho. Se alguma opção ficar "Quebrada", ela precisa de ajuste no código, normalmente uma pasta ou nome de chave em `src/PoeFree.Core/Patching/Patches`.
3. Para corrigir: rode `poefree-cli check <arquivo>`, ajuste a classe da opção, rode `dotnet test` e publique uma tag `vX.Y.Z`. O GitHub Actions gera o release, e quem usa recebe o aviso de nova versão.

O repositório usado para os avisos é `irandsj/PoeFree` (constante `UpdateChecker.DefaultRepository`; forks podem mudar `updateRepository` no `settings.json`).

## Licença e créditos

- PoeFree: **GNU AGPL-3.0-or-later** (veja [LICENSE](LICENSE)).
- [LibGGPK3](https://github.com/aianlinb/LibGGPK3), de aianlinb (AGPL-3.0): leitura e escrita do GGPK e dos bundles.
- Ideias de patches documentadas publicamente pelo [SneedSmoother](https://github.com/glutzer/SneedSmoother) (MIT).
- O Oodle (oo2core) é propriedade da Epic Games/RAD e **não** faz parte deste repositório.
- Path of Exile é marca da Grinding Gear Games. Este projeto não é afiliado à GGG.

---

## English

**PoeFree** is a free, open source (AGPL-3.0) patcher for **Path of Exile 1 and 2** that removes heavy or cluttering visual effects and adds visibility features. It is a clean-room reimplementation of what tools like PoeSmoother do, with no account, activation key, daily limits, servers or telemetry.

**Features:**

- 28 options: particles, fog, weather, post-processing, shadows, lights, bloom, MTX effects, Delirium, shatter/gore, corpses, Heist fog, burning ground, silent monsters/skills/pets, camera zoom, map reveal, high-contrast minimap, colour mods, custom minimap icons and rarity icons.
- Per-skill effect removal and protection.
- Ignore list.
- File explorer: search, view, extract and replace files.
- Profiles.
- Restore through a local backup or the official CDN index.
- Auto-detection of Standalone, Steam and Epic installs.
- English/Portuguese UI.
- Command line tool.

**Usage:**

1. Start `PoeFree.exe` and click **Download oo2core** (Oodle is proprietary and is fetched from the LibGGPK3 release; you can also pick a local copy).
2. Select the game.
3. Choose the options, close the game and click **Apply**.

Use **Restore original** to undo. Game updates also restore the files.

**How it works:** see [docs/COMO-FUNCIONA.md](docs/COMO-FUNCIONA.md) (technical notes, in Portuguese, with code references). In short: modified files are appended to new bundles (`Bundles2/LibGGPK3/*.bundle.bin`) and the index is redirected to them. The original bundles are never touched, so restoring only needs the original `_.index.bin`.

**Build:** `dotnet build`, `dotnet test`, `./publish.ps1` (.NET 10 SDK).

> Modifying game files is against Grinding Gear Games' Terms of Use. Use at your own risk.
