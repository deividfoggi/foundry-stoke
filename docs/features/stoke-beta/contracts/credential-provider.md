# Contrato: CredentialProvider

- **ADR**: 0005-authentication-strategy
- **Requisitos**: FR-019, FR-020; CC-005, SC-005

Abstração de autenticação do control-plane. `DefaultAzureCredential` (Entra ID) é o caminho
primário; fallback por API key/connection string apenas quando a credencial primária não
está disponível; erro claro quando nenhuma existir.

## Operação

```
interface CredentialProvider:

    resolveCredential() -> Credential
        # 1. Credencial injetada explicitamente (SEC-004), se houver.
        # 2. Primário (Entra ID) via fábrica de credencial. Se a fábrica falhar
        #    (pacote ausente, falha de construção, ou fábrica validadora que
        #    lançou), o primário é considerado indisponível.
        # 3. Se indisponível e fallback configurado, usa API key/connection string.
        # 4. Se nenhuma disponível, falha com NoCredentialAvailable (claro, determinístico).
```

Seams (mesma semântica entre linguagens):

- **Fábrica de credencial primária** (`entra_credential_factory`): produz o
  `DefaultAzureCredential`. O default importa `azure-identity` e o constrói lazy.
  "Primário indisponível" significa a fábrica lançar — não apenas o pacote faltar.
  Construir `DefaultAzureCredential` não valida nada; a falha só aparece na
  primeira aquisição de token.
- **Failover de runtime opcional** (`token_probe`): hook opcional que executa uma
  aquisição de token contra o primário construído. Se lançar, o primário é tratado
  como indisponível e a resolução cai para o fallback. Default ausente, para que
  `resolveCredential` permaneça não bloqueante (sem rede) a menos que o app opte.
  Entra ID e API key são mecanismos distintos; este hook é como o failover
  determinístico de runtime é ativado.

## Regras

- A credencial resolvida é reusada pelo `SessionController`, inclusive no caminho REST
  fallback do .NET (mesma auth, mesmo pipeline `Azure.Core`; ADR 0005, prioridade 5).
- Segredos de fallback MUST vir de variáveis de ambiente/configuração ou vault, **nunca
  hardcoded** (validado no security-review).
- Renovação: em sessões longas, credenciais Entra ID expiradas são renovadas via
  `DefaultAzureCredential`. Como Entra ID e API key são mecanismos distintos, não
  há failover implícito de token expirado para o fallback; o failover determinístico
  de runtime é ativado explicitamente via `token_probe` (aquisição de token que, ao
  falhar, cai para o fallback). Sem `token_probe`, uma falha de aquisição em runtime
  é um erro de autenticação (failure mode da spec).

## Erros tipados

| Erro | Quando |
|------|--------|
| `NoCredentialAvailable` | nem `DefaultAzureCredential` nem fallback disponíveis (CC-005). |
| `AuthenticationFailed` | credencial presente mas rejeitada pelo serviço. |

## Notas idiomáticas

- Python: usa `azure-identity` (`DefaultAzureCredential`); fallback lê API key/connection
  string de configuração/env.
- .NET: usa `Azure.Identity` (`DefaultAzureCredential`); `ICredentialProvider` retornando
  `TokenCredential` ou credencial de fallback.
