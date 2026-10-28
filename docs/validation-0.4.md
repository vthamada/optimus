# Validação do opTImus 0.4

Data: 27/10/2026. Ambiente: Windows x64 build 26300, SDK isolado .NET 10.0.403, runtime 10.0.14. Implementação local em três frentes de agentes, integrada e verificada pelo coordenador. Sem commit/push, CI remoto ou publicação nesta rodada.

## Build e testes

- `dotnet format --no-restore --verify-no-changes`: aprovado.
- Restauração em `--locked-mode`, antes e depois do publish: aprovada; nenhum pacote novo.
- Build Release da solução: zero avisos e zero erros, 15,52 s.
- Testes Release: **332 aprovados, zero falhas e zero ignorados**, 55,1650 s.
- Publish Release Windows x64 self-contained: aprovado.

Os testes incluem contratos de Defender/dispositivos, datas CIM tipadas e fuso desconhecido, limites independentes de projeção, transportes UTF-16LE/OEM e bytes originais, saída malformada, cancelamento e limites combinados. O serviço de integridade tem 25 casos sobre confirmação, identidade/digest, expiração após fila, concorrência, journal anterior ao comando, falhas e resultado incerto. A GUI cobre seleção alterada, contexto sem elevação, preservação de recibo e falha de persistência após resultado aplicado. Regressões anteriores permanecem na suíte.

Logs privados: `.tools/verification/iteration-04/format-check.log`, `restore-locked.log`, `build-release.log`, `tests-release.log`, `publish.log` e `restore-after-publish.log`.

## Consultas nativas finais

Consultas executadas pela CLI Release, com relatórios novos e histórico isolado. As métricas abaixo são observações desta máquina, sem controle de cache ou carga externa.

| Consulta | Resultado | Fatos | Parede CLI | CPU processo CLI | JSON |
| --- | --- | ---: | ---: | ---: | ---: |
| Microsoft Defender | Complete, saída 0, zero issues | 21 | 2,568 s | 0,469 s | 7.325 bytes |
| Dispositivos/drivers | Partial, saída 3, duas issues de limite | 468 | 4,254 s | 0,766 s | 287.321 bytes |

Dispositivos: 174 examinados e retidos no JSON original, 33 projetados em fatos. Drivers: 184 examinados e retidos, 22 projetados. O orçamento conservador de projeção é 128.000 bytes por consulta; as duas omissões de projeção permanecem explícitas. Esses números não significam inventário completo do equipamento.

A primeira prova detectou que DriverDate chega como System.DateTime no CIM e que um orçamento compartilhado prejudicava a projeção dos drivers. A correção foi feita na representação produtora: o envelope conserva valor/tipo/kind sem inventar fuso, e cada consulta tem seu orçamento. A prova final e os testes confirmam os formatos selecionados; não validam relógio do fabricante, saúde física, necessidade de atualização nem assinatura criptográfica dos drivers.

Recibos privados: `native-final-validation.json`, `defender-status-final.json` e `device-inventory-final.json`. As consultas usam catálogos fixos e não solicitam scan antivírus, alteração de proteção, instalação ou habilitação de dispositivos. Não se fez captura independente de toda atividade dos serviços do Windows.

## Integridade: prévias e limites da prova

As três prévias nativas passaram: `sfc-verify`, `dism-check` e `dism-scan`, com argumentos fixos, SHA-256 e contexto sem elevação. Não criaram diretório de operações. A GUI manteve a execução desabilitada nesse contexto.

**SFC /verifyonly e DISM /CheckHealth ou /ScanHealth não foram executados nesta rodada.** A execução elevada permanece sem homologação nativa. Fixtures não comprovam comportamento real de verificações prolongadas, permissões elevadas ou serviços externos.

Somente a ajuda dos binários foi chamada para observar o transporte: SFC /? retornou 1 e 216 bytes UTF-16LE; DISM /English /? retornou 740 e 122 bytes. Ambos informaram necessidade de elevação. Os bytes originais e o recibo `native-help-transport.json` foram preservados localmente.

O catálogo não inclui reparos. O Windows pode gravar logs CBS/DISM e temporários durante verificações. Cancelar o Job próprio não comprova que serviços externos pararam. Saída zero informa término do comando, sem laudo de integridade; resultado incerto não autoriza repetição automática.

## Executável e interface

O executável `artifacts/app/opTImus.exe`, versão 0.4.0.0, foi iniciado com DOTNET_ROOT e DOTNET_ROOT_X64 ausentes. O runtimeconfig informa Microsoft.NETCore.App e Microsoft.WindowsDesktop.App 10.0.14 incluídos.

A renderização real da aba Manutenção consultou Defender/dispositivos e preparou somente a prévia SFC. Encerrou com saída 0 em 9,569 s, CPU do processo 4,563 s. A imagem `optimus-0.4-maintenance.png` foi inspecionada; controles, prévia e bloqueio por falta de elevação aparecem na tela. Recibo: `desktop-final-validation.json`. Não houve verificação de integridade nem journal de execução.

SHA-256 do launcher: `7F9D96184C5CCCAEDC5671F50C5119BB21C2294757106032C6B60492FA5351A5`. Esse digest sozinho não autentica as DLLs/runtime; manifestos privados registram a pasta publicada e as fontes finais. O sufixo Git da versão identifica o checkout-base, sem representar o diff local não commitado.

Renderização não substitui navegação manual prolongada, teclado, leitor de tela ou piloto em outras máquinas. A pasta publicada ainda não é instalador nem distribuição assinada.

## Custos e cobertura preservada

CPU acima refere-se apenas ao processo CLI/GUI. CPU e pico de memória de toda a família PowerShell não foram retidos nessas consultas; não foram medidos pico global, tokens dos agentes ou custos totais da tarefa. Não houve inferência de modelo nesses novos comandos. Cache frio/quente e comparação de tarefas equivalentes não foram controlados; estes dados não constituem benchmark.

IA, backup/restauração e inventário anterior continuam cobertos pelas regressões. As provas reais das versões 0.2/0.3 permanecem históricas; modelo, restic e SMART físico não foram homologados novamente nesta rodada. Binários, modelos, dados do equipamento, bancos, journals e recibos continuam locais e ignorados pelo Git.
