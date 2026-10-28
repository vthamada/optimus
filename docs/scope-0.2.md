# Contratos e autoridade do recorte 0.2

O pedido de continuação com multiagentes cobre a implementação dos módulos previstos no produto. Nesta rodada, os efeitos habilitados ficam limitados a análise por modelo local, consulta SMART escolhida e criação/verificação/restauração de backup em destinos locais explicitamente selecionados. Não há limpeza, reparo do Windows, elevação, troca de drivers ou execução livre de comandos.

## Execução de ferramentas

Somente adaptadores nomeados constroem argumentos. A interface do assistente não recebe shell, scripts nem a capacidade de escolher operações de manutenção. O executável configurado é um arquivo local selecionado; digests fixados vinculam os binários preparados para teste. O runtime deve criar o processo suspenso, associá-lo a um Job Object próprio e só então retomá-lo. Falha de associação impede execução. Deadline, cancelamento, limite de saída e finalização operam sobre a propriedade do job, sem matar processos por PID/path.

As saídas têm limite conjunto antes de retenção e preservam razões de falha. O ambiente herdado não é persistido. Falha de cleanup conserva propriedade, motivo e caminho/handle necessários ao acompanhamento. O workflow mantém os resultados parciais; código zero sozinho não estabelece resultado verificado.

## IA local

O modelo só é carregado por ação explícita. A entrada usa seleção compacta de fatos e IDs; logs e nomes são dados sem autoridade. A resposta precisa passar pelo contrato JSON e referenciar somente evidências entregues. Isso comprova a integridade das referências, não a correção semântica de toda conclusão do modelo. Modelo pequeno usado no piloto é experimental e deve mostrar limitações. O adaptador não envia dados a serviços externos nem baixa arquivos. O modo offline do motor não equivale a isolamento de rede do Windows.

## Backup

Prévia antes de efeitos; confirmação vinculada ao plano e revalidação dos caminhos/identidades. O repositório inicial é novo/vazio e local, fora da origem. A restauração tem outro plano e destino novo, preservando os arquivos já existentes. Arquivo de senha é explicitamente escolhido e nunca incorporado ao perfil nem impresso como valor. A perda dele pode impedir recuperação.

Journal durável precede cada efeito. Snapshot criado, verificação de integridade e teste de restauração são estados distintos. Timeout ou cancelamento depois de efeito exige reconciliação; não há retry automático, prune, exclusão de snapshots/locks ou adoção de repositórios desconhecidos. Suporte a VSS, arquivos abertos, EFS, ACLs e placeholders depende de validação própria; não é presumido.

## Verificação

O coordenador é o único dono de restore/build/test/publish e dos contratos compartilhados. Agentes têm superfícies de escrita separadas. Ferramentas reais e modelo são testados em fixtures privadas sob `.tools/verification/iteration-02`; pastas pessoais não são copiadas nem restauradas durante testes. Fontes, binários, modelos, evidências e artefatos de desenvolvimento permanecem separados.
