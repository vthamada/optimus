# Validação do opTImus 0.3

Data: 27/10/2026. Ambiente: Windows x64 build 26300, SDK isolado .NET 10.0.403. Implementação e evidências locais, sem commit/push, CI remoto ou release pública nesta rodada.

## Testes e build

- Formatação aplicada e `dotnet format --no-restore --verify-no-changes` aprovado.
- Restauração da solução em `--locked-mode` aprovada; nenhum pacote novo.
- Build Release da solução: 0 avisos, 0 erros.
- Testes Release: **220 aprovados, 0 falhas e 0 ignorados**, 16,599 s.

Logs privados em `.tools/verification/iteration-03`: `format-final-check.log`, `restore-locked.log`, `build-release-final.log`, `tests-release-final.log`. O inventário foi integrado em três frentes independentes, com revisão cruzada e verificação conjunta pelo coordenador.

Os novos testes cobrem sentinelas de energia, ausência de bateria, tipos/unidades/endereço de rede, falhas parciais, enumeração limitada, Registro sem expansão, identidade por hive/view/key/value, valores malformados e limites antes da alocação. O fluxo da GUI tem testes de ação explícita, mescla, cancelamento, cobertura incompleta e preservação de gravação confirmada após cancelamento tardio. Regressões de processos, arquivos, SQLite, IA e backup permaneceram ativas.

## Inventário nativo

Primeira prova executada pela CLI Debug com relatório e histórico isolados em `.tools/verification/iteration-03/native-inventory-data`. Retornou 3 (parcial), com 68 evidências e relatório JSON de 894.885 bytes. Foram retidas 64 interfaces, além da evidência de descoberta; havia mais interfaces do que o limite. Energia informou ausência de bateria, preservando carga e autonomia como não aplicáveis, sem inventar percentual zero ou avaliar saúde física.

Programas: 107 entradas examinadas, 106 retidas, orçamento conservador atingido antes de concluir todos os hives/views. Inicialização: 21 entradas retidas nas oito chaves Run/RunOnce selecionadas. Esses números identificam esta amostra, não um catálogo completo do equipamento. Chaves/visões duplicadas permanecem como identidades distintas.

Não houve ping, lookup DNS, execução de comandos registrados, Win32_Product, modificação de registro ou elevação nos adaptadores. A leitura nativa confirma funcionamento nesta máquina; não é uma medição independente de toda atividade de rede/Registro do Windows. Testes de fronteira e inspeção das APIs sustentam o caráter somente leitura do código.

## Limites e proveniência

- Rede: até 64 interfaces e 32 endereços por classe/interface; omissões e campos não suportados ficam explícitos.
- Software: até 500 entradas por coletor, orçamento conservador de 1 MiB incluindo metadados/issues e até 16 KiB por valor. Tipos não textuais não viram comandos ou nomes por conversão implícita.
- Bateria física não estava presente nesta máquina. Estados de carga, carregamento e autonomia de notebook foram cobertos por fixtures, sem homologação de bateria real.
- Não se mediu throughput ou acesso à Internet. Registro Uninstall não cobre integralmente apps Store/portáteis; Run/RunOnce não cobre tarefas, serviços, pastas Startup ou estado StartupApproved.
- A IA v3 só escolhe referências de fatos. Essa restrição impede texto explicativo livre no resultado, mas não comprova relevância, veracidade da fonte nem diagnóstico causal.
- SMART físico, reparos, limpeza, sensores e elevação continuam fora da validação deste recorte. A prova de restic da versão 0.2 permanece histórica; não é apresentada como execução nova nesta rodada.
- Renderizações WPF não substituem uso manual prolongado, leitor de tela, teclado ou piloto em outras máquinas. O executável local não é um instalador assinado.

As evidências históricas 0.2 permanecem em `validation-0.2.md`; modelos, binários, bancos, recibos e dados da máquina continuam ignorados pelo Git.

## Primeira prova da IA extrativa

A primeira execução Release v3 apresentou seis fatos com rótulo, valor/unidade e data idênticos ao diagnóstico original, conferidos independentemente. A pergunta memória, porém, resultou em seis seletores iniciais sem selecionar os fatos de memória; isso demonstrou falha de relevância, sem fabricação dos valores. Recibo privado: ai-extractive-validation.json e ai-provenance-data/assistant/run-36a0b5304de64093bbc86a176b8fd6fc/result.json.

Parede da CLI: 52,570 s; CPU CLI: 5,609 s; pico working set amostrado a cada 250 ms: 48.902.144 bytes (não é pico exato). Motor/job: parede 38,670 s, CPU acumulada 95,781 s, pico de commit da família 1.329.659.904 bytes. Entrada: 6.391 bytes UTF-8, 34 fatos candidatos. Tokens reais desconhecidos. Cache e interferência externa não controlados; estas métricas não são um benchmark cold/warm.

## Prova final da seleção filtrada

A mesma sessão, o mesmo modelo/engine e a mesma pergunta memória foram usados após a correção da pré-seleção. O motor recebeu somente os dois fatos cujas chaves/rótulos correspondem ao termo. Retornou memória total e disponível. Um verificador independente comparou rótulo, valor/unidade e timestamp de cada resultado ao JSON do diagnóstico original: todos idênticos, com IDs e chaves válidos. A leitura original não foi refeita para essa comparação; datas históricas foram conservadas.

Recibo: `ai-extractive-filtered-validation.json`; request/result em `ai-provenance-data/assistant/run-bebf7bdb7846423d91859440fb1e30ef`. Estratégia lexical e exclusões estão registradas. Essa prova confirma a pergunta selecionada; não mede compreensão semântica geral. Sem correspondência, o modo amplo continua explícito, e sinônimos/negações não são interpretados pelo filtro.

| Medida | Sem pré-seleção lexical | Com pré-seleção lexical |
| --- | ---: | ---: |
| Candidatos entregues | 34 | 2 |
| Entrada UTF-8 | 6.391 bytes | 1.648 bytes |
| Selecionou fato de memória | Não | Sim, os dois |
| Parede CLI | 52,570 s | 10,418 s |
| CPU CLI | 5,609 s | 4,297 s |
| Parede do job do motor | 38,670 s | 6,396 s |
| CPU da família do motor | 95,781 s | 20,250 s |
| Pico commit da família do motor | 1.329.659.904 bytes | 1.328.627.712 bytes |
| Maior working set CLI amostrado | 48.902.144 bytes | 51.621.888 bytes |

A redução de trabalho nessa amostra não é um benchmark geral: cache frio/quente e outras cargas não foram controlados. O conjunto de candidatos/output também mudou deliberadamente. Tokens reais de entrada/saída e consumo total dos agentes não foram medidos. CPU do job soma threads/processos próprios; pico de commit difere de working set, e os picos não devem ser somados como memória total simultânea.

## Executável e interface

Publish Release win-x64 self-contained aprovado. A pasta `artifacts/app` foi iniciada com DOTNET_ROOT e DOTNET_ROOT_X64 ausentes; runtimeconfig confirma frameworks incluídos. O launcher tem versão 0.3.0 e SHA-256 `7C693EA61FD69E95EFE15DF23869B014FAF9F1DA4FFBBFB120E4E3BDD8735536`. Esse hash sozinho não autentica as DLLs/runtime da pasta, e o sufixo de versão com Git SHA identifica apenas o checkout-base, não o diff local não commitado.

A tela de inventário consultou e incorporou os dados ao diagnóstico, exibindo 72 evidências; encerrou 0 em 7,968 s. A tela da IA executou o modelo real e apresentou a seleção extrativa; encerrou 0 em 24,632 s. Imagens reais: `optimus-0.3-inventory.png` e `optimus-0.3-assistant.png`. Recibo: `desktop-final-validation.json`. São verificações de renderização/integração, não navegação manual completa.

Restauração em locked-mode após publish também aprovada. Source/manifests da pasta executável ficam privados na raiz de verificação; fontes, modelos e evidências não foram enviados ao GitHub.