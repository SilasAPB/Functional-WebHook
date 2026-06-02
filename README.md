# Functional-WebHook

Webhook em F# para simular um gateway de pagamento, com foco em pure functions e imutabilidade e script de teste em Python.

## Requisitos
- .NET 6.0 SDK ou superior
- Python 3.8 ou superior (para o script de teste)

## Estutura do Projeto
- `FunctionalWebhook/`: Contém o código do webhook em F#.
- `test/`: Contém o script de teste em Python.
- `README.md`: Este arquivo de documentação.

obs: na pasta `FunctionalWebhook/` tem um arquivo `Program.fs` com o código do webhook e um arquivo `FunctionalWebhook.fsproj` com as dependências, e na pasta `test/` tem um arquivo `test_webhook.py` com o script de teste e um arquivo `webhook.py` para mostrar um exemplo de webhook simples em linguagem não funcional.

- O servidor abre na porta 5000 para HTTP e 5443 para HTTPS, e o script de teste envia requisições para `https://localhost:5443/webhook`.

## Como Rodar o Webhook
```bash
dotnet run --project FunctionalWebhook
```
### Como Rodar o Script de Teste

- Lembres-e de instalar as dependências do Python, como `requests`, usando:
```bash
pip install -r requirements.txt
```

```bash
python test/test_webhook.py
```
### Personalizando o Script de Teste
Você pode passar argumentos para o script de teste para simular diferentes eventos e transações. Por exemplo:

```bash
python test/test_webhook.py <event> <transaction_id> <amount> <currency> <timestamp> <token>
```

```bash
python test/test_webhook.py payment_success abc123456789 49.90 BRL 2023-10-01T12:00:00Z meu-token-secreto
```
Isso simula um evento de pagamento bem-sucedido para a transação `abc123456789` no valor de `49.90 BRL` com um timestamp específico.

obs: se voce rodar o script de teste mais de uma vez com a mesma transação, ele vai retornar "cancelled" porque a transação já foi confirmada na primeira execução, para evitar isso, basta alterar o ID da transação no script de teste. E isso pode fazer um dos testes falharem, mas é esperado que isso aconteça.

## Token
Como é possivel observar na chamada do script de teste, o token é passado como argumento, e o webhook valida esse token para garantir que a requisição é legítima. O token esperado é definido na função `validateToken` dentro do `Program.fs` e seu valor default é `"meu-token-secreto"`. Você pode alterar esse valor tanto no script de teste quanto na função de validação para testar diferentes cenários de autenticação.

## Uso de DB
O webhook utiliza um banco de dados SQLite para armazenar o estado das transações, garantindo que as operações de confirmação e cancelamento sejam persistentes e consistentes. O arquivo do banco de dados é criado automaticamente na pasta do projeto e é gerenciado através da função `saveTransaction` no `Program.fs`. O arquivvo do banco de dados é nomeado como `transactions.db` e contém uma tabela chamada `Transactions` com as colunas `TransactionId`, `Status`, `Reason` e `CreatedAt`. O status pode ser "confirmed" ou "cancelled", permitindo que o webhook mantenha um registro preciso.

## Funcionalidades Implementadas
- Validação de token de autenticação (`X-Webhook-Token`)
- Validação de integridade do payload (campos obrigatórios)
- Verificação de veracidade da transação (amount e currency)
- Cancelamento automático em caso de divergência
- Confirmação automática em caso de sucesso
- Persistência em banco de dados SQLite
- Servidor HTTPS na porta 5443

## Agradecimentos
- Ao professor Fabio Ayres e todos os colegas de Programação Funcional!





