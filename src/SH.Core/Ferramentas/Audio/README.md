# Revisão dos efeitos RSSD

Ferramenta de revisão separada do executável do jogo. Não altera a ISO, os saves,
os WAVs aprovados ou a reprodução ao vivo. Requer PowerShell 7, Node.js 22+
e MinGW g++ com C++17. Não precisa de FFmpeg.

Na raiz do projeto:

```powershell
pwsh -File ./src/SH.Core/Ferramentas/Preparar-Efeitos.ps1 -Iso './out/Data/Shadow Hearts (USA).iso' -AprovadosZip '../audios certos.zip'
```

Saída padrão: `out/EfeitosSonorosRevisao`. Mantém `Aprovados` byte a byte;
somente os IDs ausentes no ZIP são renderizados em `Reextraidos`.
`Catalogo.html` permite comparar com a extração anterior. `manifesto.json`
registra hashes, duração, pico, ganhos, limites e recursos SPU utilizados.
O ZIP precisa manter `audios certos/BANCO/ID.wav`. Nenhuma instrução contida
em arquivos é executada. O conteúdo esperado é apenas WAV.

O inventário identifica SEDS/SWD na ISO; os scripts montam uma imagem do
SSD.IRX e o backend executa seu código MIPS original. O binário do driver
é obtido da ISO do usuário, nunca incluído no fonte. A versão é validada
por SHA-256, pois os pontos de entrada são específicos da edição USA.
Os bancos comuns necessários e a sequência têm posições desta ISO.

Revisão de 19/11/2026: coeficientes gaussianos de quatro pontos no lugar
de interpolação linear; decodificação ADPCM por blocos com histórico
preservado a cada volta; ENDX; volumes com sinal e sweep; PMON; ruído por
núcleo; redução de ganho apenas nas somas que excedem a faixa PCM16.
A atenuação é medida e aplicada em uma segunda renderização, antes de
saturar. Não se tenta remover ruído do próprio efeito ou mudar sua altura.
As sequências rodam a 240 Hz, com PCM estéreo a 48 kHz.

Limitações: reverberação SPU não implementada; posição e volume por cena
não disponíveis nesta prévia isolada; loops limitados a 12 segundos,
demais sequências a 30. Uma sequência silenciosa pode ser uma reserva,
usar amostra vazia ou depender de parâmetros externos. Não substituir
por uma onda arbitrária nem declarar inutilizada. Todos os reextraídos
aguardam comparação auditiva com o emulador; somente o ZIP está aprovado.

`check_spu.cpp` testa histórico ADPCM, ENDX, interpolação, volumes,
ruído e modulação com dados sintéticos. `legacy` no último argumento
do backend conserva o mixer anterior para comparação controlada.

Referências de hardware/API consultadas (implementação local):

- [PSX-SPX: ADPCM, interpolação, volume e ruído](https://psx-spx.consoledev.net/soundprocessingunitspu/)
- [PS2SDK: identificadores libsd e registros da SPU2](https://github.com/ps2dev/ps2sdk/blob/master/common/include/libsd-common.h)
- [PCSX2: comportamento de loop e PMON na SPU2](https://github.com/PCSX2/pcsx2/blob/master/pcsx2/SPU2/Mixer.cpp)
