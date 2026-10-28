# Validação do opTImus 0.9

Data do cliente: **28/10/2026**. Windows x64, SDK isolado .NET 10.0.403, runtime incluído 10.0.14. Três agentes implementaram eventos, hardware/importação e CDB; continuaram nas frentes de CLI, interface e consulta de páginas oficiais. O coordenador integrou o roteiro, a seleção local de próxima ação e a validação conjunta. Trabalho local, sem commit/push, CI remoto, assinatura ou publicação pública.

## Compilação e regressões

- Baseline: hashes dos 167 arquivos da versão anterior conferidos antes da implementação. Integração de 59 arquivos, sem escritores sobrepostos; HTML upstream usado na investigação permaneceu fora da fonte versionável.
- AngleSharp **1.8.6** adicionado com versão central fixada e lockfiles atualizados. É usado como parser inerte, sem loader ou JavaScript. A referência do projeto de testes à CLI permite testar a fronteira pública de argumentos.
- Primeira compilação identificou referência de Application ao serializador de Infrastructure; foi substituída por serialização local do rastro versionado, preservando a direção de dependências.
- Builds Debug subsequentes aprovados. Focados: 310 e depois **312 testes aprovados**, conforme os ajustes de finalização.
- Formatação aplicada e `dotnet format Optimus.slnx --no-restore --verify-no-changes` aprovado no estado final.
- Build Release final: **zero avisos e zero erros**, 15,32 s.
- Suíte Release final: **976 aprovados, zero falhas e zero ignorados**, 26,1926 s.
- Publish Windows x64 self-contained aprovado; restauração `--locked-mode` repetida depois do publish, sem alteração de versões.

Comandos: build de Optimus.slnx com `--configuration Release --no-restore --disable-build-servers`; `dotnet test tests/Optimus.Tests/Optimus.Tests.csproj --configuration Release --no-build --no-restore`; publish de Optimus.Desktop com `--runtime win-x64 --self-contained true --output artifacts/app`. Logs privados em `.tools/verification/iteration-09`.

Revisões e regressões cobriram seleção vazia válida, entrega integral do contexto e alternativas, tentativa de ferramenta repetida/estrangeira, captura de parâmetros antes de await, cancelamento na finalização da última etapa, recibo de decisão terminal, prefixo HTTP cancelado sem Save/retry, mudanças de arquivo, limites, símbolos desconhecidos e ownership pendente de CDB. Testes de CDB usam executor simulado; não são prova de CDB real.

## Consultas locais reais

Rodada sem execução de testes físicos ou alterações do Windows. Os arquivos `native-final-validation.json` e os relatórios `*-final.json` conservam as observações, inclusive fonte original, datas, cobertura e limitações.

| Comando | Resultado | Tempo CLI | CPU CLI | JSON |
|---|---|---:|---:|---:|
| restart-history, 30 dias | Complete, exit 0; 285 fatos | 1,2432 s | 0,34375 s | 87.689 bytes |
| memory-results, 30 dias | Complete, exit 0; 17 fatos | 0,4847 s | 0,28125 s | 6.704 bytes |
| hardware-profile | Complete, exit 0; 70 fatos | 1,4162 s | 0,390625 s | 21.144 bytes |
| memory-import, exemplo oficial | Partial, exit 3; 173 fatos | 0,9464 s | 0,546875 s | 157.852 bytes |
| memory-import, fixture sintética | Partial, exit 3; 113 fatos | 0,8439 s | 0,515625 s | 35.418 bytes |
| investigate, sem IA, com fixture | Partial, exit 3; cinco fontes | 1,6710 s | 0,703125 s | 154.956 bytes |
| dump-analyze, CDB ausente | Unavailable, sessão Partial, exit 3 | 0,5157 s | 0,34375 s | 3.988 bytes |
| firmware-notes, GET explícito | HTTP 403 preservado, exit 1 | 1,3398 s | 0,453125 s | 9.558 bytes |

No recorte de logs disponível, foram observados **oito registros User32 1074**, zero Kernel-Power 41/6008, zero bugchecks selecionados e zero WHEA selecionados. 1074 é pedido de desligamento/reinício, não confirmação de oito reinícios inesperados. Nenhum resultado de diagnóstico de memória foi devolvido no período. Isso não comprova saúde da RAM ou ausência de incidentes fora do log/período. Correlação temporal e duplicatas foram verificadas por regressões; não houve par 41/6008 real nessa amostra.

O hardware devolveu as cinco classes selecionadas; o Job registrou 0,8300344 s, CPU da família 0,859375 s e pico de commit 104.980.480 bytes. A leitura de eventos de reinicialização reportou 0,1871561 s; resultados de memória, 0,0544221 s. Memória do processo raiz e custos de serviços CIM fora do Job não foram medidos.

## Relatórios e comparação independente

O relatório oficial [MemTest86Report.html](https://www.memtest86.com/MemTest86Report.html) foi importado de uma cópia local de **53.098 bytes**, com UTF-16LE/BOM. Foram reconhecidas dez linhas selecionadas de erro; o resumo e a amostra permaneceram distintos. Não se tratou de um teste executado neste computador.

Uma fixture identificada como sintética contém sete linhas com esperado `0`, recebido `268435456` e XOR `268435456`: um bit divergente, posição **28**. As sete linhas foram importadas como tais. Isso verifica o cálculo e a preservação do formato; **não encontrou defeito físico na RAM da máquina**.

O verificador independente confirmou:

- Oito cabeçalhos XML originais — provider, ID, RecordID e SystemTime — e 56 campos EventData, sem interpretar mensagens localizadas.
- 27 campos CIM projetados contra o JSON original retido.
- Bytes dos dois HTMLs idênticos às fontes selecionadas; 10 + 7 linhas conferidas por cálculo independente de XOR, popcount e posição de bit.
- Rejeição de `--execute true` nos sete comandos novos, antes de criar diretórios de dados.
- Histórico da interface relido pela CLI, com três sessões: diagnóstico, importação e investigação.

Os originais e comparações ficam em `independent-final-validation.json`. Não houve arquivo de dump real nem teste de RAM físico.

## Modelo local e limite de escolha

O piloto usou llama-completion b11669 e Qwen2.5-1.5B-Instruct Q4_K_M já presentes em `.tools`, com digests conferidos. Na primeira rodada, o transporte genérico permitiu quatro fatos e o modelo devolveu todas as alternativas; o adaptador executou a primeira. A restrição passou a pertencer ao produtor do protocolo: a investigação configura **no máximo uma escolha**, no schema, nas instruções e no parser. O assistente genérico mantém o padrão de até seis fatos. A suíte completa foi repetida após essa mudança.

No piloto final, `single-model-final-validation.json` confirmou schema `maxItems: 1`, as três propostas e o contexto entregues juntos, prompt de **2.993 bytes UTF-8**, retorno `selectedFacts: [F1]` e execução de RestartHistory. O roteiro encerrou com **StepLimit** porque foi configurado para um passo. Não houve fallback para outro comando ou seleção omitida silenciosamente.

Tempo total da CLI: **21,7426 s**, CPU raiz 5,859375 s. Job do modelo: 11,6728942 s, CPU da família 37,890625 s e pico de commit 1.354.936.320 bytes. O total inclui preparação, verificação de arquivos e a consulta selecionada. Contagem real de tokens não foi medida. São observações com caches não controlados, sem comparação cold/warm; não demonstram ganho de desempenho nem qualidade geral do raciocínio. Uma pergunta piloto e referências válidas não comprovam relevância em outros problemas.

Hipóteses e próximos testes são derivados por regras declaradas sobre fatos normalizados. O modelo escolhe uma consulta; não gerou uma conclusão causal livre nem confirmou módulo defeituoso, overclock ou correção por firmware.

## Páginas oficiais e CDB

O GET explícito a `https://www.msi.com/support` retornou **HTTP 403** no transporte observado. URL, status, bytes e motivo foram preservados; as contagens de menções ficaram desconhecidas. Não houve bypass, redirecionamento, extração de notas aplicáveis ou download de imagem de BIOS. Fixtures cobrem respostas positivas, charset, limites, redirects e conteúdo inerte; não comprovam compatibilidade com todos os portais OEM, TLS/proxy ou autenticidade do conteúdo de uma resposta intermediária.

CDB não foi encontrado nos locais convencionais/seleção deste cliente. A tentativa com arquivo sintético de cabeçalho foi recusada antes de iniciar debugger. **Flags, codificação, símbolos e análise real de dump ainda precisam de validação com instalação confiável de CDB e dump autorizado.** Os testes simulados não substituem essa prova. Não houve instalação automática, attach a processos ativos ou tráfego de símbolos habilitado pelo aplicativo.

## Interface e pacote

O executável **0.9.0.0** abriu sem DOTNET_ROOT/DOTNET_ROOT_X64, com runtime próprio 10.0.14. A conferência opt-in importou a fixture sintética e executou o roteiro sem IA, terminou com exit 0 em **5,9838 s** e CPU raiz 2,96875 s. A imagem real da aba Instabilidade foi inspecionada; testes manuais de teclado/leitor de tela e todas as combinações visuais não foram executados.

Fonte final do render: `optimus-0.9-maintenance-verified.png`, metadados em `desktop-verified-validation.json`. A imagem anterior `*-final.png` e os logs anteriores preservam a fase antes da restrição do modelo. Nenhum journal de integridade foi criado. SFC/DISM, restic, SMART físico, reinicialização e aplicação de firmware não foram executados.

SHA-256 do host executável: `D3A5FCB41D8AFEA5CC16B47E05B67657C57DC085EC3781732A455AA31D88D026`. O host sozinho não representa todas as DLLs: o manifesto da pasta publicada registra cada arquivo do pacote final.

Arquivos finais de prova: `source-final-manifest.json`, `published-folder-manifest.json` e `final-verification.json`. Dados, modelos, dumps, HTMLs upstream e binários ficam ignorados pelo Git. Não há instalador assinado, release pública ou atualização remota do repositório nesta entrega. Consulte o [escopo 0.9](scope-0.9.md) e os [comandos locais](local-tools.md).
