#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <cmath>

namespace roj
{
    constexpr unsigned InputActionCount = 24;
    constexpr unsigned GamepadSourceCount = 25;
    inline constexpr const char* InputActionNames[InputActionCount] = {
        "select", "l3", "r3", "start", "up", "right", "down", "left",
        "l2", "r2", "l1", "r1", "triangle", "circle", "cross", "square",
        "left_up", "left_right", "left_down", "left_left",
        "right_up", "right_right", "right_down", "right_left"
    };
    inline constexpr uint16_t InputActionBits[16] = {
        0x0100, 0x0200, 0x0400, 0x0800, 0x1000, 0x2000, 0x4000, 0x8000,
        0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080
    };
    struct InputBindings
    {
        std::array<int, InputActionCount> keyboard = {
            0x08, 0, 0, 0x0d, 0x26, 0x27, 0x28, 0x25,
            '1', '3', 'Q', 'E', 'V', 'X', 'Z', 'C',
            'W', 'D', 'S', 'A', 'I', 'L', 'K', 'J'
        };
        // 1..14: XInput buttons; 15/16: triggers; 17..24: stick directions.
        std::array<int, InputActionCount> gamepad = {
            6, 7, 8, 5, 1, 2, 3, 4, 15, 16, 9, 10, 14, 12, 11, 13,
            17, 18, 19, 20, 21, 22, 23, 24
        };
    };
    inline void normalize(InputBindings& bindings)
    {
        for (auto& key : bindings.keyboard) key = std::clamp(key, 0, 255);
        for (auto& source : bindings.gamepad) source = std::clamp(source, 0, int(GamepadSourceCount) - 1);
    }
    inline bool input_uses_key(const InputBindings& bindings, int key)
    {
        return key != 0 && std::find(bindings.keyboard.begin(), bindings.keyboard.end(), key) != bindings.keyboard.end();
    }
    // XInput has a circular gate, while this game's PS2 running threshold
    // expects the square stick range. Use radial dead zones and expand the
    // circle onto that square, preserving partial tilt for walking.
    inline int gamepad_source_value(int source, uint16_t buttons, uint8_t lt, uint8_t rt, const int16_t* sticks)
    {
        constexpr uint16_t masks[] = {
            0x0001, 0x0008, 0x0002, 0x0004, 0x0010, 0x0020, 0x0040,
            0x0080, 0x0100, 0x0200, 0x1000, 0x2000, 0x4000, 0x8000
        };
        if (source >= 1 && source <= 14) return buttons & masks[source - 1] ? 255 : 0;
        if (source == 15 || source == 16)
        {
            const int pressure = source == 15 ? lt : rt;
            return pressure <= 30 ? 0 : (pressure - 30) * 255 / 225;
        }
        if (source < 17 || source > 24) return 0;
        const int direction = (source - 17) % 4, base = source < 21 ? 0 : 2;
        const int axis = base + (direction == 0 || direction == 2 ? 1 : 0);
        const int value = sticks[axis];
        const bool positive = direction == 0 || direction == 1;
        if ((positive && value <= 0) || (!positive && value >= 0)) return 0;
        const int magnitude = value < 0 ? -value : value;
        const int dead = base == 0 ? 7849 : 8689;
        const double radius = std::hypot(double(sticks[base]), double(sticks[base+1]));
        if (radius <= dead) return 0;
        const int peak = std::max(std::abs(int(sticks[base])), std::abs(int(sticks[base+1])));
        const double tilt = std::clamp((radius-dead)/(32767.0-dead),0.0,1.0);
        return std::clamp(int(std::lround(255.0*tilt*magnitude/peak)),0,255);
    }
    struct MappedInput
    {
        uint16_t pressed = 0; // PS2 packet bits, active high until transmission.
        uint8_t lx = 128, ly = 128, rx = 128, ry = 128;
    };
    inline uint8_t mapped_axis(int negative, int positive)
    {
        const int delta = positive - negative;
        return uint8_t(delta < 0 ? 128 - (-delta * 128 / 255) : 128 + delta * 127 / 255);
    }
    template <class KeyDown>
    MappedInput map_input(const InputBindings& bindings, KeyDown key_down, bool connected,
                         uint16_t buttons, uint8_t lt, uint8_t rt, const int16_t* sticks)
    {
        int values[InputActionCount]{};
        MappedInput result;
        for (unsigned i = 0; i < InputActionCount; ++i)
        {
            const int key = bindings.keyboard[i];
            values[i] = key && key_down(key) ? 255 : 0;
            if (connected) values[i] = std::max(values[i], gamepad_source_value(bindings.gamepad[i], buttons, lt, rt, sticks));
            if (i < 16 && values[i]) result.pressed |= InputActionBits[i];
        }
        result.lx = mapped_axis(values[19], values[17]); result.ly = mapped_axis(values[16], values[18]);
        result.rx = mapped_axis(values[23], values[21]); result.ry = mapped_axis(values[20], values[22]);
        return result;
    }
}
