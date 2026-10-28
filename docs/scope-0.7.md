# Escopo e autoridade do opTImus 0.7

Este recorte acrescenta consultas locais explícitas de conexões TCP/endpoints UDP, perfis do Firewall e eventos selecionados de falhas de aplicativos. A interface passa a usar um seletor com identidade nomeada, compartilhando o vínculo consulta/coletor com a CLI. Posição no catálogo ou texto de apresentação não escolhem uma operação.

## Conexões e portas

Get-NetTCPConnection e Get-NetUDPEndpoint leem tabelas locais selecionadas. Não há envio de pacotes, teste de conectividade, DNS, sessão CIM remota, interceptação ou encerramento de conexões. UDP não é tratado como conexão remota. Endereços, portas, códigos/nomes de estado e PIDs são valores observados, com cobertura independente por fonte.

Um PID pode ter sido reutilizado e não estabelece propriedade do processo ou vínculo ainda válido com outro inventário. Estado/listening ou endereço remoto não são classificados automaticamente como ameaça, segurança ou saúde da rede. O snapshot não é atômico.

## Perfis do Firewall

A consulta usa somente os perfis de política ActiveStore, com campos selecionados; não enumera nem altera regras, perfis, interfaces ou proteções. Enabled, GpoBoolean, ações padrão e valores NotConfigured permanecem distintos. Valores CIM ausentes não se tornam False por adaptação do PowerShell. Perfil reportado não comprova qual perfil está ativo em uma interface nem o tratamento efetivo de um pacote específico.

## Falhas de aplicativos

A fonte é o log Application, com intervalo e seleção de IDs/provedores fixos. XML e metadados são preservados dentro dos limites, sem leitura de dumps, mudança de WER ou limpeza de logs. O evento é um registro histórico, não uma conclusão causal ou prova de falha atual do hardware/software.

A leitura nativa de eventos tem um produtor compartilhado com SystemEvents, mantendo a compatibilidade do coletor existente. Limites de retenção e validação de XML não limitam a alocação interna de ToXml/Event Log; prazo e cancelamento são cooperativos entre chamadas. Falha, corte e campos desconhecidos ficam explícitos.

## Restrições preservadas

As consultas só iniciam por ação explícita ou flags de verificação visual. A troca de seleção durante uma coleta não muda a fonte já solicitada. Histórico, relatórios, IDs/datas das outras evidências e erros de persistência continuam preservados. Não há terminal para IA, elevação automática, reparo, limpeza, alteração de regras/proteções ou distribuição implícita. SFC/DISM não são executados para validar esta rodada.
