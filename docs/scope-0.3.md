# Escopo e autoridade do opTImus 0.3

Esta continuação amplia as consultas locais e corrige a apresentação da IA. Não habilita limpeza, reparos, elevação, desinstalação, alteração de inicialização ou teste de rede com tráfego.

## Inventário por ação explícita

Desktop e CLI consultam rede configurada, energia/bateria, registros de programas instalados e Run/RunOnce. O inventário não é iniciado ao abrir o app nem pelo diagnóstico básico. Interfaces/endereço IP e dados do Registro são fatos locais; a coleta não expande nem executa os comandos registrados, não usa Win32_Product e não modifica o Windows.

Cada coletor informa cobertura, limites, falhas e valores desconhecidos. Presença no Registro não estabelece instalação íntegra, execução ou inicialização habilitada. Estado da bateria não é uma medida de desgaste nem de saúde física. Não há ping, resolução DNS ou tráfego ativo de verificação.

A GUI incorpora os novos dados a uma sessão nova, substituindo somente as evidências pertencentes aos quatro coletores de inventário. Demais fatos, IDs e datas de coleta são preservados. A CLI inventory cria uma sessão somente de inventário, que pode ser exportada e usada pelo assistente via --session. Nenhum banco ou relatório anterior é substituído.

## IA extrativa

O contrato v3 aceita apenas uma seleção limitada de IDs curtos de fatos entregues ao modelo. Uma política lexical de candidatos corresponde palavras das chaves/rótulos aos termos da pergunta sem alterar os valores originais. Sem correspondência, usa fallback amplo explícito; não há interpretação de sinônimos ou negações por esse filtro. O adaptador é o dono do mapeamento entre esses IDs e evidência, chave de fato, valor, unidade e data. O aplicativo constrói a apresentação diretamente desses fatos; não exibe como observação um texto livre gerado pelo modelo.

Essa mudança evita a publicação de explicações e relações de causa inventadas pelo motor neste modo. Ela não comprova que a seleção é relevante para a pergunta, nem que os dados de origem estão corretos ou completos. Cobertura e omissões continuam explícitas. Respostas antigas com resumo/insights livres não são reinterpretadas como v3; recibos históricos permanecem preservados.

O motor continua local, sem ferramentas de ação ou shell. Leases, hashes, limites, cancelamento, reservas de capacidade e codec de transporte permanecem aplicáveis. Não há download automático de ferramentas ou modelo.

## Entrega e verificação

Frentes independentes têm um único dono por superfície de escrita. Integração, build, testes e publicação local da pasta executável são coordenados em sequência. Fixtures não substituem teste nativo; leitura real nesta máquina não homologa todo hardware, hives de outros usuários, controladoras ou condições de energia. Não há commit/push ou release pública implícitos.
