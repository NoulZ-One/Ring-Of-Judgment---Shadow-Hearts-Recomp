


<img width="1000" height="1500" alt="image" src="https://github.com/user-attachments/assets/e7142463-c96f-45e9-a678-1ebed0403ff0" />


## Ring Of Judgment: Shadow Hearts Recompilation


> **Em Andamento** // **Work In Progress**
 ---

## Sobre o projeto  // About this Project

**Ring of Judgment** 

O projeto está sendo desenvolvido principalmente em **C# / .NET** 10.

The project is being developed primarily in **C# / .NET** 10.

---

# Vulkan

> No Futuro, é certo a implementação de **Vulkan**, para que também seja possível jogar no linux.


> In the future, the implementation of **Vulkan** is certain, which will also make it possible to play on Linux.

---

# Progresso //  Progress

O progresso atual é aproximadamente:

## **~50%**

> Essa porcentagem representa o avanço estrutural do projeto como um todo e não significa que 50% do jogo já esteja recompilado.
> Conforme for avançando, a porcentagem será modificada.
> Quando estiver concluído, será liberado uma build.

--

Current progress:

## **~50%**
 
> This percentage represents the structural progress of the project as a whole and does not mean that 50% of the game has already been recompiled.
> As development progresses, the percentage will be adjusted.
> Once completed, a build will be released.

---

## C#/.NET

**Por que você está realizando essa recompilação em C#?**

> Decidi fazer essa recompilação em C# porque eu estudo C#. Vou usar o código que está sendo construido por IA como estudos, então sim, eu pretendo voltar a esse código no futuro mesmo quando ele estiver totalmente funcional. E se possível também, modificar mais algumas coisas.


**Why are you performing this recompilation in C#?**
> I decided to do this re-implementation in C# because I am studying C#. I intend to use the code being built by AI for study purposes; so yes, I plan to revisit this code in the future, even once it is fully functional.   And, if possible, I also want to make some further modifications.

---

# Estado atual


> 1. Carregamento do executável ELF

- O executável do jogo foi carregado no endereço 0x00200000.
- A análise estática identificou 4.706 funções com símbolos.
- O processo já consegue avançar da etapa de carregamento para a execução de código.

> 2. Geração de código C++

- Foram geradas 4.708 funções C++.
- 62 funções receberam implementações alternativas via HLE (High-Level Emulation).
- A geração não registrou falhas.
- Ainda existem 11 instruções reportadas como não suportadas nessa etapa.

> 3. Leitura da ISO e dos arquivos do jogo

- A ISO americana foi reconhecida.
- As operações de leitura do sistema de arquivos aparecem com resultado ok.
- O runtime localizou e leu arquivos como MODELS.PKB, COMMON.PKB e TEXTURE.PKB.
- Isso demonstra que a infraestrutura de leitura já está avançada, embora não prove que todos os recursos são interpretados corretamente.


> 4. Execução de serviços e criação de threads

Os logs registram atividade relacionada a:
- Criação de threads e uso de semáforos.
- Chamadas RPC.
- Serviços FIO, RSSD e MCSERV.
- Inicialização de componentes associados a controles e áudio.
Isso mostra que o jogo já interage com diversas partes do ambiente de execução. Ainda não comprova que todos esses serviços tenham comportamento equivalente ao do PS2 original.

> 5. Chegada às rotinas gráficas

Foram observadas chamadas a funções como RenderPrim, RenderPmBlend, GsxSetRmVertex e rotinas relacionadas a DMA e ao processamento gráfico.
Isso é um marco importante: o fluxo do jogo alcança código relacionado à renderização. Porém, ainda não há comprovação de que os comandos gráficos produzam uma imagem correta na tela.


| **Subsistema** | **Situação indicada pelos logs** |
|---|---|
| CPU e instruções MIPS/R5900 | O código é executado, mas há instruções que ainda precisam ser implementadas ou verificadas. |
| Memória e execução | O executável é carregado e o runtime avança por diversas rotinas; não há validação completa de todos os acessos à memória e exceções. |
| Sistema de arquivos | Leituras da ISO e de arquivos do jogo foram observadas com sucesso. |
| Syscalls e serviços do PS2 | Diversos serviços são chamados; falta validar completamente os resultados e a semântica de cada implementação. |
| DMA e processamento gráfico | Há atividade registrada, mas não está demonstrado que todo o fluxo de transferência e renderização esteja correto. |
| Memory Card | Há chamadas como `sceMcGetSlotMax`, `sceMcOpen` e `sceMcClose`, mas não há comprovação de gravação e carregamento persistentes. |
| Áudio e controles | Há sinais de inicialização e chamadas relacionadas, mas não há teste funcional comprovado. |

---

> **Onde travou a execução**:

Instrução sem suporte
Endereço: 0x002FCAFC · Função: GsxSetRmVertex
O runtime interrompe a execução ao encontrar a instrução VCALLMSR.
Também foram identificadas outras instruções reportadas como não suportadas na geração, incluindo VCALLMS e PMULTH.
Próxima ação técnica: examinar a palavra de instrução e o código ao redor de 0x002FCAFC, determinar a semântica correta e implementar o comportamento necessário. 
Não é seguro simplesmente substituir a instrução por um NOP, porque isso pode eliminar operações essenciais ao processamento gráfico.

---

## Próximo passo / prioridade atual:

**Resolver o bloqueio VCALLMSR**

VCALLMSR no endereço 0x002FCAFC

---
## Sobre o código

Ao executar a solução (`dotnet run --project src\SH.Core`), é gerada a pasta `OUT/`, contendo os arquivos C++ (`.cpp`) e o executável `ps2_game.exe`.
Ao executar o `ps2_game.exe`, são gerados logs que permitem analisar o comportamento do runtime, identificar problemas e investigar os próximos bloqueios durante a execução do jogo.

---

# Current Status

> 1. Loading of the ELF executable

- The game executable was loaded at address 0x00200000.
- Static analysis identified 4,706 functions with symbols.
- The process is now able to move past the loading stage and proceed to code execution.

> 2. C++ code generation

- 4,708 C++ functions were generated.
- 62 functions received alternative implementations via HLE (High-Level Emulation).
- The generation process encountered no failures.
- There are still 11 instructions reported as unsupported at this stage.

> 3. Reading of the ISO and game files

- The ISO USA has been recognized.
- File system read operations are returning an "OK" result.
- The runtime has located and read files such as MODELS.PKB, COMMON.PKB, and TEXTURE.PKB.
- This demonstrates that the read infrastructure is already well-developed, though it does not prove that all assets are being interpreted correctly.

> 4. Execution of services and creation of threads

The logs record activity related to:
- Thread creation and the use of semaphores.
- RPC calls.
- FIO, RSSD, and MCSERV services.
- Initialization of components associated with controls and audio.
This shows that the game already interacts with various parts of the execution environment. It does not yet prove that all these services behave in a way equivalent to the original PS2.

> 5. Reaching the graphics routines

Calls to functions such as RenderPrim, RenderPmBlend, and GsxSetRmVertex—along with routines related to DMA and graphics processing—have been observed.
This is a significant milestone: the game’s execution flow is reaching code associated with rendering. However, there is no confirmation yet that these graphics commands actually produce a correct image on the screen.


| **Subsystem** | **Status indicated by logs** |
|---|---|
| CPU and MIPS/R5900 instructions | Code is being executed, but some instructions still require implementation or verification. |
| Memory and execution | The executable is loaded, and the runtime progresses through various routines; however, not all memory accesses and exceptions have been fully validated. |
| File system | Successful read operations from the ISO and game files have been observed. |
| PS2 Syscalls and services | Various services are being called; the results and the semantics of each implementation have yet to be fully validated. |
| DMA and graphics processing | Activity has been recorded, but it has not been demonstrated that the entire transfer and rendering flow is correct. |
| Memory Card | Calls such as `sceMcGetSlotMax`, `sceMcOpen`, and `sceMcClose` are present, but there is no confirmation of persistent saving or loading. |
| Audio and controls | There are signs of initialization and related calls, but no proven functional testing. |

---

> **Where execution stalled**:

Unsupported instruction
Address: 0x002FCAFC · Function: GsxSetRmVertex
The runtime halts execution upon encountering the VCALLMSR instruction.
Other instructions reported as unsupported during generation—specifically VCALLMS and PMULTH—have also been identified.
Next technical step: examine the instruction word and the code surrounding 0x002FCAFC, determine the correct semantics, and implement the required behavior.
Simply replacing the instruction with a NOP is not safe, as this could eliminate operations essential to graphics processing.

---

## Next step / Current Priority:

**Resolve the VCALLMSR block**

at address 0x002FCAFC

---

## About the code

When running the solution (`dotnet run --project src\SH.Core`) generates an `OUT/` folder, which contains the C++ files (`.cpp`) and the executable `ps2_game.exe`.
Running `ps2_game.exe` produces logs that make it possible to analyze runtime behavior, identify issues, and investigate any crashes that may occur while the game is running.

---
