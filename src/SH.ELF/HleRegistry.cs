using System.Text.RegularExpressions;

namespace SH.ELF;

// Funcoes do SDK/biblioteca que o runtime substitui por codigo nativo (HLE).
// A chave e o nome do simbolo no ELF; o valor e o corpo da funcao em C++, com
// acesso a `Ctx& c` (registradores do EE em c.r[...]) e ao runtime do ps2rt.h
// (mrd/mwr, ps2_call, ps2_cstr...). Registradores: a0..a3 = c.r[4..7].lo,
// retorno em c.r[2].lo. O gerador nao emite o corpo recompilado dessas funcoes:
// a tabela e as chamadas diretas apontam para hle_<nome>, escrito em ps2_hle.cpp.
// Nomes que nao existem no ELF sao ignorados.
public static class Hle
{
    // Camada SIF/RPC do SDK: o runtime nao emula o IOP. Se o jogo chegar nelas, o
    // programa para dizendo qual foi, em vez de executar a versao recompilada
    // (que esperaria para sempre por uma resposta do IOP).
    private static readonly string[] Pending =
    {
        "sceSifGetNextRequest", "sceSifExecRequest", "sceSifGetOtherData",
        "sceSifAddCmdHandler", "sceSifRemoveCmdHandler", "sceSifSendCmd",
        "sceSifSetCmdBuffer", "sceSifSetSysCmdBuffer",
        "sceSifLoadElf", "sceSifLoadElfPart", "sceSifLoadFileReset", "sceSifLoadMemory"
    };

    // Atencao: os campos estaticos iniciam na ordem em que aparecem; Pending vem antes
    // de Functions porque Build() usa Pending.
    public static readonly IReadOnlyDictionary<string, string> Functions = Build();

    private static Dictionary<string, string> Build()
    {
        Dictionary<string, string> d = new()
        {
            // libkernel: instala patches no kernel do console (SetSyscall, Copy para
            // 0x80076000 e arredores). O runtime implementa as syscalls nativamente.
            ["_InitSys"] = "    // nada a fazer: o kernel e o proprio runtime",

            // Versoes "i" (para interrupcoes) do libkernel: usam syscalls instaladas pelo
            // _InitSys. Aqui chamam direto a syscall equivalente do runtime.
            ["iWakeupThread"] = "    c.r[3].lo = (uint64_t)(int64_t)-52;\n    ps2_syscall(c);",
            ["iSuspendThread"] = "    c.r[3].lo = (uint64_t)(int64_t)-56;\n    ps2_syscall(c);",
            ["iRotateThreadReadyQueue"] = "    c.r[3].lo = (uint64_t)(int64_t)-44;\n    ps2_syscall(c);",

            // SIF/IOP: inicializacao e carga de modulos. Nao ha IOP: tudo "da certo" e os
            // servicos (disco, controle, memory card, som) viram versoes nativas da API.
            ["sceSifInitRpc"] = "    c.r[2].lo = 0;",
            ["sceSifExitRpc"] = "    c.r[2].lo = 0;",
            ["sceSifInitCmd"] = "    c.r[2].lo = 0;",
            ["sceSifExitCmd"] = "    c.r[2].lo = 0;",
            ["sceSifSyncIop"] = "    c.r[2].lo = 1;",
            ["sceSifWriteBackDCache"] = "    // cache: nada a fazer",
            ["sceSifStartModule"] = "    if (c.r[7].lo != 0) mwr<uint32_t>(c, (uint32_t)c.r[7].lo, 0);\n    c.r[2].lo = 0;",
            ["sceSifStopModule"] = "    if (c.r[7].lo != 0) mwr<uint32_t>(c, (uint32_t)c.r[7].lo, 0);\n    c.r[2].lo = 0;",
            ["sceSifUnloadModule"] = "    c.r[2].lo = 0;",

            // RPC: os servicos do IOP (arquivos do jogo, som) sao nativos (ver ps2_rpc no runtime).
            // O lado "servidor" do EE (fila e registro) nunca recebe pedidos: so devolvem sucesso.
            ["sceSifBindRpc"] = "    ps2_rpc(c, PS2_RPC_BIND);",
            ["sceSifCallRpc"] = "    ps2_rpc(c, PS2_RPC_CALL);",
            ["sceSifCheckStatRpc"] = "    ps2_rpc(c, PS2_RPC_CHECK);",
            ["sceSifSetRpcQueue"] = "    c.r[2].lo = 0;",
            ["sceSifRegisterRpc"] = "    ps2_rpc(c, PS2_RPC_REGISTER);",
            ["sceSifRemoveRpc"] = "    c.r[2].lo = 0;",
            ["sceSifRemoveRpcQueue"] = "    c.r[2].lo = 0;",
            ["sceSifRpcLoop"] = "    ps2_rpc_loop(c);",
            ["sceSifGetNextRequest"] = "    ps2_rpc(c, PS2_RPC_NEXT_REQUEST);",
            ["sceSifExecRequest"] = "    ps2_rpc(c, PS2_RPC_EXEC_REQUEST);",

            // printf do jogo: o SDK mandaria o texto para o PC de depuracao (DECI2) e ficaria
            // repetindo a chamada ate o host aceitar. Aqui mostra so o texto de formatacao.
            ["printf"] = """
                    static unsigned shown = 0;
                    char text[256];
                    ps2_cstr(c, (uint32_t)c.r[4].lo, text, sizeof(text));

                    if (shown < 150)
                    {
                        shown++;
                        size_t n = std::strlen(text);
                        std::fprintf(stderr, "  [jogo printf] %s%s", text, (n > 0 && text[n - 1] == '\n') ? "" : "\n");
                    }

                    c.r[2].lo = std::strlen(text);
                """,

            // FMV (temporario): o IPU ainda nao e emulado, entao o sceMpeg do SDK gira para sempre
            // procurando start code. Start__8MpegPlayFPc(this, caminho) vira "filme que acaba sozinho":
            //   - devolve 1 (sucesso, como o original);
            //   - zera this+428 (como o original) e nao cria o StreamRead (this+32448 fica nulo);
            //   - agenda o callback de fim (this+32464, ex.: VioWarn::MovieCBEnd) para daqui a
            //     PS2_FMV_FRAMES vsyncs (padrao 30), pois o chamador zera a flag de fim logo apos o retorno.
            // Quem espera o fim (VioWarn, state 1) segue quando o callback roda. Remover ao emular o IPU.
            ["Start__8MpegPlayFPc"] = """
                    static unsigned shown = 0;
                    uint32_t self = (uint32_t)c.r[4].lo;
                    char path[160];
                    ps2_cstr(c, (uint32_t)c.r[5].lo, path, sizeof(path));
                    const char* e = std::getenv("PS2_FMV_FRAMES");
                    uint32_t frames = e ? (uint32_t)std::strtoul(e, nullptr, 0) : 30u;
                    uint32_t endCb = mrd<uint32_t>(c, self + 32464u);

                    mwr<uint32_t>(c, self + 428u, 0u);

                    if (endCb != 0)
                        ps2_defer_call(frames, endCb, 0u);

                    if (shown < 20)
                    {
                        shown++;
                        std::fprintf(stderr, "  [FMV pulado] Start__8MpegPlay(\"%s\") fim=0x%08X em %u vsyncs\n", path, endCb, frames);
                    }

                    c.r[2].lo = 1;
                """,

            // FMV (temporario), par do Start acima. myDelete__8MpegPlayFv(this) e chamado pelo chamador
            // (VioWarn, state 1) quando o filme acaba. No jogo real ele apaga o semaforo, o buffer e a
            // thread do filme (DeleteThread numa thread que ainda dorme: ela nunca mais acorda) e depende
            // do Run/ThreadDeleteCallBack para disparar o callback de delete (this+32468, ex.:
            // VioWarn::MovieCBDelete, que o state 3/4 espera). Aqui: zera this+76 (como o original),
            // nao libera nada (o objeto fica orfao, sem risco de uso apos liberar) e agenda o callback.
            ["myDelete__8MpegPlayFv"] = """
                    static unsigned shown = 0;
                    uint32_t self = (uint32_t)c.r[4].lo;

                    // o jogo chama myDelete(NULL) em alguns caminhos (visto no log): nada a fazer
                    if (self == 0)
                        return;

                    uint32_t delCb = mrd<uint32_t>(c, self + 32468u);

                    // Libera o que o myDelete original libera, sem o DIntr/EIntr, sem o
                    // 0x3093D0 (vibracao) e sem esperar a thread do filme:
                    //   DeleteSema(this+0x50); delete this[436] (flag 1); delete[] this[400]
                    auto call = [&](uint32_t fn, uint32_t a0v, uint32_t a1v)
                    {
                        Ctx t = c;
                        t.r[4].lo = a0v;
                        t.r[5].lo = a1v;
                        ps2_call(t, fn);
                    };

                    call(0x00209A50u, self + 0x50u, 0u);

                    uint32_t obj = mrd<uint32_t>(c, self + 436u);

                    if (obj != 0)
                        call(0x00234890u, obj, 1u);

                    mwr<uint32_t>(c, self + 436u, 0u);
                    mwr<uint32_t>(c, self + 432u, 0u);

                    uint32_t arr = mrd<uint32_t>(c, self + 400u);

                    if (arr != 0)
                        call(0x00487CC0u, arr, 0x00237740u);

                    mwr<uint32_t>(c, self + 400u, 0u);
                    mwr<uint32_t>(c, self + 76u, 0u);

                    uint32_t stream = mrd<uint32_t>(c, self + 32448u);

                    if (stream != 0)
                        mwr<uint32_t>(c, stream + 104u, 0u);

                    if (delCb != 0)
                        ps2_defer_call(2u, delCb, 0u);

                    if (shown < 20)
                    {
                        shown++;
                        std::fprintf(stderr, "  [FMV pulado] myDelete__8MpegPlay(0x%08X) delete=0x%08X em 2 vsyncs\n", self, delCb);
                    }
                """,

            // libpad: controle nativo (ver ps2_pad no runtime)
            ["scePadInit"] = "    ps2_pad(c, PS2_PAD_INIT);",
            ["scePadEnd"] = "    ps2_pad(c, PS2_PAD_END);",
            ["scePadPortOpen"] = "    ps2_pad(c, PS2_PAD_PORT_OPEN);",
            ["scePadPortClose"] = "    ps2_pad(c, PS2_PAD_PORT_CLOSE);",
            ["scePadRead"] = "    ps2_pad(c, PS2_PAD_READ);",
            ["scePadGetState"] = "    ps2_pad(c, PS2_PAD_GET_STATE);",
            ["scePadInfoMode"] = "    ps2_pad(c, PS2_PAD_INFO_MODE);",
            ["scePadInfoAct"] = "    ps2_pad(c, PS2_PAD_INFO_ACT);",
            ["scePadSetMainMode"] = "    ps2_pad(c, PS2_PAD_SET_MAIN_MODE);",
            ["scePadSetActAlign"] = "    ps2_pad(c, PS2_PAD_SET_ACT_ALIGN);",
            ["scePadSetActDirect"] = "    ps2_pad(c, PS2_PAD_SET_ACT_DIRECT);",

            // Memoria do IOP: so enderecos de mentira (o EE nunca le essa memoria diretamente).
            ["sceSifInitIopHeap"] = "    c.r[2].lo = 0;",
            ["sceSifAllocIopHeap"] = "    static uint32_t next = 0x00100000u;\n    uint32_t size = (uint32_t)c.r[4].lo;\n    c.r[2].lo = next;\n    next += (size + 63u) & ~63u;",
            ["sceSifFreeIopHeap"] = "    c.r[2].lo = 0;",

            // libcdvd: leitura direto da ISO (ver ps2_cd no runtime)
            ["sceCdInit"] = "    ps2_cd(c, PS2_CD_INIT);",
            ["sceCdMmode"] = "    ps2_cd(c, PS2_CD_MMODE);",
            ["sceCdDiskReady"] = "    ps2_cd(c, PS2_CD_DISK_READY);",
            ["sceCdGetDiskType"] = "    ps2_cd(c, PS2_CD_GET_DISK_TYPE);",
            ["sceCdRead"] = "    ps2_cd(c, PS2_CD_READ);",
            ["sceCdSync"] = "    ps2_cd(c, PS2_CD_SYNC);",
            ["sceCdSearchFile"] = "    ps2_cd(c, PS2_CD_SEARCH_FILE);",
            ["sceCdStatus"] = "    ps2_cd(c, PS2_CD_STATUS);",
            ["sceCdGetError"] = "    ps2_cd(c, PS2_CD_GET_ERROR);",
            ["sceCdBreak"] = "    ps2_cd(c, PS2_CD_BREAK);",
            ["sceCdGetReadPos"] = "    ps2_cd(c, PS2_CD_GET_READ_POS);",
            ["sceCdStInit"] = "    ps2_cd(c, PS2_CD_ST_INIT);",
            ["sceCdStStart"] = "    ps2_cd(c, PS2_CD_ST_START);",
            ["sceCdStRead"] = "    ps2_cd(c, PS2_CD_ST_READ);",
            ["sceCdStStop"] = "    ps2_cd(c, PS2_CD_ST_STOP);",

            ["sceSifRebootIop"] = """
                    char name[256];
                    std::fprintf(stderr, "  sceSifRebootIop(\"%s\")\n", ps2_cstr(c, (uint32_t)c.r[4].lo, name, sizeof(name)));
                    c.r[2].lo = 1;
                """,

            // sceSifLoadModule(arquivo, tamanho_args, args): devolve o id do modulo
            ["sceSifLoadModule"] = """
                    char name[256];
                    uint32_t id = ps2_alloc_id();
                    std::fprintf(stderr, "  sceSifLoadModule(\"%s\") -> modulo %u\n", ps2_cstr(c, (uint32_t)c.r[4].lo, name, sizeof(name)), id);
                    c.r[2].lo = id;
                """,

            // sceSifLoadStartModule(arquivo, tamanho_args, args, resultado)
            ["sceSifLoadStartModule"] = """
                    char name[256];
                    uint32_t id = ps2_alloc_id();
                    std::fprintf(stderr, "  sceSifLoadStartModule(\"%s\") -> modulo %u\n", ps2_cstr(c, (uint32_t)c.r[4].lo, name, sizeof(name)), id);
                    if (c.r[7].lo != 0) mwr<uint32_t>(c, (uint32_t)c.r[7].lo, 0);
                    c.r[2].lo = id;
                """
        };

        foreach (string name in Pending)
            d.TryAdd(name, $"    ps2_hle_unimplemented(c, \"{name}\");");

        return d;
    }

    public static bool Contains(string symbolName) => Functions.ContainsKey(symbolName);

    public static string HostName(string symbolName) =>
        "hle_" + Regex.Replace(symbolName, "[^A-Za-z0-9_]", "_");
}
