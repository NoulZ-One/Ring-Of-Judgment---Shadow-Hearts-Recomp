# Resultados de batalha e flags VU

O clipper de sprites de `GsxSetRmSprVertexL` usa FMAND para consultar o
sinal de Z no MAC. O runtime montava os nibbles em ordem incorreta, de
modo que essa consulta retornava zero e os vertices finais recebiam ADC
de descarte. Os valores ja faltavam na VRAM nativa, antes do Vulkan 720p.

O layout e Z nos bits 0..3, S em 4..7, U em 8..11 e O em 12..15,
com x no bit 3 de cada campo e w no bit 0. STATUS faz a reducao desses
campos. Zero negativo tambem conserva o sinal no MAC. Referencia de
implementacao: [PCSX2 VUflags.cpp](https://github.com/PCSX2/pcsx2/blob/master/pcsx2/VUflags.cpp).
Nao foram introduzidos valores de HP, MP, SP ou dinheiro substitutos.

`vu-flags.cpp` verifica os quatro campos, as quatro lanes, zero negativo,
STATUS e um programa VU0 sintetico com SUBi.z e FMAND. A captura local
da tela real tambem foi reproduzida no programa de diagnostico privado:
os vertices finais passaram de ADC de descarte para desenho e os numeros
originais voltaram a aparecer. Capturas de RAM/VRAM nao fazem parte dos
assets necessarios a compilacao ou distribuicao.

Os atlas P8 da paleta 5708 (HP/MP/SP, nivel e experiencia) e 5732 (dinheiro)
usam a composicao de fonte nativa, preservando linhas antes da ampliacao.
A letra G da paleta 5582 fica restrita ao retangulo UV 172,40--180,56,
para nao promover a arte do painel a camada de texto. `battle-font.cpp`
verifica esses tres perfis nos dois framebuffers, incluindo alfa/Z.

## Otimizacoes da apresentacao ampliada

Os passes de desenho e sincronizacao dos lotes Vulkan usam o retangulo
conservador ocupado pelas primitivas. A inicializacao depois de limpar
o framebuffer e a sincronizacao final continuam cobrindo a imagem toda.
As tabelas de pagina e o formato original de 4 MiB nao mudam.
O snapshot de cada lote so reconhece como sincronizados os pixels nativos
daquela regiao; caso contrario, um painel desenhado pela CPU fora dela
seria descartado na apresentacao. O teste de regressao escreve um pixel
da CPU fora do proximo lote e exige que ele apareca depois da resolucao.
Esse teste falhou na primeira versao limitada e passou com a correcao.
`PS2_GS_HI_FULL_FRAME=1` permite executar a referencia com os passes
ampliados de cada lote cobrindo a tela inteira.

A reconstrucao espacial 224->448 deixa de ser calculada quando a imagem
ampliada ja esta pronta e iria substitui-la. Continua funcionando em
Original/fallback e nas capturas F11, que guardam as tres etapas.

`internal-resolution.cpp --bench arquivo.bin` executa tambem um cenario
sintetico de 60 quadros, oito lotes pequenos por quadro, e grava os pixels
finais. A comparacao local em 720p, na RX 6600, produziu arquivos identicos
com e sem o retangulo limitado. Duas medicoes deram reducoes aproximadas
de 2 a 3% nesse cenario. Isso nao equivale ao FPS do jogo e nao mede
isoladamente o ganho de evitar a reconstrucao espacial.
