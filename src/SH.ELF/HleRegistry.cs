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
