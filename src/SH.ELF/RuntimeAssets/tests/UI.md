# Fontes dos menus e valores do anel

As letras continuam vindo dos atlas originais. O runtime conserva as linhas
originais da textura antes de reconstruir o framebuffer de 224 para 448 linhas.
Os perfis adicionais sao restritos as paletas observadas nas capturas locais:

- 5492: New Game, Continue e Vibration, inclusive filtragem linear e AFAIL FB_ONLY.
- 5470: copyright do menu inicial, desenhado por dois triangulos.
- 5613: titulo Command Menu.
- 16344: texto, numeros e sombras do menu, inclusive Z ALWAYS com escrita ativa.

A passagem auxiliar conserva alfa e profundidade do desenho original; somente
RGB passa para a composicao em altura nativa. O contorno/faixa dos dialogos
continua usando o perfil de paleta 16348 e a preferencia salva no launcher.

`ring_hud.inc` e incorporado ao C++ pelo MipsCppEmitter, sem novo arquivo de
dados necessario ao executavel. O dispatch original de `BtlStat::Draw`
(0x002AD950, SLUS-20347) publica uma fotografia do proprio HUD na fila GS.
Os campos do objeto foram identificados em `BtlStat::SetVal`, `DspCircle` e
confirmados na captura de batalha:

| Campo | Offset |
| --- | --- |
| Visibilidade | B60, bit 4 |
| Posicao X / Y em linhas do campo | B62 / B64, int16 |
| HP / MP / SP atuais | B66 / B6A / B6E, int16 |
| Alfa do HUD | B78, uint8 de 0 a 128 |

Os valores maximos sao ignorados. As tres legendas usam ciano e ficam
centralizadas acima das barras originais; acompanham o deslocamento de 28
pixels entre elas. A fila associa os glifos ao framebuffer, substitui o texto
anterior e preserva o fade. Nao ha escrita na RAM do jogo nem na VRAM.

## Teste de regressao

Depois de gerar `out/ps2_runtime.cpp` e os objetos das funcoes recompiladas,
compile `ui-ring.cpp` com C++17 e esses objetos, excluindo `ps2_main.o` e
`ps2_runtime.o`. No Windows, vincule gdi32, user32, ole32, shell32, uuid e winmm.
Execute com `PS2_GS_THREAD=0`.

O teste verifica valores atuais, independencia dos maximos, zero, SP negativo,
ciano, posicionamento acima das tres barras, substituicao, fade, ocultacao,
dois framebuffers e preservacao da memoria do jogo.

As capturas reais do Command Menu e da batalha tambem foram reproduzidas
nos caminhos CPU e Vulkan. Os pixels finais e as 4 MiB de VRAM coincidiram
entre os dois. No Command Menu, a comparacao com o caminho anterior encontrou
zero diferencas em alfa, profundidade e texturas; RGB muda intencionalmente
para preservar as linhas das fontes na imagem final. A verificacao local
do menu inicial tambem reconstroi os tres sprites e os dois triangulos usando
o atlas capturado, sobre fundo neutro.

Esses testes usam capturas e nao substituem conferir a transicao e o anel
durante uma partida com o novo executavel.
