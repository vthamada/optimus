# Validação do protótipo 0.2

Data: 27/10/2026. Implementação local em Windows x64 build 26300, SDK isolado .NET 10.0.403/runtime 10.0.14. Não houve commit, push, CI remoto, instalação global ou publicação de release nesta rodada.

## Checks finais

| Check executado | Resultado |
| --- | --- |
| dotnet format --no-restore --verify-no-changes | Aprovado após normalização de espaços |
| dotnet restore Optimus.slnx --locked-mode | Aprovado antes e depois de publish |
| Build da solução Release --no-restore --disable-build-servers | 0 avisos, 0 erros |
| Testes Release --no-build --no-restore | 159 aprovados, 0 falhas, 0 ignorados; 12,838 s |
| Publish Desktop Release win-x64 self-contained | Pasta executável gerada localmente |
| Arquivos versionáveis | 83; nenhum exe/dll/modelo/banco nos caminhos versionáveis; .tools e artifacts ignorados |

Logs ficam em `.tools/verification/iteration-02`: `format-check-final.log`, `restore-locked-final.log`, `build-release-final.log`, `tests-release-final.log`, `publish-final.log` e `restore-after-publish.log`. Esses logs não são enviados ao Git.

## Inicialização e comandos públicos

A pasta `artifacts/app` foi iniciada diretamente com DOTNET_ROOT/DOTNET_ROOT_X64 ausentes. O runtimeconfig inclui o runtime próprio. As telas Backup (2,123 s), Ferramentas (1,116 s) e Assistente com inferência real (33,158 s) encerraram com código 0 e produziram imagens WPF reais. Recibo: `desktop-final-validation.json`; imagens `optimus-final-backup.png`, `optimus-final-tools.png` e `optimus-final-assistant.png`, sob a raiz local de verificação. Não houve uso manual prolongado ou auditoria de acessibilidade.

A amostra adicional da GUI voltou a usar uma formulação genérica que confundiu tipos de coleta com eventos relacionados a CPU/memória/volumes. O aviso experimental permanece necessário; as provas técnicas não tornam a interpretação confiável para laudos ou ações automáticas.

SHA-256 do launcher publicado: `AED4DF15269BA7330462B414ABFA98D3BF6A28EC770883502A5323252A2CBD19`. O produto é uma pasta com DLLs/runtime, não apenas esse launcher; o hash sozinho não autentica o conjunto.

Na CLI Release, diagnóstico real e relatório HTML retornaram 3 (cobertura parcial por limite de eventos). Uma tentativa posterior de exportar ao mesmo nome retornou 1 e preservou o SHA-256 do relatório existente. `profile-check` e `smart-import` retornaram 0; SMART usou fixture explícita, não hardware. Recibo: `cli-final-validation.json`.

## Cobertura executada

Os testes cobrem diagnóstico/persistência, perfis, processos reais de fixture em Job Object, cancelamento/timeout e finalização de descendentes, limites de saída, argumentos literais Unicode, ambiente removido, contratos SMART, seleção/contrato da IA, reserva de capacidade entre instâncias e fluxo de backup com ferramentas simuladas.

O fluxo real de backup foi executado com restic 0.19.1 sobre três arquivos próprios de laboratório (366 bytes, incluindo Unicode, binário e subpasta). O snapshot `c4fde94b3e49285f39a02a49890d581272fc785186f54fd2a83bd57bd050d27e` teve integridade conferida por leitura dos dados. Uma restauração separada usou destino novo e --verify. Todos os SHA-256 dos arquivos restaurados coincidiram com a origem; a origem permaneceu inalterada. A senha tinha ACL privada antes da execução e seu valor não apareceu nos journals.

Nova chamada CLI de prévia para o mesmo repositório foi recusada pela reserva durável. Conteúdo do repositório e journals permaneceu inalterado. A contagem independente de criação de processos via CIM não estava disponível; ausência de invocação externa na prévia é sustentada pelo fluxo e pelos testes contratuais, sem alegação de medição dinâmica de zero processos.

Os recibos locais ficam em `.tools/verification/iteration-02/backup-real-final/validation-finalsource.json`. O digest da CLI registrada no recibo identifica exatamente o build usado; alterações posteriores em outros módulos não são retroativamente parte dessa prova. Evidências e senha permanecem fora do Git.

## Custos observados do backup

| Operação | Parede CLI | CPU dos jobs de ferramentas | Maior pico de commit dos jobs |
| --- | ---: | ---: | ---: |
| Criar e verificar backup | 8,109 s | 4,656 s | 153.456.640 bytes |
| Restaurar e verificar | 1,904 s | 0,578 s | 99.782.656 bytes |

CPU é acumulada na família do job; o pico é memória comprometida do job, não working set. Não inclui o coordenador CLI. Cache frio/quente não foi controlado. Não houve inferência nesse fluxo. Tokens dos agentes, preparação, pico total da aplicação e comparativo antes/depois não foram medidos.

## IA real

Executado llama-completion b11669 CPU x64 com Qwen2.5-1.5B-Instruct Q4_K_M e protocolo v2, pela CLI Release. O perfil de teste omitiu o hash configurado do motor; o SHA-256 observado coincidiu com a versão fixada, foi vinculado ao executor e admitiu somente o trailer oficial após um único JSON válido. A seleção entregou 4 de 4 evidências, com 27 fatos omitidos e 6.389 bytes UTF-8 de entrada. O orçamento de saída foi 768 tokens; contagens reais de tokens de entrada/saída são desconhecidas, não estimadas como medidas.

Recibo: `ai-real-final-c803216f5f17489fa52e48bdd0a006d8-data/assistant/run-d262d40728084c2aa7f2fcc7f82d8d4c/result.json`, sob a raiz local de verificação. A CLI retornou 0 e o contrato/referências passaram. A revisão encontrou observação principal sustentada e reconhecimento da amostra limitada, mas também resumo encerrado no meio de uma frase ao atingir 240 caracteres e três referências sem pertinência para aquela observação. **A prova confirma a integração técnica, não homologa a qualidade das respostas.** O aplicativo conserva o aviso experimental e não executa ações propostas pelo modelo.

Parede CLI: 39,730 s; CPU da CLI: 4,547 s; maior working set da CLI amostrado a cada 250 ms: 55.201.792 bytes (não é pico exato). Motor/job: parede 35,711 s, CPU acumulada 132,938 s e pico de commit da família 1.363.705.856 bytes. Os picos têm métricas/intervalos diferentes e não devem ser somados como pico total. Cache e interferência de outras tarefas não foram controlados; não há comparação cold/warm ou benchmark de qualidade.

Tentativas anteriores com frontend humano, saída truncada e confusão entre filtro e observação foram preservadas. A representação v2 separa cobertura de fatos e omite o texto de filtro da entrada do modelo, mantendo o diagnóstico original. Não se recupera JSON de texto arbitrário para esconder falhas de transporte.

## Limites

- SMART validado por contratos e fixtures; não foi instalado/executado smartctl sobre hardware real.
- Backup real restrito ao laboratório. VSS, arquivos em uso, ACL, ADS, EFS, placeholders de nuvem, grandes volumes e recuperação após falha de disco não foram homologados.
- Modelo pequeno experimental; validação sintática e de referências não demonstra correção semântica geral. Respostas truncadas foram rejeitadas e preservadas para investigação, sem reparo por busca de chaves.
- Renderização WPF confirma a composição visual; não substitui uso manual prolongado, navegação por teclado, leitor de tela ou avaliação de acessibilidade.
- Sem instalador, assinatura Authenticode, piloto em várias máquinas, homologação de antivírus ou teste de distribuição. Pesos e ferramentas externas não fazem parte da pasta publicada do app.

A validação histórica 0.1 permanece em `validation.md`.
