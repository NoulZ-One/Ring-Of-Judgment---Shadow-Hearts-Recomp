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

## 2026-11-20: importacao e passe nativo Vulkan por regiao

`vulkan-native-regions.cpp` compara a VRAM inteira e todos os pixels
ampliados com `PS2_GS_VK_FULL_NATIVE=1`, em 2x e 720p, nos tres destinos
(0, 2240, 5760). Inclui escritas CPU nas bordas de blocos e arredondamento
da escala 720/224. Ambos passaram. Reset de destino continua integral;
as dependencias de textura continuam sendo importadas.

Tambem passaram os testes de resolucao interna, fog e composicao offscreen.
Na reproducao de 230.000 comandos GS de uma batalha, os 91 lotes passaram
na validacao CPU/GPU e produziram BMPs com SHA256 identico. Importacoes
caem de 82.001.920 para 26.032.896 bytes. Na ultima comparacao na RX 6600,
o tempo acumulado GPU/sincronizacao foi de 84,269 para 71,385 ms (~15%).
Uma medicao preliminar deu ~27%; o tempo varia entre execucoes. Esses
numeros medem o caminho grafico capturado, nao o FPS total do jogo.

A otimizacao vale para qualquer cena que use os destinos ampliados
compativeis, incluindo exploracao e menus. Nao muda o relogio do jogo.
O F9 tambem pode salvar `gs_vu_sphere.bin` com o microprograma e os
registradores efetivos da esfera de fundo. Isso permite investigar o
fundo branco sem depender de uma leitura posterior da micro memoria,
que e reutilizada durante o quadro. Nao ha escrita desse arquivo sem F9.

## 2026-11-20: esfera de fundo e latencia MAC

O programa da esfera em 0x1800 executa o teste FMAND quatro pares depois
da operacao que calcula o sentido do triangulo. A leitura imediata pegava
os indicadores da transformacao do proximo vertice e descartava o ceu.
O historico MAC agora avanca por par no interpretador e nos blocos AOT
integrados; FMEQ/FMAND/FMOR leem o resultado com quatro pares de atraso.
O formato Z/S/U/O e o estado final no contexto permanecem iguais.

`vu-flags.cpp` testa leitura antecipada, sobrescritas intermediarias e
avanco de um bloco AOT real. Na reproducao isolada de CSphere.Draw com
RAM e microprograma capturados, o total de desenhos passou de 0 para 353,
e a imagem mostra a textura original do ceu. Validacao visual na alpha
ainda depende de copiar o executavel atualizado.

`sky-panorama.cpp` valida texturas CT32/T8 de 1024x256 com o teste alfa
NEVER/FB_ONLY usado pelo ceu, nos dois framebuffers, em 2x e 720p.
Ambos passaram: processamento na GPU e VRAM inteira identica a CPU.
