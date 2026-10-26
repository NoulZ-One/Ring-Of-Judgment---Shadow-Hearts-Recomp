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
extern volatile int g_irq_pending;
void ps2_irq_poll(Ctx& c);
void ps2_irq_idle(Ctx& c);   // entrega interrupcoes quando todas as threads estao esperando
#define PS2_POLL_IRQ(c) do { if (g_irq_pending) ps2_irq_poll(c); } while (0)

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
        r = std::nextafterf(r, 0.0f);

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
// VCALLMS/VCALLMSR: inicia um microprograma do VU0 no endereco indicado (bytes da micro memoria).
void vu0_callms(Ctx& c, uint32_t address, uint32_t raw);
";

    // Runtime minimo (ps2_runtime.cpp): despacho por endereco usando a tabela
    // gerada e paradas claras para o que ainda nao existe.
    public const string RuntimeSource = """
#include "ps2_functions.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <csignal>
#include <condition_variable>
#include <fstream>
#include <string>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <thread>
#include <vector>

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
volatile int g_irq_pending = 0;
static std::atomic<uint32_t> g_irq_flags(0);   // bit 0 = VBLANK_START (causa 2), bit 1 = TIMER0 (causa 9)
static uint64_t g_timer0_mode = 0;
static uint64_t g_timer0_comp = 0;
static uint32_t g_vsync_flag1 = 0;             // SetVSyncFlag: o kernel escreve 1 aqui a cada vsync
static uint32_t g_vsync_flag2 = 0;             // ... e o CSR aqui (64 bits)
static uint32_t g_intc_mask = 0;               // bit = causa habilitada por EnableIntc
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

// Relogio do host: os timers e o vsync andam com o tempo real, para os laços de espera terminarem.
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

uint64_t ps2_hw_read(Ctx&, uint32_t phys, int size)
{
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

void ps2_hw_write(Ctx& c, uint32_t phys, uint64_t value, int size)
{
    ps2_dma_write(c, phys, value);

    // GS_CSR: escrever 1 no bit 3 limpa o VSINT
    if (phys == 0x12001000u && (value & 0x8ull))
        g_gs_vsint = false;

    // Timer 0: modo (+0x10) e valor de comparacao (+0x20); a interrupcao sai quando o jogo liga CUE e CMPE
    if (phys == 0x10000010u)
        g_timer0_mode = (value & ~0xC00ull) | (g_timer0_mode & 0xC00ull & ~value);

    if (phys == 0x10000020u)
        g_timer0_comp = value;

    if (g_hw_log < 300)
    {
        g_hw_log++;
        std::fprintf(stderr, "hw escrita  0x%08X (%d bytes) = 0x%llX\n", phys, size, (unsigned long long)value);
    }

    g_poll_address = 0;
    g_poll_count = 0;
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
            std::this_thread::sleep_for(std::chrono::milliseconds(1));

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
                    std::fprintf(stderr, "a thread principal chamou ExitThread: encerrando\n");
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
static uint32_t g_next_id = 1;
static uint64_t g_gs_imr = 0;
static unsigned g_syscall_log = 0;
static int g_last_syscall = 0x7FFFFFFF;
static unsigned g_syscall_repeat = 0;
static uint32_t g_user_syscall[512];   // funcoes instaladas por SetSyscall, indice = numero + 256

static std::atomic<uint64_t> g_activity(0);   // sobe a cada syscall; o vigia olha se parou
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

bool ps2_iso_open(const char* path)
{
    std::ifstream f(path, std::ios::binary);

    if (!f.is_open())
        return false;

    uint8_t pvd[2048];

    f.seekg(16 * 2048);
    f.read((char*)pvd, sizeof(pvd));

    if (f.gcount() != (std::streamsize)sizeof(pvd) || pvd[0] != 1 || std::memcmp(pvd + 1, "CD001", 5) != 0)
    {
        std::fprintf(stderr, "%s nao parece uma ISO9660\n", path);
        return false;
    }

    f.clear();
    f.seekg(0, std::ios::end);

    g_iso_sectors = (uint64_t)f.tellg() / 2048;
    g_iso_root_lsn = le32(pvd + 156 + 2);
    g_iso_root_size = le32(pvd + 156 + 10);

    g_iso = std::move(f);

    std::fprintf(stderr, "ISO: %s (%llu setores)\n", path, (unsigned long long)g_iso_sectors);
    return true;
}

// Ctrl+C: mostra o que o jogo estava fazendo (util quando ele fica preso em um laco).
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

// Tique de interrupcoes: VBLANK a ~59,94 Hz e timer 0 (quando o jogo o liga) a cada 1 ms.
static void irq_tick_thread()
{
    const uint64_t period = 16683333ull;
    uint64_t next_vsync = hw_now_ns() + period;
    uint64_t t0_next = 0;

    for (;;)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));

        uint64_t now = hw_now_ns();
        uint32_t flags = 0;

        while (now >= next_vsync)
        {
            next_vsync += period;
            flags |= 1u;
        }

        // CUE (bit 7) e CMPE (bit 8) ligados e causa 9 habilitada: dispara a cada "comparacao" ticks
        // do contador (576 kHz); o handler limpa o EQUF escrevendo 1 no bit 10 do modo.
        if ((g_timer0_mode & 0x180ull) == 0x180ull && g_timer0_comp != 0 && (g_intc_mask & (1u << 9)))
        {
            uint64_t t0_period = (uint64_t)g_timer0_comp * 1000000000ull / 576000ull;

            if (t0_next == 0 || now >= t0_next)
            {
                t0_next = now + t0_period;
                g_timer0_mode |= 0x400ull;
                flags |= 2u;
            }
        }
        else
        {
            t0_next = 0;
        }

        if (flags)
        {
            g_irq_flags |= flags;
            g_irq_pending = 1;
        }
    }
}

// Procura a ISO: argumento, variavel PS2_ISO, ou o nome padrao perto da pasta atual.
// (Aqui tambem liga o tratamento de Ctrl+C, porque e chamada no inicio do programa.)
void ps2_iso_open_default(int argc, char** argv)
{
    std::signal(SIGINT, on_interrupt);
    std::signal(SIGSEGV, on_crash);
    std::signal(SIGFPE, on_crash);
    std::signal(SIGILL, on_crash);

    std::thread(irq_tick_thread).detach();

    {
        const char* w = std::getenv("PS2_WATCHDOG");
        unsigned seconds = w ? (unsigned)std::atoi(w) : 20u;

        if (seconds > 0)
            std::thread(watchdog_thread, seconds).detach();
    }

    const char* candidates[] =
    {
        argc > 1 ? argv[1] : nullptr,
        std::getenv("PS2_ISO"),
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

    std::fprintf(stderr, "ISO nao encontrada (use: ps2_game.exe caminho.iso, ou a variavel PS2_ISO)\n");
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
        bool polling = (cmd == 4u || cmd == 12u || cmd == 20u || cmd == 21u);
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

    // RSSD.IRX: som. Por enquanto so registra as chamadas.
    void rssd_service(Ctx& c, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize)
    {
        rpc_log(c, "RSSD", cmd, send, ssize, recv, rsize);
        rpc_zero_recv(c, recv, rsize);

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

    // MCSERV.IRX: cartao de memoria (libmc). Protocolo deduzido do disassembly de sceMcInit/GetInfo/Sync:
    //   comando 0xFE (init): resposta de 12 bytes = { resultado, versao do mcserv, versao do mcman };
    //                        a biblioteca exige mcserv >= 0x20A e mcman >= 0x20E.
    //   demais comandos    : a palavra 0 da resposta e o resultado que sceMcSync devolve ao jogo.
    // Por enquanto o cartao esta "ausente": resultado < -2 (0 = mesmo cartao, -1 = trocado, -2 = sem formato).
    int mc_env_int(const char* name, int fallback, bool* present = nullptr)
    {
        const char* v = std::getenv(name);

        if (present)
            *present = (v != nullptr && *v != 0);

        return (v != nullptr && *v != 0) ? (int)std::strtol(v, nullptr, 0) : fallback;
    }

    // Experimento (variaveis de ambiente, lidas uma vez):
    //   PS2_MC_RESULT  resultado do comando 1 (GetInfo) e dos demais comandos. Padrao -10 (sem cartao).
    //                  0 = mesmo cartao, -1 = cartao trocado, -2 = sem formato, < -2 = ausente.
    //   PS2_MC_FIRST   se definida, resultado do primeiro GetInfo depois de cada init (um cartao real
    //                  responde -1 ali); os seguintes usam PS2_MC_RESULT.
    //   PS2_MC_INFO    "tipo,livre,formato" (ex.: 2,8000,1): escrito como 3 palavras no ponteiro que
    //                  vai em send+0x1C do GetInfo. O layout NAO foi confirmado: e um palpite para teste.
    void mcserv_service(Ctx& c, uint32_t cmd, uint32_t send, uint32_t ssize, uint32_t recv, uint32_t rsize)
    {
        static unsigned s_mc_log = 0;
        static unsigned s_mc_inits = 0;
        static bool s_cfg = false;
        static bool s_has_first = false;
        static int s_result = -10;
        static int s_first = -10;
        static bool s_has_info = false;
        static uint32_t s_info[3] = { 2, 8000, 1 };
        static bool s_first_pending = true;
        static std::set<uint32_t> s_seen_cmds;

        if (!s_cfg)
        {
            s_cfg = true;
            // Padrao (testado): cartao "novo" no primeiro GetInfo, depois "mesmo"; tipo 2, 8000 livres, formatado.
            s_result = mc_env_int("PS2_MC_RESULT", 0);
            s_first = mc_env_int("PS2_MC_FIRST", -1, &s_has_first);
            s_has_first = true;
            s_has_info = true;

            const char* info = std::getenv("PS2_MC_INFO");

            if (info != nullptr && std::strcmp(info, "off") == 0)
            {
                s_has_info = false;   // PS2_MC_INFO=off desliga a escrita das informacoes
            }
            else if (info != nullptr && *info != 0)
            {
                unsigned a = 0, b = 0, f = 0;

                if (std::sscanf(info, "%u,%u,%u", &a, &b, &f) == 3)
                {
                    s_info[0] = a; s_info[1] = b; s_info[2] = f;
                }
            }

            std::fprintf(stderr, "  [MC] resultado=%d primeiro=%d info=%s\n", s_result, s_first,
                         s_has_info ? "sim" : "nao");
        }

        if (s_mc_log < 20)
        {
            s_mc_log++;
            rpc_log(c, "MCSERV", cmd, send, ssize, recv, rsize);
        }

        // Todo comando novo aparece uma vez no log: mostra o que o jogo pede depois do GetInfo.
        if (cmd != 0xFEu && cmd != 1u && s_seen_cmds.insert(cmd).second)
            std::fprintf(stderr, "  [MC] comando novo 0x%X (enviar=%u bytes, receber=%u bytes)\n", cmd, ssize, rsize);

        rpc_zero_recv(c, recv, rsize);

        if (recv == 0)
            return;

        if (cmd == 0xFEu)
        {
            s_mc_inits++;
            s_first_pending = true;

            if (s_mc_inits == 1 || s_mc_inits % 100 == 0)
                std::fprintf(stderr, "  [MC] sceMcInit numero %u\n", s_mc_inits);

            if (rsize >= 12)
            {
                mwr<uint32_t>(c, recv + 0, 0u);       // resultado: ok
                mwr<uint32_t>(c, recv + 4, 0x20Au);   // versao do mcserv
                mwr<uint32_t>(c, recv + 8, 0x20Eu);   // versao do mcman
            }

            return;
        }

        int result = s_result;

        if (cmd == 1u)
        {
            if (s_first_pending && s_has_first)
                result = s_first;

            s_first_pending = false;

            if (s_has_info)
            {
                uint32_t out = mrd<uint32_t>(c, send + 0x1Cu);

                if (out != 0)
                    for (int i = 0; i < 3; i++)
                        mwr<uint32_t>(c, out + 4u * (uint32_t)i, s_info[i]);
            }

            static unsigned s_get_log = 0;

            if (s_get_log < 12)
            {
                s_get_log++;
                std::fprintf(stderr, "  [MC] GetInfo -> %d (saida=0x%08X)\n", result, mrd<uint32_t>(c, send + 0x1Cu));
            }
        }

        if (rsize >= 4)
            mwr<uint32_t>(c, recv + 0, (uint32_t)result);
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

            for (uint32_t i = 0; i < 4; i++)
                mwr<uint8_t>(c, out + 4 + i, pad.sticks[i]);

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
    const bool g_gsrun = g_giflog || g_gsdump;                    // decodifica o fluxo de DMA e roda o GS
    unsigned g_giflog_budget = 600;
    unsigned g_dma_detail = 0;
    unsigned g_vif_log = 200, g_gif_log = 80, g_raw_log = 64;
    std::mutex g_gs_mu;

    struct GsStats
    {
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
    uint16_t g_col4[16][32];
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

    inline uint32_t ix_ct32(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)
    {
        uint32_t page = (bp >> 5) + (y >> 5) * bw + (x >> 6);
        return ((page << 11) + ((uint32_t)g_blk32[(y >> 3) & 3][(x >> 3) & 7] << 6) + g_col32[y & 7][x & 7]) & 0xFFFFFu;
    }

    inline uint32_t ix_ct16(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)    // indice de meia-palavra
    {
        uint32_t page = (bp >> 5) + (y >> 6) * bw + (x >> 6);
        return ((page << 12) + ((uint32_t)g_blk16[(y >> 3) & 7][(x >> 4) & 3] << 7) + g_col16[y & 7][x & 15]) & 0x1FFFFFu;
    }

    inline uint32_t ix_t8(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)      // indice de byte
    {
        uint32_t pw = bw >> 1 ? bw >> 1 : 1;
        uint32_t page = (bp >> 5) + (y >> 6) * pw + (x >> 7);
        return ((page << 13) + ((uint32_t)g_blk8[(y >> 4) & 3][(x >> 4) & 7] << 8) + g_col8[y & 15][x & 15]) & 0x3FFFFFu;
    }

    inline uint32_t ix_t4(uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)      // indice de nibble
    {
        uint32_t pw = bw >> 1 ? bw >> 1 : 1;
        uint32_t page = (bp >> 5) + (y >> 7) * pw + (x >> 7);
        return ((page << 14) + ((uint32_t)g_blk4[(y >> 4) & 7][(x >> 5) & 3] << 9) + g_col4[y & 15][x & 31]) & 0x7FFFFFu;
    }

    inline uint8_t* vram8() { return reinterpret_cast<uint8_t*>(g_vram); }

    uint32_t gs_read_px(uint32_t psm, uint32_t bp, uint32_t bw, uint32_t x, uint32_t y)
    {
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
    };

    struct GsState
    {
        GsCtx ctx[2];
        uint64_t prim = 0, rgbaq = 0x3F80000000000000ull, st = 0, uv = 0, texa = 0, colclamp = 1;
        uint64_t bitbltbuf = 0, trxpos = 0, trxreg = 0, trxdir = 3;
        GsVertex q[3];
        int nq = 0;
        uint32_t clut[256] = {};
        uint64_t clut_tex0 = ~0ull;

        // transferencia host -> local em andamento
        bool xfer = false;
        uint32_t x = 0, y = 0, x0 = 0, rrw = 0, rrh = 0, dpsm = 0, dbp = 0, dbw = 0;
        uint8_t pend[32] = {};
        unsigned npend = 0;
        uint64_t unsupported = 0;
    };

    GsState g_st;

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

    void gs_load_clut(uint64_t tex0)
    {
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

            uint32_t entry = is4 ? i + csa * 16 : j;
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

            g_st.clut[i] = gs_clut_color(cpsm, raw);
        }
    }

    // Cor do texel (RGBA 8 bits; alpha 0x80 = opaco como no GS)
    uint32_t gs_texel(const GsCtx& c, int iu, int iv)
    {
        uint32_t tbp = (uint32_t)(c.tex0 & 0x3FFF), tbw = (uint32_t)((c.tex0 >> 14) & 0x3F);
        uint32_t psm = (uint32_t)((c.tex0 >> 20) & 0x3F);
        int tw = (int)((c.tex0 >> 26) & 0xF), th = (int)((c.tex0 >> 30) & 0xF);
        int w = 1 << tw, h = 1 << th;

        auto wrap = [](int v, int size, unsigned mode, int minv, int maxv)
        {
            switch (mode)
            {
                case 0: return v & (size - 1);
                case 1: return v < 0 ? 0 : (v >= size ? size - 1 : v);
                case 2: return v < minv ? minv : (v > maxv ? maxv : v);
                default: return (v & minv) | maxv;
            }
        };

        iu = wrap(iu, w, (unsigned)(c.clamp & 3), (int)((c.clamp >> 4) & 0x3FF), (int)((c.clamp >> 14) & 0x3FF));
        iv = wrap(iv, h, (unsigned)((c.clamp >> 2) & 3), (int)((c.clamp >> 34) & 0x3FF), (int)((c.clamp >> 44) & 0x3FF));

        uint32_t raw = gs_read_px(psm, tbp, tbw, (uint32_t)iu, (uint32_t)iv);

        switch (psm)
        {
            case 0x00: return raw;
            case 0x01:
            {
                uint32_t a = (uint32_t)(g_st.texa & 0xFF);

                if ((g_st.texa & 0x8000) && (raw & 0xFFFFFF) == 0)
                    a = 0;

                return (raw & 0xFFFFFFu) | (a << 24);
            }
            case 0x02: case 0x0A:
            {
                uint32_t r = (raw & 31) << 3, g = ((raw >> 5) & 31) << 3, b = ((raw >> 10) & 31) << 3;
                uint32_t a = (raw & 0x8000) ? (uint32_t)((g_st.texa >> 32) & 0xFF) : (uint32_t)(g_st.texa & 0xFF);

                if ((g_st.texa & 0x8000) && (raw & 0x7FFF) == 0)
                    a = 0;

                return r | (g << 8) | (b << 16) | (a << 24);
            }
            case 0x13: case 0x1B: return g_st.clut[raw & 0xFF];
            case 0x14: case 0x24: case 0x2C: return g_st.clut[raw & 0xF];
            default: return 0xFF00FF | 0x80000000u;   // formato nao suportado: magenta
        }
    }

    void gs_pixel(int ci, int x, int y, float fr, float fg, float fb, float fa, float u, float v)
    {
        const GsCtx& c = g_st.ctx[ci];

        // scissor (limites inclusivos)
        int sx0 = (int)(c.scissor & 0x7FF), sx1 = (int)((c.scissor >> 16) & 0x7FF);
        int sy0 = (int)((c.scissor >> 32) & 0x7FF), sy1 = (int)((c.scissor >> 48) & 0x7FF);

        if (x < sx0 || x > sx1 || y < sy0 || y > sy1 || x < 0 || y < 0)
            return;

        int vr = (int)gs_clampf(fr, 0, 255), vg = (int)gs_clampf(fg, 0, 255), vb = (int)gs_clampf(fb, 0, 255), va = (int)gs_clampf(fa, 0, 255);
        int r = vr, g = vg, b = vb, a = va;

        if (g_st.prim & 0x10)   // TME
        {
            uint32_t t = gs_texel(c, (int)std::floor(u), (int)std::floor(v));
            int tr = (int)(t & 0xFF), tg = (int)((t >> 8) & 0xFF), tb = (int)((t >> 16) & 0xFF), ta = (int)((t >> 24) & 0xFF);
            bool tcc = ((c.tex0 >> 34) & 1) != 0;
            unsigned tfx = (unsigned)((c.tex0 >> 35) & 3);

            switch (tfx)
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
                    a = (tfx == 2 && tcc) ? std::min(255, ta + va) : (tcc ? ta : va);
                    break;
            }
        }

        // alpha test
        if (c.test & 1)
        {
            int aref = (int)((c.test >> 4) & 0xFF);
            bool pass = true;

            switch ((c.test >> 1) & 7)
            {
                case 0: pass = false; break;
                case 2: pass = a < aref; break;
                case 3: pass = a <= aref; break;
                case 4: pass = a == aref; break;
                case 5: pass = a >= aref; break;
                case 6: pass = a > aref; break;
                case 7: pass = a != aref; break;
                default: break;
            }

            if (!pass)
                return;   // AFAIL: so o caso KEEP/descartar e tratado
        }

        uint32_t fbp = (uint32_t)(c.frame & 0x1FF) * 32, fbw = (uint32_t)((c.frame >> 16) & 0x3F), fpsm = (uint32_t)((c.frame >> 24) & 0x3F);
        uint32_t fbmsk = (uint32_t)(c.frame >> 32);
        uint32_t dst = gs_read_px(fpsm, fbp, fbw, (uint32_t)x, (uint32_t)y);
        int dr, dg, db, da;

        if (fpsm == 0x02 || fpsm == 0x0A)
        {
            dr = (int)((dst & 31) << 3); dg = (int)(((dst >> 5) & 31) << 3); db = (int)(((dst >> 10) & 31) << 3);
            da = (dst & 0x8000) ? 0x80 : 0;
        }
        else
        {
            dr = (int)(dst & 0xFF); dg = (int)((dst >> 8) & 0xFF); db = (int)((dst >> 16) & 0xFF); da = (int)((dst >> 24) & 0xFF);
        }

        if (g_st.prim & 0x40)   // ABE: (A - B) * C >> 7 + D
        {
            auto pick = [&](unsigned sel, int cs, int cd) { return sel == 0 ? cs : (sel == 1 ? cd : 0); };
            unsigned A = (unsigned)(c.alpha & 3), B = (unsigned)((c.alpha >> 2) & 3), C = (unsigned)((c.alpha >> 4) & 3), D = (unsigned)((c.alpha >> 6) & 3);
            int fix = (int)((c.alpha >> 32) & 0xFF);
            int cf = C == 0 ? a : (C == 1 ? da : fix);
            int nr = (((pick(A, r, dr) - pick(B, r, dr)) * cf) >> 7) + pick(D, r, dr);
            int ng = (((pick(A, g, dg) - pick(B, g, dg)) * cf) >> 7) + pick(D, g, dg);
            int nb = (((pick(A, b, db) - pick(B, b, db)) * cf) >> 7) + pick(D, b, db);

            if (g_st.colclamp & 1)
            {
                r = nr < 0 ? 0 : (nr > 255 ? 255 : nr); g = ng < 0 ? 0 : (ng > 255 ? 255 : ng); b = nb < 0 ? 0 : (nb > 255 ? 255 : nb);
            }
            else
            {
                r = nr & 0xFF; g = ng & 0xFF; b = nb & 0xFF;
            }
        }

        uint32_t out;

        if (fpsm == 0x02 || fpsm == 0x0A)
            out = (uint32_t)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10) | ((a >= 0x80) ? 0x8000 : 0));
        else
            out = (uint32_t)(r | (g << 8) | (b << 16) | (a << 24));

        if (fbmsk != 0)
            out = (dst & fbmsk) | (out & ~fbmsk);

        gs_write_px(fpsm, fbp, fbw, (uint32_t)x, (uint32_t)y, out);
    }

    void gs_draw_sprite(int ci, const GsVertex& a, const GsVertex& b)
    {
        float x0 = std::min(a.x, b.x), x1 = std::max(a.x, b.x), y0 = std::min(a.y, b.y), y1 = std::max(a.y, b.y);

        if (x1 <= x0 || y1 <= y0)
            return;

        int px0 = (int)std::ceil(x0 - 0.5f), px1 = (int)std::ceil(x1 - 0.5f);
        int py0 = (int)std::ceil(y0 - 0.5f), py1 = (int)std::ceil(y1 - 0.5f);

        for (int y = py0; y < py1; y++)
            for (int x = px0; x < px1; x++)
            {
                float fu = ((float)x + 0.5f - a.x) / (b.x - a.x);
                float fv = ((float)y + 0.5f - a.y) / (b.y - a.y);
                gs_pixel(ci, x, y, b.r, b.g, b.b, b.a, a.u + (b.u - a.u) * fu, a.v + (b.v - a.v) * fv);
            }
    }

    void gs_draw_triangle(int ci, const GsVertex& a, const GsVertex& b, const GsVertex& c)
    {
        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        if (area == 0.0f)
            return;

        int minx = (int)std::floor(std::min(a.x, std::min(b.x, c.x))), maxx = (int)std::ceil(std::max(a.x, std::max(b.x, c.x)));
        int miny = (int)std::floor(std::min(a.y, std::min(b.y, c.y))), maxy = (int)std::ceil(std::max(a.y, std::max(b.y, c.y)));

        minx = std::max(minx, 0); miny = std::max(miny, 0);
        maxx = std::min(maxx, 2047); maxy = std::min(maxy, 2047);

        for (int y = miny; y <= maxy; y++)
            for (int x = minx; x <= maxx; x++)
            {
                float px = (float)x + 0.5f, py = (float)y + 0.5f;
                float w0 = ((b.x - px) * (c.y - py) - (b.y - py) * (c.x - px)) / area;
                float w1 = ((c.x - px) * (a.y - py) - (c.y - py) * (a.x - px)) / area;
                float w2 = 1.0f - w0 - w1;

                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;

                gs_pixel(ci, x, y, a.r * w0 + b.r * w1 + c.r * w2, a.g * w0 + b.g * w1 + c.g * w2, a.b * w0 + b.b * w1 + c.b * w2,
                    a.a * w0 + b.a * w1 + c.a * w2, a.u * w0 + b.u * w1 + c.u * w2, a.v * w0 + b.v * w1 + c.v * w2);
            }
    }

    void gs_vertex(uint64_t data, bool kick)
    {
        int ci = (int)((g_st.prim >> 9) & 1);
        const GsCtx& c = g_st.ctx[ci];
        GsVertex v;
        float ofx = (float)(c.xyoffset & 0xFFFF), ofy = (float)((c.xyoffset >> 32) & 0xFFFF);
        v.x = ((float)(data & 0xFFFF) - ofx) / 16.0f;
        v.y = ((float)((data >> 16) & 0xFFFF) - ofy) / 16.0f;
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

        if (!kick || g_st.nq < need)
            return;

        if (type == 6)
            gs_draw_sprite(ci, g_st.q[0], g_st.q[1]);
        else if (type >= 3)
            gs_draw_triangle(ci, g_st.q[0], g_st.q[1], g_st.q[2]);
        else if (type == 0)
            gs_pixel(ci, (int)v.x, (int)v.y, v.r, v.g, v.b, v.a, v.u, v.v);
        else
            g_st.unsupported++;   // linhas: ainda nao

        if (type == 6 || type == 3 || type == 1 || type == 0)
            g_st.nq = 0;
    }

    void gs_xfer_begin()
    {
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

    void gs_core_image(uint64_t lo, uint64_t hi)
    {
        if (!g_st.xfer)
            return;

        uint8_t bytes[16];
        std::memcpy(bytes, &lo, 8);
        std::memcpy(bytes + 8, &hi, 8);

        for (int i = 0; i < 16 && g_st.xfer; i++)
        {
            g_st.pend[g_st.npend++] = bytes[i];
            uint32_t psm = g_st.dpsm;

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
            case 0x04: case 0x05: gs_vertex(data, true); break;
            case 0x0C: case 0x0D: gs_vertex(data, false); break;
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
            case 0x53: g_st.trxdir = data; gs_xfer_begin(); break;
            default: break;
        }
    }

    // Grava o quadro exibido em PPM (PS2_GSDUMP=<segundos entre dumps>).
    void gs_dump_frame(unsigned index)
    {
        // PMODE.EN1/EN2 escolhem o circuito; DISPFBn / DISPLAYn dizem onde e quanto
        uint64_t pmode = g_gs_priv[0x00 / 8];
        uint64_t dispfb = 0, display = 0;
        uint32_t fbp = 0, fbw = 0, psm = 0, dbx = 0, dby = 0, w = 0, h = 0;
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
            h = (uint32_t)((display >> 44) & 0x7FF) + 1;
            valid = w != 0 && w <= 1024 && h != 0 && h <= 1024 && fbw != 0;
        }

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
                uint32_t v = gs_read_px(psm, fbp, fbw, dbx + x, dby + y);
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
        std::fprintf(stderr, "  [gs] dump %u: %s (%ux%u, PSM=0x%02X, FBP=%u, FBW=%u, nao suportado ate agora: %llu)\n", index, name, w, h, psm, fbp / 32,
            fbw, (unsigned long long)g_st.unsupported);
    }

    void gs_write(unsigned reg, uint64_t data)
    {
        reg &= 0xFF;
        g_gs.reg[reg]++;
        gs_core_write(reg, data);

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
                gs_core_image(lo, hi);
                return;
            }

            if (!active)
            {
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

    struct Vif1Decoder
    {
        uint32_t cl = 1, wl = 1;
        uint64_t skip_words = 0, direct_qw = 0;
        uint32_t dbuf[4] = {};
        unsigned dn = 0;

        static unsigned vector_bits(unsigned fmt)
        {
            static const unsigned bits[16] = { 32, 16, 8, 0, 64, 32, 16, 0, 96, 48, 24, 0, 128, 64, 32, 16 };
            return bits[fmt & 15];
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

            if (skip_words != 0)
            {
                skip_words--;
                return;
            }

            unsigned cmd = (w >> 24) & 0x7F;
            unsigned num = (w >> 16) & 0xFF;
            unsigned imm = w & 0xFFFF;

            if (g_giflog && g_vif_log != 0)
            {
                g_vif_log--;
                std::fprintf(stderr, "  [vif] codigo=0x%08X cmd=0x%02X num=%u imm=0x%04X (cl=%u wl=%u)\n", w, cmd, num, imm, cl, wl);
            }

            if (cmd >= 0x60)
            {
                g_gs.unpack_cmds++;
                uint64_t n = num == 0 ? 256 : num;
                uint64_t in_vectors = n;

                if (cl < wl && wl != 0)
                    in_vectors = (n / wl) * cl + std::min<uint64_t>(n % wl, cl);

                skip_words = (in_vectors * vector_bits(cmd & 15) + 31) / 32;
                return;
            }

            switch (cmd)
            {
                case 0x01: cl = imm & 0xFF; wl = (imm >> 8) & 0xFF; break;
                case 0x14: case 0x15: case 0x17: g_gs.mscal++; break;
                case 0x20: skip_words = 1; break;
                case 0x30: case 0x31: skip_words = 4; break;
                case 0x4A:
                    skip_words = (uint64_t)(num == 0 ? 256 : num) * 2;
                    g_gs.mpg_words += skip_words;
                    break;
                case 0x50: case 0x51:
                    direct_qw = imm == 0 ? 65536u : imm;
                    dn = 0;
                    break;
                default: break;   // NOP, OFFSET, BASE, ITOP, STMOD, MSKPATH3, MARK, FLUSH*: sem dados
            }
        }
    };

    Vif1Decoder g_vif1;

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
            else
                for (int k = 0; k < 4; k++)
                    g_vif1.word(w[k]);
        }
    }

    void dma_walk(Ctx& c, int ch, uint32_t chcr)
    {
        DmaChan& d = g_dmach[ch];
        g_gs.kicks[ch]++;
        unsigned mod = (chcr >> 2) & 3;
        bool detail = g_giflog && g_dma_detail < 12;

        if (detail)
        {
            g_dma_detail++;
            std::fprintf(stderr, "  [dma] canal %d kick: CHCR=0x%X MADR=0x%08X QWC=%u TADR=0x%08X\n", ch, chcr, d.madr, d.qwc, d.tadr);
        }

        if (mod == 0)
        {
            dma_feed(c, ch, d.madr, d.qwc);
            return;
        }

        if (mod != 1)
            return;

        uint32_t tadr = d.tadr;
        uint32_t asr[2] = {};
        int depth = 0;

        for (int guard = 0; guard < 200000; guard++)
        {
            const uint8_t* t = host_ptr(c, dma_addr(tadr), 16);
            uint64_t lo, hi;
            std::memcpy(&lo, t, 8);
            std::memcpy(&hi, t + 8, 8);

            uint32_t qwc = (uint32_t)(lo & 0xFFFF);
            unsigned id = (unsigned)((lo >> 28) & 7);
            uint32_t addr = (uint32_t)(lo >> 32);

            if (detail && guard < 8)
                std::fprintf(stderr, "    tag @0x%08X: id=%u qwc=%u addr=0x%08X  (VIFcodes da tag: 0x%08X 0x%08X)\n",
                    tadr, id, qwc, addr, (uint32_t)hi, (uint32_t)(hi >> 32));

            // No VIF1 a metade alta da DMAtag (bits 64..127) leva dois VIFcodes, tipicamente
            // [NOP, DIRECT(qwc)] (convencao da libgraph): eles chegam ao VIF antes dos dados da tag.
            // E isso que faz os pacotes GIF "puros" dos dados serem aceitos pelo PATH2.
            if (ch == 1)
            {
                g_vif1.word((uint32_t)hi);
                g_vif1.word((uint32_t)(hi >> 32));
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
        g_gs_priv[(phys - 0x12000000u) >> 3] = value;
        return;
    }

    if (!g_gsrun || phys < 0x10008000u || phys >= 0x1000B000u || (phys & 0xFFFu) >= 0x60u)
        return;

    int ch = (int)((phys - 0x10008000u) >> 12);
    uint32_t reg = phys & 0xFFFu;

    if (ch == 0)
        return;

    DmaChan& d = g_dmach[ch];

    switch (reg)
    {
        case 0x10: d.madr = (uint32_t)value; break;
        case 0x20: d.qwc = (uint32_t)value; break;
        case 0x30: d.tadr = (uint32_t)value; break;
        case 0x00:
            if (value & 0x100)
            {
                std::lock_guard<std::mutex> lk(g_gs_mu);
                dma_walk(c, ch, (uint32_t)value);
            }

            break;
        default: break;
    }
}

// Resumo a cada 120 vsyncs (2 s): o que o jogo mandou desenhar nesse intervalo.
void ps2_gs_vsync()
{
    if (!g_gsrun)
        return;

    ++g_gs_vsyncs;

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

    if (!g_giflog || g_gs_vsyncs % 120u != 0)
        return;

    std::lock_guard<std::mutex> lk(g_gs_mu);
    static const char* ptype[8] = { "ponto", "linha", "tira-linha", "triangulo", "tira-tri", "leque-tri", "sprite", "?" };

    std::fprintf(stderr, "  [gs] 2s: kicks vif1=%llu gif=%llu | GIFtags packed=%llu reglist=%llu image=%llu (imagem=%llu qw) | direct=%llu qw mpg=%llu w unpack=%llu mscal=%llu\n",
        (unsigned long long)g_gs.kicks[1], (unsigned long long)g_gs.kicks[2], (unsigned long long)g_gs.tags_packed,
        (unsigned long long)g_gs.tags_reglist, (unsigned long long)g_gs.tags_image, (unsigned long long)g_gs.image_qw,
        (unsigned long long)g_gs.direct_qw, (unsigned long long)g_gs.mpg_words, (unsigned long long)g_gs.unpack_cmds,
        (unsigned long long)g_gs.mscal);

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

// Stub: ainda nao ha micro memoria nem interpretador do VU0. Registra a chamada e "termina"
// o microprograma na hora (VBS0 de VPU_STAT = vi[29] limpo), para o jogo seguir adiante.
void vu0_callms(Ctx& c, uint32_t address, uint32_t raw)
{
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
        std::fprintf(stderr, "  [VU0 stub] %s micro=0x%04X CMSAR0=0x%X (chamada %u; ra=0x%08X)\n",
                     raw ? "VCALLMS" : "VCALLMSR", address, c.vi[27], count, (uint32_t)c.r[31].lo);
    }

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

    std::fprintf(stderr, "interrupcoes: EIE=%d dentro_de_handler=%d mascara_intc=0x%X pendente=%d flags=0x%X\n",
                 g_irq_enabled ? 1 : 0, g_in_irq ? 1 : 0, g_intc_mask, (int)g_irq_pending, g_irq_flags.load());
    std::fprintf(stderr, "  timer0: modo=0x%llX comparacao=0x%llX\n", (unsigned long long)g_timer0_mode,
                 (unsigned long long)g_timer0_comp);

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
static void irq_deliver_cause(Ctx& c, uint32_t cause)
{
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

    std::vector<IntcHandler> list = g_intc_handlers;

    for (size_t i = 0; i < list.size(); i++)
    {
        if (list[i].cause != cause)
            continue;

        Ctx irq = c;
        irq.r[29].lo = 0x01FF0000u - 0x10u;   // pilha propria da interrupcao (topo da RAM)
        irq.r[4].lo = cause;
        irq.r[5].lo = list[i].arg;
        irq.r[6].lo = 0;
        ps2_call(irq, list[i].fn);
    }
}

// Chamada pelo codigo gerado quando o tique liga g_irq_pending.
void ps2_irq_poll(Ctx& c)
{
    if (g_in_irq || !g_irq_enabled)
        return;

    uint32_t flags = g_irq_flags.exchange(0);
    g_irq_pending = 0;

    if (flags == 0)
        return;

    g_in_irq = true;

    if (flags & 1u)
        irq_deliver_cause(c, 2);

    if (flags & 2u)
        irq_deliver_cause(c, 9);

    g_in_irq = false;

    // um handler pode ter acordado uma thread de prioridade maior (ex.: iWakeupThread): cede a vez
    if (g_kernel_ready)
    {
        std::unique_lock<std::mutex> lk(g_mu);
        maybe_preempt(lk, g_running);
    }
}

// Chamada pelo escalonador quando nenhuma thread pode rodar (todas esperando).
void ps2_irq_idle(Ctx& c)
{
    if (g_in_irq)
        return;

    uint32_t flags = g_irq_flags.exchange(0);
    g_irq_pending = 0;

    if (flags == 0)
        return;

    g_in_irq = true;

    if (flags & 1u)
        irq_deliver_cause(c, 2);

    if (flags & 2u)
        irq_deliver_cause(c, 9);

    g_in_irq = false;
}

uint32_t ps2_mfc0(Ctx&, int reg)
{
    if (reg == 12)   // Status
        return g_irq_enabled ? 0x00010C01u : 0x00000C00u;

    return 0;
}

void ps2_mtc0(Ctx&, int, uint32_t) {}
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
        private readonly Dictionary<uint, JumpTableResolver.Table> _tables = new();
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
            if (_jumpTables != null)
            {
                for (int i = 0; i < count; i++)
                {
                    if (code[i].Mnemonic != "JR" || code[i].Rs == 31)
                        continue;

                    if (_jumpTables.TryResolve(code, i, start, end, out JumpTableResolver.Table? table, out _) &&
                        table != null)
                    {
                        _tables[start + (uint)(i * 4)] = table;
                    }
                }
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
                    Simple(instruction, address, I1);
                }
            }

            _sb.AppendLine("}");

            return new Result(_sb.ToString(), count, _unsupported, _byMnemonic, _dispatched);
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
                else if (_tables.TryGetValue(address, out JumpTableResolver.Table? table))
                {
                    // switch: o destino e lido antes do delay slot.
                    _sb.AppendLine($"{I2}uint32_t dest = (uint32_t){Lo(instruction.Rs)};");
                    Delay(delay, address, I2);
                    Comment(I2, $"jump table em 0x{table.TableAddress:X8}, {table.Targets.Count} destinos");
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
