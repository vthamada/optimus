# opTImus

**Inteligência para cuidar do seu computador.**

Aplicativo Windows em desenvolvimento, com interface WPF em português e CLI. O protótipo **0.9.1** reúne diagnóstico e inventário local, seleção de fatos com modelo GGUF, backup/restauração com restic e ferramentas consultivas de manutenção.

O repositório contém o código-fonte, testes e documentação. Para gerar o executável no Windows x64, siga a seção **Executar no Windows x64** e execute `scripts/dev.ps1 -Task publish`. A pasta `artifacts/app` é gerada localmente; executáveis, modelos e dados pessoais não são enviados ao Git. Ainda não há release com instalador para download.

## Começar pela interface

Abra `artifacts/app/opTImus.exe` e use **Visão geral → Iniciar diagnóstico**. Não é necessário configurar IA para coletar os dados básicos. A navegação lateral reúne Diagnóstico, Instabilidade, Consultas, Inventário, Backup, Assistente IA, Histórico e Configuração.

A interface 0.9.1 apresenta requisitos junto às ações, busca de consultas por assunto, resultados com detalhes recolhidos e etapas separadas de backup. Consulte o [guia da interface](docs/interface.md) e a [validação 0.9.1](docs/validation-ui-0.9.1.md). Ferramentas opcionais continuam dependendo de configuração; a versão não acrescenta reparos ou instalações automáticas.

## Funcionalidades implementadas

- Sistema, CPU, memória e volumes fixos; seleção de eventos do log System com XML original e cobertura explícita.
- Triagem por regras, histórico SQLite e relatórios novos HTML/JSON, sem sobrescrever arquivos existentes.
- IA local extrativa: o modelo escolhe até seis fatos; valores, unidades e datas exibidos vêm das fontes originais. Não são apresentadas explicações de causa geradas pelo modelo. A relevância da seleção continua experimental.
- Inventário por ação explícita: interfaces/endereço de rede, alimentação/bateria, programas registrados e Run/RunOnce. Não envia pacotes nem modifica os itens consultados.
- Prévia e confirmação de backup em repositório local novo, verificação de integridade e restauração separada para destino novo.
- Consulta SMART de dispositivo escolhido e importação de JSON, preservando dados originais e campos desconhecidos.
- Consulta do Microsoft Defender e inventário local de dispositivos/drivers, sem alterar proteção ou instalar drivers.
- Inventário de serviços e histórico local de atualizações do Windows, sem alterar serviços, procurar ou instalar atualizações.
- Análise de espaço de uma pasta escolhida: contagem, tamanhos lógicos e até vinte maiores arquivos, com omissões explícitas e sem exclusão.
- Consulta de processos, tarefas agendadas e discos/partições com valores originais e cobertura limitada; sem encerrar processos, alterar tarefas ou modificar discos.
- Tabelas locais TCP/UDP, perfis ActiveStore do Firewall e eventos selecionados de falhas de aplicativos, preservando as fontes e os estados desconhecidos.
- Indicadores selecionados de reinicialização no Registro, pontos de restauração reportados e estado do BitLocker, sem reiniciar, restaurar ou coletar chaves.
- Prévia, confirmação e journal para SFC /verifyonly e DISM /CheckHealth ou /ScanHealth; execução exige contexto já elevado, sem reparo ou elevação automática.
- Supervisão dos processos em Windows Job Objects, com limites, cancelamento e preservação de resultados parciais.
- Módulo Instabilidade: histórico selecionado de reinicializações/WHEA por período, resultados registrados de memória e inventário de placa-mãe/BIOS/CPU/RAM.
- Importação de relatórios HTML reais do MemTest86, com bytes originais, limites e bits divergentes por linha, sem teste físico ou atribuição automática a um pente.
- Análise de dump escolhido com CDB, pasta de símbolos locais, argumentos fixos e recibos. Completude de símbolos e causa física continuam desconhecidas.
- Roteiro de até quatro consultas, com IA local opcional para escolher o próximo passo. Hipóteses e próximos testes têm referências às evidências; o modelo não recebe comandos livres.
- Páginas oficiais de suporte e leitura explícita de uma URL oficial pela internet, sem envio automático de logs ou aplicação de firmware.

O app não oferece limpeza, reparo, desinstalação, alteração de inicialização, atualização de drivers, elevação automática ou comandos livres para a IA. Sensores, ações antivírus, Sysinternals e REA continuam fora deste recorte. CDB é opcional e precisa ser selecionado; não é instalado pelo app. A implementação SMART ainda precisa de validação com ferramenta e hardware reais.

## Executar no Windows x64

SDK fixado: .NET 10.0.403. O bootstrap opcional prepara o SDK dentro de `.tools/`, confere SHA-512 e não altera o PATH global. Execute primeiro sem `-Apply` para revisar os caminhos.

```powershell
.\scripts\bootstrap-dotnet.ps1
.\scripts\bootstrap-dotnet.ps1 -Apply
.\scripts\dev.ps1 -Task restore
.\scripts\dev.ps1 -Task build
.\scripts\dev.ps1 -Task test
.\scripts\dev.ps1 -Task run
```

O diagnóstico básico funciona sem instalar ferramentas externas. Para IA, backup e SMART, selecione os executáveis/modelo em **Configuração**, ou importe um perfil local. O aplicativo não baixa modelos nem instala ferramentas ao iniciar. Veja [configuração e comandos dos módulos](docs/local-tools.md), incluindo artefatos fixados do piloto e suas licenças.

```powershell
# Relatórios precisam de uma pasta existente e um nome de arquivo novo.
New-Item -ItemType Directory -Path .\artifacts -Force
.\scripts\dev.ps1 -Task diagnose -OutputPath "$PWD\artifacts\diagnostico.json"
.\scripts\dev.ps1 -Task inventory -OutputPath "$PWD\artifacts\inventario.json"
.\scripts\dev.ps1 -Task defender-status -OutputPath "$PWD\artifacts\defender.json"
.\scripts\dev.ps1 -Task device-inventory -OutputPath "$PWD\artifacts\dispositivos.json"
.\scripts\dev.ps1 -Task services -OutputPath "$PWD\artifacts\servicos.json"
.\scripts\dev.ps1 -Task update-history -OutputPath "$PWD\artifacts\atualizacoes.json"
.\scripts\dev.ps1 -Task processes -OutputPath "$PWD\artifacts\processos.json"
.\scripts\dev.ps1 -Task scheduled-tasks -OutputPath "$PWD\artifacts\tarefas.json"
.\scripts\dev.ps1 -Task storage -OutputPath "$PWD\artifacts\armazenamento.json"
.\scripts\dev.ps1 -Task connections -OutputPath "$PWD\artifacts\conexoes.json"
.\scripts\dev.ps1 -Task firewall -OutputPath "$PWD\artifacts\firewall.json"
.\scripts\dev.ps1 -Task application-failures -OutputPath "$PWD\artifacts\falhas-aplicativos.json"
.\scripts\dev.ps1 -Task pending-restart -OutputPath "$PWD\artifacts\reinicializacao.json"
.\scripts\dev.ps1 -Task restore-points -OutputPath "$PWD\artifacts\pontos-restauracao.json"
.\scripts\dev.ps1 -Task bitlocker-status -OutputPath "$PWD\artifacts\bitlocker.json"
.\scripts\dev.ps1 -Task folder-space -FolderPath "D:\PastaEscolhida" -OutputPath "$PWD\artifacts\espaco.json"
.\scripts\dev.ps1 -Task restart-history -Days 30 -OutputPath "$PWD\artifacts\reinicios.json"
.\scripts\dev.ps1 -Task memory-results -Days 30 -OutputPath "$PWD\artifacts\resultados-memoria.json"
.\scripts\dev.ps1 -Task hardware-profile -OutputPath "$PWD\artifacts\hardware.json"
.\scripts\dev.ps1 -Task investigate -Days 30 -Question "Por que o computador reinicia sozinho?" -OutputPath "$PWD\artifacts\investigacao.json"
.\scripts\dev.ps1 -Task memory-import -InputPath "D:\Relatorios\MemTest86.html" -OutputPath "$PWD\artifacts\memtest-importado.json"
.\scripts\dev.ps1 -Task history

# Renderizar a interface real após uma coleta.
.\scripts\dev.ps1 -Task preview -OutputPath "$PWD\artifacts\interface.png"

# Preparar pasta executável com runtime próprio.
.\scripts\dev.ps1 -Task publish
.\artifacts\app\opTImus.exe
```

`publish` produz uma pasta self-contained; o computador de destino não precisa do SDK. Ainda não há instalador ou assinatura de distribuição. Ferramentas externas e pesos do modelo não são incluídos nessa pasta.

A CLI de diagnóstico retorna 0 para coleta concluída no escopo, 3 para cobertura parcial, 1 para falha e 130 para cancelamento. Os módulos retornam resultados estruturados, inclusive recibos parciais. Ausência de alertas não comprova saúde completa do equipamento.

## Dados e limites

O histórico padrão fica em `%LOCALAPPDATA%\opTImus\diagnostics.db`; `-DataDirectory` seleciona uma raiz explícita. O SQLite não é criptografado e herda as permissões da pasta. Relatórios e recibos contêm dados de equipamento e caminhos; revise antes de compartilhar. Credenciais e o ambiente completo não são coletados.

Request, prompt, schema e saída da IA ficam retidos como evidência, com limite de 100 chamadas por raiz. A seleção é experimental: IDs válidos não comprovam que os fatos escolhidos sejam relevantes. O modo offline do motor não é uma sandbox de rede do Windows.

Na versão 0.3 o modelo retorna somente IDs dos fatos entregues, sem resumo livre. Quando os termos da pergunta correspondem a palavras nas chaves/rótulos, somente esses candidatos são entregues; sem correspondência, o conjunto amplo é usado e informado. O aplicativo monta a apresentação com os valores originais, inclusive valores desconhecidos. Isso evita atribuir ao modelo a autoria das observações; não comprova que os fatos escolhidos respondam à pergunta.

O inventário tem cobertura limitada e explícita: até 64 interfaces, 32 endereços por classe/interface, 500 registros por coletor de software e orçamento conservador de 1 MiB por coletor. Valores do Registro acima de 16 KiB não são alocados. Limites atingidos produzem estado parcial. A GUI preserva as outras evidências com suas datas originais e permite exportar a sessão combinada.

Backup de arquivos não é imagem do sistema. O fluxo inicial exige repositório novo e senha em arquivo separado. Snapshot criado, integridade verificada e restauração verificada são estados distintos. Cancelamentos e falhas preservam journal e reservas; não há repetição automática nem limpeza de snapshots/locks. Guarde a senha de modo privado e recuperável.

## Desenvolvimento e validação

`Domain` define fatos e resultados; `Application` define contratos; `Infrastructure` implementa Windows, persistência, processos e integrações; `Desktop` e `Cli` compartilham os fluxos. Consulte [arquitetura](docs/architecture.md) e [escopo 0.9](docs/scope-0.9.md).

A revisão de interface está em [validação 0.9.1](docs/validation-ui-0.9.1.md). Os checks dos coletores e suas limitações estão em [validação 0.9](docs/validation-0.9.md); as validações [0.8](docs/validation-0.8.md), [0.7](docs/validation-0.7.md), [0.6](docs/validation-0.6.md), [0.5](docs/validation-0.5.md), [0.4](docs/validation-0.4.md), [0.3](docs/validation-0.3.md), [0.2](docs/validation-0.2.md) e [0.1](docs/validation.md) permanecem como histórico. Código local, GitHub atualizado e produto distribuído são estados separados.

`.tools/`, modelos, bancos, evidências e binários não são versionados. O código próprio ainda não tem uma licença de distribuição escolhida. Distribuição pública exige revisão das licenças efetivamente incluídas, SBOM, assinatura, instalador e piloto de hardware.

As verificações de integridade não são iniciadas ao abrir o app. A prévia informa executável, argumentos, digest, contexto e caminho do journal. Nenhum código de saída é convertido automaticamente em laudo de saúde. A implementação usa catálogos fixos de comandos e preserva bytes/codificações das ferramentas nativas; não oferece terminal à IA. Consulte docs/local-tools.md antes de executar uma verificação em contexto elevado.
