# Escopo e autoridade do opTImus 0.6

Esta rodada acrescenta consultas locais explícitas de processos, tarefas agendadas e discos/partições. Usa o catálogo fixo de scripts embutidos e os fluxos compartilhados de diagnóstico, histórico e relatório. Abrir o aplicativo ou executar diagnose não inicia essas consultas.

## Processos

Win32_Process fornece o recorte limitado de identidade reportada, criação, memória e contadores cumulativos. Não há leitura de conteúdo de memória, CommandLine, ambiente, debug, alteração de privilégios, prioridade ou encerramento. PID, parent PID e caminho não estabelecem ownership nem relação ainda válida entre processos; identificadores podem ser reutilizados. O snapshot não é atômico.

Working set é memória reportada por processo, não pressão global nem memória exclusiva recuperável. Contadores originais de tempo não são percentual instantâneo de CPU. Tipos, unidades e lacunas de permissão ficam explícitos, sem converter indisponível em zero.

## Tarefas agendadas

A conexão COM é local, sem parâmetros remotos. A enumeração inclui tarefas ocultas dentro do escopo acessível, com limites de pastas, profundidade, registros e projeção. Nome, caminho, habilitação, estado, horários e último resultado são somente metadados. Não são lidos definição, ações, argumentos ou credenciais; não se inicia, interrompe, registra, exclui ou reconfigura uma tarefa.

Sentinelas e códigos originais permanecem identificados. Ready, Disabled ou resultado histórico não são laudo de saúde; horários preservam a representação/fuso informado, sem conversão temporal adivinhada. Acesso negado a uma subpasta torna a cobertura parcial.

## Discos e partições

Win32_DiskDrive e Win32_DiskPartition consultam metadados locais. Capacidade, índices, identificadores, tipo e estado reportado são conservados. Índices não são convertidos automaticamente em autoridade para acessar um dispositivo ou em associações sem prova de unicidade. Um campo Status não substitui SMART nem teste físico.

Não há leitura de setores, SMART implícito, benchmark, montagem, formatação, alteração de partições, inicialização ou reparo. As duas consultas possuem cobertura e orçamento próprios, para uma não eliminar a outra.

## Restrições preservadas

Mantêm-se consultas limitadas, bytes/JSON originais e falhas explícitas, sem terminal para IA, elevação automática, limpeza, reparo ou modificações de proteção/serviços. As verificações SFC/DISM não são executadas para validar esta rodada. A implementação local não implica commit/push, publicação ou distribuição.
