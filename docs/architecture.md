# Arquitetura do opTImus 0.9

Desktop e CLI compõem os mesmos fluxos de Application. Domain permanece independente de Windows, WPF, SQLite e motores de inferência. Infrastructure implementa os contratos; detalhes de restic, llama.cpp e smartctl ficam em seus adaptadores.

## Evidências e armazenamento

Cada coletor interpreta sua fonte uma vez e publica fatos tipados, unidades, IDs, origem, data e cobertura. Regras e seleção da IA consomem esses fatos; texto de apresentação não determina limiares ou identidade. Evidência parcial/indisponível não se torna ausência de falha.

O diagnóstico executa coletores sequencialmente fora da UI. Cancelamento entre consultas é cooperativo; a chamada nativa em andamento precisa terminar. Uma sessão cancelada não é publicada como concluída. Histórico conserva a data original.

SQLite verifica application_id, schema e tipos antes de escrever ou materializar sessões. A primeira transação de propriedade termina sem cancelamento intermediário. Não adota banco desconhecido nem substitui sessões. Dados inválidos são preservados.

Relatórios e perfis usam AtomicNewFileWriter: limite de expansão antes da alocação, arquivo CreateNew, flush e rename sem substituição pelo mesmo handle. A pasta fica vinculada durante a operação. Limpeza pré-publicação usa apenas o handle próprio; falhas retêm a posse para retry explícito. Cancelamento após publicação preserva o arquivo e informa esse fato.

## Execução externa

IExternalToolRunner recebe caminho e argumentos separados, prazo, limites de saída/memória, digest opcional e nomes de variáveis a remover. Não oferece shell. WindowsToolRunner cria processo suspenso, vincula ao Job Object próprio e só então retoma a execução. Handles herdados são delimitados. Cancelamento/timeout encerram esse job; nunca um PID apenas observado. Cleanup e drenagem também fazem parte da finalização.

Métricas do job incluem CPU da família e pico de memória comprometida; não representam working set, uso de todo o sistema ou CPU do processo raiz da CLI. Falha de limpeza mantém ownership e erro primário. Saída permanece limitada antes de desserializar.

LocalPathGuard é o dono da interpretação de caminhos locais, identidades e leases. Adaptações de backup e IA aplicam suas políticas sobre esses fatos. Caminho de exibição não prova ownership nem autoriza exclusão. Entradas remotas, reparse points e aliases ambíguos são recusados no recorte atual.

## IA, backup e SMART

A IA recebe seleção compacta de fatos, pergunta e schema; não recebe ferramentas ou autorização para agir. O protocolo v3 permite apenas IDs curtos de fatos entregues, até seis. Um único mapeamento vincula seletor a ID da evidência, chave, valor, unidade e data. A apresentação usa esses valores originais; texto livre do modelo é recusado. Os aliases são específicos da chamada e o request retém o mapeamento completo. CandidateFactFilter é o dono da política lexical em chaves/rótulos: normaliza somente o índice de busca, conserva os fatos originais e registra termos, estratégia e exclusões. Sem correspondência, o fallback amplo é explícito. Seleção por relevância pode errar; não há interpretação causal pelo modelo neste modo.

Modelo e executável são vinculados durante execução. O protocolo valida JSON, campos, tamanhos e referências. Request, prompt, schema e resultado são evidências retidas. Capacidade é reservada entre instâncias; dados desconhecidos não são apagados para liberar vagas. A seleção visita cada candidato uma vez e evita serializar novamente o prompt completo a cada fato.

O transporte de completion é versionado. Apenas o executável b11669 com digest fixado admite o marcador oficial de término após um único documento JSON válido. Outros executáveis exigem JSON puro. Prefixos, banners, múltiplos documentos ou truncamentos são falhas; não há busca por chaves para recuperar texto arbitrário.

Backup e restauração separam prévia de execução. Planos têm identidade, prazo, fingerprint e uso único; são revalidados após a fila. Confirmação da GUI se refere ao plano mostrado. Alterações invalidam prévias, inclusive mudanças durante uma chamada assíncrona. A CLI exige --execute true para efeitos.

Reservas duráveis por destino e journal antecedem cada efeito. O fluxo é init, leitura da configuração, backup e check com leitura dos dados. Restauração exige snapshot exato e destino novo, com --verify e sem sobrescrita. Falhas preservam recibos e fatos confirmados; nenhuma repetição automática tenta resolver estado incerto.

SMART recebe um dispositivo básico explicitamente selecionado, usa somente consulta e mantém JSON original e bitmask de saída. Importação tem contrato e limites iguais. Não varre dispositivos, inicia autoteste nem modifica o disco.

O aplicativo não possui auxiliar elevado. Novas ações de limpeza, reparo ou manutenção exigirão seus próprios contratos de autoridade, limites e pós-verificação.

## Inventário explícito

Composition.CreateInventory compõe os quatro coletores de IDiagnosticCollector: rede, energia, programas e inicialização. NetworkInterface consulta apenas metadados locais. GetSystemPowerStatus preserva flags e sentinelas. O adaptador do Registro sonda tamanho antes de alocar valores e lê tipos sem expansão de REG_EXPAND_SZ. Não usa Win32_Product nem executa comandos registrados.

EvidenceSessionUpdates é o dono da incorporação à sessão: exige a cobertura exata dos coletores selecionados e substitui somente seus IDs de coletor, com comparação ordinal. Inventário e manutenção compartilham essa regra; não há interpretação duplicada de ownership. Demais evidências conservam identidade e data. Uma parcialidade da sessão sem explicação nos itens não é promovida a completude. Cancelamento antes da publicação preserva a sessão anterior; após gravação confirmada, o dado salvo e o cancelamento tardio são informados juntos.

Os cartões WPF de evidência compartilham template e exibem data por fonte. As listas de fatos têm janela de altura limitada e virtualização; relatórios conservam os fatos completos retidos. O inventário da CLI é uma nova sessão própria; a GUI pode incorporar os dados à sessão em uso.

## Manutenção e transporte nativo

MaintenanceServices compõe os coletores Defender/dispositivos e o serviço de integridade. WindowsSystemQueryRunner é o dono do catálogo de scripts embutidos: somente enums nomeados, PowerShell de System32, módulos do Windows, UTF-8 e argumentos fixos. Nenhum texto de comando vem da interface ou da IA. Políticas de execução herdadas não são contornadas.

WindowsIntegrityService emite planos de uso único vinculados à instância, com identidade/digest do executável, contexto, prazo e argumentos do catálogo. Confirmação, validade e permissões são reavaliadas após a fila; journal durável antecede a chamada. Estado do comando, persistência do recibo e gravação do histórico são fatos distintos. Falha no histórico não apaga o recibo ou transforma encerramento observado em reparo confirmado.

ToolRequest declara a codificação. O executor preserva o orçamento de bytes combinado e decodifica estritamente UTF-8, UTF-16LE ou OEM, sem sniffing. Quando solicitado, snapshots de texto e bytes partem do mesmo prefixo retido; a página indica a decodificação utilizada. Codificação inválida não é sucesso. Os bytes opcionais não são duplicados nas respostas dos demais fluxos.

Drivers mantêm DriverDate como texto original ou envelope de data tipada (sourceType/value/kind). Datas sem fuso não recebem um fuso inferido. O orçamento de projeção é dividido entre dispositivos e drivers para que uma consulta não elimine a outra; JSON original e omissões continuam disponíveis.
## Serviços, histórico de atualizações e espaço

A composição de manutenção inclui consultas nomeadas para serviços e histórico local WUA. A ausência de Search/scan é deliberada: os registros históricos não informam quais atualizações estão disponíveis agora. Códigos de operação/resultado e datas são interpretados uma vez nos respectivos coletores; o JSON original permanece retido. Um serviço parado é apenas um estado observado.

FolderSpaceCollector é criado somente após seleção explícita de caminho. Composition.CreateFolderSpace entrega o mesmo DiagnosticService usado pela CLI e GUI, sem um fluxo de persistência paralelo. A coleta de metadados mantém limites, leases/identidades e omissões; não lê conteúdo para estimar tamanho nem oferece exclusão. A sessão nova substitui apenas a evidência dessa ferramenta, mantendo as outras fontes e seus timestamps.
## Processos, tarefas agendadas e armazenamento físico

As novas consultas usam o mesmo WindowsSystemQueryRunner e DiagnosticService: enum nomeado, script compilado, host do sistema, limite anterior à desserialização e identidades/datas conservadas. Processes, ScheduledTasks e Storage são fontes diferentes de Services, Startup e DeviceDrivers. Nenhuma delas controla os objetos observados. PID, índice de disco ou nome de tarefa são referências da fonte, sem constituir autorização ou prova de ownership.

Os coletores produzem contagens de descoberta/retensão/projeção e estados parciais próprios. Os consumidores exibem fatos tipados; não reinterpretam contadores cumulativos como taxa de CPU, LastTaskResult como saúde geral nem DiskDrive.Status como SMART. Discos e partições mantêm seus orçamentos e identidades separados, sem join implícito por campos de exibição.

TaskInventory só usa getters COM e fecha referências próprias em finally. Nomes de referências cuja finalização não foi confirmada permanecem no envelope, distintos de caminhos de arquivos e dos recursos que pertencem ao executor. Falhas de um ramo não tornam o restante do catálogo completo. A interface compartilha a incorporação por ownership já usada nas demais consultas, preservando as fontes anteriores e seus timestamps.
## Seleção de consultas e suporte 0.7

MaintenanceQueries interpreta tokens CLI em identidades MaintenanceQuery; MaintenanceServices.ResolveQuery é o dono do vínculo entre a identidade, o DiagnosticService e o CollectorId. CLI e Desktop usam esse vínculo. Consultas não configuradas ou desconhecidas são falhas tipadas; posição na lista e rótulo não selecionam operações. O seletor WPF substitui os comandos redundantes de botões de consulta; os métodos nomeados de preview permanecem delegando ao mesmo vínculo.

A operação selecionada é capturada antes do await. Trocar o seletor durante uma consulta não relabela a fonte em execução; busy impede outra coleta concorrente. Pasta e integridade conservam seus contratos separados. O estado aplicado em memória e a persistência no histórico continuam distintos.

NetworkConnections e FirewallProfiles usam o host nomeado de scripts. TCP/UDP mantêm cobertura e orçamento independentes; perfis de ActiveStore conservam tipos e valores CIM antes de adaptações que possam perder nulidade. Estados de perfil não viram decisão sobre tráfego ou interface ativa.

WindowsEventReader é o produtor compartilhado de eventos nativos e da interpretação do XML; SystemEvents e ApplicationFailures consomem seus metadados, sem reinterpretar mensagens localizadas. A seleção de Application usa pares de provedor/ID e uma janela fixa. XML original, datas, campos selecionados e limitações acompanham a projeção, sem uma conclusão causal gerada pelo aplicativo.
## Reinicialização, restauração e BitLocker

As consultas `pending-restart`, `restore-points` e `bitlocker-status` entram no mesmo vínculo nomeado de manutenção, sem execução ao selecionar ou abrir o aplicativo. Os coletores são os donos da interpretação: interface, regras, relatórios e seleção extrativa da IA consomem os fatos publicados, com origem, data e cobertura.

O leitor limitado de valores do Registro é compartilhado com o inventário de software. Ele sonda tamanho e tipo antes de alocar, limita cada valor a 16 KiB e preserva falhas e mudanças entre as chamadas. A interpretação de MULTI_SZ pertence à consulta de reinicialização. O inventário mantém sua interpretação textual existente. Presença desconhecida não vira chave ausente ou valor encontrado.

SystemRestore é uma consulta CIM local de propriedades selecionadas. BitLocker consulta propriedades de identificação e quatro getters fixos de estado; não chama getters de chaves. O transporte comum conserva o catálogo embutido, limites de processo/saída, políticas herdadas e cleanup próprio. Códigos de retorno e resultados parciais são evidências, não conclusões de recuperabilidade ou proteção efetiva.

O sinal COM RebootRequired continua no coletor de histórico WUA; os novos indicadores de Registro não o substituem nem atualizam sua data implicitamente. Ausência de indicadores selecionados não constitui afirmação geral de que o sistema dispensa reinício.

## Investigação de instabilidade (0.9)

InstabilityInvestigationService é o dono do catálogo finito de ferramentas e captura os vínculos antes da execução. Cada ferramenta roda no máximo uma vez, com até quatro passos; decisões desconhecidas ou repetidas falham fechadas. InstabilityServices compõe serviços de diagnóstico e importação, compartilhados pela CLI e Desktop. Nenhuma consulta parte da inicialização ou da seleção de uma aba.

LocalInvestigationPlanner usa o transporte extrativo validado para selecionar uma proposta de próxima consulta, com limite de uma escolha por rodada (ou nenhuma para encerrar). O assistente genérico mantém até seis fatos. Contexto é representado uma vez e a prévia usa o mesmo dono AssistantProtocol.Select para exigir que todas as alternativas e o contexto caibam. O modelo não escolhe comandos, argumentos, paths, fabricantes ou URLs. Decisões terminais também conservam origem e recibo; cancelamento na finalização preserva etapas concluídas como parcial.

InstabilityAssessment deriva hipóteses declaradas de fatos numéricos normalizados. Não reinterpreta XML, HTML ou saída de CDB nem produz um diagnóstico físico livre. IDs e datas das fontes continuam na sessão. O rastro versionado inclui etapas, decisão terminal, hipóteses e referências oficiais. A gravação e a aplicação em memória permanecem fatos distintos; cancelamento não salva a execução como concluída.

WindowsEventReader continua dono dos metadados e da interpretação XML. Restart usa uma QueryList com cinco seletores fixos e uma janela UTC explícita. Correlações temporais ficam separadas de identidade e contadores; duplicatas conhecidas não são contadas novamente. Resultados de memória dependem de CompletionType explícito nos formatos reconhecidos, não de ID isolado.

HardwareProfileCollector preserva classes CIM e limites independentes. MemoryTestReportImporter usa AngleSharp sem loaders/JavaScript e conserva bytes originais antes de interpretar o HTML. FirmwareNotesReader usa GET somente após URL explícita, com host HTTPS exato do catálogo, sem redirects/cookies/credentials; o DOM não carrega recursos. Essa leitura não entra no catálogo automático do modelo. Termos encontrados são pistas textuais, sem confirmar modelo, revisão, imagem ou versão disponível.

CrashDumpCollector usa apenas arquivos escolhidos, argumentos fixos, bytes/codificação declarados e supervisão por Job. Prepara a pasta de recibos somente depois de validar arquivos e digests. Pastas novas são criadas por nível sob leases; nenhum dado desconhecido é removido. Cleanup não confirmado conserva ownership. Nenhum attach, symbol server ou aplicação de firmware integra esse fluxo.
## Navegação e requisitos da interface (0.9.1)

WorkspacePage identifica as nove telas da GUI. A navegação altera apenas a seleção; consultas e operações continuam em comandos explícitos e nos mesmos serviços de Application. A Window possui cabeçalho, status/progresso e ações globais para que a árvore de acessibilidade não dependa dos filhos internos de um TabControl. O TabControl possui somente navegação e conteúdo da página.

MainViewModel.Workspace deriva instruções a partir da sessão aberta, dos caminhos informados e dos planos já existentes. Não lê arquivos nem valida ferramentas por navegação. “Caminhos informados” permanece separado de compatibilidade, permissões e validação no serviço executor. Campos vazios ou formados apenas por espaços não habilitam a IA.

A busca apresenta as mesmas MaintenanceQueryChoice, ignorando caixa e acentos em português. O vínculo continua sendo o MaintenanceQuery nomeado. Sem resultado visível, o comando não executa uma seleção anterior oculta; selecionar ou filtrar não coleta dados. Abrir uma sessão do histórico leva ao Diagnóstico sem renovar timestamps ou consultar fontes.

Detalhes de evidências começam recolhidos; mensagens de limitações permanecem visíveis. Backup, restauração, integridade, consultas de firmware, importações e seus limites de autoridade conservam os contratos anteriores. O perfil segue explícito: não há nova persistência automática de configuração.
