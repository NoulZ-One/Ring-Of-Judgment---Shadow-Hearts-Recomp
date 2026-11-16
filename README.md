


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

> 6. O jogo chegou em sua primeira gameplay

O jogo apresentou a sua primeira gameplay. é possível passar da tela de título e chegar a primeira e segunda tela do trem (com sorte se não iniciar alguma batalha)

---


| **Subsistema** | **Situação indicada pelos logs** |
|---|---|
| CPU e instruções MIPS/R5900 | O código é executado, mas há instruções que ainda precisam ser implementadas ou verificadas. |
| Memória e execução | O executável é carregado e o runtime avança por diversas rotinas; não há validação completa de todos os acessos à memória e exceções. |
| Sistema de arquivos | Leituras da ISO e de arquivos do jogo foram observadas com sucesso. |
| Syscalls e serviços do PS2 | Diversos serviços são chamados; falta validar completamente os resultados e a semântica de cada implementação. |
| DMA e processamento gráfico | Há atividade registrada, mas não está demonstrado que todo o fluxo de transferência e renderização esteja correto. |
| Memory Card | Há chamadas como `sceMcGetSlotMax`, `sceMcOpen` e `sceMcClose`, mas não há comprovação de gravação e carregamento persistentes. |
| Áudio e controles | Há sinais de inicialização e chamadas relacionadas, mas não há teste funcional comprovado. |
--

**O JOGO EXIBIU SUAS PRIMEIRAS IMAGENS**

---

## Próximo passo / prioridade atual:

**Corrigir e estabilizar a renderização do GS (Graphics Synthesizer)**

Marco alcançado: primeiras imagens do jogo!

O projeto alcançou um importante marco técnico: as primeiras imagens reconhecíveis de Shadow Hearts foram geradas com sucesso!
Ao executar o ps2_game.exe, o runtime produz arquivos no formato .PPM, contendo os primeiros resultados visuais do jogo, incluindo o logotipo da Aruze e a tela de título de Shadow Hearts.
Esse avanço demonstra que partes importantes do pipeline gráfico do PlayStation 2, incluindo o processamento de comandos gráficos e a escrita na memória gráfica, já estão funcionando na implementação própria do projeto.

## Estado atual

Embora as primeiras imagens sejam reconhecíveis, a renderização ainda apresenta problemas, como duplicação de elementos, posicionamento incorreto, faixas pretas e áreas não renderizadas corretamente.

## V1 
- Duplicação das imagens corrigida;
- Paleta de cores das logos corrigidas (aruze e a tela de título do jogo);

## V2

- A janela do jogo agora é exibida sem necessidade de arquivos PPM para visualizar o  jogo e o desenvolvimento;
- Fixado o copyright do menu de título;
- O jogo passa da tela de título. é possível iniciar um novo jogo;
- A primeira tela e a segunda tela do trem é carregada com sucesso;
- O yuri agora é visível, embora ele apareça duplicado;
- O menu ingame aparece, embora bastante pixelado; 
- O jogo inicia a primeira batalha. Quando os personagens entram em pose de combate, o jogo crasha;

## V3

- Foi corrigido o crash da batalha (testei isso na base do Slow-Motion, fui puramente guerreiro nessa correção);
- O tremor da tela do game foi corrigido. Isso acontecia conforme precisava testar diferentes perfis de compilação;
- Fixado o Yuri Duplicado  

## V4 Patch de grande impacto

- Foi fixado o render da batalha;
- Foi Fixado um problema anormal em que, quando terminava a batalha, a imagem do jogo quebrava totalmente;
- Foi Fixado o Slow-Motion do game (VIVA!!!!);
- Suporte a controle adicionado! Agora é possível usar um controle de XBOX para jogar!


## Para os próximos patch's do recompilador irei focar em:
- No desempenho geral do jogo (rodar 60 FPS e eliminar os stutter).
- Fixar o Serrilhado das letras (geral);
- Adicionar o Áudio do jogo; (essa parte vai dar bastante trabalho....)
- Fixar o pixelado dos personagens

**As músicas do jogo vão dar um enorme trabalho para ajustar e deixar como elas são. peguei como exemplo a Brain Hopper -  tema de batalha do jogo - e o trabalho que está dando é extremamente grande.**

## Conforme for aparecendo problemas, serão listados abaixo.
- Crash imediato do recompilador no diálogo "Up There!?"   

## Nota: O recompilador ainda não está pronto. Volto a Frisar bem: Quando o recompilador estiver pronto, uma BUILD será liberada. Por hora, apenas acompanhe os Logs de desenvolvimento.

## Nota 2: É possível que os próximos Logs demorem um pouco mais que o normal para serem exibidos ou atualizados. Só farei atualizações tanto no repositório, quanto nos logs, quando obtiver avanços significativos.


---
## Sobre o código

Ao executar a solução (`dotnet run --project src\SH.Core`), é gerada a pasta `OUT/`, contendo os arquivos C++ (`.cpp`). o executável do jogo precisa ser compilado a parte usando msys64.
Ao executar o `ps2_game.exe`, são gerados logs que permitem analisar o comportamento do runtime, identificar problemas e investigar os próximos bloqueios durante a execução do jogo.

---
## Primeiras imagens do jogo / First images of the game 
<img width="640" height="448" alt="gs_002" src="https://github.com/user-attachments/assets/d78ebecf-8f64-42f5-bd33-7e08db4d28d0" />
<img width="637" height="477" alt="image" src="https://github.com/user-attachments/assets/852a01c4-40df-4c61-ba82-9d4d26334291" />
<img width="1510" height="812" alt="image" src="https://github.com/user-attachments/assets/d78a7058-9e35-42e7-8bf4-acc9560e1e31" />
<img width="1919" height="1032" alt="image" src="https://github.com/user-attachments/assets/02e8002a-0ab9-4e6b-976d-b0b090dda5c1" />
<img width="1510" height="812" alt="image" src="https://github.com/user-attachments/assets/16ec21c7-0f71-46fb-9e22-b5cb28723d29" />

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
- There are still 11 instructions reported as unsupported at this stage (9 VCALLMSR, 1 VCALLMS e 1 PMULTH).

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

> 6. The game has reached its first playable state

The game has unveiled its first gameplay. It is now possible to move past the title screen and reach the first and second screens of the train (provided a battle doesn't trigger).

---

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

> **THE GAME HAS REVEALED ITS FIRST IMAGES** 

---

## Next step / Current Priority:

**Fixing and stabilizing GS (Graphics Synthesizer) rendering**

Milestone reached: first in-game images!

The project has hit a significant technical milestone: the first recognizable images from *Shadow Hearts* have been successfully generated!

When running `ps2_game.exe`, the runtime produces files in .PPM format, containing the initial visual output from the game—including the Aruze logo and the *Shadow Hearts* title screen.

This breakthrough demonstrates that key components of the PlayStation 2 graphics pipeline—such as the processing of graphics commands and writing to graphics memory—are already functional within the project’s custom implementation.

## Current Status

Although the initial images are recognizable, the rendering still presents issues, such as duplicated elements, incorrect positioning, black bars, and areas that are not rendered correctly.

## V1
- Image duplication fixed;
- Logo color palettes corrected (Aruze and the game's title screen);

## V2
- The game window now displays without requiring PPM files to view the game and its development;
- Fixed the copyright notice on the title menu;
- The game proceeds past the title screen; it is possible to start a new game;
- The first and second train screens load successfully;
- Yuri is now visible, though he appears duplicated;
- The game initiates the first battle. When the characters enter their combat stance, the game crashes;
- The in-game menu appears, though it is quite pixelated;

## V3

- Fixed a crash occurring during battles (I tested this using slow-motion; it took a real battle to track down and fix this one).
- Fixed screen shaking. This issue arose while I was testing different compilation profiles.
- Yuri Duplicated Fixed

## V4 – Major Patch

- Fixed battle rendering.
- Resolved an issue where the game’s visuals would break completely upon the conclusion of a battle.
- Fixed the game’s slow-motion functionality (HOORAY!!!!).
- Controller support added! You can now use an Xbox controller to play!

## For upcoming patches to the recompiler, I will focus on:
- Overall game performance (targeting 60 FPS and eliminating stuttering).
- Fixing jagged edges (aliasing) on ​​text throughout the game.
- Adding in-game audio.
- Fixing pixelation issues on character models.

**Adjusting the game's music tracks to keep them exactly as they are is going to be a massive undertaking. I’ve been using "Brain Hopper"—the game’s battle theme—as an example, and the amount of work it’s requiring is truly immense.**

## As issues arise, they will be listed below.
- Immediate crash of the recompiler during the "Up There!?" dialogue


## Note: The recompiler is not yet complete. To reiterate: once the recompiler is finished, a build will be released. For now, please simply follow the development logs.

## Note 2: Future logs may take a bit longer than usual to appear or be updated. I will only push updates to the repository and the logs once significant progress has been made.


---

## About the code

When running the solution (`dotnet run --project src\SH.Core`), an `OUT/` folder is generated, containing C++ (`.cpp`) files. The game executable must be compiled separately using msys64.
Running `ps2_game.exe` produces logs that make it possible to analyze runtime behavior, identify issues, and investigate any crashes that may occur while the game is running.

---
