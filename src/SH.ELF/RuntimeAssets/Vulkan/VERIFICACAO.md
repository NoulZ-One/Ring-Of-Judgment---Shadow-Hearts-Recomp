# Verificacao da integracao Vulkan

GPU detectada e usada: AMD Radeon RX 6600. Runtime C++ compilado com G++ -O3.

Comparacao de replays reais, no mesmo executavel, selecionando CPU ou Vulkan.
Oito repeticoes por processo; a primeira foi descartada para a mediana.
Os testes restauram o estado e a VRAM antes de cada repeticao.

| Captura | CPU, mediana | Vulkan, mediana | Reducao do tempo |
| --- | ---: | ---: | ---: |
| Trem antes da batalha | 40,95 ms | 26,66 ms | aproximadamente 35% |
| Batalha | 41,87 ms | 38,42 ms | aproximadamente 8% |

Todos esses resultados apresentaram zero palavras divergentes nas 4 MiB de
VRAM em relacao a referencia gravada. O backend tambem foi conferido por
triangulo, refazendo cada desenho na CPU. A versao final foi testada com
decodificacao de texturas adiada e escolha automatica dos lotes.

Esses tempos cobrem os comandos graficos capturados. Nao equivalem ao FPS
do jogo completo, nem incluem toda a logica, audio e apresentacao. O Vulkan
implementado nao garante 60 FPS constantes.

Outras verificacoes concluidas:

- SPIR-V validado para Vulkan 1.1.
- Driver indisponivel simulado com VK_DRIVER_FILES: fallback CPU sem
  divergencias e com codigo de saida zero.
- Persistencia CPU/Vulkan, idioma e preferencias anteriores no INI.
- Compilacao do launcher, runtime nativo e projetos C#.
- Executavel instalado em out verificado por SHA-256 contra a compilacao.
- Replay do executavel instalado, usando renderer=1 do INI real: sem
  divergencias, com lotes executados na GPU.

Os logs e capturas desta sessao ficam em .ring-build/performance e
.ring-build/vulkan. Os saves e os arquivos de midia nao foram modificados.

## Otimizacao posterior: transferencias parciais e texturas

Comparacao direta com a versao Vulkan anteriormente instalada, usando os
mesmos replays e a mesma GPU. Dez repeticoes por processo, descartando a
primeira para a mediana:

| Captura | Vulkan anterior | Vulkan otimizado |
| --- | ---: | ---: |
| Batalha contra Bacon | 40,102 ms | 27,179 ms |
| Trem | 27,462 ms | 26,095 ms |

A reducao no teste grafico da batalha foi de aproximadamente 32%. Isso nao
e uma medicao do novo FPS minimo durante a gameplay.

As transferencias de VRAM do replay da batalha passaram de aproximadamente
32 MiB por lote completo de comandos para 4,48 MiB, contando ida e volta.
Esse numero nao inclui os buffers de texturas e metadados. A decodificacao
das texturas tambem passou a usar leitores especializados por formato.

Todas as repeticoes permaneceram com zero palavras divergentes. O teste
de transferencia parcial passou nos seis casos dos dois framebuffers,
incluindo profundidade somente para leitura e preservacao de sentinelas.
Logs: optimization-before-battle.log, optimization-after-battle.log e
equivalentes do trem, em .ring-build/vulkan.
