# Efeitos externos por banco e ID

Integração autorizada pelo usuário em 19/11/2026: 330 WAVs de `Aprovados.zip`,
três aprovações explícitas no TXT e os demais reextraídos após revisão parcial
de 12 páginas. Total instalado: 6.994 IDs em 863 bancos, incluindo 841 entradas
silenciosas. A autorização para usar o restante não é uma afirmação de que
cada efeito foi ouvido e validado individualmente.

As identificações do TXT estão em `effect-labels.json`. Yuri, Halley e Keith
continuam marcados como prováveis. Os grupos de personagem não afirmam o
contexto exato de cada som. A grafia `01C_S002` no TXT foi interpretada como
`01C_S/002`, preservando o ID existente.

Na raiz do projeto:

```powershell
pwsh -File ./src/SH.Core/Ferramentas/Instalar-Efeitos.ps1 -Revisao './out/EfeitosSonorosRevisao' -AprovadosZip './out/EfeitosSonorosRevisao/Aprovados.zip'
```

O gerador C++ também exporta a ferramenta e seus dados para `out/Ferramentas`.
Requer PowerShell 7 e MinGW g++; Node é opcional, usado somente para atualizar
o catálogo HTML. A ferramenta não extrai novamente a ISO e não exige a antiga
`.ring-build`: as tabelas de banco e comportamento acompanham a source.

O ZIP é lido como dados WAV, sem executar instruções contidas nele. Os aprovados
são preservados, com SHA-256, em `AprovadosRecebidos`. A prioridade de instalação
é ZIP > arquivo original apontado pelo catálogo; os três efeitos explicitamente
aprovados no TXT também recebem a marca individual. O restante é autorizado
para uso e conserva seu estado de identificação. Arquivos ausentes são listados
no manifesto e não são recuperados de uma pasta antiga contra a decisão do usuário.

O preparador gera cópias PCM estéreo a 22.050 Hz, a taxa atual do mixer,
usando sinc com janela e coeficientes polifásicos. Não altera os WAVs aprovados
ou reextraídos de referência. Só reduz ganho se a reamostragem exceder PCM16.
Quatro workers preparam o lote; essa conversão não roda durante gameplay.

Estrutura necessária para reproduzir o pacote:

```text
Audio/Sound Effects/
  banks.tsv
  effects.tsv
  regist01/015.wav
  EVHT12_S/004.wav
  ...
```

`manifesto-efeitos.json` registra origem, aprovação, identificação, hash,
duração e estado de repetição de cada arquivo. Os catálogos e ZIPs de revisão
não são dependências do executável depois de preparar o pacote.

O runtime associa os dados recebidos do jogo aos bancos por três fingerprints
FNV-1a de 64 bits: SEDS sem os 12 bytes iniciais, SWD primário completo e SWD
auxiliar completo. O caminho nunca vem de texto arbitrário da ISO. Isso mantém
variantes de um mesmo banco e números de efeito repetidos separados, mesmo
quando o handle atribuído pelo jogo muda. Fallback por SEDS/primário ou somente
SEDS é permitido apenas se identificar uma única pasta; ambiguidades retornam
ao sintetizador existente. `build_effect_banks.cjs` reconstrói a tabela a partir
do plano de extração e da ISO, quando necessário. Não há dados de áudio da ISO
embutidos na DLL ou no executável.

Os WAVs são carregados sob demanda no worker de áudio, com cache LRU de 16 MiB
e até 32 disparos externos simultâneos. Vozes ativas conservam sua referência
ao PCM mesmo se o cache o expulsar. Assim o limite de cache não significa um
limite absoluto de 16 MiB para toda a memória das vozes. Não se pré-carregam
milhares de arquivos no início do jogo. Volume, estéreo e panorama da chamada
original são aplicados; o volume de efeitos do launcher permanece separado
da música. Stop por efeito, Stop por banco e descarregamento encerram as vozes.

Prioridade em execução: aprovado individualmente no pacote > WAV manual de
menu/estilhaço já configurado > demais externos autorizados > sintetizador RSSD.
Uma entrada realmente silenciosa é tratada como silêncio, sem substituí-la por
um som inventado do fallback. Arquivo ausente ou malformado usa o sintetizador
e registra o problema uma vez. Reinicie o jogo após trocar arquivos; execute
novamente o instalador para atualizar índices, hashes e marcações de silêncio.

Correção aprovada em 19/11/2026: `regist01/010` (obtenção de item) usa a
reconstrução de exatamente 2 segundos aprovada pelo usuário. O WAV de referência
aprovado fica em `out/CorrecoesAudio/Aprovados/regist01/010.wav`; seu SHA-256 está
em `effect-labels.json`. O instalador procura essa correção (ou a mesma cópia já
instalada) e impede que o efeito antigo a substitua em uma preparação futura.
Use `-Correcoes` para indicar outro diretório de correções aprovadas. O áudio
permanece externo; não é embutido na source.

Limitação preservada: 491 prévias atingiram o limite de extração. As entradas
contínuas com áudio repetem a prévia até o comando Stop do jogo, com uma rampa
de aproximadamente 3 ms na emenda. Isso evita que o efeito pare somente porque
a prévia acabou, mas não reconstrói os eventos ausentes além de 12/30 segundos.
O ponto musical da emenda e a duração dessas entradas continuam sujeitos a
revisão. Isso inclui `EVHT12_S/004`; não foi declarado fiel integralmente.

Testes: `RuntimeAssets/tests/external-effects.cpp` cobre variantes de banco,
prioridade, estéreo, volume/panorama, sobreposição, repetição, Stop, silêncio,
fallback e limites de vozes/cache. O modo com assets verifica todos os 6.994
arquivos; opcionalmente recalcula, pelo mesmo C++ usado no runtime, os 863
fingerprints diretamente da ISO e os compara com a tabela preparada.
