# Escopo e autoridade do opTImus 0.8

Este recorte acrescenta três consultas locais explícitas: indicadores selecionados de reinicialização no Registro, pontos de restauração reportados por SystemRestore e estados de volumes reportados pelo provedor BitLocker. As consultas têm identidades nomeadas e usam os mesmos fluxos de sessão, histórico e relatório da CLI e da interface.

## Indicadores de reinicialização

A fonte lê a visão de 64 bits de HKLM: existência de `SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending`, existência de `SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` e o valor `PendingFileRenameOperations` de `SYSTEM\CurrentControlSet\Control\Session Manager`.

São indicadores selecionados, não um diagnóstico universal de necessidade de reinicialização. Ausência confirmada, fonte indisponível, permissão recusada, formato inválido e retenção limitada permanecem distintos. O sinal COM da WUA continua pertencendo ao histórico de atualizações e mantém sua data original. O novo coletor não usa esse resultado antigo como estado atual nem o reinterpreta.

O Registro só é aberto para leitura. Não há limpeza de valores, agendamento de operações de arquivo ou comando de reinicialização. Os caminhos recebidos são dados e não são executados ou resolvidos para tocar os arquivos indicados.

## Pontos de restauração

Consulta local de propriedades da classe `SystemRestore`, namespace `root/default`: Description, RestorePointType, EventType, SequenceNumber e CreationTime. Nenhum método de criação, remoção, restauração ou configuração é chamado. Códigos numéricos originais e representação temporal permanecem na evidência; datas sem fuso não recebem UTC inferido.

Uma coleção vazia só informa que nenhum ponto foi devolvido no escopo consultado. A listagem não prova a configuração da proteção nem a viabilidade de recuperar o computador ou seus arquivos.

## Estado do BitLocker

Consulta local da classe `Win32_EncryptableVolume`, namespace `root/CIMV2/Security/MicrosoftVolumeEncryption`, com identificadores de volume e os getters fixos GetConversionStatus, GetProtectionStatus, GetEncryptionMethod e GetLockStatus. Resultados de método conservam ReturnValue e os códigos devolvidos. Falha, indisponibilidade, edição sem suporte e acesso recusado não se tornam proteção desligada.

Nenhum método de gestão de chaves é chamado. Material de chaves, senhas de recuperação e identificadores de protetores não fazem parte da seleção. Não há ativação, suspensão, desbloqueio, descriptografia ou alteração de volumes. A consulta não verifica recuperação de chaves nem eficácia criptográfica.

## Integração e evidência

As ações só começam após uma consulta explícita. Selecionar uma opção ou iniciar normalmente o aplicativo não executa os novos coletores. Resultados parciais conservam fatos e motivos; ausência de alertas não comprova saúde geral. O seletor, a CLI e a incorporação à sessão usam contratos nomeados; a ordem visual não escolhe a operação.

Validação real se limita ao contexto corrente. Fixtures não demonstram comportamento de uma edição Windows, provedor, volume criptografado ou restauração física. Os resultados executados estão registrados em validation-0.8.md. Nenhum reparo, reinício ou mudança de configuração faz parte desta iteração.
