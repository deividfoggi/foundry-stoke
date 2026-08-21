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
        # 1. Tenta DefaultAzureCredential (Entra ID).
        # 2. Se indisponível e fallback configurado, usa API key/connection string.
        # 3. Se nenhuma disponível, falha com NoCredentialAvailable (claro, determinístico).
```

## Regras

- A credencial resolvida é reusada pelo `SessionController`, inclusive no caminho REST
  fallback do .NET (mesma auth, mesmo pipeline `Azure.Core`; ADR 0005, prioridade 5).
- Segredos de fallback MUST vir de variáveis de ambiente/configuração ou vault, **nunca
  hardcoded** (validado no security-review).
- Renovação: em sessões longas, credenciais Entra ID expiradas são renovadas via
  `DefaultAzureCredential`; se falhar e houver fallback, usa o fallback; caso contrário,
  erro de autenticação (failure mode da spec).

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
