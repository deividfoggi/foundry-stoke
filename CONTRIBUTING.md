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
