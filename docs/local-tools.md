# Ferramentas locais do opTImus 0.9.1

As integrações são ações explícitas, com executáveis e arquivos escolhidos pelo usuário. Nenhuma ferramenta ou modelo é instalado ou baixado ao abrir o aplicativo. Um arquivo encontrado não implica que esteja homologado para todos os equipamentos.

## Configuração

Em **Configuração**, selecione `llama-completion.exe`, um modelo GGUF local, `restic.exe` e, opcionalmente, `smartctl.exe`. Mantenha as DLLs do pacote llama.cpp ao lado do executável. A integração usa o frontend raw de completion; `llama-cli.exe` é uma interface humana e não é um substituto automaticamente aceito.

Perfis JSON podem ser importados ou exportados para arquivos novos. O documento precisa de `kind: optimus.runtime-profile`, `version: 1` e um objeto `profile`. O perfil guarda caminhos/digests, não o conteúdo do arquivo de senha de backup. Campos duplicados ou desconhecidos são rejeitados. Ao selecionar o motor manualmente sem digest configurado, o serviço calcula seu SHA-256 e vincula esse conteúdo ao executor; isso identifica o arquivo escolhido, sem atestar a confiabilidade de um binário arbitrário. Os caminhos preservam seus valores de origem; o adaptador responsável valida identidade e aplicabilidade antes de executar.

O perfil piloto de desenvolvimento fica fora do Git, em `.tools/verification/iteration-02/runtime-profile-raw.json`. Ele referencia os artefatos obtidos e verificados nesta máquina; não é uma configuração portátil para copiar indiscriminadamente para outro computador.

```powershell
# Interface com o perfil local explicitamente escolhido.
.\scripts\dev.ps1 -Task run -ProfilePath "$PWD\.tools\verification\iteration-02\runtime-profile-raw.json"

# Conferir a configuração sem executar as ferramentas.
.\.tools\dotnet\dotnet.exe .\src\Optimus.Cli\bin\Debug\net10.0-windows\Optimus.Cli.dll profile-check --profile "$PWD\.tools\verification\iteration-02\runtime-profile-raw.json"
```

## IA

Execute um diagnóstico ou inventário e abra **Assistente IA**. A seleção carrega o modelo local sob execução supervisionada; a coleta básica não o carrega. O protocolo v3 aceita somente selectedFacts com até seis IDs curtos de fatos entregues. O aplicativo apresenta rótulo, valor/unidade, chave e data da fonte; não mostra um resumo livre gerado pelo modelo. Seleções inventadas, duplicadas ou não entregues e campos de texto livre são recusados. A pré-seleção corresponde palavras inteiras em chaves/rótulos, ignorando acentos e caixa; não busca valores ou interpreta sinônimos/negações. Havendo correspondência, apenas esses candidatos são entregues; sem correspondência, o fallback amplo fica explícito nas limitações. A relevância da seleção é experimental. Não recebe ferramenta de terminal, ações livres, privilégios ou acesso à rede pelo adaptador. O modo offline do motor não equivale a uma sandbox de rede do Windows.

O piloto usa Qwen2.5-1.5B-Instruct Q4_K_M. É um modelo pequeno experimental, cuja qualidade em português precisa de avaliação mais ampla. Uma seleção com IDs válidos ainda pode deixar de lado fatos relevantes. Não é um laudo de hardware nem substitui confirmação humana.

O frontend de completion aplica o template Instruct do próprio GGUF em modo conversation/single-turn. O transporte aceita um único documento JSON; apenas o executável b11669 com digest fixado admite seu marcador oficial de término depois desse documento. Banners, JSON truncado, documentos extras e marcadores em posições inesperadas são recusados, sem recuperação arbitrária do texto.

Até 100 diretórios de chamadas ficam retidos por raiz de evidências, com reserva exclusiva de capacidade entre instâncias. Request, prompt, schema e saída são preservados para reprodução; não há exclusão automática. Logs e nomes da entrada são dados sem autoridade. XML bruto volumoso fica na evidência do diagnóstico e pode ser omitido da seleção do modelo, com a omissão registrada.

```powershell
$cli = "$PWD\src\Optimus.Cli\bin\Debug\net10.0-windows\Optimus.Cli.dll"
$profile = "$PWD\.tools\verification\iteration-02\runtime-profile-raw.json"
.\.tools\dotnet\dotnet.exe $cli assist --profile $profile --data-dir "$PWD\.tools\meus-testes" --question "Quais dados de memória foram observados?"
```

Sem `--session ID`, o comando cria um diagnóstico consultivo para a análise. Com ID, usa a sessão salva e conserva suas datas. Não apresenta dados históricos como nova medição.

## Inventário local

Na aba **Inventário**, solicite a consulta de rede, alimentação/bateria, registros de programas e Run/RunOnce. Não se altera a inicialização do Windows nem se executam comandos registrados. Consultar endereços/gateways/DNS é leitura da configuração local: não há ping, resolução de nomes ou tráfego de teste. Ausência de bateria não é erro nem avaliação de desgaste.

```powershell
.\scripts\dev.ps1 -Task inventory -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\inventario.json"
# Use o ID devolvido pela coleta para selecionar fatos do inventário salvo.
.\.tools\dotnet\dotnet.exe $cli assist --profile $profile --data-dir "$PWD\.tools\meus-testes" --session "ID_DA_SESSAO" --question "Quais programas foram registrados?"
```

O comando inventory cria uma sessão própria; a GUI incorpora os novos dados conservando as outras fontes e suas datas. O inventário não é iniciado ao abrir o aplicativo ou executar diagnose. As consultas limitam retenção a 64 interfaces e 32 endereços por classe/interface; software tem 500 entradas e orçamento conservador de 1 MiB por coletor, além de 16 KiB por valor. Toda omissão ou falha informa cobertura parcial; o relatório não é uma lista completa quando há limite atingido. Chaves e visões 32/64 são preservadas como identidades distintas. Pastas Startup, tarefas agendadas, serviços, apps portáteis e catálogo completo da Microsoft Store ficam fora desse escopo.

## Backup e restauração

O primeiro recorte aceita repositório local **novo/vazio**, fora da origem. Escolha o arquivo de senha fora da origem e do repositório; mantenha-o privado e recuperável fora do Windows original. A configuração não guarda essa senha. Backup de arquivos não é imagem do sistema, clonagem ou recuperação de um disco fisicamente defeituoso.

A prévia não executa ferramenta nem cria repositório. Na GUI, os botões de execução exigem confirmação do plano. Alterar os parâmetros invalida a prévia. Os planos são de uso único e expiram; validade e identidade são reavaliadas depois da fila de execução.

Sequência: journal durável → init → leitura da configuração criada → backup NDJSON → check com leitura de dados. Snapshot criado, integridade verificada e restauração verificada são estados distintos. Arquivos abertos, VSS, ACL, ADS, EFS e placeholders de nuvem não têm cobertura homologada neste recorte.

Na CLI, o padrão também é somente prévia. `--execute true` é autorização explícita para o plano mostrado naquela chamada, com os parâmetros escolhidos:

```powershell
# Apenas prévia; substitua pelos caminhos que você escolheu.
.\.tools\dotnet\dotnet.exe $cli backup --profile $profile --source "D:\OrigemEscolhida" --repository "E:\NovoRepositorio" --password-file "E:\Chaves\backup.txt"

# Executar depois de revisar os caminhos e consequências.
.\.tools\dotnet\dotnet.exe $cli backup --profile $profile --source "D:\OrigemEscolhida" --repository "E:\NovoRepositorio" --password-file "E:\Chaves\backup.txt" --execute true

# Restauração separada, com ID exato do snapshot e destino que não existe.
.\.tools\dotnet\dotnet.exe $cli restore --profile $profile --repository "E:\NovoRepositorio" --password-file "E:\Chaves\backup.txt" --snapshot "ID_HEXADECIMAL_COMPLETO" --destination "D:\RestauracaoNova" --execute true
```

Cancelamento, timeout e saída parcial preservam recibos e instruções de recuperação. Não há repetição automática, exclusão de locks/snapshots ou prune. Reservas duráveis por destino evitam repetir uma operação cujo resultado ainda precise de reconciliação, inclusive após reinício. A presença de uma reserva não significa por si só que o backup falhou ou ficou pendente; consulte o recibo correspondente. Reconciliação assistida e backup incremental em repositórios existentes são próximos recortes, não funções já habilitadas.

## SMART

Há consulta somente leitura de um dispositivo básico explicitamente escolhido e importação de JSON de até 1 MiB. Não há varredura global, autoteste, habilitação de recurso ou mudança no disco. O parser preserva o JSON original e os códigos de saída como bitmask, incluindo divergências entre processo e documento. Campo ausente não vira estado saudável.

```powershell
.\.tools\dotnet\dotnet.exe $cli smart-read --profile $profile --target "/dev/pd0"
.\.tools\dotnet\dotnet.exe $cli smart-import --input "D:\Relatorios\smartctl.json"
```

Controladoras, USB e permissões podem limitar a consulta. A implementação de parser e fixture não estabelece compatibilidade física; a validação corrente descreve exatamente o que foi executado.

## Proveniência dos artefatos do piloto

| Componente | Versão fixada | Origem e licença declarada |
| --- | --- | --- |
| llama.cpp CPU Windows x64 | v0.7.0, build b11669 indicado pelo release | [Release oficial](https://github.com/ggml-org/llama.cpp/releases/tag/b11669), MIT |
| Modelo GGUF Qwen | revisão `91cad51170dc346986eccefdc2dd33a9da36ead9`, Q4_K_M | [Repositório oficial Qwen](https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF), Apache-2.0 declarada |
| restic | 0.19.1 Windows amd64 | [Release oficial](https://github.com/restic/restic/releases/tag/v0.19.1), BSD-2-Clause declarada |

Os digests e URLs estão em `runtime-assets.json`. São referências de desenvolvimento, não atestado jurídico de distribuição de um produto final. O código próprio ainda precisa da escolha de licença pelo responsável. Antes de distribuir um pacote, preservar as licenças/avisos dos arquivos e dependências efetivamente incluídos.

## Defender, dispositivos e integridade do Windows

Em **Consultas**, as consultas do Microsoft Defender e de dispositivos/drivers são explícitas. O estado reportado não é laudo de segurança ou saúde; ausência, tipos desconhecidos e limites têm cobertura própria. Nenhuma varredura antivírus, atualização de assinatura, instalação ou habilitação de dispositivo é solicitada.

```powershell
.\scripts\dev.ps1 -Task defender-status -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\defender.json"
.\scripts\dev.ps1 -Task device-inventory -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\dispositivos.json"

# Somente prévias: estes comandos não iniciam a verificação.
.\.tools\dotnet\dotnet.exe $cli integrity --operation sfc-verify --data-dir "$PWD\.tools\meus-testes"
.\.tools\dotnet\dotnet.exe $cli integrity --operation dism-check --data-dir "$PWD\.tools\meus-testes"
.\.tools\dotnet\dotnet.exe $cli integrity --operation dism-scan --data-dir "$PWD\.tools\meus-testes"
```

Para executar, a GUI exige confirmação do plano; na CLI, acrescentar --execute true autoriza o plano mostrado naquela chamada. É necessário executar a instância em contexto já elevado e preparar uma nova prévia nesse contexto. O programa não solicita UAC ou reinicia a si mesmo como administrador. As únicas opções são SFC /verifyonly e DISM /CheckHealth ou /ScanHealth; /scannow e /RestoreHealth não pertencem ao catálogo.

A prévia mostra os caminhos exatos, argumentos, SHA-256, prazo, permissões e journal. Ela é de uso único, expira em cinco minutos e é revalidada após a fila. Cancelamento/timeout não desfaz logs nem permite presumir que serviços externos do Windows pararam. Preserve o recibo e os logs antes de decidir repetir; não há retry automático. Até 100 diretórios de operação ficam retidos por raiz, sem limpeza automática.

Defender consulta até 16 propriedades e retém resposta de até 64 KiB; strings têm 512 caracteres. Dispositivos/drivers enumeram até 256 registros por classe, com 2048 caracteres por campo e orçamento de JSON/projeção explícito. Cada consulta recebe sua parte do orçamento. DriverDate aceita a representação textual ou data tipada retornada pelo CIM, conservando tipo, valor e indicação de fuso sem convertê-la em decisão de atualização.

Verificações de integridade capturam até 256 KiB de stdout/stderr combinados e conservam bytes originais. O limite não restringe os logs CBS/DISM gerados pelo Windows. Código zero significa somente encerramento do comando no transporte; a saída permanece sujeita à revisão. A validação 0.4 registra exatamente quais consultas foram executadas e deixa a verificação elevada como não homologada.

Referências primárias: [Get-MpComputerStatus](https://learn.microsoft.com/en-us/powershell/module/defender/get-mpcomputerstatus), [SFC](https://learn.microsoft.com/windows-server/administration/windows-commands/sfc), [opções DISM](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-global-options-for-command-line-syntax?view=windows-11).
## Serviços, atualizações registradas e espaço de pasta

Em **Consultas**, selecione Serviços do Windows e use **Executar consulta selecionada** para ler estado, modo de início e outros campos informados por Win32_Service local. Caminhos registrados são dados, não comandos a executar; o opTImus não para, inicia ou reconfigura serviços. Um serviço parado pode ser esperado.

Histórico de atualizações consulta somente registros locais do Windows Update Agent e seu sinal de reinício. Não procura atualizações disponíveis nem dispara download, instalação ou reinício. Histórico vazio ou uma instalação bem-sucedida no passado não prova que o Windows esteja atualizado; o sinal WUA não cobre todas as causas de reinício pendente.

```powershell
.\scripts\dev.ps1 -Task services -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\servicos.json"
.\scripts\dev.ps1 -Task update-history -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\atualizacoes.json"
.\scripts\dev.ps1 -Task folder-space -FolderPath "D:\PastaEscolhida" -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\espaco.json"
# CLI direta após build:
.\.tools\dotnet\dotnet.exe $cli folder-space --path "D:\PastaEscolhida" --data-dir "$PWD\.tools\meus-testes"
```

A análise de pasta lê metadados e lista até vinte maiores arquivos por tamanho lógico. Não exclui arquivos e não mede espaço recuperável: hardlinks podem contar mais de uma vez, e alocação física, ADS, compressão e deduplicação não são medidos. A observação não é snapshot atômico. Use uma pasta local direta; junctions, reparse points e entradas offline/recall são omitidas ou recusadas, com cobertura informada. O prazo é cooperativo entre chamadas de metadados do Windows.

As consultas têm limites de enumeração, resposta, projeção e profundidade. Consulte a cobertura da evidência antes de interpretar totais ou listas parciais. O JSON original das consultas Windows fica no relatório, com suas datas e identidades; a análise de pasta conserva os maiores arquivos observados e contagens, não um manifesto de todos os arquivos. Nenhuma dessas ações é automática ao abrir o aplicativo.
Limites do recorte 0.5: serviços retêm até 256 registros mais sonda, 256 KiB de resposta e 128 KiB conservadores de projeção; histórico WUA retém até 128 eventos, 256 KiB de resposta e 128000 bytes estimados de projeção. Campos textuais têm 2048 caracteres. O JSON original retém os registros recebidos dentro do orçamento de fonte; a GUI informa quantos foram projetados.

Pasta tem orçamento de 20.000 entradas, 32 níveis incluindo a pasta escolhida e prazo cooperativo de 30 segundos. A vinculação da raiz também limita ancestrais a 32. Até vinte maiores arquivos usam 512 KiB conservadores; ocorrências têm até 32 detalhes/128 KiB, com contadores de omissões. Permissão, arquivo mudando ou orçamento atingido não viram tamanho zero ou completude.

Referências primárias do recorte: [Win32_Service](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-service), [histórico WUA](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-queryhistory), [sinal WUA de reinício](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-isysteminformation-get_rebootrequired), [atributos de arquivo](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants). Consulte a [validação 0.5](validation-0.5.md) para as provas e limitações reais.
## Processos, tarefas agendadas e discos

Em **Consultas**, selecione Processos e memória e use **Executar consulta selecionada** para ler um recorte local de Win32_Process. O working set é o valor reportado por processo naquele momento; não é memória exclusiva recuperável. Os contadores de CPU são cumulativos e permanecem na unidade indicada pelo provedor, sem percentual instantâneo calculado. O aplicativo não coleta CommandLine/ambiente/conteúdo de memória nem encerra processos. PID e parent PID podem ser reutilizados e não provam uma relação ainda válida.

Tarefas agendadas consulta metadados locais, incluindo tarefas ocultas acessíveis dentro dos limites. Não lê definição, ações, argumentos ou credenciais; não executa ou reconfigura tarefas. Horários preservam tipo/fuso informados e sentinelas explícitas, sem inventar que uma tarefa tenha sido executada ou vá executar. Estado e último resultado são campos reportados, sem laudo automático.

Discos e partições usa Win32_DiskDrive e Win32_DiskPartition locais. Modelos, identificadores, capacidades, índices e códigos permanecem observações. O aplicativo não lê setores, não mede desempenho, não formata nem altera partições. Status reportado não substitui SMART ou teste físico. Não há associação automática entre os registros baseada apenas em índices ou nomes.

```powershell
.\scripts\dev.ps1 -Task processes -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\processos.json"
.\scripts\dev.ps1 -Task scheduled-tasks -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\tarefas.json"
.\scripts\dev.ps1 -Task storage -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\armazenamento.json"
```

Listas e totais estão sujeitos a descoberta, permissão, tamanho de resposta e projeção. A fonte original retida continua no JSON do relatório. Nenhuma dessas consultas é automática ao abrir o programa ou executar diagnose. A [validação 0.6](validation-0.6.md) registra as consultas realmente executadas e suas limitações.
Limites do recorte 0.6: processos retêm até 256 registros mais sonda e usam projeção conservadora de 128000 bytes, ordenando somente os registros recebidos por working set conhecido descendente. Desconhecidos ficam ao fim; empate conserva índice original. A ordem do JSON não é alterada e não se afirma ranking de todos os processos.

Tarefas têm até 128 registros, 32 tentativas de pastas, 16 níveis e 128000 bytes de projeção. GetTasks/GetFolders podem materializar coleções completas no serviço do Windows: os limites do cliente não limitam toda atividade/alocação do serviço externo. Discos e partições têm limites independentes de 32/128 registros mais sondas e 64000 bytes de projeção por fonte. As três respostas são limitadas a 256 KiB e os campos textuais a 2048 caracteres. Omissões e dados desconhecidos permanecem explícitos.

Fontes primárias do recorte: [Win32_Process](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-process), [Win32_DiskDrive](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-diskdrive), [Win32_DiskPartition](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-diskpartition), [TaskService.Connect](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskservice-connect) e [GetTasks](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-gettasks).
## Seletor de consultas, conexões, Firewall e falhas de aplicativos

Em **Consultas**, busque uma ferramenta por nome ou assunto, selecione o resultado e use **Executar consulta selecionada**. O texto abaixo informa o escopo. A consulta não inicia ao mudar a seleção ou abrir o programa. Pasta escolhida e verificações de integridade continuam com controles próprios. O vínculo com a fonte é capturado antes da coleta, sem depender da posição no menu.

Conexões e portas TCP/UDP lê tabelas locais selecionadas. Não envia pacotes, resolve DNS, conecta a destinos ou encerra conexões. UDP não tem endereço remoto nessa seleção. Endereços, portas, códigos/nomes de estado e PIDs são preservados; PIDs podem ser reutilizados e não estabelecem ownership. Escuta/conexão não é classificada automaticamente como ameaça ou saúde.

Perfis do Firewall consulta somente campos selecionados da política ActiveStore, sem ler regras ou alterar proteções. Enabled, NotConfigured, ações padrão e valores desconhecidos são distintos. O coletor preserva a nulidade CIM antes de getters adaptados do PowerShell. Perfil reportado não demonstra qual perfil está em uso numa interface ou como todo tráfego é tratado.

Falhas de aplicativos consulta pares selecionados de IDs/provedores do log Application nos últimos sete dias. Retém o XML original e metadados dentro dos limites, sem ler dumps, apagar eventos ou modificar WER. Um registro histórico não identifica por si só a causa ou a condição atual de um aplicativo.

```powershell
.\scripts\dev.ps1 -Task connections -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\conexoes.json"
.\scripts\dev.ps1 -Task firewall -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\firewall.json"
.\scripts\dev.ps1 -Task application-failures -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\falhas-aplicativos.json"
```

Consulte a cobertura por fonte, incluindo recusas de permissão, limites e campos desconhecidos. Os relatórios conservam os dados retidos mesmo quando nem todos os registros cabem na projeção da interface. A [validação 0.7](validation-0.7.md) registra exatamente as provas nativas realizadas e suas limitações.
Limites 0.7: TCP 256 + sonda e UDP 128 + sonda; payload de 110.000 bytes e projeção de 64.000 bytes por fonte, resposta de 256 KiB e textos de 512 caracteres. Firewall: 8 perfis + sonda, 8 campos, payload de 48.000 bytes, resposta de 64 KiB, projeção de 32.000 bytes e textos de 512 caracteres. A interface informa contagens retidas/projetadas; o JSON original retém os registros recebidos dentro do limite da fonte.

Application seleciona Application Error/1000, Windows Error Reporting/1001 e Application Hang/1002 nos últimos sete dias. Até vinte registros mais sonda, 32 KiB UTF-8 de XML por registro, 32 campos Data e orçamento aproximado de retenção de 256 KiB; o arquivo de relatório pode ser maior pelo envelope/indentação. Registros não são incidentes únicos nem prova causal. Timeout/prazo/cancelamento são cooperativos entre chamadas de leitura, e ToXml pode alocar antes do limite. SystemEvents compartilha o produtor de XML, conservando sua janela e seu limite anteriores.

Fontes: [NetTCPIP TCP](https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-nettcpconnection), [NetTCPIP UDP](https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-netudpendpoint), [perfis Firewall](https://learn.microsoft.com/en-us/powershell/module/netsecurity/get-netfirewallprofile) e [EventLogReader](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogreader.readevent?view=windowsdesktop-10.0).
## Reinicialização, restauração e criptografia (0.8)

Em **Consultas**, escolha **Indicadores de reinicialização**, **Pontos de restauração** ou **Estado do BitLocker** e clique em **Executar consulta selecionada**. A seleção não inicia a consulta. As evidências entram na sessão, no histórico e nos relatórios; a IA local pode selecionar seus fatos pelo fluxo extrativo existente, sem receber comandos de manutenção.

```powershell
.\scripts\dev.ps1 -Task pending-restart -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\reinicializacao.json"
.\scripts\dev.ps1 -Task restore-points -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\restauracao.json"
.\scripts\dev.ps1 -Task bitlocker-status -DataDirectory "$PWD\.tools\meus-testes" -OutputPath "$PWD\artifacts\bitlocker.json"
```

Indicadores selecionados ausentes não comprovam que todo o Windows dispense reinício. Pontos listados não comprovam recuperação viável. BitLocker sem acesso ou suporte permanece indisponível, sem assumir que a proteção esteja desligada. Não há coleta de senhas ou chaves de recuperação. As consultas não reiniciam, restauram ou alteram configurações. Os detalhes e as provas do ambiente corrente estão no [escopo 0.8](scope-0.8.md) e na [validação 0.8](validation-0.8.md).

Referências: [SystemRestore](https://learn.microsoft.com/en-us/windows/win32/sr/systemrestore), [Win32_EncryptableVolume](https://learn.microsoft.com/en-us/windows/win32/secprov/win32-encryptablevolume) e [operações de arquivo adiadas](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexa). Os marcadores CBS e WindowsUpdate são indicadores convencionais selecionados; o coletor não anuncia cobertura universal de todos os instaladores ou mecanismos de reinício.
## Diagnóstico de instabilidade (0.9)

Na aba **Instabilidade**, informe a pergunta e o período (1 a 365 dias). **Investigar** consulta histórico selecionado de reinicializações/WHEA, resultados de memória já registrados e hardware. A IA é opcional e desativada por padrão. Quando ativada, usa executável/modelo de Configuração para escolher uma próxima consulta permitida por rodada; a relevância da escolha continua experimental. Hipóteses têm fontes e próximos testes, sem confirmar a causa física.

```powershell
.\scripts\dev.ps1 -Task restart-history -Days 30 -OutputPath "$PWD\artifacts\reinicios.json"
.\scripts\dev.ps1 -Task memory-results -Days 30 -OutputPath "$PWD\artifacts\memoria-windows.json"
.\scripts\dev.ps1 -Task hardware-profile -OutputPath "$PWD\artifacts\hardware.json"
.\scripts\dev.ps1 -Task investigate -Days 30 -Question "O computador reinicia sozinho" -OutputPath "$PWD\artifacts\investigacao.json"
.\scripts\dev.ps1 -Task investigate -UseInvestigationAi -ProfilePath "D:\Perfis\ferramentas.json" -MaximumSteps 1 -OutputPath "$PWD\artifacts\primeiro-passo.json"
.\scripts\dev.ps1 -Task memory-import -InputPath "D:\Relatorios\MemTest86.html" -OutputPath "$PWD\artifacts\memtest.json"
```

O importador lê um HTML existente do MemTest86; não executa o teste. Conserva bytes/digest/codificação, resultados duplicados e linhas de erro. Last 10 Errors é uma amostra. Posição de bit é zero-based dentro de esperado XOR recebido, sem localizar automaticamente um pente. Dados antigos ou sem fuso permanecem com essa limitação.

Para analisar um dump, selecione **cdb.exe**, o arquivo **.dmp** e uma pasta única de símbolos locais. O app não instala o debugger nem baixa símbolos. No perfil v1, os campos opcionais novos são `cdbExecutablePath`, `symbolDirectory` e `cdbSha256`; perfis antigos continuam aceitos. O arquivo original do perfil nunca é substituído pela exportação.

```powershell
.\scripts\dev.ps1 -Task dump-analyze -ProfilePath "D:\Perfis\debugger.json" -DumpPath "D:\Dumps\selecionado.dmp" -OutputPath "$PWD\artifacts\dump.json"
```

A saída e os recibos indicam o que o CDB devolveu; exit zero não comprova símbolos completos nem RAM defeituosa. A análise usa arquivos escolhidos e não se conecta a um processo ativo. Dumps podem conter dados pessoais; este fluxo permanece local e não envia o arquivo.

Os links de suporte vêm de fabricante/modelo observados. Para ler uma página, selecione o fabricante observado, cole a URL exata e clique em **Consultar página oficial (internet)**. É uma ação explícita, fora do roteiro automático da IA. O leitor faz um GET ao host exato HTTPS do catálogo; não envia inventário/logs/dumps, não segue redirects e não aplica uma imagem de BIOS. Se o site exigir outro endereço regional, informe a URL oficial desejada explicitamente; respostas 403/redirect/charset desconhecido ficam como limitações.

```powershell
.\scripts\dev.ps1 -Task firmware-notes -FirmwareUrl "https://www.msi.com/support" -Manufacturer "MSI" -OutputPath "$PWD\artifacts\pagina-oficial.json"
```

O exemplo aponta uma página de suporte, sem afirmar que ela contenha notas aplicáveis ao computador. Memória/estabilidade/TPM encontrados no texto são heurísticas; confira modelo, revisão, versão e procedimento do fabricante. Nenhum teste que reinicia, mascaramento de RAM, alteração de BIOS ou instalação é iniciado pelo módulo.

Consulte o [escopo 0.9](scope-0.9.md) e a [validação 0.9](validation-0.9.md). Fontes técnicas: [eventos de reinicialização](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart), [CDB](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/cdb-command-line-options) e [relatório oficial de exemplo MemTest86](https://www.memtest86.com/MemTest86Report.html).
## Navegação e prévias da interface 0.9.1

O [guia da interface](interface.md) descreve o primeiro uso, os requisitos e as opções avançadas. SMART passou para Consultas. Os caminhos da IA, restic e smartctl ficam em Configuração; CDB e símbolos permanecem nas opções avançadas de Instabilidade. O perfil continua sendo importado explicitamente.

O Desktop aceita `--preview-page Home|Diagnostics|Instability|Maintenance|Inventory|Backup|Assistant|History|Settings` junto de `--render-preview`. `--preview-empty true` permite conferir o primeiro uso sem executar o diagnóstico ou ferramentas; não pode ser combinado com opções que pedem coletas/análises. O contrato anterior de `--preview-tab 0..7` é preservado por mapeamento explícito; não combine os dois seletores. Essas opções geram uma imagem nova e encerram o processo.
