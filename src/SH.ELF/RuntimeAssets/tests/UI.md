# Fontes dos menus e valores do anel

As letras continuam vindo dos atlas originais. O runtime conserva as linhas
originais da textura antes de reconstruir o framebuffer de 224 para 448 linhas.
Os perfis adicionais sao restritos as paletas observadas nas capturas locais:

- 5492: New Game, Continue e Vibration, inclusive filtragem linear e AFAIL FB_ONLY.
- 5470: copyright do menu inicial, desenhado por dois triangulos.
- 5613: titulo Command Menu.
- 16344: texto, numeros e sombras do menu, inclusive Z ALWAYS com escrita ativa.
- 5708: nomes e valores de BtlStat2 na batalha, atlas 512x128, inclusive
  Z ALWAYS com escrita ativa. A captura de 19/11 mostrou que essa fonte
  permanecia no framebuffer de 224 linhas e perdia metade das linhas do atlas.
- 5532, CSA 4: nome no topo (atlas 256x32) e ajuda inferior (1024x32),
  tambem com Z ALWAYS e escrita ativa. A ajuda usa TBP 5588: o limite
  anterior de TBP 5760 rejeitava esse texto mesmo com a paleta identificada.
- 5708, P8 256x128: numeros e legendas da tela de resultados.
- 5732, P8 128x32: valor de dinheiro inferior.
- 5582: letra G, limitada ao retangulo UV identificado na captura.

A passagem auxiliar conserva alfa e profundidade do desenho original; somente
RGB passa para a composicao em altura nativa. O contorno/faixa dos dialogos
continua usando o perfil de paleta 16348 e a preferencia salva no launcher.

O HUD do anel agora vem exclusivamente do desenho original de `BtlStat::Draw`
(0x002AD950, SLUS-20347). A correcao dos flags MAC do VU restaurou as legendas
e os valores cianos nativos. A antiga camada com fonte GDI se sobrepunha a
eles e foi removida, junto com seus hooks, gerador e recurso incorporado.
O comando GS de diagnostico de tipo 3 continua aceito como no-op, para que
replays antigos nao reintroduzam os valores sinteticos nem sejam confundidos
com escritas em registradores privilegiados.

## Teste de regressao

Depois de gerar `out/ps2_runtime.cpp`, compile `ui-ring.cpp` separadamente
com as mesmas flags de fixture de `battle-font.cpp`. Nao vincule a tabela de
funcoes do jogo: a fixture tem um callback nativo controlado para BtlStat.
Execute com `PS2_GS_THREAD=0`.

O teste exige uma unica chamada ao desenho original em call e tailcall,
nenhum glifo sintetico adicional, comandos antigos de tipo 3 sem duplicacao,
escala do texto nativo em 2x/720p, os dois framebuffers e RAM/VRAM intactas.

As capturas reais do Command Menu e da batalha tambem foram reproduzidas
nos caminhos CPU e Vulkan. Os pixels finais e as 4 MiB de VRAM coincidiram
entre os dois. No Command Menu, a comparacao com o caminho anterior encontrou
zero diferencas em alfa, profundidade e texturas; RGB muda intencionalmente
para preservar as linhas das fontes na imagem final. A verificacao local
do menu inicial tambem reconstroi os tres sprites e os dois triangulos usando
o atlas capturado, sobre fundo neutro.

Esses testes usam capturas e nao substituem conferir a transicao e o anel
durante uma partida com o novo executavel.

## Nitidez da cena e fonte da batalha

`text_aa` suaviza os glifos, sem aplicar um segundo filtro a cena inteira.
O filtro global anterior tambem borrava detalhes dos personagens e inimigos.
O filtro de cena opcional de diagnostico continua em `PS2_GS_PRESENT_AA`.
A remocao desse filtro e independente da resolucao interna. O caminho
Original conserva o framebuffer GS de 640x224 e a reconstrucao espacial.
Os modos ampliados do Vulkan usam buffers separados; veja
`../Vulkan/README.md` e a fixture `internal-resolution.cpp`.

`battle-font.cpp` e uma fixture isolada do renderizador com o estado GS
real registrado em BtlStat2. Nao executa funcoes nem syscalls do jogo.
Compile separadamente conforme o comentario no arquivo e execute com
`PS2_GS_THREAD=0`, depois novamente com `PS2_GS_UI_NATIVE=0`.
Verifica a preservacao das 18 linhas do atlas, posicao e escritas de alfa
e profundidade. Passando um caminho como argumento, grava as 4 MiB de
VRAM com o RGB do framebuffer mascarado; os arquivos dos dois modos
devem ser identicos. Isso verifica que a cor movida para a composicao
nao altera alfa, profundidade nem a memoria das texturas.

O filtro dos glifos integra quatro subpixels reconstruidos pela continuidade
do contorno (regra Scale2x), em vez de aplicar blur em cruz. O limiar acompanha
o alfa maximo do glifo para conservar o fade. A fixture verifica diagonais
com cobertura fracionaria, hastes de um pixel intactas, simetria, cores sem
franja escura e sombra preta. Tambem cobre os dois atlas da paleta 5532 em
ambos os framebuffers, preservacao de alfa/Z e rejeicao de outro CSA.

Para o descarte dos vertices da tela de resultados, veja `RESULTADOS.md`.
A correcao atua nos flags MAC do VU e nao cria um HUD substituto nem
inventa valores a partir de posicoes fixas na tela.
