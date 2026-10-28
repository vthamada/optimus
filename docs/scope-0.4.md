# Escopo e autoridade do opTImus 0.4

Esta continuação acrescenta consultas de estado do Microsoft Defender, dispositivos/drivers e três verificações de integridade do Windows. Não habilita reparos, instalação de drivers, exclusão de arquivos, varredura antivírus, quarentena, modificação das proteções ou elevação automática.

## Consultas nomeadas

Defender e dispositivos usam somente scripts compilados no catálogo do programa. Não há entrada de código, argumento livre ou comando para a IA. O host é o Windows PowerShell de System32, com NoProfile/NonInteractive, diretório de trabalho do sistema, hash do executável vinculado à execução e módulos restritos ao diretório do Windows nesse processo. Políticas de execução/segurança herdadas são respeitadas, sem Bypass.

Get-MpComputerStatus consulta campos de estado/assinaturas; não executa scan nem altera configurações. Win32_PnPEntity/Win32_PnPSignedDriver consultam instâncias locais com limites explícitos; não usam Win32_Product ou alvos remotos. Presença de dispositivo, status reportado, assinatura ou versão do driver não comprovam saúde física nem necessidade de atualização.

Os adaptadores mantêm saída original, tipos, datas, identidades, omissões e falhas. Campo ausente não se torna false, zero ou ausência de problema. Falta de módulo/permissão não é estado saudável.

## Verificação de integridade

As operações são SFC /verifyonly, DISM /Online /Cleanup-Image /CheckHealth e DISM /Online /Cleanup-Image /ScanHealth. O plano informa caminho, argumentos fixos, digest, prazo, permissões e consequências. A execução exige confirmação vinculada ao plano e contexto já elevado; o aplicativo não solicita UAC nem inicia auxiliar elevado.

O programa revalida o plano após a fila, registra journal antes de iniciar e acompanha somente seu Job Object. Não há retry automático após resultado incerto. Cancelamento não desfaz logs nem prova que serviços externos do Windows ficaram ociosos. As ferramentas podem escrever logs CBS/DISM; verify-only refere-se à ausência de reparo, não à ausência de toda gravação de diagnóstico pelo Windows.

Código zero confirma somente término do comando. A saída original precisa ser examinada; o produto não converte texto localizado em laudo de integridade. As verificações SFC/DISM não são iniciadas nesta rodada; somente a ajuda dos binários é consultada para observar o transporte, além de fixtures e prévias nativas. Consultas Defender/dispositivos podem ser validadas nativamente sem alterar proteções.

## Transporte e persistência

O provedor declara UTF-8, UTF-16LE ou página OEM do Windows. Não há detecção heurística nem reparo de texto. O limite continua incidindo nos bytes combinados; as verificações solicitam bytes originais para investigar falhas de codificação. Tokens, CPU e memória só são declarados quando medidos.

Coletas da manutenção substituem apenas as fontes selecionadas em uma sessão nova, preservando IDs/datas das demais evidências e parcialidades desconhecidas. Relatórios e histórico anteriores permanecem preservados. Dados, binários e journals ficam locais e fora do Git.
