// Executed in GS command order. Numbers belong to the same framebuffer as
// the original gauges and are cleared by its next opaque scene clear.
static void gs_ring_hud(uint64_t values, uint64_t position, unsigned alpha)
{
    const unsigned ci = unsigned((g_st.prim >> 9) & 1u);
    const uint32_t fbp = unsigned(g_st.ctx[ci].frame & 511u) * 32u;
    if (fbp != 0u && fbp != 2240u) return;
    std::lock_guard<std::mutex> lock(g_ui_native_mu);
    auto& cache = g_ui_native_glyphs;
    cache.erase(std::remove_if(cache.begin(),cache.end(),[&](const UiGlyphP& glyph) {
        return glyph.fbp == fbp && glyph.ring_hud;
    }),cache.end());
    const int x = int16_t(position), y = int16_t(position >> 16);
    if (!alpha || x < 100 || x > 620 || y < 0 || y > 180) return;
#ifdef _WIN32
    // Supersampled grayscale masks; no ClearType colour fringes. Build the
    // tiny alphabet once, then reuse it for every value and every framebuffer.
    struct Alphabet {
        enum { width = 10, height = 22 };
        std::array<std::array<uint8_t,width*height>,16> masks{};
        Alphabet() {
            const wchar_t characters[] = L"0123456789-HPMS ";
            HDC dc = CreateCompatibleDC(nullptr);
            if (!dc) return;
            BITMAPINFO info{};
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width*2;
            info.bmiHeader.biHeight = -height*2;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;
            void* data = nullptr;
            HBITMAP bitmap = CreateDIBSection(dc,&info,DIB_RGB_COLORS,&data,nullptr,0);
            HFONT font = CreateFontW(-36,0,0,0,FW_BOLD,FALSE,FALSE,FALSE,DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,FIXED_PITCH,L"Consolas");
            if (bitmap && font && data) {
                auto old_bitmap = SelectObject(dc,bitmap);
                auto old_font = SelectObject(dc,font);
                SetBkColor(dc,RGB(0,0,0)); SetTextColor(dc,RGB(255,255,255));
                for (int ch=0; ch<15; ++ch) {
                    std::memset(data,0,width*height*16);
                    TextOutW(dc,0,0,&characters[ch],1);
                    GdiFlush();
                    const auto* pixels = static_cast<const uint32_t*>(data);
                    for (int py=0; py<height; ++py) for (int px=0; px<width; ++px) {
                        unsigned sum=0;
                        for (int dy=0;dy<2;++dy) for(int dx=0;dx<2;++dx)
                            sum += pixels[(py*2+dy)*width*2+px*2+dx] & 255u;
                        masks[ch][py*width+px]=uint8_t((sum+2)/4);
                    }
                }
                SelectObject(dc,old_font); SelectObject(dc,old_bitmap);
            }
            if (font) DeleteObject(font);
            if (bitmap) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    };
    static const Alphabet alphabet;
    static const char* labels[] = {"HP", "MP", "SP"};
    static constexpr uint32_t color = 0x00FFFF00u; // Cyan, packed BBGGRR.
    const std::string characters="0123456789-HPMS ";
    for(int row=0;row<3;++row) {
        const int value=int16_t(values >> (row*16));
        const std::string text=std::string(labels[row])+" "+std::to_string(value);
        UiGlyphP glyph;
        glyph.fbp=fbp; glyph.vsync=g_gs_vsyncs; glyph.ring_hud=true;
        glyph.w=int(text.size())*Alphabet::width+2; glyph.h=Alphabet::height+2;
        // Center above each gauge; successive gauges step 28 pixels left.
        glyph.x=x+84-row*28-glyph.w/2; glyph.y=(y+9+row*20)*2-23;
        glyph.rgba.resize(size_t(glyph.w)*glyph.h);
        for (size_t ch=0;ch<text.size();++ch) {
            const size_t index=characters.find(text[ch]);
            if(index>=15) continue;
            for(int py=0;py<Alphabet::height;++py) for(int px=0;px<Alphabet::width;++px) {
                const unsigned coverage=alphabet.masks[index][py*Alphabet::width+px];
                if(!coverage) continue;
                const int gx=int(ch)*Alphabet::width+px+1,gy=py+1;
                for(int dy=-1;dy<=1;++dy) for(int dx=-1;dx<=1;++dx) {
                    uint32_t& pixel=glyph.rgba[size_t(gy+dy)*glyph.w+gx+dx];
                    pixel=std::max(pixel,((coverage*alpha+127)/255)<<24);
                }
            }
        }
        // Foreground is applied after all outline pixels.
        for(size_t ch=0;ch<text.size();++ch) {
            const size_t index=characters.find(text[ch]);
            if(index>=15) continue;
            for(int py=0;py<Alphabet::height;++py) for(int px=0;px<Alphabet::width;++px) {
                const unsigned a=(alphabet.masks[index][py*Alphabet::width+px]*alpha+127)/255;
                if(a) glyph.rgba[size_t(py+1)*glyph.w+int(ch)*Alphabet::width+px+1]=color|(a<<24);
            }
        }
        cache.push_back(std::move(glyph));
    }
#endif
}
