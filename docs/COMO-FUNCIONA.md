# Como funciona

Este documento explica:

1. o que foi descoberto analisando o PoeSmoother 2.08 (a ferramenta que inspirou o PoeFree);
2. como os arquivos do Path of Exile são organizados;
3. como o PoeFree implementa cada função.

## 1. Análise do PoeSmoother 2.08

A análise usou só técnicas não invasivas: leitura de strings e recursos do executável e comparação dos arquivos do jogo antes e depois do patch. Nenhum código do PoeSmoother foi copiado.

- **Tecnologia:** .NET compilado com **NativeAOT** (não dá para descompilar em C#) e interface em **Avalonia**, com SkiaSharp e HarfBuzz. Usa o Oodle (`oo2core`) ligado estaticamente.
- **Modelo de negócio:** ferramenta paga com DRM.
  - Tela de ativação com chave, presa ao hardware ("Hardware changed. This key is registered to another machine").
  - Limite diário de aplicações ("Daily apply limit reached").
  - Servidores próprios: `psone-{us,br,fra,hk,bkk}.poesmoother.eu` e `tpsone-api.poesmoother.eu`.
- **Onde ficam as regras:** a maior parte das regras de modificação (o "payload" de cada opção) **não está no executável**. Elas são baixadas do servidor depois da autenticação (`preflight/auth` → `preflight/manifest` → `payload chunk`, com verificação de hash). Só algumas funções são geradas localmente: feras, ícones de minimapa, painel do Simulacrum, cores de mods, câmera e lista de ignorados.
- **Como escreve:** grava os arquivos modificados em bundles novos, `Bundles2/PsCoreLib_N.bundle.bin`, e reaponta o `_.index.bin` para eles.
- **Como restaura:** baixa o `_.index.bin` oficial, pela API própria ou pelo CDN de patches da GGG.

Para descobrir o que cada opção realmente faz, os arquivos que estavam em `PsCoreLib_*` num `Content.ggpk` já patcheado (3.084 arquivos) foram comparados com os originais do CDN oficial (`patch.poecdn.com`). Resultado:

| Tipo | Qtd. | Mudança observada |
|---|---:|---|
| `.pet` (partículas) | 1.221 | arquivo inteiro trocado por `0` (zero emissores) |
| `.env` (ambiente) | 1.346 | chaves JSON renomeadas: `fog`→`xog`, `area`→`xrea`, `post_processing`→`xost_processing`, `effect_spawner`→`xffect_spawner`, `screenspace_fog`→`xcreenspace_fog` |
| `.ao` (objetos animados) | 243 | blocos esvaziados: `ParticleEffects {}`, `Lights {}`, `SkinMesh {}`, `AnimatedRender {}`, `FixedMesh {}`, `WindEvents {}`, `SoundEvents {}` |
| `.ot` (objetos) | 90 | zoom no `Positioned` do personagem: `CreateCameraZoomNode(5000.0, 5000.0, Z)`; remoção de chamadas de câmera em chefes/objetos; cadáveres (`DisableRendering()` em `on_death`); `disable_rendering = true` em shatter/gore; scripts de marcadores de minimapa |
| novos `.ot` | 171 | objetos marcadores (`extends MissionMarker` + `MinimapIcon { icon = N }`) e rastreadores de raridade (`AddNamedTask( EnemiesNearbyTracker, ..., máscara, ... )`, com máscara 1 = normal, 2 = mágico, 4 = raro, 8 = único) |
| `.hlsl` | 5 | `bloomgather` retorna 0; névoa do Heist transparente; `terrainburn` vazio; minimapa revelado (`res_color = max(res_color, 0.18f)`); contraste do minimapa |
| `stat_descriptions` | 5 | textos envolvidos como `"<cor>{{texto}}"` em todas as traduções |
| `uisettings.xml` | 1 | cores nomeadas adicionadas em `<Props id="MarkupColours">` |

O PoeFree aplicado com as mesmas opções gerou **resultado idêntico** em 1.185 `.pet`, 623 `.env`, 80 `.ao` e 68 `.ot` (as diferenças restantes eram só de formatação ou de opções diferentes).

## 2. Arquivos do jogo

- **Standalone:** `Content.ggpk` é um "sistema de arquivos" com registros `GGPK`, `PDIR` (pastas), `FILE` e `FREE`. Dentro dele fica a pasta `Bundles2`.
- **Steam/Epic:** a pasta `Bundles2` fica solta no disco.
- `Bundles2/_.index.bin` é o índice: a lista de bundles e, para cada arquivo (identificado pelo hash do caminho), o bundle, o offset e o tamanho. Os bundles (`*.bundle.bin`) são blocos comprimidos com **Oodle**.
- Os scripts do jogo (`.ot`, `.otc`, `.ao`, `.env`, `.txt`...) são em geral **UTF-16LE com BOM**; os shaders (`.hlsl`) são ASCII.
- O jogo encontra os arquivos pelo **hash do caminho**. Por isso dá para adicionar arquivos novos (os marcadores, por exemplo) mesmo sem registrá-los nas tabelas de pastas do índice.

O acesso a tudo isso é feito pela [LibGGPK3](https://github.com/aianlinb/LibGGPK3).

## 3. Arquitetura do PoeFree

```text
GameArchive (Core/Archive)   abre GGPK ou _.index.bin, lê/escreve/adiciona arquivos, lê o índice bruto
OodleResolver                encontra/baixa o oo2core e confere se exporta as funções necessárias
RestoreService               backup do índice original, restauração (backup local -> índice oficial do CDN)
PatchEngine                  seleciona arquivos -> lê por bundle -> aplica transformações -> gera arquivos -> grava
Patch (Core/Patching)        classe base de cada opção (pastas, extensões, Transform, Generate)
ScriptBlocks / ObjectScript  parser dos blocos { } dos scripts (ignora chaves dentro de strings e comentários)
```

### Fluxo de "Aplicar"

1. Se o índice tem bundles de mod (`LibGGPK3/`, `PsCoreLib_*`), restaura primeiro.
2. Garante um backup do `_.index.bin` original em `%APPDATA%\PoeFree\backups\<id>`.
3. Monta a lista de arquivos de todas as opções ativas, menos os protegidos pela lista de ignorados.
4. Lê os arquivos agrupados por bundle (rápido) e passa cada um pelas opções, em ordem.
5. Roda os geradores: cores de mods, marcadores e rastreadores de minimapa.
6. Grava tudo num bundle novo, `Bundles2/LibGGPK3/N.bundle.bin`, e salva o índice.
7. Guarda o hash do índice gravado. Se outro programa mexer no índice depois, o backup não é usado e a restauração baixa o índice oficial.
8. Guarda também a "impressão digital" das opções usadas (`PatchSettings.Fingerprint`: opções, valores, lista de ignorados e versão do PoeFree). Com ela dá para saber se os arquivos já estão com exatamente estas opções (`RestoreService.IsUpToDate`).

### Vigia de atualizações do jogo

O PoeFree nunca abre, fecha nem mexe no processo do jogo. Tudo sai dos arquivos e do log que o próprio jogo escreve.

- `GameProbe` (Core/Game) diz se o jogo está rodando (processos do jogo cuja pasta é a da instalação) e qual versão está instalada. A versão vem do log do jogo (`logs/LatestClient.txt`, ou o fim do `Client.txt`): a linha `Web root: https://patch.poecdn.com/<versão>/` de uma sessão que chegou a `Finished checking files`.
- `GameUpdateTracker` (Core/Diagnostics) guarda em `%APPDATA%\PoeFree\updates\<id>` o último índice **original** visto (`baseline.index.bin` + `baseline.json`). Quando os arquivos estão com o patch do PoeFree, o original é o backup feito antes de aplicar. Se outro programa mexeu no índice, não há com o que comparar e nada é relatado.
- A janela chama `Poll` a cada 5 s. Ele só lê o índice de novo se o arquivo mudou (tamanho/data), e a cada 12 voltas de qualquer jeito. Um índice original diferente do guardado só conta como atualização se aparecer igual em duas leituras seguidas.
- Na atualização: `IndexDiff.Compare` compara os dois índices (arquivos adicionados, removidos, com outro tamanho ou só movidos; nenhum arquivo é lido), o `CompatibilityCheck` roda nos arquivos novos e `GameUpdateReport` junta tudo com o diagnóstico anterior num `.md`. O botão **Copiar pedido de análise** põe na área de transferência um pedido pronto e o relatório com as listas cortadas em 40 linhas (`GameUpdateReport.Shorten`).
- Se a atualização tirou as opções, elas são reaplicadas pelo mesmo `PatchEngine` do botão Aplicar quando o jogo está fechado há duas voltas e o arquivo está 20 s sem mudar. Enquanto grava, a janela não deixa fechar. Se falhar, espera 10 min antes de tentar de novo.
- Em modo leitura, o índice do formato Steam/Epic é lido para a memória e o arquivo é fechado: o PoeFree pode ficar aberto sem impedir a Steam ou a Epic de trocar o `_.index.bin`. O `Content.ggpk` fica aberto só para leitura, com compartilhamento total, porque o jogo standalone grava dentro dele.
- `PoeFree.exe --tray` (usado pelo "Iniciar com o Windows") começa sem janela, só com o ícone na área de notificação. Um mutex por pasta de dados garante uma cópia só; abrir de novo avisa a que já roda para mostrar a janela.

### Fluxo de "Restaurar"

1. Se o índice atual é o que o PoeFree gravou, volta o backup.
2. Senão, baixa o `_.index.bin` oficial pelo `PatchClient` (servidor de patches da GGG → URL do CDN). Antes de gravar, confere se **todos os bundles referenciados existem** na instalação: se a versão do jogo for diferente, nada é gravado e aparece um aviso pedindo para atualizar o jogo.
3. Apaga os bundles de mod que sobraram.

Os bundles originais **nunca** são modificados.

### Opções

| Id | Arquivos | Técnica |
|---|---|---|
| `particles.environment` | `metadata/particles/enviro_effects/**/*.pet,.trl` | `.pet` → `0` |
| `particles.all` | `metadata/**/*.pet,.trl` | `.pet` → `0`; `.trl`: contagem inicial → `0` |
| `env.fog`, `env.weather`, `env.postprocessing`, `env.effectspawner`, `env.lights` | `metadata/environmentsettings/*.env` | renomeia as chaves JSON |
| `env.shadows` | idem | `"shadows_enabled": false` |
| `effects.weather`, `effects.environment`, `league.delirium` | `metadata/effects/weather_attachments`, `environment`, `league_affliction`... | `.pet` → 0 e blocos de render vazios nos `.ao/.aoc`; doodads do Delirium sem `AddAttached/EnableRendering` |
| `mtx.soft` / `mtx.full` | `metadata/effects/microtransactions` | partículas + luzes / também malhas |
| `skills.disable` | pastas escolhidas na aba Skills | partículas + luzes |
| `effects.shatter` | `effects/spells/shatter`, `gore_explode` | `Render { disable_rendering = true }` |
| `monsters.corpses` | `metadata/monsters/monster.ot` | `RemoveEffects(); DisableRendering();` na morte, `EnableRendering()` ao reviver |
| `shader.bloom`, `shader.heistfog`, `shader.terrainburn` | `shaders/*.hlsl` | corpo da função substituído |
| `map.reveal`, `map.contrast` | `shaders/minimap_*.hlsl` | ajuste de cor/visibilidade |
| `camera.zoom` | `metadata/characters/*.ot` + scripts com chamadas de câmera | adiciona o nó de zoom do jogador; remove `CreateCameraZoomNode`/`SetCustomCameraSpeed`/... (a linha inteira só sai se as chaves e parênteses ficarem balanceados) |
| `sound.monsters`, `sound.skills`, `sound.pets` | `.ao/.aoc` | `SoundEvents {}` |
| `colormods` | `metadata/statdescriptions/*.txt` + `metadata/ui/uisettings.xml` | cor nomeada `poefreeRRGGBB` + `"<poefreeRRGGBB>{{texto}}"` |
| `minimap.markers` | `.ot` escolhido + novo `poefreemarker_<id>.ot` | o objeto cria um marcador com ícone que o segue (some quando o objeto some ou ao atingir um estado, ex.: `activated`) |
| `minimap.rarity` | `character.ot` + novos rastreadores/marcadores | rastreadores `EnemiesNearbyTracker` por raridade seguem o jogador e marcam os inimigos próximos |

## 4. Compatibilidade com atualizações do jogo

- `CompatibilityCheck` (Core/Diagnostics) roda todas as opções em modo leitura, com dados de exemplo: uma skill, as cores sugeridas, um baú e todas as raridades.
- Para cada opção, mede quantos arquivos ela encontra e quantos altera **sozinha**, a partir do texto original. Assim uma opção não esconde a falha de outra.
- O resultado é classificado como `Ok`, `Warning` (com erros), `Broken` ou `Skipped` (não existe nesta versão do jogo). O motivo pode ser `NoTargets` (pasta/arquivo sumiu) ou `NoChanges` (formato mudou).
- O relatório fica salvo em `%APPDATA%\PoeFree\compatibility` junto com o hash do `_.index.bin`. Quando o hash muda (o jogo atualizou) ou o PoeFree muda de versão, a verificação roda de novo ao abrir o jogo.
- Depois de cada **Aplicar**, as opções escolhidas também são avaliadas, e as que não alteraram nada aparecem num aviso.
- `UpdateChecker` consulta `api.github.com/repos/<repo>/releases/latest` e compara a tag (`v1.2.3`) com a versão do programa.

### Atualização do próprio PoeFree

`SelfUpdater` (Core/Diagnostics), usado pelo botão **Atualizar agora** e por `poefree-cli update --install`:

1. Pega do release o pacote da plataforma (`PoeFree-win-x64.zip`) e o `digest` SHA-256 que o GitHub publica para ele.
2. Baixa (só por HTTPS) para uma pasta temporária e confere o SHA-256. Se não bater, nada é instalado.
3. Confere se o zip tem mesmo um `PoeFree.exe` de tamanho plausível. Só os arquivos da raiz do pacote são instalados, sempre dentro da pasta do programa.
4. Um executável em uso não pode ser sobrescrito no Windows, mas pode ser renomeado: os arquivos atuais viram `*.old`, os novos entram no lugar e o programa reinicia. Se algo falhar no meio, os `*.old` voltam ao lugar.
5. Na próxima abertura, os `*.old` são apagados.

Só funciona na versão publicada (arquivo único). Rodando pelo `dotnet run`, o botão apenas abre a página do release.

Exemplo real: a primeira verificação no PoE2 mostrou que as descrições de stats ficam em `data/statdescriptions/*.csd`, e não em `metadata/statdescriptions/*.txt` como no PoE1. As cores de mods foram ajustadas para os dois formatos.

## 5. O que não foi reproduzido

- **DRM, ativação, limites e servidores**: de propósito.
- Regras que só existem no servidor do PoeSmoother e não apareceram no arquivo analisado ("better lights", "remove darkness", "clean terrain", "remove players/monsters", "zero materials", configuração de feras, painel do Simulacrum). Elas podem ser adicionadas como novas classes `Patch` quando alguém documentar o efeito.
- Ícones das skills: são arte da GGG. A aba Skills usa os nomes das pastas do próprio jogo.
- Importação de temas do VS Code e o editor de texto com realce de sintaxe: o Explorador mostra o texto puro.
