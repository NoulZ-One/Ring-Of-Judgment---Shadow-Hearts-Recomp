#pragma once
#include <algorithm>
#include <charconv>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <ostream>
#include <string>
#include "roj_input.h"

namespace roj
{
    inline constexpr char DefaultDiscordApplicationId[] = "1573256524724445324";
    inline bool valid_discord_id(const std::string& id)
    {
        if (id.size() < 17 || id.size() > 20) return false;
        uint64_t number = 0;
        auto parsed = std::from_chars(id.data(), id.data() + id.size(), number);
        return parsed.ec == std::errc{} && parsed.ptr == id.data() + id.size() && number != 0;
    }
    struct Settings
    {
        int width = 1280;
        int height = 720;
        int window_mode = 0;
        int aspect = 0;
        int fps = 60;
        int renderer = 1; // 0: CPU; 1: Vulkan, com fallback automatico
        int internal_resolution = 0; // Vulkan: 0=640x224, 1=1280x448, 2=1280x720
        int master_volume = 100;
        int music_volume = 100;
        int effects_volume = 100;
        bool text_aa = true;
        int text_style = 1; // 0: borda preta; 1: faixa escura continua
        int launcher_language = 0; // 0: portugues; 1: English
        bool discord_presence = true;
        std::string discord_application_id = DefaultDiscordApplicationId;
        InputBindings input;
    };

    inline void normalize(Settings& value)
    {
        value.width = std::clamp(value.width, 640, 3840);
        value.height = std::clamp(value.height, 360, 2160);
        value.window_mode = std::clamp(value.window_mode, 0, 3);
        value.aspect = std::clamp(value.aspect, 0, 2);
        value.fps = value.fps == 30 ? 30 : 60;
        value.renderer = std::clamp(value.renderer, 0, 1);
        value.internal_resolution = std::clamp(value.internal_resolution, 0, 2);
        value.master_volume = std::clamp(value.master_volume, 0, 100);
        value.music_volume = std::clamp(value.music_volume, 0, 100);
        value.effects_volume = std::clamp(value.effects_volume, 0, 100);
        value.text_style = std::clamp(value.text_style, 0, 1);
        value.launcher_language = std::clamp(value.launcher_language, 0, 1);
        if (!valid_discord_id(value.discord_application_id)) value.discord_application_id.clear();
        normalize(value.input);
    }

    inline Settings read_settings(const std::filesystem::path& file)
    {
        Settings result;
        std::ifstream input(file);
        std::string line;
        while (std::getline(input, line))
        {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            const auto split = line.find('=');
            if (split == std::string::npos) continue;
            const auto key = line.substr(0, split);
            if (key == "discord_application_id")
            {
                result.discord_application_id = line.substr(split + 1);
                if (result.discord_application_id.empty()) result.discord_application_id = DefaultDiscordApplicationId;
                continue;
            }
            int value = 0;
            const char* first = line.data() + split + 1;
            const char* last = line.data() + line.size();
            const auto parsed = std::from_chars(first, last, value);
            if (parsed.ec != std::errc{} || parsed.ptr != last) continue;
            if (key == "width") result.width = value;
            else if (key == "height") result.height = value;
            else if (key == "window_mode") result.window_mode = value;
            else if (key == "aspect") result.aspect = value;
            else if (key == "fps") result.fps = value;
            else if (key == "renderer") result.renderer = value;
            else if (key == "internal_resolution") result.internal_resolution = value;
            else if (key == "master_volume") result.master_volume = value;
            else if (key == "music_volume") result.music_volume = value;
            else if (key == "effects_volume") result.effects_volume = value;
            else if (key == "text_aa") result.text_aa = value != 0;
            else if (key == "text_style") result.text_style = value;
            else if (key == "launcher_language") result.launcher_language = value;
            else if (key == "discord_presence") result.discord_presence = value != 0;
            else for (unsigned i = 0; i < InputActionCount; ++i)
            {
                if (key == std::string("key_") + InputActionNames[i]) result.input.keyboard[i] = value;
                else if (key == std::string("pad_") + InputActionNames[i]) result.input.gamepad[i] = value;
            }
        }
        normalize(result);
        return result;
    }

    inline void write_settings(std::ostream& output, Settings value)
    {
        normalize(value);
        output << "width=" << value.width << "\nheight=" << value.height
               << "\nwindow_mode=" << value.window_mode << "\naspect=" << value.aspect
               << "\nfps=" << value.fps << "\nrenderer=" << value.renderer
               << "\ninternal_resolution=" << value.internal_resolution << "\nmaster_volume=" << value.master_volume
               << "\nmusic_volume=" << value.music_volume << "\neffects_volume=" << value.effects_volume
               << "\ntext_aa=" << int(value.text_aa) << "\ntext_style=" << value.text_style
               << "\nlauncher_language=" << value.launcher_language
               << "\ndiscord_presence=" << int(value.discord_presence)
               << "\ndiscord_application_id=" << value.discord_application_id << '\n';
        for (unsigned i = 0; i < InputActionCount; ++i)
            output << "key_" << InputActionNames[i] << '=' << value.input.keyboard[i] << '\n'
                   << "pad_" << InputActionNames[i] << '=' << value.input.gamepad[i] << '\n';
    }

    inline double aspect_ratio(int selection)
    {
        constexpr double ratios[] = {4.0 / 3.0, 16.0 / 9.0, 21.0 / 9.0};
        return ratios[std::clamp(selection, 0, 2)];
    }

    struct Viewport { int x, y, width, height; };

    inline Viewport fit_viewport(int width, int height, double ratio)
    {
        Viewport result{0, 0, std::max(0, width), std::max(0, height)};
        if (width <= 0 || height <= 0 || ratio <= 0) return result;
        if (double(width) / height > ratio)
        {
            result.width = std::max(1, int(std::lround(height * ratio)));
            result.x = (width - result.width) / 2;
        }
        else
        {
            result.height = std::max(1, int(std::lround(width / ratio)));
            result.y = (height - result.height) / 2;
        }
        return result;
    }

    inline double volume_gain(int volume)
    {
        return std::clamp(volume, 0, 100) / 100.0;
    }

    inline std::chrono::nanoseconds presentation_period(int fps)
    {
        return std::chrono::nanoseconds(1000000000ll / (fps == 30 ? 30 : 60));
    }
}
