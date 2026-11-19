


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

Já inserido.

Already inserted.

---

# Progresso //  Progress

O progresso atual é aproximadamente:

## **Aproximadamente 80%**

> Essa porcentagem representa o avanço estrutural do projeto como um todo e não significa que 80% do jogo já esteja recompilado.
> Conforme for avançando, a porcentagem será modificada.
> Quando estiver concluído, será liberado uma build.

--

Current progress:

## **Approximately 80%**
 
> This percentage represents the structural progress of the project as a whole and does not mean that 80% of the game has already been recompiled.
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
O recompilador atualmente está cerca de 80%. É possível iniciar um novo jogo, salvar nos 2 slots, carregar um save existente. Ainda há coisas a serem feitas que serão listadas abaixo, entretanto, o recompilador demonstrou avanços significativos.

Ainda requer testes para saber se é totalmente jogável do começo até o fim do jogo. Aos poucos vou atualizando o Readme com os avanços.


---

## Próximo passo / prioridade atual:

**otimizações**
**Corrigir Efeitos sonoros e as músicas**

É possível que próximo patch o recompilador utilize efeitos sonoros externos (como os vídeos atuais). assim previne problemas com efeitos sonoros estourados ou chiando.

## Estado atual

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

## V5 Patch
Muitas coisas foram corrigidas e inseridas.
- Vulkan adicionado;
- Letras do menu agora são legíveis;
- Fundo escuro nos diálogos foi corrigido;
- Launcher próprio para o jogo foi criado. Nele tem opções de: 
   - Resolução externa até 4K; 
   - Controle de volume dos efeitos sonoros e músicas. conta com uma opção de Volume geral; 
   - Renderizador (CPU ou VULKAN);
   - Estilo das letras (Faixa preta como no original e uma nova opção: Borda preta!); 
   - Proporção de Imagem (no momento apenas 2 estão disponíveis. 4:3 e 16:9) estou vendo a possibilidade de por 21:9 também;
   - Limite de quadros: 30 ou 60 FPS; 
   - Opções de janela (Tela Cheia, Janela sem bordas e Tela cheia sem bordas);
   - Mapeamento de botões 
- Os vídeos do jogo apresentavam uma quebra de som horrível. foi feito mudanças para o recompilador carregar os vídeos de forma externa;
- Função de Salvar e Carregar o Save (apenas slot 1 por enquanto);

 ## V5.1 Patch
  - Resultado da tela da batalha corrigido
  - Escala de resolução interna aprimorada até 720p (somente em VULKAN). isso fixa o pixelado dos personagens e inimigos.
  - Desempenho do recompilador levemente melhorado (ainda precisa de mais otimização)
  - Analógico esquerdo melhorado (Antes as diagonais faziam o personagem caminhar, interrompendo a corrida)
  - Slot 2 do memory card agora está habilitado. É possível salvar nele também.
  - Alguns pixelados das letras das batalhas foram arrumados (possivelmente apenas das batalhas comuns. batalhas contra boss ainda será investigado);
 
  ## Próximo patch possivelmente demore a sair. Correções de Áudio e efeitos sonoros irão dar trabalho. É possível que eu passe os Efeitos Sonoros e as Músicas de forma externa como os vídeos. Assim evita problemas com chiados ou áudios estourados.


## Para os próximos patch's do recompilador irei focar em:
- No desempenho geral do jogo (sempre será necessário validar o desempenho).
- Conferir o Áudio do jogo (essa parte vai dar bastante trabalho....);
- Fixar o serrilhado das letras na batalha e alguns pontos que passou batido;
- Corrigir e Adicionar corretamente os efeitos sonoros do jogo;

**As músicas do jogo e os efeitos sonoros vão dar um enorme trabalho para ajustar e deixar como são. peguei como exemplo a Brain Hopper -  tema de batalha do jogo - e o trabalho que está dando para ajustar é extremamente grande.**

## Conforme for aparecendo problemas, serão listados abaixo.
- Efeitos sonoros quebrados (desde os efeitos sonoros da batalha até os de exploração) - precisará de um bom tempo para arrumar;
- Arrumar as músicas do jogo (algumas aparecem com algum apito estranho ou não batem com a original);

## Nota: O recompilador está quase pronto. Volto a Frisar bem: Quando o recompilador estiver pronto, uma BUILD será liberada. Por hora, apenas acompanhe os Logs de desenvolvimento.

## Nota 2: É possível que os próximos Logs demorem um pouco mais que o normal para serem exibidos ou atualizados. Só farei atualizações tanto no repositório, quanto nos logs, quando obtiver avanços significativos.


---
## Sobre o código



---
## imagens do jogo / Images of the game 

<img width="1919" height="1032" alt="image" src="https://github.com/user-attachments/assets/e4c1f944-1122-4e13-921c-0c235b1e2951" />
<img width="1919" height="1032" alt="image" src="https://github.com/user-attachments/assets/2deba2df-4c9b-4258-b7fd-376f37c1f4be" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/1a3825dd-aa24-41ce-9746-b01ef1c94249" />
<img width="1919" height="1031" alt="image" src="https://github.com/user-attachments/assets/a6f7f5fb-26b6-443c-82db-a76e16ea9349" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/0cc906bd-04cc-4605-b570-6f6c4daf202b" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/dba5b99e-2c67-47fe-a5ce-a223e462eba2" />
<img width="1919" height="1032" alt="image" src="https://github.com/user-attachments/assets/5d5abec8-41d9-4753-bc80-379e6fcd53ec" />
<img width="1919" height="1031" alt="image" src="https://github.com/user-attachments/assets/2e8aad64-3eed-4ca2-94e0-696f3ca2599c" />
<img width="1919" height="1032" alt="image" src="https://github.com/user-attachments/assets/820812c7-8ece-4986-a01f-575e733f9c78" />
<img width="1919" height="1031" alt="image" src="https://github.com/user-attachments/assets/ac23a7fd-d1d6-4f7b-aaa6-5ae8d329d790" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/c2903bc7-154a-4b11-942b-1e3904edd6ce" />
<img width="1919" height="1031" alt="image" src="https://github.com/user-attachments/assets/39f41958-65bb-42ce-92c2-7d990bface10" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/23d6f532-8437-434a-8416-529ae02897f7" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/9f863b80-7566-434d-9015-3d14d3045f09" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/ee353b90-21b2-4226-a028-89d58d27fbce" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/6b6d2a08-525c-4850-bf05-4fbf32cf486a" />
<img width="1919" height="1029" alt="image" src="https://github.com/user-attachments/assets/7dfde869-c6e3-4e6c-8789-89fe29d5959a" />














---
# Current Status

The recompiler is currently about 80% complete. It is possible to start a new game, save (currently only in slot 1), and load an existing save file. There are still tasks to be completed—listed below—but the recompiler has shown significant progress.

Further testing is required to determine whether the game is fully playable from start to finish. I will gradually update the Readme as progress is made.

---

> **THE GAME HAS REVEALED ITS FIRST IMAGES** 

---

## Next step / Current Priority:

**Fix visual defects on characters and implement optimizations**
**Fix sound effects and music**

It is possible that in the next patch, the recompiler will use external sound effects (similar to the current videos). This will help prevent issues with sound effects that are distorted or crackling.

## Current Status

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

## V5 patch
Many fixes and additions have been implemented:

- Vulkan support added;
- Menu text is now legible;
- The dark background for dialogue boxes has been corrected;
- A dedicated game launcher has been created, featuring the following options:
    - External resolution settings up to 4K;
    - Volume controls for sound effects and music, including a master volume option;
    - Renderer selection (CPU or Vulkan);
    - Text style (the original black bar style, plus a new "black outline" option);
    - Aspect ratio (currently limited to 4:3 and 16:9);
    - Frame rate limit: 30 or 60 FPS;
    - Window modes (Full Screen, Borderless Window, and Borderless Full Screen).
    - Button mapping
- The in-game videos suffered from terrible audio glitches. Changes were made to the recompiler so that the videos are loaded externally.
- Save and Load functionality (slot 1 only for now);
 ## V5.1 Patch
- Battle screen result fixed 
- Improved internal resolution scaling up to 720p (VULKAN only). This fixes the pixelation of characters and enemies. 
- Slightly improved recompiler performance (still needs more optimization) 
- Improved left analog stick (Previously the diagonals made the character walk, interrupting the run) 
- Memory card slot 2 is now enabled. You can save to it too.
- Fixed pixelation issues with text during battles (likely limited to standard encounters; boss battles are still under investigation).

## Next patch may take some time to come out. Audio corrections and sound effects will take work. It is possible for me to broadcast the Sound Effects and Music externally like videos. This avoids problems with hissing or bursting audio.

## For upcoming patches to the recompiler, I will focus on:
- Regarding overall game performance (performance will always need to be validated):
- Check the game audio (this part is going to be quite a lot of work...);
- Fix the jagged edges (aliasing) on ​​the text during battle, as well as a few other things that were overlooked;
- Correct and properly add the game's sound effects;


**Adjusting the game's music tracks to keep them exactly as they are is going to be a massive undertaking. I’ve been using "Brain Hopper"—the game’s battle theme—as an example, and the amount of work it’s requiring is truly immense.**

## As issues arise, they will be listed below.
- Broken sound effects (ranging from battle sound effects to those for exploration) – will require a significant amount of time to fix;
- Fix the game's music (some tracks have a strange whistling sound or do not match the original versions);;


## Note: The recompiler is not yet complete. To reiterate: once the recompiler is finished, a build will be released. For now, please simply follow the development logs.

## Note 2: Future logs may take a bit longer than usual to appear or be updated. I will only push updates to the repository and the logs once significant progress has been made.


---

## About the code



---
