# Validação do opTImus 0.6

Data do cliente: **28/10/2026**. Windows x64, SDK isolado .NET 10.0.403 e runtime 10.0.14. Três frentes de agentes implementaram processos, tarefas agendadas e armazenamento. O coordenador integrou GUI/CLI e validou o conjunto; houve revisão cruzada somente por leitura. Trabalho local, sem commit/push, CI remoto ou distribuição pública nesta rodada.

## Build, testes e revisão

- Restauração `--locked-mode`, antes e depois do publish: aprovada; nenhum novo pacote.
- Formatação aplicada e `dotnet format --no-restore --verify-no-changes`: aprovado.
- Build Release: zero avisos e zero erros, 12,91 s.
- Testes focados iniciais: 110 aprovados. Suíte Release final: **539 aprovados, zero falhas e zero ignorados**, 59,7099 s.
- Publish Release Windows x64 self-contained: aprovado.

As novas regressões cobrem formatos JSON ambíguos/malformados, UInt64 exatos, datas tipadas/fuso desconhecido, limites independentes e preservação de fonte/erros. Processos incluem ordenação limitada por working set, valores desconhecidos e vínculo ao índice original. Tarefas incluem raiz indisponível, travessia parcial, sentinelas, códigos não mapeados e liberação COM. Armazenamento inclui fontes independentes, campos indisponíveis e ausência de junção/saúde inferida. GUI e host cobrem ação explícita, escopo exato, preservação das outras fontes, busy/cancelamento e catálogo fixo.

A revisão encontrou um ajuste de proveniência: os rótulos de recursos COM de uma pasta usavam o contador global após a recursão. O script agora captura um identificador local por frame; a finalização referencia o frame que adquiriu o recurso. O ajuste foi incluído antes da suíte final e da prova nativa. A prova nativa não provocou falha de liberação COM; esse cenário permanece coberto por contrato/fixture e inspeção da estrutura, sem reprodução real forçada.

Logs privados em `.tools/verification/iteration-06`: `restore-locked.log`, `format-apply.log`, `format-check.log`, `build-debug.log`, `tests-focused.log`, `build-release.log`, `tests-release.log`, `publish.log` e `restore-after-publish.log`.

## Consultas nativas Release

As três consultas foram executadas com novos dados e relatórios isolados. O rótulo `initial` nos recibos identifica a primeira prova nativa desta rodada, já usando o código Release final; não é resultado de Debug ou de um build anterior aos ajustes.

| Consulta | Resultado | Fatos | Issues | Parede CLI | CPU CLI | Parede job | CPU família job | Pico de commit da família |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Processos | Partial, saída 3 | 154 | 5 | 2,965 s | 0,453 s | 1,148 s | 1,000 s | 119.255.040 bytes |
| Tarefas | Partial, saída 3 | 237 | 4 | 2,271 s | 0,391 s | 1,781 s | 1,109 s | 108.978.176 bytes |
| Discos/partições | Complete, saída 0 | 104 | 0 | 1,201 s | 0,313 s | 0,754 s | 0,688 s | 104.861.696 bytes |

Processos: 257 enumerados incluindo sonda, 256 retidos no JSON original e 11 projetados. O relatório tem 171.270 bytes. As issues informam limites de enumeração/projeção e dois campos ExecutablePath sem valor reconhecido. Não foi presumida uma causa para esses campos ausentes. A projeção ordena somente os registros retidos por working set conhecido descendente, com índice original como desempate; não é ranking de todos os processos do computador.

Tarefas: 32 pastas visitadas, zero falhas de pasta reportadas e 55 tarefas retidas no JSON, das quais 13 projetadas. A travessia não concluiu o catálogo porque atingiu o limite de pastas; o relatório de 101.003 bytes informa isso e o limite de projeção. A enumeração solicitou tarefas ocultas, dentro do escopo acessível ao token atual; não demonstra acesso a todas as tarefas protegidas.

Armazenamento: dois discos e seis partições examinados, retidos e projetados; relatório de 32.553 bytes. Complete significa conclusão das consultas selecionadas, não saúde física, SMART, autenticidade do firmware ou correção de todas as associações do equipamento. Nenhuma associação automática foi produzida.

Recibos: `native-initial-validation.json`, `processes-initial.json`, `scheduled-tasks-initial.json`, `storage-initial.json`. Flags `--execute true` foram rejeitadas nos três comandos antes de criar armazenamento, com saída 1. Não houve execução de ações nos objetos observados.

## Comparação independente da proveniência

Um verificador separado comparou os fatos aos JSONs originais retidos, sem refazer as medições do Windows:

- Nome, PID e working set dos onze processos projetados coincidiram com seus registros de origem. A ordem coincidiu com uma ordenação independente dos 256 registros retidos.
- Caminhos e códigos de último resultado das treze tarefas projetadas coincidiram com suas entradas originais.
- Identificadores, capacidades e offsets dos oito registros de armazenamento coincidiram com as fontes correspondentes.

Recibo: `independent-final-validation.json`. Essas comparações confirmam o vínculo fonte/projeção da amostra, não a veracidade física do provedor nem a permanência do estado após o horário registrado. PID, parent PID e índice de disco não se tornam provas de ownership.

## Limites e interpretação

Processos: 256 registros mais sonda, payload de 220.000 bytes e resposta de 256 KiB; projeção conservadora de 128.000 bytes. Working set não é memória exclusiva recuperável. KernelModeTime/UserModeTime permanecem contadores cumulativos originais, sem percentual ou conversão em segundos de uso de CPU. O qualificador CIM informa unidades de 100 ns; a documentação textual de KernelModeTime contém descrição divergente em milissegundos. A unidade apresentada identifica o qualificador adotado, sem resolver essa divergência por inferência.

Tarefas: 128 registros, 32 tentativas de aquisição de pastas, profundidade 16, payload de 180.000 bytes, resposta de 256 KiB e projeção de 128.000 bytes. Os limites restringem chamadas Item, travessia e retenção. GetTasks/GetFolders podem criar coleções completas no serviço do Windows; o job do cliente não limita toda alocação ou atividade desse serviço externo. Conexão COM local, getters selecionados e finalização são verificados na fonte; não houve captura independente de toda atividade do serviço.

LastRunTime/NextRunTime preservam tipo, valor e Kind. DATE zero é somente uma representação identificada; o ano 1899, sozinho, não é traduzido em nunca executada. Códigos documentados, como HAS_NOT_RUN, têm rótulo próprio, preservando o inteiro original. NextRunTime não é garantia de execução futura. Não se leem TaskDefinition, ações, argumentos ou credenciais.

Armazenamento: 32 discos e 128 partições, com sondas, payload de 110.000 bytes e projeção de 64.000 bytes por fonte. Resposta total de até 256 KiB; campos textuais de até 2048 caracteres nas três ferramentas. Metadados do provedor não equivalem a exame físico; não foram lidos setores, executados benchmarks, montados volumes ou modificadas partições.

## Executável e interface

`artifacts/app/opTImus.exe`, versão **0.6.0.0**, iniciou com DOTNET_ROOT e DOTNET_ROOT_X64 ausentes. O runtimeconfig inclui Microsoft.NETCore.App e Microsoft.WindowsDesktop.App 10.0.14. A tela Manutenção executou explicitamente processos, tarefas e armazenamento; encerrou com saída 0 em 11,446 s, CPU do processo 4,203 s. A releitura confirmou quatro sessões no histórico: diagnóstico inicial e três novas consultas. Nenhum journal ou verificação SFC/DISM foi iniciado.

Imagem real inspecionada: `optimus-0.6-maintenance-final.png`. Recibos: `desktop-final-validation.json`, `desktop-history-final.json`. Os controles novos aparecem na tela; renderização e integração não substituem uso manual prolongado, teclado, leitor de tela ou piloto em outras máquinas.

SHA-256 do launcher: `9D3AFCD8A3C1D347B4AAFD793A2D83DECF089F3EA116723C5F4FA1698147E129`. O digest do launcher sozinho não autentica DLLs/runtime; manifestos privados registram a pasta publicada e as fontes finais. O sufixo Git identifica o checkout-base, não o diff local não commitado. O resultado ainda é uma pasta executável, sem instalador ou assinatura de distribuição.

## Custos e cobertura restante

CPU/memória da família referem-se ao job PowerShell, sem incluir todos os serviços externos WMI/Task Scheduler. CPU CLI está separada; pico de working set do processo principal e pico global da tarefa não foram medidos. Pico de commit não deve ser tratado como working set nem somado a ele. Tokens dos agentes e custos totais da orquestração permanecem desconhecidos; não houve inferência de modelo nos novos comandos.

Cache frio/quente e outras cargas não foram controlados. Não havia fluxos equivalentes dessas três consultas na versão anterior para comparar. Os números são observações funcionais da amostra, sem alegação de ganho de desempenho ou benchmark geral.

IA, backup/restauração e ferramentas anteriores permaneceram nas regressões. As provas reais das versões anteriores continuam históricas: não se homologaram novamente modelo, restic, SMART físico ou verificações elevadas SFC/DISM. Esta rodada não fez limpeza, reparo, encerramento de processos, execução/alteração de tarefas, alteração de proteção ou instalação de atualizações/drivers. Dados, bancos, binários e recibos permanecem locais e ignorados pelo Git.

Referências primárias: [Win32_Process](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-process), [Win32_DiskDrive](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-diskdrive), [Win32_DiskPartition](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-diskpartition), [TaskService.Connect](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskservice-connect), [GetTasks](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-gettasks), [constantes do Task Scheduler](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-error-and-success-constants).
