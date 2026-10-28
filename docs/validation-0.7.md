# Validação do opTImus 0.7

Data do cliente: **28/10/2026**. Windows x64, SDK isolado .NET 10.0.403 e runtime 10.0.14. Três frentes de agentes implementaram conexões, perfis do Firewall e eventos Application. O coordenador integrou o seletor e os fluxos compartilhados, revisou e executou os checks do conjunto. Trabalho local, sem commit/push, CI remoto ou distribuição pública.

## Build, testes e revisão

- Restauração `--locked-mode` antes e depois do publish: aprovada, sem novos pacotes.
- Formatação e `dotnet format --no-restore --verify-no-changes`: aprovados.
- Build Release: zero avisos e zero erros, 12,26 s.
- Testes focados: **139 aprovados**. Suíte Release: **647 aprovados, zero falhas e zero ignorados**, 57,4742 s.
- Publish Release Windows x64 self-contained: aprovado.

A primeira compilação encontrou um import ausente em ApplicationFailuresTests; foi corrigido antes dos testes. Logs privados em `.tools/verification/iteration-07` incluem o erro inicial, `build-debug.log`, `tests-focused.log`, `format-check.log`, `build-release.log`, `tests-release.log`, `publish.log`, `restore-locked.log` e `restore-after-publish.log`.

O seletor substituiu sete comandos de botões de consulta por um comando com identidades nomeadas. MaintenanceQueries interpreta os tokens CLI; MaintenanceServices.ResolveQuery vincula consulta, serviço e CollectorId. Não se usa posição no menu ou texto exibido para selecionar operações. Testes cobrem os dez vínculos, tokens inválidos, serviço ausente, seleção alterada durante await, busy e escopo divergente.

A revisão encontrou uma lacuna: a CLI desembrulhava o serviço e ignorava o CollectorId esperado. MaintenanceQueryBinding.RunAsync agora valida cobertura antes da publicação, usando o mesmo dono de normalização ordinal de EvidenceSessionUpdates. Divergência retorna erro com a sessão real preservada como valor, sem salvá-la/exportá-la como consulta válida. GUI e CLI usam esse fluxo. Métodos nomeados de preview continuam delegando ao vínculo; comandos redundantes antigos foram removidos.

## Consultas reais Release

Antes da prova final houve uma execução Debug para conferir os provedores. Os valores abaixo são somente da execução **Release final**, com relatórios e histórico novos:

| Consulta | Resultado | Fatos | Issues | Parede CLI | CPU CLI | Parede job | CPU família job | Pico de commit da família |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP/UDP | Partial, saída 3 | 231 | 4 | 2,736 s | 0,438 s | 1,600 s | 1,391 s | 131.698.688 bytes |
| Firewall | Complete, saída 0 | 70 | 0 | 1,875 s | 0,406 s | 1,424 s | 1,359 s | 130.002.944 bytes |
| Application | Partial, saída 3 | 946 | 1 | 0,619 s | 0,266 s | Não se aplica | Não medido separadamente | Não medido |

TCP: 257 enumerados incluindo sonda, 256 retidos no JSON original e 11 projetados. UDP: 64 retidos, 26 projetados. As quatro issues informam enumeração TCP e projeções limitadas. Relatório de 165.393 bytes. PIDs, endereços e estados são observações; não foram resolvidos como ownership ou classificados como ameaça/saúde.

Firewall: três perfis ActiveStore examinados, retidos e projetados, relatório de 28.281 bytes. Nome, Enabled e ações padrão conservaram os tipos/valores selecionados. Complete descreve conclusão da consulta; não comprova interface ativa, regras aplicadas a um pacote ou eficácia de proteção.

Application: 18 registros examinados, 17 retidos; a retenção atingiu o orçamento de payload e a cobertura ficou parcial. Relatório de 285.402 bytes. Os 256 KiB são o orçamento de retenção/projeção de fatos, com reserva para metadados, não teto do arquivo JSON completo: envelope, indentação e serialização da sessão expandem o relatório. Nenhum prefixo de XML ou valor truncado foi apresentado como fonte completa.

Recibos: `native-final-validation.json`, `connections-final.json`, `firewall-final.json`, `application-failures-final.json`. Flags `--execute true` foram rejeitadas nos três comandos antes de criar armazenamento, com saída 1.

## Proveniência conferida separadamente

Um verificador separado comparou projeções com as fontes retidas, sem atualizar as medições do Windows:

- 37 linhas de endpoints: endereços, portas, PIDs e códigos TCP coincidiram com os registros originais.
- 24 campos dos três perfis: valores exibidos coincidiram com as representações tipadas preservadas.
- 17 XMLs de eventos: provedor, ID e SystemTime coincidiram; 391 campos Data projetados coincidiram com seus valores originais e posições.

O verificador usa `ConvertFrom-Json -DateKind String`: a conversão automática de datas do PowerShell produziu uma divergência aparente na primeira tentativa; mantendo strings originais, a comparação passou. Isso foi um problema do verificador, sem alteração do produto ou da fonte. Recibo: `independent-final-validation.json`.

## Limites específicos e correções de origem

Conexões usam consultas locais NetTCPIP, sem CimSession remota, envio de pacotes, DNS, teste de conectividade ou encerramento. TCP tem limite de 256 registros mais sonda e UDP de 128 mais sonda; payload de 110.000 bytes e projeção conservadora de 64.000 bytes por fonte, resposta total de 256 KiB e textos de 512 caracteres. UDP não recebe endereço remoto inventado. Estado enumerado preserva tipo/código/nome; o nome não determina identidade ou ação.

Firewall usa oito campos, até oito perfis mais sonda, payload de 48.000 bytes, resposta de 64 KiB e projeção conservadora de 32.000 bytes; textos de 512 caracteres. A inspeção do módulo local identificou um getter GpoBoolean que soma zero ao valor CIM, podendo converter nulo em False. O produtor verifica o CIM antes do getter, preservando ausência, tipo, código, nome e valor original. Os casos nulos/NotConfigured/divergentes foram testados com fixtures; a máquina real apresentou os três perfis sem omissões. Não foram alteradas regras ou proteções.

WindowsEventReader centraliza leitura nativa e interpretação XML para SystemEvents e ApplicationFailures. O novo coletor usa os pares Application Error/1000, Windows Error Reporting/1001 e Application Hang/1002 nos últimos sete dias. ID isolado não é classificação universal; um registro WER não é necessariamente um incidente único nem prova causal. Não se leem dumps ou mensagens localizadas para produzir diagnóstico.

Eventos: vinte registros mais sonda, XML de até 32 KiB UTF-8 por registro, até 32 campos Data e orçamento aproximado de 256 KiB. DTD é proibido e XmlResolver nulo. Datas vêm de SystemTime com fuso explícito, sem converter um DateTime local desconhecido. A sonda verifica só presença, sem getters/XML desnecessários. SystemEvents conserva limite dez/janela três dias/keys legadas; agora informa omissão por tamanho e tamanho original conhecido. Os testes incluem compatibilidade, nulidade temporal, duplicações, XML inválido/DTD, limites e cancelamento.

ReadEvent e ToXml podem trabalhar/alocar antes dos limites de retenção. Prazo nativo de 30 segundos e cancelamento são cooperativos entre chamadas; ReadEvent solicita timeout de dois segundos. EOF e erro/timeout não são tratados como a mesma condição. Não foi forçado timeout/erro real do serviço Event Log; fixtures e inspeção do produtor não substituem essa cobertura.

## Executável e interface

O executável `artifacts/app/opTImus.exe`, versão **0.7.0.0**, iniciou com DOTNET_ROOT e DOTNET_ROOT_X64 ausentes. Runtimeconfig inclui Microsoft.NETCore.App e Microsoft.WindowsDesktop.App 10.0.14. A tela executou explicitamente as três novas consultas pelo seletor, terminou com saída 0 em 8,746 s e CPU do processo de 4,391 s. A releitura confirmou quatro sessões salvas: diagnóstico básico e três consultas. Nenhum journal/verificação SFC/DISM foi iniciado.

Imagem real inspecionada: `optimus-0.7-maintenance-final.png`. Recibos: `desktop-final-validation.json`, `desktop-history-final.json`. O seletor e sua descrição aparecem corretamente. Renderização e testes do view model não substituem navegação manual prolongada, teclado, leitor de tela ou piloto em outras máquinas.

SHA-256 do launcher: `362B34BA135C27FCCE1035369B3F6E0D935B9F2F6C21611B73366C4A1C57022D`. Seu digest sozinho não autentica as DLLs/runtime; manifestos privados registram fontes finais e toda a pasta publicada. O sufixo Git identifica o checkout-base, sem representar o diff local não commitado. A pasta ainda não é instalador nem distribuição assinada.

## Custos e cobertura restante

Métricas de job referem-se à família PowerShell, sem incluir todo o custo de serviços externos do Windows. Application roda no processo CLI e registrou 0,095 s para a coleta interna; não há job separado. Pico de working set do processo principal, pico global, tokens dos agentes e custos totais da tarefa permanecem não medidos. Commit e working set são medidas distintas e não devem ser somadas como memória simultânea.

Cache frio/quente e cargas externas não foram controlados; Debug e Release não são uma comparação equivalente de desempenho. Não havia ferramentas equivalentes na versão anterior para comparar. Os números são observações funcionais, sem alegação de ganho geral de velocidade ou tokens.

Regressões de IA, backup, SMART e manutenção anterior permaneceram na suíte. As provas reais anteriores continuam históricas; não se homologaram novamente modelo, restic, SMART físico ou verificações elevadas SFC/DISM. Não houve alteração de Firewall, proteção, rede, processos, tarefas ou discos solicitada pelo código das consultas; não se capturou independentemente toda atividade de serviços/rede do Windows. Dados e recibos ficam locais e ignorados pelo Git, sem commit/push.

Referências primárias: [Get-NetTCPConnection](https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-nettcpconnection), [Get-NetUDPEndpoint](https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-netudpendpoint), [Get-NetFirewallProfile](https://learn.microsoft.com/en-us/powershell/module/netsecurity/get-netfirewallprofile), [ReadEvent](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogreader.readevent?view=windowsdesktop-10.0), [EvtNext](https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtnext).
