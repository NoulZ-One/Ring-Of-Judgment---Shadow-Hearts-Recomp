# Músicas externas

As 57 sequências SMD com identificação BGM da edição USA foram associadas a
57 faixas do ZIP fornecido pelo usuário. O banco também tem uma sequência
dummy sem identificação musical válida; ela não recebe uma faixa arbitrária.
As outras nove faixas do álbum ficam preservadas em `Originais`, sem associação
automática a uma BGM. Esse grupo inclui versões de abertura, encerramento e bônus.

O mapeamento foi obtido comparando características de altura/espectro do áudio
com referências renderizadas do SSD.IRX original, não pela ordem do álbum.
`bgm302` foi desambiguada com uma referência de 140 segundos; `bgm327` foi
comparada como vinheta curta de pousada. Os IDs são normalizados sem distinção
de maiúsculas e aceitam a grafia original `bgm_403`.

`music-map.tsv` guarda o nome do MP3, os pontos de repetição em quadros PCM de
22.050 Hz e o ganho. O observador offline do driver mediu marcas e finais de
952 pistas; a maioria coincide com a pista condutora. A comparação entre
repetições do próprio álbum refinou somente os períodos com correlação alta.
Os demais mantêm os pontos da sequência. A transição recebe uma rampa de cerca
de 3 ms para evitar estalos, sem alterar a duração do período.

**Pendências explícitas:** o álbum termina antes do ciclo completo da ISO nas
faixas abaixo. Foram configuradas para tocar uma vez; não se inventou uma
emenda nem se declarou o loop corrigido. Precisam de uma gravação completa
do ciclo no jogo ou de validação de um trecho alternativo de repetição.

| Código | Faixa | Ciclo da ISO | Áudio do ZIP |
| --- | --- | --- | --- |
| bgm321 | Trip or treat | 161,15 s | 115,95 s |
| bgm416 | Reckless | 85,50 s | 63,99 s |
| bgm417 | Sicking fucking | 95,75 s | 77,68 s |

O mapeamento e os demais loops também devem ser conferidos durante gameplay.
A comparação automática é evidência de correspondência, não uma validação
auditiva de todas as cenas. As três pendências aparecem como `loop_pendente`
no manifesto gerado.

Na raiz do projeto, para preparar a partir dos MP3s:

```powershell
pwsh -File ./src/SH.Core/Ferramentas/Preparar-Musicas.ps1 -Zip '../Musicas originais.zip'
```

A ferramenta também é exportada para `out/Ferramentas/Preparar-Musicas.ps1`
ao gerar o C++. Ela preserva os MP3s, decodifica com Media Foundation do
Windows e gera PCM estéreo de 16 bits na taxa do mixer atual. A conversão usa
reamostragem sinc com janela; não aplica remoção de ruído, normalização de
volume ou alteração de tom. O formato WAV é um derivado de reprodução:
converter um MP3 para WAV não recupera informação perdida na compressão.
O executável não precisa de FFmpeg nem de novas bibliotecas de codecs.

Estrutura instalada:

```text
Audio/Music/
  music.tsv
  bgm101.wav ... bgm506.wav
  manifesto-musicas.json
  Originais/  (os 66 MP3s preservados)
```

O runtime usa caminhos relativos ao executável, lê PCM em blocos pequenos
no worker de áudio e aplica o volume principal e de música já existentes no
launcher. Os efeitos continuam em seu canal separado. Play/Stop do RSSD
controlam o início e a troca das faixas. Uma ausência ou formato inválido
retorna ao sintetizador SMD/SWD existente. Os arquivos externos não são
embutidos no executável ou na DLL do recompilador.

`external_music.h` integra a classe ao C++ gerado. O teste
`RuntimeAssets/tests/external-music.cpp` cobre alias, estéreo, ganho,
repetição, final de reprodução e rejeição de arquivos/configurações inválidos.

Referência da API de decodificação:
[Microsoft: Source Reader e áudio PCM](https://learn.microsoft.com/en-us/windows/win32/medfound/tutorial--decoding-audio).
