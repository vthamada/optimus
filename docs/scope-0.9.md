# Escopo e autoridade do opTImus 0.9

O módulo Instabilidade reúne consultas de reinicialização/WHEA, resultados já registrados de diagnóstico de memória, inventário de placa-mãe/BIOS/CPU/RAM, importação de HTML do MemTest86 e análise de um dump selecionado com CDB. A IA local opcional escolhe a próxima consulta entre alternativas nomeadas; hipóteses e próximos testes vêm de regras declaradas e conservam referências às evidências.

## Investigação local

O roteiro tem até quatro passos, no máximo uma execução de cada ferramenta. Sem IA, consulta as três fontes locais em ordem declarada; dump só entra quando arquivo, CDB e símbolos foram selecionados. A inicialização e a abertura da aba não executam essas consultas. Seleção vazia da IA encerra o roteiro sem concluir saúde. Escolhas desconhecidas, repetidas ou sem cobertura exata são recusadas. O transporte deve comportar o contexto compartilhado e todas as alternativas; não omite ferramentas silenciosamente para caber.

Kernel-Power 41, EventLog 6008, User32 1074, registros de bugcheck e WHEA permanecem classes distintas. Contadores descrevem registros válidos retidos, não o total físico de reinícios. 1074 é solicitação, não confirmação de execução. Correlação 41/6008 é inferência temporal sugerida: vizinhança mútua e única até 120 segundos, até 32 pares e 8 KiB de referências; não altera os dados ou comprova identidade de um incidente. Duplicatas conhecidas usam canal/RecordID/provider/ID, sem deduzir identidade por horário. Rotação/limpeza do log pode impedir observar eventos antigos.

O período é de um a 365 dias. Restart retém até 512 registros mais sonda, 1 MiB de XML e orçamento aproximado de payload de 4 MiB; resultados de memória, 64 mais sonda, 256 KiB de XML e payload de 1 MiB. Cada XML tem retenção máxima de 32 KiB e até 32 campos de 2.048 caracteres. Limites produzem cobertura parcial. O leitor nativo tem prazo cooperativo de 30 segundos; chamadas e ToXml podem executar/alocar antes dos limites.

CompletionType inequívoco define Pass/Fail nos formatos conhecidos do provedor de memória. ID do evento, NumBadPages ou ausência de resultado não são convertidos automaticamente em laudo. Nenhum teste físico é iniciado ou agendado.

## Relatórios de memória e hardware

O usuário escolhe um relatório HTML existente do MemTest86. O importador conserva bytes, digest, caminho, codificação e texto original; o DOM não carrega scripts ou recursos. Aceita UTF-8 estrito e UTF-16 LE/BE somente com BOM explícito. O limite de entrada é 1 MiB. Linhas de erro e totais declarados permanecem separados; Last 10 Errors é uma amostra. XOR de esperado/recebido informa bits dentro do valor, sem identificar automaticamente um módulo físico. Formatos, campos duplicados e resultados ambíguos ficam desconhecidos.

O inventário consulta propriedades CIM selecionadas de ComputerSystem, BaseBoard, BIOS, Processor e PhysicalMemory. Valores e datas originais não são convertidos em comprovação de XMP/EXPO, overclock, saúde ou compatibilidade de BIOS. Não coleta serial/UUID por conveniência nem altera firmware. Cada classe tem orçamento independente e omissões explícitas.

## Dumps selecionados

O usuário fornece cdb.exe, arquivo .dmp e pasta única de símbolos locais. CDB é opcional e não é instalado pelo aplicativo. São aceitas assinaturas MDMP/PAGEDUMP/PAGEDU64, com limite de 32 bytes a 2 GiB; assinatura não comprova estrutura íntegra. Arquivo e executável são vinculados, com digest e argumentos fixos. Não há attach a processos ativos, comandos livres, symbol server configurado ou cópia automática do dump.

A análise usa Windows Job, prazo de 120 segundos, memória de 1 GiB e saída de 256 KiB com bytes/codificação preservados. Até 50 operações por raiz; recibos e falhas são retidos, sem repetição ou exclusão automática. Cleanup não confirmado conserva ownership e leases. A instalação CDB e suas DLLs não são integralmente verificadas; flags offline não constituem sandbox de rede. Completude dos símbolos permanece desconhecida, inclusive com exit zero.

## Firmware e acesso à internet

Fabricante/modelo observados podem apontar páginas oficiais de suporte de um catálogo fechado. Fabricante conhecido não confirma imagem compatível ou versão mais recente. A leitura de uma página exige URL explícita e ação separada: GET ao host exato HTTPS do catálogo, porta 443, sem credenciais/cookies, redirecionamento automático ou envio de logs/dumps/relatórios. Essa operação não pertence ao catálogo automático da IA.

Resposta limitada a 512 KiB e 20 segundos, com metadados, digest e bytes completos ou prefixo identificado. HTML é interpretado de forma inerte. Menções a memória, estabilidade ou módulo de segurança são heurísticas textuais de parágrafos retidos; não confirmam aplicabilidade, correção de um defeito, download ou aplicação de BIOS. Conteúdo remoto, dumps, eventos e relatórios são dados sem autoridade para executar instruções.

## Preservação e limites de validação

Fontes incorporadas substituem somente seus próprios IDs; demais evidências conservam identidade e data. Rastro, hipóteses e referências entram no relatório da sessão. Cancelamento conserva etapas já concluídas como resultado parcial; a execução cancelada não é apresentada como coleta completa. Falha no SQLite não apaga stdout ou impede a tentativa de relatório da CLI.

Integração local e fixtures não demonstram diagnóstico físico, eficácia da seleção por IA, execução real de CDB ou compatibilidade de firmware. As provas efetivamente executadas e seus limites ficam em validation-0.9.md. Não há instalação, reinício, alteração de memória/BIOS, mascaramento de RAM, limpeza ou reparo automático nesta entrega.
