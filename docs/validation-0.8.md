# Validação do opTImus 0.8

Data do cliente: **28/10/2026**. Windows x64, SDK isolado .NET 10.0.403, runtime 10.0.14. Três agentes entregaram as frentes de Registro, SystemRestore e BitLocker; o coordenador integrou e verificou o conjunto. Trabalho local, sem commit/push, CI remoto, instalador assinado ou publicação pública.

## Compilação e testes

- Baseline 0.7: hashes dos **155 arquivos** conferidos antes das alterações. Integração de 33 arquivos, sem escritores sobrepostos.
- `dotnet restore Optimus.slnx --locked-mode`: aprovado antes da compilação e depois do publish, sem novos pacotes.
- Build Debug aprovado. Testes focados: **155 aprovados**, 1,1752 s.
- `dotnet format Optimus.slnx --no-restore --verify-no-changes`: aprovado após a formatação e novamente no estado final.
- Build Release final: **zero avisos e zero erros**, 7,72 s.
- Suíte Release final: **741 aprovados, zero falhas e zero ignorados**, 22,8083 s.
- Publish Windows x64 self-contained aprovado. Executável **0.8.0.0**, runtime incluído 10.0.14.

Comandos principais executados com o SDK e caches de `.tools`: build da solução com `--no-restore --disable-build-servers`; testes de `tests/Optimus.Tests/Optimus.Tests.csproj`; suíte final com `--configuration Release --no-build --no-restore`; publish de `src/Optimus.Desktop/Optimus.Desktop.csproj` com `--configuration Release --runtime win-x64 --self-contained true --no-restore --output artifacts/app`.

A primeira rodada focada teve 154 aprovações e uma falha de fixture: 64 descrições Unicode escapadas pelo serializador ultrapassavam o limite de transporte antes de alcançar o limite de projeção pretendido. A fixture passou a usar texto ASCII e a conferir seu tamanho dentro de 180.000 bytes. Os limites do produto foram mantidos. O log inicial está preservado. A execução nativa identificou também uma recusa CIM do BitLocker classificada genericamente por HRESULT; o produtor passou a considerar ErrorCategory e UnauthorizedAccessException. A suíte completa foi repetida e a recusa de permissão foi confirmada em nova execução nativa.

## Consultas reais

Contexto **sem token administrativo**. Logs e relatórios privados ficam em `.tools/verification/iteration-08`; os arquivos `*-verified.json` e `native-verified-validation.json` correspondem à fonte final. Os arquivos `*-final.json` anteriores preservam a observação anterior ao ajuste de classificação.

| Comando | Resultado da evidência | Fatos / limitações | Tempo CLI | CPU CLI | JSON |
|---|---|---:|---:|---:|---:|
| pending-restart | Complete, exit 0 | 48 / 0 | 0,6589 s | 0,296875 s | 15.913 bytes |
| restore-points | Unavailable, sessão Partial, exit 3 | 24 / 2 | 1,4043 s | 0,4375 s | 8.910 bytes |
| bitlocker-status | Unavailable, sessão Partial, exit 3 | 24 / 2 | 6,2178 s | 0,5 s | 9.366 bytes |

O Registro confirmou ausência das duas chaves selecionadas e presença de PendingFileRenameOperations, REG_MULTI_SZ de **600 bytes**, com doze segmentos originais incluindo vazios e terminadores. Isso não é contagem de operações nem uma conclusão universal sobre necessidade de reinício. Uma leitura independente posterior coincidiu nas duas presenças/ausências, nos bytes, no texto UTF-16 e nos doze segmentos, sem tocar nos arquivos mencionados pela fila. A comparação é entre observações não atômicas que coincidiram naquele momento.

SystemRestore e Win32_EncryptableVolume recusaram acesso. Ambas as evidências conservaram `PermissionDenied`, mensagem original e resposta de falha. Nenhum registro retornado foi tratado como proteção desligada ou inexistência comprovada de pontos. **A listagem real de pontos, a consulta bem-sucedida dos quatro getters e volumes criptografados ainda não foram validadas em contexto administrativo.** Fixtures cobrem esses contratos; não substituem essa prova.

Os três comandos recusaram `--execute true` antes de criar sua pasta de dados. Não houve reinício, limpeza de Registro, criação/restauração/remoção de pontos, alteração de criptografia ou coleta de material de recuperação.

## Compatibilidade do Registro e métricas

O leitor RAW compartilhado sonda tamanho e tipo antes da alocação, limita valores a 16 KiB e não repete leituras que mudam. Erro de acesso e ausência observada permanecem diferentes. O inventário de software passou a representar presença desconhecida com valor nulo. Leituras bem-sucedidas e ausências confirmadas mantêm o formato anterior; detalhes adicionais de sondagem ficam nas falhas e mudanças, evitando consumir a cobertura do orçamento de relatório.

Comparação nativa antes/depois: os **2.244 fatos** de programas e os **181 fatos** de inicialização conservaram todos os valores, rótulos, unidades e flags originais. Programas: 107 entradas examinadas, 106 retidas, estado Partial pelo orçamento preexistente. Inicialização: 22 examinadas/retidas, Complete. Essa prova cobre o recorte observado, não um inventário completo do computador.

Inventário completo antes: 1,3069 s de parede e 0,328125 s de CPU da CLI; depois: 0,6761 s e 0,34375 s. São observações únicas com caches não controlados, sem separação de cold/warm; **não estabelecem ganho de desempenho**. Pico de memória do processo raiz e CPU/memória de serviços Windows externos não foram medidos.

Na rodada final, o coletor de Registro reportou 0,032012 s. SystemRestore: Job 0,8286411 s, CPU da família 0,796875 s e pico de commit 87.359.488 bytes. BitLocker: Job 5,7255719 s, CPU da família 0,71875 s e pico de commit 92.061.696 bytes. Esses Jobs correspondem às recusas observadas; não estimam o custo de inventários administrativos. Commit não é working set e não inclui todos os serviços CIM do Windows. Consumo de tokens, relevância da seleção da IA e custos de um modelo real não foram medidos nesta iteração.

## Interface e pacote

O executável publicado abriu sem DOTNET_ROOT/DOTNET_ROOT_X64, usando runtime incluído. A conferência visual opt-in executou as três consultas pelo seletor, produziu `optimus-0.8-maintenance-final.png` e terminou com exit 0 em 8,7730 s, CPU do processo 3,5 s. O histórico foi relido pela CLI e confirmou quatro sessões: diagnóstico inicial e três incorporações explícitas. Exit 0 da conferência significa fluxo/interface salvos; as duas fontes CIM continuaram indisponíveis.

A imagem real foi inspecionada. Não houve teste manual de teclado/leitor de tela ou validação de todas as combinações visuais. A inicialização normal não solicita os novos coletores; a conferência usa `--recovery-tools-preview true` junto de `--render-preview`. Nenhum journal de integridade foi criado. SFC/DISM, modelo GGUF, restic e SMART físico não foram executados nesta iteração.

SHA-256 do executável conferido: `43AC1D6D864F7A9D238DDA762AC99724C945C55A3AD2AA362BD064FDC4387B68`.

## Limites e trabalho fora deste recorte

- Indicadores: três fontes HKLM64 fixas; valor até 16 KiB antes da alocação; amostra até 64 segmentos e 64.000 bytes conservadores, além dos bytes/texto originais retidos. Não interpreta pares de operações nem remove destinos vazios.
- SystemRestore: 64 registros mais sonda, cinco propriedades selecionadas, texto de 1.024 caracteres, payload de 180.000 bytes, resposta de 256 KiB e projeção de 128.000 bytes. CreationTime DMTF fica original; datas CLR conservam tipo e Kind, sem fuso inferido.
- BitLocker: 32 volumes mais sonda; identificadores e quatro getters fixos, resposta de 256 KiB, payload de 210.000 bytes, projeção de 128.000 bytes e texto de 2.048 caracteres. Somente ReturnValue zero permite interpretar estados. Percentual não substitui ConversionStatus. Prazo cooperativo de 90 s e processo limitado a 120 s.
- Scripts finais: RestorePoints 4.784 caracteres e BitLockerStatus 7.466; ambos com zero erros no parser PowerShell e dentro do orçamento do host. Objetos/strings CIM podem ser materializados pelo Windows antes dos limites de projeção; chamadas nativas não são preemptíveis, e o processo permanece supervisionado pelo Job.
- A enumeração preexistente de nomes de chaves/valores no inventário usa Count seguido de Get*Names. Crescimento concorrente entre essas chamadas ainda pode ultrapassar o limite antes da materialização. Não foi introduzido pela extração RAW; resolver exige outra mudança na fronteira de enumeração, fora das três consultas desta entrega.

Fonte, pacote e checks finais são registrados em `source-final-manifest.json`, `published-folder-manifest.json` e `final-verification.json`. Artefatos e dados de diagnóstico permanecem ignorados pelo Git. Consulte o [escopo 0.8](scope-0.8.md) e os [comandos locais](local-tools.md).
