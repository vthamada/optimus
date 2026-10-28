# Escopo e autoridade do opTImus 0.5

Este recorte acrescenta inventário de serviços, histórico local do Windows Update e análise de tamanho lógico de uma pasta explicitamente selecionada. São consultas por ação do usuário. Abrir o programa ou usar diagnose não inicia essas três consultas.

## Serviços e atualizações

O catálogo de scripts embutidos recebe apenas operações nomeadas. Serviços usam Win32_Service local; não há Start/Stop/Set, desativação ou interpretação automática de PathName como executável. Estado e modo de início são observações distintas; um serviço parado não é necessariamente defeito.

Windows Update usa o histórico local WUA e o sinal RebootRequired desse provedor. Não faz Search, scan, download, instalação ou reinício. Histórico vazio ou sucesso de uma instalação anterior não demonstra que o computador esteja atualizado. O sinal de reinício não cobre todos os componentes do Windows.

Identidades, códigos, tipos, datas e valores originais permanecem nas evidências. Omissões, permissões, incompatibilidades e limites produzem cobertura explícita. A saída original é conservada dentro dos limites; não há laudo de segurança ou saúde derivado apenas do estado de transporte.

## Espaço de uma pasta

A escolha deve apontar para uma pasta local direta. O coletor consulta somente metadados, sem leitura de conteúdo, hash, exclusão, descompactação ou hidratação solicitada. Pontos de reanálise, junctions e entradas offline/recall são recusados ou omitidos com cobertura informada; não são atravessados.

O total soma tamanhos lógicos das entradas observadas e apresenta até vinte maiores arquivos. Não mede alocação física, bytes recuperáveis, ADS, compressão ou deduplicação. Hardlinks podem somar o mesmo conteúdo mais de uma vez. A observação não é snapshot atômico: outros processos podem mudar tamanhos e entradas durante a coleta.

Enumeração, profundidade, duração e detalhamento são limitados. O cancelamento é cooperativo entre chamadas de metadados do sistema, que não são preemptíveis. Falha em uma subpasta não vira tamanho zero nem completude; mantém totais parciais e motivos. A interface preserva a origem capturada mesmo se o texto da seleção mudar durante a chamada.

## Limites anteriores preservados

Continuam sem limpeza, reparo automático, alteração de serviços/proteções, instalação de drivers ou elevação automática. A IA continua sem terminal e sem autorização para executar ações. SFC/DISM permanecem restritos ao catálogo 0.4 e não são executados para validar este recorte. Dados e evidências operacionais ficam locais, sem commit/push ou distribuição implícita.
