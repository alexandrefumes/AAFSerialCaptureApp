# AAF Serial Capture App

Aplicativo desktop WPF para capturar dados de uma porta serial, registrar cada linha em arquivos `.log` e acompanhar canais numéricos em gráficos em tempo real.

## Recursos

- Conexão serial configurável, com seleção de porta e baud rate.
- DTR e RTS ativados para compatibilidade com dispositivos USB-serial e comportamentos semelhantes ao PuTTY.
- Criação de múltiplas capturas e arquivos no formato `prefixo_N.log`.
- Log bruto ao vivo e carregamento de arquivos de captura existentes.
- Gráfico multicanal em tempo real com suavização, ajuste da janela de amostras, zoom e marcadores de captura.
- Extração de dados por expressão regular configurável, incluindo múltiplos canais por linha, como `S=2124, U=-526`.
- Rótulos e perfis de captura persistidos em `%AppData%\AAFSerialCaptureApp\profiles.json`.
- Exclusão de arquivos e de blocos individuais de captura.

## Requisitos

- Windows
- .NET SDK 8.0 ou superior
- Dispositivo serial acessível por uma porta `COM`

## Executar

```bash
dotnet run --project AAFSerialCaptureApp.csproj
```

No aplicativo:

1. Atualize a lista de portas e selecione a porta serial.
2. Informe ou selecione o baud rate.
3. Conecte-se ao dispositivo.
4. Escolha a pasta e o prefixo do arquivo de log.
5. Clique em **Nova Captura**.

## Formato dos dados

O padrão inicial identifica pares de nome e valor numérico. Exemplos aceitos:

```text
A=[ 74]
S=2124, U=-526
```

O padrão de captura e os rótulos dos canais podem ser alterados em **Configurações do Gráfico**. A expressão regular precisa expor os grupos nomeados `name` e `value`.

## Estrutura do projeto

- `MainWindow`: orquestra a interface, capturas e atualização visual.
- `Services/SerialPortService`: encapsula a porta serial e entrega linhas completas.
- `Models`: mantém sessões de captura, canais, arquivos, blocos e perfis.
- `Controls/RealTimeChart`: renderiza o gráfico multicanal sem dependência de biblioteca de gráficos externa.

As linhas recebidas são encaminhadas por `Channel` para um consumidor em segundo plano. Isso impede que escrita em disco, parsing da regex e renderização da interface atrasem a leitura da serial.

## Decisão de arquitetura

O escopo do aplicativo é deliberadamente simples: um cliente desktop local, uma única janela principal, uma fonte de dados serial e persistência em arquivos locais. Por isso, não foi necessário introduzir uma arquitetura de software mirabolante, com múltiplas camadas, contêiner de injeção de dependência, mensageria externa ou padrões adicionais sem necessidade concreta.

A organização atual separa o que tem responsabilidade própria (porta serial, modelo de captura e controle de gráfico) e mantém a coordenação na janela principal. Essa escolha reduz complexidade acidental, facilita manutenção e ainda preserva o requisito mais importante do aplicativo: leitura serial responsiva sem bloquear a interface.

## Licença

Distribuído sob a [licença MIT](LICENSE).

## Esteira de entrega

O repositório usa GitHub Actions para manter a branch estável validada e publicar versões reproduzíveis:

- Pushes e pull requests para `develop` ou `main` executam restore e build em Windows.
- O trabalho cotidiano deve entrar em `develop` por pull request.
- Quando `develop` estiver pronto, o merge para `main` representa a versão estável, mas não cria uma release automaticamente.
- Para publicar, crie e envie uma tag no commit de `main`, no formato `vX.Y.Z`, por exemplo:

```bash
git switch main
git pull
git tag v1.0.0
git push origin v1.0.0
```

A tag dispara a publicação auto-contida para `win-x64`, cria uma GitHub Release e anexa o arquivo `AAFSerialCaptureApp-vX.Y.Z-win-x64.zip`. A esteira valida que a tag pertence ao histórico de `main`, evitando releases a partir de branches de desenvolvimento.