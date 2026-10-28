# Validação da interface 0.9.1

Data do cliente: **28/10/2026**. Revisão local de UI/UX, sobre os coletores e operações da [versão 0.9](validation-0.9.md). A implementação foi validada localmente, sem novas dependências ou alterações no Windows. Este registro precede o envio das fontes ao GitHub; não é prova de release executável ou CI remoto.

## Problema e mudança

A versão anterior mantinha uma lateral estática “Diagnóstico” e oito abas em duas linhas, com grande parte da área ocupada pelo cabeçalho, contadores e controles técnicos. A IA podia ficar desabilitada sem uma indicação próxima dos requisitos ausentes. Essas observações vieram do XAML e da imagem WPF anterior, além do relato de dificuldade de uso do usuário.

A revisão substitui essa organização por nove destinos nomeados na lateral: Visão geral, Diagnóstico, Instabilidade, Consultas, Inventário, Backup, Assistente IA, Histórico e Configuração. A tela inicial oferece diagnóstico e três tarefas comuns. Busca de consultas, requisitos dinâmicos, atalhos de configuração, estados vazios, progresso e detalhes recolhidos orientam os fluxos. As **35 ações anteriormente vinculadas na interface foram preservadas**. Navegar e editar caminhos não executam ferramentas.

SMART fica em Consultas. Backup usa etapas e deixa a restauração recolhida. Instabilidade exibe a ação principal e resultados antes das entradas avançadas. Abrir histórico leva ao Diagnóstico sem consultar novamente. “Caminhos informados” não significa arquivo existente, ferramenta válida ou compatibilidade comprovada. Perfis continuam explícitos; não há carregamento automático de configuração.

## Checks e execução

- SDK .NET 10.0.403 isolado em `.tools`, runtime publicado 10.0.14. Nenhuma alteração no PATH global.
- Compilações Debug e Release aprovadas com **zero avisos e zero erros**.
- Suíte **982 testes aprovados, zero falhas e zero ignorados**. Seis casos novos exercitam navegação sem efeitos, instruções de configuração/seleção de sessão, busca sem acentos e bloqueio de seleção oculta, requisitos opcionais da investigação e cancelamento síncrono em diagnóstico/inventário. Os fluxos anteriores de manutenção, confirmação, cancelamento e preservação de evidências permanecem na suíte.
- `scripts/dev.ps1 -Task format` aprovado. O primeiro check identificou CRLF em arquivos editados; a formatação foi aplicada apenas aos arquivos C# tocados e conferida novamente.
- `scripts/dev.ps1 -Task publish` gerou `artifacts/app/opTImus.exe`, versão **0.9.1.0**, com runtime próprio. Ferramentas externas e modelos continuam separados.
- Nove telas vazias foram renderizadas pelo pacote publicado, em raízes de verificação próprias, com `DOTNET_ROOT`/`DOTNET_ROOT_X64` ausentes e `--preview-empty true`. Todos os processos de verificação aguardados encerraram com código 0.
- Diagnóstico e investigação sem IA foram executados pela interface com fontes reais e históricos de teste separados. Os resultados e suas limitações permaneceram visíveis; a investigação publicada encerrou com código 0 do fluxo visual. Isso não transforma sessões parciais em saúde comprovada.
- O contrato antigo `--preview-tab 0..7` foi preservado; a investigação foi conferida com `--preview-tab 7`. Os novos seletores nomeados evitam que a posição atual na navegação determine a tela de prévia.

Logs e imagens privados: `.tools/verification/ui-091`. As provas finais do pacote ficam em `delivery-preview-results.json`, `delivery-native-result.json` e `delivery-manifest.json`. A imagem de uso está em `artifacts/optimus-0.9.1-interface.png`.

## Cancelamento e estado final

Uma rodada completa teve 979 aprovações e uma falha intermitente no teste de cancelamento do inventário. `RequestCancellation` chamava `CancellationTokenSource.Cancel()` antes de publicar a mensagem de pedido. A notificação do token podia concluir a operação sincronamente, publicar o estado final e retornar; o método então sobrescrevia esse estado com “Cancelamento solicitado”.

Dois casos determinísticos de diagnóstico e inventário reproduziram a ordem incorreta, falhando antes da correção. O pedido agora é publicado antes de sinalizar o token; notificações repetidas após cancelamento/finalização não substituem o resultado. Os 12 testes focados de workspace/inventário passaram, seguidos pela suíte completa de 982 casos. Logs `cancellation-before-fix.log`, `cancellation-after-fix.log` e `tests-final-982.log` conservam as fases. As falhas anteriores não são reclassificadas como aprovação.

## Janela real e acessibilidade

O pacote foi aberto com um histórico de teste separado. A automação nativa conferiu:

- Tela inicial e nove destinos de navegação expostos pelo Windows.
- Clique em Iniciar diagnóstico, mudança para Diagnóstico, coleta real e exibição de achados/fontes com limites preservados.
- Busca digitada `memoria`, que encontrou Processos e memória, Resultados de diagnóstico de memória e Placa-mãe/BIOS/memória.
- Busca sem correspondência, com mensagem explícita e sem botão para executar uma seleção anterior oculta.
- Cabeçalho, status, progresso, cancelamento e exportação presentes na árvore de acessibilidade nos estados aplicáveis.
- Encerramento normal da janela de teste por Alt+F4.

A primeira estrutura visual colocava cabeçalho e ações globais dentro do template de TabControl. A leitura real de acessibilidade mostrou que esses controles não eram expostos por esse peer. A correção deu à Window a posse desses elementos; o TabControl conserva navegação e conteúdo. A árvore nativa foi lida novamente e passou a incluir esses controles. Isso não equivale a certificação de leitor de tela.

A prévia offscreen também revelou que o fundo dependia da Window, fora do visual exportado. O fundo passou a ser explícito na raiz renderizada; as imagens de entrega foram refeitas. A primeira tentativa de coletar códigos de saída de WinExe não aguardava os processos e devolveu valores nulos; esses registros não são usados como prova de sucesso. A rodada válida usa Start-Process com Wait e PassThru, destino de imagem novo e código de saída confirmado.

## Limites

Não houve estudo de usabilidade com usuários externos, certificação de acessibilidade, teste completo de teclado/leitor de tela, alto contraste, múltiplos DPIs, nem validação conclusiva no tamanho mínimo da janela. Foram inspecionados renders reais e os fluxos descritos; as demais combinações de interação não foram percorridas manualmente.

Não foram executados backup/restauração, SMART físico, CDB real, reparo, SFC/DISM, teste físico de RAM, reinício, atualização de drivers ou BIOS nesta revisão. A interface mantém os requisitos e limites dos serviços anteriores. As limitações de IA, CDB e consulta de firmware da validação 0.9 continuam aplicáveis. Não há instalador assinado ou publicação remota nesta entrega.
