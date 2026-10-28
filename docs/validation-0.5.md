# Validação do opTImus 0.5

Data: 27/10/2026. Windows x64 build 26300, SDK isolado .NET 10.0.403 e runtime 10.0.14. Três frentes de agentes entregaram serviços, histórico WUA e espaço de pasta; o coordenador integrou GUI/CLI e executou a validação conjunta. Uma revisão adicional inspecionou os fluxos compartilhados. Alterações locais, sem commit/push, CI remoto ou distribuição pública nesta rodada.

## Build, testes e revisão

- Restauração `--locked-mode` antes e depois do publish: aprovada, sem novos pacotes.
- `dotnet format --no-restore --verify-no-changes`: aprovado. Uma primeira tentativa apontou um final de linha introduzido na revisão; corrigido antes do build final.
- Build Release da solução: zero avisos e zero erros, 23,77 s.
- Testes focados iniciais: 112 aprovados. Suíte Release final: **440 aprovados, zero falhas e zero ignorados**, 28,0048 s.
- Publish Release Windows x64 self-contained: aprovado.

Os novos testes cobrem JSON malformado/ambíguo, identidades/códigos/datas preservados, limites de fonte e projeção, falhas independentes de histórico/reinício e finalização COM. As consultas de pasta usam diretórios reais para contagem, Unicode, top20, hardlinks, junctions, falhas de metadados, limites e cancelamento. A GUI cobre ação explícita, seleção capturada antes do await, serialização de ações, cancelamento e ownership das evidências. Fixtures de WUA/CIM não substituem os provedores físicos; as consultas nativas abaixo complementam essa cobertura.

A revisão identificou perda dos dados na CLI quando o histórico falhava. O fluxo compartilhado agora emite a sessão concluída em stdout antes da persistência e tenta o relatório solicitado mesmo se o SQLite falhar, mantendo saída 1 e erro explícito. Foi validado com um arquivo de banco inválido próprio: hash inalterado, sessão em stdout e relatório novo preservados. Um relatório já existente também manteve seu hash; a coleta permaneceu em stdout e o comando retornou 1. Seleção de pasta ausente foi rejeitada antes de criar armazenamento.

Logs e recibos privados ficam em `.tools/verification/iteration-05`, incluindo `format-check.log`, `restore-locked.log`, `build-release.log`, `tests-focused.log`, `tests-release.log`, `publish.log` e `restore-after-publish.log`.

## Consultas Windows reais

Executadas pela CLI Release com dados/relatórios novos. Os valores abaixo descrevem esta amostra, não um inventário completo do computador.

| Consulta | Resultado | Parede CLI | CPU CLI | Parede job | CPU família do job | Pico de commit da família |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Serviços | Partial, saída 3 | 2,515 s | 0,422 s | 1,694 s | 1,000 s | 120.078.336 bytes |
| Histórico WUA | Partial, saída 3 | 4,229 s | 0,484 s | 3,675 s | 1,641 s | 109.879.296 bytes |

Serviços: 257 registros enumerados incluindo a sonda, 256 retidos no JSON original e 21 projetados como fatos. Foram retidos 189 fatos e relatório de 159.422 bytes; três issues informam enumeração/projeção limitadas. Um verificador independente comparou os textos projetados aos respectivos campos originais: coincidiram. PID/caminho/conta reportados não estabelecem ownership de processo nem integridade do executável.

Windows Update: 351 eventos na descoberta e na confirmação, com as 128 entradas recentes retidas no JSON original e 17 projetadas em fatos. Relatório de 135.356 bytes, 248 fatos e três issues de limite. O sinal RebootRequired do WUA retornou false; isso não avalia outras causas de reinício. As datas vieram como System.DateTime/Unspecified e foram mantidas sem atribuir UTC. Quantidade estável não torna a leitura transacional.

O código consulta Win32_Service local e os métodos de histórico WUA, sem solicitar alteração de serviços, Search/scan, download, instalação ou reinício. Não houve captura independente de toda atividade dos serviços do Windows ou de rede. Histórico e sinal de reinício não comprovam sistema atualizado. Os JSONs originais retidos continuam acessíveis nos relatórios, inclusive para os registros não projetados na GUI.

Recibos: `native-final-validation.json`, `services-final.json`, `update-history-final.json` e `independent-final-validation.json`.

## Pasta: comparação independente

A primeira pasta própria de teste contém três arquivos: 8192, 17 e zero bytes, incluindo subpasta e nomes Unicode. A CLI reportou exatamente três arquivos, 8209 bytes lógicos e os três tamanhos na ordem esperada; saída 0, Complete, zero issues. Parede 0,455 s, CPU CLI 0,313 s; relatório de 14.961 bytes.

Uma segunda execução analisou a árvore de fontes preparada nesta rodada. Uma enumeração independente encontrou 32 arquivos e 283.170 bytes; o coletor retornou os mesmos totais, Complete, saída 0 e vinte maiores arquivos. Parede 0,571 s, CPU CLI 0,297 s. Recibo: `folder-space-source-sample-final-validation.json`. Essa amostra inclui o estado da árvore no momento da consulta, sem afirmar que arquivos posteriores também foram examinados.

Limites: 20.000 entradas, sonda adicional, até 32 componentes para vincular a raiz e 32 níveis de DFS, prazo cooperativo de 30 segundos, top20 com orçamento conservador de 512 KiB e até 32 detalhes de ocorrências/128 KiB. Chamadas individuais de metadados não são preemptíveis. O coletor não solicita conteúdo/hash/exclusão; os totais são tamanhos lógicos por entrada, incluindo duplicação por hardlinks. Não medem espaço recuperável, alocação física, ADS, compressão ou deduplicação.

A origem do atributo 0x40000 foi corrigida no dono da interpretação: RECALL_ON_OPEN na enumeração, sem tratá-lo como recall nos metadados de handle. A correção se apoia no contrato Microsoft; comportamento real de EA, nuvem/recall, permissões especiais, mídia removível e arquivos mudando simultaneamente continua sem homologação específica. As provas de junctions e hardlinks ocorreram nas fixtures reais criadas pela suíte; não são provas de todos os sistemas de arquivos.

## Interface e executável

`artifacts/app/opTImus.exe`, versão **0.5.0.0**, iniciou com DOTNET_ROOT e DOTNET_ROOT_X64 ausentes. O runtimeconfig inclui Microsoft.NETCore.App e Microsoft.WindowsDesktop.App 10.0.14. A tela Manutenção consultou serviços/histórico WUA e analisou a pasta de teste; encerrou com saída 0 em 6,755 s, CPU do processo 3,438 s. A releitura da CLI confirmou quatro sessões salvas: diagnóstico básico e as três novas consultas. Nenhum journal/verificação SFC/DISM foi iniciado.

Imagem real inspecionada: `optimus-0.5-maintenance-final.png`. Recibos: `desktop-final-validation.json`, `desktop-history-final.json`. Renderização e integração não substituem navegação manual prolongada, teclado, leitor de tela ou piloto em outras máquinas.

SHA-256 do launcher: `9DAEECE25DFAA3B4C5282628FB0B6DD20AE91511C8A220A9F6DA360D4A00D2A3`. Esse hash sozinho não autentica DLLs/runtime; manifestos privados registram todas as fontes finais e a pasta publicada. O sufixo Git da versão identifica o checkout-base, sem representar o diff local não commitado. A pasta executável não é instalador nem distribuição assinada.

## Custos e limites restantes

As métricas de família são do Job PowerShell e não incluem todo o custo da CLI/GUI. Pico de working set do processo principal não foi obtido após sua saída e permanece desconhecido; não deve ser inferido do pico de commit do job nem somado a ele. Não se mediu pico global da tarefa, tokens dos agentes ou todos os custos de orquestração. Não houve inferência de modelo nos novos comandos.

Cache frio/quente e cargas externas não foram controlados. Não existe fluxo equivalente de serviços/WUA/pasta na versão anterior para comparação; as observações acima são medidas funcionais das novas tarefas, sem alegação de ganho de desempenho ou benchmark geral.

IA, backup, SMART e integridade anteriores permaneceram na suíte de regressão. As provas reais 0.2/0.3/0.4 continuam históricas: modelo, restic e SMART físico não foram homologados novamente; verificações elevadas SFC/DISM continuam sem prova nativa. Não houve limpeza, reparo, alteração de proteção ou instalação de drivers/atualizações neste recorte. Dados, bancos, binários, modelos e recibos permanecem locais e ignorados pelo Git.

Referências primárias: [Win32_Service](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-service), [QueryHistory](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-queryhistory), [RebootRequired](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-isysteminformation-get_rebootrequired), [atributos de arquivo](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants).
