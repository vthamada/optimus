> Interface atual: [opTImus 0.9.1](validation-ui-0.9.1.md); coletores e IA: [opTImus 0.9](validation-0.9.md). O conteúdo abaixo preserva a prova histórica da primeira versão.

# Validação do protótipo 0.1

Data: 27/10/2026. Estado: primeiro recorte consultivo implementado e verificado localmente, ainda sem commit/push da implementação, instalador ou assinatura de distribuição.

Ambiente selecionado para reprodução: Windows x64 build 26300, SDK isolado 10.0.403, runtime 10.0.14. O SDK foi obtido no canal oficial da Microsoft e teve SHA-512 conferido antes da extração. Não há SDK global instalado neste ambiente.

## Resultados executados

| Verificação | Evidência | Resultado |
| --- | --- | --- |
| Restauração e lockfiles | `dotnet restore Optimus.slnx --locked-mode` | Aprovada; repetida depois de publish para confirmar que o contrato de RID permanece estável |
| Build Release integrado | `dotnet build Optimus.slnx --configuration Release --no-restore --disable-build-servers` | Aprovado, 0 avisos e 0 erros |
| Testes Release | `dotnet test tests/Optimus.Tests/Optimus.Tests.csproj --configuration Release --no-build --no-restore` | 27 aprovados, 0 falhas e 0 ignorados |
| Formatação | `scripts/dev.ps1 -Task format` | Aprovada sobre a fonte final |
| Dependências | `dotnet list Optimus.slnx package --vulnerable --include-transitive --format json --no-restore` | Nenhuma vulnerabilidade listada pela fonte NuGet consultada; não substitui revisão de segurança |
| Diagnóstico CLI real | Release, dados/relatórios isolados em `.tools/verification` | Sistema, memória, volumes C e D e dez eventos do log System coletados; estado Partial, código 3, porque o limite de eventos foi atingido |
| Relatórios reais | JSON completo e HTML em destinos novos | Gerados; testes também verificaram escape de HTML e preservação de destino existente |
| Histórico SQLite real | Rodadas e testes com banco local | Round-trip, referências, Unicode, versões desconhecidas, banco estrangeiro e metadados malformados verificados |
| Publicação local | `scripts/dev.ps1 -Task publish` | Pasta executável self-contained gerada; não equivale a instalador nem a publicação remota |
| Inicialização do executável publicado | `artifacts/app/opTImus.exe --render-preview ... --data-dir ...`, com DOTNET_ROOT removido do processo filho | Saída 0, imagem WPF gerada com dados reais e stderr vazio |
| Isolamento dos scripts | Bootstrap com SDK já presente e execução de format, comparando as variáveis selecionadas antes/depois | Aprovado; SDK não reinstalado e ambiente do chamador preservado, inclusive variáveis ausentes |
| Higiene do repositório | `git diff --check`, ignore de `.tools` e `artifacts` | Fontes separadas de SDK, executáveis, bancos, dumps, modelos e evidência local |

A rodada de testes expôs erros reais na primeira implementação do armazenamento: `InvalidDataException` não estava incluída na conversão de erros esperados. A fronteira foi corrigida e as quatro falhas desapareceram; os testes foram mantidos. A revisão independente também levou a inicialização atômica de banco novo, validação de nulos e metadados, limite de leitura dentro da consulta SQLite, operação SQLite inteira em worker e preservação de eventos após falha tardia. Os testes finais exercitam essas fronteiras onde indicado.

A publicação inicial expôs alteração de RID nos lockfiles. O alvo suportado `win-x64` passou a fazer parte do contrato de build em `Directory.Build.props`, e publish usa dependências já restauradas. Restauração travada antes/depois da publicação confirmou a correção; não há reparo posterior de lockfile no script.

## Medição exploratória de tarefas completas

Workflow medido: processo CLI Release novo → coleta → criação de banco próprio → gravação da sessão → relatório HTML → JSON em stdout → encerramento. Cada execução usou um banco novo. O relógio inclui início/encerramento e I/O observado pelo processo chamador. Não inclui download do SDK, build, tempo de implementação ou validações anteriores.

| Amostra | Tempo de parede | CPU do processo | Pico de working set do processo, amostrado | Estado |
| --- | --- | --- | --- | --- |
| Processo novo 1 | 1,7466 s | 0,390625 s | 41.619.456 bytes | Partial; quatro evidências |
| Processo novo 2 | 0,8815 s | 0,3125 s | 40.660.992 bytes | Partial; quatro evidências |

Cache do sistema operacional não foi limpo/controlado; essas amostras não constituem comparação rigorosa de cache frio/quente ou p50/p95. Não há baseline anterior equivalente, pois o repositório continha somente README. Inferência não existe nesta versão e consumiu zero tokens de modelo no workflow do aplicativo. Tokens totais do agente de desenvolvimento, custos de ferramentas, CPU de serviços do Windows e pico da família de processos não foram medidos. Não deduzir desempenho geral, ganho percentual ou requisito mínimo de RAM desses dois resultados. Os dados completos ficam em `.tools/verification/final-diagnostic-metrics.json`, fora do controle de versão.

## Cobertura desta rodada

- Compilação de Domain, Application, Infrastructure, CLI e WPF.
- Testes de regras, falhas parciais, cancelamento, SQLite real e relatórios.
- Diagnóstico consultivo real no Windows local com banco isolado de teste.
- Renderização da interface WPF ligada aos dados reais da coleta.
- Publicação local self-contained e diagnóstico pela CLI.

## Limitações explícitas

- A imagem foi renderizada pela aplicação WPF real após uma coleta. Não comprova interação por mouse/teclado, leitor de tela, diferentes DPIs ou instalação em outra máquina.
- Os testes de fonte interrompida de eventos usam uma fixture na fronteira do produtor; o log real foi consultado normalmente, sem induzir falha no serviço Windows.
- Cancelamento durante coleta e antes do primeiro acesso ao banco foi testado. Não houve injeção física de queda de energia durante fsync, cancelamento exatamente durante commit inicial ou falha deliberada do sistema de arquivos durante limpeza final. Esses casos permanecem cobertura adicional necessária antes de manutenção mutável.
- A inicialização SQLite é publicada atomicamente após uma transação curta que termina sem cancelamento no meio. Falha de limpeza de recurso próprio conserva o caminho e a razão; não existe ainda painel de recuperação automática.
- A CI Windows está preparada em arquivo, mas não foi enviada nem executada no GitHub nesta rodada.
- O aplicativo não é um antivírus, laudo de hardware ou solução de manutenção completa nesta versão. Não foram executados limpeza, reparo, atualização, SMART, sensores, backup, IA, WinDbg ou REA.
- Não estabelece prontidão da 1.0, matriz Windows Home/Pro ou hardware diverso. O executável local não é um instalador assinado nem uma distribuição pública autorizada.
