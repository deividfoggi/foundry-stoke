# authentication-strategy

**Status**: Proposed
**Date**: 2026-08-21

## Context

A Stoke autentica no control-plane do Foundry. Os exemplos oficiais (Python e .NET) usam `DefaultAzureCredential` (Entra ID) como caminho primário. Ambientes onde a credencial gerenciada não está disponível precisam de um fallback (API key / connection string). A camada de auth precisa ser uma abstração comum entre linguagens (FR-019, FR-020, CC-005).

Além disso, a pesquisa confirmou uma lacuna: o SDK .NET (`Azure.AI.Projects`, prerelease) **não expõe, de forma confirmada, operações de sessão tipadas** (a how-to de sessões não tem pivot C#). O Python (`azure-ai-projects`) expõe o session control tipado. Isso força uma decisão de como o .NET executa as operações de sessão sem inventar API.

## Priorities and Requirements (ordered)

1. **Entra ID primário via DefaultAzureCredential** — Caminho padrão, alinhado às docs oficiais e a boas práticas de segurança (sem segredos embarcados) (FR-019).
2. **Fallback explícito e seguro** — API key / connection string apenas quando a credencial primária não estiver disponível; erro claro quando nenhuma existir (FR-020, CC-005). Segredos nunca hardcoded; vêm de configuração/vault.
3. **Não inventar APIs** — Onde o SDK oficial expõe a operação, usá-la; onde não expõe, consumir o contrato REST oficial documentado, não fabricar superfície (FR-018).
4. **Abstração comum entre linguagens** — Uma interface `CredentialProvider` e uma `SessionController` com semântica equivalente em Python e .NET (FR-021, FR-022).
5. **Mesma auth para control-plane e fallback REST** — O caminho REST de fallback do .NET deve reusar exatamente a mesma credencial/pipeline, sem um segundo mecanismo de auth.

## Options Considered

### Option 1 (auth): DefaultAzureCredential primário + fallback API key/connection string sob CredentialProvider

Interface `CredentialProvider` resolve a credencial: tenta `DefaultAzureCredential`; se indisponível e houver fallback configurado, usa API key/connection string; se nenhuma, falha com erro tipado claro.

**Evaluation against priorities**:
- **Entra ID primário**: Atende. `DefaultAzureCredential` é o caminho padrão.
- **Fallback seguro**: Atende. Fallback só quando o primário falha; erro claro na ausência total; segredos via configuração.
- **Não inventar APIs**: Atende (ortogonal a auth).
- **Abstração comum**: Atende. `CredentialProvider` idêntico em conceito entre linguagens.

### Option 1 (.NET session ops): REST fallback via ISessionController usando o pipeline Azure.Core/AIProjectClient

Onde o SDK .NET não expõe operações de sessão tipadas, a Stoke chama a **API REST `/sessions` oficial documentada** através do pipeline HTTP do `Azure.Core`/`AIProjectClient` (mesma auth via `DefaultAzureCredential`, mesmo endpoint), encapsulada atrás de `ISessionController`. Python usa o session control tipado do `azure-ai-projects` atrás do mesmo `SessionController`.

**Evaluation against priorities**:
- **Entra ID primário / fallback**: Atende. O pipeline REST usa a mesma credencial resolvida pelo `CredentialProvider`.
- **Não inventar APIs**: Atende. Consumir o contrato REST `/sessions` documentado não é inventar; é usar o contrato oficial. Se/quando o SDK .NET expuser as operações tipadas, a implementação migra atrás da mesma interface sem quebra.
- **Abstração comum**: Atende. `ISessionController` esconde a diferença (tipado no Python, REST no .NET) do consumidor.
- **Mesma auth para REST**: Atende. O REST reusa o pipeline `Azure.Core` do `AIProjectClient`, mesma credencial.

### Option 2 (.NET session ops): Aguardar/bloquear até o SDK .NET expor operações tipadas

Não implementar session control no .NET até haver API tipada oficial.

**Evaluation against priorities**:
- **Não inventar APIs**: Atende, mas ao custo de bloquear a paridade .NET indefinidamente.
- **Abstração comum**: Falha no beta. .NET ficaria sem session control, quebrando a equivalência Python/.NET exigida pelo escopo v1.2.

## Decision

- **Auth**: adotar **Option 1** — `DefaultAzureCredential` primário com fallback API key/connection string, encapsulados por uma interface `CredentialProvider`; erro tipado claro quando nenhuma credencial estiver disponível.
- **.NET session control**: adotar o **REST fallback via `ISessionController`** — onde o SDK .NET não expuser operações de sessão tipadas, a Stoke consome a API REST `/sessions` oficial documentada através do pipeline `Azure.Core`/`AIProjectClient`, com a mesma credencial. Python usa o session control tipado do `azure-ai-projects` atrás do mesmo `SessionController`. A diferença fica encapsulada; migra para tipado sem quebra quando disponível.

Justificativa: a Option 1 de auth satisfaz as prioridades 1, 2 e 4 diretamente. Para as operações .NET, o REST fallback satisfaz a prioridade 3 (consome contrato oficial, não inventa) e a prioridade 4/5 (mesma abstração, mesma auth), enquanto a Option 2 falharia a equivalência Python/.NET do beta.

## Implementation Notes

- Contratos em `docs/features/stoke-beta/contracts/credential-provider.md` e `session-controller.md`.
- **Precedência e seams do `CredentialProvider`**: a resolução segue credencial injetada (SEC-004) > primário (Entra ID) > fallback API key/connection string > `NoCredentialAvailable` (CC-005). O primário é produzido por uma **fábrica injetável** (`entra_credential_factory`); o default importa `azure-identity` e constrói `DefaultAzureCredential` lazy. "Primário indisponível" significa a **fábrica lançar** (pacote ausente, falha de construção, ou fábrica validadora fornecida pelo app que falhou), não apenas o pacote estar ausente. O **failover de runtime** é opcional e explícito via `token_probe`: um hook que executa aquisição de token contra o primário e, ao lançar, cai para o fallback. Default ausente, mantendo `resolve_credential` não bloqueante (sem rede) por padrão. Entra ID e API key são mecanismos distintos, então o failover determinístico só ocorre quando o app opta pelo `token_probe`.
- **Segurança**: segredos de fallback (API key/connection string) MUST vir de variáveis de ambiente/configuração ou vault, nunca hardcoded. Validar no security-review.
- **Gap conhecido/assunção (registrar como tal)**: (1) operações de sessão tipadas no SDK .NET não confirmadas — assumir REST fallback até confirmação; (2) strings exatas do enum de status de sessão não documentadas — a Stoke expõe enum próprio e traduz em runtime. Ambos rastreados como NEEDS RESEARCH em research.md.
- Observabilidade: falhas de auth emitem evento/erro (mantidos 100% pelo sampling tail-based, por serem erros); span `stoke.session.*` carrega o resultado de auth como atributo, sem vazar segredos.

### Notas de segurança (security-review, 2026-08-21)

- **Cadeia não determinística do `DefaultAzureCredential` (SEC-004)**: a credencial vencedora da cadeia não é determinística; um `az login` no host pode fazer a cadeia cair silenciosamente de managed identity para `AzureCliCredential`, resultando em identidade inesperada. Recomendar/suportar credencial **determinística** em produção: definir `AZURE_TOKEN_CREDENTIALS=prod`, ou injetar um `TokenCredential`/`ManagedIdentityCredential` explícito através do `CredentialProvider`. A abstração já permite; tornar explícito no contrato (credential-provider.md) e na documentação.
- **Ciclo de vida e não persistência do segredo de fallback (SEC-005)**: precedência explícita — sempre preferir `DefaultAzureCredential`; o fallback por API key/connection string só é usado quando o primário está indisponível. Invariante: segredos **NUNCA** são persistidos no store durável nem em `StoreRecord`. Minimizar o tempo de vida em memória e não expô-los em `ToString`/`repr`. Zeroização é best-effort em runtimes gerenciados (usar `char[]`/`SecureString` onde aplicável, cientes da limitação). O fallback vem apenas de env/config/vault. O aspecto de telemetria (não emitir segredos como atributos) é governado pelo ADR 0006.

## References

* docs/features/stoke-beta/spec.md (FR-018, FR-019, FR-020, CC-005, SC-005)
* docs/features/stoke-beta/research.md (.NET session ops NÃO confirmado; REST fallback; enum de status)
* docs/features/stoke-beta/contracts/session-controller.md
* docs/features/stoke-beta/contracts/credential-provider.md
