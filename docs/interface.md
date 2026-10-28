# Usar o opTImus 0.9.1

A tela **Visão geral** é o ponto de partida. A navegação lateral permanece disponível nas outras telas. Abrir uma tela, selecionar uma consulta ou editar uma configuração não executa ferramentas.

## Começar pelo diagnóstico

1. Em **Visão geral**, clique em **Iniciar diagnóstico**. Não é preciso configurar IA ou ferramentas externas.
2. A tela **Diagnóstico** mostra a sessão aberta, a cobertura e os achados de triagem. Abra cada fonte em **Dados por fonte** para consultar valores e datas. As mensagens de limitação continuam visíveis mesmo com os dados recolhidos.
3. Use **Exportar relatório…**, no rodapé, para salvar HTML ou JSON. Esse botão aparece quando há uma sessão aberta.

O estado parcial indica limitações de coleta, e não um defeito confirmado. Nenhum achado também não comprova a saúde de todo o computador. O rodapé informa a operação em andamento; **Cancelar operação** solicita o cancelamento e aguarda a finalização segura.

## Investigar reinícios ou travamentos

Em **Instabilidade**, descreva o problema, escolha o período e clique em **Investigar**. As três fontes padrão são registros de reinicialização, resultados já registrados de diagnóstico de memória e hardware.

Em **Opções da investigação (IA e dump)**, você pode incluir recursos opcionais. A mensagem junto ao botão informa o que falta e permite continuar sem IA ou dump. Hipóteses, próximos passos e fontes aparecem antes dos controles avançados.

**Opções avançadas e relatórios externos** reúne a análise de `.dmp` e a importação de HTML do MemTest86. A análise exige CDB, símbolos locais e dump selecionados. Importar um relatório não executa um teste físico. A seção de suporte/firmware identifica a ação que usa internet; não aplica BIOS.

## Fazer uma consulta específica

Em **Consultas**, procure pelo nome ou assunto, selecione um resultado e use **Executar consulta selecionada**. A busca aceita termos sem acento, como `memoria`. Selecionar um resultado não inicia a consulta. Sem correspondência, o aplicativo não habilita a execução de uma seleção anterior oculta.

As seções recolhidas reúnem **Espaço de uma pasta**, **Integridade do Windows** e **Saúde do disco com SMART**. A verificação de integridade mostra uma prévia e exige contexto de administrador; não executa reparos. O SMART aceita consulta com ferramenta configurada ou importação de JSON.

**Inventário** continua sendo a consulta conjunta de rede, energia, programas e entradas de inicialização.

## Configurar recursos opcionais

Em **Configuração**, informe apenas as ferramentas que pretende usar:

| Recurso | Entradas necessárias |
| --- | --- |
| Assistente IA | llama-completion e modelo GGUF |
| Backup | Executável restic |
| SMART | Executável smartctl; o dispositivo é informado em Consultas |
| Dump | CDB, símbolos e dump, nas opções avançadas de Instabilidade |

“Caminhos informados” não confirma que os arquivos existem ou são compatíveis. A validação pertence ao serviço chamado quando você inicia a ação. O programa não instala componentes nesta tela.

Para reutilizar os caminhos, **exporte um perfil** e importe-o na próxima abertura. Não há carregamento automático de perfil ao abrir o executável normalmente. Perfis não incluem a senha do backup.

No **Assistente IA**, a mensagem explica se faltam os caminhos ou uma sessão com dados. Os atalhos **Configurar IA** e **Coletar dados agora** levam ao próximo passo. O assistente seleciona fatos; a qualidade dessa seleção continua experimental.

## Backup e histórico

Em **Backup**, informe origem, repositório novo e vazio e arquivo de senha. Prepare a prévia e revise os detalhes antes de confirmar. A etapa de restauração fica recolhida em **Conferir a recuperação dos arquivos** e exige outra prévia e confirmação. Alterar parâmetros invalida os planos anteriores.

Em **Histórico**, selecione e abra uma sessão. O aplicativo leva você ao Diagnóstico, sem coletar novamente nem atualizar as datas das fontes antigas.

Limpeza, reparos e instalação de atualizações/drivers ainda não fazem parte desta versão. Consulte o [escopo de manutenção](scope-0.9.md) e a [validação da interface](validation-ui-0.9.1.md).
