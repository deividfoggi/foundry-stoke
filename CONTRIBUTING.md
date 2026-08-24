# Contribuindo com o Stoke

Obrigado pelo interesse em contribuir com o **Stoke**, uma biblioteca leve e
enxuta para controlar instâncias de Foundry Hosted Agent no Foundry Agent
Service. O escopo do primeiro beta cobre warm-up de instância, controle mínimo
de sessão, estado, integração com o Foundry e um store durável através de um
padrão de provider plugável. O projeto tem como alvo **Python**, **C#** e **Go**.

## Layout do monorepo

O repositório é organizado por linguagem:

| Diretório | Linguagem | Coordenadas do pacote |
|-----------|-----------|-----------------------|
| `python/` | Python | PyPI `foundry-stoke` (import `foundry_stoke`) |
| `dotnet/` | C# / .NET | NuGet `Foundry.Stoke` |
| `go/` | Go | módulo `github.com/deividfoggi/foundry-stoke` |

Mantenha as alterações contidas dentro do diretório da linguagem correspondente,
a menos que a mudança afete o comportamento compartilhado entre as três
implementações. Nesse caso, aplique a mudança de forma consistente nas três.

## Pré-requisitos por linguagem

- **Python**: siga a configuração de ambiente descrita em `python/`.
- **.NET**: siga a configuração descrita em `dotnet/`.
- **Go**: siga a configuração descrita em `go/`.

Execute os testes da linguagem afetada antes de abrir um PR.

## Convenção de branches

Use branches descritivas a partir da branch de integração:

- `feature/descricao-curta` para novas funcionalidades
- `fix/descricao-curta` para correções
- `docs/descricao-curta` para documentação

Não faça commits diretos na branch de integração.

## Convenção de commits

Utilizamos **Conventional Commits**. Exemplos:

- `feat(python): adiciona warm-up de instância`
- `fix(go): corrige controle de sessão`
- `docs: atualiza guia de contribuição`

Quando a mudança for específica de uma linguagem, use o escopo `python`,
`dotnet` ou `go`.

## Pull requests

- Abra o PR contra a branch de integração.
- Descreva o que foi alterado e por quê.
- Garanta que os testes da linguagem afetada passem.
- Mantenha o PR focado em uma única mudança lógica.

## Licença e atribuição de derivados

O Stoke é licenciado sob **Apache-2.0**. Qualquer pessoa pode fazer fork e
criar novas versões. No entanto, trabalhos derivados devem **atribuir e
referenciar o projeto original**, conforme exigido pela licença:

- Mantenha o arquivo `NOTICE` no derivado, preservando a atribuição ao projeto
  original (https://github.com/deividfoggi/foundry-stoke).
- Sinalize de forma clara os arquivos modificados (cláusula de "state changes"
  da Seção 4(b) da Apache-2.0).
- Preserve os avisos de copyright, patente, marca e atribuição presentes no
  código original.

Ao enviar uma contribuição, você concorda que ela será licenciada sob os mesmos
termos da Apache-2.0.

## Código de conduta

Ao participar deste projeto, você concorda em seguir o
[Código de Conduta](CODE_OF_CONDUCT.md).

## Releasing (Python)

O pacote Python (`foundry-stoke`) é publicado no PyPI via **Trusted Publishing
(OIDC)**. Nenhum token de PyPI/TestPyPI é armazenado no repositório. O release é
independente por linguagem: tags Python usam o prefixo `python-v*` (ex.:
`python-v0.1.0b1`), de forma que futuras tags de .NET nunca colidam.

O pipeline (`.github/workflows/release.yml`) tem três estágios encadeados:

1. `build`: gera sdist + wheel a partir de `python/`, roda `twine check`, gera um
   SBOM CycloneDX e sobe os artefatos.
2. `publish-testpypi` (environment `testpypi`): publica no TestPyPI como dry-run.
3. `publish-pypi` (environment `pypi`, protegido): publica no PyPI somente após o
   dry-run no TestPyPI e após **aprovação manual** do environment.

### Setup manual único do mantenedor (Trusted Publisher)

Antes do primeiro release, configure um "pending publisher" (Trusted Publisher)
nos dois índices. Isso é feito uma única vez pela interface web:

- No **test.pypi.org** (Account settings > Publishing > Add a pending publisher):
  - Repository owner: `deividfoggi`
  - Repository name: `foundry-stoke`
  - Workflow filename: `release.yml`
  - Environment name: `testpypi`
- No **pypi.org** (mesmo caminho):
  - Repository owner: `deividfoggi`
  - Repository name: `foundry-stoke`
  - Workflow filename: `release.yml`
  - Environment name: `pypi`

Configure também os GitHub Environments `testpypi` e `pypi` no repositório; marque
`pypi` como protegido, exigindo aprovação manual (Required reviewers).

> A disponibilidade do nome `foundry-stoke` no índice só é confirmada quando o
> TestPyPI aceita o primeiro upload. Se o nome estiver tomado, ajuste antes de
> promover para o PyPI.

### Como cortar um release

1. Atualize `version` em `python/pyproject.toml` (PEP 440; beta usa sufixo `bN`,
   ex.: `0.1.0b1`).
2. Faça commit e crie a tag correspondente:

   ```bash
   git tag python-v0.1.0b1
   git push origin python-v0.1.0b1
   ```

3. O workflow `release.yml` roda: build + SBOM, publica no TestPyPI (dry-run) e
   para no gate do environment `pypi`.
4. Aprove o environment `pypi` no GitHub para promover do TestPyPI para o PyPI.

### Supply-chain

- `.github/dependabot.yml` monitora `pip` (em `/python`) e `github-actions` (em
  `/`), semanalmente.
- O SBOM CycloneDX é gerado no estágio de build de cada release e anexado como
  artefato (`python-sbom`).
- O core permanece sem dependências de runtime; a reprodutibilidade de CI é dada
  pelo `setup-python` e pela instalação explícita dos extras, sem lockfile que
  conflite com o core zero-dep.

