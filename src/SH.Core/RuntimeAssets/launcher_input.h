// Included inside the launcher's namespace, after save().
namespace input_editor
{
    constexpr int KeyId=210, SourceId=211, DetectId=212, ClearId=213, ResetId=214, ApplyId=215;
    const wchar_t* action_name(unsigned action)
    {
        static const wchar_t* pt[]={L"Select",L"L3",L"R3",L"Start",L"Direcional: cima",L"Direcional: direita",L"Direcional: baixo",L"Direcional: esquerda",L"L2",L"R2",L"L1",L"R1",L"Triângulo",L"Círculo",L"Cruz (X)",L"Quadrado",L"Analógico esquerdo: cima",L"Analógico esquerdo: direita",L"Analógico esquerdo: baixo",L"Analógico esquerdo: esquerda",L"Analógico direito: cima",L"Analógico direito: direita",L"Analógico direito: baixo",L"Analógico direito: esquerda"};
        static const wchar_t* en[]={L"Select",L"L3",L"R3",L"Start",L"D-pad: up",L"D-pad: right",L"D-pad: down",L"D-pad: left",L"L2",L"R2",L"L1",L"R1",L"Triangle",L"Circle",L"Cross (X)",L"Square",L"Left analog: up",L"Left analog: right",L"Left analog: down",L"Left analog: left",L"Right analog: up",L"Right analog: right",L"Right analog: down",L"Right analog: left"};
        return tr(pt[action],en[action]);
    }
    const wchar_t* source_name(int source)
    {
        static const wchar_t* pt[]={L"Nenhum",L"Direcional: cima",L"Direcional: direita",L"Direcional: baixo",L"Direcional: esquerda",L"Start / Menu",L"Back / View",L"Clique analógico esquerdo",L"Clique analógico direito",L"LB / L1",L"RB / R1",L"A",L"B",L"X",L"Y",L"Gatilho LT / L2",L"Gatilho RT / R2",L"Analógico esquerdo: cima",L"Analógico esquerdo: direita",L"Analógico esquerdo: baixo",L"Analógico esquerdo: esquerda",L"Analógico direito: cima",L"Analógico direito: direita",L"Analógico direito: baixo",L"Analógico direito: esquerda"};
        static const wchar_t* en[]={L"None",L"D-pad: up",L"D-pad: right",L"D-pad: down",L"D-pad: left",L"Start / Menu",L"Back / View",L"Left stick click",L"Right stick click",L"LB / L1",L"RB / R1",L"A",L"B",L"X",L"Y",L"LT / L2 trigger",L"RT / R2 trigger",L"Left stick: up",L"Left stick: right",L"Left stick: down",L"Left stick: left",L"Right stick: up",L"Right stick: right",L"Right stick: down",L"Right stick: left"};
        return tr(pt[source],en[source]);
    }
    std::wstring key_name(int key)
    {
        if(!key)return tr(L"Nenhuma",L"None");
        if((key>='A'&&key<='Z')||(key>='0'&&key<='9'))return std::wstring(1,wchar_t(key));
        if(key>=VK_F1&&key<=VK_F24)return L"F"+std::to_wstring(key-VK_F1+1);
        if(key>=VK_NUMPAD0&&key<=VK_NUMPAD9)return L"Numpad "+std::to_wstring(key-VK_NUMPAD0);
        switch(key){
            case VK_UP:return tr(L"Cima ↑",L"Up ↑");case VK_DOWN:return tr(L"Baixo ↓",L"Down ↓");
            case VK_LEFT:return tr(L"Esquerda ←",L"Left ←");case VK_RIGHT:return tr(L"Direita →",L"Right →");
            case VK_SPACE:return tr(L"Espaço",L"Space");case VK_RETURN:return L"Enter";
            case VK_BACK:return L"Backspace";case VK_ESCAPE:return L"Esc";case VK_TAB:return L"Tab";
            case VK_SHIFT:return L"Shift";case VK_CONTROL:return L"Ctrl";case VK_MENU:return L"Alt";
        }
        UINT scan=MapVirtualKeyW(UINT(key),MAPVK_VK_TO_VSC);
        if(key==VK_INSERT||key==VK_DELETE||key==VK_HOME||key==VK_END||key==VK_PRIOR||key==VK_NEXT||key==VK_DIVIDE||key==VK_RCONTROL||key==VK_RMENU)scan|=0x100;
        wchar_t text[128]{};
        if(GetKeyNameTextW(LONG(scan<<16),text,128))return text;
        return L"VK "+std::to_wstring(key);
    }
    struct RawPad{DWORD packet;WORD buttons;BYTE lt,rt;SHORT lx,ly,rx,ry;};
    bool poll_pad(RawPad& state)
    {
        using Fn=DWORD(WINAPI*)(DWORD,RawPad*);
        static Fn get=nullptr;static bool tried=false;static int index=-1;static ULONGLONG next=0;
        if(!tried){
            tried=true;
            for(const auto* dll:{L"xinput1_4.dll",L"xinput1_3.dll",L"xinput9_1_0.dll"}){
                HMODULE module=LoadLibraryW(dll);
                if(module){get=reinterpret_cast<Fn>(GetProcAddress(module,"XInputGetState"));if(get)break;FreeLibrary(module);}
            }
        }
        if(!get)return false;
        if(index>=0){if(get(DWORD(index),&state)==0)return true;index=-1;}
        if(GetTickCount64()<next)return false;
        next=GetTickCount64()+1000;
        for(DWORD i=0;i<4;i++)if(get(i,&state)==0){index=int(i);return true;}
        return false;
    }
    struct Editor
    {
        HWND handle{},list{},keyButton{},padButton{},source{},note{},selected{};
        HFONT ownFont{};
        roj::Settings draft;
        float zoom=1;
        int action=0,capture=0; // 1: keyboard; 2: controller
        bool neutral=false;
        ULONGLONG deadline=0;
        std::wstring lastNote;
        int mp(int v)const{return int(v*zoom+.5f);}
        HWND child(const wchar_t* cls,const wchar_t* text,DWORD style,int x,int y,int w,int h,int id=0)
        {
            HWND control=CreateWindowExW(0,cls,text,WS_CHILD|WS_VISIBLE|style,mp(x),mp(y),mp(w),mp(h),handle,reinterpret_cast<HMENU>(INT_PTR(id)),GetModuleHandleW(nullptr),nullptr);
            SendMessageW(control,WM_SETFONT,WPARAM(ownFont),TRUE);return control;
        }
        void message(const wchar_t* pt,const wchar_t* en)
        {
            const std::wstring text=tr(pt,en);
            if(text!=lastNote){lastNote=text;SetWindowTextW(note,text.c_str());}
        }
        void row(unsigned i)
        {
            auto key=key_name(draft.input.keyboard[i]);
            ListView_SetItemText(list,int(i),1,key.data());
            ListView_SetItemText(list,int(i),2,const_cast<wchar_t*>(source_name(draft.input.gamepad[i])));
        }
        void stop_capture()
        {
            capture=0;KillTimer(handle,2);
            SetWindowTextW(keyButton,tr(L"Alterar tecla…",L"Change key…"));
            SetWindowTextW(padButton,tr(L"Detectar botão…",L"Detect button…"));
        }
        void assigned(bool controller)
        {
            const auto& bindings=controller?draft.input.gamepad:draft.input.keyboard;
            for(unsigned i=0;i<roj::InputActionCount;i++)if(int(i)!=action&&bindings[action]&&bindings[i]==bindings[action]){
                const std::wstring pt=std::wstring(L"Entrada também usada por ")+action_name(i)+L". Ajuste se desejar antes de salvar.";
                const std::wstring en=std::wstring(L"Input also assigned to ")+action_name(i)+L". Adjust if needed before saving.";
                message(pt.c_str(),en.c_str());return;
            }
            message(L"Entrada registrada. Clique em Salvar para guardar.",L"Input assigned. Click Save to keep your changes.");
        }
        void sync()
        {
            const int next=ListView_GetNextItem(list,-1,LVNI_SELECTED);
            if(next<0)return;
            if(next!=action){const bool cancelled=capture!=0;stop_capture();action=next;if(cancelled)message(L"Captura cancelada ao trocar de ação.",L"Capture canceled after selecting another action.");}
            SetWindowTextW(selected,action_name(unsigned(action)));
            SendMessageW(source,CB_SETCURSEL,draft.input.gamepad[action],0);
        }
        void create()
        {
            ownFont=CreateFontW(-mp(16),0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
            child(L"STATIC",tr(L"Teclado e controle",L"Keyboard and controller"),0,20,16,860,28);
            list=child(WC_LISTVIEWW,L"",LVS_REPORT|LVS_SINGLESEL|LVS_SHOWSELALWAYS|WS_TABSTOP|WS_BORDER,20,53,860,434);
            ListView_SetExtendedListViewStyle(list,LVS_EX_FULLROWSELECT|LVS_EX_DOUBLEBUFFER);
            ListView_SetBkColor(list,Panel);ListView_SetTextBkColor(list,Panel);ListView_SetTextColor(list,Text);
            const wchar_t* columns[]={tr(L"Ação no PS2",L"PS2 action"),tr(L"Teclado",L"Keyboard"),tr(L"Controle (XInput)",L"Controller (XInput)")};
            const int widths[]={290,215,330};
            for(int c=0;c<3;c++){LVCOLUMNW column{};column.mask=LVCF_TEXT|LVCF_WIDTH;column.pszText=const_cast<wchar_t*>(columns[c]);column.cx=mp(widths[c]);ListView_InsertColumn(list,c,&column);}
            for(unsigned i=0;i<roj::InputActionCount;i++){LVITEMW item{};item.mask=LVIF_TEXT;item.iItem=int(i);item.pszText=const_cast<wchar_t*>(action_name(i));ListView_InsertItem(list,&item);row(i);}
            selected=child(L"STATIC",L"",0,20,498,860,26);
            keyButton=child(L"BUTTON",tr(L"Alterar tecla…",L"Change key…"),BS_PUSHBUTTON|WS_TABSTOP,20,534,220,32,KeyId);
            source=child(L"COMBOBOX",L"",CBS_DROPDOWNLIST|WS_VSCROLL|WS_TABSTOP,260,534,350,260,SourceId);
            for(unsigned i=0;i<roj::GamepadSourceCount;i++)SendMessageW(source,CB_ADDSTRING,0,LPARAM(source_name(int(i))));
            padButton=child(L"BUTTON",tr(L"Detectar botão…",L"Detect button…"),BS_PUSHBUTTON|WS_TABSTOP,630,534,250,32,DetectId);
            child(L"BUTTON",tr(L"Limpar tecla",L"Clear key"),BS_PUSHBUTTON|WS_TABSTOP,20,575,220,29,ClearId);
            note=child(L"STATIC",L"",0,260,576,620,43);
            child(L"BUTTON",tr(L"Restaurar padrões",L"Restore defaults"),BS_PUSHBUTTON|WS_TABSTOP,20,635,210,34,ResetId);
            child(L"BUTTON",tr(L"Cancelar",L"Cancel"),BS_PUSHBUTTON|WS_TABSTOP,630,635,115,34,IDCANCEL);
            child(L"BUTTON",tr(L"Salvar",L"Save"),BS_DEFPUSHBUTTON|WS_TABSTOP,765,635,115,34,ApplyId);
            ListView_SetItemState(list,0,LVIS_SELECTED|LVIS_FOCUSED,LVIS_SELECTED|LVIS_FOCUSED);sync();
            message(L"Selecione uma ação. As alterações valem ao reiniciar o jogo.",L"Select an action. Changes take effect after restarting the game.");
        }
        void bind_key(int key)
        {
            if(capture!=1||key<1||key>255)return;
            draft.input.keyboard[action]=key;row(unsigned(action));stop_capture();
            assigned(false);
        }
        void detect()
        {
            if(capture!=2)return;
            if(GetTickCount64()>=deadline){stop_capture();message(L"Tempo esgotado. Conecte o controle e tente novamente.",L"Timed out. Connect your controller and try again.");return;}
            if(GetForegroundWindow()!=handle)return;
            RawPad pad{};if(!poll_pad(pad))return;
            const int16_t sticks[]{pad.lx,pad.ly,pad.rx,pad.ry};int detected=0;
            for(int source=1;source<int(roj::GamepadSourceCount);source++)if(roj::gamepad_source_value(source,pad.buttons,pad.lt,pad.rt,sticks)>0){detected=source;break;}
            if(!neutral){if(!detected){neutral=true;message(L"Pressione o botão ou mova o analógico desejado.",L"Press a button or move the stick in the desired direction.");}return;}
            if(detected){draft.input.gamepad[action]=detected;row(unsigned(action));SendMessageW(source,CB_SETCURSEL,detected,0);stop_capture();assigned(true);}
        }
    };
    LRESULT CALLBACK procedure(HWND hwnd,UINT message,WPARAM wparam,LPARAM lparam)
    {
        auto* editor=reinterpret_cast<Editor*>(GetWindowLongPtrW(hwnd,GWLP_USERDATA));
        if(message==WM_NCCREATE){editor=static_cast<Editor*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);SetWindowLongPtrW(hwnd,GWLP_USERDATA,LONG_PTR(editor));editor->handle=hwnd;}
        if(!editor)return DefWindowProcW(hwnd,message,wparam,lparam);
        switch(message){
            case WM_CREATE:editor->create();return 0;
            case WM_TIMER:if(wparam==2)editor->detect();return 0;
            case WM_NOTIFY:if(reinterpret_cast<NMHDR*>(lparam)->hwndFrom==editor->list&&reinterpret_cast<NMHDR*>(lparam)->code==LVN_ITEMCHANGED)editor->sync();return 0;
            case WM_COMMAND:{
                const int id=LOWORD(wparam),code=HIWORD(wparam);
                if(id==SourceId&&code==CBN_SELCHANGE){editor->stop_capture();editor->draft.input.gamepad[editor->action]=selection(editor->source);editor->row(unsigned(editor->action));editor->assigned(true);}
                else if(code==BN_CLICKED){
                    if(id==IDCANCEL){DestroyWindow(hwnd);return 0;}
                    if(id==KeyId){bool cancel=editor->capture==1;editor->stop_capture();if(!cancel){editor->capture=1;SetWindowTextW(editor->keyButton,tr(L"Cancelar captura",L"Cancel capture"));editor->message(L"Pressione uma tecla, ou clique em Cancelar captura.",L"Press a key, or click Cancel capture.");SetFocus(editor->keyButton);}else editor->message(L"Captura cancelada.",L"Capture canceled.");}
                    else if(id==DetectId){bool cancel=editor->capture==2;editor->stop_capture();if(!cancel){editor->capture=2;editor->neutral=false;editor->deadline=GetTickCount64()+15000;SetTimer(hwnd,2,25,nullptr);SetWindowTextW(editor->padButton,tr(L"Cancelar captura",L"Cancel capture"));editor->message(L"Solte os botões e centralize os analógicos.",L"Release all buttons and center both sticks.");}else editor->message(L"Captura cancelada.",L"Capture canceled.");}
                    else if(id==ClearId){editor->stop_capture();editor->draft.input.keyboard[editor->action]=0;editor->row(unsigned(editor->action));editor->message(L"Tecla removida. Clique em Salvar para guardar.",L"Key cleared. Click Save to keep your changes.");}
                    else if(id==ResetId){editor->stop_capture();editor->draft.input=roj::InputBindings{};for(unsigned i=0;i<roj::InputActionCount;i++)editor->row(i);editor->sync();editor->message(L"Padrões restaurados. Clique em Salvar para guardar.",L"Defaults restored. Click Save to keep your changes.");}
                    else if(id==ApplyId){editor->stop_capture();const auto previous=settings.input;settings.input=editor->draft.input;if(save())DestroyWindow(hwnd);else settings.input=previous;}
                }
                return 0;
            }
            case WM_CTLCOLORSTATIC:case WM_CTLCOLORBTN:case WM_CTLCOLORLISTBOX:case WM_CTLCOLOREDIT:{HDC dc=reinterpret_cast<HDC>(wparam);SetTextColor(dc,Text);SetBkColor(dc,Background);return LRESULT(background_brush);}
            case WM_ERASEBKGND:{RECT area{};GetClientRect(hwnd,&area);FillRect(reinterpret_cast<HDC>(wparam),&area,background_brush);return 1;}
            case WM_CLOSE:DestroyWindow(hwnd);return 0;
        }
        return DefWindowProcW(hwnd,message,wparam,lparam);
    }
    void open()
    {
        read_controls();Editor editor;editor.draft=settings;
        RECT work{};SystemParametersInfoW(SPI_GETWORKAREA,0,&work,0);
        editor.zoom=std::min({scale,float(work.right-work.left-30)/920.0f,float(work.bottom-work.top-50)/690.0f});
        editor.zoom=std::max(.5f,editor.zoom);
        WNDCLASSW type{};type.lpfnWndProc=procedure;type.hInstance=GetModuleHandleW(nullptr);type.hCursor=LoadCursorW(nullptr,IDC_ARROW);type.hbrBackground=background_brush;type.lpszClassName=L"RingOfJudgmentInputMapping";
        RegisterClassW(&type);
        RECT bounds{0,0,editor.mp(900),editor.mp(690)};const DWORD style=WS_POPUP|WS_CAPTION|WS_SYSMENU;
        AdjustWindowRectEx(&bounds,style,FALSE,WS_EX_DLGMODALFRAME);
        HWND dialog=CreateWindowExW(WS_EX_DLGMODALFRAME,type.lpszClassName,tr(L"Ring of Judgment — Controles",L"Ring of Judgment — Controls"),style,
            work.left+(work.right-work.left-(bounds.right-bounds.left))/2,work.top+(work.bottom-work.top-(bounds.bottom-bounds.top))/2,
            bounds.right-bounds.left,bounds.bottom-bounds.top,window,nullptr,type.hInstance,&editor);
        if(!dialog)return;
        EnableWindow(window,FALSE);ShowWindow(dialog,SW_SHOW);SetFocus(editor.list);
        MSG message{};bool quit=false;
        while(IsWindow(dialog)){
            const int result=GetMessageW(&message,nullptr,0,0);
            if(result<=0){quit=result==0;break;}
            if(editor.capture==1&&(message.message==WM_KEYDOWN||message.message==WM_SYSKEYDOWN)&&
               (message.hwnd==dialog||IsChild(dialog,message.hwnd))){if(!(message.lParam&(LPARAM(1)<<30)))editor.bind_key(int(message.wParam));continue;}
            if(!IsDialogMessageW(dialog,&message)){TranslateMessage(&message);DispatchMessageW(&message);}
        }
        if(IsWindow(dialog))DestroyWindow(dialog);
        if(editor.ownFont)DeleteObject(editor.ownFont);
        EnableWindow(window,TRUE);SetActiveWindow(window);
        if(quit)PostQuitMessage(int(message.wParam));
    }
}
