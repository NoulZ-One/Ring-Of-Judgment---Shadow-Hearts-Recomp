# Ring of Judgment — Shadow Hearts Recomp

Recompilação experimental de **Shadow Hearts**, originalmente lançado para PlayStation 2.

> ⚠️ **Projeto em desenvolvimento.**
> Atualmente estamos nas primeiras etapas de análise do executável e da arquitetura MIPS do PlayStation 2.

---

## 🎮 Sobre o projeto

**Ring of Judgment** é um projeto experimental dedicado ao estudo da recompilação de jogos de PlayStation 2 para arquiteturas modernas.

O primeiro jogo escolhido como alvo é **Shadow Hearts**.

A ideia é partir do código executável original do jogo, analisar sua estrutura, identificar suas funções e instruções MIPS e, futuramente, transformar esse código em uma implementação que possa ser executada diretamente em uma arquitetura moderna.

O projeto está sendo desenvolvido principalmente em **C# / .NET** 10.

---

# 🧭 Objetivo

O objetivo de longo prazo é chegar a uma arquitetura semelhante a:

```text
Shadow Hearts ISO
        │
        ▼
   ISO-9660
        │
        ▼
    SYSTEM.CNF
        │
        ▼
 Executável principal
        │
        ▼
        ELF
        │
        ▼
    Código MIPS
        │
        ▼
 Análise de funções
        │
        ▼
 Basic Blocks
        │
        ▼
 Control Flow Graph
        │
        ▼
    Recompilação
        │
        ▼
 Runtime moderno
        │
        ▼
 Shadow Hearts
```

A intenção não é simplesmente emular um PlayStation 2 inteiro, mas estudar uma abordagem de **recompilação específica do jogo**.

---

# 🛠️ Tecnologias

* **C#**
* **.NET**
* **Git / GitHub**
* **MIPS**
* **ELF**
* **ISO-9660**
* **PlayStation 2 / Emotion Engine**

No futuro, a parte gráfica poderá utilizar **Vulkan**, buscando uma implementação mais portátil entre Windows e Linux.

---

# 📁 Estrutura atual

A estrutura do projeto atualmente está organizada aproximadamente assim:

```text
Ring of Judgment
│
├── src
│   │
│   ├── SH.Core
│   │   └── Program.cs
│   │
│   └── SH.ELF
│       │
│       ├── ElfHeader.cs
│       ├── ElfProgramHeader.cs
│       ├── ElfReader.cs
│       ├── ElfSegment.cs
│       │
│       ├── MipsInstruction.cs
│       ├── MipsInstructionReader.cs
│       ├── MipsDecoder.cs
│       ├── MipsRegister.cs
│       ├── MipsFpuRegister.cs
│       │
│       ├── MipsProgram.cs
│       ├── MipsProgramBuilder.cs
│       │
│       ├── MipsFunction.cs
│       ├── MipsFunctionBuilder.cs
│       ├── MipsFunctionAnalyzer.cs
│       │
│       ├── MipsBasicBlock.cs
│       ├── MipsBasicBlockBuilder.cs
│       │
│       ├── MipsControlFlowGraph.cs
│       └── MipsControlFlowGraphBuilder.cs
│
└── README.md
```

---

# ✅ Progresso

O progresso atual é uma estimativa informal de aproximadamente:

## **15%**

Essa porcentagem representa o avanço estrutural do projeto como um todo e não significa que 15% do jogo já esteja recompilado.

---

## 1. Projeto C# / .NET

**Status: ✅ Concluído**

O projeto inicial foi criado em:

```text
E:\Projeto Recomp\Shadow Hearts
```

O projeto pode ser executado utilizando:

```powershell
dotnet run --project src\SH.Core
```

A aplicação já inicia e consegue processar a ISO.

---

# 2. Leitura da ISO

**Status: ✅ Concluído**

O projeto já consegue abrir e ler a ISO original utilizada durante os testes.

Tamanho identificado:

```text
3.101.032.448 bytes
```

---

# 3. ISO-9660

**Status: ✅ Concluído**

Foi implementado o suporte inicial à estrutura ISO-9660.

Durante os testes, o programa identificou:

```text
=== Volume ISO-9660 ===
Sistema: PLAYSTATION
Volume: SHADOW_HEARTS
Setores: 1514176
Tamanho setor: 2048 bytes
Raiz: setor 261
```

Isso confirmou que o programa consegue interpretar corretamente a estrutura básica da mídia.

---

# 4. Diretório raiz

**Status: ✅ Concluído**

Depois de identificar o setor raiz, o programa consegue acessar e listar os arquivos e diretórios existentes na ISO.

Essa etapa é fundamental porque permite deixar de tratar a ISO apenas como um bloco de dados e começar a trabalhar com os arquivos internos do jogo.

---

# 5. Estrutura ELF

**Status: 🟡 Em desenvolvimento**

Foi criada uma camada específica para análise de executáveis ELF:

```text
ElfHeader.cs
ElfProgramHeader.cs
ElfReader.cs
ElfSegment.cs
```

Essa camada será responsável por:

* Ler o ELF Header
* Identificar a arquitetura
* Identificar os Program Headers
* Identificar segmentos
* Obter o Entry Point
* Mapear endereços virtuais para offsets no arquivo
* Preparar o executável para análise MIPS

---

# 6. Análise MIPS

**Status: 🟡 Infraestrutura inicial criada**

O PlayStation 2 utiliza uma arquitetura baseada em MIPS em seu processador principal, o **Emotion Engine**.

Foi criada a primeira infraestrutura para trabalhar com essas instruções:

```text
MipsInstruction.cs
MipsInstructionReader.cs
MipsDecoder.cs
MipsRegister.cs
MipsFpuRegister.cs
```

A intenção é permitir que o projeto consiga transformar os bytes do executável em instruções MIPS compreensíveis pelo analisador.

---

# 7. Análise de funções

**Status: 🟡 Infraestrutura inicial criada**

Também foram criadas estruturas para reconstruir a organização do código:

```text
MipsProgram.cs
MipsProgramBuilder.cs

MipsFunction.cs
MipsFunctionBuilder.cs
MipsFunctionAnalyzer.cs
```

Essa camada deverá permitir identificar funções existentes no executável e analisar suas relações.

---

# 8. Basic Blocks

**Status: 🟡 Infraestrutura inicial criada**

Foram criadas:

```text
MipsBasicBlock.cs
MipsBasicBlockBuilder.cs
```

Um **Basic Block** representa uma sequência de instruções executada linearmente, sem desvios internos.

Isso será importante para reconstruir corretamente o fluxo de execução do código MIPS.

---

# 9. Control Flow Graph

**Status: 🟡 Infraestrutura inicial criada**

Também foram criadas:

```text
MipsControlFlowGraph.cs
MipsControlFlowGraphBuilder.cs
```

O objetivo é representar graficamente as relações entre os Basic Blocks de uma função.

A estrutura será utilizada posteriormente para:

* branches;
* jumps;
* loops;
* caminhos de execução;
* análise de funções;
* recompilação.

---

# 📌 Próximo passo

A próxima etapa é localizar automaticamente o executável principal de Shadow Hearts dentro da ISO.

O plano é:

```text
1. Localizar SYSTEM.CNF
          ↓
2. Ler SYSTEM.CNF
          ↓
3. Encontrar a entrada BOOT2
          ↓
4. Descobrir o executável principal
          ↓
5. Localizar o executável na ISO
          ↓
6. Ler seus primeiros bytes
          ↓
7. Verificar a assinatura ELF
          ↓
8. Ler o ELF Header
          ↓
9. Identificar o Entry Point
          ↓
10. Localizar o código MIPS
```

A assinatura esperada de um arquivo ELF é:

```text
7F 45 4C 46
```

---

# 🗺️ Roadmap

## Fase 1 — ISO

* [x] Criar projeto C#
* [x] Abrir ISO
* [x] Identificar ISO-9660
* [x] Ler Volume Descriptor
* [x] Localizar diretório raiz
* [x] Listar diretórios e arquivos

## Fase 2 — ELF

* [x] Criar estruturas ELF
* [x] Criar ELF Reader
* [ ] Localizar `SYSTEM.CNF`
* [ ] Identificar executável principal
* [ ] Localizar ELF
* [ ] Validar ELF Magic
* [ ] Ler ELF Header
* [ ] Ler Program Headers
* [ ] Identificar Entry Point
* [ ] Mapear Entry Point para o arquivo

## Fase 3 — MIPS

* [x] Criar representação de instruções
* [x] Criar registradores MIPS
* [x] Criar decoder inicial
* [x] Criar Instruction Reader
* [ ] Decodificar o código real do jogo
* [ ] Identificar funções
* [ ] Identificar Basic Blocks
* [ ] Construir Control Flow Graph
* [ ] Validar análise automática

## Fase 4 — Recompilação

* [ ] Traduzir MIPS para representação intermediária
* [ ] Gerar código C#
* [ ] Criar runtime
* [ ] Implementar gerenciamento de memória
* [ ] Implementar chamadas e retornos
* [ ] Implementar exceções/comportamentos específicos
* [ ] Executar código recompilado

## Fase 5 — PlayStation 2

* [ ] Emotion Engine
* [ ] DMA
* [ ] VIF
* [ ] GIF
* [ ] GS
* [ ] VU0
* [ ] VU1
* [ ] SPU2
* [ ] Input
* [ ] Outros subsistemas necessários

## Fase 6 — Jogo

* [ ] Inicialização
* [ ] Menus
* [ ] Sistema de batalha
* [ ] Judgment Ring
* [ ] Personagens
* [ ] Mapas
* [ ] Áudio
* [ ] Vídeos
* [ ] Save/Load
* [ ] Jogo completo

## Fase 7 — Polimento

* [ ] Otimização
* [ ] Compatibilidade
* [ ] Suporte Linux
* [ ] Backend Vulkan
* [ ] Melhorias gráficas
* [ ] Ferramentas de debugging
* [ ] Documentação

---

# 🔖 Checkpoints

### Checkpoint 1 — ISO Reader / ISO-9660

* [x] ISO carregada
* [x] ISO-9660 funcionando
* [x] Volume identificado
* [x] Diretório raiz localizado
* [x] Arquivos listados

### Checkpoint 2 — ELF Reader + MIPS Analyzer

* [x] Estrutura ELF criada
* [x] ELF Reader criado
* [x] Estrutura MIPS criada
* [x] Decoder MIPS criado
* [x] Análise de funções criada
* [x] Basic Blocks criados
* [x] Control Flow Graph criado
* [x] Código versionado no Git
* [x] Código enviado ao GitHub

### Checkpoint 3 — ELF encontrado

* [ ] `SYSTEM.CNF` localizado
* [ ] Executável principal identificado
* [ ] ELF localizado
* [ ] ELF Magic validado

### Checkpoint 4 — Entry Point

* [ ] ELF Header analisado
* [ ] Entry Point identificado
* [ ] Segmento correspondente localizado
* [ ] Offset do Entry Point calculado

### Checkpoint 5 — Primeiro MIPS

* [ ] Primeira instrução MIPS identificada
* [ ] Instruções decodificadas
* [ ] Primeira função identificada
* [ ] Basic Blocks construídos
* [ ] Control Flow Graph validado

---

# 💾 Git / GitHub

O projeto está versionado utilizando Git.

O código foi enviado para o repositório:

```text
Ring-Of-Judgment---Shadow-Hearts-Recomp
```

O Git será utilizado para criar checkpoints durante o desenvolvimento e permitir retornar a versões anteriores caso alguma alteração experimental cause problemas.

Arquivos de build, como:

```text
bin/
obj/
.vs/
```

não devem ser versionados.

---

# ⚠️ Sobre os arquivos do jogo

Este repositório contém apenas o código e as ferramentas desenvolvidas para o projeto.

**A ISO original de Shadow Hearts não faz parte do repositório.**

Nenhum arquivo proprietário do jogo deve ser incluído no GitHub.

O usuário deverá possuir sua própria cópia legal do jogo para realizar os testes.

---

# 📜 Licença

O projeto utiliza a licença **MIT** para o código desenvolvido neste repositório.

A licença se aplica ao código original do projeto e **não aos arquivos, personagens, músicas, texturas, modelos, código proprietário ou demais conteúdos pertencentes a Shadow Hearts ou aos seus respectivos detentores de direitos.**

---

# 🚧 Estado atual

**Ring of Judgment está em desenvolvimento inicial.**

Neste momento o projeto já consegue passar da ISO para sua estrutura de arquivos e possui a infraestrutura inicial para começar a analisar o ELF e o código MIPS.

O próximo grande marco será:

> **Encontrar o executável principal de Shadow Hearts e validar seu ELF.**

Depois disso começa uma das partes mais importantes do projeto:

> **analisar e reconstruir o código MIPS do jogo.**

---

## 🌟 Ring of Judgment

**Shadow Hearts Recompilation Project**

```text
ISO → ELF → MIPS → Functions → Recompiler → Runtime → Game
```

🖥️ Recursos planejados
Recurso	Objetivo	Status
Vulkan	Renderização multiplataforma	🟡 Planejado
Windows	Execução nativa	🟡 Planejado
Linux	Execução nativa	🟡 Planejado
macOS	Execução nativa	🟡 Planejado
60 Hz	Compatibilidade com a taxa original	🟡 Planejado
75 Hz	Suporte a monitores de 75 Hz	🟡 Planejado
120 Hz	Alta taxa de atualização	🟡 Planejado
144 Hz	Alta taxa de atualização	🟡 Planejado
Até 144 FPS/QPS	Maior taxa de atualização do jogo	🟡 Planejado
Resolução personalizada	Permitir resoluções modernas	🟡 Planejado
4:3	Proporção original	🟡 Planejado
16:9	Widescreen	🟡 Planejado
21:9	Ultrawide	🟡 Planejado
Fullscreen	Tela cheia	🟡 Planejado
Borderless	Janela sem bordas	🟡 Planejado
Janela	Execução em modo janela	🟡 Planejado
VSync	Sincronização vertical	🟡 Planejado
VRR	Compatibilidade futura com taxas variáveis	🟡 Planejado
Remapeamento de controles	Configuração personalizada	🟡 Planejado
Teclado e mouse	Entrada alternativa	🟡 Planejado
Controles modernos	Suporte a controles atuais	🟡 Planejado
Filtro de textura	Melhor qualidade visual	🟡 Planejado
Filtragem anisotrópica	Melhor qualidade de texturas	🟡 Planejado
Anti-aliasing	Redução de serrilhado	🟡 Planejado
Resolução interna escalável	Renderização acima da resolução original	🟡 Planejado
HUD adaptativo	Interface adequada a diferentes proporções	🟡 Planejado
Correção de câmera/FOV	Adaptação ao widescreen/ultrawide	🟡 Planejado
Áudio moderno	Sistema de áudio independente do hardware original	🟡 Planejado
Memory Card virtual	Sistema de saves moderno	🟡 Planejado
Ferramentas de debugging	Desenvolvimento e análise	🟡 Planejado
