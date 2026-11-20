using System.Text;
using System.Text.RegularExpressions;

namespace SH.ELF;

// Gerador de C++ a partir de uma funcao MIPS (R5900), um subconjunto por vez.
// Instrucoes ainda sem suporte viram chamadas a ps2_unsupported(...), e sao
// contadas no resultado. O texto gerado e so ASCII (compila em MSVC e GCC).
public static class MipsCppEmitter
{
    public sealed record Result(
        string Code,
        int Instructions,
        int Unsupported,
        IReadOnlyDictionary<string, int> UnsupportedByMnemonic,
        int RuntimeDispatches);

    // Runtime minimo que o codigo gerado espera. Sem aspas duplas aqui dentro.
    public const string RuntimeHeader = @"#pragma once
#include <atomic>
#include <cstdint>
#include <cstring>
#include <cmath>

// Registrador de 128 bits do EE. As operacoes de 64 bits usam so o lo.
struct Reg128 { uint64_t lo; uint64_t hi; };

struct Ctx
{
    Reg128 r[32];     // r[0] nunca e escrito: o codigo gerado pula escritas em $zero
    uint64_t hi, lo;  // HI/LO (pipeline 0)
    uint64_t hi1, lo1;  // HI1/LO1 (pipeline 1)
    uint32_t sa;      // registrador SA (QFSRV): deslocamento em bits
    uint32_t f[32];   // registradores da FPU, como bits de float
    uint32_t acc;     // acumulador da FPU
    uint32_t fcr31;   // controle da FPU: bit 23 = condicao (C)
    uint32_t vf[32][4];  // VU0: registradores VF (x, y, z, w) como bits de float
    uint32_t vacc[4];    // VU0: acumulador
    uint32_t vi[32];     // VU0: VI0..VI15 (16 bits) e controle: 16 status, 17 MAC, 18 clip, 20 R, 21 I, 22 Q, 23 P, 27 CMSAR0
    uint8_t* mem;     // RAM do EE (32 MB) no host
    uint8_t* spr;     // scratchpad do EE (16 KB) no host
};

static inline uint64_t S32(uint32_t v) { return (uint64_t)(int64_t)(int32_t)v; }

// Diagnostico: compile com -DPS2_TRACE para guardar as ultimas funcoes entradas.
#ifdef PS2_TRACE
void ps2_trace_enter(Ctx& c, uint32_t address);
#define PS2_TRACE_ENTER(a) ps2_trace_enter(c, a)
// Ultimo rotulo de desvio executado: mostra em que laco o jogo esta preso.
extern volatile uint32_t g_loop_mark;
#define PS2_LOOP_MARK(a) (g_loop_mark = (a))
#else
#define PS2_TRACE_ENTER(a) ((void)0)
#define PS2_LOOP_MARK(a) ((void)0)
#endif

// Interrupcoes (vsync, timer 0): a thread de tique do host liga g_irq_pending; o codigo gerado
// confere a flag em cada rotulo de desvio e entrega a interrupcao ali.
extern std::atomic<uint32_t> g_irq_pending;
void ps2_irq_poll(Ctx& c);
void ps2_irq_idle(Ctx& c);   // entrega interrupcoes quando todas as threads estao esperando
#define PS2_POLL_IRQ(c) do { if (g_irq_pending.load(std::memory_order_relaxed)) ps2_irq_poll(c); } while (0)

// ---- memoria do EE ----
// 0..1FFFFFFF e kseg0/kseg1 (80000000..BFFFFFFF) sao a RAM de 32 MB; 2xxxxxxx e 3xxxxxxx
// tambem espelham a RAM; 10000000..1FFFFFFF e hardware (tratado pelo runtime);
// 70000000 e o scratchpad de 16 KB. Qualquer outro endereco e erro.

uint64_t ps2_hw_read(Ctx& c, uint32_t phys, int size);
void ps2_hw_write(Ctx& c, uint32_t phys, uint64_t value, int size);
[[noreturn]] void ps2_bad_address(Ctx& c, uint32_t address, int write);

static inline uint32_t ee_phys(uint32_t a)
{
    switch (a >> 28)
    {
        case 0x8: case 0x9: case 0xA: case 0xB: return a & 0x1FFFFFFFu;   // kseg0 e kseg1
        case 0x2: case 0x3: return a & 0x0FFFFFFFu;
        default: return a;
    }
}

// Acesso a hardware: ate 8 bytes e uma chamada; 128 bits sao duas de 64.
// Feito com especializacao de template para compilar em C++11.
template <class T, bool Small = (sizeof(T) <= 8)> struct HwAccess;

template <class T> struct HwAccess<T, true>
{
    static T read(Ctx& c, uint32_t p)
    {
        T v;
        uint64_t x = ps2_hw_read(c, p, (int)sizeof(T));
        std::memcpy(&v, &x, sizeof(T));
        return v;
    }

    static void write(Ctx& c, uint32_t p, T v)
    {
        uint64_t x = 0;
        std::memcpy(&x, &v, sizeof(T));
        ps2_hw_write(c, p, x, (int)sizeof(T));
    }
};

template <class T> struct HwAccess<T, false>
{
    static T read(Ctx& c, uint32_t p)
    {
        T v;
        uint64_t lo = ps2_hw_read(c, p, 8);
        uint64_t hi = ps2_hw_read(c, p + 8, 8);
        std::memcpy(&v, &lo, 8);
        std::memcpy(reinterpret_cast<uint8_t*>(&v) + 8, &hi, 8);
        return v;
    }

    static void write(Ctx& c, uint32_t p, T v)
    {
        uint64_t lo, hi;
        std::memcpy(&lo, &v, 8);
        std::memcpy(&hi, reinterpret_cast<const uint8_t*>(&v) + 8, 8);
        ps2_hw_write(c, p, lo, 8);
        ps2_hw_write(c, p + 8, hi, 8);
    }
};

template <class T> static inline T mrd(Ctx& c, uint32_t addr)
{
    if ((addr >> 28) == 7)
    {
        T v;
        std::memcpy(&v, c.spr + (addr & 0x3FFFu), sizeof(T));
        return v;
    }

    uint32_t p = ee_phys(addr);

    if (p < 0x02000000u)
    {
        T v;
        std::memcpy(&v, c.mem + p, sizeof(T));
        return v;
    }

    if (p >= 0x10000000u && p < 0x20000000u)
        return HwAccess<T>::read(c, p);

    ps2_bad_address(c, addr, 0);
}

template <class T> static inline void mwr(Ctx& c, uint32_t addr, T v)
{
    if ((addr >> 28) == 7)
    {
        std::memcpy(c.spr + (addr & 0x3FFFu), &v, sizeof(T));
        return;
    }

    uint32_t p = ee_phys(addr);

    if (p < 0x02000000u)
    {
        std::memcpy(c.mem + p, &v, sizeof(T));
        return;
    }

    if (p >= 0x10000000u && p < 0x20000000u)
    {
        HwAccess<T>::write(c, p, v);
        return;
    }

    ps2_bad_address(c, addr, 1);
}

// ---- multiplicacao e divisao inteira (EE: MULT/MULTU tambem gravam rd) ----
// As variantes 1 usam o segundo par HI1/LO1 (pipeline 1).

template <uint64_t Ctx::*HI, uint64_t Ctx::*LO>
static inline void ee_mult_t(Ctx& c, int rd, int rs, int rt)
{
    int64_t p = (int64_t)(int32_t)c.r[rs].lo * (int64_t)(int32_t)c.r[rt].lo;
    c.*LO = S32((uint32_t)p);
    c.*HI = S32((uint32_t)((uint64_t)p >> 32));
    if (rd != 0) c.r[rd].lo = c.*LO;
}

template <uint64_t Ctx::*HI, uint64_t Ctx::*LO>
static inline void ee_multu_t(Ctx& c, int rd, int rs, int rt)
{
    uint64_t p = (uint64_t)(uint32_t)c.r[rs].lo * (uint64_t)(uint32_t)c.r[rt].lo;
    c.*LO = S32((uint32_t)p);
    c.*HI = S32((uint32_t)(p >> 32));
    if (rd != 0) c.r[rd].lo = c.*LO;
}

template <uint64_t Ctx::*HI, uint64_t Ctx::*LO>
static inline void ee_div_t(Ctx& c, int rs, int rt)
{
    int32_t a = (int32_t)c.r[rs].lo;
    int32_t b = (int32_t)c.r[rt].lo;
    if (b == 0)
    {
        c.*LO = S32(a >= 0 ? 0xFFFFFFFFu : 1u);
        c.*HI = S32((uint32_t)a);
    }
    else if (a == INT32_MIN && b == -1)
    {
        c.*LO = S32(0x80000000u);
        c.*HI = 0;
    }
    else
    {
        c.*LO = S32((uint32_t)(a / b));
        c.*HI = S32((uint32_t)(a % b));
    }
}

template <uint64_t Ctx::*HI, uint64_t Ctx::*LO>
static inline void ee_divu_t(Ctx& c, int rs, int rt)
{
    uint32_t a = (uint32_t)c.r[rs].lo;
    uint32_t b = (uint32_t)c.r[rt].lo;
    if (b == 0)
    {
        c.*LO = S32(0xFFFFFFFFu);
        c.*HI = S32(a);
    }
    else
    {
        c.*LO = S32(a / b);
        c.*HI = S32(a % b);
    }
}

static inline void ee_mult(Ctx& c, int rd, int rs, int rt)   { ee_mult_t<&Ctx::hi, &Ctx::lo>(c, rd, rs, rt); }
static inline void ee_multu(Ctx& c, int rd, int rs, int rt)  { ee_multu_t<&Ctx::hi, &Ctx::lo>(c, rd, rs, rt); }
static inline void ee_div(Ctx& c, int rs, int rt)            { ee_div_t<&Ctx::hi, &Ctx::lo>(c, rs, rt); }
static inline void ee_divu(Ctx& c, int rs, int rt)           { ee_divu_t<&Ctx::hi, &Ctx::lo>(c, rs, rt); }
static inline void ee_mult1(Ctx& c, int rd, int rs, int rt)  { ee_mult_t<&Ctx::hi1, &Ctx::lo1>(c, rd, rs, rt); }
static inline void ee_multu1(Ctx& c, int rd, int rs, int rt) { ee_multu_t<&Ctx::hi1, &Ctx::lo1>(c, rd, rs, rt); }
static inline void ee_div1(Ctx& c, int rs, int rt)           { ee_div_t<&Ctx::hi1, &Ctx::lo1>(c, rs, rt); }
static inline void ee_divu1(Ctx& c, int rs, int rt)          { ee_divu_t<&Ctx::hi1, &Ctx::lo1>(c, rs, rt); }

// ---- cargas e gravacoes desalinhadas (little-endian) ----

static inline void ee_lwl(Ctx& c, int rt, uint32_t addr)
{
    uint32_t w = mrd<uint32_t>(c, addr & ~3u);
    uint32_t s = addr & 3u;
    uint32_t r = ((uint32_t)c.r[rt].lo & (0x00FFFFFFu >> (8 * s))) | (w << (24 - 8 * s));
    c.r[rt].lo = S32(r);
}

static inline void ee_lwr(Ctx& c, int rt, uint32_t addr)
{
    uint32_t w = mrd<uint32_t>(c, addr & ~3u);
    uint32_t s = addr & 3u;
    uint32_t r = ((uint32_t)c.r[rt].lo & (0xFFFFFF00u << (24 - 8 * s))) | (w >> (8 * s));
    if (s == 0) c.r[rt].lo = S32(r);
    else c.r[rt].lo = (c.r[rt].lo & 0xFFFFFFFF00000000ull) | r;
}

static inline void ee_swl(Ctx& c, int rt, uint32_t addr)
{
    uint32_t a = addr & ~3u;
    uint32_t s = addr & 3u;
    uint32_t w = mrd<uint32_t>(c, a);
    w = (w & (0xFFFFFF00u << (8 * s))) | ((uint32_t)c.r[rt].lo >> (24 - 8 * s));
    mwr<uint32_t>(c, a, w);
}

static inline void ee_swr(Ctx& c, int rt, uint32_t addr)
{
    uint32_t a = addr & ~3u;
    uint32_t s = addr & 3u;
    uint32_t w = mrd<uint32_t>(c, a);
    w = (w & (0x00FFFFFFu >> (24 - 8 * s))) | ((uint32_t)c.r[rt].lo << (8 * s));
    mwr<uint32_t>(c, a, w);
}

static inline void ee_ldl(Ctx& c, int rt, uint32_t addr)
{
    uint64_t d = mrd<uint64_t>(c, addr & ~7u);
    uint32_t s = addr & 7u;
    c.r[rt].lo = (c.r[rt].lo & (0x00FFFFFFFFFFFFFFull >> (8 * s))) | (d << (56 - 8 * s));
}

static inline void ee_ldr(Ctx& c, int rt, uint32_t addr)
{
    uint64_t d = mrd<uint64_t>(c, addr & ~7u);
    uint32_t s = addr & 7u;
    c.r[rt].lo = (c.r[rt].lo & (0xFFFFFFFFFFFFFF00ull << (56 - 8 * s))) | (d >> (8 * s));
}

static inline void ee_sdl(Ctx& c, int rt, uint32_t addr)
{
    uint32_t a = addr & ~7u;
    uint32_t s = addr & 7u;
    uint64_t d = mrd<uint64_t>(c, a);
    d = (d & (0xFFFFFFFFFFFFFF00ull << (8 * s))) | (c.r[rt].lo >> (56 - 8 * s));
    mwr<uint64_t>(c, a, d);
}

static inline void ee_sdr(Ctx& c, int rt, uint32_t addr)
{
    uint32_t a = addr & ~7u;
    uint32_t s = addr & 7u;
    uint64_t d = mrd<uint64_t>(c, a);
    d = (d & (0x00FFFFFFFFFFFFFFull >> (56 - 8 * s))) | (c.r[rt].lo << (8 * s));
    mwr<uint64_t>(c, a, d);
}

// ---- FPU do EE: so precisao simples, sem NaN/Inf/denormais ----
// Entradas Inf/NaN viram +-FLT_MAX e denormais viram +-0. Resultados saturam em
// +-FLT_MAX e sao truncados em direcao a zero. Flags de excecao ainda ignoradas.

static inline float f32(uint32_t b) { float v; std::memcpy(&v, &b, 4); return v; }
static inline uint32_t u32(float v) { uint32_t b; std::memcpy(&b, &v, 4); return b; }

static inline double ee_in(uint32_t b)
{
    uint32_t e = b & 0x7F800000u;
    if (e == 0) return (b & 0x80000000u) ? -0.0 : 0.0;
    if (e == 0x7F800000u) b = (b & 0x80000000u) | 0x7F7FFFFFu;
    return (double)f32(b);
}

static inline uint32_t ee_pack(double v)
{
    const double MAXF = 3.4028234663852886e+38;
    if (v != v) v = 0.0;
    if (v > MAXF) v = MAXF;
    else if (v < -MAXF) v = -MAXF;

    float r = (float)v;
    if (std::fabs((double)r) > std::fabs(v))
        r = f32(u32(r) - 1u);   // um ulp para zero (truncamento), sem chamar a libm

    uint32_t b = u32(r);
    if ((b & 0x7F800000u) == 0) b &= 0x80000000u;
    return b;
}

static inline void fpu_set_c(Ctx& c, bool v)
{
    if (v) c.fcr31 |= 0x00800000u; else c.fcr31 &= ~0x00800000u;
}

static inline void fpu_add(Ctx& c, int fd, int fs, int ft) { c.f[fd] = ee_pack(ee_in(c.f[fs]) + ee_in(c.f[ft])); }
static inline void fpu_sub(Ctx& c, int fd, int fs, int ft) { c.f[fd] = ee_pack(ee_in(c.f[fs]) - ee_in(c.f[ft])); }
static inline void fpu_mul(Ctx& c, int fd, int fs, int ft) { c.f[fd] = ee_pack(ee_in(c.f[fs]) * ee_in(c.f[ft])); }

static inline void fpu_div(Ctx& c, int fd, int fs, int ft)
{
    double d = ee_in(c.f[ft]);
    if (d == 0.0) { c.f[fd] = ((c.f[fs] ^ c.f[ft]) & 0x80000000u) | 0x7F7FFFFFu; return; }
    c.f[fd] = ee_pack(ee_in(c.f[fs]) / d);
}

static inline void fpu_sqrt(Ctx& c, int fd, int ft)
{
    c.f[fd] = ee_pack(std::sqrt(std::fabs(ee_in(c.f[ft]))));
}

static inline void fpu_rsqrt(Ctx& c, int fd, int fs, int ft)
{
    double d = std::sqrt(std::fabs(ee_in(c.f[ft])));
    if (d == 0.0) { c.f[fd] = (c.f[fs] & 0x80000000u) | 0x7F7FFFFFu; return; }
    c.f[fd] = ee_pack(ee_in(c.f[fs]) / d);
}

static inline void fpu_max(Ctx& c, int fd, int fs, int ft) { c.f[fd] = ee_in(c.f[fs]) > ee_in(c.f[ft]) ? c.f[fs] : c.f[ft]; }
static inline void fpu_min(Ctx& c, int fd, int fs, int ft) { c.f[fd] = ee_in(c.f[fs]) < ee_in(c.f[ft]) ? c.f[fs] : c.f[ft]; }

static inline void fpu_adda(Ctx& c, int fs, int ft) { c.acc = ee_pack(ee_in(c.f[fs]) + ee_in(c.f[ft])); }
static inline void fpu_suba(Ctx& c, int fs, int ft) { c.acc = ee_pack(ee_in(c.f[fs]) - ee_in(c.f[ft])); }
static inline void fpu_mula(Ctx& c, int fs, int ft) { c.acc = ee_pack(ee_in(c.f[fs]) * ee_in(c.f[ft])); }
static inline void fpu_madd(Ctx& c, int fd, int fs, int ft) { c.f[fd] = ee_pack(ee_in(c.acc) + ee_in(c.f[fs]) * ee_in(c.f[ft])); }
static inline void fpu_msub(Ctx& c, int fd, int fs, int ft) { c.f[fd] = ee_pack(ee_in(c.acc) - ee_in(c.f[fs]) * ee_in(c.f[ft])); }
static inline void fpu_madda(Ctx& c, int fs, int ft) { c.acc = ee_pack(ee_in(c.acc) + ee_in(c.f[fs]) * ee_in(c.f[ft])); }
static inline void fpu_msuba(Ctx& c, int fs, int ft) { c.acc = ee_pack(ee_in(c.acc) - ee_in(c.f[fs]) * ee_in(c.f[ft])); }

static inline void fpu_cvt_s_w(Ctx& c, int fd, int fs) { c.f[fd] = ee_pack((double)(int32_t)c.f[fs]); }

static inline void fpu_cvt_w_s(Ctx& c, int fd, int fs)
{
    double v = ee_in(c.f[fs]);
    if (v >= 2147483648.0) c.f[fd] = 0x7FFFFFFFu;
    else if (v <= -2147483648.0) c.f[fd] = 0x80000000u;
    else c.f[fd] = (uint32_t)(int32_t)v;
}

static inline void fpu_c_eq(Ctx& c, int fs, int ft) { fpu_set_c(c, ee_in(c.f[fs]) == ee_in(c.f[ft])); }
static inline void fpu_c_lt(Ctx& c, int fs, int ft) { fpu_set_c(c, ee_in(c.f[fs]) < ee_in(c.f[ft])); }
static inline void fpu_c_le(Ctx& c, int fs, int ft) { fpu_set_c(c, ee_in(c.f[fs]) <= ee_in(c.f[ft])); }

// ---- VU0 em modo macro (COP2) ----
// Chame vu0_init(c) uma vez no inicio: VF0 e sempre (0, 0, 0, 1).
// Flags MAC e status ainda nao sao calculadas; o clip (VCLIPw) e exato.
// As variaveis Q, I e P ficam em vi[22], vi[21] e vi[23].

enum { VU_ADD, VU_SUB, VU_MUL, VU_MADD, VU_MSUB, VU_MAX, VU_MIN };
enum { VU_SRC_VEC, VU_SRC_X, VU_SRC_Y, VU_SRC_Z, VU_SRC_W, VU_SRC_Q, VU_SRC_I };

static inline void vu0_init(Ctx& c)
{
    c.vf[0][0] = 0; c.vf[0][1] = 0; c.vf[0][2] = 0; c.vf[0][3] = 0x3F800000u;
}

static inline Reg128 vu_qmfc2(Ctx& c, int vf)
{
    Reg128 r;
    std::memcpy(&r, c.vf[vf], 16);
    return r;
}

static inline void vu_qmtc2(Ctx& c, int vf, Reg128 r)
{
    if (vf != 0) std::memcpy(c.vf[vf], &r, 16);
}

extern const bool g_rotation_caller_trace;
void rotation_caller_record(const Ctx& c, uint32_t axis);

extern const bool g_move_callback_trace;
void ps2_battle_effect_clock(Ctx& c);
extern const bool g_field_clock_trace;
void field_clock_record(uint32_t delta_bits);

struct MoveCallbackTrace;
void move_callback_begin(const Ctx& c, MoveCallbackTrace& trace);
void move_callback_end(const Ctx& c, const MoveCallbackTrace& trace);

struct MoveCallbackTrace
{
    const Ctx& ctx;
    uint32_t vector, object, caller;
    unsigned attempts;
    double input_length, position_x, position_z;
    bool input_valid, position_valid;

    explicit MoveCallbackTrace(const Ctx& c) : ctx(c)
    {
        if (g_move_callback_trace)
        {
            attempts = 0;
            move_callback_begin(ctx, *this);
        }
    }

    // Executa apos o delay slot de retorno, com o v0 definitivo.
    ~MoveCallbackTrace()
    {
        if (g_move_callback_trace)
            move_callback_end(ctx, *this);
    }

    void attempt()
    {
        if (g_move_callback_trace)
            ++attempts;
    }
};

enum class Vu0StatusEvent : unsigned
{
    Div, Sqrt, Rsqrt, WaitQ, ReadStatus, WriteStatus, MicroDiv, MicroSqrt,
    MicroRsqrt, MicroReadStatus, Count
};
extern const bool g_vu0_status_trace;
void vu0_status_record(Vu0StatusEvent event, uint32_t pc, uint32_t value);

static inline uint32_t vu_cfc2(Ctx& c, int r)
{
    if (r == 0) return 0;
    if (r < 16) return c.vi[r] & 0xFFFFu;
    return c.vi[r & 31];
}

static inline void vu_ctc2(Ctx& c, int r, uint32_t v)
{
    if (r == 0) return;
    if (r < 16) c.vi[r] = v & 0xFFFFu;
    else if (r == 18) c.vi[18] = v & 0xFFFFFFu;
    else c.vi[r & 31] = v;
}

// Aritmetica por faixa: fd = fs OP fonte. Com to_acc, grava no acumulador.
// MADD/MSUB usam o acumulador como parcela. So as faixas de dest (x=8 .. w=1) mudam.
static inline void vu_arith(Ctx& c, int op, int src, int dest, int fd, int fs, int ft, int to_acc)
{
    uint32_t res[4];
    for (int l = 0; l < 4; l++)
    {
        uint32_t a = c.vf[fs][l];
        uint32_t b;
        switch (src)
        {
            case VU_SRC_VEC: b = c.vf[ft][l]; break;
            case VU_SRC_X:   b = c.vf[ft][0]; break;
            case VU_SRC_Y:   b = c.vf[ft][1]; break;
            case VU_SRC_Z:   b = c.vf[ft][2]; break;
            case VU_SRC_W:   b = c.vf[ft][3]; break;
            case VU_SRC_Q:   b = c.vi[22]; break;
            default:         b = c.vi[21]; break;
        }
        switch (op)
        {
            case VU_ADD:  res[l] = ee_pack(ee_in(a) + ee_in(b)); break;
            case VU_SUB:  res[l] = ee_pack(ee_in(a) - ee_in(b)); break;
            case VU_MUL:  res[l] = ee_pack(ee_in(a) * ee_in(b)); break;
            case VU_MADD: res[l] = ee_pack(ee_in(c.vacc[l]) + ee_in(a) * ee_in(b)); break;
            case VU_MSUB: res[l] = ee_pack(ee_in(c.vacc[l]) - ee_in(a) * ee_in(b)); break;
            case VU_MAX:  res[l] = ee_in(a) > ee_in(b) ? a : b; break;
            default:      res[l] = ee_in(a) < ee_in(b) ? a : b; break;
        }
    }
    for (int l = 0; l < 4; l++)
    {
        if (!(dest & (8 >> l))) continue;
        if (to_acc) c.vacc[l] = res[l];
        else if (fd != 0) c.vf[fd][l] = res[l];
    }
}

// Produto vetorial: OPMULA grava no acumulador, OPMSUB subtrai dele (so xyz).
static inline void vu_opmula(Ctx& c, int fs, int ft)
{
    uint32_t x = ee_pack(ee_in(c.vf[fs][1]) * ee_in(c.vf[ft][2]));
    uint32_t y = ee_pack(ee_in(c.vf[fs][2]) * ee_in(c.vf[ft][0]));
    uint32_t z = ee_pack(ee_in(c.vf[fs][0]) * ee_in(c.vf[ft][1]));
    c.vacc[0] = x; c.vacc[1] = y; c.vacc[2] = z;
}

static inline void vu_opmsub(Ctx& c, int fd, int fs, int ft)
{
    uint32_t x = ee_pack(ee_in(c.vacc[0]) - ee_in(c.vf[fs][1]) * ee_in(c.vf[ft][2]));
    uint32_t y = ee_pack(ee_in(c.vacc[1]) - ee_in(c.vf[fs][2]) * ee_in(c.vf[ft][0]));
    uint32_t z = ee_pack(ee_in(c.vacc[2]) - ee_in(c.vf[fs][0]) * ee_in(c.vf[ft][1]));
    if (fd != 0) { c.vf[fd][0] = x; c.vf[fd][1] = y; c.vf[fd][2] = z; }
}

static inline void vu_move(Ctx& c, int dest, int ft, int fs)
{
    uint32_t t[4] = { c.vf[fs][0], c.vf[fs][1], c.vf[fs][2], c.vf[fs][3] };
    for (int l = 0; l < 4; l++) if (ft != 0 && (dest & (8 >> l))) c.vf[ft][l] = t[l];
}

static inline void vu_mr32(Ctx& c, int dest, int ft, int fs)
{
    uint32_t t[4] = { c.vf[fs][1], c.vf[fs][2], c.vf[fs][3], c.vf[fs][0] };
    for (int l = 0; l < 4; l++) if (ft != 0 && (dest & (8 >> l))) c.vf[ft][l] = t[l];
}

static inline void vu_abs(Ctx& c, int dest, int ft, int fs)
{
    uint32_t t[4] = { c.vf[fs][0], c.vf[fs][1], c.vf[fs][2], c.vf[fs][3] };
    for (int l = 0; l < 4; l++) if (ft != 0 && (dest & (8 >> l))) c.vf[ft][l] = t[l] & 0x7FFFFFFFu;
}

// Inteiro para float com n bits fracionarios (n = 0, 4, 12, 15), e o inverso (trunca e satura).
static inline void vu_itof(Ctx& c, int dest, int ft, int fs, int n)
{
    uint32_t t[4];
    for (int l = 0; l < 4; l++) t[l] = ee_pack((double)(int32_t)c.vf[fs][l] / (double)(1 << n));
    for (int l = 0; l < 4; l++) if (ft != 0 && (dest & (8 >> l))) c.vf[ft][l] = t[l];
}

static inline void vu_ftoi(Ctx& c, int dest, int ft, int fs, int n)
{
    uint32_t t[4];
    for (int l = 0; l < 4; l++)
    {
        double v = ee_in(c.vf[fs][l]) * (double)(1 << n);
        if (v >= 2147483648.0) t[l] = 0x7FFFFFFFu;
        else if (v <= -2147483648.0) t[l] = 0x80000000u;
        else t[l] = (uint32_t)(int32_t)v;
    }
    for (int l = 0; l < 4; l++) if (ft != 0 && (dest & (8 >> l))) c.vf[ft][l] = t[l];
}

// VCLIPw: compara fs.xyz com |ft.w| e empilha 6 bits no historico de clip (24 bits).
static inline void vu_clipw(Ctx& c, int fs, int ft)
{
    double w = std::fabs(ee_in(c.vf[ft][3]));
    double x = ee_in(c.vf[fs][0]);
    double y = ee_in(c.vf[fs][1]);
    double z = ee_in(c.vf[fs][2]);
    uint32_t clip = (c.vi[18] << 6) & 0xFFFFFFu;
    if (x > w) clip |= 0x01u;
    if (x < -w) clip |= 0x02u;
    if (y > w) clip |= 0x04u;
    if (y < -w) clip |= 0x08u;
    if (z > w) clip |= 0x10u;
    if (z < -w) clip |= 0x20u;
    c.vi[18] = clip;
}

// Q = fs.fsf / ft.ftf. Divisor zero satura em +-FLT_MAX com o sinal do xor.
static inline void vu_div(Ctx& c, int fs, int fsf, int ft, int ftf)
{
    uint32_t a = c.vf[fs][fsf];
    uint32_t b = c.vf[ft][ftf];
    double d = ee_in(b);
    if (d == 0.0) c.vi[22] = ((a ^ b) & 0x80000000u) | 0x7F7FFFFFu;
    else c.vi[22] = ee_pack(ee_in(a) / d);
}

static inline void vu_sqrt(Ctx& c, int ft, int ftf)
{
    c.vi[22] = ee_pack(std::sqrt(std::fabs(ee_in(c.vf[ft][ftf]))));
}

static inline void vu_rsqrt(Ctx& c, int fs, int fsf, int ft, int ftf)
{
    uint32_t a = c.vf[fs][fsf];
    uint32_t b = c.vf[ft][ftf];
    double d = std::sqrt(std::fabs(ee_in(b)));
    if (d == 0.0) c.vi[22] = ((a ^ b) & 0x80000000u) | 0x7F7FFFFFu;
    else c.vi[22] = ee_pack(ee_in(a) / d);
}

// Inteiros do VU0 (16 bits; VI0 e sempre 0).
static inline void vu_vi_set(Ctx& c, int r, uint32_t v) { if (r != 0) c.vi[r] = v & 0xFFFFu; }
static inline void vu_iadd(Ctx& c, int id, int is, int it) { vu_vi_set(c, id, c.vi[is] + c.vi[it]); }
static inline void vu_isub(Ctx& c, int id, int is, int it) { vu_vi_set(c, id, c.vi[is] - c.vi[it]); }
static inline void vu_iand(Ctx& c, int id, int is, int it) { vu_vi_set(c, id, c.vi[is] & c.vi[it]); }
static inline void vu_ior(Ctx& c, int id, int is, int it)  { vu_vi_set(c, id, c.vi[is] | c.vi[it]); }
static inline void vu_iaddi(Ctx& c, int it, int is, int imm5) { vu_vi_set(c, it, c.vi[is] + (uint32_t)imm5); }
static inline void vu_mtir(Ctx& c, int it, int fs, int fsf) { vu_vi_set(c, it, c.vf[fs][fsf]); }

static inline void vu_mfir(Ctx& c, int dest, int ft, int is)
{
    uint32_t v = (uint32_t)(int32_t)(int16_t)c.vi[is];
    for (int l = 0; l < 4; l++) if (ft != 0 && (dest & (8 >> l))) c.vf[ft][l] = v;
}

// ---- MMI: operacoes paralelas sobre os 128 bits ----

union V128 { Reg128 r; uint8_t b[16]; uint16_t h[8]; uint32_t w[4]; uint64_t d[2]; };

static inline V128 V(const Reg128& r) { V128 v; v.r = r; return v; }

static inline void mmi_pand(Ctx& c, int rd, int rs, int rt) { Reg128 o = { c.r[rs].lo & c.r[rt].lo, c.r[rs].hi & c.r[rt].hi }; c.r[rd] = o; }
static inline void mmi_por(Ctx& c, int rd, int rs, int rt)  { Reg128 o = { c.r[rs].lo | c.r[rt].lo, c.r[rs].hi | c.r[rt].hi }; c.r[rd] = o; }
static inline void mmi_pxor(Ctx& c, int rd, int rs, int rt) { Reg128 o = { c.r[rs].lo ^ c.r[rt].lo, c.r[rs].hi ^ c.r[rt].hi }; c.r[rd] = o; }
static inline void mmi_pnor(Ctx& c, int rd, int rs, int rt) { Reg128 o = { ~(c.r[rs].lo | c.r[rt].lo), ~(c.r[rs].hi | c.r[rt].hi) }; c.r[rd] = o; }

static inline void mmi_pcpyld(Ctx& c, int rd, int rs, int rt) { Reg128 o = { c.r[rt].lo, c.r[rs].lo }; c.r[rd] = o; }
static inline void mmi_pcpyud(Ctx& c, int rd, int rs, int rt) { Reg128 o = { c.r[rs].hi, c.r[rt].hi }; c.r[rd] = o; }

static inline void mmi_paddw(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 4; i++) o.w[i] = a.w[i] + b.w[i]; c.r[rd] = o.r; }
static inline void mmi_psubw(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 4; i++) o.w[i] = a.w[i] - b.w[i]; c.r[rd] = o.r; }
static inline void mmi_paddh(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (uint16_t)(a.h[i] + b.h[i]); c.r[rd] = o.r; }
static inline void mmi_psubh(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (uint16_t)(a.h[i] - b.h[i]); c.r[rd] = o.r; }
static inline void mmi_paddb(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 16; i++) o.b[i] = (uint8_t)(a.b[i] + b.b[i]); c.r[rd] = o.r; }
static inline void mmi_psubb(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 16; i++) o.b[i] = (uint8_t)(a.b[i] - b.b[i]); c.r[rd] = o.r; }

// PADDUB: soma de bytes sem sinal com saturacao em 255 (com rt = $zero e uma copia de 128 bits).
static inline void mmi_paddub(Ctx& c, int rd, int rs, int rt)
{
    V128 a = V(c.r[rs]), b = V(c.r[rt]), o;
    for (int i = 0; i < 16; i++) { unsigned v = (unsigned)a.b[i] + b.b[i]; o.b[i] = (uint8_t)(v > 255u ? 255u : v); }
    c.r[rd] = o.r;
}

// Deslocamentos por constante (rd, rt, sa).
static inline void mmi_psllh(Ctx& c, int rd, int rt, int sa) { V128 a = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (uint16_t)(a.h[i] << (sa & 15)); c.r[rd] = o.r; }
static inline void mmi_psrlh(Ctx& c, int rd, int rt, int sa) { V128 a = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (uint16_t)(a.h[i] >> (sa & 15)); c.r[rd] = o.r; }
static inline void mmi_psrah(Ctx& c, int rd, int rt, int sa) { V128 a = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (uint16_t)((int16_t)a.h[i] >> (sa & 15)); c.r[rd] = o.r; }
static inline void mmi_psllw(Ctx& c, int rd, int rt, int sa) { V128 a = V(c.r[rt]), o; for (int i = 0; i < 4; i++) o.w[i] = a.w[i] << (sa & 31); c.r[rd] = o.r; }
static inline void mmi_psrlw(Ctx& c, int rd, int rt, int sa) { V128 a = V(c.r[rt]), o; for (int i = 0; i < 4; i++) o.w[i] = a.w[i] >> (sa & 31); c.r[rd] = o.r; }
static inline void mmi_psraw(Ctx& c, int rd, int rt, int sa) { V128 a = V(c.r[rt]), o; for (int i = 0; i < 4; i++) o.w[i] = (uint32_t)((int32_t)a.w[i] >> (sa & 31)); c.r[rd] = o.r; }

// Intercalacao: rt fica nas posicoes pares e rs nas impares.
static inline void mmi_pextlb(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) { o.b[2 * i] = b.b[i];     o.b[2 * i + 1] = a.b[i]; }     c.r[rd] = o.r; }
static inline void mmi_pextub(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) { o.b[2 * i] = b.b[8 + i]; o.b[2 * i + 1] = a.b[8 + i]; } c.r[rd] = o.r; }
static inline void mmi_pextlh(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 4; i++) { o.h[2 * i] = b.h[i];     o.h[2 * i + 1] = a.h[i]; }     c.r[rd] = o.r; }
static inline void mmi_pextuh(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 4; i++) { o.h[2 * i] = b.h[4 + i]; o.h[2 * i + 1] = a.h[4 + i]; } c.r[rd] = o.r; }
static inline void mmi_pextlw(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 2; i++) { o.w[2 * i] = b.w[i];     o.w[2 * i + 1] = a.w[i]; }     c.r[rd] = o.r; }
static inline void mmi_pextuw(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 2; i++) { o.w[2 * i] = b.w[2 + i]; o.w[2 * i + 1] = a.w[2 + i]; } c.r[rd] = o.r; }

// QFSRV: ({rs, rt} >> SA)[127:0]. SA vem de MTSAB/MTSAH.
static inline Reg128 shr128(Reg128 v, unsigned n)
{
    if (n == 0) return v;
    Reg128 o;
    if (n < 64) { o.lo = (v.lo >> n) | (v.hi << (64 - n)); o.hi = v.hi >> n; }
    else        { o.lo = v.hi >> (n - 64); o.hi = 0; }
    return o;
}

static inline Reg128 shl128(Reg128 v, unsigned n)
{
    if (n == 0) return v;
    Reg128 o;
    if (n < 64) { o.hi = (v.hi << n) | (v.lo >> (64 - n)); o.lo = v.lo << n; }
    else        { o.hi = v.lo << (n - 64); o.lo = 0; }
    return o;
}

static inline void mmi_qfsrv(Ctx& c, int rd, int rs, int rt)
{
    unsigned sa = c.sa & 127u;
    Reg128 lo = shr128(c.r[rt], sa);
    Reg128 hi = sa == 0 ? Reg128{0, 0} : shl128(c.r[rs], 128 - sa);
    Reg128 o = { lo.lo | hi.lo, lo.hi | hi.hi };
    c.r[rd] = o;
}

static inline void ee_mtsab(Ctx& c, int rs, uint32_t imm) { c.sa = (((uint32_t)c.r[rs].lo & 15u) ^ (imm & 15u)) * 8u; }
static inline void ee_mtsah(Ctx& c, int rs, uint32_t imm) { c.sa = (((uint32_t)c.r[rs].lo & 7u) ^ (imm & 7u)) * 16u; }

// ---- MMI: comparacoes, min/max, pack e trocas ----

static inline void mmi_pcgth(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (int16_t)a.h[i] > (int16_t)b.h[i] ? 0xFFFFu : 0u; c.r[rd] = o.r; }
static inline void mmi_pminh(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (int16_t)a.h[i] < (int16_t)b.h[i] ? a.h[i] : b.h[i]; c.r[rd] = o.r; }
static inline void mmi_pmaxh(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) o.h[i] = (int16_t)a.h[i] > (int16_t)b.h[i] ? a.h[i] : b.h[i]; c.r[rd] = o.r; }
static inline void mmi_pminw(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 4; i++) o.w[i] = (int32_t)a.w[i] < (int32_t)b.w[i] ? a.w[i] : b.w[i]; c.r[rd] = o.r; }

// Saturacao sem sinal: PSUBUB fica em 0, PADDUH fica em 0xFFFF.
static inline void mmi_psubub(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 16; i++) o.b[i] = (uint8_t)(a.b[i] > b.b[i] ? a.b[i] - b.b[i] : 0); c.r[rd] = o.r; }
static inline void mmi_padduh(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) { unsigned v = (unsigned)a.h[i] + b.h[i]; o.h[i] = (uint16_t)(v > 0xFFFFu ? 0xFFFFu : v); } c.r[rd] = o.r; }

// PPACB/PPACH: pega os elementos pares; rt vai para a metade baixa e rs para a alta.
static inline void mmi_ppacb(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 8; i++) { o.b[i] = b.b[2 * i]; o.b[8 + i] = a.b[2 * i]; } c.r[rd] = o.r; }
static inline void mmi_ppach(Ctx& c, int rd, int rs, int rt) { V128 a = V(c.r[rs]), b = V(c.r[rt]), o; for (int i = 0; i < 4; i++) { o.h[i] = b.h[2 * i]; o.h[4 + i] = a.h[2 * i]; } c.r[rd] = o.r; }

// PCPYH: o halfword 0 de cada doubleword se repete; PEXCW/PEXEW trocam palavras.
static inline void mmi_pcpyh(Ctx& c, int rd, int rt) { V128 b = V(c.r[rt]), o; for (int i = 0; i < 4; i++) { o.h[i] = b.h[0]; o.h[4 + i] = b.h[4]; } c.r[rd] = o.r; }
static inline void mmi_pexcw(Ctx& c, int rd, int rt) { V128 b = V(c.r[rt]), o; o.w[0] = b.w[0]; o.w[1] = b.w[2]; o.w[2] = b.w[1]; o.w[3] = b.w[3]; c.r[rd] = o.r; }
static inline void mmi_pexew(Ctx& c, int rd, int rt) { V128 b = V(c.r[rt]), o; o.w[0] = b.w[2]; o.w[1] = b.w[1]; o.w[2] = b.w[0]; o.w[3] = b.w[3]; c.r[rd] = o.r; }

// PMFHI/PMFLO juntam HI/HI1 e LO/LO1 em um registrador de 128 bits.
static inline void mmi_pmfhi(Ctx& c, int rd) { Reg128 o = { c.hi, c.hi1 }; c.r[rd] = o; }
static inline void mmi_pmflo(Ctx& c, int rd) { Reg128 o = { c.lo, c.lo1 }; c.r[rd] = o; }

// PMULTH / PMADDH / PMSUBH: 8 produtos de halfword com sinal em 8 palavras de 32 bits:
// LO = {p0, p1, p4, p5}, HI = {p2, p3, p6, p7} (LO/HI em pipeline 0 = palavras 0-1, HI1/LO1 = palavras 2-3).
// rd (se nao for $zero) recebe {LO0, HI0, LO2, HI2} = {p0, p2, p4, p6}. mode: 0 = grava, 1 = soma, 2 = subtrai.
static inline void mmi_mulh_t(Ctx& c, int rd, int rs, int rt, int mode)
{
    V128 a = V(c.r[rs]), b = V(c.r[rt]);
    uint32_t lo[4] = { (uint32_t)c.lo, (uint32_t)(c.lo >> 32), (uint32_t)c.lo1, (uint32_t)(c.lo1 >> 32) };
    uint32_t hi[4] = { (uint32_t)c.hi, (uint32_t)(c.hi >> 32), (uint32_t)c.hi1, (uint32_t)(c.hi1 >> 32) };
    uint32_t p[8];

    for (int i = 0; i < 8; i++)
        p[i] = (uint32_t)((int32_t)(int16_t)a.h[i] * (int32_t)(int16_t)b.h[i]);

    uint32_t* dst[8] = { &lo[0], &lo[1], &hi[0], &hi[1], &lo[2], &lo[3], &hi[2], &hi[3] };

    for (int i = 0; i < 8; i++)
    {
        if (mode == 0) *dst[i] = p[i];
        else if (mode == 1) *dst[i] += p[i];
        else *dst[i] -= p[i];
    }

    c.lo = (uint64_t)lo[0] | ((uint64_t)lo[1] << 32);
    c.lo1 = (uint64_t)lo[2] | ((uint64_t)lo[3] << 32);
    c.hi = (uint64_t)hi[0] | ((uint64_t)hi[1] << 32);
    c.hi1 = (uint64_t)hi[2] | ((uint64_t)hi[3] << 32);

    if (rd != 0)
    {
        Reg128 o = { (uint64_t)lo[0] | ((uint64_t)hi[0] << 32), (uint64_t)lo[2] | ((uint64_t)hi[2] << 32) };
        c.r[rd] = o;
    }
}
static inline void mmi_pmulth(Ctx& c, int rd, int rs, int rt) { mmi_mulh_t(c, rd, rs, rt, 0); }
static inline void mmi_pmaddh(Ctx& c, int rd, int rs, int rt) { mmi_mulh_t(c, rd, rs, rt, 1); }
static inline void mmi_pmsubh(Ctx& c, int rd, int rs, int rt) { mmi_mulh_t(c, rd, rs, rt, 2); }

// PMULTW / PMADDW / PMSUBW: dois produtos 32x32 com sinal (palavras 0 e 2), cada um em 64 bits:
// pipeline 0 -> (HI, LO), pipeline 1 -> (HI1, LO1); rd.lo / rd.hi recebem os resultados de 64 bits.
static inline void mmi_mulw_t(Ctx& c, int rd, int rs, int rt, int mode)
{
    uint64_t* H[2] = { &c.hi, &c.hi1 };
    uint64_t* L[2] = { &c.lo, &c.lo1 };
    uint64_t res[2];

    for (int k = 0; k < 2; k++)
    {
        uint64_t ra = k == 0 ? c.r[rs].lo : c.r[rs].hi, rb = k == 0 ? c.r[rt].lo : c.r[rt].hi;
        int64_t prod = (int64_t)(int32_t)(uint32_t)ra * (int64_t)(int32_t)(uint32_t)rb;
        int64_t acc = (int64_t)(((uint64_t)(uint32_t)*H[k] << 32) | (uint32_t)*L[k]);
        int64_t t = mode == 0 ? prod : (mode == 1 ? acc + prod : acc - prod);
        *L[k] = S32((uint32_t)t);
        *H[k] = S32((uint32_t)((uint64_t)t >> 32));
        res[k] = (uint64_t)t;
    }

    if (rd != 0) { Reg128 o = { res[0], res[1] }; c.r[rd] = o; }
}
static inline void mmi_pmultw(Ctx& c, int rd, int rs, int rt) { mmi_mulw_t(c, rd, rs, rt, 0); }
static inline void mmi_pmaddw(Ctx& c, int rd, int rs, int rt) { mmi_mulw_t(c, rd, rs, rt, 1); }
static inline void mmi_pmsubw(Ctx& c, int rd, int rs, int rt) { mmi_mulw_t(c, rd, rs, rt, 2); }

// MADD: (HI, LO) += rs * rt, com sinal; o EE tambem grava LO em rd.
static inline void ee_madd(Ctx& c, int rd, int rs, int rt)
{
    int64_t acc = (int64_t)(((uint64_t)(uint32_t)c.hi << 32) | (uint32_t)c.lo);
    acc += (int64_t)(int32_t)c.r[rs].lo * (int64_t)(int32_t)c.r[rt].lo;
    c.lo = S32((uint32_t)acc);
    c.hi = S32((uint32_t)((uint64_t)acc >> 32));
    if (rd != 0) c.r[rd].lo = c.lo;
}

// Pontos de entrada do runtime (a implementar).
void ps2_call(Ctx& c, uint32_t target);
void ps2_tailcall(Ctx& c, uint32_t target);
void ps2_syscall(Ctx& c);
void ps2_break(Ctx& c, uint32_t address);

// Auxiliares das funcoes substituidas por codigo nativo (HLE)
const char* ps2_cstr(Ctx& c, uint32_t address, char* buffer, size_t capacity);
// Agenda fn(arg) para rodar daqui a `frames` vsyncs, em contexto de interrupcao (como um callback
// assincrono do IOP/DMA). Usado pelos HLEs que precisam avisar o fim depois de a funcao voltar.
void ps2_defer_call(uint32_t frames, uint32_t fn, uint32_t arg);
bool ps2_fmv_play(const char* path, uint32_t owner, uint32_t end_callback);
void ps2_fmv_cancel(uint32_t owner);
uint32_t ps2_alloc_id();
[[noreturn]] void ps2_hle_unimplemented(Ctx& c, const char* name);

// Disco: a ISO do jogo e a API do libcdvd (sceCd*) feitas nativamente
bool ps2_iso_open(const char* path);
void ps2_iso_open_default(int argc, char** argv);

enum Ps2CdFn
{
    PS2_CD_INIT = 1, PS2_CD_MMODE, PS2_CD_DISK_READY, PS2_CD_GET_DISK_TYPE, PS2_CD_READ,
    PS2_CD_SYNC, PS2_CD_SEARCH_FILE, PS2_CD_STATUS, PS2_CD_GET_ERROR, PS2_CD_BREAK,
    PS2_CD_GET_READ_POS, PS2_CD_ST_INIT, PS2_CD_ST_START, PS2_CD_ST_READ, PS2_CD_ST_STOP
};

void ps2_cd(Ctx& c, int function);

// RPC do SIF: os servicos do IOP (arquivos do jogo, som) feitos nativamente
enum Ps2RpcFn { PS2_RPC_BIND = 1, PS2_RPC_CALL, PS2_RPC_CHECK, PS2_RPC_REGISTER, PS2_RPC_NEXT_REQUEST, PS2_RPC_EXEC_REQUEST };

void ps2_rpc(Ctx& c, int function);
void ps2_rpc_loop(Ctx& c);   // sceSifRpcLoop: atende os pedidos do IOP para servidores do EE

// Controle (libpad): a API scePad* e feita nativamente
enum Ps2PadFn
{
    PS2_PAD_INIT = 1, PS2_PAD_PORT_OPEN, PS2_PAD_PORT_CLOSE, PS2_PAD_END, PS2_PAD_READ,
    PS2_PAD_GET_STATE, PS2_PAD_INFO_MODE, PS2_PAD_INFO_ACT, PS2_PAD_SET_MAIN_MODE,
    PS2_PAD_SET_ACT_ALIGN, PS2_PAD_SET_ACT_DIRECT
};

void ps2_pad(Ctx& c, int function);

// Entrada do jogador (a camada de entrada do PC chama isto). Botoes como no PS2: bit 0 = apertado.
void ps2_pad_set_state(int port, uint16_t buttons_active_low, uint8_t right_x, uint8_t right_y, uint8_t left_x, uint8_t left_y);
void ps2_ei(Ctx& c);
void ps2_di(Ctx& c);
uint32_t ps2_mfc0(Ctx& c, int reg);
void ps2_mtc0(Ctx& c, int reg, uint32_t value);
void ps2_unsupported(Ctx& c, uint32_t address, const char* mnemonic);
extern const bool g_rssd_audio_trace;
void rssd_trace_bgm(const Ctx& c, uint32_t function);

// VCALLMS/VCALLMSR: inicia um microprograma do VU0 no endereco indicado (bytes da micro memoria).
void vu0_callms(Ctx& c, uint32_t address, uint32_t raw);
";

    // Runtime minimo (ps2_runtime.cpp): despacho por endereco usando a tabela
    // gerada e paradas claras para o que ainda nao existe.
    public static string RuntimeSource { get; } = CreateRuntimeSource();

    private static string CreateRuntimeSource()
    {
        // As rotinas acompanham SH.ELF.dll e sao inseridas no proprio C++.
        // Nao dependem de includes opcionais na pasta de compilacao.
        StringBuilder programs = new();
        foreach (string id in new[] { "0350", "0078", "0658" })
        {
            using Stream resource = typeof(MipsCppEmitter).Assembly
                .GetManifestResourceStream($"SH.ELF.Vu1.{id}")
                ?? throw new InvalidOperationException($"Rotina VU1 integrada ausente: {id}");
            using StreamReader reader = new(resource, Encoding.UTF8);
            programs.AppendLine(reader.ReadToEnd());
        }
        string ReadVulkan(string name)
        {
            using Stream resource = typeof(MipsCppEmitter).Assembly.GetManifestResourceStream($"SH.ELF.Vulkan.{name}")
                ?? throw new InvalidOperationException($"Recurso Vulkan ausente: {name}");
            using StreamReader reader = new(resource, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        using Stream shader = typeof(MipsCppEmitter).Assembly.GetManifestResourceStream("SH.ELF.Vulkan.shader")
            ?? throw new InvalidOperationException("Shader Vulkan integrado ausente.");
        using BinaryReader shaderReader = new(shader);
        StringBuilder device = new("static const uint32_t roj_vk_shader[] = {\n");
        for (long i = 0; i < shader.Length; i += 4)
            device.Append("0x").Append(shaderReader.ReadUInt32().ToString("X8")).Append("u,");
        device.AppendLine("\n};").AppendLine(ReadVulkan("device"));
        return RuntimeSourceTemplate.Replace("/* SH_EMBEDDED_VU1_PROGRAMS */", programs.ToString())
            .Replace("/* SH_EMBEDDED_VULKAN_DEVICE */", device.ToString())
            .Replace("/* SH_EMBEDDED_VULKAN_BATCH */", ReadVulkan("batch"))
            .Replace("/* SH_EMBEDDED_EXTERNAL_MUSIC */", ReadVulkan("music"))
            .Replace("/* SH_EMBEDDED_EXTERNAL_EFFECTS */", ReadVulkan("effects"));
    }

    private const string RuntimeSourceTemplate = """
#include "ps2_functions.h"
#include "roj_settings.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <csignal>
#include <cmath>
#include <condition_variable>
#include <filesystem>
#include <fstream>
#include <ctime>
#include <string>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <thread>
#include <vector>
#include <utility>
const roj::Settings& ps2_settings();
/* SH_EMBEDDED_EXTERNAL_EFFECTS */
/* SH_EMBEDDED_VULKAN_DEVICE */
#if defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

#ifdef _WIN32
// Reprodutor da sequencia RSSD de batalha. Cada chamada gera apenas o
// bloco PCM pedido pelo dispositivo, a partir dos eventos e das vozes ativas.
namespace
{
    constexpr unsigned LiveRate = 22050;
    constexpr unsigned SpuRate = 48000;
    using LiveBytes = std::vector<uint8_t>;

    uint32_t live_word(const uint8_t* p, unsigned bytes)
    {
        uint32_t value = 0;
        for (unsigned i = 0; i < bytes; ++i)
            value |= uint32_t(p[i]) << (8 * i);
        return value;
    }

    int live_signed(unsigned value, unsigned bits)
    {
        return int(value) - ((value & (1u << (bits - 1))) ? int(1u << bits) : 0);
    }

    struct LiveEnvelope
    {
        struct Phase { int step, increment, shift; bool exp, down; };
        Phase phases[4];
        int target, stage = 0, level = 0, counter = 0;
        unsigned clock = 0;

        static Phase make(unsigned rate, bool down, bool exp, bool halt)
        {
            int shift = int(rate >> 2);
            int step = (down ? -8 + int(rate & 3) : 7 - int(rate & 3));
            return {step * (1 << std::max(0, 11 - shift)),
                    halt ? 0 : 0x8000 >> std::max(0, shift - 11), shift, exp, down};
        }

        LiveEnvelope(unsigned a, unsigned b)
            : phases{make((a >> 8) & 127, false, a & 0x8000, ((a >> 8) & 127) == 127),
                     make(((a >> 4) & 15) * 4, true, true, false),
                     make((b >> 6) & 127, b & 0x4000, b & 0x8000, ((b >> 6) & 127) == 127),
                     make((b & 31) * 4, true, b & 32, (b & 31) == 31)},
              target(int((a & 15) + 1) * 2048) {}

        void release() { stage = 3; counter = 0; }

        void advance()
        {
            clock += SpuRate;
            while (clock >= LiveRate)
            {
                clock -= LiveRate;
                auto p = phases[stage];
                if (!p.increment)
                    break;
                if (p.exp && p.down)
                    p.step = (p.step * level) >> 15;
                else if (p.exp && level > 0x6000)
                {
                    if (p.shift <= 10) p.step >>= p.shift == 10 ? 1 : 2;
                    if (p.shift >= 10) p.increment >>= p.shift == 10 ? 1 : 2;
                }
                counter += std::max(1, p.increment);
                if (counter < 0x8000)
                    continue;
                counter = 0;
                level = std::clamp(level + p.step, 0, 0x7fff);
                if ((stage == 0 && level == 0x7fff) || (stage == 1 && level <= target))
                    ++stage;
            }
        }
    };

    struct LiveSample
    {
        std::vector<int16_t> pcm;
        unsigned loop = 0;
        bool repeat = false;
        int tuning = 0;

        bool decode(const uint8_t* data, unsigned size)
        {
            constexpr int coefficients[][2] = {{0,0}, {60,0}, {115,-52}, {98,-55}, {122,-60}};
            int previous = 0, older = 0;
            bool mark = false;
            for (unsigned pos = 0; pos + 16 <= size; pos += 16)
            {
                const unsigned predictor = data[pos] >> 4, shift = data[pos] & 15, flags = data[pos+1];
                if (predictor >= 5 || shift > 12)
                    return false;
                if (flags & 4) { mark = true; loop = unsigned(pcm.size()); }
                for (unsigned i = 2; i < 16; ++i)
                {
                    for (unsigned bit : {0u, 4u})
                    {
                        const int raw = live_signed((data[pos+i] >> bit) & 15, 4);
                        const auto* coef = coefficients[predictor];
                        const int value = (raw * 4096 >> shift) + ((previous * coef[0] + older * coef[1] + 32) >> 6);
                        older = previous;
                        previous = std::clamp(value, -32768, 32767);
                        pcm.push_back(int16_t(previous));
                    }
                }
                if (flags & 1) { repeat = mark && (flags & 2); break; }
            }
            return !pcm.empty();
        }
    };

    struct LiveZone { unsigned sample, root, low, high, volume, pan, adsr1, adsr2; };
    struct LiveControl { unsigned frame; bool pitch; int value; };
    struct LiveNote
    {
        unsigned frame, duration, track, control, velocity, volume;
        int bend;
        unsigned sample, adsr1, adsr2, program;
        double step, left, right;
    };
    struct LiveVoice
    {
        const LiveNote* note;
        LiveEnvelope env;
        unsigned control;
        double position = 0, step;
        int volume;
        explicit LiveVoice(const LiveNote& n)
            : note(&n), env(n.adsr1,n.adsr2), control(n.control),
              step(n.step * std::exp2(n.bend / 3072.0)), volume(int(n.volume)) {}
    };

    class LiveSong
    {
        std::vector<LiveSample> samples;
        std::vector<std::vector<LiveZone>> programs;
        std::vector<std::vector<LiveControl>> controls;
        std::vector<LiveNote> notes;
        std::vector<LiveVoice> voices;
        bool brain_hopper = false, victory_intro = false, has_loop = false;
        unsigned bpm = 0, frame = 0, end = 0, loop = 0;
        size_t next = 0, loop_note = 0;

        unsigned to_frame(unsigned ticks) const
        {
            return unsigned(std::nearbyint(double(ticks) * (LiveRate * 60.0 / (48 * bpm))));
        }

        bool bank(const LiveBytes& b)
        {
            if (b.size() < 64 || live_word(b.data()+8,4) != b.size() ||
                std::memcmp(b.data(),"swdm",4) != 0)
                return false;
            const unsigned count = b[0x14], nprogram = b[0x15], size = live_word(b.data()+0x1c,4);
            const unsigned start = live_word(b.data()+0x20,4), table = 64 + 32*count + 0x16;
            if (start > b.size() || size > b.size()-start || size % 16 || table+nprogram*0xd8 > start)
                return false;

            samples.resize(count);
            std::vector<unsigned> offsets(count, size), boundaries{size};
            for (unsigned i = 0; i < count; ++i)
            {
                const auto* entry = b.data()+64+32*i;
                if (std::none_of(entry, entry+32, [](uint8_t x){ return x != 0; }))
                    continue;
                const unsigned offset = live_word(entry+4,4);
                if (offset >= size || offset % 16)
                    return false;
                offsets[i] = offset;
                boundaries.push_back(offset);
                samples[i].tuning = live_signed(live_word(entry+14,2),16);
            }
            std::sort(boundaries.begin(), boundaries.end());
            programs.resize(256);
            for (unsigned i = 0; i < nprogram; ++i)
            {
                const auto* entry = b.data()+table+i*0xd8;
                if (!programs[entry[0]].empty() || 22+entry[1]*12 > 0xd8)
                    return false;
                for (unsigned j = 0; j < entry[1]; ++j)
                {
                    const auto* z = entry+22+j*12;
                    const unsigned index = z[0];
                    if (index >= count || offsets[index] == size || z[2] > z[3])
                        return false;
                    const unsigned offset = offsets[index];
                    const unsigned limit = *std::upper_bound(boundaries.begin(), boundaries.end(), offset);
                    if (samples[index].pcm.empty() && !samples[index].decode(b.data()+start+offset,limit-offset))
                        return false;
                    programs[entry[0]].push_back({index,z[1],z[2],z[3],z[4],z[5],live_word(z+6,2),live_word(z+8,2)});
                }
            }
            return true;
        }

        static int operands(unsigned op)
        {
            switch (op)
            {
                case 0x80: case 0x81: case 0x90: case 0x91: case 0xca: case 0xcb: return 0;
                case 0x82: case 0x83: case 0x84: case 0x94: case 0x9c: case 0xa8:
                case 0xa9: case 0xac: case 0xe0: case 0xe8: return 1;
                case 0x85: case 0xa0: case 0xd4: return 2;
                case 0xe2: return 3;
                case 0xfd: return 5;
                default: return -1;
            }
        }

        bool add_note(unsigned track, unsigned tick, unsigned duration, unsigned program,
                      int key, unsigned velocity, unsigned volume, int pan, int bend)
        {
            if (!velocity || !duration)
                return true;
            if (program >= programs.size() || programs[program].empty())
                return false;
            const auto distance = [key](const LiveZone& z) {
                return std::max({int(z.low)-key, key-int(z.high), 0});
            };
            const auto& zones = programs[program];
            const auto& z = *std::min_element(zones.begin(),zones.end(),
                [&](const LiveZone& a,const LiveZone& b){ return distance(a)<distance(b); });
            // Mesma calibracao da previa de Brain Hopper, sem normalizacao global.
            constexpr double gains[] = {.667,.904,1.031,.692,1.065,.762,1.054,1.13,.639,1.115,.959,1.628,.972};
            double gain = velocity * z.volume * (brain_hopper && program < 13 ? gains[program] : 1.0) / (127.0*127*127*32768);
            if (brain_hopper && program == 8 && z.sample == 14)
                gain *= 0.6309573445; // -4 dB, batida metalica HUBCAP R8.
            double balance = std::clamp(pan+int(z.pan)-64,0,127)/127.0;
            const double step = (SpuRate/double(LiveRate)) *
                std::exp2((key-int(z.root)+samples[z.sample].tuning/256.0)/12);
            notes.push_back({tick,duration,track,unsigned(controls[track].size()),velocity,volume,
                bend,z.sample,z.adsr1,z.adsr2,program,step,gain*std::sqrt(1-balance),gain*std::sqrt(balance)});
            return true;
        }

        bool track(const uint8_t* data, unsigned size, bool conductor)
        {
            const unsigned index = unsigned(controls.size());
            controls.emplace_back();
            unsigned tick = 0, duration = 0, program = 0, volume = 100;
            int delay = 0, octave = 4, pan = 64, bend = 0;
            for (unsigned pos = 0; pos < size;)
            {
                const unsigned op = data[pos++];
                const int n = op < 0x80 ? (pos < size ? 1+(data[pos] >> 6) : -1) : operands(op);
                if (n < 0 || unsigned(n) > size-pos)
                    return false;
                const auto* a = data+pos;
                pos += unsigned(n);
                if (op < 0x80)
                {
                    if (n > 1)
                    {
                        duration = 0;
                        for (int i = 1; i < n; ++i) duration = (duration << 8) | a[i];
                    }
                    octave += int((a[0] >> 4) & 3)-1;
                    const int key = (octave+1)*12+(a[0]&15);
                    if (key < 0 || key > 127 || (a[0]&15)>11 || duration>200000 ||
                        !add_note(index,tick,duration,program,key,op,volume,pan,bend))
                        return false;
                }
                else if (op <= 0x85)
                {
                    if (op == 0x80) delay = int(duration);
                    if (op == 0x82 || op == 0x83) delay = (op == 0x82 ? delay : int(duration)) + live_signed(a[0],8);
                    if (op >= 0x84) delay = int(live_word(a,unsigned(n)));
                    if (delay < 0 || unsigned(delay) > 200000-tick) return false;
                    tick += unsigned(delay);
                }
                else if (op == 0x90)
                {
                    if (conductor) end = tick;
                    return size-pos <= 3 && std::all_of(data+pos,data+size,[](uint8_t b){ return b == 0; });
                }
                else if (op == 0x91 && conductor)
                {
                    loop = tick;
                    has_loop = true;
                }
                else if (op == 0x94) octave = a[0];
                else if (op == 0x9c && conductor) bpm = a[0];
                else if (op == 0xac) program = a[0];
                else if (op == 0xe0)
                {
                    volume = std::min(127u,unsigned(a[0]));
                    controls[index].push_back({tick,false,int(volume)});
                }
                else if (op == 0xd4)
                {
                    bend = live_signed(live_word(a,2),16);
                    controls[index].push_back({tick,true,bend});
                }
                else if (op == 0xe8) pan = std::min(127,int(a[0]));
            }
            return false;
        }

    public:
        bool load(const LiveBytes& seq, const LiveBytes& swd)
        {
            if (seq.size() < 0x78 || swd.size() < 0x40 ||
                std::memcmp(seq.data(),"smdm",4) != 0 || live_word(seq.data()+8,4) != seq.size() ||
                live_word(seq.data()+0x1e,2) != live_word(swd.data()+0x12,2) || !bank(swd))
                return false;
            brain_hopper = std::memcmp(seq.data()+0x2c,"bgm401",7) == 0;
            victory_intro = std::memcmp(seq.data()+0x2c,"BGM409",7) == 0;
            // O campo 0x2A aponta para o primeiro bloco, relativo a 0x28.
            unsigned pos = 0x28 + live_word(seq.data()+0x2a,2);
            if (pos >= seq.size())
                return false;
            while (pos+8 <= seq.size() && live_word(seq.data()+pos,2) == 3)
            {
                const unsigned block = live_word(seq.data()+pos+2,2);
                if (block < 9 || block > seq.size()-pos ||
                    !track(seq.data()+pos+8,block-8,live_word(seq.data()+pos+6,2)==0))
                    return false;
                pos += block;
            }
            constexpr uint8_t finish[] = {4,0,8,0,0,0,0,0,0,0,4,0};
            if (seq.size()-pos != sizeof finish || std::memcmp(seq.data()+pos,finish,sizeof finish) ||
                bpm < 30 || end == 0 || (has_loop && loop >= end) || notes.empty())
                return false;

            end = to_frame(end);
            loop = to_frame(loop);
            for (auto& tr : controls)
                for (auto& c : tr) c.frame = to_frame(c.frame);
            for (auto& n : notes)
            {
                n.frame = to_frame(n.frame);
                n.duration = to_frame(n.duration);
            }
            std::stable_sort(notes.begin(),notes.end(),[](const LiveNote& a,const LiveNote& b){ return a.frame<b.frame; });
            loop_note = size_t(std::lower_bound(notes.begin(),notes.end(),loop,
                [](const LiveNote& n,unsigned at){ return n.frame<at; })-notes.begin());
            voices.reserve(48);
            return true;
        }

        void render(int16_t* dest, unsigned frames)
        {
            for (unsigned i = 0; i < frames; ++i, ++frame)
            {
                if (frame == end)
                {
                    // Como na previa validada, as vozes retornam ao ponto de loop.
                    voices.clear();
                    if (!has_loop)
                    {
                        std::fill_n(dest,2*(frames-i),int16_t(0));
                        return;
                    }
                    victory_intro = false;
                    frame = loop;
                    next = loop_note;
                }
                while (next < notes.size() && notes[next].frame == frame)
                    voices.emplace_back(notes[next++]);

                // Calibracao da abertura de BGM409 com a captura do PCSX2.
                // O orgao recua e as cordas ganham presenca, com retorno gradual
                // aos ganhos do banco antes dos efeitos registrados na vitoria.
                const double intro = victory_intro
                    ? std::clamp((3.0 * LiveRate - frame) / (0.5 * LiveRate), 0.0, 1.0)
                    : 0.0;
                double left = 0, right = 0;
                for (auto v = voices.begin(); v != voices.end();)
                {
                    const auto& n = *v->note;
                    const auto& sample = samples[n.sample];
                    if (frame == n.frame+n.duration) v->env.release();
                    auto& tr = controls[n.track];
                    while (v->control < tr.size() && tr[v->control].frame <= frame)
                    {
                        const auto& c = tr[v->control++];
                        if (c.pitch) v->step = n.step * std::exp2(c.value/3072.0);
                        else v->volume = c.value;
                    }
                    unsigned pos = unsigned(v->position);
                    const unsigned size = unsigned(sample.pcm.size());
                    if ((v->env.stage == 3 && !v->env.level) || (pos >= size && !sample.repeat))
                    {
                        v = voices.erase(v);
                        continue;
                    }
                    const double fraction = v->position-pos;
                    if (pos >= size)
                    {
                        pos = sample.loop+(pos-sample.loop)%(size-sample.loop);
                        v->position = pos+fraction;
                    }
                    const unsigned next_pos = (pos+1 == size && sample.repeat) ? sample.loop : pos+1;
                    const int a = sample.pcm[pos], b = next_pos < size ? sample.pcm[next_pos] : 0;
                    const double value = (a+(b-a)*fraction) * v->volume * (v->env.level/32768.0);
                    const double gain = 1.0 + intro * (n.program == 0 ? -0.30 : n.program == 1 ? 0.20 : 0.0);
                    left += value*n.left*gain;
                    right += value*n.right*gain;
                    v->env.advance();
                    v->position += v->step;
                    ++v;
                }
                // Ganho fixo com margem para a soma das vozes; independe da duracao.
                *dest++ = int16_t(std::clamp(left*16384,-32768.0,32767.0));
                *dest++ = int16_t(std::clamp(right*16384,-32768.0,32767.0));
            }
        }
    };
}
namespace
{
    struct EffectBank
    {
        std::shared_ptr<const LiveBytes> swd, auxiliary;
        LiveBytes seds;
        roj::EffectBankKey external_key;
    };

    struct EffectRequest
    {
        std::shared_ptr<const EffectBank> bank;
        unsigned id, volume, pan;
        uint64_t trace_id = 0;
        bool use_recording = false;
        bool item_pickup = false;
    };

    struct EffectVolumeRamp
    {
        unsigned start, length, target;
    };

    struct EffectVoice
    {
        std::shared_ptr<const LiveSample> sample;
        unsigned id, delay, remaining, release;
        unsigned total, attack;
        bool repeat_sample;
        double position = 0, step, left, right;
        double pitch_factor = 1.0;
        unsigned glide_left = 0, glide_delay = 0;
        double smoothing = 1.0, filtered = 0.0;
        unsigned shatter_frame = 0;
        double shatter_low = 0.0, shatter_high = 0.0;
        bool from_recording = false;
        bool loop_ambient = false;
        std::vector<EffectVolumeRamp> ramps;
        unsigned ramp_index = 0, ramp_remaining = 0, elapsed = 0;
        double volume_level = 1.0, volume_step = 0.0;
    };

    class LiveEffects
    {
        static constexpr unsigned ShatterDuration = LiveRate*850/1000;
        static constexpr unsigned ShatterFade = LiveRate*220/1000;

        std::vector<EffectVoice> voices;
        roj::ExternalEffects external;
        struct RecordedEffect
        {
            std::shared_ptr<const LiveSample> channels[2];
            double gain;
        };
        std::map<unsigned,RecordedEffect> recordings;
        using CacheKey = std::pair<std::shared_ptr<const LiveBytes>, unsigned>;
        std::map<CacheKey,std::shared_ptr<const LiveSample>> cache;

        std::shared_ptr<const LiveSample> sample(std::shared_ptr<const LiveBytes> bank, unsigned index)
        {
            if (!bank) return {};
            const auto& b = *bank;
            if (b.size()<64 || index>=b[20])
                return {};
            const auto key = std::make_pair(bank,index);
            if (auto i = cache.find(key); i != cache.end())
                return i->second;
            if (cache.size() >= 64)
                cache.clear();

            const unsigned start = live_word(b.data()+32,4), size = live_word(b.data()+28,4);
            const unsigned entries = 64 + 32 * b[20];
            if (entries > start || start > b.size() || size > b.size()-start)
                return {};
            const auto* entry = b.data()+64+32*index;
            if (std::none_of(entry,entry+32,[](uint8_t v){return v != 0;}))
                return {};
            const unsigned offset = live_word(entry+4,4);
            if (offset >= size || offset%16 || size%16)
                return {};
            unsigned end = size;
            for (unsigned i=0; i<b[20]; ++i)
            {
                const unsigned at = live_word(b.data()+68+32*i,4);
                if (at > offset && at < end) end=at;
            }
            auto decoded = std::make_shared<LiveSample>();
            if (end%16 || !decoded->decode(b.data()+start+offset,end-offset))
                return {};
            decoded->tuning = live_signed(live_word(entry+14,2),16);
            cache.emplace(key,decoded);
            return decoded;
        }

    public:
        void external_root(const std::filesystem::path& root)
        {
            if(external.configure(root))std::fprintf(stderr,"  [audio RSSD ao vivo] pacote de efeitos externos ativo\n");
        }
        bool load_effect_wav(unsigned id, double playback_gain, const LiveBytes& wav)
        {
            // O arquivo do efeito usa PCM estereo de 16 bits na taxa do mixer.
            if (wav.size()<12 || std::memcmp(wav.data(),"RIFF",4) ||
                std::memcmp(wav.data()+8,"WAVE",4) || live_word(wav.data()+4,4)!=wav.size()-8)
                return false;

            bool format_ok = false;
            const uint8_t* pcm = nullptr;
            unsigned frames = 0;
            for (size_t offset=12; offset+8<=wav.size();)
            {
                const auto* chunk = wav.data()+offset;
                const unsigned bytes = live_word(chunk+4,4);
                offset += 8;
                if (bytes > wav.size()-offset || (bytes&1u) > wav.size()-offset-bytes)
                    return false;
                const auto* data = wav.data()+offset;
                if (!std::memcmp(chunk,"fmt ",4))
                {
                    format_ok = bytes>=16 && live_word(data,2)==1 &&
                        live_word(data+2,2)==2 && live_word(data+4,4)==LiveRate &&
                        live_word(data+12,2)==4 && live_word(data+14,2)==16;
                }
                else if (!std::memcmp(chunk,"data",4))
                {
                    if (bytes%4 || bytes<4 || bytes>LiveRate*8)
                        return false;
                    pcm = data;
                    frames = bytes/4;
                }
                offset += bytes+(bytes&1u);
            }
            if (!format_ok || !pcm)
                return false;

            RecordedEffect effect{};
            effect.gain = playback_gain;
            for (unsigned channel=0; channel<2; ++channel)
            {
                auto sample = std::make_shared<LiveSample>();
                sample->pcm.resize(frames);
                for (unsigned i=0; i<frames; ++i)
                    sample->pcm[i] = int16_t(live_signed(live_word(pcm+4*i+2*channel,2),16));
                effect.channels[channel] = std::move(sample);
            }
            recordings.insert_or_assign(id,std::move(effect));
            return true;
        }

        unsigned play(const EffectRequest& request)
        {
            if(request.bank && external.play(request.bank->external_key,request.id,request.volume,request.pan,true))return 1;
            if (auto it=recordings.find(request.id); request.use_recording && it!=recordings.end())
            {
                // Mantem a separacao L/R e aplica o volume do pedido original.
                const auto& clip = it->second;
                const double gain = clip.gain*std::min(request.volume,127u)/127.0;
                if (gain==0.0)
                    return 0;
                const unsigned pan = std::min(request.pan,127u);
                const unsigned frames = unsigned(clip.channels[0]->pcm.size());
                if (voices.size()>30)
                    voices.erase(voices.begin(),voices.begin()+(voices.size()-30));
                const double left = gain*std::min(1.0,(127-pan)/63.0);
                const double right = gain*std::min(1.0,pan/64.0);
                voices.push_back({clip.channels[0],request.id,0,frames,0,frames,0,false,0,1.0,left,0});
                voices.back().from_recording=true;
                voices.push_back({clip.channels[1],request.id,0,frames,0,frames,0,false,0,1.0,0,right});
                voices.back().from_recording=true;
                return 2;
            }

            if(request.bank && external.play(request.bank->external_key,request.id,request.volume,request.pan))return 1;
            if (!request.bank)
                return 0;
            const auto& b = *request.bank;
            const auto& d = b.seds;
            constexpr unsigned table = 0x34;
            if (d.size() < table)
                return 0;
            const unsigned effect_id = request.id & 0xffffu;
            const unsigned common_id = request.id >> 16 ? UINT32_MAX : effect_id;
            // HandleNpcDie1/3/6, StartSequence e Polling de CBtlEnd.
            const bool death = common_id == 0x22 || common_id == 0x25 || common_id == 0x39;
            const bool encounter_intro = common_id == 0x15;
            const bool result_count = common_id == 0x17;
            const bool ring_hit = common_id == 0x1c || common_id == 0x1d;
            const bool ring_cue = common_id >= 0x19 && common_id <= 0x21;
            const bool item_cue = request.item_pickup || common_id == 0x18;
            const bool automated_effect = ring_cue || item_cue || encounter_intro || death;
            const bool banked_effect = (request.id >> 16) != 0;
            const unsigned count = live_word(d.data()+26,2);
            // SsdPlayEffectParam no SSD.IRX usa SEDS + 0x34 + 2 * ID,
            // inclusive para regist01. Um offset zero significa ausência.
            const bool wind_shear = std::memcmp(d.data()+0x20,"39A_S.sav",10) == 0;
            if (effect_id >= count || table + 2*count > d.size())
                return 0;
            const unsigned offset = live_word(d.data()+table+2*effect_id,2);
            const unsigned wave_bank = live_word(d.data()+0x1c,2);
            const bool train_bank = effect_id == 0x0c &&
                (wave_bank == 0x0310 || wave_bank == 0x0311) &&
                std::memcmp(d.data()+0x20, "TT", 2) == 0;
            if (offset < table + 2*count || offset+8 > d.size())
                return 0;
            const auto* entry = d.data()+offset;
            const unsigned tracks = entry[3], length = live_word(entry+6,2);
            const bool train_ambient = train_bank && entry[0] == 0x11;
            if ((entry[0] != 1 && !train_ambient) ||
                length < 8+2*tracks || length > d.size()-offset)
                return 0;
            if (train_ambient)
                stop(request.id, false);
            unsigned played = 0;
            for (unsigned i=0; i<tracks; ++i)
            {
                unsigned at = live_word(entry+8+2*i,2);
                const unsigned stop = i+1 < tracks ? live_word(entry+10+2*i,2) : length;
                if (at < 8+2*tracks || at >= stop || stop > length)
                    continue;
                unsigned index=256, volume=127, pan=request.pan;
                unsigned wait=0, duration=0, glide_ticks=0, glide_start=0, volume_ramp=0;
                int glide_semitones=0, detune_cents=0;
                int root=-1, pitch=-1, note_offset=0;
                int next_note=-1, next_pitch=-1;
                unsigned next_duration=0, next_start=0;
                bool valid=false, pitched=false;
                bool repeat_sample=true;
                std::vector<EffectVolumeRamp> ramps;
                // Subconjunto dos SEDS para disparos de uma amostra por camada.
                // Comandos de modulacao conhecidos mantem o alinhamento da pista.
                while (at < stop)
                {
                    const unsigned op=entry[at++];
                    if (op==0xca || op==0xcb || op==0xc2 || op==0xc1 || op==0xc0 || op==0x99 || op==0x91) continue;
                    if (op < 0x80)
                    {
                        if (at == stop) break;
                        if (item_cue || ring_hit)
                            note_offset = op & 15;
                        duration = entry[at++];
                        pitched = root >= 0 && pitch >= 0;
                        valid = true;
                        break;
                    }
                    unsigned width=0;
                    switch (op)
                    {
                        case 0xaa: case 0xe0: case 0xe8: case 0x88:
                        case 0xb2: case 0xb5: case 0xd0: case 0xd1: case 0xd2: case 0x98: width=1; break;
                        case 0x96: case 0x97: case 0xd3: case 0xc9: case 0xb7: width=2; break;
                        case 0x94: case 0xd5: case 0xd8: case 0xd9: case 0xe2: case 0xe5: width=3; break;
                        default: break;
                    }
                    if (!width || width > stop-at)
                        break;
                    const auto* args=entry+at;
                    at+=width;
                    switch (op)
                    {
                        case 0xaa: index=args[0]; break;
                        case 0xe0: volume=std::min(127u,unsigned(args[0])); break;
                        case 0xe2:
                            if (automated_effect)
                                ramps.push_back({0,live_word(args,2),std::min(127u,unsigned(args[2]))});
                            else if (!volume && args[2])
                            {
                                volume=std::min(127u,unsigned(args[2]));
                                volume_ramp=live_word(args,2);
                            }
                            break;
                        case 0xe8: pan=unsigned(std::clamp(int(pan)+int(args[0])-64,0,127)); break;
                        case 0x88: wait+=args[0]; break;
                        case 0xb2: root=args[0]; break;
                        case 0xb5: pitch=args[0]; break;
                        case 0xd3:
                            // As pistas emparelhadas da transicao usam afinacoes ligeiramente distintas.
                            if (encounter_intro)
                                detune_cents = live_signed(live_word(args,2),16);
                            break;
                        case 0xd5:
                            // Interpretacao experimental do D5 do efeito 2:
                            // duracao little-endian e deslocamento assinado.
                            glide_ticks=live_word(args,2);
                            glide_semitones=live_signed(args[2],8);
                            break;
                        case 0x97: case 0x94:
                            // Disparos 94 e 97 observados nos efeitos desta rodada.
                            valid=true;
                            pitched=op==0x94 || root>=0;
                            duration=args[width-1];
                            break;
                        default: break;
                    }
                    if (op==0x96)
                    {
                        valid=true;
                        repeat_sample=false;
                        if (automated_effect)
                            duration=args[1];
                        break;
                    }
                    // Preserve o disparo mesmo se houver automacao posterior na pista.
                    if (op==0x94 || op==0x97)
                        break;
                }
                if (valid && (automated_effect || banked_effect))
                {
                    // Depois do disparo, 89 adianta a pista e E2 interpola o volume.
                    unsigned tick = 0;
                    while (at < stop)
                    {
                        const unsigned op = entry[at++];
                        if (op == 0x89 && at < stop)
                            tick += entry[at++];
                        else if (op == 0xe2 && stop - at >= 3)
                        {
                            const unsigned length = live_word(entry+at,2);
                            const unsigned target = std::min(127u,unsigned(entry[at+2]));
                            ramps.push_back({tick, length, target});
                            at += 3;
                        }
                        else if (op == 0xd5 && stop - at >= 3)
                        {
                            glide_ticks = live_word(entry+at,2);
                            glide_semitones = live_signed(entry[at+2],8);
                            glide_start = tick;
                            at += 3;
                        }
                        else if (ring_cue && op == 0xb5 && at < stop)
                            next_pitch = entry[at++];
                        else if (ring_cue && op < 0x80 && at < stop)
                        {
                            next_note = op & 15;
                            next_duration = entry[at++];
                            next_start = std::max(tick, duration);
                            break;
                        }
                        else if (op == 0xc9 && stop - at >= 2)
                            at += 2;
                        else if (op == 0x91 || op == 0x99 || op == 0xca || op == 0xcb ||
                                 op == 0xc0 || op == 0xc1 || op == 0xc2)
                            continue;
                        else
                            break;
                    }
                }
                if (!valid)
                    continue;

                // Estas camadas dependem de modulacao ou roteamento que
                // ainda nao reproduzimos. Preserve o ataque, sem falso eco
                // no acerto nem prolongar as ondas tonais de cursor/contagem.
                if (common_id==0x1d && index==13 && i!=2)
                    continue;
                // FEFFECT toca duas pistas da mesma onda de ruido ao iniciar a carga.
                const bool special_charge = common_id == 0x33 && index == 9;
                if (special_charge && i!=0)
                    continue;
                if (common_id==0x21 || death)
                    repeat_sample=false;
                auto pcm=sample(b.swd,index);
                if (!pcm) pcm=sample(b.auxiliary,index);
                if (!pcm)
                    continue;
                // A escala de ticks e a diferenca B2/B5 sao aproximacoes
                // para as camadas observadas, sem aplicar comandos de modulacao.
                const int semitones = note_offset + ((pitched && root>=0 && pitch>=0) ? pitch-root : 0);
                // Na quebra do anel, a amostra de impacto bate com a captura a 75% da taxa.
                const double rate= (common_id==0x21 && index==10) ? 0.75 : 1.0;
                const double step=SpuRate/double(LiveRate)*
                    std::exp2(pcm->tuning/3072.0+semitones/12.0+detune_cents/1200.0)*rate;
                // Reequilibra o impacto frente a cauda do estilhaco do anel.
                const double impact_gain = (common_id==0x21 && index==10) ? 0.35 : 1.0;
                const double gain=0.24*std::min(request.volume,127u)*(ramps.empty()?volume:127u)/(127.0*127)*impact_gain;
                if (gain==0.0)
                    continue;
                const unsigned ticks_per_beat=48;
                const unsigned tempo=entry[1] ? entry[1] : 75u;
                const unsigned tick_frames=LiveRate*60/(ticks_per_beat*tempo);
                unsigned release=(pitched || !repeat_sample)?LiveRate/100:0;
                unsigned remaining=(pitched || automated_effect) && duration?duration*tick_frames+release:UINT32_MAX;
                if (common_id==0x21)
                    // A amostra de impacto pode durar mais que a quebra do anel.
                    remaining=std::min(remaining,ShatterDuration);
                unsigned attack=std::min(volume_ramp,96u)*tick_frames;
                double envelope_gain=gain;
                if (death)
                    release = LiveRate * 45u / 1000u;
                if (result_count)
                {
                    release = LiveRate * 16u / 1000u;
                    attack = LiveRate * 2u / 1000u;
                    repeat_sample = true;
                    remaining = LiveRate * 60u / 1000u;
                    envelope_gain *= 0.8;
                }
                if (ring_cue)
                {
                    release = LiveRate * 20u / 1000u;
                    // No erro, os transientes do banco tocam uma vez. Repetir o loop
                    // sem a modulacao da sequencia alonga o aviso.
                    if (common_id == 0x1e || common_id == 0x1f)
                        repeat_sample = false;
                }
                if (!ramps.empty())
                {
                    const auto& last = ramps.back();
                    release = LiveRate * (death ? 45u : 20u) / 1000u;
                    const unsigned end = (last.start + last.length) * tick_frames;
                    if (last.target == 0)
                        remaining = end + release;
                    else if (remaining != UINT32_MAX)
                        remaining = std::max(remaining, end + release);
                    attack = 0;
                }
                if (item_cue)
                {
                    // A própria sequência contém o espaçamento entre os
                    // dois toques. Preserve o loop da onda durante a nota.
                    attack = LiveRate * 2u / 1000u;
                    release = LiveRate * 12u / 1000u;
                    remaining = duration * tick_frames + release;
                }
                if (ring_hit)
                {
                    attack = LiveRate * 2u / 1000u;
                    release = LiveRate * 25u / 1000u;
                    remaining = std::min(remaining, LiveRate * 210u / 1000u);
                    repeat_sample = false;
                }
                if (wind_shear)
                {
                    repeat_sample = false;
                    release = LiveRate * 25u / 1000u;
                    remaining = duration * tick_frames + release;
                }
                if (train_ambient)
                {
                    remaining = UINT32_MAX;
                    envelope_gain *= 0.45;
                }
                if (special_charge)
                {
                    repeat_sample = false;
                    attack = LiveRate * 6u / 1000u;
                    release = LiveRate * 90u / 1000u;
                    remaining = std::min(remaining, LiveRate * 180u / 1000u);
                    envelope_gain *= 0.7;
                }
                // O sinal 0x1E do anel usa a mesma onda do cancelamento.
                // Sem a envoltoria, o laco sustenta um apito ate perto da quebra.
                const bool short_saw = common_id==4 || (common_id==0x1e && index==5);
                if (short_saw || common_id==2 || common_id==3)
                {
                    // Calibracao de interface. As ondas sinteticas curtas
                    // do banco precisam de cauda, mas nao de ganho sustentado.
                    const unsigned length_ms=common_id==3?105:65;
                    attack=LiveRate*6/1000;
                    release=LiveRate*55/1000;
                    remaining=LiveRate*length_ms/1000;
                    envelope_gain*=common_id==2?0.72:0.56;
                    // O cursor usa uma amostra gravada. Confirma e cancela
                    // usam formas de onda que precisam repetir para ter cauda.
                    repeat_sample=common_id!=2;
                }
                if (common_id==0x1d && index==9)
                {
                    // A pista de ruido branco entra depois do impacto do acerto.
                    // Uma cauda breve impede que o ruido acumule nos acertos seguintes.
                    repeat_sample=false;
                    remaining=std::min(remaining,LiveRate*100u/1000);
                    release=LiveRate*80/1000;
                    envelope_gain*=0.35;
                }
                if (voices.size()==32)
                    voices.erase(voices.begin());
                voices.push_back({pcm,request.id,wait*tick_frames,remaining,release,
                    remaining,attack,repeat_sample,0,step,
                    envelope_gain*std::sqrt(1-pan/127.0),envelope_gain*std::sqrt(pan/127.0)});
                auto& voice=voices.back();
                voice.loop_ambient = train_ambient;
                if (!ramps.empty())
                {
                    for (auto& ramp : ramps)
                    {
                        ramp.start *= tick_frames;
                        ramp.length *= tick_frames;
                    }
                    voice.ramps = std::move(ramps);
                    voice.volume_level = volume / 127.0;
                }
                if (common_id==2)
                {
                    // O ataque e a cauda do cursor foram medidos na gravacao.
                    // O relogio da amostra e a duracao da voz sao independentes.
                    constexpr double cursor_rate=2.151;
                    voice.step*=cursor_rate;
                    voice.remaining=voice.total=LiveRate*60/1000;
                    voice.attack=LiveRate*3/1000;
                    voice.release=LiveRate*15/1000;
                }
                if ((common_id==3 || automated_effect) && glide_ticks && std::abs(glide_semitones)<=48)
                {
                    voice.glide_left=glide_ticks*tick_frames;
                    voice.glide_delay=glide_start*tick_frames;
                    voice.pitch_factor=std::exp2(glide_semitones/(12.0*voice.glide_left));
                }
                if (short_saw || result_count || special_charge)
                {
                    // Atenua harmonicos agudos das ondas dente de serra.
                    voice.smoothing=1.0-std::exp(-2.0*3.141592653589793*1800.0/LiveRate);
                }
                ++played;
                if (next_duration && next_note >= 0)
                {
                    // Entradas do anel com dois disparos na mesma pista (94/97 seguido de nota).
                    EffectVoice followup = voice;
                    followup.delay += next_start*tick_frames;
                    followup.total = followup.remaining = next_duration*tick_frames + release;
                    followup.glide_left = followup.glide_delay = 0;
                    followup.step = step * std::exp2((next_note - note_offset +
                        (next_pitch >= 0 && root >= 0 ? next_pitch - (pitch >= 0 ? pitch : root) : 0))/12.0);
                    // A segunda nota continua no volume definido pela última rampa da pista.
                    if (!followup.ramps.empty())
                    {
                        followup.volume_level = followup.ramps.back().target / 127.0;
                        followup.ramps.clear();
                    }
                    if (voices.size()==32)
                        voices.erase(voices.begin());
                    voices.push_back(std::move(followup));
                    ++played;
                }
            }
            return played;
        }

        void stop(unsigned id, bool whole_bank)
        {
            external.stop(id,whole_bank);
            voices.erase(std::remove_if(voices.begin(),voices.end(),[=](const EffectVoice& v){
                return id == (whole_bank ? v.id >> 16 : v.id);
            }),voices.end());
        }

        void render(int16_t* pcm, unsigned frames, double gain = 1.0)
        {
            for (unsigned i=0; i<frames && !voices.empty(); ++i)
            {
                double left=pcm[2*i], right=pcm[2*i+1];
                for(auto v=voices.begin(); v!=voices.end();)
                {
                    if (v->delay)
                    {
                        --v->delay;
                        ++v;
                        continue;
                    }
                    const auto& sample=*v->sample;
                    const auto& data=sample.pcm;
                    const bool looping = sample.repeat && v->repeat_sample &&
                        (v->remaining != UINT32_MAX || v->loop_ambient);
                    if (v->position>=data.size() && looping)
                        v->position=sample.loop+std::fmod(v->position-sample.loop,data.size()-sample.loop);
                    const auto at=size_t(v->position);
                    if (!v->remaining || at>=data.size())
                    {
                        v=voices.erase(v);
                        continue;
                    }
                    const int next=at+1<data.size()?data[at+1]:
                        (looping ? data[sample.loop] : 0);
                    double value=data[at]+(next-data[at])*(v->position-at);
                    if (v->smoothing<1.0)
                    {
                        v->filtered+=v->smoothing*(value-v->filtered);
                        value=v->filtered;
                    }
                    double tail=v->remaining;
                    if (!looping)
                        tail=std::min(tail,(data.size()-v->position)/v->step);
                    if (v->attack)
                    {
                        const double envelope=std::min({1.0,double(v->total-v->remaining)/v->attack,tail/v->release});
                        value*=envelope*envelope;
                    }
                    else if (tail<v->release)
                        value*=tail/v->release;
                    if (v->id==0x21 && !v->from_recording)
                    {
                        // Dois filtros simples destacam de 1,3 a 4,5 kHz na cauda.
                        constexpr double two_pi = 6.283185307179586;
                        static const double low = 1.0-std::exp(-two_pi*1300.0/LiveRate);
                        static const double high = 1.0-std::exp(-two_pi*4500.0/LiveRate);
                        v->shatter_low += low*(value-v->shatter_low);
                        v->shatter_high += high*(value-v->shatter_high);
                        const unsigned frame = v->shatter_frame++;
                        const double seconds = double(frame)/LiveRate;
                        const double attack = std::min(1.0,seconds/0.054);
                        const double rise = std::clamp((seconds-0.12)/0.20,0.0,1.0);
                        const double fall = std::clamp((0.60-seconds)/0.28,0.0,1.0);
                        // Suaviza o transiente e realca os fragmentos depois do impacto.
                        // Aplica a queda final no tempo do efeito, independente do fim da amostra.
                        const double fade = std::clamp((double(ShatterDuration)-frame)/ShatterFade,0.0,1.0);
                        value = fade*attack*(value+1.3*rise*fall*(v->shatter_high-v->shatter_low));
                    }
                    if (v->remaining!=UINT32_MAX)
                        --v->remaining;
                    if (v->ramp_index < v->ramps.size() || v->ramp_remaining)
                    {
                        while (v->ramp_index < v->ramps.size() &&
                               v->ramps[v->ramp_index].start == v->elapsed)
                        {
                            const auto& ramp = v->ramps[v->ramp_index++];
                            const double target = ramp.target / 127.0;
                            v->ramp_remaining = ramp.length;
                            if (ramp.length)
                                v->volume_step = (target - v->volume_level) / ramp.length;
                            else
                                v->volume_level = target;
                        }
                        if (v->ramp_remaining)
                        {
                            --v->ramp_remaining;
                            v->volume_level += v->volume_step;
                        }
                        ++v->elapsed;
                    }
                    value *= v->volume_level;
                    left+=value*v->left*gain;
                    right+=value*v->right*gain;
                    v->position+=v->step;
                    if (v->glide_delay)
                        --v->glide_delay;
                    else if (v->glide_left)
                    {
                        v->step*=v->pitch_factor;
                        --v->glide_left;
                    }
                    ++v;
                }
                pcm[2*i]=int16_t(std::clamp(left,-32768.0,32767.0));
                pcm[2*i+1]=int16_t(std::clamp(right,-32768.0,32767.0));
            }
            external.render(pcm,frames,gain);
        }
    };
}

#endif

// As opcoes do perfil de batalha ficam ativas se a variavel estiver
// ausente. Use 0 para desativar uma opcao em um teste A/B.
static bool battle_profile_enabled(const char* name)
{
    const char* value = std::getenv(name);
    return !value || std::strcmp(value, "0") != 0;
}

extern const bool g_rssd_audio_trace = [] {
    const char* value = std::getenv("PS2_RSSD_AUDIO_TRACE");
    return value && std::strcmp(value, "1") == 0;
}();

const char* ps2_syscall_name(int number);   // gerado em ps2_syscall_names.cpp

typedef void (*Ps2Func)(Ctx&);

static const Ps2FuncEntry* find_entry(uint32_t address)
{
    unsigned lo = 0;
    unsigned hi = ps2_function_count;

    while (lo < hi)
    {
        unsigned mid = (lo + hi) / 2;
        uint32_t a = ps2_function_table[mid].address;

        if (a == address) return &ps2_function_table[mid];
        if (a < address) lo = mid + 1; else hi = mid;
    }

    return nullptr;
}

// Funcao cujo inicio e o maior endereco <= address (para achar "quem chamou" a partir do $ra).
static const Ps2FuncEntry* find_containing(uint32_t address)
{
    unsigned lo = 0;
    unsigned hi = ps2_function_count;

    while (lo < hi)
    {
        unsigned mid = (lo + hi) / 2;

        if (ps2_function_table[mid].address <= address) lo = mid + 1; else hi = mid;
    }

    return lo == 0 ? nullptr : &ps2_function_table[lo - 1];
}

#ifdef PS2_TRACE
static uint32_t g_ring[64];
static unsigned g_ring_pos = 0;

// PS2_WATCH=0x00234790,0x002216A0 imprime os argumentos ($a0..$a3), $ra e $sp a cada entrada nessas
// funcoes. PS2_WATCH_MIN=N so imprime quando $a0 ou $a1 for >= N (ex.: 0x100000 para ver alocacoes
// grandes em malloc). Util para achar quem pede memoria demais.
static uint32_t g_watch[16];
static unsigned g_watch_n = 0;
static uint32_t g_watch_min = 0;
static unsigned g_watch_prints = 0;

static int ps2_watch_init()
{
    const char* e = std::getenv("PS2_WATCH");

    if (e != nullptr)
    {
        while (*e != 0 && g_watch_n < 16)
        {
            char* end = nullptr;
            unsigned long v = std::strtoul(e, &end, 0);

            if (end == e)
                break;

            g_watch[g_watch_n++] = (uint32_t)v;
            e = *end == ',' ? end + 1 : end;
        }
    }

    const char* m = std::getenv("PS2_WATCH_MIN");

    if (m != nullptr)
        g_watch_min = (uint32_t)std::strtoul(m, nullptr, 0);

    return 0;
}

static int g_watch_ready = ps2_watch_init();

void ps2_trace_enter(Ctx& c, uint32_t address)
{
    g_ring[g_ring_pos++ & 63u] = address;

    if (g_watch_n == 0)
        return;

    for (unsigned i = 0; i < g_watch_n; i++)
    {
        if (g_watch[i] != address)
            continue;

        uint32_t a0 = (uint32_t)c.r[4].lo, a1 = (uint32_t)c.r[5].lo;

        if (g_watch_min != 0 && a0 < g_watch_min && a1 < g_watch_min)
            return;

        if (g_watch_prints++ < 400)
        {
            const Ps2FuncEntry* e = find_entry(address);

            std::fprintf(stderr, "  [watch] %s 0x%08X a0=0x%08X a1=0x%08X a2=0x%08X a3=0x%08X ra=0x%08X sp=0x%08X\n",
                e ? e->name : "?", address, a0, a1, (uint32_t)c.r[6].lo, (uint32_t)c.r[7].lo,
                (uint32_t)c.r[31].lo, (uint32_t)c.r[29].lo);
        }

        return;
    }
}
#endif

static void dump_trace()
{
#ifdef PS2_TRACE
    unsigned count = g_ring_pos < 32 ? g_ring_pos : 32;

    std::fprintf(stderr, "ultimas funcoes (da mais antiga para a mais recente):\n");

    for (unsigned i = count; i > 0; i--)
    {
        uint32_t a = g_ring[(g_ring_pos - i) & 63u];
        const Ps2FuncEntry* e = find_entry(a);

        std::fprintf(stderr, "  0x%08X  %s\n", a, e ? e->name : "?");
    }
#else
    std::fprintf(stderr, "(compile com -DPS2_TRACE para ver as ultimas funcoes)\n");
#endif
}

[[noreturn]] static void die()
{
    dump_trace();
    std::fflush(stderr);
    std::abort();
}


void ps2_call(Ctx& c, uint32_t target)
{
    const Ps2FuncEntry* e = find_entry(target);

    if (!e)
    {
        std::fprintf(stderr, "ps2_call: 0x%08X nao e inicio de funcao conhecida\n", target);
        die();
    }

    e->fn(c);
}

// JR indireto: o destino precisa ser o inicio de uma funcao (tail call).
void ps2_tailcall(Ctx& c, uint32_t target)
{
    const Ps2FuncEntry* e = find_entry(target);

    if (!e)
    {
        std::fprintf(stderr, "salto indireto para 0x%08X, que nao e inicio de funcao (longjmp ou catch?)\n", target);
        die();
    }

    e->fn(c);
}

// ---- hardware: por enquanto so registra os primeiros acessos ----

static unsigned g_hw_log = 0;
static uint32_t g_poll_address = 0;
static unsigned g_poll_count = 0;

// ---- estado das interrupcoes (compartilhado entre o modelo de hardware, os syscalls e o tique) ----
// Os dois contadores sao publicados no mesmo CAS e consumidos no mesmo exchange.
static constexpr unsigned irq_timer0_bits = 8;
static constexpr uint32_t irq_timer0_mask = (1u << irq_timer0_bits) - 1u;
static constexpr uint32_t irq_vblank_max = UINT32_MAX >> irq_timer0_bits;
std::atomic<uint32_t> g_irq_pending{0};
static std::atomic<uint64_t> g_vsync_gen(0), g_vsync_del(0);   // VBLANKs gerados pelo relogio / entregues ao jogo (diagnostico do slow-motion)

void ps2_battle_effect_clock(Ctx& c)
{
    static const bool enabled = battle_profile_enabled("PS2_BATTLE_EFFECT_CLOCK");
    if (!enabled)
        return;

    // FVMCONTEXT: flags, contador WAIT e PC da instrucao seguinte.
    const uint32_t context = ee_phys(static_cast<uint32_t>(c.r[5].lo));
    if (!c.mem || context == 0 || context > 0x02000000u - 0x1b8u)
        return;

    const uint32_t address = context + 0x1b4u;
    const int32_t remaining = mrd<int32_t>(c, address);
    const uint32_t pc = mrd<uint32_t>(c, context + 0x1a0u);
    const bool waiting = remaining > 0 && (mrd<uint32_t>(c, context + 0x1b0u) & 1u);
    using EffectClock = std::chrono::steady_clock;
    using EffectTick = std::chrono::duration<int64_t, std::ratio<1, 60>>;
    const auto now = EffectClock::now();

    struct Clock
    {
        uint32_t context = 0, pc = 0;
        EffectClock::time_point deadline{}, last{};
        int32_t remaining = 0;
    };
    static Clock clocks[32];
    auto* clock = std::find_if(std::begin(clocks), std::end(clocks),
        [context](const Clock& candidate) { return candidate.context == context; });
    if (clock == std::end(clocks))
        clock = std::min_element(std::begin(clocks), std::end(clocks),
            [](const Clock& a, const Clock& b) { return a.last < b.last; });

    // A animacao continua durante quadros que atrasam o polling do script.
    // O WAIT usa o mesmo periodo de um campo e libera a proxima instrucao
    // no proprio callback em que vence, inclusive quando faltava um passo.
    if (!waiting)
    {
        if (clock->context == context)
            *clock = {};
        return;
    }
    if (clock->context != context || clock->pc != pc || remaining > clock->remaining)
    {
        *clock = {context, pc, now + std::chrono::ceil<EffectClock::duration>(EffectTick(remaining)),
                  now, remaining - 1};
        return;
    }
    clock->last = now;
    const unsigned target = now >= clock->deadline ? 0u :
        static_cast<unsigned>(std::min<int64_t>(remaining,
            std::chrono::ceil<EffectTick>(clock->deadline - now).count()));
    clock->remaining = target ? int32_t(target - 1) : 0;
    if (target < unsigned(remaining))
        mwr<uint32_t>(c, address, target);
}

static const bool g_vsync_catchup = battle_profile_enabled("PS2_VSYNC_CATCHUP");   // entrega VBLANKs atrasados (ate 4 por vez)
static std::atomic<uint64_t> g_timer0_mode{0}, g_timer0_comp{0};
static std::atomic<uint64_t> g_timer0_periods{0}, g_timer0_delivered{0};
static std::atomic<uint64_t> g_irq_wakes{0}, g_timer0_posts{0}, g_timer0_merged{0};
// O tique amostra a etapa do EE quando um sinal encontra o bit ocupado.
enum class Timer0EeStage : unsigned { Other, Vif0, Vif1, Gif, Vblank, Timer0, Count };
static const bool g_timer0_trace_stages = [] {
    const char* value = std::getenv("PS2_TIMER0_TRACE_STAGES");
    return value && std::strcmp(value, "1") == 0;
}();
static std::atomic<Timer0EeStage> g_timer0_ee_stage{Timer0EeStage::Other};
static std::atomic<uint64_t> g_timer0_merges_by_stage[(unsigned)Timer0EeStage::Count]{};

class Timer0StageScope
{
    Timer0EeStage previous = Timer0EeStage::Other;
public:
    explicit Timer0StageScope(Timer0EeStage stage)
    {
        if (g_timer0_trace_stages)
            previous = g_timer0_ee_stage.exchange(stage, std::memory_order_relaxed);
    }
    ~Timer0StageScope()
    {
        if (g_timer0_trace_stages)
            g_timer0_ee_stage.store(previous, std::memory_order_relaxed);
    }
};

static void timer0_count_merge()
{
    g_timer0_merged.fetch_add(1, std::memory_order_relaxed);
    if (g_timer0_trace_stages)
    {
        const unsigned stage = (unsigned)g_timer0_ee_stage.load(std::memory_order_relaxed);
        g_timer0_merges_by_stage[stage].fetch_add(1, std::memory_order_relaxed);
    }
}

static uint64_t g_timer0_ticks_replayed = 0; // So o EE atualiza e le.
static uint64_t g_timer0_dma_delivered = 0;  // So o EE atualiza e le este contador.
static const bool g_timer0_dma_poll = [] {
    const char* value = std::getenv("PS2_TIMER0_DMA_POLL");
    return value && std::strcmp(value, "1") == 0;
}();
static const bool g_gs_timer_clock = [] {
    const char* value = std::getenv("PS2_GS_TIMER_CLOCK");
    return !value || std::strcmp(value, "1") == 0;
}();

static const bool g_timer0_elapsed = g_gs_timer_clock && [] {
    const char* value = std::getenv("PS2_TIMER0_ELAPSED");
    return !value || std::strcmp(value, "1") == 0;
}();

// Bits CLKS do Timer 0. O caminho legado conserva a frequencia usada antes.
static uint64_t timer0_bus_hz(uint64_t mode)
{
    constexpr uint64_t bus_hz = 147456000ull;
    switch (mode & 3u)
    {
        case 0: return bus_hz;
        case 1: return bus_hz / 16u;
        case 2: return bus_hz / 256u;
        default: return 0; // HBlank requer eventos do GS.
    }
}

static uint64_t timer0_clock_hz(uint64_t mode)
{
    return g_gs_timer_clock ? timer0_bus_hz(mode) : 576000ull;
}

static std::atomic<bool> g_irq_wait_highres_active{false};
static const bool g_timer0_phase = [] {
    const char* value = std::getenv("PS2_TIMER0_PHASE");
    return !value || std::strcmp(value, "1") == 0;
}();
static uint32_t g_vsync_flag1 = 0;             // SetVSyncFlag: o kernel escreve 1 aqui a cada vsync
static uint32_t g_vsync_flag2 = 0;             // ... e o CSR aqui (64 bits)
static std::atomic<uint32_t> g_intc_mask{0};               // bit = causa habilitada por EnableIntc
static bool g_in_irq = false;

struct IntcHandler
{
    int id;
    uint32_t cause, fn, arg;
};

static std::vector<IntcHandler> g_intc_handlers;

// Histograma de leituras por registrador (tabela fixa: pode ser lida pelo vigia sem risco de queda).
static uint32_t g_hist_addr[512];
static uint64_t g_hist_cnt[512];

static void hw_hist_add(uint32_t phys)
{
    uint32_t h = (phys ^ (phys >> 9) ^ (phys >> 17)) & 511u;

    for (uint32_t i = 0; i < 512u; i++)
    {
        uint32_t slot = (h + i) & 511u;

        if (g_hist_cnt[slot] == 0 || g_hist_addr[slot] == phys)
        {
            g_hist_addr[slot] = phys;
            g_hist_cnt[slot]++;
            return;
        }
    }
}

static void hw_hist_print()
{
    std::fprintf(stderr, "registradores de hardware mais lidos:\n");

    for (int n = 0; n < 8; n++)
    {
        int best = -1;

        for (int i = 0; i < 512; i++)
        {
            if (g_hist_cnt[i] != 0 && (best < 0 || g_hist_cnt[i] > g_hist_cnt[best]))
                best = i;
        }

        if (best < 0)
            break;

        std::fprintf(stderr, "  0x%08X  %llu leituras\n", g_hist_addr[best], (unsigned long long)g_hist_cnt[best]);
        g_hist_cnt[best] = 0;   // so para a proxima escolha; o dump encerra o programa em seguida
    }
}

// Versao do histograma que nao zera nada: usada nos pulsos para ver, sem parar o jogo, quais
// registradores o laco atual esta lendo. Mostra a diferenca desde o pulso anterior.
static void hw_hist_peek(int top_n)
{
    static uint64_t s_prev[512] = {};
    uint64_t delta[512];

    for (int i = 0; i < 512; i++)
    {
        delta[i] = g_hist_cnt[i] >= s_prev[i] ? g_hist_cnt[i] - s_prev[i] : g_hist_cnt[i];
        s_prev[i] = g_hist_cnt[i];
    }

    std::fprintf(stderr, "    leituras de hardware neste intervalo:");

    bool any = false;

    for (int n = 0; n < top_n; n++)
    {
        int best = -1;

        for (int i = 0; i < 512; i++)
        {
            if (delta[i] != 0 && (best < 0 || delta[i] > delta[best]))
                best = i;
        }

        if (best < 0)
            break;

        std::fprintf(stderr, " 0x%08X x%llu", g_hist_addr[best], (unsigned long long)delta[best]);
        delta[best] = 0;
        any = true;
    }

    std::fprintf(stderr, any ? "\n" : " (nenhuma)\n");
}

// Relogio do host: os timers e o vsync andam com o tempo real, para os laÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¾ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¾ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¾ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¾ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â§os de espera terminarem.
static uint64_t hw_now_ns()
{
    static const auto start = std::chrono::steady_clock::now();
    return (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count();
}

static bool g_gs_vsint = false;
static uint32_t g_gs_field = 0;
static uint64_t g_gs_last_vsync = 0;

static void gs_vsync_update()
{
    const uint64_t period = 16683333ull;   // NTSC: ~59,94 Hz
    uint64_t now = hw_now_ns();

    while (now - g_gs_last_vsync >= period)
    {
        g_gs_last_vsync += period;
        g_gs_vsint = true;
        g_gs_field ^= 1u;
    }
}

// Memorias do VU0 (4 KB cada): micro memoria em 0x11000000 e dados em 0x11004000 (acessiveis pela CPU).
static uint8_t g_vu0_micro[4096];
static uint8_t g_vu0_data[4096];
static uint8_t g_vu1_micro[16384];
static uint8_t g_vu1_data[16384];
uint32_t g_vif1_top = 0, g_vif1_itop = 0;
static bool g_vu0_dirty = true, g_vu1_dirty = true;   // micro memoria mudou desde a ultima analise
// O EE e o MPG no DMA sincrono sao os dois caminhos de escrita.
static uint64_t g_vu1_micro_revision = 1;
static void vu1_micro_written()
{
    ++g_vu1_micro_revision;
    g_vu1_dirty = true;
}
static bool g_vu0_flags = true, g_vu1_flags = true;   // algum programa le MAC/status (FSAND, FMAND...)?

// Perfil (impresso a cada 2 s no resumo [gs]): tempo no DMA do GS/VIF, nos microprogramas e na janela.
// Opcoes de inicializacao: evita consultar o ambiente por vetor ou instrucao.
static const bool g_vif_wl0_fix = battle_profile_enabled("PS2_VIF_WL0_FIX");
static const bool g_vif1_reverse = battle_profile_enabled("PS2_VIF1_REVERSE_TEST");
static const bool g_vif1_readback = battle_profile_enabled("PS2_VIF1_READBACK_TEST");
static const bool g_ui_native = battle_profile_enabled("PS2_GS_UI_NATIVE");
static const bool g_vu_header_trace = std::getenv("PS2_VU_HEADER_TRACE") != nullptr;
static const bool g_vu_flag_test = battle_profile_enabled("PS2_VU_FLAG_TEST");
static const bool g_vu_count_trace = std::getenv("PS2_VU_COUNT_TRACE") != nullptr;
static const bool g_vu1_fast = battle_profile_enabled("PS2_VU1_FAST");
static const bool g_sprite_generic_rows = battle_profile_enabled("PS2_GS_SPRITE_GENERIC_ROWS");
static const bool g_sprite_wide_fast = [] {
    const char* value = std::getenv("PS2_GS_SPRITE_WIDE_FAST");
    return value == nullptr || std::strcmp(value, "0") != 0;
}();
// Permite comparar o decodificador especializado com o caminho anterior.
static const bool g_sprite_tex_decode = [] {
    const char* value = std::getenv("PS2_GS_SPRITE_TEX_DECODE");
    return value && std::strcmp(value, "0") != 0;
}();
static const bool g_sprite_columns = battle_profile_enabled("PS2_GS_SPRITE_COLUMNS");
// Normalizacao experimental antes da cobertura e interpolacao dos pixels.
static const bool g_field_raster = [] {
    const char* value = std::getenv("PS2_GS_FIELD_RASTER");
    return !value || std::strcmp(value, "1") == 0;
}();
static const bool g_field_raster_train = [] {
    const char* value = std::getenv("PS2_GS_FIELD_RASTER_TRAIN");
    return !value || std::strcmp(value, "1") == 0;
}();

static bool profile_enabled(const char* name, bool default_value = false)
{
    const char* value = std::getenv(name);
    return value ? std::strcmp(value, "0") != 0 : default_value;
}

static const bool g_perf_all = profile_enabled("PS2_PERF");
static const bool g_perf_gs = profile_enabled("PS2_PERF_GS", g_perf_all);
static const bool g_field_train_diag = profile_enabled("PS2_GS_FIELD_TRAIN_DIAG", g_perf_gs);
static const bool g_field_audit = [] {
    const char* value = std::getenv("PS2_GS_FIELD_AUDIT");
    return value != nullptr && std::strcmp(value, "1") == 0;
}();
static const bool g_gs_tri_texture_specialize = battle_profile_enabled("PS2_GS_TRI_TEXTURE_SPECIALIZE");
static const bool g_gs_tri_effects_fast = [] {
    const char* value = std::getenv("PS2_GS_TRI_EFFECTS_FAST");
    return value == nullptr || std::strcmp(value, "0") != 0;
}();
static const bool g_gs_tri_early_z = battle_profile_enabled("PS2_GS_TRI_EARLY_Z");
static const bool g_gs_tri_row_fast = [] {
    const char* value = std::getenv("PS2_GS_TRI_ROW_FAST");
    return !value || std::strcmp(value, "1") == 0;
}();
#if (defined(__SSE2__) || defined(_M_X64)) && !defined(__FMA__) && !defined(__FAST_MATH__)
static const bool g_gs_tri_rgba_sse2 = [] {
    const char* value = std::getenv("PS2_GS_TRI_RGBA_SSE2");
    return !value || std::strcmp(value, "1") == 0;
}();
#else
static constexpr bool g_gs_tri_rgba_sse2 = false;
#endif
static const bool g_perf_vif1 = profile_enabled("PS2_PERF_VIF1", g_perf_all);
static const bool g_vif1_unpack_fast = battle_profile_enabled("PS2_VIF1_UNPACK_FAST");
static const bool g_perf_cadence = profile_enabled("PS2_PERF_CADENCE", g_perf_all);
static const bool g_perf = g_perf_all || g_perf_cadence || g_perf_vif1 || g_perf_gs || g_field_audit ||
    profile_enabled("PS2_GS_PROFILE_IMAGE_QWORD");

static const bool g_gs_replay_capture_enabled = [] {
    const char* value = std::getenv("PS2_GS_VULKAN_CAPTURE");
    return value && std::strcmp(value, "1") == 0;
}();
static std::atomic<bool> g_gs_replay_request{false};
std::atomic<bool> g_cap_request{false};   // F9: grava o trace dos proximos vsyncs em gs_captura_N.txt (sem custo quando nao usado)
// A medicao por quadword e opcional: duas consultas ao relogio
// a cada 16 bytes podem dominar o custo de enfileirar a imagem.
static const bool g_prof_image_qword = profile_enabled("PS2_GS_PROFILE_IMAGE_QWORD", g_perf_gs);
// 1 preserva a medicao completa. Potencias de dois permitem
// escolher a amostra sem divisao no caminho de cada quadword.
static const unsigned g_prof_image_stride = [] {
    const char* value = std::getenv("PS2_GS_IMAGE_PROFILE_STRIDE");
    if (!value || std::strcmp(value, "2") == 0) return 2u;
    if (value && std::strcmp(value, "4") == 0) return 4u;
    return 1u;
}();
static uint64_t g_prof_image_samples = 0;

uint64_t g_prof_dma_ns = 0, g_prof_vu_ns = 0, g_prof_present_ns = 0, g_prof_img_ns = 0;
// O GS assincrono registra o tempo no worker e o EE le o perfil.
static std::atomic<uint64_t> g_prof_tri_ns{0}, g_prof_sprite_ns{0};
static std::atomic<uint64_t> g_prof_sprite_fast_pixels{0}, g_prof_sprite_generic_pixels{0};
static std::atomic<uint64_t> g_prof_sprite_z16_pixels{0};
static const bool g_sprite_z16_early = battle_profile_enabled("PS2_GS_SPRITE_Z16_EARLY");
static inline uint64_t prof_now()
{
    return (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Estas metricas pertencem ao EE. O escalonador so executa uma
// thread convidada por vez, inclusive durante o tratamento de IRQ.
struct CadenceEeProfile
{
    uint64_t idle_sleep_ns = 0, dma_vif1_ns = 0, dma_gif_ns = 0;
    unsigned idle_sleeps = 0, idle_vblanks = 0, batches = 0, multiple = 0, max_pending = 0;
    uint64_t dropped = 0;
};
static CadenceEeProfile g_cadence_ee;

// VIF1 e VU1 rodam dentro da mesma regiao de DMA protegida por g_gs_mu.
struct Vu1Profile
{
    uint64_t calls = 0, pairs = 0, ns = 0, kick_ns = 0;
};
static Vu1Profile g_prof_vu1[2048];
static uint64_t g_prof_kick_ns = 0;
static const char* const g_vu_aot_mode = std::getenv("PS2_VU1_AOT_CAPTURE");
static const bool g_vu_aot_capture = g_vu_aot_mode != nullptr;
static const bool g_vu_aot_revision_cache = [] {
    const char* value = std::getenv("PS2_VU1_AOT_REVISION_CACHE");
    return !value || std::strcmp(value, "1") == 0;
}();
static uint64_t g_prof_aot_revision_hits = 0;
static const bool g_vu_aot_enabled = battle_profile_enabled("PS2_VU1_AOT");
static const bool g_vu_aot_capture_0658_only = g_vu_aot_capture && std::strcmp(g_vu_aot_mode, "auto-0658") == 0;
static std::atomic<bool> g_vu_aot_armed{g_vu_aot_capture_0658_only ||
    (g_vu_aot_capture && std::strcmp(g_vu_aot_mode, "auto") == 0)};
static uint64_t g_prof_aot_pairs = 0, g_prof_aot_match = 0, g_prof_aot_mismatch = 0, g_prof_aot_missing = 0;
static uint64_t g_prof_aot_partial_pairs = 0;
static uint64_t g_prof_unpack_ns = 0, g_prof_unpack_calls = 0, g_prof_unpack_words = 0;

struct RasterProfScope
{
    std::atomic<uint64_t>& acc;
    uint64_t start;
    explicit RasterProfScope(std::atomic<uint64_t>& a) : acc(a), start(g_perf ? prof_now() : 0) {}
    ~RasterProfScope()
    {
        if (g_perf) acc.fetch_add(prof_now() - start, std::memory_order_relaxed);
    }
};

void vu0_run(Ctx& c, uint32_t byte_addr);
void vu1_run(uint32_t byte_addr);
uint32_t g_vu0_pc_get();
uint32_t g_vu1_pc_get();

static uint8_t* vu0_mem_ptr(uint32_t phys, int size)
{
    uint32_t off;
    uint8_t* base;

    if (phys >= 0x11000000u && phys < 0x11001000u) { base = g_vu0_micro; off = phys - 0x11000000u; }
    else if (phys >= 0x11004000u && phys < 0x11005000u) { base = g_vu0_data; off = phys - 0x11004000u; }
    else if (phys >= 0x11008000u && phys < 0x1100C000u) { base = g_vu1_micro; off = phys - 0x11008000u; }
    else if (phys >= 0x1100C000u && phys < 0x11010000u) { base = g_vu1_data; off = phys - 0x1100C000u; }
    else return nullptr;

    return off + (uint32_t)size <= (base == g_vu1_micro || base == g_vu1_data ? 16384u : 4096u) ? base + off : nullptr;
}

uint64_t ps2_hw_read(Ctx&, uint32_t phys, int size)
{
    if (uint8_t* vp = vu0_mem_ptr(phys, size))
    {
        uint64_t v = 0;
        std::memcpy(&v, vp, (size_t)size);
        return v;
    }

    hw_hist_add(phys);

    if (g_hw_log < 300)
    {
        g_hw_log++;
        std::fprintf(stderr, "hw leitura  0x%08X (%d bytes)\n", phys, size);
    }

    // Timer 0: modo (+0x10, com as flags EQUF/OVFF) e comparacao (+0x20). O handler do jogo faz
    // "ler, OU 0xC00, escrever"; se a leitura devolver 0, o CUE/CMPE sao apagados e o timer morre.
    if (phys == 0x10000010u)
        return g_timer0_mode;

    if (phys == 0x10000020u)
        return g_timer0_comp;

    // Timers 0 a 3 (contador em +0x00 de cada bloco de 0x800): 16 bits a ~576 kHz (BUSCLK/256).
    if (phys >= 0x10000000u && phys < 0x10002000u && (phys & 0x7FFu) == 0u)
        return (hw_now_ns() * 576ull / 1000000ull) & 0xFFFFull;

    // GS_CSR: FINISH (bit 1) sempre pronto (o GS emulado e instantaneo), VSINT (bit 3) a cada
    // quadro, FIELD (bit 13) alternando.
    // Vale para qualquer byte/palavra dentro dos 8 bytes do registrador (o jogo le o byte +1 para o FIELD).
    if (phys >= 0x12001000u && phys < 0x12001008u)
    {
        gs_vsync_update();

        uint64_t csr = 0x2ull | (g_gs_vsint ? 0x8ull : 0ull) | ((uint64_t)g_gs_field << 13);
        if (g_gs_timer_clock)
        {
            // Revisao e ID do GS selecionado. Shadow Hearts le REV no boot
            // para escolher o valor de comparacao do Timer 0.
            csr |= (0x1Bull << 16) | (0x55ull << 24);
        }
        uint64_t value = csr >> ((phys & 7u) * 8u);

        if (size < 8)
            value &= (1ull << (size * 8)) - 1ull;

        return value;
    }

    // Muitas leituras seguidas do mesmo registrador: o jogo espera por algo que nao vem.
    if (phys == g_poll_address)
    {
        if (++g_poll_count >= 300000u)
        {
            std::fprintf(stderr, "laco de espera: 0x%08X lido %u vezes seguidas\n", phys, g_poll_count);
            die();
        }
    }
    else
    {
        g_poll_address = phys;
        g_poll_count = 0;
    }

    return 0;
}

void ps2_dma_write(Ctx& c, uint32_t phys, uint64_t value);
static void irq_poll_timer0_at_dma(Ctx& c);

void ps2_hw_write(Ctx& c, uint32_t phys, uint64_t value, int size)
{
    if (uint8_t* vp = vu0_mem_ptr(phys, size))
    {
        std::memcpy(vp, &value, (size_t)size);

        if (vp >= g_vu0_micro && vp < g_vu0_micro + sizeof g_vu0_micro) g_vu0_dirty = true;
        else if (phys >= 0x11008000u && phys < 0x1100C000u) vu1_micro_written();

        return;
    }

    // STR em VIF1/GIF inicia trabalho sincrono no EE. O ponto anterior
    // ainda pode entregar um Timer 0 antes do trabalho; o posterior
    // executa depois de liberar o lock do GS e concluir a escrita.
    const bool check_timer0 = g_timer0_dma_poll && (value & 0x100u) &&
        (phys == 0x10009000u || phys == 0x1000A000u);
    if (check_timer0)
        irq_poll_timer0_at_dma(c);

    ps2_dma_write(c, phys, value);

    // GS_CSR: escrever 1 no bit 3 limpa o VSINT
    if (phys == 0x12001000u && (value & 0x8ull))
        g_gs_vsint = false;

    // Timer 0: modo (+0x10) e valor de comparacao (+0x20); a interrupcao sai quando o jogo liga CUE e CMPE
    if (phys == 0x10000010u)
    {
        // Bits de status sao limpos com escrita de 1. Preserve um evento
        // que a thread do relogio publique durante a escrita do EE.
        uint64_t mode = g_timer0_mode.load();
        while (!g_timer0_mode.compare_exchange_weak(mode,
            (value & ~0xC00ull) | (mode & 0xC00ull & ~value)))
        {
        }
    }

    if (phys == 0x10000020u)
        g_timer0_comp = value;

    if (g_hw_log < 300)
    {
        g_hw_log++;
        std::fprintf(stderr, "hw escrita  0x%08X (%d bytes) = 0x%llX\n", phys, size, (unsigned long long)value);
    }

    g_poll_address = 0;
    g_poll_count = 0;

    if (check_timer0)
        irq_poll_timer0_at_dma(c);
}

void ps2_bad_address(Ctx&, uint32_t address, int write)
{
    std::fprintf(stderr, "acesso de %s a endereco fora do mapa: 0x%08X\n", write ? "escrita" : "leitura", address);
    die();
}

// ---- kernel do EE: threads e semaforos ----
// Cada thread do PS2 e uma thread do sistema, mas so uma roda por vez (g_running).
// A troca acontece dentro de syscalls: esperar, dormir, sinalizar, mudar prioridade,
// iniciar ou terminar threads. Prioridade menor = mais urgente; empate = ordem de chegada.

namespace
{
    enum ThreadState { T_FREE, T_DORMANT, T_READY, T_RUNNING, T_WAIT_SLEEP, T_WAIT_SEMA, T_SUSPENDED };

    struct ThreadExit {};   // desenrola a pilha do host quando a thread termina sozinha
    struct ThreadDead {};   // idem, quando outra thread a terminou (TerminateThread)

    struct EeThread
    {
        ThreadState state = T_FREE;
        uint32_t entry = 0;
        uint32_t stack = 0;
        uint32_t stack_size = 0;
        uint32_t gp = 0;
        uint32_t attr = 0;
        uint32_t option = 0;
        int init_prio = 0;
        int prio = 0;
        uint32_t arg = 0;
        int wakeup_count = 0;
        int wait_sema = 0;
        bool suspended = false;
        bool delete_on_exit = false;
        uint64_t ready_seq = 0;
        unsigned gen = 0;            // muda quando a thread e terminada: invalida a thread do sistema antiga
        Ctx* ctx = nullptr;          // a thread principal usa o Ctx de quem chamou a primeira syscall
        std::unique_ptr<Ctx> owned;
    };

    struct EeSema
    {
        bool used = false;
        int count = 0;
        int max_count = 1;
        int init_count = 0;
        uint32_t attr = 0;
        uint32_t option = 0;
        std::deque<int> waiters;
    };

    const int KE_UNKNOWN_THID = -406;
    const int KE_UNKNOWN_SEMID = -407;
    const int KE_NOT_DORMANT = -413;
    const int KE_SEMA_ZERO = -419;
    const int KE_SEMA_OVF = -420;

    std::mutex g_mu;
    std::condition_variable g_cv;
    std::deque<EeThread> g_threads;
    std::deque<EeSema> g_semas;
    int g_running = 0;
    uint64_t g_seq = 0;
    int g_dispatch_disabled = 0;
    bool g_kernel_ready = false;

    EeThread& thr(int id) { return g_threads[(size_t)id]; }
    EeSema& sem(int id) { return g_semas[(size_t)id]; }

    bool valid_thread(int id) { return id > 0 && (size_t)id < g_threads.size() && g_threads[(size_t)id].state != T_FREE; }
    bool valid_sema(int id) { return id > 0 && (size_t)id < g_semas.size() && g_semas[(size_t)id].used; }

    void kernel_init(Ctx& c)
    {
        if (g_kernel_ready)
            return;

        g_kernel_ready = true;
        g_threads.emplace_back();    // indice 0 nao e usado
        g_semas.emplace_back();
        g_threads.emplace_back();    // 1 = thread principal

        EeThread& t = g_threads[1];
        t.state = T_RUNNING;
        t.ctx = &c;
        t.ready_seq = ++g_seq;
        g_running = 1;
    }

    const char* state_name(ThreadState s)
    {
        switch (s)
        {
            case T_READY: return "pronta";
            case T_RUNNING: return "rodando";
            case T_WAIT_SLEEP: return "dormindo (SleepThread)";
            case T_WAIT_SEMA: return "esperando semaforo";
            case T_SUSPENDED: return "suspensa";
            default: return "?";
        }
    }

    // Estado de cada thread (usada no deadlock e quando o usuario aperta Ctrl+C).
    void print_threads()
    {
        for (size_t i = 1; i < g_threads.size(); i++)
        {
            EeThread& t = g_threads[i];

            if (t.state == T_FREE || t.state == T_DORMANT)
                continue;

            const Ps2FuncEntry* e = find_entry(t.entry);

            std::fprintf(stderr, "  thread %zu (prio %d, entrada %s): %s",
                i, t.prio, i == 1 ? "principal" : (e ? e->name : "?"), state_name(t.state));

            if (t.state == T_WAIT_SEMA)
                std::fprintf(stderr, " %d", t.wait_sema);

            std::fprintf(stderr, "\n");
        }

        std::fprintf(stderr, "  semaforos em uso:\n");

        for (size_t i = 1; i < g_semas.size(); i++)
        {
            const EeSema& s = g_semas[i];

            if (!s.used)
                continue;

            std::fprintf(stderr, "    sema %zu: contador=%d max=%d inicial=%d esperando=%zu\n",
                i, s.count, s.max_count, s.init_count, s.waiters.size());
        }
    }

    [[noreturn]] void deadlock()
    {
        std::fprintf(stderr, "deadlock: nenhuma thread pode rodar (esperando interrupcao, vsync ou timer?)\n");
        print_threads();
        die();
    }

    // Thread pronta (ou rodando) mais urgente; 0 se nao ha nenhuma.
    int pick_next()
    {
        int best = 0;

        for (size_t i = 1; i < g_threads.size(); i++)
        {
            EeThread& t = g_threads[i];

            if (t.state != T_READY && t.state != T_RUNNING)
                continue;

            if (best == 0 ||
                t.prio < g_threads[(size_t)best].prio ||
                (t.prio == g_threads[(size_t)best].prio && t.ready_seq < g_threads[(size_t)best].ready_seq))
            {
                best = (int)i;
            }
        }

        return best;
    }

    // Passa a vez para `next` e espera ate ser escolhida de novo.
    void switch_to(std::unique_lock<std::mutex>& lk, int me, int next)
    {
        unsigned my_gen = g_threads[(size_t)me].gen;

        thr(next).state = T_RUNNING;
        g_running = next;
        g_cv.notify_all();

        g_cv.wait(lk, [&] {
            return g_threads[(size_t)me].gen != my_gen || g_running == me;
        });

        if (g_threads[(size_t)me].gen != my_gen)
            throw ThreadDead();
    }

    void make_ready(int id)
    {
        EeThread& t = thr(id);

        t.state = t.suspended ? T_SUSPENDED : T_READY;
        t.ready_seq = ++g_seq;
    }

    // Uma thread mais urgente ficou pronta: ela toma a vez agora.
    void maybe_preempt(std::unique_lock<std::mutex>& lk, int me)
    {
        if (g_dispatch_disabled > 0)
            return;

        int best = pick_next();

        if (best != 0 && best != me)
        {
            thr(me).state = T_READY;
            switch_to(lk, me, best);
        }
    }

    // A thread atual nao pode continuar (esta esperando): escolhe outra.
    void block(std::unique_lock<std::mutex>& lk, int me, ThreadState wait_state)
    {
        thr(me).state = wait_state;

        int next = pick_next();

        // Todas as threads esperam: o tempo continua passando e as interrupcoes (vsync, timer 0)
        // podem acordar alguem. Entrega-as ate alguma thread ficar pronta (ate ~3 s).
        for (int spins = 0; next == 0 && spins < 3000; spins++)
        {
            lk.unlock();
            const uint64_t idle_start = g_perf_cadence ? prof_now() : 0;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            if (g_perf_cadence)
            {
                ++g_cadence_ee.idle_sleeps;
                g_cadence_ee.idle_sleep_ns += prof_now() - idle_start;
            }

            if (thr(me).ctx)
                ps2_irq_idle(*thr(me).ctx);

            lk.lock();
            next = pick_next();
        }

        if (next == 0)
            deadlock();

        switch_to(lk, me, next);
    }

    // A thread terminou: entrega a vez a outra e deixa a thread do sistema acabar.
    void finish_thread(std::unique_lock<std::mutex>&, int me)
    {
        EeThread& t = thr(me);

        t.state = t.delete_on_exit ? T_FREE : T_DORMANT;
        t.gen++;

        int next = pick_next();

        if (next == 0)
            deadlock();

        thr(next).state = T_RUNNING;
        g_running = next;
        g_cv.notify_all();
    }

    void thread_main(int id, unsigned gen)
    {
        try
        {
            {
                std::unique_lock<std::mutex> lk(g_mu);

                g_cv.wait(lk, [&] {
                    return g_threads[(size_t)id].gen != gen || g_running == id;
                });

                if (g_threads[(size_t)id].gen != gen)
                    return;
            }

            ps2_call(*g_threads[(size_t)id].ctx, g_threads[(size_t)id].entry);
        }
        catch (const ThreadDead&)
        {
            return;
        }
        catch (const ThreadExit&)
        {
        }

        std::unique_lock<std::mutex> lk(g_mu);
        finish_thread(lk, id);
    }

    int create_thread(Ctx& c, uint32_t param)
    {
        // struct ee_thread_t: status, func, stack, stack_size, gp, initial_priority, current_priority, attr, option
        int id = 0;

        for (size_t i = 2; i < g_threads.size(); i++)
        {
            if (g_threads[i].state == T_FREE)
            {
                id = (int)i;
                break;
            }
        }

        if (id == 0)
        {
            g_threads.emplace_back();
            id = (int)g_threads.size() - 1;
        }

        EeThread& t = thr(id);

        t.state = T_DORMANT;
        t.entry = mrd<uint32_t>(c, param + 0x04);
        t.stack = mrd<uint32_t>(c, param + 0x08);
        t.stack_size = mrd<uint32_t>(c, param + 0x0C);
        t.gp = mrd<uint32_t>(c, param + 0x10);
        t.init_prio = (int)mrd<uint32_t>(c, param + 0x14);
        t.prio = t.init_prio;
        t.attr = mrd<uint32_t>(c, param + 0x1C);
        t.option = mrd<uint32_t>(c, param + 0x20);
        t.wakeup_count = 0;
        t.suspended = false;
        t.delete_on_exit = false;

        const Ps2FuncEntry* e = find_entry(t.entry);

        std::fprintf(stderr, "  CreateThread id=%d entrada=0x%08X (%s) prio=%d pilha=0x%08X+0x%X\n",
            id, t.entry, e ? e->name : "?", t.init_prio, t.stack, t.stack_size);

        return id;
    }

    int start_thread(Ctx& c, std::unique_lock<std::mutex>& lk, int me, int id, uint32_t arg)
    {
        if (!valid_thread(id))
            return KE_UNKNOWN_THID;

        EeThread& t = thr(id);

        if (t.state != T_DORMANT)
            return KE_NOT_DORMANT;

        t.arg = arg;
        t.prio = t.init_prio;
        t.wakeup_count = 0;
        t.suspended = false;
        t.delete_on_exit = false;

        t.owned.reset(new Ctx());
        t.ctx = t.owned.get();
        t.ctx->mem = c.mem;
        t.ctx->spr = c.spr;
        vu0_init(*t.ctx);
        t.ctx->r[28].lo = t.gp;
        t.ctx->r[29].lo = S32(t.stack + t.stack_size - 16u);
        t.ctx->r[4].lo = arg;

        make_ready(id);

        std::thread(thread_main, id, t.gen).detach();

        maybe_preempt(lk, me);

        return id;
    }

    unsigned g_sema_log = 0;

    // Os stubs de syscall sao folhas (JR $ra), entao $ra ainda aponta para quem chamou.
    const char* caller_name(Ctx& c)
    {
        const Ps2FuncEntry* e = find_containing((uint32_t)c.r[31].lo);
        return e ? e->name : "?";
    }

    int create_sema(Ctx& c, uint32_t param)
    {
        // struct ee_sema_t: count, max_count, init_count, wait_threads, attr, option
        int id = 0;

        for (size_t i = 1; i < g_semas.size(); i++)
        {
            if (!g_semas[i].used)
            {
                id = (int)i;
                break;
            }
        }

        if (id == 0)
        {
            g_semas.emplace_back();
            id = (int)g_semas.size() - 1;
        }

        EeSema& s = sem(id);

        s.used = true;
        s.max_count = (int)mrd<uint32_t>(c, param + 0x04);
        s.init_count = (int)mrd<uint32_t>(c, param + 0x08);
        s.count = s.init_count;
        s.attr = mrd<uint32_t>(c, param + 0x10);
        s.option = mrd<uint32_t>(c, param + 0x14);
        s.waiters.clear();

        std::fprintf(stderr, "  CreateSema id=%d init=%d max=%d attr=%u  (por %s)\n",
            id, s.init_count, s.max_count, s.attr, caller_name(c));

        return id;
    }

    // Quem acorda primeiro: por prioridade (atributo SA_THPRI) ou por ordem de chegada.
    int take_waiter(EeSema& s)
    {
        size_t pick = 0;

        if (s.attr & 1u)
        {
            for (size_t i = 1; i < s.waiters.size(); i++)
            {
                if (thr(s.waiters[i]).prio < thr(s.waiters[pick]).prio)
                    pick = i;
            }
        }

        int id = s.waiters[pick];
        s.waiters.erase(s.waiters.begin() + (long)pick);
        return id;
    }

    int signal_sema(std::unique_lock<std::mutex>& lk, int me, int id, bool from_interrupt)
    {
        if (!valid_sema(id))
            return KE_UNKNOWN_SEMID;

        EeSema& s = sem(id);

        if (!s.waiters.empty())
        {
            make_ready(take_waiter(s));

            if (!from_interrupt)
                maybe_preempt(lk, me);

            return id;
        }

        if (s.count >= s.max_count)
            return KE_SEMA_OVF;

        s.count++;
        return id;
    }

    int wait_sema(std::unique_lock<std::mutex>& lk, int me, int id)
    {
        if (!valid_sema(id))
            return KE_UNKNOWN_SEMID;

        EeSema& s = sem(id);

        if (s.count > 0)
        {
            s.count--;
            return id;
        }

        s.waiters.push_back(me);
        thr(me).wait_sema = id;
        block(lk, me, T_WAIT_SEMA);

        return id;   // signal_sema entregou o semaforo diretamente
    }

    void remove_from_waiters(int thread_id)
    {
        for (size_t i = 1; i < g_semas.size(); i++)
        {
            std::deque<int>& w = g_semas[i].waiters;

            for (size_t k = 0; k < w.size(); k++)
            {
                if (w[k] == thread_id)
                {
                    w.erase(w.begin() + (long)k);
                    break;
                }
            }
        }
    }

    int status_code(const EeThread& t)
    {
        switch (t.state)
        {
            case T_RUNNING: return 0x01;
            case T_READY: return 0x02;
            case T_WAIT_SLEEP: case T_WAIT_SEMA: return t.suspended ? 0x0C : 0x04;
            case T_SUSPENDED: return 0x08;
            default: return 0x10;
        }
    }

    // Todas as syscalls de thread e semaforo.
    void kernel_syscall(Ctx& c, int number)
    {
        std::unique_lock<std::mutex> lk(g_mu);

        kernel_init(c);

        int me = g_running;
        int a0 = (int)(int32_t)c.r[4].lo;
        int a1 = (int)(int32_t)c.r[5].lo;
        int result = 0;

        switch (number)
        {
            case 32:   // CreateThread(param)
                result = create_thread(c, (uint32_t)c.r[4].lo);
                break;

            case 33:   // DeleteThread(id)
                if (!valid_thread(a0)) { result = KE_UNKNOWN_THID; break; }
                if (thr(a0).state != T_DORMANT) { result = KE_NOT_DORMANT; break; }
                thr(a0).state = T_FREE;
                result = a0;
                break;

            case 34:   // StartThread(id, arg)
                result = start_thread(c, lk, me, a0, (uint32_t)c.r[5].lo);
                break;

            case 35: case 36:   // ExitThread, ExitDeleteThread
                if (me == 1)
                {
                    std::fprintf(stderr, "a thread principal chamou ExitThread: encerrando\n[ROJ_SESSION_CLOSED_NORMALLY]\n");
                    std::fflush(stderr);
                    std::exit(0);
                }

                thr(me).delete_on_exit = number == 36;
                throw ThreadExit();

            case 37: case -38:   // TerminateThread(id)
            {
                if (!valid_thread(a0)) { result = KE_UNKNOWN_THID; break; }

                if (a0 == me)
                {
                    thr(me).delete_on_exit = false;
                    throw ThreadExit();
                }

                EeThread& t = thr(a0);

                if (t.state == T_DORMANT) { result = KE_NOT_DORMANT; break; }

                remove_from_waiters(a0);
                t.gen++;                  // a thread do sistema antiga acorda e se desfaz sozinha
                t.state = T_DORMANT;
                g_cv.notify_all();
                result = a0;
                break;
            }

            case 39:   // DisableDispatchThread
                g_dispatch_disabled = 1;
                break;

            case 40:   // EnableDispatchThread
                g_dispatch_disabled = 0;
                maybe_preempt(lk, me);
                break;

            case 41: case -42:   // ChangeThreadPriority(id, prio): devolve a anterior
            {
                int id = a0 == 0 ? me : a0;

                if (!valid_thread(id)) { result = KE_UNKNOWN_THID; break; }

                result = thr(id).prio;
                thr(id).prio = a1;

                if (number == 41)
                    maybe_preempt(lk, me);

                break;
            }

            case 43: case -44:   // RotateThreadReadyQueue(prio): a primeira da fila vai para o fim
            {
                int prio = a0 == 0 ? thr(me).prio : a0;
                int head = 0;

                for (size_t i = 1; i < g_threads.size(); i++)
                {
                    EeThread& t = g_threads[i];

                    if ((t.state != T_READY && t.state != T_RUNNING) || t.prio != prio)
                        continue;

                    if (head == 0 || t.ready_seq < g_threads[(size_t)head].ready_seq)
                        head = (int)i;
                }

                if (head != 0)
                    thr(head).ready_seq = ++g_seq;

                result = prio;

                if (number == 43)
                    maybe_preempt(lk, me);

                break;
            }

            case 45: case -46:   // ReleaseWaitThread(id)
            {
                if (!valid_thread(a0)) { result = KE_UNKNOWN_THID; break; }

                EeThread& t = thr(a0);

                if (t.state == T_WAIT_SLEEP || t.state == T_WAIT_SEMA)
                {
                    remove_from_waiters(a0);
                    make_ready(a0);

                    if (number == 45)
                        maybe_preempt(lk, me);
                }

                result = a0;
                break;
            }

            case 47:   // GetThreadId
                result = me;
                break;

            case 48: case -49:   // ReferThreadStatus(id, ptr)
            {
                int id = a0 == 0 ? me : a0;

                if (!valid_thread(id)) { result = KE_UNKNOWN_THID; break; }

                EeThread& t = thr(id);
                uint32_t p = (uint32_t)c.r[5].lo;

                if (p != 0)
                {
                    mwr<uint32_t>(c, p + 0x00, (uint32_t)status_code(t));
                    mwr<uint32_t>(c, p + 0x04, t.entry);
                    mwr<uint32_t>(c, p + 0x08, t.stack);
                    mwr<uint32_t>(c, p + 0x0C, t.stack_size);
                    mwr<uint32_t>(c, p + 0x10, t.gp);
                    mwr<uint32_t>(c, p + 0x14, (uint32_t)t.init_prio);
                    mwr<uint32_t>(c, p + 0x18, (uint32_t)t.prio);
                    mwr<uint32_t>(c, p + 0x1C, t.attr);
                    mwr<uint32_t>(c, p + 0x20, t.option);
                }

                result = id;
                break;
            }

            case 50:   // SleepThread
                if (thr(me).wakeup_count > 0)
                    thr(me).wakeup_count--;
                else
                    block(lk, me, T_WAIT_SLEEP);

                break;

            case 51: case -52:   // WakeupThread(id) / iWakeupThread
            {
                if (!valid_thread(a0)) { result = KE_UNKNOWN_THID; break; }

                EeThread& t = thr(a0);

                if (t.state == T_WAIT_SLEEP)
                {
                    make_ready(a0);

                    if (number == 51)
                        maybe_preempt(lk, me);
                }
                else
                {
                    t.wakeup_count++;
                }

                result = a0;
                break;
            }

            case 53: case -54:   // CancelWakeupThread(id): devolve o contador e zera
                if (!valid_thread(a0)) { result = KE_UNKNOWN_THID; break; }
                result = thr(a0).wakeup_count;
                thr(a0).wakeup_count = 0;
                break;

            case 55: case -56:   // SuspendThread(id)
            {
                int id = a0 == 0 ? me : a0;

                if (!valid_thread(id)) { result = KE_UNKNOWN_THID; break; }

                EeThread& t = thr(id);
                t.suspended = true;

                if (t.state == T_READY || t.state == T_RUNNING)
                {
                    if (id == me)
                        block(lk, me, T_SUSPENDED);
                    else
                        t.state = T_SUSPENDED;
                }

                result = id;
                break;
            }

            case 57: case -58:   // ResumeThread(id)
            {
                if (!valid_thread(a0)) { result = KE_UNKNOWN_THID; break; }

                EeThread& t = thr(a0);
                t.suspended = false;

                if (t.state == T_SUSPENDED)
                {
                    make_ready(a0);

                    if (number == 57)
                        maybe_preempt(lk, me);
                }

                result = a0;
                break;
            }

            case 64:   // CreateSema(param)
                result = create_sema(c, (uint32_t)c.r[4].lo);
                break;

            case 65:   // DeleteSema(id)
                if (!valid_sema(a0)) { result = KE_UNKNOWN_SEMID; break; }
                sem(a0).used = false;
                sem(a0).waiters.clear();
                result = a0;
                break;

            case 66: case -67:   // SignalSema(id) / iSignalSema
                if (g_sema_log < 400)
                {
                    g_sema_log++;
                    std::fprintf(stderr, "  [t%d] %s id=%d  (por %s)\n", me,
                        number < 0 ? "iSignalSema" : "SignalSema", a0, caller_name(c));
                }

                result = signal_sema(lk, me, a0, number < 0);
                break;

            case 68:   // WaitSema(id)
                if (g_sema_log < 400)
                {
                    g_sema_log++;
                    std::fprintf(stderr, "  [t%d] WaitSema id=%d contador=%d  (por %s)\n", me, a0,
                        valid_sema(a0) ? sem(a0).count : -1, caller_name(c));
                }

                result = wait_sema(lk, me, a0);
                break;

            case 69: case -70:   // PollSema(id)
                if (!valid_sema(a0)) { result = KE_UNKNOWN_SEMID; break; }

                if (sem(a0).count > 0)
                {
                    sem(a0).count--;
                    result = a0;
                }
                else
                {
                    result = KE_SEMA_ZERO;
                }

                break;

            case 71: case -72:   // ReferSemaStatus(id, ptr)
            {
                if (!valid_sema(a0)) { result = KE_UNKNOWN_SEMID; break; }

                EeSema& s = sem(a0);
                uint32_t p = (uint32_t)c.r[5].lo;

                if (p != 0)
                {
                    mwr<uint32_t>(c, p + 0x00, (uint32_t)s.count);
                    mwr<uint32_t>(c, p + 0x04, (uint32_t)s.max_count);
                    mwr<uint32_t>(c, p + 0x08, (uint32_t)s.init_count);
                    mwr<uint32_t>(c, p + 0x0C, (uint32_t)s.waiters.size());
                    mwr<uint32_t>(c, p + 0x10, s.attr);
                    mwr<uint32_t>(c, p + 0x14, s.option);
                }

                result = a0;
                break;
            }

            default:
                break;
        }

        c.r[2].lo = (uint64_t)(int64_t)result;
    }
}

// ---- syscalls do kernel do EE ----

static uint32_t g_stack_base = 0x01F80000u;
static uint32_t g_heap_start = 0;
static uint32_t g_heap_end = 0;
static uint32_t g_gs_crt_int = 0, g_gs_crt_ffmd = 0;   // SetGsCrt: SMODE2.INT e SMODE2.FFMD
static uint32_t g_next_id = 1;
static uint64_t g_gs_imr = 0;
static unsigned g_syscall_log = 0;
static int g_last_syscall = 0x7FFFFFFF;
static unsigned g_syscall_repeat = 0;
static uint32_t g_user_syscall[512];   // funcoes instaladas por SetSyscall, indice = numero + 256

static std::atomic<uint64_t> g_activity(0);   // sobe a cada syscall; o vigia olha se parou
volatile uint32_t g_dbg_dma[6] = {};   // onde o EE esta dentro do DMA (pulso): canal, indice da tag, TADR, id, QWC, ativo
volatile uint32_t g_dbg_vu[3] = {};    // microprograma do VU1 em execucao: endereco, passos, ativo
volatile uint32_t g_loop_mark = 0;             // ultimo rotulo de desvio executado (so com PS2_TRACE)

static void print_loop_mark()
{
    uint32_t m = g_loop_mark;

    if (m == 0)
        return;

    const Ps2FuncEntry* e = find_containing(m);
    std::fprintf(stderr, "ultimo ponto de desvio executado: 0x%08X  (%s+0x%X)\n", m, e ? e->name : "?", e ? m - e->address : 0u);
}

void ps2_syscall(Ctx& c)
{
    g_activity++;

    int number = (int)(int16_t)c.r[3].lo;

    // Log das primeiras syscalls (a sequencia de inicializacao). Repeticoes seguidas da mesma
    // syscall viram uma linha so; muitas repeticoes sem nada no meio sao um laco: para com o traco.
    if (number == g_last_syscall)
    {
        if (++g_syscall_repeat == 300000u)
        {
            std::fprintf(stderr, "laco de syscall: %d (%s) chamada %u vezes seguidas na thread %d\n",
                number, ps2_syscall_name(number), g_syscall_repeat, g_kernel_ready ? g_running : 1);
            die();
        }
    }
    else
    {
        if (g_syscall_repeat > 0 && g_syscall_log < 400)
            std::fprintf(stderr, "  (a mesma syscall se repetiu mais %u vezes)\n", g_syscall_repeat);

        g_syscall_repeat = 0;
        g_last_syscall = number;

        if (g_syscall_log < 400)
        {
            g_syscall_log++;
            std::fprintf(stderr, "[t%d] syscall %d (%s)\n", g_kernel_ready ? g_running : 1, number, ps2_syscall_name(number));
        }
    }

    // Syscall instalado pelo proprio programa (SetSyscall): e uma chamada normal da funcao.
    if (number >= -256 && number < 256 && g_user_syscall[number + 256] != 0)
    {
        ps2_call(c, g_user_syscall[number + 256]);
        return;
    }

    switch (number)
    {
        case 116:  // SetSyscall(numero, funcao): devolve a funcao anterior
        {
            int target = (int)(int32_t)c.r[4].lo;
            uint32_t function = (uint32_t)c.r[5].lo;

            std::fprintf(stderr, "  SetSyscall numero=%d funcao=0x%08X\n", target, function);

            if (target >= -256 && target < 256)
            {
                c.r[2].lo = g_user_syscall[target + 256];
                g_user_syscall[target + 256] = function;
            }
            else
            {
                c.r[2].lo = 0;
            }

            return;
        }

        case 60:   // SetupThread(gp, stack, stack_size, args, root_func): devolve o novo $sp
        {
            uint32_t stack = (uint32_t)c.r[5].lo;
            uint32_t size = (uint32_t)c.r[6].lo;

            if (stack == 0xFFFFFFFFu) stack = 0x02000000u - size;

            g_stack_base = stack;
            c.r[28].lo = c.r[4].lo;
            c.r[2].lo = S32(stack + size - 0x2A0u);
            return;
        }

        case 61:   // InitHeap(heap_start, heap_size): tamanho -1 = ate a base da pilha
        {
            uint32_t start = (uint32_t)c.r[4].lo;
            uint32_t size = (uint32_t)c.r[5].lo;

            g_heap_start = start;
            g_heap_end = size == 0xFFFFFFFFu ? g_stack_base : start + size;
            c.r[2].lo = start;
            return;
        }

        case 62:   // EndOfHeap
            c.r[2].lo = g_heap_end != 0 ? g_heap_end : g_stack_base;
            return;

        case 2:    // SetGsCrt(interlaced, mode, ffmd): por enquanto so registra
            std::fprintf(stderr, "  SetGsCrt interlace=%u modo=%u ffmd=%u\n",
                (unsigned)c.r[4].lo, (unsigned)c.r[5].lo, (unsigned)c.r[6].lo);
            g_gs_crt_int = (uint32_t)c.r[4].lo & 1u;
            g_gs_crt_ffmd = (uint32_t)c.r[6].lo & 1u;
            c.r[2].lo = 0;
            return;

        case 16:   // AddIntcHandler(causa, handler, next, arg): guarda o handler para a entrega
        {
            IntcHandler h;
            h.id = (int)g_next_id++;
            h.cause = (uint32_t)c.r[4].lo;
            h.fn = (uint32_t)c.r[5].lo;
            h.arg = (uint32_t)c.r[7].lo;

            std::fprintf(stderr, "  AddIntcHandler causa=%u handler=0x%08X arg=0x%08X (id %d)\n", h.cause, h.fn, h.arg, h.id);

            if (h.cause < 32)
                g_intc_handlers.push_back(h);

            c.r[2].lo = (uint64_t)h.id;
            return;
        }

        case 18:   // AddDmacHandler(canal, handler, next, arg)
            std::fprintf(stderr, "  AddDmacHandler canal=%u handler=0x%08X\n", (unsigned)c.r[4].lo, (unsigned)c.r[5].lo);
            c.r[2].lo = g_next_id++;
            return;

        case 17:   // RemoveIntcHandler(causa, id)
        {
            int id = (int)c.r[5].lo;

            for (size_t i = 0; i < g_intc_handlers.size(); i++)
            {
                if (g_intc_handlers[i].id == id)
                {
                    g_intc_handlers.erase(g_intc_handlers.begin() + (long)i);
                    break;
                }
            }

            c.r[2].lo = 0;
            return;
        }

        case 20: case -26:   // _EnableIntc(causa)
            g_intc_mask |= 1u << ((uint32_t)c.r[4].lo & 31u);
            c.r[2].lo = 1;
            return;

        case 21: case -27:   // _DisableIntc(causa)
            g_intc_mask &= ~(1u << ((uint32_t)c.r[4].lo & 31u));
            c.r[2].lo = 1;
            return;

        case 115:   // SetVSyncFlag(flag1, flag2): o kernel atualiza os dois a cada vsync
            g_vsync_flag1 = (uint32_t)c.r[4].lo;
            g_vsync_flag2 = (uint32_t)c.r[5].lo;
            std::fprintf(stderr, "  SetVSyncFlag flag1=0x%08X flag2=0x%08X\n", g_vsync_flag1, g_vsync_flag2);
            c.r[2].lo = 0;
            return;

        case 19:                  // RemoveDmacHandler
        case 22: case 23:         // _EnableDmac, _DisableDmac
        case -28: case -29:       // versoes i
        case 92: case 93: case 94: case 95:       // Enable/Disable*Handler
        case -92: case -93: case -94: case -95:
            c.r[2].lo = 0;
            return;

        case 113: case -113:   // GsPutIMR(imr): guarda a mascara de interrupcao do GS, devolve a anterior
        {
            uint64_t old = g_gs_imr;
            g_gs_imr = c.r[4].lo;
            c.r[2].lo = old;
            return;
        }

        case 112: case -112:   // GsGetIMR
            c.r[2].lo = g_gs_imr;
            return;

        // threads e semaforos: ver kernel_syscall
        case 32: case 33: case 34: case 35: case 36: case 37: case -38:
        case 39: case 40: case 41: case -42: case 43: case -44: case 45: case -46:
        case 47: case 48: case -49: case 50: case 51: case -52: case 53: case -54:
        case 55: case -56: case 57: case -58:
        case 64: case 65: case 66: case -67: case 68: case 69: case -70: case 71: case -72:
            kernel_syscall(c, number);
            return;

        case 97: case 98: case 100: case -104:   // EnableCache, DisableCache, FlushCache, iFlushCache
        case 124:                                // Deci2Call (depuracao)
            c.r[2].lo = 0;
            return;

        case 127:  // GetMemorySize
            c.r[2].lo = 0x02000000ull;
            return;

        default:
            std::fprintf(stderr, "syscall %d (%s) ainda nao implementada\n", number, ps2_syscall_name(number));
            die();
    }
}

// Texto C do jogo (ate capacity-1 caracteres), para registrar nomes de arquivos e afins.
const char* ps2_cstr(Ctx& c, uint32_t address, char* buffer, size_t capacity)
{
    size_t n = 0;

    while (n + 1 < capacity)
    {
        char ch = (char)mrd<uint8_t>(c, address + (uint32_t)n);

        if (ch == 0)
            break;

        buffer[n++] = ch;
    }

    buffer[n] = 0;
    return buffer;
}

// Ids unicos e nao nulos (modulos do IOP, handlers...).
uint32_t ps2_alloc_id()
{
    return g_next_id++;
}

// Funcao do SDK que o runtime ainda nao implementa: para com o nome e os argumentos.
[[noreturn]] void ps2_hle_unimplemented(Ctx& c, const char* name)
{
    std::fprintf(stderr, "HLE: %s foi chamada e ainda nao esta implementada (a0=0x%08X a1=0x%08X a2=0x%08X a3=0x%08X)\n",
        name, (unsigned)c.r[4].lo, (unsigned)c.r[5].lo, (unsigned)c.r[6].lo, (unsigned)c.r[7].lo);
    die();
}

// ---- disco: ISO9660 e API do libcdvd ----

namespace
{
    std::ifstream g_iso;
    uint64_t g_iso_sectors = 0;
    uint32_t g_iso_root_lsn = 0;
    uint32_t g_iso_root_size = 0;

    uint32_t g_cd_error = 0;
    uint32_t g_cd_last_bytes = 0;
    uint32_t g_cd_stream_lsn = 0;
    unsigned g_cd_log = 0;

    uint32_t le32(const uint8_t* p)
    {
        return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
    }

    void iso_required()
    {
        if (!g_iso.is_open())
        {
            std::fprintf(stderr, "o jogo acessou o disco, mas a ISO nao foi aberta (use: ps2_game.exe caminho.iso)\n");
            die();
        }
    }

    // Le `count` setores de 2048 bytes a partir de `lsn`; false se passar do fim da ISO.
    bool iso_read_sectors(uint32_t lsn, uint32_t count, void* dest)
    {
        iso_required();

        g_iso.clear();
        g_iso.seekg((std::streamoff)lsn * 2048);
        g_iso.read((char*)dest, (std::streamsize)count * 2048);

        return (uint64_t)g_iso.gcount() == (uint64_t)count * 2048;
    }

    uint8_t* host_ptr(Ctx& c, uint32_t addr, size_t len)
    {
        if ((addr >> 28) == 7)
        {
            uint32_t off = addr & 0x3FFFu;

            if ((size_t)off + len <= 0x4000)
                return c.spr + off;

            ps2_bad_address(c, addr, 1);
        }

        uint32_t p = ee_phys(addr);

        if ((uint64_t)p + len <= 0x02000000ull)
            return c.mem + p;

        ps2_bad_address(c, addr, 1);
    }

    std::string strip_version(std::string name)
    {
        size_t semi = name.find(';');

        if (semi != std::string::npos)
            name.erase(semi);

        for (size_t i = 0; i < name.size(); i++)
            name[i] = (char)std::toupper((unsigned char)name[i]);

        return name;
    }

    bool iso_dir_find(uint32_t dir_lsn, uint32_t dir_size, const std::string& component,
                      uint32_t* lsn, uint32_t* size, bool* is_dir)
    {
        std::vector<uint8_t> dir(((size_t)dir_size + 2047) & ~(size_t)2047);

        if (dir.empty() || !iso_read_sectors(dir_lsn, (uint32_t)(dir.size() / 2048), dir.data()))
            return false;

        std::string want = strip_version(component);
        size_t pos = 0;

        while (pos + 34 <= dir_size)
        {
            uint8_t len = dir[pos];

            if (len == 0)
            {
                pos = (pos / 2048 + 1) * 2048;   // os registros nao atravessam setores
                continue;
            }

            uint8_t name_len = dir[pos + 32];
            const uint8_t* name = &dir[pos + 33];

            bool special = name_len == 1 && (name[0] == 0 || name[0] == 1);   // "." e ".."

            if (!special && strip_version(std::string((const char*)name, name_len)) == want)
            {
                *lsn = le32(&dir[pos + 2]);
                *size = le32(&dir[pos + 10]);
                *is_dir = (dir[pos + 25] & 2) != 0;
                return true;
            }

            pos += len;
        }

        return false;
    }

    // Caminhos como "cdrom0:\MODULES\X.IRX;1", "\MOVIE.PKB;1" ou "movie.pkb".
    bool iso_lookup(const std::string& path_in, uint32_t* lsn, uint32_t* size, bool* is_dir)
    {
        std::string path = path_in;
        size_t colon = path.find(':');

        if (colon != std::string::npos)
            path = path.substr(colon + 1);

        std::replace(path.begin(), path.end(), '/', '\\');

        uint32_t cur_lsn = g_iso_root_lsn;
        uint32_t cur_size = g_iso_root_size;
        bool cur_dir = true;
        size_t pos = 0;

        while (pos < path.size())
        {
            size_t next = path.find('\\', pos);
            std::string component = path.substr(pos, next == std::string::npos ? std::string::npos : next - pos);
            pos = next == std::string::npos ? path.size() : next + 1;

            if (component.empty())
                continue;

            if (!cur_dir || !iso_dir_find(cur_lsn, cur_size, component, &cur_lsn, &cur_size, &cur_dir))
                return false;
        }

        *lsn = cur_lsn;
        *size = cur_size;
        *is_dir = cur_dir;
        return true;
    }
}

static std::filesystem::path g_game_directory;
#ifdef _WIN32
static std::filesystem::path ps2_windows_executable_path();
static bool ps2_windows_replace_file(const std::filesystem::path& source, const std::filesystem::path& target);
#endif

static std::filesystem::path ps2_executable_directory(const char* argv0)
{
    namespace fs = std::filesystem;
#ifdef _WIN32
    const fs::path executable = ps2_windows_executable_path();
#else
    std::error_code link_error;
    const fs::path executable = fs::read_symlink("/proc/self/exe", link_error);
#endif
    if (!executable.empty())
        return executable.parent_path();

    std::error_code error;
    const fs::path fallback = fs::absolute(argv0 && *argv0 ? fs::u8path(argv0) : fs::path("."), error);
    return error ? fs::path(".") : (argv0 && *argv0 ? fallback.parent_path() : fallback);
}

static bool ps2_iso_open_path(const std::filesystem::path& file)
{
    const std::string path = file.u8string();
    std::ifstream f(file, std::ios::binary);

    if (!f.is_open())
        return false;

    uint8_t pvd[2048];

    f.seekg(16 * 2048);
    f.read((char*)pvd, sizeof(pvd));

    if (f.gcount() != (std::streamsize)sizeof(pvd) || pvd[0] != 1 || std::memcmp(pvd + 1, "CD001", 5) != 0)
    {
        std::fprintf(stderr, "%s nao parece uma ISO9660\n", path.c_str());
        return false;
    }

    f.clear();
    f.seekg(0, std::ios::end);

    g_iso_sectors = (uint64_t)f.tellg() / 2048;
    g_iso_root_lsn = le32(pvd + 156 + 2);
    g_iso_root_size = le32(pvd + 156 + 10);

    g_iso = std::move(f);

    std::fprintf(stderr, "ISO: %s (%llu setores)\n", path.c_str(), (unsigned long long)g_iso_sectors);
    return true;
}

bool ps2_iso_open(const char* path)
{
    return ps2_iso_open_path(std::filesystem::u8path(path));
}


// Ctrl+C: mostra o que o jogo estava fazendo (util quando ele fica preso em um laco).
void ps2_install_crash_filter();   // filtro de excecoes do Windows (definido no fim do arquivo)
void ps2_begin_crash_log();
void ps2_win_tune();               // resolucao do timer e anti-throttling do Windows (definido no fim do arquivo)

static void on_interrupt(int)
{
    std::fprintf(stderr, "\ninterrompido (Ctrl+C). Threads:\n");
    print_threads();
    print_loop_mark();
    hw_hist_print();
    dump_trace();
    std::fflush(stderr);
    std::_Exit(130);
}

// Queda do programa (acesso invalido, divisao por zero, instrucao ilegal no host): mostra onde.
static void on_crash(int sig)
{
    static volatile int in_crash = 0;

    if (in_crash)
        std::_Exit(4);

    in_crash = 1;
    ps2_begin_crash_log();

    const char* name = sig == SIGSEGV ? "SIGSEGV (acesso invalido a memoria)"
        : sig == SIGFPE ? "SIGFPE (divisao por zero ou erro aritmetico)"
        : sig == SIGILL ? "SIGILL (instrucao ilegal)" : "sinal";

    std::fprintf(stderr, "\nQUEDA DO PROGRAMA: %s (sinal %d). Threads e ultimas funcoes do jogo:\n", name, sig);
    print_threads();
    print_loop_mark();
    hw_hist_print();
    dump_trace();
    std::fflush(stderr);
    std::_Exit(4);
}

// Vigia: se nenhuma syscall acontece por muito tempo, o jogo esta preso num laco de codigo (sem
// chamar o kernel). Mostra onde e termina. PS2_WATCHDOG=segundos (padrao 20; 0 desliga).
static void irq_debug_print();   // definida junto das interrupcoes

static void watchdog_thread(unsigned seconds)
{
    uint64_t last = g_activity.load();
    unsigned idle = 0;
    unsigned elapsed = 0;

    const char* p = std::getenv("PS2_PULSE");
    unsigned pulse = p ? (unsigned)std::atoi(p) : 5u;   // segundos entre "pulsos de vida" (0 desliga)

    for (;;)
    {
        std::this_thread::sleep_for(std::chrono::seconds(1));
        elapsed++;

        uint64_t now = g_activity.load();

        if (pulse != 0 && elapsed % pulse == 0)
        {
#ifdef PS2_TRACE
            uint32_t top = g_ring_pos == 0 ? 0u : g_ring[(g_ring_pos - 1) & 63u];
            const Ps2FuncEntry* e = top ? find_entry(top) : nullptr;
            std::fprintf(stderr, "  [pulso %us] syscalls=%llu funcoes chamadas=%u ultima=%s marca=0x%08X\n", elapsed,
                (unsigned long long)now, g_ring_pos, e ? e->name : "?", g_loop_mark);
            hw_hist_peek(6);
            std::fprintf(stderr, "    [onde] dma: %s canal=%u tag#=%u tadr=0x%08X id=%u qwc=%u | vu1: %s prog=0x%X passos=%u\n",
                g_dbg_dma[5] ? "DENTRO" : "fora", g_dbg_dma[0], g_dbg_dma[1], g_dbg_dma[2], g_dbg_dma[3], g_dbg_dma[4],
                g_dbg_vu[2] ? "RODANDO" : "parado", g_dbg_vu[0], g_dbg_vu[1]);
#else
            std::fprintf(stderr, "  [pulso %us] syscalls=%llu\n", elapsed, (unsigned long long)now);
#endif

            // PS2_PULSE_DETAIL=1: a cada pulso mostra o estado de cada thread, os semaforos e as
            // ultimas 64 funcoes chamadas (para ver o que o laco principal faz quando "parece parado").
            if (std::getenv("PS2_PULSE_DETAIL"))
            {
                print_threads();
                dump_trace();
            }
        }

        if (now != last)
        {
            last = now;
            idle = 0;
            continue;
        }

        if (++idle >= seconds)
        {
            std::fprintf(stderr, "\nVIGIA: nenhuma syscall ha %u segundos; o jogo esta preso em um laco de codigo. Threads e ultimas funcoes:\n", idle);
            print_threads();
            irq_debug_print();
            print_loop_mark();
            hw_hist_print();
            dump_trace();
            std::fflush(stderr);
            std::_Exit(3);
        }
    }
}

// Agenda comparacoes do Timer 0 no relogio monotono do host. Em modo
// fixo, um despertar atrasado nao desloca o prazo da proxima comparacao.
struct Timer0Schedule
{
    uint64_t next = 0;
    uint64_t last_period = 0;

    uint64_t poll(uint64_t now, uint64_t period, bool fixed_phase)
    {
        if (period == 0)
        {
            next = last_period = 0;
            return 0;
        }
        if (next == 0 || (fixed_phase && period != last_period))
        {
            last_period = period;
            next = now + period;
            return fixed_phase ? 0 : 1;
        }
        if (now < next)
            return 0;

        const uint64_t elapsed = 1 + (now - next) / period;
        next = fixed_phase ? next + elapsed * period : now + period;
        return elapsed;
    }
};

// Tique de interrupcoes: VBLANK a ~59,94 Hz e timer 0 (quando o jogo o liga) a cada 1 ms.
// Implementacao Win32 junto com as outras APIs de janela.
static void irq_wait_tick();

static uint32_t irq_add_pending(uint32_t pending, uint32_t vblanks, uint64_t timer_events)
{
    const uint32_t count = std::min((pending >> irq_timer0_bits) + vblanks, irq_vblank_max);
    const uint32_t old_timer = pending & irq_timer0_mask;
    const uint32_t timer = g_timer0_elapsed
        ? old_timer + (uint32_t)std::min<uint64_t>(timer_events, irq_timer0_mask - old_timer)
        : old_timer | (timer_events != 0 ? 1u : 0u);
    return (count << irq_timer0_bits) | timer;
}

static void irq_tick_thread()
{
    const uint64_t period = 16683333ull;
    uint64_t next_vsync = hw_now_ns() + period;
    Timer0Schedule timer0;

    for (;;)
    {
        irq_wait_tick();
        g_irq_wakes.fetch_add(1, std::memory_order_relaxed);

        uint64_t now = hw_now_ns();
        uint32_t vblanks = 0;
        if (now >= next_vsync)
        {
            const uint64_t elapsed = 1 + (now - next_vsync) / period;
            next_vsync += elapsed * period;
            g_vsync_gen.fetch_add(elapsed, std::memory_order_relaxed);
            vblanks = (uint32_t)std::min<uint64_t>(elapsed, irq_vblank_max);
        }

        // CUE (bit 7), CMPE (bit 8) e causa 9 habilitados.
        const uint64_t mode = g_timer0_mode.load();
        const uint64_t compare = g_timer0_comp.load() & 0xFFFFull;
        const bool timer0_enabled = (mode & 0x180ull) == 0x180ull &&
            (g_intc_mask.load() & (1u << 9));
        const uint64_t clock_hz = timer0_clock_hz(mode);
        const uint64_t period_ns = timer0_enabled && clock_hz
            ? compare * 1000000000ull / clock_hz : 0;
        const uint64_t periods = timer0.poll(now, period_ns, g_timer0_phase);
        if (periods != 0)
        {
            g_timer0_periods.fetch_add(periods, std::memory_order_relaxed);
            g_timer0_mode.fetch_or(0x400ull);
        }

        if (vblanks || periods)
        {
            uint32_t pending = g_irq_pending.load(std::memory_order_relaxed);
            uint32_t next;
            do
            {
                next = irq_add_pending(pending, vblanks, periods);
            }
            while (!g_irq_pending.compare_exchange_weak(pending, next,
                std::memory_order_release, std::memory_order_relaxed));
            if (periods)
            {
                // O CAS deixa em pending o estado imediatamente anterior a publicacao.
                g_timer0_posts.fetch_add(1, std::memory_order_relaxed);
                if (pending & irq_timer0_mask)
                    timer0_count_merge();
            }
        }
    }
}

// ---- entrada configuravel no launcher: teclado, XInput e roteiro de diagnostico ----
//   Teclado (so com a janela do jogo em foco): Enter=START, Backspace=SELECT, setas=direcional, Z=cruz, X=bola,
//            C=quadrado, V=triangulo, Q=L1, E=R1, 1=L2, 3=R2, WASD=analogico esquerdo (com diagonais), IJKL=analogico direito.
//   PS2_PAD_SCRIPT="START@10+0.2,CROSS@14+0.2": BOTAO@segundos+duracao (segundos desde o inicio do programa).
//   Botoes: SELECT L3 R3 START UP RIGHT DOWN LEFT L2 R2 L1 R1 TRIANGLE CIRCLE CROSS SQUARE.
void ps2_window_start();          // janela Win32 (definida no fim do arquivo; vazia fora do Windows)
bool ps2_key_down(int vk);        // tecla apertada (so conta com a janela do jogo em foco)
bool ps2_xinput_state(uint16_t* buttons, uint8_t* lt, uint8_t* rt, int16_t* sticks);   // controle XInput (Xbox e compativeis); false se nao houver

namespace
{
    struct PadButton { const char* name; uint16_t bit; };

    const PadButton k_pad_buttons[] =
    {
        { "SELECT", 0x0100 }, { "L3", 0x0200 }, { "R3", 0x0400 }, { "START", 0x0800 },
        { "UP", 0x1000 }, { "RIGHT", 0x2000 }, { "DOWN", 0x4000 }, { "LEFT", 0x8000 },
        { "L2", 0x0001 }, { "R2", 0x0002 }, { "L1", 0x0004 }, { "R1", 0x0008 },
        { "TRIANGLE", 0x0010 }, { "CIRCLE", 0x0020 }, { "CROSS", 0x0040 }, { "SQUARE", 0x0080 }
    };

    struct PadScriptEntry { uint16_t bit; double start; double end; };

    std::vector<PadScriptEntry> pad_parse_script(const char* text)
    {
        std::vector<PadScriptEntry> out;

        if (!text)
            return out;

        std::string s(text);
        size_t pos = 0;

        while (pos < s.size())
        {
            size_t next = s.find_first_of(",;", pos);
            std::string tok = s.substr(pos, next == std::string::npos ? std::string::npos : next - pos);
            pos = next == std::string::npos ? s.size() : next + 1;

            size_t at = tok.find('@');

            if (at == std::string::npos)
                continue;

            std::string name = tok.substr(0, at);

            for (char& ch : name)
                ch = (char)std::toupper((unsigned char)ch);

            double start = std::atof(tok.c_str() + at + 1);
            size_t plus = tok.find('+', at);
            double dur = plus == std::string::npos ? 0.2 : std::atof(tok.c_str() + plus + 1);

            for (const PadButton& b : k_pad_buttons)
            {
                if (name == b.name)
                {
                    out.push_back({ b.bit, start, start + (dur > 0.0 ? dur : 0.2) });
                    break;
                }
            }
        }

        return out;
    }
}

// Ative somente ao comparar o teclado com os pacotes lidos pelo jogo.
static std::atomic<bool> g_fmv_start_pressed{false};
static const bool g_pad_read_trace = [] {
    const char* value = std::getenv("PS2_PAD_READ_TRACE");
    return value && std::strcmp(value, "1") == 0;
}();

static void pad_input_thread()
{
    const std::vector<PadScriptEntry> script = pad_parse_script(std::getenv("PS2_PAD_SCRIPT"));
    const auto t0 = std::chrono::steady_clock::now();
    uint16_t last = 0;

    for (;;)
    {
        uint16_t pressed = 0;
        double now = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();

        for (const PadScriptEntry& e : script)
        {
            if (now >= e.start && now < e.end)
                pressed |= e.bit;
        }

        uint16_t gp_buttons = 0;
        uint8_t gp_lt = 0, gp_rt = 0;
        int16_t gp_sticks[4] = {};
        const bool has_gamepad = ps2_xinput_state(&gp_buttons, &gp_lt, &gp_rt, gp_sticks);

        const auto mapped = roj::map_input(ps2_settings().input, ps2_key_down, has_gamepad,
            gp_buttons, gp_lt, gp_rt, gp_sticks);
        pressed |= mapped.pressed;

        if (pressed != last)
        {
            std::fprintf(stderr, "  [pad] botoes = 0x%04X\n", (unsigned)pressed);
            last = pressed;
        }

        const uint8_t lx = mapped.lx, ly = mapped.ly, rx = mapped.rx, ry = mapped.ry;

        static uint32_t last_sticks = 0x80808080u;
        const uint32_t sticks = ((uint32_t)lx << 24) | ((uint32_t)ly << 16) | ((uint32_t)rx << 8) | ry;

        if (sticks != last_sticks)
        {
            if (g_pad_read_trace)
                std::fprintf(stderr, "  [pad fonte] t_ms=%llu esq=(%02X,%02X) dir=(%02X,%02X)\n",
                    (unsigned long long)(hw_now_ns() / 1000000u), lx, ly, rx, ry);
            else
                std::fprintf(stderr, "  [pad] analogico esq=(%02X,%02X) dir=(%02X,%02X)\n", lx, ly, rx, ry);
            last_sticks = sticks;
        }

        {
            static bool f9_last = false;
            const bool f9 = ps2_key_down(0x78) && !roj::input_uses_key(ps2_settings().input, 0x78);

            if (f9 && !f9_last)
            {
                if (g_gs_replay_capture_enabled)
                    g_gs_replay_request = true;
                else
                    g_cap_request = true;
            }

            f9_last = f9;
            if (g_vu_aot_capture && ps2_key_down(0x77) && !roj::input_uses_key(ps2_settings().input, 0x77) && !g_vu_aot_armed.exchange(true))
                std::fprintf(stderr, "  [VU1 AOT] F8: capturando as proximas chamadas em 0x0350, 0x0078 e 0x0658\n");
        }

        ps2_pad_set_state(0, (uint16_t)~pressed, rx, ry, lx, ly);
        g_fmv_start_pressed.store((pressed & 0x0800u) != 0, std::memory_order_relaxed);
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}

// Procura a ISO: argumento, PS2_ISO, Data junto do executavel, depois os caminhos antigos.
// (Aqui tambem liga o tratamento de Ctrl+C, porque e chamada no inicio do programa.)
namespace { int gs_replay_cpu_1x(const char* path); }

void ps2_iso_open_default(int argc, char** argv)
{
    if (const char* replay = std::getenv("PS2_GS_REPLAY_1X"); replay && *replay)
        std::exit(gs_replay_cpu_1x(replay));

    g_game_directory = ps2_executable_directory(argc > 0 ? argv[0] : nullptr);
    std::signal(SIGINT, on_interrupt);
    std::signal(SIGSEGV, on_crash);
    std::signal(SIGABRT, on_crash);
    ps2_install_crash_filter();
    ps2_win_tune();
    std::signal(SIGFPE, on_crash);
    std::signal(SIGILL, on_crash);

    std::thread(irq_tick_thread).detach();
    std::thread(pad_input_thread).detach();
    ps2_window_start();

    {
        const char* w = std::getenv("PS2_WATCHDOG");
        unsigned seconds = w ? (unsigned)std::atoi(w) : 20u;

        if (seconds > 0)
            std::thread(watchdog_thread, seconds).detach();
    }

    if (argc > 1 && ps2_iso_open(argv[1]))
        return;
    if (const char* iso = std::getenv("PS2_ISO"); iso && *iso && ps2_iso_open(iso))
        return;
    if (ps2_iso_open_path(g_game_directory / "Data" / "Shadow Hearts (USA).iso"))
        return;

    const char* candidates[] =
    {
        "Shadow Hearts (USA).iso",
        "..\\Shadow Hearts (USA).iso",
        "..\\..\\Shadow Hearts (USA).iso",
        "../Shadow Hearts (USA).iso",
        "../../Shadow Hearts (USA).iso"
    };

    for (size_t i = 0; i < sizeof(candidates) / sizeof(candidates[0]); i++)
    {
        if (candidates[i] && ps2_iso_open(candidates[i]))
            return;
    }

    std::fprintf(stderr, "ISO nao encontrada (use Data/Shadow Hearts (USA).iso ou informe o caminho)\n");
}

// libcdvd: argumentos em a0..a3 (c.r[4..7]), retorno em v0. As leituras sao feitas na hora:
// quando o jogo pergunta (sceCdSync), ja terminaram.
void ps2_cd(Ctx& c, int function)
{
    uint32_t a0 = (uint32_t)c.r[4].lo;
    uint32_t a1 = (uint32_t)c.r[5].lo;
    uint32_t a2 = (uint32_t)c.r[6].lo;
    uint32_t a3 = (uint32_t)c.r[7].lo;
    uint64_t result = 0;

    switch (function)
    {
        case PS2_CD_INIT: case PS2_CD_MMODE: case PS2_CD_BREAK:
        case PS2_CD_ST_INIT: case PS2_CD_ST_STOP:
            result = 1;
            break;

        case PS2_CD_DISK_READY:     // SCECdComplete
            result = 2;
            break;

        case PS2_CD_GET_DISK_TYPE:  // SCECdPS2DVD
            result = 0x14;
            break;

        case PS2_CD_SYNC:           // nada em andamento
            result = 0;
            break;

        case PS2_CD_STATUS:         // SCECdStatPause: disco parado e pronto
            result = 0x0A;
            break;

        case PS2_CD_GET_ERROR:
            result = g_cd_error;
            break;

        case PS2_CD_GET_READ_POS:
            result = g_cd_last_bytes;
            break;

        case PS2_CD_READ:           // sceCdRead(lsn, setores, destino, modo)
        {
            if (g_cd_log < 80)
            {
                g_cd_log++;
                std::fprintf(stderr, "  sceCdRead lsn=%u setores=%u destino=0x%08X\n", a0, a1, a2);
            }

            uint8_t* dest = host_ptr(c, a2, (size_t)a1 * 2048);

            if (iso_read_sectors(a0, a1, dest))
            {
                g_cd_error = 0;
            }
            else
            {
                g_cd_error = 0x30;   // erro de leitura (generico)
                std::fprintf(stderr, "  sceCdRead: leitura fora da ISO (lsn=%u setores=%u)\n", a0, a1);
            }

            g_cd_last_bytes = a1 * 2048;
            result = 1;
            break;
        }

        case PS2_CD_SEARCH_FILE:    // sceCdSearchFile(sceCdlFILE*, nome): lsn, tamanho, nome[16], data[8]
        {
            char name[256];
            ps2_cstr(c, a1, name, sizeof(name));

            uint32_t lsn = 0, size = 0;
            bool is_dir = false;
            bool found = a0 != 0 && (iso_required(), iso_lookup(name, &lsn, &size, &is_dir)) && !is_dir;

            std::fprintf(stderr, "  sceCdSearchFile(\"%s\") -> ", name);

            if (found)
            {
                std::fprintf(stderr, "lsn %u tamanho %u\n", lsn, size);

                mwr<uint32_t>(c, a0 + 0, lsn);
                mwr<uint32_t>(c, a0 + 4, size);

                std::string last = name;
                size_t slash = last.find_last_of("\\/:");

                if (slash != std::string::npos)
                    last = last.substr(slash + 1);

                for (uint32_t i = 0; i < 16; i++)
                    mwr<uint8_t>(c, a0 + 8 + i, i < last.size() && i < 15 ? (uint8_t)last[i] : 0);
            }
            else
            {
                std::fprintf(stderr, "nao encontrado\n");
            }

            result = found ? 1 : 0;
            break;
        }

        case PS2_CD_ST_START:       // sceCdStStart(lsn, modo)
            g_cd_stream_lsn = a0;
            result = 1;
            break;

        case PS2_CD_ST_READ:        // sceCdStRead(setores, destino, modo, erro*): devolve os setores lidos
        {
            uint8_t* dest = host_ptr(c, a1, (size_t)a0 * 2048);

            if (iso_read_sectors(g_cd_stream_lsn, a0, dest))
            {
                g_cd_stream_lsn += a0;
                result = a0;
            }
            else
            {
                std::fprintf(stderr, "  sceCdStRead: leitura fora da ISO (lsn=%u)\n", g_cd_stream_lsn);
                result = 0;
            }

            if (a3 != 0)
                mwr<uint32_t>(c, a3, 0);

            break;
        }

        default:
            break;
    }

    c.r[2].lo = result;
}

// ---- RPC do SIF: os servicos do IOP viram codigo nativo ----

namespace
{
    typedef void (*RpcServiceFn)(Ctx& c, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize);

    struct RpcService
    {
        uint32_t sid;
        const char* name;
        RpcServiceFn fn;
    };

    unsigned g_rpc_log = 0;

    // Registra a chamada e as primeiras palavras do que o jogo enviou (para descobrir o protocolo).
    void rpc_log(Ctx& c, const char* service, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize)
    {
        if (g_rpc_log >= 400)
            return;

        g_rpc_log++;

        std::fprintf(stderr, "  RPC %s comando=%u enviar=0x%08X (%u bytes) receber=0x%08X (%u bytes)\n",
            service, cmd, send, ssize, recv, rsize);

        // so as palavras diferentes de zero (o buffer do FIO tem 664 bytes, quase todo zerado)
        if (send != 0 && ssize > 0 && ssize <= 2048)
        {
            unsigned shown = 0;

            for (uint32_t off = 0; off + 4 <= ssize && shown < 48; off += 4)
            {
                uint32_t value = mrd<uint32_t>(c, send + off);

                if (value == 0)
                    continue;

                if (shown == 0)
                    std::fprintf(stderr, "    nao nulos:");

                std::fprintf(stderr, " +%X=%08X", off, value);
                shown++;
            }

            if (shown > 0)
                std::fprintf(stderr, "\n");
        }
    }

    // ---- servidores RPC do lado do EE ----
    // O jogo registra uma funcao com sceSifRegisterRpc e entra no sceSifRpcLoop. Quem chama essa
    // funcao e o IOP; aqui os servicos nativos (como o RSSD) enfileiram os pedidos com
    // ps2_ee_rpc_post e o sceSifRpcLoop os entrega, na thread que esta no laco.
    struct EeRpcServer
    {
        uint32_t sd, sid, func, buff, cfunc, cbuff, queue;
    };

    struct EeRpcRequest
    {
        uint32_t sid, fno;
        std::vector<uint8_t> data;
    };

    std::vector<EeRpcServer> g_ee_rpc_servers;
    std::deque<EeRpcRequest> g_ee_rpc_queue;

    // Pedido que sceSifGetNextRequest entregou e sceSifExecRequest ainda vai executar.
    bool g_ee_exec_valid = false;
    EeRpcRequest g_ee_exec_req;
    EeRpcServer g_ee_exec_server;

    const EeRpcServer* ee_rpc_find(uint32_t sid)
    {
        for (size_t i = 0; i < g_ee_rpc_servers.size(); i++)
        {
            if (g_ee_rpc_servers[i].sid == sid)
                return &g_ee_rpc_servers[i];
        }

        return nullptr;
    }
    int g_rpc_loop_thread = 0;   // thread dentro do sceSifRpcLoop (0 = ainda nao entrou)

    // "O IOP chama o servidor `sid` do EE com o comando `fno` e esses dados."
    void ee_rpc_post(Ctx& c, uint32_t sid, uint32_t fno, const void* data, uint32_t size)
    {
        EeRpcRequest r;
        r.sid = sid;
        r.fno = fno;
        r.data.assign((const uint8_t*)data, (const uint8_t*)data + size);
        g_ee_rpc_queue.push_back(r);

        std::fprintf(stderr, "  IOP -> EE: servidor 0x%08X comando=0x%X (%u bytes) na fila\n", sid, fno, size);

        // se a thread do servidor ja esta no laco, ela pode estar dormindo: acorda
        if (g_rpc_loop_thread != 0)
        {
            c.r[4].lo = (uint64_t)g_rpc_loop_thread;
            c.r[3].lo = 51;   // WakeupThread
            ps2_syscall(c);
        }
    }

    void rpc_zero_recv(Ctx& c, uint32_t recv, uint32_t rsize)
    {
        if (recv == 0 || rsize == 0 || rsize > 0x10000u)
            return;

        for (uint32_t i = 0; i < rsize; i++)
            mwr<uint8_t>(c, recv + i, 0);
    }

    // FIO.IRX: leitura de arquivos do jogo. Por enquanto so registra as chamadas.
    uint32_t g_fio_last_hash = 0;
    unsigned g_fio_repeat = 0;

    // Procura na pilha enderecos de retorno plausiveis (palavra logo depois de um JAL/JALR)
    // para mostrar quem chamou a funcao do jogo que esta no laco.
    void print_stack_callers(Ctx& c)
    {
        uint32_t sp = (uint32_t)c.r[29].lo;
        unsigned shown = 0;

        std::fprintf(stderr, "possiveis chamadores (varredura da pilha, do mais proximo para o mais distante):\n");

        for (uint32_t off = 0; off < 0x1000 && shown < 16; off += 4)
        {
            uint32_t w = mrd<uint32_t>(c, sp + off);

            if (w < 0x00200000u || w >= 0x004D7000u || (w & 3u) != 0)
                continue;

            uint32_t ins = mrd<uint32_t>(c, w - 8);
            bool is_call = (ins >> 26) == 3u || ((ins >> 26) == 0u && (ins & 0x3Fu) == 9u);

            if (!is_call)
                continue;

            const Ps2FuncEntry* e = find_containing(w);

            std::fprintf(stderr, "  sp+0x%03X  0x%08X  %s+0x%X\n", off, w, e ? e->name : "?", e ? w - e->address : 0u);
            shown++;
        }
    }

    void fio_service(Ctx& c, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize)
    {
        // impressao digital da chamada: comando + conteudo enviado
        uint32_t h = 2166136261u ^ cmd;

        for (uint32_t off = 0; send != 0 && off + 4 <= ssize && off < 2048; off += 4)
            h = (h ^ mrd<uint32_t>(c, send + off)) * 16777619u;

        if (h == g_fio_last_hash)
        {
            g_fio_repeat++;
        }
        else
        {
            g_fio_last_hash = h;
            g_fio_repeat = 0;
        }

        // Comandos de consulta (4 getStatus, 12 strDataCh, 20 CdSync, 21 GetError) sao repetidos o
        // tempo todo pelas threads do jogo: so os 3 primeiros de cada um aparecem no log, para nao
        // gastar o limite de linhas do rpc_log nem esconder os comandos que importam.
        // Blocos VAG validos tambem chegam continuamente; limite o log desse trafego.
        const bool vag_block = cmd == 18u && send != 0 && ssize == 0x1000u && recv != 0 && rsize == 0x20u;
        bool polling = vag_block || cmd == 4u || cmd == 12u || cmd == 20u || cmd == 21u;
        static unsigned s_poll_logged[32] = {};

        if (polling)
        {
            unsigned& n = s_poll_logged[cmd & 31u];

            if (n < 3u)
            {
                n++;
                rpc_log(c, "FIO", cmd, send, ssize, recv, rsize);
            }
            else if (n == 3u)
            {
                n++;
                std::fprintf(stderr, "  (FIO comando=%u e consulta de rotina; as proximas nao serao impressas)\n", cmd);
            }
        }
        else if (g_fio_repeat < 2)
        {
            rpc_log(c, "FIO", cmd, send, ssize, recv, rsize);
        }
        // (o protocolo abaixo foi deduzido do disassembly de FileIOLib::read/CdSync/GetError)
        else if (g_fio_repeat == 2)
        {
            std::fprintf(stderr, "  (FIO comando=%u: mesma chamada repetida; as proximas iguais nao serao impressas)\n", cmd);
        }

        // Resposta padrao: 32 bytes zerados = "sem erro, nada ocupado".
        //   comando 1  (open)    : o jogo ignora a resposta.
        //   comando 20 (CdSync)  : palavra 3 da resposta != 0 significa "ainda ocupado".
        //   comando 21 (GetErr)  : palavra 3 da resposta e o codigo de erro (0 = nenhum).
        rpc_zero_recv(c, recv, rsize);

        // FileIOLib::strVagDebugData envia um bloco de 4 KiB e le a palavra 3.
        // PlayStreamADPCM so avanca o contador e libera o bloco quando esse retorno
        // e positivo. Consuma o bloco em silencio enquanto o IOP VAG nao tem mixer.
        if (vag_block)
        {
            mwr<uint32_t>(c, recv + 0xCu, ssize);
            if (s_poll_logged[18] == 1u)
                std::fprintf(stderr, "  FIO VAG: bloco de 4096 bytes aceito em modo silencioso\n");
            return;
        }

        // Leitura de arquivo dos PKB (protocolo deduzido do disassembly de FileIOLib::read/getStatus):
        //   cmd 2 com +0x64 (destino) e +0x68 (bytes) zerados = "carregue o arquivo do FIOHND"; o IOP
        //         guarda o pedido (LSN em +0x70, tamanho arredondado a 2048 em +0x78) e aceita (palavra 0 = 1).
        //   cmd 4 (getStatus): enquanto ha pedido pendente, a resposta e {5, tamanho, 0}. Com 5 o
        //         FileIOLib::getStatus aloca o BufMemory (hnd[40], tamanho = palavra 1) e envia o cmd 5.
        //   cmd 5: +0x64 = destino no EE (dados do BufMemory), +0x68 = bytes, +0x70 = LSN: o IOP copia o
        //         arquivo (setor a partir de LSN) para la. Depois o cmd 4 volta a responder 0 (= pronto).
        // Todos os arquivos dos PKB observados comecam no inicio de um setor (LSN = inicio do PKB +
        // deslocamento/2048, resto 0), entao nao ha deslocamento dentro do setor.
        static bool s_pkb_armed = false;
        static uint32_t s_pkb_size = 0;

        if (cmd == 4 && s_pkb_armed && recv != 0 && rsize >= 12)
        {
            s_pkb_armed = false;
            mwr<uint32_t>(c, recv + 0, 5u);
            mwr<uint32_t>(c, recv + 4, s_pkb_size);
            mwr<uint32_t>(c, recv + 8, 0u);
            std::fprintf(stderr, "  FIO getStatus: arquivo de PKB pronto (%u bytes); o jogo vai pedir os dados (cmd 5)\n", s_pkb_size);
        }

        // Comandos sem implementacao (6 = strReadStart, 12 = ?, ...): na primeira vez de cada um,
        // mostra quem chamou e as ultimas funcoes, para saber o que ele faz no FileIOLib.
        {
            static std::set<uint32_t> s_fio_unknown;
            bool known = cmd == 1u || cmd == 2u || cmd == 4u || cmd == 5u || cmd == 6u || cmd == 12u || cmd == 20u || cmd == 21u;

            if (!known && s_fio_unknown.insert(cmd).second)
            {
                std::fprintf(stderr, "\n  [FIO] comando %u ainda sem implementacao; quem chamou:\n", cmd);
                print_stack_callers(c);
                dump_trace();
                std::fprintf(stderr, "\n");
            }
        }

        // comando 6 (FileIOLib::strReadStart): +0x68 = parametro (0x7F no filme), +0x6C.. = copia do
        // _FIOHND. O FileIOLib so devolve verdadeiro se a palavra 0 da resposta for 1.
        if (cmd == 6 && recv != 0 && rsize >= 4)
        {
            static unsigned s_str_start_log = 0;

            if (s_str_start_log < 20)
            {
                s_str_start_log++;
                std::fprintf(stderr, "  FIO strReadStart: parametro=0x%X lsn=%u tamanho=%u\n",
                    mrd<uint32_t>(c, send + 0x68), mrd<uint32_t>(c, send + 0x70), mrd<uint32_t>(c, send + 0x78));
            }

            mwr<uint32_t>(c, recv + 0, 1u);
        }

        // comando 12 (FileIOLib::strDataCh): a palavra 0 da resposta e o retorno. Diferente de zero
        // significa "o canal de audio do IOP quer mais 0x1000 bytes"; o StreamRead::Run entao copia um
        // bloco do anel de PCM e o envia por outro comando. Zero ("nao precisa de dados") ja e a
        // resposta padrao e nao trava nada: a thread so consulta a cada poucos ticks.

        // comando 2 (read): o pedido (664 bytes) traz
        //   +0x64 = endereco de destino no EE (BufMemory + 4)     +0x68 = bytes a ler
        //   +0x6C.. = copia do _FIOHND (0x2C bytes): +0x70 = LSN absoluto do arquivo no disco
        // A resposta so precisa ter a palavra 0 = 1 ("pedido aceito"); sem isso o FileIOLib::read
        // devolve 0 e o chamador repete o pedido para sempre.
        if (cmd == 2 && send != 0 && ssize >= 0x98)
        {
            uint32_t dest = mrd<uint32_t>(c, send + 0x64);
            uint32_t bytes = mrd<uint32_t>(c, send + 0x68);
            uint32_t lsn = mrd<uint32_t>(c, send + 0x70);
            bool ok = true;
            bool direct = false;

            // Leitura direta (hipotese, a confirmar com o disassembly de FileIOLib::read): para arquivos
            // dos PKB o jogo chama read() sem BufMemory, entao +0x64/+0x68 vem zerados. Nesse caso o
            // FIOHND copiado em +0x6C traz o destino no EE (palavra 0), o LSN (palavra 1, = +0x70) e o
            // tamanho (palavra 3, = +0x78). Sem isso a leitura "volta ok" sem copiar nada e o jogo
            // processa um buffer zerado (foi assim que a Logo caiu num BREAK de divisao por zero).
            // DESLIGADO por padrao: a hipotese estava errada. Pelo disassembly, hnd[0] (+0x6C) e o terceiro
            // argumento do open() (um ponteiro/token dentro do pedido do DvdromOrder), nao o destino, e a
            // leitura direta corrompia a heap. Mantido so sob PS2_FIO_DIRECT=1 para experimentos.
            if (dest == 0 && bytes == 0 && std::getenv("PS2_FIO_DIRECT") != nullptr)
            {
                uint32_t d2 = mrd<uint32_t>(c, send + 0x6C);
                uint32_t b2 = mrd<uint32_t>(c, send + 0x78);

                if (d2 != 0 && b2 != 0 && b2 <= 0x04000000u && (uint64_t)d2 + b2 <= 0x02000000ull)
                {
                    dest = d2;
                    bytes = b2;
                    direct = true;
                }
            }

            if (dest == 0 && bytes == 0 && !direct)
            {
                s_pkb_armed = true;
                s_pkb_size = mrd<uint32_t>(c, send + 0x78);
                std::fprintf(stderr, "  FIO leitura sem BufMemory (PKB): lsn=%u tamanho=%u -> pedido guardado\n",
                    lsn, s_pkb_size);
            }

            if (dest != 0 && bytes != 0)
            {
                uint32_t sectors = (bytes + 2047u) / 2048u;
                uint32_t done = 0;
                std::vector<uint8_t> chunk;

                while (ok && done < sectors)
                {
                    uint32_t n = sectors - done;

                    if (n > 512u)
                        n = 512u;

                    chunk.resize((size_t)n * 2048u);
                    ok = iso_read_sectors(lsn + done, n, chunk.data());

                    if (ok)
                    {
                        uint32_t offset = done * 2048u;
                        uint32_t len = bytes - offset < n * 2048u ? bytes - offset : n * 2048u;
                        std::memcpy(host_ptr(c, dest + offset, len), chunk.data(), len);
                    }

                    done += n;
                }
            }

            static unsigned s_fio_reads = 0;

            if (s_fio_reads < 200)
            {
                s_fio_reads++;
                std::fprintf(stderr, "  FIO leitura%s: lsn=%u bytes=%u (%u setores) -> 0x%08X %s\n",
                    direct ? " direta" : "", lsn, bytes, (bytes + 2047u) / 2048u, dest, ok ? "ok" : "FALHOU (fora da ISO)");
            }

            if (ok)
                mwr<uint32_t>(c, recv + 0, 1u);   // 1 = pedido aceito
        }

        // comando 5: o FileIOLib::getStatus mandou o destino (BufMemory + 4) e o tamanho do arquivo cujo
        // pedido o cmd 2 deixou pendente. O LSN vem na copia do _FIOHND (+0x70).
        if (cmd == 5 && send != 0 && ssize >= 0x98)
        {
            uint32_t dest = mrd<uint32_t>(c, send + 0x64);
            uint32_t bytes = mrd<uint32_t>(c, send + 0x68);
            uint32_t lsn = mrd<uint32_t>(c, send + 0x70);
            bool ok = dest != 0 && bytes != 0 && (uint64_t)dest + bytes <= 0x02000000ull;
            uint32_t sectors = (bytes + 2047u) / 2048u;
            uint32_t done = 0;
            std::vector<uint8_t> chunk;

            while (ok && done < sectors)
            {
                uint32_t n = sectors - done;

                if (n > 512u)
                    n = 512u;

                chunk.resize((size_t)n * 2048u);
                ok = iso_read_sectors(lsn + done, n, chunk.data());

                if (ok)
                {
                    uint32_t offset = done * 2048u;
                    uint32_t len = bytes - offset < n * 2048u ? bytes - offset : n * 2048u;
                    std::memcpy(host_ptr(c, dest + offset, len), chunk.data(), len);
                }

                done += n;
            }

            static unsigned s_fio_cmd5 = 0;

            if (s_fio_cmd5 < 200)
            {
                s_fio_cmd5++;
                std::fprintf(stderr, "  FIO cmd 5 (dados de PKB): lsn=%u bytes=%u (%u setores) -> 0x%08X %s\n",
                    lsn, bytes, sectors, dest, ok ? "ok" : "FALHOU");
            }

            // palavra 0 != 0 = "ainda ocupado": o proximo getStatus (cmd 4, que agora responde 0) conclui.
            mwr<uint32_t>(c, recv + 0, 1u);
        }

        // Se o jogo insiste na mesma chamada sem progresso, e laco sem fim: para e mostra onde.
        if (!polling && g_fio_repeat >= 300)
        {
            std::fprintf(stderr, "\nFIO: o jogo repetiu a mesma chamada (comando=%u) %u vezes sem progresso.\n"
                "O servico FIO ainda so registra a chamada; falta implementar o que o comando faz.\n", cmd, g_fio_repeat);
            print_threads();
            print_stack_callers(c);
            die();
        }
    }

    bool rssd_trace_record(uint32_t kind, uint32_t code, const void* data, uint32_t size)
    {
        // Um fluxo por sessao conserva a ordem entre comandos e chamadas de BGM.
        struct Capture
        {
            std::FILE* file = nullptr;
            uint64_t bytes = 0;
            ~Capture() { if (file) std::fclose(file); }
        };
        static Capture capture;
        static bool stopped = false;
        if (stopped)
            return false;
        if (!capture.file)
        {
            capture.file = std::fopen("rssd_audio_trace.bin", "wb");
            const char magic[] = "ROJRSSD1";
            if (!capture.file || std::fwrite(magic, 1, 8, capture.file) != 8)
            {
                stopped = true;
                std::fprintf(stderr, "  [audio RSSD] falha ao abrir rssd_audio_trace.bin\n");
                return false;
            }
            capture.bytes = 8;
            std::fprintf(stderr, "  [audio RSSD] gravando rssd_audio_trace.bin\n");
        }

        const uint32_t header[] = {kind, code, size};
        if (capture.bytes + sizeof header + size > (128ull << 20))
        {
            stopped = true;
            std::fprintf(stderr, "  [audio RSSD] limite de 128 MiB atingido\n");
            return false;
        }
        if (std::fwrite(header, 1, sizeof header, capture.file) != sizeof header ||
            (size && std::fwrite(data, 1, size, capture.file) != size))
        {
            stopped = true;
            std::fprintf(stderr, "  [audio RSSD] falha ao gravar a captura\n");
            return false;
        }
        capture.bytes += sizeof header + size;
        return true;
    }

    void rssd_trace_rpc(const Ctx& c, uint32_t cmd, uint32_t send, uint32_t size)
    {
        constexpr uint32_t ee_ram_size = 32u << 20;
        const uint32_t address = ee_phys(send);
        if (!c.mem || address == 0 || address >= ee_ram_size ||
            size < 32 || size > 0x8020u || size > ee_ram_size - address)
            return;

        // O mesmo buffer pode ser usado como resposta e zerado pelo RPC.
        rssd_trace_record(0, cmd, c.mem + address, size);
    }

#ifdef _WIN32
    bool rssd_fx_trace_enabled()
    {
        static const bool enabled = [] {
            const char* value = std::getenv("PS2_RSSD_FX_TRACE");
            return value && std::strcmp(value,"1") == 0;
        }();
        return enabled;
    }

    long long rssd_fx_ms()
    {
        using namespace std::chrono;
        static const auto start = steady_clock::now();
        return duration_cast<milliseconds>(steady_clock::now()-start).count();
    }

    void rssd_live_play(LiveBytes sequence, std::shared_ptr<const LiveBytes> bank);
    void rssd_live_stop();
    void rssd_live_effect(EffectRequest request);
    void rssd_live_effect_stop(unsigned id, bool whole_bank = false);

    // Leia a origem real da chamada sincrona de PlayCmnEffect na SLUS-20347.
    // O RPC pode carregar um efeito com o mesmo ID a partir de scripts e animacoes.
    const Ps2FuncEntry* rssd_common_effect_caller(const Ctx& c, uint32_t cmd)
    {
        if (cmd != 0x70u || uint32_t(c.r[31].lo) != 0x0046AEF0u)
            return nullptr;
        const uint32_t stack = ee_phys(uint32_t(c.r[29].lo));
        if (!c.mem || stack > (32u << 20) - 0xb4u)
            return nullptr;
        const uint8_t* frames = c.mem + stack;
        // RssdCallFunc salva $ra em +0x70. SsdPlayEffectNormal e
        // PlayCmnEffect salvam o link no inicio de seus quadros de 0x30 bytes.
        if (live_word(frames + 0x70, 4) != 0x0046B908u ||
            live_word(frames + 0x80, 4) != 0x004175CCu)
            return nullptr;
        const uint32_t link = live_word(frames + 0xb0, 4);
        return link >= 0x00200008u && link < 0x004D7000u ? find_containing(link - 8u) : nullptr;
    }

    bool rssd_menu_effect_caller(const Ps2FuncEntry* caller)
    {
        if (!caller || !caller->name)
            return false;
        const char* name = caller->name;
        // Callbacks da interface. A transicao automatica do mapa e
        // processada por ControlRequest, que tambem usa o ID 1.
        return std::strstr(name, "MenuSelect__") || std::strstr(name, "MenuDecision__") ||
            std::strstr(name, "MenuCancel__") || std::strstr(name, "MenuControlFunc__") ||
            std::strstr(name, "Polling__4Logo") || std::strstr(name, "Polling__5Title") ||
            std::strstr(name, "Polling__8LoadMenu") ||
            std::strstr(name, "cbDecide__7BtlMenu") || std::strstr(name, "cbCancel__7BtlMenu") ||
            std::strstr(name, "cbSelect__7BtlMenu") || std::strstr(name, "UpdateCharActSelPC__7CBattle");
    }

    // O retorno de AddEffectData vai para CSe. O jogo combina esse
    // identificador com o indice local ao montar o comando de som.
    uint32_t rssd_live_event(const Ctx& c, uint32_t cmd, const uint8_t* packet, uint32_t size)
    {
        static LiveBytes incoming, sequence;
        static std::map<unsigned,std::shared_ptr<const LiveBytes>> waves;
        static std::deque<unsigned> recent_waves;
        static std::map<unsigned,std::shared_ptr<const EffectBank>> effects;
        static unsigned expected = 0, incoming_id = 0;
        static bool active = false;
        static uint64_t trace_serial = 0;
        const bool trace = rssd_fx_trace_enabled();
        if (size < 32)
            return 0;

        const auto* data = packet + 32;
        const unsigned length = live_word(packet+12,4);
        if (length > size-32)
            return 0;

        if (cmd == 0x20 || cmd == 0x21)
        {
            if (cmd == 0x20)
            {
                incoming.clear();
                expected = 0;
                if (length >= 0x40 && !std::memcmp(data,"swdm",4))
                {
                    incoming_id = live_word(data+0x12,2);
                    const unsigned declared = live_word(data+8,4);
                    if (declared >= length && declared <= (8u << 20))
                    {
                        expected = declared;
                        incoming.reserve(expected);
                    }
                }
            }
            if (!expected)
                return 0;
            if (length > expected-incoming.size())
            {
                expected = 0;
                incoming.clear();
                return 0;
            }
            incoming.insert(incoming.end(),data,data+length);
            if (incoming.size() == expected)
            {
                waves[incoming_id] = std::make_shared<LiveBytes>(std::move(incoming));
                incoming.clear();
                expected = 0;
                if (incoming_id > 1)
                {
                    recent_waves.erase(std::remove(recent_waves.begin(),recent_waves.end(),incoming_id),
                        recent_waves.end());
                    recent_waves.push_back(incoming_id);
                    if (recent_waves.size() > 16)
                    {
                        waves.erase(recent_waves.front());
                        recent_waves.pop_front();
                    }
                }
            }
            return 0;
        }

        const auto effect_command = [&](unsigned op, const uint8_t* record, bool batch)
        {
            const unsigned id = live_word(record+16,4);
            const unsigned handle = id >> 16;
            if (op == 0x61)
            {
                if (id != 0 && effects.erase(id))
                    rssd_live_effect_stop(id, true);
                return;
            }
            if (op == 0x79 || op == 0x7a)
            {
                rssd_live_effect_stop(id, op == 0x7a);
                return;
            }
            if (op != 0x70 && op != 0x71)
                return;

            const unsigned arg1 = live_word(record+20,4), arg2 = live_word(record+24,4);
            const auto bank = effects.find(handle);
            const bool menu_id = id >= 1 && id <= 3;
            const auto* caller = !batch && (menu_id || id == 0x10 || trace)
                ? rssd_common_effect_caller(c, op) : nullptr;
            const bool item_pickup = id == 0x10 && caller && caller->name &&
                std::strcmp(caller->name, "k2script_decoder__8K2ScriptFv") == 0;
            const bool use_recording = handle == 0 && (!menu_id || rssd_menu_effect_caller(caller));
            const uint64_t serial = trace ? ++trace_serial : 0;
            if (trace)
                std::fprintf(stderr,"  [RSSD FX] t=%lldms n=%llu origem=%s cmd=%02X id=%08X arg1=%u arg2=%u resultado=%s chamador=%s\n",
                    rssd_fx_ms(),static_cast<unsigned long long>(serial),batch?"lote":"direta",op,id,arg1,arg2,
                    bank!=effects.end()||use_recording?"enviado":"sem-banco",
                    caller && caller->name ? caller->name : "-");
            if (bank != effects.end() || use_recording)
                rssd_live_effect({bank!=effects.end()?bank->second:nullptr,id,op==0x71?arg1:127,
                    op==0x71?std::min(127u,arg2):64,serial,use_recording,item_pickup});
        };

        if (cmd == 4)
        {
            const unsigned count = live_word(packet+20,4);
            if (length%32 || count > length/32)
                return 0;
            for (unsigned i=0; i<count; ++i)
            {
                const auto* record = data+32*i;
                effect_command(live_word(record,2),record,true);
            }
            return 0;
        }

        if (cmd == 0x60)
        {
            if (length < 0x34 || std::memcmp(data,"seds",4) || live_word(data+8,4)!=length)
                return UINT32_MAX;
            const unsigned primary_id = live_word(data+28,2);
            const bool common = std::memcmp(data+0x20,"regist01.sav",13) == 0;
            constexpr unsigned table = 0x34;
            if (table+2*live_word(data+26,2)>length)
                return UINT32_MAX;
            const unsigned auxiliary_id = common ? 1 : live_word(data+30,2);
            const auto primary = waves.find(primary_id), auxiliary = waves.find(auxiliary_id);
            if (primary == waves.end() && auxiliary == waves.end())
                return UINT32_MAX;
            unsigned handle = common ? 0 : std::max(1u,primary_id);
            if (!common)
            {
                while (effects.count(handle))
                {
                    const auto& existing = effects.at(handle)->seds;
                    if (existing.size() == length && std::equal(existing.begin(),existing.end(),data))
                        break;
                    handle = handle == 0xffffu ? 1u : handle+1u;
                    if (handle == std::max(1u,primary_id))
                        return UINT32_MAX;
                }
            }
            auto loaded = std::make_shared<EffectBank>(EffectBank{
                primary!=waves.end()?primary->second:nullptr,
                auxiliary!=waves.end()?auxiliary->second:nullptr,LiveBytes(data,data+length)});
            loaded->external_key.sequence=roj::effect_fingerprint(data,length,12);
            if(loaded->swd)loaded->external_key.primary=roj::effect_fingerprint(loaded->swd->data(),loaded->swd->size());
            if(loaded->auxiliary)loaded->external_key.auxiliary=roj::effect_fingerprint(loaded->auxiliary->data(),loaded->auxiliary->size());
            effects[handle]=std::move(loaded);
            if (trace)
                std::fprintf(stderr,"  [RSSD FX] t=%lldms banco=%.16s id=%04X registros=%u\n",
                    rssd_fx_ms(),reinterpret_cast<const char*>(data+0x20),handle,live_word(data+26,2));
            return handle;
        }

        if (cmd == 0x31)
        {
            if (length >= 0x78 && !std::memcmp(data,"smdm",4) && live_word(data+8,4)==length)
                sequence.assign(data,data+length);
            else
                sequence.clear();
            return 0;
        }

        if (cmd == 0x34 || cmd == 0x36 || cmd == 0x32)
        {
            // As faixas observadas usam o canal de musica 0.
            if (live_word(packet+16,4) != 0)
                return 0;
            if (cmd == 0x34 && !sequence.empty())
            {
                const auto bank = waves.find(live_word(sequence.data()+0x1e,2));
                if (bank != waves.end())
                {
                    rssd_live_play(sequence,bank->second);
                    active = true;
                }
            }
            else if (cmd != 0x34)
            {
                if (active)
                    rssd_live_stop();
                active = false;
                if (cmd == 0x32)
                    sequence.clear();
            }
            return 0;
        }

        effect_command(cmd,packet,false);
        return 0;
    }

#endif

    // RSSD.IRX: protocolo de som do jogo.
    void rssd_service(Ctx& c, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize)
    {
        rpc_log(c, "RSSD", cmd, send, ssize, recv, rsize);
        if (g_rssd_audio_trace)
            rssd_trace_rpc(c, cmd, send, ssize);
#ifdef _WIN32
        // RssdCallFunc pode reutilizar o buffer de envio para a resposta.
        // Leia o cabecalho e o payload inteiros antes de escrever nesse buffer.
        uint32_t result = 0;
        static const bool live_audio = battle_profile_enabled("PS2_RSSD_LIVE_AUDIO");
        if (live_audio)
        {
            constexpr uint32_t ee_ram_size = 32u << 20;
            const uint32_t address = ee_phys(send);
            if (c.mem && address && address < ee_ram_size && ssize <= ee_ram_size - address)
                result = rssd_live_event(c, cmd, c.mem + address, ssize);
        }
#endif
        rpc_zero_recv(c, recv, rsize);
#ifdef _WIN32
        if (cmd == 0x60 && recv && rsize >= 12 && rsize <= 0x10000u)
            mwr<uint32_t>(c, recv + 8, result);
#endif

        // comando 1 = inicializar o RSSD. O RssdSifRpcServer do jogo (sid 0x901) trata o
        // comando 0x1000 como "IOP pronto": faz SignalSema no semaforo que o SsdInit espera.
        // O servidor so le 32 bytes de dados e ignora o conteudo nesse ramo.
        if (cmd == 1)
        {
            uint8_t payload[32] = {};
            ee_rpc_post(c, 0x901u, 0x1000u, payload, sizeof(payload));
        }

        // comandos 0x20 / 0x21: envio de um bloco de dados de onda (0x8000 bytes) para o SPU, por DMA.
        // Quando a DMA termina o IOP avisa o EE pelo servidor 0x901 com o comando 0x25:
        // RssdBackgroundNextWave desliga o bit 2 ("DMA em andamento") do registrador de estado e
        // acorda a thread que manda o proximo bloco. O jogo so confere que o campo +0x10 do
        // registro (32 bytes) e >= 0, entao um registro zerado basta.
        if (cmd == 0x20 || cmd == 0x21)
        {
            uint8_t record[32] = {};
            ee_rpc_post(c, 0x901u, 0x25u, record, sizeof(record));
        }
    }

    namespace fs = std::filesystem;

    // Cartao de PS2 em uma pasta local. O protocolo do jogo continua sendo o MCSERV.
    class MemoryCard
    {
        static constexpr uint32_t ClusterSize = 1024, Capacity = 8000 * ClusterSize;
        static constexpr int Missing = -4, Denied = -5, Full = -3, Absent = -10, Unformatted = -2;
        struct File
        {
            fs::path path;
            std::vector<uint8_t> bytes;
            size_t pos = 0;
            uint32_t flags = 0;
            bool dirty = false;
        };
        struct DirectoryEntry
        {
            fs::path path;
            std::string name;
        };

        fs::path current;
        std::map<uint32_t, File> handles;
        std::vector<DirectoryEntry> listing;
        size_t listed = 0;
        bool first_info = true;
        unsigned card_port = 0;

        fs::path root() const { return g_game_directory / "Saves" / (card_port == 0 ? "Slot1" : "Slot2"); }

        // Use apenas RAM do EE, incluindo seus aliases, para os buffers do RPC.
        static uint8_t* ram(Ctx& c, uint32_t addr, uint32_t count)
        {
            const uint32_t physical = ee_phys(addr);
            return c.mem && physical && uint64_t(physical) + count <= 0x02000000u
                ? c.mem + physical : nullptr;
        }

        static bool safe_name(const std::string& name, bool pattern = false)
        {
            if (name.empty() || name.size() > 32 || name.back() == '.' || name.back() == ' ')
                return false;
            for (unsigned char ch : name)
                if (ch < 32 || ch >= 127 || std::strchr("\\/:<>|\"", ch) ||
                    (!pattern && (ch == '*' || ch == '?')))
                    return false;
            std::string stem = name.substr(0, name.find('.'));
            for (char& ch : stem)
                ch = (char)std::toupper((unsigned char)ch);
            return stem != "CON" && stem != "PRN" && stem != "AUX" && stem != "NUL" &&
                !(stem.size() == 4 && (stem.compare(0, 3, "COM") == 0 || stem.compare(0, 3, "LPT") == 0) &&
                  stem[3] >= '0' && stem[3] <= '9');
        }

        fs::path resolve(std::string name) const
        {
            fs::path relative = !name.empty() && name[0] == '/' ? fs::path{} : current;
            for (size_t start = 0; start < name.size();)
            {
                const size_t slash = name.find('/', start);
                const std::string part = name.substr(start, slash - start);
                start = slash == std::string::npos ? name.size() : slash + 1;
                if (part == "..")
                    relative = relative.parent_path();
                else if (!part.empty() && part != ".")
                {
                    if (!safe_name(part))
                        return {};
                    relative /= part;
                }
            }

            // Um link simbolico num save copiado nao pode redirecionar operacoes para fora do cartao.
            fs::path target = root();
            for (const auto& part : relative)
            {
                target /= part;
                if (fs::is_symlink(fs::symlink_status(target)))
                    return {};
            }
            return target;
        }

        static bool match(const char* pattern, const char* name)
        {
            const char* star = nullptr;
            const char* retry = nullptr;
            while (*name)
            {
                if (*pattern == '?' || *pattern == *name)
                {
                    ++pattern;
                    ++name;
                }
                else if (*pattern == '*')
                {
                    star = pattern++;
                    retry = name;
                }
                else if (star)
                {
                    pattern = star + 1;
                    name = ++retry;
                }
                else
                    return false;
            }
            while (*pattern == '*')
                ++pattern;
            return !*pattern;
        }

        uint32_t free_clusters() const
        {
            uint64_t used = 0;
            for (const auto& entry : fs::recursive_directory_iterator(root()))
            {
                if (fs::is_symlink(entry.symlink_status()))
                    continue;
                if (entry.is_directory())
                    ++used;
                else if (entry.is_regular_file())
                    used += (entry.file_size() + ClusterSize - 1) / ClusterSize;
            }
            return used < Capacity / ClusterSize ? uint32_t(Capacity / ClusterSize - used) : 0;
        }

        static bool commit(File& file)
        {
            if (!file.dirty)
                return true;

            // Grave e feche uma copia temporaria antes de substituir o save anterior.
            // O sufixo nao e um nome de Memory Card valido e fica fora de GetDir.
            fs::path temporary = file.path;
            temporary += ".ring-of-judgment-memory-card-temporary";
            const auto status = fs::symlink_status(temporary);
            if (fs::exists(status) && !fs::is_regular_file(status))
                return false;
            std::ofstream out(temporary, std::ios::binary | std::ios::trunc);
            if (!file.bytes.empty())
                out.write(reinterpret_cast<const char*>(file.bytes.data()), file.bytes.size());
            out.close();
            std::error_code error;
            bool replaced = false;
            if (out)
            {
#ifdef _WIN32
                replaced = ps2_windows_replace_file(temporary, file.path);
#else
                fs::rename(temporary, file.path, error);
                replaced = !error;
#endif
            }
            if (!replaced)
            {
                fs::remove(temporary, error);
                return false;
            }
            std::fprintf(stderr, "  [MC] save gravado: %s (%zu bytes)\n",
                file.path.filename().string().c_str(), file.bytes.size());
            file.dirty = false;
            return true;
        }

        int open(const fs::path& path, uint32_t flags)
        {
            constexpr uint32_t Write = 2, Create = 0x0200, Truncate = 0x0400, Append = 0x0100;
            if (path == root() || !fs::is_directory(path.parent_path()))
                return Missing;
            if (flags & 0x40u) // sceMcMkdir usa sceMcOpen com este bit.
            {
                if (fs::is_directory(path))
                    return 0;
                return fs::create_directory(path) ? 0 : Denied;
            }
            const bool exists = fs::exists(path);
            if (!exists && !(flags & Create))
                return Missing;
            if (exists && !fs::is_regular_file(path))
                return Denied;
            for (const auto& item : handles)
                if (item.second.path == path && ((flags | item.second.flags) & Write))
                    return Denied;

            uint32_t fd = card_port * 16;
            while (fd < (card_port + 1) * 16 && handles.count(fd))
                ++fd;
            if (fd == (card_port + 1) * 16)
                return -7;

            File file;
            file.path = path;
            file.flags = flags;
            file.dirty = !exists || ((flags & Write) && (flags & Truncate));
            if (exists && !file.dirty)
            {
                const auto length = fs::file_size(path);
                if (length > Capacity)
                    return Denied;
                file.bytes.resize(size_t(length));
                std::ifstream in(path, std::ios::binary);
                if (!in || (!file.bytes.empty() && !in.read(reinterpret_cast<char*>(file.bytes.data()), file.bytes.size())))
                    return Denied;
            }
            if (flags & Append)
                file.pos = file.bytes.size();
            handles.emplace(fd, std::move(file));
            return int(fd);
        }

        static void fill_time(Ctx& c, uint32_t address, const fs::path& path)
        {
            const auto timestamp = fs::last_write_time(path);
            const auto now = std::chrono::system_clock::now();
            const auto time = std::chrono::time_point_cast<std::chrono::system_clock::duration>(
                timestamp - fs::file_time_type::clock::now() + now);
            const auto seconds = std::chrono::system_clock::to_time_t(time + std::chrono::hours(9));
            const std::tm* date = std::gmtime(&seconds);
            if (!date)
                return;
            mwr<uint8_t>(c, address + 1, uint8_t(date->tm_sec));
            mwr<uint8_t>(c, address + 2, uint8_t(date->tm_min));
            mwr<uint8_t>(c, address + 3, uint8_t(date->tm_hour));
            mwr<uint8_t>(c, address + 4, uint8_t(date->tm_mday));
            mwr<uint8_t>(c, address + 5, uint8_t(date->tm_mon + 1));
            mwr<uint16_t>(c, address + 6, uint16_t(date->tm_year + 1900));
        }

        int get_dir(Ctx& c, const std::string& name, uint32_t mode, uint32_t count, uint32_t output)
        {
            if (count > 4096 || (count && !ram(c, output, count * 64u)))
                return Denied;
            if (mode == 0)
            {
                listing.clear();
                listed = 0;
                const size_t slash = name.rfind('/');
                const fs::path dir = resolve(slash == std::string::npos ? "" : name.substr(0, slash + 1));
                const std::string pattern = name.substr(slash == std::string::npos ? 0 : slash + 1);
                if (dir.empty() || !fs::is_directory(dir))
                    return Missing;
                const std::string search = pattern.empty() ? "*" : pattern;
                if (search != "." && search != ".." && !safe_name(search, true))
                    return Denied;

                // A libmc inclui estas duas entradas antes dos arquivos. O menu
                // Load de Shadow Hearts comeca a validar o save no indice 2.
                if (match(search.c_str(), "."))
                    listing.push_back({dir, "."});
                if (match(search.c_str(), ".."))
                    listing.push_back({dir == root() ? dir : dir.parent_path(), ".."});
                const size_t first_child = listing.size();
                for (const auto& entry : fs::directory_iterator(dir))
                {
                    const std::string item = entry.path().filename().string();
                    const auto status = entry.symlink_status();
                    if (safe_name(item) && match(search.c_str(), item.c_str()) &&
                        (fs::is_regular_file(status) || fs::is_directory(status)))
                        listing.push_back({entry.path(), item});
                }
                std::sort(listing.begin() + first_child, listing.end(),
                    [](const DirectoryEntry& left, const DirectoryEntry& right) { return left.name < right.name; });
            }
            unsigned written = 0;
            while (written < count && listed < listing.size())
            {
                const DirectoryEntry& entry = listing[listed++];
                const fs::path& path = entry.path;
                const auto status = fs::symlink_status(path);
                const bool directory = fs::is_directory(status);
                if (!directory && !fs::is_regular_file(status))
                    continue;
                const uint32_t address = output + written++ * 64u;
                std::memset(ram(c, address, 64), 0, 64);
                fill_time(c, address, path);
                fill_time(c, address + 8, path);
                mwr<uint32_t>(c, address + 16, directory ? 0u : uint32_t(fs::file_size(path)));
                mwr<uint16_t>(c, address + 20, directory ? 0x8427u : 0x8497u);
                std::memcpy(ram(c, address + 32, 32), entry.name.data(), entry.name.size());
            }
            return int(written);
        }

        int file_command(Ctx& c, uint32_t cmd, uint32_t send)
        {
            const auto field = [&](uint32_t offset) { return mrd<uint32_t>(c, send + offset); };
            const auto it = handles.find(field(0));
            if (it == handles.end())
                return Denied;
            File& file = it->second;
            if (cmd == 3 || cmd == 10)
            {
                if (!commit(file))
                    return Denied;
                if (cmd == 3)
                    handles.erase(it);
                return 0;
            }
            if (cmd == 4)
            {
                const uint32_t origin = field(20);
                if (origin > 2)
                    return Denied;
                const int64_t base = origin == 0 ? 0 : (origin == 1 ? file.pos : file.bytes.size());
                const int64_t next = base + int32_t(field(16));
                if (next < 0 || next > Capacity)
                    return Denied;
                file.pos = size_t(next);
                return int(next);
            }
            const uint32_t size = field(12), buffer = field(24);
            if (size > Capacity)
                return Denied;
            if (cmd == 5)
            {
                // A copia direta ja entrega inclusive as pontas desalinhadas. O callback
                // da libmc recebe zero nos dois comprimentos para nao copiar dados antigos.
                uint8_t* alignment = ram(c, field(28), 8);
                uint8_t* dest = size ? ram(c, buffer, size) : nullptr;
                if (!(file.flags & 1u) || !alignment || (size && !dest))
                    return Denied;
                std::memset(alignment, 0, 8);
                const size_t bytes = std::min<size_t>(size, file.bytes.size() - std::min(file.pos, file.bytes.size()));
                if (bytes)
                    std::memcpy(dest, file.bytes.data() + file.pos, bytes);
                file.pos += bytes;
                return int(bytes);
            }

            // O pacote de escrita separa os primeiros 0..16 bytes do restante em RAM.
            const uint32_t prefix = field(20);
            const uint8_t* rest = size ? ram(c, buffer, size) : nullptr;
            if (!(file.flags & 2u) || prefix > 16 || (size && !rest))
                return Denied;
            const size_t bytes = size + prefix;
            if (!bytes)
                return 0;
            const size_t pos = file.flags & 0x0100u ? file.bytes.size() : file.pos;
            const size_t end = pos + bytes;
            const uint64_t disk_size = fs::exists(file.path) ? fs::file_size(file.path) : 0;
            if (end > Capacity || (end + ClusterSize - 1) / ClusterSize >
                (disk_size + ClusterSize - 1) / ClusterSize + free_clusters())
                return Full;
            if (end > file.bytes.size())
                file.bytes.resize(end);
            if (prefix)
                std::memcpy(file.bytes.data() + pos, ram(c, send + 32, prefix), prefix);
            if (size)
                std::memcpy(file.bytes.data() + pos + prefix, rest, size);
            file.pos = end;
            file.dirty = true;
            return int(bytes);
        }

    public:
        explicit MemoryCard(unsigned port = 0) : card_port(port) {}
        void init()
        {
            current.clear();
            handles.clear();
            listing.clear();
            listed = 0;
            first_info = true;
        }

        int command(Ctx& c, uint32_t cmd, uint32_t send, uint32_t size)
        {
            const bool descriptor = cmd == 1 || (cmd >= 3 && cmd <= 6) || cmd == 10 || cmd == 16;
            if (size != (descriptor ? 0x30u : 0x414u) || !ram(c, send, size))
                return Denied;

            const auto field = [&](uint32_t offset) { return mrd<uint32_t>(c, send + offset); };
            const bool file_rpc = (cmd >= 3 && cmd <= 6) || cmd == 10;
            const bool port_present = file_rpc || (field(descriptor ? 4 : 0) == card_port && field(descriptor ? 8 : 4) == 0);
            const auto status = fs::symlink_status(root());
            // Cada pasta SlotN e o marcador de formato daquele cartao.
            if (fs::is_symlink(fs::symlink_status(root().parent_path())) ||
                (fs::exists(status) && !fs::is_directory(status)))
                return Denied;
            const bool formatted = port_present && fs::is_directory(status);
            if (cmd == 1)
            {
                const uint32_t out = field(28);
                if (!ram(c, out, 0x94))
                    return Denied;
                mwr<uint32_t>(c, out, port_present ? 2u : 0u);
                mwr<uint32_t>(c, out + 4, formatted ? free_clusters() : 0);
                mwr<uint32_t>(c, out + 0x90, formatted ? 1u : 0u);
                if (!port_present)
                    return Absent;
                if (!formatted)
                    return Unformatted;
                const int result = first_info ? -1 : 0;
                first_info = false;
                return result;
            }
            if (!port_present)
                return Absent;
            if (cmd == 16)
            {
                if (!formatted)
                    fs::create_directories(root());
                return 0;
            }
            if (!formatted)
                return Unformatted;
            if (file_rpc)
                return file_command(c, cmd, send);

            const char* start = reinterpret_cast<const char*>(ram(c, send + 20, size - 20));
            const char* end = static_cast<const char*>(std::memchr(start, 0, size - 20));
            if (!end)
                return Denied;
            const std::string name(start, end);
            if (cmd == 13)
                return get_dir(c, name, field(8), field(12), field(16));
            const fs::path path = resolve(name);
            if (path.empty())
                return Denied;
            switch (cmd)
            {
                case 2: return open(path, field(8));
                case 12:
                {
                    if (!fs::is_directory(path))
                        return Missing;
                    const uint32_t out = field(16);
                    const std::string absolute = path == root() ? "/" : "/" + path.lexically_relative(root()).generic_string();
                    if (out)
                    {
                        uint8_t* buffer = ram(c, out, 1024);
                        if (!buffer || absolute.size() >= 1024)
                            return Denied;
                        std::memcpy(buffer, absolute.c_str(), absolute.size() + 1);
                    }
                    current = path == root() ? fs::path{} : path.lexically_relative(root());
                    return 0;
                }
                case 15:
                    if (path == root() || !fs::exists(path))
                        return Missing;
                    for (const auto& item : handles)
                        if (item.second.path == path)
                            return Denied;
                    if (fs::is_directory(path) && !fs::is_empty(path))
                        return -6;
                    return fs::remove(path) ? 0 : Denied;
                case 18: return fs::is_directory(path) ? int(free_clusters()) : Missing;
                default: return Denied;
            }
        }
    };

    void mcserv_service(Ctx& c, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize)
    {
        static MemoryCard cards[2]{MemoryCard(0),MemoryCard(1)};
        static std::set<uint32_t> seen;
        if (!recv || rsize < 4)
            return;
        rpc_zero_recv(c, recv, rsize);
        if (cmd == 0xFE)
        {
            for (auto& card : cards) card.init();
            if (rsize >= 12)
            {
                mwr<uint32_t>(c, recv + 4, 0x20Au);
                mwr<uint32_t>(c, recv + 8, 0x20Eu);
            }
            return;
        }

        int result = -5;
        try
        {
            const bool descriptor = cmd == 1 || (cmd >= 3 && cmd <= 6) || cmd == 10 || cmd == 16;
            const bool file_rpc = (cmd >= 3 && cmd <= 6) || cmd == 10;
            const uint32_t physical = ee_phys(send);
            if (c.mem && physical && uint64_t(physical) + ssize <= 0x02000000u &&
                ssize == (descriptor ? 0x30u : 0x414u)) {
                const unsigned port = file_rpc ? mrd<uint32_t>(c,send)/16u : mrd<uint32_t>(c,send+(descriptor?4:0));
                result = port < 2 ? cards[port].command(c,cmd,send,ssize) : -10;
            }
        }
        catch (const fs::filesystem_error& e)
        {
            std::fprintf(stderr, "  [MC] erro de arquivo: %s\n", e.what());
        }
        mwr<uint32_t>(c, recv, uint32_t(result));
        if (seen.insert(cmd).second || cmd == 16 || (cmd == 3 && result < 0))
            std::fprintf(stderr, "  [MC] comando 0x%02X -> %d\n", cmd, result);
    }

    // Servico desconhecido: registra e devolve zeros (sucesso), para o boot nao parar.
    void generic_service(Ctx& c, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize)
    {
        rpc_log(c, "GENERIC", cmd, send, ssize, recv, rsize);
        rpc_zero_recv(c, recv, rsize);
    }

    // Servicos criados sob demanda no bind (os ponteiros do std::map sao estaveis).
    std::map<uint32_t, RpcService> g_rpc_dynamic;

    const RpcService g_rpc_services[] =
    {
        { 0x98765432u, "FIO", fio_service },
        { 0x00000900u, "RSSD", rssd_service },
        { 0x80000400u, "MCSERV", mcserv_service }
    };

    const RpcService* rpc_find(uint32_t sid)
    {
        for (size_t i = 0; i < sizeof(g_rpc_services) / sizeof(g_rpc_services[0]); i++)
        {
            if (g_rpc_services[i].sid == sid)
                return &g_rpc_services[i];
        }

        auto it = g_rpc_dynamic.find(sid);

        if (it != g_rpc_dynamic.end())
            return &it->second;

        return nullptr;
    }
}

void ps2_rpc(Ctx& c, int function)
{
    uint32_t client = (uint32_t)c.r[4].lo;

    switch (function)
    {
        case PS2_RPC_BIND:   // sceSifBindRpc(client, sid, modo): preenche o sceSifClientData
        {
            uint32_t sid = (uint32_t)c.r[5].lo;
            const RpcService* service = rpc_find(sid);

            if (!service)
            {
                std::fprintf(stderr, "  sceSifBindRpc(0x%08X): servico sem implementacao, usando stub generico\n", sid);
                g_rpc_dynamic[sid] = RpcService{ sid, "GENERIC", generic_service };
                service = rpc_find(sid);
            }
            else
            {
                static std::map<uint32_t, unsigned> s_bind_count;
                unsigned n = ++s_bind_count[sid];

                if (n <= 3)
                    std::fprintf(stderr, "  sceSifBindRpc(0x%08X) -> servico %s\n", sid, service->name);
                else if (n == 4)
                    std::fprintf(stderr, "  sceSifBindRpc(0x%08X): mais binds iguais; a partir daqui so a cada 1000\n", sid);
                else if (n % 1000 == 0)
                    std::fprintf(stderr, "  sceSifBindRpc(0x%08X): %u binds\n", sid, n);
            }

            // struct sceSifClientData (40 bytes):
            //   0x00 hdr { pkt_addr, rpc_id, sema_id, mode }   0x10 command   0x14 buff   0x18 cbuff
            //   0x1C end_function   0x20 end_param   0x24 server
            mwr<uint32_t>(c, client + 0x00, 0);
            mwr<uint32_t>(c, client + 0x04, sid);                              // rpc_id
            mwr<uint32_t>(c, client + 0x08, 0);
            mwr<uint32_t>(c, client + 0x0C, 0);
            mwr<uint32_t>(c, client + 0x10, 0);
            mwr<uint32_t>(c, client + 0x14, 0);
            mwr<uint32_t>(c, client + 0x18, 0);
            mwr<uint32_t>(c, client + 0x1C, 0);
            mwr<uint32_t>(c, client + 0x20, 0);
            mwr<uint32_t>(c, client + 0x24, 0x00100000u + (sid & 0xFFFFu));   // server: qualquer valor nao nulo

            c.r[2].lo = 0;
            break;
        }

        case PS2_RPC_CALL:   // sceSifCallRpc(client, cmd, modo, enviar, tam, receber, tam, fim, arg_fim)
        {
            uint32_t cmd = (uint32_t)c.r[5].lo;
            uint32_t send = (uint32_t)c.r[7].lo;
            uint32_t ssize = (uint32_t)c.r[8].lo;
            uint32_t recv = (uint32_t)c.r[9].lo;
            uint32_t rsize = (uint32_t)c.r[10].lo;
            uint32_t end_func = (uint32_t)c.r[11].lo;
            uint32_t end_arg = mrd<uint32_t>(c, (uint32_t)c.r[29].lo);   // 9o argumento: topo da pilha

            uint32_t sid = mrd<uint32_t>(c, client + 0x04);
            const RpcService* service = rpc_find(sid);

            if (!service)
            {
                std::fprintf(stderr, "RPC: sceSifCallRpc num cliente nao associado (servico 0x%08X)\n", sid);
                die();
            }

            service->fn(c, cmd, send, ssize, recv, rsize);

            // a chamada e feita na hora: avisa o fim como o IOP faria
            if (end_func != 0)
            {
                c.r[4].lo = S32(end_arg);
                ps2_call(c, end_func);
            }

            c.r[2].lo = 0;
            break;
        }

        case PS2_RPC_CHECK:  // sceSifCheckStatRpc(client): 0 = terminou
            c.r[2].lo = 0;
            break;

        case PS2_RPC_REGISTER:   // sceSifRegisterRpc(sd, sid, func, buff, tam, cfunc, cbuff, fila)
        {
            EeRpcServer s;
            s.sd = (uint32_t)c.r[4].lo;
            s.sid = (uint32_t)c.r[5].lo;
            s.func = (uint32_t)c.r[6].lo;
            s.buff = (uint32_t)c.r[7].lo;
            s.cfunc = (uint32_t)c.r[8].lo;
            s.cbuff = (uint32_t)c.r[9].lo;
            s.queue = (uint32_t)c.r[10].lo;

            const Ps2FuncEntry* e = find_entry(s.func);
            std::fprintf(stderr, "  sceSifRegisterRpc sid=0x%08X func=0x%08X (%s) buff=0x%08X\n",
                s.sid, s.func, e ? e->name : "?", s.buff);

            g_ee_rpc_servers.push_back(s);
            c.r[2].lo = s.sd;   // o SDK devolve o proprio sceSifServeData
            break;
        }

        case PS2_RPC_NEXT_REQUEST:   // sceSifGetNextRequest(fila): proximo pedido do IOP, ou 0
        {
            static unsigned s_empty_polls = 0;

            while (!g_ee_rpc_queue.empty())
            {
                EeRpcRequest req = g_ee_rpc_queue.front();
                g_ee_rpc_queue.pop_front();

                const EeRpcServer* server = ee_rpc_find(req.sid);

                if (!server)
                {
                    std::fprintf(stderr, "  RPC EE: pedido para o servidor 0x%08X, que nao esta registrado\n", req.sid);
                    continue;
                }

                // o IOP gravaria os dados no buffer do servidor (por DMA) antes do pedido chegar
                for (size_t i = 0; i < req.data.size(); i++)
                    mwr<uint8_t>(c, server->buff + (uint32_t)i, req.data[i]);

                std::fprintf(stderr, "  sceSifGetNextRequest: servidor 0x%08X comando=0x%X\n", req.sid, req.fno);

                g_ee_exec_req = req;
                g_ee_exec_server = *server;
                g_ee_exec_valid = true;
                s_empty_polls = 0;
                c.r[2].lo = server->sd;
                return;
            }

            c.r[2].lo = 0;

            // O jogo fica perguntando se o IOP mandou algo. Se nunca chega, e laco sem fim.
            if (++s_empty_polls == 200000)
            {
                std::fprintf(stderr, "\nsceSifGetNextRequest: o jogo consultou %u vezes e nenhum pedido do IOP chegou.\n"
                    "Ele espera um aviso do IOP (servidor RPC do EE) que o runtime ainda nao envia.\n", s_empty_polls);
                print_threads();
                print_stack_callers(c);
                die();
            }

            break;
        }

        case PS2_RPC_EXEC_REQUEST:   // sceSifExecRequest(sd): executa o pedido entregue acima
        {
            if (g_ee_exec_valid)
            {
                EeRpcRequest req = g_ee_exec_req;
                EeRpcServer server = g_ee_exec_server;
                g_ee_exec_valid = false;

                std::fprintf(stderr, "  sceSifExecRequest: servidor 0x%08X comando=0x%X\n", req.sid, req.fno);

                c.r[4].lo = req.fno;
                c.r[5].lo = server.buff;
                c.r[6].lo = (uint64_t)req.data.size();
                ps2_call(c, server.func);
            }

            c.r[2].lo = 0;
            break;
        }

        default:
            break;
    }
}

// sceSifRpcLoop(fila): roda para sempre na thread do servidor; entrega os pedidos do IOP.
void ps2_rpc_loop(Ctx& c)
{
    g_rpc_loop_thread = g_kernel_ready ? g_running : 1;

    for (;;)
    {
        while (!g_ee_rpc_queue.empty())
        {
            EeRpcRequest req = g_ee_rpc_queue.front();
            g_ee_rpc_queue.pop_front();

            const EeRpcServer* server = nullptr;

            for (size_t i = 0; i < g_ee_rpc_servers.size(); i++)
            {
                if (g_ee_rpc_servers[i].sid == req.sid)
                    server = &g_ee_rpc_servers[i];
            }

            if (!server)
            {
                std::fprintf(stderr, "  RPC EE: pedido para o servidor 0x%08X, que nao esta registrado\n", req.sid);
                continue;
            }

            // o IOP gravaria os dados no buffer do servidor (por DMA) antes de chamar a funcao
            for (size_t i = 0; i < req.data.size(); i++)
                mwr<uint8_t>(c, server->buff + (uint32_t)i, req.data[i]);

            std::fprintf(stderr, "  RPC EE: servidor 0x%08X comando=0x%X\n", req.sid, req.fno);

            c.r[4].lo = req.fno;
            c.r[5].lo = server->buff;
            c.r[6].lo = (uint64_t)req.data.size();
            ps2_call(c, server->func);
        }

        c.r[3].lo = 50;   // SleepThread: ate chegar outro pedido
        ps2_syscall(c);
    }
}

// ---- controle: libpad nativo ----

namespace
{
    struct PadState
    {
        bool open;
        uint16_t buttons;     // ativo em zero: 0xFFFF = nada apertado
        uint8_t sticks[4];    // direito X, direito Y, esquerdo X, esquerdo Y (0x80 = centro)
    };

    PadState g_pads[2] =
    {
        { false, 0xFFFF, { 0x80, 0x80, 0x80, 0x80 } },
        { false, 0xFFFF, { 0x80, 0x80, 0x80, 0x80 } }
    };

    // Existe um DualShock 2 na porta 1 (indice 0), slot 0. A porta 2 fica vazia.
    bool pad_present(int port, int slot)
    {
        return port == 0 && slot == 0 && g_pads[0].open;
    }
}

void rssd_trace_bgm(const Ctx& c, uint32_t function)
{
    const uint32_t args[] = {
        (uint32_t)c.r[4].lo, (uint32_t)c.r[5].lo, (uint32_t)c.r[6].lo, (uint32_t)c.r[31].lo
    };
    rssd_trace_record(1, function, args, sizeof args);
}

void ps2_pad_set_state(int port, uint16_t buttons_active_low, uint8_t right_x, uint8_t right_y, uint8_t left_x, uint8_t left_y)
{
    if (port < 0 || port > 1)
        return;

    g_pads[port].buttons = buttons_active_low;
    g_pads[port].sticks[0] = right_x;
    g_pads[port].sticks[1] = right_y;
    g_pads[port].sticks[2] = left_x;
    g_pads[port].sticks[3] = left_y;
}

// libpad: argumentos em a0..a3, retorno em v0.
extern const bool g_rotation_caller_trace = [] {
    const char* value = std::getenv("PS2_ROTATION_CALLER_TRACE");
    return value && std::strcmp(value, "1") == 0;
}();

namespace
{
    // Classifica pela ultima amostra do analogico que o jogo leu.
    // O EE atualiza estes contadores e imprime o resumo.
    bool g_rotation_pad_active = false;
    uint64_t g_rotation_pad_reads[2]{};
    std::map<std::pair<uint32_t, uint32_t>, uint64_t> g_rotation_callers[2];
}

void rotation_caller_record(const Ctx& c, uint32_t axis)
{
    const uint32_t link = (uint32_t)c.r[31].lo;
    // JAL grava PC+8. Em tail calls, o link ainda e o do chamador anterior.
    const uint32_t caller = link >= 8 ? link - 8 : 0;
    ++g_rotation_callers[g_rotation_pad_active][{caller, axis}];
}

static void rotation_caller_report()
{
    if (!g_rotation_caller_trace)
        return;

    for (unsigned active = 0; active < 2; ++active)
    {
        auto& callers = g_rotation_callers[active];
        using Entry = std::map<std::pair<uint32_t, uint32_t>, uint64_t>::value_type;
        std::vector<const Entry*> ordered;
        for (const auto& entry : callers)
            ordered.push_back(&entry);
        const size_t shown = std::min<size_t>(8, ordered.size());
        std::partial_sort(ordered.begin(), ordered.begin() + shown, ordered.end(),
            [](const Entry* a, const Entry* b) {
                return a->second != b->second ? a->second > b->second : a->first < b->first;
            });
        std::fprintf(stderr, "  [prof rotacao] analogico=%u leituras=%llu locais=%zu\n",
            active, (unsigned long long)g_rotation_pad_reads[active], callers.size());
        for (size_t i = 0; i < shown; ++i)
        {
            const auto& [site, count] = *ordered[i];
            const auto* fn = site.first ? find_containing(site.first) : nullptr;
            std::fprintf(stderr, "  [prof rotacao local] analogico=%u eixo=%c pc_link=%08X funcao=%08X nome=%s chamadas=%llu\n",
                active, (int)site.second, site.first, fn ? fn->address : 0u,
                fn && fn->name ? fn->name : "?", (unsigned long long)count);
        }
        g_rotation_pad_reads[active] = 0;
        callers.clear();
    }
}

extern const bool g_field_clock_trace = [] {
    const char* value = std::getenv("PS2_FIELD_CLOCK_TRACE");
    return value && std::strcmp(value, "1") == 0;
}();

namespace
{
    unsigned g_field_clock_samples = 0;
    unsigned g_field_clock_invalid = 0;
    double g_field_clock_seconds = 0;
}

void field_clock_record(uint32_t delta_bits)
{
    float delta;
    std::memcpy(&delta, &delta_bits, sizeof delta);
    if (std::isfinite(delta) && delta >= 0)
    {
        ++g_field_clock_samples;
        g_field_clock_seconds += delta;
    }
    else
    {
        ++g_field_clock_invalid;
    }
}

static void field_clock_report(double seconds)
{
    if (!g_field_clock_trace)
        return;

    if (seconds > 0)
    {
        const uint64_t mode = g_timer0_mode.load(std::memory_order_relaxed);
        std::fprintf(stderr,
            "  [prof relogio campo] correcao=%u modo=0x%llX clock_hz=%llu amostras=%u invalidas=%u "
            "tempo_jogo_s=%.4f tempo_real_s=%.4f tempo_jogo_por_s=%.3f\n",
            (unsigned)g_gs_timer_clock, (unsigned long long)mode,
            (unsigned long long)timer0_clock_hz(mode), g_field_clock_samples,
            g_field_clock_invalid, g_field_clock_seconds, seconds, g_field_clock_seconds / seconds);
    }
    g_field_clock_samples = g_field_clock_invalid = 0;
    g_field_clock_seconds = 0;
}

extern const bool g_move_callback_trace = [] {
    const char* value = std::getenv("PS2_MOVE_CALLBACK_TRACE");
    return value && std::strcmp(value, "1") == 0;
}();

namespace
{
    struct MoveVector
    {
        double x = 0, z = 0;
        bool valid = false;
    };

    struct MoveCallbackCounts
    {
        unsigned calls = 0, accepted = 0, attempts = 0, max_attempts = 0;
        unsigned retries = 0, exhausted = 0, vector_pairs = 0, position_pairs = 0;
        double in_xz = 0, out_xz = 0, field80_xz = 0;
    };

    // Os callbacks e o resumo de VBlank rodam no EE. Agrupe por modelo e chamador.
    std::map<std::pair<uint32_t, uint32_t>, MoveCallbackCounts> g_move_callbacks;

    MoveVector move_vector_xz(const Ctx& c, uint32_t address)
    {
        const uint32_t physical = ee_phys(address);
        // Leia apenas RAM sem invocar MMIO ou alterar o estado do EE.
        if (!c.mem || physical == 0 || physical > (32u << 20) - 12u)
            return {};
        float x, z;
        std::memcpy(&x, c.mem + physical, sizeof x);
        std::memcpy(&z, c.mem + physical + 8, sizeof z);
        return {x, z, std::isfinite(x) && std::isfinite(z)};
    }

    static void move_callback_report(double seconds)
    {
        if (!g_move_callback_trace)
            return;

        std::fprintf(stderr, "  [prof movimento] janela=%.3fs grupos=%zu\n",
            seconds, g_move_callbacks.size());
        if (seconds > 0)
        {
            for (const auto& [key, stats] : g_move_callbacks)
            {
                std::fprintf(stderr,
                    "  [prof movimento modelo] obj=%08X pc_link=%08X chamadas=%u aceitas=%u recusadas=%u aceitas/s=%.2f "
                    "tentativas=%u max_tentativas=%u chamadas_com_retry=%u limite=%u "
                    "pares_vetor=%u entrada_xz_media=%.4f saida_xz_media=%.4f "
                    "pares_campo80=%u delta_campo80_xz_medio=%.4f campo80_xz/s=%.4f\n",
                    key.first, key.second, stats.calls, stats.accepted, stats.calls - stats.accepted,
                    stats.accepted / seconds, stats.attempts, stats.max_attempts,
                    stats.retries, stats.exhausted, stats.vector_pairs,
                    stats.vector_pairs ? stats.in_xz / stats.vector_pairs : 0.0,
                    stats.vector_pairs ? stats.out_xz / stats.vector_pairs : 0.0,
                    stats.position_pairs,
                    stats.position_pairs ? stats.field80_xz / stats.position_pairs : 0.0,
                    stats.field80_xz / seconds);
            }
        }
        g_move_callbacks.clear();
    }
}

void move_callback_begin(const Ctx& c, MoveCallbackTrace& trace)
{
    trace.vector = (uint32_t)c.r[4].lo;
    trace.object = (uint32_t)c.r[5].lo;
    const uint32_t link = (uint32_t)c.r[31].lo;
    trace.caller = link >= 8 ? link - 8 : 0;

    const MoveVector input = move_vector_xz(c, trace.vector);
    trace.input_valid = input.valid;
    trace.input_length = std::hypot(input.x, input.z);

    // O callback grava a posicao candidata em obj+0x80 por um SQ alinhado.
    const MoveVector position = trace.object
        ? move_vector_xz(c, (trace.object + 0x80u) & ~15u)
        : MoveVector{};
    trace.position_valid = position.valid;
    trace.position_x = position.x;
    trace.position_z = position.z;
}

void move_callback_end(const Ctx& c, const MoveCallbackTrace& trace)
{
    auto& stats = g_move_callbacks[{trace.object, trace.caller}];
    ++stats.calls;
    stats.attempts += trace.attempts;
    stats.max_attempts = std::max(stats.max_attempts, trace.attempts);
    stats.retries += trace.attempts > 1;

    if ((uint32_t)c.r[2].lo != 1)
    {
        stats.exhausted += trace.attempts == 16;
        return;
    }

    ++stats.accepted;
    const MoveVector output = move_vector_xz(c, trace.vector);
    if (trace.input_valid && output.valid)
    {
        ++stats.vector_pairs;
        stats.in_xz += trace.input_length;
        stats.out_xz += std::hypot(output.x, output.z);
    }

    if (trace.position_valid)
    {
        const MoveVector position = move_vector_xz(c, (trace.object + 0x80u) & ~15u);
        if (position.valid)
        {
            ++stats.position_pairs;
            stats.field80_xz += std::hypot(position.x - trace.position_x, position.z - trace.position_z);
        }
    }
}

void ps2_pad(Ctx& c, int function)
{
    int port = (int)(int32_t)c.r[4].lo;
    int slot = (int)(int32_t)c.r[5].lo;
    uint32_t a2 = (uint32_t)c.r[6].lo;
    int a3 = (int)(int32_t)c.r[7].lo;
    int64_t result = 0;

    switch (function)
    {
        case PS2_PAD_INIT: case PS2_PAD_END:
        case PS2_PAD_SET_MAIN_MODE: case PS2_PAD_SET_ACT_ALIGN: case PS2_PAD_SET_ACT_DIRECT:
            result = 1;
            break;

        case PS2_PAD_PORT_OPEN:      // scePadPortOpen(porta, slot, buffer_dma)
            if (port >= 0 && port <= 1 && slot == 0)
            {
                g_pads[port].open = true;
                std::fprintf(stderr, "  scePadPortOpen(porta %d, slot %d)\n", port, slot);
                result = 1;
            }

            break;

        case PS2_PAD_PORT_CLOSE:
            if (port >= 0 && port <= 1)
                g_pads[port].open = false;

            result = 1;
            break;

        case PS2_PAD_GET_STATE:      // 6 = scePadStateStable, 0 = scePadStateDiscon
            result = pad_present(port, slot) ? 6 : 0;
            break;

        case PS2_PAD_READ:           // scePadRead(porta, slot, dados[32]): 1 = ok
        {
            if (!pad_present(port, slot))
                break;

            uint32_t out = a2;

            for (uint32_t i = 0; i < 32; i++)
                mwr<uint8_t>(c, out + i, 0);

            const PadState& pad = g_pads[port];

            mwr<uint8_t>(c, out + 0, 0x00);                         // estado: ok
            mwr<uint8_t>(c, out + 1, 0x73);                         // 7 = analogico (DualShock), 3 = 3 pares de bytes
            mwr<uint8_t>(c, out + 2, (uint8_t)(pad.buttons >> 8));  // botoes, ativos em zero
            mwr<uint8_t>(c, out + 3, (uint8_t)(pad.buttons & 0xFF));

            uint8_t axes[4];
            for (uint32_t i = 0; i < 4; ++i)
            {
                axes[i] = pad.sticks[i];
                mwr<uint8_t>(c, out + 4 + i, axes[i]);
            }

            if (g_rotation_caller_trace)
            {
                g_rotation_pad_active = axes[2] != 0x80 || axes[3] != 0x80;
                ++g_rotation_pad_reads[g_rotation_pad_active];
            }

            if (g_pad_read_trace)
            {
                // Registre os bytes entregues, sem reler a thread de entrada.
                static uint64_t reads = 0;
                static unsigned last_left = 0x10000u;
                const unsigned left = ((unsigned)axes[2] << 8) | axes[3];
                ++reads;
                if (left != last_left)
                {
                    std::fprintf(stderr, "  [pad lido] t_ms=%llu leitura=%llu esq=(%02X,%02X)\n",
                        (unsigned long long)(hw_now_ns() / 1000000u),
                        (unsigned long long)reads, axes[2], axes[3]);
                    last_left = left;
                }
            }

            result = 1;
            break;
        }

        case PS2_PAD_INFO_MODE:      // scePadInfoMode(porta, slot, termo, indice)
        {
            if (!pad_present(port, slot))
                break;

            int term = (int)a2;

            if (term == 1)           // PAD_MODECURID: modo atual (7 = analogico)
                result = 7;
            else if (term == 4)      // PAD_MODETABLE: -1 = quantos modos; senao o id do modo
                result = a3 == -1 ? 2 : (a3 == 0 ? 4 : (a3 == 1 ? 7 : 0));

            break;
        }

        case PS2_PAD_INFO_ACT:       // sem motores de vibracao
            result = 0;
            break;

        default:
            break;
    }

    c.r[2].lo = (uint64_t)result;
}

// ---- DMA (VIF1 / GIF) -> GS: por enquanto so decodifica e registra (PS2_GIFLOG=1), nao desenha ----
// Canais: 1 = VIF1 (0x10009000), 2 = GIF (0x1000A000). Cada kick (CHCR com STR) percorre a cadeia de
// DMAtags na hora e entrega os dados: no GIF direto para o decodificador de GIFtags; no VIF1 passam
// pelo interpretador de VIFcodes (DIRECT/DIRECTHL -> GIF, UNPACK/MPG sao so contados). O resultado e
// o fluxo de escritas em registradores do GS (gs_write), o ponto onde entrara o GS de verdade.
namespace
{
    struct DmaChan { uint32_t madr = 0, qwc = 0, tadr = 0; };
    DmaChan g_dmach[3];
    const bool g_giflog = std::getenv("PS2_GIFLOG") != nullptr;
    const bool g_gsdump = std::getenv("PS2_GSDUMP") != nullptr;   // segundos entre dumps de quadro (padrao 5)
#ifdef _WIN32
    const bool g_gswin = std::getenv("PS2_NOWINDOW") == nullptr;  // janela do jogo (desligue com PS2_NOWINDOW=1)
#else
    const bool g_gswin = false;
#endif
    const bool g_gsrun = g_giflog || g_gsdump || g_gswin;         // decodifica o fluxo de DMA e roda o GS
    unsigned g_giflog_budget = 600;
    unsigned g_dma_detail = 0;
    unsigned g_vif_log = 200, g_gif_log = 80, g_raw_log = 64;
    std::mutex g_gs_mu;

    struct GsStats
    {
        uint64_t xgkicks = 0, xgkick_qw = 0;
        uint64_t kicks[3] = {}, tags_packed = 0, tags_reglist = 0, tags_image = 0, image_qw = 0;
        uint64_t direct_qw = 0, mpg_words = 0, unpack_cmds = 0, mscal = 0;
        uint64_t prim[8] = {};
        uint64_t reg[256] = {};
    };

    GsStats g_gs;
    unsigned g_gs_vsyncs = 0;

    const char* gs_reg_name(unsigned r)
    {
        switch (r)
        {
            case 0x00: return "PRIM";      case 0x01: return "RGBAQ";    case 0x02: return "ST";
            case 0x03: return "UV";        case 0x04: return "XYZF2";    case 0x05: return "XYZ2";
            case 0x06: return "TEX0_1";    case 0x07: return "TEX0_2";   case 0x08: return "CLAMP_1";
            case 0x09: return "CLAMP_2";   case 0x0A: return "FOG";      case 0x0C: return "XYZF3";
            case 0x0D: return "XYZ3";      case 0x14: return "TEX1_1";   case 0x15: return "TEX1_2";
            case 0x16: return "TEX2_1";    case 0x17: return "TEX2_2";   case 0x18: return "XYOFFSET_1";
            case 0x19: return "XYOFFSET_2"; case 0x1A: return "PRMODECONT"; case 0x1B: return "PRMODE";
            case 0x1C: return "TEXCLUT";   case 0x22: return "SCANMSK";  case 0x3B: return "TEXA";
            case 0x3D: return "FOGCOL";    case 0x3F: return "TEXFLUSH"; case 0x40: return "SCISSOR_1";
            case 0x41: return "SCISSOR_2"; case 0x42: return "ALPHA_1";  case 0x43: return "ALPHA_2";
            case 0x44: return "DIMX";      case 0x45: return "DTHE";     case 0x46: return "COLCLAMP";
            case 0x47: return "TEST_1";    case 0x48: return "TEST_2";   case 0x49: return "PABE";
            case 0x4A: return "FBA_1";     case 0x4B: return "FBA_2";    case 0x4C: return "FRAME_1";
            case 0x4D: return "FRAME_2";   case 0x4E: return "ZBUF_1";   case 0x4F: return "ZBUF_2";
            case 0x50: return "BITBLTBUF"; case 0x51: return "TRXPOS";   case 0x52: return "TRXREG";
            case 0x53: return "TRXDIR";    case 0x54: return "HWREG";    case 0x60: return "SIGNAL";
            case 0x61: return "FINISH";    case 0x62: return "LABEL";
            default: return "?";
        }
    }


    // ======== GS por software (esboco): VRAM de 4 MB, transferencias, sprites e triangulos 2D ========
    // Cobre o que as telas atuais usam (sprites com/sem textura, CLUT de 4/8 bits, alpha, scissor).
    // Fora do escopo por enquanto: Z-buffer, mipmap, filtro bilinear, fog, antialiasing, dither.
    uint32_t g_vram[1u << 20];                       // 4 MB
    uint8_t g_blk32[4][8], g_blk16[8][4], g_blk8[4][8], g_blk4[8][4];
    uint8_t g_col32[8][8], g_col16[8][16], g_col8[16][16];

    // Z-buffer: blocos em ordem propria (PSMZ32 / PSMZ16 / PSMZ16S). O jogo usa Z16S (ZBP=140) com teste GEQUAL.
    static const uint8_t g_blk32z[4][8] = { { 24, 25, 28, 29, 8, 9, 12, 13 }, { 26, 27, 30, 31, 10, 11, 14, 15 }, { 16, 17, 20, 21, 0, 1, 4, 5 }, { 18, 19, 22, 23, 2, 3, 6, 7 } };
    static const uint8_t g_blk16z[8][4] = { { 24, 26, 16, 18 }, { 25, 27, 17, 19 }, { 28, 30, 20, 22 }, { 29, 31, 21, 23 }, { 8, 10, 0, 2 }, { 9, 11, 1, 3 }, { 12, 14, 4, 6 }, { 13, 15, 5, 7 } };
    static const uint8_t g_blk16sz[8][4] = { { 24, 26, 8, 10 }, { 25, 27, 9, 11 }, { 16, 18, 0, 2 }, { 17, 19, 1, 3 }, { 28, 30, 12, 14 }, { 29, 31, 13, 15 }, { 20, 22, 4, 6 }, { 21, 23, 5, 7 } };
    uint16_t g_col4[16][32];
    // Deslocamentos dentro de uma pagina. A base e o wrap da VRAM
    // continuam separados para aceitar BP desalinhado e qualquer FBW.
    static uint16_t g_page_ct32[32][64], g_page_z16s[64][64];
    bool g_gs_tables_ready = false;

    void gs_init_tables()
    {
        if (g_gs_tables_ready)
            return;

        g_gs_tables_ready = true;

        static const int b32[8] = { 0, 1, 4, 5, 16, 17, 20, 21 };
        static const int b16[4] = { 0, 2, 8, 10 };
        static const int p32[8] = { 0, 1, 4, 5, 8, 9, 12, 13 };
        static const int p16a[16] = { 0, 2, 8, 10, 16, 18, 24, 26, 1, 3, 9, 11, 17, 19, 25, 27 };
        static const int p8[16] = { 0, 4, 16, 20, 32, 36, 48, 52, 2, 6, 18, 22, 34, 38, 50, 54 };
        static const int p4[8] = { 0, 8, 32, 40, 64, 72, 96, 104 };

        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 8; c++)
            {
                g_blk32[r][c] = (uint8_t)(b32[c] + 2 * (r & 1) + 8 * (r >> 1));
                g_blk8[r][c] = g_blk32[r][c];
            }

        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 4; c++)
            {
                g_blk16[r][c] = (uint8_t)(b16[c] + (r & 1) + 4 * ((r >> 1) & 1) + 16 * (r >> 2));
                g_blk4[r][c] = g_blk16[r][c];
            }

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                g_col32[y][x] = (uint8_t)(p32[x] + 2 * (y & 1) + 16 * (y >> 1));

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 16; x++)
                g_col16[y][x] = (uint8_t)(p16a[x] + 4 * (y & 1) + 32 * (y >> 1));

        for (unsigned y = 0; y < 32; ++y)
            for (unsigned x = 0; x < 64; ++x)
                g_page_ct32[y][x] = (uint16_t)((g_blk32[y >> 3][x >> 3] << 6) + g_col32[y & 7][x & 7]);

        for (unsigned y = 0; y < 64; ++y)
            for (unsigned x = 0; x < 64; ++x)
                g_page_z16s[y][x] = (uint16_t)((g_blk16sz[y >> 3][x >> 4] << 7) + g_col16[y & 7][x & 15]);

        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int v = p8[x];

                if (y & 2) v ^= 33;
                if (y & 1) v += 8;

                switch (y >> 2)
                {
                    case 1: v ^= 96; break;
                    case 2: v += 128; break;
                    case 3: v ^= 224; break;
                    default: break;
                }

                g_col8[y][x] = (uint8_t)v;
            }

        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
            {
                int v = p4[x & 7] + 2 * (x >> 3);

                if (y & 2) v ^= 65;
                if (y & 1) v += 16;

                switch (y >> 2)
                {
                    case 1: v ^= 192; break;
                    case 2: v += 256; break;
                    case 3: v ^= 448; break;
                    default: break;
                }

                g_col4[y][x] = (uint16_t)v;
            }
    }

    // bp vem em blocos de 64 palavras (BP/TBP/CBP). So o bp alinhado a pagina (multiplo de 32) tem (bp & 31) == 0;
    // CLUTs ficam em qualquer bloco, entao o deslocamento dentro da pagina (bp & 31) precisa entrar no endereco.
    inline uint32_t ix_ct32(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)
    {
        uint32_t page = (bp >> 5) + (y >> 5) * bw + (x >> 6);
        return ((page << 11) + ((bp & 31u) << 6) + g_page_ct32[y & 31][x & 63]) & 0xFFFFFu;
    }

    inline uint32_t ix_ct16(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)    // indice de meia-palavra
    {
        uint32_t page = (bp >> 5) + (y >> 6) * bw + (x >> 6);
        return ((page << 12) + ((bp & 31u) << 7) + ((uint32_t)g_blk16[(y >> 3) & 7][(x >> 4) & 3] << 7) + g_col16[y & 7][x & 15]) & 0x1FFFFFu;
    }

    inline uint32_t ix_z32(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)
    {
        uint32_t page = (bp >> 5) + (y >> 5) * bw + (x >> 6);
        return ((page << 11) + ((bp & 31u) << 6) + ((uint32_t)g_blk32z[(y >> 3) & 3][(x >> 3) & 7] << 6) + g_col32[y & 7][x & 7]) & 0xFFFFFu;
    }

    inline uint32_t ix_z16(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)     // indice de meia-palavra
    {
        uint32_t page = (bp >> 5) + (y >> 6) * bw + (x >> 6);
        return ((page << 12) + ((bp & 31u) << 7) + ((uint32_t)g_blk16z[(y >> 3) & 7][(x >> 4) & 3] << 7) + g_col16[y & 7][x & 15]) & 0x1FFFFFu;
    }

    inline uint32_t ix_z16s(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)    // indice de meia-palavra
    {
        uint32_t page = (bp >> 5) + (y >> 6) * bw + (x >> 6);
        return ((page << 12) + ((bp & 31u) << 7) + g_page_z16s[y & 63][x & 63]) & 0x1FFFFFu;
    }

    inline uint32_t ix_t8(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)      // indice de byte
    {
        uint32_t pw = bw >> 1 ? bw >> 1 : 1;
        uint32_t page = (bp >> 5) + (y >> 6) * pw + (x >> 7);
        return ((page << 13) + ((bp & 31u) << 8) + ((uint32_t)g_blk8[(y >> 4) & 3][(x >> 4) & 7] << 8) + g_col8[y & 15][x & 15]) & 0x3FFFFFu;
    }

    inline uint32_t ix_t4(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)      // indice de nibble
    {
        uint32_t pw = bw >> 1 ? bw >> 1 : 1;
        uint32_t page = (bp >> 5) + (y >> 7) * pw + (x >> 7);
        return ((page << 14) + ((bp & 31u) << 9) + ((uint32_t)g_blk4[(y >> 4) & 7][(x >> 5) & 3] << 9) + g_col4[y & 15][x & 31]) & 0x7FFFFFu;
    }

    inline uint8_t* vram8() { return reinterpret_cast<uint8_t*>(g_vram); }

    template <int fixed_psm = -1>
    uint32_t gs_read_px(uint32_t runtime_psm, uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)
    {
        const uint32_t psm = fixed_psm >= 0 ? (uint32_t)fixed_psm : runtime_psm;
        switch (psm)
        {
            case 0x00: return g_vram[ix_ct32(bp, bw, x, y)];
            case 0x01: return g_vram[ix_ct32(bp, bw, x, y)] & 0xFFFFFFu;
            case 0x02: case 0x0A:
            {
                uint16_t v;
                std::memcpy(&v, vram8() + (size_t)ix_ct16(bp, bw, x, y) * 2, 2);
                return v;
            }
            case 0x30: return g_vram[ix_z32(bp, bw, x, y)];
            case 0x31: return g_vram[ix_z32(bp, bw, x, y)] & 0xFFFFFFu;
            case 0x32: case 0x3A:
            {
                uint16_t v;
                std::memcpy(&v, vram8() + (size_t)(psm == 0x32 ? ix_z16(bp, bw, x, y) : ix_z16s(bp, bw, x, y)) * 2, 2);
                return v;
            }
            case 0x13: return vram8()[ix_t8(bp, bw, x, y)];
            case 0x14:
            {
                uint32_t n = ix_t4(bp, bw, x, y);
                return (vram8()[n >> 1] >> ((n & 1) * 4)) & 0xF;
            }
            case 0x1B: return g_vram[ix_ct32(bp, bw, x, y)] >> 24;
            case 0x24: return (g_vram[ix_ct32(bp, bw, x, y)] >> 24) & 0xF;
            case 0x2C: return (g_vram[ix_ct32(bp, bw, x, y)] >> 28) & 0xF;
            default: return 0;
        }
    }

    void gs_write_px(uint32_t psm, uint32_t bp, uint32_t bw, uint32_t x, uint32_t y, uint32_t v)
    {
        switch (psm)
        {
            case 0x00: g_vram[ix_ct32(bp, bw, x, y)] = v; break;
            case 0x01:
            {
                uint32_t& d = g_vram[ix_ct32(bp, bw, x, y)];
                d = (d & 0xFF000000u) | (v & 0xFFFFFFu);
                break;
            }
            case 0x02: case 0x0A:
            {
                uint16_t h = (uint16_t)v;
                std::memcpy(vram8() + (size_t)ix_ct16(bp, bw, x, y) * 2, &h, 2);
                break;
            }
            case 0x30: g_vram[ix_z32(bp, bw, x, y)] = v; break;
            case 0x31:
            {
                uint32_t& d = g_vram[ix_z32(bp, bw, x, y)];
                d = (d & 0xFF000000u) | (v & 0xFFFFFFu);
                break;
            }
            case 0x32: case 0x3A:
            {
                uint16_t h = (uint16_t)v;
                std::memcpy(vram8() + (size_t)(psm == 0x32 ? ix_z16(bp, bw, x, y) : ix_z16s(bp, bw, x, y)) * 2, &h, 2);
                break;
            }
            case 0x13: vram8()[ix_t8(bp, bw, x, y)] = (uint8_t)v; break;
            case 0x14:
            {
                uint32_t n = ix_t4(bp, bw, x, y);
                uint8_t& b = vram8()[n >> 1];
                b = (n & 1) ? (uint8_t)((b & 0x0F) | ((v & 0xF) << 4)) : (uint8_t)((b & 0xF0) | (v & 0xF));
                break;
            }
            case 0x1B:
            {
                uint32_t& d = g_vram[ix_ct32(bp, bw, x, y)];
                d = (d & 0x00FFFFFFu) | ((v & 0xFF) << 24);
                break;
            }
            case 0x24:
            {
                uint32_t& d = g_vram[ix_ct32(bp, bw, x, y)];
                d = (d & 0xF0FFFFFFu) | ((v & 0xF) << 24);
                break;
            }
            case 0x2C:
            {
                uint32_t& d = g_vram[ix_ct32(bp, bw, x, y)];
                d = (d & 0x0FFFFFFFu) | ((v & 0xF) << 28);
                break;
            }
            default: break;
        }
    }

    struct GsCtx
    {
        uint64_t tex0 = 0, tex1 = 0, clamp = 0, alpha = 0, test = 0, frame = 0, zbuf = 0, xyoffset = 0, scissor = 0;
    };

    struct GsVertex
    {
        float x = 0, y = 0, r = 0, g = 0, b = 0, a = 0, u = 0, v = 0;
        float us = 0, vs = 0, q = 1;   // S*largura, T*altura e Q crus (modo ST): interpolados e divididos por pixel
        uint32_t z = 0;                // Z do vertice (24 bits em XYZF, 32 em XYZ)
        bool persp = false;
    };

    struct GsState
    {
        GsCtx ctx[2];
        uint64_t prim = 0, rgbaq = 0x3F80000000000000ull, st = 0, uv = 0, texa = 0, colclamp = 1;
        uint64_t bitbltbuf = 0, trxpos = 0, trxreg = 0, trxdir = 3;
        // Incrementado a cada TRXDIR para nao reutilizar um download GS antigo.
        uint64_t trx_generation = 0;
        GsVertex q[3];
        int nq = 0;
        uint32_t clut[512] = {};   // buffer de CLUT do GS: 256 cores de 32 bits ou 512 de 16 bits
        uint64_t clut_tex0 = ~0ull;

        // transferencia host -> local em andamento
        bool xfer = false;
        uint32_t x = 0, y = 0, x0 = 0, rrw = 0, rrh = 0, dpsm = 0, dbp = 0, dbw = 0;
        uint8_t pend[32] = {};
        unsigned npend = 0;
        uint64_t unsupported = 0;
    };

    GsState g_st;
    void gs_vk_flush();
    bool gs_vk_scene_sprite(int ci, const GsVertex& a, const GsVertex& b);
    void gs_vk_hi_reset(uint32_t fbp);
    void gs_vk_palette_barrier(uint32_t bp);

    // Registradores privilegiados (para saber qual area esta sendo exibida)
    uint64_t g_gs_priv[0x100 / 8];

    inline float gs_clampf(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

    uint32_t gs_clut_color(uint32_t cpsm, uint32_t v)
    {
        if (cpsm == 0x00)
            return v;

        if (cpsm == 0x01)
            return (v & 0xFFFFFFu) | 0x80000000u;

        uint32_t r = (v & 31) << 3, g = ((v >> 5) & 31) << 3, b = ((v >> 10) & 31) << 3;
        return r | (g << 8) | (b << 16) | ((v & 0x8000) ? 0x80000000u : 0u);
    }

    // Origem (cbp+1) de cada grupo de 16 entradas do buffer de CLUT; 0 = nunca carregado, 0xFFFFFFFF = formato fora do CT32.
    uint32_t g_clut_prov[32] = {};
    uint32_t g_clutfix_reloads = 0;
    const bool g_clutfix = std::getenv("PS2_NOCLUTFIX") == nullptr;

    void gs_load_clut(uint64_t tex0)
    {
        gs_vk_palette_barrier(uint32_t((tex0 >> 37) & 0x3FFFu));
        uint32_t psm = (uint32_t)((tex0 >> 20) & 0x3F);
        uint32_t cbp = (uint32_t)((tex0 >> 37) & 0x3FFF);
        uint32_t cpsm = (uint32_t)((tex0 >> 51) & 0xF);
        uint32_t csa = (uint32_t)((tex0 >> 56) & 0x1F);
        bool is4 = psm == 0x14 || psm == 0x24 || psm == 0x2C;
        unsigned n = is4 ? 16 : 256;

        for (unsigned i = 0; i < n; i++)
        {
            uint32_t j = i;

            if (!is4)
                j = (i & 0xE7u) | ((i & 0x08u) << 1) | ((i & 0x10u) >> 1);   // CSM1: bits 3 e 4 trocados

            uint32_t entry = is4 ? i : j;   // memoria: sempre a partir do CBP (o CSA nao desloca a leitura)
            uint32_t x, y, raw;

            if (cpsm == 0x00 || cpsm == 0x01)
            {
                x = entry & 7;
                y = entry >> 3;

                if (!is4) { x = entry & 15; y = entry >> 4; }

                raw = g_vram[ix_ct32(cbp, 1, x, y)];
            }
            else
            {
                x = entry & 15;
                y = entry >> 4;
                uint16_t h;
                std::memcpy(&h, vram8() + (size_t)ix_ct16(cbp, 1, x, y) * 2, 2);
                raw = h;
            }

            g_st.clut[is4 ? ((csa * 16 + i) & 0x1FFu) : i] = gs_clut_color(cpsm, raw);   // CSA = posicao no buffer
        }

        const uint32_t prov = (cpsm == 0x00 || cpsm == 0x01) ? cbp + 1u : 0xFFFFFFFFu;

        if (is4)
            g_clut_prov[csa & 31u] = prov;
        else
            for (int g = 0; g < 16; g++)
                g_clut_prov[g] = prov;
    }

    // CLD=0 manda usar a CLUT "ja carregada", mas o buffer e unico e outras cargas (8 bits, ou 4 bits em CSA 0) o sobrescrevem.
    // Se o desenho aponta um CBP diferente do que o grupo realmente contem, recarrega da VRAM (PS2_NOCLUTFIX=1 desliga).
    void gs_clut_ensure(uint64_t tex0)
    {
        const uint32_t psm = (uint32_t)((tex0 >> 20) & 0x3F), cpsm = (uint32_t)((tex0 >> 51) & 0xF);

        if (!g_clutfix || (cpsm != 0x00 && cpsm != 0x01))
            return;

        const bool is4 = psm == 0x14 || psm == 0x24 || psm == 0x2C, is8 = psm == 0x13 || psm == 0x1B;

        if (!is4 && !is8)
            return;

        const uint32_t cbp = (uint32_t)((tex0 >> 37) & 0x3FFF), csa = (uint32_t)((tex0 >> 56) & 0x1F), want = cbp + 1u;
        bool stale = false;

        if (is4)
            stale = g_clut_prov[csa & 31u] != want;
        else
            for (int g = 0; g < 16 && !stale; g++)
                stale = g_clut_prov[g] != want;

        if (!stale)
            return;

        if (g_clutfix_reloads++ < 6)
            std::fprintf(stderr, "  [clut] recarga: psm=0x%02X cbp=%u csa=%u (o buffer tinha outra origem)\n", psm, cbp, csa);

        // Recarrega as 256 entradas (imagem 16x16 de CT32 no CBP): a CLUT da fonte guarda uma paleta de 16 cores por CSA
        // (branco em CSA 0, ciano em CSA 1...). Os 16 primeiros itens coincidem com a carga de 4 bits.
        gs_load_clut((tex0 & ~(0x3Full << 20) & ~(0x1Full << 56)) | (0x13ull << 20));
    }

    // Cor do texel (RGBA 8 bits; alpha 0x80 = opaco como no GS)
    // floor/ceil sem chamar a libm (a chamada por pixel era o maior custo do rasterizador). Fora de +-1e9 devolvem 0.
    static inline int fast_floor(float f)
    {
        if (!(f > -1e9f && f < 1e9f))
            return 0;

        const int i = (int)f;
        return (float)i > f ? i - 1 : i;
    }

    static inline int fast_ceil(float f)
    {
        if (!(f > -1e9f && f < 1e9f))
            return 0;

        const int i = (int)f;
        return (float)i < f ? i + 1 : i;
    }

    // Parametros da textura decodificados uma vez por primitiva (o TEX0/CLAMP/TEX1 nao mudam dentro dela).
    struct TexInfo
    {
        uint32_t tbp = 0, tbw = 0, psm = 0, csa16 = 0, a0 = 0, a1 = 0;
        int w = 1, h = 1, uminv = 0, umaxv = 0, vminv = 0, vmaxv = 0;
        unsigned umode = 0, vmode = 0, tfx = 0;
        bool tcc = false, aem = false, linear = false;
        // Teste O: cobertura vertical de dois texels somente para a fonte do menu.
        // Nao combina quadros/campos e permanece desativado por padrao.
        bool ui_vpair = false;
        int ui_vfirst = 0, ui_vspan = 0;
    };

    void gs_tex_info(const GsCtx& c, TexInfo& t)
    {
        gs_clut_ensure(c.tex0);

        t.tbp = (uint32_t)(c.tex0 & 0x3FFF);
        t.tbw = (uint32_t)((c.tex0 >> 14) & 0x3F);
        t.psm = (uint32_t)((c.tex0 >> 20) & 0x3F);
        t.w = 1 << (int)((c.tex0 >> 26) & 0xF);
        t.h = 1 << (int)((c.tex0 >> 30) & 0xF);
        t.umode = (unsigned)(c.clamp & 3);
        t.vmode = (unsigned)((c.clamp >> 2) & 3);
        t.uminv = (int)((c.clamp >> 4) & 0x3FF);
        t.umaxv = (int)((c.clamp >> 14) & 0x3FF);
        t.vminv = (int)((c.clamp >> 24) & 0x3FF);
        t.vmaxv = (int)((c.clamp >> 34) & 0x3FF);
        t.csa16 = ((uint32_t)(c.tex0 >> 56) & 0x1Fu) * 16u;
        t.tcc = ((c.tex0 >> 34) & 1) != 0;
        t.tfx = (unsigned)((c.tex0 >> 35) & 3);
        t.a0 = (uint32_t)(g_st.texa & 0xFF);
        t.a1 = (uint32_t)((g_st.texa >> 32) & 0xFF);
        t.aem = (g_st.texa & 0x8000) != 0;

        const unsigned mmag = (unsigned)((c.tex1 >> 5) & 1), mmin = (unsigned)((c.tex1 >> 6) & 7);
        static const bool nobilinear = std::getenv("PS2_NOBILINEAR") != nullptr;
        t.linear = !nobilinear && (mmag != 0 || mmin == 1 || mmin == 4 || mmin == 5);
    }

    static inline int gs_tex_wrap(int v, int size, unsigned mode, int minv, int maxv)
    {
        switch (mode)
        {
            case 0: return v & (size - 1);
            case 1: return v < 0 ? 0 : (v >= size ? size - 1 : v);
            case 2: return v < minv ? minv : (v > maxv ? maxv : v);
            default: return (v & minv) | maxv;
        }
    }

    template <int fixed_psm = -1>
    static inline uint32_t gs_texel_wrapped(const TexInfo& t, int iu, int iv)
    {
        const uint32_t raw = gs_read_px<fixed_psm>(t.psm, t.tbp, t.tbw, (uint32_t)iu, (uint32_t)iv);

        switch (fixed_psm >= 0 ? (uint32_t)fixed_psm : t.psm)
        {
            case 0x00: return raw;
            case 0x01:
            {
                uint32_t a = t.a0;

                if (t.aem && (raw & 0xFFFFFF) == 0)
                    a = 0;

                return (raw & 0xFFFFFFu) | (a << 24);
            }
            case 0x02: case 0x0A:
            {
                uint32_t r = (raw & 31) << 3, g = ((raw >> 5) & 31) << 3, b = ((raw >> 10) & 31) << 3;
                uint32_t a = (raw & 0x8000) ? t.a1 : t.a0;

                if (t.aem && (raw & 0x7FFF) == 0)
                    a = 0;

                return r | (g << 8) | (b << 16) | (a << 24);
            }
            case 0x13: case 0x1B: return g_st.clut[raw & 0xFF];
            case 0x14: case 0x24: case 0x2C: return g_st.clut[(t.csa16 + (raw & 0xFu)) & 0x1FFu];
            default: return 0xFF00FF | 0x80000000u;   // formato nao suportado: magenta
        }
    }

    template <int fixed_psm = -1>
    uint32_t gs_texel(const TexInfo& t, int iu, int iv)
    {
        return gs_texel_wrapped<fixed_psm>(t,
            gs_tex_wrap(iu, t.w, t.umode, t.uminv, t.umaxv),
            gs_tex_wrap(iv, t.h, t.vmode, t.vminv, t.vmaxv));
    }

    // Os pesos somam 65536. Cada canal acumulado cabe em 24 bits,
    // entao podemos calcular R/B e G/A em pares com faixas de 32 bits.
    static inline uint32_t gs_bilinear(uint32_t t00, uint32_t t10, uint32_t t01, uint32_t t11, int wu, int wv)
    {
        const unsigned w00 = (256 - wu) * (256 - wv), w10 = wu * (256 - wv);
        const unsigned w01 = (256 - wu) * wv, w11 = wu * wv;
        auto pair = [&](uint32_t shift)
        {
            auto lanes = [shift](uint32_t color) -> uint64_t
            {
                color >>= shift;
                return (color & 0xFFu) | ((uint64_t)(color & 0xFF0000u) << 16);
            };
            const uint64_t sum = lanes(t00) * w00 + lanes(t10) * w10
                + lanes(t01) * w01 + lanes(t11) * w11;
            return (uint32_t)(((sum >> 16) & 0xFFu) | ((sum >> 32) & 0xFF0000u));
        };
        return pair(0) | (pair(8) << 8);
    }

    // Filtra os quatro canais de um pixel juntos. A reducao para bytes
    // ocorre so depois das duas interpolacoes, como no calculo escalar.
    static inline uint32_t gs_sprite_bilinear(uint32_t t00, uint32_t t10,
                                             uint32_t t01, uint32_t t11, int wu, int wv)
    {
#if defined(__SSE2__) || defined(_M_X64)
        const __m128i zero = _mm_setzero_si128();
        const __m128i left = _mm_set_epi32(0, 0, (int)t01, (int)t00);
        const __m128i right = _mm_set_epi32(0, 0, (int)t11, (int)t10);
        const __m128i pairs = _mm_unpacklo_epi8(left, right);
        const __m128i weights_u = _mm_set1_epi32((wu << 16) | (256 - wu));
        const __m128i bias = _mm_set1_epi32(32768);
        const __m128i top = _mm_sub_epi32(_mm_madd_epi16(_mm_unpacklo_epi8(pairs, zero), weights_u), bias);
        const __m128i bottom = _mm_sub_epi32(_mm_madd_epi16(_mm_unpackhi_epi8(pairs, zero), weights_u), bias);
        // O bias permite guardar os intermediarios de 16 bits como
        // valores com sinal. Recupera 128 depois do deslocamento final.
        const __m128i horizontal = _mm_packs_epi32(top, bottom);
        const __m128i rows = _mm_unpacklo_epi16(horizontal, _mm_srli_si128(horizontal, 8));
        const __m128i weights_v = _mm_set1_epi32((wv << 16) | (256 - wv));
        const __m128i vertical = _mm_madd_epi16(rows, weights_v);
        const __m128i result = _mm_add_epi32(_mm_srai_epi32(vertical, 16), _mm_set1_epi32(128));
        return (uint32_t)_mm_cvtsi128_si32(_mm_packus_epi16(_mm_packs_epi32(result, zero), zero));
#else
        return gs_bilinear(t00, t10, t01, t11, wu, wv);
#endif
    }

    // Amostra a textura em (u, v) (coordenadas em texels): vizinho mais proximo ou bilinear (TEX1.MMAG/MMIN).
    template <int fixed_psm = -1, bool linear = false>
    uint32_t gs_sample(const TexInfo& t, float u, float v)
    {
        // Teste O: quando uma fonte de 19 linhas e comprimida a 9,5 pixels no GS,
        // o modo nearest escolhe so uma das duas linhas do glifo (pares OU impares).
        // Calculamos a media das duas linhas do par no mesmo atlas. E um teste
        // visual, NAO uma emulacao fiel da recomposicao de campos do PS2.
        // Restrito aos sprites de texto marcados por gs_draw_sprite().
        if (fixed_psm < 0 && t.ui_vpair && t.ui_vspan > 0)
        {
            const int tex_u = fast_floor(u);
            const int rel_v = fast_floor((v - (float)t.ui_vfirst) * 0.5f);
            const int start = std::max(0, std::min(t.ui_vspan - 1, rel_v * 2));
            const int next = std::min(t.ui_vspan - 1, start + 1);
            const uint32_t a = gs_texel<fixed_psm>(t, tex_u, t.ui_vfirst + start);
            const uint32_t b = gs_texel<fixed_psm>(t, tex_u, t.ui_vfirst + next);
            // Media sem contaminacao entre canais RGBA, inclusive o alfa.
            return ((a & 0xFEFEFEFEu) >> 1)
                 + ((b & 0xFEFEFEFEu) >> 1)
                 + (a & b & 0x01010101u);
        }

        if (!(fixed_psm < 0 ? t.linear : linear))
            return gs_texel<fixed_psm>(t, (int)fast_floor(u), (int)fast_floor(v));

        const float fu = u - 0.5f, fv = v - 0.5f;
        const int iu = (int)fast_floor(fu), iv = (int)fast_floor(fv);
        const int wu = (int)((fu - (float)iu) * 256.0f), wv = (int)((fv - (float)iv) * 256.0f);

        if (wu < 4 && wv < 4)    // alinhado ao texel (mapeamento 1:1): sem mistura
            return gs_texel<fixed_psm>(t, iu, iv);

        // Aplica CLAMP/REPEAT uma vez por coordenada e reutiliza texels na borda.
        const int x0 = gs_tex_wrap(iu, t.w, t.umode, t.uminv, t.umaxv);
        const int x1 = gs_tex_wrap(iu + 1, t.w, t.umode, t.uminv, t.umaxv);
        const int y0 = gs_tex_wrap(iv, t.h, t.vmode, t.vminv, t.vmaxv);
        const int y1 = gs_tex_wrap(iv + 1, t.h, t.vmode, t.vminv, t.vmaxv);
        const uint32_t t00 = gs_texel_wrapped<fixed_psm>(t, x0, y0);
        const uint32_t t10 = x1 == x0 ? t00 : gs_texel_wrapped<fixed_psm>(t, x1, y0);
        const uint32_t t01 = y1 == y0 ? t00 : gs_texel_wrapped<fixed_psm>(t, x0, y1);
        const uint32_t t11 = y1 == y0 ? t10 : (x1 == x0 ? t01 : gs_texel_wrapped<fixed_psm>(t, x1, y1));

        if ((unsigned)wu <= 256u && (unsigned)wv <= 256u)
            return gs_bilinear(t00, t10, t01, t11, wu, wv);

        // Mantem o calculo legado para coordenadas fora da faixa de fast_floor.
        uint32_t out = 0;

        for (int sh = 0; sh < 32; sh += 8)
        {
            const int c00 = (int)((t00 >> sh) & 0xFF), c10 = (int)((t10 >> sh) & 0xFF), c01 = (int)((t01 >> sh) & 0xFF), c11 = (int)((t11 >> sh) & 0xFF);
            const int top = c00 * (256 - wu) + c10 * wu, bot = c01 * (256 - wu) + c11 * wu;
            out |= (uint32_t)(((top * (256 - wv) + bot * wv) >> 16) & 0xFF) << sh;
        }

        return out;
    }

    using GsTextureSampler = uint32_t (*)(const TexInfo&, float, float);

    template <int psm>
    static GsTextureSampler gs_tri_sampler_for(bool linear)
    {
        return linear ? &gs_sample<psm, true> : &gs_sample<psm, false>;
    }

    // Seleciona PSM e filtro por triangulo. Cada texel continua a ser lido
    // da VRAM em ordem, inclusive quando o destino tambem e a textura.
    static GsTextureSampler gs_tri_sampler(const TexInfo& t)
    {
        if (t.ui_vpair)
            return nullptr;
        switch (t.psm)
        {
            case 0x00: return gs_tri_sampler_for<0x00>(t.linear);
            case 0x01: return gs_tri_sampler_for<0x01>(t.linear);
            case 0x02: case 0x0A: return gs_tri_sampler_for<0x02>(t.linear);
            case 0x13: return gs_tri_sampler_for<0x13>(t.linear);
            case 0x14: return gs_tri_sampler_for<0x14>(t.linear);
            case 0x1B: return gs_tri_sampler_for<0x1B>(t.linear);
            case 0x24: return gs_tri_sampler_for<0x24>(t.linear);
            case 0x2C: return gs_tri_sampler_for<0x2C>(t.linear);
            default: return nullptr;
        }
    }

    // Estado do pixel decodificado uma vez por primitiva (antes era refeito a cada pixel).
    struct PixCfg
    {
        int sx0 = 0, sx1 = 0, sy0 = 0, sy1 = 0;
        bool tme = false, ate = false, abe = false, is16 = false, need_dst = false, clamp = true;
        unsigned atst = 1, afail = 0, A = 0, B = 0, C = 0, D = 0;
        int aref = 0, fix = 0;
        uint32_t fbp = 0, fbw = 0, fpsm = 0, fbmsk = 0;
        // Z-buffer: ZBUF (ZBP, PSM, ZMSK) + TEST.ZTE/ZTST. zactive = precisa ler e/ou escrever Z.
        bool zte = false, zmsk = true, zactive = false;
        unsigned ztst = 1;
        uint32_t zbp = 0, zpsm = 0x3A, zmax = 0xFFFF;
        TexInfo ti;
    };

    static inline bool gs_alpha_pass(const PixCfg& pc, int alpha)
    {
        if (!pc.ate) return true;
        switch (pc.atst)
        {
            case 0: return false;
            case 2: return alpha < pc.aref;
            case 3: return alpha <= pc.aref;
            case 4: return alpha == pc.aref;
            case 5: return alpha >= pc.aref;
            case 6: return alpha > pc.aref;
            case 7: return alpha != pc.aref;
            default: return true;
        }
    }

    void gs_pix_cfg(int ci, PixCfg& p)
    {
        const GsCtx& c = g_st.ctx[ci];
        p.sx0 = std::max(0, (int)(c.scissor & 0x7FF)); p.sx1 = (int)((c.scissor >> 16) & 0x7FF);
        p.sy0 = std::max(0, (int)((c.scissor >> 32) & 0x7FF)); p.sy1 = (int)((c.scissor >> 48) & 0x7FF);
        p.tme = (g_st.prim & 0x10) != 0;
        p.abe = (g_st.prim & 0x40) != 0;
        p.ate = (c.test & 1) != 0;
        p.atst = (unsigned)((c.test >> 1) & 7);
        p.aref = (int)((c.test >> 4) & 0xFF);
        p.afail = (unsigned)((c.test >> 12) & 3);
        p.fbp = (uint32_t)(c.frame & 0x1FF) * 32; p.fbw = (uint32_t)((c.frame >> 16) & 0x3F); p.fpsm = (uint32_t)((c.frame >> 24) & 0x3F);
        p.fbmsk = (uint32_t)(c.frame >> 32);
        p.is16 = p.fpsm == 0x02 || p.fpsm == 0x0A;
        p.A = (unsigned)(c.alpha & 3); p.B = (unsigned)((c.alpha >> 2) & 3); p.C = (unsigned)((c.alpha >> 4) & 3); p.D = (unsigned)((c.alpha >> 6) & 3);
        p.fix = (int)((c.alpha >> 32) & 0xFF);
        p.clamp = (g_st.colclamp & 1) != 0;
        p.need_dst = p.abe || p.fbmsk != 0 || (p.ate && p.afail == 3) || p.fpsm == 0x01;   // o destino so e lido se influencia o resultado

        p.zte = ((c.test >> 16) & 1) != 0;
        p.ztst = (unsigned)((c.test >> 17) & 3);
        p.zbp = (uint32_t)(c.zbuf & 0x1FF) * 32;
        p.zpsm = 0x30u | (uint32_t)((c.zbuf >> 24) & 0xF);
        p.zmsk = ((c.zbuf >> 32) & 1) != 0;
        p.zmax = p.zpsm == 0x30 ? 0xFFFFFFFFu : (p.zpsm == 0x31 ? 0xFFFFFFu : 0xFFFFu);
        p.zactive = p.zte && (p.ztst != 1 || !p.zmsk);   // ALWAYS com ZMSK=1 nao le nem escreve nada

        if (p.tme)
            gs_tex_info(c, p.ti);
    }

    static inline int gs_clamp255(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
    static inline int gs_f2i255(float v) { return v <= 0.0f ? 0 : (v >= 255.0f ? 255 : (int)v); }   // limita antes de converter (sem estouro de int)

    static inline uint32_t gs_zread(const PixCfg& pc, uint32_t x, uint32_t y) { return gs_read_px(pc.zpsm, pc.zbp, pc.fbw, x, y); }
    static inline void gs_zwrite(const PixCfg& pc, uint32_t x, uint32_t y, uint32_t z) { gs_write_px(pc.zpsm, pc.zbp, pc.fbw, x, y, z & pc.zmax); }

    static inline bool gs_zpass(const PixCfg& pc, uint32_t x, uint32_t y, uint32_t z)
    {
        const uint32_t zt = z & pc.zmax;

        if (pc.ztst >= 2)
        {
            const uint32_t zo = gs_zread(pc, x, y) & pc.zmax;

            if (pc.ztst == 2 ? !(zt >= zo) : !(zt > zo))
                return false;
        }
        else if (pc.ztst == 0)
        {
            return false;
        }

        return true;
    }

    // O caminho por linhas continua usando teste e escrita juntos.
    static inline bool gs_ztest(const PixCfg& pc, uint32_t x, uint32_t y, uint32_t z, bool z_write_ok)
    {
        if (!gs_zpass(pc, x, y, z))
            return false;

        if (z_write_ok && !pc.zmsk)
            gs_zwrite(pc, x, y, z);

        return true;
    }

    // Etapa comum depois do scissor e do teste de leitura de Z. A escrita
    // de profundidade continua depois de AFAIL, inclusive com alias de VRAM.
    template <bool source_alpha_fast = false, bool direct_ct32 = false>
#if defined(__GNUC__)
    __attribute__((always_inline))
#endif
    static inline void gs_shade_pixel(const PixCfg& pc, int x, int y,
        int vr, int vg, int vb, int va, uint32_t texel, uint32_t z, uint32_t addr = 0, uint8_t* z16s = nullptr)
    {
        int r = vr, g = vg, b = vb, a = va;

        if (pc.tme)
        {
            const uint32_t t = texel;
            const int tr = (int)(t & 0xFF), tg = (int)((t >> 8) & 0xFF), tb = (int)((t >> 16) & 0xFF), ta = (int)((t >> 24) & 0xFF);
            const bool tcc = pc.ti.tcc;

            switch (pc.ti.tfx)
            {
                case 0:   // MODULATE
                    r = std::min(255, tr * vr >> 7); g = std::min(255, tg * vg >> 7); b = std::min(255, tb * vb >> 7);
                    a = tcc ? std::min(255, ta * va >> 7) : va;
                    break;
                case 1:   // DECAL
                    r = tr; g = tg; b = tb; a = tcc ? ta : va;
                    break;
                default:  // HIGHLIGHT / HIGHLIGHT2
                    r = std::min(255, (tr * vr >> 7) + va); g = std::min(255, (tg * vg >> 7) + va); b = std::min(255, (tb * vb >> 7) + va);
                    a = (pc.ti.tfx == 2 && tcc) ? std::min(255, ta + va) : (tcc ? ta : va);
                    break;
            }
        }

        bool afail_rgb_only = false, fb_ok = true, z_write_ok = true;

        if (!gs_alpha_pass(pc, a))
        {
            // AFAIL: 0 KEEP (nada), 1 FB_ONLY (cor, sem Z), 2 ZB_ONLY (so Z), 3 RGB_ONLY (RGB, sem alfa e sem Z)
            switch (pc.afail)
            {
                case 0: return;
                case 1: z_write_ok = false; break;
                case 2: fb_ok = false; break;
                default: z_write_ok = false; afail_rgb_only = true; break;
            }
        }

        if (pc.zactive && z_write_ok && !pc.zmsk)
        {
            if (direct_ct32 && z16s != nullptr)
            {
                // O teste de profundidade ja resolveu este endereco.
                // Escreve depois do AFAIL e antes de ler a cor de destino.
                const uint16_t depth = (uint16_t)z;
                std::memcpy(z16s, &depth, sizeof(depth));
            }
            else
                gs_zwrite(pc, (uint32_t)x, (uint32_t)y, z);
        }

        if (!fb_ok)
            return;

        uint32_t dst = 0;
        int dr = 0, dg = 0, db = 0, da = 0;

        if (pc.need_dst)
        {
            dst = direct_ct32 ? g_vram[addr] : gs_read_px(pc.fpsm, pc.fbp, pc.fbw, (uint32_t)x, (uint32_t)y);

            if (pc.is16)
            {
                dr = (int)((dst & 31) << 3); dg = (int)(((dst >> 5) & 31) << 3); db = (int)(((dst >> 10) & 31) << 3);
                da = (dst & 0x8000) ? 0x80 : 0;
            }
            else
            {
                dr = (int)(dst & 0xFF); dg = (int)((dst >> 8) & 0xFF); db = (int)((dst >> 16) & 0xFF); da = (int)((dst >> 24) & 0xFF);
            }
        }

        if (pc.abe)   // (A - B) * C >> 7 + D
        {
            auto pick = [](unsigned sel, int cs, int cd) { return sel == 0 ? cs : (sel == 1 ? cd : 0); };
            const int cf = pc.C == 0 ? a : (pc.C == 1 ? da : pc.fix);
            int nr, ng, nb;
            if (source_alpha_fast && pc.A == 0 && pc.B == 1 && pc.C == 0 && pc.D == 1)
            {
                nr = ((r - dr) * a >> 7) + dr;
                ng = ((g - dg) * a >> 7) + dg;
                nb = ((b - db) * a >> 7) + db;
            }
            else
            {
                nr = (((pick(pc.A, r, dr) - pick(pc.B, r, dr)) * cf) >> 7) + pick(pc.D, r, dr);
                ng = (((pick(pc.A, g, dg) - pick(pc.B, g, dg)) * cf) >> 7) + pick(pc.D, g, dg);
                nb = (((pick(pc.A, b, db) - pick(pc.B, b, db)) * cf) >> 7) + pick(pc.D, b, db);
            }

            if (pc.clamp) { r = gs_clamp255(nr); g = gs_clamp255(ng); b = gs_clamp255(nb); }
            else { r = nr & 0xFF; g = ng & 0xFF; b = nb & 0xFF; }
        }

        uint32_t out = pc.is16 ? (uint32_t)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10) | ((a >= 0x80) ? 0x8000 : 0))
                               : (uint32_t)(r | (g << 8) | (b << 16) | (a << 24));

        if (afail_rgb_only)   // AFAIL = RGB_ONLY: o alfa do destino e mantido
            out = pc.is16 ? ((out & 0x7FFFu) | (dst & 0x8000u)) : ((out & 0x00FFFFFFu) | (dst & 0xFF000000u));

        if (pc.fbmsk != 0)
            out = (dst & pc.fbmsk) | (out & ~pc.fbmsk);

        if (direct_ct32)
            g_vram[addr] = out;
        else if (pc.fpsm == 0x00)
            g_vram[ix_ct32(pc.fbp, pc.fbw, (uint32_t)x, (uint32_t)y)] = out;
        else
            gs_write_px(pc.fpsm, pc.fbp, pc.fbw, (uint32_t)x, (uint32_t)y, out);
    }

    // prechecked indica que o chamador ja verificou scissor e Z.
    template <bool prechecked = false>
    void gs_pixel(const PixCfg& pc, int x, int y, float fr, float fg, float fb, float fa, float u, float v, uint32_t z = 0)
    {
        if (!prechecked && (x < pc.sx0 || x > pc.sx1 || y < pc.sy0 || y > pc.sy1))
            return;
        if (!prechecked && pc.zactive && !gs_zpass(pc, (uint32_t)x, (uint32_t)y, z))
            return;

        gs_shade_pixel(pc, x, y, gs_f2i255(fr), gs_f2i255(fg), gs_f2i255(fb), gs_f2i255(fa),
            pc.tme ? gs_sample(pc.ti, u, v) : 0, z);
    }

    // Versao avulsa (pontos): decodifica o estado a cada chamada.
    void gs_pixel(int ci, int x, int y, float fr, float fg, float fb, float fa, float u, float v, uint32_t z = 0)
    {
        gs_vk_flush();
        PixCfg pc;
        gs_pix_cfg(ci, pc);
        gs_pixel(pc, x, y, fr, fg, fb, fa, u, v, z);
    }

    // Diagnostico do GS. Dois modos:
    //  PS2_GSTRACE=<quantos> [PS2_GSTRACE_SKIP=<n>]: imprime o estado de cada desenho (sprite/triangulo), a partir do n-esimo.
    //  PS2_GSTRACE_DUMP=1: guarda os ultimos desenhos e, a cada dump de quadro (PS2_GSDUMP), imprime os dos 2 ultimos vsyncs.
    static std::deque<std::pair<unsigned, std::string>> g_trace_ring;

    // O trace detalhado vai para gs_trace.txt (escrita em arquivo e rapida). Imprimir milhares de linhas no console/pipe do
    // PowerShell travava o jogo por varios segundos a cada dump. PS2_GSTRACE_STDERR=1 volta a imprimir no stderr.
    static FILE* gs_trace_out()
    {
        static FILE* f = nullptr;
        static bool tried = false;

        if (std::getenv("PS2_GSTRACE_STDERR") != nullptr)
            return stderr;

        if (!tried)
        {
            tried = true;
            f = std::fopen("gs_trace.txt", "w");

            if (f != nullptr)
                std::fprintf(stderr, "  [gs] trace detalhado em gs_trace.txt\n");
        }

        return f != nullptr ? f : stderr;
    }
    static std::string g_trace_chain;   // quem disparou o DMA que esta sendo processado (ver gs_trace_set_caller)
    static unsigned long g_trace_draws = 0;   // total de sprites/triangulos desenhados (conta sempre)
    // Por kick de DMA (so quando o trace esta ligado): vertices chutados, triangulos de area zero e primeiro/ultimo vertice.
    static unsigned long g_trace_vtx = 0, g_trace_degen = 0;
    static char g_trace_v0[200] = "", g_trace_vn[200] = "";
    static char g_trace_all[1200] = "";   // todos os vertices (ate 8) do kick: raw XY (12.4) + posicao + uv

    static std::set<unsigned> g_cap_cbps;   // CBPs de CLUT usados pelos desenhos da captura
    static bool g_cap_active = false;     // captura por F9 em andamento
    static unsigned g_cap_until = 0, g_cap_count = 0;

    static bool gs_trace_enabled()
    {
        static int on = -1;

        if (on < 0)
            on = (std::getenv("PS2_GSTRACE") != nullptr || std::getenv("PS2_GSTRACE_DUMP") != nullptr) ? 1 : 0;

        return on == 1 || g_cap_active;
    }

    // Cadeia de chamadores de quem escreveu no CHCR do DMA: $ra e, varrendo a pilha, enderecos de retorno plausiveis
    // (a palavra anterior ao retorno e um JAL/JALR). Heuristica de depurador: funciona sem informacao de frame.
    static void gs_trace_set_caller(Ctx& c)
    {
        if (!gs_trace_enabled())
            return;

        g_trace_chain.clear();
        const char* last = nullptr;
        int shown = 0;
        char tmp[160];

        auto add = [&](uint32_t ret)
        {
            const Ps2FuncEntry* e = find_containing(ret - 8);

            if (e == nullptr || e->name == nullptr || e->name == last || shown >= 10)
                return;

            last = e->name;
            std::snprintf(tmp, sizeof(tmp), "%s%s@%08X", shown ? " < " : "", e->name, (unsigned)(ret - 8));
            g_trace_chain += tmp;
            shown++;
        };

        add((uint32_t)c.r[31].lo);
        uint32_t sp = (uint32_t)c.r[29].lo & 0x01FFFFFFu;

        for (uint32_t i = 0; i < 1024 && shown < 10 && sp + i * 4 + 4 <= 0x02000000u; i++)
        {
            uint32_t w;
            std::memcpy(&w, c.mem + sp + i * 4, 4);

            if ((w & 3u) != 0 || w < 0x00100010u || w >= 0x01FFFFF0u)
                continue;

            uint32_t op;
            std::memcpy(&op, c.mem + (w - 8), 4);

            if ((op >> 26) == 3u || ((op >> 26) == 0u && (op & 0x3Fu) == 9u))   // JAL / JALR
                add(w);
        }
    }

    void gs_trace_draw(const char* kind, int ci, float x0, float y0, float x1, float y1)
    {
        static bool init = false;
        static long skip = 0, count = 0, seen = 0;
        static bool ring = false;

        if (!init)
        {
            const char* t = std::getenv("PS2_GSTRACE");
            const char* k = std::getenv("PS2_GSTRACE_SKIP");
            count = t ? std::atol(t) : 0;
            skip = k ? std::atol(k) : 0;
            ring = std::getenv("PS2_GSTRACE_DUMP") != nullptr;
            init = true;
        }

        g_trace_draws++;

        const bool rec = ring || g_cap_active;

        if (g_cap_active)
            g_cap_cbps.insert((unsigned)((g_st.ctx[ci].tex0 >> 37) & 0x3FFF));

        if (count <= 0 && !rec)
            return;

        long n = seen++;
        bool direct = count > 0 && n >= skip;

        if (!direct && !rec)
            return;

        if (direct)
            count--;

        const GsCtx& c = g_st.ctx[ci];
        char buf[2600];
        std::snprintf(buf, sizeof(buf),
            "  [gs] draw #%ld vs=%u %s ctx=%d prim=0x%03X (%.1f,%.1f)-(%.1f,%.1f) tex0: tbp=%u tbw=%u psm=0x%02X tw=%u th=%u tcc=%u tfx=%u cbp=%u cpsm=0x%X csa=%u cld=%u"
            " | tex1=0x%llX clamp=0x%llX alpha=0x%llX test=0x%llX texa=0x%llX | frame: fbp=%u fbw=%u psm=0x%02X fbmsk=0x%08X | v0=%s vn=%s scissor=0x%llX ofs=0x%llX zbuf=0x%llX | chamada: %s",
            n, g_gs_vsyncs, kind, ci, (unsigned)g_st.prim, x0, y0, x1, y1,
            (unsigned)(c.tex0 & 0x3FFF), (unsigned)((c.tex0 >> 14) & 0x3F), (unsigned)((c.tex0 >> 20) & 0x3F),
            (unsigned)((c.tex0 >> 26) & 0xF), (unsigned)((c.tex0 >> 30) & 0xF), (unsigned)((c.tex0 >> 34) & 1), (unsigned)((c.tex0 >> 35) & 3),
            (unsigned)((c.tex0 >> 37) & 0x3FFF), (unsigned)((c.tex0 >> 51) & 0xF), (unsigned)((c.tex0 >> 56) & 0x1F), (unsigned)(c.tex0 >> 61),
            (unsigned long long)c.tex1, (unsigned long long)c.clamp, (unsigned long long)c.alpha, (unsigned long long)c.test, (unsigned long long)g_st.texa,
            (unsigned)(c.frame & 0x1FF), (unsigned)((c.frame >> 16) & 0x3F), (unsigned)((c.frame >> 24) & 0x3F), (unsigned)(c.frame >> 32),
            g_trace_v0, g_trace_vn, (unsigned long long)c.scissor, (unsigned long long)c.xyoffset, (unsigned long long)c.zbuf,
            g_trace_chain.c_str());

        if (direct)
            std::fprintf(gs_trace_out(), "%s\n", buf);

        if (rec)
        {
            g_trace_ring.emplace_back(g_gs_vsyncs, std::string(buf));

            if (g_trace_ring.size() > 6000)
                g_trace_ring.pop_front();
        }
    }

    // Captura F9: por padrao 3 VSyncs (legado). Com PS2_GS_CAPTURE_FRAME=1,
    // espera 60 VSyncs (~1 segundo de video) para capturar comandos de desenho
    // mesmo quando a batalha gera apenas 4-6 quadros efetivos por segundo.
    // Esta opcao NAO modifica os pixels, o FIELDLOCK nem os registradores do GS.
    static void gs_capture_poll()
    {
        static const bool capture_frame = std::getenv("PS2_GS_CAPTURE_FRAME") != nullptr;
        if (!g_cap_active && g_cap_request.exchange(false))
        {
            g_cap_active = true;
            g_cap_until = g_gs_vsyncs + (capture_frame ? 60u : 3u);
            g_trace_ring.clear();
            g_cap_cbps.clear();
            std::fprintf(stderr, "  [captura N] F9: janela=%u vsyncs; aguarde o arquivo gs_captura_N.txt...\n",
                         capture_frame ? 60u : 3u);
            return;
        }

        if (g_cap_active && g_gs_vsyncs >= g_cap_until)
        {
            g_cap_active = false;
            char name[64];
            std::snprintf(name, sizeof name, "gs_captura_%u.txt", ++g_cap_count);
            FILE* f = std::fopen(name, "w");

            if (f != nullptr)
            {
                std::fprintf(f, "captura vsync %u..%u, %zu desenhos; modo=%s\n",
                             g_cap_until - (capture_frame ? 60u : 3u), g_cap_until,
                             g_trace_ring.size(), capture_frame ? "F9_N_60_VSYNC" : "F9_3_VSYNC");

                for (const auto& e : g_trace_ring)
                    std::fprintf(f, "%s\n", e.second.c_str());

                // estado da CLUT: buffer interno (512 entradas) e o que esta na VRAM no CBP de cada contexto
                std::fprintf(f, "\n== CLUT buffer interno (RGBA32, 16 por linha) ==\n");

                for (int i = 0; i < 256; i += 16)
                {
                    std::fprintf(f, "%3d:", i);

                    for (int k = 0; k < 16; k++)
                        std::fprintf(f, " %08X", (unsigned)g_st.clut[i + k]);

                    std::fprintf(f, "\n");
                }

                std::fprintf(f, "\n== CLUT na VRAM (16 entradas de 4 bits, 8x2 CT32) em cada CBP usado nos desenhos ==\n");

                for (unsigned cb : g_cap_cbps)
                {
                    std::fprintf(f, "cbp=%5u:", cb);

                    for (int i = 0; i < 16; i++)
                        std::fprintf(f, " %08X", (unsigned)g_vram[ix_ct32(cb, 1, (uint32_t)(i & 7), (uint32_t)(i >> 3))]);

                    std::fprintf(f, "\n");
                }

                g_cap_cbps.clear();

                for (int ci = 0; ci < 2; ci++)
                {
                    const uint64_t t0 = g_st.ctx[ci].tex0;
                    const unsigned cbp = (unsigned)((t0 >> 37) & 0x3FFF), cpsm = (unsigned)((t0 >> 51) & 0xF);
                    std::fprintf(f, "\n== contexto %d: tex0=0x%016llX tbp=%u psm=0x%02X cbp=%u cpsm=0x%X csa=%u cld=%u; VRAM no CBP (8x2, CT32):\n",
                        ci, (unsigned long long)t0, (unsigned)(t0 & 0x3FFF), (unsigned)((t0 >> 20) & 0x3F), cbp, cpsm, (unsigned)((t0 >> 56) & 0x1F), (unsigned)(t0 >> 61));

                    for (int i = 0; i < 16; i++)
                        std::fprintf(f, " %08X", (unsigned)g_vram[ix_ct32(cbp, 1, (uint32_t)(i & 7), (uint32_t)(i >> 3))]);

                    std::fprintf(f, "\n");
                }

                std::fclose(f);
            }

            std::fprintf(stderr, "  [captura] %s gravado (%zu desenhos)\n", name, g_trace_ring.size());
            g_trace_ring.clear();
        }
    }

    // PS2_GSTRACE_KICK=1 (junto com PS2_GSTRACE_DUMP): registra cada kick de DMA, inclusive os que nao geram desenho,
    // com quantos sprites/triangulos ele produziu e o contador de primitivas nao suportadas.
    static void gs_trace_kick(int ch, unsigned long draws_before, uint64_t unsupported_before)
    {
        static int on = -1;

        if (on < 0)
            on = (std::getenv("PS2_GSTRACE_DUMP") != nullptr && std::getenv("PS2_GSTRACE_KICK") != nullptr) ? 1 : 0;

        if (on != 1)
            return;

        char buf[1400];
        std::snprintf(buf, sizeof(buf), "  [gs] kick ch=%d vs=%u desenhos=%lu nao_suportado=+%llu vertices=%lu degenerados=%lu primeiro=%s ultimo=%s prim=0x%03X%s | chamada: %s",
            ch, g_gs_vsyncs, g_trace_draws - draws_before, (unsigned long long)(g_st.unsupported - unsupported_before),
            g_trace_vtx, g_trace_degen, g_trace_vtx ? g_trace_v0 : "-", g_trace_vtx > 1 ? g_trace_vn : "-", (unsigned)g_st.prim, (g_trace_degen ? g_trace_all : ""), g_trace_chain.c_str());
        g_trace_ring.emplace_back(g_gs_vsyncs, std::string(buf));

        if (g_trace_ring.size() > 1200)
            g_trace_ring.pop_front();
    }

    // Chamado por gs_dump_frame: imprime os desenhos dos 2 ultimos vsyncs (o quadro que acabou de ser exibido).
    void gs_trace_flush(unsigned index)
    {
        if (g_trace_ring.empty())
            return;

        FILE* out = gs_trace_out();
        std::fprintf(out, "  [gs] desenhos recentes antes do dump %u (vsync %u):\n", index, g_gs_vsyncs);

        for (const auto& e : g_trace_ring)
            if (e.first + 2 >= g_gs_vsyncs)
                std::fprintf(out, "%s\n", e.second.c_str());

        if (out != stderr)
            std::fflush(out);

        g_trace_ring.clear();
    }

    // ---- caminho rapido (sprites grandes e triangulos 2D de cor plana) ----
    // A textura (so a janela de texels que a primitiva usa) e decodificada uma vez para memoria linear, ja com a CLUT e o
    // TEXA aplicados, e as linhas sao desenhadas por um laco sem decisoes por pixel. Cobre: textura de 4/8/16/24/32 bits,
    // repeticao ou clamp simples, MODULATE/DECAL, qualquer blend (A,B,C,D), alpha test e FBMSK, destino de 32 bits.
    // O resto segue o caminho generico. PS2_NOFASTSPRITE=1 desliga.
    bool g_nofastsprite = std::getenv("PS2_NOFASTSPRITE") != nullptr;

    struct FastRaster
    {
        const PixCfg* pc = nullptr;
        std::vector<uint32_t> lin;
        struct Column
        {
            uint32_t vram_x;
            int tex_x0, tex_x1, weight;
        };
        std::vector<Column> columns;
        int ox = 0, oy = 0, ww = 1, wh = 1;
        int W = 1, H = 1, wmask = 0, hmask = 0;
        bool linear = false, std_blend = false, tcc = false, decal = false;
        int vr = 0, vg = 0, vb = 0, va = 0;

        static bool tex_ok(const TexInfo& t)
        {
            return (t.psm == 0x00 || t.psm == 0x01 || t.psm == 0x02 || t.psm == 0x0A || t.psm == 0x13 || t.psm == 0x14 || t.psm == 0x1B || t.psm == 0x24 || t.psm == 0x2C)
                && t.umode <= 1 && t.vmode <= 1 && t.tfx <= 1;
        }

        // Escolhe o formato antes de percorrer a janela. O swizzle,
        // o TEXA e a CLUT continuam sendo os mesmos da leitura generica.
        template <int psm>
        void decode_sprite_texture(const TexInfo& ti)
        {
            for (int y = 0; y < wh; ++y)
            {
                uint32_t* row = lin.data() + (size_t)y * ww;
                for (int x = 0; x < ww; ++x)
                    row[x] = gs_texel_wrapped<psm>(ti, ox + x, oy + y);
            }
        }

        int wrapx(int v) const { return pc->ti.umode == 0 ? (v & wmask) : (v < 0 ? 0 : (v >= W ? W - 1 : v)); }
        int wrapy(int v) const { return pc->ti.vmode == 0 ? (v & hmask) : (v < 0 ? 0 : (v >= H ? H - 1 : v)); }

        // umin..vmax: extensao (em texels) das coordenadas que a primitiva vai amostrar
        bool setup(const PixCfg& cfg, float cr, float cg, float cb, float ca, float umin, float umax, float vmin, float vmax,
            bool sprite_columns = false)
        {
            pc = &cfg;

            if (cfg.fpsm != 0x00)
                return false;

            std_blend = cfg.abe && cfg.A == 0 && cfg.B == 1 && cfg.C == 0 && cfg.D == 1;
            vr = gs_f2i255(cr); vg = gs_f2i255(cg); vb = gs_f2i255(cb); va = gs_f2i255(ca);

            if (!cfg.tme)
                return true;

            const TexInfo& ti = cfg.ti;

            if (!tex_ok(ti))
                return false;

            W = ti.w; H = ti.h; wmask = W - 1; hmask = H - 1;
            linear = ti.linear; tcc = ti.tcc; decal = ti.tfx == 1;

            // janela de texels (com folga para o bilinear); com repeticao e coordenadas fora da textura, decodifica tudo
            const int m = linear ? 2 : 1;
            int x0 = fast_floor(umin) - m, x1 = fast_ceil(umax) + m, y0 = fast_floor(vmin) - m, y1 = fast_ceil(vmax) + m;

            if (ti.umode == 0 && (x0 < 0 || x1 >= W)) { x0 = 0; x1 = W - 1; }
            if (ti.vmode == 0 && (y0 < 0 || y1 >= H)) { y0 = 0; y1 = H - 1; }

            x0 = std::max(0, std::min(x0, W - 1)); x1 = std::max(0, std::min(x1, W - 1));
            y0 = std::max(0, std::min(y0, H - 1)); y1 = std::max(0, std::min(y1, H - 1));

            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;

            ox = x0; oy = y0; ww = x1 - x0 + 1; wh = y1 - y0 + 1;

            if ((long long)ww * wh > 1048576)
                return false;

            lin.resize((size_t)ww * (size_t)wh);

            if (sprite_columns && g_sprite_tex_decode)
            {
                // tex_ok() ja validou os formatos e a janela esta nos limites da textura.
                switch (ti.psm)
                {
                    case 0x00: decode_sprite_texture<0x00>(ti); break;
                    case 0x01: decode_sprite_texture<0x01>(ti); break;
                    case 0x02: decode_sprite_texture<0x02>(ti); break;
                    case 0x0A: decode_sprite_texture<0x0A>(ti); break;
                    case 0x13: decode_sprite_texture<0x13>(ti); break;
                    case 0x14: decode_sprite_texture<0x14>(ti); break;
                    case 0x1B: decode_sprite_texture<0x1B>(ti); break;
                    case 0x24: decode_sprite_texture<0x24>(ti); break;
                    case 0x2C: decode_sprite_texture<0x2C>(ti); break;
                }
            }
            else
            {
                for (int y = 0; y < wh; ++y)
                    for (int x = 0; x < ww; ++x)
                        lin[(size_t)y * ww + x] = sprite_columns
                            ? gs_texel_wrapped(ti, ox + x, oy + y) : gs_texel(ti, ox + x, oy + y);
            }

            return true;
        }

        inline uint32_t fetch(int iu, int iv) const
        {
            return lin[(size_t)((wrapy(iv) - oy) * ww + (wrapx(iu) - ox))];
        }

        // Sprites reutilizam o mesmo U em todas as linhas.
        void prepare_sprite_columns(int xs, int xe, float u, float du)
        {
            columns.resize((size_t)(xe - xs + 1));
            for (int x = xs; x <= xe; ++x)
            {
                const uint32_t sx = (uint32_t)x;
                Column& col = columns[(size_t)(x - xs)];
                // O swizzle CT32 e a soma dos componentes X e Y.
                col.vram_x = ix_ct32(0, 0, sx, 0);
                if (pc->tme)
                {
                    const float uc = u + du * (float)(x - xs);
                    const float fu = linear ? uc - 0.5f : uc;
                    const int iu = fast_floor(fu);
                    col.tex_x0 = wrapx(iu) - ox;
                    if (linear)
                    {
                        col.tex_x1 = wrapx(iu + 1) - ox;
                        col.weight = (int)((fu - (float)iu) * 256.0f);
                    }
                }
            }
        }

        // u,v no centro do pixel xs; du,dv por pixel
        template <bool cached_columns = false, bool constant_z16s = false>
        void row(int y, int xs, int xe, float u, float du, float v, float dv, float z0, float dz) const
        {
            const PixCfg& c = *pc;
            const bool tme = c.tme;
            const uint32_t fbp = c.fbp, fbw = c.fbw, fbmsk = c.fbmsk;

            // v constante na linha (sprites): calcula as linhas da textura uma vez
            const bool vconst = dv == 0.0f;
            int rowA = 0, rowB = 0, wvc = 0;

            if (tme && vconst)
            {
                if (linear)
                {
                    const float fvv = v - 0.5f;
                    const int iv = fast_floor(fvv);
                    wvc = (int)((fvv - (float)iv) * 256.0f);
                    rowA = (wrapy(iv) - oy) * ww;
                    rowB = (wrapy(iv + 1) - oy) * ww;
                }
                else
                {
                    rowA = (wrapy(fast_floor(v)) - oy) * ww;
                }
            }

            const uint32_t vram_y = cached_columns ? ix_ct32(fbp, fbw, 0, (uint32_t)y) : 0;
            // Sprites estao a profundidade constante. No Z16S, tanto o
            // endereco de Y quanto a conversao de Z sao comuns a linha toda.
            const uint16_t depth = constant_z16s
                ? uint16_t(z0 <= 0.0f ? 0u : (z0 >= 4294967040.0f ? 0xFFFFFF00u : (uint32_t)z0)) : 0;
            const uint32_t zpage = constant_z16s
                ? (((c.zbp >> 5) + ((uint32_t)y >> 6) * fbw) << 12) + ((c.zbp & 31u) << 7) : 0;
            for (int x = xs; x <= xe; x++)
            {
                uint8_t* zpixel = nullptr;
                if constexpr (constant_z16s)
                {
                    const uint32_t offset = (zpage + (((uint32_t)x >> 6) << 12) +
                        g_page_z16s[y & 63][x & 63]) & 0x1FFFFFu;
                    zpixel = vram8() + (size_t)offset * 2;
                    uint16_t resident;
                    std::memcpy(&resident, zpixel, sizeof(resident));
                    if (depth < resident || (depth == resident && c.ztst == 3u))
                        continue;
                }
                const float uc = u + du * (float)(x - xs), vc = v + dv * (float)(x - xs);   // multiplicacao (sem acumular erro)
                int r = vr, g = vg, bl = vb, al = va;

                if (tme)
                {
                    uint32_t t;

                    if (linear)
                    {
                        const float fu = uc - 0.5f;
                        const int iu = fast_floor(fu);
                        const int wu = cached_columns ? columns[(size_t)(x - xs)].weight
                            : (int)((fu - (float)iu) * 256.0f);
                        const int x0i = cached_columns ? columns[(size_t)(x - xs)].tex_x0 : wrapx(iu) - ox;
                        int ra = rowA, rb = rowB, wv = wvc;

                        if (!vconst)
                        {
                            const float fvv = vc - 0.5f;
                            const int iv = fast_floor(fvv);
                            wv = (int)((fvv - (float)iv) * 256.0f);
                            ra = (wrapy(iv) - oy) * ww;
                            rb = (wrapy(iv + 1) - oy) * ww;
                        }

                        if (wu < 4 && wv < 4)
                        {
                            t = lin[(size_t)(ra + x0i)];
                        }
                        else
                        {
                            const int x1i = cached_columns ? columns[(size_t)(x - xs)].tex_x1 : wrapx(iu + 1) - ox;
                            const uint32_t t00 = lin[(size_t)(ra + x0i)], t10 = lin[(size_t)(ra + x1i)];
                            const uint32_t t01 = lin[(size_t)(rb + x0i)], t11 = lin[(size_t)(rb + x1i)];
                            t = 0;

                            if (cached_columns && (unsigned)wu <= 256u && (unsigned)wv <= 256u)
                            {
                                t = g_sprite_wide_fast
                                    ? gs_sprite_bilinear(t00, t10, t01, t11, wu, wv)
                                    : gs_bilinear(t00, t10, t01, t11, wu, wv);
                            }
                            else
                            {
                                for (int sh = 0; sh < 32; sh += 8)
                                {
                                    const int c00 = (int)((t00 >> sh) & 0xFF), c10 = (int)((t10 >> sh) & 0xFF);
                                    const int c01 = (int)((t01 >> sh) & 0xFF), c11 = (int)((t11 >> sh) & 0xFF);
                                    const int top = c00 * (256 - wu) + c10 * wu, bot = c01 * (256 - wu) + c11 * wu;
                                    t |= (uint32_t)(((top * (256 - wv) + bot * wv) >> 16) & 0xFF) << sh;
                                }
                            }
                        }
                    }
                    else
                    {
                        const int ra = vconst ? rowA : (wrapy(fast_floor(vc)) - oy) * ww;
                        const int tex_x = cached_columns ? columns[(size_t)(x - xs)].tex_x0 : wrapx(fast_floor(uc)) - ox;
                        t = lin[(size_t)(ra + tex_x)];
                    }

                    const int tr = (int)(t & 0xFF), tg = (int)((t >> 8) & 0xFF), tb = (int)((t >> 16) & 0xFF), ta = (int)((t >> 24) & 0xFF);

                    if (decal)
                    {
                        r = tr; g = tg; bl = tb; al = tcc ? ta : va;
                    }
                    else
                    {
                        r = std::min(255, tr * vr >> 7); g = std::min(255, tg * vg >> 7); bl = std::min(255, tb * vb >> 7);
                        al = tcc ? std::min(255, ta * va >> 7) : va;
                    }
                }

                bool afail_rgb_only = false, fb_ok = true, z_write_ok = true;

                if (c.ate)
                {
                    bool pass = true;

                    switch (c.atst)
                    {
                        case 0: pass = false; break;
                        case 2: pass = al < c.aref; break;
                        case 3: pass = al <= c.aref; break;
                        case 4: pass = al == c.aref; break;
                        case 5: pass = al >= c.aref; break;
                        case 6: pass = al > c.aref; break;
                        case 7: pass = al != c.aref; break;
                        default: break;
                    }

                    if (!pass)
                    {
                        switch (c.afail)
                        {
                            case 0: continue;
                            case 1: z_write_ok = false; break;
                            case 2: fb_ok = false; break;
                            default: z_write_ok = false; afail_rgb_only = true; break;
                        }
                    }
                }

                if constexpr (constant_z16s)
                {
                    // AFAIL ainda decide a escrita. A ordem Z antes da cor
                    // e preservada mesmo quando ambos usam a mesma VRAM.
                    if (z_write_ok && !c.zmsk)
                        std::memcpy(zpixel, &depth, sizeof(depth));
                }
                else if (c.zactive)
                {
                    const float zf = z0 + dz * (float)(x - xs);
                    const uint32_t zi = zf <= 0.0f ? 0u : (zf >= 4294967040.0f ? 0xFFFFFF00u : (uint32_t)zf);

                    if (!gs_ztest(c, (uint32_t)x, (uint32_t)y, zi, z_write_ok))
                        continue;
                }

                if (!fb_ok)
                    continue;

                const uint32_t addr = cached_columns ? (vram_y + columns[(size_t)(x - xs)].vram_x) & 0xFFFFFu
                    : ix_ct32(fbp, fbw, (uint32_t)x, (uint32_t)y);
                uint32_t* dp = &g_vram[addr];
                const uint32_t dst = c.need_dst ? *dp : 0;

                if (c.abe)
                {
                    const int dr = (int)(dst & 0xFF), dg = (int)((dst >> 8) & 0xFF), db = (int)((dst >> 16) & 0xFF);
                    int nr, ng, nb;

                    if (std_blend)
                    {
                        nr = ((r - dr) * al >> 7) + dr; ng = ((g - dg) * al >> 7) + dg; nb = ((bl - db) * al >> 7) + db;
                    }
                    else
                    {
                        const int da = (int)((dst >> 24) & 0xFF);
                        const int cf = c.C == 0 ? al : (c.C == 1 ? da : c.fix);
                        const int ar = c.A == 0 ? r : (c.A == 1 ? dr : 0), br = c.B == 0 ? r : (c.B == 1 ? dr : 0), dd_r = c.D == 0 ? r : (c.D == 1 ? dr : 0);
                        const int ag = c.A == 0 ? g : (c.A == 1 ? dg : 0), bg = c.B == 0 ? g : (c.B == 1 ? dg : 0), dd_g = c.D == 0 ? g : (c.D == 1 ? dg : 0);
                        const int ab = c.A == 0 ? bl : (c.A == 1 ? db : 0), bb = c.B == 0 ? bl : (c.B == 1 ? db : 0), dd_b = c.D == 0 ? bl : (c.D == 1 ? db : 0);
                        nr = (((ar - br) * cf) >> 7) + dd_r; ng = (((ag - bg) * cf) >> 7) + dd_g; nb = (((ab - bb) * cf) >> 7) + dd_b;
                    }

                    if (c.clamp) { r = gs_clamp255(nr); g = gs_clamp255(ng); bl = gs_clamp255(nb); }
                    else { r = nr & 0xFF; g = ng & 0xFF; bl = nb & 0xFF; }
                }

                uint32_t out = (uint32_t)(r | (g << 8) | (bl << 16) | (al << 24));

                if (afail_rgb_only)
                    out = (out & 0x00FFFFFFu) | (dst & 0xFF000000u);

                if (fbmsk != 0)
                    out = (dst & fbmsk) | (out & ~fbmsk);

                *dp = out;
            }
        }
    };

    // Retangulos grandes de cor constante: resolve o blend uma vez
    // para cada valor possivel de um canal. A mascara CT32 usa a cor
    // anterior exatamente como no caminho por pixel.
    static void gs_sprite_flat_rect(const PixCfg& pc, const GsVertex& color,
                                   int x0, int x1, int y0, int y1)
    {
        const unsigned alpha = (unsigned)gs_f2i255(color.a);
        const unsigned rgb[3] = {(unsigned)gs_f2i255(color.r),
                                 (unsigned)gs_f2i255(color.g),
                                 (unsigned)gs_f2i255(color.b)};
        uint32_t columns[2048];
        for (int x = x0; x < x1; ++x)
            columns[x - x0] = ix_ct32(0, 0, (uint32_t)x, 0);
        uint32_t blend[3][256];
        if (pc.abe)
        {
            for (unsigned ch = 0; ch < 3; ++ch)
                for (int dst = 0; dst < 256; ++dst)
                {
                    const int value = ((((int)rgb[ch] - dst) * (int)alpha) >> 7) + dst;
                    const uint32_t channel = (uint32_t)(pc.clamp ? gs_clamp255(value) : (value & 255));
                    blend[ch][dst] = channel << (8 * ch);
                }
        }
        const uint32_t flat = (alpha << 24) | (rgb[2] << 16) | (rgb[1] << 8) | rgb[0];
        for (int y = y0; y < y1; ++y)
        {
            const uint32_t row = ix_ct32(pc.fbp, pc.fbw, 0, (uint32_t)y);
            for (int x = x0; x < x1; ++x)
            {
                uint32_t& pixel = g_vram[(row + columns[x - x0]) & 0xFFFFFu];
                if (pc.abe)
                {
                    const uint32_t dst = pixel;
                    const uint32_t out = (alpha << 24) | blend[0][dst & 255u] |
                        blend[1][(dst >> 8) & 255u] | blend[2][(dst >> 16) & 255u];
                    pixel = (dst & pc.fbmsk) | (out & ~pc.fbmsk);
                }
                else
                    pixel = pc.fbmsk ? ((pixel & pc.fbmsk) | (flat & ~pc.fbmsk)) : flat;
            }
        }
    }

    bool gs_sprite_fast(const PixCfg& pc, const GsVertex& a, const GsVertex& b, int px0, int px1, int py0, int py1)
    {
        if (g_nofastsprite || px0 >= px1 || py0 >= py1)
            return false;

        if (g_sprite_wide_fast && pc.fpsm == 0u && !pc.tme && !pc.zactive && !pc.ate &&
            px1 - px0 <= 2048 &&
            (!pc.abe || (pc.A == 0 && pc.B == 1 && pc.C == 0 && pc.D == 1)))
        {
            gs_sprite_flat_rect(pc, b, px0, px1, py0, py1);
            if (g_perf_gs)
                g_prof_sprite_fast_pixels.fetch_add((uint64_t)(px1 - px0) * (py1 - py0), std::memory_order_relaxed);
            return true;
        }

        static thread_local FastRaster fr;
        const float du = (b.u - a.u) / (b.x - a.x);
        const float u_first = a.u + du * ((float)px0 + 0.5f - a.x), u_last = a.u + du * ((float)(px1 - 1) + 0.5f - a.x);
        const float iyr = 1.0f / (b.y - a.y);
        const float v_first = a.v + (b.v - a.v) * (((float)py0 + 0.5f - a.y) * iyr), v_last = a.v + (b.v - a.v) * (((float)(py1 - 1) + 0.5f - a.y) * iyr);

        if (!fr.setup(pc, b.r, b.g, b.b, b.a, std::min(u_first, u_last), std::max(u_first, u_last),
            std::min(v_first, v_last), std::max(v_first, v_last), g_sprite_columns))
            return false;

        if (g_perf_gs)
            g_prof_sprite_fast_pixels.fetch_add((uint64_t)(px1 - px0) * (py1 - py0), std::memory_order_relaxed);
        if (g_sprite_columns)
            fr.prepare_sprite_columns(px0, px1 - 1, u_first, du);

        const bool z16_early = g_sprite_z16_early && g_sprite_columns &&
            pc.zactive && pc.zpsm == 0x3Au && pc.ztst >= 2u;
        if (z16_early && g_perf_gs)
            g_prof_sprite_z16_pixels.fetch_add((uint64_t)(px1 - px0) * (py1 - py0), std::memory_order_relaxed);

        for (int y = py0; y < py1; y++)
        {
            const float v = a.v + (b.v - a.v) * (((float)y + 0.5f - a.y) * iyr);
            if (z16_early)
                fr.row<true, true>(y, px0, px1 - 1, u_first, du, v, 0.0f, (float)b.z, 0.0f);
            else if (g_sprite_columns)
                fr.row<true>(y, px0, px1 - 1, u_first, du, v, 0.0f, (float)b.z, 0.0f);
            else
                fr.row(y, px0, px1 - 1, u_first, du, v, 0.0f, (float)b.z, 0.0f);
        }

        return true;
    }

    static const char* const g_field_recon_mode = [] {
        const char* value = std::getenv("PS2_FIELD_RECON");
        return value ? value : "spatial";
    }();

    // Captura os sprites de texto em resolucao vertical original, antes
    // de perder linhas ao reduzir o atlas para o framebuffer entrelacado.
    // O texto e retirado apenas do framebuffer de baixa resolucao para nao
    // aparecer duas vezes. Os texels e o alfa sao preservados em uma copia.
    // A composicao ocorre mais tarde em gs_present_frame(), apos a ampliacao.
    // Esta camada e experimental; nao substitui a emulacao GS/CRT do PS2.
    struct UiGlyphP
    {
        uint32_t fbp = 0;
        unsigned vsync = 0;
        int x = 0, y = 0, w = 0, h = 0;
        uint32_t tint = 0;
        bool outline = false;
        bool ring_hud = false;
        int clip_x0 = 0, clip_y0 = 0, clip_x1 = 640, clip_y1 = 448;
        std::vector<uint32_t> rgba; // 0xAABBGGRR, um texel por pixel final
    };
    std::mutex g_ui_native_mu;
    std::vector<UiGlyphP> g_ui_native_glyphs;

    // A sombra e um segundo sprite preto. Preserve o alfa original do GS;
    // reduzi-lo apaga o fundo escuro que separa as letras do cenario.
    static int gs_text_vertex_alpha(const GsVertex& vertex)
    {
        return gs_f2i255(vertex.a);
    }

    static uint32_t gs_text_lerp(uint32_t top, uint32_t bottom, unsigned fraction)
    {
        const unsigned a = (top >> 24) * (256u - fraction), b = (bottom >> 24) * fraction;
        const unsigned coverage = a + b;
        if (!coverage)
            return 0;
        uint32_t texel = ((coverage + 128) / 256) << 24;
        for (unsigned shift = 0; shift < 24; shift += 8)
        {
            const unsigned color = (((top >> shift) & 255u) * a +
                ((bottom >> shift) & 255u) * b) / coverage;
            texel |= color << shift;
        }
        return texel;
    }

    static uint32_t gs_text_pair(uint32_t top, uint32_t bottom)
    {
        return gs_text_lerp(top, bottom, 128);
    }

    static uint32_t gs_ui_glyph_pixel(const PixCfg& pc, uint32_t tex, uint32_t tint, int alpha, int tested_alpha)
    {
        const uint32_t r = std::min(255u, ((tex & 255u) * (tint & 255u)) >> 7);
        const uint32_t g = std::min(255u, (((tex >> 8) & 255u) * ((tint >> 8) & 255u)) >> 7);
        const uint32_t b = std::min(255u, (((tex >> 16) & 255u) * ((tint >> 16) & 255u)) >> 7);
        const uint32_t a = std::min(128u, ((tex >> 24) * unsigned(alpha)) >> 7);
        const int test = std::min(255u, ((tex >> 24) * unsigned(tested_alpha)) >> 7);
        return gs_alpha_pass(pc, test) || pc.afail == 1 || pc.afail == 3
            ? r | (g << 8) | (b << 16) | (a << 24) : 0u;
    }

    static void gs_ui_sprite_auxiliary(const PixCfg& pc, const GsVertex& a, const GsVertex& b)
    {
        // RGB is composed at native height later. Keep the original alpha
        // and Z writes now, including ALWAYS + writable depth in menus.
        PixCfg aux = pc;
        aux.fbmsk = 0x00FFFFFFu;
        aux.need_dst = true;
        const int x0 = std::max({0, pc.sx0, fast_ceil(std::min(a.x,b.x) - .5f)});
        const int x1 = std::min({2048, pc.sx1+1, fast_ceil(std::max(a.x,b.x) - .5f)});
        const int y0 = std::max({0, pc.sy0, fast_ceil(std::min(a.y,b.y) - .5f)});
        const int y1 = std::min({2048, pc.sy1+1, fast_ceil(std::max(a.y,b.y) - .5f)});
        if (x0 >= x1 || y0 >= y1 || gs_sprite_fast(aux,a,b,x0,x1,y0,y1)) return;
        const float ix = 1.0f/(b.x-a.x), iy = 1.0f/(b.y-a.y);
        for (int y=y0; y<y1; ++y) for (int x=x0; x<x1; ++x)
            gs_pixel(aux,x,y,b.r,b.g,b.b,b.a,
                a.u+(b.u-a.u)*((x+.5f-a.x)*ix),
                a.v+(b.v-a.v)*((y+.5f-a.y)*iy),b.z);
    }

    static void gs_ui_apply_black_mask(std::vector<uint32_t>& pixels, const std::vector<uint8_t>& mask)
    {
        for (size_t i = 0; i < mask.size(); ++i)
        {
            const int alpha = mask[i];
            if (!alpha) continue;
            const uint32_t color = pixels[i];
            const int red = int((color >> 16) & 255u), green = int((color >> 8) & 255u), blue = int(color & 255u);
            pixels[i] = uint32_t((red + ((-red * alpha) >> 7)) << 16) |
                uint32_t((green + ((-green * alpha) >> 7)) << 8) |
                uint32_t(blue + ((-blue * alpha) >> 7));
        }
    }

    static void gs_ui_outline(std::vector<uint32_t>& pixels, int width, int height,
                              const std::vector<UiGlyphP>& glyphs)
    {
        if (std::none_of(glyphs.begin(), glyphs.end(), [](const UiGlyphP& glyph) { return glyph.outline; }))
            return;
        // Uma mascara comum evita que a borda de uma letra cubra a letra
        // vizinha. O contorno acompanha a cobertura, inclusive nos fades.
        static std::vector<uint8_t> mask;
        mask.assign(size_t(width) * height, 0);
        for (const auto& glyph : glyphs)
        {
            if (!glyph.outline) continue;
            for (int y = 0; y < glyph.h; ++y)
                for (int x = 0; x < glyph.w; ++x)
                {
                    const unsigned alpha = std::min(128u, glyph.rgba[size_t(y) * glyph.w + x] >> 24);
                    if (!alpha) continue;
                    for (int oy = -1; oy <= 1; ++oy)
                        for (int ox = -1; ox <= 1; ++ox)
                        {
                            const int dx = glyph.x + x + ox, dy = glyph.y + y + oy;
                            if (dx < 0 || dx >= width || dy < 0 || dy >= height ||
                                dx < glyph.clip_x0 || dx >= glyph.clip_x1 ||
                                dy < glyph.clip_y0 || dy >= glyph.clip_y1) continue;
                            auto& coverage = mask[size_t(dy) * width + dx];
                            const unsigned edge = ox && oy ? (alpha * 3 + 2) / 4 : alpha;
                            coverage = uint8_t(std::max(unsigned(coverage), edge));
                        }
                }
        }
        gs_ui_apply_black_mask(pixels, mask);
    }

    static void gs_ui_caption_band(std::vector<uint32_t>& pixels, int width, int height,
                                   const std::vector<UiGlyphP>& glyphs)
    {
        struct Band { int x0, y0, x1, y1, cx0, cy0, cx1, cy1; unsigned alpha; };
        std::vector<Band> bands;
        for (const auto& glyph : glyphs)
        {
            if (!glyph.outline) continue;
            unsigned alpha = 0;
            for (uint32_t texel : glyph.rgba) alpha = std::max(alpha, std::min(127u, texel >> 24));
            if (!alpha) continue;
            bands.push_back({glyph.x, glyph.y, glyph.x + glyph.w, glyph.y + glyph.h,
                glyph.clip_x0, glyph.clip_y0, glyph.clip_x1, glyph.clip_y1, alpha});
        }
        if (bands.empty()) return;
        std::sort(bands.begin(), bands.end(), [](const Band& a, const Band& b) {
            return a.y0 != b.y0 ? a.y0 < b.y0 : a.x0 < b.x0;
        });
        // Forma um fundo unico para cada bloco de texto, incluindo espacos
        // e linhas consecutivas, sem escurecer duas vezes as intersecoes.
        std::vector<Band> blocks;
        for (const auto& band : bands)
        {
            auto same = std::find_if(blocks.begin(), blocks.end(), [&](const Band& block) {
                return band.y0 <= block.y1 + 4 && band.y1 >= block.y0 - 4 &&
                    band.cx0 == block.cx0 && band.cy0 == block.cy0 && band.cx1 == block.cx1 && band.cy1 == block.cy1;
            });
            if (same == blocks.end()) blocks.push_back(band);
            else
            {
                same->x0 = std::min(same->x0, band.x0); same->x1 = std::max(same->x1, band.x1);
                same->y0 = std::min(same->y0, band.y0); same->y1 = std::max(same->y1, band.y1);
                same->alpha = std::max(same->alpha, band.alpha);
            }
        }
        static std::vector<uint8_t> mask;
        mask.assign(size_t(width) * height, 0);
        // A referencia do emulador conserva aproximadamente 25% da cena
        // no centro da faixa. Bordas suaves: convolucao da caixa com sigma=4.
        constexpr double inverse_sigma = 0.1767766952966369;
        for (const auto& block : blocks)
        {
            const int left = block.x0 - 2, right = block.x1 + 2;
            const int top = block.y0 - 3, bottom = block.y1 + 1;
            const int x0 = std::max({0, block.cx0, left - 12}), x1 = std::min({width, block.cx1, right + 12});
            const int y0 = std::max({0, block.cy0, top - 12}), y1 = std::min({height, block.cy1, bottom + 12});
            if (x1 <= x0 || y1 <= y0) continue;
            std::vector<double> horizontal(size_t(x1 - x0), 0.0);
            for (int x = x0; x < x1; ++x)
                horizontal[size_t(x - x0)] = 0.5 * (std::erf((x + 0.5 - left) * inverse_sigma) -
                    std::erf((x + 0.5 - right) * inverse_sigma));
            for (int y = y0; y < y1; ++y)
            {
                const double vertical = 0.5 * (std::erf((y + 0.5 - top) * inverse_sigma) -
                    std::erf((y + 0.5 - bottom) * inverse_sigma));
                for (int x = x0; x < x1; ++x)
                {
                    const unsigned alpha = unsigned(std::clamp(std::lround(96.0 * block.alpha / 127.0 *
                        horizontal[size_t(x - x0)] * vertical), 0l, 96l));
                    auto& coverage = mask[size_t(y) * width + x];
                    coverage = uint8_t(std::max(unsigned(coverage), alpha));
                }
            }
        }
        gs_ui_apply_black_mask(pixels, mask);
    }

    static void gs_ui_text_background(std::vector<uint32_t>& pixels, int width, int height,
                                      const std::vector<UiGlyphP>& glyphs, int style)
    {
        if (style == 0) gs_ui_outline(pixels, width, height, glyphs);
        else gs_ui_caption_band(pixels, width, height, glyphs);
    }

    static void gs_ui_filter(UiGlyphP& glyph)
    {
        const auto source = glyph.rgba;
        unsigned peak = 0;
        for (uint32_t pixel : source) peak = std::max(peak, pixel >> 24);
        if (!peak) return;
        const unsigned threshold = (peak + 1) / 2;
        const auto sample = [&](int x, int y) -> uint32_t {
            return x < 0 || y < 0 || x >= glyph.w || y >= glyph.h
                ? 0u : source[size_t(y) * glyph.w + x];
        };
        const auto inside = [&](uint32_t pixel) { return (pixel >> 24) >= threshold; };
        for (int y = 0; y < glyph.h; ++y)
            for (int x = 0; x < glyph.w; ++x)
            {
                const uint32_t e = sample(x,y), b = sample(x,y-1), d = sample(x-1,y);
                const uint32_t f = sample(x+1,y), h = sample(x,y+1);
                const bool ib=inside(b), id=inside(d), iff=inside(f), ih=inside(h);
                if (ib == ih || id == iff) continue;
                // Reconstrua quatro subpixels pela continuidade do contorno
                // (regra Scale2x) e integre a cobertura. Ao contrario do blur
                // em cruz, isso preserva hastes retas de um pixel e so trata
                // os cantos das diagonais. O limiar acompanha o fade do texto.
                const uint32_t subpixels[4] = {
                    id == ib ? d : e, ib == iff ? f : e,
                    id == ih ? d : e, ih == iff ? f : e
                };
                unsigned coverage = 0, red = 0, green = 0, blue = 0;
                for (uint32_t texel : subpixels) {
                    const unsigned alpha = texel >> 24;
                    coverage += alpha;
                    red += (texel & 255u) * alpha;
                    green += ((texel >> 8) & 255u) * alpha;
                    blue += ((texel >> 16) & 255u) * alpha;
                }
                // Interpolacao premultiplicada: transparencia nao injeta preto
                // nas letras claras e a sombra preta continua preta.
                glyph.rgba[size_t(y) * glyph.w + x] = coverage ? (red / coverage) |
                        ((green / coverage) << 8) | ((blue / coverage) << 16) |
                        (((coverage + 2) / 4) << 24) : 0u;
            }
    }


    static void gs_ui_on_sprite(const PixCfg& pc, int x0, int y0, int x1, int y1)
    {
        // Field scenes also clear an offscreen target before composing it.
        // Its one-bit color mask and ALWAYS alpha test still start a new scene.
        if (pc.fbp == 5760 && !pc.tme && !pc.abe && !pc.fpsm && pc.fbw == 10 &&
            pc.fbmsk == 1 && pc.ate && pc.atst == 1 && x0 <= 0 && y0 <= 0 && x1 >= 640 && y1 >= 224 &&
            pc.sx0 == 0 && pc.sy0 == 0 && pc.sx1 >= 639 && pc.sy1 >= 223)
            gs_vk_hi_reset(pc.fbp);
        if (!pc.tme && !pc.abe && !pc.ate && !pc.fbmsk && !pc.fpsm && pc.fbw == 10 &&
            x0 <= 0 && y0 <= 0 && x1 >= 640 && y1 >= 224 &&
            pc.sx0 == 0 && pc.sy0 == 0 && pc.sx1 >= 639 && pc.sy1 >= 223)
            gs_vk_hi_reset(pc.fbp);
        // A limpeza opaca deste framebuffer tambem descarta a sua camada de
        // interface. O outro framebuffer ainda pode estar na janela.
        if (!g_ui_native || pc.tme || pc.abe || pc.ate || pc.fbmsk || pc.fpsm ||
            pc.fbw != 10 || (pc.fbp != 0 && pc.fbp != 2240) ||
            (pc.zte && pc.ztst != 1) ||
            x0 > 0 || y0 > 0 || x1 < 640 || y1 < 224 ||
            pc.sx0 > 0 || pc.sy0 > 0 || pc.sx1 < 639 || pc.sy1 < 223)
            return;
        std::lock_guard<std::mutex> lk(g_ui_native_mu);
        auto& glyphs = g_ui_native_glyphs;
        glyphs.erase(std::remove_if(glyphs.begin(), glyphs.end(),
            [&](const UiGlyphP& glyph) { return glyph.fbp == pc.fbp; }), glyphs.end());
    }

    // O sprite ja foi recortado pelo scissor. Prepara so coordenadas e
    // enderecos: os texels continuam a ser lidos pixel a pixel da VRAM.
    template <bool ct32>
    static void gs_sprite_generic_rows(const PixCfg& pc, const GsVertex& a, const GsVertex& b,
        int px0, int px1, int py0, int py1, float ixr, float iyr)
    {
        struct Column { float u; int tex_x; uint32_t vram_x; };
        // O scissor GS tem 11 bits. O chamador garante x dentro desse intervalo.
        static thread_local Column columns[2048];
        const TexInfo& ti = pc.ti;
        const bool nearest = pc.tme && !ti.linear && !(ti.ui_vpair && ti.ui_vspan > 0);
        const int r = gs_f2i255(b.r), g = gs_f2i255(b.g), bl = gs_f2i255(b.b), al = gs_f2i255(b.a);
        for (int x = px0; x < px1; ++x)
        {
            Column& col = columns[x];
            if (pc.tme)
            {
                const float fu = ((float)x + 0.5f - a.x) * ixr;
                col.u = a.u + (b.u - a.u) * fu;
                if (nearest)
                    col.tex_x = gs_tex_wrap(fast_floor(col.u), ti.w, ti.umode, ti.uminv, ti.umaxv);
            }
            if (ct32)
                col.vram_x = ix_ct32(0, 0, (uint32_t)x, 0);
        }

        for (int y = py0; y < py1; ++y)
        {
            const float fv = ((float)y + 0.5f - a.y) * iyr;
            const float v = a.v + (b.v - a.v) * fv;
            const int tex_y = nearest ? gs_tex_wrap(fast_floor(v), ti.h, ti.vmode, ti.vminv, ti.vmaxv) : 0;
            const uint32_t vram_y = ct32 ? ix_ct32(pc.fbp, pc.fbw, 0, (uint32_t)y) : 0;

            for (int x = px0; x < px1; ++x)
            {
                if (pc.zactive && !gs_zpass(pc, (uint32_t)x, (uint32_t)y, b.z))
                    continue;

                const Column& col = columns[x];
                uint32_t texel = 0;
                if (nearest)
                    texel = gs_texel_wrapped(ti, col.tex_x, tex_y);
                else if (pc.tme)
                    texel = gs_sample(ti, col.u, v);

                const uint32_t addr = ct32 ? (vram_y + col.vram_x) & 0xFFFFFu : 0;
                gs_shade_pixel<true, ct32>(pc, x, y, r, g, bl, al, texel, b.z, addr);
            }
        }
    }

    void gs_draw_sprite(int ci, const GsVertex& a, const GsVertex& b)
    {
        float x0 = std::min(a.x, b.x), x1 = std::max(a.x, b.x), y0 = std::min(a.y, b.y), y1 = std::max(a.y, b.y);

        if (x1 <= x0 || y1 <= y0)
            return;

        gs_trace_draw("sprite", ci, x0, y0, x1, y1);

        const RasterProfScope prof(g_prof_sprite_ns);
        int px0 = (int)fast_ceil(x0 - 0.5f), px1 = (int)fast_ceil(x1 - 0.5f);
        int py0 = (int)fast_ceil(y0 - 0.5f), py1 = (int)fast_ceil(y1 - 0.5f);

        PixCfg pc;
        gs_pix_cfg(ci, pc);

        // Scene overlays must blend with detailed model pixels, rather than
        // forcing a native-resolution replacement during the next resolve.
        // Keep consecutive fog sprites in GS order in the same GPU batch.
        if (gs_vk_scene_sprite(ci, a, b)) return;
        gs_vk_flush();

        // The game tiles a separate dark patch behind each dialogue letter.
        // Its atlas is distinct from the glyph atlas and portrait. Suppress
        // only its RGB: the selected final outline/band replaces this patch;
        // original alpha/depth writes remain intact for GS compatibility.
        const bool caption_patch = g_ui_native && g_gs_crt_int && g_gs_crt_ffmd &&
            pc.tme && pc.abe && pc.fpsm == 0 && pc.fbw == 10 &&
            (pc.fbp == 0 || pc.fbp == 2240) && pc.ti.psm == 0x14 &&
            pc.ti.w == 64 && pc.ti.h == 32 && pc.ti.tbw == 2 &&
            pc.ti.csa16 == 0 && pc.ate && pc.atst == 6 && pc.aref == 1 && pc.afail == 0 && !pc.zactive &&
            g_st.ctx[ci].alpha == 0x44 && pc.ti.tcc && pc.ti.tfx == 0 &&
            std::fabs(x1-x0-16) < .01f && std::fabs(y1-y0-19) < .01f &&
            a.u == 0 && b.u == 39 && a.v == 0 && b.v == 19;
        if (caption_patch) { gs_ui_sprite_auxiliary(pc,a,b); return; }

        // The streamed field background uses 16x16 CT32 atlases and 16x8
        // field sprites with the signed-maximum screen-space Z sentinel.
        // Truncating that sentinel to Z16S writes 65535 over the scene depth
        // and rejects every character after the opening train battle.
        // Keep the backdrop read-only in depth; model and HUD depth writes
        // retain their normal GS rules. Do not apply this to ordinary sprites.
        if (pc.tme && pc.abe && pc.fpsm == 0 && pc.fbw == 10 &&
            (pc.fbp == 0 || pc.fbp == 2240) && pc.ti.psm == 0 &&
            pc.ti.w == 16 && pc.ti.h == 16 && pc.ti.tbw == 1 &&
            pc.ti.tfx == 0 && pc.ti.tcc && pc.zactive &&
            pc.zpsm == 0x3A && pc.ztst == 2 &&
            a.z == 0x7FFFFFFFu && b.z == 0x7FFFFFFFu &&
            std::fabs(x1 - x0 - 16.0f) < 0.01f &&
            std::fabs(y1 - y0 - 8.0f) < 0.01f &&
            std::fabs(std::fabs(b.u - a.u) - 16.0f) < 0.01f &&
            std::fabs(std::fabs(b.v - a.v) - 8.0f) < 0.01f)
            pc.zmsk = true;

        gs_ui_on_sprite(pc, px0, py0, px1, py1);

        // Teste M: usa a escolha basal de TEX1 do proprio decodificador, sem os
        // overrides posteriores para sprites. Opcao experimental: por padrao
        // preservamos integralmente o comportamento estavel do teste L.
        static const bool sprite_tex1 = std::getenv("PS2_GS_SPRITE_TEX1") != nullptr;
        static const bool sprite_probe = std::getenv("PS2_GS_SPRITE_PROBE") != nullptr;
        const bool tex1_linear = pc.ti.linear;

        // Sprite com mapeamento 1:1 (1 texel por pixel, tipico de fonte/interface): amostra por vizinho mais proximo.
        // Com o deslocamento de meia linha do modo campo o bilinear misturava cada texel com o vizinho e as linhas finas
        // do glifo (1 texel de altura, com alfa parcial) caiam abaixo do teste de alfa e sumiam ("a" virava "u").
        // PS2_SPRITE_BILINEAR=1 desliga esta regra.
        static const bool sprite_bilinear = std::getenv("PS2_SPRITE_BILINEAR") != nullptr;

        if (!sprite_tex1)
        {
            // Caminho original do teste L: preservado para a comparacao A/B.
            if (pc.ti.linear && !sprite_bilinear)
            {
                const float ru = (b.u - a.u) / (b.x - a.x), rv = (b.v - a.v) / (b.y - a.y);

                if (std::fabs(ru - 1.0f) < 0.02f && std::fabs(rv - 1.0f) < 0.02f)
                    pc.ti.linear = false;
            }
            else if (!pc.ti.linear && !sprite_bilinear)
            {
                // Em reducoes, o caminho antigo reativa bilinear ate quando TEX1 escolheu nearest.
                // Esta heuristica pode modificar as bordas finas dos glifos e por isso agora e testavel.
                static const bool no_auto = std::getenv("PS2_SPRITE_NOAUTOFILTER") != nullptr;
                const float ru = (b.u - a.u) / (b.x - a.x), rv = (b.v - a.v) / (b.y - a.y);

                if (!no_auto && (std::fabs(ru) < 0.98f || std::fabs(rv) < 0.98f) && std::fabs(ru) > 0.2f && std::fabs(rv) > 0.2f)
                    pc.ti.linear = true;
            }
        }

        // Teste O (opcional): teste de prefiltragem de cobertura vertical para
        // os glifos do menu Attack/Fusion/Item/Defend identificados na captura N.
        // Esses sprites usam atlas 4 bits em TBP=9504, 54x19 texels numa area
        // de 54x9,5 pixels. Os dois campos deslocados amostram linhas alternadas.
        // Nao altera o campo apresentado, geometria, GS global, DMA nem VU1.
        static const bool ui_vpair_enabled = std::getenv("PS2_GS_UI_VPAIR") != nullptr;
        if (ui_vpair_enabled && pc.tme && !pc.ti.linear &&
            pc.ti.psm == 0x14u && pc.ti.tbp == 9504u &&
            (x1 - x0) >= 32.0f && (x1 - x0) <= 55.0f &&
            (y1 - y0) >= 9.0f && (y1 - y0) <= 10.0f)
        {
            const float ru = (b.u - a.u) / (b.x - a.x);
            const float rv = (b.v - a.v) / (b.y - a.y);
            const int vbase = fast_floor(a.v);
            const int vsize = fast_floor(b.v - a.v + 0.5f);
            if (std::fabs(ru - 1.0f) < 0.03f &&
                std::fabs(rv - 2.0f) < 0.03f && vsize >= 18 && vsize <= 19)
            {
                pc.ti.ui_vpair = true;
                pc.ti.ui_vfirst = vbase;
                pc.ti.ui_vspan = vsize;
                static uint32_t shown = 0;
                if (shown++ < 12)
                    std::fprintf(stderr,
                        "  [GS UI O] filtro_vertical_par tex=%u XY=(%.1f,%.1f)-(%.1f,%.1f) V=(%.1f,%.1f) linhas=%d\n",
                        pc.ti.tbp, x0, y0, x1, y1, a.v, b.v, vsize);
            }
        }

        // Compoe os atlas de texto na imagem final de 448 linhas.
        // PS2_GS_UI_NATIVE=0
        // volta ao desenho direto no framebuffer.
        static const bool ui_native_enabled = g_ui_native;
        // Teste Q: instrumento diagnostico para verificar POR QUE o teste P nao
        // imprimiu seus registros. Nao altera pixels nem o comportamento do P.
        // [GS UI Q] versao identifica se o .exe foi regenerado/recompilado.
        // [GS UI Q] candidato detalha o predicado real do atlas da batalha.
        static const bool ui_qprobe = std::getenv("PS2_GS_UI_QPROBE") != nullptr;
        if (ui_qprobe)
        {
            static std::atomic<unsigned> q_versao{0u};
            if (q_versao.fetch_add(1u) == 0u)
                std::fprintf(stderr,
                    "  [GS UI Q] versao=Q instalado=1 native=%d vpair=%d field=%s\n",
                    g_ui_native ? 1 : 0,
                    std::getenv("PS2_GS_UI_VPAIR") != nullptr ? 1 : 0,
                    g_field_recon_mode);

            // Captura exatamente o atlas PSMT4 identificado pela captura F9 N,
            // mesmo quando outras condicoes do P rejeitam o sprite.
            if (pc.tme && pc.ti.tbp == 9504u && pc.ti.psm == 0x14u &&
                x1 - x0 >= 32.0f && x1 - x0 <= 55.0f &&
                y1 - y0 >= 8.5f && y1 - y0 <= 10.5f)
            {
                static std::atomic<unsigned> q_amostras{0u};
                const unsigned n = q_amostras.fetch_add(1u);
                if (n < 24u)
                {
                    const float du = (b.u - a.u) / (b.x - a.x);
                    const float dv = (b.v - a.v) / (b.y - a.y);
                    const int w = (int)std::lround(x1 - x0);
                    const int h = (int)std::lround((y1 - y0) * 2.0f);
                    const bool estados = !pc.ti.linear && pc.ti.tfx == 0u && pc.ti.tcc &&
                        pc.fpsm == 0u && pc.fbmsk == 0u &&
                        (pc.fbp == 0u || pc.fbp == 2240u) &&
                        !pc.zactive && pc.abe && g_st.ctx[ci].alpha == 0x44u;
                    const bool uv_ok = std::fabs(du - 1.0f) < 0.03f &&
                        std::fabs(dv - 2.0f) < 0.03f &&
                        w >= 32 && w <= 55 && h >= 18 && h <= 20 &&
                        a.u >= 0.0f && a.v >= 0.0f;
                    std::fprintf(stderr,
                        "  [GS UI Q] candidato=%u native=%d estados=%d uv=%d fbp=%u xy=(%.2f,%.2f)-(%.2f,%.2f)"
                        " du=%.5f dv=%.5f wh=%dx%d linear=%d tfx=%u tcc=%d fpsm=%u fbmsk=%08X"
                        " zactive=%d abe=%d alpha=%llX\n",
                        n, ui_native_enabled ? 1 : 0, estados ? 1 : 0, uv_ok ? 1 : 0,
                        pc.fbp / 32u, x0, y0, x1, y1, du, dv, w, h,
                        pc.ti.linear ? 1 : 0, pc.ti.tfx, pc.ti.tcc ? 1 : 0,
                        pc.fpsm, pc.fbmsk, pc.zactive ? 1 : 0, pc.abe ? 1 : 0,
                        (unsigned long long)g_st.ctx[ci].alpha);
                }
            }
        }
        // As capturas de itens e da conversa com Bacon usam a mesma
        // paleta de fonte. Os atlas mudam de endereco e podem ter 128 ou
        // 256 texels de largura (nome, primeira e segunda linha da fala).
        const bool caption_font = pc.ti.psm == 0x13u && pc.ti.h == 64 &&
            (pc.ti.w == 128 || pc.ti.w == 256) && pc.ti.tbw == unsigned(pc.ti.w / 64) &&
            ((g_st.ctx[ci].tex0 >> 37) & 0x3FFFu) == 16348u;
        const unsigned font_palette = unsigned((g_st.ctx[ci].tex0 >> 37) & 0x3FFFu);
        const bool system_font = pc.ti.psm == 0x14u && font_palette == 16344u;
        const bool menu_font = pc.ti.psm == 0x14u &&
            ((font_palette == 5492u && pc.ti.w == 128 && pc.ti.h == 64) ||
             (font_palette == 5613u && pc.ti.w == 256 && pc.ti.h == 64));
        // BtlStat2: nomes e valores do HUD, atlas 512x128 da captura de batalha.
        // Usa Z ALWAYS com escrita, por isso era rejeitado pelo caminho nativo
        // e perdia metade das linhas da fonte no framebuffer de 224 linhas.
        // These UI allocations move with the battle's loaded resources.
        // Atlas layout, CSA and the sprite/depth/UV checks identify the font;
        // a fixed CLUT address misses the first Bacon battle on the train.
        const bool battle_font = pc.ti.psm == 0x14u &&
            pc.ti.w == 512 && pc.ti.h == 128 && pc.ti.tbw == 8u;
        const bool result_font = pc.ti.psm == 0x13u &&
            ((font_palette == 5708u && pc.ti.w == 256 && pc.ti.h == 128 && pc.ti.tbw == 4u) ||
             (font_palette == 5732u && pc.ti.w == 128 && pc.ti.h == 32 && pc.ti.tbw == 2u));
        const bool result_gold_label = pc.ti.psm == 0x14u && font_palette == 5582u &&
            pc.ti.w == 1024 && pc.ti.h == 128 && pc.ti.tbw == 10u &&
            a.u == 172.0f && a.v == 40.0f && b.u == 180.0f && b.v == 56.0f;
        // Nome no topo e ajuda inferior: paleta 5532, CSA 4. A faixa de
        // ajuda usa TBP 5588, abaixo do limite usado pelos outros atlas.
        const bool battle_prompt_font = pc.ti.psm == 0x14u &&
            pc.ti.csa16 == 64u && pc.ti.h == 32 &&
            ((pc.ti.w == 256 && pc.ti.tbw == 4u) ||
             (pc.ti.w == 1024 && pc.ti.tbw == 10u));
        // Cyan current HP/MP/SP beside the ring use the high nibble of a
        // CT32 texture (PSMT4HH), not the ordinary PSMT4 font format.
        const bool ring_status_font = pc.ti.psm == 0x2Cu && pc.ti.csa16 == 48u &&
            pc.ti.w == 256 && pc.ti.h == 64 && pc.ti.tbw == 4u;
        const bool known_font = caption_font || system_font || menu_font || battle_font || battle_prompt_font || ring_status_font || result_font || result_gold_label;
        const bool writable_font_depth = known_font && pc.zactive && pc.ztst == 1u &&
            pc.zbp >= 4480u && pc.zbp <= 15000u && (pc.ti.tbp >= 5760u || battle_prompt_font);
        if (ui_native_enabled && g_gs_crt_int && g_gs_crt_ffmd &&
            pc.tme && (!pc.ti.linear || known_font) && (pc.ti.psm == 0x14u || caption_font || result_font || ring_status_font) &&
            pc.ti.tfx == 0u && pc.ti.tcc &&
            pc.fpsm == 0u && pc.fbmsk == 0u &&
            (pc.fbp == 0u || pc.fbp == 2240u) &&
            (!pc.ate || pc.afail == 0u || known_font) &&
            (!pc.zactive || writable_font_depth) && pc.abe &&
            g_st.ctx[ci].alpha == 0x44u &&
            (x1 - x0) >= 1.0f && (x1 - x0) <= 640.0f &&
            (y1 - y0) >= 7.0f && (y1 - y0) <= (known_font ? 32.0f : 13.0f))
        {
            const float du = (b.u - a.u) / (b.x - a.x);
            const float dv = (b.v - a.v) / (b.y - a.y);
            const int w = (int)std::lround(x1 - x0);
            const int h = (int)std::lround((y1 - y0) * 2.0f);

            // Os atlas de dialogo tambem usam duas linhas de textura por
            // linha do campo. Preserve ambas na imagem final, como no menu.
            const bool shadow = b.r == 0 && b.g == 0 && b.b == 0;
            if ((std::fabs(du - 1.0f) < 0.03f || (known_font && shadow && std::fabs((b.u-a.u)-w) <= 1.05f) ||
                 (ring_status_font && std::fabs((b.u-a.u)-w) <= 1.05f)) &&
                (std::fabs(dv - 2.0f) < 0.03f || (caption_font && std::fabs(dv - 1.0f) < 0.03f) ||
                 (known_font && shadow && std::fabs((b.v-a.v)-h) <= 1.05f) ||
                 (ring_status_font && std::fabs((b.v-a.v)-h) <= 2.05f)) &&
                w >= 1 && w <= 640 && h >= 14 && h <= 64 &&
                a.u >= 0.0f && a.v >= 0.0f)
            {
                UiGlyphP glyph;
                glyph.fbp = pc.fbp;
                glyph.vsync = g_gs_vsyncs;
                glyph.x = (int)std::lround(x0);
                glyph.y = (int)std::lround(y0 * 2.0f);
                glyph.w = w;
                glyph.h = h;
                glyph.outline = caption_font;
                glyph.clip_x0 = pc.sx0; glyph.clip_x1 = pc.sx1 + 1;
                glyph.clip_y0 = pc.sy0 * 2; glyph.clip_y1 = (pc.sy1 + 1) * 2;
                glyph.rgba.resize((size_t)w * (size_t)h);

                // Faz snapshot dos texels enquanto a CLUT e o TEX0 deste
                // sprite ainda estao ativos. Nao depende de estados futuros.
                const int vr = gs_f2i255(b.r);
                const int vg = gs_f2i255(b.g);
                const int vb = gs_f2i255(b.b);
                glyph.tint = uint32_t(vr | (vg << 8) | (vb << 16));
                const int original_alpha = gs_f2i255(b.a);
                const int va = gs_text_vertex_alpha(b);
                TexInfo native_texture = pc.ti;
                native_texture.ui_vpair = false;
                for (int py = 0; py < h; ++py)
                    for (int px = 0; px < w; ++px)
                    {
                        const int screen_x = glyph.x + px, screen_y = glyph.y + py;
                        if (screen_x < pc.sx0 || screen_x > pc.sx1 ||
                            screen_y < pc.sy0 * 2 || screen_y >= (pc.sy1 + 1) * 2)
                            continue;
                        const float u = a.u + (b.u - a.u) * ((px + 0.5f) / w);
                        const float v = a.v + (b.v - a.v) * ((py + 0.5f) / h);
                        uint32_t tex = gs_sample(native_texture, u, v);
                        if (caption_font)
                        {
                            const float sample_y = v - 0.5f;
                            const int row = fast_floor(sample_y);
                            const int first = fast_floor(a.v), last = fast_floor(b.v - 0.01f);
                            const unsigned fraction = unsigned(std::lround((sample_y - row) * 256.0f));
                            tex = gs_text_lerp(gs_texel(pc.ti, fast_floor(u), std::clamp(row, first, last)),
                                gs_texel(pc.ti, fast_floor(u), std::clamp(row + 1, first, last)), fraction);
                        }
                        glyph.rgba[(size_t)py*w+px] = gs_ui_glyph_pixel(pc,tex,glyph.tint,va,original_alpha);
                    }

                static const bool smooth_text = ps2_settings().text_aa && battle_profile_enabled("PS2_GS_UI_EDGEAA");
                if (smooth_text && !caption_font)
                    gs_ui_filter(glyph);
                // O filtro de cobertura nao deve vazar para fora da janela
                // de dialogo, inclusive durante rolagem e abertura.
                if (glyph.x < pc.sx0 || glyph.x + w > pc.sx1 + 1 ||
                    glyph.y < pc.sy0 * 2 || glyph.y + h > (pc.sy1 + 1) * 2)
                    for (int py = 0; py < h; ++py)
                        for (int px = 0; px < w; ++px)
                            if (glyph.x + px < pc.sx0 || glyph.x + px > pc.sx1 ||
                                glyph.y + py < pc.sy0 * 2 || glyph.y + py >= (pc.sy1 + 1) * 2)
                                glyph.rgba[size_t(py) * w + px] = 0;

                {
                    std::lock_guard<std::mutex> lk(g_ui_native_mu);
                    auto& cache = g_ui_native_glyphs;

                    // O contorno pode ter varias passagens pretas, separadas
                    // por apenas um pixel. Preserve cada desenho e sua ordem,
                    // inclusive quando ele reutiliza a cor ou a paleta da letra.
                    // A limpeza no reuso do framebuffer evita acumulo temporal.
                    if (cache.size() >= 2048)
                        cache.erase(cache.begin());
                    cache.push_back(std::move(glyph));
                }
                static unsigned reports_p = 0;
                if (reports_p++ < 10u)
                    std::fprintf(stderr,
                        "  [GS UI P] fonte_nativa fbp=%u XY=(%.1f,%.1f) %dx%d pixels finais\n",
                        pc.fbp / 32u, x0, y0, w, h);

                // Nao desenhar a versao de 9,5 linhas: sera composta apos
                // a reconstrucao espacial, com 19 linhas originais.
                gs_ui_sprite_auxiliary(pc,a,b);
                return;
            }
        }

        // Teste M: diagnostico restrito aos sprites pequenos texturizados da UI.
        // As primeiras 48 ocorrencias e uma amostra a cada 512 sao impressas;
        // nenhum acesso a textura ou pixel e modificado pelo diagnostico.
        if (sprite_probe && pc.tme &&
            (x1 - x0) <= 32.0f && (y1 - y0) <= 32.0f)
        {
            static uint64_t small_sprite_count = 0;
            const uint64_t n = ++small_sprite_count;

            if (n <= 48 || (n % 512u) == 0)
            {
                const GsCtx& ctx = g_st.ctx[ci];
                const float du = (b.u - a.u) / (b.x - a.x);
                const float dv = (b.v - a.v) / (b.y - a.y);
                const unsigned mmag = (unsigned)((ctx.tex1 >> 5) & 1u);
                const unsigned mmin = (unsigned)((ctx.tex1 >> 6) & 7u);

                // Calcula qual filtro a heuristica antiga aplicaria neste mesmo sprite.
                // Assim um unico teste M mostra se a alteracao afeta a letra examinada.
                bool legacy_linear = tex1_linear;

                if (!sprite_bilinear)
                {
                    if (legacy_linear && std::fabs(du - 1.0f) < 0.02f && std::fabs(dv - 1.0f) < 0.02f)
                        legacy_linear = false;
                    else if (!legacy_linear && std::getenv("PS2_SPRITE_NOAUTOFILTER") == nullptr &&
                             (std::fabs(du) < 0.98f || std::fabs(dv) < 0.98f) &&
                             std::fabs(du) > 0.2f && std::fabs(dv) > 0.2f)
                        legacy_linear = true;
                }

                std::fprintf(stderr,
                    "  [GS SPRITE M] n=%llu ctx=%d FBP=%u "
                    "XY=(%.2f,%.2f)-(%.2f,%.2f) UV=(%.2f,%.2f)-(%.2f,%.2f) "
                    "dUV=(%.3f,%.3f) PSM=%02X TBP=%u TEX0=%016llX TEX1=%016llX "
                    "MMAG=%u MMIN=%u tex1_linear=%u legado_linear=%u usado_linear=%u mudaria=%u "
                    "ATE=%u ATST=%u AREF=%d CLAMP=%016llX modo_tex1=%u\n",
                    (unsigned long long)n, ci, pc.fbp,
                    x0, y0, x1, y1, a.u, a.v, b.u, b.v,
                    du, dv, pc.ti.psm, pc.ti.tbp,
                    (unsigned long long)ctx.tex0, (unsigned long long)ctx.tex1,
                    mmag, mmin, tex1_linear ? 1u : 0u,
                    legacy_linear ? 1u : 0u, pc.ti.linear ? 1u : 0u,
                    (legacy_linear != pc.ti.linear) ? 1u : 0u,
                    pc.ate ? 1u : 0u, pc.atst, pc.aref,
                    (unsigned long long)ctx.clamp, sprite_tex1 ? 1u : 0u);
            }
        }

        // recorta ao scissor uma vez (em vez de testar pixel a pixel)
        px0 = std::max(px0, pc.sx0); px1 = std::min(px1, pc.sx1 + 1);
        py0 = std::max(py0, pc.sy0); py1 = std::min(py1, pc.sy1 + 1);

        // As outras fontes desenhadas em modo campo usam duas linhas do
        // atlas por pixel. Integre as duas coberturas antes do blend.
        static const bool smooth_text = ps2_settings().text_aa && battle_profile_enabled("PS2_GS_UI_EDGEAA");
        const float dx = b.x - a.x, dy = b.y - a.y;
        const float du = b.u - a.u, dv = b.v - a.v;
        if (smooth_text && g_gs_crt_int && pc.tme && pc.ti.psm == 0x14u &&
            !pc.ti.linear && pc.ti.tfx == 0u && pc.ti.tcc && pc.abe &&
            g_st.ctx[ci].alpha == 0x44u && !pc.zactive && (!pc.ate || pc.afail == 0) &&
            dx >= 4 && dx <= 480 && dy >= 7 && dy <= 13 &&
            du >= dx * 0.5f && du <= dx * 1.5f && std::fabs(dv - dy * 2) < 0.13f)
        {
            const int r = gs_f2i255(b.r), g = gs_f2i255(b.g), bl = gs_f2i255(b.b);
            const int original_alpha = gs_f2i255(b.a), alpha = gs_text_vertex_alpha(b);
            const int first_v = fast_floor(a.v), last_v = fast_floor(b.v - 0.01f);
            const PixCfg test = pc;
            pc.ate = false;
            const auto sample = [&](int u, int v)
            {
                const uint32_t texel = gs_texel(pc.ti, u, std::clamp(v, first_v, last_v));
                const int tex_alpha = std::min(255, int(texel >> 24) * original_alpha >> 7);
                if (!gs_alpha_pass(test, tex_alpha))
                    return 0u;
                const unsigned coverage = std::min(128, int(texel >> 24) * alpha >> 7);
                return (texel & 0x00FFFFFFu) | (coverage << 24);
            };
            for (int y = py0; y < py1; ++y)
            {
                const int v = fast_floor(a.v + (y + 0.5f - a.y) * (dv / dy) - 0.5f);
                for (int x = px0; x < px1; ++x)
                {
                    const int u = fast_floor(a.u + (x + 0.5f - a.x) * (du / dx));
                    const uint32_t texel = gs_text_pair(sample(u, v), sample(u, v + 1));
                    if (texel >> 24)
                        gs_shade_pixel<true>(pc, x, y, r, g, bl, 128, texel, b.z);
                }
            }
            return;
        }

        if ((long long)(px1 - px0) * (long long)(py1 - py0) >= 2048 && gs_sprite_fast(pc, a, b, px0, px1, py0, py1))
            return;

        if (g_perf_gs && px1 > px0 && py1 > py0)
            g_prof_sprite_generic_pixels.fetch_add((uint64_t)(px1 - px0) * (py1 - py0), std::memory_order_relaxed);
        const float ixr = 1.0f / (b.x - a.x), iyr = 1.0f / (b.y - a.y);

        if (g_sprite_generic_rows && px1 > px0 && py1 - py0 >= 2)
        {
            if (pc.fpsm == 0)
                gs_sprite_generic_rows<true>(pc, a, b, px0, px1, py0, py1, ixr, iyr);
            else
                gs_sprite_generic_rows<false>(pc, a, b, px0, px1, py0, py1, ixr, iyr);
            return;
        }

        for (int y = py0; y < py1; y++)
        {
            const float fv = ((float)y + 0.5f - a.y) * iyr;
            const float vv = a.v + (b.v - a.v) * fv;

            for (int x = px0; x < px1; x++)
            {
                const float fu = ((float)x + 0.5f - a.x) * ixr;
                gs_pixel(pc, x, y, b.r, b.g, b.b, b.a, a.u + (b.u - a.u) * fu, vv, b.z);
            }
        }
    }

    // Teste T: cobertura subpixel de arestas 3D, isolada do framebuffer PS2.
    // A VRAM/z-buffer permanecem SEM ALTERACOES. A mascara opcional e apenas
    // uma sugestao geometrica para a apresentacao em 640x448; nao e SSAA 2x real.
    struct GsEdgeT
    {
        uint32_t rgba = 0;             // Ultima cor RGBA32 gravada pelo triangulo no GS.
        uint32_t vsync = 0;            // Evita reutilizar bordas antigas indefinidamente.
        uint8_t coverage = 0;          // 1..3 de quatro subamostras geometrico-espaciais.
        int8_t outside_x = 0;         // Direcao do exterior do triangulo (x ou y).
        int8_t outside_y = 0;
    };
    std::mutex g_edge_t_mu;
    std::vector<GsEdgeT> g_edge_t_frame[2];
    std::atomic<uint64_t> g_edge_t_triangles{0}, g_edge_t_border_pixels{0};
    static bool gs_edge_t_enabled()
    {
        static const char* value = std::getenv("PS2_GS_TRI_COVERAGE_T");
        return value != nullptr && value[0] == '1';
    }

    // Teste U: auditoria do TEX1 e teste controlado de alinhamento UV do caminho 3D.
    // Todos os controles sao DESLIGADOS por padrao. Nao alterar sprites, GS VRAM,
    // rasterizacao 2D, FIELDLOCK, blending, Z-buffer, VIF, DMA ou microprogramas VU.
    // Um desvio positivo move a amostragem dentro da textura, em unidades de texel.
    // NAO equivale a antialiasing e NAO e uma correcao validada: A/B diagnostico.
    static bool gs_tex_u_audit_enabled()
    {
        static const char* env = std::getenv("PS2_GS_TEX_AUDIT_U");
        return env != nullptr && env[0] == '1';
    }

    static float gs_tex_uv_bias_u()
    {
        static const float bias = []() -> float
        {
            const char* env = std::getenv("PS2_GS_TRI_UV_BIAS_U");
            if (env == nullptr || env[0] == '\0') return 0.0f;
            char* end = nullptr;
            const float v = std::strtof(env, &end);
            if (end == env || *end != '\0' || !std::isfinite(v)) return 0.0f;
            // Limitar a meia amostra (em texels) para impedir um erro grosseiro.
            return std::max(-0.5f, std::min(0.5f, v));
        }();
        return bias;
    }

    unsigned g_gs_replay_triangles = 0; // Acesso exclusivo do executor GS.
    std::atomic<uint64_t> g_tex_u_3d{0}, g_tex_u_linear{0}, g_tex_u_nearest{0};
/* SH_EMBEDDED_VULKAN_BATCH */

    static bool gs_ui_copyright(int ci, const GsVertex& a, const GsVertex& b,
                                const GsVertex& c, const PixCfg& pc)
    {
        // This title-screen strip uses two triangles, unlike the menu sprites.
        // Restrict the lift to its font palette; never move the logo's shadow.
        if (!g_ui_native || !g_gs_crt_int || !g_gs_crt_ffmd || !pc.tme ||
            pc.ti.psm != 0x14u || pc.ti.w != 1024 || pc.ti.h != 64 ||
            ((g_st.ctx[ci].tex0 >> 37) & 16383u) != 5470u ||
            pc.fbmsk || pc.fpsm || pc.zactive || !pc.abe || pc.ti.tfx || !pc.ti.tcc ||
            g_st.ctx[ci].alpha != 0x44u || (pc.fbp != 0u && pc.fbp != 2240u) ||
            a.r != b.r || b.r != c.r || a.g != b.g || b.g != c.g ||
            a.b != b.b || b.b != c.b || a.a != b.a || b.a != c.a ||
            a.q != b.q || b.q != c.q || a.q == 0) return false;
        const float x0=std::min({a.x,b.x,c.x}), y0=std::min({a.y,b.y,c.y});
        const int w=int(std::lround(std::max({a.x,b.x,c.x})-x0));
        const int h=int(std::lround((std::max({a.y,b.y,c.y})-y0)*2));
        if (w<1 || w>640 || h<14 || h>64) return false;
        gs_vk_flush();
        const float area=(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
        if (!area) return false;
        UiGlyphP glyph;
        glyph.fbp=pc.fbp; glyph.vsync=g_gs_vsyncs;
        glyph.x=int(std::lround(x0)); glyph.y=int(std::lround(y0*2));
        glyph.w=w; glyph.h=h; glyph.rgba.resize(size_t(w)*h);
        glyph.tint=uint32_t(gs_f2i255(c.r)|(gs_f2i255(c.g)<<8)|(gs_f2i255(c.b)<<16));
        TexInfo texture=pc.ti; texture.ui_vpair=false;
        auto weights=[&](float x,float y,float& wa,float& wb,float& wc) {
            wa=((b.x-x)*(c.y-y)-(b.y-y)*(c.x-x))/area;
            wb=((c.x-x)*(a.y-y)-(c.y-y)*(a.x-x))/area;
            wc=1-wa-wb;
        };
        for(int y=0;y<h;++y) for(int x=0;x<w;++x) {
            float wa,wb,wc; weights(x0+x+.5f,y0+(y+.5f)*.5f,wa,wb,wc);
            const float u=a.persp ? (wa*a.us+wb*b.us+wc*c.us)/a.q : wa*a.u+wb*b.u+wc*c.u;
            const float v=a.persp ? (wa*a.vs+wb*b.vs+wc*c.vs)/a.q : wa*a.v+wb*b.v+wc*c.v;
            glyph.rgba[size_t(y)*w+x]=gs_ui_glyph_pixel(pc,gs_sample(texture,u,v),glyph.tint,
                gs_text_vertex_alpha(c),gs_f2i255(c.a));
        }
        if(ps2_settings().text_aa && battle_profile_enabled("PS2_GS_UI_EDGEAA")) gs_ui_filter(glyph);
        auto edge=[&](const GsVertex& p,const GsVertex& q,float x,float y) {
            const float dx=(q.x-p.x)*(area>0?1:-1), dy=(q.y-p.y)*(area>0?1:-1);
            const float e=dx*(y-p.y)-dy*(x-p.x);
            return e>0 || (e==0 && (dy>0 || (dy==0 && dx<0)));
        };
        for(int y=0;y<h;++y) for(int x=0;x<w;++x) {
            const float sx=x0+x+.5f, sy=y0+(y+.5f)*.5f;
            if(sx<pc.sx0 || sx>=pc.sx1+1 || sy<pc.sy0 || sy>=pc.sy1+1 ||
                !edge(a,b,sx,sy) || !edge(b,c,sx,sy) || !edge(c,a,sx,sy))
                glyph.rgba[size_t(y)*w+x]=0;
        }
        { std::lock_guard<std::mutex> lock(g_ui_native_mu);
          if(g_ui_native_glyphs.size()>=2048) g_ui_native_glyphs.erase(g_ui_native_glyphs.begin());
          g_ui_native_glyphs.push_back(std::move(glyph)); }
        const uint64_t frame=g_st.ctx[ci].frame;
        g_st.ctx[ci].frame=frame|(uint64_t(0x00FFFFFFu)<<32);
        gs_draw_triangle(ci,a,b,c); // Preserve alpha; mask also prevents recursion.
        g_st.ctx[ci].frame=frame;
        return true;
    }

    void gs_draw_triangle(int ci, const GsVertex& a, const GsVertex& b, const GsVertex& c)
    {
        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        if (area == 0.0f)
        {
            g_trace_degen++;
            return;
        }

        gs_trace_draw("tri", ci, std::min(a.x, std::min(b.x, c.x)), std::min(a.y, std::min(b.y, c.y)), std::max(a.x, std::max(b.x, c.x)), std::max(a.y, std::max(b.y, c.y)));

        const RasterProfScope prof(g_prof_tri_ns);
        int minx = (int)fast_floor(std::min(a.x, std::min(b.x, c.x))), maxx = (int)fast_ceil(std::max(a.x, std::max(b.x, c.x)));
        int miny = (int)fast_floor(std::min(a.y, std::min(b.y, c.y))), maxy = (int)fast_ceil(std::max(a.y, std::max(b.y, c.y)));

        minx = std::max(minx, 0); miny = std::max(miny, 0);
        maxx = std::min(maxx, 2047); maxy = std::min(maxy, 2047);

        PixCfg pc;
        gs_pix_cfg(ci, pc);

        if (gs_ui_copyright(ci,a,b,c,pc)) return;

        // Condicoes reais da batalha: somente triangulos com STQ/perspectiva
        // destinados aos framebuffers FBP 0/70 (nao os sprites TEX1=0 da UI).
        // A contagem pode ser ativada isoladamente para um baseline fiel.
        const bool tex_u_3d = a.persp && b.persp && c.persp && pc.tme &&
            pc.fpsm == 0u && pc.fbw == 10u &&
            (pc.fbp == 0u || pc.fbp == 2240u);
        const float tex_u_bias = tex_u_3d ? gs_tex_uv_bias_u() : 0.0f;
        if (tex_u_3d && gs_tex_u_audit_enabled())
        {
            const uint64_t index = g_tex_u_3d.fetch_add(1, std::memory_order_relaxed);
            if (pc.ti.linear)
                g_tex_u_linear.fetch_add(1, std::memory_order_relaxed);
            else
                g_tex_u_nearest.fetch_add(1, std::memory_order_relaxed);
            if (index < 8u)
            {
                const uint64_t tex1 = g_st.ctx[ci].tex1;
                std::fprintf(stderr,
                    "  [GS TEX U] amostra=%llu fbp=%u TEX1=0x%llX MMAG=%u MMIN=%u bilinear=%u "
                    "PSM=0x%X TBP=%u bias=%.3f\n",
                    (unsigned long long)index, pc.fbp / 32u,
                    (unsigned long long)tex1, (unsigned)((tex1 >> 5) & 1u),
                    (unsigned)((tex1 >> 6) & 7u), pc.ti.linear ? 1u : 0u,
                    pc.ti.psm, pc.ti.tbp, (double)tex_u_bias);
            }
        }
        minx = std::max(minx, pc.sx0); maxx = std::min(maxx, pc.sx1);
        miny = std::max(miny, pc.sy0); maxy = std::min(maxy, pc.sy1);

        if (g_gs_replay_capture_enabled && minx <= maxx && miny <= maxy)
            ++g_gs_replay_triangles;

        if (gs_vk_enqueue(ci, a, b, c, pc, minx, maxx, miny, maxy))
            return;
        gs_vk_flush();

        const bool persp = a.persp && b.persp && c.persp;

        // Apenas triangulos 3D texturizados nos dois framebuffers documentados
        // (FBP 0 e 70), sem tocar nos sprites de texto ou nos menus 2D.
        const bool edge_t = gs_edge_t_enabled() && persp && pc.tme &&
            pc.fpsm == 0u && pc.fbw == 10u &&
            (pc.fbp == 0u || pc.fbp == 2240u);
        std::unique_lock<std::mutex> edge_t_lock(g_edge_t_mu, std::defer_lock);
        std::vector<GsEdgeT>* edge_t_frame = nullptr;
        if (edge_t)
        {
            // Trava somente enquanto o GS desenha UM triangulo. A thread da
            // apresentacao copia a mascara sob o mesmo mutex, sem data races.
            edge_t_lock.lock();
            const int idx = pc.fbp == 0u ? 0 : 1;
            auto& frame = g_edge_t_frame[idx];
            if (frame.size() != 640u * 224u)
                frame.resize(640u * 224u);
            edge_t_frame = &frame;
            g_edge_t_triangles.fetch_add(1, std::memory_order_relaxed);
        }

        // Triangulos 2D grandes de cor plana (quads de UI, fundos): usa o caminho rapido por linha.
        bool use_fast = false;
        static thread_local FastRaster fr;

        if (!g_nofastsprite && !persp && (long long)(maxx - minx + 1) * (long long)(maxy - miny + 1) >= 1024
            && a.r == b.r && b.r == c.r && a.g == b.g && b.g == c.g && a.b == b.b && b.b == c.b && a.a == b.a && b.a == c.a)
        {
            use_fast = fr.setup(pc, c.r, c.g, c.b, c.a, std::min(a.u, std::min(b.u, c.u)), std::max(a.u, std::max(b.u, c.u)),
                std::min(a.v, std::min(b.v, c.v)), std::max(a.v, std::max(b.v, c.v)));
        }

        // Pesos baricentricos lineares em px: w0 = k0 + s0*px, w1 = k1 + s1*px, w2 = 1 - w0 - w1. Para cada linha
        // resolve o intervalo de px com os tres pesos >= 0 e percorre so ele.
        const float inv = 1.0f / area;
        const float s0 = (b.y - c.y) * inv, s1 = (c.y - a.y) * inv, s2 = -(s0 + s1);
        const float c0 = (b.x * c.y - b.y * c.x) * inv, d0 = (c.x - b.x) * inv;
        const float c1 = (c.x * a.y - c.y * a.x) * inv, d1 = (a.x - c.x) * inv;
        const bool early_z = g_gs_tri_early_z && !edge_t;
        const GsTextureSampler sampler = g_gs_tri_texture_specialize && early_z && pc.tme && pc.fpsm == 0
            ? gs_tri_sampler(pc.ti) : nullptr;
        // O caminho especializado do GS ja conhece o formato de cor.
        // Em Z16S, reutiliza o mesmo endereco no teste e na escrita.
        const bool fast_effect = g_gs_tri_effects_fast && sampler != nullptr;
#if (defined(__SSE2__) || defined(_M_X64)) && !defined(__FMA__) && !defined(__FAST_MATH__)
        const bool rgba_sse2 = g_gs_tri_rgba_sse2 && early_z && pc.fpsm == 0 &&
            (!pc.tme || sampler != nullptr);
        const __m128 rgba_a = _mm_setr_ps(a.r, a.g, a.b, a.a);
        const __m128 rgba_b = _mm_setr_ps(b.r, b.g, b.b, b.a);
        const __m128 rgba_c = _mm_setr_ps(c.r, c.g, c.b, c.a);
        const __m128 rgba_min = _mm_setzero_ps();
        const __m128 rgba_max = _mm_set1_ps(255.0f);
        const bool direct_z16s = (fast_effect || (rgba_sse2 && !pc.tme)) && pc.zactive && pc.zpsm == 0x3Au;
#else
        const bool direct_z16s = fast_effect && pc.zactive && pc.zpsm == 0x3Au;
#endif

        const bool row_address = g_gs_tri_row_fast && (sampler != nullptr || direct_z16s);
#if (defined(__SSE2__) || defined(_M_X64)) && !defined(__FMA__) && !defined(__FAST_MATH__)
        const bool row_address_rgba = g_gs_tri_row_fast && rgba_sse2;
#else
        constexpr bool row_address_rgba = false;
#endif
        const bool use_row_address = row_address || row_address_rgba;
        for (int y = miny; y <= maxy; y++)
        {
            const float py = (float)y + 0.5f;
            const float k0 = c0 + d0 * py, k1 = c1 + d1 * py, k2 = 1.0f - k0 - k1;
            const float ks[3] = { k0, k1, k2 }, ss[3] = { s0, s1, s2 };
            float lo = -1e30f, hi = 1e30f;
            bool empty = false;

            for (int i = 0; i < 3; i++)
            {
                if (ss[i] > 1e-9f) lo = std::max(lo, -ks[i] / ss[i]);
                else if (ss[i] < -1e-9f) hi = std::min(hi, -ks[i] / ss[i]);
                else if (ks[i] < 0.0f) empty = true;
            }

            if (empty || lo > hi)
                continue;

            const int xs = std::max(minx, (int)fast_ceil(lo - 0.5f - 1e-4f)), xe = std::min(maxx, (int)fast_floor(hi - 0.5f + 1e-4f));

            if (use_fast)
            {
                if (xs <= xe)
                {
                    const float px = (float)xs + 0.5f;
                    const float w0 = k0 + s0 * px, w1 = k1 + s1 * px, w2 = 1.0f - w0 - w1;
                    fr.row(y, xs, xe, a.u * w0 + b.u * w1 + c.u * w2, a.u * s0 + b.u * s1 + c.u * s2,
                        a.v * w0 + b.v * w1 + c.v * w2, a.v * s0 + b.v * s1 + c.v * s2,
                        (float)a.z * w0 + (float)b.z * w1 + (float)c.z * w2, (float)a.z * s0 + (float)b.z * s1 + (float)c.z * s2);
                }

                continue;
            }

            // FBP/ZBP e a faixa em Y ficam fixos nesta linha. O pixel
            // conserva a selecao de pagina em X e a mascara de wrap da VRAM.
            const uint32_t row_y = (uint32_t)y;
            const uint16_t* color_row = use_row_address ? g_page_ct32[row_y & 31u] : nullptr;
            const uint16_t* depth_row = use_row_address && direct_z16s ? g_page_z16s[row_y & 63u] : nullptr;
            const uint32_t color_base = use_row_address
                ? (((pc.fbp >> 5) + (row_y >> 5) * pc.fbw) << 11) + ((pc.fbp & 31u) << 6) : 0u;
            const uint32_t depth_base = depth_row
                ? (((pc.zbp >> 5) + (row_y >> 6) * pc.fbw) << 12) + ((pc.zbp & 31u) << 7) : 0u;

#if (defined(__SSE2__) || defined(_M_X64)) && !defined(__FMA__) && !defined(__FAST_MATH__)
            alignas(16) float weights0[4], weights1[4], weights2[4], depths[4];
            const bool group_depth = use_row_address && rgba_sse2 && xe - xs >= 7;
#endif
            for (int x = xs; x <= xe; x++)
            {
                const uint32_t ux = (uint32_t)x;
                float w0, w1, w2, zf;
#if (defined(__SSE2__) || defined(_M_X64)) && !defined(__FMA__) && !defined(__FAST_MATH__)
                if (group_depth)
                {
                    const unsigned lane = (unsigned)(x - xs) & 3u;
                    if (lane == 0u)
                    {
                        // Agrupa somente a matematica. A VRAM ainda e lida e
                        // escrita na ordem original, pixel por pixel.
                        const __m128 px4 = _mm_add_ps(_mm_set1_ps((float)x), _mm_setr_ps(0.5f, 1.5f, 2.5f, 3.5f));
                        const __m128 w04 = _mm_add_ps(_mm_set1_ps(k0), _mm_mul_ps(_mm_set1_ps(s0), px4));
                        const __m128 w14 = _mm_add_ps(_mm_set1_ps(k1), _mm_mul_ps(_mm_set1_ps(s1), px4));
                        const __m128 w24 = _mm_sub_ps(_mm_sub_ps(_mm_set1_ps(1.0f), w04), w14);
                        const __m128 zab = _mm_add_ps(_mm_mul_ps(_mm_set1_ps((float)a.z), w04),
                            _mm_mul_ps(_mm_set1_ps((float)b.z), w14));
                        _mm_store_ps(weights0, w04);
                        _mm_store_ps(weights1, w14);
                        _mm_store_ps(weights2, w24);
                        _mm_store_ps(depths, _mm_add_ps(zab, _mm_mul_ps(_mm_set1_ps((float)c.z), w24)));
                    }
                    w0 = weights0[lane];
                    w1 = weights1[lane];
                    w2 = weights2[lane];
                    zf = depths[lane];
                }
                else
#endif
                {
                    const float px = (float)x + 0.5f;
                    w0 = k0 + s0 * px;
                    w1 = k1 + s1 * px;
                    w2 = 1.0f - w0 - w1;
                    zf = (float)a.z * w0 + (float)b.z * w1 + (float)c.z * w2;
                }
                const uint32_t zi = zf <= 0.0f ? 0u : (zf >= 4294967040.0f ? 0xFFFFFF00u : (uint32_t)zf);
                // xs..xe e miny..maxy ja estao no scissor. Testa Z antes de UV/STQ e RGBA.
                // A escrita de Z ainda depende de AFAIL e continua dentro de gs_pixel.
                uint8_t* depth_pixel = nullptr;
                if (direct_z16s)
                {
                    if (pc.ztst == 0u)
                        continue;
                    const uint32_t zindex = depth_row
                        ? (depth_base + ((ux >> 6) << 12) + depth_row[ux & 63u]) & 0x1FFFFFu
                        : ix_z16s(pc.zbp, pc.fbw, ux, row_y);
                    depth_pixel = vram8() + (size_t)zindex * 2;
                    if (pc.ztst >= 2u)
                    {
                        uint16_t previous;
                        std::memcpy(&previous, depth_pixel, sizeof(previous));
                        const uint16_t depth = (uint16_t)zi;
                        if (depth < previous || (pc.ztst == 3u && depth == previous))
                            continue;
                    }
                }
                else if (early_z && pc.zactive && !gs_zpass(pc, (uint32_t)x, (uint32_t)y, zi))
                    continue;

                float u = 0.0f, v = 0.0f;

                if (persp && (!early_z || pc.tme))
                {
                    float q = a.q * w0 + b.q * w1 + c.q * w2;
                    q = q != 0.0f ? 1.0f / q : 1.0f;
                    u = (a.us * w0 + b.us * w1 + c.us * w2) * q;
                    v = (a.vs * w0 + b.vs * w1 + c.vs * w2) * q;
                }
                else if (!early_z || pc.tme)
                {
                    u = a.u * w0 + b.u * w1 + c.u * w2;
                    v = a.v * w0 + b.v * w1 + c.v * w2;
                }

#if (defined(__SSE2__) || defined(_M_X64)) && !defined(__FMA__) && !defined(__FAST_MATH__)
                if (rgba_sse2)
                {
                    // Paraleliza os quatro canais de um pixel sem reordenar as somas.
                    // O Z ja foi testado; a escrita ainda depende do teste de alfa.
                    const __m128 ab = _mm_add_ps(_mm_mul_ps(rgba_a, _mm_set1_ps(w0)),
                        _mm_mul_ps(rgba_b, _mm_set1_ps(w1)));
                    const __m128 rgba = _mm_add_ps(ab, _mm_mul_ps(rgba_c, _mm_set1_ps(w2)));
                    int32_t channels[4];
                    const __m128 clipped = _mm_min_ps(_mm_max_ps(rgba, rgba_min), rgba_max);
                    _mm_storeu_si128(reinterpret_cast<__m128i*>(channels), _mm_cvttps_epi32(clipped));
                    const uint32_t addr = color_row
                        ? (color_base + ((ux >> 6) << 11) + color_row[ux & 63u]) & 0xFFFFFu
                        : ix_ct32(pc.fbp, pc.fbw, ux, row_y);
                    const uint32_t texel = sampler ? sampler(pc.ti, u + tex_u_bias, v + tex_u_bias) : 0u;
                    if (fast_effect || !pc.tme)
                        gs_shade_pixel<true, true>(pc, x, y, channels[0], channels[1], channels[2],
                            channels[3], texel, zi, addr, depth_pixel);
                    else
                        gs_shade_pixel<false, true>(pc, x, y, channels[0], channels[1], channels[2],
                            channels[3], texel, zi, addr);
                    continue;
                }
#endif

                // A interpolacao e o Z-test originais continuam inalterados.
                // Calcular a cobertura 2x2 SOMENTE quando a posicao esta perto
                // de uma das tres arestas, para economizar CPU.
                unsigned coverage_t = 4u;
                int outside_x_t = 0, outside_y_t = 0;
                if (edge_t && x >= 1 && x < 639 && y >= 1 && y < 223 &&
                    (w0 < 0.25f * (std::fabs(s0) + std::fabs(d0)) + 1e-5f ||
                     w1 < 0.25f * (std::fabs(s1) + std::fabs(d1)) + 1e-5f ||
                     w2 < 0.25f * (std::fabs(s2) + std::fabs(d0 + d1)) + 1e-5f))
                {
                    // Quatro posicoes no pixel, sem alterar a amostragem do GS.
                    // Centro das subcelulas: (+/-0.25, +/-0.25).
                    static const int sx[4] = {-1,  1, -1,  1};
                    static const int sy[4] = {-1, -1,  1,  1};
                    unsigned hits = 0;
                    int sumx = 0, sumy = 0;
                    for (int sample = 0; sample < 4; ++sample)
                    {
                        const float dx = (float)sx[sample] * 0.25f;
                        const float dy = (float)sy[sample] * 0.25f;
                        const float e0 = w0 + s0 * dx + d0 * dy;
                        const float e1 = w1 + s1 * dx + d1 * dy;
                        const float e2 = 1.0f - e0 - e1;
                        if (e0 >= 0.0f && e1 >= 0.0f && e2 >= 0.0f)
                        {
                            ++hits;
                            sumx += sx[sample]; sumy += sy[sample];
                        }
                    }
                    if (hits > 0u && hits < 4u)
                    {
                        // O lado externo e oposto ao centroide dos samples
                        // cobertos. Evita buscar pixels dentro do triangulo.
                        if (std::abs(sumx) >= std::abs(sumy) && sumx != 0)
                            outside_x_t = sumx > 0 ? -1 : 1;
                        else if (sumy != 0)
                            outside_y_t = sumy > 0 ? -1 : 1;
                        if (outside_x_t || outside_y_t)
                            coverage_t = hits;
                    }
                }
                uint32_t old_rgb_t = 0u;
                if (coverage_t < 4u)
                    old_rgb_t = gs_read_px(0x00u, pc.fbp, pc.fbw, (uint32_t)x, (uint32_t)y);

                // Comparacao UV do teste U: afeta apenas a busca de texels
                // dos triangulos 3D selecionados. Geometria e Z nao mudam.
                // Bias zero (padrao) preserva EXATAMENTE a versao T.
                const float r = a.r * w0 + b.r * w1 + c.r * w2;
                const float g = a.g * w0 + b.g * w1 + c.g * w2;
                const float blue = a.b * w0 + b.b * w1 + c.b * w2;
                const float alpha = a.a * w0 + b.a * w1 + c.a * w2;
                if (sampler != nullptr)
                {
                    // CT32 usa o mesmo endereco para o blend e a escrita.
                    const uint32_t addr = color_row
                        ? (color_base + ((ux >> 6) << 11) + color_row[ux & 63u]) & 0xFFFFFu
                        : ix_ct32(pc.fbp, pc.fbw, ux, row_y);
                    const uint32_t texel = sampler(pc.ti, u + tex_u_bias, v + tex_u_bias);
                    if (fast_effect)
                        gs_shade_pixel<true, true>(pc, x, y, gs_f2i255(r), gs_f2i255(g), gs_f2i255(blue),
                            gs_f2i255(alpha), texel, zi, addr, depth_pixel);
                    else
                        gs_shade_pixel<false, true>(pc, x, y, gs_f2i255(r), gs_f2i255(g), gs_f2i255(blue),
                            gs_f2i255(alpha), texel, zi, addr);
                }
                else if (early_z)
                    gs_pixel<true>(pc, x, y, r, g, blue, alpha, u + tex_u_bias, v + tex_u_bias, zi);
                else
                    gs_pixel(pc, x, y, r, g, blue, alpha, u + tex_u_bias, v + tex_u_bias, zi);

                if (coverage_t < 4u)
                {
                    const uint32_t after = gs_read_px(0x00u, pc.fbp, pc.fbw, (uint32_t)x, (uint32_t)y);
                    // Um Z-test rejeitado ou pixel transparente nao deve
                    // contaminar a mascara de outro triangulo.
                    if ((old_rgb_t & 0x00FFFFFFu) != (after & 0x00FFFFFFu))
                    {
                        GsEdgeT& edge = (*edge_t_frame)[(size_t)y * 640u + (size_t)x];
                        edge.rgba = after;
                        edge.vsync = g_gs_vsyncs;
                        edge.coverage = (uint8_t)coverage_t;
                        edge.outside_x = (int8_t)outside_x_t;
                        edge.outside_y = (int8_t)outside_y_t;
                        g_edge_t_border_pixels.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            }
        }
    }

    // Campos do interlacado: o jogo desenha os quadros alternados com um deslocamento de meia linha (o campo
    // impar sobe 0,5 linha do buffer). No PS2 o campo impar sai uma linha de saida mais abaixo, e os dois se
    // alinham; mostrar os quadros sem isso faz a imagem tremer. Aqui, cada vertice de sprite vota se a coordenada
    // Y esta numa meia linha (2*Y impar); o voto decai, entao vale o do quadro desenhado por ultimo em cada FBP.
    float g_half_vote[512] = {};

    // Evidencia por FBP. Usa os mesmos vertices e o mesmo reset do voto legado.
    struct FieldSpriteEvidence
    {
        unsigned sprites = 0, half_y = 0, half_ofy = 0, ofy_last = 0;
    };
    FieldSpriteEvidence g_field_evidence[512];
    std::atomic<unsigned> g_gs_flips{0};   // trocas do buffer exibido (DISPFB): aproximam os quadros do jogo

    struct FieldUnknownOffset
    {
        unsigned ofy = 0, fbw = 0, psm = 0, context = 0;
        unsigned vertices = 0;
        bool target = false;
    };

    struct FieldRasterState
    {
        unsigned shifted = 0, base = 0, unknown = 0;
        unsigned shifted_sprite = 0, shifted_triangle = 0;
        bool shifted_train = false;
        FieldUnknownOffset offsets[4];
        unsigned other_unknown = 0, zero_origin = 0;
    };

    static void gs_record_unknown_ofy(FieldRasterState& state, const GsCtx& c,
                                      unsigned ofy, unsigned context, bool target)
    {
        // Amostra limitada por framebuffer. Registra o estado junto com o
        // OFY para diferenciar origem nao reconhecida de layout incompativel.
        const unsigned fbw = (unsigned)((c.frame >> 16) & 0x3Fu);
        const unsigned psm = (unsigned)((c.frame >> 24) & 0x3Fu);
        for (auto& sample : state.offsets)
        {
            if (sample.vertices == 0)
            {
                sample = {ofy, fbw, psm, context, 1u, target};
                return;
            }
            if (sample.ofy == ofy && sample.fbw == fbw && sample.psm == psm &&
                sample.context == context && sample.target == target)
            {
                ++sample.vertices;
                return;
            }
        }
        ++state.other_unknown;
    };
    FieldRasterState g_field_raster_state[512];

    // Reconhece apenas o framebuffer entrelacado 640 pixels em CT32 do jogo.
    // A normalizacao afeta cor e Z na rasterizacao, inclusive leituras posteriores.
    static bool gs_field_raster_target(const GsCtx& c)
    {
        const unsigned fbp = (unsigned)(c.frame & 0x1FFu);
        return g_gs_crt_int && g_gs_crt_ffmd &&
            (fbp == 0u || fbp == 70u) &&
            ((c.frame >> 16) & 0x3Fu) == 10u &&
            ((c.frame >> 24) & 0x3Fu) == 0u;
    }

    static bool gs_field_raster_train_ofy(unsigned ofy)
    {
        // O historico do trem mostra pares como 76B0/76B8 e 7580/7588.
        // Preserva a parte inteira da origem; somente as duas fases
        // observadas, separadas por oito unidades em ponto fixo, entram.
        const unsigned fraction = ofy & 0xFu;
        return g_field_raster_train && ofy >= 0x7500u && ofy <= 0x7CF8u &&
            (fraction == 0u || fraction == 8u);
    }

    void gs_vertex(uint64_t data, bool kick, bool xyzf = false)
    {
        int ci = (int)((g_st.prim >> 9) & 1);
        const GsCtx& c = g_st.ctx[ci];
        GsVertex v;
        float ofx = (float)(c.xyoffset & 0xFFFF), ofy = (float)((c.xyoffset >> 32) & 0xFFFF);
        v.x = ((float)(data & 0xFFFF) - ofx) / 16.0f;
        v.y = ((float)((data >> 16) & 0xFFFF) - ofy) / 16.0f;
        // Mantem o voto de campo baseado na coordenada original.
        const float original_y = v.y;
        if (g_field_raster)
        {
            FieldRasterState& state = g_field_raster_state[c.frame & 0x1FFu];
            const unsigned raw_ofy = (unsigned)((c.xyoffset >> 32) & 0xFFFFu);
            const bool target = gs_field_raster_target(c);
            const bool train_ofy = target && gs_field_raster_train_ofy(raw_ofy);
            if (target && (raw_ofy == 0x7908u || (train_ofy && (raw_ofy & 0xFu) == 8u)))
            {
                v.y += 0.5f;
                ++state.shifted;
                state.shifted_train |= train_ofy && raw_ofy != 0x7908u;
                const unsigned type = (unsigned)(g_st.prim & 7u);
                state.shifted_sprite += type == 6u;
                state.shifted_triangle += type >= 3u && type <= 5u;
            }
            else if (target && (raw_ofy == 0x7900u || train_ofy))
                ++state.base;
            else
            {
                ++state.unknown;
                // Conta a origem zero apenas no mesmo layout dos desenhos
                // do trem. As coordenadas destes vertices ficam intactas.
                if (g_field_raster_train && target && raw_ofy == 0u)
                    ++state.zero_origin;
                if (g_field_train_diag)
                    gs_record_unknown_ofy(state, c, raw_ofy, (unsigned)ci, target);
            }
        }
        v.z = xyzf ? (uint32_t)((data >> 32) & 0xFFFFFFu) : (uint32_t)(data >> 32);

        v.r = (float)(g_st.rgbaq & 0xFF);
        v.g = (float)((g_st.rgbaq >> 8) & 0xFF);
        v.b = (float)((g_st.rgbaq >> 16) & 0xFF);
        v.a = (float)((g_st.rgbaq >> 24) & 0xFF);

        int tw = 1 << (int)((c.tex0 >> 26) & 0xF), th = 1 << (int)((c.tex0 >> 30) & 0xF);

        if (g_st.prim & 0x100)   // FST: UV em ponto fixo 12.4
        {
            v.u = (float)(g_st.uv & 0x3FFF) / 16.0f;
            v.v = (float)((g_st.uv >> 16) & 0x3FFF) / 16.0f;
        }
        else                     // ST / Q
        {
            float s, t, q;
            uint32_t sb = (uint32_t)g_st.st, tb = (uint32_t)(g_st.st >> 32), qb = (uint32_t)(g_st.rgbaq >> 32);
            std::memcpy(&s, &sb, 4); std::memcpy(&t, &tb, 4); std::memcpy(&q, &qb, 4);

            if (q == 0.0f)
                q = 1.0f;

            v.u = s / q * (float)tw;
            v.v = t / q * (float)th;
            v.us = s * (float)tw;
            v.vs = t * (float)th;
            v.q = q;
            v.persp = true;
        }

        unsigned type = (unsigned)(g_st.prim & 7);
        int need = type == 6 ? 2 : (type >= 3 ? 3 : (type == 0 ? 1 : 2));

        // fila de vertices (tira/leque: desloca)
        if (g_st.nq >= need)
        {
            if (type == 4) { g_st.q[0] = g_st.q[1]; g_st.q[1] = g_st.q[2]; g_st.nq = 2; }
            else if (type == 5) { g_st.q[1] = g_st.q[2]; g_st.nq = 2; }
            else if (type == 2) { g_st.q[0] = g_st.q[1]; g_st.nq = 1; }
            else g_st.nq = 0;
        }

        g_st.q[g_st.nq++] = v;

        if (type == 6 && g_st.nq == 1)
        {
            // primeiro vertice do sprite: Y em meia linha (2*Y impar) = quadro "impar"; OFY com fracao tambem conta
            const int n = (int)std::floor(original_y * 2.0f + 0.5f);
            float& vote = g_half_vote[c.frame & 0x1FF];
            vote += (n & 1) ? 1.0f : -1.0f;

            const unsigned ofy = (unsigned)((c.xyoffset >> 32) & 0xFFFFu);
            const bool half_ofy = (ofy & 0xFu) >= 8u;
            if (half_ofy)
                vote += 4.0f;

            if (g_field_audit)
            {
                FieldSpriteEvidence& evidence = g_field_evidence[c.frame & 0x1FF];
                ++evidence.sprites;
                evidence.half_y += (unsigned)(n & 1);
                evidence.half_ofy += half_ofy;
                evidence.ofy_last = ofy;
            }

            vote = vote > 64.0f ? 64.0f : (vote < -64.0f ? -64.0f : vote);
        }

        if (kick && gs_trace_enabled())
        {
            char* dst = (g_trace_vtx == 0) ? g_trace_v0 : g_trace_vn;
            std::snprintf(dst, 200, "(%.1f,%.1f) rgba=%.0f,%.0f,%.0f,%.0f uv=%.1f,%.1f", v.x, v.y, v.r, v.g, v.b, v.a, v.u, v.v);
            if (g_trace_vtx == 0)
                g_trace_all[0] = 0;

            if (g_trace_vtx < 8)
            {
                size_t n = std::strlen(g_trace_all);
                std::snprintf(g_trace_all + n, sizeof(g_trace_all) - n, " [raw=%04X,%04X (%.1f,%.1f) uv=%.0f,%.0f a=%.0f]",
                    (unsigned)(data & 0xFFFF), (unsigned)((data >> 16) & 0xFFFF), v.x, v.y, v.u, v.v, v.a);
            }

            g_trace_vtx++;
        }

        if (!kick || g_st.nq < need)
            return;

        if (type == 6)
            gs_draw_sprite(ci, g_st.q[0], g_st.q[1]);
        else if (type >= 3)
            gs_draw_triangle(ci, g_st.q[0], g_st.q[1], g_st.q[2]);
        else if (type == 0)
            gs_pixel(ci, (int)v.x, (int)v.y, v.r, v.g, v.b, v.a, v.u, v.v, v.z);
        else
            g_st.unsupported++;   // linhas: ainda nao

        if (type == 6 || type == 3 || type == 1 || type == 0)
            g_st.nq = 0;
    }

    void gs_xfer_begin()
    {
        gs_vk_flush();
        gs_init_tables();
        g_st.dbp = (uint32_t)((g_st.bitbltbuf >> 32) & 0x3FFF);
        g_st.dbw = (uint32_t)((g_st.bitbltbuf >> 48) & 0x3F);
        g_st.dpsm = (uint32_t)((g_st.bitbltbuf >> 56) & 0x3F);
        g_st.x0 = g_st.x = (uint32_t)((g_st.trxpos >> 32) & 0x7FF);
        g_st.y = (uint32_t)((g_st.trxpos >> 48) & 0x7FF);
        g_st.rrw = (uint32_t)(g_st.trxreg & 0xFFF);
        g_st.rrh = (uint32_t)((g_st.trxreg >> 32) & 0xFFF);
        g_st.npend = 0;
        uint32_t dir = (uint32_t)(g_st.trxdir & 3);

        if (dir == 0)
        {
            g_st.xfer = g_st.rrw != 0 && g_st.rrh != 0;
            g_st.y -= (g_st.xfer ? 0u : 0u);
            g_st.rrh += g_st.y;   // passa a ser o y final (exclusivo)
        }
        else if (dir == 2)   // local -> local
        {
            uint32_t sbp = (uint32_t)(g_st.bitbltbuf & 0x3FFF), sbw = (uint32_t)((g_st.bitbltbuf >> 16) & 0x3F);
            uint32_t spsm = (uint32_t)((g_st.bitbltbuf >> 24) & 0x3F);
            uint32_t sx = (uint32_t)(g_st.trxpos & 0x7FF), sy = (uint32_t)((g_st.trxpos >> 16) & 0x7FF);
            uint32_t dx = g_st.x0, dy = (uint32_t)((g_st.trxpos >> 48) & 0x7FF);

            for (uint32_t yy = 0; yy < ((g_st.trxreg >> 32) & 0xFFF); yy++)
                for (uint32_t xx = 0; xx < g_st.rrw; xx++)
                    gs_write_px(g_st.dpsm, g_st.dbp, g_st.dbw, dx + xx, dy + yy, gs_read_px(spsm, sbp, sbw, sx + xx, sy + yy));
        }
    }

    void gs_xfer_pixel(uint32_t v)
    {
        gs_write_px(g_st.dpsm, g_st.dbp, g_st.dbw, g_st.x, g_st.y, v);

        if (++g_st.x >= g_st.x0 + g_st.rrw)
        {
            g_st.x = g_st.x0;

            if (++g_st.y >= g_st.rrh)
                g_st.xfer = false;
        }
    }

    // Formatos de Z se acumulam no upload como os de cor de mesmo tamanho (32/24/16 bits)
    static inline uint32_t gs_color_fmt(uint32_t psm)
    {
        return psm == 0x30 ? 0x00u : (psm == 0x31 ? 0x01u : ((psm == 0x32 || psm == 0x3A) ? 0x02u : psm));
    }

    void gs_core_image(uint64_t lo, uint64_t hi)
    {
        gs_vk_flush();
        if (!g_st.xfer)
            return;

        uint8_t bytes[16];
        std::memcpy(bytes, &lo, 8);
        std::memcpy(bytes + 8, &hi, 8);

        // Caminho rapido: quadword inteiro de pixels completos (32, 16, 8 e 4 bits), sem passar pelo buffer de bytes.
        if (g_st.npend == 0)
        {
            const uint32_t fpsm = gs_color_fmt(g_st.dpsm);

            if (fpsm == 0x00)
            {
                uint32_t w[4];
                std::memcpy(w, bytes, 16);

                for (int k = 0; k < 4 && g_st.xfer; k++)
                    gs_xfer_pixel(w[k]);

                return;
            }

            if (fpsm == 0x02 || fpsm == 0x0A)
            {
                uint16_t h[8];
                std::memcpy(h, bytes, 16);

                for (int k = 0; k < 8 && g_st.xfer; k++)
                    gs_xfer_pixel(h[k]);

                return;
            }

            if (fpsm == 0x13 || fpsm == 0x1B)
            {
                for (int k = 0; k < 16 && g_st.xfer; k++)
                    gs_xfer_pixel(bytes[k]);

                return;
            }

            if (fpsm == 0x14 || fpsm == 0x24 || fpsm == 0x2C)
            {
                for (int k = 0; k < 16 && g_st.xfer; k++)
                {
                    gs_xfer_pixel(bytes[k] & 0xF);

                    if (g_st.xfer)
                        gs_xfer_pixel(bytes[k] >> 4);
                }

                return;
            }
        }

        for (int i = 0; i < 16 && g_st.xfer; i++)
        {
            g_st.pend[g_st.npend++] = bytes[i];
            uint32_t psm = gs_color_fmt(g_st.dpsm);

            if (psm == 0x00 && g_st.npend == 4)
            {
                uint32_t v; std::memcpy(&v, g_st.pend, 4); g_st.npend = 0; gs_xfer_pixel(v);
            }
            else if (psm == 0x01 && g_st.npend == 3)
            {
                uint32_t v = g_st.pend[0] | (g_st.pend[1] << 8) | (g_st.pend[2] << 16); g_st.npend = 0; gs_xfer_pixel(v);
            }
            else if ((psm == 0x02 || psm == 0x0A) && g_st.npend == 2)
            {
                uint32_t v = g_st.pend[0] | (g_st.pend[1] << 8); g_st.npend = 0; gs_xfer_pixel(v);
            }
            else if (psm == 0x13 || psm == 0x1B)
            {
                g_st.npend = 0; gs_xfer_pixel(bytes[i]);
            }
            else if (psm == 0x14 || psm == 0x24 || psm == 0x2C)
            {
                g_st.npend = 0;
                gs_xfer_pixel(bytes[i] & 0xF);

                if (g_st.xfer)
                    gs_xfer_pixel(bytes[i] >> 4);
            }
            else if (psm != 0x00 && psm != 0x01 && psm != 0x02 && psm != 0x0A)
            {
                g_st.npend = 0;
                g_st.unsupported++;
            }
        }
    }

    void gs_core_write(unsigned reg, uint64_t data)
    {
        gs_init_tables();

        switch (reg)
        {
            case 0x00: g_st.prim = data & 0x7FF; g_st.nq = 0; break;
            case 0x01: g_st.rgbaq = data; break;
            case 0x02: g_st.st = data; break;
            case 0x03: g_st.uv = data; break;
            case 0x04: gs_vertex(data, true, true); break;
            case 0x05: gs_vertex(data, true, false); break;
            case 0x0C: gs_vertex(data, false, true); break;
            case 0x0D: gs_vertex(data, false, false); break;
            case 0x06: case 0x07:
            {
                GsCtx& c = g_st.ctx[reg - 6];
                c.tex0 = data;
                uint32_t cld = (uint32_t)(data >> 61);

                if (cld != 0)
                    gs_load_clut(data);

                break;
            }
            case 0x08: case 0x09: g_st.ctx[reg - 8].clamp = data; break;
            case 0x14: case 0x15: g_st.ctx[reg - 0x14].tex1 = data; break;
            case 0x18: case 0x19: g_st.ctx[reg - 0x18].xyoffset = data; break;
            case 0x3B: g_st.texa = data; break;
            case 0x40: case 0x41: g_st.ctx[reg - 0x40].scissor = data; break;
            case 0x42: case 0x43: g_st.ctx[reg - 0x42].alpha = data; break;
            case 0x46: g_st.colclamp = data; break;
            case 0x47: case 0x48: g_st.ctx[reg - 0x47].test = data; break;
            case 0x4C: case 0x4D: g_st.ctx[reg - 0x4C].frame = data; break;
            case 0x4E: case 0x4F: g_st.ctx[reg - 0x4E].zbuf = data; break;
            case 0x50: g_st.bitbltbuf = data; break;
            case 0x51: g_st.trxpos = data; break;
            case 0x52: g_st.trxreg = data; break;
            case 0x53:
                g_st.trxdir = data;
                ++g_st.trx_generation; // Invalida um download anterior, inclusive se TRXDIR deixar de ser 1.
                gs_xfer_begin();
                break;
            default: break;
        }
    }

    // Parametros do quadro exibido: PMODE.EN1/EN2 escolhem o circuito; DISPFBn / DISPLAYn dizem onde e quanto.
    struct DispParams
    {
        uint64_t pmode = 0, dispfb = 0, display = 0;
        uint32_t fbp = 0, fbw = 0, psm = 0, dbx = 0, dby = 0, w = 0, h = 0, magv = 1;
    };

    bool gs_display_params(DispParams& d)
    {
        uint64_t pmode = g_gs_priv[0x00 / 8];
        uint64_t dispfb = 0, display = 0;
        uint32_t fbp = 0, fbw = 0, psm = 0, dbx = 0, dby = 0, w = 0, h = 0, magv = 1;
        bool valid = false;

        // tenta primeiro o circuito habilitado em PMODE (EN1 = bit 0, EN2 = bit 1); se os parametros
        // nao fizerem sentido, tenta o outro
        for (int pass = 0; pass < 2 && !valid; pass++)
        {
            bool c1 = pass == 0 ? (pmode & 1) != 0 : (pmode & 1) == 0;
            dispfb = g_gs_priv[(c1 ? 0x70 : 0x90) / 8];
            display = g_gs_priv[(c1 ? 0x80 : 0xA0) / 8];

            fbp = (uint32_t)(dispfb & 0x1FF) * 32;
            fbw = (uint32_t)((dispfb >> 9) & 0x3F);
            psm = (uint32_t)((dispfb >> 15) & 0x1F);
            dbx = (uint32_t)((dispfb >> 32) & 0x7FF);
            dby = (uint32_t)((dispfb >> 43) & 0x7FF);
            uint32_t magh = (uint32_t)((display >> 23) & 0xF) + 1;
            w = ((uint32_t)((display >> 32) & 0xFFF) + 1) / magh;
            // DISPLAY.DH e a altura de SAIDA (em linhas de exibicao), nao a do buffer. Cada linha do
            // buffer se repete (MAGV + 1) vezes. ATENCAO: FFMD=1 e modo FRAME,
            // nao FIELD (confirmado por PS2SDK e PCSX2). O fator adicional 2 abaixo e
            // uma heuristica legada que mantemos para preservar a batalha. O jogo usa
            // buffers de ~640x224 em FBP 0/70: ler 448 linhas invadiria o vizinho.
            magv = ((uint32_t)((display >> 27) & 0x3) + 1) * ((g_gs_crt_int && g_gs_crt_ffmd) ? 2u : 1u);
            h = (uint32_t)((display >> 44) & 0x7FF) + 1;
            valid = w != 0 && w <= 1024 && h != 0 && h <= 1024 && fbw != 0;
        }

        d.pmode = pmode; d.dispfb = dispfb; d.display = display;
        d.fbp = fbp; d.fbw = fbw; d.psm = psm; d.dbx = dbx; d.dby = dby; d.w = w; d.h = h; d.magv = magv;
        return valid;
    }

    // Janela: o ultimo quadro convertido (RGB em 0x00RRGGBB), entregue a thread da janela.
    std::mutex g_win_mu;
    std::vector<uint32_t> g_win_pix;
    uint32_t g_win_w = 0, g_win_h = 0;
    std::atomic<unsigned> g_win_seq{0};

    // Compara o buffer enviado ao GDI com o ultimo desenho bem-sucedido.
    // A janela chama este metodo apos o desenho, inclusive em redesenhos.
    struct WindowFrameContent
    {
        uint32_t width = 0, height = 0;
        std::vector<uint32_t> previous;

        bool record(const std::vector<uint32_t>& pixels, uint32_t w, uint32_t h)
        {
            if (width == w && height == h && previous == pixels)
                return false;
            previous = pixels;
            width = w;
            height = h;
            return true;
        }
    };

    // F11 pede uma captura da mesma troca, em tres etapas da apresentacao.
    // O GS copia os pixels; a thread da janela escreve os arquivos.
    struct FontCapture
    {
        unsigned flip = 0, fbp = 0, psm = 0, magv = 0;
        uint32_t w = 0, source_h = 0, h = 0, final_w = 0, final_h = 0;
        bool spatial = false, native_ui = false;
        std::vector<uint32_t> source, reconstructed, final;
    };
    std::atomic<bool> g_font_capture_request{false};
    FontCapture g_font_capture; // Protegido por g_win_mu.
#ifdef _WIN32
    std::atomic<unsigned> g_win_blits{0}, g_win_coalesced{0}, g_win_blit_errors{0};
    // Empacota os contadores para o perfil obter o mesmo instante dos dois.
    // Metade inferior: diferentes. Metade superior: iguais ao anterior.
    std::atomic<uint64_t> g_win_content_counts{0};
#endif
    struct PresentProfile
    {
        unsigned pacing = 0, invalid = 0, fieldlock = 0, same_flip = 0, aligned = 0, early_flip = 0;
        unsigned queue_skips = 0, unchanged = 0;
        uint64_t queue_wait_ns = 0;
        unsigned raster_fields = 0, raster_mixed = 0, raster_unknown = 0, raster_train = 0, raster_train_zero = 0;
        uint64_t raster_sprite_vertices = 0, raster_triangle_vertices = 0;
    };
    PresentProfile g_present_profile;

    // Ultimo campo barrado no intervalo. Copiado com a fila do GS
    // sincronizada para que os numeros correspondam ao voto apresentado.
    struct FieldTrainDiagnostic
    {
        unsigned flip = 0, fbp = 0, w = 0, h = 0, magv = 0, fbw = 0, psm = 0;
        FieldRasterState raster;
    };
    FieldTrainDiagnostic g_field_train_sample;

    struct FieldFlipAudit
    {
        unsigned flip = 0, circuit = 0, csr_field = 0, fbp = 0;
        unsigned dy = 0, dby = 0;
        int vote = 0, present_vote = 0;
        unsigned checked = 0, blocked = 0, published = 0;
        FieldSpriteEvidence sprites;
    };
    constexpr unsigned kFieldAuditCapacity = 128;
    FieldFlipAudit g_field_flips[kFieldAuditCapacity];

    // Le uma linha da VRAM e converte a cor para o formato da janela.
    // Em CT32/CT24 o swizzle e separavel: reutiliza X em todas as linhas
    // e calcula Y uma vez por linha. As outras conversoes continuam genericas.
    class GsScanoutRow
    {
        uint32_t columns[1024];
        uint32_t cached_x = 0, cached_w = 0;

        static uint32_t rgb24(uint32_t v)
        {
            return ((v & 0xFFu) << 16) | (v & 0xFF00u) | ((v >> 16) & 0xFFu);
        }

    public:
        void read(uint32_t* out, const DispParams& d, uint32_t y, bool fast)
        {
            if (fast && (d.psm == 0x00u || d.psm == 0x01u))
            {
                if (cached_x != d.dbx || cached_w != d.w)
                {
                    for (uint32_t x = 0; x < d.w; ++x)
                        columns[x] = ix_ct32(0, 0, d.dbx + x, 0);
                    cached_x = d.dbx;
                    cached_w = d.w;
                }

                const uint32_t row = ix_ct32(d.fbp, d.fbw, 0, d.dby + y);
                for (uint32_t x = 0; x < d.w; ++x)
                    out[x] = rgb24(g_vram[(row + columns[x]) & 0xFFFFFu]);
                return;
            }

            const bool c16 = d.psm == 0x02u || d.psm == 0x0Au;
            for (uint32_t x = 0; x < d.w; ++x)
            {
                const uint32_t v = gs_read_px(d.psm, d.fbp, d.fbw, d.dbx + x, d.dby + y);
                out[x] = c16
                    ? (((v & 31u) << 19) | (((v >> 5) & 31u) << 11) | (((v >> 10) & 31u) << 3))
                    : rgb24(v);
            }
        }
    };

    // Converte o quadro exibido para a janela (~30 Hz; espaca mais se a leitura da VRAM estiver cara).
    // Le so uma linha por grupo de repeticao (MAGV / campo) e replica as demais.
    // Teste K: apenas no modo espacial, esperamos a fila do GS antes de fazer um snapshot.
    // Declaracao antecipada: implementacao original permanece mais abaixo no runtime.
    void gs_q_drain();
    bool gs_q_idle();
    // Teste S: antisserrilhamento espacial OPCIONAL na imagem de apresentacao.
    // Nao altera VRAM, Z, texturas ou estados do GS. Usa o mesmo quadro, nunca
    // combina imagens de instantes diferentes. Inspirado na ideia de FXAA:
    // detecta contraste local e amostra na direcao tangencial a borda.
    // Esta filtragem nao recupera poligonos ou texels que nao foram rasterizados.
    struct GsAaRgbS
    {
        float r, g, b;
    };

    static inline float gs_aa_luma_s(uint32_t c)
    {
        return (float)((((c >> 16) & 255u) * 77u + ((c >> 8) & 255u) * 150u
            + (c & 255u) * 29u) >> 8);
    }

    static inline GsAaRgbS gs_aa_bilinear_s(const std::vector<uint32_t>& src,
                                              int w, int h, float fx, float fy)
    {
        fx = std::max(0.0f, std::min(fx, (float)(w - 1)));
        fy = std::max(0.0f, std::min(fy, (float)(h - 1)));
        const int x0 = (int)fx, y0 = (int)fy;
        const int x1 = std::min(x0 + 1, w - 1);
        const int y1 = std::min(y0 + 1, h - 1);
        const float tx = fx - (float)x0, ty = fy - (float)y0;
        const float w00 = (1.0f - tx) * (1.0f - ty);
        const float w10 = tx * (1.0f - ty);
        const float w01 = (1.0f - tx) * ty;
        const float w11 = tx * ty;
        const uint32_t c00 = src[(size_t)y0 * (size_t)w + (size_t)x0];
        const uint32_t c10 = src[(size_t)y0 * (size_t)w + (size_t)x1];
        const uint32_t c01 = src[(size_t)y1 * (size_t)w + (size_t)x0];
        const uint32_t c11 = src[(size_t)y1 * (size_t)w + (size_t)x1];
        const float r = (float)((c00 >> 16) & 255u) * w00 + (float)((c10 >> 16) & 255u) * w10
            + (float)((c01 >> 16) & 255u) * w01 + (float)((c11 >> 16) & 255u) * w11;
        const float g = (float)((c00 >> 8) & 255u) * w00 + (float)((c10 >> 8) & 255u) * w10
            + (float)((c01 >> 8) & 255u) * w01 + (float)((c11 >> 8) & 255u) * w11;
        const float b = (float)(c00 & 255u) * w00 + (float)(c10 & 255u) * w10
            + (float)(c01 & 255u) * w01 + (float)(c11 & 255u) * w11;
        return {r, g, b};
    }

    // Se glifos != nullptr, limita a analise a cada retangulo do atlas P/R.
    // Os dois modos sao independentes: o usuario pode testar cena e fonte
    // separadamente, sem modificar o GS original nem a logica do jogo.
    static uint32_t gs_aa_apply_s(std::vector<uint32_t>& img, int w, int h,
                                   float threshold, float max_span, float strength,
                                   const std::vector<UiGlyphP>* glifos)
    {
        if (w < 3 || h < 3 || img.size() != (size_t)w * (size_t)h)
            return 0;

        // Snapshot imutavel: nenhuma amostragem usa pixels ja suavizados.
        const std::vector<uint32_t> orig = img;
        uint32_t changed = 0;
        auto process = [&](int xl, int xr, int yt, int yb)
        {
            xl = std::max(1, xl); xr = std::min(w - 2, xr);
            yt = std::max(1, yt); yb = std::min(h - 2, yb);
            if (xl > xr || yt > yb) return;
            for (int y = yt; y <= yb; ++y)
            for (int x = xl; x <= xr; ++x)
            {
                const size_t i = (size_t)y * (size_t)w + (size_t)x;
                const float lM  = gs_aa_luma_s(orig[i]);
                const float lN  = gs_aa_luma_s(orig[i - (size_t)w]);
                const float lS  = gs_aa_luma_s(orig[i + (size_t)w]);
                const float lW  = gs_aa_luma_s(orig[i - 1u]);
                const float lE  = gs_aa_luma_s(orig[i + 1u]);
                const float lNW = gs_aa_luma_s(orig[i - (size_t)w - 1u]);
                const float lNE = gs_aa_luma_s(orig[i - (size_t)w + 1u]);
                const float lSW = gs_aa_luma_s(orig[i + (size_t)w - 1u]);
                const float lSE = gs_aa_luma_s(orig[i + (size_t)w + 1u]);
                const float lmin = std::min(lM, std::min(std::min(lN, lS), std::min(lW, lE)));
                const float lmax = std::max(lM, std::max(std::max(lN, lS), std::max(lW, lE)));
                if (lmax - lmin < std::max(threshold, lmax * 0.14f))
                    continue; // nao altera areas lisas e contrastes fracos

                // Direcao da borda a partir das diferencas diagonais.
                float dx = -((lNW + lNE) - (lSW + lSE));
                float dy = ((lNW + lSW) - (lNE + lSE));
                const float reduce = std::max((lNW + lNE + lSW + lSE) * (1.0f / 32.0f), 2.0f);
                const float inv = 1.0f / (std::min(std::fabs(dx), std::fabs(dy)) + reduce);
                dx = std::max(-max_span, std::min(max_span, dx * inv));
                dy = std::max(-max_span, std::min(max_span, dy * inv));
                if (std::fabs(dx) + std::fabs(dy) < 0.125f)
                    continue;

                // Primeiro, duas amostras internas sobre a tangente da borda.
                const GsAaRgbS a0 = gs_aa_bilinear_s(orig, w, h,
                    (float)x - dx * (1.0f / 6.0f), (float)y - dy * (1.0f / 6.0f));
                const GsAaRgbS a1 = gs_aa_bilinear_s(orig, w, h,
                    (float)x + dx * (1.0f / 6.0f), (float)y + dy * (1.0f / 6.0f));
                GsAaRgbS a = {(a0.r + a1.r) * 0.5f,
                              (a0.g + a1.g) * 0.5f, (a0.b + a1.b) * 0.5f};
                // Duas amostras externas reduzem os degraus em diagonais.
                const GsAaRgbS b0 = gs_aa_bilinear_s(orig, w, h,
                    (float)x - dx * 0.5f, (float)y - dy * 0.5f);
                const GsAaRgbS b1 = gs_aa_bilinear_s(orig, w, h,
                    (float)x + dx * 0.5f, (float)y + dy * 0.5f);
                GsAaRgbS b = {a.r * 0.5f + (b0.r + b1.r) * 0.25f,
                              a.g * 0.5f + (b0.g + b1.g) * 0.25f,
                              a.b * 0.5f + (b0.b + b1.b) * 0.25f};
                const float lb = (77.0f * b.r + 150.0f * b.g + 29.0f * b.b) / 256.0f;
                // Limita halos: rejeita amostra externa fora do intervalo local.
                if (lb < lmin || lb > lmax) b = a;
                // Controle de intensidade: preserva a cor central dos
                // glifos finos para nao apagar a fonte (regressao do O).
                const uint32_t original = orig[i];
                b.r = (float)((original >> 16) & 255u) * (1.0f - strength) + b.r * strength;
                b.g = (float)((original >> 8) & 255u) * (1.0f - strength) + b.g * strength;
                b.b = (float)(original & 255u) * (1.0f - strength) + b.b * strength;
                const uint32_t r = (uint32_t)std::min(255.0f, std::max(0.0f, b.r + 0.5f));
                const uint32_t g = (uint32_t)std::min(255.0f, std::max(0.0f, b.g + 0.5f));
                const uint32_t bl = (uint32_t)std::min(255.0f, std::max(0.0f, b.b + 0.5f));
                const uint32_t color = (r << 16) | (g << 8) | bl;
                if (img[i] != color) {img[i] = color; ++changed;}
            }
        };
        if (glifos != nullptr)
        {
            // Nao afeta outros menus e nao interpreta um retangulo como texto
            // se ele nao foi identificado no atlas especifico da batalha.
            for (const UiGlyphP& glyph : *glifos)
                process(glyph.x, glyph.x + glyph.w - 1,
                        glyph.y, glyph.y + glyph.h - 1);
        }
        else
            process(1, w - 2, 1, h - 2);
        return changed;
    }

    // Uma linha na imagem de saida corresponde a meia linha no framebuffer 640x224.
    // Replica a linha da borda para manter o tamanho e evitar uma faixa preta.
    static void gs_align_spatial_field(uint32_t* pixels, size_t width, size_t height, int shift)
    {
        const size_t row_bytes = width * sizeof(uint32_t);
        const size_t move_bytes = (height - 1) * row_bytes;
        if (shift > 0)
        {
            std::memmove(pixels + width, pixels, move_bytes);
        }
        else
        {
            std::memmove(pixels, pixels + width, move_bytes);
            std::memcpy(pixels + (height - 1) * width, pixels + (height - 2) * width, row_bytes);
        }
    }

#if defined(__SSE2__) || defined(_M_X64)
    static unsigned gs_spatial_row_sse2(uint32_t* out, const uint32_t* a, const uint32_t* b,
                                        uint32_t width, uint32_t& x)
    {
        const __m128i low = _mm_set1_epi32(255);
        const __m128i one = _mm_set1_epi8(1);
        const __m128i threshold = _mm_set1_epi32(30);
        const __m128i margin = _mm_set1_epi32(12);
        const auto delta = [&](const __m128i lhs, const __m128i rhs) {
            const __m128i d = _mm_or_si128(_mm_subs_epu8(lhs, rhs), _mm_subs_epu8(rhs, lhs));
            return _mm_add_epi32(_mm_and_si128(d, low),
                _mm_add_epi32(_mm_and_si128(_mm_srli_epi32(d, 8), low),
                              _mm_and_si128(_mm_srli_epi32(d, 16), low)));
        };
        const auto load = [](const uint32_t* p) {
            return _mm_loadu_si128(reinterpret_cast<const __m128i*>(p));
        };
        const auto select = [](const __m128i mask, const __m128i yes, const __m128i no) {
            return _mm_or_si128(_mm_and_si128(mask, yes), _mm_andnot_si128(mask, no));
        };
        unsigned diagonals = 0;
        for (; x + 4 < width; x += 4)
        {
            const auto top = load(a + x), bottom = load(b + x);
            const auto vert = delta(top, bottom);
            auto t = top, s = bottom;
            if (_mm_movemask_epi8(_mm_cmpgt_epi32(vert, threshold)))
            {
                const auto tl = load(a + x - 1), tr = load(a + x + 1);
                const auto bl = load(b + x - 1), br = load(b + x + 1);
                const auto left = delta(tl, br), right = delta(tr, bl);
                const auto l = _mm_and_si128(_mm_cmpgt_epi32(vert, _mm_add_epi32(left, threshold)),
                                              _mm_cmpgt_epi32(right, _mm_add_epi32(left, margin)));
                const auto r = _mm_and_si128(_mm_cmpgt_epi32(vert, _mm_add_epi32(right, threshold)),
                                              _mm_cmpgt_epi32(left, _mm_add_epi32(right, margin)));
                t = select(l, tl, select(r, tr, top));
                s = select(l, br, select(r, bl, bottom));
                const unsigned axes = (unsigned)_mm_movemask_ps(_mm_castsi128_ps(_mm_or_si128(l, r)));
                diagonals += (axes & 1u) + ((axes >> 1) & 1u) + ((axes >> 2) & 1u) + (axes >> 3);
            }
            const auto rounded = _mm_avg_epu8(t, s);
            const auto avg = _mm_sub_epi8(rounded, _mm_and_si128(_mm_xor_si128(t, s), one));
            _mm_storeu_si128(reinterpret_cast<__m128i*>(out + x), avg);
        }
        return diagonals;
    }
#endif

    struct PresentPacing
    {
        using Clock = std::chrono::steady_clock;
        Clock::time_point next{}, next_flip{};
        unsigned last_flip = 0;
        bool published = false;

        // Da ao EE uma janela curta para continuar rodando enquanto
        // o worker termina. Passado o prazo, volta a sincronizar.
        Clock::time_point queue_deadline{};

        bool defer_for_queue(Clock::time_point now, bool queue_idle)
        {
            if (queue_idle)
            {
                queue_deadline = {};
                return false;
            }
            if (queue_deadline == Clock::time_point{})
                queue_deadline = now + std::chrono::milliseconds(33);
            if (now < queue_deadline)
                return true;
            queue_deadline = {};
            return false;
        }

        bool can_advance(Clock::time_point now, unsigned flip) const
        {
            return published && flip != last_flip && now >= next_flip;
        }

        void did_publish(Clock::time_point frame_start, Clock::time_point work_start,
                         Clock::time_point finish, unsigned flip, bool legacy)
        {
            const auto work = finish - work_start;
            const auto period = roj::presentation_period(ps2_settings().fps);
            if (legacy)
            {
                next = finish + std::max<Clock::duration>(std::chrono::milliseconds(33), work * 3);
                next_flip = work_start + std::chrono::milliseconds(16);
            }
            else
            {
                // Mantem a fase entre apresentacoes. Iniciar o prazo apos
                // drenar a fila atrasava cada prazo e descartava o VBlank seguinte.
                next = published ? next + period : frame_start + period;
                if (next < frame_start) next = frame_start + period;
                if (next < finish) next = finish;
                // Tolera ate 1 ms de variacao do VBlank apenas se houver
                // uma troca nova; o mesmo framebuffer nao ganha essa antecipacao.
                const auto tolerance = std::min<Clock::duration>(period / 4, std::chrono::milliseconds(1));
                next_flip = next - tolerance;
            }
            last_flip = flip;
            published = true;
        }
    };

    static void gs_ui_scale_geometry(UiGlyphP& result,const UiGlyphP& source,unsigned width,unsigned height)
    {
        const double sx=width/640.0,sy=height/448.0;
        result.fbp=source.fbp; result.vsync=source.vsync; result.tint=source.tint;
        result.outline=source.outline; result.ring_hud=source.ring_hud;
        result.x=int(std::lround(source.x*sx)); result.y=int(std::lround(source.y*sy));
        result.w=int(std::lround((source.x+source.w)*sx))-result.x;
        result.h=int(std::lround((source.y+source.h)*sy))-result.y;
        result.clip_x0=int(std::lround(source.clip_x0*sx)); result.clip_x1=int(std::lround(source.clip_x1*sx));
        result.clip_y0=int(std::lround(source.clip_y0*sy)); result.clip_y1=int(std::lround(source.clip_y1*sy));
    }

    static UiGlyphP gs_ui_scale_glyph(const UiGlyphP& source,unsigned width,unsigned height)
    {
        UiGlyphP result;
        gs_ui_scale_geometry(result,source,width,height);
        const double sx=width/640.0,sy=height/448.0;
        result.rgba.assign(size_t(result.w)*result.h,0);
        const auto sample=[&](int x,int y){return x<0||y<0||x>=source.w||y>=source.h?0u:source.rgba[size_t(y)*source.w+x];};
        for(int y=0;y<result.h;++y)for(int x=0;x<result.w;++x){
            const double u=(result.x+x+.5)/sx-source.x-.5,v=(result.y+y+.5)/sy-source.y-.5;
            const int ix=int(std::floor(u)),iy=int(std::floor(v));
            const unsigned fx=unsigned(std::lround((u-ix)*256)),fy=unsigned(std::lround((v-iy)*256));
            const uint32_t top=gs_text_lerp(sample(ix,iy),sample(ix+1,iy),fx);
            const uint32_t bottom=gs_text_lerp(sample(ix,iy+1),sample(ix+1,iy+1),fx);
            result.rgba[size_t(y)*result.w+x]=gs_text_lerp(top,bottom,fy);
        }
        return result;
    }

    struct UiGlyphScaleCache
    {
        struct Entry { bool valid = false; UiGlyphP source, scaled; };
        unsigned width = 0, height = 0;
        std::vector<Entry> entries;
        void begin(unsigned w, unsigned h, size_t count)
        {
            if (width != w || height != h) entries.clear();
            width = w; height = h;
            entries.resize(count);
        }
        const UiGlyphP& get(size_t index, const UiGlyphP& source)
        {
            Entry& entry = entries[index];
            const UiGlyphP& old = entry.source;
            UiGlyphP layout; gs_ui_scale_geometry(layout,source,width,height);
            const bool same = entry.valid && old.fbp == source.fbp &&
                old.w == source.w && old.h == source.h && old.rgba == source.rgba &&
                layout.w == entry.scaled.w && layout.h == entry.scaled.h &&
                int64_t(layout.x-entry.scaled.x)*640 == int64_t(source.x-old.x)*width &&
                int64_t(layout.y-entry.scaled.y)*448 == int64_t(source.y-old.y)*height;
            if (!same)
            {
                entry.source = source;
                entry.scaled = gs_ui_scale_glyph(source, width, height);
                entry.valid = true;
            }
            else
            {
                // Integer translations with the same sampling phase reuse
                // texels during menu slides as well as stationary screens.
                layout.rgba = std::move(entry.scaled.rgba);
                entry.scaled = std::move(layout);
                entry.source.x = source.x; entry.source.y = source.y;
            }
            return entry.scaled;
        }
    };

    void gs_present_frame()
    {
        using clk = PresentPacing::Clock;
        static PresentPacing pacing;
        static const bool flip_pacing = battle_profile_enabled("PS2_GS_PRESENT_ON_FLIP");
        const clk::time_point t0 = clk::now();
        unsigned flip = g_gs_flips.load(std::memory_order_relaxed);
        const bool early = t0 < pacing.next;
        if (early && (!flip_pacing || !pacing.can_advance(t0, flip)))
        {
            if (g_perf_gs) ++g_present_profile.pacing;
            return;
        }

        const bool capture_pending = g_font_capture_request.load(std::memory_order_relaxed);
        static const bool defer_present = [] {
            const char* value = std::getenv("PS2_GS_PRESENT_DEFER");
            return value != nullptr && std::strcmp(value, "1") == 0;
        }();
        const bool queue_drained = flip_pacing || g_field_audit || g_field_raster ||
            capture_pending || defer_present;
        // O EE e o unico produtor. Se a fila ja terminou, nenhum
        // comando novo pode chegar ate esta chamada de apresentacao sair.
        if (defer_present && pacing.defer_for_queue(t0, gs_q_idle()))
        {
            if (g_perf_gs) ++g_present_profile.queue_skips;
            return;
        }
        if (queue_drained)
        {
            // Com GS assincrono, le DISPFB e o voto apenas apos os comandos
            // enfileirados. No GS sincrono, gs_q_drain retorna imediatamente.
            const uint64_t wait_start = g_perf_gs ? prof_now() : 0;
            gs_q_drain();
            if (g_perf_gs) g_present_profile.queue_wait_ns += prof_now() - wait_start;
            flip = g_gs_flips.load(std::memory_order_relaxed);
        }

        DispParams d;

        if (!gs_display_params(d))
        {
            if (g_perf_gs) ++g_present_profile.invalid;
            return;
        }

        // O jogo desloca meia linha nos campos impares. A protecao contra
        // campos ainda desalinhados independe do limite de FPS do launcher.
        // Campos normalizados na rasterizacao continuam elegiveis abaixo.
        // PS2_FIELDLOCK=0 permite desativar a protecao em testes A/B.
        static const bool fieldlock = [] {
            const char* value = std::getenv("PS2_FIELDLOCK");
            return !value || value[0] != '0';
        }();
        // Teste K: reconstrucao APENAS ESPACIAL, com linhas da MESMA imagem.
        // A reconstrucao usa apenas pixels da imagem atual. O ajuste opcional
        // abaixo usa g_half_vote para alinhar a imagem ja reconstruida.
        // Os testes I/J usavam capturas temporais: com 4-6 FPS a defasagem vira tremor.
        const bool spatial_active = std::strcmp(g_field_recon_mode, "spatial") == 0
            && d.magv == 2 && d.h >= 2 && d.w >= 3;

        // O FIELDLOCK legado e aplicado inclusive ao modo espacial.
        // Teste L: respeita FIELDLOCK tambem no modo espacial.
        // No K, !spatial_active ignorava o bloqueio e exibia quadros de
        // posicionamento vertical diferente, causando a tremulacao observada.
        // Manter o descarte legado ate modelarmos a paridade real do GS.
        const unsigned fbp_index = (d.fbp / 32u) & 0x1FFu;
        const float vote = g_half_vote[fbp_index];
        const FieldRasterState& raster_state = g_field_raster_state[fbp_index];
        // A captura do vagao encontrou dois vertices com OFY=0 no
        // mesmo layout CT32. Mantem esses vertices onde foram desenhados.
        // Qualquer outro estado desconhecido continua vetando a excecao.
        const bool train_zero_pair = raster_state.shifted_train &&
            raster_state.unknown == 2u && raster_state.zero_origin == 2u;
        const bool raster_field = g_field_raster && spatial_active &&
            g_gs_crt_int && g_gs_crt_ffmd && d.magv == 2u &&
            d.w == 640u && d.h == 448u && d.fbw == 10u && d.psm == 0u &&
            d.dbx == 0u && d.dby == 0u && (d.fbp == 0u || d.fbp == 2240u) &&
            raster_state.shifted != 0 && (raster_state.unknown == 0 || train_zero_pair) &&
            (raster_state.base == 0 || raster_state.shifted_train);
        // No framebuffer entrelacado conhecido, uma troca nova identifica
        // a imagem concluida. Reprocessar a mesma troca repete os pixels e
        // compete com a renderizacao do proximo campo sem melhorar o movimento.
        if (flip_pacing && pacing.published && flip != 0 && flip == pacing.last_flip &&
            !capture_pending && spatial_active && g_gs_crt_int && g_gs_crt_ffmd &&
            d.w == 640u && d.h == 448u && d.fbw == 10u && d.psm == 0u &&
            d.dbx == 0u && d.dby == 0u && (d.fbp == 0u || d.fbp == 2240u))
        {
            if (g_perf_gs) ++g_present_profile.unchanged;
            return;
        }
        FieldFlipAudit* audit = nullptr;
        if (g_field_audit && flip != 0)
        {
            FieldFlipAudit& entry = g_field_flips[flip % kFieldAuditCapacity];
            if (entry.flip == flip && entry.fbp == d.fbp / 32u)
            {
                audit = &entry;
                ++audit->checked;
                audit->present_vote = (int)vote;
            }
        }
        if (fieldlock && d.magv > 1 && vote > 0.0f && !raster_field)
        {
            if (g_field_raster)
            {
                g_present_profile.raster_mixed += raster_state.base != 0;
                g_present_profile.raster_unknown += raster_state.unknown != 0;
                if (g_field_train_diag && raster_state.unknown != 0)
                    g_field_train_sample = {flip, fbp_index, d.w, d.h, d.magv, d.fbw, d.psm, raster_state};
            }
            if (audit != nullptr) ++audit->blocked;
            if (g_perf_gs) ++g_present_profile.fieldlock;
            return;
        }

        // Mede o trabalho de apresentacao a partir daqui. A espera para
        // drenar comandos do GS nao deve prolongar o prazo entre quadros.
        const auto work_start = clk::now();
        const uint64_t prof_t0 = g_perf ? prof_now() : 0;
        // O swap devolve o buffer anterior para a proxima apresentacao.
        static std::vector<uint32_t> pix;
        pix.resize((size_t)d.w * d.h);
        static GsScanoutRow scanout;
        static const bool scanout_fast = [] {
            const char* value = std::getenv("PS2_GS_SCANOUT_FAST");
            return value == nullptr || std::strcmp(value, "0") != 0;
        }();

        // campo impar: desloca PS2_FIELDSHIFT * (MAGV / 2) linhas de saida; PS2_NOFIELDFIX=1 desliga
        static const bool nofix = std::getenv("PS2_NOFIELDFIX") != nullptr;
        static const int fieldshift = std::getenv("PS2_FIELDSHIFT") != nullptr ? std::atoi(std::getenv("PS2_FIELDSHIFT")) : 1;
        const bool half = !nofix && !raster_field && d.magv > 1 && vote > 0.0f;
        const int shift = half ? fieldshift * (int)(d.magv / 2) : 0;
        const uint32_t nlines = (d.h + d.magv - 1) / d.magv;
        static const bool nosmoothv = std::getenv("PS2_NOSMOOTHV") != nullptr;
        FontCapture capture;
        // Aguarda uma imagem elegivel para que os tres arquivos sejam do
        // mesmo framebuffer, com os mesmos registradores e conteudo de VRAM.
        if (capture_pending && g_font_capture_request.exchange(false))
        {
            capture.flip = flip;
            capture.fbp = d.fbp / 32u;
            capture.psm = d.psm;
            capture.magv = d.magv;
            capture.w = d.w;
            capture.source_h = nlines;
            capture.h = d.h;
            capture.spatial = spatial_active;
            capture.native_ui = g_ui_native;
            capture.source.resize((size_t)d.w * nlines);
            for (uint32_t y = 0; y < nlines; ++y)
                scanout.read(capture.source.data() + (size_t)y * d.w, d, y, scanout_fast);
        }

        // Em resolucao ampliada, a reconstrucao de 224 para 448 linhas seria
        // calculada e descartada logo depois. So a execute para Original ou
        // para as capturas F11, que precisam guardar as tres etapas.
        std::vector<uint32_t> high_pixels;
        unsigned high_width=d.w,high_height=d.h;
        const bool high_ready=d.magv==2u && d.w==640u && d.h==448u && d.fbw==10u &&
            !d.psm && !d.dbx && !d.dby && gs_vk_hi_resolve(d.fbp,high_pixels,high_width,high_height);
        if (!high_ready || capture.w != 0) {
        if (spatial_active)
        {
            // Snapshot do MESMO framebuffer (FBP/DBX/DBY) na resolucao de origem.
            // O jogo usa aproximadamente 640x224 por framebuffer, alternando FBP 0/70.
            // Ler 448 linhas diretamente invadiria o proximo framebuffer: nao fazer isso.
            // Esta espera e custo diagnostico; NAO e uma otimizacao de desempenho.
            if (!queue_drained)
                gs_q_drain();
            static std::vector<uint32_t> source;
            source.resize((size_t)nlines * d.w);
            for (uint32_t y = 0; y < nlines; ++y)
                scanout.read(&source[(size_t)y * d.w], d, y, scanout_fast);

            // Reconstrucao edge-directed apenas de linhas IMPARES ausentes.
            // Evita combinar duas capturas de tempos diferentes (weave I/J), que
            // produziram a oscilacao. Preserva as linhas pares do GS pixel a pixel.
            auto avg_rgb = [](uint32_t a, uint32_t b) -> uint32_t
            {
                return ((a & 0xFEFEFEFEu) >> 1) + ((b & 0xFEFEFEFEu) >> 1)
                    + (a & b & 0x01010101u);
            };
            auto color_delta = [](uint32_t a, uint32_t b) -> int
            {
                return std::abs((int)(a & 255u) - (int)(b & 255u))
                    + std::abs((int)((a >> 8) & 255u) - (int)((b >> 8) & 255u))
                    + std::abs((int)((a >> 16) & 255u) - (int)((b >> 16) & 255u));
            };
            uint64_t diagonals = 0;
            for (uint32_t y = 0; y < d.h; ++y)
            {
                const uint32_t row = std::min(y / 2u, nlines - 1u);
                const uint32_t* a = &source[(size_t)row * d.w];
                uint32_t* out = &pix[(size_t)y * d.w];
                if ((y & 1u) == 0u || row + 1u >= nlines)
                {
                    std::memcpy(out, a, (size_t)d.w * sizeof(uint32_t));
                    continue;
                }
                const uint32_t* b = &source[(size_t)(row + 1u) * d.w];
                uint32_t x = 0;
#if defined(__SSE2__) || defined(_M_X64)
                if (scanout_fast && d.w)
                {
                    out[0] = avg_rgb(a[0], b[0]);
                    x = 1;
                    diagonals += gs_spatial_row_sse2(out, a, b, d.w, x);
                }
#endif
                for (; x < d.w; ++x)
                {
                    const int vertical = color_delta(a[x], b[x]);
                    uint32_t top = a[x], bottom = b[x];
                    // Escolhe vizinhos diagonais APENAS se ambos concordarem muito
                    // melhor que a interpolacao vertical. Na duvida, preserva a media.
                    // Com delta vertical <= 30, nenhuma diagonal pode
                    // satisfazer o limiar abaixo. A media permanece identica.
                    if (x > 0u && x + 1u < d.w && (!scanout_fast || vertical > 30))
                    {
                        const int left = color_delta(a[x - 1u], b[x + 1u]);
                        const int right = color_delta(a[x + 1u], b[x - 1u]);
                        if (left + 30 < vertical && left + 12 < right)
                        {
                            top = a[x - 1u]; bottom = b[x + 1u]; ++diagonals;
                        }
                        else if (right + 30 < vertical && right + 12 < left)
                        {
                            top = a[x + 1u]; bottom = b[x - 1u]; ++diagonals;
                        }
                    }
                    out[x] = avg_rgb(top, bottom);
                }
            }
            static unsigned reports_k = 0;
            if (reports_k++ < 12u || (reports_k & 255u) == 0u)
                std::fprintf(stderr,
                    "  [GS FIELD K] modo=spatial FBP=%u FFMD=%u fonte=%ux%u saida=%ux%u diagonais=%llu (sem weave)\n",
                    d.fbp / 32u, g_gs_crt_ffmd, d.w, nlines, d.w, d.h,
                    (unsigned long long)diagonals);
        }
        else if (!nosmoothv && shift == 0 && d.magv == 2 && nlines >= 2)
        {
            // linhas pares = linha da fonte; impares = media das duas vizinhas (em vez de duplicar: bordas menos "quadradas")
            static std::vector<uint32_t> L;
            L.resize((size_t)nlines * d.w);

            for (uint32_t by = 0; by < nlines; ++by)
                scanout.read(&L[(size_t)by * d.w], d, by, scanout_fast);

            for (uint32_t r = 0; r < d.h; r++)
            {
                const uint32_t* a = &L[(size_t)std::min(r / 2, nlines - 1) * d.w];
                uint32_t* o = &pix[(size_t)r * d.w];

                if ((r & 1u) == 0)
                {
                    std::memcpy(o, a, (size_t)d.w * sizeof(uint32_t));
                }
                else
                {
                    const uint32_t* b = &L[(size_t)std::min(r / 2 + 1, nlines - 1) * d.w];

                    for (uint32_t x = 0; x < d.w; x++)
                        o[x] = ((a[x] & 0xFEFEFEFEu) >> 1) + ((b[x] & 0xFEFEFEFEu) >> 1) + (a[x] & b[x] & 0x01010101u);
                }
            }
        }
        else
        {
            if (shift != 0)
                std::fill(pix.begin(), pix.end(), 0u);

            for (uint32_t by = 0; by < nlines; by++)
            {
                int r0 = (int)(by * d.magv) + shift, r1 = r0 + (int)d.magv;

                if (by == 0) r0 = 0;                 // a primeira linha cobre o topo
                if (by + 1 == nlines) r1 = (int)d.h; // a ultima cobre o fim

                const int lo = std::max(0, r0), hi = std::min((int)d.h, r1);

                if (lo >= hi)
                    continue;

                uint32_t* row = &pix[(size_t)lo * d.w];

                scanout.read(row, d, by, scanout_fast);

                for (int r = lo + 1; r < hi; r++)
                    std::memcpy(&pix[(size_t)r * d.w], row, (size_t)d.w * sizeof(uint32_t));
            }
        }

        }
        if (capture.w != 0)
            capture.reconstructed = pix;

        // Teste U: um resumo dos estados TEX1/filtragem da geometria 3D.
        // Nao consulta a VRAM nem altera a imagem. O primeiro resumo sera
        // emitido assim que for apresentado um framebuffer de batalha.
        if (gs_tex_u_audit_enabled())
        {
            static unsigned reports_u = 0;
            const uint64_t total = g_tex_u_3d.load(std::memory_order_relaxed);
            if (total > 0u && (reports_u++ < 10u || (reports_u & 255u) == 0u))
                std::fprintf(stderr,
                    "  [GS TEX U] resumo tri3d=%llu bilinear=%llu nearest=%llu bias=%.3f "
                    "FBP=%u (cumulativo)\n",
                    (unsigned long long)total,
                    (unsigned long long)g_tex_u_linear.load(std::memory_order_relaxed),
                    (unsigned long long)g_tex_u_nearest.load(std::memory_order_relaxed),
                    (double)gs_tex_uv_bias_u(), d.fbp / 32u);
        }

        // Teste T: AA guiado pelas arestas REAIS dos triangulos rasterizados,
        // nao por contraste da tela inteira (diferente do teste S).
        // Conserva VRAM, Z, DMA e a camada de fonte P/R intactos.
        if (gs_edge_t_enabled() && spatial_active && d.w == 640u && d.h == 448u &&
            d.fbw == 10u && d.psm == 0u && d.dbx == 0u && d.dby == 0u &&
            (d.fbp == 0u || d.fbp == 2240u))
        {
            const uint64_t t_edge = prof_now();
            std::vector<GsEdgeT> edges;
            {
                std::lock_guard<std::mutex> lock(g_edge_t_mu);
                edges = g_edge_t_frame[d.fbp == 0u ? 0 : 1];
            }
            uint32_t count = 0u;
            if (edges.size() == 640u * 224u)
            {
                // Fonte de comparacao imutavel: os pixels ja modificados
                // nao influenciam o proximo pixel (evita borrar em cadeia).
                const std::vector<uint32_t> original = pix;
                for (unsigned y = 1; y < 223; ++y)
                    for (unsigned x = 1; x < 639; ++x)
                    {
                        const GsEdgeT& e = edges[(size_t)y * 640u + x];
                        if (e.coverage == 0u || e.coverage >= 4u ||
                            (unsigned)(g_gs_vsyncs - e.vsync) > 60u)
                            continue;
                        // Desenhos posteriores (sprites/menu/outros modelos)
                        // invalidam automaticamente esta anotacao via cor.
                        const uint32_t current = gs_read_px(0x00u, d.fbp, d.fbw, x, y);
                        if (current != e.rgba)
                            continue;
                        const int nx = (int)x + (int)e.outside_x;
                        const int ny = (int)y + (int)e.outside_y;
                        if (nx < 0 || nx >= 640 || ny < 0 || ny >= 224)
                            continue;
                        // Apenas transicoes perceptiveis, evitando suavizar
                        // detalhes internos das texturas.
                        for (int sub = 0; sub < 2; ++sub)
                        {
                            const size_t dstidx = ((size_t)y * 2u + (size_t)sub) * 640u + x;
                            const size_t srcidx = ((size_t)ny * 2u + (size_t)sub) * 640u + (unsigned)nx;
                            const uint32_t fg = original[dstidx], bg = original[srcidx];
                            const int diff = std::abs((int)((fg >> 16) & 255u) - (int)((bg >> 16) & 255u))
                                           + std::abs((int)((fg >> 8) & 255u) - (int)((bg >> 8) & 255u))
                                           + std::abs((int)(fg & 255u) - (int)(bg & 255u));
                            if (diff < 36)
                                continue;
                            // Mistura moderada: fator externo = (4 - cobertura)/8.
                            // Mais conservador que apagar 100% do sample ausente.
                            const unsigned outside = 4u - e.coverage;
                            const unsigned inside = 8u - outside;
                            const unsigned r = ((((fg >> 16) & 255u) * inside) + (((bg >> 16) & 255u) * outside) + 4u) / 8u;
                            const unsigned g = ((((fg >> 8) & 255u) * inside) + (((bg >> 8) & 255u) * outside) + 4u) / 8u;
                            const unsigned b = (((fg & 255u) * inside) + ((bg & 255u) * outside) + 4u) / 8u;
                            pix[dstidx] = (r << 16) | (g << 8) | b;
                            ++count;
                        }
                    }
            }
            static unsigned reports_t = 0;
            if (reports_t++ < 10u || (reports_t & 255u) == 0u)
                std::fprintf(stderr,
                    "  [GS EDGE T] cobertura_2x2=1 fbp=%u triangulos=%llu bordas_amostradas=%llu pixels_suavizados=%u tempo=%llu ms (VRAM intacta)\n",
                    d.fbp / 32u,
                    (unsigned long long)g_edge_t_triangles.load(std::memory_order_relaxed),
                    (unsigned long long)g_edge_t_border_pixels.load(std::memory_order_relaxed),
                    count, (unsigned long long)((prof_now() - t_edge) / 1000000u));
        }

        // Teste S (cena): antisserrilhamento na APRESENTACAO, depois da
        // reconstrucao espacial e ANTES de sobrepor a fonte P/R.
        // Nao modifica a VRAM nem os triangulos originais do PS2.
        static const char* edgeaa_scene = std::getenv("PS2_GS_PRESENT_AA");
        if (edgeaa_scene != nullptr && edgeaa_scene[0] != '0')
        {
            const uint64_t start = prof_now();
            const uint32_t changed = gs_aa_apply_s(pix, (int)d.w, (int)d.h,
                30.0f, 3.0f, 0.85f, nullptr);
            static unsigned reports = 0;
            if (reports++ < 8u)
                std::fprintf(stderr,
                    "  [GS AA S] cena=1 alterados=%u %ux%u tempo=%llu ms (somente apresentacao)\n",
                    changed, d.w, d.h,
                    (unsigned long long)((prof_now() - start) / 1000000u));
        }

        // Teste A/B: alinha somente o framebuffer conhecido do jogo quando o
        // voto identifica um campo deslocado. O sinal indica a direcao na tela.
        static const int align_shift = []
        {
            const char* value = std::getenv("PS2_FIELD_SPATIAL_ALIGN");
            if (value == nullptr) return 0;
            if (std::strcmp(value, "1") == 0 || std::strcmp(value, "+1") == 0) return 1;
            return std::strcmp(value, "-1") == 0 ? -1 : 0;
        }();
        if (align_shift != 0 && spatial_active && half && g_gs_crt_int && g_gs_crt_ffmd &&
            d.w == 640u && d.h == 448u && d.fbw == 10u && d.psm == 0u &&
            d.dbx == 0u && d.dby == 0u && (d.fbp == 0u || d.fbp == 2240u))
        {
            gs_align_spatial_field(pix.data(), d.w, d.h, align_shift);
            if (g_perf_gs) ++g_present_profile.aligned;
        }

        // text_aa pertence somente aos glifos (gs_ui_filter). Filtrar aqui
        // apagava detalhes de personagens e inimigos na cena inteira, mesmo
        // quando o usuario queria suavizar apenas as letras. O filtro de cena
        // continua disponivel explicitamente em PS2_GS_PRESENT_AA acima.

        // Teste P: compoe o TEXTO da propria imagem/FBP sobre o resultado
        // de 640x448. Nao faz weave e nao altera o FIELDLOCK do teste L.
        // O blend GS ALPHA=0x44 e (fonte-destino)*alfa/128+destino.
        unsigned present_width=d.w,present_height=d.h;
        if(high_ready){
            for(uint32_t& pixel:high_pixels)pixel=((pixel&255u)<<16)|(pixel&0xFF00u)|((pixel>>16)&255u);
            pix.swap(high_pixels);present_width=high_width;present_height=high_height;
        }
        static const bool ui_native_present = g_ui_native;
        if (ui_native_present && d.magv == 2u && d.w == 640u && d.h == 448u &&
            d.dbx == 0u && d.dby == 0u)
        {
            std::vector<UiGlyphP> glyphs;
            {
                std::lock_guard<std::mutex> lk(g_ui_native_mu);
                for (const UiGlyphP& glyph : g_ui_native_glyphs)
                    if (glyph.fbp == d.fbp)
                        glyphs.push_back(glyph);
            }
            if(present_width!=d.w||present_height!=d.h)
            {
                // Each display buffer owns its cache. Compare actual texels,
                // so palette changes, fades, clipping and cursor animation
                // apply immediately; only equivalent samples are reused.
                static UiGlyphScaleCache caches[2];
                auto& cache = caches[d.fbp == 2240 ? 1 : 0];
                cache.begin(present_width, present_height, glyphs.size());
                for(size_t i=0;i<glyphs.size();++i) glyphs[i]=cache.get(i,glyphs[i]);
            }
            gs_ui_text_background(pix, int(present_width), int(present_height), glyphs, ps2_settings().text_style);
            for (const UiGlyphP& glyph : glyphs)
                for (int gy = 0; gy < glyph.h; ++gy)
                {
                    const int dy = glyph.y + gy;
                    if (dy < 0 || dy >= (int)present_height) continue;
                    for (int gx = 0; gx < glyph.w; ++gx)
                    {
                        const int dx = glyph.x + gx;
                        if (dx < 0 || dx >= (int)present_width) continue;
                        const uint32_t tex = glyph.rgba[(size_t)gy * glyph.w + gx];
                        const int alpha = (int)((tex >> 24) & 255u);
                        if (alpha == 0) continue;
                        uint32_t& dst = pix[(size_t)dy * present_width + dx];
                        const int sr = (int)(tex & 255u), sg = (int)((tex >> 8) & 255u);
                        const int sb = (int)((tex >> 16) & 255u);
                        const int dr = (int)((dst >> 16) & 255u);
                        const int dg = (int)((dst >> 8) & 255u);
                        const int db = (int)(dst & 255u);
                        const int nr = gs_clamp255(dr + (((sr - dr) * alpha) >> 7));
                        const int ng = gs_clamp255(dg + (((sg - dg) * alpha) >> 7));
                        const int nb = gs_clamp255(db + (((sb - db) * alpha) >> 7));
                        dst = (uint32_t)((nr << 16) | (ng << 8) | nb);
                    }
                }
            static unsigned reported_p = 0;
            if (!glyphs.empty() && reported_p++ < 10u)
                std::fprintf(stderr,
                    "  [GS UI P] apresentacao_nativa fbp=%u glifos=%zu (sem weave)\n",
                    d.fbp / 32u, glyphs.size());
        }

        if (capture.w != 0) {
            capture.final = pix;
            capture.final_w=present_width;capture.final_h=present_height;
        }
        {
            std::lock_guard<std::mutex> lk(g_win_mu);
            if (capture.w != 0)
                g_font_capture = std::move(capture);
            g_win_pix.swap(pix);
            g_win_w = present_width;
            g_win_h = present_height;
            // Publica a sequencia junto com os pixels e as dimensoes.
            g_win_seq.fetch_add(1);
        }

        if (audit != nullptr) ++audit->published;
        if (!queue_drained)
            flip = g_gs_flips.load(std::memory_order_relaxed);
        if (g_perf_gs)
        {
            // Conta publicacoes efetivas, depois do FIELDLOCK.
            if (pacing.published && flip == pacing.last_flip)
                ++g_present_profile.same_flip;
            if (raster_field && (!pacing.published || flip != pacing.last_flip))
            {
                ++g_present_profile.raster_fields;
                if (raster_state.shifted_train) ++g_present_profile.raster_train;
                if (train_zero_pair && fieldlock && vote > 0.0f) ++g_present_profile.raster_train_zero;
                g_present_profile.raster_sprite_vertices += raster_state.shifted_sprite;
                g_present_profile.raster_triangle_vertices += raster_state.shifted_triangle;
            }
            if (early)
                ++g_present_profile.early_flip;
        }
        if (g_perf) g_prof_present_ns += prof_now() - prof_t0;
        static const bool legacy_pacing = std::getenv("PS2_GS_LEGACY_PACING") != nullptr;
        pacing.did_publish(t0, work_start, clk::now(), flip, legacy_pacing);
    }

    static void gs_save_bmp(const char* name, const std::vector<uint32_t>& pixels,
                            uint32_t width, uint32_t height)
    {
        const uint32_t stride = (width * 3 + 3) & ~3u;
        const uint32_t image_bytes = stride * height;
        const uint32_t size = 54 + image_bytes, offset = 54, dib = 40, planes_bpp = (24u << 16) | 1u;
        const int32_t top_down_height = -(int32_t)height;
        unsigned char header[54] = {'B', 'M'};
        std::memcpy(header + 2, &size, 4);
        std::memcpy(header + 10, &offset, 4);
        std::memcpy(header + 14, &dib, 4);
        std::memcpy(header + 18, &width, 4);
        std::memcpy(header + 22, &top_down_height, 4);
        std::memcpy(header + 26, &planes_bpp, 4);
        std::memcpy(header + 34, &image_bytes, 4);
        std::vector<unsigned char> line(stride, 0);
        FILE* f = std::fopen(name, "wb");
        if (f == nullptr)
        {
            std::fprintf(stderr, "  [captura] falha ao abrir %s\n", name);
            return;
        }

        bool ok = std::fwrite(header, 1, sizeof(header), f) == sizeof(header);
        for (uint32_t y = 0; ok && y < height; ++y)
        {
            for (uint32_t x = 0; x < width; ++x)
            {
                const uint32_t p = pixels[(size_t)y * width + x];
                line[x * 3] = (unsigned char)p;
                line[x * 3 + 1] = (unsigned char)(p >> 8);
                line[x * 3 + 2] = (unsigned char)(p >> 16);
            }
            ok = std::fwrite(line.data(), 1, stride, f) == stride;
        }
        if (std::fclose(f) != 0) ok = false;
        std::fprintf(stderr, "  [captura] %s %s\n", name, ok ? "gravado" : "falha na gravacao");
    }

    static void gs_save_font_capture(const FontCapture& capture)
    {
        static unsigned shots = 0;
        const unsigned id = ++shots;
        char name[80];
        std::fprintf(stderr,
            "  [captura fonte] id=%u troca=%u FBP=%u PSM=%02X MAGV=%u spatial=%u ui_native=%u\n",
            id, capture.flip, capture.fbp, capture.psm, capture.magv,
            (unsigned)capture.spatial, (unsigned)capture.native_ui);
        std::snprintf(name, sizeof name, "gs_fonte_%u_vram.bmp", id);
        gs_save_bmp(name, capture.source, capture.w, capture.source_h);
        std::snprintf(name, sizeof name, "gs_fonte_%u_recon.bmp", id);
        gs_save_bmp(name, capture.reconstructed, capture.w, capture.h);
        std::snprintf(name, sizeof name, "gs_fonte_%u_final.bmp", id);
        gs_save_bmp(name, capture.final, capture.final_w?capture.final_w:capture.w, capture.final_h?capture.final_h:capture.h);
    }

    // Grava o quadro exibido em PPM (PS2_GSDUMP=<segundos entre dumps>).
    void gs_dump_frame(unsigned index)
    {
        gs_trace_flush(index);

        DispParams dp;
        const bool valid = gs_display_params(dp);
        const uint32_t fbp = dp.fbp, fbw = dp.fbw, psm = dp.psm, dbx = dp.dbx, dby = dp.dby, w = dp.w, h = dp.h, magv = dp.magv;
        const uint64_t pmode = dp.pmode, dispfb = dp.dispfb, display = dp.display;

        if (!valid)
        {
            std::fprintf(stderr, "  [gs] dump %u: parametros de exibicao invalidos (PMODE=0x%llX DISPFB=0x%llX DISPLAY=0x%llX)\n",
                index, (unsigned long long)pmode, (unsigned long long)dispfb, (unsigned long long)display);
            return;
        }

        char name[64];
        std::snprintf(name, sizeof(name), "gs_%03u.ppm", index);
        FILE* f = std::fopen(name, "wb");

        if (f == nullptr)
            return;

        std::fprintf(f, "P6\n%u %u\n255\n", w, h);

        for (uint32_t y = 0; y < h; y++)
            for (uint32_t x = 0; x < w; x++)
            {
                uint32_t v = gs_read_px(psm, fbp, fbw, dbx + x, dby + y / magv);
                uint8_t px[3];

                if (psm == 0x02 || psm == 0x0A)
                {
                    px[0] = (uint8_t)((v & 31) << 3); px[1] = (uint8_t)(((v >> 5) & 31) << 3); px[2] = (uint8_t)(((v >> 10) & 31) << 3);
                }
                else
                {
                    px[0] = (uint8_t)v; px[1] = (uint8_t)(v >> 8); px[2] = (uint8_t)(v >> 16);
                }

                std::fwrite(px, 1, 3, f);
            }

        std::fclose(f);
        std::fprintf(stderr, "  [gs] dump %u: %s (%ux%u, buffer %ux%u, repeticao=%u, PSM=0x%02X, FBP=%u, FBW=%u, DISPLAY=0x%llX, nao suportado ate agora: %llu)\n", index, name, w, h,
            w, (h + magv - 1) / magv, magv, psm, fbp / 32, fbw, (unsigned long long)display, (unsigned long long)g_st.unsupported);
    }

    // ---------------------------------------------------------------------------------------------
    // GS assincrono (PS2_GS_THREAD=1): a thread do EE so decodifica o GIF e enfileira os comandos; uma thread propria
    // executa, EM ORDEM, os registradores do GS (inclui os desenhos), as transferencias de imagem e as trocas de tela.
    // Assim o rasterizador roda em paralelo com o codigo do jogo e do VU1 em vez de em serie com ele.
    // Estado do GS (g_st, VRAM, cache de CLUT) so e tocado pela thread do GS; a fila e SPSC (um produtor, um consumidor).
    // ---------------------------------------------------------------------------------------------
    struct GsCmd { uint64_t a, b; uint32_t kind, reg; };   // kind: 0 register, 1 image, 2 privileged register, 3 ring HUD snapshot

    constexpr uint32_t kGsQSize = 1u << 18;
    GsCmd g_gsq[kGsQSize];
    std::atomic<uint64_t> g_gsq_head{0}, g_gsq_tail{0};
    std::atomic<bool> g_gsq_stop{false};
    std::atomic<uint64_t> g_gsq_busy_ns{0};
    // Diagnostico: tempo e numero de esperas por fila cheia.
    std::atomic<uint64_t> g_gsq_full_wait_ns{0};
    std::atomic<uint64_t> g_gsq_full_wait_count{0};
    const bool g_gs_async = battle_profile_enabled("PS2_GS_THREAD");

    static const bool g_gsq_start_fast = [] {
        const char* value = std::getenv("PS2_GS_START_FAST");
        return !value || std::strcmp(value, "1") == 0;
    }();
    // Assim como write_head, esta marca pertence ao produtor da fila.
    static bool g_gsq_started = false;

    static const bool g_gsq_batch = [] {
        const char* value = std::getenv("PS2_GS_QUEUE_BATCH");
        return !value || std::strcmp(value, "1") == 0;
    }();
    // Reutiliza os cursores do produtor mesmo sem publicar em lotes.
    static const bool g_gsq_local_cursor = [] {
        const char* value = std::getenv("PS2_GS_LOCAL_CURSOR");
        return value && std::strcmp(value, "1") == 0;
    }();
    static const bool g_gsq_use_local_cursor = g_gsq_batch || g_gsq_local_cursor;
    // So o produtor modifica estes cursores. O consumidor ve apenas
    // os comandos anteriores ao head publicado com release.
    static uint64_t g_gsq_write_head = 0, g_gsq_safe_tail = 0;
    static uint64_t g_gsq_publications = 0;
    constexpr unsigned kGsPublishBatch = 16;

    static const bool g_gsq_wake_on_work = [] {
        const char* value = std::getenv("PS2_GS_WAKE_ON_WORK");
        return !value || std::strcmp(value, "1") == 0;
    }();
    static std::mutex g_gsq_work_mu;
    static std::condition_variable g_gsq_work_cv;
    static std::atomic<bool> g_gsq_worker_waiting{false};
    static std::atomic<uint64_t> g_gsq_work_wakes{0};

    // O consumidor marca a espera antes de reler head. O produtor
    // publica head antes de ler a marca. A ordem sequencial impede
    // que ambos deixem de observar a atualizacao do outro.
    static void gs_q_publish(uint64_t head)
    {
        if (g_perf_gs) ++g_gsq_publications;
        if (!g_gsq_wake_on_work)
        {
            g_gsq_head.store(head, std::memory_order_release);
            return;
        }

        g_gsq_head.store(head, std::memory_order_seq_cst);
        if (g_gsq_worker_waiting.load(std::memory_order_seq_cst))
        {
            std::lock_guard<std::mutex> lk(g_gsq_work_mu);
            g_gsq_work_cv.notify_one();
        }
    }

    static const bool g_gsq_limit_flips = [] {
        const char* value = std::getenv("PS2_GS_LIMIT_FLIPS");
        return value != nullptr && std::strcmp(value, "1") == 0;
    }();

    // O EE pode comecar a desenhar o campo seguinte, mas espera pela
    // conclusao do DISPFB anterior antes de publicar outra troca na fila.
    // Estado de escrita do produtor separado dos registradores do worker.
    static uint64_t g_gsq_pmode = 0, g_gsq_dispfb[2] = {};
    static uint64_t g_gsq_last_flip_end = 0;
    static uint64_t g_gsq_flip_wait_ns = 0, g_gsq_flip_waits = 0;
    static std::mutex g_gsq_flip_mu;
    static std::condition_variable g_gsq_flip_cv;
    static uint64_t g_gsq_priv_done = 0; // Protegido por g_gsq_flip_mu.

    static uint32_t gs_active_dispfb(uint64_t pmode)
    {
        return (pmode & 1u) ? 0x12000070u : 0x12000090u;
    }

    static bool gs_changed_fbp(uint64_t value, uint64_t previous)
    {
        return ((value ^ previous) & 0x1FFu) != 0;
    }

    static bool gs_queue_wait_previous_flip(uint32_t reg, uint64_t value)
    {
        if (reg == 0x12000000u)
            g_gsq_pmode = value;
        if (reg != 0x12000070u && reg != 0x12000090u)
            return false;

        uint64_t& previous = g_gsq_dispfb[reg == 0x12000090u];
        const bool flip = reg == gs_active_dispfb(g_gsq_pmode) && gs_changed_fbp(value, previous);
        previous = value;
        if (flip && g_gsq_last_flip_end != 0)
        {
            std::unique_lock<std::mutex> lk(g_gsq_flip_mu);
            if (g_gsq_priv_done < g_gsq_last_flip_end)
            {
                const uint64_t start = prof_now();
                g_gsq_flip_cv.wait(lk, [] { return g_gsq_priv_done >= g_gsq_last_flip_end; });
                g_gsq_flip_wait_ns += prof_now() - start;
                ++g_gsq_flip_waits;
            }
        }
        return flip;
    }

    struct CadenceGsProfile
    {
        unsigned gaps[5] = {}; // 0, 1, 2, 3 ou pelo menos 4 VBLANKs entre trocas.
        uint64_t max_gap_ns = 0;
    };
    std::mutex g_cadence_gs_mu;
    CadenceGsProfile g_cadence_gs;

    static void gs_profile_flip()
    {
        // Chamado pelo GS sincrono ou pelo worker. O contador de VBLANKs
        // representa entregas no EE ate a aplicacao do DISPFB pelo GS.
        const uint64_t now = prof_now();
        const uint64_t vblank = g_vsync_del.load(std::memory_order_relaxed);
        std::lock_guard<std::mutex> lk(g_cadence_gs_mu);
        static uint64_t last_ns = 0, last_vblank = 0;
        if (last_ns != 0)
        {
            const unsigned bucket = (unsigned)std::min<uint64_t>(vblank - last_vblank, 4u);
            ++g_cadence_gs.gaps[bucket];
            g_cadence_gs.max_gap_ns = std::max(g_cadence_gs.max_gap_ns, now - last_ns);
        }
        last_ns = now;
        last_vblank = vblank;
    }

    // registrador privilegiado do GS (PMODE, DISPFB1/2, DISPLAY1/2...): diz qual area e exibida
    void gs_priv_apply(uint32_t phys, uint64_t value, unsigned csr_field)
    {
        gs_vk_flush();
        uint64_t& reg = g_gs_priv[(phys - 0x12000000u) >> 3];
        if ((phys == 0x12000070u || phys == 0x12000090u) && gs_changed_fbp(value, reg))
        {
            // O jogo escreve nos dois DISPFB; so o circuito escolhido conta como troca.
            const uint32_t active = gs_active_dispfb(g_gs_priv[0]);
            if (phys == active)
            {
                const unsigned flip = g_gs_flips.fetch_add(1) + 1;
                if (g_perf_cadence) gs_profile_flip();
                if (g_field_audit)
                {
                    const unsigned fbp = (unsigned)(value & 0x1FFu);
                    const uint64_t display = g_gs_priv[((phys & 0xFFu) + 0x10u) / 8u];
                    FieldFlipAudit& entry = g_field_flips[flip % kFieldAuditCapacity];
                    entry = {};
                    entry.flip = flip;
                    entry.circuit = phys == 0x12000070u ? 1u : 2u;
                    entry.csr_field = csr_field;
                    entry.fbp = fbp;
                    entry.dy = (unsigned)((display >> 12) & 0x7FFu);
                    entry.dby = (unsigned)((value >> 43) & 0x7FFu);
                    entry.vote = (int)g_half_vote[fbp];
                    entry.sprites = g_field_evidence[fbp];
                }
            }

            // O buffer que sai da tela volta a acumular votos para o proximo quadro.
            const unsigned previous = (unsigned)(reg & 0x1FFu);
            g_half_vote[previous] = 0.0f;
            if (g_field_audit) g_field_evidence[previous] = {};
            if (g_field_raster) g_field_raster_state[previous] = {};
            // Trocar DISPFB nao apaga a imagem na VRAM. O jogo pode alternar
            // os mesmos buffers varias vezes sem enviar nenhum desenho.
            // Preserve tambem seus glifos; gs_ui_on_sprite os descarta quando
            // a limpeza opaca inicia o redesenho real daquele framebuffer.
        }

        reg = value;
    }

    // Captura nativa a partir de uma troca do DISPFB ativo. O formato
    // v1 usa a ABI da mesma build, inclusive para o estado parcial da primitiva e da transferencia do GS.
    struct GsReplayHeader
    {
        char magic[8] = {'R', 'O', 'J', 'G', 'S', '1', 'X', '\0'};
        uint32_t version = 1;
        uint32_t state_size = sizeof(GsState);
        uint32_t command_size = sizeof(GsCmd);
        uint32_t vram_size = sizeof(g_vram);
        uint32_t command_count = 0;
        uint32_t crt_int = 0, crt_ffmd = 0;
        uint32_t reserved = 0;
    };
    static_assert(sizeof(GsReplayHeader) == 40 && sizeof(GsCmd) == 24);
    constexpr size_t kGsReplayMaxCommands = 1u << 20;
    constexpr unsigned kGsReplayMinTriangles = 256;
    constexpr unsigned kGsReplayMaxFlips = 120;

    struct GsReplayCapture
    {
        GsReplayHeader header;
        GsState initial_state;
        uint64_t initial_priv[0x100 / 8];
        std::vector<uint32_t> initial_vram;
        std::vector<GsCmd> commands;
        unsigned flips = 0;
    };

    // So o executor dos comandos do GS toca neste objeto (worker ou EE).
    static std::unique_ptr<GsReplayCapture> g_gs_replay_capture;

    static void gs_save_replay(const GsReplayCapture& capture)
    {
        const auto stamp = std::chrono::system_clock::now().time_since_epoch().count();
        const std::string name = "gs_vulkan_1x_" + std::to_string(stamp) + ".bin";
        const std::string pending = name + ".tmp";
        FILE* file = std::fopen(pending.c_str(), "wb");
        if (!file)
        {
            std::fprintf(stderr, "  [GS Vulkan 1x] nao foi possivel criar %s\n", pending.c_str());
            return;
        }

        auto write = [file](const void* data, size_t bytes)
        {
            return std::fwrite(data, 1, bytes, file) == bytes;
        };
        const bool written = write(&capture.header, sizeof(capture.header)) &&
            write(&capture.initial_state, sizeof(capture.initial_state)) &&
            write(capture.initial_priv, sizeof(capture.initial_priv)) &&
            write(capture.initial_vram.data(), sizeof(g_vram)) &&
            write(capture.commands.data(), capture.commands.size() * sizeof(GsCmd)) &&
            write(g_vram, sizeof(g_vram));
        const bool closed = std::fclose(file) == 0;
        if (!written || !closed || std::rename(pending.c_str(), name.c_str()) != 0)
        {
            std::remove(pending.c_str());
            std::fprintf(stderr, "  [GS Vulkan 1x] falha ao gravar %s\n", name.c_str());
            return;
        }

        std::fprintf(stderr, "  [GS Vulkan 1x] captura concluida: %s (%u comandos, %u triangulos, %u trocas)\n",
            name.c_str(), (unsigned)capture.header.command_count, g_gs_replay_triangles, capture.flips);
    }

    inline void gs_apply_command(const GsCmd& c)
    {
        if (c.kind == 0) gs_core_write(c.reg, c.a);
        else if (c.kind == 1) gs_core_image(c.a, c.b);
        else if (c.kind == 3) { /* Legacy synthetic ring HUD: ignored after native VU fix. */ }
        else gs_priv_apply(c.reg, c.a, (unsigned)c.b);
    }

    static void gs_exec_capture(const GsCmd& c)
    {
        const bool flip = c.kind == 2 && c.reg == gs_active_dispfb(g_gs_priv[0]) &&
            gs_changed_fbp(c.a, g_gs_priv[(c.reg - 0x12000000u) >> 3]);

        if (g_gs_replay_capture)
        {
            auto& capture = *g_gs_replay_capture;
            if (capture.commands.size() == kGsReplayMaxCommands)
            {
                std::fprintf(stderr, "  [GS Vulkan 1x] limite da captura atingido. Pressione F9 para tentar novamente.\n");
                g_gs_replay_capture.reset();
                gs_apply_command(c);
                return;
            }

            capture.commands.push_back(c);
            gs_apply_command(c);
            if (!flip)
                return;

            // O jogo tambem troca DISPFB entre intervalos que so escrevem
            // registradores. Mantem a sequencia ate haver rasterizacao util.
            ++capture.flips;
            if (capture.header.crt_int != g_gs_crt_int || capture.header.crt_ffmd != g_gs_crt_ffmd)
                std::fprintf(stderr, "  [GS Vulkan 1x] modo do CRT mudou. Pressione F9 em uma cena estavel.\n");
            else if (g_gs_replay_triangles >= kGsReplayMinTriangles ||
                (capture.flips == kGsReplayMaxFlips && g_gs_replay_triangles != 0))
            {
                capture.header.command_count = (uint32_t)capture.commands.size();
                gs_save_replay(capture);
            }
            else if (capture.flips == kGsReplayMaxFlips)
                std::fprintf(stderr, "  [GS Vulkan 1x] nenhum triangulo em %u trocas. Pressione F9 durante o efeito.\n",
                    capture.flips);
            else
                return;

            g_gs_replay_capture.reset();
            return;
        }

        gs_apply_command(c);
        if (flip && g_gs_replay_request.exchange(false))
        {
            try
            {
                auto capture = std::make_unique<GsReplayCapture>();
                capture->commands.reserve(kGsReplayMaxCommands);
                capture->initial_state = g_st;
                std::memcpy(capture->initial_priv, g_gs_priv, sizeof(g_gs_priv));
                capture->initial_vram.assign(g_vram, g_vram + sizeof(g_vram) / sizeof(g_vram[0]));
                capture->header.crt_int = g_gs_crt_int;
                capture->header.crt_ffmd = g_gs_crt_ffmd;
                g_gs_replay_triangles = 0;
                g_gs_replay_capture = std::move(capture);
                std::fprintf(stderr, "  [GS Vulkan 1x] captura iniciada. Aguardando triangulos...\n");
            }
            catch (const std::bad_alloc&)
            {
                std::fprintf(stderr, "  [GS Vulkan 1x] memoria insuficiente para a captura\n");
            }
        }
    }

    inline void gs_exec(const GsCmd& c)
    {
        if (g_gs_replay_capture_enabled) gs_exec_capture(c);
        else gs_apply_command(c);
    }

    // Roda a CPU em um processo separado, antes de iniciar EE, audio e janela.
    // A medida cobre o lote de comandos, sem leitura do arquivo nem comparacao.
    int gs_replay_cpu_1x(const char* path)
    {
        auto close = [](FILE* stream) { std::fclose(stream); };
        std::unique_ptr<FILE, decltype(close)> file(std::fopen(path, "rb"), close);
        auto read = [&](void* data, size_t bytes)
        {
            return file && std::fread(data, 1, bytes, file.get()) == bytes;
        };
        GsReplayHeader header;
        const GsReplayHeader expected;
        if (!read(&header, sizeof(header)) || std::memcmp(header.magic, expected.magic, 8) != 0 ||
            header.version != expected.version || header.state_size != sizeof(GsState) ||
            header.command_size != sizeof(GsCmd) || header.vram_size != sizeof(g_vram) ||
            header.command_count == 0 || header.command_count > kGsReplayMaxCommands ||
            header.crt_int > 1 || header.crt_ffmd > 1 || header.reserved != 0)
        {
            std::fprintf(stderr, "  [GS Vulkan 1x] cabecalho invalido ou captura de outra build: %s\n", path);
            return 2;
        }

        std::vector<GsCmd> commands(header.command_count);
        std::vector<uint32_t> reference(sizeof(g_vram) / sizeof(g_vram[0]));
        if (!read(&g_st, sizeof(g_st)) || !read(g_gs_priv, sizeof(g_gs_priv)) ||
            !read(g_vram, sizeof(g_vram)) || !read(commands.data(), commands.size() * sizeof(GsCmd)) ||
            !read(reference.data(), sizeof(g_vram)) || std::fgetc(file.get()) != EOF ||
            std::ferror(file.get()))
        {
            std::fprintf(stderr, "  [GS Vulkan 1x] tamanho inesperado da captura: %s\n", path);
            return 2;
        }
        for (const auto& command : commands)
        {
            const bool valid = (command.kind == 0 && command.reg <= 0xFFu) ||
                (command.kind == 1 && command.reg == 0) ||
                (command.kind == 3 && command.reg <= 128u) ||
                (command.kind == 2 && command.reg >= 0x12000000u &&
                    command.reg < 0x12000100u && (command.reg & 7u) == 0);
            if (!valid)
            {
                std::fprintf(stderr, "  [GS Vulkan 1x] comando invalido na captura\n");
                return 2;
            }
        }

        g_gs_crt_int = header.crt_int;
        g_gs_crt_ffmd = header.crt_ffmd;
        gs_init_tables();
#ifdef _WIN32
        if (gs_vk_requested()) g_vk_device.ready();
#endif
        const int repeat = std::clamp(std::getenv("PS2_GS_REPLAY_REPEAT") ? std::atoi(std::getenv("PS2_GS_REPLAY_REPEAT")) : 1, 1, 60);
        const GsState initialState = g_st;
        uint64_t initialPriv[0x100 / 8]; std::memcpy(initialPriv, g_gs_priv, sizeof(initialPriv));
        const std::vector<uint32_t> initialMemory(std::begin(g_vram), std::end(g_vram));
        bool failed = false;
        for (int iteration = 0; iteration < repeat; ++iteration)
        {
        if (iteration != 0) {
            g_st = initialState;
            std::memcpy(g_gs_priv, initialPriv, sizeof(initialPriv));
            std::memcpy(g_vram, initialMemory.data(), sizeof(g_vram));
            std::fill(std::begin(g_clut_prov), std::end(g_clut_prov), 0u);
            g_ui_native_glyphs.clear();
        }
        const uint64_t start = prof_now();
        for (const auto& command : commands)
            gs_apply_command(command);
        gs_vk_flush();
        const double ms = (prof_now() - start) / 1e6;
#ifdef _WIN32
        std::fprintf(stderr, "  [Vulkan] lotes=%llu triangulos_GPU=%llu triangulos_CPU=%llu preparo_ms=%.3f\n",
            (unsigned long long)g_vk_batches, (unsigned long long)g_vk_rendered, (unsigned long long)g_vk_cpu, g_vk_prepare_ns / 1e6);
#endif

        size_t differences = 0, first = 0;
        for (size_t i = 0; i < reference.size(); ++i)
        {
            if (g_vram[i] != reference[i] && differences++ == 0)
                first = i;
        }
        if (differences)
            std::fprintf(stderr, "  [GS Vulkan 1x] primeira divergencia: VRAM+0x%06X: CPU=%08X esperado=%08X\n",
                (unsigned)(first * sizeof(uint32_t)), (unsigned)g_vram[first], (unsigned)reference[first]);
        std::fprintf(stderr, "  [GS Vulkan 1x] replay CPU: comandos=%u, tempo_ms=%.3f, palavras_VRAM_diferentes=%zu\n",
            (unsigned)header.command_count, ms, differences);
        failed |= differences != 0;
        }
        return failed ? 1 : 0;
    }

    void gs_worker_main()
    {
        uint64_t t = 0;
        unsigned idle = 0;

        while (!g_gsq_stop.load(std::memory_order_relaxed))
        {
            const uint64_t h = g_gsq_head.load(std::memory_order_acquire);

            if (t == h)
            {

                if (g_gsq_wake_on_work)
                {
                    std::unique_lock<std::mutex> lk(g_gsq_work_mu);
                    g_gsq_worker_waiting.store(true, std::memory_order_seq_cst);
                    g_gsq_work_cv.wait(lk, [t] {
                        return g_gsq_stop.load(std::memory_order_relaxed) ||
                            g_gsq_head.load(std::memory_order_seq_cst) != t;
                    });
                    g_gsq_worker_waiting.store(false, std::memory_order_seq_cst);
                    if (g_perf_gs) g_gsq_work_wakes.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }

                if (++idle < 4000u) std::this_thread::yield();
                else std::this_thread::sleep_for(std::chrono::microseconds(100));

                continue;
            }

            idle = 0;
            const uint64_t t0 = prof_now();

            while (t < h)
            {
                const GsCmd& cmd = g_gsq[t & (kGsQSize - 1)];
                const bool signal_priv = g_gsq_limit_flips && cmd.kind == 2;
                gs_exec(cmd);
                ++t;

                if (signal_priv)
                {
                    {
                        std::lock_guard<std::mutex> lk(g_gsq_flip_mu);
                        g_gsq_priv_done = t;
                    }
                    g_gsq_flip_cv.notify_one();
                }
                if (signal_priv || (t & 255u) == 0)
                {
                    g_gsq_tail.store(t, std::memory_order_release);
                    if (g_gsq_stop.load(std::memory_order_relaxed))
                        return;
                }
            }

            g_gsq_tail.store(t, std::memory_order_release);
            g_gsq_busy_ns.fetch_add(prof_now() - t0, std::memory_order_relaxed);
        }
    }

    void gs_q_start()
    {
        static std::once_flag once;
        std::call_once(once, []
        {
            // Junta o worker antes da destruicao dos mutexes e da fila.
            static std::thread worker(gs_worker_main);
            std::atexit([]
            {
                // deixa a fila esvaziar um pouco antes de encerrar (evita derrubar a thread no meio de um desenho)
                for (int i = 0; i < 200 && g_gsq_tail.load() != g_gsq_head.load(); i++)
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));

                {
                    std::lock_guard<std::mutex> lk(g_gsq_work_mu);
                    g_gsq_stop.store(true, std::memory_order_relaxed);
                }
                g_gsq_work_cv.notify_one();
                worker.join();
            });
            std::fprintf(stderr, "  [gs] GS assincrono ligado (thread propria; fila de %u comandos)\n", kGsQSize);
        });
    }

    static void gs_q_flush_batch()
    {
        if (g_gsq_batch && g_gs_async &&
            g_gsq_write_head != g_gsq_head.load(std::memory_order_relaxed))
            gs_q_publish(g_gsq_write_head);
    }

    inline void gs_q_push(uint32_t kind, uint32_t reg, uint64_t a, uint64_t b)
    {
        if (!g_gsq_start_fast || !g_gsq_started)
        {
            gs_q_start();
            g_gsq_started = true;
        }
        // Escritas privilegiadas e esperas pela troca anterior precisam
        // enxergar todos os comandos produzidos ate aqui.
        if (kind == 2) gs_q_flush_batch();
        const bool flip = g_gsq_limit_flips && kind == 2 && gs_queue_wait_previous_flip(reg, a);
        const uint64_t h = g_gsq_use_local_cursor ? g_gsq_write_head : g_gsq_head.load(std::memory_order_relaxed);
        const uint64_t tail = g_gsq_use_local_cursor ? g_gsq_safe_tail : g_gsq_tail.load(std::memory_order_acquire);

        if (h - tail >= kGsQSize)
        {
            uint64_t current_tail = g_gsq_tail.load(std::memory_order_acquire);
            if (h - current_tail >= kGsQSize)
            {
                gs_q_flush_batch();
                const uint64_t wait_start = prof_now();
                g_gsq_full_wait_count.fetch_add(1, std::memory_order_relaxed);
                do
                {
                    std::this_thread::yield();
                    current_tail = g_gsq_tail.load(std::memory_order_acquire);
                }
                while (h - current_tail >= kGsQSize);
                g_gsq_full_wait_ns.fetch_add(prof_now() - wait_start, std::memory_order_relaxed);
            }
            // Um tail antigo so reduz o espaco estimado. Releia com
            // acquire antes de reutilizar qualquer slot ainda ocupado.
            if (g_gsq_use_local_cursor) g_gsq_safe_tail = current_tail;
        }

        g_gsq[h & (kGsQSize - 1)] = GsCmd{ a, b, kind, reg };
        if (flip) g_gsq_last_flip_end = h + 1;
        if (g_gsq_use_local_cursor)
            g_gsq_write_head = h + 1;
        if (g_gsq_batch && kind != 2 && (g_gsq_write_head % kGsPublishBatch) != 0)
            return;
        gs_q_publish(h + 1);
    }

    // Espera o GS terminar tudo que ja foi enfileirado (para quem precisa ler a VRAM / o estado do GS).
    bool gs_q_idle()
    {
        return !g_gs_async || g_gsq_tail.load(std::memory_order_acquire) ==
            g_gsq_head.load(std::memory_order_acquire);
    }

    void gs_q_drain()
    {
        gs_q_flush_batch();
        while (!gs_q_idle())
            std::this_thread::yield();
        gs_vk_flush();
    }

    uint32_t dma_addr(uint32_t a); // implementada na secao DMA, mais abaixo.

    // Teste H: buffer de um download do GS. Preparado somente se o hardware tiver
    // enviado TRXDIR=1 (VRAM local -> host) e se o formato de pixel for conhecido.
    struct GsDownload
    {
        uint64_t generation = ~0ull;
        size_t cursor = 0;
        std::vector<uint8_t> bytes;
        uint32_t psm = 0;
        uint32_t width = 0, height = 0;
    };

    GsDownload g_gs_download;

    // Empacota os pixels na ordem de transferencia de imagem do GS, little-endian.
    // O retorno da VRAM NAO transforma os pixels em RGB: preserva o formato PSM.
    bool gs_prepare_download()
    {
        const uint64_t generation = g_st.trx_generation;
        if (g_gs_download.generation == generation)
            return !g_gs_download.bytes.empty();

        g_gs_download.generation = generation;
        g_gs_download.cursor = 0;
        g_gs_download.bytes.clear();

        if ((g_st.trxdir & 3u) != 1u)
            return false; // Sem TRXDIR=1, nao existe download GS confirmado.

        const uint64_t blt = g_st.bitbltbuf;
        const uint32_t bp = (uint32_t)(blt & 0x3FFFu);
        const uint32_t bw = (uint32_t)((blt >> 16) & 0x3Fu);
        const uint32_t psm = (uint32_t)((blt >> 24) & 0x3Fu);
        const uint32_t sx = (uint32_t)(g_st.trxpos & 0x7FFu);
        const uint32_t sy = (uint32_t)((g_st.trxpos >> 16) & 0x7FFu);
        const uint32_t w = (uint32_t)(g_st.trxreg & 0xFFFu);
        const uint32_t h = (uint32_t)((g_st.trxreg >> 32) & 0xFFFu);
        const uint32_t dir = (uint32_t)((g_st.trxpos >> 59) & 3u);

        // Implementamos apenas formatos e transferencia em ordem direta.
        // Parametros desconhecidos nao podem alterar a RAM do jogo.
        unsigned bits = 0;
        switch (psm)
        {
            case 0x00: case 0x30: bits = 32; break;
            case 0x01: case 0x31: bits = 24; break;
            case 0x02: case 0x0A: case 0x32: case 0x3A: bits = 16; break;
            case 0x13: case 0x1B: bits = 8; break;
            case 0x14: case 0x24: case 0x2C: bits = 4; break;
            default: return false;
        }

        const uint64_t pixels = (uint64_t)w * h;
        const uint64_t byte_count = (pixels * bits + 7u) / 8u;
        if (bw == 0 || w == 0 || h == 0 || dir != 0 || byte_count == 0 || byte_count > 4u * 1024u * 1024u)
            return false;

        // QWC e DMA sao alinhados em 16 bytes; apenas o ultimo QW e preenchido.
        g_gs_download.bytes.assign((size_t)((byte_count + 15u) & ~15ull), 0);
        g_gs_download.psm = psm;
        g_gs_download.width = w;
        g_gs_download.height = h;

        const uint64_t mask = bits == 32 ? 0xFFFFFFFFull : ((1ull << bits) - 1u);
        for (uint64_t i = 0; i < pixels; ++i)
        {
            const uint32_t x = sx + (uint32_t)(i % w);
            const uint32_t y = sy + (uint32_t)(i / w);
            const uint64_t pix = (uint64_t)gs_read_px(psm, bp, bw, x, y) & mask;
            const uint64_t bit = i * bits;
            // bits pode ser 4/8/16/24/32; todos sao multiplos de 4.
            for (unsigned b = 0; b < bits; b += 4)
            {
                const uint64_t p = bit + b;
                const size_t at = (size_t)(p >> 3);
                const unsigned shift = (unsigned)(p & 7u);
                g_gs_download.bytes[at] |= (uint8_t)(((pix >> b) & 0xFu) << shift);
            }
        }
        return true;
    }

    // Copia um bloco da transferencia VRAM -> EE RAM. Retorna falso quando nao
    // ha download valido ou quando a FIFO nao possui dados suficientes.
    bool gs_download_to_ram(Ctx& c, uint32_t madr, uint32_t qwc)
    {
        // Espera o GS terminar os comandos enfileirados ANTES de ler a VRAM.
        gs_q_drain();
        if (!gs_prepare_download())
            return false;

        const uint64_t n = (uint64_t)qwc * 16u;
        if (n > g_gs_download.bytes.size() - g_gs_download.cursor)
            return false;

        uint8_t* dst = host_ptr(c, dma_addr(madr), (size_t)n);
        std::memcpy(dst, g_gs_download.bytes.data() + g_gs_download.cursor, (size_t)n);
        g_gs_download.cursor += (size_t)n;
        return true;
    }

    void gs_write(unsigned reg, uint64_t data)
    {
        reg &= 0xFF;
        g_gs.reg[reg]++;

        if (g_gs_async) gs_q_push(0, reg, data, 0);
        else gs_exec({ data, 0, 0, reg });

        if (reg == 0x00)
            g_gs.prim[data & 7]++;

        if (!g_giflog || g_giflog_budget == 0)
            return;

        g_giflog_budget--;

        char extra[160] = "";

        switch (reg)
        {
            case 0x4C: case 0x4D:
                std::snprintf(extra, sizeof(extra), "  FBP=%u FBW=%u PSM=0x%02X", (unsigned)(data & 0x1FF),
                    (unsigned)((data >> 16) & 0x3F), (unsigned)((data >> 24) & 0x3F));
                break;
            case 0x06: case 0x07:
                std::snprintf(extra, sizeof(extra), "  TBP0=%u TBW=%u PSM=0x%02X TW=%u TH=%u",
                    (unsigned)(data & 0x3FFF), (unsigned)((data >> 14) & 0x3F), (unsigned)((data >> 20) & 0x3F),
                    (unsigned)((data >> 26) & 0xF), (unsigned)((data >> 30) & 0xF));
                break;
            case 0x50:
                std::snprintf(extra, sizeof(extra), "  DBP=%u DBW=%u DPSM=0x%02X", (unsigned)((data >> 32) & 0x3FFF),
                    (unsigned)((data >> 48) & 0x3F), (unsigned)((data >> 56) & 0x3F));
                break;
            case 0x52:
                std::snprintf(extra, sizeof(extra), "  RRW=%u RRH=%u", (unsigned)(data & 0xFFF),
                    (unsigned)((data >> 32) & 0xFFF));
                break;
            case 0x00:
                std::snprintf(extra, sizeof(extra), "  tipo=%u", (unsigned)(data & 7));
                break;
            default:
                break;
        }

        std::fprintf(stderr, "  [gs] %-10s 0x%016llX%s\n", gs_reg_name(reg), (unsigned long long)data, extra);
    }

    struct GifDecoder
    {
        bool active = false;
        unsigned flg = 0, nreg = 0, reg_idx = 0;
        uint64_t loops_left = 0, image_left = 0, regs = 0;
        uint32_t q = 0x3F800000u;

        void qword(uint64_t lo, uint64_t hi)
        {
            if (image_left != 0)
            {
                image_left--;
                g_gs.image_qw++;
                const bool sampled = g_prof_image_qword &&
                    ((g_gs.image_qw - 1u) & (g_prof_image_stride - 1u)) == 0;
                const uint64_t start = sampled ? prof_now() : 0;

                if (g_gs_async) gs_q_push(1, 0, lo, hi);
                else gs_exec({ lo, hi, 1, 0 });

                if (sampled)
                {
                    g_prof_img_ns += prof_now() - start;
                    ++g_prof_image_samples;
                }
                return;
            }

            if (!active)
            {
                // GIF resets its internal Q at every tag, including IMAGE and
                // REGLIST tags. PACKED RGBA must not reuse the previous draw's Q.
                q = 0x3F800000u;
                uint64_t nloop = lo & 0x7FFF;
                bool pre = ((lo >> 46) & 1) != 0;
                uint64_t prim = (lo >> 47) & 0x7FF;
                flg = (unsigned)((lo >> 58) & 3);
                nreg = (unsigned)((lo >> 60) & 0xF);

                if (nreg == 0)
                    nreg = 16;

                regs = hi;

                if (g_giflog && g_gif_log != 0)
                {
                    g_gif_log--;
                    std::fprintf(stderr, "  [gif] tag lo=0x%016llX hi=0x%016llX nloop=%llu eop=%u pre=%u prim=0x%llX flg=%u nreg=%u\n",
                        (unsigned long long)lo, (unsigned long long)hi, (unsigned long long)nloop,
                        (unsigned)((lo >> 15) & 1), pre ? 1u : 0u, (unsigned long long)prim, flg, nreg);
                }

                if (pre && flg == 0)
                    gs_write(0x00, prim);

                if (flg == 0)
                    g_gs.tags_packed++;
                else if (flg == 1)
                    g_gs.tags_reglist++;
                else
                    g_gs.tags_image++;

                if (nloop == 0)
                    return;

                if (flg >= 2)
                {
                    image_left = nloop;
                    return;
                }

                active = true;
                loops_left = nloop;
                reg_idx = 0;

                if (flg == 1)
                    loops_left = (nloop * nreg + 1) / 2;   // REGLIST: dois registradores de 64 bits por qword

                return;
            }

            if (flg == 0)
            {
                unsigned r = (unsigned)((regs >> (reg_idx * 4)) & 0xF);

                switch (r)
                {
                    case 0x0: gs_write(0x00, lo & 0x7FF); break;
                    case 0x1:
                        gs_write(0x01, (lo & 0xFF) | (((lo >> 32) & 0xFF) << 8) | ((hi & 0xFF) << 16)
                            | (((hi >> 32) & 0xFF) << 24) | ((uint64_t)q << 32));
                        break;
                    case 0x2:
                        q = (uint32_t)hi;
                        gs_write(0x02, lo);
                        break;
                    case 0x3: gs_write(0x03, (lo & 0x3FFF) | (((lo >> 32) & 0x3FFF) << 16)); break;
                    case 0x4:
                        gs_write(((hi >> 47) & 1) ? 0x0C : 0x04, (lo & 0xFFFF) | (((lo >> 32) & 0xFFFF) << 16)
                            | (((hi >> 4) & 0xFFFFFF) << 32) | (((hi >> 36) & 0xFF) << 56));
                        break;
                    case 0x5:
                        gs_write(((hi >> 47) & 1) ? 0x0D : 0x05, (lo & 0xFFFF) | (((lo >> 32) & 0xFFFF) << 16)
                            | ((hi & 0xFFFFFFFFull) << 32));
                        break;
                    case 0xA: gs_write(0x0A, ((hi >> 36) & 0xFF) << 56); break;
                    case 0xE: gs_write((unsigned)(hi & 0xFF), lo); break;
                    default: break;   // 0xF = NOP; 6..9, B..D nao usados
                }

                if (++reg_idx == nreg)
                {
                    reg_idx = 0;

                    if (--loops_left == 0)
                        active = false;
                }
            }
            else
            {
                for (int k = 0; k < 2; k++)
                {
                    unsigned r = (unsigned)((regs >> (reg_idx * 4)) & 0xF);
                    gs_write(r, k == 0 ? lo : hi);

                    if (++reg_idx == nreg)
                        reg_idx = 0;
                }

                if (--loops_left == 0)
                    active = false;
            }
        }
    };

    GifDecoder g_gif;

    // Diagnostico somente de leitura: registra a origem REAL de cada palavra
    // que o DMA entrega ao VIF1 (tag superior ou payload).
    // Nao modifica TTE, a ordem do DMA, o VIF ou o GS.
    struct DmaProbeOrigin
    {
        uint32_t tag_addr = 0, word_addr = 0, chcr = 0;
        unsigned tag_id = 0, lane = 0;
        bool from_tag = false;
    };
    static const bool g_vif1_direct_qword = [] {
        const char* value = std::getenv("PS2_VIF1_DIRECT_QWORD");
        return !value || std::strcmp(value, "1") == 0;
    }();
    static const bool g_vif1_direct_run = [] {
        const char* value = std::getenv("PS2_VIF1_DIRECT_RUN");
        return !value || std::strcmp(value, "1") == 0;
    }();
    static uint64_t g_vif1_direct_runs = 0;
    static uint64_t g_vif1_direct_fast_qwords = 0;

    const bool g_dma_probe = std::getenv("PS2_DMA_PROBE") != nullptr;
    const bool g_vif1_tag_nop_fast = [] {
        const char* value = std::getenv("PS2_VIF1_TAG_NOP_FAST");
        return !value || std::strcmp(value, "1") == 0;
    }();
    uint64_t g_vif1_tag_nop_pairs = 0;

    DmaProbeOrigin g_dma_probe_origin = {};

    // VIF0 / VIF1: interpretam os VIFcodes. Levam micro codigo (MPG) e dados (UNPACK) ao VU0/VU1, disparam
    // programas (MSCAL/MSCALF/MSCNT) e, no VIF1, repassam DIRECT/DIRECTHL ao GIF (PATH2).
    struct VifDecoder
    {
        int unit;
        explicit VifDecoder(int u = 0) : unit(u) {}
        Ctx* cx = nullptr;                       // VU0 roda no contexto do EE que disparou o DMA
        uint32_t cl = 1, wl = 1, mode = 0, mask = 0;
        uint32_t row[4] = {}, col[4] = {};
        uint32_t base = 0, ofst = 0, tops = 0, itops = 0;
        bool db = false, dbf = false;
        uint64_t skip_words = 0, direct_qw = 0, mpg_left = 0, up_left = 0;
        uint32_t mpg_addr = 0, up_fmt = 0, up_addr = 0, up_num = 0;
        bool up_usn = false, up_mask = false;
        int reg_kind = 0;
        unsigned reg_left = 0, reg_pos = 0;
        uint32_t dbuf[4] = {};
        unsigned dn = 0;
        std::vector<uint32_t> up_words;
        uint32_t hist[32] = {};     // ultimos VIFcodes decodificados (diagnostico de dessincronia)
        unsigned hist_n = 0;
        struct ProbeCommand { uint32_t code = 0; DmaProbeOrigin src = {}; };
        ProbeCommand probe_hist[16] = {};
        uint64_t probe_hist_n = 0;
        int bad_log = 8;

        void probe_dump(const char* reason, uint32_t code)
        {
            if (unit != 1 || !g_dma_probe)
                return;

            const DmaProbeOrigin& o = g_dma_probe_origin;
            std::fprintf(stderr,
                "  [DMA PROBE %s] VIF=0x%08X origem=%s addr=0x%08X tag=0x%08X id=%u lane=%u CHCR=0x%X TTE=%u pendentes(DIRECT=%llu MPG=%llu UNPACK=%llu)\n",
                reason, code, o.from_tag ? "TAG" : "PAYLOAD", o.word_addr,
                o.tag_addr, o.tag_id, o.lane, o.chcr, (o.chcr & 0x40u) != 0,
                (unsigned long long)direct_qw, (unsigned long long)mpg_left,
                (unsigned long long)up_left);

            // Os ultimos 12 comandos, com o endereco da palavra que os originou.
            const unsigned count = (unsigned)std::min<uint64_t>(12, probe_hist_n);
            for (unsigned back = count; back > 0; --back)
            {
                const ProbeCommand& e = probe_hist[(probe_hist_n - back) & 15u];
                std::fprintf(stderr,
                    "    [DMA PROBE HIST] VIF=%08X origem=%s addr=%08X tag=%08X id=%u CHCR=%X\n",
                    e.code, e.src.from_tag ? "TAG" : "PAYLOAD", e.src.word_addr,
                    e.src.tag_addr, e.src.tag_id, e.src.chcr);
            }
        }
        bool desync = false;        // VIFcode impossivel visto: o resto da cadeia DMA e lixo e e descartado

        // PS2_VIF_DISCARD=1 (experimental): descarta o resto da cadeia DMA apos um VIFcode invalido e recomeca no proximo DMA.
        // Por padrao NAO: o hardware trata um VIFcode desconhecido como NOP e segue (as palavras seguintes costumam ser codigos
        // validos, como ITOP), e descartar a cadeia apagava cenas inteiras da batalha.
        static bool resync_enabled()
        {
            static const bool on = std::getenv("PS2_VIF_DISCARD") != nullptr;
            return on;
        }

        // Chamado no inicio de cada DMA em cadeia: um pacote termina sempre em fronteira de comando. Se sobrou dado
        // pendente, a contagem de palavras de algum comando esta errada: avisa e recomeca em um VIFcode (so com PS2_VIF_DISCARD=1).
        void boundary(const char* quem)
        {
            const bool left = direct_qw != 0 || mpg_left != 0 || up_left != 0 || reg_left != 0 || skip_words != 0 || dn != 0;

            if (!left && !desync)
                return;

            if (bad_log > 0)
            {
                bad_log--;
                std::fprintf(stderr, "  [VIF%d] %s: pacote anterior terminou no meio de um comando (direct=%llu mpg=%llu unpack=%llu reg=%u skip=%llu dn=%u; unpack fmt=%u num=%u addr=0x%X) - ultimos VIFcodes:",
                    unit, quem, (unsigned long long)direct_qw, (unsigned long long)mpg_left, (unsigned long long)up_left, reg_left,
                    (unsigned long long)skip_words, dn, up_fmt, up_num, up_addr);

                for (unsigned i = 0; i < 12 && i < hist_n; i++)
                    std::fprintf(stderr, " %08X", hist[(hist_n - 1 - i) & 31u]);

                std::fprintf(stderr, "\n");
            }

            if (resync_enabled())
            {
                desync = false;
                direct_qw = mpg_left = up_left = skip_words = 0;
                reg_left = 0;
                dn = 0;
                up_words.clear();
            }
        }

        static unsigned vector_bits(unsigned fmt)
        {
            static const unsigned bits[16] = { 32, 16, 8, 0, 64, 32, 16, 0, 96, 48, 24, 0, 128, 64, 32, 16 };
            return bits[fmt & 15];
        }

        void unpack_commit()
        {
            static const unsigned nc[16] = { 1, 1, 1, 0, 2, 2, 2, 0, 3, 3, 3, 0, 4, 4, 4, 4 };
            static const unsigned cb[16] = { 32, 16, 8, 0, 32, 16, 8, 0, 32, 16, 8, 0, 32, 16, 8, 5 };
            const unsigned fmt = up_fmt & 15u;
            uint8_t* data = unit ? g_vu1_data : g_vu0_data;
            const uint32_t dmask = unit ? 0x3FFu : 0xFFu;

            if (nc[fmt] == 0)
                return;

            const bool profile = g_perf_vif1 && unit == 1;
            const uint64_t start = profile ? prof_now() : 0;
            const bool fast = g_vif1_unpack_fast && unit == 1;
            uint64_t bitpos = 0;
            const uint64_t total = (uint64_t)up_words.size() * 32u;

            auto get = [&](unsigned n) -> uint32_t
            {
                if (fast)
                {
                    // Cada UNPACK usa uma largura fixa de 8, 16 ou 32 bits.
                    // Todos dividem 32 e bitpos comeca em zero: nenhum campo cruza palavras.
                    const uint32_t word = up_words[(size_t)(bitpos >> 5)];
                    const unsigned shift = (unsigned)(bitpos & 31u);
                    bitpos += n;
                    return (word >> shift) & (0xFFFFFFFFu >> (32 - n));
                }

                uint32_t v = 0;

                for (unsigned i = 0; i < n; i++)
                {
                    const uint64_t b = bitpos + i;
                    v |= ((up_words[(size_t)(b >> 5)] >> (b & 31u)) & 1u) << i;
                }

                bitpos += n;
                return v;
            };

            for (uint32_t i = 0; i < up_num; i++)
            {
                const unsigned need = fmt == 15 ? 16u : nc[fmt] * cb[fmt];

                if (bitpos + need > total)
                    break;

                uint32_t lane[4] = { 0, 0, 0, 0 };

                if (fmt == 15)
                {
                    const uint32_t v = get(16);
                    lane[0] = (v & 31u) << 3; lane[1] = ((v >> 5) & 31u) << 3; lane[2] = ((v >> 10) & 31u) << 3; lane[3] = ((v >> 15) & 1u) << 7;
                }
                else
                {
                    uint32_t comp[4] = { 0, 0, 0, 0 };

                    for (unsigned k = 0; k < nc[fmt]; k++)
                    {
                        uint32_t v = get(cb[fmt]);

                        if (!up_usn && cb[fmt] < 32 && (v & (1u << (cb[fmt] - 1))))
                            v |= ~0u << cb[fmt];

                        comp[k] = v;
                    }

                    if (nc[fmt] == 1) { lane[0] = lane[1] = lane[2] = lane[3] = comp[0]; }
                    else { lane[0] = comp[0]; lane[1] = comp[1]; lane[2] = comp[2]; lane[3] = comp[3]; }
                }

                const uint32_t slot = (cl >= wl && wl != 0) ? (i / wl) * cl + (i % wl) : i;
                uint32_t* dst = reinterpret_cast<uint32_t*>(data) + (((up_addr + slot) & dmask) * 4u);
                const unsigned mrow = i < 3 ? i : 3;
                const uint32_t old_x = dst[0];  // apenas para diagnosticar sobrescritas do cabecalho

                for (int l = 0; l < 4; l++)
                {
                    const unsigned code = up_mask ? ((mask >> ((mrow * 4 + (unsigned)l) * 2)) & 3u) : 0u;

                    if (code == 3)
                        continue;

                    uint32_t v = lane[l];

                    if (code == 0)
                    {
                        if (mode == 1) v += row[l];
                        else if (mode == 2) { v += row[l]; row[l] = v; }
                    }
                    else if (code == 1) v = row[l];
                    else v = col[mrow];

                    dst[l] = v;
                }

                // PS2_VU_HEADER_TRACE=1: rastreia a origem do contador VI03 da VU1.
                // Estes sao os dois quadwords observados nos logs (TOP=0x0FE/0x27F + 186).
                // Somente instrumentacao: nao altera memoria nem o fluxo VIF.
                if (unit == 1 && g_vu_header_trace)
                {
                    const uint32_t a = (up_addr + slot) & dmask;
                    if (a == 0x1B8u || a == 0x339u)
                    {
                        static uint64_t header_writes = 0;
                        ++header_writes;
                        if (header_writes <= 64 || (header_writes & 4095u) == 0)
                            std::fprintf(stderr,
                                "  [VU1 WRITE VIF] n=%llu D[%03X] x=%08X (antes=%08X) fmt=%u i=%u/%u base=%03X slot=%u cl=%u wl=%u mode=%u mask=%d palavra_inicial=%08X\n",
                                (unsigned long long)header_writes, a, dst[0], old_x,
                                fmt, i, up_num, up_addr, slot, cl, wl, mode, (int)up_mask,
                                up_words.empty() ? 0u : up_words.front());
                    }
                }

                // PS2_VU1WATCH=n: mostra todo UNPACK que grava o quadword n da memoria de dados do VU1
                static const int watch = std::getenv("PS2_VU1WATCH") ? (int)std::strtol(std::getenv("PS2_VU1WATCH"), nullptr, 0) : -1;

                if (unit == 1 && watch >= 0 && (((up_addr + slot) & dmask) == (uint32_t)watch))
                    std::fprintf(stderr, "  [VU1WATCH] UNPACK fmt=%u usn=%d addr=0x%X i=%u/%u CL=%u WL=%u MODE=%u mask=%d(0x%08X) TOPS=0x%X -> [0x%X] = %08X %08X %08X %08X\n",
                        fmt, (int)up_usn, up_addr, i, up_num, cl, wl, mode, (int)up_mask, mask, tops, watch, dst[0], dst[1], dst[2], dst[3]);
            }

            if (profile)
            {
                ++g_prof_unpack_calls;
                g_prof_unpack_words += up_words.size();
            }
            up_words.clear();
            if (profile)
                g_prof_unpack_ns += prof_now() - start;
        }

        void start_program(uint32_t byte_addr, bool cont)
        {
            if (unit == 0)
            {
                if (cx != nullptr)
                {
                    vu0_run(*cx, cont ? g_vu0_pc_get() * 8u : byte_addr);
                    cx->vi[29] &= ~1u;
                }

                return;
            }

            g_vif1_itop = itops;
            g_vif1_top = tops;

            if (db)
            {
                dbf = !dbf;
                tops = base + (dbf ? ofst : 0u);
            }

            if (!cont)
            {
                if (unit == 1 && g_dma_probe && byte_addr >= 0x4000u)
                {
                    static unsigned suspicious_mscal = 0;
                    if (suspicious_mscal++ < 8)
                        probe_dump("MSCAL_FORA_DA_VU1", (byte_addr / 8u) | 0x14000000u);
                }
                static uint32_t seen1[32];
                static unsigned seen1_n = 0;
                bool is_new = true;

                for (unsigned i = 0; i < seen1_n; i++)
                    if (seen1[i] == byte_addr) { is_new = false; break; }

                if (is_new && seen1_n < 32)
                {
                    seen1[seen1_n++] = byte_addr;
                    std::fprintf(stderr, "  [VU1] MSCAL micro=0x%04X (TOP=0x%X ITOP=0x%X)\n", byte_addr, g_vif1_top, g_vif1_itop);
                }
            }

            vu1_run(cont ? g_vu1_pc_get() * 8u : byte_addr);
            g_gs.mscal++;
        }

        // A mesma tag pode carregar duas palavras de payload. So
        // dispense a decodificacao quando ambas seriam comandos NOP.
        bool try_tag_nops(uint64_t tag)
        {
            constexpr uint64_t command_bits = 0x7F0000007F000000ull;
            if ((tag & command_bits) || (direct_qw | mpg_left | up_left | reg_left | skip_words) ||
                g_dma_probe || (g_giflog && g_vif_log != 0))
                return false;

            hist[hist_n++ & 31u] = (uint32_t)tag;
            hist[hist_n++ & 31u] = (uint32_t)(tag >> 32);
            return true;
        }

        void word(uint32_t w)
        {
            if (direct_qw != 0)
            {
                dbuf[dn++] = w;

                if (dn == 4)
                {
                    dn = 0;
                    direct_qw--;
                    g_gs.direct_qw++;
                    g_gif.qword((uint64_t)dbuf[0] | ((uint64_t)dbuf[1] << 32), (uint64_t)dbuf[2] | ((uint64_t)dbuf[3] << 32));
                }

                return;
            }

            if (mpg_left != 0)
            {
                std::memcpy((unit ? g_vu1_micro : g_vu0_micro) + (mpg_addr & (unit ? 0x3FFFu : 0xFFFu)), &w, 4);

                if (unit) vu1_micro_written(); else g_vu0_dirty = true;
                mpg_addr += 4;
                mpg_left--;
                return;
            }

            if (up_left != 0)
            {
                up_words.push_back(w);

                if (--up_left == 0)
                    unpack_commit();

                return;
            }

            if (reg_left != 0)
            {
                if (reg_kind == 1) row[reg_pos] = w;
                else if (reg_kind == 2) col[reg_pos] = w;
                else mask = w;

                reg_pos++;
                reg_left--;
                return;
            }

            if (skip_words != 0)
            {
                skip_words--;
                return;
            }

            const unsigned cmd = (w >> 24) & 0x7F;
            const unsigned num = (w >> 16) & 0xFF;
            const unsigned imm = w & 0xFFFF;

            hist[hist_n++ & 31u] = w;

            if (unit == 1 && g_dma_probe)
            {
                ProbeCommand& entry = probe_hist[probe_hist_n++ & 15u];
                entry.code = w;
                entry.src = g_dma_probe_origin;
            }

            if (cmd < 0x60)
            {
                static const unsigned char ok[0x60] = { 1,1,1,1,1,1,1,1, 0,0,0,0,0,0,0,0, 1,1,0,1,1,1,0,1, 0,0,0,0,0,0,0,0,
                    1,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 1,1,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
                    0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0 };
                const unsigned char good = ok[cmd] || cmd == 0x4A || cmd == 0x50 || cmd == 0x51;

                if (!good)
                {
                    if (unit == 1 && g_dma_probe)
                    {
                        static unsigned invalid_count = 0;
                        if (invalid_count++ < 8)
                            probe_dump("INVALIDO", w);
                    }
                    if (resync_enabled())
                        desync = true;

                    if (bad_log > 0)
                    {
                        bad_log--;
                        std::fprintf(stderr, "  [VIF%d] VIFcode invalido 0x%08X (cmd=0x%02X): ignorado (tratado como NOP, como no hardware). ultimos:", unit, w, cmd);

                        for (unsigned i = 1; i < 12 && i <= hist_n - 1; i++)
                            std::fprintf(stderr, " %08X", hist[(hist_n - 1 - i) & 31u]);

                        std::fprintf(stderr, "\n");
                    }
                }
            }

            if (unit == 1 && g_giflog && g_vif_log != 0)
            {
                g_vif_log--;
                std::fprintf(stderr, "  [vif] codigo=0x%08X cmd=0x%02X num=%u imm=0x%04X (cl=%u wl=%u)\n", w, cmd, num, imm, cl, wl);
            }

            if (cmd >= 0x60)
            {
                // Perfil de batalha: quando WL=0 e CL>0, este caminho
                // evita consumir os proximos VIFcodes como dados de UNPACK.
                // PS2_VIF_WL0_FIX=0 restaura o caminho anterior.
                if (unit == 1 && cl != 0 && wl == 0)
                {
                    static uint64_t wl0_count = 0;
                    ++wl0_count;
                    const bool wl0_fix = g_vif_wl0_fix;

                    // Limita o volume de logs em cenas que usam muitos UNPACK.
                    if (wl0_count <= 16 || (wl0_count & 4095u) == 0)
                        std::fprintf(stderr,
                            "  [VIF1 WL0] n=%llu cmd=%02X num=%u imm=%04X CL=%u WL=%u acao=%s\n",
                            (unsigned long long)wl0_count, cmd, num, imm, cl, wl,
                            wl0_fix ? "NOP SEM PAYLOAD" : "LEGADO (CONSUME PAYLOAD)");

                    if (wl0_fix)
                    {
                        // NÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¾ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¾ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¾ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¾ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â‚¬Å¾Ã‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Â¦Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â£o cria uma transferencia UNPACK nem consome as
                        // proximas palavras como payload; o proximo word()
                        // podera interpretar corretamente o VIFcode seguinte.
                        return;
                    }
                }

                if (unit == 1)
                    g_gs.unpack_cmds++;

                uint64_t n = num == 0 ? 256 : num;
                uint64_t in_vectors = n;

                if (cl < wl && wl != 0)
                    in_vectors = (n / wl) * cl + std::min<uint64_t>(n % wl, cl);

                up_fmt = cmd & 15u;
                up_usn = (imm & 0x4000u) != 0;
                up_mask = (cmd & 0x10u) != 0;
                up_addr = (imm & 0x3FFu) + ((unit == 1 && (imm & 0x8000u)) ? tops : 0u);
                up_num = (uint32_t)n;
                up_words.clear();
                up_left = (in_vectors * vector_bits(cmd & 15) + 31) / 32;
                return;
            }

            switch (cmd)
            {
                case 0x01: cl = imm & 0xFF; wl = (imm >> 8) & 0xFF; break;
                case 0x02: if (unit == 1) { ofst = imm & 0x3FF; dbf = false; tops = base; db = true; } break;
                case 0x03: if (unit == 1) base = imm & 0x3FF; break;
                case 0x04: if (unit == 1) itops = imm & 0x3FF; break;
                case 0x05: mode = imm & 3; break;
                case 0x14: case 0x15: start_program(imm * 8u, false); break;
                case 0x17: start_program(0, true); break;
                case 0x20: reg_kind = 3; reg_left = 1; reg_pos = 0; break;
                case 0x30: reg_kind = 1; reg_left = 4; reg_pos = 0; break;
                case 0x31: reg_kind = 2; reg_left = 4; reg_pos = 0; break;
                case 0x4A:
                    mpg_addr = imm * 8u;
                    mpg_left = (uint64_t)(num == 0 ? 256 : num) * 2;
                    g_gs.mpg_words += mpg_left;
                    break;
                case 0x50: case 0x51:
                    if (unit == 1)
                    {
                        if (g_dma_probe && imm > 4096u)
                        {
                            static unsigned huge_direct = 0;
                            if (huge_direct++ < 8)
                                probe_dump("DIRECT_GRANDE", w);
                        }
                        direct_qw = imm == 0 ? 65536u : imm;
                        dn = 0;
                    }

                    break;
                default: break;   // NOP, MSKPATH3, MARK, FLUSH*: sem dados
            }
        }
    };

    VifDecoder g_vif0(0);
    VifDecoder g_vif1(1);

    uint32_t dma_addr(uint32_t a)
    {
        return (a & 0x80000000u) ? (0x70000000u | (a & 0x3FFFu)) : (a & 0x7FFFFFFFu);
    }

    void dma_feed(Ctx& c, int ch, uint32_t addr, uint32_t qwc)
    {
        if (qwc == 0)
            return;

        const uint8_t* p = host_ptr(c, dma_addr(addr), (size_t)qwc * 16);

        if (g_giflog && g_raw_log != 0)
        {
            std::fprintf(stderr, "  [raw] canal %d: %u qw @0x%08X\n", ch, qwc, addr);

            for (uint32_t i = 0; i < qwc && g_raw_log != 0; i++, g_raw_log--)
            {
                uint32_t w[4];
                std::memcpy(w, p + (size_t)i * 16, 16);
                std::fprintf(stderr, "    %08X %08X %08X %08X\n", w[0], w[1], w[2], w[3]);
            }
        }

        for (uint32_t i = 0; i < qwc; i++)
        {
            uint32_t w[4];
            std::memcpy(w, p + (size_t)i * 16, 16);

            if (ch == 2)
                g_gif.qword((uint64_t)w[0] | ((uint64_t)w[1] << 32), (uint64_t)w[2] | ((uint64_t)w[3] << 32));
            else if (ch == 0)
            {
                for (int k = 0; k < 4; k++)
                    g_vif0.word(w[k]);

                if (g_vif0.desync)
                    return;
            }
            else if (g_vif1_direct_qword && !g_dma_probe && !g_vif1.desync &&
                     g_vif1.direct_qw != 0 && g_vif1.dn == 0)
            {
                // Limite o trecho ao DIRECT atual e aos bytes deste DMA.
                // Cada quadword continua passando pelo GIF na ordem original.
                const uint32_t count = g_vif1_direct_run
                    ? (uint32_t)std::min<uint64_t>(g_vif1.direct_qw, qwc - i) : 1u;
                g_gif.qword((uint64_t)w[0] | ((uint64_t)w[1] << 32),
                            (uint64_t)w[2] | ((uint64_t)w[3] << 32));
                for (uint32_t k = 1; k < count; ++k)
                {
                    uint64_t lo, hi;
                    const uint8_t* quadword = p + (size_t)(i + k) * 16;
                    std::memcpy(&lo, quadword, 8);
                    std::memcpy(&hi, quadword + 8, 8);
                    g_gif.qword(lo, hi);
                }
                g_vif1.direct_qw -= count;
                g_gs.direct_qw += count;
                if (g_perf)
                {
                    g_vif1_direct_fast_qwords += count;
                    ++g_vif1_direct_runs;
                }
                i += count - 1;
            }
            else
            {
                for (int k = 0; k < 4; k++)
                {
                    if (g_dma_probe)
                    {
                        g_dma_probe_origin.from_tag = false;
                        g_dma_probe_origin.word_addr = addr + i * 16u + (uint32_t)k * 4u;
                        g_dma_probe_origin.lane = (unsigned)k;
                    }
                    g_vif1.word(w[k]);
                }

                if (g_vif1.desync)
                    return;
            }
        }
    }

    // VIFcode plausivel? (comandos que existem no VIF; UNPACK = 0x60..0x7F)
    inline bool vif_code_ok(uint32_t w)
    {
        const unsigned cmd = (w >> 24) & 0x7Fu;

        if (cmd >= 0x60u)
            return true;

        switch (cmd)
        {
            case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x07:
            case 0x10: case 0x11: case 0x13: case 0x14: case 0x15: case 0x17:
            case 0x20: case 0x30: case 0x31: case 0x4A: case 0x50: case 0x51:
                return true;
            default:
                return false;
        }
    }

    // Par de VIFcodes da metade alta de uma DMAtag: tags legitimas trazem [comando sem dados, comando qualquer]
    // (por exemplo [NOP, DIRECT], [STCYCL, UNPACK], [FLUSH, MSCAL]). Lixo aleatorio quase nunca passa nesse teste.
    inline bool vif_hi_pair_ok(uint32_t w0, uint32_t w1)
    {
        switch ((w0 >> 24) & 0x7Fu)
        {
            case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x07:
            case 0x10: case 0x11: case 0x13:
                return vif_code_ok(w1);
            default:
                return false;
        }
    }

    // diagnostico: [0] TTE+par ok, [1] TTE+lixo, [2] sem TTE e zero, [3] sem TTE+par ok, [4] sem TTE+lixo
    uint64_t g_hi_stat[5] = {};

    void dma_walk(Ctx& c, int ch, uint32_t chcr)
    {
        DmaChan& d = g_dmach[ch];
        struct DbgDmaMark { DbgDmaMark() { g_dbg_dma[5] = 1; } ~DbgDmaMark() { g_dbg_dma[5] = 0; } } dbg_dma_mark;
        g_dbg_dma[0] = (uint32_t)ch;
        g_vif0.cx = &c;
        g_gs.kicks[ch]++;
        unsigned mod = (chcr >> 2) & 3;
        if (g_dma_probe && ch == 1)
        {
            g_dma_probe_origin = {};
            g_dma_probe_origin.chcr = chcr;
        }
        bool detail = g_giflog && g_dma_detail < 12;

        if (detail)
        {
            g_dma_detail++;
            std::fprintf(stderr, "  [dma] canal %d kick: CHCR=0x%X MADR=0x%08X QWC=%u TADR=0x%08X\n", ch, chcr, d.madr, d.qwc, d.tadr);
        }

        if (mod == 0)
        {
            // Teste G (reversivel): no DMA1, CHCR.DIR=0 significa transferencia
            // VIF1 -> RAM, nao RAM -> VIF1. O caminho legado alimentava o VIF1
            // com os bytes ja presentes em MADR, produzindo VIFcodes falsos,
            // DIRECTs enormes e MSCALs para enderecos fora da VU1.
            //
            // Esta chave DIAGNOSTICA apenas impede a transferencia ao contrario.
            // O retorno real de dados GS/VIF1 para a RAM ainda NAO esta implementado;
            // por isso nao altera a RAM nem deve ser considerado um fix definitivo.
            if (ch == 1 && (chcr & 1u) == 0u)
            {
                const bool block_reverse_feed =
                    g_vif1_reverse;
                static uint64_t reverse_count = 0;
                ++reverse_count;

                // Inclui o primeiro caso e algumas amostras posteriores,
                // sem inundar o log durante a batalha.
                if (g_dma_probe &&
                    (reverse_count <= 16u ||
                     (reverse_count & (reverse_count - 1u)) == 0u))
                    std::fprintf(stderr,
                        "  [DMA1 REVERSE] n=%llu CHCR=%03X DIR=0 MADR=%08X "
                        "QWC=%u RA=%08X acao=%s\n",
                        (unsigned long long)reverse_count, chcr, d.madr, d.qwc,
                        (uint32_t)c.r[31].lo,
                        block_reverse_feed ? "IGNORAR FEED (SEM READBACK)" :
                                             "LEGADO (RAM INTERPRETADA COMO VIF)");

                if (block_reverse_feed)
                {
                    // Teste H opt-in: agora podemos tentar o caminho VRAM -> RAM.
                    // Se o GS nao estiver em TRXDIR=1, conserva o teste G sem escrita.
                    if (g_vif1_readback)
                    {
                        const bool ok = gs_download_to_ram(c, d.madr, d.qwc);
                        static uint64_t trials = 0, successes = 0;
                        ++trials;
                        if (ok) ++successes;
                        if (g_dma_probe && (trials <= 16 || (trials & (trials - 1u)) == 0u))
                            std::fprintf(stderr,
                                "  [DMA1 READBACK H] n=%llu status=%s TRXDIR=%u GER=%llu "
                                "PSM=%02X RRW=%u RRH=%u QWC=%u RAM=%08X "
                                "cursor=%zu/%zu sucessos=%llu\n",
                                (unsigned long long)trials,
                                ok ? "COPIADO" : "SEM_GS_DOWNLOAD",
                                (unsigned)(g_st.trxdir & 3u),
                                (unsigned long long)g_st.trx_generation,
                                g_gs_download.psm, g_gs_download.width,
                                g_gs_download.height, d.qwc, d.madr,
                                g_gs_download.cursor, g_gs_download.bytes.size(),
                                (unsigned long long)successes);
                    }
                    return; // Nunca alimenta VIF1 com dados de DMA reverso.
                }
            }

            // Teste F: observa apenas os DMA1 normais que incluem os enderecos
            // em que o teste E2 interpretou dados do payload como VIFcodes.
            // Nao altera a transferencia nem o estado do VIF1.
            bool probe_normal_hit = false;
            if (g_dma_probe && ch == 1 && d.qwc != 0)
            {
                const uint64_t dma_begin = dma_addr(d.madr);
                const uint64_t dma_end = dma_begin + (uint64_t)d.qwc * 16u;
                const uint32_t suspect[] = { 0x01166C20u, 0x0116725Cu, 0x011681FCu, 0x011685F4u };
                for (uint32_t a : suspect)
                    if (dma_begin <= a && (uint64_t)a < dma_end)
                        probe_normal_hit = true;

                if (probe_normal_hit)
                {
                    std::fprintf(stderr,
                        "  [DMA PROBE NORMAL INICIO] MADR=%08X QWC=%u FIM=%08llX CHCR=%X RA=%08X SP=%08X VIF: DIRECT=%llu dn=%u MPG=%llu UNPACK=%llu REG=%u SKIP=%llu cl=%u wl=%u TOP=%03X\n",
                        d.madr, d.qwc, (unsigned long long)dma_end, chcr,
                        (uint32_t)c.r[31].lo, (uint32_t)c.r[29].lo,
                        (unsigned long long)g_vif1.direct_qw, g_vif1.dn,
                        (unsigned long long)g_vif1.mpg_left,
                        (unsigned long long)g_vif1.up_left,
                        g_vif1.reg_left, (unsigned long long)g_vif1.skip_words,
                        g_vif1.cl, g_vif1.wl, g_vif1.tops);

                    // Os comandos anteriores podem mostrar por que o VIF1 nao
                    // esta consumindo esse buffer como payload de DIRECT/UNPACK.
                    const unsigned n = (unsigned)std::min<uint64_t>(8, g_vif1.probe_hist_n);
                    for (unsigned back = n; back > 0; --back)
                    {
                        const auto& h = g_vif1.probe_hist[(g_vif1.probe_hist_n - back) & 15u];
                        std::fprintf(stderr,
                            "    [DMA PROBE NORMAL HIST] VIF=%08X origem=%s addr=%08X tag=%08X CHCR=%X\n",
                            h.code, h.src.from_tag ? "TAG" : "PAYLOAD",
                            h.src.word_addr, h.src.tag_addr, h.src.chcr);
                    }
                }
            }

            // Mesma chamada do teste E: nenhuma mudanca funcional.
            dma_feed(c, ch, d.madr, d.qwc);

            if (probe_normal_hit)
                std::fprintf(stderr,
                    "  [DMA PROBE NORMAL FIM] MADR=%08X QWC=%u VIF: DIRECT=%llu dn=%u MPG=%llu UNPACK=%llu REG=%u SKIP=%llu\n",
                    d.madr, d.qwc, (unsigned long long)g_vif1.direct_qw,
                    g_vif1.dn, (unsigned long long)g_vif1.mpg_left,
                    (unsigned long long)g_vif1.up_left, g_vif1.reg_left,
                    (unsigned long long)g_vif1.skip_words);
            return;
        }

        if (mod != 1)
            return;

        uint32_t tadr = d.tadr;
        uint32_t asr[2] = {};
        int depth = 0;
        const bool tte = (chcr & 0x40u) != 0;

        if (ch == 1) g_vif1.boundary("DMA1");
        else if (ch == 0) g_vif0.boundary("DMA0");

        for (int guard = 0; guard < 200000; guard++)
        {
            if ((ch == 1 && g_vif1.desync) || (ch == 0 && g_vif0.desync))
                return;   // fluxo perdido: o resto da cadeia e lixo (o proximo DMA recomeca limpo)

            const uint8_t* t = host_ptr(c, dma_addr(tadr), 16);
            uint64_t lo, hi;
            std::memcpy(&lo, t, 8);
            std::memcpy(&hi, t + 8, 8);

            uint32_t qwc = (uint32_t)(lo & 0xFFFF);
            unsigned id = (unsigned)((lo >> 28) & 7);
            uint32_t addr = (uint32_t)(lo >> 32);
            g_dbg_dma[1] = (uint32_t)guard; g_dbg_dma[2] = tadr; g_dbg_dma[3] = id; g_dbg_dma[4] = qwc;

            if (detail && guard < 8)
                std::fprintf(stderr, "    tag @0x%08X: id=%u qwc=%u addr=0x%08X  (VIFcodes da tag: 0x%08X 0x%08X)\n",
                    tadr, id, qwc, addr, (uint32_t)hi, (uint32_t)(hi >> 32));

            // No VIF1 a metade alta da DMAtag (bits 64..127) leva dois VIFcodes, tipicamente
            // [NOP, DIRECT(qwc)] (convencao da libgraph): eles chegam ao VIF antes dos dados da tag.
            // E isso que faz os pacotes GIF "puros" dos dados serem aceitos pelo PATH2.
            // PADRAO: envia com TTE, ou sem TTE se o par for plausivel. PS2_VIF_TTE=1 so envia com CHCR.TTE (bit 6), como no hardware (experimental).
            // Sem TTE a metade alta da tag e lixo (o hardware nao a envia): so repassa se os DOIS VIFcodes forem plausiveis
            // (tags legitimas com [NOP, DIRECT]...), descartando o lixo que dessincronizava o fluxo na batalha.
            // PS2_VIF_TTE=1: so envia com CHCR.TTE (experimental). PS2_VIF_TAGALWAYS=1: sempre envia (comportamento antigo).
            static const bool tte_only = std::getenv("PS2_VIF_TTE") != nullptr;
            static const bool tag_always = std::getenv("PS2_VIF_TAGALWAYS") != nullptr;   // PS2_VIF_TAGALWAYS=1: sempre envia (comportamento antigo)
            static unsigned hi_dropped = 0;
            const bool pair_ok = vif_hi_pair_ok((uint32_t)hi, (uint32_t)(hi >> 32));
            const bool hi_ok = tte || pair_ok;

            if (g_dma_probe && ch == 1)
            {
                g_dma_probe_origin.tag_addr = tadr;
                g_dma_probe_origin.tag_id = id;
                g_dma_probe_origin.chcr = chcr;
                static uint64_t tag_count = 0;
                ++tag_count;
                if (tag_count <= 16 || (tag_count & (tag_count - 1)) == 0)
                    std::fprintf(stderr,
                        "  [DMA PROBE TAG] n=%llu TADR=%08X id=%u QWC=%u addr=%08X CHCR=%X TTE=%u hi=%08X,%08X par=%u enviado=%u\n",
                        (unsigned long long)tag_count, tadr, id, qwc, addr, chcr,
                        tte ? 1u : 0u, (uint32_t)hi, (uint32_t)(hi >> 32),
                        pair_ok ? 1u : 0u,
                        (tag_always || (tte_only ? tte : hi_ok)) ? 1u : 0u);
            }

            if (ch == 1)
                g_hi_stat[tte ? (pair_ok ? 0 : 1) : (hi == 0 ? 2 : (pair_ok ? 3 : 4))]++;

            if (!hi_ok && !tag_always && !tte_only && (++hi_dropped <= 4 || (hi_dropped & 4095u) == 0))
                std::fprintf(stderr, "  [DMA%d] tag sem TTE com VIFcodes invalidos (%08X %08X) descartados (%u vezes)\n", ch,
                    (uint32_t)hi, (uint32_t)(hi >> 32), hi_dropped);

            if (tag_always ? true : (tte_only ? tte : hi_ok))
            {
                if (ch == 1)
                {
                    if (g_vif1_tag_nop_fast && g_vif1.try_tag_nops(hi))
                    {
                        if (g_perf) ++g_vif1_tag_nop_pairs;
                    }
                    else
                    {
                        if (g_dma_probe)
                        {
                            g_dma_probe_origin.from_tag = true;
                            g_dma_probe_origin.word_addr = tadr + 8u;
                            g_dma_probe_origin.lane = 2;
                        }
                        g_vif1.word((uint32_t)hi);
                        if (g_dma_probe)
                        {
                            g_dma_probe_origin.word_addr = tadr + 12u;
                            g_dma_probe_origin.lane = 3;
                        }
                        g_vif1.word((uint32_t)(hi >> 32));
                    }
                }
                else if (ch == 0)
                {
                    g_vif0.word((uint32_t)hi);
                    g_vif0.word((uint32_t)(hi >> 32));
                }
            }

            switch (id)
            {
                case 0: dma_feed(c, ch, addr, qwc); return;                                  // refe
                case 1: dma_feed(c, ch, tadr + 16, qwc); tadr += 16 + qwc * 16; break;      // cnt
                case 2: dma_feed(c, ch, tadr + 16, qwc); tadr = addr; break;                // next
                case 3: case 4: dma_feed(c, ch, addr, qwc); tadr += 16; break;              // ref, refs
                case 5:                                                                      // call
                    dma_feed(c, ch, tadr + 16, qwc);

                    if (depth < 2)
                        asr[depth++] = tadr + 16 + qwc * 16;

                    tadr = addr;
                    break;
                case 6:                                                                      // ret
                    dma_feed(c, ch, tadr + 16, qwc);

                    if (depth == 0)
                        return;

                    tadr = asr[--depth];
                    break;
                default: dma_feed(c, ch, tadr + 16, qwc); return;                           // end
            }
        }
    }
}

void ps2_dma_write(Ctx& c, uint32_t phys, uint64_t value)
{
    // registradores privilegiados do GS (PMODE, DISPFB1/2, DISPLAY1/2...): dizem qual area e exibida
    if (g_gsrun && phys >= 0x12000000u && phys < 0x12000100u && (phys & 7u) == 0)
    {
        // Carrega o FIELD visto pelo EE junto com a escrita para o GS assincrono.
        const unsigned csr_field = g_field_audit ? g_gs_field : 0u;
        if (g_gs_async) gs_q_push(2, phys, value, csr_field);
        else gs_exec({ value, csr_field, 2, phys });

        return;
    }

    if (!g_gsrun || phys < 0x10008000u || phys >= 0x1000B000u || (phys & 0xFFFu) >= 0x60u)
        return;

    int ch = (int)((phys - 0x10008000u) >> 12);
    uint32_t reg = phys & 0xFFFu;

    DmaChan& d = g_dmach[ch];

    switch (reg)
    {
        case 0x10: d.madr = (uint32_t)value; break;
        case 0x20: d.qwc = (uint32_t)value; break;
        case 0x30: d.tadr = (uint32_t)value; break;
        case 0x00:
            if (g_dma_probe && ch == 1)
            {
                static uint64_t chcr_count = 0;
                ++chcr_count;
                if (chcr_count <= 20 || (chcr_count & (chcr_count - 1)) == 0)
                    std::fprintf(stderr,
                        "  [DMA PROBE CHCR] n=%llu valor=%08X TTE=%u STR=%u MADR=%08X QWC=%u TADR=%08X RA=%08X SP=%08X\n",
                        (unsigned long long)chcr_count, (uint32_t)value,
                        (((uint32_t)value & 0x40u) != 0) ? 1u : 0u,
                        (((uint32_t)value & 0x100u) != 0) ? 1u : 0u,
                        d.madr, d.qwc, d.tadr, (uint32_t)c.r[31].lo, (uint32_t)c.r[29].lo);
            }
            if (value & 0x100)
            {
                constexpr Timer0EeStage stages[] = {
                    Timer0EeStage::Vif0, Timer0EeStage::Vif1, Timer0EeStage::Gif
                };
                const Timer0StageScope timer0_stage(stages[ch]);
                std::lock_guard<std::mutex> lk(g_gs_mu);

                if (ch == 0)
                {
                    const uint64_t pt0 = g_perf ? prof_now() : 0;
                    dma_walk(c, 0, (uint32_t)value);   // VIF0: micro codigo e dados do VU0, sem GS
                    if (g_perf) g_prof_dma_ns += prof_now() - pt0;
                    break;
                }

                gs_trace_set_caller(c);
                g_trace_vtx = 0;
                g_trace_degen = 0;
                unsigned long draws_before = g_trace_draws;
                uint64_t unsupported_before = g_st.unsupported;
                const uint64_t pt0 = g_perf ? prof_now() : 0;
                dma_walk(c, ch, (uint32_t)value);
                gs_q_flush_batch();
                const uint64_t elapsed = g_perf ? prof_now() - pt0 : 0;
                g_prof_dma_ns += elapsed;
                if (g_perf_cadence)
                {
                    if (ch == 1) g_cadence_ee.dma_vif1_ns += elapsed;
                    if (ch == 2) g_cadence_ee.dma_gif_ns += elapsed;
                }
                gs_trace_kick(ch, draws_before, unsupported_before);
            }

            break;
        default: break;
    }
}

// Imprime o ranking pelo tempo de interpretacao, com o tempo do GIF em coluna propria.
static void vif1_prof_report(double seconds)
{
    if (seconds > 0.0)
    {
        std::fprintf(stderr, "  [prof VIF1] intervalo=%.3f s UNPACK=%s commits=%llu payload_words=%llu commit_ms=%.3f\n",
            seconds, g_vif1_unpack_fast ? "fast" : "legado",
            (unsigned long long)g_prof_unpack_calls, (unsigned long long)g_prof_unpack_words,
            g_prof_unpack_ns / 1e6);

        unsigned active[2048], count = 0;
        Vu1Profile total;
        for (unsigned i = 0; i < 2048; ++i)
        {
            const Vu1Profile& p = g_prof_vu1[i];
            if (p.calls == 0)
                continue;
            active[count++] = i;
            total.calls += p.calls;
            total.pairs += p.pairs;
            total.ns += p.ns;
            total.kick_ns += p.kick_ns;
        }
        const unsigned shown = std::min(count, 8u);
        std::partial_sort(active, active + shown, active + count, [](unsigned a, unsigned b)
        {
            const uint64_t ta = g_prof_vu1[a].ns - g_prof_vu1[a].kick_ns;
            const uint64_t tb = g_prof_vu1[b].ns - g_prof_vu1[b].kick_ns;
            return ta != tb ? ta > tb : a < b;
        });

        std::fprintf(stderr, "  [prof VU1] caminho=%s entradas=%u chamadas=%llu pares=%llu interpretador_ms=%.3f xgkick_ms=%.3f\n",
            g_vu1_fast ? "fast" : "legado", count, (unsigned long long)total.calls, (unsigned long long)total.pairs,
            (total.ns - total.kick_ns) / 1e6, total.kick_ns / 1e6);
        for (unsigned rank = 0; rank < shown; ++rank)
        {
            const unsigned i = active[rank];
            const Vu1Profile& p = g_prof_vu1[i];
            std::fprintf(stderr, "  [prof VU1] entrada=0x%04X chamadas=%llu pares=%llu interpretador_ms=%.3f xgkick_ms=%.3f\n",
                i * 8u, (unsigned long long)p.calls, (unsigned long long)p.pairs,
                (p.ns - p.kick_ns) / 1e6, p.kick_ns / 1e6);
        }
    }

    // O primeiro relatorio apenas estabelece a linha de base dos intervalos.
    for (Vu1Profile& p : g_prof_vu1)
        p = {};
    if (seconds > 0.0 && g_vu_aot_enabled)
        std::fprintf(stderr, "  [prof VU1 AOT] pares_nativos=%llu compativel=%llu micro_diferente=%llu sem_include=%llu pares_nativos_parcial=%llu cache_revisao=%u reusos=%llu\n",
            (unsigned long long)g_prof_aot_pairs, (unsigned long long)g_prof_aot_match,
            (unsigned long long)g_prof_aot_mismatch, (unsigned long long)g_prof_aot_missing,
            (unsigned long long)g_prof_aot_partial_pairs,
            (unsigned)g_vu_aot_revision_cache, (unsigned long long)g_prof_aot_revision_hits);
    g_prof_aot_pairs = g_prof_aot_match = g_prof_aot_mismatch = g_prof_aot_missing = g_prof_aot_partial_pairs = 0;
    g_prof_aot_revision_hits = 0;
    g_prof_unpack_calls = g_prof_unpack_words = g_prof_unpack_ns = 0;
}

// Resumo a cada 120 vsyncs (2 s): o que o jogo mandou desenhar nesse intervalo.
// Relatorio de desempenho a cada 120 vsyncs (PS2_PERF=1 ou PS2_GIFLOG=1): onde o tempo de emulacao foi gasto.
static void gs_field_report()
{
    // O relatorio e chamado com g_gs_mu. Aguarda o consumidor antes
    // de ler os snapshots preenchidos pela thread do GS.
    gs_q_drain();
    static unsigned last_flip = 0;
    const unsigned latest = g_gs_flips.load(std::memory_order_relaxed);
    if (latest == last_flip)
        return;

    // A troca mais recente ainda pode ser avaliada no proximo VBLANK.
    const unsigned current = latest - 1u;
    const unsigned delta = current - last_flip;
    const unsigned count = std::min(delta, kFieldAuditCapacity - 1u);
    if (count < delta)
        std::fprintf(stderr, "  [campo GS] trocas_fora_do_historico=%u\n", delta - count);
    for (unsigned i = count; i != 0; --i)
    {
        const unsigned flip = current - i + 1;
        const FieldFlipAudit& f = g_field_flips[flip % kFieldAuditCapacity];
        std::fprintf(stderr,
            "  [campo GS] troca=%u circuito=%u FBP=%u FIELD_CSR=%u DY=%u DBY=%u "
            "voto_troca=%d sprites=%u Y_meia=%u OFY_meia=%u OFY_ultimo=0x%04X "
            "avaliacoes=%u bloqueios=%u publicados=%u ultimo_voto_janela=%d\n",
            f.flip, f.circuit, f.fbp, f.csr_field, f.dy, f.dby,
            f.vote, f.sprites.sprites, f.sprites.half_y, f.sprites.half_ofy,
            f.sprites.ofy_last, f.checked, f.blocked, f.published, f.present_vote);
    }
    last_flip = current;
}

static void gs_cadence_report(bool has_interval)
{
    CadenceGsProfile gs;
    {
        std::lock_guard<std::mutex> lk(g_cadence_gs_mu);
        gs = g_cadence_gs;
        g_cadence_gs = {};
    }
    const CadenceEeProfile ee = g_cadence_ee;
    g_cadence_ee = {};
    if (!has_interval) return;

    std::fprintf(stderr,
        "  [prof cadencia GS] vb_0=%u vb_1=%u vb_2=%u vb_3=%u vb_4mais=%u maior_gap_ms=%.3f\n",
        gs.gaps[0], gs.gaps[1], gs.gaps[2], gs.gaps[3], gs.gaps[4], gs.max_gap_ns / 1e6);
    std::fprintf(stderr,
        "  [prof cadencia EE] lotes=%u lotes_multiplos=%u max_pendentes=%u descartados=%llu "
        "vblank_ocioso=%u sleeps=%u sleep_ms=%.3f vif1_ms=%.3f gif_ms=%.3f\n",
        ee.batches, ee.multiple, ee.max_pending, (unsigned long long)ee.dropped,
        ee.idle_vblanks, ee.idle_sleeps, ee.idle_sleep_ns / 1e6, ee.dma_vif1_ns / 1e6, ee.dma_gif_ns / 1e6);
}

extern const bool g_vu0_status_trace = [] {
    const char* value = std::getenv("PS2_VU0_STATUS_TRACE");
    return value && std::strcmp(value, "1") == 0;
}();

namespace
{
    constexpr unsigned kVu0EventCount = (unsigned)Vu0StatusEvent::Count;
    constexpr const char* kVu0EventNames[] = {
        "VDIV", "VSQRT", "VRSQRT", "VWAITQ", "CFC2_VI16", "CTC2_VI16",
        "micro_DIV", "micro_SQRT", "micro_RSQRT", "micro_leitura_STATUS"
    };
    static_assert(sizeof kVu0EventNames / sizeof *kVu0EventNames == kVu0EventCount);
    struct Vu0EventCounts
    {
        uint64_t count = 0;
        uint32_t first_pc = 0, first_value = 0, last_pc = 0, last_value = 0;
    };
    Vu0EventCounts g_vu0_events[kVu0EventCount]{};

    void vu0_status_report()
    {
        if (!g_vu0_status_trace)
            return;
        for (unsigned i = 0; i < kVu0EventCount; ++i)
        {
            const auto& counts = g_vu0_events[i];
            std::fprintf(stderr,
                "  [prof VU0 STATUS] op=%s n=%llu primeiro_pc=%08X primeiro_status=%03X ultimo_pc=%08X ultimo_status=%03X\n",
                kVu0EventNames[i], (unsigned long long)counts.count, counts.first_pc,
                counts.first_value, counts.last_pc, counts.last_value);
            g_vu0_events[i] = {};
        }
    }
}

void vu0_status_record(Vu0StatusEvent event, uint32_t pc, uint32_t value)
{
    // Apenas a thread convidada ativa do EE executa estas instrucoes.
    auto& counts = g_vu0_events[(unsigned)event];
    value &= 0xFFFu;
    if (counts.count++ == 0)
    {
        counts.first_pc = pc;
        counts.first_value = value;
    }
    counts.last_pc = pc;
    counts.last_value = value;
}

static void gs_prof_report()
{
        static uint64_t last_dma = 0, last_vu = 0, last_pr = 0, last_tri = 0, last_sprite = 0, last_img = 0, last_wall = 0;
        static unsigned last_flips = 0, last_presented = 0;
        const uint64_t now = prof_now();
        const unsigned flips = g_gs_flips.load();
        const unsigned presented = g_win_seq.load(std::memory_order_relaxed);
        const uint64_t tri = g_prof_tri_ns.load(std::memory_order_relaxed);
        const uint64_t sprite = g_prof_sprite_ns.load(std::memory_order_relaxed);

        if (g_field_clock_trace)
            field_clock_report(last_wall ? (double)(now - last_wall) / 1e9 : 0.0);
        if (g_move_callback_trace)
            move_callback_report(last_wall ? (double)(now - last_wall) / 1e9 : 0.0);

        if (last_wall != 0)
        {
            const double seconds = (double)(now - last_wall) / 1e9;
            std::fprintf(stderr, "  [prof] taxas: GS=%.1f trocas/s | janela=%.1f quadros publicados/s\n",
                (flips - last_flips) / seconds, (presented - last_presented) / seconds);
            vu0_status_report();
            rotation_caller_report();
            char upload[48] = "sem_medicao";
            if (g_prof_image_qword)
                std::snprintf(upload, sizeof upload, "%llu%s", (unsigned long long)((g_prof_img_ns - last_img) / 1000000u),
                    g_prof_image_stride == 1 ? "" : "_amostrado");
            std::fprintf(stderr, "  [prof] %.1f s: DMA/GS=%llu ms (microprogramas=%llu raster=%llu upload=%s) janela=%llu ms | trocas de buffer=%u\n",
                (double)(now - last_wall) / 1e9, (unsigned long long)((g_prof_dma_ns - last_dma) / 1000000u),
                (unsigned long long)((g_prof_vu_ns - last_vu) / 1000000u), (unsigned long long)((tri - last_tri + sprite - last_sprite) / 1000000u),
                upload, (unsigned long long)((g_prof_present_ns - last_pr) / 1000000u),
                flips - last_flips);
            static uint64_t last_samples = 0;
            std::fprintf(stderr, "  [prof upload] ativo=%u passo=%u amostras=%llu\n",
                (unsigned)g_prof_image_qword, g_prof_image_stride,
                (unsigned long long)(g_prof_image_samples - last_samples));
            last_samples = g_prof_image_samples;
        }

        if (g_perf_gs)
        {
            if (last_wall != 0)
            {
                std::fprintf(stderr, "  [prof GS] tri_early_z=%u tri_ms=%.3f sprite_ms=%.3f tri_texture_specialize=%u tri_effects_fast=%u tri_rgba_sse2=%u tri_row_fast=%u\n",
                    (unsigned)g_gs_tri_early_z, (tri - last_tri) / 1e6,
                    (sprite - last_sprite) / 1e6, (unsigned)g_gs_tri_texture_specialize, (unsigned)g_gs_tri_effects_fast, (unsigned)g_gs_tri_rgba_sse2, (unsigned)g_gs_tri_row_fast);
                std::fprintf(stderr,
                    "  [prof janela] publicados=%u pacing=%u display_invalido=%u fieldlock=%u publicados_sem_nova_troca=%u campos_alinhados=%u adiantados_por_troca=%u fila_adiados=%u fila_espera_ms=%.3f sem_troca_ignorados=%u\n",
                    presented - last_presented, g_present_profile.pacing, g_present_profile.invalid,
                    g_present_profile.fieldlock, g_present_profile.same_flip, g_present_profile.aligned,
                    g_present_profile.early_flip, g_present_profile.queue_skips,
                    g_present_profile.queue_wait_ns / 1e6, g_present_profile.unchanged);
                std::fprintf(stderr,
                    "  [prof campo raster] ativo=%u campos=%u vertices_sprite=%llu vertices_tri=%llu bloqueios_fase_mista=%u bloqueios_OFY_desconhecido=%u trem_ativo=%u campos_trem=%u recuperados_ofy_zero=%u\n",
                    (unsigned)g_field_raster, g_present_profile.raster_fields,
                    (unsigned long long)g_present_profile.raster_sprite_vertices,
                    (unsigned long long)g_present_profile.raster_triangle_vertices,
                    g_present_profile.raster_mixed, g_present_profile.raster_unknown,
                    (unsigned)(g_field_raster && g_field_raster_train), g_present_profile.raster_train,
                    g_present_profile.raster_train_zero);
            }
            static uint64_t last_fast_pixels = 0, last_generic_pixels = 0, last_z16_pixels = 0;
            const uint64_t fast_pixels = g_prof_sprite_fast_pixels.load(std::memory_order_relaxed);
            const uint64_t generic_pixels = g_prof_sprite_generic_pixels.load(std::memory_order_relaxed);
            const uint64_t z16_pixels = g_prof_sprite_z16_pixels.load(std::memory_order_relaxed);
            if (last_wall != 0)
                std::fprintf(stderr, "  [prof sprites] colunas=%u pixels_fast=%llu pixels_generico=%llu generico_linhas=%u wide_fast=%u z16_early=%u pixels_z16=%llu\n",
                    (unsigned)g_sprite_columns, (unsigned long long)(fast_pixels - last_fast_pixels),
                    (unsigned long long)(generic_pixels - last_generic_pixels), (unsigned)g_sprite_generic_rows,
                    (unsigned)g_sprite_wide_fast, (unsigned)g_sprite_z16_early, (unsigned long long)(z16_pixels - last_z16_pixels));
            last_fast_pixels = fast_pixels;
            last_generic_pixels = generic_pixels;
            last_z16_pixels = z16_pixels;
            g_present_profile = {};
            if (g_field_train_diag && g_field_train_sample.raster.unknown != 0)
            {
                const auto& sample = g_field_train_sample;
                const auto& state = sample.raster;
                if (last_wall != 0)
                {
                    std::fprintf(stderr,
                        "  [prof OFY trem] troca=%u FBP=%u tamanho=%ux%u MAGV=%u FBW=%u PSM=%02X "
                        "base=%u ajustados=%u fase_trem=%u desconhecidos=%u outros=%u\n",
                        sample.flip, sample.fbp, sample.w, sample.h, sample.magv, sample.fbw, sample.psm,
                        state.base, state.shifted, (unsigned)state.shifted_train, state.unknown, state.other_unknown);
                    for (const auto& offset : state.offsets)
                        if (offset.vertices != 0)
                            std::fprintf(stderr,
                                "  [prof OFY vertice] OFY=0x%04X FBW=%u PSM=%02X contexto=%u layout_valido=%u vertices=%u\n",
                                offset.ofy, offset.fbw, offset.psm, offset.context + 1,
                                (unsigned)offset.target, offset.vertices);
                }
                g_field_train_sample = {};
            }
        }

        std::fprintf(stderr, "  [prof] tags VIF1: TTE ok=%llu lixo=%llu | sem TTE: zero=%llu ok=%llu lixo=%llu\n", (unsigned long long)g_hi_stat[0],
            (unsigned long long)g_hi_stat[1], (unsigned long long)g_hi_stat[2], (unsigned long long)g_hi_stat[3], (unsigned long long)g_hi_stat[4]);

        {
            static uint64_t last_nop_pairs = 0;
            std::fprintf(stderr, "  [prof VIF1 NOP] ativo=%u pares=%llu\n",
                (unsigned)g_vif1_tag_nop_fast, (unsigned long long)(g_vif1_tag_nop_pairs - last_nop_pairs));
            last_nop_pairs = g_vif1_tag_nop_pairs;
        }

        {
            static uint64_t last_gen = 0, last_del = 0;
            const uint64_t gen = g_vsync_gen.load(), del = g_vsync_del.load();
            std::fprintf(stderr, "  [prof] vsync: gerados=%llu entregues=%llu (neste intervalo)%s\n", (unsigned long long)(gen - last_gen),
                (unsigned long long)(del - last_del), g_vsync_catchup ? " [catchup]" : "");
            last_gen = gen; last_del = del;
        }

        {
            static uint64_t last_periods = 0, last_delivered = 0, last_wakes = 0, last_posts = 0, last_merged = 0, last_dma_delivered = 0;
            const uint64_t periods = g_timer0_periods.load(std::memory_order_relaxed);
            const uint64_t delivered = g_timer0_delivered.load(std::memory_order_relaxed);
            const uint64_t wakes = g_irq_wakes.load(std::memory_order_relaxed);
            const uint64_t posts = g_timer0_posts.load(std::memory_order_relaxed);
            const uint64_t merged = g_timer0_merged.load(std::memory_order_relaxed);
            std::fprintf(stderr, "  [prof timer0] fase_fixa=%u espera_alta_res=%u despertares=%llu periodos=%llu sinais=%llu agrupados_ee=%llu entregues=%llu entregues_dma=%llu dma_poll=%u comparacao=0x%llX\n",
                (unsigned)g_timer0_phase, (unsigned)g_irq_wait_highres_active.load(std::memory_order_relaxed),
                (unsigned long long)(wakes - last_wakes), (unsigned long long)(periods - last_periods),
                (unsigned long long)(posts - last_posts), (unsigned long long)(merged - last_merged),
                (unsigned long long)(delivered - last_delivered),
                (unsigned long long)(g_timer0_dma_delivered - last_dma_delivered), (unsigned)g_timer0_dma_poll,
                (unsigned long long)g_timer0_comp.load());
            last_periods = periods;
            last_delivered = delivered;
            last_wakes = wakes;
            last_posts = posts;
            last_merged = merged;
            last_dma_delivered = g_timer0_dma_delivered;
            static uint64_t last_recovered = 0;
            if (g_timer0_elapsed)
                std::fprintf(stderr, "  [prof timer0 relogio] ticks_reexecutados=%llu\n",
                    (unsigned long long)(g_timer0_ticks_replayed - last_recovered));
            last_recovered = g_timer0_ticks_replayed;
        }

        if (g_timer0_trace_stages)
        {
            constexpr unsigned count = (unsigned)Timer0EeStage::Count;
            unsigned long long delta[count]{};
            for (unsigned i = 0; i < count; ++i)
                delta[i] = g_timer0_merges_by_stage[i].exchange(0, std::memory_order_relaxed);

            std::fprintf(stderr, "  [prof timer0 etapas] outros=%llu vif0=%llu vif1=%llu gif=%llu vblank=%llu timer0=%llu\n",
                delta[0], delta[1], delta[2], delta[3], delta[4], delta[5]);
        }

        if (g_perf_cadence)
            gs_cadence_report(last_wall != 0);

        if (last_wall != 0)
        {
            static uint64_t last_direct_fast = 0, last_direct_total = 0, last_direct_runs = 0;
            std::fprintf(stderr, "  [prof VIF1 DIRECT] ativo=%u rapidos=%llu total=%llu agrupar=%u trechos=%llu\n",
                (unsigned)g_vif1_direct_qword,
                (unsigned long long)(g_vif1_direct_fast_qwords - last_direct_fast),
                (unsigned long long)(g_gs.direct_qw - last_direct_total), (unsigned)g_vif1_direct_run,
                (unsigned long long)(g_vif1_direct_runs - last_direct_runs));
            last_direct_runs = g_vif1_direct_runs;
            last_direct_fast = g_vif1_direct_fast_qwords;
            last_direct_total = g_gs.direct_qw;
        }

        if (g_perf_vif1)
            vif1_prof_report(last_wall == 0 ? 0.0 : (double)(now - last_wall) / 1e9);

        if (g_gs_async && last_wall != 0)
        {
            static uint64_t last_wait_ns = 0, last_wait_count = 0;
            const uint64_t waited = g_gsq_full_wait_ns.load(std::memory_order_relaxed);
            const uint64_t blocked = g_gsq_full_wait_count.load(std::memory_order_relaxed);
            std::fprintf(stderr, "  [prof] GS FILA CHEIA: espera=%llu ms | bloqueios=%llu (neste intervalo)\n",
                (unsigned long long)((waited - last_wait_ns) / 1000000u),
                (unsigned long long)(blocked - last_wait_count));
            last_wait_ns = waited;
            last_wait_count = blocked;
            static uint64_t last_busy = 0, last_flip_wait_ns = 0, last_flip_waits = 0;
            const uint64_t busy = g_gsq_busy_ns.load(std::memory_order_relaxed);
            std::fprintf(stderr, "  [prof] thread do GS: ocupada=%llu ms | fila=%llu comandos\n", (unsigned long long)((busy - last_busy) / 1000000u),
                (unsigned long long)(g_gsq_head.load() - g_gsq_tail.load()));
            last_busy = busy;
            static uint64_t last_work_wakes = 0;
            const uint64_t work_wakes = g_gsq_work_wakes.load(std::memory_order_relaxed);
            std::fprintf(stderr, "  [prof GS despertar] ativo=%u retomadas=%llu\n",
                (unsigned)g_gsq_wake_on_work, (unsigned long long)(work_wakes - last_work_wakes));
            last_work_wakes = work_wakes;
            static uint64_t last_publications = 0, last_commands = 0;
            const uint64_t commands = g_gsq_head.load(std::memory_order_relaxed);
            std::fprintf(stderr, "  [prof GS lote] ativo=%u comandos=%llu publicacoes=%llu inicio_rapido=%u cursor_local=%u\n",
                (unsigned)g_gsq_batch, (unsigned long long)(commands - last_commands),
                (unsigned long long)(g_gsq_publications - last_publications), (unsigned)g_gsq_start_fast, (unsigned)g_gsq_local_cursor);
            last_commands = commands;
            last_publications = g_gsq_publications;
            std::fprintf(stderr, "  [prof fila por troca] ativo=%u esperas=%llu espera_ms=%.3f\n",
                (unsigned)g_gsq_limit_flips, (unsigned long long)(g_gsq_flip_waits - last_flip_waits),
                (g_gsq_flip_wait_ns - last_flip_wait_ns) / 1e6);
            last_flip_waits = g_gsq_flip_waits;
            last_flip_wait_ns = g_gsq_flip_wait_ns;
        }

#ifdef _WIN32
        if (g_perf_gs)
        {
            static unsigned last_blits = 0, last_coalesced = 0, last_errors = 0;
            const unsigned blits = g_win_blits.load(std::memory_order_relaxed);
            const unsigned coalesced = g_win_coalesced.load(std::memory_order_relaxed);
            const unsigned errors = g_win_blit_errors.load(std::memory_order_relaxed);
            if (last_wall != 0)
            {
                const double seconds = (now - last_wall) / 1e9;
                std::fprintf(stderr, "  [prof GDI] novos/s=%.1f novos=%u pulados=%u falhas=%u\n",
                    (blits - last_blits) / seconds, blits - last_blits,
                    coalesced - last_coalesced, errors - last_errors);
            }
            static uint64_t last_content_counts = 0;
            const uint64_t content_counts = g_win_content_counts.load(std::memory_order_relaxed);
            const unsigned distinct = (unsigned)content_counts - (unsigned)last_content_counts;
            const unsigned repeated = (unsigned)(content_counts >> 32) - (unsigned)(last_content_counts >> 32);
            if (last_wall != 0)
                std::fprintf(stderr, "  [prof GDI pixels] distintos/s=%.1f distintos=%u repetidos=%u\n",
                    distinct / ((now - last_wall) / 1e9), distinct, repeated);
            last_content_counts = content_counts;
            last_blits = blits;
            last_coalesced = coalesced;
            last_errors = errors;
        }
#endif
        last_dma = g_prof_dma_ns; last_vu = g_prof_vu_ns; last_pr = g_prof_present_ns; last_tri = tri; last_sprite = sprite; last_img = g_prof_img_ns;
        last_wall = now; last_flips = flips; last_presented = presented;
}

void ps2_gs_vsync()
{
    if (!g_gsrun)
        return;

    ++g_gs_vsyncs;
    gs_capture_poll();

    if (g_gswin)
    {
        std::lock_guard<std::mutex> lk(g_gs_mu);
        gs_present_frame();
    }

    if (g_gsdump)
    {
        static unsigned dumps = 0;
        static unsigned period = 0;

        if (period == 0)
        {
            const char* e = std::getenv("PS2_GSDUMP");
            unsigned secs = e != nullptr ? (unsigned)std::strtoul(e, nullptr, 0) : 5u;
            period = (secs == 0 ? 5u : secs) * 60u;
        }

        if (g_gs_vsyncs % period == 0 && dumps < 60)
        {
            std::lock_guard<std::mutex> lk(g_gs_mu);
            gs_dump_frame(dumps++);
        }
    }

    static const bool g_perf_on = g_perf || g_giflog || g_field_train_diag ||
        g_timer0_trace_stages || g_field_clock_trace || g_move_callback_trace;

    if (g_perf_on && g_gs_vsyncs % 120u == 0)
    {
        std::lock_guard<std::mutex> lk(g_gs_mu);
        gs_prof_report();
        if (g_field_audit) gs_field_report();
    }

    if (!g_giflog || g_gs_vsyncs % 120u != 0)
        return;

    std::lock_guard<std::mutex> lk(g_gs_mu);
    static const char* ptype[8] = { "ponto", "linha", "tira-linha", "triangulo", "tira-tri", "leque-tri", "sprite", "?" };

    std::fprintf(stderr, "  [gs] 2s: kicks vif1=%llu gif=%llu | GIFtags packed=%llu reglist=%llu image=%llu (imagem=%llu qw) | direct=%llu qw mpg=%llu w unpack=%llu mscal=%llu xgkick=%llu (%llu qw)\n",
        (unsigned long long)g_gs.kicks[1], (unsigned long long)g_gs.kicks[2], (unsigned long long)g_gs.tags_packed,
        (unsigned long long)g_gs.tags_reglist, (unsigned long long)g_gs.tags_image, (unsigned long long)g_gs.image_qw,
        (unsigned long long)g_gs.direct_qw, (unsigned long long)g_gs.mpg_words, (unsigned long long)g_gs.unpack_cmds,
        (unsigned long long)g_gs.mscal, (unsigned long long)g_gs.xgkicks, (unsigned long long)g_gs.xgkick_qw);

    if (!g_perf)
        gs_prof_report();

    std::fprintf(stderr, "  [gs] 2s: PRIM:");

    for (int i = 0; i < 8; i++)
        if (g_gs.prim[i] != 0)
            std::fprintf(stderr, " %s=%llu", ptype[i], (unsigned long long)g_gs.prim[i]);

    std::fprintf(stderr, " | registradores:");

    for (int n = 0; n < 8; n++)
    {
        int best = -1;

        for (int r = 0; r < 256; r++)
            if (g_gs.reg[r] != 0 && (best < 0 || g_gs.reg[r] > g_gs.reg[best]))
                best = r;

        if (best < 0)
            break;

        std::fprintf(stderr, " %s=%llu", gs_reg_name((unsigned)best), (unsigned long long)g_gs.reg[best]);
        g_gs.reg[best] = 0;   // so para o ranking; os totais por registrador nao importam depois do resumo
    }

    std::fprintf(stderr, "\n");
    std::memset(g_gs.prim, 0, sizeof(g_gs.prim));
    std::memset(g_gs.reg, 0, sizeof(g_gs.reg));
}

void ps2_break(Ctx& c, uint32_t address)
{
    // MWCC usa BREAK 7 depois de DIV para "divisao por zero"; outros codigos sao asserts do jogo.
    uint32_t word = mrd<uint32_t>(c, address);
    uint32_t code = (word >> 6) & 0xFFFFFu;

    std::fprintf(stderr, "BREAK em 0x%08X codigo=%u%s\n", address, code,
        code == 7 ? " (divisao por zero)" : code == 6 ? " (overflow)" : "");

    static const char* names[32] = { "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3", "t0", "t1", "t2", "t3",
        "t4", "t5", "t6", "t7", "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "t8", "t9", "k0", "k1",
        "gp", "sp", "fp", "ra" };

    for (int i = 1; i < 32; i++)
        std::fprintf(stderr, "  %-4s= 0x%08X%s", names[i], (unsigned)c.r[i].lo, (i % 4 == 3) ? "\n" : "");

    std::fprintf(stderr, "\n");
    die();
}

void ps2_unsupported(Ctx&, uint32_t address, const char* mnemonic)
{
    std::fprintf(stderr, "instrucao sem suporte em 0x%08X (%s)\n", address, mnemonic);
    die();
}

// ---- VU0 e VU1 em modo micro: interpretador ----
// VU0: micro 4 KB / dados 4 KB, no estado do modo macro (VF, ACC, VI, Q, I, flags). VU1: 16 KB / 16 KB, estado proprio
// (g_vu1_regs), com EFU (P), XTOP/XITOP e XGKICK (envia o pacote ao GIF). A micro memoria chega por MPG (VIF) ou por
// escritas da CPU; os dados por UNPACK ou escritas da CPU. MSCAL/VCALLMS rodam o programa na hora, ate o bit E.
// Cada instrucao de 64 bits = lower (palavra 0) + upper (palavra 1).
// Simplificacoes: sem ciclos de pipeline, exceto a latencia de Q (DIV/SQRT 7, RSQRT 13).

struct VuUnit
{
    Ctx* r;
    uint8_t* micro;
    uint8_t* data;
    uint32_t imask;     // mascara do indice de instrucao (0x1FF no VU0, 0x7FF no VU1)
    uint32_t dmask;     // mascara do indice de quadword da memoria de dados (0xFF / 0x3FF)
    bool vu1;
    uint32_t pc;
};

uint32_t g_vu0_pc = 0, g_vu1_pc = 0;     // instrucao (de 64 bits) onde o ultimo programa parou, para MSCNT
uint32_t g_vu0_pc_get() { return g_vu0_pc; }
uint32_t g_vu1_pc_get() { return g_vu1_pc; }
static unsigned g_vu0_runs = 0, g_vu1_runs = 0;

static inline uint32_t* um_q(const VuUnit& u, uint32_t index) { return reinterpret_cast<uint32_t*>(u.data) + ((index & u.dmask) * 4u); }

// Rastreia somente escritas na primeira palavra dos dois cabecalhos usados por VI03.
// Chamado nos caminhos SQ/ISW da VU, nunca modifica o valor gravado.
static void um_trace_header_write(const VuUnit& u, uint32_t qaddr, const char* op, uint32_t pc, uint32_t valor)
{
    if (!u.vu1 || !g_vu_header_trace)
        return;
    const uint32_t a = qaddr & u.dmask;
    if (a != 0x1B8u && a != 0x339u)
        return;
    static uint64_t n = 0;
    ++n;
    if (n <= 64 || (n & 4095u) == 0)
        std::fprintf(stderr, "  [VU1 WRITE MICRO] n=%llu pc=%04X %s D[%03X].x=%08X\n",
            (unsigned long long)n, pc * 8u, op, a, valor);
}


static bool g_um_flags = true;   // calcula MAC/status? (so se algum programa carregado os le)

// Algum programa le MAC/status? (FSEQ/FSAND/FSOR/FMEQ/FMAND/FMOR). Se nenhum le, o calculo e dispensavel.
static bool um_scan_flag_readers(const uint8_t* micro, uint32_t pairs)
{
    const uint32_t* m = reinterpret_cast<const uint32_t*>(micro);

    for (uint32_t i = 0; i < pairs; i++)
    {
        if (m[i * 2 + 1] & 0x80000000u)
            continue;   // bit I: a palavra lower e um float

        const uint32_t op = m[i * 2] >> 25;

        if (op == 0x14 || op == 0x16 || op == 0x17 || op == 0x18 || op == 0x1A || op == 0x1B)
            return true;
    }

    return false;
}

// Flags MAC/status a partir dos resultados exatos (antes de empacotar) das faixas escritas.
static void um_flags(Ctx& c, const double* r, int dest)
{
    if (!g_um_flags)
        return;

    uint32_t sign = 0, zero = 0, unf = 0, ovf = 0;

    for (int l = 0; l < 4; l++)
    {
        const uint32_t m = 8u >> l;

        if (!(dest & (int)m))
            continue;

        const double v = r[l];
        const double a = std::fabs(v);

        if (a > 3.4028234663852886e38) ovf |= m;
        else if (a != 0.0 && a < 1.1754943508222875e-38) { unf |= m; zero |= m; }
        else if (a == 0.0) zero |= m;

        if (std::signbit(v)) sign |= m;
    }

    // MAC: Z[3:0], S[7:4], U[11:8], O[15:12]; lane x=3 ... w=0.
    // FMAND reads these bit positions directly (including the UI sprite clipper).
    const uint32_t mac = zero | (sign << 4) | (unf << 8) | (ovf << 12);
    const uint32_t st = ((mac & 0x000Fu) ? 1u : 0u) | ((mac & 0x00F0u) ? 2u : 0u) | ((mac & 0x0F00u) ? 4u : 0u) | ((mac & 0xF000u) ? 8u : 0u);
    c.vi[17] = mac;
    c.vi[16] = (c.vi[16] & 0xFF0u) | st | (st << 6);
}

// Caminho rapido (sem flags): aritmetica em float com a semantica do VU (denormal vira zero, Inf/NaN viram +-FLT_MAX).
// Difere do caminho exato por no maximo 1 ulp (arredonda em vez de truncar).
static inline float um_f(uint32_t b)
{
    const uint32_t e = b & 0x7F800000u;

    if (e == 0) b &= 0x80000000u;
    else if (e == 0x7F800000u) b = (b & 0x80000000u) | 0x7F7FFFFFu;

    float f;
    std::memcpy(&f, &b, 4);
    return f;
}

static inline uint32_t um_p(float r)
{
    uint32_t b;
    std::memcpy(&b, &r, 4);
    const uint32_t e = b & 0x7F800000u;

    if (e == 0) return b & 0x80000000u;
    if (e == 0x7F800000u) return (b & 0x80000000u) | 0x7F7FFFFFu;
    return b;
}

template <int fixed_op = -1, int fixed_src = -1>
static void um_arith_fast(Ctx& c, int runtime_op, int runtime_src, int dest, int fd, int fs, int ft, int to_acc)
{
    const int op = fixed_op >= 0 ? fixed_op : runtime_op;
    const int src = fixed_src >= 0 ? fixed_src : runtime_src;
    float bq = 0.0f;

    if (src == VU_SRC_Q) bq = um_f(c.vi[22]);
    else if (src == VU_SRC_I) bq = um_f(c.vi[21]);
    else if (src >= VU_SRC_X && src <= VU_SRC_W) bq = um_f(c.vf[ft][src - VU_SRC_X]);

    uint32_t out[4];

    for (int l = 0; l < 4; l++)
    {
        if (!(dest & (8 >> l)))
            continue;

        const uint32_t ab = c.vf[fs][l];
        const float a = um_f(ab);
        const float b = src == VU_SRC_VEC ? um_f(c.vf[ft][l]) : bq;

        switch (op)
        {
            case VU_ADD:  out[l] = um_p(a + b); break;
            case VU_SUB:  out[l] = um_p(a - b); break;
            case VU_MUL:  out[l] = um_p(a * b); break;
            case VU_MADD: out[l] = um_p(um_f(c.vacc[l]) + a * b); break;
            case VU_MSUB: out[l] = um_p(um_f(c.vacc[l]) - a * b); break;
            case VU_MAX:  out[l] = a > b ? ab : (src == VU_SRC_VEC ? c.vf[ft][l] : um_p(b)); break;
            default:      out[l] = a < b ? ab : (src == VU_SRC_VEC ? c.vf[ft][l] : um_p(b)); break;
        }
    }

    for (int l = 0; l < 4; l++)
    {
        if (!(dest & (8 >> l))) continue;
        if (to_acc) c.vacc[l] = out[l];
        else if (fd != 0) c.vf[fd][l] = out[l];
    }
}

// Mesmo calculo em double e mesmo empacotamento nos dois caminhos.
template <bool masked, int fixed_op = -1, int fixed_src = -1>
static void um_arith_exact(Ctx& c, int runtime_op, int runtime_src, int dest, int fd, int fs, int ft, int to_acc)
{
    const int op = fixed_op >= 0 ? fixed_op : runtime_op;
    const int src = fixed_src >= 0 ? fixed_src : runtime_src;
    auto operand = [&](int lane) -> uint32_t
    {
        switch (src)
        {
            case VU_SRC_VEC: return c.vf[ft][lane];
            case VU_SRC_X:   return c.vf[ft][0];
            case VU_SRC_Y:   return c.vf[ft][1];
            case VU_SRC_Z:   return c.vf[ft][2];
            case VU_SRC_W:   return c.vf[ft][3];
            case VU_SRC_Q:   return c.vi[22];
            default:         return c.vi[21];
        }
    };

    // As faixas mascaradas nao escrevem VF/ACC nem participam de MAC/status.
    // Para broadcast, a mesma palavra e a mesma conversao servem a todas as faixas.
    uint32_t shared = 0;
    double broadcast = 0.0;
    if (masked && src != VU_SRC_VEC)
    {
        shared = operand(0);
        broadcast = ee_in(shared);
    }

    double res[4];
    uint32_t out[4];
    for (int l = 0; l < 4; l++)
    {
        if (masked && !(dest & (8 >> l)))
            continue;

        const uint32_t ab = c.vf[fs][l];
        const uint32_t bb = masked && src != VU_SRC_VEC ? shared : operand(l);
        const double a = ee_in(ab);
        const double b = masked && src != VU_SRC_VEC ? broadcast : ee_in(bb);
        double r;

        switch (op)
        {
            case VU_ADD:  r = a + b; break;
            case VU_SUB:  r = a - b; break;
            case VU_MUL:  r = a * b; break;
            case VU_MADD: r = ee_in(c.vacc[l]) + a * b; break;
            case VU_MSUB: r = ee_in(c.vacc[l]) - a * b; break;
            case VU_MAX:  r = a > b ? a : b; break;
            default:      r = a < b ? a : b; break;
        }

        res[l] = r;
        out[l] = op == VU_MAX ? (a > b ? ab : bb) : op == VU_MIN ? (a < b ? ab : bb) : ee_pack(r);
    }

    for (int l = 0; l < 4; l++)
    {
        if (!(dest & (8 >> l))) continue;
        if (to_acc) c.vacc[l] = out[l];
        else if (fd != 0) c.vf[fd][l] = out[l];
    }

    um_flags(c, res, dest);
}

static void um_arith(Ctx& c, int op, int src, int dest, int fd, int fs, int ft, int to_acc, bool optimize)
{
    if (!g_um_flags)
        um_arith_fast(c, op, src, dest, fd, fs, ft, to_acc);
    else if (optimize)
        um_arith_exact<true>(c, op, src, dest, fd, fs, ft, to_acc);
    else
        um_arith_exact<false>(c, op, src, dest, fd, fs, ft, to_acc);
}

static void um_opmula(Ctx& c, int fs, int ft)
{
    double r[4] = { ee_in(c.vf[fs][1]) * ee_in(c.vf[ft][2]), ee_in(c.vf[fs][2]) * ee_in(c.vf[ft][0]), ee_in(c.vf[fs][0]) * ee_in(c.vf[ft][1]), 0.0 };
    for (int l = 0; l < 3; l++) c.vacc[l] = ee_pack(r[l]);
    um_flags(c, r, 0xE);
}

static void um_opmsub(Ctx& c, int fd, int fs, int ft)
{
    double r[4] = { ee_in(c.vacc[0]) - ee_in(c.vf[fs][1]) * ee_in(c.vf[ft][2]),
                    ee_in(c.vacc[1]) - ee_in(c.vf[fs][2]) * ee_in(c.vf[ft][0]),
                    ee_in(c.vacc[2]) - ee_in(c.vf[fs][0]) * ee_in(c.vf[ft][1]), 0.0 };
    if (fd != 0) for (int l = 0; l < 3; l++) c.vf[fd][l] = ee_pack(r[l]);
    um_flags(c, r, 0xE);
}

struct UmLowerWrite { int vf = 0; int mask = 0; uint32_t v[4] = {}; };   // escrita de VF adiada (o upper le os valores antigos)

static void um_set_vf(UmLowerWrite& w, int reg, int mask, const uint32_t* v)
{
    if (reg == 0) return;
    w.vf = reg; w.mask = mask;
    for (int l = 0; l < 4; l++) w.v[l] = v[l];
}

static inline int um_first_lane(int dest) { return (dest & 8) ? 0 : (dest & 4) ? 1 : (dest & 2) ? 2 : 3; }


// Estado, memorias e saida do VU1 (definidos antes do DMA).
void vu1_xgkick(uint32_t qaddr);
extern uint32_t g_vif1_top, g_vif1_itop;

// Instrucoes lower exclusivas do VU1: MFP, XTOP, XITOP, XGKICK e a EFU (resultado em P = vi[23]).
static void um_vu1_special(VuUnit& u, Ctx& c, UmLowerWrite& lw, unsigned idx, int it, int is, int dest, int fsf)
{
    (void)u;
    const double x = ee_in(c.vf[is][0]), y = ee_in(c.vf[is][1]), z = ee_in(c.vf[is][2]), w = ee_in(c.vf[is][3]);
    const double s = ee_in(c.vf[is][fsf]);
    const double big = 3.4028234663852886e38;
    double r = 0.0;

    switch (idx)
    {
        case 100: { uint32_t v = c.vi[23]; uint32_t t[4] = { v, v, v, v }; um_set_vf(lw, it, dest, t); return; }   // MFP
        case 104: if (it != 0) c.vi[it] = g_vif1_top & 0x3FFu; return;                                          // XTOP
        case 105: if (it != 0) c.vi[it] = g_vif1_itop & 0x3FFu; return;                                         // XITOP
        case 108: vu1_xgkick(c.vi[is]); return;                                                                 // XGKICK
        case 112: r = x * x + y * y + z * z; break;                                                              // ESADD
        case 113: { double d = x * x + y * y + z * z; r = d != 0.0 ? 1.0 / d : big; break; }                    // ERSADD
        case 114: r = std::sqrt(x * x + y * y + z * z); break;                                                   // ELENG
        case 115: { double d = std::sqrt(x * x + y * y + z * z); r = d != 0.0 ? 1.0 / d : big; break; }        // ERLENG
        case 116: r = x != 0.0 ? std::atan(y / x) : (y < 0 ? -1.5707963267948966 : 1.5707963267948966); break; // EATANxy
        case 117: r = x != 0.0 ? std::atan(z / x) : (z < 0 ? -1.5707963267948966 : 1.5707963267948966); break; // EATANxz
        case 118: r = x + y + z + w; break;                                                                      // ESUM
        case 120: r = std::sqrt(std::fabs(s)); break;                                                            // ESQRT
        case 121: { double d = std::sqrt(std::fabs(s)); r = d != 0.0 ? 1.0 / d : big; break; }                  // ERSQRT
        case 122: r = s != 0.0 ? 1.0 / s : big; break;                                                           // ERCPR
        case 124: r = std::sin(s); break;                                                                        // ESIN
        case 125: r = std::atan(s); break;                                                                       // EATAN
        case 126: r = std::exp(-s); break;                                                                       // EEXP
        default: return;                                                                                         // 123 WAITP: nada
    }

    c.vi[23] = ee_pack(r);
}

struct VuAotProgram
{
    const uint32_t* image;
    unsigned (*run)(VuUnit&, uint32_t, unsigned, bool);
};

// Os includes AOT ja trazem operacao e fonte como constantes. Use-as
// nos mesmos calculos para que o compilador elimine os switches.
#if defined(PS2_VU1_AOT_SPECIALIZE)
template <int op, int src>
static void um_arith_aot(Ctx& c, int dest, int fd, int fs, int ft, int to_acc, bool optimize)
{
    if (!g_um_flags)
        um_arith_fast<op, src>(c, op, src, dest, fd, fs, ft, to_acc);
    else if (optimize)
        um_arith_exact<true, op, src>(c, op, src, dest, fd, fs, ft, to_acc);
    else
        um_arith_exact<false, op, src>(c, op, src, dest, fd, fs, ft, to_acc);
}
#define um_arith(c, op, src, dest, fd, fs, ft, to_acc, optimize) \
    um_arith_aot<op, src>(c, dest, fd, fs, ft, to_acc, optimize)
#endif

// Rotinas VU1 integradas pelo recompilador, sem includes externos.
/* SH_EMBEDDED_VU1_PROGRAMS */

#if defined(PS2_VU1_AOT_SPECIALIZE)
#undef um_arith
#endif

static int vu_aot_target(uint32_t entry)
{
    switch (entry)
    {
        case 0x0350u: return 0;
        case 0x0078u: return 1;
        case 0x0658u: return 2;
        default: return -1;
    }
}

struct VuAotSelection
{
    const VuAotProgram* program = nullptr;
    // Nulo indica que toda a imagem coincide com a captura.
    const uint8_t* prefix = nullptr;
};

static VuAotSelection vu_aot_match(const VuUnit& u, int target)
{
    if (!u.vu1 || !g_vu_aot_enabled || g_vu_aot_capture || target < 0)
        return {};

    static const VuAotProgram* const programs[] = {
        &vu_aot_0350,
        &vu_aot_0078,
        &vu_aot_0658,
    };
    const VuAotProgram* program = programs[target];
    if (program == nullptr)
    {
        ++g_prof_aot_missing;
        return {};
    }

    struct Validation
    {
        uint32_t image[4096];
        uint8_t prefix[2048];
        bool ready = false;
        bool exact = false;
    };
    // Guarda as duas ultimas imagens por entrada para reaproveitar a
    // validacao quando o jogo alterna entre dois overlays.
    static Validation validations[3][2];
    static unsigned last_slot[3] = {};
    static uint64_t last_revision[3] = {};
    unsigned slot = last_slot[target];
    if (g_vu_aot_revision_cache && u.micro == g_vu1_micro &&
        validations[target][slot].ready && last_revision[target] == g_vu1_micro_revision)
    {
        ++g_prof_aot_revision_hits;
        ++g_prof_aot_match;
        return {program, nullptr};
    }

    auto matches = [&](const Validation& candidate)
    {
        return candidate.ready && std::memcmp(u.micro, candidate.image, sizeof candidate.image) == 0;
    };
    if (!matches(validations[target][slot]))
    {
        slot ^= 1u;
    }
    Validation& cache = validations[target][slot];
    // Uma imagem nova ainda precisa passar pela comparacao exata.
    if (slot != last_slot[target] && !matches(cache))
    {
        std::memcpy(cache.image, u.micro, sizeof cache.image);
        cache.exact = std::memcmp(cache.image, program->image, sizeof cache.image) == 0;
        if (!cache.exact)
        {
            unsigned consecutive = 0;
            for (int pc = 2047; pc >= 0; --pc)
            {
                const unsigned word = (unsigned)pc * 2;
                const bool same = cache.image[word] == program->image[word] &&
                    cache.image[word + 1] == program->image[word + 1];
                consecutive = same ? std::min(consecutive + 1, 8u) : 0;
                cache.prefix[pc] = (uint8_t)consecutive;
            }
        }
        cache.ready = true;
    }

    // So reutilize validacoes exatas da micromemoria real. Imagens
    // parciais continuam no caminho anterior, inclusive apos overlays.
    last_revision[target] = u.micro == g_vu1_micro && cache.exact ? g_vu1_micro_revision : 0;
    last_slot[target] = slot;
    if (cache.exact)
    {
        ++g_prof_aot_match;
        return {program, nullptr};
    }
    ++g_prof_aot_mismatch;
    return {program, cache.prefix};
}

// Emite apenas pares que nao alteram fluxo, Q pendente ou o GIF.
static bool vu_aot_emit_pair(std::string& code, uint32_t pc, uint32_t lo, uint32_t hi)
{
    if ((hi & 0x78000000u) || (hi == 0 && lo == 0))
        return false;

    std::string lower, upper;
    bool deferred = false;
    const unsigned it = (lo >> 16) & 31u, is = (lo >> 11) & 31u, id = (lo >> 6) & 31u;
    const unsigned ld = (lo >> 21) & 15u;
    char line[256];

    if (hi & 0x80000000u)
    {
        std::snprintf(line, sizeof line, "c.vi[21] = 0x%08Xu;\n", lo);
        lower = line;
    }
    else if ((lo >> 25) == 0 || (lo >> 25) == 1)
    {
        const int imm = (int)(lo & 0x7FFu) - ((lo & 0x400u) ? 2048 : 0);
        if ((lo >> 25) == 0)
        {
            std::snprintf(line, sizeof line, "um_set_vf(lw, %u, %u, um_q(u, c.vi[%u] + (uint32_t)(%d)));\n",
                it, ld, is, imm);
            deferred = true;
        }
        else
        {
            std::snprintf(line, sizeof line, "auto qaddr = c.vi[%u] + (uint32_t)(%d); auto* q = um_q(u, qaddr);\n",
                it, imm);
            lower = line;
            for (unsigned l = 0; l < 4; ++l)
                if (ld & (8u >> l))
                {
                    std::snprintf(line, sizeof line, "q[%u] = c.vf[%u][%u];\n", l, is, l);
                    lower += line;
                }
            if (ld & 8u)
            {
                std::snprintf(line, sizeof line, "um_trace_header_write(u, qaddr, \"SQ\", %u, q[0]);\n", pc);
                lower += line;
            }
            line[0] = 0;
        }
        lower += line;
    }
    else if ((lo >> 25) == 8 || (lo >> 25) == 9)
    {
        const unsigned imm = ((lo >> 10) & 0x7800u) | (lo & 0x7FFu);
        if (it != 0)
        {
            std::snprintf(line, sizeof line, "c.vi[%u] = (c.vi[%u] %c %uu) & 0xFFFFu;\n",
                it, is, (lo >> 25) == 8 ? '+' : '-', imm);
            lower = line;
        }
    }
    else if ((lo >> 25) == 0x40)
    {
        const unsigned funct = lo & 63u;
        const unsigned idx = (id << 2) | (funct & 3u);
        if (funct == 0x30 || funct == 0x31 || funct == 0x34 || funct == 0x35)
        {
            const char* name = funct == 0x30 ? "vu_iadd" : funct == 0x31 ? "vu_isub"
                : funct == 0x34 ? "vu_iand" : "vu_ior";
            std::snprintf(line, sizeof line, "%s(c, %u, %u, %u);\n", name, id, is, it);
            lower = line;
        }
        else if (funct >= 0x3C && idx == 48)
        {
            std::snprintf(line, sizeof line, "um_set_vf(lw, %u, %u, c.vf[%u]);\n", it, ld, is);
            lower = line;
            deferred = true;
        }
        else if (funct >= 0x3C && (idx == 52 || idx == 54))
        {
            if (idx == 54 && is != 0)
            {
                std::snprintf(line, sizeof line, "c.vi[%u] = (c.vi[%u] - 1u) & 0xFFFFu;\n", is, is);
                lower += line;
            }
            std::snprintf(line, sizeof line, "um_set_vf(lw, %u, %u, um_q(u, c.vi[%u]));\n", it, ld, is);
            lower += line;
            if (idx == 52 && is != 0)
            {
                std::snprintf(line, sizeof line, "c.vi[%u] = (c.vi[%u] + 1u) & 0xFFFFu;\n", is, is);
                lower += line;
            }
            deferred = true;
        }
        else
            return false;
    }
    else
        return false;

    const unsigned f = hi & 63u, d = (hi >> 21) & 15u, ft = (hi >> 16) & 31u;
    const unsigned fs = (hi >> 11) & 31u, fd = (hi >> 6) & 31u;
    const unsigned idx = (fd << 2) | (f & 3u);
    static const int bc_ops[] = {VU_ADD,VU_SUB,VU_MADD,VU_MSUB,VU_MAX,VU_MIN,VU_MUL};
    static const int ops[] = {VU_MUL,VU_MAX,VU_MUL,VU_MIN,VU_ADD,VU_MADD,VU_ADD,VU_MADD,
        VU_SUB,VU_MSUB,VU_SUB,VU_MSUB,VU_ADD,VU_MADD,VU_MUL,VU_MAX,VU_SUB,VU_MSUB,0,VU_MIN};
    static const int shift[] = {0,4,12,15};
    int op = -1, src = VU_SRC_VEC;
    bool acc = false;
    if (f < 0x1C)
    {
        op = bc_ops[f >> 2];
        src = VU_SRC_X + (int)(f & 3u);
    }
    else if (f == 0x2E)
    {
        std::snprintf(line, sizeof line, "um_opmsub(c, %u, %u, %u);\n", fd, fs, ft);
        upper = line;
    }
    else if (f < 0x30)
    {
        op = ops[f - 0x1C];
        if (f < 0x28)
            src = (f == 0x1C || (f >= 0x20 && ((f - 0x20) & 2u) == 0)) ? VU_SRC_Q : VU_SRC_I;
    }
    else if (f >= 0x3C && idx < 16)
    {
        op = bc_ops[idx >> 2]; src = VU_SRC_X + (int)(idx & 3u); acc = true;
    }
    else if (f >= 0x3C && idx < 24)
    {
        std::snprintf(line, sizeof line, "%s(c, %u, %u, %u, %d);\n",
            idx < 20 ? "vu_itof" : "vu_ftoi", d, ft, fs, shift[idx & 3u]);
        upper = line;
    }
    else if (f >= 0x3C && idx < 28)
    {
        op = VU_MUL; src = VU_SRC_X + (int)(idx - 24); acc = true;
    }
    else if (f >= 0x3C && (idx == 28 || idx == 30 || (idx >= 32 && idx <= 45 && idx != 43)))
    {
        static const int acc_ops[] = {VU_ADD,VU_MADD,VU_ADD,VU_MADD,VU_SUB,VU_MSUB,VU_SUB,VU_MSUB,
            VU_ADD,VU_MADD,VU_MUL,0,VU_SUB,VU_MSUB};
        op = idx < 32 ? VU_MUL : acc_ops[idx - 32];
        src = idx >= 40 ? VU_SRC_VEC : (idx == 30 || (idx >= 32 && (idx & 2u))) ? VU_SRC_I : VU_SRC_Q;
        acc = true;
    }
    else if (f >= 0x3C && (idx == 29 || idx == 31 || idx == 46))
    {
        if (idx == 29)
            std::snprintf(line, sizeof line, "vu_abs(c, %u, %u, %u);\n", d, ft, fs);
        else
            std::snprintf(line, sizeof line, "%s(c, %u, %u);\n", idx == 31 ? "vu_clipw" : "um_opmula", fs, ft);
        upper = line;
    }
    else if (f >= 0x3C && idx == 47)
    {
        // NOP upper.
    }
    else
        return false;

    if (op >= 0)
    {
        std::snprintf(line, sizeof line, "um_arith(c, %d, %d, %u, %u, %u, %u, %u, optimize);\n",
            op, src, d, acc ? 0u : fd, fs, ft, (unsigned)acc);
        upper = line;
    }

    // Lower le/escreve antes do upper; uma escrita VF adiada so entra depois.
    code += "{\n";
    if (deferred) code += "UmLowerWrite lw;\n";
    code += lower + upper;
    if (deferred)
        code += "if (lw.vf != 0) for (int lane = 0; lane < 4; ++lane)"
                " if (lw.mask & (8 >> lane)) c.vf[lw.vf][lane] = lw.v[lane];\n";
    code += "c.vf[0][0] = c.vf[0][1] = c.vf[0][2] = 0; c.vf[0][3] = 0x3F800000u; c.vi[0] = 0;\n}\n";
    return true;
}

static bool vu_aot_generate(const uint32_t* image, uint32_t entry, const uint32_t* visits)
{
    char name[48], tmp[56], suffix[8];
    std::snprintf(suffix, sizeof suffix, "%04x", entry);
    std::snprintf(name, sizeof name, "ps2_vu1_aot_%s.inc", suffix);
    std::snprintf(tmp, sizeof tmp, "%s.tmp", name);
    FILE* out = std::fopen(tmp, "wb");
    if (out == nullptr)
    {
        std::fprintf(stderr, "  [VU1 AOT] falha ao abrir %s\n", tmp);
        return false;
    }

    std::fprintf(out, "// Gerado pelo Ring of Judgment para a VU1.\nstatic const uint32_t vu_aot_image_%s[] = {\n", suffix);
    for (unsigned i = 0; i < 4096; ++i)
        std::fprintf(out, "0x%08Xu,%s", image[i], (i & 7u) == 7 ? "\n" : " ");
    std::fprintf(out, "};\nstatic unsigned vu_aot_run_%s(VuUnit& u, uint32_t pc, unsigned budget, bool optimize)\n"
        "{\nCtx& c = *u.r;\nswitch (pc) {\n", suffix);

    unsigned blocks = 0;
    for (unsigned pc = 0; pc < 2048; ++pc)
    {
        if (visits[pc] == 0)
            continue;
        std::string code;
        unsigned n = 0;
        for (; n < 8 && pc + n < 2048 && visits[pc + n] != 0; ++n)
        {
            const unsigned k = (pc + n) * 2;
            if (!vu_aot_emit_pair(code, pc + n, image[k], image[k + 1]))
                break;
        }
        if (n < 2)
            continue;
        std::fprintf(out, "case %u: { if (budget < %u) return 0;\n%sreturn %u; }\n", pc, n, code.c_str(), n);
        ++blocks;
    }
    std::fprintf(out, "default: return 0;\n}\n}\nstatic const VuAotProgram vu_aot_%s"
        "{vu_aot_image_%s, vu_aot_run_%s};\n", suffix, suffix, suffix);
    const bool write_ok = std::ferror(out) == 0;
    const bool close_ok = std::fclose(out) == 0;
    if (!write_ok || !close_ok)
    {
        std::remove(tmp);
        std::fprintf(stderr, "  [VU1 AOT] falha ao gravar %s\n", tmp);
        return false;
    }
    // No Windows, um arquivo anterior pode impedir a troca. Preserve-o;
    // o temporario fica disponivel para o usuario escolher a captura.
    if (std::rename(tmp, name) != 0)
    {
        std::fprintf(stderr, "  [VU1 AOT] falha ao publicar %s; captura em %s\n", name, tmp);
        return false;
    }
    std::fprintf(stderr, "  [VU1 AOT] entrada=0x%04X blocos=%u arquivo=%s\n", entry, blocks, name);
    return true;
}

static unsigned vu_run(VuUnit& u, uint32_t byte_addr)
{
    Ctx& c = *u.r;
    const bool optimize = u.vu1 && g_vu1_fast;
    auto arith = [&](int op, int src, int dest, int fd, int fs, int ft, int to_acc)
    {
        um_arith(c, op, src, dest, fd, fs, ft, to_acc, optimize);
    };
    struct DbgVuMark { DbgVuMark(uint32_t a) { g_dbg_vu[0] = a; g_dbg_vu[1] = 0; g_dbg_vu[2] = 1; } ~DbgVuMark() { g_dbg_vu[2] = 0; } } dbg_vu_mark(byte_addr);
    const uint32_t* mem = reinterpret_cast<const uint32_t*>(u.micro);
    uint32_t pc = (byte_addr >> 3) & u.imask;
    int pend_target = -1, pend_cd = 0;
    bool end_pending = false;
    uint32_t q_val = 0;
    int q_cd = 0;
    unsigned steps = 0;
    // Programa que ja estourou o limite antes (dado de entrada invalido) volta a rodar com um limite 10x menor:
    // cada estouro de 300000 pares custa ~15 ms de CPU, e o mesmo programa estoura a cada quadro.
    // PS2_VU_MAXSTEPS=n fixa o limite.
    static unsigned s_aborts[2][2048];
    static const unsigned s_env_max = std::getenv("PS2_VU_MAXSTEPS") ? (unsigned)std::atoi(std::getenv("PS2_VU_MAXSTEPS")) : 0u;
    unsigned& abort_count = s_aborts[u.vu1 ? 1 : 0][(byte_addr >> 3) & 0x7FFu];
    const unsigned kMaxSteps = s_env_max != 0 ? s_env_max : (abort_count >= 2 ? 20000u : 300000u);
    const unsigned kTraceFrom = kMaxSteps > 64u ? kMaxSteps - 64u : 0u;
    struct UmRec { uint32_t pc, lo, hi; } ring[64];
    unsigned ring_n = 0;
    const int target = u.vu1 ? vu_aot_target(byte_addr) : -1;
    const VuAotSelection aot = vu_aot_match(u, target);
    uint64_t aot_pairs = 0;
    static uint32_t visits[2048];
    static bool captured[3] = {};
    const bool capture = target >= 0 && !captured[target] &&
        (!g_vu_aot_capture_0658_only || target == 2) &&
        g_vu_aot_armed.load(std::memory_order_relaxed);
    if (capture)
        std::fill(std::begin(visits), std::end(visits), 0u);

    c.vf[0][0] = 0; c.vf[0][1] = 0; c.vf[0][2] = 0; c.vf[0][3] = 0x3F800000u;

    for (; steps < kMaxSteps; steps++)
    {
        if ((steps & 0x3FFu) == 0)
            g_dbg_vu[1] = steps;

        if (aot.program != nullptr && q_cd == 0 && pend_cd == 0 && !end_pending && steps < kTraceFrom)
        {
            unsigned budget = std::min(kTraceFrom - steps, 1024u - (steps & 0x3FFu));
            // O include antigo tambem verifica o tamanho do bloco antes de
            // executar. Limite o orcamento aos pares de codigo identicos.
            if (aot.prefix != nullptr)
                budget = std::min(budget, (unsigned)aot.prefix[pc]);
            const unsigned count = budget >= 2 ? aot.program->run(u, pc, budget, optimize) : 0;
            if (count != 0)
            {
                pc = (pc + count) & u.imask;
                steps += count - 1;
                aot_pairs += count;
                continue;
            }
        }

        if (q_cd > 0 && --q_cd == 0)
            c.vi[22] = q_val;

        const uint32_t cur = pc;
        const uint32_t lo = mem[cur * 2], hi = mem[cur * 2 + 1];
        if (capture) ++visits[cur];
        pc = (pc + 1) & u.imask;

        if (steps >= kTraceFrom)
            ring[ring_n++ & 63u] = { cur, lo, hi };

        const bool was_delay = pend_cd > 0;
        const bool stop_after = end_pending;
        int new_target = -1;
        UmLowerWrite lw;

        if (hi & 0x40000000u)
            end_pending = true;

        // ---- lower ----
        if (hi & 0x80000000u)
        {
            c.vi[21] = lo;      // bit I: a palavra lower e um float imediato carregado em I
        }
        else
        {
            const int it = (lo >> 16) & 31, is = (lo >> 11) & 31, id = (lo >> 6) & 31;
            const int dest = (lo >> 21) & 15;
            const int32_t imm11 = ((int32_t)(lo << 21)) >> 21;
            const uint32_t op7 = lo >> 25;

            switch (op7)
            {
                case 0x00:   // LQ
                {
                    const uint32_t* q = um_q(u, c.vi[is] + (uint32_t)imm11);
                    um_set_vf(lw, it, dest, q);
                    break;
                }
                case 0x01:   // SQ fs, imm(it)
                {
                    uint32_t* q = um_q(u, c.vi[it] + (uint32_t)imm11);
                    for (int l = 0; l < 4; l++) if (dest & (8 >> l)) q[l] = c.vf[is][l];
                    if (dest & 8) um_trace_header_write(u, c.vi[it] + (uint32_t)imm11, "SQ", cur, q[0]);
                    break;
                }
                case 0x04:   // ILW
                {
                    const uint32_t origem = c.vi[is];
                    const uint32_t indice = (origem + (uint32_t)imm11) & u.dmask;
                    const uint32_t* q = um_q(u, origem + (uint32_t)imm11);
                    uint32_t valor = q[um_first_lane(dest)] & 0xFFFFu;

                    // TESTE A/B APENAS (PS2_VU_FLAG_TEST=1): se o bit 15 de 0x803E for
                    // realmente um marcador de pacote, experimenta executar com o contador 62.
                    // Restringe-se a ILW.x -> VI03 em tres PCs observados nos logs;
                    // nao altera os dados na memoria VU1. NUNCA habilitar como correcao final.
                    if (u.vu1 && it == 3 && dest == 8 && valor == 0x803Eu &&
                        (cur * 8u == 0x0090u || cur * 8u == 0x0368u || cur * 8u == 0x0678u) &&
                        g_vu_flag_test)
                    {
                        valor = 0x003Eu;
                        static uint64_t flags_testados = 0;
                        ++flags_testados;
                        if (flags_testados <= 32 || (flags_testados & 4095u) == 0)
                            std::fprintf(stderr, "  [VU1 TESTE FLAG] n=%llu pc=%04X D[%03X].x=%08X contador=%u (SOMENTE TESTE)\n",
                                (unsigned long long)flags_testados, cur * 8u, indice, q[um_first_lane(dest)], valor);
                    }

                    // Diagnostico sob demanda: investiga de onde vem o contador VI03 dos lacos VU1.
                    // Nao altera registradores, transferencias nem o comportamento do jogo.
                    // Ative apenas no teste com PS2_VU_COUNT_TRACE=1 (variavel presente).
                    if (u.vu1 && it == 3 && g_vu_count_trace)
                    {
                        static uint64_t amostras = 0;
                        ++amostras;
                        if (amostras <= 48 || (amostras & 2047u) == 0)
                            std::fprintf(stderr,
                                "  [VU1 CONTAGEM] inicio=0x%05X pc=0x%04X TOP=0x%03X VI%02d=0x%04X desloc=%d D[0x%03X].%c=0x%08X VI03=%u amostra=%llu\n",
                                byte_addr, cur * 8u, g_vif1_top, is, origem & 0xFFFFu,
                                imm11, indice, "xyzw"[um_first_lane(dest)], q[um_first_lane(dest)],
                                valor, (unsigned long long)amostras);
                    }

                    if (it != 0) c.vi[it] = valor;
                    break;
                }
                case 0x05:   // ISW
                {
                    uint32_t* q = um_q(u, c.vi[is] + (uint32_t)imm11);
                    for (int l = 0; l < 4; l++) if (dest & (8 >> l)) q[l] = c.vi[it] & 0xFFFFu;
                    if (dest & 8) um_trace_header_write(u, c.vi[is] + (uint32_t)imm11, "ISW", cur, q[0]);
                    break;
                }
                case 0x08: case 0x09:   // IADDIU / ISUBIU
                {
                    const uint32_t imm15 = ((lo >> 10) & 0x7800u) | (lo & 0x7FFu);
                    if (it != 0) c.vi[it] = (op7 == 0x08 ? c.vi[is] + imm15 : c.vi[is] - imm15) & 0xFFFFu;
                    break;
                }
                case 0x10: c.vi[1] = ((c.vi[18] & 0xFFFFFFu) == (lo & 0xFFFFFFu)) ? 1u : 0u; break;          // FCEQ
                case 0x11: c.vi[18] = lo & 0xFFFFFFu; break;                                                   // FCSET
                case 0x12: c.vi[1] = ((c.vi[18] & lo & 0xFFFFFFu) != 0) ? 1u : 0u; break;                     // FCAND
                case 0x13: c.vi[1] = (((c.vi[18] | lo) & 0xFFFFFFu) == 0xFFFFFFu) ? 1u : 0u; break;           // FCOR
                case 0x14: case 0x15: case 0x16: case 0x17:   // FSEQ FSSET FSAND FSOR
                {
                    const uint32_t imm12 = ((lo >> 10) & 0x800u) | (lo & 0x7FFu);
                    if (!u.vu1 && g_vu0_status_trace && op7 != 0x15 && it != 0)
                        vu0_status_record(Vu0StatusEvent::MicroReadStatus, cur * 8u, c.vi[16]);
                    if (op7 == 0x14 && it != 0) c.vi[it] = ((c.vi[16] & 0xFFFu) == imm12) ? 1u : 0u;
                    else if (op7 == 0x15) c.vi[16] = (c.vi[16] & 0x3Fu) | (imm12 & 0xFC0u);
                    else if (op7 == 0x16 && it != 0) c.vi[it] = c.vi[16] & imm12;
                    else if (op7 == 0x17 && it != 0) c.vi[it] = (c.vi[16] | imm12) & 0xFFFu;
                    break;
                }
                case 0x18: if (it != 0) c.vi[it] = (c.vi[is] == (c.vi[17] & 0xFFFFu)) ? 1u : 0u; break;        // FMEQ
                case 0x1A: if (it != 0) c.vi[it] = c.vi[is] & c.vi[17] & 0xFFFFu; break;                       // FMAND
                case 0x1B: if (it != 0) c.vi[it] = (c.vi[is] | c.vi[17]) & 0xFFFFu; break;                     // FMOR
                case 0x1C: if (it != 0) c.vi[it] = c.vi[18] & 0xFFFu; break;                                   // FCGET
                case 0x20: new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask); break;                   // B
                case 0x21:                                                                                       // BAL
                    if (it != 0) c.vi[it] = (cur + 2) & 0xFFFFu;
                    new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask);
                    break;
                case 0x24: new_target = (int)(c.vi[is] & u.imask); break;                                       // JR
                case 0x25:                                                                                       // JALR
                    if (it != 0) c.vi[it] = (cur + 2) & 0xFFFFu;
                    new_target = (int)(c.vi[is] & u.imask);
                    break;
                case 0x28: if (c.vi[it] == c.vi[is]) new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask); break;   // IBEQ
                case 0x29: if (c.vi[it] != c.vi[is]) new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask); break;   // IBNE
                case 0x2C: if ((int16_t)c.vi[is] < 0)  new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask); break; // IBLTZ
                case 0x2D: if ((int16_t)c.vi[is] > 0)  new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask); break; // IBGTZ
                case 0x2E: if ((int16_t)c.vi[is] <= 0) new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask); break; // IBLEZ
                case 0x2F: if ((int16_t)c.vi[is] >= 0) new_target = (int)((cur + 1 + (uint32_t)imm11) & u.imask); break; // IBGEZ
                case 0x40:   // lower "especial"
                {
                    const uint32_t funct = lo & 0x3Fu;
                    const int fsf = (lo >> 21) & 3, ftf = (lo >> 23) & 3;

                    if (funct == 0x30) { vu_iadd(c, id, is, it); break; }
                    if (funct == 0x31) { vu_isub(c, id, is, it); break; }
                    if (funct == 0x32) { vu_iaddi(c, it, is, ((int32_t)(lo << 21)) >> 27); break; }
                    if (funct == 0x34) { vu_iand(c, id, is, it); break; }
                    if (funct == 0x35) { vu_ior(c, id, is, it); break; }

                    if (funct < 0x3C)
                        break;

                    const unsigned idx = (((lo >> 6) & 0x1Fu) << 2) | (funct & 3u);

                    switch (idx)
                    {
                        case 48: um_set_vf(lw, it, dest, c.vf[is]); break;   // MOVE
                        case 49: { uint32_t t[4] = { c.vf[is][1], c.vf[is][2], c.vf[is][3], c.vf[is][0] }; um_set_vf(lw, it, dest, t); break; }   // MR32
                        case 52: case 54:   // LQI / LQD
                        {
                            if (idx == 54 && is != 0) c.vi[is] = (c.vi[is] - 1u) & 0xFFFFu;
                            um_set_vf(lw, it, dest, um_q(u, c.vi[is]));
                            if (idx == 52 && is != 0) c.vi[is] = (c.vi[is] + 1u) & 0xFFFFu;
                            break;
                        }
                        case 53: case 55:   // SQI / SQD fs, (it)
                        {
                            if (idx == 55 && it != 0) c.vi[it] = (c.vi[it] - 1u) & 0xFFFFu;
                            uint32_t* q = um_q(u, c.vi[it]);
                            for (int l = 0; l < 4; l++) if (dest & (8 >> l)) q[l] = c.vf[is][l];
                            if (dest & 8) um_trace_header_write(u, c.vi[it], idx == 53 ? "SQI" : "SQD", cur, q[0]);
                            if (idx == 53 && it != 0) c.vi[it] = (c.vi[it] + 1u) & 0xFFFFu;
                            break;
                        }
                        case 56: case 57: case 58:   // DIV / SQRT / RSQRT: Q chega depois de 7 (13) ciclos
                        {
                            if (q_cd > 0) { c.vi[22] = q_val; q_cd = 0; }
                            const uint32_t old_q = c.vi[22];
                            if (idx == 56) vu_div(c, is, fsf, it, ftf);
                            else if (idx == 57) vu_sqrt(c, it, ftf);
                            else vu_rsqrt(c, is, fsf, it, ftf);
                            if (!u.vu1 && g_vu0_status_trace)
                            {
                                constexpr Vu0StatusEvent events[] = {
                                    Vu0StatusEvent::MicroDiv, Vu0StatusEvent::MicroSqrt, Vu0StatusEvent::MicroRsqrt
                                };
                                vu0_status_record(events[idx - 56], cur * 8u, c.vi[16]);
                            }
                            q_val = c.vi[22];
                            c.vi[22] = old_q;
                            q_cd = idx == 58 ? 13 : 7;
                            break;
                        }
                        case 59: if (q_cd > 0) { c.vi[22] = q_val; q_cd = 0; } break;   // WAITQ
                        case 60: vu_mtir(c, it, is, fsf); break;                          // MTIR
                        case 61: { uint32_t v = (uint32_t)(int32_t)(int16_t)c.vi[is]; uint32_t t[4] = { v, v, v, v }; um_set_vf(lw, it, dest, t); break; }   // MFIR
                        case 62: { const uint32_t* q = um_q(u, c.vi[is]); if (it != 0) c.vi[it] = q[um_first_lane(dest)] & 0xFFFFu; break; }   // ILWR
                        case 63: { uint32_t* q = um_q(u, c.vi[is]); for (int l = 0; l < 4; l++) if (dest & (8 >> l)) q[l] = c.vi[it] & 0xFFFFu; if (dest & 8) um_trace_header_write(u, c.vi[is], "ISWR", cur, q[0]); break; }   // ISWR
                        case 64:   // RNEXT
                        {
                            uint32_t r = c.vi[20];
                            const uint32_t b = ((r >> 4) & 1u) ^ ((r >> 22) & 1u);
                            c.vi[20] = (((r << 1) & 0x7FFFFFu) | b) | 0x3F800000u;
                            uint32_t v = c.vi[20]; uint32_t t[4] = { v, v, v, v }; um_set_vf(lw, it, dest, t);
                            break;
                        }
                        case 65: { uint32_t v = c.vi[20]; uint32_t t[4] = { v, v, v, v }; um_set_vf(lw, it, dest, t); break; }   // RGET
                        case 66: c.vi[20] = 0x3F800000u | (c.vf[is][fsf] & 0x7FFFFFu); break;                                      // RINIT
                        case 67: c.vi[20] = 0x3F800000u | ((c.vi[20] ^ c.vf[is][fsf]) & 0x7FFFFFu); break;                         // RXOR
                        default:
                            if (u.vu1) um_vu1_special(u, c, lw, idx, it, is, dest, fsf);
                            break;
                    }

                    break;
                }
                default: break;
            }
        }

        // ---- upper ----
        {
            const uint32_t funct = hi & 0x3Fu;
            const int dest = (hi >> 21) & 15, ft = (hi >> 16) & 31, fs = (hi >> 11) & 31, fd = (hi >> 6) & 31;

            if (funct < 0x1C)
            {
                static const int ops[7] = { VU_ADD, VU_SUB, VU_MADD, VU_MSUB, VU_MAX, VU_MIN, VU_MUL };
                arith(ops[funct >> 2], VU_SRC_X + (int)(funct & 3), dest, fd, fs, ft, 0);
            }
            else if (funct < 0x30)
            {
                switch (funct)
                {
                    case 0x1C: arith(VU_MUL,  VU_SRC_Q, dest, fd, fs, ft, 0); break;
                    case 0x1D: arith(VU_MAX,  VU_SRC_I, dest, fd, fs, ft, 0); break;
                    case 0x1E: arith(VU_MUL,  VU_SRC_I, dest, fd, fs, ft, 0); break;
                    case 0x1F: arith(VU_MIN,  VU_SRC_I, dest, fd, fs, ft, 0); break;
                    case 0x20: arith(VU_ADD,  VU_SRC_Q, dest, fd, fs, ft, 0); break;
                    case 0x21: arith(VU_MADD, VU_SRC_Q, dest, fd, fs, ft, 0); break;
                    case 0x22: arith(VU_ADD,  VU_SRC_I, dest, fd, fs, ft, 0); break;
                    case 0x23: arith(VU_MADD, VU_SRC_I, dest, fd, fs, ft, 0); break;
                    case 0x24: arith(VU_SUB,  VU_SRC_Q, dest, fd, fs, ft, 0); break;
                    case 0x25: arith(VU_MSUB, VU_SRC_Q, dest, fd, fs, ft, 0); break;
                    case 0x26: arith(VU_SUB,  VU_SRC_I, dest, fd, fs, ft, 0); break;
                    case 0x27: arith(VU_MSUB, VU_SRC_I, dest, fd, fs, ft, 0); break;
                    case 0x28: arith(VU_ADD,  VU_SRC_VEC, dest, fd, fs, ft, 0); break;
                    case 0x29: arith(VU_MADD, VU_SRC_VEC, dest, fd, fs, ft, 0); break;
                    case 0x2A: arith(VU_MUL,  VU_SRC_VEC, dest, fd, fs, ft, 0); break;
                    case 0x2B: arith(VU_MAX,  VU_SRC_VEC, dest, fd, fs, ft, 0); break;
                    case 0x2C: arith(VU_SUB,  VU_SRC_VEC, dest, fd, fs, ft, 0); break;
                    case 0x2D: arith(VU_MSUB, VU_SRC_VEC, dest, fd, fs, ft, 0); break;
                    case 0x2E: um_opmsub(c, fd, fs, ft); break;
                    default:   arith(VU_MIN,  VU_SRC_VEC, dest, fd, fs, ft, 0); break;   // 0x2F MINI
                }
            }
            else if (funct >= 0x3C)
            {
                const unsigned idx = (((hi >> 6) & 0x1Fu) << 2) | (funct & 3u);

                if (idx < 16)
                {
                    static const int ops[4] = { VU_ADD, VU_SUB, VU_MADD, VU_MSUB };
                    arith(ops[idx >> 2], VU_SRC_X + (int)(idx & 3), dest, 0, fs, ft, 1);
                }
                else if (idx < 20) { static const int n[4] = { 0, 4, 12, 15 }; vu_itof(c, dest, ft, fs, n[idx - 16]); }
                else if (idx < 24) { static const int n[4] = { 0, 4, 12, 15 }; vu_ftoi(c, dest, ft, fs, n[idx - 20]); }
                else if (idx < 28) arith(VU_MUL, VU_SRC_X + (int)(idx - 24), dest, 0, fs, ft, 1);
                else switch (idx)
                {
                    case 28: arith(VU_MUL,  VU_SRC_Q, dest, 0, fs, ft, 1); break;   // MULAq
                    case 29: vu_abs(c, dest, ft, fs); break;
                    case 30: arith(VU_MUL,  VU_SRC_I, dest, 0, fs, ft, 1); break;   // MULAi
                    case 31: vu_clipw(c, fs, ft); break;
                    case 32: arith(VU_ADD,  VU_SRC_Q, dest, 0, fs, ft, 1); break;
                    case 33: arith(VU_MADD, VU_SRC_Q, dest, 0, fs, ft, 1); break;
                    case 34: arith(VU_ADD,  VU_SRC_I, dest, 0, fs, ft, 1); break;
                    case 35: arith(VU_MADD, VU_SRC_I, dest, 0, fs, ft, 1); break;
                    case 36: arith(VU_SUB,  VU_SRC_Q, dest, 0, fs, ft, 1); break;
                    case 37: arith(VU_MSUB, VU_SRC_Q, dest, 0, fs, ft, 1); break;
                    case 38: arith(VU_SUB,  VU_SRC_I, dest, 0, fs, ft, 1); break;
                    case 39: arith(VU_MSUB, VU_SRC_I, dest, 0, fs, ft, 1); break;
                    case 40: arith(VU_ADD,  VU_SRC_VEC, dest, 0, fs, ft, 1); break;
                    case 41: arith(VU_MADD, VU_SRC_VEC, dest, 0, fs, ft, 1); break;
                    case 42: arith(VU_MUL,  VU_SRC_VEC, dest, 0, fs, ft, 1); break;
                    case 44: arith(VU_SUB,  VU_SRC_VEC, dest, 0, fs, ft, 1); break;
                    case 45: arith(VU_MSUB, VU_SRC_VEC, dest, 0, fs, ft, 1); break;
                    case 46: um_opmula(c, fs, ft); break;
                    default: break;   // 47 NOP
                }
            }
        }

        // escrita adiada do lower (LQ, MOVE...): depois do upper, para ele ter lido os valores antigos
        if (optimize && lw.vf != 0 && lw.mask == 15)
            std::memcpy(c.vf[lw.vf], lw.v, sizeof(lw.v));
        else if (lw.vf != 0)
            for (int l = 0; l < 4; l++)
                if (lw.mask & (8 >> l)) c.vf[lw.vf][l] = lw.v[l];

        c.vf[0][0] = 0; c.vf[0][1] = 0; c.vf[0][2] = 0; c.vf[0][3] = 0x3F800000u;
        c.vi[0] = 0;

        if (was_delay)
        {
            pend_cd = 0;
            pc = (uint32_t)pend_target;
            pend_target = -1;
        }
        else if (new_target >= 0)
        {
            pend_target = new_target;
            pend_cd = 1;
        }

        if (stop_after)
            break;
    }

    if (q_cd > 0)
        c.vi[22] = q_val;

    if (steps >= kMaxSteps)
    {
        abort_count++;

        if (abort_count <= 3 || (abort_count & 255u) == 0)
            std::fprintf(stderr, "  [VU%d] programa em 0x%04X nao terminou em %u instrucoes: abortado (%u vezes)\n", u.vu1 ? 1 : 0, byte_addr, kMaxSteps, abort_count);

        // despeja o laco (ultimas 64 instrucoes) e os registradores, uma vez por endereco
        static uint32_t dumped[8];
        static unsigned dumped_n = 0;
        bool seen = false;

        for (unsigned i = 0; i < dumped_n; i++)
            if (dumped[i] == byte_addr) seen = true;

        if (!seen && dumped_n < 8)
        {
            dumped[dumped_n++] = byte_addr;
            char name[64];
            std::snprintf(name, sizeof name, "vu%d_laco_0x%04X.txt", u.vu1 ? 1 : 0, byte_addr);

            if (FILE* f = std::fopen(name, "w"))
            {
                std::fprintf(f, "VU%d: programa em 0x%04X nao terminou em %u pares. pc=%u\n", u.vu1 ? 1 : 0, byte_addr, kMaxSteps, pc);
                std::fprintf(f, "VI:");

                for (int i = 0; i < 32; i++)
                    std::fprintf(f, " vi%02d=%04X", i, (unsigned)(c.vi[i] & 0xFFFFFFu));

                std::fprintf(f, "\nMAC=%04X status=%03X clip=%06X Q=%08X I=%08X P=%08X\n", (unsigned)(c.vi[17] & 0xFFFF), (unsigned)(c.vi[16] & 0xFFF), (unsigned)(c.vi[18] & 0xFFFFFF), c.vi[22], c.vi[21], c.vi[23]);

                for (int i = 0; i < 32; i++)
                    std::fprintf(f, "vf%02d = %08X %08X %08X %08X\n", i, c.vf[i][0], c.vf[i][1], c.vf[i][2], c.vf[i][3]);

                std::fprintf(f, "\nultimos 64 pares executados (endereco  upper lower):\n");

                for (unsigned k = 0; k < 64; k++)
                {
                    const UmRec& r = ring[(ring_n + k) & 63u];
                    std::fprintf(f, "%04X: %08X %08X\n", (unsigned)(r.pc * 8u), r.hi, r.lo);
                }

                std::fprintf(f, "\nprograma (a partir do endereco inicial, 96 pares):\n");

                for (unsigned k = 0; k < 96; k++)
                {
                    const uint32_t a = ((byte_addr >> 3) + k) & u.imask;
                    std::fprintf(f, "%04X: %08X %08X\n", (unsigned)(a * 8u), mem[a * 2 + 1], mem[a * 2]);
                }

                std::fclose(f);
                std::fprintf(stderr, "  [VU%d] laco gravado em %s\n", u.vu1 ? 1 : 0, name);
            }
        }
    }

    if (capture)
    {
        // Uma tentativa por entrada evita regravar a captura a cada quadro.
        captured[target] = true;
        vu_aot_generate(mem, byte_addr, visits);
    }
    g_prof_aot_pairs += aot_pairs;
    if (aot.prefix != nullptr)
        g_prof_aot_partial_pairs += aot_pairs;
    u.pc = pc;
    // A iteracao que sai por break tambem executou um par.
    return steps + (steps < kMaxSteps ? 1u : 0u);
}

// VU1: estado proprio (so VF, ACC, VI, Q, P, I, R e flags importam; o resto do Ctx fica sem uso).
static Ctx g_vu1_regs;

void vu0_run(Ctx& c, uint32_t byte_addr)
{
    g_vu0_runs++;

    if (g_vu0_dirty) { g_vu0_flags = um_scan_flag_readers(g_vu0_micro, 512); g_vu0_dirty = false; }

    g_um_flags = g_vu0_flags;
    const uint64_t pt0 = g_perf ? prof_now() : 0;
    VuUnit u = { &c, g_vu0_micro, g_vu0_data, 0x1FFu, 0xFFu, false, g_vu0_pc };
    vu_run(u, byte_addr);
    g_vu0_pc = u.pc;
    if (g_perf) g_prof_vu_ns += prof_now() - pt0;
}

void vu1_run(uint32_t byte_addr)
{
    g_vu1_runs++;

    if (g_vu1_dirty) { g_vu1_flags = um_scan_flag_readers(g_vu1_micro, 2048); g_vu1_dirty = false; }

    g_um_flags = g_vu1_flags;

    // Programa 0x1720 (clipper de poligonos do Shadow Hearts): a contagem de triangulos fica em D[0x10].x e o laco a
    // decrementa ANTES de testar. Com contagem 0 ele da a volta (0xFFFF) e roda 65535 voltas sobre lixo; no hardware o jogo
    // nunca manda isso, entao pacote com contagem 0 (ou maior que cabe na memoria) e pacote vazio: nao roda.
    // PS2_VU_NOCNTGUARD=1 desliga.
    static const bool no_cnt_guard = std::getenv("PS2_VU_NOCNTGUARD") != nullptr;

    if (!no_cnt_guard && byte_addr == 0x1720u)
    {
        const uint32_t cnt = reinterpret_cast<const uint32_t*>(g_vu1_data)[16 * 4] & 0xFFFFu;

        if (cnt == 0 || cnt > 400u)
        {
            static unsigned skipped = 0;
            skipped++;

            if (skipped <= 3 || (skipped & 1023u) == 0)
                std::fprintf(stderr, "  [VU1] 0x1720: contagem de triangulos invalida (%u) -> pacote ignorado (%u vezes)\n", cnt, skipped);

            return;
        }
    }

    const uint64_t pt0 = g_perf ? prof_now() : 0;
    const uint64_t kick_before = g_prof_kick_ns;
    VuUnit u = { &g_vu1_regs, g_vu1_micro, g_vu1_data, 0x7FFu, 0x3FFu, true, g_vu1_pc };
    const unsigned pairs = vu_run(u, byte_addr);
    g_vu1_pc = u.pc;
    const uint64_t elapsed = g_perf ? prof_now() - pt0 : 0;
    g_prof_vu_ns += elapsed;
    if (g_perf_vif1)
    {
        Vu1Profile& p = g_prof_vu1[(byte_addr >> 3) & 0x7FFu];
        ++p.calls;
        p.pairs += pairs;
        p.ns += elapsed;
        p.kick_ns += g_prof_kick_ns - kick_before;
    }
}

// XGKICK: envia ao GIF (PATH1), a partir do quadword indicado da memoria de dados do VU1, ate o pacote com EOP.
void vu1_xgkick(uint32_t qaddr)
{
    const uint64_t start = g_perf_vif1 ? prof_now() : 0;
    uint32_t a = qaddr & 0x3FFu;
    bool eop = false;

    for (unsigned guard = 0; guard < 4096; guard++)
    {
        const uint32_t* q = reinterpret_cast<const uint32_t*>(g_vu1_data) + ((a & 0x3FFu) * 4u);
        const uint64_t lo = (uint64_t)q[0] | ((uint64_t)q[1] << 32), hi = (uint64_t)q[2] | ((uint64_t)q[3] << 32);

        if (!g_gif.active && g_gif.image_left == 0)
            eop = ((lo >> 15) & 1u) != 0;

        g_gif.qword(lo, hi);
        a++;
        g_gs.xgkick_qw++;

        if (!g_gif.active && g_gif.image_left == 0 && eop)
            break;
    }

    g_gs.xgkicks++;
    if (g_perf_vif1)
        g_prof_kick_ns += prof_now() - start;
}

// VCALLMS / VCALLMSR: roda o microprograma (interpretador acima). PS2_VU0LOG=1 mostra o inicio de cada programa novo.
void vu0_callms(Ctx& c, uint32_t address, uint32_t raw)
{
    static const bool log = std::getenv("PS2_VU0LOG") != nullptr;
    static unsigned count = 0;
    static uint32_t seen[64];
    static unsigned seen_n = 0;

    count++;
    bool is_new = true;
    for (unsigned i = 0; i < seen_n; i++)
        if (seen[i] == address) { is_new = false; break; }

    if (is_new && seen_n < 64)
    {
        seen[seen_n++] = address;
        bool empty = true;
        for (unsigned i = 0; i < 4096 && empty; i++)
            if (g_vu0_micro[i] != 0) empty = false;

        std::fprintf(stderr, "  [VU0] %s micro=0x%04X CMSAR0=0x%X (chamada %u; ra=0x%08X)%s\n",
                     raw ? "VCALLMS" : "VCALLMSR", address, c.vi[27], count, (uint32_t)c.r[31].lo,
                     empty ? "  ** micro memoria VAZIA: o microprograma nao foi carregado **" : "");

        if (log)
        {
            const uint32_t* m = reinterpret_cast<const uint32_t*>(g_vu0_micro);

            for (uint32_t i = 0; i < 40 && ((address >> 3) + i) < 512; i++)
                std::fprintf(stderr, "    %04X: %08X %08X\n", (unsigned)(((address >> 3) + i) * 8), m[((address >> 3) + i) * 2 + 1], m[((address >> 3) + i) * 2]);
        }
    }

    vu0_run(c, address);
    c.vi[29] &= ~1u;   // VPU_STAT.VBS0: VU0 ocioso
}

// Estado das interrupcoes: DIntr/EIntr do SDK usam DI/EI e leem o registrador Status (bit 16 = EIE).
static bool g_irq_enabled = true;

void ps2_ei(Ctx&) { g_irq_enabled = true; }
void ps2_di(Ctx&) { g_irq_enabled = false; }

// Diagnostico: quantas vezes cada causa foi entregue e quando foi a ultima.
static unsigned g_irq_count[32] = {};

struct DeferredCall { int frames; uint32_t fn; uint32_t arg; };
static std::vector<DeferredCall> g_deferred;
static std::mutex g_deferred_mu;

void ps2_defer_call(uint32_t frames, uint32_t fn, uint32_t arg)
{
    std::lock_guard<std::mutex> lk(g_deferred_mu);
    g_deferred.push_back({ (int)frames, fn, arg });
}
static uint64_t g_irq_last_ns[32] = {};

static void irq_debug_print()
{
    uint64_t now = hw_now_ns();

    const uint32_t pending = g_irq_pending.load(std::memory_order_relaxed);
    std::fprintf(stderr, "interrupcoes: EIE=%d dentro_de_handler=%d mascara_intc=0x%X vblank_pendente=%u timer0_pendente=%u\n",
                 g_irq_enabled ? 1 : 0, g_in_irq ? 1 : 0, g_intc_mask.load(),
                 pending >> irq_timer0_bits, pending & irq_timer0_mask);
    std::fprintf(stderr, "  timer0: modo=0x%llX comparacao=0x%llX\n", (unsigned long long)g_timer0_mode.load(),
                 (unsigned long long)g_timer0_comp.load());

    for (int cause = 0; cause < 32; cause++)
    {
        if (g_irq_count[cause] == 0)
            continue;

        std::fprintf(stderr, "  causa %d: %u entregas, ultima ha %llu ms\n", cause, g_irq_count[cause],
                     (unsigned long long)((now - g_irq_last_ns[cause]) / 1000000ull));
    }
}

// Chama os handlers da causa com uma copia do contexto e uma pilha propria; o contexto da thread
// interrompida nao e alterado.
static void irq_deliver_cause(Ctx& c, uint32_t cause, uint32_t timer_events = 1)
{
    const Timer0StageScope timer0_stage(cause == 2 ? Timer0EeStage::Vblank : Timer0EeStage::Timer0);
    g_irq_count[cause & 31u]++;
    g_irq_last_ns[cause & 31u] = hw_now_ns();

    if (cause == 2)   // o kernel mesmo atualiza as flags do SetVSyncFlag a cada vsync
    {
        gs_vsync_update();

        if (g_vsync_flag1)
            mwr<uint32_t>(c, g_vsync_flag1, 1u);

        if (g_vsync_flag2)
            mwr<uint64_t>(c, g_vsync_flag2, 0x2ull | ((uint64_t)g_gs_field << 13));
    }

    // chamadas agendadas por ps2_defer_call: contam um vsync por entrega
    if (cause == 2)
    {
        ps2_gs_vsync();

        std::vector<DeferredCall> due;

        {
            std::lock_guard<std::mutex> lk(g_deferred_mu);

            for (size_t i = 0; i < g_deferred.size();)
            {
                if (--g_deferred[i].frames <= 0)
                {
                    due.push_back(g_deferred[i]);
                    g_deferred.erase(g_deferred.begin() + (std::ptrdiff_t)i);
                }
                else
                    i++;
            }
        }

        for (size_t i = 0; i < due.size(); i++)
        {
            std::fprintf(stderr, "  [adiado] chamando 0x%08X(0x%X)\n", due[i].fn, due[i].arg);

            Ctx irq = c;
            irq.r[29].lo = 0x01FF0000u - 0x10u;
            irq.r[4].lo = due[i].arg;
            irq.r[5].lo = 0;
            irq.r[6].lo = 0;
            ps2_call(irq, due[i].fn);
        }
    }

    if (!(g_intc_mask & (1u << cause)))
        return;

    if (cause == 9)
        g_timer0_delivered.fetch_add(1, std::memory_order_relaxed);

    std::vector<IntcHandler> list = g_intc_handlers;

    for (size_t i = 0; i < list.size(); i++)
    {
        if (list[i].cause != cause)
            continue;

        // TimerWatcher0 atualiza os temporizadores de 1 ms e seu divisor de 17
        // ticks. Cada periodo pendente precisa percorrer o mesmo caminho.
        const uint32_t deliveries = cause == 9 && list[i].fn == 0x0022E470u ? timer_events : 1u;
        g_timer0_ticks_replayed += deliveries - 1;
        for (uint32_t tick = 0; tick < deliveries; ++tick)
        {
            Ctx irq = c;
            irq.r[29].lo = 0x01FF0000u - 0x10u;
            irq.r[4].lo = cause;
            irq.r[5].lo = list[i].arg;
            irq.r[6].lo = 0;
            ps2_call(irq, list[i].fn);
        }
    }
}

// Ambos os caminhos consomem os mesmos lotes. No modo de perfil,
// conta tambem VBLANKs pendentes que excedem o limite de entrega.
static void irq_deliver_pending(Ctx& c, uint32_t pending, bool idle)
{
    if (const uint32_t count = pending >> irq_timer0_bits)
    {
        const uint32_t n = g_vsync_catchup ? std::min(count, 4u) : 1u;
        if (g_perf_cadence)
        {
            ++g_cadence_ee.batches;
            g_cadence_ee.multiple += count > 1;
            g_cadence_ee.max_pending = std::max(g_cadence_ee.max_pending, count);
            g_cadence_ee.dropped += count - n;
        }

        for (uint32_t k = 0; k < n; k++)
        {
            if (g_perf_cadence && idle) ++g_cadence_ee.idle_vblanks;
            irq_deliver_cause(c, 2);
            g_vsync_del++;
        }
    }

    if (const uint32_t timer_events = pending & irq_timer0_mask)
        irq_deliver_cause(c, 9, timer_events);
}

// Um handler pode acordar uma thread de prioridade maior.
static void irq_preempt_if_ready()
{
    if (g_kernel_ready)
    {
        std::unique_lock<std::mutex> lk(g_mu);
        maybe_preempt(lk, g_running);
    }
}

// Consome so o contador do Timer 0. VBLANK e a apresentacao continuam
// nos pontos normais do EE. A operacao atomica preserva sinais novos.
static void irq_poll_timer0_at_dma(Ctx& c)
{
    if (g_in_irq || !g_irq_enabled || !(g_intc_mask.load() & (1u << 9)) ||
        !(g_irq_pending.load(std::memory_order_relaxed) & irq_timer0_mask))
        return;

    const uint32_t timer_events = g_irq_pending.fetch_and(~irq_timer0_mask,
        std::memory_order_acq_rel) & irq_timer0_mask;
    if (!timer_events)
        return;

    g_in_irq = true;
    ++g_timer0_dma_delivered;
    irq_deliver_cause(c, 9, timer_events);
    g_in_irq = false;
    irq_preempt_if_ready();
}

// Chamada pelo codigo gerado quando o tique liga g_irq_pending.
void ps2_irq_poll(Ctx& c)
{
    if (g_in_irq || !g_irq_enabled)
        return;

    const uint32_t pending = g_irq_pending.exchange(0, std::memory_order_acq_rel);

    if (pending == 0)
        return;

    g_in_irq = true;
    irq_deliver_pending(c, pending, false);
    g_in_irq = false;
    irq_preempt_if_ready();
}

// Chamada pelo escalonador quando nenhuma thread pode rodar (todas esperando).
void ps2_irq_idle(Ctx& c)
{
    if (g_in_irq)
        return;

    const uint32_t pending = g_irq_pending.exchange(0, std::memory_order_acq_rel);

    if (pending == 0)
        return;

    g_in_irq = true;

    irq_deliver_pending(c, pending, true);

    g_in_irq = false;
}

uint32_t ps2_mfc0(Ctx&, int reg)
{
    if (reg == 12)   // Status
        return g_irq_enabled ? 0x00010C01u : 0x00000C00u;

    return 0;
}

void ps2_mtc0(Ctx&, int, uint32_t) {}

// ---- janela do jogo (Win32/GDI): mostra o quadro que o GS por software desenhou ----
// Fica no fim do arquivo de proposito: <windows.h> define macros que nao devem vazar para o resto do runtime.
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mmsystem.h>
#include <mfplay.h>

static std::filesystem::path ps2_windows_executable_path()
{
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, &path[0], DWORD(path.size()));
    if (!length || length >= path.size())
        return {};
    path.resize(length);
    return std::filesystem::path(path);
}

const roj::Settings& ps2_settings()
{
    static const auto settings = roj::read_settings(
        ps2_windows_executable_path().parent_path() / L"RingOfJudgment.ini");
    return settings;
}

static bool ps2_windows_replace_file(const std::filesystem::path& source, const std::filesystem::path& target)
{
    return MoveFileExW(source.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
}

static double ps2_presentation_aspect()
{
    if (const char* value = std::getenv("PS2_ASPECT"))
    {
        if (std::strcmp(value,"4:3") == 0) return 4.0/3.0;
        if (std::strcmp(value,"16:9") == 0) return 16.0/9.0;
        if (std::strcmp(value,"21:9") == 0) return 21.0/9.0;
        if (std::strcmp(value,"stretch") == 0) return 0;
    }
    return roj::aspect_ratio(ps2_settings().aspect);
}

static std::wstring g_display_device;
static DEVMODEW g_display_mode{};
static bool g_display_exclusive = false, g_display_applied = false;

static void ps2_restore_display()
{
    if (!g_display_applied) return;
    g_display_applied = false;
    ChangeDisplaySettingsExW(g_display_device.c_str(),nullptr,nullptr,0,nullptr);
}

static void ps2_activate_display(HWND window, bool active)
{
    if (!g_display_exclusive) return;
    if (!active)
        ps2_restore_display();
    else if (!g_display_applied)
    {
        g_display_applied = ChangeDisplaySettingsExW(g_display_device.c_str(),&g_display_mode,
            nullptr,CDS_FULLSCREEN,nullptr) == DISP_CHANGE_SUCCESSFUL;
        MONITORINFO monitor{sizeof monitor};
        GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&monitor);
        const RECT rect = monitor.rcMonitor;
        SetWindowPos(window,HWND_TOP,rect.left,rect.top,rect.right-rect.left,rect.bottom-rect.top,
            SWP_NOACTIVATE);
    }
}

static HWND ps2_create_game_window(HINSTANCE instance)
{
    SetProcessDPIAware();
    const auto& settings = ps2_settings();
    POINT cursor{};
    GetCursorPos(&cursor);
    const HMONITOR monitor = MonitorFromPoint(cursor,MONITOR_DEFAULTTOPRIMARY);
    MONITORINFOEXW info{};
    info.cbSize = sizeof info;
    GetMonitorInfoW(monitor,reinterpret_cast<MONITORINFO*>(&info));
    g_display_device = info.szDevice;
    if (settings.window_mode == 1)
    {
        g_display_mode.dmSize = sizeof g_display_mode;
        g_display_mode.dmPelsWidth = settings.width;
        g_display_mode.dmPelsHeight = settings.height;
        g_display_mode.dmBitsPerPel = 32;
        g_display_mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;
        const LONG result = ChangeDisplaySettingsExW(info.szDevice,&g_display_mode,
            nullptr,CDS_FULLSCREEN,nullptr);
        g_display_applied = g_display_exclusive = result == DISP_CHANGE_SUCCESSFUL;
        if (!g_display_exclusive)
            std::fprintf(stderr,"  [janela] modo %dx%d indisponivel (%ld); usando tela cheia sem bordas\n",
                settings.width,settings.height,result);
        GetMonitorInfoW(monitor,reinterpret_cast<MONITORINFO*>(&info));
    }
    const bool fullscreen = settings.window_mode == 1 || settings.window_mode == 3;
    const DWORD style = settings.window_mode == 0 ? WS_OVERLAPPEDWINDOW : WS_POPUP;
    const RECT area = fullscreen ? info.rcMonitor : info.rcWork;
    RECT size{0,0,settings.width,settings.height};
    if (settings.window_mode == 0) AdjustWindowRect(&size,style,FALSE);
    const int width = fullscreen ? area.right-area.left : std::min(size.right-size.left,area.right-area.left);
    const int height = fullscreen ? area.bottom-area.top : std::min(size.bottom-size.top,area.bottom-area.top);
    const int x = area.left + ((area.right-area.left)-width)/2;
    const int y = area.top + ((area.bottom-area.top)-height)/2;
    HWND window = CreateWindowExW(0,L"Ps2GameWindow",L"Ring of Judgment",style | WS_VISIBLE,
        x,y,width,height,nullptr,nullptr,instance,nullptr);
    if (!window) ps2_restore_display();
    return window;
}

/* SH_EMBEDDED_EXTERNAL_MUSIC */
namespace
{
    struct RssdWaveApi
    {
        decltype(&waveOutOpen) open = nullptr;
        decltype(&waveOutPrepareHeader) prepare = nullptr;
        decltype(&waveOutWrite) write = nullptr;
        decltype(&waveOutPause) pause = nullptr;
        decltype(&waveOutRestart) restart = nullptr;
        decltype(&waveOutReset) reset = nullptr;
        decltype(&waveOutUnprepareHeader) unprepare = nullptr;
        decltype(&waveOutClose) close = nullptr;

        explicit RssdWaveApi(HMODULE module)
        {
            if (!module)
                return;
#define RSSD_WAVE_LOAD(member, name) member = reinterpret_cast<decltype(member)>(GetProcAddress(module, #name))
            RSSD_WAVE_LOAD(open, waveOutOpen);
            RSSD_WAVE_LOAD(prepare, waveOutPrepareHeader);
            RSSD_WAVE_LOAD(write, waveOutWrite);
            RSSD_WAVE_LOAD(pause, waveOutPause);
            RSSD_WAVE_LOAD(restart, waveOutRestart);
            RSSD_WAVE_LOAD(reset, waveOutReset);
            RSSD_WAVE_LOAD(unprepare, waveOutUnprepareHeader);
            RSSD_WAVE_LOAD(close, waveOutClose);
#undef RSSD_WAVE_LOAD
        }

        bool ready() const
        {
            return open && prepare && write && pause && restart && reset && unprepare && close;
        }
    };

    HMODULE rssd_live_module()
    {
        wchar_t path[MAX_PATH];
        const UINT length = GetSystemDirectoryW(path, MAX_PATH);
        const wchar_t suffix[] = L"\\winmm.dll";
        if (!length || length + sizeof suffix / sizeof *suffix > MAX_PATH)
            return nullptr;
        std::memcpy(path + length, suffix, sizeof suffix);
        return LoadLibraryW(path);
    }

    void rssd_load_recorded_effects(LiveEffects& effects)
    {
        // Busca os WAVs na pasta do executavel, independente do diretorio atual.
        std::wstring path(32768,L'\0');
        const DWORD len = GetModuleFileNameW(nullptr,&path[0],DWORD(path.size()));
        if (!len || len>=path.size())
            return;
        path.resize(len);
        const auto separator = path.find_last_of(L"\\/");
        if (separator==std::wstring::npos)
            return;
        path.resize(separator+1);
        struct Asset { unsigned id; double gain; const wchar_t* file; const char* label; };
        // Os quatro sons compartilham o ganho para manter o equilibrio da previa.
        constexpr double menu_gain = 0.26;
        constexpr Asset assets[] = {
            {0x21,0.65,L"Audio\\Sound Effects\\estilhaco_anel.wav","estilhaco externo"},
            {0x09,menu_gain,L"Audio\\Sound Effects\\menu_abrir.wav","menu abrir"},
            {0x01,menu_gain,L"Audio\\Sound Effects\\menu_cursor.wav","menu cursor"},
            {0x02,menu_gain,L"Audio\\Sound Effects\\menu_confirmar.wav","menu confirmar"},
            {0x03,menu_gain,L"Audio\\Sound Effects\\menu_voltar.wav","menu voltar"},
        };

        for (const auto& asset : assets)
        {
            const std::wstring full_path = path + asset.file;
            const HANDLE file = CreateFileW(full_path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,
                                            OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
            if (file==INVALID_HANDLE_VALUE)
                continue;
            LARGE_INTEGER length{};
            LiveBytes bytes;
            DWORD received = 0;
            bool ok = GetFileSizeEx(file,&length) && length.QuadPart>=44 && length.QuadPart<=262144;
            if (ok)
            {
                bytes.resize(size_t(length.QuadPart));
                ok = ReadFile(file,bytes.data(),DWORD(bytes.size()),&received,nullptr) && received==bytes.size();
            }
            CloseHandle(file);
            if (ok && effects.load_effect_wav(asset.id,asset.gain,bytes))
            {
                if (asset.id==0x21)
                    std::fprintf(stderr,"  [audio RSSD ao vivo] estilhaco externo carregado\n");
                else
                    std::fprintf(stderr,"  [audio RSSD ao vivo] %s: WAV carregado\n",asset.label);
            }
            else
                std::fprintf(stderr,"  [audio RSSD ao vivo] %s invalido; usando banco RSSD\n",asset.label);
        }
    }

    class LivePlayer
    {
        // Um worker mantem o decoder e o dispositivo fora do caminho do EE.
        // Tres blocos de cerca de 17 ms limitam a latencia dos efeitos.
        static constexpr unsigned BlockFrames = 384;
        static constexpr unsigned BlockCount = 3;
        std::mutex mutex;
        std::condition_variable wake;
        struct Command
        {
            enum Kind { Play, Stop, Effect, StopEffect } kind;
            LiveBytes sequence;
            std::shared_ptr<const LiveBytes> bank;
            bool whole_bank = false;
            EffectRequest effect{};
            unsigned id = 0;

            explicit Command(Kind k) : kind(k) {}
        };
        std::deque<Command> queue;
        bool closing = false;
        bool device_failed = false;
        std::unique_ptr<LiveSong> song;
        std::unique_ptr<roj::ExternalMusic> external_song;
        LiveEffects effects;
        bool effect_logged = false;
        std::string song_name;
        std::thread thread;

        bool update(bool wait)
        {
            std::deque<Command> commands;
            {
                std::unique_lock<std::mutex> lock(mutex);
                if (wait)
                    wake.wait(lock,[&]{ return closing || !queue.empty(); });
                if (closing)
                    return false;
                commands.swap(queue);
            }
            for (auto& c : commands)
            {
                if (c.kind == Command::Effect)
                {
                    const unsigned layers = effects.play(c.effect);
                    if (c.effect.trace_id)
                        std::fprintf(stderr,"  [RSSD FX] t=%lldms n=%llu id=%u resultado=%s camadas=%u\n",
                            rssd_fx_ms(),static_cast<unsigned long long>(c.effect.trace_id),
                            c.effect.id,layers?"mixado":"sem-camada-compativel",layers);
                    if (layers && !effect_logged)
                    {
                        // Confirma o primeiro disparo sem poluir o log do jogo.
                        effect_logged = true;
                        std::fprintf(stderr,"  [audio RSSD ao vivo] efeito %u: ativo\n",c.effect.id);
                    }
                }
                else if (c.kind == Command::StopEffect)
                    effects.stop(c.id,c.whole_bank);
                else
                {
                    if (song || external_song)
                    {
                        std::fprintf(stderr,"  [audio RSSD ao vivo] %s: fim\n",song_name.c_str());
                        song.reset();
                        external_song.reset();
                    }
                    if (c.kind == Command::Play)
                    {
                        if(c.sequence.size()<0x3c || !c.bank)continue;
                        const auto first=c.sequence.begin()+0x2c;
                        song_name.assign(first,std::find(first,first+16,uint8_t(0)));
                        auto external=std::make_unique<roj::ExternalMusic>();
                        if(external->open(ps2_windows_executable_path().parent_path()/L"Audio"/L"Music",song_name)){
                            external_song=std::move(external);
                            std::fprintf(stderr,"  [audio RSSD ao vivo] %s: musica externa\n",song_name.c_str());
                            continue;
                        }
                        auto next = std::make_unique<LiveSong>();
                        if (next->load(c.sequence,*c.bank))
                        {
                            song = std::move(next);
                            const auto name = c.sequence.begin()+0x2c;
                            song_name.assign(name,std::find(name,name+16,uint8_t(0)));
                            std::fprintf(stderr,"  [audio RSSD ao vivo] %s: inicio\n",song_name.c_str());
                        }
                        else
                            std::fprintf(stderr,"  [audio RSSD ao vivo] falha ao interpretar SMD/SWD\n");
                    }
                }
            }
            return true;
        }

        bool playback(const RssdWaveApi& api)
        {
            WAVEFORMATEX format = {};
            format.wFormatTag = WAVE_FORMAT_PCM;
            format.nChannels = 2;
            format.nSamplesPerSec = LiveRate;
            format.wBitsPerSample = 16;
            format.nBlockAlign = 4;
            format.nAvgBytesPerSec = LiveRate * format.nBlockAlign;
            HANDLE event = CreateEventW(nullptr,FALSE,FALSE,nullptr);
            if (!event)
                return false;
            HWAVEOUT dev = nullptr;
            if (api.open(&dev,WAVE_MAPPER,&format,reinterpret_cast<DWORD_PTR>(event),0,CALLBACK_EVENT) != MMSYSERR_NOERROR)
            {
                CloseHandle(event);
                return false;
            }
            struct Buffers { WAVEHDR heads[BlockCount] = {}; int16_t pcm[BlockCount][BlockFrames*2]; };
            auto buffers = std::make_unique<Buffers>();
            auto& heads = buffers->heads;
            auto write = [&](unsigned i)
            {
                auto& pcm = buffers->pcm[i];
                if (external_song)
                {
                    if(!external_song->render(pcm,BlockFrames)){
                        std::fprintf(stderr,"  [audio RSSD ao vivo] %s: falha ao ler musica externa\n",song_name.c_str());
                        external_song.reset();
                    }
                }
                else if (song)
                    song->render(pcm,BlockFrames);
                else
                    std::fill_n(pcm,BlockFrames*2,int16_t(0));
                const auto& settings = ps2_settings();
                const double master = roj::volume_gain(settings.master_volume);
                const double music = master * roj::volume_gain(settings.music_volume);
                if (music != 1.0)
                    for (auto& sample : pcm)
                        sample = int16_t(std::lround(sample * music));
                effects.render(pcm,BlockFrames,master * roj::volume_gain(settings.effects_volume));
                return api.write(dev,&heads[i],sizeof(WAVEHDR)) == MMSYSERR_NOERROR;
            };

            bool ok = api.pause(dev) == MMSYSERR_NOERROR;
            for (unsigned i = 0; ok && i < BlockCount; ++i)
            {
                heads[i].lpData = reinterpret_cast<char*>(buffers->pcm[i]);
                heads[i].dwBufferLength = sizeof buffers->pcm[i];
                ok = api.prepare(dev,&heads[i],sizeof(WAVEHDR)) == MMSYSERR_NOERROR && write(i);
            }

            ok = ok && api.restart(dev) == MMSYSERR_NOERROR;
            unsigned next = 0;
            while (ok && update(false))
            {
                WaitForSingleObject(event,10);
                for (unsigned count=0; count<BlockCount && ok && (heads[next].dwFlags & WHDR_DONE); ++count)
                {
                    ok = update(false) && write(next);
                    next = (next+1)%BlockCount;
                }
            }

            bool clean = api.reset(dev) == MMSYSERR_NOERROR;
            for (auto& h : heads)
                if ((h.dwFlags & WHDR_PREPARED) && api.unprepare(dev,&h,sizeof h) != MMSYSERR_NOERROR)
                    clean = false;
            if (api.close(dev) != MMSYSERR_NOERROR)
            {
                // Um dispositivo que nao fechou ainda pode referenciar os buffers.
                buffers.release();
                return false;
            }
            CloseHandle(event);
            return ok && clean;
        }

        void run()
        {
            effects.external_root(ps2_windows_executable_path().parent_path()/L"Audio"/L"Sound Effects");
            rssd_load_recorded_effects(effects);
            const RssdWaveApi api(rssd_live_module());
            if (!api.ready() || (update(true) && !playback(api)))
            {
                std::lock_guard<std::mutex> lock(mutex);
                device_failed = true;
                queue.clear();
                std::fprintf(stderr,"  [audio RSSD ao vivo] falha no dispositivo de audio\n");
            }
        }

        void post(Command command)
        {
            {
                std::lock_guard<std::mutex> lock(mutex);
                if (device_failed || (command.kind == Command::Effect && queue.size() >= 64))
                {
                    if (command.effect.trace_id)
                        std::fprintf(stderr,"  [RSSD FX] t=%lldms n=%llu id=%u resultado=%s\n",
                            rssd_fx_ms(),static_cast<unsigned long long>(command.effect.trace_id),
                            command.effect.id,device_failed?"dispositivo-indisponivel":"fila-cheia");
                    return;
                }
                queue.push_back(std::move(command));
            }
            wake.notify_one();
        }

    public:
        LivePlayer() : thread(&LivePlayer::run,this) {}
        ~LivePlayer()
        {
            {
                std::lock_guard<std::mutex> lock(mutex);
                closing = true;
            }
            wake.notify_one();
            thread.join();
        }

        void play(LiveBytes sequence, std::shared_ptr<const LiveBytes> bank)
        {
            Command c(Command::Play);
            c.sequence=std::move(sequence);
            c.bank=std::move(bank);
            post(std::move(c));
        }

        void stop() { post(Command(Command::Stop)); }

        void effect(EffectRequest request)
        {
            Command c(Command::Effect);
            c.effect=std::move(request);
            post(std::move(c));
        }

        void stop_effect(unsigned id, bool whole_bank)
        {
            Command c(Command::StopEffect);
            c.id=id;
            c.whole_bank=whole_bank;
            post(std::move(c));
        }
    };

    LivePlayer& rssd_live_player()
    {
        static LivePlayer player;
        return player;
    }

    void rssd_live_play(LiveBytes sequence, std::shared_ptr<const LiveBytes> bank)
    {
        rssd_live_player().play(std::move(sequence),std::move(bank));
    }

    void rssd_live_stop()
    {
        rssd_live_player().stop();
    }

    void rssd_live_effect(EffectRequest request)
    {
        rssd_live_player().effect(std::move(request));
    }

    void rssd_live_effect_stop(unsigned id, bool whole_bank)
    {
        rssd_live_player().stop_effect(id,whole_bank);
    }
}

#ifdef _MSC_VER
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#endif

namespace
{
    HWND g_hwnd = nullptr;
    volatile bool g_redraw = false;

    struct FmvRequest
    {
        std::filesystem::path file;
        uint32_t owner = 0;
        uint32_t end_callback = 0;
    };
    std::mutex g_fmv_mutex;
    FmvRequest g_fmv_pending;
    uint32_t g_fmv_cancel_owner = 0;

    class FmvEvents final : public IMFPMediaPlayerCallback
    {
        std::atomic<ULONG> references{1};
    public:
        enum State { Opening, Playing, Finished, Failed };
        std::atomic<State> state{Opening};

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override
        {
            if (!out) return E_POINTER;
            *out = nullptr;
            if (iid != __uuidof(IUnknown) && iid != __uuidof(IMFPMediaPlayerCallback))
                return E_NOINTERFACE;
            *out = static_cast<IMFPMediaPlayerCallback*>(this);
            AddRef();
            return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
        ULONG STDMETHODCALLTYPE Release() override
        {
            const ULONG remaining = --references;
            if (!remaining) delete this;
            return remaining;
        }
        void STDMETHODCALLTYPE OnMediaPlayerEvent(MFP_EVENT_HEADER* event) override
        {
            if (FAILED(event->hrEvent) || event->eEventType == MFP_EVENT_TYPE_ERROR)
                state.store(Failed, std::memory_order_release);
            else if (event->eEventType == MFP_EVENT_TYPE_PLAYBACK_ENDED)
                state.store(Finished, std::memory_order_release);
            else if (event->eEventType == MFP_EVENT_TYPE_PLAY)
            {
                State expected = Opening;
                state.compare_exchange_strong(expected, Playing);
            }
        }
    };

    class WindowMovie
    {
        IMFPMediaPlayer* player = nullptr;
        HWND surface = nullptr;
        FmvEvents* events = nullptr;
        FmvRequest current;
        uint64_t opened = 0;
        bool reported = false, start_down = false;

        void layout(HWND parent)
        {
            RECT bounds{};
            GetClientRect(parent,&bounds);
            const auto view = roj::fit_viewport(bounds.right,bounds.bottom,ps2_presentation_aspect());
            HDC dc = GetDC(parent);
            FillRect(dc,&bounds,reinterpret_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
            ReleaseDC(parent,dc);
            SetWindowPos(surface,HWND_TOP,view.x,view.y,view.width,view.height,SWP_NOACTIVATE);
            player->SetAspectRatioMode(0);
            player->UpdateVideo();
        }

        void close(bool completed)
        {
            if (player)
            {
                player->Shutdown();
                player->Release();
                player = nullptr;
            }
            if (events)
            {
                events->Release();
                events = nullptr;
            }
            if (surface)
            {
                HWND parent = GetParent(surface);
                DestroyWindow(surface);
                surface = nullptr;
                InvalidateRect(parent, nullptr, FALSE);
            }
            const uint32_t callback = current.end_callback;
            current = {};
            g_redraw = true;
            if (completed && callback)
                ps2_defer_call(1, callback, 0);
        }

        void open(HWND hwnd, FmvRequest request)
        {
            current = std::move(request);
            opened = hw_now_ns();
            reported = false;
            start_down = g_fmv_start_pressed.load(std::memory_order_relaxed);
            static const HMODULE module = LoadLibraryExW(L"mfplay.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            using Create = decltype(&MFPCreateMediaPlayer);
            static const Create create = module
                ? reinterpret_cast<Create>(GetProcAddress(module, "MFPCreateMediaPlayer")) : nullptr;
            RECT bounds{};
            GetClientRect(hwnd, &bounds);
            surface = CreateWindowExW(0, L"STATIC", nullptr, WS_CHILD | WS_VISIBLE | SS_BLACKRECT,
                0, 0, bounds.right, bounds.bottom, hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
            events = new FmvEvents;
            const HRESULT hr = create && surface ? create(current.file.c_str(), TRUE,
                MFP_OPTION_FREE_THREADED_CALLBACK, events, surface, &player) : E_NOINTERFACE;
            if (FAILED(hr) || !player)
            {
                std::fprintf(stderr, "  [FMV] falha ao abrir %ls (0x%08lX)\n",
                    current.file.filename().c_str(), (unsigned long)hr);
                close(true);
            }
            else
                layout(hwnd);
        }

    public:
        bool update(HWND hwnd)
        {
            FmvRequest request;
            uint32_t cancelled = 0;
            {
                std::lock_guard<std::mutex> lock(g_fmv_mutex);
                request = std::move(g_fmv_pending);
                g_fmv_pending = {};
                cancelled = std::exchange(g_fmv_cancel_owner, 0);
            }
            if (current.owner && (current.owner == cancelled || request.owner))
                close(false);
            if (request.owner)
                open(hwnd, std::move(request));
            if (!player)
                return false;

            const auto state = events->state.load(std::memory_order_acquire);
            const bool start = g_fmv_start_pressed.load(std::memory_order_relaxed);
            const bool skip = state == FmvEvents::Playing && start && !start_down;
            start_down = start;
            if (!reported && state == FmvEvents::Playing)
            {
                reported = true;
                player->SetVolume(float(roj::volume_gain(ps2_settings().master_volume)));
                std::fprintf(stderr, "  [FMV] reproduzindo %ls\n", current.file.filename().c_str());
            }
            if (skip || state == FmvEvents::Finished || state == FmvEvents::Failed ||
                (state == FmvEvents::Opening && hw_now_ns() - opened > 15000000000ull))
            {
                const char* reason = skip ? "interrompido pelo controle" :
                    state == FmvEvents::Finished ? "fim" : "falha do decodificador";
                std::fprintf(stderr, "  [FMV] %s: %ls\n", reason, current.file.filename().c_str());
                close(true);
                return false;
            }

            if (g_redraw)
            {
                g_redraw = false;
                layout(hwnd);
            }
            return true;
        }
    };


    LRESULT CALLBACK ps2_wndproc(HWND h, UINT m, WPARAM w, LPARAM l)
    {
        switch (m)
        {
            case WM_CLOSE:
            case WM_DESTROY:
                ps2_restore_display();
                std::fprintf(stderr, "\n[ROJ_SESSION_CLOSED_NORMALLY]\n");
                std::fflush(stderr);
                std::_Exit(0);

            case WM_ACTIVATEAPP:
                ps2_activate_display(h,w != 0);
                return 0;

            case WM_SIZE:
                g_redraw = true;
                return 0;

            case WM_ERASEBKGND:
                return 1;

            case WM_PAINT:
            {
                PAINTSTRUCT ps;
                BeginPaint(h, &ps);
                EndPaint(h, &ps);
                g_redraw = true;
                return 0;
            }

            default:
                break;
        }

        return DefWindowProcA(h, m, w, l);
    }

    void ps2_window_thread()
    {
        WNDCLASSA wc = {};
        wc.lpfnWndProc = ps2_wndproc;
        wc.hInstance = GetModuleHandleA(nullptr);
        wc.hCursor = LoadCursorA(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
        wc.lpszClassName = "Ps2GameWindow";
        RegisterClassA(&wc);

        HWND hw = ps2_create_game_window(wc.hInstance);

        if (hw == nullptr)
        {
            std::fprintf(stderr, "  [janela] CreateWindow falhou (%lu)\n", (unsigned long)GetLastError());
            return;
        }

        g_hwnd = hw;
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        WindowMovie movie;

        unsigned seen = 0;
        WindowFrameContent frame_content;
        unsigned distinct = 0, repeated = 0;
        unsigned last_mark_distinct = 0, last_mark_repeated = 0;
        uint64_t last_mark_ns = 0;
        bool f12_last = false;
        std::vector<uint32_t> pix;
        uint32_t w = 0, h = 0;
        unsigned last_flips = g_gs_flips.load();
        std::chrono::steady_clock::time_point last_title = std::chrono::steady_clock::now();

        for (;;)
        {
            MSG msg;

            while (PeekMessageA(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&msg);
                DispatchMessageA(&msg);
            }

            if (movie.update(hw))
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                continue;
            }

            const std::chrono::steady_clock::time_point tnow = std::chrono::steady_clock::now();

            if (tnow - last_title >= std::chrono::seconds(1))
            {
                const unsigned f = g_gs_flips.load();
                char title[96];
                std::snprintf(title, sizeof title, "Shadow Hearts - %u trocas de buffer/s", f - last_flips);
                SetWindowTextA(hw, title);
                last_flips = f;
                last_title = tnow;
            }

            {
                static bool f10_last = false;
                const bool f10 = ps2_key_down(0x79) && !roj::input_uses_key(ps2_settings().input, 0x79);

                if (f10 && !f10_last && !pix.empty())
                {
                    static unsigned shots = 0;
                    char name[64];
                    std::snprintf(name, sizeof name, "gs_shot_%u.bmp", ++shots);
                    gs_save_bmp(name, pix, w, h);
                }

                f10_last = f10;
            }

            static bool f11_last = false;
            const bool f11 = ps2_key_down(0x7A) && !roj::input_uses_key(ps2_settings().input, 0x7A);
            if (f11 && !f11_last)
                g_font_capture_request.store(true, std::memory_order_relaxed);
            f11_last = f11;

            const bool f12 = ps2_key_down(0x7B) && !roj::input_uses_key(ps2_settings().input, 0x7B);
            if (f12 && !f12_last)
            {
                const uint64_t now = prof_now();
                if (last_mark_ns == 0)
                    std::fprintf(stderr, "  [medicao GDI] inicio\n");
                else
                {
                    const double seconds = (now - last_mark_ns) / 1e9;
                    const unsigned changed = distinct - last_mark_distinct;
                    std::fprintf(stderr,
                        "  [medicao GDI] segundos=%.3f distintos=%u repetidos=%u distintos/s=%.2f\n",
                        seconds, changed, repeated - last_mark_repeated, changed / seconds);
                }
                last_mark_ns = now;
                last_mark_distinct = distinct;
                last_mark_repeated = repeated;
            }
            f12_last = f12;

            FontCapture capture;
            const bool fresh = g_win_seq.load() != seen;
            if (fresh)
            {
                std::lock_guard<std::mutex> lk(g_win_mu);
                const unsigned seq = g_win_seq.load();
                pix.swap(g_win_pix);
                w = g_win_w;
                h = g_win_h;
                if (g_perf_gs) g_win_coalesced.fetch_add(seq - seen - 1u, std::memory_order_relaxed);
                seen = seq;
                if (g_font_capture.w != 0)
                {
                    capture = std::move(g_font_capture);
                    g_font_capture = {};
                }
            }
            if (capture.w != 0)
                gs_save_font_capture(capture);

            if ((fresh || g_redraw) && !pix.empty())
            {
                g_redraw = false;

                BITMAPINFO bi = {};
                bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bi.bmiHeader.biWidth = (LONG)w;
                bi.bmiHeader.biHeight = -(LONG)h;       // de cima para baixo
                bi.bmiHeader.biPlanes = 1;
                bi.bmiHeader.biBitCount = 32;
                bi.bmiHeader.biCompression = BI_RGB;

                RECT cr;
                GetClientRect(hw, &cr);
                HDC dc = GetDC(hw);
                static const bool smooth = std::getenv("PS2_NOSMOOTH") == nullptr;

                if (smooth)
                {
                    SetStretchBltMode(dc, HALFTONE);    // suaviza o aumento da imagem (em vez de quadrados)
                    SetBrushOrgEx(dc, 0, 0, nullptr);
                }

                const auto view = roj::fit_viewport(cr.right,cr.bottom,ps2_presentation_aspect());
                const int dx = view.x, dy = view.y, dw = view.width, dh = view.height;

                // So as barras (areas fora da imagem) sao pintadas de preto; pintar a janela toda antes de cada quadro piscava.
                if (dx > 0)
                {
                    PatBlt(dc, 0, 0, dx, (int)cr.bottom, BLACKNESS);
                    PatBlt(dc, dx + dw, 0, (int)cr.right - dx - dw, (int)cr.bottom, BLACKNESS);
                }

                if (dy > 0)
                {
                    PatBlt(dc, 0, 0, (int)cr.right, dy, BLACKNESS);
                    PatBlt(dc, 0, dy + dh, (int)cr.right, (int)cr.bottom - dy - dh, BLACKNESS);
                }

                const int drawn = StretchDIBits(dc, dx, dy, dw, dh, 0, 0, (int)w, (int)h,
                    pix.data(), &bi, DIB_RGB_COLORS, SRCCOPY);
                ReleaseDC(hw, dc);
                // Conta imagens novas aceitas pelo GDI. Redesenhos de uma
                // imagem antiga e a cadencia do compositor sao outra medida.
                if (g_perf_gs || last_mark_ns != 0)
                {
                    const bool changed = drawn > 0 && frame_content.record(pix, w, h);
                    if (fresh && drawn > 0)
                    {
                        distinct += changed;
                        repeated += !changed;
                        g_win_content_counts.store(((uint64_t)repeated << 32) | distinct, std::memory_order_relaxed);
                        g_win_blits.fetch_add(1, std::memory_order_relaxed);
                    }
                    else if (fresh)
                        g_win_blit_errors.fetch_add(1, std::memory_order_relaxed);
                }
            }
            else
            {
                Sleep(2);
            }
        }
    }
}

bool ps2_fmv_play(const char* path, uint32_t owner, uint32_t end_callback)
{
    if (!g_gswin || !owner || std::getenv("PS2_FMV_FRAMES"))
        return false;
    std::string name(path);
    const auto separator = name.find_last_of("\\/");
    if (separator != std::string::npos)
        name.erase(0, separator + 1);
    for (char& ch : name) ch = char(std::toupper(uint8_t(ch)));
    if (name.size() != 12 || name.compare(0, 6, "MOVIE_") != 0 ||
        !std::isdigit(uint8_t(name[6])) || !std::isdigit(uint8_t(name[7])) || name.substr(8) != ".PSS")
        return false;

    auto file = g_game_directory / "Videos" / (name.substr(0, 8) + ".mp4");
    std::error_code error;
    if (!std::filesystem::is_regular_file(file, error))
    {
        std::fprintf(stderr, "  [FMV] arquivo ausente: %s\n", name.c_str());
        return false;
    }
    std::lock_guard<std::mutex> lock(g_fmv_mutex);
    g_fmv_pending = {std::move(file), owner, end_callback};
    return true;
}

void ps2_fmv_cancel(uint32_t owner)
{
    std::lock_guard<std::mutex> lock(g_fmv_mutex);
    if (g_fmv_pending.owner == owner)
        g_fmv_pending = {};
    g_fmv_cancel_owner = owner;
}

void ps2_window_start()
{
    if (g_gswin)
        std::thread(ps2_window_thread).detach();
}

// Excecoes que o signal() do C nao pega (estouro de pilha, divisao inteira por zero, instrucao ilegal...).
static LONG WINAPI ps2_exception_filter(EXCEPTION_POINTERS* ep)
{
    ps2_begin_crash_log();
    ps2_restore_display();
    std::fprintf(stderr, "\n  [CRASH] excecao do Windows 0x%08lX no endereco %p\n",
        (unsigned long)ep->ExceptionRecord->ExceptionCode, ep->ExceptionRecord->ExceptionAddress);

    if (ep->ExceptionRecord->ExceptionCode == 0xC00000FDu)
        std::fprintf(stderr, "  (estouro de pilha: tente aumentar --stack ou procure recursao no jogo)\n");

    std::fflush(stderr);
    on_crash(SIGSEGV);
    return EXCEPTION_EXECUTE_HANDLER;
}

void ps2_install_crash_filter()
{
    SetUnhandledExceptionFilter(ps2_exception_filter);
}

void ps2_begin_crash_log()
{
    // The launcher supervisor retains the recent output from this pipe.
    // A direct launch has no supervisor, so create a report only on failure.
    static bool started = false;
    char supervised[2]{};
    const bool monitored = GetEnvironmentVariableA("ROJ_CRASH_SUPERVISED", supervised, sizeof supervised) == 1 && supervised[0] == '1';
    if (started || (monitored && GetFileType(GetStdHandle(STD_ERROR_HANDLE)) == FILE_TYPE_PIPE)) return;
    started = true;
    wchar_t executable[32768]{};
    if (GetModuleFileNameW(nullptr, executable, DWORD(std::size(executable))))
    {
        const auto path = std::filesystem::path(executable).parent_path() / L"RingOfJudgment-crash.log";
        _wfreopen(path.c_str(), L"w", stderr);
    }
}

// Usa um temporizador de alta resolucao por thread, sem espera ocupada.
static void irq_wait_tick()
{
    static HANDLE timer = []() -> HANDLE {
        const char* enable = std::getenv("PS2_IRQ_HIGHRES");
        if (enable && std::strcmp(enable, "1") != 0)
            return nullptr;

        // Valor documentado, inclusive para SDKs MinGW sem a constante.
        constexpr DWORD high_resolution = 0x00000002u;
        HANDLE result = CreateWaitableTimerExW(nullptr, nullptr, high_resolution,
                                               TIMER_MODIFY_STATE | SYNCHRONIZE);
        if (!result)
            std::fprintf(stderr, "  [irq] temporizador de alta resolucao indisponivel (erro %lu)\n",
                         (unsigned long)GetLastError());
        return result;
    }();

    if (timer)
    {
        LARGE_INTEGER due{};
        // O compare moderno chega em ~1,017 ms. Acordar a cada 1 ms no
        // Windows pode cruzar dois compares com a latencia do agendador.
        due.QuadPart = g_gs_timer_clock ? -2500 : -10000;
        if (SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE) &&
            WaitForSingleObject(timer, INFINITE) == WAIT_OBJECT_0)
        {
            g_irq_wait_highres_active.store(true, std::memory_order_relaxed);
            return;
        }

        std::fprintf(stderr, "  [irq] falha na espera de alta resolucao (erro %lu)\n",
                     (unsigned long)GetLastError());
        CloseHandle(timer);
        timer = nullptr;
        g_irq_wait_highres_active.store(false, std::memory_order_relaxed);
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(1));
}

// O runtime espera com sleep_for(1 ms) (escalonador ocioso e tique do vsync). No Windows um sleep de 1 ms dura ~15,6 ms
// por padrao, e em janelas nao focadas/ocultas o sistema ainda agrupa os timers. Pede 0,5 ms de resolucao, desliga o
// "power throttling" do processo e sobe um pouco a prioridade, para o jogo nao ser desacelerado pelo proprio sistema.
void ps2_win_tune()
{
    typedef LONG (NTAPI * NtSetTimerResolutionFn)(ULONG, BOOLEAN, PULONG);
    typedef BOOL (WINAPI * SetProcessInformationFn)(HANDLE, int, LPVOID, DWORD);

    if (std::getenv("PS2_NOTUNE") != nullptr)
        return;

    HMODULE nt = GetModuleHandleA("ntdll.dll");

    if (nt != nullptr)
    {
        NtSetTimerResolutionFn f = reinterpret_cast<NtSetTimerResolutionFn>(reinterpret_cast<void*>(GetProcAddress(nt, "NtSetTimerResolution")));

        if (f != nullptr)
        {
            ULONG cur = 0;
            f(5000, TRUE, &cur);
            std::fprintf(stderr, "  [win] resolucao do timer: %.2f ms\n", (double)cur / 10000.0);
        }
    }

    struct ProcPowerThrottling { ULONG Version; ULONG ControlMask; ULONG StateMask; } st = { 1, 0x5, 0 };   // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, ambos desligados
    HMODULE k = GetModuleHandleA("kernel32.dll");

    if (k != nullptr)
    {
        SetProcessInformationFn sp = reinterpret_cast<SetProcessInformationFn>(reinterpret_cast<void*>(GetProcAddress(k, "SetProcessInformation")));

        if (sp != nullptr)
            sp(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, &st, sizeof st);
    }

    SetPriorityClass(GetCurrentProcess(), ABOVE_NORMAL_PRIORITY_CLASS);
}

bool ps2_key_down(int vk)
{
    if (g_hwnd != nullptr && GetForegroundWindow() != g_hwnd)
        return false;

    return (GetAsyncKeyState(vk) & 0x8000) != 0;
}

// XInput carregado em tempo de execucao (sem precisar de -lxinput). Aceita ate 4 controles; usa o primeiro conectado.
// PS2_NOXINPUT=1 desliga.
bool ps2_xinput_state(uint16_t* buttons, uint8_t* lt, uint8_t* rt, int16_t* sticks)
{
    if (g_hwnd != nullptr && GetForegroundWindow() != g_hwnd)
        return false;
    struct XState { uint32_t packet; uint16_t buttons; uint8_t lt, rt; int16_t lx, ly, rx, ry; };   // XINPUT_STATE
    typedef uint32_t (__stdcall *GetStateFn)(uint32_t, XState*);

    static const bool disabled = std::getenv("PS2_NOXINPUT") != nullptr;
    static GetStateFn fn = nullptr;
    static bool tried = false;
    static int pad_index = -1;
    static ULONGLONG next_scan = 0;

    if (disabled)
        return false;

    if (!tried)
    {
        tried = true;
        static const char* const dlls[] = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };

        for (const char* name : dlls)
        {
            HMODULE m = LoadLibraryA(name);

            if (m != nullptr && (fn = reinterpret_cast<GetStateFn>(GetProcAddress(m, "XInputGetState"))) != nullptr)
                break;
        }
    }

    if (fn == nullptr)
        return false;

    XState st = {};

    if (pad_index >= 0)
    {
        if (fn((uint32_t)pad_index, &st) != 0)
        {
            std::fprintf(stderr, "  [pad] controle %d desconectado\n", pad_index);
            pad_index = -1;
            return false;
        }
    }
    else
    {
        const ULONGLONG now = GetTickCount64();

        if (now < next_scan)   // procurar controle ausente e lento no XInput antigo: so tenta 1x por segundo
            return false;

        next_scan = now + 1000;

        for (int i = 0; i < 4; i++)
        {
            if (fn((uint32_t)i, &st) == 0)
            {
                pad_index = i;
                std::fprintf(stderr, "  [pad] controle XInput %d conectado\n", i);
                break;
            }
        }

        if (pad_index < 0)
            return false;
    }

    *buttons = st.buttons; *lt = st.lt; *rt = st.rt;
    sticks[0] = st.lx; sticks[1] = st.ly; sticks[2] = st.rx; sticks[3] = st.ry;
    return true;
}
#else
const roj::Settings& ps2_settings()
{
    static const auto settings = roj::read_settings("RingOfJudgment.ini");
    return settings;
}
bool ps2_fmv_play(const char*, uint32_t, uint32_t) { return false; }
void ps2_fmv_cancel(uint32_t) {}
void ps2_window_start() {}
bool ps2_key_down(int) { return false; }
bool ps2_xinput_state(uint16_t*, uint8_t*, uint8_t*, int16_t*) { return false; }
void ps2_install_crash_filter() {}
void ps2_win_tune() {}
static void irq_wait_tick()
{
    std::this_thread::sleep_for(std::chrono::microseconds(g_gs_timer_clock ? 250 : 1000));
}
#endif
""";

    public static string FunctionName(ElfSymbol function)
    {
        string clean = Regex.Replace(function.Name, "[^A-Za-z0-9_]", "_");

        return $"f_{function.Address:X8}_{clean}";
    }

    public static Result EmitFunction(
        ElfImage image,
        ElfSymbol function,
        IReadOnlyDictionary<uint, string>? names = null,
        JumpTableResolver? jumpTables = null,
        IReadOnlyDictionary<uint, string>? directCalls = null,
        bool comments = true,
        bool includeHeader = true)
    {
        Writer writer = new(names, jumpTables, directCalls, comments, includeHeader);

        return writer.Emit(image, function);
    }

    private static bool HasDelaySlot(MipsInstruction instruction)
    {
        return instruction.IsRelativeBranch ||
               instruction.Mnemonic is "J" or "JAL" or "JR" or "JALR";
    }

    private static bool IsCall(MipsInstruction instruction)
    {
        return instruction.Mnemonic == "JAL" ||
               instruction.IsBranchAndLink;
    }

    private static string Lo(int register) => $"c.r[{register}].lo";

    private static HashSet<uint> CollectLabels(
        MipsInstruction[] code,
        uint start,
        uint end,
        IEnumerable<uint> extraTargets)
    {
        HashSet<uint> labels = new();

        // Destinos de jump tables.
        foreach (uint target in extraTargets)
        {
            if (target >= start && target < end)
                labels.Add(target);
        }

        for (int i = 0; i < code.Length; i++)
        {
            uint address = start + (uint)(i * 4);

            if (IsCall(code[i]))
                continue;

            if (code[i].TryGetBranchTarget(address, out uint target) &&
                target >= start &&
                target < end)
            {
                labels.Add(target);
            }
        }

        // Delay slot que tambem e alvo de salto: a instrucao seguinte
        // precisa de um label para o caminho que nao passa pelo slot.
        for (int i = 0; i + 2 < code.Length; i++)
        {
            if (HasDelaySlot(code[i]) &&
                labels.Contains(start + (uint)((i + 1) * 4)))
            {
                labels.Add(start + (uint)((i + 2) * 4));
            }
        }

        return labels;
    }

    private static string? Condition(MipsInstruction i)
    {
        string rs = Lo(i.Rs);
        string rt = Lo(i.Rt);

        return i.Mnemonic switch
        {
            "BEQ" or "BEQL" => $"{rs} == {rt}",
            "BNE" or "BNEL" => $"{rs} != {rt}",
            "BLEZ" or "BLEZL" => $"(int64_t){rs} <= 0",
            "BGTZ" or "BGTZL" => $"(int64_t){rs} > 0",
            "BLTZ" or "BLTZL" or "BLTZAL" or "BLTZALL" => $"(int64_t){rs} < 0",
            "BGEZ" or "BGEZL" or "BGEZAL" or "BGEZALL" => $"(int64_t){rs} >= 0",
            "BC1T" or "BC1TL" => "(c.fcr31 & 0x00800000u) != 0",
            "BC1F" or "BC1FL" => "(c.fcr31 & 0x00800000u) == 0",
            _ => null
        };
    }

    // Uma instrucao simples. "" = nada a emitir; null = sem suporte.
    private static string? Statement(MipsInstruction i, uint pc)
    {
        if (i.IsVu0Macro)
            return Vu0Statement(i);

        int rs = i.Rs;
        int rt = i.Rt;
        int rd = i.Rd;
        int sa = i.Shamt;

        string rsLo = Lo(rs);
        string rtLo = Lo(rt);

        uint simm32 = unchecked((uint)(int)i.SignedImmediate);
        long simm64 = i.SignedImmediate;
        uint uimm = i.Immediate;

        string address = $"(uint32_t){rsLo} + 0x{simm32:X}u";

        string SetRd(string expression) =>
            rd == 0
                ? "/* destino e $zero */"
                : $"{Lo(rd)} = {expression};";

        string SetRt(string expression) =>
            rt == 0
                ? "/* destino e $zero */"
                : $"{Lo(rt)} = {expression};";

        switch (i.Mnemonic)
        {
            case "NOP":
            case "SYNC":
                return "";

            case "SYSCALL":
                return "ps2_syscall(c);";

            // 32 bits (resultado estendido com sinal)
            case "ADDIU":
            case "ADDI":
                return SetRt($"S32((uint32_t){rsLo} + 0x{simm32:X}u)");

            case "ADDU":
            case "ADD":
                return SetRd($"S32((uint32_t){rsLo} + (uint32_t){rtLo})");

            case "SUBU":
            case "SUB":
                return SetRd($"S32((uint32_t){rsLo} - (uint32_t){rtLo})");

            // 64 bits
            case "DADDIU":
            case "DADDI":
                return SetRt($"{rsLo} + (uint64_t)({simm64}LL)");

            case "DADDU":
            case "DADD":
                return SetRd($"{rsLo} + {rtLo}");

            case "DSUBU":
            case "DSUB":
                return SetRd($"{rsLo} - {rtLo}");

            // logicas
            case "AND": return SetRd($"{rsLo} & {rtLo}");
            case "OR": return SetRd($"{rsLo} | {rtLo}");
            case "XOR": return SetRd($"{rsLo} ^ {rtLo}");
            case "NOR": return SetRd($"~({rsLo} | {rtLo})");

            case "ANDI": return SetRt($"{rsLo} & 0x{uimm:X}ull");
            case "ORI": return SetRt($"{rsLo} | 0x{uimm:X}ull");
            case "XORI": return SetRt($"{rsLo} ^ 0x{uimm:X}ull");

            case "LUI":
                uint upper = uimm << 16;
                return SetRt($"S32(0x{upper:X8}u)");

            // comparacoes
            case "SLT":
                return SetRd($"((int64_t){rsLo} < (int64_t){rtLo}) ? 1 : 0");

            case "SLTU":
                return SetRd($"({rsLo} < {rtLo}) ? 1 : 0");

            case "SLTI":
                return SetRt($"((int64_t){rsLo} < {simm64}LL) ? 1 : 0");

            case "SLTIU":
                return SetRt($"({rsLo} < (uint64_t)({simm64}LL)) ? 1 : 0");

            // deslocamentos
            case "SLL":
                return SetRd($"S32((uint32_t){rtLo} << {sa})");

            case "SRL":
                return SetRd($"S32((uint32_t){rtLo} >> {sa})");

            case "SRA":
                return SetRd($"(uint64_t)(int64_t)((int32_t){rtLo} >> {sa})");

            case "SLLV":
                return SetRd($"S32((uint32_t){rtLo} << ({rsLo} & 31))");

            case "SRLV":
                return SetRd($"S32((uint32_t){rtLo} >> ({rsLo} & 31))");

            case "SRAV":
                return SetRd($"(uint64_t)(int64_t)((int32_t){rtLo} >> ({rsLo} & 31))");

            case "DSLL": return SetRd($"{rtLo} << {sa}");
            case "DSRL": return SetRd($"{rtLo} >> {sa}");
            case "DSRA": return SetRd($"(uint64_t)((int64_t){rtLo} >> {sa})");
            case "DSLL32": return SetRd($"{rtLo} << {sa + 32}");
            case "DSRL32": return SetRd($"{rtLo} >> {sa + 32}");
            case "DSRA32": return SetRd($"(uint64_t)((int64_t){rtLo} >> {sa + 32})");

            case "MOVZ":
                return rd == 0 ? "" : $"if ({rtLo} == 0) {Lo(rd)} = {rsLo};";

            case "MOVN":
                return rd == 0 ? "" : $"if ({rtLo} != 0) {Lo(rd)} = {rsLo};";

            // cargas
            case "LB": return SetRt($"(uint64_t)(int64_t)mrd<int8_t>(c, {address})");
            case "LBU": return SetRt($"mrd<uint8_t>(c, {address})");
            case "LH": return SetRt($"(uint64_t)(int64_t)mrd<int16_t>(c, {address})");
            case "LHU": return SetRt($"mrd<uint16_t>(c, {address})");
            case "LW": return SetRt($"S32(mrd<uint32_t>(c, {address}))");
            case "LWU": return SetRt($"mrd<uint32_t>(c, {address})");
            case "LD": return SetRt($"mrd<uint64_t>(c, {address})");

            case "LQ":
                return rt == 0
                    ? ""
                    : $"c.r[{rt}] = mrd<Reg128>(c, ({address}) & ~15u);";

            // gravacoes
            case "SB": return $"mwr<uint8_t>(c, {address}, (uint8_t){rtLo});";
            case "SH": return $"mwr<uint16_t>(c, {address}, (uint16_t){rtLo});";
            case "SW": return $"mwr<uint32_t>(c, {address}, (uint32_t){rtLo});";
            case "SD": return $"mwr<uint64_t>(c, {address}, {rtLo});";
            case "SQ": return $"mwr<Reg128>(c, ({address}) & ~15u, c.r[{rt}]);";

            // PADDUB rd, rs, $zero e o move de 128 bits do Metrowerks
            case "PADDUB":
                if (rd == 0)
                    return "";

                return rt == 0
                    ? $"c.r[{rd}] = c.r[{rs}];"
                    : $"mmi_paddub(c, {rd}, {rs}, {rt});";

            default:
                return TemplateFor(i, pc, address);
        }
    }

    // VU0 em modo macro. Campos: dest = Rs & 0xF, fd = Shamt, fs = Rd, ft = Rt.
    private static string? Vu0Statement(MipsInstruction i)
    {
        int dest = i.Rs & 0xF;
        int fd = i.Shamt;
        int fs = i.Rd;
        int ft = i.Rt;
        int fsf = i.Rs & 3;
        int ftf = (i.Rs >> 2) & 3;
        string name = i.Mnemonic;

        if (Vu0.TryParseArith(name, out string op, out string source, out bool toAcc))
        {
            return $"vu_arith(c, VU_{op}, VU_SRC_{source}, {dest}, {fd}, {fs}, {ft}, {(toAcc ? 1 : 0)});";
        }

        int imm5 = i.Shamt >= 16 ? i.Shamt - 32 : i.Shamt;

        return name switch
        {
            "VNOP" or "VWAITQ" => "",

            // Microprogramas do VU0: por enquanto so um stub que registra a chamada (ps2_hle: vu0_callms).
            "VCALLMS" => $"vu0_callms(c, 0x{((i.Raw >> 6) & 0x7FFF) * 8:X}u, 0x{i.Raw:X8}u);",
            "VCALLMSR" => "vu0_callms(c, c.vi[27] * 8u, 0u);",

            "VOPMSUB" => $"vu_opmsub(c, {fd}, {fs}, {ft});",
            "VOPMULA" => $"vu_opmula(c, {fs}, {ft});",

            "VMOVE" => $"vu_move(c, {dest}, {ft}, {fs});",
            "VMR32" => $"vu_mr32(c, {dest}, {ft}, {fs});",
            "VABS" => $"vu_abs(c, {dest}, {ft}, {fs});",

            "VITOF0" => $"vu_itof(c, {dest}, {ft}, {fs}, 0);",
            "VITOF4" => $"vu_itof(c, {dest}, {ft}, {fs}, 4);",
            "VITOF12" => $"vu_itof(c, {dest}, {ft}, {fs}, 12);",
            "VITOF15" => $"vu_itof(c, {dest}, {ft}, {fs}, 15);",

            "VFTOI0" => $"vu_ftoi(c, {dest}, {ft}, {fs}, 0);",
            "VFTOI4" => $"vu_ftoi(c, {dest}, {ft}, {fs}, 4);",
            "VFTOI12" => $"vu_ftoi(c, {dest}, {ft}, {fs}, 12);",
            "VFTOI15" => $"vu_ftoi(c, {dest}, {ft}, {fs}, 15);",

            "VCLIPw" => $"vu_clipw(c, {fs}, {ft});",

            "VDIV" => $"vu_div(c, {fs}, {fsf}, {ft}, {ftf});",
            "VSQRT" => $"vu_sqrt(c, {ft}, {ftf});",
            "VRSQRT" => $"vu_rsqrt(c, {fs}, {fsf}, {ft}, {ftf});",

            "VIADD" => $"vu_iadd(c, {fd}, {fs}, {ft});",
            "VISUB" => $"vu_isub(c, {fd}, {fs}, {ft});",
            "VIAND" => $"vu_iand(c, {fd}, {fs}, {ft});",
            "VIOR" => $"vu_ior(c, {fd}, {fs}, {ft});",
            "VIADDI" => $"vu_iaddi(c, {ft}, {fs}, {imm5});",

            "VMTIR" => $"vu_mtir(c, {ft}, {fs}, {fsf});",
            "VMFIR" => $"vu_mfir(c, {dest}, {ft}, {fs});",

            _ => null
        };
    }

    // Instrucoes cujo destino e o campo rd / rt (escritas em $zero sao puladas).
    private static readonly HashSet<string> WritesRd = new()
    {
        "MFHI", "MFLO", "MFHI1", "MFLO1", "DSLLV", "DSRLV", "DSRAV",
        "PAND", "POR", "PXOR", "PNOR", "PCPYLD", "PCPYUD",
        "PADDW", "PSUBW", "PADDH", "PSUBH", "PADDB", "PSUBB",
        "PSLLH", "PSRLH", "PSRAH", "PSLLW", "PSRLW", "PSRAW",
        "PEXTLB", "PEXTUB", "PEXTLH", "PEXTUH", "PEXTLW", "PEXTUW",
        "QFSRV",
        "PCGTH", "PMINH", "PMAXH", "PMINW", "PSUBUB", "PADDUH", "PPACB", "PPACH",
        "PCPYH", "PEXCW", "PEXEW", "PMFHI", "PMFLO"
    };

    private static readonly HashSet<string> WritesRt = new()
    {
        "MFC1", "MFC0", "LWL", "LWR", "LDL", "LDR", "QMFC2", "CFC2"
    };

    // Instrucoes que viram uma linha de C++ a partir de um modelo. Campos:
    // {fd} {fs} {ft} (COP1), {rs} {rt} {rd}, {sa}, {imm}, {rsLo} {rtLo}, {addr}, {pc}.
    // Sem aspas nem barras invertidas aqui dentro.
    private static readonly Dictionary<string, string> Templates = new()
    {
        // FPU: movimentacao e memoria
        ["MTC1"] = "c.f[{fs}] = (uint32_t){rtLo};",
        ["MFC1"] = "c.r[{rt}].lo = S32(c.f[{fs}]);",
        ["LWC1"] = "c.f[{ft}] = mrd<uint32_t>(c, {addr});",
        ["SWC1"] = "mwr<uint32_t>(c, {addr}, c.f[{ft}]);",

        // FPU: aritmetica
        ["ADD.S"] = "fpu_add(c, {fd}, {fs}, {ft});",
        ["SUB.S"] = "fpu_sub(c, {fd}, {fs}, {ft});",
        ["MUL.S"] = "fpu_mul(c, {fd}, {fs}, {ft});",
        ["DIV.S"] = "fpu_div(c, {fd}, {fs}, {ft});",
        ["SQRT.S"] = "fpu_sqrt(c, {fd}, {ft});",
        ["RSQRT.S"] = "fpu_rsqrt(c, {fd}, {fs}, {ft});",
        ["ABS.S"] = "c.f[{fd}] = c.f[{fs}] & 0x7FFFFFFFu;",
        ["NEG.S"] = "c.f[{fd}] = c.f[{fs}] ^ 0x80000000u;",
        ["MOV.S"] = "c.f[{fd}] = c.f[{fs}];",
        ["MAX.S"] = "fpu_max(c, {fd}, {fs}, {ft});",
        ["MIN.S"] = "fpu_min(c, {fd}, {fs}, {ft});",

        // FPU: acumulador
        ["ADDA.S"] = "fpu_adda(c, {fs}, {ft});",
        ["SUBA.S"] = "fpu_suba(c, {fs}, {ft});",
        ["MULA.S"] = "fpu_mula(c, {fs}, {ft});",
        ["MADD.S"] = "fpu_madd(c, {fd}, {fs}, {ft});",
        ["MSUB.S"] = "fpu_msub(c, {fd}, {fs}, {ft});",
        ["MADDA.S"] = "fpu_madda(c, {fs}, {ft});",
        ["MSUBA.S"] = "fpu_msuba(c, {fs}, {ft});",

        // FPU: conversao e comparacao
        ["CVT.S.W"] = "fpu_cvt_s_w(c, {fd}, {fs});",
        ["CVT.W.S"] = "fpu_cvt_w_s(c, {fd}, {fs});",
        ["C.F.S"] = "fpu_set_c(c, false);",
        ["C.EQ.S"] = "fpu_c_eq(c, {fs}, {ft});",
        ["C.LT.S"] = "fpu_c_lt(c, {fs}, {ft});",
        ["C.LE.S"] = "fpu_c_le(c, {fs}, {ft});",

        // multiplicacao e divisao inteira
        ["MFHI"] = "c.r[{rd}].lo = c.hi;",
        ["MFLO"] = "c.r[{rd}].lo = c.lo;",
        ["MTHI"] = "c.hi = {rsLo};",
        ["MTLO"] = "c.lo = {rsLo};",
        ["MULT"] = "ee_mult(c, {rd}, {rs}, {rt});",
        ["MULTU"] = "ee_multu(c, {rd}, {rs}, {rt});",
        ["DIV"] = "ee_div(c, {rs}, {rt});",
        ["DIVU"] = "ee_divu(c, {rs}, {rt});",

        // multiplicacao/divisao do pipeline 1
        ["MULT1"] = "ee_mult1(c, {rd}, {rs}, {rt});",
        ["MULTU1"] = "ee_multu1(c, {rd}, {rs}, {rt});",
        ["DIV1"] = "ee_div1(c, {rs}, {rt});",
        ["DIVU1"] = "ee_divu1(c, {rs}, {rt});",
        ["MFHI1"] = "c.r[{rd}].lo = c.hi1;",
        ["MFLO1"] = "c.r[{rd}].lo = c.lo1;",
        ["MTHI1"] = "c.hi1 = {rsLo};",
        ["MTLO1"] = "c.lo1 = {rsLo};",

        // deslocamentos variaveis de 64 bits
        ["DSLLV"] = "c.r[{rd}].lo = {rtLo} << ({rsLo} & 63);",
        ["DSRLV"] = "c.r[{rd}].lo = {rtLo} >> ({rsLo} & 63);",
        ["DSRAV"] = "c.r[{rd}].lo = (uint64_t)((int64_t){rtLo} >> ({rsLo} & 63));",

        // cargas e gravacoes desalinhadas
        ["LWL"] = "ee_lwl(c, {rt}, {addr});",
        ["LWR"] = "ee_lwr(c, {rt}, {addr});",
        ["SWL"] = "ee_swl(c, {rt}, {addr});",
        ["SWR"] = "ee_swr(c, {rt}, {addr});",
        ["LDL"] = "ee_ldl(c, {rt}, {addr});",
        ["LDR"] = "ee_ldr(c, {rt}, {addr});",
        ["SDL"] = "ee_sdl(c, {rt}, {addr});",
        ["SDR"] = "ee_sdr(c, {rt}, {addr});",

        // cache e prefetch nao tem efeito no host
        ["CACHE"] = "",
        ["PREF"] = "",

        // COP0
        ["MFC0"] = "c.r[{rt}].lo = S32(ps2_mfc0(c, {rd}));",
        ["MTC0"] = "ps2_mtc0(c, {rd}, (uint32_t){rtLo});",

        // MMI
        ["PAND"] = "mmi_pand(c, {rd}, {rs}, {rt});",
        ["POR"] = "mmi_por(c, {rd}, {rs}, {rt});",
        ["PXOR"] = "mmi_pxor(c, {rd}, {rs}, {rt});",
        ["PNOR"] = "mmi_pnor(c, {rd}, {rs}, {rt});",
        ["PCPYLD"] = "mmi_pcpyld(c, {rd}, {rs}, {rt});",
        ["PCPYUD"] = "mmi_pcpyud(c, {rd}, {rs}, {rt});",
        ["PADDW"] = "mmi_paddw(c, {rd}, {rs}, {rt});",
        ["PSUBW"] = "mmi_psubw(c, {rd}, {rs}, {rt});",
        ["PADDH"] = "mmi_paddh(c, {rd}, {rs}, {rt});",
        ["PSUBH"] = "mmi_psubh(c, {rd}, {rs}, {rt});",
        ["PADDB"] = "mmi_paddb(c, {rd}, {rs}, {rt});",
        ["PSUBB"] = "mmi_psubb(c, {rd}, {rs}, {rt});",
        ["PSLLH"] = "mmi_psllh(c, {rd}, {rt}, {sa});",
        ["PSRLH"] = "mmi_psrlh(c, {rd}, {rt}, {sa});",
        ["PSRAH"] = "mmi_psrah(c, {rd}, {rt}, {sa});",
        ["PSLLW"] = "mmi_psllw(c, {rd}, {rt}, {sa});",
        ["PSRLW"] = "mmi_psrlw(c, {rd}, {rt}, {sa});",
        ["PSRAW"] = "mmi_psraw(c, {rd}, {rt}, {sa});",
        ["PEXTLB"] = "mmi_pextlb(c, {rd}, {rs}, {rt});",
        ["PEXTUB"] = "mmi_pextub(c, {rd}, {rs}, {rt});",
        ["PEXTLH"] = "mmi_pextlh(c, {rd}, {rs}, {rt});",
        ["PEXTUH"] = "mmi_pextuh(c, {rd}, {rs}, {rt});",
        ["PEXTLW"] = "mmi_pextlw(c, {rd}, {rs}, {rt});",
        ["PEXTUW"] = "mmi_pextuw(c, {rd}, {rs}, {rt});",
        ["QFSRV"] = "mmi_qfsrv(c, {rd}, {rs}, {rt});",
        ["MTSAB"] = "ee_mtsab(c, {rs}, {imm});",
        ["MTSAH"] = "ee_mtsah(c, {rs}, {imm});",

        // MMI: comparacoes, min/max, pack, trocas
        ["PCGTH"] = "mmi_pcgth(c, {rd}, {rs}, {rt});",
        ["PMINH"] = "mmi_pminh(c, {rd}, {rs}, {rt});",
        ["PMAXH"] = "mmi_pmaxh(c, {rd}, {rs}, {rt});",
        ["PMINW"] = "mmi_pminw(c, {rd}, {rs}, {rt});",
        ["PSUBUB"] = "mmi_psubub(c, {rd}, {rs}, {rt});",
        ["PADDUH"] = "mmi_padduh(c, {rd}, {rs}, {rt});",
        ["PPACB"] = "mmi_ppacb(c, {rd}, {rs}, {rt});",
        ["PPACH"] = "mmi_ppach(c, {rd}, {rs}, {rt});",
        ["PCPYH"] = "mmi_pcpyh(c, {rd}, {rt});",
        ["PEXCW"] = "mmi_pexcw(c, {rd}, {rt});",
        ["PEXEW"] = "mmi_pexew(c, {rd}, {rt});",
        ["PMFHI"] = "mmi_pmfhi(c, {rd});",
        ["PMFLO"] = "mmi_pmflo(c, {rd});",
        ["PMULTH"] = "mmi_pmulth(c, {rd}, {rs}, {rt});",
        ["PMADDH"] = "mmi_pmaddh(c, {rd}, {rs}, {rt});",
        ["PMSUBH"] = "mmi_pmsubh(c, {rd}, {rs}, {rt});",
        ["PMULTW"] = "mmi_pmultw(c, {rd}, {rs}, {rt});",
        ["PMADDW"] = "mmi_pmaddw(c, {rd}, {rs}, {rt});",
        ["PMSUBW"] = "mmi_pmsubw(c, {rd}, {rs}, {rt});",
        ["MADD"] = "ee_madd(c, {rd}, {rs}, {rt});",

        // interrupcoes (COP0)
        ["EI"] = "ps2_ei(c);",
        ["DI"] = "ps2_di(c);",

        // COP2: transferencias e memoria
        ["QMFC2"] = "c.r[{rt}] = vu_qmfc2(c, {rd});",
        ["QMTC2"] = "vu_qmtc2(c, {rd}, c.r[{rt}]);",
        ["CFC2"] = "c.r[{rt}].lo = S32(vu_cfc2(c, {rd}));",
        ["CTC2"] = "vu_ctc2(c, {rd}, (uint32_t){rtLo});",
        ["LQC2"] = "vu_qmtc2(c, {rt}, mrd<Reg128>(c, ({addr}) & ~15u));",
        ["SQC2"] = "mwr<Reg128>(c, ({addr}) & ~15u, vu_qmfc2(c, {rt}));",

        ["BREAK"] = "ps2_break(c, {pc});"
    };

    private static string? TemplateFor(MipsInstruction i, uint pc, string addr)
    {
        // Destinos em $zero: nada a gravar.
        if (WritesRd.Contains(i.Mnemonic) && i.Rd == 0)
            return "";

        if (WritesRt.Contains(i.Mnemonic) && i.Rt == 0)
            return "";

        // Registradores de controle da FPU: so o fcr31 (e o fcr0, so leitura).
        if (i.Mnemonic == "CFC1")
        {
            if (i.Rt == 0)
                return "";

            return i.Rd switch
            {
                31 => $"c.r[{i.Rt}].lo = S32(c.fcr31);",
                0 => $"c.r[{i.Rt}].lo = 0x2E00ull;",
                _ => $"c.r[{i.Rt}].lo = 0;"
            };
        }

        if (i.Mnemonic == "CTC1")
        {
            return i.Rd == 31
                ? $"c.fcr31 = (uint32_t){Lo(i.Rt)};"
                : "";
        }

        if (!Templates.TryGetValue(i.Mnemonic, out string? template))
            return null;

        return template
            .Replace("{fd}", i.Shamt.ToString())
            .Replace("{fs}", i.Rd.ToString())
            .Replace("{ft}", i.Rt.ToString())
            .Replace("{rs}", i.Rs.ToString())
            .Replace("{rt}", i.Rt.ToString())
            .Replace("{rd}", i.Rd.ToString())
            .Replace("{sa}", i.Shamt.ToString())
            .Replace("{imm}", i.Immediate.ToString())
            .Replace("{rsLo}", Lo(i.Rs))
            .Replace("{rtLo}", Lo(i.Rt))
            .Replace("{addr}", addr)
            .Replace("{pc}", $"0x{pc:X8}u");
    }

    private sealed class Writer
    {
        private const string I1 = "    ";
        private const string I2 = "        ";

        private readonly StringBuilder _sb = new();
        private readonly IReadOnlyDictionary<uint, string>? _names;
        private readonly Dictionary<string, int> _byMnemonic = new();
        private readonly JumpTableResolver? _jumpTables;
        private readonly Dictionary<uint, (uint TableAddress, uint[] Targets)> _tables = new();
        private readonly IReadOnlyDictionary<uint, string>? _directCalls;
        private readonly bool _comments;
        private readonly bool _includeHeader;
        private int _unsupported;
        private int _dispatched;

        public Writer(
            IReadOnlyDictionary<uint, string>? names,
            JumpTableResolver? jumpTables,
            IReadOnlyDictionary<uint, string>? directCalls,
            bool comments,
            bool includeHeader)
        {
            _names = names;
            _jumpTables = jumpTables;
            _directCalls = directCalls;
            _comments = comments;
            _includeHeader = includeHeader;
        }

        // Comentarios so quando pedidos (o executavel inteiro gera dezenas de MB).
        private void Comment(string indent, string text)
        {
            if (_comments)
                _sb.AppendLine($"{indent}// {text}");
        }

        public Result Emit(ElfImage image, ElfSymbol function)
        {
            uint start = function.Address;
            int count = checked((int)(function.Size / 4));
            uint end = start + (uint)(count * 4);

            MipsInstruction[] code = new MipsInstruction[count];

            for (int i = 0; i < count; i++)
            {
                code[i] = MipsDecoder.Decode(
                    image.ReadUInt32(start + (uint)(i * 4)));
            }

            // JR em registrador que nao e $ra: tenta resolver como jump table.
            for (int i = 0; i < count; i++)
            {
                if (code[i].Mnemonic != "JR" || code[i].Rs == 31)
                    continue;

                uint jump = start + (uint)(i * 4);
                if (_jumpTables != null &&
                    _jumpTables.TryResolve(code, i, start, end, out JumpTableResolver.Table? table, out _) &&
                    table != null)
                {
                    _tables[jump] = (table.TableAddress, table.Targets.Distinct().ToArray());
                    continue;
                }

                // FParticleSet no SLUS-20347 guarda o endereco da entrada
                // na pilha via SQ/LQ. O resolvedor normal perde esse rastreio.
                if (start == 0x0041AB80u && code[i].Rs == 4)
                    ResolveParticleTable(image, start, end, jump);
            }

            HashSet<uint> labels = CollectLabels(
                code,
                start,
                end,
                _tables.Values.SelectMany(t => t.Targets));

            if (_includeHeader)
            {
                _sb.AppendLine("#include \"ps2rt.h\"");
                _sb.AppendLine();
            }

            Comment("", $"{function.Name}  0x{start:X8}  ({function.Size} bytes)");
            _sb.AppendLine($"void {FunctionName(function)}(Ctx& c)");
            _sb.AppendLine("{");
            _sb.AppendLine($"{I1}PS2_TRACE_ENTER(0x{start:X8}u);");
            if (start == 0x0047A230u)
                _sb.AppendLine($"{I1}ps2_battle_effect_clock(c);");
            // Marca o pedido de batalha e as operacoes de BGM na captura RSSD.
            if (start is 0x00413AE0u or 0x004176A0u or 0x00417970u or 0x00417E70u)
                _sb.AppendLine($"{I1}if (g_rssd_audio_trace) rssd_trace_bgm(c, 0x{start:X8}u);");

            // AddTime de CFieldModel, quando chamado por CField::Refresh.
            if (start == 0x00320A10u)
                _sb.AppendLine($"{I1}if (g_field_clock_trace && (uint32_t)c.r[31].lo == 0x0031F3A8u) field_clock_record(c.f[12]);");

            // MoveTranslateCallback na imagem SLUS-20347.
            bool traceMoveCallback = start == 0x00321230u && labels.Contains(0x0032133Cu);
            if (traceMoveCallback)
                _sb.AppendLine($"{I1}MoveCallbackTrace movement_probe(c);");

            // Entradas das tres rotinas SDK na imagem SLUS-20347.
            string? rotationAxis = start switch
            {
                0x00202980u => "Z",
                0x00202A28u => "X",
                0x00202AD0u => "Y",
                _ => null
            };
            if (rotationAxis != null)
                _sb.AppendLine($"{I1}if (g_rotation_caller_trace) rotation_caller_record(c, '{rotationAxis}');");


            for (int i = 0; i < count; i++)
            {
                uint address = start + (uint)(i * 4);
                MipsInstruction instruction = code[i];

                bool isDelaySlot = i > 0 && HasDelaySlot(code[i - 1]);
                bool isLabel = labels.Contains(address);

                if (isLabel)
                {
                    _sb.AppendLine($"L_{address:X8}:");
                    _sb.AppendLine($"{I1}PS2_LOOP_MARK(0x{address:X8}u);");
                    _sb.AppendLine($"{I1}PS2_POLL_IRQ(c);");
                    if (traceMoveCallback && address == 0x0032133Cu)
                        _sb.AppendLine($"{I1}movement_probe.attempt();");
                }

                // Ja foi emitido junto do branch anterior.
                if (isDelaySlot && !isLabel)
                    continue;

                if (HasDelaySlot(instruction))
                {
                    MipsInstruction? delay =
                        i + 1 < count ? code[i + 1] : null;

                    Control(instruction, address, delay, start, end);

                    // O proximo e um delay slot que tambem e alvo: o
                    // caminho normal precisa pula-lo.
                    if (labels.Contains(address + 4) && address + 8 < end)
                        _sb.AppendLine($"{I1}goto L_{address + 8:X8};");
                }
                else
                {
                    // SLUS-20347 lottery easing reaches sin(pi/2). Our EE
                    // arithmetic can leave that endpoint one float ULP below
                    // one, so the original completion test never succeeds.
                    // Snap only this completed easing, not other sine results.
                    if (start == 0x0027EED0u && address == 0x0027F198u)
                    {
                        _sb.AppendLine($"{I1}if (c.f[21] == 0x3F7FFFFFu) {{");
                        _sb.AppendLine($"{I1}    const uint32_t ring = (uint32_t)c.r[17].lo;");
                        _sb.AppendLine($"{I1}    const float base = f32(mrd<uint32_t>(c, ring + 0x6Cu));");
                        _sb.AppendLine($"{I1}    const float elapsed = f32(mrd<uint32_t>(c, ring + 0x90u));");
                        _sb.AppendLine($"{I1}    if (elapsed >= base + 0.3f * (10.0f - base)) c.f[21] = 0x3F800000u;");
                        _sb.AppendLine($"{I1}}}");
                    }
                    Simple(instruction, address, I1);
                }
            }

            _sb.AppendLine("}");

            return new Result(_sb.ToString(), count, _unsupported, _byMnemonic, _dispatched);
        }

        private void ResolveParticleTable(ElfImage image, uint start, uint end, uint jump)
        {
            const uint tableAddress = 0x004CB980u;
            const int entryCount = 41;

            // Confirma o par LUI/ADDIU que materializa o endereco da tabela.
            // A carga final no $a0 e seguida pelo JR observado no log.
            bool loadsTableBase = false;
            for (uint pc = Math.Max(start, jump - 48u); pc + 4 < jump; pc += 4)
            {
                if (image.ReadUInt32(pc) == 0x3C05004Du &&
                    image.ReadUInt32(pc + 4) is 0x24A4B980u or 0x20A4B980u)
                {
                    loadsTableBase = true;
                    break;
                }
            }
            if (!loadsTableBase || image.ReadUInt32(jump - 4) != 0x8C440000u)
                return;

            // Le a tabela do ELF para emitir todos os casos locais, incluindo
            // os que nao sao destinos de branches diretos na funcao.
            HashSet<uint> targets = new();
            for (int i = 0; i < entryCount; i++)
            {
                uint target = image.ReadUInt32(tableAddress + (uint)(4 * i));
                if (target < start || target >= end || (target & 3u) != 0)
                    return;

                targets.Add(target);
            }

            if (targets.Contains(0x0041B3C4u))
                _tables[jump] = (tableAddress, targets.OrderBy(target => target).ToArray());
        }

        private void Simple(MipsInstruction instruction, uint address, string indent)
        {
            Comment(indent, $"0x{address:X8}  {instruction.Format(address)}");

            string? statement = Statement(instruction, address);

            if (statement == null)
            {
                Unsupported(instruction, address, indent);
                return;
            }

            string? statusEvent = instruction.Mnemonic switch
            {
                "VDIV" => "Div",
                "VSQRT" => "Sqrt",
                "VRSQRT" => "Rsqrt",
                "VWAITQ" => "WaitQ",
                "CFC2" when instruction.Rd == 16 && instruction.Rt != 0 => "ReadStatus",
                "CTC2" when instruction.Rd == 16 => "WriteStatus",
                _ => null
            };

            if (statusEvent != null)
                _sb.AppendLine($"{indent}if (g_vu0_status_trace) vu0_status_record(Vu0StatusEvent::{statusEvent}, 0x{address:X8}u, c.vi[16]);");

            if (statement.Length > 0)
                _sb.AppendLine($"{indent}{statement}");
        }

        private void Unsupported(MipsInstruction instruction, uint address, string indent)
        {
            _unsupported++;
            _byMnemonic[instruction.Mnemonic] =
                _byMnemonic.GetValueOrDefault(instruction.Mnemonic) + 1;

            _sb.AppendLine(
                $"{indent}ps2_unsupported(c, 0x{address:X8}u, \"{instruction.Mnemonic}\");");
        }

        private void Delay(MipsInstruction? delay, uint branchAddress, string indent)
        {
            if (delay == null)
            {
                Comment(indent, "delay slot fora do simbolo da funcao");
                return;
            }

            if (HasDelaySlot(delay))
            {
                Comment(indent, "delay slot com branch/jump: nao suportado");
                Unsupported(delay, branchAddress + 4, indent);
                return;
            }

            Simple(delay, branchAddress + 4, indent);
        }

        // Chamada direta quando o destino e uma funcao conhecida; senao, pela tabela.
        private string CallTo(uint target)
        {
            string text;

            if (_directCalls != null && _directCalls.TryGetValue(target, out string? direct))
                text = $"{direct}(c);";
            else
                text = $"ps2_call(c, 0x{target:X8}u);";

            if (_comments && _names != null && _names.TryGetValue(target, out string? name))
                text += $"  // {name}";

            return text;
        }

        private string JumpTo(uint target, uint start, uint end)
        {
            if (target >= start && target < end)
                return $"goto L_{target:X8};";

            return $"{{ {CallTo(target)} return; }}";
        }

        private void Control(
            MipsInstruction instruction,
            uint address,
            MipsInstruction? delay,
            uint start,
            uint end)
        {
            string mnemonic = instruction.Mnemonic;
            uint link = address + 8;

            Comment(I1, $"0x{address:X8}  {instruction.Format(address)}");
            _sb.AppendLine($"{I1}{{");

            if (mnemonic == "JR")
            {
                if (instruction.Rs == 31)
                {
                    Delay(delay, address, I2);
                    _sb.AppendLine($"{I2}return;");
                }
                else if (_tables.TryGetValue(address, out var table))
                {
                    // switch: o destino e lido antes do delay slot.
                    _sb.AppendLine($"{I2}uint32_t dest = (uint32_t){Lo(instruction.Rs)};");
                    Delay(delay, address, I2);
                    Comment(I2, $"jump table em 0x{table.TableAddress:X8}, {table.Targets.Length} destinos");
                    _sb.AppendLine($"{I2}switch (dest)");
                    _sb.AppendLine($"{I2}{{");

                    foreach (uint target in table.Targets)
                        _sb.AppendLine($"{I2}    case 0x{target:X8}u: goto L_{target:X8};");

                    _sb.AppendLine($"{I2}    default: ps2_unsupported(c, 0x{address:X8}u, \"JR\"); return;");
                    _sb.AppendLine($"{I2}}}");
                }
                else
                {
                    // Nao e jump table reconhecida: tail call por ponteiro de funcao
                    // (o callee volta para o nosso chamador via $ra). O runtime recusa
                    // destinos que nao sao inicio de funcao (longjmp, catch).
                    _dispatched++;
                    _sb.AppendLine($"{I2}uint32_t dest = (uint32_t){Lo(instruction.Rs)};");
                    Delay(delay, address, I2);
                    Comment(I2, "JR indireto: tail call por ponteiro");
                    _sb.AppendLine($"{I2}ps2_tailcall(c, dest);");
                    _sb.AppendLine($"{I2}return;");
                }
            }
            else if (mnemonic == "JALR")
            {
                _sb.AppendLine($"{I2}uint32_t dest = (uint32_t){Lo(instruction.Rs)};");

                if (instruction.Rd != 0)
                    _sb.AppendLine($"{I2}{Lo(instruction.Rd)} = 0x{link:X8}ull;");

                Delay(delay, address, I2);
                _sb.AppendLine($"{I2}ps2_call(c, dest);");
            }
            else if (mnemonic == "JAL")
            {
                instruction.TryGetBranchTarget(address, out uint target);

                _sb.AppendLine($"{I2}{Lo(31)} = 0x{link:X8}ull;");
                Delay(delay, address, I2);
                _sb.AppendLine($"{I2}{CallTo(target)}");
            }
            else if (mnemonic == "J")
            {
                instruction.TryGetBranchTarget(address, out uint target);

                Delay(delay, address, I2);
                _sb.AppendLine($"{I2}{JumpTo(target, start, end)}");
            }
            else
            {
                string? condition = Condition(instruction);
                instruction.TryGetBranchTarget(address, out uint target);
                bool likely = instruction.IsLikelyBranch;

                // A condicao e avaliada antes do delay slot, que pode
                // alterar os registradores comparados.
                if (condition == null)
                {
                    _sb.AppendLine($"{I2}bool take = false;");
                    Unsupported(instruction, address, I2);
                }
                else
                {
                    _sb.AppendLine($"{I2}bool take = {condition};");
                }

                if (instruction.IsBranchAndLink)
                {
                    _sb.AppendLine($"{I2}{Lo(31)} = 0x{link:X8}ull;");

                    if (likely)
                    {
                        _sb.AppendLine($"{I2}if (take)");
                        _sb.AppendLine($"{I2}{{");
                        Delay(delay, address, I2 + "    ");
                        _sb.AppendLine($"{I2}}}");
                    }
                    else
                    {
                        Delay(delay, address, I2);
                    }

                    _sb.AppendLine($"{I2}if (take) {CallTo(target)}");
                }
                else if (likely)
                {
                    // Branch likely: o delay slot so executa se desviar.
                    _sb.AppendLine($"{I2}if (take)");
                    _sb.AppendLine($"{I2}{{");
                    Delay(delay, address, I2 + "    ");
                    _sb.AppendLine($"{I2}    {JumpTo(target, start, end)}");
                    _sb.AppendLine($"{I2}}}");
                }
                else
                {
                    Delay(delay, address, I2);
                    _sb.AppendLine($"{I2}if (take) {JumpTo(target, start, end)}");
                }
            }

            _sb.AppendLine($"{I1}}}");
        }
    }
}
