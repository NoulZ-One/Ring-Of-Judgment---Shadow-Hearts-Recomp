# Vulkan no runtime recompilado

O `MipsCppEmitter.cs` integra `device.h`, `batch.inc` e o shader `raster.spv`
em `ps2_runtime.cpp`. Esses arquivos acompanham `SH.ELF.dll` como recursos.
O jogo distribuido nao precisa de shader externo, SDK, arquivo INC ou PS1.
No Windows, o loader `vulkan-1.dll` vem do driver grafico instalado.

O seletor **Renderizador** do launcher salva `renderer=1` para Vulkan ou
`renderer=0` para CPU. A configuracao vale na proxima execucao. Na ausencia
de Vulkan, o runtime mantem o renderizador por CPU. O shader e compilado
para Vulkan 1.1. Esta integracao Vulkan foi implementada para Windows;
o caminho de outros sistemas continua por CPU.

## Desenho e compatibilidade

O backend compute agrupa triangulos por framebuffer, monta listas de
primitivas por blocos de 8 x 8 pixels e conserva a ordem original do GS.
O pixel percorre sua lista em ordem, com os testes de alfa, profundidade,
blend inteiro, mascara de escrita, CLAMP/REPEAT e filtragem da versao CPU.
Os intervalos de rasterizacao e a decodificacao das texturas usam as
mesmas rotinas da CPU. Retangulos triangulados de cor constante conservam
a interpolacao por linha do caminho `FastRaster`.

A decodificacao das texturas e adiada ate a escolha do backend do lote,
com uma copia da paleta vigente. Lotes destinados a CPU evitam esse trabalho
duplicado. A escolha considera a quantidade de pixels e o tipo de desenho,
pois transferir alguns triangulos pequenos pode custar mais que desenha-los.

Os buffers de trabalho ficam na memoria da GPU. Transferencias e fences
sincronizam os resultados com a VRAM do runtime antes de desenhos por CPU,
uploads, leituras dependentes e apresentacao. Os sprites e fontes continuam
no caminho existente. A VRAM do GS continua no formato original; a resolucao
interna ampliada usa buffers separados. O seletor de resolucao externa do
launcher continua controlando o tamanho da janela ou da tela cheia.
A apresentacao da janela continua no caminho GDI existente; Vulkan executa
o desenho dos lotes selecionados, antes da composicao e apresentacao.

As transferencias de VRAM usam listas de blocos de 256 bytes, agrupados
em faixas consecutivas. O conjunto de leitura inclui a profundidade mesmo
quando ZMSK impede sua escrita. O conjunto devolvido a CPU inclui somente
blocos potencialmente escritos. As demais palavras da VRAM permanecem
intactas. A decodificacao de cada textura escolhe o formato antes do laco
de texels, usando os mesmos leitores especializados da versao CPU.

O teste `tests/partial-transfers.cpp` compara os 4 MiB com a CPU nos dois
framebuffers, com Z mascarado rejeitando/aceitando pixels e Z gravavel.
Tambem verifica sentinelas fora da area desenhada. Ele inclui o runtime
gerado em out e deve ser vinculado aos objetos do jogo, exceto ps2_main.o
e ps2_runtime.o. Execute com PS2_GS_RENDERER=vulkan e PS2_GS_VK_SINGLE=1;
o teste exige uma GPU Vulkan ativa, para impedir um falso positivo por
fallback.

O caminho inicial cobre CT32 nos framebuffers 0/70 de 640 x 224, Z16S e
texturas decodificadas de ate 512 x 512 nos formatos ja suportados pelo GS.
Estados diferentes e texturas que realimentam a propria area de escrita
usam CPU. Lotes pequenos tambem podem usar CPU para evitar o custo de ida
e volta a GPU. Uma falha de submissao refaz o lote a partir da VRAM original
e dos estados salvos das primitivas, depois mantem CPU para a sessao.

Nao ha geracao de quadros nem alteracao da velocidade da logica do jogo.
O limite de 60 FPS nao garante 60 FPS em todas as cenas.

## Resolucao interna

`internal_resolution` no INI seleciona Original (0: GS 640x224 com a
reconstrucao existente), 2x (1: 1280x448) ou 720p (2: 1280x720). O padrao
para configuracoes antigas continua Original. E preciso reiniciar o jogo.
`PS2_GS_INTERNAL_RESOLUTION=0/1/2` permite comparar os modos em diagnostico.

Os vertices e os texels sao reamostrados em cada pixel da imagem ampliada;
nao e apenas um aumento da imagem pronta. Cor e Z16 ficam em regioes
separadas do buffer Vulkan, fora das 4 MiB originais. O desenho nativo
continua sendo executado para preservar leituras de framebuffer, efeitos,
transferencias e a logica do jogo. A imagem ampliada usa uma regra de
arestas compartilhadas para evitar blend duplicado entre dois triangulos.

Antes de cada lote e da apresentacao, o shader sincroniza as mudancas
feitas pela CPU. Snapshots da imagem nativa distinguem essas mudancas dos
detalhes novos rasterizados na GPU. A limpeza do framebuffer invalida o
buffer correspondente, incluindo quando a cor de fundo nao mudou.
Os glifos originais e o HUD sao compostos nas coordenadas da nova imagem,
com interpolacao de cobertura premultiplicada; o estilo de contorno/faixa
permanece independente. A captura F11 registra as dimensoes finais corretas.

Os modos ampliados exigem Vulkan. Estados de desenho fora do caminho
compativel continuam usando o resultado nativo da CPU na regiao afetada;
na ausencia/falha de Vulkan, a apresentacao volta ao caminho Original.
O custo inclui rasterizacao nativa e ampliada, sincronizacao e leitura da
imagem maior. A taxa de quadros deve ser medida em gameplay.

`tests/internal-resolution.cpp`, compilado como fixture isolada do runtime,
exige uma GPU Vulkan real. Execute com PS2_GS_THREAD=0,
PS2_GS_RENDERER=vulkan e PS2_GS_INTERNAL_RESOLUTION=1 e depois 2. Verifica
geometria subpixel, gradientes, textura em perspectiva, oclusao por Z,
transparencias em arestas compartilhadas, persistencia entre lotes,
sobreposicoes da CPU, limpeza e os dois framebuffers. A VRAM nativa deve
continuar identica, nas 4 MiB, ao desenho de referencia na CPU.

## Atualizacao do shader

Depois de editar `raster.comp`, atualize o recurso antes de compilar SH.ELF:

```powershell
C:/msys64/ucrt64/bin/glslangValidator.exe -V -Os --target-env vulkan1.1 -o src/SH.ELF/RuntimeAssets/Vulkan/raster.spv src/SH.ELF/RuntimeAssets/Vulkan/raster.comp
C:/msys64/ucrt64/bin/spirv-val.exe --target-env vulkan1.1 src/SH.ELF/RuntimeAssets/Vulkan/raster.spv
dotnet build src/SH.Core/SH.Core.csproj -c Release
```

Gere novamente o C++ com SH.Core e compile usando
`out/Ferramentas/Compilar-Jogo.ps1`. O compilador precisa dos headers Vulkan;
o shader precompilado ja esta integrado ao gerador.

## Verificacao com comandos reais do GS

`PS2_GS_REPLAY_1X` seleciona um arquivo gravado pelo diagnostico F9.
`PS2_GS_RENDERER=cpu` ou `vulkan` substitui a escolha do launcher somente
naquele processo. O replay compara as 4 MiB de VRAM com o resultado
registrado; o codigo de saida indica qualquer divergencia.

`PS2_GS_REPLAY_REPEAT=12` repete a mesma captura no mesmo processo para
medir o custo depois da inicializacao da GPU. Cada repeticao restaura o
estado inicial e a VRAM, sem carregar ou escrever saves. Descarte a primeira
medicao ao comparar desempenho continuo. O tempo do replay nao inclui toda
a logica, audio ou apresentacao da janela e nao equivale ao FPS do jogo.

Para conferir cada triangulo separadamente, use `PS2_GS_VK_SINGLE=1` e
`PS2_GS_VK_VALIDATE=1`. Esse modo e deliberadamente lento e refaz cada lote
na CPU para comparar a memoria. `PS2_GS_VK_PROFILE=1` detalha os custos de
preparo, transferencia e sincronizacao; nao deve ser usado como configuracao
normal do launcher.
